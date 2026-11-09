using System;
using System.Collections.Generic;
using System.Linq;
using fire.Package.Manager;

namespace fire.Native
{
    /// <summary>Typed natives in C (docs/packages/README.md "Typed functions"): a function of a package in C whose manifest lists `"parameters"` takes C numbers for
    /// the parameters typed `int`, `int[32]`, `int[16]`, `float`, `bool` (everything else stays a Value) and returns one when `"returns"` is such a type. The
    /// generated code passes the C number of a slot directly; where a Value has to be converted (the VM's wrapper, the `fire_cn_` functions of linkage
    /// static/shared, a slot of unknown type) the conversion is lenient - as the functions read their Values before (`v.i`, `toR(v)`): a float gives its
    /// whole part to an int, anything else its `i`.</summary>
    public static class NativeCTypes
    {
        /// <summary>The C type of a manifest type, null for a Value.</summary>
        public static string? CType(string? type) => type?.Trim() switch
        {
            "int" or "int[64]" => "int64_t",
            "int[32]" => "int32_t",
            "int[16]" => "int16_t",
            "float" => "Real",
            "bool" => "bool",
            _ => null,
        };

        /// <summary>The function is typed: its manifest lists the parameters (and only a package in C can have one).</summary>
        public static bool IsTyped(PackageNativeFunction fn, bool isC) => isC && fn.Parameters != null;

        /// <summary>The C type of parameter <paramref name="i"/> of a typed function (null: a Value).</summary>
        public static string? ParamCType(PackageNativeFunction fn, int i) => fn.Parameters is { } ps && i < ps.Count ? CType(ps[i]) : null;

        /// <summary>The C type of the result of a typed function (null: a Value).</summary>
        public static string? ReturnCType(PackageNativeFunction fn) => fn.Parameters != null ? CType(fn.Returns) : null;

        /// <summary>The C expression of the parameter type <paramref name="cType"/> made of the Value <paramref name="value"/> (lenient, see the class).</summary>
        public static string FromValue(string cType, string value) => cType switch
        {
            "Real" => $"toR({value})",
            "bool" => $"({value}).i != 0",
            _ => $"({cType})fire_natI({value})",
        };

        /// <summary>The Value of a C result of the type <paramref name="cType"/>.</summary>
        public static string ToValue(string cType, string expr) => cType switch
        {
            "Real" => $"Float((Real)({expr}))",
            "bool" => $"Bool({expr})",
            _ => $"Int((int64_t)({expr}))",
        };

        /// <summary>A function with Values around a typed one (`Value name(Value a0, ..., OwnList* list) { return Int(fn((int64_t)fire_natI(a0), ...)); }`).</summary>
        public static string ValueWrapper(string name, string function, PackageNativeFunction fn, bool needsList, string prefix = "static ")
        {
            var ps = Enumerable.Range(0, fn.Arguments).Select(i => $"Value a{i}").Concat(needsList ? new[] { "OwnList* list" } : Array.Empty<string>()).ToList();
            var args = Enumerable.Range(0, fn.Arguments).Select(i => ParamCType(fn, i) is { } t ? FromValue(t, $"a{i}") : $"a{i}")
                .Concat(needsList ? new[] { "list" } : Array.Empty<string>());
            string call = $"{function}({string.Join(", ", args)})";
            return $"{prefix}Value {name}({(ps.Count == 0 ? "void" : string.Join(", ", ps))}) {{ return {(ReturnCType(fn) is { } r ? ToValue(r, call) : call)}; }}";
        }
    }
}
