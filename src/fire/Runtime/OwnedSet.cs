using System;
using System.Collections.Generic;

namespace fire.Runtime
{
    /// <summary>
    /// Die Objekte, die ein Owner (Scope oder Objektinstanz) gerade besitzt - in der Reihenfolge, in der sie dazukamen (in dieser
    /// Reihenfolge werden sie beim Verlassen auch zerstört).
    ///
    /// Das erste Objekt steht direkt in der Struktur, erst ab dem zweiten gibt es eine Liste: der allerhäufigste Fall ist genau EIN
    /// besessenes Objekt (`var q = new P()` in einem Block), und dafür sollen weder eine `List&lt;T&gt;` samt Array noch beim Zerstören
    /// eine Kopie (`ToArray`) angelegt werden. Eine veränderliche Struktur: nur als privates, nicht-readonly Feld verwenden.
    /// </summary>
    internal struct OwnedSet
    {
        private ObjectInstance? _first;
        private List<ObjectInstance>? _rest; // bleibt nach dem Leeren bestehen (ein wiederverwendeter Owner braucht sie wieder)

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

        /// <summary>Entfernt alle schon zerstörten Objekte (die übrigen behalten ihre Reihenfolge).</summary>
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

        /// <summary>Zerstört alle Objekte (in der Reihenfolge des Hinzukommens) und leert danach den Satz. Während der Zerstörung (Destruktoren)
        /// darf sich der Besitz ändern, deshalb wird bei mehreren Objekten mit einer Kopie gearbeitet; der Einzelfall braucht keine.</summary>
        public void DestroyAll(IDestructRunner runner)
        {
            var first = _first;
            if (first == null) return;
            if (_rest == null || _rest.Count == 0)
                first.Destroy(runner);
            else
                foreach (var obj in ToArray())
                    obj.Destroy(runner);
            Clear();
        }
    }
}
