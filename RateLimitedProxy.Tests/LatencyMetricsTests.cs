using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using RateLimitedProxy.Api.Services;

namespace RateLimitedProxy.Tests;

// Milestone 7: latency percentiles + a metrics endpoint.
//
// Contract agreed on 2026-10-15 (his answers):
//   GET /metrics      - outside /proxy, never forwarded, no X-Api-Key, spends no token,
//                       never counts itself
//   Latency           - arrival at the proxy -> response decided, read from the injected
//                       TimeProvider (GetTimestamp / GetElapsedTime)
//   Sampled           - every ADMITTED request (passed key, rate and breaker checks) except
//                       client aborts: upstream answers of any status, 504s (measured, so about
//                       TimeoutMs), 502s, cap-503s
//   Not sampled       - 401, 429, circuit-503, client aborts
//   Storage           - fixed buckets since startup, upper edges (ms, inclusive <=):
//                       1, 2, 5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, + overflow
//   Percentile        - nearest-rank: rank = ceil(p * N); walk cumulative counts; report the
//                       bucket's UPPER EDGE
//   Read              - snapshot the buckets first, N = sum of the snapshot
//   No samples        - p50Ms / p95Ms = null
//   Rank in overflow  - that percentile = null; overflowCount says how many samples overflowed
//   errorRate         - totalErrors / totalRequests; null when totalRequests = 0
//   JSON fields       - totalRequests, totalErrors, errorRate, totalRejected, totalRateLimited,
//                       totalUnauthorized, totalCircuitOpen, sampleCount, p50Ms, p95Ms,
//                       overflowCount   (camelCase, the ASP.NET Core default)
//
// How exact latencies are produced: the proxy and FakeUpstream share one FakeTimeProvider.
// /proxy/advance/{ms} makes the upstream move that clock forward by exactly ms while the
// request is in flight, so the proxy measures exactly ms. Those tests send requests ONE AT A
// TIME - a concurrent request would see the clock jump too.
// Tests about 504 / cap-503 / 502 use the real clock instead.
[Collection(TestProxy.TimingSensitive)]
public class LatencyMetricsTests : IAsyncLifetime
{
    private const int TimeoutMs = 500;
    private static readonly int[] Edges = [1, 2, 5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000];

    private readonly FakeUpstream _upstream = new();
    private readonly FakeTimeProvider _time = new();

    public Task InitializeAsync() => _upstream.InitializeAsync();

    public Task DisposeAsync() => _upstream.DisposeAsync();

    private WebApplicationFactory<Program> Proxy(
        bool fakeClock = true,
        string? baseUrl = null,
        int maxConcurrency = 20,
        int? capacity = null,
        string? breakerThreshold = null
    ) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            TestProxy.AllowTestKey(b);
            b.UseSetting("Upstream:BaseUrl", baseUrl ?? _upstream.BaseUrl);
            b.UseSetting("Upstream:TimeoutMs", TimeoutMs.ToString());
            b.UseSetting("Upstream:MaxConcurrency", maxConcurrency.ToString());
            if (capacity is not null)
                b.UseSetting("RateLimit:Capacity", capacity.Value.ToString());
            if (breakerThreshold is not null)
                b.UseSetting("CircuitBreaker:FailureThreshold", breakerThreshold);
            if (fakeClock)
            {
                _upstream.Clock = _time;
                b.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(_time));
            }
        });

    // ---- helpers ------------------------------------------------------------------

    private sealed record Snapshot(JsonElement Root)
    {
        private JsonElement Field(string name)
        {
            Assert.True(Root.TryGetProperty(name, out var v), $"/metrics has no \"{name}\" field: {Root}");
            return v;
        }

        public long Long(string name) => Field(name).GetInt64();

        public long? NullableLong(string name)
        {
            var v = Field(name);
            return v.ValueKind == JsonValueKind.Null ? null : v.GetInt64();
        }

        public double? NullableDouble(string name)
        {
            var v = Field(name);
            return v.ValueKind == JsonValueKind.Null ? null : v.GetDouble();
        }

        public long SampleCount => Long("sampleCount");
        public long OverflowCount => Long("overflowCount");
        public long? P50 => NullableLong("p50Ms");
        public long? P95 => NullableLong("p95Ms");
        public double? ErrorRate => NullableDouble("errorRate");
    }

    private static async Task<Snapshot> Read(HttpClient client)
    {
        using var response = await client.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        return new Snapshot(JsonDocument.Parse(json).RootElement.Clone());
    }

    private static async Task Latencies(HttpClient client, IEnumerable<int> msEach)
    {
        foreach (var ms in msEach)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/proxy/advance/{ms}")).StatusCode);
    }

    private static async Task<HttpStatusCode[]> FireAtOnce(HttpClient client, int count, string path)
    {
        var responses = await Task.WhenAll(Enumerable.Range(0, count).Select(_ => client.GetAsync(path)));
        return responses.Select(r => r.StatusCode).ToArray();
    }

    private static string DeadUpstreamUrl()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return $"http://127.0.0.1:{port}/";
    }

    // ---- 1. The endpoint ------------------------------------------------------------

    [Fact]
    public async Task Metrics_needs_no_key_and_returns_json_with_every_field()
    {
        using var proxy = Proxy();
        using var client = proxy.CreateClient(); // no X-Api-Key

        using var response = await client.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var snap = new Snapshot(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone());
        foreach (var f in new[] { "totalRequests", "totalErrors", "totalRejected", "totalRateLimited",
                                  "totalUnauthorized", "totalCircuitOpen", "sampleCount", "overflowCount" })
            snap.Long(f);
        _ = snap.ErrorRate;
        _ = snap.P50;
        _ = snap.P95;
    }

    [Fact]
    public async Task A_fresh_proxy_reports_null_percentiles_and_null_error_rate()
    {
        using var proxy = Proxy();
        using var client = proxy.CreateClient();

        var snap = await Read(client);

        Assert.Equal(0, snap.SampleCount);
        Assert.Equal(0, snap.OverflowCount);
        Assert.Null(snap.P50); // NOT the first bucket edge: rank ceil(0.5 * 0) = 0 must not "find" bucket 1
        Assert.Null(snap.P95);
        Assert.Null(snap.ErrorRate); // 0 / 0
        Assert.Equal(0, snap.Long("totalRequests"));
    }

    [Fact]
    public async Task Metrics_never_counts_itself_and_is_never_forwarded()
    {
        using var proxy = Proxy();
        using var client = proxy.CreateClient();

        for (var i = 0; i < 5; i++)
            await Read(client);
        var snap = await Read(client);

        Assert.Equal(0, snap.SampleCount);
        Assert.Equal(0, snap.Long("totalRequests"));
        Assert.Equal(0, snap.Long("totalUnauthorized")); // no key, but it isn't a proxy request
        Assert.Equal(0, _upstream.Hits);
    }

    [Fact]
    public async Task Metrics_spends_no_token()
    {
        using var proxy = Proxy(capacity: 1);
        using var keyed = TestProxy.CreateKeyedClient(proxy);

        for (var i = 0; i < 5; i++)
            await Read(keyed); // even WITH a key, /metrics is not rate limited

        Assert.Equal(HttpStatusCode.OK, (await keyed.GetAsync("/proxy/status/200")).StatusCode);
    }

    // ---- 2. Known distributions (exact, on the fake clock) ----------------------------

    [Fact]
    public async Task Ten_samples_10_to_100_give_p50_50_and_p95_100()
    {
        using var proxy = Proxy();
        using var client = TestProxy.CreateKeyedClient(proxy);

        await Latencies(client, Enumerable.Range(1, 10).Select(i => i * 10));
        var snap = await Read(client);

        Assert.Equal(10, snap.SampleCount);
        Assert.Equal(50, snap.P50);  // rank 5  -> 50 ms  -> bucket <= 50
        Assert.Equal(100, snap.P95); // rank 10 -> 100 ms -> bucket <= 100
    }

    [Fact]
    public async Task Twenty_samples_5_to_100_give_p50_50_and_p95_100()
    {
        using var proxy = Proxy();
        using var client = TestProxy.CreateKeyedClient(proxy);

        await Latencies(client, Enumerable.Range(1, 20).Select(i => i * 5));
        var snap = await Read(client);

        Assert.Equal(20, snap.SampleCount);
        Assert.Equal(50, snap.P50);  // rank 10 -> 50 ms -> bucket <= 50
        Assert.Equal(100, snap.P95); // rank 19 -> 95 ms -> bucket <= 100 (upper edge, never under-reports)
    }

    [Theory]
    [InlineData(95, 5, 1)]   // rank 95 is the LAST 1 ms sample
    [InlineData(94, 6, 500)] // rank 95 is the FIRST 300 ms sample -> bucket <= 500
    public async Task P95_rank_is_the_ceiling_exactly(int fast, int slow, int expectedP95)
    {
        using var proxy = Proxy();
        using var client = TestProxy.CreateKeyedClient(proxy);

        await Latencies(client, Enumerable.Repeat(1, fast).Concat(Enumerable.Repeat(300, slow)));
        var snap = await Read(client);

        Assert.Equal(100, snap.SampleCount);
        Assert.Equal(1, snap.P50);
        Assert.Equal(expectedP95, snap.P95);
    }

    public static TheoryData<int, int?> EdgeCases()
    {
        var data = new TheoryData<int, int?> { { 0, 1 } }; // 0 ms goes in the first bucket
        for (var i = 0; i < Edges.Length; i++)
        {
            data.Add(Edges[i], Edges[i]); // exactly on an edge -> that bucket (<=)
            data.Add(Edges[i] + 1, i + 1 < Edges.Length ? Edges[i + 1] : null); // 1 ms over -> next, or overflow
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(EdgeCases))]
    public async Task Bucket_edges_are_inclusive(int ms, int? expected)
    {
        using var proxy = Proxy();
        using var client = TestProxy.CreateKeyedClient(proxy);

        await Latencies(client, [ms]);
        var snap = await Read(client);

        Assert.Equal(1, snap.SampleCount);
        Assert.Equal(expected, snap.P50);
        Assert.Equal(expected, snap.P95);
        Assert.Equal(expected is null ? 1 : 0, snap.OverflowCount);
    }

    [Fact]
    public async Task All_samples_overflowing_gives_null_percentiles_but_counts_them()
    {
        using var proxy = Proxy();
        using var client = TestProxy.CreateKeyedClient(proxy);

        await Latencies(client, Enumerable.Repeat(6000, 10));
        var snap = await Read(client);

        Assert.Equal(10, snap.SampleCount); // sampleCount > 0 is what says "not no-data"
        Assert.Equal(10, snap.OverflowCount);
        Assert.Null(snap.P50);
        Assert.Null(snap.P95);
    }

    [Fact]
    public async Task Overflow_beyond_rank_95_nulls_p95_only()
    {
        using var proxy = Proxy();
        using var client = TestProxy.CreateKeyedClient(proxy);

        await Latencies(client, Enumerable.Repeat(1, 90).Concat(Enumerable.Repeat(6000, 10)));
        var snap = await Read(client);

        Assert.Equal(100, snap.SampleCount);
        Assert.Equal(10, snap.OverflowCount);
        Assert.Equal(1, snap.P50);
        Assert.Null(snap.P95);
    }

    [Fact]
    public async Task Order_of_arrival_does_not_matter()
    {
        using var proxy = Proxy();
        using var client = TestProxy.CreateKeyedClient(proxy);

        // Same 20 values as Twenty_samples_5_to_100, shuffled.
        var values = Enumerable.Range(1, 20).Select(i => i * 5).OrderBy(v => (v * 7919) % 20).ToArray();
        await Latencies(client, values);
        var snap = await Read(client);

        Assert.Equal(50, snap.P50);
        Assert.Equal(100, snap.P95);
    }

    // ---- 3. Which requests are sampled --------------------------------------------------

    [Fact]
    public async Task Upstream_answers_of_any_status_are_sampled()
    {
        using var proxy = Proxy();
        using var client = TestProxy.CreateKeyedClient(proxy);

        await client.GetAsync("/proxy/status/200");
        await client.GetAsync("/proxy/status/404");
        await client.GetAsync("/proxy/status/500");
        var snap = await Read(client);

        Assert.Equal(3, snap.SampleCount);
    }

    [Fact]
    public async Task Unauthorized_rate_limited_and_circuit_rejections_are_not_sampled()
    {
        using var proxy = Proxy(capacity: 3, breakerThreshold: "1");
        using var keyed = TestProxy.CreateKeyedClient(proxy);
        using var anonymous = proxy.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/proxy/status/200")).StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, (await keyed.GetAsync("/proxy/status/500")).StatusCode); // sampled; opens circuit
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await keyed.GetAsync("/proxy/status/200")).StatusCode);  // circuit
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await keyed.GetAsync("/proxy/status/200")).StatusCode);  // circuit, last token
        Assert.Equal(HttpStatusCode.TooManyRequests, (await keyed.GetAsync("/proxy/status/200")).StatusCode);
        var snap = await Read(keyed);

        Assert.Equal(1, snap.SampleCount); // only the 500
        Assert.Equal(1, snap.Long("totalUnauthorized"));
        Assert.Equal(2, snap.Long("totalCircuitOpen"));
        Assert.Equal(1, snap.Long("totalRateLimited"));
    }

    [Fact]
    public async Task A_504_is_sampled_at_about_the_timeout()
    {
        using var proxy = Proxy(fakeClock: false);
        using var client = TestProxy.CreateKeyedClient(proxy);

        Assert.Equal(HttpStatusCode.GatewayTimeout, (await client.GetAsync("/proxy/hang")).StatusCode);
        var snap = await Read(client);

        Assert.Equal(1, snap.SampleCount);
        Assert.Equal(1000, snap.P50); // ~500 ms + overhead -> bucket (500, 1000]
    }

    [Fact]
    public async Task Cap_503s_are_sampled_too()
    {
        using var proxy = Proxy(fakeClock: false, maxConcurrency: 1);
        using var client = TestProxy.CreateKeyedClient(proxy);

        var statuses = await FireAtOnce(client, 6, "/proxy/hang");
        var capRejected = statuses.Count(s => s == HttpStatusCode.ServiceUnavailable);
        Assert.True(capRejected > 0, "setup failed: nothing was cap-rejected");
        var snap = await Read(client);

        Assert.Equal(6, snap.SampleCount); // every one was admitted: 504s AND cap-503s
        Assert.Equal(statuses.Count(s => s == HttpStatusCode.GatewayTimeout), snap.Long("totalRequests"));
        Assert.Equal(1000, snap.P95); // all waited ~TimeoutMs
    }

    [Fact]
    public async Task A_502_is_sampled()
    {
        using var proxy = Proxy(fakeClock: false, baseUrl: DeadUpstreamUrl());
        using var client = TestProxy.CreateKeyedClient(proxy);

        Assert.Equal(HttpStatusCode.BadGateway, (await client.GetAsync("/proxy/anything")).StatusCode);
        var snap = await Read(client);

        Assert.Equal(1, snap.SampleCount);
        Assert.NotNull(snap.P50);
        Assert.True(snap.P50 < TimeoutMs, $"a refused connection is fast, but p50 = {snap.P50}");
    }

    [Fact]
    public async Task A_client_abort_is_not_sampled()
    {
        using var proxy = Proxy(fakeClock: false);
        using var client = TestProxy.CreateKeyedClient(proxy);

        using (var giveUp = new CancellationTokenSource(TimeSpan.FromMilliseconds(TimeoutMs / 5)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => client.GetAsync("/proxy/hang", giveUp.Token));
        await Task.Delay(TimeoutMs * 3); // past where a missed abort would have become a 504

        Assert.Equal(0, (await Read(client)).SampleCount);
    }

    // ---- 4. Error rate and counters ------------------------------------------------------

    [Fact]
    public async Task Error_rate_is_errors_over_total_requests()
    {
        using var proxy = Proxy();
        using var client = TestProxy.CreateKeyedClient(proxy);

        for (var i = 0; i < 3; i++) await client.GetAsync("/proxy/status/200");
        await client.GetAsync("/proxy/status/500");
        var snap = await Read(client);

        Assert.Equal(4, snap.Long("totalRequests"));
        Assert.Equal(1, snap.Long("totalErrors"));
        Assert.Equal(0.25, snap.ErrorRate!.Value, 6);
    }

    [Fact]
    public async Task Rejections_do_not_dilute_the_error_rate()
    {
        using var proxy = Proxy();
        using var keyed = TestProxy.CreateKeyedClient(proxy);
        using var anonymous = proxy.CreateClient();

        await keyed.GetAsync("/proxy/status/500");
        for (var i = 0; i < 9; i++) await anonymous.GetAsync("/proxy/status/200"); // 401s
        var snap = await Read(keyed);

        Assert.Equal(1.0, snap.ErrorRate!.Value, 6); // 1 error / 1 request, not 1 / 10
    }

    [Fact]
    public async Task Counters_in_the_json_match_the_metrics_service()
    {
        using var proxy = Proxy();
        using var keyed = TestProxy.CreateKeyedClient(proxy);
        using var anonymous = proxy.CreateClient();
        var metrics = proxy.Services.GetRequiredService<Metrics>();

        await keyed.GetAsync("/proxy/status/200");
        await keyed.GetAsync("/proxy/status/502");
        await anonymous.GetAsync("/proxy/status/200");
        var snap = await Read(keyed);

        Assert.Equal(metrics.TotalRequests, snap.Long("totalRequests"));
        Assert.Equal(metrics.TotalErrors, snap.Long("totalErrors"));
        Assert.Equal(metrics.TotalRejected, snap.Long("totalRejected"));
        Assert.Equal(metrics.TotalRateLimited, snap.Long("totalRateLimited"));
        Assert.Equal(metrics.TotalUnauthorized, snap.Long("totalUnauthorized"));
        Assert.Equal(metrics.TotalCircuitOpen, snap.Long("totalCircuitOpen"));
    }

    // ---- 5. Concurrency -------------------------------------------------------------------

    [Fact]
    public async Task Sample_count_is_exact_under_concurrency()
    {
        for (var run = 0; run < 5; run++)
        {
            using var proxy = Proxy(fakeClock: false, maxConcurrency: 200);
            using var client = TestProxy.CreateKeyedClient(proxy);

            var statuses = await FireAtOnce(client, 200, "/proxy/status/200");
            Assert.All(statuses, s => Assert.Equal(HttpStatusCode.OK, s));
            var snap = await Read(client);

            Assert.Equal(200, snap.SampleCount);
            Assert.Equal(200, snap.Long("totalRequests"));
        }
    }

    [Fact]
    public async Task Reading_while_requests_record_is_always_self_consistent()
    {
        // The torn-read scenario: /metrics walks the buckets while requests are recording.
        // No sample here can overflow, so whenever sampleCount > 0 both percentiles must be
        // real bucket edges and p50 <= p95. A walk that ran off the end would give null or a 500.
        using var proxy = Proxy(fakeClock: false, maxConcurrency: 50);
        using var client = TestProxy.CreateKeyedClient(proxy);

        var load = FireAtOnce(client, 600, "/proxy/slow/1");
        var reads = 0;
        while (!load.IsCompleted)
        {
            var snap = await Read(client);
            reads++;
            if (snap.SampleCount == 0) continue;
            Assert.Equal(0, snap.OverflowCount);
            Assert.NotNull(snap.P50);
            Assert.NotNull(snap.P95);
            Assert.Contains((int)snap.P50!.Value, Edges);
            Assert.Contains((int)snap.P95!.Value, Edges);
            Assert.True(snap.P50 <= snap.P95, $"p50 {snap.P50} > p95 {snap.P95}");
        }
        await load;

        Assert.True(reads > 1, "setup failed: the load finished before /metrics was read twice");
        Assert.Equal(600, (await Read(client)).SampleCount);
    }
}
