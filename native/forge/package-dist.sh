#!/bin/sh
# The forge/VM part of a distribution of fire without .NET (docs/ROADMAP_C_PORT.md, Phase D step 6): forge-c built with the C compiler, then
# `forge-c package-dist <folder>` - forge, the VM in C and its variants, native/, Source/ (the standard packages) and Prebuilt/ (their libraries for this machine).
#
#   native/forge/package-dist.sh <folder> [--dry-run] [--name <program>] [--no-vm] [--no-prebuilt]
#
# CC: the C compiler (default cc). On Windows (MSYS2, w64devkit's sh): forge.exe with gcc of MinGW (-static); the folder is then what setup.iss takes with
# /DForgeDist=<folder>. Nothing of .NET is used.
set -e
if [ $# -lt 1 ]; then sed -n '2,9p' "$0" | sed 's/^# \{0,1\}//'; exit 2; fi
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../.." && pwd)
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*) exe=.exe; libs="-static" ;;
    *) exe=; libs="-lm -lpthread" ;;
esac
echo "building forge-c with ${CC:-cc}..."
# shellcheck disable=SC2086
${CC:-cc} -O2 "$root/native/forge/forge.c" -o "$tmp/forge-c$exe" $libs
# (the forge-c of the temporary folder reads the tables and the runtime of this repository; package-dist copies it and lets the copy build the rest)
FIRE_TABLES="$root/native/tables" FIRE_NATIVE_ROOT="$root/native" FIRE_PACKAGES="$tmp/Packages" "$tmp/forge-c$exe" package-dist "$@"
