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

# Installs the CLI's dotnet tool from a folder holding its package, as a user would from a downloaded release file, into
# a folder of its own (never globally), and runs it: its version, its worker (started through the dotnet host) and a
# summary of the fixture dump (sprint 3.9, ADR-039). Without a dump (the release's check of the file it attaches) it
# stops after the worker.
# Usage: check-tool.sh <folder with HeapScope.Cli.<version>.nupkg> [fixture dump]
set -euo pipefail
[ $# -eq 1 ] || [ $# -eq 2 ] || { echo "Usage: check-tool.sh <package folder> [fixture dump]" >&2; exit 64; }
version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$(dirname "$0")/../Directory.Build.props")
tools=$(mktemp -d)
export HEAPSCOPE_DATA_FOLDER="${HEAPSCOPE_DATA_FOLDER:-$(mktemp -d)}"
# From the downloaded file only, as INSTALL.md says: a nuget.config beside it clears every other source, so a package of
# the same ID on nuget.org (which HeapScope does not hold yet) can never be installed instead (3.9 review H1).
feed=$(mktemp -d)
cp "$1"/HeapScope.Cli."$version".nupkg "$feed/"
cat > "$feed/nuget.config" <<'CONFIG'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="heapscope" value="." />
  </packageSources>
</configuration>
CONFIG
(cd "$tools" && dotnet tool install HeapScope.Cli --version "$version" --configfile "$feed/nuget.config" --tool-path "$tools")
cli="$tools/heapscope-cli"
[ -x "$cli" ] || cli="$tools/heapscope-cli.exe"
# On failure, the end of each HeapScope log (CI sets Logging__File__Directory; otherwise the data folder's logs).
fail() { echo "$1" >&2; for f in "${Logging__File__Directory:-$HEAPSCOPE_DATA_FOLDER/logs}"/*.log; do [ -f "$f" ] && { echo "== $f" >&2; tail -40 "$f" >&2; }; done; exit 1; }
"$cli" --version || fail "heapscope-cli --version failed"
# The worker started through the dotnet host keeps its GC settings (3.9 review M2).
info=$("$cli" worker info 2>&1 | tr -d '\r') || { echo "$info" >&2; fail "heapscope-cli worker info failed: the tool's worker did not start"; }
echo "$info"
grep -qE "^Version: +$version$" <<< "$info" || fail "The tool's worker is not version $version"
grep -qE '^Server GC: +True$' <<< "$info" || fail "The tool's worker does not run Server GC"
if [ $# -eq 1 ]; then
  echo "The dotnet tool $version installs from its package and runs: its worker answered with Server GC."
  exit 0
fi
summary=$("$cli" summary "$2" --filter StaticCacheEntry | tr -d '\r') || fail "heapscope-cli summary failed"
grep -qE "^ +2,000 .* Fixtures\.Leaks\.StaticCacheEntry$" <<< "$summary" || { echo "$summary"; fail "The tool did not count 2,000 StaticCacheEntry objects"; }
echo "The dotnet tool $version installs from its package and runs: its worker and a summary of the fixture dump."
