using System;
using System.Collections.Generic;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>
    /// Die Kopier-Präfixe `flat x` und `copy x` (SPEC 2.4). Anders als <see cref="ObjectCopier"/> (`taking`, isolierte
    /// Kopie für einen Thread, die jede Referenz nach außen ablehnt) ist das ein gewöhnlicher Wertausdruck: das Ergebnis
    /// ist ein neues Objekt, das wie jedes neue Objekt einen Owner bekommt (SPEC 2.1) und mit ihm zerstört wird.
    ///
    /// <b>flat</b>: das Objekt selbst wird kopiert, samt seiner Felder - wertartige (bool/int/float/char/string/undefined)
    /// als Wert, alles Referenzartige (Objekte, Arrays, Puffer, Lambdas, Pointer) bleibt dieselbe Referenz wie im
    /// Original. Der Operand darf auch ein Array (neues Array, gleiche Elemente) oder ein Puffer (Byte-Kopie) sein.
    ///
    /// <b>copy</b>: Tiefenkopie. Jede vom Operanden aus über Felder/Array-Elemente erreichbare Instanz wird genau EINMAL
    /// kopiert; taucht dieselbe Instanz (oder dasselbe Array) noch einmal auf - auch zyklisch -, zeigt die Kopie auf die
    /// schon gemachte Kopie (Identitätstabelle). Die Besitzverhältnisse bleiben erhalten: gehörte eine kopierte Instanz im
    /// Original einer ebenfalls kopierten Instanz, gehört ihre Kopie deren Kopie; alles andere (Wurzel, und Instanzen,
    /// deren Owner nicht mitkopiert wird) gehört dem übergebenen Owner.
    ///
    /// Für beide gilt: Konstruktoren laufen nicht (es werden nur Feldwerte übertragen), der Destruktor der Kopie läuft wie
    /// bei jedem Objekt. Actors werden nie kopiert (ein Actor als Operand ist ein Fehler, ein Actor in der Tiefe bleibt
    /// eine geteilte Referenz - Actor-Referenzen sind ohnehin für das Herumreichen gedacht, THREADING_DESIGN 2), ebenso
    /// bleibt ein schon zerstörtes Objekt in der Tiefe eine geteilte Referenz. Lambdas und Pointer werden geteilt.
    /// </summary>
    public static class ObjectCloner
    {
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
                    return Value.MakeArray(items);
                }

                case ValueKind.Buffer:
                    return Value.MakeBuffer(source.AsBuffer().Clone());

                default:
                    // Wertartig (bool/int/float/char/string/undefined) und Referenzen ohne eigenen Inhalt
                    // (Lambda/Pointer): es gibt nichts zu kopieren.
                    return source;
            }
        }

        private static void RequireCopyableRoot(ObjectInstance obj)
        {
            if (obj.IsDestroyed)
                throw new InvalidOperationException(
                    $"Ein bereits zerstörtes Objekt ('{obj.ClassName}') lässt sich nicht kopieren.");
            if (IsActor(obj))
                throw new InvalidOperationException(
                    $"Ein Actor ('{obj.ClassName}') lässt sich nicht kopieren - Actor-Referenzen werden geteilt.");
        }

        private static bool IsActor(ObjectInstance obj) => obj.Mailbox != null || (obj.RtClass?.IsActor ?? false);

        private static ObjectInstance FlatCopy(ObjectInstance node, IOwner owner)
        {
            var copy = new ObjectInstance(node.ClassName, owner, node.RtClass);
            foreach (var (name, value) in Snapshot(node))
                copy.Fields[name] = value;
            return copy;
        }

        /// <summary>Die Felder unter dem Baum-Lock gelesen (ein Objekt, das an einem `taking`-Thread hängt, kann
        /// gleichzeitig von dort verändert werden).</summary>
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

            // Phase 1 (Entdecken): alle zu kopierenden Instanzen mit ihren Feld-Schnappschüssen, in Fundreihenfolge.
            private readonly List<ObjectInstance> _order = new();
            private readonly Dictionary<ObjectInstance, List<KeyValuePair<string, Value>>> _found = new(ReferenceEqualityComparer.Instance);
            private readonly HashSet<object> _containersSeen = new(ReferenceEqualityComparer.Instance);

            // Phase 2/3 (Anlegen/Füllen): Original -> Kopie (Identitätstabellen).
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

            /// <summary>Ein Array als Operand: es hat keine Wurzel-Instanz, alle darin gefundenen Objekte kommen nach den
            /// gewohnten Regeln an den Owner.</summary>
            public Value RunOnContainer(Value container)
            {
                Discover(container);
                Materialize();
                FillFields();
                return MapValue(container);
            }

            /// <summary>Sammelt alles Erreichbare (Objekte, Arrays) ohne Rekursion über den C#-Stack.</summary>
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
                            // Actors und Zerstörtes bleiben geteilte Referenzen (nur die Wurzel wird vorab geprüft).
                            if (!ReferenceEquals(obj, _root) && (IsActor(obj) || obj.IsDestroyed)) break;
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

            /// <summary>Legt alle Kopien an (noch ohne Feldwerte) - die Wurzel zuerst, danach jede Instanz unter ihrem
            /// kopierten Besitzer, falls der mitkopiert wird, sonst unter dem Wurzel-Owner.</summary>
            private void Materialize()
            {
                if (_root != null)
                    _copies[_root] = new ObjectInstance(_root.ClassName, _rootOwner, _root.RtClass);
                foreach (var obj in _order)
                    MaterializeOne(obj);
            }

            private ObjectInstance MaterializeOne(ObjectInstance obj)
            {
                if (_copies.TryGetValue(obj, out var existing)) return existing;
                // Der Besitz-Baum hat keine Zyklen (TakeTo verhindert sie), die Rekursion über die Besitzer endet also.
                IOwner owner = obj.Owner is ObjectInstance ownerObj && _found.ContainsKey(ownerObj)
                    ? MaterializeOne(ownerObj)
                    : _rootOwner;
                var copy = new ObjectInstance(obj.ClassName, owner, obj.RtClass);
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
                        _arrayCopies[array] = copy; // vor dem Füllen eintragen: ein Array darf sich selbst enthalten
                        for (int i = 0; i < array.Length; i++)
                            copy.Items[i] = MapValue(array.Items[i]);
                        return Value.MakeArray(copy);
                    }

                    case ValueKind.Buffer:
                    {
                        var buffer = v.AsBuffer();
                        if (!_bufferCopies.TryGetValue(buffer, out var copy))
                            _bufferCopies[buffer] = copy = buffer.Clone();
                        return Value.MakeBuffer(copy);
                    }

                    default:
                        return v;
                }
            }
        }
    }
}
