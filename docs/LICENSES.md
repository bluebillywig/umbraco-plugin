# Licence scan

Rescan of 2026-11-04 (second pass; the first was the same day). Sources: `src/*/obj/project.assets.json` for NuGet, `Client/package-lock.json` and the installed `node_modules/*/package.json` for npm, the licence files on disk, and the source maps of a build from a clean clone. Re-run it before every release, and after any dependency change.

## Result

- No copyleft licence in the NuGet closure or in the npm tree. Everything is MIT, Apache-2.0, BSD, ISC, 0BSD, Unlicense or Python-2.0 (a development tool), or one of the packages described under "Not standard open source" below.
- Code that is **distributed**: our own files plus 21 npm packages bundled into the upload view chunk (all MIT except `mime-match`, ISC), plus the generated API client runtime. Their notices and texts are in [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md), copied from the installed files.
- The nupkg contains no third-party binary (`unzip -l`, clean clone).
- Items that still need a human are under "Open items".

## Our own licence

MIT, copyright (c) 2026 Blue Billywig (decided 2026-11-05; see `LICENSE` and the `PackageLicenseExpression` in the csproj). MIT is compatible with every dependency in the package's closure (all MIT, Apache-2.0, BSD or ISC). The reference plugin declares GPLv2 or later; Blue Billywig is the author and copyright holder of both, and the package reimplements behaviour in C# without copying its code, so the GPL does not bind this package. Ownership should still be confirmed by Blue Billywig's legal contact before the first public release.

## .NET dependencies

The test site's restored closure is 164 NuGet packages (`src/UmbracoSite/obj/project.assets.json`). The package project's closure is 118 (`src/BlueBillywig.Umbraco/obj/project.assets.json`). Both counts are the same as in the first scan: no NuGet dependency was added since.

| Licence | Site (164) | Package (118) | How determined |
|---|---|---|---|
| MIT | 96 | 60 | SPDX expression in the nuspec |
| Apache-2.0 | 39 | 33 | SPDX expression |
| BSD-3-Clause | 5 | 5 | SPDX expression |
| BSD-2-Clause | 1 | 1 | SPDX expression |
| No SPDX expression | 23 | 19 | Licence file or URL read, see below |

Direct references of the package: `BlueBillywig.Sapi` 1.0.0 (MIT), `Umbraco.Cms.Api.Common`, `Umbraco.Cms.Api.Management`, `Umbraco.Cms.Web.Common`, `Umbraco.Cms.Web.Website` 17.8.0 (MIT), `Swashbuckle.AspNetCore` 10.2.3 (MIT).

The 23 packages without an SPDX expression (4 of them, marked "site only", are not in the package project's closure):

| Package | Version | What the package's own files say |
|---|---|---|
| Lucene.Net family (9 packages: `Lucene.Net`, `.Analysis.Common`, `.Facet`, `.Grouping`, `.Join`, `.Queries`, `.QueryParser`, `.Replicator`, `.Sandbox`) | 4.8.0-beta00018 | `LICENSE.txt`: Apache-2.0 text, followed by notices for code derived from Unicode, Python, ICU and others (all permissive, read). `NOTICE.txt`: Apache Software Foundation. Copyright in the nuspec: The Apache Software Foundation |
| J2N | 2.1.0 | `LICENSE.txt`: Apache-2.0 |
| NPoco | 6.3.0 | `LICENSE.txt`: Apache-2.0 |
| NCrontab | 3.4.0 | `COPYING.txt` (read in full): Apache-2.0 text, plus an OpenSymphony Group BSD-style notice for portions (clauses forbid use of the OpenSymphony name for endorsement). Copyright (nuspec): Atif Aziz; portions OpenSymphony Group 2001 and Microsoft 2023 |
| DotNet.Glob | 3.1.0 | **No licence text in the package** (the nupkg holds only the nuspec and assemblies). The nuspec's licence URL was fetched on 2026-11-04: MIT, copyright Darrell Tunnell. The fetch returns a rendering by a summarising tool, so re-read the text at the URL for a release |
| K4os.Compression.LZ4 | 1.3.8 | **No licence text in the package.** Licence URL fetched on 2026-11-04 (it redirected to the raw file): MIT, copyright Milosz Krajewski. Same caveat |
| Markdown | 2.2.1 | **No licence text in the package.** Licence URL fetched on 2026-11-04: MIT, copyright red. Same caveat |
| Microsoft.NETCore.Platforms, NETStandard.Library | 1.1.0, 1.6.1 | `dotnet_library_license.txt` and `ThirdPartyNotices.txt` in the package: Microsoft .NET Library licence terms (not an open source licence). These packages contain no code that this package distributes |
| Microsoft.Data.SqlClient.SNI.runtime (site only) | 6.0.2 | `LICENSE.txt`: Microsoft software licence terms for a runtime component, with distribution terms (see below) |
| SixLabors.ImageSharp, SixLabors.ImageSharp.Web (site only) | 3.2.0, 3.3.0 | `LICENSE` (identical in both, read in full): Six Labors Split License 1.0 (see below) |
| Dazinator.Extensions.FileProviders | 2.0.0 | **No licence information at all** in the package (no licence element, no URL, no file). Project URL only |
| NPoco.Abstractions | 6.3.0 | **No licence information** in the package. Same project URL and repository as NPoco (Apache-2.0), but the package does not say so |
| NPoco.SqlServer (site only) | 6.3.0 | **No licence information** in the package. Same remark |

### Not standard open source

- **Six Labors Split License 1.0** (ImageSharp, ImageSharp.Web). Works are licensed under Apache-2.0 if you consume them in open source or source-available software, as a *transitive* dependency, as a direct dependency as a for-profit with under 1M USD annual gross revenue, or as a non-profit. For all other cases a paid Six Labors Commercial License applies. Who is affected: in the test site's assets these two packages come in through `Umbraco.Cms` (`Umbraco.Cms.Imaging.ImageSharp`), which only the host site references. **They are not in this package's closure.** A site that installs this package gets them from Umbraco's own packages and is the party that consumes them; this package does not ship them and does not add a direct dependency on them.
- **Microsoft.Data.SqlClient.SNI.runtime** (site only). Microsoft licence terms: the site may distribute the code in its applications only if it is used as part of the application, with end user terms that protect it at least as much, and the distributor indemnifies Microsoft. This is a host site matter and does not concern this package.

## Uppy

- Upstream licence: MIT. The installed `LICENSE` of the eight `@uppy/*` packages that are bundled is the same file (checksum equal): "Copyright (c) 2018 Transloadit". (The first scan quoted 2019 from the upstream repository; the files on disk say 2018 and are what the notices use.)
- This package uses Uppy for the upload view. Three packages are direct dependencies, pinned exactly: `@uppy/core` 5.2.0, `@uppy/aws-s3` 5.1.0, `@uppy/dashboard` 5.1.1. Everything else in the Uppy row of the bundle table below is their transitive dependencies.
- **Why the 5.x line.** Uppy 6.x removed the `createMultipartUpload`, `signPart` and `completeMultipartUpload` callbacks from `@uppy/aws-s3` and signs S3 requests itself through `signRequest`. This package needs the callbacks: SAPI creates the multipart upload and signs the parts. See [DEVELOPMENT.md](DEVELOPMENT.md) for the exit plan.
- The reference plugin ships a prebuilt Uppy bundle without the MIT notice next to it. That is a gap in the reference plugin, not in this repo.

## Other third-party code in the reference plugin (for context)

- PHP vendor tree: composer metadata says MIT for all ten packages. `bluebillywig/vmsrpc` has no licence file.
- `imask` and the jQuery UI "smoothness" stylesheet are bundled there. Their notices weren't checked.

## npm dependencies

`Client/package-lock.json` lists 259 packages (lockfile version 3; rescanned 2026-11-04 after adding `vitest`). Direct dependencies: runtime `@uppy/aws-s3`, `@uppy/core`, `@uppy/dashboard`; development `@hey-api/openapi-ts`, `@umbraco-cms/backoffice`, `chalk`, `cross-env`, `node-fetch`, `typescript`, `vite`, `vitest`.

| Licence | Packages (from the lockfile) |
|---|---|
| MIT | 234 (50 of them are optional platform binaries of esbuild and rollup, not installed on this Mac; their licence is read from the lockfile) |
| BSD-3-Clause | 9 (one is written "BSD 3-Clause", `@heximal/expressions`) |
| ISC | 6 |
| BSD-2-Clause | 2 |
| Apache-2.0 | 3 (one is `expect-type`, a `vitest` dependency) |
| MPL-2.0 OR Apache-2.0 | 2 (`dompurify`, two copies, dual licensed) |
| 0BSD, Python-2.0 (`argparse`), Unlicense | 1 each |

Of the 259, 27 are production dependencies (the Uppy tree; 26 MIT and 1 ISC) and 232 are development-only. For the 209 installed packages the `license` field of `node_modules/*/package.json` was read and agrees with the lockfile. None has no licence field.

### What is really in the shipped bundle

Extracted from `*.js.map` `sources[]` of the build on a clean clone (the working copy's build gave the identical list): 158 sources, of which 120 are under `node_modules` and the rest are our `Client/src` files (the generated API client included). All 120 are in one chunk, `library-upload.element-*.js`. The bundled packages:

| Package | Version | Licence |
|---|---|---|
| @uppy/core | 5.2.0 (direct) | MIT |
| @uppy/aws-s3 | 5.1.0 (direct) | MIT |
| @uppy/dashboard | 5.1.1 (direct) | MIT |
| @uppy/companion-client | 5.1.1 | MIT |
| @uppy/provider-views | 5.2.2 | MIT |
| @uppy/store-default | 5.0.0 | MIT |
| @uppy/thumbnail-generator | 5.1.0 | MIT |
| @uppy/utils | 7.2.0 | MIT |
| @transloadit/prettier-bytes | 0.3.5 | MIT |
| classnames | 2.5.1 | MIT |
| exifr | 7.1.3 | MIT |
| is-network-error | 1.3.2 | MIT |
| lodash | 4.18.1 | MIT |
| mime-match | 1.0.2 | ISC (no licence file; README notice) |
| namespace-emitter | 2.0.1 | MIT |
| nanoid | 5.1.16 (two nested copies) | MIT |
| p-retry | 6.2.1 | MIT |
| preact | 10.29.8 | MIT |
| retry | 0.13.1 | MIT |
| shallow-equal | 3.1.0 | MIT |
| wildcard | 1.1.2 | MIT (no licence file; README notice) |

Installed and in the lockfile but **not** in the bundle: `@types/retry`, `eventemitter3`, `p-queue`, `p-timeout` (the first scan listed them as Uppy dependencies; the source maps show none of their code is bundled). `@umbraco*` is external. The code of the build tools (vite, rollup, esbuild, typescript and so on) is not in the output.

`npm ci` reports 7 vulnerabilities (1 low, 5 high, 1 critical) at the time of this scan, the same count as before `vitest` was added. They are in development dependencies (`@umbraco-cms/backoffice` tooling, `@hey-api/openapi-ts`, `monaco-editor`, `dompurify`, `js-yaml`) and none of that code is bundled; review again with `npm audit` at release.

### Client test runner (build-time only)

`vitest` 5.1.0 (MIT, direct dev dependency, pinned exactly) added 19 packages to the lockfile, all `dev: true`: `@jridgewell/resolve-uri`, `@jridgewell/sourcemap-codec`, `@jridgewell/trace-mapping`, `@types/chai`, `@types/deep-eql`, `@vitest/mocker`, `@vitest/spy`, `assertion-error`, `chai`, `es-module-lexer`, `estree-walker`, `expect-type` (Apache-2.0), `magic-string`, `obug`, `std-env`, `tinybench`, `tinyexec`, `vitest` itself and `why-is-node-running`; the other 18 are MIT. None is non-permissive or unknown. The tests run with `npm test` (`build.sh` and the pull request workflow), never ship in the package, and nothing from them is bundled, so they need no entry in `THIRD-PARTY-NOTICES.md`. The package file list is unchanged by them.

## Open items

1. Our licence is decided (MIT, `LICENSE`, csproj, README). Still to do: legal confirmation of who owns the plugin code, before the repository is made public and 1.0.0-rc.1 is published.
2. Dazinator.Extensions.FileProviders 2.0.0 has no licence information in its package. NPoco.Abstractions and NPoco.SqlServer 6.3.0 have none either (their sibling NPoco is Apache-2.0). All three are transitive dependencies of Umbraco that the consumer's restore downloads; find the licence in their upstream repositories and record it, or ask Umbraco.
3. DotNet.Glob, K4os.Compression.LZ4 and Markdown carry no licence text in their packages; the MIT result above comes from fetching the licence URLs in the nuspec through a summarising tool. Re-read the three texts directly before a release if the notices must quote them (they are transitive dependencies, not distributed by this package).
4. Decide whether the install guide must warn site owners about the Six Labors Split License of Umbraco's ImageSharp packages. This package does not depend on them, so no entry is needed for the nupkg.
5. `src/UmbracoSite/appsettings.json` has a generated `Umbraco:CMS:Imaging:HMACSecretKey` and `Umbraco:CMS:Global:Id` committed in the repo, which will be made public at release time (no key is rotated and no history is deleted, by the owner's decision). They belong to the development host site and aren't used by the package, but they are development-site values only.
6. Re-scan before each release (NuGet closure, `package-lock.json`, source maps) and regenerate `THIRD-PARTY-NOTICES.md` with its procedure. A new bundled package needs a notice before it ships.
