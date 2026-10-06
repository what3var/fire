using System;

namespace fire.Terminal
{
    /// <summary>
    /// Eine anpassbare 256-Farben-Palette (Index 0-255) - jeder Eintrag ist
    /// ein 32-Bit-Farbwert, per <see cref="SetColor"/> jederzeit frei
    /// überschreibbar (klassisches VGA-Palettenregister-Verhalten: der
    /// Index bleibt gleich, welche tatsächliche Farbe dahinter steckt, kann
    /// sich ändern). Die Default-Belegung ist strukturiert wie die
    /// verbreitete xterm-256-Farbpalette:
    ///
    /// - Index 0-15: die klassischen 16 CGA/QBasic-Farben (siehe
    ///   <see cref="PixelColor.QBasicPalette"/>) - für Rückwärtskompatibilität
    ///   mit einfachem `COLOR 0-15`-artigem Gebrauch.
    /// - Index 16-231: ein 6x6x6-Farbwürfel (216 Farben, Stufen 0/51/102/
    ///   153/204/255 je Kanal) - dieselbe, weithin bekannte Struktur wie die
    ///   "websichere" 216-Farben-Palette.
    /// - Index 232-255: ein 24-stufiger Graukeil.
    ///
    /// Das ist BEWUSST nicht der exakte historische VGA-Standard-DAC (dessen
    /// genaue Werte sich ohne Möglichkeit, sie hier tatsächlich zu rendern
    /// und zu prüfen, nicht verlässlich aus dem Gedächtnis reproduzieren
    /// ließen) - da die Palette ohnehin vollständig per SetColor überschreibbar
    /// ist, zählt für die Defaults vor allem eine nachvollziehbare, garantiert
    /// korrekte Formel mit guter Farbabdeckung.
    /// </summary>
    public sealed class Palette
    {
        private readonly int[] _entries = new int[256];

        public Palette()
        {
            FillDefaults();
        }

        /// <summary>Wird bei JEDER Änderung der Palette erhöht - ein Framebuffer im Palette-Modus erkennt daran, dass sein sichtbares Abbild
        /// neu berechnet werden muss (siehe Framebuffer.Resolve), auch wenn sich kein einziger Index geändert hat.</summary>
        public int Version { get; private set; }

        /// <summary>Überschreibt Palette-Index `index` mit einem neuen
        /// 32-Bit-Farbwert - wirkt sich sofort auf alles aus, was diesen
        /// Index danach per <see cref="GetColor"/> nachschlägt (z.B. bereits
        /// gezeichnete Pixel NICHT rückwirkend, da ein Framebuffer-Pixel
        /// selbst keinen Palette-Index speichert, sondern schon beim
        /// Zeichnen zu einer konkreten PixelColor aufgelöst wurde - siehe
        /// Renderer.SetPixel(byte)-Überladungen).</summary>
        public void SetColor(byte index, int color)
        {
            _entries[index] = color;
            Version++;
        }

        public PixelColor GetColor(byte index) => new PixelColor(unchecked((uint)_entries[index]));

        /// <summary>Der Eintrag als gepackter Wert (R im niedrigsten Byte), ohne den Umweg über PixelColor.</summary>
        public uint GetPacked(byte index) => unchecked((uint)_entries[index]);

        /// <summary>Kopiert alle 256 Einträge (gepackt) nach `destination` (mindestens 256 Plätze).</summary>
        public void CopyPacked(Span<uint> destination)
        {
            for (int i = 0; i < 256; i++) destination[i] = unchecked((uint)_entries[i]);
        }

        /// <summary>Setzt die ersten `colors.Length` Einträge (höchstens 256) auf einmal; die übrigen bleiben unverändert.</summary>
        public void SetAll(ReadOnlySpan<uint> colors)
        {
            int n = Math.Min(colors.Length, 256);
            for (int i = 0; i < n; i++) _entries[i] = unchecked((int)colors[i]);
            Version++;
        }

        /// <summary>Stellt die Standard-Belegung wieder her (siehe Klassen-Doku).</summary>
        public void ResetToDefaults()
        {
            FillDefaults();
            Version++;
        }

        /// <summary>Der Index der Farbe, die `color` am nächsten kommt (kleinster Abstand der Kanäle R, G, B im Quadrat; der Alpha-Wert zählt
        /// nicht). Bei gleichem Abstand gewinnt der kleinere Index; eine exakt vorhandene Farbe wird sofort gefunden.</summary>
        public byte FindNearest(PixelColor color)
        {
            int r = color.R, g = color.G, b = color.B;
            int best = 0, bestDistance = int.MaxValue;
            for (int i = 0; i < 256; i++)
            {
                uint packed = unchecked((uint)_entries[i]);
                int dr = (int)(packed & 0xFF) - r, dg = (int)((packed >> 8) & 0xFF) - g, db = (int)((packed >> 16) & 0xFF) - b;
                int distance = dr * dr + dg * dg + db * db;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                    if (distance == 0) break;
                }
            }
            return (byte)best;
        }

        private void FillDefaults()
        {
            for (int i = 0; i < 16; i++)
                _entries[i] = PixelColor.QBasicPalette[i];

            int[] levels = { 0, 51, 102, 153, 204, 255 };
            int idx = 16;
            foreach (int r in levels)
            {
                foreach (int g in levels)
                {
                    foreach (int b in levels)
                    {
                        _entries[idx] = PixelColor.FromRgb((byte)r, (byte)g, (byte)b);
                        idx++;
                    }
                }
            }
            // idx steht jetzt bei 16 + 216 = 232.

            for (int i = 0; i < 24; i++)
            {
                byte v = (byte)(8 + i * 10);
                _entries[232 + i] = PixelColor.FromRgb(v, v, v);
            }
        }
    }
}
