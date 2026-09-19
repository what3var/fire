using System.Threading;

namespace fire.Runtime
{
    /// <summary>
    /// Ein Lock, der einem GANZEN ausgecheckten Ownership-Baum zugeordnet ist
    /// (siehe docs/THREADING_DESIGN.md Abschnitt 4.5 "Locking") - nicht einem
    /// einzelnen Knoten. Sobald ein Objekt zum ersten Mal Ziel von `taking`
    /// wird, bekommt es (und rekursiv alle von ihm besessenen Objekte, siehe
    /// ObjectInstance.ActivateThreadSharing) eine gemeinsame Instanz hiervon
    /// zugewiesen - JEDER Zugriff auf JEDEN Knoten in diesem Baum (auch ganz
    /// normale Lese-/Schreibzugriffe vom besitzenden Thread selbst, nicht nur
    /// `sync`) muss diesen Lock respektieren. Objekte, die nie mit `taking` in
    /// Berührung kommen, haben `ObjectInstance.ThreadLock == null` und zahlen
    /// dadurch keinerlei Locking-Overhead - das ist die im Design bewusst
    /// gewählte Kosten-Optimierung.
    ///
    /// Bewusst EIN Lock pro Baum statt pro Knoten: eine `sync`/`taking`-
    /// Operation berührt ohnehin nie mehr als einen Baum gleichzeitig (siehe
    /// Fall A/B/C in THREADING_DESIGN.md - Objektreferenzen werden bei einem
    /// flachen Sync als atomare Knoten behandelt, nie rekursiv in einen
    /// FREMDEN Baum hinein synchronisiert), ein feingranulareres Modell hätte
    /// also keinen Sicherheitsgewinn, nur zusätzliche Deadlock-Komplexität
    /// durch mögliche Lock-Reihenfolgen zwischen mehreren Knoten-Locks.
    /// </summary>
    public sealed class ThreadShareLock
    {
        private readonly object _gate = new();

        /// <summary>Eindeutige, monoton steigende Erzeugungsreihenfolge -
        /// Grundlage für eine global konsistente Lock-Reihenfolge, falls doch
        /// einmal mehr als ein Baum-Lock gleichzeitig gehalten werden muss
        /// (aktuell kommt das in keiner der implementierten Operationen vor,
        /// aber zukünftige Erweiterungen sollten IMMER in aufsteigender
        /// Order-Reihenfolge sperren, um AB-BA-Deadlocks zu vermeiden).</summary>
        public long Order { get; }

        private static long _nextOrder;

        public ThreadShareLock()
        {
            Order = Interlocked.Increment(ref _nextOrder);
        }

        /// <summary>Blockierender Eintritt (für `sync`/normale Feldzugriffe
        /// auf einem geteilten Baum) - wartet, bis der Lock frei ist.</summary>
        public void Enter() => Monitor.Enter(_gate);

        /// <summary>Nicht-blockierender Eintritt (für `try sync`/`try sync flat`)
        /// - liefert sofort `false`, wenn der Lock gerade belegt ist, statt zu
        /// warten.</summary>
        public bool TryEnter() => Monitor.TryEnter(_gate);

        public void Exit() => Monitor.Exit(_gate);
    }
}
