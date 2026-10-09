using System;
using System.Diagnostics;

namespace fire.Runtime
{
    public sealed partial class VM
    {
        /// <summary>Text of a value for output and text concatenation: an object with a parameterless `ToString()` method yields its result, everything else
        /// its usual representation. null if `ToString()` raised an exception that was already redirected to a handler (no result).</summary>
        internal string? StringifyForText(fire.Values.Value v)
        {
            if (v.Kind != fire.Values.ValueKind.Class) return v.ToString();
            var obj = (ObjectInstance)v.AsObjectRef();
            if (ResolveClass(obj.ClassName).FindMethod("ToString", 0) == null) return v.ToString();
            var result = CallMethodNested(obj, "ToString", Array.Empty<fire.Values.Value>());
            if (result == null) return null;
            return result.Value.Kind == fire.Values.ValueKind.String ? result.Value.AsString() : result.Value.ToString();
        }

        /// <summary>For the host `print`: the first argument, if it is an object with `ToString()`, as text (see <see cref="StringifyForText"/>). null if
        /// the conversion was redirected - the native call then returns no result.</summary>
        public static fire.Values.Value[]? StringifyForPrint(fire.Values.Value[] args)
        {
            if (args.Length == 0 || args[0].Kind != fire.Values.ValueKind.Class || CurrentThreadVm is not { } vm) return args;
            var text = vm.StringifyForText(args[0]);
            if (text == null) { vm._nativeRedirected = true; return null; }
            return new[] { fire.Values.Value.MakeString(text) };
        }

        // Default waiting time of the wait functions (`#timeout`). Static, so that the program's fire threads see it too; a new main program
        // starts again with 30 seconds (see the VM constructor).
        private static long s_defaultTimeoutTicks = DefaultTimeoutTicks;
        private const long DefaultTimeoutTicks = 30L * 10_000_000;

        /// <summary>The waiting time that a wait function takes without a time specification of its own: the program's `#timeout`, otherwise 30 seconds.</summary>
        public static TimeSpan DefaultTimeout => TimeSpan.FromTicks(System.Threading.Volatile.Read(ref s_defaultTimeoutTicks));

        internal static void SetDefaultTimeout(TimeSpan timeout) => System.Threading.Volatile.Write(ref s_defaultTimeoutTicks, timeout.Ticks);
        internal static void ResetDefaultTimeout() => System.Threading.Volatile.Write(ref s_defaultTimeoutTicks, DefaultTimeoutTicks);

        /// <summary>Waits until `condition` becomes true, at most `timeout` (an `undefined` = <see cref="DefaultTimeout"/>; otherwise a TimeSpan, a time value like `500ms`
        /// or milliseconds, as with `Sleep`). If the call comes from a VM (`CurrentThreadVm`), the waiting is not deaf (see <see cref="SleepTicks"/>):
        /// `leave`/`terminate` end it immediately (result false), the main program's queue keeps running. Returns true as soon as `condition` was true.
        /// `ArgumentException` if `timeout` is not a time specification.</summary>
        public static bool WaitUntil(Func<bool> condition, fire.Values.Value timeout)
        {
            long ticks;
            if (timeout.Kind == fire.Values.ValueKind.Undefined) ticks = System.Threading.Volatile.Read(ref s_defaultTimeoutTicks);
            else if (!TimeNatives.TryTimeTicks(timeout, out ticks, out var error)) throw new ArgumentException("Invalid wait time: " + error);

            if (CurrentThreadVm is { } vm) return vm.WaitUntilTicks(condition, ticks);

            long deadline = Stopwatch.GetTimestamp() + (long)(ticks * (Stopwatch.Frequency / 10_000_000.0));
            while (!condition())
            {
                if (Stopwatch.GetTimestamp() >= deadline) return false;
                System.Threading.Thread.Sleep(2);
            }
            return true;
        }

        private bool WaitUntilTicks(Func<bool> condition, long ticks)
        {
            long deadline = Stopwatch.GetTimestamp() + (long)(ticks * (Stopwatch.Frequency / 10_000_000.0));
            while (true)
            {
                if (condition()) return true;
                PollSignalsAfterOp();
                if (_stopExecutionRequested) return false;

                long remaining = deadline - Stopwatch.GetTimestamp();
                if (remaining <= 0) return false;
                int milliseconds = (int)Math.Clamp(remaining * 1000 / Stopwatch.Frequency, 1, 5);
                FireRuntime.WaitForWake(milliseconds);
            }
        }

        /// <summary>`Sleep`: puts this thread to sleep for `ticks` (100 ns) - but not deaf. Between the short sleep pieces, what otherwise runs at the safe
        /// points runs (<see cref="PollSignalsAfterOp"/>): `leave`/`terminate` end the sleeping immediately, delivered fire-thread exceptions are handled, and
        /// in the main program the automatic processing of the queue works (sections, `fire global` jobs, host callbacks; not with `#nosync`).
        /// An entry in the queue wakes the sleeping earlier.</summary>
        public void SleepTicks(long ticks)
        {
            long deadline = Stopwatch.GetTimestamp() + (long)(ticks * (Stopwatch.Frequency / 10_000_000.0));
            while (true)
            {
                PollSignalsAfterOp();
                if (_stopExecutionRequested) return;

                long remaining = deadline - Stopwatch.GetTimestamp();
                if (remaining <= 0) return;
                int milliseconds = (int)Math.Clamp(remaining * 1000 / Stopwatch.Frequency, 1, 20);
                FireRuntime.WaitForWake(milliseconds);
            }
        }
    }
}
