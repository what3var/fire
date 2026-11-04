#!/bin/sh
# Checks fire_display_dcomp.h against the Windows SDK of MinGW (vtable places, GUIDs, structures): builds tools/check_dcomp.cpp and runs it under Wine.
set -e
here=$(cd "$(dirname "$0")" && pwd)
out=${TMPDIR:-/tmp}/fire_check_dcomp.exe
x86_64-w64-mingw32-g++ -O1 -static -Wall -Wextra -Wno-unused-function -o "$out" "$here/check_dcomp.cpp"
WINEDEBUG=-all wine "$out" 2>/dev/null | tr -d '\r'
