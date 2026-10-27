// fire native graphics: image decoders - PNG (all color types and depths, interlaced, with its own inflate), BMP (1-32 bits, RLE, bit masks) and GIF (the first image).
// A port of src/fire.Terminal/Images; the error messages are the same. No exceptions: a decoder returns false and says why.
#pragma once

#include <cstdint>
#include <cstring>
#include <string>
#include <vector>
#include "fire_gfx.hpp"

namespace fire {
namespace gfx {

/// What a decoder makes: palette pixels (indices + palette) or true color pixels.
struct ImageData {
    int width = 0, height = 0;
    bool indexed = false;
    std::vector<uint8_t> indices;
    uint32_t palette[256];
    int transparentIndex = -1;
    std::vector<uint32_t> pixels;
    std::string format;

    void paletteBlack() { for (int i = 0; i < 256; i++) palette[i] = 0xFF000000u; }

    /// The framebuffer of this image; `mode` -1: like the image.
    bool toFramebuffer(int mode, Framebuffer& fb) const {
        int target = mode >= 0 ? mode : (indexed ? INDEXED : RGBA);
        if (!fb.create(width, height, target)) return false;
        if (target == RGBA) {
            if (!indexed) std::memcpy(fb.pixels, pixels.data(), pixels.size() * 4);
            else for (size_t i = 0; i < indices.size(); i++) fb.pixels[i] = palette[indices[i]];
            return true;
        }
        if (indexed) {
            std::memcpy(fb.indices, indices.data(), indices.size());
            fb.palette.setAll(palette, 256);
            fb.transparentIndex = transparentIndex;
            return true;
        }
        // true color into a palette framebuffer: the nearest entry of the default palette (without alpha), remembered per color
        struct Cached { uint32_t rgb; uint8_t index; bool valid; };
        std::vector<Cached> cache(4096, Cached{0, 0, false});
        for (size_t i = 0; i < pixels.size(); i++) {
            uint32_t c = pixels[i] & 0x00FFFFFFu;
            Cached& slot = cache[(c * 2654435761u) >> 20];
            if (!slot.valid || slot.rgb != c) { slot.rgb = c; slot.index = fb.palette.findNearest(c | 0xFF000000u); slot.valid = true; }
            fb.indices[i] = slot.index;
        }
        return true;
    }
};

constexpr int64_t MAX_PIXELS = 64LL * 1024 * 1024;

inline bool checkSize(const char* format, int64_t w, int64_t h, std::string& err) {
    if (w <= 0 || h <= 0) { err = std::string(format) + ": invalid image size " + std::to_string(w) + "x" + std::to_string(h) + "."; return false; }
    if (w * h > MAX_PIXELS) { err = std::string(format) + ": image too large (" + std::to_string(w) + "x" + std::to_string(h) + "; at most " + std::to_string(MAX_PIXELS / (1024 * 1024)) + " million pixels)."; return false; }
    return true;
}

#define FIRE_IMG_FAIL(message) do { err = (message); return false; } while (0)

// ---- inflate (RFC 1951) inside a zlib stream (RFC 1950) -------------------------------------------------------------------------------------------
namespace inflate_detail {

struct Bits {
    const uint8_t* in; size_t n, pos = 0; uint32_t buf = 0; int count = 0; bool truncated = false;
    int get(int need) {
        while (count < need) {
            if (pos >= n) { truncated = true; return 0; }
            buf |= (uint32_t)in[pos++] << count;
            count += 8;
        }
        int v = (int)(buf & ((1u << need) - 1));
        buf >>= need;
        count -= need;
        return v;
    }
};

struct Huffman {
    uint16_t counts[16], symbols[288];
    bool build(const uint8_t* lengths, int n) {
        std::memset(counts, 0, sizeof counts);
        for (int i = 0; i < n; i++) counts[lengths[i]]++;
        counts[0] = 0;
        int left = 1;
        for (int len = 1; len < 16; len++) { left <<= 1; left -= counts[len]; if (left < 0) return false; }
        uint16_t offsets[16];
        offsets[1] = 0;
        for (int len = 1; len < 15; len++) offsets[len + 1] = (uint16_t)(offsets[len] + counts[len]);
        for (int i = 0; i < n; i++) if (lengths[i]) symbols[offsets[lengths[i]]++] = (uint16_t)i;
        return true;
    }
    int decode(Bits& b) const {
        int code = 0, first = 0, index = 0;
        for (int len = 1; len < 16; len++) {
            code |= b.get(1);
            if (b.truncated) return -1;
            int count = counts[len];
            if (code - count < first) return symbols[index + (code - first)];
            index += count; first += count; first <<= 1; code <<= 1;
        }
        return -2;   // no such code
    }
};

}  // namespace inflate_detail

/// 0: `outSize` bytes were produced; 1: the data is invalid; 2: the data ended too soon.
inline int zlibInflate(const uint8_t* in, size_t n, uint8_t* out, size_t outSize) {
    using namespace inflate_detail;
    if (n < 2) return 2;
    if ((in[0] & 0x0F) != 8 || ((in[0] << 8) | in[1]) % 31 != 0 || (in[1] & 0x20)) return 1;
    Bits b{in + 2, n - 2};
    size_t o = 0;
    static const uint16_t lenBase[29] = {3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258};
    static const uint8_t lenExtra[29] = {0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0};
    static const uint16_t distBase[30] = {1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577};
    static const uint8_t distExtra[30] = {0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13};
    bool last = false;
    while (!last && o < outSize) {
        last = b.get(1) != 0;
        int type = b.get(2);
        if (b.truncated) return 2;
        if (type == 0) {
            b.buf = 0; b.count = 0;   // stored block: to the next byte
            if (b.pos + 4 > b.n) return 2;
            unsigned len = b.in[b.pos] | (b.in[b.pos + 1] << 8), nlen = b.in[b.pos + 2] | (b.in[b.pos + 3] << 8);
            b.pos += 4;
            if ((len ^ 0xFFFF) != nlen) return 1;
            if (b.pos + len > b.n) { size_t have = b.n - b.pos; for (size_t i = 0; i < have && o < outSize; i++) out[o++] = b.in[b.pos + i]; return o >= outSize ? 0 : 2; }
            for (unsigned i = 0; i < len && o < outSize; i++) out[o++] = b.in[b.pos + i];
            b.pos += len;
            continue;
        }
        if (type == 3) return 1;
        Huffman lit, dist;
        if (type == 1) {
            uint8_t lengths[288];
            for (int i = 0; i < 144; i++) lengths[i] = 8;
            for (int i = 144; i < 256; i++) lengths[i] = 9;
            for (int i = 256; i < 280; i++) lengths[i] = 7;
            for (int i = 280; i < 288; i++) lengths[i] = 8;
            lit.build(lengths, 288);
            uint8_t dl[30];
            for (int i = 0; i < 30; i++) dl[i] = 5;
            dist.build(dl, 30);
        } else {
            int nlen = b.get(5) + 257, ndist = b.get(5) + 1, ncode = b.get(4) + 4;
            if (b.truncated) return 2;
            if (nlen > 286 || ndist > 30) return 1;
            static const uint8_t order[19] = {16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15};
            uint8_t lengths[320];
            std::memset(lengths, 0, sizeof lengths);
            for (int i = 0; i < ncode; i++) lengths[order[i]] = (uint8_t)b.get(3);
            if (b.truncated) return 2;
            Huffman code;
            if (!code.build(lengths, 19)) return 1;
            int index = 0;
            uint8_t all[320];
            std::memset(all, 0, sizeof all);
            while (index < nlen + ndist) {
                int sym = code.decode(b);
                if (sym == -1) return 2;
                if (sym < 0) return 1;
                if (sym < 16) all[index++] = (uint8_t)sym;
                else {
                    int prev = 0, rep;
                    if (sym == 16) { if (index == 0) return 1; prev = all[index - 1]; rep = 3 + b.get(2); }
                    else if (sym == 17) rep = 3 + b.get(3);
                    else rep = 11 + b.get(7);
                    if (b.truncated) return 2;
                    if (index + rep > nlen + ndist) return 1;
                    while (rep--) all[index++] = (uint8_t)prev;
                }
            }
            if (!lit.build(all, nlen)) return 1;
            if (!dist.build(all + nlen, ndist)) return 1;
        }
        while (true) {
            int sym = lit.decode(b);
            if (sym == -1) return 2;
            if (sym < 0) return 1;
            if (sym < 256) { if (o >= outSize) return 0; out[o++] = (uint8_t)sym; if (o >= outSize) return 0; continue; }
            if (sym == 256) break;
            sym -= 257;
            if (sym >= 29) return 1;
            int len = lenBase[sym] + b.get(lenExtra[sym]);
            int ds = dist.decode(b);
            if (ds == -1 || b.truncated) return 2;
            if (ds < 0 || ds >= 30) return 1;
            size_t d = distBase[ds] + (size_t)b.get(distExtra[ds]);
            if (b.truncated) return 2;
            if (d > o) return 1;
            for (int i = 0; i < len && o < outSize; i++, o++) out[o] = out[o - d];
            if (o >= outSize) return 0;
        }
    }
    return o >= outSize ? 0 : 2;
}

// ---- PNG ---------------------------------------------------------------------------------------------------------------------------------------------
namespace png_detail {

inline uint32_t crc(const uint8_t* p, size_t n) {
    static uint32_t table[256];
    static bool ready = false;
    if (!ready) {
        for (uint32_t k = 0; k < 256; k++) { uint32_t c = k; for (int i = 0; i < 8; i++) c = (c & 1) ? 0xEDB88320u ^ (c >> 1) : c >> 1; table[k] = c; }
        ready = true;
    }
    uint32_t c = 0xFFFFFFFFu;
    for (size_t i = 0; i < n; i++) c = table[(c ^ p[i]) & 0xFF] ^ (c >> 8);
    return c ^ 0xFFFFFFFFu;
}
inline uint32_t be32(const uint8_t* d, size_t p) { return ((uint32_t)d[p] << 24) | ((uint32_t)d[p + 1] << 16) | ((uint32_t)d[p + 2] << 8) | d[p + 3]; }

static const int PassX[7] = {0, 4, 0, 2, 0, 1, 0}, PassY[7] = {0, 0, 4, 0, 2, 0, 1}, PassDx[7] = {8, 8, 4, 4, 2, 2, 1}, PassDy[7] = {8, 8, 8, 4, 4, 2, 2};
inline int passWidth(int w, int pass) { return w > PassX[pass] ? (w - PassX[pass] + PassDx[pass] - 1) / PassDx[pass] : 0; }
inline int passHeight(int h, int pass) { return h > PassY[pass] ? (h - PassY[pass] + PassDy[pass] - 1) / PassDy[pass] : 0; }

inline uint32_t sample(const uint8_t* row, int index, int bitDepth) {
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
inline uint32_t scale(uint32_t v, int bitDepth) {
    switch (bitDepth) { case 8: return v; case 16: return v >> 8; case 1: return v * 255; case 2: return v * 85; default: return v * 17; }
}

inline bool unfilter(int filter, uint8_t* cur, const uint8_t* prior, size_t n, int bpp, std::string& err) {
    switch (filter) {
        case 0: break;
        case 1: for (size_t i = (size_t)bpp; i < n; i++) cur[i] = (uint8_t)(cur[i] + cur[i - bpp]); break;
        case 2: for (size_t i = 0; i < n; i++) cur[i] = (uint8_t)(cur[i] + prior[i]); break;
        case 3: for (size_t i = 0; i < n; i++) { int left = i >= (size_t)bpp ? cur[i - bpp] : 0; cur[i] = (uint8_t)(cur[i] + ((left + prior[i]) >> 1)); } break;
        case 4:
            for (size_t i = 0; i < n; i++) {
                int a = i >= (size_t)bpp ? cur[i - bpp] : 0, b = prior[i], c = i >= (size_t)bpp ? prior[i - bpp] : 0;
                int p = a + b - c, pa = std::abs(p - a), pb = std::abs(p - b), pc = std::abs(p - c);
                int pred = pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
                cur[i] = (uint8_t)(cur[i] + pred);
            }
            break;
        default: err = "PNG: unknown scanline filter " + std::to_string(filter) + "."; return false;
    }
    return true;
}

struct Pass {
    int bitDepth, colorType, channels, bitsPerPixel, bpp, fullWidth;
    bool hasKey; uint32_t keyGray, keyR, keyG, keyB;
};

inline bool decodePass(const std::vector<uint8_t>& raw, size_t& offset, int pw, int ph, int x0, int y0, int dx, int dy, const Pass& p, ImageData& out, std::string& err) {
    size_t rowBytes = (size_t)(((int64_t)pw * p.bitsPerPixel + 7) / 8);
    std::vector<uint8_t> prior(rowBytes, 0), cur(rowBytes, 0);
    for (int row = 0; row < ph; row++) {
        int filter = raw[offset++];
        std::memcpy(cur.data(), raw.data() + offset, rowBytes);
        offset += rowBytes;
        if (!unfilter(filter, cur.data(), prior.data(), rowBytes, p.bpp, err)) return false;
        int y = y0 + row * dy;
        for (int col = 0; col < pw; col++) {
            int x = x0 + col * dx;
            size_t pos = (size_t)y * p.fullWidth + x;
            if (out.indexed) { out.indices[pos] = (uint8_t)sample(cur.data(), col, p.bitDepth); continue; }
            uint32_t r, g, b, a = 255;
            switch (p.colorType) {
                case 0: { uint32_t v = sample(cur.data(), col, p.bitDepth); if (p.hasKey && v == p.keyGray) a = 0; r = g = b = scale(v, p.bitDepth); break; }
                case 2: {
                    uint32_t rr = sample(cur.data(), col * 3, p.bitDepth), gg = sample(cur.data(), col * 3 + 1, p.bitDepth), bb = sample(cur.data(), col * 3 + 2, p.bitDepth);
                    if (p.hasKey && rr == p.keyR && gg == p.keyG && bb == p.keyB) a = 0;
                    r = scale(rr, p.bitDepth); g = scale(gg, p.bitDepth); b = scale(bb, p.bitDepth);
                    break;
                }
                case 4: r = g = b = scale(sample(cur.data(), col * 2, p.bitDepth), p.bitDepth); a = scale(sample(cur.data(), col * 2 + 1, p.bitDepth), p.bitDepth); break;
                default:
                    r = scale(sample(cur.data(), col * 4, p.bitDepth), p.bitDepth); g = scale(sample(cur.data(), col * 4 + 1, p.bitDepth), p.bitDepth);
                    b = scale(sample(cur.data(), col * 4 + 2, p.bitDepth), p.bitDepth); a = scale(sample(cur.data(), col * 4 + 3, p.bitDepth), p.bitDepth);
                    break;
            }
            out.pixels[pos] = r | (g << 8) | (b << 16) | (a << 24);
        }
        std::swap(prior, cur);
    }
    return true;
}

}  // namespace png_detail

inline bool decodePng(const uint8_t* d, size_t size, ImageData& out, std::string& err) {
    using namespace png_detail;
    size_t pos = 8;
    int width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
    bool haveHeader = false, sawEnd = false;
    std::vector<uint8_t> plte, trns, idat;
    while (!sawEnd) {
        if (pos + 12 > size) FIRE_IMG_FAIL("PNG: file truncated (no IEND).");
        uint32_t length = be32(d, pos);
        if (length > 0x7FFFFFFFu || (uint64_t)pos + 12 + length > size) FIRE_IMG_FAIL("PNG: file truncated (chunk extends past the end).");
        std::string type((const char*)d + pos + 4, 4);
        const uint8_t* data = d + pos + 8;
        if (crc(d + pos + 4, 4 + (size_t)length) != be32(d, pos + 8 + length)) FIRE_IMG_FAIL("PNG: checksum error in chunk '" + type + "'.");
        pos += 12 + (size_t)length;
        if (!haveHeader && type != "IHDR") FIRE_IMG_FAIL("PNG: the first chunk must be IHDR.");
        if (type == "IHDR") {
            if (haveHeader || length != 13) FIRE_IMG_FAIL("PNG: invalid IHDR chunk.");
            width = (int)std::min<uint32_t>(be32(data, 0), 0x7FFFFFFF);
            height = (int)std::min<uint32_t>(be32(data, 4), 0x7FFFFFFF);
            bitDepth = data[8]; colorType = data[9];
            if (data[10] != 0) FIRE_IMG_FAIL("PNG: unknown compression method.");
            if (data[11] != 0) FIRE_IMG_FAIL("PNG: unknown filter method.");
            interlace = data[12];
            if (interlace > 1) FIRE_IMG_FAIL("PNG: unknown interlace method.");
            if (!checkSize("PNG", width, height, err)) return false;
            bool validDepth = colorType == 0 ? (bitDepth == 1 || bitDepth == 2 || bitDepth == 4 || bitDepth == 8 || bitDepth == 16)
                : (colorType == 2 || colorType == 4 || colorType == 6) ? (bitDepth == 8 || bitDepth == 16)
                : colorType == 3 ? (bitDepth == 1 || bitDepth == 2 || bitDepth == 4 || bitDepth == 8) : false;
            if (!validDepth) FIRE_IMG_FAIL("PNG: invalid combination of color type " + std::to_string(colorType) + " and bit depth " + std::to_string(bitDepth) + ".");
            haveHeader = true;
        } else if (type == "PLTE") {
            if (length == 0 || length % 3 != 0 || length > 768) FIRE_IMG_FAIL("PNG: invalid palette.");
            plte.assign(data, data + length);
        } else if (type == "tRNS") trns.assign(data, data + length);
        else if (type == "IDAT") idat.insert(idat.end(), data, data + length);
        else if (type == "IEND") sawEnd = true;
    }
    if (colorType == 3 && plte.empty()) FIRE_IMG_FAIL("PNG: Palette-Bild ohne PLTE-Chunk.");
    if (idat.empty()) FIRE_IMG_FAIL("PNG: no image data (IDAT).");

    int channels = colorType == 0 ? 1 : colorType == 2 ? 3 : colorType == 3 ? 1 : colorType == 4 ? 2 : 4;
    int bitsPerPixel = channels * bitDepth;
    int bpp = std::max(1, bitsPerPixel / 8);
    int64_t expected = 0;
    if (interlace == 0) expected = ((int64_t)width * bitsPerPixel + 7) / 8 + 1, expected *= height;
    else for (int pass = 0; pass < 7; pass++) {
        int pw = passWidth(width, pass), ph = passHeight(height, pass);
        if (pw > 0 && ph > 0) expected += (((int64_t)pw * bitsPerPixel + 7) / 8 + 1) * ph;
    }
    if (expected > 0x7FFFFFFF) FIRE_IMG_FAIL("PNG: image too large.");
    std::vector<uint8_t> raw((size_t)expected);
    int status = zlibInflate(idat.data(), idat.size(), raw.data(), raw.size());
    if (status == 2) FIRE_IMG_FAIL("PNG: the image data is too short (truncated or corrupt).");
    if (status == 1) FIRE_IMG_FAIL("PNG: the image data cannot be decompressed (corrupt).");

    out.width = width; out.height = height; out.format = "PNG";
    bool indexed = colorType == 3;
    out.indexed = indexed;
    if (indexed) out.indices.assign((size_t)width * height, 0); else out.pixels.assign((size_t)width * height, 0);
    Pass p{bitDepth, colorType, channels, bitsPerPixel, bpp, width, false, 0, 0, 0, 0};
    if (!indexed && colorType != 4 && colorType != 6 && !trns.empty()) {
        if (colorType == 0 && trns.size() >= 2) { p.keyGray = (uint32_t)(trns[0] << 8 | trns[1]); p.hasKey = true; }
        else if (colorType == 2 && trns.size() >= 6) { p.keyR = (uint32_t)(trns[0] << 8 | trns[1]); p.keyG = (uint32_t)(trns[2] << 8 | trns[3]); p.keyB = (uint32_t)(trns[4] << 8 | trns[5]); p.hasKey = true; }
    }
    size_t offset = 0;
    if (interlace == 0) { if (!decodePass(raw, offset, width, height, 0, 0, 1, 1, p, out, err)) return false; }
    else for (int pass = 0; pass < 7; pass++) {
        int pw = passWidth(width, pass), ph = passHeight(height, pass);
        if (pw == 0 || ph == 0) continue;
        if (!decodePass(raw, offset, pw, ph, PassX[pass], PassY[pass], PassDx[pass], PassDy[pass], p, out, err)) return false;
    }
    if (indexed) {
        out.paletteBlack();
        size_t entries = plte.size() / 3;
        int transparent = -1;
        for (size_t i = 0; i < entries; i++) {
            uint32_t a = i < trns.size() ? trns[i] : 255u;
            out.palette[i] = plte[i * 3] | ((uint32_t)plte[i * 3 + 1] << 8) | ((uint32_t)plte[i * 3 + 2] << 16) | (a << 24);
            if (a == 0 && transparent < 0) transparent = (int)i;
        }
        for (uint8_t b : out.indices) if (b >= entries) FIRE_IMG_FAIL("PNG: a pixel refers to a palette entry that does not exist.");
        out.transparentIndex = transparent;
        return true;
    }
    out.transparentIndex = -1;
    return true;
}

// ---- BMP ---------------------------------------------------------------------------------------------------------------------------------------------
namespace bmp_detail {
inline int le16(const uint8_t* d, size_t p) { return d[p] | (d[p + 1] << 8); }
inline int le32(const uint8_t* d, size_t p) { return (int)((uint32_t)d[p] | ((uint32_t)d[p + 1] << 8) | ((uint32_t)d[p + 2] << 16) | ((uint32_t)d[p + 3] << 24)); }
inline int trailingZeros(uint32_t v) { int n = 0; while (!(v & 1)) { v >>= 1; n++; } return n; }
inline uint32_t extract(uint32_t v, uint32_t mask) {
    if (mask == 0) return 0;
    int shift = trailingZeros(mask);
    uint32_t max = mask >> shift;
    return ((v & mask) >> shift) * 255 / max;
}
}  // namespace bmp_detail

inline bool decodeBmp(const uint8_t* d, size_t size, ImageData& out, std::string& err) {
    using namespace bmp_detail;
    if (size < 26) FIRE_IMG_FAIL("BMP: file too short.");
    int dataOffset = le32(d, 10), headerSize = le32(d, 14);
    int width, height, bpp, compression = 0, colorsUsed = 0;
    bool topDown = false;
    if (headerSize == 12) { width = le16(d, 18); height = le16(d, 20); bpp = le16(d, 24); }
    else if (headerSize >= 40) {
        if (size < 14 + 40) FIRE_IMG_FAIL("BMP: file too short.");
        width = le32(d, 18);
        int h = le32(d, 22);
        topDown = h < 0;
        height = h == INT32_MIN ? 0 : std::abs(h);
        if (le16(d, 26) != 1) FIRE_IMG_FAIL("BMP: invalid plane count.");
        bpp = le16(d, 28);
        compression = le32(d, 30);
        colorsUsed = le32(d, 46);
    } else FIRE_IMG_FAIL("BMP: unsupported header (" + std::to_string(headerSize) + " bytes).");
    if (!checkSize("BMP", width, height, err)) return false;
    if (!(bpp == 1 || bpp == 4 || bpp == 8 || bpp == 16 || bpp == 24 || bpp == 32)) FIRE_IMG_FAIL("BMP: unsupported color depth (" + std::to_string(bpp) + " bits).");
    if (!(compression == 0 || compression == 1 || compression == 2 || compression == 3 || compression == 6)) FIRE_IMG_FAIL("BMP: unsupported compression (" + std::to_string(compression) + ").");
    if ((compression == 1 && bpp != 8) || (compression == 2 && bpp != 4)) FIRE_IMG_FAIL("BMP: RLE does not match the color depth.");
    if ((compression == 3 || compression == 6) && !(bpp == 16 || bpp == 32)) FIRE_IMG_FAIL("BMP: bit masks are only allowed with 16 and 32 bits.");
    if (dataOffset < 0 || (size_t)dataOffset > size) FIRE_IMG_FAIL("BMP: invalid data offset.");

    uint32_t maskR = 0, maskG = 0, maskB = 0, maskA = 0;
    bool hasMasks = compression == 3 || compression == 6;
    if (hasMasks) {
        size_t maskPos = 14 + 40;
        if (size < maskPos + 12) FIRE_IMG_FAIL("BMP: file too short for the bit masks.");
        maskR = (uint32_t)le32(d, maskPos); maskG = (uint32_t)le32(d, maskPos + 4); maskB = (uint32_t)le32(d, maskPos + 8);
        if ((compression == 6 || headerSize >= 56) && size >= maskPos + 16) maskA = (uint32_t)le32(d, maskPos + 12);
    } else if (bpp == 16) { maskR = 0x7C00; maskG = 0x03E0; maskB = 0x001F; }

    out.width = width; out.height = height; out.format = "BMP";
    out.paletteBlack();
    if (bpp <= 8) {
        int entries = colorsUsed > 0 ? colorsUsed : 1 << bpp;
        if (entries > 256 || entries > (1 << bpp)) entries = std::min(256, 1 << bpp);
        int entrySize = headerSize == 12 ? 3 : 4;
        size_t palPos = 14 + (size_t)headerSize;
        if (palPos + (size_t)entries * entrySize > size) FIRE_IMG_FAIL("BMP: file too short for the palette.");
        for (int i = 0; i < entries; i++) {
            size_t p = palPos + (size_t)i * entrySize;
            out.palette[i] = d[p + 2] | ((uint32_t)d[p + 1] << 8) | ((uint32_t)d[p] << 16) | 0xFF000000u;
        }
        out.indexed = true;
        out.transparentIndex = -1;
        out.indices.assign((size_t)width * height, 0);
        if (compression == 0) {
            int64_t rowSize = ((int64_t)width * bpp + 31) / 32 * 4;
            if ((int64_t)dataOffset + rowSize * height > (int64_t)size) FIRE_IMG_FAIL("BMP: file too short for the image data.");
            for (int row = 0; row < height; row++) {
                int y = topDown ? row : height - 1 - row;
                size_t rowStart = (size_t)dataOffset + (size_t)row * rowSize;
                for (int x = 0; x < width; x++) {
                    int bitPos = x * bpp, shift = 8 - bpp - (bitPos & 7);
                    out.indices[(size_t)y * width + x] = (uint8_t)((d[rowStart + (bitPos >> 3)] >> shift) & ((1 << bpp) - 1));
                }
            }
            return true;
        }
        bool rle8 = compression == 1;
        size_t p = (size_t)dataOffset;
        int x = 0, row = 0;
        auto put = [&](int index) {
            if (x >= 0 && x < width && row >= 0 && row < height) out.indices[(size_t)(height - 1 - row) * width + x] = (uint8_t)index;
            x++;
        };
        while (p + 1 < size) {
            int count = d[p], value = d[p + 1];
            p += 2;
            if (count > 0) { for (int i = 0; i < count; i++) put(rle8 ? value : (i % 2 == 0 ? value >> 4 : value & 0xF)); continue; }
            if (value == 0) { x = 0; row++; continue; }
            if (value == 1) break;
            if (value == 2) { if (p + 1 >= size) break; x += d[p]; row += d[p + 1]; p += 2; continue; }
            int n = value, bytes = rle8 ? n : (n + 1) / 2;
            if (p + (size_t)bytes > size) FIRE_IMG_FAIL("BMP: RLE-Daten abgeschnitten.");
            for (int i = 0; i < n; i++) put(rle8 ? d[p + i] : (i % 2 == 0 ? d[p + i / 2] >> 4 : d[p + i / 2] & 0xF));
            p += (size_t)bytes + (bytes & 1);
        }
        return true;
    }

    int64_t rowSize = ((int64_t)width * bpp + 31) / 32 * 4;
    if ((int64_t)dataOffset + rowSize * height > (int64_t)size) FIRE_IMG_FAIL("BMP: file too short for the image data.");
    out.indexed = false;
    out.transparentIndex = -1;
    out.pixels.assign((size_t)width * height, 0);
    bool anyAlpha = false;
    for (int row = 0; row < height; row++) {
        int y = topDown ? row : height - 1 - row;
        size_t rowStart = (size_t)dataOffset + (size_t)row * rowSize;
        for (int x = 0; x < width; x++) {
            uint32_t r, g, b, a = 255;
            if (bpp == 24) { size_t p = rowStart + (size_t)x * 3; b = d[p]; g = d[p + 1]; r = d[p + 2]; }
            else {
                uint32_t v = bpp == 16 ? (uint32_t)le16(d, rowStart + (size_t)x * 2) : (uint32_t)le32(d, rowStart + (size_t)x * 4);
                if (bpp == 32 && !hasMasks) { b = v & 0xFF; g = (v >> 8) & 0xFF; r = (v >> 16) & 0xFF; a = v >> 24; if (a != 0) anyAlpha = true; }
                else { r = extract(v, maskR); g = extract(v, maskG); b = extract(v, maskB); if (maskA != 0) a = extract(v, maskA); }
            }
            out.pixels[(size_t)y * width + x] = r | (g << 8) | (b << 16) | (a << 24);
        }
    }
    if (bpp == 32 && !hasMasks && !anyAlpha) for (uint32_t& px : out.pixels) px |= 0xFF000000u;   // the fourth byte is mostly unused: the image is opaque
    return true;
}

// ---- GIF ---------------------------------------------------------------------------------------------------------------------------------------------
namespace gif_detail {
inline bool readPalette(const uint8_t* d, size_t size, size_t& pos, int entries, uint32_t* palette, std::string& err) {
    if (pos + (size_t)entries * 3 > size) { err = "GIF: file truncated (palette)."; return false; }
    for (int i = 0; i < 256; i++) palette[i] = 0xFF000000u;
    for (int i = 0; i < entries; i++) { palette[i] = d[pos] | ((uint32_t)d[pos + 1] << 8) | ((uint32_t)d[pos + 2] << 16) | 0xFF000000u; pos += 3; }
    return true;
}
inline bool lzw(const std::vector<uint8_t>& data, int minCodeSize, size_t pixelCount, std::vector<uint8_t>& output, std::string& err) {
    output.assign(pixelCount, 0);
    int clear = 1 << minCodeSize, end = clear + 1;
    std::vector<int16_t> prefix(4096, 0);
    std::vector<uint8_t> suffix(4096, 0), stack(4097, 0);
    int codeSize = minCodeSize + 1, next = end + 1, prev = -1;
    uint8_t first = 0;
    size_t outPos = 0, dataPos = 0;
    int bitBuffer = 0, bitCount = 0;
    while (outPos < pixelCount) {
        while (bitCount < codeSize) {
            if (dataPos >= data.size()) return true;   // data ends without an end code: lenient
            bitBuffer |= data[dataPos++] << bitCount;
            bitCount += 8;
        }
        int code = bitBuffer & ((1 << codeSize) - 1);
        bitBuffer >>= codeSize;
        bitCount -= codeSize;
        if (code == clear) { codeSize = minCodeSize + 1; next = end + 1; prev = -1; continue; }
        if (code == end) break;
        if (prev == -1) {
            if (code >= clear) { err = "GIF: invalid LZW code at the start."; return false; }
            output[outPos++] = (uint8_t)code; prev = code; first = (uint8_t)code;
            continue;
        }
        int cur = code, sp = 0;
        if (code >= next) {
            if (code > next) { err = "GIF: corrupt LZW data (code outside of the dictionary)."; return false; }
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
    return true;
}
}  // namespace gif_detail

inline bool decodeGif(const uint8_t* d, size_t size, ImageData& out, std::string& err) {
    using namespace gif_detail;
    if (size < 13) FIRE_IMG_FAIL("GIF: file too short.");
    int screenW = d[6] | (d[7] << 8), screenH = d[8] | (d[9] << 8), flags = d[10], backgroundIndex = d[11];
    if (!checkSize("GIF", screenW, screenH, err)) return false;
    size_t pos = 13;
    uint32_t globalPalette[256];
    bool haveGlobal = false;
    if (flags & 0x80) { if (!readPalette(d, size, pos, 2 << (flags & 7), globalPalette, err)) return false; haveGlobal = true; }
    int transparent = -1;
    while (pos < size) {
        int block = d[pos++];
        if (block == 0x3B) break;
        if (block == 0x21) {
            if (pos >= size) break;
            int label = d[pos++];
            if (label == 0xF9 && pos + 5 < size && d[pos] == 4) transparent = (d[pos + 1] & 1) != 0 ? d[pos + 4] : -1;
            while (pos < size) { int len = d[pos++]; if (len == 0) break; pos += (size_t)len; }
            continue;
        }
        if (block != 0x2C) { char hex[8]; std::snprintf(hex, sizeof hex, "%02X", block); FIRE_IMG_FAIL(std::string("GIF: unknown block 0x") + hex + "."); }
        if (pos + 9 > size) FIRE_IMG_FAIL("GIF: file truncated (image descriptor).");
        int left = d[pos] | (d[pos + 1] << 8), top = d[pos + 2] | (d[pos + 3] << 8), w = d[pos + 4] | (d[pos + 5] << 8), h = d[pos + 6] | (d[pos + 7] << 8), iflags = d[pos + 8];
        pos += 9;
        uint32_t palette[256];
        bool havePalette = haveGlobal;
        if (haveGlobal) std::memcpy(palette, globalPalette, sizeof palette);
        if (iflags & 0x80) { if (!readPalette(d, size, pos, 2 << (iflags & 7), palette, err)) return false; havePalette = true; }
        if (!havePalette) FIRE_IMG_FAIL("GIF: weder globale noch lokale Palette.");
        bool interlaced = (iflags & 0x40) != 0;
        if (pos >= size) FIRE_IMG_FAIL("GIF: file truncated (image data).");
        int minCode = d[pos++];
        if (minCode < 2 || minCode > 11) FIRE_IMG_FAIL("GIF: invalid LZW code size.");
        std::vector<uint8_t> packed;
        while (true) {
            if (pos >= size) FIRE_IMG_FAIL("GIF: file truncated (image data).");
            int len = d[pos++];
            if (len == 0) break;
            if (pos + (size_t)len > size) FIRE_IMG_FAIL("GIF: file truncated (image data).");
            packed.insert(packed.end(), d + pos, d + pos + len);
            pos += (size_t)len;
        }
        std::vector<uint8_t> frame;
        if (!lzw(packed, minCode, (size_t)w * (size_t)h, frame, err)) return false;
        int fill = transparent >= 0 ? transparent : std::min(backgroundIndex, 255);
        out.width = screenW; out.height = screenH; out.format = "GIF"; out.indexed = true; out.transparentIndex = transparent;
        out.indices.assign((size_t)screenW * screenH, (uint8_t)fill);
        std::memcpy(out.palette, palette, sizeof palette);
        std::vector<int> rows((size_t)h);
        if (!interlaced) for (int i = 0; i < h; i++) rows[(size_t)i] = i;
        else {
            int n = 0;
            const int starts[4] = {0, 4, 2, 1}, steps[4] = {8, 8, 4, 2};
            for (int s = 0; s < 4; s++) for (int y = starts[s]; y < h; y += steps[s]) rows[(size_t)n++] = y;
        }
        for (int r = 0; r < h; r++) {
            int y = top + rows[(size_t)r];
            if (y < 0 || y >= screenH) continue;
            for (int x = 0; x < w; x++) {
                int px = left + x;
                if (px < 0 || px >= screenW) continue;
                out.indices[(size_t)y * screenW + px] = frame[(size_t)r * w + x];
            }
        }
        return true;
    }
    FIRE_IMG_FAIL("GIF: the file contains no image.");
}

/// Reads PNG, BMP or GIF. false: `err` is the message of the ImageException.
inline bool decodeImage(const uint8_t* d, size_t n, ImageData& out, std::string& err) {
    if (n == 0) FIRE_IMG_FAIL("The image data is empty.");
    if (n >= 8 && d[0] == 0x89 && d[1] == 'P' && d[2] == 'N' && d[3] == 'G' && d[4] == 0x0D && d[5] == 0x0A && d[6] == 0x1A && d[7] == 0x0A) return decodePng(d, n, out, err);
    if (n >= 2 && d[0] == 'B' && d[1] == 'M') return decodeBmp(d, n, out, err);
    if (n >= 6 && d[0] == 'G' && d[1] == 'I' && d[2] == 'F' && d[3] == '8' && (d[4] == '7' || d[4] == '9') && d[5] == 'a') return decodeGif(d, n, out, err);
    FIRE_IMG_FAIL("Unknown image format (expected: PNG, BMP or GIF).");
}

#undef FIRE_IMG_FAIL

}  // namespace gfx
}  // namespace fire
