using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shiny.AspNet.Acme.Internal;

namespace Shiny.AspNet.Acme;

/// <summary>Registering ACME certificates with an ASP.NET Core app.</summary>
public static class AcmeServiceCollectionExtensions
{
    /// <summary>
    /// Obtains and renews a certificate from an ACME CA and serves it on every Kestrel HTTPS endpoint
    /// that has no certificate of its own.
    /// <code>
    /// builder.WebHost.UseUrls("http://*:80", "https://*:443");   // HTTP-01 is always port 80
    ///
    /// builder.Services.AddAcme(o =>
    /// {
    ///     o.Domains = ["example.com"];
    ///     o.Email = "ops@example.com";
    ///     o.AcceptTermsOfService = true;
    ///     o.DirectoryUrl = AcmeDirectories.LetsEncryptStaging;   // until it works; then drop this line
    /// });
    /// </code>
    /// <para>
    /// This registers an <see cref="AcmeCertificateManager"/> singleton and wires it up with nothing
    /// else to call: Kestrel's HTTPS defaults select its certificate (so <c>UseHttps()</c> without a
    /// certificate, an <c>https://</c> URL, or a configuration endpoint with no certificate all serve
    /// it), a startup filter answers HTTP-01 at <c>/.well-known/acme-challenge/</c> ahead of the app's
    /// own middleware, and a hosted service — once the app has started — loads the stored certificate or
    /// orders one, and renews at two thirds of the lifetime (or when the CA's renewal information says),
    /// swapping each new certificate in without a restart.
    /// </para>
    /// <para>
    /// Kestrel keeps one HTTPS-defaults callback: calling <c>ConfigureHttpsDefaults</c> yourself replaces
    /// the ACME wiring — call <c>https.UseAcme(manager)</c> inside yours. A second
    /// <c>AddAcme</c> call adds to the same options rather than replacing them.
    /// </para>
    /// </summary>
    public static IServiceCollection AddAcme(this IServiceCollection services, Action<AcmeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = services.AddOptions<AcmeOptions>();
        if (configure is not null)
            options.Configure(configure);

        if (!services.Any(x => x.ServiceType == typeof(AcmeCertificateManager)))
        {
            services.AddSingleton(sp => new AcmeCertificateManager(
                sp.GetRequiredService<IOptions<AcmeOptions>>().Value,
                sp.GetService<ILoggerFactory>()
            ));
            services.AddHostedService<AcmeManagerRenewalService>();
            AddWiring(services);
        }

        return services;
    }

    /// <summary>
    /// Registers an <see cref="AcmeCertificateRegistry"/> — many certificates, chosen per connection by
    /// SNI, with entries added at runtime — and wires it the same way as <see cref="AddAcme"/>: one
    /// HTTP-01 responder for every entry, every Kestrel HTTPS endpoint without a certificate of its own,
    /// and renewal while the app runs.
    /// <code>
    /// builder.Services.AddAcmeRegistry(o =>
    /// {
    ///     o.Email = "ops@example.com";
    ///     o.AcceptTermsOfService = true;
    /// });
    ///
    /// var app = builder.Build();
    /// var acme = app.Services.GetRequiredService&lt;AcmeCertificateRegistry&gt;();
    /// acme.Set("site", ["example.com", "www.example.com"]);
    /// await acme.IssueAsync("site");                 // nothing is ever ordered implicitly
    /// </code>
    /// <para>
    /// Use this or <see cref="AddAcme"/>, not both: when both are registered, the endpoints Kestrel
    /// wires by default serve the single manager's certificate (wire others with <c>https.UseAcme(registry)</c>).
    /// A second call adds to the same options rather than replacing them.
    /// </para>
    /// </summary>
    public static IServiceCollection AddAcmeRegistry(this IServiceCollection services, Action<AcmeRegistryOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = services.AddOptions<AcmeRegistryOptions>();
        if (configure is not null)
            options.Configure(configure);

        if (!services.Any(x => x.ServiceType == typeof(AcmeCertificateRegistry)))
        {
            services.AddSingleton(sp => new AcmeCertificateRegistry(
                sp.GetRequiredService<IOptions<AcmeRegistryOptions>>().Value,
                sp.GetService<ILoggerFactory>()
            ));
            services.AddHostedService<AcmeRegistryRenewalService>();
            AddWiring(services);
        }

        return services;
    }

    static void AddWiring(IServiceCollection services)
    {
        // First in line, not appended: Kestrel applies its HTTPS defaults the moment an endpoint calls
        // UseHttps — which, for endpoints declared in code, is inside the app's own ConfigureKestrel
        // callback. Registered after it, the defaults would be set too late for those endpoints.
        if (!services.Any(x => x.ImplementationType == typeof(AcmeKestrelSetup)))
            services.Insert(0, ServiceDescriptor.Singleton<IConfigureOptions<KestrelServerOptions>, AcmeKestrelSetup>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IStartupFilter, AcmeStartupFilter>());
    }
}
