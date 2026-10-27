using System.Text;
using fire.Compiler;
using fire.Projects;

/// <summary>`forge test` (docs/TESTING.md): attributes, the generated driver, `Testing.Assert`, the report and the folder `testing/` of a project.</summary>
static class TestingTests
{
    private static int _failures;

    private static void Check(string title, bool ok, string? detail = null)
    {
        if (!ok) _failures++;
        Console.WriteLine(ok ? $"OK: Tests: {title}" : $"FEHLER: Tests: {title}" + (detail == null ? "" : $"\n{detail}"));
    }

    private static string Write(string root, string rel, string text)
    {
        string full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        return full;
    }

    private static (int Exit, string Output) Forge(params string[] args)
    {
        var output = new StringWriter();
        int exit = CommandLineRunner.Run(args, output, output);
        fire.Values.Value.SingleFloats = false;
        return (exit, output.ToString().Replace("\r\n", "\n"));
    }

    public static void Run()
    {
        Console.WriteLine("=== forge test ===");
        string root = Path.Combine(Path.GetTempPath(), "fire-testing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Attributes();
            Files(root);
            Explorer(root);
            Project(root);
        }
        catch (Exception ex)
        {
            Check("ohne Ausnahme durchgelaufen", false, ex.ToString());
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
        Console.WriteLine(_failures == 0 ? "Alle Test-Pruefungen bestanden." : $"FEHLER: {_failures} Test-Pruefung(en) fehlgeschlagen.");
    }

    private static void Attributes()
    {
        var program = Parser.ParseMultiple(new[] { new ProcessedSource("""
            class T {
                int count = 0
                [Test "Title", "Text"]
                [Parameters 1..3, {1, 2}, -4, "a|b", new List<int>()]
                undefined M(int a, any b, int c, string d, any e) { }
                [Other]
                undefined N() { }
            }
            """, Array.Empty<string>()) });
        var cls = program.OfType<fire.Ast.ClassDecl>().First(c => c.Name == "T");
        var m = cls.Members.OfType<fire.Ast.MethodDecl>().First(x => x.Name == "M");
        var n = cls.Members.OfType<fire.Ast.MethodDecl>().First(x => x.Name == "N");
        Check("Attribute: Namen und Argumente an der Methode", m.Attributes is { Count: 2 } a && a[0].Name == "Test" && a[0].Args.Count == 2 && a[1].Name == "Parameters" && a[1].Args.Count == 5
            && n.Attributes is [{ Name: "Other", Args.Count: 0 }]);
        Check("Attribute: Argumente als Text ({} wird zu [], Bereich bleibt, -4 ohne Leerzeichen)", m.Attributes![1].ArgTexts.Take(4).SequenceEqual(new[] { "1 .. 3", "[1, 2]", "-4", "\"a|b\"" }) && m.Attributes![1].ArgTexts[4].StartsWith("new List"),
            string.Join(" ; ", m.Attributes![1].ArgTexts));
        Check("Attribute: ein Feld davor wird nicht zum Array ([ auf neuer Zeile)", m.Attributes![1].Args[0] is fire.Ast.RangeExpr);

        try
        {
            Parser.ParseMultiple(new[] { new ProcessedSource("class U {\n [Test]\n int x\n}", Array.Empty<string>()) });
            Check("Attribute: auch vor Feldern", true);
        }
        catch (Exception ex) { Check("Attribute: auch vor Feldern", false, ex.Message); }
        try
        {
            Parser.ParseMultiple(new[] { new ProcessedSource("class U {\n [Test]\n construct() { }\n}", Array.Empty<string>()) });
            Check("Attribute: nur vor Methoden, Feldern und Properties", false);
        }
        catch (Exception ex) { Check("Attribute: nur vor Methoden, Feldern und Properties", ex.Message.Contains("can only stand before a method, a field or a property"), ex.Message); }
    }

    private static void Files(string root)
    {
        string file = Write(root, "calc.fire", """
            #import "testing"
            class Calc {
                static int Add(int a, int b) { return a + b }
                static int Div(int a, int b) { return a / b }
            }
            class CalcTests {
                int prepared = 0
                string log = ""
                [PrepareTesting]
                undefined Start() { this.log = "S" }
                [PrepareTest]
                undefined Prepare() { this.prepared++ }
                [CleanTest]
                undefined Clean() { this.log += "c" }
                [CleanTesting]
                undefined Stop() { if (this.log != "Scccccccccccc") { throw new Exception("wrong order: " + this.log) } }

                [Test "Addition", "adds two numbers"]
                [Parameters 1..3, 10]
                [Parameters -1, 1]
                undefined Add(int a, int b) { Testing.Assert.AreEqual(a + b, Calc.Add(b, a)) }
                [Test]
                [Parameters 1..2, 1..2]
                undefined Grid(int x, int y) { Testing.Assert.IsTrue(x * y < 4, "too big") }
                [Test "On purpose"]
                undefined Fails() { print("about to fail"); Testing.Assert.AreEqual(5, 2 + 2, "math") }
                [Test]
                undefined Crashes() { int z = Calc.Div(1, 0) }
                [Test]
                undefined Asserts() {
                    var e = Testing.Assert.Throws(() => { Calc.Div(1, 0) })
                    Testing.Assert.IsTrue(e is of DivideByZeroException)
                    Testing.Assert.Contains("ell", "hello")
                    Testing.Assert.Contains(2, 1..3)
                    Testing.Assert.AreSequenceEqual([3, 2, 1], 3..1)
                    Testing.Assert.AreNear(1.0, 1.05, 0.1)
                    Testing.Assert.AreNotEqual(1, 2)
                    Testing.Assert.IsUndefined(undefined)
                    Testing.Assert.DoesNotThrow(() => { Calc.Add(1, 2) })
                    Testing.Assert.AreEqual(100cm, 1m)
                }
                [Test]
                static undefined Static() { Testing.Assert.IsFalse(false) }
            }
            print("the top level does not run")
            """);
        var (exit, output) = Forge("test", file);
        Check("Datei: Exitcode 1 bei Fehlern", exit == 1, output);
        Check("Datei: Bereiche und mehrere [Parameters] laufen einzeln", output.Contains("ok      CalcTests.Addition(1, 10)") && output.Contains("ok      CalcTests.Addition(3, 10)") && output.Contains("ok      CalcTests.Addition(-1, 1)"), output);
        Check("Datei: zwei Bereiche laufen als Kombinationen", output.Contains("ok      CalcTests.Grid(2, 1)") && output.Contains("FAILED  CalcTests.Grid(2, 2)") && output.Contains("too big - expected true"), output);
        Check("Datei: Assertion ist ein Fehlschlag mit Ausgabe des Tests", output.Contains("FAILED  CalcTests.On purpose") && output.Contains("math - expected 5, got 4") && output.Contains("about to fail"), output);
        Check("Datei: andere Ausnahme ist ein Fehler", output.Contains("ERROR   CalcTests.Crashes"), output);
        Check("Datei: Assert-Methoden, statischer Test, Reihenfolge von Prepare/Clean", output.Contains("ok      CalcTests.Asserts") && output.Contains("ok      CalcTests.Static") && !output.Contains("PROBLEM"), output);
        Check("Datei: die oberste Ebene laeuft nicht", !output.Contains("the top level does not run"), output);
        Check("Datei: Summe", output.Contains("12 tests: 9 passed, 2 failed, 1 error"), output);

        string bad = Write(root, "bad.fire", """
            class Bad {
                [Test]
                undefined NeedsArgs(int a) { }
                [Parameters 1]
                undefined NoTest(int a) { }
                [Test]
                [Parameters 1, 2]
                undefined Count(int a) { }
            }
            class Broken {
                construct(int needed) { }
                [Test]
                undefined T() { }
            }
            """);
        (exit, output) = Forge("test", bad);
        Check("Probleme: fehlende Parameter, [Parameters] ohne [Test], falsche Anzahl, Klasse ohne passenden Konstruktor",
            exit == 1 && output.Contains("bad.fire:3: the test 'Bad.NeedsArgs' takes 1 parameters") && output.Contains("bad.fire:5: 'Bad.NoTest' has [Parameters] but no [Test]")
            && output.Contains("bad.fire:7: [Parameters] gives 2 values, 'Count' takes 1") && output.Contains("Broken: the test object could not be made"), output);

        string ok = Write(root, "ok.fire", "#import \"testing\"\nclass A {\n    [Test]\n    undefined One() { Testing.Assert.AreEqual(\"x\", \"x\") }\n}\n");
        (exit, output) = Forge("test", ok);
        Check("Alles bestanden: Exitcode 0", exit == 0 && output.Contains("1 test: 1 passed, 0 failed, 0 errors"), output);
    }

    /// <summary>What the Test Explorer of the editor uses: finding the tests without running them, a filter, live events, stopping a run.</summary>
    private static void Explorer(string root)
    {
        string calc = Path.Combine(root, "calc.fire");
        var found = TestDiscovery.InFile(calc);
        Check("Explorer: findet alle Tests der Datei mit Klasse, Titel, Zeile", found.Count == 6 && found.Select(t => t.Key).SequenceEqual(new[]
            { "CalcTests.Add", "CalcTests.Grid", "CalcTests.Fails", "CalcTests.Crashes", "CalcTests.Asserts", "CalcTests.Static" })
            && found[0] is { Title: "Addition", Description: "adds two numbers", HasParameters: true } && found[2] is { Title: "On purpose", HasParameters: false } && found[3].Title == "Crashes",
            string.Join("; ", found.Select(t => $"{t.Key} {t.Title} {t.Line}")));
        var lines = File.ReadAllLines(calc);
        Check("Explorer: die Zeile ist die der Methode", found.All(t => t.Line >= 1 && t.Line <= lines.Length && lines[t.Line - 1].Contains("undefined " + t.Method + "(")),
            string.Join("; ", found.Select(t => $"{t.Method}:{t.Line}")));
        var problems = new List<string>();
        Check("Explorer: Datei ohne Tests, kaputte Datei", TestDiscovery.InFile("x.fire", "print(1)").Count == 0 && TestDiscovery.InFile("y.fire", "class C { [Test] undefined M( { }", problems).Count == 0 && problems.Count == 1,
            string.Join("; ", problems));
        Check("Explorer: #include wird uebersprungen, Zeilen bleiben", TestDiscovery.InFile(Path.Combine(root, "inc.fire"), "#include \"missing.fire\"\nclass I {\n  [Test]\n  undefined T() { }\n}\n") is [{ Key: "I.T", Line: 4 }]);

        var filter = TestFilter.Parse("CalcTests.Add, Other, *.St*");
        Check("Filter: Klasse.Methode, Klasse, Muster", filter.Includes("CalcTests", "Add") && !filter.Includes("CalcTests", "Grid") && filter.Includes("Other", "X") && filter.Includes("CalcTests", "Static"));

        var (exit, output) = Forge("test", calc, "--filter", "CalcTests.Add,CalcTests.Static");
        // (the [CleanTesting] of the class counts all its tests - with a filter it complains, as it should)
        Check("forge test --filter: nur die gewaehlten Tests", output.Contains("5 tests: 5 passed") && output.Contains("CalcTests.Static") && !output.Contains("Grid") && !output.Contains("On purpose"), output);
        (exit, output) = Forge("test", calc, "--filter", "Nothing.Here");
        Check("forge test --filter: kein Treffer laeuft nichts", output.Contains("no tests"), output);

        // the events of a run, as the explorer shows them
        var runner = new TestRunner(TextWriter.Null, false) { Filter = TestFilter.Of(new[] { "CalcTests.Grid", "CalcTests.Fails" }) };
        var started = new List<string>();
        var finished = new List<string>();
        runner.TestStarted += r => started.Add(r.Name);
        runner.TestFinished += r => finished.Add($"{r.Name}={r.Status}");
        var run = runner.RunInVm("calc", new[] { File.ReadAllText(calc) }, files: new[] { "calc.fire" }, basePath: root);
        Check("Runner: Ereignisse fuer Start und Ende jedes Falls", started.Count == 5 && finished.Count == 5 && finished.Contains("CalcTests.Grid(2, 2)=Failed") && finished.Contains("CalcTests.On purpose=Failed")
            && run.Results.Count == 5, string.Join("; ", finished));

        // stopping a run: a test that never ends
        string endless = Write(root, "endless.fire", "class E {\n    [Test]\n    undefined First() { }\n    [Test]\n    undefined Forever() { while (true) { } }\n    [Test]\n    undefined After() { }\n}\n");
        var stopper = new TestRunner(TextWriter.Null, false);
        stopper.TestStarted += r => { if (r.Method == "Forever") ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(200); stopper.Cancel(); }); };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var stopped = stopper.RunInVm("endless", new[] { File.ReadAllText(endless) }, files: new[] { "endless.fire" });
        Check("Runner: Cancel beendet einen haengenden Test", watch.Elapsed.TotalSeconds < 20 && stopper.Cancelled && stopped.Results.Any(r => r.Method == "First" && r.Status == TestStatus.Passed)
            && stopped.Results.Any(r => r.Method == "Forever" && r.Status == TestStatus.Error && r.Message == "cancelled") && !stopped.Results.Any(r => r.Method == "After"),
            string.Join("; ", stopped.Results.Select(r => $"{r.Name}={r.Status}:{r.Message}")) + " | " + string.Join("; ", stopped.Problems));
    }

    private static void Project(string root)
    {
        string dir = Path.Combine(root, "sln");
        Write(dir, "Lib/Lib.fireproj", "{ \"format\": 1, \"name\": \"Lib\", \"type\": \"library\" }");
        Write(dir, "Lib/lib.fire", "namespace Lib { class Greeter { static string Hello(string name) { return \"Hello, \" + name + \"!\" } } }");
        Write(dir, "Lib/testing/lib_tests.fire", "#import \"testing\"\nclass GreeterTests {\n    [Test \"Greets\"]\n    undefined Hello() { Testing.Assert.AreEqual(\"Hello, World!\", Lib.Greeter.Hello(\"World\")) }\n}\n");
        Write(dir, "App/App.fireproj", "{ \"format\": 1, \"name\": \"App\", \"type\": \"exe\", \"references\": [ { \"project\": \"../Lib/Lib.fireproj\" } ] }");
        Write(dir, "App/main.fire", "#import \"Lib\"\nprint(Lib.Greeter.Hello(\"app\"))\n");
        string sln = Write(dir, "Demo.firesln", "{ \"format\": 1, \"projects\": [ \"Lib/Lib.fireproj\", \"App/App.fireproj\" ] }");

        var workspace = Workspace.Open(sln);
        var lib = workspace.Projects.First(p => p.Name == "Lib");
        Check("Projekt: testing/ gehoert nicht zu den Dateien, aber zu den Testdateien", lib.Files.All(f => Path.GetFileName(Path.GetDirectoryName(f)) != "testing") && lib.TestFiles.Count == 1);
        Check("Projekt: der Plan fuer Tests nimmt testing/ mit", BuildPlan.Create(workspace, lib, tests: true).Sources.Count == 2 && BuildPlan.Create(workspace, lib).Sources.Count == 1);

        var (exit, output) = Forge("test", sln);
        Check("Projektmappe: jedes Projekt mit Tests, Summe", exit == 0 && output.Contains("Tests of Lib:") && output.Contains("ok      GreeterTests.Greets") && !output.Contains("Tests of App:"), output);

        string pkgDir = Path.Combine(root, "pkg");
        (exit, output) = Forge("build", Path.Combine(dir, "Lib", "Lib.fireproj"), "-o", pkgDir);
        bool testsInPackage = Directory.Exists(pkgDir) && Directory.GetFiles(pkgDir, "*.fpk").Any(f =>
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(f);
            return zip.Entries.Any(e => { using var r = new StreamReader(e.Open()); return r.ReadToEnd().Contains("GreeterTests"); });
        });
        Check("Projekt: das Paket enthaelt die Tests nicht", exit == 0 && !testsInPackage, output);

        // the tests of a standard package with natives: usb against its simulated device
        (exit, output) = Forge("test", Path.Combine(StandardSources.DefaultFolder, "usb", "usb.fireproj"));
        Check("Paket usb: die Tests gegen das simulierte Geraet laufen durch", exit == 0 && output.Contains("6 passed, 0 failed, 0 errors"), output);
    }
}
