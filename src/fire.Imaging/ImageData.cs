using System;

namespace fire.Imaging
{
    /// <summary>An image cannot be read (unknown format, damaged, truncated, too large, unsupported variant).</summary>
    public sealed class ImageFormatException : Exception
    {
        public ImageFormatException(string message) : base(message) { }
    }

    /// <summary>
    /// A decoded image, independent of the file format: either INDEXED (one palette index per pixel plus up to 256 palette colours - PNG with
    /// palette, GIF, BMP with at most 8 bit) or TRUECOLOR (one RGBA value per pixel).
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
