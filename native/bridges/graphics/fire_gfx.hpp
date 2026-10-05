// fire native graphics: framebuffers (RGBA or palette), the palette, drawing (lines, rectangles, circles, ellipses, triangles, polygons, flood fill, text), blitting.
// A port of src/fire.Terminal (Framebuffer, Palette, Paint, Shapes, Blitter, TerminalCanvas) - the same pixels come out. No operating system, no exceptions: it runs on
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

struct Brush { uint32_t rgba = 0; uint8_t index = 0; };

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

    Brush resolveBrush(Paint paint) const {
        Brush b;
        if (mode == INDEXED) {
            uint8_t index = paint.isIndex() ? (uint8_t)paint.index : palette.findNearest(paint.rgba);
            b.rgba = palette.get(index); b.index = index;
            return b;
        }
        if (paint.isIndex()) { b.rgba = palette.get((uint8_t)paint.index); b.index = (uint8_t)paint.index; return b; }
        b.rgba = paint.rgba; b.index = 0;
        return b;
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

    void plot(int x, int y, const Brush& brush) {
        if ((unsigned)x >= (unsigned)width || (unsigned)y >= (unsigned)height) return;
        size_t i = (size_t)y * width + x;
        if (indices) indices[i] = brush.index; else pixels[i] = brush.rgba;
    }

    void hline(int x0, int x1, int y, const Brush& brush) {
        if ((unsigned)y >= (unsigned)height) return;
        if (x1 < x0) std::swap(x0, x1);
        x0 = std::max(0, x0);
        x1 = std::min(width - 1, x1);
        if (x1 < x0) return;
        size_t at = (size_t)y * width + x0, n = (size_t)(x1 - x0 + 1);
        if (indices) std::memset(indices + at, brush.index, n);
        else for (size_t k = 0; k < n; k++) pixels[at + k] = brush.rgba;
    }

    void fillRect(int x, int y, int w, int h, const Brush& brush) {
        int x0 = std::max(0, x), y0 = std::max(0, y);
        int64_t x1 = std::min<int64_t>(width, (int64_t)x + w), y1 = std::min<int64_t>(height, (int64_t)y + h);
        if (x1 <= x0) return;
        for (int yy = y0; yy < y1; yy++) hline(x0, (int)x1 - 1, yy, brush);
    }

    void clear(const Brush& brush) {
        size_t n = pixelCount();
        if (indices) std::memset(indices, brush.index, n);
        else for (size_t k = 0; k < n; k++) pixels[k] = brush.rgba;
    }

    void scrollUp(int rows, const Brush& fill) {
        if (rows <= 0) return;
        if (rows >= height) { clear(fill); return; }
        size_t keep = (size_t)(height - rows) * width, moved = (size_t)rows * width;
        if (indices) { std::memmove(indices, indices + moved, keep); std::memset(indices + keep, fill.index, moved); return; }
        std::memmove(pixels, pixels + moved, keep * 4);
        for (size_t k = 0; k < moved; k++) pixels[keep + k] = fill.rgba;
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

// ---- shapes ---------------------------------------------------------------------------------------------------------------------------------
namespace shapes {

inline void line(Framebuffer& fb, int x0, int y0, int x1, int y1, const Brush& brush) {
    int64_t dxl = std::llabs((int64_t)x1 - x0), dyl = std::llabs((int64_t)y1 - y0);
    if (y0 == y1) { fb.hline(x0, x1, y0, brush); return; }
    if (dxl > 0x7FFFFFFF / 2 || dyl > 0x7FFFFFFF / 2) return;   // absurd coordinates: draw nothing instead of overflowing
    int dx = (int)dxl, sx = x0 < x1 ? 1 : -1;
    int dy = -(int)dyl, sy = y0 < y1 ? 1 : -1;
    int err = dx + dy;
    while (true) {
        fb.plot(x0, y0, brush);
        if (x0 == x1 && y0 == y1) break;
        int e2 = 2 * err;
        if (e2 >= dy) { err += dy; x0 += sx; }
        if (e2 <= dx) { err += dx; y0 += sy; }
    }
}

inline void rect(Framebuffer& fb, int x, int y, int w, int h, const Brush& brush) {
    if (w <= 0 || h <= 0) return;
    fb.hline(x, x + w - 1, y, brush);
    if (h > 1) fb.hline(x, x + w - 1, y + h - 1, brush);
    for (int yy = y + 1; yy < y + h - 1; yy++) {
        fb.plot(x, yy, brush);
        if (w > 1) fb.plot(x + w - 1, yy, brush);
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

inline void ellipse(Framebuffer& fb, int cx, int cy, int rx, int ry, const Brush& brush) {
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
                fb.hline(cx + sign * from, cx + sign * dx, y, brush);
            }
    }
}
inline void circle(Framebuffer& fb, int cx, int cy, int r, const Brush& brush) { ellipse(fb, cx, cy, r, r, brush); }

inline void fillEllipse(Framebuffer& fb, int cx, int cy, int rx, int ry, const Brush& brush) {
    if (rx < 0 || ry < 0) return;
    rx = std::min(rx, MAX_RADIUS);
    ry = std::min(ry, MAX_RADIUS);
    std::vector<int> spans = ellipseSpans(rx, ry);
    for (int dy = 0; dy <= ry; dy++) {
        fb.hline(cx - spans[(size_t)dy], cx + spans[(size_t)dy], cy + dy, brush);
        if (dy != 0) fb.hline(cx - spans[(size_t)dy], cx + spans[(size_t)dy], cy - dy, brush);
    }
}
inline void fillCircle(Framebuffer& fb, int cx, int cy, int r, const Brush& brush) { fillEllipse(fb, cx, cy, r, r, brush); }

inline void triangle(Framebuffer& fb, int x0, int y0, int x1, int y1, int x2, int y2, const Brush& brush) {
    line(fb, x0, y0, x1, y1, brush);
    line(fb, x1, y1, x2, y2, brush);
    line(fb, x2, y2, x0, y0, brush);
}

inline void polygon(Framebuffer& fb, const std::vector<int>& points, const Brush& brush, bool closed) {
    int n = (int)(points.size() / 2);
    if (n < 2) return;
    for (int i = 0; i + 1 < n; i++) line(fb, points[2 * i], points[2 * i + 1], points[2 * i + 2], points[2 * i + 3], brush);
    if (closed && n > 2) line(fb, points[2 * (n - 1)], points[2 * (n - 1) + 1], points[0], points[1], brush);
}

/// Even-odd fill: the rows between the crossings of the edges (half open, so a vertex counts once), then the outline.
inline void fillPolygon(Framebuffer& fb, const std::vector<int>& points, const Brush& brush) {
    int n = (int)(points.size() / 2);
    if (n < 3) { polygon(fb, points, brush, false); return; }
    int64_t minY = INT64_MAX, maxY = INT64_MIN;
    for (int i = 0; i < n; i++) { minY = std::min<int64_t>(minY, points[2 * i + 1]); maxY = std::max<int64_t>(maxY, points[2 * i + 1]); }
    int yFrom = (int)std::max<int64_t>(minY, 0), yTo = (int)std::min<int64_t>(maxY, fb.height - 1);
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
        for (size_t i = 0; i + 1 < crossings.size(); i += 2) fb.hline(crossings[i], crossings[i + 1], y, brush);
    }
    polygon(fb, points, brush, true);   // the outline belongs to the area (and closes gaps left by the rounding)
}

inline void fillTriangle(Framebuffer& fb, int x0, int y0, int x1, int y1, int x2, int y2, const Brush& brush) {
    fillPolygon(fb, std::vector<int>{x0, y0, x1, y1, x2, y2}, brush);
}

/// Scanline flood fill with its own stack. Without a border: what has the color of the start pixel; with one: everything except border and fill color.
inline void flood(Framebuffer& fb, int x, int y, const Brush& brush, bool hasBorder, uint32_t border) {
    if ((unsigned)x >= (unsigned)fb.width || (unsigned)y >= (unsigned)fb.height) return;
    uint32_t fill = fb.isIndexed() ? brush.index : brush.rgba;
    uint32_t target = fb.getRaw(x, y);
    auto fillable = [&](int px, int py) {
        uint32_t v = fb.getRaw(px, py);
        return hasBorder ? (v != border && v != fill) : v == target;
    };
    if (!hasBorder && target == fill) return;
    if (hasBorder && (target == border || target == fill)) return;
    std::vector<std::pair<int, int>> stack;
    stack.push_back({x, y});
    while (!stack.empty()) {
        auto [sx, sy] = stack.back();
        stack.pop_back();
        if (!fillable(sx, sy)) continue;
        int left = sx, right = sx;
        while (left > 0 && fillable(left - 1, sy)) left--;
        while (right < fb.width - 1 && fillable(right + 1, sy)) right++;
        fb.hline(left, right, sy, brush);
        for (int ny = sy - 1; ny <= sy + 1; ny += 2) {
            if ((unsigned)ny >= (unsigned)fb.height) continue;
            bool inRun = false;
            for (int px = left; px <= right; px++) {
                bool ok = fillable(px, ny);
                if (ok && !inRun) stack.push_back({px, ny});
                inRun = ok;
            }
        }
    }
}
inline void floodFill(Framebuffer& fb, int x, int y, const Brush& brush) { flood(fb, x, y, brush, false, 0); }
inline void floodFillBorder(Framebuffer& fb, int x, int y, const Brush& brush, const Brush& border) {
    flood(fb, x, y, brush, true, fb.isIndexed() ? border.index : border.rgba);
}

}  // namespace shapes

// ---- blitting ---------------------------------------------------------------------------------------------------------------------------------
enum BlitMode { BLIT_COPY = 0, BLIT_TRANSPARENT = 1, BLIT_BLEND = 2 };

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

/// Copies a region (scaled with nearest neighbor, flipped for a negative size) between framebuffers of any color mode.
inline void blit(Framebuffer& dst, const Framebuffer& srcIn, int sx, int sy, int sw, int sh, int dx, int dy, int dw, int dh, int mode, int colorKey) {
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

    int64_t dxStart = std::max<int64_t>(0, -(int64_t)dx), dxEnd = std::min<int64_t>(absDw, dst.width - (int64_t)dx);
    int64_t dyStart = std::max<int64_t>(0, -(int64_t)dy), dyEnd = std::min<int64_t>(absDh, dst.height - (int64_t)dy);
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

// ---- the console: text and the drawing functions of a framebuffer ----------------------------------------------------------------------------------
struct Canvas {
    Framebuffer* target = nullptr;
    bool smallFont = false;      // the built-in font is 8x14 (the default) or 8x8
    int cellWidth = 8, cellHeight = 14, columns = 0, rows = 0, cursorRow = 0, cursorColumn = 0;
    Paint foreground = Paint::fromRgba(rgb(255, 255, 255));
    Paint background = Paint::fromRgba(rgb(0, 0, 0));
    bool hasBackground = true;

    explicit Canvas(Framebuffer* fb, bool small = false) : target(fb), smallFont(small), cellHeight(small ? 8 : 14) { fbRetain(fb); updateGrid(); }
    Canvas(const Canvas&) = delete;
    Canvas& operator=(const Canvas&) = delete;
    ~Canvas() { fbRelease(target); }

    void updateGrid() { columns = target->width / cellWidth; rows = target->height / cellHeight; }
    const uint8_t* glyph(char16_t c) const { uint32_t i = c < 256 ? c : '?'; return smallFont ? kGlyphs8x8[i] : kGlyphs8x14[i]; }

    void locate(int row, int column) {
        cursorRow = std::max(0, std::min(row, std::max(0, rows - 1)));
        cursorColumn = std::max(0, std::min(column, std::max(0, columns - 1)));
    }
    Brush clearBrush() const { return target->resolveBrush(hasBackground ? background : Paint::fromRgba(rgb(0, 0, 0))); }
    void clear() { target->clear(clearBrush()); cursorRow = 0; cursorColumn = 0; }
    void setColor(Paint fg, Paint bg) { foreground = fg; background = bg; hasBackground = true; }

    void drawGlyph(int x, int y, char16_t c, const Brush& fg, bool hasBg, const Brush& bg) {
        if (hasBg) target->fillRect(x, y, cellWidth, cellHeight, bg);
        const uint8_t* rowsBits = glyph(c);
        for (int gy = 0; gy < cellHeight; gy++) {
            unsigned bits = rowsBits[gy];
            if (bits == 0) continue;
            for (int gx = 0; gx < cellWidth; gx++)
                if ((bits >> (7 - gx)) & 1u) target->plot(x + gx, y + gy, fg);
        }
    }

    void newLine() {
        cursorColumn = 0;
        cursorRow++;
        if (cursorRow >= rows) { target->scrollUp(cellHeight, clearBrush()); cursorRow = rows - 1; }
    }
    void advance() { cursorColumn++; if (cursorColumn >= columns) newLine(); }

    void print(const char16_t* text, uint32_t n) {
        Brush fg = target->resolveBrush(foreground);
        Brush bg = hasBackground ? target->resolveBrush(background) : Brush();
        for (uint32_t i = 0; i < n; i++) {
            char16_t c = text[i];
            if (c == '\n') { newLine(); continue; }
            if (c == '\r') continue;
            drawGlyph(cursorColumn * cellWidth, cursorRow * cellHeight, c, fg, hasBackground, bg);
            advance();
        }
    }

    void drawText(int x, int y, const char16_t* text, uint32_t n, Paint fg, bool hasBg, Paint bg) {
        Brush f = target->resolveBrush(fg);
        Brush b = hasBg ? target->resolveBrush(bg) : Brush();
        for (uint32_t i = 0; i < n; i++) { drawGlyph(x, y, text[i], f, hasBg, b); x += cellWidth; }
    }
};

}  // namespace gfx
}  // namespace fire
