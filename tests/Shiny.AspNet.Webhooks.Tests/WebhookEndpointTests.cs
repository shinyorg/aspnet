using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Shiny.AspNet.Webhooks.Tests;

// ---------------------------------------------------------------------------
// MVC: [RequireWebhookSignature] on a controller or action becomes endpoint
// metadata, which UseWebhookVerification enforces.
// ---------------------------------------------------------------------------

[ApiController]
[Route("mvc-hooks")]
[RequireWebhookSignature("github")]
public class HookController : ControllerBase
{
    [HttpPost("github")]
    public string GitHub()
    {
        var webhook = this.HttpContext.GetWebhook();
        return $"{webhook.Verifier}:{webhook.EventType}:{webhook.ReadAsString()}";
    }

    // The action's attribute replaces the controller's: a delivery comes from one sender.
    [HttpPost("other")]
    [RequireWebhookSignature("other")]
    public string Other() => this.HttpContext.GetWebhook().Verifier;
}

public class WebhookEndpointTests
{
    const string Secret = "It's a Secret to Everybody";

    static CancellationToken Token => TestContext.Current.CancellationToken;

    sealed class TestApp(WebApplication app) : IAsyncDisposable
    {
        public WebApplication App { get; } = app;
        public HttpClient Client { get; } = app.GetTestClient();

        public async ValueTask DisposeAsync()
        {
            this.Client.Dispose();
            await this.App.DisposeAsync();
        }
    }

    static async Task<TestApp> StartAsync(Action<WebApplication> map, Action<IServiceCollection>? services = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        services?.Invoke(builder.Services);

        var app = builder.Build();
        map(app);
        await app.StartAsync(Token);

        return new TestApp(app);
    }

    static HttpRequestMessage GitHubDelivery(string path, string body, string? secret = Secret, string delivery = "d-1", string @event = "push")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        if (secret is not null)
        {
            var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body));
            request.Headers.TryAddWithoutValidation("X-Hub-Signature-256", "sha256=" + Convert.ToHexStringLower(hash));
        }

        request.Headers.TryAddWithoutValidation("X-GitHub-Delivery", delivery);
        request.Headers.TryAddWithoutValidation("X-GitHub-Event", @event);
        return request;
    }

    static async Task<string> ReadBodyAsStringAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    static void MapGitHub(IEndpointRouteBuilder app, Action<WebhookOptions>? configure = null, List<string>? seen = null)
        => app.MapWebhook("/hooks/github", WebhookSignature.GitHub(Secret), async (WebhookContext ctx) =>
        {
            seen?.Add(ctx.DeliveryId!);

            // Both views of the body: the verified bytes, and the request stream the ordinary API reads.
            var viaStream = await ReadBodyAsStringAsync(ctx.Request, ctx.RequestAborted);
            await ctx.Response.WriteAsync($"{ctx.EventType}|{ctx.ReadAsString()}|{viaStream}", ctx.RequestAborted);
        }, configure);

    /// <summary>A body with no declared length, so the server sees it chunked.</summary>
    sealed class UnsizedContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    [Fact]
    public async Task Runs_the_handler_for_a_valid_delivery_with_the_body_readable_both_ways()
    {
        await using var server = await StartAsync(app => MapGitHub(app));

        var response = await server.Client.SendAsync(GitHubDelivery("/hooks/github", "{\"ref\":\"main\"}"), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("push|{\"ref\":\"main\"}|{\"ref\":\"main\"}", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Refuses_a_forged_delivery_with_a_problem_that_names_no_check()
    {
        var seen = new List<string>();
        await using var server = await StartAsync(app => MapGitHub(app, seen: seen));

        var response = await server.Client.SendAsync(GitHubDelivery("/hooks/github", "{}", secret: "guess"), Token);
        var body = await response.Content.ReadAsStringAsync(Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("could not be verified", body);
        Assert.DoesNotContain("secret", body, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(seen);
    }

    [Fact]
    public async Task Rejections_go_through_the_registered_problem_details_service()
    {
        await using var server = await StartAsync(
            app => MapGitHub(app),
            services => services.AddProblemDetails(o => o.CustomizeProblemDetails = c => c.ProblemDetails.Extensions["app"] = "custom")
        );

        var response = await server.Client.SendAsync(GitHubDelivery("/hooks/github", "{}", secret: "guess"), Token);
        var body = await response.Content.ReadAsStringAsync(Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("\"app\":\"custom\"", body);
    }

    [Fact]
    public async Task Refuses_an_unsigned_delivery()
    {
        await using var server = await StartAsync(app => MapGitHub(app));

        var response = await server.Client.SendAsync(GitHubDelivery("/hooks/github", "{}", secret: null), Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Verifies_a_body_that_arrives_in_many_reads()
    {
        // Larger than any single read buffer on the way in, so the body has to be reassembled exactly
        // before the signature can match.
        var large = "{\"blob\":\"" + new string('x', 100_000) + "\"}";

        await using var server = await StartAsync(app =>
            app.MapWebhook("/hooks/github", WebhookSignature.GitHub(Secret), async (WebhookContext ctx) =>
                await ctx.Response.WriteAsync(ctx.Body.Length.ToString(), ctx.RequestAborted)));

        var response = await server.Client.SendAsync(GitHubDelivery("/hooks/github", large), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(large.Length.ToString(), await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Refuses_a_body_over_the_limit_with_413()
    {
        await using var server = await StartAsync(app => MapGitHub(app, o => o.MaxBodySize = 64));

        var response = await server.Client.SendAsync(GitHubDelivery("/hooks/github", new string('x', 65)), Token);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Refuses_an_unsized_body_over_the_limit_with_413()
    {
        await using var server = await StartAsync(app => MapGitHub(app, o => o.MaxBodySize = 64));

        var request = GitHubDelivery("/hooks/github", "");
        request.Content = new UnsizedContent(Encoding.UTF8.GetBytes(new string('x', 200)));

        var response = await server.Client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task A_redelivery_is_acknowledged_without_running_the_handler_again()
    {
        var seen = new List<string>();
        await using var server = await StartAsync(app => MapGitHub(app, o => o.SuppressDuplicates(), seen));

        var first = await server.Client.SendAsync(GitHubDelivery("/hooks/github", "{}", delivery: "abc"), Token);
        var second = await server.Client.SendAsync(GitHubDelivery("/hooks/github", "{}", delivery: "abc"), Token);
        var other = await server.Client.SendAsync(GitHubDelivery("/hooks/github", "{}", delivery: "def"), Token);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        Assert.Equal(["abc", "def"], seen);
    }

    [Fact]
    public async Task A_delivery_store_registered_in_di_is_used_when_the_options_name_none()
    {
        var seen = new List<string>();
        await using var server = await StartAsync(
            app => MapGitHub(app, seen: seen),
            services => services.AddSingleton<IWebhookDeliveryStore>(new InMemoryWebhookDeliveryStore())
        );

        await server.Client.SendAsync(GitHubDelivery("/hooks/github", "{}", delivery: "abc"), Token);
        var second = await server.Client.SendAsync(GitHubDelivery("/hooks/github", "{}", delivery: "abc"), Token);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(["abc"], seen);
    }

    [Fact]
    public async Task A_forged_delivery_does_not_poison_the_duplicate_store()
    {
        var seen = new List<string>();
        await using var server = await StartAsync(app => MapGitHub(app, o => o.SuppressDuplicates(), seen));

        await server.Client.SendAsync(GitHubDelivery("/hooks/github", "{}", secret: "forged", delivery: "abc"), Token);
        var real = await server.Client.SendAsync(GitHubDelivery("/hooks/github", "{}", delivery: "abc"), Token);

        Assert.Equal(HttpStatusCode.OK, real.StatusCode);
        Assert.Equal(["abc"], seen);
    }

    [Fact]
    public async Task A_delivery_the_handler_failed_is_processed_when_retried()
    {
        var attempts = 0;

        await using var server = await StartAsync(app =>
            app.MapWebhook(
                "/hooks/github",
                WebhookSignature.GitHub(Secret),
                (WebhookContext ctx) => Task.FromResult(++attempts == 1 ? Results.StatusCode(503) : Results.Ok()),
                o => o.SuppressDuplicates()
            ));

        var failed = await server.Client.SendAsync(GitHubDelivery("/hooks/github", "{}", delivery: "retry-me"), Token);
        var retried = await server.Client.SendAsync(GitHubDelivery("/hooks/github", "{}", delivery: "retry-me"), Token);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task A_stale_standard_webhook_is_refused_by_the_configured_clock()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var secret = WebhookSigner.GenerateSecret();
        var signer = WebhookSigner.StandardWebhooks(secret, clock);
        var handled = 0;

        await using var server = await StartAsync(app =>
            app.MapWebhook("/hooks/std", WebhookSignature.StandardWebhooks(secret), (WebhookContext _) =>
            {
                handled++;
                return Task.CompletedTask;
            }, o => o.TimeProvider = clock));

        HttpRequestMessage Signed()
        {
            var body = "{\"type\":\"ping\"}"u8.ToArray();
            var request = new HttpRequestMessage(HttpMethod.Post, "/hooks/std") { Content = new ByteArrayContent(body) };
            signer.Apply(request, "msg_1", body);
            return request;
        }

        var fresh = await server.Client.SendAsync(Signed(), Token);

        var stale = Signed();
        clock.Advance(TimeSpan.FromMinutes(10));
        var replayed = await server.Client.SendAsync(stale, Token);

        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, replayed.StatusCode);
        Assert.Equal(1, handled);
    }

    [Fact]
    public async Task A_named_verifier_is_resolved_from_the_registered_options()
    {
        await using var server = await StartAsync(
            app => app.MapWebhook("/hooks/github", "github", (WebhookContext ctx) => Task.FromResult(Results.Text(ctx.Verifier))),
            services => services.AddWebhooks(o => o.AddVerifier("github", WebhookSignature.GitHub(Secret)))
        );

        var response = await server.Client.SendAsync(GitHubDelivery("/hooks/github", "{}"), Token);

        Assert.Equal("github", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Mapping_an_unregistered_verifier_fails_at_startup_not_per_request()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(
            app => app.MapWebhook("/hooks/x", "missing", (WebhookContext _) => Task.CompletedTask)
        ));
    }

    [Fact]
    public async Task A_custom_rejection_replaces_the_problem_response()
    {
        await using var server = await StartAsync(app => MapGitHub(app, o => o.OnRejected = (ctx, _) =>
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return ValueTask.CompletedTask;
        }));

        var response = await server.Client.SendAsync(GitHubDelivery("/hooks/github", "{}", secret: "forged"), Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Middleware_verifies_a_mapped_route_that_requires_a_signature()
    {
        await using var server = await StartAsync(
            app =>
            {
                app.UseWebhookVerification();
                app.MapPost("/hooks/plain", async (HttpContext ctx) =>
                {
                    var webhook = ctx.GetWebhook();
                    await ctx.Response.WriteAsync(webhook.DeliveryId + ":" + await ReadBodyAsStringAsync(ctx.Request, ctx.RequestAborted), ctx.RequestAborted);
                }).RequireWebhookSignature("github");
                app.MapPost("/open", (HttpContext ctx) => ctx.Response.WriteAsync("open", ctx.RequestAborted));
            },
            services => services.AddWebhooks(o => o.AddVerifier("github", WebhookSignature.GitHub(Secret)))
        );

        var ok = await server.Client.SendAsync(GitHubDelivery("/hooks/plain", "{\"a\":1}", delivery: "x1"), Token);
        var forged = await server.Client.SendAsync(GitHubDelivery("/hooks/plain", "{}", secret: "no"), Token);
        var open = await server.Client.PostAsync("/open", new StringContent("hi"), Token);

        Assert.Equal("x1:{\"a\":1}", await ok.Content.ReadAsStringAsync(Token));
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        Assert.Equal("open", await open.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Middleware_verifies_every_route_in_a_group()
    {
        await using var server = await StartAsync(
            app =>
            {
                app.UseWebhookVerification();
                var hooks = app.MapGroup("/hooks").RequireWebhookSignature("github");
                hooks.MapPost("/a", (HttpContext ctx) => ctx.Response.WriteAsync("a:" + ctx.GetWebhook().Verifier, ctx.RequestAborted));
                hooks.MapPost("/b", (HttpContext ctx) => ctx.Response.WriteAsync("b:" + ctx.GetWebhook().Verifier, ctx.RequestAborted));
            },
            services => services.AddWebhooks(o => o.AddVerifier("github", WebhookSignature.GitHub(Secret)))
        );

        var a = await server.Client.SendAsync(GitHubDelivery("/hooks/a", "{}"), Token);
        var b = await server.Client.SendAsync(GitHubDelivery("/hooks/b", "{}"), Token);
        var forged = await server.Client.SendAsync(GitHubDelivery("/hooks/b", "{}", secret: "no"), Token);

        Assert.Equal("a:github", await a.Content.ReadAsStringAsync(Token));
        Assert.Equal("b:github", await b.Content.ReadAsStringAsync(Token));
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
    }

    [Fact]
    public async Task Middleware_and_map_webhook_together_verify_once()
    {
        var seen = new List<string>();
        await using var server = await StartAsync(
            app =>
            {
                app.UseWebhookVerification();
                MapGitHub(app, seen: seen);
            },
            services => services.AddWebhooks(o => o.SuppressDuplicates())
        );

        var response = await server.Client.SendAsync(GitHubDelivery("/hooks/github", "{}", delivery: "once"), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["once"], seen);
    }

    [Fact]
    public async Task Middleware_applies_the_settings_a_map_webhook_route_was_mapped_with()
    {
        // The middleware reaches the route first; it must not swap the route's 64-byte limit for the
        // shared 5 MiB one.
        await using var server = await StartAsync(app =>
        {
            app.UseWebhookVerification();
            MapGitHub(app, o => o.MaxBodySize = 64);
        });

        var response = await server.Client.SendAsync(GitHubDelivery("/hooks/github", new string('x', 65)), Token);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Minimal_api_endpoints_are_verified_and_bind_the_delivery()
    {
        await using var server = await StartAsync(
            app =>
            {
                app.UseWebhookVerification();
                app.MapPost("/min-hooks/github", (WebhookContext webhook) => $"{webhook.EventType}:{webhook.ReadAsString()}")
                    .WithMetadata(new RequireWebhookSignatureAttribute("github"));
                app.MapPost("/min-hooks/raw", async (HttpRequest request, CancellationToken ct) => "raw:" + await ReadBodyAsStringAsync(request, ct))
                    .RequireWebhookSignature("github");
            },
            services => services.AddWebhooks(o => o.AddVerifier("github", WebhookSignature.GitHub(Secret)))
        );

        var ok = await server.Client.SendAsync(GitHubDelivery("/min-hooks/github", "{\"n\":1}", @event: "issues"), Token);
        var raw = await server.Client.SendAsync(GitHubDelivery("/min-hooks/raw", "{\"n\":2}"), Token);
        var forged = await server.Client.SendAsync(GitHubDelivery("/min-hooks/github", "{}", secret: "forged"), Token);

        Assert.Equal("issues:{\"n\":1}", await ok.Content.ReadAsStringAsync(Token));
        Assert.Equal("raw:{\"n\":2}", await raw.Content.ReadAsStringAsync(Token));
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
    }

    [Fact]
    public async Task An_endpoint_that_takes_the_delivery_fails_closed_without_the_middleware()
    {
        var ran = false;
        await using var server = await StartAsync(
            app => app.MapPost("/min-hooks/github", (WebhookContext webhook) =>
            {
                ran = true;
                return webhook.ReadAsString();
            }).RequireWebhookSignature("github"),
            services => services.AddWebhooks(o => o.AddVerifier("github", WebhookSignature.GitHub(Secret)))
        );

        // TestServer surfaces an unhandled exception to the client rather than writing a 500.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => server.Client.SendAsync(GitHubDelivery("/min-hooks/github", "{}"), Token)
        );

        Assert.Contains("UseWebhookVerification", ex.Message);
        Assert.False(ran);
    }

    [Fact]
    public async Task Mvc_controllers_and_actions_are_verified_with_the_attribute()
    {
        await using var server = await StartAsync(
            app =>
            {
                app.UseWebhookVerification();
                app.MapControllers();
            },
            services =>
            {
                services.AddControllers().AddApplicationPart(typeof(HookController).Assembly);
                services.AddWebhooks(o =>
                {
                    o.AddVerifier("github", WebhookSignature.GitHub(Secret));
                    o.AddVerifier("other", WebhookSignature.Hmac(h =>
                    {
                        h.Name = "other";
                        h.HeaderName = "X-Hub-Signature-256";
                        h.Prefix = "sha256=";
                        h.Secrets.Add("other-secret");
                    }));
                });
            }
        );

        var ok = await server.Client.SendAsync(GitHubDelivery("/mvc-hooks/github", "{\"n\":1}", @event: "issues"), Token);
        var forged = await server.Client.SendAsync(GitHubDelivery("/mvc-hooks/github", "{}", secret: "forged"), Token);
        var other = await server.Client.SendAsync(GitHubDelivery("/mvc-hooks/other", "{}", secret: "other-secret"), Token);
        var otherWithClassSecret = await server.Client.SendAsync(GitHubDelivery("/mvc-hooks/other", "{}"), Token);

        Assert.Equal("github:issues:{\"n\":1}", await ok.Content.ReadAsStringAsync(Token));
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        Assert.Equal("other", await other.Content.ReadAsStringAsync(Token));
        Assert.Equal(HttpStatusCode.Unauthorized, otherWithClassSecret.StatusCode);
    }

    [Fact]
    public async Task Endpoints_carry_the_webhook_metadata()
    {
        await using var server = await StartAsync(
            app =>
            {
                app.MapPost("/a", (HttpContext _) => Task.CompletedTask).RequireWebhookSignature("github");
                app.MapPost("/b", (HttpContext _) => Task.CompletedTask).WithMetadata(new RequireWebhookSignatureAttribute("stripe"));
            }
        );

        var endpoints = server.App.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();

        Assert.Equal("github", endpoints.Single(e => e.RoutePattern.RawText == "/a").Metadata.GetMetadata<IRequireWebhookSignatureMetadata>()?.VerifierName);
        Assert.Equal("stripe", endpoints.Single(e => e.RoutePattern.RawText == "/b").Metadata.GetMetadata<IRequireWebhookSignatureMetadata>()?.VerifierName);
    }
}
