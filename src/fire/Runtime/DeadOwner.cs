using System;
using System.Collections.Generic;

namespace fire.Runtime
{
    /// <summary>
    /// The "owner" of a destroyed object (see ObjectInstance.Destroy): so that a reusable scope is not left referenced after being left
    /// by objects it once owned. Itself never owns anything.
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
