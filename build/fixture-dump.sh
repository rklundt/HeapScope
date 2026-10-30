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

# Starts the LeakyConsole fixture, waits for READY, writes a full dump of it and stops it (sprint 3.6, peer review S1:
# one copy for every CI job and the local gate). The dump holds fixture data only; it is never uploaded.
# Usage: fixture-dump.sh <dump path> <capture: cli=<heapscope-cli path> | dotnet-dump> [configuration]
set -euo pipefail
[ $# -ge 2 ] || { echo "Usage: fixture-dump.sh <dump path> <cli=<heapscope-cli> | dotnet-dump> [Release|Debug]" >&2; exit 64; }
dump="$1"
capture="$2"
configuration="${3:-Release}"
log="$(dirname "$dump")/fixture.log"
fixture="fixtures/LeakyConsole/bin/$configuration/net10.0/LeakyConsole.dll"
dotnet "$fixture" --leak all --count 2000 --payload 32 --late-garbage 300 > "$log" 2>&1 &
fixture_pid=$!
trap 'kill $fixture_pid 2>/dev/null || true' EXIT
for _ in $(seq 1 120); do
  grep -q READY "$log" && break
  kill -0 "$fixture_pid" 2>/dev/null || { echo "The fixture exited before it was ready:"; cat "$log"; exit 1; }
  sleep 1
done
cat "$log"
pid=$(sed -n 's/.*READY pid=\([0-9]*\).*/\1/p' "$log")
[ -n "$pid" ] || { echo "The fixture did not report READY within 120 s."; exit 1; }
case "$capture" in
  cli=*) "${capture#cli=}" capture --pid "$pid" --out "$dump" ;;
  dotnet-dump) dotnet-dump collect --process-id "$pid" --type Full --output "$dump" ;;
  *) echo "Unknown capture: $capture" >&2; exit 64 ;;
esac
