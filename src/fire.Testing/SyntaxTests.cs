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

        Console.WriteLine(_failures == 0 ? "Alle Syntax-Pruefungen bestanden." : $"FEHLER: {_failures} Syntax-Pruefung(en) fehlgeschlagen.");
    }
}
