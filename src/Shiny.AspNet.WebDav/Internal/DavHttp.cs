using Microsoft.AspNetCore.Routing.Patterns;

namespace Shiny.AspNet.WebDav.Internal;

/// <summary>The few response and request shapes a DAV handler needs, over ASP.NET's own types.</summary>
static class DavHttp
{
    /// <summary>Sends a buffered body with a Content-Length.</summary>
    public static Task WriteBytesAsync(this HttpResponse response, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        response.ContentLength = body.Length;

        // HEAD gets the headers a GET would have, and no body.
        if (HttpMethods.IsHead(response.HttpContext.Request.Method))
            return response.StartAsync(cancellationToken);

        return response.Body.WriteAsync(body, cancellationToken).AsTask();
    }

    /// <summary>Sends text as UTF-8, with a Content-Length.</summary>
    public static Task WriteTextAsync(this HttpResponse response, string text, string contentType, CancellationToken cancellationToken)
    {
        response.ContentType = contentType;

        return response.WriteBytesAsync(System.Text.Encoding.UTF8.GetBytes(text), cancellationToken);
    }

    /// <summary>
    /// Whether the request carries a body. A declared length of zero is no body; a chunked one is
    /// a body even when it turns out to be empty, because there is no way to tell without reading it.
    /// </summary>
    public static bool HasBody(this HttpRequest request)
        => request.ContentLength is > 0 ||
           (request.ContentLength is null && request.Headers.TransferEncoding.Count > 0);

    /// <summary>
    /// The URL path a mount is reachable at: the request's <c>PathBase</c> plus every literal segment
    /// of the matched route up to its catch-all - so a mount inside a route group, or behind
    /// <c>UsePathBase</c>, reports hrefs that include the outer prefix. A client resolves every member
    /// URL against them, so one that is short by a segment breaks the whole mount.
    /// </summary>
    public static string MountPath(HttpContext context)
    {
        var builder = new System.Text.StringBuilder(context.Request.PathBase.Value?.TrimEnd('/') ?? string.Empty);

        if (context.GetEndpoint() is RouteEndpoint { RoutePattern: { } pattern })
        {
            foreach (var segment in pattern.PathSegments)
            {
                if (!segment.IsSimple || segment.Parts[0] is not RoutePatternLiteralPart literal)
                    break;

                builder.Append('/').Append(literal.Content);
            }
        }

        return builder.ToString();
    }
}
