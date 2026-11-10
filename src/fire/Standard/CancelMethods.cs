using System;
using fire.Values;

namespace fire.Standard
{
    /// <summary>Cancellation (docs/ROADMAP_TASKS.md section 6): the natives behind the prelude's CancellationSource / CancellationToken. A node is an int
    /// handle (0: none). The VM implements them (fire.Runtime.Cancellation), a native build in fire_rt.h.</summary>
    public static class CancelMethods
    {
        public static readonly string[] Names =
        {
            "__cancel_new", "__cancel_release", "__cancel_state", "__cancel_current", "__cancel_ref", "__cancel_cancel", "__cancel_after", "__cancel_time",
        };

        /// <summary>Set by the VM: (name, arguments) on the VM of the calling thread.</summary>
        public static Func<string, Value[], Value>? Implementation;

        public static Value Call(string name, Value[] args) =>
            (Implementation ?? throw new InvalidOperationException("Cancellation needs a running program."))(name, args);
    }
}
