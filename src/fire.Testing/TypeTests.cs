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
            try { xs.Add("zwei") } catch (ArgumentException ex) { print(ex.message) }
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
            try { var bad = new Box<int>("x") } catch (ArgumentException ex) { print(ex.message) }
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
            """) == "TC002 TC001 TC001 TC001 TC001 TC002 TC001 TC001|Parameter 'value' is a T (int), not String.\nParameter 'item' is a T (int), not String.\n1");
        Check("Generics: falsche Anzahl Typargumente (E4061), Typargumente an einer nicht generischen Klasse (E4045)",
            Run("List<int, int> a = new List()") == "|ERROR E4061" && Run("class A { }\nA<int> a = new A()") == "|ERROR E4045");
        Check("eine Einheit : any verlangt keine bestimmte Einheit", Run("any_unit_ok()\nint x : any = 3m\nx = 5s\nprint(x)".Replace("any_unit_ok()\n", "")) == "|5s");

        Check("Einheiten: andere Dimension an einer Deklaration, unitless an einer Einheit, Einheit an unitless, Rechnung ueber Dimensionen hinweg (TC006)", Run("""
            int c : mm = 2kg
            int n = 5
            n = 5mm
            var y = 5mm + 2kg
            float bad : any m = 3s
            class Box { float w : mm
                undefined Set(float v : mm) { this.w = v }
                float Len() { return this.w }
            }
            var bx = new Box()
            bx.Set(3)
            """) == "TC006 TC006 TC006 TC006 TC006 TC006|");
        Check("Einheiten: dieselbe Dimension rechnet um, var ohne Einheit bleibt frei, * und / verrechnen Dimensionen", Run("""
            int a : mm = 2m
            var x = 5mm
            x = 3s
            var z = 3m * 2s
            float sp : any m = 3km
            float v : m = 10m / 2s * 1s
            print(a)
            print(v)
            """) == "|2000mm\n5m");
        Check("Zuweisung als Bedingung: TC009 (auch strict nur Warnung), doppelt geklammert und Verbundzuweisung nicht", Run("bool ok = false\nint a = 0\nif (ok = true) { print(1) }\nif ((ok = true)) { print(2) }\nwhile ((a += 1) < 3) { }\nwhile (ok = false) { }\nprint(a)", TypeCheckMode.Strict) == "TC009 TC009|1\n2\n3");
        Check("Einheiten: eine ganze Zahl in eine groebere Einheit verliert den Bruchteil (TC007, auch strict nur Warnung)", Run("int r : m = 5mm\nvar q = 5mm:m\nprint(r)", TypeCheckMode.Strict) == "TC007 TC007|0m");
        Check("Einheiten: strict macht TC006 zum Fehler", Run("int c : mm = 2kg", TypeCheckMode.Strict) == "|ERROR TC006");
        Check("Einheiten: Property mit Einheit rechnet beim Setzen und Lesen um", Run("""
            class B { float w
                float Len : m { get { return this.w * 1m } set { this.w = value / 1m } }
            }
            var b = new B()
            b.Len = 250cm
            print(b.Len)
            print(b.w)
            """) == "|2.5m\n2.5");
        Check("Einheiten: die Initialisierung legt die Einheit fest (int a = 5mm ist ab da in mm), ohne Initialisierer oder mit 5 unitless, unbekannt bleibt frei", Run("""
            int a = 5mm
            a = 3cm
            print(a)
            float w = 1cm
            w = 2m
            print(w)
            int n = 5
            try { n = 2mm } catch (UnitMismatchException e) { print("unitless") }
            class P { any v
                construct(any v) { this.v = v }
            }
            int d = new P(5mm).v
            d = 7s
            print(d)
            class B { float len = 1cm }
            var b = new B()
            b.len = 2m
            print(b.len)
            """) == "TC006|30mm\n200cm\nunitless\n7s\n200cm");
        Check("Einheiten: die Einheit am Rueckgabetyp (float:m Len(), float : mm, float:any m) rechnet die Rueckgabe um", Run("""
            class B {
                float w = 25cm
                float:m Len() { return this.w }
                static float : mm Half(float:m x) { return x / 2 }
                float:any m Raw() { return this.w }
                float:m Bad() { return 3s }
            }
            var b = new B()
            print(b.Len())
            print(B.Half(3m))
            print(b.Raw())
            float:m x = b.Len()
            """) == "TC006|0.25m\n1500mm\n25cm");
        Check("eine float-Deklaration haelt eine Kommazahl (float f = 3 ist 3.0, auch Parameter und Rueckgaben)", Run("""
            float g = 3
            print(g / 2)
            class C { static float H(float x) { return x / 2 } static float One() { return 1 } }
            print(C.H(3))
            print(C.One() / 2)
            """) == "|1.5\n1.5\n0.5");
        Check("Einheiten: Ueberlauf beim Umrechnen ist eine OverflowException", Run("try { int big : nm = 900000000000km } catch (OverflowException e) { print(\"overflow\") }") == "|overflow");

        Check("Properties und Felder ohne this. (auch aus einer abgeleiteten Klasse) werden geprueft", Run("""
            class A {
                int n
                int Count { get { return this.n } set { this.n = value } }
                float:m Len { get { return 1m } set { } }
                undefined Set() { Count = "text"; n = "x"; Len = 3s }
            }
            class B : A { undefined Go() { Count = true } }
            print(1)
            """) == "TC001 TC001 TC006 TC001|1");
        Check("unbekannte Member einer bekannten Klasse (TC008) und falsche Argumentzahl (TC002)", Run("""
            class E2 { string message }
            class P { int x
                undefined M(int a, int b = 2) { }
            }
            var p = new P()
            var e = new E2()
            print(e.Message)
            p.Nope()
            p.M()
            p.M(1)
            p.TakeTo(e)
            """, TypeCheckMode.Strict) == "|ERROR TC008 TC008 TC002");
        Check("generische Methoden: die Typparameter kommen aus den Argumenten (auch aus dem Ergebnis eines Lambdas)", Run("""
            class Box<T> {
                T item
                construct(T item) { this.item = item }
                Box<R> Map<R>(R lambda<T> fn) { return new Box<R>(fn(this.item)) }
                static Box<U> Of<U>(U value) { return new Box<U>(value) }
            }
            class Util { static T Id<T>(T x) { return x } static T FirstOf<T>(IEnumerable<T> items) { foreach (x in items) { return x } return undefined } }
            var b = Box.Of(3)
            var c = b.Map(x => x * 2.5)
            bool w1 = c.item
            bool w2 = Util.Id("hi")
            bool w3 = Util.FirstOf([1, 2])
            var l = new List<string>()
            l.Add("a")
            bool w4 = Util.FirstOf(l)
            print(c.item)
            """) == "TC001 TC001 TC001 TC001|7.5");
        Check("Linq: Query<T> aus List<T> und Arrays, Select<R>, Aggregate<A>; ohne bekannten Typ bleibt es any", Run("""
            #import "linq"
            var nums = new List<int>()
            nums.Add(3)
            nums.Add(4)
            var q = nums.Where(x => x > 3).Select(x => "n" + x)
            bool w1 = q.First()
            bool w2 = Linq.From([1.5, 2.5]).Sum()
            int total = nums.Aggregate(0, (acc, x) => acc + x)
            List<string> names = q.ToList()
            any raw = nums
            int anything = Linq.From(raw).First()
            print(total + " " + names[0] + " " + anything)
            """) == "TC001 TC001|7 n4 3");

        // overloading by parameter type (SPEC 5.4.3): the checker picks the overload of the argument types and knows its return type
        Check("Ueberladung: der Rueckgabetyp kommt von der passenden Ueberladung", Run("""
            #strict
            class U {
                static int F(int x) { return x + 1 }
                static string F(string s) { return s + "!" }
                static string F<T>(T x) { return "g" }
            }
            int a = U.F(1)
            string b = U.F("s")
            string c = U.F<int>(2)
            print(a + " " + b + " " + c)
            """) == "|2 s! g");
        Check("Ueberladung: falscher Rueckgabetyp der gewaehlten Ueberladung wird gemeldet", Run("""
            class U {
                static int F(int x) { return x }
                static string F(string s) { return s }
            }
            int c = U.F("s")
            """) == "TC001|");
        Check("Ueberladung: Typargumente ohne generische Methode passender Laenge sind ein Fehler", Run("""
            class U { static int F(int x) { return x } }
            print(U.F<string>(1))
            """, TypeCheckMode.Strict) == "|ERROR TC002");
        Check("Ueberladung: zwei Methoden mit denselben Parametertypen sind ein Fehler, andere Typen nicht", Run("""
            class U {
                int F(int x) { return 1 }
                int F(int y) { return 2 }
            }
            """).StartsWith("|ERROR") && Run("""
            class U {
                int F(int x) { return 1 }
                int F(float y) { return 2 }
                construct(int a) { }
                construct(string a) { }
            }
            print(new U(1).F(1.5))
            """) == "|2");
        Check("Ueberladung: ein typisierter Parameter in der Ableitung ueberschreibt einen untypisierten der Basis", Run("""
            class B { string Over(any x) { return "base" } }
            class D : B { string Over(int x) { return "derived" } }
            print(new D().Over(2))
            new D().Over("a")
            """, TypeCheckMode.Strict) == "|ERROR TC002");
        Check("Ueberladung: ein any-Argument waehlt erst zur Laufzeit (kein Befund)", Run("""
            #strict
            class U {
                static string F(int x) { return "int" }
                static string F(string s) { return "string" }
            }
            any v = "x"
            print(U.F(v))
            v = 3
            print(U.F(v))
            """) == "|string\nint");

        // the type checker's choice goes to the call (OverloadHint): taken when the arguments are what it assumed, otherwise the run time scores them
        {
            long taken = VM.HintsTaken, missed = VM.HintsMissed;
            string output = Run("""
                class Animal { }
                class Dog : Animal { }
                class Show {
                    static string Of(int x) { return "int" }
                    static string Of(string s) { return "string" }
                    static string Of(Animal a) { return "animal" }
                    static string Of<T>(T x) { return "generic" }
                    string Me(int x) { return "me int" }
                    string Me(float f) { return "me float" }
                    construct() { }
                    construct(int a) { }
                    construct(string s) { }
                }
                class SubShow : Show {
                    string Me(int x) { return "sub int" }
                }
                print(Show.Of(1) + " " + Show.Of("s") + " " + Show.Of(new Dog()) + " " + Show.Of(2.5))
                var s = new Show(3)
                var t = new Show("x")
                print(s.Me(1) + " " + s.Me(1.5))
                Show sub = new SubShow()
                print(sub.Me(1) + " " + sub.Me(1.5))
                any v = "dyn"
                print(Show.Of(v))
                """, TypeCheckMode.Strict);
            Check("Ueberladung vorab gewaehlt: statisch, Konstruktor, Objekt (auch abgeleitet) nehmen den Hinweis, any waehlt zur Laufzeit",
                output == "|int string animal generic\nme int me float\nsub int me float\nstring" && VM.HintsTaken - taken == 10 && VM.HintsMissed == missed,
                $"{output} taken {VM.HintsTaken - taken} missed {VM.HintsMissed - missed}");
            taken = VM.HintsTaken; missed = VM.HintsMissed;
            output = Run("""
                class Show {
                    static string Of(int x) { return "int" }
                    static string Of(string s) { return "string" }
                }
                int x = 3
                x = "text"
                print(Show.Of(x))
                """);
            Check("Ueberladung vorab gewaehlt: passt der Wert nicht zur Annahme (lax), waehlt die Laufzeit richtig",
                output == "TC001|string" && VM.HintsMissed - missed == 1, $"{output} missed {VM.HintsMissed - missed}");
            Check("Rueckgabewert: ein unbewiesener Wert wird gegen den deklarierten Typ geprueft", Run("""
                class U { static int F() { any v = "s"; return v } }
                try { int z = U.F() } catch (ArgumentException e) { print(e.message) }
                """) == "|Return value of 'F' is an int, not String.");
        }

        // the standard library keeps its declared types honest: every import compiles without a single type conflict or untyped parameter, in strict mode
        foreach (var import in new[] { "io", "filesystem", "dialogs", "ui.docking", "json", "net", "http", "tls", "audio", "tracker", "wifi", "gpio", "i2c", "spi", "random", "time", "linq", "reflection", "ui", "scene3d", "graphics", "windows", "devices" })
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
