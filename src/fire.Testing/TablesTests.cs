using fire.Compiler;

/// <summary>The tables of native/tables (docs/ROADMAP_C_PORT.md, phase A 6; alone with FIRE_TESTS_ONLY=tables): the files of the repository are what the C# code says -
/// an opcode, a message or a native that was added without `forge tables native/tables` is a failed check that names the file.</summary>
static class TablesTests
{
    private static string Folder([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "native", "tables"));

    public static int Run()
    {
        Console.WriteLine("=== Tabellen (native/tables) ===");
        int failures = 0;
        void Check(string title, bool ok, string detail = "")
        {
            if (!ok) failures++;
            Console.WriteLine(ok ? $"OK: Tabellen: {title}" : $"FEHLER: Tabellen: {title}" + (detail.Length == 0 ? "" : "\n" + detail));
        }
        var tables = Tables.All();
        foreach (var (name, text) in tables)
        {
            string path = Path.Combine(Folder(), name);
            string onDisk = File.Exists(path) ? File.ReadAllText(path) : "";
            Check($"{name} ist der Stand des Codes", onDisk == text,
                onDisk.Length == 0 ? "  Die Datei fehlt: forge tables native/tables" : "  Die Datei ist veraltet (forge tables native/tables schreibt sie neu):\n" + TestCases.Difference(onDisk, text));
        }
        // what a reader in C relies on
        var ops = tables["opcodes.def"].Split('\n').Where(l => l.StartsWith("FIRE_OP(")).ToList();
        Check("opcodes.def: jeder Opcode einmal, die Nummern luecklos ab 0", ops.Count == Enum.GetValues<fire.Bytecode.OpCode>().Length
            && ops.Select((l, i) => l.Contains($", {i}, \"")).All(x => x));
        var natives = tables["natives.def"].Split('\n').Where(l => l.StartsWith("FIRE_NATIVE(")).ToList();
        Check("natives.def: print ist die erste native Funktion, die Indizes luecklos", natives.Count > 1 && natives[0].StartsWith("FIRE_NATIVE(0, \"print\"") && natives.Select((l, i) => l.StartsWith($"FIRE_NATIVE({i}, ")).All(x => x));
        var codes = tables["errors.def"].Split('\n').Where(l => l.StartsWith("FIRE_ERROR(")).Select(l => l.Substring(11, 5)).ToList();
        Check("errors.def: jeder Code einmal", codes.Count > 100 && codes.Distinct().Count() == codes.Count, string.Join(" ", codes.GroupBy(c => c).Where(g => g.Count() > 1).Select(g => g.Key)));
        Check("prelude.fire ist ein Programm fuer sich (wird uebersetzt)", Compiles(tables["prelude.fire"]));
        Console.WriteLine(failures == 0 ? "Alle Pruefungen der Tabellen bestanden." : $"FEHLER: {failures} Pruefung(en) der Tabellen fehlgeschlagen.");
        return failures;
    }

    private static bool Compiles(string prelude)
    {
        // (the linker puts the prelude in front of every program: an empty program is the prelude alone)
        try { new Linker().CompileAndLink(new[] { "" }); return prelude.Length > 1000; }
        catch (Exception) { return false; }
    }
}
