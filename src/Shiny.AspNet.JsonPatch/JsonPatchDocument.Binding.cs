using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;

namespace Shiny.AspNet.JsonPatch;

// Minimal-API parameter binding.
//
// A JSON Patch is not "JSON in some format a formatter can read" — it has exactly one media type, it needs no
// registered serializer metadata, and its failures deserve an operation index rather than "the body could not
// be read". So rather than letting minimal APIs infer it as an application/json body, the type binds itself.
// BindAsync and IEndpointParameterMetadataProvider are both static, interface-shaped conventions the Request
// Delegate Generator understands, so a JsonPatchDocument parameter is as AOT-clean as an int.
public sealed partial class JsonPatchDocument : IEndpointParameterMetadataProvider
{
    /// <summary>
    /// Binds a <see cref="JsonPatchDocument"/> handler parameter from the request body.
    /// <para>
    /// A failure is thrown as a <see cref="JsonPatchException"/> — 400 for a missing or invalid patch (a wrong
    /// <c>Content-Type</c> normally never gets this far: see <see cref="PopulateMetadata"/>). It is a
    /// <see cref="BadHttpRequestException"/>, so the status survives
    /// even with no exception handling at all; with <c>services.AddJsonPatch()</c> and
    /// <c>app.UseExceptionHandler()</c> the response is the full problem naming the operation, plus an
    /// <c>Accept-Patch</c> header on a 415.
    /// </para>
    /// </summary>
    public static async ValueTask<JsonPatchDocument?> BindAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return await context.Request.ReadJsonPatchAsync(context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// Declares the body as <c>application/json-patch+json</c>, so OpenAPI describes the operation as taking
    /// a JSON Patch and nothing else.
    /// <para>
    /// Routing honours the same metadata: a request with any other <c>Content-Type</c> is answered 415 by the
    /// endpoint matcher before the handler runs (with no problem body or <c>Accept-Patch</c> — for those, read
    /// the body yourself with <see cref="JsonPatchRequestExtensions.ReadJsonPatchAsync"/>). The upside is that a
    /// merge-patch endpoint declaring <c>.Accepts&lt;T&gt;("application/merge-patch+json")</c> can share the
    /// route, and the <c>Content-Type</c> picks between them.
    /// </para>
    /// </summary>
    public static void PopulateMetadata(ParameterInfo parameter, EndpointBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Metadata.Add(new JsonPatchAcceptsMetadata());
    }

    sealed class JsonPatchAcceptsMetadata : IAcceptsMetadata
    {
        static readonly string[] Types = [MediaType];

        public IReadOnlyList<string> ContentTypes => Types;

        public Type? RequestType => typeof(JsonPatchDocument);

        public bool IsOptional => false;
    }
}
