using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;

namespace RateLimitedProxy.Tests;

// Upstream:BaseUrl with a path (decided 2026-10-15, option (a): validate, don't normalise).
//
// RFC 3986 §5.2.3: resolving a relative path drops the base path's last segment.
//   https://api.example.com/v1   + users/5 -> https://api.example.com/users/5     (v1 lost, silently)
//   https://api.example.com/v1/  + users/5 -> https://api.example.com/v1/users/5
//
// Contract:
//   BaseUrl path not ending in "/"   -> the proxy refuses to start (options validation)
//   BaseUrl with no path at all      -> fine, slash or not (nothing to drop)
//   BaseUrl path ending in "/"       -> the path is kept in front of every forwarded path
public class BaseUrlTests : IClassFixture<FakeUpstream>
{
    private readonly FakeUpstream _upstream;

    public BaseUrlTests(FakeUpstream upstream) => _upstream = upstream;

    private static WebApplicationFactory<Program> ProxyFor(string baseUrl) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            TestProxy.AllowTestKey(b);
            b.UseSetting("Upstream:BaseUrl", baseUrl);
        });

    private static async Task<EchoResponse> EchoOf(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"expected the upstream echo (200), got {(int)response.StatusCode}: {json}");
        return JsonSerializer.Deserialize<EchoResponse>(json, EchoResponse.Json)!;
    }

    [Theory]
    [InlineData("")]    // http://127.0.0.1:port
    [InlineData("/")]   // http://127.0.0.1:port/
    public async Task A_host_only_base_url_works_with_or_without_the_slash(string suffix)
    {
        using var proxy = ProxyFor(_upstream.BaseUrl.TrimEnd('/') + suffix);
        using var client = TestProxy.CreateKeyedClient(proxy);

        var echo = await EchoOf(await client.GetAsync("/proxy/users/5"));

        Assert.Equal("/users/5", echo.Path);
    }

    [Fact]
    public async Task A_base_path_ending_in_a_slash_is_kept()
    {
        using var proxy = ProxyFor(_upstream.BaseUrl.TrimEnd('/') + "/v1/");
        using var client = TestProxy.CreateKeyedClient(proxy);

        var echo = await EchoOf(await client.GetAsync("/proxy/users/5?x=1"));

        Assert.Equal("/v1/users/5", echo.Path);
        Assert.Equal("?x=1", echo.Query);
    }

    [Theory]
    [InlineData("/v1")]
    [InlineData("/api/v1")]
    public void A_base_path_without_a_trailing_slash_stops_the_proxy_from_starting(string path)
    {
        var baseUrl = _upstream.BaseUrl.TrimEnd('/') + path;

        var thrown = TestProxy.StartupFailure(() => ProxyFor(baseUrl));

        Assert.True(thrown is not null,
            $"BaseUrl {baseUrl} started - every request would silently lose '{path.Split('/').Last()}'");
        Assert.True(TestProxy.Flatten(thrown!).Any(e => e is OptionsValidationException),
            $"startup failed, but not from options validation: {thrown!.GetType().Name}: {thrown.Message}");
    }
}
