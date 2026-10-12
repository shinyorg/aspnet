using System.Net;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Shiny.AspNet.WebDav.Tests;

/// <summary>
/// The parts of the port that only exist because it is hosted on ASP.NET Core: route groups,
/// <c>PathBase</c>, HEAD, and the convention builder the mount returns.
/// </summary>
public class WebDavAspNetTests
{
    static readonly XNamespace Dav = "DAV:";

    static async Task<List<string>> HrefsAsync(TestApp server, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), path);
        request.Headers.Add("Depth", "1");

        var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(207, (int)response.StatusCode);

        var document = XDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        return document.Root!.Elements(Dav + "response").Select(r => r.Element(Dav + "href")!.Value).ToList();
    }

    [Fact]
    public async Task A_mount_inside_a_route_group_reports_hrefs_with_the_group_prefix()
    {
        using var root = new ContentRoot().With("a.txt", "a");

        await using var server = await TestApp.StartAsync(app =>
            app.MapGroup("/api").MapWebDav("/dav", o => o.RootPath = root.Path)
        );

        var hrefs = await HrefsAsync(server, "/api/dav/");

        Assert.Contains("/api/dav/", hrefs);
        Assert.Contains("/api/dav/a.txt", hrefs);
    }

    [Fact]
    public async Task A_mount_behind_use_path_base_reports_hrefs_with_the_path_base()
    {
        using var root = new ContentRoot().With("a.txt", "a");

        await using var server = await TestApp.StartAsync(app =>
        {
            app.UsePathBase("/base");
            app.UseRouting();
            app.MapWebDav("/dav", o => o.RootPath = root.Path);
        });

        var hrefs = await HrefsAsync(server, "/base/dav");

        Assert.Contains("/base/dav/", hrefs);
        Assert.Contains("/base/dav/a.txt", hrefs);
    }

    [Fact]
    public async Task A_destination_inside_a_route_group_resolves_to_the_same_mount()
    {
        using var root = new ContentRoot().With("a.txt", "a");

        await using var server = await TestApp.StartAsync(app =>
            app.MapGroup("/api").MapWebDav("/dav", o =>
            {
                o.RootPath = root.Path;
                o.AllowWrite = true;
            })
        );

        var request = new HttpRequestMessage(new HttpMethod("COPY"), "/api/dav/a.txt");
        request.Headers.Add("Destination", "/api/dav/b.txt");

        var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("a", await File.ReadAllTextAsync(Path.Combine(root.Path, "b.txt"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_mount_root_answers_with_and_without_a_trailing_slash()
    {
        using var root = new ContentRoot();
        await using var server = await TestApp.StartAsync(app => app.MapWebDav("/dav", o => o.RootPath = root.Path));

        foreach (var path in (string[])["/dav", "/dav/"])
        {
            var response = await server.Client.SendAsync(new HttpRequestMessage(HttpMethod.Options, path), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task Head_on_a_file_sends_the_headers_without_the_body()
    {
        using var root = new ContentRoot().With("a.txt", "hello");
        await using var server = await TestApp.StartAsync(app => app.MapWebDav("/dav", o => o.RootPath = root.Path));

        var response = await server.Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/dav/a.txt"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(5, response.Content.Headers.ContentLength);
        Assert.NotNull(response.Headers.ETag);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Conventions_on_the_mount_builder_reach_every_verb()
    {
        using var root = new ContentRoot().With("a.txt", "a");
        var seen = new List<string>();

        await using var server = await TestApp.StartAsync(app =>
            app.MapWebDav("/dav", o => o.RootPath = root.Path)
                .WithMetadata(new MountMarker())
                .Add(builder => seen.Add(builder.DisplayName ?? string.Empty))
        );

        var dataSource = server.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>();
        var mounted = dataSource.Endpoints.Where(e => e.Metadata.GetMetadata<MountMarker>() is not null).ToList();

        // OPTIONS, GET/HEAD, PROPFIND, PUT, MKCOL, PROPPATCH, COPY, MOVE, DELETE, LOCK, UNLOCK.
        Assert.Equal(11, mounted.Count);
        Assert.Equal(11, seen.Count);
    }

    [Fact]
    public async Task A_content_type_provider_overrides_the_default_table()
    {
        using var root = new ContentRoot().With("notes.custom", "x");

        var provider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
        provider.Mappings[".custom"] = "application/x-custom";

        await using var server = await TestApp.StartAsync(app => app.MapWebDav("/dav", o =>
        {
            o.RootPath = root.Path;
            o.ContentTypeProvider = provider;
        }));

        var response = await server.Client.GetAsync("/dav/notes.custom", TestContext.Current.CancellationToken);

        Assert.Equal("application/x-custom", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Over_kestrel_status_only_answers_and_large_uploads_work()
    {
        // TestServer does not enforce what Kestrel does about 204 framing; this runs the real thing.
        using var root = new ContentRoot().With("a.txt", "a");

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        await using var app = builder.Build();
        app.MapWebDav("/dav", o =>
        {
            o.RootPath = root.Path;
            o.AllowWrite = true;
            o.AllowDelete = true;
        });

        await app.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

            var put = await client.PutAsync("/dav/a.txt", new StringContent("b", Encoding.UTF8), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

            var delete = await client.DeleteAsync("/dav/a.txt", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

            // Above Kestrel's 30 MB default request body limit and below the mount's own 64 MB one:
            // the mount's limit is the one that applies.
            var large = new ByteArrayContent(new byte[31 * 1024 * 1024]);
            var bigPut = await client.PutAsync("/dav/large.bin", large, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Created, bigPut.StatusCode);
            Assert.Equal(31 * 1024 * 1024, new FileInfo(Path.Combine(root.Path, "large.bin")).Length);

            var mkcol = await client.SendAsync(new HttpRequestMessage(new HttpMethod("MKCOL"), "/dav/docs"), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Created, mkcol.StatusCode);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    sealed class MountMarker;
}
