using System;

namespace fire.Terminal
{
    /// <summary>An image cannot be read (unknown format, damaged, truncated, too large, unsupported variant).</summary>
    public sealed class ImageFormatException : Exception
    {
        public ImageFormatException(string message) : base(message) { }
    }

    /// <summary>
    /// A decoded image, independent of the file format: either INDEXED (one palette index per pixel plus up to 256 palette colours - PNG with
    /// palette, GIF, BMP with at most 8 bit) or TRUECOLOR (one RGBA value per pixel). <see cref="ToFramebuffer"/> turns it into a framebuffer.
    /// </summary>
    public sealed class ImageData
    {
        public int Width { get; }
        public int Height { get; }

        public bool IsIndexed => Indices != null;

        /// <summary>Indexed images: one index per pixel, row by row from top to bottom.</summary>
        public byte[]? Indices { get; }

        /// <summary>Indexed images: the palette (packed R,G,B,A values, see PixelColor); unused entries are opaque black.</summary>
        public uint[]? Palette { get; }

        /// <summary>Indexed images: the index that counts as transparent (GIF transparency, PNG palette with alpha 0), or -1.</summary>
        public int TransparentIndex { get; }

        /// <summary>Truecolor images: one packed R,G,B,A value per pixel, row by row from top to bottom.</summary>
        public uint[]? Pixels { get; }

        /// <summary>Does a truecolor image have transparent or semi-transparent pixels?</summary>
        public bool HasAlpha { get; }

        /// <summary>The file format the image comes from ("PNG", "BMP", "GIF"), or "raw data".</summary>
        public string Format { get; }

        private ImageData(int width, int height, byte[]? indices, uint[]? palette, int transparentIndex, uint[]? pixels, bool hasAlpha, string format)
        {
            Width = width;
            Height = height;
            Indices = indices;
            Palette = palette;
            TransparentIndex = transparentIndex;
            Pixels = pixels;
            HasAlpha = hasAlpha;
            Format = format;
        }

        public static ImageData CreateIndexed(int width, int height, byte[] indices, uint[] palette, int transparentIndex, string format)
        {
            var full = new uint[256];
            Array.Fill(full, 0xFF000000u);
            Array.Copy(palette, full, Math.Min(palette.Length, 256));
            return new ImageData(width, height, indices, full, transparentIndex, null, false, format);
        }

        public static ImageData CreateTruecolor(int width, int height, uint[] pixels, string format)
        {
            bool alpha = false;
            foreach (uint p in pixels)
                if ((p >> 24) != 0xFF) { alpha = true; break; }
            return new ImageData(width, height, null, null, -1, pixels, alpha, format);
        }

        /// <summary>Builds a framebuffer from the image. `mode` null = like the image (indexed -> palette framebuffer with the palette of the file,
        /// truecolor -> RGBA). Forced: an indexed image into an RGBA framebuffer is resolved to colours via its palette; a
        /// truecolor image into a palette framebuffer is mapped to the (default) palette (per pixel the nearest entry, alpha does not count).</summary>
        public Framebuffer ToFramebuffer(ColorMode? mode = null)
        {
            var target = mode ?? (IsIndexed ? ColorMode.Indexed : ColorMode.Rgba);
            var fb = new Framebuffer(Width, Height, target);

            if (target == ColorMode.Rgba)
            {
                if (Pixels != null) Array.Copy(Pixels, fb.Pixels, Pixels.Length);
                else
                    for (int i = 0; i < Indices!.Length; i++)
                        fb.Pixels[i] = Palette![Indices[i]];
                return fb;
            }

            if (IsIndexed)
            {
                Array.Copy(Indices!, fb.Indices!, Indices!.Length);
                fb.Palette.SetAll(Palette!);
                fb.TransparentIndex = TransparentIndex;
                fb.MarkDirty();
                return fb;
            }

            var cache = new System.Collections.Generic.Dictionary<uint, byte>();
            for (int i = 0; i < Pixels!.Length; i++)
            {
                uint rgb = Pixels[i] & 0x00FFFFFF;
                if (!cache.TryGetValue(rgb, out byte index))
                    cache[rgb] = index = fb.Palette.FindNearest(new PixelColor(rgb | 0xFF000000u));
                fb.Indices![i] = index;
            }
            fb.MarkDirty();
            return fb;
        }

        /// <summary>Maximum number of pixels of an image (protection against absurd size specifications in damaged files: 4 bytes per pixel).</summary>
        public const long MaxPixels = 64L * 1024 * 1024;

        /// <summary>Checks width and height for a sensible range.</summary>
        public static void CheckSize(string format, long width, long height)
        {
            if (width <= 0 || height <= 0)
                throw new ImageFormatException($"{format}: invalid image size {width}x{height}.");
            if (width * height > MaxPixels)
                throw new ImageFormatException($"{format}: image too large ({width}x{height}; at most {MaxPixels / (1024 * 1024)} million pixels).");
        }
    }
}
