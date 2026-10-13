using fire.Compiler;
using fire.Runtime;
using fire.Values;

/// <summary>The type checker (docs/TYPES.md, step 1): conflicts between known types are warnings (lax) or errors (strict), `var` needs an initializer.</summary>
static class TypeTests
{
    private static int _failures;

    private static void Check(string title, bool ok, string? detail = null)
    {
        if (!ok) _failures++;
        Console.WriteLine(ok ? $"OK: Typen: {title}" : $"FEHLER: Typen: {title}" + (detail == null ? "" : $"\n{detail}"));
    }

    /// <summary>The codes of the warnings, then `|`, then the output or the errors of a build.</summary>
    private static string Run(string source, TypeCheckMode? mode = null)
    {
        var warnings = new DiagnosticCollector();
        var lines = new List<string>();
        string errors = "";
        Linker.ProcessDefaultTypeChecking = mode;
        try
        {
            using (warnings.Begin())
            {
                var session = RuntimeSession.Build(new[] { source }, VmExecutionMode.Release, a => { lines.Add(a[0].ToString()!); return Value.MakeUndefined(); });
                session.Run();
            }
        }
        catch (Exception ex)
        {
            errors = "ERROR " + string.Join(" ", CompileDiagnostics.Of(ex).Select(d => d.Code));
        }
        finally { Linker.ProcessDefaultTypeChecking = null; Value.SingleFloats = false; }
        return string.Join(" ", warnings.Items.Select(d => d.Code)) + "|" + errors + string.Join("\n", lines);
    }

    public static void Run()
    {
        Console.WriteLine("=== Typen: Typpruefung (lax/strict) und var ===");

        Check("lax: eine Zuweisung an einen anderen Typ ist eine Warnung, das Programm laeuft", Run("int x = 3\nx = \"hello\"\nprint(x)") == "TC001|hello");
        Check("lax: Initialisierer, Argument und Rueckgabe", Run("""
            class A { int f(int a) { return a } }
            int y = 2.5
            var o = new A()
            o.f("s")
            """) == "TC001 TC002|");
        Check("strict (Schalter): derselbe Konflikt ist ein Fehler", Run("int x = 3\nx = \"hello\"", TypeCheckMode.Strict) == "|ERROR TC001");
        Check("#strict in der Datei", Run("#strict\nint x = \"a\"") == "|ERROR TC001");
        Check("#lax in der Datei schlaegt den Schalter strict", Run("#lax\nint x = \"a\"\nprint(x)", TypeCheckMode.Strict) == "TC001|a");
        Check("int geht implizit an float, alles geht an string, undefined an alles", Run("float f = 2\nstring s = 12\nint u = undefined\nprint(f)\nprint(s)") == "|2\n12");
        Check("ein Objekt geht an seine Basisklasse, ein Downcast bleibt erlaubt, Unverwandtes nicht", Run("""
            class Animal { }
            class Dog : Animal { }
            class Rock { }
            Animal a = new Dog()
            Dog d = a
            Rock r = a
            """) == "TC001|");
        Check("var hat den Typ des Initialisierers fuer immer", Run("var v = 1\nv = \"t\"") == "TC001|");
        Check("var aus einer Variablen, einem Rueckgabewert und undefined!Typ", Run("""
            class A { int f() { return 1 } }
            int k = 5
            var a = k
            var b = new A().f()
            var c = undefined!int
            a = "x"
            b = "x"
            c = "x"
            """) == "TC001 TC001 TC001|");
        Check("var ohne Initialisierer ist ein Fehler (E4058), eine Deklaration mit Typ nicht", Run("var q\nprint(1)") == "|ERROR E4058" && Run("int q\nprint(1)") == "|1");
        Check("unbekannte Typen (dynamic) melden nichts", Run("var a = [1, 2]\nint x = a[0]\nvar f = n => n\nint y = f(1)\nprint(x)") == "|1");
        Check("die Typpruefung aendert das Programm nicht", Run("var t = 1\nfloat g = t\nprint(g)") == "|1");

        Console.WriteLine(_failures == 0 ? "Alle Typpruefungs-Pruefungen bestanden." : $"FEHLER: {_failures} Typpruefungs-Pruefung(en) fehlgeschlagen.");
    }
}
