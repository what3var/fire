using fire.Compiler;
using fire.Runtime;
using fire.Values;

/// <summary>Typed declarations in `for`, `foreach (var x in ...)` and the compound assignments (`+=`, `-=`, `*=`, `/=`, `%=`, `^=`, `&=`, `|=`, `#=`, `<<=`, `>>=`): docs/SPEC.md.</summary>
static class SyntaxTests
{
    private static int _failures;

    private static void Check(string title, bool ok, string? detail = null)
    {
        if (!ok) _failures++;
        Console.WriteLine(ok ? $"OK: Syntax: {title}" : $"FEHLER: Syntax: {title}" + (detail == null ? "" : $"\n{detail}"));
    }

    private static string Output(string source)
    {
        var lines = new List<string>();
        try
        {
            var session = RuntimeSession.Build(new[] { source }, VmExecutionMode.Release, a => { lines.Add(a[0].ToString()!); return Value.MakeUndefined(); });
            session.Run();
            Value.SingleFloats = false;
        }
        catch (Exception ex)
        {
            return "ERROR " + string.Join(" | ", CompileDiagnostics.Of(ex).Select(d => d.Code + " " + d.Message));
        }
        return string.Join("\n", lines);
    }

    private static string Output2(string main, string other)
    {
        var lines = new List<string>();
        try
        {
            var session = RuntimeSession.Build(new[] { main, other }, VmExecutionMode.Release, a => { lines.Add(a[0].ToString()!); return Value.MakeUndefined(); });
            session.Run();
        }
        catch (Exception ex)
        {
            return "ERROR " + string.Join(" | ", CompileDiagnostics.Of(ex).Select(d => d.Code + " " + d.Message));
        }
        return string.Join("\n", lines);
    }

    public static void Run()
    {
        Console.WriteLine("=== Syntax: for (int i ...) und Verbundzuweisungen ===");

        Check("for mit typisierter Variable, auch zwei Schleifen mit demselben Namen", Output("""
            var sum = 0
            for (int i = 0; i < 4; i++) { sum += i }
            for (int i = 10; i < 12; i++) { sum += i }
            print(sum)
            """) == "27");

        Check("for mit float/string-Variable", Output("""
            for (float x = 0.5; x < 2.0; x += 0.5) { print(x) }
            """) == "0.5\n1\n1.5");

        Check("foreach (var x in ...)", Output("""
            var total = 0
            foreach (var x in [1, 2, 3]) { total += x }
            print(total)
            """) == "6");

        Check("Ganzzahl-Division und Modulo durch 0: fangbare DivideByZeroException (VM)", Output("""
            int z = 0
            try { print(7 / z) } catch (DivideByZeroException e) { print("div: " + e.message) }
            try { print(7 % z) } catch (DivideByZeroException e) { print("mod: " + e.message) }
            float f = 0.0
            print(1.0 / f)
            print(7 / 2)
            """) == "div: Division by zero.\nmod: Division by zero.\nInfinity\n3");

        Check("Verbundzuweisungen auf Variablen", Output("""
            var a = 10
            a += 5
            a -= 3
            a *= 4
            a /= 6
            a %= 5
            print(a)
            var p = 2
            p ^= 10
            print(p)
            var b = 12
            b &= 10
            b |= 1
            b #= 3
            print(b)
            var s = 1
            s <<= 4
            s >>= 2
            print(s)
            var t = "ab"
            t += "cd"
            print(t)
            """) == "3\n1024\n10\n4\nabcd");

        Check("Verbundzuweisungen auf Felder und Elemente", Output("""
            class C { int n; construct() { this.n = 1 } }
            var c = new C()
            c.n += 41
            c.n *= 2
            var arr = [1, 2, 3]
            var i = 1
            arr[i] += 10
            arr[i + 1] *= 5
            print(c.n)
            print(arr[0] + "," + arr[1] + "," + arr[2])
            """) == "84\n1,12,15");

        {
            string o = Output("""
                var d = 2m
                d += 50cm
                print(d)
                """);
            Check("Verbundzuweisung mit Einheiten", o == "250cm", o);
        }

        Check("der Wert rechts ist ein ganzer Ausdruck (a *= b + c) und rechtsassoziativ", Output("""
            var a = 2
            a *= 3 + 4
            var x = 1
            var y = 1
            x += y += 2
            print(a + " " + x + " " + y)
            """) == "14 4 3");

        Check("ein Ziel mit Aufruf oder ++ wird abgelehnt (es wuerde zweimal ausgewertet)", Output("""
            var arr = [1, 2, 3]
            var i = 0
            arr[i++] += 1
            """).StartsWith("ERROR E3159"));

        Check("Vergleiche und Verschiebungen bleiben, wie sie waren", Output("""
            var a = 3
            var b = a <= 3 && a >= 3 && a != 4
            print(b)
            print(1 << 3)
            print(16 >> 2)
            """) == "True\n8\n4");

        Console.WriteLine("=== Syntax: Bedingungsoperator, do-while, break N, case-Listen, Ziffern-Trenner, readonly ===");

        Check("cond ? a : b, verschachtelt, nur ein Zweig wird ausgewertet", Output("""
            int n = 5
            print(n > 3 ? "big" : "small")
            print(n > 9 ? "a" : n > 4 ? "b" : "c")
            print(true ? 1 : 1 / 0)
            """) == "big\nb\n1");

        Check("do-while laeuft mindestens einmal, continue prueft die Bedingung", Output("""
            int i = 0
            do { i++; if (i == 2) continue; print(i) } while (i < 3)
            do print("once") while (false)
            """) == "1\n3\nonce");

        Check("break 2 / continue 2 verlassen verschachtelte Schleifen (auch foreach und try)", Output("""
            foreach (a in [1, 2, 3]) {
                foreach (b in [10, 20]) {
                    try { if (a == 2) continue 2; if (a == 3 && b == 20) break 2; print(a * b) } catch (Exception e) { }
                }
            }
            print("end")
            """) == "10\n20\n30\nend");

        Check("break 2 ausserhalb von zwei Schleifen ist ein Fehler", Output("""
            while (true) { break 2 }
            """).StartsWith("ERROR"));

        Check("break 2 / continue 2 durch try mit finally: jedes finally laeuft, das innerste zuerst", Output("""
            string log = ""
            for (int a = 0; a < 3; a++) {
                try {
                    foreach (b in [1, 2, 3]) {
                        try {
                            if (a == 1 && b == 2) continue 2
                            if (a == 2 && b == 2) break 2
                            log = log + a + b + " "
                        } finally { log = log + "i" + a + b + " " }
                    }
                } finally { log = log + "o" + a + " " }
            }
            print(log)
            """) is var through && through == "01 i01 02 i02 03 i03 o0 11 i11 i12 o1 21 i21 i22 o2 ", through);

        Check("break 2 aus catch mit finally, throw im finally beendet den Sprung, return durch finally in Schleifen", Output("""
            class E1 : Exception { construct() { } }
            class F {
                static string C() {
                    string log = ""
                    foreach (x in [1, 2]) {
                        try {
                            while (true) {
                                try { throw new E1() }
                                catch (E1 e) { log = log + "c "; break 2 }
                                finally { log = log + "f1 " }
                            }
                        } finally { log = log + "f2 " }
                        log = log + "never "
                    }
                    return log
                }
                static string T() {
                    string log = ""
                    try {
                        foreach (x in [1, 2]) {
                            foreach (y in [1, 2]) {
                                try { try { break 2 } finally { log = log + "f1 "; throw new E1() } } finally { log = log + "f2 " }
                            }
                        }
                    } catch (E1 e) { log = log + "caught" }
                    return log
                }
                static int G() {
                    int n = 0
                    foreach (x in [1, 2, 3]) {
                        foreach (y in [1, 2, 3]) {
                            try { try { n = n + 1; if (y == 2) continue 2 } finally { n = n + 10 } } finally { n = n + 100 }
                        }
                    }
                    return n
                }
                static int R() {
                    foreach (x in [1, 2, 3]) { while (true) { try { return x * 7 } finally { print("R fin") } } }
                    return 0
                }
            }
            print(F.C())
            print(F.T())
            print(F.G())
            print(F.R() + 1)
            """) is var paths && paths == "c f1 f2 \nf1 f2 caught\n666\nR fin\n8", paths);

        Check("Kombinierte Einheit am Rueckgabetyp (float:m/s Speed()), an Parametern und Feldern", Output("""
            class Runner {
                float:m dist
                float:s time
                construct(float:km d, float t : min) { dist = d; time = t }
                float:m/s Speed() { return dist / time }
                float : km/h SpeedKmh() { return dist / time }
                float:any m/s Raw() { return dist / time }
                float:m/s^2 Acc(float:m/s v, float t : s) { return v / t }
                float:km/h Fast { get { return dist / time * 2 } }
                float Slow : km/h { get { return dist / time / 2 } }
                float:m/s Bad() { any d = dist; return d }
            }
            var r = new Runner(10km, 48min)
            print(r.Speed())
            print(r.SpeedKmh())
            print(r.Raw())
            print(r.Acc(36km / 1h, 2s))
            print(r.Fast + " " + r.Slow)
            float:m/s v = 36km / 1h
            print(v)
            try { r.Bad() } catch (UnitMismatchException e) { print(e.message) }
            """) is var units && units == "3.4722222222222223m/s\n12.5km/h\n3.4722222222222223m/s\n5m/s^2\n25km/h 6.25km/h\n10m/s\nExpected unit 'm/s', got: m.", units);

        Check("float[16] und float[8] runden bei jeder Zuweisung (Halbe Genauigkeit, FP8 E4M3)", Output("""
            float[16] h = 0.1
            print(h)
            h = 65519.0
            print(h)
            h = 65520.0
            print(h)
            h = 2049.0
            print(h + " " + 2051.0!float[16] + " " + 0.0000001!float[16] + " " + 0.00000001!float[16])
            float[8] q = 0.1
            print(q)
            q = -1000.0
            print(q + " " + 17.0!float[8] + " " + 19.0!float[8] + " " + 0.001!float[8] + " " + 300.7!float[8])
            class Reg { float[16] v = 0; undefined Set(float x) { v = x } }
            var r = new Reg()
            r.Set(1.0001)
            print(r.v)
            class Calc { static float Narrow(float[8] x) { return x } }
            print(Calc.Narrow(3.3))
            float[16] mm : mm = 1.0001m
            print(mm)
            """) is var small && small == "0.0999755859375\n65504\nInfinity\n2048 2052 1.1920928955078125E-07 0\n0.1015625\n-448 16 20 0.001953125 288\n1\n3.25\n1000mm", small);

        Check("case mit mehreren Werten und Vergleichen", Output("""
            foreach (n in [5, 6, 7, -1]) {
                switch (n) {
                    case 5, 6: print("five-six")
                    case < 0, > 100: print("out")
                    case default: print("other")
                }
            }
            """) == "five-six\nfive-six\nother\nout");

        Check("Ziffern-Trenner und ; nach einem Block", Output("""
            print(1_000_000 + 0xFF_FF)
            foreach (x in [1]) { print(x) }; print("same line")
            """) == "1065535\n1\nsame line");

        Check("readonly-Feld von aussen zuweisen ist ein Fehler", Output("""
            class C { readonly int r; construct() { this.r = 1 } }
            var c = new C()
            c.r = 5
            """).StartsWith("ERROR"));

        Console.WriteLine("=== Syntax: eigene Einheiten (unit) ===");

        Check("unit: Faktor, to/from, Name als Einheit, Schluesselwort als Symbol, Einheit vor der Deklaration benutzt", Output("""
            print(5in + 1ft)
            float d : in = 2.54cm
            print(d + " " + 2Inch + " " + (5in is in m) + " " + 3mV)
            var unit = 5
            print(unit)
            unit Inch: in { coerce m => 0.0254 }
            unit Foot: ft { coerce to in => value * 12 }
            prefixed unit Volt: V
            """) == "17in\n1in 2in True 3mV\n5");
        Check("unit: Umrechnungen aus einer anderen Datei", Output2("float f : ft = 1m\nprint(f)", "unit Foot: ft { coerce from m => value / 0.3048 }") == "3.280839895013123ft");
        string UnitError(string source) => Output(source) is var o && o.StartsWith("ERROR") ? o : "";
        Check("unit: eingebaute Einheit ist ein Fehler, ein Symbol wie Praefix+Einheit nicht (die Einheit gewinnt)", UnitError("unit Meter: m") is var e1 && e1.Contains("already a unit of fire")
            && UnitError("unit Liter: l").Contains("already a unit of fire") && UnitError("unit Gauss: Gs { coerce m => 1 }") == ""
            && Output("unit Gauss: Gs { coerce m => 2 }\nprint(2Gs:m + \" \" + 1ms + \" \" + 1hs:s)").Trim() == "4m 1ms 100s", e1);
        Check("unit: doppelt deklariert, im Kreis definiert, Konvertierungen widersprechen sich", UnitError("unit Inch: in\nunit Zoll: in").Contains("declared twice")
            && UnitError("unit A: aa { coerce cc => 2 }\nunit C: cc { coerce aa => 3 }").Contains("circle")
            && UnitError("unit X: xx { coerce m => 2\n coerce cm => 300 }").Contains("do not agree"));
        Check("unit: Kurzform nur Faktor und Offset, Funktionen brauchen to und from, ohne Konstante, im Block", UnitError("unit D: dBx { coerce mW => 10 ^ (value / 10) }").Contains("factor and an offset")
            && UnitError("unit D: dBx { coerce to mW => 10 ^ (value / 10) }").Contains("needs both directions")
            && UnitError("prefixed unit Celsius: C { coerce K => value + 273.15 }").Contains("takes no prefixes")
            && UnitError("unit X: xx { coerce m => foo }").Contains("computed while compiling")
            && UnitError("if (true) { unit X: xx }").Contains("top level"));
        Check("unit: Offset (Celsius/Fahrenheit) und Funktionen (dBm), auch ueber to/from mit Faktor und Offset", Output("""
            unit Kelvin: K
            unit Celsius: C { coerce K => value + 273.15 }
            unit Fahrenheit: F { coerce to C => (value - 32) * 5 / 9
                                 coerce from C => value * 9 / 5 + 32 }
            prefixed unit Watt: W
            unit DecibelMilliwatt: dBm
            {
                coerce to mW => 10 ^ (value / 10)
                coerce from mW => 10 * Math.Log10(value)
            }
            print(100.0C:F + " " + 32.0F:C + " " + 0.0C:K + " " + (20C + 9F) + " " + (100C > 200F))
            print(30.0dBm:W + " " + 1.0W:dBm + " " + (20.0dBm + 1.0mW) + " " + 20.0C * 2s)
            float t : C = 300.0K
            print(t)
            """) == "212F 0C 273.15K 7C True\n1W 30dBm 20dBm 40K*s\n26.850000000000023C");
        Check("unit: Einheiten eines Programms gelten nicht fuer das naechste", Output("print(5in)") == "5in");

        // `override` (SPEC 5.1): after the access modifier, before the type; only methods, properties and operators of an object
        Check("override: nach dem Zugriffsmodifikator, vor Methode, Property, Operator (auch generisch und mit Attribut)", Output("""
            class A { protected string M() { return "a" } int P { get { return 1 } } bool operator==(any o) { return false } T Id<T>(T x) { return x } }
            class B : A {
                [Doc "x"]
                protected override string M() { return "b" }
                override int P { get { return 2 } }
                override bool operator==(any o) { return true }
                override T Id<T>(T x) { return x }
                string Show() { return this.M() + this.P + (this == 1) + this.Id(5) }
            }
            print(new B().Show())
            """) == "b2True5");
        Check("override: vor dem Zugriffsmodifikator, mit static, an Feld, Konstruktor und Destruktor ist ein Syntaxfehler",
            Output("class A { M() { } }\nclass B : A { override public M() { } }").Contains("The access modifier comes first")
            && Output("class B { static override int M() { return 1 } }").Contains("do not go together")
            && Output("class B { override static int M() { return 1 } }").Contains("cannot be 'override'")
            && Output("class B { override int x }").Contains("A field cannot be 'override'")
            && Output("class B { override construct() { } }").Contains("A constructor is not inherited")
            && Output("class B { override destruct() { } }").Contains("both run"));
        Check("override ist ein reserviertes Wort", Output("var override = 1").StartsWith("ERROR"));
        // `fire handover x` (docs/language/15-threads-and-actors.md): a keyword, repeatable and mixed with `taking`, not for `fire global`, and in a thread
        // only for the thread's own variables
        string handover = Output("""
            #import "time"
            class Item { int v }
            var a = new Item()
            a.v = 1
            var b = 2
            var c = new Item()
            c.v = 3
            var done = 0
            fire handover a taking b handover c { sync global { done = a.v + b + c.v } }
            print((a == undefined) + " " + b + " " + (c == undefined))
            while (done == 0) { Sleep(5ms) }
            print(done)
            """);
        Check("fire handover: mehrfach, mit taking gemischt, die Variablen werden undefined", handover == "True 2 True\n6", handover);
        string handoverGlobal = Output("var a = 1\nfire global handover a { }");
        Check("fire handover: nicht bei fire global", handoverGlobal.StartsWith("ERROR E3000 'handover' does not exist for 'fire global'"), handoverGlobal);
        string handoverTwice = Output("var a = 1\nfire taking a handover a { }");
        Check("fire handover: derselbe Name zweimal ist ein Fehler", handoverTwice.Contains("'a' was already captured in this 'fire'"), handoverTwice);
        string handoverMain = Output("var g = 1\nfire { var own = 2\n fire handover own { }\n fire handover g { } }");
        Check("fire handover: im Thread nur eigene Variablen, kein Global des Hauptprogramms", handoverMain.Contains("'handover' needs a variable of this thread: 'g' is a global variable of the main program"), handoverMain);
        string handoverWord = Output("var handover = 1");
        Check("fire handover: 'handover' ist ein Schluesselwort", handoverWord.StartsWith("ERROR"), handoverWord);

        // ---- `#globaljobs safepoints|sleep|sync` (docs/THREADING_DESIGN.md section 7): when the main program runs its `fire global` jobs ----
        const string jobsBody = "#import \"time\"\nfire global { print(\"job1\") }\nfor (var i = 0; i < 2; i++) { print(\"loop \" + i) }\nfire global { print(\"job2\") }\nH.f()\nSleep(1ms)\nprint(\"n \" + (sync globals))\nclass H { static undefined f() { print(\"call\") } }\n";
        const string atSafepoints = "loop 0\njob1\nloop 1\ncall\njob2\nn 0", atSleep = "loop 0\nloop 1\ncall\njob1\njob2\nn 0", atSync = "loop 0\nloop 1\ncall\njob1\njob2\nn 2";
        string jobsDefault = Output(jobsBody);
        Check("#globaljobs: ohne Angabe safepoints - Ruecksprung einer Schleife und Sleep, kein Aufruf", jobsDefault == atSafepoints, jobsDefault);
        string jobsSleep = Output("#globaljobs sleep\n" + jobsBody);
        Check("#globaljobs sleep: nur Sleep (und sync globals)", jobsSleep == atSleep, jobsSleep);
        string jobsSync = Output("#globaljobs sync\n" + jobsBody);
        Check("#globaljobs sync: nur sync globals", jobsSync == atSync, jobsSync);
        string jobsNoSync = Output("#nosync\n" + jobsBody);
        Check("#nosync ist #globaljobs sync", jobsNoSync == atSync, jobsNoSync);
        string jobsLast = Output("#globaljobs sync\n#globaljobs safepoints\n" + jobsBody);
        Check("#globaljobs: die letzte Angabe gilt", jobsLast == atSafepoints, jobsLast);
        string jobsBad = Output("#globaljobs often\nprint(1)");
        Check("#globaljobs: ein unbekannter Modus ist E3160", jobsBad.StartsWith("ERROR E3160 '#globaljobs' expects safepoints, sleep or sync"), jobsBad);
        string jobsMissing = Output("#globaljobs\nprint(1)");
        Check("#globaljobs: ohne Modus ist E3160", jobsMissing.StartsWith("ERROR E3160"), jobsMissing);
        Linker.ProcessGlobalJobs = GlobalJobsMode.Sleep;
        string jobsCli;
        try { jobsCli = Output("#globaljobs sync\n" + jobsBody); }
        finally { Linker.ProcessGlobalJobs = null; }
        Check("#globaljobs: --global-jobs geht vor die Direktive", jobsCli == atSleep, jobsCli);
        var cli = CommandLineParser.Parse(new[] { "run", "a.fire", "--global-jobs", "SLEEP" });
        var cliInline = CommandLineParser.Parse(new[] { "build", "a.fire", "--global-jobs=sync" });
        var cliBad = CommandLineParser.Parse(new[] { "run", "a.fire", "--global-jobs", "often" });
        Check("--global-jobs: Kommandozeile (auch mit =, Gross/Klein egal), ungueltig ist ein Fehler",
            cli.Error == null && cli.GlobalJobs == GlobalJobsMode.Sleep && cliInline.GlobalJobs == GlobalJobsMode.Sync && CommandLineParser.Parse(new[] { "run", "a.fire" }).GlobalJobs == null
            && cliBad.Error == "--global-jobs must be followed by safepoints, sleep or sync.", cliBad.Error);
        var config = fire.Native.NativeConfig.Parse("""{ "globalJobs": "sleep" }""");
        string configBad;
        try { fire.Native.NativeConfig.Parse("""{ "globalJobs": "often" }"""); configBad = "(no error)"; }
        catch (fire.Native.NativeConfigException ex) { configBad = ex.Message; }
        Check("fire.native.json: \"globalJobs\" wird gelesen, ein unbekannter Wert gemeldet, geschrieben wie gelesen",
            config.GlobalJobsValue == GlobalJobsMode.Sleep && configBad.Contains("\"globalJobs\": 'often' is not one of: safepoints, sleep, sync") && config.ToJson().Contains("\"globalJobs\": \"sleep\""), configBad);
        string threadsBody = "var n = 0\nfire global { n = n + 1 }\nprint(n)\n";
        string cConfig = fire.Compiler.NativeBuilder.GenerateLinked(new[] { threadsBody }, TargetProfile.Host, globalJobs: GlobalJobsMode.Sleep).C;
        string cDirective = fire.Compiler.NativeBuilder.GenerateLinked(new[] { "#globaljobs sync\n" + threadsBody }, TargetProfile.Host, globalJobs: GlobalJobsMode.Sleep).C;
        string cDefault = fire.Compiler.NativeBuilder.GenerateLinked(new[] { threadsBody }, TargetProfile.Host).C;
        Check("fire.native.json: \"globalJobs\" gilt fuer ein Programm ohne Direktive; die Direktive geht vor; ohne alles nichts (safepoints)",
            cConfig.Contains("g_globalJobs = 2;") && cDirective.Contains("g_globalJobs = 0;") && !cDirective.Contains("g_globalJobs = 2;") && !cDefault.Contains("g_globalJobs ="));

        // properties and base interfaces in an interface (SPEC 8.5)
        Check("Interface: Properties in jeder Schreibweise (get/set in jeder Reihenfolge, ein Accessor pro Zeile, Einheit, Array, generisch), Basis-Interfaces mit Namespace und Typargumenten", Output("""
            namespace Shapes {
                interface INamed { string Name { get; } }
                interface ISized<T> { T Size { set; get; } }
                interface IShape : INamed, Shapes.ISized<int> {
                    float Width : mm {
                        get;
                        set;
                    }
                    int[] Corners { get; }
                    float Area()
                }
                class Box : Shapes.IShape {
                    string Name { get { return "box" } }
                    int Size { get; set; }
                    float Width : mm { get; set; }
                    int[] Corners { get { return [1, 2, 3, 4] } }
                    float Area() { return 2.0 }
                }
            }
            Shapes.IShape s = new Shapes.Box()
            s.Width = 3cm
            s.Width += 1mm
            s.Size = 2
            s.Size *= 4
            print(s.Name + " " + s.Width + " " + s.Size + " " + s.Corners.length + " " + s.Area())
            """) is var props && props == "box 31mm 8 4 2", props);
        Check("Interface: doppeltes get ist E3020, ein Name ohne Accessoren E3023, ein unbekanntes Wort E3022", Output("interface I { int X { get; get; } }").StartsWith("ERROR E3020")
            && Output("interface I { int X { } }").StartsWith("ERROR E3023") && Output("interface I { int X { init; } }") is var unknownAccessor && unknownAccessor.StartsWith("ERROR E3022"), Output("interface I { int X { } }"));

        Console.WriteLine(_failures == 0 ? "Alle Syntax-Pruefungen bestanden." : $"FEHLER: {_failures} Syntax-Pruefung(en) fehlgeschlagen.");
    }
}
