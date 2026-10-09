namespace fire.Values
{
    /// <summary>
    /// Abstract target of a pointer. Concrete implementations (scope slot,
    /// object field) live in the runtime layer, since they depend on its types (Scope,
    /// ObjectInstance) - Values itself stays independent of them
    /// (the same layering as with Value.MakeClassRef/MakeLambda, which
    /// also reference runtime types only loosely via 'object').
    ///
    /// Deliberate design decision (SPEC "Pointer/unsafe"): instead of a raw
    /// byte buffer, a pointer here points to an EXISTING, managed
    /// storage location (scope slot or object field) - real aliasing (`*p = x`
    /// really changes the variable `p` points to), without duplicating the scope/
    /// ownership infrastructure. "Pointer arithmetic" accordingly means
    /// "N slots further" instead of "N bytes further". For real native
    /// addresses (e.g. to pass them to an 'extern' function) this is
    /// later, at the actual 'extern' linking, marshalled into a real,
    /// pinned/unmanaged buffer - that is deliberately not part of this
    /// stage.
    /// </summary>
    public abstract class PointerTarget
    {
        public abstract Value Read();
        public abstract void Write(Value v);

        /// <summary>Returns a new PointerTarget shifted by `elementOffset` logical elements. The shift is always allowed (even beyond the end, as in C);
        /// only reading/writing out of range throws a <c>PointerRangeException</c>. A pointer to a variable or a field is an "array" with one element.</summary>
        public abstract PointerTarget Advance(long elementOffset);

        /// <summary>`this - other` in elements, if both point into the same array/the same variable, otherwise null.</summary>
        public abstract long? DistanceTo(PointerTarget other);
    }
}
