using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using RateLimitedProxy.Api.Services;

namespace RateLimitedProxy.Tests;

// Milestone 5: a token bucket per API key.
//
// Contract agreed on 2026-10-14:
//   X-Api-Key              - the key header (name case-insensitive, value case-sensitive ordinal)
//   Missing / unknown key  - 401 + "WWW-Authenticate: ApiKey", upstream never called,
//                            counted in TotalUnauthorized only
//   RateLimit:ApiKeys      - the configured key list; one bucket per key, created at startup
//   RateLimit:Capacity     - burst size and the most a bucket can hold   (default 10)
//   RateLimit:RefillPerSecond - tokens added per second, fractions kept  (default 1.0)
//   Bucket empty           - 429 + Retry-After = seconds until the next whole token, rounded UP;
//                            upstream never called; counted in TotalRateLimited only
//   Order                  - key check -> rate check -> concurrency slot
//   Token = attempt        - a request that passes the rate check but then gets a 503 still
//                            spent its token
//   X-Api-Key              - never forwarded upstream
//   Time                   - the limiter reads time from the TimeProvider in DI; these tests
//                            swap in a FakeTimeProvider so time only moves when a test moves it
//   ValidateOnStart        - rejects Capacity < 1, RefillPerSecond <= 0, no keys, blank keys,
//                            duplicate keys
//
// NOTE: the "slot" tests below still rely on Upstream:TimeoutMs running on REAL time
// (CancelAfter). If you ever move the timeout onto the injected TimeProvider, those tests
// will hang on the frozen fake clock - tell me and I'll change them.
[Collection(TestProxy.TimingSensitive)]
public class RateLimitTests : IAsyncLifetime
{
    private const int TimeoutMs = 500;

    private readonly FakeUpstream _upstream = new();
    private readonly FakeTimeProvider _time = new();

    public Task InitializeAsync() => _upstream.InitializeAsync();

    public Task DisposeAsync() => _upstream.DisposeAsync();

    private WebApplicationFactory<Program> Proxy(
        string[] keys,
        double? capacity = null,
        double? refillPerSecond = null,
        int maxConcurrency = 20
    ) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment(TestProxy.Environment);
            b.UseSetting("Upstream:BaseUrl", _upstream.BaseUrl);
            b.UseSetting("Upstream:TimeoutMs", TimeoutMs.ToString());
            b.UseSetting("Upstream:MaxConcurrency", maxConcurrency.ToString());
            for (var i = 0; i < keys.Length; i++)
                b.UseSetting($"RateLimit:ApiKeys:{i}", keys[i]);
            if (capacity is not null)
                b.UseSetting(
                    "RateLimit:Capacity",
                    capacity.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                );
            if (refillPerSecond is not null)
                b.UseSetting(
                    "RateLimit:RefillPerSecond",
                    refillPerSecond.Value.ToString(
                        System.Globalization.CultureInfo.InvariantCulture
                    )
                );
            b.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(_time));
        });

    private static Task<HttpResponseMessage> Send(
        HttpClient client,
        string? key,
        string path = "/proxy/status/200",
        string headerName = "X-Api-Key",
        CancellationToken ct = default
    )
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (key is not null)
            request.Headers.TryAddWithoutValidation(headerName, key);
        return client.SendAsync(request, ct);
    }

    private static async Task<HttpStatusCode> StatusOf(
        HttpClient client,
        string? key,
        string path = "/proxy/status/200"
    )
    {
        using var response = await Send(client, key, path);
        return response.StatusCode;
    }

    // Read through reflection so this file compiles before the new counters exist.
    private static long Counter(Metrics metrics, string name)
    {
        var property = typeof(Metrics).GetProperty(name);
        Assert.True(property is not null, $"Metrics has no public {name} property yet");
        return Convert.ToInt64(property!.GetValue(metrics));
    }

    private void Advance(double seconds) => _time.Advance(TimeSpan.FromSeconds(seconds));

    // ---- 1. Who gets in: the key check ----------------------------------------

    [Fact]
    public async Task A_configured_key_is_forwarded()
    {
        using var proxy = Proxy(["alice"]);
        using var client = proxy.CreateClient();

        Assert.Equal(HttpStatusCode.OK, await StatusOf(client, "alice"));
        Assert.Equal(1, _upstream.Hits);
    }

    [Theory]
    [InlineData(null)] // no header at all
    [InlineData("")] // header present, empty
    [InlineData("mallory")] // not in the list
    [InlineData("ALICE")] // value is case-sensitive: not the same key as "alice"
    public async Task Missing_or_unknown_key_gets_401_with_WWW_Authenticate_ApiKey(string? key)
    {
        using var proxy = Proxy(["alice"]);
        using var client = proxy.CreateClient();

        using var response = await Send(client, key);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var challenge = Assert.Single(response.Headers.WwwAuthenticate);
        Assert.Equal("ApiKey", challenge.Scheme);
        Assert.Equal(0, _upstream.Hits);
    }

    [Fact]
    public async Task The_header_name_is_case_insensitive()
    {
        using var proxy = Proxy(["alice"]);
        using var client = proxy.CreateClient();

        using var lower = await Send(client, "alice", headerName: "x-api-key");
        using var upper = await Send(client, "alice", headerName: "X-API-KEY");

        Assert.Equal(HttpStatusCode.OK, lower.StatusCode);
        Assert.Equal(HttpStatusCode.OK, upper.StatusCode);
    }

    [Fact]
    public async Task A_401_is_counted_as_unauthorized_only()
    {
        using var proxy = Proxy(["alice"]);
        using var client = proxy.CreateClient();
        var metrics = proxy.Services.GetRequiredService<Metrics>();

        await StatusOf(client, null);
        await StatusOf(client, "mallory");

        Assert.Equal(2, Counter(metrics, "TotalUnauthorized"));
        Assert.Equal(0, metrics.TotalRequests);
        Assert.Equal(0, metrics.TotalErrors);
        Assert.Equal(0, metrics.TotalRejected);
        Assert.Equal(0, Counter(metrics, "TotalRateLimited"));
    }

    [Fact]
    public async Task Rotating_made_up_keys_never_gets_through()
    {
        // Mallory's bypass: a 429 on m1, so try m2, m3, ... Every one must be a 401.
        using var proxy = Proxy(["alice"], capacity: 1);
        using var client = proxy.CreateClient();

        for (var i = 0; i < 50; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, await StatusOf(client, $"m{i}"));

        Assert.Equal(0, _upstream.Hits);
    }

    [Fact]
    public async Task X_Api_Key_is_never_forwarded_upstream_in_any_casing()
    {
        using var proxy = Proxy(["alice"]);
        using var client = proxy.CreateClient();

        foreach (var name in new[] { "X-Api-Key", "x-api-key", "X-API-KEY" })
        {
            using var response = await Send(client, "alice", "/proxy/echo", name);
            var json = await response.Content.ReadAsStringAsync();
            Assert.True(
                response.StatusCode == HttpStatusCode.OK,
                $"expected the echo (200), got {(int)response.StatusCode}: {json}"
            );
            var echo = JsonSerializer.Deserialize<EchoResponse>(json, EchoResponse.Json)!;

            Assert.False(
                echo.Headers.ContainsKey("X-Api-Key"),
                $"sent as '{name}', the upstream received X-Api-Key: {echo.Headers.GetValueOrDefault("X-Api-Key")}"
            );
        }
    }

    // ---- 2. The bucket ---------------------------------------------------------

    [Fact]
    public async Task Defaults_are_a_burst_of_10_then_1_token_per_second()
    {
        using var proxy = Proxy(["alice"]); // Capacity and RefillPerSecond not set
        using var client = proxy.CreateClient();

        for (var i = 1; i <= 10; i++)
            Assert.True(
                await StatusOf(client, "alice") == HttpStatusCode.OK,
                $"request {i} of the first 10 should pass"
            );
        Assert.Equal((HttpStatusCode)429, await StatusOf(client, "alice"));

        Advance(1);
        Assert.Equal(HttpStatusCode.OK, await StatusOf(client, "alice"));
        Assert.Equal((HttpStatusCode)429, await StatusOf(client, "alice"));
    }

    [Fact]
    public async Task An_empty_bucket_gives_429_and_the_upstream_is_never_called()
    {
        using var proxy = Proxy(["alice"], capacity: 3);
        using var client = proxy.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
            statuses.Add(await StatusOf(client, "alice"));

        Assert.Equal(
            [
                HttpStatusCode.OK,
                HttpStatusCode.OK,
                HttpStatusCode.OK,
                (HttpStatusCode)429,
                (HttpStatusCode)429,
            ],
            statuses
        );
        Assert.Equal(3, _upstream.Hits);
    }

    [Fact]
    public async Task Idle_time_never_fills_the_bucket_past_capacity()
    {
        using var proxy = Proxy(["alice"], capacity: 3, refillPerSecond: 1);
        using var client = proxy.CreateClient();

        Advance(3600); // an hour idle would be 3600 tokens with no cap

        var passed = 0;
        for (var i = 0; i < 6; i++)
            if (await StatusOf(client, "alice") == HttpStatusCode.OK)
                passed++;

        Assert.Equal(3, passed);
    }

    [Fact]
    public async Task Refill_keeps_the_fraction()
    {
        // 0.25 tokens/s = one token every 4 s. Empty at t=0.
        // Asking every 1.5 s: 0.375 at t=1.5, 0.75 at t=3.0, 1.125 at t=4.5 -> pass.
        // A refill that throws away the fraction and resets its clock never gets there.
        using var proxy = Proxy(["alice"], capacity: 1, refillPerSecond: 0.25);
        using var client = proxy.CreateClient();

        Assert.Equal(HttpStatusCode.OK, await StatusOf(client, "alice"));

        Advance(1.5);
        Assert.Equal((HttpStatusCode)429, await StatusOf(client, "alice"));
        Advance(1.5);
        Assert.Equal((HttpStatusCode)429, await StatusOf(client, "alice"));
        Advance(1.5);
        Assert.Equal(HttpStatusCode.OK, await StatusOf(client, "alice"));
    }

    [Fact]
    public async Task Each_key_has_its_own_bucket()
    {
        using var proxy = Proxy(["alice", "bob"], capacity: 1);
        using var client = proxy.CreateClient();

        Assert.Equal(HttpStatusCode.OK, await StatusOf(client, "alice"));
        Assert.Equal((HttpStatusCode)429, await StatusOf(client, "alice"));

        Assert.Equal(HttpStatusCode.OK, await StatusOf(client, "bob"));
    }

    [Fact]
    public async Task Keys_differing_only_in_case_are_two_keys_with_two_buckets()
    {
        // Ordinal, case-sensitive: "Abc" and "abc" are not duplicates, so startup is fine.
        using var proxy = Proxy(["Abc", "abc"], capacity: 1);
        using var client = proxy.CreateClient();

        Assert.Equal(HttpStatusCode.OK, await StatusOf(client, "Abc"));
        Assert.Equal(HttpStatusCode.OK, await StatusOf(client, "abc"));
        Assert.Equal((HttpStatusCode)429, await StatusOf(client, "Abc"));
    }

    [Fact]
    public async Task A_429_is_counted_as_rate_limited_only()
    {
        using var proxy = Proxy(["alice"], capacity: 1);
        using var client = proxy.CreateClient();
        var metrics = proxy.Services.GetRequiredService<Metrics>();

        await StatusOf(client, "alice"); // 200
        await StatusOf(client, "alice"); // 429
        await StatusOf(client, "alice"); // 429

        Assert.Equal(2, Counter(metrics, "TotalRateLimited"));
        Assert.Equal(1, metrics.TotalRequests); // only the 200
        Assert.Equal(0, metrics.TotalErrors);
        Assert.Equal(0, metrics.TotalRejected);
        Assert.Equal(0, Counter(metrics, "TotalUnauthorized"));
    }

    // ---- 3. Retry-After --------------------------------------------------------

    private static int RetryAfterSeconds(HttpResponseMessage response)
    {
        Assert.Equal((HttpStatusCode)429, response.StatusCode);
        var delta = response.Headers.RetryAfter?.Delta;
        Assert.True(
            delta is not null,
            $"a 429 needs Retry-After as whole seconds; got '{string.Join(",", response.Headers.TryGetValues("Retry-After", out var v) ? v : [])}'"
        );
        return (int)delta!.Value.TotalSeconds;
    }

    [Fact]
    public async Task Retry_After_is_the_seconds_until_the_next_token_rounded_up()
    {
        // 0.25 tokens/s, capacity 1. Empty at t=0.
        //   t=0.0 -> next token in 4.0 s -> "4"   (ceiling of a whole number is itself, not 5)
        //   t=2.5 -> next token in 1.5 s -> "2"   (rounding down would say 1 - too early)
        using var proxy = Proxy(["alice"], capacity: 1, refillPerSecond: 0.25);
        using var client = proxy.CreateClient();

        Assert.Equal(HttpStatusCode.OK, await StatusOf(client, "alice"));

        using (var atZero = await Send(client, "alice"))
            Assert.Equal(4, RetryAfterSeconds(atZero));

        Advance(2.5);
        using (var atTwoAndAHalf = await Send(client, "alice"))
            Assert.Equal(2, RetryAfterSeconds(atTwoAndAHalf));
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.25)]
    [InlineData(2.0)]
    public async Task Obeying_Retry_After_exactly_succeeds_and_one_second_less_does_not(
        double refillPerSecond
    )
    {
        // A client that waits exactly Retry-After must get in. Waiting one second less must not -
        // otherwise the header is padded and makes clients wait longer than they need to.
        using var proxy = Proxy(["alice"], capacity: 1, refillPerSecond: refillPerSecond);
        using var client = proxy.CreateClient();

        Assert.Equal(HttpStatusCode.OK, await StatusOf(client, "alice"));

        var limitedSeen = 0;
        foreach (var wait in new[] { 0.0, 0.5, 1.0, 1.5, 0.25 })
        {
            Advance(wait);
            using var limited = await Send(client, "alice");
            if (limited.StatusCode == HttpStatusCode.OK)
                continue; // that wait alone refilled it
            limitedSeen++;

            var retryAfter = RetryAfterSeconds(limited);
            Assert.True(retryAfter >= 1, $"Retry-After was {retryAfter}; on a 429 it can't be 0");

            Advance(retryAfter - 1);
            Assert.True(
                await StatusOf(client, "alice") == (HttpStatusCode)429,
                $"rate {refillPerSecond}/s: Retry-After said {retryAfter} s but the token was there after {retryAfter - 1} s"
            );

            Advance(1);
            Assert.True(
                await StatusOf(client, "alice") == HttpStatusCode.OK,
                $"rate {refillPerSecond}/s: waited the full Retry-After ({retryAfter} s) and still got a 429"
            );
        }

        // Without this, a proxy with no rate limiting at all passes: every request is a 200.
        Assert.True(
            limitedSeen > 0,
            "no request was ever rate limited, so nothing about Retry-After was checked"
        );
    }

    // ---- 4. Concurrency: refill-check-take is atomic ---------------------------

    [Fact]
    public async Task Concurrent_requests_on_one_key_never_spend_more_than_capacity()
    {
        // Time is frozen, so each bucket holds exactly 5 tokens. 40 requests at once on the
        // same key: exactly 5 may get past the limiter. Check-then-act without a lock lets
        // extra ones through. 20 keys = 20 independent attempts at the race in one proxy.
        //
        // "Got past the limiter" = anything but a 429. On a cold proxy one of the 5 can still
        // time out upstream (504) - that's not the limiter's business, so it isn't asserted.
        const int capacity = 5,
            burst = 40,
            rounds = 20;
        var keys = Enumerable.Range(0, rounds).Select(i => $"k{i}").Append("warmup").ToArray();
        using var proxy = Proxy(keys, capacity: capacity, maxConcurrency: burst);
        using var client = proxy.CreateClient();

        await StatusOf(client, "warmup"); // JIT + first upstream connection, outside the race
        var hitsBefore = _upstream.Hits;

        foreach (var key in keys.Where(k => k != "warmup"))
        {
            var statuses = await Task.WhenAll(
                Enumerable.Range(0, burst).Select(_ => StatusOf(client, key))
            );

            var limited = statuses.Count(s => s == (HttpStatusCode)429);
            Assert.True(
                limited == burst - capacity,
                $"key {key}: {burst - limited} of {burst} got past the limiter - expected exactly {capacity} "
                    + $"(statuses: {string.Join(",", statuses.GroupBy(s => (int)s).Select(g => $"{g.Key}x{g.Count()}"))})"
            );
        }

        Assert.True(
            _upstream.Hits - hitsBefore <= capacity * rounds,
            $"the upstream saw {_upstream.Hits - hitsBefore} requests; the buckets only held {capacity * rounds} tokens"
        );
    }

    // ---- 5. Order: rate check before the concurrency slot ---------------------

    private async Task<Task<HttpStatusCode>> HoldTheOnlySlot(HttpClient client, string key)
    {
        var hitsBefore = _upstream.Hits;
        var holder = StatusOf(client, key, "/proxy/hang");
        await WaitUntil(() => _upstream.Hits == hitsBefore + 1);
        return holder;
    }

    [Fact]
    public async Task An_over_limit_client_gets_its_429_without_queueing_for_a_slot()
    {
        using var proxy = Proxy(["holder", "mallory"], capacity: 1, maxConcurrency: 1);
        using var client = proxy.CreateClient();

        Assert.Equal(HttpStatusCode.OK, await StatusOf(client, "mallory")); // her only token
        var holder = await HoldTheOnlySlot(client, "holder");

        var clock = Stopwatch.StartNew();
        var status = await StatusOf(client, "mallory");
        clock.Stop();

        Assert.Equal((HttpStatusCode)429, status);
        Assert.True(
            clock.ElapsedMilliseconds < TimeoutMs / 2,
            $"the 429 took {clock.ElapsedMilliseconds} ms with the slot busy - was it queued for the slot first?"
        );

        Assert.Equal(HttpStatusCode.GatewayTimeout, await holder);
    }

    [Fact]
    public async Task A_request_that_gets_a_503_still_spent_its_token()
    {
        // Token = attempt: bob passes the rate check, waits for the only slot, gets 503.
        // With time frozen, his next request must be a 429 - the token is gone.
        using var proxy = Proxy(["holder", "bob"], capacity: 1, maxConcurrency: 1);
        using var client = proxy.CreateClient();

        var holder = await HoldTheOnlySlot(client, "holder");

        // Bob's deadline starts a few ms after the holder's, so he can grab the slot in the gap
        // when the holder times out. /hang makes that end in a 504 instead of a 200 - either way
        // he passed the rate check and the upstream never answered him.
        var bob = await StatusOf(client, "bob", "/proxy/hang");
        Assert.True(
            bob is HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout,
            $"bob should end as 503 (no slot) or 504 (slot in the last ms, then timed out), got {(int)bob}"
        );
        Assert.Equal(HttpStatusCode.GatewayTimeout, await holder);

        Assert.Equal((HttpStatusCode)429, await StatusOf(client, "bob"));
    }

    // ---- 6. Startup validation -------------------------------------------------

    public static TheoryData<string, string[], string?, string?> BadConfigs =>
        new()
        {
            { "capacity 0", ["alice"], "0", null },
            { "capacity negative", ["alice"], "-1", null },
            { "capacity below 1", ["alice"], "0.5", null },
            { "refill 0", ["alice"], null, "0" },
            { "refill negative", ["alice"], null, "-0.5" },
            { "no keys", [], null, null },
            { "blank key", ["alice", "   "], null, null },
            { "duplicate key", ["alice", "alice"], null, null },
        };

    [Theory]
    [MemberData(nameof(BadConfigs))]
    public void Bad_rate_limit_config_stops_the_proxy_from_starting(
        string why,
        string[] keys,
        string? capacity,
        string? refill
    )
    {
        // When startup throws, WebApplicationFactory sometimes reports ObjectDisposedException
        // instead of the real exception (a race inside the factory under parallel test load).
        // That hides WHY startup failed, so retry with a fresh factory until the real one shows.
        Exception? thrown = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var proxy = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseEnvironment(TestProxy.Environment);
                b.UseSetting("Upstream:BaseUrl", _upstream.BaseUrl);
                for (var i = 0; i < keys.Length; i++)
                    b.UseSetting($"RateLimit:ApiKeys:{i}", keys[i]);
                if (capacity is not null)
                    b.UseSetting("RateLimit:Capacity", capacity);
                if (refill is not null)
                    b.UseSetting("RateLimit:RefillPerSecond", refill);
            });

            thrown = null;
            try
            {
                proxy.CreateClient().Dispose();
            }
            catch (Exception e)
            {
                thrown = e;
            }

            if (thrown is not ObjectDisposedException)
                break;
        }

        Assert.True(thrown is not null, $"{why}: the proxy started anyway");
        // An int Capacity can't even bind "0.5" - failing at binding is just as good as failing validation.
        var bindingFailure =
            why == "capacity below 1" && Flatten(thrown!).Any(e => e is InvalidOperationException);
        Assert.True(
            bindingFailure || Flatten(thrown!).Any(e => e is OptionsValidationException),
            $"{why}: startup failed, but not from options validation: {thrown!.GetType().Name}: {thrown.Message}"
        );
    }

    private static IEnumerable<Exception> Flatten(Exception e)
    {
        yield return e;
        if (e is AggregateException agg)
            foreach (var inner in agg.InnerExceptions.SelectMany(Flatten))
                yield return inner;
        else if (e.InnerException is not null)
            foreach (var inner in Flatten(e.InnerException))
                yield return inner;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("condition never became true");
            await Task.Delay(10);
        }
    }
}
