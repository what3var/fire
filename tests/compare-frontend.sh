#!/usr/bin/env bash
# The frontend in C against the one in C# (docs/spec/FORGE_C.md, docs/ROADMAP_C_PORT.md Phase C 1/2): `tokens` and `ast` of every source of the corpus -
# tests/cases/**/*.fire, packages/**/*.fire, the samples and templates in src/fire.Project - must be byte-identical.
#
#   tests/compare-frontend.sh <forge (C#)> <forge-c> [tokens|ast|both] [work folder]
#
# <forge (C#)> is the program forge (or `dotnet .../forge.dll`, quoted); <forge-c> the program built from native/forge/forge.c. Prints the match rate per
# part of the corpus and the first differences; the outputs stay in the work folder (default: a temporary one). Exit code 0 when everything matches.
set -u
if [ $# -lt 2 ]; then echo "usage: $0 <forge (C#)> <forge-c> [tokens|ast|both] [work folder]" >&2; exit 2; fi
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

status=0
kinds=(tokens ast)
[ "$WHAT" = tokens ] && kinds=(tokens)
[ "$WHAT" = ast ] && kinds=(ast)
for kind in "${kinds[@]}"; do
  for part in cases packages project; do
    lst="$WORK/$part.lst"
    total=$(wc -l < "$lst")
    rm -rf "$WORK/$kind-$part-cs" "$WORK/$kind-$part-c"
    $FORGE_CS "$kind" --list "$lst" --out "$WORK/$kind-$part-cs" > /dev/null
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
