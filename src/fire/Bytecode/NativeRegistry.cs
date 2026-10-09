using System;
using System.Collections.Generic;
using fire.Values;

namespace fire.Bytecode
{
    /// <summary>A native function (implemented in C#), callable via the
    /// CALL_NATIVE opcode through its index in the registry.</summary>
    public delegate Value NativeFunction(Value[] args);

    /// <summary>A "tryable" native function - registered for APIs whose
    /// success is not guaranteed (timeouts, unavailable hardware,
    /// etc. - typically IO: serial ports, network, files).
    /// Callable ONLY via `try Name(...)` (see SPEC 8.1.3) - returns on
    /// success `true` and the result value in `result`, on failure/
    /// timeout `false` (then `result` is discarded, the script sees
    /// `undefined`). DELIBERATELY not an exception mechanism (like C#'s own
    /// `TryParse` pattern) - the actual timeout/error logic lies
    /// entirely with the registering host/framework, not in the language
    /// itself. Any timeout parameter is a perfectly normal Value
    /// argument with a unit (`Value.Unit` is already publicly readable -
    /// the host implementation reads it itself, e.g. to tell "500ms" from
    /// "2s").</summary>
    public delegate bool TryableNativeFunction(Value[] args, out Value result);

    /// <summary>
    /// Registry of native functions. This is deliberately the only place through which
    /// the bytecode talks to the "outside world" - the reserved extension point
    /// for later operating-system/host APIs (file access, console, network, ...),
    /// without anything in the bytecode format having to change for that. Currently
    /// filled only with "print", for testing the VM.
    /// </summary>
    public sealed class NativeRegistry
    {
        private readonly List<NativeFunction> _functions = new();
        private readonly Dictionary<string, int> _indexByName = new();

        private readonly List<TryableNativeFunction> _tryableFunctions = new();
        private readonly Dictionary<string, int> _tryableIndexByName = new();

        public NativeRegistry()
        {

        }

        public NativeRegistry(NativeRegistry sourceToCopy)
        {
            _functions = sourceToCopy._functions;
            _indexByName = sourceToCopy._indexByName;
            _tryableFunctions = sourceToCopy._tryableFunctions;
            _tryableIndexByName = sourceToCopy._tryableIndexByName;
        }

        public int Register(string name, NativeFunction fn)
        {
            int idx = _functions.Count;
            _functions.Add(fn);
            _indexByName[name] = idx;
            return idx;
        }

        /// <summary>Registers several related native functions at
        /// once, all under the same name PREFIX (e.g. for a
        /// self-contained API group such as a graphics/console bridge) - the
        /// name actually registered for each entry is
        /// `prefix + suffix` (example: `RegisterGroup("__GRPH", new()
        /// { ["set"] = ..., ["get"] = ... })` registers `__GRPHset` and
        /// `__GRPHget`). Returns the list of names created this way (in
        /// the iteration order of `functions`) - useful for checking/
        /// logging which names were created, or for reusing them e.g. in
        /// a generated preprocessor/prelude source text
        /// directly.</summary>
        public IReadOnlyList<string> RegisterGroup(string prefix, IReadOnlyDictionary<string, NativeFunction> functions)
        {
            var names = new List<string>(functions.Count);
            foreach (var (suffix, fn) in functions)
            {
                string name = prefix + suffix;
                Register(name, fn);
                names.Add(name);
            }
            return names;
        }

        /// <summary>Registers a function callable ONLY via `try Name(...)`
        /// (see TryableNativeFunction docs) - a normal call
        /// `Name(...)` WITHOUT 'try' is a compile error for this name
        /// (see Resolver), no automatic fallback to "throws on
        /// failure".</summary>
        public int RegisterTryable(string name, TryableNativeFunction fn)
        {
            int idx = _tryableFunctions.Count;
            _tryableFunctions.Add(fn);
            _tryableIndexByName[name] = idx;
            return idx;
        }

        public int IndexOf(string name) =>
            _indexByName.TryGetValue(name, out var idx)
                ? idx
                : throw new InvalidOperationException($"No native function named '{name}' is registered.");

        public int TryableIndexOf(string name) =>
            _tryableIndexByName.TryGetValue(name, out var idx)
                ? idx
                : throw new InvalidOperationException($"No 'tryable' native function named '{name}' is registered.");

        public bool Has(string name) => _indexByName.ContainsKey(name);

        public bool IsTryable(string name) => _tryableIndexByName.ContainsKey(name);

        public IEnumerable<string> Names => _indexByName.Keys;
        public IEnumerable<string> TryableNames => _tryableIndexByName.Keys;

        public NativeFunction this[int index] => _functions[index];
        public TryableNativeFunction TryableAt(int index) => _tryableFunctions[index];

        public static NativeRegistry CreateDefault()
        {
            var registry = new NativeRegistry();
            registry.Register("print", args =>
            {
                Console.WriteLine(args.Length > 0 ? args[0].ToString() : "");
                return Value.MakeUndefined();
            });
            registry.RegisterBaseTypeNatives();
            return registry;
        }

        /// <summary>Registers the native functions to which the prelude maps the methods of the
        /// base-type extensions (`class extends string/char`, SPEC 5.5.1, 8.12) - ONE
        /// function per base type, the method passes its first argument (an ID). MUST be in every
        /// registry with which a program including the prelude is compiled/executed, and at
        /// the same position in the order as when compiling (native functions are jumped to via their
        /// index) - hence everywhere directly after `print`.</summary>
        /// <param name="resources">The files embedded in the program that runs (<c>CompiledProgram.Resources</c>); null while compiling.</param>
        public void RegisterBaseTypeNatives(System.Collections.Generic.IReadOnlyList<fire.Runtime.ResourceEntry>? resources = null)
        {
            Register(fire.Standard.StringMethods.NativeName, fire.Standard.StringMethods.Call);
            Register(fire.Standard.CharMethods.NativeName, fire.Standard.CharMethods.Call);
            Register(fire.Standard.ResourceMethods.NativeName, args => fire.Standard.ResourceMethods.Call(args, resources));
        }
    }
}
