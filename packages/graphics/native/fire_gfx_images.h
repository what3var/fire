/* fire native graphics: image decoders - PNG (all color types and depths, interlaced, with its own inflate), BMP (1-32 bits, RLE, bit masks) and GIF (the first image).
 * A port of src/fire.Terminal/Images; the error messages are the same. No exceptions: a decoder returns 0 and says why (`err`, FIRE_GFX_ERR_SIZE bytes). */
#ifndef FIRE_GFX_IMAGES_H
#define FIRE_GFX_IMAGES_H

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "fire_gfx.h"

#define FIRE_GFX_ERR_SIZE 512

/* What a decoder makes: palette pixels (indices + palette) or true color pixels. */
typedef struct FireGfxImage {
    int width, height;
    int indexed;
    uint8_t* indices;      /* width*height (indexed) */
    uint32_t palette[256];
    int transparentIndex;
    uint32_t* pixels;      /* width*height (true color) */
    const char* format;
} FireGfxImage;

static void fire_gfx_imageInit(FireGfxImage* im) {
    memset(im, 0, sizeof *im);
    im->transparentIndex = -1;
    im->format = "";
}
static void fire_gfx_imageFree(FireGfxImage* im) { free(im->indices); free(im->pixels); im->indices = NULL; im->pixels = NULL; }
static void fire_gfx_imagePaletteBlack(FireGfxImage* im) { int i; for (i = 0; i < 256; i++) im->palette[i] = 0xFF000000u; }
static size_t fire_gfx_imageCount(const FireGfxImage* im) { return (size_t)im->width * (size_t)im->height; }

/* The framebuffer of this image; `mode` -1: like the image. */
static int fire_gfx_imageToFramebuffer(const FireGfxImage* im, int mode, FireGfxFramebuffer* fb) {
    int target = mode >= 0 ? mode : (im->indexed ? FIRE_GFX_INDEXED : FIRE_GFX_RGBA);
    size_t i, n = fire_gfx_imageCount(im);
    FireGfxCachedIndex* cache;
    if (!fire_gfx_fbCreate(fb, im->width, im->height, target)) return 0;
    if (target == FIRE_GFX_RGBA) {
        if (!im->indexed) memcpy(fb->pixels, im->pixels, n * 4);
        else for (i = 0; i < n; i++) fb->pixels[i] = im->palette[im->indices[i]];
        return 1;
    }
    if (im->indexed) {
        memcpy(fb->indices, im->indices, n);
        fire_gfx_paletteSetAll(&fb->palette, im->palette, 256);
        fb->transparentIndex = im->transparentIndex;
        return 1;
    }
    /* true color into a palette framebuffer: the nearest entry of the default palette (without alpha), remembered per color */
    cache = (FireGfxCachedIndex*)calloc(4096, sizeof(FireGfxCachedIndex));
    if (cache == NULL) return 0;
    for (i = 0; i < n; i++) {
        uint32_t c = im->pixels[i] & 0x00FFFFFFu;
        FireGfxCachedIndex* slot = &cache[(c * 2654435761u) >> 20];
        if (!slot->valid || slot->color != c) { slot->color = c; slot->index = fire_gfx_paletteNearest(&fb->palette, c | 0xFF000000u); slot->valid = 1; }
        fb->indices[i] = slot->index;
    }
    free(cache);
    return 1;
}

#define FIRE_GFX_IMAGE_MAX_PIXELS ((int64_t)64 * 1024 * 1024)

static int fire_gfx_checkSize(const char* format, int64_t w, int64_t h, char* err) {
    if (w <= 0 || h <= 0) { snprintf(err, FIRE_GFX_ERR_SIZE, "%s: invalid image size %lldx%lld.", format, (long long)w, (long long)h); return 0; }
    if (w * h > FIRE_GFX_IMAGE_MAX_PIXELS) {
        snprintf(err, FIRE_GFX_ERR_SIZE, "%s: image too large (%lldx%lld; at most %lld million pixels).", format, (long long)w, (long long)h, (long long)(FIRE_GFX_IMAGE_MAX_PIXELS / (1024 * 1024)));
        return 0;
    }
    return 1;
}

#define FIRE_IMG_FAIL(message) do { snprintf(err, FIRE_GFX_ERR_SIZE, "%s", (message)); return 0; } while (0)

/* ---- inflate (RFC 1951) inside a zlib stream (RFC 1950) ------------------------------------------------------------------------------------------- */
typedef struct FireGfxBits { const uint8_t* in; size_t n, pos; uint32_t buf; int count; int truncated; } FireGfxBits;
static int fire_gfx_bitsGet(FireGfxBits* b, int need) {
    int v;
    while (b->count < need) {
        if (b->pos >= b->n) { b->truncated = 1; return 0; }
        b->buf |= (uint32_t)b->in[b->pos++] << b->count;
        b->count += 8;
    }
    v = (int)(b->buf & ((1u << need) - 1));
    b->buf >>= need;
    b->count -= need;
    return v;
}

typedef struct FireGfxHuffman { uint16_t counts[16], symbols[288]; } FireGfxHuffman;
static int fire_gfx_huffmanBuild(FireGfxHuffman* h, const uint8_t* lengths, int n) {
    int i, len, left = 1;
    uint16_t offsets[16];
    memset(h->counts, 0, sizeof h->counts);
    for (i = 0; i < n; i++) h->counts[lengths[i]]++;
    h->counts[0] = 0;
    for (len = 1; len < 16; len++) { left <<= 1; left -= h->counts[len]; if (left < 0) return 0; }
    offsets[1] = 0;
    for (len = 1; len < 15; len++) offsets[len + 1] = (uint16_t)(offsets[len] + h->counts[len]);
    for (i = 0; i < n; i++) if (lengths[i]) h->symbols[offsets[lengths[i]]++] = (uint16_t)i;
    return 1;
}
static int fire_gfx_huffmanDecode(const FireGfxHuffman* h, FireGfxBits* b) {
    int code = 0, first = 0, index = 0, len;
    for (len = 1; len < 16; len++) {
        int count;
        code |= fire_gfx_bitsGet(b, 1);
        if (b->truncated) return -1;
        count = h->counts[len];
        if (code - count < first) return h->symbols[index + (code - first)];
        index += count; first += count; first <<= 1; code <<= 1;
    }
    return -2;   /* no such code */
}

/* 0: `outSize` bytes were produced; 1: the data is invalid; 2: the data ended too soon. */
static int fire_gfx_zlibInflate(const uint8_t* in, size_t n, uint8_t* out, size_t outSize) {
    static const uint16_t lenBase[29] = {3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258};
    static const uint8_t lenExtra[29] = {0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0};
    static const uint16_t distBase[30] = {1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577};
    static const uint8_t distExtra[30] = {0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13};
    FireGfxBits b;
    size_t o = 0;
    int last = 0;
    if (n < 2) return 2;
    if ((in[0] & 0x0F) != 8 || ((in[0] << 8) | in[1]) % 31 != 0 || (in[1] & 0x20)) return 1;
    b.in = in + 2; b.n = n - 2; b.pos = 0; b.buf = 0; b.count = 0; b.truncated = 0;
    while (!last && o < outSize) {
        int type;
        FireGfxHuffman lit, dist;
        last = fire_gfx_bitsGet(&b, 1) != 0;
        type = fire_gfx_bitsGet(&b, 2);
        if (b.truncated) return 2;
        if (type == 0) {
            unsigned len, nlen, i;
            b.buf = 0; b.count = 0;   /* stored block: to the next byte */
            if (b.pos + 4 > b.n) return 2;
            len = b.in[b.pos] | (b.in[b.pos + 1] << 8); nlen = b.in[b.pos + 2] | (b.in[b.pos + 3] << 8);
            b.pos += 4;
            if ((len ^ 0xFFFF) != nlen) return 1;
            if (b.pos + len > b.n) { size_t have = b.n - b.pos, k; for (k = 0; k < have && o < outSize; k++) out[o++] = b.in[b.pos + k]; return o >= outSize ? 0 : 2; }
            for (i = 0; i < len && o < outSize; i++) out[o++] = b.in[b.pos + i];
            b.pos += len;
            continue;
        }
        if (type == 3) return 1;
        if (type == 1) {
            uint8_t lengths[288], dl[30];
            int i;
            for (i = 0; i < 144; i++) lengths[i] = 8;
            for (i = 144; i < 256; i++) lengths[i] = 9;
            for (i = 256; i < 280; i++) lengths[i] = 7;
            for (i = 280; i < 288; i++) lengths[i] = 8;
            fire_gfx_huffmanBuild(&lit, lengths, 288);
            for (i = 0; i < 30; i++) dl[i] = 5;
            fire_gfx_huffmanBuild(&dist, dl, 30);
        } else {
            static const uint8_t order[19] = {16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15};
            int nlen = fire_gfx_bitsGet(&b, 5) + 257, ndist = fire_gfx_bitsGet(&b, 5) + 1, ncode = fire_gfx_bitsGet(&b, 4) + 4;
            uint8_t lengths[320], all[320];
            FireGfxHuffman code;
            int i, index = 0;
            if (b.truncated) return 2;
            if (nlen > 286 || ndist > 30) return 1;
            memset(lengths, 0, sizeof lengths);
            for (i = 0; i < ncode; i++) lengths[order[i]] = (uint8_t)fire_gfx_bitsGet(&b, 3);
            if (b.truncated) return 2;
            if (!fire_gfx_huffmanBuild(&code, lengths, 19)) return 1;
            memset(all, 0, sizeof all);
            while (index < nlen + ndist) {
                int sym = fire_gfx_huffmanDecode(&code, &b);
                if (sym == -1) return 2;
                if (sym < 0) return 1;
                if (sym < 16) all[index++] = (uint8_t)sym;
                else {
                    int prev = 0, rep;
                    if (sym == 16) { if (index == 0) return 1; prev = all[index - 1]; rep = 3 + fire_gfx_bitsGet(&b, 2); }
                    else if (sym == 17) rep = 3 + fire_gfx_bitsGet(&b, 3);
                    else rep = 11 + fire_gfx_bitsGet(&b, 7);
                    if (b.truncated) return 2;
                    if (index + rep > nlen + ndist) return 1;
                    while (rep--) all[index++] = (uint8_t)prev;
                }
            }
            if (!fire_gfx_huffmanBuild(&lit, all, nlen)) return 1;
            if (!fire_gfx_huffmanBuild(&dist, all + nlen, ndist)) return 1;
        }
        for (;;) {
            int sym = fire_gfx_huffmanDecode(&lit, &b), len, ds, i;
            size_t d;
            if (sym == -1) return 2;
            if (sym < 0) return 1;
            if (sym < 256) { if (o >= outSize) return 0; out[o++] = (uint8_t)sym; if (o >= outSize) return 0; continue; }
            if (sym == 256) break;
            sym -= 257;
            if (sym >= 29) return 1;
            len = lenBase[sym] + fire_gfx_bitsGet(&b, lenExtra[sym]);
            ds = fire_gfx_huffmanDecode(&dist, &b);
            if (ds == -1 || b.truncated) return 2;
            if (ds < 0 || ds >= 30) return 1;
            d = distBase[ds] + (size_t)fire_gfx_bitsGet(&b, distExtra[ds]);
            if (b.truncated) return 2;
            if (d > o) return 1;
            for (i = 0; i < len && o < outSize; i++, o++) out[o] = out[o - d];
            if (o >= outSize) return 0;
        }
    }
    return o >= outSize ? 0 : 2;
}

/* ---- PNG --------------------------------------------------------------------------------------------------------------------------------------------- */
static uint32_t fire_gfx_crc(const uint8_t* p, size_t n) {
    static uint32_t table[256];
    static int ready = 0;
    uint32_t c = 0xFFFFFFFFu;
    size_t i;
    if (!ready) {
        uint32_t k;
        for (k = 0; k < 256; k++) { uint32_t v = k; int j; for (j = 0; j < 8; j++) v = (v & 1) ? 0xEDB88320u ^ (v >> 1) : v >> 1; table[k] = v; }
        ready = 1;
    }
    for (i = 0; i < n; i++) c = table[(c ^ p[i]) & 0xFF] ^ (c >> 8);
    return c ^ 0xFFFFFFFFu;
}
static uint32_t fire_gfx_be32(const uint8_t* d, size_t p) { return ((uint32_t)d[p] << 24) | ((uint32_t)d[p + 1] << 16) | ((uint32_t)d[p + 2] << 8) | d[p + 3]; }

static const int g_fireGfxPassX[7] = {0, 4, 0, 2, 0, 1, 0}, g_fireGfxPassY[7] = {0, 0, 4, 0, 2, 0, 1}, g_fireGfxPassDx[7] = {8, 8, 4, 4, 2, 2, 1}, g_fireGfxPassDy[7] = {8, 8, 8, 4, 4, 2, 2};
static int fire_gfx_passWidth(int w, int pass) { return w > g_fireGfxPassX[pass] ? (w - g_fireGfxPassX[pass] + g_fireGfxPassDx[pass] - 1) / g_fireGfxPassDx[pass] : 0; }
static int fire_gfx_passHeight(int h, int pass) { return h > g_fireGfxPassY[pass] ? (h - g_fireGfxPassY[pass] + g_fireGfxPassDy[pass] - 1) / g_fireGfxPassDy[pass] : 0; }

static uint32_t fire_gfx_sample(const uint8_t* row, int index, int bitDepth) {
    switch (bitDepth) {
        case 8: return row[index];
        case 16: return (uint32_t)(row[index * 2] << 8 | row[index * 2 + 1]);
        default: {
            int bitPos = index * bitDepth;
            int shift = 8 - bitDepth - (bitPos & 7);
            return (uint32_t)((row[bitPos >> 3] >> shift) & ((1 << bitDepth) - 1));
        }
    }
}
static uint32_t fire_gfx_scale(uint32_t v, int bitDepth) {
    switch (bitDepth) { case 8: return v; case 16: return v >> 8; case 1: return v * 255; case 2: return v * 85; default: return v * 17; }
}

static int fire_gfx_unfilter(int filter, uint8_t* cur, const uint8_t* prior, size_t n, int bpp, char* err) {
    size_t i;
    switch (filter) {
        case 0: break;
        case 1: for (i = (size_t)bpp; i < n; i++) cur[i] = (uint8_t)(cur[i] + cur[i - bpp]); break;
        case 2: for (i = 0; i < n; i++) cur[i] = (uint8_t)(cur[i] + prior[i]); break;
        case 3: for (i = 0; i < n; i++) { int left = i >= (size_t)bpp ? cur[i - bpp] : 0; cur[i] = (uint8_t)(cur[i] + ((left + prior[i]) >> 1)); } break;
        case 4:
            for (i = 0; i < n; i++) {
                int a = i >= (size_t)bpp ? cur[i - bpp] : 0, b = prior[i], c = i >= (size_t)bpp ? prior[i - bpp] : 0;
                int p = a + b - c, pa = abs(p - a), pb = abs(p - b), pc = abs(p - c);
                int pred = pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
                cur[i] = (uint8_t)(cur[i] + pred);
            }
            break;
        default: snprintf(err, FIRE_GFX_ERR_SIZE, "PNG: unknown scanline filter %d.", filter); return 0;
    }
    return 1;
}

typedef struct FireGfxPngPass {
    int bitDepth, colorType, channels, bitsPerPixel, bpp, fullWidth;
    int hasKey; uint32_t keyGray, keyR, keyG, keyB;
} FireGfxPngPass;

static int fire_gfx_decodePass(const uint8_t* raw, size_t* offset, int pw, int ph, int x0, int y0, int dx, int dy, const FireGfxPngPass* p, FireGfxImage* out, char* err) {
    size_t rowBytes = (size_t)(((int64_t)pw * p->bitsPerPixel + 7) / 8);
    uint8_t* prior = (uint8_t*)calloc(rowBytes ? rowBytes : 1, 1);
    uint8_t* cur = (uint8_t*)calloc(rowBytes ? rowBytes : 1, 1);
    int row, col;
    if (prior == NULL || cur == NULL) { free(prior); free(cur); FIRE_IMG_FAIL("Not enough memory for the image."); }
    for (row = 0; row < ph; row++) {
        int filter = raw[(*offset)++], y;
        uint8_t* t;
        memcpy(cur, raw + *offset, rowBytes);
        *offset += rowBytes;
        if (!fire_gfx_unfilter(filter, cur, prior, rowBytes, p->bpp, err)) { free(prior); free(cur); return 0; }
        y = y0 + row * dy;
        for (col = 0; col < pw; col++) {
            int x = x0 + col * dx;
            size_t pos = (size_t)y * p->fullWidth + x;
            uint32_t r, g, b, a = 255;
            if (out->indexed) { out->indices[pos] = (uint8_t)fire_gfx_sample(cur, col, p->bitDepth); continue; }
            switch (p->colorType) {
                case 0: { uint32_t v = fire_gfx_sample(cur, col, p->bitDepth); if (p->hasKey && v == p->keyGray) a = 0; r = g = b = fire_gfx_scale(v, p->bitDepth); break; }
                case 2: {
                    uint32_t rr = fire_gfx_sample(cur, col * 3, p->bitDepth), gg = fire_gfx_sample(cur, col * 3 + 1, p->bitDepth), bb = fire_gfx_sample(cur, col * 3 + 2, p->bitDepth);
                    if (p->hasKey && rr == p->keyR && gg == p->keyG && bb == p->keyB) a = 0;
                    r = fire_gfx_scale(rr, p->bitDepth); g = fire_gfx_scale(gg, p->bitDepth); b = fire_gfx_scale(bb, p->bitDepth);
                    break;
                }
                case 4: r = g = b = fire_gfx_scale(fire_gfx_sample(cur, col * 2, p->bitDepth), p->bitDepth); a = fire_gfx_scale(fire_gfx_sample(cur, col * 2 + 1, p->bitDepth), p->bitDepth); break;
                default:
                    r = fire_gfx_scale(fire_gfx_sample(cur, col * 4, p->bitDepth), p->bitDepth); g = fire_gfx_scale(fire_gfx_sample(cur, col * 4 + 1, p->bitDepth), p->bitDepth);
                    b = fire_gfx_scale(fire_gfx_sample(cur, col * 4 + 2, p->bitDepth), p->bitDepth); a = fire_gfx_scale(fire_gfx_sample(cur, col * 4 + 3, p->bitDepth), p->bitDepth);
                    break;
            }
            out->pixels[pos] = r | (g << 8) | (b << 16) | (a << 24);
        }
        t = prior; prior = cur; cur = t;
    }
    free(prior);
    free(cur);
    return 1;
}

/* (a growing byte array: the chunks of a PNG, the data of a GIF) */
typedef struct FireGfxBytes { uint8_t* data; size_t n, cap; } FireGfxBytes;
static int fire_gfx_bytesAppend(FireGfxBytes* b, const uint8_t* data, size_t n) {
    if (b->n + n > b->cap) {
        size_t cap = b->cap ? b->cap : 256;
        uint8_t* grown;
        while (cap < b->n + n) cap *= 2;
        grown = (uint8_t*)realloc(b->data, cap);
        if (grown == NULL) return 0;
        b->data = grown;
        b->cap = cap;
    }
    if (n) memcpy(b->data + b->n, data, n);
    b->n += n;
    return 1;
}
static void fire_gfx_bytesAssign(FireGfxBytes* b, const uint8_t* data, size_t n) { b->n = 0; fire_gfx_bytesAppend(b, data, n); }

static int fire_gfx_decodePngInner(const uint8_t* d, size_t size, FireGfxImage* out, char* err, FireGfxBytes* plte, FireGfxBytes* trns, FireGfxBytes* idat, uint8_t** rawOut) {
    size_t pos = 8, offset = 0;
    int width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0, haveHeader = 0, sawEnd = 0;
    int channels, bitsPerPixel, bpp, status, indexed, pass;
    int64_t expected = 0;
    uint8_t* raw;
    FireGfxPngPass p;
    while (!sawEnd) {
        uint32_t length;
        char type[5];
        const uint8_t* data;
        if (pos + 12 > size) FIRE_IMG_FAIL("PNG: file truncated (no IEND).");
        length = fire_gfx_be32(d, pos);
        if (length > 0x7FFFFFFFu || (uint64_t)pos + 12 + length > size) FIRE_IMG_FAIL("PNG: file truncated (chunk extends past the end).");
        memcpy(type, d + pos + 4, 4);
        type[4] = 0;
        data = d + pos + 8;
        if (fire_gfx_crc(d + pos + 4, 4 + (size_t)length) != fire_gfx_be32(d, pos + 8 + length)) { snprintf(err, FIRE_GFX_ERR_SIZE, "PNG: checksum error in chunk '%s'.", type); return 0; }
        pos += 12 + (size_t)length;
        if (!haveHeader && memcmp(type, "IHDR", 4) != 0) FIRE_IMG_FAIL("PNG: the first chunk must be IHDR.");
        if (memcmp(type, "IHDR", 4) == 0) {
            int validDepth;
            uint32_t bw, bh;
            if (haveHeader || length != 13) FIRE_IMG_FAIL("PNG: invalid IHDR chunk.");
            bw = fire_gfx_be32(data, 0); bh = fire_gfx_be32(data, 4);
            width = (int)(bw < 0x7FFFFFFFu ? bw : 0x7FFFFFFFu);
            height = (int)(bh < 0x7FFFFFFFu ? bh : 0x7FFFFFFFu);
            bitDepth = data[8]; colorType = data[9];
            if (data[10] != 0) FIRE_IMG_FAIL("PNG: unknown compression method.");
            if (data[11] != 0) FIRE_IMG_FAIL("PNG: unknown filter method.");
            interlace = data[12];
            if (interlace > 1) FIRE_IMG_FAIL("PNG: unknown interlace method.");
            if (!fire_gfx_checkSize("PNG", width, height, err)) return 0;
            validDepth = colorType == 0 ? (bitDepth == 1 || bitDepth == 2 || bitDepth == 4 || bitDepth == 8 || bitDepth == 16)
                : (colorType == 2 || colorType == 4 || colorType == 6) ? (bitDepth == 8 || bitDepth == 16)
                : colorType == 3 ? (bitDepth == 1 || bitDepth == 2 || bitDepth == 4 || bitDepth == 8) : 0;
            if (!validDepth) { snprintf(err, FIRE_GFX_ERR_SIZE, "PNG: invalid combination of color type %d and bit depth %d.", colorType, bitDepth); return 0; }
            haveHeader = 1;
        } else if (memcmp(type, "PLTE", 4) == 0) {
            if (length == 0 || length % 3 != 0 || length > 768) FIRE_IMG_FAIL("PNG: invalid palette.");
            fire_gfx_bytesAssign(plte, data, length);
        } else if (memcmp(type, "tRNS", 4) == 0) fire_gfx_bytesAssign(trns, data, length);
        else if (memcmp(type, "IDAT", 4) == 0) { if (!fire_gfx_bytesAppend(idat, data, length)) FIRE_IMG_FAIL("Not enough memory for the image."); }
        else if (memcmp(type, "IEND", 4) == 0) sawEnd = 1;
    }
    if (colorType == 3 && plte->n == 0) FIRE_IMG_FAIL("PNG: Palette-Bild ohne PLTE-Chunk.");
    if (idat->n == 0) FIRE_IMG_FAIL("PNG: no image data (IDAT).");

    channels = colorType == 0 ? 1 : colorType == 2 ? 3 : colorType == 3 ? 1 : colorType == 4 ? 2 : 4;
    bitsPerPixel = channels * bitDepth;
    bpp = fire_gfx_imax(1, bitsPerPixel / 8);
    if (interlace == 0) { expected = ((int64_t)width * bitsPerPixel + 7) / 8 + 1; expected *= height; }
    else for (pass = 0; pass < 7; pass++) {
        int pw = fire_gfx_passWidth(width, pass), ph = fire_gfx_passHeight(height, pass);
        if (pw > 0 && ph > 0) expected += (((int64_t)pw * bitsPerPixel + 7) / 8 + 1) * ph;
    }
    if (expected > 0x7FFFFFFF) FIRE_IMG_FAIL("PNG: image too large.");
    raw = (uint8_t*)calloc((size_t)expected ? (size_t)expected : 1, 1);
    if (raw == NULL) FIRE_IMG_FAIL("Not enough memory for the image.");
    *rawOut = raw;
    status = fire_gfx_zlibInflate(idat->data, idat->n, raw, (size_t)expected);
    if (status == 2) FIRE_IMG_FAIL("PNG: the image data is too short (truncated or corrupt).");
    if (status == 1) FIRE_IMG_FAIL("PNG: the image data cannot be decompressed (corrupt).");

    out->width = width; out->height = height; out->format = "PNG";
    indexed = colorType == 3;
    out->indexed = indexed;
    if (indexed) out->indices = (uint8_t*)calloc(fire_gfx_imageCount(out), 1); else out->pixels = (uint32_t*)calloc(fire_gfx_imageCount(out), 4);
    if (indexed ? out->indices == NULL : out->pixels == NULL) FIRE_IMG_FAIL("Not enough memory for the image.");
    p.bitDepth = bitDepth; p.colorType = colorType; p.channels = channels; p.bitsPerPixel = bitsPerPixel; p.bpp = bpp; p.fullWidth = width;
    p.hasKey = 0; p.keyGray = 0; p.keyR = 0; p.keyG = 0; p.keyB = 0;
    if (!indexed && colorType != 4 && colorType != 6 && trns->n > 0) {
        const uint8_t* t = trns->data;
        if (colorType == 0 && trns->n >= 2) { p.keyGray = (uint32_t)(t[0] << 8 | t[1]); p.hasKey = 1; }
        else if (colorType == 2 && trns->n >= 6) { p.keyR = (uint32_t)(t[0] << 8 | t[1]); p.keyG = (uint32_t)(t[2] << 8 | t[3]); p.keyB = (uint32_t)(t[4] << 8 | t[5]); p.hasKey = 1; }
    }
    if (interlace == 0) { if (!fire_gfx_decodePass(raw, &offset, width, height, 0, 0, 1, 1, &p, out, err)) return 0; }
    else for (pass = 0; pass < 7; pass++) {
        int pw = fire_gfx_passWidth(width, pass), ph = fire_gfx_passHeight(height, pass);
        if (pw == 0 || ph == 0) continue;
        if (!fire_gfx_decodePass(raw, &offset, pw, ph, g_fireGfxPassX[pass], g_fireGfxPassY[pass], g_fireGfxPassDx[pass], g_fireGfxPassDy[pass], &p, out, err)) return 0;
    }
    if (indexed) {
        size_t entries = plte->n / 3, i, n = fire_gfx_imageCount(out);
        int transparent = -1;
        fire_gfx_imagePaletteBlack(out);
        for (i = 0; i < entries; i++) {
            uint32_t a = i < trns->n ? trns->data[i] : 255u;
            out->palette[i] = plte->data[i * 3] | ((uint32_t)plte->data[i * 3 + 1] << 8) | ((uint32_t)plte->data[i * 3 + 2] << 16) | (a << 24);
            if (a == 0 && transparent < 0) transparent = (int)i;
        }
        for (i = 0; i < n; i++) if (out->indices[i] >= entries) FIRE_IMG_FAIL("PNG: a pixel refers to a palette entry that does not exist.");
        out->transparentIndex = transparent;
        return 1;
    }
    out->transparentIndex = -1;
    return 1;
}
static int fire_gfx_decodePng(const uint8_t* d, size_t size, FireGfxImage* out, char* err) {
    FireGfxBytes plte = {NULL, 0, 0}, trns = {NULL, 0, 0}, idat = {NULL, 0, 0};
    uint8_t* raw = NULL;
    int ok = fire_gfx_decodePngInner(d, size, out, err, &plte, &trns, &idat, &raw);
    free(plte.data); free(trns.data); free(idat.data); free(raw);
    return ok;
}

/* ---- BMP --------------------------------------------------------------------------------------------------------------------------------------------- */
static int fire_gfx_le16(const uint8_t* d, size_t p) { return d[p] | (d[p + 1] << 8); }
static int fire_gfx_le32(const uint8_t* d, size_t p) { return (int)((uint32_t)d[p] | ((uint32_t)d[p + 1] << 8) | ((uint32_t)d[p + 2] << 16) | ((uint32_t)d[p + 3] << 24)); }
static int fire_gfx_trailingZeros(uint32_t v) { int n = 0; while (!(v & 1)) { v >>= 1; n++; } return n; }
static uint32_t fire_gfx_extract(uint32_t v, uint32_t mask) {
    int shift;
    uint32_t max;
    if (mask == 0) return 0;
    shift = fire_gfx_trailingZeros(mask);
    max = mask >> shift;
    return ((v & mask) >> shift) * 255 / max;
}

/* (an RLE pixel of a BMP) */
static void fire_gfx_bmpPut(FireGfxImage* out, int* x, int row, int index) {
    if (*x >= 0 && *x < out->width && row >= 0 && row < out->height) out->indices[(size_t)(out->height - 1 - row) * out->width + *x] = (uint8_t)index;
    (*x)++;
}

static int fire_gfx_decodeBmp(const uint8_t* d, size_t size, FireGfxImage* out, char* err) {
    int dataOffset, headerSize, width, height, bpp, compression = 0, colorsUsed = 0, topDown = 0, hasMasks, row, x;
    uint32_t maskR = 0, maskG = 0, maskB = 0, maskA = 0;
    int64_t rowSize;
    int anyAlpha = 0;
    if (size < 26) FIRE_IMG_FAIL("BMP: file too short.");
    dataOffset = fire_gfx_le32(d, 10); headerSize = fire_gfx_le32(d, 14);
    if (headerSize == 12) { width = fire_gfx_le16(d, 18); height = fire_gfx_le16(d, 20); bpp = fire_gfx_le16(d, 24); }
    else if (headerSize >= 40) {
        int h;
        if (size < 14 + 40) FIRE_IMG_FAIL("BMP: file too short.");
        width = fire_gfx_le32(d, 18);
        h = fire_gfx_le32(d, 22);
        topDown = h < 0;
        height = h == INT32_MIN ? 0 : abs(h);
        if (fire_gfx_le16(d, 26) != 1) FIRE_IMG_FAIL("BMP: invalid plane count.");
        bpp = fire_gfx_le16(d, 28);
        compression = fire_gfx_le32(d, 30);
        colorsUsed = fire_gfx_le32(d, 46);
    } else { snprintf(err, FIRE_GFX_ERR_SIZE, "BMP: unsupported header (%d bytes).", headerSize); return 0; }
    if (!fire_gfx_checkSize("BMP", width, height, err)) return 0;
    if (!(bpp == 1 || bpp == 4 || bpp == 8 || bpp == 16 || bpp == 24 || bpp == 32)) { snprintf(err, FIRE_GFX_ERR_SIZE, "BMP: unsupported color depth (%d bits).", bpp); return 0; }
    if (!(compression == 0 || compression == 1 || compression == 2 || compression == 3 || compression == 6)) { snprintf(err, FIRE_GFX_ERR_SIZE, "BMP: unsupported compression (%d).", compression); return 0; }
    if ((compression == 1 && bpp != 8) || (compression == 2 && bpp != 4)) FIRE_IMG_FAIL("BMP: RLE does not match the color depth.");
    if ((compression == 3 || compression == 6) && !(bpp == 16 || bpp == 32)) FIRE_IMG_FAIL("BMP: bit masks are only allowed with 16 and 32 bits.");
    if (dataOffset < 0 || (size_t)dataOffset > size) FIRE_IMG_FAIL("BMP: invalid data offset.");

    hasMasks = compression == 3 || compression == 6;
    if (hasMasks) {
        size_t maskPos = 14 + 40;
        if (size < maskPos + 12) FIRE_IMG_FAIL("BMP: file too short for the bit masks.");
        maskR = (uint32_t)fire_gfx_le32(d, maskPos); maskG = (uint32_t)fire_gfx_le32(d, maskPos + 4); maskB = (uint32_t)fire_gfx_le32(d, maskPos + 8);
        if ((compression == 6 || headerSize >= 56) && size >= maskPos + 16) maskA = (uint32_t)fire_gfx_le32(d, maskPos + 12);
    } else if (bpp == 16) { maskR = 0x7C00; maskG = 0x03E0; maskB = 0x001F; }

    out->width = width; out->height = height; out->format = "BMP";
    fire_gfx_imagePaletteBlack(out);
    if (bpp <= 8) {
        int entries = colorsUsed > 0 ? colorsUsed : 1 << bpp, entrySize, i, rle8;
        size_t palPos, p;
        if (entries > 256 || entries > (1 << bpp)) entries = fire_gfx_imin(256, 1 << bpp);
        entrySize = headerSize == 12 ? 3 : 4;
        palPos = 14 + (size_t)headerSize;
        if (palPos + (size_t)entries * entrySize > size) FIRE_IMG_FAIL("BMP: file too short for the palette.");
        for (i = 0; i < entries; i++) {
            size_t q = palPos + (size_t)i * entrySize;
            out->palette[i] = d[q + 2] | ((uint32_t)d[q + 1] << 8) | ((uint32_t)d[q] << 16) | 0xFF000000u;
        }
        out->indexed = 1;
        out->transparentIndex = -1;
        out->indices = (uint8_t*)calloc(fire_gfx_imageCount(out), 1);
        if (out->indices == NULL) FIRE_IMG_FAIL("Not enough memory for the image.");
        if (compression == 0) {
            rowSize = ((int64_t)width * bpp + 31) / 32 * 4;
            if ((int64_t)dataOffset + rowSize * height > (int64_t)size) FIRE_IMG_FAIL("BMP: file too short for the image data.");
            for (row = 0; row < height; row++) {
                int y = topDown ? row : height - 1 - row;
                size_t rowStart = (size_t)dataOffset + (size_t)row * rowSize;
                for (x = 0; x < width; x++) {
                    int bitPos = x * bpp, shift = 8 - bpp - (bitPos & 7);
                    out->indices[(size_t)y * width + x] = (uint8_t)((d[rowStart + (bitPos >> 3)] >> shift) & ((1 << bpp) - 1));
                }
            }
            return 1;
        }
        rle8 = compression == 1;
        p = (size_t)dataOffset;
        x = 0; row = 0;
        while (p + 1 < size) {
            int count = d[p], value = d[p + 1], n, bytes;
            p += 2;
            if (count > 0) { for (i = 0; i < count; i++) fire_gfx_bmpPut(out, &x, row, rle8 ? value : (i % 2 == 0 ? value >> 4 : value & 0xF)); continue; }
            if (value == 0) { x = 0; row++; continue; }
            if (value == 1) break;
            if (value == 2) { if (p + 1 >= size) break; x += d[p]; row += d[p + 1]; p += 2; continue; }
            n = value; bytes = rle8 ? n : (n + 1) / 2;
            if (p + (size_t)bytes > size) FIRE_IMG_FAIL("BMP: RLE-Daten abgeschnitten.");
            for (i = 0; i < n; i++) fire_gfx_bmpPut(out, &x, row, rle8 ? d[p + i] : (i % 2 == 0 ? d[p + i / 2] >> 4 : d[p + i / 2] & 0xF));
            p += (size_t)bytes + (bytes & 1);
        }
        return 1;
    }

    rowSize = ((int64_t)width * bpp + 31) / 32 * 4;
    if ((int64_t)dataOffset + rowSize * height > (int64_t)size) FIRE_IMG_FAIL("BMP: file too short for the image data.");
    out->indexed = 0;
    out->transparentIndex = -1;
    out->pixels = (uint32_t*)calloc(fire_gfx_imageCount(out), 4);
    if (out->pixels == NULL) FIRE_IMG_FAIL("Not enough memory for the image.");
    for (row = 0; row < height; row++) {
        int y = topDown ? row : height - 1 - row;
        size_t rowStart = (size_t)dataOffset + (size_t)row * rowSize;
        for (x = 0; x < width; x++) {
            uint32_t r, g, b, a = 255;
            if (bpp == 24) { size_t q = rowStart + (size_t)x * 3; b = d[q]; g = d[q + 1]; r = d[q + 2]; }
            else {
                uint32_t v = bpp == 16 ? (uint32_t)fire_gfx_le16(d, rowStart + (size_t)x * 2) : (uint32_t)fire_gfx_le32(d, rowStart + (size_t)x * 4);
                if (bpp == 32 && !hasMasks) { b = v & 0xFF; g = (v >> 8) & 0xFF; r = (v >> 16) & 0xFF; a = v >> 24; if (a != 0) anyAlpha = 1; }
                else { r = fire_gfx_extract(v, maskR); g = fire_gfx_extract(v, maskG); b = fire_gfx_extract(v, maskB); if (maskA != 0) a = fire_gfx_extract(v, maskA); }
            }
            out->pixels[(size_t)y * width + x] = r | (g << 8) | (b << 16) | (a << 24);
        }
    }
    if (bpp == 32 && !hasMasks && !anyAlpha) {   /* the fourth byte is mostly unused: the image is opaque */
        size_t i, n = fire_gfx_imageCount(out);
        for (i = 0; i < n; i++) out->pixels[i] |= 0xFF000000u;
    }
    return 1;
}

/* ---- GIF --------------------------------------------------------------------------------------------------------------------------------------------- */
static int fire_gfx_gifPalette(const uint8_t* d, size_t size, size_t* pos, int entries, uint32_t* palette, char* err) {
    int i;
    if (*pos + (size_t)entries * 3 > size) FIRE_IMG_FAIL("GIF: file truncated (palette).");
    for (i = 0; i < 256; i++) palette[i] = 0xFF000000u;
    for (i = 0; i < entries; i++) { palette[i] = d[*pos] | ((uint32_t)d[*pos + 1] << 8) | ((uint32_t)d[*pos + 2] << 16) | 0xFF000000u; *pos += 3; }
    return 1;
}
/* `output` (pixelCount bytes, zeroed) receives the pixels. */
static int fire_gfx_lzw(const uint8_t* data, size_t dataSize, int minCodeSize, size_t pixelCount, uint8_t* output, char* err) {
    int clear = 1 << minCodeSize, end = clear + 1;
    /* (the tables on the heap: a task of a board has a small stack) */
    int16_t* prefix = (int16_t*)calloc(4096, sizeof(int16_t));
    uint8_t* suffix = (uint8_t*)calloc(4096, 1);
    uint8_t* stack = (uint8_t*)calloc(4097, 1);
    int codeSize = minCodeSize + 1, next = end + 1, prev = -1, ok = 1;
    uint8_t first = 0;
    size_t outPos = 0, dataPos = 0;
    int bitBuffer = 0, bitCount = 0;
    if (prefix == NULL || suffix == NULL || stack == NULL) { snprintf(err, FIRE_GFX_ERR_SIZE, "%s", "Not enough memory for the image."); ok = 0; goto done; }
    while (outPos < pixelCount) {
        int code, cur, sp = 0;
        while (bitCount < codeSize) {
            if (dataPos >= dataSize) goto done;   /* data ends without an end code: lenient */
            bitBuffer |= data[dataPos++] << bitCount;
            bitCount += 8;
        }
        code = bitBuffer & ((1 << codeSize) - 1);
        bitBuffer >>= codeSize;
        bitCount -= codeSize;
        if (code == clear) { codeSize = minCodeSize + 1; next = end + 1; prev = -1; continue; }
        if (code == end) break;
        if (prev == -1) {
            if (code >= clear) { snprintf(err, FIRE_GFX_ERR_SIZE, "%s", "GIF: invalid LZW code at the start."); ok = 0; goto done; }
            output[outPos++] = (uint8_t)code; prev = code; first = (uint8_t)code;
            continue;
        }
        cur = code;
        if (code >= next) {
            if (code > next) { snprintf(err, FIRE_GFX_ERR_SIZE, "%s", "GIF: corrupt LZW data (code outside of the dictionary)."); ok = 0; goto done; }
            stack[(size_t)sp++] = first;
            cur = prev;
        }
        while (cur >= clear) { stack[(size_t)sp++] = suffix[(size_t)cur]; cur = prefix[(size_t)cur]; }
        stack[(size_t)sp++] = (uint8_t)cur;
        first = (uint8_t)cur;
        while (sp > 0 && outPos < pixelCount) output[outPos++] = stack[(size_t)--sp];
        if (next < 4096) {
            prefix[(size_t)next] = (int16_t)prev;
            suffix[(size_t)next] = first;
            next++;
            if (next == (1 << codeSize) && codeSize < 12) codeSize++;
        }
        prev = code;
    }
done:
    free(prefix);
    free(suffix);
    free(stack);
    return ok;
}

static int fire_gfx_decodeGif(const uint8_t* d, size_t size, FireGfxImage* out, char* err) {
    int screenW, screenH, flags, backgroundIndex, haveGlobal = 0, transparent = -1;
    size_t pos = 13;
    uint32_t globalPalette[256];
    if (size < 13) FIRE_IMG_FAIL("GIF: file too short.");
    screenW = d[6] | (d[7] << 8); screenH = d[8] | (d[9] << 8); flags = d[10]; backgroundIndex = d[11];
    if (!fire_gfx_checkSize("GIF", screenW, screenH, err)) return 0;
    if (flags & 0x80) { if (!fire_gfx_gifPalette(d, size, &pos, 2 << (flags & 7), globalPalette, err)) return 0; haveGlobal = 1; }
    while (pos < size) {
        int block = d[pos++], left, top, w, h, iflags, havePalette, interlaced, minCode, fill, r, ok;
        uint32_t palette[256];
        FireGfxBytes packed = {NULL, 0, 0};
        uint8_t* frame;
        int* rows;
        if (block == 0x3B) break;
        if (block == 0x21) {
            int label;
            if (pos >= size) break;
            label = d[pos++];
            if (label == 0xF9 && pos + 5 < size && d[pos] == 4) transparent = (d[pos + 1] & 1) != 0 ? d[pos + 4] : -1;
            while (pos < size) { int len = d[pos++]; if (len == 0) break; pos += (size_t)len; }
            continue;
        }
        if (block != 0x2C) { snprintf(err, FIRE_GFX_ERR_SIZE, "GIF: unknown block 0x%02X.", block); return 0; }
        if (pos + 9 > size) FIRE_IMG_FAIL("GIF: file truncated (image descriptor).");
        left = d[pos] | (d[pos + 1] << 8); top = d[pos + 2] | (d[pos + 3] << 8); w = d[pos + 4] | (d[pos + 5] << 8); h = d[pos + 6] | (d[pos + 7] << 8); iflags = d[pos + 8];
        pos += 9;
        havePalette = haveGlobal;
        if (haveGlobal) memcpy(palette, globalPalette, sizeof palette);
        if (iflags & 0x80) { if (!fire_gfx_gifPalette(d, size, &pos, 2 << (iflags & 7), palette, err)) return 0; havePalette = 1; }
        if (!havePalette) FIRE_IMG_FAIL("GIF: weder globale noch lokale Palette.");
        interlaced = (iflags & 0x40) != 0;
        if (pos >= size) FIRE_IMG_FAIL("GIF: file truncated (image data).");
        minCode = d[pos++];
        if (minCode < 2 || minCode > 11) FIRE_IMG_FAIL("GIF: invalid LZW code size.");
        for (;;) {
            int len;
            if (pos >= size) { free(packed.data); FIRE_IMG_FAIL("GIF: file truncated (image data)."); }
            len = d[pos++];
            if (len == 0) break;
            if (pos + (size_t)len > size) { free(packed.data); FIRE_IMG_FAIL("GIF: file truncated (image data)."); }
            if (!fire_gfx_bytesAppend(&packed, d + pos, (size_t)len)) { free(packed.data); FIRE_IMG_FAIL("Not enough memory for the image."); }
            pos += (size_t)len;
        }
        frame = (uint8_t*)calloc(w && h ? (size_t)w * (size_t)h : 1, 1);
        rows = (int*)calloc(h ? (size_t)h : 1, sizeof(int));
        if (frame == NULL || rows == NULL) { free(packed.data); free(frame); free(rows); FIRE_IMG_FAIL("Not enough memory for the image."); }
        ok = fire_gfx_lzw(packed.data, packed.n, minCode, (size_t)w * (size_t)h, frame, err);
        free(packed.data);
        if (!ok) { free(frame); free(rows); return 0; }
        fill = transparent >= 0 ? transparent : fire_gfx_imin(backgroundIndex, 255);
        out->width = screenW; out->height = screenH; out->format = "GIF"; out->indexed = 1; out->transparentIndex = transparent;
        out->indices = (uint8_t*)malloc(fire_gfx_imageCount(out));
        if (out->indices == NULL) { free(frame); free(rows); FIRE_IMG_FAIL("Not enough memory for the image."); }
        memset(out->indices, (uint8_t)fill, fire_gfx_imageCount(out));
        memcpy(out->palette, palette, sizeof palette);
        if (!interlaced) { int i; for (i = 0; i < h; i++) rows[(size_t)i] = i; }
        else {
            static const int starts[4] = {0, 4, 2, 1}, steps[4] = {8, 8, 4, 2};
            int n = 0, s, y;
            for (s = 0; s < 4; s++) for (y = starts[s]; y < h; y += steps[s]) rows[(size_t)n++] = y;
        }
        for (r = 0; r < h; r++) {
            int y = top + rows[(size_t)r], x;
            if (y < 0 || y >= screenH) continue;
            for (x = 0; x < w; x++) {
                int px = left + x;
                if (px < 0 || px >= screenW) continue;
                out->indices[(size_t)y * screenW + px] = frame[(size_t)r * w + x];
            }
        }
        free(frame);
        free(rows);
        return 1;
    }
    FIRE_IMG_FAIL("GIF: the file contains no image.");
}

/* Reads PNG, BMP or GIF. 0: `err` is the message of the ImageException (the caller frees what `out` holds either way: fire_gfx_imageFree). */
static int fire_gfx_decodeImage(const uint8_t* d, size_t n, FireGfxImage* out, char* err) {
    if (n == 0) FIRE_IMG_FAIL("The image data is empty.");
    if (n >= 8 && d[0] == 0x89 && d[1] == 'P' && d[2] == 'N' && d[3] == 'G' && d[4] == 0x0D && d[5] == 0x0A && d[6] == 0x1A && d[7] == 0x0A) return fire_gfx_decodePng(d, n, out, err);
    if (n >= 2 && d[0] == 'B' && d[1] == 'M') return fire_gfx_decodeBmp(d, n, out, err);
    if (n >= 6 && d[0] == 'G' && d[1] == 'I' && d[2] == 'F' && d[3] == '8' && (d[4] == '7' || d[4] == '9') && d[5] == 'a') return fire_gfx_decodeGif(d, n, out, err);
    FIRE_IMG_FAIL("Unknown image format (expected: PNG, BMP or GIF).");
}

/* ---- writing: PNG (RGBA, 8 bits; deflate with the fixed Huffman codes and LZ77) and BMP (32 bits) - Framebuffer.ToPng and the clipboard ---------- */
typedef struct FireGfxOut { uint8_t* data; size_t n, cap; int failed; uint32_t bits; int count; } FireGfxOut;
static void fire_gfx_outByte(FireGfxOut* o, uint8_t b) {
    if (o->failed) return;
    if (o->n == o->cap) {
        size_t cap = o->cap ? o->cap * 2 : 4096;
        uint8_t* grown = (uint8_t*)realloc(o->data, cap);
        if (grown == NULL) { o->failed = 1; return; }
        o->data = grown; o->cap = cap;
    }
    o->data[o->n++] = b;
}
static void fire_gfx_outBytes(FireGfxOut* o, const void* p, size_t n) { size_t i; for (i = 0; i < n; i++) fire_gfx_outByte(o, ((const uint8_t*)p)[i]); }
static void fire_gfx_outBe32(FireGfxOut* o, uint32_t v) { fire_gfx_outByte(o, (uint8_t)(v >> 24)); fire_gfx_outByte(o, (uint8_t)(v >> 16)); fire_gfx_outByte(o, (uint8_t)(v >> 8)); fire_gfx_outByte(o, (uint8_t)v); }
static void fire_gfx_outLe32(FireGfxOut* o, uint32_t v) { fire_gfx_outByte(o, (uint8_t)v); fire_gfx_outByte(o, (uint8_t)(v >> 8)); fire_gfx_outByte(o, (uint8_t)(v >> 16)); fire_gfx_outByte(o, (uint8_t)(v >> 24)); }
/* bits, the lowest first (as deflate packs them) */
static void fire_gfx_outBits(FireGfxOut* o, uint32_t value, int n) {
    o->bits |= value << o->count;
    o->count += n;
    while (o->count >= 8) { fire_gfx_outByte(o, (uint8_t)o->bits); o->bits >>= 8; o->count -= 8; }
}
/* a Huffman code: its bits from the highest (deflate stores codes the other way round) */
static void fire_gfx_outCode(FireGfxOut* o, uint32_t code, int n) {
    uint32_t reversed = 0;
    int i;
    for (i = 0; i < n; i++) reversed |= ((code >> i) & 1u) << (n - 1 - i);
    fire_gfx_outBits(o, reversed, n);
}
static void fire_gfx_outLiteral(FireGfxOut* o, int symbol) {
    if (symbol < 144) fire_gfx_outCode(o, 0x30u + (uint32_t)symbol, 8);
    else if (symbol < 256) fire_gfx_outCode(o, 0x190u + (uint32_t)(symbol - 144), 9);
    else if (symbol < 280) fire_gfx_outCode(o, (uint32_t)(symbol - 256), 7);
    else fire_gfx_outCode(o, 0xC0u + (uint32_t)(symbol - 280), 8);
}
static void fire_gfx_outMatch(FireGfxOut* o, int length, int distance) {
    static const int lengthBase[29] = {3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258};
    static const int lengthExtra[29] = {0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0};
    static const int distBase[30] = {1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577};
    static const int distExtra[30] = {0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13};
    int l = 28, d = 29;
    while (lengthBase[l] > length) l--;
    while (distBase[d] > distance) d--;
    fire_gfx_outLiteral(o, 257 + l);
    if (lengthExtra[l]) fire_gfx_outBits(o, (uint32_t)(length - lengthBase[l]), lengthExtra[l]);
    fire_gfx_outCode(o, (uint32_t)d, 5);
    if (distExtra[d]) fire_gfx_outBits(o, (uint32_t)(distance - distBase[d]), distExtra[d]);
}
/* zlib around one deflate block with the fixed codes: greedy LZ77 over a window of 32 KB (a few candidates per position). 0 without memory. */
#define FIRE_GFX_LZ_WINDOW 32768
#define FIRE_GFX_LZ_HASH 32768
static int fire_gfx_zlibDeflate(const uint8_t* in, size_t n, FireGfxOut* o) {
    int32_t* head = (int32_t*)malloc(sizeof(int32_t) * FIRE_GFX_LZ_HASH);
    int32_t* prev = (int32_t*)malloc(sizeof(int32_t) * FIRE_GFX_LZ_WINDOW);
    uint32_t a = 1, b = 0;
    size_t i = 0, k;
    if (head == NULL || prev == NULL) { free(head); free(prev); return 0; }
    for (k = 0; k < FIRE_GFX_LZ_HASH; k++) head[k] = -1;
    fire_gfx_outByte(o, 0x78); fire_gfx_outByte(o, 0x01);
    fire_gfx_outBits(o, 1, 1);   /* the last block */
    fire_gfx_outBits(o, 1, 2);   /* fixed Huffman codes */
    while (i < n) {
        int bestLength = 0, bestDistance = 0;
        if (i + 3 <= n) {
            uint32_t h = ((uint32_t)in[i] << 16 | (uint32_t)in[i + 1] << 8 | in[i + 2]) * 2654435761u >> 17;
            int32_t candidate = head[h];
            int tries = 32;
            size_t limit = n - i < 258 ? n - i : 258;
            while (candidate >= 0 && i - (size_t)candidate <= FIRE_GFX_LZ_WINDOW && tries-- > 0) {
                size_t len = 0;
                while (len < limit && in[(size_t)candidate + len] == in[i + len]) len++;
                if ((int)len > bestLength) { bestLength = (int)len; bestDistance = (int)(i - (size_t)candidate); if (len == limit) break; }
                candidate = prev[(size_t)candidate % FIRE_GFX_LZ_WINDOW];
            }
            prev[i % FIRE_GFX_LZ_WINDOW] = head[h];
            head[h] = (int32_t)i;
        }
        if (bestLength >= 3) {
            fire_gfx_outMatch(o, bestLength, bestDistance);
            /* the positions inside the match go into the hash too */
            for (k = 1; k < (size_t)bestLength; k++) {
                size_t p = i + k;
                if (p + 3 <= n) {
                    uint32_t h = ((uint32_t)in[p] << 16 | (uint32_t)in[p + 1] << 8 | in[p + 2]) * 2654435761u >> 17;
                    prev[p % FIRE_GFX_LZ_WINDOW] = head[h];
                    head[h] = (int32_t)p;
                }
            }
            i += (size_t)bestLength;
        } else fire_gfx_outLiteral(o, in[i++]);
    }
    fire_gfx_outLiteral(o, 256);   /* end of the block */
    if (o->count > 0) fire_gfx_outBits(o, 0, 8 - o->count);
    for (k = 0; k < n; k++) { a = (a + in[k]) % 65521u; b = (b + a) % 65521u; }
    fire_gfx_outBe32(o, (b << 16) | a);
    free(head);
    free(prev);
    return !o->failed;
}
static void fire_gfx_outChunk(FireGfxOut* o, const char* type, const uint8_t* data, size_t n) {
    size_t start;
    fire_gfx_outBe32(o, (uint32_t)n);
    start = o->n;
    fire_gfx_outBytes(o, type, 4);
    fire_gfx_outBytes(o, data, n);
    if (!o->failed) fire_gfx_outBe32(o, fire_gfx_crc(o->data + start, n + 4));
}
/* A PNG file of the pixels (R,G,B,A per 32-bit word, width*height): color type 6 (RGBA), 8 bits, no interlacing. malloc; NULL without memory. */
static uint8_t* fire_gfx_encodePng(const uint32_t* rgba, int width, int height, size_t* size) {
    static const uint8_t signature[8] = {0x89, 'P', 'N', 'G', 0x0D, 0x0A, 0x1A, 0x0A};
    FireGfxOut out, z;
    uint8_t header[13];
    size_t rowBytes = (size_t)width * 4 + 1, x, y;
    uint8_t* raw = (uint8_t*)malloc(rowBytes * (size_t)height + 1);
    *size = 0;
    if (raw == NULL) return NULL;
    for (y = 0; y < (size_t)height; y++) {
        uint8_t* row = raw + y * rowBytes;
        row[0] = 0;   /* filter: none */
        for (x = 0; x < (size_t)width; x++) {
            uint32_t p = rgba[y * (size_t)width + x];
            row[1 + x * 4] = (uint8_t)p; row[2 + x * 4] = (uint8_t)(p >> 8); row[3 + x * 4] = (uint8_t)(p >> 16); row[4 + x * 4] = (uint8_t)(p >> 24);
        }
    }
    memset(&out, 0, sizeof out);
    memset(&z, 0, sizeof z);
    if (!fire_gfx_zlibDeflate(raw, rowBytes * (size_t)height, &z)) { free(raw); free(z.data); return NULL; }
    free(raw);
    header[0] = (uint8_t)(width >> 24); header[1] = (uint8_t)(width >> 16); header[2] = (uint8_t)(width >> 8); header[3] = (uint8_t)width;
    header[4] = (uint8_t)(height >> 24); header[5] = (uint8_t)(height >> 16); header[6] = (uint8_t)(height >> 8); header[7] = (uint8_t)height;
    header[8] = 8; header[9] = 6; header[10] = 0; header[11] = 0; header[12] = 0;
    fire_gfx_outBytes(&out, signature, 8);
    fire_gfx_outChunk(&out, "IHDR", header, 13);
    fire_gfx_outChunk(&out, "IDAT", z.data, z.n);
    fire_gfx_outChunk(&out, "IEND", NULL, 0);
    free(z.data);
    if (out.failed) { free(out.data); return NULL; }
    *size = out.n;
    return out.data;
}
/* A BMP file of the pixels: 32 bits (B, G, R, A), from the bottom row up, BITMAPINFOHEADER - what the clipboard of Windows takes (CF_DIB). malloc; NULL without memory. */
static uint8_t* fire_gfx_encodeBmp(const uint32_t* rgba, int width, int height, size_t* size) {
    size_t pixels = (size_t)width * (size_t)height * 4, total = 54 + pixels, i, x, y;
    uint8_t* d = (uint8_t*)calloc(total, 1);
    *size = 0;
    if (d == NULL) return NULL;
    d[0] = 'B'; d[1] = 'M';
    for (i = 0; i < 4; i++) { d[2 + i] = (uint8_t)(total >> (8 * i)); d[10 + i] = (uint8_t)(54 >> (8 * i)); d[14 + i] = (uint8_t)(40 >> (8 * i));
        d[18 + i] = (uint8_t)((uint32_t)width >> (8 * i)); d[22 + i] = (uint8_t)((uint32_t)height >> (8 * i)); d[34 + i] = (uint8_t)(pixels >> (8 * i)); }
    d[26] = 1; d[28] = 32;
    for (y = 0; y < (size_t)height; y++) {
        const uint32_t* row = rgba + ((size_t)height - 1 - y) * (size_t)width;
        uint8_t* o = d + 54 + y * (size_t)width * 4;
        for (x = 0; x < (size_t)width; x++) { uint32_t p = row[x]; o[x * 4] = (uint8_t)(p >> 16); o[x * 4 + 1] = (uint8_t)(p >> 8); o[x * 4 + 2] = (uint8_t)p; o[x * 4 + 3] = (uint8_t)(p >> 24); }
    }
    *size = total;
    return d;
}

#undef FIRE_IMG_FAIL

#endif
