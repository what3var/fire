namespace fire.Values
{
    /// <summary>
    /// Signals a violated internal INVARIANT of the VM/compiler -
    /// e.g. `Value.AsInt()` on a Value that (according to correctly compiled
    /// bytecode) must not be `Int` at all (see Value.RequireKind).
    ///
    /// DELIBERATELY distinguished from the generic `InvalidOperationException`
    /// (even though it is currently only a thin wrapper around it): this type
    /// marks a COMPILER/VM BUG, not a runtime state
    /// reachable by a valid script (unlike e.g. an invalid array
    /// index, see ScriptArray.TryGet/TrySet - THAT is normal control-flow logic
    /// routinely triggered by real scripts and therefore
    /// deliberately implemented WITHOUT exceptions). Nowhere in the whole core
    /// is this type deliberately caught and "handled" - it always
    /// propagates all the way to the abort (see docs/PORTING.md, section
    /// "VM-internal control flow").
    ///
    /// For the later C++ port (see docs/PORTING.md) this is the
    /// decisive distinction: THIS type translates to an
    /// `assert(...)`/panic handler (also works without C++ exceptions,
    /// common on embedded targets), NOT as a `TryX()` return value like
    /// in the array example - an assertion is not a state a
    /// caller could sensibly "handle", it indicates a bug.
    /// </summary>
    public sealed class VmInvariantViolationException : System.Exception
    {
        public VmInvariantViolationException(string message) : base(message) { }
    }
}
