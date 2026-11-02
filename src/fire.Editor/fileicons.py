"""Builds fileicons.res from fileicons.rc, like `rc /fo fileicons.res fileicons.rc` does - for when no Windows SDK is at hand.

Only the subset of the .rc this project uses: `#define NAME number` and `NAME ICON "file.ico"`.
Usage (in src/fire.Editor):   python fileicons.py
"""
import os, re, struct

HERE = os.path.dirname(os.path.abspath(__file__))
RT_ICON, RT_GROUP_ICON = 3, 14
FLAGS = 0x1030  # MOVEABLE | PURE | DISCARDABLE


def entry(rtype, rid, data):
    header = struct.pack('<HHHH', 0xFFFF, rtype, 0xFFFF, rid) + struct.pack('<IHHII', 0, FLAGS, 0, 0, 0)
    blob = struct.pack('<II', len(data), 8 + len(header)) + header + data
    return blob + b'\0' * (-len(blob) % 4)


def main():
    rc = open(os.path.join(HERE, 'fileicons.rc'), encoding='utf-8').read()
    defines = {m[0]: int(m[1]) for m in re.findall(r'^\s*#define\s+(\w+)\s+(\d+)', rc, re.M)}
    icons = [(defines.get(m[0]) or int(m[0]), m[1].replace('\\\\', os.sep))
             for m in re.findall(r'^\s*(\w+)\s+ICON\s+"([^"]+)"', rc, re.M)]
    out = struct.pack('<II', 0, 32) + struct.pack('<HHHH', 0xFFFF, 0, 0xFFFF, 0) + b'\0' * 16  # the empty first entry of every .res
    next_id = 1
    for group_id, file in icons:
        ico = open(os.path.join(HERE, file), 'rb').read()
        count = struct.unpack_from('<H', ico, 4)[0]
        group = struct.pack('<HHH', 0, 1, count)
        for i in range(count):
            w, h, colors, reserved, planes, bits, size, offset = struct.unpack_from('<BBBBHHII', ico, 6 + 16 * i)
            out += entry(RT_ICON, next_id, ico[offset:offset + size])
            group += struct.pack('<BBBBHHIH', w, h, colors, reserved, planes, bits, size, next_id)
            next_id += 1
        out += entry(RT_GROUP_ICON, group_id, group)
    open(os.path.join(HERE, 'fileicons.res'), 'wb').write(out)
    print(f'fileicons.res: {len(icons)} icons, {next_id - 1} images')


if __name__ == '__main__':
    main()
