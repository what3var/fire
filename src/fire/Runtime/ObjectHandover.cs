using System;
using System.Collections.Generic;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>
    /// `fire handover x` (docs/spec/THREADING_DESIGN.md section 1): x goes to the new thread WITHOUT a copy - the counterpart of `taking` for what must not or
    /// cannot be duplicated (a `[NoCopy]` resource such as a NativeHandle). Like `taking`, it takes everything reachable from x through fields and array
    /// elements (and everything the objects own): those parts move as they are. A part whose owner moves too keeps its owner, every other part (x itself
    /// included) belongs to `target` - the root scope of the new thread - from then on, whoever owned it before (as `take` does). The parts leave the
    /// shared area of the globals (the thread changes them without a section). The sender's variable becomes `undefined` (the parser adds that
    /// assignment); every other reference that existed before (the sender's) points to a retired instance: its use is the DestroyedException
    /// "... was handed over to another thread" (see <see cref="MoveAll"/>). The native runtime moves the
    /// same way (fire_rt.h handOver).
    ///
    /// Actors and destroyed objects stay shared references (not moved). A lambda or a raw pointer reached through a field or an element cannot go to
    /// another thread: <see cref="RequireMovable"/> refuses it, as well as - in a fire thread - an object of the main program's globals.
    /// </summary>
    public static class ObjectHandover
    {
        /// <summary>Checked for every `handover` value before anything is copied or moved: an InvalidOperationException (the VM turns it into the
        /// ArgumentException) for a lambda or a pointer in a field or an element, and - in a fire thread - for a part that belongs to the shared area of
        /// the main program's globals (the thread does not own it). A `[NoCopy]` instance is fine (nothing is copied).</summary>
        public static void RequireMovable(Value v, bool fromFireThread) => Discover(v, fromFireThread, new Parts());

        /// <summary>Moves the `handover` values of one `fire` (`vs`) with everything reachable from them to `target` (see the class comment) and returns
        /// what the thread gets for them. With `retire` (not in performance mode) every moved part gets a new identity: a new instance takes its place
        /// (the references among the moved parts and the returned values point to the new ones), the old one is retired - every other reference
        /// (the sender's) is then a reference to a dead object, whose use is the DestroyedException "... was handed over to another thread"
        /// (VM.ThrowDestroyedObject). The native runtime does the same with the generations of the slots (fire_rt.h handOverAll).</summary>
        public static Value[] MoveAll(IReadOnlyList<Value> vs, IOwner target, bool retire = true, bool senderIsFireThread = false)
        {
            var result = new Value[vs.Count];
            for (int i = 0; i < vs.Count; i++) result[i] = vs[i];
            var parts = new Parts();
            foreach (var v in vs) Discover(v, fromFireThread: false, parts);
            if (parts.Objects.Count == 0 && parts.Leaves.Count == 0) return result;
            foreach (var obj in parts.Objects)
            {
                if (!(obj.Owner is ObjectInstance o && parts.Contains(o)) && !(obj.Owner is ScriptArray a && parts.Contains(a)))
                    obj.ReparentToOwner(target);
                obj.LeaveGlobalsDomain();
            }
            foreach (var leaf in parts.Leaves)
            {
                if (leaf.LeafOwner != null && !(leaf.LeafOwner is ObjectInstance o && parts.Contains(o)) && !(leaf.LeafOwner is ScriptArray a && parts.Contains(a)))
                    LeafOwnership.Reparent(leaf, target);
                if (leaf is ScriptArray array) array.IsShared = false;
                // a bound lambda: its target does not move with it (unless it is one of the parts) - its calls run where the target lives
                if (leaf is LambdaValue { Home: LambdaHome.Here, OnTarget: ObjectInstance boundTo } movedLambda && !parts.Contains(boundTo))
                    movedLambda.Home = boundTo.Mailbox != null || senderIsFireThread && !boundTo.InGlobalsDomain ? LambdaHome.OtherThread : LambdaHome.MainProgram;
            }
            if (!retire) return result;

            // the new identities: old part -> the instance that takes its place
            var fresh = new Dictionary<object, object>(ReferenceEqualityComparer.Instance);
            foreach (var obj in parts.Objects) fresh[obj] = obj.Relocate();
            foreach (var leaf in parts.Leaves)
                fresh[leaf] = leaf switch { ScriptArray a => a.Relocate(), ByteBuffer b => b.Relocate(), _ => leaf };
            Value Map(Value v) => v.Kind switch
            {
                ValueKind.Class when v.AsObjectRef() is ObjectInstance o && fresh.TryGetValue(o, out var n) => Value.MakeClassRef(n),
                ValueKind.Array when fresh.TryGetValue(v.AsArray(), out var n) => Value.MakeArray((ScriptArray)n),
                ValueKind.Buffer when fresh.TryGetValue(v.AsBuffer(), out var n) => Value.MakeBuffer((ByteBuffer)n),
                _ => v,
            };
            IOwner MapOwner(IOwner owner) => fresh.TryGetValue(owner, out var n) ? (IOwner)n : owner;
            // the owners hold the new instances in the places of the old ones; the references among the parts point to the new ones
            foreach (var obj in parts.Objects)
            {
                var n = (ObjectInstance)fresh[obj];
                var owner = MapOwner(obj.Owner);
                n.SetOwnerAfterHandover(owner);
                ReplaceIn(owner, obj, n);
                n.Fields.Rewrite(Map);
            }
            foreach (var leaf in parts.Leaves)
            {
                var n = (IOwnedLeaf)fresh[leaf];
                // (an inner part of a multi-dimensional array has no owner: its outer array holds it in Parts)
                if (leaf.LeafOwner != null)
                {
                    var owner = MapOwner(leaf.LeafOwner);
                    n.LeafOwner = owner;
                    ReplaceLeafIn(owner, leaf, n);
                }
                if (n is LambdaValue movedCaptures && movedCaptures.Captures != null)
                    for (int i = 0; i < movedCaptures.Captures.Length; i++) movedCaptures.Captures[i] = Map(movedCaptures.Captures[i]);
                if (n is ScriptArray array)
                {
                    for (int i = 0; i < array.Items.Length; i++) array.Items[i] = Map(array.Items[i]);
                    if (array.Parts != null)
                        for (int i = 0; i < array.Parts.Count; i++)
                            if (fresh.TryGetValue(array.Parts[i], out var p)) array.Parts[i] = (IOwnedLeaf)p;
                }
            }
            for (int i = 0; i < result.Length; i++) result[i] = Map(result[i]);
            foreach (var obj in parts.Objects) obj.RetireAfterHandover((ObjectInstance)fresh[obj]);
            foreach (var leaf in parts.Leaves)
            {
                if (leaf is ScriptArray a) a.RetireAfterHandover((ScriptArray)fresh[a]);
                else if (leaf is ByteBuffer b) b.RetireAfterHandover((ByteBuffer)fresh[b]);
            }
            return result;
        }

        private static void ReplaceIn(IOwner owner, ObjectInstance old, ObjectInstance fresh)
        {
            switch (owner)
            {
                case Scope scope: scope.ReplaceOwned(old, fresh); break;
                case ObjectInstance obj: obj.ReplaceOwned(old, fresh); break;
                case ScriptArray array: array.ReplaceOwned(old, fresh); break;
            }
        }

        private static void ReplaceLeafIn(IOwner owner, IOwnedLeaf old, IOwnedLeaf fresh)
        {
            switch (owner)
            {
                case Scope scope: scope.ReplaceLeaf(old, fresh); break;
                case ObjectInstance obj: obj.ReplaceLeaf(old, fresh); break;
                case ScriptArray array: array.ReplaceLeaf(old, fresh); break;
            }
        }

        private sealed class Parts
        {
            public readonly List<ObjectInstance> Objects = new();
            public readonly List<IOwnedLeaf> Leaves = new();
            public readonly HashSet<object> Seen = new(ReferenceEqualityComparer.Instance);
            public bool Contains(object o) => Seen.Contains(o);
        }

        /// <summary>Everything that moves (added to `parts`, which the values of one `fire` share). Walks like the copy of `taking` (a stack, fields in their order), and also
        /// what the objects own.</summary>
        private static void Discover(Value v, bool fromFireThread, Parts parts)
        {
            if (v.Kind != ValueKind.Class && v.Kind != ValueKind.Array && v.Kind != ValueKind.Buffer && v.Kind != ValueKind.Lambda) return;
            var pending = new Stack<Value>();
            pending.Push(v);
            while (pending.Count > 0)
            {
                var x = pending.Pop();
                switch (x.Kind)
                {
                    case ValueKind.Class:
                    {
                        var obj = (ObjectInstance)x.AsObjectRef();
                        if (obj.IsDestroyed || obj.Mailbox != null || (obj.RtClass?.IsActor ?? false) || !parts.Seen.Add(obj)) break;
                        if (fromFireThread && obj.InGlobalsDomain) throw NotOwned();
                        parts.Objects.Add(obj);
                        foreach (var (_, field) in ObjectCopier.FieldsOf(obj)) pending.Push(field);
                        foreach (var owned in obj.OwnedObjects) pending.Push(Value.MakeClassRef(owned));
                        break;
                    }
                    case ValueKind.Array:
                    {
                        var array = x.AsArray();
                        if (array.IsDestroyed || !parts.Seen.Add(array)) break;
                        if (fromFireThread && array.IsShared) throw NotOwned();
                        parts.Leaves.Add(array);
                        foreach (var item in array.Items) pending.Push(item);
                        break;
                    }
                    case ValueKind.Buffer:
                    {
                        var buffer = x.AsBuffer();
                        if (buffer.IsDestroyed || !parts.Seen.Add(buffer)) break;
                        parts.Leaves.Add(buffer);
                        break;
                    }
                    case ValueKind.Lambda:
                    {
                        // (it moves with its store; bound with `on`, its calls stay where its target lives - MoveAll sets its home)
                        var lambda = (LambdaValue)x.AsLambda();
                        if (lambda.IsDestroyed || !parts.Seen.Add(lambda)) break;
                        parts.Leaves.Add(lambda);
                        if (lambda.Captures != null) foreach (var c in lambda.Captures) pending.Push(c);
                        foreach (var owned in lambda.OwnedObjects) pending.Push(Value.MakeClassRef(owned));
                        break;
                    }
                    case ValueKind.Pointer:
                        throw new InvalidOperationException("'handover' rejected: raw pointers cannot be handed to a thread.");
                }
            }
        }

        private static InvalidOperationException NotOwned() =>
            new("'handover' rejected: it reaches an object of the main program's global variables, which this thread does not own.");
    }
}
