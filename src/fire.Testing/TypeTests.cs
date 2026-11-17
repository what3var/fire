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
    private static string Run(string source, TypeCheckMode? mode, bool verbose) => Run(source, mode, verbose, VmExecutionMode.Release);

    /// <summary>Run in a debug build of the VM (`forge run -m debug`, the default of the editor).</summary>
    private static string RunDebug(string source) => Run(source, null, false, VmExecutionMode.Debug);

    private static string Run(string source, TypeCheckMode? mode, bool verbose, VmExecutionMode execution)
    {
        var warnings = new DiagnosticCollector();
        var lines = new List<string>();
        string errors = "";
        TestCases.CollectSource("types", source, mode?.ToString());
        Linker.ProcessDefaultTypeChecking = mode;
        try
        {
            using (warnings.Begin())
            {
                var session = RuntimeSession.Build(new[] { source }, execution, a => { lines.Add(a[0].ToString()!); return Value.MakeUndefined(); });
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
        TestCases.CollectSource("types", source, Linker.ProcessDefaultTypeChecking?.ToString());
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
        // TC033: an index or a size that is certainly no int, `*x` of a value that is certainly no pointer - errors in lax mode too (through any: the ArgumentException)
        Check("TC033: Index (lesen, schreiben, String), Groesse (Array, Puffer, mehrere Dimensionen) und Dereferenzierung der falschen Art sind Fehler (auch lax)",
            Run("int a[] = new int[3]\nstring s = \"abc\"\nfloat f = 1.5\nint x = 5\nprint(a[\"x\"])\na[f] = 1\nprint(s[true])\nint b[] = new int[s]\nbyte c[] = new byte[f]\nint j[][] = new int[2][\"x\"]\nunsafe { print(*x) }\nunsafe { *s = 1 }") is var kindBad
                && kindBad == "|ERROR TC033 TC033 TC033 TC033 TC033 TC033 TC033 TC033", kindBad);
        Check("TC033: int, word, Enum und any als Index und Groesse sowie Zeiger sind erlaubt",
            Run("enum E { A, B }\nint a[] = new int[3]\nword w = 1\nany i = 1\nint x = 5\na[w] = 4\na[E.B] = 5\nprint(a[i] + a[1])\nint b[] = new int[w]\nprint(b.Length)\nunsafe { int* p = &x\n print(*p) }") is var kindOk
                && kindOk == "|10\n1\n5", kindOk);
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
        Check("var mit dem Ergebnis einer String-Methode (class extends string): der deklarierte Typ - strikt kein TC019, falsche Zuweisungen gemeldet",
            Run("string t = \"a,b\"\nvar at = t.IndexOf(\",\")\nvar part = t.Substring(2)\nvar parts = t.Split(\",\")\nvar has = t.Contains(\"a\")\nvar up = t.ToUpper()\nprint(at + 1)\nprint(part + parts.length + has + up)", TypeCheckMode.Strict) is var strs
            && strs == "|2\nb2TrueA,B" && Run("string t = \"ab\"\nint q = t.Substring(1)\nbool b = t.IndexOf(\"b\")", TypeCheckMode.Strict) == "|ERROR TC001 TC001", strs);
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
        Check("defined (SPEC 8.10b): 'int!' sagt, wie es heisst; defined als Typargument ist ein Fehler, ein string (beginnt bei \"\"), ein Array aus Werten (defined int a[]) oder aus strings (defined string s[], ROADMAP 19) nicht",
            Run("class K { int! n }") is var bang && bang.StartsWith("|ERROR E3000")
            && Run("class K { defined string s[2] }\nprint(\"[\" + new K().s[1] + \"]\")") == "|[]" && Run("class K { defined string s }\nprint(\"[\" + new K().s + \"]\")") == "|[]" && Run("var l = new List<defined int>()").StartsWith("|ERROR")
            && Run("class K { defined int a[3] }\nprint(new K().a[2])") == "|0", bang + " / " + Run("class K { defined int a[3] }\nprint(new K().a[2])"));
        string maybeObj = Run("class Dog { }\nDog x\ndefined Dog d = x");
        Check("defined fuer Objekte (SPEC 8.10b): ohne Initialisierer ein Fehler (lokal, statisch, Feld ohne Konstruktor, ein Konstruktor, der es nicht setzt); jeder Konstruktor setzt es: still; defined any ist ein Fehler; ein Wert, der undefined sein kann, ist TC022",
            Run("class Dog { }\ndefined Dog d") is var noValue && noValue.StartsWith("|ERROR E4000")
            && Run("class Dog { }\nclass K { static defined Dog d }").StartsWith("|ERROR E4000")
            && Run("class Dog { }\nclass K { defined Dog d }").StartsWith("|ERROR E4000")
            && Run("class Dog { }\nclass K { defined Dog d\n construct(Dog x) { if (x != undefined) { this.d = x } } }").StartsWith("|ERROR E4000")
            && Run("class Dog { }\nclass K { defined Dog d\n construct() { this.d = new Dog() }\n construct(Dog x) { d = x } }\nprint(new K().d != undefined)") == "|True"
            && Run("defined any a = 1").StartsWith("|ERROR E4000")
            && maybeObj.StartsWith("TC022|")
            && Run("string s\ndefined string t = s") .StartsWith("TC022|")
            && Run("class Dog { }\ndefined Dog d = new Dog()\nd = undefined").StartsWith("TC022|")
            && Run("class Dog { }\nDog x\nx = new Dog()\ndefined Dog d = x\nprint(1)") == "|1", noValue + " / " + maybeObj);
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
            {
                const string animals = "class Animal { }\nclass Dog : Animal { }\ninterface INamed { string Name { get; } }\nclass Bird : Animal, INamed { string Name { get { return \"b\" } } }\n";
                string Prop(string ifaceProp, string classProp) => Run(animals + "interface I { " + ifaceProp + " }\nclass C : I { " + classProp + " }\nprint(\"ok\")");
                var cases = new (string Iface, string Class, string Want)[]
                {
                    ("Animal A { get; }", "Dog A { get; set; }", "|ok"),                 // a getter may give a derived class
                    ("INamed A { get; }", "Bird A { get; set; }", "|ok"),                // ... or a class of the interface
                    ("List<Animal> A { get; }", "List<Dog> A { get; set; }", "|ok"),     // ... or derived type arguments (D10)
                    ("int A { get; }", "defined int A { get; set; }", "|ok"),            // ... or a defined value
                    ("Dog A { set; }", "Animal A { get; set; }", "|ok"),                 // a setter may take more
                    ("any A { get; }", "Dog A { get; set; }", "|ok"),
                    ("Animal A { get; set; }", "Dog A { get; set; }", "|ERROR E4066"),   // get and set: the same type
                    ("Dog A { get; }", "Animal A { get; set; }", "|ERROR E4066"),
                    ("Dog A { get; }", "string A { get; set; }", "|ERROR E4066"),
                    ("List<Animal> A { get; set; }", "List<Dog> A { get; set; }", "|ERROR E4066"),
                    ("defined int A { get; }", "int A { get; set; }", "|ERROR E4066"),
                    ("int A { get; set; }", "defined int A { get; set; }", "|ERROR E4066"),
                    ("Dog A { get; }", "any A { get; set; }", "|ERROR E4066"),
                    ("lambda<int> A { get; }", "lambda<int, int> A { get; set; }", "|ERROR E4066"),
                };
                var wrong = cases.Select(c => (c, got: Prop(c.Iface, c.Class))).Where(r => r.got != r.c.Want).Select(r => $"{r.c.Iface} / {r.c.Class}: {r.got}").ToList();
                Check("Interface-Property: Klassen, Interfaces, Typargumente, defined und Lambdas - der Getter darf Spezielleres geben, der Setter Allgemeineres nehmen (ROADMAP 0)",
                    wrong.Count == 0, string.Join("; ", wrong));
                Check("Interface-Property: die Meldung nennt beide Typen", CompileText(animals + "interface I { Animal A { get; set; } }\nclass C : I { Dog A { get; set; } }")
                    .Contains("Property 'A' of class 'C' is 'Dog', interface 'I' declares it as 'Animal'"), "");
            }
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
            Check("Statisch und Instanz mit demselben Namen ist E4076 (DECISIONS D11), auch ueber die Basisklasse; getrennte Namen gehen",
                Run("class R {\n bool Inside(int a, int b) { return true }\n static bool Inside(R a, R b) { return false }\n}\nprint(1)") is var mixed && mixed.StartsWith("|ERROR E4076")
                && Run("class B { static int F(int a) { return 1 } }\nclass C : B { int F() { return 2 } }\nprint(1)").StartsWith("|ERROR E4076")
                && Run("class R { int G() { return 1 } static int H() { return 2 } }\nprint(R.H() + new R().G())") == "|3", mixed);
            Check("E4076 auch, wenn die abgeleitete Klasse die statische Methode hat und die Basis die fuer Instanzen",
                Run("class A { int Get() { return 1 } }\nclass B : A { static int Get(int a) { return a } }\nprint(1)") is var mixedUp && mixedUp.StartsWith("|ERROR E4076"), mixedUp);
            // An `int` holds an int: a value of an enum that goes into one (TC001 says a cast is meant) is its number there - a variable, a global, a
            // field, a static field, a parameter, a return value, an element of `int a[]`, also through `any` - in a debug build as in a release build
            const string enumIntoInt = """
                enum Color { Red, Green, Blue }
                class H {
                    int f = 1
                    static int s = 1
                    int Take(int p) { return p }
                    int Ret() { any c = Color.Green; return c }
                    undefined Local() {
                        int k = Color.Blue
                        print("local " + k + " " + (k is of int))
                        int j = 1
                        j = Color.Blue
                        print("assigned " + j + " " + (j is of int))
                        any a = Color.Blue
                        int viaAny = a
                        print("any " + viaAny + " " + (viaAny is of int) + " " + (a is of Color))
                        float fl = Color.Blue
                        print("float " + fl + " " + (fl is of float))
                        int arr[] = new int[2]
                        arr[0] = Color.Blue
                        print("element " + arr[0] + " " + (arr[0] is of int))
                    }
                }
                int n = Color.Blue
                print(n)
                print(n is of int)
                print(n + 1)
                var h = new H()
                h.Local()
                h.f = Color.Blue
                print("field " + h.f + " " + (h.f is of int))
                H.s = Color.Green
                print("static " + H.s + " " + (H.s is of int))
                var r = h.Take(Color.Blue)
                print("parameter " + r + " " + (r is of int))
                var q = h.Ret()
                print("return " + q + " " + (q is of int))
                Color c = Color.Blue
                print(c + " " + (c is of Color) + " " + (c is of int) + " " + c!int)
                try { c = 2; print("an int went into a Color") } catch (ArgumentException e) { print(e.message) }
                """;
            const string enumIntoIntOut = "2\nTrue\n3\nlocal 2 True\nassigned 2 True\nany 2 True True\nfloat 2 True\nelement 2 True\nfield 2 True\nstatic 1 True\nparameter 2 True\nreturn 1 True\n"
                + "Blue True False 2\nVariable 'c' is a Color, not Int.";
            Check("Ein Enum-Wert in einer int-Deklaration ist dort seine Zahl (TC001 bleibt): Variable, Feld, statisches Feld, Parameter, Rueckgabe, Element, ueber any",
                Run(enumIntoInt) is var enumInt && enumInt.EndsWith("|" + enumIntoIntOut) && enumInt.StartsWith("TC001") && !enumInt.Contains("ERROR"), enumInt);
            Check("Ein Enum-Wert in einer int-Deklaration: im Debug-Build dasselbe", RunDebug(enumIntoInt) is var enumIntDebug && enumIntDebug == enumInt, enumIntDebug);
            // A `string` holds a string (DECISIONS D14): what goes in becomes its text, `x.ToString()` - a variable, an assignment, a field, a static field, a
            // property, a parameter, a return value, an element of `string a[]`, a `T` of List<string>; an object's own ToString() runs, its exception propagates
            const string intoString = """
                enum Color { Red, Green, Blue }
                class P { int x = 3
                    string ToString() { return "P(" + this.x + ")" } }
                class Q { int y = 1 }
                class Bad { string ToString() { throw new ArgumentException("no text") } }
                class H {
                    string f
                    static string s
                    defined string d = "x"
                    string Prop { get; set; }
                    string Take(string p) { return p + "/" + (p == undefined ? -1 : p.Length) }
                    string Ret(any v) { return v }
                }
                any vals[] = [5, 2.5, true, 'c', Color.Blue, 5m, [1], new P(), new Q(), undefined]
                var h = new H()
                foreach (v in vals) {
                    string j = v
                    h.f = v
                    H.s = v
                    h.Prop = v
                    string arr[] = new string[1]
                    arr[0] = v
                    any l = new List<string>()
                    l.Add(v)
                    print(j + "|" + h.f + "|" + H.s + "|" + h.Prop + "|" + h.Take(v) + "|" + h.Ret(v) + "|" + arr[0] + "|" + l[0] + "|" + (j is of string))
                }
                string n = 5
                print("" + n.Length + " " + (n == 5) + " " + (n == "5"))
                try { h.f = new Bad(); print("no exception") } catch (ArgumentException e) { print("field: " + e.message) }
                try { print(h.Take(new Bad())) } catch (ArgumentException e) { print("parameter: " + e.message) }
                try { h.d = undefined; print("undefined went in") } catch (ArgumentException e) { print(e.message) }
                h.d = 7
                print(h.d + " " + (h.d is of string))
                print(new Q().ToString() + " " + 4.ToString() + " " + Color.Red.ToString() + " " + 3m.ToString() + " " + [1].ToString())
                """;
            const string intoStringOut = "5|5|5|5|5/1|5|5|5|True\n2.5|2.5|2.5|2.5|2.5/3|2.5|2.5|2.5|True\nTrue|True|True|True|True/4|True|True|True|True\nc|c|c|c|c/1|c|c|c|True\n"
                + "Blue|Blue|Blue|Blue|Blue/4|Blue|Blue|Blue|True\n5m|5m|5m|5m|5m/2|5m|5m|5m|True\n<array>|<array>|<array>|<array>|<array>/7|<array>|<array>|<array>|True\n"
                + "P(3)|P(3)|P(3)|P(3)|P(3)/4|P(3)|P(3)|P(3)|True\n<object Q>|<object Q>|<object Q>|<object Q>|<object Q>/10|<object Q>|<object Q>|<object Q>|True\n"
                + "undefined|undefined|undefined|undefined|undefined/-1|undefined|undefined|undefined|False\n1 False True\nfield: no text\nparameter: no text\n"
                + "Field 'd' is a string and never undefined.\n7 True\n<object Q> 4 Red 3m <array>";
            Check("Eine string-Deklaration haelt den Text des Werts (D14): Variable, Feld, statisches Feld, Property, Parameter, Rueckgabe, Element, List<string>; ToString wirft",
                Run(intoString) is var intoStr && intoStr.EndsWith("|" + intoStringOut) && !intoStr.Contains("ERROR"), intoStr);
            Check("Eine string-Deklaration haelt den Text des Werts: im Debug-Build dasselbe", RunDebug(intoString) is var intoStrDebug && intoStrDebug == intoStr, intoStrDebug);
            // the type checker: a value that is certainly no string into `string` - TC034 in strict mode, a warning (the conversion is the rule); lax says nothing
            Check("TC034: eine Zahl, ein Enum-Wert, ein Objekt in eine string-Deklaration - strict eine Warnung, lax nichts; ein String und any nie",
                Run("class P { }\nclass K { static string T(string s) { return s } }\nany x = 1\nstring a = 5\nstring b = new P()\nstring c = \"c\"\nstring d = x\nprint(K.T(2) + a + c + d)", TypeCheckMode.Strict) is var strictText
                && strictText.Split('|')[0].Split(' ').Count(c => c == "TC034") == 3 && strictText.EndsWith("|25c1")
                && Run("class P { }\nclass K { static string T(string s) { return s } }\nany x = 1\nstring a = 5\nstring b = new P()\nstring c = \"c\"\nstring d = x\nprint(K.T(2) + a + c + d)", TypeCheckMode.Lax) == "|25c1", strictText);
            // `new Dog[n]` is a Dog array from the start (DECISIONS D10): held in `any` it goes only where an array of Dog or of a base class is declared,
            // and it checks what is stored through every reference
            const string arraysFromAny = """
                class Animal { }
                class Dog : Animal { }
                class Cat : Animal { }
                class Stone { }
                interface INamed { }
                class Bird : Animal, INamed { }
                class Box { Cat cats[]
                    undefined Set(any a) { this.cats = a } }
                class U { static undefined TakeCats(Cat c[]) { print("took " + c.length) } }
                any d = new Dog[2]
                try { Cat c[] = d; print("1 accepted") } catch (ArgumentException e) { print("1 " + e.message) }
                try { Stone s[] = d; print("2 accepted") } catch (ArgumentException e) { print("2 " + e.message) }
                try { Animal a[] = d; print("3 accepted") } catch (ArgumentException e) { print("3 " + e.message) }
                try { Animal a[] = d; a[0] = new Cat(); print("4 accepted") } catch (ArgumentException e) { print("4 " + e.message) }
                try { Animal a[] = d; a[0] = new Dog(); print("5 accepted") } catch (ArgumentException e) { print("5 " + e.message) }
                try { Cat c[]; c = d; print("6 accepted") } catch (ArgumentException e) { print("6 " + e.message) }
                try { var b = new Box(); b.Set(d); print("7 accepted") } catch (ArgumentException e) { print("7 " + e.message) }
                try { U.TakeCats(d); print("8 accepted") } catch (ArgumentException e) { print("8 " + e.message) }
                any an = new Animal[2]
                try { Dog dd[] = an; print("9 accepted") } catch (ArgumentException e) { print("9 " + e.message) }
                try { int n[] = d; print("10 accepted") } catch (ArgumentException e) { print("10 " + e.message) }
                any birds = new Bird[1]
                try { INamed n[] = birds; print("11 accepted") } catch (ArgumentException e) { print("11 " + e.message) }
                try { INamed n[] = d; print("12 accepted") } catch (ArgumentException e) { print("12 " + e.message) }
                any nested = new Dog[2][2]
                try { Cat c[][] = nested; print("13 accepted") } catch (ArgumentException e) { print("13 " + e.message) }
                any strs = new string[2]
                try { Cat c[] = strs; print("14 accepted") } catch (ArgumentException e) { print("14 " + e.message) }
                try { strs[0] = 5; print("15 accepted") } catch (ArgumentException e) { print("15 " + e.message) }
                var x = new Dog[2]
                any viaVar = x
                try { Cat c[] = viaVar; print("16 accepted") } catch (ArgumentException e) { print("16 " + e.message) }
                any empty = []
                try { Cat c[] = empty; print("17 accepted") } catch (ArgumentException e) { print("17 " + e.message) }
                int[8] small[] = new int[4]
                small[0] = 300
                float:m lens[] = new float[2]
                lens[0] = 50cm
                defined string names[] = new string[2]
                print(small[0] + " " + lens[0] + " [" + names[1] + "]")
                """;
            const string arraysFromAnyOut = "1 An array of Dog cannot be stored where an array of Cat is declared.\n2 An array of Dog cannot be stored where an array of Stone is declared.\n3 accepted\n"
                + "4 An element of an array of Dog must be a Dog (or undefined), not Cat.\n5 accepted\n6 An array of Dog cannot be stored where an array of Cat is declared.\n"
                + "7 An array of Dog cannot be stored where an array of Cat is declared.\n8 An array of Dog cannot be stored where an array of Cat is declared.\n"
                + "9 An array of Animal cannot be stored where an array of Dog is declared.\n10 An array of Dog cannot be stored where an array of int is declared.\n11 accepted\n"
                + "12 An array of Dog cannot be stored where an array of INamed is declared.\n13 An array of Dog cannot be stored where an array of Cat is declared.\n"
                + "14 An array of string cannot be stored where an array of Cat is declared.\n15 accepted\n"
                + "16 An array of Dog cannot be stored where an array of Cat is declared.\n17 accepted\n44 0.5m []";
            Check("new Dog[n] ist ein Dog-Array, auch in any: nur in die Deklaration von Dog oder einer Basis (Kovarianz), das Array prueft weiter, was hineinkommt; Zahlen-Arrays nehmen Breite und Einheit der Deklaration",
                Run(arraysFromAny) is var fromAny && fromAny == "|" + arraysFromAnyOut && RunDebug(arraysFromAny) == fromAny, fromAny);
            Check("Arrays zur Uebersetzungszeit: ein fremdes Array und ein Array der Basisklasse in der Deklaration der abgeleiteten sind TC001 (strict: ein Fehler), die Basis-Deklaration nimmt das abgeleitete",
                Run("class Animal { }\nclass Dog : Animal { }\nclass Cat : Animal { }\nDog dogs[] = new Dog[1]\nAnimal all[] = dogs\nAnimal fresh[] = new Dog[1]\nprint(all.length + fresh.length)") is var covariant && covariant == "|2"
                && Run("class Animal { }\nclass Dog : Animal { }\nclass Cat : Animal { }\nDog dogs[] = new Dog[1]\ntry { Cat c[] = dogs } catch (ArgumentException e) { print(e.message) }") == "TC001|An array of Dog cannot be stored where an array of Cat is declared."
                && Run("class Animal { }\nclass Dog : Animal { }\nAnimal all[] = new Animal[1]\ntry { Dog d[] = all } catch (ArgumentException e) { print(e.message) }") == "TC001|An array of Animal cannot be stored where an array of Dog is declared."
                && Run("class Animal { }\nclass Dog : Animal { }\ntry { Dog d[] = new Animal[1] } catch (ArgumentException e) { print(e.message) }") == "TC001|An array of Animal cannot be stored where an array of Dog is declared."
                && Run("class Animal { }\nclass Dog : Animal { }\nDog d[] = new Animal[1]", TypeCheckMode.Strict) == "|ERROR TC001", covariant);
            // DECISIONS D12: a debug build derives `defined` exactly like a release build - [InferDefined] with reflection and JSON, the fields and
            // parameters of a plain program, a field without an initializer that starts at 0
            const string inferDefined = """
                #import "reflection"
                #import "json"
                [InferDefined]
                class Counter {
                    int count = 0
                    int late
                    construct() { this.late = 5 }
                    undefined Add(int n) { this.count = this.count + n }
                }
                class Plain {
                    int n = 3
                    construct() { }
                    undefined Add(int by) { this.n = this.n + by }
                }
                var c = new Counter()
                c.Add(2)
                try { Reflect.Set(c, "count", undefined); print("stored undefined: " + c.count) } catch (ArgumentException e) { print(e.message) }
                print(c.count)
                var j = Json.Deserialize<Counter>("{}")
                print("json missing: " + j.count + " " + j.late)
                try { var k = Json.Deserialize<Counter>("{\"count\": null}"); print("json null: " + k.count) } catch (ArgumentException e) { print(e.message) }
                var p = new Plain()
                p.Add(1)
                Reflect.Set(p, "n", undefined)
                print("plain: " + p.n)
                """;
            const string inferDefinedOut = "|Field 'count' (derived as never undefined: nothing in the program stores undefined in it) is an int and never undefined.\n2\njson missing: 0 0\n"
                + "$.count is an int and never undefined (derived: nothing in the program stores undefined in it).\nplain: undefined";
            Check("[InferDefined] im Debug-Build wie im Release-Build (DECISIONS D12): Reflect.Set und JSON gehen durch die hergeleitete Pruefung, ohne Attribut bleibt das Feld wie deklariert",
                Run(inferDefined) is var inferRelease && inferRelease == inferDefinedOut && RunDebug(inferDefined) is var inferDebug && inferDebug == inferDefinedOut, _lastRun);
            {
                // (without the attribute: what is derived shows in the declared kind the classes are compiled with)
                const string derivedPlain = "class C {\n    int count\n    int step = 2\n    int maybe = 1\n    construct() { count = 0 }\n    undefined Add(int x) { count++; step = step + x }\n"
                    + "    undefined Set(any v) { maybe = v }\n}\nvar c = new C()\nc.Add(3)\nc.Set(4)\nprint(c.count + c.step + c.maybe)";
                string States(VmExecutionMode execution)
                {
                    var rc = new Linker().CompileAndLink(new[] { derivedPlain }, null, null, execution).Program.Classes["C"];
                    return string.Join(" ", new[] { "count", "step", "maybe" }.Select(f => f + "=" + fire.Bytecode.DeclaredKind.IsNonNull(rc.FindFieldRequiredKind(f))));
                }
                string releaseStates = States(VmExecutionMode.Release), debugStates = States(VmExecutionMode.Debug);
                Check("Hergeleitetes defined ohne Attribut: ein Debug-Build leitet dieselben Felder her wie ein Release-Build (count ohne Initialisierer, step; maybe nicht) und laeuft gleich",
                    releaseStates == "count=True step=True maybe=False" && debugStates == releaseStates && Run(derivedPlain) == "|10" && RunDebug(derivedPlain) == "|10", releaseStates + " / " + debugStates);
            }
            // An exception of the method of a message that a `catch` around the `process` takes (in a fire thread - a task of the pool - and in the main
            // program): the VM popped a result the method never returned ("Index was outside the bounds of the array", the end of the thread)
            const string processCatch = """
                class Oops : Exception { construct(string m) : base(m) { } }
                actor Worker {
                    int handled = 0
                    undefined Fail(string why) { throw new Oops(why) }
                    undefined Ok() { this.handled = this.handled + 1 }
                }
                string log = ""
                int done = 0
                fire {
                    var w = new Worker()
                    string out = ""
                    w.Fail("first")
                    try { process w; out = out + "not reached\n" } catch (Oops e) { out = out + "process: " + e.message + "\n" }
                    w.Fail("second")
                    try { while (try process w) { out = out + "not reached\n" } } catch (Oops e) { out = out + "try process: " + e.message + "\n" }
                    w.Ok()
                    w.Fail("third")
                    w.Ok()
                    try {
                        int n = 0
                        while (try process w) { n = n + 1 }
                        out = out + "not reached " + n + "\n"
                    } catch (Exception e) { out = out + "outer: " + e.message + "\n" }
                    int rest = 0
                    while (try process w) { rest = rest + 1 }
                    out = out + "rest " + rest + " handled " + w.handled
                    sync global { log = out; done = 1 }
                }
                while (done < 1) { sync globals }
                print(log)
                actor Main2 { undefined Boom() { throw new Oops("main") } }
                var m = new Main2()
                m.Boom()
                try { process m; print("not reached") } catch (Oops e) { print("main process: " + e.message) }
                m.Boom()
                int k = 5
                try { if (try process m) { print("not reached") } } catch (Oops e) { print("main try process: " + e.message + " " + k) }
                print("end")
                """;
            Check("process: eine Ausnahme der Nachricht, die ein catch um das process faengt (fire-Thread und Hauptprogramm) - kein Laufzeitfehler der VM, der Stack bleibt",
                Run(processCatch) is var processed && processed == "|process: first\ntry process: second\nouter: third\nrest 1 handled 2\nmain process: main\nmain try process: main 5\nend"
                && RunDebug(processCatch) == processed, processed);
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

        // `x!`, `x:`, `x:!` (SPEC 3.1, 3.2): the type checker knows what they give - next to an anchor, for a declaration, without a context - and the
        // compiler emits the conversion it found instead of looking at the anchor at run time. Both ways give the same (AnchorAtRunTimeOverride), in a
        // release and in a debug build.
        {
            string RunAnchor(string source, bool atRunTime, VmExecutionMode mode)
            {
                fire.Compiler.Compiler.AnchorAtRunTimeOverride = atRunTime;
                try { return Run(source, null, false, mode); }
                finally { fire.Compiler.Compiler.AnchorAtRunTimeOverride = null; }
            }
            // every case: the output of the fixed conversion; it must be the output of the conversion at run time, in both builds
            var anchorCases = new (string Name, string Source, string Expected)[]
            {
                ("das Beispiel der Doku: b + a:! ist float km, b: + a:! ohne Anker unitless",
                    "float b = 2km\nint a = 500m\nvar c = b + a:!\nprint(c)\nprint(c is of float)\nfloat d = b: + a:!\nprint(d)",
                    "|2.5km\nTrue\n2500"),
                ("int und int", "int a = 5\nint b = 7\nprint(a! + b)\nprint(a + b!)\nprint(a! + b!)\nprint((a! + b) is of int)", "|12\n12\n12\nTrue"),
                ("float und float", "float a = 5.5\nfloat b = 7\nprint(a! + b)\nprint(a + b!)\nprint(a! * b!)", "|12.5\n12.5\n38.5"),
                ("int und float in beiden Richtungen (ein float zum int wird gerundet, .5 zur geraden Zahl)",
                    "int a = 5\nfloat b = 2.5\nprint(a! + b)\nprint((a! + b) is of float)\nprint(b! + a)\nprint((b! + a) is of int)\nprint(a + b!)\nprint(b + a!)\nprint(a! / b)\nprint(b! / a)\nfloat c = 3.5\nprint(c! + a)",
                    "|7.5\nTrue\n7\nTrue\n7\n7.5\n2\n0\n9"),
                ("Einheiten einer Dimension: der Anker gibt die Einheit, mit ! auch den Typ",
                    "int a = 500m\nfloat b = 2km\nint c = 3km\nprint(c: + a)\nprint(c:! + a)\nprint(b:! + a)\nprint(a + b:!)\nprint(a:! + b)\nprint(b + a:!)\nprint(a: + c:)\nprint(a:! + b:!)\nprint(a: == c:)",
                    "|3500m\n3500m\n2500m\n2500m\n2.5km\n2.5km\n3500\n2500\nFalse"),
                ("eine ganze Zahl in die groebere Einheit des Ankers wird gerundet (TC007)",
                    "int a = 500m\nfloat b = 2km\nint c = 3km\nprint(a: + b)\nprint(c + a:)\nprint(a: < b)", "TC007 TC007 TC007|2km\n3km\nTrue"),
                ("::  uebernimmt nur die Einheit des Ankers", "int a = 500m\nfloat b = 2km\nint c = 3km\nprint(a:: + b)\nprint(b + a::)\nprint(a:: + c::)", "|502km\n502km\n503"),
                ("eine andere Dimension: ':' rechnet in die Basiseinheit und scheitert nie",
                    "int a = 500m\nint k = 2kg\nfloat f = 1.5s\nint n = 4\nprint(a: + k)\nprint(k + a:)\nprint(a: + f)\nprint(a: + n)\nprint(n: + a)\nprint(a:! + f)",
                    "|502kg\n502kg\n501.5s\n504\n504m\n501.5s"),
                ("* und / mit Einheiten", "int a = 1500m\nfloat b = 2km\nprint(a:! * b)\nprint(b / a:!)", "|3km^2\n1.3333333333333333"),
                ("Enum-Operanden: der Anker ist die Zahl des Enum-Werts",
                    "enum Color { Red, Green, Blue }\nint a = 5\nfloat f = 1.5\nColor c = Color.Blue\nprint(c! + a)\nprint(a + c!)\nprint(a! + c)\nprint((a! + c) is of int)\nprint(c! + f)\nprint(f! + c)\nprint(c! + c)",
                    "|7\n7\n7\nTrue\n3.5\n4\n4"),
                ("byte und word",
                    "byte b = 200\nword w = 7\nint a = 5\nfloat f = 2.5\nprint(a! + w)\nprint((a! + w) is of word)\nprint(w! + a)\nprint((w! + a) is of int)\nprint(w! + f)\nprint(f! + w)\nprint(b! + f)\nprint(f! + b)\nprint(a! + b)",
                    "|12\nTrue\n12\nTrue\n9.5\n9\n202.5\n202\n205"),
                ("int[64]", "int[64] big = 5000000000\nint a = 3\nfloat f = 1.5\nprint(a! + big)\nprint(big! + a)\nprint(big! + f)\nprint(f! + big)", "|5000000003\n5000000003\n5000000001.5\n5000000002"),
                ("char als Anker und als Operand", "char ch = 'A'\nint a = 66\nfloat f = 66.4\nprint((a + 0)! == ch)\nprint(ch! + a)\nprint(ch! + f)", "|False\n131\n131.4"),
                ("ein undefinierter Operand wird 0 (wie undefined!int)", "int u\nfloat v\nint a = 5\nfloat f = 1.5\nprint(u! + a)\nprint(u! + f)\nprint(v! + a)\nprint(undefined! + a)\nprint(undefined:! + 2km)", "|5\n1.5\n5\n5\n2km"),
                ("in groesseren Ausdruecken: an jedem Teilschritt neu",
                    "int a = 500m\nfloat b = 2km\nint n = 3\nfloat c = (b + a:!) * n\nprint(c)\nfloat d = b + a:! + a:!\nprint(d)\nprint(n * (a:! + b) > 1km)\nprint(-(a:! + b))\nprint([a:! + b, b][0])\nprint(b + (a + a):!)",
                    "|7.5km\n3km\nTrue\n-2.5km\n2.5km\n3km"),
                ("als Argument, als Rueckgabe, als Initialisierer einer typisierten Deklaration",
                    "class K {\n static any F(any x) { return x }\n static float:km G(int a : any, float b : any) { return b + a:! }\n static int H(float x, int y) { return x! + y }\n}\nint a = 500m\nfloat b = 2km\n"
                    + "print(K.F(b + a:!))\nprint(K.G(a, b))\nprint(K.H(2.6, 3))\nfloat x = b + a:!\nprint(x)\nfloat:m y = b + a:!\nprint(y)\nint z = 2.6! + 1\nprint(z)",
                    "|2.5km\n2.5km\n6\n2.5km\n2500m\n4"),
                ("Anker jeder Herkunft: Feld (auch mit Einheit des Initialisierers), statisches Feld, Property, Rueckgabe, Parameter mit Vorgabewert, var, Array-Element, Schleifenvariable, ?:, +=",
                    "class P {\n    float len = 1cm\n    int n = 3\n    float:m w\n    static float sl = 2km\n    float any : any\n    float Prop : mm { get { return 4mm } }\n    float Get() { return 7 }\n    float:km GetKm() { return 1500m }\n    any GetAny() { return 2km }\n    undefined Par(float d = 1cm, int e : any = 2m, float g : any m = 1m) {\n        int a = 5mm\n        print(d + a:!)\n        print(e + a:)\n        print(g + a:!)\n        print(a:! + d)\n    }\n}\nvar p = new P()\nint a = 5mm\nint u = 4\nprint(p.len + a:!)\nprint(a:! + p.len)\nprint(p.n + a:!)\np.w = 3m\nprint(p.w + a:!)\nprint(P.sl + a:!)\np.any = 2dm\nprint(p.any + a:!)\nprint(p.Prop + a:!)\nprint(p.Get() + u!)\nprint(p.GetKm() + a:!)\nprint(p.GetAny() + a:!)\np.Par()\np.Par(5, 3km, 4mm)\nvar v = 5cm\nprint(v + a:!)\nv = 2m\nprint(v + a:!)\nfloat arr[] = [1.5, 2.5]\nprint(arr[0] + u!)\nfloat:km arrk[] = [1.5km]\nprint(arrk[0] + a:!)\nint loc = 2m\nloc = 50cm\nprint(loc + a:)\nfor (int i = 0; i < 2; i++) { print(i + 2.6!) }\nforeach (var x in [1.5, 2.5]) { print(x + u!) }\nprint((u > 2 ? 1.5 : 2) + u!)\nprint(Math.Sqrt(16) + u!)\nprint(\"abc\".Length + 2.6!)\nfloat f = 2.5\nf += u!\nprint(f)\nint cnt = 2\ncnt += 2.6!\nprint(cnt)\nprint((cnt++) + 2.6!)\nprint(u! + (f = 4.5))\n",
                    "TC007 TC007|1.5cm\n1.5cm\n3.005\n3.005m\n2.000005km\n2.05dm\n9mm\n11\n1.500005km\n2km\n1.5cm\n2m\n1.005m\n1.5cm\n5.005\n3km\n9mm\n5.005\n5cm\n2m\n5.5\n1.500005km\n0m\n3\n4\n5.5\n6.5\n5.5\n8\n6\n6.5\n5\n8\n8.5"),
                ("ein Anker vom Typ any: die Umwandlung bleibt die der Laufzeit", "any a = 5\nany f = 2.5\nint i = 7\nfloat g = 1.5\nprint(i! + f)\nprint(g! + a)\nprint(a! + g)\nprint(f! + i)", "|9.5\n7\n6.5\n9"),
            };
            foreach (var c in anchorCases)
            {
                string fixedRelease = RunAnchor(c.Source, false, VmExecutionMode.Release), fixedDebug = RunAnchor(c.Source, false, VmExecutionMode.Debug);
                string runRelease = RunAnchor(c.Source, true, VmExecutionMode.Release), runDebug = RunAnchor(c.Source, true, VmExecutionMode.Debug);
                Check($"x! / x: am Anker: {c.Name} - fest uebersetzt wie zur Laufzeit, Debug wie Release",
                    fixedRelease == c.Expected && fixedDebug == fixedRelease && runRelease == fixedRelease && runDebug == fixedRelease,
                    $"erwartet: {c.Expected}\nfest (Release): {fixedRelease}\nfest (Debug): {fixedDebug}\nLaufzeit (Release): {runRelease}\nLaufzeit (Debug): {runDebug}");
            }
            // the static type: strict mode takes `var c = b + a:!` (it was TC019), and what is computed with it is checked
            Check("x:! am Anker hat einen statischen Typ: var c = b + a:! ist strikt in Ordnung, eine falsche Zuweisung danach TC001",
                Run("float b = 2km\nint a = 500m\nvar c = b + a:!\nprint(c)", TypeCheckMode.Strict) is var typedAnchor && typedAnchor == "|2.5km"
                && Run("float b = 2km\nint a = 500m\nvar c = b + a:!\nc = \"x\"", TypeCheckMode.Strict) == "|ERROR TC001"
                && Run("int a = 5\nfloat b = 2.5\nvar c = b! + a\nint d = c\nprint(d)", TypeCheckMode.Strict) == "|7", typedAnchor);
            // never more than the run time can give: an anchor declared `any`, a whole number that leaves its dimension (an int or a float)
            Check("x! neben any und x: aus der Dimension heraus: der Typ ist nicht bekannt (strikt TC019), der int-Deklaration bleibt ihre Pruefung",
                Run("any f = 2.5\nint i = 7\nvar c = i! + f\nprint(c)", TypeCheckMode.Strict) is var anyAnchor && anyAnchor == "|ERROR TC019"
                && Run("int a = 5mm\nvar d = a:\nprint(d)") == "TC019|0.005"
                && Run("int a = 5mm\ntry { int e = a:\nprint(e) } catch (ArgumentException x) { print(x.message) }") == "|Variable 'e' is an int, not Float."
                && Run("int a = 5mm\ntry { int e = a:unitless\nprint(e) } catch (ArgumentException x) { print(x.message) }") == "|Variable 'e' is an int, not Float."
                && Run("int a = 2km\nint e = a:m\nprint(e)\nfloat f = 5mm\nfloat g = f:\nprint(g)") == "|2000m\n0.005", anyAnchor);
            // without a context: the type stays, the unit becomes unitless
            Check("ohne Kontext: var c = a! behaelt Typ und Einheit (kein TC019 mehr), a: wird unitless",
                Run("int a = 500m\nvar c = a!\nprint(c)\nprint(c is of int)\nfloat f = 2.5km\nvar g = f:!\nprint(g)\nvar h = f:\nprint(h)", TypeCheckMode.Strict) is var noContext && noContext == "|500m\nTrue\n2500\n2500", noContext);
            // the context of a declaration: `!` is exactly `!int` / `!float` / `!char` / `!word` there, `:` converts into the declared unit
            Check("Kontext aus der Deklaration: int i = 2.6! rundet wie !int, float:km d = a:!, char, word; string und any sind kein Kontext",
                Run("int a = 500m\nfloat f = 2.6\nint i = 2.6!\nprint(i)\nint j = f!\nprint(j)\nint j2 = 2.5!\nprint(j2)\nint j3 = -9.5!\nprint(j3)\nfloat:km d = a:!\nprint(d)\nfloat e = a:!\nprint(e)\nfloat e2 = a!\nprint(e2)\n"
                    + "char c = 65!\nprint(c)\nword w = 7!\nprint(w is of word)\nany x = f!\nprint(x)\nint[8] small = 300.4!\nprint(small)\nint u\nint k = u!\nprint(k)", TypeCheckMode.Strict) is var declared
                && declared == "|3\n3\n2\n-10\n0.5km\n500\n500m\nA\nTrue\n2.6\n44\n0"
                && RunDebug("int i = 2.6!\nprint(i)\nint a = 500m\nfloat:km d = a:!\nprint(d)") == "|3\n0.5km"
                && Run("float f = 2.6\nstring s = f!\nprint(s)") == "|2.6", declared);
            Check("Kontext aus der Deklaration: eine ganze Zahl in die groebere deklarierte Einheit wird gerundet (TC007)",
                Run("int a = 500m\nfloat:km d2 = a:\nprint(d2)\nint:km d3 = a:\nprint(d3)") is var declaredLoss && declaredLoss == "TC007 TC007|0km\n0km", declaredLoss);
            Check("Kontext aus der Deklaration: Zuweisung an ein Feld, an eine Variable (auch var), Parameter, benanntes Argument, Rueckgabe",
                Run("class P {\n int n\n float:m len\n undefined Set(float v, int mm : mm) { this.n = v!\n this.len = mm:! }\n}\nclass K {\n static int I(int x) { return x }\n static float:km D(float d : km) { return d }\n"
                    + " static int R(float x) { return x! }\n static float:km RU(int a : any) { return a:! }\n int Inst(int x, float y) { return x }\n}\nvar p = new P()\np.Set(2.6, 1500mm)\nprint(p.n)\nprint(p.len)\nint q = 1\nq = 7.7!\nprint(q)\n"
                    + "var v = 1\nv = 2.4!\nprint(v)\nfloat:km dist = 1km\nint steps = 250m\ndist = steps:!\nprint(dist)\nint a = 500m\nprint(K.I(2.6!))\nprint(K.D(a:!))\nprint(K.R(3.5))\nprint(K.RU(a))\nprint(new K().Inst(2.6!, 1))\nprint(K.I(x: 2.6!))",
                    TypeCheckMode.Strict) is var targets && targets == "|3\n1.5m\n8\n2\n0.25km\n3\n0.5km\n4\n0.5km\n3\n3", targets);
            // what always fails is said at compile time (TC013), the run time keeps its message; an undefined anchor
            Check("x! neben einem string oder bool und x!string / x!bool wandeln nichts um: TC013, ein Fehler auch lax; neben any die ArgumentException mit dem Hinweis",
                Run("int a = 5\nstring s = \"x\"\ntry { print(a! + s) } catch (ArgumentException e) { print(e.message) }") is var toText && toText == "|ERROR TC013"
                && Run("int a = 5\nstring s = \"x\"\nprint(s + a!)", TypeCheckMode.Strict) == "|ERROR TC013"
                && Run("int a = 5\nbool t = true\nprint(a! == t)") == "|ERROR TC013"
                && Run("print(5!string)") == "|ERROR TC013" && Run("print(1!bool)") == "|ERROR TC013"
                && Run("string s = \"x\"\nbool t = true\nprint(s! + \"y\")\nprint(s!string)\nprint(t!bool)\nprint(t! == true)", TypeCheckMode.Strict) == "|xy\nx\nTrue\nTrue"
                && Run("any s = \"x\"\nint a = 5\ntry { print(a! + s) } catch (ArgumentException e) { print(e.message) }")
                    == "|Cannot coerce type Int to String: '!' does not make the text of a value (write \"\" + x or x.ToString())."
                && Run("any v = 5\ntry { print(v!string) } catch (ArgumentException e) { print(e.message) }").StartsWith("|Cannot coerce type Int to String:")
                && Run("any t = true\nint a = 5\ntry { print(a! == t) } catch (ArgumentException e) { print(e.message) }") == "|Cannot coerce type Int to Bool.", toText);
            Check("x! neben einem undefinierten Anker: == und != melden den undefinierten Anker, die Rechnung den undefinierten Operanden",
                Run("int a = 5\nint u\ntry { print(a! == u) } catch (ArgumentException e) { print(e.message) }\ntry { print(a! + u) } catch (Exception e) { print(e.message) }") is var undefAnchor
                && undefAnchor == "|Cannot coerce type Int to Undefined: the operand that gives '!' its type is undefined.\nType Undefined is not numeric.", undefAnchor);
            // the parser: `name:` at the start of an argument is the unit conversion when nothing follows that could be a value
            Check("Parser: F(a:!), F(a:), F(a:, b), F(a:!int), F(a:! + b) sind Argumente mit Umwandlung; F(size: 2) und F(flag: !x) bleiben benannt",
                Run("class K {\n static any F(any a) { return a }\n static any G(any a, any b) { return \"\" + a + \"|\" + b }\n static any N(any size, bool flag = false) { return \"\" + size + \"|\" + flag }\n}\nint a = 500m\nbool flag = true\n"
                    + "print(K.F(a:!))\nprint(K.F(a:))\nprint(K.G(a:, 2))\nprint(K.G(a:!, 2))\nprint(K.G(1, a:!))\nprint(K.F(a:!int))\nprint(K.F(a:! + 2km))\nprint(K.F(a:! * 2))\nprint(K.N(size: 2))\nprint(K.N(size: 2, flag: !flag))\n"
                    + "print(K.N(1, flag:!flag))\nprint(K.F(a::))\nvar arr = [1, 2, 3]\nint ix = 1\nprint(arr[ix!])\nprint(arr[ix:])\nvar lam = (any v) => v\nprint(lam(a:!))\nprint(lam(a:))\nvar lam2 = (int v : any) => v:!\nprint(lam2(a))") is var argForms
                && argForms == "TC007|500\n500\n500|2\n500|2\n1|500\n500\n2km\n1000\n2|False\n2|False\n1|False\n500\n2\n2\n500\n500\n500", argForms);
            // (decision of the owner: `name:` is a named argument only at the START of an argument - `F(a: -b)`, `F(x, a: - b)`; anywhere else in an argument
            // the usual parsing applies - `F(1 + a: - b)`, `F(G(a:) - b)`, `F((a: - b))` are expressions with the conversion `a:`)
            Check("Parser: nur am Anfang eines Arguments ist `name:` benannt - F(a: -b), F(x, a: - b); F(1 + a: - b), F(G(a:) - b), F((a: - b)) sind Ausdruecke",
                Run("class K {\n static string F(int first = 0, int a = 100) { return \"first=\" + first + \" a=\" + a }\n static int G(int v) { return v * 10 }\n}\nint a = 5\nint b = 2\n"
                    + "print(K.F(a: -b))\nprint(K.F(a: - b))\nprint(K.F(a, a: - b))\nprint(K.F(1 + a: - b))\nprint(K.F(K.G(a:) - b))\nprint(K.F((a: - b)))") is var startOnly
                && startOnly == "|first=0 a=-2\nfirst=0 a=-2\nfirst=5 a=-2\nfirst=4 a=100\nfirst=48 a=100\nfirst=3 a=100", startOnly);
        }

        // members of one name (DECISIONS D11): static and instance are not mixed - methods, fields and properties alike, also over base classes (E4076);
        // a field or a property is declared once (E4077)
        {
            Check("E4077: zwei Properties, zwei Felder, Feld und Property mit demselben Namen in einer Klasse (vorher E5000 oder still das letzte)",
                Run("class A { int Size { get { return 1 } }\n int Size { get { return 2 } } }\nprint(1)") is var twice && twice == "|ERROR E4077"
                && Run("class A { int Count { get; set; } = 5\n int Count { get; set; } = 6 }\nprint(1)") == "|ERROR E4077"
                && Run("class A { int n = 1\n int n = 2 }\nprint(1)") == "|ERROR E4077"
                && Run("class A { static int n = 1\n static int n = 2 }\nprint(1)") == "|ERROR E4077"
                && Run("class A { int n = 1\n int n { get { return 2 } } }\nprint(1)") == "|ERROR E4077", twice);
            Check("E4076: statisch und Instanz gemischt - Feld/Feld, Property/Property, Property/Methode, Methode/Feld",
                Run("class A { static int n = 1\n int n = 2 }\nprint(1)") is var mixedMembers && mixedMembers == "|ERROR E4076"
                && Run("class A { static int Size { get { return 1 } }\n int Size { get { return 2 } } }\nprint(1)") == "|ERROR E4076"
                && Run("class A { static int N { get { return 1 } }\n int N() { return 2 } }\nprint(1)") == "|ERROR E4076"
                && Run("class A { static int N() { return 1 }\n int N = 2 }\nprint(1)") == "|ERROR E4076", mixedMembers);
            Check("E4076 auch ueber die Basisklasse, fuer Felder und Properties",
                Run("class B { static int n = 1 }\nclass A : B { int n = 2 }\nprint(1)") is var mixedBase && mixedBase == "|ERROR E4076"
                && Run("class B { int N { get { return 1 } } }\nclass A : B { static int N { get { return 2 } } }\nprint(1)") == "|ERROR E4076", mixedBase);
            Check("erlaubt bleibt: ein Feld der Basisklasse in der abgeleiteten noch einmal, Methode und Feld bzw. Property desselben Namens, eine Auto-Property",
                Run("class B { int n = 1 }\nclass A : B { int n = 2 }\nclass C { int n = 1\n int n() { return 2 } }\nclass D { int N { get { return 1 } }\n int N() { return 2 } }\nclass E { int Count { get; set; } = 5\n static int Total { get; set; } = 1 }\n"
                    + "print(new A().n)\nprint(new C().n + new C().n())\nprint(new D().N + new D().N())\nprint(new E().Count + E.Total)") is var allowedMembers && allowedMembers == "|2\n3\n3\n6", allowedMembers);
        }

        // an array as a return type is checked like an array stored in a variable of that type (it was not checked at all)
        {
            string arrays = "class Animal { }\nclass Cat : Animal { }\nclass Dog : Animal { }\nclass K {\n static Cat[] F(any a) { return a }\n static Animal[] G(any a) { return a }\n static Cat[] H() { return new Cat[2] }\n"
                + " static int[] I(any a) { return a }\n static string[] S(any a) { return a }\n static float[] FL(any a) { return a }\n static int[] Lit() { return [1, 2] }\n static float:m[] M(any a) { return a }\n int[] Prop { get { return \"no\" } }\n}\n"
                + "class U { static undefined T(lambda f) { try { print(f()) } catch (ArgumentException e) { print(e.message) } } }\nany d = new Dog[2]\n";
            const string arraysOut = "An array of Dog cannot be stored where an array of Cat is declared.\n2\n2\nAn array of int is declared here, not String.\n"
                + "An element of an array of int must be an int (or undefined), not Float.\n1\n1\n2\n0.01m\nAn array of int is declared here, not Int.\nundefined\n"
                + "An array of int is declared here, not String.";
            string arrayCalls = "U.T(() => K.F(d).length)\nU.T(() => K.G(d).length)\nU.T(() => K.H().length)\nU.T(() => K.I(\"text\"))\nU.T(() => K.I([1.5, 2.5])[0])\nU.T(() => K.S([1, 2])[0])\nU.T(() => K.FL([1, 2])[0])\nU.T(() => K.Lit()[1])\n"
                + "U.T(() => K.M([1cm, 2cm])[0])\nU.T(() => K.I(5))\nU.T(() => K.I(undefined))\nU.T(() => new K().Prop)";
            Check("Array als Rueckgabetyp: geprueft wie die Zuweisung an eine Array-Variable des Typs (Klasse, int, string, float, Einheit, Property), undefined geht",
                Run(arrays + arrayCalls) is var arrayReturn && arrayReturn.EndsWith("|" + arraysOut) && RunDebug(arrays + arrayCalls).EndsWith("|" + arraysOut), arrayReturn);
            Check("Array als Rueckgabetyp: wo der Typ bekannt ist TC003",
                Run("class Cat { }\nclass Dog { }\nclass K {\n static Cat[] F() { Dog d[] = new Dog[1]\n return d }\n static int[] G() { return \"s\" }\n}\nprint(1)") is var arrayStatic && arrayStatic == "TC003 TC003|1", arrayStatic);
        }

        // `new int[n]` remembers its kind (DECISIONS D10): width and unit stay open for the first declaration, another kind is refused
        {
            string open = "class Cat { }\nclass U { static undefined T(lambda f) { try { print(f()) } catch (ArgumentException e) { print(e.message) } } }\n"
                + "U.T(() => { any d = new int[2]\n Cat c[] = d\n return \"Cat\" })\n"
                + "U.T(() => { any f = new float[2]\n int c[] = f\n return \"int\" })\n"
                + "U.T(() => { any d = new int[2]\n float c[] = d\n c[0] = 1\n return c[0] is of float })\n"
                + "U.T(() => { any d = new int[4]\n int[8] a[] = d\n a[0] = 300\n return a[0] })\n"
                + "U.T(() => { any d = new float[2]\n float:m g[] = d\n g[0] = 5cm\n return g[0] })\n"
                + "U.T(() => { any d = new int[2]\n string s[] = d\n return \"string\" })\n"
                + "U.T(() => { any b = new bool[2]\n int c[] = b\n return \"int\" })\n"
                + "U.T(() => { any b = new int[2]\n bool c[] = b\n return \"bool\" })\n"
                + "U.T(() => { any b = new char[2]\n char c[] = b\n c[0] = 'x'\n return c[0] })\n"
                + "U.T(() => { any g = new int[2][3]\n float c[][] = g\n any h = new int[2][3]\n Cat cc[][] = h\n return \"Cat\" })\n"
                + "U.T(() => { any d = new int[2]\n d[0] = 7\n int c[] = copy d\n return c[0] })\n"
                + "U.T(() => { any d = new int[3]\n byte b[] = d\n return b.length })\n";
            const string openOut = "An array of int cannot be stored where an array of Cat is declared.\nAn array of float cannot be stored where an array of int is declared.\nTrue\n44\n0.05m\n"
                + "An array of int cannot be stored where an array of string is declared.\nAn array of bool cannot be stored where an array of int is declared.\n"
                + "An array of int cannot be stored where an array of bool is declared.\nx\nAn array of int cannot be stored where an array of Cat is declared.\n7\n3";
            Check("new int[n] merkt sich seine Art: geht in int (jede Breite, jede Einheit) und float, nicht in eine Klasse, string, bool; float nicht in int",
                Run(open) is var openKind && openKind.EndsWith("|" + openOut) && RunDebug(open).EndsWith("|" + openOut), openKind);
            Check("new int[n]: die Deklarationen der Doku gehen weiter (int[8] a[] = new int[4], float:m f[] = new float[2], float f[] = new int[2]); wo der Typ bekannt ist TC001",
                Run("int[8] a[] = new int[4]\na[0] = 300\nfloat:m f[] = new float[2]\nf[0] = 5cm\nfloat g[] = new int[2]\ng[0] = 1\nint grid[][] = new int[2][2]\ngrid[1][1] = 4\nprint(a[0] + \" \" + f[0] + \" \" + g[0] + \" \" + grid[1][1])", TypeCheckMode.Strict) is var openDocs
                && openDocs == "|44 0.05m 1 4" && Run("class Cat { }\nCat c[] = new int[2]", TypeCheckMode.Strict) == "|ERROR TC001" && Run("int c[] = new float[2]", TypeCheckMode.Strict) == "|ERROR TC001", openDocs);
        }

        // the standard library keeps its declared types honest: every import compiles without a single type conflict or untyped parameter, in strict mode
        foreach (var import in new[] { "io", "console", "machine", "dos", "xms", "x86", "filesystem", "dialogs", "ui.docking", "json", "circuits", "circuits.ui", "net", "process", "hash", "compression", "ai", "ai.agent", "ai.models", "assistant", "mcp", "http", "tls", "audio", "usb", "tracker", "wifi", "gpio", "i2c", "spi", "random", "time", "linq", "reflection", "ui", "scene3d", "scene3d.terrain", "scene3d.effects", "scene3d.physics", "graphics", "windows", "devices", "data", "data.sqlite", "data.memory", "data.mysql", "data.sqlserver", "data.postgres", "http.data" })
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
