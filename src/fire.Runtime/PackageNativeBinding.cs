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
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetHostFn(IntPtr host);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void VoidFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr NameFn(int index);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ArityFn(int index);

        private sealed class Library
        {
            public CallFn Call = null!;
            public Action? Reset;
            public Dictionary<string, (int Index, int Arity)> Functions = new();
            public string? Failure;
        }

        private static readonly Dictionary<string, Library> Libraries = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object Lock = new();
        private static readonly Dictionary<string, (string File, Func<string, string?>? Locate)> Locations = new(StringComparer.Ordinal);

        // For measuring natives outside the VM (docs/PACKAGE_NATIVES.md, "Tracing calls"): FIRE_PKG_TRACE names a file that gets a line per call of a native whose name
        // starts with FIRE_PKG_TRACE_PREFIX (default: every native) - the name and the arguments - so that a small C++ program can replay them against the library.
        private static readonly string? TracePath = Environment.GetEnvironmentVariable("FIRE_PKG_TRACE");
        private static readonly string TracePrefix = Environment.GetEnvironmentVariable("FIRE_PKG_TRACE_PREFIX") ?? "";
        private static StreamWriter? _trace;

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
                lock (Lock) Locations[name] = (file, locate);
                if (file == HostLibrary)
                {
                    // the host runs it itself (`Sleep`: the VM waits and can be aborted)
                    natives.Register(name, HostNatives.Find(name) ?? (_ => throw new InvalidOperationException($"The native function '{name}' is run by the host, and this host does not provide it.")));
                    continue;
                }
                natives.Register(name, args => Invoke(name, file, args, locate));
            }
        }

        /// <summary>Calls a native of a package by name from the host (a host function that works with the library, like the waiting functions of the devices: the VM waits, the library looks).</summary>
        internal static Value Call(string name, params Value[] args)
        {
            (string File, Func<string, string?>? Locate) where;
            lock (Lock)
                if (!Locations.TryGetValue(name, out where)) throw new InvalidOperationException($"The native function '{name}' of a package is not registered.");
            return Invoke(name, where.File, args, where.Locate);
        }

        /// <summary>A program has ended: every loaded library forgets what the program left behind (`fire_pkg_reset`: open streams, ...).</summary>
        internal static void ResetLibraries()
        {
            Library[] loaded;
            lock (Lock) loaded = Libraries.Values.ToArray();
            foreach (var lib in loaded)
                try { lib.Reset?.Invoke(); } catch (Exception) { /* a library that cannot reset is not the end of the host */ }
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
                    // optional exports: the host of the VM tells the library what it decides (policy, console), and a program's end resets it
                    if (NativeLibrary.TryGetExport(handle, "fire_pkg_set_host", out var setHost))
                        Marshal.GetDelegateForFunctionPointer<SetHostFn>(setHost)(PackageHost.Block);
                    if (NativeLibrary.TryGetExport(handle, "fire_pkg_reset", out var reset))
                        lib.Reset = Marshal.GetDelegateForFunctionPointer<VoidFn>(reset).Invoke;
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

            if (TracePath != null && name.StartsWith(TracePrefix, StringComparison.Ordinal)) Trace(name, args);
            // the blocks for the arguments, the result and the message stay with the thread (a call is a few hundred ns, not a few allocations); a native
            // that calls back into the VM on the same thread gets blocks of its own (depth)
            var blocks = CallBlocks.For(args.Length);
            List<IntPtr>? allocs = null;
            List<GCHandle>? pins = null;
            IntPtr argBlock = blocks.Args, result = blocks.Result, error = blocks.Error;
            try
            {
                Marshal.WriteByte(error, 0);
                for (int i = 0; i < args.Length; i++) ToNative(args[i], argBlock + i * ValSize, ref allocs, ref pins, name);
                int rc = lib.Call(fn.Index, argBlock, args.Length, result, error, 512);
                if (rc == 2)
                {
                    // the native throws an exception of the program: "ClassName\nmessage" (as UTF-8)
                    string text = Marshal.PtrToStringUTF8(error) ?? "";
                    int nl = text.IndexOf('\n');
                    string cls = nl < 0 ? text : text.Substring(0, nl), message = nl < 0 ? "" : text.Substring(nl + 1);
                    if (VM.CurrentThreadVm is { } vm) return vm.NativeFail(cls, message);
                    throw new ScriptErrorException(cls, message);   // the VM throws it as an exception of the program (its class, when the program has it)
                }
                if (rc != 0) throw new InvalidOperationException($"{name}: {Marshal.PtrToStringUTF8(error)}");
                return FromNative(result);
            }
            finally
            {
                if (allocs != null) foreach (var p in allocs) Marshal.FreeHGlobal(p);
                if (pins != null) foreach (var pin in pins) pin.Free();
                CallBlocks.Release();
            }
        }

        // The memory of the calls of a thread: per depth (a native may call back into the VM, which calls a native again) blocks for the arguments, the result
        // and the message, grown when a call has more arguments. Never freed (a few hundred bytes per thread and depth).
        private sealed class CallBlocks
        {
            public IntPtr Args, Result, Error;
            public int Capacity;
            [ThreadStatic] private static List<CallBlocks>? _stack;
            [ThreadStatic] private static int _depth;

            public static CallBlocks For(int argc)
            {
                var stack = _stack ??= new List<CallBlocks>();
                if (_depth == stack.Count)
                    stack.Add(new CallBlocks { Result = Marshal.AllocHGlobal(ValSize), Error = Marshal.AllocHGlobal(512) });
                var b = stack[_depth++];
                if (b.Capacity < Math.Max(1, argc))
                {
                    if (b.Args != IntPtr.Zero) Marshal.FreeHGlobal(b.Args);
                    b.Capacity = Math.Max(8, argc);
                    b.Args = Marshal.AllocHGlobal(b.Capacity * ValSize);
                }
                return b;
            }

            public static void Release() { if (_depth > 0) _depth--; }
        }

        // one line of the trace: the name and the arguments, tab separated - i:int f:float(bits, hex) b:bool c:char s:text(base64 of UTF-8) B:buffer(length; the bytes
        // are not kept) a:count followed by the items in the next fields - u: undefined
        private static void Trace(string name, Value[] args)
        {
            lock (Lock)
            {
                _trace ??= new StreamWriter(TracePath!, append: false) { AutoFlush = false };
                var sb = new System.Text.StringBuilder(name);
                foreach (var a in args) TraceValue(sb, a);
                _trace.WriteLine(sb.ToString());
                _trace.Flush();
            }
        }

        private static void TraceValue(System.Text.StringBuilder sb, Value v)
        {
            sb.Append('\t');
            switch (v.Kind)
            {
                case ValueKind.Int: sb.Append("i:").Append(v.AsInt()); break;
                case ValueKind.Float: sb.Append("f:").Append(BitConverter.DoubleToInt64Bits(v.AsFloat()).ToString("x")); break;
                case ValueKind.Bool: sb.Append("b:").Append(v.AsBool() ? 1 : 0); break;
                case ValueKind.Char: sb.Append("c:").Append((int)v.AsChar()); break;
                case ValueKind.String: sb.Append("s:").Append(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(v.AsString()))); break;
                case ValueKind.Buffer: sb.Append("B:").Append(v.AsBuffer().Length); break;
                case ValueKind.Array:
                {
                    var arr = v.AsArray();
                    sb.Append("a:").Append(arr.Length);
                    for (int i = 0; i < arr.Length; i++) TraceValue(sb, arr.Items[i]);
                    break;
                }
                default: sb.Append("u:"); break;
            }
        }

        private static void ToNative(Value v, IntPtr dest, ref List<IntPtr>? allocs, ref List<GCHandle>? pins, string function)
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
                    (allocs ??= new List<IntPtr>()).Add(p);
                    fv.Kind = KindString; fv.Length = s.Length; fv.Bits = p.ToInt64();
                    break;
                }
                case ValueKind.Array:
                {
                    var arr = v.AsArray();
                    IntPtr items = Marshal.AllocHGlobal(Math.Max(1, arr.Length) * ValSize);
                    (allocs ??= new List<IntPtr>()).Add(items);
                    for (int i = 0; i < arr.Length; i++) ToNative(arr.Items[i], items + i * ValSize, ref allocs, ref pins, function);
                    fv.Kind = KindArray; fv.Length = arr.Length; fv.Bits = items.ToInt64();
                    break;
                }
                case ValueKind.Buffer:
                {
                    // by reference: pinned for the call, the native reads and writes the bytes in place
                    var buf = v.AsBuffer();
                    var pin = GCHandle.Alloc(buf.Bytes, GCHandleType.Pinned);
                    (pins ??= new List<GCHandle>()).Add(pin);
                    fv.Kind = KindBuffer; fv.Length = buf.Length; fv.Bits = pin.AddrOfPinnedObject().ToInt64();
                    break;
                }
                default:
                    throw new InvalidOperationException($"An argument of the native function '{function}' is {v.Kind}: only numbers, bool, char, text, byte buffers and arrays of these can be passed to a package's native.");
            }
            Marshal.WriteInt32(dest, fv.Kind);
            Marshal.WriteInt32(dest, 4, fv.Length);
            Marshal.WriteInt64(dest, 8, fv.Bits);
        }

        private static Value FromNative(IntPtr src)
        {
            var fv = new FireVal { Kind = Marshal.ReadInt32(src), Length = Marshal.ReadInt32(src, 4), Bits = Marshal.ReadInt64(src, 8) };
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
