using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Shiny.AspNet.JsonPatch.Tests;

/// <summary>The same patch over the wire: a RequestDelegate handler, then a minimal-API parameter.</summary>
public class JsonPatchHttpTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static StringContent Patch(string json) => new(json, Encoding.UTF8, JsonPatchDocument.MediaType);

    static async Task<WebApplication> StartAsync(
        Action<WebApplication> map,
        Action<IServiceCollection>? services = null,
        bool kestrel = false
    )
    {
        var builder = WebApplication.CreateSlimBuilder();
        if (kestrel)
            builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));
        else
            builder.WebHost.UseTestServer();

        services?.Invoke(builder.Services);

        var app = builder.Build();
        map(app);
        await app.StartAsync(Token);

        return app;
    }

    static HttpClient Client(WebApplication app)
    {
        if (app.Services.GetRequiredService<IServer>() is TestServer test)
            return test.CreateClient();

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new HttpClient { BaseAddress = new Uri(address) };
    }

    static void MapRaw(WebApplication app) => app.MapPatch("/raw", (RequestDelegate)(async ctx =>
    {
        IResult result;
        try
        {
            var patch = await ctx.Request.ReadJsonPatchAsync(ctx.RequestAborted);
            var patched = patch.ApplyTo(JsonNode.Parse("""{"n":1,"list":[]}"""));

            result = Results.Content(patched!.ToJsonString(), "application/json");
        }
        catch (JsonPatchException ex)
        {
            result = ex.ToResult();
        }

        await result.ExecuteAsync(ctx);
    }));

    [Fact]
    public async Task Raw_handler_reads_and_applies_a_patch()
    {
        await using var app = await StartAsync(MapRaw);
        using var client = Client(app);

        var ok = await client.PatchAsync("/raw", Patch("""[{"op":"replace","path":"/n","value":2},{"op":"add","path":"/list/-","value":"x"}]"""), Token);

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("""{"n":2,"list":["x"]}""", await ok.Content.ReadAsStringAsync(Token));

        var conflict = await client.PatchAsync("/raw", Patch("""[{"op":"add","path":"/x","value":1},{"op":"test","path":"/n","value":5}]"""), Token);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        var problem = JsonNode.Parse(await conflict.Content.ReadAsStringAsync(Token))!;
        Assert.Equal(1, problem["operationIndex"]!.GetValue<int>());
        Assert.Equal("test", problem["op"]!.GetValue<string>());
        Assert.Equal("testFailed", problem["patchError"]!.GetValue<string>());
    }

    [Fact]
    public async Task Raw_handler_answers_415_with_accept_patch_for_plain_json()
    {
        await using var app = await StartAsync(MapRaw);
        using var client = Client(app);

        var response = await client.PatchAsync(
            "/raw",
            new StringContent("""[{"op":"remove","path":"/n"}]""", Encoding.UTF8, "application/json"),
            Token
        );

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal(JsonPatchDocument.MediaType, response.Headers.GetValues("Accept-Patch").Single());
    }

    [Fact]
    public async Task Raw_handler_answers_400_for_an_empty_body()
    {
        await using var app = await StartAsync(MapRaw);
        using var client = Client(app);

        var response = await client.PatchAsync("/raw", Patch(""), Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("malformed", JsonNode.Parse(await response.Content.ReadAsStringAsync(Token))!["patchError"]!.GetValue<string>());
    }

    static void MapUnhandled(WebApplication app) => app.MapPatch("/unhandled", (RequestDelegate)(async ctx =>
    {
        var patch = await ctx.Request.ReadJsonPatchAsync(ctx.RequestAborted);
        patch.ApplyTo(JsonNode.Parse("{}"));
        ctx.Response.StatusCode = StatusCodes.Status204NoContent;
    }));

    [Fact]
    public async Task Unhandled_patch_failure_becomes_a_problem_with_its_status()
    {
        await using var app = await StartAsync(
            app =>
            {
                app.UseExceptionHandler();
                MapUnhandled(app);
            },
            services => services.AddProblemDetails().AddJsonPatch()
        );
        using var client = Client(app);

        var response = await client.PatchAsync("/unhandled", Patch("""[{"op":"remove","path":"/gone"}]"""), Token);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);

        var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync(Token))!;
        Assert.Equal("pathNotFound", problem["patchError"]!.GetValue<string>());
        Assert.Equal(0, problem["operationIndex"]!.GetValue<int>());
    }

    /// <summary>
    /// With no exception handling at all, the status still comes through: JsonPatchException is a
    /// BadHttpRequestException, whose status Kestrel answers with instead of a 500. Kestrel rather than
    /// TestServer, because TestServer rethrows an unhandled exception into the client.
    /// </summary>
    [Fact]
    public async Task Unhandled_patch_failure_keeps_its_status_with_no_exception_handler()
    {
        await using var app = await StartAsync(MapUnhandled, kestrel: true);
        using var client = Client(app);

        var conflict = await client.PatchAsync("/unhandled", Patch("""[{"op":"remove","path":"/gone"}]"""), Token);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        var unsupported = await client.PatchAsync("/unhandled", new StringContent("{}", Encoding.UTF8, "application/json"), Token);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, unsupported.StatusCode);
    }

    // ── Minimal-API parameter ───────────────────────────────────────────

    static Task<WebApplication> StartBound() => StartAsync(
        app =>
        {
            app.UseExceptionHandler();
            app.MapPatch("/api/widgets/{id:int}", (int id, JsonPatchDocument patch) =>
            {
                var widget = new PatchWidget(id, "original", ["a"], 10m);

                try
                {
                    return Results.Json(patch.ApplyTo(widget, PatchJson.Default.PatchWidget), PatchJson.Default.PatchWidget);
                }
                catch (JsonPatchException ex)
                {
                    return ex.ToResult();
                }
            });
        },
        services => services.AddProblemDetails().AddJsonPatch()
    );

    [Fact]
    public async Task Binds_a_json_patch_parameter()
    {
        await using var app = await StartBound();
        using var client = Client(app);

        var response = await client.PatchAsync(
            "/api/widgets/4",
            Patch("""[{"op":"replace","path":"/name","value":"renamed"},{"op":"add","path":"/tags/0","value":"first"}]"""),
            Token
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(Token))!;
        Assert.Equal(4, body["id"]!.GetValue<int>());
        Assert.Equal("renamed", body["name"]!.GetValue<string>());
        Assert.Equal("""["first","a"]""", body["tags"]!.ToJsonString());
    }

    /// <summary>
    /// Differs from upstream: the parameter's accepts-metadata is honoured by ASP.NET routing itself, so a
    /// request with another Content-Type is refused with 415 by the matcher before the endpoint (and so the
    /// binder) ever runs. The status is the same; the routing 415 has no problem body and no Accept-Patch —
    /// the handler-level ReadJsonPatchAsync path, and the DocumentDb resource, still send both.
    /// </summary>
    [Fact]
    public async Task Bound_parameter_is_refused_by_routing_for_another_content_type()
    {
        await using var app = await StartBound();
        using var client = Client(app);

        var response = await client.PatchAsync(
            "/api/widgets/4",
            new StringContent("""{"name":"x"}""", Encoding.UTF8, "application/merge-patch+json"),
            Token
        );

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    /// <summary>
    /// The upside of routing knowing the media type: a merge-patch endpoint and a JSON Patch endpoint can share
    /// one route, and the Content-Type picks between them.
    /// </summary>
    [Fact]
    public async Task Routing_picks_the_json_patch_endpoint_by_content_type()
    {
        await using var app = await StartAsync(app =>
        {
            app.MapPatch("/things/{id}", (string id, JsonPatchDocument patch) => Results.Text("json-patch"));
            app.MapPatch("/things/{id}", (string id) => Results.Text("merge-patch")).Accepts<JsonObject>("application/merge-patch+json");
        });
        using var client = Client(app);

        var jsonPatch = await client.PatchAsync("/things/1", Patch("""[{"op":"remove","path":"/a"}]"""), Token);
        Assert.Equal("json-patch", await jsonPatch.Content.ReadAsStringAsync(Token));

        var merge = await client.PatchAsync("/things/1", new StringContent("{}", Encoding.UTF8, "application/merge-patch+json"), Token);
        Assert.Equal("merge-patch", await merge.Content.ReadAsStringAsync(Token));
    }

    /// <summary>A body that claims to be a JSON Patch but is not one is the binder's to refuse, with the full problem.</summary>
    [Fact]
    public async Task Bound_parameter_answers_400_for_a_patch_that_is_not_an_array()
    {
        await using var app = await StartBound();
        using var client = Client(app);

        var response = await client.PatchAsync("/api/widgets/4", Patch("""{"name":"x"}"""), Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("malformed", JsonNode.Parse(await response.Content.ReadAsStringAsync(Token))!["patchError"]!.GetValue<string>());
    }

    [Fact]
    public async Task Bound_parameter_answers_400_naming_the_malformed_operation()
    {
        await using var app = await StartBound();
        using var client = Client(app);

        var response = await client.PatchAsync(
            "/api/widgets/4",
            Patch("""[{"op":"replace","path":"/name","value":"x"},{"op":"add","path":"/tags"}]"""),
            Token
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync(Token))!;
        Assert.Equal(1, problem["operationIndex"]!.GetValue<int>());
        Assert.Equal("malformed", problem["patchError"]!.GetValue<string>());
    }

    [Fact]
    public async Task Bound_parameter_answers_409_for_a_path_that_does_not_exist()
    {
        await using var app = await StartBound();
        using var client = Client(app);

        var response = await client.PatchAsync("/api/widgets/4", Patch("""[{"op":"remove","path":"/tags/5"}]"""), Token);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync(Token))!;
        Assert.Equal("pathNotFound", problem["patchError"]!.GetValue<string>());
        Assert.Equal("/tags/5", problem["path"]!.GetValue<string>());
    }

    /// <summary>
    /// The parameter declares its own body metadata, so ApiExplorer/OpenAPI see a json-patch+json request body
    /// rather than inferring application/json.
    /// </summary>
    [Fact]
    public async Task Bound_parameter_declares_a_json_patch_request_body()
    {
        await using var app = await StartBound();

        var endpoint = app.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/api/widgets/{id:int}");

        var accepts = endpoint.Metadata.GetOrderedMetadata<IAcceptsMetadata>();
        var patch = Assert.Single(accepts, a => a.RequestType == typeof(JsonPatchDocument));

        Assert.Equal([JsonPatchDocument.MediaType], patch.ContentTypes);
        Assert.DoesNotContain(accepts, a => a.ContentTypes.Contains("application/json"));
    }
}
