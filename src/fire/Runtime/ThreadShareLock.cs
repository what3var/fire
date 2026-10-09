using System.Threading;

namespace fire.Runtime
{
    /// <summary>
    /// A lock assigned to an ENTIRE checked-out ownership tree
    /// (see docs/THREADING_DESIGN.md section 4.5 "Locking") - not to a
    /// single node. As soon as an object becomes the target of `taking` for the first time,
    /// it (and recursively all objects owned by it, see
    /// ObjectInstance.ActivateThreadSharing) is assigned a common instance of this
    /// - EVERY access to EVERY node in this tree (even perfectly
    /// normal read/write accesses by the owning thread itself, not only
    /// `sync`) must respect this lock. Objects that never come into
    /// contact with `taking` have `ObjectInstance.ThreadLock == null` and pay
    /// thereby no locking overhead at all - that is the cost
    /// optimisation deliberately chosen in the design.
    ///
    /// Deliberately ONE lock per tree instead of per node: a `sync`/`taking`
    /// operation never touches more than one tree at a time anyway (see
    /// case A/B/C in THREADING_DESIGN.md - object references are treated as atomic nodes in a
    /// flat sync, never synchronised recursively into a
    /// FOREIGN tree), so a finer-grained model would
    /// bring no safety gain, only additional deadlock complexity
    /// through possible lock orders between several node locks.
    /// </summary>
    public sealed class ThreadShareLock
    {
        private readonly object _gate = new();

        /// <summary>Unique, monotonically increasing creation order -
        /// basis for a globally consistent lock order, should
        /// more than one tree lock ever have to be held at the same time
        /// (currently this occurs in none of the implemented operations,
        /// but future extensions should ALWAYS lock in ascending
        /// Order sequence, to avoid AB-BA deadlocks).</summary>
        public long Order { get; }

        private static long _nextOrder;

        public ThreadShareLock()
        {
            Order = Interlocked.Increment(ref _nextOrder);
        }

        /// <summary>Blocking entry (for `sync`/normal field accesses
        /// on a shared tree) - waits until the lock is free.</summary>
        public void Enter() => Monitor.Enter(_gate);

        /// <summary>Non-blocking entry (for `try sync`/`try sync flat`)
        /// - returns `false` immediately if the lock is currently held, instead of
        /// waiting.</summary>
        public bool TryEnter() => Monitor.TryEnter(_gate);

        public void Exit() => Monitor.Exit(_gate);
    }
}
