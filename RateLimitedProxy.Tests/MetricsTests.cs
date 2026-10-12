using RateLimitedProxy.Api.Services;

namespace RateLimitedProxy.Tests;

// Milestone 2: "Counters are exact under concurrency — run it 20 times and check."
public class MetricsTests
{
    private const int Threads = 8;
    private const int CallsPerThread = 1_000_000;

    // ---- Classification rule: 5xx is an error, everything else is not ----

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    [InlineData(599)]
    public void FiveHundreds_count_as_errors(int status)
    {
        var metrics = new Metrics();
        metrics.ProcessRequest(status);

        Assert.Equal(1, metrics.TotalRequests);
        Assert.Equal(1, metrics.TotalErrors);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(201)]
    [InlineData(301)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(499)]
    public void Non_5xx_are_not_errors(int status)
    {
        var metrics = new Metrics();
        metrics.ProcessRequest(status);

        Assert.Equal(1, metrics.TotalRequests);
        Assert.Equal(0, metrics.TotalErrors);
    }

    // ---- Concurrency: one shared instance, many threads, exact totals ----

    public static IEnumerable<object[]> TwentyRuns() =>
        Enumerable.Range(1, 20).Select(run => new object[] { run });

    [Theory]
    [MemberData(nameof(TwentyRuns))]
    public void Totals_are_exact_under_concurrency(int run)
    {
        _ = run; // only here so each of the 20 runs is reported separately

        // One instance shared by every thread, exactly like the DI singleton.
        var metrics = new Metrics();

        // Every thread waits here until all are ready, so they truly overlap.
        using var startLine = new Barrier(Threads);

        var workers = Enumerable.Range(0, Threads).Select(t => new Thread(() =>
        {
            startLine.SignalAndWait();
            for (var i = 0; i < CallsPerThread; i++)
            {
                // Every 4th call is a 503, so errors are exactly a quarter of total.
                metrics.ProcessRequest(i % 4 == 0 ? 503 : 200);
            }
        })).ToList();

        workers.ForEach(w => w.Start());
        workers.ForEach(w => w.Join());

        const long expectedTotal = (long)Threads * CallsPerThread;
        const long expectedErrors = expectedTotal / 4;

        Assert.True(metrics.TotalRequests == expectedTotal,
            $"TotalRequests: expected {expectedTotal:N0}, got {metrics.TotalRequests:N0} " +
            $"(lost {expectedTotal - metrics.TotalRequests:N0} updates)");
        Assert.True(metrics.TotalErrors == expectedErrors,
            $"TotalErrors: expected {expectedErrors:N0}, got {metrics.TotalErrors:N0} " +
            $"(lost {expectedErrors - metrics.TotalErrors:N0} updates)");
    }
}
