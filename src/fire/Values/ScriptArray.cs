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
    /// fire-`IndexOutOfBoundsException` um (VM.ThrowIndexOutOfBounds) -
    /// diese Umwandlung bleibt bewusst VM-seitig, nicht hier, da "eine
    /// Skript-Exception werfen" ein Konzept der VM/des Interpreters ist,
    /// keins dieser reinen Datenstruktur.
    /// </summary>
    public sealed class ScriptArray : fire.Runtime.IOwnedLeaf
    {
        public Value[] Items { get; }

        /// <summary>Der Owner (SPEC 2): ein Scope oder ein Objekt; null fuer ein Array, das ausserhalb der VM entstand und nie zerstoert wird.</summary>
        public fire.Runtime.IOwner? LeafOwner { get; set; }

        /// <summary>Zerstoert (der Owner wurde verlassen/zerstoert oder `delete`): der Zugriff ist ein Fehler (Debug/Release).</summary>
        public bool IsDestroyed { get; private set; }

        /// <summary>Innere Arrays einer mehrdimensionalen Allokation (`new int[3][4]`): sie gehoeren zum aeusseren Array und werden mit ihm zerstoert.</summary>
        public System.Collections.Generic.List<fire.Runtime.IOwnedLeaf>? Parts { get; set; }

        public void MarkDestroyed()
        {
            if (IsDestroyed) return;
            IsDestroyed = true;
            Special = true;
            LeafOwner = null;
            if (Parts != null) foreach (var part in Parts) part.MarkDestroyed();
            Parts = null;
        }

        /// <summary>Wurde dieses Array von einem Fire-Thread über die Globals erreicht (siehe GlobalsBroker)? Dann gehört es zum geteilten
        /// Bereich: Elementzugriffe laufen unter dem Baum-Lock, und ein Fire-Thread ändert Elemente nur innerhalb einer Sektion.</summary>
        public bool IsShared { get => _shared; set { _shared = value; Special = value || IsDestroyed; } }
        private bool _shared;

        /// <summary>Geteilt oder zerstoert: die Schnellpfade der VM (Elementzugriff) nehmen dann den langsamen Weg, der beides beachtet.</summary>
        public bool Special { get; private set; }
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
