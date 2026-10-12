# Shiny AspNet

A set of independent libraries for ASP.NET Core. Each one is a single package you add to an existing app,
registered the ASP.NET way — `builder.Services.Add…()`, `app.Map…()` or `app.Use…()`. Every `Map…` returns an
`IEndpointConventionBuilder`, so `RequireAuthorization`, `RequireCors`, `RequireRateLimiting` and the rest of
ASP.NET's own conventions apply to everything a library maps. All packages are trim and Native AOT clean.

Most of these are ports of the [Shiny HTTP Server](https://shinylib.net/httpserver/) add-ons, re-hosted on Kestrel.
Documentation: https://shinylib.net/aspnet/

| Package | Description |
| --- | --- |
| `Shiny.AspNet.WebHosting` | Modular startup with `IWebModule` |
| `Shiny.AspNet.WebDav` | WebDAV class 1 & 2 server — mount a directory in Finder, Explorer or any WebDAV client |
| `Shiny.AspNet.CalDav` | CalDAV & CardDAV — calendars and contacts for Apple, Thunderbird and DAVx⁵ |
| `Shiny.AspNet.Tus` | Resumable uploads over tus 1.0.0 |
| `Shiny.AspNet.FileSync` | Dropbox-style file sync with content-defined chunking |
| `Shiny.AspNet.NuGet` | A private NuGet V3 feed |
| `Shiny.AspNet.Npm` | A private npm registry |
| `Shiny.AspNet.DocumentDb` | REST + live SSE endpoints for a Shiny.DocumentDb document type |
| `Shiny.AspNet.JsonPatch` | RFC 6902 JSON Patch on System.Text.Json, with minimal-API binding |
| `Shiny.AspNet.Webhooks` | Verify inbound webhooks (GitHub, Stripe, Slack, Standard Webhooks) and sign outbound ones |
| `Shiny.AspNet.Idempotency` | `Idempotency-Key` middleware |
| `Shiny.AspNet.ContentDigest` | RFC 9530 `Content-Digest` / `Repr-Digest` |
| `Shiny.AspNet.Acme` | Automatic TLS from Let's Encrypt or any ACME CA, hot-swapped into Kestrel |
| `Shiny.AspNet.Discovery` | mDNS / DNS-SD advertising and discovery |
| `Shiny.AspNet.Ssh` | SSH remote forwarding, including zero-account quick tunnels |
| `Shiny.AspNet.Tunnels` | cloudflared, ngrok and Tailscale Funnel agents, supervised |

## Hosting

### Shiny.AspNet.WebHosting
* Merges service container build and post build scenarios into a single class using `IWebModule`

#### Setup
1. Install the NuGet package `Shiny.AspNet.WebHosting`
2. Add a web module by implementing `IWebModule`:
   ```csharp
   using Shiny;

   public class MyWebModule : IWebModule
   {
       public void Add(WebApplicationBuilder builder)
       {
           // Register your services here
       }

       public void Use(WebApplication app)
       {
           // Configure your middleware/endpoints here
       }
   }
   ```
3. In your application hosting startup, add the following:
   ```csharp
   using Shiny;

   var builder = WebApplication.CreateBuilder(args);
   builder.AddInfrastructureModules(new MyWebModule());

   var app = builder.Build();
   app.UseInfrastructureModules();
   ```

> Previously shipped as `Shiny.Extensions.WebHosting` from [shinyorg/extensions](https://github.com/shinyorg/extensions). The API and `Shiny` namespace are unchanged — swap the package reference to migrate.

### Shiny.AspNet.Acme
Automatic HTTPS for Kestrel from Let's Encrypt, ZeroSSL or any ACME (RFC 8555) CA — a maintained replacement for LettuceEncrypt.

- Issues, stores and renews certificates, hot-swapping them into Kestrel with no restart (intermediate chain included)
- HTTP-01 answered ahead of your whole pipeline; TLS-ALPN-01 answered from the TLS handshake
- ACME Renewal Information (RFC 9773), External Account Binding (ZeroSSL), IP-address certificates, ECDSA/RSA
- `AcmeCertificateRegistry`: many certificates chosen by SNI, added/removed at runtime, PEM/PFX import
- In-box crypto only (no BouncyCastle/Certes); `Shiny.AspNet.Acme` meter for expiry alerts

```csharp
builder.WebHost.UseUrls("http://*:80", "https://*:443");
builder.Services.AddAcme(o =>
{
    o.Domains = ["example.com"];
    o.Email = "ops@example.com";
    o.AcceptTermsOfService = true;
    o.DirectoryUrl = AcmeDirectories.LetsEncryptStaging; // remove once it works
});
```

### Shiny.AspNet.Discovery
Advertise an ASP.NET Core app on the local network with mDNS/DNS-SD (Bonjour), and find the servers other machines advertise.

- Publishes on the port Kestrel actually bound (port 0 works), withdraws with a goodbye on shutdown
- Re-announces on network change; retries a refused registration and logs at Error when it gives up
- `IServerLocator`: `FindFirstAsync` / `FindAllAsync` / `WatchAsync`, yielding ready-to-use base URLs, never finding itself
- TXT records for path, scheme and your own metadata

```csharp
builder.WebHost.UseUrls("http://0.0.0.0:0");
builder.Services.AddServerAdvertisement(o => o.ServiceType = "_myapp._tcp");
builder.Services.AddServerLocator();

var peer = await locator.FindFirstAsync("_myapp._tcp", TimeSpan.FromSeconds(5));
```

## Content & protocols

### Shiny.AspNet.WebDav
A WebDAV (RFC 4918) class 1 & 2 server for ASP.NET Core — mount a directory in Finder, Windows Explorer or any WebDAV client.

- PROPFIND/PROPPATCH/MKCOL/COPY/MOVE/LOCK/UNLOCK plus GET/HEAD with ranges & conditional requests
- `If` header (lock tokens and ETags), shared/exclusive locks, RFC 4331 quota, dead properties
- Built-in browser file manager (upload, new folder, rename, delete) driven by the mount's own verbs
- Read-only by default; atomic, size-bounded uploads; hardened XML parsing; containment checks incl. symlinks
- Pluggable `IWebDavFileSystem` and `IWebDavPropertyStore`
- Returns an `IEndpointConventionBuilder` — `RequireAuthorization`, `RequireCors`, etc. cover every verb; `RequireAuthorizationForChanges` leaves reads open

```csharp
app.MapWebDav("/dav", o =>
{
    o.RootPath = Path.Combine(app.Environment.ContentRootPath, "share");
    o.AllowWrite = true;
    o.AllowDelete = true;
})
.RequireAuthorization();
```

### Shiny.AspNet.CalDav
CalDAV (RFC 4791) and CardDAV (RFC 6352) for ASP.NET Core — serve calendars and contacts to iOS/macOS Calendar & Contacts, Thunderbird and DAVx⁵.

- Discovery: `/.well-known/caldav|carddav`, current-user-principal, home sets
- calendar-query (recurrence-aware time ranges), multiget, free-busy, addressbook-query, sync-collection (RFC 6578)
- PUT validation with RFC preconditions, If-Match / If-None-Match, per-principal isolation
- File-backed stores included; implement `ICalendarStore` / `IAddressBookStore` for your own data
- Built on Shiny.AspNet.WebDav

```csharp
app.MapCalDav("/dav", o =>
{
    o.CalendarStore = new FileCalendarStore(Path.Combine(dataPath, "calendars"));
    o.AddressBookStore = new FileAddressBookStore(Path.Combine(dataPath, "contacts"));
})
.RequireAuthorization();
```

### Shiny.AspNet.Tus
Resumable uploads over the tus 1.0.0 protocol — an upload that drops half way resumes from the last byte the server kept.

- Core protocol plus creation, creation-with-upload, defer-length, termination, expiration, checksum (sha1/sha256/md5) and concatenation
- Keeps every byte of an interrupted request; a stalled request is taken over instead of locking the client out
- Streams to the store, never buffers; `DiskTusStore` survives restarts (offset = file length)
- Lifts Kestrel's 30 MB body limit for tus requests (the protocol bounds them); an explicit `RequestSizeLimit` wins
- Expiry sweep tied to `IHostApplicationLifetime`; `WithTusHeaders()` for CORS

```csharp
app.MapTus("/files", o =>
{
    o.Store = new DiskTusStore(Path.Combine(builder.Environment.ContentRootPath, "App_Data", "uploads"));
    o.Expiration = TimeSpan.FromDays(1);
    o.OnUploadCompleteAsync = async ctx => { await using var file = await ctx.OpenReadAsync(); /* ... */ };
})
.RequireAuthorization();
```

### Shiny.AspNet.FileSync
Dropbox-style file sync server: content-defined chunks stored once by hash, so an edit moves only what changed.

- Change feed with long-poll, optimistic revisions (409 conflicts, 422 missing chunks), tombstones
- Pack uploads/downloads (Range-resumable) and resumable pack uploads over tus at `{prefix}/uploads`
- Per-user stores via `StoreSelector`; garbage collection of unreferenced chunks
- Works with the existing `Shiny.Net.HttpServer.FileSync.Client` (background transfers on iOS/Android)
- One `IEndpointConventionBuilder` covers every route, tus included

```csharp
app.MapFileSync("/sync", o => o.Store = new DiskFileSyncStore(Path.Combine(dataDir, "sync")))
   .RequireAuthorization();
```

### Shiny.AspNet.NuGet
A private NuGet feed for ASP.NET Core: the NuGet V3 protocol, so `dotnet restore`, `dotnet nuget push`, Visual Studio and Rider work unchanged.

- Service index, package content, registrations, search and autocomplete; push, unlist/relist and delete
- Uses NuGet's own folder layout on disk, so the directory is also a plain local package source
- Version rules from `NuGet.Versioning`; the nuspec is read by hand, so it is trim/AOT-clean
- API keys (`X-NuGet-ApiKey`) or ASP.NET authorization; `OnBeforePushAsync` / `OnPackagePushedAsync` hooks
- Write routes raise Kestrel's 30 MB body limit to `MaxPackageSize` (250 MB by default)

```csharp
app.MapNuGetFeed("/nuget", o =>
{
    o.Store = new DiskNuGetPackageStore(Path.Combine(dataDir, "packages"));
    o.ApiKeys.Add(builder.Configuration["NuGet:ApiKey"]!);
})
.ForWrites(w => w.RequireRateLimiting("push"));
// dotnet nuget add source http://host:5000/nuget/v3/index.json -n mine
```

### Shiny.AspNet.Npm
A private npm registry for ASP.NET Core: the registry API npm, pnpm and Yarn speak. No database, no Node.js.

- Install (full and abbreviated metadata, ETag revalidation), publish, dist-tags, deprecate, unpublish, owners, search, whoami, login
- Bearer tokens mapped to users; maintainer checks like the public registry
- Optional fall-through to registry.npmjs.org (`Upstream`); local packages always win
- Scoped packages in both URL forms; trim/AOT-clean
- Write routes raise Kestrel's 30 MB body limit to fit `MaxTarballSize` (100 MB by default) once base64-encoded

```csharp
app.MapNpmRegistry("/npm", o =>
{
    o.Store = new DiskNpmPackageStore(Path.Combine(dataDir, "npm"));
    o.Tokens[builder.Configuration["Npm:Token"]!] = "ci";
});
// .npmrc: @acme:registry=http://host:5000/npm/
```

### Shiny.AspNet.JsonPatch
RFC 6902 JSON Patch over RFC 6901 JSON Pointer for ASP.NET Core, on System.Text.Json.Nodes — trim and Native AOT clean.

- add / remove / replace / move / copy / test, applied all-or-nothing to a copy
- Apply to any `JsonNode`, or to a typed value via its source-generated `JsonTypeInfo<T>`
- `JsonPatchDocument` binds as a minimal-API parameter (`application/json-patch+json` only; RDG-compatible)
- Failures carry the RFC 5789 status (400/409/415/422) and an RFC 9457 problem naming the failed operation
- Public `JsonPointer`, RFC 6902 §4.6 `test` equality, fluent builder

```csharp
builder.Services.AddProblemDetails();
builder.Services.AddJsonPatch();
app.UseExceptionHandler();

app.MapPatch("/notes/{id:int}", (int id, JsonPatchDocument patch, NoteStore notes) =>
    Results.Json(patch.ApplyTo(notes.Get(id), AppJson.Default.Note), AppJson.Default.Note));
```

### Shiny.AspNet.DocumentDb
A Shiny.DocumentDb document type as a complete REST + Server-Sent Events resource in one line.

- List (filter / orderby / fields / skip-take / cursor), by-id, count, create, replace, delete
- PATCH as RFC 7396 merge patch or RFC 6902 JSON Patch, with ETag / If-Match (412/428)
- Server-side scopes enforced on reads and on both sides of writes (out-of-scope = 404)
- Field allow-list, live SSE tail with heartbeats, schema-free `MapDocumentCollection`
- Returns a `RouteGroupBuilder` with OpenAPI metadata, so `RequireAuthorization`, `RequireRateLimiting`, etc. cover every route

```csharp
builder.Services.AddDocumentStore(o => o.DatabaseProvider = new SqliteDatabaseProvider(path));

app.MapDocuments<Order>("/orders", o =>
{
    o.Operations = DocumentEndpoints.All;
    o.TypeInfo = AppJson.Default.Order;
    o.AllowFilterOn(x => x.Status, x => x.Total);
    o.Scope<ITenantContext>((tenant, _) => x => x.TenantId == tenant.TenantId);
})
.RequireAuthorization("orders");
```

## Middleware

### Shiny.AspNet.Webhooks
Verify inbound webhooks over the exact bytes that arrived, and sign the ones you send.

- Built-in verifiers: GitHub, Stripe, Slack, Standard Webhooks (Svix, Clerk, Resend, OpenAI…), generic HMAC-in-a-header
- Constant-time comparison, secret rotation, replay windows on signed timestamps
- Duplicate-delivery suppression (bounded in-memory store, or your own `IWebhookDeliveryStore`)
- `MapWebhook` verifies inline; or `[RequireWebhookSignature]` / `.RequireWebhookSignature(...)` + `app.UseWebhookVerification()` on minimal APIs, groups and MVC
- 401 problem details via `IProblemDetailsService`; the reason is logged, never returned
- `WebhookSigner` for outbound Standard Webhooks signatures

```csharp
builder.Services.AddWebhooks(o =>
{
    o.AddVerifier("stripe", WebhookSignature.Stripe(builder.Configuration["Stripe:WebhookSecret"]!));
    o.SuppressDuplicates();
});

app.MapWebhook("/hooks/github", WebhookSignature.GitHub(secret), async (WebhookContext ctx) => { /* ... */ });

app.UseWebhookVerification();
app.MapPost("/hooks/stripe", (WebhookContext hook) => Results.Ok()).RequireWebhookSignature("stripe");
```

### Shiny.AspNet.Idempotency
`Idempotency-Key` (IETF draft) middleware: a retried POST gets the stored original response instead of running the handler twice.

- Opt in per endpoint: `.RequireIdempotencyKey()`, `.WithIdempotency()`, `.DisableIdempotency()` on endpoints, groups or controllers; `[Idempotent]` / `[DisableIdempotency]`
- Replays status, headers and body with `Idempotent-Replayed: true`
- 409 + `Retry-After` while the first request is in flight; 422 when a key is reused with a different body
- Keys scoped by method, path and authenticated caller; 5xx/thrown requests are not stored
- Response copied as it is written: streaming, `BodyWriter`, `SendFileAsync` and `Content-Length` behave normally
- In-memory store by default; `AddDistributedCacheIdempotencyStore()` for Redis/SQL via `IDistributedCache`

```csharp
builder.Services.AddIdempotency(o => o.Expiration = TimeSpan.FromHours(6));

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.UseIdempotency();

app.MapPost("/payments", Charge).RequireIdempotencyKey();
```

### Shiny.AspNet.ContentDigest
RFC 9530 `Content-Digest` / `Repr-Digest` for ASP.NET Core — end-to-end integrity across proxies, gateways and tunnels.

- Verifies request bodies as they stream (never buffered); mismatch, malformed or unsupported → 400 problem + `Want-Content-Digest`
- `.RequireContentDigest()` refuses bodies without one; handlers that swallow a failed check are overruled
- Answers `Want-Content-Digest` / `Want-Repr-Digest`: small bodies get a header, streamed bodies a trailer (HTTP/2, HTTP/3 — Kestrel sends no trailers on HTTP/1.1)
- `[ContentDigest]` / `[DisableContentDigest]` attributes and `.WithContentDigest()` / `.DisableContentDigest()` conventions
- sha-256 and sha-512; `DigestFields` helpers to compute and parse

```csharp
builder.Services.AddContentDigest();

var app = builder.Build();
app.UseContentDigest();   // after routing, before UseRequestDecompression/UseResponseCompression

app.MapPost("/firmware", Upload).RequireContentDigest();
app.MapGet("/snapshot", Snapshot).WithContentDigest();
```

## Tunnels

### Shiny.AspNet.Ssh
SSH remote port forwarding (`ssh -R`) for a Kestrel app — publish through a VPS you own, or get a free public HTTPS address from a hosted tunnel (Pinggy, sish). Pure managed code; nothing to install.

- Forwards to the address Kestrel actually bound (works with port 0, `--urls`, `ASPNETCORE_URLS`)
- Starts once Kestrel is listening, closes before it stops
- Host keys are pinned — connecting unverified fails unless you opt out explicitly
- Auto-reconnect with backoff; `PublicUrl` / `State` change notifications (`QuickTunnel` is `INotifyPropertyChanged`)

```csharp
// a free public address while developing
builder.Services.AddQuickTunnel(autoStart: builder.Environment.IsDevelopment());

// or a host you own
builder.Services.AddSshTunnel(o =>
{
    o.Host = "tunnel.example.com";
    o.Username = "tunnel";
    o.PrivateKeyPath = keyPath;
    o.RemotePort = 8080;
    o.HostKeyFingerprints.Add("SHA256:…");
});
```

### Shiny.AspNet.Tunnels
Publish a Kestrel app through cloudflared, ngrok or Tailscale Funnel by supervising the vendor's own agent process.

- Reads the port off the running server and starts the agent once Kestrel is listening
- Reports the public URL the agent announces (`ITunnelAgent.PublicUrl`)
- Fails fast with a useful message when the binary is missing, exits early, or never announces a URL
- Kills the agent's whole process tree on shutdown

```csharp
builder.Services.AddCloudflareTunnel();     // or AddNgrokTunnel() / AddTailscaleFunnel()
```
