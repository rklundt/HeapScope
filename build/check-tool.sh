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
# summary of the fixture dump (sprint 3.9, ADR-039).
# Usage: check-tool.sh <folder with HeapScope.Cli.<version>.nupkg> <fixture dump>
set -euo pipefail
[ $# -eq 2 ] || { echo "Usage: check-tool.sh <package folder> <fixture dump>" >&2; exit 64; }
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
"$cli" --version
ping=$("$cli" worker ping 2>&1 | tr -d '\r')
echo "$ping"
grep -q "^Worker $version " <<< "$ping" || { echo "The tool's worker did not answer"; exit 1; }
summary=$("$cli" summary "$2" --filter StaticCacheEntry | tr -d '\r')
grep -qE "^ +2,000 .* Fixtures\.Leaks\.StaticCacheEntry$" <<< "$summary" || { echo "$summary"; echo "The tool did not count 2,000 StaticCacheEntry objects"; exit 1; }
echo "The dotnet tool $version installs from its package and runs: its worker and a summary of the fixture dump."
