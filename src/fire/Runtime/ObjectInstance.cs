using System.Collections.Generic;
using fire.Bytecode;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>
    /// Runtime instance of a class. Carries exactly one owner (scope or another
    /// ObjectInstance, SPEC 2) and is itself an IOwner again (fields
    /// can own further object instances).
    ///
    /// Fields live in a Runtime.FieldStore (see there) - for declared fields KNOWN AT
    /// COMPILE TIME (the normal case for every field access generated
    /// by the compiler) a fixed array slot instead of a
    /// dictionary lookup per access (see RtClass/RuntimeClass.FieldIndex),
    /// with a dictionary fallback for everything else (e.g. test code without
    /// a RuntimeClass). Member access (`obj.field`) stays by name
    /// at runtime (MemberExpr.Name remains a string, see the resolver
    /// comment on that - the static type of `obj` is not always known),
    /// but the name resolution itself is cached via RuntimeClass.FieldIndex
    /// instead of being recomputed on every access.
    /// </summary>
    public sealed class ObjectInstance : IOwner
    {
        /// <summary>Name of the class (SPEC 2) - deliberately ONLY the name, not
        /// the full AST (ClassDecl) as before: at runtime the VM reads
        /// only this name (for RuntimeClass lookups, error
        /// messages), never fields/method bodies directly from the AST - those
        /// have long since been compiled to RuntimeClass.Fields/Methods/Chunks.
        /// Important for the planned program serialisation: as a result,
        /// every ObjectInstance (and thus potentially every saved
        /// program state) does not carry the complete Stmt/Expr AST hierarchy.</summary>
        public string ClassName { get; }

        /// <summary>The compiled counterpart to ClassDef (see Bytecode.
        /// RuntimeClass) - basis for the fast, slot-indexed
        /// field access (see Fields/FieldStore docs). Null only for
        /// ObjectInstances that are constructed OUTSIDE the normal compiler/VM pipeline
        /// directly (e.g. pure ownership-model tests in
        /// Program.cs) - Fields then falls back completely to the dictionary
        /// fallback, functionally unchanged, just without the
        /// speed advantage.</summary>
        public RuntimeClass? RtClass { get; private set; }

        public IOwner Owner { get; private set; }
        public FieldStore Fields { get; }

        // The owned children (see OwnedSet): most objects own none, the rest mostly exactly one.
        private OwnedSet _owned;
        private List<IOwnedLeaf>? _leaves; // owned arrays and buffers
        private bool _destroyed;

        public bool IsDestroyed => _destroyed;

        /// <summary>Destroyed AND the destruction batch has ended (see <see cref="DestroyBatch"/>): from now on any use is an error (SPEC 2.5).
        /// Until then a destructor may still use the other objects of its scope, including those already destroyed.</summary>
        public bool IsDead => _dead;
        private bool _dead;

        /// <summary>The destruction batch has ended: the object is dead. Its class is forgotten so that the VM's inline caches (they compare the
        /// class) no longer hit it - the slow path reports the use.</summary>
        internal void Kill()
        {
            _dead = true;
            RtClass = null;
        }

        /// <summary>Unique, monotonically increasing ID - see ThreadShareLock.Order docs (basis of a future global
        /// lock order across multiple trees). Assigned only on the FIRST read (atomic counting on every
        /// `new` would be a noticeable item in object creation for a field that hardly anyone reads): the order of the IDs
        /// is that of first access, not of creation.</summary>
        public long Id
        {
            get
            {
                long id = System.Threading.Volatile.Read(ref _id);
                if (id != 0) return id;
                long fresh = System.Threading.Interlocked.Increment(ref _nextId);
                id = System.Threading.Interlocked.CompareExchange(ref _id, fresh, 0);
                return id != 0 ? id : fresh;
            }
        }
        private long _id;
        private static long _nextId;

        /// <summary>Null as long as this object was never (directly or as a
        /// descendant of a checked-out ancestor) the target of `taking` -
        /// then every field access runs entirely without locking overhead (see
        /// TryGetFieldLocked/SetFieldLocked). Set via
        /// ActivateThreadSharing, see there for the exact semantics.</summary>
        public ThreadShareLock? ThreadLock { get; private set; }

        /// <summary>The probes of this object (`probe obj.member changed ...`) or null - the normal case.</summary>
        public ProbeTable? Probes { get; private set; }

        /// <summary>If this field is non-null (tree lock or probes), writes do not take the VM's inline-cache fast paths,
        /// but the slow path that respects locks and probes. A single comparison on the hot path.</summary>
        public object? AccessGuard { get; private set; }

        private void RefreshGuard() => AccessGuard = (object?)ThreadLock ?? Probes;

        /// <summary>The probe table of the object, created on demand (turns off the fast paths for this object).</summary>
        public ProbeTable GetOrCreateProbes()
        {
            if (Probes == null)
            {
                Probes = new ProbeTable();
                RefreshGuard();
            }
            return Probes;
        }

        /// <summary>Does this object belong to the shared area of the GLOBAL variables (see GlobalsBroker/docs/THREADING_DESIGN.md
        /// section 7)? These are all objects owned (directly or via other objects) by the main program's global scope
        /// as soon as a `fire` thread is running. Fire threads read them directly (under the tree lock), but change them only inside a
        /// section that the main program grants at `sync globals`.</summary>
        public bool InGlobalsDomain { get; private set; }

        /// <summary>Takes this tree (itself and all owned objects) into the shared area of the globals and activates locking for it
        /// (an already existing tree lock, e.g. from `taking`, stays in place). Idempotent.</summary>
        public void MarkGlobalsDomain(ThreadShareLock treeLock)
        {
            if (InGlobalsDomain) return;
            InGlobalsDomain = true;
            ThreadLock ??= treeLock;
            RefreshGuard();
            for (int i = 0; i < _owned.Count; i++)
                _owned[i].MarkGlobalsDomain(ThreadLock);
        }

        /// <summary>Hidden back-link to the original, if THIS
        /// object itself is a `taking` copy (see
        /// docs/THREADING_DESIGN.md section 3) - basis for `sync`/
        /// `sync flat`, which of course need to know where to write back to.
        /// Null for an object that is not a copy (the normal case).</summary>
        public ObjectInstance? SyncOrigin { get; set; }

        /// <summary>Every node of a `taking` copy (not only the root, which carries `SyncOrigin`): at the end of the fire thread
        /// these objects stay untouched - they are copies of objects of the main program and trigger no destructors there.</summary>
        public bool IsTakingCopy { get; set; }

        /// <summary>Set (on `new`, see VM.NewObject) if this
        /// instance comes from an `actor` declaration (docs/
        /// THREADING_DESIGN.md section 2) - null for perfectly normal objects.
        /// This instance itself is the actual "is it an actor?"
        /// marker in the whole rest of the VM (instead of a bool field of its own):
        /// every method call on an object with a mailbox set
        /// becomes a message (VM.CallMethod) instead of executing directly -
        /// regardless of which thread the call comes from (also
        /// from the actor's own "home" thread, see THREADING_DESIGN.md
        /// for the deliberate simplification of this stage).</summary>
        public ActorMailbox? Mailbox { get; set; }

        public ObjectInstance(string className, IOwner initialOwner, RuntimeClass? rtClass = null)
        {
            ClassName = className;
            RtClass = rtClass;
            Fields = new FieldStore(rtClass);
            Owner = initialOwner;
            Owner.AddOwned(this);
        }

        // -----------------------------------------------------------
        // Multithreading: Baum-weites Locking (docs/THREADING_DESIGN.md 4.5)
        // -----------------------------------------------------------

        /// <summary>Activates thread sharing for THIS entire ownership
        /// tree (itself and recursively all owned objects) with the
        /// given, shared tree lock - idempotent: stops the
        /// recursion as soon as a node already carries the same or a
        /// (theoretically) different lock, so it does not run through completely again on repeated
        /// `taking` of the same (sub)tree.
        /// Cycles are unproblematic here, since the ownership graph is by
        /// construction free of cycles (see TakeTo/IsAncestorOf).</summary>
        public void ActivateThreadSharing(ThreadShareLock treeLock)
        {
            if (ThreadLock != null) return;
            ThreadLock = treeLock;
            RefreshGuard();
            for (int i = 0; i < _owned.Count; i++)
                _owned[i].ActivateThreadSharing(treeLock);
        }

        /// <summary>Reads a field under the tree lock if this object
        /// was ever the target of `taking` (otherwise unguarded, see
        /// ThreadLock docs - the normal case, no overhead).</summary>
        public bool TryGetFieldLocked(string name, out Value value)
        {
            if (ThreadLock == null) return Fields.TryGetValue(name, out value);
            ThreadLock.Enter();
            try { return Fields.TryGetValue(name, out value); }
            finally { ThreadLock.Exit(); }
        }

        public bool HasFieldLocked(string name)
        {
            if (ThreadLock == null) return Fields.ContainsKey(name);
            ThreadLock.Enter();
            try { return Fields.ContainsKey(name); }
            finally { ThreadLock.Exit(); }
        }

        /// <summary>Writes a field under the tree lock. Deliberately held as a SHORT
        /// lock restricted to exactly this one dictionary operation
        /// (not across a whole property fallback chain,
        /// see VM.SetField) - a minimal race possibility between a
        /// preceding HasFieldLocked check and this Set is deliberately
        /// accepted here (last-writer-wins is the basic philosophy
        /// of the entire sync model anyway), whereas a lock held over several steps
        /// would execute arbitrary, potentially slow user code
        /// (property setter calls) with the lock held - that
        /// would block other threads for unnecessarily long.</summary>
        public void SetFieldLocked(string name, Value value)
        {
            if (ThreadLock == null) { Fields[name] = value; return; }
            ThreadLock.Enter();
            try { Fields[name] = value; }
            finally { ThreadLock.Exit(); }
        }

        // -----------------------------------------------------------
        // IOwner (fields of this instance can themselves own objects again)
        // -----------------------------------------------------------
        public IReadOnlyList<ObjectInstance> OwnedObjects => _owned.AsList();
        public void AddOwned(ObjectInstance obj)
        {
            _owned.Add(obj);
            // A newly owned item in a shared tree becomes part of it immediately (otherwise it would be readable without a lock).
            if (InGlobalsDomain) obj.MarkGlobalsDomain(ThreadLock!);
            else if (ThreadLock != null) obj.ActivateThreadSharing(ThreadLock);
        }
        public void RemoveOwned(ObjectInstance obj) => _owned.Remove(obj);
        public void AddLeaf(IOwnedLeaf leaf) => (_leaves ??= new List<IOwnedLeaf>()).Add(leaf);
        public void RemoveLeaf(IOwnedLeaf leaf) => _leaves?.Remove(leaf);

        // -----------------------------------------------------------
        // Ownership-Transfer: TakeUpwards / TakeGlobal / TakeTo (SPEC 2.2)
        // -----------------------------------------------------------

        /// <summary>The owner becomes the parent scope of the current owner scope. Valid only
        /// if the current owner is a scope (not an object) and
        /// this scope has a parent (the global scope has none).</summary>
        public void TakeUpwards()
        {
            if (Owner is not Scope currentScope)
                throw new OwnershipException(
                    "TakeUpwards is only valid if the current owner is a scope.");
            if (currentScope.Parent == null)
                throw new OwnershipException(
                    "TakeUpwards: the current scope has no parent scope (already global).");
            Reparent(currentScope.Parent);
        }

        /// <summary>The owner becomes the global scope.</summary>
        public void TakeGlobal(Scope globalScope) => Reparent(globalScope);

        /// <summary>The owner becomes <paramref name="target"/>. Checks for cycles
        /// (the target object must not already be transitively owned by this object)
        /// and for an ongoing cascade deletion of the target: if the target is
        /// already being destroyed, this object is treated as if the
        /// handover had occurred one second BEFORE the deletion began - it is destroyed
        /// immediately along with it (SPEC 2.2, "race with ongoing deletion").</summary>
        public void TakeTo(ObjectInstance target, IDestructRunner runner)
        {
            if (target._destroyed)
            {
                Owner.RemoveOwned(this);
                Destroy(runner);
                return;
            }

            if (IsAncestorOf(target))
                throw new OwnershipException(
                    "TakeTo: cycle detected - the target object is already owned (directly or transitively) by this object.");

            Reparent(target);
        }

        private void Reparent(IOwner newOwner)
        {
            Owner.RemoveOwned(this);
            Owner = newOwner;
            newOwner.AddOwned(this);
        }

        /// <summary>Generalised variant of TakeUpwards/TakeGlobal for
        /// an arbitrary target scope - intended internally for the VM (SPEC 2.3:
        /// "return hands ownership to the calling scope"), not part of the
        /// public script API (TakeUpwards/TakeGlobal/TakeTo remain for that).</summary>
        public void ReparentTo(Scope newOwner) => Reparent(newOwner);

        /// <summary>The object becomes an argument of the call of <paramref name="scope"/> (see Scope.AddArgument): it dies last.</summary>
        public void ReparentToArgument(Scope scope)
        {
            if (_destroyed) return;
            Owner.RemoveOwned(this);
            Owner = scope;
            scope.AddArgument(this);
        }

        /// <summary>Like <see cref="ReparentTo"/> for an arbitrary owner (scope or object) - for OwnershipWalk. A destroyed object stays where it is.</summary>
        internal void ReparentToOwner(IOwner newOwner)
        {
            if (_destroyed) return;
            Reparent(newOwner);
        }

        /// <summary>true if <paramref name="candidate"/> hangs anywhere below
        /// this object in the ownership tree (directly or transitively) -
        /// basis of the cycle protection in TakeTo.</summary>
        private bool IsAncestorOf(ObjectInstance candidate)
        {
            for (int i = 0; i < _owned.Count; i++)
            {
                var child = _owned[i];
                if (ReferenceEquals(child, candidate)) return true;
                if (child.IsAncestorOf(candidate)) return true;
            }
            return false;
        }

        // -----------------------------------------------------------
        // is from / is under (SPEC 6)
        // -----------------------------------------------------------

        /// <summary>`objekt is from ownerKandidat` - direkter Owner-Vergleich.</summary>
        public bool IsOwnedBy(object ownerCandidate) => ReferenceEquals(Owner, ownerCandidate);

        /// <summary>`object is under ownerCandidate` - transitively along the
        /// ownership chain (not the lexical scope parent chain!): the
        /// immediate owner, its owner (if again an object), etc. The
        /// chain ends as soon as a scope is reached (scopes have no
        /// "owner", only a lexical parent - that is deliberately a different
        /// relation and is not included here).</summary>
        public bool IsTransitivelyOwnedBy(object ownerCandidate)
        {
            IOwner current = Owner;
            while (true)
            {
                if (ReferenceEquals(current, ownerCandidate)) return true;
                if (current is ObjectInstance oi) current = oi.Owner;
                else if (current is fire.Values.ScriptArray array && array.LeafOwner != null) current = array.LeafOwner;
                else return false;
            }
        }

        // -----------------------------------------------------------
        // Cascade deletion (SPEC 2.3)
        // -----------------------------------------------------------

        /// <summary>End of a fire thread for a `taking` copy (it stays untouched, it triggers no destructor): what the thread itself created
        /// in it is destroyed; copies in it are treated the same way.</summary>
        public void DestroyOwnedNonCopies(IDestructRunner runner)
        {
            var children = new List<ObjectInstance>(_owned.AsList());
            foreach (var child in children)
            {
                if (child.IsTakingCopy) child.DestroyOwnedNonCopies(runner);
                else child.Destroy(runner);
            }
        }

        /// <summary>Destroys this object: first calls destruct() (via the
        /// runner provided by the evaluator), then cascades to all objects still
        /// owned by this object. Idempotent (calling multiple times is
        /// harmless, e.g. if an object ends up in the same cascade both regularly and via the
        /// TakeTo race handling).</summary>
        public void Destroy(IDestructRunner runner)
        {
            if (_destroyed) return;
            _destroyed = true;
            DestroyBatch.Enter();
            try { DestroyCore(runner); }
            finally { DestroyBatch.Exit(this); }
        }

        private void DestroyCore(IDestructRunner runner)
        {

            // Probes live with the object
            if (Probes != null) ProbeRegistry.Forget(Probes.RemoveAll());

            // Without a destructor in the class chain there is nothing to execute (an object without a RuntimeClass does not know the chain: the runner decides)
            if (RtClass == null || RtClass.HasDestructorInChain())
                runner.RunDestructor(this);

            _owned.DestroyAll(runner);
            LeafOwnership.DestroyAll(_leaves, runner);

            // A destroyed object belongs to no one any more: its former owner (usually a scope that is about to be reused)
            // must no longer point at it. `Owner` is never null - a placeholder accepts requests to the dead owner.
            Owner = DeadOwner.Instance;
        }
    }

    /// <summary>The destruction batch: leaving a scope, a `delete`, the end of the program destroy several objects one after another (in order of
    /// creation). A destructor still sees the other objects of the batch - including those already destroyed (the destructor of a writer flushes a stream that was destroyed before
    /// it). Only when the outermost batch ends are they dead (<see cref="ObjectInstance.IsDead"/>): after that, using them throws a DestroyedException.
    /// Per thread, because every thread leaves its own scopes.</summary>
    internal static class DestroyBatch
    {
        [System.ThreadStatic] private static int _depth;
        [System.ThreadStatic] private static List<ObjectInstance>? _zombies;

        public static void Enter() => _depth++;

        /// <summary>Leaves the batch; <paramref name="destroyed"/> (an object destroyed in the batch) dies as soon as the outermost one ends.</summary>
        public static void Exit(ObjectInstance? destroyed = null)
        {
            if (destroyed != null) (_zombies ??= new List<ObjectInstance>()).Add(destroyed);
            if (--_depth > 0) return;
            _depth = 0;
            var zombies = _zombies;
            if (zombies == null) return;
            _zombies = null;
            foreach (var obj in zombies) obj.Kill();
        }
    }
}
