using fire.Projects;

/// <summary>The start project of a solution and Clean (docs/tools/spark.md, "The start project" and "Build, Build Solution and Clean"; docs/tools/projects.md): `startup` saved in and
/// read from the solution file (old files without it), which project Start runs from the active file (Workspace.StartProjectFor), the order of Build Solution (Workspace.BuildOrder)
/// and what Clean removes (ProjectOutput.Clean) - never a source. A quick run: FIRE_TESTS_ONLY=startproject (FIRE_TESTS_FILTER narrows it further by the title of a check).</summary>
static class StartProjectTests
{
    private static int _failures;

    private static void Check(string title, bool ok, string? detail = null)
    {
        string? filter = Environment.GetEnvironmentVariable("FIRE_TESTS_FILTER");
        if (!string.IsNullOrWhiteSpace(filter) && !filter.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(f => title.Contains(f, StringComparison.OrdinalIgnoreCase))) return;
        if (!ok) _failures++;
        Console.WriteLine(ok ? $"OK: Startprojekt: {title}" : $"FEHLER: Startprojekt: {title}" + (detail == null ? "" : $"\n{detail}"));
    }

    private static string Write(string root, string rel, string text)
    {
        string full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        return full;
    }

    public static int Run()
    {
        Console.WriteLine("=== Startprojekt, Build Solution und Clean ===");
        string root = Path.Combine(Path.GetTempPath(), "fire-startproject-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { RunIn(root); }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
        Console.WriteLine(_failures == 0 ? "Alle Startprojekt-Pruefungen bestanden." : $"FEHLER: {_failures} Startprojekt-Pruefung(en) fehlgeschlagen.");
        return _failures;
    }

    private static void RunIn(string root)
    {
        // a solution: a library, two programs that reference it (App first in the file, Tool second), and a script in the solution folder that belongs to no project
        Write(root, "Lib/Lib.fireproj", """{ "name": "Lib", "type": "library" }""");
        string libFile = Write(root, "Lib/lib.fire", "namespace Lib {\n    class G { static string Hi() { return \"hi\" } }\n}\n");
        Write(root, "App/App.fireproj", """{ "name": "App", "references": [ { "project": "../Lib/Lib.fireproj" } ] }""");
        string appMain = Write(root, "App/main.fire", "#import \"Lib\"\nprint(Lib.G.Hi())\n");
        Write(root, "Tool/Tool.fireproj", """{ "name": "Tool", "references": [ { "project": "../Lib/Lib.fireproj" } ] }""");
        string toolMain = Write(root, "Tool/main.fire", "print(\"tool\")\n");
        string toolTest = Write(root, "Tool/testing/tool_test.fire", "test \"t\" { assert(true) }\n");
        string toolNotes = Write(root, "Tool/notes/overview.md", "# Tool\n");
        string loose = Write(root, "scratch.fire", "print(\"alone\")\n");
        string readme = Write(root, "README.md", "# the solution\n");
        // an old solution file: no `startup` (the format before it, still read)
        string sln = Write(root, "Demo.firesln", """{ "format": 1, "name": "Demo", "projects": ["App/App.fireproj", "Lib/Lib.fireproj", "Tool/Tool.fireproj"] }""");

        // ---- save and load ------------------------------------------------------------------------------------------------------------------
        var ws = Workspace.Open(sln);
        Check("ohne startup: das erste Programm der Mappe ist das Startprojekt", ws.Startup?.Name == "App", ws.Startup?.Name);
        Check("ohne startup: die Datei bleibt beim Laden ohne startup", ws.Solution!.Startup == null && !File.ReadAllText(sln).Contains("startup"));
        ws.SetStartup(ws.FindByName("Tool")!);
        string text = File.ReadAllText(sln);
        Check("SetStartup schreibt \"startup\" mit dem Namen in die Mappendatei", text.Contains("\"startup\": \"Tool\""), text);
        Check("SetStartup laesst die anderen Eintraege der Datei stehen", text.Contains("\"App/App.fireproj\"") && text.Contains("\"name\": \"Demo\"") && text.Contains("\"format\": 1"), text);
        var reopened = Workspace.Open(sln);
        Check("gespeichertes Startprojekt wird wieder geladen", reopened.Startup?.Name == "Tool" && reopened.Solution!.Startup == "Tool", reopened.Startup?.Name);
        Check("FireSolution: startup im JSON hin und zurueck", FireSolution.Parse(reopened.Solution!.ToJson()).Startup == "Tool");
        Check("startup mit anderer Gross-/Kleinschreibung findet das Projekt", Workspace.Open(Write(root, "Case.firesln", """{ "name": "Case", "projects": ["App/App.fireproj", "Tool/Tool.fireproj"], "startup": "tool" }""")).Startup?.Name == "Tool");
        Check("unbekanntes startup: zurueck auf das erste Programm",
            Workspace.Open(Write(root, "Gone.firesln", """{ "name": "Gone", "projects": ["Lib/Lib.fireproj", "App/App.fireproj"], "startup": "Removed" }""")).Startup?.Name == "App");
        var removing = Workspace.Open(sln);
        removing.RemoveProject(removing.FindByName("Tool")!);
        Check("ein entferntes Startprojekt nimmt startup aus der Datei", !File.ReadAllText(sln).Contains("startup") && removing.Startup?.Name == "App", File.ReadAllText(sln));
        removing.AddProject(Path.Combine(root, "Tool", "Tool.fireproj"));
        removing.SetStartup(removing.FindByName("Tool")!);

        // ---- which project Start runs -------------------------------------------------------------------------------------------------------
        ws = Workspace.Open(sln);
        Check("Start aus einer Datei des Startprojekts: das Startprojekt", ws.StartProjectFor(toolMain, isScript: true)?.Name == "Tool");
        Check("Start aus einer Datei eines anderen Programms der Mappe: das Startprojekt", ws.StartProjectFor(appMain, isScript: true)?.Name == "Tool");
        Check("Start aus einer Datei der Bibliothek: das Startprojekt", ws.StartProjectFor(libFile, isScript: true)?.Name == "Tool");
        Check("Start aus einem Test des Projekts (testing/): das Startprojekt", ws.StartProjectFor(toolTest, isScript: true)?.Name == "Tool");
        Check("Start aus einer Projektdatei: das Startprojekt", ws.StartProjectFor(Path.Combine(root, "App", "App.fireproj"))?.Name == "Tool");
        Check("Start aus der Mappendatei: das Startprojekt", ws.StartProjectFor(sln)?.Name == "Tool");
        Check("Start aus einer Notiz oder einem anderen Dokument: das Startprojekt", ws.StartProjectFor(toolNotes)?.Name == "Tool" && ws.StartProjectFor(readme)?.Name == "Tool");
        Check("Start ohne Dokument: das Startprojekt", ws.StartProjectFor(null)?.Name == "Tool");
        Check("ein Skript, das zu keinem Projekt gehoert, laeuft allein", ws.StartProjectFor(loose, isScript: true) == null && ws.StartProjectFor(loose) == null);
        Check("ein ungespeichertes Skript laeuft allein", ws.StartProjectFor(null, isScript: true) == null);
        var single = Workspace.Open(Path.Combine(root, "App", "App.fireproj"));
        Check("ohne Mappe: das Projekt selbst", single.StartProjectFor(appMain, isScript: true)?.Name == "App" && single.StartProjectFor(Path.Combine(root, "App", "App.fireproj"))?.Name == "App");
        Check("nichts offen: kein Projekt", new Workspace().StartProjectFor(appMain, isScript: true) == null);

        // ---- the order of Build Solution ----------------------------------------------------------------------------------------------------
        var order = ws.BuildOrder().Select(p => p.Name).ToList();
        Check("Build Solution: die Bibliothek vor den Programmen, sonst die Reihenfolge der Mappe", string.Join(",", order) == "Lib,App,Tool", string.Join(",", order));
        // a cycle does not hang, and a library that is only referenced (not in the solution) is not built on its own
        Write(root, "Cyc/A/A.fireproj", """{ "name": "A", "type": "library", "references": [ { "project": "../B/B.fireproj" } ] }""");
        Write(root, "Cyc/B/B.fireproj", """{ "name": "B", "type": "library", "references": [ { "project": "../A/A.fireproj" } ] }""");
        Write(root, "Cyc/C/C.fireproj", """{ "name": "C", "type": "library" }""");
        Write(root, "Cyc/P/P.fireproj", """{ "name": "P", "references": [ { "project": "../A/A.fireproj" }, { "project": "../C/C.fireproj" } ] }""");
        var cyc = Workspace.Open(Write(root, "Cyc/Cyc.firesln", """{ "name": "Cyc", "projects": ["P/P.fireproj", "B/B.fireproj", "A/A.fireproj"] }"""));
        var cycOrder = cyc.BuildOrder().Select(p => p.Name).ToList();
        Check("Build Solution: ein Kreis von Verweisen haengt nicht, nur Projekte der Mappe, jedes einmal",
            cycOrder.Count == 3 && cycOrder.Distinct().Count() == 3 && !cycOrder.Contains("C") && cycOrder.IndexOf("P") == 2, string.Join(",", cycOrder));

        // ---- Clean ------------------------------------------------------------------------------------------------------------------------
        string program = Write(root, "Tool/bin/linux-dbg/Tool", "binary");
        Write(root, "Tool/bin/linux-dbg/Tool.src/fire_program.c", "int main() {}");
        Write(root, "Tool/bin/Lib-1.0.0.fpk", "package");
        Write(root, "Tool/obj/native/libx.a", "lib");
        string resource = Write(root, "Tool/resources/data.txt", "keep");
        var tool = ws.FindByName("Tool")!;
        var cleaned = ProjectOutput.Clean(tool);
        Check("Clean entfernt bin/ und obj/ des Projekts", !Directory.Exists(Path.Combine(root, "Tool", "bin")) && !Directory.Exists(Path.Combine(root, "Tool", "obj")) && cleaned.Removed.Count == 2 && cleaned.Kept.Count == 0,
            string.Join("\n", cleaned.Removed.Concat(cleaned.Kept)));
        Check("Clean laesst Quellen, Tests, Notizen, Ressourcen und die Projektdatei stehen",
            File.Exists(toolMain) && File.Exists(toolTest) && File.Exists(toolNotes) && File.Exists(resource) && File.Exists(Path.Combine(root, "Tool", "Tool.fireproj")) && !File.Exists(program));
        Check("Clean laesst die anderen Projekte stehen", File.Exists(appMain) && File.Exists(libFile));
        var again = ProjectOutput.Clean(tool);
        Check("Clean ohne Build-Ordner: nichts zu tun", again.Removed.Count == 0 && again.Kept.Count == 0);
        // a project that names a file in bin/ as a source: bin/ is no build folder for it - kept
        Write(root, "Odd/Odd.fireproj", """{ "name": "Odd", "files": ["bin/main.fire"] }""");
        string oddSource = Write(root, "Odd/bin/main.fire", "print(1)\n");
        Write(root, "Odd/obj/x.o", "o");
        var odd = Workspace.Open(Path.Combine(root, "Odd", "Odd.fireproj")).Projects[0];
        var oddClean = ProjectOutput.Clean(odd);
        Check("Clean laesst einen Ordner stehen, der eine Quelle des Projekts enthaelt", File.Exists(oddSource) && !Directory.Exists(Path.Combine(root, "Odd", "obj")) && oddClean.Kept.Count == 1 && oddClean.Kept[0].Contains("main.fire"),
            string.Join("\n", oddClean.Removed.Concat(oddClean.Kept)));
        // a nested project in bin/ (unusual, but its files are no build output)
        Write(root, "Nest/Nest.fireproj", """{ "name": "Nest" }""");
        Write(root, "Nest/main.fire", "print(1)\n");
        string nested = Write(root, "Nest/bin/Inner/Inner.fireproj", """{ "name": "Inner" }""");
        var nestClean = ProjectOutput.Clean(Workspace.Open(Path.Combine(root, "Nest", "Nest.fireproj")).Projects[0]);
        Check("Clean laesst einen Ordner stehen, der eine Projektdatei enthaelt", File.Exists(nested) && nestClean.Kept.Count == 1);
        // a build folder that is a link: the link goes, not what it points to
        if (!OperatingSystem.IsWindows())
        {
            string outside = Write(root, "Elsewhere/keep.txt", "keep");
            Write(root, "Linked/Linked.fireproj", """{ "name": "Linked" }""");
            Directory.CreateSymbolicLink(Path.Combine(root, "Linked", "bin"), Path.GetDirectoryName(outside)!);
            var linkClean = ProjectOutput.Clean(Workspace.Open(Path.Combine(root, "Linked", "Linked.fireproj")).Projects[0]);
            Check("Clean: ein bin/, das ein Link ist - der Link geht, das Ziel bleibt", File.Exists(outside) && !Directory.Exists(Path.Combine(root, "Linked", "bin")) && linkClean.Removed.Count == 1);
        }
    }
}
