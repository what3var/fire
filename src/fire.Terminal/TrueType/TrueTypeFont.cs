using System;
using System.Collections.Generic;
using System.Text;

namespace fire.Terminal.TrueType
{
    /// <summary>Metrics of one glyph for a size, in pixels (the y axis points down; <see cref="YOffset"/> is the distance of the top of the glyph image from the baseline, <see cref="XOffset"/> that of its left edge from the pen position).</summary>
    public readonly record struct GlyphMetrics(double AdvanceWidth, double LeftSideBearing, int YOffset, int MinWidth, int MinHeight, int XOffset);

    /// <summary>The vertical metrics of a font for a size, in pixels (<see cref="Descender"/> is negative).</summary>
    public readonly record struct LineMetrics(double Ascender, double Descender, double LineGap);

    /// <summary>
    /// TrueType outlines for the renderer: reads a .ttf (glyf outlines: simple and compound glyphs, cmap formats 4, 6 and 12, horizontal metrics, the 'kern' table), measures
    /// text and rasterises glyphs into 8 bit coverage images with exact area coverage (anti-aliased, no hinting).
    ///
    /// This is a port to C# of libschrift 0.10.2 (https://github.com/tomolt/libschrift, (c) 2019-2022 Thomas Oltmann and contributors, ISC license, see LICENSE-libschrift.md);
    /// the native build has the same algorithm in C++ (native/bridges/graphics/fire_gfx_ttf.hpp), and the tests compare both with the original: the glyph images are equal. Every read of the font
    /// is bounds checked (a damaged font gives a "no glyph" result and no exception). Not safe for concurrent use of ONE font from several threads (the caches are not locked).
    /// </summary>
    /// <remarks>
    /// The license of libschrift (the original), which applies to this port:
    /// <code>
    /// ISC License
    ///
    /// (c) 2019-2022 Thomas Oltmann and contributors
    ///
    /// Permission to use, copy, modify, and/or distribute this software for any
    /// purpose with or without fee is hereby granted, provided that the above
    /// copyright notice and this permission notice appear in all copies.
    ///
    /// THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES
    /// WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF
    /// MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR
    /// ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
    /// WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN
    /// ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF
    /// OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.
    /// </code>
    /// </remarks>
    public sealed class TrueTypeFont
    {
        private readonly byte[] _mem;
        private uint _unitsPerEm, _numLongHmtx;
        private int _locaFormat;

        private TrueTypeFont(byte[] data) { _mem = data; }

        /// <summary>Reads a font from the bytes of a .ttf file (the array is kept, not copied); null if it is not a TrueType font.</summary>
        public static TrueTypeFont? Load(byte[] data)
        {
            if (data == null || data.LongLength > uint.MaxValue) return null;
            var font = new TrueTypeFont(data);
            return font.Init() ? font : null;
        }

        public uint UnitsPerEm => _unitsPerEm;

        public bool TryGetLineMetrics(double size, out LineMetrics metrics)
        {
            metrics = default;
            if (!GetTable("hhea", out uint hhea) || !Safe(hhea, 36)) return false;
            double factor = size / _unitsPerEm;
            metrics = new LineMetrics(GetI16(hhea + 4) * factor, GetI16(hhea + 6) * factor, GetI16(hhea + 8) * factor);
            return true;
        }

        /// <summary>The glyph of a code point (0 if the font has none); false if the cmap could not be read.</summary>
        public bool TryLookup(uint codepoint, out uint glyph) => GlyphId(codepoint, out glyph);

        public bool TryGetGlyphMetrics(double size, uint glyph, out GlyphMetrics metrics)
        {
            metrics = default;
            double xScale = size / _unitsPerEm;
            if (!HorMetrics(glyph, out int adv, out int lsb)) return false;
            double advance = adv * xScale, bearing = lsb * xScale + 0.0;
            if (!OutlineOffset(glyph, out uint outline)) return false;
            if (outline == 0) { metrics = new GlyphMetrics(advance, bearing, 0, 0, 0, 0); return true; }
            var box = new int[4];
            if (!GlyphBbox(size, outline, box)) return false;
            metrics = new GlyphMetrics(advance, bearing, -box[3], box[2] - box[0] + 1, box[3] - box[1] + 1, box[0]);
            return true;
        }

        /// <summary>The horizontal kerning between two glyphs in pixels (the 'kern' table; 0 if there is none).</summary>
        public bool TryGetKerning(double size, uint left, uint right, out double xShift)
        {
            xShift = 0;
            if (!GetTable("kern", out uint offset)) return true;
            if (!Safe(offset, 4)) return false;
            if (GetU16(offset) != 0) return true;
            uint numTables = GetU16(offset + 2);
            offset += 4;
            double xs = 0;
            while (numTables > 0)
            {
                if (!Safe(offset, 6)) return false;
                uint length = GetU16(offset + 2), format = GetU8(offset + 4), flags = GetU8(offset + 5);
                offset += 6;
                if (format == 0 && (flags & 1) != 0 && (flags & 2) == 0)
                {
                    if (!Safe(offset, 8)) return false;
                    uint numPairs = GetU16(offset);
                    offset += 8;
                    if (!Safe(offset, numPairs * 6)) return false;
                    uint key = (left << 16) | (right & 0xFFFF);
                    long lo = 0, hi = numPairs;
                    while (lo < hi)
                    {
                        long mid = lo + (hi - lo) / 2;
                        uint k = GetU32(offset + (uint)mid * 6);
                        if (k < key) lo = mid + 1;
                        else if (k > key) hi = mid;
                        else
                        {
                            int value = GetI16(offset + (uint)mid * 6 + 4);
                            if ((flags & 4) == 0) xs += value;
                            break;
                        }
                    }
                }
                offset += length;
                --numTables;
            }
            xShift = xs / _unitsPerEm * size;
            return true;
        }

        /// <summary>Renders the glyph into `pixels` (width * height bytes of coverage 0..255; width and height at least those of the metrics). true: drawn (or the glyph has no outline).</summary>
        public bool TryRender(double size, uint glyph, byte[] pixels, int width, int height)
        {
            if (!OutlineOffset(glyph, out uint outline)) return false;
            if (outline == 0) return true;
            var box = new int[4];
            if (!GlyphBbox(size, outline, box)) return false;
            var transform = new double[6];
            transform[0] = size / _unitsPerEm;
            transform[1] = 0.0;
            transform[2] = 0.0;
            transform[4] = 0.0 - box[0];
            transform[3] = -size / _unitsPerEm;
            transform[5] = box[3] - 0.0;
            var outl = new Outline();
            if (!DecodeOutline(outline, 0, outl)) return false;
            return RenderOutline(outl, transform, pixels, width, height);
        }

        /// <summary>A name from the 'name' table (1 family, 2 style, 4 full name, 6 PostScript name); "" if there is none.</summary>
        public string GetName(int nameId)
        {
            if (!GetTable("name", out uint table) || !Safe(table, 6)) return "";
            uint count = GetU16(table + 2);
            uint strings = table + GetU16(table + 4);
            string best = "";
            int bestScore = -1;
            for (uint i = 0; i < count; i++)
            {
                uint rec = table + 6 + i * 12;
                if (!Safe(rec, 12)) break;
                uint platform = GetU16(rec), encoding = GetU16(rec + 2), language = GetU16(rec + 4), id = GetU16(rec + 6), length = GetU16(rec + 8), offset = GetU16(rec + 10);
                if ((int)id != nameId || !Safe(strings + offset, length)) continue;
                int score = -1;
                string text = "";
                if (platform == 3 && (encoding == 1 || encoding == 10)) { score = language == 0x409 ? 3 : 2; text = Utf16Be(strings + offset, length); }
                else if (platform == 0) { score = 1; text = Utf16Be(strings + offset, length); }
                else if (platform == 1 && encoding == 0)
                {
                    score = 0;
                    var sb = new StringBuilder();
                    for (uint k = 0; k < length; k++) sb.Append((char)GetU8(strings + offset + k));   // (Mac Roman: the ASCII part is what matters for names)
                    text = sb.ToString();
                }
                if (score > bestScore && text.Length > 0) { bestScore = score; best = text; }
            }
            return best;
        }

        // ---- reading ------------------------------------------------------------------------------------------------------------------------------
        private bool Safe(uint offset, uint margin)
        {
            uint size = (uint)_mem.Length;
            if (offset > size) return false;
            return size - offset >= margin;
        }

        private byte GetU8(uint o) => o < (uint)_mem.Length ? _mem[o] : (byte)0;
        private int GetI8(uint o) => (sbyte)GetU8(o);
        private uint GetU16(uint o) => (uint)((GetU8(o) << 8) | GetU8(o + 1));
        private int GetI16(uint o) => (short)GetU16(o);
        private uint GetU32(uint o) => ((uint)GetU8(o) << 24) | ((uint)GetU8(o + 1) << 16) | ((uint)GetU8(o + 2) << 8) | GetU8(o + 3);

        private string Utf16Be(uint o, uint length)
        {
            var sb = new StringBuilder();
            for (uint i = 0; i + 1 < length; i += 2) sb.Append((char)GetU16(o + i));
            return sb.ToString();
        }

        private bool GetTable(string tag, out uint offset)
        {
            offset = 0;
            uint numTables = GetU16(4);
            if (!Safe(12, numTables * 16)) return false;
            uint key = ((uint)tag[0] << 24) | ((uint)tag[1] << 16) | ((uint)tag[2] << 8) | tag[3];
            long lo = 0, hi = numTables;
            while (lo < hi)
            {
                long mid = lo + (hi - lo) / 2;
                uint k = GetU32(12 + (uint)mid * 16);
                if (k < key) lo = mid + 1;
                else if (k > key) hi = mid;
                else { offset = GetU32(12 + (uint)mid * 16 + 8); return true; }
            }
            return false;
        }

        private bool Init()
        {
            if (!Safe(0, 12)) return false;
            uint scalerType = GetU32(0);
            if (scalerType != 0x00010000 && scalerType != 0x74727565) return false;
            if (!GetTable("head", out uint head) || !Safe(head, 54)) return false;
            _unitsPerEm = GetU16(head + 18);
            _locaFormat = GetI16(head + 50);
            if (_unitsPerEm == 0) return false;
            if (!GetTable("hhea", out uint hhea) || !Safe(hhea, 36)) return false;
            _numLongHmtx = GetU16(hhea + 34);
            return true;
        }

        // ---- cmap ---------------------------------------------------------------------------------------------------------------------------------
        private bool CmapFmt4(uint table, uint charCode, out uint glyph)
        {
            glyph = 0;
            if (charCode > 0xFFFF) return true;
            uint shortCode = charCode;
            if (!Safe(table, 8)) return false;
            uint segCountX2 = GetU16(table);
            if ((segCountX2 & 1) != 0 || segCountX2 == 0) return false;
            uint endCodes = table + 8, startCodes = endCodes + segCountX2 + 2, idDeltas = startCodes + segCountX2, idRangeOffsets = idDeltas + segCountX2;
            if (!Safe(idRangeOffsets, segCountX2)) return false;
            long low = 0, high = segCountX2 / 2 - 1;
            while (low != high)
            {
                long mid = low + (high - low) / 2;
                if (GetU16(endCodes + (uint)mid * 2) < shortCode) low = mid + 1; else high = mid;
            }
            uint segIdxX2 = (uint)low * 2;
            uint startCode = GetU16(startCodes + segIdxX2);
            if (startCode > shortCode) return true;
            uint idDelta = GetU16(idDeltas + segIdxX2);
            uint idRangeOffset = GetU16(idRangeOffsets + segIdxX2);
            if (idRangeOffset == 0) { glyph = (shortCode + idDelta) & 0xFFFF; return true; }
            uint idOffset = idRangeOffsets + segIdxX2 + idRangeOffset + 2U * (shortCode - startCode);
            if (!Safe(idOffset, 2)) return false;
            uint id = GetU16(idOffset);
            glyph = id != 0 ? (id + idDelta) & 0xFFFF : 0;
            return true;
        }

        private bool CmapFmt6(uint table, uint charCode, out uint glyph)
        {
            glyph = 0;
            if (charCode > 0xFFFF) return true;
            if (!Safe(table, 4)) return false;
            uint firstCode = GetU16(table), entryCount = GetU16(table + 2);
            if (!Safe(table, 4 + 2 * entryCount)) return false;
            if (charCode < firstCode) return false;
            charCode -= firstCode;
            if (!(charCode < entryCount)) return false;
            glyph = GetU16(table + 4 + 2 * charCode);
            return true;
        }

        private bool CmapFmt12(uint table, uint charCode, out uint glyph)
        {
            glyph = 0;
            if (!Safe(table, 16)) return false;
            uint len = GetU32(table + 4);
            if (len < 16) return false;
            if (!Safe(table, len)) return false;
            uint numEntries = GetU32(table + 12);
            if (numEntries > (len - 16) / 12) return false;
            for (uint i = 0; i < numEntries; ++i)
            {
                uint firstCode = GetU32(table + i * 12 + 16), lastCode = GetU32(table + i * 12 + 16 + 4);
                if (charCode < firstCode || charCode > lastCode) continue;
                glyph = unchecked((charCode - firstCode) + GetU32(table + i * 12 + 16 + 8));
                return true;
            }
            return true;
        }

        private bool GlyphId(uint charCode, out uint glyph)
        {
            glyph = 0;
            if (!GetTable("cmap", out uint cmap)) return false;
            if (!Safe(cmap, 4)) return false;
            uint numEntries = GetU16(cmap + 2);
            if (!Safe(cmap, 4 + numEntries * 8)) return false;
            for (uint idx = 0; idx < numEntries; ++idx)
            {
                uint entry = cmap + 4 + idx * 8;
                uint type = GetU16(entry) * 64 + GetU16(entry + 2);   // (octal 0004 = platform 0 encoding 4, 0312 = platform 3 encoding 10)
                if (type == 4 || type == 202)
                {
                    uint table = cmap + GetU32(entry + 4);
                    if (!Safe(table, 8)) return false;
                    if (GetU16(table) == 12) return CmapFmt12(table, charCode, out glyph);
                    return false;
                }
            }
            for (uint idx = 0; idx < numEntries; ++idx)
            {
                uint entry = cmap + 4 + idx * 8;
                uint type = GetU16(entry) * 64 + GetU16(entry + 2);
                if (type == 3 || type == 193)   // octal 0003 and 0301
                {
                    uint table = cmap + GetU32(entry + 4);
                    if (!Safe(table, 6)) return false;
                    switch (GetU16(table))
                    {
                        case 4: return CmapFmt4(table + 6, charCode, out glyph);
                        case 6: return CmapFmt6(table + 6, charCode, out glyph);
                        default: return false;
                    }
                }
            }
            return false;
        }

        // ---- metrics and outlines -----------------------------------------------------------------------------------------------------------------
        private bool HorMetrics(uint glyph, out int advanceWidth, out int leftSideBearing)
        {
            advanceWidth = 0; leftSideBearing = 0;
            if (!GetTable("hmtx", out uint hmtx)) return false;
            if (glyph < _numLongHmtx)
            {
                uint offset = hmtx + 4 * glyph;
                if (!Safe(offset, 4)) return false;
                advanceWidth = (int)GetU16(offset);
                leftSideBearing = GetI16(offset + 2);
                return true;
            }
            uint boundary = hmtx + 4U * _numLongHmtx;
            if (boundary < 4) return false;
            uint o = boundary - 4;
            if (!Safe(o, 4)) return false;
            advanceWidth = (int)GetU16(o);
            o = boundary + 2 * (glyph - _numLongHmtx);
            if (!Safe(o, 2)) return false;
            leftSideBearing = GetI16(o);
            return true;
        }

        private bool GlyphBbox(double size, uint outline, int[] box)
        {
            if (!Safe(outline, 10)) return false;
            box[0] = GetI16(outline + 2); box[1] = GetI16(outline + 4); box[2] = GetI16(outline + 6); box[3] = GetI16(outline + 8);
            if (box[2] <= box[0] || box[3] <= box[1]) return false;
            double xScale = size / _unitsPerEm, yScale = size / _unitsPerEm;
            box[0] = (int)Math.Floor(box[0] * xScale + 0.0);
            box[1] = (int)Math.Floor(box[1] * yScale + 0.0);
            box[2] = (int)Math.Ceiling(box[2] * xScale + 0.0);
            box[3] = (int)Math.Ceiling(box[3] * yScale + 0.0);
            return true;
        }

        private bool OutlineOffset(uint glyph, out uint offset)
        {
            offset = 0;
            if (!GetTable("loca", out uint loca) || !GetTable("glyf", out uint glyf)) return false;
            uint thisOff, next;
            if (_locaFormat == 0)
            {
                uint b = loca + 2 * glyph;
                if (!Safe(b, 4)) return false;
                thisOff = 2U * GetU16(b);
                next = 2U * GetU16(b + 2);
            }
            else
            {
                uint b = loca + 4 * glyph;
                if (!Safe(b, 8)) return false;
                thisOff = GetU32(b);
                next = GetU32(b + 4);
            }
            offset = thisOff == next ? 0 : glyf + thisOff;
            return true;
        }

        private struct Point { public double X, Y; }
        private struct Line { public ushort Beg, End; }
        private struct Curve { public ushort Beg, End, Ctrl; }
        private struct Cell { public double Area, Cover; }

        private sealed class Outline
        {
            public readonly List<Point> Points = new();
            public readonly List<Curve> Curves = new();
            public readonly List<Line> Lines = new();
        }

        private static Point Midpoint(Point a, Point b) => new() { X = 0.5 * (a.X + b.X), Y = 0.5 * (a.Y + b.Y) };
        private static bool GrowOk(Outline o) => o.Points.Count < 0xFFFF && o.Curves.Count < 0xFFFF && o.Lines.Count < 0xFFFF;

        private bool SimpleFlags(ref uint offset, uint numPts, byte[] flags)
        {
            uint off = offset;
            byte value = 0, repeat = 0;
            for (uint i = 0; i < numPts; ++i)
            {
                if (repeat != 0) --repeat;
                else
                {
                    if (!Safe(off, 1)) return false;
                    value = GetU8(off++);
                    if ((value & 0x08) != 0)
                    {
                        if (!Safe(off, 1)) return false;
                        repeat = GetU8(off++);
                    }
                }
                flags[i] = value;
            }
            offset = off;
            return true;
        }

        private bool SimplePoints(uint offset, uint numPts, byte[] flags, Outline outl, int basePoint)
        {
            long accum = 0;
            var xs = new double[numPts];
            for (uint i = 0; i < numPts; ++i)
            {
                if ((flags[i] & 0x02) != 0)
                {
                    if (!Safe(offset, 1)) return false;
                    long value = GetU8(offset++);
                    long bit = (flags[i] & 0x10) != 0 ? 1 : 0;
                    accum -= (value ^ -bit) + bit;
                }
                else if ((flags[i] & 0x10) == 0)
                {
                    if (!Safe(offset, 2)) return false;
                    accum += GetI16(offset);
                    offset += 2;
                }
                xs[i] = accum;
            }
            accum = 0;
            for (uint i = 0; i < numPts; ++i)
            {
                if ((flags[i] & 0x04) != 0)
                {
                    if (!Safe(offset, 1)) return false;
                    long value = GetU8(offset++);
                    long bit = (flags[i] & 0x20) != 0 ? 1 : 0;
                    accum -= (value ^ -bit) + bit;
                }
                else if ((flags[i] & 0x20) == 0)
                {
                    if (!Safe(offset, 2)) return false;
                    accum += GetI16(offset);
                    offset += 2;
                }
                outl.Points[basePoint + (int)i] = new Point { X = xs[i], Y = accum };
            }
            return true;
        }

        private static bool DecodeContour(byte[] flags, int flagBase, uint basePoint, uint count, Outline outl)
        {
            if (count < 2) return true;
            uint looseEnd;
            int fb = flagBase;
            if ((flags[fb] & 0x01) != 0)
            {
                looseEnd = basePoint++;
                ++fb;
                --count;
            }
            else if ((flags[fb + (int)count - 1] & 0x01) != 0)
            {
                looseEnd = basePoint + --count;
            }
            else
            {
                if (!GrowOk(outl)) return false;
                looseEnd = (uint)outl.Points.Count;
                outl.Points.Add(Midpoint(outl.Points[(int)basePoint], outl.Points[(int)(basePoint + count - 1)]));
            }
            uint beg = looseEnd, ctrl = 0;
            bool gotCtrl = false;
            for (uint i = 0; i < count; ++i)
            {
                uint cur = basePoint + i;
                if ((flags[fb + (int)i] & 0x01) != 0)
                {
                    if (!GrowOk(outl)) return false;
                    if (gotCtrl) outl.Curves.Add(new Curve { Beg = (ushort)beg, End = (ushort)cur, Ctrl = (ushort)ctrl });
                    else outl.Lines.Add(new Line { Beg = (ushort)beg, End = (ushort)cur });
                    beg = cur;
                    gotCtrl = false;
                }
                else
                {
                    if (gotCtrl)
                    {
                        if (!GrowOk(outl)) return false;
                        uint center = (uint)outl.Points.Count;
                        outl.Points.Add(Midpoint(outl.Points[(int)ctrl], outl.Points[(int)cur]));
                        outl.Curves.Add(new Curve { Beg = (ushort)beg, End = (ushort)center, Ctrl = (ushort)ctrl });
                        beg = center;
                    }
                    ctrl = cur;
                    gotCtrl = true;
                }
            }
            if (!GrowOk(outl)) return false;
            if (gotCtrl) outl.Curves.Add(new Curve { Beg = (ushort)beg, End = (ushort)looseEnd, Ctrl = (ushort)ctrl });
            else outl.Lines.Add(new Line { Beg = (ushort)beg, End = (ushort)looseEnd });
            return true;
        }

        private bool SimpleOutline(uint offset, uint numContours, Outline outl)
        {
            uint basePoint = (uint)outl.Points.Count;
            if (!Safe(offset, numContours * 2 + 2)) return false;
            uint numPts = GetU16(offset + (numContours - 1) * 2);
            if (numPts >= 0xFFFF) return false;
            numPts++;
            if (outl.Points.Count > 0xFFFF - numPts) return false;
            var endPts = new uint[numContours];
            for (uint i = 0; i < numContours; ++i) { endPts[i] = GetU16(offset); offset += 2; }
            for (uint i = 0; i + 1 < numContours; ++i) if (endPts[i + 1] < endPts[i] + 1) return false;
            offset += 2U + GetU16(offset);
            var flags = new byte[numPts];
            if (!SimpleFlags(ref offset, numPts, flags)) return false;
            for (uint i = 0; i < numPts; i++) outl.Points.Add(default);
            if (!SimplePoints(offset, numPts, flags, outl, (int)basePoint)) return false;
            uint beg = 0;
            for (uint i = 0; i < numContours; ++i)
            {
                if (endPts[i] < beg || endPts[i] >= numPts) return false;
                uint count = endPts[i] - beg + 1;
                if (!DecodeContour(flags, (int)beg, basePoint + beg, count, outl)) return false;
                beg = endPts[i] + 1;
            }
            return true;
        }

        private bool CompoundOutline(uint offset, int recDepth, Outline outl)
        {
            if (recDepth >= 4) return false;
            uint flags;
            do
            {
                var local = new double[6];
                if (!Safe(offset, 4)) return false;
                flags = GetU16(offset);
                uint glyph = GetU16(offset + 2);
                offset += 4;
                if ((flags & 0x002) == 0) return false;
                if ((flags & 0x001) != 0)
                {
                    if (!Safe(offset, 4)) return false;
                    local[4] = GetI16(offset);
                    local[5] = GetI16(offset + 2);
                    offset += 4;
                }
                else
                {
                    if (!Safe(offset, 2)) return false;
                    local[4] = GetI8(offset);
                    local[5] = GetI8(offset + 1);
                    offset += 2;
                }
                if ((flags & 0x008) != 0)
                {
                    if (!Safe(offset, 2)) return false;
                    local[0] = GetI16(offset) / 16384.0;
                    local[3] = local[0];
                    offset += 2;
                }
                else if ((flags & 0x040) != 0)
                {
                    if (!Safe(offset, 4)) return false;
                    local[0] = GetI16(offset + 0) / 16384.0;
                    local[3] = GetI16(offset + 2) / 16384.0;
                    offset += 4;
                }
                else if ((flags & 0x080) != 0)
                {
                    if (!Safe(offset, 8)) return false;
                    local[0] = GetI16(offset + 0) / 16384.0;
                    local[1] = GetI16(offset + 2) / 16384.0;
                    local[2] = GetI16(offset + 4) / 16384.0;
                    local[3] = GetI16(offset + 6) / 16384.0;
                    offset += 8;
                }
                else
                {
                    local[0] = 1.0;
                    local[3] = 1.0;
                }
                if (!OutlineOffset(glyph, out uint outline)) return false;
                if (outline != 0)
                {
                    int basePoint = outl.Points.Count;
                    if (!DecodeOutline(outline, recDepth + 1, outl)) return false;
                    TransformPoints(outl.Points, basePoint, outl.Points.Count - basePoint, local);
                }
            } while ((flags & 0x020) != 0);
            return true;
        }

        private bool DecodeOutline(uint offset, int recDepth, Outline outl)
        {
            if (!Safe(offset, 10)) return false;
            int numContours = GetI16(offset);
            if (numContours > 0) return SimpleOutline(offset + 10, (uint)numContours, outl);
            if (numContours < 0) return CompoundOutline(offset + 10, recDepth, outl);
            return true;
        }

        // ---- rasterising ----------------------------------------------------------------------------------------------------------------------------
        private static void TransformPoints(List<Point> points, int start, int n, double[] trf)
        {
            for (int i = start; i < start + n; ++i)
            {
                Point pt = points[i];
                points[i] = new Point { X = pt.X * trf[0] + pt.Y * trf[2] + trf[4], Y = pt.X * trf[1] + pt.Y * trf[3] + trf[5] };
            }
        }

        private static void ClipPoints(List<Point> points, int width, int height)
        {
            for (int i = 0; i < points.Count; i++)
            {
                Point pt = points[i], p = pt;
                if (pt.X < 0.0) p.X = 0.0;
                if (pt.X >= width) p.X = Math.BitDecrement((double)width);
                if (pt.Y < 0.0) p.Y = 0.0;
                if (pt.Y >= height) p.Y = Math.BitDecrement((double)height);
                points[i] = p;
            }
        }

        private static bool IsFlat(Outline outl, Curve curve)
        {
            const double maxArea2 = 2.0;
            Point a = outl.Points[curve.Beg], b = outl.Points[curve.Ctrl], c = outl.Points[curve.End];
            double gx = b.X - a.X, gy = b.Y - a.Y, hx = c.X - a.X, hy = c.Y - a.Y;
            double area2 = Math.Abs(gx * hy - hx * gy);
            return area2 <= maxArea2;
        }

        private static bool TesselateCurve(Curve curve, Outline outl)
        {
            const int stackSize = 10;
            var stack = new Curve[stackSize];
            int top = 0;
            for (; ; )
            {
                if (IsFlat(outl, curve) || top >= stackSize)
                {
                    if (outl.Lines.Count >= 0xFFFF) return false;
                    outl.Lines.Add(new Line { Beg = curve.Beg, End = curve.End });
                    if (top == 0) break;
                    curve = stack[--top];
                }
                else
                {
                    if (outl.Points.Count + 3 > 0xFFFF) return false;
                    ushort ctrl0 = (ushort)outl.Points.Count;
                    outl.Points.Add(Midpoint(outl.Points[curve.Beg], outl.Points[curve.Ctrl]));
                    ushort ctrl1 = (ushort)outl.Points.Count;
                    outl.Points.Add(Midpoint(outl.Points[curve.Ctrl], outl.Points[curve.End]));
                    ushort pivot = (ushort)outl.Points.Count;
                    outl.Points.Add(Midpoint(outl.Points[ctrl0], outl.Points[ctrl1]));
                    stack[top++] = new Curve { Beg = curve.Beg, End = pivot, Ctrl = ctrl0 };
                    curve = new Curve { Beg = pivot, End = curve.End, Ctrl = ctrl1 };
                }
            }
            return true;
        }

        private static int FastFloor(double x) { int i = (int)x; return i - (i > x ? 1 : 0); }
        private static int FastCeil(double x) { int i = (int)x; return i + (i < x ? 1 : 0); }
        private static int Sign(double x) => (x > 0 ? 1 : 0) - (x < 0 ? 1 : 0);

        private static void DrawLine(Cell[] cells, int width, Point origin, Point goal)
        {
            double deltaX = goal.X - origin.X, deltaY = goal.Y - origin.Y;
            int dirX = Sign(deltaX), dirY = Sign(deltaY);
            if (dirY == 0) return;
            double crossingIncrX = dirX != 0 ? Math.Abs(1.0 / deltaX) : 1.0;
            double crossingIncrY = Math.Abs(1.0 / deltaY);
            double nextCrossingX = 0, nextCrossingY = 0;
            int pixelX, pixelY, numSteps = 0;
            if (dirX == 0)
            {
                pixelX = FastFloor(origin.X);
                nextCrossingX = 100.0;
            }
            else if (dirX > 0)
            {
                pixelX = FastFloor(origin.X);
                nextCrossingX = (origin.X - pixelX) * crossingIncrX;
                nextCrossingX = crossingIncrX - nextCrossingX;
                numSteps += FastCeil(goal.X) - FastFloor(origin.X) - 1;
            }
            else
            {
                pixelX = FastCeil(origin.X) - 1;
                nextCrossingX = (origin.X - pixelX) * crossingIncrX;
                numSteps += FastCeil(origin.X) - FastFloor(goal.X) - 1;
            }
            if (dirY > 0)
            {
                pixelY = FastFloor(origin.Y);
                nextCrossingY = (origin.Y - pixelY) * crossingIncrY;
                nextCrossingY = crossingIncrY - nextCrossingY;
                numSteps += FastCeil(goal.Y) - FastFloor(origin.Y) - 1;
            }
            else
            {
                pixelY = FastCeil(origin.Y) - 1;
                nextCrossingY = (origin.Y - pixelY) * crossingIncrY;
                numSteps += FastCeil(origin.Y) - FastFloor(goal.Y) - 1;
            }
            double nextDistance = Math.Min(nextCrossingX, nextCrossingY);
            double halfDeltaX = 0.5 * deltaX;
            double prevDistance = 0.0;
            for (int step = 0; step < numSteps; ++step)
            {
                double xAverage = origin.X + (prevDistance + nextDistance) * halfDeltaX;
                double yDifference = (nextDistance - prevDistance) * deltaY;
                long at = (long)pixelY * width + pixelX;
                if (at < 0 || at >= cells.Length) return;
                ref Cell cell = ref cells[at];
                cell.Cover += yDifference;
                xAverage -= pixelX;
                cell.Area += (1.0 - xAverage) * yDifference;
                prevDistance = nextDistance;
                bool alongX = nextCrossingX < nextCrossingY;
                pixelX += alongX ? dirX : 0;
                pixelY += alongX ? 0 : dirY;
                nextCrossingX += alongX ? crossingIncrX : 0.0;
                nextCrossingY += alongX ? 0.0 : crossingIncrY;
                nextDistance = Math.Min(nextCrossingX, nextCrossingY);
            }
            {
                double xAverage = origin.X + (prevDistance + 1.0) * halfDeltaX;
                double yDifference = (1.0 - prevDistance) * deltaY;
                long at = (long)pixelY * width + pixelX;
                if (at < 0 || at >= cells.Length) return;
                ref Cell cell = ref cells[at];
                cell.Cover += yDifference;
                xAverage -= pixelX;
                cell.Area += (1.0 - xAverage) * yDifference;
            }
        }

        private static bool RenderOutline(Outline outl, double[] transform, byte[] pixels, int width, int height)
        {
            if (width <= 0 || height <= 0) return true;
            long numPixels = (long)width * height;
            if (numPixels > pixels.Length) return false;
            var cells = new Cell[numPixels];
            TransformPoints(outl.Points, 0, outl.Points.Count, transform);
            ClipPoints(outl.Points, width, height);
            int curveCount = outl.Curves.Count;
            for (int i = 0; i < curveCount; ++i)
                if (!TesselateCurve(outl.Curves[i], outl)) return false;
            foreach (Line line in outl.Lines) DrawLine(cells, width, outl.Points[line.Beg], outl.Points[line.End]);
            double accum = 0.0;
            for (long i = 0; i < numPixels; ++i)
            {
                double value = Math.Abs(accum + cells[i].Area);
                value = Math.Min(value, 1.0);
                value = value * 255.0 + 0.5;
                pixels[i] = (byte)value;
                accum += cells[i].Cover;
            }
            return true;
        }
    }
}
