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
        Check("unbekannte Typen (dynamic) melden nichts", Run("var a = [1, 2]\nint x = a[0]\nvar f = (any n) => n\nint y = f(1)\nprint(x)") == "|1");
        Check("die Typpruefung aendert das Programm nicht", Run("var t = 1\nfloat g = t\nprint(g)") == "|1");

        // any, class, fields and properties need types, untyped parameters
        Check("any nimmt alles, class nur Objekte", Run("""
            class Thing { }
            class H { class o
                any a }
            var h = new H()
            h.a = 5
            h.a = "s"
            h.o = new Thing()
            h.o = 5
            h.o = "text"
            """) == "TC001 TC001|");
        Check("any als Parametertyp: jeder Wert, ein Objekt-Parameter (class) nur Objekte", Run("""
            class K { undefined Take(any v) { } undefined Obj(class o) { } }
            var k = new K()
            k.Take(1)
            k.Obj(new K())
            k.Obj(7)
            """) == "TC002|");
        Check("ein Feld mit var ist ein Fehler (E4059), ein Feld ohne Typ auch, any ist erlaubt", Run("class A { var x }") == "|ERROR E4059" && Run("class A { x }") == "|ERROR E4059" && Run("class A { any x\n}\nprint(1)") == "|1");
        Check("eine Property ohne Typ ist ein Fehler (E4060)", Run("class A { int v\n    P { get { return this.v } }\n}") == "|ERROR E4060" && Run("class A { int v\n    int P { get { return this.v } }\n}\nprint(1)") == "|1");
        Check("ein Parameter ohne Typ: Warnung (lax) mit Hinweis, Fehler (strict)", Run("class A { undefined M(x) { } }\nprint(1)") == "TC004|1" && Run("class A { undefined M(x) { } }", TypeCheckMode.Strict) == "|ERROR TC004");
        Check("eine Methode ohne Rueckgabetyp: Warnung (lax) mit Hinweis, Fehler (strict); Konstruktoren und Operatoren brauchen keinen", Run("class A { M() { return 1 } }\nprint(1)") == "TC005|1" && Run("class A { M() { } }", TypeCheckMode.Strict) == "|ERROR TC005"
            && Run("class A { int v\n    construct() { this.v = 1 }\n    operator+(any o) { return 1 }\n    destruct() { }\n}\nprint(1)", TypeCheckMode.Strict) == "|1");
        Check("Rueckgabetypen: undefined fuer 'nichts', ein Wert- und undefined-Rueckgabe passt zu jedem Typ, ein falscher Typ ist TC003", Run("class A { undefined P() { return }\n    int Q(bool b) { if (b) { return 1 } return undefined }\n    int R() { return \"x\" }\n}\nprint(1)") == "TC003|1"
            && Run("class A { undefined P() { return }\n    int Q(bool b) { if (b) { return 1 } return }\n}\nvar a = new A()\nprint(a.Q(false))", TypeCheckMode.Strict) == "|undefined");
        Check("typisierte Operatoren: `bool operator==(any o)`", Run("class A { int v\n    construct(int v) { this.v = v }\n    bool operator==(any o) { return this.v == o.v }\n    A operator+(A o) { return new A(this.v + o.v) }\n}\nvar a = new A(1)\nvar b = new A(1)\nprint(a == b)\nprint((a + b).v)", TypeCheckMode.Strict) == "|True\n2");
        Check("Arrays: Elemente, Literale, new, length, foreach haben den Typ der Elemente", Run("""
            int a[] = [1, 2, 3]
            a[0] = "x"
            int whole = a
            var b = new float[3]
            bool flag = b[0]
            foreach (x in a) { bool q = x }
            int n = a.length + "abc".Length
            float f[] = [1, 2.5]
            print(n)
            """) == "TC001 TC001 TC001 TC001|6");
        Check("Lambdas: falsche Anzahl Argumente oder Parameter wird schon beim Uebersetzen gemeldet", Run("int lambda<int, int> add = (x, y) => x + y\nlambda<int> three = (x, y) => x\nadd(1)", TypeCheckMode.Strict) == "|ERROR TC001 TC002");
        Check("Strings und Indexer: s[i] ist ein char, operator[] liefert seinen Rueckgabetyp", Run("""
            class Box { int v
                construct(int v) { this.v = v }
                int operator[](int i) { return this.v }
            }
            string t = "abc"
            int c = t[0]
            var bx = new Box(3)
            bool w = bx[0]
            print(bx[1])
            """) == "TC001 TC001|3");
        Check("Lambdas: Typen aus dem Ziel (Parameter, Variable, Feld), aus den eigenen Parametern (var), sonst any mit Hinweis", Run("""
            int lambda<int, int> add = (x, y) => x + y
            bool r = add(1, 2)
            lambda<string> wrong = (int z) => z
            int lambda<int> sq = x => "s"
            var u = (p) => p
            var f = (int k, string m) => k
            bool g = f(1, "a")
            class Box { int v
                construct(int v) { this.v = v }
                int Twice(int lambda<int> fn) { return fn(this.v) }
            }
            print(new Box(3).Twice(v => v * 2))
            """) == "TC001 TC002 TC001 TC003 TC004 TC001|6");
        Check("Generics: List<T> und eigene generische Klassen, Typargumente in Deklarationen, Substitution, foreach ueber IEnumerable<T>", Run("""
            List<int> xs = new List<int>()
            xs.Add(1)
            xs.Add("zwei")
            bool wrong = xs[0]
            foreach (x in xs) { bool b = x }
            List<string> names = new List<string>()
            names = xs
            List raw = new List()
            raw.Add("a")
            xs = raw
            class Box<T> { T item
                construct(T item) { this.item = item }
                T Get() { return this.item }
            }
            var bi = new Box<int>(4)
            bool bb = bi.Get()
            var bad = new Box<int>("x")
            List<List<int>> nested = new List<List<int>>()
            nested.Add(xs)
            bool deep = nested[0][0]
            class Bag : IEnumerable<float> {
                List<float> items
                construct() { this.items = new List<float>() }
                IEnumerator<float> GetEnumerator() { return this.items.GetEnumerator() }
            }
            foreach (f in new Bag()) { bool fb = f }
            IEnumerable<int> e = xs
            print(xs.count)
            """) == "TC002 TC001 TC001 TC001 TC001 TC002 TC001 TC001|1");
        Check("Generics: falsche Anzahl Typargumente (E4061), Typargumente an einer nicht generischen Klasse (E4045)",
            Run("List<int, int> a = new List()") == "|ERROR E4061" && Run("class A { }\nA<int> a = new A()") == "|ERROR E4045");
        Check("eine Einheit : any verlangt keine bestimmte Einheit", Run("any_unit_ok()\nint x : any = 3m\nx = 5s\nprint(x)".Replace("any_unit_ok()\n", "")) == "|5s");

        // the standard library keeps its declared types honest: every import compiles without a single type conflict or untyped parameter, in strict mode
        foreach (var import in new[] { "io", "net", "http", "tls", "audio", "tracker", "wifi", "gpio", "i2c", "spi", "random", "time", "linq", "reflection", "ui", "scene3d", "graphics", "windows", "devices" })
        {
            Environment.SetEnvironmentVariable("FIRE_TYPECHECK_ALL", "1");
            try { Check($"Bibliothek {import}: strikt typgeprueft ohne Befund", Run($"#import \"{import}\"\nprint(1)", TypeCheckMode.Strict) == "|1"); }
            finally { Environment.SetEnvironmentVariable("FIRE_TYPECHECK_ALL", null); }
        }

        Console.WriteLine(_failures == 0 ? "Alle Typpruefungs-Pruefungen bestanden." : $"FEHLER: {_failures} Typpruefungs-Pruefung(en) fehlgeschlagen.");
    }

    /// <summary>A development aid (`FIRE_TESTS_ONLY=snippets`, `FIRE_SRC` = the folder of the test sources): compiles every plain raw string of the tests and lists those that fail with a field, property or `var` error.</summary>
    public static void Snippets()
    {
        string dir = Environment.GetEnvironmentVariable("FIRE_SRC") ?? ".";
        var pattern = new System.Text.RegularExpressions.Regex("(?<!\\$)\"\"\"\\r?\\n(.*?)\"\"\"", System.Text.RegularExpressions.RegexOptions.Singleline);
        int bad = 0, total = 0;
        foreach (var file in Directory.GetFiles(dir, "*.cs"))
        {
            string text = File.ReadAllText(file);
            foreach (System.Text.RegularExpressions.Match m in pattern.Matches(text))
            {
                string snippet = m.Groups[1].Value;
                if (snippet.Contains("{{") || !(snippet.Contains("class ") || snippet.Contains("var "))) continue;
                total++;
                try { RuntimeSession.Build(new[] { snippet }, VmExecutionMode.Release, a => Value.MakeUndefined()); }
                catch (Exception ex)
                {
                    var codes = CompileDiagnostics.Of(ex).Where(d => d.Code is "E4058" or "E4059" or "E4060").ToList();
                    if (codes.Count == 0) continue;
                    bad++;
                    int line = text.Substring(0, m.Index).Count(c => c == '\n') + 1;
                    Console.WriteLine($"{Path.GetFileName(file)}:{line}: {codes[0].Code} {codes[0].Message}");
                }
                finally { Value.SingleFloats = false; }
            }
        }
        Console.WriteLine($"{total} Ausschnitte, {bad} mit Feld-/Property-/var-Fehler");
    }
}
