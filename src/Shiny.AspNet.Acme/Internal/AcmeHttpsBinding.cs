using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace Shiny.AspNet.Acme.Internal;

/// <summary>
/// Connects a certificate source — a manager's one certificate, or a registry's many by SNI — to Kestrel
/// HTTPS endpoints, through three hooks on <see cref="HttpsConnectionAdapterOptions"/>:
/// <list type="number">
/// <item><see cref="HttpsConnectionAdapterOptions.TlsClientHelloBytesCallback"/>: Kestrel sniffs the
/// ClientHello without consuming it; the SNI name and offered ALPN protocols are parsed and parked on
/// the connection.</item>
/// <item><see cref="HttpsConnectionAdapterOptions.OnAuthenticate"/>: runs just before the handshake
/// with those in hand. A TLS-ALPN-01 probe for a pending challenge gets the challenge certificate and
/// <c>acme-tls/1</c> as the only protocol; anything else gets the current certificate as a
/// <see cref="SslStreamCertificateContext"/> — the intermediate included, which a bare certificate from
/// a selection callback would not carry.</item>
/// <item><see cref="HttpsConnectionAdapterOptions.ServerCertificateSelector"/>: what Kestrel requires
/// to accept an endpoint with no certificate, and the fallback when no hello was parsed.</item>
/// </list>
/// Every hook reads the current certificate per handshake, so a renewal is live for the next connection.
/// </summary>
sealed class AcmeHttpsBinding(
    Func<string?, SslStreamCertificateContext?> select,
    AcmeChallengeResponder challenges,
    Func<bool> answerTlsAlpn
)
{
    readonly List<(HttpsConnectionAdapterOptions Https, object Selector, bool Yields)> endpoints = [];

    /// <summary>
    /// Wires <paramref name="https"/>. With <paramref name="yieldToOwnCertificate"/> — the
    /// <c>ConfigureHttpsDefaults</c> path, which runs before the endpoint's own configuration — an
    /// endpoint that ends up with a <see cref="HttpsConnectionAdapterOptions.ServerCertificate"/> of its
    /// own keeps serving it. Without it — an explicit <c>UseAcme</c> — ACME's certificate comes first and
    /// the endpoint's own is the fallback for a name ACME has nothing for yet. Either way, an endpoint
    /// later given a selector of its own is left entirely alone.
    /// </summary>
    public void Apply(HttpsConnectionAdapterOptions https, bool yieldToOwnCertificate)
    {
        Func<ConnectionContext?, string?, X509Certificate2?> selector = (_, name) =>
            yieldToOwnCertificate && https.ServerCertificate is { } own
                ? own
                : select(name)?.TargetCertificate ?? https.ServerCertificate;

        https.ServerCertificateSelector = selector;

        var previousHello = https.TlsClientHelloBytesCallback;
        https.TlsClientHelloBytesCallback = (connection, bytes) =>
        {
            previousHello?.Invoke(connection, bytes);

            if (TlsClientHelloParser.Parse(bytes) is { } hello)
                connection.Features.Set(hello);
        };

        var previousAuthenticate = https.OnAuthenticate;
        (X509Certificate2 Certificate, SslStreamCertificateContext Context)? ownContext = null;

        // Kestrel ignores ServerCertificate when a selector is set, so the endpoint's own certificate
        // is served from here, built once with the chain the endpoint configured — what Kestrel itself
        // would have done.
        SslStreamCertificateContext? Own()
        {
            if (https.ServerCertificate is not { } own)
                return null;

            if (ownContext is not { } cached || !ReferenceEquals(cached.Certificate, own))
                ownContext = cached = (own, SslStreamCertificateContext.Create(own, https.ServerCertificateChain));

            return cached.Context;
        }

        https.OnAuthenticate = (connection, ssl) =>
        {
            previousAuthenticate?.Invoke(connection, ssl);

            // Someone configured this endpoint's certificate selection after us; theirs stands.
            if (!ReferenceEquals(https.ServerCertificateSelector, selector))
                return;

            if (yieldToOwnCertificate && Own() is { } own)
            {
                Use(ssl, own);
                return;
            }

            // No hello parsed (not TLS we understand): the selector answers, by SNI, as usual.
            if (connection.Features.Get<TlsClientHello>() is not { } hello)
                return;

            if (answerTlsAlpn() && challenges.Respond(hello.ServerName, hello.ApplicationProtocols) is { } challenge)
            {
                // RFC 8737 §3: the challenge certificate, and acme-tls/1 as the negotiated protocol.
                // The CA closes the connection after the handshake; it never carries HTTP.
                Use(ssl, challenge);
                ssl.ApplicationProtocols = [AcmeChallengeResponder.AcmeTls1];
                return;
            }

            if ((select(hello.ServerName) ?? Own()) is { } context)
                Use(ssl, context);
        };

        lock (this.endpoints)
            this.endpoints.Add((https, selector, yieldToOwnCertificate));
    }

    /// <summary>
    /// How many wired endpoints still serve ACME certificates — not displaced by a certificate or
    /// selector of their own. Read once Kestrel has bound, for the startup warning.
    /// </summary>
    public int ServingEndpoints
    {
        get
        {
            lock (this.endpoints)
                return this.endpoints.Count(x =>
                    ReferenceEquals(x.Https.ServerCertificateSelector, x.Selector)
                    && !(x.Yields && x.Https.ServerCertificate is not null));
        }
    }

    static void Use(SslServerAuthenticationOptions ssl, SslStreamCertificateContext context)
    {
        // SslStream takes exactly one of these.
        ssl.ServerCertificateSelectionCallback = null;
        ssl.ServerCertificate = null;
        ssl.ServerCertificateContext = context;
    }
}
