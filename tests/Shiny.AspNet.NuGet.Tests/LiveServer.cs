using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Shiny.AspNet.NuGet.Tests;

/// <summary>
/// The app on real Kestrel, bound to free loopback ports. Real sockets rather than TestServer: the
/// clients under test bring their own HTTP stacks, and the URLs the server hands out have to be
/// ones those stacks can reach. <see cref="Address"/> speaks HTTP/1.1; <see cref="Http2Address"/>
/// speaks cleartext HTTP/2 with prior knowledge, which Kestrel only accepts on an HTTP/2-only
/// endpoint.
/// </summary>
sealed class LiveServer : IAsyncDisposable
{
    readonly WebApplication app;

    LiveServer(WebApplication app, Uri address, Uri http2Address)
    {
        this.app = app;
        this.Address = address;
        this.Http2Address = http2Address;
        this.Client = new HttpClient { BaseAddress = address };
    }

    public Uri Address { get; }

    public Uri Http2Address { get; }

    public int Port => this.Address.Port;

    public HttpClient Client { get; }

    public static async Task<LiveServer> StartAsync(Action<WebApplication> configure)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(o =>
        {
            o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1);
            o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http2);
        });

        var app = builder.Build();
        configure(app);
        await app.StartAsync(TestContext.Current.CancellationToken);

        // In the order the endpoints were configured.
        var addresses = app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses
            .Select(a => new Uri(a))
            .ToList();

        return new LiveServer(app, addresses[0], addresses[1]);
    }

    public async ValueTask DisposeAsync()
    {
        this.Client.Dispose();
        await this.app.StopAsync();
        await this.app.DisposeAsync();
    }
}
