using Shiny.AspNet.CalDav.Internal;
using Shiny.AspNet.WebDav;

namespace Shiny.AspNet.CalDav;

/// <summary>The methods CalDAV (RFC 4791) and extended MKCOL (RFC 5689) use on top of WebDAV.</summary>
public static class CalDavMethods
{
    /// <summary>Creates a calendar collection (RFC 4791 §5.3.1).</summary>
    public const string MkCalendar = "MKCALENDAR";

    /// <summary>Asks for a report — every query, multiget and sync. Same as <see cref="WebDavMethods.Report"/>.</summary>
    public const string Report = WebDavMethods.Report;
}

/// <summary>
/// The routes one <c>MapCalDav</c> call registered, so a policy can be stated once for the whole
/// mount — the counterpart of <see cref="WebDavMountBuilder"/>.
/// <para>
/// An <see cref="IEndpointConventionBuilder"/> over the mount's route group: ASP.NET's own
/// <c>RequireAuthorization</c>, <c>RequireCors</c>, <c>RequireRateLimiting</c> and
/// <c>WithMetadata</c> cover every verb under the prefix at once.
/// </para>
/// <code>
/// app.MapCalDav("/dav", o =>
/// {
///     o.CalendarStore = new FileCalendarStore(Path.Combine(dataPath, "calendars"));
///     o.AddressBookStore = new FileAddressBookStore(Path.Combine(dataPath, "contacts"));
/// })
/// .RequireAuthorization();
/// </code>
/// </summary>
public sealed class CalDavMountBuilder : IEndpointConventionBuilder
{
    readonly RouteGroupBuilder group;

    internal CalDavMountBuilder(RouteGroupBuilder group, IEndpointConventionBuilder? wellKnown)
    {
        this.group = group;
        this.WellKnown = wellKnown;
    }

    /// <summary>The route group the mount lives in. Conventions added here reach every verb.</summary>
    public RouteGroupBuilder Group => this.group;

    /// <summary>
    /// The <c>/.well-known/caldav</c> and <c>/.well-known/carddav</c> redirects, when they were
    /// mapped. Not covered by conventions added to the mount: they reveal nothing but where the mount
    /// is, and a client usually asks them before it has sent any credentials.
    /// </summary>
    public IEndpointConventionBuilder? WellKnown { get; }

    /// <inheritdoc />
    public void Add(Action<EndpointBuilder> convention) => ((IEndpointConventionBuilder)this.group).Add(convention);

    /// <inheritdoc />
    public void Finally(Action<EndpointBuilder> finallyConvention) => ((IEndpointConventionBuilder)this.group).Finally(finallyConvention);
}

/// <summary>
/// Calendars and contacts served over CalDAV (RFC 4791) and CardDAV (RFC 6352): subscribed to and
/// synced by iOS and macOS Calendar and Contacts, Thunderbird, DAVx⁵ on Android, and every other
/// client that speaks them.
/// <para>
/// One mount serves both, because the two share almost everything — a principal, discovery, sync
/// tokens, multiget, the ETag rules — and a client configured with one account URL expects to find
/// both home sets on the same principal.
/// </para>
/// <code>
/// app.UseAuthentication();
/// app.UseAuthorization();
///
/// app.MapCalDav("/dav", o =>
/// {
///     o.CalendarStore = new FileCalendarStore(Path.Combine(dataPath, "calendars"));
///     o.AddressBookStore = new FileAddressBookStore(Path.Combine(dataPath, "contacts"));
/// })
/// .RequireAuthorization();
/// </code>
/// <para>
/// On iOS: <b>Settings → Calendar → Accounts → Add Account → Other → Add CalDAV Account</b>, with the
/// server's host name and the Basic credentials. Serve it over TLS: every one of those clients sends
/// the password on every request.
/// </para>
/// </summary>
public static class CalDavExtensions
{
    /// <summary>
    /// Maps a CalDAV/CardDAV mount at <paramref name="prefix"/> — and, when
    /// <see cref="CalDavOptions.MapWellKnown"/> is on and <paramref name="endpoints"/> is the app
    /// rather than a route group, the <c>/.well-known</c> redirects to it.
    /// </summary>
    public static CalDavMountBuilder MapCalDav(this IEndpointRouteBuilder endpoints, string prefix, Action<CalDavOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new CalDavOptions();
        configure(options);

        return endpoints.MapCalDav(prefix, options);
    }

    /// <summary>Maps a CalDAV/CardDAV mount from options built elsewhere.</summary>
    public static CalDavMountBuilder MapCalDav(this IEndpointRouteBuilder endpoints, string prefix, CalDavOptions options)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(options);

        options.Validate();

        var normalized = NormalizePrefix(prefix);
        var group = endpoints.MapGroup(normalized);
        var handler = new CalDavHandler(options);

        // One route per verb: an ASP.NET catch-all also matches the empty remainder, so
        // "/dav/{**path}" answers for the mount root as well as everything under it.
        void MapVerb(string[] methods, Func<HttpContext, ValueTask> handle)
            => group.MapMethods("/{**path}", methods, handler.Guard(handle));

        MapVerb([HttpMethods.Options], handler.OptionsAsync);
        MapVerb([HttpMethods.Get, HttpMethods.Head], handler.GetAsync);
        MapVerb([HttpMethods.Put], handler.PutAsync);
        MapVerb([HttpMethods.Delete], handler.DeleteAsync);
        MapVerb([WebDavMethods.PropFind], handler.PropFindAsync);
        MapVerb([WebDavMethods.PropPatch], handler.PropPatchAsync);
        MapVerb([WebDavMethods.Report], handler.ReportAsync);
        MapVerb([WebDavMethods.MkCol], handler.MkColAsync);
        MapVerb([CalDavMethods.MkCalendar], handler.MkCalendarAsync);

        // Not verbs this mount has any use for, but a client that tries one should get the Allow
        // list the mount publishes rather than the router's.
        MapVerb([WebDavMethods.Copy, WebDavMethods.Move, WebDavMethods.Lock, WebDavMethods.Unlock], handler.NotAllowedAsync);

        // A CalDAV mount is one protocol behind a dozen routes, and RFC 4791 documents it.
        group.ExcludeFromDescription();

        // The redirects belong at the root of the site. A route group passed in here is somewhere
        // below it, so they are only mapped automatically when this is the app itself — anything
        // else maps them with MapCalDavWellKnown, naming the full mount path.
        IEndpointConventionBuilder? wellKnown = null;

        if (options.MapWellKnown && endpoints is not RouteGroupBuilder)
        {
            wellKnown = endpoints.MapCalDavWellKnown(
                normalized,
                calDav: options.CalendarStore is not null,
                cardDav: options.AddressBookStore is not null
            );
        }

        return new CalDavMountBuilder(group, wellKnown);
    }

    /// <summary>
    /// Maps <c>/.well-known/caldav</c> and <c>/.well-known/carddav</c> (RFC 6764) as redirects to
    /// <paramref name="mountPath"/> — for a mount placed inside a route group, or one whose
    /// automatic redirects were turned off. The request's <c>PathBase</c> is prepended, so a site
    /// behind <c>UsePathBase</c> redirects to where the mount really is.
    /// </summary>
    public static IEndpointConventionBuilder MapCalDavWellKnown(
        this IEndpointRouteBuilder endpoints,
        string mountPath,
        bool calDav = true,
        bool cardDav = true
    )
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var normalized = NormalizePrefix(mountPath);
        var location = normalized.EndsWith('/') ? normalized : normalized + "/";
        var group = endpoints.MapGroup("/.well-known");

        // RFC 6764 §5: a redirect to the context path. A 301 rather than a 307, because clients
        // (iOS among them) remember a permanent one and stop asking.
        Task Redirect(HttpContext context)
        {
            context.Response.StatusCode = StatusCodes.Status301MovedPermanently;
            context.Response.Headers[HeaderNames.Location] = context.Request.PathBase.Add(location).Value;
            context.Response.ContentLength = 0;

            return context.Response.StartAsync(context.RequestAborted);
        }

        string[] methods = [HttpMethods.Get, HttpMethods.Head, WebDavMethods.PropFind, HttpMethods.Options];

        if (calDav)
            group.MapMethods("/caldav", methods, Redirect);

        if (cardDav)
            group.MapMethods("/carddav", methods, Redirect);

        group.ExcludeFromDescription();

        return group;
    }

    static string NormalizePrefix(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        return "/" + prefix.Trim().Trim('/');
    }
}
