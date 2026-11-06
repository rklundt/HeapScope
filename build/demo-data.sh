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

# Makes the demo data the screenshots show, from one running LeakyApi fixture: fixture data only, never real data.
# Writes demo-before.dmp, demo-after.dmp and demo-allocations.nettrace to artifacts/demo (git-ignored).
# Usage: demo-data.sh [Release|Debug]
set -euo pipefail
configuration="${1:-Release}"
cd "$(dirname "$0")/.."
demo=artifacts/demo
rm -rf "$demo"
mkdir -p "$demo"
dotnet build fixtures/LeakyApi -c "$configuration" -v quiet -nologo
dotnet build src/HeapScope.Cli -c "$configuration" -v quiet -nologo
root="$(pwd -W 2>/dev/null || pwd)"
# A folder of its own: the capture records sessions, and the user's library stays out of it.
export HEAPSCOPE_DATA_FOLDER="$root/$demo/data"
cli="src/HeapScope.Cli/bin/$configuration/net10.0/heapscope-cli"
url=http://127.0.0.1:5199
log="$demo/fixture.log"
dotnet "fixtures/LeakyApi/bin/$configuration/net10.0/LeakyApi.dll" --urls "$url" --Leaks:Detected all --Clean:Enabled true > "$log" 2>&1 &
fixture_pid=$!
trap 'kill $fixture_pid 2>/dev/null || true' EXIT
for _ in $(seq 1 60); do
  grep -q READY "$log" && break
  kill -0 "$fixture_pid" 2>/dev/null || { echo "The fixture exited before it was ready:"; cat "$log"; exit 1; }
  sleep 1
done
pid=$(sed -n 's/.*READY pid=\([0-9]*\).*/\1/p' "$log")
[ -n "$pid" ] || { echo "The fixture did not report READY within 60 s."; exit 1; }
work() { for _ in $(seq 1 "$1"); do curl -s -o /dev/null -X POST "$url/work"; done; }
work 40
"$cli" capture --pid "$pid" --out "$demo/demo-before.dmp"
"$cli" trace --pid "$pid" --profile allocations --duration 12 --out "$demo/demo-allocations.nettrace" > "$demo/trace.log" 2>&1 &
trace_pid=$!
sleep 2
work 400
wait "$trace_pid" || { echo "The trace failed:"; cat "$demo/trace.log"; exit 1; }
"$cli" capture --pid "$pid" --out "$demo/demo-after.dmp"
ls -la "$demo"
