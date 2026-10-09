using System.Collections.Generic;

namespace fire.Bytecode
{
    /// <summary>
    /// Global (process-wide, shared across ALL VM instances/threads)
    /// registration for the two handlers from docs/THREADING_DESIGN.md
    /// section 6.2/6.3: `catch threads(ExceptionType e)`/`catch threads()`
    /// (unhandled user exceptions from fire threads) and
    /// `catch terminate(v)` (global emergency stop). Both are - unlike a
    /// normal `try`/`catch` - registered only ONCE, globally (not per
    /// scope/call), and by design run exclusively on the
    /// main thread (see VM.HandleDeliveredThreadException/
    /// RunTerminateHandlerIfAny).
    ///
    /// Deliberately pure storage without matching logic - the MATCHING logic
    /// (checking the exception type name against the base-class chain) lives in
    /// VM.InstanceMatchesClassName (needs access to the calling
    /// VM instance's own `_classes`), not here.
    /// </summary>
    public static class GlobalHandlers
    {
        /// <summary>Like HandlerTemplate.Catches for a normal try/catch:
        /// (TypeName, Proto) in registration order - `TypeName == null`
        /// stands for `catch threads()` (matches everything). The proto has either
        /// 0 parameters (`catch threads()`, body without a bound variable) or
        /// 1 parameter (`catch threads(ExceptionType e)`, `e` bound).</summary>
        public static readonly List<(string? TypeName, FunctionProto Proto)> ThreadsCatches = new();

        /// <summary>At most one - `catch terminate(v)` exists only once
        /// in the whole program (no stack as with try/catch). 0 or 1
        /// parameter, analogous to ThreadsCatches.</summary>
        public static FunctionProto? TerminateHandler;

        private static readonly object Gate = new();

        public static void RegisterThreadsCatch(string? typeName, FunctionProto proto)
        {
            lock (Gate) ThreadsCatches.Add((typeName, proto));
        }

        public static void RegisterTerminateCatch(FunctionProto proto)
        {
            lock (Gate) TerminateHandler = proto;
        }

        /// <summary>Intended for tests only (see VM.ResetTerminateForTests) -
        /// resets the global registration between mutually independent
        /// program runs in the same process.</summary>
        public static void ResetForTests()
        {
            lock (Gate)
            {
                ThreadsCatches.Clear();
                TerminateHandler = null;
            }
        }
    }
}
