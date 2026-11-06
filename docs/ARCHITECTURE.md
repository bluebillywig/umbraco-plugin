# Architecture

The package as it is built today. Only code that exists is described; what is proven is in [PARITY.md](PARITY.md), which is the source of truth for status. Paths are relative to `src/BlueBillywig.Umbraco/` unless they start with `tests/` or `src/UmbracoSite/`. Check the file when in doubt: the code moves faster than this document.

## Layers

```text
Backoffice UI          Client/src/ (Lit elements, Vite bundle, generated TypeScript client in Client/src/api/)
   |
Backoffice API         Controllers/            /umbraco/bluebillywig/api/v1/...   (authorization policies, see below)
   |
Domain services        Library/ Editing/ Upload/ Cta/ Analytics/ Gate/ Seo/
   |
Settings               Configuration/          options + persisted settings + cleanup
SAPI client            Sapi/                   the single door to Blue Billywig
   |
Rendering              Embedding/              render chokepoint, consent, nonce, oEmbed, rich text, converters
Razor                  Views/Partials/         block and video partials
Wiring                 Composing/              four IComposer classes, discovered by Umbraco
```

One project, `BlueBillywig.Umbraco.csproj` (net10.0, Razor SDK). The Vite output goes to its `wwwroot/App_Plugins/BlueBillywig/` and ships as Static Web Assets. The host site `src/UmbracoSite/` references it by project reference; tests are in `tests/BlueBillywig.Umbraco.Tests/`.

## Folders

| Folder | What is in it |
|---|---|
| `Authorization/` | `BlueBillywigPolicies.cs`: policy names, requirements, handlers, `IBackOfficeUserInfo` |
| `Analytics/` | `AnalyticsService` (dashboard and per-clip numbers, 5 minute cache), `AnalyticsParser` |
| `Composing/` | `BlueBillywigComposer`, `BlueBillywigEmbedComposer`, `BlueBillywigGateComposer`, `BlueBillywigApiComposer` |
| `Configuration/` | `BlueBillywigOptions`, `BlueBillywigSettings`, `BlueBillywigSettingsStore`, `BlueBillywigCleanup` |
| `Controllers/` | Backoffice API: `Settings`, `Gate`, `Status`, `Video` (picker), `Library`, `ClipEdit`, `Cta`, `Upload`, `Analytics` controllers; base class `BlueBillywigControllerBase` |
| `Cta/` | Call to action: `CtaService`, `CtaTranslator` (config to TimelineNG), `CtaSavePlanner`, `CtaTimelineLocator`, validators |
| `Editing/` | `ClipEditService` (metadata, tags, status, CPP, playout, language, embargo, subtitles, thumbnails, bulk), `ClipEditRules`, `SubtitleRules`, `ThumbnailValidator`, `ThumbnailState`, `ClipPlayoutStore`, `CustomFieldDefinitions` |
| `Embedding/` | Render chokepoint, `EmbedHtml`, stored value, oEmbed, rich text rewriter, property value converters, view component and tag helpers, consent and nonce interfaces |
| `Gate/` | Content gate: `ContentGate`, `ContentGateFactory`, `GateJwt`, `GateInjector`, `GatePepper`, `GateSettingsStore`, `ICppLookup`, member viewer provider, health tally |
| `Library/` | `LibraryService`, `LibraryQuery` (validation and Solr escaping), `LibraryMapper` |
| `Sapi/` | `IBlueBillywigClient`, `BlueBillywigClient`, `SapiResult`, `SapiJson` |
| `Seo/` | `VideoJsonLd`, `VideoSitemap`, `VideoSitemapService`, `VideoSitemapController`, `UmbracoSitemapPageSource`, `VideoReferences` |
| `Localization/` | `VisitorText`: the texts a visitor sees (consent placeholder, gate message, labels), one embedded JSON file per language in `Localization/Visitor/` |
| `Upload/` | `UploadService`, `UploadValidation`, `IPendingUploads` |
| `ViewModels/` | Request and response models of the backoffice API |
| `Views/Partials/` | Block and video partials |
| `Client/src/` | `api/` (generated, never edit), `dashboards/`, `embed/`, `entrypoints/`, `library/`, `localization/` (backoffice translations, one file per culture), `rte/`, `shared/` |
| `Client/public/` | Shipped as is: `bb-lazy.js`, `bb-lazy.css`, `bb-cta-form-overlay.js`, `bb-cta-form-overlay.css`, `umbraco-package.json` |

## Wiring (`Composing/`)

| Composer | Registers |
|---|---|
| `BlueBillywigComposer` | `BlueBillywigOptions` from config section `BlueBillywig`; memory cache; settings store; `IBlueBillywigClient`; `IThumbnailState`, `IClipPlayoutStore`, `ILibraryService`, `IClipEditService`, `IAnalyticsService`, `IBlueBillywigCleanup`, `IPendingUploads`, `IUploadService`, `ICtaService` (`ICtaIdGenerator` with `TryAdd`); the authorization handlers and the policies (see below). All singletons |
| `BlueBillywigEmbedComposer` | `IHttpContextAccessor`; `IBlueBillywigConsentProvider`, `IBlueBillywigCspNonceProvider` and `IBlueBillywigEmbedDiagnostics` (who sees the embed problem marker) with `TryAddSingleton` (a site's own registration wins); the renderer as `IBlueBillywigEmbedRenderer` and `IBlueBillywigAssets`; the oEmbed provider via `builder.EmbedProviders().Append<...>()` |
| `BlueBillywigGateComposer` | Gate health tally, `GateSettingsStore` (as `IGateConfigStore`, `IGateSettings`, `IGateSecretStore`), `ICppLookup` (`SapiCppLookup`), `ContentGateFactory`, the video sitemap service and page source. With `TryAddSingleton`: `IGateMemberSource`, `IGateViewerProvider`, `IGateMemberGroupSource` |
| `BlueBillywigApiComposer` | The OpenAPI document `bluebillywigumbraco` (`Constants.ApiName`), a security filter, and `OperationIds`, which names operations for controllers under `BlueBillywig.Umbraco.Controllers` |

## Settings (`Configuration/`)

- `BlueBillywigOptions`: optional configuration overrides. Connection: `Publication`, `BaseUrl`, `ApiId`, `ApiSecret`, `ReadOnlyApiId`, `ReadOnlyApiSecret`. Content protection: `GateSharedSecret`. Delete capability: `DeleteUserGroups`. Rendering: `DisableLazyLoader`, `ForceLazyLoader`, `LazyLoadOEmbed`, `LazyLoadOEmbedChannel`, `LazyLoadComponent`, `ShowEmbedProblems`. All are explained in [INSTALL.md](INSTALL.md).
- `BlueBillywigSettings`: the effective settings record. Besides the credentials: `Playout` (default `default`), `Embed` (default `javascript`), `PageCount` (15), `EnableCustomFields` (false), `EnableCta` (true). `HasCredentials` needs publication, API ID and secret. `ResolveBaseUrl()` returns `BaseUrl` or `https://{Publication}.bbvms.com`.
- `BlueBillywigSettingsStore`: reads config first, then Umbraco's key-value table (`IKeyValueService`, keys prefixed `BlueBillywig.`). Secrets are stored protected with ASP.NET Data Protection. If the Data Protection keys change, a stored secret reads as unset. A null secret on save keeps the stored one; the UI never reads secrets back. `ConfigOverrides()` lists the fields pinned by config. `Changed` fires after `Save`.
- `GateSettingsStore` (`Gate/`): the content protection settings, keys prefixed `BlueBillywig.gate.` (enabled, expiry, groups, message, login URL, shared secret, salt, pepper). Shared secret and salt are Data Protection protected. Writes that need insert-if-absent (the pepper) take Umbraco's key-value write lock.
- `BlueBillywigCleanup` (`IBlueBillywigCleanup`): the uninstall counterpart. It empties an explicit list of own keys (the fixed settings keys, the gate keys, and `BlueBillywig.ClipPlayout.{numeric id}`), clears caches, returns the key names it cleared, never values. `IKeyValueService` cannot delete a row, so values are set to empty, which the package reads as unset.

## SAPI client (`Sapi/`)

`IBlueBillywigClient` / `BlueBillywigClient` is the only code that talks to SAPI. It wraps `BlueBillywig.Sapi` (`Sdk.WithRpcTokenAuthentication`).

- **Never throws to callers.** Every call returns `SapiResult(Ok, StatusCode, Json, RawBody, Error)`. Unconfigured: `Failure(0, "Blue Billywig API not configured")`. A non-2xx answer: `Failure(status, "SAPI request failed (status)")`. Network failure: `Failure(0, "Could not reach Blue Billywig")`. Only cancellation by the caller is rethrown.
- **Error bodies stay in the log.** The upstream body of a non-2xx answer is logged at warning level and never returned.
- **Errors are not cached.** `GetAsync(..., cacheFor)` stores only `Ok` results in `IMemoryCache`, under a key made from path and sorted query.
- **Cache clearing.** All entries share a cancellation token; `ClearCache()` swaps it and cancels the old one. The client subscribes to the settings store's `Changed` event. The edit services call `ClearCache()` after writes.
- **Read-only token.** `GetReadOnlyToken()` returns an RPC token from the read-only pair only, or null. It is the only way a token may be produced for a browser. `GET library/clips/{id}/preview` uses it: with a token the preview URL carries it, without one the preview is the public player (plays published clips only).
- **Verbs.** `GetAsync`, `PostAsync`, `PutAsync`, `DeleteAsync`, `SendAsync` (pre-built content, headers, query: used by upload and multipart), `TestConnectionAsync` (`GET /sapi/publication`), `IsConnected`.

## Stored value

The **Blue Billywig Video** property stores JSON, parsed and validated by `Embedding/BlueBillywigVideo.cs`:

```json
{ "type": "clip", "id": 123, "publication": "x", "playout": "default",
  "showTranscript": true, "showChapters": false, "lazyLoad": true }
```

| Field | Applies to | Default | Validation |
|---|---|---|---|
| `type` | all | `clip` | `clip`, `channel` or `shorts`, otherwise the value is dropped |
| `id` | all | required | positive and below 10^12, number or digit string |
| `publication` | all | configured one | `PublicationSlug`: lowercase `a-z`, `0-9`, dots and dashes, 2 to 63 characters, no `..`, starting and ending with a letter or digit (the same check as the settings dashboard, which needs 3 when typed). A stored value in another case is lowercased on parse |
| `playout` | clip | configured default | `[A-Za-z0-9_-]{1,64}` |
| `showTranscript` | clip | true | |
| `showChapters` | clip | false | |
| `lazyLoad` | clip, channel | clip true, channel false | |
| `height` | channel, shorts | `600px` / `700px` | `\d{1,4}(.\d{1,2})?(px|vh|em|rem|%)` |
| `fullscreen` | shorts | false | |

`BlueBillywigVideo.Parse` accepts a string, `JsonNode` or `JsonElement` and returns null for anything unusable. Invalid optional fields fall back to defaults. `ToJson()` writes only the fields that apply to the type. The server side of the editor is `BlueBillywigVideoDataEditor` (alias `BlueBillywig.Video`, JSON value type). `BlueBillywigVideoPropertyValueConverter` turns the stored value into `BlueBillywigVideo` for Razor.

## Render chokepoint

`IBlueBillywigEmbedRenderer.RenderAsync(BlueBillywigVideo?)` in `Embedding/BlueBillywigEmbedRenderer.cs` produces every embed on the site. The view component, tag helper, block partials and the rich text rewriter (which also serves oEmbed references) all call it. In order:

1. **Invalid video** (null, no id, bad publication): empty HTML, no SAPI call.
2. **Nonce.** `IBlueBillywigCspNonceProvider` is asked once; the value is accepted only if it matches `[A-Za-z0-9+/=_-]`. A value that was returned but refused leaves the embed without a nonce, which is logged as a warning and shown as the embed problem marker (see below).
3. **Assets, always.** `bb-lazy.css` and `bb-lazy.js` (nonce, `defer`, version query), once per request (tracked in `HttpContext.Items`), also when consent is denied, because the placeholder's button needs the script. Skipped only when `DisableLazyLoader` is on.
4. **Call to action overlay assets.** `bb-cta-form-overlay.css` and `.js`, once per request, with the nonce, while the settings toggle `EnableCta` is on. Not switched off by `DisableLazyLoader`.
5. **Consent.** Granted if `IBlueBillywigConsentProvider` says so or the `bb_consent=1` cookie is present.
6. **Gate.** For clips and channels, when content protection is enabled, the output goes through `ContentGate` (`RenderGatedAsync`): policy lookup, member check, JWT injected into the player URLs (including base64 lazy wrappers and the noscript copy), or the gate message. Any doubt ends in the gate message. A response that carries a token or a gate message is marked `Cache-Control: private, no-store` and kept out of ASP.NET output caching (`GateResponse.MarkPrivate`); if that is no longer possible the token is not emitted. Without consent the gate runs on a stand-in tag that is thrown away, so a visitor who may not watch is told so instead of being asked to consent. Shorts have no content protection policy and skip the gate.
7. **Render by type:**
   - **Clip.** Embed code from `/sapi/embedcode/{id}/{playout}/{embedType}` (cached 1 hour), through `EmbedHtml.TryPrepare`; the clip from `/sapi/mediaclip/{id}` (cached 5 minutes). The playout is the embed's own, else the one chosen in the library (`IClipPlayoutStore`, same publication only), else the site default; resolved once at the top of `RenderAsync`. Fallback to a plain script tag if SAPI is unavailable or the embed code has an unexpected shape. 16:9 responsive box, `WrapLazy` when `lazyLoad`. Then **JSON-LD** (`Seo/VideoJsonLd`) for the embed types iframe, oembed, url and amp only (`ShouldEmitFor`; the JavaScript embed brings its own), then the transcript and chapters if switched on. JSON-LD, transcript and chapters sit inside the clip markup, so the gate covers them.
   - **Channel.** Script embed `.../ch/{id}.js` plus a noscript link, in a box of the configured height.
   - **Shorts.** An iframe to `.../sh/{id}.html`.

### `EmbedHtml.TryPrepare`

SAPI embed code is treated as untrusted. It keeps only `script`, `iframe`, `div`, `span`, `a` and `noscript`; drops `on*`, `srcdoc` and existing `nonce` attributes; requires every script and iframe `src` to be https on an allowed host; rejects the whole body on anything unexpected (the renderer then uses the script fallback). Allowed hosts: the player host, the publication's own base URL, `bbvms.com`, `bluebillywig.eu`, `bluebillywig.com` and their subdomains. Our nonce is added to scripts.

### Embed problem marker

When the page's CSP nonce cannot be applied, the embed would otherwise be a blank box on a strict-CSP site. Two cases count: the nonce provider returned a value the renderer refuses (every script of the render then goes out without a nonce), and, with a nonce in play, SAPI's embed code was not in the expected shape (the script fallback is used; it carries the nonce and works, so the marker is added to it, nothing is taken away). In both the renderer logs a warning. The marker itself is `<div class="bb-embed-problem" role="alert" data-bb-reason="nonce-rejected|embed-code-unprepared">` with the visitor text `embedProblem` (HTML-encoded, no inline style because a strict policy may block it; the rule is in `bb-lazy.css`).

The reference plugin shows its marker to everyone. Here it is narrower, decided by `IBlueBillywigEmbedDiagnostics` (`Embedding/IBlueBillywigEmbedDiagnostics.cs`): **nobody** by default except (a) a request that carries a signed-in backoffice user, who sees it and whose response is then sent `Cache-Control: private, no-store` and kept out of output caching (`GateResponse.MarkPrivate`; if that is no longer possible the marker is not shown), and (b) everybody while the config switch `BlueBillywig:ShowEmbedProblems` is on (staging, debugging; the output is the same for all, so nothing is marked private). Everyone else keeps the silent fallback and the log warning. Without a nonce the fallback is not a CSP matter and no marker is shown. A site can replace the interface (`TryAdd`), for example to show it to its own staff. The backoffice user is read from the request's user, else by asking the backoffice authentication scheme; that detection on a real front-end request has not been observed in a browser.

### Lazy loading

`WrapLazy` emits `<div class="bb-lazy-embed" data-bb-embed="{base64 of the embed HTML}">` plus a `noscript` copy. `Client/public/bb-lazy.js` swaps in the real embed when the placeholder scrolls into view or is clicked, rebuilding script tags so they run and keep their attributes (including the nonce). The poster is the clip's main thumbnail (`ClipThumbnails`, `ThumbnailState`). The loader also carries the consent button handler (`data-bb-consent-grant`) and the chapter click handler.

### Consent and nonce

`IBlueBillywigConsentProvider.IsGranted(HttpContext?)`: default `AlwaysGrantedConsentProvider` grants. When denied, the placeholder names no host. Its button makes `bb-lazy.js` set the session cookie `bb_consent=1` and reload; the renderer treats that cookie as consent as well. `IBlueBillywigCspNonceProvider.GetNonce(HttpContext?)`: default returns null. The nonce goes on the asset scripts, the fallback script tag, scripts in prepared embed code, and the JSON-LD script.

### Overlay assets

`bb-cta-form-overlay.js` runs in the page, finds widgets with the class `bb-cta-form`, swaps their content for an email form, stores `{email, submittedAt, clipId}` in `localStorage` under `bbtl_{clipId}` and, if the widget's class carries `bb-cta-form-config-*` with a `submitEndpoint`, POSTs the payload as JSON with `credentials: 'omit'`. No server of this package receives it.

### Transcript and chapters

The transcript is the clip's `transcript` text, or else the first published subtitle's contents (SRT with timestamps and counters removed, double-encoded UTF-8 repaired), as encoded text in a `<details>` element, right to left for Arabic, Hebrew and other RTL languages. Chapters are links with `data-time`.

## Rich text and oEmbed

`BlueBillywigOEmbedProvider` plugs into Umbraco's rich text Embed button. `BlueBillywigUrls.TryParseOEmbedUrl` accepts `https://{pub}.bbvms.com/p/{playout}/c/{id}.html`, `/view/{playout}/{id}.html` and `/ch/{id}.html`. No HTTP request is made. The returned markup is a reference, `<span class="bb-oembed-ref">...</span>`, with text that names no player host; the editor stores it inside Umbraco's `umb-embed-holder`.

The player is made at page render time: `BlueBillywigRichTextValueConverter` wraps Umbraco's own rich text converter and returns a `DeferredRichText` when the markup holds a reference; `RichTextEmbedRewriter` replaces each holder (and each block reference `bb-rte-video-ref`) with `IBlueBillywigEmbedRenderer.RenderAsync`, per request, so consent, nonce and the gate apply. Markup baked in by older content is removed. For the Delivery API only the emptied reference is output.

The rich text toolbar button (`Client/src/rte/`) opens the library picker modal and inserts the same reference through the Embed extension's `setEmbeddedMedia` command.

## Upload flow

`Client/src/library/library-upload.element.ts` (Uppy 5.x, see [DEVELOPMENT.md](DEVELOPMENT.md)), `Controllers/UploadController.cs`, `Upload/UploadService.cs`.

1. Pressing Upload calls `POST upload/init` with filename, size, content type, title, description, tags, status. The server validates (status allowlist `published`, `draft`, `archived`; 20 GB maximum; title falls back to the file name), creates the clip (`PUT /sapi/mediaclip`), starts the TUS upload (`POST /sapi/tus`) and returns the upload id, part size and presigned part URLs. If a step fails after the clip exists, the placeholder clip is deleted again.
2. The browser PUTs each part directly to the storage URLs. `POST upload/sign-part` asks for a fresh presigned URL when needed.
3. `POST upload/complete` sends part numbers and ETags to `/sapi/tus/{id}/complete`.
4. `POST upload/abort` deletes the placeholder clip, only for a clip this user started and that was not completed (`IPendingUploads`, in memory, 24 hours; a restart forgets pending uploads).

**The server sees:** small JSON calls (the metadata, part numbers, ETags, presigned URLs) and nothing else. **The server does not see:** any file bytes. The storage host must allow this site's origin (CORS); that is a platform-side setting.

## Edit, CTA, gate, sitemap and analytics services

- **Edit** (`Editing/ClipEditService`, `ClipEditController`). Reads and writes one clip: title, description, tags, status, content protection policy, per-clip playout (kept in the key-value table by `ClipPlayoutStore`), folder, language, embargo, custom fields (only names from a SAPI-fetched allowlist; `status`, `cpp` and `foldercode` through custom fields are refused), subtitles (list, upload `.vtt`/`.srt` up to 1 MB with a content check, delete, set default), thumbnails (JPG or PNG up to 10 MB, aspect ratio tolerance 15 percent, set main; `ThumbnailState` remembers recent edits so the embed gets `?v=` and the library shows the new picture) and bulk actions (`status`, `folder`, `cpp`, at most 50 distinct ids, plus `bulk-delete`). Where SAPI is known to answer 200 and change nothing (status, CPP, default subtitle), the service checks and reports a failure. A CPP change invalidates the gate's cached lookup (`ICppLookup.Invalidate`), on every node of a load-balanced site: the clip id travels in a one-field payload through the same distributed cache refresher as the clear (`ISapiCacheSignal.InvalidateClip`). Changing `cpp` (single or bulk) needs the `Delete` policy as well as the section; it is checked in the action because the endpoints are shared with ordinary edits.
- **CTA** (`Cta/`). `CtaService` reads and saves the call to action of a clip. `CtaTranslator` converts the flat editor config to a TimelineNG timeline. `CtaTimelineLocator` finds the timeline the package authored; a timeline made by another tool is refused with 409 and left alone; saving twice never creates a second timeline. Images are created as creatives (`library/cta/images`, data URI up to 10 MB). When the settings toggle is off, every CTA endpoint answers 404. Removal is `POST .../cta/remove`, not an HTTP DELETE, on purpose (see the controller comment).
- **Gate** (`Gate/`). `ContentGate` decides per embed. Pieces: `ICppLookup`/`SapiCppLookup` (is the clip or channel protected; 5 minute cache of definite answers only), `IGateViewerProvider` (who is viewing; default `MemberGateViewerProvider`: Umbraco frontend members and their member groups; a backoffice login is not a member), `GateJwt` (HS256, claims `sub`, `iat`, `exp`, `entity_access`; expiry clamped to 300 to 7200 seconds), `GatePepper` (per-install pepper so `sub` is an HMAC of the member key, never the raw id), `GateInjector` (puts `jwt=` into player URLs on allowed hosts, depth cap 2 for nested base64, fails closed), `GateHealthTally` (error counts for the last hour, feeds the backoffice banner).
- **JSON-LD** (`Seo/VideoJsonLd`). A `VideoObject` script block with name, description, thumbnail, upload date, duration and transcript, escaped so `</script>` cannot break out; nothing is emitted if encoding fails.
- **Sitemap** (`Seo/`). `VideoSitemapController` serves `/video-sitemap.xml`, the only anonymous endpoint of the package. `UmbracoSitemapPageSource` walks published content and finds clips with `VideoReferences` (property value, blocks, rich text references); pages without a template, without an absolute URL or behind Public Access are skipped. `VideoSitemapService` loads each clip (it must be published, have no content protection policy, a title and a thumbnail), writes the XML (`VideoSitemap`), caches it 10 minutes per host name (at most 8 host names), answers 200 with an empty, valid urlset when SAPI is not configured (logged once at Information, never cached), and answers 503 with `Retry-After` when SAPI is configured but could not be asked, instead of an empty sitemap. At most 2000 clips.
- **Analytics** (`Analytics/`). `AnalyticsService` returns the dashboard (last seven days: views, player loads, play rate, top five videos, device split) and per-clip analytics (1 to 365 days, retention curve). Cached 5 minutes; a failed section is reported as failed, never as zero.
- **Interception point for site code** (`Sapi/IBlueBillywigClient`). There is no separate data service: the client is the one door to Blue Billywig and the replaceable counterpart of the reference plugin's data filters (PARITY X5). A site registers its own implementation; see [INSTALL.md](INSTALL.md).

## Authorization policies and the sweep test

`Authorization/BlueBillywigPolicies.cs`. All policies copy the authentication scheme of Umbraco's own `SectionAccessSettings` policy.

| Policy | Rule | Used by |
|---|---|---|
| `BlueBillywig.Library` | The user's groups allow the section `BlueBillywig.Section` | Library, ClipEdit, Cta, Upload controllers; per-clip analytics |
| `BlueBillywig.Delete` | `Library` plus membership of a user group listed in `BlueBillywigOptions.DeleteUserGroups` (default `admin` and `editor`) | Every HTTP DELETE, `library/clips/bulk-delete` |
| `BlueBillywig.Overview` | Access to the Content, Settings or Blue Billywig section | `GET status` |
| `SectionAccessSettings` (Umbraco) | Settings section | Settings and Gate controllers |
| `SectionAccessContent` (Umbraco) | Content section | Video controller (picker lookups), dashboard analytics |

`tests/BlueBillywig.Umbraco.Tests/SecurityInvariantTests.cs`, class `EndpointAuthorizationTests`, is the sweep (invariant I3). It reflects over every controller in the assembly and fails when a routed action has no `[Authorize]` with a known policy, has `[AllowAnonymous]`, or when a public method has no HTTP attribute. Every `[HttpDelete]` must carry the Delete policy on the action; Settings endpoints must carry the Settings policy. The dictionary `DeliberatelyPublic` in that class is the allowlist; its only entry is `VideoSitemapController.ServeSitemap`, with the reason. `AuthorizationTests.cs` and `ClipEditAuthorizationTests.cs` test the handlers and the policy of selected actions.

## Backoffice extension map (`Client/src/`)

| Alias | Type | File |
|---|---|---|
| `BlueBillywig.Bundle` | bundle (`Client/public/umbraco-package.json`) | `bundle.manifests.ts` |
| `BlueBillywig.Entrypoint` | `backofficeEntryPoint`: connects the generated client to the backoffice auth context | `entrypoints/entrypoint.ts` |
| `BlueBillywig.Dashboard.Settings` | `dashboard`, Settings section, label "Blue Billywig" | `dashboards/settings.element.ts` |
| `BlueBillywig.Dashboard.Analytics` | `dashboard`, Content section, label "Blue Billywig analytics" | `dashboards/analytics.element.ts` |
| `BlueBillywig.Section` | `section` "Blue Billywig", condition `Umb.Condition.SectionUserPermission` | `library/manifest.ts` |
| `BlueBillywig.SectionView.Videos`, `.Livestreams`, `.Playlists`, `.Upload`, `.Platform` | `sectionView` tabs | `library-videos`, `library-livestreams`, `library-playlists`, `library-upload`, `library-platform` elements; Videos uses `clip-browser`, `clip-tile`, `folder-tree` |
| `BlueBillywig.Modal.ClipDetails` | `modal` (sidebar): details, preview, copy embed code, edit, delete | `clip-details.modal.element.ts` |
| `BlueBillywig.Modal.ClipEdit` | `modal` (sidebar): metadata, thumbnails, custom fields, subtitles, CTA editor (`cta-editor.element.ts`), per-clip analytics (`clip-analytics.element.ts`), tag chips | `clip-edit.modal.element.ts` |
| `BlueBillywig.Modal.LibraryPicker` | `modal` (sidebar, full): the library as a picker | `library-picker.modal.element.ts` |
| `BlueBillywig.Video` | `propertyEditorSchema` | `embed/manifest.ts` |
| `BlueBillywig.PropertyEditorUi.Video` | `propertyEditorUi` | `embed/video-picker.element.ts`, helpers in `embed/video-value.ts` |
| `BlueBillywig.Tiptap.Toolbar.Video` | `tiptapToolbarExtension` button on `Umb.Tiptap.Embed` | `rte/manifest.ts`, `rte/video-toolbar.api.ts` |
| `BlueBillywig.Localization.en`, `.da`, `.de`, `.fi`, `.fr`, `.nb`, `.nl`, `.sv` | `localization` (English is the source and the fallback) | `localization/manifest.ts`, one `<culture>.ts` per culture; keys read as `bluebillywig_<name>` |

Shared: `shared/credentials-banner.element.ts` (the "not configured" banner and the content protection error banner) and `shared/credentials-header-app.element.ts` (the same notice in the header, for administrators). `Client/src/api/` is generated (see [BUILD.md](BUILD.md)).

The library is a section with section views and modals, not a tree with workspaces, because the videos live in an external service with no Umbraco entity behind them (reasoning in the header comment of `library/manifest.ts`). The section has to be granted per user group.

## Backoffice API (`Controllers/`)

Base class `BlueBillywigControllerBase`: `[BackOfficeRoute("bluebillywig/api/v{version}")]`, document `bluebillywigumbraco`. `FromSapi` turns a failed result into 502 with a generic message. The class carries `[RefusalShape]` (`Controllers/RefusalShapeAttribute.cs`), a result filter that turns every object result with status 400 or above into RFC 7807 problem details with the controller's sentence in `detail` and in an `error` extension (the automatic model-validation 400 included): the backoffice's API error interceptor otherwise replaces a body without `type`, `title` and `status` by a bare "Bad Request". The client reads it with `errorText` in `Client/src/library/errors.ts`. Routes (under `/umbraco/bluebillywig/api/v1/`), grouped by policy; the controllers hold the exact verbs:

| Routes | Policy |
|---|---|
| `settings`, `settings/playouts`, `settings/test-connection`, `settings/clear-cache`, `settings/cleanup`, `settings/disconnect` | Settings. The POSTs need a JSON body (`{"confirm": true}`, or the form values for test-connection), so a cross-site form post is refused with 415 and a cross-site fetch needs a preflight |
| `settings/gate`, `settings/gate/policies`, `settings/gate/member-groups`, `settings/gate/health` | Settings |
| `status` | Overview |
| `clips`, `clips/{id}`, `folders`, `channels`, `shorts`, `playouts`, `features`, `preview` (picker; at most 15 results per page; `features` says whether the publication has channels and shorts, cached an hour; `preview` is the player URL for the editor's "Load preview") | Content |
| `library/capabilities`, `library/clips`, `library/livestreams`, `library/playlists`, `library/folders`, `library/clips/{id}`, `.../preview`, `.../embed-code` | Library |
| `library/clips/{id}/edit` (GET), `library/clips/{id}` (PUT), `library/clips/bulk`, `library/tags`, `library/custom-field-groups`, `library/languages`, `library/policies`, `library/playouts`, subtitle and thumbnail routes under `library/clips/{id}/` (the subtitle `.../default` POST needs the body `{"confirm": true}`, like `.../cta/remove`) | Library |
| `library/clips/{id}/cta` (GET, PUT), `.../cta/remove` (POST, body `{"confirm": true}`), `library/cta/validate`, `library/cta/images` | Library |
| `upload/init`, `upload/sign-part`, `upload/complete`, `upload/abort` | Library |
| `library/clips/{id}` (DELETE), `library/clips/bulk-delete`, subtitle DELETE | Delete |
| `analytics/dashboard` | Content |
| `analytics/clips/{id}` | Library |
| `/video-sitemap.xml` (not under the backoffice route) | none, deliberate |

## Razor surface

- View component `BlueBillywigVideo` (`Component.InvokeAsync("BlueBillywigVideo", new { video = ... })`, or `id`, `type`, `playout`, `lazy`, `transcript`, `chapters`).
- Tag helpers `<bb-video>` (attributes `video`, `type`, `video-id`, `playout`, `lazy`, `transcript`, `chapters`) and `<bb-assets />` (`IBlueBillywigAssets`).
- Partials: `Views/Partials/BlueBillywig/Video.cshtml`, `BlueBillywig/Block.cshtml`, and zero-config block partials `blocklist/Components/blueBillywigVideo.cshtml`, `blockgrid/Components/blueBillywigVideo.cshtml`, `richtext/Components/blueBillywigVideo.cshtml` (the last leaves a reference that the rewriter turns into the embed) for an element type with the alias `blueBillywigVideo`.

## Delivery API

`BlueBillywigVideoPropertyValueConverter` also implements `IDeliveryApiPropertyValueConverter`: `type`, `id`, `publication`, `playout`, `playerUrl`, `scriptUrl`, `height`, `showTranscript`, `showChapters` (`BlueBillywigVideoApiModel`). No SAPI call, so expanded and non-expanded output are the same, and never a token. Rich text carries references only.

## Reference plugin to Umbraco mapping

Statuses are copied from [PARITY.md](PARITY.md), which holds the current counts and the evidence. Differences the owner accepted on purpose are listed under [Accepted deviations](PARITY.md#accepted-deviations) there.

| Reference plugin | Umbraco | PARITY rows |
|---|---|---|
| Plugin settings page (`blue-billywig-*` options), test connection, credentials notice, platform page, uninstall | Settings dashboard, `BlueBillywigSettingsStore`, `BlueBillywigOptions`, banner, Platform view, `BlueBillywigCleanup` | S2 to S6, S8, S10, S16 Proven; S1, S9, S11 to S15, S17 Partial |
| Video, channel, shorts blocks | Property editor (`clip`, `channel`, `shorts`) and block partials | E3 Proven; E1, E2 Partial; E23, E26 Proven (editor preview, picker); E24, E25 Partial |
| Classic editor media button, Media Library tab | Rich text toolbar button, picker modal | E4, E5 Proven |
| `[blue-billywig-embed]` shortcode | `<bb-video>`, view component | E6 Partial |
| oEmbed handlers | `BlueBillywigOEmbedProvider` and `RichTextEmbedRewriter` | E7 Partial |
| Render path, embed types, lazy loader | `BlueBillywigEmbedRenderer`, `bb-lazy.*` | E10, E20, E28 Proven; E8, E9, E11, E12, E27 Partial |
| Consent filter `bluebillywig_consent_granted` | `IBlueBillywigConsentProvider` | E13 Proven, X1 Proven |
| CSP nonce filter `bluebillywig_csp_nonce` | `IBlueBillywigCspNonceProvider` | E14 Proven, X2 Proven |
| Transcript, chapters | Renderer | E15, E16 Partial |
| Copy embed code | Details modal and playlists view | E17 Proven |
| (none) headless output | Delivery API converter | E18 Proven |
| `init_upload`, `sign_part`, `complete_upload`, `abort_upload`, Uppy | `UploadController`, `UploadService`, Upload view | U1, U2, U4 Proven, U3 Partial |
| Library list, filters, folders, playlists, livestreams, hover, details, delete | Library controller and section views | L1, L2, L4, L5, L7, L8, L9 Proven; L3, L6 Partial |
| `update_clip`, custom fields, status and CPP, tags, languages, bulk, subtitles, thumbnails, capabilities | `ClipEditService`, `ClipEditController`, edit modal | M2, M4, M9, M10 Proven; M1, M3, M5 to M8, M11 Partial |
| `get_cta`, `save_cta`, `delete_cta`, `CtaTranslator`, form overlay | `Cta/`, CTA editor element, `bb-cta-form-overlay.*` | C2, C3, C5, C7 Proven; C1, C4, C6 Partial |
| Content protection (`ContentGate`) | `Gate/` | G3 to G5 Proven; G1, G2, G6 to G9 Partial |
| JSON-LD, video sitemap entries | `Seo/` | Q1 to Q3 Proven |
| Translations (`.po` files, locales da, de, fi, fr, nb, nl, sv) | `Client/src/localization/` (backoffice), `Localization/VisitorText.cs` (visitor texts) | T1, T2 Partial |
| Analytics dashboard widget, per-clip analytics | Analytics dashboard, `clip-analytics` element | A1, A2, A4, A5 Proven; A3 Partial |
| `bluebillywig_disable_lazy_loader`, `_enqueue_lazy_loader`, `_lazy_load_*` filters | `BlueBillywigOptions` | X3, X4 Partial |
| `blue_billywig_*` data filters | `IBlueBillywigClient` (the service interface that once sat next to it was removed: nothing used it) | X5 Proven |
| `SapiClient` | `IBlueBillywigClient` | no row |
| Security invariants | `SecurityInvariantTests.cs`, `PathEscapingTests`, `RendererTests`, `SearchValidationTests` | I2 to I5 Proven; I1, I6 Partial |

## Translations

Two separate mechanisms, because they run in different places (PARITY T1, T2; the strings that need a native review are in [TRANSLATIONS.md](TRANSLATIONS.md)).

- **Backoffice** (`Client/src/localization/`). One `localization` extension per culture; each file default-exports a dictionary in the area `bluebillywig`. Elements read terms with `this.localize.term("bluebillywig_<name>", ...args)`, manifests use `#bluebillywig_<name>` for labels. Umbraco loads the user's culture and falls back to the `en` set for a key the file lacks. Pure helpers (`library/errors.ts`, `library/upload-limits.ts`) take a `Term` function so Node tests can use them. Views not yet moved still show English; the list is in TRANSLATIONS.md.
- **Visitor** (`Localization/VisitorText.cs`). The renderer, the gate message and the two public scripts need texts on the server and in the browser. The server reads `CultureInfo.CurrentUICulture` and looks the key up in the embedded `Localization/Visitor/<lang>.json`, falling back to `en`. The static scripts `bb-lazy.js` and `bb-cta-form-overlay.js` get their texts from a `data-bb-text` JSON attribute that the renderer prints on their script tags, and keep English defaults in the code. Text an editor typed (custom consent message, gate message, CTA labels) is never translated.

## Tests

`tests/BlueBillywig.Umbraco.Tests/` (xUnit, no Docker, no network). `Fakes.cs` provides `FakeSettings`, `FakeClient` (canned `SapiResult`s, records requested paths), `FixedConsent`, `FixedNonce` and `Rig`; the subfolders `Cta/`, `Gate/` and `Seo/` hold their own test support. The package makes internals visible to the test project. The client has its own Vitest suite in `Client/test/` (pure helpers, the two public scripts against a hand-written fake DOM, the translation files); see [DEVELOPMENT.md](DEVELOPMENT.md). The renderer test for the responsive iframe reads the shipped `Client/public/bb-lazy.css`, so that file must stay at its path. How to add a test: [DEVELOPMENT.md](DEVELOPMENT.md).
