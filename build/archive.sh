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

# Packs a published build into its release archive, or unpacks one (sprint 3.9, ADR-039): a .zip for Windows (Explorer
# opens it), a .tar.gz elsewhere (it keeps the executables' modes). The archive's top folder is the build's folder.
# Usage: archive.sh pack <build folder> <output folder>   (prints the archive's path; the runtime is the folder's suffix)
#        archive.sh unpack <archive> <output folder>      (prints the build folder's path)
set -euo pipefail
[ $# -eq 3 ] || { echo "Usage: archive.sh pack <build folder> <output folder> | unpack <archive> <output folder>" >&2; exit 64; }
here="$(cd "$(dirname "$0")" && pwd)"
mkdir -p "$3"
out="$(cd "$3" && pwd)"
case "$1" in
  pack)
    parent="$(cd "$(dirname "$2")" && pwd)"
    name="$(basename "$2")"
    # Relative names: Git Bash's GNU tar reads "D:" in a Windows path as a remote host.
    case "$name" in
      *-win-*) dotnet run "$here/zip.cs" -- create "$parent/$name" "$out/$name.zip" >&2; echo "$out/$name.zip" ;;
      *) (cd "$parent" && tar -czf "$out/$name.tar.gz" "$name"); echo "$out/$name.tar.gz" ;;
    esac
    ;;
  unpack)
    case "$2" in
      *.zip) name="$(basename "$2" .zip)"; dotnet run "$here/zip.cs" -- extract "$2" "$out" >&2 ;;
      *.tar.gz) name="$(basename "$2" .tar.gz)"; (cd "$out" && tar -xzf "$2") ;;
      *) echo "Not a release archive: $2" >&2; exit 64 ;;
    esac
    echo "$out/$name"
    ;;
  *) echo "Unknown action: $1" >&2; exit 64 ;;
esac
