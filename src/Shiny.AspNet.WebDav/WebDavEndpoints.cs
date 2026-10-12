using Shiny.AspNet.WebDav.Internal;

namespace Shiny.AspNet.WebDav;

/// <summary>
/// The routes one <c>MapWebDav</c> call just registered, so a policy can be stated once for the
/// whole mount.
/// <para>
/// It is an <see cref="IEndpointConventionBuilder"/> over the mount's route group, so ASP.NET's own
/// extensions — <c>RequireAuthorization</c>, <c>RequireCors</c>, <c>RequireRateLimiting</c>,
/// <c>RequireHost</c>, <c>WithMetadata</c> — cover every verb at once, and a verb added in a later
/// version cannot quietly arrive unprotected. The routes are also kept by what they do, for a policy
/// that should only apply to some of them: <see cref="RequireAuthorizationForChanges"/> is the
/// common one.
/// </para>
/// <code>
/// app.MapWebDav("/dav", o =>
/// {
///     o.RootPath = Path.Combine(app.Environment.ContentRootPath, "share");
///     o.AllowWrite = true;
///     o.AllowDelete = true;
/// })
/// .RequireAuthorization();
/// </code>
/// </summary>
public sealed class WebDavMountBuilder : IEndpointConventionBuilder
{
    readonly RouteGroupBuilder group;
    readonly List<IEndpointConventionBuilder> read;
    readonly List<IEndpointConventionBuilder> write;
    readonly List<IEndpointConventionBuilder> delete;
    readonly List<IEndpointConventionBuilder> locking;

    internal WebDavMountBuilder(
        RouteGroupBuilder group,
        List<IEndpointConventionBuilder> read,
        List<IEndpointConventionBuilder> write,
        List<IEndpointConventionBuilder> delete,
        List<IEndpointConventionBuilder> locking
    )
    {
        this.group = group;
        this.read = read;
        this.write = write;
        this.delete = delete;
        this.locking = locking;
    }

    /// <summary>The route group the mount lives in. Conventions added here reach every verb.</summary>
    public RouteGroupBuilder Group => this.group;

    /// <summary><c>OPTIONS</c>, <c>GET</c>/<c>HEAD</c> and <c>PROPFIND</c>.</summary>
    public IReadOnlyList<IEndpointConventionBuilder> ReadRoutes => this.read;

    /// <summary><c>PUT</c>, <c>MKCOL</c>, <c>PROPPATCH</c>, <c>COPY</c> and <c>MOVE</c>.</summary>
    public IReadOnlyList<IEndpointConventionBuilder> WriteRoutes => this.write;

    /// <summary><c>DELETE</c>.</summary>
    public IReadOnlyList<IEndpointConventionBuilder> DeleteRoutes => this.delete;

    /// <summary><c>LOCK</c> and <c>UNLOCK</c>.</summary>
    public IReadOnlyList<IEndpointConventionBuilder> LockRoutes => this.locking;

    /// <summary>Every route this mount registered.</summary>
    public IEnumerable<IEndpointConventionBuilder> Routes
        => this.read.Concat(this.write).Concat(this.delete).Concat(this.locking);

    /// <summary>
    /// Requires authorization on the routes that change something, leaving reads open.
    /// <para>
    /// Locking counts as a change: a lock reserves the right to write, and one taken anonymously is
    /// a way to stop everyone else writing.
    /// </para>
    /// </summary>
    public WebDavMountBuilder RequireAuthorizationForChanges(params string[] policies)
    {
        foreach (var route in this.write.Concat(this.delete).Concat(this.locking))
            route.RequireAuthorization(policies);

        return this;
    }

    /// <inheritdoc />
    public void Add(Action<EndpointBuilder> convention) => ((IEndpointConventionBuilder)this.group).Add(convention);

    /// <inheritdoc />
    public void Finally(Action<EndpointBuilder> finallyConvention) => ((IEndpointConventionBuilder)this.group).Finally(finallyConvention);
}

/// <summary>
/// A directory served over WebDAV (RFC 4918): browsable, readable and writable by Finder, Windows
/// Explorer, the GNOME and KDE file managers, and every WebDAV client library there is.
/// <para>
/// This is the protocol an operating system already speaks — mount it and the app's storage shows
/// up as a drive, with no client to write at all.
/// </para>
/// <code>
/// app.MapWebDav("/dav", o =>
/// {
///     o.RootPath = Path.Combine(app.Environment.ContentRootPath, "share");
///     o.AllowWrite = true;
///     o.AllowDelete = true;
/// })
/// .RequireAuthorization();
/// </code>
/// <para>
/// Serve it over TLS and put authentication in front of it. WebDAV clients send Basic credentials
/// on every request, and a mount without either is a directory anyone who finds the URL can write
/// to.
/// </para>
/// </summary>
public static class WebDavExtensions
{
    /// <summary>Maps a WebDAV mount at <paramref name="prefix"/>.</summary>
    public static WebDavMountBuilder MapWebDav(
        this IEndpointRouteBuilder endpoints,
        string prefix,
        Action<WebDavOptions> configure
    )
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new WebDavOptions();
        configure(options);

        return endpoints.MapWebDav(prefix, options);
    }

    /// <summary>Maps a WebDAV mount from options built elsewhere.</summary>
    public static WebDavMountBuilder MapWebDav(
        this IEndpointRouteBuilder endpoints,
        string prefix,
        WebDavOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(options);

        // Resolved here rather than on the first request, so a root that does not exist is a startup
        // failure naming it rather than a mount that answers 404 to everything.
        var fileSystem = options.ResolveFileSystem();
        var handler = new WebDavHandler(options, fileSystem);
        var group = endpoints.MapGroup(NormalizePrefix(prefix));

        var read = new List<IEndpointConventionBuilder>();
        var write = new List<IEndpointConventionBuilder>();
        var delete = new List<IEndpointConventionBuilder>();
        var locking = new List<IEndpointConventionBuilder>();

        // One route per verb: an ASP.NET catch-all also matches the empty remainder, so
        // "/dav/{**path}" answers for the mount root as well as everything under it.
        void MapVerb(List<IEndpointConventionBuilder> routes, string[] methods, Func<HttpContext, ValueTask> handle)
            => routes.Add(group.MapMethods("/{**path}", methods, handler.Guard(handle)));

        MapVerb(read, [HttpMethods.Options], handler.OptionsAsync);
        MapVerb(read, [HttpMethods.Get, HttpMethods.Head], handler.GetAsync);
        MapVerb(read, [WebDavMethods.PropFind], handler.PropFindAsync);

        // Every verb is mapped whatever the options say, and the handler answers 405 for the ones
        // this mount does not allow. Leaving them unmapped would make the router's own 405 the
        // answer, with an Allow header assembled from the route table rather than from the options
        // — two sources of truth for the same question, and the wrong one is the one clients read.
        MapVerb(write, [HttpMethods.Put], handler.PutAsync);
        MapVerb(write, [WebDavMethods.MkCol], handler.MkColAsync);
        MapVerb(write, [WebDavMethods.PropPatch], handler.PropPatchAsync);
        MapVerb(write, [WebDavMethods.Copy], handler.CopyAsync);
        MapVerb(write, [WebDavMethods.Move], handler.MoveAsync);

        MapVerb(delete, [HttpMethods.Delete], handler.DeleteAsync);

        MapVerb(locking, [WebDavMethods.Lock], handler.LockAsync);
        MapVerb(locking, [WebDavMethods.Unlock], handler.UnlockAsync);

        // A WebDAV mount is not an API surface anyone wants described in an OpenAPI document: it is
        // one protocol behind a dozen routes, and RFC 4918 already documents it.
        group.ExcludeFromDescription();

        return new WebDavMountBuilder(group, read, write, delete, locking);
    }

    static string NormalizePrefix(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        return "/" + prefix.Trim().Trim('/');
    }
}
