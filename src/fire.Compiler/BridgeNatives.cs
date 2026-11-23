using fire.Bytecode;
using fire.Values;

namespace fire.Compiler
{
    /// <summary>
    /// The natives of the imports the compiler knows (graphics, windows, reflection) as names, in their order - from native/tables/bridges.def (carried by forge as
    /// the resource cvm/tables/bridges.def), as forge-c takes them. Compiling needs only their names and indexes; what runs them is the VM in C (and the C library
    /// of graphics and windows). Since M1 the file is the source of the names (it was written from the C# bridges before).
    /// </summary>
    public static class BridgeNatives
    {
        private static Dictionary<string, List<string>>? _names;
        private static readonly object Lock = new();

        /// <summary>The natives of an import ("graphics", "windows", "reflection") in their order.</summary>
        public static IReadOnlyList<string> Of(string import)
        {
            lock (Lock)
            {
                _names ??= Read();
                return _names.TryGetValue(import, out var list) ? list : new List<string>();
            }
        }

        /// <summary>Registers the natives of an import as names (a call of one at compile time does nothing; the program runs in `fire`).</summary>
        public static void Register(NativeRegistry natives, string import)
        {
            foreach (string name in Of(import)) natives.Register(name, args => Value.MakeUndefined());
        }

        private static Dictionary<string, List<string>> Read()
        {
            var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            using var stream = typeof(BridgeNatives).Assembly.GetManifestResourceStream("cvm/tables/bridges.def")
                ?? throw new InvalidOperationException("forge carries no tables/bridges.def (the resource cvm/tables/bridges.def).");
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                // FIRE_BRIDGE_NATIVE(import, index, "name")
                if (!line.StartsWith("FIRE_BRIDGE_NATIVE(", StringComparison.Ordinal)) continue;
                string inner = line.Substring("FIRE_BRIDGE_NATIVE(".Length, line.LastIndexOf(')') - "FIRE_BRIDGE_NATIVE(".Length);
                var parts = inner.Split(',', 3);
                string import = parts[0].Trim();
                int index = int.Parse(parts[1].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                string name = parts[2].Trim().Trim('"');
                if (!result.TryGetValue(import, out var list)) result[import] = list = new List<string>();
                if (index != list.Count) throw new InvalidDataException($"tables/bridges.def: {import} {index} out of order.");
                list.Add(name);
            }
            return result;
        }
    }
}
