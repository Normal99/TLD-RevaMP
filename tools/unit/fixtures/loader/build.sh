#!/bin/sh
# Build the loader fixtures: the stub TLDLoader API and the fixture mod that binds
# to it (the same way a third-party TLD Loader mod does).
#
#   sh tools/unit/fixtures/loader/build.sh
set -e
ROOT="$(cd "$(dirname "$0")/../../../.." && pwd)"
cd "$ROOT"
dotnet build src/TLDLoaderShim/TLDLoaderShim.csproj -c Release -nologo -v q
dotnet build tools/unit/fixtures/loader/RecorderMod.csproj -c Release -nologo -v q
echo "loader fixtures built"
