"""Compares two .fpk files structurally: every field of the local headers, the central directory and the end record, except the times (DOS time and
date: when the package was made) and the compressed bytes with their size (the deflaters differ: zlib-ng in .NET, packages/compression in forge-c).
The uncompressed contents are compared too (through zipfile). Exit 0 when they agree."""
import struct, sys, zipfile


def parse(path):
    data = open(path, "rb").read()
    end = data.rfind(b"PK\x05\x06")
    eocd = struct.unpack("<IHHHHIIH", data[end:end + 22])
    count, cd = eocd[3], eocd[6]
    entries = []
    at = cd
    for _ in range(count):
        (sig, made, need, flags, method, time, date, crc, csize, usize, nlen, xlen, clen, disk, iattr, eattr, off) = struct.unpack("<IHHHHHHIIIHHHHHII", data[at:at + 46])
        name = data[at + 46:at + 46 + nlen]
        extra = data[at + 46 + nlen:at + 46 + nlen + xlen]
        comment = data[at + 46 + nlen + xlen:at + 46 + nlen + xlen + clen]
        local = struct.unpack("<IHHHHHIIIHH", data[off:off + 30])
        lname = data[off + 30:off + 30 + local[9]]
        lextra = data[off + 30 + local[9]:off + 30 + local[9] + local[10]]
        entries.append({
            "central": (sig, made, need, flags, method, crc, usize, name, extra, comment, disk, iattr, eattr),
            "local": (local[0], local[1], local[2], local[3], local[6], local[8], lname, lextra),  # (not the time, the date, the compressed size)
            "localsizes": (local[6], local[8]),  # crc, usize
        })
        at += 46 + nlen + xlen + clen
    return {"count": count, "disks": eocd[1:3], "entries": entries}


def main():
    a, b = parse(sys.argv[1]), parse(sys.argv[2])
    ok = True
    if a["count"] != b["count"] or a["disks"] != b["disks"]:
        print("  the end records differ", a["count"], b["count"]); ok = False
    for x, y in zip(a["entries"], b["entries"]):
        if x != y:
            print("  entry differs:", x["central"][7], x, y); ok = False
    za, zb = zipfile.ZipFile(sys.argv[1]), zipfile.ZipFile(sys.argv[2])
    for ia, ib in zip(za.infolist(), zb.infolist()):
        if za.read(ia) != zb.read(ib):
            print("  content differs:", ia.filename); ok = False
    sys.exit(0 if ok else 1)


main()
