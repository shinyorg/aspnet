using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Shiny.AspNet.FileSync.Tests;

/// <summary>
/// A real Kestrel on loopback port 0 - the sync client needs a real server URI to talk to
/// - or, with <c>inMemory</c>, a <see cref="TestServer"/>.
/// Kestrel listens twice: HTTP/1.1 on <see cref="Port"/>, HTTP/2 with prior knowledge (h2c) on
/// <see cref="Http2Port"/>.
/// </summary>
sealed class TestHost : IAsyncDisposable
{
    readonly WebApplication app;

    TestHost(WebApplication app, HttpClient client, int port, int http2Port)
    {
        this.app = app;
        this.Client = client;
        this.Port = port;
        this.Http2Port = http2Port;
    }

    public HttpClient Client { get; }

    public int Port { get; }

    public int Http2Port { get; }

    public IServiceProvider Services => this.app.Services;

    public static async Task<TestHost> StartAsync(
        Action<WebApplication> configure,
        Action<WebApplicationBuilder>? builder = null,
        bool inMemory = false
    )
    {
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();

        if (inMemory)
        {
            b.WebHost.UseTestServer();
        }
        else
        {
            b.WebHost.ConfigureKestrel(k =>
            {
                k.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1);
                k.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http2);
            });
        }

        builder?.Invoke(b);

        var app = b.Build();
        configure(app);
        await app.StartAsync(TestContext.Current.CancellationToken);

        if (inMemory)
            return new TestHost(app, app.GetTestClient(), 80, 80);

        var ports = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
            .Addresses
            .Select(a => new Uri(a).Port)
            .ToArray();

        var client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{ports[0]}"),
            Timeout = TimeSpan.FromSeconds(30)
        };

        return new TestHost(app, client, ports[0], ports[1]);
    }

    public async ValueTask DisposeAsync()
    {
        this.Client.Dispose();
        await this.app.StopAsync(CancellationToken.None);
        await this.app.DisposeAsync();
    }
}
