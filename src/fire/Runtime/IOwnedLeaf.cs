using System;
using System.Collections.Generic;

namespace fire.Runtime
{
    /// <summary>
    /// A value that, like an object, has exactly one owner (SPEC 2), but owns nothing itself: an array or a byte buffer. An owner
    /// (scope or object) destroys its leaves when it is left or destroyed; a destroyed leaf must not be used any more
    /// (in the Debug and Release execution modes this is a catchable exception, unchecked in performance mode).
    ///
    /// `Owner == null`: the leaf was created outside the VM and has no owner - it is never destroyed (memory management
    /// is taken over by .NET).
    /// </summary>
    public interface IOwnedLeaf
    {
        IOwner? LeafOwner { get; set; }
        bool IsDestroyed { get; }

        /// <summary>Marks the leaf as destroyed (and also destroys its parts). To be called only by the owner and by <see cref="LeafOwnership"/>.</summary>
        void MarkDestroyed(IDestructRunner runner);
    }

    /// <summary>Ownership change and destruction of leaves (arrays, buffers) - the counterpart to the methods of <see cref="ObjectInstance"/>.</summary>
    public static class LeafOwnership
    {
        /// <summary>The leaf gets its first owner (freshly created).</summary>
        public static void Adopt(IOwnedLeaf leaf, IOwner owner)
        {
            if (leaf.LeafOwner != null) { Reparent(leaf, owner); return; }
            leaf.LeafOwner = owner;
            owner.AddLeaf(leaf);
        }

        public static void Reparent(IOwnedLeaf leaf, IOwner newOwner)
        {
            if (leaf.IsDestroyed) throw new OwnershipException("A destroyed array or buffer cannot change its owner.");
            leaf.LeafOwner?.RemoveLeaf(leaf);
            leaf.LeafOwner = newOwner;
            newOwner.AddLeaf(leaf);
        }

        /// <summary>`x.TakeUpwards()`: the owner becomes the parent scope of the previous owner scope.</summary>
        public static void TakeUpwards(IOwnedLeaf leaf)
        {
            if (leaf.LeafOwner is not Scope scope)
                throw new OwnershipException("TakeUpwards is only valid if the current owner is a scope.");
            if (scope.Parent == null)
                throw new OwnershipException("TakeUpwards: the current scope has no parent scope (already global).");
            Reparent(leaf, scope.Parent);
        }

        /// <summary>`x.TakeTo(object)`: if the target is already in cascade deletion, the leaf is destroyed immediately as well (as with objects, SPEC 2.2).</summary>
        public static void TakeTo(IOwnedLeaf leaf, ObjectInstance target, IDestructRunner runner)
        {
            if (target.IsDestroyed) { Destroy(leaf, runner); return; }
            Reparent(leaf, target);
        }

        /// <summary>An inner array created in the same expression belongs to the outer one: it leaves its scope and is destroyed together with the outer one.</summary>
        public static void AttachPart(fire.Values.ScriptArray outer, IOwnedLeaf part)
        {
            part.LeafOwner?.RemoveLeaf(part);
            part.LeafOwner = null;
            (outer.Parts ??= new List<IOwnedLeaf>()).Add(part);
        }

        /// <summary>`delete x` / end of the owner: the leaf belongs to no one any more and is destroyed.</summary>
        public static void Destroy(IOwnedLeaf leaf, IDestructRunner runner)
        {
            if (leaf.IsDestroyed) return;
            leaf.LeafOwner?.RemoveLeaf(leaf);
            leaf.MarkDestroyed(runner);
        }

        /// <summary>Destroys all leaves of a list (the owner cleans them up itself afterwards).</summary>
        internal static void DestroyAll(List<IOwnedLeaf>? leaves, IDestructRunner runner)
        {
            if (leaves == null || leaves.Count == 0) return;
            var all = leaves.ToArray();
            leaves.Clear();
            foreach (var leaf in all) leaf.MarkDestroyed(runner);
        }
    }
}
