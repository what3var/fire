namespace fire.Values
{
    /// <summary>
    /// Ein Array fester Größe (Elemente default-initialisiert mit 'undefined').
    /// Bewusst simpel gehalten - Allokation ist eindimensional; mehrdimensionale
    /// ("jagged") Arrays entstehen aus mehreren verschachtelten ScriptArray-
    /// Instanzen (siehe Compiler.CompileArrayAlloc), nicht aus einer eigenen
    /// mehrdimensionalen Laufzeit-Repräsentation.
    ///
    /// Zugriff bewusst OHNE C#-Exceptions für den ungültigen-Index-Fall (siehe
    /// TryGet/TrySet) - Rückgabewert/out-Parameter statt throw/catch, damit
    /// sich dieselbe Logik 1:1 nach C++ (Ziel: FreeRTOS-Portierung, siehe
    /// docs/PORTING.md) übertragen lässt, wo Exceptions in eingebetteten
    /// Umgebungen oft ganz abgeschaltet sind. Die VM (siehe VM.ArrayGet/
    /// ArraySet) wandelt ein `false` hier in eine ordentliche, fangbare
    /// ScriptLang-`IndexOutOfBoundsException` um (VM.ThrowIndexOutOfBounds) -
    /// diese Umwandlung bleibt bewusst VM-seitig, nicht hier, da "eine
    /// Skript-Exception werfen" ein Konzept der VM/des Interpreters ist,
    /// keins dieser reinen Datenstruktur.
    /// </summary>
    public sealed class ScriptArray
    {
        public Value[] Items { get; }
        public int Length => Items.Length;

        public ScriptArray(int length)
        {
            Items = new Value[length];
            for (int i = 0; i < length; i++)
                Items[i] = Value.MakeUndefined();
        }

        /// <summary>Liefert `false` bei ungültigem Index (`value` dann
        /// `default`), statt zu werfen - der Aufrufer entscheidet selbst, was
        /// das bedeutet (siehe VM.ArrayGet: eine fangbare Skript-Exception).</summary>
        public bool TryGet(long index, out Value value)
        {
            if (index < 0 || index >= Items.Length)
            {
                value = default;
                return false;
            }
            value = Items[index];
            return true;
        }

        /// <summary>Liefert `false` bei ungültigem Index, OHNE zu schreiben.</summary>
        public bool TrySet(long index, Value value)
        {
            if (index < 0 || index >= Items.Length) return false;
            Items[index] = value;
            return true;
        }

        /// <summary>UNGEPRÜFTER Zugriff (siehe Bytecode.VmExecutionMode.
        /// Performance) - ein ungültiger Index führt zu einer rohen .NET-
        /// IndexOutOfRangeException (in C++: undefiniertes Verhalten) statt
        /// eines kontrollierten `false`. Nur von VM-Opcode-Handlern im
        /// Performance-Modus aufgerufen, nie direkt aus Skript-Code heraus
        /// wählbar.</summary>
        public Value GetUnchecked(long index) => Items[(int)index];

        public void SetUnchecked(long index, Value value) => Items[(int)index] = value;
    }
}
