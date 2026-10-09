using System;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>A pointer is used outside its range (SPEC 8.3): an element beyond the bounds of an array/buffer, an offset other than 0 for a pointer to a variable or a
    /// field (an "array" with exactly one element), or the array/buffer is destroyed. The VM turns this into the catchable `IndexOutOfBoundsException` or `DestroyedException`.</summary>
    public sealed class PointerRangeException : Exception
    {
        public long Index { get; }
        public long Length { get; }
        /// <summary>"array" or "buffer" if the target is destroyed (otherwise null).</summary>
        public string? DestroyedKind { get; }
        public bool Destroyed => DestroyedKind != null;
        /// <summary>"Array index" for array/buffer elements, "Pointer offset" for variables and fields.</summary>
        public string What { get; }
        public PointerRangeException(string what, long index, long length, string? destroyedKind = null)
            : base(destroyedKind != null ? $"Access to a destroyed {destroyedKind}." : $"{what} {index} out of range (length {length}).")
        {
            What = what; Index = index; Length = length; DestroyedKind = destroyedKind;
        }
    }

    /// <summary>Points to a concrete slot in a concrete scope (local or global variable). A pointer to a variable is like a pointer to an array with exactly
    /// one element (C): `p + n` is allowed (also `p - 1 + 1`), only offset 0 may be used - otherwise `IndexOutOfBoundsException`. (Formerly the pointer moved to the neighbouring slot of the scope;
    /// that does not exist in natively translated code - there the variables are C++ variables.)</summary>
    public sealed class ScopeSlotPointerTarget : PointerTarget
    {
        public Scope Scope { get; }
        public int Slot { get; }
        public long Offset { get; }

        public ScopeSlotPointerTarget(Scope scope, int slot, long offset = 0)
        {
            Scope = scope;
            Slot = slot;
            Offset = offset;
            scope.MarkEscaped(); // the scope is now reachable from outside and is not reused (see Scope.CanRecycle)
        }

        public override Value Read() => Offset == 0 ? Scope.GetSlot(Slot) : throw new PointerRangeException("Pointer offset", Offset, 1);
        public override void Write(Value v)
        {
            if (Offset != 0) throw new PointerRangeException("Pointer offset", Offset, 1);
            Scope.SetSlot(Slot, v);
        }

        public override PointerTarget Advance(long elementOffset) =>
            elementOffset == 0 ? this : new ScopeSlotPointerTarget(Scope, Slot, Offset + elementOffset);

        public override long? DistanceTo(PointerTarget other) =>
            other is ScopeSlotPointerTarget o && ReferenceEquals(o.Scope, Scope) && o.Slot == Slot ? Offset - o.Offset : null;

        public override bool Equals(object? obj) =>
            obj is ScopeSlotPointerTarget o && ReferenceEquals(o.Scope, Scope) && o.Slot == Slot && o.Offset == Offset;

        public override int GetHashCode() => System.HashCode.Combine(Scope, Slot, Offset);
    }

    /// <summary>Points to a named field of a concrete object instance - like a pointer to a variable an "array" with one element (see <see cref="ScopeSlotPointerTarget"/>).</summary>
    public sealed class FieldPointerTarget : PointerTarget
    {
        public ObjectInstance Instance { get; }
        public string FieldName { get; }
        public long Offset { get; }

        public FieldPointerTarget(ObjectInstance instance, string fieldName, long offset = 0)
        {
            Instance = instance;
            FieldName = fieldName;
            Offset = offset;
        }

        public override Value Read() => Offset == 0 ? Instance.Fields[FieldName] : throw new PointerRangeException("Pointer offset", Offset, 1);
        public override void Write(Value v)
        {
            if (Offset != 0) throw new PointerRangeException("Pointer offset", Offset, 1);
            Instance.Fields[FieldName] = v;
        }

        public override PointerTarget Advance(long elementOffset) =>
            elementOffset == 0 ? this : new FieldPointerTarget(Instance, FieldName, Offset + elementOffset);

        public override long? DistanceTo(PointerTarget other) =>
            other is FieldPointerTarget o && ReferenceEquals(o.Instance, Instance) && o.FieldName == FieldName ? Offset - o.Offset : null;

        public override bool Equals(object? obj) =>
            obj is FieldPointerTarget o && ReferenceEquals(o.Instance, Instance) && o.FieldName == FieldName && o.Offset == Offset;

        public override int GetHashCode() => System.HashCode.Combine(Instance, FieldName, Offset);
    }

    /// <summary>Points to an element of an array or buffer (argument for a `ref` parameter, `p + n`). The index and the array are checked on read/write.</summary>
    public sealed class ElementPointerTarget : PointerTarget
    {
        private readonly ScriptArray? _array;
        private readonly ByteBuffer? _buffer;
        public long Index { get; }

        public ElementPointerTarget(ScriptArray array, long index) { _array = array; Index = index; }
        public ElementPointerTarget(ByteBuffer buffer, long index) { _buffer = buffer; Index = index; }

        private PointerRangeException OutOfRange() =>
            (_array?.IsDestroyed ?? _buffer!.IsDestroyed) ? new PointerRangeException("Array index", Index, 0, _array != null ? "array" : "buffer")
                                                           : new PointerRangeException("Array index", Index, _array?.Length ?? _buffer!.Length);

        public override Value Read()
        {
            if (_array != null)
                return !_array.IsDestroyed && _array.TryGet(Index, out var v) ? v : throw OutOfRange();
            return !_buffer!.IsDestroyed && _buffer.TryGet(Index, out byte b) ? Value.MakeInt(b, width: NumericWidth.W8) : throw OutOfRange();
        }

        public override void Write(Value v)
        {
            if (_array != null)
            {
                if (_array.IsDestroyed || !_array.TrySet(Index, v)) throw OutOfRange();
                return;
            }
            if (_buffer!.IsDestroyed || !_buffer.TrySet(Index, (byte)v.AsInt())) throw OutOfRange();
        }

        public override PointerTarget Advance(long elementOffset) => _array != null
            ? new ElementPointerTarget(_array, Index + elementOffset)
            : new ElementPointerTarget(_buffer!, Index + elementOffset);

        public override long? DistanceTo(PointerTarget other) =>
            other is ElementPointerTarget o && ReferenceEquals(o._array, _array) && ReferenceEquals(o._buffer, _buffer) ? Index - o.Index : null;

        public override bool Equals(object? obj) =>
            obj is ElementPointerTarget o && ReferenceEquals(o._array, _array) && ReferenceEquals(o._buffer, _buffer) && o.Index == Index;

        public override int GetHashCode() => System.HashCode.Combine(_array, _buffer, Index);
    }
}
