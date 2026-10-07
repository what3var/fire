// fire native graphics: framebuffers (RGBA or palette), the palette, drawing (lines, rectangles, circles, ellipses, triangles, polygons, flood fill, text), blitting.
// A port of src/fire.Terminal (Framebuffer, Palette, Paint, Surface, Shapes, Brush, Pen, Blitter, Renderer) - the same pixels come out. No operating system, no exceptions: it runs on
// a microcontroller; a platform package only has to put the pixels on a screen (see ../fire_bridge_windows.hpp).
//
// A color is a "paint": a number 0..255 is an index into the palette of the framebuffer, anything else is a direct value r + g*256 + b*65536 + a*16777216.
#pragma once

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>
#include "fire_gfx_font.hpp"

namespace fire {
namespace gfx {

constexpr int RGBA = 0, INDEXED = 1;

inline uint32_t rgb(uint32_t r, uint32_t g, uint32_t b) { return r | (g << 8) | (b << 16) | 0xFF000000u; }

struct Paint {
    uint32_t rgba = 0;
    int16_t index = -1;
    bool isIndex() const { return index >= 0; }
    static Paint fromIndex(uint8_t i) { Paint p; p.index = i; return p; }
    static Paint fromRgba(uint32_t packed) { Paint p; p.rgba = packed; p.index = -1; return p; }
    /// An argument of a script: a number without bits above the lowest byte is a palette index.
    static Paint fromArgument(int64_t value) {
        uint32_t raw = (uint32_t)(int32_t)value;
        return (raw & 0xFFFFFF00u) == 0 ? fromIndex((uint8_t)raw) : fromRgba(raw);
    }
};

/// A color resolved for a target (see Surface::resolve): `rgba` is the value (its alpha counts when blending), `index` the palette entry for a palette framebuffer.
struct Pixel { uint32_t rgba = 0; uint8_t index = 0; };

struct Palette {
    uint32_t entries[256];
    int version = 0;

    Palette() { fillDefaults(); }
    void set(uint8_t i, uint32_t color) { entries[i] = color; version++; }
    uint32_t get(uint8_t i) const { return entries[i]; }
    void setAll(const uint32_t* colors, int n) {
        if (n > 256) n = 256;
        for (int i = 0; i < n; i++) entries[i] = colors[i];
        version++;
    }
    /// The entry that is closest to a color (the squared distance in R, G, B; the first one wins).
    uint8_t findNearest(uint32_t color) const {
        int r = (int)(color & 0xFF), g = (int)((color >> 8) & 0xFF), b = (int)((color >> 16) & 0xFF);
        int best = 0, bestDistance = 0x7FFFFFFF;
        for (int i = 0; i < 256; i++) {
            uint32_t p = entries[i];
            int dr = (int)(p & 0xFF) - r, dg = (int)((p >> 8) & 0xFF) - g, db = (int)((p >> 16) & 0xFF) - b;
            int distance = dr * dr + dg * dg + db * db;
            if (distance < bestDistance) {
                bestDistance = distance;
                best = i;
                if (distance == 0) break;
            }
        }
        return (uint8_t)best;
    }
    bool same(const Palette& o) const { return this == &o || std::memcmp(entries, o.entries, sizeof entries) == 0; }
private:
    /// The 16 colors of QBasic, the 6x6x6 color cube, 24 grays.
    void fillDefaults() {
        static const uint32_t qbasic[16] = {
            rgb(0, 0, 0), rgb(0, 0, 170), rgb(0, 170, 0), rgb(0, 170, 170), rgb(170, 0, 0), rgb(170, 0, 170), rgb(170, 85, 0), rgb(170, 170, 170),
            rgb(85, 85, 85), rgb(85, 85, 255), rgb(85, 255, 85), rgb(85, 255, 255), rgb(255, 85, 85), rgb(255, 85, 255), rgb(255, 255, 85), rgb(255, 255, 255)};
        for (int i = 0; i < 16; i++) entries[i] = qbasic[i];
        static const int levels[6] = {0, 51, 102, 153, 204, 255};
        int idx = 16;
        for (int r : levels) for (int g : levels) for (int b : levels) entries[idx++] = rgb((uint32_t)r, (uint32_t)g, (uint32_t)b);
        for (int i = 0; i < 24; i++) { uint32_t v = (uint32_t)(8 + i * 10); entries[232 + i] = rgb(v, v, v); }
    }
};

struct Framebuffer {
    int width = 0, height = 0, mode = RGBA;
    uint32_t* pixels = nullptr;   // RGBA: width*height
    uint8_t* indices = nullptr;   // palette mode: width*height
    Palette palette;
    int transparentIndex = -1;
    int refs = 1;   // the table of the bridge, every canvas and every window that shows it: it lives as long as one of them does

    Framebuffer() {}
    Framebuffer(const Framebuffer&) = delete;
    Framebuffer& operator=(const Framebuffer&) = delete;
    ~Framebuffer() { std::free(pixels); std::free(indices); }

    /// Allocates the pixels; false when the size is invalid or does not fit in memory.
    bool create(int64_t w, int64_t h, int m) {
        if (w <= 0 || h <= 0 || w > 0x7FFFFFFF || h > 0x7FFFFFFF || w * h > 0x7FFFFFFF || (m != RGBA && m != INDEXED)) return false;
        width = (int)w; height = (int)h; mode = m;
        size_t n = (size_t)(w * h);
        if (m == INDEXED) { indices = static_cast<uint8_t*>(std::calloc(n, 1)); return indices != nullptr; }
        pixels = static_cast<uint32_t*>(std::calloc(n, 4));
        return pixels != nullptr;
    }

    bool isIndexed() const { return indices != nullptr; }
    size_t pixelCount() const { return (size_t)width * (size_t)height; }
    size_t byteCount() const { return indices ? pixelCount() : pixelCount() * 4; }

    /// The pixels as colors, also for a palette framebuffer (for a window that shows the framebuffer).
    void resolveTo(uint32_t* out) const {
        if (!indices) { std::memcpy(out, pixels, pixelCount() * 4); return; }
        for (size_t i = 0, n = pixelCount(); i < n; i++) out[i] = palette.entries[indices[i]];
    }

    uint32_t getRaw(int x, int y) const {
        if ((unsigned)x >= (unsigned)width || (unsigned)y >= (unsigned)height) return 0;
        size_t i = (size_t)y * width + x;
        return indices ? indices[i] : pixels[i];
    }
    uint8_t getIndex(int x, int y) const {
        if ((unsigned)x >= (unsigned)width || (unsigned)y >= (unsigned)height) return 0;
        size_t i = (size_t)y * width + x;
        return indices ? indices[i] : palette.findNearest(pixels[i]);
    }
    uint32_t getPixel(int x, int y) const {
        if ((unsigned)x >= (unsigned)width || (unsigned)y >= (unsigned)height) return 0;
        size_t i = (size_t)y * width + x;
        return indices ? palette.get(indices[i]) : pixels[i];
    }
};

inline void fbRetain(Framebuffer* fb) { fb->refs++; }
inline void fbRelease(Framebuffer* fb) { if (--fb->refs == 0) delete fb; }

/// A palette framebuffer of the same size that marks the pixels to cut out: index 1 (white) = set, index 0 (black) = not (see Slicer).
inline bool makeMask(const Framebuffer& src, uint8_t threshold, bool darkIsRemoved, uint8_t alphaThreshold, Framebuffer& mask) {
    if (!mask.create(src.width, src.height, INDEXED)) return false;
    mask.palette.set(0, rgb(0, 0, 0));
    mask.palette.set(1, rgb(255, 255, 255));
    mask.transparentIndex = 0;
    auto decide = [&](uint32_t packed) -> uint8_t {
        if ((packed >> 24) < alphaThreshold) return 0;
        double lum = 0.299 * (packed & 0xFF) + 0.587 * ((packed >> 8) & 0xFF) + 0.114 * ((packed >> 16) & 0xFF);
        bool dark = lum < threshold;
        return (darkIsRemoved ? dark : !dark) ? 1 : 0;
    };
    if (src.indices) {
        uint8_t table[256];
        for (int i = 0; i < 256; i++) table[i] = decide(src.palette.get((uint8_t)i));
        for (size_t i = 0, n = src.pixelCount(); i < n; i++) mask.indices[i] = table[src.indices[i]];
    } else
        for (size_t i = 0, n = src.pixelCount(); i < n; i++) mask.indices[i] = decide(src.pixels[i]);
    return true;
}

// ---- the surface: pixels and spans with clipping and alpha blending ------------------------------------------------------------------------------------
/// Mixes `src` (alpha a) over `dst`: dst*(255-a)/255 + src*a/255 per channel; the result is opaque when one of them was.
inline uint32_t mix(uint32_t dst, uint32_t src) {
    uint32_t a = src >> 24;
    if (a == 255 || (dst >> 24) == 0) return src;
    if (a == 0) return dst;
    uint32_t inv = 255 - a;
    uint32_t r = ((src & 0xFF) * a + (dst & 0xFF) * inv + 127) / 255;
    uint32_t g = (((src >> 8) & 0xFF) * a + ((dst >> 8) & 0xFF) * inv + 127) / 255;
    uint32_t b = (((src >> 16) & 0xFF) * a + ((dst >> 16) & 0xFF) * inv + 127) / 255;
    uint32_t outA = a + ((dst >> 24) * inv + 127) / 255;
    return r | (g << 8) | (b << 16) | (std::min(outA, 255u) << 24);
}

/// What the drawing functions write through: the pixels of a framebuffer with clipping at the edge and - when `blend` - alpha blending. A color with alpha: in an RGBA target
/// with blending alpha 255 is a copy, 0 draws nothing, in between it is mixed; without blending it is copied with its alpha. A palette target only copies: with blending a color
/// from alpha 128 on, below that nothing; without blending always.
struct Surface {
    Framebuffer* fb;
    uint32_t* pixels;
    uint8_t* indices;
    int width, height;
    bool blend;
    int clipLeft, clipTop, clipRight, clipBottom;   // drawing is limited to x in [clipLeft, clipRight) and y in [clipTop, clipBottom), inside the target

    Surface(Framebuffer* f, bool b, int cl = 0, int ct = 0, int cr = 0x7FFFFFFF, int cb = 0x7FFFFFFF)
        : fb(f), pixels(f->pixels), indices(f->indices), width(f->width), height(f->height), blend(b),
          clipLeft(std::max(0, cl)), clipTop(std::max(0, ct)), clipRight(std::min(f->width, cr)), clipBottom(std::min(f->height, cb)) {}

    bool isIndexed() const { return indices != nullptr; }

    Pixel resolve(Paint paint) const {
        Pixel p;
        const Palette& palette = fb->palette;
        if (indices) {
            uint8_t index = paint.isIndex() ? (uint8_t)paint.index : palette.findNearest(paint.rgba);
            p.rgba = paint.isIndex() ? palette.get(index) : paint.rgba;
            p.index = index;
            return p;
        }
        if (paint.isIndex()) { p.rgba = palette.get((uint8_t)paint.index); p.index = (uint8_t)paint.index; return p; }
        p.rgba = paint.rgba; p.index = 0;
        return p;
    }

    bool visible(const Pixel& p) const {
        if (!blend) return true;
        uint32_t a = p.rgba >> 24;
        return indices ? a >= 128 : a != 0;
    }
    bool isCopy(const Pixel& p) const { return !blend || indices || (p.rgba >> 24) == 255; }

    void put(int x, int y, const Pixel& p) {
        if (x < clipLeft || x >= clipRight || y < clipTop || y >= clipBottom) return;
        if (!visible(p)) return;
        size_t i = (size_t)y * width + x;
        if (indices) indices[i] = p.index;
        else if (!blend || (p.rgba >> 24) == 255) pixels[i] = p.rgba;
        else pixels[i] = mix(pixels[i], p.rgba);
    }

    /// A horizontal line from x0 to x1 (both included, any order) in row y, clipped.
    void span(int y, int x0, int x1, const Pixel& p) {
        if (y < clipTop || y >= clipBottom) return;
        if (x1 < x0) std::swap(x0, x1);
        x0 = std::max(clipLeft, x0);
        x1 = std::min(clipRight - 1, x1);
        if (x1 < x0 || !visible(p)) return;
        size_t at = (size_t)y * width + x0, n = (size_t)(x1 - x0 + 1);
        if (indices) std::memset(indices + at, p.index, n);
        else if (!blend || (p.rgba >> 24) == 255) for (size_t k = 0; k < n; k++) pixels[at + k] = p.rgba;
        else for (size_t k = 0; k < n; k++) pixels[at + k] = mix(pixels[at + k], p.rgba);
    }

    void rect(int x, int y, int w, int h, const Pixel& p) {
        if (w <= 0 || h <= 0) return;
        int y0 = std::max(clipTop, y), y1 = (int)std::min<int64_t>(clipBottom, (int64_t)y + h);
        int xr = (int)std::min<int64_t>((int64_t)x + w - 1, 0x7FFFFFFF);
        for (int yy = y0; yy < y1; yy++) span(yy, x, xr, p);
    }

    /// The raw value of the pixel: the index in a palette target, else the color; 0 outside.
    uint32_t raw(int x, int y) const {
        if ((unsigned)x >= (unsigned)width || (unsigned)y >= (unsigned)height) return 0;
        size_t i = (size_t)y * width + x;
        return indices ? indices[i] : pixels[i];
    }
};

// ---- shapes: they hand pixels and spans to a sink (a brush fills them, a pen stamps them) ---------------------------------------------------------------------
namespace shapes {

template <class Sink> void line(Sink& sink, int x0, int y0, int x1, int y1) {
    int64_t dxl = std::llabs((int64_t)x1 - x0), dyl = std::llabs((int64_t)y1 - y0);
    if (y0 == y1) { sink.span(y0, x0, x1); return; }
    if (dxl > 0x7FFFFFFF / 2 || dyl > 0x7FFFFFFF / 2) return;   // absurd coordinates: draw nothing instead of overflowing
    int dx = (int)dxl, sx = x0 < x1 ? 1 : -1;
    int dy = -(int)dyl, sy = y0 < y1 ? 1 : -1;
    int err = dx + dy;
    while (true) {
        sink.point(x0, y0);
        if (x0 == x1 && y0 == y1) break;
        int e2 = 2 * err;
        if (e2 >= dy) { err += dy; x0 += sx; }
        if (e2 <= dx) { err += dx; y0 += sy; }
    }
}

template <class Sink> void rect(Sink& sink, int x, int y, int w, int h) {
    if (w <= 0 || h <= 0) return;
    sink.span(y, x, x + w - 1);
    if (h > 1) sink.span(y + h - 1, x, x + w - 1);
    for (int yy = y + 1; yy < y + h - 1; yy++) {
        sink.point(x, yy);
        if (w > 1) sink.point(x + w - 1, yy);
    }
}

constexpr int MAX_RADIUS = 1 << 14;

/// The half widths of the rows of an ellipse: the pixels (dx, dy) with (2dx)^2/a^2 + (2dy)^2/b^2 <= 1, a = 2rx+1, b = 2ry+1.
inline std::vector<int> ellipseSpans(int rx, int ry) {
    int64_t a = 2LL * rx + 1, b = 2LL * ry + 1;
    int64_t a2 = a * a, b2 = b * b;
    std::vector<int> spans((size_t)ry + 1);
    for (int dy = 0; dy <= ry; dy++) {
        int64_t rhs = a2 * (b2 - 4LL * dy * dy);
        int64_t dx = (int64_t)std::sqrt((double)rhs / (4.0 * (double)b2));
        while (dx > 0 && 4 * dx * dx * b2 > rhs) dx--;
        while (4 * (dx + 1) * (dx + 1) * b2 <= rhs) dx++;
        spans[(size_t)dy] = (int)std::min<int64_t>(dx, rx);
    }
    return spans;
}

template <class Sink> void ellipse(Sink& sink, int cx, int cy, int rx, int ry) {
    if (rx < 0 || ry < 0) return;
    rx = std::min(rx, MAX_RADIUS);
    ry = std::min(ry, MAX_RADIUS);
    std::vector<int> spans = ellipseSpans(rx, ry);
    for (int dy = 0; dy <= ry; dy++) {
        int dx = spans[(size_t)dy];
        int neighbor = dy < ry ? spans[(size_t)dy + 1] : -1;
        if (dy == 0) neighbor = ry >= 1 ? std::min(neighbor, spans[1]) : -1;
        int from = std::min(neighbor + 1, dx);
        for (int sign = -1; sign <= 1; sign += 2)
            for (int rowSign = -1; rowSign <= 1; rowSign += 2) {
                if (dy == 0 && rowSign == 1) continue;
                int y = cy + rowSign * dy;
                sink.span(y, cx + sign * from, cx + sign * dx);
            }
    }
}
template <class Sink> void circle(Sink& sink, int cx, int cy, int r) { ellipse(sink, cx, cy, r, r); }

template <class Sink> void fillEllipse(Sink& sink, int cx, int cy, int rx, int ry) {
    if (rx < 0 || ry < 0) return;
    rx = std::min(rx, MAX_RADIUS);
    ry = std::min(ry, MAX_RADIUS);
    std::vector<int> spans = ellipseSpans(rx, ry);
    for (int dy = 0; dy <= ry; dy++) {
        sink.span(cy + dy, cx - spans[(size_t)dy], cx + spans[(size_t)dy]);
        if (dy != 0) sink.span(cy - dy, cx - spans[(size_t)dy], cx + spans[(size_t)dy]);
    }
}
template <class Sink> void fillCircle(Sink& sink, int cx, int cy, int r) { fillEllipse(sink, cx, cy, r, r); }

template <class Sink> void triangle(Sink& sink, int x0, int y0, int x1, int y1, int x2, int y2) {
    line(sink, x0, y0, x1, y1);
    line(sink, x1, y1, x2, y2);
    line(sink, x2, y2, x0, y0);
}

template <class Sink> void polygon(Sink& sink, const std::vector<int>& points, bool closed) {
    int n = (int)(points.size() / 2);
    if (n < 2) return;
    for (int i = 0; i + 1 < n; i++) line(sink, points[2 * i], points[2 * i + 1], points[2 * i + 2], points[2 * i + 3]);
    if (closed && n > 2) line(sink, points[2 * (n - 1)], points[2 * (n - 1) + 1], points[0], points[1]);
}

/// Even-odd fill: the rows between the crossings of the edges (half open, so a vertex counts once), then the outline.
template <class Sink> void fillPolygon(Sink& sink, const std::vector<int>& points, int clipHeight) {
    int n = (int)(points.size() / 2);
    if (n < 3) { polygon(sink, points, false); return; }
    int64_t minY = INT64_MAX, maxY = INT64_MIN;
    for (int i = 0; i < n; i++) { minY = std::min<int64_t>(minY, points[2 * i + 1]); maxY = std::max<int64_t>(maxY, points[2 * i + 1]); }
    int yFrom = (int)std::max<int64_t>(minY, 0), yTo = (int)std::min<int64_t>(maxY, clipHeight - 1);
    std::vector<int> crossings;
    for (int y = yFrom; y <= yTo; y++) {
        crossings.clear();
        for (int i = 0; i < n; i++) {
            int ax = points[2 * i], ay = points[2 * i + 1];
            int bx = points[2 * ((i + 1) % n)], by = points[2 * ((i + 1) % n) + 1];
            if (ay == by) continue;
            if (ay > by) { std::swap(ax, bx); std::swap(ay, by); }
            if (y < ay || y >= by) continue;
            int64_t num = (int64_t)(bx - ax) * (y - ay);
            int64_t den = by - ay;
            int64_t x = ax + (num >= 0 ? (2 * num + den) / (2 * den) : -((2 * -num + den) / (2 * den)));
            crossings.push_back((int)std::max<int64_t>(std::min<int64_t>(x, 0x7FFFFFFF / 2), INT32_MIN / 2));
        }
        std::sort(crossings.begin(), crossings.end());
        for (size_t i = 0; i + 1 < crossings.size(); i += 2) sink.span(y, crossings[i], crossings[i + 1]);
    }
    polygon(sink, points, true);   // the outline belongs to the area (and closes gaps left by the rounding)
}

template <class Sink> void fillTriangle(Sink& sink, int x0, int y0, int x1, int y1, int x2, int y2, int clipHeight) {
    fillPolygon(sink, std::vector<int>{x0, y0, x1, y1, x2, y2}, clipHeight);
}

}  // namespace shapes

// ---- brush: how an area is filled ------------------------------------------------------------------------------------------------------------------------
/// A solid brush. It offers the fill functions (rectangle, circle, ellipse, triangle, polygon, flood fill); the renderer only hands it the surface.
struct Brush {
    Paint color;

    struct FillSink {
        Surface& surface;
        Pixel pixel;
        void point(int x, int y) { surface.put(x, y, pixel); }
        void span(int y, int x0, int x1) { surface.span(y, x0, x1, pixel); }
    };

    void fillSpan(Surface& s, int y, int x0, int x1) const { FillSink k{s, s.resolve(color)}; k.span(y, x0, x1); }
    void fillRect(Surface& s, int x, int y, int w, int h) const {
        if (w <= 0 || h <= 0) return;
        FillSink k{s, s.resolve(color)};
        int y0 = std::max(s.clipTop, y), y1 = (int)std::min<int64_t>(s.clipBottom, (int64_t)y + h);
        int xr = (int)std::min<int64_t>((int64_t)x + w - 1, 0x7FFFFFFF);
        for (int yy = y0; yy < y1; yy++) k.span(yy, x, xr);
    }
    void fillCircle(Surface& s, int cx, int cy, int r) const { FillSink k{s, s.resolve(color)}; shapes::fillCircle(k, cx, cy, r); }
    void fillEllipse(Surface& s, int cx, int cy, int rx, int ry) const { FillSink k{s, s.resolve(color)}; shapes::fillEllipse(k, cx, cy, rx, ry); }
    void fillTriangle(Surface& s, int x0, int y0, int x1, int y1, int x2, int y2) const { FillSink k{s, s.resolve(color)}; shapes::fillTriangle(k, x0, y0, x1, y1, x2, y2, s.height); }
    void fillPolygon(Surface& s, const std::vector<int>& points) const { FillSink k{s, s.resolve(color)}; shapes::fillPolygon(k, points, s.height); }

    /// Fills the 4-connected area around (x, y) that has the pixel value of the start; with a border: everything except border and fill color. The area is determined
    /// first (the pixels stay untouched), then filled - so mixing a half transparent color does not change what belongs to the area.
    void flood(Surface& s, int x, int y, bool hasBorder, Pixel border) const {
        if ((unsigned)x >= (unsigned)s.width || (unsigned)y >= (unsigned)s.height) return;
        bool indexed = s.isIndexed();
        Pixel fillPixel = s.resolve(color);
        uint32_t fill = indexed ? fillPixel.index : fillPixel.rgba;
        uint32_t target = s.raw(x, y);
        uint32_t borderRaw = indexed ? border.index : border.rgba;
        bool copy = s.isCopy(fillPixel);

        if (!hasBorder && target == fill && copy) return;
        if (hasBorder && (target == borderRaw || (target == fill && copy))) return;

        int w = s.width, h = s.height;
        std::vector<uint8_t> seen((size_t)w * h, 0);
        auto fillable = [&](int px, int py) {
            if (seen[(size_t)py * w + px]) return false;
            uint32_t v = s.raw(px, py);
            return hasBorder ? (v != borderRaw && !(copy && v == fill)) : v == target;
        };

        struct Run { int y, x0, x1; };
        std::vector<Run> runs;
        std::vector<std::pair<int, int>> stack;
        stack.push_back({x, y});
        while (!stack.empty()) {
            auto [sx, sy] = stack.back();
            stack.pop_back();
            if (!fillable(sx, sy)) continue;
            int left = sx, right = sx;
            while (left > 0 && fillable(left - 1, sy)) left--;
            while (right < w - 1 && fillable(right + 1, sy)) right++;
            for (int px = left; px <= right; px++) seen[(size_t)sy * w + px] = 1;
            runs.push_back({sy, left, right});
            for (int ny = sy - 1; ny <= sy + 1; ny += 2) {
                if ((unsigned)ny >= (unsigned)h) continue;
                bool inRun = false;
                for (int px = left; px <= right; px++) {
                    bool ok = fillable(px, ny);
                    if (ok && !inRun) stack.push_back({px, ny});
                    inRun = ok;
                }
            }
        }
        FillSink k{s, fillPixel};
        for (const Run& r : runs) k.span(r.y, r.x0, r.x1);
    }
    void floodFill(Surface& s, int x, int y) const { flood(s, x, y, false, Pixel()); }
    void floodFillBorder(Surface& s, int x, int y, Paint border) const { flood(s, x, y, true, s.resolve(border)); }
};

// ---- pen: points, lines, paths ---------------------------------------------------------------------------------------------------------------------------
enum PenShape { PEN_ROUND = 0, PEN_SQUARE = 1 };

/// A pen draws with a tip of `width` pixels. The tip is rendered once (the rows of its stamp) and copied to every pixel of a line - mixed when the color is half transparent
/// (the union of all stamps is mixed once, so overlaps do not mix twice).
struct Pen {
    static constexpr int MAX_WIDTH = 512;
    struct StampRow { int dy, dx0, dx1; };

    Paint color;
    int width = 1;
    int shape = PEN_ROUND;
    std::vector<StampRow> stamp;

    Pen(Paint c, int w, int s) : color(c), width(std::max(1, std::min(w, MAX_WIDTH))), shape(s) { render(); }
    void setWidth(int w) { width = std::max(1, std::min(w, MAX_WIDTH)); render(); }
    void setShape(int s) { shape = s; render(); }

    void render() {
        int w = width, half = (w - 1) / 2;
        stamp.clear();
        for (int j = 0; j < w; j++) {
            int first = -1, last = -1;
            for (int i = 0; i < w; i++) {
                bool inside = shape == PEN_SQUARE
                    || (int64_t)(2 * i - (w - 1)) * (2 * i - (w - 1)) + (int64_t)(2 * j - (w - 1)) * (2 * j - (w - 1)) <= (int64_t)w * w - 1;
                if (!inside) continue;
                if (first < 0) first = i;
                last = i;
            }
            if (first >= 0) stamp.push_back({j - half, first - half, last - half});
        }
    }

    struct StampSink {
        Surface& surface;
        Pixel pixel;
        const std::vector<StampRow>& stamp;
        void point(int x, int y) { for (const StampRow& r : stamp) surface.span(y + r.dy, x + r.dx0, x + r.dx1, pixel); }
        void span(int y, int x0, int x1) {
            if (x1 < x0) std::swap(x0, x1);
            for (const StampRow& r : stamp) surface.span(y + r.dy, x0 + r.dx0, x1 + r.dx1, pixel);
        }
    };

    struct UnionSink {
        Surface& surface;
        Pixel pixel;
        const std::vector<StampRow>& stamp;
        struct Run { int y, x0, x1; };
        std::vector<Run> runs;
        void point(int x, int y) { for (const StampRow& r : stamp) runs.push_back({y + r.dy, x + r.dx0, x + r.dx1}); }
        void span(int y, int x0, int x1) {
            if (x1 < x0) std::swap(x0, x1);
            for (const StampRow& r : stamp) runs.push_back({y + r.dy, x0 + r.dx0, x1 + r.dx1});
        }
        void flush() {
            std::sort(runs.begin(), runs.end(), [](const Run& a, const Run& b) { return a.y != b.y ? a.y < b.y : a.x0 < b.x0; });
            size_t i = 0;
            while (i < runs.size()) {
                int y = runs[i].y, x0 = runs[i].x0, x1 = runs[i].x1;
                i++;
                while (i < runs.size() && runs[i].y == y && runs[i].x0 <= x1 + 1) { x1 = std::max(x1, runs[i].x1); i++; }
                surface.span(y, x0, x1, pixel);
            }
        }
    };

    template <class Emit> void stroke(Surface& s, Emit emit) const {
        Pixel pixel = s.resolve(color);
        if (!s.visible(pixel)) return;
        if (s.isCopy(pixel)) {
            StampSink sink{s, pixel, stamp};
            emit(sink);
        } else {
            UnionSink sink{s, pixel, stamp, {}};
            emit(sink);
            sink.flush();
        }
    }

    void drawPoint(Surface& s, int x, int y) const { stroke(s, [&](auto& k) { k.point(x, y); }); }
    void drawLine(Surface& s, int x0, int y0, int x1, int y1) const { stroke(s, [&](auto& k) { shapes::line(k, x0, y0, x1, y1); }); }
    void drawPath(Surface& s, const std::vector<int>& points, bool closed) const { stroke(s, [&](auto& k) { shapes::polygon(k, points, closed); }); }
    void drawRect(Surface& s, int x, int y, int w, int h) const { stroke(s, [&](auto& k) { shapes::rect(k, x, y, w, h); }); }
    void drawCircle(Surface& s, int cx, int cy, int r) const { stroke(s, [&](auto& k) { shapes::ellipse(k, cx, cy, r, r); }); }
    void drawEllipse(Surface& s, int cx, int cy, int rx, int ry) const { stroke(s, [&](auto& k) { shapes::ellipse(k, cx, cy, rx, ry); }); }
    void drawTriangle(Surface& s, int x0, int y0, int x1, int y1, int x2, int y2) const { stroke(s, [&](auto& k) { shapes::triangle(k, x0, y0, x1, y1, x2, y2); }); }
    void drawPolygon(Surface& s, const std::vector<int>& points, bool closed) const { stroke(s, [&](auto& k) { shapes::polygon(k, points, closed); }); }
};

// ---- blitting ---------------------------------------------------------------------------------------------------------------------------------
enum BlitMode { BLIT_COPY = 0, BLIT_TRANSPARENT = 1, BLIT_BLEND = 2 };

/// Copies a region (scaled with nearest neighbor, flipped for a negative size) between framebuffers of any color mode. Without alpha blending the mode Blend acts like Transparent.
inline void blit(Framebuffer& dst, const Framebuffer& srcIn, int sx, int sy, int sw, int sh, int dx, int dy, int dw, int dh, int mode, int colorKey, bool blend,
                 int clipLeft = 0, int clipTop = 0, int clipRight = 0x7FFFFFFF, int clipBottom = 0x7FFFFFFF) {
    if (!blend && mode == BLIT_BLEND) mode = BLIT_TRANSPARENT;
    if (sw <= 0 || sh <= 0 || dw == 0 || dh == 0) return;
    int64_t absDw = std::llabs((int64_t)dw), absDh = std::llabs((int64_t)dh);
    bool flipX = dw < 0, flipY = dh < 0;

    // the same buffer: read from a copy so that overlapping regions do not overwrite themselves
    Framebuffer snapshot;
    const Framebuffer* srcp = &srcIn;
    if (&dst == &srcIn) {
        snapshot.width = srcIn.width; snapshot.height = srcIn.height; snapshot.mode = srcIn.mode;
        snapshot.transparentIndex = srcIn.transparentIndex;
        size_t n = srcIn.pixelCount();
        if (srcIn.indices) { snapshot.indices = static_cast<uint8_t*>(std::malloc(n)); if (!snapshot.indices) return; std::memcpy(snapshot.indices, srcIn.indices, n); }
        else { snapshot.pixels = static_cast<uint32_t*>(std::malloc(n * 4)); if (!snapshot.pixels) return; std::memcpy(snapshot.pixels, srcIn.pixels, n * 4); }
        snapshot.palette = srcIn.palette;
        srcp = &snapshot;
    }
    const Framebuffer& src = *srcp;

    uint32_t srcTable[256];
    if (src.isIndexed()) std::memcpy(srcTable, src.palette.entries, sizeof srcTable);
    uint8_t map[256];
    bool directIndices = src.isIndexed() && dst.isIndexed() && src.palette.same(dst.palette);
    if (src.isIndexed() && dst.isIndexed() && !directIndices)
        for (int i = 0; i < 256; i++) map[i] = dst.palette.findNearest(srcTable[i]);
    int key = src.isIndexed() ? (colorKey >= 0 ? colorKey : src.transparentIndex) : -1;

    int64_t dxStart = std::max<int64_t>(std::max<int64_t>(0, clipLeft) - (int64_t)dx, 0), dxEnd = std::min<int64_t>(absDw, std::min<int64_t>(dst.width, clipRight) - (int64_t)dx);
    int64_t dyStart = std::max<int64_t>(std::max<int64_t>(0, clipTop) - (int64_t)dy, 0), dyEnd = std::min<int64_t>(absDh, std::min<int64_t>(dst.height, clipBottom) - (int64_t)dy);
    if (dxEnd <= dxStart || dyEnd <= dyStart) return;

    // a small cache for the palette lookup of RGBA sources (many equal colors, and a search costs 256 comparisons)
    struct Cached { uint32_t color; uint8_t index; bool valid; };
    std::vector<Cached> cache(!src.isIndexed() && dst.isIndexed() ? 1024 : 0, Cached{0, 0, false});

    for (int64_t j = dyStart; j < dyEnd; j++) {
        int64_t fy = flipY ? absDh - 1 - j : j;
        int64_t srcY = sy + (2 * fy + 1) * sh / (2 * absDh);
        if (srcY < 0 || srcY >= src.height) continue;
        int dstY = (int)(dy + j);
        for (int64_t i = dxStart; i < dxEnd; i++) {
            int64_t fx = flipX ? absDw - 1 - i : i;
            int64_t srcX = sx + (2 * fx + 1) * sw / (2 * absDw);
            if (srcX < 0 || srcX >= src.width) continue;
            int dstX = (int)(dx + i);
            size_t srcPos = (size_t)(srcY * src.width + srcX);
            size_t dstPos = (size_t)dstY * dst.width + dstX;
            if (src.isIndexed()) {
                uint8_t idx = src.indices[srcPos];
                if (mode != BLIT_COPY && idx == key) continue;
                if (dst.isIndexed()) dst.indices[dstPos] = directIndices ? idx : map[idx];
                else {
                    uint32_t c = srcTable[idx];
                    if (mode == BLIT_BLEND) c = mix(dst.pixels[dstPos], c);
                    dst.pixels[dstPos] = c;
                }
            } else {
                uint32_t c = src.pixels[srcPos];
                uint32_t alpha = c >> 24;
                if (mode != BLIT_COPY && alpha == 0) continue;
                if (dst.isIndexed()) {
                    if (mode == BLIT_BLEND && alpha < 128) continue;
                    Cached& slot = cache[(c * 2654435761u) >> 22];
                    if (!slot.valid || slot.color != c) { slot.color = c; slot.index = dst.palette.findNearest(c); slot.valid = true; }
                    dst.indices[dstPos] = slot.index;
                } else dst.pixels[dstPos] = mode == BLIT_BLEND ? mix(dst.pixels[dstPos], c) : c;
            }
        }
    }
}

// ---- the renderer: terminal text and the drawing functions of a framebuffer ----------------------------------------------------------------------------------
struct Renderer {
    Framebuffer* target = nullptr;
    bool smallFont = false;      // the built-in font is 8x14 (the default) or 8x8
    bool alphaBlending = true;
    int cellWidth = 8, cellHeight = 14, columns = 0, rows = 0, cursorRow = 0, cursorColumn = 0;
    Paint foreground = Paint::fromRgba(rgb(255, 255, 255));
    Paint background = Paint::fromRgba(rgb(0, 0, 0));
    bool hasBackground = true;
    int clipLeft = 0, clipTop = 0, clipRight = 0x7FFFFFFF, clipBottom = 0x7FFFFFFF;

    explicit Renderer(Framebuffer* fb, bool small = false) : target(fb), smallFont(small), cellHeight(small ? 8 : 14) { fbRetain(fb); updateGrid(); }
    Renderer(const Renderer&) = delete;
    Renderer& operator=(const Renderer&) = delete;
    ~Renderer() { fbRelease(target); }

    Surface surface() const { return Surface(target, alphaBlending, clipLeft, clipTop, clipRight, clipBottom); }
    void setClip(int x, int y, int w, int h) {
        clipLeft = x; clipTop = y;
        clipRight = (int)std::min<int64_t>((int64_t)x + std::max(0, w), 0x7FFFFFFF);
        clipBottom = (int)std::min<int64_t>((int64_t)y + std::max(0, h), 0x7FFFFFFF);
    }
    void resetClip() { clipLeft = 0; clipTop = 0; clipRight = 0x7FFFFFFF; clipBottom = 0x7FFFFFFF; }
    void updateGrid() { columns = target->width / cellWidth; rows = target->height / cellHeight; }
    const uint8_t* glyph(char16_t c) const { uint32_t i = c < 256 ? c : '?'; return smallFont ? kGlyphs8x8[i] : kGlyphs8x14[i]; }

    void locate(int row, int column) {
        cursorRow = std::max(0, std::min(row, std::max(0, rows - 1)));
        cursorColumn = std::max(0, std::min(column, std::max(0, columns - 1)));
    }

    // Clear and scrolling set pixels as they are (no blending)
    Pixel clearPixel() const { return surface().resolve(hasBackground ? background : Paint::fromRgba(rgb(0, 0, 0))); }
    void clearAll(const Pixel& p) {
        size_t n = target->pixelCount();
        if (target->indices) std::memset(target->indices, p.index, n);
        else for (size_t k = 0; k < n; k++) target->pixels[k] = p.rgba;
    }
    void clear() { clearAll(clearPixel()); cursorRow = 0; cursorColumn = 0; }
    void clearTo(Paint paint) { clearAll(surface().resolve(paint)); }
    void setColor(Paint fg, Paint bg) { foreground = fg; background = bg; hasBackground = true; }

    void scrollUp(int pixelRows, const Pixel& fill) {
        int w = target->width, h = target->height;
        if (pixelRows <= 0) return;
        if (pixelRows >= h) { clearAll(fill); return; }
        size_t keep = (size_t)(h - pixelRows) * w, moved = (size_t)pixelRows * w;
        if (target->indices) { std::memmove(target->indices, target->indices + moved, keep); std::memset(target->indices + keep, fill.index, moved); return; }
        std::memmove(target->pixels, target->pixels + moved, keep * 4);
        for (size_t k = 0; k < moved; k++) target->pixels[keep + k] = fill.rgba;
    }

    /// One character with its upper left corner at (x, y): a background that is not visible is none, a foreground that is not visible draws no glyph pixels.
    void drawGlyph(Surface& s, int x, int y, char16_t c, const Pixel& fg, bool hasBg, const Pixel& bg) {
        if (hasBg && !s.visible(bg)) hasBg = false;
        bool fgVisible = s.visible(fg);
        if (!fgVisible && !hasBg) return;
        if (hasBg) s.rect(x, y, cellWidth, cellHeight, bg);
        if (!fgVisible) return;
        const uint8_t* rowsBits = glyph(c);
        for (int gy = 0; gy < cellHeight; gy++) {
            unsigned bits = rowsBits[gy];
            if (bits == 0) continue;
            for (int gx = 0; gx < cellWidth; gx++)
                if ((bits >> (7 - gx)) & 1u) s.put(x + gx, y + gy, fg);
        }
    }

    void newLine() {
        cursorColumn = 0;
        cursorRow++;
        if (cursorRow >= rows) { scrollUp(cellHeight, clearPixel()); cursorRow = rows - 1; }
    }
    void advance() { cursorColumn++; if (cursorColumn >= columns) newLine(); }

    void print(const char16_t* text, uint32_t n) {
        Surface s = surface();
        Pixel fg = s.resolve(foreground);
        Pixel bg = hasBackground ? s.resolve(background) : Pixel();
        for (uint32_t i = 0; i < n; i++) {
            char16_t c = text[i];
            if (c == '\n') { newLine(); continue; }
            if (c == '\r') continue;
            drawGlyph(s, cursorColumn * cellWidth, cursorRow * cellHeight, c, fg, hasBackground, bg);
            advance();
        }
    }

    /// Text at pixel coordinates: the glyph pixels in the color of `fg`, with `bg` (null = none) the whole cell below.
    void drawText(int x, int y, const char16_t* text, uint32_t n, const Brush& fg, const Brush* bg) {
        Surface s = surface();
        Pixel f = s.resolve(fg.color);
        Pixel b = bg ? s.resolve(bg->color) : Pixel();
        for (uint32_t i = 0; i < n; i++) { drawGlyph(s, x, y, text[i], f, bg != nullptr, b); x += cellWidth; }
    }
};

}  // namespace gfx
}  // namespace fire
