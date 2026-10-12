using Microsoft.Extensions.Primitives;

namespace RateLimitedProxy.Api.Services;

public class HopByHopHeaders
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host",
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade",
    };

    public static bool IsHopByHop(string name) => Names.Contains(name);

    public static HashSet<string> NamedInConnection(
        IEnumerable<KeyValuePair<string, StringValues>> headers
    )
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in headers)
        {
            if (!name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            foreach (var value in values)
            {
                foreach (
                    var token in (value ?? "").Split(
                        ',',
                        StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries
                    )
                )
                    result.Add(token);
            }
        }
        return result;
    }
}
