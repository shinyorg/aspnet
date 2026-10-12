using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace Shiny.AspNet.JsonPatch;

/// <summary>
/// Reading an <c>application/json-patch+json</c> body from an ASP.NET Core <see cref="HttpRequest"/> — for a
/// <see cref="RequestDelegate"/> handler, or anywhere a <see cref="JsonPatchDocument"/> parameter is not an
/// option.
/// </summary>
public static class JsonPatchRequestExtensions
{
    /// <summary>True when the request declares an <c>application/json-patch+json</c> body (parameters ignored).</summary>
    public static bool IsJsonPatch(this HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType)
            && contentType.MediaType.Equals(JsonPatchDocument.MediaType, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads and parses the body as a JSON Patch.
    /// <para>
    /// Strict about the <c>Content-Type</c>: anything but <c>application/json-patch+json</c> throws
    /// <see cref="JsonPatchErrorKind.UnsupportedMediaType"/> (415). A JSON array sent as plain
    /// <c>application/json</c> is not assumed to be a JSON Patch, because on a resource that also accepts
    /// merge patch the two readings of the same body would write different things.
    /// </para>
    /// <code>
    /// app.MapPatch("/notes/{id}", async (HttpContext ctx) =>
    /// {
    ///     try
    ///     {
    ///         var patch = await ctx.Request.ReadJsonPatchAsync(ctx.RequestAborted);
    ///         store.Save(patch.ApplyTo(store.Get(id), AppJson.Default.Note));
    ///         return Results.NoContent();
    ///     }
    ///     catch (JsonPatchException ex)
    ///     {
    ///         return ex.ToResult();   // 400 / 409 / 415 / 422 problem naming the failed operation
    ///     }
    /// });
    /// </code>
    /// </summary>
    /// <exception cref="JsonPatchException">Wrong content type (415), no body or an invalid patch (400).</exception>
    public static async Task<JsonPatchDocument> ReadJsonPatchAsync(
        this HttpRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.IsJsonPatch())
            throw new JsonPatchException(
                JsonPatchErrorKind.UnsupportedMediaType,
                $"A JSON Patch body must be sent as '{JsonPatchDocument.MediaType}' "
                    + $"(got '{(string.IsNullOrEmpty(request.ContentType) ? "no Content-Type" : request.ContentType)}')."
            );

        // A Content-Type says nothing about whether bytes follow. The server knows when the framing rules out a
        // body; an empty chunked body still reaches the parser, which reports it as malformed all the same.
        var canHaveBody = request.HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody;
        if (request.ContentLength == 0 || canHaveBody == false)
            throw new JsonPatchException(JsonPatchErrorKind.Malformed, "A JSON Patch body is required.");

        return await JsonPatchDocument.ParseAsync(request.Body, cancellationToken).ConfigureAwait(false);
    }
}
