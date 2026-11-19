using System.Globalization;
using System.Text;
using fire.Bytecode;

namespace fire.Compiler
{
    /// <summary>
    /// The tables the compiler and the VM in C need exactly as the ones in C# have them (docs/ROADMAP_C_PORT.md, phase A 6), as files in `native/tables`:
    /// `forge tables &lt;folder&gt;` writes them from the C# code, which stays their source as long as it lives; the tests check that the files of the repository are
    /// what the code says (FIRE_TESTS_ONLY=tables). The `.def` files are lists of macro calls - a C file defines the macro and includes the file, any other
    /// tool reads the lines.
    /// </summary>
    public static class Tables
    {
        /// <summary>The files by name: their text, UTF-8 with line feeds.</summary>
        public static IReadOnlyDictionary<string, string> All() => new Dictionary<string, string>
        {
            ["opcodes.def"] = Opcodes(),
            ["errors.def"] = Errors(),
            ["natives.def"] = Natives(),
            ["bridges.def"] = Bridges(),
            ["prelude.fire"] = fire.Standard.Prelude.Source.Replace("\r\n", "\n"),
        };

        /// <summary>Writes the files into a folder (made if it is missing); the names of the files that changed.</summary>
        public static List<string> Write(string folder)
        {
            Directory.CreateDirectory(folder);
            var changed = new List<string>();
            foreach (var (name, text) in All())
            {
                string path = Path.Combine(folder, name);
                if (File.Exists(path) && File.ReadAllText(path) == text) continue;
                File.WriteAllText(path, text, new UTF8Encoding(false));
                changed.Add(name);
            }
            return changed;
        }

        private static string CString(string s)
        {
            var b = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') b.Append('\\').Append(c);
                else if (c == '\n') b.Append("\\n");
                else if (c == '\t') b.Append("\\t");
                else if (c < 0x20) b.Append("\\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture)).Append("\" \"");
                else b.Append(c);
            }
            return b.Append('"').ToString();
        }

        /// <summary>FIRE_OP(name, number, "sizes"): the opcodes in the order of their numbers; a character of the last argument per operand, its size in bytes
        /// (2: u16 little endian, 1: u8) - docs/spec/BYTECODE.md.</summary>
        public static string Opcodes()
        {
            var b = new StringBuilder();
            b.Append("/* The opcodes of the bytecode (docs/spec/BYTECODE.md): FIRE_OP(name, number, operands) - a character per operand, its size in bytes (2: u16 little endian, 1: u8).\n");
            b.Append(" * Written by `forge tables` from OpCode.cs and OperandSizes.cs; do not edit. */\n");
            foreach (var op in Enum.GetValues<OpCode>())
            {
                if (!OperandSizes.Table.TryGetValue(op, out var sizes)) throw new InvalidOperationException($"The opcode {op} has no operand sizes (OperandSizes.Table).");
                b.Append("FIRE_OP(").Append(op).Append(", ").Append((int)op).Append(", \"").Append(string.Concat(sizes.Select(s => s.ToString(CultureInfo.InvariantCulture)))).Append("\")\n");
            }
            return b.ToString();
        }

        /// <summary>FIRE_ERROR(code, area, "template"): the messages of the compiler that have a code (docs/tools/errors.md), `{*}` a part that varies.</summary>
        public static string Errors()
        {
            var b = new StringBuilder();
            b.Append("/* The messages of the compiler with a code (docs/tools/errors.md): FIRE_ERROR(code, area, template) - {*} in a template is a part that varies.\n");
            b.Append(" * First the general code of each area, then the messages in the order of the catalogue. Written by `forge tables` from ErrorCatalog; do not edit. */\n");
            foreach (var (code, area, template) in ErrorCatalog.All())
                b.Append("FIRE_ERROR(").Append(code).Append(", ").Append(area.ToString().ToUpperInvariant()).Append(", ").Append(CString(template)).Append(")\n");
            return b.ToString();
        }

        /// <summary>FIRE_NATIVE(index, "name", tryable): the native functions every program has, in the order they are registered (the index a `CallNative` of
        /// the prelude uses); the natives of imports follow them per program.</summary>
        public static string Natives()
        {
            var registry = NativeRegistry.CreateDefault();
            var b = new StringBuilder();
            b.Append("/* The native functions every program has: FIRE_NATIVE(index, name, tryable) - the index is the one `CallNative` uses; what a program imports follows.\n");
            b.Append(" * Written by `forge tables` from NativeRegistry.CreateDefault; do not edit. */\n");
            int i = 0;
            foreach (var name in registry.Names)
                b.Append("FIRE_NATIVE(").Append(i++).Append(", ").Append(CString(name)).Append(", ").Append(registry.IsTryable(name) ? 1 : 0).Append(")\n");
            return b.ToString();
        }

        /// <summary>FIRE_BRIDGE_NATIVE(import, index, name): the natives an import of the compiler registers behind those every program has (ImportedPreludes.Insert:
        /// graphics, then windows, then reflection - the natives of packages follow, the program file names them), each in the order of its registration. A program
        /// file does not name these (a call addresses them by index): the VM in C reads its natives from here.</summary>
        public static string Bridges()
        {
            var b = new StringBuilder();
            b.Append("/* The natives of the imports the compiler knows, behind those of natives.def in this order when a program imports them (ImportedPreludes.Insert): graphics,\n");
            b.Append(" * windows, reflection - FIRE_BRIDGE_NATIVE(import, index, name), the index within the import. Written by `forge tables` from GraphicsBridge.RegisterStubs,\n");
            b.Append(" * WindowsBridge.RegisterStubs and ReflectionNatives.Register; do not edit. */\n");
            void Group(string import, Action<NativeRegistry> register)
            {
                var registry = new NativeRegistry();
                register(registry);
                int i = 0;
                foreach (var name in registry.Names.OrderBy(registry.IndexOf))
                    b.Append("FIRE_BRIDGE_NATIVE(").Append(import).Append(", ").Append(i++).Append(", ").Append(CString(name)).Append(")\n");
            }
            Group("graphics", fire.Terminal.Bridge.GraphicsBridge.RegisterStubs);
            Group("windows", fire.Windows.Bridge.WindowsBridge.RegisterStubs);
            Group("reflection", fire.Runtime.ReflectionNatives.Register);
            return b.ToString();
        }
    }
}
