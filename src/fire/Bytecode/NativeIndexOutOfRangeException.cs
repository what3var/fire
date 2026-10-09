using System;

namespace fire.Bytecode
{
    /// <summary>
    /// A native function (see <see cref="NativeFunction"/>) uses this to report an invalid index
    /// or an invalid length. The VM catches it right at the call and turns it into a completely
    /// normal `IndexOutOfBoundsException` of the script that can be caught via `try`/`catch` - a native
    /// function otherwise has no access to the script exceptions (it does not know the VM).
    /// `What` is at the start of the message ("String index", "Array index", ...).
    /// </summary>
    public sealed class NativeIndexOutOfRangeException : Exception
    {
        public long Index { get; }
        public int Length { get; }
        public string What { get; }

        public NativeIndexOutOfRangeException(long index, int length, string what = "Index")
            : base($"{what} {index} out of range (length {length}).")
        {
            Index = index;
            Length = length;
            What = what;
        }
    }
}
