using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using RateLimitedProxy.Api.Services;

namespace RateLimitedProxy.Tests;

// Milestone 3: ANY /proxy/{**path} forwards to Upstream:BaseUrl, with Upstream:TimeoutMs.
//
// Contract agreed on 2026-10-12:
//   Upstream:BaseUrl    - where to forward to
//   Upstream:TimeoutMs  - upstream timeout in milliseconds -> 504 when exceeded
//   ANY /proxy/{**path} - the proxy endpoint
//   Metrics             - records the FINAL status the client received
public class ProxyForwardingTests : IClassFixture<FakeUpstream>, IDisposable
{
    private const int TimeoutMs = 500;

    private readonly FakeUpstream _upstream;
    private readonly WebApplicationFactory<Program> _proxy;
    private readonly HttpClient _client;

    public ProxyForwardingTests(FakeUpstream upstream)
    {
        _upstream = upstream;

        // A fresh proxy per test, so every test starts with fresh Metrics.
        _proxy = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Upstream:BaseUrl", upstream.BaseUrl);
            b.UseSetting("Upstream:TimeoutMs", TimeoutMs.ToString());
        });
        _client = _proxy.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _proxy.Dispose();
    }

    private async Task<EchoResponse> EchoOf(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"expected the upstream echo (200), got {(int)response.StatusCode}: {json}");
        return JsonSerializer.Deserialize<EchoResponse>(json, EchoResponse.Json)!;
    }

    // ---- 1. URL mapping -------------------------------------------------------

    [Fact]
    public async Task Path_and_query_are_forwarded_exactly()
    {
        var echo = await EchoOf(await _client.GetAsync("/proxy/users/5?active=true&page=2"));

        Assert.Equal("/users/5", echo.Path);
        Assert.Equal("?active=true&page=2", echo.Query);
    }

    [Fact]
    public async Task Encoded_characters_in_the_path_survive()
    {
        var echo = await EchoOf(await _client.GetAsync("/proxy/files/a%20b.txt"));

        Assert.Equal("/files/a b.txt", echo.Path);
    }

    // ---- 2. Method and body ---------------------------------------------------

    [Theory]
    [InlineData("GET")]
    [InlineData("DELETE")]
    public async Task Methods_without_a_body_are_forwarded(string method)
    {
        var echo = await EchoOf(await _client.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/proxy/items/1")));

        Assert.Equal(method, echo.Method);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task Methods_with_a_body_forward_the_body_and_content_type(string method)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), "/proxy/items")
        {
            Content = new StringContent("""{"name":"widget","qty":3}""", Encoding.UTF8, "application/json")
        };

        var echo = await EchoOf(await _client.SendAsync(request));

        Assert.Equal(method, echo.Method);
        Assert.Equal("""{"name":"widget","qty":3}""", echo.Body);
        Assert.StartsWith("application/json", echo.Headers.GetValueOrDefault("Content-Type"));
    }

    // ---- 3. Headers -----------------------------------------------------------

    [Fact]
    public async Task Client_host_is_not_forwarded()
    {
        var echo = await EchoOf(await _client.GetAsync("/proxy/anything"));

        var upstreamHost = new Uri(_upstream.BaseUrl).Authority; // e.g. 127.0.0.1:43127
        Assert.Equal(upstreamHost, echo.Host);
    }

    [Fact]
    public async Task Ordinary_headers_are_forwarded()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/proxy/anything");
        request.Headers.Add("X-Correlation-Id", "abc-123");
        request.Headers.Add("Accept-Language", "sw-KE");

        var echo = await EchoOf(await _client.SendAsync(request));

        Assert.Equal("abc-123", echo.Headers.GetValueOrDefault("X-Correlation-Id"));
        Assert.Equal("sw-KE", echo.Headers.GetValueOrDefault("Accept-Language"));
    }

    [Fact]
    public async Task Proxy_authorization_is_not_forwarded()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/proxy/anything");
        request.Headers.TryAddWithoutValidation("Proxy-Authorization", "Basic c2VjcmV0");

        var echo = await EchoOf(await _client.SendAsync(request));

        Assert.False(echo.Headers.ContainsKey("Proxy-Authorization"),
            "Proxy-Authorization is a hop-by-hop credential for the proxy, it must not reach the upstream");
    }

    // ---- 4. Response passthrough ----------------------------------------------

    [Theory]
    [InlineData(200)]
    [InlineData(201)]
    [InlineData(404)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task Upstream_status_body_and_headers_come_back_unchanged(int status)
    {
        var response = await _client.GetAsync($"/proxy/status/{status}");

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal($"status {status}", await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.TryGetValues("X-Upstream", out var values) && values.Single() == "fake",
            "the upstream's response header X-Upstream should be copied back to the client");
    }

    // ---- 5. Timeout -----------------------------------------------------------

    [Fact]
    public async Task Hanging_upstream_gives_a_timely_504()
    {
        var clock = Stopwatch.StartNew();
        var response = await _client.GetAsync("/proxy/hang");
        clock.Stop();

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.True(clock.ElapsedMilliseconds < TimeoutMs + 2_000,
            $"504 took {clock.ElapsedMilliseconds} ms with a {TimeoutMs} ms timeout");
        Assert.True(clock.ElapsedMilliseconds >= TimeoutMs * 0.8,
            $"504 came back after only {clock.ElapsedMilliseconds} ms; was the upstream even called?");
    }

    // ---- 6. Metrics record the final status ------------------------------------

    [Fact]
    public async Task Metrics_count_every_request_and_only_5xx_as_errors()
    {
        var metrics = _proxy.Services.GetRequiredService<Metrics>();

        await _client.GetAsync("/proxy/status/200");
        await _client.GetAsync("/proxy/status/404"); // not an error
        await _client.GetAsync("/proxy/status/500"); // error
        await _client.GetAsync("/proxy/status/503"); // error
        await _client.GetAsync("/proxy/hang");       // 504 from the proxy -> error

        Assert.Equal(5, metrics.TotalRequests);
        Assert.Equal(3, metrics.TotalErrors);
    }

    [Fact]
    public async Task Requests_actually_reach_the_upstream()
    {
        var before = _upstream.Hits;

        await _client.GetAsync("/proxy/status/200");

        Assert.Equal(before + 1, _upstream.Hits);
    }
}
