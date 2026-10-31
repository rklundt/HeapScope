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

# Checks that native libraries hold no code of Skia's raw camera image decoder: Adobe's DNG SDK and Google's piex
# (2026-10-30). The stock SkiaSharp 3.119.4 libraries for Windows, Linux and macOS all hold these names; the ones HeapScope
# builds without the decoder (.github/workflows/skia-native.yml) hold none. Used by that workflow and check-package.sh.
# Usage: check-no-dng.sh <file>...
set -euo pipefail
[ $# -ge 1 ] || { echo "Usage: check-no-dng.sh <file>..." >&2; exit 64; }
failed=0
for file in "$@"; do
  [ -s "$file" ] || { echo "Missing or empty: $file"; failed=1; continue; }
  found=$(grep -a -o -E 'dng_host|dng_negative|dng_stream|dng_exception|SkRawCodec|piex' "$file" | sort | uniq -c | tr -s ' ' | tr '\n' ';' || true)
  if [ -n "$found" ]; then
    echo "$file holds DNG SDK or piex code:$found"
    failed=1
  else
    echo "$file: no DNG SDK or piex code."
  fi
done
exit "$failed"
