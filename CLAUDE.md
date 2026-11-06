# CLAUDE.md

Guidance for Claude Code (claude.ai/code) when working in this repository.

## What this is

`BlueBillywig.Umbraco`: Blue Billywig OVP (SAPI) integration for **Umbraco 17** (.NET 10, developed against 17.8.0), the counterpart of the reference plugin (version 2.0.5). Version `1.0.0-rc.1`, a pre-release published on nuget.org. Remote: `bluebillywig/umbraco-plugin`, to be made public at release time. Licence MIT (`LICENSE`, `PackageLicenseExpression`). The goal is functional parity, tracked row by row in `docs/PARITY.md`. Don't mark a row Proven without the evidence it asks for.

## Commands

- **Build and test everything:** `./build.sh` (client, then .NET, then tests). `./build.sh pack` adds `artifacts/BlueBillywig.Umbraco.<version>.nupkg`.
- **Client only:** `cd src/BlueBillywig.Umbraco/Client && npm ci && npm run build`. Watch: `npm run watch`.
- **Order matters:** the client must build before `dotnet build`/`pack`, or the package silently ships without its backoffice files (Static Web Assets are discovered at evaluation time).
- **Test command:** `dotnet test BlueBillywig.Umbraco.slnx --configuration Release`; one class: `dotnet test tests/BlueBillywig.Umbraco.Tests --filter "FullyQualifiedName~RendererTests"`. xUnit, no Docker, no network.
- **Tests:** about a thousand xUnit tests plus a Vitest suite (`cd src/BlueBillywig.Umbraco/Client && npm test`, Node, no DOM); the counts change, don't quote them.
- **Run the test site:** `ASPNETCORE_ENVIRONMENT=Development dotnet run --no-launch-profile --project src/UmbracoSite --urls http://localhost:21183`. Port 21183 is what `npm run generate-client` reads. Back office at `/umbraco`; the login is in the git-ignored `secrets/backoffice-admin.env`.
- **Regenerate the TypeScript API client** (after any controller change, with the site running): `cd src/BlueBillywig.Umbraco/Client && npm run generate-client`. The script exits 0 even on failure; read its output.
- Long output: pipe through `bb-brain squeeze`.
- Details: `docs/BUILD.md`, `docs/DEVELOPMENT.md`.

## Architecture

One package project, `src/BlueBillywig.Umbraco/` (Razor SDK, net10.0); a host site `src/UmbracoSite/` (project reference, SQLite); tests in `tests/BlueBillywig.Umbraco.Tests/`. Full description: `docs/ARCHITECTURE.md`.

- **`Sapi/BlueBillywigClient`** (`IBlueBillywigClient`): the single chokepoint for SAPI. Wraps `BlueBillywig.Sapi`. **Never throws to callers**: returns `SapiResult`. Upstream error bodies are logged, never returned. **Errors are not cached.** The cache is cleared when settings change. `GetReadOnlyToken()` is the only way to mint a token for a browser (used by the library preview) and uses the read-only pair only.
- **`Configuration/`**: `BlueBillywigOptions` (config section `BlueBillywig`, wins over stored values) and `BlueBillywigSettingsStore` (Umbraco key-value table, secrets protected by Data Protection, write-only through the API).
- **`Embedding/BlueBillywigEmbedRenderer`**: the render chokepoint. Every embed (view component, `<bb-video>` tag helper, partials, oEmbed) goes through `RenderAsync`. Assets ship first and always, then the consent gate, then the player. The content gate (`Gate/`), JSON-LD (`Seo/`) and the call to action overlay assets attach inside this method; don't produce embed HTML anywhere else. Rich text stores only references; `Embedding/RichTextEmbedRewriter` turns them into embeds per request through this method.
- **`Embedding/EmbedHtml`**: treats SAPI embed code as untrusted (tag allowlist, host allowlist, own nonce only).
- **Consent / nonce**: `IBlueBillywigConsentProvider` (default grants) and `IBlueBillywigCspNonceProvider` (default none), registered with `TryAdd` so a site can replace them. Nothing from the player host loads before consent.
- **`Controllers/`**: backoffice API under `/umbraco/bluebillywig/api/v1/`, published as OpenAPI document `bluebillywigumbraco`; the TypeScript client in `Client/src/api/` is generated from it. Domain logic is in `Library/`, `Editing/`, `Upload/`, `Cta/`, `Analytics/`, `Gate/`, `Seo/`; `Sapi/IBlueBillywigClient` is also the documented point for site code to read from or replace. The only anonymous route is `/video-sitemap.xml` (`Seo/VideoSitemapController`).
- **Authorization**: policies in `Authorization/BlueBillywigPolicies.cs` (`Library` = the Blue Billywig section, `Delete` = section plus a delete user group, `Overview`) next to Umbraco's Settings and Content policies. `EndpointAuthorizationTests` (in `tests/.../SecurityInvariantTests.cs`) sweeps every action; a new anonymous route needs an entry in its `DeliberatelyPublic` list.
- **`Client/src/`**: Lit elements for the backoffice (settings and analytics dashboards, the Blue Billywig section with its views and modals, the video property editor, the rich text toolbar button), built by Vite into `wwwroot/App_Plugins/BlueBillywig/` (git-ignored). `Client/public/` holds `bb-lazy.js`, `bb-lazy.css`, `bb-cta-form-overlay.js`, `bb-cta-form-overlay.css` and `umbraco-package.json`.
- **`Composing/`**: four `IComposer`s (core, embed, gate and sitemap, API document), discovered by Umbraco.

## Conventions that matter

- **Auth first**: every backoffice endpoint has an `[Authorize(Policy = ...)]`. A public endpoint must be deliberate and documented. Keep failed SAPI calls as 502 via `FromSapi`.
- **Encode everything** that came from SAPI or stored JSON before it reaches HTML; validate ids, publications, playouts, heights; escape SAPI path segments with `BlueBillywigUrls.Segment`; sanitise and escape search input (`VideoController`).
- **Secrets never leave the server**: no read-write secret or token in responses, HTML or logs. Settings responses say whether a secret is set, never what it is.
- **No raw SAPI calls** outside `IBlueBillywigClient`.
- **Central package versions** in `Directory.Packages.props`; no versions in project files.
- **Never edit `*.gen.ts`**; regenerate.
- **Version** is in three files (csproj, `Client/public/umbraco-package.json`, `Client/package.json`); see `docs/RELEASING.md`.
- **Translations**: user-facing text is a key, not a literal. Backoffice text: `Client/src/localization/en.ts` first, then the other seven files (`this.localize.term("bluebillywig_<name>")`). Text a visitor sees (renderer, gate message, public scripts): `Localization/Visitor/en.json` and the other seven, read with `VisitorText.Get`. Tests fail when a file lacks a key. `docs/TRANSLATIONS.md` lists what still needs a native review.
- **Comments** say why, and name the reference plugin's behaviour being matched.
- Test with the fakes in `tests/.../Fakes.cs`; port the reference plugin's test cases where PARITY.md says so.

## Rules

- **Dev publication only.** Develop and test against the development publication on the dev OVP. Never a customer publication. Writes to SAPI are verified by reading back, on clips this project created.
- **Never print secrets.** Don't `cat` or echo `src/UmbracoSite/appsettings.Local.json` or `secrets/*`. To inspect: `jq -r '.BlueBillywig | keys' src/UmbracoSite/appsettings.Local.json`. Keep secrets out of docs, tickets, PR text and commits. `appsettings.Local.json` loads in Debug builds only.
- **No new libraries, frameworks, test tools, linters, build plugins or CI providers without asking.** (Uppy is approved and pinned to 5.x; see `docs/DEVELOPMENT.md`.)
- **No literal IPs** in scripts, config or docs.
- **Do not commit or push** unless asked. PRs go to `bluebillywig/umbraco-plugin`, never to a fork. Never force-push without asking.
- **Releasing**: the release workflows are in `.github/workflows/` (`docs/RELEASING.md`). Never run them, publish to NuGet or list on the Marketplace without the owner's explicit decision. Don't weaken them.
- Third-party code added to what ships needs an entry in `THIRD-PARTY-NOTICES.md`.
- Don't name the reference plugin's product or its repository or path in the repo; say "the reference plugin".

## Docs

`README.md` (entry), `docs/INSTALL.md` (site owners), `docs/USER-GUIDE.md` (editors), `docs/BUILD.md`, `docs/DEVELOPMENT.md`, `docs/ARCHITECTURE.md`, `docs/RELEASING.md`, `docs/PARITY.md`, `docs/TRANSLATIONS.md`, `docs/LICENSES.md`, `THIRD-PARTY-NOTICES.md`. Keep them true: when code changes behaviour, update the doc in the same change.
