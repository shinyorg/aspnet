using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shiny.Net.Discovery;

namespace Shiny.AspNet.Discovery.Tests;

/// <summary>
/// The advertiser and the locator against a stand-in responder. What is worth testing here is the
/// wiring — that the advertisement carries the port Kestrel actually bound and follows the app's
/// lifetime, and that a browse result becomes a URL a client can actually call — not whether multicast
/// DNS works, which is the discovery library's own problem.
/// </summary>
public class ServerAdvertiserTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Advertises_the_port_the_server_actually_bound()
    {
        var mdns = new FakeMdns();
        await using var test = await TestApp.StartAsync();

        await using var advertiser = (ServerAdvertiser)await test.App.AdvertiseAsync(mdns, o => o.ServiceType = "_myapp._tcp", Token);

        var registration = Assert.Single(mdns.Published);

        Assert.NotEqual(0, test.Port);
        Assert.Equal(test.Port, registration.Port);
        Assert.Equal("_myapp._tcp", registration.ServiceType);
        Assert.Equal(Environment.MachineName, registration.InstanceName);
    }

    [Fact]
    public async Task Publishes_the_conventional_txt_records()
    {
        var mdns = new FakeMdns();
        await using var test = await TestApp.StartAsync();

        await using var advertiser = (ServerAdvertiser)await test.App.AdvertiseAsync(
            mdns,
            o =>
            {
                o.Path = "/api";
                o.TxtRecords["role"] = "controller";
            },
            Token
        );

        var records = Assert.Single(mdns.Published).TxtRecords!;

        Assert.Equal("/api", records["path"]);
        Assert.Equal("http", records["scheme"]);
        Assert.Equal("controller", records["role"]);
    }

    /// <summary>Hosted services start before Kestrel binds; publishing then would carry no port at all.</summary>
    [Fact]
    public async Task Waits_for_the_application_to_start_before_publishing()
    {
        var mdns = new FakeMdns();
        await using var test = await TestApp.CreateAsync();

        await using var advertiser = await test.App.AdvertiseAsync(mdns, cancellationToken: Token);
        Assert.Empty(mdns.Published);

        await test.App.StartAsync(Token);
        await WaitUntil(() => mdns.Published.Count == 1, Token, "the publication once the app started");

        Assert.Equal(test.Port, mdns.Published[0].Port);
    }

    [Fact]
    public async Task Withdraws_when_the_application_stops()
    {
        var mdns = new FakeMdns();
        await using var test = await TestApp.StartAsync();

        var advertiser = (ServerAdvertiser)await test.App.AdvertiseAsync(mdns, cancellationToken: Token);
        Assert.NotNull(advertiser.Publication);

        await test.App.StopAsync(Token);

        // The goodbye is what says the withdrawal finished, and the cleared property is not: the
        // advertiser drops Publication before it awaits the dispose that sends the packet, so a wait
        // on the property alone returns mid-withdrawal and races the very thing being asserted.
        await WaitUntil(() => mdns.GoodbyesSent == 1, Token, "the publication to be withdrawn");

        Assert.Null(advertiser.Publication);
        Assert.Equal(1, mdns.GoodbyesSent);
    }

    [Fact]
    public async Task Registered_in_the_container_it_follows_the_host()
    {
        var mdns = new FakeMdns();
        await using var test = await TestApp.CreateAsync(services =>
        {
            services.AddSingleton<IMdnsManager>(mdns);
            services.AddServerAdvertisement(o => o.ServiceType = "_myapp._tcp");
            services.AddServerAdvertisement(o => o.InstanceName = "Kitchen Pi");
            services.AddServerLocator();
        });

        // The app's responder stands; the library adds none of its own.
        Assert.Same(mdns, test.App.Services.GetRequiredService<IMdnsManager>());

        await test.App.StartAsync(Token);
        await WaitUntil(() => mdns.Published.Count == 1, Token, "the hosted advertisement");

        var registration = mdns.Published[0];
        Assert.Equal(test.Port, registration.Port);
        Assert.Equal("_myapp._tcp", registration.ServiceType);
        Assert.Equal("Kitchen Pi", registration.InstanceName);

        await test.App.StopAsync(Token);
        Assert.Equal(1, mdns.GoodbyesSent);
    }

    [Fact]
    public async Task Reports_the_name_the_responder_settled_on()
    {
        var mdns = new FakeMdns { RenameTo = "Kitchen Pi (2)" };
        await using var test = await TestApp.StartAsync();

        await using var advertiser = (ServerAdvertiser)await test.App.AdvertiseAsync(
            mdns,
            o => o.InstanceName = "Kitchen Pi",
            Token
        );

        Assert.Equal("Kitchen Pi (2)", advertiser.Publication!.InstanceName);
    }

    /// <summary>
    /// The moment the advertiser publishes is the moment a responder is least likely to answer — the
    /// app has just started, often at boot, and the platform's mDNS stack is coming up at its own pace.
    /// </summary>
    [Fact]
    public async Task Retries_a_registration_the_responder_refused()
    {
        var mdns = new FakeMdns { PublishFailures = 2 };
        var logger = new RecordingLogger<ServerAdvertiser>();
        await using var test = await TestApp.StartAsync();

        await using var advertiser = new ServerAdvertiser(mdns, test.Server, null, FastRetries(), logger);
        await advertiser.StartAsync(Token);

        Assert.Equal(3, mdns.PublishAttempts);
        Assert.NotNull(advertiser.Publication);
        Assert.Equal(test.Port, advertiser.Publication!.Port);
        Assert.Empty(logger.At(LogLevel.Error));
    }

    /// <summary>An app nothing on the link can find looks perfectly healthy from the inside, so it has to say so.</summary>
    [Fact]
    public async Task Says_so_at_error_when_the_advertisement_will_not_publish()
    {
        var mdns = new FakeMdns { PublishFailures = int.MaxValue };
        var logger = new RecordingLogger<ServerAdvertiser>();
        await using var test = await TestApp.StartAsync();

        await using var advertiser = new ServerAdvertiser(mdns, test.Server, null, FastRetries(), logger);
        await advertiser.StartAsync(Token);

        Assert.Equal(3, mdns.PublishAttempts);
        Assert.Null(advertiser.Publication);

        var error = Assert.Single(logger.At(LogLevel.Error));

        Assert.NotNull(error.Exception);
        Assert.Contains("will not be discovered", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A start and a stop close together — an app stopped while its first publish is still retrying —
    /// are pushed off the host's lifetime thread, and once they are, they are free to land in either
    /// order. A withdrawal applied before the publication it followed must not be undone by it.
    /// </summary>
    [Fact]
    public async Task A_change_that_lands_out_of_order_does_not_undo_the_newer_one()
    {
        var mdns = new FakeMdns();
        await using var test = await TestApp.StartAsync();

        await using var advertiser = new ServerAdvertiser(mdns, test.Server, null, FastRetries());

        // Stamped in the order the app actually moved in, then applied the other way round.
        var running = advertiser.Next();
        var stopping = advertiser.Next();

        await advertiser.ApplyAsync(stopping, running: false);
        await advertiser.ApplyAsync(running, running: true);

        Assert.Null(advertiser.Publication);
        Assert.Empty(mdns.Published);
    }

    [Fact]
    public void Prefers_cleartext_and_skips_addresses_without_a_port()
    {
        var server = new FakeServer("http://unix:/tmp/app.sock", "https://0.0.0.0:7443", "http://0.0.0.0:7080");

        Assert.Equal(("http", 7080), Advertiser(server).Endpoint());
        Assert.Equal(("https", 7443), Advertiser(server, o => o.Scheme = "https").Endpoint());
        Assert.Equal(("https", 7443), Advertiser(new FakeServer("https://[::]:7443")).Endpoint());
        Assert.Null(Advertiser(new FakeServer("https://[::]:7443"), o => o.Scheme = "http").Endpoint());
    }

    [Fact]
    public async Task Says_so_when_the_server_has_no_port_to_advertise()
    {
        var mdns = new FakeMdns();
        var logger = new RecordingLogger<ServerAdvertiser>();

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        await app.StartAsync(Token);

        await using var advertiser = new ServerAdvertiser(mdns, app.GetTestServer(), null, FastRetries(), logger);
        await advertiser.StartAsync(Token);

        Assert.Empty(mdns.Published);
        Assert.Contains("no port to advertise", Assert.Single(logger.At(LogLevel.Warning)).Message, StringComparison.Ordinal);
    }

    static ServerAdvertiser Advertiser(IServer server, Action<ServerAdvertisementOptions>? configure = null)
    {
        var options = FastRetries();
        configure?.Invoke(options);
        return new ServerAdvertiser(new FakeMdns(), server, null, options);
    }

    /// <summary>The shipped policy with the waiting taken out of it.</summary>
    static ServerAdvertisementOptions FastRetries() => new()
    {
        PublishAttempts = 3,
        PublishRetryDelay = TimeSpan.FromMilliseconds(5),
        MaxPublishRetryDelay = TimeSpan.FromMilliseconds(20)
    };

    /// <summary>
    /// Polls until the condition holds, and says so when it never does. Returning quietly on the
    /// deadline left the timeout to be reported by whichever assert came next, which describes the
    /// state the wait was still waiting for rather than the wait that ran out.
    /// </summary>
    static async Task WaitUntil(Func<bool> condition, CancellationToken cancellationToken, string? what = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;

            await Task.Delay(20, cancellationToken);
        }

        Assert.Fail($"Timed out waiting for {what ?? "the condition"}");
    }

    sealed class FakeServer : IServer
    {
        public FakeServer(params string[] addresses)
        {
            var feature = new ServerAddressesFeature();
            foreach (var address in addresses)
                feature.Addresses.Add(address);

            this.Features.Set<IServerAddressesFeature>(feature);
        }

        public IFeatureCollection Features { get; } = new FeatureCollection();
        public Task StartAsync<TContext>(Microsoft.AspNetCore.Hosting.Server.IHttpApplication<TContext> application, CancellationToken cancellationToken) where TContext : notnull => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() { }
    }
}

/// <summary>An app on Kestrel, on loopback port 0 — so every test proves the advertised port is the one the OS picked.</summary>
sealed class TestApp : IAsyncDisposable
{
    public required WebApplication App { get; init; }

    public IServer Server => this.App.Services.GetRequiredService<IServer>();

    public int Port => new Uri(this.Server.Features.Get<IServerAddressesFeature>()!.Addresses.Single()).Port;

    public static async Task<TestApp> StartAsync()
    {
        var test = await CreateAsync();
        await test.App.StartAsync(TestContext.Current.CancellationToken);
        return test;
    }

    public static Task<TestApp> CreateAsync(Action<IServiceCollection>? services = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));
        services?.Invoke(builder.Services);

        return Task.FromResult(new TestApp { App = builder.Build() });
    }

    public async ValueTask DisposeAsync() => await this.App.DisposeAsync();
}

sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

/// <summary>Keeps every log line, so a test can assert on what was said and at what level.</summary>
sealed class RecordingLogger<T> : ILogger<T>
{
    readonly List<LogEntry> entries = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        // Written from whatever thread the retry loop landed on, read from the test's.
        lock (this.entries)
            this.entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
    }

    public IReadOnlyList<LogEntry> At(LogLevel level)
    {
        lock (this.entries)
            return [.. this.entries.Where(x => x.Level == level)];
    }
}

public class ServerLocatorTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Turns_a_resolved_service_into_a_base_address()
    {
        var mdns = new FakeMdns();
        mdns.Announce(new MdnsService
        {
            InstanceName = "Kitchen Pi",
            ServiceType = "_myapp._tcp",
            HostName = "kitchen.local",
            Port = 5000,
            Addresses = [IPAddress.Parse("192.168.1.40")],
            TxtRecords = new Dictionary<string, string> { ["path"] = "/api", ["scheme"] = "http" }
        });

        var found = await new ServerLocator(mdns).FindFirstAsync("_myapp._tcp", TimeSpan.FromSeconds(2), cancellationToken: Token);

        Assert.NotNull(found);
        Assert.Equal("Kitchen Pi", found.InstanceName);
        Assert.Equal("http://192.168.1.40:5000/api", found.BaseAddress.ToString());
    }

    [Fact]
    public async Task Prefers_an_ipv4_address_for_the_base_url()
    {
        var mdns = new FakeMdns();
        mdns.Announce(new MdnsService
        {
            InstanceName = "Dual",
            ServiceType = "_myapp._tcp",
            Port = 8080,
            Addresses = [IPAddress.Parse("fd00::1"), IPAddress.Parse("10.0.0.5")]
        });

        var found = await new ServerLocator(mdns).FindFirstAsync("_myapp._tcp", TimeSpan.FromSeconds(2), cancellationToken: Token);

        Assert.Equal("http://10.0.0.5:8080/", found!.BaseAddress.ToString());
        Assert.Equal("http://[fd00::1]:8080/", found.BaseAddressFor(IPAddress.Parse("fd00::1")).ToString());
    }

    [Fact]
    public async Task A_filter_picks_the_instance_the_app_cares_about()
    {
        var mdns = new FakeMdns();
        mdns.Announce(new MdnsService
        {
            InstanceName = "Printer",
            ServiceType = "_http._tcp",
            Port = 631,
            Addresses = [IPAddress.Parse("192.168.1.10")]
        });
        mdns.Announce(new MdnsService
        {
            InstanceName = "Kitchen Pi",
            ServiceType = "_http._tcp",
            Port = 5000,
            Addresses = [IPAddress.Parse("192.168.1.40")],
            TxtRecords = new Dictionary<string, string> { ["role"] = "controller" }
        });

        var found = await new ServerLocator(mdns).FindFirstAsync(
            "_http._tcp",
            TimeSpan.FromSeconds(2),
            x => x.TxtRecords.TryGetValue("role", out var role) && role == "controller",
            Token
        );

        Assert.Equal("Kitchen Pi", found!.InstanceName);
    }

    [Fact]
    public async Task Nothing_on_the_link_is_an_answer_rather_than_a_failure()
    {
        var found = await new ServerLocator(new FakeMdns())
            .FindFirstAsync("_nothing._tcp", TimeSpan.FromMilliseconds(200), cancellationToken: Token);

        Assert.Null(found);
    }

    /// <summary>
    /// An app that hosts and browses at once — every peer-to-peer app — hears its own advertisement back through its
    /// own responder. Listing itself as a peer is wrong, and connecting to it is worse.
    /// </summary>
    [Fact]
    public async Task Leaves_out_the_apps_own_advertisement()
    {
        var mdns = new FakeMdns { RenameTo = "Me (2)" };
        await using var test = await TestApp.StartAsync();
        await using var advertiser = await test.App.AdvertiseAsync(mdns, o =>
        {
            o.ServiceType = "_myapp._tcp";
            o.InstanceName = "Me";
        }, Token);

        // what the responder hears back: this app under the name it settled on, a peer, and a same-named service of another type
        mdns.Announce(Resolved("Me (2)", "_myapp._tcp."));
        mdns.Announce(Resolved("Peer", "_myapp._tcp"));
        mdns.Announce(Resolved("Me (2)", "_other._tcp"));

        var found = await new ServerLocator(mdns).FindAllAsync("_myapp._tcp", TimeSpan.FromMilliseconds(300), Token);
        Assert.Equal(["Peer"], found.Select(x => x.InstanceName));

        var other = await new ServerLocator(mdns).FindAllAsync("_other._tcp", TimeSpan.FromMilliseconds(300), Token);
        Assert.Equal(["Me (2)"], other.Select(x => x.InstanceName));
    }


    [Fact]
    public async Task Finds_the_name_again_once_the_app_stops_advertising_it()
    {
        var mdns = new FakeMdns();
        await using var test = await TestApp.StartAsync();
        var advertiser = await test.App.AdvertiseAsync(mdns, o =>
        {
            o.ServiceType = "_myapp._tcp";
            o.InstanceName = "Shared";
        }, Token);
        mdns.Announce(Resolved("Shared", "_myapp._tcp"));

        Assert.Null(await new ServerLocator(mdns).FindFirstAsync("_myapp._tcp", TimeSpan.FromMilliseconds(300), cancellationToken: Token));

        // gone from here - a device that took the name since is a peer like any other
        await advertiser.StopAsync(Token);
        var found = await new ServerLocator(mdns).FindFirstAsync("_myapp._tcp", TimeSpan.FromSeconds(2), cancellationToken: Token);
        Assert.Equal("Shared", found?.InstanceName);
    }


    [Fact]
    public async Task Another_responder_still_sees_the_advertisement()
    {
        var mine = new FakeMdns();
        var theirs = new FakeMdns();
        await using var test = await TestApp.StartAsync();
        await using var advertiser = await test.App.AdvertiseAsync(mine, o =>
        {
            o.ServiceType = "_myapp._tcp";
            o.InstanceName = "Host";
        }, Token);
        theirs.Announce(Resolved("Host", "_myapp._tcp"));

        var found = await new ServerLocator(theirs).FindFirstAsync("_myapp._tcp", TimeSpan.FromSeconds(2), cancellationToken: Token);
        Assert.Equal("Host", found?.InstanceName);
    }


    static MdnsService Resolved(string instanceName, string serviceType) => new()
    {
        InstanceName = instanceName,
        ServiceType = serviceType,
        Port = 5000,
        Addresses = [IPAddress.Parse("192.168.1.40")]
    };


    [Fact]
    public async Task An_unresolved_instance_is_skipped()
    {
        var mdns = new FakeMdns();
        mdns.Announce(new MdnsService { InstanceName = "Half", ServiceType = "_myapp._tcp" });

        var found = await new ServerLocator(mdns)
            .FindFirstAsync("_myapp._tcp", TimeSpan.FromMilliseconds(300), cancellationToken: Token);

        Assert.Null(found);
    }
}

/// <summary>A responder that records what it was asked to publish and replays what it was told to find.</summary>
sealed class FakeMdns : IMdnsManager
{
    readonly List<MdnsBrowseResult> announcements = [];
    readonly List<MdnsServiceRegistration> published = [];
    readonly Lock sync = new();

    int goodbyes;
    int attempts;

    /// <summary>
    /// A snapshot, because the advertiser publishes and withdraws on its own task while the test
    /// thread is polling this — an unsynchronized list read that way tears or throws under load.
    /// </summary>
    public IReadOnlyList<MdnsServiceRegistration> Published
    {
        get
        {
            lock (this.sync)
                return [.. this.published];
        }
    }

    public int GoodbyesSent => Volatile.Read(ref this.goodbyes);

    /// <summary>Simulates the responder resolving a name conflict, which it is entitled to do.</summary>
    public string? RenameTo { get; set; }

    /// <summary>How many registrations to refuse before letting one through — a responder that is not up yet.</summary>
    public int PublishFailures { get; set; }

    /// <summary>Every attempt, refused ones included, so a test can see the retry rather than only its outcome.</summary>
    public int PublishAttempts => Volatile.Read(ref this.attempts);

    public void Announce(MdnsService service) => this.announcements.Add(new MdnsBrowseResult(MdnsBrowseStatus.Found, service));

    public async IAsyncEnumerable<MdnsBrowseResult> Browse(
        MdnsBrowseConfig config,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default
    )
    {
        foreach (var announcement in this.announcements)
        {
            // platforms differ on the trailing dot of a fully qualified type
            if (announcement.Service.ServiceType.TrimEnd('.') == config.ServiceType.TrimEnd('.'))
                yield return announcement;
        }

        // Browsing never completes on its own — the contract the real one has, and the reason the
        // locator has to impose its own deadline.
        await Task.Delay(Timeout.Infinite, ct);
    }

    public Task<MdnsService?> Resolve(string instanceName, string serviceType, TimeSpan? timeout = null, CancellationToken ct = default)
        => Task.FromResult<MdnsService?>(null);

    public Task<IMdnsPublication> Publish(MdnsServiceRegistration registration, CancellationToken ct = default)
    {
        Interlocked.Increment(ref this.attempts);

        if (this.PublishFailures > 0)
        {
            this.PublishFailures--;
            return Task.FromException<IMdnsPublication>(new InvalidOperationException("the responder refused the registration"));
        }

        lock (this.sync)
            this.published.Add(registration);

        return Task.FromResult<IMdnsPublication>(new FakePublication(
            this,
            this.RenameTo ?? registration.InstanceName,
            registration
        ));
    }

    sealed class FakePublication(FakeMdns owner, string instanceName, MdnsServiceRegistration registration) : IMdnsPublication
    {
        public string InstanceName { get; } = instanceName;
        public string ServiceType { get; } = registration.ServiceType;
        public string Domain { get; } = registration.Domain;
        public int Port { get; } = registration.Port;

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref owner.goodbyes);
            return default;
        }
    }
}
