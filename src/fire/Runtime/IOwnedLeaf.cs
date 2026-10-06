using System;
using System.Collections.Generic;

namespace fire.Runtime
{
    /// <summary>
    /// Ein Wert, der wie ein Objekt genau einen Owner hat (SPEC 2), aber selbst nichts besitzt: ein Array oder ein Byte-Puffer. Ein Owner
    /// (Scope oder Objekt) zerstoert seine Blaetter beim Verlassen bzw. Zerstoeren; ein zerstoertes Blatt darf nicht mehr benutzt werden
    /// (in den Ausfuehrungsmodi Debug und Release ist das eine fangbare Ausnahme, im Performance-Modus ungeprueft).
    ///
    /// `Owner == null`: das Blatt wurde ausserhalb der VM erzeugt und hat keinen Owner - es wird nie zerstoert (die Speicherverwaltung
    /// uebernimmt .NET).
    /// </summary>
    public interface IOwnedLeaf
    {
        IOwner? LeafOwner { get; set; }
        bool IsDestroyed { get; }

        /// <summary>Markiert das Blatt als zerstoert (und laesst seine Teile mit zerstoeren). Nur vom Owner und von <see cref="LeafOwnership"/> aufzurufen.</summary>
        void MarkDestroyed(IDestructRunner runner);
    }

    /// <summary>Besitzwechsel und Zerstoerung der Blaetter (Arrays, Puffer) - das Gegenstueck zu den Methoden von <see cref="ObjectInstance"/>.</summary>
    public static class LeafOwnership
    {
        /// <summary>Das Blatt bekommt seinen ersten Owner (frisch erzeugt).</summary>
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

        /// <summary>`x.TakeUpwards()`: der Owner wird der Parent-Scope des bisherigen Owner-Scopes.</summary>
        public static void TakeUpwards(IOwnedLeaf leaf)
        {
            if (leaf.LeafOwner is not Scope scope)
                throw new OwnershipException("TakeUpwards is only valid if the current owner is a scope.");
            if (scope.Parent == null)
                throw new OwnershipException("TakeUpwards: the current scope has no parent scope (already global).");
            Reparent(leaf, scope.Parent);
        }

        /// <summary>`x.TakeTo(objekt)`: ist das Ziel schon in der Kaskadenloeschung, wird das Blatt sofort mit zerstoert (wie bei Objekten, SPEC 2.2).</summary>
        public static void TakeTo(IOwnedLeaf leaf, ObjectInstance target, IDestructRunner runner)
        {
            if (target.IsDestroyed) { Destroy(leaf, runner); return; }
            Reparent(leaf, target);
        }

        /// <summary>Ein im selben Ausdruck erzeugtes inneres Array gehoert dem aeusseren: es verlaesst seinen Scope und wird mit dem aeusseren zerstoert.</summary>
        public static void AttachPart(fire.Values.ScriptArray outer, IOwnedLeaf part)
        {
            part.LeafOwner?.RemoveLeaf(part);
            part.LeafOwner = null;
            (outer.Parts ??= new List<IOwnedLeaf>()).Add(part);
        }

        /// <summary>`delete x` / Ende des Owners: das Blatt gehoert niemandem mehr und ist zerstoert.</summary>
        public static void Destroy(IOwnedLeaf leaf, IDestructRunner runner)
        {
            if (leaf.IsDestroyed) return;
            leaf.LeafOwner?.RemoveLeaf(leaf);
            leaf.MarkDestroyed(runner);
        }

        /// <summary>Zerstoert alle Blaetter einer Liste (der Owner raeumt sie danach selbst auf).</summary>
        internal static void DestroyAll(List<IOwnedLeaf>? leaves, IDestructRunner runner)
        {
            if (leaves == null || leaves.Count == 0) return;
            var all = leaves.ToArray();
            leaves.Clear();
            foreach (var leaf in all) leaf.MarkDestroyed(runner);
        }
    }
}
