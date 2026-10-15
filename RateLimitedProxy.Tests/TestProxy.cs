using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace RateLimitedProxy.Tests;

// Milestone 5 made an API key mandatory. The M3/M4 tests aren't about rate limiting,
// so they all use one known key with a bucket so big it never runs out.
public static class TestProxy
{
    public const string Key = "test-key";

    // "Testing", not the default "Development": Development loads user secrets and
    // appsettings.Development.json, and their keys would merge into every test's key list.
    public const string Environment = "Testing";

    // Test classes in the same xUnit collection run one after another, not in parallel.
    // Every class that asserts on TimeoutMs-sized timings goes here, so one class's load
    // (e.g. 800 concurrent requests) can't push another class's requests past the timeout.
    public const string TimingSensitive = "timing-sensitive";

    public static void AllowTestKey(IWebHostBuilder b)
    {
        b.UseEnvironment(Environment);
        b.UseSetting("RateLimit:ApiKeys:0", Key);
        b.UseSetting("RateLimit:Capacity", "1000000");
        b.UseSetting("RateLimit:RefillPerSecond", "1000000");
        NeverTripBreaker(b);
    }

    // Milestone 6 added a circuit breaker. The older tests deliberately produce strings of
    // 5xx/504/502 and must not have the breaker start answering for the upstream.
    // CircuitBreakerTests override this with their own threshold.
    public static void NeverTripBreaker(IWebHostBuilder b) =>
        b.UseSetting("CircuitBreaker:FailureThreshold", "1000000");

    public static HttpClient CreateKeyedClient(WebApplicationFactory<Program> proxy)
    {
        var client = proxy.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", Key);
        return client;
    }

    // Starts a proxy and returns why it failed to start, or null if it started.
    // When startup throws, WebApplicationFactory sometimes reports ObjectDisposedException
    // instead of the real exception (a race inside the factory under parallel test load),
    // so retry with a fresh factory until the real one shows.
    public static Exception? StartupFailure(Func<WebApplicationFactory<Program>> createProxy)
    {
        Exception? thrown = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var proxy = createProxy();
            thrown = null;
            try { proxy.CreateClient().Dispose(); }
            catch (Exception e) { thrown = e; }

            if (thrown is not ObjectDisposedException) break;
        }
        return thrown;
    }

    public static IEnumerable<Exception> Flatten(Exception e)
    {
        yield return e;
        if (e is AggregateException agg)
            foreach (var inner in agg.InnerExceptions.SelectMany(Flatten)) yield return inner;
        else if (e.InnerException is not null)
            foreach (var inner in Flatten(e.InnerException)) yield return inner;
    }
}
