using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Shiny.AspNet.Acme.Internal;

/// <summary>
/// Makes ACME the certificate source of every Kestrel HTTPS endpoint that does not bring its own,
/// through <see cref="KestrelServerOptions.ConfigureHttpsDefaults"/> — which every HTTPS endpoint,
/// code-configured, URL-configured or from <c>appsettings.json</c>, passes through before its own
/// configuration runs.
/// </summary>
sealed class AcmeKestrelSetup(IServiceProvider services) : IConfigureOptions<KestrelServerOptions>
{
    public void Configure(KestrelServerOptions options)
    {
        var binding = services.GetService<AcmeCertificateManager>()?.Binding
            ?? services.GetService<AcmeCertificateRegistry>()?.Binding;

        if (binding is not null)
            options.ConfigureHttpsDefaults(https => binding.Apply(https, yieldToOwnCertificate: true));
    }
}
