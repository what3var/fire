using System.Collections;
using System.Collections.Generic;
using fire.Bytecode;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>
    /// Field storage of an ObjectInstance (see ObjectInstance.Fields) -
    /// deliberately the same API surface as the raw
    /// `Dictionary&lt;string, Value&gt;` it used to be (indexer, TryGetValue, ContainsKey,
    /// enumerable as (name, Value) pairs), so that SyncEngine/ObjectCopier/
    /// UncaughtScriptException/PointerTargets/test code keep working
    /// UNCHANGED, without having to know anything about the internal
    /// layout themselves.
    ///
    /// Internally split in two:
    /// - An array for the fields declared and KNOWN at compile time
    ///   (fixed slot index via RuntimeClass.FieldIndex, computed ONCE per
    ///   class, not per instance - see there) - O(1) access
    ///   without hashing/string comparison, more compact than a dictionary entry
    ///   per field. This is the path that EVERY compiler-generated
    ///   field access takes (GetField/SetField/SetFieldOnThis/AddressOfField
    ///   ALWAYS emit only names of actually declared fields).
    /// - A (lazily created) dictionary fallback for everything else - really
    ///   reached only if no RuntimeClass is known at all (see
    ///   ObjectInstance constructor) or a field name is used that
    ///   was not declared (e.g. test code that constructs an ObjectInstance
    ///   directly and sets fields "on the fly" by name, without
    ///   going through the compiler - real, compiled scripts never do
    ///   that).
    /// </summary>
    public sealed class FieldStore : IEnumerable<KeyValuePair<string, Value>>
    {
        private readonly RuntimeClass? _rtClass;
        private readonly Value[] _known;
        private Dictionary<string, Value>? _extra;

        public FieldStore(RuntimeClass? rtClass)
        {
            _rtClass = rtClass;
            // (one slot per declared field of the whole chain: a field that a derived class declares again keeps the slot of the base class unused, see RuntimeClass.FieldIndex)
            _known = new Value[rtClass?.FlattenedFieldNames.Count ?? 0];

            // `default(Value)` is `false` (ValueKind.Bool = 0) - a declared field
            // that has not been assigned anything yet is, however, `undefined`. This becomes visible
            // for an object whose constructor aborts before the field
            // initialisers ran (e.g. an exception while evaluating the base(...)
            // arguments): its destruct() may then see `undefined`, not an
            // invented `false`.
            for (int i = 0; i < _known.Length; i++)
                _known[i] = Value.MakeUndefined();
        }

        public Value this[string name]
        {
            get
            {
                if (_rtClass != null && _rtClass.FieldIndex.TryGetValue(name, out int idx))
                    return _known[idx];
                if (_extra != null && _extra.TryGetValue(name, out var v))
                    return v;
                throw new KeyNotFoundException($"No field named '{name}'.");
            }
            set
            {
                if (_rtClass != null && _rtClass.FieldIndex.TryGetValue(name, out int idx))
                {
                    _known[idx] = value;
                    return;
                }
                (_extra ??= new Dictionary<string, Value>())[name] = value;
            }
        }

        /// <summary>Direct access to a DECLARED field via its index (see RuntimeClass.FieldIndex) -
        /// for the VM's inline caches, which already know the index.</summary>
        public Value GetAt(int index) => _known[index];
        public void SetAt(int index, Value value) => _known[index] = value;

        public bool TryGetValue(string name, out Value value)
        {
            if (_rtClass != null && _rtClass.FieldIndex.TryGetValue(name, out int idx))
            {
                value = _known[idx];
                return true;
            }
            if (_extra != null && _extra.TryGetValue(name, out value))
                return true;
            value = default;
            return false;
        }

        /// <summary>Appends the values of all fields (for the walk through the reference graph, see OwnershipWalk).</summary>
        internal void AppendValues(List<Value> sink)
        {
            sink.AddRange(_known);
            if (_extra != null) sink.AddRange(_extra.Values);
        }

        public bool ContainsKey(string name)
        {
            if (_rtClass != null && _rtClass.FieldIndex.ContainsKey(name)) return true;
            return _extra != null && _extra.ContainsKey(name);
        }

        public IEnumerator<KeyValuePair<string, Value>> GetEnumerator()
        {
            if (_rtClass != null)
            {
                var names = _rtClass.FlattenedFieldNames;
                for (int i = 0; i < names.Count; i++)
                    yield return new KeyValuePair<string, Value>(names[i], _known[i]);
            }
            if (_extra != null)
                foreach (var kv in _extra)
                    yield return kv;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
