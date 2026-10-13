using Microsoft.Extensions.Options;

namespace RateLimitedProxy.Api.Services;

public class ConcurrencyLimiter
{
    private readonly SemaphoreSlim _slots;

    public ConcurrencyLimiter(IOptions<UpstreamOptions> options)
    {
        var max = options.Value.MaxConcurrency;

        _slots = new SemaphoreSlim(max, max);
    }

    public Task WaitAsync(CancellationToken cancellationToken) =>
        _slots.WaitAsync(cancellationToken);

    public void Release() => _slots.Release();
}
