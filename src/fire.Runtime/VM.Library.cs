using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using fire.Ast;
using fire.Standard;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>
    /// The VM's side of `Library` (docs/language/18-native-code.md, <see cref="LibraryMethods"/>): `NativeLibrary` loads, a delegate made for the C signature calls
    /// (the same technique as `#extern`, BuildNonGenericDelegateType). The types are those of the native backend (fire_rt.h libCall, the generated thunks): `int` is 64
    /// bits, `int[32]` a C int; `bool` is one byte, `char` UTF-16, `string` UTF-8 both ways (a returned string is copied, never freed), pointers are addresses.
    /// </summary>
    public sealed partial class VM
    {
        private static readonly ConcurrentDictionary<(long Fn, string Signature), (Delegate Call, List<TypeRef> Params, TypeRef? Return)> LibraryDelegates = new();

        internal Value LibraryCall(Value[] a)
        {
            switch ((LibraryMethod)a[0].AsInt())
            {
                case LibraryMethod.Load:
                    try { return Value.MakeInt((long)NativeLibrary.Load(a[1].AsString())); }
                    catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or ArgumentException or InvalidOperationException) { return Value.MakeInt(0); }
                case LibraryMethod.Symbol:
                    return Value.MakeInt(NativeLibrary.TryGetExport((IntPtr)a[1].AsInt(), a[2].AsString(), out var address) ? (long)address : 0);
                case LibraryMethod.Arity:
                    return Value.MakeInt(LibraryMethods.Parse(a[1].AsString()) is { } parsed ? parsed.Params.Count : -1);
                case LibraryMethod.Call:
                    return LibraryInvoke(a[1].AsInt(), a[2].AsString(), a[3].Kind == ValueKind.Array ? a[3].AsArray().Items : Array.Empty<Value>());
                default:
                    throw new InvalidOperationException($"{LibraryMethods.NativeName}: unknown function {a[0]}.");
            }
        }

        private static Type LibraryClrType(TypeRef t) =>
            t.PointerDepth > 0 || t.BaseName == "string" ? typeof(IntPtr) : t.BaseName switch
            {
                "bool" => typeof(byte),
                "char" => typeof(ushort),
                "float" => t.BitWidth == 32 ? typeof(float) : typeof(double),
                _ => t.BitWidth switch { 8 => typeof(sbyte), 16 => typeof(short), 32 => typeof(int), _ => typeof(long) },
            };

        private Value LibraryInvoke(long fn, string signature, Value[] args)
        {
            var entry = LibraryDelegates.GetOrAdd((fn, LibraryMethods.Normalize(signature)), key =>
            {
                var (ret, ps) = LibraryMethods.Parse(key.Signature) ?? throw new InvalidOperationException($"The signature '{signature}' is not valid.");
                var types = ps.ConvertAll(LibraryClrType).ToArray();
                var delegateType = BuildNonGenericDelegateType(types, ret == null ? typeof(void) : LibraryClrType(ret), "lib");
                return (Marshal.GetDelegateForFunctionPointer((IntPtr)key.Fn, delegateType), ps, ret);
            });
            if (args.Length != entry.Params.Count)
                throw new ScriptErrorException("ArgumentException", $"The function expects {entry.Params.Count} argument(s), got {args.Length}.");
            var native = new object?[args.Length];
            var cleanups = new List<Action>();
            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    var t = entry.Params[i];
                    var v = args[i];
                    if (t.PointerDepth > 0)
                    {
                        if (v.Kind == ValueKind.Pointer)
                        {
                            var (one, more) = MarshalArgsOut(new[] { v });
                            native[i] = one[0];
                            cleanups.AddRange(more);
                        }
                        else native[i] = v.Kind == ValueKind.Undefined ? IntPtr.Zero : (IntPtr)LibraryInt(v);
                    }
                    else if (t.BaseName == "string")
                    {
                        IntPtr s = v.Kind == ValueKind.Undefined ? IntPtr.Zero : Marshal.StringToCoTaskMemUTF8(v.Kind == ValueKind.String ? v.AsString() : throw new ScriptErrorException("ArgumentException", "A string parameter of a native function expects a string."));
                        native[i] = s;
                        if (s != IntPtr.Zero) cleanups.Add(() => Marshal.FreeCoTaskMem(s));
                    }
                    else native[i] = t.BaseName switch
                    {
                        "bool" => (byte)(v.Kind == ValueKind.Bool ? (v.AsBool() ? 1 : 0) : LibraryInt(v) != 0 ? 1 : 0),
                        "char" => (ushort)LibraryInt(v),
                        "float" => t.BitWidth == 32 ? (object)(float)LibraryFloat(v) : LibraryFloat(v),
                        _ => t.BitWidth switch { 8 => (sbyte)LibraryInt(v), 16 => (short)LibraryInt(v), 32 => (int)LibraryInt(v), _ => (object)LibraryInt(v) },
                    };
                }
                object? result;
                try { result = entry.Call.DynamicInvoke(native); }
                catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException != null) { throw ex.InnerException; }
                var r = entry.Return;
                if (r == null) return Value.MakeUndefined();
                if (r.PointerDepth > 0) return Value.MakeInt((long)(IntPtr)result!);
                return r.BaseName switch
                {
                    "string" => (IntPtr)result! == IntPtr.Zero ? Value.MakeUndefined() : Value.MakeString(Marshal.PtrToStringUTF8((IntPtr)result!) ?? ""),
                    "bool" => Value.MakeBool((byte)result! != 0),
                    "char" => Value.MakeChar((char)(ushort)result!),
                    "float" => Value.MakeFloat(result is float f ? f : (double)result!),
                    _ => Value.MakeInt(Convert.ToInt64(result)),
                };
            }
            finally { foreach (var cleanup in cleanups) cleanup(); }
        }

        private static long LibraryInt(Value v) => v.Kind switch
        {
            ValueKind.Int => v.AsInt(),
            ValueKind.Bool => v.AsBool() ? 1 : 0,
            ValueKind.Char => v.AsChar(),
            ValueKind.Float => (long)v.AsFloat(),
            _ => throw new ScriptErrorException("ArgumentException", "A native function was called with an argument that is not a number."),
        };

        private static double LibraryFloat(Value v) => v.Kind switch
        {
            ValueKind.Float => v.AsFloat(),
            ValueKind.Int => v.AsInt(),
            _ => throw new ScriptErrorException("ArgumentException", "A native function was called with an argument that is not a number."),
        };
    }
}
