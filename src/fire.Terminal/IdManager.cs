using System.Collections.Generic;

namespace fire.Terminal
{
    /// <summary>
    /// Generic ID-based object manager - common basis for
    /// FramebufferManager/RendererManager (here) and WindowManager (see
    /// fire.Terminal.Windows). Assigns ASCENDING IDs that are unique within this
    /// ONE instance, starting at 1 - 0 deliberately stays reserved as
    /// "invalid/no ID", so that a forgotten or wrongly
    /// initialised id field (default value 0 in C#) does not by chance point to a
    /// real object. IDs are NEVER reused after Destroy()
    /// (the counter only runs forwards) - a handle that is passed on "too late" by the
    /// scripting language and has already been destroyed
    /// thus reliably points to nothing instead of accidentally to a NEW,
    /// different object of the same ID.
    /// </summary>
    public sealed class IdManager<T> where T : class
    {
        private readonly Dictionary<int, T> _items = new();
        private int _nextId = 1;

        public int Create(T item)
        {
            int id = _nextId++;
            _items[id] = item;
            return id;
        }

        /// <summary>Throws KeyNotFoundException for an unknown/already
        /// destroyed ID - the right choice for most call sites
        /// (a call with an invalid ID is a programming error of the
        /// caller, not a normal case to be tolerated silently).</summary>
        public T Get(int id) =>
            _items.TryGetValue(id, out var item)
                ? item
                : throw new KeyNotFoundException($"No resource with ID {id} (unknown or already destroyed).");

        public bool TryGet(int id, out T? item) => _items.TryGetValue(id, out item);

        /// <summary>Returns false (instead of throwing) if `id` does not (any longer)
        /// exist - destroying an already destroyed/unknown ID is
        /// deliberately NOT an error (makes idempotent clean-up easier).</summary>
        public bool Destroy(int id) => _items.Remove(id);

        public IEnumerable<int> Ids => _items.Keys;
    }
}
