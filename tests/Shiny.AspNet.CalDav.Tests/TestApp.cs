using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Shiny.AspNet.CalDav.Tests;

/// <summary>
/// An ASP.NET Core app with a client in front of it - on Kestrel over real sockets by default, with
/// one HTTP/1.1 and one cleartext HTTP/2 listener (Kestrel only speaks h2c on an HTTP/2-only
/// endpoint), or in memory on <see cref="TestServer"/>.
/// </summary>
sealed class TestApp : IAsyncDisposable
{
    readonly WebApplication app;

    TestApp(WebApplication app, HttpClient client, int port, int http2Port)
    {
        this.app = app;
        this.Client = client;
        this.Port = port;
        this.Http2Port = http2Port;
    }

    public HttpClient Client { get; }

    public IServiceProvider Services => this.app.Services;

    /// <summary>The HTTP/1.1 listener's port, or 0 in memory.</summary>
    public int Port { get; }

    /// <summary>The HTTP/2 listener's port, or 0 in memory.</summary>
    public int Http2Port { get; }

    public static Task<TestApp> StartAsync(
        Action<WebApplication> configure,
        Action<IServiceCollection>? configureServices = null
    ) => StartAsync(configure, configureServices, inMemory: false);

    public static Task<TestApp> StartInMemoryAsync(
        Action<WebApplication> configure,
        Action<IServiceCollection>? configureServices = null
    ) => StartAsync(configure, configureServices, inMemory: true);

    static async Task<TestApp> StartAsync(
        Action<WebApplication> configure,
        Action<IServiceCollection>? configureServices,
        bool inMemory
    )
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();

        int port = 0, http2Port = 0;

        if (inMemory)
        {
            builder.WebHost.UseTestServer();
        }
        else
        {
            port = FreePort();
            http2Port = FreePort();

            builder.WebHost.ConfigureKestrel(kestrel =>
            {
                kestrel.Listen(IPAddress.Loopback, port, l => l.Protocols = HttpProtocols.Http1);
                kestrel.Listen(IPAddress.Loopback, http2Port, l => l.Protocols = HttpProtocols.Http2);
            });
        }

        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        configure(app);

        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = inMemory
            ? app.GetTestClient()
            : new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(30) };

        return new TestApp(app, client, port, http2Port);
    }

    static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async ValueTask DisposeAsync()
    {
        this.Client.Dispose();
        await this.app.StopAsync();
        await this.app.DisposeAsync();
    }
}

/// <summary>
/// Basic authentication against a fixed user list - ASP.NET ships no Basic handler, and these tests
/// only need one that says yes or no.
/// </summary>
sealed class BasicTestOptions : AuthenticationSchemeOptions
{
    public Dictionary<string, string> Users { get; } = new(StringComparer.Ordinal);

    public BasicTestOptions AddUser(string name, string password)
    {
        this.Users[name] = password;
        return this;
    }
}

sealed class BasicTestHandler(IOptionsMonitor<BasicTestOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<BasicTestOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!AuthenticationHeaderValue.TryParse(this.Request.Headers.Authorization, out var header) ||
            !header.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase) ||
            header.Parameter is null)
            return Task.FromResult(AuthenticateResult.NoResult());

        var pair = Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter)).Split(':', 2);

        if (pair.Length != 2 || !this.Options.Users.TryGetValue(pair[0], out var password) || password != pair[1])
            return Task.FromResult(AuthenticateResult.Fail("Bad credentials."));

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, pair[0])], this.Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), this.Scheme.Name)
        ));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        this.Response.StatusCode = StatusCodes.Status401Unauthorized;
        this.Response.Headers.WWWAuthenticate = "Basic realm=\"test\"";
        return Task.CompletedTask;
    }
}

static class BasicTestExtensions
{
    public static AuthenticationBuilder AddAuthenticationWithBasic(this IServiceCollection services, Action<BasicTestOptions> configure)
        => services
            .AddAuthentication("Basic")
            .AddScheme<BasicTestOptions, BasicTestHandler>("Basic", configure);
}

/// <summary>A temporary content directory, torn down with the test.</summary>
sealed class ContentRoot : IDisposable
{
    public ContentRoot()
    {
        this.Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "shiny-caldav-" + Guid.NewGuid().ToString("n")[..8]
        );

        Directory.CreateDirectory(this.Path);
    }

    public string Path { get; }

    public ContentRoot With(string relativePath, string content)
    {
        var full = System.IO.Path.Combine(this.Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);

        return this;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(this.Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
