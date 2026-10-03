using System;
using System.Collections.Generic;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>Eine Probe (`probe obj.member changed/changing handler`, docs/DESIGN_LAMBDA_REFLECTION_PROBE.md): ein Handler für
    /// Schreibzugriffe auf ein Mitglied (oder alle, `Member == null`) eines Objekts.</summary>
    public sealed class ProbeEntry
    {
        public long Id { get; init; }

        /// <summary>Name des Mitglieds, null = alle Mitglieder.</summary>
        public string? Member { get; init; }

        /// <summary>`changing` (vor dem Schreiben, kann mit `false` abbrechen) oder `changed` (danach, nur bei geändertem Wert).</summary>
        public bool IsChanging { get; init; }

        public LambdaValue Handler { get; init; } = null!;
    }

    /// <summary>Die Proben EINES Objekts (siehe <see cref="ObjectInstance.Probes"/>). Threadsicher: Proben können von jedem Thread gesetzt und
    /// entfernt werden, gefeuert wird auf dem Thread des Schreibers.</summary>
    public sealed class ProbeTable
    {
        private readonly object _gate = new();
        private readonly List<ProbeEntry> _entries = new();

        // Mitglieder, deren Handler gerade laufen: ein Schreiben darauf aus dem Handler heraus feuert nicht erneut (keine Rekursion)
        private readonly HashSet<string> _running = new();

        public bool IsEmpty { get { lock (_gate) return _entries.Count == 0; } }

        public void Add(ProbeEntry entry) { lock (_gate) _entries.Add(entry); }

        public bool Remove(long id)
        {
            lock (_gate) return _entries.RemoveAll(e => e.Id == id) > 0;
        }

        /// <summary>Entfernt alle Proben dieses Mitglieds (`member == null`: ausschließlich die für alle Mitglieder). Liefert die entfernten Ids.</summary>
        public List<long> RemoveMember(string? member)
        {
            var ids = new List<long>();
            lock (_gate)
            {
                _entries.RemoveAll(e => { bool hit = e.Member == member; if (hit) ids.Add(e.Id); return hit; });
            }
            return ids;
        }

        /// <summary>Entfernt alle Proben des Objekts. Liefert die entfernten Ids.</summary>
        public List<long> RemoveAll()
        {
            lock (_gate)
            {
                var ids = _entries.ConvertAll(e => e.Id);
                _entries.Clear();
                return ids;
            }
        }

        /// <summary>Gibt es Proben, die ein Schreiben auf `member` betreffen (für dieses Mitglied oder für alle)?</summary>
        public bool Affects(string member)
        {
            lock (_gate)
                foreach (var e in _entries)
                    if (e.Member == null || e.Member == member) return true;
            return false;
        }

        /// <summary>Momentaufnahme der passenden Proben in der Reihenfolge ihrer Anmeldung.</summary>
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

    /// <summary>Welches Objekt gehört zu welchem Probe-Handle (`var h = probe ...; silence h`)? Schwach, damit ein vergessenes Handle ein
    /// Objekt nicht am Leben hält.</summary>
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
