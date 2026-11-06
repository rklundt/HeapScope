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
# A folder given is read from where the script was called, and only this script's own picture names are removed from it.
out=
if [ -n "${1:-}" ]; then
  mkdir -p "$1" || exit 1
  out="$(CDPATH= cd -- "$1" && pwd)" || exit 1
fi
configuration="${2:-Release}"
cd "$(dirname "$0")/.."
out="${out:-$(pwd)/artifacts/screenshots}"
demo=artifacts/demo
for file in demo-before.dmp demo-after.dmp demo-allocations.nettrace; do
  [ -s "$demo/$file" ] || { echo "Missing $demo/$file: run build/demo-data.sh first."; exit 1; }
done
dotnet build src/HeapScope.UI -c "$configuration" -v quiet -nologo || exit 1
root="$(pwd -W 2>/dev/null || pwd)"
export HEAPSCOPE_DATA_FOLDER="$root/$demo/data"
app="src/HeapScope.UI/bin/$configuration/net10.0/HeapScope"
mkdir -p "$out" || exit 1
rm -f "$out"/[0-9][0-9]-*-light.png "$out"/[0-9][0-9]-*-dark.png
# The Live page watches the fixture, under load, for real.
dotnet build fixtures/LeakyApi -c "$configuration" -v quiet -nologo || exit 1
url=http://127.0.0.1:5198
log="$demo/live-fixture.log"
dotnet "fixtures/LeakyApi/bin/$configuration/net10.0/LeakyApi.dll" --urls "$url" --Leaks:Detected all --Clean:Enabled true > "$log" 2>&1 &
fixture_pid=$!
( while kill -0 "$fixture_pid" 2>/dev/null; do curl -s -o /dev/null -X POST "$url/work"; sleep 0.05; done ) &
traffic_pid=$!
trap 'kill "$traffic_pid" "$fixture_pid" 2>/dev/null || true' EXIT
for _ in $(seq 1 60); do grep -q READY "$log" && break; sleep 1; done
pid=$(sed -n 's/.*READY pid=\([0-9]*\).*/\1/p' "$log")
[ -n "$pid" ] || { echo "The fixture did not report READY within 60 s."; exit 1; }
failed=0
for theme in Light Dark; do
  timeout 400 "$app" --headless --Ui:Theme "$theme" --screenshots "$out" \
    --smoke-open "$root/$demo/demo-before.dmp" --smoke-compare "$root/$demo/demo-after.dmp" \
    --smoke-allocations "$root/$demo/demo-allocations.nettrace" --screenshot-live-pid "$pid" --Logging:File:Directory "$root/$demo/logs"
  code=$?
  echo "$theme theme: exit code $code"
  [ "$code" -eq 0 ] || failed=1
done
echo "$(ls "$out"/[0-9][0-9]-*-light.png "$out"/[0-9][0-9]-*-dark.png 2>/dev/null | wc -l) pictures in $out"
exit "$failed"
