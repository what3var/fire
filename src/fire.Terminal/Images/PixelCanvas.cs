using System;
using System.Collections.Generic;

namespace fire.Terminal
{
    /// <summary>
    /// The picture the pixel editor of the editor works on (logic only, no user interface): either INDEXED - one palette index per pixel and a palette of 256 colours, by default the palette of
    /// fire with entry 0 transparent - or TRUECOLOR (one RGBA value per pixel; colours are packed like <see cref="PixelColor"/>: R in the lowest byte, A in the highest). A drawing function takes
    /// a *value*: the palette index for an indexed picture, the packed colour for a truecolor one. Changes are grouped for undo: <see cref="BeginEdit"/> before a stroke, <see cref="EndEdit"/> after it.
    /// </summary>
    public sealed class PixelCanvas
    {
        private sealed class Snapshot
        {
            public int Width, Height;
            public bool Indexed;
            public byte[]? Indices;
            public uint[]? Pixels;
            public uint[] Palette = Array.Empty<uint>();
            public int TransparentIndex;
        }

        private byte[]? _indices;
        private uint[]? _pixels;
        private readonly uint[] _palette = new uint[256];
        private readonly List<Snapshot> _undo = new();
        private readonly List<Snapshot> _redo = new();
        private Snapshot? _stroke;
        private const int UndoLimit = 100;

        public int Width { get; private set; }
        public int Height { get; private set; }
        public bool IsIndexed { get; private set; }
        /// <summary>The palette entry that is transparent (-1: none); an indexed picture made here has entry 0.</summary>
        public int TransparentIndex { get; private set; }
        public bool IsModified { get; private set; }
        public event Action? Changed;

        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;

        /// <summary>The 256 colours of the palette (a truecolor picture keeps one as a source of colours).</summary>
        public IReadOnlyList<uint> Palette => _palette;

        /// <summary>The palette of fire with entry 0 transparent.</summary>
        public static uint[] DefaultPalette()
        {
            var p = new uint[256];
            new Palette().CopyPacked(p);
            for (int i = 0; i < 256; i++) p[i] |= 0xFF000000u;
            p[0] = 0;
            return p;
        }

        private PixelCanvas() { }

        public static PixelCanvas Create(int width, int height, bool indexed)
        {
            if (width < 1 || height < 1 || (long)width * height > ImageData.MaxPixels) throw new ArgumentOutOfRangeException(nameof(width), "The size of a picture is 1 x 1 to 64 million pixels.");
            var c = new PixelCanvas { Width = width, Height = height, IsIndexed = indexed, TransparentIndex = indexed ? 0 : -1 };
            Array.Copy(DefaultPalette(), c._palette, 256);
            if (indexed) c._indices = new byte[width * height];   // everything is entry 0: transparent
            else c._pixels = new uint[width * height];             // everything is 0: transparent
            return c;
        }

        public static PixelCanvas FromImage(ImageData image)
        {
            var c = new PixelCanvas { Width = image.Width, Height = image.Height, IsIndexed = image.IsIndexed, TransparentIndex = image.IsIndexed ? image.TransparentIndex : -1 };
            if (image.IsIndexed)
            {
                c._indices = (byte[])image.Indices!.Clone();
                Array.Copy(image.Palette!, c._palette, 256);
            }
            else
            {
                c._pixels = (uint[])image.Pixels!.Clone();
                Array.Copy(DefaultPalette(), c._palette, 256);
            }
            return c;
        }

        public ImageData ToImage() => IsIndexed
            ? ImageData.CreateIndexed(Width, Height, (byte[])_indices!.Clone(), (uint[])_palette.Clone(), TransparentIndex, "PNG")
            : ImageData.CreateTruecolor(Width, Height, (uint[])_pixels!.Clone(), "PNG");

        public bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

        /// <summary>The value at a pixel: the palette index, or the packed colour.</summary>
        public uint ValueAt(int x, int y) => IsIndexed ? _indices![y * Width + x] : _pixels![y * Width + x];

        /// <summary>The colour at a pixel as it is seen (packed, R lowest): the transparent entry has alpha 0.</summary>
        public uint ColorAt(int x, int y)
        {
            if (!IsIndexed) return _pixels![y * Width + x];
            int index = _indices![y * Width + x];
            return index == TransparentIndex ? _palette[index] & 0x00FFFFFFu : _palette[index];
        }

        /// <summary>The colour of a palette entry as it is seen (the transparent entry has alpha 0).</summary>
        public uint EntryColor(int index) => index == TransparentIndex ? _palette[index] & 0x00FFFFFFu : _palette[index];

        /// <summary>All pixels as packed colours (for drawing the picture).</summary>
        public void CopyColors(Span<uint> destination)
        {
            for (int i = 0; i < Width * Height; i++)
                destination[i] = IsIndexed ? EntryColor(_indices![i]) : _pixels![i];
        }

        // ---- undo -----------------------------------------------------------------------------------------------------------------------

        private Snapshot Take() => new()
        {
            Width = Width, Height = Height, Indexed = IsIndexed, Indices = (byte[]?)_indices?.Clone(), Pixels = (uint[]?)_pixels?.Clone(),
            Palette = (uint[])_palette.Clone(), TransparentIndex = TransparentIndex,
        };

        private void Restore(Snapshot s)
        {
            Width = s.Width; Height = s.Height; IsIndexed = s.Indexed; TransparentIndex = s.TransparentIndex;
            _indices = (byte[]?)s.Indices?.Clone(); _pixels = (uint[]?)s.Pixels?.Clone();
            Array.Copy(s.Palette, _palette, 256);
        }

        private void Push(Snapshot s)
        {
            _undo.Add(s);
            if (_undo.Count > UndoLimit) _undo.RemoveAt(0);
            _redo.Clear();
        }

        /// <summary>Starts a change that is undone as a whole (a stroke of the pencil, a rectangle).</summary>
        public void BeginEdit()
        {
            if (_stroke != null) return;
            _stroke = Take();
        }

        /// <summary>Ends the change; when nothing changed, nothing is kept for undo.</summary>
        public void EndEdit()
        {
            if (_stroke == null) return;
            var start = _stroke;
            _stroke = null;
            if (Same(start)) return;
            Push(start);
            IsModified = true;
            Changed?.Invoke();
        }

        /// <summary>Puts the picture back to what it was at <see cref="BeginEdit"/> (to draw a line or a rectangle again while the mouse moves).</summary>
        public void RestoreStroke()
        {
            if (_stroke == null) return;
            Restore(_stroke);
        }

        private bool Same(Snapshot s)
        {
            if (s.Width != Width || s.Height != Height || s.Indexed != IsIndexed || s.TransparentIndex != TransparentIndex) return false;
            if (!s.Palette.AsSpan().SequenceEqual(_palette)) return false;
            return IsIndexed ? s.Indices.AsSpan().SequenceEqual(_indices) : s.Pixels.AsSpan().SequenceEqual(_pixels);
        }

        public void Undo()
        {
            if (_undo.Count == 0) return;
            _redo.Add(Take());
            Restore(_undo[^1]);
            _undo.RemoveAt(_undo.Count - 1);
            IsModified = true;
            Changed?.Invoke();
        }

        public void Redo()
        {
            if (_redo.Count == 0) return;
            _undo.Add(Take());
            Restore(_redo[^1]);
            _redo.RemoveAt(_redo.Count - 1);
            IsModified = true;
            Changed?.Invoke();
        }

        public void MarkSaved() { IsModified = false; }

        // ---- drawing (inside a BeginEdit/EndEdit pair; nothing is announced until EndEdit) ---------------------------------------------------

        public void Set(int x, int y, uint value)
        {
            if (!InBounds(x, y)) return;
            if (IsIndexed) _indices![y * Width + x] = (byte)value; else _pixels![y * Width + x] = value;
        }

        /// <summary>A line (Bresenham) from one pixel to another, both ends included.</summary>
        public void Line(int x0, int y0, int x1, int y1, uint value)
        {
            int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
            int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
            int err = dx + dy;
            while (true)
            {
                Set(x0, y0, value);
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }
        }

        /// <summary>A rectangle between two corners (any order), only its border or filled.</summary>
        public void Rectangle(int x0, int y0, int x1, int y1, uint value, bool filled)
        {
            int left = Math.Min(x0, x1), right = Math.Max(x0, x1), top = Math.Min(y0, y1), bottom = Math.Max(y0, y1);
            for (int y = Math.Max(top, 0); y <= Math.Min(bottom, Height - 1); y++)
                for (int x = Math.Max(left, 0); x <= Math.Min(right, Width - 1); x++)
                    if (filled || y == top || y == bottom || x == left || x == right) Set(x, y, value);
        }

        /// <summary>Fills the area of equal value around a pixel (4-connected).</summary>
        public void Fill(int x, int y, uint value)
        {
            if (!InBounds(x, y)) return;
            uint target = ValueAt(x, y);
            if (target == value) return;
            var stack = new Stack<(int, int)>();
            stack.Push((x, y));
            while (stack.Count > 0)
            {
                var (cx, cy) = stack.Pop();
                if (!InBounds(cx, cy) || ValueAt(cx, cy) != target) continue;
                // run along the row, then look above and below
                int left = cx, right = cx;
                while (left > 0 && ValueAt(left - 1, cy) == target) left--;
                while (right < Width - 1 && ValueAt(right + 1, cy) == target) right++;
                for (int i = left; i <= right; i++) Set(i, cy, value);
                for (int i = left; i <= right; i++)
                {
                    if (cy > 0 && ValueAt(i, cy - 1) == target) stack.Push((i, cy - 1));
                    if (cy < Height - 1 && ValueAt(i, cy + 1) == target) stack.Push((i, cy + 1));
                }
            }
        }

        // ---- changes of the whole picture (each is one step of undo) -------------------------------------------------------------------------

        private void Whole(Action change)
        {
            Push(Take());
            change();
            IsModified = true;
            Changed?.Invoke();
        }

        /// <summary>Sets a palette entry (any colour, with alpha); an indexed picture shows it at once.</summary>
        public void SetPaletteColor(int index, uint color) => Whole(() => _palette[index] = color);

        /// <summary>Changes the size: the content stays at the top left, new area is transparent (a truecolor picture: 0; an indexed one: the transparent entry, or 0).</summary>
        public void Resize(int width, int height)
        {
            if (width < 1 || height < 1 || (long)width * height > ImageData.MaxPixels) throw new ArgumentOutOfRangeException(nameof(width), "The size of a picture is 1 x 1 to 64 million pixels.");
            if (width == Width && height == Height) return;
            Whole(() =>
            {
                byte fill = (byte)Math.Max(TransparentIndex, 0);
                var indices = IsIndexed ? new byte[width * height] : null;
                var pixels = IsIndexed ? null : new uint[width * height];
                if (indices != null && fill != 0) Array.Fill(indices, fill);
                for (int y = 0; y < Math.Min(height, Height); y++)
                    for (int x = 0; x < Math.Min(width, Width); x++)
                    {
                        if (IsIndexed) indices![y * width + x] = _indices![y * Width + x];
                        else pixels![y * width + x] = _pixels![y * Width + x];
                    }
                Width = width; Height = height; _indices = indices; _pixels = pixels;
            });
        }

        /// <summary>Makes an indexed picture from a truecolor one: each pixel gets the nearest entry of the palette (transparent pixels get the transparent entry 0).</summary>
        public void ConvertToIndexed()
        {
            if (IsIndexed) return;
            Whole(() =>
            {
                Array.Copy(DefaultPalette(), _palette, 256);
                var indices = new byte[Width * Height];
                var cache = new Dictionary<uint, byte>();
                var palette = new Palette();
                for (int i = 0; i < indices.Length; i++)
                {
                    uint p = _pixels![i];
                    if ((p >> 24) < 128) { indices[i] = 0; continue; }
                    uint rgb = (p & 0x00FFFFFF) | 0xFF000000u;
                    if (!cache.TryGetValue(rgb, out byte index))
                    {
                        index = NearestOpaque(rgb);
                        cache[rgb] = index;
                    }
                    indices[i] = index;
                }
                _indices = indices; _pixels = null; IsIndexed = true; TransparentIndex = 0;
            });
        }

        private byte NearestOpaque(uint rgb)
        {
            int r = (int)(rgb & 255), g = (int)((rgb >> 8) & 255), b = (int)((rgb >> 16) & 255);
            long best = long.MaxValue; byte bestIndex = 1;
            for (int i = 1; i < 256; i++)   // entry 0 is transparent: never a colour for a pixel
            {
                uint q = _palette[i];
                long dr = (int)(q & 255) - r, dg = (int)((q >> 8) & 255) - g, db = (int)((q >> 16) & 255) - b;
                long d = dr * dr + dg * dg + db * db;
                if (d < best) { best = d; bestIndex = (byte)i; }
            }
            return bestIndex;
        }

        /// <summary>Makes a truecolor picture from an indexed one (the colours of the palette are copied into the pixels).</summary>
        public void ConvertToTruecolor()
        {
            if (!IsIndexed) return;
            Whole(() =>
            {
                var pixels = new uint[Width * Height];
                for (int i = 0; i < pixels.Length; i++) pixels[i] = EntryColor(_indices![i]);
                _pixels = pixels; _indices = null; IsIndexed = false; TransparentIndex = -1;
            });
        }
    }
}
