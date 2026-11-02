/* TrueType outlines for the renderer: reads a .ttf (glyf outlines: simple and compound glyphs, cmap formats 4, 6 and 12, horizontal metrics, the 'kern' table), measures text and rasterises
 * glyphs into 8 bit coverage images with exact area coverage (anti-aliased, no hinting).
 *
 * This is a port to C of libschrift 0.10.2 (https://github.com/tomolt/libschrift, (c) 2019-2022 Thomas Oltmann and contributors, ISC license, see LICENSE-libschrift.md), written
 * to be read like the original and to give the same pixels. Differences: it works on a block of memory only (no file mapping), every read of the font is bounds checked (a damaged font
 * gives a "no glyph" result and no stray memory access), the buffers grow with realloc and the code points are UTF-16 aware at the call sites. The same algorithm exists in C#
 * (fire.Terminal/TrueType/TrueTypeFont.cs) for the virtual machine, and the tests compare both with the original: glyph images must be equal.
 *
 *
 * The license of libschrift (the original), which applies to this port:
 *
 *   ISC License
 *
 *   (c) 2019-2022 Thomas Oltmann and contributors
 *
 *   Permission to use, copy, modify, and/or distribute this software for any
 *   purpose with or without fee is hereby granted, provided that the above
 *   copyright notice and this permission notice appear in all copies.
 *
 *   THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES
 *   WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF
 *   MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR
 *   ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
 *   WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN
 *   ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF
 *   OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.
 */

/* Floating point: everything is `double` as in the original; a compiler that fuses multiply and add would change the last bit of some coverage values, so that is switched off
 * here - for the rest of the graphics sources (the text layout and the slicer as well; GCC: until the end of fire_bridge_graphics.h, which restores the options). */
#ifndef FIRE_GFX_TTF_H
#define FIRE_GFX_TTF_H

#include <math.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#if defined(__clang__)
#pragma clang fp contract(off)
#elif defined(__GNUC__)
#pragma GCC push_options
#pragma GCC optimize("fp-contract=off")
#endif

typedef struct FireTtfGMetrics { double advanceWidth, leftSideBearing; int yOffset, minWidth, minHeight, xOffset; } FireTtfGMetrics;   /* xOffset: the left edge of the glyph image from the pen position */
typedef struct FireTtfLMetrics { double ascender, descender, lineGap; } FireTtfLMetrics;

typedef struct FireTtfFont {
    uint8_t* mem;
    size_t size;
    uint32_t unitsPerEm, numLongHmtx;
    int locaFormat;
} FireTtfFont;

typedef struct FireTtfPoint { double x, y; } FireTtfPoint;
typedef struct FireTtfLine { uint16_t beg, end; } FireTtfLine;
typedef struct FireTtfCurve { uint16_t beg, end, ctrl; } FireTtfCurve;
typedef struct FireTtfOutline {
    FireTtfPoint* points; size_t numPoints, capPoints;
    FireTtfCurve* curves; size_t numCurves, capCurves;
    FireTtfLine* lines; size_t numLines, capLines;
} FireTtfOutline;
typedef struct FireTtfCell { double area, cover; } FireTtfCell;

static void fire_ttf_init(FireTtfFont* f) { f->mem = NULL; f->size = 0; f->unitsPerEm = 0; f->numLongHmtx = 0; f->locaFormat = 0; }
static void fire_ttf_free(FireTtfFont* f) { free(f->mem); fire_ttf_init(f); }

static int fire_ttf_safe(const FireTtfFont* f, uint32_t offset, uint32_t margin) {
    uint32_t size = (uint32_t)f->size;
    if (offset > size) return 0;
    return size - offset >= margin;
}
static uint8_t fire_ttf_getu8(const FireTtfFont* f, uint32_t o) { return o < f->size ? f->mem[o] : 0; }
static int fire_ttf_getu8s(const FireTtfFont* f, uint32_t o) { return (int8_t)fire_ttf_getu8(f, o); }
static uint16_t fire_ttf_getu16(const FireTtfFont* f, uint32_t o) { return (uint16_t)((fire_ttf_getu8(f, o) << 8) | fire_ttf_getu8(f, o + 1)); }
static int fire_ttf_geti16(const FireTtfFont* f, uint32_t o) { return (int16_t)fire_ttf_getu16(f, o); }
static uint32_t fire_ttf_getu32(const FireTtfFont* f, uint32_t o) {
    return ((uint32_t)fire_ttf_getu8(f, o) << 24) | ((uint32_t)fire_ttf_getu8(f, o + 1) << 16) | ((uint32_t)fire_ttf_getu8(f, o + 2) << 8) | fire_ttf_getu8(f, o + 3);
}

/* (a text that grows: the UTF-8 of a name) */
typedef struct FireTtfText { char* s; size_t n, cap; } FireTtfText;
static void fire_ttf_textPush(FireTtfText* t, char c) {
    if (t->n + 2 > t->cap) {
        size_t cap = t->cap ? t->cap * 2 : 32;
        char* grown = (char*)realloc(t->s, cap);
        if (grown == NULL) return;
        t->s = grown;
        t->cap = cap;
    }
    t->s[t->n++] = c;
    t->s[t->n] = 0;
}

static void fire_ttf_utf16be(const FireTtfFont* f, uint32_t o, unsigned length, FireTtfText* s) {
    unsigned i;
    for (i = 0; i + 1 < length; i += 2) {
        uint32_t c = fire_ttf_getu16(f, o + i);
        if (c >= 0xD800 && c <= 0xDBFF && i + 3 < length) { uint32_t d = fire_ttf_getu16(f, o + i + 2); if (d >= 0xDC00 && d <= 0xDFFF) { c = 0x10000 + ((c - 0xD800) << 10) + (d - 0xDC00); i += 2; } }
        if (c < 0x80) fire_ttf_textPush(s, (char)c);
        else if (c < 0x800) { fire_ttf_textPush(s, (char)(0xC0 | (c >> 6))); fire_ttf_textPush(s, (char)(0x80 | (c & 0x3F))); }
        else if (c < 0x10000) { fire_ttf_textPush(s, (char)(0xE0 | (c >> 12))); fire_ttf_textPush(s, (char)(0x80 | ((c >> 6) & 0x3F))); fire_ttf_textPush(s, (char)(0x80 | (c & 0x3F))); }
        else { fire_ttf_textPush(s, (char)(0xF0 | (c >> 18))); fire_ttf_textPush(s, (char)(0x80 | ((c >> 12) & 0x3F))); fire_ttf_textPush(s, (char)(0x80 | ((c >> 6) & 0x3F))); fire_ttf_textPush(s, (char)(0x80 | (c & 0x3F))); }
    }
}

static int fire_ttf_gettable(const FireTtfFont* f, const char* tag, uint32_t* offset) {
    unsigned numTables = fire_ttf_getu16(f, 4);
    uint32_t key;
    size_t lo = 0, hi = numTables;
    if (!fire_ttf_safe(f, 12, (uint32_t)numTables * 16)) return 0;
    /* the table directory is sorted by tag: binary search */
    key = ((uint32_t)(uint8_t)tag[0] << 24) | ((uint32_t)(uint8_t)tag[1] << 16) | ((uint32_t)(uint8_t)tag[2] << 8) | (uint8_t)tag[3];
    while (lo < hi) {
        size_t mid = lo + (hi - lo) / 2;
        uint32_t k = fire_ttf_getu32(f, 12 + (uint32_t)mid * 16);
        if (k < key) lo = mid + 1;
        else if (k > key) hi = mid;
        else { *offset = fire_ttf_getu32(f, 12 + (uint32_t)mid * 16 + 8); return 1; }
    }
    return 0;
}

static int fire_ttf_initTables(FireTtfFont* f) {
    uint32_t scalerType, head, hhea;
    if (!fire_ttf_safe(f, 0, 12)) return 0;
    scalerType = fire_ttf_getu32(f, 0);
    if (scalerType != 0x00010000 && scalerType != 0x74727565) return 0;
    if (!fire_ttf_gettable(f, "head", &head) || !fire_ttf_safe(f, head, 54)) return 0;
    f->unitsPerEm = fire_ttf_getu16(f, head + 18);
    f->locaFormat = fire_ttf_geti16(f, head + 50);
    if (f->unitsPerEm == 0) return 0;
    if (!fire_ttf_gettable(f, "hhea", &hhea) || !fire_ttf_safe(f, hhea, 36)) return 0;
    f->numLongHmtx = fire_ttf_getu16(f, hhea + 34);
    return 1;
}

/* Takes a copy of the font file. 0: it is not a TrueType font (or too damaged to read the tables we need). */
static int fire_ttf_load(FireTtfFont* f, const uint8_t* data, size_t size) {
    if (size > 0xFFFFFFFFu) return 0;
    free(f->mem);
    f->mem = (uint8_t*)malloc(size ? size : 1);
    f->size = 0;
    if (f->mem == NULL) return 0;
    if (size) memcpy(f->mem, data, size);
    f->size = size;
    return fire_ttf_initTables(f);
}

static uint32_t fire_ttf_unitsPerEm(const FireTtfFont* f) { return f->unitsPerEm; }

/* The ascent, descent (negative) and line gap for a size in pixels (the size is the em: units per em map to `size` pixels). */
static int fire_ttf_lmetrics(const FireTtfFont* f, double size, FireTtfLMetrics* m) {
    uint32_t hhea;
    double factor;
    m->ascender = 0; m->descender = 0; m->lineGap = 0;
    if (!fire_ttf_gettable(f, "hhea", &hhea)) return 0;
    if (!fire_ttf_safe(f, hhea, 36)) return 0;
    factor = size / f->unitsPerEm;
    m->ascender = fire_ttf_geti16(f, hhea + 4) * factor;
    m->descender = fire_ttf_geti16(f, hhea + 6) * factor;
    m->lineGap = fire_ttf_geti16(f, hhea + 8) * factor;
    return 1;
}

/* ---- cmap --------------------------------------------------------------------------------------------------------------------------------- */
static int fire_ttf_cmapFmt4(const FireTtfFont* f, uint32_t table, uint32_t charCode, uint32_t* glyph) {
    uint32_t shortCode = charCode, segCountX2, endCodes, startCodes, idDeltas, idRangeOffsets, segIdxX2, startCode, idDelta, idRangeOffset, idOffset, id;
    size_t low, high;
    if (charCode > 0xFFFF) { *glyph = 0; return 1; }
    if (!fire_ttf_safe(f, table, 8)) return 0;
    segCountX2 = fire_ttf_getu16(f, table);
    if ((segCountX2 & 1) || !segCountX2) return 0;
    endCodes = table + 8; startCodes = endCodes + segCountX2 + 2; idDeltas = startCodes + segCountX2; idRangeOffsets = idDeltas + segCountX2;
    if (!fire_ttf_safe(f, idRangeOffsets, segCountX2)) return 0;
    /* the segment that contains the code: the first whose end code is not lower (binary search over the end codes) */
    low = 0; high = segCountX2 / 2 - 1;
    while (low != high) {
        size_t mid = low + (high - low) / 2;
        if (fire_ttf_getu16(f, endCodes + (uint32_t)mid * 2) < shortCode) low = mid + 1; else high = mid;
    }
    segIdxX2 = (uint32_t)low * 2;
    startCode = fire_ttf_getu16(f, startCodes + segIdxX2);
    if (startCode > shortCode) return 1;   /* (glyph stays 0) */
    idDelta = fire_ttf_getu16(f, idDeltas + segIdxX2);
    idRangeOffset = fire_ttf_getu16(f, idRangeOffsets + segIdxX2);
    if (!idRangeOffset) { *glyph = (shortCode + idDelta) & 0xFFFF; return 1; }
    idOffset = idRangeOffsets + segIdxX2 + idRangeOffset + 2U * (shortCode - startCode);
    if (!fire_ttf_safe(f, idOffset, 2)) return 0;
    id = fire_ttf_getu16(f, idOffset);
    *glyph = id ? (id + idDelta) & 0xFFFF : 0;
    return 1;
}
static int fire_ttf_cmapFmt6(const FireTtfFont* f, uint32_t table, uint32_t charCode, uint32_t* glyph) {
    unsigned firstCode, entryCount;
    if (charCode > 0xFFFF) { *glyph = 0; return 1; }
    if (!fire_ttf_safe(f, table, 4)) return 0;
    firstCode = fire_ttf_getu16(f, table); entryCount = fire_ttf_getu16(f, table + 2);
    if (!fire_ttf_safe(f, table, 4 + 2 * entryCount)) return 0;
    if (charCode < firstCode) return 0;
    charCode -= firstCode;
    if (!(charCode < entryCount)) return 0;
    *glyph = fire_ttf_getu16(f, table + 4 + 2 * charCode);
    return 1;
}
static int fire_ttf_cmapFmt12(const FireTtfFont* f, uint32_t table, uint32_t charCode, uint32_t* glyph) {
    uint32_t len, numEntries, i;
    *glyph = 0;
    if (!fire_ttf_safe(f, table, 16)) return 0;
    len = fire_ttf_getu32(f, table + 4);
    if (len < 16) return 0;
    if (!fire_ttf_safe(f, table, len)) return 0;
    numEntries = fire_ttf_getu32(f, table + 12);
    if (numEntries > (len - 16) / 12) return 0;
    for (i = 0; i < numEntries; ++i) {
        uint32_t firstCode = fire_ttf_getu32(f, table + i * 12 + 16), lastCode = fire_ttf_getu32(f, table + i * 12 + 16 + 4);
        if (charCode < firstCode || charCode > lastCode) continue;
        *glyph = (charCode - firstCode) + fire_ttf_getu32(f, table + i * 12 + 16 + 8);
        return 1;
    }
    return 1;
}
static int fire_ttf_glyphId(const FireTtfFont* f, uint32_t charCode, uint32_t* glyph) {
    uint32_t cmap;
    unsigned numEntries, idx;
    *glyph = 0;
    if (!fire_ttf_gettable(f, "cmap", &cmap)) return 0;
    if (!fire_ttf_safe(f, cmap, 4)) return 0;
    numEntries = fire_ttf_getu16(f, cmap + 2);
    if (!fire_ttf_safe(f, cmap, 4 + numEntries * 8)) return 0;
    for (idx = 0; idx < numEntries; ++idx) {   /* a map of the whole repertoire first */
        uint32_t entry = cmap + 4 + idx * 8;
        int type = fire_ttf_getu16(f, entry) * 0100 + fire_ttf_getu16(f, entry + 2);
        if (type == 0004 || type == 0312) {
            uint32_t table = cmap + fire_ttf_getu32(f, entry + 4);
            if (!fire_ttf_safe(f, table, 8)) return 0;
            if (fire_ttf_getu16(f, table) == 12) return fire_ttf_cmapFmt12(f, table, charCode, glyph);
            return 0;
        }
    }
    for (idx = 0; idx < numEntries; ++idx) {   /* then one of the BMP */
        uint32_t entry = cmap + 4 + idx * 8;
        int type = fire_ttf_getu16(f, entry) * 0100 + fire_ttf_getu16(f, entry + 2);
        if (type == 0003 || type == 0301) {
            uint32_t table = cmap + fire_ttf_getu32(f, entry + 4);
            if (!fire_ttf_safe(f, table, 6)) return 0;
            switch (fire_ttf_getu16(f, table)) {
                case 4: return fire_ttf_cmapFmt4(f, table + 6, charCode, glyph);
                case 6: return fire_ttf_cmapFmt6(f, table + 6, charCode, glyph);
                default: return 0;
            }
        }
    }
    return 0;
}

/* The glyph for a code point (0 if the font has none; 0 as the result if the cmap could not be read). */
static int fire_ttf_lookup(const FireTtfFont* f, uint32_t codepoint, uint32_t* glyph) { return fire_ttf_glyphId(f, codepoint, glyph); }

/* ---- metrics and outlines ----------------------------------------------------------------------------------------------------------------- */
static int fire_ttf_horMetrics(const FireTtfFont* f, uint32_t glyph, int* advanceWidth, int* leftSideBearing) {
    uint32_t hmtx, boundary, offset;
    if (!fire_ttf_gettable(f, "hmtx", &hmtx)) return 0;
    if (glyph < f->numLongHmtx) {
        offset = hmtx + 4 * glyph;
        if (!fire_ttf_safe(f, offset, 4)) return 0;
        *advanceWidth = fire_ttf_getu16(f, offset);
        *leftSideBearing = fire_ttf_geti16(f, offset + 2);
        return 1;
    }
    boundary = hmtx + 4U * f->numLongHmtx;
    if (boundary < 4) return 0;
    offset = boundary - 4;
    if (!fire_ttf_safe(f, offset, 4)) return 0;
    *advanceWidth = fire_ttf_getu16(f, offset);
    offset = boundary + 2 * (glyph - f->numLongHmtx);
    if (!fire_ttf_safe(f, offset, 2)) return 0;
    *leftSideBearing = fire_ttf_geti16(f, offset);
    return 1;
}
static int fire_ttf_glyphBbox(const FireTtfFont* f, double size, uint32_t outline, int box[4]) {
    double xScale, yScale;
    if (!fire_ttf_safe(f, outline, 10)) return 0;
    box[0] = fire_ttf_geti16(f, outline + 2); box[1] = fire_ttf_geti16(f, outline + 4); box[2] = fire_ttf_geti16(f, outline + 6); box[3] = fire_ttf_geti16(f, outline + 8);
    if (box[2] <= box[0] || box[3] <= box[1]) return 0;
    xScale = size / f->unitsPerEm; yScale = size / f->unitsPerEm;
    box[0] = (int)floor(box[0] * xScale + 0.0);
    box[1] = (int)floor(box[1] * yScale + 0.0);
    box[2] = (int)ceil(box[2] * xScale + 0.0);
    box[3] = (int)ceil(box[3] * yScale + 0.0);
    return 1;
}
static int fire_ttf_outlineOffset(const FireTtfFont* f, uint32_t glyph, uint32_t* offset) {
    uint32_t loca, glyf, base, thisOff, next;
    if (!fire_ttf_gettable(f, "loca", &loca) || !fire_ttf_gettable(f, "glyf", &glyf)) return 0;
    if (f->locaFormat == 0) {
        base = loca + 2 * glyph;
        if (!fire_ttf_safe(f, base, 4)) return 0;
        thisOff = 2U * fire_ttf_getu16(f, base);
        next = 2U * fire_ttf_getu16(f, base + 2);
    } else {
        base = loca + 4 * glyph;
        if (!fire_ttf_safe(f, base, 8)) return 0;
        thisOff = fire_ttf_getu32(f, base);
        next = fire_ttf_getu32(f, base + 4);
    }
    *offset = thisOff == next ? 0 : glyf + thisOff;
    return 1;
}

static int fire_ttf_gmetrics(const FireTtfFont* f, double size, uint32_t glyph, FireTtfGMetrics* m) {
    double xScale = size / f->unitsPerEm;
    int adv, lsb, bbox[4];
    uint32_t outline;
    m->advanceWidth = 0; m->leftSideBearing = 0; m->yOffset = 0; m->minWidth = 0; m->minHeight = 0; m->xOffset = 0;
    if (!fire_ttf_horMetrics(f, glyph, &adv, &lsb)) return 0;
    m->advanceWidth = adv * xScale;
    m->leftSideBearing = lsb * xScale + 0.0;
    if (!fire_ttf_outlineOffset(f, glyph, &outline)) return 0;
    if (!outline) return 1;
    if (!fire_ttf_glyphBbox(f, size, outline, bbox)) return 0;
    m->minWidth = bbox[2] - bbox[0] + 1;
    m->minHeight = bbox[3] - bbox[1] + 1;
    m->yOffset = -bbox[3];   /* (the y axis points down) */
    m->xOffset = bbox[0];
    return 1;
}

/* The horizontal kerning between two glyphs in pixels (the 'kern' table; 0 if there is none). */
static int fire_ttf_kerning(const FireTtfFont* f, double size, uint32_t left, uint32_t right, double* xShift) {
    uint32_t offset;
    unsigned numTables;
    double xs = 0;
    *xShift = 0;
    if (!fire_ttf_gettable(f, "kern", &offset)) return 1;
    if (!fire_ttf_safe(f, offset, 4)) return 0;
    if (fire_ttf_getu16(f, offset) != 0) return 1;
    numTables = fire_ttf_getu16(f, offset + 2);
    offset += 4;
    while (numTables > 0) {
        unsigned length, format, flags;
        if (!fire_ttf_safe(f, offset, 6)) return 0;
        length = fire_ttf_getu16(f, offset + 2); format = fire_ttf_getu8(f, offset + 4); flags = fire_ttf_getu8(f, offset + 5);
        offset += 6;
        if (format == 0 && (flags & 1) && !(flags & 2)) {
            unsigned numPairs;
            uint32_t key;
            size_t lo, hi;
            if (!fire_ttf_safe(f, offset, 8)) return 0;
            numPairs = fire_ttf_getu16(f, offset);
            offset += 8;
            if (!fire_ttf_safe(f, offset, (uint32_t)numPairs * 6)) return 0;
            /* binary search for the pair (left, right) as a 32 bit number */
            key = (left << 16) | (right & 0xFFFF);
            lo = 0; hi = numPairs;
            while (lo < hi) {
                size_t mid = lo + (hi - lo) / 2;
                uint32_t k = fire_ttf_getu32(f, offset + (uint32_t)mid * 6);
                if (k < key) lo = mid + 1;
                else if (k > key) hi = mid;
                else {
                    int value = fire_ttf_geti16(f, offset + (uint32_t)mid * 6 + 4);
                    if (!(flags & 4)) xs += value;   /* (cross stream kerning moves the glyph up or down: not used) */
                    break;
                }
            }
        }
        offset += length;
        --numTables;
    }
    *xShift = xs / f->unitsPerEm * size;
    return 1;
}

/* A name from the 'name' table in UTF-8 (nameId 1 family, 2 style, 4 full name, 6 PostScript name), malloc'd; "" if there is none (NULL only without memory). */
static char* fire_ttf_name(const FireTtfFont* f, int nameId) {
    uint32_t table, strings;
    unsigned count, i;
    FireTtfText best = {NULL, 0, 0};
    int bestScore = -1;
    if (fire_ttf_gettable(f, "name", &table) && fire_ttf_safe(f, table, 6)) {
        count = fire_ttf_getu16(f, table + 2);
        strings = table + fire_ttf_getu16(f, table + 4);
        for (i = 0; i < count; i++) {
            uint32_t rec = table + 6 + i * 12;
            unsigned platform, encoding, language, id, length, offset;
            int score = -1;
            FireTtfText text = {NULL, 0, 0};
            if (!fire_ttf_safe(f, rec, 12)) break;
            platform = fire_ttf_getu16(f, rec); encoding = fire_ttf_getu16(f, rec + 2); language = fire_ttf_getu16(f, rec + 4); id = fire_ttf_getu16(f, rec + 6);
            length = fire_ttf_getu16(f, rec + 8); offset = fire_ttf_getu16(f, rec + 10);
            if ((int)id != nameId || !fire_ttf_safe(f, strings + offset, length)) continue;
            if (platform == 3 && (encoding == 1 || encoding == 10)) { score = language == 0x409 ? 3 : 2; fire_ttf_utf16be(f, strings + offset, length, &text); }
            else if (platform == 0) { score = 1; fire_ttf_utf16be(f, strings + offset, length, &text); }
            else if (platform == 1 && encoding == 0) {
                unsigned k;
                score = 0;
                for (k = 0; k < length; k++) {
                    uint8_t c = fire_ttf_getu8(f, strings + offset + k);
                    if (c < 0x80) fire_ttf_textPush(&text, (char)c);
                    else { fire_ttf_textPush(&text, (char)(0xC0 | (c >> 6))); fire_ttf_textPush(&text, (char)(0x80 | (c & 0x3F))); }
                }
            }
            if (score > bestScore && text.n > 0) { bestScore = score; free(best.s); best = text; }
            else free(text.s);
        }
    }
    if (best.s == NULL) { best.s = (char*)malloc(1); if (best.s) best.s[0] = 0; }
    return best.s;
}

static FireTtfPoint fire_ttf_midpoint(FireTtfPoint a, FireTtfPoint b) { FireTtfPoint p; p.x = 0.5 * (a.x + b.x); p.y = 0.5 * (a.y + b.y); return p; }
static int fire_ttf_growOk(const FireTtfOutline* o) { return o->numPoints < 0xFFFF && o->numCurves < 0xFFFF && o->numLines < 0xFFFF; }

/* (the arrays of an outline: 0 without memory) */
static int fire_ttf_reserve(void** items, size_t* cap, size_t need, size_t size) {
    size_t n;
    void* grown;
    if (need <= *cap) return 1;
    n = *cap ? *cap : 64;
    while (n < need) n *= 2;
    grown = realloc(*items, n * size);
    if (grown == NULL) return 0;
    *items = grown;
    *cap = n;
    return 1;
}
static int fire_ttf_pushPoint(FireTtfOutline* o, FireTtfPoint p) {
    if (!fire_ttf_reserve((void**)&o->points, &o->capPoints, o->numPoints + 1, sizeof(FireTtfPoint))) return 0;
    o->points[o->numPoints++] = p;
    return 1;
}
static int fire_ttf_pushCurve(FireTtfOutline* o, uint16_t beg, uint16_t end, uint16_t ctrl) {
    if (!fire_ttf_reserve((void**)&o->curves, &o->capCurves, o->numCurves + 1, sizeof(FireTtfCurve))) return 0;
    o->curves[o->numCurves].beg = beg; o->curves[o->numCurves].end = end; o->curves[o->numCurves].ctrl = ctrl;
    o->numCurves++;
    return 1;
}
static int fire_ttf_pushLine(FireTtfOutline* o, uint16_t beg, uint16_t end) {
    if (!fire_ttf_reserve((void**)&o->lines, &o->capLines, o->numLines + 1, sizeof(FireTtfLine))) return 0;
    o->lines[o->numLines].beg = beg; o->lines[o->numLines].end = end;
    o->numLines++;
    return 1;
}
static void fire_ttf_outlineFree(FireTtfOutline* o) { free(o->points); free(o->curves); free(o->lines); memset(o, 0, sizeof *o); }

static int fire_ttf_simpleFlags(const FireTtfFont* f, uint32_t* offset, unsigned numPts, uint8_t* flags) {
    uint32_t off = *offset;
    uint8_t value = 0, repeat = 0;
    unsigned i;
    for (i = 0; i < numPts; ++i) {
        if (repeat) --repeat;
        else {
            if (!fire_ttf_safe(f, off, 1)) return 0;
            value = fire_ttf_getu8(f, off++);
            if (value & 0x08) {
                if (!fire_ttf_safe(f, off, 1)) return 0;
                repeat = fire_ttf_getu8(f, off++);
            }
        }
        flags[i] = value;
    }
    *offset = off;
    return 1;
}
static int fire_ttf_simplePoints(const FireTtfFont* f, uint32_t offset, unsigned numPts, const uint8_t* flags, FireTtfPoint* points) {
    long accum = 0;
    unsigned i;
    for (i = 0; i < numPts; ++i) {
        if (flags[i] & 0x02) {
            long value, bit;
            if (!fire_ttf_safe(f, offset, 1)) return 0;
            value = (long)fire_ttf_getu8(f, offset++);
            bit = !!(flags[i] & 0x10);
            accum -= (value ^ -bit) + bit;
        } else if (!(flags[i] & 0x10)) {
            if (!fire_ttf_safe(f, offset, 2)) return 0;
            accum += fire_ttf_geti16(f, offset);
            offset += 2;
        }
        points[i].x = (double)accum;
    }
    accum = 0;
    for (i = 0; i < numPts; ++i) {
        if (flags[i] & 0x04) {
            long value, bit;
            if (!fire_ttf_safe(f, offset, 1)) return 0;
            value = (long)fire_ttf_getu8(f, offset++);
            bit = !!(flags[i] & 0x20);
            accum -= (value ^ -bit) + bit;
        } else if (!(flags[i] & 0x20)) {
            if (!fire_ttf_safe(f, offset, 2)) return 0;
            accum += fire_ttf_geti16(f, offset);
            offset += 2;
        }
        points[i].y = (double)accum;
    }
    return 1;
}
static int fire_ttf_decodeContour(const uint8_t* flags, unsigned basePoint, unsigned count, FireTtfOutline* outl) {
    unsigned looseEnd, beg, ctrl = 0, i;
    int gotCtrl = 0;
    if (count < 2) return 1;
    if (flags[0] & 0x01) {
        looseEnd = basePoint++;
        ++flags;
        --count;
    } else if (flags[count - 1] & 0x01) {
        looseEnd = basePoint + --count;
    } else {
        FireTtfPoint mid;
        if (!fire_ttf_growOk(outl)) return 0;
        looseEnd = (unsigned)outl->numPoints;
        mid = fire_ttf_midpoint(outl->points[basePoint], outl->points[basePoint + count - 1]);
        if (!fire_ttf_pushPoint(outl, mid)) return 0;
    }
    beg = looseEnd;
    for (i = 0; i < count; ++i) {
        unsigned cur = basePoint + i;
        if (flags[i] & 0x01) {
            if (!fire_ttf_growOk(outl)) return 0;
            if (gotCtrl) { if (!fire_ttf_pushCurve(outl, (uint16_t)beg, (uint16_t)cur, (uint16_t)ctrl)) return 0; }
            else if (!fire_ttf_pushLine(outl, (uint16_t)beg, (uint16_t)cur)) return 0;
            beg = cur;
            gotCtrl = 0;
        } else {
            if (gotCtrl) {
                unsigned center;
                FireTtfPoint mid;
                if (!fire_ttf_growOk(outl)) return 0;
                center = (unsigned)outl->numPoints;
                mid = fire_ttf_midpoint(outl->points[ctrl], outl->points[cur]);
                if (!fire_ttf_pushPoint(outl, mid)) return 0;
                if (!fire_ttf_pushCurve(outl, (uint16_t)beg, (uint16_t)center, (uint16_t)ctrl)) return 0;
                beg = center;
            }
            ctrl = cur;
            gotCtrl = 1;
        }
    }
    if (!fire_ttf_growOk(outl)) return 0;
    if (gotCtrl) return fire_ttf_pushCurve(outl, (uint16_t)beg, (uint16_t)looseEnd, (uint16_t)ctrl);
    return fire_ttf_pushLine(outl, (uint16_t)beg, (uint16_t)looseEnd);
}
static int fire_ttf_simpleOutline(const FireTtfFont* f, uint32_t offset, unsigned numContours, FireTtfOutline* outl) {
    unsigned basePoint = (unsigned)outl->numPoints, numPts, i, beg;
    unsigned* endPts;
    uint8_t* flags;
    int ok = 0;
    if (!fire_ttf_safe(f, offset, numContours * 2 + 2)) return 0;
    numPts = fire_ttf_getu16(f, offset + (numContours - 1) * 2);
    if (numPts >= 0xFFFF) return 0;
    numPts++;
    if (outl->numPoints > 0xFFFFu - numPts) return 0;
    endPts = (unsigned*)malloc(sizeof(unsigned) * numContours);
    flags = (uint8_t*)calloc(numPts, 1);
    if (endPts == NULL || flags == NULL) goto done;
    for (i = 0; i < numContours; ++i) { endPts[i] = fire_ttf_getu16(f, offset); offset += 2; }
    for (i = 0; i + 1 < numContours; ++i) if (endPts[i + 1] < endPts[i] + 1) goto done;   /* (falling end points: damaged or malicious) */
    offset += 2U + fire_ttf_getu16(f, offset);
    if (!fire_ttf_simpleFlags(f, &offset, numPts, flags)) goto done;
    if (!fire_ttf_reserve((void**)&outl->points, &outl->capPoints, (size_t)basePoint + numPts, sizeof(FireTtfPoint))) goto done;
    memset(outl->points + basePoint, 0, sizeof(FireTtfPoint) * numPts);
    outl->numPoints = (size_t)basePoint + numPts;
    if (!fire_ttf_simplePoints(f, offset, numPts, flags, outl->points + basePoint)) goto done;
    beg = 0;
    for (i = 0; i < numContours; ++i) {
        unsigned count;
        if (endPts[i] < beg || endPts[i] >= numPts) goto done;
        count = endPts[i] - beg + 1;
        if (!fire_ttf_decodeContour(flags + beg, basePoint + beg, count, outl)) goto done;
        beg = endPts[i] + 1;
    }
    ok = 1;
done:
    free(endPts);
    free(flags);
    return ok;
}

static void fire_ttf_transformPoints(FireTtfPoint* points, size_t n, const double trf[6]) {
    size_t i;
    for (i = 0; i < n; ++i) {
        FireTtfPoint pt = points[i];
        points[i].x = pt.x * trf[0] + pt.y * trf[2] + trf[4];
        points[i].y = pt.x * trf[1] + pt.y * trf[3] + trf[5];
    }
}

static int fire_ttf_decodeOutline(const FireTtfFont* f, uint32_t offset, int recDepth, FireTtfOutline* outl);

static int fire_ttf_compoundOutline(const FireTtfFont* f, uint32_t offset, int recDepth, FireTtfOutline* outl) {
    unsigned flags;
    if (recDepth >= 4) return 0;
    do {
        double local[6] = {0, 0, 0, 0, 0, 0};
        unsigned glyph;
        uint32_t outline;
        if (!fire_ttf_safe(f, offset, 4)) return 0;
        flags = fire_ttf_getu16(f, offset);
        glyph = fire_ttf_getu16(f, offset + 2);
        offset += 4;
        if (!(flags & 0x002)) return 0;   /* (matching points: not implemented, as in libschrift) */
        if (flags & 0x001) {
            if (!fire_ttf_safe(f, offset, 4)) return 0;
            local[4] = fire_ttf_geti16(f, offset);
            local[5] = fire_ttf_geti16(f, offset + 2);
            offset += 4;
        } else {
            if (!fire_ttf_safe(f, offset, 2)) return 0;
            local[4] = fire_ttf_getu8s(f, offset);
            local[5] = fire_ttf_getu8s(f, offset + 1);
            offset += 2;
        }
        if (flags & 0x008) {
            if (!fire_ttf_safe(f, offset, 2)) return 0;
            local[0] = fire_ttf_geti16(f, offset) / 16384.0;
            local[3] = local[0];
            offset += 2;
        } else if (flags & 0x040) {
            if (!fire_ttf_safe(f, offset, 4)) return 0;
            local[0] = fire_ttf_geti16(f, offset + 0) / 16384.0;
            local[3] = fire_ttf_geti16(f, offset + 2) / 16384.0;
            offset += 4;
        } else if (flags & 0x080) {
            if (!fire_ttf_safe(f, offset, 8)) return 0;
            local[0] = fire_ttf_geti16(f, offset + 0) / 16384.0;
            local[1] = fire_ttf_geti16(f, offset + 2) / 16384.0;
            local[2] = fire_ttf_geti16(f, offset + 4) / 16384.0;
            local[3] = fire_ttf_geti16(f, offset + 6) / 16384.0;
            offset += 8;
        } else {
            local[0] = 1.0;
            local[3] = 1.0;
        }
        if (!fire_ttf_outlineOffset(f, glyph, &outline)) return 0;
        if (outline) {
            size_t basePoint = outl->numPoints;
            if (!fire_ttf_decodeOutline(f, outline, recDepth + 1, outl)) return 0;
            fire_ttf_transformPoints(outl->points + basePoint, outl->numPoints - basePoint, local);
        }
    } while (flags & 0x020);
    return 1;
}
static int fire_ttf_decodeOutline(const FireTtfFont* f, uint32_t offset, int recDepth, FireTtfOutline* outl) {
    int numContours;
    if (!fire_ttf_safe(f, offset, 10)) return 0;
    numContours = fire_ttf_geti16(f, offset);
    if (numContours > 0) return fire_ttf_simpleOutline(f, offset + 10, (unsigned)numContours, outl);
    if (numContours < 0) return fire_ttf_compoundOutline(f, offset + 10, recDepth, outl);
    return 1;
}

/* ---- rasterising ---------------------------------------------------------------------------------------------------------------------------- */
static void fire_ttf_clipPoints(FireTtfOutline* o, int width, int height) {
    size_t i;
    for (i = 0; i < o->numPoints; i++) {
        FireTtfPoint pt = o->points[i];
        if (pt.x < 0.0) o->points[i].x = 0.0;
        if (pt.x >= width) o->points[i].x = nextafter((double)width, 0.0);
        if (pt.y < 0.0) o->points[i].y = 0.0;
        if (pt.y >= height) o->points[i].y = nextafter((double)height, 0.0);
    }
}
static int fire_ttf_isFlat(const FireTtfOutline* outl, FireTtfCurve curve) {
    const double maxArea2 = 2.0;
    FireTtfPoint a = outl->points[curve.beg], b = outl->points[curve.ctrl], c = outl->points[curve.end];
    FireTtfPoint g, h;
    double area2;
    g.x = b.x - a.x; g.y = b.y - a.y;
    h.x = c.x - a.x; h.y = c.y - a.y;
    area2 = fabs(g.x * h.y - h.x * g.y);
    return area2 <= maxArea2;
}
static int fire_ttf_tesselateCurve(FireTtfCurve curve, FireTtfOutline* outl) {
    enum { stackSize = 10 };
    FireTtfCurve stack[stackSize];
    unsigned top = 0;
    for (;;) {
        if (fire_ttf_isFlat(outl, curve) || top >= stackSize) {
            if (outl->numLines >= 0xFFFF) return 0;
            if (!fire_ttf_pushLine(outl, curve.beg, curve.end)) return 0;
            if (top == 0) break;
            curve = stack[--top];
        } else {
            uint16_t ctrl0, ctrl1, pivot;
            FireTtfPoint p;
            if (outl->numPoints + 3 > 0xFFFF) return 0;
            ctrl0 = (uint16_t)outl->numPoints;
            p = fire_ttf_midpoint(outl->points[curve.beg], outl->points[curve.ctrl]);
            if (!fire_ttf_pushPoint(outl, p)) return 0;
            ctrl1 = (uint16_t)outl->numPoints;
            p = fire_ttf_midpoint(outl->points[curve.ctrl], outl->points[curve.end]);
            if (!fire_ttf_pushPoint(outl, p)) return 0;
            pivot = (uint16_t)outl->numPoints;
            p = fire_ttf_midpoint(outl->points[ctrl0], outl->points[ctrl1]);
            if (!fire_ttf_pushPoint(outl, p)) return 0;
            stack[top].beg = curve.beg; stack[top].end = pivot; stack[top].ctrl = ctrl0;
            top++;
            curve.beg = pivot; curve.ctrl = ctrl1;   /* (curve.end stays) */
        }
    }
    return 1;
}
static int fire_ttf_fastFloor(double x) { int i = (int)x; return i - (i > x); }
static int fire_ttf_fastCeil(double x) { int i = (int)x; return i + (i < x); }
static int fire_ttf_sign(double x) { return (x > 0) - (x < 0); }
/* std::min of the original (`b < a ? b : a`) */
static double fire_ttf_min(double a, double b) { return b < a ? b : a; }

static void fire_ttf_drawLine(FireTtfCell* cells, size_t numCells, int width, FireTtfPoint origin, FireTtfPoint goal) {
    FireTtfPoint delta, crossingIncr, nextCrossing;
    int dirX, dirY, pixelX, pixelY, numSteps = 0, step;
    double nextDistance, halfDeltaX, prevDistance, xAverage, yDifference;
    long long at;
    FireTtfCell* cell;
    delta.x = goal.x - origin.x; delta.y = goal.y - origin.y;
    dirX = fire_ttf_sign(delta.x); dirY = fire_ttf_sign(delta.y);
    if (!dirY) return;
    crossingIncr.x = dirX ? fabs(1.0 / delta.x) : 1.0;
    crossingIncr.y = fabs(1.0 / delta.y);
    nextCrossing.x = 0; nextCrossing.y = 0;
    if (!dirX) {
        pixelX = fire_ttf_fastFloor(origin.x);
        nextCrossing.x = 100.0;
    } else if (dirX > 0) {
        pixelX = fire_ttf_fastFloor(origin.x);
        nextCrossing.x = (origin.x - pixelX) * crossingIncr.x;
        nextCrossing.x = crossingIncr.x - nextCrossing.x;
        numSteps += fire_ttf_fastCeil(goal.x) - fire_ttf_fastFloor(origin.x) - 1;
    } else {
        pixelX = fire_ttf_fastCeil(origin.x) - 1;
        nextCrossing.x = (origin.x - pixelX) * crossingIncr.x;
        numSteps += fire_ttf_fastCeil(origin.x) - fire_ttf_fastFloor(goal.x) - 1;
    }
    if (dirY > 0) {
        pixelY = fire_ttf_fastFloor(origin.y);
        nextCrossing.y = (origin.y - pixelY) * crossingIncr.y;
        nextCrossing.y = crossingIncr.y - nextCrossing.y;
        numSteps += fire_ttf_fastCeil(goal.y) - fire_ttf_fastFloor(origin.y) - 1;
    } else {
        pixelY = fire_ttf_fastCeil(origin.y) - 1;
        nextCrossing.y = (origin.y - pixelY) * crossingIncr.y;
        numSteps += fire_ttf_fastCeil(origin.y) - fire_ttf_fastFloor(goal.y) - 1;
    }
    nextDistance = fire_ttf_min(nextCrossing.x, nextCrossing.y);
    halfDeltaX = 0.5 * delta.x;
    prevDistance = 0.0;
    for (step = 0; step < numSteps; ++step) {
        int alongX;
        xAverage = origin.x + (prevDistance + nextDistance) * halfDeltaX;
        yDifference = (nextDistance - prevDistance) * delta.y;
        at = (long long)pixelY * width + pixelX;
        if (at < 0 || (unsigned long long)at >= numCells) return;   /* (cannot happen with clipped points; a safety net for damaged input) */
        cell = &cells[at];
        cell->cover += yDifference;
        xAverage -= (double)pixelX;
        cell->area += (1.0 - xAverage) * yDifference;
        prevDistance = nextDistance;
        alongX = nextCrossing.x < nextCrossing.y;
        pixelX += alongX ? dirX : 0;
        pixelY += alongX ? 0 : dirY;
        nextCrossing.x += alongX ? crossingIncr.x : 0.0;
        nextCrossing.y += alongX ? 0.0 : crossingIncr.y;
        nextDistance = fire_ttf_min(nextCrossing.x, nextCrossing.y);
    }
    xAverage = origin.x + (prevDistance + 1.0) * halfDeltaX;
    yDifference = (1.0 - prevDistance) * delta.y;
    at = (long long)pixelY * width + pixelX;
    if (at < 0 || (unsigned long long)at >= numCells) return;
    cell = &cells[at];
    cell->cover += yDifference;
    xAverage -= (double)pixelX;
    cell->area += (1.0 - xAverage) * yDifference;
}
static int fire_ttf_renderOutline(FireTtfOutline* outl, const double transform[6], uint8_t* pixels, int width, int height) {
    size_t numPixels, i, numCurves;
    FireTtfCell* cells;
    double accum = 0.0;
    if (width <= 0 || height <= 0) return 1;
    numPixels = (size_t)width * (size_t)height;
    cells = (FireTtfCell*)calloc(numPixels, sizeof(FireTtfCell));
    if (cells == NULL) return 0;
    fire_ttf_transformPoints(outl->points, outl->numPoints, transform);
    fire_ttf_clipPoints(outl, width, height);
    numCurves = outl->numCurves;
    for (i = 0; i < numCurves; ++i)   /* (the tesselation adds lines and points, not curves) */
        if (!fire_ttf_tesselateCurve(outl->curves[i], outl)) { free(cells); return 0; }
    for (i = 0; i < outl->numLines; i++) fire_ttf_drawLine(cells, numPixels, width, outl->points[outl->lines[i].beg], outl->points[outl->lines[i].end]);
    for (i = 0; i < numPixels; ++i) {
        double value = fabs(accum + cells[i].area);
        value = fire_ttf_min(value, 1.0);
        value = value * 255.0 + 0.5;
        pixels[i] = (uint8_t)value;
        accum += cells[i].cover;
    }
    free(cells);
    return 1;
}

/* Renders the glyph into `pixels` (width * height bytes, coverage 0..255): width and height are those of fire_ttf_gmetrics (the width rounded up to a multiple of 4 is fine).
 * 1: drawn (or the glyph has no outline). */
static int fire_ttf_render(const FireTtfFont* f, double size, uint32_t glyph, uint8_t* pixels, int width, int height) {
    uint32_t outline;
    int bbox[4], ok;
    double transform[6];
    FireTtfOutline outl;
    if (!fire_ttf_outlineOffset(f, glyph, &outline)) return 0;
    if (!outline) return 1;
    if (!fire_ttf_glyphBbox(f, size, outline, bbox)) return 0;
    transform[0] = size / f->unitsPerEm;
    transform[1] = 0.0;
    transform[2] = 0.0;
    transform[4] = 0.0 - bbox[0];
    transform[3] = -size / f->unitsPerEm;
    transform[5] = bbox[3] - 0.0;
    memset(&outl, 0, sizeof outl);
    ok = fire_ttf_decodeOutline(f, outline, 0, &outl) && fire_ttf_renderOutline(&outl, transform, pixels, width, height);
    fire_ttf_outlineFree(&outl);
    return ok;
}

#endif
