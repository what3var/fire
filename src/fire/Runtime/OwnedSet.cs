using System;
using System.Collections.Generic;

namespace fire.Runtime
{
    /// <summary>
    /// The objects an owner (scope or object instance) currently owns - in the order in which they were added (in this
    /// order they are also destroyed on leaving).
    ///
    /// The first object sits directly in the struct, only from the second on is there a list: by far the most common case is exactly ONE
    /// owned object (`var q = new P()` in a block), and for that neither a `List&lt;T&gt;` with its array nor, on destroying,
    /// a copy (`ToArray`) should be allocated. A mutable struct: use only as a private, non-readonly field.
    /// </summary>
    internal struct OwnedSet
    {
        private ObjectInstance? _first;
        private List<ObjectInstance>? _rest; // remains after being emptied (a reused owner needs it again)

        public readonly bool IsEmpty => _first == null;
        public readonly int Count => _first == null ? 0 : 1 + (_rest?.Count ?? 0);

        public readonly ObjectInstance this[int index] => index == 0 ? _first! : _rest![index - 1];

        public void Add(ObjectInstance obj)
        {
            if (_first == null) _first = obj;
            else (_rest ??= new List<ObjectInstance>()).Add(obj);
        }

        public void Remove(ObjectInstance obj)
        {
            if (_first == null) return;
            if (ReferenceEquals(_first, obj))
            {
                if (_rest is { Count: > 0 })
                {
                    _first = _rest[0];
                    _rest.RemoveAt(0);
                }
                else _first = null;
                return;
            }
            _rest?.Remove(obj);
        }

        public void Clear()
        {
            _first = null;
            _rest?.Clear();
        }

        /// <summary>Removes all already destroyed objects (the others keep their order).</summary>
        public void RemoveDestroyed()
        {
            var all = ToArray();
            Clear();
            foreach (var obj in all)
                if (!obj.IsDestroyed) Add(obj);
        }

        public readonly ObjectInstance[] ToArray()
        {
            if (_first == null) return Array.Empty<ObjectInstance>();
            var result = new ObjectInstance[Count];
            result[0] = _first;
            _rest?.CopyTo(result, 1);
            return result;
        }

        public readonly IReadOnlyList<ObjectInstance> AsList() => _first == null ? Array.Empty<ObjectInstance>() : ToArray();

        /// <summary>Destroys all objects (in the order they were added) and then empties the set. During destruction (destructors)
        /// ownership may change, therefore with several objects a copy is worked on; the single case needs none.</summary>
        public void DestroyAll(IDestructRunner runner)
        {
            var first = _first;
            if (first == null) return;
            DestroyBatch.Enter();
            try
            {
                if (_rest == null || _rest.Count == 0)
                    first.Destroy(runner);
                else
                    foreach (var obj in ToArray())
                        obj.Destroy(runner);
            }
            finally { DestroyBatch.Exit(); }
            Clear();
        }
    }
}
