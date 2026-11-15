#!/usr/bin/env bash
# The reference of a compiler (docs/ROADMAP_C_PORT.md, phase A 4): what forge makes of the corpus, to compare two compilers - the one in C# against the one in C,
# or a compiler before and after a change. Two references of compilers that mean the same are the same files.
#
#   tests/reference.sh <forge> <output folder> [jobs]
#   diff -r <reference a> <reference b>          (or: diff a/manifest.sha256 b/manifest.sha256)
#
# For every case of tests/cases/*/ (the file as it is, its lines about the case are comments) and every sample project of spark:
#   <name>.dump.txt   the program as text (forge dump): everything the program file holds
#   <name>.fbc        the program file (forge bytecode)
#   <name>.c          the C of the native backend for this machine (forge native; cases only) - without the runtime next to it
#   <name>.log        what forge wrote to the error output (warnings, errors) and its exit codes
# and manifest.sha256: the checksum of every file, sorted. Paths of this machine do not get into the files: forge runs in the folder of the case.
set -u
if [ $# -lt 2 ]; then sed -n '2,13p' "$0" | sed 's/^# \{0,1\}//'; exit 2; fi
forge=$(realpath "$1"); out=$(mkdir -p "$2" && realpath "$2"); jobs=${3:-$(nproc)}
root=$(cd "$(dirname "$0")/.." && pwd)
export forge out

one() {   # <folder> <file> <name> <kind: case|project>
    cd "$1" || exit 1
    local base="$out/$3" log="$out/$3.log" tmp
    : > "$log"
    "$forge" dump "$2" -o "$base.dump.txt" 2>> "$log"; echo "dump: exit $?" >> "$log"
    "$forge" bytecode "$2" -o "$base.fbc" > /dev/null 2>> "$log"; echo "bytecode: exit $?" >> "$log"
    if [ "$4" = case ]; then
        tmp=$(mktemp -d)
        "$forge" native "$2" -o "$tmp/program.c" > /dev/null 2>> "$log"; echo "native: exit $?" >> "$log"
        [ -f "$tmp/program.c" ] && mv "$tmp/program.c" "$base.c"
        [ -f "$tmp/fire_natives.cpp" ] && mv "$tmp/fire_natives.cpp" "$base.natives.cpp"
        rm -rf "$tmp"
    fi
}
export -f one

"$forge" install-standard > /dev/null 2>&1   # (the standard packages once, not by every process at the same time)
{
    for group in "$root"/tests/cases/*/; do
        g=$(basename "$group"); mkdir -p "$out/cases/$g"
        for f in "$group"*.fire; do printf '%s\0%s\0%s\0%s\0' "$group" "$(basename "$f")" "cases/$g/$(basename "$f" .fire)" case; done
    done
    samples=$(dirname "$forge")/Samples
    if [ -d "$samples" ]; then
        mkdir -p "$out/samples"
        find "$samples" -name '*.fireproj' | sort | while read -r p; do
            printf '%s\0%s\0%s\0%s\0' "$(dirname "$p")" "$(basename "$p")" "samples/$(basename "$p" .fireproj)" project
        done
    fi
} | xargs -0 -n 4 -P "$jobs" bash -c 'one "$@"' _

cd "$out" || exit 1
find . -type f ! -name manifest.sha256 -print0 | LC_ALL=C sort -z | xargs -0 sha256sum > manifest.sha256
logs=$(find . -name '*.log' | wc -l); bad=$(find . -name '*.log' -print0 | xargs -0 grep -l 'exit [^0]' | wc -l)
echo "$(grep -c . manifest.sha256) files in $out ($(du -sh . | cut -f1)); $((logs - bad)) of $logs programs without an error"
[ "$bad" = 0 ] || { echo "with errors:"; find . -name '*.log' -print0 | xargs -0 grep -l 'exit [^0]' | sed 's/^/  /'; }
