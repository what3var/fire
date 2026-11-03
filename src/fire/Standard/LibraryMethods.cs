using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using fire.Ast;
using fire.Values;

namespace fire.Standard
{
    /// <summary>The functions behind the class `Library` (IDs for <see cref="LibraryMethods.NativeName"/>, fixed values - never renumber).</summary>
    public enum LibraryMethod
    {
        /// <summary>`(1, path)`: loads a native library - its handle (an int), 0 when it cannot be loaded, -1 for a fire plugin the program cannot load (not linkage "shared").</summary>
        Load = 1,
        /// <summary>`(2, handle, name)`: the address of a function of the library, 0 when it has none of that name.</summary>
        Symbol = 2,
        /// <summary>`(3, signature)`: the number of parameters of a C signature; -1: not a signature, -2: one the native build has no thunk for.</summary>
        Arity = 3,
        /// <summary>`(4, address, signature, args)`: calls the function with the arguments converted as the signature says.</summary>
        Call = 4,
        /// <summary>`(5, handle, path)`: a fire plugin (ROADMAP 9 phase D b) - its classes registered: an array of their names; undefined: the library is no fire plugin;
        /// a string: why it does not fit the program.</summary>
        Plugin = 5,
    }

    /// <summary>
    /// The class `Library` of the prelude (always there, docs/language/18-native-code.md): a native library (C ABI) loaded while the program runs, its functions by name
    /// and C signature as lambdas - `Library.Load("libm.so.6").Get("cos", "float(float)")`. One native function `__LibCall(id, ...)`: the VM loads with NativeLibrary
    /// and calls through a delegate made for the signature (fire.Runtime, VM.LibraryCall); the native backend with dlopen/LoadLibrary and a thunk per signature that
    /// is a text of the program (fire_rt.h: libCall, the generated fire_libThunk). The errors are the same in both (they are thrown here, in fire).
    /// </summary>
    public static class LibraryMethods
    {
        public const string NativeName = "__LibCall";

        /// <summary>The most parameters a function of a library can have.</summary>
        public const int MaxParams = 8;

        /// <summary>The implementation of the VM (set by fire.Runtime; the compiler only needs the name).</summary>
        public static Func<Value[], Value>? Implementation;

        public static Value Call(Value[] args) =>
            (Implementation ?? throw new InvalidOperationException("Library: native libraries can only be loaded by a running program."))(args);

        private static string Lambdas()
        {
            var sb = new StringBuilder();
            for (int n = 0; n <= MaxParams; n++)
            {
                string ps = string.Join(", ", Enumerable.Range(0, n).Select(i => $"any a{i}"));
                string xs = string.Join(", ", Enumerable.Range(0, n).Select(i => $"a{i}"));
                sb.Append($"                    if (n == {n}) {{ return ({ps}) => {NativeName}({(int)LibraryMethod.Call}, fn, signature, [{xs}]) }}\n");
            }
            return sb.ToString();
        }

        public static string PreludeSource { get; } = $$"""

            /// A native library that cannot be loaded, a function it does not have, a signature that is not one (`Library.Load`, `Get`).
            class LibraryException : Exception {
                construct(string message) { this.message = message }
            }

            /// A native library (C ABI) loaded while the program runs: its functions by name and C signature as lambdas (docs/language/18-native-code.md).
            class Library {
                private int handle
                private string path
                private any classes
                construct(int handle, string path) { this.handle = handle; this.path = path }

                /// Loads the native library at `path` (or a name the system finds: "libm.so.6", "kernel32.dll"); a LibraryException when it cannot.
                /// A fire plugin (`forge build --linkage plugin`, its `.fireplugin` file or the library next to it): its classes become part of the program
                /// (Reflect.New, Type.Named, `is of`, catch) - a LibraryException when it does not fit the program.
                static Library Load(string path) {
                    var handle = {{NativeName}}({{(int)LibraryMethod.Load}}, path)
                    if (handle == 0) { throw new LibraryException("The library '" + path + "' could not be loaded.") }
                    if (handle == -1) { throw new LibraryException("The library '" + path + "' is a fire plugin: this program cannot load fire plugins - build it with linkage \"shared\".") }
                    var lib = new Library(handle, path)
                    var plugin = {{NativeName}}({{(int)LibraryMethod.Plugin}}, handle, path)
                    if (plugin is of string) { throw new LibraryException(plugin) }
                    if (plugin is of int) { throw new LibraryException("The library '" + path + "' is a fire plugin: this program cannot load fire plugins - build it with linkage \"shared\".") }
                    lib.classes = plugin
                    return lib
                }

                /// The classes of a fire plugin (their full names); empty for a C library.
                any Classes() {
                    if (this.classes == undefined) { return [] }
                    return this.classes
                }

                /// Is the library a fire plugin?
                bool IsPlugin { get { return this.classes != undefined } }

                /// The path (or name) the library was loaded from.
                string Path { get { return this.path } }

                /// The function `name` of the library with the C signature `signature` - the result type, then the parameter types in parentheses:
                /// "int[32](string, float)" (bool, int, int[8|16|32|64], float, float[32], char, string, pointers with `*`, void) - as a lambda that calls it.
                any Get(string name, string signature) {
                    var n = {{NativeName}}({{(int)LibraryMethod.Arity}}, signature)
                    if (n == -1) { throw new LibraryException("The signature '" + signature + "' is not valid: write the result type and the parameter types in C order, like \"int[32](string, float)\" (bool, int, int[8|16|32|64], float, float[32], char, string, pointers with *, void).") }
                    if (n == -2) { throw new LibraryException("The signature '" + signature + "' is not known to this native build: pass it to Get as a text constant.") }
                    if (n > {{MaxParams}}) { throw new LibraryException("The signature '" + signature + "' has more than {{MaxParams}} parameters.") }
                    var fn = {{NativeName}}({{(int)LibraryMethod.Symbol}}, this.handle, name)
                    if (fn == 0) { throw new LibraryException("The library '" + this.path + "' has no function '" + name + "'.") }
            {{Lambdas()}}                    return undefined
                }
            }

            """;

        // ---- C signatures ("int[32](string, float)") ----------------------------------------------------------------------------------------------------

        /// <summary>A signature without white space (the key of the thunks of the native backend).</summary>
        public static string Normalize(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (char c in text) if (!char.IsWhiteSpace(c)) sb.Append(c);
            return sb.ToString();
        }

        /// <summary>Parses a C signature: the result type (null: void) and the parameter types; null when it is not one. The same grammar as fire_rt.h `fire_libSigArity`.</summary>
        public static (TypeRef? Return, List<TypeRef> Params)? Parse(string text)
        {
            string s = Normalize(text);
            int pos = 0;
            TypeRef? Type(bool allowVoid, out bool isVoid)
            {
                isVoid = false;
                int start = pos;
                while (pos < s.Length && char.IsAsciiLetterLower(s[pos])) pos++;
                string name = s.Substring(start, pos - start);
                int? width = null;
                if (pos < s.Length && s[pos] == '[')
                {
                    int close = s.IndexOf(']', pos);
                    if (close < 0 || !int.TryParse(s.AsSpan(pos + 1, close - pos - 1), out int w)) return null;
                    width = w;
                    pos = close + 1;
                }
                int depth = 0;
                while (pos < s.Length && s[pos] == '*') { depth++; pos++; }
                if (name == "void")
                {
                    if (width != null) return null;
                    if (depth == 0) { isVoid = allowVoid; return null; }
                    return new TypeRef("void", null, depth);
                }
                bool ok = name switch
                {
                    "bool" or "char" or "string" => width == null,
                    "int" => width is null or 8 or 16 or 32 or 64,
                    "float" => width is null or 32 or 64,
                    _ => false,
                };
                return ok ? new TypeRef(name, width, depth) : null;
            }
            var ret = Type(true, out bool retVoid);
            if (ret == null && !retVoid) return null;
            if (pos >= s.Length || s[pos] != '(') return null;
            pos++;
            var ps = new List<TypeRef>();
            if (pos < s.Length && s[pos] == ')') pos++;
            else
            {
                while (true)
                {
                    var t = Type(ps.Count == 0, out bool pVoid);
                    if (pVoid && pos < s.Length && s[pos] == ')') { pos++; break; }   // `int(void)`
                    if (t == null) return null;
                    ps.Add(t);
                    if (pos < s.Length && s[pos] == ',') { pos++; continue; }
                    if (pos < s.Length && s[pos] == ')') { pos++; break; }
                    return null;
                }
            }
            return pos == s.Length ? (ret, ps) : null;
        }
    }
}
