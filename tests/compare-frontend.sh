#!/usr/bin/env bash
# The frontend in C against the one in C# (docs/spec/FORGE_C.md, docs/ROADMAP_C_PORT.md Phase C 1-5): `tokens`, `ast`, `diagnostics` and the program file
# (`fbc`) of every source of the corpus - tests/cases/**/*.fire, packages/**/*.fire, the samples and templates in src/fire.Project - must be byte-identical.
#
#   tests/compare-frontend.sh <forge (C#)> <forge-c> [tokens|ast|both|diag|fbc|markup|all] [work folder]
#
# `both` is tokens and ast; `diag` compares `diagnostics` (what forge prints while it compiles a source up to the type check: the resolver, the type
# checker, the preludes of its imports); `fbc` the program file `forge bytecode` writes (`forge fbc <mode> <file>`: the bytecode compiler, the linker,
# the line tables and in Debug the sources) in the modes Debug, Release and Performance (FBC_MODES="release" for fewer); `all` is all of them.
# FORGE_CS_CACHE=1 keeps the outputs of the C# forge that are in the work folder already.
#
# <forge (C#)> is the program forge (or `dotnet .../forge.dll`, quoted); <forge-c> the program built from native/forge/forge.c. Prints the match rate per
# part of the corpus and the first differences; the outputs stay in the work folder (default: a temporary one). Exit code 0 when everything matches.
# FRONTEND_MUTANTS=n adds a part "mutants": n damaged copies of every source of the corpus (cut off, a character removed, a piece repeated - the same
# ones every time), which go through the error paths of the lexer, the preprocessor and the parser.
# `markup` (Phase C 5) compares what `forge ui|biscuit|fdata|falstad <file>` writes (both streams: the generated script, the schematic, the
# diagnostics, the warnings) with `forge-c markup --list`: the markup files of the repository (.fxml, .biscuit, .fdata), the markup src/fire.Testing
# writes inline (tests/extract-markup.py: .fxml, .biscuit, .fdata, Falstad's circuits as .txt) and, with FRONTEND_MUTANTS=n, n damaged copies of each
# (the error paths of the XML and JSON readers). The C# forge runs once per file there (it has no --list for these commands).
# `native` (Phase D 1, docs/spec/FORGE_C.md) compares the C of `forge native` (`forge cgen <options> <file>`: the program, then the natives in C++ and the
# units of a split program) for every option set of NATIVE_OPTIONS (default: "default"; words joined by `+`: debug, release, performance, split, strip0-3,
# speed, size, i16, i32, f32, t:<target> - e.g. NATIVE_OPTIONS="default performance split t:esp32+size"). A program the generator in C does not port yet
# is `unported: ...` - counted apart (NATIVE_STRICT=1: as a difference).
# `buildplan` (Phase D 2) compares what a native build would run (`forge buildplan <options> <file>`: the toolchain, the lines of the log before the
# commands, the compiler commands with the folder of the build as <build> and the program as <program>, the key of the program cache and every file of the
# folder of the build with its SHA-256) for every option set of BUILDPLAN_OPTIONS (default: "default tc:gcc+quick"; the words of `native` and tc:<toolchain>,
# quick, quick1, nosplit). `build` builds every BUILD_EVERY-th case of tests/cases (default 25) with both forges for real, with each toolchain of
# BUILD_TOOLCHAINS (default "gcc tcc"; BUILD_FLAGS="--quick" for more switches), without the program cache, and compares the logs (normalized) and what the two
# programs print - and counts the programs of forge-c that print the case's .expected.
# `project` (Phase D 3) compares the model of projects: `forge projectplan <file>` (the workspace, the build order, every member's files, folders, build actions,
# its BuildPlan) for every project and solution file of src/fire.Project, packages, tests and PROJECT_EXTRA (files or folders, read only); `buildplan` of every
# program project (PROJECT_BUILDPLAN_OPTIONS, default "default"); and packs every library project with both forges into the work folder: what they print,
# each forge's `fpk` read of both packages, the zips (tests/fpk-compare.py: everything but the times and the compressed bytes) and the forge copies (json/).
# `projectbuild` builds the program projects of src/fire.Project natively with both forges (BUILD_TOOLCHAINS) - log and program byte-identical - and runs
# `build --all` (PROJECT_ALL_FLAGS, default --engine native) and `publish` on a copy of each solution folder of PROJECT_COPY, comparing what they print.
# `pkglib` (Phase D 2) compares what PackageLibrary would build for the VM (`forge pkglibplan <import>`) for every import with natives of the store.
set -u
if [ $# -lt 2 ]; then echo "usage: $0 <forge (C#)> <forge-c> [tokens|ast|both|diag|fbc|markup|native|buildplan|build|project|projectbuild|pkglib|all] [work folder]" >&2; exit 2; fi
FORGE_CS=$1
FORGE_C=$2
WHAT=${3:-both}
ROOT=$(cd "$(dirname "$0")/.." && pwd)
WORK=${4:-$(mktemp -d)}
mkdir -p "$WORK"

cd "$ROOT"
list() { find "$@" \( -name bin -o -name obj \) -prune -o -name '*.fire' -type f -print | LC_ALL=C sort; }
list tests/cases > "$WORK/cases.lst"
list packages > "$WORK/packages.lst"
list src/fire.Project > "$WORK/project.lst"

parts=(cases packages project)
if [ -n "${FRONTEND_MUTANTS:-}" ]; then
  mkdir -p "$WORK/mutants"
  cat "$WORK/cases.lst" "$WORK/packages.lst" "$WORK/project.lst" | python3 -c '
import random, sys
out, n = sys.argv[1], int(sys.argv[2])
random.seed(7)
names = []
for k, path in enumerate(l.rstrip("\n") for l in sys.stdin):
    data = open(path, encoding="utf-8", errors="replace").read()
    for m in range(n if data else 0):
        i, j, r = random.randrange(len(data)), random.randrange(len(data)), random.random()
        text = data[:i] if r < 0.34 else data[:i] + data[i + 1:] if r < 0.67 else data[:i] + data[j:j + random.randrange(1, 20)] + data[i:]
        name = "%s/m%05d-%d.fire" % (out, k, m)
        open(name, "w", encoding="utf-8").write(text)
        names.append(name)
print("\n".join(names))' "$WORK/mutants" "$FRONTEND_MUTANTS" > "$WORK/mutants.lst"
  parts+=(mutants)
fi

status=0
kinds=(tokens ast)
[ "$WHAT" = tokens ] && kinds=(tokens)
[ "$WHAT" = ast ] && kinds=(ast)
[ "$WHAT" = diag ] && kinds=(diagnostics)
fbcKinds=(); for m in ${FBC_MODES:-debug release performance}; do fbcKinds+=("fbc-$m"); done
[ "$WHAT" = fbc ] && kinds=("${fbcKinds[@]}")
[ "$WHAT" = markup ] && kinds=()
nativeKinds=(); for o in ${NATIVE_OPTIONS:-default}; do nativeKinds+=("cgen-$o"); done
[ "$WHAT" = native ] && kinds=("${nativeKinds[@]}")
planKinds=(); for o in ${BUILDPLAN_OPTIONS:-default tc:gcc+quick}; do planKinds+=("buildplan-$o"); done
[ "$WHAT" = buildplan ] && kinds=("${planKinds[@]}")
[ "$WHAT" = build ] && kinds=()
[ "$WHAT" = project ] && kinds=()
[ "$WHAT" = projectbuild ] && kinds=()

# ---- build (Phase D 2): real native builds of a sample of tests/cases with both forges, their logs and what the programs print ----
if [ "$WHAT" = build ]; then
  every=${BUILD_EVERY:-25}
  sample=$(awk -v e="$every" 'NR % e == 1' "$WORK/cases.lst")
  normalize() { sed -E -e 's#/tmp/fire-native-[0-9a-f]{32}#<build>#g' -e "s#$1#<program>#g" -e 's#taken from the cache \([0-9a-f]{16}\)#taken from the cache (<key>)#'; }
  for tc in ${BUILD_TOOLCHAINS:-gcc tcc}; do
    total=0 sameLog=0 sameRun=0 expected=0 skipped=0 failedBoth=0 unported=0
    diffs=()
    mkdir -p "$WORK/build-$tc"
    n=0
    for file in $sample; do
      n=$((n + 1))
      base=${file%.fire}
      if tail -n 12 "$file" | grep -q -e '^//# skip: forge run' -e '^//# output: varies' || [ -f "$base.error" ] || [ -f "$base.diagnostics" ] || [ ! -f "$base.expected" ]; then skipped=$((skipped + 1)); continue; fi
      total=$((total + 1))
      group=$(basename "$(dirname "$file")")
      modeArgs=(); [ "$group" = native ] && modeArgs=(-m RELEASE)
      d="$WORK/build-$tc/$n"; rm -rf "$d"; mkdir -p "$d"
      FIRE_PROGRAM_CACHE=0 $FORGE_CS build "$ROOT/$file" --engine native -o "$d/p-cs" --toolchain "$tc" "${modeArgs[@]}" ${BUILD_FLAGS:-} 2> "$d/err-cs.txt" | normalize "$d/p-cs" > "$d/log-cs.txt"
      FIRE_PROGRAM_CACHE=0 "$FORGE_C" build "$ROOT/$file" --engine native -o "$d/p-c" --toolchain "$tc" "${modeArgs[@]}" ${BUILD_FLAGS:-} 2> "$d/err-c.txt" | normalize "$d/p-c" > "$d/log-c.txt"
      # (a part of the generator in C that is not ported yet: counted apart)
      if grep -q '^forge-c: unported: ' "$d/err-c.txt"; then unported=$((unported + 1)); continue; fi
      if cmp -s "$d/log-cs.txt" "$d/log-c.txt"; then sameLog=$((sameLog + 1)); else diffs+=("$n:$file (log)"); fi
      if [ ! -x "$d/p-cs" ] && [ ! -x "$d/p-c" ]; then failedBoth=$((failedBoth + 1)); continue; fi
      (cd "$(dirname "$ROOT/$file")" && timeout 60 "$d/p-cs" < /dev/null > "$d/run-cs.txt" 2>&1; echo "exit $?" >> "$d/run-cs.txt")
      (cd "$(dirname "$ROOT/$file")" && timeout 60 "$d/p-c" < /dev/null > "$d/run-c.txt" 2>&1; echo "exit $?" >> "$d/run-c.txt")
      if cmp -s "$d/run-cs.txt" "$d/run-c.txt"; then sameRun=$((sameRun + 1)); else diffs+=("$n:$file (run)"); fi
      head -n -1 "$d/run-c.txt" | cmp -s - "$base.expected" && expected=$((expected + 1))
    done
    echo "build $tc: $total cases ($skipped of the sample not run): the log identical $sameLog, the program prints the same $sameRun, as expected $expected (not ported yet: $unported, both builds failed: $failedBoth)"
    if [ ${#diffs[@]} -gt 0 ]; then
      status=1
      for x in "${diffs[@]:0:5}"; do
        idx=${x%%:*}
        echo "  differs: ${x#*:}"
        case $x in
          *"(log)") diff "$WORK/build-$tc/$idx/log-cs.txt" "$WORK/build-$tc/$idx/log-c.txt" | head -6 | sed 's/^/    /' ;;
          *) diff "$WORK/build-$tc/$idx/run-cs.txt" "$WORK/build-$tc/$idx/run-c.txt" | head -6 | sed 's/^/    /' ;;
        esac
      done
    fi
  done
fi
# ---- project (Phase D 3): the model of projects and solutions, the programs of projects, the packages of libraries ----
if [ "$WHAT" = project ]; then
  # the project and solution files of the repository and those of PROJECT_EXTRA (paths, separated by spaces; read only - packages are written into the work folder)
  find src/fire.Project packages tests \( -name bin -o -name obj \) -prune -o \( -name '*.fireproj' -o -name '*.firesln' \) -type f -print | LC_ALL=C sort > "$WORK/projects.lst"
  for extra in ${PROJECT_EXTRA:-}; do
    case "$extra" in
      *.fireproj|*.firesln) echo "$extra" ;;
      *) find "$extra" \( -name bin -o -name obj -o -name .git \) -prune -o \( -name '*.fireproj' -o -name '*.firesln' \) -type f -print | LC_ALL=C sort ;;
    esac
  done >> "$WORK/projects.lst"
  # projectplan: the workspace, the build order, the files, folders and build actions of every member and its BuildPlan
  mkdir -p "$WORK/projectplan"
  n=0 same=0 diffs=()
  while IFS= read -r file; do
    n=$((n + 1))
    $FORGE_CS projectplan "$file" > "$WORK/projectplan/$n-cs.txt" 2>&1
    "$FORGE_C" projectplan "$file" > "$WORK/projectplan/$n-c.txt" 2>&1
    if cmp -s "$WORK/projectplan/$n-cs.txt" "$WORK/projectplan/$n-c.txt"; then same=$((same + 1)); else diffs+=("$n:$file"); fi
  done < "$WORK/projects.lst"
  echo "projectplan: $same of $n identical"
  for x in "${diffs[@]:0:5}"; do status=1; echo "  differs: ${x#*:}"; diff "$WORK/projectplan/${x%%:*}-cs.txt" "$WORK/projectplan/${x%%:*}-c.txt" | head -6 | sed 's/^/    /'; done
  [ ${#diffs[@]} -gt 0 ] && status=1
  grep '\.fireproj$' "$WORK/projects.lst" | while IFS= read -r file; do
    if grep -q '"type" *: *"library"' "$file"; then echo "$file" >> "$WORK/libraries.lst.tmp"; else echo "$file" >> "$WORK/programs.lst.tmp"; fi
  done
  mv -f "$WORK/libraries.lst.tmp" "$WORK/libraries.lst" 2>/dev/null; mv -f "$WORK/programs.lst.tmp" "$WORK/programs.lst" 2>/dev/null
  touch "$WORK/libraries.lst" "$WORK/programs.lst"
  # buildplan of the program of every program project (the C, the commands, the files of the build: the native build of a project)
  mkdir -p "$WORK/projectbuildplan"
  for o in ${PROJECT_BUILDPLAN_OPTIONS:-default}; do
    n=0 same=0 unported=0 diffs=()
    while IFS= read -r file; do
      n=$((n + 1))
      $FORGE_CS buildplan "$o" "$file" > "$WORK/projectbuildplan/$o-$n-cs.txt" 2>&1
      "$FORGE_C" buildplan "$o" "$file" > "$WORK/projectbuildplan/$o-$n-c.txt" 2>&1
      if cmp -s "$WORK/projectbuildplan/$o-$n-cs.txt" "$WORK/projectbuildplan/$o-$n-c.txt"; then same=$((same + 1))
      elif [ -z "${NATIVE_STRICT:-}" ] && head -c 10 "$WORK/projectbuildplan/$o-$n-c.txt" | grep -q '^unported:'; then unported=$((unported + 1))
      else diffs+=("$n:$file"); fi
    done < "$WORK/programs.lst"
    echo "project buildplan $o: $same of $n identical ($unported not ported yet)"
    for x in "${diffs[@]:0:5}"; do echo "  differs: ${x#*:}"; diff "$WORK/projectbuildplan/$o-${x%%:*}-cs.txt" "$WORK/projectbuildplan/$o-${x%%:*}-c.txt" | head -6 | sed 's/^/    /'; done
    [ ${#diffs[@]} -gt 0 ] && status=1
  done
  # the packages of the libraries: what both forges print, each forge reads both packages (`fpk`), the zips agree in everything but the times and the compressed
  # bytes (tests/fpk-compare.py), the forge copies (json/) are the same
  n=0 same=0 diffs=()
  while IFS= read -r file; do
    n=$((n + 1))
    d="$WORK/pack/$n"; rm -rf "$d"; mkdir -p "$d/cs" "$d/c"
    $FORGE_CS build "$file" -o "$d/cs" > "$d/out-cs.txt" 2> "$d/err-cs.txt"; echo "exit $?" >> "$d/out-cs.txt"
    "$FORGE_C" build "$file" -o "$d/c" > "$d/out-c.txt" 2> "$d/err-c.txt"; echo "exit $?" >> "$d/out-c.txt"
    sed -i "s#$d/cs#<out>#g" "$d/out-cs.txt" "$d/err-cs.txt"; sed -i "s#$d/c#<out>#g" "$d/out-c.txt" "$d/err-c.txt"
    why=""
    cmp -s "$d/out-cs.txt" "$d/out-c.txt" || why="$why stdout"
    cmp -s "$d/err-cs.txt" "$d/err-c.txt" || why="$why stderr"
    pcs=$(ls "$d"/cs/*.fpk 2>/dev/null | head -1); pc=$(ls "$d"/c/*.fpk 2>/dev/null | head -1)
    if [ -n "$pcs" ] && [ -n "$pc" ]; then
      $FORGE_CS fpk "$pcs" > "$d/read-cs-cs.txt"; "$FORGE_C" fpk "$pcs" > "$d/read-c-cs.txt"; $FORGE_CS fpk "$pc" > "$d/read-cs-c.txt"; "$FORGE_C" fpk "$pc" > "$d/read-c-c.txt"
      for r in read-c-cs read-cs-c read-c-c; do cmp -s "$d/read-cs-cs.txt" "$d/$r.txt" || why="$why $r"; done
      python3 -I tests/fpk-compare.py "$pcs" "$pc" > "$d/zip.txt" || why="$why zip"
      sed "s#$d/cs#<out>#g" "$d"/cs/json/*.json > "$d/json-cs.txt"; sed "s#$d/c#<out>#g" "$d"/c/json/*.json > "$d/json-c.txt"
      cmp -s "$d/json-cs.txt" "$d/json-c.txt" || why="$why json"
    elif [ -n "$pcs$pc" ]; then why="$why one-package"; fi
    if [ -z "$why" ]; then same=$((same + 1)); else diffs+=("$n:$file:$why"); fi
  done < "$WORK/libraries.lst"
  echo "pack: $same of $n identical (what is printed, the packages read by both, their zip structure and the forge copies)"
  for x in "${diffs[@]:0:5}"; do status=1; echo "  differs:${x#*:}"; done
fi

# ---- projectbuild (Phase D 3): real native builds of the program projects, `build --all` and `publish` of copies of solutions ----
if [ "$WHAT" = projectbuild ]; then
  find src/fire.Project \( -name bin -o -name obj \) -prune -o -name '*.fireproj' -type f -print | LC_ALL=C sort | while IFS= read -r file; do
    grep -q '"type" *: *"library"' "$file" || echo "$file"
  done > "$WORK/build-programs.lst"
  normalize() { sed -E -e 's#/tmp/fire-native-[0-9a-f]{32}#<build>#g' -e "s#$1#<out>#g"; }
  for tc in ${BUILD_TOOLCHAINS:-gcc tcc}; do
    n=0 sameLog=0 sameProgram=0 unported=0 diffs=()
    while IFS= read -r file; do
      n=$((n + 1))
      d="$WORK/projectbuild-$tc/$n"; rm -rf "$d"; mkdir -p "$d"
      FIRE_PROGRAM_CACHE=0 $FORGE_CS build "$file" --engine native -o "$d/p-cs" --toolchain "$tc" ${BUILD_FLAGS:-} 2> "$d/err-cs.txt" | normalize "$d/p-cs" > "$d/log-cs.txt"
      FIRE_PROGRAM_CACHE=0 "$FORGE_C" build "$file" --engine native -o "$d/p-c" --toolchain "$tc" ${BUILD_FLAGS:-} 2> "$d/err-c.txt" | normalize "$d/p-c" > "$d/log-c.txt"
      if grep -q '^forge-c: unported: ' "$d/err-c.txt"; then unported=$((unported + 1)); continue; fi
      if cmp -s "$d/log-cs.txt" "$d/log-c.txt" && cmp -s "$d/err-cs.txt" "$d/err-c.txt"; then sameLog=$((sameLog + 1)); else diffs+=("$n:$file (log)"); fi
      if cmp -s "$d/p-cs" "$d/p-c"; then sameProgram=$((sameProgram + 1)); else diffs+=("$n:$file (program)"); fi
    done < "$WORK/build-programs.lst"
    echo "projectbuild $tc: $n program projects: the log identical $sameLog, the program byte-identical $sameProgram (not ported yet: $unported)"
    for x in "${diffs[@]:0:5}"; do status=1; echo "  differs: ${x#*:}"; done
  done
  # `build --all` and `publish` of a copy of each solution of PROJECT_COPY (folders; their projects write into their folders: bin/, obj/, publish/)
  for src in ${PROJECT_COPY:-}; do
    name=$(basename "$src")
    for side in cs c; do rm -rf "$WORK/copy-$side/$name"; mkdir -p "$WORK/copy-$side"; cp -a "$src" "$WORK/copy-$side/$name"; done
    sln=$(cd "$WORK/copy-cs/$name" && ls *.firesln | head -1)
    for cmd in "build --all ${PROJECT_ALL_FLAGS:---engine native}" "publish"; do
      tag=${cmd%% *}
      (cd "$WORK/copy-cs/$name" && FIRE_PROGRAM_CACHE=0 $FORGE_CS $cmd "$sln" > "$WORK/$name-$tag-out-cs.txt" 2> "$WORK/$name-$tag-err-cs.txt"; echo "exit $?" >> "$WORK/$name-$tag-out-cs.txt")
      (cd "$WORK/copy-c/$name" && FIRE_PROGRAM_CACHE=0 "$FORGE_C" $cmd "$sln" > "$WORK/$name-$tag-out-c.txt" 2> "$WORK/$name-$tag-err-c.txt"; echo "exit $?" >> "$WORK/$name-$tag-out-c.txt")
      for f in out err; do
        normalize "$WORK/copy-cs/$name" < "$WORK/$name-$tag-$f-cs.txt" | sed -E 's/[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}/<time>/' > "$WORK/$name-$tag-$f-cs.norm"
        normalize "$WORK/copy-c/$name" < "$WORK/$name-$tag-$f-c.txt" | sed -E 's/[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}/<time>/' > "$WORK/$name-$tag-$f-c.norm"
        if cmp -s "$WORK/$name-$tag-$f-cs.norm" "$WORK/$name-$tag-$f-c.norm"; then echo "$name $tag $f: identical"
        else status=1; echo "$name $tag $f: differs"; diff "$WORK/$name-$tag-$f-cs.norm" "$WORK/$name-$tag-$f-c.norm" | head -10 | sed 's/^/    /'; fi
      done
    done
  done
fi

# ---- pkglib (Phase D 2): the libraries of the packages for the VM - `forge pkglibplan <import>` of every import with natives in the store (FIRE_PACKAGES, else
# Packages/ next to the C# forge) and of `graphics+windows`: the file, the keys (NativeHashOf, the runtime's fingerprint, RuntimeKey), the toolchain, the command,
# the files of the folder of the build ----
if [ "$WHAT" = pkglib ]; then
  kinds=()
  store=${FIRE_PACKAGES:-$(dirname "$(command -v "${FORGE_CS%% *}" || echo "$FORGE_CS")")/Packages}
  python3 -I -c '
import json, os, sys
root = sys.argv[1]
for d in sorted(os.listdir(root)):
    p = os.path.join(root, d, "package.json")
    if os.path.exists(p):
        for im in json.load(open(p, encoding="utf-8-sig")).get("imports", []):
            if "native" in im: print(im["name"])
print("graphics+windows")' "$store" > "$WORK/pkglib.lst"
  total=0 same=0
  mkdir -p "$WORK/pkglib"
  while IFS= read -r name; do
    total=$((total + 1))
    $FORGE_CS pkglibplan "$name" > "$WORK/pkglib/$name-cs.txt" 2>&1
    "$FORGE_C" pkglibplan "$name" > "$WORK/pkglib/$name-c.txt" 2>&1
    if cmp -s "$WORK/pkglib/$name-cs.txt" "$WORK/pkglib/$name-c.txt"; then same=$((same + 1))
    else status=1; echo "  differs: $name"; diff "$WORK/pkglib/$name-cs.txt" "$WORK/pkglib/$name-c.txt" | head -6 | sed 's/^/    /'; fi
  done < "$WORK/pkglib.lst"
  echo "pkglib: $same of $total identical"
fi
[ "$WHAT" = all ] && kinds=(tokens ast diagnostics "${fbcKinds[@]}")

# ---- markup (Phase C 5) ----
if [ "$WHAT" = markup ] || [ "$WHAT" = all ]; then
  find . \( -name .git -o -name .claude -o -name bin -o -name obj \) -prune -o \( -name '*.fxml' -o -name '*.biscuit' -o -name '*.fdata' \) -type f -print \
    | LC_ALL=C sort > "$WORK/markup-files.lst"
  rm -rf "$WORK/markup-inline"
  python3 -I tests/extract-markup.py "$WORK/markup-inline" > "$WORK/markup-inline.lst"
  markupParts=(files inline)
  if [ -n "${FRONTEND_MUTANTS:-}" ]; then
    rm -rf "$WORK/markup-mutants"
    cat "$WORK/markup-files.lst" "$WORK/markup-inline.lst" | python3 -c '
import os, random, sys
out, n = sys.argv[1], int(sys.argv[2])
random.seed(7)
names = []
for k, path in enumerate(l.rstrip("\n") for l in sys.stdin):
    data = open(path, encoding="utf-8", errors="replace").read()
    for m in range(n if data else 0):
        i, j, r = random.randrange(len(data)), random.randrange(len(data)), random.random()
        text = data[:i] if r < 0.34 else data[:i] + data[i + 1:] if r < 0.67 else data[:i] + data[j:j + random.randrange(1, 20)] + data[i:]
        d = "%s/m%05d-%d" % (out, k, m)
        os.makedirs(d, exist_ok=True)
        name = os.path.join(d, os.path.basename(path))
        open(name, "w", encoding="utf-8").write(text)
        names.append(name)
print("\n".join(names))' "$WORK/markup-mutants" "$FRONTEND_MUTANTS" > "$WORK/markup-mutants.lst"
    markupParts+=(mutants)
  fi
  for part in "${markupParts[@]}"; do
    lst="$WORK/markup-$part.lst"
    total=$(wc -l < "$lst")
    if [ -z "${FORGE_CS_CACHE:-}" ] || [ ! -d "$WORK/markup-$part-cs" ]; then
      rm -rf "$WORK/markup-$part-cs"
      mkdir -p "$WORK/markup-$part-cs"
      n=0
      while IFS= read -r file; do
        n=$((n + 1))
        case "$file" in *.fxml) cmd=ui ;; *.biscuit) cmd=biscuit ;; *.fdata) cmd=fdata ;; *) cmd=falstad ;; esac
        $FORGE_CS $cmd "$file" > "$WORK/markup-$part-cs/$n.txt" 2>&1
      done < "$lst"
    fi
    rm -rf "$WORK/markup-$part-c"
    "$FORGE_C" markup --list "$lst" --out "$WORK/markup-$part-c" > /dev/null
    same=0
    diffs=()
    n=0
    while IFS= read -r file; do
      n=$((n + 1))
      if cmp -s "$WORK/markup-$part-cs/$n.txt" "$WORK/markup-$part-c/$n.txt"; then same=$((same + 1)); else diffs+=("$n:$file"); fi
    done < "$lst"
    echo "markup $part: $same of $total identical"
    if [ $same -ne $total ]; then
      status=1
      for d in "${diffs[@]:0:5}"; do
        idx=${d%%:*}
        echo "  differs: ${d#*:}"
        diff "$WORK/markup-$part-cs/$idx.txt" "$WORK/markup-$part-c/$idx.txt" | head -6 | sed 's/^/    /'
      done
    fi
  done
fi
for kind in "${kinds[@]}"; do
  for part in "${parts[@]}"; do
    lst="$WORK/$part.lst"
    total=$(wc -l < "$lst")
    if [ -z "${FORGE_CS_CACHE:-}" ] || [ ! -d "$WORK/$kind-$part-cs" ]; then
      rm -rf "$WORK/$kind-$part-cs"
      # (`fbc-<mode>` is the command `fbc <mode>`, `cgen-<options>` `cgen <options>`)
      $FORGE_CS ${kind/-/ } --list "$lst" --out "$WORK/$kind-$part-cs" > /dev/null
    fi
    rm -rf "$WORK/$kind-$part-c"
    "$FORGE_C" ${kind/-/ } --list "$lst" --out "$WORK/$kind-$part-c" > /dev/null
    same=0
    unported=0
    diffs=()
    n=0
    while IFS= read -r file; do
      n=$((n + 1))
      if cmp -s "$WORK/$kind-$part-cs/$n.txt" "$WORK/$kind-$part-c/$n.txt"; then same=$((same + 1))
      elif [ -z "${NATIVE_STRICT:-}" ] && head -c 10 "$WORK/$kind-$part-c/$n.txt" 2>/dev/null | grep -q '^unported:'; then unported=$((unported + 1))
      else diffs+=("$n:$file"); fi
    done < "$lst"
    if [ $unported -gt 0 ]; then echo "$kind $part: $same of $total identical ($unported not ported yet)"; else echo "$kind $part: $same of $total identical"; fi
    if [ ${#diffs[@]} -gt 0 ] || { [ $same -ne $total ] && [ -n "${NATIVE_STRICT:-}" ]; }; then
      status=1
      for d in "${diffs[@]:0:5}"; do
        idx=${d%%:*}
        echo "  differs: ${d#*:}"
        case $kind in
          fbc-*) cmp "$WORK/$kind-$part-cs/$idx.txt" "$WORK/$kind-$part-c/$idx.txt" | sed 's/^/    /' ;;   # (bytes: `forge dump` shows a program file as text)
          *) diff "$WORK/$kind-$part-cs/$idx.txt" "$WORK/$kind-$part-c/$idx.txt" | head -6 | sed 's/^/    /' ;;
        esac
      done
    fi
  done
done
echo "outputs: $WORK"
exit $status
