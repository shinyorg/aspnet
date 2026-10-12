using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Shiny;

namespace Shiny.AspNet.WebHosting.Tests;

public class WebModuleTests
{
    [Fact]
    public async Task Modules_Add_Then_Use_In_Registration_Order()
    {
        var calls = new List<string>();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddInfrastructureModules(new RecordingModule("a", calls), new RecordingModule("b", calls));

        Assert.Equal(["a.Add", "b.Add"], calls);
        Assert.Contains(builder.Services, x => x.ServiceType == typeof(Marker));

        await using var app = builder.Build();
        app.UseInfrastructureModules();
        Assert.Equal(["a.Add", "b.Add", "a.Use", "b.Use"], calls);

        await app.StartAsync(TestContext.Current.CancellationToken);
        var response = await app.GetTestClient().GetStringAsync("/a", TestContext.Current.CancellationToken);
        Assert.Equal("a", response);
    }

    [Fact]
    public async Task Use_Only_Runs_Modules_Once()
    {
        var calls = new List<string>();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddInfrastructureModules(new RecordingModule("a", calls));

        await using var app = builder.Build();
        app.UseInfrastructureModules();
        app.UseInfrastructureModules();

        Assert.Single(calls, x => x == "a.Use");
    }

    sealed class Marker;

    sealed class RecordingModule(string name, List<string> calls) : IWebModule
    {
        public void Add(WebApplicationBuilder builder)
        {
            calls.Add($"{name}.Add");
            builder.Services.AddSingleton<Marker>();
        }

        public void Use(WebApplication app)
        {
            calls.Add($"{name}.Use");
            app.MapGet("/" + name, (HttpContext ctx) => ctx.Response.WriteAsync(name));
        }
    }
}
