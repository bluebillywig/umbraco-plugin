#!/usr/bin/env bash
# Full build: backoffice client, .NET solution, tests, and optionally the NuGet package.
#   ./build.sh            build and test
#   ./build.sh pack       also produce artifacts/*.nupkg
# The client build must run before dotnet build, or the package ships without its backoffice files.
set -euo pipefail
cd "$(dirname "$0")"

CONFIG="${CONFIGURATION:-Release}"

echo "==> Client: npm ci, typecheck, bundle, tests"
( cd src/BlueBillywig.Umbraco/Client && npm ci && npm run build && npm test )

echo "==> .NET: restore, build ($CONFIG)"
dotnet restore BlueBillywig.Umbraco.slnx
dotnet build BlueBillywig.Umbraco.slnx --no-restore --configuration "$CONFIG"

echo "==> .NET: tests"
dotnet test BlueBillywig.Umbraco.slnx --no-build --configuration "$CONFIG"

if [ "${1:-}" = "pack" ]; then
  echo "==> .NET: pack"
  rm -rf artifacts
  dotnet pack src/BlueBillywig.Umbraco --no-build --configuration "$CONFIG" --output artifacts
  ls -l artifacts
fi
