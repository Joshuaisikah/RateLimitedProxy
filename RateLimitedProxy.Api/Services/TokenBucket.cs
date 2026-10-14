namespace RateLimitedProxy.Api.Services;

public class TokenBucket
{
    private readonly object _lock = new();
    private readonly int _capacity;
    private readonly double _refillPerSecond;
    private readonly TimeProvider _time;
    private double _tokens;
    private long _latestTimestamp;

    public TokenBucket(int capacity, double refillPerSecond, TimeProvider time)
    {
        _tokens = capacity;
        _capacity = capacity;
        _refillPerSecond = refillPerSecond;
        _time = time;
        _latestTimestamp = time.GetTimestamp();
    }

    public bool TryTake(out double waitSeconds)
    {
        lock (_lock)
        {
            var now = _time.GetTimestamp();
            var elapsedSeconds = _time.GetElapsedTime(_latestTimestamp, now).TotalSeconds;
            _latestTimestamp = now;
            _tokens = Math.Min(_capacity, _tokens + elapsedSeconds * _refillPerSecond);
            if (_tokens >= 1)
            {
                _tokens -= 1;
                waitSeconds = 0;
                return true;
            }
            else
            {
                waitSeconds = (1 - _tokens) / _refillPerSecond;
                return false;
            }
        }
    }
}
