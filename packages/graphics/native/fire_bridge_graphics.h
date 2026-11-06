/* fire native bridge "graphics" (C): the natives behind `#import "graphics"` (docs/spec/GRAPHICS_LAYER.md) - framebuffers, the palette, images (PNG, BMP, GIF), the renderer (text and
 * drawing with brushes and pens) and the slicer. The fire side (Framebuffer, Renderer, Brush, Pen, Slicer, ToolPath - fire source) is the same as in the VM; this file is what its
 * `__GRPH...` functions do (the generator calls `__GRPHName` as fire_gfx_Name). The drawing itself is in fire_gfx*.h (a port of src/fire.Terminal that needs no operating system).
 * A window that shows a framebuffer is the `windows` bridge.
 *
 * C99, part of the generated program (after the runtime) when the program imports "graphics"; in the virtual machine these natives are C#. Image files are read through the file
 * system of the platform (FIRE_PLATFORM_FS_C_HEADER: fire_fs_*, restricted by FIRE_IO_POLICY like the IO bridge). Differences to the VM: an unknown or destroyed resource id ends
 * the program with an error (the VM throws a .NET exception); the renderer uses the 8x14 font. The message of the last failed function is g_gfxError (per thread). */
#ifndef FIRE_BRIDGE_GRAPHICS_H
#define FIRE_BRIDGE_GRAPHICS_H

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include FIRE_PLATFORM_FS_C_HEADER
#include "fire_gfx.h"
#include "fire_gfx_text.h"
#include "fire_gfx_images.h"
#include "fire_gfx_slicer.h"
#include "fire_gfx_fx.h"
#include "fire_gfx_gpu.h"

/* The framebuffers, renderers, brushes and pens by id (ids start at 1 and are not used again, like the VM's IdManager). */
typedef struct FireGfxTables {
    int ready;
    FireGfxFramebuffer** framebuffers; size_t fbCount, fbCap;
    FireGfxRenderer** renderers; size_t rndCount, rndCap;
    FireGfxBrush** brushes; size_t bshCount, bshCap;
    FireGfxPen** pens; size_t penCount, penCap;
    FireGfxFontTable fonts;
} FireGfxTables;
static FireGfxTables g_fireGfxTables;

/* Appends a pointer to a table: its id, -1 without memory. */
static int64_t fire_gfxb_push(void*** items, size_t* count, size_t* cap, void* item) {
    if (!fire_gfx_grow((void**)items, cap, *count, sizeof(void*))) return -1;
    (*items)[(*count)++] = item;
    return (int64_t)*count - 1;
}
static FireGfxTables* fire_gfxb_tables(void) {
    FireGfxTables* t = &g_fireGfxTables;
    if (!t->ready) {
        t->ready = 1;
        fire_gfxb_push((void***)&t->framebuffers, &t->fbCount, &t->fbCap, NULL);
        fire_gfxb_push((void***)&t->renderers, &t->rndCount, &t->rndCap, NULL);
        fire_gfxb_push((void***)&t->brushes, &t->bshCount, &t->bshCap, NULL);
        fire_gfxb_push((void***)&t->pens, &t->penCount, &t->penCap, NULL);
        fire_gfx_fontTableInit(&t->fonts);
    }
    return t;
}

FIRE_NORETURN static void fire_gfxb_noResource(int64_t i) {
    char text[96];
    snprintf(text, sizeof text, "No resource with ID %lld (unknown or already destroyed).", (long long)i);
    fatal(text);
}
static void fire_gfxb_outOfMemory(void) { fatal("Out of memory."); }

/* A framebuffer by id; fatal when the id is not valid (like the VM's KeyNotFoundException). */
static FireGfxFramebuffer* fire_gfxb_fbOf(Value id) {
    int64_t i = (int32_t)id.i;
    FireGfxTables* t = fire_gfxb_tables();
    if (i > 0 && (size_t)i < t->fbCount && t->framebuffers[(size_t)i]) return t->framebuffers[(size_t)i];
    fire_gfxb_noResource(i);
    return NULL;
}
/* A package draws the pictures of the windows that show the framebuffer `fbId` itself (scene3d DirectDrawing): the windows bridge calls `fn` (a fire_present_fn, see
 * fire_display_c.h) instead of showing the framebuffer. 1: attached; 0: there is no such framebuffer. Detaching takes only the presenter of the same ctx away. */
static int fire_gfx_attachPresenter(int64_t fbId, void (*fn)(void), void* ctx) {
    FireGfxTables* t = fire_gfxb_tables();
    if (fbId <= 0 || (size_t)fbId >= t->fbCount || !t->framebuffers[(size_t)fbId]) return 0;
    t->framebuffers[(size_t)fbId]->presentFn = fn;
    t->framebuffers[(size_t)fbId]->presentCtx = ctx;
    return 1;
}
static void fire_gfx_detachPresenter(int64_t fbId, void* ctx) {
    FireGfxTables* t = fire_gfxb_tables();
    if (fbId <= 0 || (size_t)fbId >= t->fbCount || !t->framebuffers[(size_t)fbId] || t->framebuffers[(size_t)fbId]->presentCtx != ctx) return;
    t->framebuffers[(size_t)fbId]->presentFn = NULL;
    t->framebuffers[(size_t)fbId]->presentCtx = NULL;
}
static FireGfxRenderer* fire_gfxb_rendererOf(Value id) {
    int64_t i = (int32_t)id.i;
    FireGfxTables* t = fire_gfxb_tables();
    if (i > 0 && (size_t)i < t->rndCount && t->renderers[(size_t)i]) return t->renderers[(size_t)i];
    fire_gfxb_noResource(i);
    return NULL;
}
static FireGfxBrush* fire_gfxb_brushOf(Value id) {
    int64_t i = (int32_t)id.i;
    FireGfxTables* t = fire_gfxb_tables();
    if (i > 0 && (size_t)i < t->bshCount && t->brushes[(size_t)i]) return t->brushes[(size_t)i];
    fire_gfxb_noResource(i);
    return NULL;
}
static FireGfxPen* fire_gfxb_penOf(Value id) {
    int64_t i = (int32_t)id.i;
    FireGfxTables* t = fire_gfxb_tables();
    if (i > 0 && (size_t)i < t->penCount && t->pens[(size_t)i]) return t->pens[(size_t)i];
    fire_gfxb_noResource(i);
    return NULL;
}
static int fire_gfxb_addFramebuffer(FireGfxFramebuffer* fb) {
    FireGfxTables* t = fire_gfxb_tables();
    int64_t id = fire_gfxb_push((void***)&t->framebuffers, &t->fbCount, &t->fbCap, fb);
    if (id < 0) fire_gfxb_outOfMemory();
    return (int)id;
}

static int fire_gfxb_I(Value v) { return (int32_t)v.i; }
static double fire_gfxb_D(Value v) { return v.kind == K_Float ? (double)v.f : (double)v.i; }

static void fire_gfxb_setError(const char* message) {
    size_t n = strlen(message);
    if (n > sizeof g_gfxError.message - 1) n = sizeof g_gfxError.message - 1;
    memcpy(g_gfxError.message, message, n);
    g_gfxError.message[n] = 0;
    g_gfxError.code = 1;
}
static void fire_gfxb_clearError(void) { g_gfxError.code = 0; g_gfxError.message[0] = 0; }

static Value fire_gfxb_strOf8(const char* text, OwnList* list) {
    size_t n = strlen(text), i;
    Str* s = allocStr((uint32_t)n, list);
    /* UTF-8 messages are plain ASCII here except for paths: widen what is ASCII, the rest as is */
    fire_char16* out = strChars(s);
    for (i = 0; i < n; i++) out[i] = (unsigned char)text[i];
    return StrV(s);
}

/* A script string (UTF-16) as UTF-8 (malloc; the caller frees it). */
static char* fire_gfxb_utf8Of(Value text) {
    const Str* s = strOf(text);
    char* out = (char*)malloc((size_t)s->length * 3 + 1);
    size_t n = 0;
    uint32_t i;
    if (out == NULL) fire_gfxb_outOfMemory();
    for (i = 0; i < s->length; i++) {
        uint32_t c = s->data[i];
        if (c >= 0xD800 && c <= 0xDBFF && i + 1 < s->length && s->data[i + 1] >= 0xDC00 && s->data[i + 1] <= 0xDFFF) {
            c = 0x10000 + ((c - 0xD800) << 10) + (s->data[i + 1] - 0xDC00);
            i++;
        } else if (c >= 0xD800 && c <= 0xDFFF) c = 0xFFFD;
        if (c < 0x80) out[n++] = (char)c;
        else if (c < 0x800) { out[n++] = (char)(0xC0 | (c >> 6)); out[n++] = (char)(0x80 | (c & 0x3F)); }
        else if (c < 0x10000) { out[n++] = (char)(0xE0 | (c >> 12)); out[n++] = (char)(0x80 | ((c >> 6) & 0x3F)); out[n++] = (char)(0x80 | (c & 0x3F)); }
        else { out[n++] = (char)(0xF0 | (c >> 18)); out[n++] = (char)(0x80 | ((c >> 12) & 0x3F)); out[n++] = (char)(0x80 | ((c >> 6) & 0x3F)); out[n++] = (char)(0x80 | (c & 0x3F)); }
    }
    out[n] = 0;
    return out;
}

/* A UTF-8 text as a script string (UTF-16). */
static Value fire_gfxb_strFromUtf8(const char* text, OwnList* list) {
    size_t len = strlen(text), i = 0, count = 0;
    fire_char16* units = (fire_char16*)malloc(sizeof(fire_char16) * (len * 2 + 1));
    Str* r;
    if (units == NULL) fire_gfxb_outOfMemory();
    while (i < len) {
        unsigned char b = (unsigned char)text[i];
        uint32_t c = b;
        size_t extra = 0, k;
        if (b >= 0xF0 && b < 0xF8) { c = b & 0x07; extra = 3; }
        else if (b >= 0xE0) { c = b & 0x0F; extra = 2; }
        else if (b >= 0xC0) { c = b & 0x1F; extra = 1; }
        i++;
        for (k = 0; k < extra && i < len; k++, i++) c = (c << 6) | ((unsigned char)text[i] & 0x3F);
        if (c >= 0x10000) { c -= 0x10000; units[count++] = (fire_char16)(0xD800 + (c >> 10)); units[count++] = (fire_char16)(0xDC00 + (c & 0x3FF)); }
        else units[count++] = (fire_char16)c;
    }
    r = allocStr((uint32_t)count, list);
    if (count) memcpy(strChars(r), units, count * sizeof(fire_char16));
    free(units);
    return StrV(r);
}

/* ---- framebuffers ------------------------------------------------------------------------------------------------------------------------------------ */
static Value fire_gfx_FbCreate(Value w, Value h, Value mode) {
    FireGfxFramebuffer* fb = fire_gfx_fbNew();
    if (fb == NULL) fire_gfxb_outOfMemory();
    if (!fire_gfx_fbCreate(fb, fire_gfxb_I(w), fire_gfxb_I(h), fire_gfxb_I(mode))) { fire_gfx_fbDelete(fb); return Int(-1); }
    if (fb->pixels != NULL && fire_gfx_gpuWanted()) fire_gfx_gpuAttach(fb);   /* (FIRE_GFX_GPU: every RGBA framebuffer on the GPU) */
    return Int(fire_gfxb_addFramebuffer(fb));
}
/* Framebuffer.Accelerated: on the GPU (1: it is now; 0: there is none, or it is a palette framebuffer - GpuError says why). */
static Value fire_gfx_FbSetAccelerated(Value id, Value on) {
    FireGfxFramebuffer* fb = fire_gfxb_fbOf(id);
    if (on.i == 0) { fire_gfx_gpuDetach(fb); return Bool(false); }
    return Bool(fire_gfx_gpuAttach(fb) != 0);
}
static Value fire_gfx_FbGetAccelerated(Value id) { return Bool(fire_gfxb_fbOf(id)->gpu != NULL); }
static Value fire_gfx_FbGpuError(OwnList* list) { return fire_gfxb_strOf8(fire_gfx_gpuError(), list); }
static Value fire_gfx_FbGpuReadbacks(void) { return Int((int64_t)fire_gfx_gpuReadbacks()); }
static Value fire_gfx_FbDestroy(Value id) {
    int64_t i = (int32_t)id.i;
    FireGfxTables* t = fire_gfxb_tables();
    if (i > 0 && (size_t)i < t->fbCount && t->framebuffers[(size_t)i]) {
        fire_gfx_fbRelease(t->framebuffers[(size_t)i]);
        t->framebuffers[(size_t)i] = NULL;
        return Bool(true);
    }
    return Bool(false);
}
static Value fire_gfx_FbWidth(Value id) { return Int(fire_gfxb_fbOf(id)->width); }
static Value fire_gfx_FbHeight(Value id) { return Int(fire_gfxb_fbOf(id)->height); }
static Value fire_gfx_FbResize(Value id, Value w, Value h) { return Bool(fire_gfx_fbResize(fire_gfxb_fbOf(id), fire_gfxb_I(w), fire_gfxb_I(h)) != 0); }
static Value fire_gfx_FbMode(Value id) { return Int(fire_gfxb_fbOf(id)->mode); }
static Value fire_gfx_FbByteCount(Value id) { return Int((int64_t)fire_gfx_fbByteCount(fire_gfxb_fbOf(id))); }

static void fire_gfxb_checkByteOffset(const FireGfxFramebuffer* fb, int64_t offset) {
    int64_t total = (int64_t)fire_gfx_fbByteCount(fb);
    if (offset < 0 || offset >= total) {
        char text[128];
        snprintf(text, sizeof text, "Byte offset %lld outside of the buffer (size %lld bytes).", (long long)offset, (long long)total);
        fatal(text);
    }
}
static Value fire_gfx_FbReadByte(Value id, Value offset) {
    FireGfxFramebuffer* fb = fire_gfxb_fbOf(id);
    int off = fire_gfxb_I(offset);
    fire_gfxb_checkByteOffset(fb, off);
    fire_gfx_fbCpu(fb, 0);
    if (fb->indices) return Int(fb->indices[off]);
    return Int((fb->pixels[off / 4] >> ((off % 4) * 8)) & 0xFF);
}
static Value fire_gfx_FbWriteByte(Value id, Value offset, Value value) {
    FireGfxFramebuffer* fb = fire_gfxb_fbOf(id);
    int off = fire_gfxb_I(offset), shift;
    uint8_t b = (uint8_t)value.i;
    uint32_t* px;
    fire_gfxb_checkByteOffset(fb, off);
    fire_gfx_fbCpu(fb, 1);
    if (fb->indices) { fb->indices[off] = b; return Undef(); }
    shift = (off % 4) * 8;
    px = &fb->pixels[off / 4];
    *px = (*px & ~(0xFFu << shift)) | ((uint32_t)b << shift);
    return Undef();
}
/* R, G, B, A bytes per pixel, whatever the byte order of the machine is. */
static Value fire_gfx_FbReadBytes(Value id, OwnList* list) {
    FireGfxFramebuffer* fb = fire_gfxb_fbOf(id);
    size_t n = fire_gfx_fbByteCount(fb), i, count;
    Buf* b = allocBuf((uint32_t)n, list);
    uint8_t* bytes = bufBytes(b);
    fire_gfx_fbCpu(fb, 0);
    if (fb->indices) { if (n) memcpy(bytes, fb->indices, n); }
    else for (i = 0, count = fire_gfx_fbPixelCount(fb); i < count; i++) {
        uint32_t px = fb->pixels[i];
        bytes[i * 4] = (uint8_t)px; bytes[i * 4 + 1] = (uint8_t)(px >> 8); bytes[i * 4 + 2] = (uint8_t)(px >> 16); bytes[i * 4 + 3] = (uint8_t)(px >> 24);
    }
    return BufV(b);
}
/* The framebuffer as a PNG file (RGBA, 8 bits): Framebuffer.ToPng - to save it, or to put it on the clipboard. A palette framebuffer is resolved through its palette;
 * its transparent index becomes transparent. */
static uint32_t* fire_gfxb_rgbaOf(const FireGfxFramebuffer* fb) {
    size_t n = fire_gfx_fbPixelCount(fb), i;
    uint32_t* rgba = (uint32_t*)malloc(n * 4 + 4);
    if (rgba == NULL) fire_gfxb_outOfMemory();
    fire_gfx_fbResolveTo(fb, rgba);
    if (fb->indices && fb->transparentIndex >= 0)
        for (i = 0; i < n; i++) if (fb->indices[i] == fb->transparentIndex) rgba[i] &= 0x00FFFFFFu;
    return rgba;
}
static Value fire_gfx_FbToPng(Value id, OwnList* list) {
    FireGfxFramebuffer* fb = fire_gfxb_fbOf(id);
    uint32_t* rgba = fire_gfxb_rgbaOf(fb);
    size_t size = 0;
    uint8_t* png = fire_gfx_encodePng(rgba, fb->width, fb->height, &size);
    Buf* b;
    free(rgba);
    if (png == NULL || size > 0x7FFFFFFFu) { free(png); fire_gfxb_outOfMemory(); }
    b = allocBuf((uint32_t)size, list);
    memcpy(bufBytes(b), png, size);
    free(png);
    return BufV(b);
}
static Value fire_gfx_FbWriteBytes(Value id, Value data) {
    FireGfxFramebuffer* fb;
    FireBytes b;
    size_t expected, i, count;
    const uint8_t* bytes;
    if (!bytesArg(data, "Framebuffer.WriteBytes", &b)) return Undef();
    fire_gfxb_clearError();
    fb = fire_gfxb_fbOf(id);
    expected = fire_gfx_fbByteCount(fb);
    if (b.length != expected) {
        char text[128];
        snprintf(text, sizeof text, "Expected exactly %llu bytes, got %lu.", (unsigned long long)expected, (unsigned long)b.length);
        fire_gfxb_setError(text);
        return Bool(false);
    }
    bytes = b.data;
    fire_gfx_fbCpu(fb, 1);
    if (fb->indices) { if (expected) memcpy(fb->indices, bytes, expected); }
    else for (i = 0, count = fire_gfx_fbPixelCount(fb); i < count; i++)
        fb->pixels[i] = bytes[i * 4] | ((uint32_t)bytes[i * 4 + 1] << 8) | ((uint32_t)bytes[i * 4 + 2] << 16) | ((uint32_t)bytes[i * 4 + 3] << 24);
    return Bool(true);
}

static int fire_gfxb_checkPaletteIndex(int64_t index) {
    if ((uint64_t)index > 255) {
        char text[96];
        snprintf(text, sizeof text, "Palette index %lld outside of 0-255.", (long long)index);
        fire_gfxb_setError(text);
        return 0;
    }
    return 1;
}
/* The colors of the palette are unsigned values (r + g*256 + b*65536 + a*16777216); -1 for an invalid index. */
static Value fire_gfx_FbGetPaletteColor(Value id, Value index) {
    fire_gfxb_clearError();
    if (!fire_gfxb_checkPaletteIndex(fire_gfxb_I(index))) return Int(-1);
    return Int((int64_t)fire_gfx_paletteGet(&fire_gfxb_fbOf(id)->palette, (uint8_t)fire_gfxb_I(index)));
}
static Value fire_gfx_FbSetPaletteColor(Value id, Value index, Value color) {
    fire_gfxb_clearError();
    if (!fire_gfxb_checkPaletteIndex(fire_gfxb_I(index))) return Bool(false);
    fire_gfx_paletteSet(&fire_gfxb_fbOf(id)->palette, (uint8_t)fire_gfxb_I(index), (uint32_t)fire_gfxb_I(color));
    return Bool(true);
}
static Value fire_gfx_FbReadPalette(Value id, Value withAlpha, OwnList* list) {
    const FireGfxPalette* p = &fire_gfxb_fbOf(id)->palette;
    int stride = withAlpha.i ? 4 : 3, i;
    Buf* b = allocBuf((uint32_t)(256 * stride), list);
    uint8_t* bytes = bufBytes(b);
    for (i = 0; i < 256; i++) {
        uint32_t c = p->entries[i];
        bytes[i * stride] = (uint8_t)c; bytes[i * stride + 1] = (uint8_t)(c >> 8); bytes[i * stride + 2] = (uint8_t)(c >> 16);
        if (stride == 4) bytes[i * stride + 3] = (uint8_t)(c >> 24);
    }
    return BufV(b);
}
static int fire_gfxb_writePalette(FireGfxFramebuffer* fb, const uint8_t* data, size_t n) {
    int stride, i;
    uint32_t colors[256];
    if (n != 768 && n != 1024) {
        char text[96];
        snprintf(text, sizeof text, "A palette has 768 (RGB) or 1024 (RGBA) bytes, got %llu.", (unsigned long long)n);
        fire_gfxb_setError(text);
        return 0;
    }
    stride = n == 768 ? 3 : 4;
    for (i = 0; i < 256; i++)
        colors[i] = data[i * stride] | ((uint32_t)data[i * stride + 1] << 8) | ((uint32_t)data[i * stride + 2] << 16) | ((stride == 4 ? (uint32_t)data[i * stride + 3] : 255u) << 24);
    fire_gfx_paletteSetAll(&fb->palette, colors, 256);
    return 1;
}
static Value fire_gfx_FbWritePalette(Value id, Value data) {
    FireGfxFramebuffer* fb;
    FireBytes b;
    if (!bytesArg(data, "Framebuffer.WritePalette", &b)) return Undef();
    fire_gfxb_clearError();
    fb = fire_gfxb_fbOf(id);
    return Bool(fire_gfxb_writePalette(fb, b.data, b.length) != 0);
}

/* A new framebuffer from decoded image data: the id, or -1 with the message in LastError. */
static Value fire_gfxb_fromImage(const uint8_t* data, size_t n, int mode) {
    FireGfxImage image;
    char err[FIRE_GFX_ERR_SIZE];
    FireGfxFramebuffer* fb;
    fire_gfx_imageInit(&image);
    err[0] = 0;
    if (!fire_gfx_decodeImage(data, n, &image, err)) { fire_gfx_imageFree(&image); fire_gfxb_setError(err); return Int(-1); }
    fb = fire_gfx_fbNew();
    if (fb == NULL || !fire_gfx_imageToFramebuffer(&image, mode, fb)) { fire_gfx_fbDelete(fb); fire_gfx_imageFree(&image); fire_gfxb_setError("Not enough memory for the image."); return Int(-1); }
    fire_gfx_imageFree(&image);
    return Int(fire_gfxb_addFramebuffer(fb));
}
static Value fire_gfx_FbLoadImage(Value data, Value mode) {
    FireBytes b;
    if (!bytesArg(data, "Framebuffer.FromImage", &b)) return Undef();
    fire_gfxb_clearError();
    return fire_gfxb_fromImage(b.data, b.length, mode.i < 0 ? -1 : fire_gfxb_I(mode));
}
static Value fire_gfx_FbLoadFile(Value path, Value mode) {
    const Str* s;
    char* raw;
    char* full;
    size_t n = 0;
    uint32_t i;
    FireFsStatus status;
    FILE* f;
    uint8_t* bytes = NULL;
    size_t size = 0, cap = 0, got;
    uint8_t chunk[4096];
    Value result;
    fire_gfxb_clearError();
    s = strOf(path);
    raw = (char*)malloc((size_t)s->length * 3 + 1);
    if (raw == NULL) fire_gfxb_outOfMemory();
    for (i = 0; i < s->length; i++) {
        uint32_t c = s->data[i];
        if (c < 0x80) raw[n++] = (char)c;
        else if (c < 0x800) { raw[n++] = (char)(0xC0 | (c >> 6)); raw[n++] = (char)(0x80 | (c & 0x3F)); }
        else { raw[n++] = (char)(0xE0 | (c >> 12)); raw[n++] = (char)(0x80 | ((c >> 6) & 0x3F)); raw[n++] = (char)(0x80 | (c & 0x3F)); }
    }
    raw[n] = 0;
    full = fire_fs_fullPath(raw);
    free(raw);
    if (full == NULL) fire_gfxb_outOfMemory();
#if defined(FIRE_LIBRARY) || defined(FIRE_IO_POLICY)
    {
        char reason[256];
        int allowed;
        reason[0] = 0;
#ifdef FIRE_LIBRARY
        /* the library for the VM: the host decides what a program may read (its IoPolicy), as for IO.File */
        {
            const fire_host* host = libraryHost();
            allowed = !host || !host->io_allow || host->io_allow(full, 1, reason, (int32_t)sizeof reason);
        }
#else
        allowed = FIRE_IO_POLICY(full, 1, reason, sizeof reason);
#endif
        if (!allowed) {
            char text[1024];
            if (reason[0]) snprintf(text, sizeof text, "%s", reason);
            else snprintf(text, sizeof text, "Access to '%s' is not allowed.", full);
            fire_gfxb_setError(text);
            free(full);
            return Int(-1);
        }
    }
#endif
    memset(&status, 0, sizeof status);
    f = fire_fs_open(full, 0, 0, &status);
    free(full);
    if (!f) { fire_gfxb_setError(status.message); return Int(-1); }
    while ((got = fread(chunk, 1, sizeof chunk, f)) > 0) {
        if (size + got > cap) {
            size_t c = cap ? cap * 2 : 65536;
            uint8_t* grown;
            while (c < size + got) c *= 2;
            grown = (uint8_t*)realloc(bytes, c);
            if (grown == NULL) { free(bytes); fclose(f); fire_gfxb_outOfMemory(); }
            bytes = grown;
            cap = c;
        }
        memcpy(bytes + size, chunk, got);
        size += got;
    }
    fclose(f);
    result = fire_gfxb_fromImage(bytes, size, mode.i < 0 ? -1 : fire_gfxb_I(mode));
    free(bytes);
    return result;
}
/* Raw pixels, top to bottom: RGBA 4 bytes (R, G, B, A) or one palette index per pixel, optionally with a palette (768 or 1024 bytes). */
static Value fire_gfx_FbFromPixels(Value w, Value h, Value pixels, Value mode, Value palette) {
    int width, height, m;
    FireGfxFramebuffer* fb;
    FireBytes b, pb;
    size_t expected, i, count;
    const uint8_t* bytes;
    /* (both are taken before the framebuffer is made: an exception does not come back here in the library for the VM) */
    if (!bytesArg(pixels, "Framebuffer.FromPixels", &b)) return Undef();
    if ((palette.kind == K_Buffer || palette.kind == K_Array) && !bytesArg(palette, "Framebuffer.FromPixels", &pb)) return Undef();
    fire_gfxb_clearError();
    width = fire_gfxb_I(w); height = fire_gfxb_I(h); m = fire_gfxb_I(mode);
    fb = fire_gfx_fbNew();
    if (fb == NULL) fire_gfxb_outOfMemory();
    if (width <= 0 || height <= 0) { fire_gfx_fbDelete(fb); fire_gfxb_setError("The framebuffer size must be positive. (Parameter 'width')"); return Int(-1); }
    if (m != FIRE_GFX_RGBA && m != FIRE_GFX_INDEXED) { fire_gfx_fbDelete(fb); fire_gfxb_setError("Unknown color mode. (Parameter 'mode')"); return Int(-1); }
    if (!fire_gfx_fbCreate(fb, width, height, m)) { fire_gfx_fbDelete(fb); fire_gfxb_setError("Not enough memory for the image."); return Int(-1); }
    expected = fire_gfx_fbByteCount(fb);
    if (b.length != expected) {
        char text[256];
        fire_gfx_fbDelete(fb);
        snprintf(text, sizeof text, "Expected exactly %llu bytes (%dx%d, %s per pixel), got %lu. (Parameter 'pixels')",
                 (unsigned long long)expected, width, height, m == FIRE_GFX_INDEXED ? "1 byte" : "4 bytes", (unsigned long)b.length);
        fire_gfxb_setError(text);
        return Int(-1);
    }
    bytes = b.data;
    if (fb->indices) memcpy(fb->indices, bytes, expected);
    else for (i = 0, count = fire_gfx_fbPixelCount(fb); i < count; i++)
        fb->pixels[i] = bytes[i * 4] | ((uint32_t)bytes[i * 4 + 1] << 8) | ((uint32_t)bytes[i * 4 + 2] << 16) | ((uint32_t)bytes[i * 4 + 3] << 24);
    if (palette.kind == K_Buffer || palette.kind == K_Array) {
        if (!fire_gfxb_writePalette(fb, pb.data, pb.length)) {
            char text[sizeof g_gfxError.message + 32];
            fire_gfx_fbDelete(fb);
            g_gfxError.message[sizeof g_gfxError.message - 1] = 0;
            snprintf(text, sizeof text, "%s (Parameter 'data')", g_gfxError.message);
            fire_gfxb_setError(text);
            return Int(-1);
        }
    }
    return Int(fire_gfxb_addFramebuffer(fb));
}
static Value fire_gfx_FbLastError(OwnList* list) { return fire_gfxb_strOf8(g_gfxError.message, list); }
static Value fire_gfx_FbGetTransparentIndex(Value id) { return Int(fire_gfxb_fbOf(id)->transparentIndex); }
static Value fire_gfx_FbSetTransparentIndex(Value id, Value index) {
    fire_gfxb_fbOf(id)->transparentIndex = (int)fire_gfx_lmax(-1, fire_gfx_lmin(255, fire_gfxb_I(index)));
    return Undef();
}
static Value fire_gfx_FbToMask(Value id, Value threshold, Value darkIsRemoved, Value alphaThreshold) {
    FireGfxFramebuffer* src;
    FireGfxFramebuffer* mask;
    fire_gfxb_clearError();
    src = fire_gfxb_fbOf(id);
    mask = fire_gfx_fbNew();
    if (mask == NULL || !fire_gfx_makeMask(src, (uint8_t)fire_gfx_imax(0, fire_gfx_imin(255, fire_gfxb_I(threshold))), darkIsRemoved.i != 0,
                                           (uint8_t)fire_gfx_imax(0, fire_gfx_imin(255, fire_gfxb_I(alphaThreshold))), mask)) {
        fire_gfx_fbDelete(mask); fire_gfxb_setError("Not enough memory for the mask."); return Int(-1);
    }
    return Int(fire_gfxb_addFramebuffer(mask));
}

/* ---- the slicer ------------------------------------------------------------------------------------------------------------------------------------- */
static Value fire_gfx_SlcSlice(Value id, Value lineWidth, Value pixelSize, Value overlap, Value strategy, Value tolerance, Value flipY, OwnList* list) {
    int64_t i = (int32_t)id.i;
    FireGfxTables* t = fire_gfxb_tables();
    FireGfxSlicer s;
    FireGfxToolPaths paths = {NULL, 0, 0, 0};
    double tolerance_;
    Arr* outer;
    size_t k, j;
    fire_gfxb_clearError();
    if (!(i > 0 && (size_t)i < t->fbCount && t->framebuffers[(size_t)i])) {
        char text[96];
        snprintf(text, sizeof text, "No resource with ID %lld (unknown or already destroyed).", (long long)i);
        fire_gfxb_setError(text);
        return Int(-1);
    }
    fire_gfx_slicerInit(&s);
    s.lineWidth = fire_gfxb_D(lineWidth); s.pixelSize = fire_gfxb_D(pixelSize); s.overlap = fire_gfxb_D(overlap); s.strategy = fire_gfxb_I(strategy); s.flipY = flipY.i != 0;
    tolerance_ = fire_gfxb_D(tolerance);
    s.simplifyTolerance = tolerance_ < 0 ? NAN : tolerance_;
    if (s.lineWidth <= 0) { fire_gfxb_setError("Specified argument was out of the range of valid values."); return Int(-1); }
    fire_gfx_slice(&s, t->framebuffers[(size_t)i], &paths);
    if (paths.failed) { fire_gfx_pathsFree(&paths); fire_gfxb_outOfMemory(); }
    outer = allocArr((uint32_t)paths.count, list);
    /* the VM hands out plain arrays that live as long as something refers to them (the prelude puts the points into objects that outlive the call): here they
     * belong to the global scope (in the library for the VM there is none: they are copied to the VM with the result, the call's list keeps them until then) */
    for (k = 0; k < paths.count; k++) {
        Arr* entry = allocArr(3, g_globalOwn != NULL ? g_globalOwn : list);
        Arr* points = allocArr((uint32_t)paths.items[k].count * 2, g_globalOwn != NULL ? g_globalOwn : list);
        for (j = 0; j < paths.items[k].count; j++) { arrItems(points)[2 * j] = Float((Real)paths.items[k].points[j].x); arrItems(points)[2 * j + 1] = Float((Real)paths.items[k].points[j].y); }
        arrItems(entry)[0] = Int(paths.items[k].kind);
        arrItems(entry)[1] = Bool(paths.items[k].closed != 0);
        arrItems(entry)[2] = ArrV(points);
        arrItems(outer)[k] = ArrV(entry);
    }
    fire_gfx_pathsFree(&paths);
    return ArrV(outer);
}

/* ---- the renderer (text and drawing), brushes and pens -------------------------------------------------------------------------------------------------------- */
static Value fire_gfx_RndCreate(Value fbId) {
    int64_t i = (int32_t)fbId.i, id;
    FireGfxTables* t = fire_gfxb_tables();
    FireGfxRenderer* r;
    if (!(i > 0 && (size_t)i < t->fbCount && t->framebuffers[(size_t)i])) return Int(-1);
    r = fire_gfx_rendererNew(t->framebuffers[(size_t)i], 0);
    if (r == NULL) fire_gfxb_outOfMemory();
    id = fire_gfxb_push((void***)&t->renderers, &t->rndCount, &t->rndCap, r);
    if (id < 0) fire_gfxb_outOfMemory();
    return Int(id);
}
static Value fire_gfx_RndDestroy(Value id) {
    int64_t i = (int32_t)id.i;
    FireGfxTables* t = fire_gfxb_tables();
    if (i > 0 && (size_t)i < t->rndCount && t->renderers[(size_t)i]) { fire_gfx_rendererDelete(t->renderers[(size_t)i]); t->renderers[(size_t)i] = NULL; return Bool(true); }
    return Bool(false);
}
static Value fire_gfx_RndPrint(Value id, Value text) { const Str* s = strOf(text); fire_gfx_rendererPrint(fire_gfxb_rendererOf(id), s->data, s->length); return Undef(); }
static Value fire_gfx_RndLocate(Value id, Value row, Value column) { fire_gfx_rendererLocate(fire_gfxb_rendererOf(id), fire_gfxb_I(row), fire_gfxb_I(column)); return Undef(); }
static Value fire_gfx_RndClear(Value id) { fire_gfx_rendererClear(fire_gfxb_rendererOf(id)); return Undef(); }
static Value fire_gfx_RndClearTo(Value id, Value color) { fire_gfx_rendererClearTo(fire_gfxb_rendererOf(id), fire_gfx_paintArgument(fire_gfxb_I(color))); return Undef(); }
static Value fire_gfx_RndSetColor(Value id, Value fg, Value bg) { fire_gfx_rendererSetColor(fire_gfxb_rendererOf(id), fire_gfx_paintArgument(fire_gfxb_I(fg)), fire_gfx_paintArgument(fire_gfxb_I(bg))); return Undef(); }
static Value fire_gfx_RndSetPixel(Value id, Value x, Value y, Value color) {
    FireGfxSurface s = fire_gfx_rendererSurface(fire_gfxb_rendererOf(id));
    FireGfxPixel p = fire_gfx_resolve(&s, fire_gfx_paintArgument(fire_gfxb_I(color)));
    fire_gfx_put(&s, fire_gfxb_I(x), fire_gfxb_I(y), &p);
    return Undef();
}
/* A pixel as a number for the script: a value that would read as a palette index (only the R byte used, so always transparent) is the canonical transparent 0x100. */
static Value fire_gfx_RndGetPixel(Value id, Value x, Value y) {
    uint32_t p = fire_gfx_fbGetPixel(fire_gfxb_rendererOf(id)->target, fire_gfxb_I(x), fire_gfxb_I(y));
    return Int((p & 0xFFFFFF00u) == 0 ? 0x100 : (int32_t)p);
}
static Value fire_gfx_RndGetPixelIndex(Value id, Value x, Value y) { return Int(fire_gfx_fbGetIndex(fire_gfxb_rendererOf(id)->target, fire_gfxb_I(x), fire_gfxb_I(y))); }
static Value fire_gfx_RndCellWidth(Value id) { return Int(fire_gfxb_rendererOf(id)->cellWidth); }
static Value fire_gfx_RndCellHeight(Value id) { return Int(fire_gfxb_rendererOf(id)->cellHeight); }
static Value fire_gfx_RndGetAlphaBlending(Value id) { return Bool(fire_gfxb_rendererOf(id)->alphaBlending != 0); }
static Value fire_gfx_RndSetClip(Value id, Value x, Value y, Value w, Value h) { fire_gfx_rendererSetClip(fire_gfxb_rendererOf(id), fire_gfxb_I(x), fire_gfxb_I(y), fire_gfxb_I(w), fire_gfxb_I(h)); return Undef(); }
static Value fire_gfx_RndResetClip(Value id) { fire_gfx_rendererResetClip(fire_gfxb_rendererOf(id)); return Undef(); }
static Value fire_gfx_RndSetAlphaBlending(Value id, Value on) { fire_gfxb_rendererOf(id)->alphaBlending = on.i != 0; return Undef(); }
/* The colour of a paint for an RGBA framebuffer (a palette index through its palette). */
static uint32_t fire_gfxb_rgbaOfPaint(const FireGfxFramebuffer* fb, FireGfxPaint paint) {
    return fire_gfx_paintIsIndex(paint) ? fire_gfx_paletteGet(&fb->palette, (uint8_t)paint.index) : paint.rgba;
}
/* Text on the GPU when the framebuffer has a twin there (1: drawn; 0: the software draws it). */
static int fire_gfxb_gpuText(FireGfxRenderer* r, FireGfxTextFont* font, int size, int x, int y, const Str* s, const FireGfxBrush* fg, const FireGfxBrush* bg) {
    int clip[4];
    if (r->target->gpu == NULL) return 0;
    clip[0] = r->clipLeft; clip[1] = r->clipTop; clip[2] = r->clipRight; clip[3] = r->clipBottom;
    return fire_gfx_gpuText(r->target, clip, !r->alphaBlending, font, r->smallFont, r->cellWidth, r->cellHeight, size, x, y, s->data, s->length,
                            fire_gfxb_rgbaOfPaint(r->target, fg->color), bg != NULL, bg != NULL ? fire_gfxb_rgbaOfPaint(r->target, bg->color) : 0);
}

static Value fire_gfx_RndDrawText(Value id, Value x, Value y, Value text, Value fg, Value bg) {
    FireGfxRenderer* r = fire_gfxb_rendererOf(id);
    const Str* s = strOf(text);
    if (fire_gfxb_gpuText(r, NULL, 0, fire_gfxb_I(x), fire_gfxb_I(y), s, fire_gfxb_brushOf(fg), fire_gfxb_I(bg) == 0 ? NULL : fire_gfxb_brushOf(bg))) return Undef();
    fire_gfx_rendererDrawText(r, fire_gfxb_I(x), fire_gfxb_I(y), s->data, s->length, fire_gfxb_brushOf(fg), fire_gfxb_I(bg) == 0 ? NULL : fire_gfxb_brushOf(bg));
    return Undef();
}

static Value fire_gfx_RndDrawTextFont(Value id, Value x, Value y, Value text, Value fg, Value bg, Value font, Value size) {
    FireGfxRenderer* r = fire_gfxb_rendererOf(id);
    const Str* s = strOf(text);
    int64_t f = (int32_t)font.i;
    FireGfxTextFont* tf = f == 0 ? NULL : fire_gfx_fontTableGet(&fire_gfxb_tables()->fonts, f);
    if (f != 0 && !tf) fire_gfxb_noResource(f);
    if (fire_gfxb_gpuText(r, tf, fire_gfxb_I(size), fire_gfxb_I(x), fire_gfxb_I(y), s, fire_gfxb_brushOf(fg), fire_gfxb_I(bg) == 0 ? NULL : fire_gfxb_brushOf(bg))) return Undef();
    fire_gfx_drawTextIn(r, tf, fire_gfxb_I(size), fire_gfxb_I(x), fire_gfxb_I(y), s->data, s->length, fire_gfxb_brushOf(fg), fire_gfxb_I(bg) == 0 ? NULL : fire_gfxb_brushOf(bg));
    return Undef();
}

/* The points of a polygon from a script array `[x0, y0, x1, y1, ...]`: floats are cut off, an odd last element does not count. malloc'd (*count numbers). */
static int* fire_gfxb_readPoints(Value array, size_t* count) {
    Arr* a;
    uint32_t n, i;
    int* points;
    if (array.kind != K_Array) fatal("A polygon needs an array of coordinates.");
    a = arrOf(array);
    n = a->length - a->length % 2;
    points = (int*)malloc(sizeof(int) * (n ? n : 1));
    if (points == NULL) fire_gfxb_outOfMemory();
    for (i = 0; i < n; i++) {
        Value v = arrItems(a)[i];
        points[i] = v.kind == K_Float ? (int)v.f : (int)v.i;
    }
    *count = n;
    return points;
}

/* A shape on the GPU when the framebuffer has a twin there (1: drawn - or nothing to draw; 0: the software draws it). */
static int fire_gfxb_gpuShape(FireGfxRenderer* r, int x, int y, int w, int h, int radius, int stroke, FireGfxPaint paint) {
    uint32_t rgba;
    if (r->target->gpu == NULL) return 0;
    rgba = fire_gfx_paintIsIndex(paint) ? fire_gfx_paletteGet(&r->target->palette, (uint8_t)paint.index) : paint.rgba;
    if (r->alphaBlending && (rgba >> 24) == 0) return 1;
    return fire_gfx_gpuShape(r->target, r->clipLeft, r->clipTop, r->clipRight, r->clipBottom, x, y, w, h, radius, stroke, rgba, !r->alphaBlending);
}

/* fills (brush) */
static Value fire_gfx_RndFillRect(Value id, Value x, Value y, Value w, Value h, Value brush) {
    FireGfxRenderer* r = fire_gfxb_rendererOf(id);
    FireGfxSurface s;
    if (fire_gfxb_gpuShape(r, fire_gfxb_I(x), fire_gfxb_I(y), fire_gfxb_I(w), fire_gfxb_I(h), 0, 0, fire_gfxb_brushOf(brush)->color)) return Undef();
    s = fire_gfx_rendererSurface(r);
    fire_gfx_brushFillRect(fire_gfxb_brushOf(brush), &s, fire_gfxb_I(x), fire_gfxb_I(y), fire_gfxb_I(w), fire_gfxb_I(h));
    return Undef();
}
/* rounded rectangles and effects (fire_gfx_fx.h) */
static Value fire_gfx_RndFillRoundRect(Value id, Value x, Value y, Value w, Value h, Value radius, Value brush) {
    FireGfxRenderer* r = fire_gfxb_rendererOf(id);
    FireGfxSurface s;
    if (fire_gfxb_gpuShape(r, fire_gfxb_I(x), fire_gfxb_I(y), fire_gfxb_I(w), fire_gfxb_I(h), fire_gfxb_I(radius), 0, fire_gfxb_brushOf(brush)->color)) return Undef();
    s = fire_gfx_rendererSurface(r);
    FireGfxPixel p = fire_gfx_resolve(&s, fire_gfxb_brushOf(brush)->color);
    fire_gfx_fxFillRoundRect(&s, fire_gfxb_I(x), fire_gfxb_I(y), fire_gfxb_I(w), fire_gfxb_I(h), fire_gfxb_I(radius), &p);
    return Undef();
}
static Value fire_gfx_RndDrawRoundRect(Value id, Value x, Value y, Value w, Value h, Value radius, Value pen) {
    FireGfxRenderer* r = fire_gfxb_rendererOf(id);
    FireGfxSurface s;
    FireGfxPen* pn = fire_gfxb_penOf(pen);
    FireGfxPixel p;
    if (fire_gfxb_gpuShape(r, fire_gfxb_I(x), fire_gfxb_I(y), fire_gfxb_I(w), fire_gfxb_I(h), fire_gfxb_I(radius), pn->width, pn->color)) return Undef();
    s = fire_gfx_rendererSurface(r);
    p = fire_gfx_resolve(&s, pn->color);
    fire_gfx_fxStrokeRoundRect(&s, fire_gfxb_I(x), fire_gfxb_I(y), fire_gfxb_I(w), fire_gfxb_I(h), fire_gfxb_I(radius), pn->width, &p);
    return Undef();
}
static Value fire_gfx_RndDrawShadow(Value id, Value x, Value y, Value w, Value h, Value radius, Value soft, Value color, Value dy) {
    FireGfxRenderer* r = fire_gfxb_rendererOf(id);
    FireGfxSurface s;
    if (r->target->gpu != NULL && fire_gfx_gpuShadow(r->target, r->clipLeft, r->clipTop, r->clipRight, r->clipBottom, r->alphaBlending, fire_gfxb_I(x), fire_gfxb_I(y), fire_gfxb_I(w),
                                                     fire_gfxb_I(h), fire_gfxb_I(radius), fire_gfxb_I(soft), (uint32_t)(int32_t)color.i, fire_gfxb_I(dy))) return Undef();
    s = fire_gfx_rendererSurface(r);
    fire_gfx_fxShadow(&s, fire_gfxb_I(x), fire_gfxb_I(y), fire_gfxb_I(w), fire_gfxb_I(h), fire_gfxb_I(radius), fire_gfxb_I(soft), (uint32_t)(int32_t)color.i, fire_gfxb_I(dy));
    return Undef();
}
static Value fire_gfx_RndBlur(Value id, Value x, Value y, Value w, Value h, Value radius, Value corner) {
    FireGfxRenderer* r = fire_gfxb_rendererOf(id);
    FireGfxSurface s;
    if (r->target->gpu != NULL && fire_gfx_gpuBlur(r->target, r->clipLeft, r->clipTop, r->clipRight, r->clipBottom, fire_gfxb_I(x), fire_gfxb_I(y), fire_gfxb_I(w), fire_gfxb_I(h),
                                                   fire_gfxb_I(corner), fire_gfxb_I(radius))) return Undef();
    s = fire_gfx_rendererSurface(r);
    fire_gfx_fxBlur(&s, fire_gfxb_I(x), fire_gfxb_I(y), fire_gfxb_I(w), fire_gfxb_I(h), fire_gfxb_I(corner), fire_gfxb_I(radius));
    return Undef();
}
static Value fire_gfx_RndDrawMaterial(Value id, Value kind, Value x, Value y, Value w, Value h, Value radius, Value tint, Value base, Value flags) {
    FireGfxRenderer* r = fire_gfxb_rendererOf(id);
    FireGfxSurface s;
    if (r->target->gpu != NULL && fire_gfx_gpuMaterial(r->target, r->clipLeft, r->clipTop, r->clipRight, r->clipBottom, r->alphaBlending, fire_gfxb_I(x), fire_gfxb_I(y), fire_gfxb_I(w),
                                                       fire_gfxb_I(h), fire_gfxb_I(radius), fire_gfxb_I(kind), (uint32_t)(int32_t)tint.i, (uint32_t)(int32_t)base.i,
                                                       fire_gfxb_I(flags))) return Undef();
    s = fire_gfx_rendererSurface(r);
    fire_gfx_fxMaterial(&s, fire_gfxb_I(x), fire_gfxb_I(y), fire_gfxb_I(w), fire_gfxb_I(h), fire_gfxb_I(radius), fire_gfxb_I(kind), (uint32_t)(int32_t)tint.i, (uint32_t)(int32_t)base.i,
                        fire_gfxb_I(flags));
    return Undef();
}
static Value fire_gfx_RndFill(Value id, Value brush) {
    FireGfxRenderer* r = fire_gfxb_rendererOf(id);
    FireGfxSurface s = fire_gfx_rendererSurface(r);
    fire_gfx_brushFillRect(fire_gfxb_brushOf(brush), &s, 0, 0, r->target->width, r->target->height);
    return Undef();
}
static Value fire_gfx_RndFillCircle(Value id, Value cx, Value cy, Value rad, Value brush) {
    FireGfxSurface s = fire_gfx_rendererSurface(fire_gfxb_rendererOf(id));
    fire_gfx_brushFillCircle(fire_gfxb_brushOf(brush), &s, fire_gfxb_I(cx), fire_gfxb_I(cy), fire_gfxb_I(rad));
    return Undef();
}
static Value fire_gfx_RndFillEllipse(Value id, Value cx, Value cy, Value rx, Value ry, Value brush) {
    FireGfxSurface s = fire_gfx_rendererSurface(fire_gfxb_rendererOf(id));
    fire_gfx_brushFillEllipse(fire_gfxb_brushOf(brush), &s, fire_gfxb_I(cx), fire_gfxb_I(cy), fire_gfxb_I(rx), fire_gfxb_I(ry));
    return Undef();
}
static Value fire_gfx_RndFillTriangle(Value id, Value x0, Value y0, Value x1, Value y1, Value x2, Value y2, Value brush) {
    FireGfxSurface s = fire_gfx_rendererSurface(fire_gfxb_rendererOf(id));
    fire_gfx_brushFillTriangle(fire_gfxb_brushOf(brush), &s, fire_gfxb_I(x0), fire_gfxb_I(y0), fire_gfxb_I(x1), fire_gfxb_I(y1), fire_gfxb_I(x2), fire_gfxb_I(y2));
    return Undef();
}
static Value fire_gfx_RndFillPolygon(Value id, Value points, Value brush) {
    FireGfxSurface s;
    int* p;
    size_t n;
    if (!leafAlive(points)) return destroyedError(points);
    s = fire_gfx_rendererSurface(fire_gfxb_rendererOf(id));
    p = fire_gfxb_readPoints(points, &n);
    fire_gfx_brushFillPolygon(fire_gfxb_brushOf(brush), &s, p, n);
    free(p);
    return Undef();
}
static Value fire_gfx_RndFloodFill(Value id, Value x, Value y, Value brush) {
    FireGfxSurface s = fire_gfx_rendererSurface(fire_gfxb_rendererOf(id));
    fire_gfx_brushFloodFill(fire_gfxb_brushOf(brush), &s, fire_gfxb_I(x), fire_gfxb_I(y));
    return Undef();
}
static Value fire_gfx_RndFloodFillBorder(Value id, Value x, Value y, Value brush, Value border) {
    FireGfxSurface s = fire_gfx_rendererSurface(fire_gfxb_rendererOf(id));
    fire_gfx_brushFloodFillBorder(fire_gfxb_brushOf(brush), &s, fire_gfxb_I(x), fire_gfxb_I(y), fire_gfx_paintArgument(fire_gfxb_I(border)));
    return Undef();
}

/* draws (pen) */
static Value fire_gfx_RndDrawPoint(Value id, Value x, Value y, Value pen) {
    FireGfxSurface s = fire_gfx_rendererSurface(fire_gfxb_rendererOf(id));
    fire_gfx_penDrawPoint(fire_gfxb_penOf(pen), &s, fire_gfxb_I(x), fire_gfxb_I(y));
    return Undef();
}
static Value fire_gfx_RndDrawLine(Value id, Value x0, Value y0, Value x1, Value y1, Value pen) {
    FireGfxSurface s = fire_gfx_rendererSurface(fire_gfxb_rendererOf(id));
    fire_gfx_penDrawLine(fire_gfxb_penOf(pen), &s, fire_gfxb_I(x0), fire_gfxb_I(y0), fire_gfxb_I(x1), fire_gfxb_I(y1));
    return Undef();
}
static Value fire_gfx_RndDrawPath(Value id, Value points, Value pen, Value closed) {
    FireGfxSurface s;
    int* p;
    size_t n;
    if (!leafAlive(points)) return destroyedError(points);
    s = fire_gfx_rendererSurface(fire_gfxb_rendererOf(id));
    p = fire_gfxb_readPoints(points, &n);
    fire_gfx_penDrawPath(fire_gfxb_penOf(pen), &s, p, n, closed.i != 0);
    free(p);
    return Undef();
}
static Value fire_gfx_RndDrawRect(Value id, Value x, Value y, Value w, Value h, Value pen) {
    FireGfxRenderer* r = fire_gfxb_rendererOf(id);
    FireGfxSurface s;
    FireGfxPen* pn = fire_gfxb_penOf(pen);
    /* (a pen of one pixel: an outline on the GPU; a wider one stamps its tip - the software) */
    if (pn->width == 1 && fire_gfxb_gpuShape(r, fire_gfxb_I(x), fire_gfxb_I(y), fire_gfxb_I(w), fire_gfxb_I(h), 0, 1, pn->color)) return Undef();
    s = fire_gfx_rendererSurface(r);
    fire_gfx_penDrawRect(fire_gfxb_penOf(pen), &s, fire_gfxb_I(x), fire_gfxb_I(y), fire_gfxb_I(w), fire_gfxb_I(h));
    return Undef();
}
static Value fire_gfx_RndDrawCircle(Value id, Value cx, Value cy, Value rad, Value pen) {
    FireGfxSurface s = fire_gfx_rendererSurface(fire_gfxb_rendererOf(id));
    fire_gfx_penDrawCircle(fire_gfxb_penOf(pen), &s, fire_gfxb_I(cx), fire_gfxb_I(cy), fire_gfxb_I(rad));
    return Undef();
}
static Value fire_gfx_RndDrawEllipse(Value id, Value cx, Value cy, Value rx, Value ry, Value pen) {
    FireGfxSurface s = fire_gfx_rendererSurface(fire_gfxb_rendererOf(id));
    fire_gfx_penDrawEllipse(fire_gfxb_penOf(pen), &s, fire_gfxb_I(cx), fire_gfxb_I(cy), fire_gfxb_I(rx), fire_gfxb_I(ry));
    return Undef();
}
static Value fire_gfx_RndDrawTriangle(Value id, Value x0, Value y0, Value x1, Value y1, Value x2, Value y2, Value pen) {
    FireGfxSurface s = fire_gfx_rendererSurface(fire_gfxb_rendererOf(id));
    fire_gfx_penDrawTriangle(fire_gfxb_penOf(pen), &s, fire_gfxb_I(x0), fire_gfxb_I(y0), fire_gfxb_I(x1), fire_gfxb_I(y1), fire_gfxb_I(x2), fire_gfxb_I(y2));
    return Undef();
}
static Value fire_gfx_RndDrawPolygon(Value id, Value points, Value pen, Value closed) {
    FireGfxSurface s;
    int* p;
    size_t n;
    if (!leafAlive(points)) return destroyedError(points);
    s = fire_gfx_rendererSurface(fire_gfxb_rendererOf(id));
    p = fire_gfxb_readPoints(points, &n);
    fire_gfx_penDrawPolygon(fire_gfxb_penOf(pen), &s, p, n, closed.i != 0);
    free(p);
    return Undef();
}
static Value fire_gfx_RndBlit(Value id, Value src, Value sx, Value sy, Value sw, Value sh, Value dx, Value dy, Value dw, Value dh, Value mode, Value key) {
    FireGfxRenderer* r = fire_gfxb_rendererOf(id);
    if (r->target->gpu != NULL && fire_gfx_gpuBlit(r->target, fire_gfxb_fbOf(src), fire_gfxb_I(sx), fire_gfxb_I(sy), fire_gfxb_I(sw), fire_gfxb_I(sh), fire_gfxb_I(dx), fire_gfxb_I(dy),
                                                   fire_gfxb_I(dw), fire_gfxb_I(dh), fire_gfxb_I(mode), r->alphaBlending, r->clipLeft, r->clipTop, r->clipRight, r->clipBottom)) return Undef();
    fire_gfx_blit(r->target, fire_gfxb_fbOf(src), fire_gfxb_I(sx), fire_gfxb_I(sy), fire_gfxb_I(sw), fire_gfxb_I(sh), fire_gfxb_I(dx), fire_gfxb_I(dy), fire_gfxb_I(dw), fire_gfxb_I(dh),
                  fire_gfxb_I(mode), fire_gfxb_I(key), r->alphaBlending, r->clipLeft, r->clipTop, r->clipRight, r->clipBottom);
    return Undef();
}

/* The colour as a script gives it: a palette index 0-255 or the direct value. */
/* (a color as the script set it: unsigned, 0xAABBGGRR - as UI.Color.Rgb makes it) */
static Value fire_gfxb_colorNumber(FireGfxPaint p) { return Int(fire_gfx_paintIsIndex(p) ? (int64_t)p.index : (int64_t)p.rgba); }

/* fonts (ID 0 = the font of the renderer, 1 and 2 = the built-in bitmap fonts, -1 = none) */
static FireGfxTextFont* fire_gfxb_fontOf(Value id) {
    int64_t i = (int32_t)id.i;
    FireGfxTextFont* f = fire_gfx_fontTableGet(&fire_gfxb_tables()->fonts, i);
    if (!f) fire_gfxb_noResource(i);
    return f;
}
static Value fire_gfx_FntLoad(Value data) {
    FireBytes b;
    if (!bytesArg(data, "Font.Load", &b)) return Undef();
    return Int(fire_gfx_fontTableLoad(&fire_gfxb_tables()->fonts, b.data, b.length));
}
static Value fire_gfx_FntAdd(Value data, Value alias) {
    FireBytes b;
    char* name;
    int id;
    if (!bytesArg(data, "Font.Add", &b)) return Undef();
    name = fire_gfxb_utf8Of(alias);
    id = fire_gfx_fontTableAdd(&fire_gfxb_tables()->fonts, b.data, b.length, name);
    free(name);
    return Int(id);
}
static Value fire_gfx_FntOpen(Value name) {
    char* text = fire_gfxb_utf8Of(name);
    int id = fire_gfx_fontTableOpen(&fire_gfxb_tables()->fonts, text);
    free(text);
    return Int(id);
}
static Value fire_gfx_FntDestroy(Value id) { return Bool(fire_gfx_fontTableDestroy(&fire_gfxb_tables()->fonts, (int32_t)id.i) != 0); }
static Value fire_gfx_FntName(Value id, OwnList* list) { const FireGfxTextFont* f = fire_gfxb_fontOf(id); return fire_gfxb_strFromUtf8(f->name ? f->name : "", list); }
static Value fire_gfx_FntIsBitmap(Value id) { return Bool(fire_gfxb_fontOf(id)->bitmap != 0); }
static Value fire_gfx_FntAscent(Value id, Value size) { return Int(fire_gfx_fontAscent(fire_gfxb_fontOf(id), fire_gfxb_I(size))); }
static Value fire_gfx_FntHeight(Value id, Value size) { return Int(fire_gfx_fontLineHeight(fire_gfxb_fontOf(id), fire_gfxb_I(size))); }
static Value fire_gfx_FntMeasure(Value id, Value size, Value text) { const Str* s = strOf(text); return Int(fire_gfx_fontMeasure(fire_gfxb_fontOf(id), s->data, s->length, fire_gfxb_I(size))); }

/* brushes */
static Value fire_gfx_BshCreateSolid(Value color) {
    FireGfxTables* t = fire_gfxb_tables();
    FireGfxBrush* b = (FireGfxBrush*)malloc(sizeof(FireGfxBrush));
    int64_t id;
    if (b == NULL) fire_gfxb_outOfMemory();
    b->color = fire_gfx_paintArgument(fire_gfxb_I(color));
    id = fire_gfxb_push((void***)&t->brushes, &t->bshCount, &t->bshCap, b);
    if (id < 0) fire_gfxb_outOfMemory();
    return Int(id);
}
static Value fire_gfx_BshDestroy(Value id) {
    int64_t i = (int32_t)id.i;
    FireGfxTables* t = fire_gfxb_tables();
    if (i > 0 && (size_t)i < t->bshCount && t->brushes[(size_t)i]) { free(t->brushes[(size_t)i]); t->brushes[(size_t)i] = NULL; return Bool(true); }
    return Bool(false);
}
static Value fire_gfx_BshGetColor(Value id) { return fire_gfxb_colorNumber(fire_gfxb_brushOf(id)->color); }
static Value fire_gfx_BshSetColor(Value id, Value color) { fire_gfxb_brushOf(id)->color = fire_gfx_paintArgument(fire_gfxb_I(color)); return Undef(); }

/* pens */
static Value fire_gfx_PenCreate(Value color, Value width, Value shape) {
    FireGfxTables* t = fire_gfxb_tables();
    FireGfxPen* p = (FireGfxPen*)malloc(sizeof(FireGfxPen));
    int64_t id;
    if (p == NULL) fire_gfxb_outOfMemory();
    fire_gfx_penInit(p, fire_gfx_paintArgument(fire_gfxb_I(color)), fire_gfxb_I(width), fire_gfxb_I(shape));
    id = fire_gfxb_push((void***)&t->pens, &t->penCount, &t->penCap, p);
    if (id < 0) fire_gfxb_outOfMemory();
    return Int(id);
}
static Value fire_gfx_PenDestroy(Value id) {
    int64_t i = (int32_t)id.i;
    FireGfxTables* t = fire_gfxb_tables();
    if (i > 0 && (size_t)i < t->penCount && t->pens[(size_t)i]) { free(t->pens[(size_t)i]); t->pens[(size_t)i] = NULL; return Bool(true); }
    return Bool(false);
}
static Value fire_gfx_PenGetColor(Value id) { return fire_gfxb_colorNumber(fire_gfxb_penOf(id)->color); }
static Value fire_gfx_PenSetColor(Value id, Value color) { fire_gfxb_penOf(id)->color = fire_gfx_paintArgument(fire_gfxb_I(color)); return Undef(); }
static Value fire_gfx_PenGetWidth(Value id) { return Int(fire_gfxb_penOf(id)->width); }
static Value fire_gfx_PenSetWidth(Value id, Value width) { fire_gfx_penSetWidth(fire_gfxb_penOf(id), fire_gfxb_I(width)); return Undef(); }
static Value fire_gfx_PenGetShape(Value id) { return Int(fire_gfxb_penOf(id)->shape); }
static Value fire_gfx_PenSetShape(Value id, Value shape) { fire_gfx_penSetShape(fire_gfxb_penOf(id), fire_gfxb_I(shape)); return Undef(); }

/* (the floating point options of fire_gfx_ttf.h end with the graphics sources) */
#if defined(__GNUC__) && !defined(__clang__)
#pragma GCC pop_options
#endif

#endif
