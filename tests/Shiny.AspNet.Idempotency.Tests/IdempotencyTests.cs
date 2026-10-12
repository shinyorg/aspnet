using System.Net;
using System.Net.Quic;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Shiny.AspNet.Idempotency.Tests;

// ---------------------------------------------------------------------------
// An MVC controller opting in with [Idempotent] — the ASP.NET counterpart of
// upstream's generated endpoint classes: the attribute becomes endpoint metadata
// and the middleware reads it like any convention-added metadata.
// ---------------------------------------------------------------------------

[ApiController]
[Route("api/orders")]
[Idempotent]
public class IdempotentOrdersController : ControllerBase
{
    public static int Placed;

    [HttpPost]
    public string Place() => "order " + Interlocked.Increment(ref Placed);

    [HttpPost("preview")]
    [DisableIdempotency]
    public string Preview() => "preview " + Interlocked.Increment(ref Placed);

    [HttpPost("optional")]
    [Idempotent(Required = false)]
    public string Optional() => "optional " + Interlocked.Increment(ref Placed);
}

public class IdempotencyTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A charge endpoint that counts how many times it really ran.</summary>
    sealed class Ledger
    {
        int charges;

        public int Charges => Volatile.Read(ref this.charges);

        public async Task ChargeAsync(HttpContext ctx)
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync(ctx.RequestAborted);
            var number = Interlocked.Increment(ref this.charges);

            ctx.Response.StatusCode = StatusCodes.Status201Created;
            ctx.Response.Headers["X-Charge"] = number.ToString();
            await ctx.Response.WriteAsync($"charge {number} for {body}", ctx.RequestAborted);
        }
    }

    static Task<TestHost> StartAsync(Action<WebApplication> map, Action<IdempotencyOptions>? configure = null)
        => TestHost.StartAsync(
            app =>
            {
                app.UseIdempotency();
                map(app);
            },
            services => services.AddIdempotency(configure)
        );

    static HttpRequestMessage Post(string path, string? key, string body = "10.00")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body) };

        if (key is not null)
            request.Headers.TryAddWithoutValidation(IdempotencyHeaders.IdempotencyKey, key);

        return request;
    }

    [Fact]
    public async Task A_retry_with_the_same_key_gets_the_stored_response_and_the_handler_runs_once()
    {
        var ledger = new Ledger();
        await using var server = await StartAsync(app => app.MapPost("/payments", ledger.ChargeAsync).RequireIdempotencyKey());

        var first = await server.Client.SendAsync(Post("/payments", "key-1"), Token);
        var retry = await server.Client.SendAsync(Post("/payments", "key-1"), Token);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(Token), await retry.Content.ReadAsStringAsync(Token));
        Assert.Equal("1", retry.Headers.GetValues("X-Charge").Single());
        Assert.Equal("true", retry.Headers.GetValues(IdempotencyHeaders.IdempotentReplayed).Single());
        Assert.False(first.Headers.Contains(IdempotencyHeaders.IdempotentReplayed));
        Assert.Equal(1, ledger.Charges);
    }

    [Fact]
    public async Task A_quoted_structured_field_key_is_the_same_key_as_the_bare_one()
    {
        var ledger = new Ledger();
        await using var server = await StartAsync(app => app.MapPost("/payments", ledger.ChargeAsync).RequireIdempotencyKey());

        await server.Client.SendAsync(Post("/payments", "\"8e03978e-40d5-43e8-bc93-6894a57f9324\""), Token);
        var retry = await server.Client.SendAsync(Post("/payments", "8e03978e-40d5-43e8-bc93-6894a57f9324"), Token);

        Assert.True(retry.Headers.Contains(IdempotencyHeaders.IdempotentReplayed));
        Assert.Equal(1, ledger.Charges);
    }

    [Fact]
    public async Task Different_keys_are_different_requests()
    {
        var ledger = new Ledger();
        await using var server = await StartAsync(app => app.MapPost("/payments", ledger.ChargeAsync).RequireIdempotencyKey());

        await server.Client.SendAsync(Post("/payments", "a"), Token);
        await server.Client.SendAsync(Post("/payments", "b"), Token);

        Assert.Equal(2, ledger.Charges);
    }

    [Fact]
    public async Task Reusing_a_key_with_a_different_body_is_refused_with_422()
    {
        var ledger = new Ledger();
        await using var server = await StartAsync(app => app.MapPost("/payments", ledger.ChargeAsync).RequireIdempotencyKey());

        await server.Client.SendAsync(Post("/payments", "key-1", "10.00"), Token);
        var reuse = await server.Client.SendAsync(Post("/payments", "key-1", "99.00"), Token);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, reuse.StatusCode);
        Assert.Equal("application/problem+json", reuse.Content.Headers.ContentType?.MediaType);
        Assert.Equal(1, ledger.Charges);
    }

    [Fact]
    public async Task A_duplicate_that_arrives_while_the_first_is_still_running_gets_409()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;

        await using var server = await StartAsync(app => app.MapPost("/slow", async (HttpContext ctx) =>
        {
            Interlocked.Increment(ref runs);
            entered.TrySetResult();
            await release.Task;
            await ctx.Response.WriteAsync("done", ctx.RequestAborted);
        }).RequireIdempotencyKey());

        var first = server.Client.SendAsync(Post("/slow", "key-1"), Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

        var duplicate = await server.Client.SendAsync(Post("/slow", "key-1"), Token);

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("application/problem+json", duplicate.Content.Headers.ContentType?.MediaType);
        Assert.True(duplicate.Headers.Contains("Retry-After"));

        release.SetResult();
        Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);

        // Once the first has finished, the same retry is answered with its response.
        var later = await server.Client.SendAsync(Post("/slow", "key-1"), Token);
        Assert.Equal("done", await later.Content.ReadAsStringAsync(Token));
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task A_missing_key_on_an_endpoint_that_requires_one_is_a_400_problem()
    {
        var ledger = new Ledger();
        await using var server = await StartAsync(app => app.MapPost("/payments", ledger.ChargeAsync).RequireIdempotencyKey());

        var response = await server.Client.SendAsync(Post("/payments", key: null), Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Idempotency-Key", await response.Content.ReadAsStringAsync(Token));
        Assert.Equal(0, ledger.Charges);
    }

    [Fact]
    public async Task An_empty_or_oversized_key_is_a_400()
    {
        var ledger = new Ledger();
        await using var server = await StartAsync(
            app => app.MapPost("/payments", ledger.ChargeAsync).RequireIdempotencyKey(),
            o => o.MaxKeyLength = 8
        );

        Assert.Equal(HttpStatusCode.BadRequest, (await server.Client.SendAsync(Post("/payments", "\"\""), Token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await server.Client.SendAsync(Post("/payments", "123456789"), Token)).StatusCode);
        Assert.Equal(0, ledger.Charges);
    }

    [Fact]
    public async Task WithIdempotency_honours_a_key_but_lets_a_request_without_one_through()
    {
        var ledger = new Ledger();
        await using var server = await StartAsync(app => app.MapPost("/payments", ledger.ChargeAsync).WithIdempotency());

        await server.Client.SendAsync(Post("/payments", key: null), Token);
        await server.Client.SendAsync(Post("/payments", key: null), Token);
        await server.Client.SendAsync(Post("/payments", "k"), Token);
        await server.Client.SendAsync(Post("/payments", "k"), Token);

        Assert.Equal(3, ledger.Charges);
    }

    [Fact]
    public async Task A_route_that_did_not_opt_in_ignores_the_key()
    {
        var ledger = new Ledger();
        await using var server = await StartAsync(app => app.MapPost("/payments", ledger.ChargeAsync));

        await server.Client.SendAsync(Post("/payments", "k"), Token);
        await server.Client.SendAsync(Post("/payments", "k"), Token);

        Assert.Equal(2, ledger.Charges);
    }

    [Fact]
    public async Task A_server_error_is_not_stored_so_the_retry_runs_again()
    {
        var runs = 0;
        await using var server = await StartAsync(app => app.MapPost("/flaky", async (HttpContext ctx) =>
        {
            if (Interlocked.Increment(ref runs) == 1)
            {
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            await ctx.Response.WriteAsync("ok", ctx.RequestAborted);
        }).RequireIdempotencyKey());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await server.Client.SendAsync(Post("/flaky", "k"), Token)).StatusCode);

        var retry = await server.Client.SendAsync(Post("/flaky", "k"), Token);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal("ok", await retry.Content.ReadAsStringAsync(Token));

        // ...and the success is what the next retry gets.
        var again = await server.Client.SendAsync(Post("/flaky", "k"), Token);
        Assert.True(again.Headers.Contains(IdempotencyHeaders.IdempotentReplayed));
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task A_handler_that_throws_releases_the_key()
    {
        var runs = 0;
        await using var server = await StartAsync(app => app.MapPost("/throws", async (HttpContext ctx) =>
        {
            if (Interlocked.Increment(ref runs) == 1)
                throw new InvalidOperationException("boom");

            await ctx.Response.WriteAsync("ok", ctx.RequestAborted);
        }).RequireIdempotencyKey());

        Assert.Equal(HttpStatusCode.InternalServerError, (await server.Client.SendAsync(Post("/throws", "k"), Token)).StatusCode);
        Assert.Equal("ok", await (await server.Client.SendAsync(Post("/throws", "k"), Token)).Content.ReadAsStringAsync(Token));
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task Client_errors_are_stored_by_default_and_the_status_filter_is_configurable()
    {
        var runs = 0;
        RequestDelegate reject = ctx =>
        {
            Interlocked.Increment(ref runs);
            ctx.Response.StatusCode = 402;
            return Task.CompletedTask;
        };

        await using (var server = await StartAsync(app => app.MapPost("/declined", reject).RequireIdempotencyKey()))
        {
            await server.Client.SendAsync(Post("/declined", "k"), Token);
            var retry = await server.Client.SendAsync(Post("/declined", "k"), Token);

            Assert.Equal(HttpStatusCode.PaymentRequired, retry.StatusCode);
            Assert.Equal(1, runs);
        }

        runs = 0;

        await using (var server = await StartAsync(
            app => app.MapPost("/declined", reject).RequireIdempotencyKey(),
            o => o.ShouldStoreStatusCode = status => status is >= 200 and < 300
        ))
        {
            await server.Client.SendAsync(Post("/declined", "k"), Token);
            await server.Client.SendAsync(Post("/declined", "k"), Token);

            Assert.Equal(2, runs);
        }
    }

    [Fact]
    public async Task A_key_is_scoped_to_the_path()
    {
        var ledger = new Ledger();
        await using var server = await StartAsync(app =>
        {
            app.MapPost("/payments", ledger.ChargeAsync).RequireIdempotencyKey();
            app.MapPost("/refunds", ledger.ChargeAsync).RequireIdempotencyKey();
        });

        await server.Client.SendAsync(Post("/payments", "shared"), Token);
        var other = await server.Client.SendAsync(Post("/refunds", "shared"), Token);

        Assert.False(other.Headers.Contains(IdempotencyHeaders.IdempotentReplayed));
        Assert.Equal(2, ledger.Charges);
    }

    [Fact]
    public async Task A_key_is_scoped_to_the_caller_so_one_user_never_sees_another_users_reply()
    {
        var ledger = new Ledger();
        await using var server = await TestHost.StartAsync(app =>
        {
            // Stands in for authentication: whoever X-User names is the caller.
            app.Use((ctx, next) =>
            {
                if (ctx.Request.Headers["X-User"].ToString() is { Length: > 0 } user)
                    ctx.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user)], "test"));

                return next(ctx);
            });

            app.UseIdempotency();
            app.MapPost("/payments", ledger.ChargeAsync).RequireIdempotencyKey();
        });

        HttpRequestMessage As(string user)
        {
            var request = Post("/payments", "same-key");
            request.Headers.Add("X-User", user);
            return request;
        }

        await server.Client.SendAsync(As("alice"), Token);
        var bob = await server.Client.SendAsync(As("bob"), Token);
        var aliceAgain = await server.Client.SendAsync(As("alice"), Token);

        Assert.False(bob.Headers.Contains(IdempotencyHeaders.IdempotentReplayed));
        Assert.True(aliceAgain.Headers.Contains(IdempotencyHeaders.IdempotentReplayed));
        Assert.Equal(2, ledger.Charges);
    }

    [Fact]
    public async Task Get_is_never_intercepted_even_on_an_opted_in_route()
    {
        var runs = 0;
        await using var server = await StartAsync(app => app.MapGet("/read", (HttpContext ctx) =>
        {
            Interlocked.Increment(ref runs);
            return ctx.Response.WriteAsync("x", ctx.RequestAborted);
        }).RequireIdempotencyKey());

        // No key and not refused: the method is idempotent already.
        Assert.Equal(HttpStatusCode.OK, (await server.Client.GetAsync("/read", Token)).StatusCode);
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task A_stored_response_expires()
    {
        var ledger = new Ledger();
        await using var server = await StartAsync(
            app => app.MapPost("/payments", ledger.ChargeAsync).RequireIdempotencyKey(TimeSpan.FromMilliseconds(150))
        );

        await server.Client.SendAsync(Post("/payments", "k"), Token);
        await Task.Delay(400, Token);
        await server.Client.SendAsync(Post("/payments", "k"), Token);

        Assert.Equal(2, ledger.Charges);
    }

    [Fact]
    public async Task A_request_body_over_the_limit_is_refused_with_413_and_claims_nothing()
    {
        var ledger = new Ledger();
        await using var server = await StartAsync(
            app => app.MapPost("/payments", ledger.ChargeAsync).RequireIdempotencyKey(),
            o => o.MaxRequestBodyBytes = 16
        );

        var tooBig = await server.Client.SendAsync(Post("/payments", "k", new string('x', 100)), Token);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooBig.StatusCode);

        // The corrected retry is not locked out by the refused one.
        var fixedUp = await server.Client.SendAsync(Post("/payments", "k", "small"), Token);
        Assert.Equal(HttpStatusCode.Created, fixedUp.StatusCode);
    }

    [Fact]
    public async Task A_response_too_large_to_store_is_served_and_the_key_released()
    {
        var runs = 0;
        await using var server = await StartAsync(
            app => app.MapPost("/big", (HttpContext ctx) =>
            {
                Interlocked.Increment(ref runs);
                return ctx.Response.WriteAsync(new string('y', 1000), ctx.RequestAborted);
            }).RequireIdempotencyKey(),
            o => o.MaxResponseBodyBytes = 100
        );

        var first = await server.Client.SendAsync(Post("/big", "k"), Token);
        Assert.Equal(1000, (await first.Content.ReadAsStringAsync(Token)).Length);

        await server.Client.SendAsync(Post("/big", "k"), Token);
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task A_group_convention_covers_every_route_and_one_route_can_opt_out()
    {
        var ledger = new Ledger();
        await using var server = await StartAsync(app =>
        {
            var payments = app.MapGroup("/api").RequireIdempotencyKey();

            payments.MapPost("/pay", ledger.ChargeAsync);
            payments.MapGroup("/nested").MapPost("/pay", ledger.ChargeAsync);
            payments.MapPost("/quote", ledger.ChargeAsync).DisableIdempotency();
        });

        Assert.Equal(HttpStatusCode.BadRequest, (await server.Client.SendAsync(Post("/api/pay", null), Token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await server.Client.SendAsync(Post("/api/nested/pay", null), Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await server.Client.SendAsync(Post("/api/quote", null), Token)).StatusCode);
    }

    [Fact]
    public async Task Controllers_honour_the_attribute_and_an_action_overrides_its_controller()
    {
        await using var server = await TestHost.StartAsync(
            app =>
            {
                app.UseIdempotency();
                app.MapControllers();
            },
            services =>
            {
                services.AddIdempotency();
                services.AddControllers().AddApplicationPart(typeof(IdempotentOrdersController).Assembly);
            }
        );

        Assert.Equal(HttpStatusCode.BadRequest, (await server.Client.SendAsync(Post("/api/orders", null), Token)).StatusCode);

        var first = await (await server.Client.SendAsync(Post("/api/orders", "g1"), Token)).Content.ReadAsStringAsync(Token);
        var retry = await server.Client.SendAsync(Post("/api/orders", "g1"), Token);

        Assert.Equal(first, await retry.Content.ReadAsStringAsync(Token));
        Assert.True(retry.Headers.Contains(IdempotencyHeaders.IdempotentReplayed));

        // [DisableIdempotency] beats the controller, and Required = false lets a keyless call through.
        Assert.Equal(HttpStatusCode.OK, (await server.Client.SendAsync(Post("/api/orders/preview", null), Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await server.Client.SendAsync(Post("/api/orders/optional", null), Token)).StatusCode);
    }

    [Fact]
    public async Task The_attribute_works_as_minimal_api_metadata()
    {
        var ledger = new Ledger();
        await using var server = await StartAsync(app => app.MapPost("/payments", ledger.ChargeAsync).WithMetadata(new IdempotentAttribute()));

        Assert.Equal(HttpStatusCode.BadRequest, (await server.Client.SendAsync(Post("/payments", null), Token)).StatusCode);

        await server.Client.SendAsync(Post("/payments", "m"), Token);
        var retry = await server.Client.SendAsync(Post("/payments", "m"), Token);

        Assert.True(retry.Headers.Contains(IdempotencyHeaders.IdempotentReplayed));
        Assert.Equal(1, ledger.Charges);
    }

    [Fact]
    public async Task A_body_written_through_BodyWriter_is_captured_and_replayed_with_a_content_length()
    {
        var runs = 0;
        await using var server = await StartAsync(app => app.MapPost("/json", async (HttpContext ctx) =>
        {
            Interlocked.Increment(ref runs);
            await ctx.Response.WriteAsJsonAsync(new Receipt(runs), ReceiptContext.Default.Receipt, cancellationToken: ctx.RequestAborted);
        }).RequireIdempotencyKey());

        var first = await server.Client.SendAsync(Post("/json", "j"), Token);
        var retry = await server.Client.SendAsync(Post("/json", "j"), Token);

        var body = await first.Content.ReadAsStringAsync(Token);
        Assert.Equal(body, await retry.Content.ReadAsStringAsync(Token));
        Assert.Equal("application/json", retry.Content.Headers.ContentType?.MediaType);
        // The replay knows its length; the original went out however the handler framed it.
        Assert.Equal(Encoding.UTF8.GetByteCount(body), retry.Content.Headers.ContentLength);
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task A_sent_file_is_captured_and_replayed()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, "file contents", Token);

        try
        {
            var runs = 0;
            await using var server = await StartAsync(app => app.MapPost("/file", (HttpContext ctx) =>
            {
                Interlocked.Increment(ref runs);
                return ctx.Response.SendFileAsync(path, ctx.RequestAborted);
            }).RequireIdempotencyKey());

            await server.Client.SendAsync(Post("/file", "f"), Token);
            var retry = await server.Client.SendAsync(Post("/file", "f"), Token);

            Assert.Equal("file contents", await retry.Content.ReadAsStringAsync(Token));
            Assert.True(retry.Headers.Contains(IdempotencyHeaders.IdempotentReplayed));
            Assert.Equal(1, runs);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_response_the_handler_started_itself_streams_and_is_still_replayed()
    {
        var runs = 0;
        var sawStarted = false;

        await using var server = await StartAsync(app => app.MapPost("/stream", async (HttpContext ctx) =>
        {
            Interlocked.Increment(ref runs);
            await ctx.Response.StartAsync(ctx.RequestAborted);
            sawStarted = ctx.Response.HasStarted;
            await ctx.Response.WriteAsync("streamed", ctx.RequestAborted);
        }).RequireIdempotencyKey());

        var first = await server.Client.SendAsync(Post("/stream", "s"), Token);
        Assert.Equal("streamed", await first.Content.ReadAsStringAsync(Token));
        Assert.True(sawStarted);

        // Streamed as it was written — the bytes were copied on the way past, not held back — and so
        // still replayable.
        var retry = await server.Client.SendAsync(Post("/stream", "s"), Token);
        Assert.Equal("streamed", await retry.Content.ReadAsStringAsync(Token));
        Assert.True(retry.Headers.Contains(IdempotencyHeaders.IdempotentReplayed));
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task Http2_replays_the_stored_response()
    {
        var ledger = new Ledger();
        await using var server = await TestHost.StartAsync(
            app =>
            {
                app.UseIdempotency();
                app.MapPost("/payments", ledger.ChargeAsync).RequireIdempotencyKey();
            },
            kestrel: TestHost.Http2Only
        );

        using var client = CreateHttp2Client(server.Address!);

        var first = await client.SendAsync(H2(Post("/payments", "h2")), Token);
        var retry = await client.SendAsync(H2(Post("/payments", "h2")), Token);

        Assert.Equal(HttpVersion.Version20, retry.Version);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(Token), await retry.Content.ReadAsStringAsync(Token));
        Assert.True(retry.Headers.Contains(IdempotencyHeaders.IdempotentReplayed));
        Assert.Equal(1, ledger.Charges);
    }

    [Fact]
    public async Task Http2_concurrent_duplicates_on_one_connection_run_the_handler_once()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;

        await using var server = await TestHost.StartAsync(
            app =>
            {
                app.UseIdempotency();
                app.MapPost("/slow", async (HttpContext ctx) =>
                {
                    Interlocked.Increment(ref runs);
                    await release.Task;
                    await ctx.Response.WriteAsync("done", ctx.RequestAborted);
                }).RequireIdempotencyKey();
            },
            kestrel: TestHost.Http2Only
        );

        using var client = CreateHttp2Client(server.Address!);

        // Multiplexed onto one connection: the case HTTP/1.1 cannot even produce.
        var requests = Enumerable.Range(0, 5).Select(_ => client.SendAsync(H2(Post("/slow", "same")), Token)).ToArray();

        await WaitUntilAsync(() => Volatile.Read(ref runs) == 1);
        await Task.Delay(200, Token);
        release.SetResult();

        var responses = await Task.WhenAll(requests);
        var statuses = responses.Select(r => r.StatusCode).ToList();

        Assert.All(responses, r => Assert.Equal(HttpVersion.Version20, r.Version));
        Assert.Equal(1, runs);
        Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.OK));
        Assert.Equal(4, statuses.Count(s => s == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task Http3_replays_the_stored_response()
    {
#pragma warning disable CA1416 // guarded by QuicListener.IsSupported
        Assert.SkipUnless(QuicListener.IsSupported, "QUIC is not supported on this platform.");

        using var certificate = CreateCertificate();
        var ledger = new Ledger();

        await using var server = await TestHost.StartAsync(
            app =>
            {
                app.UseIdempotency();
                app.MapPost("/payments", ledger.ChargeAsync).RequireIdempotencyKey();
            },
            kestrel: k => k.Listen(IPAddress.Loopback, 0, l =>
            {
                l.Protocols = HttpProtocols.Http3;
                l.UseHttps(certificate);
            })
        );
#pragma warning restore CA1416

        using var client = new HttpClient(new SocketsHttpHandler
        {
            SslOptions = { RemoteCertificateValidationCallback = (_, cert, _, _) => cert?.GetCertHashString() == certificate.GetCertHashString() }
        })
        {
            BaseAddress = new Uri($"https://127.0.0.1:{server.Address!.Port}"),
            Timeout = TimeSpan.FromSeconds(30)
        };

        static HttpRequestMessage H3(HttpRequestMessage request)
        {
            request.Version = HttpVersion.Version30;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            return request;
        }

        var first = await client.SendAsync(H3(Post("/payments", "h3")), Token);
        var retry = await client.SendAsync(H3(Post("/payments", "h3")), Token);

        Assert.Equal(HttpVersion.Version30, retry.Version);
        Assert.Equal(await first.Content.ReadAsStringAsync(Token), await retry.Content.ReadAsStringAsync(Token));
        Assert.True(retry.Headers.Contains(IdempotencyHeaders.IdempotentReplayed));
        Assert.Equal(1, ledger.Charges);
    }

    [Fact]
    public async Task A_body_that_fails_to_read_claims_no_key()
    {
        // Upstream proves this with its own Content-Digest middleware. Here a stand-in does what any
        // verifying body wrapper does: throw BadHttpRequestException at the end of a corrupt body.
        var ledger = new Ledger();
        await using var server = await TestHost.StartAsync(app =>
        {
            app.Use((ctx, next) =>
            {
                if (ctx.Request.Headers.ContainsKey("X-Corrupt"))
                    ctx.Request.Body = new FailingAtEndStream(ctx.Request.Body);

                return next(ctx);
            });

            app.UseIdempotency();
            app.MapPost("/payments", ledger.ChargeAsync).RequireIdempotencyKey();
        });

        var corrupt = Post("/payments", "k");
        corrupt.Headers.Add("X-Corrupt", "1");

        // Truncated in transit: refused, and the key is left free for the intact retry.
        Assert.Equal(HttpStatusCode.BadRequest, (await server.Client.SendAsync(corrupt, Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await server.Client.SendAsync(Post("/payments", "k"), Token)).StatusCode);
        Assert.Equal(1, ledger.Charges);
    }

    [Fact]
    public async Task Memory_store_reserves_atomically_and_never_evicts_a_claim_in_flight()
    {
        var store = new MemoryIdempotencyStore(maxBytes: 10);

        Assert.Null(await store.TryReserveAsync("in-flight", "fp", TimeSpan.FromMinutes(1), Token));
        Assert.True((await store.TryReserveAsync("in-flight", "fp", TimeSpan.FromMinutes(1), Token))!.InFlight);

        // Two completed records well over the byte budget: the older goes, the claim stays.
        foreach (var key in new[] { "old", "new" })
        {
            Assert.Null(await store.TryReserveAsync(key, "fp", TimeSpan.FromMinutes(1), Token));
            var response = new IdempotentResponse(200, [], new byte[8], DateTimeOffset.UtcNow);
            await store.CompleteAsync(key, new IdempotencyRecord("fp", response, DateTimeOffset.UtcNow.AddHours(1)), Token);
            await Task.Delay(5, Token);
        }

        Assert.NotNull(await store.TryReserveAsync("in-flight", "fp", TimeSpan.FromMinutes(1), Token));
        Assert.NotNull(await store.TryReserveAsync("new", "fp", TimeSpan.FromMinutes(1), Token));
        Assert.Null(await store.TryReserveAsync("old", "fp", TimeSpan.FromMinutes(1), Token));

        // Releasing a completed record does nothing; releasing a claim frees it.
        await store.ReleaseAsync("new", Token);
        Assert.NotNull(await store.TryReserveAsync("new", "fp", TimeSpan.FromMinutes(1), Token));

        await store.ReleaseAsync("in-flight", Token);
        Assert.Null(await store.TryReserveAsync("in-flight", "fp", TimeSpan.FromMinutes(1), Token));
    }

    [Fact]
    public async Task Memory_store_treats_an_expired_claim_as_abandoned()
    {
        var store = new MemoryIdempotencyStore();

        Assert.Null(await store.TryReserveAsync("k", "fp", TimeSpan.FromMilliseconds(50), Token));
        await Task.Delay(150, Token);

        Assert.Null(await store.TryReserveAsync("k", "fp", TimeSpan.FromMinutes(1), Token));
    }

    [Fact]
    public async Task A_second_AddIdempotency_call_composes_with_the_first()
    {
        var ledger = new Ledger();
        await using var server = await TestHost.StartAsync(
            app =>
            {
                app.UseIdempotency();
                app.MapPost("/payments", ledger.ChargeAsync).RequireIdempotencyKey();
            },
            services =>
            {
                services.AddIdempotency(o => o.ReplayedHeaderName = "X-Replayed");
                services.AddIdempotency(o => o.HeaderName = "X-Key");
            }
        );

        HttpRequestMessage Request()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/payments") { Content = new StringContent("1") };
            request.Headers.Add("X-Key", "k");
            return request;
        }

        await server.Client.SendAsync(Request(), Token);
        var retry = await server.Client.SendAsync(Request(), Token);

        Assert.Equal("true", retry.Headers.GetValues("X-Replayed").Single());
        Assert.Equal(1, ledger.Charges);
    }

    [Fact]
    public async Task UseIdempotency_without_AddIdempotency_says_what_is_missing()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => TestHost.StartAsync(
            app => app.UseIdempotency(),
            services => { }
        ));

        Assert.Contains("AddIdempotency", ex.Message);
    }

    [Fact]
    public async Task The_distributed_cache_store_replays_and_reports_in_flight_claims()
    {
        var ledger = new Ledger();
        await using var server = await TestHost.StartAsync(
            app =>
            {
                app.UseIdempotency();
                app.MapPost("/payments", ledger.ChargeAsync).RequireIdempotencyKey();
            },
            services =>
            {
                services.AddDistributedMemoryCache();
                services.AddIdempotency().AddDistributedCacheIdempotencyStore();
            }
        );

        var first = await server.Client.SendAsync(Post("/payments", "d"), Token);
        var retry = await server.Client.SendAsync(Post("/payments", "d"), Token);
        var reuse = await server.Client.SendAsync(Post("/payments", "d", "other"), Token);

        Assert.Equal(await first.Content.ReadAsStringAsync(Token), await retry.Content.ReadAsStringAsync(Token));
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal("1", retry.Headers.GetValues("X-Charge").Single());
        Assert.True(retry.Headers.Contains(IdempotencyHeaders.IdempotentReplayed));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reuse.StatusCode);
        Assert.Equal(1, ledger.Charges);
    }

    [Fact]
    public async Task Distributed_cache_store_claims_releases_and_never_releases_a_completed_record()
    {
        var cache = new Microsoft.Extensions.Caching.Distributed.MemoryDistributedCache(
            Microsoft.Extensions.Options.Options.Create(new Microsoft.Extensions.Caching.Memory.MemoryDistributedCacheOptions())
        );
        var store = new DistributedCacheIdempotencyStore(cache);

        Assert.Null(await store.TryReserveAsync("k", "fp", TimeSpan.FromMinutes(1), Token));
        Assert.True((await store.TryReserveAsync("k", "fp", TimeSpan.FromMinutes(1), Token))!.InFlight);

        await store.ReleaseAsync("k", Token);
        Assert.Null(await store.TryReserveAsync("k", "fp", TimeSpan.FromMinutes(1), Token));

        var response = new IdempotentResponse(201, [new("Content-Type", "text/plain"), new("X-A", "1"), new("X-A", "2")], [1, 2, 3], DateTimeOffset.UtcNow);
        await store.CompleteAsync("k", new IdempotencyRecord("fp", response, DateTimeOffset.UtcNow.AddHours(1)), Token);
        await store.ReleaseAsync("k", Token);

        var stored = (await store.TryReserveAsync("k", "fp", TimeSpan.FromMinutes(1), Token))!.Response!;
        Assert.Equal(201, stored.StatusCode);
        Assert.Equal(response.Headers, stored.Headers);
        Assert.Equal(response.Body, stored.Body);
        Assert.Equal(response.Created.UtcTicks, stored.Created.UtcTicks);
    }

    static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);

        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(25, Token);

        Assert.True(condition());
    }

    /// <summary>A message built by hand ignores the client's default version, so it has to say so itself.</summary>
    static HttpRequestMessage H2(HttpRequestMessage request)
    {
        request.Version = HttpVersion.Version20;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        return request;
    }

    static HttpClient CreateHttp2Client(Uri address) => new(new SocketsHttpHandler())
    {
        BaseAddress = address,
        DefaultRequestVersion = HttpVersion.Version20,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        Timeout = TimeSpan.FromSeconds(30)
    };

    static X509Certificate2 CreateCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());

        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12), null);
    }

    /// <summary>Reads through, then throws where a verifying stream would report a corrupt body.</summary>
    sealed class FailingAtEndStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            return read > 0 ? read : throw new Microsoft.AspNetCore.Http.BadHttpRequestException("The request body does not match its Content-Digest.");
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => this.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => this.ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public sealed record Receipt(int Number);

[System.Text.Json.Serialization.JsonSerializable(typeof(Receipt))]
sealed partial class ReceiptContext : System.Text.Json.Serialization.JsonSerializerContext;
