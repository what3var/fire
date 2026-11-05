namespace fire.Values
{
    /// <summary>Little or big endian. Affects ONLY the interpretation
    /// of ToLittleEndian()/ToBigEndian() on a ByteBuffer (see there) -
    /// the bytes actually stored never change implicitly
    /// by themselves, only through an EXPLICIT call of one of these two
    /// methods.</summary>
    public enum ByteOrder { Little, Big }

    /// <summary>
    /// A raw byte buffer of fixed size - deliberately SEPARATE from ScriptArray
    /// (which holds boxed Value[] elements, an overhead of several bytes per
    /// element): a ByteBuffer is a real, compact byte[], intended for
    /// binary data from IO (serial, network, files). Deliberately restricted to
    /// the simplest C# building blocks (array, simple loops), NO
    /// LINQ/reflection/high-level .NET features - this class is meant to
    /// be transferable 1:1 into a C++ VM later.
    ///
    /// Carries a ByteOrder as a mutable property: set explicitly on
    /// creation or (default) taken over from the
    /// host architecture (see VM.HostByteOrder - determined at runtime
    /// via a bit trick, no compile flag).
    /// </summary>
    public sealed class ByteBuffer : fire.Runtime.IOwnedLeaf
    {
        public byte[] Bytes { get; }

        /// <summary>The owner (SPEC 2), see <see cref="ScriptArray.LeafOwner"/>.</summary>
        public fire.Runtime.IOwner? LeafOwner { get; set; }
        public bool IsDestroyed { get; private set; }
        public void MarkDestroyed(fire.Runtime.IDestructRunner runner) { IsDestroyed = true; LeafOwner = null; }
        public ByteOrder Order { get; set; }
        /// <summary>The number of bytes (what natives, files and the network see).</summary>
        public int Length => Bytes.Length;

        /// <summary>The bytes of one element: 1 for a byte buffer, 2, 4 or 8 for a buffer of machine words (`word a[]`, SPEC 8.10 - Value.WordBits / 8; the
        /// words in the byte order of the machine). The language indexes elements: <see cref="Count"/> of them.</summary>
        public int ElementSize { get; init; } = 1;
        /// <summary>A buffer of machine words (`word a[]`).</summary>
        public bool IsWords => ElementSize != 1;
        /// <summary>The number of elements (`buffer.length`): bytes, or words.</summary>
        public int Count => ElementSize == 1 ? Bytes.Length : Bytes.Length / ElementSize;

        /// <summary>A buffer of `count` machine words of `elementSize` bytes, all 0.</summary>
        public static ByteBuffer Words(int count, int elementSize) => new(count * elementSize, ByteConversions.HostByteOrder) { ElementSize = elementSize };

        /// <summary>The word at element `index` (a word buffer; no bounds check beyond the array's own).</summary>
        public ulong GetWord(long index)
        {
            var span = new System.ReadOnlySpan<byte>(Bytes, (int)index * ElementSize, ElementSize);
            return ElementSize switch
            {
                2 => System.Runtime.InteropServices.MemoryMarshal.Read<ushort>(span),
                4 => System.Runtime.InteropServices.MemoryMarshal.Read<uint>(span),
                _ => System.Runtime.InteropServices.MemoryMarshal.Read<ulong>(span),
            };
        }

        /// <summary>Stores a word at element `index` (cut to the element size, the byte order of the machine).</summary>
        public void SetWord(long index, ulong value)
        {
            var span = new System.Span<byte>(Bytes, (int)index * ElementSize, ElementSize);
            switch (ElementSize)
            {
                case 2: System.Runtime.InteropServices.MemoryMarshal.Write(span, (ushort)value); break;
                case 4: System.Runtime.InteropServices.MemoryMarshal.Write(span, (uint)value); break;
                default: System.Runtime.InteropServices.MemoryMarshal.Write(span, value); break;
            }
        }

        /// <summary>From this length on a buffer lies on the Pinned Object Heap: the GC never moves it, a framebuffer keeps its address and natives
        /// get it without a copy (see PackageNativeBinding).</summary>
        public const int PinnedThreshold = 16 * 1024;

        public ByteBuffer(int length, ByteOrder order)
        {
            Bytes = length >= PinnedThreshold ? System.GC.AllocateArray<byte>(length, pinned: true) : new byte[length];
            Order = order;
        }

        /// <summary>`fire handover` (see ObjectInstance.HandedOverTo): the buffer that took this one's place; this one is dead (destroyed) from then on.</summary>
        public ByteBuffer? HandedOverTo { get; private set; }

        internal ByteBuffer Relocate() => new(Bytes, Order) { LeafOwner = LeafOwner, _hash = GetHashCode(), ElementSize = ElementSize };

        // `==` compares the buffer, not the instance (see ObjectInstance.Equals).
        private int _hash;
        public override int GetHashCode()
        {
            if (_hash == 0) _hash = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this) | 1;
            return _hash;
        }
        public override bool Equals(object? obj) =>
            ReferenceEquals(this, obj)
            || (obj is ByteBuffer other && (HandedOverTo != null || other.HandedOverTo != null) && ReferenceEquals(Latest(this), Latest(other)));
        private static ByteBuffer Latest(ByteBuffer b)
        {
            while (b.HandedOverTo != null) b = b.HandedOverTo;
            return b;
        }

        internal void RetireAfterHandover(ByteBuffer fresh)
        {
            HandedOverTo = fresh;
            LeafOwner = null;
            IsDestroyed = true;
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

        public ByteBuffer(byte[] bytes, ByteOrder order)
        {
            Bytes = bytes;
            Order = order;
        }

        /// <summary>Returns `false` for an invalid index (`value` then 0),
        /// instead of throwing - see ScriptArray.TryGet for the same
        /// reasoning (C++ portability without exceptions in the hot path).</summary>
        public bool TryGet(long index, out byte value)
        {
            if (index < 0 || index >= Bytes.Length)
            {
                value = 0;
                return false;
            }
            value = Bytes[index];
            return true;
        }

        public bool TrySet(long index, byte value)
        {
            if (index < 0 || index >= Bytes.Length) return false;
            Bytes[index] = value;
            return true;
        }

        /// <summary>Like TryGet/TrySet, but WITHOUT the bounds check (see
        /// Bytecode.VmExecutionMode.Performance) - an invalid index leads
        /// to a raw, UNCAUGHT .NET IndexOutOfRangeException. Called only
        /// by VM opcode handlers in performance mode, never
        /// selectable directly from script code.</summary>
        public byte GetUnchecked(long index) => Bytes[(int)index];

        public void SetUnchecked(long index, byte value) => Bytes[(int)index] = value;

        /// <summary>Copy with the same bytes, but an independent
        /// backing array (mutations of the copy do not affect the
        /// original, as with every other "value is copied" place
        /// of this language).</summary>
        public ByteBuffer Clone()
        {
            var copy = new byte[Bytes.Length];
            System.Array.Copy(Bytes, copy, Bytes.Length);
            return new ByteBuffer(copy, Order) { ElementSize = ElementSize };
        }

        /// <summary>Returns a copy with reversed byte order -
        /// the ENTIRE buffer mirrored as ONE contiguous block (not
        /// element by element at a fixed width), since a ByteBuffer
        /// itself knows no element width - for a single multi-byte field
        /// (e.g. a 4-byte int) that IS exactly the desired meaning;
        /// for a buffer with several equally wide fields the
        /// mirroring has to be done per field yourself (e.g. via
        /// ReadU32/WriteU32 with explicit order, see prelude).</summary>
        public ByteBuffer Reversed(ByteOrder newOrder)
        {
            var copy = new byte[Bytes.Length];
            for (int i = 0; i < Bytes.Length; i++)
                copy[i] = Bytes[Bytes.Length - 1 - i];
            return new ByteBuffer(copy, newOrder);
        }
    }
}
