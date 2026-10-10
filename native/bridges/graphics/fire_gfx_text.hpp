// fire native graphics: fonts for text at pixel positions (TextFont: the two built-in bitmap fonts or a TrueType font), the layout and drawing of text, the table of the fonts of a program with
// the lookup by name, and the fonts installed on the system. A port of src/fire.Terminal/TextFont.cs - the same pixels come out (the layout is described there).
#pragma once

#include <cstdlib>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <vector>
#include "fire_gfx.hpp"
#include "fire_gfx_ttf.hpp"

namespace fire {
namespace gfx {

constexpr int kDefaultFontSize = 14, kMaxFontSize = 512;

/// Rounds half up (floor(x + 0.5)), like TextFont.Round in C#.
inline int roundHalfUp(double x) { return (int)std::floor(x + 0.5); }

/// The next code point of a UTF-16 text (a surrogate pair is one; a lone surrogate is U+FFFD).
inline uint32_t nextCodePoint(const char16_t* text, uint32_t n, uint32_t& i) {
    char16_t c = text[i++];
    if (c >= 0xD800 && c <= 0xDBFF && i < n && text[i] >= 0xDC00 && text[i] <= 0xDFFF) {
        uint32_t cp = 0x10000 + (((uint32_t)c - 0xD800) << 10) + ((uint32_t)text[i] - 0xDC00);
        i++;
        return cp;
    }
    return (c >= 0xD800 && c <= 0xDFFF) ? 0xFFFDu : (uint32_t)c;
}

struct TextFont {
    std::string name;
    bool bitmap = false, smallBitmap = false;   // a built-in bitmap font: 8x14 (default) or 8x8
    ttf::Font ttfFont;

    struct Cached { ttf::GMetrics m; std::vector<uint8_t> image; bool hasImage = false; };
    std::unordered_map<int64_t, Cached> glyphs;

    static int clampSize(int size) { return size <= 0 ? kDefaultFontSize : std::min(size, kMaxFontSize); }

    int cellWidth() const { return 8; }
    int cellHeight() const { return smallBitmap ? 8 : 14; }

    int ascent(int size) const {
        if (bitmap) return cellHeight();
        size = clampSize(size);
        ttf::LMetrics m;
        return ttfFont.lmetrics(size, m) ? roundHalfUp(m.ascender) : size;
    }
    int lineHeight(int size) const {
        if (bitmap) return cellHeight();
        size = clampSize(size);
        ttf::LMetrics m;
        if (!ttfFont.lmetrics(size, m)) return size;
        return std::max(1, roundHalfUp(m.ascender) + roundHalfUp(-m.descender) + roundHalfUp(m.lineGap));
    }
    int baseline(int size) const {
        ttf::LMetrics m;
        if (!ttfFont.lmetrics(size, m)) return size;
        return roundHalfUp(m.ascender) + (roundHalfUp(m.lineGap) >> 1);
    }

    const Cached& glyph(int size, uint32_t g) {
        int64_t key = ((int64_t)size << 32) | (int64_t)g;
        auto it = glyphs.find(key);
        if (it != glyphs.end()) return it->second;
        if (glyphs.size() >= 8192) glyphs.clear();
        Cached c;
        if (ttfFont.gmetrics(size, g, c.m)) {
            if (c.m.minWidth > 0 && c.m.minHeight > 0) {
                c.image.assign((size_t)c.m.minWidth * c.m.minHeight, 0);
                c.hasImage = ttfFont.render(size, g, c.image.data(), c.m.minWidth, c.m.minHeight);
            }
        } else c.m = ttf::GMetrics();
        return glyphs.emplace(key, std::move(c)).first->second;
    }

    /// The layout loop: the pen position (fractions kept) after the text; `draw(cached glyph, pen position rounded to pixels)` is called for every glyph.
    template <class Draw>
    double layout(const char16_t* text, uint32_t n, int size, Draw draw) {
        double pen = 0;
        uint32_t previous = 0;
        bool hasPrevious = false;
        for (uint32_t i = 0; i < n;) {
            uint32_t cp = nextCodePoint(text, n, i);
            if (cp < 0x20) continue;
            uint32_t g;
            if (!ttfFont.lookup(cp, g)) g = 0;
            double kern;
            if (hasPrevious && ttfFont.kerning(size, previous, g, kern)) pen += kern;
            const Cached& c = glyph(size, g);
            draw(c, roundHalfUp(pen));
            pen += c.m.advanceWidth;
            previous = g;
            hasPrevious = true;
        }
        return pen;
    }

    int measure(const char16_t* text, uint32_t n, int size) {
        if (bitmap) return (int)n * cellWidth();
        size = clampSize(size);
        return roundHalfUp(layout(text, n, size, [](const Cached&, int) {}));
    }

    void drawBitmap(Surface& s, int x, int y, const char16_t* text, uint32_t n, const Pixel& fg, bool hasBg, const Pixel& bg) const {
        int cw = cellWidth(), ch = cellHeight();
        if (hasBg && !s.visible(bg)) hasBg = false;
        bool fgVisible = s.visible(fg);
        if (!fgVisible && !hasBg) return;
        for (uint32_t i = 0; i < n; i++) {
            uint32_t c = text[i] < 256 ? text[i] : '?';
            const uint8_t* rows = smallBitmap ? kGlyphs8x8[c] : kGlyphs8x14[c];
            if (hasBg) s.rect(x, y, cw, ch, bg);
            if (fgVisible)
                for (int gy = 0; gy < ch; gy++) {
                    unsigned bits = rows[gy];
                    if (bits == 0) continue;
                    for (int gx = 0; gx < cw; gx++)
                        if ((bits >> (7 - gx)) & 1u) s.put(x + gx, y + gy, fg);
                }
            x += cw;
        }
    }

    void drawTrueType(Surface& s, int x, int y, const char16_t* text, uint32_t n, int size, const Pixel& fg, bool hasBg, const Pixel& bg) {
        size = clampSize(size);
        if (hasBg && !s.visible(bg)) hasBg = false;
        bool fgVisible = s.visible(fg);
        if (!fgVisible && !hasBg) return;
        if (hasBg) s.rect(x, y, measure(text, n, size), lineHeight(size), bg);
        if (!fgVisible) return;
        int base = y + baseline(size);
        uint32_t rgb = fg.rgba & 0x00FFFFFFu, alpha = fg.rgba >> 24;
        uint8_t index = fg.index;
        layout(text, n, size, [&](const Cached& g, int penX) {
            if (!g.hasImage) return;
            int gx0 = x + penX + g.m.xOffset, gy0 = base + g.m.yOffset;
            int w = g.m.minWidth, h = g.m.minHeight;
            for (int gy = 0; gy < h; gy++)
                for (int gx = 0; gx < w; gx++) {
                    uint32_t c = g.image[(size_t)gy * w + gx];
                    if (c == 0) continue;
                    uint32_t a = (alpha * c + 127) / 255;
                    if (a == 0) continue;
                    Pixel p;
                    p.rgba = rgb | (a << 24);
                    p.index = index;
                    s.put(gx0 + gx, gy0 + gy, p);
                }
        });
    }
};

/// Name for comparing: lower case letters and digits only (ASCII; everything else is dropped).
inline std::string normalizeFontName(const std::string& name) {
    std::string out;
    for (char ch : name) {
        unsigned char c = (unsigned char)ch;
        if (c >= 'A' && c <= 'Z') out.push_back((char)(c + 32));
        else if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) out.push_back((char)c);
    }
    return out;
}

/// "fonts/Roboto-Bold.ttf" -> "Roboto-Bold" (the last part of the path without the extension .ttf/.otf/.ttc).
inline std::string fontBaseName(const std::string& path) {
    size_t slash = path.find_last_of("/\\");
    std::string file = slash == std::string::npos ? path : path.substr(slash + 1);
    size_t dot = file.find_last_of('.');
    if (dot != std::string::npos && dot > 0) {
        std::string ext = file.substr(dot);
        for (char& c : ext) if (c >= 'A' && c <= 'Z') c = (char)(c + 32);
        if (ext == ".ttf" || ext == ".otf" || ext == ".ttc") return file.substr(0, dot);
    }
    return file;
}

/// The fonts installed on the system: the folders FIRE_FONT_DIRS (separated by ';'), then the usual font folders of Windows, macOS and Linux.
struct SystemFonts {
    struct Entry { std::string path, key, familyKey, fullKey; bool namesRead = false; };
    std::vector<Entry> entries;
    bool scanned = false;
    std::string dirsKey;

    static std::string env(const char* name) { const char* v = std::getenv(name); return v ? v : ""; }

    static std::vector<std::string> folders() {
        std::vector<std::string> out;
        std::string dirs = env("FIRE_FONT_DIRS");
        size_t start = 0;
        while (start <= dirs.size() && !dirs.empty()) {
            size_t end = dirs.find(';', start);
            std::string part = dirs.substr(start, end == std::string::npos ? std::string::npos : end - start);
            if (!part.empty()) out.push_back(part);
            if (end == std::string::npos) break;
            start = end + 1;
        }
        std::string win = env("WINDIR"), local = env("LOCALAPPDATA"), home = env("HOME"), xdg = env("XDG_DATA_HOME");
        if (!win.empty()) out.push_back(win + "/Fonts");
        if (!local.empty()) out.push_back(local + "/Microsoft/Windows/Fonts");
        out.push_back("/System/Library/Fonts");
        out.push_back("/System/Library/Fonts/Supplemental");
        out.push_back("/Library/Fonts");
        out.push_back("/usr/share/fonts");
        out.push_back("/usr/local/share/fonts");
        if (!home.empty()) {
            out.push_back(home + "/Library/Fonts");
            out.push_back(home + "/.fonts");
            out.push_back(home + "/.local/share/fonts");
        }
        if (!xdg.empty()) out.push_back(xdg + "/fonts");
        return out;
    }

    static bool read(const std::string& path, std::vector<uint8_t>& bytes) {
        plat::fs::Status status;
        std::FILE* f = plat::fs::open(path, 0, 0, status);
        if (!f) return false;
        bytes.clear();
        uint8_t chunk[4096];
        size_t got;
        while ((got = std::fread(chunk, 1, sizeof chunk, f)) > 0) bytes.insert(bytes.end(), chunk, chunk + got);
        std::fclose(f);
        return true;
    }

    void scan() {
        std::string key = env("FIRE_FONT_DIRS");
        if (scanned && key == dirsKey) return;
        entries.clear();
        for (const std::string& folder : folders()) {
            std::vector<std::string> files, wanted;
            if (plat::fs::list(folder, "*", true, false, files).code != 0) continue;
            for (const std::string& file : files) {
                size_t dot = file.find_last_of('.');
                if (dot == std::string::npos) continue;
                std::string ext = file.substr(dot);
                for (char& c : ext) if (c >= 'A' && c <= 'Z') c = (char)(c + 32);
                if (ext == ".ttf" || ext == ".otf") wanted.push_back(file);
            }
            std::sort(wanted.begin(), wanted.end());
            for (const std::string& file : wanted) {
                Entry e;
                e.path = file;
                e.key = normalizeFontName(fontBaseName(file));
                entries.push_back(std::move(e));
            }
        }
        dirsKey = key;
        scanned = true;
    }

    static bool tryLoad(const std::string& path, std::vector<uint8_t>& bytes, ttf::Font* out = nullptr) {
        if (!read(path, bytes)) return false;
        ttf::Font probe;
        ttf::Font& f = out ? *out : probe;
        return f.load(bytes.data(), bytes.size());
    }

    /// The bytes of the font file for a normalized name: a file with this name first, then a font whose full name it is, then one whose family name it is.
    bool find(const std::string& name, std::vector<uint8_t>& bytes) {
        scan();
        for (Entry& e : entries)
            if (e.key == name && tryLoad(e.path, bytes)) return true;
        for (Entry& e : entries) {
            if (e.namesRead) continue;
            e.namesRead = true;
            std::vector<uint8_t> data;
            ttf::Font f;
            if (tryLoad(e.path, data, &f)) { e.familyKey = normalizeFontName(f.name(1)); e.fullKey = normalizeFontName(f.name(4)); }
        }
        for (Entry& e : entries)
            if (e.fullKey == name && tryLoad(e.path, bytes)) return true;
        for (Entry& e : entries)
            if (e.familyKey == name && tryLoad(e.path, bytes)) return true;
        return false;
    }
};

/// The fonts of a program: ID 1 = the built-in 8x14, ID 2 = the built-in 8x8, then the loaded ones (an ID is never used twice). 0 = the font of the renderer (no entry), -1 = none.
struct FontTable {
    std::vector<TextFont*> fonts;
    std::unordered_map<std::string, int> names;
    std::unordered_set<std::string> systemMisses;
    SystemFonts system;

    FontTable() {
        fonts.push_back(nullptr);
        TextFont* big = new TextFont();
        big->name = "8x14"; big->bitmap = true;
        TextFont* small = new TextFont();
        small->name = "8x8"; small->bitmap = true; small->smallBitmap = true;
        fonts.push_back(big);
        fonts.push_back(small);
    }
    FontTable(const FontTable&) = delete;
    FontTable& operator=(const FontTable&) = delete;
    ~FontTable() { for (TextFont* f : fonts) delete f; }

    TextFont* get(int64_t id) const { return id > 0 && (size_t)id < fonts.size() ? fonts[(size_t)id] : nullptr; }

    int load(const uint8_t* data, size_t size) {
        TextFont* f = new TextFont();
        if (!f->ttfFont.load(data, size)) { delete f; return -1; }
        f->name = f->ttfFont.name(4);
        if (f->name.empty()) f->name = f->ttfFont.name(1);
        fonts.push_back(f);
        return (int)fonts.size() - 1;
    }

    void registerName(const std::string& name, int id) {
        std::string key = normalizeFontName(name);
        if (!key.empty()) names[key] = id;
    }

    int add(const uint8_t* data, size_t size, const std::string& alias) {
        int id = load(data, size);
        if (id < 0) return -1;
        registerName(fontBaseName(alias), id);
        registerName(fonts[(size_t)id]->ttfFont.name(1), id);
        registerName(fonts[(size_t)id]->ttfFont.name(4), id);
        return id;
    }

    int open(const std::string& name) {
        std::string key = normalizeFontName(name);
        if (key.empty() || key == "console" || key == "default") return 0;
        if (key == "8x14" || key == "14x8") return 1;
        if (key == "8x8") return 2;
        auto known = names.find(key);
        if (known != names.end()) return known->second;
        if (systemMisses.count(key)) return -1;
        std::vector<uint8_t> bytes;
        int id = system.find(key, bytes) ? load(bytes.data(), bytes.size()) : -1;
        if (id < 0) { systemMisses.insert(key); return -1; }
        names[key] = id;
        return id;
    }

    bool destroy(int64_t id) {
        if (id <= 2 || (size_t)id >= fonts.size() || !fonts[(size_t)id]) return false;
        delete fonts[(size_t)id];
        fonts[(size_t)id] = nullptr;
        for (auto it = names.begin(); it != names.end();) { if (it->second == id) it = names.erase(it); else ++it; }
        return true;
    }
};

/// Text in a font at pixel coordinates (`font` null = the font of the renderer): the glyph pixels in the color of `fg`, with `bg` (null = none) the cell or the line below.
inline void drawTextIn(Renderer& r, TextFont* font, int size, int x, int y, const char16_t* text, uint32_t n, const Brush& fg, const Brush* bg) {
    if (!font) { r.drawText(x, y, text, n, fg, bg); return; }
    Surface s = r.surface();
    Pixel f = s.resolve(fg.color);
    Pixel b = bg ? s.resolve(bg->color) : Pixel();
    if (font->bitmap) font->drawBitmap(s, x, y, text, n, f, bg != nullptr, b);
    else font->drawTrueType(s, x, y, text, n, size, f, bg != nullptr, b);
}

}  // namespace gfx
}  // namespace fire
