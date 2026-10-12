using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Shiny.AspNet.JsonPatch;

/// <summary>Registration for JSON Patch's exception handling.</summary>
public static class JsonPatchServiceCollectionExtensions
{
    /// <summary>
    /// Registers an <see cref="IExceptionHandler"/> that answers an unhandled <see cref="JsonPatchException"/>
    /// with its full RFC 9457 problem — the status for its kind, the failing operation's index, <c>op</c> and
    /// <c>path</c>, and <c>Accept-Patch</c> on a 415 — instead of a bare status code.
    /// <para>
    /// Exception handlers only run inside <c>app.UseExceptionHandler()</c>, so add that to the pipeline too.
    /// Nothing else is needed to use <see cref="JsonPatchDocument"/>; this only matters for patches that are
    /// not caught and turned into <see cref="JsonPatchException.ToResult"/> by the handler itself.
    /// </para>
    /// <code>
    /// builder.Services.AddProblemDetails();
    /// builder.Services.AddJsonPatch();
    /// ...
    /// app.UseExceptionHandler();
    /// app.MapPatch("/notes/{id}", (string id, JsonPatchDocument patch) => ...);
    /// </code>
    /// </summary>
    public static IServiceCollection AddJsonPatch(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddExceptionHandler<JsonPatchExceptionHandler>();
        return services;
    }

    sealed class JsonPatchExceptionHandler : IExceptionHandler
    {
        public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
        {
            if (exception is not JsonPatchException patch)
                return false;

            httpContext.Response.StatusCode = patch.StatusCode;
            await patch.ToResult().ExecuteAsync(httpContext).ConfigureAwait(false);

            return true;
        }
    }
}
