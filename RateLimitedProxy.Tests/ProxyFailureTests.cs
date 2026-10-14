using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using RateLimitedProxy.Api.Services;

namespace RateLimitedProxy.Tests;

// Milestone 3, the two failure paths that are NOT the upstream answering:
//
//   Upstream unreachable (nothing listening) -> 502 Bad Gateway, fast, counted as an error
//   Client gives up before the proxy answers -> nothing counted at all (the nginx 499 idea)
[Collection(TestProxy.TimingSensitive)]
public class ProxyFailureTests : IClassFixture<FakeUpstream>
{
    private const int TimeoutMs = 500;

    private readonly FakeUpstream _upstream;

    public ProxyFailureTests(FakeUpstream upstream) => _upstream = upstream;

    private static WebApplicationFactory<Program> ProxyFor(string baseUrl) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Upstream:BaseUrl", baseUrl);
            b.UseSetting("Upstream:TimeoutMs", TimeoutMs.ToString());
            TestProxy.AllowTestKey(b);
        });

    // A local port that was free a moment ago and has nobody listening on it now,
    // so a connect attempt is refused immediately instead of hanging.
    private static string DeadUpstreamUrl()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return $"http://127.0.0.1:{port}";
    }

    // ---- 1. Upstream unreachable ----------------------------------------------

    [Fact]
    public async Task Unreachable_upstream_gives_a_fast_502()
    {
        using var proxy = ProxyFor(DeadUpstreamUrl());
        using var client = TestProxy.CreateKeyedClient(proxy);

        var clock = Stopwatch.StartNew();
        var response = await client.GetAsync("/proxy/anything");
        clock.Stop();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.True(clock.ElapsedMilliseconds < TimeoutMs,
            $"a refused connection should fail fast, but the 502 took {clock.ElapsedMilliseconds} ms " +
            $"(timeout is {TimeoutMs} ms) - did it wait for the timeout instead?");
    }

    [Fact]
    public async Task Unreachable_upstream_is_counted_as_an_error()
    {
        using var proxy = ProxyFor(DeadUpstreamUrl());
        using var client = TestProxy.CreateKeyedClient(proxy);
        var metrics = proxy.Services.GetRequiredService<Metrics>();

        await client.GetAsync("/proxy/anything");

        Assert.Equal(1, metrics.TotalRequests);
        Assert.Equal(1, metrics.TotalErrors);
    }

    [Fact]
    public async Task Proxy_keeps_working_after_an_unreachable_upstream()
    {
        // The failure must be handled per request, not poison the app.
        using var proxy = ProxyFor(DeadUpstreamUrl());
        using var client = TestProxy.CreateKeyedClient(proxy);

        var first = await client.GetAsync("/proxy/a");
        var second = await client.GetAsync("/proxy/b");

        Assert.Equal(HttpStatusCode.BadGateway, first.StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway, second.StatusCode);
    }

    // ---- 2. Upstream stalls after the headers --------------------------------

    [Fact]
    public async Task Upstream_that_stalls_after_headers_is_cut_off_by_the_timeout()
    {
        // Deliberately says nothing about WHICH status the client gets - that is a design
        // decision still open. It only demands the proxy doesn't wait forever.
        using var proxy = ProxyFor(_upstream.BaseUrl);
        using var client = TestProxy.CreateKeyedClient(proxy);
        var clientPatience = TimeSpan.FromMilliseconds(TimeoutMs + 3_000);

        var clock = Stopwatch.StartNew();
        using var giveUp = new CancellationTokenSource(clientPatience);
        try
        {
            using var response = await client.GetAsync("/proxy/stall-body", giveUp.Token);
            await response.Content.ReadAsStringAsync(giveUp.Token);
        }
        catch (OperationCanceledException) when (giveUp.IsCancellationRequested)
        {
            Assert.Fail($"proxy was still waiting on the upstream body after {clock.ElapsedMilliseconds} ms " +
                        $"with a {TimeoutMs} ms upstream timeout");
        }
        catch (Exception) { /* proxy cut it off some other way - acceptable for this test */ }
    }

    // ---- 3. Client aborts -----------------------------------------------------

    [Fact]
    public async Task Client_abort_before_the_timeout_is_not_counted()
    {
        using var proxy = ProxyFor(_upstream.BaseUrl);
        using var client = TestProxy.CreateKeyedClient(proxy);
        var metrics = proxy.Services.GetRequiredService<Metrics>();

        // The client gives up well before the proxy's own upstream timeout would fire.
        using var giveUp = new CancellationTokenSource(TimeSpan.FromMilliseconds(TimeoutMs / 5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetAsync("/proxy/hang", giveUp.Token));

        // Wait past the point where the upstream timeout WOULD have fired. If the proxy
        // missed the abort, it would record a 504 somewhere in this window.
        await Task.Delay(TimeoutMs * 3);

        Assert.Equal(0, metrics.TotalRequests);
        Assert.Equal(0, metrics.TotalErrors);
    }

    [Fact]
    public async Task Client_abort_does_not_break_the_next_request()
    {
        using var proxy = ProxyFor(_upstream.BaseUrl);
        using var client = TestProxy.CreateKeyedClient(proxy);
        var metrics = proxy.Services.GetRequiredService<Metrics>();

        using var giveUp = new CancellationTokenSource(TimeSpan.FromMilliseconds(TimeoutMs / 5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetAsync("/proxy/hang", giveUp.Token));

        var next = await client.GetAsync("/proxy/status/200");

        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        await Task.Delay(TimeoutMs * 3);
        Assert.Equal(1, metrics.TotalRequests); // only the 200, never the aborted one
        Assert.Equal(0, metrics.TotalErrors);
    }
}
