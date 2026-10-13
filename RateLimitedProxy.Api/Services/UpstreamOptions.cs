namespace RateLimitedProxy.Api.Services;

public class UpstreamOptions
{
    public const string SectionName = "Upstream";
    public string BaseUrl { get; set; } = string.Empty;
    public int TimeoutMs { get; set; } = 5000;
    public int MaxConcurrency { get; set; } = 20;
}
