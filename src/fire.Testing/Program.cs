// The test program of spark (docs/tools/testing.md, docs/ROADMAP_C_PORT.md M3). The tests of forge and the VM are data (tests/cases) and run without .NET:
// `forge conformance` (native/forge/tools/conformance.c). What stays here is C#: spark and its libraries (fire.Tooling, fire.Project, fire.Git, fire.Imaging,
// fire.UI.Markup) - the language services over `forge serve`, the background checks, the solution explorer, git, notes, the setup wizard, the previews and the
// circuit editor in `fire`, spark's runner of `forge build --report json`, the debugger's client, the device drivers, the MCP server, the models of the editors.
// forge and fire are the C programs next to this program (the build puts the folder of `forge package-dist` there: src/ForgeDist.targets) or FIRE_FORGE.
//
//   FIRE_TESTS_ONLY=<group>   one group (the names below); without it: all of them, one after the other.

TestProgress.Install();
// Wine of Ubuntu (9.0~repack) frees the value of getenv("TMPDIR") while it picks the folder of its wineserver: with TMPDIR set every Windows program aborts at
// once with "free(): invalid pointer". /tmp is what TMPDIR=/tmp means anyway - the variable goes for this process and everything it starts.
if (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("TMPDIR") is { } wineTmp && wineTmp.TrimEnd('/') == "/tmp") Environment.SetEnvironmentVariable("TMPDIR", null);
// the look of the UI must not depend on the desktop of the machine that runs the tests (SystemTheme: scaling, dark mode, accent, font)
Environment.SetEnvironmentVariable("FIRE_SYSTEM_THEME", "off");
string? only = Environment.GetEnvironmentVariable("FIRE_TESTS_ONLY");

// groups that need no store of their own
var quick = new (string Name, Func<int> Run)[]
{
    ("resume", TestResumeTests.Run),                                                               // the keys and records of FIRE_TESTS_RESUME / FIRE_TESTS_SHARD
    ("startproject", () => TestResume.Group("StartProjectTests", StartProjectTests.Run, 0)),       // the start project of a solution, the order of Build Solution
    ("explorer", () => TestResume.Group("ExplorerTests", ExplorerTests.Run, 0)),                   // the filter, Sync and Show All Files of the solution explorer
    ("forgereport", () => TestResume.Group("ForgeReportTests", ForgeReportTests.Run, 0)),          // spark's runner of `forge build --report json` (a fake forge and the real one)
    ("editorchecks", () => TestResume.Group("BackgroundCheckTests", BackgroundCheckTests.Run, 0) + TestResume.Group("ServeTests.Protocol", ServeTests.RunProtocol, 0)),
};
if (only == "cvmrun-child") { CVmRunTests.ChildHost(args); return; }   // (the stand-in for spark of CVmRunTests.Ending - its children end with it)
foreach (var (name, run) in quick)
    if (only == name) { Environment.ExitCode = run() == 0 ? 0 : 1; return; }

// The standard libraries: installed by forge (`forge install-standard`, from Source/ next to it) into a store of this run - FIRE_PACKAGES for forge, ember, fire and spark's classes.
var standardRoot = Path.Combine(Path.GetTempPath(), "fire-test-standard-" + Guid.NewGuid().ToString("N"));
TestTempFolders.Add(standardRoot);
string store = Path.Combine(standardRoot, "Packages");
Directory.CreateDirectory(store);
Environment.SetEnvironmentVariable("FIRE_PACKAGES", store);
fire.Projects.PackageStore.Default = new fire.Projects.PackageStore(store);
Forge.Reset();
var standardInstalled = StandardSources.EnsureInstalled(m => Console.WriteLine(m));
if (fire.Projects.PackageStore.Default.Installed().Count < fire.Projects.StandardPackages.Bridges.Count)
    Console.WriteLine($"FEHLER: Standardpakete: nur {fire.Projects.PackageStore.Default.Installed().Count} von {fire.Projects.StandardPackages.Bridges.Count} installiert");

// Fonts for the tests: a synthetic TrueType font of about 1 KB, written as "FireSans.ttf" into a folder that FIRE_FONT_DIRS names (the previews draw text with it).
byte[] fireTestFont = Convert.FromBase64String("AAEAAAALAIAAAwAwT1MvMkUCRH0AAAE4AAAAYGNtYXACMAFTAAABvAAAAGxnbHlmPzzozgAAAjwAAAEGaGVhZC+Ah2AAAAC8AAAANmhoZWEGEAH+AAAA9AAAACRobXR4EyQCWAAAAZgAAAAka2Vybv/h//wAAANEAAAAJGxvY2EBEQFeAAACKAAAABRtYXhwABAAGgAAARgAAAAgbmFtZaJXW7UAAANoAAAAZnBvc3QEn3EYAAAD0AAAADgAAQAAAAEAAEv3EAlfDzz1AAMD6AAAAADm8CF+AAAAAObwIX4AAAAAAooDhAAAAAMAAgAAAAAAAAABAAADIP84AGQCvAAAAAACigABAAAAAAAAAAAAAAAAAAAACQABAAAACQAQAAIABwACAAIAAAAAAAAAAAAAAAAAAgABAAMCIAGQAAUABAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAwAAAAAAAAAAAAAAAD8/Pz8AAAAgAMEDIP84AAADIADIAAAAAAAAAAAAAAAAAAAAIAAAAlgAZAEsAAACvAAyAlgAAAK8AGQCvABkASwAZAK8ADIBLABkAAAAAgAAAAMAAAAUAAMAAQAAABQABABYAAAAEgAQAAMAAgAgAC4AQQBIAE8AVgBpAMH//wAAACAALgBBAEgATwBWAGkAwf///+H/2P/B/73/tf+t/5//RgABAAAAAAAAAAAAAAAAAAAAAAAAAAAADQANABoALQBJAGAAawB3AIMAAQBkAAAB9AK8AAMAADMRIRFkAZACvP1EAAABADIAAAKKArwAAgAAMwEBMgEsASwCvP1EAAEAAAAAAlgCvAAFAAARAQEjAwMBLAEseLS0Arz9RAK8/j4BwgAAAgBkAAACWAK8AAcADwAAExAzMhEQIyITFDMyNTQjImT6+vr6eIKCgoIBXgFe/qL+ogFelpaWAAABAGQAAAJYArwACwAAMxEzESERMxEjESERZGQBLGRk/tQCvP7UASz9RAEs/tQAAQBkAAAAyAB4AAMAADM1MxVkZHh4//8AMgAAAooDhAAmAAIAAAAHAAYA+gMMAAEAZAAAAMgCvAADAAAzETMRZGQCvP1EAAAAAAABAAAAIAABAAMADAABAAYAAgAD/7AAAwAC/8QABQAEAB4AAAAEADYAAQAAAAAAAQAJAAAAAQAAAAAAAgAHAAkAAwABBAkAAQASABAAAwABBAkAAgAOACJGaXJlIFRlc3RSZWd1bGFyAEYAaQByAGUAIABUAGUAcwB0AFIAZQBnAHUAbABhAHIAAAACAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAkAAAADACQAOQAyACsBAgDJAEwDZG90");
string fontTestDir = Path.Combine(Path.GetTempPath(), "fire-font-test-" + Guid.NewGuid().ToString("N"));
TestTempFolders.Add(fontTestDir);
Directory.CreateDirectory(fontTestDir);
File.WriteAllBytes(Path.Combine(fontTestDir, "FireSans.ttf"), fireTestFont);
Environment.SetEnvironmentVariable("FIRE_FONT_DIRS", fontTestDir);

static int Count(Action run) { run(); return 0; }
var groups = new (string Name, Func<int> Run)[]
{
    ("cvmrun", () => TestResume.Group("CVmRunTests", CVmRunTests.Run, 0)),                         // spark's Run/Debug in `fire` (a fake fire, then forge and fire)
    ("previews", () => TestResume.Group("PreviewTests", PreviewTests.Run, 0)),                     // the design view of markup and the block preview in `fire`
    ("projects", () => { TestResume.Group("ProjectTests.Run", () => { ProjectTests.Run(); }); return 0; }),   // projects, solutions, templates (the model of spark)
    ("serve", () => TestResume.Group("ServeTests", ServeTests.Run, 0) + Count(() => TestResume.Group("CompletionTests.Run", () => { CompletionTests.Run(); }))),
    ("completion", () => Count(() => TestResume.Group("CompletionTests.Run", () => { CompletionTests.Run(); }))),
    ("notes", () => Count(() => TestResume.Group("NotesTests.Run", () => { NotesTests.Run(); }))),
    ("git", () => Count(() => { TestResume.Group("GitTests.Run", () => { GitTests.Run(); }); TestResume.Group("GitSyncTests.Run", () => { GitSyncTests.Run(); }); TestResume.Group("GitRepositoryViewTests.Run", () => { GitRepositoryViewTests.Run(); }); })),
    ("circuiteditor", () => Count(() => TestResume.Group("CircuitEditorTests.Run", () => { CircuitEditorTests.Run(); }))),
    ("blocks", () => Count(() => TestResume.Group("BlockModelTests.Run", () => { BlockModelTests.Run(); }))),
    ("mesh", () => Count(() => TestResume.Group("MeshModelTests.Run", () => { MeshModelTests.Run(); }))),
    ("setupwizard", () => Count(() => TestResume.Group("SetupWizardTests.Run", () => { SetupWizardTests.Run(); }))),
    ("mcpserver", () => Count(() => TestResume.Group("McpServerTests.Run", () => { McpServerTests.Run(); }))),
    ("aimodels", () => Count(() => TestResume.Group("AiModelsTests.Run", () => { AiModelsTests.Run(); }))),
    ("debugger", () => Count(() => TestResume.Group("DebuggerTests.Run", () => { DebuggerTests.Run(); }))),
    ("drivers", () => DriverAgentTests.Run()),
    ("image", () => Count(() => TestResume.Group("ImageTests.Run", () => { ImageTests.Run(); }))),
};
int failed = 0;
foreach (var (name, run) in only == null ? quick.Concat(groups) : groups.Where(g => g.Name == only))
{
    Console.WriteLine($"=== {name} ===");
    failed += run();
}
if (only != null && !groups.Any(g => g.Name == only)) { Console.Error.WriteLine($"FIRE_TESTS_ONLY={only}: no such group ({string.Join(", ", quick.Concat(groups).Select(g => g.Name))})."); Environment.ExitCode = 2; return; }
int all = TestProgress.Count;
Console.WriteLine(TestProgress.Failures == 0 && failed == 0 ? $"Alle Pruefungen bestanden ({all})." : $"FEHLER: {TestProgress.Failures + failed} Pruefung(en) fehlgeschlagen (von {all}).");
Environment.ExitCode = TestProgress.Failures == 0 && failed == 0 ? 0 : 1;
