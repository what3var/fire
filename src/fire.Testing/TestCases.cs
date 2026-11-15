using System.Text;

/// <summary>
/// Test cases as files (docs/tools/testing.md "The cases as files", docs/ROADMAP_C_PORT.md L5): a group is a folder `tests/cases/&lt;group&gt;` of the repository, a case is
/// `&lt;number&gt;-&lt;slug&gt;.fire` - the source of the program, exactly, then a line `//# name: &lt;title&gt;` - and next to it `&lt;number&gt;-&lt;slug&gt;.expected`: what the program
/// prints (the lines of `print`, each ended by a line feed). The files are UTF-8 with line feeds and git never converts them (.gitattributes), so a case is the same program everywhere.
/// The test program in C# reads them, a runner in C will read the same files.
/// </summary>
static class TestCases
{
    public sealed record Case(string Id, string Name, string Source, string? Expected)
    {
        /// <summary>The file of the source; the expected output is next to it (`.expected`).</summary>
        public string File { get; init; } = "";
        /// <summary>The program ends with an exception that nothing catches: the message of the command line (`.unhandled` next to the source), else null.</summary>
        public string? Unhandled { get; init; }
        /// <summary>The output is not the same on every run or machine (`//# output: varies`): the case has no expected output, the engines are only compared with each other.</summary>
        public bool Varies { get; init; }
    }

    private const string NameTag = "//# name: ";
    private const string VariesTag = "//# output: varies";

    /// <summary>The folder `tests/cases` of the repository this test program was built from.</summary>
    public static string Root([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "tests", "cases"));

    /// <summary>The cases of a group in the order of their files.</summary>
    public static List<Case> Load(string group)
    {
        string dir = Path.Combine(Root(), group);
        var list = new List<Case>();
        if (!Directory.Exists(dir)) return list;
        foreach (var file in Directory.GetFiles(dir, "*.fire").OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal))
        {
            string text = System.IO.File.ReadAllText(file, Encoding.UTF8);
            // the lines `//# key: value` at the end are about the case (name: its title; note: a remark; output: varies - no expected output); the source is everything before them
            if (!text.EndsWith('\n')) throw new InvalidDataException($"{file}: a case ends with a line '{NameTag}<title>'.");
            int end = text.Length - 1;   // (the line feed that ends the last line)
            string? name = null;
            bool varies = false;
            while (true)
            {
                int start = text.LastIndexOf('\n', end - 1) + 1;
                if (start == 0 || !text.AsSpan(start, end - start).StartsWith("//# ")) break;
                string line = text.Substring(start, end - start);
                if (line.StartsWith(NameTag, StringComparison.Ordinal)) name = line.Substring(NameTag.Length);
                else if (line == VariesTag) varies = true;
                end = start - 1;
            }
            if (name == null) throw new InvalidDataException($"{file}: a case ends with a line '{NameTag}<title>'.");
            string source = text.Substring(0, end);
            string expectedFile = Path.ChangeExtension(file, ".expected");
            string unhandledFile = Path.ChangeExtension(file, ".unhandled");
            list.Add(new Case(Path.GetFileNameWithoutExtension(file), name, source, !varies && System.IO.File.Exists(expectedFile) ? System.IO.File.ReadAllText(expectedFile, Encoding.UTF8) : null)
            {
                File = file, Varies = varies,
                Unhandled = !varies && System.IO.File.Exists(unhandledFile) ? System.IO.File.ReadAllText(unhandledFile, Encoding.UTF8).TrimEnd('\n') : null,
            });
        }
        return list;
    }

    /// <summary>Records what a case prints (and the message of the exception it ends with, if it does) as its expected output: FIRE_TESTS_UPDATE=1.</summary>
    public static void Record(Case c, string output, string? unhandled)
    {
        var utf8 = new UTF8Encoding(false);
        System.IO.File.WriteAllText(Path.ChangeExtension(c.File, ".expected"), output, utf8);
        string unhandledFile = Path.ChangeExtension(c.File, ".unhandled");
        if (unhandled != null) System.IO.File.WriteAllText(unhandledFile, unhandled + "\n", utf8);
        else if (System.IO.File.Exists(unhandledFile)) System.IO.File.Delete(unhandledFile);
    }

    /// <summary>Where two texts differ first, for the report of a failed case.</summary>
    public static string Difference(string expected, string actual)
    {
        var e = expected.Split('\n'); var a = actual.Split('\n');
        for (int i = 0; i < Math.Max(e.Length, a.Length); i++)
        {
            string el = i < e.Length ? e[i] : "(nichts mehr)", al = i < a.Length ? a[i] : "(nichts mehr)";
            if (el != al) return $"  Zeile {i + 1}:\n  erwartet: {el}\n  erhalten: {al}";
        }
        return "";
    }

    /// <summary>The names and sources of cases as one text, to compare two sets of cases (the cases in the code against the cases in files).</summary>
    public static string Listing(IEnumerable<(string Name, string Source)> cases)
    {
        var b = new StringBuilder();
        foreach (var (name, source) in cases.OrderBy(c => c.Name, StringComparer.Ordinal).ThenBy(c => c.Source, StringComparer.Ordinal))
            b.Append("=== ").Append(name).Append('\n').Append(source).Append("\n=== end\n");
        return b.ToString();
    }

    /// <summary>A file name for a title: its first words in ASCII.</summary>
    public static string Slug(string name)
    {
        var b = new StringBuilder();
        foreach (char ch in name.Normalize(NormalizationForm.FormD))
        {
            if (b.Length >= 48) break;
            char c = char.ToLowerInvariant(ch);
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') b.Append(c);
            else if (c is ' ' or '-' or '_' or '/' or ':' or '.' or ',' or '(' or ')') { if (b.Length > 0 && b[^1] != '-') b.Append('-'); }
        }
        return b.ToString().Trim('-');
    }

    /// <summary>Writes a case into the folder of a group (its files are new, or replaced).</summary>
    public static void Write(string group, string id, string name, string source, string? expected)
    {
        if (name.Contains('\n') || name.Contains('\r')) throw new ArgumentException("The title of a case is one line.");
        string dir = Path.Combine(Root(), group);
        Directory.CreateDirectory(dir);
        var utf8 = new UTF8Encoding(false);
        System.IO.File.WriteAllText(Path.Combine(dir, id + ".fire"), source + "\n" + NameTag + name + "\n", utf8);
        if (expected != null) System.IO.File.WriteAllText(Path.Combine(dir, id + ".expected"), expected, utf8);
    }
}
