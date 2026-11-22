#!/usr/bin/env bash
# Runs the cases of tests/cases with a forge and compares what they print with what is expected (docs/tools/testing.md "The cases as files"): the check
# that does not need the test program in C# - for a forge and a VM in C as for the ones in C# (docs/ROADMAP_C_PORT.md, L5).
#
#   tests/run-cases.sh <forge> [<group>...] [-j <jobs>]        (groups: the folders of tests/cases; default: all)
#
# A case is right when `forge run <case>.fire`
#   - prints the lines of <case>.expected (and, in a group vm-*, ends with the line `UNBEHANDELT: <message>` where the program ends with an exception nothing
#     catches - without its stack trace; in the group trace with it, as stderr has it; in the group native the message and the trace are <case>.unhandled),
#   - or, with <case>.error, does not compile and names that part of the message,
#   - or, with <case>.diagnostics, writes exactly that to the error output (warnings, errors, the exception it ends with) and prints <case>.expected;
#     `//# types: strict` runs it with --types strict.
# In the modes of `//# modes:` (default: the mode the program asks for; in the group native: release). Not run: `//# skip: forge run ...` and `//# output: varies`.
# Exit code 0 when every case is right; the cases that are not are listed.
set -u
if [ $# -lt 1 ]; then sed -n '2,12p' "$0" | sed 's/^# \{0,1\}//'; exit 2; fi
forge=$(realpath "$1"); shift
root=$(cd "$(dirname "$0")" && pwd)/cases
jobs=$(nproc); groups=()
while [ $# -gt 0 ]; do if [ "$1" = -j ]; then jobs=$2; shift 2; else groups+=("$1"); shift; fi; done
[ ${#groups[@]} -gt 0 ] || for g in "$root"/*/; do groups+=("$(basename "$g")"); done
export forge root
# the look of the desktop stays out (like the test program, Program.cs): the font of GNOME/KDE would be the UI's font - native/3210, 4110 measured
# their text with Cantarell where the cases expect the console font
export FIRE_SYSTEM_THEME=off

one() {   # <group> <case file>
    local group=$1 f=$2 n dir tmp modes mode out code err actual want bad="" types extra
    dir=$(dirname "$f"); n=$(basename "$f" .fire)
    tail -n 12 "$f" | grep -q -e '^//# skip: forge run' -e '^//# output: varies' && { echo "SKIP $group/$n"; return; }
    modes=$(tail -n 12 "$f" | sed -n 's|^//# modes: ||p' | tr 'a-z' 'A-Z')
    [ -z "$modes" ] && [ "$group" = native ] && modes=RELEASE   # (the test program runs these in release, whatever the source asks for)
    types=$(tail -n 12 "$f" | sed -n 's|^//# types: ||p'); extra=(); [ -n "$types" ] && extra=(--types "$types")
    cd "$dir" || return
    tmp=$(mktemp)
    for mode in ${modes:-default}; do
        if [ "$mode" = default ]; then out=$(timeout 120 "$forge" run "$n.fire" "${extra[@]}" 2> "$tmp"; echo "x$?"); else out=$(timeout 120 "$forge" run "$n.fire" -m "$mode" "${extra[@]}" 2> "$tmp"; echo "x$?"); fi
        code=${out##*x}; out=${out%x*}
        err=$(grep -v '^warning ' "$tmp")
        if [ -f "$n.diagnostics" ]; then   # a case about the messages of the compiler: the whole error output, and the output
            grep -v -e '^Building the native part of ' -e '^Installing the standard package ' "$tmp" | cmp -s - "$n.diagnostics" || bad="$bad $mode(diagnostics)"
            want=$(cat "$n.expected"; echo x); [ "$out" = "${want%x}" ] || bad="$bad $mode"
            continue
        fi
        if [ -f "$n.error" ]; then
            { [ "$code" != 0 ] && printf '%s' "$err" | grep -qF -- "$(cat "$n.error")"; } || bad="$bad $mode"
            continue
        fi
        actual=$out
        if [ "$code" != 0 ] && [ -n "$err" ]; then
            if [ "$group" = native ]; then [ -f "$n.unhandled" ] && [ "$err" = "$(cat "$n.unhandled")" ] || bad="$bad $mode(end)"
            elif [ "$group" = trace ]; then actual="${actual}UNBEHANDELT: $err"$'\n'
            else actual="${actual}UNBEHANDELT: $(printf '%s\n' "$err" | grep -v -e '^   at ' -e '^   \.\.\. ' -e '^   --- awaited at ---$' -e "^   in thread '")"$'\n'; fi
        elif [ -f "$n.unhandled" ]; then bad="$bad $mode(end)"; fi
        want=$(cat "$n.expected"; echo x); want=${want%x}
        [ "$actual" = "$want" ] || bad="$bad $mode"
    done
    rm -f "$tmp"
    if [ -z "$bad" ]; then echo "OK   $group/$n"; else echo "FAIL $group/$n:$bad"; fi
}
export -f one

"$forge" install-standard > /dev/null 2>&1
result=$(for g in "${groups[@]}"; do for f in "$root/$g"/*.fire; do printf '%s\0%s\0' "$g" "$f"; done; done | xargs -0 -n 2 -P "$jobs" bash -c 'one "$@"' _ 2> /dev/null | LC_ALL=C sort)
# a case that was wrong runs once more, alone: what depends on timing (threads) must not fail because the machine was busy with the other cases
again=$(printf '%s\n' "$result" | sed -n 's/^FAIL \([^:]*\):.*/\1/p')
if [ -n "$again" ]; then
    second=$(for c in $again; do one "${c%%/*}" "$root/$c.fire" 2> /dev/null; done)
    for c in $(printf '%s\n' "$second" | sed -n 's/^OK   //p'); do result=$(printf '%s\n' "$result" | sed "s|^FAIL $c:.*|OK   $c (alone, at the second try)|"); done
fi
printf '%s\n' "$result" | grep -v '^OK   [^ ]*$'
ok=$(printf '%s\n' "$result" | grep -c '^OK '); fail=$(printf '%s\n' "$result" | grep -c '^FAIL '); skip=$(printf '%s\n' "$result" | grep -c '^SKIP ')
echo "$ok right, $fail wrong, $skip not run"
[ "$fail" = 0 ]
