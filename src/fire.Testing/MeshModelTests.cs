using fire.Compiler;

/// <summary>The mesh models of spark's CAD editor (`.fmesh`, docs/graphics/models.md): the text, the bodies to start from, extruding, mirroring, deleting,
/// welding, the ray that picks a face, and the model file forge makes of them. FIRE_TESTS_ONLY=mesh.</summary>
static class MeshModelTests
{
    private static int _failures;

    private static void Check(string title, bool ok, string detail = "")
    {
        if (!ok) _failures++;
        Console.WriteLine(ok ? $"OK: Netzmodelle: {title}" : $"FEHLER: Netzmodelle: {title}" + (detail != "" ? "\n  " + detail : ""));
    }

    /// <summary>A small house for the tests (also the resource of Scene3DTests): a box with a roof (two faces of the top pulled up), a door (a face of the front
    /// pushed in) and a chimney mirrored to the other side; the roof a part of its own that nods.</summary>
    public static string House()
    {
        var m = MeshModel.CreateEmpty();
        var box = m.AddBox(new[] { -1.0, 0, -0.75 }, new[] { 1.0, 1.2, 0.75 }, 13, 0);
        // the roof: the top split along x, the middle line pulled up
        int top = box[2];
        int a = m.SplitEdge(m.Faces[top].V[0], m.Faces[top].V[1]);
        int b = m.SplitEdge(m.Faces[top].V[3], m.Faces[top].V[4]);
        int roof2 = m.SplitFace(top, a, b);
        m.Move(new[] { a, b }, new[] { 0, 0.6, 0 });
        m.Faces[top].Color = 4; m.Faces[roof2].Color = 4;
        m.Parts.Add(new BlockModel.Part { Name = "roof", Parent = 0, Pivot = new[] { 0, 1.2, 0 } });
        m.Faces[top].Part = 1; m.Faces[roof2].Part = 1;
        var nod = new BlockModel.Animation { Name = "nod", Length = 1 };
        nod.Keys.Add(new BlockModel.Key { Part = 1, Time = 0 });
        nod.Keys.Add(new BlockModel.Key { Part = 1, Time = 0.5, Rotate = new double[] { 0, 0, 10 } });
        m.Animations.Add(nod);
        return m.ToJson();
    }

    public static int Run()
    {
        Console.WriteLine("=== Netzmodelle (.fmesh) ===");
        bool Near(double[] a, params double[] b) => a.Zip(b).All(p => Math.Abs(p.First - p.Second) < 1e-9);

        var cube = MeshModel.CreateDefault();
        Check("Standard: ein Wuerfel (8 Ecken, 6 Flaechen, 12 Dreiecke, 12 Kanten)", cube.Vertices.Count == 8 && cube.Faces.Count == 6 && cube.TriangleCount == 12 && cube.Edges().Count == 12);
        bool outward = Enumerable.Range(0, cube.Faces.Count).All(f => MeshModel.Dot(cube.Normal(f), MeshModel.Sub(cube.Center(f), new[] { 0, 0.5, 0 })) > 0);
        Check("Wuerfel: alle Normalen zeigen nach aussen", outward);
        string text = cube.ToJson();
        var back = MeshModel.Parse(text);
        Check("Text hin und zurueck", back.ToJson() == text && back.Faces.Count == 6 && back.Faces[0].Color == 9, text);
        Check("Text: eine Ecke je Zeile, eine Flaeche je Zeile [Farbe, Teil, Ecken...]", text.Contains("\"vertices\": [\n    [-0.5, 0, -0.5],\n    [0.5, 0, -0.5],") && text.Contains("[9, 0, 1, 3, 7, 5]"), text);
        Check("Text: ungueltig -> InvalidDataException", Throws(() => MeshModel.Parse("{\"x\": 1}")) && Throws(() => MeshModel.Parse("{\"fmesh\": 1, \"vertices\": [[0, 0, 0]], \"faces\": [[0, 0, 0, 1, 2]]}")));
        Check("Leerer Text: der Wuerfel", MeshModel.Parse("").Faces.Count == 6 && MeshModel.Parse("").Palette.Count == 16);
        var rounded = MeshModel.CreateEmpty();
        rounded.AddVertex(0.1 + 0.2, -1e-9, 1.0 / 3);
        Check("Text: Zahlen auf Mikrometer gerundet, keine -0", rounded.ToJson().Contains("[0.3, 0, 0.333333]"), rounded.ToJson());

        // ---- extruding ----
        var ex = MeshModel.CreateDefault();
        int topFace = Enumerable.Range(0, 6).First(f => ex.Normal(f)[1] > 0.9);
        var made = ex.Extrude(new[] { topFace }, 0.5);
        Check("Extrudieren: der Deckel geht 0.5 nach oben, 4 Waende kommen dazu (10 Flaechen, 12 Ecken)",
            ex.Faces.Count == 10 && ex.Vertices.Count == 12 && made.Count == 5 && made[0] == topFace && ex.Faces[topFace].V.All(v => Math.Abs(ex.Vertices[v][1] - 1.5) < 1e-9));
        bool wallsOut = made.Skip(1).All(f => { var n = ex.Normal(f); var c = ex.Center(f); return Math.Abs(n[1]) < 1e-9 && n[0] * c[0] + n[2] * c[2] > 0 && c[1] > 1; });
        Check("Extrudieren: die Waende stehen senkrecht und zeigen nach aussen", wallsOut);
        Check("Extrudieren: geschlossen (jede Kante an genau zwei Flaechen)", Closed(ex));
        var pocket = MeshModel.CreateDefault();
        int front = Enumerable.Range(0, 6).First(f => pocket.Normal(f)[2] > 0.9);
        pocket.Extrude(new[] { front }, -0.3);
        Check("Nach innen extrudieren: die Flaeche geht 0.3 hinein, geschlossen", pocket.Faces[front].V.All(v => Math.Abs(pocket.Vertices[v][2] - 0.2) < 1e-9) && Closed(pocket));
        var corner = MeshModel.CreateDefault();
        var two = Enumerable.Range(0, 6).Where(f => corner.Normal(f)[1] > 0.9 || corner.Normal(f)[0] > 0.9).ToList();
        corner.Extrude(two, 0.25);
        var shared = corner.Faces[two[0]].V.Intersect(corner.Faces[two[1]].V).ToList();
        Check("Zwei Flaechen an einer Kante: beide gehen 0.25 hinaus (die Ecke diagonal), geschlossen",
            shared.Count == 2 && shared.All(v => Math.Abs(corner.Vertices[v][0] - 0.75) < 1e-9 && Math.Abs(corner.Vertices[v][1] - 1.25) < 1e-9) && Closed(corner) && corner.Faces.Count == 6 + 6);
        var plane = MeshModel.CreateEmpty();
        var p = plane.AddPlane(new[] { 0.0, 0, 0 }, 2, 1, 3, 0);
        Check("Ebene: eine Flaeche nach oben", plane.Faces.Count == 1 && Near(plane.Normal(0), 0, 1, 0));
        plane.Extrude(p, 0.5);
        Check("Eine Ebene extrudiert: ein geschlossener Koerper (Deckel, 4 Waende, Boden nach unten)", plane.Faces.Count == 6 && Closed(plane) && plane.Faces.Any(f => plane.Normal(plane.Faces.IndexOf(f))[1] < -0.9));

        // ---- mirroring ----
        var half = MeshModel.CreateEmpty();
        half.AddBox(new[] { 0.0, 0, 0 }, new[] { 1.0, 1, 1 }, 2, 0);
        int inPlane = Enumerable.Range(0, 6).First(f => half.Normal(f)[0] < -0.9);
        half.DeleteFaces(new[] { inPlane });
        int verts = half.Vertices.Count;
        var copies = half.Mirror(Enumerable.Range(0, half.Faces.Count).ToList(), 0, 0);
        Check("Spiegeln an x = 0: 5 Flaechen dazu, die 4 Ecken auf der Ebene geteilt (8 + 4 Ecken), geschlossen",
            copies.Count == 5 && half.Vertices.Count == verts + 4 && Closed(half), $"{copies.Count} {half.Vertices.Count}");
        bool mirroredOut = copies.All(f => { var n = half.Normal(f); var c = half.Center(f); return MeshModel.Dot(n, MeshModel.Sub(c, new[] { -0.5, 0.5, 0.5 })) > 0; });
        Check("Spiegeln: die Kopien sind umgedreht (ihre Normalen zeigen nach aussen)", mirroredOut);
        var flat = MeshModel.CreateEmpty();
        flat.AddPlane(new[] { 0.0, 0, 0 }, 1, 1, 0, 0);
        Check("Spiegeln: eine Flaeche in der Ebene wird nicht kopiert", flat.Mirror(new[] { 0 }, 1, 0).Count == 0 && flat.Faces.Count == 1);
        var moved = MeshModel.CreateDefault();
        moved.Mirror(Enumerable.Range(0, 6).ToList(), 1, 0, copy: false);
        Check("Spiegeln ohne Kopie: der Wuerfel liegt unter dem Boden, die Normalen zeigen weiter nach aussen",
            moved.Vertices.All(v => v[1] <= 1e-9) && Enumerable.Range(0, 6).All(f => MeshModel.Dot(moved.Normal(f), MeshModel.Sub(moved.Center(f), new[] { 0, -0.5, 0 })) > 0));

        // ---- the other changes ----
        var del = MeshModel.CreateDefault();
        del.DeleteFaces(new[] { 0 });
        Check("Flaeche loeschen: die Ecken bleiben (andere Flaechen nutzen sie)", del.Faces.Count == 5 && del.Vertices.Count == 8);
        del.DeleteVertices(new[] { 0 });
        Check("Ecke loeschen: mit allen Flaechen an ihr (von 5 bleiben 2)", del.Vertices.Count == 7 && del.Faces.Count == 2 && del.Faces.All(f => f.V.All(v => v < 7)), del.Faces.Count.ToString());
        var de = MeshModel.CreateDefault();
        var e = de.Edges()[0];
        de.DeleteEdges(new[] { e });
        Check("Kante loeschen: die zwei Flaechen an ihr gehen", de.Faces.Count == 4);
        var weld = MeshModel.CreateEmpty();
        weld.AddBox(new[] { 0.0, 0, 0 }, new[] { 1.0, 1, 1 }, 0, 0);
        weld.AddBox(new[] { 1.0, 0, 0 }, new[] { 2.0, 1, 1 }, 0, 0);
        Check("Verschweissen: die 4 Ecken an der Beruehrung werden eins", weld.Weld() == 4 && weld.Vertices.Count == 12);
        var merge = MeshModel.CreateDefault();
        int topF = Enumerable.Range(0, 6).First(f => merge.Normal(f)[1] > 0.9);
        merge.Merge(merge.Faces[topF].V);
        Check("Zusammenfuehren: der Deckel wird eine Spitze (eine Pyramide: 5 Ecken, 5 Flaechen)", merge.Vertices.Count == 5 && merge.Faces.Count == 5 && Closed(merge) && merge.Vertices.Any(v => Near(v, 0, 1, 0)));
        var split = MeshModel.CreateDefault();
        int m0 = split.SplitEdge(0, 1);
        Check("Kante teilen: eine Ecke in der Mitte, in beiden Flaechen", Near(split.Vertices[m0], 0, 0, -0.5) && split.Faces.Count(f => f.V.Contains(m0)) == 2 && Closed(split));
        var sf = MeshModel.CreateDefault();
        int f0 = 0;
        var l0 = sf.Faces[f0].V;
        int nf = sf.SplitFace(f0, l0[0], l0[2]);
        Check("Flaeche teilen: zwei Dreiecke", nf == 6 && sf.Faces[f0].V.Count == 3 && sf.Faces[nf].V.Count == 3 && Closed(sf) && sf.SplitFace(f0, l0[0], l0[1]) == -1);
        var hole = MeshModel.CreateDefault();
        int gone = Enumerable.Range(0, 6).First(f => hole.Normal(f)[1] > 0.9);
        var loop = hole.Faces[gone].V.ToList();
        hole.DeleteFaces(new[] { gone });
        loop.Reverse();
        int filled = hole.FaceFromVertices(loop, 9, 0);
        Check("Flaeche aus Ecken: das Loch geschlossen, nach aussen gedreht", hole.Normal(filled)[1] > 0.9 && Closed(hole));
        var rot = MeshModel.CreateDefault();
        rot.Rotate(Enumerable.Range(0, 8), new[] { 0.0, 0, 0 }, 1, 90);
        Check("Drehen um y (90 Grad, rechtshaendig: x -> -z): (0.5, 0, -0.5) -> (-0.5, 0, -0.5)", Near(rot.Vertices[1], -0.5, 0, -0.5), string.Join(", ", rot.Vertices[1]));
        var colors = MeshModel.CreateDefault();
        colors.RemoveColor(3);
        Check("Farbe entfernt: die Farben danach ruecken auf", colors.Faces.All(f => f.Color == 8) && colors.Palette.Count == 15);
        var parts = MeshModel.Parse(House());
        parts.RemovePart(1);
        Check("Teil entfernt: seine Flaechen gehen zum Eltern-Teil, seine Schluessel gehen", parts.Faces.All(f => f.Part == 0) && parts.Animations[0].Keys.Count == 0);

        // ---- bodies ----
        var cyl = MeshModel.CreateEmpty();
        cyl.AddCylinder(new[] { 0.0, 0, 0 }, 0.5, 2, 8, 1, 0);
        Check("Zylinder: 8 Seiten, Deckel und Boden, geschlossen, Normalen nach aussen", cyl.Faces.Count == 10 && cyl.Vertices.Count == 16 && Closed(cyl) && Outward(cyl, new[] { 0, 1.0, 0 }));
        var sph = MeshModel.CreateEmpty();
        sph.AddSphere(new[] { 0.0, 1, 0 }, 1, 8, 4, 1, 0);
        Check("Kugel: 8 x 4 (Dreiecke an den Polen), geschlossen, Normalen nach aussen", sph.Faces.Count == 8 * 4 && sph.Vertices.Count == 2 + 8 * 3 && Closed(sph) && Outward(sph, new[] { 0, 1.0, 0 }));

        // ---- picking ----
        Check("Strahl von oben trifft den Deckel", cube.Raycast(new[] { 0.1, 5, 0.2 }, new[] { 0.0, -1, 0 }, out int hit, out double t) && cube.Normal(hit)[1] > 0.9 && Math.Abs(t - 4) < 1e-9, $"{hit} {t}");
        Check("Strahl von innen trifft die Rueckseite einer Flaeche (beide Seiten)", cube.Raycast(new[] { 0.0, 0.5, 0 }, new[] { 1.0, 0, 0 }, out hit, out t) && cube.Normal(hit)[0] > 0.9 && Math.Abs(t - 0.5) < 1e-9);
        Check("Strahl vorbei: nichts", !cube.Raycast(new[] { 3.0, 5, 3 }, new[] { 0.0, -1, 0 }, out _, out _));

        // ---- the model file ----
        var house = MeshModel.Parse(House());
        Check("Haus: Text hin und zurueck (Teile, Animation)", house.ToJson() == House() && house.Parts.Count == 2 && house.Animations.Count == 1);
        Check("Haus: geschlossen, Normalen nach aussen", Closed(house) && Outward(house, new[] { 0, 0.6, 0 }));
        byte[] f3d = house.ToF3d();
        Check("Modelldatei: F3D1, Teile body/roof und je Farbe ein Teil darunter (4), ein Clip", f3d[0] == 'F' && BitConverter.ToUInt16(f3d, 6) == 4 && BitConverter.ToUInt16(f3d, 8) == 1, BitConverter.ToUInt16(f3d, 6).ToString());
        Check("Modelldatei: die Box ist das Haus (x -1 .. 1, y 0 .. 1.8)", Math.Abs(BitConverter.ToSingle(f3d, 12) + 1) < 1e-6 && Math.Abs(BitConverter.ToSingle(f3d, 16)) < 1e-6 && Math.Abs(BitConverter.ToSingle(f3d, 28) - 1.8f) < 1e-6,
            string.Join(" ", Enumerable.Range(0, 6).Select(i => BitConverter.ToSingle(f3d, 12 + 4 * i))));
        Check("forge: ConvertFile nimmt .fmesh", ModelConverter.IsModelSource("a.fmesh") && ModelConverter.ConvertMesh(House()).SequenceEqual(f3d));

        Console.WriteLine(_failures == 0 ? "Alle Netzmodell-Pruefungen bestanden." : $"FEHLER: {_failures} Netzmodell-Pruefung(en) fehlgeschlagen.");
        return _failures;
    }

    /// <summary>Every directed edge once and its reverse once: a closed surface whose faces agree on their side.</summary>
    private static bool Closed(MeshModel m)
    {
        var directed = new Dictionary<(int, int), int>();
        foreach (var f in m.Faces) for (int i = 0; i < f.V.Count; i++) { var e = (f.V[i], f.V[(i + 1) % f.V.Count]); directed[e] = directed.GetValueOrDefault(e) + 1; }
        return directed.All(p => p.Value == 1 && directed.GetValueOrDefault((p.Key.Item2, p.Key.Item1)) == 1);
    }

    private static bool Outward(MeshModel m, double[] inside) =>
        Enumerable.Range(0, m.Faces.Count).All(f => MeshModel.Dot(m.Normal(f), MeshModel.Sub(m.Center(f), inside)) > 0);

    private static bool Throws(Action a) { try { a(); return false; } catch (InvalidDataException) { return true; } }
}
