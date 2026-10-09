using System;
using fire.Runtime;

namespace fire.Bytecode
{
    /// <summary>A script `throw` for which no matching `catch` was
    /// found. The VM itself NO LONGER throws this type internally (see VM.
    /// UnhandledException/docs/PORTING.md, section "VM-internal
    /// control flow") - `VM.Run()` returns quite normally in this case,
    /// the caller then checks `vm.UnhandledException`. This type remains
    /// as a pure C# convenience for host code that prefers to work with a
    /// real, thrown exception - see
    /// `throw new UncaughtScriptException(vm.UnhandledException)`.</summary>
    public sealed class UncaughtScriptException : Exception
    {
        public ObjectInstance ExceptionInstance { get; }

        public UncaughtScriptException(ObjectInstance instance)
            : base(BuildMessage(instance))
        {
            ExceptionInstance = instance;
        }

        private static string BuildMessage(ObjectInstance instance)
        {
            string msg = instance.Fields.TryGetValue("message", out var m) ? m.ToString() : "(no 'message' field)";
            return $"Unbehandelte Exception vom Typ '{instance.ClassName}': {msg}";
        }
    }
}
