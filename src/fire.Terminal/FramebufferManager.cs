using System;
using System.Collections.Generic;

namespace fire.Terminal
{
    /// <summary>
    /// Verwaltet Framebuffer-Instanzen über aufsteigende, eindeutige IDs
    /// (siehe IdManager) - der Einstiegspunkt für ein rein funktionales,
    /// ID-basiertes API (z.B. für eine spätere Skriptsprachen-Anbindung):
    /// jede Methode nimmt/liefert nur Ints/Bytes, nie eine Objektreferenz.
    /// Für C#-seitige Weiterverwendung (ConsoleManager, WindowManager) gibt
    /// es zusätzlich <see cref="GetFramebuffer"/>, das die echte Instanz
    /// liefert.
    /// </summary>
    public sealed class FramebufferManager
    {
        private readonly IdManager<Framebuffer> _framebuffers = new();

        public int CreateFramebuffer(int width, int height, ColorMode mode = ColorMode.Rgba) =>
            _framebuffers.Create(new Framebuffer(width, height, mode));

        /// <summary>Nimmt einen fertig aufgebauten Framebuffer entgegen (z.B. aus einer Bilddatei, siehe ImageDecoder) und liefert seine ID.</summary>
        public int AddFramebuffer(Framebuffer framebuffer) => _framebuffers.Create(framebuffer);

        public ColorMode GetMode(int id) => _framebuffers.Get(id).Mode;

        // -----------------------------------------------------------
        // Bilder laden (siehe ImageDecoder): als Dateiinhalt oder als rohe Pixel
        // -----------------------------------------------------------

        /// <summary>Dekodiert eine Bilddatei aus Bytes (PNG, BMP, GIF) in einen NEUEN Framebuffer und liefert seine ID. `mode` null = wie das Bild
        /// (indiziert -> Palette-Framebuffer, Truecolor -> RGBA), sonst erzwungen (siehe ImageData.ToFramebuffer). Wirft ImageFormatException.</summary>
        public int LoadImage(byte[] data, ColorMode? mode = null) =>
            _framebuffers.Create(ImageDecoder.Decode(data).ToFramebuffer(mode));

        /// <summary>Ein neuer Framebuffer aus rohen Pixeln: RGBA-Modus `width*height*4` Byte (R, G, B, A je Pixel), Palette-Modus `width*height` Byte
        /// (ein Index je Pixel) und optional eine Palette (768 Byte RGB oder 1024 Byte RGBA, wie WritePalette). Zeilenweise von oben nach unten.</summary>
        public int CreateFromPixels(int width, int height, byte[] pixels, ColorMode mode, byte[]? palette = null)
        {
            var fb = new Framebuffer(width, height, mode);
            int expected = fb.Indices != null ? fb.Indices.Length : fb.Pixels.Length * 4;
            if (pixels.Length != expected)
                throw new ArgumentException($"Erwarte genau {expected} Byte ({width}x{height}, {(mode == ColorMode.Indexed ? "1 Byte" : "4 Byte")} je Pixel), erhalten {pixels.Length}.", nameof(pixels));
            if (fb.Indices != null) Buffer.BlockCopy(pixels, 0, fb.Indices, 0, pixels.Length);
            else Buffer.BlockCopy(pixels, 0, fb.Pixels, 0, pixels.Length);
            fb.MarkDirty();
            int id = _framebuffers.Create(fb);
            if (palette != null)
            {
                try { WritePalette(id, palette); }
                catch { _framebuffers.Destroy(id); throw; }
            }
            return id;
        }

        /// <summary>Ein neuer Palette-Framebuffer mit der Maske des Bildes `id` (siehe Framebuffer.ToMask); liefert seine ID.</summary>
        public int CreateMask(int id, int threshold, bool darkIsRemoved, int alphaThreshold) =>
            _framebuffers.Create(_framebuffers.Get(id).ToMask((byte)Math.Clamp(threshold, 0, 255), darkIsRemoved, (byte)Math.Clamp(alphaThreshold, 0, 255)));

        /// <summary>Zerlegt die Maske `id` in Werkzeugbahnen (siehe ImageSlicer).</summary>
        public List<ToolPath> Slice(int id, double lineWidth, double pixelSize, double overlap, FillStrategy strategy, double simplifyTolerance, bool flipY) =>
            new ImageSlicer(lineWidth, pixelSize) { Overlap = overlap, Strategy = strategy, SimplifyTolerance = simplifyTolerance, FlipY = flipY }.Slice(_framebuffers.Get(id));

        public int GetTransparentIndex(int id) => _framebuffers.Get(id).TransparentIndex;
        public void SetTransparentIndex(int id, int index) => _framebuffers.Get(id).TransparentIndex = Math.Clamp(index, -1, 255);

        public bool DestroyFramebuffer(int id) => _framebuffers.Destroy(id);

        /// <summary>Für C#-seitige Weiterverwendung (z.B. ConsoleManager/
        /// WindowManager, die eine echte Framebuffer-Instanz brauchen) -
        /// kein Teil des rein-ID-basierten Oberflächen-APIs.</summary>
        public Framebuffer GetFramebuffer(int id) => _framebuffers.Get(id);

        public int GetWidth(int id) => _framebuffers.Get(id).Width;
        public int GetHeight(int id) => _framebuffers.Get(id).Height;

        // -----------------------------------------------------------
        // Byteweiser Zugriff auf die Framebuffer-Rohdaten (lesend/
        // schreibend, siehe CONSOLE.md) - im RGBA-Modus: Byte-Offset 0 = R des
        // ersten Pixels, 1 = G, 2 = B, 3 = A, 4 = R des zweiten Pixels usw. (feste
        // Reihenfolge, siehe PixelColor-Doku - UNABHÄNGIG von der
        // Host-Endianness, da hier bewusst manuell pro Kanal geschoben/
        // maskiert wird statt sich auf eine rohe Speicher-Reinterpretation
        // zu verlassen). Das ist der "immer korrekte" Basisweg. Im Palette-
        // Modus ist ein Byte ein Pixel: der Palette-Index (Offset = y * Breite + x).
        // -----------------------------------------------------------

        public byte ReadByte(int id, int byteOffset)
        {
            var fb = _framebuffers.Get(id);
            CheckByteOffset(fb, byteOffset);
            if (fb.Indices != null) return fb.Indices[byteOffset];
            uint packed = fb.Pixels[byteOffset / 4];
            int channel = byteOffset % 4;
            return (byte)(packed >> (channel * 8));
        }

        public void WriteByte(int id, int byteOffset, byte value)
        {
            var fb = _framebuffers.Get(id);
            CheckByteOffset(fb, byteOffset);
            if (fb.Indices != null)
            {
                fb.Indices[byteOffset] = value;
                fb.MarkDirty();
                return;
            }
            int pixelIndex = byteOffset / 4;
            int channel = byteOffset % 4;
            int shift = channel * 8;
            uint mask = ~((uint)0xFF << shift);
            fb.Pixels[pixelIndex] = (fb.Pixels[pixelIndex] & mask) | ((uint)value << shift);
        }

        /// <summary>Größe der Rohdaten in Byte: 4 je Pixel (RGBA) bzw. 1 je Pixel (Palette).</summary>
        public int GetByteCount(int id) => ByteCount(_framebuffers.Get(id));

        private static int ByteCount(Framebuffer fb) => fb.Indices != null ? fb.Indices.Length : fb.Pixels.Length * 4;

        private static void CheckByteOffset(Framebuffer fb, int byteOffset)
        {
            int totalBytes = ByteCount(fb);
            if (byteOffset < 0 || byteOffset >= totalBytes)
                throw new ArgumentOutOfRangeException(
                    nameof(byteOffset), $"Byte-Offset {byteOffset} außerhalb des Puffers (Größe {totalBytes} Byte).");
        }

        // -----------------------------------------------------------
        // Blockweiser Zugriff - NICHT zwingend, aber aus Convenience-
        // Gründen (siehe CONSOLE.md): schneller als Byte-für-Byte, wenn der
        // GESAMTE Inhalt auf einmal gebraucht wird. Nutzt Buffer.BlockCopy
        // (rohe Speicherkopie) - das gibt exakt die R,G,B,A-Byte-Reihenfolge
        // wieder, WEIL .NET auf allen realistischen Zielplattformen (x86/
        // x64/ARM im Normalbetrieb) little-endian ist UND PixelColors Byte-
        // Layout genau dafür ausgelegt ist (siehe PixelColor-Doku) - anders
        // als ReadByte/WriteByte oben verlässt sich das hier also bewusst
        // auf die Host-Endianness, für den Geschwindigkeitsgewinn eines
        // rohen Speicher-Kopierens statt einer Schleife mit Bit-Operationen.
        // -----------------------------------------------------------

        public byte[] ReadBytes(int id)
        {
            var fb = _framebuffers.Get(id);
            var bytes = new byte[ByteCount(fb)];
            if (fb.Indices != null) Buffer.BlockCopy(fb.Indices, 0, bytes, 0, bytes.Length);
            else Buffer.BlockCopy(fb.Pixels, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        public void WriteBytes(int id, byte[] data)
        {
            var fb = _framebuffers.Get(id);
            int expected = ByteCount(fb);
            if (data.Length != expected)
                throw new ArgumentException($"Erwarte genau {expected} Byte, erhalten {data.Length}.", nameof(data));
            if (fb.Indices != null)
            {
                Buffer.BlockCopy(data, 0, fb.Indices, 0, data.Length);
                fb.MarkDirty();
            }
            else Buffer.BlockCopy(data, 0, fb.Pixels, 0, data.Length);
        }

        // -----------------------------------------------------------
        // Palette (jeder Framebuffer hat eine, siehe Framebuffer.Palette)
        // -----------------------------------------------------------

        public int GetPaletteColor(int id, int index)
        {
            CheckPaletteIndex(index);
            return _framebuffers.Get(id).Palette.GetColor((byte)index);
        }

        public void SetPaletteColor(int id, int index, int color)
        {
            CheckPaletteIndex(index);
            _framebuffers.Get(id).Palette.SetColor((byte)index, color);
        }

        /// <summary>Die Palette als Bytes: 768 (R, G, B je Eintrag) oder mit `withAlpha` 1024 (R, G, B, A).</summary>
        public byte[] ReadPalette(int id, bool withAlpha = false)
        {
            var palette = _framebuffers.Get(id).Palette;
            int stride = withAlpha ? 4 : 3;
            var bytes = new byte[256 * stride];
            for (int i = 0; i < 256; i++)
            {
                var c = palette.GetColor((byte)i);
                bytes[i * stride] = c.R;
                bytes[i * stride + 1] = c.G;
                bytes[i * stride + 2] = c.B;
                if (withAlpha) bytes[i * stride + 3] = c.A;
            }
            return bytes;
        }

        /// <summary>Setzt die ganze Palette aus Bytes: 768 (R, G, B; Alpha 255) oder 1024 (R, G, B, A).</summary>
        public void WritePalette(int id, byte[] data)
        {
            if (data.Length != 768 && data.Length != 1024)
                throw new ArgumentException($"Eine Palette hat 768 (RGB) oder 1024 (RGBA) Byte, erhalten {data.Length}.", nameof(data));
            int stride = data.Length == 768 ? 3 : 4;
            var colors = new uint[256];
            for (int i = 0; i < 256; i++)
                colors[i] = new PixelColor(data[i * stride], data[i * stride + 1], data[i * stride + 2], stride == 4 ? data[i * stride + 3] : (byte)255).Packed;
            _framebuffers.Get(id).Palette.SetAll(colors);
        }

        private static void CheckPaletteIndex(int index)
        {
            if ((uint)index > 255) throw new ArgumentOutOfRangeException(nameof(index), $"Palette-Index {index} außerhalb von 0-255.");
        }
    }
}
