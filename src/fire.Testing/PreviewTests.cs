using System.Diagnostics;
using fire.Tooling;
/// <summary>
/// spark's previews in `fire` (docs/spec/VM_C.md, "spark auf der C-VM", M1 (4)): the design view of markup (UiPreview: its window on the host's display file,
/// FIRE_DISPLAY_SHM; the places of the elements as print) and the preview of block models (BlockPreview.RenderFrames: scene3d writes PNGs) run as a child
/// process `fire` - the same picture as in the C# VM, the same places, errors, a newer request ends the older one, and how long a picture takes in either.
/// Part of FIRE_TESTS_ONLY=cvmrun (CVmRunTests) and alone FIRE_TESTS_ONLY=previews.
/// </summary>
static class PreviewTests
{
    private static int _failures;

    private static void Check(string title, bool ok, string? detail = null)
    {
        Console.WriteLine($"[{(ok ? "OK" : "FEHLER")}] Vorschau in fire: {title}");
        if (!ok)
        {
            _failures++;
            if (detail != null) Console.WriteLine("    " + detail.Replace("\n", "\n    "));
        }
    }

    private const string Markup = """
        <Window Class="Demo" Title="Demo" Width="320" Height="200">
          <Resources>
            <Style Key="Primary" Target="Button">
              <Setter Property="Background" Value="#3366AA"/>
            </Style>
          </Resources>
          <StackPanel Left="10" Top="10" Width="300" Height="180" Spacing="6">
            <Label Name="title" Text="Hello from the design view"/>
            <TextBox Name="name" Text="{Binding Name}" Width="200"/>
            <Button Name="ok" Text="OK" Style="Primary" Width="80" Height="28"/>
            <CheckBox Name="check" Text="Remember"/>
          </StackPanel>
        </Window>
        """;

    // (M1: the previews run only in `fire`; the C# VM - FIRE_SPARK_VM=dotnet - and the checks against it are gone)
    private static UiPreviewResult Render(fire.UI.Markup.MarkupDocument doc, string? path) => UiPreview.Render(doc, path);

    private static double Median(List<double> values) { values.Sort(); return values.Count == 0 ? 0 : values[values.Count / 2]; }

    public static int Run()
    {
        _failures = 0;
        if (CVmScript.Locate() == null) { Check("fire (die C-VM) gefunden oder gebaut", false); return _failures; }
        string root = Path.Combine(Path.GetTempPath(), "fire-previews-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Design(root);
            Blocks();
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
        Console.WriteLine(_failures == 0 ? "Alle Pruefungen der Vorschau in fire bestanden." : $"FEHLER: {_failures} Pruefung(en) der Vorschau in fire fehlgeschlagen.");
        return _failures;
    }

    private static void Design(string root)
    {
        string path = Path.Combine(root, "Demo.fxml");
        File.WriteAllText(path, Markup);
        var doc = fire.UI.Markup.MarkupParser.Parse(Markup);
        var inFire = Render(doc, path);
        var again = Render(doc, path);
        var shown = doc.AllElements().Where(e => fire.UI.Markup.MarkupSchema.Find(e.Tag) is { IsPart: false }).ToList();
        Check("Entwurfsansicht: laeuft in fire, Bild in der Groesse des Fensters, ein Ort je Element",
            inFire is { Ok: true, Engine: "c", Width: 320, Height: 200 } && inFire.Pixels.Length == 320 * 200 && inFire.Rects.Count == shown.Count,
            inFire.Error ?? $"{inFire.Engine} {inFire.Width}x{inFire.Height} {inFire.Rects.Count}/{shown.Count}");
        if (inFire.Ok && again.Ok)
        {
            int differ = 0;
            for (int i = 0; i < Math.Min(inFire.Pixels.Length, again.Pixels.Length); i++) if (inFire.Pixels[i] != again.Pixels[i]) differ++;
            Check("Entwurfsansicht: ein zweites Bild ist dasselbe (Pixel fuer Pixel, dieselben Orte der Elemente)", differ == 0 && inFire.Pixels.Length == again.Pixels.Length && inFire.Rects.SequenceEqual(again.Rects),
                $"{differ} Pixel verschieden; " + string.Join(" ", inFire.Rects) + "\n" + string.Join(" ", again.Rects));
            int okAt = shown.FindIndex(e => e.Name == "ok");
            var okRect = inFire.Rects.FirstOrDefault(r => r.Index == okAt);
            uint primary = 0x33u | (0x66u << 8) | (0xAAu << 16) | (255u << 24);
            Check("Entwurfsansicht: der Style steckt im Bild aus fire", okRect != null && inFire.Pixels[(okRect.Y + 3) * inFire.Width + okRect.X + 3] == primary,
                okRect == null ? "kein Ort" : $"0x{inFire.Pixels[(okRect.Y + 3) * inFire.Width + okRect.X + 3]:X8}");
        }
        var bad = Render(fire.UI.Markup.MarkupParser.Parse("<Window Class=\"B\"><Label Left=\"abc\"/></Window>"), null);
        Check("Entwurfsansicht: ein Fehler im Markup wird gemeldet (nichts laeuft)", !bad.Ok && bad.Error != null, bad.Error);

        // the latency: a picture after an edit (compile here + the process `fire`)
        var fireTimes = new List<double>();
        var fireCompile = new List<double>();
        var fireRun = new List<double>();
        var fireLink = new List<double>();
        for (int i = 0; i < 6; i++)
        {
            var edited = fire.UI.Markup.MarkupParser.Parse(Markup.Replace("Hello from", "Hello " + i + " from"));
            var w = Stopwatch.StartNew();
            var a = Render(edited, path);
            double ta = w.Elapsed.TotalMilliseconds;
            double link = CVmScript.Last?.LinkMs ?? 0;
            if (i == 0) continue;   // (the first: the libraries and the JIT warm up)
            if (a.Ok) { fireTimes.Add(ta); fireCompile.Add(a.CompileMs); fireRun.Add(a.RunMs); fireLink.Add(link); }
        }
        Console.WriteLine($"    Entwurfsansicht, Median von 5: fire {Median(fireTimes):0} ms (uebersetzen {Median(fireCompile):0}, davon die Programmdatei {Median(fireCompile) - Median(fireLink):0}, Prozess {Median(fireRun):0})");
        // (the script is compiled with the library ui here - most of the time; the process of `fire` - start, load, draw, end - adds little)
        Check("Entwurfsansicht: der Prozess fire (Start, Laden, Zeichnen, Ende) braucht fuer ein Bild unter 150 ms, das Bild nach einer Aenderung unter 1500 ms",
            fireTimes.Count == 5 && Median(fireRun) < 150 && Median(fireTimes) < 1500, string.Join(", ", fireTimes.Select(t => t.ToString("0"))));
    }

    private static void Blocks()
    {
        var model = BlockModel.CreateDefault(8, 8, 8);
        for (int x = 2; x < 6; x++) for (int y = 0; y < 4; y++) for (int z = 2; z < 6; z++) model.Set(x, y, z, 4 + (x + z) % 3);
        var anim = new BlockModel.Animation { Name = "spin", Length = 0.2, Loop = true };
        anim.Keys.Add(new BlockModel.Key { Part = 0, Time = 0 });
        anim.Keys.Add(new BlockModel.Key { Part = 0, Time = 0.1, Rotate = new double[] { 0, 90, 0 } });
        model.Animations.Add(anim);
        byte[] f3d = model.ToF3d();
        double size = model.SizeX * model.BlockSize;
        (List<byte[]>? Frames, string Error, string Engine, double CompileMs, double RunMs) Frames(string engine, string animation, int frames, CancellationToken cancel = default) =>
            BlockPreview.RenderFrames(f3d, size, size, size, animation, false, 35, frames, cancel);   // (only in `fire` since M1: `engine` is always "c")
        static bool Png(byte[] png, int w, int h) => png.Length > 24 && png[1] == 'P' && png[2] == 'N' && png[3] == 'G'
            && (png[16] << 24 | png[17] << 16 | png[18] << 8 | png[19]) == w && (png[20] << 24 | png[21] << 16 | png[22] << 8 | png[23]) == h;

        var w = Stopwatch.StartNew();
        var still = Frames("c", "", 1);
        double fireStill = w.Elapsed.TotalMilliseconds;
        Check("Blockvorschau: laeuft in fire, ein PNG 480x360", still.Engine == "c" && still.Frames is { Count: 1 } f && Png(f[0], 480, 360), still.Error);
        var spin = Frames("c", "spin", 6);
        Check("Blockvorschau: eine Animation in fire, ein Bild je 1/30 s, die Bilder verschieden", spin.Frames is { Count: 6 } s && s.All(p => Png(p, 480, 360)) && !s[0].SequenceEqual(s[3]), spin.Error);
        // warm: the second picture
        w.Restart();
        var again = Frames("c", "", 1);
        double fireWarm = w.Elapsed.TotalMilliseconds;
        w.Restart();
        Frames("c", "spin", 6);
        double fireSpin = w.Elapsed.TotalMilliseconds;
        Console.WriteLine($"    Blockvorschau: ein Bild fire {fireStill:0} ms (warm {fireWarm:0}: uebersetzen {again.CompileMs:0}, Prozess {again.RunMs:0}); 6 Bilder der Animation fire {fireSpin:0} ms");
        Check("Blockvorschau: ein zweites Bild ist dasselbe PNG", still.Frames is { Count: 1 } a && again.Frames is { Count: 1 } b && a[0].SequenceEqual(b[0]), again.Error);

        // a newer choice (another animation, the slider) ends the drawing of the older one: its process goes
        using var cancel = new CancellationTokenSource();
        var job = Task.Run(() => Frames("c", "spin", 240, cancel.Token));
        Thread.Sleep(600);
        w.Restart();
        cancel.Cancel();
        bool ended = job.Wait(10000);
        Check("Blockvorschau: eine neuere Wahl beendet den Prozess der aelteren", ended && job.Result.Frames == null && w.ElapsedMilliseconds < 5000, $"{ended} {w.ElapsedMilliseconds} ms {job.Result.Error}");
        var broken = Frames("c", "", 1);
        Check("Blockvorschau: danach zeichnet fire wieder", broken.Frames is { Count: 1 }, broken.Error);
    }
}
