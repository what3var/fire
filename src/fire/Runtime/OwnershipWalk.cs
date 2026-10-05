using System;
using System.Collections.Generic;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>Was `Take(...)`, `TakeUpwards(...)`, `TakeGlobal(...)`, `TakeTo(obj, ...)` außer dem Objekt selbst mitnehmen (SPEC 2.2): der Wert von `Takes`.</summary>
    public static class Takes
    {
        /// <summary>Nur das Objekt, auf dem die Methode aufgerufen wird (was es besitzt, wandert ohnehin mit).</summary>
        public const int This = 0;
        /// <summary>Das Objekt und alles, worauf seine Felder (bei einem Array: seine Elemente) unmittelbar zeigen; es besitzt sie danach.</summary>
        public const int Children = 1;
        /// <summary>Wie `return`: alles Erreichbare, was einem Scope des laufenden Aufrufs gehört (rekursiv); es wandert zu dem Objekt, das darauf zeigt.</summary>
        public const int Locals = 2;
        /// <summary>Alles Erreichbare (rekursiv), ganz gleich, wem es gehört.</summary>
        public const int All = 3;
    }

    /// <summary>Der Gang durch den Graphen der Verweise für `Takes` und für `return` (SPEC 2.2, 2.3): welche Objekte, Arrays und Puffer hinter einem Wert hängen und
    /// wem sie danach gehören. Jeder Knoten wird nur einmal besucht (Verweiszyklen sind möglich, der Besitz ist ein Baum).</summary>
    public static class OwnershipWalk
    {
        /// <summary>Nimmt, was hinter <paramref name="root"/> hängt, nach <paramref name="mode"/> mit (der Wurzel selbst hat der Aufrufer schon den neuen Owner gegeben).
        /// Ein mitgenommener Knoten gehört danach dem Objekt, das auf ihn zeigt (bei einem Array: dem Owner des Arrays, wenn der ein Objekt ist), sonst
        /// <paramref name="fallback"/>. <paramref name="isLocalScope"/> sagt, ob ein Scope zum laufenden Aufruf gehört (`Takes.Locals`): lokal ist, was einem solchen Scope gehört, auch über andere Objekte hinweg (die gleich mit ihm sterben).</summary>
        public static void MoveReachable(Value root, int mode, Func<Scope, bool> isLocalScope, IOwner fallback)
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
                IOwner carrier = (node as ObjectInstance) ?? ((node as IOwnedLeaf)?.LeafOwner as ObjectInstance) ?? fallback;
                values.Clear();
                CollectValues(node, values);
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
                        _ => move || (owner is ObjectInstance parentObj && visited.Contains(parentObj)),
                    };
                    if (move && !ReferenceEquals(owner, carrier))
                    {
                        var target = carrier;
                        // der neue Owner darf nicht unter dem Knoten hängen (der Besitz bleibt ein Baum)
                        if (child is ObjectInstance childObj && target is ObjectInstance targetObj && IsAncestor(childObj, targetObj)) target = fallback;
                        Reparent(child, target);
                    }
                    if (descend) work.Push(child);
                }
            }
        }

        /// <summary>Gehört etwas (über die Kette der Owner) einem lokalen Scope?</summary>
        public static bool IsLocal(IOwner? owner, Func<Scope, bool> isLocalScope)
        {
            while (owner is ObjectInstance oi) owner = oi.Owner;
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

        /// <summary><paramref name="ancestor"/> steht in der Besitzkette von <paramref name="node"/> (oder ist es selbst).</summary>
        private static bool IsAncestor(ObjectInstance ancestor, ObjectInstance node)
        {
            for (IOwner? o = node; o is ObjectInstance oi; o = oi.Owner)
                if (ReferenceEquals(oi, ancestor)) return true;
            return false;
        }
    }
}
