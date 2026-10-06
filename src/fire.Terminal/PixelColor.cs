using System.Runtime.InteropServices;

namespace fire.Terminal
{
    /// <summary>
    /// Eine einzelne Farbe, 4 Byte: R, G, B, A - IMMER in genau dieser
    /// Byte-Reihenfolge im Speicher (R zuerst, dann G, B, A), UNABHÄNGIG von
    /// der Endianness der Host-Architektur. Das ist bewusst KEIN gepackter
    /// 32-Bit-Ganzzahlwert im klassischen "ARGB"-Sinn (dessen Bit-Anordnung
    /// von der Host-Endianness abhinge) - Packed hier ist die reine
    /// Speicher-Reinterpretation derselben 4 Bytes als eine Zahl, nur für
    /// SCHNELLE Ganzwert-Operationen in C# selbst (Vergleich, Hashing,
    /// Kopieren) gedacht, nicht als portabler "Farbwert".
    ///
    /// Für die spätere Skriptsprachen-Anbindung ist das der Punkt: eine
    /// PixelColor lässt sich als 4 zusammenhängende Bytes lesen (R,G,B,A,
    /// exakt in dieser Reihenfolge, z.B. über einen Byte-Puffer-Blick auf
    /// diesen Speicher) OHNE jede Bit-Schiebe-/Masken-Rechnung - "R" ist
    /// einfach Byte 0, "G" Byte 1, usw., ganz gleich ob man einzelne Bytes
    /// oder den ganzen 32-Bit-Block liest.
    ///
    /// [StructLayout(LayoutKind.Explicit)] überlagert Packed UND die vier
    /// Byte-Felder auf DENSELBEN Speicherbereich (FieldOffset 0-3) - beide
    /// Sichten sind buchstäblich dieselben 4 Bytes, keine Umrechnung, kein
    /// zusätzlicher Speicher, kein Overhead.
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    public readonly struct PixelColor
    {
        [FieldOffset(0)] public readonly byte R;
        [FieldOffset(1)] public readonly byte G;
        [FieldOffset(2)] public readonly byte B;
        [FieldOffset(3)] public readonly byte A;

        /// <summary>Dieselben 4 Bytes als EIN 32-Bit-Wert (R im niedrigsten
        /// Byte, siehe Klassen-Doku) - für schnelle Vergleiche/Hashing/
        /// Kopieren, ohne die einzelnen Kanäle einzeln anzufassen.</summary>
        [FieldOffset(0)] public readonly uint Packed;

        public PixelColor(byte r, byte g, byte b, byte a = 255) : this()
        {
            R = r;
            G = g;
            B = b;
            A = a;
        }

        public PixelColor(uint packed) : this()
        {
            Packed = packed;
        }

        /// <summary>Implizite Konvertierung zu einem 32-Bit-Farbwert (siehe
        /// Klassen-Doku) - macht `SetColor(index, farbe)`/Grafikfunktionen
        /// möglich, die wahlweise eine PixelColor ODER direkt einen rohen
        /// int-Farbwert entgegennehmen, ohne zwei separate Aufrufstellen im
        /// aufrufenden Code zu brauchen (siehe Renderer/Palette).</summary>
        public static implicit operator int(PixelColor color) => unchecked((int)color.Packed);

        /// <summary>Vollständig durchsichtig, als (0, 1, 0, 0) und nicht (0, 0, 0, 0): als Zahl (256) wird sie nicht für den Palette-Index 0 gehalten
        /// (siehe <see cref="Paint.ToArgument"/>). Siehe Renderer.
        /// Background-Doku ("optional transparent") und Framebuffer.SetPixel
        /// (schreibt den Alpha-Wert unverändert ins Zielpixel, MISCHT NICHT
        /// - ein Framebuffer dieser Bibliothek führt selbst kein Alpha-
        /// Blending durch, siehe dortige Doku).</summary>
        public static readonly PixelColor Transparent = new(0, 1, 0, 0);

        public static PixelColor FromRgb(byte r, byte g, byte b) => new(r, g, b, 255);

        // ---------------------------------------------------------------
        // Klassische 16-Farben-CGA-/QBasic-Palette (COLOR-Anweisung,
        // Farbnummern 0-15) - als benannte Konstanten, damit eine spätere
        // fire-Anbindung (z.B. "color(QBColor.LightBlue, ...)") nicht
        // erst eigene Farbwerte definieren/recherchieren muss. Werte
        // entsprechen der Standard-CGA-Palette.
        // ---------------------------------------------------------------
        public static readonly PixelColor Black = FromRgb(0, 0, 0);
        public static readonly PixelColor Blue = FromRgb(0, 0, 170);
        public static readonly PixelColor Green = FromRgb(0, 170, 0);
        public static readonly PixelColor Cyan = FromRgb(0, 170, 170);
        public static readonly PixelColor Red = FromRgb(170, 0, 0);
        public static readonly PixelColor Magenta = FromRgb(170, 0, 170);
        public static readonly PixelColor Brown = FromRgb(170, 85, 0);
        public static readonly PixelColor LightGray = FromRgb(170, 170, 170);
        public static readonly PixelColor DarkGray = FromRgb(85, 85, 85);
        public static readonly PixelColor LightBlue = FromRgb(85, 85, 255);
        public static readonly PixelColor LightGreen = FromRgb(85, 255, 85);
        public static readonly PixelColor LightCyan = FromRgb(85, 255, 255);
        public static readonly PixelColor LightRed = FromRgb(255, 85, 85);
        public static readonly PixelColor LightMagenta = FromRgb(255, 85, 255);
        public static readonly PixelColor Yellow = FromRgb(255, 255, 85);
        public static readonly PixelColor White = FromRgb(255, 255, 255);

        /// <summary>Die 16 QBasic-Farben in ihrer klassischen Nummerierung
        /// (Index 0-15, wie bei der `COLOR`-Anweisung) - für eine spätere
        /// Anbindung, die Farben per Ganzzahl statt per Name wählen will.</summary>
        public static readonly PixelColor[] QBasicPalette =
        {
            Black, Blue, Green, Cyan, Red, Magenta, Brown, LightGray,
            DarkGray, LightBlue, LightGreen, LightCyan, LightRed, LightMagenta, Yellow, White,
        };
    }
}
