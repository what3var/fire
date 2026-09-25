using System;
using fire.Runtime;

namespace fire.Bytecode
{
    /// <summary>Eine Skript-`throw`, für die kein passender `catch` gefunden
    /// wurde. Die VM selbst wirft diesen Typ NICHT mehr intern (siehe VM.
    /// UnhandledException/docs/PORTING.md, Abschnitt "VM-interner
    /// Kontrollfluss") - `VM.Run()` kehrt in diesem Fall ganz normal zurück,
    /// der Aufrufer prüft danach `vm.UnhandledException`. Dieser Typ bleibt
    /// als reine C#-Bequemlichkeit für Host-Code, der lieber mit einer
    /// echten, geworfenen Exception arbeitet - siehe
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
            string msg = instance.Fields.TryGetValue("message", out var m) ? m.ToString() : "(kein 'message'-Feld)";
            return $"Unbehandelte Exception vom Typ '{instance.ClassName}': {msg}";
        }
    }
}
