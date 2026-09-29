using System;

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

        public int CreateFramebuffer(int width, int height) =>
            _framebuffers.Create(new Framebuffer(width, height));

        public bool DestroyFramebuffer(int id) => _framebuffers.Destroy(id);

        /// <summary>Für C#-seitige Weiterverwendung (z.B. ConsoleManager/
        /// WindowManager, die eine echte Framebuffer-Instanz brauchen) -
        /// kein Teil des rein-ID-basierten Oberflächen-APIs.</summary>
        public Framebuffer GetFramebuffer(int id) => _framebuffers.Get(id);

        public int GetWidth(int id) => _framebuffers.Get(id).Width;
        public int GetHeight(int id) => _framebuffers.Get(id).Height;

        // -----------------------------------------------------------
        // Byteweiser Zugriff auf die Framebuffer-Rohdaten (lesend/
        // schreibend, siehe CONSOLE.md) - Byte-Offset 0 = R des ersten
        // Pixels, 1 = G, 2 = B, 3 = A, 4 = R des zweiten Pixels usw. (feste
        // Reihenfolge, siehe PixelColor-Doku - UNABHÄNGIG von der
        // Host-Endianness, da hier bewusst manuell pro Kanal geschoben/
        // maskiert wird statt sich auf eine rohe Speicher-Reinterpretation
        // zu verlassen). Das ist der "immer korrekte" Basisweg.
        // -----------------------------------------------------------

        public byte ReadByte(int id, int byteOffset)
        {
            var fb = _framebuffers.Get(id);
            CheckByteOffset(fb, byteOffset);
            uint packed = fb.Pixels[byteOffset / 4];
            int channel = byteOffset % 4;
            return (byte)(packed >> (channel * 8));
        }

        public void WriteByte(int id, int byteOffset, byte value)
        {
            var fb = _framebuffers.Get(id);
            CheckByteOffset(fb, byteOffset);
            int pixelIndex = byteOffset / 4;
            int channel = byteOffset % 4;
            int shift = channel * 8;
            uint mask = ~((uint)0xFF << shift);
            fb.Pixels[pixelIndex] = (fb.Pixels[pixelIndex] & mask) | ((uint)value << shift);
        }

        private static void CheckByteOffset(Framebuffer fb, int byteOffset)
        {
            int totalBytes = fb.Pixels.Length * 4;
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
            var bytes = new byte[fb.Pixels.Length * 4];
            Buffer.BlockCopy(fb.Pixels, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        public void WriteBytes(int id, byte[] data)
        {
            var fb = _framebuffers.Get(id);
            int expected = fb.Pixels.Length * 4;
            if (data.Length != expected)
                throw new ArgumentException($"Erwarte genau {expected} Byte, erhalten {data.Length}.", nameof(data));
            Buffer.BlockCopy(data, 0, fb.Pixels, 0, data.Length);
        }
    }
}
