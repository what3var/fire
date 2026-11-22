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
    /// - <see cref="Performance"/>: the checks the language leaves out there (destroyed objects, ownership, access rights - SPEC: undefined
    ///   behaviour) are skipped. An array or buffer index outside the bounds is undefined behaviour too, but the VM must not end the process
    ///   of its host (spark) with a raw .NET IndexOutOfRangeException: it throws the catchable `IndexOutOfBoundsException` of Release, as the
    ///   VM in C does (the *Unchecked accessors of ScriptArray/ByteBuffer behind one comparison - .NET checks the access anyway). Intended only for
    ///   code that has already been thoroughly tested and is trustworthy.
    /// </summary>
    public enum VmExecutionMode
    {
        Debug = 0,
        Release = 1,
        Performance = 2,
    }
}
