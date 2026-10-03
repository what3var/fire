using System;

namespace fire.Terminal
{
    /// <summary>Ein Bild kann nicht gelesen werden (unbekanntes Format, beschädigt, abgeschnitten, zu groß, nicht unterstützte Variante).</summary>
    public sealed class ImageFormatException : Exception
    {
        public ImageFormatException(string message) : base(message) { }
    }

    /// <summary>
    /// Ein dekodiertes Bild, unabhängig vom Dateiformat: entweder INDIZIERT (ein Palette-Index je Pixel plus bis zu 256 Palette-Farben - PNG mit
    /// Palette, GIF, BMP mit höchstens 8 Bit) oder TRUECOLOR (ein RGBA-Wert je Pixel). <see cref="ToFramebuffer"/> macht daraus einen Framebuffer.
    /// </summary>
    public sealed class ImageData
    {
        public int Width { get; }
        public int Height { get; }

        public bool IsIndexed => Indices != null;

        /// <summary>Indizierte Bilder: ein Index je Pixel, zeilenweise von oben nach unten.</summary>
        public byte[]? Indices { get; }

        /// <summary>Indizierte Bilder: die Palette (gepackte R,G,B,A-Werte, siehe PixelColor); nicht belegte Einträge sind deckendes Schwarz.</summary>
        public uint[]? Palette { get; }

        /// <summary>Indizierte Bilder: der Index, der als durchsichtig gilt (GIF-Transparenz, PNG-Palette mit Alpha 0), oder -1.</summary>
        public int TransparentIndex { get; }

        /// <summary>Truecolor-Bilder: ein gepackter R,G,B,A-Wert je Pixel, zeilenweise von oben nach unten.</summary>
        public uint[]? Pixels { get; }

        /// <summary>Hat ein Truecolor-Bild durchsichtige oder halbdurchsichtige Pixel?</summary>
        public bool HasAlpha { get; }

        /// <summary>Das Dateiformat, aus dem das Bild stammt ("PNG", "BMP", "GIF"), oder "Rohdaten".</summary>
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

        /// <summary>Baut einen Framebuffer aus dem Bild. `mode` null = wie das Bild (indiziert -> Palette-Framebuffer mit der Palette der Datei,
        /// Truecolor -> RGBA). Erzwungen: ein indiziertes Bild in einen RGBA-Framebuffer wird über seine Palette in Farben aufgelöst; ein
        /// Truecolor-Bild in einen Palette-Framebuffer auf die (Standard-)Palette abgebildet (je Pixel der nächste Eintrag, Alpha zählt nicht).</summary>
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

        /// <summary>Höchstzahl Pixel eines Bildes (Schutz vor absurden Größenangaben in beschädigten Dateien: 4 Byte je Pixel).</summary>
        public const long MaxPixels = 64L * 1024 * 1024;

        /// <summary>Prüft Breite und Höhe auf einen sinnvollen Bereich.</summary>
        public static void CheckSize(string format, long width, long height)
        {
            if (width <= 0 || height <= 0)
                throw new ImageFormatException($"{format}: ungültige Bildgröße {width}x{height}.");
            if (width * height > MaxPixels)
                throw new ImageFormatException($"{format}: Bild zu groß ({width}x{height}; höchstens {MaxPixels / (1024 * 1024)} Millionen Pixel).");
        }
    }
}
