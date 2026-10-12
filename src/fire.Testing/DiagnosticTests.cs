using System.Text;
using fire.Compiler;
using fire.Runtime;
using fire.Values;

/// <summary>Errors with codes and warnings (`warn 123: "text"`): docs/ERRORS.md, SPEC 1.7.</summary>
static class DiagnosticTests
{
    private static int _failures;

    private static void Check(string title, bool ok, string? detail = null)
    {
        if (!ok) _failures++;
        Console.WriteLine(ok ? $"OK: Diagnose: {title}" : $"FEHLER: Diagnose: {title}" + (detail == null ? "" : $"\n{detail}"));
    }

    /// <summary>Compiles and runs <paramref name="source"/>; the warnings and what the program printed. An error of the compile is returned as its diagnostics.</summary>
    private static (IReadOnlyList<Diagnostic> Diagnostics, string Output) Compile(string source)
    {
        var collector = new DiagnosticCollector();
        using var collecting = collector.Begin();
        var lines = new List<string>();
        try
        {
            var session = RuntimeSession.Build(new[] { source }, VmExecutionMode.Release, a => { lines.Add(a[0].ToString()!); return Value.MakeUndefined(); });
            session.Run();
            Value.SingleFloats = false;
        }
        catch (Exception ex)
        {
            return (CompileDiagnostics.Of(ex).Concat(collector.Items).ToList(), "");
        }
        return (collector.Items, string.Join("\n", lines));
    }

    public static void Run()
    {
        Console.WriteLine("=== Fehlercodes und Warnungen ===");

        // ---- warnings ----
        {
            var (diagnostics, output) = Compile("""
                warn 7: "the top level"
                class Box {
                    warn 8: "a class"
                    int size
                    construct() { this.size = 3 }
                    Grow() {
                        warn 9: "a method (soon obsolete: use Resize)"
                        this.size = this.size + 1
                        return this.size
                    }
                }
                var b = new Box()
                for (var i = 0; i < 3; i++) {
                    warn 10: "in a loop"
                    b.Grow()
                }
                if (b.size > 2) warn 11: "as the body of an if"
                print("size " + b.size)
                """);
            Check("warn ID: \"Text\" ist eine Warnung: Code W<ID>, Zeile, Text; das Programm laeuft unveraendert",
                output == "size 6" && diagnostics.Count == 5 && diagnostics.All(d => d.IsWarning)
                && string.Join(",", diagnostics.Select(d => d.Code + "@" + d.Line)) == "W7@1,W8@3,W9@7,W10@14,W11@17"
                && diagnostics[2].Message == "a method (soon obsolete: use Resize)", string.Join("\n", diagnostics.Select(d => d.ToLogLine())) + "\n" + output);
            Check("eine Warnung nennt sich so im Log: Zeile, Art, Code, Text", diagnostics[0].ToLogLine() == "warning W7: the top level (line 1)" && (diagnostics[0] with { File = "main.fire" }).ToLogLine() == "main.fire(1): warning W7: the top level", diagnostics[0].ToLogLine());

            var (names, namesOut) = Compile("var warn = 5\nprint(warn + 1)\nwarn = warn * 2\nprint(warn)");
            Check("warn bleibt ein Name, solange keine Zahl und kein ':' folgen", names.Count == 0 && namesOut == "6\n10", string.Join("\n", names.Select(d => d.ToLogLine())) + namesOut);

            var plain = new List<string>();
            RuntimeSession.Build(new[] { "warn 1: \"x\"\nprint(\"ran\")" }, VmExecutionMode.Release, a => { plain.Add(a[0].ToString()!); return Value.MakeUndefined(); }).Run();
            Check("ohne Sammler (kein Aufrufer, der sie will) wird eine Warnung ohne Folgen uebergangen", plain.Count == 1 && plain[0] == "ran");

            var (bad, _) = Compile("warn 5\nprint(1)");
            Check("warn ohne ':' und Text ist ein ganz normaler Fehler (ein Ausdruck, dem das Zeilenende fehlt)", bad.Count == 1 && bad[0].IsError, string.Join("\n", bad.Select(d => d.ToLogLine())));
            var (noText, _) = Compile("warn 5: 7\nprint(1)");
            Check("warn <ID>: braucht den Text als Zeichenkette (Fehler E3157)", noText.Count == 1 && noText[0].Code == "E3157", string.Join("\n", noText.Select(d => d.ToLogLine())));
            var (bigId, _) = Compile("warn 123456: \"x\"");
            Check("die ID einer Warnung ist eine Zahl von 0 bis 99999 (Fehler E3158)", bigId.Count == 1 && bigId[0].Code == "E3158", string.Join("\n", bigId.Select(d => d.ToLogLine())));
        }

        // ---- codes of errors ----
        {
            var cases = new (string Source, string Code, string Text)[]
            {
                ("var s = \"abc", ErrorCatalog.CodeOf(ErrorArea.Lexer, "String literals must not span multiple lines"), "String literals must not span multiple lines"),
                ("print(unknownName)", ErrorCatalog.CodeOf(ErrorArea.Resolver, "Unknown identifier 'unknownName'"), "Unknown identifier"),
                ("print(1", ErrorCatalog.CodeOf(ErrorArea.Parser, "Expected ')' after the argument list"), "Expected ')' after the argument list"),
                ("#endif", ErrorCatalog.CodeOf(ErrorArea.Preprocessor, "'#endif' without '#if' (line 1)."), "'#endif' without '#if'"),
                ("var x = new Nothing()", ErrorCatalog.CodeOf(ErrorArea.Resolver, "Unknown class 'Nothing'"), "Unknown class"),
            };
            foreach (var (source, code, text) in cases)
            {
                var (diagnostics, _) = Compile(source);
                Check($"Fehler '{text}' hat den Code {code}", diagnostics.Count >= 1 && diagnostics[0].Code == code && diagnostics[0].IsError && code.Length == 5 && code[0] == 'E' && !code.EndsWith("000") && diagnostics[0].Message.Contains(text.Split('\'')[0].Trim()),
                    string.Join("\n", diagnostics.Select(d => d.ToLogLine())));
            }
            var (unknown, _) = Compile("print(unknownName)");
            Check("die Zeile eines Fehlers ist die Zeile des Quelltexts, der Text ohne Zeilenangabe", unknown.Count == 1 && unknown[0].Line == 1 && unknown[0].Message == "Unknown identifier 'unknownName'" && unknown[0].ToLogLine() == "error " + unknown[0].Code + ": Unknown identifier 'unknownName' (line 1)", unknown[0].ToLogLine());

            // the catalogue: unique codes, the codes of the areas, every message finds its own code
            var all = ErrorCatalog.All().ToList();
            Check("der Katalog: jeder Code kommt einmal vor und hat die Form E<Bereich><Nummer>", all.Select(e => e.Code).Distinct().Count() == all.Count && all.All(e => System.Text.RegularExpressions.Regex.IsMatch(e.Code, "^E[1-7][0-9]{3}$")), $"{all.Count} Eintraege");
            Check("der Katalog: viele Meldungen (Lexer, Praeprozessor, Parser, Resolver, Compiler)", all.Count > 250 && Enum.GetValues<ErrorArea>().All(a => all.Any(e => e.Area == a)), all.Count.ToString());
            var shadowed = new List<string>();
            foreach (var entry in all.Where(e => !e.Code.EndsWith("000") && e.Area != ErrorArea.Native))
            {
                string sample = entry.Template.Replace("{*}", "x");
                string found = ErrorCatalog.CodeOf(entry.Area, sample);
                if (found != entry.Code) shadowed.Add($"{entry.Code} '{sample}' -> {found}");
            }
            // (a message that another entry matches as well and that is more specific: it gets that code - the catalogue does not hide one by accident)
            Check("der Katalog: eine Beispielmeldung zu jedem Eintrag bekommt wieder seinen Code (oder den eines genaueren Eintrags desselben Bereichs)", shadowed.Count <= 6, string.Join("\n", shadowed));
            Check("eine Meldung, die der Katalog nicht kennt, hat den allgemeinen Code ihres Bereichs", ErrorCatalog.CodeOf(ErrorArea.Parser, "a message that nobody wrote") == "E3000" && ErrorCatalog.CodeOf(ErrorArea.Resolver, "?") == "E4000" && ErrorCatalog.CodeOf(ErrorArea.Lexer, "?") == "E1000");
            Check("Describe nennt den Text zu einem Code", ErrorCatalog.Describe("E1003") == "Unterminated string literal" && ErrorCatalog.Describe("E1004") == "String literals must not span multiple lines" && ErrorCatalog.Describe("W1") == null);
        }

        // ---- a member of a class that is not there is found when the program is compiled ----
        {
            var (misspelled, _) = Compile("#import \"time\"\nvar t = DateTime.Nowx");
            Check("ClassName.Member: ein Name, den die Klasse nicht hat, ist ein Uebersetzungsfehler (E4056) mit Zeile", misspelled.Count == 1 && misspelled[0].Code == "E4056" && misspelled[0].Line == 2 && misspelled[0].Message == "Class 'DateTime' has no static member 'Nowx'", string.Join("\n", misspelled.Select(d => d.ToLogLine())));
            var (instance, _) = Compile("class Box {\n int size\n Grow() { return 1 }\n}\nvar x = Box.Grow()");
            Check("ClassName.Member: ein Instanzmitglied braucht ein Objekt (E4057)", instance.Count == 1 && instance[0].Code == "E4057", string.Join("\n", instance.Select(d => d.ToLogLine())));
            var (fine, fineOut) = Compile("class Base { static int Count = 3\n static Twice(int x) { return x * 2 } }\nclass Derived : Base { }\nenum Kind { A, B }\nprint(Derived.Count + \" \" + Base.Twice(4) + \" \" + Kind.B)");
            Check("ClassName.Member: statische Felder, Methoden (auch geerbte) und Enum-Werte bleiben erlaubt", fine.Count == 0 && fineOut == "3 8 1", string.Join("\n", fine.Select(d => d.ToLogLine())) + fineOut);
            var (clock, clockOut) = Compile("#import \"time\"\nprint((DateTime.Now.Year > 2000) + \" \" + DateTime.UtcNow.Kind + \" \" + DateTime.Now.Kind + \" \" + (DateTime.Today.Hour == 0))");
            Check("DateTime.Now, UtcNow und Today sind Eigenschaften (wie in .NET)", clock.Count == 0 && clockOut == "True utc local True", string.Join("\n", clock.Select(d => d.ToLogLine())) + clockOut);
        }

        // ---- the command line prints them ----
        {
            string dir = Path.Combine(Path.GetTempPath(), "fire-diag-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string warnFile = Path.Combine(dir, "w.fire");
                File.WriteAllText(warnFile, "print(\"hi\")\nwarn 42: \"check this\"\nprint(\"there\")\n");
                var outW = new StringWriter(); var errW = new StringWriter();
                int code = CommandLineRunner.Run(new[] { "run", warnFile }, outW, errW);
                Check("Befehlszeile: eine Warnung steht auf stderr (mit Code und Zeile), das Programm laeuft und endet mit 0", code == 0 && errW.ToString().Contains("warning W42: check this (line 2)") && !outW.ToString().Contains("W42"), errW.ToString());
                string errFile = Path.Combine(dir, "e.fire");
                File.WriteAllText(errFile, "print(nothing)\n");
                var outE = new StringWriter(); var errE = new StringWriter();
                int codeE = CommandLineRunner.Run(new[] { "run", errFile }, outE, errE);
                Check("Befehlszeile: ein Fehler steht auf stderr mit Code und Zeile, Exitcode 1", codeE == CommandLineRunner.ExitScriptError && System.Text.RegularExpressions.Regex.IsMatch(errE.ToString(), @"error E4\d{3}: Unknown identifier 'nothing' \(line 1\)"), errE.ToString());
                string lexFile = Path.Combine(dir, "l.fire");
                File.WriteAllText(lexFile, "var s = \"open\n");
                var errL = new StringWriter();
                int codeL = CommandLineRunner.Run(new[] { "run", lexFile }, new StringWriter(), errL);
                Check("Befehlszeile: auch ein Fehler im Text des Programms (Lexer) ist eine Meldung mit Code, kein Absturz", codeL == CommandLineRunner.ExitScriptError && errL.ToString().Contains("error E1"), errL.ToString());
            }
            finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
        }

        // ---- a native build reports them in its result ----
        {
            var config = new fire.Native.NativeConfig();
            var target = TargetProfile.Host;
            var toolchain = config.ResolveToolchain(target);
            string tmp = Path.Combine(Path.GetTempPath(), "fire-diag-" + Guid.NewGuid().ToString("N") + ".out");
            var failed = NativeBuilder.BuildSafe(new[] { "warn 3: \"before the error\"\nprint(nothing)" }, config, target, toolchain, tmp);
            Check("nativer Build: die Fehler und Warnungen des Compilers sind im Ergebnis (mit Codes), der Log nennt den Fehler",
                !failed.Ok && failed.Diagnostics is { Count: 2 } && failed.Diagnostics.Count(d => d.IsError && d.Code.StartsWith("E4")) == 1 && failed.Diagnostics.Count(d => d.Code == "W3") == 1 && failed.Log.Contains("Unknown identifier"),
                string.Join("\n", failed.Diagnostics?.Select(d => d.ToLogLine()) ?? Array.Empty<string>()) + "\n" + failed.Log);
            var unsupported = NativeBuilder.BuildSafe(new[] { "fire { print(1) }" }, config, TargetProfile.Wasm, config.ResolveToolchain(TargetProfile.Wasm), tmp);
            Check("nativer Build: etwas, das das Ziel nicht kann (Threads im Browser), ist ein Fehler E7001", !unsupported.Ok && unsupported.Diagnostics is { Count: 1 } && unsupported.Diagnostics[0].Code == ErrorCatalog.NativeUnsupported, string.Join("\n", unsupported.Diagnostics?.Select(d => d.ToLogLine()) ?? Array.Empty<string>()));
        }

        Console.WriteLine(_failures == 0 ? "Alle Diagnose-Pruefungen bestanden." : $"FEHLER: {_failures} Diagnose-Pruefung(en) fehlgeschlagen.");
    }
}
