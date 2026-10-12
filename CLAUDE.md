# CLAUDE.md

Guidance for Claude Code when working in this repository.

## What this is

**Shiny.AspNet** — a set of .NET libraries for ASP.NET Core applications. Repo: `https://github.com/shinyorg/aspnet`. Packages are AOT/trim-clean where possible (`IsAotCompatible` is on for shipping libraries).

- Target framework: `net10.0` (see `Directory.Build.props`, `BaseTargetFramework`).
- Versioning: Nerdbank.GitVersioning via `version.json`. Release branches are named `v{major}`.
- Packages are produced on `Release` builds (`GeneratePackageOnBuild`).
- Package versions are managed centrally in `Directory.Packages.props`.

## Layout

- `src/` — shipping libraries
- `samples/` — runnable samples (`Sample.Web`)
- `skills/` — Claude Code skills shipped by this repo's plugin (`.claude-plugin/`)
- `readme.md` — the NuGet/GitHub readme (also packed via `PackageReadmeFile`)

## Build & test

```bash
dotnet build Build.slnf          # build the shipping set
dotnet build AspNet.slnx         # build everything including samples
dotnet test AspNet.slnx          # run all test projects
```

Test projects live in `tests/Shiny.AspNet.<Name>.Tests` and pick up xunit.v3, Microsoft.Testing.Platform,
`Microsoft.AspNetCore.TestHost` and the ASP.NET framework reference from `tests/Directory.Build.props` —
a test csproj only needs its `ProjectReference`s and any extra packages.

## Libraries ported from Shiny.Net.HttpServer

Several libraries here are ports of add-ons from `~/Desktop/dev/httpserver` (`Shiny.Net.HttpServer.<Name>`),
re-hosted on ASP.NET Core. They are forks, not shims: port idiomatically to ASP.NET rather than
emulating the httpserver API.

- **Naming**: package/project `Shiny.AspNet.<Name>`, namespace `Shiny.AspNet.<Name>` (`.Internal` for internals).
- **HTTP surface**: `Microsoft.AspNetCore.Http.HttpContext`, `Task` instead of `ValueTask` for request
  delegates, `IHeaderDictionary` indexers instead of `GetFirst`/`Set`, `HostString`, `IProblemDetailsService`
  / `Results.Problem` for problem details.
- **Registration**: `services.Add<Name>(...)` for DI/hosted services; `endpoints.Map<Name>(prefix, ...)` on
  `IEndpointRouteBuilder` returning an `IEndpointConventionBuilder` (usually the `RouteGroupBuilder`), so
  `RequireAuthorization`, `RequireCors`, `RequireRateLimiting`, `ExcludeFromDescription`, etc. come from
  ASP.NET itself. Middleware is `app.Use<Name>()` / `IMiddleware`.
- **Drop** httpserver-only plumbing: its own CORS / rate-limit / IP-filter wrappers, `HttpServer` lifecycle
  attachment (use `IHostedService` or `IHostApplicationLifetime`), its OpenAPI types.
- **AOT/trim clean**: shipping projects have `IsAotCompatible`; map with `RequestDelegate` overloads (not
  inferred minimal-API lambdas), System.Text.Json source generation, no reflection.
- **Tests**: port the matching upstream tests from `httpserver/tests/Shiny.Net.HttpServer.Tests/<Name>Tests.cs`
  onto `TestServer` (or Kestrel on port 0 when a real client stack is required).

## Library ↔ docs ↔ skill map

| `src/` project | Docs area (`~/Desktop/dev/documentation/src/content/docs/`) | Skill (`skills/`) |
| --- | --- | --- |
| `Shiny.AspNet.WebHosting` | `webhost/` | `shiny-web-hosting` |

The docs live in an Astro/Starlight site at `~/Desktop/dev/documentation` (source, not the built `shinyorg.github.io`). The sidebar/menu is defined in `~/Desktop/dev/documentation/src/sidebar-topics.mjs`.

## Workflow rules (always apply when changing a library)

When you fix or add behavior in any `src/` library, do **all** of the following as part of the same change — do not stop at the code:

1. **Update `readme.md`** — keep the repo/NuGet readme accurate for the change.
2. **Update the local skill** in `skills/<skill>/SKILL.md` for the matching library (see map above).
3. **Update the Shiny docs** at `~/Desktop/dev/documentation` — edit the matching docs area's `.mdx` content.
4. **New feature → add a menu node** in `~/Desktop/dev/documentation/src/sidebar-topics.mjs` under the correct library entry. Existing-behavior fixes don't need a new node.
5. **Add a release note** in the matching `…/release-notes.mdx` — rules below.

## Release notes

Release notes live at `~/Desktop/dev/documentation/src/content/docs/<area>/release-notes.mdx`.

**Format** (newest first):

```mdx
## v1                                  ← grouped by major version
### 1.1 - June 14, 2026                ← Major.Minor (append .Patch only when non-zero, e.g. 1.3.1)
<RN type="feature">Description</RN>
<RN type="enhancement">Description</RN>
<RN type="fix" breaking={true}>Description</RN>
<RN type="fix" githubNumber={1}>Description (links the GitHub issue/PR)</RN>
```

`type` is one of `feature | enhancement | fix | chore`. Add `breaking={true}` for breaking changes. The `<RN>` component is imported at the top of each file: `import RN from '/src/components/ReleaseNote.astro';`.

**Which version to use:** take the raw `version` from `version.json`, **minus any beta/preview suffix**. Render the heading as `Major.Minor`, adding the patch segment only when it is non-zero.

**Where to put the note:**

- **Beta/preview version** (still in development, not yet released):
  - If that version's heading does **not** exist in the release notes → create a new version entry with the release date set to **`TBD`**.
  - If it **already** exists → append the new `<RN>` line to that existing entry.
- **Non-beta version** (a real release) that does **not** yet exist in the release notes → create a new version entry with the release date set to **today**.

Create the `## v{major}` group heading too if this is the first entry for a new major version.
