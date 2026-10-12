namespace RateLimitedProxy.Api.Models;

public class Metrics
{
    private long _totalRequests;
    private long _totalErrors;

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
