using fire.Terminal;

/// <summary>Writing pictures (the pixel editor saves with these): everything that is written reads back the same.</summary>
static class ImageTests
{
    private static int _failures;

    private static void Check(string title, bool ok, string? detail = null)
    {
        if (!ok) _failures++;
        Console.WriteLine(ok ? $"OK: Bilder: {title}" : $"FEHLER: Bilder: {title}" + (detail == null ? "" : $"\n{detail}"));
    }

    public static void Run()
    {
        Console.WriteLine("=== Bilder schreiben ===");
        // an indexed picture of 5 x 3 with a palette of 256 entries, index 0 transparent
        var palette = new uint[256];
        for (int i = 0; i < 256; i++) palette[i] = 0xFF000000u | (uint)(i * 3 % 256) | ((uint)(i * 5 % 256) << 8) | ((uint)(i * 7 % 256) << 16);
        palette[0] = 0x00000000;
        var indices = new byte[15];
        for (int i = 0; i < indices.Length; i++) indices[i] = (byte)(i * 17);
        var indexed = ImageData.CreateIndexed(5, 3, indices, palette, 0, "PNG");

        var png = ImageEncoder.Encode(indexed, "PNG");
        var back = ImageDecoder.Decode(png);
        Check("PNG: ein Bild mit Palette bleibt indiziert, mit Palette und Pixeln", back.IsIndexed && back.Width == 5 && back.Height == 3 && back.Indices!.SequenceEqual(indices) && Enumerable.Range(1, 255).All(i => (back.Palette![i] & 0xFFFFFF) == (palette[i] & 0xFFFFFF)));
        Check("PNG: der durchsichtige Eintrag bleibt durchsichtig", back.TransparentIndex == 0);

        var bmp = ImageEncoder.Encode(indexed, "BMP");
        var bmpBack = ImageDecoder.Decode(bmp);
        Check("BMP: ein Bild mit Palette bleibt indiziert (der durchsichtige Eintrag geht verloren)", bmpBack.IsIndexed && bmpBack.Indices!.SequenceEqual(indices) && (bmpBack.Palette![200] & 0xFFFFFF) == (palette[200] & 0xFFFFFF));

        // truecolor with and without transparency
        var rgba = new uint[4 * 2];
        for (int i = 0; i < rgba.Length; i++) rgba[i] = 0xFF000000u | (uint)(i * 30) | ((uint)(i * 20) << 8) | ((uint)(i * 10) << 16);
        rgba[3] = 0x80102030;
        var withAlpha = ImageData.CreateTruecolor(4, 2, rgba, "PNG");
        var pngAlpha = ImageDecoder.Decode(ImageEncoder.Encode(withAlpha, "PNG"));
        Check("PNG: Truecolor mit Alpha kommt unveraendert zurueck", !pngAlpha.IsIndexed && pngAlpha.Pixels!.SequenceEqual(rgba) && pngAlpha.HasAlpha);
        var opaque = ImageData.CreateTruecolor(4, 2, rgba.Select(p => p | 0xFF000000u).ToArray(), "PNG");
        var pngOpaque = ImageDecoder.Decode(ImageEncoder.Encode(opaque, "PNG"));
        Check("PNG: Truecolor ohne Alpha kommt unveraendert zurueck", !pngOpaque.IsIndexed && pngOpaque.Pixels!.SequenceEqual(opaque.Pixels!) && !pngOpaque.HasAlpha);
        var bmpOpaque = ImageDecoder.Decode(ImageEncoder.Encode(opaque, "BMP"));
        Check("BMP: Truecolor ohne Alpha (24 Bit) kommt unveraendert zurueck", !bmpOpaque.IsIndexed && bmpOpaque.Pixels!.SequenceEqual(opaque.Pixels!));
        var bmpAlpha = ImageDecoder.Decode(ImageEncoder.Encode(withAlpha, "BMP"));
        Check("BMP: Truecolor mit Alpha (32 Bit) behaelt die Farben", !bmpAlpha.IsIndexed && bmpAlpha.Width == 4 && bmpAlpha.Height == 2 && (bmpAlpha.Pixels![0] & 0xFFFFFF) == (rgba[0] & 0xFFFFFF) && (bmpAlpha.Pixels[5] & 0xFFFFFF) == (rgba[5] & 0xFFFFFF));
        Check("Format: nach der Endung", ImageEncoder.FormatOf("a.PNG") == "PNG" && ImageEncoder.FormatOf("a.bmp") == "BMP" && ImageEncoder.FormatOf("a.gif") == null);
        Check("Format: ein anderes Format wird abgelehnt", Throws(() => ImageEncoder.Encode(indexed, "GIF")));
        // ---- the canvas of the pixel editor ---------------------------------------------------------------------------------------------
        var canvas = PixelCanvas.Create(8, 6, indexed: true);
        Check("Zeichenflaeche: neu ist alles durchsichtig (Eintrag 0), die Palette ist die von fire", canvas.IsIndexed && canvas.TransparentIndex == 0 && canvas.ColorAt(3, 3) >> 24 == 0 && canvas.Palette[0] == 0 && (canvas.Palette[1] >> 24) == 255 && canvas.Palette.Count == 256);
        canvas.BeginEdit(); canvas.Line(0, 0, 7, 5, 9); canvas.EndEdit();
        Check("Zeichenflaeche: eine Linie beruehrt beide Enden", canvas.ValueAt(0, 0) == 9 && canvas.ValueAt(7, 5) == 9 && canvas.CanUndo && canvas.IsModified);
        canvas.BeginEdit(); canvas.Rectangle(1, 1, 3, 3, 4, filled: false); canvas.EndEdit();
        Check("Zeichenflaeche: ein Rahmen laesst die Mitte aus", canvas.ValueAt(1, 1) == 4 && canvas.ValueAt(3, 3) == 4 && canvas.ValueAt(2, 2) != 4 && canvas.ValueAt(1, 3) == 4);
        canvas.BeginEdit(); canvas.Fill(2, 2, 5); canvas.EndEdit();
        Check("Zeichenflaeche: Fuellen bleibt innerhalb des Rahmens", canvas.ValueAt(2, 2) == 5 && canvas.ValueAt(5, 0) != 5);
        canvas.Undo();
        Check("Zeichenflaeche: Zurueck nimmt den letzten Strich weg, Wiederholen bringt ihn zurueck", canvas.ValueAt(2, 2) != 5 && canvas.CanRedo);
        canvas.Redo();
        Check("Zeichenflaeche: Wiederholen", canvas.ValueAt(2, 2) == 5);
        canvas.BeginEdit(); canvas.EndEdit();
        Check("Zeichenflaeche: ein Strich ohne Aenderung bleibt nicht im Verlauf", canvas.CanUndo && canvas.ValueAt(2, 2) == 5);
        canvas.BeginEdit(); canvas.Line(0, 5, 7, 5, 7); canvas.RestoreStroke(); canvas.Line(0, 4, 7, 4, 6); canvas.EndEdit();
        Check("Zeichenflaeche: Gummiband - der Strich wird zurueckgesetzt und neu gezogen", canvas.ValueAt(3, 5) != 7 && canvas.ValueAt(3, 4) == 6);
        canvas.SetPaletteColor(5, 0xFF123456);
        Check("Zeichenflaeche: ein Paletteneintrag wird neu gesetzt und ist ein Schritt zum Zuruecknehmen", canvas.ColorAt(2, 2) == 0xFF123456);
        canvas.Undo();
        Check("Zeichenflaeche: Zurueck setzt den Paletteneintrag zurueck", canvas.ColorAt(2, 2) != 0xFF123456);
        canvas.Resize(10, 4);
        Check("Zeichenflaeche: neue Groesse behaelt oben links, neuer Bereich ist durchsichtig", canvas.Width == 10 && canvas.Height == 4 && canvas.ValueAt(0, 0) == 9 && canvas.ColorAt(9, 3) >> 24 == 0);
        var round = ImageDecoder.Decode(ImageEncoder.Encode(canvas.ToImage(), "PNG"));
        var again = PixelCanvas.FromImage(round);
        Check("Zeichenflaeche: speichern und wieder laden gibt dasselbe Bild", again.IsIndexed && again.Width == 10 && again.ValueAt(1, 1) == canvas.ValueAt(1, 1) && again.TransparentIndex == 0);
        canvas.ConvertToTruecolor();
        Check("Zeichenflaeche: in Truecolor umwandeln behaelt die Farben und die Durchsichtigkeit", !canvas.IsIndexed && canvas.ColorAt(0, 0) == canvas.Palette[9] && canvas.ColorAt(9, 3) >> 24 == 0);
        canvas.ConvertToIndexed();
        Check("Zeichenflaeche: in Paletten-Farben umwandeln: durchsichtig bleibt Eintrag 0", canvas.IsIndexed && canvas.ValueAt(9, 3) == 0 && canvas.ValueAt(0, 0) != 0);
        var truecolor = PixelCanvas.Create(3, 3, indexed: false);
        truecolor.BeginEdit(); truecolor.Set(1, 1, 0xFF0000FF); truecolor.Fill(0, 0, 0x80102030); truecolor.EndEdit();
        Check("Zeichenflaeche: Truecolor malt mit Farbwerten (auch halbdurchsichtigen)", truecolor.ColorAt(1, 1) == 0xFF0000FF && truecolor.ColorAt(0, 0) == 0x80102030);
        Check("Zeichenflaeche: eine unsinnige Groesse wird abgelehnt", Throws(() => PixelCanvas.Create(0, 5, true)) && Throws(() => canvas.Resize(5, -1)));

        Console.WriteLine(_failures == 0 ? "Alle Bild-Pruefungen bestanden." : $"FEHLER: {_failures} Bild-Pruefung(en) fehlgeschlagen.");
    }

    private static bool Throws(Action a) { try { a(); return false; } catch (Exception) { return true; } }
}
