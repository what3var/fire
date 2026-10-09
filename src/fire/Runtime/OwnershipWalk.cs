using System;
using System.Collections.Generic;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>What `Take(...)`, `TakeUpwards(...)`, `TakeGlobal(...)`, `TakeTo(obj, ...)` take along besides the object itself (SPEC 2.2): the value of `Takes`.</summary>
    public static class Takes
    {
        /// <summary>Only the object on which the method is called (what it owns moves along anyway).</summary>
        public const int This = 0;
        /// <summary>The object and everything its fields point to directly; for an array and for everything that implements `IEnumerable`, its items (for an object via its enumerator). It owns them afterwards.</summary>
        public const int Children = 1;
        /// <summary>Like `return`: everything reachable that belongs to a scope of the running call (recursively); it moves to the object that points to it.</summary>
        public const int Locals = 2;
        /// <summary>Everything reachable (recursively), no matter who owns it.</summary>
        public const int All = 3;
    }

    /// <summary>The walk through the reference graph for `Takes` and for `return` (SPEC 2.2, 2.3): which objects, arrays and buffers hang behind a value and
    /// who owns them afterwards. Each node is visited only once (reference cycles are possible, ownership is a tree).</summary>
    public static class OwnershipWalk
    {
        /// <summary>Takes along what hangs behind <paramref name="root"/>, according to <paramref name="mode"/> (the caller has already given the root itself its new owner).
        /// A taken-along node afterwards belongs to the object that points to it (for an array: the owner of the array, if that is an object), otherwise
        /// <paramref name="fallback"/>. <paramref name="isLocalScope"/> says whether a scope belongs to the running call (`Takes.Locals`): local is what belongs to such a scope, even across other objects (which die along with it).</summary>
        public static void MoveReachable(Value root, int mode, Func<Scope, bool> isLocalScope, IOwner fallback, Func<ObjectInstance, IReadOnlyList<Value>?>? enumerate = null)
        {
            if (mode == Takes.This) return;
            object? rootNode = NodeOf(root);
            if (rootNode == null) return;

            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance) { rootNode };
            var work = new Stack<object>();
            work.Push(rootNode);
            var values = new List<Value>();
            while (work.Count > 0)
            {
                var node = work.Pop();
                // where taken-along things go: to the object that points to them; for an array to the object that owns the array, otherwise to the array itself (it can own)
                IOwner carrier = node switch
                {
                    ObjectInstance obj => obj,
                    ScriptArray array => (array.LeafOwner as ObjectInstance) ?? (IOwner)array,
                    _ => fallback,
                };
                values.Clear();
                // `Takes.Children` of an IEnumerable: its items, not its fields
                if (mode == Takes.Children && node is ObjectInstance enumerableObj && enumerate?.Invoke(enumerableObj) is { } items) values.AddRange(items);
                else CollectValues(node, values);
                foreach (var v in values)
                {
                    var child = NodeOf(v);
                    if (child == null || !visited.Add(child)) continue;
                    if (IsDestroyed(child)) continue;
                    var owner = OwnerOf(child);
                    bool move = mode switch
                    {
                        Takes.Children => true,
                        Takes.All => true,
                        _ => IsLocal(owner, isLocalScope),
                    };
                    bool descend = mode switch
                    {
                        Takes.Children => false,
                        Takes.All => true,
                        _ => move || (owner is { } parentOwner && visited.Contains(parentOwner)),
                    };
                    if (move && !ReferenceEquals(owner, carrier))
                    {
                        var target = carrier;
                        // the new owner must not hang below the node (ownership stays a tree)
                        if (child is ObjectInstance childObj && IsAncestor(childObj, target)) target = fallback;
                        Reparent(child, target);
                    }
                    if (descend) work.Push(child);
                }
            }
        }

        /// <summary>Does something belong (via the chain of owners) to a local scope?</summary>
        public static bool IsLocal(IOwner? owner, Func<Scope, bool> isLocalScope)
        {
            while (owner is ObjectInstance or ScriptArray) owner = owner is ObjectInstance oi ? oi.Owner : ((ScriptArray)owner).LeafOwner;
            return owner is Scope scope && isLocalScope(scope);
        }

        private static object? NodeOf(Value v) => v.Kind switch
        {
            ValueKind.Class => v.AsObjectRef() as ObjectInstance,
            ValueKind.Array => v.AsArray(),
            ValueKind.Buffer => v.AsBuffer(),
            _ => null,
        };

        private static IOwner? OwnerOf(object node) => node is ObjectInstance oi ? oi.Owner : (node as IOwnedLeaf)?.LeafOwner;

        private static bool IsDestroyed(object node) => node is ObjectInstance oi ? oi.IsDestroyed : (node is IOwnedLeaf leaf && leaf.IsDestroyed);

        private static void CollectValues(object node, List<Value> sink)
        {
            if (node is ObjectInstance oi) oi.Fields.AppendValues(sink);
            else if (node is ScriptArray array) sink.AddRange(array.Items);
        }

        private static void Reparent(object node, IOwner target)
        {
            if (node is ObjectInstance oi) oi.ReparentToOwner(target);
            else if (node is IOwnedLeaf leaf) LeafOwnership.Reparent(leaf, target);
        }

        /// <summary>`take x` (SPEC 2.2): the value (object, array, buffer) now belongs to <paramref name="holder"/> (scope, object or array) - unconditionally, no matter who owned it before.
        /// Everything else (number, text, ...) has no owner and stays unchanged. A destroyed thing stays where it is (the VM reports it beforehand).</summary>
        public static void TakeValue(Value v, IOwner holder, IDestructRunner runner)
        {
            switch (NodeOf(v))
            {
                case ObjectInstance obj:
                    if (obj.IsDestroyed) return;
                    if (ReferenceEquals(obj.Owner, holder)) return;
                    if (IsAncestor(obj, holder))
                        throw new OwnershipException("take: cycle detected - the holder is already owned (directly or transitively) by this object.");
                    if (holder is ObjectInstance target) obj.TakeTo(target, runner); else obj.ReparentToOwner(holder);
                    break;
                case IOwnedLeaf leaf:
                    if (leaf.IsDestroyed) return;
                    if (ReferenceEquals(leaf.LeafOwner, holder)) return;
                    if (leaf is ScriptArray arr)
                        for (IOwner? o = holder; o != null; o = o is ObjectInstance oi ? oi.Owner : (o as ScriptArray)?.LeafOwner)
                            if (ReferenceEquals(o, arr)) throw new OwnershipException("take: cycle detected - the holder is already owned (directly or transitively) by this array.");
                    if (holder is ObjectInstance targetObj) LeafOwnership.TakeTo(leaf, targetObj, runner); else LeafOwnership.Reparent(leaf, holder);
                    break;
            }
        }

        /// <summary><paramref name="ancestor"/> is in the ownership chain of <paramref name="node"/> (or is it itself).</summary>
        private static bool IsAncestor(ObjectInstance ancestor, IOwner node)
        {
            for (IOwner? o = node; o != null; o = o is ObjectInstance oi ? oi.Owner : (o as ScriptArray)?.LeafOwner)
                if (ReferenceEquals(o, ancestor)) return true;
            return false;
        }
    }
}
