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

# The local full gate (sprint 1.9; bash since sprint 3.6, so it runs wherever CI does): Debug and Release builds with
# zero warnings, then the fast and the integration tiers (build/test.sh), the same steps as CI. Each run appends a line to
# artifacts/gate/history.csv (git-ignored) with the failed tests' names, so tests that fail now and then show up, and
# keeps its TRX results under artifacts/test-results/<time>. Exit code 0 when everything passed.
# Usage: gate.sh
set -uo pipefail
cd "$(dirname "$0")/.."
stamp=$(date +%Y%m%d-%H%M%S)
results="artifacts/test-results/$stamp"
mkdir -p artifacts/gate "$results"
log="$results/gate.log"
commit="$(git rev-parse --short HEAD)$( [ -n "$(git status --porcelain)" ] && echo '+changes')"
started=$(date +%s)

build_ok=true
warnings=0
for configuration in Debug Release; do
  echo "== Build $configuration" | tee -a "$log"
  output=$(dotnet build HeapScope.sln -c "$configuration" 2>&1) || build_ok=false
  echo "$output" >> "$log"
  count=$(echo "$output" | sed -n 's/^ *\([0-9]*\) Warning(s).*/\1/p' | tail -1)
  warnings=$((warnings + ${count:-0}))
done
[ "$warnings" -eq 0 ] || build_ok=false
built=$(date +%s)

tests_ok=true
if [ "$build_ok" = true ]; then
  for tier in fast integration; do
    echo "== Tests: $tier" | tee -a "$log"
    bash build/test.sh "$tier" Release "$results" >> "$log" 2>&1 || tests_ok=false
  done
fi

total=$(grep -h 'total:' "$log" | awk '{ s += $2 } END { print s + 0 }')
failed_count=$(grep -h 'failed:' "$log" | awk '{ s += $2 } END { print s + 0 }')
failures=$(grep -h '^failed ' "$log" | awk '{print $2}' | sort -u | tr '\n' ' ')
outcome=pass
[ "$build_ok" = true ] || outcome=build-failed
[ "$build_ok" = true ] && [ "$tests_ok" = false ] && outcome=tests-failed
[ -f artifacts/gate/history.csv ] || echo "Time,Commit,Outcome,BuildSeconds,Warnings,TestSeconds,Tests,Failed,FailedTests" > artifacts/gate/history.csv
echo "$(date '+%Y-%m-%d %H:%M:%S'),$commit,$outcome,$((built - started)),$warnings,$(( $(date +%s) - built )),${total:-0},${failed_count:-0},${failures% }" >> artifacts/gate/history.csv
echo "Gate: $outcome (warnings $warnings, tests ${total:-0}); log and results in $results"
if [ -n "$failures" ]; then
  echo "Failed: $failures"
  earlier=$(head -n -1 artifacts/gate/history.csv | cut -d, -f9 | tr -d '"' | tr ' ' '\n' | sort -u)
  for name in $failures; do echo "$earlier" | grep -qx "$name" && echo "Failed before as well: $name"; done
fi
[ "$outcome" = pass ]
