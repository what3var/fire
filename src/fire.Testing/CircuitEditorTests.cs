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
            s 240 48 352 48 0 0 false
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
        var falstad5 = FalstadImport.Convert("""
            $ 1 0.000005 10.2 50 5 50 5e-11
            174 304 416 272 368 1 100000 0.5 Resistance
            T 288 192 336 224 6 4.0000000000000003e-7 6 0.0008897636271907768 -0.08891607989108269 0.999
            156 80 224 128 224 0 5
            182 160 240 224 240 0 0.5 1 4 5 0
            178 624 304 752 304 22 1 0.2 -0.020210235015409483 0.05 1000000 0.02 20 0.015 0.005 2
            """);
        Check("Falstad: Potentiometer, Transformator, JK-Flipflop, Schmitt-Trigger und Relais",
            falstad5.Elements == 5 && falstad5.Warnings.Count == 0
            && falstad5.Biscuit.Contains("{\"type\": \"Potentiometer\", \"from\": [38, 52], \"to\": [34, 46], \"resistance\": \"100k\"}")
            && falstad5.Biscuit.Contains("{\"type\": \"Transformer\", \"from\": [36, 24], \"to\": [42, 24], \"inductance\": \"400n\", \"ratio\": \"6\", \"reverse\": \"true\"}")
            && falstad5.Biscuit.Contains("{\"type\": \"JKFlipFlop\", \"from\": [10, 28], \"to\": [22, 28], \"falling\": \"true\", \"spacing\": \"4\"}")
            && falstad5.Biscuit.Contains("{\"type\": \"SchmittTrigger\", \"from\": [20, 30], \"to\": [28, 30], \"lower\": \"1\", \"upper\": \"4\"}")
            && falstad5.Biscuit.Contains("{\"type\": \"Relay\", \"from\": [78, 38], \"to\": [94, 38], \"coil\": \"both\"}"), falstad5.Biscuit + string.Join(" | ", falstad5.Warnings));
        var falstad6 = FalstadImport.Convert("""
            $ 1 0.000005 10.2 50 5 50 5e-11
            S 112 208 192 208 0 1 false 0 2
            181 688 208 688 496 0 293 100 120 0.4 0.4
            171 176 240 496 240 0 0.00000003 75.0 80 0.0
            159 144 112 208 112 0
            160 112 104 112 168 0
            170 240 208 192 208 3 10.0 2000.0 5.0 0.1
            172 272 288 192 288 0 6 4.5 5.0 0.0 0.0 0.5 Y Voltage
            m 208 128 256 80 0 100.0 250000.0 0 1.0E-8 1.0E-10
            187 256 144 352 256 0 1000.0 1.0E9 4000.0 0.0015
            R 304 288 304 336 0 3 20 2 3 0 0.5
            82 0 0 0 40 0 0 40 5 0 0 0.5
            """);
        Check("Falstad: Umschalter, Lampe, Leitung, Analogschalter, Sweep, Schieberail, Memristor, Funkenstrecke, Dreieck, Typ als Zahl",
            falstad6.Elements == 11 && falstad6.Warnings.Count == 0
            && falstad6.Biscuit.Contains("{\"type\": \"Switch2\", \"from\": [14, 26], \"to\": [24, 26], \"position\": \"1\"}")
            && falstad6.Biscuit.Contains("\"type\": \"Lamp\", \"from\": [86, 26], \"to\": [86, 62], \"power\": \"100\", \"voltage\": \"120\"")
            && falstad6.Biscuit.Contains("{\"type\": \"TransmissionLine\", \"from\": [22, 30], \"to\": [62, 30], \"delay\": \"30n\", \"impedance\": \"75\", \"width\": \"10\"}")
            && falstad6.Biscuit.Contains("{\"type\": \"AnalogSwitch\", \"from\": [18, 14], \"to\": [26, 14]}")
            && falstad6.Biscuit.Contains("{\"type\": \"AnalogSwitch2\", \"from\": [14, 13], \"to\": [14, 21]}")
            && falstad6.Biscuit.Contains("{\"type\": \"Sweep\", \"from\": [30, 26], \"to\": [24, 26], \"minFrequency\": \"10\", \"maxFrequency\": \"2k\", \"amplitude\": \"5\", \"sweepTime\": \"100m\", \"logarithmic\": \"true\"}")
            && falstad6.Biscuit.Contains("{\"type\": \"Rail\", \"from\": [34, 36], \"to\": [24, 36], \"voltage\": \"4.5\"}")
            && falstad6.Biscuit.Contains("\"type\": \"Memristor\", \"from\": [26, 16], \"to\": [32, 10], \"onResistance\": \"100\", \"offResistance\": \"250k\"")
            && falstad6.Biscuit.Contains("\"type\": \"SparkGap\", \"from\": [32, 18], \"to\": [44, 32], \"onResistance\": \"1k\", \"offResistance\": \"1G\", \"breakdown\": \"4k\", \"holdCurrent\": \"1.5m\"")
            && falstad6.Biscuit.Contains("{\"type\": \"Rail\", \"from\": [38, 36], \"to\": [38, 42], \"waveform\": \"triangle\", \"voltage\": \"2\", \"frequency\": \"20\", \"offset\": \"3\"}")
            && falstad6.Biscuit.Contains("{\"type\": \"Rail\", \"from\": [0, 0], \"to\": [0, 5], \"voltage\": \"5\"}"), falstad6.Biscuit + string.Join(" | ", falstad6.Warnings));
        var falstad7 = FalstadImport.Convert("""
            $ 1 0.000005 10.2 50 5 50 5e-11
            177 384 160 368 288 0 0.0 0.0
            206 1360 272 1312 528 0 0.011 0.008 100 false
            203 1264 432 1328 432 0 500 100000000 30 0.01
            j 240 176 272 176 1 -4 0.00125
            400 592 496 656 496 0 0_1_-9.1_0.47_100 0_1_-9.6_0.58_100 -1
            173 304 240 368 240 0 93.0 680.0
            176 560 256 560 352 1 0.805904783 -0.9415790148957013 1e-11
            175 320 208 320 288 0
            179 272 224 304 224 0 -1.0
            212 1456 320 1504 320 0 2 (a-b)*10
            213 976 528 1008 528 0 2 a*b*2/1000
            402 512 528 624 528 0 0_0_40_-12_0_0_0.5 0_0_40_12_0_0_0.5
            407 288 192 320 192 1
            """);
        Check("Falstad: Thyristor, Triac, Diac, JFET, Darlington, Triode, Varaktor, Tunneldiode, Stromkonverter, gesteuerte Quelle, OTA, Optokoppler",
            falstad7.Elements == 12 && falstad7.Warnings.Count == 1 && falstad7.Warnings[0].Contains("'a*b*2/1000' is not linear")
            && falstad7.Biscuit.Contains("{\"type\": \"Thyristor\", \"from\": [48, 20], \"to\": [46, 36], \"legacy\": \"true\"}")
            && falstad7.Biscuit.Contains("{\"type\": \"Triac\", \"from\": [170, 34], \"to\": [164, 66], \"triggerCurrent\": \"11m\", \"holdingCurrent\": \"8m\"}")
            && falstad7.Biscuit.Contains("\"type\": \"Diac\", \"from\": [158, 54], \"to\": [166, 54], \"onResistance\": \"500\"")
            && falstad7.Biscuit.Contains("{\"type\": \"Jfet\", \"from\": [30, 22], \"to\": [34, 22], \"pChannel\": \"true\"}")
            && falstad7.Biscuit.Contains("{\"type\": \"Darlington\", \"from\": [74, 62], \"to\": [82, 62], \"model\": \"pnp\"}")
            && falstad7.Biscuit.Contains("{\"type\": \"Triode\", \"from\": [38, 30], \"to\": [46, 30]}")
            && falstad7.Biscuit.Contains("{\"type\": \"Varactor\", \"from\": [70, 32], \"to\": [70, 44], \"capacitance\": \"10p\"}")
            && falstad7.Biscuit.Contains("{\"type\": \"TunnelDiode\", \"from\": [40, 26], \"to\": [40, 36]}")
            && falstad7.Biscuit.Contains("{\"type\": \"CurrentConveyor\", \"from\": [34, 28], \"to\": [46, 28], \"gain\": \"-1\", \"spacing\": \"4\"}")
            && falstad7.Biscuit.Contains("{\"type\": \"VCVS\", \"from\": [182, 40], \"to\": [194, 40], \"gain\": \"10\", \"spacing\": \"4\"}")
            && falstad7.Biscuit.Contains("{\"type\": \"Ota\", \"from\": [64, 66], \"to\": [78, 66], \"positive\": \"12\", \"negative\": \"-12\"}")
            && falstad7.Biscuit.Contains("{\"type\": \"Optocoupler\", \"from\": [36, 24], \"to\": [48, 24]}"), falstad7.Biscuit + string.Join(" | ", falstad7.Warnings));
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
