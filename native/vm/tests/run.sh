#!/usr/bin/env bash
# The spike of the VM in C (docs/spec/VM_SPIKE.md): compiles each script here with the C# forge to a program file, runs it with the C VM and with
# `forge run` (the C# VM) and compares what they print and their exit code; then runs all program files one after the other in ONE process of the C VM
# (L6: a script that fails ends only itself) and checks that nothing of a script is left alive afterwards.
#
#   native/vm/tests/run.sh <forge> [<fire>]      (<fire>: the C VM; default: built here with cc into a temporary folder)
set -u
forge=$(realpath "$1")
here=$(cd "$(dirname "$0")" && pwd)
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
if [ $# -ge 2 ]; then fire=$(realpath "$2"); else fire=$tmp/fire; ${CC:-cc} -O2 -DFIRE_VM_STATS "$here/../fire.c" -o "$fire" -lm || exit 1; fi
bad=0
for f in "$here"/*.fire; do
    n=$(basename "$f" .fire)
    "$forge" bytecode "$f" -o "$tmp/$n.fbc" > /dev/null 2>&1 || { echo "FAIL $n: does not compile"; bad=1; continue; }
    "$fire" "$tmp/$n.fbc" > "$tmp/$n.c.out" 2> "$tmp/$n.c.err"; c=$?
    if grep -q '^//# vm-spike: fails' "$f"; then
        # (a script the spike cannot run to its end: it must fail with an error of the runtime, after printing what it printed before)
        if [ $c -ne 1 ] || ! grep -q 'fire runtime error' "$tmp/$n.c.err"; then echo "FAIL $n: expected an error of the runtime (exit $c)"; bad=1; else echo "ok   $n (ends with: $(head -1 "$tmp/$n.c.err"))"; fi
        continue
    fi
    "$forge" run "$f" > "$tmp/$n.cs.out" 2> "$tmp/$n.cs.err"; s=$?
    if [ $c -ne $s ] || ! cmp -s "$tmp/$n.c.out" "$tmp/$n.cs.out"; then
        echo "FAIL $n: exit $c (C) / $s (C#)"; diff "$tmp/$n.c.out" "$tmp/$n.cs.out" | head -20; bad=1
    elif [ $c -ne 0 ] && ! cmp -s <(grep -v '^warning' "$tmp/$n.c.err") <(grep -v '^warning' "$tmp/$n.cs.err"); then
        echo "FAIL $n: the error output differs"; diff <(grep -v '^warning' "$tmp/$n.c.err") <(grep -v '^warning' "$tmp/$n.cs.err") | head; bad=1
    else echo "ok   $n (exit $c)"; fi
done
# all of them in one process, twice: the failing ones end only themselves, nothing stays alive
all=$(ls "$tmp"/*.fbc | sort)
"$fire" --stats $all $all > "$tmp/all.out" 2> "$tmp/all.err"
expected=$(for n in $all $all; do b=$(basename "$n" .fbc); [ -f "$tmp/$b.c.out" ] && cat "$tmp/$b.c.out"; done)
if [ "$expected" != "$(cat "$tmp/all.out")" ]; then echo "FAIL one process: the output differs from the single runs"; bad=1; fi
if grep '^\[fire vm\]' "$tmp/all.err" | grep -v 'alive afterwards: 0 handles (objects, arrays), 0 strings, 0 in the temporary pool, 0 classes' | grep -q .; then
    echo "FAIL one process: something stayed alive"; grep '^\[fire vm\]' "$tmp/all.err"; bad=1
else echo "ok   $(grep -c '^\[fire vm\]' "$tmp/all.err") programs in one process, nothing left alive after any of them"; fi
exit $bad
