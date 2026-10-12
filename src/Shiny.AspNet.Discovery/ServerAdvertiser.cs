using System.Net.NetworkInformation;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shiny.Net.Discovery;

namespace Shiny.AspNet.Discovery;

/// <summary>What to advertise the app as on the local link.</summary>
public sealed class ServerAdvertisementOptions
{
    /// <summary>
    /// The DNS-SD service type. <c>_http._tcp</c> by default, which is what a browser, a printer
    /// dialog and every generic Bonjour tool already look for.
    /// <para>
    /// Use a private type — <c>_myapp._tcp</c> — when the point is for <em>your</em> app to find
    /// <em>your</em> other instance. Browsing <c>_http._tcp</c> on a busy network turns up printers,
    /// routers and NAS boxes, and telling yours apart from those means filtering on a TXT record
    /// anyway.
    /// </para>
    /// </summary>
    public string ServiceType { get; set; } = "_http._tcp";

    /// <summary>
    /// The human-readable instance name. Defaults to the machine name, which is what the user
    /// already calls this machine.
    /// <para>
    /// Not guaranteed to survive: when the link already has a service with this name the responder
    /// renames it, so read the final one back from <see cref="IServerAdvertiser.Publication"/>.
    /// </para>
    /// </summary>
    public string? InstanceName { get; set; }

    /// <summary>
    /// The path the service lives at, published as the conventional <c>path</c> TXT record so a
    /// generic browser opens the right URL.
    /// </summary>
    public string Path { get; set; } = "/";

    /// <summary>
    /// Which of the server's bound addresses to advertise, by scheme: <c>"http"</c> or <c>"https"</c>.
    /// Null (the default) prefers a cleartext endpoint when there is one — the one a peer on the link
    /// can call without trusting a certificate — and otherwise takes the HTTPS one. The chosen scheme is
    /// published as the <c>scheme</c> TXT record.
    /// </summary>
    public string? Scheme { get; set; }

    /// <summary>
    /// Extra TXT records. Keep the total small — RFC 6763 wants the whole record inside one packet.
    /// <para>
    /// This is where an app puts what a peer needs to decide whether it cares: an app id, a
    /// protocol version, a role, a pairing state.
    /// </para>
    /// </summary>
    public IDictionary<string, string> TxtRecords { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How many times publishing the advertisement is attempted before it is given up on. Three by
    /// default.
    /// <para>
    /// The moments this runs are the moments a responder is least likely to answer: the app has just
    /// started, often at boot, and the platform's mDNS stack is coming up at its own pace. One refused
    /// registration otherwise leaves an app that is running and unfindable — which, from the user's
    /// side of it, is an app that is not there.
    /// </para>
    /// </summary>
    public int PublishAttempts { get; set; } = 3;

    /// <summary>
    /// How long to wait before the second publish attempt. One second by default, doubling up to
    /// <see cref="MaxPublishRetryDelay"/>.
    /// </summary>
    public TimeSpan PublishRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The ceiling the doubling backoff stops at. Fifteen seconds by default.</summary>
    public TimeSpan MaxPublishRetryDelay { get; set; } = TimeSpan.FromSeconds(15);
}

/// <summary>Advertises the running app on the local link, and stops when it stops.</summary>
public interface IServerAdvertiser : IAsyncDisposable
{
    /// <summary>The live publication, once there is one. Carries the name actually in use.</summary>
    IMdnsPublication? Publication { get; }

    /// <summary>
    /// Publishes the advertisement — now, when the application has already started, otherwise as soon
    /// as it does — and withdraws it when the application begins stopping.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Sends a goodbye and stops advertising. Idempotent.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Makes an ASP.NET Core app findable on the local link without anyone typing an IP address.
/// <para>
/// Binding a port solves being reachable. It does not solve being <em>found</em>: the address is
/// assigned by whatever network the machine joined, the port may be one the OS picked
/// (<c>http://0.0.0.0:0</c>), and the only ways out of that are configuration, a QR code, or this.
/// mDNS is what every printer, speaker and NAS on the link already uses, it needs no server, no account
/// and no internet, and it is the one mechanism a peer app can rely on being able to use.
/// </para>
/// <para>
/// The advertisement follows the app: published on <see cref="IHostApplicationLifetime.ApplicationStarted"/>
/// with the port read back from the server's actual bound addresses (<see cref="IServerAddressesFeature"/>),
/// so a dynamic port is advertised correctly, and withdrawn with a goodbye on
/// <see cref="IHostApplicationLifetime.ApplicationStopping"/> — before the server drains, so peers stop
/// being sent here first. The addresses are the responder's business; when the machine changes networks
/// the record is re-announced so peers already on the new link see it.
/// </para>
/// <para>
/// A publication the responder refuses is retried rather than dropped, because the failure mode it
/// leaves behind — an app running perfectly and findable by nobody — looks like success from inside
/// the app.
/// </para>
/// </summary>
public sealed class ServerAdvertiser : IServerAdvertiser
{
    readonly IMdnsManager mdns;
    readonly IServer server;
    readonly IHostApplicationLifetime? lifetime;
    readonly ServerAdvertisementOptions options;
    readonly ILogger logger;
    readonly LocalPublications local;
    readonly SemaphoreSlim gate = new(1, 1);

    int advertisedPort;
    bool started;
    bool watchingNetwork;
    CancellationTokenRegistration onStarted;
    CancellationTokenRegistration onStopping;
    Task pending = Task.CompletedTask;

    // Stamped on the thread that caused the change and compared again once the work reaches the
    // front of the gate. See OnLifetime for what goes wrong without it.
    long sequence;
    long applied;

    /// <param name="mdns">The responder to publish through.</param>
    /// <param name="server">The server whose bound addresses are advertised.</param>
    /// <param name="lifetime">
    /// When given, publishing waits for <see cref="IHostApplicationLifetime.ApplicationStarted"/> and
    /// the record is withdrawn on <see cref="IHostApplicationLifetime.ApplicationStopping"/>. When null,
    /// <see cref="StartAsync"/> publishes immediately — the server must already be listening.
    /// </param>
    /// <param name="options">What to advertise.</param>
    /// <param name="logger">Where publish failures are reported.</param>
    public ServerAdvertiser(
        IMdnsManager mdns,
        IServer server,
        IHostApplicationLifetime? lifetime,
        ServerAdvertisementOptions options,
        ILogger<ServerAdvertiser>? logger = null
    )
    {
        ArgumentNullException.ThrowIfNull(mdns);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(options);

        this.mdns = mdns;
        this.server = server;
        this.lifetime = lifetime;
        this.options = options;
        this.logger = logger ?? NullLogger<ServerAdvertiser>.Instance;
        this.local = LocalPublications.For(mdns);
    }

    public IMdnsPublication? Publication { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (this.started)
            return;

        this.started = true;
        this.WatchNetwork(true);

        if (this.lifetime is null || this.lifetime.ApplicationStarted.IsCancellationRequested)
        {
            await this.PublishAsync(this.Next(), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            this.onStarted = this.lifetime.ApplicationStarted.Register(() => this.OnLifetime(running: true));
        }

        if (this.lifetime is not null)
            this.onStopping = this.lifetime.ApplicationStopping.Register(() => this.OnLifetime(running: false));
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        this.started = false;
        this.WatchNetwork(false);
        await this.onStarted.DisposeAsync().ConfigureAwait(false);
        await this.onStopping.DisposeAsync().ConfigureAwait(false);

        // Stamped like everything else: an explicit stop must not be undone by a publication that
        // was already in flight when it was called.
        await this.WithdrawAsync(this.Next()).ConfigureAwait(false);

        // And whatever a lifetime callback set off has finished by the time this returns.
        await this.pending.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await this.StopAsync(CancellationToken.None).ConfigureAwait(false);
        this.gate.Dispose();
    }

    void OnLifetime(bool running)
    {
        // Stamped here, on the host's own lifetime thread, where the order is still the order the app
        // actually moved in. The mDNS work itself has to come off that thread — it talks to a platform
        // responder, it is slow, and it may be retrying — and the moment it does, a start and a stop
        // close together are free to land in either order. A publication applied on top of the
        // withdrawal that followed it leaves a record for an app that is gone.
        var stamp = this.Next();
        this.pending = Task.Run(() => this.ApplyAsync(stamp, running));
    }

    internal async Task ApplyAsync(long stamp, bool running)
    {
        try
        {
            if (running)
                await this.PublishAsync(stamp, CancellationToken.None).ConfigureAwait(false);
            else
                await this.WithdrawAsync(stamp).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Nothing awaits this task, so an escaping exception is one reported nowhere. Error
            // rather than warning: an advertisement that did not update is an app that cannot be
            // found, and it will go on looking healthy from the inside.
            this.logger.LogError(ex, "Failed to {Action} the mDNS advertisement", running ? "publish" : "withdraw");
        }
    }

    void WatchNetwork(bool watch)
    {
        if (watch == this.watchingNetwork)
            return;

        try
        {
            if (watch)
                NetworkChange.NetworkAddressChanged += this.OnAddressesChanged;
            else
                NetworkChange.NetworkAddressChanged -= this.OnAddressesChanged;

            this.watchingNetwork = watch;
        }
        catch (PlatformNotSupportedException)
        {
            // No change notifications here; the record simply is not re-announced on a network move.
        }
    }

    void OnAddressesChanged(object? sender, EventArgs e)
    {
        // Re-announced rather than left alone: a machine that just joined a network needs its records
        // seen by peers that were already there, and a fresh announcement is how that happens. The
        // name is preserved by the responder, so peers see an update, not a new service.
        if (!this.started || this.Publication is null)
            return;

        var stamp = this.Next();

        _ = Task.Run(async () =>
        {
            try
            {
                await this.PublishAsync(stamp, CancellationToken.None, force: true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "Failed to re-announce the mDNS advertisement after a network change");
            }
        });
    }

    /// <summary>The stamp for one request to change the advertisement. Taken on the thread that caused it.</summary>
    internal long Next() => Interlocked.Increment(ref this.sequence);

    /// <summary>
    /// Whether a newer request has already been applied, in which case this one is stale and must do
    /// nothing. Called inside the gate, so the comparison and the update cannot interleave.
    /// </summary>
    bool Superseded(long stamp)
    {
        if (stamp < this.applied)
            return true;

        this.applied = stamp;
        return false;
    }

    async Task PublishAsync(long stamp, CancellationToken cancellationToken, bool force = false)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.Superseded(stamp))
            {
                this.logger.LogDebug("Skipping a stale advertisement update; a newer one has already been applied");
                return;
            }

            if (this.Endpoint() is not { } endpoint)
            {
                // Not silence, and not an exception either. A server that is not listening on a TCP
                // port at all — TestServer, a Unix socket, a named pipe — has nothing to advertise on
                // the link; a server that should be and cannot name a port is not fine. The two are not
                // distinguishable from here, and both end with an app nobody on the link can find, so
                // both get said out loud.
                this.logger.LogWarning(
                    "The server reports no bound {Scheme} address with a TCP port, so there is no port to advertise and nothing was published",
                    this.options.Scheme ?? "http(s)"
                );
                return;
            }

            if (this.Publication is not null && !force && this.advertisedPort == endpoint.Port)
                return;

            await this.WithdrawCoreAsync().ConfigureAwait(false);
            await this.PublishCoreAsync(stamp, endpoint.Port, endpoint.Scheme, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.gate.Release();
        }
    }

    async Task PublishCoreAsync(long stamp, int port, string scheme, CancellationToken cancellationToken)
    {
        var attempts = Math.Max(1, this.options.PublishAttempts);
        var delay = Clamp(this.options.PublishRetryDelay);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var registration = new MdnsServiceRegistration
                {
                    InstanceName = this.options.InstanceName ?? Environment.MachineName,
                    ServiceType = this.options.ServiceType,
                    Port = port,
                    TxtRecords = this.BuildTxtRecords(scheme)
                };

                this.Publication = await this.mdns.Publish(registration, cancellationToken).ConfigureAwait(false);
                this.advertisedPort = port;

                // a locator on the same responder leaves this app's own advertisement out of what it finds
                this.local.Add(this.Publication);

                this.logger.LogInformation(
                    "Advertising {Instance} as {ServiceType} on port {Port}",
                    this.Publication.InstanceName,
                    this.Publication.ServiceType,
                    this.Publication.Port
                );

                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (attempt >= attempts)
                {
                    // Error, not warning. The app is up and answering and looks perfectly healthy from
                    // the inside, while no peer on the link can find it — and most alerting pipelines
                    // page on Error and only file Warning, so a warning here is a failure nobody sees.
                    this.logger.LogError(
                        ex,
                        "Gave up advertising the server on port {Port} after {Attempts} attempt(s). It is running but will not be discovered until the app restarts or the machine changes network",
                        port,
                        attempts
                    );
                    return;
                }

                this.logger.LogWarning(
                    ex,
                    "Failed to advertise the server on port {Port} (attempt {Attempt} of {Attempts}); retrying in {Delay}",
                    port,
                    attempt,
                    attempts,
                    delay
                );

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = Clamp(TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, this.options.MaxPublishRetryDelay.Ticks)));

                // A newer request is waiting on the gate this loop is holding — the app stopping, most
                // likely — and it knows more than an attempt that has already failed. Retrying would
                // advertise a service that is on its way out.
                if (Interlocked.Read(ref this.sequence) != stamp)
                {
                    this.logger.LogDebug("Abandoning the advertisement retry; the app has changed state since it started");
                    return;
                }
            }
        }
    }

    async Task WithdrawAsync(long stamp)
    {
        await this.gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (this.Superseded(stamp))
            {
                this.logger.LogDebug("Skipping a stale withdrawal; a newer advertisement update has already been applied");
                return;
            }

            await this.WithdrawCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            this.gate.Release();
        }
    }

    async Task WithdrawCoreAsync()
    {
        if (this.Publication is not { } publication)
            return;

        this.Publication = null;
        this.advertisedPort = 0;
        this.local.Remove(publication);

        try
        {
            // Disposing sends the goodbye packet, which is what stops peers holding a record for a
            // service that has gone. Without it they wait for the TTL to expire.
            await publication.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Usually benign - the responder had already dropped it - but not always, and the case
            // where it is not is a record peers will hold and offer the user for as long as its TTL
            // runs. There is nothing to retry here, since the publication is gone from this side
            // either way, so the honest thing is to say it happened.
            this.logger.LogWarning(ex, "The mDNS publication did not withdraw cleanly; peers may hold a stale record until it expires");
        }
    }

    /// <summary>A misconfigured negative delay is a configuration mistake, not a reason to throw out of a retry loop.</summary>
    static TimeSpan Clamp(TimeSpan delay) => delay < TimeSpan.Zero ? TimeSpan.Zero : delay;

    IReadOnlyDictionary<string, string> BuildTxtRecords(string scheme)
    {
        var records = new Dictionary<string, string>(this.options.TxtRecords, StringComparer.OrdinalIgnoreCase);

        // The conventional record for _http._tcp, and the one a generic browser uses to build a URL.
        if (!records.ContainsKey("path"))
            records["path"] = this.options.Path;

        if (!records.ContainsKey("scheme"))
            records["scheme"] = scheme;

        return records;
    }

    /// <summary>
    /// The address to advertise, read from what the server actually bound — after start, so port 0
    /// has become the port the OS assigned. Unix sockets and named pipes have no port and are skipped.
    /// </summary>
    internal (string Scheme, int Port)? Endpoint()
    {
        if (this.server.Features.Get<IServerAddressesFeature>() is not { } feature)
            return null;

        (string Scheme, int Port)? http = null;
        (string Scheme, int Port)? https = null;

        foreach (var address in feature.Addresses)
        {
            BindingAddress parsed;
            try
            {
                parsed = BindingAddress.Parse(address);
            }
            catch (FormatException)
            {
                continue;
            }

            if (parsed.IsUnixPipe || parsed.IsNamedPipe || parsed.Port <= 0)
                continue;

            if (string.Equals(parsed.Scheme, "http", StringComparison.OrdinalIgnoreCase))
                http ??= ("http", parsed.Port);
            else if (string.Equals(parsed.Scheme, "https", StringComparison.OrdinalIgnoreCase))
                https ??= ("https", parsed.Port);
        }

        return this.options.Scheme?.ToLowerInvariant() switch
        {
            "http" => http,
            "https" => https,
            _ => http ?? https
        };
    }
}
