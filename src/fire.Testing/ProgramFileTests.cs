using fire.Bytecode;
using fire.Compiler;
using fire.Projects;
using fire.Runtime;
using fire.Values;

/// <summary>The program file `.fbc` (ProgramFile, docs/spec/BYTECODE.md "Das Dateiformat .fbc") and the text of a program (ProgramDump, `forge dump`), alone with
/// FIRE_TESTS_ONLY=programfile: a program that is written and read back is the program it was (the same text, the same bytes when written again, the same output
/// when it runs), the compiler gives the same bytes - and the native backend the same C - for the same sources, and a damaged file is refused. The programs: a script with a bit of everything and the
/// samples of spark (docs/ROADMAP_C_PORT.md, phase A 1 and 3).</summary>
static class ProgramFileTests
{
    private static int _failures;

    private static void Check(string title, bool ok, string? detail = null)
    {
        if (!ok) _failures++;
        Console.WriteLine(ok ? $"OK: Programmdatei: {title}" : $"FEHLER: Programmdatei: {title}" + (string.IsNullOrEmpty(detail) ? "" : $"\n{detail}"));
    }

    private const string Everything = """
        prefixed unit Volt: V
        unit Inch: in { coerce m => 0.0254 }
        unit Kelvin: K
        unit Celsius: C { coerce K => value + 273.15 }
        unit Decibel: dB {
            coerce to unitless => 10 ^ (value / 20)
            coerce from unitless => 20 * Math.Log10(value)
        }
        enum Color { Red, Green = 5, Blue }
        interface IShape { float Area() }
        class Animal {
            string name
            static int count = 0
            construct(string name) { this.name = name; Animal.count++ }
            string Sound() { return "..." }
            string ToString() { return this.name + " says " + this.Sound() }
        }
        class Dog : Animal {
            construct(string name) : base(name) { }
            override string Sound() { return "Woof" }
        }
        class Square : IShape {
            float side = 1.0
            construct(float side) { this.side = side }
            float Area() { return this.side * this.side }
        }
        record Point {
            int x
            int y
            construct(int x, int y) { this.x = x; this.y = y }
            int Sum() { return this.x + this.y }
        }
        class Box<T> {
            T inside
            construct(T v) { this.inside = v }
            T Get() { return this.inside }
        }
        class BadInput : Exception {
            construct(string message) : base(message) { }
        }
        class Maths {
            static int Divide(int a, int b = 2) {
                if (b == 0) { throw new BadInput("division by zero") }
                return a / b
            }
        }
        var animals = new List()
        animals.Add(new Dog("Rex"))
        animals.Add(new Animal("Tom"))
        foreach (var a in animals) { print(a) }
        print(Animal.count)
        print(new Square(3.0).Area())
        print(new Point(1, 2) == new Point(1, 2))
        print(new Box<int>(7).Get())
        print(Color.Green)
        print(5V + 300mV)
        print(100.0C:K)
        print(20dB:unitless)
        print(10in)
        var twice = (int x) => x * 2
        print(twice(21))
        try {
            print(Maths.Divide(10))
            print(Maths.Divide(1, 0))
        } catch (BadInput e) {
            print("caught: " + e.message)
        } finally {
            print("done")
        }
        print($"{3.14159:F2} {'x'} {"ä中"} {255:X}")
        print(-0.0)
        print(9223372036854775807)
        """;

    private static List<string> Output(Func<Func<Value[], Value>, Action> build)
    {
        var lines = new List<string>();
        try
        {
            build(args => { lock (lines) lines.Add(args.Length > 0 ? args[0].ToString()! : ""); return Value.MakeUndefined(); })();
        }
        catch (Exception ex) { lines.Add("AUSNAHME " + ex.GetType().Name + ": " + ex.Message); }
        VM.ResetTerminateForTests();
        Value.SingleFloats = false; Value.Ints32 = false; Value.Ints16 = false;
        return lines;
    }

    /// <summary>What must hold for every program: the text and the bytes survive the file.</summary>
    private static void RoundTrip(string title, Func<LinkedProgram> link)
    {
        LinkedProgram first;
        try { first = link(); }
        catch (Exception ex) { Check($"{title}: wird uebersetzt", false, string.Join("\n", CompileDiagnostics.Of(ex).Select(d => d.Code + " " + d.Message).DefaultIfEmpty(ex.ToString()))); return; }
        try
        {
            byte[] bytes = ProgramFile.Write(first);
            string text = ProgramDump.Text(first);
            var back = ProgramFile.Read(bytes);
            string textBack = ProgramDump.Text(back);
            Check($"{title}: gelesen ist es dasselbe Programm (Text von {text.Length} Zeichen)", text == textBack, FirstDifference(text, textBack));
            Check($"{title}: noch einmal geschrieben sind es dieselben {bytes.Length} Bytes", ProgramFile.Write(back).AsSpan().SequenceEqual(bytes));
            // without the line tables: the same program otherwise
            var bare = ProgramFile.Read(ProgramFile.Write(first, lineTables: false));
            Check($"{title}: ohne Zeilentabellen bleibt der Rest", ProgramDump.Text(bare, lineTables: false) == ProgramDump.Text(first, lineTables: false) && !ProgramDump.Text(bare).Contains("\n" + new string(' ', 4) + "line ") );
            // the compiler again: the same bytes (nothing in a program depends on the run that made it)
            byte[] again = ProgramFile.Write(link());
            Check($"{title}: zweimal uebersetzt sind es dieselben Bytes", again.AsSpan().SequenceEqual(bytes), again.AsSpan().SequenceEqual(bytes) ? "" : FirstDifference(text, ProgramDump.Text(ProgramFile.Read(again))));
        }
        catch (Exception ex) { Check($"{title}: schreiben und lesen", false, ex.ToString()); }
    }

    private static string FirstDifference(string a, string b)
    {
        if (a == b) return "";
        var la = a.Split('\n'); var lb = b.Split('\n');
        for (int i = 0; i < Math.Min(la.Length, lb.Length); i++)
            if (la[i] != lb[i]) return $"  Zeile {i + 1}:\n  vorher:  {la[i]}\n  nachher: {lb[i]}";
        return $"  {la.Length} Zeilen vorher, {lb.Length} nachher";
    }

    public static int Run()
    {
        _failures = 0;

        // 1) a script with a bit of everything: the file, and the program of the file runs like the one the compiler made
        RoundTrip("alles", () => new Linker().CompileAndLink(new[] { Everything }));
        {
            var direct = Output(writer => RuntimeSession.Build(new[] { Everything }, VmExecutionMode.Release, writer).Run);
            var linked = new Linker().CompileAndLink(new[] { Everything }, null, null, VmExecutionMode.Release);
            var restored = ProgramFile.Read(ProgramFile.Write(linked));
            var fromFile = Output(writer => Session.Build(restored, VmExecutionMode.Release, writer).Run);
            Check("alles: das Programm aus der Datei laeuft wie das uebersetzte", direct.Count == 18 && !direct.Any(l => l.StartsWith("AUSNAHME")) && direct.SequenceEqual(fromFile),
                "  uebersetzt:\n    " + string.Join("\n    ", direct) + "\n  aus der Datei:\n    " + string.Join("\n    ", fromFile));
            // the line tables: where a statement of the program begins - there with them, gone without them
            var lined = ProgramFile.Read(ProgramFile.Write(linked, lineTables: true)).Program.TopLevel;
            var bare = ProgramFile.Read(ProgramFile.Write(linked, lineTables: false)).Program.TopLevel;
            Check("die Zeilentabelle eines Chunks kommt gleich zurueck", lined.Lines.Any() && lined.Lines.SequenceEqual(linked.Program.TopLevel.Lines)
                && Enumerable.Range(0, linked.Program.TopLevel.Code.Count).All(ip => lined.GetLocation(ip) == linked.Program.TopLevel.GetLocation(ip)));
            Check("ohne Zeilentabellen hat das Programm keine Zeilen", !bare.Lines.Any() && bare.Code.SequenceEqual(linked.Program.TopLevel.Code));
        }

        // 2) what is shared stays shared, what a file cannot hold is refused
        {
            var linked = new Linker().CompileAndLink(new[] { Everything });
            var back = ProgramFile.Read(ProgramFile.Write(linked));
            var dog = back.Program.Classes["Dog"];
            Check("eine Basisklasse ist die Klasse der Tabelle (ein Objekt, nicht zwei)", ReferenceEquals(dog.Base, back.Program.Classes["Animal"]));
            Check("die Funktionen einer Klasse kennen ihre Klasse wieder", dog.Methods.Values.All(o => o.All(m => ReferenceEquals(m.Chunk.OwnerClass, dog))));
            Check("die Enums des Programms sind wieder bekannt", back.Program.Enums != null && back.Program.Enums.ContainsKey("Color") && back.Program.Enums["Color"].Members.Count == 3);
            Check("die Einheiten des Programms kommen mit", back.Program.CustomUnits.Count == linked.Program.CustomUnits.Count && back.Program.CustomUnits.SequenceEqual(linked.Program.CustomUnits));

            byte[] bytes = ProgramFile.Write(linked);
            string Refused(byte[] file) { try { ProgramFile.Read(file); return ""; } catch (ProgramFileException ex) { return ex.Message; } catch (Exception ex) { return "falsche Ausnahme " + ex.GetType().Name; } }
            Check("eine andere Datei ist kein Programm", Refused(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }).Contains("Not a fire program file") && !ProgramFile.IsProgram(new byte[] { (byte)'F', (byte)'B' }) && ProgramFile.IsProgram(bytes));
            var newer = (byte[])bytes.Clone(); newer[4] = 99;
            Check("eine andere Version des Formats wird mit ihrer Nummer abgelehnt", Refused(newer).Contains("version 99"));
            Check("eine abgeschnittene Datei wird abgelehnt", new[] { 6, 7, 100, bytes.Length / 2, bytes.Length - 1 }.All(n => Refused(bytes.AsSpan(0, n).ToArray()).Contains("damaged")));
            Check("Bytes hinter dem Ende werden abgelehnt", Refused(bytes.Concat(new byte[] { 0 }).ToArray()).Contains("damaged"));
            // every damage is a ProgramFileException or a program, never another exception: a byte changed at many places
            int wrong = 0;
            for (int i = 6; i < bytes.Length; i += Math.Max(1, bytes.Length / 400))
            {
                var hurt = (byte[])bytes.Clone(); hurt[i] ^= 0x5A;
                if (Refused(hurt).StartsWith("falsche Ausnahme")) wrong++;
            }
            Check("eine beschaedigte Datei gibt nie eine andere Ausnahme als die der Programmdatei", wrong == 0, $"  {wrong} Stellen");

            var unit = linked.Program.TopLevel.Units.Concat(ModuleInterfaceChunks(linked).SelectMany(c => c.Units)).FirstOrDefault(u => u.DisplaySymbol != null);
            Check("eine Einheit fuer sich (die Schnittstelle eines Moduls) kommt gleich zurueck", unit != null && ProgramFile.ReadUnit(ProgramFile.WriteUnit(unit)) is var u2 && u2.Equals(unit) && u2.DisplaySymbol == unit.DisplaySymbol && u2.Scale == unit.Scale);
        }

        // 3) the samples of spark: bigger programs with the packages (ui, graphics, scene3d with resources, circuits, threads, the network)
        foreach (var sample in SampleCatalog.Load())
        {
            RoundTrip($"Beispiel {sample.Name}", () =>
            {
                var ws = new Workspace();
                ws.Load(Path.Combine(sample.Folder, sample.ProjectFile));
                var plan = BuildPlan.Create(ws, ws.Projects[0]);
                return new Linker { Plan = plan, SourcePaths = plan.SourcePaths.Cast<string?>().ToList() }.CompileAndLink(plan.SourceTexts);
            });
        }

        // 4) the same for the C of the native backend: translated twice it is the same text (phase A 3) - the script, and the samples as projects
        {
            string Generated(Func<string> generate) { try { return generate(); } catch (Exception ex) { return "AUSNAHME " + ex.GetType().Name + ": " + ex.Message + " " + Guid.NewGuid(); } }
            void Twice(string title, Func<string> generate)
            {
                string a = Generated(generate), b = Generated(generate);
                // (a sample the native backend does not translate is not this test's business: it must only say so both times)
                bool translated = !a.StartsWith("AUSNAHME");
                Check($"{title}: zweimal nach C uebersetzt ist es derselbe Text" + (translated ? $" ({a.Length} Zeichen)" : " (wird nicht uebersetzt)"),
                    translated ? a == b : b.StartsWith("AUSNAHME"), translated ? FirstDifference(a, b) : "");
            }
            Twice("alles", () => NativeBuilder.GenerateLinked(new[] { Everything }, TargetProfile.Host).C);
            foreach (var sample in SampleCatalog.Load())
                Twice($"Beispiel {sample.Name}", () =>
                {
                    var ws = new Workspace();
                    ws.Load(Path.Combine(sample.Folder, sample.ProjectFile));
                    var plan = BuildPlan.Create(ws, ws.Projects[0]);
                    return NativeBuilder.GenerateLinked(plan.SourceTexts, TargetProfile.Host, plan: plan, sourcePaths: plan.SourcePaths.Cast<string?>().ToList()).C;
                });
        }

        Console.WriteLine(_failures == 0 ? "Alle Pruefungen der Programmdatei bestanden." : $"FEHLER: {_failures} Pruefung(en) der Programmdatei fehlgeschlagen.");
        return _failures;
    }

    private static List<Chunk> ModuleInterfaceChunks(LinkedProgram linked) => PluginBytecode.Chunks(linked.Program);
}
