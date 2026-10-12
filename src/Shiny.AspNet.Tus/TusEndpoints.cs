using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shiny.AspNet.Tus.Internal;

namespace Shiny.AspNet.Tus;

/// <summary>
/// The routes one <c>MapTus</c> call registered, so a policy can be stated once for the whole
/// endpoint - <c>.RequireAuthorization()</c> covers creation, every <c>PATCH</c> and every
/// <c>DELETE</c> alike, and a route added in a later version cannot arrive unprotected. Every
/// ASP.NET convention (<c>RequireAuthorization</c>, <c>RequireCors</c>, <c>RequireRateLimiting</c>,
/// <c>WithMetadata</c>, ...) applies, since this is the route group the routes live in.
/// <code>
/// app.MapTus("/files", o => o.Store = new DiskTusStore(uploads))
///    .RequireAuthorization();
/// </code>
/// </summary>
public sealed class TusEndpointConventionBuilder : IEndpointConventionBuilder
{
    readonly IEndpointConventionBuilder group;
    readonly TusHandler handler;

    internal TusEndpointConventionBuilder(IEndpointConventionBuilder group, TusHandler handler)
    {
        this.group = group;
        this.handler = handler;
    }

    /// <summary>
    /// Deletes every unfinished upload past its expiry now, rather than waiting for the sweep.
    /// Useful from a background job of your own, or to sweep on a schedule that suits the store
    /// better than <see cref="TusOptions.CleanupInterval"/>. Returns how many were removed.
    /// </summary>
    public Task<int> RemoveExpiredUploadsAsync(CancellationToken cancellationToken = default)
        => this.handler.RemoveExpiredAsync(cancellationToken);

    /// <inheritdoc />
    public void Add(Action<EndpointBuilder> convention)
        => this.group.Add(convention);

    /// <inheritdoc />
    public void Finally(Action<EndpointBuilder> finallyConvention)
        => this.group.Finally(finallyConvention);
}

/// <summary>
/// Resumable uploads over tus 1.0.0 (https://tus.io/protocols/resumable-upload).
/// <para>
/// A plain <c>POST</c> of a 200 MB video from a phone is all or nothing: the train goes into a
/// tunnel at 180 MB and the whole thing starts again. tus splits an upload into a create and a
/// series of <c>PATCH</c>es against a URL, and lets the client ask with <c>HEAD</c> how many bytes
/// the server has, so a dropped connection costs only what was in flight. The server keeps every
/// byte it received, including from the request that was cut off.
/// </para>
/// <code>
/// app.MapTus("/files", o =>
/// {
///     o.Store = new DiskTusStore(Path.Combine(builder.Environment.ContentRootPath, "App_Data", "uploads"));
///     o.MaxSize = 2L * 1024 * 1024 * 1024;
///     o.Expiration = TimeSpan.FromDays(1);
///     o.OnUploadCompleteAsync = async ctx =>
///     {
///         var name = ctx.Upload.Metadata.GetValueOrDefault("filename") ?? ctx.Upload.Id;
///         await using var file = await ctx.OpenReadAsync();
///         // ...
///     };
/// })
/// .RequireAuthorization();
/// </code>
/// <para>
/// Core protocol plus creation, creation-with-upload, creation-defer-length, termination,
/// expiration, checksum (sha1, sha256, md5) and concatenation. <c>X-HTTP-Method-Override</c> is
/// honoured on <c>POST</c>.
/// </para>
/// </summary>
public static class TusEndpointRouteBuilderExtensions
{
    /// <summary>Maps a tus endpoint at <paramref name="prefix"/>.</summary>
    public static TusEndpointConventionBuilder MapTus(this IEndpointRouteBuilder endpoints, string prefix, Action<TusOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new TusOptions();
        configure(options);

        return endpoints.MapTus(prefix, options);
    }

    /// <summary>Maps a tus endpoint from options built elsewhere.</summary>
    public static TusEndpointConventionBuilder MapTus(this IEndpointRouteBuilder endpoints, string prefix, TusOptions options)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(options);

        if (options.LockReleaseTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), $"{nameof(TusOptions.LockReleaseTimeout)} cannot be negative.");

        if (options.CleanupInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), $"{nameof(TusOptions.CleanupInterval)} must be positive.");

        var group = endpoints.MapGroup(NormalizePrefix(prefix));
        var handler = new TusHandler(options, options.ResolveStore());

        group.MapMethods("/", [HttpMethods.Options], handler.Guard(handler.OptionsAsync, requireVersion: false));
        group.MapMethods("/", [HttpMethods.Post], handler.Guard(handler.CreateAsync));
        group.MapMethods("/{id}", [HttpMethods.Options], handler.Guard(handler.OptionsAsync, requireVersion: false));
        group.MapMethods("/{id}", [HttpMethods.Head], handler.Guard(handler.HeadAsync));
        group.MapMethods("/{id}", [HttpMethods.Patch], handler.Guard(handler.PatchAsync));
        group.MapMethods("/{id}", [HttpMethods.Delete], handler.Guard(handler.DeleteAsync));
        group.MapMethods("/{id}", [HttpMethods.Post], handler.Guard(handler.OverrideAsync));

        // One protocol behind seven routes, documented by tus.io rather than by an OpenAPI schema
        // no tus client would read.
        group.ExcludeFromDescription();

        // The expiry sweep follows the application: a timer from when it has started until it
        // begins stopping. Without a lifetime (a bare route builder in a test, say) request traffic
        // still nudges it.
        if (endpoints.ServiceProvider.GetService<IHostApplicationLifetime>() is { } lifetime)
            handler.AttachTo(lifetime);

        return new TusEndpointConventionBuilder(group, handler);
    }

    static string NormalizePrefix(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        return "/" + prefix.Trim().Trim('/');
    }
}

/// <summary>CORS for a browser tus client.</summary>
public static class TusCorsPolicyBuilderExtensions
{
    /// <summary>
    /// Allows the methods and request headers tus sends, and exposes the response headers it reads.
    /// <code>
    /// builder.Services.AddCors(o => o.AddPolicy("uploads", p => p.WithOrigins("https://app.example.com").WithTusHeaders()));
    /// app.MapTus("/files", ...).RequireCors("uploads");
    /// </code>
    /// <para>
    /// A policy without this fails in the browser in the least helpful way: the preflight for the
    /// first <c>POST</c> is refused because <c>Tus-Resumable</c> is not an allowed header, and the
    /// console says only that CORS blocked it.
    /// </para>
    /// </summary>
    public static CorsPolicyBuilder WithTusHeaders(this CorsPolicyBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder
            .WithMethods(HttpMethods.Post, HttpMethods.Head, HttpMethods.Patch, HttpMethods.Delete, HttpMethods.Options)
            .WithHeaders([.. TusHeaderNames.Request])
            .WithExposedHeaders([.. TusHeaderNames.Exposed]);
    }
}
