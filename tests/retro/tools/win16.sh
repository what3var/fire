#!/bin/sh
# Runs PROG.EXE of a folder (a program for Windows 3.x) under Wine in Xvfb (at most 120 s) and prints its output (FIRE_STDOUT: a Windows program has no
# console). The prefix of Wine is FIRE_RETRO_WINEPREFIX or ~/.cache/fire/retro-wine (made on the first run). Wine runs without TMPDIR: the Wine 9 of
# Ubuntu frees getenv("TMPDIR") and ends every program with "free(): invalid pointer".
#   win16.sh <folder>
dir=$1
prefix=${FIRE_RETRO_WINEPREFIX:-$HOME/.cache/fire/retro-wine}
mkdir -p "$prefix"
rm -f "$dir/OUT.TXT"
cd "$dir" || exit 1
env -u TMPDIR WINEDEBUG=-all WINEPREFIX="$prefix" FIRE_STDOUT=OUT.TXT timeout 120 xvfb-run -a wine PROG.EXE > wine.log 2>&1
code=$?
if [ -f OUT.TXT ]; then tr -d '\r' < OUT.TXT; else echo "(no output)"; cat wine.log; fi
[ $code -eq 124 ] && echo "(Wine ended after 120 s)"
exit 0
