using System;
using System.Collections.Generic;

namespace fire.Terminal
{
    /// <summary>
    /// Manages framebuffer instances via ascending, unique IDs
    /// (see IdManager) - the entry point for a purely functional,
    /// ID-based API (e.g. for a later scripting-language binding):
    /// every method only takes/returns ints/bytes, never an object reference.
    /// For continued use on the C# side (RendererManager, WindowManager) there is
    /// additionally <see cref="GetFramebuffer"/>, which returns the real
    /// instance.
    /// </summary>
    public sealed class FramebufferManager
    {
        private readonly IdManager<Framebuffer> _framebuffers = new();

        public int CreateFramebuffer(int width, int height, ColorMode mode = ColorMode.Rgba) =>
            _framebuffers.Create(new Framebuffer(width, height, mode));

        /// <summary>Accepts a fully built framebuffer (e.g. from an image file, see ImageDecoder) and returns its ID.</summary>
        public int AddFramebuffer(Framebuffer framebuffer) => _framebuffers.Create(framebuffer);

        public ColorMode GetMode(int id) => _framebuffers.Get(id).Mode;

        /// <summary>Brings the framebuffer to a new size (see Framebuffer.Resize); false for an invalid size (it then stays as it was).</summary>
        public bool Resize(int id, int width, int height) => _framebuffers.Get(id).Resize(width, height);

        // -----------------------------------------------------------
        // Loading images (see ImageDecoder): as file content or as raw pixels
        // -----------------------------------------------------------

        /// <summary>Decodes an image file from bytes (PNG, BMP, GIF) into a NEW framebuffer and returns its ID. `mode` null = like the image
        /// (indexed -> palette framebuffer, true colour -> RGBA), otherwise forced (see ImageData.ToFramebuffer). Throws ImageFormatException.</summary>
        public int LoadImage(byte[] data, ColorMode? mode = null) =>
            _framebuffers.Create(ImageDecoder.Decode(data).ToFramebuffer(mode));

        /// <summary>A new framebuffer from raw pixels: RGBA mode `width*height*4` bytes (R, G, B, A per pixel), palette mode `width*height` bytes
        /// (one index per pixel) and optionally a palette (768 bytes RGB or 1024 bytes RGBA, like WritePalette). Row by row from top to bottom.</summary>
        public int CreateFromPixels(int width, int height, byte[] pixels, ColorMode mode, byte[]? palette = null)
        {
            var fb = new Framebuffer(width, height, mode);
            int expected = fb.Indices != null ? fb.Indices.Length : fb.Pixels.Length * 4;
            if (pixels.Length != expected)
                throw new ArgumentException($"Expected exactly {expected} bytes ({width}x{height}, {(mode == ColorMode.Indexed ? "1 byte" : "4 bytes")} per pixel), got {pixels.Length}.", nameof(pixels));
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

        /// <summary>A new palette framebuffer with the mask of the image `id` (see Framebuffer.ToMask); returns its ID.</summary>
        public int CreateMask(int id, int threshold, bool darkIsRemoved, int alphaThreshold) =>
            _framebuffers.Create(_framebuffers.Get(id).ToMask((byte)Math.Clamp(threshold, 0, 255), darkIsRemoved, (byte)Math.Clamp(alphaThreshold, 0, 255)));

        /// <summary>Zerlegt die Maske `id` in Werkzeugbahnen (siehe ImageSlicer).</summary>
        public List<ToolPath> Slice(int id, double lineWidth, double pixelSize, double overlap, FillStrategy strategy, double simplifyTolerance, bool flipY) =>
            new ImageSlicer(lineWidth, pixelSize) { Overlap = overlap, Strategy = strategy, SimplifyTolerance = simplifyTolerance, FlipY = flipY }.Slice(_framebuffers.Get(id));

        public int GetTransparentIndex(int id) => _framebuffers.Get(id).TransparentIndex;
        public void SetTransparentIndex(int id, int index) => _framebuffers.Get(id).TransparentIndex = Math.Clamp(index, -1, 255);

        public bool DestroyFramebuffer(int id) => _framebuffers.Destroy(id);

        /// <summary>For continued use on the C# side (e.g. RendererManager/
        /// WindowManager, which need a real framebuffer instance) -
        /// not part of the purely ID-based surface API.</summary>
        public Framebuffer GetFramebuffer(int id) => _framebuffers.Get(id);

        public int GetWidth(int id) => _framebuffers.Get(id).Width;
        public int GetHeight(int id) => _framebuffers.Get(id).Height;

        // -----------------------------------------------------------
        // Byte-wise access to the raw framebuffer data (reading/
        // writing, see CONSOLE.md) - in RGBA mode: byte offset 0 = R of the
        // first pixel, 1 = G, 2 = B, 3 = A, 4 = R of the second pixel etc. (fixed
        // order, see the PixelColor documentation - INDEPENDENT of the
        // host endianness, since here each channel is deliberately shifted/
        // masked manually instead of relying on a raw memory
        // reinterpretation). This is the "always correct" basic way. In palette
        // mode a byte is a pixel: the palette index (offset = y * width + x).
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

        /// <summary>Size of the raw data in bytes: 4 per pixel (RGBA) or 1 per pixel (palette).</summary>
        public int GetByteCount(int id) => ByteCount(_framebuffers.Get(id));

        private static int ByteCount(Framebuffer fb) => fb.Indices != null ? fb.Indices.Length : fb.Pixels.Length * 4;

        private static void CheckByteOffset(Framebuffer fb, int byteOffset)
        {
            int totalBytes = ByteCount(fb);
            if (byteOffset < 0 || byteOffset >= totalBytes)
                throw new ArgumentOutOfRangeException(
                    nameof(byteOffset), $"Byte offset {byteOffset} outside of the buffer (size {totalBytes} bytes).");
        }

        // -----------------------------------------------------------
        // Block-wise access - NOT mandatory, but for convenience
        // (see CONSOLE.md): faster than byte by byte when the
        // ENTIRE content is needed at once. Uses Buffer.BlockCopy
        // (raw memory copy) - this reproduces exactly the R,G,B,A byte order,
        // BECAUSE .NET is little-endian on all realistic target platforms (x86/
        // x64/ARM in normal operation) AND the byte layout of PixelColor is designed
        // for exactly that (see the PixelColor documentation) - unlike
        // ReadByte/WriteByte above, this deliberately relies
        // on the host endianness, for the speed gain of a
        // raw memory copy instead of a loop with bit operations.
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
                throw new ArgumentException($"Expected exactly {expected} bytes, got {data.Length}.", nameof(data));
            if (fb.Indices != null)
            {
                Buffer.BlockCopy(data, 0, fb.Indices, 0, data.Length);
                fb.MarkDirty();
            }
            else Buffer.BlockCopy(data, 0, fb.Pixels, 0, data.Length);
        }

        // -----------------------------------------------------------
        // Palette (every framebuffer has one, see Framebuffer.Palette)
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

        /// <summary>The palette as bytes: 768 (R, G, B per entry) or, with `withAlpha`, 1024 (R, G, B, A).</summary>
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

        /// <summary>Sets the whole palette from bytes: 768 (R, G, B; alpha 255) or 1024 (R, G, B, A).</summary>
        public void WritePalette(int id, byte[] data)
        {
            if (data.Length != 768 && data.Length != 1024)
                throw new ArgumentException($"A palette has 768 (RGB) or 1024 (RGBA) bytes, got {data.Length}.", nameof(data));
            int stride = data.Length == 768 ? 3 : 4;
            var colors = new uint[256];
            for (int i = 0; i < 256; i++)
                colors[i] = new PixelColor(data[i * stride], data[i * stride + 1], data[i * stride + 2], stride == 4 ? data[i * stride + 3] : (byte)255).Packed;
            _framebuffers.Get(id).Palette.SetAll(colors);
        }

        private static void CheckPaletteIndex(int index)
        {
            if ((uint)index > 255) throw new ArgumentOutOfRangeException(nameof(index), $"Palette index {index} outside of 0-255.");
        }
    }
}
