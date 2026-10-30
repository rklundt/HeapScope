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

# Runs one tier of HeapScope's tests (sprint 3.6, peer review P4 and S1), the same way in CI and in build/gate.sh.
#   fast         every test project that runs on this system but the Integration project, without the Integration and
#                Slow traits; a project left with no tests fails (peer review S1: nothing may stop running silently)
#   integration  the Integration project (Windows: it targets net10.0-windows; it captures and loads real dumps)
# Test projects are found, not listed, so a new one is never left out; the Integration and Slow traits belong in the
# Integration project only, which this script checks. Results go to <results>/<tier> as TRX files.
# Usage: test.sh <fast|integration> [Release|Debug] [results folder]
set -uo pipefail
[ $# -ge 1 ] || { echo "Usage: test.sh <fast|integration> [Release|Debug] [results folder]" >&2; exit 64; }
tier="$1"
configuration="${2:-Release}"
results="${3:-artifacts/test-results}/$tier"
report=(--report-xunit-trx --results-directory "$results")
windows=false
case "$(uname -s)" in MINGW*|MSYS*|CYGWIN*) windows=true ;; esac
[ "${OS:-}" = "Windows_NT" ] && windows=true

failed=""
integration=tests/HeapScope.Integration.Tests/HeapScope.Integration.Tests.csproj

# An Integration or Slow test outside the Integration project would run in neither tier (review).
misplaced=$(grep -rlE 'Trait\("Category", "(Integration|Slow)"\)' tests --include='*.cs' | grep -v '^tests/HeapScope.Integration.Tests/' || true)
if [ -n "$misplaced" ]; then
  echo "Integration and Slow tests belong in tests/HeapScope.Integration.Tests; found in:"
  echo "$misplaced"
  exit 1
fi

case "$tier" in
  fast)
    for project in tests/*/*.Tests.csproj; do
      name=$(basename "$project" .csproj)
      if [ "$project" = "$integration" ]; then
        echo "== $name: the integration tier runs it"
        continue
      fi

      if grep -q -- '-windows</TargetFramework>' "$project" && [ "$windows" = false ]; then
        echo "== $name: targets Windows, skipped here"
        continue
      fi

      echo "== $name"
      dotnet test --project "$project" -c "$configuration" "${report[@]}" \
        --filter-not-trait "Category=Integration" --filter-not-trait "Category=Slow" \
        || failed="$failed $name"
    done
    ;;
  integration)
    [ "$windows" = true ] || { echo "The integration tier runs on Windows (its project targets net10.0-windows)."; exit 0; }
    dotnet test --project "$integration" -c "$configuration" "${report[@]}" \
      || failed="$failed HeapScope.Integration.Tests"
    ;;
  *)
    echo "Unknown tier: $tier" >&2
    exit 64
    ;;
esac

if [ -n "$failed" ]; then
  echo "Failed:$failed"
  exit 1
fi
