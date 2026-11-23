#!/bin/bash
# Compares the C# ember (src/fire.Ember) with ember in fire (tools/ember): the same commands in a store of their own, then the output, the error
# output, the exit codes and the files (the store, the lock file, what create/blank/forge/index wrote) side by side.
#
#   tests/compare-ember.sh <folder of the C# ember> <fire ember program>
#
#   <folder of the C# ember>  the build folder with `ember` (e.g. src/fire.Compiler/bin/Release/net10.0/linux-x64)
#   <fire ember program>      ember in fire, built with `forge build tools/ember/ember.fireproj -o <file>` (a VM program)
#
# Both run from the same place: the ember under test is put into <tmp>/inst (the C# ember with its runtime as links next to it), so the store
# is <tmp>/inst/Packages for both and the paths in the output are the same. Nothing goes to the network: the sources are the folder
# PackageSource and an index on this machine, GitHub is tests/ember-fake-github.py (FIRE_GITHUB_API on 127.0.0.1). The .fpk files are made
# once with the C# ember, so that both install the same bytes; a .fpk that one of them writes (forge) is compared by its entries and its
# package.json (the compressed bytes and times differ).
# KEEP=1 keeps the folder. Exit code 0 when everything is the same.
set -u
CS_BIN=$(cd "$1" && pwd)
FIRE_EMBER=$(cd "$(dirname "$2")" && pwd)/$(basename "$2")
HERE=$(cd "$(dirname "$0")" && pwd)
T=$(mktemp -d /tmp/fire-ember-compare.XXXXXX)
export NO_PROXY=127.0.0.1,localhost no_proxy=127.0.0.1,localhost
unset GITHUB_TOKEN FIRE_GITHUB_TOKEN
SERVER_PID=

cleanup() {
    [ -n "$SERVER_PID" ] && kill "$SERVER_PID" 2>/dev/null
    [ "${KEEP:-0}" = 1 ] && echo "kept: $T" || rm -rf "$T"
}
trap cleanup EXIT

# ---- fixtures (once, with the C# ember) ----
FX=$T/fx
mkdir -p "$FX/files" "$FX/src/gpkg" "$FX/pk"
package() {   # name version [dependency]
    local dir=$FX/pk/$1-$2
    mkdir -p "$dir/$1"
    echo "class ${1^} { static string Version() { return \"$2\" } }" > "$dir/$1/$1.fire"
    local deps="[]"
    [ -n "${3:-}" ] && deps="[ \"$3\" ]"
    cat > "$dir/package.json" <<EOF
{
  "name": "$1", "version": "$2", "author": "Ada", "description": "The package $1 for the comparison of the two embers.", "license": "MIT",
  "dependencies": $deps,
  "imports": [ { "name": "$1", "prelude": "$1/$1.fire" } ]
}
EOF
    "$CS_BIN/ember" forge "$dir/package.json" -o "$FX/files" > /dev/null || { echo "cannot forge $1 $2"; exit 2; }
}
package mathkit 1.0.0
package mathkit 1.1.0
package kit2 1.0.0 mathkit
# the source of the repository o/gpkg (a package.json in its root) and its release
cat > "$FX/src/package.json" <<'EOF'
{ "name": "gpkg", "version": "1.0.0", "author": "Grace", "description": "A package from the fake GitHub.", "license": "MIT",
  // a comment, as the C# ember reads it
  "imports": [ { "name": "gpkg", "prelude": "gpkg/gpkg.fire" }, ], }
EOF
echo 'class Gpkg { static int Answer() { return 42 } }' > "$FX/src/gpkg/gpkg.fire"
echo '# gpkg' > "$FX/src/README.md"
"$CS_BIN/ember" forge "$FX/src/package.json" -o "$FX/files" > /dev/null || { echo "cannot forge gpkg"; exit 2; }
rm -rf "$FX/src/build"
# the index on this machine: mathkit 1.1.0 only there
python3 - "$FX/files" <<'EOF'
import hashlib, json, os, sys
d = sys.argv[1]
f = os.path.join(d, "mathkit-1.1.0.fpk")
data = open(f, "rb").read()
index = {"format": 1, "packages": [{"name": "mathkit", "author": "Ada", "description": "The package mathkit (web).", "license": "MIT",
         "versions": [{"version": "1.1.0", "url": "mathkit-1.1.0.fpk", "sha256": hashlib.sha256(data).hexdigest(), "size": len(data)},
                      {"version": "0.9.0", "url": "mathkit-0.9.0.fpk", "sha256": "00", "yanked": True}]}]}
json.dump(index, open(os.path.join(d, "index.json"), "w"), indent=2)
EOF

python3 "$HERE/ember-fake-github.py" "$FX" "$T/port" &
SERVER_PID=$!
for i in $(seq 50); do [ -s "$T/port" ] && break; sleep 0.1; done
PORT=$(cat "$T/port")
export FIRE_GITHUB_API=http://127.0.0.1:$PORT

# ---- one run ----
INST=$T/inst
WORK=$T/work

setup() {   # side
    rm -rf "$INST" "$WORK"
    mkdir -p "$INST/PackageSource" "$WORK/files"
    if [ "$1" = cs ]; then
        for f in "$CS_BIN"/*; do
            n=$(basename "$f")
            case "$n" in
                ember|ember.dll|ember.deps.json|ember.runtimeconfig.json|fire.Package.Manager.dll) cp "$f" "$INST/$n" ;;
                Packages|Source|Samples|Templates|Prebuilt|forge|forge.*|fire|ember.json|PackageSource) ;;
                *) ln -s "$f" "$INST/$n" ;;
            esac
        done
    else
        cp "$FIRE_EMBER" "$INST/ember"
    fi
    cp "$FX/files/mathkit-1.0.0.fpk" "$FX/files/kit2-1.0.0.fpk" "$INST/PackageSource/"
    cp "$FX/files/mathkit-1.0.0.fpk" "$WORK/files/"
}

normalize() {
    sed -E -e "s#$T#<T>#g" -e 's#/tmp/ember-[0-9a-f]{32}#<TEMP>#g' -e "s#127\.0\.0\.1:$PORT#127.0.0.1:<PORT>#g" \
        -e 's#"installed": "[^"]*"#"installed": "<TIME>"#'
}

N=0
run() {   # name args...
    local name=$1
    shift
    N=$((N + 1))
    local base
    base=$OUT/$(printf %02d $N)-$name
    (cd "$WORK" && "$INST/ember" "$@" > "$base.out" 2> "$base.err"; echo $? > "$base.code")
    normalize < "$base.out" > "$base.o" && mv "$base.o" "$base.out"
    normalize < "$base.err" > "$base.e" && mv "$base.e" "$base.err"
}

snapshot() {   # name: the files of the store and the work folder, the text files with their content, a .fpk by its entries
    local file=$OUT/$(printf %02d $N)-$1.files
    (
        cd "$T"
        find inst/Packages work -type f 2>/dev/null | LC_ALL=C sort | while read -r f; do
            case "$f" in
                *.fpk) echo "== $f (entries)"; python3 -c 'import sys, zipfile; z = zipfile.ZipFile(sys.argv[1]); [print(n) for n in z.namelist()]; print(z.read("package.json").decode())' "$f" ;;
                # (the checksum and size of a package this ember packed itself - a source build, an index of what `forge` wrote - differ with the deflater)
                */ember-lock.json|work/out/index.json) echo "== $f"; python3 -c '
import json, sys
d = json.load(open(sys.argv[1]))
for e in d.get("gitHub", []):
    if e.get("kind") == "source": e["sha256"] = "<packed here>"
for p in d.get("packages", []):
    for v in p.get("versions", []): v["sha256"] = "<packed here>"; v["size"] = 0
print(json.dumps(d, indent=2, ensure_ascii=False))' "$f"; cat "$f" | wc -l ;;
                *.json|*.fire|*.hpp|*.md) echo "== $f"; cat "$f"; echo ;;
                *) echo "== $f ($(stat -c %s "$f") bytes)" ;;
            esac
        done
    ) | normalize > "$file"
}

scenario() {
    run noargs
    run help help
    run unknown frobnicate
    run install-usage install
    run list-empty list
    run sources-default sources
    cat > "$INST/ember.json" <<EOF
{ "sources": [ "PackageSource", "http://127.0.0.1:$PORT/files/index.json" ], }
EOF
    run sources sources
    run find find
    run find-kit find kit
    run find-none find nothingatall
    run install-version install mathkit@1.0.0
    run install-again install mathkit@1.0.0
    run install-dep install kit2
    run list list
    snapshot after-install
    run remove-needed remove mathkit
    run update update
    run list-updated list
    run install-yanked install mathkit@0.9.0
    run install-noversion install mathkit@7.0.0
    run remove-kit2 remove kit2
    run remove-mathkit remove mathkit
    run remove-none remove nosuch
    run install-file install files/mathkit-1.0.0.fpk
    run install-none install nosuch
    run gh-install install github:o/gpkg
    run gh-again install github:o/gpkg
    run gh-list list
    run gh-sources sources
    snapshot after-github
    run gh-update update github:o/gpkg
    run gh-main install github:o/gpkg@main --force
    run gh-main-list list
    snapshot after-source
    run gh-remove remove github:o/gpkg
    run gh-limited install github:o/limited
    run gh-missing install github:o/missing
    run gh-bad install github:bad
    run gh-name-usage install mathkit --name x
    run create create mk/mathkit.json
    run create-exists create mk/mathkit.json
    run blank blank blank.json
    run forge forge mk/mathkit.json -o out
    run forge-problems forge blank.json
    run index index out http://example.com/packages
    mkdir -p "$WORK/cache/linux/k1"
    printf 'xxxx' > "$WORK/cache/linux/k1/libfire.a"
    export FIRE_NATIVE_CACHE=$WORK/cache
    run cache cache
    run cache-clean cache clean
    run cache-usage cache foo
    unset FIRE_NATIVE_CACHE
    snapshot end
}

for side in cs fire; do
    OUT=$T/out-$side
    mkdir -p "$OUT"
    N=0
    setup $side
    scenario
done

# ---- the comparison ----
same=0
differ=0
for f in "$T/out-cs"/*; do
    n=$(basename "$f")
    if cmp -s "$f" "$T/out-fire/$n"; then same=$((same + 1)); else
        differ=$((differ + 1))
        echo "--- differs: $n"
        diff "$f" "$T/out-fire/$n" | head -${DIFF_LINES:-20}
    fi
done
echo "ember C# vs fire: $same same, $differ differ ($N commands, outputs, exit codes and file snapshots)"
[ $differ = 0 ]
