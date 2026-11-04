#!/usr/bin/env python3
"""Writes the screenshot programs of the documentation into docs/images/src/ (then ./make.sh runs them).

A picture in a page that stands right before an example makes it from that example:

* ![text](../images/NAME.png) followed by a ```fire block: the block becomes src/NAME.fire;
* ![text](../images/NAME.png) followed by an ```xml block whose first line is <!-- File.fxml --> and then a ```fire block (the code-behind):
  the markup becomes src/NAME/File.fxml, the code-behind src/NAME.fire (its `#include "File.fxml"` points there).

The program's `x.Run()` (x a UI.Root or a window made from markup) becomes "a few cycles, then save the picture". An HTML comment right after the
last block, <!-- shot: code -->, adds code that runs before the picture is taken (to type a text, click a button, select an item); several lines are
separated by `;;`. Programs in src/ that no page makes (hand-written ones) are left alone."""
import os, re, sys

here = os.path.dirname(os.path.abspath(__file__))
docs = os.path.dirname(here)
src = os.path.join(here, "src")
pattern = re.compile(
    r"!\[[^\]]*\]\((?:\.\./)*images/([\w.-]+)\.png\)\s*\n\s*\n?"
    r"(?:```xml\n\s*<!--\s*([\w.-]+\.fxml)\s*-->\n(.*?)```\s*\n\s*\n?)?"
    r"```fire\n(.*?)```(\s*<!--\s*shot:(.*?)-->)?", re.S)
written = 0
for folder, _, files in os.walk(docs):
    for f in files:
        if not f.endswith(".md"):
            continue
        page = os.path.relpath(os.path.join(folder, f), docs)
        text = open(os.path.join(folder, f), encoding="utf-8").read()
        for m in pattern.finditer(text):
            name, fxml_name, fxml, code, extra = m.group(1), m.group(2), m.group(3), m.group(4), (m.group(6) or "").strip()
            run = re.search(r"^(\s*)(\w+)\.Run\(\)\s*(//.*)?$", code, re.M)
            if not run:
                print(f"{page}: {name} has no `.Run()` - skipped", file=sys.stderr)
                continue
            before = "".join(run.group(1) + line.strip() + "\n" for line in extra.split(";;") if line.strip())
            code = code[:run.start()] + before + f'{run.group(1)}Shot.Save({run.group(2)}, "{name}.png")' + code[run.end():]
            if fxml_name:
                os.makedirs(os.path.join(src, name), exist_ok=True)
                open(os.path.join(src, name, fxml_name), "w", encoding="utf-8").write(fxml)
                code = code.replace(f'#include "{fxml_name}"', f'#include "{name}/{fxml_name}"')
            lines = code.split("\n")
            imports = [i for i, l in enumerate(lines) if l.startswith("#import") or l.startswith("#include")]
            lines.insert(imports[-1] + 1 if imports else 0, '#include "shot.fire"')
            header = f"// Made from docs/{page} by extract.py - edit the page, not this file.\n"
            open(os.path.join(src, name + ".fire"), "w", encoding="utf-8").write(header + "\n".join(lines))
            written += 1
print(f"{written} programs written")
