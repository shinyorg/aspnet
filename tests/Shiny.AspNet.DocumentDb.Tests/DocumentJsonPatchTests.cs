using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shiny.AspNet.JsonPatch;

namespace Shiny.AspNet.DocumentDb.Tests;

/// <summary>
/// The DocumentDb resource takes JSON Patch alongside merge patch. What must not change with the format is
/// what the endpoint enforces: the If-Match check, the scope on both sides of the write, and the 404 for a
/// document outside it.
/// </summary>
public class DocumentJsonPatchTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static StringContent Patch(string json) => new(json, Encoding.UTF8, JsonPatchDocument.MediaType);

    static async Task<JsonObject> Read(DocumentServer server, string path)
        => JsonNode.Parse(await server.Client.GetStringAsync(path, Token))!.AsObject();

    [Fact]
    public async Task Applies_a_json_patch_to_a_typed_document()
    {
        await using var server = await DocumentServer.StartAsync();
        await server.SeedAsync(new Order { Id = "a", CustomerId = "c1", Status = "open", Total = 10m, Notes = "n" });

        var response = await server.Client.PatchAsync("/orders/a", Patch("""
            [
              {"op":"test","path":"/total","value":10.0},
              {"op":"replace","path":"/status","value":"closed"},
              {"op":"remove","path":"/notes"}
            ]
            """), Token);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.NotNull(response.Headers.ETag);

        var after = await Read(server, "/orders/a");
        Assert.Equal("closed", after["status"]!.GetValue<string>());
        Assert.True(after["notes"] is null || after["notes"]!.GetValueKind() == JsonValueKind.Null);
    }

    [Fact]
    public async Task A_failed_test_is_a_409_and_writes_nothing()
    {
        await using var server = await DocumentServer.StartAsync();
        await server.SeedAsync(new Order { Id = "a", CustomerId = "c1", Status = "open" });

        var response = await server.Client.PatchAsync("/orders/a", Patch("""
            [
              {"op":"replace","path":"/status","value":"closed"},
              {"op":"test","path":"/status","value":"open"}
            ]
            """), Token);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync(Token))!;
        Assert.Equal(1, problem["operationIndex"]!.GetValue<int>());

        Assert.Equal("open", (await Read(server, "/orders/a"))["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_malformed_patch_is_a_400()
    {
        await using var server = await DocumentServer.StartAsync();
        await server.SeedAsync(new Order { Id = "a", CustomerId = "c1" });

        var response = await server.Client.PatchAsync("/orders/a", Patch("""{"status":"closed"}"""), Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Replacing_the_document_with_a_non_object_is_a_422()
    {
        await using var server = await DocumentServer.StartAsync();
        await server.SeedAsync(new Order { Id = "a", CustomerId = "c1" });

        var response = await server.Client.PatchAsync("/orders/a", Patch("""[{"op":"replace","path":"","value":[1]}]"""), Token);

        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Honours_if_match_exactly_as_merge_patch_does()
    {
        await using var server = await DocumentServer.StartAsync();
        await server.SeedAsync(new Order { Id = "a", CustomerId = "c1" });

        using var stale = new HttpRequestMessage(HttpMethod.Patch, "/orders/a")
        {
            Content = Patch("""[{"op":"replace","path":"/status","value":"closed"}]""")
        };
        stale.Headers.IfMatch.Add(new EntityTagHeaderValue("\"999\""));

        Assert.Equal(HttpStatusCode.PreconditionFailed, (await server.Client.SendAsync(stale, Token)).StatusCode);

        var current = await server.Client.GetAsync("/orders/a", Token);
        using var fresh = new HttpRequestMessage(HttpMethod.Patch, "/orders/a")
        {
            Content = Patch("""[{"op":"replace","path":"/status","value":"closed"}]""")
        };
        fresh.Headers.IfMatch.Add(current.Headers.ETag!);

        Assert.Equal(HttpStatusCode.NoContent, (await server.Client.SendAsync(fresh, Token)).StatusCode);
    }

    [Fact]
    public async Task Requires_if_match_when_the_resource_does()
    {
        await using var server = await DocumentServer.StartAsync(o => o.RequireIfMatch = true);
        await server.SeedAsync(new Order { Id = "a", CustomerId = "c1" });

        var response = await server.Client.PatchAsync("/orders/a", Patch("""[{"op":"replace","path":"/status","value":"x"}]"""), Token);

        Assert.Equal(428, (int)response.StatusCode);
    }

    [Fact]
    public async Task Advertises_both_patch_formats_on_a_read()
    {
        await using var server = await DocumentServer.StartAsync();
        await server.SeedAsync(new Order { Id = "a", CustomerId = "c1" });

        var response = await server.Client.GetAsync("/orders/a", Token);
        var accept = response.Headers.GetValues("Accept-Patch").Single();

        Assert.Contains("application/merge-patch+json", accept);
        Assert.Contains(JsonPatchDocument.MediaType, accept);
    }

    [Fact]
    public async Task Still_reads_merge_patch_for_every_other_content_type()
    {
        await using var server = await DocumentServer.StartAsync();
        await server.SeedAsync(new Order { Id = "a", CustomerId = "c1", Notes = "n" });

        var response = await server.Client.PatchAsync(
            "/orders/a",
            new StringContent("""{"status":"merged"}""", Encoding.UTF8, "application/merge-patch+json"),
            Token
        );

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("merged", (await Read(server, "/orders/a"))["status"]!.GetValue<string>());
    }

    // ── Scope ───────────────────────────────────────────────────────────

    static Task<DocumentServer> StartScoped() => DocumentServer.StartAsync(
        o => o.Scope<ICustomerContext>((customer, ctx) =>
        {
            var id = customer.Resolve(ctx.Http);
            return x => x.CustomerId == id;
        })
    );

    static HttpRequestMessage As(string customer, string path, string patch)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, path) { Content = Patch(patch) };
        request.Headers.Add("X-Customer", customer);
        return request;
    }

    [Fact]
    public async Task Reports_an_out_of_scope_document_as_missing()
    {
        await using var server = await StartScoped();
        await server.SeedAsync(new Order { Id = "b", CustomerId = "c2" });

        var response = await server.Client.SendAsync(As("c1", "/orders/b", """[{"op":"replace","path":"/status","value":"x"}]"""), Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Refuses_a_patch_that_moves_the_document_out_of_scope()
    {
        await using var server = await StartScoped();
        await server.SeedAsync(new Order { Id = "a", CustomerId = "c1" });

        var response = await server.Client.SendAsync(
            As("c1", "/orders/a", """[{"op":"replace","path":"/customerId","value":"c2"}]"""),
            Token
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Schema-free collection ──────────────────────────────────────────

    [Fact]
    public async Task Applies_a_json_patch_to_a_schema_free_collection()
    {
        await using var server = await DocumentServer.StartAsync(
            mapExtra: app => app.MapDocumentCollection("/notes", "notes", o => o.Operations = DocumentEndpoints.All)
        );

        var created = await server.Client.PostAsync(
            "/notes",
            new StringContent("""{"id":"n1","title":"hello","tags":["a"]}""", Encoding.UTF8, "application/json"),
            Token
        );
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var response = await server.Client.PatchAsync("/notes/n1", Patch("""
            [
              {"op":"add","path":"/tags/-","value":"b"},
              {"op":"move","from":"/title","path":"/heading"}
            ]
            """), Token);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var read = await server.Client.GetAsync("/notes/n1", Token);
        Assert.Contains(JsonPatchDocument.MediaType, read.Headers.GetValues("Accept-Patch").Single());

        var after = JsonNode.Parse(await read.Content.ReadAsStringAsync(Token))!;
        Assert.Equal("""["a","b"]""", after["tags"]!.ToJsonString());
        Assert.Equal("hello", after["heading"]!.GetValue<string>());
        Assert.Null(after["title"]);

        var missing = await server.Client.PatchAsync("/notes/nope", Patch("""[{"op":"remove","path":"/x"}]"""), Token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
