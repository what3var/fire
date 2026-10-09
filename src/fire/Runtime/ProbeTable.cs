using System;
using System.Collections.Generic;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>A probe (`probe obj.member changed/changing handler`, docs/DESIGN_LAMBDA_REFLECTION_PROBE.md): a handler for
    /// write accesses to a member (or all, `Member == null`) of an object.</summary>
    public sealed class ProbeEntry
    {
        public long Id { get; init; }

        /// <summary>Name of the member, null = all members.</summary>
        public string? Member { get; init; }

        /// <summary>`changing` (before writing, can abort with `false`) or `changed` (afterwards, only if the value changed).</summary>
        public bool IsChanging { get; init; }

        public LambdaValue Handler { get; init; } = null!;
    }

    /// <summary>The probes of ONE object (see <see cref="ObjectInstance.Probes"/>). Thread-safe: probes can be set and
    /// removed from any thread, firing happens on the writer's thread.</summary>
    public sealed class ProbeTable
    {
        private readonly object _gate = new();
        private readonly List<ProbeEntry> _entries = new();

        // Members whose handlers are currently running: a write to them from inside the handler does not fire again (no recursion)
        private readonly HashSet<string> _running = new();

        public bool IsEmpty { get { lock (_gate) return _entries.Count == 0; } }

        public void Add(ProbeEntry entry) { lock (_gate) _entries.Add(entry); }

        public bool Remove(long id)
        {
            lock (_gate) return _entries.RemoveAll(e => e.Id == id) > 0;
        }

        /// <summary>Removes all probes of this member (`member == null`: only those for all members). Returns the removed ids.</summary>
        public List<long> RemoveMember(string? member)
        {
            var ids = new List<long>();
            lock (_gate)
            {
                _entries.RemoveAll(e => { bool hit = e.Member == member; if (hit) ids.Add(e.Id); return hit; });
            }
            return ids;
        }

        /// <summary>Removes all probes of the object. Returns the removed ids.</summary>
        public List<long> RemoveAll()
        {
            lock (_gate)
            {
                var ids = _entries.ConvertAll(e => e.Id);
                _entries.Clear();
                return ids;
            }
        }

        /// <summary>Are there probes affecting a write to `member` (for this member or for all)?</summary>
        public bool Affects(string member)
        {
            lock (_gate)
                foreach (var e in _entries)
                    if (e.Member == null || e.Member == member) return true;
            return false;
        }

        /// <summary>Snapshot of the matching probes in the order of their registration.</summary>
        public List<ProbeEntry> Match(string member, bool changing)
        {
            var result = new List<ProbeEntry>();
            lock (_gate)
                foreach (var e in _entries)
                    if (e.IsChanging == changing && (e.Member == null || e.Member == member)) result.Add(e);
            return result;
        }

        public bool TryBeginRunning(string member) { lock (_gate) return _running.Add(member); }
        public void EndRunning(string member) { lock (_gate) _running.Remove(member); }
    }

    /// <summary>Which object belongs to which probe handle (`var h = probe ...; silence h`)? Weak, so that a forgotten handle does not keep an
    /// object alive.</summary>
    public static class ProbeRegistry
    {
        private static readonly Dictionary<long, WeakReference<ObjectInstance>> Owners = new();
        private static long _nextId;

        public static long NextId() => System.Threading.Interlocked.Increment(ref _nextId);

        public static void Register(long id, ObjectInstance owner)
        {
            lock (Owners) Owners[id] = new WeakReference<ObjectInstance>(owner);
        }

        public static void Forget(IEnumerable<long> ids)
        {
            lock (Owners) foreach (var id in ids) Owners.Remove(id);
        }

        public static ObjectInstance? OwnerOf(long id)
        {
            lock (Owners) return Owners.TryGetValue(id, out var weak) && weak.TryGetTarget(out var owner) ? owner : null;
        }
    }
}
