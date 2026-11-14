using fire.Values;

namespace fire.Runtime
{
    /// <summary>The members a value has without a class of the program: the ownership methods of everything that is owned (objects, arrays, buffers,
    /// lambdas), `Length` of texts, arrays and buffers, and the built-in methods of the value types. `x??.name` (a conditional access that checks the member,
    /// docs/language/03-operators.md) asks this for a value that is no object - the VM directly, natively the same table (fire_rt.h fire_builtinHas).</summary>
    public static class BuiltinMembers
    {
        /// <summary>The ownership methods: every owned value has them.</summary>
        public static bool IsOwnership(string name) => name is "TakeTo" or "TakeLocal" or "TakeUpwards" or "TakeGlobal";

        /// <summary>Does a value of this kind have the member (without a class of the program)? <paramref name="isEnum"/>: an int that is a value of an enum.</summary>
        public static bool Has(ValueKind kind, bool isEnum, string name) => kind switch
        {
            ValueKind.String => name is "Length" or "length" or "ToBytes" or "ToUnicode",
            ValueKind.Char => name is "ToByte" or "ToUnicode",
            ValueKind.Int => name is "ToChar" || (isEnum && name is "ToString" or "HasFlag"),
            ValueKind.Array => IsOwnership(name) || name is "Length" or "length" or "GetEnumerator",
            ValueKind.Buffer => IsOwnership(name) || name is "Length" or "length" or "littleEndian" or "GetEnumerator" or "ToString" or "ToUnicode" or "ToUnicodeChar"
                or "ToLittleEndian" or "ToBigEndian" || fire.Standard.BufferMethods.Has(name),
            ValueKind.Lambda => IsOwnership(name),
            _ => false,
        };
    }
}
