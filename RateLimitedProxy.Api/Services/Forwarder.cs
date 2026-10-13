using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace RateLimitedProxy.Api.Services;

public class Forwarder
{
    private readonly HttpClient _httpClient;
    private readonly UpstreamOptions _options;

    public Forwarder(HttpClient httpClient, IOptions<UpstreamOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
    }

    public async Task<HttpResponseMessage> ForwardRequestAsync(
        string method,
        string path,
        string query,
        IEnumerable<KeyValuePair<string, StringValues>> headers,
        Stream? body,
        CancellationToken cancellationToken
    )
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(_options.TimeoutMs));
        var target = new Uri(path + query, UriKind.Relative);
        using var request = new HttpRequestMessage(new HttpMethod(method), target);
        if (body != null)
        {
            request.Content = new StreamContent(body);
        }
        var skip = HopByHopHeaders.NamedInConnection(headers);
        foreach (var (name, value) in headers)
        {
            if (HopByHopHeaders.IsHopByHop(name) || skip.Contains(name))
                continue;
            if (!request.Headers.TryAddWithoutValidation(name, value.ToArray()))
                request.Content?.Headers.TryAddWithoutValidation(name, value.ToArray());
        }
        try
        {
            var response = await _httpClient.SendAsync(request, cts.Token);
            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpstreamTimeoutException();
        }
    }
}
