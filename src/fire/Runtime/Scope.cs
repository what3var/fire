using System;
using System.Linq;
using System.Collections.Generic;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>
    /// Runtime counterpart to a scope node recognised by the resolver (block,
    /// function/method/lambda body, global scope). Variables are slot-indexed
    /// (index corresponds exactly to what the resolver determined for the respective
    /// IdentifierExpr - no name lookup needed at runtime).
    ///
    /// Scope is at the same time an IOwner: object instances whose owner this scope
    /// is are destroyed in cascade in <see cref="Release"/> when the scope
    /// is left (end of block/function) - unless they were transferred beforehand via
    /// TakeUpwards/TakeGlobal/TakeTo or passed on to the parent
    /// scope via return (SPEC 2.3; the "return hands over ownership" behaviour
    /// is implemented by the evaluator by transferring, before the release of the function scope,
    /// the return value - if it is an object instance owned by this scope -
    /// via TakeUpwards to the calling scope).
    /// </summary>
    public sealed class Scope : IOwner
    {
        // Mutable only because of reuse (see Reinit/Recycle): after being left, a scope is handed out again from the VM's pool
        // and then gets a new parent.
        private Scope? _parent;
        public Scope? Parent => _parent;
        public bool IsGlobal { get; }

        // Deliberately NULL instead of pre-created empty arrays (see DefineSlot) - EVERY block execution (e.g. every single
        // loop iteration, see Compiler.CompileScopedBody: one EnterScope/ExitScope pair PER iteration) would otherwise need
        // a slot array, even if the block declares no local variable at all (the most common case with simple
        // loop bodies). Owned objects: see OwnedSet (the first one without a list object).
        // Slots as an array with a counter instead of List<Value>: a scope is created on EVERY call and every
        // loop iteration, and List<T> brings an extra object and version counter per instance.
        private Value[]? _slots;
        private int _slotCount;
        private OwnedSet _owned;
        // Arguments that a call received as the result of another call (`f(g())`, SPEC 2.1): they belong to the call and die last, after everything the called function created itself
        private OwnedSet _args;
        private List<IOwnedLeaf>? _leaves; // owned arrays and buffers (rare: mostly null)

        public Scope(Scope? parent, bool isGlobal = false)
        {
            _parent = parent;
            IsGlobal = isGlobal;
        }

        /// <summary>Scope with already occupied slots: `slots` now belongs to this scope, the first `count`
        /// entries are the parameters (the caller copied them directly from the stack, instead of distributing them via an
        /// intermediate array and individual DefineSlot calls); the rest is room for local variables.</summary>
        public Scope(Scope? parent, Value[] slots, int count)
        {
            _parent = parent;
            _slots = slots;
            _slotCount = count;
        }

        // -----------------------------------------------------------
        // Reuse (VM pool)
        //
        // Every block, every loop iteration and every call creates a scope - and the vast majority own neither objects nor
        // are they ever referenced from outside. The VM returns such scopes to a pool on leaving and hands them out again on the
        // next entry (including their slot array), instead of allocating two new objects every time.
        //
        // A scope is reusable only as long as NOTHING else can point to it:
        //  - it comes from the pool (`IsPooled`: only these scopes are returned, never the global one or ones created by other code),
        //  - it no longer owns an object (a destroyed object forgets its owner, see ObjectInstance.Destroy; a handed-on one
        //    long since has a new one),
        //  - no pointer points to one of its slots (`MarkEscaped`, set by ScopeSlotPointerTarget).
        // -----------------------------------------------------------
        private bool _pooled;
        private bool _escaped;

        /// <summary>A new scope for the VM's pool: returned on leaving (ExitScope/return) if it is still reusable then.</summary>
        public static Scope CreatePooled(Scope? parent) => new Scope(parent) { _pooled = true };

        /// <summary>Can this scope go back into the pool now (see above)?</summary>
        public bool CanRecycle => _pooled && !_escaped && _owned.IsEmpty && _args.IsEmpty && (_leaves == null || _leaves.Count == 0);

        /// <summary>A pointer to a slot of this scope exists (ScopeSlotPointerTarget): the scope must never be reused,
        /// otherwise the pointer would remain aimed at the variables of an entirely different block.</summary>
        public void MarkEscaped() => _escaped = true;

        /// <summary>Hands the scope out again (taken from the pool): new parent, empty.</summary>
        public void Reinit(Scope? parent)
        {
            _parent = parent;
            _pooled = true;
        }

        /// <summary>Like <see cref="Reinit"/> for a call: ensures a slot array with at least `capacity` places (the existing one is
        /// reused if it suffices) and occupies the first `paramCount` slots - the caller copies the parameters directly into it
        /// (see <see cref="SlotArray"/>).</summary>
        public void ReinitForCall(Scope? parent, int paramCount, int capacity)
        {
            _parent = parent;
            _pooled = true;
            if (_slots == null || _slots.Length < capacity) _slots = new Value[capacity];
            _slotCount = paramCount;
        }

        /// <summary>The raw slot array (for the VM only, directly after <see cref="ReinitForCall"/>: copy parameters into it).</summary>
        public Value[] SlotArray => _slots!;

        /// <summary>Cleans up a left scope for reuse: the values are forgotten (otherwise the pool would keep objects
        /// alive), parent and pool flag reset. Call only if <see cref="CanRecycle"/> holds.</summary>
        public void Recycle()
        {
            if (_slotCount > 0)
            {
                Array.Clear(_slots!, 0, _slotCount);
                _slotCount = 0;
            }
            _parent = null;
            _pooled = false;
        }

        // -----------------------------------------------------------
        // Slot-Zugriff
        // -----------------------------------------------------------

        /// <summary>Creates a new slot (the order must correspond exactly to
        /// the declaration order the resolver based itself on)
        /// and returns its index.</summary>
        public int DefineSlot(Value initialValue)
        {
            var slots = _slots;
            if (slots == null)
                _slots = slots = new Value[4];
            else if (_slotCount == slots.Length)
            {
                Array.Resize(ref _slots, slots.Length * 2);
                slots = _slots;
            }
            slots[_slotCount] = initialValue;
            return _slotCount++;
        }

        /// <summary>GetSlot/SetSlot are called only with an index that
        /// stems from a previous DefineSlot order (see
        /// Resolver/Compiler - the index is known for certain at compile time) -
        /// `_slots` is therefore guaranteed to be already occupied at this point.
        /// An index beyond the defined slots is a VM/compiler bug
        /// (see Values.VmInvariantViolationException docs) and throws as
        /// before.</summary>
        public Value GetSlot(int index)
        {
            if ((uint)index >= (uint)_slotCount) ThrowBadSlot(index);
            return _slots![index];
        }

        /// <summary>Reference to a slot (for the VM: a slot is copied directly onto the stack or overwritten from the
        /// stack, without intermediate copies). Same range check as GetSlot.</summary>
        public ref Value SlotRef(int index)
        {
            if ((uint)index >= (uint)_slotCount) ThrowBadSlot(index);
            return ref _slots![index];
        }

        public void SetSlot(int index, Value value)
        {
            if ((uint)index >= (uint)_slotCount) ThrowBadSlot(index);
            _slots![index] = value;
        }

        private void ThrowBadSlot(int index) =>
            throw new System.ArgumentOutOfRangeException(nameof(index), $"Slot {index} is not defined (slots: {_slotCount}).");

        /// <summary>Number of occupied slots - for debug/inspection purposes (see
        /// VM.DebugLocals), not needed by normal execution itself.</summary>
        public int SlotCount => _slotCount;

        /// <summary>Walks `depth` parent steps upward - depth corresponds exactly to
        /// what the resolver determined in ResolvedRef.Local(depth, slot).</summary>
        public Scope GetAncestor(int depth)
        {
            var scope = this;
            for (int i = 0; i < depth; i++)
                scope = scope.Parent!;
            return scope;
        }

        // -----------------------------------------------------------
        // IOwner
        // -----------------------------------------------------------
        public IReadOnlyList<ObjectInstance> OwnedObjects => _args.IsEmpty ? _owned.AsList() : new List<ObjectInstance>(_owned.AsList().Concat(_args.AsList())).AsReadOnly();
        /// <summary>Does this scope currently own objects? Otherwise leaving is a mere re-hooking of the parent pointer
        /// (see VM.Step, ExitScope).</summary>
        public bool HasOwned => !_owned.IsEmpty || !_args.IsEmpty || _leaves is { Count: > 0 };

        /// <summary>Set for the global scope of the main program as soon as a `fire` thread is running (see GlobalsBroker): every object
        /// that belongs to it - even one that only arises later - then belongs to the shared area (see
        /// ObjectInstance.MarkGlobalsDomain).</summary>
        public ThreadShareLock? SharingLock { get; set; }

        public void AddOwned(ObjectInstance obj)
        {
            _owned.Add(obj);
            if (SharingLock != null) obj.MarkGlobalsDomain(SharingLock);
        }
        public void RemoveOwned(ObjectInstance obj) { _owned.Remove(obj); if (!_args.IsEmpty) _args.Remove(obj); }

        /// <summary>An argument of the call that this scope owns (see <c>_args</c>).</summary>
        public void AddArgument(ObjectInstance obj)
        {
            _args.Add(obj);
            if (SharingLock != null) obj.MarkGlobalsDomain(SharingLock);
        }
        public void AddLeaf(IOwnedLeaf leaf) => (_leaves ??= new List<IOwnedLeaf>()).Add(leaf);
        public void RemoveLeaf(IOwnedLeaf leaf) => _leaves?.Remove(leaf);

        /// <summary>Like <see cref="Release"/>, but only for objects for which `filter` says yes - the others remain owned by this scope
        /// (for the end of a fire thread: its globals snapshots and `taking` copies are copies of objects of the
        /// main program and must not trigger destructors there, e.g. close a shared handle).</summary>
        public void ReleaseWhere(IDestructRunner runner, Func<ObjectInstance, bool> filter)
        {
            if (_owned.IsEmpty) return;   // (arrays and buffers remain: fire-thread snapshots are copies of the main program)
            var all = _owned.ToArray();
            DestroyBatch.Enter();
            try
            {
                foreach (var obj in all)
                {
                    if (filter(obj)) obj.Destroy(runner);
                    else obj.DestroyOwnedNonCopies(runner);   // (a copy stays, what the thread made inside of it does not)
                }
            }
            finally { DestroyBatch.Exit(); }
            _owned.RemoveDestroyed();
        }

        /// <summary>Called on leaving the scope: destroys in cascade all objects still owned by this scope.</summary>
        public void Release(IDestructRunner runner)
        {
            if (_owned.IsEmpty && _args.IsEmpty && _leaves == null) return;
            DestroyBatch.Enter();   // (what an array owns dies in the same batch as the objects of the scope)
            try
            {
                if (!_owned.IsEmpty) _owned.DestroyAll(runner);
                if (_leaves != null) LeafOwnership.DestroyAll(_leaves, runner);
                if (!_args.IsEmpty) _args.DestroyAll(runner);
            }
            finally { DestroyBatch.Exit(); } // (otherwise: nothing to do - the most common case with simple blocks/loop bodies)
        }
    }
}
