# Development

How to run the test site, work on the package and extend it. Build and test commands are in [BUILD.md](BUILD.md); how the code is laid out is in [ARCHITECTURE.md](ARCHITECTURE.md). Commands here were run on macOS unless noted. Anything not run is marked "not verified".

## Local environment

You need the prerequisites from [BUILD.md](BUILD.md) and a first build (`./build.sh`) so the backoffice bundle exists in `src/BlueBillywig.Umbraco/wwwroot/App_Plugins/BlueBillywig/`.

### The host site

`src/UmbracoSite/` is an Umbraco 17.8 site that references the package project directly (`ProjectReference`), so it always runs your working copy. It uses SQLite, and its database is `src/UmbracoSite/umbraco/Data/Umbraco.sqlite.db`. That folder and `umbraco/Logs/` are git-ignored.

Start it on HTTP, port 21183:

```bash
ASPNETCORE_ENVIRONMENT=Development dotnet run --no-launch-profile --project src/UmbracoSite --urls http://localhost:21183
```

This is how the running development site was started (taken from its process list). `src/UmbracoSite/Properties/launchSettings.json` also defines a profile `Umbraco.Web.UI` with `https://localhost:44392;http://localhost:21183`, which `dotnet run --launch-profile Umbraco.Web.UI` would use; not verified.

Port 21183 matters: `npm run generate-client` reads the OpenAPI document from it. Only one site can use it at a time.

### Why HTTP

`src/UmbracoSite/appsettings.Development.json` sets `Umbraco:CMS:Global:UseHttps` to `false`. Umbraco's own default is `true` (`appsettings-schema.Umbraco.Cms.json`: "whether HTTPS should be used"). The launch profile and the command above serve plain HTTP on 21183, so Development turns it off. It also means no local HTTPS certificate is needed, and `scripts/generate-openapi.js` talks to the site over HTTP. This is a development setting only. Never copy it to a production site. What Umbraco does differently with it on (redirects, cookie flags) was not checked here.

### First run and first login

On first run against an empty database, Umbraco must install. Unattended install is controlled by `Umbraco:CMS:Unattended` (`InstallUnattended`, `UnattendedUserName`, `UnattendedUserEmail`, `UnattendedUserPassword`; names read from the schema file). `appsettings.json` only sets `UpgradeUnattended: true`. To install unattended, set these as environment variables for the first start, taking the admin login from the git-ignored secrets file:

```bash
export Umbraco__CMS__Unattended__InstallUnattended=true
export Umbraco__CMS__Unattended__UnattendedUserName="Admin"
export Umbraco__CMS__Unattended__UnattendedUserEmail="$(sed -n 's/^email=//p' secrets/backoffice-admin.env)"
export Umbraco__CMS__Unattended__UnattendedUserPassword="$(sed -n 's/^password=//p' secrets/backoffice-admin.env)"
```

Not verified end to end: the existing database was installed before this was written. To start over, stop the site and delete `src/UmbracoSite/umbraco/Data/`.

Then open `http://localhost:21183/umbraco` and sign in with the email and password from `secrets/backoffice-admin.env`. Don't print that file into logs, tickets or chat.

The test view `src/UmbracoSite/Views/videoPage.cshtml` expects a document type with the alias `videoPage` and the properties `video` (Blue Billywig Video), `blocks` (Block List) and `body` (rich text). Those document types live in the local database, not in the repository. Create them in Settings if your database is new.

## Credentials

The package reads the config section `BlueBillywig` (class `BlueBillywigOptions`):

| Key | Meaning |
|---|---|
| `Publication` | Publication name, the `X` in `X.bbvms.com` |
| `BaseUrl` | SAPI origin, https only. Default `https://{Publication}.bbvms.com`. Set it for a publication on a dev OVP |
| `ApiId`, `ApiSecret` | Read-write API token |
| `ReadOnlyApiId`, `ReadOnlyApiSecret` | Read-only token, used only where a token must reach a browser (the clip preview in the library) |
| `GateSharedSecret` | Content protection shared secret |
| `DeleteUserGroups` | User group aliases that may delete (default `admin`, `editor`) |
| `DisableLazyLoader`, `ForceLazyLoader`, `LazyLoadOEmbed`, `LazyLoadOEmbedChannel`, `LazyLoadComponent`, `ShowEmbedProblems` | Rendering switches, see [INSTALL.md](INSTALL.md) |

A value set in config wins over one saved from the settings dashboard, and the dashboard then shows that field as pinned. Without config, values are saved in Umbraco's key-value table, secrets encrypted with ASP.NET Data Protection.

### `appsettings.Local.json`

`src/UmbracoSite/appsettings.Local.json` is git-ignored. `Program.cs` loads it, optional and reloading on change, **only in Debug builds** (`#if DEBUG`), after the other config files. `dotnet run` builds Debug, so it is loaded; `./build.sh` builds Release, which never reads it. It currently holds a `BlueBillywig` section with the keys `Publication`, `BaseUrl`, `ApiId` and `ApiSecret`. Create it by hand:

```json
{
  "BlueBillywig": {
    "Publication": "<development publication>",
    "BaseUrl": "<its SAPI origin, https>",
    "ApiId": "<numeric id>",
    "ApiSecret": "<secret>"
  }
}
```

Add the `Umbraco:CMS:Imaging:HMACSecretKey` value here too if you want resized-image URLs to survive restarts (see "The image-resizing key" below). Get the values from whoever manages the development publication's API token. Don't commit them or paste them anywhere. The repository also has a git-ignored `secrets/` folder at its root; today it holds one file, the backoffice admin login for the test site (a `key=value` file with an email and a password). Treat anything in it as secret and never print it. To check which keys are set without showing values: `jq -r '.BlueBillywig | keys' src/UmbracoSite/appsettings.Local.json`.

### The image-resizing key

The demo site's `Umbraco:CMS:Imaging:HMACSecretKey` is not in the tracked `appsettings.json` (it is an empty string there). Put your own value in the git-ignored `src/UmbracoSite/appsettings.Local.json` under `Umbraco` > `CMS` > `Imaging` > `HMACSecretKey`, or set the environment variable `Umbraco__CMS__Imaging__HMACSecretKey`; any long random string will do. If neither is set, `Program.cs` makes a random key for the life of the process, so resized-image URLs work but change after a restart. A key that was committed earlier is still in the git history: treat it as published and do not reuse it.

### Alternatives

Environment variables work in any build configuration, with a double underscore for nesting: `BlueBillywig__ApiSecret`.

.NET user-secrets also work in principle (Development environment), but `UmbracoSite.csproj` has no `UserSecretsId` today. `dotnet user-secrets init --project src/UmbracoSite` would add one to the csproj, which is a committed file, so agree that first. Not verified.

### The development publication

Develop and test only against the development publication on the dev OVP (as used in [PARITY.md](PARITY.md)). Never point a development site, a test or a script at a customer publication, and never write to a publication you don't own for this project. Anything that writes to SAPI must be checked on clips this project created. See "Proven" in PARITY.md.

## Watch mode for the client

```bash
cd src/BlueBillywig.Umbraco/Client
npm run watch
```

`watch` is `tsc && vite build --watch`: it type-checks once, then rebuilds into `wwwroot/App_Plugins/BlueBillywig/` on each save. TypeScript errors after the first check are not shown by Vite; run `npx tsc` to check. Reload the backoffice after a rebuild. Chunk file names contain a content hash, so they change. Whether the running site serves a brand-new chunk name without a restart was not verified; if you see a 404 for a chunk, restart the site.

C# changes need a restart: stop the site and run the command again.

## Client tests

`npm test` in `src/BlueBillywig.Umbraco/Client` runs Vitest (a dev-only dependency; `build.sh` and the pull request workflow run it after the client build). Configuration is `vitest.config.ts` (Node environment, no DOM package); tests are in `Client/test/`, outside `src/`, so the production `tsc` and the bundle do not see them. Tests are not type-checked (`tsc` covers `src/` only).

- Pure helpers in `src/` are imported directly: keyboard and motion rules, upload limits (also compared with the constants in `Upload/UploadValidation.cs`, read as text), the video property value, error and formatting helpers.
- `public/bb-lazy.js` and `public/bb-cta-form-overlay.js` are loaded as text into a Node `vm` context with a small hand-written fake of the few globals they touch (`test/helpers/fake-dom.ts`). That covers their security rules: the per-request key, the https and host checks, no inline script, the CTA endpoint rule, the remembered-submission entry. It is not a browser.
- Translations (`test/localization.test.ts`): every culture file under `src/localization/` has exactly the English keys, no empty value and the same `{0}` placeholders; every `bluebillywig_*` key the code uses exists in English; a missing text reads English; the visitor JSON files under `Localization/Visitor/` have the same parity. The xUnit side (`Localization/VisitorTextTests.cs`) checks the embedded copies, the language choice and the renderer and gate output.
- Not covered, because it needs a DOM runner: the Lit elements, real HTML parsing in the lazy loader (the tests supply the parsed nodes), layout and focus behaviour, `IntersectionObserver` timing, and a non-exported helper inside an element file.

## Debugging

- **Console log.** In Development, Serilog writes to the console (`appsettings.Development.json`). SAPI failures are logged as `SAPI {Method} {Path} returned {Status}: {Body}`. This is the only place an upstream error body appears.
- **File log.** `src/UmbracoSite/umbraco/Logs/UmbracoTraceLog.*.json`.
- **Backoffice.** Use the browser's developer tools. A Debug build serves the bundle's source maps, so TypeScript sources show in the Sources tab. The Release build and the package leave the maps out.
- **API.** Backoffice endpoints sit under `/umbraco/bluebillywig/api/v1/` and answer 401 without a backoffice session (`GET .../settings` returned 401 unauthenticated). The OpenAPI document is public: `http://localhost:21183/umbraco/swagger/bluebillywigumbraco/swagger.json` (lists every operation of every controller: settings, gate, status, picker lookups, library, clip edit, CTA, upload, analytics).
- **Static files.** `http://localhost:21183/App_Plugins/BlueBillywig/bb-lazy.js` should answer 200. The sitemap is at `http://localhost:21183/video-sitemap.xml`.
- **Consent and CSP switches.** In the Development environment `src/UmbracoSite/Testing/DevTestHooks.cs` (compiled in Debug only) registers a consent provider and a nonce provider driven by cookies: `bb_test_consent=deny` makes consent refuse; `bb_test_csp=1` sends a nonce-based Content-Security-Policy and the matching nonce; `bb_test_csp=mismatch` sends a different nonce than the policy lists (a negative control). The package itself ships neither.

## How the pieces fit

```text
Backoffice (Lit elements)  -->  generated TS client  -->  Controllers  -->  IBlueBillywigClient  -->  SAPI
Frontend (Razor / Delivery API) --> IBlueBillywigEmbedRenderer ------------^
```

- `Client/src/` holds the backoffice UI. `Client/src/api/` is generated from the controllers.
- `Controllers/` serve the backoffice only.
- `Sapi/BlueBillywigClient.cs` is the one door to SAPI.
- `Embedding/BlueBillywigEmbedRenderer.cs` is the one place embed HTML is produced.
- `Composing/` registers everything with Umbraco's DI.

Details and file paths: [ARCHITECTURE.md](ARCHITECTURE.md).

## Conventions

- **C#:** net10.0, nullable enabled, implicit usings, file-scoped namespaces. Package versions only in `Directory.Packages.props`.
- **SAPI access:** only through `IBlueBillywigClient`. No `HttpClient` or SDK call elsewhere.
- **Results, not exceptions:** the client returns `SapiResult`. Callers check `Ok`.
- **Endpoints:** derive from `BlueBillywigControllerBase`, add an `[Authorize(Policy = ...)]` (see "Authorization and the sweep" below), return `FromSapi(result)` for upstream failures (502, so an outage differs from an empty list).
- **HTML:** every value that came from SAPI or from stored JSON is encoded before it is printed. Validate ids, publications, playouts and heights with the `IsValid*` helpers on `BlueBillywigVideo`.
- **Path segments** sent to SAPI go through `BlueBillywigUrls.Segment`.
- **TypeScript:** Lit elements with `UmbElementMixin`, calls through the generated SDK wrapped in `tryExecute`. Never edit `*.gen.ts`.
- **No new libraries, test tools, linters, build plugins or CI providers** without asking the owner.
- **Comments** explain why, and mention the reference plugin's behaviour that is being matched when there is one.
- **Parity:** a feature isn't done until its row in [PARITY.md](PARITY.md) can say Proven. Don't edit the status yourself; the audit does.

## Security invariants

From the end of PARITY.md (status there). Where each is enforced and tested today:

| # | Invariant | Where it is enforced | Test |
|---|---|---|---|
| I1 | The read-write secret and token never reach a browser or HTML | `GetSettings` returns `HasApiSecret`, never the secret; `GetReadOnlyToken` is the only token API (used by `library/clips/{id}/preview`) | `ReadWriteSecretTests` in `SecurityInvariantTests.cs` |
| I2 | SAPI error bodies never reach responses or HTML | `BlueBillywigClient` logs the body and returns `SapiResult.Failure` with a generic message | `SecurityInvariantTests.cs`, `BlueBillywigClientTests` |
| I3 | Every endpoint requires backoffice auth, except deliberately public ones | `[Authorize(Policy = ...)]` on every controller or action | The sweep, class `EndpointAuthorizationTests` in `SecurityInvariantTests.cs` |
| I4 | SAPI path segments are encoded | `BlueBillywigUrls.Segment` | `PathEscapingTests` |
| I5 | Values from SAPI are encoded in HTML | `EmbedHtml.Encoder`, `Enc.Encode` in the renderer | `RendererTests` and others |
| I6 | Solr filter inputs validated | `LibraryQuery` (and `VideoController`) | `SearchValidationTests`, `LibraryQueryTests` |

## Authorization and the sweep

Policies (`Authorization/BlueBillywigPolicies.cs`): `BlueBillywigPolicies.Library` (the Blue Billywig section; reading, upload, edit, CTA), `BlueBillywigPolicies.Delete` (the section plus a delete user group; every `[HttpDelete]` and `bulk-delete`), `BlueBillywigPolicies.Overview` (Content, Settings or Blue Billywig section; `status`), and Umbraco's `AuthorizationPolicies.SectionAccessSettings` and `SectionAccessContent`. Pick the narrowest that fits.

The sweep is `EndpointAuthorizationTests` in `tests/BlueBillywig.Umbraco.Tests/SecurityInvariantTests.cs`. It reflects over every controller in the package assembly and fails when:

- a routed action has no `[Authorize]`, or none with a policy in `KnownPolicies`;
- an action or controller has `[AllowAnonymous]`;
- a public method on a controller has no `[Http...]` attribute (it would still be reachable on the class route);
- an `[HttpDelete]` action lacks `BlueBillywigPolicies.Delete`;
- a Settings controller action lacks the Settings policy.

**A new policy** must be added to `KnownPolicies` in that class. **A deliberately anonymous route** (the only one today is the video sitemap) needs both: `[AllowAnonymous]` on the action, and an entry in the `DeliberatelyPublic` dictionary with the key `"ControllerName.ActionName"` and the reason in the value. The action name must be unique in the package: the opt-out check matches by member name. Think twice: anything anonymous is on the public internet, so it may show only what a public page already shows. A destructive action that should not be an `[HttpDelete]` (like `cta/remove`) is a POST with the Library policy on purpose; say why in a comment.

## Adding an endpoint, end to end

Illustration (the SAPI path `/sapi/note` is made up; `folders` already exists): a read-only `GET /umbraco/bluebillywig/api/v1/library/notes`.

1. **Controller.** Add `Controllers/NotesController.cs`:

   ```csharp
   [Authorize(Policy = BlueBillywigPolicies.Library)]
   public sealed class NotesController : BlueBillywigControllerBase
   {
       private readonly IBlueBillywigClient _client;
       public NotesController(IBlueBillywigClient client) => _client = client;

       [HttpGet("library/notes")]
       [ProducesResponseType<IReadOnlyList<NoteResponseModel>>(StatusCodes.Status200OK)]
       public async Task<IActionResult> Notes(CancellationToken ct)
       {
           SapiResult result = await _client.GetAsync("/sapi/note", cacheFor: TimeSpan.FromMinutes(5), ct: ct);
           return result.Ok ? Ok(/* map to a response model */) : FromSapi(result);
       }
   }
   ```

   Rules: the namespace must start with `BlueBillywig.Umbraco.Controllers` (`OperationIds.CanHandle` in `Composing/BlueBillywigApiComposer.cs` only names operations from there); response models go in `ViewModels/`; the generated TypeScript function name comes from the verb and route (`GET clips/{id}` became `getClipsById`); use the narrowest policy that fits (above); encode path segments; validate input before it reaches a SAPI query.
2. **Build and start the site** on port 21183 (see above). Check the operation shows in the swagger document.
3. **Regenerate the client.** `cd src/BlueBillywig.Umbraco/Client && npm run generate-client`. Review `git diff src/api` for exactly your operation.
4. **Use it in TypeScript.**

   ```ts
   import { tryExecute } from "@umbraco-cms/backoffice/resources";
   import { getLibraryNotes } from "../api/sdk.gen.js";
   const { data, error } = await tryExecute(this, getLibraryNotes());
   ```

   The exact export name is generated; read `src/api/sdk.gen.ts` to see it. The entry point (`entrypoints/entrypoint.ts`) already configures the client with the backoffice auth context, so no token handling is needed. Compare with `embed/video-picker.element.ts` for the current pattern.
5. **Test.** Add xUnit tests in `tests/BlueBillywig.Umbraco.Tests/` using `FakeClient` (`Fakes.cs`): register a response with `client.Json("/sapi/note", "...")` and assert on the mapped result and on `client.Requests`. Pure helpers (validation, escaping) are `internal static` and reachable from tests through `InternalsVisibleTo`.
6. **Run the sweep.** `dotnet test tests/BlueBillywig.Umbraco.Tests --filter "FullyQualifiedName~EndpointAuthorizationTests"` must pass; it fails when the new action has no known policy.
7. **Build everything** with `./build.sh`, then try it in the backoffice.
8. **Record it** in [ARCHITECTURE.md](ARCHITECTURE.md). A PARITY row is changed by the audit, not by you.

## Adding a backoffice element

Elements are Lit custom elements with `UmbElementMixin`, in `Client/src/<area>/`. Take `library/library-videos.element.ts` as the smallest example (a section view that hosts `bb-clip-browser` and opens a modal).

1. **Write the element** (`@customElement("bb-...")`, default export, a `HTMLElementTagNameMap` entry). Call the API through the generated SDK wrapped in `tryExecute`; show an error state, not an empty state, when the call fails. Start views with `<bb-credentials-banner>` if they need credentials. Shadow DOM: page CSS does not reach in, so style inside the element (Uppy's CSS is imported as text for that reason).
2. **Register it** in the manifest of its area (`library/manifest.ts`, `dashboards/manifest.ts`, `embed/manifest.ts`, `rte/manifest.ts`), with an alias `BlueBillywig.<Type>.<Name>` and a lazy `element: () => import("./x.element.js")` (or `js:`). Section views carry the condition `Umb.Condition.SectionAlias` for `BlueBillywig.Section`. A new area needs its manifests added to `bundle.manifests.ts`. A modal needs a `UmbModalToken` in `library/modal-tokens.ts`.
3. **Build and check.** `npm run build` in `Client/` (type check plus bundle) or `npm run watch`; reload the backoffice. Chunk names contain a hash.
4. **Try it in the backoffice** with a user who has the right section; use the browser, not just the API, because a PARITY row for editor-facing behaviour needs the UI to have been used.
5. If you add a **third-party npm package** that ends up in the bundle: ask first (CLAUDE.md), then add its notice to [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md) and re-run the licence scan.

## Adding or changing a translated text

Two places, because the texts run in two places (details in [ARCHITECTURE.md](ARCHITECTURE.md#translations), status in [TRANSLATIONS.md](TRANSLATIONS.md)).

- **Backoffice text.** Add the key under `bluebillywig` in `Client/src/localization/en.ts` first, then the same key in `da`, `de`, `fi`, `fr`, `nb`, `nl` and `sv` (the Vitest parity test fails otherwise). Use `this.localize.term("bluebillywig_<name>", arg0, ...)` in an element, `<umb-localize key="bluebillywig_<name>">` in a template, or `"#bluebillywig_<name>"` as a manifest label. `{0}`, `{1}` are the arguments. A helper that is not an element takes a `Term` function (`localization/terms.ts`) and defaults to English, so its tests need no element. A new nested sentence that includes another word (a noun) uses the `nounOf` pattern in `library/errors.ts`; avoid building sentences from pieces in a language with grammatical gender or case.
- **Visitor text** (anything the renderer, the gate message or the two public scripts print). Add the key to all eight files under `Localization/Visitor/` (`en.json` is the source), ask for it with `VisitorText.Get("<key>")` in C#, and, if a browser script needs it, add the key to `LoaderTextKeys` or `OverlayTextKeys` in `BlueBillywigEmbedRenderer` and read it with the script's own `t(key, english)` function. Never translate text an editor typed.
- Add the new strings to the right table in [TRANSLATIONS.md](TRANSLATIONS.md) with their provenance (reused from the reference plugin, adapted, or written for this package), and say which still need a native review.

## Adding a test

xUnit; the project is `tests/BlueBillywig.Umbraco.Tests/`. No Docker, no network, no running site.

- Put the test next to its area (`Cta/`, `Gate/`, `Seo/` have subfolders and their own `*TestSupport.cs`). Name facts as sentences (`A_stored_value_keeps_its_own_choice_whatever_the_switch_says`).
- Use the fakes in `Fakes.cs`: `FakeSettings`, `FakeClient` (`Responses`, `Json(path, json)`, `Handler` for scripted answers, `Requests` and `Calls` to assert what was sent, `IsConnected`, `ReadOnlyToken`), `FixedConsent`, `FixedNonce`, `Rig` (builds a renderer).
- Internals are visible to the test project (`InternalsVisibleTo`), so `internal static` helpers can be called directly.
- Writes to SAPI: assert the request that went out (`Calls`) and the generic error text when it fails; never assert on an upstream body.
- Security-relevant output (HTML, JSON-LD, tokens, responses): add a case that feeds a hostile value (script tags, quotes, a sentinel secret) and checks it is encoded or absent. `SecurityInvariantTests.cs` shows the pattern.
- Run one class: `dotnet test tests/BlueBillywig.Umbraco.Tests --filter "FullyQualifiedName~RendererTests"`. Pipe long output through `bb-brain squeeze`.

## The development player and the development CDN: what could not be proven there

The development publication on the dev OVP was used for everything in [PARITY.md](PARITY.md). These properties of it limit what that evidence shows:

- **The dev player did not load.** Its player script answered 404: the dev publication's player setting points at retired media hosts. So whether the real player renders a call to action's form class into the page (C1, C4, C6), whether chapters work against the real player object (E16, tested against a simulated player), the shorts player (E3) and playback of protected clips (G1) were not observed. Rows that depend on these say so.
- **The dev CDN caches the clip script's redirect for 90 seconds without keying on the query string.** A first visitor's token was served to an anonymous request (G6). Production's CDN configuration varies on `jwt`. Do not read a leak there as a package bug, and do not test the gate through that CDN without that in mind.
- **The dev SAPI answers list queries from replicas that disagree** (a result count of 202 against 210, alternating), so new clips and date filters flap for a moment (L2). Read back twice before you call a write lost.
- **The read-write second key pair** on the dev publication is not read-only, so the read-only token path (S2, L7) could not be observed.

Develop and write only against clips this project created. Don't point a development site at a customer publication.

## Uppy is pinned to 5.x

The upload view uses `@uppy/core` 5.2.0, `@uppy/aws-s3` 5.1.0 and `@uppy/dashboard` 5.1.1, pinned exactly in `Client/package.json`. Don't bump them to 6.x.

Uppy 6.0.0 rewrote `@uppy/aws-s3` and removed the `createMultipartUpload`, `signPart` and `completeMultipartUpload` callbacks (see the `@uppy/aws-s3` changelog). Our flow depends on them: SAPI creates the multipart upload and signs every part, and completion goes through `/sapi/tus/{id}/complete`. Uppy 6 expects to create and sign the upload itself through `signRequest`.

Exit plan, decided 2026-11-04:

1. A server-side contract for Uppy 6's `signRequest` mode is being designed in `formatengine-go` (separate work, its own ADO ticket and PR). It adds routes only; existing TUS routes don't change.
2. When that ships, move `Client/src/library/library-upload.element.ts` and `Upload/UploadService.cs` to the new contract and upgrade the three Uppy packages together. Re-run the licence scan ([LICENSES.md](LICENSES.md)) and regenerate `THIRD-PARTY-NOTICES.md` with the procedure at its end.
3. If 5.x becomes a security problem first, the fallback is a small custom Uppy plugin that PUTs the parts to the presigned URLs SAPI already returns.

## Screenshots

The images in `docs/images/` (used in the README, [USER-GUIDE.md](USER-GUIDE.md) and [INSTALL.md](INSTALL.md)) were captured from the demo site (`src/UmbracoSite`) running against the development publication, with a headless Chrome at 1400 px width. Library views are filtered to generic test clips. Retake them when the UI changes; a screenshot that no longer matches the text is worse than none.

Some of them (the library views, the picker, the bulk selection, the analytics dashboard and the front-end page) show video titles that exist in the development publication. Review them before the repository is made public. The analytics image has its top videos list shortened to one test clip; the capture script refuses to save an image whose visible text mentions a term on a block list, but it cannot read text inside thumbnails, so look at every image.
