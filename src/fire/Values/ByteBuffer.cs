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
        public int Length => Bytes.Length;

        /// <summary>From this length on a buffer lies on the Pinned Object Heap: the GC never moves it, a framebuffer keeps its address and natives
        /// get it without a copy (see PackageNativeBinding).</summary>
        public const int PinnedThreshold = 16 * 1024;

        public ByteBuffer(int length, ByteOrder order)
        {
            Bytes = length >= PinnedThreshold ? System.GC.AllocateArray<byte>(length, pinned: true) : new byte[length];
            Order = order;
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
            return new ByteBuffer(copy, Order);
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
