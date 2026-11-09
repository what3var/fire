using fire.Compiler;

/// <summary>The block models of spark's block editor (`.fblocks`, docs/graphics/models.md): the text, the faces that are seen, the greedy rectangles, the ray that
/// picks a block, and the model file forge makes of them. FIRE_TESTS_ONLY=blocks.</summary>
static class BlockModelTests
{
    private static int _failures;

    private static void Check(string title, bool ok, string detail = "")
    {
        if (!ok) _failures++;
        Console.WriteLine(ok ? $"OK: Blockmodelle: {title}" : $"FEHLER: Blockmodelle: {title}" + (detail != "" ? "\n  " + detail : ""));
    }

    /// <summary>A small figure for the tests (also the resource of Scene3DTests): a body of two colours with a head (a part of its own) on top.</summary>
    public static string Figure()
    {
        var m = BlockModel.CreateDefault(6, 8, 4);
        m.Parts.Add(new BlockModel.Part { Name = "head", Parent = 0, Pivot = new double[] { 3, 5, 2 } });
        for (int x = 1; x < 5; x++) for (int y = 0; y < 5; y++) for (int z = 1; z < 3; z++) m.Set(x, y, z, y < 2 ? 10 : 4);
        for (int x = 2; x < 4; x++) for (int y = 5; y < 7; y++) for (int z = 1; z < 3; z++) m.Set(x, y, z, 15, 1);
        // "nod": the head turns around x (0, 30, 0 degrees in a second); "hop": the body goes up and down once (half a second, no loop)
        var nod = new BlockModel.Animation { Name = "nod", Length = 1, Loop = true };
        nod.Keys.Add(new BlockModel.Key { Part = 1, Time = 0 });
        nod.Keys.Add(new BlockModel.Key { Part = 1, Time = 0.5, Rotate = new double[] { 30, 0, 0 } });
        var hop = new BlockModel.Animation { Name = "hop", Length = 0.5, Loop = false };
        hop.Keys.Add(new BlockModel.Key { Part = 0, Time = 0 });
        hop.Keys.Add(new BlockModel.Key { Part = 0, Time = 0.25, Move = new double[] { 0, 2, 0 }, Scale = new double[] { 1.1, 0.9, 1.1 } });
        hop.Keys.Add(new BlockModel.Key { Part = 0, Time = 0.5 });
        m.Animations.Add(nod); m.Animations.Add(hop);
        return m.ToJson();
    }

    public static int Run()
    {
        Console.WriteLine("=== Blockmodelle (.fblocks) ===");
        var m = BlockModel.CreateDefault(8, 8, 8);
        m.Set(1, 2, 3, 4);
        m.Set(0, 0, 0, 2, 0);
        m.Parts.Add(new BlockModel.Part { Name = "arm", Parent = 0, Pivot = new double[] { 2, 3.5, 1 } });
        m.Set(5, 5, 5, 7, 1);
        string text = m.ToJson();
        var back = BlockModel.Parse(text);
        Check("Text hin und zurueck (Groesse, Palette, Teile, Bloecke)", back.ToJson() == text && back.Count == 3 && back.Parts.Count == 2 && back.Parts[1].Pivot[1] == 3.5, text);
        Check("Text: ein Block je Zeile, nach y, z, x", text.Contains("[0, 0, 0, 2],\n    [1, 2, 3, 4],\n    [5, 5, 5, 7, 1]"), text);
        Check("Text: ungueltig -> InvalidDataException", Throws(() => BlockModel.Parse("{\"x\": 1}")) && Throws(() => BlockModel.Parse("{\"fblocks\": 1, \"blocks\": [[1, 2]]}")));
        Check("Leerer Text: ein Standardmodell", BlockModel.Parse("").Palette.Count == 16 && BlockModel.Parse("").Parts.Count == 1);

        var one = BlockModel.CreateDefault(4, 4, 4);
        one.Set(1, 1, 1, 3);
        Check("Ein Block: 6 Flaechen", one.Quads().Count == 6 && one.VisibleFaces().Count() == 6);
        var two = BlockModel.CreateDefault(4, 4, 4);
        two.Set(1, 1, 1, 3); two.Set(2, 1, 1, 3);
        Check("Zwei Bloecke einer Farbe: die Flaeche dazwischen faellt weg, gleiche Flaechen verschmelzen (6 Rechtecke)", two.Quads().Count == 6 && two.VisibleFaces().Count() == 10, two.Quads().Count + " " + two.VisibleFaces().Count());
        two.Set(2, 1, 1, 4);
        Check("Zwei Farben: 10 Flaechen (nichts zwischen ihnen)", two.Quads().Count == 10);
        two.Parts.Add(new BlockModel.Part { Name = "b", Parent = 0 });
        two.Set(2, 1, 1, 3, 1);
        Check("Zwei Teile: die Flaechen zwischen ihnen bleiben (12)", two.Quads().Count == 12);
        var plate = BlockModel.CreateDefault(8, 2, 8);
        for (int x = 0; x < 8; x++) for (int z = 0; z < 8; z++) plate.Set(x, 0, z, 5);
        Check("Eine Platte 8x1x8: 6 Rechtecke statt 256 Flaechen", plate.Quads().Count == 6 && plate.VisibleFaces().Count() == 8 * 8 * 2 + 8 * 4, plate.Quads().Count.ToString());
        var stairs = BlockModel.CreateDefault(4, 4, 1);
        for (int x = 0; x < 4; x++) for (int y = 0; y <= x; y++) stairs.Set(x, y, 0, 1);
        Check("Eine Treppe: 4 Deckel, 1 Boden, 4 Stufen-Waende, je eine Seite vorn und hinten pro Saeule", stairs.Quads().Count == 4 + 1 + 4 + 1 + 4 + 4, stairs.Quads().Count.ToString());
        double Area(BlockModel.Quad q) { var u = q.B.Zip(q.A, (a, b) => a - b).ToArray(); var v = q.D.Zip(q.A, (a, b) => a - b).ToArray(); return Math.Sqrt(Math.Pow(u[1] * v[2] - u[2] * v[1], 2) + Math.Pow(u[2] * v[0] - u[0] * v[2], 2) + Math.Pow(u[0] * v[1] - u[1] * v[0], 2)); }
        Check("Die Rechtecke der Treppe decken genau ihre sichtbaren Flaechen", Math.Abs(stairs.Quads().Sum(Area) - stairs.VisibleFaces().Count()) < 1e-9);
        bool outward = true;
        foreach (var q in one.Quads())
        {
            var d = BlockModel.Directions[q.Dir];
            var u = q.B.Zip(q.A, (a, b) => a - b).ToArray(); var v = q.C.Zip(q.A, (a, b) => a - b).ToArray();
            double nx = u[1] * v[2] - u[2] * v[1], ny = u[2] * v[0] - u[0] * v[2], nz = u[0] * v[1] - u[1] * v[0];
            if (nx * d.X + ny * d.Y + nz * d.Z <= 0) outward = false;
        }
        Check("Die Ecken laufen von aussen gesehen gegen den Uhrzeigersinn (die Normale zeigt nach aussen)", outward);

        Check("Strahl von oben trifft den Block, durch seinen Deckel", one.Raycast(new[] { 1.5, 10, 1.5 }, new[] { 0.0, -1, 0 }, out var cell, out int face) && cell == (1, 1, 1) && face == 2, $"{cell} {face}");
        Check("Strahl von der Seite (von +x her) trifft die +x-Flaeche",
            one.Raycast(new[] { 10, 1.5, 1.5 }, new[] { -1.0, 0, 0 }, out cell, out face) && cell == (1, 1, 1) && face == 0, $"{cell} {face}");
        Check("Strahl schraeg trifft den Block", one.Raycast(new[] { -3.0, 5.2, -3 }, new[] { 1.0, -1, 1 }, out cell, out face) && cell == (1, 1, 1), $"{cell} {face}");
        Check("Strahl ohne Block trifft den Boden (Zelle y = -1, Flaeche nach oben)", one.Raycast(new[] { 3.5, 10, 0.5 }, new[] { 0.0, -1, 0 }, out cell, out face) && cell == (3, -1, 0) && face == 2, $"{cell} {face}");
        Check("Strahl am Gitter vorbei: nichts", !one.Raycast(new[] { 30.0, 10, 30 }, new[] { 0.0, -1, 0 }, out _, out _));

        var fig = BlockModel.Parse(Figure());
        byte[] f3d = fig.ToF3d();
        Check("Modelldatei: F3D1, Teile body/head und je Farbe ein Teil darunter (5)", f3d.Length > 40 && f3d[0] == 'F' && BitConverter.ToUInt16(f3d, 6) == 5, BitConverter.ToUInt16(f3d, 6).ToString());
        Check("Modelldatei: die Box ist das Gitter um den Ursprung unten in der Mitte", Math.Abs(BitConverter.ToSingle(f3d, 12) + 0.2f) < 1e-6 && Math.Abs(BitConverter.ToSingle(f3d, 28) - 0.7f) < 1e-6,
            $"{BitConverter.ToSingle(f3d, 12)} .. {BitConverter.ToSingle(f3d, 28)}");
        Check("forge: ConvertFile nimmt .fblocks", ModelConverter.IsModelSource("a.fblocks") && ModelConverter.ConvertBlocks(Figure()).SequenceEqual(f3d));

        // ---- animations ----
        var figText = Figure();
        var fig2 = BlockModel.Parse(figText);
        Check("Animationen: Text hin und zurueck (Name, Laenge, Schleife, Schluessel)", fig2.ToJson() == figText && fig2.Animations.Count == 2 && !fig2.Animations[1].Loop && fig2.Animations[1].Keys.Count == 3, figText);
        var nodA = fig2.Animations[0];
        bool Near(double[] a, params double[] b) => a.Zip(b).All(p => Math.Abs(p.First - p.Second) < 1e-9);
        Check("Pose: zwischen zwei Schluesseln linear (0.25 s: 15 Grad)", Near(fig2.PartPose(nodA, 1, 0.25).Rotate, 15, 0, 0));
        Check("Pose: Schleife - nach dem letzten Schluessel zurueck zum ersten (0.75 s: 15 Grad), 1.25 s wie 0.25 s",
            Near(fig2.PartPose(nodA, 1, 0.75).Rotate, 15, 0, 0) && Near(fig2.PartPose(nodA, 1, 1.25).Rotate, 15, 0, 0));
        Check("Pose: ein Teil ohne Schluessel bleibt in Ruhe", Near(fig2.PartPose(nodA, 0, 0.3).Move, 0, 0, 0) && Near(fig2.PartPose(nodA, 0, 0.3).Scale, 1, 1, 1));
        var hopA = fig2.Animations[1];
        Check("Pose: ohne Schleife bleibt das Ende (2 s wie 0.5 s), Verschieben und Skalieren", Near(fig2.PartPose(hopA, 0, 0.125).Move, 0, 1, 0) && Near(fig2.PartPose(hopA, 0, 0.125).Scale, 1.05, 0.95, 1.05)
            && Near(fig2.PartPose(hopA, 0, 2).Move, 0, 0, 0));
        var turn = BlockModel.CreateDefault(4, 4, 4);
        turn.Parts.Add(new BlockModel.Part { Name = "arm", Parent = 0, Pivot = new double[] { 1, 1, 1 } });
        var spin = new BlockModel.Animation { Name = "spin" };
        spin.Keys.Add(new BlockModel.Key { Part = 1, Time = 0, Rotate = new double[] { 0, 0, 90 } });
        turn.Animations.Add(spin);
        turn.Animations.Add(new BlockModel.Animation { Name = "lift", Keys = { new BlockModel.Key { Part = 0, Time = 0, Move = new double[] { 0, 1, 0 } } } });
        var mats = turn.PoseMatrices(spin, 0);
        double[] Apply(double[] m, double x, double y, double z) => new[] { m[0] * x + m[1] * y + m[2] * z + m[3], m[4] * x + m[5] * y + m[6] * z + m[7], m[8] * x + m[9] * y + m[10] * z + m[11] };
        Check("Pose: ein Teil dreht um seinen Drehpunkt (90 Grad um z: (2, 1, 1) -> (1, 2, 1)), der Drehpunkt bleibt",
            Near(Apply(mats[1], 2, 1, 1), 1, 2, 1) && Near(Apply(mats[1], 1, 1, 1), 1, 1, 1) && Near(Apply(mats[0], 2, 1, 1), 2, 1, 1));
        var lifted = turn.PoseMatrices(turn.Animations[1], 0);
        Check("Pose: ein Kind geht mit seinem Eltern-Teil", Near(Apply(lifted[1], 2, 1, 1), 2, 2, 1));
        turn.RemovePart(1);
        Check("Ein Teil entfernt: seine Schluessel gehen mit", turn.Animations[0].Keys.Count == 0 && turn.Animations[1].Keys.Count == 1);
        byte[] anim3d = fig2.ToF3d();
        Check("Modelldatei: zwei Clips", BitConverter.ToUInt16(anim3d, 8) == 2);

        Console.WriteLine(_failures == 0 ? "Alle Blockmodell-Pruefungen bestanden." : $"FEHLER: {_failures} Blockmodell-Pruefung(en) fehlgeschlagen.");
        return _failures;
    }

    private static bool Throws(Action a) { try { a(); return false; } catch (InvalidDataException) { return true; } }
}
