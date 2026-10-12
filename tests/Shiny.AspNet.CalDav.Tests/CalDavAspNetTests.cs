using System.Net;
using System.Text;

namespace Shiny.AspNet.CalDav.Tests;

/// <summary>
/// What only exists because the mount is hosted on ASP.NET Core: route groups, <c>PathBase</c>, and
/// where the <c>/.well-known</c> redirects end up.
/// </summary>
public class CalDavAspNetTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static string Propfind(string element)
        => $"""<?xml version="1.0" encoding="utf-8"?><D:propfind xmlns:D="DAV:"><D:prop><{element}/></D:prop></D:propfind>""";

    static async Task<string> PrincipalAsync(TestApp app, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), path)
        {
            Content = new StringContent(Propfind("D:current-user-principal"), Encoding.UTF8, "application/xml")
        };
        request.Headers.Add("Depth", "0");

        var response = await app.Client.SendAsync(request, Token);
        Assert.Equal(207, (int)response.StatusCode);

        return await response.Content.ReadAsStringAsync(Token);
    }

    [Fact]
    public async Task A_mount_in_a_route_group_hands_out_hrefs_with_the_group_prefix_and_no_automatic_redirects()
    {
        using var root = new ContentRoot();

        await using var app = await TestApp.StartInMemoryAsync(server => server.MapGroup("/api").MapCalDav("/dav", o =>
        {
            o.CalendarStore = new FileCalendarStore(root.Path);
            o.DefaultPrincipal = "me";
        }));

        Assert.Contains("/api/dav/principals/me/", await PrincipalAsync(app, "/api/dav/"), StringComparison.Ordinal);

        // The group is below the site root, where the well-known URLs live, so nothing was mapped there.
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/.well-known/caldav", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/api/.well-known/caldav", Token)).StatusCode);
    }

    [Fact]
    public async Task MapCalDavWellKnown_points_at_a_mount_inside_a_group()
    {
        using var root = new ContentRoot();

        await using var app = await TestApp.StartInMemoryAsync(server =>
        {
            server.MapGroup("/api").MapCalDav("/dav", o =>
            {
                o.CalendarStore = new FileCalendarStore(root.Path);
                o.DefaultPrincipal = "me";
            });

            server.MapCalDavWellKnown("/api/dav", cardDav: false);
        });

        var response = await app.Client.GetAsync("/.well-known/caldav", Token);

        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal("/api/dav/", response.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/.well-known/carddav", Token)).StatusCode);
    }

    [Fact]
    public async Task Behind_use_path_base_the_redirect_and_the_hrefs_include_the_path_base()
    {
        using var root = new ContentRoot();

        await using var app = await TestApp.StartInMemoryAsync(server =>
        {
            server.UsePathBase("/base");
            server.UseRouting();

            server.MapCalDav("/dav", o =>
            {
                o.CalendarStore = new FileCalendarStore(root.Path);
                o.DefaultPrincipal = "me";
            });
        });

        var redirect = await app.Client.GetAsync("/base/.well-known/caldav", Token);

        Assert.Equal(HttpStatusCode.MovedPermanently, redirect.StatusCode);
        Assert.Equal("/base/dav/", redirect.Headers.Location?.OriginalString);

        Assert.Contains("/base/dav/principals/me/", await PrincipalAsync(app, "/base/dav/"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Conventions_on_the_mount_builder_reach_every_verb_but_not_the_redirects()
    {
        using var root = new ContentRoot();

        await using var app = await TestApp.StartInMemoryAsync(
            server =>
            {
                server.UseAuthentication();
                server.UseAuthorization();

                server.MapCalDav("/dav", o => o.CalendarStore = new FileCalendarStore(root.Path))
                    .RequireAuthorization();
            },
            services =>
            {
                services.AddAuthenticationWithBasic(o => o.AddUser("ada", "hunter2"));
                services.AddAuthorization();
            }
        );

        foreach (var method in (string[])["OPTIONS", "GET", "PUT", "DELETE", "PROPFIND", "PROPPATCH", "REPORT", "MKCOL", "MKCALENDAR", "COPY", "MOVE", "LOCK", "UNLOCK"])
        {
            var response = await app.Client.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/dav/"), Token);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        // A client asks for these before it has any credentials to send.
        Assert.Equal(HttpStatusCode.MovedPermanently, (await app.Client.GetAsync("/.well-known/caldav", Token)).StatusCode);
    }
}
