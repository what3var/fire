// TrueType outlines for the renderer: reads a .ttf (glyf outlines: simple and compound glyphs, cmap formats 4, 6 and 12, horizontal metrics, the 'kern' table), measures text and rasterises
// glyphs into 8 bit coverage images with exact area coverage (anti-aliased, no hinting).
//
// This is a port to C++ of libschrift 0.10.2 (https://github.com/tomolt/libschrift, (c) 2019-2022 Thomas Oltmann and contributors, ISC license, see LICENSE-libschrift.md), written
// to be read like the original and to give the same pixels. Differences: it works on a block of memory only (no file mapping), every read of the font is bounds checked (a damaged font
// gives a "no glyph" result and no stray memory access), the buffers are std::vector and the code points are UTF-16 aware at the call sites. The same algorithm exists in C#
// (fire.Terminal/TrueType/TrueTypeFont.cs) for the virtual machine, and the tests compare both with the original: glyph images must be equal.
//
//
// The license of libschrift (the original), which applies to this port:
//
//   ISC License
//
//   (c) 2019-2022 Thomas Oltmann and contributors
//
//   Permission to use, copy, modify, and/or distribute this software for any
//   purpose with or without fee is hereby granted, provided that the above
//   copyright notice and this permission notice appear in all copies.
//
//   THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES
//   WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF
//   MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR
//   ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
//   WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN
//   ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF
//   OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.

// Floating point: everything is `double` as in the original; a compiler that fuses multiply and add would change the last bit of some coverage values, so that is switched off here.
#pragma once

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <string>
#include <vector>

#if defined(__clang__)
#pragma clang fp contract(off)
#elif defined(__GNUC__)
#pragma GCC optimize("fp-contract=off")
#endif

namespace fire {
namespace gfx {
namespace ttf {

struct GMetrics { double advanceWidth = 0, leftSideBearing = 0; int yOffset = 0, minWidth = 0, minHeight = 0, xOffset = 0; };   // xOffset: the left edge of the glyph image from the pen position
struct LMetrics { double ascender = 0, descender = 0, lineGap = 0; };

class Font {
public:
    /// Takes a copy of the font file. false: it is not a TrueType font (or too damaged to read the tables we need).
    bool load(const uint8_t* data, size_t size) {
        if (size > 0xFFFFFFFFu) return false;
        mem_.assign(data, data + size);
        return init();
    }

    uint32_t unitsPerEm() const { return unitsPerEm_; }

    /// The ascent, descent (negative) and line gap for a size in pixels (the size is the em: units per em map to `size` pixels).
    bool lmetrics(double size, LMetrics& m) const {
        m = LMetrics();
        uint32_t hhea;
        if (!gettable("hhea", hhea)) return false;
        if (!safe(hhea, 36)) return false;
        double factor = size / unitsPerEm_;
        m.ascender = geti16(hhea + 4) * factor;
        m.descender = geti16(hhea + 6) * factor;
        m.lineGap = geti16(hhea + 8) * factor;
        return true;
    }

    /// The glyph for a code point (0 if the font has none; false if the cmap could not be read).
    bool lookup(uint32_t codepoint, uint32_t& glyph) const { return glyphId(codepoint, glyph); }

    bool gmetrics(double size, uint32_t glyph, GMetrics& m) const {
        m = GMetrics();
        double xScale = size / unitsPerEm_;
        int adv, lsb;
        if (!horMetrics(glyph, adv, lsb)) return false;
        m.advanceWidth = adv * xScale;
        m.leftSideBearing = lsb * xScale + 0.0;
        uint32_t outline;
        if (!outlineOffset(glyph, outline)) return false;
        if (!outline) return true;
        int bbox[4];
        if (!glyphBbox(size, outline, bbox)) return false;
        m.minWidth = bbox[2] - bbox[0] + 1;
        m.minHeight = bbox[3] - bbox[1] + 1;
        m.yOffset = -bbox[3];   // (the y axis points down)
        m.xOffset = bbox[0];
        return true;
    }

    /// The horizontal kerning between two glyphs in pixels (the 'kern' table; 0 if there is none).
    bool kerning(double size, uint32_t left, uint32_t right, double& xShift) const {
        xShift = 0;
        uint32_t offset;
        if (!gettable("kern", offset)) return true;
        if (!safe(offset, 4)) return false;
        if (getu16(offset) != 0) return true;
        unsigned numTables = getu16(offset + 2);
        offset += 4;
        double xs = 0;
        while (numTables > 0) {
            if (!safe(offset, 6)) return false;
            unsigned length = getu16(offset + 2), format = getu8(offset + 4), flags = getu8(offset + 5);
            offset += 6;
            if (format == 0 && (flags & 1) && !(flags & 2)) {
                if (!safe(offset, 8)) return false;
                unsigned numPairs = getu16(offset);
                offset += 8;
                if (!safe(offset, (uint32_t)numPairs * 6)) return false;
                // binary search for the pair (left, right) as a 32 bit number
                uint32_t key = (left << 16) | (right & 0xFFFF);
                size_t lo = 0, hi = numPairs;
                while (lo < hi) {
                    size_t mid = lo + (hi - lo) / 2;
                    uint32_t k = getu32(offset + (uint32_t)mid * 6);
                    if (k < key) lo = mid + 1;
                    else if (k > key) hi = mid;
                    else {
                        int value = geti16(offset + (uint32_t)mid * 6 + 4);
                        if (!(flags & 4)) xs += value;   // (cross stream kerning moves the glyph up or down: not used)
                        break;
                    }
                }
            }
            offset += length;
            --numTables;
        }
        xShift = xs / unitsPerEm_ * size;
        return true;
    }

    /// Renders the glyph into `pixels` (width * height bytes, coverage 0..255): width and height are those of gmetrics (the width rounded up to a multiple of 4 is fine). true: drawn
    /// (or the glyph has no outline).
    bool render(double size, uint32_t glyph, uint8_t* pixels, int width, int height) const {
        uint32_t outline;
        if (!outlineOffset(glyph, outline)) return false;
        if (!outline) return true;
        int bbox[4];
        if (!glyphBbox(size, outline, bbox)) return false;
        double transform[6];
        transform[0] = size / unitsPerEm_;
        transform[1] = 0.0;
        transform[2] = 0.0;
        transform[4] = 0.0 - bbox[0];
        transform[3] = -size / unitsPerEm_;
        transform[5] = bbox[3] - 0.0;
        Outline outl;
        if (!decodeOutline(outline, 0, outl)) return false;
        return renderOutline(outl, transform, pixels, width, height);
    }

    /// A name from the 'name' table in UTF-8 (nameId 1 family, 2 style, 4 full name, 6 PostScript name); "" if there is none.
    std::string name(int nameId) const {
        uint32_t table;
        if (!gettable("name", table) || !safe(table, 6)) return "";
        unsigned count = getu16(table + 2);
        uint32_t strings = table + getu16(table + 4);
        std::string best;
        int bestScore = -1;
        for (unsigned i = 0; i < count; i++) {
            uint32_t rec = table + 6 + i * 12;
            if (!safe(rec, 12)) break;
            unsigned platform = getu16(rec), encoding = getu16(rec + 2), language = getu16(rec + 4), id = getu16(rec + 6), length = getu16(rec + 8), offset = getu16(rec + 10);
            if ((int)id != nameId || !safe(strings + offset, length)) continue;
            int score = -1;
            std::string text;
            if (platform == 3 && (encoding == 1 || encoding == 10)) { score = language == 0x409 ? 3 : 2; text = utf16be(strings + offset, length); }
            else if (platform == 0) { score = 1; text = utf16be(strings + offset, length); }
            else if (platform == 1 && encoding == 0) { score = 0; for (unsigned k = 0; k < length; k++) { uint8_t c = getu8(strings + offset + k); if (c < 0x80) text.push_back((char)c); else { text.push_back((char)(0xC0 | (c >> 6))); text.push_back((char)(0x80 | (c & 0x3F))); } } }
            if (score > bestScore && !text.empty()) { bestScore = score; best = text; }
        }
        return best;
    }

private:
    struct Point { double x, y; };
    struct Line { uint16_t beg, end; };
    struct Curve { uint16_t beg, end, ctrl; };
    struct Outline { std::vector<Point> points; std::vector<Curve> curves; std::vector<Line> lines; };
    struct Cell { double area, cover; };

    std::vector<uint8_t> mem_;
    uint32_t unitsPerEm_ = 0, numLongHmtx_ = 0;
    int locaFormat_ = 0;

    bool safe(uint32_t offset, uint32_t margin) const {
        uint32_t size = (uint32_t)mem_.size();
        if (offset > size) return false;
        return size - offset >= margin;
    }
    uint8_t getu8(uint32_t o) const { return o < mem_.size() ? mem_[o] : 0; }
    int getu8s(uint32_t o) const { return (int8_t)getu8(o); }
    uint16_t getu16(uint32_t o) const { return (uint16_t)((getu8(o) << 8) | getu8(o + 1)); }
    int geti16(uint32_t o) const { return (int16_t)getu16(o); }
    uint32_t getu32(uint32_t o) const { return ((uint32_t)getu8(o) << 24) | ((uint32_t)getu8(o + 1) << 16) | ((uint32_t)getu8(o + 2) << 8) | getu8(o + 3); }

    std::string utf16be(uint32_t o, unsigned length) const {
        std::string s;
        for (unsigned i = 0; i + 1 < length; i += 2) {
            uint32_t c = getu16(o + i);
            if (c >= 0xD800 && c <= 0xDBFF && i + 3 < length) { uint32_t d = getu16(o + i + 2); if (d >= 0xDC00 && d <= 0xDFFF) { c = 0x10000 + ((c - 0xD800) << 10) + (d - 0xDC00); i += 2; } }
            if (c < 0x80) s.push_back((char)c);
            else if (c < 0x800) { s.push_back((char)(0xC0 | (c >> 6))); s.push_back((char)(0x80 | (c & 0x3F))); }
            else if (c < 0x10000) { s.push_back((char)(0xE0 | (c >> 12))); s.push_back((char)(0x80 | ((c >> 6) & 0x3F))); s.push_back((char)(0x80 | (c & 0x3F))); }
            else { s.push_back((char)(0xF0 | (c >> 18))); s.push_back((char)(0x80 | ((c >> 12) & 0x3F))); s.push_back((char)(0x80 | ((c >> 6) & 0x3F))); s.push_back((char)(0x80 | (c & 0x3F))); }
        }
        return s;
    }

    bool gettable(const char* tag, uint32_t& offset) const {
        unsigned numTables = getu16(4);
        if (!safe(12, (uint32_t)numTables * 16)) return false;
        // the table directory is sorted by tag: binary search
        uint32_t key = ((uint32_t)(uint8_t)tag[0] << 24) | ((uint32_t)(uint8_t)tag[1] << 16) | ((uint32_t)(uint8_t)tag[2] << 8) | (uint8_t)tag[3];
        size_t lo = 0, hi = numTables;
        while (lo < hi) {
            size_t mid = lo + (hi - lo) / 2;
            uint32_t k = getu32(12 + (uint32_t)mid * 16);
            if (k < key) lo = mid + 1;
            else if (k > key) hi = mid;
            else { offset = getu32(12 + (uint32_t)mid * 16 + 8); return true; }
        }
        return false;
    }

    bool init() {
        if (!safe(0, 12)) return false;
        uint32_t scalerType = getu32(0);
        if (scalerType != 0x00010000 && scalerType != 0x74727565) return false;
        uint32_t head, hhea;
        if (!gettable("head", head) || !safe(head, 54)) return false;
        unitsPerEm_ = getu16(head + 18);
        locaFormat_ = geti16(head + 50);
        if (unitsPerEm_ == 0) return false;
        if (!gettable("hhea", hhea) || !safe(hhea, 36)) return false;
        numLongHmtx_ = getu16(hhea + 34);
        return true;
    }

    // ---- cmap ---------------------------------------------------------------------------------------------------------------------------------
    bool cmapFmt4(uint32_t table, uint32_t charCode, uint32_t& glyph) const {
        if (charCode > 0xFFFF) { glyph = 0; return true; }
        uint32_t shortCode = charCode;
        if (!safe(table, 8)) return false;
        uint32_t segCountX2 = getu16(table);
        if ((segCountX2 & 1) || !segCountX2) return false;
        uint32_t endCodes = table + 8, startCodes = endCodes + segCountX2 + 2, idDeltas = startCodes + segCountX2, idRangeOffsets = idDeltas + segCountX2;
        if (!safe(idRangeOffsets, segCountX2)) return false;
        // the segment that contains the code: the first whose end code is not lower (binary search over the end codes)
        size_t low = 0, high = segCountX2 / 2 - 1;
        while (low != high) {
            size_t mid = low + (high - low) / 2;
            if (getu16(endCodes + (uint32_t)mid * 2) < shortCode) low = mid + 1; else high = mid;
        }
        uint32_t segIdxX2 = (uint32_t)low * 2;
        uint32_t startCode = getu16(startCodes + segIdxX2);
        if (startCode > shortCode) return true;   // (glyph stays 0)
        uint32_t idDelta = getu16(idDeltas + segIdxX2);
        uint32_t idRangeOffset = getu16(idRangeOffsets + segIdxX2);
        if (!idRangeOffset) { glyph = (shortCode + idDelta) & 0xFFFF; return true; }
        uint32_t idOffset = idRangeOffsets + segIdxX2 + idRangeOffset + 2U * (shortCode - startCode);
        if (!safe(idOffset, 2)) return false;
        uint32_t id = getu16(idOffset);
        glyph = id ? (id + idDelta) & 0xFFFF : 0;
        return true;
    }
    bool cmapFmt6(uint32_t table, uint32_t charCode, uint32_t& glyph) const {
        if (charCode > 0xFFFF) { glyph = 0; return true; }
        if (!safe(table, 4)) return false;
        unsigned firstCode = getu16(table), entryCount = getu16(table + 2);
        if (!safe(table, 4 + 2 * entryCount)) return false;
        if (charCode < firstCode) return false;
        charCode -= firstCode;
        if (!(charCode < entryCount)) return false;
        glyph = getu16(table + 4 + 2 * charCode);
        return true;
    }
    bool cmapFmt12(uint32_t table, uint32_t charCode, uint32_t& glyph) const {
        glyph = 0;
        if (!safe(table, 16)) return false;
        uint32_t len = getu32(table + 4);
        if (len < 16) return false;
        if (!safe(table, len)) return false;
        uint32_t numEntries = getu32(table + 12);
        if (numEntries > (len - 16) / 12) return false;
        for (uint32_t i = 0; i < numEntries; ++i) {
            uint32_t firstCode = getu32(table + i * 12 + 16), lastCode = getu32(table + i * 12 + 16 + 4);
            if (charCode < firstCode || charCode > lastCode) continue;
            glyph = (charCode - firstCode) + getu32(table + i * 12 + 16 + 8);
            return true;
        }
        return true;
    }
    bool glyphId(uint32_t charCode, uint32_t& glyph) const {
        glyph = 0;
        uint32_t cmap;
        if (!gettable("cmap", cmap)) return false;
        if (!safe(cmap, 4)) return false;
        unsigned numEntries = getu16(cmap + 2);
        if (!safe(cmap, 4 + numEntries * 8)) return false;
        for (unsigned idx = 0; idx < numEntries; ++idx) {   // a map of the whole repertoire first
            uint32_t entry = cmap + 4 + idx * 8;
            int type = getu16(entry) * 0100 + getu16(entry + 2);
            if (type == 0004 || type == 0312) {
                uint32_t table = cmap + getu32(entry + 4);
                if (!safe(table, 8)) return false;
                if (getu16(table) == 12) return cmapFmt12(table, charCode, glyph);
                return false;
            }
        }
        for (unsigned idx = 0; idx < numEntries; ++idx) {   // then one of the BMP
            uint32_t entry = cmap + 4 + idx * 8;
            int type = getu16(entry) * 0100 + getu16(entry + 2);
            if (type == 0003 || type == 0301) {
                uint32_t table = cmap + getu32(entry + 4);
                if (!safe(table, 6)) return false;
                switch (getu16(table)) {
                    case 4: return cmapFmt4(table + 6, charCode, glyph);
                    case 6: return cmapFmt6(table + 6, charCode, glyph);
                    default: return false;
                }
            }
        }
        return false;
    }

    // ---- metrics and outlines -----------------------------------------------------------------------------------------------------------------
    bool horMetrics(uint32_t glyph, int& advanceWidth, int& leftSideBearing) const {
        uint32_t hmtx;
        if (!gettable("hmtx", hmtx)) return false;
        if (glyph < numLongHmtx_) {
            uint32_t offset = hmtx + 4 * glyph;
            if (!safe(offset, 4)) return false;
            advanceWidth = getu16(offset);
            leftSideBearing = geti16(offset + 2);
            return true;
        }
        uint32_t boundary = hmtx + 4U * numLongHmtx_;
        if (boundary < 4) return false;
        uint32_t offset = boundary - 4;
        if (!safe(offset, 4)) return false;
        advanceWidth = getu16(offset);
        offset = boundary + 2 * (glyph - numLongHmtx_);
        if (!safe(offset, 2)) return false;
        leftSideBearing = geti16(offset);
        return true;
    }
    bool glyphBbox(double size, uint32_t outline, int box[4]) const {
        if (!safe(outline, 10)) return false;
        box[0] = geti16(outline + 2); box[1] = geti16(outline + 4); box[2] = geti16(outline + 6); box[3] = geti16(outline + 8);
        if (box[2] <= box[0] || box[3] <= box[1]) return false;
        double xScale = size / unitsPerEm_, yScale = size / unitsPerEm_;
        box[0] = (int)std::floor(box[0] * xScale + 0.0);
        box[1] = (int)std::floor(box[1] * yScale + 0.0);
        box[2] = (int)std::ceil(box[2] * xScale + 0.0);
        box[3] = (int)std::ceil(box[3] * yScale + 0.0);
        return true;
    }
    bool outlineOffset(uint32_t glyph, uint32_t& offset) const {
        uint32_t loca, glyf;
        if (!gettable("loca", loca) || !gettable("glyf", glyf)) return false;
        uint32_t base, thisOff, next;
        if (locaFormat_ == 0) {
            base = loca + 2 * glyph;
            if (!safe(base, 4)) return false;
            thisOff = 2U * getu16(base);
            next = 2U * getu16(base + 2);
        } else {
            base = loca + 4 * glyph;
            if (!safe(base, 8)) return false;
            thisOff = getu32(base);
            next = getu32(base + 4);
        }
        offset = thisOff == next ? 0 : glyf + thisOff;
        return true;
    }

    static Point midpoint(Point a, Point b) { return Point{0.5 * (a.x + b.x), 0.5 * (a.y + b.y)}; }
    static bool growOk(const Outline& o) { return o.points.size() < 0xFFFF && o.curves.size() < 0xFFFF && o.lines.size() < 0xFFFF; }

    bool simpleFlags(uint32_t& offset, unsigned numPts, std::vector<uint8_t>& flags) const {
        uint32_t off = offset;
        uint8_t value = 0, repeat = 0;
        for (unsigned i = 0; i < numPts; ++i) {
            if (repeat) --repeat;
            else {
                if (!safe(off, 1)) return false;
                value = getu8(off++);
                if (value & 0x08) {
                    if (!safe(off, 1)) return false;
                    repeat = getu8(off++);
                }
            }
            flags[i] = value;
        }
        offset = off;
        return true;
    }
    bool simplePoints(uint32_t offset, unsigned numPts, const std::vector<uint8_t>& flags, Point* points) const {
        long accum = 0;
        for (unsigned i = 0; i < numPts; ++i) {
            if (flags[i] & 0x02) {
                if (!safe(offset, 1)) return false;
                long value = (long)getu8(offset++);
                long bit = !!(flags[i] & 0x10);
                accum -= (value ^ -bit) + bit;
            } else if (!(flags[i] & 0x10)) {
                if (!safe(offset, 2)) return false;
                accum += geti16(offset);
                offset += 2;
            }
            points[i].x = (double)accum;
        }
        accum = 0;
        for (unsigned i = 0; i < numPts; ++i) {
            if (flags[i] & 0x04) {
                if (!safe(offset, 1)) return false;
                long value = (long)getu8(offset++);
                long bit = !!(flags[i] & 0x20);
                accum -= (value ^ -bit) + bit;
            } else if (!(flags[i] & 0x20)) {
                if (!safe(offset, 2)) return false;
                accum += geti16(offset);
                offset += 2;
            }
            points[i].y = (double)accum;
        }
        return true;
    }
    bool decodeContour(const uint8_t* flags, unsigned basePoint, unsigned count, Outline& outl) const {
        if (count < 2) return true;
        unsigned looseEnd;
        if (flags[0] & 0x01) {
            looseEnd = basePoint++;
            ++flags;
            --count;
        } else if (flags[count - 1] & 0x01) {
            looseEnd = basePoint + --count;
        } else {
            if (!growOk(outl)) return false;
            looseEnd = (unsigned)outl.points.size();
            outl.points.push_back(midpoint(outl.points[basePoint], outl.points[basePoint + count - 1]));
        }
        unsigned beg = looseEnd, ctrl = 0;
        bool gotCtrl = false;
        for (unsigned i = 0; i < count; ++i) {
            unsigned cur = basePoint + i;
            if (flags[i] & 0x01) {
                if (!growOk(outl)) return false;
                if (gotCtrl) outl.curves.push_back(Curve{(uint16_t)beg, (uint16_t)cur, (uint16_t)ctrl});
                else outl.lines.push_back(Line{(uint16_t)beg, (uint16_t)cur});
                beg = cur;
                gotCtrl = false;
            } else {
                if (gotCtrl) {
                    if (!growOk(outl)) return false;
                    unsigned center = (unsigned)outl.points.size();
                    outl.points.push_back(midpoint(outl.points[ctrl], outl.points[cur]));
                    outl.curves.push_back(Curve{(uint16_t)beg, (uint16_t)center, (uint16_t)ctrl});
                    beg = center;
                }
                ctrl = cur;
                gotCtrl = true;
            }
        }
        if (!growOk(outl)) return false;
        if (gotCtrl) outl.curves.push_back(Curve{(uint16_t)beg, (uint16_t)looseEnd, (uint16_t)ctrl});
        else outl.lines.push_back(Line{(uint16_t)beg, (uint16_t)looseEnd});
        return true;
    }
    bool simpleOutline(uint32_t offset, unsigned numContours, Outline& outl) const {
        unsigned basePoint = (unsigned)outl.points.size();
        if (!safe(offset, numContours * 2 + 2)) return false;
        unsigned numPts = getu16(offset + (numContours - 1) * 2);
        if (numPts >= 0xFFFF) return false;
        numPts++;
        if (outl.points.size() > 0xFFFFu - numPts) return false;
        std::vector<unsigned> endPts(numContours);
        for (unsigned i = 0; i < numContours; ++i) { endPts[i] = getu16(offset); offset += 2; }
        for (unsigned i = 0; i + 1 < numContours; ++i) if (endPts[i + 1] < endPts[i] + 1) return false;   // (falling end points: damaged or malicious)
        offset += 2U + getu16(offset);
        std::vector<uint8_t> flags(numPts);
        if (!simpleFlags(offset, numPts, flags)) return false;
        outl.points.resize(basePoint + numPts);
        if (!simplePoints(offset, numPts, flags, outl.points.data() + basePoint)) return false;
        unsigned beg = 0;
        for (unsigned i = 0; i < numContours; ++i) {
            if (endPts[i] < beg || endPts[i] >= numPts) return false;
            unsigned count = endPts[i] - beg + 1;
            if (!decodeContour(flags.data() + beg, basePoint + beg, count, outl)) return false;
            beg = endPts[i] + 1;
        }
        return true;
    }
    bool compoundOutline(uint32_t offset, int recDepth, Outline& outl) const {
        if (recDepth >= 4) return false;
        unsigned flags;
        do {
            double local[6] = {0, 0, 0, 0, 0, 0};
            if (!safe(offset, 4)) return false;
            flags = getu16(offset);
            unsigned glyph = getu16(offset + 2);
            offset += 4;
            if (!(flags & 0x002)) return false;   // (matching points: not implemented, as in libschrift)
            if (flags & 0x001) {
                if (!safe(offset, 4)) return false;
                local[4] = geti16(offset);
                local[5] = geti16(offset + 2);
                offset += 4;
            } else {
                if (!safe(offset, 2)) return false;
                local[4] = getu8s(offset);
                local[5] = getu8s(offset + 1);
                offset += 2;
            }
            if (flags & 0x008) {
                if (!safe(offset, 2)) return false;
                local[0] = geti16(offset) / 16384.0;
                local[3] = local[0];
                offset += 2;
            } else if (flags & 0x040) {
                if (!safe(offset, 4)) return false;
                local[0] = geti16(offset + 0) / 16384.0;
                local[3] = geti16(offset + 2) / 16384.0;
                offset += 4;
            } else if (flags & 0x080) {
                if (!safe(offset, 8)) return false;
                local[0] = geti16(offset + 0) / 16384.0;
                local[1] = geti16(offset + 2) / 16384.0;
                local[2] = geti16(offset + 4) / 16384.0;
                local[3] = geti16(offset + 6) / 16384.0;
                offset += 8;
            } else {
                local[0] = 1.0;
                local[3] = 1.0;
            }
            uint32_t outline;
            if (!outlineOffset(glyph, outline)) return false;
            if (outline) {
                size_t basePoint = outl.points.size();
                if (!decodeOutline(outline, recDepth + 1, outl)) return false;
                transformPoints(outl.points.data() + basePoint, outl.points.size() - basePoint, local);
            }
        } while (flags & 0x020);
        return true;
    }
    bool decodeOutline(uint32_t offset, int recDepth, Outline& outl) const {
        if (!safe(offset, 10)) return false;
        int numContours = geti16(offset);
        if (numContours > 0) return simpleOutline(offset + 10, (unsigned)numContours, outl);
        if (numContours < 0) return compoundOutline(offset + 10, recDepth, outl);
        return true;
    }

    // ---- rasterising ----------------------------------------------------------------------------------------------------------------------------
    static void transformPoints(Point* points, size_t n, const double trf[6]) {
        for (size_t i = 0; i < n; ++i) {
            Point pt = points[i];
            points[i] = Point{pt.x * trf[0] + pt.y * trf[2] + trf[4], pt.x * trf[1] + pt.y * trf[3] + trf[5]};
        }
    }
    static void clipPoints(std::vector<Point>& points, int width, int height) {
        for (Point& p : points) {
            Point pt = p;
            if (pt.x < 0.0) p.x = 0.0;
            if (pt.x >= width) p.x = std::nextafter((double)width, 0.0);
            if (pt.y < 0.0) p.y = 0.0;
            if (pt.y >= height) p.y = std::nextafter((double)height, 0.0);
        }
    }
    static bool isFlat(const Outline& outl, Curve curve) {
        const double maxArea2 = 2.0;
        Point a = outl.points[curve.beg], b = outl.points[curve.ctrl], c = outl.points[curve.end];
        Point g{b.x - a.x, b.y - a.y}, h{c.x - a.x, c.y - a.y};
        double area2 = std::fabs(g.x * h.y - h.x * g.y);
        return area2 <= maxArea2;
    }
    static bool tesselateCurve(Curve curve, Outline& outl) {
        const unsigned stackSize = 10;
        Curve stack[stackSize];
        unsigned top = 0;
        for (;;) {
            if (isFlat(outl, curve) || top >= stackSize) {
                if (outl.lines.size() >= 0xFFFF) return false;
                outl.lines.push_back(Line{curve.beg, curve.end});
                if (top == 0) break;
                curve = stack[--top];
            } else {
                if (outl.points.size() + 3 > 0xFFFF) return false;
                uint16_t ctrl0 = (uint16_t)outl.points.size();
                outl.points.push_back(midpoint(outl.points[curve.beg], outl.points[curve.ctrl]));
                uint16_t ctrl1 = (uint16_t)outl.points.size();
                outl.points.push_back(midpoint(outl.points[curve.ctrl], outl.points[curve.end]));
                uint16_t pivot = (uint16_t)outl.points.size();
                outl.points.push_back(midpoint(outl.points[ctrl0], outl.points[ctrl1]));
                stack[top++] = Curve{curve.beg, pivot, ctrl0};
                curve = Curve{pivot, curve.end, ctrl1};
            }
        }
        return true;
    }
    static int fastFloor(double x) { int i = (int)x; return i - (i > x); }
    static int fastCeil(double x) { int i = (int)x; return i + (i < x); }
    static int sign(double x) { return (x > 0) - (x < 0); }

    static void drawLine(Cell* cells, size_t numCells, int width, Point origin, Point goal) {
        Point delta{goal.x - origin.x, goal.y - origin.y};
        int dirX = sign(delta.x), dirY = sign(delta.y);
        if (!dirY) return;
        Point crossingIncr{dirX ? std::fabs(1.0 / delta.x) : 1.0, std::fabs(1.0 / delta.y)};
        Point nextCrossing{0, 0};
        int pixelX, pixelY, numSteps = 0;
        if (!dirX) {
            pixelX = fastFloor(origin.x);
            nextCrossing.x = 100.0;
        } else if (dirX > 0) {
            pixelX = fastFloor(origin.x);
            nextCrossing.x = (origin.x - pixelX) * crossingIncr.x;
            nextCrossing.x = crossingIncr.x - nextCrossing.x;
            numSteps += fastCeil(goal.x) - fastFloor(origin.x) - 1;
        } else {
            pixelX = fastCeil(origin.x) - 1;
            nextCrossing.x = (origin.x - pixelX) * crossingIncr.x;
            numSteps += fastCeil(origin.x) - fastFloor(goal.x) - 1;
        }
        if (dirY > 0) {
            pixelY = fastFloor(origin.y);
            nextCrossing.y = (origin.y - pixelY) * crossingIncr.y;
            nextCrossing.y = crossingIncr.y - nextCrossing.y;
            numSteps += fastCeil(goal.y) - fastFloor(origin.y) - 1;
        } else {
            pixelY = fastCeil(origin.y) - 1;
            nextCrossing.y = (origin.y - pixelY) * crossingIncr.y;
            numSteps += fastCeil(origin.y) - fastFloor(goal.y) - 1;
        }
        double nextDistance = std::min(nextCrossing.x, nextCrossing.y);
        double halfDeltaX = 0.5 * delta.x;
        double prevDistance = 0.0;
        for (int step = 0; step < numSteps; ++step) {
            double xAverage = origin.x + (prevDistance + nextDistance) * halfDeltaX;
            double yDifference = (nextDistance - prevDistance) * delta.y;
            long long at = (long long)pixelY * width + pixelX;
            if (at < 0 || (unsigned long long)at >= numCells) return;   // (cannot happen with clipped points; a safety net for damaged input)
            Cell& cell = cells[at];
            cell.cover += yDifference;
            xAverage -= (double)pixelX;
            cell.area += (1.0 - xAverage) * yDifference;
            prevDistance = nextDistance;
            bool alongX = nextCrossing.x < nextCrossing.y;
            pixelX += alongX ? dirX : 0;
            pixelY += alongX ? 0 : dirY;
            nextCrossing.x += alongX ? crossingIncr.x : 0.0;
            nextCrossing.y += alongX ? 0.0 : crossingIncr.y;
            nextDistance = std::min(nextCrossing.x, nextCrossing.y);
        }
        double xAverage = origin.x + (prevDistance + 1.0) * halfDeltaX;
        double yDifference = (1.0 - prevDistance) * delta.y;
        long long at = (long long)pixelY * width + pixelX;
        if (at < 0 || (unsigned long long)at >= numCells) return;
        Cell& cell = cells[at];
        cell.cover += yDifference;
        xAverage -= (double)pixelX;
        cell.area += (1.0 - xAverage) * yDifference;
    }
    static bool renderOutline(Outline& outl, const double transform[6], uint8_t* pixels, int width, int height) {
        if (width <= 0 || height <= 0) return true;
        size_t numPixels = (size_t)width * (size_t)height;
        std::vector<Cell> cells(numPixels, Cell{0.0, 0.0});
        transformPoints(outl.points.data(), outl.points.size(), transform);
        clipPoints(outl.points, width, height);
        for (size_t i = 0; i < outl.curves.size(); ++i)   // (the tesselation adds lines and points, not curves)
            if (!tesselateCurve(outl.curves[i], outl)) return false;
        for (const Line& line : outl.lines) drawLine(cells.data(), numPixels, width, outl.points[line.beg], outl.points[line.end]);
        double accum = 0.0;
        for (size_t i = 0; i < numPixels; ++i) {
            double value = std::fabs(accum + cells[i].area);
            value = std::min(value, 1.0);
            value = value * 255.0 + 0.5;
            pixels[i] = (uint8_t)value;
            accum += cells[i].cover;
        }
        return true;
    }
};

}  // namespace ttf
}  // namespace gfx
}  // namespace fire
