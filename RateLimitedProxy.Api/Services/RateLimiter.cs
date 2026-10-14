using Microsoft.Extensions.Options;

namespace RateLimitedProxy.Api.Services;

public enum RateLimitOutcome
{
    UnknownKey,
    Limited,
    Allowed,
}

public readonly record struct RateLimitResult(RateLimitOutcome Outcome, int RetryAfterSeconds);

public class RateLimiter
{
    private readonly Dictionary<string, TokenBucket> _buckets;

    public RateLimiter(IOptions<RateLimitOptions> rateLimitOptions, TimeProvider time)
    {
        var o = rateLimitOptions.Value;
        _buckets = o.ApiKeys.ToDictionary(
            key => key,
            key => new TokenBucket(o.Capacity, o.RefillPerSecond, time),
            StringComparer.Ordinal
        );
    }

    public RateLimitResult Check(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || !_buckets.TryGetValue(apiKey, out var bucket))
            return new RateLimitResult(RateLimitOutcome.UnknownKey, 0);

        if (bucket.TryTake(out var waitSeconds))
            return new RateLimitResult(RateLimitOutcome.Allowed, 0);

        var seconds = (int)Math.Max(1, Math.Ceiling(waitSeconds));
        return new RateLimitResult(RateLimitOutcome.Limited, seconds);
    }
}
