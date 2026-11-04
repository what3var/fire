using System;
using System.Collections.Generic;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>
    /// `fire handover x` (docs/THREADING_DESIGN.md section 1): x goes to the new thread WITHOUT a copy - the counterpart of `taking` for what must not or
    /// cannot be duplicated (a `[NoCopy]` resource such as a NativeHandle). Like `taking`, it takes everything reachable from x through fields and array
    /// elements (and everything the objects own): those parts move as they are. A part whose owner moves too keeps its owner, every other part (x itself
    /// included) belongs to `target` - the root scope of the new thread - from then on, whoever owned it before (as `take` does). The parts leave the
    /// shared area of the globals (the thread changes them without a section). The sender's variable becomes `undefined` (the parser adds that
    /// assignment); other references the sender still holds point to objects of another thread now and must not be used. The native runtime moves the
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
        public static void RequireMovable(Value v, bool fromFireThread) => Discover(v, fromFireThread);

        /// <summary>Moves `v` with everything reachable from it to `target` (see the class comment) and returns it.</summary>
        public static Value MoveTo(Value v, IOwner target)
        {
            var parts = Discover(v, fromFireThread: false);
            if (parts == null) return v;
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
            }
            return v;
        }

        private sealed class Parts
        {
            public readonly List<ObjectInstance> Objects = new();
            public readonly List<IOwnedLeaf> Leaves = new();
            public readonly HashSet<object> Seen = new(ReferenceEqualityComparer.Instance);
            public bool Contains(object o) => Seen.Contains(o);
        }

        /// <summary>Everything that moves (null for a value without parts). Walks like the copy of `taking` (a stack, fields in their order), and also
        /// what the objects own.</summary>
        private static Parts? Discover(Value v, bool fromFireThread)
        {
            if (v.Kind != ValueKind.Class && v.Kind != ValueKind.Array && v.Kind != ValueKind.Buffer) return null;
            var parts = new Parts();
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
                        throw new InvalidOperationException("'handover' rejected: lambda values cannot be handed to a thread.");
                    case ValueKind.Pointer:
                        throw new InvalidOperationException("'handover' rejected: raw pointers cannot be handed to a thread.");
                }
            }
            return parts;
        }

        private static InvalidOperationException NotOwned() =>
            new("'handover' rejected: it reaches an object of the main program's global variables, which this thread does not own.");
    }
}
