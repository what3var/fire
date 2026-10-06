// fire native bridge "graphics": the natives behind `#import "graphics"` (docs/CONSOLE.md) - framebuffers, the palette, images (PNG, BMP, GIF), the renderer (text and
// drawing with brushes and pens) and the slicer. The fire side (Framebuffer, Renderer, Brush, Pen, Slicer, ToolPath - fire source) is the same as in the VM; this file is what its `__GRPH...` functions do.
// The drawing itself is in graphics/ (a port of src/fire.Terminal that needs no operating system). A window that shows a framebuffer is the `windows` bridge.
//
// Included by the generated file (after fire_rt.hpp) when the program imports "graphics". Image files are read through the file system of the platform
// (FIRE_PLATFORM_FS_HEADER, restricted by FIRE_IO_POLICY like the IO bridge). Differences to the VM: an unknown or destroyed resource id ends the program
// with an error (the VM throws a .NET exception); the renderer uses the 8x14 font.
#pragma once

#include <cstring>
#include FIRE_PLATFORM_FS_HEADER
#include "graphics/fire_gfx.hpp"
#include "graphics/fire_gfx_images.hpp"
#include "graphics/fire_gfx_slicer.hpp"

namespace fire {
namespace gfx {

/// The framebuffers, renderers, brushes and pens by id (ids start at 1 and are not used again, like the VM's IdManager).
struct Tables {
    std::vector<Framebuffer*> framebuffers{1, nullptr};
    std::vector<Renderer*> renderers{1, nullptr};
    std::vector<Brush*> brushes{1, nullptr};
    std::vector<Pen*> pens{1, nullptr};
    ~Tables() {
        for (Renderer* c : renderers) delete c;
        for (Brush* b : brushes) delete b;
        for (Pen* p : pens) delete p;
        for (Framebuffer* f : framebuffers) if (f) fbRelease(f);
    }
};
inline Tables& tables() { static Tables t; return t; }

/// A framebuffer by id; fatal when the id is not valid (like the VM's KeyNotFoundException).
inline Framebuffer* fbOf(Value id) {
    int64_t i = (int32_t)id.i;
    Tables& t = tables();
    if (i > 0 && (size_t)i < t.framebuffers.size() && t.framebuffers[(size_t)i]) return t.framebuffers[(size_t)i];
    fatal(("No resource with ID " + std::to_string(i) + " (unknown or already destroyed).").c_str());
}
inline Renderer* rendererOf(Value id) {
    int64_t i = (int32_t)id.i;
    Tables& t = tables();
    if (i > 0 && (size_t)i < t.renderers.size() && t.renderers[(size_t)i]) return t.renderers[(size_t)i];
    fatal(("No resource with ID " + std::to_string(i) + " (unknown or already destroyed).").c_str());
}
inline Brush* brushOf(Value id) {
    int64_t i = (int32_t)id.i;
    Tables& t = tables();
    if (i > 0 && (size_t)i < t.brushes.size() && t.brushes[(size_t)i]) return t.brushes[(size_t)i];
    fatal(("No resource with ID " + std::to_string(i) + " (unknown or already destroyed).").c_str());
}
inline Pen* penOf(Value id) {
    int64_t i = (int32_t)id.i;
    Tables& t = tables();
    if (i > 0 && (size_t)i < t.pens.size() && t.pens[(size_t)i]) return t.pens[(size_t)i];
    fatal(("No resource with ID " + std::to_string(i) + " (unknown or already destroyed).").c_str());
}
inline int addFramebuffer(Framebuffer* fb) { tables().framebuffers.push_back(fb); return (int)tables().framebuffers.size() - 1; }

inline int I(Value v) { return (int32_t)v.i; }
inline double D(Value v) { return v.kind == K_Float ? (double)v.f : (double)v.i; }

inline void setError(const std::string& message) {
    size_t n = message.size() < sizeof g_gfxError.message - 1 ? message.size() : sizeof g_gfxError.message - 1;
    std::memcpy(g_gfxError.message, message.data(), n);
    g_gfxError.message[n] = 0;
    g_gfxError.code = 1;
}
inline void clearError() { g_gfxError.code = 0; g_gfxError.message[0] = 0; }

inline Value strOf8(const std::string& text, OwnList* list) {
    Str* s = allocStr((uint32_t)text.size(), list);
    // UTF-8 messages are plain ASCII here except for paths: widen what is ASCII, the rest as is
    char16_t* out = strChars(s);
    for (size_t i = 0; i < text.size(); i++) out[i] = (unsigned char)text[i];
    return StrV(s);
}

inline Value newBuffer(const uint8_t* data, size_t n, OwnList* list) {
    Buf* b = allocBuf((uint32_t)n, list);
    if (n) std::memcpy(b->bytes(), data, n);
    return BufV(b);
}

// ---- framebuffers ------------------------------------------------------------------------------------------------------------------------------------
inline Value FbCreate(Value w, Value h, Value mode) {
    Framebuffer* fb = new Framebuffer();
    if (!fb->create(I(w), I(h), I(mode))) { delete fb; return Int(-1); }
    return Int(addFramebuffer(fb));
}
inline Value FbDestroy(Value id) {
    int64_t i = (int32_t)id.i;
    Tables& t = tables();
    if (i > 0 && (size_t)i < t.framebuffers.size() && t.framebuffers[(size_t)i]) {
        fbRelease(t.framebuffers[(size_t)i]);
        t.framebuffers[(size_t)i] = nullptr;
        return Bool(true);
    }
    return Bool(false);
}
inline Value FbWidth(Value id) { return Int(fbOf(id)->width); }
inline Value FbHeight(Value id) { return Int(fbOf(id)->height); }
inline Value FbMode(Value id) { return Int(fbOf(id)->mode); }
inline Value FbByteCount(Value id) { return Int((int64_t)fbOf(id)->byteCount()); }

inline void checkByteOffset(const Framebuffer* fb, int64_t offset) {
    int64_t total = (int64_t)fb->byteCount();
    if (offset < 0 || offset >= total)
        fatal(("Byte offset " + std::to_string(offset) + " outside of the buffer (size " + std::to_string(total) + " bytes).").c_str());
}
inline Value FbReadByte(Value id, Value offset) {
    Framebuffer* fb = fbOf(id);
    int off = I(offset);
    checkByteOffset(fb, off);
    if (fb->indices) return Int(fb->indices[off]);
    return Int((fb->pixels[off / 4] >> ((off % 4) * 8)) & 0xFF);
}
inline Value FbWriteByte(Value id, Value offset, Value value) {
    Framebuffer* fb = fbOf(id);
    int off = I(offset);
    checkByteOffset(fb, off);
    uint8_t b = (uint8_t)value.i;
    if (fb->indices) { fb->indices[off] = b; return Undef(); }
    int shift = (off % 4) * 8;
    uint32_t& px = fb->pixels[off / 4];
    px = (px & ~(0xFFu << shift)) | ((uint32_t)b << shift);
    return Undef();
}
/// R, G, B, A bytes per pixel, whatever the byte order of the machine is.
inline Value FbReadBytes(Value id, OwnList* list) {
    Framebuffer* fb = fbOf(id);
    size_t n = fb->byteCount();
    Buf* b = allocBuf((uint32_t)n, list);
    if (fb->indices) std::memcpy(b->bytes(), fb->indices, n);
    else for (size_t i = 0, count = fb->pixelCount(); i < count; i++) {
        uint32_t px = fb->pixels[i];
        b->bytes()[i * 4] = (uint8_t)px; b->bytes()[i * 4 + 1] = (uint8_t)(px >> 8); b->bytes()[i * 4 + 2] = (uint8_t)(px >> 16); b->bytes()[i * 4 + 3] = (uint8_t)(px >> 24);
    }
    return BufV(b);
}
inline Value FbWriteBytes(Value id, Value data) {
    if (!leafAlive(data)) return destroyedError(data);
    clearError();
    Framebuffer* fb = fbOf(id);
    Buf* b = bufOf(data);
    size_t expected = fb->byteCount();
    if (b->length != expected) { setError("Expected exactly " + std::to_string(expected) + " bytes, got " + std::to_string(b->length) + "."); return Bool(false); }
    if (fb->indices) std::memcpy(fb->indices, b->bytes(), expected);
    else for (size_t i = 0, count = fb->pixelCount(); i < count; i++)
        fb->pixels[i] = b->bytes()[i * 4] | ((uint32_t)b->bytes()[i * 4 + 1] << 8) | ((uint32_t)b->bytes()[i * 4 + 2] << 16) | ((uint32_t)b->bytes()[i * 4 + 3] << 24);
    return Bool(true);
}

inline bool checkPaletteIndex(int64_t index) {
    if ((uint64_t)index > 255) { setError("Palette index " + std::to_string(index) + " outside of 0-255."); return false; }
    return true;
}
/// The colors of the palette are unsigned values (r + g*256 + b*65536 + a*16777216); -1 for an invalid index.
inline Value FbGetPaletteColor(Value id, Value index) {
    clearError();
    if (!checkPaletteIndex(I(index))) return Int(-1);
    return Int((int64_t)fbOf(id)->palette.get((uint8_t)I(index)));
}
inline Value FbSetPaletteColor(Value id, Value index, Value color) {
    clearError();
    if (!checkPaletteIndex(I(index))) return Bool(false);
    fbOf(id)->palette.set((uint8_t)I(index), (uint32_t)I(color));
    return Bool(true);
}
inline Value FbReadPalette(Value id, Value withAlpha, OwnList* list) {
    const Palette& p = fbOf(id)->palette;
    int stride = withAlpha.i ? 4 : 3;
    Buf* b = allocBuf((uint32_t)(256 * stride), list);
    for (int i = 0; i < 256; i++) {
        uint32_t c = p.entries[i];
        b->bytes()[i * stride] = (uint8_t)c; b->bytes()[i * stride + 1] = (uint8_t)(c >> 8); b->bytes()[i * stride + 2] = (uint8_t)(c >> 16);
        if (stride == 4) b->bytes()[i * stride + 3] = (uint8_t)(c >> 24);
    }
    return BufV(b);
}
inline bool writePalette(Framebuffer* fb, const uint8_t* data, size_t n) {
    if (n != 768 && n != 1024) { setError("A palette has 768 (RGB) or 1024 (RGBA) bytes, got " + std::to_string(n) + "."); return false; }
    int stride = n == 768 ? 3 : 4;
    uint32_t colors[256];
    for (int i = 0; i < 256; i++)
        colors[i] = data[i * stride] | ((uint32_t)data[i * stride + 1] << 8) | ((uint32_t)data[i * stride + 2] << 16) | ((stride == 4 ? (uint32_t)data[i * stride + 3] : 255u) << 24);
    fb->palette.setAll(colors, 256);
    return true;
}
inline Value FbWritePalette(Value id, Value data) {
    if (!leafAlive(data)) return destroyedError(data);
    clearError();
    Framebuffer* fb = fbOf(id);
    Buf* b = bufOf(data);
    return Bool(writePalette(fb, b->bytes(), b->length));
}

/// A new framebuffer from decoded image data: the id, or -1 with the message in LastError.
inline Value fromImage(const uint8_t* data, size_t n, int mode) {
    ImageData image;
    std::string err;
    if (!decodeImage(data, n, image, err)) { setError(err); return Int(-1); }
    Framebuffer* fb = new Framebuffer();
    if (!image.toFramebuffer(mode, *fb)) { delete fb; setError("Not enough memory for the image."); return Int(-1); }
    return Int(addFramebuffer(fb));
}
inline Value FbLoadImage(Value data, Value mode) {
    if (!leafAlive(data)) return destroyedError(data);
    clearError();
    Buf* b = bufOf(data);
    return fromImage(b->bytes(), b->length, mode.i < 0 ? -1 : I(mode));
}
inline Value FbLoadFile(Value path, Value mode) {
    clearError();
    const Str* s = strOf(path);
    std::string raw;
    for (uint32_t i = 0; i < s->length; i++) {
        uint32_t c = s->data[i];
        if (c < 0x80) raw.push_back((char)c);
        else if (c < 0x800) { raw.push_back((char)(0xC0 | (c >> 6))); raw.push_back((char)(0x80 | (c & 0x3F))); }
        else { raw.push_back((char)(0xE0 | (c >> 12))); raw.push_back((char)(0x80 | ((c >> 6) & 0x3F))); raw.push_back((char)(0x80 | (c & 0x3F))); }
    }
    std::string full = plat::fs::fullPath(raw);
#ifdef FIRE_IO_POLICY
    std::string reason;
    if (!FIRE_IO_POLICY(full, 1, reason)) { setError(reason.empty() ? "Access to '" + full + "' is not allowed." : reason); return Int(-1); }
#endif
    plat::fs::Status status;
    std::FILE* f = plat::fs::open(full, 0, 0, status);
    if (!f) { setError(status.message); return Int(-1); }
    std::vector<uint8_t> bytes;
    uint8_t chunk[4096];
    size_t got;
    while ((got = std::fread(chunk, 1, sizeof chunk, f)) > 0) bytes.insert(bytes.end(), chunk, chunk + got);
    std::fclose(f);
    return fromImage(bytes.data(), bytes.size(), mode.i < 0 ? -1 : I(mode));
}
/// Raw pixels, top to bottom: RGBA 4 bytes (R, G, B, A) or one palette index per pixel, optionally with a palette (768 or 1024 bytes).
inline Value FbFromPixels(Value w, Value h, Value pixels, Value mode, Value palette) {
    if (!leafAlive(pixels)) return destroyedError(pixels);
    clearError();
    int width = I(w), height = I(h), m = I(mode);
    Framebuffer* fb = new Framebuffer();
    if (width <= 0 || height <= 0) { delete fb; setError("The framebuffer size must be positive. (Parameter 'width')"); return Int(-1); }
    if (m != RGBA && m != INDEXED) { delete fb; setError("Unknown color mode. (Parameter 'mode')"); return Int(-1); }
    if (!fb->create(width, height, m)) { delete fb; setError("Not enough memory for the image."); return Int(-1); }
    Buf* b = bufOf(pixels);
    size_t expected = fb->byteCount();
    if (b->length != expected) {
        delete fb;
        setError("Expected exactly " + std::to_string(expected) + " bytes (" + std::to_string(width) + "x" + std::to_string(height) + ", " + (m == INDEXED ? "1 byte" : "4 bytes") + " per pixel), got " + std::to_string(b->length) + ". (Parameter 'pixels')");
        return Int(-1);
    }
    if (fb->indices) std::memcpy(fb->indices, b->bytes(), expected);
    else for (size_t i = 0, count = fb->pixelCount(); i < count; i++)
        fb->pixels[i] = b->bytes()[i * 4] | ((uint32_t)b->bytes()[i * 4 + 1] << 8) | ((uint32_t)b->bytes()[i * 4 + 2] << 16) | ((uint32_t)b->bytes()[i * 4 + 3] << 24);
    if (palette.kind == K_Buffer) {
        if (!leafAlive(palette)) { delete fb; return destroyedError(palette); }
        Buf* pb = bufOf(palette);
        if (!writePalette(fb, pb->bytes(), pb->length)) { delete fb; g_gfxError.message[sizeof g_gfxError.message - 1] = 0; std::string m2 = g_gfxError.message; setError(m2 + " (Parameter 'data')"); return Int(-1); }
    }
    return Int(addFramebuffer(fb));
}
inline Value FbLastError(OwnList* list) { return strOf8(g_gfxError.message, list); }
inline Value FbGetTransparentIndex(Value id) { return Int(fbOf(id)->transparentIndex); }
inline Value FbSetTransparentIndex(Value id, Value index) {
    fbOf(id)->transparentIndex = (int)std::max<int64_t>(-1, std::min<int64_t>(255, I(index)));
    return Undef();
}
inline Value FbToMask(Value id, Value threshold, Value darkIsRemoved, Value alphaThreshold) {
    clearError();
    Framebuffer* src = fbOf(id);
    Framebuffer* mask = new Framebuffer();
    if (!makeMask(*src, (uint8_t)std::max(0, std::min(255, I(threshold))), darkIsRemoved.i != 0, (uint8_t)std::max(0, std::min(255, I(alphaThreshold))), *mask)) {
        delete mask; setError("Not enough memory for the mask."); return Int(-1);
    }
    return Int(addFramebuffer(mask));
}

// ---- the slicer -------------------------------------------------------------------------------------------------------------------------------------
inline Value SlcSlice(Value id, Value lineWidth, Value pixelSize, Value overlap, Value strategy, Value tolerance, Value flipY, OwnList* list) {
    clearError();
    int64_t i = (int32_t)id.i;
    Tables& t = tables();
    if (!(i > 0 && (size_t)i < t.framebuffers.size() && t.framebuffers[(size_t)i])) { setError("No resource with ID " + std::to_string(i) + " (unknown or already destroyed)."); return Int(-1); }
    Slicer s;
    s.lineWidth = D(lineWidth); s.pixelSize = D(pixelSize); s.overlap = D(overlap); s.strategy = I(strategy); s.flipY = flipY.i != 0;
    double tolerance_ = D(tolerance);
    s.simplifyTolerance = tolerance_ < 0 ? NAN : tolerance_;
    if (s.lineWidth <= 0) { setError("Specified argument was out of the range of valid values."); return Int(-1); }
    std::vector<ToolPath> paths = s.slice(*t.framebuffers[(size_t)i]);
    Arr* outer = allocArr((uint32_t)paths.size(), list);
    // the VM hands out plain arrays that live as long as something refers to them (the prelude puts the points into objects that outlive the call): here they
    // belong to the global scope
    for (size_t k = 0; k < paths.size(); k++) {
        Arr* entry = allocArr(3, g_globalOwn);
        Arr* points = allocArr((uint32_t)paths[k].points.size() * 2, g_globalOwn);
        for (size_t j = 0; j < paths[k].points.size(); j++) { points->items()[2 * j] = Float((Real)paths[k].points[j].x); points->items()[2 * j + 1] = Float((Real)paths[k].points[j].y); }
        entry->items()[0] = Int(paths[k].kind);
        entry->items()[1] = Bool(paths[k].closed);
        entry->items()[2] = ArrV(points);
        outer->items()[k] = ArrV(entry);
    }
    return ArrV(outer);
}

// ---- the renderer (text and drawing), brushes and pens --------------------------------------------------------------------------------------------------------
inline Value RndCreate(Value fbId) {
    int64_t i = (int32_t)fbId.i;
    Tables& t = tables();
    if (!(i > 0 && (size_t)i < t.framebuffers.size() && t.framebuffers[(size_t)i])) return Int(-1);
    t.renderers.push_back(new Renderer(t.framebuffers[(size_t)i]));
    return Int((int64_t)t.renderers.size() - 1);
}
inline Value RndDestroy(Value id) {
    int64_t i = (int32_t)id.i;
    Tables& t = tables();
    if (i > 0 && (size_t)i < t.renderers.size() && t.renderers[(size_t)i]) { delete t.renderers[(size_t)i]; t.renderers[(size_t)i] = nullptr; return Bool(true); }
    return Bool(false);
}
inline Value RndPrint(Value id, Value text) { const Str* s = strOf(text); rendererOf(id)->print(s->data, s->length); return Undef(); }
inline Value RndLocate(Value id, Value row, Value column) { rendererOf(id)->locate(I(row), I(column)); return Undef(); }
inline Value RndClear(Value id) { rendererOf(id)->clear(); return Undef(); }
inline Value RndClearTo(Value id, Value color) { rendererOf(id)->clearTo(Paint::fromArgument(I(color))); return Undef(); }
inline Value RndSetColor(Value id, Value fg, Value bg) { rendererOf(id)->setColor(Paint::fromArgument(I(fg)), Paint::fromArgument(I(bg))); return Undef(); }
inline Value RndSetPixel(Value id, Value x, Value y, Value color) {
    Surface s = rendererOf(id)->surface();
    s.put(I(x), I(y), s.resolve(Paint::fromArgument(I(color))));
    return Undef();
}
inline Value RndGetPixel(Value id, Value x, Value y) { return Int((int32_t)rendererOf(id)->target->getPixel(I(x), I(y))); }
inline Value RndGetPixelIndex(Value id, Value x, Value y) { return Int(rendererOf(id)->target->getIndex(I(x), I(y))); }
inline Value RndCellWidth(Value id) { return Int(rendererOf(id)->cellWidth); }
inline Value RndCellHeight(Value id) { return Int(rendererOf(id)->cellHeight); }
inline Value RndGetAlphaBlending(Value id) { return Bool(rendererOf(id)->alphaBlending); }
inline Value RndSetAlphaBlending(Value id, Value on) { rendererOf(id)->alphaBlending = on.i != 0; return Undef(); }
inline Value RndDrawText(Value id, Value x, Value y, Value text, Value fg, Value bg) {
    Renderer* r = rendererOf(id);
    const Str* s = strOf(text);
    r->drawText(I(x), I(y), s->data, s->length, *brushOf(fg), I(bg) == 0 ? nullptr : brushOf(bg));
    return Undef();
}

/// The points of a polygon from a script array `[x0, y0, x1, y1, ...]`: floats are cut off, an odd last element does not count.
inline std::vector<int> readPoints(Value array) {
    std::vector<int> points;
    if (array.kind != K_Array) fatal("A polygon needs an array of coordinates.");
    Arr* a = arrOf(array);
    uint32_t n = a->length - a->length % 2;
    for (uint32_t i = 0; i < n; i++) {
        Value v = a->items()[i];
        points.push_back(v.kind == K_Float ? (int)v.f : (int)v.i);
    }
    return points;
}

// fills (brush)
inline Value RndFillRect(Value id, Value x, Value y, Value w, Value h, Value brush) { Renderer* r = rendererOf(id); Surface s = r->surface(); brushOf(brush)->fillRect(s, I(x), I(y), I(w), I(h)); return Undef(); }
inline Value RndFill(Value id, Value brush) { Renderer* r = rendererOf(id); Surface s = r->surface(); brushOf(brush)->fillRect(s, 0, 0, r->target->width, r->target->height); return Undef(); }
inline Value RndFillCircle(Value id, Value cx, Value cy, Value rad, Value brush) { Surface s = rendererOf(id)->surface(); brushOf(brush)->fillCircle(s, I(cx), I(cy), I(rad)); return Undef(); }
inline Value RndFillEllipse(Value id, Value cx, Value cy, Value rx, Value ry, Value brush) { Surface s = rendererOf(id)->surface(); brushOf(brush)->fillEllipse(s, I(cx), I(cy), I(rx), I(ry)); return Undef(); }
inline Value RndFillTriangle(Value id, Value x0, Value y0, Value x1, Value y1, Value x2, Value y2, Value brush) {
    Surface s = rendererOf(id)->surface();
    brushOf(brush)->fillTriangle(s, I(x0), I(y0), I(x1), I(y1), I(x2), I(y2));
    return Undef();
}
inline Value RndFillPolygon(Value id, Value points, Value brush) {
    if (!leafAlive(points)) return destroyedError(points);
    Surface s = rendererOf(id)->surface();
    brushOf(brush)->fillPolygon(s, readPoints(points));
    return Undef();
}
inline Value RndFloodFill(Value id, Value x, Value y, Value brush) { Surface s = rendererOf(id)->surface(); brushOf(brush)->floodFill(s, I(x), I(y)); return Undef(); }
inline Value RndFloodFillBorder(Value id, Value x, Value y, Value brush, Value border) {
    Surface s = rendererOf(id)->surface();
    brushOf(brush)->floodFillBorder(s, I(x), I(y), Paint::fromArgument(I(border)));
    return Undef();
}

// draws (pen)
inline Value RndDrawPoint(Value id, Value x, Value y, Value pen) { Surface s = rendererOf(id)->surface(); penOf(pen)->drawPoint(s, I(x), I(y)); return Undef(); }
inline Value RndDrawLine(Value id, Value x0, Value y0, Value x1, Value y1, Value pen) { Surface s = rendererOf(id)->surface(); penOf(pen)->drawLine(s, I(x0), I(y0), I(x1), I(y1)); return Undef(); }
inline Value RndDrawPath(Value id, Value points, Value pen, Value closed) {
    if (!leafAlive(points)) return destroyedError(points);
    Surface s = rendererOf(id)->surface();
    penOf(pen)->drawPath(s, readPoints(points), closed.i != 0);
    return Undef();
}
inline Value RndDrawRect(Value id, Value x, Value y, Value w, Value h, Value pen) { Surface s = rendererOf(id)->surface(); penOf(pen)->drawRect(s, I(x), I(y), I(w), I(h)); return Undef(); }
inline Value RndDrawCircle(Value id, Value cx, Value cy, Value rad, Value pen) { Surface s = rendererOf(id)->surface(); penOf(pen)->drawCircle(s, I(cx), I(cy), I(rad)); return Undef(); }
inline Value RndDrawEllipse(Value id, Value cx, Value cy, Value rx, Value ry, Value pen) { Surface s = rendererOf(id)->surface(); penOf(pen)->drawEllipse(s, I(cx), I(cy), I(rx), I(ry)); return Undef(); }
inline Value RndDrawTriangle(Value id, Value x0, Value y0, Value x1, Value y1, Value x2, Value y2, Value pen) {
    Surface s = rendererOf(id)->surface();
    penOf(pen)->drawTriangle(s, I(x0), I(y0), I(x1), I(y1), I(x2), I(y2));
    return Undef();
}
inline Value RndDrawPolygon(Value id, Value points, Value pen, Value closed) {
    if (!leafAlive(points)) return destroyedError(points);
    Surface s = rendererOf(id)->surface();
    penOf(pen)->drawPolygon(s, readPoints(points), closed.i != 0);
    return Undef();
}
inline Value RndBlit(Value id, Value src, Value sx, Value sy, Value sw, Value sh, Value dx, Value dy, Value dw, Value dh, Value mode, Value key) {
    Renderer* r = rendererOf(id);
    blit(*r->target, *fbOf(src), I(sx), I(sy), I(sw), I(sh), I(dx), I(dy), I(dw), I(dh), I(mode), I(key), r->alphaBlending);
    return Undef();
}

/// The colour as a script gives it: a palette index 0-255 or the direct value.
inline Value colorNumber(const Paint& p) { return Int(p.isIndex() ? (int64_t)p.index : (int64_t)(int32_t)p.rgba); }

// brushes
inline Value BshCreateSolid(Value color) {
    Tables& t = tables();
    Brush* b = new Brush();
    b->color = Paint::fromArgument(I(color));
    t.brushes.push_back(b);
    return Int((int64_t)t.brushes.size() - 1);
}
inline Value BshDestroy(Value id) {
    int64_t i = (int32_t)id.i;
    Tables& t = tables();
    if (i > 0 && (size_t)i < t.brushes.size() && t.brushes[(size_t)i]) { delete t.brushes[(size_t)i]; t.brushes[(size_t)i] = nullptr; return Bool(true); }
    return Bool(false);
}
inline Value BshGetColor(Value id) { return colorNumber(brushOf(id)->color); }
inline Value BshSetColor(Value id, Value color) { brushOf(id)->color = Paint::fromArgument(I(color)); return Undef(); }

// pens
inline Value PenCreate(Value color, Value width, Value shape) {
    Tables& t = tables();
    t.pens.push_back(new Pen(Paint::fromArgument(I(color)), I(width), I(shape)));
    return Int((int64_t)t.pens.size() - 1);
}
inline Value PenDestroy(Value id) {
    int64_t i = (int32_t)id.i;
    Tables& t = tables();
    if (i > 0 && (size_t)i < t.pens.size() && t.pens[(size_t)i]) { delete t.pens[(size_t)i]; t.pens[(size_t)i] = nullptr; return Bool(true); }
    return Bool(false);
}
inline Value PenGetColor(Value id) { return colorNumber(penOf(id)->color); }
inline Value PenSetColor(Value id, Value color) { penOf(id)->color = Paint::fromArgument(I(color)); return Undef(); }
inline Value PenGetWidth(Value id) { return Int(penOf(id)->width); }
inline Value PenSetWidth(Value id, Value width) { penOf(id)->setWidth(I(width)); return Undef(); }
inline Value PenGetShape(Value id) { return Int(penOf(id)->shape); }
inline Value PenSetShape(Value id, Value shape) { penOf(id)->setShape(I(shape)); return Undef(); }

}  // namespace gfx
}  // namespace fire
