using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Shiny.AspNet.Acme.Internal;

/// <summary>
/// Runs the issuance and renewal loop for the lifetime of the host.
/// <para>
/// The loop starts on <see cref="IHostApplicationLifetime.ApplicationStarted"/>, not when hosted
/// services start: HTTP-01 and TLS-ALPN-01 are answered by this same app, so Kestrel has to be listening
/// before the CA is asked to validate anything. It stops when the host stops, waiting for an order in
/// flight to notice.
/// </para>
/// </summary>
abstract class AcmeRenewalService(
    IHostApplicationLifetime lifetime,
    IServer? server,
    AcmeHttpsBinding binding,
    AcmeChallengeTypes challenges,
    ILogger logger
) : BackgroundService
{
    protected abstract void Start();

    protected abstract Task StopRenewalAsync();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Started from the ApplicationStarted callback itself, synchronously: by the time the host's
        // StartAsync returns, the loop is running — and stopping it straight after is not a race.
        using (lifetime.ApplicationStarted.Register(() =>
        {
            if (stoppingToken.IsCancellationRequested)
                return;

            this.ReportEndpoints();
            this.Start();
        }))
        {
            try
            {
                await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await this.StopRenewalAsync().ConfigureAwait(false);
    }

    void ReportEndpoints()
    {
        // TestServer and other non-Kestrel servers never wire an HTTPS endpoint; say nothing there.
        if (server?.Features.Get<IServerAddressesFeature>() is not { } feature)
            return;

        var addresses = feature.Addresses.ToArray();
        if (addresses.Length == 0)
            return;

        if (binding.ServingEndpoints == 0)
            logger.LogWarning(
                "ACME is configured but no HTTPS endpoint serves its certificate. Add one without a certificate " +
                "(\"https://*:443\", or ListenAnyIP(443, l => l.UseHttps())) or call https.UseAcme(...) on it"
            );

        var port80 = addresses.Any(x =>
            x.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && BindingAddress.Parse(x).Port == 80);

        if (!port80 && challenges.HasFlag(AcmeChallengeTypes.Http01))
            logger.LogInformation(
                "HTTP-01 validation connects to port 80 and no cleartext endpoint is bound there — fine if a router or " +
                "proxy forwards port 80 to this app, otherwise validation will fail"
            );
    }
}

sealed class AcmeManagerRenewalService(
    AcmeCertificateManager manager,
    IHostApplicationLifetime lifetime,
    IServiceProvider services,
    ILogger<AcmeCertificateManager> logger
) : AcmeRenewalService(lifetime, (IServer?)services.GetService(typeof(IServer)), manager.Binding, manager.Options.Challenges, logger)
{
    protected override void Start() => manager.StartRenewal();

    protected override Task StopRenewalAsync() => manager.StopRenewalAsync();
}

sealed class AcmeRegistryRenewalService(
    AcmeCertificateRegistry registry,
    IHostApplicationLifetime lifetime,
    IServiceProvider services,
    ILogger<AcmeCertificateRegistry> logger
) : AcmeRenewalService(lifetime, (IServer?)services.GetService(typeof(IServer)), registry.Binding, registry.Options.Challenges, logger)
{
    protected override void Start() => registry.StartRenewal();

    protected override Task StopRenewalAsync() => registry.StopRenewalAsync();
}
