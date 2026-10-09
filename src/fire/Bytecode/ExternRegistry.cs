using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace fire.Bytecode
{
    /// <summary>An actually linked native implementation of a function declared via
    /// `extern`. Takes the already MARSHALLED arguments
    /// (see VM.CallExtern: bool/long/double/char/string for
    /// value types, IntPtr for pointer arguments - real, pinned native
    /// memory, no script-internal PointerTarget any more) and returns the
    /// (likewise already native) return value, or null for "no
    /// return value" (ReturnType missing in the `extern` declaration).</summary>
    public delegate object? ExternFunction(object?[] args);

    /// <summary>
    /// Directory of actually linked `extern` implementations - separate
    /// from NativeRegistry because `extern` deliberately has TWO phases: the
    /// declaration (part of the script, already processed beforehand by the
    /// resolver/compiler, see ExternDecl/ResolvedRef.Extern) and the actual
    /// linking (host-side, AFTER compiling, even optional - a
    /// script can declare and compile `extern` without the
    /// implementation ever being registered; that fails only on the
    /// ACTUAL call, not on compiling). Hence lookup by
    /// name at runtime instead of by compile-time index as in NativeRegistry.
    /// </summary>
    public sealed class ExternRegistry
    {
        private readonly Dictionary<string, ExternFunction> _fns = new();

        public void Register(string name, ExternFunction fn) => _fns[name] = fn;

        public bool TryGet(string name, out ExternFunction fn) => _fns.TryGetValue(name, out fn!);

        /// <summary>A small demo linking against real native Windows APIs
        /// via P/Invoke (user32/kernel32) - shows the full marshalling path
        /// (script value -> real native type/address -> real OS call ->
        /// back into a script value). Runs only on Windows (the DLLs
        /// exist only there) - on Linux/macOS build a registry of its own
        /// with matching platform APIs instead, the pattern stays
        /// exactly the same.</summary>
        public static ExternRegistry CreateWinApiDemo()
        {
            var reg = new ExternRegistry();

            // extern int ShowMessageBox(string text, string caption)
            reg.Register("ShowMessageBox", args =>
                (long)WinApiNative.MessageBoxW(IntPtr.Zero, (string)args[0]!, (string)args[1]!, 0));

            // extern int GetTickCount()
            reg.Register("GetTickCount", args => (long)WinApiNative.GetTickCount());

            // extern int QueryPerformanceCounter(int* counter) - writes
            // a 64-bit counter through the pointer, i.e. 8 bytes - fits exactly
            // the generic 8-byte slot copy in VM.WriteNativeValue/
            // ReadNativeValue (unlike e.g. a 4-byte int* as with some
            // other WinAPI function, which would NOT fit together cleanly).
            reg.Register("QueryPerformanceCounter", args =>
                (long)WinApiNative.QueryPerformanceCounter((IntPtr)args[0]!));

            return reg;
        }

        private static class WinApiNative
        {
            [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
            public static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

            [DllImport("kernel32.dll", EntryPoint = "GetTickCount")]
            public static extern uint GetTickCount();

            [DllImport("kernel32.dll", EntryPoint = "QueryPerformanceCounter")]
            public static extern int QueryPerformanceCounter(IntPtr lpPerformanceCount);
        }
    }
}
