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

# Publishes HeapScope for one runtime identifier (sprint 2.3.6, ADR-025): the Avalonia app and the CLI, self-contained,
# into one folder, each with the self-contained worker in its worker/ subfolder (build/Worker.Deploy.targets).
# Usage: publish.sh <rid> <output folder>, for example: publish.sh linux-x64 out/HeapScope-linux-x64.
# Runtime identifiers: win-x64, linux-x64, osx-arm64, osx-x64. macOS builds are not signed or notarized (Backlog).
# Every executable and the assemblies they share are ReadyToRun (the app and the CLI share one folder, so both must be).
set -euo pipefail
[ $# -eq 2 ] || { echo "Usage: publish.sh <win-x64|linux-x64|osx-arm64|osx-x64> <output folder>" >&2; exit 64; }
rid="$1"
out="$2"
case "$rid" in
  win-x64|linux-x64|osx-arm64|osx-x64) ;;
  *) echo "Unsupported runtime identifier: $rid" >&2; exit 64 ;;
esac
for project in src/HeapScope.UI src/HeapScope.Cli; do
  dotnet publish "$project" -c Release -r "$rid" --self-contained -p:PublishReadyToRun=true -p:HeapScopePublish=true -o "$out"
done

# The license texts travel with every build (sprint 3.6, peer review P3): HeapScope's own and every shipped package's.
dotnet run build/collect-licenses.cs -- "$out" "$rid" src/HeapScope.UI src/HeapScope.Cli src/HeapScope.Worker
bash build/check-package.sh "$out"
echo "Published HeapScope for $rid to $out"
