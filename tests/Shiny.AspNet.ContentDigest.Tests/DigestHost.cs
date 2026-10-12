using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Shiny.AspNet.ContentDigest.Tests;

/// <summary>
/// Kestrel on an ephemeral loopback port with a real <see cref="HttpClient"/> in front of it.
/// <para>
/// Deliberately not <c>TestServer</c>: framing, chunked bodies and trailers only exist at the socket
/// boundary, and that is most of what a digest has to get right.
/// </para>
/// </summary>
sealed class DigestHost : IAsyncDisposable
{
    readonly WebApplication app;

    DigestHost(WebApplication app, int port)
    {
        this.app = app;
        this.Port = port;
        this.Client = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}"),
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public HttpClient Client { get; }

    public int Port { get; }

    /// <param name="configure">The pipeline and endpoints.</param>
    /// <param name="services">Service registrations — <c>AddContentDigest</c> and anything else.</param>
    /// <param name="http2">Listen for HTTP/2 with prior knowledge (h2c) instead of HTTP/1.1.</param>
    public static async Task<DigestHost> StartAsync(
        Action<WebApplication> configure,
        Action<IServiceCollection>? services = null,
        bool http2 = false
    )
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = http2 ? HttpProtocols.Http2 : HttpProtocols.Http1));
        services?.Invoke(builder.Services);

        var app = builder.Build();
        configure(app);
        await app.StartAsync(TestContext.Current.CancellationToken);

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new DigestHost(app, new Uri(address).Port);
    }

    public async ValueTask DisposeAsync()
    {
        this.Client.Dispose();
        await this.app.DisposeAsync();
    }
}
