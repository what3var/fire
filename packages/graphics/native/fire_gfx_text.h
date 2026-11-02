/* fire native graphics: fonts for text at pixel positions (TextFont: the two built-in bitmap fonts or a TrueType font), the layout and drawing of text, the table of the fonts of a program with
 * the lookup by name, and the fonts installed on the system. A port of src/fire.Terminal/TextFont.cs - the same pixels come out (the layout is described there). */
#ifndef FIRE_GFX_TEXT_H
#define FIRE_GFX_TEXT_H

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include FIRE_PLATFORM_FS_C_HEADER   /* (system fonts are read from the file system of the platform) */
#include "fire_gfx.h"
#include "fire_gfx_ttf.h"

enum { FIRE_GFX_DEFAULT_FONT_SIZE = 14, FIRE_GFX_MAX_FONT_SIZE = 512 };

/* Rounds half up (floor(x + 0.5)), like TextFont.Round in C#. */
static int fire_gfx_roundHalfUp(double x) { return (int)floor(x + 0.5); }

/* The next code point of a UTF-16 text (a surrogate pair is one; a lone surrogate is U+FFFD). */
static uint32_t fire_gfx_nextCodePoint(const uint16_t* text, uint32_t n, uint32_t* i) {
    uint16_t c = text[(*i)++];
    if (c >= 0xD800 && c <= 0xDBFF && *i < n && text[*i] >= 0xDC00 && text[*i] <= 0xDFFF) {
        uint32_t cp = 0x10000 + (((uint32_t)c - 0xD800) << 10) + ((uint32_t)text[*i] - 0xDC00);
        (*i)++;
        return cp;
    }
    return (c >= 0xD800 && c <= 0xDFFF) ? 0xFFFDu : (uint32_t)c;
}

/* A copy of a text (malloc), NULL without memory. */
static char* fire_gfx_strdup(const char* s) {
    size_t n = strlen(s);
    char* d = (char*)malloc(n + 1);
    if (d != NULL) memcpy(d, s, n + 1);
    return d;
}

/* A glyph of a size, as the cache keeps it. */
typedef struct FireGfxGlyph {
    int64_t key;   /* -1: a free slot */
    FireTtfGMetrics m;
    uint8_t* image;
    int hasImage;
} FireGfxGlyph;

#define FIRE_GFX_GLYPH_SLOTS 16384   /* (a power of two; at most 8192 glyphs are kept, then the cache starts afresh) */

typedef struct FireGfxTextFont {
    char* name;
    int bitmap, smallBitmap;   /* a built-in bitmap font: 8x14 (default) or 8x8 */
    FireTtfFont ttfFont;
    FireGfxGlyph* glyphs;      /* the cache (FIRE_GFX_GLYPH_SLOTS slots, made when the first glyph is drawn) */
    size_t glyphCount;
} FireGfxTextFont;

static FireGfxTextFont* fire_gfx_textFontNew(void) {
    FireGfxTextFont* f = (FireGfxTextFont*)calloc(1, sizeof(FireGfxTextFont));
    if (f == NULL) return NULL;
    fire_ttf_init(&f->ttfFont);
    return f;
}
static void fire_gfx_glyphsClear(FireGfxTextFont* f) {
    size_t i;
    if (f->glyphs == NULL) return;
    for (i = 0; i < FIRE_GFX_GLYPH_SLOTS; i++) { if (f->glyphs[i].key >= 0) free(f->glyphs[i].image); f->glyphs[i].key = -1; f->glyphs[i].image = NULL; }
    f->glyphCount = 0;
}
static void fire_gfx_textFontDelete(FireGfxTextFont* f) {
    if (f == NULL) return;
    fire_gfx_glyphsClear(f);
    free(f->glyphs);
    fire_ttf_free(&f->ttfFont);
    free(f->name);
    free(f);
}

static int fire_gfx_clampFontSize(int size) { return size <= 0 ? FIRE_GFX_DEFAULT_FONT_SIZE : fire_gfx_imin(size, FIRE_GFX_MAX_FONT_SIZE); }

static int fire_gfx_fontCellWidth(const FireGfxTextFont* f) { (void)f; return 8; }
static int fire_gfx_fontCellHeight(const FireGfxTextFont* f) { return f->smallBitmap ? 8 : 14; }

static int fire_gfx_fontAscent(const FireGfxTextFont* f, int size) {
    FireTtfLMetrics m;
    if (f->bitmap) return fire_gfx_fontCellHeight(f);
    size = fire_gfx_clampFontSize(size);
    return fire_ttf_lmetrics(&f->ttfFont, size, &m) ? fire_gfx_roundHalfUp(m.ascender) : size;
}
static int fire_gfx_fontLineHeight(const FireGfxTextFont* f, int size) {
    FireTtfLMetrics m;
    if (f->bitmap) return fire_gfx_fontCellHeight(f);
    size = fire_gfx_clampFontSize(size);
    if (!fire_ttf_lmetrics(&f->ttfFont, size, &m)) return size;
    return fire_gfx_imax(1, fire_gfx_roundHalfUp(m.ascender) + fire_gfx_roundHalfUp(-m.descender) + fire_gfx_roundHalfUp(m.lineGap));
}
static int fire_gfx_fontBaseline(const FireGfxTextFont* f, int size) {
    FireTtfLMetrics m;
    if (!fire_ttf_lmetrics(&f->ttfFont, size, &m)) return size;
    return fire_gfx_roundHalfUp(m.ascender) + (fire_gfx_roundHalfUp(m.lineGap) >> 1);
}

/* The glyph `g` of a size: from the cache, or measured and rendered now. NULL only without memory. */
static const FireGfxGlyph* fire_gfx_fontGlyph(FireGfxTextFont* f, int size, uint32_t g) {
    int64_t key = ((int64_t)size << 32) | (int64_t)g;
    size_t mask = FIRE_GFX_GLYPH_SLOTS - 1, at;
    FireGfxGlyph* c;
    if (f->glyphs == NULL) {
        size_t i;
        f->glyphs = (FireGfxGlyph*)calloc(FIRE_GFX_GLYPH_SLOTS, sizeof(FireGfxGlyph));
        if (f->glyphs == NULL) return NULL;
        for (i = 0; i < FIRE_GFX_GLYPH_SLOTS; i++) f->glyphs[i].key = -1;
        f->glyphCount = 0;
    }
    at = (size_t)(((uint64_t)key * 0x9E3779B97F4A7C15ull) >> 40) & mask;
    while (f->glyphs[at].key >= 0) {
        if (f->glyphs[at].key == key) return &f->glyphs[at];
        at = (at + 1) & mask;
    }
    if (f->glyphCount >= 8192) {
        fire_gfx_glyphsClear(f);
        at = (size_t)(((uint64_t)key * 0x9E3779B97F4A7C15ull) >> 40) & mask;
    }
    c = &f->glyphs[at];
    c->key = key;
    c->image = NULL;
    c->hasImage = 0;
    f->glyphCount++;
    if (fire_ttf_gmetrics(&f->ttfFont, size, g, &c->m)) {
        if (c->m.minWidth > 0 && c->m.minHeight > 0) {
            c->image = (uint8_t*)calloc((size_t)c->m.minWidth * c->m.minHeight, 1);
            c->hasImage = c->image != NULL && fire_ttf_render(&f->ttfFont, size, g, c->image, c->m.minWidth, c->m.minHeight);
        }
    } else memset(&c->m, 0, sizeof c->m);
    return c;
}

/* The layout loop: the pen position (fractions kept) after the text; `draw(ctx, cached glyph, pen position rounded to pixels)` is called for every glyph (`draw` NULL: none). */
typedef void (*FireGfxDrawGlyph)(void* ctx, const FireGfxGlyph* glyph, int penX);
static double fire_gfx_fontLayout(FireGfxTextFont* f, const uint16_t* text, uint32_t n, int size, FireGfxDrawGlyph draw, void* ctx) {
    double pen = 0;
    uint32_t previous = 0, i = 0;
    int hasPrevious = 0;
    while (i < n) {
        uint32_t cp = fire_gfx_nextCodePoint(text, n, &i), g;
        double kern;
        const FireGfxGlyph* c;
        if (cp < 0x20) continue;
        if (!fire_ttf_lookup(&f->ttfFont, cp, &g)) g = 0;
        if (hasPrevious && fire_ttf_kerning(&f->ttfFont, size, previous, g, &kern)) pen += kern;
        c = fire_gfx_fontGlyph(f, size, g);
        if (c == NULL) break;
        if (draw) draw(ctx, c, fire_gfx_roundHalfUp(pen));
        pen += c->m.advanceWidth;
        previous = g;
        hasPrevious = 1;
    }
    return pen;
}

static int fire_gfx_fontMeasure(FireGfxTextFont* f, const uint16_t* text, uint32_t n, int size) {
    if (f->bitmap) return (int)n * fire_gfx_fontCellWidth(f);
    size = fire_gfx_clampFontSize(size);
    return fire_gfx_roundHalfUp(fire_gfx_fontLayout(f, text, n, size, NULL, NULL));
}

static void fire_gfx_fontDrawBitmap(const FireGfxTextFont* f, FireGfxSurface* s, int x, int y, const uint16_t* text, uint32_t n, const FireGfxPixel* fg, int hasBg, const FireGfxPixel* bg) {
    int cw = fire_gfx_fontCellWidth(f), ch = fire_gfx_fontCellHeight(f), fgVisible, gx, gy;
    uint32_t i;
    if (hasBg && !fire_gfx_visible(s, bg)) hasBg = 0;
    fgVisible = fire_gfx_visible(s, fg);
    if (!fgVisible && !hasBg) return;
    for (i = 0; i < n; i++) {
        uint32_t c = text[i] < 256 ? text[i] : '?';
        const uint8_t* rows = f->smallBitmap ? g_fireGfxGlyphs8x8[c] : g_fireGfxGlyphs8x14[c];
        if (hasBg) fire_gfx_rect(s, x, y, cw, ch, bg);
        if (fgVisible)
            for (gy = 0; gy < ch; gy++) {
                unsigned bits = rows[gy];
                if (bits == 0) continue;
                for (gx = 0; gx < cw; gx++)
                    if ((bits >> (7 - gx)) & 1u) fire_gfx_put(s, x + gx, y + gy, fg);
            }
        x += cw;
    }
}

/* (what the glyphs of a TrueType text are drawn with) */
typedef struct FireGfxTtfDraw { FireGfxSurface* s; int x, base; uint32_t rgb, alpha; uint8_t index; } FireGfxTtfDraw;
static void fire_gfx_ttfDrawGlyph(void* ctx, const FireGfxGlyph* g, int penX) {
    const FireGfxTtfDraw* d = (const FireGfxTtfDraw*)ctx;
    int gx0, gy0, w, h, gx, gy;
    if (!g->hasImage) return;
    gx0 = d->x + penX + g->m.xOffset; gy0 = d->base + g->m.yOffset;
    w = g->m.minWidth; h = g->m.minHeight;
    for (gy = 0; gy < h; gy++)
        for (gx = 0; gx < w; gx++) {
            uint32_t c = g->image[(size_t)gy * w + gx], a;
            FireGfxPixel p;
            if (c == 0) continue;
            a = (d->alpha * c + 127) / 255;
            if (a == 0) continue;
            p.rgba = d->rgb | (a << 24);
            p.index = d->index;
            fire_gfx_put(d->s, gx0 + gx, gy0 + gy, &p);
        }
}
static void fire_gfx_fontDrawTrueType(FireGfxTextFont* f, FireGfxSurface* s, int x, int y, const uint16_t* text, uint32_t n, int size, const FireGfxPixel* fg, int hasBg, const FireGfxPixel* bg) {
    int fgVisible;
    FireGfxTtfDraw d;
    size = fire_gfx_clampFontSize(size);
    if (hasBg && !fire_gfx_visible(s, bg)) hasBg = 0;
    fgVisible = fire_gfx_visible(s, fg);
    if (!fgVisible && !hasBg) return;
    if (hasBg) fire_gfx_rect(s, x, y, fire_gfx_fontMeasure(f, text, n, size), fire_gfx_fontLineHeight(f, size), bg);
    if (!fgVisible) return;
    d.s = s;
    d.x = x;
    d.base = y + fire_gfx_fontBaseline(f, size);
    d.rgb = fg->rgba & 0x00FFFFFFu; d.alpha = fg->rgba >> 24;
    d.index = fg->index;
    fire_gfx_fontLayout(f, text, n, size, fire_gfx_ttfDrawGlyph, &d);
}

/* Name for comparing: lower case letters and digits only (ASCII; everything else is dropped). malloc'd. */
static char* fire_gfx_normalizeFontName(const char* name) {
    size_t n = strlen(name), k = 0, i;
    char* out = (char*)malloc(n + 1);
    if (out == NULL) return NULL;
    for (i = 0; i < n; i++) {
        unsigned char c = (unsigned char)name[i];
        if (c >= 'A' && c <= 'Z') out[k++] = (char)(c + 32);
        else if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) out[k++] = (char)c;
    }
    out[k] = 0;
    return out;
}

/* Is `ext` (from the dot on) .ttf/.otf, or with `ttc` also .ttc - in any case? */
static int fire_gfx_fontExtension(const char* ext, int ttc) {
    char lower[5];
    size_t i, n = strlen(ext);
    if (n != 4) return 0;
    for (i = 0; i < 4; i++) lower[i] = (ext[i] >= 'A' && ext[i] <= 'Z') ? (char)(ext[i] + 32) : ext[i];
    lower[4] = 0;
    return strcmp(lower, ".ttf") == 0 || strcmp(lower, ".otf") == 0 || (ttc && strcmp(lower, ".ttc") == 0);
}

/* "fonts/Roboto-Bold.ttf" -> "Roboto-Bold" (the last part of the path without the extension .ttf/.otf/.ttc). malloc'd. */
static char* fire_gfx_fontBaseName(const char* path) {
    const char* file = path;
    const char* p;
    const char* dot = NULL;
    char* out;
    size_t n;
    for (p = path; *p; p++) if (*p == '/' || *p == '\\') file = p + 1;
    for (p = file; *p; p++) if (*p == '.') dot = p;
    n = strlen(file);
    if (dot != NULL && dot > file && fire_gfx_fontExtension(dot, 1)) n = (size_t)(dot - file);
    out = (char*)malloc(n + 1);
    if (out == NULL) return NULL;
    memcpy(out, file, n);
    out[n] = 0;
    return out;
}

/* ---- the fonts installed on the system: the folders FIRE_FONT_DIRS (separated by ';'), then the usual font folders of Windows, macOS and Linux ---- */
typedef struct FireGfxSystemFont { char* path; char* key; char* familyKey; char* fullKey; int namesRead; } FireGfxSystemFont;
typedef struct FireGfxSystemFonts {
    FireGfxSystemFont* entries;
    size_t count, cap;
    int scanned;
    char* dirsKey;
} FireGfxSystemFonts;

static const char* fire_gfx_env(const char* name) { const char* v = getenv(name); return v ? v : ""; }

/* `a` + `b` (malloc). */
static char* fire_gfx_concat(const char* a, const char* b) {
    size_t x = strlen(a), y = strlen(b);
    char* out = (char*)malloc(x + y + 1);
    if (out == NULL) return NULL;
    memcpy(out, a, x);
    memcpy(out + x, b, y + 1);
    return out;
}
static void fire_gfx_addFolder(FireFsList* out, char* owned) { if (owned != NULL) { fire_fs_listAdd(out, owned); free(owned); } }

static void fire_gfx_fontFolders(FireFsList* out) {
    const char* dirs = fire_gfx_env("FIRE_FONT_DIRS");
    const char* win = fire_gfx_env("WINDIR");
    const char* local = fire_gfx_env("LOCALAPPDATA");
    const char* home = fire_gfx_env("HOME");
    const char* xdg = fire_gfx_env("XDG_DATA_HOME");
    const char* start = dirs;
    while (*dirs) {
        const char* end = strchr(start, ';');
        size_t n = end == NULL ? strlen(start) : (size_t)(end - start);
        if (n > 0) {
            char* part = (char*)malloc(n + 1);
            if (part != NULL) { memcpy(part, start, n); part[n] = 0; fire_fs_listAdd(out, part); free(part); }
        }
        if (end == NULL) break;
        start = end + 1;
    }
    if (*win) fire_gfx_addFolder(out, fire_gfx_concat(win, "/Fonts"));
    if (*local) fire_gfx_addFolder(out, fire_gfx_concat(local, "/Microsoft/Windows/Fonts"));
    fire_fs_listAdd(out, "/System/Library/Fonts");
    fire_fs_listAdd(out, "/System/Library/Fonts/Supplemental");
    fire_fs_listAdd(out, "/Library/Fonts");
    fire_fs_listAdd(out, "/usr/share/fonts");
    fire_fs_listAdd(out, "/usr/local/share/fonts");
    if (*home) {
        fire_gfx_addFolder(out, fire_gfx_concat(home, "/Library/Fonts"));
        fire_gfx_addFolder(out, fire_gfx_concat(home, "/.fonts"));
        fire_gfx_addFolder(out, fire_gfx_concat(home, "/.local/share/fonts"));
    }
    if (*xdg) fire_gfx_addFolder(out, fire_gfx_concat(xdg, "/fonts"));
}

/* The bytes of a file (malloc'd into *bytes, *size); 0 when it cannot be opened. */
static int fire_gfx_readFile(const char* path, uint8_t** bytes, size_t* size) {
    FireFsStatus status;
    FILE* f;
    uint8_t chunk[4096];
    size_t got, n = 0, cap = 0;
    uint8_t* data = NULL;
    memset(&status, 0, sizeof status);
    f = fire_fs_open(path, 0, 0, &status);
    *bytes = NULL; *size = 0;
    if (!f) return 0;
    while ((got = fread(chunk, 1, sizeof chunk, f)) > 0) {
        if (n + got > cap) {
            size_t c = cap ? cap * 2 : 65536;
            uint8_t* grown;
            while (c < n + got) c *= 2;
            grown = (uint8_t*)realloc(data, c);
            if (grown == NULL) { free(data); fclose(f); return 0; }
            data = grown;
            cap = c;
        }
        memcpy(data + n, chunk, got);
        n += got;
    }
    fclose(f);
    *bytes = data;
    *size = n;
    return 1;
}

static void fire_gfx_systemFontsClear(FireGfxSystemFonts* s) {
    size_t i;
    for (i = 0; i < s->count; i++) { free(s->entries[i].path); free(s->entries[i].key); free(s->entries[i].familyKey); free(s->entries[i].fullKey); }
    s->count = 0;
}

static void fire_gfx_systemFontsScan(FireGfxSystemFonts* s) {
    const char* key = fire_gfx_env("FIRE_FONT_DIRS");
    FireFsList folders = {NULL, 0, 0};
    size_t i;
    if (s->scanned && s->dirsKey != NULL && strcmp(key, s->dirsKey) == 0) return;
    fire_gfx_systemFontsClear(s);
    fire_gfx_fontFolders(&folders);
    for (i = 0; i < folders.count; i++) {
        FireFsList files = {NULL, 0, 0}, wanted = {NULL, 0, 0};
        FireFsStatus st;
        size_t k;
        memset(&st, 0, sizeof st);
        if (!fire_fs_list(folders.items[i], "*", 1, 0, &files, &st)) { fire_fs_listFree(&files); continue; }
        for (k = 0; k < files.count; k++) {
            const char* dot = strrchr(files.items[k], '.');
            if (dot == NULL) continue;
            if (fire_gfx_fontExtension(dot, 0)) fire_fs_listAdd(&wanted, files.items[k]);
        }
        fire_fs_listSort(&wanted);
        for (k = 0; k < wanted.count; k++) {
            FireGfxSystemFont e;
            char* base;
            if (!fire_gfx_grow((void**)&s->entries, &s->cap, s->count, sizeof(FireGfxSystemFont))) break;
            e.path = fire_gfx_strdup(wanted.items[k]);
            base = fire_gfx_fontBaseName(wanted.items[k]);
            e.key = base ? fire_gfx_normalizeFontName(base) : NULL;
            free(base);
            e.familyKey = NULL; e.fullKey = NULL; e.namesRead = 0;
            if (e.path == NULL || e.key == NULL) { free(e.path); free(e.key); continue; }
            s->entries[s->count++] = e;
        }
        fire_fs_listFree(&files);
        fire_fs_listFree(&wanted);
    }
    fire_fs_listFree(&folders);
    free(s->dirsKey);
    s->dirsKey = fire_gfx_strdup(key);
    s->scanned = 1;
}

/* Reads the file and checks that it is a TrueType font (`out` NULL: a font of its own for the check). On success the caller frees *bytes. */
static int fire_gfx_tryLoadFont(const char* path, uint8_t** bytes, size_t* size, FireTtfFont* out) {
    FireTtfFont probe;
    FireTtfFont* f = out ? out : &probe;
    int ok;
    if (!fire_gfx_readFile(path, bytes, size)) return 0;
    if (!out) fire_ttf_init(&probe);
    ok = fire_ttf_load(f, *bytes, *size);
    if (!out) fire_ttf_free(&probe);
    if (!ok) { free(*bytes); *bytes = NULL; *size = 0; }
    return ok;
}

static int fire_gfx_keyIs(const char* key, const char* name) { return key != NULL && strcmp(key, name) == 0; }

/* The bytes of the font file for a normalized name: a file with this name first, then a font whose full name it is, then one whose family name it is. */
static int fire_gfx_systemFontsFind(FireGfxSystemFonts* s, const char* name, uint8_t** bytes, size_t* size) {
    size_t i;
    fire_gfx_systemFontsScan(s);
    for (i = 0; i < s->count; i++)
        if (fire_gfx_keyIs(s->entries[i].key, name) && fire_gfx_tryLoadFont(s->entries[i].path, bytes, size, NULL)) return 1;
    for (i = 0; i < s->count; i++) {
        FireGfxSystemFont* e = &s->entries[i];
        uint8_t* data;
        size_t n;
        FireTtfFont f;
        if (e->namesRead) continue;
        e->namesRead = 1;
        fire_ttf_init(&f);
        if (fire_gfx_tryLoadFont(e->path, &data, &n, &f)) {
            char* family = fire_ttf_name(&f, 1);
            char* full = fire_ttf_name(&f, 4);
            free(e->familyKey); free(e->fullKey);
            e->familyKey = family ? fire_gfx_normalizeFontName(family) : NULL;
            e->fullKey = full ? fire_gfx_normalizeFontName(full) : NULL;
            free(family); free(full);
            free(data);
        }
        fire_ttf_free(&f);
    }
    for (i = 0; i < s->count; i++)
        if (fire_gfx_keyIs(s->entries[i].fullKey, name) && fire_gfx_tryLoadFont(s->entries[i].path, bytes, size, NULL)) return 1;
    for (i = 0; i < s->count; i++)
        if (fire_gfx_keyIs(s->entries[i].familyKey, name) && fire_gfx_tryLoadFont(s->entries[i].path, bytes, size, NULL)) return 1;
    return 0;
}

/* ---- the fonts of a program: ID 1 = the built-in 8x14, ID 2 = the built-in 8x8, then the loaded ones (an ID is never used twice). 0 = the font of the renderer (no entry), -1 = none. */
typedef struct FireGfxFontName { char* key; int id; } FireGfxFontName;
typedef struct FireGfxFontTable {
    FireGfxTextFont** fonts;
    size_t count, cap;
    FireGfxFontName* names;
    size_t nameCount, nameCap;
    char** systemMisses;
    size_t missCount, missCap;
    FireGfxSystemFonts system;
} FireGfxFontTable;

static int fire_gfx_fontsPush(FireGfxFontTable* t, FireGfxTextFont* f) {
    if (!fire_gfx_grow((void**)&t->fonts, &t->cap, t->count, sizeof(FireGfxTextFont*))) return -1;
    t->fonts[t->count++] = f;
    return (int)t->count - 1;
}

static void fire_gfx_fontTableInit(FireGfxFontTable* t) {
    FireGfxTextFont* big;
    FireGfxTextFont* small;
    memset(t, 0, sizeof *t);
    fire_gfx_fontsPush(t, NULL);
    big = fire_gfx_textFontNew();
    if (big) { big->name = fire_gfx_strdup("8x14"); big->bitmap = 1; }
    small = fire_gfx_textFontNew();
    if (small) { small->name = fire_gfx_strdup("8x8"); small->bitmap = 1; small->smallBitmap = 1; }
    fire_gfx_fontsPush(t, big);
    fire_gfx_fontsPush(t, small);
}

static FireGfxTextFont* fire_gfx_fontTableGet(const FireGfxFontTable* t, int64_t id) { return id > 0 && (size_t)id < t->count ? t->fonts[(size_t)id] : NULL; }

static int fire_gfx_fontTableLoad(FireGfxFontTable* t, const uint8_t* data, size_t size) {
    FireGfxTextFont* f = fire_gfx_textFontNew();
    int id;
    if (f == NULL) return -1;
    if (!fire_ttf_load(&f->ttfFont, data, size)) { fire_gfx_textFontDelete(f); return -1; }
    f->name = fire_ttf_name(&f->ttfFont, 4);
    if (f->name != NULL && f->name[0] == 0) { free(f->name); f->name = fire_ttf_name(&f->ttfFont, 1); }
    id = fire_gfx_fontsPush(t, f);
    if (id < 0) fire_gfx_textFontDelete(f);
    return id;
}

/* names[key] = id (the key is taken over). */
static void fire_gfx_fontTableSetName(FireGfxFontTable* t, char* key, int id) {
    size_t i;
    for (i = 0; i < t->nameCount; i++) if (strcmp(t->names[i].key, key) == 0) { t->names[i].id = id; free(key); return; }
    if (!fire_gfx_grow((void**)&t->names, &t->nameCap, t->nameCount, sizeof(FireGfxFontName))) { free(key); return; }
    t->names[t->nameCount].key = key;
    t->names[t->nameCount].id = id;
    t->nameCount++;
}
static const FireGfxFontName* fire_gfx_fontTableFindName(const FireGfxFontTable* t, const char* key) {
    size_t i;
    for (i = 0; i < t->nameCount; i++) if (strcmp(t->names[i].key, key) == 0) return &t->names[i];
    return NULL;
}

static void fire_gfx_fontTableRegister(FireGfxFontTable* t, const char* name, int id) {
    char* key;
    if (name == NULL) return;
    key = fire_gfx_normalizeFontName(name);
    if (key == NULL) return;
    if (key[0] == 0) { free(key); return; }
    fire_gfx_fontTableSetName(t, key, id);
}

static int fire_gfx_fontTableAdd(FireGfxFontTable* t, const uint8_t* data, size_t size, const char* alias) {
    int id = fire_gfx_fontTableLoad(t, data, size);
    char* text;
    if (id < 0) return -1;
    text = fire_gfx_fontBaseName(alias);
    fire_gfx_fontTableRegister(t, text, id);
    free(text);
    text = fire_ttf_name(&t->fonts[(size_t)id]->ttfFont, 1);
    fire_gfx_fontTableRegister(t, text, id);
    free(text);
    text = fire_ttf_name(&t->fonts[(size_t)id]->ttfFont, 4);
    fire_gfx_fontTableRegister(t, text, id);
    free(text);
    return id;
}

static int fire_gfx_fontTableOpen(FireGfxFontTable* t, const char* name) {
    char* key = fire_gfx_normalizeFontName(name);
    const FireGfxFontName* known;
    uint8_t* bytes = NULL;
    size_t size = 0, i;
    int id;
    if (key == NULL) return -1;
    if (key[0] == 0 || strcmp(key, "console") == 0 || strcmp(key, "default") == 0) { free(key); return 0; }
    if (strcmp(key, "8x14") == 0 || strcmp(key, "14x8") == 0) { free(key); return 1; }
    if (strcmp(key, "8x8") == 0) { free(key); return 2; }
    known = fire_gfx_fontTableFindName(t, key);
    if (known != NULL) { id = known->id; free(key); return id; }
    for (i = 0; i < t->missCount; i++) if (strcmp(t->systemMisses[i], key) == 0) { free(key); return -1; }
    id = fire_gfx_systemFontsFind(&t->system, key, &bytes, &size) ? fire_gfx_fontTableLoad(t, bytes, size) : -1;
    free(bytes);
    if (id < 0) {
        if (fire_gfx_grow((void**)&t->systemMisses, &t->missCap, t->missCount, sizeof(char*))) t->systemMisses[t->missCount++] = key;
        else free(key);
        return -1;
    }
    fire_gfx_fontTableSetName(t, key, id);
    return id;
}

static int fire_gfx_fontTableDestroy(FireGfxFontTable* t, int64_t id) {
    size_t i, k = 0;
    if (id <= 2 || (size_t)id >= t->count || !t->fonts[(size_t)id]) return 0;
    fire_gfx_textFontDelete(t->fonts[(size_t)id]);
    t->fonts[(size_t)id] = NULL;
    for (i = 0; i < t->nameCount; i++) {
        if (t->names[i].id == id) free(t->names[i].key);
        else t->names[k++] = t->names[i];
    }
    t->nameCount = k;
    return 1;
}

/* Text in a font at pixel coordinates (`font` NULL = the font of the renderer): the glyph pixels in the color of `fg`, with `bg` (NULL = none) the cell or the line below. */
static void fire_gfx_drawTextIn(FireGfxRenderer* r, FireGfxTextFont* font, int size, int x, int y, const uint16_t* text, uint32_t n, const FireGfxBrush* fg, const FireGfxBrush* bg) {
    FireGfxSurface s;
    FireGfxPixel f, b;
    if (!font) { fire_gfx_rendererDrawText(r, x, y, text, n, fg, bg); return; }
    s = fire_gfx_rendererSurface(r);
    f = fire_gfx_resolve(&s, fg->color);
    b = bg ? fire_gfx_resolve(&s, bg->color) : fire_gfx_pixelNone();
    if (font->bitmap) fire_gfx_fontDrawBitmap(font, &s, x, y, text, n, &f, bg != NULL, &b);
    else fire_gfx_fontDrawTrueType(font, &s, x, y, text, n, size, &f, bg != NULL, &b);
}

#endif
