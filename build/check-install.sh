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

# Runs an unpacked release build as a user would, on a machine without .NET (sprint 3.9, ADR-039): checks the folder,
# then the CLI (its version, its worker, a summary of the fixture dump) and the app without a display (it opens the dump
# and drills down). On Windows it hides the installed .NET first; on Linux CI runs it in a plain Ubuntu container
# that has only the libraries INSTALL.md lists. The dump is the LeakyConsole fixture's (build/fixture-dump.sh).
# Usage: check-install.sh <unpacked build folder> <fixture dump>
set -euo pipefail
[ $# -eq 2 ] || { echo "Usage: check-install.sh <unpacked build folder> <fixture dump>" >&2; exit 64; }
dir="$1"
dump="$2"
bash "$(dirname "$0")/check-package.sh" "$dir"

# No .NET to fall back on: off the path, and no DOTNET_ROOT.
PATH=$(tr ':' '\n' <<< "$PATH" | grep -vi dotnet | paste -sd: -)
unset DOTNET_ROOT DOTNET_ROOT_X64 DOTNET_HOST_PATH
! command -v dotnet > /dev/null || { echo "dotnet is still on the path: $(command -v dotnet)"; exit 1; }
export HEAPSCOPE_DATA_FOLDER="${HEAPSCOPE_DATA_FOLDER:-$(mktemp -d)}"

ext=""
case "$(uname -s)" in MINGW*|MSYS*|CYGWIN*) ext=".exe" ;; esac
"$dir/heapscope-cli$ext" --version
"$dir/heapscope-cli$ext" worker ping
grep -q '"System.GC.Server": true' "$dir/worker/HeapScope.Worker.runtimeconfig.json"
summary=$("$dir/heapscope-cli$ext" summary "$dump" --filter StaticCacheEntry | tr -d '\r')
grep -qE "^ +2,000 .* Fixtures\.Leaks\.StaticCacheEntry$" <<< "$summary" || { echo "$summary"; echo "The CLI did not count 2,000 StaticCacheEntry objects"; exit 1; }

log=$(mktemp -d)
"$dir/HeapScope$ext" --headless --smoke-open "$dump" --Logging:File:Directory "$log" || { cat "$log"/*.log; exit 1; }
grep -q "Smoke open types: .*Fixtures.Leaks.StaticCacheEntry 2000" "$log"/*.log || { cat "$log"/*.log; echo "The app did not show 2000 StaticCacheEntry objects"; exit 1; }
echo "Installed build $dir runs without .NET: the CLI, its worker and the app opened the fixture dump."
