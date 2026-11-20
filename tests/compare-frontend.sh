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
set -u
if [ $# -lt 2 ]; then echo "usage: $0 <forge (C#)> <forge-c> [tokens|ast|both|diag|fbc|markup|native|all] [work folder]" >&2; exit 2; fi
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
