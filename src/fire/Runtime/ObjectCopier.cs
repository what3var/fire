using System;
using System.Collections.Generic;
using fire.Bytecode;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>Thrown when `taking` (ObjectCopier.Take) reaches a value that cannot be copied for a thread: a lambda or a raw pointer. The spawner
    /// refuses those beforehand (ObjectCopier.RequireNoCopyFree, an ArgumentException), so this is only the safety net.</summary>
    public sealed class TakingViolationException : System.Exception
    {
        public TakingViolationException(string message) : base(message) { }
    }

    /// <summary>
    /// `taking X` (docs/spec/THREADING_DESIGN.md section 3): creates a complete, isolated deep copy of EVERYTHING reachable from X through fields and
    /// array elements (objects, arrays, buffers) for a fire thread - regardless of who owns the parts in the original (an object that X only refers
    /// to, an array literal assigned to a field, the objects in it). Every part is copied once (shared parts and cycles stay shared/cyclic in the copy);
    /// a copied object that belonged to another copied object belongs to that one's copy, every other copy (the root included) belongs to `newOwner`
    /// (the root scope of the new thread). Every copied object is marked IsTakingCopy (no destructor runs for it when the thread ends). The native
    /// runtime copies the same way (fire_rt.h takeCopy/DeepCopier).
    ///
    /// Activates thread sharing (ObjectInstance.ActivateThreadSharing) on the ORIGINAL root's tree - the basis of a later `sync`.
    /// A destroyed object or array stays a shared reference (there is nothing to copy). Lambdas and raw pointers cannot be copied: the
    /// spawner refuses them beforehand (<see cref="RequireNoCopyFree"/> with "taking", an ArgumentException); reaching one here anyway is a
    /// TakingViolationException.
    /// </summary>
    public static class ObjectCopier
    {
        /// <summary>The copy of `root` (see the class comment). Afterwards the copy carries `SyncOrigin == root` (see ObjectInstance.SyncOrigin docs),
        /// the basis for `sync`/`sync flat` (see SyncEngine).</summary>
        public static ObjectInstance Take(ObjectInstance root, IOwner newOwner)
        {
            var treeLock = new ThreadShareLock();
            root.ActivateThreadSharing(treeLock);

            var copy = (ObjectInstance)new Copier(newOwner).Run(Value.MakeClassRef(root)).AsObjectRef();
            copy.SyncOrigin = root;
            return copy;
        }

        /// <summary>What a `taking` value becomes in the thread: an object as <see cref="Take"/>, an array or a buffer as an isolated copy (with
        /// everything reachable from it, owned by `newOwner`), anything else as it is (value-like, strings, a lambda at the top level is shared).</summary>
        public static Value TakeValue(Value v, IOwner newOwner)
        {
            switch (v.Kind)
            {
                case ValueKind.Class:
                {
                    var obj = (ObjectInstance)v.AsObjectRef();
                    return obj.IsDestroyed ? v : Value.MakeClassRef(Take(obj, newOwner));
                }
                case ValueKind.Array:
                case ValueKind.Buffer:
                    return new Copier(newOwner).Run(v);
                default:
                    return v;
            }
        }

        /// <summary>Discovery first (everything reachable, without recursion on the C# stack), then all object copies, then their fields.</summary>
        private sealed class Copier
        {
            private readonly IOwner _owner;
            private readonly List<ObjectInstance> _order = new();
            private readonly Dictionary<ObjectInstance, List<KeyValuePair<string, Value>>> _found = new(ReferenceEqualityComparer.Instance);
            private readonly HashSet<object> _containersSeen = new(ReferenceEqualityComparer.Instance);
            private readonly Dictionary<ObjectInstance, ObjectInstance> _copies = new(ReferenceEqualityComparer.Instance);
            private readonly Dictionary<ScriptArray, ScriptArray> _arrayCopies = new(ReferenceEqualityComparer.Instance);
            private readonly Dictionary<ByteBuffer, ByteBuffer> _bufferCopies = new(ReferenceEqualityComparer.Instance);

            public Copier(IOwner owner) => _owner = owner;

            private object? _rootObj;

            public Value Run(Value root)
            {
                _rootObj = root.Kind == ValueKind.Class ? root.AsObjectRef() : null;
                Discover(root);
                foreach (var obj in _order) Materialize(obj);
                foreach (var obj in _order)
                {
                    var copy = _copies[obj];
                    foreach (var (name, value) in _found[obj])
                        copy.Fields[name] = Map(value);
                }
                return Map(root);
            }

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
                            if (obj.IsDestroyed || _found.ContainsKey(obj)) break;
                            if (IsActor(obj) && !ReferenceEquals(v.AsObjectRef(), _rootObj)) break;   // (an actor reached through a field: its reference goes along, like `fire with`)
                            var fields = FieldsOf(obj);
                            _found[obj] = fields;
                            _order.Add(obj);
                            foreach (var field in fields) pending.Push(field.Value);
                            break;
                        }
                        case ValueKind.Array:
                        {
                            var array = v.AsArray();
                            if (array.IsDestroyed || !_containersSeen.Add(array)) break;
                            foreach (var item in array.Items) pending.Push(item);
                            break;
                        }
                        case ValueKind.Lambda:
                            throw new TakingViolationException("'taking' rejected: lambda values cannot be copied for a thread.");
                        case ValueKind.Pointer:
                            throw new TakingViolationException("'taking' rejected: raw pointers cannot be copied for a thread.");
                    }
                }
            }

            /// <summary>The copy of `obj`: under the copy of its owner when that is copied too, otherwise under the thread's owner.</summary>
            private ObjectInstance Materialize(ObjectInstance obj)
            {
                if (_copies.TryGetValue(obj, out var existing)) return existing;
                // (the ownership tree has no cycles, so the recursion over the owners terminates)
                IOwner owner = obj.Owner is ObjectInstance ownerObj && _found.ContainsKey(ownerObj) ? Materialize(ownerObj) : _owner;
                var copy = new ObjectInstance(obj.ClassName, owner, obj.RtClass) { IsTakingCopy = true, TypeArgs = obj.TypeArgs };
                _copies[obj] = copy;
                return copy;
            }

            private Value Map(Value v)
            {
                switch (v.Kind)
                {
                    case ValueKind.Class:
                        return _copies.TryGetValue((ObjectInstance)v.AsObjectRef(), out var copy) ? Value.MakeClassRef(copy) : v;
                    case ValueKind.Array:
                    {
                        var array = v.AsArray();
                        if (array.IsDestroyed) return v;
                        if (_arrayCopies.TryGetValue(array, out var existing)) return Value.MakeArray(existing);
                        var arrayCopy = new ScriptArray(array.Length);
                        LeafOwnership.Adopt(arrayCopy, _owner);
                        arrayCopy.CopyTypeOf(array);   // (a copy of a typed array is typed)
                        _arrayCopies[array] = arrayCopy;   // (before filling: an array may contain itself)
                        for (int i = 0; i < array.Length; i++) arrayCopy.Items[i] = Map(array.Items[i]);
                        return Value.MakeArray(arrayCopy);
                    }
                    case ValueKind.Buffer:
                    {
                        var buffer = v.AsBuffer();
                        if (buffer.IsDestroyed) return v;
                        if (!_bufferCopies.TryGetValue(buffer, out var bufferCopy))
                        {
                            _bufferCopies[buffer] = bufferCopy = buffer.Clone();
                            LeafOwnership.Adopt(bufferCopy, _owner);
                        }
                        return Value.MakeBuffer(bufferCopy);
                    }
                    default:
                        return v;
                }
            }
        }

        /// <summary>`[NoCopy]` (SPEC 5.12) before a copy for another thread - `taking` (`op` "taking") and the arguments of `fire global` ("fire global"):
        /// an InvalidOperationException (the VM turns it into the ArgumentException) when an instance of a `[NoCopy]` class is reachable from `v` through
        /// fields and array elements - checked before anything is copied, queued or locked, so that no thread starts and no job runs. Walks like the
        /// discovery of the native DeepCopier (a stack, fields in their order; destroyed objects are skipped), so both engines name the same instance.
        /// An array is walked too (`taking` copies it). For `taking` a lambda or a raw pointer reached through a field or an element is refused the same way
        /// ("'taking' rejected: ..."): it cannot be copied for a thread (a lambda as the `taking` value itself is shared).</summary>
        public static void RequireNoCopyFree(Value v, string op)
        {
            if (v.Kind != ValueKind.Class && v.Kind != ValueKind.Array) return;
            object? root = v.Kind == ValueKind.Class ? v.AsObjectRef() : null;
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var pending = new Stack<Value>();
            pending.Push(v);
            while (pending.Count > 0)
            {
                var x = pending.Pop();
                if (op == "taking" && (x.Kind == ValueKind.Lambda || x.Kind == ValueKind.Pointer))
                    throw new InvalidOperationException(x.Kind == ValueKind.Lambda
                        ? "'taking' rejected: lambda values cannot be copied for a thread."
                        : "'taking' rejected: raw pointers cannot be copied for a thread.");
                if (x.Kind == ValueKind.Class)
                {
                    var obj = (ObjectInstance)x.AsObjectRef();
                    if (obj.IsDestroyed || !seen.Add(obj)) continue;
                    if (IsActor(obj) && !ReferenceEquals(obj, root)) continue;   // (an actor at depth is not copied: what it holds stays with it)
                    if (obj.RtClass?.IsNoCopy() == true)
                        throw new InvalidOperationException(RuntimeClass.NoCopyMessage(obj.ClassName, !ReferenceEquals(obj, root), op));
                    foreach (var (_, field) in FieldsOf(obj)) pending.Push(field);
                }
                else if (x.Kind == ValueKind.Array)
                {
                    var arr = (ScriptArray)x.AsArray();
                    if (arr.IsDestroyed || !seen.Add(arr)) continue;
                    for (int i = 0; i < arr.Length; i++) pending.Push(arr.Items[i]);
                }
            }
        }

        /// <summary>An actor: reached through a field or an element it is not copied - its reference goes along (THREADING_DESIGN 2: actor references may
        /// be passed around; the language reference: "Actors ... stay shared references"), as with ObjectCloner.</summary>
        private static bool IsActor(ObjectInstance obj) => obj.Mailbox != null || (obj.RtClass?.IsActor ?? false);

        /// <summary>The fields of `node`, read under its tree lock when it has one.</summary>
        internal static List<KeyValuePair<string, Value>> FieldsOf(ObjectInstance node)
        {
            var fields = new List<KeyValuePair<string, Value>>();
            if (node.ThreadLock != null)
            {
                node.ThreadLock.Enter();
                try { fields.AddRange(node.Fields); }
                finally { node.ThreadLock.Exit(); }
            }
            else fields.AddRange(node.Fields);
            return fields;
        }
    }
}
