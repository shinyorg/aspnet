using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Shiny.AspNet.Ssh;

/// <summary>Registering SSH tunnels with an ASP.NET Core app.</summary>
public static class SshServiceCollectionExtensions
{
    /// <summary>
    /// Registers an SSH tunnel that forwards to this app's Kestrel listener.
    /// <code>
    /// builder.Services.AddSshTunnel(o =>
    /// {
    ///     o.Host = "tunnel.example.com";
    ///     o.Username = "tunnel";
    ///     o.PrivateKeyPath = keyPath;
    ///     o.RemoteBindAddress = "0.0.0.0";
    ///     o.RemotePort = 8080;
    ///     o.HostKeyFingerprints.Add("SHA256:…");
    /// });
    /// </code>
    /// </summary>
    /// <param name="autoStart">
    /// Start the tunnel once the app is listening. Off, the tunnel is registered but idle — resolve
    /// <see cref="SshTunnel"/> and call <see cref="SshTunnel.StartAsync"/> when it is wanted.
    /// </param>
    public static IServiceCollection AddSshTunnel(
        this IServiceCollection services,
        Action<SshTunnelOptions> configureOptions,
        bool autoStart = true
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureOptions);

        services.TryAddSingleton(sp =>
        {
            var options = new SshTunnelOptions();
            configureOptions(options);

            return new SshTunnel(sp.GetRequiredService<IServer>(), options, sp.GetService<ILoggerFactory>());
        });

        if (autoStart)
            services.AddHostedService<SshTunnelHostedService>();

        return services;
    }

    /// <summary>
    /// Registers a <see cref="QuickTunnel"/> — a free public HTTPS address from a hosted SSH tunnel.
    /// <code>
    /// builder.Services.AddQuickTunnel(autoStart: builder.Environment.IsDevelopment());
    /// </code>
    /// </summary>
    /// <param name="autoStart">
    /// Start once the app is listening. Off by default: putting an app on the public internet through
    /// someone else's server should be a decision, not a side effect of adding a package. Leave it off
    /// and call <see cref="QuickTunnel.StartAsync"/> when it is wanted.
    /// </param>
    public static IServiceCollection AddQuickTunnel(
        this IServiceCollection services,
        QuickTunnelHost host = QuickTunnelHost.Pinggy,
        string? subdomain = null,
        Action<SshTunnelOptions>? configure = null,
        bool autoStart = false
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(sp => QuickTunnel.For(
            sp.GetRequiredService<IServer>(),
            host,
            subdomain,
            configure,
            sp.GetService<ILoggerFactory>()
        ));

        if (autoStart)
            services.AddHostedService<QuickTunnelHostedService>();

        return services;
    }
}

/// <summary>
/// Starts a tunnel after Kestrel is listening and closes it on shutdown.
/// <para>
/// A lifecycle service rather than a plain hosted service: with <c>WebApplication</c>, services
/// registered on <c>builder.Services</c> start <em>before</em> the web server, so a plain
/// <c>StartAsync</c> would find no address to forward to. <see cref="IHostedLifecycleService.StartedAsync"/>
/// runs once Kestrel has bound, and <see cref="IHostedLifecycleService.StoppingAsync"/> closes the
/// public address before Kestrel stops answering behind it.
/// </para>
/// </summary>
abstract class TunnelLifecycleService(Func<CancellationToken, Task> start, Func<CancellationToken, Task> stop) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => start(cancellationToken);

    public Task StoppingAsync(CancellationToken cancellationToken) => stop(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

sealed class SshTunnelHostedService(SshTunnel tunnel)
    : TunnelLifecycleService(tunnel.StartAsync, tunnel.StopAsync);

sealed class QuickTunnelHostedService(QuickTunnel tunnel)
    : TunnelLifecycleService(tunnel.StartAsync, tunnel.StopAsync);
