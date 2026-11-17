using System;
using fire.Values;

namespace fire.Standard
{
    /// <summary>`__array_copy(src, dst, count)` (the prelude: List.Grow&lt;int&gt;/&lt;float&gt;): the first `count` elements of src into dst - at once when
    /// both arrays hold the same element type, else one by one through the typing of dst. Natively fire_rt.h fire_arrayCopy (one block for packed arrays).</summary>
    public static class ArrayCopyMethods
    {
        public const string NativeName = "__array_copy";

        public static Value Call(Value[] args)
        {
            if (args.Length != 3 || args[0].Kind != ValueKind.Array || args[1].Kind != ValueKind.Array || args[2].Kind != ValueKind.Int)
                throw new InvalidOperationException("__array_copy needs two arrays and a count.");
            var src = args[0].AsArray();
            var dst = args[1].AsArray();
            long n = args[2].AsInt();
            if (n < 0 || n > src.Length || n > dst.Length) throw new InvalidOperationException("__array_copy: the count is outside the arrays.");
            if (src.ElemType == dst.ElemType && src.ElemUnit == dst.ElemUnit && (src.ElemDefined || !dst.ElemDefined))
            {
                dst.CopyElementsFrom(src, (int)n);
                return Value.MakeUndefined();
            }
            for (long i = 0; i < n; i++)
            {
                if (!dst.TryConvertElement(src.GetUnchecked(i), out var stored, out _, out var message))
                    throw new InvalidOperationException(message ?? "__array_copy: an element does not fit the target array.");
                dst.SetUnchecked(i, stored);
            }
            return Value.MakeUndefined();
        }
    }
}
