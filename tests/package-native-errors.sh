#!/usr/bin/env bash
# The exception that a native of a package throws, in the VM in C and natively (docs/spec/VM_C.md, "Pakete"): over the C ABI only its class and its text
# come to the VM, which makes the exception with makeClassError (vm_core.c). A package of its own (in a store of its own, FIRE_PACKAGES) whose natives
# fail: IndexOutOfBoundsException (message, index, length - no constructor of one argument: its message stayed undefined) and UnitMismatchException.
# The VM and the native build must print the same for both: makeClassError of the VM (vm_core.c) and the one the native build generates
# (gen/cppgenerator2.c) make a class without a constructor of one argument with its constructor of the fewest parameters, the message first and
# undefined for the rest, and read the index and the length of an IndexOutOfBoundsException back from the text (before, the native build skipped
# such classes: `fireError("UnitMismatchException", ...)` of a package native ended the program instead of throwing).
#
#   tests/package-native-errors.sh <forge> [--vm-only]      (FIRE_TABLES / FIRE_NATIVE_ROOT as for the forge; the standard packages are installed
#                                                            into the temporary store)
set -u
forge=$(realpath "$1")
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
pkg=$tmp/store/fire-pkabi
mkdir -p "$pkg/pkabi" "$tmp/prog"
cat > "$pkg/package.json" <<'EOF'
{ "format": 1, "name": "fire-pkabi", "version": "1.0.0", "author": "test", "description": "natives that fail", "license": "fire", "homepage": "",
  "dependencies": [],
  "imports": [ { "name": "pkabi", "standard": false, "prelude": "pkabi/pkabi.fire", "requires": [],
    "native": { "language": "c", "sources": [ "pkabi/fire_bridge_pkabi.h" ], "platformSources": {}, "platforms": [], "libraries": {}, "linkLibraries": {},
      "exceptions": [ "IndexOutOfBoundsException", "UnitMismatchException" ],
      "functions": [
        { "name": "__PkIndex", "arguments": 1, "cpp": "", "c": "fire_pkabi_Index", "needsList": false, "returnsReference": false, "host": false, "returns": "any" },
        { "name": "__PkUnit", "arguments": 0, "cpp": "", "c": "fire_pkabi_Unit", "needsList": false, "returnsReference": false, "host": false, "returns": "any" },
        { "name": "__PkIndexText", "arguments": 0, "cpp": "", "c": "fire_pkabi_IndexText", "needsList": false, "returnsReference": false, "host": false, "returns": "any" } ] } } ] }
EOF
cat > "$pkg/pkabi/pkabi.fire" <<'EOF'
class PkAbi {
    static Index(n) { return __PkIndex(n) }
    static Unit() { return __PkUnit() }
    static IndexText() { return __PkIndexText() }
}
EOF
cat > "$pkg/pkabi/fire_bridge_pkabi.h" <<'EOF'
static Value fire_pkabi_Index(Value n) { return indexError("Array index", n.i, 3); }
static Value fire_pkabi_Unit(void) { return fireError("UnitMismatchException", "Expected unit 'm', got: s."); }
static Value fire_pkabi_IndexText(void) { return fireError("IndexOutOfBoundsException", "Array index -9 out of range (length 2)."); }
EOF
printf '%s\n' '#import "pkabi"' \
    'try { PkAbi.Index(7) } catch (IndexOutOfBoundsException e) { print("caught: " + e.message + " | " + e.index + " " + e.length) }' \
    > "$tmp/prog/index.fire"
printf '%s\n' '#import "pkabi"' \
    'try { PkAbi.Unit() } catch (UnitMismatchException e) { print("caught: " + e.message + " | " + e.expectedUnit) }' \
    > "$tmp/prog/unit.fire"
printf '%s\n' '#import "pkabi"' \
    'try { PkAbi.IndexText() } catch (IndexOutOfBoundsException e) { print("caught: " + e.message + " | " + e.index + " " + e.length) }' \
    > "$tmp/prog/text.fire"
export FIRE_PACKAGES=$tmp/store
bad=0
want_index="caught: Array index 7 out of range (length 3). | 7 3"
want_unit="caught: Expected unit 'm', got: s. | undefined"
want_text="caught: Array index -9 out of range (length 2). | -9 2"
check() {   # name, got, want
    if [ "$2" == "$3" ]; then echo "ok   $1"; else echo "FAIL $1"; echo "  got:      $2"; echo "  expected: $3"; bad=1; fi
}
cd "$tmp/prog"
check "VM: IndexOutOfBoundsException of a package native" "$("$forge" run index.fire 2>&1 | grep -v '^Installing ' | tail -1)" "$want_index"
check "VM: UnitMismatchException of a package native" "$("$forge" run unit.fire 2>&1 | grep -v '^Installing ' | tail -1)" "$want_unit"
check "VM: IndexOutOfBoundsException of fireError (index and length from the text)" "$("$forge" run text.fire 2>&1 | grep -v '^Installing ' | tail -1)" "$want_text"
if [ "${2:-}" != "--vm-only" ]; then
    if "$forge" build index.fire --engine native -o "$tmp/prog/index" > "$tmp/build.log" 2>&1; then
        check "native: IndexOutOfBoundsException of a package native (as the VM)" "$("$tmp/prog/index" 2>&1 | tail -1)" "$want_index"
    else echo "FAIL native build:"; tail -5 "$tmp/build.log"; bad=1; fi
    if "$forge" build unit.fire --engine native -o "$tmp/prog/unit" > "$tmp/build.log" 2>&1; then
        check "native: UnitMismatchException of a package native (as the VM)" "$("$tmp/prog/unit" 2>&1 | tail -1)" "$want_unit"
    else echo "FAIL native build:"; tail -5 "$tmp/build.log"; bad=1; fi
    if "$forge" build text.fire --engine native -o "$tmp/prog/text" > "$tmp/build.log" 2>&1; then
        check "native: IndexOutOfBoundsException of fireError (as the VM)" "$("$tmp/prog/text" 2>&1 | tail -1)" "$want_text"
    else echo "FAIL native build:"; tail -5 "$tmp/build.log"; bad=1; fi
fi
exit $bad
