using System;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>Ein Zeiger wird ausserhalb seines Bereichs benutzt (SPEC 8.3): ein Element jenseits der Grenzen eines Arrays/Puffers, ein Versatz ungleich 0 bei einem Zeiger auf eine Variable oder ein
    /// Feld (ein "Array" mit genau einem Element), oder das Array/der Puffer ist zerstoert. Die VM macht daraus die fangbare `IndexOutOfBoundsException` bzw. `DestroyedException`.</summary>
    public sealed class PointerRangeException : Exception
    {
        public long Index { get; }
        public long Length { get; }
        /// <summary>"array" oder "buffer", wenn das Ziel zerstoert ist (sonst null).</summary>
        public string? DestroyedKind { get; }
        public bool Destroyed => DestroyedKind != null;
        /// <summary>"Array index" fuer Array-/Puffer-Elemente, "Pointer offset" fuer Variablen und Felder.</summary>
        public string What { get; }
        public PointerRangeException(string what, long index, long length, string? destroyedKind = null)
            : base(destroyedKind != null ? $"Access to a destroyed {destroyedKind}." : $"{what} {index} out of range (length {length}).")
        {
            What = what; Index = index; Length = length; DestroyedKind = destroyedKind;
        }
    }

    /// <summary>Zeigt auf einen konkreten Slot in einem konkreten Scope (lokale oder globale Variable). Ein Zeiger auf eine Variable ist wie ein Zeiger auf ein Array mit genau
    /// einem Element (C): `p + n` ist erlaubt (auch `p - 1 + 1`), benutzt werden darf nur der Versatz 0 - sonst `IndexOutOfBoundsException`. (Frueher rueckte der Zeiger zum Nachbar-Slot des Scopes;
    /// den gibt es im nativ uebersetzten Code nicht - dort sind die Variablen C++-Variablen.)</summary>
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
            scope.MarkEscaped(); // die Scope ist ab jetzt von außen erreichbar und wird nicht wiederverwendet (siehe Scope.CanRecycle)
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

    /// <summary>Zeigt auf ein benanntes Feld einer konkreten Objektinstanz - wie ein Zeiger auf eine Variable ein "Array" mit einem Element (siehe <see cref="ScopeSlotPointerTarget"/>).</summary>
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

    /// <summary>Zeigt auf ein Element eines Arrays oder Puffers (Argument fuer einen `ref`-Parameter, `p + n`). Der Index und das Array werden beim Lesen/Schreiben geprueft.</summary>
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
