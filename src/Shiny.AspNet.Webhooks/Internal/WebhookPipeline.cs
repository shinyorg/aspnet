using System.Buffers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Shiny.AspNet.Webhooks.Internal;

/// <summary>
/// Buffer, verify, deduplicate, invoke — the one sequence both <c>MapWebhook</c> and the middleware run.
/// </summary>
static class WebhookPipeline
{
    const string RejectedDetail = "The webhook signature could not be verified.";

    public static async Task RunAsync(
        HttpContext context,
        IWebhookVerifier verifier,
        WebhookOptions options,
        ILogger logger,
        RequestDelegate next
    )
    {
        // Already verified — the middleware ran ahead of a MapWebhook route. Verifying twice would be
        // harmless but recording the delivery id twice would make it a duplicate of itself.
        if (context.TryGetWebhook(out _))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var request = context.Request;
        byte[]? body;

        try
        {
            body = await ReadBodyAsync(request, options.MaxBodySize, context.RequestAborted).ConfigureAwait(false);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            // Kestrel's own MaxRequestBodySize was hit before ours was.
            body = null;
        }

        if (body is null)
        {
            logger.LogWarning(
                "Rejected {Verifier} webhook {Method} {Path}: body exceeds {Limit} bytes",
                verifier.Name,
                request.Method,
                request.Path,
                options.MaxBodySize
            );

            await WriteProblemAsync(context, StatusCodes.Status413PayloadTooLarge, $"The webhook body exceeds the {options.MaxBodySize} byte limit.")
                .ConfigureAwait(false);
            return;
        }

        // Whatever runs next reads the same bytes that were verified, through the ordinary API —
        // Request.Body, and Request.BodyReader, which ASP.NET re-wraps around a replaced Body.
        request.Body = new MemoryStream(body, writable: false);

        var result = verifier.Verify(request.Headers, body, options.TimeProvider.GetUtcNow());

        if (!result.Succeeded)
        {
            // The reason goes to the log and nowhere else. A forger learns only that it failed.
            logger.LogWarning(
                "Rejected {Verifier} webhook {Method} {Path} from {Address}: {Reason}",
                verifier.Name,
                request.Method,
                request.Path,
                context.Connection.RemoteIpAddress?.ToString() ?? "an unknown address",
                result.FailureReason
            );

            if (options.OnRejected is { } onRejected)
            {
                await onRejected(context, result).ConfigureAwait(false);
                return;
            }

            await WriteProblemAsync(context, StatusCodes.Status401Unauthorized, RejectedDetail).ConfigureAwait(false);
            return;
        }

        var webhook = new WebhookContext(context, verifier.Name, body, result);
        context.SetWebhook(webhook);

        var store = options.DeliveryStore ?? context.RequestServices?.GetService<IWebhookDeliveryStore>();
        string? deliveryKey = null;

        if (store is not null && result.DeliveryId is { Length: > 0 } deliveryId)
        {
            // Keyed by sender as well as id: two senders' ids are two namespaces, and a collision
            // between them would silently drop a real delivery.
            deliveryKey = verifier.Name + ":" + deliveryId;

            if (!await store.TryAddAsync(deliveryKey, context.RequestAborted).ConfigureAwait(false))
            {
                logger.LogInformation(
                    "Acknowledged duplicate {Verifier} webhook delivery {DeliveryId} without handling it",
                    verifier.Name,
                    deliveryId
                );

                context.Response.StatusCode = options.DuplicateStatusCode;
                context.Response.ContentLength = 0;
                return;
            }
        }

        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch
        {
            await ForgetAsync(store, deliveryKey).ConfigureAwait(false);
            throw;
        }

        // A 5xx tells the sender to retry. Keeping the id would turn that retry into a "duplicate"
        // of a delivery that never succeeded, and the event would be lost.
        if (context.Response.StatusCode >= 500)
            await ForgetAsync(store, deliveryKey).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the whole body, or returns null as soon as it is known to be over the limit — from a
    /// declared <c>Content-Length</c> before reading anything, or mid-read for a chunked body — so no
    /// more than <paramref name="limit"/> bytes are ever held.
    /// </summary>
    static async Task<byte[]?> ReadBodyAsync(HttpRequest request, int limit, CancellationToken cancellationToken)
    {
        if (request.ContentLength is { } declared)
        {
            if (declared > limit)
                return null;

            if (declared == 0)
                return [];
        }

        using var held = new MemoryStream(request.ContentLength is { } length ? (int)length : 0);
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);

        try
        {
            int read;
            while ((read = await request.Body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (held.Length + read > limit)
                    return null;

                held.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return held.ToArray();
    }

    static ValueTask ForgetAsync(IWebhookDeliveryStore? store, string? deliveryKey)
        => deliveryKey is not null && store is not null
            ? store.RemoveAsync(deliveryKey, CancellationToken.None)
            : ValueTask.CompletedTask;

    /// <summary>
    /// A problem response through <see cref="Results.Problem(string?, string?, int?, string?, string?, IDictionary{string, object?}?)"/>,
    /// which hands it to a registered <c>IProblemDetailsService</c> (and so to any
    /// <c>AddProblemDetails</c> customisation) before falling back to writing it itself.
    /// </summary>
    static Task WriteProblemAsync(HttpContext context, int status, string detail)
        => Results.Problem(detail: detail, statusCode: status).ExecuteAsync(context);
}
