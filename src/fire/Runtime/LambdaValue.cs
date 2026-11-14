using fire.Bytecode;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>
    /// Runtime value of a lambda function. Needs - unlike classic
    /// closures - NO captured enclosing scope: per SPEC
    /// 4.2 lambdas see only their own scope (created anew on every call) plus
    /// the global scope anyway. A LambdaValue therefore only has to remember the compiled
    /// function body (proto) and the currently bound 'this' (OnTarget).
    ///
    /// Ownership (SPEC 4.2 / 2.1): lambda values are treated like
    /// object instances - the owner is the object on direct field assignment, otherwise the
    /// current scope. This policy (which owner for which assignment) is put in place
    /// by the evaluator/compiler at the respective assignment site; the
    /// owner field here is therefore optional and is not yet filled at this stage
    /// (function/call frames) - lambda values can already be
    /// created, bound and called, their ownership integration follows
    /// with the class/object stage.
    /// </summary>
    /// <summary>Where a lambda that went to another thread runs (docs/ROADMAP_TASKS.md step 8 (e)): bound with `on`, its body runs where its target
    /// lives - a call from elsewhere is a job there, the caller waits for the result.</summary>
    public enum LambdaHome : byte
    {
        /// <summary>Where it is: an unbound lambda, or one whose target lives in the same thread.</summary>
        Here,
        /// <summary>Its target is an object of the main program: a call in a fire thread is a job of the main program (like `await on obj`).</summary>
        MainProgram,
        /// <summary>Its target is an object of another fire thread (not served yet: a call is an error).</summary>
        OtherThread,
    }

    public sealed class LambdaValue : IOwnedLeaf, IOwner
    {
        /// <summary>Where its calls run (set when it goes to another thread: `taking`, `handover`, a field of what a thread gets).</summary>
        public LambdaHome Home { get; set; }
        /// <summary>With <see cref="LambdaHome.OtherThread"/>: the fire thread its target lives in (the VM's inbox of that thread; null: an actor - its
        /// mailbox takes the calls).</summary>
        public object? HomeBox { get; set; }

        // ---- the owner structure (ROADMAP_TASKS step 8): a lambda belongs to a scope or an object like an array (LeafOwner), dies with it, and owns
        //      its store - the copies and moved instances it captured - like an array its elements
        public IOwner? LeafOwner { get; set; }
        public bool IsDestroyed { get; private set; }
        private OwnedSet _ownedObjects;
        private System.Collections.Generic.List<IOwnedLeaf>? _parts;
        public System.Collections.Generic.IReadOnlyList<ObjectInstance> OwnedObjects => _ownedObjects.AsList();
        public void AddOwned(ObjectInstance obj) => _ownedObjects.Add(obj);
        public void RemoveOwned(ObjectInstance obj) => _ownedObjects.Remove(obj);
        public void AddLeaf(IOwnedLeaf leaf) => (_parts ??= new System.Collections.Generic.List<IOwnedLeaf>()).Add(leaf);
        public void RemoveLeaf(IOwnedLeaf leaf) => _parts?.Remove(leaf);
        public void MarkDestroyed(IDestructRunner runner)
        {
            if (IsDestroyed) return;
            if (!_ownedObjects.IsEmpty) _ownedObjects.DestroyAll(runner);
            IsDestroyed = true;
            LeafOwner = null;
            if (_parts != null) foreach (var part in _parts.ToArray()) part.MarkDestroyed(runner);
            _parts = null;
        }

        public FunctionProto Proto { get; }

        /// <summary>The object bound via 'on' (this context), or null
        /// if no 'on' was used.</summary>
        public object? OnTarget { get; }

        public IOwner? Owner { get; private set; }

        /// <summary>Values of the outer local variables used by the body, COPIED on creation (lambda captures, SPEC 4.2),
        /// or null. On a call they land as slots directly after the parameters.</summary>
        public Value[]? Captures { get; }

        /// <summary>The type arguments of the generic code the lambda was made in that its body uses (SetTypeArgs for a lambda), or null: a call of
        /// the lambda gives them to its scope (TakeCallTypeArgs), where its body names the i-th `$i@`.</summary>
        public string[]? TypeArgs { get; init; }

        public LambdaValue(FunctionProto proto, object? onTarget, IOwner? owner = null, Value[]? captures = null)
        {
            Proto = proto;
            OnTarget = onTarget;
            Owner = owner;
            Captures = captures;
        }

        /// <summary>`a on obj2` - creates a NEW lambda value with a different
        /// this context; `a` itself stays unchanged (SPEC 4.2). The same proto
        /// is reused (execution, not definition, changes).</summary>
        public LambdaValue WithOnTarget(object? newTarget, IOwner? owner = null) =>
            new(Proto, newTarget, owner, (Value[]?)Captures?.Clone()) { TypeArgs = TypeArgs };   // (the VM gives the new lambda copies for its own store)

        internal void SetOwnerInternal(IOwner newOwner) => Owner = newOwner;

        public bool IsOwnedBy(object ownerCandidate) => Owner != null && ReferenceEquals(Owner, ownerCandidate);

        public bool IsTransitivelyOwnedBy(object ownerCandidate)
        {
            IOwner? current = Owner;
            while (current != null)
            {
                if (ReferenceEquals(current, ownerCandidate)) return true;
                current = current is ObjectInstance oi ? oi.Owner : null;
            }
            return false;
        }
    }
}
