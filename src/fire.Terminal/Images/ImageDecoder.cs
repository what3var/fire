using System;

namespace fire.Terminal
{
    /// <summary>
    /// Reads image files (PNG, BMP, GIF) from bytes. Own decoders without any dependency (the library is deliberately platform-independent,
    /// without System.Drawing/WPF): the format is recognised by the first bytes, not by a file extension.
    /// </summary>
    public static class ImageDecoder
    {
        /// <summary>The format of the data ("PNG", "BMP", "GIF") or null.</summary>
        public static string? DetectFormat(ReadOnlySpan<byte> data)
        {
            if (data.Length >= 8 && data[0] == 0x89 && data[1] == 'P' && data[2] == 'N' && data[3] == 'G' && data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A) return "PNG";
            if (data.Length >= 2 && data[0] == 'B' && data[1] == 'M') return "BMP";
            if (data.Length >= 6 && data[0] == 'G' && data[1] == 'I' && data[2] == 'F' && data[3] == '8' && (data[4] == '7' || data[4] == '9') && data[5] == 'a') return "GIF";
            return null;
        }

        /// <summary>Decodes an image file. Throws <see cref="ImageFormatException"/> for unknown, damaged or unsupported content.</summary>
        public static ImageData Decode(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data.Length == 0) throw new ImageFormatException("The image data is empty.");
            try
            {
                return DetectFormat(data) switch
                {
                    "PNG" => PngDecoder.Decode(data),
                    "BMP" => BmpDecoder.Decode(data),
                    "GIF" => GifDecoder.Decode(data),
                    _ => throw new ImageFormatException("Unknown image format (expected: PNG, BMP or GIF)."),
                };
            }
            catch (ImageFormatException) { throw; }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidDataException or OverflowException or InvalidOperationException)
            {
                // a decoder that has read beyond the end of the data: truncated/damaged file
                throw new ImageFormatException($"Corrupt image file ({ex.GetType().Name}).");
            }
        }
    }
}
