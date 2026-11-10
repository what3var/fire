using System;
using System.Collections.Generic;
using fire.Bytecode;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>
    /// The copy prefixes `flat x` and `copy x` (SPEC 2.4). Unlike <see cref="ObjectCopier"/> (`taking`, isolated
    /// copy for a thread that rejects every reference pointing outward), this is an ordinary value expression: the result
    /// is a new object that, like any new object, gets an owner (SPEC 2.1) and is destroyed with it.
    ///
    /// <b>flat</b>: the object itself is copied, including its fields - value-like ones (bool/int/float/char/string/undefined)
    /// as a value, everything reference-like (objects, arrays, buffers, lambdas, pointers) remains the same reference as in the
    /// original. The operand may also be an array (new array, same elements) or a buffer (byte copy).
    ///
    /// <b>copy</b>: deep copy. Every instance reachable from the operand via fields/array elements is copied exactly ONCE;
    /// if the same instance (or the same array) shows up again - even cyclically - the copy points to the
    /// copy already made (identity table). Ownership relations are preserved: if a copied instance belonged in the
    /// original to an instance that is also copied, its copy belongs to that one's copy; everything else (root, and instances
    /// whose owner is not copied along) belongs to the passed owner.
    ///
    /// For both: constructors do not run (only field values are transferred), the destructor of the copy runs like
    /// for any object. Actors are never copied (an actor as operand is an error, an actor at depth remains
    /// a shared reference - actor references are meant for passing around anyway, THREADING_DESIGN 2), likewise
    /// an already destroyed object at depth remains a shared reference. Pointers are shared; a lambda is copied with its store (CopyLambda). An instance of a `[NoCopy]` class (SPEC 5.12)
    /// is never duplicated: as the operand of `flat`/`copy`, or reached by `copy`, it is an error (`flat` keeps the references of the fields, so a field
    /// holding one is fine there).
    /// </summary>
    public static class ObjectCloner
    {
        /// <summary>A copy of a lambda (ROADMAP_TASKS step 8): a new lambda with the same body and target that belongs to <paramref name="owner"/> and has
        /// a store of its own - the captures copied (`copy`: deeply, into the new lambda; `flat`: the same values).</summary>
        public static LambdaValue CopyLambda(LambdaValue lambda, IOwner owner, bool deep)
        {
            var copy = lambda.WithOnTarget(lambda.OnTarget);
            LeafOwnership.Adopt(copy, owner);
            if (deep && copy.Captures != null)
                for (int i = 0; i < copy.Captures.Length; i++) copy.Captures[i] = Clone(copy.Captures[i], copy, deep: true);
            return copy;
        }

        public static Value Clone(Value source, IOwner owner, bool deep)
        {
            switch (source.Kind)
            {
                case ValueKind.Class:
                {
                    var obj = (ObjectInstance)source.AsObjectRef();
                    RequireCopyableRoot(obj);
                    return Value.MakeClassRef(deep ? new DeepCopier(owner, obj).Run() : FlatCopy(obj, owner));
                }

                case ValueKind.Array:
                {
                    var array = source.AsArray();
                    if (deep) return new DeepCopier(owner, null).RunOnContainer(source);
                    var items = new ScriptArray(array.Length);
                    Array.Copy(array.Items, items.Items, array.Length);
                    LeafOwnership.Adopt(items, owner);
                    return Value.MakeArray(items);
                }

                case ValueKind.Buffer:
                {
                    var copy = source.AsBuffer().Clone();
                    LeafOwnership.Adopt(copy, owner);
                    return Value.MakeBuffer(copy);
                }

                case ValueKind.Lambda when source.AsLambda() is LambdaValue lambda:
                    return Value.MakeLambda(CopyLambda(lambda, owner, deep));

                default:
                    // Value-like (bool/int/float/char/string/undefined) and references without content of their own
                    // (lambda/pointer): there is nothing to copy.
                    return source;
            }
        }

        /// <summary>Like <see cref="Clone"/>, but for a copy that is to belong to an OBJECT (assignment to a field, SPEC 2.4) -
        /// the handover behaves like `TakeTo` (SPEC 2.2): if the target object is already in cascade deletion, the
        /// copy is treated as if it had been handed over one second BEFORE its start - it is destroyed immediately along with it (including
        /// `destruct()`). (There cannot be a cycle: the copy is new and owns nothing that the target already owned.)</summary>
        public static Value CloneOwnedBy(Value source, ObjectInstance owner, bool deep, IDestructRunner runner)
        {
            if (!owner.IsDestroyed)
                return Clone(source, owner, deep);

            var scratch = new Scope(null);
            var result = Clone(source, scratch, deep);
            scratch.Release(runner);
            return result;
        }

        private static void RequireCopyableRoot(ObjectInstance obj)
        {
            if (obj.IsDestroyed)
                throw new InvalidOperationException(
                    "An already destroyed object cannot be copied.");
            if (IsActor(obj))
                throw new InvalidOperationException(
                    $"An actor ('{obj.ClassName}') cannot be copied - actor references are shared.");
            RequireCopyable(obj, nested: false);
        }

        /// <summary>`[NoCopy]` (SPEC 5.12, inherited): the instance is never duplicated - an ArgumentException (the VM turns the InvalidOperationException into it)
        /// before anything is copied.</summary>
        private static void RequireCopyable(ObjectInstance obj, bool nested)
        {
            if (obj.RtClass?.IsNoCopy() == true)
                throw new InvalidOperationException(RuntimeClass.NoCopyMessage(obj.ClassName, nested));
        }

        private static bool IsActor(ObjectInstance obj) => obj.Mailbox != null || (obj.RtClass?.IsActor ?? false);

        private static ObjectInstance FlatCopy(ObjectInstance node, IOwner owner)
        {
            var copy = new ObjectInstance(node.ClassName, owner, node.RtClass) { TypeArgs = node.TypeArgs };
            foreach (var (name, value) in Snapshot(node))
                copy.Fields[name] = value;
            return copy;
        }

        /// <summary>The fields read under the tree lock (an object attached to a `taking` thread can
        /// be modified from there at the same time).</summary>
        private static List<KeyValuePair<string, Value>> Snapshot(ObjectInstance node)
        {
            var fields = new List<KeyValuePair<string, Value>>();
            if (node.ThreadLock != null)
            {
                node.ThreadLock.Enter();
                try { fields.AddRange(node.Fields); }
                finally { node.ThreadLock.Exit(); }
            }
            else
            {
                fields.AddRange(node.Fields);
            }
            return fields;
        }

        private sealed class DeepCopier
        {
            private readonly IOwner _rootOwner;
            private readonly ObjectInstance? _root;

            // Phase 1 (discover): all instances to copy with their field snapshots, in discovery order.
            private readonly List<ObjectInstance> _order = new();
            private readonly Dictionary<ObjectInstance, List<KeyValuePair<string, Value>>> _found = new(ReferenceEqualityComparer.Instance);
            private readonly HashSet<object> _containersSeen = new(ReferenceEqualityComparer.Instance);

            // Phase 2/3 (create/fill): original -> copy (identity tables).
            private readonly Dictionary<ObjectInstance, ObjectInstance> _copies = new(ReferenceEqualityComparer.Instance);
            private readonly Dictionary<ScriptArray, ScriptArray> _arrayCopies = new(ReferenceEqualityComparer.Instance);
            private readonly Dictionary<ByteBuffer, ByteBuffer> _bufferCopies = new(ReferenceEqualityComparer.Instance);

            public DeepCopier(IOwner rootOwner, ObjectInstance? root)
            {
                _rootOwner = rootOwner;
                _root = root;
            }

            public ObjectInstance Run()
            {
                Discover(Value.MakeClassRef(_root!));
                Materialize();
                FillFields();
                return _copies[_root!];
            }

            /// <summary>An array as operand: it has no root instance, all objects found in it end up at the owner
            /// under the usual rules.</summary>
            public Value RunOnContainer(Value container)
            {
                Discover(container);
                Materialize();
                FillFields();
                return MapValue(container);
            }

            /// <summary>Collects everything reachable (objects, arrays) without recursion on the C# stack.</summary>
            private void Discover(Value start)
            {
                var pending = new Stack<Value>();
                pending.Push(start);
                while (pending.Count > 0)
                {
                    var v = pending.Pop();
                    switch (v.Kind)
                    {
                        case ValueKind.Class:
                        {
                            var obj = (ObjectInstance)v.AsObjectRef();
                            if (_found.ContainsKey(obj)) break;
                            // Actors and destroyed objects remain shared references (only the root is checked up front).
                            if (!ReferenceEquals(obj, _root) && (IsActor(obj) || obj.IsDestroyed)) break;
                            RequireCopyable(obj, nested: !ReferenceEquals(obj, _root));   // (still nothing copied: discovery comes first)
                            var snapshot = Snapshot(obj);
                            _found[obj] = snapshot;
                            _order.Add(obj);
                            foreach (var field in snapshot) pending.Push(field.Value);
                            break;
                        }
                        case ValueKind.Array:
                        {
                            var array = v.AsArray();
                            if (!_containersSeen.Add(array)) break;
                            foreach (var item in array.Items) pending.Push(item);
                            break;
                        }
                    }
                }
            }

            /// <summary>Creates all copies (still without field values) - the root first, then each instance under its
            /// copied owner, if that is copied along, otherwise under the root owner.</summary>
            private void Materialize()
            {
                if (_root != null)
                    _copies[_root] = new ObjectInstance(_root.ClassName, _rootOwner, _root.RtClass) { TypeArgs = _root.TypeArgs };
                foreach (var obj in _order)
                    MaterializeOne(obj);
            }

            private ObjectInstance MaterializeOne(ObjectInstance obj)
            {
                if (_copies.TryGetValue(obj, out var existing)) return existing;
                // The ownership tree has no cycles (TakeTo prevents them), so the recursion over the owners terminates.
                IOwner owner = obj.Owner is ObjectInstance ownerObj && _found.ContainsKey(ownerObj)
                    ? MaterializeOne(ownerObj)
                    : _rootOwner;
                var copy = new ObjectInstance(obj.ClassName, owner, obj.RtClass) { TypeArgs = obj.TypeArgs };
                _copies[obj] = copy;
                return copy;
            }

            private void FillFields()
            {
                foreach (var obj in _order)
                {
                    var copy = _copies[obj];
                    foreach (var (name, value) in _found[obj])
                        copy.Fields[name] = MapValue(value);
                }
            }

            private Value MapValue(Value v)
            {
                switch (v.Kind)
                {
                    case ValueKind.Class:
                    {
                        var obj = (ObjectInstance)v.AsObjectRef();
                        return _copies.TryGetValue(obj, out var copy) ? Value.MakeClassRef(copy) : v;
                    }

                    case ValueKind.Array:
                    {
                        var array = v.AsArray();
                        if (_arrayCopies.TryGetValue(array, out var existing)) return Value.MakeArray(existing);
                        var copy = new ScriptArray(array.Length);
                        copy.CopyTypeOf(array);   // (a copy of a typed array is typed)
                        LeafOwnership.Adopt(copy, _rootOwner);   // every copied array belongs to the owner of the copy (elements never change their owner)
                        _arrayCopies[array] = copy; // register before filling: an array may contain itself
                        for (int i = 0; i < array.Length; i++)
                            copy.Items[i] = MapValue(array.Items[i]);
                        return Value.MakeArray(copy);
                    }

                    case ValueKind.Buffer:
                    {
                        var buffer = v.AsBuffer();
                        if (!_bufferCopies.TryGetValue(buffer, out var copy))
                        {
                            _bufferCopies[buffer] = copy = buffer.Clone();
                            LeafOwnership.Adopt(copy, _rootOwner);
                        }
                        return Value.MakeBuffer(copy);
                    }

                    case ValueKind.Lambda when v.AsLambda() is LambdaValue { IsDestroyed: false } lambda:
                        return Value.MakeLambda(CopyLambda(lambda, _rootOwner, deep: true));   // (a lambda at depth: a copy with its store, like an array)

                    default:
                        return v;
                }
            }
        }
    }
}
