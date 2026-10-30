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
# zero warnings, the fast and the integration tiers (build/test.sh), then CI's fixture check: a LeakyConsole dump
# (build/fixture-dump.sh; the CLI captures it on Windows, dotnet-dump elsewhere) checked against the known answers
# (build/check-known-answers.sh, which needs jq; without jq the check is reported as skipped). Each run appends a line to
# artifacts/gate/history.csv (git-ignored) with the failed tests' names, so tests that fail now and then show up, and
# keeps its log and TRX results under artifacts/test-results/<time>. The dump is deleted. Exit code 0 when all passed.
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

  # CI's fixture check (sprint 3.6 review H4): the CLI's capture and the known answers.
  echo "== Fixture dump and known answers" | tee -a "$log"
  cli=src/HeapScope.Cli/bin/Release/net10.0/heapscope-cli
  capture="dotnet-dump"
  case "$(uname -s)" in MINGW*|MSYS*|CYGWIN*) cli="$cli.exe"; capture="cli=$cli" ;; esac
  dump="$(pwd)/$results/leaky.dmp"
  if [ "$capture" = dotnet-dump ] && ! command -v dotnet-dump > /dev/null; then
    echo "Fixture check skipped: dotnet-dump is not installed" | tee -a "$log"
    fixture=skipped
  elif ! bash build/fixture-dump.sh "$dump" "$capture" >> "$log" 2>&1; then
    tests_ok=false
    fixture=failed
  elif ! command -v jq > /dev/null; then
    echo "Known answers skipped: jq is not installed (the capture passed)" | tee -a "$log"
    fixture=capture-only
  elif bash build/check-known-answers.sh "$cli" "$dump" >> "$log" 2>&1; then
    fixture=passed
  else
    tests_ok=false
    fixture=failed
  fi

  rm -f "$dump"
  echo "Fixture check: $fixture" | tee -a "$log"
fi

total=$(grep -h 'total:' "$log" | awk '{ s += $2 } END { print s + 0 }')
failed_count=$(grep -h 'failed:' "$log" | awk '{ s += $2 } END { print s + 0 }')
failures=$(grep -h '^failed ' "$log" | awk '{print $2}' | sort -u | tr '\n' ' ')
outcome=pass
[ "$build_ok" = true ] || outcome=build-failed
[ "$build_ok" = true ] && [ "$tests_ok" = false ] && outcome=tests-failed
[ -f artifacts/gate/history.csv ] || echo "Time,Commit,Outcome,BuildSeconds,Warnings,TestSeconds,Tests,Failed,FailedTests" > artifacts/gate/history.csv
# The failed names are quoted: a theory's display name may hold a comma.
echo "$(date '+%Y-%m-%d %H:%M:%S'),$commit,$outcome,$((built - started)),$warnings,$(( $(date +%s) - built )),${total:-0},${failed_count:-0},\"${failures% }\"" >> artifacts/gate/history.csv
echo "Gate: $outcome (warnings $warnings, tests ${total:-0}, fixture check ${fixture:-not run}); log and results in $results"
if [ -n "$failures" ]; then
  echo "Failed: $failures"
  # sed '$d' drops the line just written (head -n -1 is GNU only); the names are the last quoted field, in this script's
  # rows and in the fully quoted rows gate.ps1 wrote before sprint 3.6.
  earlier=$(sed '$d' artifacts/gate/history.csv | awk -F'"' 'NF > 1 { print $(NF - 1) }' | tr ' ' '\n' | sort -u)
  for name in $failures; do echo "$earlier" | grep -qx "$name" && echo "Failed before as well: $name"; done
fi
[ "$outcome" = pass ]
