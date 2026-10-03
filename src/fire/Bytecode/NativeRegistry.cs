using System;
using System.Collections.Generic;
using fire.Values;

namespace fire.Bytecode
{
    /// <summary>Eine native (in C# implementierte) Funktion, aufrufbar per
    /// CALL_NATIVE-Opcode über ihren Index in der Registry.</summary>
    public delegate Value NativeFunction(Value[] args);

    /// <summary>Eine "tryable" native Funktion - registriert für APIs, deren
    /// Erfolg nicht garantiert ist (Timeouts, nicht verfügbare Hardware,
    /// o.ä. - typischerweise IO: serielle Schnittstellen, Netzwerk, Dateien).
    /// NUR über `try Name(...)` aufrufbar (siehe SPEC 8.1.3) - liefert bei
    /// Erfolg `true` und den Ergebniswert in `result`, bei Fehlschlag/
    /// Timeout `false` (dann wird `result` verworfen, das Skript sieht
    /// `undefined`). BEWUSST kein Exception-Mechanismus (wie C#s eigenes
    /// `TryParse`-Muster) - die eigentliche Timeout-/Fehler-Logik liegt
    /// komplett beim registrierenden Host/Framework, nicht in der Sprache
    /// selbst. Ein etwaiger Timeout-Parameter ist ein ganz normales Value-
    /// Argument mit Einheit (`Value.Unit` ist bereits öffentlich lesbar -
    /// die Host-Implementierung liest sie selbst aus, z.B. um "500ms" von
    /// "2s" zu unterscheiden).</summary>
    public delegate bool TryableNativeFunction(Value[] args, out Value result);

    /// <summary>
    /// Registry nativer Funktionen. Das ist bewusst die einzige Stelle, über die
    /// der Bytecode mit der "Außenwelt" spricht - der reservierte Erweiterungspunkt
    /// für spätere Betriebssystem-/Host-APIs (Dateizugriff, Konsole, Netzwerk, ...),
    /// ohne dass sich am Bytecode-Format dafür noch etwas ändern müsste. Aktuell
    /// nur mit "print" befüllt, zum Testen der VM.
    /// </summary>
    public sealed class NativeRegistry
    {
        private readonly List<NativeFunction> _functions = new();
        private readonly Dictionary<string, int> _indexByName = new();

        private readonly List<TryableNativeFunction> _tryableFunctions = new();
        private readonly Dictionary<string, int> _tryableIndexByName = new();

        public NativeRegistry()
        {

        }

        public NativeRegistry(NativeRegistry sourceToCopy)
        {
            _functions = sourceToCopy._functions;
            _indexByName = sourceToCopy._indexByName;
            _tryableFunctions = sourceToCopy._tryableFunctions;
            _tryableIndexByName = sourceToCopy._tryableIndexByName;
        }

        public int Register(string name, NativeFunction fn)
        {
            int idx = _functions.Count;
            _functions.Add(fn);
            _indexByName[name] = idx;
            return idx;
        }

        /// <summary>Registriert mehrere zusammengehörige native Funktionen auf
        /// einmal, alle unter demselben Namens-PRÄFIX (z.B. für eine
        /// abgeschlossene API-Gruppe wie eine Grafik-/Konsolen-Brücke) - der
        /// tatsächlich registrierte Name jedes Eintrags ist
        /// `prefix + suffix` (Beispiel: `RegisterGroup("__GRPH", new()
        /// { ["set"] = ..., ["get"] = ... })` registriert `__GRPHset` und
        /// `__GRPHget`). Liefert die Liste der so erzeugten Namen zurück (in
        /// der Iterationsreihenfolge von `functions`) - nützlich zum Prüfen/
        /// Loggen, welche Namen dabei entstanden sind, oder um sie z.B. in
        /// einem generierten Präprozessor-/Prelude-Quelltext direkt
        /// wiederzuverwenden.</summary>
        public IReadOnlyList<string> RegisterGroup(string prefix, IReadOnlyDictionary<string, NativeFunction> functions)
        {
            var names = new List<string>(functions.Count);
            foreach (var (suffix, fn) in functions)
            {
                string name = prefix + suffix;
                Register(name, fn);
                names.Add(name);
            }
            return names;
        }

        /// <summary>Registriert eine NUR über `try Name(...)` aufrufbare
        /// Funktion (siehe TryableNativeFunction-Doku) - ein normaler Aufruf
        /// `Name(...)` OHNE 'try' ist für diesen Namen ein Compile-Fehler
        /// (siehe Resolver), kein automatischer Fallback auf "wirft bei
        /// Fehlschlag".</summary>
        public int RegisterTryable(string name, TryableNativeFunction fn)
        {
            int idx = _tryableFunctions.Count;
            _tryableFunctions.Add(fn);
            _tryableIndexByName[name] = idx;
            return idx;
        }

        public int IndexOf(string name) =>
            _indexByName.TryGetValue(name, out var idx)
                ? idx
                : throw new InvalidOperationException($"Keine native Funktion namens '{name}' registriert.");

        public int TryableIndexOf(string name) =>
            _tryableIndexByName.TryGetValue(name, out var idx)
                ? idx
                : throw new InvalidOperationException($"Keine 'tryable' native Funktion namens '{name}' registriert.");

        public bool Has(string name) => _indexByName.ContainsKey(name);

        public bool IsTryable(string name) => _tryableIndexByName.ContainsKey(name);

        public IEnumerable<string> Names => _indexByName.Keys;
        public IEnumerable<string> TryableNames => _tryableIndexByName.Keys;

        public NativeFunction this[int index] => _functions[index];
        public TryableNativeFunction TryableAt(int index) => _tryableFunctions[index];

        public static NativeRegistry CreateDefault()
        {
            var registry = new NativeRegistry();
            registry.Register("print", args =>
            {
                Console.WriteLine(args.Length > 0 ? args[0].ToString() : "");
                return Value.MakeUndefined();
            });
            registry.RegisterBaseTypeNatives();
            return registry;
        }

        /// <summary>Registriert die nativen Funktionen, auf die der Prelude die Methoden der
        /// Basistyp-Erweiterungen (`class extends string/char`, SPEC 5.5.1, 8.12) abbildet - je EINE
        /// Funktion pro Basistyp, die Methode wählt ihr erstes Argument (eine ID). MUSS in jeder
        /// Registry stehen, mit der ein Programm samt Prelude kompiliert/ausgeführt wird, und zwar
        /// an derselben Stelle der Reihenfolge wie beim Kompilieren (native Funktionen werden über ihren
        /// Index angesprungen) - deshalb überall direkt hinter `print`.</summary>
        public void RegisterBaseTypeNatives()
        {
            Register(fire.Standard.StringMethods.NativeName, fire.Standard.StringMethods.Call);
            Register(fire.Standard.CharMethods.NativeName, fire.Standard.CharMethods.Call);
        }
    }
}
