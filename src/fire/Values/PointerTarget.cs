namespace fire.Values
{
    /// <summary>
    /// Abstraktes Ziel eines Pointers. Konkrete Implementierungen (Scope-Slot,
    /// Objekt-Feld) leben in der Runtime-Schicht, da sie auf deren Typen (Scope,
    /// ObjectInstance) angewiesen sind - Values selbst bleibt davon unabhängig
    /// (dieselbe Schichtung wie bei Value.MakeClassRef/MakeLambda, die
    /// Runtime-Typen auch nur lose über 'object' referenzieren).
    ///
    /// Bewusste Design-Entscheidung (SPEC "Pointer/unsafe"): statt eines rohen
    /// Byte-Puffers zeigt ein Pointer hier auf einen EXISTIERENDEN, verwalteten
    /// Speicherort (Scope-Slot oder Objekt-Feld) - echtes Aliasing (`*p = x`
    /// verändert wirklich die Variable, auf die `p` zeigt), ohne die Scope-/
    /// Ownership-Infrastruktur zu duplizieren. "Pointer-Arithmetik" bedeutet
    /// dementsprechend "N Slots weiter" statt "N Bytes weiter". Für echte native
    /// Adressen (z.B. um sie an eine 'extern'-Funktion zu übergeben) wird das
    /// später beim tatsächlichen 'extern'-Linking in einen echten,
    /// gepinnten/unmanaged Puffer marshalt - das ist bewusst nicht Teil dieser
    /// Ausbaustufe.
    /// </summary>
    public abstract class PointerTarget
    {
        public abstract Value Read();
        public abstract void Write(Value v);

        /// <summary>Liefert ein neues PointerTarget, das um `elementOffset` logische Elemente verschoben ist. Die Verschiebung ist immer erlaubt (auch ueber das Ende hinaus, wie in C);
        /// erst das Lesen/Schreiben ausserhalb des Bereichs wirft eine <c>PointerRangeException</c>. Ein Zeiger auf eine Variable oder ein Feld ist ein "Array" mit einem Element.</summary>
        public abstract PointerTarget Advance(long elementOffset);

        /// <summary>`this - other` in Elementen, wenn beide in dasselbe Array/dieselbe Variable zeigen, sonst null.</summary>
        public abstract long? DistanceTo(PointerTarget other);
    }
}
