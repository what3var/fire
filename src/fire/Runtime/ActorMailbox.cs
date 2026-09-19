using System.Collections.Concurrent;
using System.Threading;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>Eine einzelne Nachricht in einer Actor-Mailbox: Methodenname +
    /// Argumente, genau wie bei einem normalen Methodenaufruf - nur eben nicht
    /// sofort ausgeführt, sondern erst bei `process`/`try process` (siehe
    /// Ast.ProcessStmt/TryProcessExpr, VM.ProcessOneMessage).</summary>
    public readonly struct ActorMessage
    {
        public string MethodName { get; }
        public Value[] Args { get; }

        public ActorMessage(string methodName, Value[] args)
        {
            MethodName = methodName;
            Args = args;
        }
    }

    /// <summary>Die Mailbox EINER Actor-Instanz (docs/THREADING_DESIGN.md
    /// Abschnitt 2) - jede Actor-Instanz (siehe Runtime.ObjectInstance.
    /// Mailbox) bekommt bei `new` genau eine eigene. `Enqueue` wird von
    /// JEDEM Thread aus aufgerufen (immer dann, wenn irgendwo im Programm
    /// eine Methode auf einer Actor-Referenz aufgerufen wird, siehe
    /// VM.CallMethod), `TryProcessOne` NUR vom "Heimat"-Thread des Actors
    /// (durch `process`/`try process` - diese Beschränkung wird hier bewusst
    /// NICHT technisch erzwungen, siehe Resolver.ResolveProcessTarget für
    /// die eigentliche Prüfung).
    ///
    /// Das `SemaphoreSlim` hält den "wie viele Nachrichten warten"-Zähler
    /// IMMER exakt synchron mit der Queue: jedes Enqueue erhöht ihn um
    /// genau 1, jedes ERFOLGREICHE TryProcessOne (ob blockierend oder nicht)
    /// verringert ihn um genau 1 - so kann `try process` einfach `Wait(0)`
    /// probieren (nicht-blockierendes Anfragen "ist gerade was da?"), ohne
    /// dass sich Zähler und tatsächlicher Queue-Inhalt je auseinander
    /// entwickeln können.</summary>
    public sealed class ActorMailbox
    {
        private readonly ConcurrentQueue<ActorMessage> _queue = new();
        private readonly SemaphoreSlim _signal = new(0);

        public void Enqueue(ActorMessage message)
        {
            _queue.Enqueue(message);
            _signal.Release();
        }

        /// <summary>`blocking=true` (siehe Ast.ProcessStmt): wartet, bis
        /// mindestens eine Nachricht da ist, und liefert dann IMMER `true`.
        /// `blocking=false` (siehe Ast.TryProcessExpr): liefert sofort
        /// `false`, wenn gerade nichts wartet.</summary>
        public bool TryProcessOne(bool blocking, out ActorMessage message)
        {
            if (blocking)
                _signal.Wait();
            else if (!_signal.Wait(0))
            {
                message = default;
                return false;
            }

            // Der Zähler wurde oben bereits verringert (Wait ist hier immer
            // erfolgreich durchgelaufen) - die Queue MUSS an dieser Stelle
            // mindestens ein Element haben (siehe Klassenkommentar zur
            // Synchronität von Zähler und Queue-Inhalt).
            bool ok = _queue.TryDequeue(out message);
            System.Diagnostics.Debug.Assert(ok, "ActorMailbox: Signal ohne zugehörige Nachricht - Zähler/Queue sind auseinandergelaufen.");
            return ok;
        }
    }
}
