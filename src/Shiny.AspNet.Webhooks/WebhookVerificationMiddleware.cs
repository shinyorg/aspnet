using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shiny.AspNet.Webhooks.Internal;

namespace Shiny.AspNet.Webhooks;

/// <summary>
/// Verifies deliveries to endpoints that carry <see cref="IRequireWebhookSignatureMetadata"/> — from
/// <c>[RequireWebhookSignature]</c> on a controller, an action or a minimal-API handler, or
/// <c>.RequireWebhookSignature(…)</c> on a mapped route or group. Installed with
/// <c>app.UseWebhookVerification()</c>; <c>MapWebhook</c> does the same work inline and does not
/// need it.
/// <para>
/// It reads <c>HttpContext.GetEndpoint()</c>, so it must run after routing has picked the
/// endpoint — which, with <c>WebApplication</c>, is anywhere in the pipeline unless you call
/// <c>UseRouting()</c> yourself, in which case put it after that. Endpoints without the metadata pass
/// straight through, so installing it costs nothing elsewhere.
/// </para>
/// </summary>
public sealed class WebhookVerificationMiddleware(
    RequestDelegate next,
    IOptions<WebhookOptions> options,
    ILogger<WebhookVerificationMiddleware> logger
)
{
    readonly RequestDelegate next = next ?? throw new ArgumentNullException(nameof(next));
    readonly WebhookOptions options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
    readonly ILogger logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var endpoint = context.GetEndpoint();
        var metadata = endpoint?.Metadata.GetMetadata<IRequireWebhookSignatureMetadata>();
        if (metadata is null)
            return this.next(context);

        // A MapWebhook route carries the settings it was mapped with; anything else uses the shared ones.
        var effective = (metadata as WebhookMetadata)?.Options ?? this.options;

        var verifier = metadata.Verifier
            ?? (metadata.VerifierName is { } name
                ? effective.GetVerifier(name)
                : throw new InvalidOperationException(
                    $"Endpoint '{endpoint!.DisplayName}' requires a webhook signature but names no verifier."
                ));

        return WebhookPipeline.RunAsync(context, verifier, effective, this.logger, this.next);
    }
}
