using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Shiny.AspNet.Tunnels;

/// <summary>Registering a tunnel agent with an ASP.NET Core app.</summary>
public static class TunnelAgentServiceExtensions
{
    /// <summary>
    /// Runs a Cloudflare quick tunnel for the lifetime of the app.
    /// <code>
    /// builder.Services.AddCloudflareTunnel();
    /// </code>
    /// </summary>
    public static IServiceCollection AddCloudflareTunnel(
        this IServiceCollection services,
        Action<CloudflareTunnelOptions>? configure = null
    )
        => services.AddTunnelAgent(sp =>
        {
            var options = new CloudflareTunnelOptions();
            configure?.Invoke(options);

            return new CloudflareTunnel(options, sp.GetService<ILoggerFactory>()?.CreateLogger<CloudflareTunnel>());
        });

    /// <summary>Runs an ngrok tunnel for the lifetime of the app.</summary>
    public static IServiceCollection AddNgrokTunnel(
        this IServiceCollection services,
        Action<NgrokTunnelOptions>? configure = null
    )
        => services.AddTunnelAgent(sp =>
        {
            var options = new NgrokTunnelOptions();
            configure?.Invoke(options);

            return new NgrokTunnel(options, sp.GetService<ILoggerFactory>()?.CreateLogger<NgrokTunnel>());
        });

    /// <summary>Runs Tailscale Funnel (or tailnet-only serve) for the lifetime of the app.</summary>
    public static IServiceCollection AddTailscaleFunnel(
        this IServiceCollection services,
        Action<TailscaleFunnelOptions>? configure = null
    )
        => services.AddTunnelAgent(sp =>
        {
            var options = new TailscaleFunnelOptions();
            configure?.Invoke(options);

            return new TailscaleFunnel(options, sp.GetService<ILoggerFactory>()?.CreateLogger<TailscaleFunnel>());
        });

    /// <summary>
    /// Registers any agent, and the hosted service that starts it once Kestrel is listening. Resolve
    /// <see cref="ITunnelAgent"/> to read <see cref="ITunnelAgent.PublicUrl"/>.
    /// </summary>
    public static IServiceCollection AddTunnelAgent(this IServiceCollection services, Func<IServiceProvider, ITunnelAgent> factory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(factory);

        services.TryAddSingleton(factory);
        services.AddHostedService<TunnelAgentHostedService>();

        return services;
    }
}

/// <summary>
/// Starts the agent after Kestrel is listening and kills it on shutdown.
/// <para>
/// A lifecycle service rather than a plain hosted service, and for a reason that is easy to get
/// wrong: with <c>WebApplication</c>, services registered on <c>builder.Services</c> start
/// <em>before</em> the web server, so a plain <c>StartAsync</c> would find no address to forward to.
/// <see cref="IHostedLifecycleService.StartedAsync"/> runs once every hosted service — Kestrel's
/// included — has started, and <see cref="IHostedLifecycleService.StoppingAsync"/> takes the public
/// address down before Kestrel stops answering behind it.
/// </para>
/// </summary>
sealed class TunnelAgentHostedService(
    ITunnelAgent agent,
    IServer server,
    ILogger<TunnelAgentHostedService> logger
) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StartedAsync(CancellationToken cancellationToken)
    {
        var url = await agent.StartAsync(server, cancellationToken).ConfigureAwait(false);

        logger.LogInformation("{Agent} is publishing this app at {Public}", agent.Name, url);
    }

    public Task StoppingAsync(CancellationToken cancellationToken) => agent.StopAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
