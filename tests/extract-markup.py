#!/usr/bin/env python3
"""The markup that src/fire.Testing writes inline (string literals given to MarkupParser, FireUiGenerator, BiscuitGenerator, DataModelGenerator,
FalstadImport, MarkupFiles), as files - for tests/compare-frontend.sh markup (docs/spec/FORGE_C.md, Phase C 5).

    tests/extract-markup.py <folder>      writes <folder>/<n>/Snippet.fxml|.biscuit|.fdata|.txt and prints the paths

A literal is a C# string ("...", @"...", raw \"\"\"...\"\"\") or a concatenation of them with `+` (constants of the same file are resolved); what is
markup is decided by its text: `<cir` and `$ ` are Falstad's circuits, `<` the user interface, a JSON object with "entities"/"enums"/"types" a data
model, one with "elements"/"ports"/"biscuit" a schematic. Interpolated strings are left out. The same text is written once."""
import os
import re
import sys

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")


def read_regular(src, i, verbatim):
    """A "..." (or @"...") literal starting at the quote; returns (text, end)."""
    out = []
    i += 1
    while i < len(src):
        ch = src[i]
        if verbatim:
            if ch == '"':
                if i + 1 < len(src) and src[i + 1] == '"':
                    out.append('"')
                    i += 2
                    continue
                return "".join(out), i + 1
            out.append(ch)
            i += 1
            continue
        if ch == '"':
            return "".join(out), i + 1
        if ch == "\\":
            e = src[i + 1]
            simple = {"n": "\n", "t": "\t", "r": "\r", "0": "\0", "\\": "\\", '"': '"', "'": "'", "a": "\a", "b": "\b", "f": "\f", "v": "\v", "e": "\x1b"}
            if e in simple:
                out.append(simple[e])
                i += 2
                continue
            if e == "u":
                out.append(chr(int(src[i + 2:i + 6], 16)))
                i += 6
                continue
            if e == "x":
                m = re.match(r"[0-9A-Fa-f]{1,4}", src[i + 2:])
                out.append(chr(int(m.group(0), 16)))
                i += 2 + len(m.group(0))
                continue
            out.append(e)
            i += 2
            continue
        out.append(ch)
        i += 1
    return None, i


def read_raw(src, i):
    """A raw literal \"\"\"...\"\"\" (three or more quotes) starting at the first quote; returns (text, end)."""
    n = 0
    while src[i + n] == '"':
        n += 1
    delim = '"' * n
    end = src.find(delim, i + n)
    body = src[i + n:end]
    if "\n" not in body:
        return body, end + n
    lines = body.split("\n")
    # the first line (after the opening quotes) is empty; the last holds the indentation of the closing quotes
    indent = lines[-1]
    content = lines[1:-1]
    text = "\n".join(l[len(indent):] if l.startswith(indent) else l.lstrip() for l in content)
    return text, end + n


def constants(src):
    """`const string name = "...";` of a file (single literals)."""
    found = {}
    for m in re.finditer(r'const\s+string\s+([A-Za-z_]\w*)\s*=\s*"((?:[^"\\]|\\.)*)"\s*;', src):
        text, _ = read_regular('"' + m.group(2) + '"', 0, False)
        found[m.group(1)] = text
    return found


def kind(text):
    t = text.lstrip()
    if t.startswith("<cir") or t.startswith("$ "):
        return "txt"
    if t.startswith("<"):
        return "fxml" if ("Window" in t or "View" in t or "Class" in t) else None
    if t.startswith("{"):
        if re.search(r'"(entities|enums|types)"', t):
            return "fdata"
        if re.search(r'"(elements|ports|biscuit)"', t):
            return "biscuit"
    return None


def main():
    out = sys.argv[1]
    os.makedirs(out, exist_ok=True)
    folder = os.path.join(ROOT, "src", "fire.Testing")
    seen = set()
    n = 0
    for name in sorted(os.listdir(folder)):
        if not name.endswith(".cs"):
            continue
        src = open(os.path.join(folder, name), encoding="utf-8").read()
        if not re.search(r"Markup|Biscuit|Falstad|DataModel|fxml|fdata", src):
            continue
        consts = constants(src)
        for expr in literals_iter(src, consts):
            text = "".join(expr)
            k = kind(text)
            if not k or text in seen:
                continue
            seen.add(text)
            n += 1
            d = os.path.join(out, "%04d" % n)
            os.makedirs(d, exist_ok=True)
            path = os.path.join(d, "Snippet." + k)
            with open(path, "w", encoding="utf-8", newline="") as f:
                f.write(text)
            print(path)


def literals_iter(src, consts):
    """The string expressions of a C# file: lists of parts (literals and constants joined by +)."""
    i = 0
    cur = None
    expect_plus = False
    while i < len(src):
        ch = src[i]
        if src.startswith("//", i):
            j = src.find("\n", i)
            i = len(src) if j < 0 else j
            continue
        if src.startswith("/*", i):
            i = src.find("*/", i) + 2
            continue
        if ch == "'":
            j = i + 1
            if src[j] == "\\":
                j += 1
            j = src.find("'", j + 1)
            i = j + 1
            if cur is not None:
                yield cur
            cur, expect_plus = None, False
            continue
        if ch == '"' or (ch in "@$" and i + 1 < len(src) and src[i + 1] in '"@$'):
            interpolated = False
            verbatim = False
            j = i
            while src[j] in "@$":
                interpolated |= src[j] == "$"
                verbatim |= src[j] == "@"
                j += 1
            if src.startswith('"""', j):
                text, end = read_raw(src, j)
            else:
                text, end = read_regular(src, j, verbatim)
            i = end
            if text is None or interpolated:
                if cur is not None:
                    yield cur
                cur, expect_plus = None, False
                continue
            if cur is not None and not expect_plus:
                cur.append(text)
            else:
                if cur is not None:
                    yield cur
                cur = [text]
            expect_plus = True
            continue
        if ch.isspace():
            i += 1
            continue
        if ch == "+" and cur is not None and expect_plus:
            expect_plus = False
            i += 1
            continue
        m = re.match(r"[A-Za-z_][A-Za-z0-9_]*", src[i:])
        if m and m.group(0) in consts and (cur is None or not expect_plus):
            # a constant: starts or continues a concatenation
            if cur is None:
                cur = [consts[m.group(0)]]
            else:
                cur.append(consts[m.group(0)])
            expect_plus = True
            i += len(m.group(0))
            continue
        if cur is not None:
            yield cur
        cur, expect_plus = None, False
        i += len(m.group(0)) if m else 1
    if cur is not None:
        yield cur


if __name__ == "__main__":
    main()
