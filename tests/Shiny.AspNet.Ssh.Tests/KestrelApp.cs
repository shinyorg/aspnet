using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Shiny.AspNet.Ssh.Tests;

/// <summary>A real Kestrel app on an ephemeral loopback port — tunnels forward to real sockets, so TestServer will not do.</summary>
sealed class KestrelApp : IAsyncDisposable
{
    KestrelApp(WebApplication app) => this.App = app;

    public WebApplication App { get; }

    public IServer Server => this.App.Services.GetRequiredService<IServer>();

    public int Port => new Uri(this.Server.Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;

    public static async Task<KestrelApp> StartAsync(
        Action<WebApplication>? map = null,
        Action<WebApplicationBuilder>? configure = null,
        string url = "http://127.0.0.1:0"
    )
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(url);
        builder.Logging.ClearProviders();
        configure?.Invoke(builder);

        var app = builder.Build();
        map?.Invoke(app);

        await app.StartAsync(TestContext.Current.CancellationToken);
        return new KestrelApp(app);
    }

    public async ValueTask DisposeAsync()
    {
        await this.App.StopAsync(CancellationToken.None);
        await this.App.DisposeAsync();
    }
}
