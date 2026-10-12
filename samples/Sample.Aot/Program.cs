using System.Text.Json.Serialization;
using Shiny.AspNet.Acme;
using Shiny.AspNet.CalDav;
using Shiny.AspNet.ContentDigest;
using Shiny.AspNet.Discovery;
using Shiny.AspNet.DocumentDb;
using Shiny.AspNet.FileSync;
using Shiny.AspNet.Idempotency;
using Shiny.AspNet.JsonPatch;
using Shiny.AspNet.Npm;
using Shiny.AspNet.NuGet;
using Shiny.AspNet.Ssh;
using Shiny.AspNet.Tunnels;
using Shiny.AspNet.Tus;
using Shiny.AspNet.WebDav;
using Shiny.AspNet.Webhooks;

// Every library, reached the way an app would reach it. See the csproj for why this exists.
var data = Path.Combine(AppContext.BaseDirectory, "data");

var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJson.Default));
builder.AddInfrastructureModules(new ObservabilityModule());

builder.Services.AddProblemDetails();
builder.Services.AddJsonPatch();
builder.Services.AddWebhooks(o => o.AddVerifier("stripe", WebhookSignature.Stripe("whsec_c2FtcGxl")));
builder.Services.AddIdempotency();
builder.Services.AddContentDigest();
builder.Services.AddAcme(o =>
{
    o.Domains = ["example.com"];
    o.AcceptTermsOfService = true;
    o.DirectoryUrl = AcmeDirectories.LetsEncryptStaging;
});
builder.Services.AddServerAdvertisement();
builder.Services.AddServerLocator();
builder.Services.AddQuickTunnel();
builder.Services.AddSshTunnel(o => o.Host = "tunnel.example.com", autoStart: false);
builder.Services.AddCloudflareTunnel();

var app = builder.Build();
app.UseInfrastructureModules();
app.UseExceptionHandler();
app.UseWebhookVerification();
app.UseIdempotency();
app.UseContentDigest();

app.MapWebDav("/dav", o => o.RootPath = Path.Combine(data, "dav"));
app.MapCalDav("/cal", o =>
{
    o.CalendarStore = new FileCalendarStore(Path.Combine(data, "calendars"));
    o.AddressBookStore = new FileAddressBookStore(Path.Combine(data, "contacts"));
});
app.MapTus("/files", o => o.Store = new DiskTusStore(Path.Combine(data, "uploads")));
app.MapFileSync("/sync", o => o.Store = new DiskFileSyncStore(Path.Combine(data, "sync")));
app.MapNuGetFeed("/nuget", o =>
{
    o.Store = new DiskNuGetPackageStore(Path.Combine(data, "nuget"));
    o.ApiKeys.Add("key");
});
app.MapNpmRegistry("/npm", o =>
{
    o.Store = new DiskNpmPackageStore(Path.Combine(data, "npm"));
    o.Tokens["token"] = "ci";
});
app.MapDocuments<Note>("/notes", o =>
{
    o.Operations = DocumentEndpoints.All;
    o.TypeInfo = AppJson.Default.Note;
});
app.MapPatch("/patch", (JsonPatchDocument patch) => Results.Json(patch.ApplyTo(new Note(), AppJson.Default.Note), AppJson.Default.Note));
app.MapWebhook("/hooks/github", WebhookSignature.GitHub("secret"), _ => Task.CompletedTask);
app.MapPost("/hooks/stripe", () => Results.Ok()).RequireWebhookSignature("stripe");
app.MapPost("/payments", () => Results.Ok()).RequireIdempotencyKey();
app.MapGet("/snapshot", () => "snapshot").WithContentDigest();

app.Run();

public class Note
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
}

[JsonSerializable(typeof(Note))]
partial class AppJson : JsonSerializerContext;

sealed class ObservabilityModule : IWebModule
{
    public void Add(WebApplicationBuilder builder) => builder.Services.AddHealthChecks();
    public void Use(WebApplication app) => app.MapHealthChecks("/health");
}
