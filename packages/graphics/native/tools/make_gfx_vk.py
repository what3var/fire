#!/usr/bin/env python3
"""Writes ../fire_gfx_vk.h and ../../../scene3d/native/s3d_vk.h - one and the same file: the part of the Khronos Vulkan header (vulkan_core.h) that the Vulkan backends
of fire use (graphics: ../fire_gfx_gpu.h, scene3d: s3d_gpu_vk.h) - the types, the
constants and the function pointer types it names, and what they need - so that building fire needs no Vulkan SDK. The declarations are taken word for word from the
header; enums keep only the values the backend names.

    python3 make_gfx_vk.py [path/to/vulkan_core.h]        (default: /usr/include/vulkan/vulkan_core.h)

Both packages get the same declarations under one include guard (FIRE_VK_DECLS_H): a native build puts the sources of both into one file - the second copy is
skipped there - and the library of each package for the VM has its own. Run it after a backend uses something new (scene3d's tools/make_s3d_vk.py runs this one)."""
import os
import re
import sys

here = os.path.dirname(os.path.abspath(__file__))
core_path = sys.argv[1] if len(sys.argv) > 1 else "/usr/include/vulkan/vulkan_core.h"
core = open(core_path).read()
scene3d = os.path.join(here, "..", "..", "..", "scene3d", "native")
backend = open(os.path.join(here, "..", "fire_gfx_gpu.h")).read() + "\n" + open(os.path.join(scene3d, "s3d_gpu_vk.h")).read()

# ---- what the header declares ----
structs = {}      # name -> (kind, body text, position)
for m in re.finditer(r"typedef (struct|union) (Vk\w+) \{(.*?)\} \2;", core, re.S):
    structs[m.group(2)] = (m.group(1), m.group(3), m.start())
enums = {}        # name -> (list of (constant, value text), position)
for m in re.finditer(r"typedef enum (Vk\w+) \{(.*?)\} \1;", core, re.S):
    items = []
    for line in m.group(2).split("\n"):
        mm = re.match(r"\s*(VK_\w+) = ([^,]+),?\s*$", line)
        if mm:
            items.append((mm.group(1), mm.group(2).strip()))
    enums[m.group(1)] = (items, m.start())
flags = dict((m.group(2), m.group(1)) for m in re.finditer(r"typedef (VkFlags|VkFlags64) (Vk\w+);", core))
handles = dict((m.group(2), m.group(1)) for m in re.finditer(r"(VK_DEFINE_HANDLE|VK_DEFINE_NON_DISPATCHABLE_HANDLE)\((Vk\w+)\)", core))
pfns = dict((m.group(1), m.group(0)) for m in re.finditer(r"typedef [^;]*?\(VKAPI_PTR \*(PFN_vk\w+)\)\([^;]*?\);", core, re.S))
defines = dict((m.group(1), m.group(0)) for m in re.finditer(r"#define (VK_[A-Z0-9_]+)[ \t]+[^\n]+", core))
const_enum = {}
for name, (items, _) in enums.items():
    for c, _v in items:
        const_enum.setdefault(c, name)

# ---- what the backend names ----
used_constants = set(re.findall(r"\bVK_[A-Z0-9_]+\b", backend))
# a type that the backend only uses through a pointer needs only to be declared
used_types = set(m.group(1) for m in re.finditer(r"\b(Vk[A-Z]\w+)\b(?!\s*\*)", backend))
used_pointer_types = set(re.findall(r"\b(Vk[A-Z]\w+)\b\s*\*", backend)) - used_types
used_functions = set(re.findall(r"X\((vk\w+)\)", backend)) | {"vkGetInstanceProcAddr", "vkCreateInstance", "vkEnumerateInstanceExtensionProperties"}

need_struct, need_decl, need_enum, need_flags, need_handle, need_pfn, need_define = set(), set(), {}, set(), set(), set(), set()

def need_type(t, by_value):
    if t in structs:
        need_decl.add(t)
        if by_value and t not in need_struct:
            need_struct.add(t)
            for line in structs[t][1].split(";"):
                line = line.strip()
                if not line:
                    continue
                mm = re.match(r"(?:const\s+)?(\w+)\s*(\*?)", line)
                if mm:
                    need_type(mm.group(1), mm.group(2) == "")
                for d in re.findall(r"\[(VK_\w+)\]", line):
                    need_define.add(d)
    elif t in enums:
        need_enum.setdefault(t, set())
    elif t in flags:
        need_flags.add(t)
    elif t in handles:
        need_handle.add(t)
    elif t.startswith("PFN_"):
        need_pfn.add(t)

for c in used_constants:
    if c in const_enum:
        need_enum.setdefault(const_enum[c], set()).add(c)
    elif c in defines:
        need_define.add(c)
for t in used_types:
    need_type(t, True)
for t in used_pointer_types:
    need_type(t, False)
for f in used_functions:
    p = "PFN_" + f
    if p not in pfns:
        sys.exit("unknown function " + f)
    need_pfn.add(p)
done_pfn = set()
while need_pfn - done_pfn:
    for p in sorted(need_pfn - done_pfn):
        done_pfn.add(p)
        for t, star in re.findall(r"(?:const\s+)?\b(Vk\w+)\b\s*(\*?)", pfns[p].split("(VKAPI_PTR")[1]):
            need_type(t, star == "")
        r = re.match(r"typedef (\w+)", pfns[p]).group(1)
        need_type(r, True)
# an enum constant that is defined by another one (an alias) needs that one, too
for e, cs in need_enum.items():
    values = dict(enums[e][0])
    for c in list(cs):
        v = values[c]
        if v in values:
            cs.add(v)
# VK_MAKE_VERSION and the other macros the defines use
for d in list(need_define):
    for x in re.findall(r"\bVK_[A-Z0-9_]+\b", defines[d]):
        if x in defines:
            need_define.add(x)

# ---- writing ----
out = []
w = out.append
w("/* fire: the part of the Vulkan API that the Vulkan backends use (graphics: fire_gfx_gpu.h, scene3d: s3d_gpu_vk.h). GENERATED by graphics/native/tools/make_gfx_vk.py from the")
w(" * Khronos header vulkan_core.h - do not edit. graphics/native/fire_gfx_vk.h and scene3d/native/s3d_vk.h are the same file under one include guard (a native build has both).")
w(" * The declarations are taken word for word from that header (enums keep only the values the backend names); Vulkan itself is loaded at run time.")
w(" *")
w(" * vulkan_core.h: Copyright 2015-2024 The Khronos Group Inc. Licensed under the Apache License, Version 2.0 (http://www.apache.org/licenses/LICENSE-2.0); this file is")
w(" * a derivative of it (selected declarations). */")
w("#ifndef FIRE_VK_DECLS_H")
w("#define FIRE_VK_DECLS_H")
w("#include <stddef.h>")
w("#include <stdint.h>")
w("")
w("#if defined(_WIN32)")
w("#define VKAPI_ATTR")
w("#define VKAPI_CALL __stdcall")
w("#define VKAPI_PTR VKAPI_CALL")
w("#elif defined(__ANDROID__) && defined(__ARM_ARCH) && __ARM_ARCH < 7")
w("#error \"Vulkan is not supported for the 'armeabi' NDK ABI\"")
w("#elif defined(__ANDROID__) && defined(__ARM_ARCH) && __ARM_ARCH >= 7 && defined(__ARM_32BIT_STATE)")
w("#define VKAPI_ATTR __attribute__((pcs(\"aapcs-vfp\")))")
w("#define VKAPI_CALL")
w("#define VKAPI_PTR VKAPI_ATTR")
w("#else")
w("#define VKAPI_ATTR")
w("#define VKAPI_CALL")
w("#define VKAPI_PTR")
w("#endif")
w("")
w("typedef uint32_t VkBool32;")
w("typedef uint64_t VkDeviceAddress;")
w("typedef uint64_t VkDeviceSize;")
w("typedef uint32_t VkFlags;")
w("typedef uint32_t VkSampleMask;")
w("")
for d in sorted(need_define, key=lambda d: core.index(defines[d])):
    w(defines[d] if not defines[d].endswith("\\") else re.search(r"#define " + d + r"\b.*?[^\\]\n", core, re.S).group(0).rstrip())
w("")
w("#define VK_DEFINE_HANDLE(object) typedef struct object##_T* object;")
w("#if defined(__LP64__) || defined(_WIN64) || (defined(__x86_64__) && !defined(__ILP32__)) || defined(_M_X64) || defined(__ia64) || defined(_M_IA64) || defined(__aarch64__) || defined(__powerpc64__) || (defined(__riscv) && __riscv_xlen == 64)")
w("#define VK_DEFINE_NON_DISPATCHABLE_HANDLE(object) typedef struct object##_T *object;")
w("#else")
w("#define VK_DEFINE_NON_DISPATCHABLE_HANDLE(object) typedef uint64_t object;")
w("#endif")
for h in sorted(need_handle, key=lambda h: core.index("(" + h + ")")):
    w("%s(%s)" % (handles[h], h))
w("")
for e in sorted(need_enum, key=lambda e: enums[e][1]):
    items = enums[e][0]
    keep = need_enum[e]
    w("typedef enum %s {" % e)
    for c, v in items:
        if c in keep or "_MAX_ENUM" in c:
            w("    %s = %s," % (c, v))
    w("} %s;" % e)
w("")
for f in sorted(need_flags, key=lambda f: core.index(" " + f + ";")):
    w("typedef %s %s;" % (flags[f], f))
w("")
# every struct is declared first (a pointer to one needs no more), the ones that are used by value are defined in the order of the header
for s in sorted(need_decl, key=lambda s: structs[s][2]):
    w("typedef %s %s %s;" % (structs[s][0], s, s))
w("")
for s in sorted(need_struct, key=lambda s: structs[s][2]):
    w("%s %s {%s};" % (structs[s][0], s, structs[s][1]))
    w("")
for p in sorted(need_pfn, key=lambda p: core.index(pfns[p])):
    w(re.sub(r"\s+", " ", pfns[p]))
w("")
w("#endif /* FIRE_VK_DECLS_H */")
w("")
for target in (os.path.join(here, "..", "fire_gfx_vk.h"), os.path.join(scene3d, "s3d_vk.h")):
    open(target, "w").write("\n".join(out))
print("wrote fire_gfx_vk.h and s3d_vk.h: %d structs, %d enums, %d function types" % (len(need_struct), len(need_enum), len(need_pfn)))
