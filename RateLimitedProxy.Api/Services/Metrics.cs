namespace RateLimitedProxy.Api.Services;

public class Metrics
{
    private long _totalRequests;
    private long _totalErrors;
    private long _totalRejected;
    private long _totalUnauthorized;
    private long _totalRateLimited;
    public long TotalRateLimited => Interlocked.Read(ref _totalRateLimited);

    public void RecordRateLimited() => Interlocked.Increment(ref _totalRateLimited);

    public long TotalUnauthorized => Interlocked.Read(ref _totalUnauthorized);

    public void RecordUnauthorized() => Interlocked.Increment(ref _totalUnauthorized);

    public long TotalRejected => Interlocked.Read(ref _totalRejected);

    public void RecordRejected() => Interlocked.Increment(ref _totalRejected);

    //public method that takes the status code;
    public void ProcessRequest(int statusCode)
    {
        Interlocked.Increment(ref _totalRequests);
        if (statusCode >= 500)
        {
            Interlocked.Increment(ref _totalErrors);
        }
    }

    //read only property that returns the total requests
    public long TotalRequests => _totalRequests;

    //read only property that returns the total errors
    public long TotalErrors => _totalErrors;
}
