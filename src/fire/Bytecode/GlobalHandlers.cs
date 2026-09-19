using System.Collections.Generic;

namespace fire.Bytecode
{
    /// <summary>
    /// Globale (prozessweite, über ALLE VM-Instanzen/Threads geteilte)
    /// Registrierung für die beiden Handler aus docs/THREADING_DESIGN.md
    /// Abschnitt 6.2/6.3: `catch threads(ExceptionType e)`/`catch threads()`
    /// (unbehandelte Nutzer-Exceptions aus Fire-Threads) und
    /// `catch terminate(v)` (globaler Not-Aus). Beide werden - anders als ein
    /// normales `try`/`catch` - nur EINMAL, global registriert (nicht pro
    /// Scope/Aufruf), und laufen laut Design ausschließlich auf dem
    /// Main-Thread (siehe VM.HandleDeliveredThreadException/
    /// RunTerminateHandlerIfAny).
    ///
    /// Bewusst reine Speicherung ohne Matching-Logik - die MATCHING-Logik
    /// (Exception-Typname gegen die Basisklassen-Kette prüfen) lebt in
    /// VM.InstanceMatchesClassName (braucht Zugriff auf die aufrufende
    /// VM-Instanz eigene `_classes`), nicht hier.
    /// </summary>
    public static class GlobalHandlers
    {
        /// <summary>Wie HandlerTemplate.Catches bei einem normalen try/catch:
        /// (TypeName, Proto) in Registrierungsreihenfolge - `TypeName == null`
        /// steht für `catch threads()` (matcht alles). Der Proto hat entweder
        /// 0 Parameter (`catch threads()`, Body ohne gebundene Variable) oder
        /// 1 Parameter (`catch threads(ExceptionType e)`, `e` gebunden).</summary>
        internal static readonly List<(string? TypeName, FunctionProto Proto)> ThreadsCatches = new();

        /// <summary>Höchstens einer - `catch terminate(v)` gibt es nur einmal
        /// im ganzen Programm (kein Stack wie bei try/catch). 0 oder 1
        /// Parameter, analog zu ThreadsCatches.</summary>
        internal static FunctionProto? TerminateHandler;

        private static readonly object Gate = new();

        public static void RegisterThreadsCatch(string? typeName, FunctionProto proto)
        {
            lock (Gate) ThreadsCatches.Add((typeName, proto));
        }

        public static void RegisterTerminateCatch(FunctionProto proto)
        {
            lock (Gate) TerminateHandler = proto;
        }

        /// <summary>Nur für Tests gedacht (siehe VM.ResetTerminateForTests) -
        /// setzt die globale Registrierung zwischen voneinander unabhängigen
        /// Programmläufen im selben Prozess zurück.</summary>
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
