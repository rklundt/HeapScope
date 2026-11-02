#!/usr/bin/env bash
# HeapScope
# Copyright (C) 2026 Ray Klundt
# https://github.com/rklundt/HeapScope
#
# This program is free software: you can redistribute it and/or modify
# it under the terms of the GNU Affero General Public License as published
# by the Free Software Foundation, version 3 only, with additional terms
# under Section 7 of that license.
#
# Additional terms, including attribution requirements, apply.
# See the ADDITIONAL-TERMS.md file distributed with this program.
#
# This program is distributed in the hope that it will be useful,
# but WITHOUT ANY WARRANTY; without even the implied warranty of
# MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
# GNU Affero General Public License for more details.
#
# You should have received a copy of the GNU Affero General Public License
# along with this program (see the LICENSE file). If not, see
# <https://www.gnu.org/licenses/>.
#
# Commercial licenses are available. See COMMERCIAL.md.

# Packs the CLI as a dotnet tool (sprint 3.9, ADR-039): one framework-dependent package for every system, HeapScope.Cli,
# command heapscope-cli, with the worker's assembly (no executable; the front end starts it through the dotnet host),
# HeapScope's license files and the third-party licenses. Then checks the package.
# Usage: pack-tool.sh <output folder>
set -euo pipefail
[ $# -eq 1 ] || { echo "Usage: pack-tool.sh <output folder>" >&2; exit 64; }
out="$1"
mkdir -p "$out"
staging=$(mktemp -d)
publish=$(mktemp -d)
trap 'rm -rf "$staging" "$publish"' EXIT

# HeapScopePublish leaves TraceEvent's native binaries out (Directory.Build.targets), as in a published build. The pack
# publishes into a fresh folder: the tool takes every file there.
dotnet restore src/HeapScope.Cli -p:HeapScopeTool=true -p:HeapScopePublish=true
dotnet run build/collect-licenses.cs -- "$staging" any src/HeapScope.Cli src/HeapScope.Worker
dotnet pack src/HeapScope.Cli -c Release --no-restore -p:HeapScopeTool=true -p:HeapScopePublish=true \
  -p:HeapScopeToolLicenses="$staging" -p:PublishDir="$publish/" -o "$out"

version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)
package="$out/HeapScope.Cli.$version.nupkg"
[ -f "$package" ] || { echo "Missing $package"; exit 1; }
# A .nupkg is a zip; dotnet runs the listing so no unzip is needed.
listing=$(dotnet run build/zip.cs -- list "$package")
failed=0
tools="tools/net10.0/any"
for file in heapscope-cli.dll DotnetToolSettings.xml worker/HeapScope.Worker.dll worker/HeapScope.Worker.runtimeconfig.json \
  LICENSE ADDITIONAL-TERMS.md COMMERCIAL.md THIRD-PARTY-NOTICES.md licenses/README.md; do
  grep -qx "$tools/$file" <<< "$listing" || { echo "Missing $tools/$file"; failed=1; }
done
grep -qx "LICENSE" <<< "$listing" || { echo "Missing the package's LICENSE"; failed=1; }
# No worker executable and no runtime at any depth (3.9 review M5), no unreviewed binaries, at least one third-party
# license folder.
unwanted=$(grep -iE "/HeapScope\.Worker(\.exe)?$|(^|/)(lib)?(hostfxr|coreclr)\.(dll|so|dylib)$|msdia140\.dll|KernelTraceControl\.dll" <<< "$listing" || true)
grep -qE "^$tools/licenses/[^/]+/.+" <<< "$listing" || { echo "No third-party license files under $tools/licenses/"; failed=1; }
[ -z "$unwanted" ] || { echo "Files a tool must not carry:"; echo "$unwanted"; failed=1; }
[ "$failed" -eq 0 ] && echo "Packed $package: the worker's assembly, the license files, no executable worker or unreviewed binaries."
exit "$failed"
