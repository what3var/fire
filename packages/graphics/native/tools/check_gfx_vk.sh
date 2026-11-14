#!/bin/sh
# Checks ../fire_gfx_vk.h against the Khronos header: the size and the place of every member of every struct it defines must be the same, and the enum values it keeps
# must have the same numbers. Needs a C compiler and the Vulkan headers (the Vulkan SDK, or the package libvulkan-dev).
set -e
cd "$(dirname "$0")"
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
python3 - "$tmp/probe.c" <<'PY'
import re, sys
src = open("../fire_gfx_vk.h").read()
lines = ['#include <stdio.h>', '#include <stddef.h>', '#ifdef REAL', '#include <vulkan/vulkan_core.h>', '#else', '#include "fire_gfx_vk.h"', '#endif', 'int main(void) {']
for kind, name, body in re.findall(r"^(struct|union) (Vk\w+) \{(.*?)\};", src, re.S | re.M):
    lines.append('    printf("%s %%zu\\n", sizeof(%s));' % (name, name))
    for member in re.findall(r"(\w+)(?:\[[^\]]*\])*\s*;", body):
        lines.append('    printf("%s.%s %%zu\\n", offsetof(%s, %s));' % (name, member, name, member))
for name in re.findall(r"^\s+(VK_\w+) = [^,]+,", src, re.M):
    lines.append('    printf("%s %%lld\\n", (long long)%s);' % (name, name))
lines.append('    return 0;\n}')
open(sys.argv[1], "w").write("\n".join(lines) + "\n")
PY
cc -std=c11 -I.. -o "$tmp/mine" "$tmp/probe.c"
cc -std=c11 -DREAL -o "$tmp/real" "$tmp/probe.c"
"$tmp/mine" > "$tmp/mine.txt"
"$tmp/real" > "$tmp/real.txt"
if diff "$tmp/real.txt" "$tmp/mine.txt"; then echo "fire_gfx_vk.h matches the Vulkan header ($(wc -l < "$tmp/mine.txt") sizes, offsets and values)"; else echo "fire_gfx_vk.h differs from the Vulkan header" >&2; exit 1; fi
