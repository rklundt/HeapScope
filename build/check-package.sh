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

# Checks a published HeapScope build (sprint 3.6, peer review P3): the license texts travel with it, the .NET runtime's
# and the native graphics libraries' notices are there, and binaries not reviewed for redistribution are not.
# Usage: check-package.sh <publish folder>
set -euo pipefail
[ $# -eq 1 ] || { echo "Usage: check-package.sh <publish folder>" >&2; exit 64; }
dir="$1"
failed=0
for file in LICENSE ADDITIONAL-TERMS.md COMMERCIAL.md THIRD-PARTY-NOTICES.md; do
  [ -s "$dir/$file" ] || { echo "Missing $file"; failed=1; }
done
ls "$dir"/licenses/Microsoft.NETCore.App.Runtime.*/* > /dev/null 2>&1 || { echo "Missing the .NET runtime's license files under licenses/"; failed=1; }
ls "$dir"/licenses/SkiaSharp.NativeAssets.*/THIRD-PARTY-NOTICES.txt > /dev/null 2>&1 || { echo "Missing SkiaSharp's native notices under licenses/"; failed=1; }
unreviewed=$(find "$dir" -iname 'msdia140.dll' -o -iname 'KernelTraceControl.dll')
[ -z "$unreviewed" ] || { echo "Binaries not reviewed for redistribution are in the build:"; echo "$unreviewed"; failed=1; }
[ "$failed" -eq 0 ] && echo "Package $dir: license files present, no unreviewed binaries."
exit "$failed"
