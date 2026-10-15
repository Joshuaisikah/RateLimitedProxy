using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using RateLimitedProxy.Api.Services;

namespace RateLimitedProxy.Tests;

// Milestone 6: a circuit breaker in front of the upstream.
//
// Contract agreed on 2026-10-15 (his answers):
//   States        - closed / open / half-open; transitions are LAZY (checked when a request
//                   arrives, read from the TimeProvider in DI - no timers)
//   Closed        - normal pipeline. FailureThreshold CONSECUTIVE failures -> open
//   Success       - resets the consecutive-failure count to 0
//   Failure       - upstream answered any 5xx; proxy 504 (upstream timeout, including a
//                   request that waited for a slot and then ran out of time); proxy 502
//                   (upstream unreachable)
//   Not counted   - client abort, 401, 429, cap-503: they neither count NOR reset
//   Open          - rejected immediately, upstream never called
//   Open -> half  - the first request that arrives >= BreakDuration after opened-at
//   Half-open     - exactly ONE trial request goes upstream; everyone else is rejected like open
//   Trial success - closed (count back to 0)
//   Trial failure - open again, opened-at = NOW (the cooldown restarts)
//   Scope         - one breaker for the whole proxy, not per key
//
//   Order         - key check -> rate check -> breaker -> concurrency slot. So an unknown key
//                   gets 401 even while open, and a circuit-rejected request has spent its token
//   Never reports - a trial that ends without an upstream verdict (client abort, cap-503)
//                   releases its claim, so the next request becomes the trial
//   Late results  - a non-trial result arriving while open or half-open is ignored; only the
//                   trial's own report moves the circuit out of half-open
//   Retry-After   - open: seconds until half-open, rounded up; half-open: 1 (the trial is quick)
//   Rejection     - 503 + header "X-Proxy-Reason: circuit-open"; the cap-503 does NOT carry it
//   Metrics       - counted in Metrics.TotalCircuitOpen only, not TotalRequests/Errors/Rejected
//   Config        - "CircuitBreaker": FailureThreshold (default 5), BreakDurationSeconds
//                   (default 30); ValidateOnStart rejects FailureThreshold < 1, duration <= 0
//   Upstream 4xx  - a SUCCESS for the breaker (upstream is alive and answering)
//
// Upstream:TimeoutMs still runs on REAL time; only the breaker's cooldown uses the fake clock.
[Collection(TestProxy.TimingSensitive)]
public class CircuitBreakerTests : IAsyncLifetime
{
    private const int TimeoutMs = 500;
    private const string OtherKey = "other-key";

    private readonly FakeUpstream _upstream = new();
    private readonly FakeTimeProvider _time = new();

    public Task InitializeAsync() => _upstream.InitializeAsync();

    public Task DisposeAsync() => _upstream.DisposeAsync();

    // Leaving threshold/breakSeconds null means "use the app's defaults" (5 and 30).
    private WebApplicationFactory<Program> Proxy(
        string? baseUrl = null,
        string? threshold = null,
        string? breakSeconds = null,
        int maxConcurrency = 20,
        int capacity = 1_000_000
    ) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment(TestProxy.Environment);
            b.UseSetting("Upstream:BaseUrl", baseUrl ?? _upstream.BaseUrl);
            b.UseSetting("Upstream:TimeoutMs", TimeoutMs.ToString());
            b.UseSetting("Upstream:MaxConcurrency", maxConcurrency.ToString());
            b.UseSetting("RateLimit:ApiKeys:0", TestProxy.Key);
            b.UseSetting("RateLimit:ApiKeys:1", OtherKey);
            b.UseSetting("RateLimit:Capacity", capacity.ToString());
            b.UseSetting("RateLimit:RefillPerSecond", "1000000");
            if (threshold is not null)
                b.UseSetting("CircuitBreaker:FailureThreshold", threshold);
            if (breakSeconds is not null)
                b.UseSetting("CircuitBreaker:BreakDurationSeconds", breakSeconds);
            b.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(_time));
        });

    private static async Task<HttpResponseMessage> Send(
        HttpClient client,
        string path,
        string? key = TestProxy.Key,
        CancellationToken ct = default
    )
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (key is not null)
            request.Headers.TryAddWithoutValidation("X-Api-Key", key);
        return await client.SendAsync(request, ct);
    }

    private static async Task<HttpStatusCode> Status(HttpClient client, string path, string? key = TestProxy.Key)
    {
        using var response = await Send(client, path, key);
        return response.StatusCode;
    }

    private static bool IsCircuitRejection(HttpResponseMessage r) =>
        r.StatusCode == HttpStatusCode.ServiceUnavailable
        && r.Headers.TryGetValues("X-Proxy-Reason", out var values)
        && values.Contains("circuit-open");

    private static void AssertCircuitRejected(HttpResponseMessage r, string because)
    {
        var reason = r.Headers.TryGetValues("X-Proxy-Reason", out var v) ? string.Join(",", v) : "(none)";
        Assert.True(IsCircuitRejection(r),
            $"{because}: expected 503 + X-Proxy-Reason: circuit-open, got {(int)r.StatusCode} with X-Proxy-Reason {reason}");
    }

    private static void AssertWentThrough(HttpResponseMessage r, HttpStatusCode expected, string because)
    {
        Assert.False(IsCircuitRejection(r), $"{because}: the circuit rejected it");
        Assert.Equal(expected, r.StatusCode);
    }

    // Sends n upstream 500s one after another and checks each one really reached the upstream.
    private async Task Fail(HttpClient client, int n, string? key = TestProxy.Key)
    {
        for (var i = 0; i < n; i++)
        {
            var before = _upstream.Hits;
            using var r = await Send(client, "/proxy/status/500", key);
            AssertWentThrough(r, HttpStatusCode.InternalServerError, $"failure #{i + 1} of {n}");
            Assert.Equal(before + 1, _upstream.Hits);
        }
    }

    private Task Open(HttpClient client) => Fail(client, 5);

    private void Advance(double seconds) => _time.Advance(TimeSpan.FromSeconds(seconds));

    private static long Counter(Metrics metrics, string name)
    {
        var property = typeof(Metrics).GetProperty(name);
        Assert.True(property is not null, $"Metrics has no public {name} property yet");
        return Convert.ToInt64(property!.GetValue(metrics));
    }

    private static string DeadUpstreamUrl()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return $"http://127.0.0.1:{port}/";
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"never happened: {what}");
            await Task.Delay(10);
        }
    }

    // ---- 1. Opening: 5 consecutive failures ------------------------------------

    [Fact]
    public async Task Five_consecutive_failures_open_it_and_the_sixth_never_reaches_upstream()
    {
        using var proxy = Proxy();
        using var client = proxy.CreateClient();

        await Open(client);
        var hits = _upstream.Hits;

        using var sixth = await Send(client, "/proxy/status/200");

        AssertCircuitRejected(sixth, "6th request after 5 failures");
        Assert.Equal(hits, _upstream.Hits);
    }

    [Fact]
    public async Task Four_failures_do_not_open_it()
    {
        using var proxy = Proxy();
        using var client = proxy.CreateClient();

        await Fail(client, 4);
        using var next = await Send(client, "/proxy/status/200");

        AssertWentThrough(next, HttpStatusCode.OK, "only 4 failures so far");
    }

    [Fact]
    public async Task A_success_resets_the_count_FFFFSF_stays_closed()
    {
        using var proxy = Proxy();
        using var client = proxy.CreateClient();

        await Fail(client, 4);
        using (var s = await Send(client, "/proxy/status/200"))
            AssertWentThrough(s, HttpStatusCode.OK, "the S in FFFFSF");
        await Fail(client, 1); // count is now 1, not 5

        using var next = await Send(client, "/proxy/status/200");
        AssertWentThrough(next, HttpStatusCode.OK, "FFFFSF is 1 consecutive failure, not 5");
    }

    [Fact]
    public async Task After_a_reset_it_takes_five_fresh_failures_to_open()
    {
        using var proxy = Proxy();
        using var client = proxy.CreateClient();

        await Fail(client, 4);
        using (var s = await Send(client, "/proxy/status/200"))
            AssertWentThrough(s, HttpStatusCode.OK, "reset");
        await Fail(client, 5); // Fail() itself asserts all 5 went through

        using var next = await Send(client, "/proxy/status/200");
        AssertCircuitRejected(next, "5 consecutive failures after the reset");
    }

    // ---- 2. What counts as a failure -------------------------------------------

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task Any_5xx_answered_by_the_upstream_counts(int code)
    {
        using var proxy = Proxy();
        using var client = proxy.CreateClient();

        for (var i = 0; i < 5; i++)
            Assert.Equal((HttpStatusCode)code, await Status(client, $"/proxy/status/{code}"));
        var hits = _upstream.Hits;

        using var next = await Send(client, "/proxy/status/200");
        AssertCircuitRejected(next, $"5 upstream {code}s");
        Assert.Equal(hits, _upstream.Hits);
    }

    [Fact]
    public async Task Upstream_timeouts_504_count()
    {
        using var proxy = Proxy();
        using var client = proxy.CreateClient();

        var statuses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Status(client, "/proxy/hang")));
        Assert.All(statuses, s => Assert.Equal(HttpStatusCode.GatewayTimeout, s));
        var hits = _upstream.Hits;

        using var next = await Send(client, "/proxy/status/200");
        AssertCircuitRejected(next, "5 upstream timeouts");
        Assert.Equal(hits, _upstream.Hits);
    }

    [Fact]
    public async Task Unreachable_upstream_502_counts()
    {
        using var proxy = Proxy(baseUrl: DeadUpstreamUrl());
        using var client = proxy.CreateClient();

        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.BadGateway, await Status(client, "/proxy/anything"));

        using var next = await Send(client, "/proxy/anything");
        AssertCircuitRejected(next, "5 unreachable-upstream 502s");
    }

    [Fact]
    public async Task An_upstream_4xx_is_a_success_and_resets_the_count()
    {
        // ASSUMPTION: upstream answered = upstream alive. Veto if you disagree.
        using var proxy = Proxy();
        using var client = proxy.CreateClient();

        await Fail(client, 4);
        Assert.Equal(HttpStatusCode.NotFound, await Status(client, "/proxy/status/404"));
        await Fail(client, 1);

        using var next = await Send(client, "/proxy/status/200");
        AssertWentThrough(next, HttpStatusCode.OK, "the 404 should have reset the count");
    }

    // ---- 3. What does NOT count (and does not reset either) --------------------
    // Pattern for each: 4 failures, then the ignored outcome, then:
    //   one more 500 must still REACH upstream   -> the ignored outcome didn't count as a failure
    //   and the request after it must be REJECTED -> the ignored outcome didn't reset the count

    [Fact]
    public async Task Client_abort_neither_counts_nor_resets()
    {
        using var proxy = Proxy();
        using var client = proxy.CreateClient();

        await Fail(client, 4);
        using (var giveUp = new CancellationTokenSource(TimeSpan.FromMilliseconds(TimeoutMs / 5)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => Send(client, "/proxy/hang", ct: giveUp.Token));
        await Task.Delay(TimeoutMs * 3); // past where a missed abort would have become a 504

        await Fail(client, 1); // asserts it reached upstream: the abort was not failure #5
        using var next = await Send(client, "/proxy/status/200");
        AssertCircuitRejected(next, "the abort must not have reset the count");
    }

    [Fact]
    public async Task Unauthorized_401_neither_counts_nor_resets()
    {
        using var proxy = Proxy();
        using var client = proxy.CreateClient();

        await Fail(client, 4);
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, await Status(client, "/proxy/status/200", key: null));

        await Fail(client, 1);
        using var next = await Send(client, "/proxy/status/200");
        AssertCircuitRejected(next, "the 401s must not have reset the count");
    }

    [Fact]
    public async Task Rate_limited_429_neither_counts_nor_resets()
    {
        // Every key gets a bucket of 4. test-key spends all 4 on failures, then gets 429s.
        // other-key still has tokens to send the 5th failure.
        using var proxy = Proxy(capacity: 4);
        using var client = proxy.CreateClient();

        await Fail(client, 4);
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.TooManyRequests, await Status(client, "/proxy/status/200"));

        await Fail(client, 1, key: OtherKey);
        using var next = await Send(client, "/proxy/status/200", OtherKey);
        AssertCircuitRejected(next, "the 429s must not have reset the count");
    }

    [Fact]
    public async Task Cap_503_does_not_count()
    {
        // Cap 1, six /hang at once: one or two get the slot and 504 (those DO count),
        // the rest never get a slot and 503 (those must NOT count).
        using var proxy = Proxy(maxConcurrency: 1);
        using var client = proxy.CreateClient();

        var statuses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Status(client, "/proxy/hang")));
        var timedOut = statuses.Count(s => s == HttpStatusCode.GatewayTimeout);
        var capRejected = statuses.Count(s => s == HttpStatusCode.ServiceUnavailable);
        Assert.True(capRejected >= 2 && timedOut is >= 1 and <= 3,
            $"setup failed: wanted 1-3 timeouts and some cap-503s, got {timedOut} x 504, {capRejected} x 503");

        // Failures so far = timedOut. If the cap-503s counted, we'd be at 6 and already open.
        await Fail(client, 5 - timedOut);
        using var next = await Send(client, "/proxy/status/200");
        AssertCircuitRejected(next, $"{timedOut} timeouts + {5 - timedOut} upstream 500s = 5 failures");
    }

    // ---- 4. The open state -----------------------------------------------------

    [Fact]
    public async Task Open_rejects_fast_and_concurrently_without_calling_upstream()
    {
        using var proxy = Proxy();
        using var client = proxy.CreateClient();
        await Open(client);
        var hits = _upstream.Hits;

        var clock = Stopwatch.StartNew();
        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Send(client, "/proxy/slow/300")));
        clock.Stop();

        Assert.All(responses, r => AssertCircuitRejected(r, "open circuit"));
        Assert.Equal(hits, _upstream.Hits);
        Assert.True(clock.ElapsedMilliseconds < 300,
            $"20 rejections took {clock.ElapsedMilliseconds} ms - an open circuit should not wait on anything");
        foreach (var r in responses) r.Dispose();
    }

    [Fact]
    public async Task A_cap_503_does_not_look_like_a_circuit_rejection()
    {
        using var proxy = Proxy(maxConcurrency: 1, threshold: "1000000");
        using var client = proxy.CreateClient();

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Send(client, "/proxy/hang")));
        var capRejected = responses.Where(r => r.StatusCode == HttpStatusCode.ServiceUnavailable).ToList();

        Assert.NotEmpty(capRejected);
        Assert.All(capRejected, r => Assert.False(IsCircuitRejection(r),
            "a cap-503 carries X-Proxy-Reason: circuit-open - clients can't tell 'proxy full' from 'upstream dead'"));
        foreach (var r in responses) r.Dispose();
    }

    [Fact]
    public async Task A_circuit_rejection_is_counted_only_in_TotalCircuitOpen()
    {
        using var proxy = Proxy();
        using var client = proxy.CreateClient();
        var metrics = proxy.Services.GetRequiredService<Metrics>();

        await Open(client);
        for (var i = 0; i < 3; i++)
            using (var r = await Send(client, "/proxy/status/200"))
                AssertCircuitRejected(r, "open");

        Assert.Equal(3, Counter(metrics, "TotalCircuitOpen"));
        Assert.Equal(5, metrics.TotalRequests); // only the 5 that reached upstream
        Assert.Equal(5, metrics.TotalErrors);
        Assert.Equal(0, metrics.TotalRejected); // that's the cap-503 counter
    }

    [Fact]
    public async Task Still_open_one_millisecond_before_30s()
    {
        using var proxy = Proxy();
        using var client = proxy.CreateClient();
        await Open(client);
        var hits = _upstream.Hits;

        Advance(30 - 0.001);
        using var r = await Send(client, "/proxy/status/200");

        AssertCircuitRejected(r, "29.999 s after opening");
        Assert.Equal(hits, _upstream.Hits);
    }

    [Fact]
    public async Task Requests_while_open_do_not_extend_the_cooldown()
    {
        // Rejections are not failures: hammering an open circuit must not push opened-at forward.
        using var proxy = Proxy();
        using var client = proxy.CreateClient();
        await Open(client);

        for (var t = 0; t < 29; t++)
        {
            Advance(1);
            using var r = await Send(client, "/proxy/status/200");
            AssertCircuitRejected(r, $"{t + 1} s after opening");
        }
        Advance(1); // 30 s after the ORIGINAL opening

        using var trial = await Send(client, "/proxy/status/200");
        AssertWentThrough(trial, HttpStatusCode.OK, "30 s after the original opening");
    }

    // ---- 5. Half-open ------------------------------------------------------------

    [Fact]
    public async Task At_30s_a_trial_goes_through_and_a_success_closes_it()
    {
        using var proxy = Proxy();
        using var client = proxy.CreateClient();
        await Open(client);
        var hits = _upstream.Hits;

        Advance(30);
        using (var trial = await Send(client, "/proxy/status/200"))
            AssertWentThrough(trial, HttpStatusCode.OK, "trial at exactly 30 s");
        Assert.Equal(hits + 1, _upstream.Hits);

        // Closed now: concurrent requests all go through, not just one.
        var after = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Send(client, "/proxy/slow/100")));
        Assert.All(after, r => AssertWentThrough(r, HttpStatusCode.OK, "closed after a successful trial"));
        foreach (var r in after) r.Dispose();
    }

    [Fact]
    public async Task Closing_after_a_trial_starts_the_count_from_zero()
    {
        using var proxy = Proxy();
        using var client = proxy.CreateClient();
        await Open(client);
        Advance(30);
        Assert.Equal(HttpStatusCode.OK, await Status(client, "/proxy/status/200"));

        await Fail(client, 4); // 4 fresh failures - must NOT open
        using var next = await Send(client, "/proxy/status/200");
        AssertWentThrough(next, HttpStatusCode.OK, "only 4 failures since it closed");
    }

    [Fact]
    public async Task Exactly_one_trial_when_15_arrive_at_once()
    {
        using var proxy = Proxy();
        using var client = proxy.CreateClient();
        await Open(client);
        var hits = _upstream.Hits;

        Advance(30);
        // /slow/300 keeps the trial in flight while the other 14 arrive.
        var responses = await Task.WhenAll(Enumerable.Range(0, 15).Select(_ => Send(client, "/proxy/slow/300")));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(14, responses.Count(IsCircuitRejection));
        Assert.Equal(hits + 1, _upstream.Hits);
        foreach (var r in responses) r.Dispose();
    }

    [Fact]
    public async Task A_failed_trial_reopens_and_restarts_the_cooldown_from_the_trial()
    {
        using var proxy = Proxy();
        using var client = proxy.CreateClient();
        await Open(client); // opened at t = 0

        Advance(30); // t = 30
        await Fail(client, 1); // the trial fails -> open again, opened-at = 30
        using (var r = await Send(client, "/proxy/status/200"))
            AssertCircuitRejected(r, "right after a failed trial");

        Advance(30 - 0.001); // t = 59.999: 30 s since t=0 long gone, but not since the trial
        using (var r = await Send(client, "/proxy/status/200"))
            AssertCircuitRejected(r, "29.999 s after the failed trial");

        Advance(0.001); // t = 60
        using var trial = await Send(client, "/proxy/status/200");
        AssertWentThrough(trial, HttpStatusCode.OK, "30 s after the failed trial");
    }

    [Fact]
    public async Task While_a_trial_hangs_everyone_else_is_rejected_and_its_504_reopens()
    {
        using var proxy = Proxy();
        using var client = proxy.CreateClient();
        await Open(client);
        var hits = _upstream.Hits;

        Advance(30);
        var trial = Status(client, "/proxy/hang");
        await WaitUntil(() => _upstream.Hits == hits + 1, "the trial reached the upstream");

        using (var during = await Send(client, "/proxy/status/200"))
            AssertCircuitRejected(during, "a trial is already in flight");
        Assert.Equal(hits + 1, _upstream.Hits);

        Assert.Equal(HttpStatusCode.GatewayTimeout, await trial);
        using (var after = await Send(client, "/proxy/status/200"))
            AssertCircuitRejected(after, "the trial timed out, so it should be open again");

        Advance(30);
        using var nextTrial = await Send(client, "/proxy/status/200");
        AssertWentThrough(nextTrial, HttpStatusCode.OK, "30 s after the timed-out trial");
    }

    // ---- 6. Scope ---------------------------------------------------------------

    [Fact]
    public async Task One_breaker_for_the_whole_proxy_not_per_key()
    {
        using var proxy = Proxy();
        using var client = proxy.CreateClient();

        await Fail(client, 3, key: TestProxy.Key);
        await Fail(client, 2, key: OtherKey);

        using var r = await Send(client, "/proxy/status/200", TestProxy.Key);
        AssertCircuitRejected(r, "5 failures across two keys still means the upstream is failing");
    }

    // ---- 7. Config ----------------------------------------------------------------

    [Fact]
    public async Task Threshold_and_duration_come_from_config()
    {
        using var proxy = Proxy(threshold: "2", breakSeconds: "5");
        using var client = proxy.CreateClient();

        await Fail(client, 2);
        using (var r = await Send(client, "/proxy/status/200"))
            AssertCircuitRejected(r, "threshold 2");

        Advance(5);
        using var trial = await Send(client, "/proxy/status/200");
        AssertWentThrough(trial, HttpStatusCode.OK, "BreakDurationSeconds 5");
    }

    [Theory]
    [InlineData("threshold 0", "0", null)]
    [InlineData("threshold negative", "-1", null)]
    [InlineData("duration 0", null, "0")]
    [InlineData("duration negative", null, "-5")]
    public void Bad_breaker_config_stops_the_proxy_from_starting(string why, string? threshold, string? seconds)
    {
        var thrown = TestProxy.StartupFailure(() => Proxy(threshold: threshold, breakSeconds: seconds));

        Assert.True(thrown is not null, $"{why}: the proxy started anyway");
        Assert.True(TestProxy.Flatten(thrown!).Any(e => e is OptionsValidationException),
            $"{why}: startup failed, but not from options validation: {thrown!.GetType().Name}: {thrown.Message}");
    }

    // ---- 8. Order, stuck trials, late results, Retry-After ---------------------------

    [Fact]
    public async Task An_unknown_key_gets_401_even_while_open()
    {
        using var proxy = Proxy();
        using var client = proxy.CreateClient();
        await Open(client);

        Assert.Equal(HttpStatusCode.Unauthorized, await Status(client, "/proxy/status/200", key: null));
        Assert.Equal(HttpStatusCode.Unauthorized, await Status(client, "/proxy/status/200", key: "nope"));
    }

    [Fact]
    public async Task A_circuit_rejected_request_has_spent_its_token()
    {
        // Bucket of 3, threshold 2: two failures spend 2 tokens and open the circuit.
        // The 3rd request is rejected by the circuit AFTER taking the last token,
        // so the 4th finds the bucket empty: 429, not another circuit 503.
        using var proxy = Proxy(threshold: "2", capacity: 3);
        using var client = proxy.CreateClient();

        await Fail(client, 2);
        using (var third = await Send(client, "/proxy/status/200"))
            AssertCircuitRejected(third, "circuit open, 1 token left");

        Assert.Equal(HttpStatusCode.TooManyRequests, await Status(client, "/proxy/status/200"));
    }

    [Fact]
    public async Task A_trial_whose_client_aborts_releases_the_claim()
    {
        using var proxy = Proxy();
        using var client = proxy.CreateClient();
        await Open(client);
        Advance(30);

        using (var giveUp = new CancellationTokenSource(TimeSpan.FromMilliseconds(TimeoutMs / 5)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => Send(client, "/proxy/hang", ct: giveUp.Token));
        // Past where a missed abort would have become a 504 (which would reopen it).
        await Task.Delay(TimeoutMs * 3);

        var hits = _upstream.Hits;
        using var next = await Send(client, "/proxy/status/200");
        AssertWentThrough(next, HttpStatusCode.OK,
            "the aborted trial never reported - if it kept its claim, the breaker is wedged forever");
        Assert.Equal(hits + 1, _upstream.Hits);

        // And that request was a real trial: its success closed the circuit.
        var after = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Send(client, "/proxy/slow/100")));
        Assert.All(after, r => AssertWentThrough(r, HttpStatusCode.OK, "closed after the replacement trial"));
        foreach (var r in after) r.Dispose();
    }

    [Fact]
    public async Task A_late_success_while_open_is_ignored()
    {
        // S is admitted while closed and is still in flight when 5 fast failures open the circuit.
        using var proxy = Proxy();
        using var client = proxy.CreateClient();

        var hits = _upstream.Hits;
        var late = Status(client, "/proxy/slow/300");
        await WaitUntil(() => _upstream.Hits == hits + 1, "the slow request reached the upstream");
        await Open(client);

        Assert.Equal(HttpStatusCode.OK, await late);

        using var next = await Send(client, "/proxy/status/200");
        AssertCircuitRejected(next, "a success from before the circuit opened must not close it");
    }

    [Fact]
    public async Task Late_failures_while_open_do_not_restart_the_cooldown()
    {
        // Threshold 2, so it takes only 2 late failures to look like "another 2 in a row".
        // Two /hang requests are admitted while closed, then 2 fast failures open the circuit.
        // The two /hang 504s arrive while open; they must not reopen it (= move opened-at).
        using var proxy = Proxy(threshold: "2");
        using var client = proxy.CreateClient();

        var hits = _upstream.Hits;
        var late = Task.WhenAll(Status(client, "/proxy/hang"), Status(client, "/proxy/hang"));
        await WaitUntil(() => _upstream.Hits == hits + 2, "both hanging requests reached the upstream");
        await Fail(client, 2); // opened at fake t = 0

        Advance(15); // t = 15
        Assert.All(await late, s => Assert.Equal(HttpStatusCode.GatewayTimeout, s));

        Advance(15); // t = 30 since the real opening
        using var trial = await Send(client, "/proxy/status/200");
        AssertWentThrough(trial, HttpStatusCode.OK,
            "the late 504s must not have reopened it at t = 15");
    }

    [Fact]
    public async Task Open_rejection_says_when_to_retry_rounded_up()
    {
        using var proxy = Proxy();
        using var client = proxy.CreateClient();
        await Open(client);

        Advance(10.5);
        using (var r = await Send(client, "/proxy/status/200"))
        {
            AssertCircuitRejected(r, "open");
            Assert.Equal(TimeSpan.FromSeconds(20), r.Headers.RetryAfter?.Delta); // 19.5 s left -> 20
        }

        Advance(19.499); // 0.001 s left
        using (var r = await Send(client, "/proxy/status/200"))
        {
            AssertCircuitRejected(r, "open, 1 ms left");
            Assert.Equal(TimeSpan.FromSeconds(1), r.Headers.RetryAfter?.Delta); // never 0
        }
    }

    [Fact]
    public async Task Half_open_rejection_says_retry_after_1()
    {
        using var proxy = Proxy();
        using var client = proxy.CreateClient();
        await Open(client);
        Advance(30);

        var trial = Send(client, "/proxy/slow/300");
        await WaitUntil(() => _upstream.Hits == 6, "the trial reached the upstream");

        using (var r = await Send(client, "/proxy/status/200"))
        {
            AssertCircuitRejected(r, "half-open, trial in flight");
            Assert.Equal(TimeSpan.FromSeconds(1), r.Headers.RetryAfter?.Delta);
        }
        (await trial).Dispose();
    }
}
