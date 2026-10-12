using System.Diagnostics.Metrics;

namespace Shiny.AspNet.Acme;

/// <summary>
/// The meter ACME reports on. Subscribe to <see cref="MeterName"/> to alert on a certificate before it
/// lapses:
/// <code>
/// builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddMeter(AcmeTelemetry.MeterName));
/// </code>
/// <list type="bullet">
/// <item><c>shiny.acme.certificate.issuances</c> — counter of orders, tagged <c>acme.certificate.name</c>,
/// <c>acme.issuance.result</c> (<c>success</c> / <c>failure</c>) and, on failure, <c>error.type</c>.</item>
/// <item><c>shiny.acme.certificate.expiry</c> — observable gauge of days until the certificate being
/// served expires, per <c>acme.certificate.name</c>.</item>
/// </list>
/// </summary>
public static class AcmeTelemetry
{
    /// <summary>The meter's name: <c>Shiny.AspNet.Acme</c>.</summary>
    public const string MeterName = "Shiny.AspNet.Acme";

    internal static readonly Meter Meter = new(MeterName, typeof(AcmeTelemetry).Assembly.GetName().Version?.ToString(3));
}
