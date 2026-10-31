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

# Replaces SkiaSharp's stock native library in a published build with HeapScope's build of it, which leaves out Skia's
# raw camera image decoder: Adobe's DNG SDK and Google's piex (2026-10-30). The libraries come from SkiaSharp's own
# sources and build scripts, at the same release, built by .github/workflows/skia-native.yml and attached to a release
# of this repository; each is checked here against the checksum pinned below. HEAPSCOPE_SKIA_NATIVE_DIR may name a
# folder that already holds them (an offline build); the checksums are checked either way. publish.sh runs this.
# Usage: skia-native.sh <rid> <publish folder>
# On a SkiaSharp update: rerun skia-native.yml at the new release, attach its files to a new release, and update the
# version, the release and the checksums here (DEVELOPER.md 7, "SkiaSharp native libraries").
set -euo pipefail
[ $# -eq 2 ] || { echo "Usage: skia-native.sh <rid> <publish folder>" >&2; exit 64; }
rid="$1"
out="$2"
skia_version=3.119.4
release=https://github.com/rklundt/HeapScope/releases/download/skia-native-3.119.4-1
case "$rid" in
  win-x64) files="libSkiaSharp.dll:libSkiaSharp.win-x64.dll:fbbce099ac3937de272e3714c17ce29d1100b99fc9193b78e67452bf263329a1 libSkiaSharp.pdb:libSkiaSharp.win-x64.pdb:b32f942a17a1a1b69ae6d47a46e7805b174cf47dd326f485311b6fdecd026bf4" ;;
  linux-x64) files="libSkiaSharp.so:libSkiaSharp.linux-x64.so:ae7c2afaefb4a19d1250a867f736380b6880eaa8ba6e5389d724ca0f921eef85" ;;
  osx-arm64|osx-x64) files="libSkiaSharp.dylib:libSkiaSharp.osx.dylib:03e783e2e31cba3b4ae9475954cd534992cd9b79339b344e126cb105ba5b8e20" ;;
  *) echo "Unsupported runtime identifier: $rid" >&2; exit 64 ;;
esac

# The libraries are built for one SkiaSharp release; SkiaSharp refuses a native library of another version at run time.
grep -q "\"SkiaSharp/$skia_version\"" "$out/HeapScope.deps.json" || {
  echo "The build does not use SkiaSharp $skia_version: rebuild the native libraries for its version (DEVELOPER.md 7)." >&2
  exit 1
}

sha256() { if command -v sha256sum > /dev/null; then sha256sum "$1"; else shasum -a 256 "$1"; fi | cut -d' ' -f1; }
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
for entry in $files; do
  IFS=: read -r name asset expected <<< "$entry"
  [ -f "$out/$name" ] || { echo "The build has no $name to replace." >&2; exit 1; }
  if [ -n "${HEAPSCOPE_SKIA_NATIVE_DIR:-}" ]; then
    cp "$HEAPSCOPE_SKIA_NATIVE_DIR/$asset" "$work/$asset"
  else
    curl -fsSL --retry 3 -o "$work/$asset" "$release/$asset"
  fi
  actual=$(sha256 "$work/$asset")
  [ "$actual" = "$expected" ] || { echo "$asset has checksum $actual, expected $expected." >&2; exit 1; }
  cp "$work/$asset" "$out/$name"
  echo "Replaced $name with $asset (SkiaSharp $skia_version without the DNG SDK and piex, sha256 $actual)."
done
