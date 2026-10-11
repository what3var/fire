using System;
using System.Collections.Generic;
using System.IO;
using fire.Terminal.TrueType;

namespace fire.Terminal
{
    /// <summary>
    /// A font for text at pixel positions (<see cref="Renderer.DrawText(int, int, string, Brush, Brush?, TextFont?, int)"/>): either one of the two built-in bitmap fonts (8x14, 8x8 - a fixed size, the size argument is ignored) or a
    /// TrueType font (any size in pixels, anti-aliased).
    ///
    /// The layout of a TrueType text is simple and exactly specified, because the native build has the same code in C++ (fire_gfx.hpp) and both draw the same pixels: the size is the em in pixels; the line is
    /// `Ascent + Descent + LineGap` pixels high (each rounded); the baseline is `Ascent + LineGap / 2` below the top; the pen starts at x and moves on by the advance width of every glyph plus the
    /// kerning of the pair (in fractions of a pixel); a glyph is drawn at the pen position rounded to whole pixels. Characters below U+0020 are skipped, a character the font does not have is drawn as the glyph 0 of the font.
    /// </summary>
    public sealed class TextFont
    {
        public const int DefaultSize = 14, MaxSize = 512;

        private readonly TrueTypeFont? _ttf;
        private readonly Dictionary<long, CachedGlyph> _glyphs = new();

        private readonly struct CachedGlyph
        {
            public readonly GlyphMetrics Metrics;
            public readonly byte[]? Image;
            public CachedGlyph(GlyphMetrics metrics, byte[]? image) { Metrics = metrics; Image = image; }
        }

        public string Name { get; }
        public IGlyphFont? Bitmap { get; }
        public TrueTypeFont? TrueType => _ttf;
        public bool IsBitmap => Bitmap != null;

        public TextFont(string name, IGlyphFont bitmap) { Name = name; Bitmap = bitmap; }
        public TextFont(string name, TrueTypeFont ttf) { Name = name; _ttf = ttf; }

        public static int ClampSize(int size) => size <= 0 ? DefaultSize : Math.Min(size, MaxSize);

        /// <summary>Pixels above the baseline (rounded ascender); for a bitmap font the height of a cell.</summary>
        public int Ascent(int size)
        {
            if (Bitmap != null) return Bitmap.GlyphHeight;
            size = ClampSize(size);
            return _ttf!.TryGetLineMetrics(size, out var m) ? Round(m.Ascender) : size;
        }

        /// <summary>Height of a line of text in pixels (ascent + descent + line gap); for a bitmap font the height of a cell.</summary>
        public int LineHeight(int size)
        {
            if (Bitmap != null) return Bitmap.GlyphHeight;
            size = ClampSize(size);
            if (!_ttf!.TryGetLineMetrics(size, out var m)) return size;
            return Math.Max(1, Round(m.Ascender) + Round(-m.Descender) + Round(m.LineGap));
        }

        /// <summary>The distance from the top of the line to the baseline.</summary>
        private int Baseline(int size)
        {
            if (!_ttf!.TryGetLineMetrics(size, out var m)) return size;
            return Round(m.Ascender) + (Round(m.LineGap) >> 1);
        }

        /// <summary>Width of a text in pixels (the pen position after the last glyph, rounded).</summary>
        public int Measure(string text, int size)
        {
            if (Bitmap != null) return text.Length * Bitmap.GlyphWidth;
            size = ClampSize(size);
            double pen = 0;
            Layout(text, size, ref pen, null);
            return Round(pen);
        }

        /// <summary>Draws a TrueType text with its upper left corner at (x, y).</summary>
        internal void DrawTrueType(in Surface surface, int x, int y, string text, int size, in Pixel foreground, bool hasBackground, in Pixel background)
        {
            size = ClampSize(size);
            if (hasBackground && !surface.Visible(background)) hasBackground = false;
            bool fgVisible = surface.Visible(foreground);
            if (!fgVisible && !hasBackground) return;
            if (hasBackground) surface.Rect(x, y, Measure(text, size), LineHeight(size), background);
            if (!fgVisible) return;
            int baseline = y + Baseline(size);
            uint rgb = foreground.Rgba & 0x00FFFFFFu, alpha = foreground.Rgba >> 24;
            byte index = foreground.Index;
            double pen = 0;
            var s = surface;
            Layout(text, size, ref pen, (glyph, penX) =>
            {
                if (glyph.Image == null) return;
                int gx0 = x + penX + glyph.Metrics.XOffset, gy0 = baseline + glyph.Metrics.YOffset;
                int w = glyph.Metrics.MinWidth, h = glyph.Metrics.MinHeight;
                byte[] image = glyph.Image;
                for (int gy = 0; gy < h; gy++)
                    for (int gx = 0; gx < w; gx++)
                    {
                        uint c = image[gy * w + gx];
                        if (c == 0) continue;
                        uint a = (alpha * c + 127) / 255;
                        if (a == 0) continue;
                        s.Put(gx0 + gx, gy0 + gy, new Pixel(rgb | (a << 24), index));
                    }
            });
        }

        /// <summary>Draws a text with a bitmap font (the cells of this font, one after the other, like the text of the renderer's own font).</summary>
        internal void DrawBitmap(in Surface surface, int x, int y, string text, in Pixel foreground, bool hasBackground, in Pixel background)
        {
            var font = Bitmap!;
            int cw = font.GlyphWidth, ch = font.GlyphHeight;
            if (hasBackground && !surface.Visible(background)) hasBackground = false;
            bool fgVisible = surface.Visible(foreground);
            if (!fgVisible && !hasBackground) return;
            foreach (char c in text)
            {
                if (hasBackground) surface.Rect(x, y, cw, ch, background);
                if (fgVisible)
                    for (int gy = 0; gy < ch; gy++)
                        for (int gx = 0; gx < cw; gx++)
                            if (font.IsPixelSet(c, gx, gy)) surface.Put(x + gx, y + gy, foreground);
                x += cw;
            }
        }

        // the layout loop: `pen` is the pen position in pixels (fractions kept); `draw` gets every glyph with its pen position rounded to whole pixels
        private void Layout(string text, int size, ref double pen, Action<CachedGlyph, int>? draw)
        {
            var ttf = _ttf!;
            uint previous = 0;
            bool hasPrevious = false;
            for (int i = 0; i < text.Length;)
            {
                uint cp = NextCodePoint(text, ref i);
                if (cp < 0x20) continue;
                if (!ttf.TryLookup(cp, out uint glyph)) glyph = 0;
                if (hasPrevious && ttf.TryGetKerning(size, previous, glyph, out double kern)) pen += kern;
                var g = GetGlyph(size, glyph);
                draw?.Invoke(g, Round(pen));
                pen += g.Metrics.AdvanceWidth;
                previous = glyph;
                hasPrevious = true;
            }
        }

        private CachedGlyph GetGlyph(int size, uint glyph)
        {
            long key = ((long)size << 32) | glyph;
            if (_glyphs.TryGetValue(key, out var cached)) return cached;
            if (_glyphs.Count >= 8192) _glyphs.Clear();
            GlyphMetrics metrics;
            byte[]? image = null;
            if (_ttf!.TryGetGlyphMetrics(size, glyph, out metrics))
            {
                if (metrics.MinWidth > 0 && metrics.MinHeight > 0)
                {
                    image = new byte[metrics.MinWidth * metrics.MinHeight];
                    if (!_ttf.TryRender(size, glyph, image, metrics.MinWidth, metrics.MinHeight)) image = null;
                }
            }
            else metrics = default;
            cached = new CachedGlyph(metrics, image);
            _glyphs[key] = cached;
            return cached;
        }

        /// <summary>The next code point of a UTF-16 string (a surrogate pair is one; a lone surrogate is U+FFFD).</summary>
        internal static uint NextCodePoint(string s, ref int i)
        {
            char c = s[i++];
            if (char.IsHighSurrogate(c) && i < s.Length && char.IsLowSurrogate(s[i])) return (uint)char.ConvertToUtf32(c, s[i++]);
            return char.IsSurrogate(c) ? 0xFFFDu : c;
        }

        /// <summary>Rounds half up (floor(x + 0.5)) - the same in C# and C++.</summary>
        internal static int Round(double x) => (int)Math.Floor(x + 0.5);
    }

    /// <summary>
    /// The fonts of a program (the "Fnt" natives of the graphics bridge): the two built-in bitmap fonts (ID 1 = 8x14, ID 2 = 8x8), fonts that were added with a name, and the fonts found on the system.
    /// <see cref="Open"/> finds a font by name: first the built-in ones ("8x14", "8x8", also written "14x8"), then the ones added with <see cref="Add"/> (by the alias, the file name without folder and
    /// extension, the family name and the full name of the font), then the files of the system font folders (<see cref="SystemFonts"/>). Names are compared without case, spaces and punctuation.
    /// </summary>
    public sealed class FontManager
    {
        public const int Builtin8x14 = 1, Builtin8x8 = 2;

        private readonly IdManager<TextFont> _fonts = new();
        private readonly Dictionary<string, int> _names = new();
        private readonly HashSet<string> _systemMisses = new();

        public FontManager()
        {
            _fonts.Create(new TextFont("8x14", new IntegratedGlyphFont(false)));
            _fonts.Create(new TextFont("8x8", new IntegratedGlyphFont(true)));
        }

        public TextFont Get(int id) => _fonts.Get(id);

        /// <summary>Reads a TrueType font from the bytes of a file; -1 if it is not one. The font has no name (use <see cref="Add"/> to find it by name).</summary>
        public int Load(byte[] data)
        {
            var ttf = TrueTypeFont.Load(data);
            if (ttf == null) return -1;
            string name = ttf.GetName(4);
            if (name.Length == 0) name = ttf.GetName(1);
            return _fonts.Create(new TextFont(name, ttf));
        }

        /// <summary>Reads a font and makes it findable by name: by `alias` (a name or the path of a resource: folder and extension do not count), the family name and the full name. -1 if the data is no TrueType font.</summary>
        public int Add(byte[] data, string alias)
        {
            int id = Load(data);
            if (id < 0) return -1;
            var ttf = _fonts.Get(id).TrueType!;
            Register(BaseName(alias), id);
            Register(ttf.GetName(1), id);
            Register(ttf.GetName(4), id);
            return id;
        }

        private void Register(string name, int id)
        {
            string key = NormalizeName(name);
            if (key.Length > 0) _names[key] = id;
        }

        /// <summary>The font with this name (see the class documentation): its ID; 0 for "" and "console" (the font of the renderer); -1 if there is none.</summary>
        public int Open(string name)
        {
            string key = NormalizeName(name);
            if (key.Length == 0 || key == "console" || key == "default") return 0;
            if (key == "8x14" || key == "14x8") return Builtin8x14;
            if (key == "8x8") return Builtin8x8;
            if (_names.TryGetValue(key, out int known)) return known;
            if (_systemMisses.Contains(key)) return -1;
            var data = SystemFonts.Find(key);
            int id = data == null ? -1 : Load(data);
            if (id < 0) { _systemMisses.Add(key); return -1; }
            _names[key] = id;
            return id;
        }

        /// <summary>Frees a font. The built-in ones stay (false).</summary>
        public bool Destroy(int id)
        {
            if (id <= Builtin8x8) return false;
            if (!_fonts.Destroy(id)) return false;
            foreach (var key in new List<string>(_names.Keys))
                if (_names[key] == id) _names.Remove(key);
            return true;
        }

        /// <summary>Name for comparing: lower case letters and digits only (ASCII; everything else is dropped).</summary>
        public static string NormalizeName(string name)
        {
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (c >= 'A' && c <= 'Z') sb.Append((char)(c + 32));
                else if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>"fonts/Roboto-Bold.ttf" -> "Roboto-Bold" (the last part of the path without the extension .ttf/.otf/.ttc).</summary>
        public static string BaseName(string path)
        {
            int slash = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
            string file = slash >= 0 ? path.Substring(slash + 1) : path;
            int dot = file.LastIndexOf('.');
            if (dot > 0)
            {
                string ext = file.Substring(dot).ToLowerInvariant();
                if (ext == ".ttf" || ext == ".otf" || ext == ".ttc") return file.Substring(0, dot);
            }
            return file;
        }
    }

    /// <summary>The fonts installed on the system: the folders FIRE_FONT_DIRS (separated by ';', for tests and portable programs), then the usual font folders of Windows, macOS and Linux.</summary>
    public static class SystemFonts
    {
        private sealed class Entry
        {
            public string Path = "";
            public string Key = "";            // the normalized file name
            public bool NamesRead;
            public string FamilyKey = "", FullKey = "";
        }

        private static readonly object Lock = new();
        private static string? s_dirsKey;
        private static List<Entry>? s_entries;

        public static IEnumerable<string> Folders()
        {
            var env = Environment.GetEnvironmentVariable("FIRE_FONT_DIRS");
            if (!string.IsNullOrEmpty(env))
                foreach (var part in env.Split(';'))
                    if (part.Length > 0) yield return part;
            string? win = Environment.GetEnvironmentVariable("WINDIR");
            string? local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            string? home = Environment.GetEnvironmentVariable("HOME");
            string? xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (!string.IsNullOrEmpty(win)) yield return Path.Combine(win, "Fonts");
            if (!string.IsNullOrEmpty(local)) yield return Path.Combine(local, "Microsoft", "Windows", "Fonts");
            yield return "/System/Library/Fonts";
            yield return "/System/Library/Fonts/Supplemental";
            yield return "/Library/Fonts";
            yield return "/usr/share/fonts";
            yield return "/usr/local/share/fonts";
            if (!string.IsNullOrEmpty(home))
            {
                yield return home + "/Library/Fonts";
                yield return home + "/.fonts";
                yield return home + "/.local/share/fonts";
            }
            if (!string.IsNullOrEmpty(xdg)) yield return xdg + "/fonts";
        }

        private static List<Entry> Entries()
        {
            string key = Environment.GetEnvironmentVariable("FIRE_FONT_DIRS") ?? "";
            if (s_entries != null && s_dirsKey == key) return s_entries;
            var list = new List<Entry>();
            foreach (var folder in Folders())
            {
                var files = new List<string>();
                try
                {
                    if (!Directory.Exists(folder)) continue;
                    foreach (var file in Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                    {
                        string ext = Path.GetExtension(file).ToLowerInvariant();
                        if (ext == ".ttf" || ext == ".otf") files.Add(file);
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                files.Sort(StringComparer.Ordinal);
                foreach (var file in files) list.Add(new Entry { Path = file, Key = FontManager.NormalizeName(FontManager.BaseName(file)) });
            }
            s_dirsKey = key;
            s_entries = list;
            return list;
        }

        /// <summary>The bytes of the font file for a normalized name: a file with this name first, then a font whose full name it is, then one whose family name it is. null if there is none.</summary>
        public static byte[]? Find(string normalizedName)
        {
            lock (Lock)
            {
                var entries = Entries();
                foreach (var entry in entries)
                    if (entry.Key == normalizedName && TryLoad(entry.Path, out var data)) return data;
                foreach (var entry in entries)
                {
                    if (entry.NamesRead) continue;
                    entry.NamesRead = true;
                    if (TryLoad(entry.Path, out _, out var ttf))
                    {
                        entry.FamilyKey = FontManager.NormalizeName(ttf!.GetName(1));
                        entry.FullKey = FontManager.NormalizeName(ttf.GetName(4));
                    }
                }
                foreach (var entry in entries)
                    if (entry.FullKey == normalizedName && TryLoad(entry.Path, out var data)) return data;
                foreach (var entry in entries)
                    if (entry.FamilyKey == normalizedName && TryLoad(entry.Path, out var data)) return data;
                return null;
            }
        }

        private static bool TryLoad(string path, out byte[]? data) => TryLoad(path, out data, out _);

        private static bool TryLoad(string path, out byte[]? data, out TrueTypeFont? font)
        {
            font = null;
            if (!TryRead(path, out data)) return false;
            font = TrueTypeFont.Load(data!);
            return font != null;
        }

        private static bool TryRead(string path, out byte[]? data)
        {
            try { data = File.ReadAllBytes(path); return true; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { data = null; return false; }
        }
    }
}
