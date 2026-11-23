#!/usr/bin/env bash
# The exception that a native of a package throws, in the VM in C and natively (docs/spec/VM_C.md, "Pakete"): over the C ABI only its class and its text
# come to the VM, which makes the exception with makeClassError (vm_core.c). A package of its own (in a store of its own, FIRE_PACKAGES) whose natives
# fail: IndexOutOfBoundsException (message, index, length - no constructor of one argument: its message stayed undefined) and UnitMismatchException.
# The VM and the native build must print the same for IndexOutOfBounds (index and length read back from the text in the VM); the native build reaches
# UnitMismatchException only with makeUnitError of the runtime, not through fireError - only the VM is asked about it.
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
        { "name": "__PkUnit", "arguments": 0, "cpp": "", "c": "fire_pkabi_Unit", "needsList": false, "returnsReference": false, "host": false, "returns": "any" } ] } } ] }
EOF
cat > "$pkg/pkabi/pkabi.fire" <<'EOF'
class PkAbi {
    static Index(n) { return __PkIndex(n) }
    static Unit() { return __PkUnit() }
}
EOF
cat > "$pkg/pkabi/fire_bridge_pkabi.h" <<'EOF'
static Value fire_pkabi_Index(Value n) { return indexError("Array index", n.i, 3); }
static Value fire_pkabi_Unit(void) { return fireError("UnitMismatchException", "Expected unit 'm', got: s."); }
EOF
printf '%s\n' '#import "pkabi"' \
    'try { PkAbi.Index(7) } catch (IndexOutOfBoundsException e) { print("caught: " + e.message + " | " + e.index + " " + e.length) }' \
    > "$tmp/prog/index.fire"
printf '%s\n' '#import "pkabi"' \
    'try { PkAbi.Unit() } catch (UnitMismatchException e) { print("caught: " + e.message + " | " + e.expectedUnit) }' \
    > "$tmp/prog/unit.fire"
export FIRE_PACKAGES=$tmp/store
bad=0
want_index="caught: Array index 7 out of range (length 3). | 7 3"
want_unit="caught: Expected unit 'm', got: s. | undefined"
check() {   # name, got, want
    if [ "$2" == "$3" ]; then echo "ok   $1"; else echo "FAIL $1"; echo "  got:      $2"; echo "  expected: $3"; bad=1; fi
}
cd "$tmp/prog"
check "VM: IndexOutOfBoundsException of a package native" "$("$forge" run index.fire 2>&1 | grep -v '^Installing ' | tail -1)" "$want_index"
check "VM: UnitMismatchException of a package native" "$("$forge" run unit.fire 2>&1 | grep -v '^Installing ' | tail -1)" "$want_unit"
if [ "${2:-}" != "--vm-only" ]; then
    if "$forge" build index.fire --engine native -o "$tmp/prog/index" > "$tmp/build.log" 2>&1; then
        check "native: IndexOutOfBoundsException of a package native (as the VM)" "$("$tmp/prog/index" 2>&1 | tail -1)" "$want_index"
    else echo "FAIL native build:"; tail -5 "$tmp/build.log"; bad=1; fi
fi
exit $bad
