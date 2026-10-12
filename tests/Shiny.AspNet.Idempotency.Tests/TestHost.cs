using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Shiny.AspNet.Idempotency.Tests;

/// <summary>A WebApplication on TestServer, or on Kestrel when a real protocol stack is needed.</summary>
sealed class TestHost : IAsyncDisposable
{
    readonly WebApplication app;

    TestHost(WebApplication app, HttpClient client, Uri? address)
    {
        this.app = app;
        this.Client = client;
        this.Address = address;
    }

    public HttpClient Client { get; }

    /// <summary>The Kestrel address, when started on Kestrel.</summary>
    public Uri? Address { get; }

    public static async Task<TestHost> StartAsync(
        Action<WebApplication> configure,
        Action<IServiceCollection>? services = null,
        Action<KestrelServerOptions>? kestrel = null
    )
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();

        if (kestrel is null)
            builder.WebHost.UseTestServer();
        else
            builder.WebHost.UseKestrel(kestrel);

        if (services is null)
            builder.Services.AddIdempotency();
        else
            services(builder.Services);

        var app = builder.Build();

        // Stands in for an exception handler: an unhandled failure becomes a 500, as on a real server,
        // and a body that failed to read becomes the 400 Kestrel would give it.
        app.Use(async (ctx, next) =>
        {
            try
            {
                await next(ctx);
            }
            catch (Microsoft.AspNetCore.Http.BadHttpRequestException ex) when (!ctx.Response.HasStarted)
            {
                ctx.Response.Clear();
                ctx.Response.StatusCode = ex.StatusCode;
            }
            catch (Exception) when (!ctx.Response.HasStarted)
            {
                ctx.Response.Clear();
                ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            }
        });

        configure(app);
        await app.StartAsync(TestContext.Current.CancellationToken);

        if (kestrel is null)
            return new TestHost(app, app.GetTestClient(), null);

        var address = new Uri(app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.First());
        return new TestHost(app, new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(30) }, address);
    }

    public static Action<KestrelServerOptions> Http2Only
        => k => k.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http2);

    public async ValueTask DisposeAsync()
    {
        this.Client.Dispose();
        await this.app.DisposeAsync();
    }
}
