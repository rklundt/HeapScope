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
# `check` only compares the SkiaSharp version the app restores (its lock file) with the one pinned here; CI and the gate
# run it first, so a SkiaSharp update fails a check that says so by name.
# Usage: skia-native.sh <rid> <publish folder>
#        skia-native.sh check
#
# RARE, BY HAND: when an Avalonia update brings a new SkiaSharp, the libraries must be rebuilt for it before any package
# builds. Nothing does this automatically (skia-native.yml runs only by hand, since 2026-11-02). The message below, and
# DEVELOPER.md section 7, "When an Avalonia update brings a new SkiaSharp", give the steps.
set -euo pipefail
skia_version=3.119.4
release=https://github.com/rklundt/HeapScope/releases/download/skia-native-3.119.4-1

# Said wherever the versions differ, so whoever meets it (often in a Dependabot PR for Avalonia) knows what happened.
stale() {
  cat >&2 <<EOF
==========================================================================================================
SkiaSharp is now $1, but HeapScope's own SkiaSharp native library is built for $skia_version.

This is expected after an Avalonia update, and nothing is broken. HeapScope ships its own build of
SkiaSharp's native library, without Adobe's DNG SDK and Google's piex (ADR-038). That build is made by
hand, once per SkiaSharp version, and every package build stops here until it is made for $1.

What to do (DEVELOPER.md section 7, "When an Avalonia update brings a new SkiaSharp"):
  1. In .github/workflows/skia-native.yml, set SKIASHARP_COMMIT to the commit of SkiaSharp's v$1 tag
     (https://github.com/mono/SkiaSharp/releases/tag/v$1).
  2. Push that branch and run the workflow on it by hand, with macOS:
       gh workflow run skia-native.yml --ref <branch> -f macos=true
  3. Attach the run's files to a new pre-release of this repository, skia-native-$1-1.
  4. In build/skia-native.sh, set skia_version=$1, the release to skia-native-$1-1, and each
     SHA-256 from that release's SHA256SUMS. Push; the package builds pass again.
==========================================================================================================
EOF
  exit 1
}

if [ "${1:-}" = "check" ] && [ $# -eq 1 ]; then
  lock="$(dirname "$0")/../src/HeapScope.UI/packages.lock.json"
  restored=$(tr -d '\r' < "$lock" | awk '/^ *"SkiaSharp": \{/ { found = 1 } found && /"resolved"/ { gsub(/[^0-9.]/, ""); print; exit }')
  [ -n "$restored" ] || { echo "No SkiaSharp in $lock: the app no longer uses it? Then publish.sh needs no skia-native.sh." >&2; exit 1; }
  [ "$restored" = "$skia_version" ] || stale "$restored"
  echo "SkiaSharp $restored: HeapScope's native library without the DNG SDK is built for it (ADR-038)."
  exit 0
fi
[ $# -eq 2 ] || { echo "Usage: skia-native.sh <rid> <publish folder> | check" >&2; exit 64; }
rid="$1"
out="$2"
case "$rid" in
  win-x64) files="libSkiaSharp.dll:libSkiaSharp.win-x64.dll:fbbce099ac3937de272e3714c17ce29d1100b99fc9193b78e67452bf263329a1 libSkiaSharp.pdb:libSkiaSharp.win-x64.pdb:b32f942a17a1a1b69ae6d47a46e7805b174cf47dd326f485311b6fdecd026bf4" ;;
  linux-x64) files="libSkiaSharp.so:libSkiaSharp.linux-x64.so:ae7c2afaefb4a19d1250a867f736380b6880eaa8ba6e5389d724ca0f921eef85" ;;
  osx-arm64|osx-x64) files="libSkiaSharp.dylib:libSkiaSharp.osx.dylib:03e783e2e31cba3b4ae9475954cd534992cd9b79339b344e126cb105ba5b8e20" ;;
  *) echo "Unsupported runtime identifier: $rid" >&2; exit 64 ;;
esac

# The libraries are built for one SkiaSharp release; SkiaSharp refuses a native library of another version at run time.
grep -q "\"SkiaSharp/$skia_version\"" "$out/HeapScope.deps.json" \
  || stale "$(grep -o '"SkiaSharp/[0-9.]*"' "$out/HeapScope.deps.json" | head -1 | tr -dc '0-9.' || true)"

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
