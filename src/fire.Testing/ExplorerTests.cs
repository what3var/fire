using fire.Projects;

/// <summary>The logic behind the tool bar of spark's solution explorer (docs/tools/spark.md, "The tool bar of the solution explorer") without a window: the filter by name
/// (ExplorerTree.Filter: matches, their parents opened, what lies below a match), the way to a row (ExplorerTree.PathTo, Sync with Active Document) and what Show All Files adds
/// (ProjectFiles.HiddenEntries). Alone: FIRE_TESTS_ONLY=explorer (seconds); FIRE_TESTS_FILTER narrows it by the title of a check.</summary>
static partial class ExplorerTests
{
    private static int _failures;

    private static void Check(string title, bool ok, string? detail = null)
    {
        string? filter = Environment.GetEnvironmentVariable("FIRE_TESTS_FILTER");
        if (!string.IsNullOrWhiteSpace(filter) && !filter.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(f => title.Contains(f, StringComparison.OrdinalIgnoreCase))) return;
        if (!ok) _failures++;
        Console.WriteLine(ok ? $"OK: Explorer: {title}" : $"FEHLER: Explorer: {title}" + (detail == null ? "" : $"\n{detail}"));
    }

    private sealed class Row
    {
        public string Name = "";
        public List<Row> Children = new();
        public Row(string name, params Row[] children) { Name = name; Children.AddRange(children); }
        public override string ToString() => Name;
    }

    private static string Names(IEnumerable<Row> rows) => string.Join(",", rows.Select(r => r.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));

    public static int Run()
    {
        Console.WriteLine("=== Explorer: Filter, Sync und alle Dateien ===");
        _failures = 0;
        FilterChecks();
        HiddenChecks();
        FolderChecks();
        Console.WriteLine(_failures == 0 ? "Alle Explorer-Pruefungen bestanden." : $"FEHLER: {_failures} Explorer-Pruefung(en) fehlgeschlagen.");
        return _failures;
    }

    private static void FilterChecks()
    {
        // Solution > App > (src > (ui > Window.fire, helpers.fire), main.fire), Lib > (core > core.fire, util.fire)
        var window = new Row("Window.fire");
        var ui = new Row("ui", window);
        var helpers = new Row("helpers.fire");
        var src = new Row("src", ui, helpers);
        var main = new Row("main.fire");
        var app = new Row("App", src, main);
        var coreFile = new Row("core.fire");
        var core = new Row("core", coreFile);
        var util = new Row("util.fire");
        var lib = new Row("Lib", core, util);
        var solution = new Row("", app, lib);   // (the solution row has no name for the filter)
        var roots = new[] { solution };
        ExplorerFilterResult<Row>? F(string? q) => ExplorerTree.Filter(roots, r => r.Children, r => r.Name, q);

        Check("Matches: ohne Text passt alles", ExplorerTree.Matches("main.fire", "") && ExplorerTree.Matches("main.fire", null) && ExplorerTree.Matches("x", "   "));
        Check("Matches: Gross- und Kleinschreibung zaehlt nicht", ExplorerTree.Matches("Window.fire", "WINDOW") && ExplorerTree.Matches("Window.fire", "dow.F"));
        Check("Matches: jedes Wort muss vorkommen, in beliebiger Reihenfolge", ExplorerTree.Matches("main.fire", "fire main") && !ExplorerTree.Matches("main.fire", "main lib"));
        Check("Filter: leerer Text filtert nicht (null)", F("") == null && F("  ") == null && F(null) == null);

        var r = F("window")!;
        Check("Filter: der Treffer und seine Eltern bleiben, sonst nichts", Names(r.Shown) == ",App,src,ui,Window.fire", Names(r.Shown));
        Check("Filter: die Eltern des Treffers werden geoeffnet", r.Expanded.SetEquals(new[] { solution, app, src, ui }) && !r.Expanded.Contains(window), Names(r.Expanded));
        Check("Filter: Matches nennt den Treffer", r.Matches.Count == 1 && r.Matches[0] == window);

        r = F("FIRE")!;
        Check("Filter: alle Dateien eines Musters, die Ordner ohne Treffer fallen weg",
            Names(r.Shown) == ",App,core,core.fire,helpers.fire,Lib,main.fire,src,ui,util.fire,Window.fire" && r.Matches.Count == 5, Names(r.Shown));
        Check("Filter: die Treffer in der Reihenfolge des Baums", string.Join(",", r.Matches.Select(m => m.Name)) == "Window.fire,helpers.fire,main.fire,core.fire,util.fire");

        r = F("core")!;
        Check("Filter: unter einem passenden Ordner bleibt alles sichtbar", r.Shown.Contains(core) && r.Shown.Contains(coreFile) && !r.Shown.Contains(util) && !r.Shown.Contains(app), Names(r.Shown));
        Check("Filter: ein passender Ordner mit einem Treffer darunter wird geoeffnet", r.Expanded.Contains(core) && r.Expanded.Contains(lib));

        r = F("src")!;
        Check("Filter: ein passender Ordner ohne Treffer darunter bleibt zu, sein Inhalt sichtbar", !r.Expanded.Contains(src) && r.Shown.Contains(window) && r.Shown.Contains(helpers), Names(r.Expanded));

        r = F("nothing")!;
        Check("Filter: kein Treffer - nichts bleibt", r.Shown.Count == 0 && r.Matches.Count == 0 && r.Expanded.Count == 0);
        Check("Filter: die namenlose Wurzel passt nie von selbst", F("a")!.Matches.All(m => m != solution));

        var path = ExplorerTree.PathTo(roots, x => x.Children, x => x == window);
        Check("PathTo: von der Wurzel bis zur Zeile", path != null && string.Join("/", path.Select(p => p.Name)) == "/App/src/ui/Window.fire", path == null ? "null" : string.Join("/", path.Select(p => p.Name)));
        Check("PathTo: die erste Zeile (Tiefe zuerst)", ExplorerTree.PathTo(roots, x => x.Children, x => x.Name.EndsWith(".fire"))?.LastOrDefault() == window);
        Check("PathTo: keine passende Zeile - null", ExplorerTree.PathTo(roots, x => x.Children, x => x.Name == "missing") == null);
    }

    private static void HiddenChecks()
    {
        string root = Path.Combine(Path.GetTempPath(), "fire-explorer-" + Guid.NewGuid().ToString("N"));
        void Write(string rel) { string full = Path.Combine(root, rel); Directory.CreateDirectory(Path.GetDirectoryName(full)!); File.WriteAllText(full, "x"); }
        try
        {
            Write("App.fireproj");
            Write("main.fire");
            Write("src/a.fire");
            Write("src/.cache");
            Write("bin/Debug/App.fbc");
            Write("obj/x.json");
            Write(".hidden/notes.txt");
            Write(".git/HEAD");
            Write(".gitignore");
            Write("node_modules/pkg/index.js");
            Write("Other/Other.fireproj");      // another project: its folder belongs to it
            Write("Other/bin/o.fbc");
            var (folders, files) = ProjectFiles.HiddenEntries(root);
            string f = string.Join(",", folders), g = string.Join(",", files);
            Check("HiddenEntries: bin, obj, node_modules und Punkt-Ordner sind die versteckten Ordner, mit ihren Unterordnern",
                folders.SequenceEqual(new[] { ".git", ".hidden", "bin", "bin/Debug", "node_modules", "node_modules/pkg", "obj" }), f);
            Check("HiddenEntries: ihre Dateien und Punkt-Dateien in den gewoehnlichen Ordnern",
                files.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(new[] { ".gitignore", ".hidden/notes.txt", "bin/Debug/App.fbc", "node_modules/pkg/index.js", "obj/x.json", "src/.cache" }), g);
            Check("HiddenEntries: der Inhalt von .git wird nicht aufgezaehlt", !files.Any(x => x.StartsWith(".git/")), g);
            Check("HiddenEntries: der Ordner eines anderen Projekts bleibt draussen", !folders.Concat(files).Any(x => x.StartsWith("Other")), f + " | " + g);
            Check("HiddenEntries: nichts, was das Projekt schon zeigt", !files.Contains("main.fire") && !files.Contains("src/a.fire") && !folders.Contains("src"));
            var (lf, lfiles) = ProjectFiles.HiddenEntries(root, limit: 3);
            Check("HiddenEntries: limit begrenzt die Zahl der Eintraege", lf.Count + lfiles.Count == 3, $"{lf.Count}+{lfiles.Count}");
            Check("HiddenEntries: ein fehlender Ordner gibt nichts", ProjectFiles.HiddenEntries(Path.Combine(root, "missing")) is { Folders.Count: 0, Files.Count: 0 });
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}
