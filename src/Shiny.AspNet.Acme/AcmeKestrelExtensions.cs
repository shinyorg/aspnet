using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace Shiny.AspNet.Acme;

/// <summary>Wiring an <see cref="AcmeCertificateManager"/> or <see cref="AcmeCertificateRegistry"/> into one Kestrel HTTPS endpoint.</summary>
public static class AcmeKestrelExtensions
{
    /// <summary>
    /// Serves the ACME certificate on this endpoint, and answers TLS-ALPN-01 on it when that challenge
    /// is enabled. A <see cref="HttpsConnectionAdapterOptions.ServerCertificate"/> set on the endpoint
    /// becomes the fallback, served while ACME has no certificate yet. <c>AddAcme</c> wires every HTTPS
    /// endpoint without a certificate for you; call this inside your own <c>ConfigureHttpsDefaults</c>
    /// (which replaces ACME's), or on an endpoint that has a certificate of its own.
    /// <code>
    /// builder.WebHost.ConfigureKestrel((context, kestrel) =>
    ///     kestrel.ListenAnyIP(443, listen => listen.UseHttps(https =>
    ///         https.UseAcme(kestrel.ApplicationServices.GetRequiredService&lt;AcmeCertificateManager&gt;()))));
    /// </code>
    /// <para>
    /// This sets <see cref="HttpsConnectionAdapterOptions.OnAuthenticate"/>, which Kestrel refuses on an
    /// endpoint that also speaks HTTP/3 — see the package documentation for HTTP/3.
    /// </para>
    /// </summary>
    public static HttpsConnectionAdapterOptions UseAcme(this HttpsConnectionAdapterOptions https, AcmeCertificateManager manager)
    {
        ArgumentNullException.ThrowIfNull(https);
        ArgumentNullException.ThrowIfNull(manager);

        manager.Binding.Apply(https, yieldToOwnCertificate: false);
        return https;
    }

    /// <summary>
    /// Serves the registry's certificates on this endpoint, chosen by SNI, and answers TLS-ALPN-01 on it
    /// for every entry when that challenge is enabled.
    /// </summary>
    public static HttpsConnectionAdapterOptions UseAcme(this HttpsConnectionAdapterOptions https, AcmeCertificateRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(https);
        ArgumentNullException.ThrowIfNull(registry);

        registry.Binding.Apply(https, yieldToOwnCertificate: false);
        return https;
    }
}
