# Releasing

How to cut a release of `BlueBillywig.Umbraco`. **Nothing here has been done yet**: the package is at 0.1.0, unreleased, with no publish workflow in this repository. Do not publish to NuGet or list on the Umbraco Marketplace until the pre-release checklist at the end is complete and the owner has said go.

## Version

The version lives in three places. Keep them equal.

| File | Field | Today |
|---|---|---|
| `src/BlueBillywig.Umbraco/BlueBillywig.Umbraco.csproj` | `<Version>` (the NuGet version and file name) | `0.1.0` |
| `src/BlueBillywig.Umbraco/Client/public/umbraco-package.json` | `version` (shown in the Umbraco backoffice) | `0.1.0` |
| `src/BlueBillywig.Umbraco/Client/package.json` | `version` (not shipped; keep tidy) | `0.0.0` |

Use semantic versioning. A pre-release is `0.2.0-rc.1`. Edit the three files, commit, and rebuild: the client bundle copies `umbraco-package.json` into the package.

## Build and pack

```bash
./build.sh pack
```

It must finish with all tests passing and `artifacts/BlueBillywig.Umbraco.<version>.nupkg` present. Run it from a clean clone of the commit you will tag (see [BUILD.md](BUILD.md)), not from your working copy.

## What the nupkg must contain

Check with `unzip -l artifacts/BlueBillywig.Umbraco.<version>.nupkg` and `unzip -p artifacts/*.nupkg BlueBillywig.Umbraco.nuspec`.

- `lib/net10.0/BlueBillywig.Umbraco.dll`
- `README.md` (the repository root README, and the nuspec has `<readme>README.md</readme>`)
- `staticwebassets/App_Plugins/BlueBillywig/` with: `umbraco-package.json`, `blue-billywig-umbraco.js`, the hashed chunks (settings, analytics, picker, toolbar, library views and modals, the upload chunk with Uppy inside, and so on), the chunk of each backoffice language (`en`, `da`, `de`, `fi`, `fr`, `nb`, `nl`, `sv`), `bb-lazy.js`, `bb-lazy.css`, `bb-cta-form-overlay.js` and `bb-cta-form-overlay.css`. **No `.map` files**: the Release build leaves them out on purpose (the csproj removes `wwwroot\**\*.map` when `Configuration` is `Release`), because they hold the TypeScript sources. For 0.1.0 that is 36 files under `App_Plugins/BlueBillywig/`, 47 in the whole package (checked 2026-11-05 with `unzip -l`; the number changes with the chunks, so check for the folders and files, not the count). **If this folder is missing the package has no backoffice UI** (see the build order in BUILD.md).
- `build/`, `buildMultiTargeting/` and `buildTransitive/` props files for Static Web Assets.

In the nuspec, check:

- `<id>BlueBillywig.Umbraco</id>` and the expected `<version>`
- `<authors>Blue Billywig</authors>`, a description
- `<tags>` contains no `umbraco-marketplace` unless the owner decided on listing (below)
- `<repository type="git" url="https://github.com/bluebillywig/umbraco-plugin" commit="...">` with the commit you are releasing
- a `<license>` element. **0.1.0 has none**: a licence has to be chosen first
- dependencies: `BlueBillywig.Sapi`, `Swashbuckle.AspNetCore`, `Umbraco.Cms.Api.Common`, `Umbraco.Cms.Api.Management`, `Umbraco.Cms.Web.Common`, `Umbraco.Cms.Web.Website`

It must not contain: secrets, `appsettings.Local.json`, `secrets/`, `node_modules`, `Client/src` TypeScript, the test site.

The packed 0.1.0 package was checked this way on a clean clone (commit 2983f11, 2026-11-04): it had all of the above except a licence element, and no third-party DLL. Its nuspec `<description>` came from the csproj `<Description>`, which was rewritten on 2026-11-05 to cover the section, the editors, the dashboards, the embeds and the languages; re-read it before the release and check it still says what is in the package.

## Supported Umbraco versions

The csproj caps the four Umbraco references: `<PackageReference Include="Umbraco.Cms.Web.Common" VersionOverride="[17.8.0,18.0.0)" />` for `Umbraco.Cms.Api.Common`, `Api.Management`, `Web.Common` and `Web.Website`. The packed nuspec lists `version="[17.8.0, 18.0.0)"` for all four (checked 2026-11-04 on a clean clone). A site on Umbraco 18 cannot install it, which is intended: the backoffice client and API were built only against 17. **Open decision:** the lower bound 17.8.0 excludes a site on 17.0 to 17.7; decide deliberately whether that is right and test the lowest version you promise.

`Directory.Packages.props` pins `17.8.0` centrally for the build. The Marketplace derives the supported Umbraco range from these dependency ranges (Umbraco's "Listing Your Package" page, read on 2026-11-04), so the cap is also what shows there.

## GitHub release

1. Merge the release changes to the default branch (through a pull request to `bluebillywig/umbraco-plugin`).
2. Tag `v<version>` on that commit and create a GitHub release for the tag. The Blue Billywig .NET SDK's flow is a good model: `~/git/bb-sapi-dotnet-sdk/.github/workflows/` has `create-release-branch.yaml`, `create-release.yaml` and `publish.yaml`.
3. Attach `artifacts/BlueBillywig.Umbraco.<version>.nupkg` to the release if you want a downloadable file.

## NuGet publishing with Trusted Publishing

The SDK publishes from a workflow (`publish.yaml`) that runs when a GitHub release is published. In words, what it does:

- Triggers on `release: published`, with permissions `contents: read` and `id-token: write`.
- Checks out the tag, installs .NET, restores, builds in Release, runs the tests.
- Packs with the version taken from the tag name: `dotnet pack ... -p:Version="${GITHUB_REF_NAME#v}"`.
- Logs in to NuGet with the `NuGet/login@v1` action, passing the nuget.org user name from a repository secret (`NUGET_USER`). nuget.org exchanges the workflow's OIDC token for a short-lived API key, so **no long-lived API key is stored in GitHub**.
- Pushes `artifacts/*.nupkg` to `https://api.nuget.org/v3/index.json` with `--skip-duplicate`.

To do the same here you need, once, on nuget.org: a Trusted Publishing policy for the repository `bluebillywig/umbraco-plugin` and the workflow file name, and the `NUGET_USER` secret in the repository. The workflow itself does not exist in this repository and was deliberately not written yet. When it is written, it needs a Node step and the client build before `dotnet restore`, which the SDK (pure .NET) does not have; mirror `.github/workflows/pr-checks.yaml` for that part. Check the pushed nupkg against the list above, because a pack that skipped the client build will publish without errors.

Check the package contents before the release is published; a published version number is not reusable on nuget.org.

## Umbraco Marketplace (owner decision)

Listing makes the package public on `marketplace.umbraco.com`. Do it only when the owner explicitly decides to. Per Umbraco's documentation:

- Add the NuGet tag `umbraco-marketplace` to `<PackageTags>` in the csproj. It is deliberately not there now.
- The package needs a dependency on an `Umbraco.Cms.*` package; it has several. The supported range is read from those dependency ranges.
- Tag only the installable package, which is this one.
- Optionally add `umbraco-marketplace.json` at the location of the NuGet project URL (for a GitHub repository, the root of the default branch), with fields such as `Title`, `Description`, `Category`, `DocumentationUrl`, `IssueTrackerUrl`, `LicenseTypes` and `AuthorDetails`. The schema is at `https://marketplace.umbraco.com/umbraco-marketplace-schema.json`; all fields are optional. This file does not exist yet.
- `PackageProjectUrl` is not set in the csproj today. Marketplace takes the project URL from NuGet, so set it when you list.

## Pre-release checklist

- [ ] Licence chosen, confirmed with legal (who owns the code this was ported from), `LICENSE` file added, and `<PackageLicenseExpression>` or `<PackageLicenseFile>` set in the csproj. The README licence section updated.
- [ ] `docs/LICENSES.md` rescanned (NuGet closure, `package-lock.json`, source maps) and its open items closed or accepted by the owner. Open today: the licence choice; no licence information in the packages `Dazinator.Extensions.FileProviders`, `NPoco.Abstractions` and `NPoco.SqlServer`; the MIT texts of `DotNet.Glob`, `K4os.Compression.LZ4` and `Markdown` were read through a fetch tool and not from the packages (transitive, not distributed by this package); whether the install guide must warn about the Six Labors Split License of Umbraco's ImageSharp packages (not in this package's closure); the committed development-site keys (below).
- [ ] `THIRD-PARTY-NOTICES.md` regenerated with the procedure inside it (a new bundled npm package needs its notice before it ships; the Uppy versions are pinned, see DEVELOPMENT.md).
- [ ] `docs/PARITY.md` has no Open, Partial or Blocked rows, and the independent audit is done.
- [ ] The development site's committed `Umbraco:CMS:Imaging:HMACSecretKey` and `Umbraco:CMS:Global:Id` in `src/UmbracoSite/appsettings.json`: regenerate or move them to configuration before the repository is public (they belong to the test site, not to the package).
- [ ] Secret scan of the repository and its history: no tokens, passwords, API secrets or key material anywhere. Make sure the test site never goes to production with the committed keys above. `src/UmbracoSite/appsettings.Local.json` and `secrets/` must stay untracked (`git ls-files` shows neither).
- [ ] `docs/PARITY.md`: the three **Blocked** rows (S2 read-only token pair, L7 preview with the read-only token, G1 protected playback) need what their notes ask for (a read-only key pair on a dev publication; a provisioned shared secret for a test alias or the gatekeeper's fixtures, and a dev player that loads), or an owner-accepted N/A.
- [ ] **Git history.** An early commit message (hash `8c4f142`) names the original product. Removing it needs a history rewrite, which changes every later hash and needs the owner's decision (and a plan for existing clones and the PR flow); do not rewrite on your own. If the owner decides against a rewrite, accept it in writing here.
- [ ] **Product-name check.** No user-visible string, rendered class name, CSS class, log text, doc or package metadata contains the product name of the reference plugin. Search the repository, the built bundle and the rendered HTML of the test site, not only the source (for example `git grep -i -n -E 'the-name'` plus `grep -r` over `wwwroot/App_Plugins/BlueBillywig/` and a saved page), with the name filled in by the person who knows it; do not put the name in this file.
- [ ] Versions equal in the three files above.
- [ ] Umbraco dependency range: the cap below 18 is in place; the lower bound is decided and tested.
- [ ] The nuspec `<description>` and the README "What exists today" list match what is built.
- [ ] `./build.sh pack` run from a clean clone of the release commit: green, with the nupkg contents checked against this document.
- [ ] The package installed into a separate, empty Umbraco 17 site from the nupkg: dashboard, picker and rendering work. (Not done for 0.1.0.)
- [ ] `docs/INSTALL.md`, `docs/USER-GUIDE.md` and `docs/ARCHITECTURE.md` match the code of this release.
- [ ] `npm audit` reviewed. The 7 vulnerabilities seen on 2026-11-04 are in development dependencies only and are not bundled.
- [ ] The owner has approved publishing to NuGet, and separately approved a Marketplace listing, if wanted.
