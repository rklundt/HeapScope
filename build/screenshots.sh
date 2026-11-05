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

# Captures every page of the app as PNG files at twice the size, one run per theme (a run's charts keep the colors they first
# drew with). Fixture data only: run build/demo-data.sh first. By hand; not in CI, and no test compares the pictures.
# Pictures go to the folder given, named NN-page-light.png and NN-page-dark.png; docs/images holds the ones the README uses.
# Usage: screenshots.sh [output folder, default artifacts/screenshots] [Release|Debug]
set -uo pipefail
out="${1:-artifacts/screenshots}"
configuration="${2:-Release}"
cd "$(dirname "$0")/.."
demo=artifacts/demo
for file in demo-before.dmp demo-after.dmp demo-allocations.nettrace; do
  [ -s "$demo/$file" ] || { echo "Missing $demo/$file: run build/demo-data.sh first."; exit 1; }
done
dotnet build src/HeapScope.UI -c "$configuration" -v quiet -nologo || exit 1
root="$(pwd -W 2>/dev/null || pwd)"
export HEAPSCOPE_DATA_FOLDER="$root/$demo/data"
app="src/HeapScope.UI/bin/$configuration/net10.0/HeapScope"
rm -rf "$out"
failed=0
for theme in Light Dark; do
  timeout 300 "$app" --headless --Ui:Theme "$theme" --screenshots "$out" \
    --smoke-open "$root/$demo/demo-before.dmp" --smoke-compare "$root/$demo/demo-after.dmp" \
    --smoke-allocations "$root/$demo/demo-allocations.nettrace" --smoke-live 6000 --Logging:File:Directory "$root/$demo/logs"
  code=$?
  echo "$theme theme: exit code $code"
  [ "$code" -eq 0 ] || failed=1
done
echo "$(ls "$out" | wc -l) pictures in $out"
exit "$failed"
