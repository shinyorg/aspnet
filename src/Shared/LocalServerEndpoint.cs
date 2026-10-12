using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Shiny.AspNet.Internal;

/// <summary>
/// Works out where on this machine a tunnel should send its traffic: the address Kestrel actually
/// bound, read after it started.
/// <para>
/// Read off the running server rather than off configuration, because configuration is not the
/// answer — <c>http://127.0.0.1:0</c> becomes a real port only once Kestrel has bound it, and an
/// app started with <c>--urls</c> or <c>ASPNETCORE_URLS</c> never told the code which port it chose.
/// </para>
/// </summary>
static class LocalServerEndpoint
{
    /// <summary>
    /// The host and port to forward to.
    /// <para>
    /// Plain <c>http</c> is preferred, and required: every tunnel here terminates TLS at its public
    /// edge and speaks plaintext to the origin, so forwarding to an <c>https</c> endpoint would hand
    /// Kestrel a request it can only answer with a handshake failure.
    /// </para>
    /// </summary>
    public static (string Host, int Port) Resolve(IServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
        if (addresses is null || addresses.Count == 0)
            throw new InvalidOperationException(
                "The server is not listening, so there is no address to tunnel. Tunnels start once the " +
                "application has started — register them with the Add…Tunnel methods, or call StartAsync " +
                "after app.StartAsync()."
            );

        foreach (var address in addresses)
        {
            if (TryParse(address, out var scheme, out var host, out var port)
                && scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
                return (host, port);
        }

        throw new InvalidOperationException(
            $"The server is only listening on {string.Join(", ", addresses)}. A tunnel terminates TLS at its " +
            "public edge and forwards plain HTTP, so it needs an http:// binding to forward to — add one " +
            "(e.g. http://127.0.0.1:0) or set the tunnel's local port explicitly."
        );
    }

    internal static bool TryParse(string address, out string scheme, out string host, out int port)
    {
        scheme = host = string.Empty;
        port = 0;

        var schemeEnd = address.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
            return false;

        scheme = address[..schemeEnd];
        var authority = address[(schemeEnd + 3)..];

        var pathStart = authority.IndexOf('/');
        if (pathStart >= 0)
            authority = authority[..pathStart];

        var portStart = authority.LastIndexOf(':');
        if (portStart < 0 || authority.EndsWith(']'))
            return false;

        if (!int.TryParse(authority[(portStart + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out port) || port <= 0)
            return false;

        host = ToConnectableHost(authority[..portStart]);
        return true;
    }

    /// <summary>
    /// A bound address is not always one you can connect to. The wildcards (<c>0.0.0.0</c>,
    /// <c>[::]</c>, <c>*</c>, <c>+</c>) and <c>localhost</c> all accept loopback IPv4, so they become
    /// <c>127.0.0.1</c>; a specific address is used as-is, because a server bound only to a LAN
    /// address does not answer on loopback at all.
    /// </summary>
    static string ToConnectableHost(string host)
    {
        if (host is "*" or "+" or "0.0.0.0" or "[::]" || host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return "127.0.0.1";

        var bare = host.Trim('[', ']');
        return IPAddress.TryParse(bare, out _) ? bare : host;
    }

    /// <summary>The host formatted for a URL — IPv6 literals need their brackets back.</summary>
    public static string ForUrl(string host)
        => host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;
}
