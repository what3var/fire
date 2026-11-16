namespace fire.Values
{
    /// <summary>
    /// An array of fixed size (elements default-initialised with 'undefined').
    /// Deliberately kept simple - allocation is one-dimensional; multi-dimensional
    /// ("jagged") arrays arise from several nested ScriptArray
    /// instances (see Compiler.CompileArrayAlloc), not from a dedicated
    /// multi-dimensional runtime representation.
    ///
    /// Access deliberately WITHOUT C# exceptions for the invalid-index case (see
    /// TryGet/TrySet) - return value/out parameter instead of throw/catch, so that
    /// the same logic can be transferred 1:1 to C++ (the native runtime, also on FreeRTOS), where exceptions in embedded
    /// environments are often switched off entirely. The VM (see VM.ArrayGet/
    /// ArraySet) turns a `false` here into a proper, catchable
    /// fire `IndexOutOfBoundsException` (VM.ThrowIndexOutOfBounds) -
    /// this conversion deliberately stays on the VM side, not here, since "throwing a
    /// script exception" is a concept of the VM/interpreter,
    /// not of this pure data structure.
    /// </summary>
    public sealed class ScriptArray : fire.Runtime.IOwnedLeaf, fire.Runtime.IOwner
    {
        // The elements. Three states (docs/spec: the VM's storage of arrays):
        //   - Values (_items): one 24-byte Value per element - every array that is not one of the two below.
        //   - fresh (_items and _bools null): `new T[n]` before anything was stored - every element is `undefined`, nothing is allocated yet.
        //   - packed bools (_bools): a `bool a[]` (ElemType Bool) - ONE BYTE per element: 0 false, 1 true, 2 undefined.
        // Code that knows the states reads and writes through TryGet/TrySet/GetUnchecked/SetUnchecked (and the VM's fast paths through PackedBools);
        // everything else takes Items, which turns the array into Values for good (Unpack) - the same elements, so nothing but the memory changes.
        private Value[]? _items;
        private byte[]? _bools;
        private readonly int _length;

        /// <summary>The byte of an undefined element of a packed bool array (0: false, 1: true).</summary>
        public const byte UndefinedBool = 2;
        private static readonly Value[] BoolValues = { Value.MakeBool(false), Value.MakeBool(true), Value.MakeUndefined() };

        /// <summary>The elements as Values. A fresh or packed array (<see cref="IsPacked"/>) becomes an array of Values by this - read single elements with
        /// <see cref="GetUnchecked"/> or <see cref="TryGet"/> instead where the array may be big.</summary>
        public Value[] Items => _items ?? Unpack();

        /// <summary>The elements are not stored as Values (a fresh array, packed bools): they are bools and `undefined` only - nothing in them refers to
        /// an object, an array, a buffer or a lambda.</summary>
        public bool IsPacked => _items == null;

        /// <summary>The bytes of a packed bool array that the VM may read and write in place (0 false, 1 true, <see cref="UndefinedBool"/>); null for
        /// every other array and for a shared or destroyed one (their accesses take the slow path).</summary>
        public byte[]? PackedBools => _shared || IsDestroyed ? null : _bools;

        private Value[] Unpack()
        {
            var items = new Value[_length];
            var bools = _bools;
            if (bools == null) System.Array.Fill(items, Value.MakeUndefined());
            else for (int i = 0; i < items.Length; i++) items[i] = BoolValues[bools[i]];
            _items = items;
            _bools = null;
            return items;
        }

        /// <summary>The owner (SPEC 2): a scope or an object; null for an array created outside the VM that is never destroyed.</summary>
        public fire.Runtime.IOwner? LeafOwner { get; set; }

        /// <summary>Destroyed (the owner was left/destroyed or `delete`): access is an error (Debug/Release).</summary>
        public bool IsDestroyed { get; private set; }

        /// <summary>Inner arrays of a multi-dimensional allocation (`new int[3][4]`): they belong to the outer array and are destroyed with it.</summary>
        public System.Collections.Generic.List<fire.Runtime.IOwnedLeaf>? Parts { get; set; }

        // An array can own objects (SPEC 2.2): what `Takes` and `return` take along on an array belongs to the array and dies with it.
        private fire.Runtime.OwnedSet _ownedObjects;
        public System.Collections.Generic.IReadOnlyList<fire.Runtime.ObjectInstance> OwnedObjects => _ownedObjects.AsList();
        public void AddOwned(fire.Runtime.ObjectInstance obj) => _ownedObjects.Add(obj);
        public void RemoveOwned(fire.Runtime.ObjectInstance obj) => _ownedObjects.Remove(obj);
        public void AddLeaf(fire.Runtime.IOwnedLeaf leaf) => (Parts ??= new System.Collections.Generic.List<fire.Runtime.IOwnedLeaf>()).Add(leaf);
        public void RemoveLeaf(fire.Runtime.IOwnedLeaf leaf) => Parts?.Remove(leaf);

        public void MarkDestroyed(fire.Runtime.IDestructRunner runner)
        {
            if (IsDestroyed) return;
            // what belongs to the array dies before it (the destructors still see it)
            if (!_ownedObjects.IsEmpty) _ownedObjects.DestroyAll(runner);
            IsDestroyed = true;
            Special = true;
            LeafOwner = null;
            if (Parts != null) foreach (var part in Parts.ToArray()) part.MarkDestroyed(runner);
            Parts = null;
        }

        /// <summary>Was this array reached by a fire thread via the globals (see GlobalsBroker)? Then it belongs to the shared
        /// area: element accesses run under the tree lock, and a fire thread changes elements only inside a section.</summary>
        public bool IsShared { get => _shared; set { _shared = value; Special = value || IsDestroyed || ElemType != 0; } }
        private bool _shared;

        /// <summary>Shared or destroyed: the VM's fast paths (element access) then take the slow path, which respects both.</summary>
        public bool Special { get; private set; }
        public int Length => _length;

        /// <summary>`fire handover` (see ObjectInstance.HandedOverTo): the array that took this one's place; this one is dead (destroyed) from then on.</summary>
        public ScriptArray? HandedOverTo { get; private set; }

        private ScriptArray(ScriptArray storage) { _items = storage._items; _bools = storage._bools; _length = storage._length; }

        /// <summary>The new array for a handover: the same elements, owner, owned objects and parts (not registered anywhere yet).</summary>
        internal ScriptArray Relocate()
        {
            // (a fresh array has no storage to share yet: it gets its Values first)
            if (_items == null && _bools == null) Unpack();
            return new(this) { LeafOwner = LeafOwner, Parts = Parts, _ownedObjects = _ownedObjects, _hash = GetHashCode() };
        }

        // `==` compares the array, not the instance (see ObjectInstance.Equals).
        private int _hash;
        public override int GetHashCode()
        {
            if (_hash == 0) _hash = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this) | 1;
            return _hash;
        }
        public override bool Equals(object? obj) =>
            ReferenceEquals(this, obj)
            || (obj is ScriptArray other && (HandedOverTo != null || other.HandedOverTo != null) && ReferenceEquals(Latest(this), Latest(other)));
        private static ScriptArray Latest(ScriptArray a)
        {
            while (a.HandedOverTo != null) a = a.HandedOverTo;
            return a;
        }

        internal void ReplaceOwned(fire.Runtime.ObjectInstance old, fire.Runtime.ObjectInstance fresh) => _ownedObjects.Replace(old, fresh);
        internal void ReplaceLeaf(fire.Runtime.IOwnedLeaf old, fire.Runtime.IOwnedLeaf fresh) { int i = Parts?.IndexOf(old) ?? -1; if (i >= 0) Parts![i] = fresh; }

        internal void RetireAfterHandover(ScriptArray fresh)
        {
            HandedOverTo = fresh;
            _ownedObjects = default;
            Parts = null;
            LeafOwner = null;
            IsDestroyed = true;
            _shared = false;
            Special = true;
        }

        /// <summary>See ObjectInstance.LivesInAnotherThread.</summary>
        public bool LivesInAnotherThread
        {
            get
            {
                var current = HandedOverTo;
                while (current?.HandedOverTo != null) current = current.HandedOverTo;
                return current != null && !current.IsDestroyed;
            }
        }

        /// <summary>A fresh array: every element is `undefined`. Its storage comes with the first use (Values) or with its type (`bool a[]`: bytes).</summary>
        public ScriptArray(int length)
        {
            if (length < 0) throw new System.OverflowException();
            _length = length;
        }

        /// <summary>The element type of a typed array (<see cref="ArrayElem"/>; 0: untyped, any value). A typed array (`int a[]`, `int[8] a[]`, `float a[]`,
        /// `bool a[]`, `char a[]`) holds only values of that type and `undefined`: an int is cut to the width, a float rounded, an int becomes a float in a
        /// float array; anything else is an ArgumentException, a number with another unit a UnitMismatchException (SPEC 8.4.2). The native backend
        /// stores such an array packed (one C number per element instead of a Value).</summary>
        public byte ElemType { get; private set; }

        /// <summary>`new int[n]` before a declaration typed it: the kind it was made for (ArrayElem.OpenKindOf - I64, F64, Bool or Char; 0: none, a literal).
        /// Nothing is checked against it but the declaration the array goes to (ArrayElem.OpenFits).</summary>
        public byte OpenKind { get; set; }

        /// <summary>The unit demand of the elements of a typed array (`int:cm a[]`: "cm"; null: the elements have no unit).</summary>
        public string? ElemUnit { get; private set; }

        /// <summary>`defined int a[]`: the elements are never `undefined` (ArrayElem.DefinedFlag) - they start at the zero of the type, storing `undefined`
        /// is an ArgumentException.</summary>
        public bool ElemDefined { get; private set; }

        /// <summary>Makes this typed array a defined one: an `undefined` element becomes the zero of the type (`new int[n]` starts at 0 that way).</summary>
        public void MakeDefined() => MakeDefined(ArrayElem.Zero(ElemType, ElemUnit));

        /// <summary>Like MakeDefined with the zero given (the first value of an enum). False - and the array stays as it is - when the zero is undefined
        /// (an array of objects has none) and an element is: such an array is defined only with every element given.</summary>
        public bool MakeDefined(Value zero)
        {
            if (ElemDefined || ElemType == ArrayElem.None) return true;
            if (_bools is { } bools && zero.Kind == ValueKind.Bool)
            {
                // (packed bools: the undefined ones become the zero)
                byte zeroByte = zero.AsBool() ? (byte)1 : (byte)0;
                System.MemoryExtensions.Replace(System.MemoryExtensions.AsSpan(bools), UndefinedBool, zeroByte);
                ElemDefined = true;
                return true;
            }
            var items = Items;
            if (zero.Kind == ValueKind.Undefined)
                for (int i = 0; i < items.Length; i++)
                    if (items[i].Kind == ValueKind.Undefined) return false;
            for (int i = 0; i < items.Length; i++)
                if (items[i].Kind == ValueKind.Undefined) items[i] = zero;
            ElemDefined = true;
            return true;
        }

        /// <summary>Makes this (new) array a typed one: its elements are converted; the first that does not fit is reported (`error`, `message`, the element
        /// in `offending`) and the array stays untyped.</summary>
        public bool MakeTyped(byte elemType, string? elemUnit, out ArrayElem.Error error, out string? message, out Value offending)
        {
            if (_items == null && _bools == null)
            {
                // a fresh array (`new bool[n]`, `new int[n]`): every element is the same `undefined` - converted once; bools get their bytes, every other
                // type its Values (no second array of them)
                offending = Value.MakeUndefined();
                if (!ArrayElem.TryConvert(elemType, elemUnit, offending, out var fresh, out error, out message)) return false;
                if (elemType == ArrayElem.Bool && elemUnit == null && fresh.Kind == ValueKind.Undefined)
                {
                    var bytes = new byte[_length];
                    System.Array.Fill(bytes, UndefinedBool);
                    _bools = bytes;
                }
                else
                {
                    var values = new Value[_length];
                    System.Array.Fill(values, fresh);
                    _items = values;
                }
            }
            else
            {
                var items = Items;
                var converted = new Value[items.Length];
                bool packable = elemType == ArrayElem.Bool && elemUnit == null;
                for (int i = 0; i < items.Length; i++)
                {
                    offending = items[i];
                    if (!ArrayElem.TryConvert(elemType, elemUnit, items[i], out converted[i], out error, out message)) return false;
                    packable = packable && IsPackableBool(converted[i]);
                }
                if (packable)
                {
                    // (`bool a[] = [true, false]`: the literal becomes bytes)
                    var bytes = new byte[converted.Length];
                    for (int i = 0; i < bytes.Length; i++) bytes[i] = BoolByte(converted[i]);
                    _bools = bytes;
                    _items = null;
                }
                else System.Array.Copy(converted, items, items.Length);
            }
            ElemType = elemType;
            ElemUnit = elemUnit;
            Special = _shared || IsDestroyed || elemType != 0;
            offending = default;
            error = ArrayElem.Error.None;
            message = null;
            return true;
        }

        /// <summary>`array[index] = value` for a typed array: the value converted to the element type (<see cref="ArrayElem.TryConvert"/>), or the reason
        /// why it does not fit. An untyped array takes the value as it is.</summary>
        public bool TryConvertElement(Value value, out Value stored, out ArrayElem.Error error, out string? message) =>
            ArrayElem.TryConvert(ElemType, ElemUnit, value, ElemDefined, out stored, out error, out message);

        /// <summary>Copies the element type of another array (a copy of a typed array is typed).</summary>
        public void CopyTypeOf(ScriptArray other)
        {
            ElemType = other.ElemType;
            ElemUnit = other.ElemUnit;
            ElemDefined = other.ElemDefined;
            OpenKind = other.OpenKind;
            Special = _shared || IsDestroyed || ElemType != 0;
        }

        /// <summary>Is `value` what a byte of a packed bool array can hold - a bool or a plain `undefined`?</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static bool IsPackableBool(in Value value) =>
            value.Kind == ValueKind.Bool || value.Kind == ValueKind.Undefined && value.Unit is null or { IsUnitless: true };

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static byte BoolByte(in Value value) => value.Kind == ValueKind.Bool ? (value.AsBool() ? (byte)1 : (byte)0) : UndefinedBool;

        /// <summary>The first `count` elements of `source` into this array as they are (no conversion: the caller knows they fit) - packed bools as bytes
        /// when this array can take them so (a fresh one becomes packed like the source), else element by element.</summary>
        public void CopyElementsFrom(ScriptArray source, int count)
        {
            if (count <= 0) return;
            if (source._bools is { } from)
            {
                if (_items == null && _bools == null && count == _length && ElemType == ArrayElem.Bool && ElemUnit == null)
                {
                    _bools = System.MemoryExtensions.AsSpan(from, 0, count).ToArray();
                    return;
                }
                if (_bools is { } to) { System.Array.Copy(from, to, count); return; }
                var items = Items;
                for (int i = 0; i < count; i++) items[i] = BoolValues[from[i]];
                return;
            }
            if (source._items == null) { for (int i = 0; i < count; i++) SetUnchecked(i, BoolValues[UndefinedBool]); return; }
            if (_bools != null) { for (int i = 0; i < count; i++) SetUnchecked(i, source._items[i]); return; }
            System.Array.Copy(source._items, Items, count);
        }

        /// <summary>All elements as Values into `destination` (as long as this array) - without turning a packed array into Values.</summary>
        public void ReadItems(Value[] destination)
        {
            if (_items is { } items) { System.Array.Copy(items, destination, items.Length); return; }
            if (_bools is { } bools) { for (int i = 0; i < bools.Length; i++) destination[i] = BoolValues[bools[i]]; return; }
            System.Array.Fill(destination, Value.MakeUndefined(), 0, _length);
        }

        /// <summary>Returns `false` for an invalid index (`value` then
        /// `default`) instead of throwing - the caller decides itself what
        /// that means (see VM.ArrayGet: a catchable script exception).</summary>
        public bool TryGet(long index, out Value value)
        {
            if (index < 0 || index >= _length)
            {
                value = default;
                return false;
            }
            value = GetUnchecked(index);
            return true;
        }

        /// <summary>Returns `false` for an invalid index, WITHOUT writing.</summary>
        public bool TrySet(long index, Value value)
        {
            if (index < 0 || index >= _length) return false;
            SetUnchecked(index, value);
            return true;
        }

        /// <summary>UNCHECKED access (see Bytecode.VmExecutionMode.
        /// Performance) - an invalid index leads to a raw .NET
        /// IndexOutOfRangeException (in C++: undefined behaviour) instead of
        /// a controlled `false`. Called only by VM opcode handlers in
        /// performance mode, never selectable directly from script
        /// code.</summary>
        public Value GetUnchecked(long index)
        {
            if (_items is { } items) return items[(int)index];
            if (_bools is { } bools) return BoolValues[bools[(int)index]];
            if ((ulong)index >= (ulong)_length) throw new System.IndexOutOfRangeException();
            return BoolValues[UndefinedBool];   // (a fresh array)
        }

        public void SetUnchecked(long index, Value value)
        {
            if (_items is { } items) { items[(int)index] = value; return; }
            // (packed bools take a bool or a plain undefined in place; anything else - a caller that did not convert - makes the array Values, as before)
            if (_bools is { } bools && IsPackableBool(value)) { bools[(int)index] = BoolByte(value); return; }
            Items[(int)index] = value;
        }
    }
}
