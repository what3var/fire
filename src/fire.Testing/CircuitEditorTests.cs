using fire.Compiler;
using fire.Runtime;
using fire.UI.Markup;
using fire.Values;

/// <summary>The editors of circuits (docs/CIRCUITS.md): the generator of `.biscuit` files, the import of Falstad circuits, the CircuitView of the package circuits.ui
/// (driven with mouse and keys in the virtual machine) and the live session that spark runs it in.</summary>
static class CircuitEditorTests
{
    private static int _failures;

    private static void Check(string title, bool ok, string detail = "")
    {
        if (!ok) _failures++;
        Console.WriteLine(ok ? $"OK: Schaltungseditor: {title}" : $"FEHLER: Schaltungseditor: {title}" + (detail != "" ? "\n  " + detail : ""));
    }

    private static void CheckLines(string title, IReadOnlyList<string> actual, string[] expected)
    {
        bool ok = actual.SequenceEqual(expected);
        Check(title, ok, ok ? "" : $"erwartet: {string.Join(" | ", expected)}\n  erhalten: {string.Join(" | ", actual)}");
    }

    /// <summary>Runs a script in the VM with a window that shows nothing; the printed lines.</summary>
    private static List<string> Run(string script)
    {
        var lines = new List<string>();
        try
        {
            var session = RuntimeSession.Build(new[] { script }, VmExecutionMode.Release, a => { lines.Add(a[0].ToString() ?? ""); return Value.MakeUndefined(); },
                windowRenderer: () => new FakeRenderer());
            try { session.Run(); }
            finally { session.CloseHostResources(); }
            if (session.VirtualMachine?.UnhandledException != null) lines.Add("UNHANDLED: " + new fire.Bytecode.UncaughtScriptException(session.VirtualMachine.UnhandledException).Message);
        }
        catch (Exception ex) { lines.Add("AUSNAHME: " + CompileErrors.Describe(ex)); }
        return lines;
    }

    public static void Run()
    {
        Console.WriteLine("=== Schaltungseditor (.biscuit, Falstad-Import, CircuitView, Live-Sitzung) ===");

        // ---- the generator ----
        string Errors(string text)
        {
            try { BiscuitGenerator.Generate(text, "X.biscuit"); return ""; }
            catch (MarkupException ex) { return string.Join(" | ", ex.Diagnostics.Select(d => d.ToString())); }
        }
        var generated = BiscuitGenerator.Generate("""{"biscuit": 1, "namespace": "Lab", "elements": [{"type": "Resistor", "name": "r1", "from": [0, 0], "to": [8, 0]}, {"type": "Scope", "name": "s", "from": [0, 4], "to": [8, 8], "probe": "r1"}]}""", "my amp.biscuit");
        Check("Generator: Klasse nach dem Dateinamen, Namespace, typisierte Felder, OnBuilt setzt sie",
            generated.Contains("namespace Lab {") && generated.Contains("class my_amp : Circuits.Circuit {") && generated.Contains("Circuits.Resistor r1") && generated.Contains("Circuits.Scope s")
            && generated.Contains("undefined OnBuilt()") && generated.Contains("this.LoadSchematic(\""), generated);
        Check("Generator: kaputtes JSON mit Zeile",
            Errors("{\n  \"biscuit\": 1,\n  \"elements\": [\n    {\"type\": \"Wire\" \"from\": [0, 0]}\n  ]\n}").StartsWith("line 4:"), Errors("{\n  \"biscuit\": 1,\n  \"elements\": [\n    {\"type\": \"Wire\" \"from\": [0, 0]}\n  ]\n}"));
        string many = Errors("""
            {"elements": [
              {"type": "Resistr", "from": [0, 0], "to": [8, 0]},
              {"type": "Resistor", "name": "parts", "from": [0, 0], "to": [8, 0]},
              {"type": "Resistor", "name": "a", "from": [0, 0], "to": [8, 0]},
              {"type": "Resistor", "name": "a", "from": [0, 0], "to": [8, 0]},
              {"type": "Wire", "from": [0, 0]},
              {"type": "Wire", "name": "class", "from": [0, 0], "to": [1, 1.5]}
            ]}
            """);
        Check("Generator: unbekannter Typ, Name eines Members, doppelter Name, fehlende Punkte, Schluesselwort - alle mit Zeile",
            many.Contains("line 2: There is no element 'Resistr'") && many.Contains("line 3: 'parts' is a member") && many.Contains("line 5: Two elements are named 'a'")
            && many.Contains("line 6: The Wire needs 'from' and 'to'") && many.Contains("line 7: 'class' is no name"), many);
        Check("Dateien: .biscuit wird wie .fxml als erzeugtes Skript uebersetzt (Projekte, #include)",
            fire.Projects.FireProject.IsSourceFile("a/b.biscuit") && fire.Projects.FireProject.IsMarkupFile("b.biscuit") && fire.Projects.FireProject.IsCircuitFile("b.biscuit")
            && !fire.Projects.FireProject.IsUiMarkupFile("b.biscuit") && MarkupFiles.GenerateScript("b.biscuit", """{"elements": []}""").Contains("class b : Circuits.Circuit"));

        // ---- the import of Falstad circuits ----
        var falstad = FalstadImport.Convert("""
            $ 1 0.000005 10.2 50 5 50 5e-11
            v 112 368 112 48 0 0 40 5 0 0 0.5
            r 112 48 240 48 0 1000
            c 240 48 240 368 0 0.000001 0 0.001
            w 112 368 240 368 0
            g 112 368 112 400 0 0
            s 240 48 352 48 0 1 false
            162 352 48 352 368 2 default-led 1 0 0 0.01
            t 400 208 464 208 0 -1 -0.6 0.6 100 default
            a 400 304 528 304 9 15 -15 1000000 0 0 100000
            150 560 208 672 208 0 2 0 5
            L 560 304 512 304 0 1 false 5 0
            207 112 48 112 16 4 VCC
            165 600 400 664 400 2 5
            o 2 64 0 4099 5 0.1 0 2 2 3
            38 1 0 1 101 Resistance
            """, "Demo");
        Check("Falstad: Elemente, Punkte durch 8, Werte mit Praefix, Schalter, LED-Farbe, PNP, Op-Amp vertauscht, Gatter, Logikeingang, Label, Scope",
            falstad.Elements == 13 && falstad.Biscuit.Contains("\"class\": \"Demo\"") && falstad.Biscuit.Contains("\"timeStep\": \"5us\"")
            && falstad.Biscuit.Contains("{\"type\": \"Source\", \"from\": [14, 46], \"to\": [14, 6], \"voltage\": \"5\"}")
            && falstad.Biscuit.Contains("\"type\": \"Resistor\", \"from\": [14, 6], \"to\": [30, 6], \"resistance\": \"1k\"")
            && falstad.Biscuit.Contains("\"capacitance\": \"1u\"") && falstad.Biscuit.Contains("\"type\": \"Switch\", \"from\": [30, 6], \"to\": [44, 6], \"closed\": \"true\"")
            && falstad.Biscuit.Contains("\"type\": \"Led\", \"from\": [44, 6], \"to\": [44, 46], \"color\": \"red\"") && falstad.Biscuit.Contains("\"model\": \"pnp\"")
            && falstad.Biscuit.Contains("\"type\": \"OpAmp\", \"from\": [50, 38], \"to\": [66, 38], \"model\": \"opamp\", \"flip\": \"true\"")
            && falstad.Biscuit.Contains("\"type\": \"And\"") && falstad.Biscuit.Contains("\"type\": \"LogicInput\", \"from\": [70, 38], \"to\": [64, 38], \"high\": \"true\"")
            && falstad.Biscuit.Contains("\"type\": \"Label\", \"from\": [14, 6], \"to\": [14, 2], \"text\": \"VCC\"")
            && falstad.Biscuit.Contains("\"type\": \"Scope\"") && falstad.Biscuit.Contains("\"probe\": \"C1\""), falstad.Biscuit);
        Check("Falstad: was das Paket nicht hat, wird genannt", falstad.Warnings.Count == 1 && falstad.Warnings[0].Contains("'165' (555 timer)"), string.Join(" | ", falstad.Warnings));
        CheckLines("Falstad: die uebersetzte Schaltung laeuft (Kondensator laedt ueber 1k)", Run(
            "#import \"circuits\"\n" +
            "var c = new Circuits.Circuit(\"x\")\n" +
            "c.LoadSchematic(" + BiscuitGenerator.Quote(falstad.Biscuit) + ")\n" +
            "c.Run(2ms)\n" +
            "var bad = 0\n" +
            "foreach (e in c.Schematic.Elements()) { if (e.Error != \"\") { bad = bad + 1 } }\n" +
            "print(\"errors \" + bad + \" vcc \" + Math.Round(c.Schematic.Elements()[11].PinVoltage(0)) + \" recorded \" + (c.Schematic.Elements()[12].scope.Count > 0))\n"), new[] { "errors 0 vcc 5 recorded True" });

        // ---- the view: placing, wires, moving, undo, switches, the editor of properties ----
        CheckLines("CircuitView: platzieren, Draht vom Pin, verschieben, Undo, Drehen, Loeschen, Schalter, Eigenschaften", Run("""
            #import "circuits.ui"
            var fb = new Framebuffer(500, 360)
            var win = new Window(fb, "Test")
            var ui = new UI.Root(fb, win)
            ui.Scaling = 1.0
            var c = new Circuits.Circuit("edit")
            var view = new UI.CircuitView(c, 0, 0, 500, 360)
            ui.Add(view)
            ui.Update()
            var changes = 0
            view.onChange = func (v) => { changes = changes + 1 }
            class P { static int X(int u) { return 40 + u * 10 } }
            view.BeginPlace("Source")
            ui.MouseDown(1, P.X(0), P.X(12))
            ui.MouseMove(P.X(0), P.X(0))
            ui.MouseUp(1, P.X(0), P.X(0))
            view.BeginPlace("Resistor")
            ui.MouseDown(1, P.X(0), P.X(0))
            ui.MouseUp(1, P.X(0), P.X(0))
            view.BeginPlace("Led")
            ui.MouseDown(1, P.X(8), P.X(0))
            ui.MouseMove(P.X(8), P.X(12))
            ui.MouseUp(1, P.X(8), P.X(12))
            view.CancelPlace()
            ui.MouseDown(1, P.X(8), P.X(12))
            ui.MouseMove(P.X(0), P.X(12))
            ui.MouseUp(1, P.X(0), P.X(12))
            view.BeginPlace("Ground")
            ui.MouseDown(1, P.X(0), P.X(12))
            ui.MouseMove(P.X(0), P.X(16))
            ui.MouseUp(1, P.X(0), P.X(16))
            view.CancelPlace()
            for (int i = 0; i < 5; i++) { ui.Update() }
            var s = c.Schematic
            foreach (e in s.Elements()) { print(e.Type + " " + e.Name + " " + e.x1 + "," + e.y1 + " " + e.x2 + "," + e.y2) }
            Circuits.Led led = s.Find("D1").part!Circuits.Led
            print("changes " + changes + " led " + led.IsOn + " " + Math.Round(led.Current / 1mA) + " mA")
            ui.MouseDown(1, P.X(4), P.X(0))
            ui.MouseMove(P.X(4), P.X(4))
            ui.MouseUp(1, P.X(4), P.X(4))
            for (int i = 0; i < 5; i++) { ui.Update() }
            print("moved " + s.Find("R1").y1 + " led " + led.IsOn)
            ui.KeyDown(122, 64)
            for (int i = 0; i < 5; i++) { ui.Update() }
            led = s.Find("D1").part!Circuits.Led
            print("ctrl+z " + s.Find("R1").y1 + " led " + led.IsOn)
            view.Select(s.Find("R1"))
            ui.KeyDown(114, 0)
            var r = s.Find("R1")
            print("r rotates " + r.x1 + "," + r.y1 + " " + r.x2 + "," + r.y2)
            ui.KeyDown(122, 64)
            view.Select(s.Find("D1"))
            ui.KeyDown(127, 0)
            print("del " + s.Elements().count)
            ui.KeyDown(122, 64)
            view.BeginPlace("Switch")
            ui.MouseDown(1, P.X(20), P.X(0))
            ui.MouseUp(1, P.X(20), P.X(0))
            view.CancelPlace()
            ui.MouseDown(1, P.X(24), P.X(0))
            ui.MouseUp(1, P.X(24), P.X(0))
            var sw = s.Find("S1")
            print("switch " + sw.part!Circuits.Switch.Closed + " " + sw.Get("closed"))
            ui.MouseDown(1, P.X(4), P.X(0))
            ui.MouseUp(1, P.X(4), P.X(0))
            ui.MouseDown(1, P.X(4), P.X(0))
            ui.MouseUp(1, P.X(4), P.X(0))
            ui.Update()
            var ed = ui.popups[0]!UI.CircuitElementEditor
            ed.fields[0].text = "4.7k"
            ed.nameBox.text = "Rload"
            ed.Accept()
            ui.Update()
            print("editor " + ui.popups.count + " " + s.Find("Rload").Get("resistance") + " " + s.Find("Rload").part!Circuits.Resistor.Resistance)
            view.readOnly = true
            view.BeginPlace("Resistor")
            ui.MouseDown(1, P.X(24), P.X(0))
            ui.MouseUp(1, P.X(24), P.X(0))
            print("read-only: " + s.Elements().count + " elements, switch " + sw.part!Circuits.Switch.Closed)
            ui.Draw()
            """), new[]
        {
            "Source V1 0,12 0,0", "Resistor R1 0,0 8,0", "Led D1 8,0 8,12", "Wire  8,12 0,12", "Ground  0,12 0,16",
            "changes 5 led True 3 mA", "moved 4 led False", "ctrl+z 0 led True", "r rotates 0,0 0,8", "del 4", "switch True true",
            "editor 0 4.7k 4700Ohm", "read-only: 6 elements, switch False",
        });

        // ---- the live session (spark runs the view in it) ----
        using (var live = new LiveSession())
        {
            var messages = new List<string>();
            var gotFrame = new ManualResetEventSlim();
            var gotPong = new ManualResetEventSlim();
            live.Message += m => { lock (messages) messages.Add(m); if (m == "pong 7") gotPong.Set(); };
            live.FrameReady += () => gotFrame.Set();
            live.Start("""
                #import "ui"
                extern string __HostCommand()
                var fb = new Framebuffer(200, 100)
                var win = new Window(fb, "live")
                var ui = new UI.Root(fb, win)
                ui.Scaling = 1.0
                var clicks = 0
                var b = new UI.Button("Hi", 10, 10)
                b.onClick = func () => { clicks = clicks + 1 }
                ui.Add(b)
                while (ui.Tick()) {
                    var cmd = __HostCommand()
                    if (cmd.StartsWith("ping")) { print("pong " + cmd.Substring(5) + " clicks " + clicks) }
                }
                """);
            bool frame = gotFrame.Wait(20000);
            live.MouseMove(20, 20, 0);
            live.MouseButton(1, true, 20, 20);
            live.MouseButton(1, false, 20, 20);
            Thread.Sleep(300);
            live.Send("ping 7");
            var deadline = DateTime.Now.AddSeconds(10);
            string? pong = null;
            while (DateTime.Now < deadline && pong == null)
            {
                lock (messages) pong = messages.FirstOrDefault(m => m.StartsWith("pong"));
                Thread.Sleep(20);
            }
            uint[] pixels = Array.Empty<uint>();
            long number = live.CopyFrame(ref pixels, out int w, out int h);
            Check("Live-Sitzung: Bilder kommen beim Host an, Maus und Befehle beim Programm", frame && pong == "pong 7 clicks 1" && w == 200 && h == 100 && number > 0 && pixels.Length == 20000,
                $"frame {frame}, answer {pong}, {w}x{h}, frame {number}");
            live.Stop();
            Check("Live-Sitzung: Stop beendet das Programm", !live.IsRunning);
        }

        Console.WriteLine(_failures == 0 ? "Alle Schaltungseditor-Pruefungen bestanden." : $"FEHLER: {_failures} Schaltungseditor-Pruefung(en) fehlgeschlagen.");
    }
}
