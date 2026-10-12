using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shiny.AspNet.Internal;

namespace Shiny.AspNet.Ssh;

/// <summary>
/// Publishes an ASP.NET Core app through SSH remote port forwarding — <c>ssh -R</c>, in library form.
/// <para>
/// The app opens an outbound SSH connection and asks the server to forward a port back down it, to
/// the address Kestrel is listening on. Nothing ever connects *to* this machine. Any SSH endpoint you
/// can log in to will do: a VPS you own, or a hosted tunnel like sish or Pinggy (see
/// <see cref="QuickTunnel"/>).
/// </para>
/// <para>
/// Starts and stops independently of the app: it can be serving locally the whole time and expose
/// itself publicly only while someone has asked for it. Forwarded requests arrive as loopback
/// connections — SSH does not carry the caller's address, so <c>RemoteIpAddress</c> is the forward's
/// loopback end, and <c>Request.Scheme</c> is <c>http</c> even when the public edge is HTTPS.
/// </para>
/// </summary>
public sealed class SshTunnel : IAsyncDisposable
{
    readonly IServer? server;
    readonly SshTunnelOptions options;
    readonly ILoggerFactory? loggerFactory;
    readonly SemaphoreSlim gate = new(1, 1);

    SshForwarder? forwarder;

    /// <param name="server">
    /// The running server whose address is forwarded to. May be null when
    /// <see cref="SshTunnelOptions.LocalPort"/> is set explicitly.
    /// </param>
    public SshTunnel(IServer? server, SshTunnelOptions options, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (server is null && options.LocalPort <= 0)
            throw new ArgumentException(
                $"Without a server to read the port from, {nameof(SshTunnelOptions)}.{nameof(SshTunnelOptions.LocalPort)} must be set.",
                nameof(server)
            );

        this.server = server;
        this.options = options;
        this.loggerFactory = loggerFactory;
    }

    /// <summary>The public address, once the tunnel is up.</summary>
    public string? PublicUrl => this.forwarder?.PublicUrl;

    /// <summary>The port the SSH server bound, which is only known after connecting if you asked for 0.</summary>
    public int RemotePort => this.forwarder?.RemotePort ?? 0;

    /// <summary>True while the SSH connection is up. Goes false between drop and reconnect.</summary>
    public bool IsConnected => this.forwarder?.IsConnected ?? false;

    /// <summary>Raised when the SSH connection drops or comes back.</summary>
    public event EventHandler<bool>? ConnectivityChanged;

    /// <summary>
    /// Raised when the public address changes — which a hosted tunnel does on every reconnect.
    /// Raised on a background thread.
    /// </summary>
    public event EventHandler<string?>? PublicUrlChanged;

    /// <summary>Opens the tunnel. Returns once the forward is up, with the public address if one is known.</summary>
    public async Task<string?> StartAsync(CancellationToken cancellationToken = default)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.forwarder is not null)
                return this.PublicUrl;

            var (host, port) = this.options.LocalPort > 0
                ? (this.options.LocalHost, this.options.LocalPort)
                : LocalServerEndpoint.Resolve(this.server!);

            var created = new SshForwarder(
                this.options,
                host,
                port,
                this.loggerFactory?.CreateLogger<SshTunnel>() ?? NullLogger<SshTunnel>.Instance
            );

            // Subscribed before connecting, so the first address is reported through the same path
            // as every later one.
            created.PublicUrlChanged += this.OnPublicUrlChanged;
            created.ConnectivityChanged += this.OnConnectivityChanged;

            try
            {
                await created.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                created.PublicUrlChanged -= this.OnPublicUrlChanged;
                created.ConnectivityChanged -= this.OnConnectivityChanged;

                await created.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            this.forwarder = created;
            return created.PublicUrl;
        }
        finally
        {
            this.gate.Release();
        }
    }

    /// <summary>Closes the tunnel. The app keeps running and stays reachable locally.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.forwarder is not { } running)
                return;

            this.forwarder = null;

            running.PublicUrlChanged -= this.OnPublicUrlChanged;
            running.ConnectivityChanged -= this.OnConnectivityChanged;

            await running.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            this.gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await this.StopAsync(CancellationToken.None).ConfigureAwait(false);
        this.gate.Dispose();
    }

    void OnPublicUrlChanged(object? sender, string? url) => this.PublicUrlChanged?.Invoke(this, url);

    void OnConnectivityChanged(object? sender, bool online) => this.ConnectivityChanged?.Invoke(this, online);
}
