using System.Collections.Generic;

namespace fire.Runtime
{
    /// <summary>
    /// Everything that can be the owner of an object instance: a scope or another
    /// object instance (SPEC 2). Holds the list of currently owned objects, so that
    /// cascade deletion (on leaving a scope or on destroying an
    /// object) and cycle protection in TakeTo can access it.
    /// </summary>
    public interface IOwner
    {
        IReadOnlyList<ObjectInstance> OwnedObjects { get; }
        void AddOwned(ObjectInstance obj);
        void RemoveOwned(ObjectInstance obj);

        /// <summary>Arrays and buffers that belong to this owner (see <see cref="IOwnedLeaf"/>).</summary>
        void AddLeaf(IOwnedLeaf leaf);
        void RemoveLeaf(IOwnedLeaf leaf);
    }
}
