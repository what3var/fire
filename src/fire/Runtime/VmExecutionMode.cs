namespace fire.Runtime
{
    /// <summary>
    /// Controls how much runtime overhead the VM affords itself - two levels
    /// above the default behaviour (<see cref="Debug"/>):
    ///
    /// - <see cref="Debug"/> (default): all safety checks (array/buffer
    ///   bounds, access modifiers, unit requirements of fields) active.
    ///
    /// - <see cref="Release"/>: like Debug - this mode used to lower the
    ///   overhead of the shutdown check per instruction; that now runs in
    ///   EVERY mode only at safe points (loop back-edge, call,
    ///   `leave`/`terminate`, see VM.PollSignals) and costs one
    ///   comparison there - the mode no longer differs from Debug.
    ///
    /// - <see cref="Performance"/>: additionally the
    ///   array/buffer bounds checks are skipped (see ScriptArray/
    ///   Values.ByteBuffer, each *Unchecked variants) - an invalid
    ///   index then leads to a RAW, UNCAUGHT .NET
    ///   IndexOutOfRangeException (the operating system/.NET always checks array
    ///   accesses itself anyway, that cannot be
    ///   bypassed completely in managed C# - "switching off bounds checks" means
    ///   concretely here: the more expensive conversion into a catchable,
    ///   proper script exception is dropped, not the memory safety
    ///   itself) instead of a catchable `IndexOutOfBoundsException`. Intended only for
    ///   code that has already been thoroughly tested and is trustworthy.
    /// </summary>
    public enum VmExecutionMode
    {
        Debug = 0,
        Release = 1,
        Performance = 2,
    }
}
