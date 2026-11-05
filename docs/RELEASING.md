# Releasing

How `BlueBillywig.Umbraco` is released. The first release is the pre-release **`1.0.0-rc.1`**, published to nuget.org with the same flow as the Blue Billywig .NET SAPI SDK (GitHub workflows, nuget.org Trusted Publishing). The repository becomes public at release time; the licence is MIT (`LICENSE`).

**Status:** the workflows and the package metadata are in the repository; nothing has been published. The one-time setup below has to be done on GitHub and nuget.org by the owner, and the pre-release checklist at the end (honest status) is not complete. Do not run the release workflows, publish to NuGet or list on the Umbraco Marketplace without the owner's explicit decision.

## Version

The version lives in three places.

| File | Field | Today | Notes |
|---|---|---|---|
| `src/BlueBillywig.Umbraco/BlueBillywig.Umbraco.csproj` | `<Version>` | `1.0.0-rc.1` | Local builds and `./build.sh pack`. The publish workflow overrides it with `-p:Version=` from the release tag |
| `src/BlueBillywig.Umbraco/Client/public/umbraco-package.json` | `version` | `1.0.0-rc.1` | Shown in the Umbraco backoffice. The publish workflow rewrites it from the tag before it builds the client |
| `src/BlueBillywig.Umbraco/Client/package.json` | `version` | `0.0.0` | Not shipped; keep tidy |

For the release itself the **tag is the source of truth**: `v1.0.0-rc.1` gives NuGet version `1.0.0-rc.1` and backoffice version `1.0.0-rc.1`, whatever the files say. Keep the files close to the line you are releasing so local builds are recognisable. Semantic versioning; a pre-release has a suffix (`1.0.0-rc.1`, `1.0.0-rc.2`), and nuget.org hides it unless `--prerelease` or the exact version is used.

## One-time setup

The owner does these; automated agents are not allowed to write secrets or change nuget.org.

On GitHub (repository `bluebillywig/umbraco-plugin`, public at release time):

1. No repository secret is needed. The nuget.org account name that owns the Trusted Publishing policy (`ghultink`) is written in `publish.yaml` (`user:` of the `NuGet/login` step): it is a public name, not a key, and no NuGet API key is stored anywhere. If the policy is ever moved to another nuget.org account, change that line.
2. Actions must be allowed to run in the repository, and the default workflow permissions must not forbid the `contents: write` and `actions: write` that `create-release.yaml` asks for itself (organisation policy can restrict this; check once).
3. Branch protection as for the other repositories (`bb-enable-pr-flow`), if wanted, on `main` and `release/*`. The release branch workflow pushes a new branch with `GITHUB_TOKEN`; check your rules allow it.

On nuget.org (signed in as `ghultink`, or an organisation that account belongs to):

1. **Reserve the package id prefix `BlueBillywig.`** (nuget.org: account > "Manage ID prefixes" / reserved-prefix request), so the id `BlueBillywig.Umbraco` is verifiably ours. nuget.org reviews the request; before the first push is better.
2. **Trusted Publishing policy** (nuget.org: account > Trusted Publishing): repository owner `bluebillywig`, repository `umbraco-plugin`, workflow file **`publish.yaml`** (no environment). For a package id that does not exist yet, create the policy on the account so the first push can create the package. The workflow gets a short-lived API key by OIDC (`NuGet/login@v1`).
3. Check the package id `BlueBillywig.Umbraco` is free (re-check before the release).

## The sequence for `1.0.0-rc.1`

1. Make sure `main` holds what you want to release and the pull request checks (`pr-checks.yaml`) are green. Work through the checklist at the end.
2. GitHub > Actions > **Create release branch** > Run workflow with version **`1.0`**. This creates `release/v1.0` from `main` (the input must be `major.minor`).
3. GitHub > Actions > **Create release** > Run workflow **from the branch `release/v1.0`** (not from `main`; the workflow refuses other branches) with the suffix **`rc.1`** and the patch empty. It creates the GitHub release and tag **`v1.0.0-rc.1`**, marked as a pre-release, with generated notes, and then starts **Publish to NuGet** (`publish.yaml`) for that tag.
4. Watch **Publish to NuGet** in Actions.
5. Verify on nuget.org (indexing can take a few minutes): the package page shows version `1.0.0-rc.1`, MIT, the project URL and the README; then install it as in the checklist.

## A later stable release

- Next candidate: run **Create release** again from `release/v1.0` with suffix `rc.2`. The patch stays 0 while no stable `v1.0.x` exists.
- Stable `1.0.0`: run **Create release** from `release/v1.0` with the suffix **empty**. The patch is the latest stable `v1.0.N` plus one, or `0` when there is none, so this gives `v1.0.0`.
- After `1.0.0` exists, an empty suffix gives `1.0.1`, `1.0.2` and so on, and a suffix gives a pre-release of the next patch (`1.0.1-rc.1`). The optional **patch** input overrides the number (digits only).
- A new minor line: **Create release branch** with `1.1`, then release from `release/v1.1`. Fixes for an older line go to its `release/vX.Y` branch.

Only the tag decides the version, so a stable release needs no file edit, but update the three files afterwards so `main` does not drift.

## What the workflows check

- **Create release branch:** the input is `major.minor`; it branches from `main` and pushes `release/v<version>`.
- **Create release:** runs only from a `release/vX.Y` branch; the patch is digits; the suffix is dot-separated alphanumerics. A suffix adds `--prerelease`. It creates the release with `GITHUB_TOKEN`, targeting the release branch. A release created with `GITHUB_TOKEN` does not fire the `release: published` trigger of another workflow, so as a last step it runs `gh workflow run publish.yaml --ref <tag> -f tag=<tag>` (a `workflow_dispatch` started with `GITHUB_TOKEN` is allowed).
- **Publish to NuGet** (on `release: published` and on `workflow_dispatch` with the input `tag`; permissions `contents: read` and `id-token: write`):
  - it checks out the tag; the tag must be `vMAJOR.MINOR.PATCH` or `vMAJOR.MINOR.PATCH-suffix`, otherwise it stops;
  - it sets `umbraco-package.json` `version` from the tag, builds the client (`npm ci`, `npm run build`) **before** `dotnet restore`/`build`, runs the client tests, builds in Release with `-p:Version=<tag version>`, runs the .NET tests, packs with the same version;
  - it logs in with Trusted Publishing and pushes `artifacts/*.nupkg` with `--skip-duplicate`;
  - a concurrency group per tag (no cancel) serialises two runs for the same tag if both triggers ever fire; the second push is then a harmless duplicate. You can also run it by hand from Actions with a tag, for example to retry after a failed run for the same tag.
- There is **no check in the workflow that the nupkg has its backoffice files**. A pack that skipped the client build publishes without an error (see BUILD.md), so the client build step must stay before the .NET steps, and the contents are checked by hand (below) on the package built locally and again from the published one.
- A pre-release is published exactly like a stable release; nothing in `publish.yaml` treats it differently except the version string.

## Build and pack locally

```bash
./build.sh pack
```

It must finish with all tests passing and `artifacts/BlueBillywig.Umbraco.<version>.nupkg` present. Run it from a clean clone of the commit you will release (see [BUILD.md](BUILD.md)), not from your working copy.

## What the nupkg must contain

Check with `unzip -l artifacts/BlueBillywig.Umbraco.<version>.nupkg` and `unzip -p artifacts/*.nupkg BlueBillywig.Umbraco.nuspec`.

- `lib/net10.0/BlueBillywig.Umbraco.dll`
- `README.md` (the repository root README; the nuspec has `<readme>README.md</readme>`)
- `staticwebassets/App_Plugins/BlueBillywig/` with: `umbraco-package.json` (whose `version` equals the package version), `blue-billywig-umbraco.js`, the hashed chunks (settings, analytics, picker, toolbar, library views and modals, the upload chunk with Uppy inside, and so on), the chunk of each backoffice language (`en`, `da`, `de`, `fi`, `fr`, `nb`, `nl`, `sv`), `bb-lazy.js`, `bb-lazy.css`, `bb-cta-form-overlay.js` and `bb-cta-form-overlay.css`. **No `.map` files**: the Release build leaves them out on purpose (the csproj removes `wwwroot\**\*.map` when `Configuration` is `Release`), because they hold the TypeScript sources. For `1.0.0-rc.1` that is 36 files under `App_Plugins/BlueBillywig/`, 47 in the whole package (checked 2026-11-05 with `unzip -l`; the number changes with the chunks, so check for the folders and files, not the count). **If this folder is missing the package has no backoffice UI.**
- `build/`, `buildMultiTargeting/` and `buildTransitive/` props files for Static Web Assets.

In the nuspec, check:

- `<id>BlueBillywig.Umbraco</id>` and the expected `<version>`
- `<authors>Blue Billywig</authors>`, a description
- `<license type="expression">MIT</license>`
- `<projectUrl>https://github.com/bluebillywig/umbraco-plugin</projectUrl>`
- `<repository type="git" url="https://github.com/bluebillywig/umbraco-plugin" commit="...">` with the commit you are releasing
- `<readme>README.md</readme>`
- `<tags>` contains no `umbraco-marketplace` unless the owner decided on listing (below)
- dependencies: `BlueBillywig.Sapi`, `Swashbuckle.AspNetCore`, and `Umbraco.Cms.Api.Common`, `Umbraco.Cms.Api.Management`, `Umbraco.Cms.Web.Common`, `Umbraco.Cms.Web.Website` as the range `[17.8.0, 18.0.0)`

It must not contain: secrets, `appsettings.Local.json`, `secrets/`, `node_modules`, `Client/src` TypeScript, the test site.

`./build.sh pack` of 2026-11-05 for `1.0.0-rc.1` showed all of the above (47 files, 0 `.map` files, `umbraco-package.json` version `1.0.0-rc.1`). Its nuspec `<description>` comes from the csproj `<Description>`; re-read it before the release and check it still says what is in the package.

## Supported Umbraco versions

The csproj caps the four Umbraco references with `VersionOverride="[17.8.0,18.0.0)"`. The packed nuspec lists `version="[17.8.0, 18.0.0)"` for all four. A site on Umbraco 18 cannot install it, which is intended: the backoffice client and API were built only against 17. **Open decision:** the lower bound 17.8.0 excludes a site on 17.0 to 17.7; decide deliberately whether that is right and test the lowest version you promise.

`Directory.Packages.props` pins `17.8.0` centrally for the build. The Marketplace derives the supported Umbraco range from these dependency ranges (Umbraco's "Listing Your Package" page, read on 2026-11-04), so the cap is also what shows there.

## Rollback and mistakes

- **A published nuget.org version cannot be deleted and its number cannot be reused.** A mistake means a new version (`1.0.0-rc.2`), never a replacement.
- **Unlist** the bad version on nuget.org (package page > Manage > untick "List in search results"). It stays installable by exact version for sites that already use it, and disappears from search.
- **Deprecate** it on nuget.org (Manage > Deprecation), with a reason and the version that replaces it, so installs warn.
- Mark the matching GitHub release as withdrawn in its notes; do not delete the tag if anyone built from it.
- If a credential was ever put in a package, treat it as leaked and rotate it (for the initial release the owner decided no key is rotated; re-check with the secret scan below).
- If **Publish to NuGet** fails after the release exists, fix the cause on the release branch and run **Create release** again with the next suffix or patch; do not move the tag. Re-running the publish for the same tag is fine only when nothing in the code needs to change (the push skips duplicates).

## Umbraco Marketplace (owner decision)

Listing makes the package public on `marketplace.umbraco.com`. Do it only when the owner explicitly decides to. The tag `umbraco-marketplace` is **not added yet**. Per Umbraco's documentation:

- Add the NuGet tag `umbraco-marketplace` to `<PackageTags>` in the csproj, then release a new version (tags are part of the package).
- The package needs a dependency on an `Umbraco.Cms.*` package; it has several. The supported range is read from those dependency ranges.
- Tag only the installable package, which is this one.
- Optionally add `umbraco-marketplace.json` at the location of the NuGet project URL (the root of the default branch), with fields such as `Title`, `Description`, `Category`, `DocumentationUrl`, `IssueTrackerUrl`, `LicenseTypes` and `AuthorDetails`. The schema is at `https://marketplace.umbraco.com/umbraco-marketplace-schema.json`; all fields are optional. This file does not exist yet.
- `PackageProjectUrl` is set in the csproj, and Marketplace takes the project URL from it.

## Pre-release checklist (status 2026-11-05)

Done:

- [x] Licence chosen: MIT, `LICENSE`, `<PackageLicenseExpression>` in the csproj, README licence section. (Legal confirmation of who owns the ported plugin's code is still an open item in [LICENSES.md](LICENSES.md).)
- [x] Versions: csproj and `umbraco-package.json` at `1.0.0-rc.1`.
- [x] Release workflows in `.github/workflows/` (create release branch, create release, publish).
- [x] `./build.sh pack` green (all .NET and client tests pass); nupkg contents and nuspec checked against this document.
- [x] Umbraco dependency cap below 18 is in place.

Not done (do not release before these are decided or done):

- [ ] **The package installed from the nupkg into a separate, empty Umbraco 17 site: NOT DONE.** The package has only ever run in the test site in this repository, which references the project directly. Do this with the exact nupkg (local folder source, [INSTALL.md](INSTALL.md)): dashboard, picker, rendering, the section, a language other than English.
- [ ] **Parity is not proven.** [PARITY.md](PARITY.md) is the source of truth and still has rows that are Open, Partial or Blocked. The three **Blocked** rows (S2 read-only token pair, L7 preview with the read-only token, G1 protected playback) need what their notes ask for, or an owner-accepted N/A. Releasing a pre-release with known gaps is the owner's decision; say so in the release notes and keep the README status honest. The package has been tested against the development publication only.
- [ ] The release workflows have never run: the first run (create branch, create release, publish) is itself untested.
- [ ] The lower Umbraco bound (17.8.0) decided and tested.
- [ ] The nuspec `<description>` and the README "What exists today" list match what is built.
- [ ] `docs/LICENSES.md` rescanned and its open items closed or accepted by the owner (licence texts of a few transitive packages, ownership confirmation).
- [ ] `THIRD-PARTY-NOTICES.md` regenerated with the procedure inside it.
- [ ] Secret scan of the repository **and its history** immediately before the repository is made public: no tokens, passwords, API secrets or key material. `src/UmbracoSite/appsettings.Local.json` and `secrets/` must stay untracked (`git ls-files` shows neither). By the owner's decision no key is rotated and no history is deleted; the test site's committed `Umbraco:CMS:Imaging:HMACSecretKey` and `Umbraco:CMS:Global:Id` in `src/UmbracoSite/appsettings.json` belong to the development site, never to a production site.
- [ ] Product-name check: no user-visible string, rendered class name, CSS class, log text, doc or package metadata contains the product name of the reference plugin. Search the repository, the built bundle and the rendered HTML of the test site, not only the source, with the name filled in by the person who knows it; do not put the name in this file.
- [ ] `docs/INSTALL.md`, `docs/USER-GUIDE.md` and `docs/ARCHITECTURE.md` match the code of this release.
- [ ] `npm audit` reviewed (development dependencies only, not bundled, when last seen on 2026-11-04).
- [ ] One-time setup above done (Trusted Publishing policy, `BlueBillywig.` prefix, repository made public).
- [ ] The owner has approved publishing to NuGet, and separately approved a Marketplace listing, if wanted.
