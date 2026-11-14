using fire.Compiler;
using fire.Runtime;
using fire.Values;

/// <summary>The type checker (docs/spec/TYPES.md, step 1): conflicts between known types are warnings (lax) or errors (strict), `var` needs an initializer.</summary>
static class TypeTests
{
    private static int _failures;

    private static void Check(string title, bool ok, string? detail = null)
    {
        if (!ok) _failures++;
        Console.WriteLine(ok ? $"OK: Typen: {title}" : $"FEHLER: Typen: {title}" + (detail == null ? "" : $"\n{detail}") + (detail == null && _lastRun != null ? $"\n(zuletzt: {_lastRun})" : ""));
        _lastRun = null;
    }

    /// <summary>The result of the last Run (shown with a failed check that names no detail of its own).</summary>
    private static string? _lastRun;

    /// <summary>The codes of the warnings, then `|`, then the output or the errors of a build.</summary>
    private static string Run(string source, TypeCheckMode? mode = null) => Run(source, mode, false);

    /// <param name="verbose">the findings with their messages (where the standard library has one)</param>
    private static string Run(string source, TypeCheckMode? mode, bool verbose)
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
            errors = "ERROR " + string.Join(" ", CompileDiagnostics.Of(ex).Select(d => verbose ? $"{d.Code} {d.File}:{d.Line} {d.Message}\n" : d.Code));
        }
        finally { Linker.ProcessDefaultTypeChecking = null; Value.SingleFloats = false; Value.Ints32 = false; Value.Ints16 = false; }
        return _lastRun = string.Join(" ", warnings.Items.Select(d => verbose ? $"{d.Code} {d.File}:{d.Line} {d.Message}\n" : d.Code)) + "|" + errors + string.Join("\n", lines);
    }

    /// <summary>The messages of the errors of a strict build ("" when it compiles).</summary>
    private static string CompileTextStrict(string source)
    {
        Linker.ProcessDefaultTypeChecking = TypeCheckMode.Strict;
        try { return CompileText(source); }
        finally { Linker.ProcessDefaultTypeChecking = null; }
    }

    /// <summary>The messages of the errors of a build ("" when it compiles).</summary>
    private static string CompileText(string source)
    {
        try { RuntimeSession.Build(new[] { source }, VmExecutionMode.Release, a => Value.MakeUndefined()); return ""; }
        catch (Exception ex) { return string.Join("\n", CompileDiagnostics.Of(ex).Select(d => d.Message)); }
    }

    public static void Run()
    {
        Console.WriteLine("=== Typen: Typpruefung (lax/strict) und var ===");

        Check("lax: eine Zuweisung an einen anderen Typ ist eine Warnung, das Programm laeuft - der Typ bleibt (zur Laufzeit eine ArgumentException)",
            Run("int x = 3\ntry { x = \"hello\" } catch (ArgumentException e) { print(e.message) }\nprint(x)") == "TC001|Variable 'x' is an int, not String.\n3");
        Check("lax: Initialisierer, Argument und Rueckgabe", Run("""
            class A { int f(int a) { return a } }
            int y = 2.5
            var o = new A()
            o.f("s")
            """) == "TC001 TC002|");
        Check("strict (Schalter): derselbe Konflikt ist ein Fehler", Run("int x = 3\nx = \"hello\"", TypeCheckMode.Strict) == "|ERROR TC001");
        Check("#strict in der Datei", Run("#strict\nint x = \"a\"") == "|ERROR TC001");
        // A fire body runs without `this`: a capture (taking, with) wins over a field of the same name; a field without one is an error at compile time
        Check("fire: ein Capture verdeckt ein gleichnamiges Feld, ein Feld ohne Capture ist im fire-Block ein Fehler (er laeuft ohne this)",
            Run("class P { int n = 5\n undefined Go() { int n = 7\n fire taking n { int k = n + 1 } } }\nnew P().Go()\nprint(\"ok\")") is var fireCap && fireCap == "|ok"
            && Run("class P { int n = 5\n undefined Go() { fire { int k = n } } }\nnew P().Go()") is var fireField && fireField.StartsWith("|ERROR E4"), _lastRun);
        // Expression trees (docs/ROADMAP_EXPRESSIONS.md): only a lambda written at the call with an expression body, nothing that changes something (TC030)
        Check("Expression trees: ein Block, ein Lambda in einer Variablen, eine Zuweisung, ++ sind TC030 (auch lax); ein Ausdruck geht",
            Run("class P { int age\n construct(int a) { this.age = a } }\nclass Probe { string Show(lambda expression<P> e) { return e.ToString() } }\nvar pr = new Probe()\nprint(pr.Show(p => { return p.age > 1 }))") is var treeBlock && treeBlock == "|ERROR TC030"
            && Run("class P { int age\n construct(int a) { this.age = a } }\nclass Probe { string Show(lambda expression<P> e) { return e.ToString() } }\nvar pr = new Probe()\nvar f = (P p) => p.age > 1\nprint(pr.Show(f))") == "|ERROR TC030"
            && Run("class P { int age\n construct(int a) { this.age = a } }\nclass Probe { string Show(lambda expression<P> e) { return e.ToString() } }\nvar pr = new Probe()\nint k = 0\nprint(pr.Show(p => k == p.age))") == "|p => (k == p.age)"
            && Run("class P { int age\n construct(int a) { this.age = a } }\nclass Probe { string Show(lambda expression<P> e) { return e.ToString() } }\nvar pr = new Probe()\nprint(pr.Show(p => p.age++))") == "|ERROR TC030", _lastRun);
        // Records (step 9): a write outside the construction (TC026) and a field that can change (TC027) are errors in lax mode too; a record derives only from a record
        Check("Records: Schreiben ausserhalb der Konstruktion ist TC026 (auch lax), im Konstruktor erlaubt",
            Run("record P { int x\n construct(int x) { this.x = x }\n undefined M() { this.x = 2 } }\nvar p = new P(1)\np.x = 3\np.x++") is var recW && recW == "|ERROR TC026 TC026 TC026", recW);
        Check("Records: ein Feld, das sich aendern kann (Klasse, Array), ist TC027; Werte, Records, Lambdas, any sind erlaubt",
            Run("class B { int v }\nrecord Q { B b; int a[] }\nrecord R { int i; string s; Q q; lambda f; any x }\nprint(1)") is var recF && recF == "|ERROR TC027 TC027", recF);
        Check("Records: ein Record erbt nur von einem Record, eine Klasse nicht von einem",
            Run("record P { int x }\nclass C : P { }\nprint(1)") is var recI && recI == "|ERROR E5000", recI);
        Check("with: ohne copy/flat nur fuer Records - auf einer Klasse TC028 (auch lax), ein unbekanntes Feld TC008",
            Run("class C { int v }\nrecord R { int x }\nvar d = new C() with { .v = 1 }\nprint(1)") is var recWith && recWith == "|ERROR TC028"
            && Run("#strict\nrecord R { int x }\nvar e = new R() with { .y = 2 }") == "|ERROR TC008", recWith);
        Check("Channel: ein Objekt ohne Wort (copy/take) und nicht frisch ist TC029 (auch lax); copy, take, new und Werte sind erlaubt",
            Run("class J { int n }\nvar ch = new Channel<J>()\nvar j = new J()\nch.Send(j)\nprint(1)") is var chW && chW == "|ERROR TC029"
            && Run("class J { int n }\nvar ch = new Channel<J>()\nvar j = new J()\nch.Send(copy j)\nch.Send(take j)\nch.Send(new J())\nvar ci = new Channel<int>()\nci.Send(3)\nprint(ch.Count + ci.Count)") == "|4"
            && Run("record RL { int n }\nvar ch = new Channel<RL>()\nvar r = new RL()\nch.Send(r)\nprint(ch.Count)") == "|1", chW);
        Check("Records: ueber any ist das Schreiben die AccessDeniedException, == vergleicht den Inhalt",
            Run("record P { int x\n construct(int x) { this.x = x } }\nany p = new P(1)\ntry { p.x = 2 } catch (AccessDeniedException e) { print(\"denied\") }\nprint((p == new P(1)) + \" \" + p.x)") is var recA && recA == "|denied\nTrue 1", recA);
        Check("#lax in der Datei schlaegt den Schalter strict", Run("#lax\nint x = 1\ntry { x = \"a\" } catch (ArgumentException e) { print(\"guard\") }\nprint(x)", TypeCheckMode.Strict) == "TC001|guard\n1");
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
        // the type of a `var` holds at run time too (a reported conflict is guarded), `0.` is a float literal
        Check("var: int bleibt int auch zur Laufzeit (x += 0.5 ist TC001 und eine ArgumentException), 0. ist ein float",
            Run("var x = 0\ntry { x += 0.5 } catch (ArgumentException e) { print(e.message) }\nprint(x)\nvar f = 0.\nf += 0.5\nprint(f)\nvar g = 3.\nprint(g / 2)") is var keep
            && keep == "TC001|Variable 'x' is an int, not Float.\n0\n0.5\n1.5", keep);
        Check("var = undefined: der Typ kommt aus der ersten Zuweisung (TC020, auch strikt nur eine Warnung) und bleibt",
            Run("var z = undefined\nz = 4\ntry { z = \"s\" } catch (ArgumentException e) { print(\"kept\") }\nprint(z)") is var late && late == "TC020 TC001|kept\n4"
            && Run("var z = undefined\nz = 4\nprint(z)", TypeCheckMode.Strict) == "TC020|4", late);
        Check("var ohne ableitbaren Typ (undefined ohne Zuweisung, Wert unbekannten Typs): TC019 - lax laute Warnung (any), strikt ein Fehler; any x ist still",
            Run("var w = undefined\nprint(1)") == "TC019|1" && Run("var w = undefined\nprint(1)", TypeCheckMode.Strict) == "|ERROR TC019"
            && Run("any a = [1]\nvar e = a[0]\nprint(e)") == "TC019|1" && Run("any a = [1]\nvar e = a[0]\nprint(e)", TypeCheckMode.Strict) == "|ERROR TC019"
            && Run("any a = [1]\nany e = a[0]\nprint(e)", TypeCheckMode.Strict) == "|1");
        Check("fire-Block: ein Local der Umgebung ohne taking/handover wird kopiert - TC023 (lax eine Warnung, strikt ein Fehler), wenn die Kopie Speicher kostet; eine Zahl und taking/handover still",
            Run("class W { static undefined Go() { var a = [3]\n fire { print(a[0]) } } }\nW.Go()") is var lax && lax.StartsWith("TC023|")
            && Run("class W { static undefined Go() { var a = [3]\n fire { print(a[0]) } } }\nW.Go()", TypeCheckMode.Strict) == "|ERROR TC023"
            && Run("class W { static undefined Go() { int k = 3\n fire { print(k) } } }\nW.Go()", TypeCheckMode.Strict).StartsWith("|")
            && Run("class W { static undefined Go() { var a = [3]\n fire { print((taking a)[0]) } } }\nW.Go()", TypeCheckMode.Strict).StartsWith("|"));
        Check("var aus einem als any deklarierten Wert: TC020 (eine Warnung, auch strikt)",
            Run("class K { static any G() { return 1 } }\nvar v = K.G()\nprint(v)", TypeCheckMode.Strict) == "TC020|1");
        Check("var aus verwandten Typen: int/float wird float, Klassen die naechste gemeinsame Basis; unverwandte werden any (TC020)", Run("""
            class Animal { }
            class Dog : Animal { }
            class Cat : Animal { }
            bool yes = 1 > 0
            var m = yes ? 1 : 2.5
            print(m / 2)
            var a = yes ? new Dog() : new Cat()
            a = new Cat()
            Animal back = a
            var u = yes ? 1 : "one"
            var arr = [1, "one"]
            print(u)
            """, TypeCheckMode.Strict) is var joined && joined == "TC020 TC020|0.5\n1", joined);
        Check("int / int ist ein int (7 / 2 ist 3), eine Potenz zweier ints ein int (2 ^ 3 ist 8, 2 ^ -1 ist 0), mit float-Ziel ein float (float h = 2 ^ -1 ist 0.5)",
            Run("var q = 7 / 2\nvar p = 2 ^ 3\nint both = q + p\nprint(both)\nprint(p / 16)\nprint(2 ^ 3 is of int)\nprint(2 ^ -1)\nfloat h = 2 ^ -1\nprint(h)\nfloat g = 2 ^ 3 / 3\nprint(g > 2.6)", TypeCheckMode.Strict) is var pw
            && pw == "|11\n0\nTrue\n0\n0.5\nTrue"
            && Run("var p = 2.0 ^ 3\nint i = p\nprint(1)", TypeCheckMode.Strict) == "|ERROR TC001", pw);
        Check("#intwidth 32: ein int[64] in ein schlichtes int ist TC021 und wird geschnitten (auch zur Laufzeit, Feld und Parameter); var behaelt die Breite, !int ist still",
            Run("#intwidth 32\nint[64] t = 864000000000\nint x = t + 0\nvar y = t + 1\nint z = (t + 2)!int\nclass B { int v\n  static int Id(int p) { return p } }\nvar b = new B()\nb.v = t\nprint(x)\nprint(y)\nprint(z)\nprint(b.v)\nprint(B.Id(t))")
                is var cut && cut == "TC021 TC021 TC021|711573504\n864000000001\n711573506\n711573504\n711573504"
            && Run("int[64] t = 864000000000\nint x = t\nprint(x)") == "|864000000000", cut);
        Check("Natives der Pakete haben den Typ ihres Manifests (\"returns\"): var aus einem Native ist strikt still, int[64]-Ticks sind TC021 an einem int (#intwidth 32)",
            Run("#import \"io\"\nvar e = __IOLastError()\nint x = e + 1\nprint(x > -100)", TypeCheckMode.Strict) is var nat && nat == "|True"
            && Run("#intwidth 32\n#import \"time\"\nvar now = __time_now()\nint[64] t = now\nint small = now\nprint(t > 0)") == "TC021|True", nat);
        {
            string quiet = Run("class K { static defined int T(defined int x) { return x * 2 } static defined int G(int y) { return K.T(trust y) } }\nint s = 0\nfor (int i = 0; i < 3; i++) { s = s + i }\nvar w = s * 2\nprint(K.T(s) + K.T(w) + K.T(4) + K.T(s + 1) + K.G(5))");
            string warn = Run("class K { static defined int T(defined int x) { return x } static defined int R(int y) { return y } }\nint u\ntry { K.T(u) } catch (ArgumentException e) { print(e.message) }\nprint(K.R(3))");
            string more = Run("class K { defined int c }\nvar k = new K()\nint u\ndefined int d = 1\ntry { d = u } catch (ArgumentException e) { print(e.message) }\ntry { k.c = undefined } catch (ArgumentException e) { print(e.message) }\nprint(k.c + d)");
            Check("defined (SPEC 8.10b): ein Wert, der undefined sein kann, an ein defined-Ziel ist TC022 (Parameter, Feld, lokale defined, Rueckgabe); Literale, Rechnungen, defined und hergeleitete lokale sind still",
                quiet == "|44" && warn == "TC022 TC022|Parameter 'x' is an int and never undefined.\n3"
                && more == "TC022 TC022|Variable 'd' is an int and never undefined.\nField 'c' is an int and never undefined.\n1", quiet + " / " + warn + " / " + more);
        }
        Check("defined (SPEC 8.10b): ein Feld, eine lokale defined starten bei 0; ein Enum wird zur Zahl; trust bleibt ein Name, wo kein Operand folgt",
            Run("enum C { A, B }\nclass K { defined int n\ndefined float f\ndefined bool b\nstatic string S(defined int v) { return \"\" + v } }\nvar k = new K()\ndefined int d\nvar trust = 3\nprint(k.n + \" \" + k.f + \" \" + k.b + \" \" + d + \" \" + K.S(C.B!int) + \" \" + (trust + 1))") is var zero && zero == "|0 0 False 0 1 4", zero);
        Check("defined (SPEC 8.10b): 'int!' sagt, wie es heisst; defined an einem Array, einem string oder als Typargument ist ein Fehler",
            Run("class K { int! n }") is var bang && bang.StartsWith("|ERROR E3000")
            && Run("class K { defined int a[] }").StartsWith("|ERROR") && Run("class K { defined string s }").StartsWith("|ERROR") && Run("var l = new List<defined int>()").StartsWith("|ERROR"), bang);
        Check("var a = [] hat keine Elementtypen: TC020 (any[]), int a[] = [] ist still",
            Run("var a = []\nprint(a.length)", TypeCheckMode.Strict) == "TC020|0" && Run("int a[] = []\nprint(a.length)", TypeCheckMode.Strict) == "|0");
        Check("Namen im eigenen Namespace: Parameter, Felder und Aufrufe (UI.M.Max) haben ihren Typ", Run("""
            namespace Cx {
                class Port { string n
                    construct(string n) { this.n = n } }
                class Circuit { Port AddPort(string name) { return new Port(name) } }
                class M { static int Max(int a, int b) { if (a > b) { return a } return b } }
                class Sw : Circuit {
                    construct() : base() { var com = this.AddPort("com"); print(com.n + Cx.M.Max(1, 2)) }
                }
            }
            new Cx.Sw()
            """, TypeCheckMode.Strict) is var ns && ns == "|com2", ns);
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
        // (an `int a[]` is a typed array: the string is TC001 and at run time an ArgumentException, SPEC 8.4.2)
        Check("Arrays: Elemente, Literale, new, length, foreach haben den Typ der Elemente", Run("""
            int a[] = [1, 2, 3]
            try { a[0] = "x" } catch (ArgumentException e) { print("A") }
            try { int whole = a } catch (ArgumentException e) { }
            var b = new float[3]
            bool flag = b[0]
            foreach (x in a) { try { bool q = x } catch (ArgumentException e) { } }
            int n = a.length + "abc".Length
            float f[] = [1, 2.5]
            print(n)
            """) == "TC001 TC001 TC001 TC001|A\n6");
        Check("Lambdas: falsche Anzahl Argumente oder Parameter wird schon beim Uebersetzen gemeldet", Run("int lambda<int, int> add = (x, y) => x + y\nlambda<int> three = (x, y) => x\nadd(1)", TypeCheckMode.Strict) == "|ERROR TC001 TC002");
        Check("Strings und Indexer: s[i] ist ein char, operator[] liefert seinen Rueckgabetyp", Run("""
            class Box { int v
                construct(int v) { this.v = v }
                int operator[](int i) { return this.v }
            }
            string t = "abc"
            try { int c = t[0] } catch (ArgumentException e) { }
            var bx = new Box(3)
            try { bool w = bx[0] } catch (ArgumentException e) { }
            print(bx[1])
            """) == "TC001 TC001|3");
        Check("Lambdas: Typen aus dem Ziel (Parameter, Variable, Feld), aus den eigenen Parametern (var), sonst any mit Hinweis", Run("""
            int lambda<int, int> add = (x, y) => x + y
            try { bool r = add(1, 2) } catch (ArgumentException e) { }
            lambda<string> wrong = (int z) => z
            int lambda<int> sq = x => "s"
            var u = (p) => p
            var f = (int k, string m) => k
            try { bool g = f(1, "a") } catch (ArgumentException e) { }
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
            try { bool wrong = xs[0] } catch (ArgumentException e) { }
            foreach (x in xs) { try { bool b = x } catch (ArgumentException e) { } }
            List<string> names = new List<string>()
            try { names = xs } catch (ArgumentException e) { }
            List raw = new List()
            raw.Add("a")
            xs = raw
            class Box<T> { T item
                construct(T item) { this.item = item }
                T Get() { return this.item }
            }
            var bi = new Box<int>(4)
            try { bool bb = bi.Get() } catch (ArgumentException e) { }
            try { var bad = new Box<int>("x") } catch (ArgumentException ex) { print(ex.message) }
            List<List<int>> nested = new List<List<int>>()
            nested.Add(xs)
            try { bool deep = nested[0][0] } catch (ArgumentException e) { }
            class Bag : IEnumerable<float> {
                List<float> items
                construct() { this.items = new List<float>() }
                IEnumerator<float> GetEnumerator() { return this.items.GetEnumerator() }
            }
            foreach (f in new Bag()) { try { bool fb = f } catch (ArgumentException e) { } }
            IEnumerable<int> e = xs
            print(xs.Count)
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
        Check("Einheiten: Namen kombinierter Einheiten behalten die Symbole der Operanden (kg*m/s^2, km/h, m*mm), gemischte Praefixe kuerzen sich in einen Praefix (ms^-1)", Run("""
            var f = 5kg * (2m / 1s / 1s)
            print(f + " " + 2km / 1h + " " + 1m * 1mm + " " + 500mm * 2m + " " + 2kg * 3g + " " + 3m * 2m * 4m + " " + 10m / 2s * 3s)
            float:kg*m/s^2 g = f
            float:N n = f
            print(g + " " + 1m / (1mm * 1s) + " " + "1m/mm*s".ToFloat() + " " + "2 m*m/s/s".ToFloat() + " " + "3 km/m*h".ToFloat() + " " + 1.0dm3:l + " " + 2 / 4s + " " + n)
            unit Newton: N { coerce kg*m/s^2 => 1 }
            """) == "|10kg*m/s^2 2km/h 1m*mm 1000mm*m 6kg*g 24m^3 15m\n10kg*m/s^2 1ms^-1 1ms^-1 2m^2/s^2 3km/m*h 1l 0s^-1 10N");
        Check("Einheiten: Potenz als Ziffern direkt nach dem Symbol (1m3, 5dm3, float v : m3, float:m/s2, kg*m2/s2, \"5 m3\"), 1e3 ist wissenschaftliche Schreibweise", Run("""
            print(1m3 + " " + 5dm3 + " " + 2cm2)
            float v : m3 = 5l
            float:m/s2 a = 9.81m / 1s / 1s
            float:kg*m2/s2 e = 2kg * 3m2 / 1s / 1s
            float w : m^3 = 1m3
            print(v + " " + a + " " + e + " " + w + " " + (1m3 == 1000l) + " " + 1m3:l + " " + 5:m3)
            print("5 m3".ToFloat() + " " + "2 m/s2".ToFloat() + " " + "1e3".ToFloat() + " " + "1e3m".ToFloat() + " " + "3dm3".ToFloat():l)
            print(1e3 + " " + 2.5e-3 + " " + 1e3m + " " + 0x1F + " " + 2E+2)
            """) == "|1m^3 5dm^3 2cm^2\n0.005m^3 9.81m/s^2 6kg*m^2/s^2 1m^3 True 1000l 5m^3\n5m^3 2m/s^2 1000 1000m 3l\n1000 0.0025 1000m 31 200");
        Check("Einheiten: eine Potenz 0 als Ziffer ist ein Fehler (1m0, float x : s0)", CompileText("print(1m0)").Contains("1 or more") && CompileText("float x : s0 = 1s").Contains("1 or more"), CompileText("print(1m0)") + " / " + CompileText("float x : s0 = 1s"));
        Check("Einheiten: Arrays mit Einheit am Typ rechnen ihre Elemente um (Initialisierer, Element, Parameter, Feld, mehrdimensional), andere Dimension TC006/UnitMismatchException", Run("""
            float:km/h arr[] = [36m / 1s, 10km / 1h]
            print(arr[0] + " " + arr[1])
            arr[1] = 5m / 1s
            arr[0] += 1m / 1s
            float b[3] : mm
            b[0] = 2cm
            int c[] : cm = [1m, 20mm]
            print(arr[0] + " " + arr[1] + " " + b[0] + " " + b[1] + " " + c[0] + " " + c[1])
            class K {
                float:m lens[]
                construct() { lens = [1km, 2cm] }
                static float:m Sum(float v[] : m) { float s = 0m; foreach (x in v) { s += x } return s }
                static float:cm[] Two() { return [1m, 2m] }
            }
            var k = new K()
            k.lens[1] = 3mm
            print(K.Sum([1km, 20cm]) + " " + k.lens[0] + " " + k.lens[1] + " " + K.Two()[1])
            float:m grid[][] = new float[2][2]
            grid[1][0] = 3km
            float:mm nested[][] = [[1m], [2cm, 3m]]
            print(grid[1][0] + " " + nested[1][1] + " " + grid[0][0])
            any w = 3kg
            try { arr[0] = w } catch (UnitMismatchException e) { print(e.message) }
            any bad = [1m, 2s]
            try { float:m xs[] = bad } catch (UnitMismatchException e) { print(e.message) }
            float:km/h wrong[] = [3kg]
            """) == "TC006|129.6km/h 10km/h\n133.2km/h 18km/h 20mm undefined:mm 100cm 2cm\n1000.2m 1000m 0.003m 200cm\n3000m 3000mm undefined:m\nExpected unit 'km/h', got: kg.\nExpected unit 'm', got: s.");
        Check("Einheiten: ein Element eines Arrays mit Einheit in einer anderen Dimension ist TC006 (auch strict ein Fehler)", Run("float:km/h arr[] = [1m / 1s]\narr[0] = 2s", TypeCheckMode.Strict) == "|ERROR TC006");
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
            try { bool w1 = c.item } catch (ArgumentException e) { }
            try { bool w2 = Util.Id("hi") } catch (ArgumentException e) { }
            try { bool w3 = Util.FirstOf([1, 2]) } catch (ArgumentException e) { }
            var l = new List<string>()
            l.Add("a")
            try { bool w4 = Util.FirstOf(l) } catch (ArgumentException e) { }
            print(c.item)
            """) == "TC001 TC001 TC001 TC001|7.5");
        Check("Linq: Query<T> aus List<T> und Arrays, Select<R>, Aggregate<A>; ohne bekannten Typ bleibt es any", Run("""
            #import "linq"
            var nums = new List<int>()
            nums.Add(3)
            nums.Add(4)
            var q = nums.Where(x => x > 3).Select(x => "n" + x)
            try { bool w1 = q.First() } catch (ArgumentException e) { }
            try { bool w2 = Linq.From([1.5, 2.5]).Sum() } catch (ArgumentException e) { }
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
            class D : B { override string Over(int x) { return "derived" } }
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
                    override string Me(int x) { return "sub int" }
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
                try { x = "text" } catch (ArgumentException e) { print("kept") }
                print(Show.Of(x))
                """);
            // (a conflict in lax mode is a warning, and the value does not get in - the variable stays an int, the hint holds)
            Check("Ueberladung vorab gewaehlt: ein Konflikt (lax) kommt nicht in die Variable, der Hinweis bleibt richtig",
                output == "TC001|kept\nint" && VM.HintsMissed - missed == 0, $"{output} missed {VM.HintsMissed - missed}");
            Check("Rueckgabewert: ein unbewiesener Wert wird gegen den deklarierten Typ geprueft", Run("""
                class U { static int F() { any v = "s"; return v } }
                try { int z = U.F() } catch (ArgumentException e) { print(e.message) }
                """) == "|Return value of 'F' is an int, not String.");
        }

        // access modifiers: decided at compile time where the class of the receiver is known (TC012, an error in lax mode too); `any` and reflection at run time
        {
            string access = """
                class Base {
                    private int secret = 1
                    protected int shared = 2
                    private static int sv = 4
                    private int Hidden() { return 10 }
                    protected int Guarded() { return 20 }
                    private static int Counter() { return 7 }
                    private int Hush { get { return this.secret } set { this.secret = value } }
                    int Same(Base other) { return other.secret + other.Hidden() + Base.sv + Base.Counter() + other.Hush }
                    int InLambda() { var me = this; var f = func () => (taking me).secret + me.Hidden(); return f() }
                }
                class Child : Base {
                    int Peek() { return this.shared + this.Guarded() + shared + Guarded() }
                }
                class Locked { private construct() { } static Locked Make() { return new Locked() } }
                class Guarded2 { protected construct() { } }
                class Sub2 : Guarded2 { }

                """;
            Check("Zugriff: erlaubte Zugriffe (eigene Klasse, anderes Objekt derselben Klasse, protected aus der Unterklasse, Lambda in der Methode) ohne Befund",
                Run(access + "var b = new Base()\nprint(b.Same(b))\nprint(b.InLambda())\nprint(new Child().Peek())\nprint(Locked.Make() is of Locked)\nprint(new Sub2() is of Guarded2)") is var allowed
                && allowed == "|23\n11\n44\nTrue\nTrue", allowed);
            foreach (var (what, code) in new[]
            {
                ("privates Feld lesen", "var b = new Base()\nprint(b.secret)"),
                ("privates Feld schreiben", "var b = new Base()\nb.secret = 5"),
                ("protected Feld von aussen", "Base b = new Child()\nprint(b.shared)"),
                ("private Methode", "var b = new Base()\nb.Hidden()"),
                ("protected Methode", "var b = new Base()\nb.Guarded()"),
                ("private statische Methode", "print(Base.Counter())"),
                ("privates statisches Feld", "print(Base.sv)"),
                ("private Property lesen", "var b = new Base()\nprint(b.Hush)"),
                ("private Property schreiben", "var b = new Base()\nb.Hush = 3"),
                ("privater Konstruktor", "var l = new Locked()"),
                ("privater Konstruktor der Basisklasse", "class Sub : Locked { }"),
                ("privates Feld der Basisklasse ueber this", "class C2 : Base { int P() { return this.secret } }"),
                ("privates Feld der Basisklasse ohne this", "class C2 : Base { int P() { return secret } }"),
                ("private Methode der Basisklasse ohne this", "class C2 : Base { int P() { return Hidden() } }"),
                ("private Methode der Basisklasse ueber base", "class C2 : Base { int P() { return base.Hidden() } }"),
                ("im Lambda einer anderen Klasse", "class C2 { int P(Base b) { var f = func () => (taking b).secret; return f() } }"),
                ("Parameter einer anderen Klasse", "class F { static int G(Base b) { return b.secret } }"),
                ("Rueckgabetyp", "class F { static Base Make() { return new Base() } }\nprint(F.Make().Hidden())"),
            })
                Check($"Zugriff: {what} ist ein Fehler beim Uebersetzen (TC012, auch lax)", Run(access + code) is var denied && denied.StartsWith("|ERROR TC012") && !denied.Contains("TC008"), denied);
            Check("Zugriff: die Meldung ist die der Laufzeit", Run(access + "var b = new Base()\nprint(b.secret)", null) is var text && text.Contains("TC012")
                && CompileText(access + "var b = new Base()\nprint(b.secret)").Contains("Field 'secret' of 'Base' is private and cannot be accessed from here."));
            Check("Zugriff: mit `any` bleibt es eine AccessDeniedException zur Laufzeit",
                Run(access + "any b = new Base()\ntry { print(b.secret) } catch (AccessDeniedException e) { print(e.message) }\ntry { b.Hidden() } catch (AccessDeniedException e) { print(e.message) }") is var dynamic
                && dynamic == "|Field 'secret' of 'Base' is private and cannot be accessed from here.\nMethod 'Hidden' of 'Base' is private and cannot be called from here.", dynamic);
            Check("Zugriff: eine Unterklasse deklariert den Namen oeffentlich - die Laufzeit entscheidet nach dem Objekt", Run("""
                class B { private int M() { return 1 } }
                class D : B { override int M() { return 2 } }
                B b = new D()
                print(b.M())
                B b2 = new B()
                try { print(b2.M()) } catch (AccessDeniedException e) { print("verweigert") }
                """) is var mixed && mixed == "|2\nverweigert", mixed);

            // a member of an interface is public: so is the method that implements it (E4063) - a call through the interface needs no access check
            string iface = "interface IShape { int Area() }\n";
            foreach (var (what, code) in new[]
            {
                ("private", "class Sq : IShape { private int Area() { return 4 } }"),
                ("protected", "class Sq : IShape { protected int Area() { return 4 } }"),
                ("verdeckt die oeffentliche der Basisklasse", "class Base { int Area() { return 1 } }\nclass Sq : Base, IShape { private int Area() { return 4 } }"),
                ("in einer Unterklasse einer implementierenden Klasse", "class Sq : IShape { int Area() { return 4 } }\nclass Sub : Sq { protected int Area() { return 5 } }"),
            })
                Check($"Interface: eine Implementierung {what} ist ein Fehler (E4063)", Run(iface + code + "\nprint(1)") is var notPublic && notPublic == "|ERROR E4063", notPublic);
            Check("Interface: die Meldung nennt Methode, Klasse und Interface",
                CompileText(iface + "class Sq : IShape { private int Area() { return 4 } }") is var ifaceText
                && ifaceText.Contains("Method 'Area' of class 'Sq' implements interface 'IShape' and must be public, not private"), ifaceText);
            Check("Interface: oeffentlich geerbt, eine private Ueberladung anderer Stelligkeit oder ein privates Mitglied einer fremden Klasse ohne Befund", Run(iface + """
                class Base { int Area() { return 1 } }
                class Sq : Base, IShape { private int Area(int scale) { return 4 * scale } int Twice() { return this.Area(2) } }
                class Other { private int Area() { return 9 } int Mine() { return this.Area() } }
                IShape s = new Sq()
                print(s.Area())
                print(new Sq().Twice() + new Other().Mine())
                """) is var inherited && inherited == "|1\n17", inherited);
        }

        // properties in interfaces (SPEC 8.5): `int Limit { get; set; }` / `{ get; }` - implemented by a public property (auto or with get/set) of the class
        {
            string iface = "interface ILimit { int Limit { get; set; }\n string Name { get; }\n float Delay : ms { get; } }\n";
            Check("Interface-Property: Auto-Property, get/set und Basisklasse implementieren, Zugriff ueber das Interface mit Typ und Einheit", Run(iface + """
                class Base { string Name { get { return "base" } } }
                class Lim : Base, ILimit {
                    int Limit { get; set; }
                    float Delay : ms { get { return 2s } }
                }
                ILimit l = new Lim()
                l.Limit = 4
                l.Limit = l.Limit + 1
                int n = l.Limit
                float : ms d = l.Delay
                print(n + " " + l.Name + " " + d)
                """) is var props && props == "|5 base 2000ms", props);
            Check("Interface-Property: ein read-only Mitglied darf read-write implementiert sein", Run(iface + """
                class Lim : ILimit { int Limit { get; set; }
                 string Name { get; set; }
                 float Delay : ms { get; set; } }
                var x = new Lim()
                x.Name = "n"
                ILimit l = x
                print(l.Name)
                """) is var rw && rw == "|n", rw);
            Check("Interface-Property: fehlt sie, ist es E4064", Run(iface + "class Lim : ILimit { int Limit { get; set; }\n float Delay : ms { get; } }\nprint(1)") is var missing && missing == "|ERROR E4064", missing);
            Check("Interface-Property: fehlt der Setter, ist es E4064", Run(iface + "class Lim : ILimit { int Limit { get; }\n string Name { get; }\n float Delay : ms { get; } }\nprint(1)") is var noSet && noSet == "|ERROR E4064", noSet);
            Check("Interface-Property: ein Feld gleichen Namens zaehlt nicht (E4065)", Run(iface + "class Lim : ILimit { int Limit\n string Name { get; }\n float Delay : ms { get; } }\nprint(1)") is var field && field == "|ERROR E4065", field);
            Check("Interface-Property: get_/set_-Methoden zaehlen nicht (E4064)", Run(iface + "class Lim : ILimit { int get_Limit() { return 1 }\n undefined set_Limit(int v) { }\n string Name { get; }\n float Delay : ms { get; } }\nprint(1)") is var methods && methods == "|ERROR E4064", methods);
            Check("Interface-Property: private oder protected ist E4063", Run(iface + "class Lim : ILimit { private int Limit { get; set; }\n string Name { get; }\n float Delay : ms { get; } }\nprint(1)") is var hidden && hidden == "|ERROR E4063"
                && CompileText(iface + "class Lim : ILimit { int Limit { get; set; }\n protected string Name { get; }\n float Delay : ms { get; } }").Contains("Property 'Name' of class 'Lim' implements interface 'ILimit' and must be public, not protected"), hidden);
            Check("Interface-Property: ein anderer Werttyp oder eine andere Einheit ist E4066", Run(iface + "class Lim : ILimit { string Limit { get; set; }\n string Name { get; }\n float Delay : ms { get; } }\nprint(1)") is var otherType && otherType == "|ERROR E4066"
                && Run(iface + "class Lim : ILimit { int Limit { get; set; }\n string Name { get; }\n float Delay : s { get; } }\nprint(1)") == "|ERROR E4066", otherType);
            Check("Interface-Property: Zuweisung an eine get-only Property ueber das Interface ist TC016 (auch lax ein Fehler)", Run(iface + """
                class Lim : ILimit { int Limit { get; set; }
                 string Name { get; set; }
                 float Delay : ms { get; } }
                ILimit l = new Lim()
                l.Name = "x"
                """) is var readOnly && readOnly == "|ERROR TC016", readOnly);
            Check("Interface-Property: Typ und Einheit gelten beim Typpruefer (TC001, TC006)", Run(iface + """
                class Lim : ILimit { int Limit { get; set; }
                 string Name { get; }
                 float Delay : ms { get; } }
                ILimit l = new Lim()
                l.Limit = "x"
                float : kg w = l.Delay
                """) is var typed && typed.StartsWith("TC001 TC006|"), typed);
            Check("Interface-Property: ein Rumpf im Interface ist E3161, ohne Typ E3162", Run("interface I { int X { get { return 1 } } }\nprint(1)") == "|ERROR E3161"
                && Run("interface I { X { get; } }\nprint(1)") is var untyped && untyped == "|ERROR E3162", Run("interface I { X { get; } }\nprint(1)"));
        }

        // interface inheritance (SPEC 8.5): `interface IConnection : IStream, IOther` - a class implements all of it and is of every base interface
        {
            string ifaces = """
                interface IReader { int Read() }
                interface IWriter { undefined Write(int v) }
                interface IStream : IReader, IWriter { int Size { get; } }

                """;
            Check("Interface-Vererbung: zuweisbar an das Basis-Interface und als Parameter, is of, Cast, Overload", Run(ifaces + """
                class Mem : IStream {
                    int last
                    int Read() { return this.last }
                    undefined Write(int v) { this.last = v }
                    int Size { get { return 1 } }
                }
                class Use {
                    static int Pull(IReader r) { return r.Read() }
                    static string Pick(IWriter w) { return "writer" }
                    static string Pick(string s) { return "string" }
                }
                IStream s = new Mem()
                s.Write(7)
                IReader r = s
                IWriter w = s
                print(Use.Pull(s) + " " + r.Read() + " " + Use.Pick(s) + " " + s.Size)
                print((s is of IReader) + " " + (new Mem() is of IWriter) + " " + (r!IStream).Size)
                """) is var basic && basic == "|7 7 writer 1\nTrue True 1", basic);
            Check("Interface-Vererbung: strikt ohne Befund", Run(ifaces + """
                class Mem : IStream { int Read() { return 1 }
                 undefined Write(int v) { }
                 int Size { get { return 1 } } }
                IStream s = new Mem()
                IReader r = s
                print(r.Read())
                """, TypeCheckMode.Strict) is var strict && strict == "|1", strict);
            Check("Interface-Vererbung: where T is of IReader nimmt eine Klasse eines abgeleiteten Interfaces und das Interface selbst (kein E4013)", Run(ifaces + """
                class Mem : IStream { int Read() { return 1 }
                 undefined Write(int v) { }
                 int Size { get { return 1 } } }
                class Holder<T> where T is of IReader { T item }
                var a = new Holder<Mem>()
                var b = new Holder<IStream>()
                print(2)
                """) is var where && where == "|2"
                && Run(ifaces + "class Holder<T> where T is of IStream { T item }\nvar a = new Holder<IReader>()") == "|ERROR E4013", where);
            Check("Interface-Vererbung: ein geerbtes Mitglied fehlt (E4010)", Run(ifaces + "class Mem : IStream { int Read() { return 1 }\n int Size { get { return 1 } } }\nprint(1)") is var missing && missing == "|ERROR E4010", missing);
            Check("Interface-Vererbung: ein geerbtes Mitglied privat (E4063)", Run(ifaces + "class Mem : IStream { int Read() { return 1 }\n private undefined Write(int v) { }\n int Size { get { return 1 } } }\nprint(1)") is var priv && priv == "|ERROR E4063", priv);
            Check("Interface-Vererbung: ein Zyklus ist E4068, eine Klasse als Basis E4067", Run("interface IA : IB { }\ninterface IB : IC { }\ninterface IC : IA { }\nprint(1)") is var cycle && cycle.StartsWith("|ERROR E4068")
                && Run("class K { }\ninterface IA : K { }\nprint(1)") == "|ERROR E4067"
                && CompileText("interface IA : IB { }\ninterface IB : IA { }").Contains("Interface 'IA' extends itself (IA -> IB -> IA)"), cycle);
            Check("Interface-Vererbung: Raute (zwei Wege zum selben Interface)", Run("""
                interface IBase { int Id() }
                interface ILeft : IBase { int L() }
                interface IRight : IBase { int R() }
                interface IBoth : ILeft, IRight { }
                class Both : IBoth { int Id() { return 1 }
                 int L() { return 2 }
                 int R() { return 3 } }
                IBoth b = new Both()
                ILeft l = b
                IBase x = l
                IRight r = b
                print(x.Id() + l.L() + r.R() + b.Id())
                print(b is of IBase)
                """, TypeCheckMode.Strict) is var diamond && diamond == "|7\nTrue", diamond);
            Check("Interface-Vererbung: ein unverwandtes Interface ist nicht zuweisbar (TC001)", Run(ifaces + """
                class Mem : IStream { int Read() { return 1 }
                 undefined Write(int v) { }
                 int Size { get { return 1 } } }
                interface IOther { }
                IReader r = new Mem()
                IOther o = r
                """) is var unrelated && unrelated.StartsWith("TC001|"), unrelated);
            Check("Interface-Vererbung: generisch (IList<T> : IEnumerable<T>) - foreach kennt den Elementtyp", Run("""
                interface IList<T> : IEnumerable<T> { int Count { get; } }
                class Nums : IList<int> {
                    int Count { get { return 2 } }
                    IEnumerator<int> GetEnumerator() { var l = new List<int>(); l.Add(3); l.Add(4); return l.GetEnumerator() }
                }
                IList<int> n = new Nums()
                int sum = 0
                foreach (x in n) { sum = sum + x }
                print(sum + n.Count)
                """, TypeCheckMode.Strict) is var generic && generic == "|9"
                && Run("interface IList<T> : IEnumerable<T> { }\nclass Nums : IList<int> { IEnumerator<int> GetEnumerator() { return new List<int>().GetEnumerator() } }\nIList<int> n = new Nums()\nforeach (x in n) { bool b = x }") is var elem && elem.StartsWith("TC001|"), generic);

            // through an interface type only what the interface (and the ones it extends) declares is reachable (TC008) - a cast reaches the rest
            string reach = ifaces + """
                class Mem : IStream {
                    int last = 5
                    int Read() { return this.last }
                    undefined Write(int v) { this.last = v }
                    int Size { get { return 1 } }
                    int Extra() { return 9 }
                }
                IStream s = new Mem()

                """;
            Check("Interface-Typ: nicht deklarierte Mitglieder sind TC008 (lax Warnung, Feld, Methode, Methode als Wert), falsche Stelligkeit TC002", Run(reach + """
                print(s.last)
                print(s.Extra())
                var f = s.Extra
                print(s.Read(1))
                """) is var laxReach && laxReach.StartsWith("TC008 TC008 TC008 TC019 TC002|5\n9"), laxReach);
            Check("Interface-Typ: nicht deklarierte Mitglieder sind strikt Fehler (TC008)", Run(reach + "IReader r = s\nprint(r.Size)\nprint(s.Extra())", TypeCheckMode.Strict) == "|ERROR TC008 TC008");
            Check("Interface-Typ: geerbte Interface-Mitglieder, Property, Besitz-Methoden und Cast strikt ohne Befund", Run(reach + """
                s.Write(3)
                IReader r = s
                print(s.Read() + r.Read() + s.Size + (s!Mem).Extra() + (r!Mem).last)
                var other = new Mem()
                try other.TakeTo(s)
                IStream t = new Mem()
                t.TakeLocal()
                try t.TakeTo(s)
                """, TypeCheckMode.Strict) is var allowed && allowed == "|19", allowed);
        }

        // named arguments: each goes to the parameter of its name (its type is checked there); an unknown name and a parameter without value are errors
        {
            string decl = "class C { static int F(int a, string b = \"x\", int c = 3) { return a + c }\n construct(int size, bool open = false) { } }\n";
            Check("Benannte Argumente: der Typ wird am Parameter des Namens geprueft", Run(decl + "print(C.F(1, c: \"no\"))") is var named && named.StartsWith("TC002"), named);
            Check("Benannte Argumente: passende Namen ohne Befund", Run(decl + "print(C.F(c: 10, a: 1))\nvar o = new C(open: true, size: 2)\nprint(1)") == "|11\n1");
            Check("Benannte Argumente: ein unbekannter Name ist ein Fehler (TC011)", Run(decl + "print(C.F(1, d: 2))") is var unknown && unknown.Contains("ERROR TC011"), unknown);
            Check("Benannte Argumente: ein Pflichtparameter ohne Wert ist ein Fehler (TC011)", Run(decl + "print(C.F(c: 2))") is var missing && missing.Contains("ERROR TC011"), missing);
            Check("Benannte Argumente: auch beim Konstruktor (TC011)", Run(decl + "var o = new C(open: true)") is var ctor && ctor.Contains("ERROR TC011"), ctor);
        }

        // `x!Dog` where x can never be a Dog: TC013 (a warning in lax mode, an error in strict mode); possible casts, interfaces a subclass joins, `any` pass
        {
            string decl = """
                interface IAnimal { string Name() }
                interface IPet { }
                interface IRobot { }
                class Animal { }
                class Dog : Animal, IAnimal { string Name() { return "Rex" } }
                class Puppy : Dog, IPet { }
                class Cat : Animal { }
                class Car { }

                """;
            Check("Cast: unverwandte Klassen sind TC013 (lax eine Warnung, das Programm laeuft)", Run(decl + "Cat c = undefined\nprint(c!Dog)") is var unrelated && unrelated == "TC013|undefined", unrelated);
            Check("Cast: strikt ist TC013 ein Fehler", Run(decl + "Cat c = new Cat()\nprint(c!Dog)", TypeCheckMode.Strict) is var strict && strict == "|ERROR TC013", strict);
            Check("Cast: Klasse zu Interface, das keine Unterklasse hat, und zurueck", Run(decl + "Car car = undefined\nprint(car!IAnimal)\nIAnimal a = undefined\nprint(a!Cat)\nprint(a!Car)") is var iface
                && iface == "TC013 TC013 TC013|undefined\nundefined\nundefined", iface);
            Check("Cast: moegliche Casts ohne Befund (Ab- und Aufwaerts, Interface ueber Unterklasse, zwei Interfaces, any, Zahlen)", Run(decl + """
                Animal a = new Puppy()
                print(a!Dog.Name())
                Dog d = new Puppy()
                print(d!Animal)
                print(d!IPet)
                IAnimal ia = d
                print(ia!Dog.Name() + " " + ia!Puppy)
                IAnimal none = undefined
                print(none!IRobot)
                any x = new Cat()
                try { print(x!Dog) } catch (ArgumentException e) { print("any") }
                print(65!char)
                """) is var fine && fine == "|Rex\n<object Puppy>\n<object Puppy>\nRex <object Puppy>\nundefined\nany\nA", fine);
        }

        // `override` (SPEC 5.1): a member that replaces an inherited one says so (TC014: lax a warning, strict an error); `override` that replaces nothing is TC015
        {
            const string ovBase = """
                interface ISpeaker { string Speak() }
                class Animal {
                    string Speak() { return "..." }
                    string Hello(string s) { return s }
                    int Legs { get { return 4 } }
                    bool operator==(any o) { return false }
                    static string Kind() { return "animal" }
                }
                class Box<T> { string Put(T x) { return "box" } }

                """;
            Check("override: fehlt bei Methode, Property, Operator - lax TC014-Warnungen, das Programm laeuft", Run(ovBase + """
                class Dog : Animal {
                    string Speak() { return "Woof" }
                    int Legs { get { return 4 } }
                    bool operator==(any o) { return true }
                }
                print(new Dog().Speak())
                """) is var missing && missing == "TC014 TC014 TC014|Woof", missing);
            Check("override: strikt ist TC014 ein Fehler", Run(ovBase + "class Dog : Animal { string Speak() { return \"Woof\" } }\nprint(1)", TypeCheckMode.Strict) is var strictMissing && strictMissing == "|ERROR TC014", strictMissing);
            Check("override: markiert ohne Befund (auch geerbt ueber zwei Stufen, generische Basis, andere Ueberladung, statisch ohne override, Interface ohne override)", Run(ovBase + """
                class Dog : Animal, ISpeaker {
                    override string Speak() { return "Woof" }
                    override int Legs { get { return 3 } }
                    override bool operator==(any o) { return true }
                    string Hello(int i) { return "int" }
                    static string Kind() { return "dog" }
                }
                class Puppy : Dog { override string Speak() { return "Yip" } }
                class IntBox : Box<int> { override string Put(int x) { return "int box" } }
                class Robot : ISpeaker { string Speak() { return "beep" } }
                Animal a = new Puppy()
                print(a.Speak() + " " + a.Legs + " " + new IntBox().Put(1) + " " + new Robot().Speak() + " " + new Dog().Hello(1) + " " + new Dog().Hello("s"))
                """, TypeCheckMode.Strict) is var marked && marked == "|Yip 3 int box beep int s", marked);
            Check("override: ohne passendes Basis-Mitglied ist TC015 (auch lax ein Fehler): Tippfehler, andere Parameter, Property, nur Interface", Run(ovBase + """
                class Dog : Animal, ISpeaker {
                    override string Speek() { return "typo" }
                    override string Hello(int i) { return "int" }
                    override int Tail { get { return 1 } }
                }
                class Robot : ISpeaker { override string Speak() { return "beep" } }
                print(1)
                """) is var nothing && nothing == "|ERROR TC015 TC015 TC015 TC015", nothing);
            Check("override: die Meldung nennt die Basisklasse und wie es geht", CompileTextStrict(ovBase + "class Dog : Animal { string Hello(string name) { return name } }")
                is var text && text.Contains("The method 'Hello' of 'Dog' replaces the one of its base class 'Animal' - mark it 'override' (override string Hello(string name))"), text);
            // inside a namespace the base class is found like any other type name: written short (`B : A` for N.A), in full, or in another namespace (#using)
            const string nsBase = """
                namespace Zoo {
                    class Animal {
                        string Speak() { return "..." }
                        int Legs { get { return 4 } }
                    }
                }
                namespace Farm {
                    class Beast { string Speak() { return "beast" } }
                }

                """;
            Check("override im Namespace: Basis kurz, voll qualifiziert und aus anderem Namespace - markiert ohne Befund (strikt)", Run(nsBase + """
                namespace Zoo {
                    class Dog : Animal { override string Speak() { return "Woof" } override int Legs { get { return 3 } } }
                    class Cat : Zoo.Animal { override string Speak() { return "Miau" } }
                    class Puppy : Dog { override string Speak() { return "Yip" } }
                    class Cow : Farm.Beast { override string Speak() { return "Muh" } }
                }
                #using Farm
                namespace Barn { class Pig : Beast { override string Speak() { return "Oink" } } }
                Zoo.Animal a = new Zoo.Puppy()
                print(new Zoo.Dog().Speak() + " " + new Zoo.Dog().Legs + " " + new Zoo.Cat().Speak() + " " + a.Speak() + " " + new Zoo.Cow().Speak() + " " + new Barn.Pig().Speak())
                """, TypeCheckMode.Strict) is var nsMarked && nsMarked == "|Woof 3 Miau Yip Muh Oink", nsMarked);
            Check("override im Namespace: fehlt - TC014 auch bei kurz geschriebener Basis (Methode, Property, zwei Stufen, anderer Namespace)", Run(nsBase + """
                namespace Zoo {
                    class Dog : Animal { string Speak() { return "Woof" } int Legs { get { return 3 } } }
                    class Cat : Zoo.Animal { string Speak() { return "Miau" } }
                    class Puppy : Dog { string Speak() { return "Yip" } }
                }
                #using Farm
                namespace Barn { class Pig : Beast { string Speak() { return "Oink" } } }
                print(new Zoo.Puppy().Speak())
                """) is var nsMissing && nsMissing == "TC014 TC014 TC014 TC014 TC014|Yip", nsMissing);
            Check("override im Namespace: TC015 nur ohne passendes Basis-Mitglied", Run(nsBase + """
                namespace Zoo { class Dog : Animal { override string Speek() { return "typo" } } }
                print(1)
                """) is var nsNothing && nsNothing == "|ERROR TC015", nsNothing);
            Check("override im Namespace: die Meldung nennt Klasse und Basis voll", CompileTextStrict(nsBase + "namespace Zoo { class Dog : Animal { string Speak() { return \"Woof\" } } }") is var nsText && nsText.Contains("The method 'Speak' of 'Zoo.Dog' replaces the one of its base class 'Zoo.Animal' - mark it 'override' (override string Speak())"), nsText);
        }

        // `byte` (and `word`) are raw data without a unit (SPEC 8.10): a unit there is TC017 - an error in lax mode too; at run time the stored value has none
        {
            Check("byte ohne Einheit: Wert mit Einheit, Einheit an der Deklaration, Array, Feld, Parameter sind TC017 (auch lax)", Run("""
                byte c = 5m
                byte d : m = 5
                byte e[] = [1m, 2m]
                class K { byte f : s
                 byte g = 3s
                 undefined Set(byte v : mm) { } }
                byte h = 1
                h = 4s
                """) is var rawUnits && rawUnits == "|ERROR TC017 TC017 TC017 TC017 TC017 TC017 TC017", rawUnits);
            Check("byte ohne Einheit: die Meldung nennt den Typ", CompileText("byte c = 5m") is var rawText && rawText.Contains("A value in 'm' cannot be stored in 'c' of type 'byte'"), rawText);
            Check("byte ohne Einheit: zur Laufzeit verliert der gespeicherte Wert die Einheit; int[8] behaelt sie", Run("""
                any x = 5m
                byte c = x
                byte d = 7
                d = x
                int[8] i8 = x
                print(c + " " + d + " " + x!byte + " " + i8)
                byte ok = 5m::unitless
                print(ok)
                """) is var rawRun && rawRun == "|5 5 5 5m\n5", rawRun);
            Check("word ohne Einheit: Wert mit Einheit, Einheit an der Deklaration, Feld, Parameter sind TC017 (auch lax)", Run("""
                word a = 5m
                word b : s = 1
                class W { word f : kg
                 undefined Set(word v : mm) { } }
                word c = 1
                c = 2s
                """) is var wordUnits && wordUnits == "|ERROR TC017 TC017 TC017 TC017 TC017", wordUnits);
            Check("word: int geht in word, word in float und byte; word in int will !int (TC001), float in word auch; word op int ist word", Run("""
                word w = 5
                int i = 3
                w = i
                float f = w
                byte b = w
                int j = w!int
                var x = w + i
                word y = x * 2
                print(w + " " + f + " " + b + " " + j + " " + y + " " + (x is of word) + " " + (-x is of word) + " " + (~x is of word) + " " + ((x + 0.5) is of float))
                """) is var wordOk && wordOk == "|3 3 3 3 12 True True True True", wordOk);
            Check("word: int i = w und word v = 2.5 sind TC001 (lax: zur Laufzeit eine ArgumentException); strikt ein Fehler",
                Run("word w = 5\ntry { int i = w\nprint(0) } catch (ArgumentException e) { print(1) }\nword v = 1\ntry { v = 2.5\nprint(0) } catch (ArgumentException e) { print(1) }") is var wordBad && wordBad == "TC001 TC001|1\n1"
                && Run("word w = 5\nint i = w\nprint(1)", TypeCheckMode.Strict) == "|ERROR TC001", wordBad);
            Check("word: 'word' mit Breite ist ein Syntaxfehler", CompileText("word[16] w = 1") is var wordWidth && wordWidth.Contains("'word' has the width of the target's machine word"), wordWidth);
        }

        // the standard library keeps its declared types honest: every import compiles without a single type conflict or untyped parameter, in strict mode
        foreach (var import in new[] { "io", "console", "machine", "dos", "xms", "x86", "filesystem", "dialogs", "ui.docking", "json", "circuits", "circuits.ui", "net", "process", "hash", "compression", "http", "tls", "audio", "usb", "tracker", "wifi", "gpio", "i2c", "spi", "random", "time", "linq", "reflection", "ui", "scene3d", "scene3d.terrain", "scene3d.effects", "scene3d.physics", "graphics", "windows", "devices", "data", "data.sqlite", "data.memory", "data.mysql", "data.sqlserver", "data.postgres", "http.data" })
        {
            Environment.SetEnvironmentVariable("FIRE_TYPECHECK_ALL", "1");
            try { Check($"Bibliothek {import}: strikt typgeprueft ohne Befund", Run($"#import \"{import}\"\nprint(1)", TypeCheckMode.Strict, true) is var found && found == "|1", found); }
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
                finally { Value.SingleFloats = false; Value.Ints32 = false; Value.Ints16 = false; }
            }
        }
        Console.WriteLine($"{total} Ausschnitte, {bad} mit Feld-/Property-/var-Fehler");
    }
}
