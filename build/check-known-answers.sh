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

# Checks the CLI's answers on a dump of the LeakyConsole fixture (run with --leak all --count 2000) against the known
# answers (fixtures/KnownAnswers, ADR-013): one object per item for each leak, 32 bytes per cache entry, 148 bytes
# retained by each, every entry held through the static s_cache, a complete heap, a named runtime version.
# Usage: check-known-answers.sh <heapscope-cli> <dump>. Used by CI on every system (sprints 2.3.5 and 2.3.6).
set -euo pipefail
cli="$1"
dump="$2"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

"$cli" open "$dump" --top 5 | tee "$work/open.txt"
"$cli" summary "$dump" --filter Fixtures.Leaks --format json > "$work/summary.json"
"$cli" instances "$dump" --type Fixtures.Leaks.StaticCacheEntry --retained --top 5 --format json > "$work/instances.json"
"$cli" paths "$dump" --type Fixtures.Leaks.StaticCacheEntry --format json > "$work/paths.json"

grep -q "^Process: .*\.NET [1-9][0-9]*\." "$work/open.txt" || { echo "No runtime version in the open output"; exit 1; }
if grep -q "Note: Parts of the managed heap could not be read" "$work/open.txt"; then echo "The dump was read as incomplete"; exit 1; fi
for leak in StaticCacheEntry SessionRecord LeakySubscriber UndisposedResource CapturedState; do
  jq -e --arg t "Fixtures.Leaks.$leak" '[.Rows[] | select(.Name == $t)] | length == 1 and .[0].Objects == 2000' "$work/summary.json" > /dev/null \
    || { echo "Known answer failed: 2000 objects of $leak"; jq '.Rows[] | {Name, Objects}' "$work/summary.json"; exit 1; }
done
jq -e '.TotalObjects == 2000 and .TotalBytes == 64000 and (.Rows | length) == 5 and all(.Rows[]; .Retained == 148)' "$work/instances.json" > /dev/null \
  || { echo "Known answer failed: StaticCacheEntry sizes"; cat "$work/instances.json"; exit 1; }
jq -e '.CompleteGraph and .Objects == 2000 and .Unreachable == 0 and (.Paths[0].Objects == 2000) and (.Paths[0].Root.Description | contains("Fixtures.Leaks.StaticCacheLeak.s_cache"))' "$work/paths.json" > /dev/null \
  || { echo "Known answer failed: StaticCacheEntry path"; cat "$work/paths.json"; exit 1; }
echo "Known answers match."
