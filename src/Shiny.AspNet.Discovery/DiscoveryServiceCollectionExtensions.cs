using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shiny.Net.Discovery;

namespace Shiny.AspNet.Discovery;

/// <summary>Wiring mDNS discovery to an ASP.NET Core app.</summary>
public static class DiscoveryServiceCollectionExtensions
{
    /// <summary>
    /// Advertises the app on the local link for as long as the host runs.
    /// <code>
    /// builder.WebHost.UseUrls("http://0.0.0.0:0");   // any port: the advertisement carries the one bound
    /// builder.Services.AddServerAdvertisement(o =>
    /// {
    ///     o.ServiceType = "_myapp._tcp";
    ///     o.TxtRecords["role"] = "controller";
    /// });
    /// </code>
    /// <para>
    /// The record is published once the application has started — the port comes from the server's
    /// bound addresses, so a dynamic one is right — and withdrawn with a goodbye packet when it begins
    /// stopping. Bind to an address peers can reach (<c>0.0.0.0</c> or a LAN address, not
    /// <c>localhost</c>), or they will find the app and fail to connect.
    /// </para>
    /// <para>
    /// The responder is <see cref="IMdnsManager"/> from Shiny.Net.Discovery, registered here unless one
    /// already is. On a plain <c>net10.0</c> app that is a managed responder binding UDP 5353 — the
    /// firewall has to allow it.
    /// </para>
    /// <para>A second call adds to the same options rather than replacing them.</para>
    /// </summary>
    public static IServiceCollection AddServerAdvertisement(
        this IServiceCollection services,
        Action<ServerAdvertisementOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        AddResponder(services);

        var options = services.AddOptions<ServerAdvertisementOptions>();
        if (configure is not null)
            options.Configure(configure);

        services.TryAddSingleton<IServerAdvertiser>(sp => new ServerAdvertiser(
            sp.GetRequiredService<IMdnsManager>(),
            sp.GetRequiredService<IServer>(),
            sp.GetRequiredService<IHostApplicationLifetime>(),
            sp.GetRequiredService<IOptions<ServerAdvertisementOptions>>().Value,
            sp.GetService<ILoggerFactory>()?.CreateLogger<ServerAdvertiser>()
        ));

        services.AddHostedService<ServerAdvertisementHostedService>();

        return services;
    }

    /// <summary>
    /// Registers <see cref="IServerLocator"/> for finding servers other machines advertise — another
    /// instance of this app, or anything on the link. Needs no server of its own: a worker or console
    /// app can use it too.
    /// </summary>
    public static IServiceCollection AddServerLocator(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        AddResponder(services);
        services.TryAddSingleton<IServerLocator>(sp => new ServerLocator(sp.GetRequiredService<IMdnsManager>()));

        return services;
    }

    static void AddResponder(IServiceCollection services)
    {
        // One responder per app: the advertiser and the locator must share it, or the locator finds
        // this app's own advertisement. And one registered by the app — a test double, a configured
        // instance — stands.
        if (!services.Any(x => x.ServiceType == typeof(IMdnsManager)))
            services.AddMdns();
    }
}

/// <summary>
/// Starts the advertiser with the host. The advertiser itself waits for
/// <see cref="IHostApplicationLifetime.ApplicationStarted"/> to publish — hosted services start before
/// the server is listening — and withdraws on <see cref="IHostApplicationLifetime.ApplicationStopping"/>.
/// </summary>
sealed class ServerAdvertisementHostedService(IServerAdvertiser advertiser) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => advertiser.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => advertiser.StopAsync(cancellationToken);
}

/// <summary>Advertising an app without registering the advertiser in its container.</summary>
public static class ServerAdvertisementHostExtensions
{
    /// <summary>
    /// Advertises a host's server through <paramref name="mdns"/>. Call it before or after
    /// <c>app.StartAsync()</c>: it publishes once the application has started, and withdraws when it
    /// stops or the returned advertiser is disposed.
    /// <code>
    /// await app.StartAsync();
    /// await using var advertisement = await app.AdvertiseAsync(mdns, o => o.ServiceType = "_myapp._tcp");
    /// </code>
    /// </summary>
    public static async Task<IServerAdvertiser> AdvertiseAsync(
        this IHost host,
        IMdnsManager mdns,
        Action<ServerAdvertisementOptions>? configure = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(mdns);

        var options = new ServerAdvertisementOptions();
        configure?.Invoke(options);

        var advertiser = new ServerAdvertiser(
            mdns,
            host.Services.GetRequiredService<IServer>(),
            host.Services.GetService<IHostApplicationLifetime>(),
            options,
            host.Services.GetService<ILoggerFactory>()?.CreateLogger<ServerAdvertiser>()
        );
        await advertiser.StartAsync(cancellationToken).ConfigureAwait(false);

        return advertiser;
    }
}
