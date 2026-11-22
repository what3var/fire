#!/usr/bin/env bash
# Runs the cases of tests/cases with the VM in C (native/vm, docs/spec/VM_C.md): each case is compiled by a forge to a program file (`forge bytecode`),
# the C VM runs it, and what it prints is compared with what the case expects - the same rules as tests/run-cases.sh (which runs `forge run`).
#
#   tests/run-cases-vm.sh <forge> [<group>...] [-j <jobs>] [--fire <fire>] [--cache <dir>] [--list]
#
# <fire>: the C VM (default: built here with cc -O2 into the cache, with its variants for `#intwidth 32|16` and `#floatwidth 32` beside it:
# fire-i32, fire-i16, fire-f32, fire-i32-f32, fire-i16-f32 - a program of other widths runs in the variant, VM_C.md). The libraries of the natives of packages are looked up in the folder of <forge>
# (FIRE_PKG_PATH before it). The program files are kept in the cache (default: a temporary folder, removed at
# the end) and written again only when the case or the forge is newer - a second run with the same --cache only runs the VM.
# --list: every case with OK/FAIL (default: only those that are not right). At the end: the counts per group, then all.
set -u
if [ $# -lt 1 ]; then sed -n '2,10p' "$0" | sed 's/^# \{0,1\}//'; exit 2; fi
forge=$(realpath "$1"); shift
here=$(cd "$(dirname "$0")" && pwd)
root=$here/cases
jobs=$(nproc); groups=(); fire=""; cache=""; list=0
while [ $# -gt 0 ]; do
    case "$1" in
        -j) jobs=$2; shift 2 ;;
        --fire) fire=$(realpath "$2"); shift 2 ;;
        --cache) mkdir -p "$2"; cache=$(realpath "$2"); shift 2 ;;
        --list) list=1; shift ;;
        *) groups+=("$1"); shift ;;
    esac
done
[ ${#groups[@]} -gt 0 ] || for g in "$root"/*/; do groups+=("$(basename "$g")"); done
keep=1; [ -n "$cache" ] || { cache=$(mktemp -d); keep=0; }
[ $keep = 1 ] || trap 'rm -rf "$cache"' EXIT
if [ -z "$fire" ]; then
    fire=$cache/fire
    src="$here/../native/vm/fire.c"
    ${CC:-cc} -O2 -w "$src" -o "$fire" -lm -lpthread -ldl &
    for v in "i32 -DFIRE_INT32" "i16 -DFIRE_INT16" "f32 -DFIRE_FLOAT32" "i32-f32 -DFIRE_INT32 -DFIRE_FLOAT32" "i16-f32 -DFIRE_INT16 -DFIRE_FLOAT32"; do
        # shellcheck disable=SC2086
        ${CC:-cc} -O2 -w ${v#* } "$src" -o "$fire-${v%% *}" -lm -lpthread -ldl &
    done
    wait || true
    for f in "$fire" "$fire"-i32 "$fire"-i16 "$fire"-f32 "$fire"-i32-f32 "$fire"-i16-f32; do [ -x "$f" ] || { echo "the VM was not built: $f" >&2; exit 1; }; done
fi
# the libraries of the packages' natives: those forge built (its package store, Packages/) or brought (Prebuilt/), next to it
FIRE_PKG_PATH="${FIRE_PKG_PATH:+$FIRE_PKG_PATH:}$(dirname "$forge")"
export forge root fire cache FIRE_PKG_PATH
# the look of the desktop stays out (like the test program, Program.cs): the font of GNOME/KDE would be the UI's font - native/3210, 4110 measured
# their text with Cantarell where the cases expect the console font
export FIRE_SYSTEM_THEME=off

one() {   # <group> <case file>
    local group=$1 f=$2 n dir tmp modes mode out code err actual want bad="" types extra fbc cerr ccode
    dir=$(dirname "$f"); n=$(basename "$f" .fire)
    tail -n 12 "$f" | grep -q -e '^//# skip: forge run' -e '^//# output: varies' && { echo "SKIP $group/$n"; return; }
    modes=$(tail -n 12 "$f" | sed -n 's|^//# modes: ||p' | tr 'a-z' 'A-Z')
    [ -z "$modes" ] && [ "$group" = native ] && modes=RELEASE   # (the test program runs these in release, whatever the source asks for)
    types=$(tail -n 12 "$f" | sed -n 's|^//# types: ||p'); extra=(); [ -n "$types" ] && extra=(--types "$types")
    cd "$dir" || return
    mkdir -p "$cache/$group"
    tmp=$(mktemp)
    for mode in ${modes:-default}; do
        fbc=$cache/$group/$n.$mode.fbc; cerr=$cache/$group/$n.$mode.cerr; ccode=$cache/$group/$n.$mode.code
        if [ ! -f "$ccode" ] || [ "$f" -nt "$ccode" ] || [ "$forge" -nt "$ccode" ]; then
            rm -f "$fbc"
            if [ "$mode" = default ]; then timeout 120 "$forge" bytecode "$n.fire" -o "$fbc" "${extra[@]}" > /dev/null 2> "$cerr"; echo $? > "$ccode"
            else timeout 120 "$forge" bytecode "$n.fire" -o "$fbc" -m "$mode" "${extra[@]}" > /dev/null 2> "$cerr"; echo $? > "$ccode"; fi
        fi
        if [ "$(cat "$ccode")" = 0 ] && [ -f "$fbc" ]; then
            out=$(timeout ${VM_TIMEOUT:-20} "$fire" "$fbc" 2> "$tmp"; echo "x$?")
        else
            out="x$(cat "$ccode")"; : > "$tmp"
        fi
        code=${out##*x}; out=${out%x*}
        cat "$cerr" "$tmp" > "$tmp.all"
        err=$(grep -v '^warning ' "$tmp.all")
        if [ -f "$n.diagnostics" ]; then   # a case about the messages of the compiler: the whole error output, and the output
            grep -v -e '^Building the native part of ' -e '^Installing the standard package ' "$tmp.all" | cmp -s - "$n.diagnostics" || bad="$bad $mode(diagnostics)"
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
        want=$(cat "$n.expected" 2> /dev/null; echo x); want=${want%x}
        [ "$actual" = "$want" ] || bad="$bad $mode"
    done
    rm -f "$tmp" "$tmp.all"
    if [ -z "$bad" ]; then echo "OK   $group/$n"; else echo "FAIL $group/$n:$bad"; fi
}
export -f one

"$forge" install-standard > /dev/null 2>&1
result=$(for g in "${groups[@]}"; do for f in "$root/$g"/*.fire; do printf '%s\0%s\0' "$g" "$f"; done; done | xargs -0 -n 2 -P "$jobs" bash -c 'one "$@" < /dev/null; exit 0' _ 2> /dev/null | LC_ALL=C sort)
if [ $list = 1 ]; then printf '%s\n' "$result"; else printf '%s\n' "$result" | grep -v '^OK   [^ ]*$'; fi
for g in "${groups[@]}"; do
    gr=$(printf '%s\n' "$result" | grep " $g/")
    printf '%-16s %4d right, %4d wrong, %3d not run\n' "$g" "$(printf '%s\n' "$gr" | grep -c '^OK ')" "$(printf '%s\n' "$gr" | grep -c '^FAIL ')" "$(printf '%s\n' "$gr" | grep -c '^SKIP ')"
done
ok=$(printf '%s\n' "$result" | grep -c '^OK '); fail=$(printf '%s\n' "$result" | grep -c '^FAIL '); skip=$(printf '%s\n' "$result" | grep -c '^SKIP ')
echo "$ok right, $fail wrong, $skip not run"
[ "$fail" = 0 ]
