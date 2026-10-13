using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using RateLimitedProxy.Api.Services;

namespace RateLimitedProxy.Tests;

// Milestone 4: at most Upstream:MaxConcurrency requests are inside the upstream at once.
//
// Contract agreed on 2026-10-13:
//   Upstream:MaxConcurrency - the cap
//   Cap full                - wait for a slot, but only until the request's TimeoutMs deadline
//   TimeoutMs               - ONE budget for the whole request, slot wait included
//   No slot by the deadline - 503, upstream never called
//   Metrics                 - a 503 from the cap is NOT an error and NOT in TotalRequests;
//                             it goes in its own counter, TotalRejected
//
// Each test gets its own FakeUpstream, so Hits and PeakInFlight belong to that test alone.
public class ConcurrencyCapTests : IAsyncLifetime
{
    private const int TimeoutMs = 500;

    private readonly FakeUpstream _upstream = new();

    public Task InitializeAsync() => _upstream.InitializeAsync();
    public Task DisposeAsync() => _upstream.DisposeAsync();

    private WebApplicationFactory<Program> ProxyWithCap(int cap) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Upstream:BaseUrl", _upstream.BaseUrl);
            b.UseSetting("Upstream:TimeoutMs", TimeoutMs.ToString());
            b.UseSetting("Upstream:MaxConcurrency", cap.ToString());
        });

    // Read through reflection so the other test files still compile before TotalRejected exists.
    private static long TotalRejected(Metrics metrics)
    {
        var property = typeof(Metrics).GetProperty("TotalRejected");
        Assert.True(property is not null, "Metrics has no public TotalRejected property yet");
        return Convert.ToInt64(property!.GetValue(metrics));
    }

    private static async Task<HttpStatusCode[]> FireAtOnce(HttpClient client, int count, string path)
    {
        var responses = await Task.WhenAll(Enumerable.Range(0, count).Select(_ => client.GetAsync(path)));
        return responses.Select(r => r.StatusCode).ToArray();
    }

    // Fire enough requests at a hanging upstream that most of them can never get a slot
    // before their deadline. With cap 1: the first holds the slot for the whole budget,
    // the rest wait, and at most one of them grabs the slot with a few ms left.
    private static Task<HttpStatusCode[]> Overload(HttpClient client) => FireAtOnce(client, 6, "/proxy/hang");

    // ---- 1. The cap holds -----------------------------------------------------

    [Fact]
    public async Task Upstream_never_sees_more_than_the_cap()
    {
        using var proxy = ProxyWithCap(2);
        using var client = proxy.CreateClient();

        // 10 requests, 50 ms each, 2 at a time = ~250 ms, well inside the 500 ms budget.
        var statuses = await FireAtOnce(client, 10, "/proxy/slow/50");

        Assert.All(statuses, s => Assert.Equal(HttpStatusCode.OK, s));
        Assert.True(_upstream.PeakInFlight <= 2,
            $"cap is 2 but the upstream saw {_upstream.PeakInFlight} requests at once");
    }

    [Fact]
    public async Task The_cap_is_actually_used_not_just_one_at_a_time()
    {
        // Guards against a "fix" that serialises everything (cap of 1 no matter the setting).
        using var proxy = ProxyWithCap(3);
        using var client = proxy.CreateClient();

        await FireAtOnce(client, 9, "/proxy/slow/100");

        Assert.Equal(3, _upstream.PeakInFlight);
    }

    // ---- 2. Cap full -> 503 at the deadline -----------------------------------

    [Fact]
    public async Task Requests_that_never_get_a_slot_get_503_and_never_reach_the_upstream()
    {
        using var proxy = ProxyWithCap(1);
        using var client = proxy.CreateClient();

        var statuses = await Overload(client);

        Assert.All(statuses, s => Assert.True(
            s is HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout,
            $"every request should end as 503 (no slot) or 504 (slot, then timed out), got {(int)s}"));
        Assert.Contains(HttpStatusCode.ServiceUnavailable, statuses);

        // Only requests that got a slot may reach the upstream - and every one of those 504s.
        // (No peak check here: a timed-out /hang can linger upstream a moment after the proxy
        // let go of it, so the peak would race. The leak tests below measure it cleanly.)
        // A waiter can get the slot with ~0 ms of budget left and 504 before it even connects,
        // so a 504 doesn't guarantee a hit - but a hit always means a 504, never a 503.
        var timedOut = statuses.Count(s => s == HttpStatusCode.GatewayTimeout);
        Assert.True(_upstream.Hits >= 1 && _upstream.Hits <= timedOut,
            $"upstream was hit {_upstream.Hits} times but only {timedOut} requests got a slot (504) - " +
            "a rejected request reached the upstream");
    }

    [Fact]
    public async Task A_cap_503_is_counted_as_rejected_not_as_an_error_or_a_request()
    {
        using var proxy = ProxyWithCap(1);
        using var client = proxy.CreateClient();
        var metrics = proxy.Services.GetRequiredService<Metrics>();

        var statuses = await Overload(client);

        var rejected = statuses.Count(s => s == HttpStatusCode.ServiceUnavailable);
        var timedOut = statuses.Count(s => s == HttpStatusCode.GatewayTimeout);
        Assert.True(rejected > 0, "setup failed: nothing was rejected");

        Assert.Equal(rejected, TotalRejected(metrics));
        Assert.Equal(timedOut, metrics.TotalRequests); // only requests that got a slot
        Assert.Equal(timedOut, metrics.TotalErrors);   // the 504s; the 503s are not errors
    }

    [Fact]
    public async Task The_503_arrives_at_the_deadline_not_before()
    {
        // "Bounded wait until the TimeoutMs deadline" - rejecting instantly would be a
        // different design (fail-fast), not the one agreed.
        using var proxy = ProxyWithCap(1);
        using var client = proxy.CreateClient();

        var holder = client.GetAsync("/proxy/hang");
        await WaitUntil(() => _upstream.Hits == 1);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        // /hang, so a waiter that does get the slot holds it to its own deadline
        // instead of handing it straight on to the next waiter.
        var waiters = await FireAtOnce(client, 4, "/proxy/hang");
        clock.Stop();
        await holder;

        Assert.Contains(HttpStatusCode.ServiceUnavailable, waiters);
        Assert.True(clock.ElapsedMilliseconds >= TimeoutMs * 0.8,
            $"the 503s came back after {clock.ElapsedMilliseconds} ms - they didn't wait for the {TimeoutMs} ms deadline");
    }

    // ---- 3. No leaked slots (the over-release bug) ----------------------------

    [Fact]
    public async Task Slot_wait_timeouts_do_not_raise_the_cap()
    {
        // The WaitAsync-returned-false door: if a rejected request still calls Release(),
        // every 503 above permanently adds one slot.
        using var proxy = ProxyWithCap(1);
        using var client = proxy.CreateClient();

        var first = await Overload(client);
        Assert.Contains(HttpStatusCode.ServiceUnavailable, first);

        await WaitUntil(() => _upstream.InFlightIsZero);
        _upstream.ResetPeak();

        var after = await FireAtOnce(client, 5, "/proxy/slow/50");

        Assert.All(after, s => Assert.Equal(HttpStatusCode.OK, s));
        Assert.True(_upstream.PeakInFlight <= 1,
            $"cap is 1 but after the 503s the upstream saw {_upstream.PeakInFlight} at once - " +
            "rejected requests are releasing slots they never acquired");
    }

    [Fact]
    public async Task Client_aborts_while_waiting_do_not_raise_the_cap()
    {
        // The exception door: a client gives up while queued for a slot.
        using var proxy = ProxyWithCap(1);
        using var client = proxy.CreateClient();

        var holder = client.GetAsync("/proxy/hang");
        await WaitUntil(() => _upstream.Hits == 1);

        var aborters = Enumerable.Range(0, 4).Select(async _ =>
        {
            using var giveUp = new CancellationTokenSource(TimeSpan.FromMilliseconds(TimeoutMs / 5));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => client.GetAsync("/proxy/status/200", giveUp.Token));
        });
        await Task.WhenAll(aborters);
        await holder;

        await WaitUntil(() => _upstream.InFlightIsZero);
        _upstream.ResetPeak();

        var after = await FireAtOnce(client, 5, "/proxy/slow/50");

        Assert.All(after, s => Assert.Equal(HttpStatusCode.OK, s));
        Assert.True(_upstream.PeakInFlight <= 1,
            $"cap is 1 but after the aborts the upstream saw {_upstream.PeakInFlight} at once - " +
            "aborted waiters are releasing slots they never acquired");
        Assert.Equal(1, _upstream.Hits - 5); // the aborted waiters never reached the upstream
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("condition never became true");
            await Task.Delay(10);
        }
    }
}
