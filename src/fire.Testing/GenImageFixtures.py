# Erzeugt src/fire.Testing/ImageFixtures.cs: die Testbilder (PNG/BMP/GIF) als Base64.
#
#   pip install pillow
#   python3 GenImageFixtures.py [ausgabedatei]      (Vorgabe: ImageFixtures.cs neben diesem Skript)
#
# Alle Bilder sind 13x7 Pixel gross und folgen denselben Formeln (rgb/alpha/pal_entry/pal_index unten), die auch die Pruefungen in
# Program.cs ("Bilder: PNG, BMP, GIF") verwenden. "pil_*" schreibt Pillow (ein echter Encoder), die uebrigen ein eigener Schreiber:
# PNG in allen Farbarten/Tiefen, Verschraenkung und mit allen fuenf Zeilenfiltern, BMP mit 1/4/16/32 Bit, Bitmasken, RLE und OS/2-Kopfzeile.
import sys, struct, zlib, io, base64
from PIL import Image

W, H = 13, 7

def rgb(x, y):
    return ((x*19+3) % 256, (y*35+5) % 256, ((x*7+y*13)*3) % 256)
def alpha(x, y):
    return (x*37 + y*91) % 256
def pal_entry(i):
    return ((i*40+10) % 256, (i*70+20) % 256, (i*110+30) % 256)
def pal_index(x, y, n):
    return (x*5 + y*3) % n

out = {}

# ---------------- PNG: eigener Encoder (alle Farbarten/Tiefen, Verschraenkung, alle Filter) ----------------
def chunk(t, data):
    c = struct.pack('>I', len(data)) + t + data
    return c + struct.pack('>I', zlib.crc32(t + data) & 0xffffffff)

def pack_row(samples, depth):
    if depth == 8:
        return bytes(samples)
    if depth == 16:
        return b''.join(struct.pack('>H', s) for s in samples)
    bits = ''.join(format(s, '0%db' % depth) for s in samples)
    bits += '0' * (-len(bits) % 8)
    return bytes(int(bits[i:i+8], 2) for i in range(0, len(bits), 8))

def apply_filter(f, cur, prev, bpp):
    res = bytearray()
    for i in range(len(cur)):
        a = cur[i-bpp] if i >= bpp else 0
        b = prev[i]
        c = prev[i-bpp] if i >= bpp else 0
        if f == 0: p = 0
        elif f == 1: p = a
        elif f == 2: p = b
        elif f == 3: p = (a + b) // 2
        else:
            pp = a + b - c
            pa, pb, pc = abs(pp-a), abs(pp-b), abs(pp-c)
            p = a if pa <= pb and pa <= pc else (b if pb <= pc else c)
        res.append((cur[i] - p) & 255)
    return bytes(res)

ADAM = [(0,0,8,8),(4,0,8,8),(0,4,4,8),(2,0,4,4),(0,2,2,4),(1,0,2,2),(0,1,1,2)]

def png(ct, depth, sample, w=W, h=H, plte=None, trns=None, interlace=False, filter_cycle=True):
    channels = {0:1, 2:3, 3:1, 4:2, 6:4}[ct]
    bpp = max(1, channels*depth//8)
    raw = bytearray()
    passes = [(0,0,1,1)] if not interlace else ADAM
    for (x0, y0, dx, dy) in passes:
        xs = list(range(x0, w, dx)); ys = list(range(y0, h, dy))
        if not xs or not ys: continue
        prev = bytes(len(pack_row([0]*(len(xs)*channels), depth)))
        for r, y in enumerate(ys):
            samples = []
            for x in xs: samples.extend(sample(x, y))
            cur = pack_row(samples, depth)
            f = (r % 5) if filter_cycle else 0
            raw.append(f)
            raw.extend(apply_filter(f, cur, prev, bpp))
            prev = cur
    data = b'\x89PNG\r\n\x1a\n'
    data += chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, depth, ct, 0, 0, 1 if interlace else 0))
    if plte: data += chunk(b'PLTE', bytes(plte))
    if trns is not None: data += chunk(b'tRNS', bytes(trns))
    # IDAT in zwei Teile, damit das Zusammensetzen mehrerer Chunks getestet wird
    z = zlib.compress(bytes(raw), 9)
    half = len(z)//2
    data += chunk(b'IDAT', z[:half]) + chunk(b'IDAT', z[half:])
    data += chunk(b'IEND', b'')
    return data

def scale(v, depth):  # 8-Bit-Wert auf die Tiefe
    return v >> (8 - depth) if depth < 8 else (v if depth == 8 else v * 257)

out['png_rgba8'] = png(6, 8, lambda x, y: (*rgb(x, y), alpha(x, y)))
out['png_rgb8'] = png(2, 8, lambda x, y: rgb(x, y))
out['png_rgba16'] = png(6, 16, lambda x, y: tuple(c*257 for c in (*rgb(x, y), alpha(x, y))))
out['png_rgb16'] = png(2, 16, lambda x, y: tuple(c*257 for c in rgb(x, y)))
out['png_gray8'] = png(0, 8, lambda x, y: (rgb(x, y)[0],))
out['png_gray16'] = png(0, 16, lambda x, y: (rgb(x, y)[0]*257,))
out['png_graya8'] = png(4, 8, lambda x, y: (rgb(x, y)[0], alpha(x, y)))
out['png_graya16'] = png(4, 16, lambda x, y: (rgb(x, y)[0]*257, alpha(x, y)*257))
for d in (1, 2, 4):
    mx = (1 << d) - 1
    out['png_gray%d' % d] = png(0, d, lambda x, y, mx=mx: ((x*3 + y) % (mx+1),))
for d, n in ((1, 2), (2, 4), (4, 16), (8, 11)):
    plte = [c for i in range(n) for c in pal_entry(i)]
    out['png_pal%d' % d] = png(3, d, lambda x, y, n=n: (pal_index(x, y, n),), plte=plte)
# Palette mit Transparenz (Eintrag 1 durchsichtig, Eintrag 2 halb)
plte = [c for i in range(6) for c in pal_entry(i)]
out['png_pal8_trns'] = png(3, 8, lambda x, y: (pal_index(x, y, 6),), plte=plte, trns=[255, 0, 128])
# Schluesselfarben
out['png_gray8_key'] = png(0, 8, lambda x, y: (rgb(x, y)[0],), trns=struct.pack('>H', rgb(0, 0)[0]))
out['png_rgb8_key'] = png(2, 8, lambda x, y: rgb(x, y), trns=struct.pack('>HHH', *rgb(0, 0)))
# Verschraenkt
out['png_rgba8_adam7'] = png(6, 8, lambda x, y: (*rgb(x, y), alpha(x, y)), interlace=True)
out['png_gray4_adam7'] = png(0, 4, lambda x, y: ((x*3 + y) % 16,), interlace=True)
out['png_pal2_adam7'] = png(3, 2, lambda x, y: (pal_index(x, y, 4),), plte=[c for i in range(4) for c in pal_entry(i)], interlace=True)
out['png_rgb16_adam7'] = png(2, 16, lambda x, y: tuple(c*257 for c in rgb(x, y)), interlace=True)
# sehr kleine Bilder (leere Adam7-Durchgaenge)
out['png_rgb8_1x1_adam7'] = png(2, 8, lambda x, y: rgb(x + 5, y + 5), w=1, h=1, interlace=True)
out['png_rgb8_3x2_adam7'] = png(2, 8, lambda x, y: rgb(x, y), w=3, h=2, interlace=True)

# ---------------- PNG: Pillow (echter Encoder) ----------------
def pil_png(im, **kw):
    b = io.BytesIO(); im.save(b, 'PNG', **kw); return b.getvalue()
im = Image.new('RGBA', (W, H))
for y in range(H):
    for x in range(W): im.putpixel((x, y), (*rgb(x, y), alpha(x, y)))
out['pil_png_rgba'] = pil_png(im)
out['pil_png_rgba_optimized'] = pil_png(im, optimize=True, compress_level=9)
im = Image.new('RGB', (W, H))
for y in range(H):
    for x in range(W): im.putpixel((x, y), rgb(x, y))
out['pil_png_rgb'] = pil_png(im)
imp = Image.new('P', (W, H))
imp.putpalette([c for i in range(11) for c in pal_entry(i)])
for y in range(H):
    for x in range(W): imp.putpixel((x, y), pal_index(x, y, 11))
out['pil_png_pal'] = pil_png(imp)
out['pil_png_pal_trns'] = pil_png(imp, transparency=3)
out['pil_png_pal_bits4'] = pil_png(imp, bits=4)
iml = Image.new('L', (W, H))
for y in range(H):
    for x in range(W): iml.putpixel((x, y), rgb(x, y)[0])
out['pil_png_gray'] = pil_png(iml)
im1 = Image.new('1', (W, H))
for y in range(H):
    for x in range(W): im1.putpixel((x, y), 255 if (x + y) % 3 == 0 else 0)
out['pil_png_bilevel'] = pil_png(im1)

# ---------------- BMP: Pillow ----------------
def pil_bmp(im):
    b = io.BytesIO(); im.save(b, 'BMP'); return b.getvalue()
out['pil_bmp_pal8'] = pil_bmp(imp)
out['pil_bmp_rgb24'] = pil_bmp(Image.merge('RGB', Image.open(io.BytesIO(out['pil_png_rgb'])).split()))
ima = Image.open(io.BytesIO(out['pil_png_rgba']))
out['pil_bmp_rgba32'] = pil_bmp(ima)
out['pil_bmp_bilevel'] = pil_bmp(im1)
out['pil_bmp_gray8'] = pil_bmp(iml)

# ---------------- BMP: eigener Schreiber ----------------
def bmp(w, h, bpp, rows, palette=None, compression=0, topdown=False, core=False, masks=None, pixel_data=None, colors_used=None):
    # rows: Liste von Zeilen (von oben nach unten) als bytes, bereits auf 4 Byte aufgefuellt; oder pixel_data
    pal = b''
    if palette:
        pal = b''.join((struct.pack('BBBB', b, g, r, 0) if not core else struct.pack('BBB', b, g, r)) for (r, g, b) in palette)
    if core:
        hdr = struct.pack('<IHHHH', 12, w, h, 1, bpp)
    else:
        hdr = struct.pack('<IiiHHIIiiII', 40, w, -h if topdown else h, 1, bpp, compression, 0, 2835, 2835,
                          colors_used if colors_used is not None else (len(palette) if palette else 0), 0)
    extra = b''
    if masks:
        extra = b''.join(struct.pack('<I', m) for m in masks)
    if pixel_data is None:
        order = rows if topdown else list(reversed(rows))
        pixel_data = b''.join(order)
    off = 14 + len(hdr) + len(extra) + len(pal)
    return b'BM' + struct.pack('<IHHI', off + len(pixel_data), 0, 0, off) + hdr + extra + pal + pixel_data

def pad4(b):
    return b + bytes(-len(b) % 4)

def idx_rows(depth, n):
    rows = []
    for y in range(H):
        samples = [pal_index(x, y, n) for x in range(W)]
        rows.append(pad4(pack_row(samples, depth)))
    return rows

pal16 = [pal_entry(i) for i in range(16)]
out['bmp_pal4'] = bmp(W, H, 4, idx_rows(4, 16), palette=pal16)
out['bmp_pal4_topdown'] = bmp(W, H, 4, idx_rows(4, 16), palette=pal16, topdown=True)
out['bmp_pal1'] = bmp(W, H, 1, idx_rows(1, 2), palette=[pal_entry(0), pal_entry(1)])
out['bmp_pal8_partial'] = bmp(W, H, 8, idx_rows(8, 5), palette=[pal_entry(i) for i in range(5)], colors_used=5)
rows24 = [pad4(b''.join(bytes([rgb(x, y)[2], rgb(x, y)[1], rgb(x, y)[0]]) for x in range(W))) for y in range(H)]
out['bmp_rgb24_topdown'] = bmp(W, H, 24, rows24, topdown=True)
rows32 = [b''.join(bytes([rgb(x, y)[2], rgb(x, y)[1], rgb(x, y)[0], alpha(x, y)]) for x in range(W)) for y in range(H)]
out['bmp_rgb32_alpha'] = bmp(W, H, 32, rows32)
rows32z = [b''.join(bytes([rgb(x, y)[2], rgb(x, y)[1], rgb(x, y)[0], 0]) for x in range(W)) for y in range(H)]
out['bmp_rgb32_noalpha'] = bmp(W, H, 32, rows32z)
# 16 Bit: 5-5-5 (BI_RGB) und 5-6-5 (BITFIELDS)
def c555(x, y):
    r, g, b = rgb(x, y); return ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3)
def c565(x, y):
    r, g, b = rgb(x, y); return ((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3)
out['bmp_rgb555'] = bmp(W, H, 16, [pad4(b''.join(struct.pack('<H', c555(x, y)) for x in range(W))) for y in range(H)])
out['bmp_rgb565'] = bmp(W, H, 16, [pad4(b''.join(struct.pack('<H', c565(x, y)) for x in range(W))) for y in range(H)],
                        compression=3, masks=[0xF800, 0x07E0, 0x001F])
# 32 Bit mit Bitmasken und Alpha (BI_ALPHABITFIELDS = 6)
out['bmp_rgba32_masks'] = bmp(W, H, 32, rows32, compression=6, masks=[0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000])
# OS/2 (12-Byte-Kopfzeile): 24 Bit und 8 Bit
out['bmp_os2_rgb24'] = bmp(W, H, 24, rows24, core=True)
out['bmp_os2_pal8'] = bmp(W, H, 8, idx_rows(8, 7), palette=[pal_entry(i) for i in range(256)], core=True)

# RLE8: jede Zeile als Folge aus Wiederholung und absolutem Lauf, Delta und Zeilenende
def rle8_encode(indices_rows_top_to_bottom):
    data = bytearray()
    for row in reversed(indices_rows_top_to_bottom):   # RLE beginnt unten
        x = 0
        # absoluter Lauf (>=3) fuer die ersten 5 Pixel, dann Wiederholungen
        head = row[:5]
        data += bytes([0, len(head)]) + bytes(head)
        if len(head) % 2: data += b'\x00'
        rest = row[5:]
        i = 0
        while i < len(rest):
            j = i
            while j < len(rest) and rest[j] == rest[i] and j - i < 255: j += 1
            data += bytes([j - i, rest[i]])
            i = j
        data += b'\x00\x00'
    data += b'\x00\x01'
    return bytes(data)
idx_rows_plain = [[pal_index(x, y, 9) for x in range(W)] for y in range(H)]
out['bmp_rle8'] = bmp(W, H, 8, None, palette=[pal_entry(i) for i in range(9)], compression=1, pixel_data=rle8_encode(idx_rows_plain))

def rle4_encode(rows):
    data = bytearray()
    for row in reversed(rows):
        # absoluter Lauf der ersten 6 Pixel (3 Bytes, aufgefuellt auf gerade Anzahl -> 4 Bytes), dann Wiederholung je Pixelpaar-Wert
        head = row[:6]
        nibbles = head
        packed = bytes((nibbles[i] << 4) | (nibbles[i+1] if i+1 < len(nibbles) else 0) for i in range(0, len(nibbles), 2))
        data += bytes([0, len(head)]) + packed
        if len(packed) % 2: data += b'\x00'
        rest = row[6:]
        i = 0
        while i < len(rest):
            # Wiederholung zweier abwechselnder Nibbles (a,b) so lange wie moeglich
            a = rest[i]; b = rest[i+1] if i+1 < len(rest) else 0
            n = 2 if i+1 < len(rest) else 1
            data += bytes([n, (a << 4) | b])
            i += n
        data += b'\x00\x00'
    data += b'\x00\x01'
    return bytes(data)
idx_rows16 = [[pal_index(x, y, 16) for x in range(W)] for y in range(H)]
out['bmp_rle4'] = bmp(W, H, 4, None, palette=pal16, compression=2, pixel_data=rle4_encode(idx_rows16))

# ---------------- GIF: Pillow ----------------
def pil_gif(frames, **kw):
    b = io.BytesIO()
    frames[0].save(b, 'GIF', save_all=len(frames) > 1, append_images=frames[1:], **kw)
    return b.getvalue()
def pal_image(n, shift=0):
    p = Image.new('P', (W, H))
    p.putpalette([c for i in range(n) for c in pal_entry(i + shift)])
    for y in range(H):
        for x in range(W): p.putpixel((x, y), pal_index(x, y, n))
    return p
out['pil_gif_pal'] = pil_gif([pal_image(11)], interlace=False)
out['pil_gif_interlaced'] = pil_gif([pal_image(11)], interlace=True)
out['pil_gif_trans'] = pil_gif([pal_image(11)], transparency=2, interlace=False)
out['pil_gif_anim'] = pil_gif([pal_image(11), pal_image(11, 3)], duration=100, loop=0)
big = Image.new('P', (40, 30)); big.putpalette([c for i in range(64) for c in pal_entry(i)])
for y in range(30):
    for x in range(40): big.putpixel((x, y), (x*7 + y*11 + (x*y)) % 64)
out['pil_gif_big'] = pil_gif([big], interlace=True)

# C#-Quelltext
lines = ['// Erzeugt von GenImageFixtures.py (Pillow und eigene Schreiber) - nicht von Hand bearbeiten.',
         'static class ImageFixtures', '{', '    public static byte[] Get(string name) => System.Convert.FromBase64String(Data[name]);', '',
         '    public static readonly System.Collections.Generic.Dictionary<string, string> Data = new()', '    {']
for k in sorted(out):
    lines.append('        ["%s"] = "%s",' % (k, base64.b64encode(out[k]).decode()))
lines += ['    };', '}']
import os
out_path = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), 'ImageFixtures.cs')
open(out_path, 'w').write('\n'.join(lines) + '\n')
for k in sorted(out):
    print(k, len(out[k]))
