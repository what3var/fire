using System;
using System.Collections.Generic;
using System.Threading;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>
    /// Die Vermittlung zwischen dem Hauptprogramm (dem die globalen Variablen gehören) und seinen `fire`-Threads
    /// (docs/THREADING_DESIGN.md Abschnitt 7):
    ///
    /// - Fire-Threads LESEN Globals direkt (kein Snapshot), geschützt durch einen gemeinsamen Lock (<see cref="Lock"/>, derselbe für
    ///   die Werte der Globals und für alle Objekte, die dem globalen Scope gehören).
    /// - ÄNDERN dürfen sie den geteilten Bereich nur innerhalb einer SEKTION: der Thread meldet sich an (<see cref="EnterSection"/>) und
    ///   wartet; das Hauptprogramm erteilt die Sektionen der Reihe nach, wenn es `sync globals` aufruft (<see cref="Drain"/>) - immer nur
    ///   eine zugleich, während es selbst wartet. Der Zustand der Globals ist damit für das Hauptprogramm jederzeit klar, und es gibt
    ///   genau einen Schreiber.
    /// - `fire global { ... }` reiht stattdessen einen Auftrag ein (<see cref="PostJob"/>), den das Hauptprogramm beim nächsten
    ///   `sync globals` ausführt; der Thread wartet nicht.
    ///
    /// Die Reihenfolge ist FIFO. Endet das Hauptprogramm (<see cref="Close"/>), werden wartende und künftige Sektionen sofort gewährt
    /// - niemand hängt an einem Besitzer, der nicht mehr antwortet.
    /// </summary>
    public sealed class GlobalsBroker
    {
        /// <summary>Der globale Scope des Hauptprogramms.</summary>
        public Scope Scope { get; }

        /// <summary>Schützt Globals-Werte und alle Objekte des geteilten Bereichs.</summary>
        public ThreadShareLock Lock { get; }

        /// <summary>Das Hauptprogramm: nur seine VM führt `sync globals` aus.</summary>
        public VM Owner { get; }

        private abstract class Request { }

        private sealed class SectionRequest : Request
        {
            public readonly ManualResetEventSlim Granted = new(false);
            public readonly ManualResetEventSlim Done = new(false);
        }

        private sealed class JobRequest : Request
        {
            public LambdaValue Lambda = null!;
            public Value[] Args = Array.Empty<Value>();
            public Scope Holder = null!;
        }

        private readonly object _gate = new();
        private readonly Queue<Request> _queue = new();
        private bool _closed;

        public GlobalsBroker(VM owner, Scope scope)
        {
            Owner = owner;
            Scope = scope;
            Lock = new ThreadShareLock();
            scope.SharingLock = Lock;
        }

        /// <summary>Liegt etwas in der Warteschlange?</summary>
        public bool HasPending
        {
            get { lock (_gate) return _queue.Count > 0; }
        }

        public bool IsClosed
        {
            get { lock (_gate) return _closed; }
        }

        // -------------------------------------------------------------
        // Thread-Seite
        // -------------------------------------------------------------

        /// <summary>Meldet den aufrufenden Thread an und wartet, bis das Hauptprogramm die Sektion erteilt. Danach darf er den geteilten
        /// Bereich ändern, bis er <see cref="ExitSection"/> aufruft. Liefert ein Handle für den Abschluss (oder null, wenn das
        /// Hauptprogramm schon beendet ist - dann wird sofort gewährt und es gibt nichts abzuschließen).</summary>
        public object? EnterSection()
        {
            var request = new SectionRequest();
            lock (_gate)
            {
                if (_closed) return null;
                _queue.Enqueue(request);
            }
            FireRuntime.WakeWaitingOwner();
            request.Granted.Wait();
            return request;
        }

        /// <summary>Beendet die Sektion, die <see cref="EnterSection"/> erteilt hat.</summary>
        public void ExitSection(object? handle)
        {
            if (handle is SectionRequest request) request.Done.Set();
        }

        /// <summary>`fire global { ... }`: reiht den Auftrag ein und kehrt sofort zurück. `holder` besitzt die für den Auftrag kopierten
        /// Objekte und wird freigegeben, sobald er gelaufen ist.</summary>
        public void PostJob(LambdaValue lambda, Value[] args, Scope holder)
        {
            lock (_gate)
            {
                if (_closed) { holder.Release(NullDestructRunner.Instance); return; }
                _queue.Enqueue(new JobRequest { Lambda = lambda, Args = args, Holder = holder });
            }
            FireRuntime.WakeWaitingOwner();
        }

        // -------------------------------------------------------------
        // Besitzer-Seite
        // -------------------------------------------------------------

        /// <summary>`sync globals`: arbeitet alles ab, was bis jetzt in der Warteschlange liegt (auch das, was währenddessen
        /// dazukommt), der Reihe nach. Liefert die Anzahl der bearbeiteten Einträge. Nur auf dem Thread des Besitzers aufrufen.</summary>
        public int Drain()
        {
            int handled = 0;
            while (true)
            {
                Request? next;
                lock (_gate)
                {
                    if (_queue.Count == 0) return handled;
                    next = _queue.Dequeue();
                }
                handled++;
                switch (next)
                {
                    case SectionRequest section:
                        section.Granted.Set();
                        section.Done.Wait(); // der Thread arbeitet, das Hauptprogramm wartet
                        break;
                    case JobRequest job:
                        Owner.RunGlobalsJob(job.Lambda, job.Args);
                        job.Holder.Release(Owner);
                        break;
                }
            }
        }

        /// <summary>Das Hauptprogramm ist beendet: wartende Sektionen werden sofort gewährt (ohne auf ihr Ende zu warten), künftige
        /// ebenso, eingereihte Aufträge verfallen.</summary>
        public void Close()
        {
            List<Request> pending;
            lock (_gate)
            {
                if (_closed) return;
                _closed = true;
                pending = new List<Request>(_queue);
                _queue.Clear();
            }
            foreach (var request in pending)
            {
                if (request is SectionRequest section) section.Granted.Set();
                else if (request is JobRequest job) job.Holder.Release(NullDestructRunner.Instance);
            }
        }
    }
}
