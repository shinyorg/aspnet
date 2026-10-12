using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Shiny.AspNet.ContentDigest.Tests;

[ApiController]
[Route("api/sync")]
[ContentDigest(RequireRequestDigest = true, AlwaysEmitResponseDigest = false)]
public class SyncController : ControllerBase
{
    // Inherits the controller's: a digest is required on the way in, none sent unasked.
    [HttpPost("batch")]
    public string Batch() => "accepted";

    // An action's attribute replaces the controller's rather than adding to it.
    [HttpPost("snapshot")]
    [ContentDigest]
    public string Snapshot() => "snapshot";

    [HttpPost("off")]
    [DisableContentDigest]
    public string Off() => "plain";
}

public class ControllerTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static HttpRequestMessage Post(string path, string? digest)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent("x") };
        if (digest is not null)
            request.Content.Headers.TryAddWithoutValidation(DigestFields.ContentDigest, digest);

        return request;
    }

    static string? Digest(HttpResponseMessage response)
        => response.Headers.TryGetValues(DigestFields.ContentDigest, out var values) ? values.Single() : null;

    [Fact]
    public async Task Controller_and_action_attributes_are_honoured_most_specific_first()
    {
        await using var server = await DigestHost.StartAsync(
            app =>
            {
                app.UseContentDigest();
                app.MapControllers();
            },
            services =>
            {
                services.AddContentDigest();
                services.AddControllers().AddApplicationPart(typeof(SyncController).Assembly);
            }
        );

        var x = DigestFields.Compute("x"u8);

        Assert.Equal(HttpStatusCode.BadRequest, (await server.Client.SendAsync(Post("/api/sync/batch", null), Token)).StatusCode);

        var batch = await server.Client.SendAsync(Post("/api/sync/batch", x), Token);
        Assert.Equal(HttpStatusCode.OK, batch.StatusCode);
        Assert.Null(Digest(batch));

        var snapshot = await server.Client.SendAsync(Post("/api/sync/snapshot", null), Token);
        Assert.Equal(HttpStatusCode.OK, snapshot.StatusCode);
        Assert.Equal(DigestFields.Compute(Encoding.UTF8.GetBytes(await snapshot.Content.ReadAsStringAsync(Token))), Digest(snapshot));

        var off = await server.Client.SendAsync(Post("/api/sync/off", DigestFields.Compute("wrong"u8)), Token);
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
    }
}
