# Third-party notices

This file lists third-party code that is distributed with `BlueBillywig.Umbraco` and carries the copyright notices and licence texts that code requires. It rests on the scan in [docs/LICENSES.md](docs/LICENSES.md) (scan of 2026-11-04). It is not legal advice. Our own licence is not chosen yet.

Every licence text below was copied by a script from the licence file of the installed package under `src/BlueBillywig.Umbraco/Client/node_modules/` (the file is named for each package). Nothing was retyped. Where identical files exist for several packages (compared by checksum), the text is shown once.

## What is distributed

1. **The NuGet package** `BlueBillywig.Umbraco.<version>.nupkg` contains our own assembly (`lib/net10.0/BlueBillywig.Umbraco.dll`), our own client bundle and static files, the readme and Static Web Assets props files. Checked with `unzip -l` on the package built from a clean clone on 2026-11-04: **no third-party DLL or binary**. Its NuGet dependencies are references that the consumer's restore downloads under their own licences (section "NuGet dependencies").
2. **The backoffice client bundle** in `staticwebassets/App_Plugins/BlueBillywig/`. It contains our `Client/src` code, the generated API client, and **the code of the npm packages listed below**, bundled into the chunk `library-upload.element-*.js` (the upload view) and its source map. Found by extracting `.sources[]` from every `*.js.map` of the build and keeping the paths that contain `node_modules`; `@umbraco*` imports are external (the Umbraco backoffice supplies them at runtime) and are not distributed here. The Uppy stylesheets (`@uppy/core` and `@uppy/dashboard` `style.min.css`) are imported as text into the same chunk and are covered by the Uppy notice.
3. Our static files `bb-lazy.js`, `bb-lazy.css`, `bb-cta-form-overlay.js`, `bb-cta-form-overlay.css` are our own code. They were not found to contain third-party code (not scanned line by line).

## NuGet dependencies (referenced, not copied into the package)

The data below is the `<license type="expression">`, `<copyright>` and `<authors>` of each package's `.nuspec` in the local NuGet cache. None of these six packages ships a licence or notice file, so the full text is not reproduced here; get it from the SPDX identifier (https://spdx.org/licenses/MIT.html).

| Package | Version | Licence (SPDX, from nuspec) | Copyright / authors (from nuspec) |
|---|---|---|---|
| BlueBillywig.Sapi | 1.0.0 | MIT | Authors: Blue Billywig |
| Umbraco.Cms.Api.Common | 17.8.0 | MIT | Copyright © Umbraco 2026 |
| Umbraco.Cms.Api.Management | 17.8.0 | MIT | Copyright © Umbraco 2026 |
| Umbraco.Cms.Web.Common | 17.8.0 | MIT | Copyright © Umbraco 2026 |
| Umbraco.Cms.Web.Website | 17.8.0 | MIT | Copyright © Umbraco 2026 |
| Swashbuckle.AspNetCore | 10.2.3 | MIT | Authors: domaindrivendev (no copyright element) |

The package project's transitive closure is 118 NuGet packages (from `obj/project.assets.json`); it is summarised in [docs/LICENSES.md](docs/LICENSES.md). It contains no copyleft licence.

**Packages the host site distributes, not this package.** `SixLabors.ImageSharp` 3.2.0 and `SixLabors.ImageSharp.Web` 3.3.0 (Six Labors Split License 1.0) and `Microsoft.Data.SqlClient.SNI.runtime` 6.0.2 (Microsoft software licence terms, a proprietary runtime component) are **not** in this package's closure. In the test site's `obj/project.assets.json` they come in through `Umbraco.Cms` (via `Umbraco.Cms.Imaging.ImageSharp` and `Umbraco.Cms.Persistence.SqlServer`), which only the host site references. A site that installs this package gets them from Umbraco's own packages and is the distributor of them. Their licence terms are summarised in docs/LICENSES.md; the licence files are in the NuGet cache folders of those packages.

## Code included in the client bundle

| Package | Version | Licence | Licence file read |
|---|---|---|---|
| @transloadit/prettier-bytes | 0.3.5 | MIT | LICENSE.md  |
| @uppy/aws-s3 | 5.1.0 | MIT | LICENSE  |
| @uppy/companion-client | 5.1.1 | MIT | LICENSE  |
| @uppy/core | 5.2.0 | MIT | LICENSE  |
| nanoid (copy nested under @uppy/core) | 5.1.16 | MIT | LICENSE  |
| @uppy/dashboard | 5.1.1 | MIT | LICENSE  |
| nanoid (copy nested under @uppy/dashboard) | 5.1.16 | MIT | LICENSE  |
| @uppy/provider-views | 5.2.2 | MIT | LICENSE  |
| @uppy/store-default | 5.0.0 | MIT | LICENSE  |
| @uppy/thumbnail-generator | 5.1.0 | MIT | LICENSE  |
| @uppy/utils | 7.2.0 | MIT | LICENSE  |
| classnames | 2.5.1 | MIT | LICENSE  |
| exifr | 7.1.3 | MIT | LICENSE  |
| is-network-error | 1.3.2 | MIT | license  |
| lodash | 4.18.1 | MIT | LICENSE  |
| mime-match | 1.0.2 | ISC | none in the package; see below |
| namespace-emitter | 2.0.1 | MIT | LICENSE.md  |
| p-retry | 6.2.1 | MIT | license  |
| preact | 10.29.8 | MIT | LICENSE  |
| retry | 0.13.1 | MIT | License  |
| shallow-equal | 3.1.0 | MIT | LICENSE  |
| wildcard | 1.1.2 | MIT | none in the package; see below |
| @hey-api/openapi-ts (generated client runtime, see below) | 0.99.0 | MIT | LICENSE.md |

All of the above except the generated client runtime are bundled into `library-upload.element-*.js`; nanoid is bundled twice (two nested copies of the same version).

### @transloadit/prettier-bytes 0.3.5

Licence: MIT. Licence file: LICENSE.md.

```text
The MIT License (MIT)

Copyright (c) 2020 Transloadit (https://transloadit.com)

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### @uppy/aws-s3 5.1.0, @uppy/companion-client 5.1.1, @uppy/core 5.2.0, @uppy/dashboard 5.1.1, @uppy/provider-views 5.2.2, @uppy/store-default 5.0.0, @uppy/thumbnail-generator 5.1.0, @uppy/utils 7.2.0

Licence: MIT. Licence file: LICENSE (identical in all of these packages).

```text
The MIT License (MIT)

Copyright (c) 2018 Transloadit

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### nanoid 5.1.16 (two nested copies, same version)

Licence: MIT. Licence file: LICENSE (identical in all of these packages).

```text
The MIT License (MIT)

Copyright 2017 Andrey Sitnik <andrey@sitnik.es>

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

### classnames 2.5.1

Licence: MIT. Licence file: LICENSE.

```text
The MIT License (MIT)

Copyright (c) 2018 Jed Watson

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### exifr 7.1.3

Licence: MIT. Licence file: LICENSE.

```text
MIT License

Copyright (c) 2020 Mike Kovařík, Mutiny.cz

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### is-network-error 1.3.2, p-retry 6.2.1

Licence: MIT. Licence file: license (identical in all of these packages).

```text
MIT License

Copyright (c) Sindre Sorhus <sindresorhus@gmail.com> (https://sindresorhus.com)

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

### lodash 4.18.1

Licence: MIT. Licence file: LICENSE.

```text
Copyright OpenJS Foundation and other contributors <https://openjsf.org/>

Based on Underscore.js, copyright Jeremy Ashkenas,
DocumentCloud and Investigative Reporters & Editors <http://underscorejs.org/>

This software consists of voluntary contributions made by many
individuals. For exact contribution history, see the revision history
available at https://github.com/lodash/lodash

The following license applies to all parts of this software except as
documented below:

====

Permission is hereby granted, free of charge, to any person obtaining
a copy of this software and associated documentation files (the
"Software"), to deal in the Software without restriction, including
without limitation the rights to use, copy, modify, merge, publish,
distribute, sublicense, and/or sell copies of the Software, and to
permit persons to whom the Software is furnished to do so, subject to
the following conditions:

The above copyright notice and this permission notice shall be
included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

====

Copyright and related rights for sample code are waived via CC0. Sample
code is defined as all source code displayed within the prose of the
documentation.

CC0: http://creativecommons.org/publicdomain/zero/1.0/

====

Files located in the node_modules and vendor directories are externally
maintained libraries used by this software which have their own
licenses; we recommend you read them, as their terms may differ from the
terms above.
```

### namespace-emitter 2.0.1

Licence: MIT. Licence file: LICENSE.md.

```text
The MIT License (MIT)

Copyright (c) 2015 Seth Vincent

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
contributorS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
```

### preact 10.29.8

Licence: MIT. Licence file: LICENSE.

```text
The MIT License (MIT)

Copyright (c) 2015-present Jason Miller

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### retry 0.13.1

Licence: MIT. Licence file: License.

```text
Copyright (c) 2011:
Tim Koschützki (tim@debuggable.com)
Felix Geisendörfer (felix@debuggable.com)

 Permission is hereby granted, free of charge, to any person obtaining a copy
 of this software and associated documentation files (the "Software"), to deal
 in the Software without restriction, including without limitation the rights
 to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 copies of the Software, and to permit persons to whom the Software is
 furnished to do so, subject to the following conditions:

 The above copyright notice and this permission notice shall be included in
 all copies or substantial portions of the Software.

 THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
 THE SOFTWARE.
```

### shallow-equal 3.1.0

Licence: MIT. Licence file: LICENSE.

```text
The MIT License (MIT)

Copyright © 2023 Misha Moroshko and Philip Su

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the “Software”), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
of the Software, and to permit persons to whom the Software is furnished to do
so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED “AS IS”, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### mime-match 1.0.2

Licence: ISC (package.json). **The package has no licence file.** The notice below is the "License(s)" section of the package's own `README.md`, copied as is.

```text
## License(s)

### ISC

Copyright (c) 2016, Damon Oehlman <damon.oehlman@gmail.com>

Permission to use, copy, modify, and/or distribute this software for any
purpose with or without fee is hereby granted, provided that the above
copyright notice and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES WITH
REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF MERCHANTABILITY
AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR ANY SPECIAL, DIRECT,
INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES WHATSOEVER RESULTING FROM
LOSS OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE OR
OTHER TORTIOUS ACTION, ARISING OUT OF OR IN CONNECTION WITH THE USE OR
PERFORMANCE OF THIS SOFTWARE.
```

### wildcard 1.1.2

Licence: MIT (package.json). **The package has no licence file.** The notice below is the "License(s)" section of the package's own `README.md`, copied as is.

```text
## License(s)

### MIT

Copyright (c) 2014 Damon Oehlman <damon.oehlman@gmail.com>

Permission is hereby granted, free of charge, to any person obtaining
a copy of this software and associated documentation files (the
'Software'), to deal in the Software without restriction, including
without limitation the rights to use, copy, modify, merge, publish,
distribute, sublicense, and/or sell copies of the Software, and to
permit persons to whom the Software is furnished to do so, subject to
the following conditions:

The above copyright notice and this permission notice shall be
included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED 'AS IS', WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY
CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE
SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

### @hey-api/openapi-ts 0.99.0 (generated client runtime)

Licence: MIT. The files under `Client/src/api/` are generated by this tool; the generated runtime (`core/*.gen.ts`, `client/*.gen.ts`) is derived from the tool's templates and is part of the bundle. The tool itself is a build-time dependency and is not bundled. Text of `node_modules/@hey-api/openapi-ts/LICENSE.md`:

```text
MIT License

Copyright (c) Hey API

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## How to regenerate this file

1. Build the package from a clean clone (`./build.sh pack`, see [docs/BUILD.md](docs/BUILD.md)).
2. List what the client bundle contains: `for m in src/BlueBillywig.Umbraco/wwwroot/App_Plugins/BlueBillywig/*.map; do jq -r '.sources[]' "$m"; done | sort -u | grep node_modules`. Every package directory in that list needs an entry above. For each, copy the licence file from `src/BlueBillywig.Umbraco/Client/node_modules/<package>/` verbatim (a script, not by hand), and read `license` and `version` from its `package.json`.
3. List the nupkg's contents (`unzip -l`) and confirm it holds no third-party binaries or assets. If it does, add them.
4. For the direct NuGet references, read `<license>` and `<copyright>` from `~/.nuget/packages/<id>/<version>/<id>.nuspec` and update the table. Add a licence text only if the package ships one.
5. Re-run the licence scan in [docs/LICENSES.md](docs/LICENSES.md) and update this file with anything new. Don't write licence text from memory.
