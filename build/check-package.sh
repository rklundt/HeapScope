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
# and the native graphics libraries' notices are there, and binaries not reviewed for redistribution are not. SkiaSharp's
# native library holds no code of Skia's raw camera image decoder, Adobe's DNG SDK and Google's piex (2026-10-30):
# build/skia-native.sh replaced the stock one.
# Usage: check-package.sh <publish folder>
set -euo pipefail
[ $# -eq 1 ] || { echo "Usage: check-package.sh <publish folder>" >&2; exit 64; }
dir="$1"
failed=0
for file in LICENSE ADDITIONAL-TERMS.md COMMERCIAL.md THIRD-PARTY-NOTICES.md; do
  [ -s "$dir/$file" ] || { echo "Missing $file"; failed=1; }
done
grep -q "They are not HeapScope's license" "$dir/licenses/README.md" 2>/dev/null || { echo "Missing licenses/README.md, which says whose licenses the folder holds"; failed=1; }
ls "$dir"/licenses/Microsoft.NETCore.App.Runtime.*/* > /dev/null 2>&1 || { echo "Missing the .NET runtime's license files under licenses/"; failed=1; }
ls "$dir"/licenses/SkiaSharp.NativeAssets.*/THIRD-PARTY-NOTICES.txt > /dev/null 2>&1 || { echo "Missing SkiaSharp's native notices under licenses/"; failed=1; }
unreviewed=$(find "$dir" -iname 'msdia140.dll' -o -iname 'KernelTraceControl.dll')
[ -z "$unreviewed" ] || { echo "Binaries not reviewed for redistribution are in the build:"; echo "$unreviewed"; failed=1; }
skia=$(find "$dir" -maxdepth 1 -name 'libSkiaSharp.*')
if [ -z "$skia" ]; then
  echo "Missing SkiaSharp's native library"; failed=1
else
  # shellcheck disable=SC2086 # one name per line, none with spaces
  bash "$(dirname "$0")/check-no-dng.sh" $skia || failed=1
fi
[ "$failed" -eq 0 ] && echo "Package $dir: license files present, no unreviewed binaries, no DNG SDK or piex code."
exit "$failed"
