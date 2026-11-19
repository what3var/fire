#!/usr/bin/env bash
# The frontend in C against the one in C# (docs/spec/FORGE_C.md, docs/ROADMAP_C_PORT.md Phase C 1-3): `tokens`, `ast` and `diagnostics` of every source of
# the corpus - tests/cases/**/*.fire, packages/**/*.fire, the samples and templates in src/fire.Project - must be byte-identical.
#
#   tests/compare-frontend.sh <forge (C#)> <forge-c> [tokens|ast|both|diag|all] [work folder]
#
# `both` is tokens and ast; `diag` compares `diagnostics` (what forge prints while it compiles a source up to the type check: the resolver, the type
# checker, the preludes of its imports); `all` is all three. FORGE_CS_CACHE=1 keeps the outputs of the C# forge that are in the work folder already.
#
# <forge (C#)> is the program forge (or `dotnet .../forge.dll`, quoted); <forge-c> the program built from native/forge/forge.c. Prints the match rate per
# part of the corpus and the first differences; the outputs stay in the work folder (default: a temporary one). Exit code 0 when everything matches.
# FRONTEND_MUTANTS=n adds a part "mutants": n damaged copies of every source of the corpus (cut off, a character removed, a piece repeated - the same
# ones every time), which go through the error paths of the lexer, the preprocessor and the parser.
set -u
if [ $# -lt 2 ]; then echo "usage: $0 <forge (C#)> <forge-c> [tokens|ast|both|diag|all] [work folder]" >&2; exit 2; fi
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
[ "$WHAT" = all ] && kinds=(tokens ast diagnostics)
for kind in "${kinds[@]}"; do
  for part in "${parts[@]}"; do
    lst="$WORK/$part.lst"
    total=$(wc -l < "$lst")
    if [ -z "${FORGE_CS_CACHE:-}" ] || [ ! -d "$WORK/$kind-$part-cs" ]; then
      rm -rf "$WORK/$kind-$part-cs"
      $FORGE_CS "$kind" --list "$lst" --out "$WORK/$kind-$part-cs" > /dev/null
    fi
    rm -rf "$WORK/$kind-$part-c"
    "$FORGE_C" "$kind" --list "$lst" --out "$WORK/$kind-$part-c" > /dev/null
    same=0
    diffs=()
    n=0
    while IFS= read -r file; do
      n=$((n + 1))
      if cmp -s "$WORK/$kind-$part-cs/$n.txt" "$WORK/$kind-$part-c/$n.txt"; then same=$((same + 1)); else diffs+=("$n:$file"); fi
    done < "$lst"
    echo "$kind $part: $same of $total identical"
    if [ $same -ne $total ]; then
      status=1
      for d in "${diffs[@]:0:5}"; do
        idx=${d%%:*}
        echo "  differs: ${d#*:}"
        diff "$WORK/$kind-$part-cs/$idx.txt" "$WORK/$kind-$part-c/$idx.txt" | head -6 | sed 's/^/    /'
      done
    fi
  done
done
echo "outputs: $WORK"
exit $status
