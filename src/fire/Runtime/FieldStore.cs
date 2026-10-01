using System.Collections;
using System.Collections.Generic;
using fire.Bytecode;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>
    /// Feldspeicher einer ObjectInstance (siehe ObjectInstance.Fields) -
    /// bewusst dieselbe API-Oberfläche wie vorher ein rohes
    /// `Dictionary&lt;string, Value&gt;` (Indexer, TryGetValue, ContainsKey,
    /// aufzählbar als (Name, Value)-Paare), damit SyncEngine/ObjectCopier/
    /// UncaughtScriptException/PointerTargets/Testcode UNVERÄNDERT
    /// weiterlaufen, ohne selbst etwas über die interne Aufteilung wissen zu
    /// müssen.
    ///
    /// Intern zweigeteilt:
    /// - Ein Array für die zur Kompilierzeit BEKANNTEN, deklarierten Felder
    ///   (fester Slot-Index über RuntimeClass.FieldIndex, EINMALIG pro
    ///   Klasse berechnet, nicht pro Instanz - siehe dort) - O(1)-Zugriff
    ///   ohne Hashing/Stringvergleich, kompakter als ein Dictionary-Eintrag
    ///   pro Feld. Das ist der Pfad, den JEDER vom Compiler erzeugte
    ///   Feldzugriff nimmt (GetField/SetField/SetFieldOnThis/AddressOfField
    ///   emittieren IMMER nur Namen tatsächlich deklarierter Felder).
    /// - Ein (lazy angelegtes) Dictionary-Fallback für alles andere - wird
    ///   real nur erreicht, wenn gar keine RuntimeClass bekannt ist (siehe
    ///   ObjectInstance-Konstruktor) oder ein Feldname verwendet wird, der
    ///   nicht deklariert wurde (z.B. Testcode, der eine ObjectInstance
    ///   direkt konstruiert und Felder "on the fly" per Name setzt, ohne
    ///   über den Compiler zu gehen - reale, kompilierte Skripte tun das
    ///   nie).
    /// </summary>
    public sealed class FieldStore : IEnumerable<KeyValuePair<string, Value>>
    {
        private readonly RuntimeClass? _rtClass;
        private readonly Value[] _known;
        private Dictionary<string, Value>? _extra;

        public FieldStore(RuntimeClass? rtClass)
        {
            _rtClass = rtClass;
            _known = new Value[rtClass?.FieldIndex.Count ?? 0];

            // `default(Value)` ist `false` (ValueKind.Bool = 0) - ein deklariertes Feld,
            // das noch nichts zugewiesen bekam, ist aber `undefined`. Sichtbar wird das
            // bei einem Objekt, dessen Konstruktor abbricht, bevor die Feld-
            // Initialisierer liefen (z.B. Exception beim Auswerten der base(...)-
            // Argumente): sein destruct() darf dann `undefined` sehen, nicht ein
            // erfundenes `false`.
            for (int i = 0; i < _known.Length; i++)
                _known[i] = Value.MakeUndefined();
        }

        public Value this[string name]
        {
            get
            {
                if (_rtClass != null && _rtClass.FieldIndex.TryGetValue(name, out int idx))
                    return _known[idx];
                if (_extra != null && _extra.TryGetValue(name, out var v))
                    return v;
                throw new KeyNotFoundException($"Kein Feld namens '{name}'.");
            }
            set
            {
                if (_rtClass != null && _rtClass.FieldIndex.TryGetValue(name, out int idx))
                {
                    _known[idx] = value;
                    return;
                }
                (_extra ??= new Dictionary<string, Value>())[name] = value;
            }
        }

        /// <summary>Direkter Zugriff auf ein DEKLARIERTES Feld über seinen Index (siehe RuntimeClass.FieldIndex) -
        /// für die Inline-Caches der VM, die den Index schon kennen.</summary>
        public Value GetAt(int index) => _known[index];
        public void SetAt(int index, Value value) => _known[index] = value;

        public bool TryGetValue(string name, out Value value)
        {
            if (_rtClass != null && _rtClass.FieldIndex.TryGetValue(name, out int idx))
            {
                value = _known[idx];
                return true;
            }
            if (_extra != null && _extra.TryGetValue(name, out value))
                return true;
            value = default;
            return false;
        }

        public bool ContainsKey(string name)
        {
            if (_rtClass != null && _rtClass.FieldIndex.ContainsKey(name)) return true;
            return _extra != null && _extra.ContainsKey(name);
        }

        public IEnumerator<KeyValuePair<string, Value>> GetEnumerator()
        {
            if (_rtClass != null)
            {
                var names = _rtClass.FlattenedFieldNames;
                for (int i = 0; i < names.Count; i++)
                    yield return new KeyValuePair<string, Value>(names[i], _known[i]);
            }
            if (_extra != null)
                foreach (var kv in _extra)
                    yield return kv;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
