#!/usr/bin/env bash
# The rest of the command line of forge-c against the C# forge (docs/spec/FORGE_C.md, Phase D Schritt 4): `build|publish|bytecode --report json` (what
# spark reads), `run`, `native`, `bytecode` of projects and several files, `build` with -D/-i/--overlay/--debug-info, `test` with several files, -D, -f,
# `data`, the errors of the command line - each command with both forges in a copy of a small fixture (written here: files, a solution of a library, a
# program and a broken program, tests, a data program), comparing stdout, stderr, the exit code and the files they wrote (text files with the folder of the
# copy replaced; the pid of the report, the GUIDs of temporary folders, the seconds of `test` and the lines of building a package's natives are left out).
#
#   tests/compare-commands.sh <forge (C#)> <forge-c> [work folder]
#
# Both forges are given with ABSOLUTE paths (the commands run in the copies). FIRE_PACKAGES is the store of the C# forge (both use it); FIRE_TABLES, else
# native/tables of the repository, is forge-c's. COMMANDS_PACKAGES="vm native" also runs `test <project>` for every project of packages/ with both
# (the engines listed; slow). The work folder (default: a temporary one) is emptied after each command. Exit code 0 when everything matches.
set -u
if [ $# -lt 2 ]; then echo "usage: $0 <forge (C#)> <forge-c> [work folder]" >&2; exit 2; fi
FORGE_CS=$1; FORGE_C=$2
ROOT=$(cd "$(dirname "$0")/.." && pwd)
WORK=${3:-$(mktemp -d "${TMPDIR:-/tmp}/compare-commands-XXXXXX")}
mkdir -p "$WORK"; WORK=$(cd "$WORK" && pwd)
export FIRE_TABLES=${FIRE_TABLES:-$ROOT/native/tables} FIRE_PROGRAM_CACHE=0
CSDIR=$(dirname "$(command -v "$FORGE_CS")"); CDIR=$(dirname "$(command -v "$FORGE_C")")

# ---- the fixtures ----
F=$WORK/fixtures; rm -rf "$F"; mkdir -p "$F/files" "$F/sol/Core" "$F/sol/App/data" "$F/sol/App/testing" "$F/sol/Bad" "$F/tests" "$F/data"
printf 'print("hi")\nwarn 100: "w"\n' > "$F/files/a.fire"
printf 'print(x)\n' > "$F/files/bad.fire"
printf 'print("two")\n#if X\nprint("x")\n#endif\nterminate(3)\n' > "$F/files/two.fire"
printf 'throw new Exception("boom")\n' > "$F/files/ex.fire"
printf 'var a = Environment.Args\nprint(a.length)\nprint(a[0])\nprint(a[1])\n' > "$F/files/args.fire"
printf 'print("overlaid")\n' > "$F/files/ov.txt"
printf '{ "name": "All", "projects": ["Core/Core.fireproj", "App/App.fireproj", "Bad/Bad.fireproj"], "startup": "App" }\n' > "$F/sol/All.firesln"
printf '{ "name": "Core", "type": "library" }\n' > "$F/sol/Core/Core.fireproj"
printf 'class Greeter {\n    static string Hello(string name) { return "Hello " + name }\n}\n' > "$F/sol/Core/greeter.fire"
printf '{ "name": "App", "type": "exe", "references": [ { "project": "../Core/Core.fireproj" } ], "buildActions": { "data/**": "copy" }, "publish": { "enabled": true, "targets": ["../dist/App"] } }\n' > "$F/sol/App/App.fireproj"
printf '#import "Core"\nwarn 7: "careful"\nprint(Greeter.Hello("you"))\n#if EXTRA\nprint("extra")\n#endif\n' > "$F/sol/App/main.fire"
echo data > "$F/sol/App/data/x.txt"
printf 'class T {\n    [Test]\n    undefined A() { assert(1 == 1) }\n}\n' > "$F/sol/App/testing/t.fire"
printf '{ "name": "Bad", "type": "exe", "references": [ { "project": "../Core/Core.fireproj" } ] }\n' > "$F/sol/Bad/Bad.fireproj"
printf 'print(nothere)\n' > "$F/sol/Bad/main.fire"
printf 'class A {\n    [Test]\n    undefined One() { assert(1 == 1) }\n#if X\n    [Test]\n    undefined WithX() { assert(false) }\n#endif\n}\n' > "$F/tests/a.fire"
printf 'class B {\n    [Test]\n    undefined Two() { float f = 0.1\n print(f * 3) }\n}\nwarn 3: "w3"\n' > "$F/tests/b.fire"
# (a file is compiled as a file: `#include` from its folder, a message names the file)
mkdir -p "$F/tests/sub" "$F/sol/Pkg"
printf 'class Inc { static int Two() { return 2 } }\n' > "$F/tests/sub/inc.fire"
printf '#import "testing"\n#include "inc.fire"\nclass I {\n    [Test]\n    undefined Two() { Testing.Assert.AreEqual(2, Inc.Two()) }\n}\n' > "$F/tests/sub/i.fire"
printf 'class C {\n    [Test]\n    undefined T() { int x = 1 + }\n}\n' > "$F/tests/sub/c.fire"
# (a package that is not installed: E6000)
printf '{ "name": "Pkg", "references": [ { "package": "no-such-package-xyz" } ] }\n' > "$F/sol/Pkg/Pkg.fireproj"
printf 'print(1)\n' > "$F/sol/Pkg/p.fire"
mkdir -p "$F/stats"
printf 'print("a")\n' > "$F/stats/a.fire"
printf 'class K { int x\n string Name() { return "k" + x } }\nvar k = new K()\nfor (int i = 0; i < 10; i++) print(k.Name() + i)\nprint(Math.Sqrt(2.0))\n' > "$F/stats/b.fire"
(cd "$F/stats" && "$FORGE_CS" build a.fire --engine native --toolchain gcc --stats -o pa > /dev/null 2>&1; "$FORGE_CS" build b.fire --engine native --toolchain gcc --stats -o pb > /dev/null 2>&1
 rm -f pa pb pa.map pb.map a.fire b.fire; printf '{"Target": 3}' > bad.json; printf 'null' > null.json; printf '{nope' > broken.json)
printf '#import "data"\n[Table "people"]\nclass Person {\n    [Key] int id\n    string name\n}\n[Migration 1, "people"]\nclass CreatePeople {\n    undefined Up(Data.Migrator m) {\n        m.CreateTable("people", [m.Key("id"), m.Column("name", "string", 100, true)])\n    }\n    undefined Down(Data.Migrator m) { m.DropTable("people") }\n}\nprint("top level")\n' > "$F/data/app.fire"

# ---- one command with both: same (0) or not (1) ----
norm() { sed -E -e "s#$WORK/run-(cs|fc)#<DIR>#g" -e 's/"pid":[0-9]+/"pid":0/' -e 's#fire-(native|test|run|data)-[0-9a-f]{32}#fire-\1-<G>#g' -e "s#$CSDIR#<FORGE>#g" -e "s#$CDIR#<FORGE>#g" \
  -e 's/\(([0-9]+\.)?[0-9]+ s\)/(<T>)/' -e '/Building the native part of (the package|graphics)/d' -e '/"type":"background"/d'; }
same=0; different=0
one() {
  local fixture=$1 nofiles=; shift
  [ "$1" = nofiles ] && { nofiles=1; shift; }   # (a program with debug information: it names the temporary folder of its build)
  rm -rf "$WORK/run-cs" "$WORK/run-fc"; mkdir -p "$WORK/run-cs" "$WORK/run-fc"
  cp -r "$F/$fixture/." "$WORK/run-cs/"; cp -r "$F/$fixture/." "$WORK/run-fc/"
  (cd "$WORK/run-cs" && "$FORGE_CS" "$@" > ../o-cs.txt 2> ../e-cs.txt < /dev/null; echo "exit $?" >> ../o-cs.txt)
  (cd "$WORK/run-fc" && "$FORGE_C" "$@" > ../o-fc.txt 2> ../e-fc.txt < /dev/null; echo "exit $?" >> ../o-fc.txt)
  local f ok=1 files=""
  for f in o-cs o-fc e-cs e-fc; do norm < "$WORK/$f.txt" > "$WORK/$f.n"; done
  cmp -s "$WORK/o-cs.n" "$WORK/o-fc.n" && cmp -s "$WORK/e-cs.n" "$WORK/e-fc.n" || ok=0
  if ! diff -rq "$WORK/run-cs" "$WORK/run-fc" > "$WORK/files.diff"; then
    files=$(grep '^Files ' "$WORK/files.diff" | while read -r _ a _ b _; do
      case "$a" in *.fpk|*.db|*/obj/publish/*|*/Core-*.json) continue ;; esac   # (the paths of the copies inside a package, the times of a database and of a stamp)
      file "$a" | grep -q text || { echo "$a"; continue; }
      cmp -s <(sed "s#$WORK/run-cs#X#g" "$a") <(sed "s#$WORK/run-fc#X#g" "$b") || echo "$a"; done; grep -v '^Files ' "$WORK/files.diff")
    [ -n "$files" ] && [ -z "$nofiles" ] && ok=0
  fi
  if [ $ok = 1 ]; then same=$((same + 1)); else
    different=$((different + 1)); echo "  differs: ($fixture) $*"
    diff "$WORK/o-cs.n" "$WORK/o-fc.n" | head -8 | sed 's/^/    /'; diff "$WORK/e-cs.n" "$WORK/e-fc.n" | head -6 | sed 's/^/    /'
    [ -n "$files" ] && echo "$files" | head -4 | sed 's/^/    files: /'
  fi
  rm -rf "$WORK/run-cs" "$WORK/run-fc"
}

# the report (spark)
for a in "build a.fire --report json" "build bad.fire --report json" "bytecode a.fire --report json" "bytecode a.fire --report json -o x/y.fbc" \
         "build a.fire --engine native --report json -o a.out" "nofiles build a.fire --engine native --report=json --debug-info -o a.out" "build bad.fire --engine native --report json" \
         "bytecode a.fire bad.fire --report json -D X" "build nope.fire --report json" "build a.fire --report xml" "build a.fire --report json --overlay a.fire=ov.txt" \
         "bytecode a.fire --report json --overlay=a.fire=ov.txt" "build a.fire --report json --config nope.json" "build a.fire --report json -t esp32 --engine vm" \
         "build --report json" "build a.fire b.fire --report json" "build a.fire --report json --engine native -t wasm" "bytecode a.fire --report json -i 32 -f 32" \
         "build a.fire --report json -m debug --types strict"; do one files $a; done
for a in "build All.firesln --report json" "build App/App.fireproj --report json -D EXTRA" "build All.firesln -p Core --report json" "build All.firesln -p Core --check --report json" \
         "build All.firesln -p Bad --report json" "build All.firesln -p Bad --report json --engine native" "build All.firesln --all --report json" \
         "build All.firesln --all --report json --engine native" "bytecode All.firesln --report json" "bytecode All.firesln -p Core --report json" \
         "build All.firesln --report json --engine native" "publish All.firesln --report json" "publish All.firesln -p Bad --report json"; do one sol $a; done
# run, bytecode, native, build with the options of the files
for a in "run a.fire" "run a.fire two.fire" "run a.fire two.fire -D X" "run ex.fire" "run nope.fire" "run" "run a.fire -i 32" "run args.fire -- a b" "bytecode a.fire two.fire -D X -o x.fbc" \
         "bytecode a.fire" "build a.fire two.fire -D X" "build a.fire -i 32 -o p" "build a.fire --global-jobs sync" "build a.fire two.fire --engine native -D X -o n" \
         "build a.fire -X" "publish a.fire -o x" "native a.fire two.fire -D X" "native a.fire -o sub/x.c -t esp32" "native nope.fire" "native a.fire -t nope" \
         "help" "frob"; do one files $a; done
for a in "run All.firesln" "run All.firesln -p Core" "native All.firesln" "bytecode All.firesln -D EXTRA -o p.fbc" "build All.firesln -D EXTRA" \
         "build All.firesln -D EXTRA --engine native" "build All.firesln --all -D EXTRA" "test All.firesln" "test All.firesln -D EXTRA"; do one sol $a; done
# test
for a in "test a.fire" "test a.fire b.fire" "test a.fire b.fire -D X" "test b.fire -f 32" "test a.fire -i 32" "test a.fire b.fire --engine native -D X" \
         "test a.fire --types strict" "test a.fire -o x" "test a.fire nope.fire" "test b.fire --engine native -f 32 --keep-all" \
         "test sub/i.fire" "test sub/i.fire -D X" "test sub/i.fire --engine native" "test sub/c.fire" "test sub/c.fire a.fire"; do one tests $a; done
for a in "build Pkg/Pkg.fireproj" "build Pkg/Pkg.fireproj --report json" "bytecode Pkg/Pkg.fireproj"; do one sol $a; done
# data
for a in "data status app.fire --url memory:" "data migrate app.fire --url sqlite:x.db" "data import --url sqlite:x.db" "data migrate --url memory:" "data" "data frob" \
         "data status app.fire" "data status app.fire --url foo:x" "data status app.fire --url memory: --to 3"; do one data $a; done
# dump, stats
for a in "dump a.fire" "dump bad.fire" "dump a.fire -o d.txt" "dump a.fire two.fire -m debug -D X"; do one files $a; done
one sol dump All.firesln
[ -f "$F/stats/pa.stats.json" ] && for a in "stats pa.stats.json pb.stats.json" "stats pb.stats.json pa.stats.json" "stats pa.stats.json pa.stats.json" "stats pa.stats.json bad.json" \
  "stats null.json pa.stats.json" "stats broken.json pa.stats.json" "stats nope.json pa.stats.json" "stats pa.stats.json"; do one stats $a; done
echo "commands: $((same + different)): the same $same, different $different"

# ---- `test <project>` of every package project (COMMANDS_PACKAGES) ----
for engine in ${COMMANDS_PACKAGES:-}; do
  ps=0; pd=0; pn=0
  for p in $(cd "$ROOT" && find packages -name '*.fireproj' -not -path '*/bin/*' | LC_ALL=C sort); do
    pn=$((pn + 1))
    (cd "$ROOT" && timeout 900 "$FORGE_CS" test "$p" --engine "$engine" < /dev/null > "$WORK/p-cs.txt" 2>&1; echo "exit $?" >> "$WORK/p-cs.txt")
    (cd "$ROOT" && timeout 900 "$FORGE_C" test "$p" --engine "$engine" < /dev/null > "$WORK/p-fc.txt" 2>&1; echo "exit $?" >> "$WORK/p-fc.txt")
    if cmp -s <(norm < "$WORK/p-cs.txt") <(norm < "$WORK/p-fc.txt"); then ps=$((ps + 1)); else pd=$((pd + 1)); echo "  differs: test $p --engine $engine"; diff <(norm < "$WORK/p-cs.txt") <(norm < "$WORK/p-fc.txt") | head -6 | sed 's/^/    /'; fi
  done
  echo "test of the package projects ($engine): $pn: the same $ps, different $pd"
  different=$((different + pd))
done
[ $different = 0 ]
