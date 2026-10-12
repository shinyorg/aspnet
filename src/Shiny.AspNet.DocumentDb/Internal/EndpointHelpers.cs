using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Text;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;

namespace Shiny.AspNet.DocumentDb.Internal;

/// <summary>Problem responses the handlers return: status first, because that is what each one is about.</summary>
static class Problems
{
    /// <summary>
    /// An RFC 9457 problem. <see cref="TypedResults.Problem(string?, string?, int?, string?, string?, IDictionary{string, object?}?)"/>
    /// writes through <c>IProblemDetailsService</c> when the app registered one, so these are shaped like every
    /// other problem the app returns.
    /// </summary>
    public static IResult Create(int status, string detail) => TypedResults.Problem(detail: detail, statusCode: status);
}

/// <summary>
/// OpenAPI metadata for routes mapped as plain <see cref="RequestDelegate"/>s.
/// <para>
/// ASP.NET's <c>Produces</c>/<c>Accepts</c>/<c>ProducesProblem</c> conventions hang off
/// <see cref="RouteHandlerBuilder"/>, which only an inferred (reflection- or generator-bound) handler gets; a
/// <see cref="RequestDelegate"/> route has a plain <see cref="IEndpointConventionBuilder"/>. These add the same
/// metadata objects those conventions add, so ApiExplorer and Microsoft.AspNetCore.OpenApi read them unchanged.
/// </para>
/// </summary>
static class EndpointDescription
{
    static readonly string[] Json = ["application/json"];
    static readonly string[] ProblemJson = ["application/problem+json"];

    /// <summary>
    /// The operation id and tag OpenAPI sees. The id is the endpoint name, so it also works with
    /// <c>LinkGenerator</c>; it has to be unique, which is why it is built from the type or collection name.
    /// </summary>
    public static IEndpointConventionBuilder Describe(this IEndpointConventionBuilder builder, string operationId, string tag)
        => builder.WithName(operationId).WithTags(tag);

    public static IEndpointConventionBuilder Produces<TResponse>(this IEndpointConventionBuilder builder, int statusCode)
        => builder.WithMetadata(new ProducesResponseTypeMetadata(statusCode, typeof(TResponse), Json));

    public static IEndpointConventionBuilder Produces(this IEndpointConventionBuilder builder, int statusCode, string? contentType = null)
        => builder.WithMetadata(new ProducesResponseTypeMetadata(statusCode, null, contentType is null ? [] : [contentType]));

    public static IEndpointConventionBuilder ProducesProblem(this IEndpointConventionBuilder builder, int statusCode)
        => builder.WithMetadata(new ProducesResponseTypeMetadata(statusCode, typeof(ProblemDetails), ProblemJson));

    /// <summary>
    /// The request body. Note routing enforces this: a request whose <c>Content-Type</c> is not listed is a 415
    /// from the matcher — which is why the PATCH routes, which read several formats, do not declare one.
    /// </summary>
    public static IEndpointConventionBuilder Accepts<TRequest>(this IEndpointConventionBuilder builder, string contentType)
        => builder.WithMetadata(new AcceptsMetadata([contentType], typeof(TRequest)));
}

/// <summary>
/// The <c>text/event-stream</c> framing for the live tail, written straight to the response.
/// <para>
/// ASP.NET Core 10's <c>TypedResults.ServerSentEvents</c> was the first choice, but it has no way to send a
/// comment line, and a keep-alive should be a comment: an event — even an empty one — reaches every
/// <c>EventSource</c> listener, where a comment is consumed by the parser and never seen. So this writes the
/// same headers that result would (no caching, no compression, no buffering) and frames each event itself.
/// </para>
/// </summary>
sealed class EventStreamWriter
{
    static readonly byte[] Heartbeat = ": keep-alive\n\n"u8.ToArray();

    readonly HttpContext http;

    EventStreamWriter(HttpContext http) => this.http = http;

    public static async Task<EventStreamWriter> StartAsync(HttpContext http)
    {
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache,no-store";
        http.Response.Headers.Pragma = "no-cache";

        // A compressing proxy or middleware would hold events back until a block filled; identity encoding
        // tells response compression to leave the stream alone.
        http.Response.Headers.ContentEncoding = "identity";
        http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();

        await http.Response.StartAsync(http.RequestAborted).ConfigureAwait(false);
        await http.Response.Body.FlushAsync(http.RequestAborted).ConfigureAwait(false);

        return new EventStreamWriter(http);
    }

    public async Task SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        await this.http.Response.Body.WriteAsync(Heartbeat, cancellationToken).ConfigureAwait(false);
        await this.http.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One named event. The payload is single-line JSON, so it is always exactly one <c>data:</c> line.</summary>
    public async Task SendAsync(string eventName, string data, CancellationToken cancellationToken)
    {
        var frame = Encoding.UTF8.GetBytes($"event: {eventName}\ndata: {data}\n\n");

        await this.http.Response.Body.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await this.http.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
