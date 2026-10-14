namespace RateLimitedProxy.Api.Services;

public class RateLimitOptions
{
    public const string SectionName = "RateLimit";
    public int Capacity { get; set; } = 10;
    public double RefillPerSecond { get; set; } = 1.0;
    public string[] ApiKeys { get; set; } = [];
}
