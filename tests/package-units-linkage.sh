#!/usr/bin/env bash
# The units of a package (`native.units`, docs/spec/EMBED.md) with the linkages static, shared and plugin: a program that imports the package forge (libforge
# is its unit) compiles a source in the process. The unit is compiled once with the library of the module `pkg:forge` (in the user's cache) and linked into it,
# the module's C gets FIRE_PKG_UNITS_FORGE - without it the bridge would look for libfireforge next to the program and the program would fail. A plugin that
# imports forge calls the natives in C of its host (built with linkage shared), so the unit is the host's.
#
#   tests/package-units-linkage.sh <forge> [<toolchain>]     (default gcc: shared and plugin need gcc or clang; with tcc only static is built - shared
#                                                            falls back to static; FIRE_TABLES / FIRE_NATIVE_ROOT / FIRE_PACKAGES as for the forge)
# A first run compiles libforge.c once per linkage (with gcc -O2 about a minute and a half each); later runs take the libraries from the cache.
set -u
forge=$(realpath "$1")
toolchain=${2:-gcc}
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
cd "$tmp"
printf '%s\n' '#import "forge"' \
    'var c = Forge.Compiler.CompileSource("print(1)\n", "x.fire")' \
    'print("compiled: " + c.Succeeded + " " + (c.Program.Length > 100))' > main.fire
printf '%s\n' '#import "forge"' \
    'class Kit {' \
    '    static bool Ok = Kit.Run()' \
    '    static bool Run() {' \
    '        var c = Forge.Compiler.CompileSource("print(1)\n", "x.fire")' \
    '        print("plugin: " + c.Succeeded)' \
    '        return c.Succeeded' \
    '    }' \
    '}' > kit.fire
printf '%s\n' '#import "forge"' \
    'var c = Forge.Compiler.CompileSource("print(2)\n", "y.fire")' \
    'print("host: " + c.Succeeded)' \
    'var kit = Library.Load("./kit.so")' \
    'print(kit.Classes()[0])' > host.fire
bad=0
check() {   # name, got, want
    if [ "$2" == "$3" ]; then echo "ok   $1"; else echo "FAIL $1"; echo "  got:      $2"; echo "  expected: $3"; bad=1; fi
}
for linkage in static shared; do
    mkdir -p "$linkage"
    if "$forge" build main.fire --engine native --toolchain "$toolchain" --linkage "$linkage" -o "$linkage/main" > "$linkage.log" 2>&1; then
        check "linkage $linkage ($toolchain): a program that imports forge compiles in the process" "$(cd "$linkage" && ./main 2>&1 | tail -1)" "compiled: True True"
    else echo "FAIL linkage $linkage ($toolchain): the build"; tail -5 "$linkage.log"; bad=1; fi
done
if [ "$toolchain" != "tcc" ]; then
    if "$forge" build host.fire --engine native --toolchain "$toolchain" --linkage shared -o host > host.log 2>&1 \
        && "$forge" build kit.fire --engine native --toolchain "$toolchain" --linkage plugin --host host -o kit > kit.log 2>&1; then
        check "linkage plugin ($toolchain): a plugin that imports forge, loaded by a host with linkage shared" "$(./host 2>&1 | tr '\n' '|')" "host: True|plugin: True|Kit|"
    else echo "FAIL linkage plugin ($toolchain): the build"; tail -5 host.log kit.log; bad=1; fi
fi
exit $bad
