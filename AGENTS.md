# Shiny.AspNet — Working Notes

Guidance for maintaining this repo. Code lives in `src/`, tests in `tests/`, samples in `samples/`,
the published Claude Code skills in `skills/`, and the public documentation site in a **separate**
repo at `~/Desktop/dev/documentation` (rendered to https://shinylib.net/aspnet).

**Shiny.AspNet** is a set of independent libraries for ASP.NET Core (`net10.0`), one package per concern.
Most are ports of the `Shiny.Net.HttpServer` add-ons (`~/Desktop/dev/httpserver`) re-hosted on Kestrel.
Every shipping package is trim and Native AOT clean. Repo: https://github.com/shinyorg/aspnet.

## Layout

- `src/Shiny.AspNet.<Name>/` — shipping libraries
- `src/Shared/` — internal source linked into more than one package (`<Compile Include="..\Shared\…" Link="…"/>`)
- `tests/Shiny.AspNet.<Name>.Tests/` — one xunit.v3 project per library
- `samples/Sample.Web` — runnable sample; `samples/Sample.Aot` — published with Native AOT in CI, never run
- `skills/` — Claude Code skills (`shiny-aspnet`, `shiny-web-hosting`), shipped by `.claude-plugin/` and synced
  to `shinyorg/skills`
- `readme.md` — the GitHub readme, also packed into every package; `nuget.txt` is packed as `readme.txt`

## Build & test

```bash
dotnet build Build.slnf -c Release       # the shipping set; packs .nupkg + .snupkg
dotnet test AspNet.slnx                  # every test project
dotnet publish samples/Sample.Aot -r osx-arm64 -c Release   # Native AOT check (warnings are errors)
```

The SDK is pinned by `global.json`. Package versions are central in `Directory.Packages.props`.

Test projects pick up xunit.v3, Microsoft.Testing.Platform, `Microsoft.AspNetCore.TestHost` and the ASP.NET
framework reference from `tests/Directory.Build.props` — a test csproj only needs its `ProjectReference`s and
any extra packages. Use `TestServer` by default; use Kestrel on `http://127.0.0.1:0` when the test needs real
sockets (TLS, tunnels, dropped connections, a third-party client with its own HTTP stack).

## After every new feature or fix

A change is not "done" until the artifacts below are in sync. Do all of them in the same change unless
there's a reason not to.

1. **Code + tests** (`src/`, `tests/`)
   - New behavior needs a test that fails without it. A bug fix starts with the failing test.
   - The trim/AOT analyzers are on for every shipping project, and CI publishes `samples/Sample.Aot` with
     Native AOT and warnings as errors. A change that introduces reflection or an unannotated dynamic
     dependency is a regression, not a warning to suppress. A new library must be referenced and *used*
     from `Sample.Aot` — the analyzers on the library alone do not see every path an app reaches (a
     collection expression on a `JsonArray`, for one, binds to a `RequiresDynamicCode` overload).
   - A new project goes in `AspNet.slnx`, and shipping projects also in `Build.slnf`.

2. **Documentation site** (`~/Desktop/dev/documentation/src/content/docs/`)
   - Update the library's page (see the map below).
   - Add a **release note** — see the release-note rules below.
   - A brand new page needs a sidebar entry in `src/sidebar-topics.mjs` (under *Server & Cloud → ASP.NET
     Extensions*), and a new package needs a row in the package table on `aspnet/index.mdx`.

3. **Skill** (`skills/<skill>/SKILL.md`)
   - The agent-facing "how to generate correct code" doc. Keep it aligned with the code, and update the
     `triggers:` list when a new public type, method or package is introduced. Changes on `main`/`v*` are
     opened as a PR against `shinyorg/skills` by the Sync Skills workflow.

4. **readme.md** (repo root)
   - Packed into every NuGet package. Update the package table and the library's section.

## Library ↔ docs ↔ skill map

| `src/` project | Docs page (`~/Desktop/dev/documentation/src/content/docs/`) | Skill (`skills/`) |
| --- | --- | --- |
| `Shiny.AspNet.WebHosting` | `webhost/` (own release notes) | `shiny-web-hosting` |
| `Shiny.AspNet.WebDav` | `aspnet/webdav.mdx` | `shiny-aspnet` |
| `Shiny.AspNet.CalDav` | `aspnet/caldav.mdx` | `shiny-aspnet` |
| `Shiny.AspNet.Tus` | `aspnet/tus.mdx` | `shiny-aspnet` |
| `Shiny.AspNet.FileSync` | `aspnet/filesync.mdx` | `shiny-aspnet` |
| `Shiny.AspNet.NuGet` | `aspnet/nuget.mdx` | `shiny-aspnet` |
| `Shiny.AspNet.Npm` | `aspnet/npm.mdx` | `shiny-aspnet` |
| `Shiny.AspNet.DocumentDb` | `aspnet/documentdb.mdx` | `shiny-aspnet` |
| `Shiny.AspNet.JsonPatch` | `aspnet/json-patch.mdx` | `shiny-aspnet` |
| `Shiny.AspNet.Webhooks` | `aspnet/webhooks.mdx` | `shiny-aspnet` |
| `Shiny.AspNet.Idempotency` | `aspnet/idempotency.mdx` | `shiny-aspnet` |
| `Shiny.AspNet.ContentDigest` | `aspnet/content-digest.mdx` | `shiny-aspnet` |
| `Shiny.AspNet.Acme` | `aspnet/acme.mdx` | `shiny-aspnet` |
| `Shiny.AspNet.Discovery` | `aspnet/discovery.mdx` | `shiny-aspnet` |
| `Shiny.AspNet.Ssh` | `aspnet/ssh.mdx` | `shiny-aspnet` |
| `Shiny.AspNet.Tunnels` | `aspnet/tunnel-agents.mdx` | `shiny-aspnet` |

Every `aspnet/` page shares one release-notes file, `aspnet/release-notes.mdx`. The docs are the Astro/Starlight
source at `~/Desktop/dev/documentation`, not the built `shinyorg.github.io`.

## Conventions for libraries (and for porting from Shiny.Net.HttpServer)

Ports are forks, not shims: port idiomatically to ASP.NET rather than emulating the httpserver API.

- **Naming**: package/project `Shiny.AspNet.<Name>`, namespace `Shiny.AspNet.<Name>` (`.Internal` for internals).
- **HTTP surface**: `Microsoft.AspNetCore.Http.HttpContext`, `Task` for request delegates and request-level hooks
  (store interfaces may stay `ValueTask`), `IHeaderDictionary` indexers, `HostString`, and `IProblemDetailsService`
  / `Results.Problem` for problem details.
- **Registration**: `services.Add<Name>(...)` for DI and hosted services; `endpoints.Map<Name>(prefix, ...)` on
  `IEndpointRouteBuilder` returning an `IEndpointConventionBuilder` (usually over a `RouteGroupBuilder`), so
  `RequireAuthorization`, `RequireCors`, `RequireRateLimiting` and `ExcludeFromDescription` come from ASP.NET
  itself; middleware is `app.Use<Name>()`. Per-endpoint behavior is endpoint metadata (attribute + convention
  method sharing one interface).
- **Lifecycle**: anything that needs Kestrel's bound address starts from `IHostedLifecycleService.StartedAsync`
  or `IHostApplicationLifetime.ApplicationStarted` — services on `builder.Services` start *before* Kestrel. Read
  addresses from `IServerAddressesFeature`, never from configuration.
- **Request bodies**: upload routes raise Kestrel's 30 MB `MaxRequestBodySize` for themselves only (via
  `IHttpMaxRequestBodySizeFeature` or `IRequestSizeLimitMetadata`), and an app-supplied limit wins.
- **Drop** httpserver-only plumbing: its CORS / rate-limit / IP-filter wrappers, `HttpServer` lifecycle hooks, its
  OpenAPI types.
- **AOT/trim clean**: map with `RequestDelegate` overloads (or RDG-compatible handlers), System.Text.Json source
  generation, no reflection.
- **Tests**: port the matching upstream tests from `httpserver/tests/Shiny.Net.HttpServer.Tests/<Name>Tests.cs`.
  A bug found while porting is a bug upstream too — fix it there as well, with its own test and release note.

## Versioning

Nerdbank.GitVersioning, configured by `version.json`:

- `version` is the next release. While it carries a prerelease suffix (`1.0.0-beta.{height}`) every build is a
  beta whose last number is the commit height; to cut a release, drop the suffix (`"1.0.0"`), and bump the
  version (and restore a suffix if wanted) for the next cycle. Changing `version.json` resets the height.
- Package versions are public (no `-g<commit>` suffix) only with `PublicRelease=true` — CI sets it for every
  step — or on a ref matching `publicReleaseRefSpec` (`v1.0`, `v1.0.1` tags).
- `assemblyVersion.precision: revision` puts the height in the assembly's fourth component.
- Branches: `main` and `v{major}` build, test and publish; everything else only builds when run manually.

## CI (`.github/workflows/`)

- **build.yml** — on `main`/`v*` pushes (ignoring `skills/` and `.claude-plugin/`) and manual runs, on macOS:
  build `Build.slnf` → test `AspNet.slnx` (trx uploaded as `test-results`, failures surfaced inline) → Native AOT
  publish of `Sample.Aot` → upload packages → publish to nuget.org through trusted publishing (OIDC, user
  `aritchie`, no API key secret).
- **sync-skills.yml** — on `main`/`v*` pushes touching `skills/`: copies every `skills/*` directory into the
  `shiny` plugin of `shinyorg/skills` and opens a PR there. Needs the `SKILLS_REPO_TOKEN` secret.

## Release notes

Release notes live in the documentation repo at
`~/Desktop/dev/documentation/src/content/docs/aspnet/release-notes.mdx` (and `webhost/release-notes.mdx` for
WebHosting).

**Which version does a note go against?** The `version` field in `version.json` — **the raw version portion
only** (strip any prerelease suffix: `1.0.0-beta.{height}` → `1.0.0`).

**Heading style.** Feature/minor releases are headed by `major.minor` (`## 1.1 - June 13, 2026`); patch releases
use the full `major.minor.patch` (`## 1.0.2 - May 30, 2026`).

**If the version isn't released yet (beta / prerelease, or work in progress for the next version):**
- If a `## <version> TBD` heading already exists, **add the note under it**. If you're changing something that
  hasn't shipped yet (already an entry under a `TBD` section), edit that entry rather than adding a duplicate.
- Otherwise **create a new `## <version> TBD` heading** at the top and add the note there.

**If the version is a final release**, the section is dated; add the note under the matching dated section (or
promote the `TBD` section to a dated one when cutting the release).

Each note is a single `<RN>` line — `type="feature|enhancement|fix|breaking"` (`breaking` is its own type), with
`import RN from '/src/components/ReleaseNote.astro';` at the top of the file. Newest version section stays at the
top.
