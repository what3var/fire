using System;
using fire.Values;

namespace fire.Standard
{
    /// <summary>Cancellation (docs/ROADMAP_TASKS.md section 6): the natives behind the prelude's CancellationSource / CancellationToken. A node is an int
    /// handle (0: none). The VM implements them (fire.Runtime.Cancellation), a native build in fire_rt.h.</summary>
    /// <summary>`handover x` in a lambda: x - and what it reaches - moves to the lambda like a handover to a thread (every reference from before is dead);
    /// the caller's variable is undefined afterwards. The VM: ObjectHandover.MoveAll; native: fire_rt.h fire_lambdaHandover.</summary>
    public static class LambdaHandover
    {
        public const string NativeName = "__lambda_handover";
        public static Func<Value[], Value>? Implementation;
        public static Value Call(Value[] args) =>
            (Implementation ?? throw new InvalidOperationException("'handover' needs a running program."))(args);
    }

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

    /// <summary>Completion (docs/ROADMAP_TASKS.md section 6): the natives behind the prelude's Completion - a value that another task delivers. A node is an
    /// int handle (0: none), counted like a cancellation node. The VM implements them (fire.Runtime.Completions), a native build in fire_rt.h.</summary>
    public static class CompletionMethods
    {
        public static readonly string[] Names =
        {
            "__completion_new", "__completion_ref", "__completion_release", "__completion_done", "__completion_set", "__completion_wait",
        };

        /// <summary>Set by the VM: (name, arguments) on the VM of the calling thread.</summary>
        public static Func<string, Value[], Value>? Implementation;

        public static Value Call(string name, Value[] args) =>
            (Implementation ?? throw new InvalidOperationException("A Completion needs a running program."))(name, args);
    }

    /// <summary>`__fd_wait(fd, events, ms)` (docs/ROADMAP_HTTP.md step 0): waits until the socket `fd` (the net package's __NetNativeHandle) is readable (bit 1)
    /// or writable (bit 2), `ms` ran out (0) or the task is cancelled; -1: not supported here (the caller waits as before). The VM: fire.Runtime.FdWait;
    /// native: fire_rt.h fire_fd_wait (a poller thread).</summary>
    public static class FdWaitMethods
    {
        public const string NativeName = "__fd_wait";
        public static Func<Value[], Value>? Implementation;
        public static Value Call(Value[] args) =>
            (Implementation ?? throw new InvalidOperationException("__fd_wait needs a running program."))(args);
    }
}
