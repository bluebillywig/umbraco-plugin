# Building the package

How to build, test and pack `BlueBillywig.Umbraco` from a fresh clone. Every command below was run on macOS (arm64, macOS 26) from a clean clone. Linux and Windows are described from reading the project files only and are **not verified**.

## What gets built

| Part | Source | Output |
|---|---|---|
| Backoffice client (TypeScript, Lit, Vite) | `src/BlueBillywig.Umbraco/Client/` | `src/BlueBillywig.Umbraco/wwwroot/App_Plugins/BlueBillywig/` (git-ignored) |
| Package library (C#, Razor views) | `src/BlueBillywig.Umbraco/` | `bin/Release/net10.0/BlueBillywig.Umbraco.dll` |
| Test site | `src/UmbracoSite/` | Umbraco 17 host, not packaged |
| Unit tests | `tests/BlueBillywig.Umbraco.Tests/` (xUnit) and `src/BlueBillywig.Umbraco/Client/test/` (Vitest) | test results only |
| NuGet package | `dotnet pack` of the library | `artifacts/BlueBillywig.Umbraco.<version>.nupkg` |

## Prerequisites

| Tool | Version | Check |
|---|---|---|
| .NET SDK | 10 (verified with 10.0.105) | `dotnet --version` |
| Node.js | 24.13 or later (verified with 24.16.0) | `node -v` |
| npm | 11 or later (verified with 11.13.0) | `npm -v` |
| git | any recent | `git --version` |
| bash | for `build.sh` | `bash --version` |

Where the Node requirement comes from, read from the installed packages:

- `vite` 7.3.7 needs Node `^20.19.0 || >=22.12.0`.
- `@hey-api/openapi-ts` 0.99.0 (used only by `npm run generate-client`) needs Node `>=22.18.0`.
- `@umbraco-cms/backoffice` 17.x declares `node >=24.13` and `npm >=11`. That is the strictest, so use it. Older Node versions were **not tried**.

The target framework is `net10.0` (`src/BlueBillywig.Umbraco/BlueBillywig.Umbraco.csproj`). There is no `global.json`, so any installed SDK 10 is used.

Network access is needed on the first build: NuGet restore from nuget.org (including `BlueBillywig.Sapi` 1.0.0) and `npm ci` from the npm registry. The repository is private, so authenticate first (`gh auth login`).

### macOS

Verified. Install with Homebrew (`brew install dotnet node`) or the official installers.

### Linux

Not verified. Needs bash, the .NET 10 SDK and Node. One difference was read from `src/UmbracoSite/UmbracoSite.csproj`: on Linux and Windows the site references the app-local ICU package `Microsoft.ICU.ICU4C.Runtime` (version set in `Directory.Packages.props`), on macOS it does not.

### Windows

Not verified. `build.sh` is a bash script, so run it from Git Bash or WSL, or run the steps by hand from PowerShell (the commands are the same).

## One command

From the repository root:

```bash
./build.sh
```

Builds the client and the .NET solution and runs the tests. To also produce the package in `artifacts/`:

```bash
./build.sh pack
```

It runs these steps in this order and stops at the first failure (`set -euo pipefail`):

1. `npm ci && npm run build` in `src/BlueBillywig.Umbraco/Client`
2. `dotnet restore BlueBillywig.Umbraco.slnx`
3. `dotnet build BlueBillywig.Umbraco.slnx --no-restore --configuration Release`
4. `dotnet test BlueBillywig.Umbraco.slnx --no-build --configuration Release`
5. With `pack`: `rm -rf artifacts`, then `dotnet pack src/BlueBillywig.Umbraco --no-build --configuration Release --output artifacts`

`CONFIGURATION=Debug ./build.sh` builds Debug instead. Release is the default and is what a package must be built with.

A full run on a clean clone with warm NuGet and npm caches took about two minutes on an idle machine (first pass). On a machine with a load average around 100 a clean clone with `./build.sh pack` took about three and a half minutes (checked 2026-11-04, started 20:49, package written 20:53); start only one build at a time. Expect the end of the output to look like this (about a thousand tests; the number grows as tests are added, 1024 at commit 2983f11):

```text
Build succeeded.
    2 Warning(s)
    0 Error(s)
Passed!  - Failed:     0, Passed:  1024, Skipped:     0, Total:  1024
  Successfully created package '.../artifacts/BlueBillywig.Umbraco.0.1.0.nupkg'.
```

The test count changes as tests are added.

## Each step by hand

### 1. Client

```bash
cd src/BlueBillywig.Umbraco/Client
npm ci
npm run build
npm test
cd ../../..
```

`npm test` runs the Vitest suite (Node, no browser; dev-only, see [DEVELOPMENT.md](DEVELOPMENT.md)); `build.sh` runs it right after the build. `npm run build` is `tsc && vite build`. `tsc` only type-checks (`noEmit`). Vite bundles `src/bundle.manifests.ts` as an ES module library and writes it, with chunks, source maps and the files from `Client/public/`, to `src/BlueBillywig.Umbraco/wwwroot/App_Plugins/BlueBillywig/` (`vite.config.ts`, `outDir`, `emptyOutDir: true`). Anything matching `@umbraco*` is external: the backoffice supplies it at runtime and it is not bundled.

Use `npm ci`, not `npm install`. It installs exactly what `package-lock.json` pins and never rewrites it.

`npm ci` prints an audit summary (7 vulnerabilities at the time of writing: 1 low, 5 high, 1 critical; checked 2026-11-04). All of them are in development-time dependencies of `@umbraco-cms/backoffice` (`monaco-editor` and `dompurify`) and `@hey-api/openapi-ts` (`js-yaml`). None is bundled into the output. Don't run `npm audit fix --force`: it would downgrade `@hey-api/openapi-ts`, and that is a library choice to discuss first.

Expected files in `wwwroot/App_Plugins/BlueBillywig/`: `blue-billywig-umbraco.js` (the entry), hashed chunks for each part (`entrypoint-*`, `sdk.gen-*`, `client.gen-*`, `settings.element-*`, `analytics.element-*`, `video-picker.element-*`, `video-toolbar.api-*`, `credentials-banner.element-*`, `clip-browser.element-*`, `clip-details.modal.element-*`, `clip-edit.modal.element-*`, `library-*.element-*`, `modal-tokens-*`, `errors-*`, and one chunk per backoffice language: `en-*`, `da-*`, `de-*`, `fi-*`, `fr-*`, `nb-*`, `nl-*`, `sv-*`), their `.map` files (the Release package leaves them out, see below), and from `Client/public/`: `umbraco-package.json`, `bb-lazy.css`, `bb-lazy.js`, `bb-cta-form-overlay.css`, `bb-cta-form-overlay.js`. The upload chunk (`library-upload.element-*`) is the large one (Uppy is bundled into it). The hash suffixes change when the content changes.

### 2. Restore

```bash
dotnet restore BlueBillywig.Umbraco.slnx
```

The solution (`BlueBillywig.Umbraco.slnx`, the XML solution format) holds the package, the test site and the tests. Versions are pinned centrally in `Directory.Packages.props`; the project files carry no versions.

### 3. Build

```bash
dotnet build BlueBillywig.Umbraco.slnx --no-restore --configuration Release
```

The build also builds the test site. It copies Umbraco's JSON schema files into `src/UmbracoSite/` (`appsettings-schema*.json`, `umbraco-package-schema.json`). Those are git-ignored or already tracked and do not change `git status` on a clean clone.

### 4. Test

```bash
dotnet test BlueBillywig.Umbraco.slnx --no-build --configuration Release
```

The .NET unit tests. (The Vitest suite already ran in step 1.) They use fakes (`tests/BlueBillywig.Umbraco.Tests/Fakes.cs`), start no site and make no network calls, so they need no credentials. To run a subset:

```bash
dotnet test tests/BlueBillywig.Umbraco.Tests --no-build --configuration Release --filter "FullyQualifiedName~RendererTests"
```

### 5. Pack

```bash
dotnet pack src/BlueBillywig.Umbraco --no-build --configuration Release --output artifacts
```

The result is `artifacts/BlueBillywig.Umbraco.<Version>.nupkg`. `artifacts/` is git-ignored. See [RELEASING.md](RELEASING.md) for what the package must contain.

## Why the client builds before dotnet

The library is a Razor SDK project (`Microsoft.NET.Sdk.Razor`) with `StaticWebAssetBasePath` set to `/`. Files under its `wwwroot/` are Static Web Assets. The SDK discovers them while MSBuild evaluates the project, which is when `dotnet build` and `dotnet pack` start. A file Vite writes after that point is not in the asset list, so it is not served by the host and not put in the package.

Building in the wrong order does **not** fail. It was tried on a clean clone: restore, build and pack all succeeded, and the resulting `BlueBillywig.Umbraco.0.1.0.nupkg` contained only the DLL, the nuspec and the README, with no `staticwebassets/` at all. Such a package installs fine and then has no backoffice UI. After running the client build and `dotnet build` again on the same clone, the next pack contained all the files, so the fix is simply to build in order. Always check the contents (see [RELEASING.md](RELEASING.md)).

## Generating the API client

`src/BlueBillywig.Umbraco/Client/src/api/` is generated from the package's own OpenAPI document and is committed. Regenerate it whenever a controller's routes, parameters or response models change.

1. Start the test site on port 21183 (see [DEVELOPMENT.md](DEVELOPMENT.md)). The document is served unauthenticated at `http://localhost:21183/umbraco/swagger/bluebillywigumbraco/swagger.json`.
2. Run:

```bash
cd src/BlueBillywig.Umbraco/Client
npm run generate-client
```

`scripts/generate-openapi.js` fetches that URL and runs `@hey-api/openapi-ts` into `src/api`, with `throwOnError: true` and `responseStyle: 'fields'`. Run on a clean clone against a running site whose API matched the committed client, it produced no change in `git status`.

The script exits with code 0 even when it cannot reach the site or gets a non-200 answer. It only prints `ERROR:`. In a script or CI step, check the output or `git diff`, not the exit code.

After regenerating, rebuild the client (step 1) and commit the changes under `src/api`. Never edit files named `*.gen.ts`.

## Clean and rebuild

```bash
git clean -xfd -n      # dry run: shows what would be removed, including node_modules and artifacts
```

Remove build output only, keeping any local site data:

```bash
rm -rf artifacts src/BlueBillywig.Umbraco/wwwroot/App_Plugins src/BlueBillywig.Umbraco/Client/node_modules
find src tests -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
./build.sh
```

`git clean -xfd` without `-n` also deletes `src/UmbracoSite/umbraco/Data/` (the SQLite database), `src/UmbracoSite/appsettings.Local.json` and `secrets/`. Don't run it in a working copy you use for development.

## Versioning and package contents

The version is `<Version>` in `src/BlueBillywig.Umbraco/BlueBillywig.Umbraco.csproj` (currently `0.1.0`). It names the nupkg. To build another version without editing the file: `dotnet pack ... -p:Version=1.2.3` (the Blue Billywig SDK's publish workflow does this from the tag). The version also appears in `Client/public/umbraco-package.json` and `Client/package.json`; see [RELEASING.md](RELEASING.md).

Contents of the 0.1.0 package, from `unzip -l` on `./build.sh pack` of 2026-11-05 (47 files; no third-party DLL, see [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)):

```text
lib/net10.0/BlueBillywig.Umbraco.dll
staticwebassets/App_Plugins/BlueBillywig/    (the Vite output listed above without the `.map` files: 36 files; the csproj removes `wwwroot\**\*.map` from Release builds because they hold the TypeScript sources)
build/, buildMultiTargeting/, buildTransitive/   (Static Web Assets props files)
README.md
BlueBillywig.Umbraco.nuspec
```

The Razor views (`Views/Partials/...`) are compiled into the DLL. Nothing from `Client/src` or `node_modules` is in the package. The nuspec lists these dependencies: `BlueBillywig.Sapi`, `Swashbuckle.AspNetCore`, `Umbraco.Cms.Api.Common`, `Umbraco.Cms.Api.Management`, `Umbraco.Cms.Web.Common`, `Umbraco.Cms.Web.Website`, the four `Umbraco.Cms.*` ones as the range `[17.8.0, 18.0.0)` (set with `VersionOverride` in the csproj; checked in the nuspec on 2026-11-04), the other two as a minimum version. There is no `<license>` element yet (see [RELEASING.md](RELEASING.md)).

## Troubleshooting

**The nupkg has no `staticwebassets/`, or the backoffice shows nothing.** The client was not built before `dotnet build`/`pack`. Run step 1, then build and pack again.

**`git clone` says `git@github.com: Permission denied (publickey)` although you gave an https URL.** A global git rule (`url.git@github.com:bluebillywig/.insteadof https://github.com/bluebillywig/`) rewrites the URL to SSH. Either load your SSH key, or clone over https with GitHub CLI credentials and the rule switched off for that command:

```bash
GIT_CONFIG_GLOBAL=/dev/null git -c credential.helper= -c credential.helper='!gh auth git-credential' clone https://github.com/bluebillywig/umbraco-plugin.git
```

**`npm run generate-client` prints `ERROR: Failed to connect to the OpenAPI spec` or `non OK (200) response`.** The site is not running on port 21183, or the OpenAPI document name is wrong. Start the site, and check the URL in a browser. The script still exits 0.

**`error NU5039: The readme file 'README.md' does not exist in the package.` during pack.** Reproduced by removing the README item from the library project. `README.md` at the repository root is packed by `<None Include="..\..\README.md" Pack="true" PackagePath="\" />` in `src/BlueBillywig.Umbraco/BlueBillywig.Umbraco.csproj`. Don't move or delete the root README or that item.

**Restore fails offline.** Restore needs nuget.org. With warm caches (`~/.nuget/packages`, `~/.npm`) a repeat build works offline; a first build does not.

**Build errors about the Node version, or `tsc`/`vite` crashes immediately.** Check `node -v` against the table above. Not verified with older Node.
