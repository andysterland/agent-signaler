using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace AgentSignaler.RpcHost;

internal static partial class RpcEndpointPolicy
{
    [GeneratedRegex(@"\Ahttps?://(localhost|127\.0\.0\.1|\[::1\])(?::([0-9]{1,5}))?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OriginPattern();

    public static bool IsOriginAllowed(string? origin)
    {
        if (string.IsNullOrEmpty(origin) || origin.Length > 256 || !OriginPattern().IsMatch(origin)) return false;
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Port is >= 0 and <= 65535 &&
            string.IsNullOrEmpty(uri.UserInfo);
    }

    public static bool IsRequestAllowed(HttpContext context, int port, bool requireOrigin)
    {
        var host = context.Request.Host;
        return context.Connection.RemoteIpAddress is { } remote && IPAddress.IsLoopback(remote) &&
            host.Host.ToLowerInvariant() is "localhost" or "127.0.0.1" or "[::1]" &&
            host.Port == port &&
            (!requireOrigin || (context.Request.Headers.Origin.Count == 1 &&
                IsOriginAllowed(context.Request.Headers.Origin[0])));
    }
}
