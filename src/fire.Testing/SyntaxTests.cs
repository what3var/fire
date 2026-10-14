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
        Check("unit: eingebaute Einheit (auch mit Praefix) ist ein Fehler", UnitError("unit Meter: m") is var e1 && e1.Contains("already a unit of fire") && UnitError("unit Gauss: Gs { coerce m => 1 }").Contains("the prefix 'G' in front of 's'"), e1);
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

        Console.WriteLine(_failures == 0 ? "Alle Syntax-Pruefungen bestanden." : $"FEHLER: {_failures} Syntax-Pruefung(en) fehlgeschlagen.");
    }
}
