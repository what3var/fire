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
        public Value[] Items { get; }

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
        public int Length => Items.Length;

        public ScriptArray(int length)
        {
            Items = new Value[length];
            for (int i = 0; i < length; i++)
                Items[i] = Value.MakeUndefined();
        }

        /// <summary>The element type of a typed array (<see cref="ArrayElem"/>; 0: untyped, any value). A typed array (`int a[]`, `int[8] a[]`, `float a[]`,
        /// `bool a[]`, `char a[]`) holds only values of that type and `undefined`: an int is cut to the width, a float rounded, an int becomes a float in a
        /// float array; anything else is an ArgumentException, a number with another unit a UnitMismatchException (SPEC 8.4.2). The native backend
        /// stores such an array packed (one C number per element instead of a Value).</summary>
        public byte ElemType { get; private set; }

        /// <summary>The unit demand of the elements of a typed array (`int:cm a[]`: "cm"; null: the elements have no unit).</summary>
        public string? ElemUnit { get; private set; }

        /// <summary>Makes this (new) array a typed one: its elements are converted; the first that does not fit is reported (`error`, `message`, the element
        /// in `offending`) and the array stays untyped.</summary>
        public bool MakeTyped(byte elemType, string? elemUnit, out ArrayElem.Error error, out string? message, out Value offending)
        {
            var converted = new Value[Items.Length];
            for (int i = 0; i < Items.Length; i++)
            {
                offending = Items[i];
                if (!ArrayElem.TryConvert(elemType, elemUnit, Items[i], out converted[i], out error, out message)) return false;
            }
            System.Array.Copy(converted, Items, Items.Length);
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
            ArrayElem.TryConvert(ElemType, ElemUnit, value, out stored, out error, out message);

        /// <summary>Copies the element type of another array (a copy of a typed array is typed).</summary>
        public void CopyTypeOf(ScriptArray other)
        {
            ElemType = other.ElemType;
            ElemUnit = other.ElemUnit;
            Special = _shared || IsDestroyed || ElemType != 0;
        }

        /// <summary>Returns `false` for an invalid index (`value` then
        /// `default`) instead of throwing - the caller decides itself what
        /// that means (see VM.ArrayGet: a catchable script exception).</summary>
        public bool TryGet(long index, out Value value)
        {
            if (index < 0 || index >= Items.Length)
            {
                value = default;
                return false;
            }
            value = Items[index];
            return true;
        }

        /// <summary>Returns `false` for an invalid index, WITHOUT writing.</summary>
        public bool TrySet(long index, Value value)
        {
            if (index < 0 || index >= Items.Length) return false;
            Items[index] = value;
            return true;
        }

        /// <summary>UNCHECKED access (see Bytecode.VmExecutionMode.
        /// Performance) - an invalid index leads to a raw .NET
        /// IndexOutOfRangeException (in C++: undefined behaviour) instead of
        /// a controlled `false`. Called only by VM opcode handlers in
        /// performance mode, never selectable directly from script
        /// code.</summary>
        public Value GetUnchecked(long index) => Items[(int)index];

        public void SetUnchecked(long index, Value value) => Items[(int)index] = value;
    }
}
