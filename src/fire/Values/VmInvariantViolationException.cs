namespace ScriptLang.Values
{
    /// <summary>
    /// Signalisiert eine verletzte interne INVARIANTE der VM/des Compilers -
    /// z.B. `Value.AsInt()` auf einem Value, das (laut korrekt kompiliertem
    /// Bytecode) gar nicht `Int` sein dürfte (siehe Value.RequireKind).
    ///
    /// BEWUSST vom generischen `InvalidOperationException` unterschieden
    /// (auch wenn es aktuell nur ein dünner Wrapper darum ist): dieser Typ
    /// markiert einen COMPILER-/VM-BUG, keinen durch ein gültiges Skript
    /// erreichbaren Laufzeitzustand (anders als z.B. ein ungültiger Array-
    /// Index, siehe ScriptArray.TryGet/TrySet - DAS ist normale, von echten
    /// Skripten routinemäßig ausgelöste Kontrollflusslogik und deshalb
    /// bewusst OHNE Exception implementiert). Nirgendwo im gesamten Kern
    /// wird dieser Typ gezielt gefangen und "behandelt" - er propagiert
    /// immer bis zum Abbruch durch (siehe docs/PORTING.md, Abschnitt
    /// "VM-interner Kontrollfluss").
    ///
    /// Für die spätere C++-Portierung (siehe docs/PORTING.md) ist das die
    /// entscheidende Unterscheidung: DIESER Typ übersetzt sich als
    /// `assert(...)`/Panic-Handler (funktioniert auch ohne C++-Exceptions,
    /// üblich auf Embedded-Targets), NICHT als `TryX()`-Rückgabewert wie
    /// beim Array-Beispiel - eine Assertion ist kein Zustand, den ein
    /// Aufrufer sinnvoll "behandeln" könnte, sie zeigt einen Bug an.
    /// </summary>
    public sealed class VmInvariantViolationException : System.Exception
    {
        public VmInvariantViolationException(string message) : base(message) { }
    }
}
