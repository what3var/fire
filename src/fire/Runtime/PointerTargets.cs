using ScriptLang.Values;

namespace ScriptLang.Runtime
{
    /// <summary>Zeigt auf einen konkreten Slot in einem konkreten Scope (lokale
    /// oder globale Variable). "Weiterrücken" bewegt sich innerhalb desselben
    /// Scopes zum nächsten Slot - Scopes speichern ihre Variablen ohnehin schon
    /// als zusammenhängende Liste, das passt also natürlich zusammen. Kein
    /// Bounds-Check gegen die tatsächliche Slot-Anzahl hier - ein ungültiger
    /// Zugriff schlägt beim eigentlichen Lesen/Schreiben fehl (Scope.GetSlot/
    /// SetSlot werfen dann ganz regulär eine IndexOutOfRangeException).</summary>
    public sealed class ScopeSlotPointerTarget : PointerTarget
    {
        public Scope Scope { get; }
        public int Slot { get; }

        public ScopeSlotPointerTarget(Scope scope, int slot)
        {
            Scope = scope;
            Slot = slot;
        }

        public override Value Read() => Scope.GetSlot(Slot);
        public override void Write(Value v) => Scope.SetSlot(Slot, v);

        public override PointerTarget Advance(long elementOffset) =>
            new ScopeSlotPointerTarget(Scope, Slot + (int)elementOffset);

        public override bool Equals(object? obj) =>
            obj is ScopeSlotPointerTarget o && ReferenceEquals(o.Scope, Scope) && o.Slot == Slot;

        public override int GetHashCode() => System.HashCode.Combine(Scope, Slot);
    }

    /// <summary>Zeigt auf ein benanntes Feld einer konkreten Objektinstanz.
    /// Felder liegen nicht garantiert zusammenhängend (Runtime.FieldStore -
    /// bekannte Felder in einem Array, aber pro Klasse in eigener Reihenfolge,
    /// unbekannte im Dictionary-Fallback), daher ergibt "Weiterrücken" hier
    /// nur bei Offset 0 Sinn (liefert sich selbst zurück) - alles andere gibt
    /// null zurück (ungültige Pointer-Arithmetik über eine Feldgrenze
    /// hinweg).</summary>
    public sealed class FieldPointerTarget : PointerTarget
    {
        public ObjectInstance Instance { get; }
        public string FieldName { get; }

        public FieldPointerTarget(ObjectInstance instance, string fieldName)
        {
            Instance = instance;
            FieldName = fieldName;
        }

        public override Value Read() => Instance.Fields[FieldName];
        public override void Write(Value v) => Instance.Fields[FieldName] = v;

        public override PointerTarget? Advance(long elementOffset) =>
            elementOffset == 0 ? this : null;

        public override bool Equals(object? obj) =>
            obj is FieldPointerTarget o && ReferenceEquals(o.Instance, Instance) && o.FieldName == FieldName;

        public override int GetHashCode() => System.HashCode.Combine(Instance, FieldName);
    }
}
