#!/usr/bin/env bash
# A word buffer in the width of the program (docs/spec/VM_C.md): the VM in C keeps a word in 64 bits, but the elements of `word a[]` have the width of the
# program's `word` - 2, 4 or 8 bytes, as in a native build for that target (and the C# VM: width/8). The case native/2735 (its bytes through
# ToLittleEndian/ToBigEndian, ToString, ToUnicode, Json) runs
#   - in the VM with the words of a 32-bit and a 16-bit program: forge-c writes every program for the VM with 64-bit words (the VM runs only on this
#     machine), so the width in the program file is set by hand (the byte after the mode, the float and the int width);
#   - natively for targets of a fire.native.json that extend linux with "wordWidth": 32 and 16 (gcc, when there is one);
# and the lines must be the same.
#
#   tests/vm-word-width.sh <forge> <fire>       (FIRE_TABLES / FIRE_NATIVE_ROOT / FIRE_PACKAGES as for the forge)
set -u
forge=$(realpath "$1")
fire=$(realpath "$2")
here=$(cd "$(dirname "$0")" && pwd)
case=$(ls "$here"/cases/native/2735-*.fire)
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
cp "$case" "$tmp/w.fire"
cd "$tmp"
printf '%s\n' '{ "targets": {' '  "linux-w32": { "extends": "linux", "wordWidth": 32 },' '  "linux-w16": { "extends": "linux", "wordWidth": 16 }' '} }' > fire.native.json
bad=0
check() {   # name, got, want
    if [ "$2" == "$3" ]; then echo "ok   $1"; else echo "FAIL $1"; echo "  got:"; echo "$2" | sed 's/^/    /'; echo "  expected:"; echo "$3" | sed 's/^/    /'; bad=1; fi
}
"$forge" bytecode w.fire -o w64.fbc > bc.log 2>&1 || { echo "FAIL bytecode"; cat bc.log; exit 1; }
python3 - w64.fbc <<'EOF' || { echo "FAIL: the widths of the program file not found"; exit 1; }
import sys
d = open(sys.argv[1], 'rb').read()
# (the mode - 0, 1, 2 - and the float, int and word widths, 64 each: one place in the file)
hits = [i for i in range(6, len(d) - 3) if d[i] in (0, 1, 2) and d[i + 1:i + 4] == b'@@@']
if len(hits) != 1: sys.exit(1)
for bits in (32, 16):
    b = bytearray(d); b[hits[0] + 3] = bits
    open('w%d.fbc' % bits, 'wb').write(bytes(b))
EOF
want64=$(cat "${case%.fire}.expected")
check "VM, 64-bit words: the case's lines" "$("$fire" w64.fbc 2>&1)" "$want64"
want32=$(printf '%s\n' "3 12 12 4294967295" "2 1 2 1" "12 6" "16 52 18" "[258,65,4294967295]")
want16=$(printf '%s\n' "3 6 6 65535" "2 1 2 1" "6 3" "8 52 18" "[258,65,65535]")
check "VM, 32-bit words: elements of 4 bytes" "$("$fire" w32.fbc 2>&1)" "$want32"
check "VM, 16-bit words: elements of 2 bytes" "$("$fire" w16.fbc 2>&1)" "$want16"
if command -v gcc > /dev/null; then
    for bits in 32 16; do
        if "$forge" build w.fire --engine native -t linux-w$bits --toolchain gcc -o w-$bits > build.log 2>&1; then
            want=want$bits
            check "native, target with \"wordWidth\": $bits (as the VM)" "$(./w-$bits 2>&1)" "${!want}"
        else echo "FAIL native build for wordWidth $bits"; tail -5 build.log; bad=1; fi
    done
fi
exit $bad
