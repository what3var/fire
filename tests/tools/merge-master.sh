#!/usr/bin/env bash
# Merges master into this branch when master still holds the cases "native == VM" as text in src/fire.Testing/Program.cs (docs/tools/testing.md
# "The cases as files"): the cases master added or changed go into tests/cases/native (tests/tools/cases.py), the rest of Program.cs is merged as usual.
#
#   tests/tools/merge-master.sh [<branch>]        (default: origin/master; the work tree must be clean)
#
# Afterwards: resolve what else conflicts, build, record the expected output of the new and changed cases and look at it
# (FIRE_TESTS_ONLY=native FIRE_TESTS_UPDATE=1), then commit the merge.
set -eu
cd "$(git rev-parse --show-toplevel)"
theirs=${1:-origin/master}
file=src/fire.Testing/Program.cs
[ -z "$(git status --porcelain)" ] || { echo "The work tree is not clean."; exit 1; }
base=$(git merge-base HEAD "$theirs")
tmp=$(mktemp -d); trap 'rm -rf "$tmp"' EXIT
git show "$base:$file" > "$tmp/base.cs"
git show "$theirs:$file" > "$tmp/theirs.cs"
git show "HEAD:$file" > "$tmp/ours.cs"
python3 tests/tools/cases.py strip "$tmp/base.cs" "$tmp/base.stripped.cs"
git merge --no-commit --no-ff "$theirs" || true
# Program.cs: ours against master without its literal cases; the cases of master into the files
python3 tests/tools/cases.py strip "$tmp/theirs.cs" "$tmp/theirs.stripped.cs" tests/cases/native
if git merge-file -p "$tmp/ours.cs" "$tmp/base.stripped.cs" "$tmp/theirs.stripped.cs" > "$tmp/merged.cs"; then
    cp "$tmp/merged.cs" "$file"; git add "$file" tests/cases/native
    echo "Program.cs merged."
else
    cp "$tmp/merged.cs" "$file"; git add tests/cases/native
    echo "Program.cs has conflicts outside the cases: resolve them, then git add $file."
fi
git status --short | grep -v '^[AM]  ' || echo "Nothing else conflicts."
