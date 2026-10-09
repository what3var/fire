using System.Collections.Generic;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>Thrown when `taking` (ObjectCopier.Take) finds a reference
    /// that cannot be safely copied in isolation - either because
    /// it POINTS OUT of the copied ownership tree (see
    /// docs/THREADING_DESIGN.md section 3: "hard rule"), or because it is
    /// a kind of value that this stage does not support at all yet
    /// (lambda, pointer - see ObjectCopier class comment).</summary>
    public sealed class TakingViolationException : System.Exception
    {
        public TakingViolationException(string message) : base(message) { }
    }

    /// <summary>
    /// `taking X` (docs/THREADING_DESIGN.md section 3): creates a
    /// complete, isolated deep copy of X's entire OWN
    /// ownership tree for a fire thread. In doing so activates thread sharing
    /// (ObjectInstance.ActivateThreadSharing) on the ORIGINAL tree - from
    /// this point on EVERY field access to EVERY node in
    /// this tree respects the shared tree lock, including normal accesses from the
    /// owning (originating) thread itself (see ThreadShareLock docs).
    ///
    /// DELIBERATELY NOT supported in this first stage (throws
    /// TakingViolationException instead of producing an unsafe/incorrect copy):
    /// erzeugen):
    /// - References (object fields) that POINT OUT of the copied tree
    ///   (to an independently owned object) - that would be a hidden
    ///   shared-memory hole, exactly what `taking` is meant to prevent.
    /// - Lambda values anywhere in the reachable graph - a correct copy
    ///   would, for a bound `this` (`on obj`), have to remap its reference to the newly
    ///   created COPY of that object (two-step procedure:
    ///   first copy the whole object graph, then in a second
    ///   pass rewire all lambda bindings afterwards) - this is
    ///   a sensible extension for a later stage, deliberately deferred here for
    ///   time/risk reasons.
    /// - Raw pointers (`unsafe`/`&`) anywhere in the reachable graph - a
    ///   pointer points to a concrete, managed storage location (scope
    ///   slot or object field, see PointerTarget docs), for which there is no
    ///   sensible "copy" without re-resolving the target address itself
    ///   - regardless of whether it points into the own tree or out of it.
    /// </summary>
    public static class ObjectCopier
    {
        /// <summary>Creates the isolated copy of `root`'s ownership tree,
        /// with `newOwner` as the owner of the copy (typically a scope in the
        /// new fire thread). Afterwards the copy carries `SyncOrigin == root`
        /// (see ObjectInstance.SyncOrigin docs), the basis for `sync`/
        /// `sync flat` (see SyncEngine).</summary>
        public static ObjectInstance Take(ObjectInstance root, IOwner newOwner)
        {
            var treeLock = new ThreadShareLock();
            root.ActivateThreadSharing(treeLock);

            var map = new Dictionary<ObjectInstance, ObjectInstance>(ReferenceEqualityComparer.Instance);
            var copy = CopyNode(root, root, newOwner, map);
            copy.SyncOrigin = root;
            return copy;
        }

        private static ObjectInstance CopyNode(
            ObjectInstance node, ObjectInstance treeRoot, IOwner copyOwner,
            Dictionary<ObjectInstance, ObjectInstance> map)
        {
            if (map.TryGetValue(node, out var existing)) return existing;

            var copy = new ObjectInstance(node.ClassName, copyOwner, node.RtClass) { IsTakingCopy = true };
            map[node] = copy;

            // Read under the tree lock (since ActivateThreadSharing, node can
            // theoretically already be observed by another concurrently running thread
            // if root itself was already the target of
            // taking before - a nested taking on an already
            // checked-out subtree).
            var fieldsSnapshot = new List<KeyValuePair<string, Value>>();
            if (node.ThreadLock != null)
            {
                node.ThreadLock.Enter();
                try { fieldsSnapshot.AddRange(node.Fields); }
                finally { node.ThreadLock.Exit(); }
            }
            else
            {
                fieldsSnapshot.AddRange(node.Fields);
            }

            foreach (var (name, value) in fieldsSnapshot)
                copy.Fields[name] = CopyValue(value, treeRoot, copyOwner, map);

            return copy;
        }

        private static Value CopyValue(
            Value v, ObjectInstance treeRoot, IOwner copyOwner,
            Dictionary<ObjectInstance, ObjectInstance> map)
        {
            switch (v.Kind)
            {
                case ValueKind.Class:
                {
                    var target = (ObjectInstance)v.AsObjectRef();
                    if (!IsWithinTree(target, treeRoot))
                        throw new TakingViolationException(
                            $"'taking' rejected: a field refers to an instance of " +
                            $"'{target.ClassName}', which is not part of its own ownership tree.");
                    var childCopy = CopyNode(target, treeRoot, copyOwner, map);
                    return Value.MakeClassRef(childCopy);
                }

                case ValueKind.Array:
                {
                    var arr = (ScriptArray)v.AsArray();
                    var newArr = new ScriptArray(arr.Length);
                    for (int i = 0; i < arr.Length; i++)
                        newArr.Items[i] = CopyValue(arr.Items[i], treeRoot, copyOwner, map);
                    return Value.MakeArray(newArr);
                }

                case ValueKind.Lambda:
                    throw new TakingViolationException(
                        "'taking' rejected: lambda values cannot be " +
                        "copied at this stage (see the ObjectCopier class comment).");

                case ValueKind.Pointer:
                    throw new TakingViolationException(
                        "'taking' rejected: raw pointers cannot be " +
                        "copied at this stage (see the ObjectCopier class comment).");

                default:
                    // bool/int/float/char/string/undefined - value-like, directly copyable.
                    return v;
            }
        }

        /// <summary>`node` belongs to `treeRoot`'s own ownership tree if it
        /// either is the root itself or is transitively owned by it via the owner chain
        /// (ObjectInstance.IsTransitivelyOwnedBy).</summary>
        private static bool IsWithinTree(ObjectInstance node, ObjectInstance treeRoot) =>
            ReferenceEquals(node, treeRoot) || node.IsTransitivelyOwnedBy(treeRoot);
    }
}
