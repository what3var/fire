using System;
using System.Collections.Generic;

namespace fire.Runtime
{
    /// <summary>
    /// Der "Besitzer" eines zerstörten Objekts (siehe ObjectInstance.Destroy): damit eine wiederverwendbare Scope nach dem Verlassen nicht
    /// von Objekten referenziert bleibt, die sie einmal besaßen. Besitzt selbst nie etwas.
    /// </summary>
    internal sealed class DeadOwner : IOwner
    {
        public static readonly DeadOwner Instance = new();
        private DeadOwner() { }

        public IReadOnlyList<ObjectInstance> OwnedObjects => Array.Empty<ObjectInstance>();
        public void AddOwned(ObjectInstance obj) => throw new OwnershipException("A destroyed object cannot own anything any more.");
        public void RemoveOwned(ObjectInstance obj) { }
        public void AddLeaf(IOwnedLeaf leaf) => throw new OwnershipException("A destroyed object cannot own anything any more.");
        public void RemoveLeaf(IOwnedLeaf leaf) { }
    }
}
