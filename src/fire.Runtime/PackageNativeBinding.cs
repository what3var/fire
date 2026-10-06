using System.Runtime.InteropServices;
using fire.Bytecode;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>
    /// Binds the natives of packages (docs/PACKAGE_NATIVES.md) to the virtual machine: they are C++ built into a shared library with the C ABI of native/abi/fire_pkg_abi.h.
    /// A native is registered as a function that loads its library on first use (by path, or - in a packed program - by name from the payload), copies the arguments into
    /// the plain data of the ABI, calls the entry point and copies the result back. Numbers, text, byte buffers and arrays of these cross; objects, lambdas and pointers do not.
    /// </summary>
    public static class PackageNativeBinding
    {
        /// <summary>The "library" of a native that the host runs itself (<c>host</c> in the manifest).</summary>
        public const string HostLibrary = "@host";

        [StructLayout(LayoutKind.Sequential)]
        private struct FireVal { public int Kind; public int Length; public long Bits; }

        private const int KindUndefined = 0, KindBool = 1, KindInt = 2, KindFloat = 3, KindChar = 4, KindString = 5, KindArray = 6, KindBuffer = 7;
        private static readonly int ValSize = Marshal.SizeOf<FireVal>();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CallFn(int index, IntPtr args, int argc, IntPtr result, IntPtr error, int errorSize);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int IntFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr NameFn(int index);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ArityFn(int index);

        private sealed class Library
        {
            public CallFn Call = null!;
            public Dictionary<string, (int Index, int Arity)> Functions = new();
            public string? Failure;
        }

        private static readonly Dictionary<string, Library> Libraries = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object Lock = new();

        /// <summary>Registers the natives <paramref name="names"/> (in this order: calls address natives by index); <paramref name="libraryFiles"/> are the file names of the libraries they
        /// live in, <paramref name="locate"/> finds a library file on this machine (null: the packed program's payload provides it by name).</summary>
        public static void Register(NativeRegistry natives, IReadOnlyList<string>? names, IReadOnlyList<string>? libraryFiles, Func<string, string?>? locate)
        {
            if (names == null) return;
            for (int i = 0; i < names.Count; i++)
            {
                string name = names[i];
                string file = libraryFiles != null && i < libraryFiles.Count ? libraryFiles[i] : "";
                if (natives.Has(name)) continue;
                if (file == HostLibrary)
                {
                    // the host runs it itself (`Sleep`: the VM waits and can be aborted)
                    natives.Register(name, HostNatives.Find(name) ?? (_ => throw new InvalidOperationException($"The native function '{name}' is run by the host, and this host does not provide it.")));
                    continue;
                }
                natives.Register(name, args => Invoke(name, file, args, locate));
            }
        }

        private static Library Load(string file, Func<string, string?>? locate)
        {
            lock (Lock)
            {
                if (Libraries.TryGetValue(file, out var done)) return done;
                var lib = new Library();
                Libraries[file] = lib;
                try
                {
                    if (file.Length == 0) throw new DllNotFoundException("the package has no native library");
                    string? path = locate?.Invoke(file);
                    IntPtr handle = path != null ? NativeLibrary.Load(path) : NativeLibrary.Load(file, typeof(PackageNativeBinding).Assembly, null);
                    var version = Marshal.GetDelegateForFunctionPointer<IntFn>(NativeLibrary.GetExport(handle, "fire_pkg_abi_version"));
                    if (version() != 1) throw new InvalidOperationException($"the library speaks version {version()} of the package ABI (this fire understands 1)");
                    lib.Call = Marshal.GetDelegateForFunctionPointer<CallFn>(NativeLibrary.GetExport(handle, "fire_pkg_call"));
                    var count = Marshal.GetDelegateForFunctionPointer<IntFn>(NativeLibrary.GetExport(handle, "fire_pkg_function_count"));
                    var nameOf = Marshal.GetDelegateForFunctionPointer<NameFn>(NativeLibrary.GetExport(handle, "fire_pkg_function_name"));
                    var arityOf = Marshal.GetDelegateForFunctionPointer<ArityFn>(NativeLibrary.GetExport(handle, "fire_pkg_function_arity"));
                    for (int i = 0, n = count(); i < n; i++)
                        if (Marshal.PtrToStringAnsi(nameOf(i)) is { } fn) lib.Functions[fn] = (i, arityOf(i));
                }
                catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or InvalidOperationException or ArgumentException)
                {
                    lib.Failure = ex.Message;
                }
                return lib;
            }
        }

        private static Value Invoke(string name, string file, Value[] args, Func<string, string?>? locate)
        {
            var lib = Load(file, locate);
            if (lib.Failure != null)
                throw new InvalidOperationException($"The native function '{name}' of a package cannot run in the virtual machine: its library '{file}' could not be loaded ({lib.Failure}). " +
                    "The compiler builds it from the C++ source of the package with a C++ compiler (g++, clang++ or cl): install one, or use a native build.");
            if (!lib.Functions.TryGetValue(name, out var fn))
                throw new InvalidOperationException($"The library '{file}' does not export the native function '{name}'.");
            if (fn.Arity != args.Length)
                throw new InvalidOperationException($"The native function '{name}' takes {fn.Arity} argument(s), called with {args.Length}.");

            var allocs = new List<IntPtr>();
            IntPtr argBlock = Marshal.AllocHGlobal(Math.Max(1, args.Length) * ValSize), result = Marshal.AllocHGlobal(ValSize), error = Marshal.AllocHGlobal(512);
            try
            {
                Marshal.WriteByte(error, 0);
                for (int i = 0; i < args.Length; i++) ToNative(args[i], argBlock + i * ValSize, allocs, name);
                int rc = lib.Call(fn.Index, argBlock, args.Length, result, error, 512);
                if (rc == 2)
                {
                    // the native throws an exception of the program: "ClassName\nmessage" (as UTF-8)
                    string text = Marshal.PtrToStringUTF8(error) ?? "";
                    int nl = text.IndexOf('\n');
                    string cls = nl < 0 ? text : text.Substring(0, nl), message = nl < 0 ? "" : text.Substring(nl + 1);
                    if (VM.CurrentThreadVm is { } vm) return vm.NativeFail(cls, message);
                    throw new InvalidOperationException($"{cls}: {message}");
                }
                if (rc != 0) throw new InvalidOperationException($"{name}: {Marshal.PtrToStringUTF8(error)}");
                return FromNative(result);
            }
            finally
            {
                foreach (var p in allocs) Marshal.FreeHGlobal(p);
                Marshal.FreeHGlobal(argBlock);
                Marshal.FreeHGlobal(result);
                Marshal.FreeHGlobal(error);
            }
        }

        private static void ToNative(Value v, IntPtr dest, List<IntPtr> allocs, string function)
        {
            var fv = new FireVal();
            switch (v.Kind)
            {
                case ValueKind.Undefined: fv.Kind = KindUndefined; break;
                case ValueKind.Bool: fv.Kind = KindBool; fv.Bits = v.AsBool() ? 1 : 0; break;
                case ValueKind.Int: fv.Kind = KindInt; fv.Bits = v.AsInt(); break;
                case ValueKind.Float: fv.Kind = KindFloat; fv.Bits = BitConverter.DoubleToInt64Bits(v.AsFloat()); break;
                case ValueKind.Char: fv.Kind = KindChar; fv.Bits = v.AsChar(); break;
                case ValueKind.String:
                {
                    string s = v.AsString();
                    IntPtr p = Marshal.StringToHGlobalUni(s);
                    allocs.Add(p);
                    fv.Kind = KindString; fv.Length = s.Length; fv.Bits = p.ToInt64();
                    break;
                }
                case ValueKind.Array:
                {
                    var arr = v.AsArray();
                    IntPtr items = Marshal.AllocHGlobal(Math.Max(1, arr.Length) * ValSize);
                    allocs.Add(items);
                    for (int i = 0; i < arr.Length; i++) ToNative(arr.Items[i], items + i * ValSize, allocs, function);
                    fv.Kind = KindArray; fv.Length = arr.Length; fv.Bits = items.ToInt64();
                    break;
                }
                case ValueKind.Buffer:
                {
                    var buf = v.AsBuffer();
                    IntPtr p = Marshal.AllocHGlobal(Math.Max(1, buf.Length));
                    allocs.Add(p);
                    if (buf.Length > 0) Marshal.Copy(buf.Bytes, 0, p, buf.Length);
                    fv.Kind = KindBuffer; fv.Length = buf.Length; fv.Bits = p.ToInt64();
                    break;
                }
                default:
                    throw new InvalidOperationException($"An argument of the native function '{function}' is {v.Kind}: only numbers, bool, char, text, byte buffers and arrays of these can be passed to a package's native.");
            }
            Marshal.StructureToPtr(fv, dest, false);
        }

        private static Value FromNative(IntPtr src)
        {
            var fv = Marshal.PtrToStructure<FireVal>(src);
            switch (fv.Kind)
            {
                case KindUndefined: return Value.MakeUndefined();
                case KindBool: return Value.MakeBool(fv.Bits != 0);
                case KindInt: return Value.MakeInt(fv.Bits);
                case KindFloat: return Value.MakeFloat(BitConverter.Int64BitsToDouble(fv.Bits));
                case KindChar: return Value.MakeChar((char)fv.Bits);
                case KindString: return Value.MakeString(fv.Length == 0 ? "" : Marshal.PtrToStringUni(new IntPtr(fv.Bits), fv.Length));
                case KindArray:
                {
                    var arr = new ScriptArray(fv.Length);
                    for (int i = 0; i < fv.Length; i++) arr.Items[i] = FromNative(new IntPtr(fv.Bits) + i * ValSize);
                    return Value.MakeArray(arr);
                }
                case KindBuffer:
                {
                    var bytes = new byte[fv.Length];
                    if (fv.Length > 0) Marshal.Copy(new IntPtr(fv.Bits), bytes, 0, fv.Length);
                    return Value.MakeBuffer(new ByteBuffer(bytes, BitConverter.IsLittleEndian ? ByteOrder.Little : ByteOrder.Big));
                }
                default: throw new InvalidOperationException("A native function of a package returned a value of an unknown kind.");
            }
        }
    }
}
