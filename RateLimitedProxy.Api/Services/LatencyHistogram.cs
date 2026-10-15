namespace RateLimitedProxy.Api.Services;

public readonly record struct LatencySnapshot(
    long Count,
    double? P50,
    double? P95,
    long OverflowCount
);

public class LatencyHistogram
{
    private static readonly double[] edges = [1, 2, 5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000];
    private readonly long[] _counts = new long[edges.Length + 1];

    public void Record(TimeSpan elapsed)
    {
        var ms = elapsed.TotalMilliseconds;
        var i = 0;
        while (i < edges.Length && ms > edges[i])
            i++;
        Interlocked.Increment(ref _counts[i]);
    }

    public LatencySnapshot Snapshot()
    {
        var copy = new long[_counts.Length];
        for (var i = 0; i < copy.Length; i++)
            copy[i] = Interlocked.Read(ref _counts[i]);
        var n = copy.Sum();
        return new LatencySnapshot(n, Percentile(copy, n, 50), Percentile(copy, n, 95), copy[^1]);
    }

    public static double? Percentile(long[] counts, long n, int pct)
    {
        if (n == 0)
            return null;
        var rank = (pct * n + 99) / 100;
        long running = 0;
        for (var i = 0; i < counts.Length; i++)
        {
            running += counts[i];
            if (running >= rank)
                return i < edges.Length ? edges[i] : null;
        }
        return null;
    }
}
