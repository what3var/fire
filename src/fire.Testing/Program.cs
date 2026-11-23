using System.Collections.Generic;
using fire.Ast;
using fire.Bytecode;
using fire.Compiler;
using fire.Lexing;
using fire.Parsing;
using fire.Resolving;
using fire.Runtime;
using fire.Values;

// The standard libraries (time, io, graphics, ...): packed from their projects (Source next to the program, copied from packages/ of the repository) and installed into a store of their
// own for this run (the C++ libraries of their natives are built on first use).
// every check line numbered against the count of the last run ("OK: [12/530] ..."), every line flushed at once (TestUtils.cs, docs/tools/testing.md)
TestProgress.Install();
// Wine of Ubuntu (9.0~repack) frees the value of getenv("TMPDIR") while it picks the folder of its wineserver: with TMPDIR set every Windows program aborts at
// once with "free(): invalid pointer". /tmp is what TMPDIR=/tmp means anyway - the variable goes for this process and everything it starts.
if (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("TMPDIR") is { } wineTmp && wineTmp.TrimEnd('/') == "/tmp") Environment.SetEnvironmentVariable("TMPDIR", null);
// FIRE_TESTS_ONLY=resume: the keys and records of FIRE_TESTS_RESUME / FIRE_TESTS_SHARD (TestResumeTests.cs; a second, nothing built)
if (Environment.GetEnvironmentVariable("FIRE_TESTS_ONLY") == "resume") { Environment.ExitCode = TestResumeTests.Run() == 0 ? 0 : 1; return; }
// FIRE_TESTS_ONLY=startproject: the start project of a solution, the order of Build Solution and Clean (seconds; before the standard libraries are installed, which it does not need)
if (Environment.GetEnvironmentVariable("FIRE_TESTS_ONLY") == "startproject") { Environment.ExitCode = TestResume.Group("StartProjectTests", StartProjectTests.Run, 0) == 0 ? 0 : 1; return; }
// FIRE_TESTS_ONLY=publish: build actions (Ignore, Copy) and Publish of projects and solutions (PublishTests.cs; seconds, the native part only with a C compiler; nothing installed)
if (Environment.GetEnvironmentVariable("FIRE_TESTS_ONLY") == "publish") { Environment.ExitCode = TestResume.Group("PublishTests", PublishTests.Run, 0) == 0 ? 0 : 1; return; }
// FIRE_TESTS_ONLY=forgereport: spark builds through forge - the report of `forge build --report json` written and read, spark's runner against a fake forge (cancel included), the real forge on a library (ForgeReportTests.cs; seconds)
if (Environment.GetEnvironmentVariable("FIRE_TESTS_ONLY") == "forgereport") { Environment.ExitCode = TestResume.Group("ForgeReportTests", ForgeReportTests.Run, 0) == 0 ? 0 : 1; return; }
// FIRE_TESTS_ONLY=cvmrun: spark on the VM in C - Run/Debug as a child process `fire run x.fbc` against a fake fire and with the real forge and fire (CVmRunTests.cs; seconds)
// (FIRE_TESTS_ONLY=cvmrun-child: the stand-in for spark of CVmRunTests.Ending - its children end with it)
if (Environment.GetEnvironmentVariable("FIRE_TESTS_ONLY") == "cvmrun-child") { CVmRunTests.ChildHost(args); return; }
if (Environment.GetEnvironmentVariable("FIRE_TESTS_ONLY") == "cvmrun") { Environment.ExitCode = TestResume.Group("CVmRunTests", CVmRunTests.Run, 0) == 0 ? 0 : 1; return; }
// FIRE_TESTS_ONLY=cvmwin: the VM in C on Windows - fire.exe built with MinGW, run under Wine: the command line, a sample of the corpus, packed programs, FIRE_DISPLAY_SHM, __HostCommand, FIRE_DEVICE_HOST (CVmWindowsTests.cs; minutes)
if (Environment.GetEnvironmentVariable("FIRE_TESTS_ONLY") == "cvmwin") { Environment.ExitCode = TestResume.Group("CVmWindowsTests", CVmWindowsTests.Run, 0) == 0 ? 0 : 1; return; }
// FIRE_TESTS_ONLY=explorer: the filter, Sync and Show All Files of spark's solution explorer (ExplorerTests.cs; seconds, nothing installed)
if (Environment.GetEnvironmentVariable("FIRE_TESTS_ONLY") == "explorer") { Environment.ExitCode = TestResume.Group("ExplorerTests", ExplorerTests.Run, 0) == 0 ? 0 : 1; return; }
// FIRE_TESTS_ONLY=editorchecks: how spark checks the documents in the background while typing - debounce, cancellation, stale results, the gate of the analyses (BackgroundCheckTests.cs; seconds)
if (Environment.GetEnvironmentVariable("FIRE_TESTS_ONLY") == "editorchecks") { Environment.ExitCode = TestResume.Group("BackgroundCheckTests", BackgroundCheckTests.Run, 0) + TestResume.Group("ServeTests.Protocol", ServeTests.RunProtocol, 0) == 0 ? 0 : 1; return; }
// the look of the UI must not depend on the desktop of the machine that runs the tests (SystemTheme: scaling, dark mode, accent, font)
Environment.SetEnvironmentVariable("FIRE_SYSTEM_THEME", "off");
// scene3d's GPU path takes a Vulkan device that is a CPU (lavapipe) only with FIRE_S3D_GPU=cpu - without it the hardware checks only see the fallback to
// software and a broken GPU path goes unnoticed (like FIRE_GFX_GPU for the 2D GPU checks); Vulkan wants an XDG_RUNTIME_DIR. Set from outside, they stay.
if (Environment.GetEnvironmentVariable("FIRE_S3D_GPU") is not { Length: > 0 }) Environment.SetEnvironmentVariable("FIRE_S3D_GPU", "cpu");
if (Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is not { Length: > 0 })
{
    string xdg = Path.Combine(Path.GetTempPath(), "fire-test-xdg-" + Environment.ProcessId);
    Directory.CreateDirectory(xdg);
    try { File.SetUnixFileMode(xdg, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch (Exception) { }
    Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", xdg);
}
var standardRoot = Path.Combine(Path.GetTempPath(), "fire-test-standard-" + Guid.NewGuid().ToString("N"));
// (the folders of this run - the installed standard packages, hundreds of MB with their builds, and the test font - go when it ends: a normal end, a `return` of a
// FIRE_TESTS_ONLY group, an exception nothing catches, Ctrl+C or SIGTERM. Only this run's own: the folders of other runs stay)
TestTempFolders.Add(standardRoot);
fire.Package.Manager.PackageStore.Default = new fire.Package.Manager.PackageStore(Path.Combine(standardRoot, "Packages"));
var standardInstalled = StandardSources.EnsureInstalled(m => Console.WriteLine(m), prebuiltFolder: null);
static void CopyTree(string from, string to)
{
    Directory.CreateDirectory(to);
    foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
    foreach (var dir in Directory.GetDirectories(from)) CopyTree(dir, Path.Combine(to, Path.GetFileName(dir)));
}
if (standardInstalled.Count != fire.Package.Manager.StandardPackages.Bridges.Count)
    Console.WriteLine($"FEHLER: Standardpakete: nur {standardInstalled.Count} von {fire.Package.Manager.StandardPackages.Bridges.Count} installiert ({string.Join(", ", standardInstalled)})");

// Fonts for the tests: a synthetic TrueType font of about 1 KB (square, round and compound glyphs, a kern table; family "Fire Test"), written as "FireSans.ttf" into a folder that FIRE_FONT_DIRS names.
// The system lookup of fonts finds it by file name ("firesans"), full name and family name - in the VM and in the native programs (they inherit the variable).
byte[] fireTestFont = Convert.FromBase64String("AAEAAAALAIAAAwAwT1MvMkUCRH0AAAE4AAAAYGNtYXACMAFTAAABvAAAAGxnbHlmPzzozgAAAjwAAAEGaGVhZC+Ah2AAAAC8AAAANmhoZWEGEAH+AAAA9AAAACRobXR4EyQCWAAAAZgAAAAka2Vybv/h//wAAANEAAAAJGxvY2EBEQFeAAACKAAAABRtYXhwABAAGgAAARgAAAAgbmFtZaJXW7UAAANoAAAAZnBvc3QEn3EYAAAD0AAAADgAAQAAAAEAAEv3EAlfDzz1AAMD6AAAAADm8CF+AAAAAObwIX4AAAAAAooDhAAAAAMAAgAAAAAAAAABAAADIP84AGQCvAAAAAACigABAAAAAAAAAAAAAAAAAAAACQABAAAACQAQAAIABwACAAIAAAAAAAAAAAAAAAAAAgABAAMCIAGQAAUABAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAwAAAAAAAAAAAAAAAD8/Pz8AAAAgAMEDIP84AAADIADIAAAAAAAAAAAAAAAAAAAAIAAAAlgAZAEsAAACvAAyAlgAAAK8AGQCvABkASwAZAK8ADIBLABkAAAAAgAAAAMAAAAUAAMAAQAAABQABABYAAAAEgAQAAMAAgAgAC4AQQBIAE8AVgBpAMH//wAAACAALgBBAEgATwBWAGkAwf///+H/2P/B/73/tf+t/5//RgABAAAAAAAAAAAAAAAAAAAAAAAAAAAADQANABoALQBJAGAAawB3AIMAAQBkAAAB9AK8AAMAADMRIRFkAZACvP1EAAABADIAAAKKArwAAgAAMwEBMgEsASwCvP1EAAEAAAAAAlgCvAAFAAARAQEjAwMBLAEseLS0Arz9RAK8/j4BwgAAAgBkAAACWAK8AAcADwAAExAzMhEQIyITFDMyNTQjImT6+vr6eIKCgoIBXgFe/qL+ogFelpaWAAABAGQAAAJYArwACwAAMxEzESERMxEjESERZGQBLGRk/tQCvP7UASz9RAEs/tQAAQBkAAAAyAB4AAMAADM1MxVkZHh4//8AMgAAAooDhAAmAAIAAAAHAAYA+gMMAAEAZAAAAMgCvAADAAAzETMRZGQCvP1EAAAAAAABAAAAIAABAAMADAABAAYAAgAD/7AAAwAC/8QABQAEAB4AAAAEADYAAQAAAAAAAQAJAAAAAQAAAAAAAgAHAAkAAwABBAkAAQASABAAAwABBAkAAgAOACJGaXJlIFRlc3RSZWd1bGFyAEYAaQByAGUAIABUAGUAcwB0AFIAZQBnAHUAbABhAHIAAAACAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAkAAAADACQAOQAyACsBAgDJAEwDZG90");
string fontTestDir = Path.Combine(Path.GetTempPath(), "fire-font-test-" + Guid.NewGuid().ToString("N"));
TestTempFolders.Add(fontTestDir);
Directory.CreateDirectory(fontTestDir);
File.WriteAllBytes(Path.Combine(fontTestDir, "FireSans.ttf"), fireTestFont);
Environment.SetEnvironmentVariable("FIRE_FONT_DIRS", fontTestDir);

// FIRE_TESTS_ONLY=previews: spark's previews in `fire` - the design view of markup and the preview of block models as child processes (PreviewTests.cs; seconds; after the standard packages are installed; also part of circuiteditor)
if (Environment.GetEnvironmentVariable("FIRE_TESTS_ONLY") == "previews") { Environment.ExitCode = TestResume.Group("PreviewTests", PreviewTests.Run, 0) == 0 ? 0 : 1; return; }
// FIRE_TESTS_ONLY=trace: stack traces of exceptions - the cases tests/cases/trace in the VM, the format, the clickable lines of spark, forge test (TraceTests.cs; seconds)
if (Environment.GetEnvironmentVariable("FIRE_TESTS_ONLY") == "trace") { Environment.ExitCode = TestResume.Group("TraceTests", TraceTests.Run, 0) == 0 ? 0 : 1; return; }
// FIRE_TESTS_ONLY=projects runs only the tests of projects, templates and packing (a quick run while working on them).
// (TestResume.Group: a group of checks is one unit of FIRE_TESTS_RESUME/FIRE_TESTS_SHARD - not run again when it passed with the same build)
if (Environment.GetEnvironmentVariable("FIRE_TESTS_ONLY") == "projects") { TestResume.Group("ProjectTests.Run", () => { ProjectTests.Run(); }); return; }
// FIRE_TESTS_ONLY=linkage: linkage "static" of native builds (libraries in the cache, ROADMAP 9)
if (Environment.GetEnvironmentVariable("FIRE_TESTS_ONLY") == "linkage") { TestResume.Group("LinkageTests.Run", () => { LinkageTests.Run(); }); return; }
// FIRE_TESTS_ONLY=registry: the package sources of ember against an index on the web (a local HTTP server; docs/packages/README.md)
if (Environment.GetEnvironmentVariable("FIRE_TESTS_ONLY") == "registry") { TestResume.Group("RegistryTests.Run", () => { RegistryTests.Run(); }); return; }
// FIRE_TESTS_ONLY=bios16: the targets without an operating system (ROADMAP 13.5: the boot sector, a build booted in QEMU)
if (Environment.GetEnvironmentVariable("FIRE_TESTS_ONLY") == "bios16") { TestResume.Group("BiosTests.RunAlone", () => { BiosTests.RunAlone(); }); return; }
if (Environment.GetEnvironmentVariable("FIRE_TESTS_ONLY") == "scene3d") { TestResume.Group("Scene3DTests.Run", () => { Scene3DTests.Run(); }); return; }
if (Environment.GetEnvironmentVariable("FIRE_TESTS_ONLY") == "ai") { TestResume.Group("AiTests.Run", () => { AiTests.Run(); }); return; }
if (Environment.GetEnvironmentVariable("FIRE_TESTS_ONLY") == "mcpserver") { TestResume.Group("McpServerTests.Run", () => { McpServerTests.Run(); }); return; }
if (Environment.GetEnvironmentVariable("FIRE_TESTS_ONLY") == "aimodels") { TestResume.Group("AiModelsTests.Run", () => { AiModelsTests.Run(); }); return; }
// FIRE_TESTS_ONLY=setupwizard: spark's setup wizard without a window (catalog, pages, plan, progress, the download of a model)
if (Environment.GetEnvironmentVariable("FIRE_TESTS_ONLY") == "setupwizard") { TestResume.Group("SetupWizardTests.Run", () => { SetupWizardTests.Run(); }); return; }
if (Environment.GetEnvironmentVariable("FIRE_TESTS_ONLY") == "blocks") { TestResume.Group("BlockModelTests.Run", () => { BlockModelTests.Run(); }); return; }
if (Environment.GetEnvironmentVariable("FIRE_TESTS_ONLY") == "mesh") { TestResume.Group("MeshModelTests.Run", () => { MeshModelTests.Run(); }); return; }
if (Environment.GetEnvironmentVariable("FIRE_TESTS_ONLY") == "diagnostics") { TestResume.Group("DiagnosticTests.Run", () => { DiagnosticTests.Run(); }); return; }
// FIRE_TESTS_ONLY=programfile: the program file .fbc and `forge dump` (docs/ROADMAP_C_PORT.md, phase A)
if (Environment.GetEnvironmentVariable("FIRE_TESTS_ONLY") == "programfile") { TestResume.Group("ProgramFileTests.Run", () => { ProgramFileTests.Run(); }); return; }
if (Environment.GetEnvironmentVariable("FIRE_TESTS_ONLY") == "tables") { TestResume.Group("TablesTests.Run", () => { TablesTests.Run(); }); return; }
if (Environment.GetEnvironmentVariable("FIRE_TESTS_ONLY") == "syntax") { TestResume.Group("SyntaxTests.Run", () => { SyntaxTests.Run(); }); return; }
if (Environment.GetEnvironmentVariable("FIRE_TESTS_ONLY") == "types") { TestResume.Group("TypeTests.Run", () => { TypeTests.Run(); }); return; }
if (Environment.GetEnvironmentVariable("FIRE_TESTS_ONLY") == "completion") { TestResume.Group("CompletionTests.Run", () => { CompletionTests.Run(); }); return; }
// FIRE_TESTS_ONLY=serve: the language services over the protocol - `forge-c serve` (native/forge/serve*.c) with spark's client: scheduling of the diagnostics, documents, projects,
// the diagnostics compared with LiveDiagnostics, the completion (CompletionTests over the protocol) and the markup completion (ServeTests.cs; FIRE_SPARK_LANG=inproc: in this process)
if (Environment.GetEnvironmentVariable("FIRE_TESTS_ONLY") == "serve") { Environment.ExitCode = TestResume.Group("ServeTests", ServeTests.Run, 0) == 0 ? 0 : 1; TestResume.Group("CompletionTests.Run", () => { CompletionTests.Run(); }); return; }
if (Environment.GetEnvironmentVariable("FIRE_TESTS_ONLY") == "testing") { TestResume.Group("TestingTests.Run", () => { TestingTests.Run(); }); return; }
if (Environment.GetEnvironmentVariable("FIRE_TESTS_ONLY") == "notes") { TestResume.Group("NotesTests.Run", () => { NotesTests.Run(); }); return; }
// FIRE_TESTS_ONLY=git: git (GitTests), the files that change under the editor, the Git panel (GitSyncTests), the Repository view: graph, rebase, reset, cherry-pick, ... (GitRepositoryViewTests); FIRE_TESTS_FILTER narrows the GitSync checks by title
if (Environment.GetEnvironmentVariable("FIRE_TESTS_ONLY") == "git") { TestResume.Group("GitTests.Run", () => { GitTests.Run(); }); TestResume.Group("GitSyncTests.Run", () => { GitSyncTests.Run(); }); TestResume.Group("GitRepositoryViewTests.Run", () => { GitRepositoryViewTests.Run(); }); return; }
if (Environment.GetEnvironmentVariable("FIRE_TESTS_ONLY") == "circuiteditor") { TestResume.Group("CircuitEditorTests.Run", () => { CircuitEditorTests.Run(); }); int previewFailures = TestResume.Group("PreviewTests", PreviewTests.Run, 0); if (previewFailures > 0) Environment.ExitCode = 1; return; }
if (Environment.GetEnvironmentVariable("FIRE_TESTS_ONLY") == "snippets") { TestResume.Group("TypeTests.Snippets", () => { TypeTests.Snippets(); }); return; }
if (Environment.GetEnvironmentVariable("FIRE_TESTS_ONLY") == "debugger") { TestResume.Group("DebuggerTests.Run", () => { DebuggerTests.Run(); }); return; }
if (Environment.GetEnvironmentVariable("FIRE_TESTS_ONLY") == "drivers") { int driverFailures = DriverAgentTests.Run(); Console.WriteLine(driverFailures == 0 ? "Alle Treiber-Pruefungen bestanden." : $"FEHLER: {driverFailures} Treiber-Pruefung(en) fehlgeschlagen."); return; }
// (filled by sections that FIRE_TESTS_ONLY may skip: the native device cases by the device checks of the VM, the UI drawing by the UI checks)
var devNativeCases = new List<(string Title, string Script, string[] Expected, string? DefaultId)>();
string uiDrawScript = "";
string[] uiDrawExpected = Array.Empty<string>();
// FIRE_TESTS_ONLY=ui: only the UI library in the VM (fake renderer: text fields, clipboard, drag & drop, touch, joystick, markup, ...)
if (Environment.GetEnvironmentVariable("FIRE_TESTS_ONLY") == "ui") goto uiTests;
// FIRE_TESTS_ONLY=native: only the native backend (generated C against the VM, devices, windows, FreeRTOS, toolchains)
if (Environment.GetEnvironmentVariable("FIRE_TESTS_ONLY") == "native") goto nativeTests;
// FIRE_TESTS_ONLY=packer: only the packer (single file, payload, loader) and the native targets checked there (wasm with Emscripten and the browser)
if (Environment.GetEnvironmentVariable("FIRE_TESTS_ONLY") == "packer") goto packerTests;
// FIRE_TESTS_FILTER=text: of the checks that are listed as cases (the native cases, the native device and window cases, the UI checks) only those whose name contains
// the text (several texts separated by '|'; case does not matter) - together with FIRE_TESTS_ONLY a run of a minute instead of half an hour.
static bool TestSelected(string name)
{
    string? filter = Environment.GetEnvironmentVariable("FIRE_TESTS_FILTER");
    if (string.IsNullOrWhiteSpace(filter)) return true;
    return filter.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase));
}

// Returns the TestData directory relative to THIS source file, independent of the current working directory at execution (dotnet run can be started from
// different places).
static string GetTestDataDir([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
    Path.Combine(Path.GetDirectoryName(here)!, "TestData");

// Small manual smoke test for lexer + parser + unit system, until the
// evaluator exists. Locally for you: `dotnet run` in the src/fire folder.

string sample = """
int a = 5mm
float b = undefined:km

var c = b + a:!
var d = b: + a:!

class Exception {
    string message

    construct(string message) {
        this.message = message
    }
}

class InvalidUnitException : Exception {
    construct(string message) : base(message) { }
}

class Foo : Bar {
    construct(int x) : base(x) {
        this.x = x
    }

    destruct() {
        // aufraeumen
    }

    Compute(int y) {
        return this.x + y
    }
}

var f = func (x) on obj => { return x + 1 }

if (a is in mm) {
    var ok = true
}

if (a is of int) {
    // Basistyp-Check
}

if (!(a is of float)) {
    // prefix '!' negates a parenthesised expression
}

if (foo is from objList) {
    // direkter Owner
}

if (foo is under globalList) {
    // transitiver Owner
}

try {
    throw new InvalidUnitException("geht nicht")
} catch (InvalidUnitException e) {
    e.resume(0)
} catch (e) {
    // catch-all
} finally {
    cleanup()
}

{
    riskyStepOne()

    catch (e) {
        log(e.message)
    }

    riskyStepTwo()
    riskyStepThree()
}

var neg = !flag
var inv = ~mask
var m = -5

var withContinuation = 1 _
    + 2 + 3

var _ = 42
""";

Console.WriteLine("=== Lexer-Test ===");
var lexer = new Lexer(sample);
var tokens = lexer.Tokenize();
foreach (var token in tokens)
{
    if (token.Type == TokenType.Eof) break;
    Console.WriteLine(token);
}

// Token.Length = length in the source text (editor highlighting): quotation marks, escapes, `$"..."` and char literals count as well.
{
    string src = "var a = \"x\\ny\" + $\"v{1}\" + 'c' + 42mm";
    var lengths = new Lexer(src).Tokenize().Where(t => t.Type is TokenType.StringLiteral or TokenType.InterpolatedStringLiteral or TokenType.CharLiteral or TokenType.IntLiteral)
        .Select(t => src.Substring(t.Column - 1, t.Length)).ToList();
    var expectedTexts = new List<string> { "\"x\\ny\"", "$\"v{1}\"", "'c'", "42mm" };
    bool ok = lengths.SequenceEqual(expectedTexts);
    Console.WriteLine(ok ? "OK: Token.Length deckt String-/Char-/Zahl-Literale vollstaendig ab"
        : $"FEHLER: Token.Length\n  erwartet: {string.Join(" | ", expectedTexts)}\n  erhalten: {string.Join(" | ", lengths)}");
}

Console.WriteLine();
Console.WriteLine("=== Parser-Test ===");
try
{
    var program = Parser.Parse(sample);
    Console.WriteLine($"OK - {program.Count} Top-Level-Statements geparst:");
    foreach (var stmt in program)
        Console.WriteLine($"  {stmt.GetType().Name}");
}
catch (ParseException ex)
{
    Console.WriteLine($"PARSE-FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Resolver-Test (gültiges Programm) ===");

string resolverSampleValid = """
var globalCounter = 0

class Counter {
    int value

    construct(int start) {
        this.value = start
    }

    Increment(int by) {
        var doubled = by * 2
        this.value = this.value + doubled
        return this.value
    }
}

{
    var outer = 1
    {
        var inner = outer + 1
        globalCounter = globalCounter + inner
    }
}

var makeAdder = func (n) => {
    return n + globalCounter
}
""";

try
{
    var program = Parser.Parse(resolverSampleValid);
    var result = Resolver.Resolve(program);
    Console.WriteLine($"OK - {result.GlobalSlotCount} globale Slots, {result.Classes.Count} Klasse(n).");
    foreach (var (exprNode, resolved) in result.References)
    {
        if (exprNode is IdentifierExpr id)
            Console.WriteLine($"  '{id.Name}' (Zeile {id.Line}) -> {resolved}");
    }
}
catch (ResolverException ex)
{
    Console.WriteLine($"UNERWARTETER RESOLVER-FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Resolver-Test (Lambda erfasst Block-Locals als Kopie; ein Name aus einem anderen Block bleibt unbekannt) ===");

string resolverSampleCapture = """
{
    var blockOnlyLocal = 42
    var lam = func () => {
        return blockOnlyLocal
    }
}
""";

try
{
    Resolver.Resolve(Parser.Parse(resolverSampleCapture));
    Console.WriteLine("OK - die Lambda erfasst 'blockOnlyLocal' (Capture).");
}
catch (ResolverException ex)
{
    Console.WriteLine($"FEHLER: unerwarteter Resolver-Fehler: {ex.Message}");
}

string resolverSampleInvalid = """
{
    var blockOnlyLocal = 42
}
{
    var lam = func () => {
        return blockOnlyLocal
    }
}
""";

try
{
    Resolver.Resolve(Parser.Parse(resolverSampleInvalid));
    Console.WriteLine("FEHLER: hätte ResolverException werfen müssen (Name aus einem anderen Block)");
}
catch (ResolverException ex)
{
    Console.WriteLine($"Erwarteter Fehler: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Unit-System-Test ===");

var mm = Unit.Parse("mm");
var km = Unit.Parse("km");
var s = Unit.Parse("s");
var min = Unit.Parse("min");
var apples = Unit.Parse("apples");

Console.WriteLine($"mm kompatibel zu km? {mm.IsCompatibleWith(km)}");
Console.WriteLine($"1km in mm: {Value.MakeInt(1, km).CoerceUnit(mm)}");
Console.WriteLine($"5mm + 1km (nach Umrechnung auf mm): " +
    $"{Value.Add(Value.MakeInt(5, mm), Value.MakeInt(1, km).CoerceUnit(mm))}");

Console.WriteLine($"90min in s: {Value.MakeInt(90, min).CoerceUnit(s)}");
Console.WriteLine($"mm * mm Einheit: {Unit.Multiply(mm, mm)}");
Console.WriteLine($"m / s Einheit: {Unit.Divide(Unit.Parse("m"), s)}");
Console.WriteLine($"m / s^2 Einheit: {Unit.Divide(Unit.Parse("m"), Unit.Multiply(s, s))}");
Console.WriteLine($"apples kompatibel zu mm? {apples.IsCompatibleWith(mm)}");

try
{
    _ = Value.Add(Value.MakeInt(1, mm), Value.MakeInt(1, apples));
    Console.WriteLine("FEHLER: hätte UnitMismatchException werfen müssen");
}
catch (UnitMismatchException ex)
{
    Console.WriteLine($"Erwarteter Fehler bei mm + apples: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Runtime/Ownership-Test ===");

var dummyClass = "Dummy";
var globalScope = new Scope(null, isGlobal: true);
var funcScope = new Scope(globalScope);
var innerScope = new Scope(funcScope);
var log = new LoggingDestructRunner();

var objA = new ObjectInstance(dummyClass, innerScope);
Console.WriteLine($"a.Owner == innerScope? {ReferenceEquals(objA.Owner, innerScope)}");

objA.TakeUpwards();
Console.WriteLine($"nach TakeUpwards: a.Owner == funcScope? {ReferenceEquals(objA.Owner, funcScope)}");

objA.TakeGlobal(globalScope);
Console.WriteLine($"nach TakeGlobal: a.Owner == globalScope? {ReferenceEquals(objA.Owner, globalScope)}");

var objB = new ObjectInstance(dummyClass, innerScope);
objB.TakeTo(objA, log);
Console.WriteLine($"nach TakeTo: b.Owner == a? {ReferenceEquals(objB.Owner, objA)}");
Console.WriteLine($"b is from a? {objB.IsOwnedBy(objA)}");
Console.WriteLine($"b is under globalScope? {objB.IsTransitivelyOwnedBy(globalScope)}");

try
{
    objA.TakeTo(objB, log); // a belongs globally, b belongs to a -> a->b would be a cycle
    Console.WriteLine("FEHLER: Zyklus wurde nicht erkannt!");
}
catch (OwnershipException ex)
{
    Console.WriteLine($"Erwarteter Zyklus-Fehler: {ex.Message}");
}

var objC = new ObjectInstance(dummyClass, innerScope);
Console.WriteLine($"Vor Release: innerScope besitzt {innerScope.OwnedObjects.Count} Objekt(e)");
innerScope.Release(log);
Console.WriteLine($"Nach Release: innerScope besitzt {innerScope.OwnedObjects.Count} Objekt(e), c zerstört? {objC.IsDestroyed}");

Console.WriteLine("Race-Test (TakeTo während laufender Zerstörung des Ziels):");
var objD = new ObjectInstance(dummyClass, globalScope);
var raceRunner = new RaceDemoRunner(objA, objD, log);
objA.Destroy(raceRunner);
Console.WriteLine($"  d zerstört (obwohl TakeTo mitten in a's Destroy() aufgerufen wurde)? {objD.IsDestroyed}");


Console.WriteLine();
Console.WriteLine("=== Sprachtests als Dateien (tests/cases/smoke, vm-*) in der C-VM ===");
{
    // What used to be printed here for a reader - a script, its output and a sentence about what to expect - is checked now: the cases are files
    // (TestCases), run like `forge run` runs a program - in `fire`, the VM in C (TestCasesVm, M1) - and what they print is compared with the output recorded
    // next to them. The families vm-* are the cases of the checks of the C# VM further down (CheckExt, CheckArr, CheckPerf, CheckClone, CheckLife,
    // CheckShutdown, CheckGl, CheckLq, CheckRf, CheckSc): with the C# VM they ran there, now here.
    int caseFailures = TestCasesVm.Run("smoke", "vm-arr", "vm-arr-error", "vm-clone", "vm-clone-error", "vm-ext", "vm-ext-error", "vm-gl", "vm-life", "vm-lq",
        "vm-perf", "vm-rf", "vm-sc", "vm-sc-checked", "vm-shutdown");
    Console.WriteLine(caseFailures == 0 ? "Alle Sprachtests aus Dateien bestanden." : $"FEHLER: {caseFailures} Sprachtest(s) aus Dateien fehlgeschlagen.");
}

TestResume.Group("TraceTests", TraceTests.Run, 0);   // (the stack traces: tests/cases/trace)

Console.WriteLine();
Console.WriteLine("=== Typsystem-Test: Bitbreiten / Arrays / Pointer+unsafe / extern (gültig) ===");

string typeSystemSample = """
extern int GetTickCount()
extern PlaySound(string path)

int[8] narrow = 300
int[64] wide = 9999999999

int arr[10]
int matrix[][]
var dyn = new int[5]

unsafe {
    var x = 42
    int[64]* p = &x
    var y = *p
}
""";

try
{
    var program = Parser.Parse(typeSystemSample);
    var result = Resolver.Resolve(program);
    Console.WriteLine($"OK - {result.GlobalSlotCount} globale Slots geparst und aufgelöst.");
}
catch (Exception ex) when (ex is ParseException or ResolverException)
{
    Console.WriteLine($"UNERWARTETER FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Typsystem-Test: Dereferenzierung außerhalb 'unsafe' (muss fehlschlagen) ===");
try
{
    var program = Parser.Parse("var x = 5\nvar y = *x");
    Resolver.Resolve(program);
    Console.WriteLine("FEHLER: hätte ResolverException werfen müssen");
}
catch (ResolverException ex)
{
    Console.WriteLine($"Erwarteter Fehler: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Typsystem-Test: ungültige Bitbreite (muss fehlschlagen) ===");
try
{
    var program = Parser.Parse("int[17] x = 1");
    Resolver.Resolve(program);
    Console.WriteLine("FEHLER: hätte ResolverException werfen müssen");
}
catch (ResolverException ex)
{
    Console.WriteLine($"Erwarteter Fehler: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Typsystem-Test: 'try take' ausserhalb von Argument und Zuweisung (muss fehlschlagen) ===");
try
{
    var program = Parser.Parse("class Box {\n    int v\n}\nvar b = new Box()\nvar c = try take b == undefined");
    Resolver.Resolve(program);
    Console.WriteLine("FEHLER: hätte ResolverException werfen müssen");
}
catch (ResolverException ex) when (ex.Message.Contains("'try take'"))
{
    Console.WriteLine($"Erwarteter Fehler: {ex.Message}");
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: falsche Ausnahme: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Value.TruncateTo-Test ===");
var wideVal = Value.MakeInt(300);
Console.WriteLine($"300 (int64) -> TruncateTo(W8) = {wideVal.TruncateTo(NumericWidth.W8).AsInt()} (erwartet: 44, da 300 mod 256 - 256 = 44)");
var wideFloat = Value.MakeFloat(3.14159265358979);
Console.WriteLine($"3.14159265358979 (double) -> TruncateTo(W32) = {wideFloat.TruncateTo(NumericWidth.W32).AsFloat()}");

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: '&'/'*' außerhalb 'unsafe' (muss fehlschlagen) ===");
try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse("var x = 5\nvar p = &x");
    Resolver.Resolve(program, natives.Names);
    Console.WriteLine("FEHLER: hätte ResolverException werfen müssen");
}
catch (ResolverException ex)
{
    Console.WriteLine($"Erwarteter Fehler: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: Interface nicht erfüllt (muss fehlschlagen) ===");

string brokenInterfaceSample = """
interface IFoo {
    Bar()
}
class Broken : IFoo {
}
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(brokenInterfaceSample);
    Resolver.Resolve(program, natives.Names);
    Console.WriteLine("FEHLER: hätte ResolverException werfen müssen");
}
catch (ResolverException ex)
{
    Console.WriteLine($"Erwarteter Fehler: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== #include: relativ zur Datei, die es enthaelt; jede Datei nur einmal (auch eine Datei des Programms) ===");
{
    // a/lib/l.fire includes helper.fire (next to it); b/y.fire includes ../a/lib/l.fire and ../a/x.fire, which is a file of the program itself and includes lib/l.fire too;
    // forge runs from a third folder: every path is relative to the file that has the #include, nothing is inserted twice
    string root = Path.Combine(Path.GetTempPath(), "fire-include-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(root, "a", "lib"));
    Directory.CreateDirectory(Path.Combine(root, "b"));
    Directory.CreateDirectory(Path.Combine(root, "elsewhere"));
    File.WriteAllText(Path.Combine(root, "a", "lib", "l.fire"), "#include \"helper.fire\"\nclass L { static string Name() { return \"L \" + H.Name() } }\n");
    File.WriteAllText(Path.Combine(root, "a", "lib", "helper.fire"), "class H { static string Name() { return \"H\" } }\n");
    File.WriteAllText(Path.Combine(root, "a", "x.fire"), "#include \"lib/l.fire\"\nclass X { static string Name() { return \"X\" } }\n");
    File.WriteAllText(Path.Combine(root, "b", "y.fire"), "#include \"../a/lib/l.fire\"\n#include \"../a/x.fire\"\nprint(L.Name() + \" \" + X.Name())\n");
    string oldDir = Directory.GetCurrentDirectory();
    var oldOut = Console.Out;
    var captured = new StringWriter();
    var err = new StringWriter();
    int code;
    try
    {
        Directory.SetCurrentDirectory(Path.Combine(root, "elsewhere"));
        Console.SetOut(captured);
        code = fire.Compiler.CommandLineRunner.Run(new[] { "run", Path.Combine("..", "a", "x.fire"), Path.Combine("..", "b", "y.fire") }, captured, err);
    }
    finally { Console.SetOut(oldOut); Directory.SetCurrentDirectory(oldDir); }
    bool includeOk = code == 0 && captured.ToString().Trim() == "L H X";
    Console.WriteLine(includeOk ? "OK: #include relativ zur Datei (verschachtelt, aus einem anderen Ordner), jede Datei einmal" : $"FEHLER: #include relativ zur Datei: Exitcode {code}, Ausgabe '{captured}', Fehler '{err}'");
    try { Directory.Delete(root, true); } catch (IOException) { }
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: readonly Zuweisung nach Deklaration (muss fehlschlagen) ===");

string readonlyReassignSample = """
readonly var PI = 3
PI = 4
""";

try
{
    var program = Parser.Parse(readonlyReassignSample);
    var resolveResult = Resolver.Resolve(program);
    Console.WriteLine("FEHLER: hätte eine ResolverException werfen müssen, ist aber durchgelaufen.");
}
catch (ResolverException ex)
{
    Console.WriteLine($"Erwarteter Fehler: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: readonly Feld außerhalb des Konstruktors zugewiesen (muss fehlschlagen) ===");

string readonlyFieldInvalidSample = """
class Circle {
    readonly float radius

    construct(float radius) {
        this.radius = radius
    }

    Scale() {
        this.radius = this.radius * 2
    }
}
""";

try
{
    var program = Parser.Parse(readonlyFieldInvalidSample);
    var resolveResult = Resolver.Resolve(program);
    Console.WriteLine("FEHLER: hätte eine ResolverException werfen müssen, ist aber durchgelaufen.");
}
catch (ResolverException ex)
{
    Console.WriteLine($"Erwarteter Fehler: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: doppelte Methode mit GLEICHER Argumentzahl (muss fehlschlagen) ===");

string duplicateOverloadSample = """
class Foo {
    int Bar(int a) { return a }
    int Bar(int b) { return b * 2 }
}
""";

try
{
    var program = Parser.Parse(duplicateOverloadSample);
    var resolveResult = Resolver.Resolve(program);
    Console.WriteLine("FEHLER: hätte eine ResolverException werfen müssen, ist aber durchgelaufen.");
}
catch (ResolverException ex)
{
    Console.WriteLine($"Erwarteter Fehler: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: Extension-Klasse für unbekannte Klasse (muss fehlschlagen) ===");

string extendUnknownSample = """
class extends DoesNotExist {
    any x
}
""";

try
{
    var program = Parser.Parse(extendUnknownSample);
    Console.WriteLine("FEHLER: hätte eine ParseException werfen müssen, ist aber durchgelaufen.");
}
catch (ParseException ex)
{
    Console.WriteLine($"Erwarteter Fehler: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: Pflichtparameter NACH optionalem (muss fehlschlagen) ===");

string badOptionalSample = """
class Bad {
    int Foo(int a = 1, int b) {
        return a + b
    }
}
""";

try
{
    var program = Parser.Parse(badOptionalSample);
    var resolveResult = Resolver.Resolve(program);
    Console.WriteLine("FEHLER: hätte eine ResolverException werfen müssen, ist aber durchgelaufen.");
}
catch (ResolverException ex)
{
    Console.WriteLine($"Erwarteter Fehler: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: 'break' außerhalb einer Schleife (muss fehlschlagen) ===");

string breakOutsideSample = """
    print("vorher")
    break
    print("nachher")
    """;

try
{
    var program = Parser.Parse(breakOutsideSample);
    var natives = NativeRegistry.CreateDefault();
    Resolver.Resolve(program, natives.Names);
    Console.WriteLine("FEHLER: hätte eine ResolverException werfen müssen, ist aber durchgelaufen.");
}
catch (ResolverException ex)
{
    Console.WriteLine($"Erwarteter Fehler: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: generische Klasse, Constraint verletzt (muss fehlschlagen) ===");

string genericsViolationSample = """
class Animal {
}

class Cat {
}

class Container3<T> where T is of Animal {
}

var bad = new Container3<Cat>()
""";

try
{
    var program = Parser.Parse(genericsViolationSample);
    var resolveResult = Resolver.Resolve(program);
    Console.WriteLine("FEHLER: hätte eine ResolverException werfen müssen, ist aber durchgelaufen.");
}
catch (ResolverException ex)
{
    Console.WriteLine($"Erwarteter Fehler: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: generische Klasse, falsche Typ-Argument-Anzahl (muss fehlschlagen) ===");

string genericsArityMismatchSample = """
class Animal {
}

class Dog : Animal {
}

class Container4<T> where T is of Animal {
}

var bad = new Container4<Dog, Animal>()
""";

try
{
    var program = Parser.Parse(genericsArityMismatchSample);
    var resolveResult = Resolver.Resolve(program);
    Console.WriteLine("FEHLER: hätte eine ResolverException werfen müssen, ist aber durchgelaufen.");
}
catch (ResolverException ex)
{
    Console.WriteLine($"Erwarteter Fehler: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: nicht-generische Klasse mit Typ-Argumenten (muss fehlschlagen) ===");

string genericsNonGenericSample = """
class Plain {
}

var bad = new Plain<int>()
""";

try
{
    var program = Parser.Parse(genericsNonGenericSample);
    var resolveResult = Resolver.Resolve(program);
    Console.WriteLine("FEHLER: hätte eine ResolverException werfen müssen, ist aber durchgelaufen.");
}
catch (ResolverException ex)
{
    Console.WriteLine($"Erwarteter Fehler: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: break außerhalb einer Schleife (muss fehlschlagen) ===");

string breakOutsideLoopSample = """
print("before")
break
""";

try
{
    var program = Parser.Parse(breakOutsideLoopSample);
    var resolveResult = Resolver.Resolve(program);
    Console.WriteLine("FEHLER: hätte eine ResolverException werfen müssen, ist aber durchgelaufen.");
}
catch (ResolverException ex)
{
    Console.WriteLine($"Erwarteter Fehler: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: continue außerhalb einer Schleife (muss fehlschlagen) ===");

string continueOutsideLoopSample = """
continue
""";

try
{
    var program = Parser.Parse(continueOutsideLoopSample);
    var resolveResult = Resolver.Resolve(program);
    Console.WriteLine("FEHLER: hätte eine ResolverException werfen müssen, ist aber durchgelaufen.");
}
catch (ResolverException ex)
{
    Console.WriteLine($"Erwarteter Fehler: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: break in Lambda innerhalb einer Schleife (muss fehlschlagen) ===");

string breakInLambdaSample = """
var n = 0
while (n < 5) {
    n = n + 1
    var f = func() => {
        break
    }
}
""";

try
{
    var program = Parser.Parse(breakInLambdaSample);
    var resolveResult = Resolver.Resolve(program);
    Console.WriteLine("FEHLER: hätte eine ResolverException werfen müssen, ist aber durchgelaufen.");
}
catch (ResolverException ex)
{
    Console.WriteLine($"Erwarteter Fehler: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: break in finally innerhalb einer Schleife (muss fehlschlagen) ===");

string breakInTrySample = """
var m = 0
while (m < 5) {
    m = m + 1
    try {
        m = 7
    }
    finally {
        break
    }
}
""";

try
{
    var program = Parser.Parse(breakInTrySample);
    var resolveResult = Resolver.Resolve(program);
    Console.WriteLine("FEHLER: hätte eine ResolverException werfen müssen, ist aber durchgelaufen.");
}
catch (ResolverException ex)
{
    Console.WriteLine($"Erwarteter Fehler: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Multithreading: sync nach Zerstoerung des Ziels -> TargetGone ===");

try
{
    var dummyClass2 = "SyncGoneDummy";
    var ownerScope = new Scope(null, isGlobal: true);
    var original = new ObjectInstance(dummyClass2, ownerScope);
    original.Fields["x"] = Value.MakeInt(1);

    var childScope2 = new Scope(null, isGlobal: true);
    var copy2 = ObjectCopier.Take(original, childScope2);

    original.Destroy(NullDestructRunner.Instance);

    var result = SyncEngine.Sync(copy2, blocking: true);
    Console.WriteLine($"Sync-Ergebnis nach Zerstoerung des Ziels (erwartet TargetGone): {result}");
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Multithreading: 'taking' kopiert auch baumfremde Referenzen (alles Erreichbare) ===");

try
{
    var dummyClass3 = "TakingDummy";
    var scopeA = new Scope(null, isGlobal: true);
    var independent = new ObjectInstance(dummyClass3, scopeA);

    var root = new ObjectInstance(dummyClass3, scopeA);
    // Deliberately set WITHOUT reparenting - simulates a reference foreign to the
    // tree, as normal field assignment (which reparents per the ownership
    // policy) would not actually produce.
    root.Fields["escapesTree"] = Value.MakeClassRef(independent);

    var childScope3 = new Scope(null, isGlobal: true);
    var taken = ObjectCopier.Take(root, childScope3);
    var escaped = (ObjectInstance)taken.Fields["escapesTree"].AsObjectRef();
    bool copiedApart = !ReferenceEquals(escaped, independent) && escaped.IsTakingCopy && ReferenceEquals(escaped.Owner, childScope3);
    Console.WriteLine(copiedApart
        ? "Baumfremde Referenz kopiert, die Kopie gehoert dem Thread-Scope."
        : "FEHLER: die baumfremde Referenz haette kopiert werden muessen (Besitzer: Thread-Scope).");
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Multithreading: Schreiben auf ein Hauptprogramm-Global in 'fire' wird uebersetzt (laeuft als Sektion, siehe Block 'Globals und Fire-Threads') ===");

string mtGlobalWriteRejectedSample = """
var counter = 10

fire {
    counter = 99
}
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(mtGlobalWriteRejectedSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    Compiler.Compile(program, resolveResult, natives);
    Console.WriteLine("OK: das Schreiben wird uebersetzt");
}
catch (ResolverException ex)
{
    Console.WriteLine($"FEHLER: Schreiben auf ein Global in 'fire' wurde abgelehnt: {ex.Message}");
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER (falscher Exception-Typ): {ex.GetType().Name}: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Multithreading: '#noshadow' schaltet den Globals-Snapshot ab ===");

string mtNoShadowSample = """
#noshadow

var counter = 10

fire {
    print(counter)
}
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(mtNoShadowSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    Compiler.Compile(program, resolveResult, natives);
    Console.WriteLine("FEHLER: Hätte einen ResolverException erwarten sollen (Snapshot ist ja abgeschaltet), ist aber durchgelaufen!");
}
catch (ResolverException ex)
{
    Console.WriteLine($"Erwarteter Fehler (Snapshot korrekt abgeschaltet): {ex.Message}");
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER (falscher Exception-Typ): {ex.GetType().Name}: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Freie Praeprozessor-Direktiven: #name wert1, wert2, ... ===");

try
{
    var registry = new DirectiveRegistry(); // completely empty, NOT CreateDefault()
    var receivedArgs = new List<Value>();
    registry.Register("mydirective", 4, (ctx, args, line) =>
    {
        receivedArgs.AddRange(args);
        return null; // produces no replacement text
    });

    string source = """
    #mydirective "value1", 632, 84.23, 74mm
    print("nach der Direktive")
    """;

    string preprocessed = Preprocessor.Process(source, "/home/claude", registry).Source;
    Console.WriteLine($"Erhaltene Argumente ({receivedArgs.Count}, erwartet 4):");
    foreach (var v in receivedArgs)
        Console.WriteLine($"  {v.Kind}: {v}");

    Console.WriteLine("Vorverarbeiteter Text enthaelt noch 'print(...)' (erwartet true): " +
        preprocessed.Contains("print(\"nach der Direktive\")"));
}
catch (System.Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Falsche Parameteranzahl -> klarer Fehler ===");

try
{
    var registry = new DirectiveRegistry();
    registry.Register("needsTwo", 2, (ctx, args, line) => null);
    Preprocessor.Process("#needsTwo \"nur einer\"\n", "/home/claude", registry);
    Console.WriteLine("FEHLER: haette werfen sollen, hat aber nicht.");
}
catch (PreprocessorException ex)
{
    Console.WriteLine($"Erwarteter Fehler: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Unbekannte Direktive (z.B. '#extern'/'#noshadow') wird unveraendert durchgereicht ===");

try
{
    var registry = new DirectiveRegistry(); // 'extern' is NOT registered here
    string source = "#extern \"kernel32.dll\"\nprint(\"x\")\n";
    string result = Preprocessor.Process(source, "/home/claude", registry).Source;
    Console.WriteLine("Zeile blieb erhalten (erwartet true): " + result.Contains("#extern \"kernel32.dll\""));
}
catch (System.Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Generics: gleicher Name - falsche Typ-Argumente / Doppeldefinition (müssen fehlschlagen) ===");

foreach (var (label, source) in new[]
{
    ("falsche Typ-Argument-Anzahl", "class Box { }\nclass Box<T> { }\nvar x = new Box<int, int>()"),
    ("zwei generische mit gleicher Anzahl", "class Box { }\nclass Box<T> { }\nclass Box<U> { }"),
})
{
    try
    {
        var natives = NativeRegistry.CreateDefault();
        var program = Parser.Parse(source);
        Resolver.Resolve(program, natives.Names);
        Console.WriteLine($"FEHLER ({label}): haette fehlschlagen muessen");
    }
    catch (ResolverException ex)
    {
        Console.WriteLine($"Erwarteter Fehler ({label}): {ex.Message}");
    }
}

Console.WriteLine();
Console.WriteLine("=== Resolver: sammelt ALLE Fehler statt beim ersten abzubrechen ===");

string manyResolverErrors = """
var a = unknown1
var b = a + 1
var c = new Nope()
class K : Missing {
    Foo() {
        return alsoMissing
    }
    Bar() {
        return stillMissing
    }
}
print(zzz)
try { print(x1) } catch (NoSuchException e) { print(e) } finally { print(x2) }
print(p1 + p2)
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(manyResolverErrors);
    Resolver.Resolve(program, natives.Names);
    Console.WriteLine("FEHLER: haette fehlschlagen muessen");
}
catch (ResolverException ex)
{
    Console.WriteLine($"Message (= Meldung des ersten gesammelten Fehlers, wie bisher): {ex.Message}");
    Console.WriteLine($"Anzahl gesammelter Fehler (erwartet: 11): {ex.Errors.Count}");
    foreach (var error in ex.Errors)
        Console.WriteLine($"  Zeile {error.Line}: {error.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Compiler: sammelt ALLE Fehler statt beim ersten abzubrechen ===");

string manyCompilerErrors = """
class A {
    int f = print
    M1() {
        var x = print
        var y = 2
        var z = print
    }
    M2() { print = 5 }
}
var top1 = print
var ok = 1
var lam = func () => { var inner = print
                       return inner }
if (ok == 1) {
    var nested = print
}
print(ok)
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(manyCompilerErrors);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    Compiler.Compile(program, resolveResult, natives);
    Console.WriteLine("FEHLER: haette fehlschlagen muessen");
}
catch (CompilerException ex)
{
    Console.WriteLine($"Anzahl gesammelter Fehler (erwartet: 7): {ex.Errors.Count}");
    foreach (var error in ex.Errors)
        Console.WriteLine($"  Zeile {error.Line}: {error.Message}");
    Console.WriteLine($"Ist eine NotSupportedException (bestehender Code faengt sie weiter): {ex is NotSupportedException}");
}

Console.WriteLine();
Console.WriteLine("=== Editor-Vervollständigung: Klassen-Mitglieder werden über die Typen aufgelöst ===");

{
    const string classes = """
        class Animal {
            string name
            protected int age
            private int secret
            static int Count
            construct(string n) { this.name = n }
            Speak() { return "..." }
        }
        class Dog : Animal {
            Tail tail = new Tail()
            Bark() { return 1 }
            Wag() { return this.tail }
        }
        class Tail {
            int length
            Curl() { }
        }
        enum Color { Red, Green }

        """;

    int completionFailures = 0;
    // '|' in the source = cursor position. `expected`: these names MUST be suggested,
    // `forbidden`: these MUST NOT be (comma-separated).
    void CheckCompletion(string title, string source, string expected, string forbidden = "", bool exact = false)
    {
        int cursor = source.IndexOf('|');
        source = source.Remove(cursor, 1);
        var index = fire.Editor.ScriptSymbolIndex.Build(source);
        var names = fire.Editor.CompletionEngine.GetSuggestions(source, cursor, index).Select(i => i.Text).ToList();
        var want = expected.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var deny = forbidden.Split(',', StringSplitOptions.RemoveEmptyEntries);
        bool ok = want.All(names.Contains) && !deny.Any(names.Contains) && (!exact || names.Count == want.Length);
        if (!ok) completionFailures++;
        Console.WriteLine($"{(ok ? "OK" : "FEHLER")}: {title} -> {string.Join(", ", names.Take(10))}");
    }

    CheckCompletion("typisierte Variable", classes + "Dog d = new Dog(\"a\")\nd.|", "Bark,Wag,tail,name,Speak", "Curl,Count,age,secret,Dog");
    // named arguments: at the start of an argument the parameters of the callee as `name: ` (those already named left out)
    const string namedDecl = "class W { static int F(int size, bool open = false) { return size }\n construct(int width, int height = 1) { } }\n";
    CheckCompletion("benannte Argumente: statische Methode", classes + namedDecl + "W.F(1, |", "size: ,open: ");
    CheckCompletion("benannte Argumente: schon benannte fehlen", classes + namedDecl + "W.F(open: true, |", "size: ", "open: ");
    CheckCompletion("benannte Argumente: Konstruktor", classes + namedDecl + "var w = new W(|", "width: ,height: ");
    CheckCompletion("benannte Argumente: nicht ausserhalb einer Argumentliste", classes + namedDecl + "var x = |", "", "size: ,width: ");
    CheckCompletion("typisierte Variable ohne Initialisierer", classes + "Dog d\nd.|", "Bark,Wag,tail,name,Speak", "Curl,Count,age,secret,Dog");
    // 'var x : unit' only fixes a unit, the type comes from the initialiser.
    CheckCompletion("var mit Einheit", classes + "var d : mm = new Dog(\"a\")\nd.|", "Bark,Wag", "Curl");
    CheckCompletion("var = new X()", classes + "var d = new Dog(\"a\")\nd.|", "Bark,name,Speak", "Curl,length");
    CheckCompletion("Praefix filtert", classes + "var d = new Dog(\"a\")\nd.Ba|", "Bark", "Speak", exact: true);
    CheckCompletion("var = andere Variable", classes + "var d = new Dog(\"a\")\nvar e = d\ne.|", "Bark", "Curl");
    CheckCompletion("Methodenkette (Rueckgabetyp aus return)", classes + "var d = new Dog(\"a\")\nd.Wag().|", "Curl,length", "Bark", exact: true);
    CheckCompletion("Feldkette", classes + "var d = new Dog(\"a\")\nd.tail.|", "Curl,length", "Bark", exact: true);
    CheckCompletion("new X().", classes + "new Dog(\"a\").|", "Bark", "Curl");
    CheckCompletion("Zuweisung spaeter", classes + "var d = null\nd = new Dog(\"a\")\nd.|", "Bark", "Curl");
    CheckCompletion("typisierter Parameter", classes + "Foo(Dog d) { d.| }", "Bark", "Curl");
    CheckCompletion("Array-Element", classes + "var arr = new Dog[3]\narr[0].|", "Bark", "Curl");
    CheckCompletion("foreach ueber Array", classes + "Dog ds[]\nforeach (var x in ds) { x.| }", "Bark", "Curl");
    CheckCompletion("foreach ohne var (so schreibt es die Sprache)", classes + "Dog ds[]\nforeach (x in ds) { x.| }", "Bark", "Curl");
    CheckCompletion("foreach-Variable wird vorgeschlagen", "var l = new List()\nforeach (zeile in l) { zei| }", "zeile");
    CheckCompletion("Klasse. nur statisch", classes + "Animal.|", "Count", "Speak,name", exact: true);
    CheckCompletion("Enum.", classes + "var c = Color.|", "Red,Green", exact: true);
    CheckCompletion("private/protected ausserhalb versteckt", classes + "var a = new Animal(\"x\")\na.|", "name,Speak", "secret,age,Count");
    CheckCompletion("private innerhalb sichtbar", "class A { private int p\n M() { this.| } }", "p,M");
    CheckCompletion("protected in Ableitung", "class A { protected int p }\nclass B : A { M() { this.| } }", "p,M");
    CheckCompletion("this.feld.", "class A { B b = new B()\n M() { this.b.| } }\nclass B { Z() {} }", "Z", "M", exact: true);
    CheckCompletion("Feld ohne this.", "class A { B b = new B()\n M() { b.| } }\nclass B { Z() {} }", "Z", "M", exact: true);
    CheckCompletion("Feldtyp aus Konstruktor", "class A { b\n construct() { this.b = new B() }\n M() { this.b.| } }\nclass B { Z() {} }", "Z", "M", exact: true);
    CheckCompletion("Aufruf ohne this.", "class A { B mk() { return new B() }\n M() { mk().| } }\nclass B { Z() {} }", "Z", "M", exact: true);
    CheckCompletion("generische Klasse", "class Box<T> where T is of A {\n T item\n Get() { return this.item }\n}\nclass A { Run() {} }\nvar b = new Box<A>()\nb.|", "Get,item", "Run", exact: true);
    CheckCompletion("Interface-Mitglieder", "interface I { Foo() }\nclass A : I { Bar() {} }\nI a\na.|", "Foo", "Bar", exact: true);
    CheckCompletion("Interface-Mitglieder: Properties und geerbte Interfaces", "interface IBase { Foo() }\ninterface IOther { int Size { get; } }\ninterface I : IBase, IOther { int Limit { get; set; }\n float Delay : ms { get; } }\nclass A : I { Bar() {} }\nI a\na.|", "Foo,Size,Limit,Delay", "Bar,get,set,ms", exact: true);
    CheckCompletion("Prelude-Klasse", "var l = new List()\nl.|", "Add,GetEnumerator", "Speak");
    CheckCompletion("Erweiterung per #import", "#import \"graphics\"\nvar fb = new Framebuffer(1, 2)\nfb.|", "Width,Height,ReadByte");
    CheckCompletion("#import io: IO. zeigt Streams und Enums", "#import \"io\"\nIO.|", "MemoryStream,Stream,IStream,SeekOrigin,IOException,TextReader", "FileStream,FileMode,Directory");
    CheckCompletion("#import filesystem: IO. zeigt dazu Dateien, Verzeichnisse, Pfade", "#import \"filesystem\"\nIO.|", "FileStream,FileMode,File,Directory,Path,SearchOption,MemoryStream,Stream");
    CheckCompletion("#import io: Stream-Mitglieder", "#import \"filesystem\"\nvar s = new IO.FileStream(\"a.bin\")\ns.|", "ReadBytes,ReadAll,CopyTo,Position,Length,Close,Seek,Name", "ToBuffer,Throw");
    CheckCompletion("#import io: IO.FileMode.", "#import \"filesystem\"\nvar m = IO.FileMode.|", "Open,Create,CreateNew,OpenOrCreate,Append", exact: true);
    CheckCompletion("nach 'new' nur Klassen (keine Interfaces)", "interface I { Foo() }\nclass A { }\nvar x = new |", "A", "I,var");
    CheckCompletion("bool hat keine Mitglieder", "var b = true\nb.|", "", exact: true);
    CheckCompletion("Variable aus fremder Methode nicht sichtbar -> Fallback", "class A { M() { var q = new B() }\n N() { q.| } }\nclass B { Z() {} }", "Z");
    CheckCompletion("unbestimmbar -> Fallback auf alle Klassen", classes + "var d = something()\nd.|", "Bark,Curl");
    CheckCompletion("Zyklus haengt nicht", "var a = b\nvar b = a\na.|", "");
    // ---- Namespaces and their members ----
    const string geo = """
        namespace Geometry {
            class Shape { string label
                Describe() { return "s" } }
            class Circle : Shape {
                int radius
                static Circle Unit() { return new Circle() }
                Area() { return 1 }
            }
            interface IDrawable { Draw() }
            enum Kind { Round, Flat }
            namespace Inner {
                class Deep { Go() { } }
            }
        }
        class Other { Z() { } }

        """;
    CheckCompletion("Namespace. zeigt Klassen, Enums, Unter-Namespaces", geo + "Geometry.|", "Shape,Circle,IDrawable,Kind,Inner", "Deep,Other,Area", exact: true);
    CheckCompletion("Namespace. Praefix", geo + "Geometry.Ci|", "Circle", "Shape", exact: true);
    CheckCompletion("verschachtelter Namespace.", geo + "Geometry.Inner.|", "Deep", "Shape", exact: true);
    CheckCompletion("Namespace.Klasse. -> statische Mitglieder", geo + "Geometry.Circle.|", "Unit", "Area,radius", exact: true);
    CheckCompletion("Namespace.Enum.", geo + "Geometry.Kind.|", "Round,Flat", exact: true);
    CheckCompletion("new Namespace.", geo + "var c = new Geometry.|", "Shape,Circle,Inner", "IDrawable,Kind", exact: true);
    CheckCompletion("new Namespace.Klasse(...).", geo + "new Geometry.Circle().|", "Area,radius,Describe,label", "Unit,Z");
    CheckCompletion("var = new Namespace.Klasse()", geo + "var c = new Geometry.Circle()\nc.|", "Area,radius,Describe,label", "Unit,Z");
    CheckCompletion("Namespace-Typ als Deklaration", geo + "Geometry.Circle c\nc.|", "Area,radius", "Unit,Z");
    CheckCompletion("Ergebnis einer statischen Methode aus Namespace", geo + "var c = Geometry.Circle.Unit()\nc.|", "Area,radius", "Unit");
    CheckCompletion("Namespace auf oberster Ebene angeboten", geo + "Geo|", "Geometry", "Other,Circle");
    CheckCompletion("Klasse ausserhalb des Namespaces nicht unqualifiziert", geo + "Cir|", "", "Circle,Shape");
    CheckCompletion("Klasse ohne Namespace unqualifiziert", geo + "Ot|", "Other", "Circle");
    CheckCompletion("new: Klasse nur qualifiziert erreichbar", geo + "var x = new Ci|", "", "Circle");
    CheckCompletion("Kontext: innerhalb des Namespaces einfach ansprechbar", geo + "namespace Geometry { class Extra { M() { var c = new Ci| } } }", "Circle", "Other");
    CheckCompletion("#using macht Klassen unqualifiziert ansprechbar", "#using Geometry\n" + geo + "var c = new Ci|", "Circle", "Deep");
    CheckCompletion("#using: var = new Klasse()", "#using Geometry\n" + geo + "var c = new Circle()\nc.|", "Area,radius", "Unit");
    CheckCompletion("#using: Namespaces vorschlagen", "namespace Geometry { namespace Inner { } }\n#using Ge|", "Geometry", "Inner", exact: true);
    CheckCompletion("#using: Unter-Namespaces", "namespace Geometry { namespace Inner { } }\n#using Geometry.|", "Inner", "Geometry", exact: true);
    CheckCompletion("Basisklasse aus Namespace geerbt", geo + "var c = new Geometry.Circle()\nc.|", "label,Describe");
    CheckCompletion("Feldtyp aus Namespace", "namespace N { class A { B b = new B()\n M() { this.b.| } }\n class B { Z() { } } }", "Z", "M", exact: true);
    CheckCompletion("Feldtyp qualifiziert", "namespace N { class B { Z() { } } }\nclass A { N.B b\n M() { this.b.| } }", "Z", "M", exact: true);
    CheckCompletion("gleichnamige Klassen in zwei Namespaces", "namespace P { class Same { OnlyP() { } } }\nnamespace Q { class Same { OnlyQ() { } } }\nvar a = new P.Same()\na.|", "OnlyP", "OnlyQ", exact: true);
    CheckCompletion("class extends im Namespace", "namespace N { class A { X() { } } }\nnamespace N { class extends A { Y() { this.| } } }", "X,Y");
    Console.WriteLine(completionFailures == 0 ? "Alle Vervollstaendigungs-Pruefungen bestanden." : $"FEHLER: {completionFailures} Pruefung(en) fehlgeschlagen.");
}

Console.WriteLine();
Console.WriteLine("=== Strings: Editor-Vervollstaendigung ===");
{
    int failures = 0;
    void Check(string title, string source, string expected, string forbidden = "")
    {
        int cursor = source.IndexOf('|');
        source = source.Remove(cursor, 1);
        var index = fire.Editor.ScriptSymbolIndex.Build(source);
        var names = fire.Editor.CompletionEngine.GetSuggestions(source, cursor, index).Select(i => i.Text).ToList();
        bool ok = expected.Split(',').All(names.Contains) && !forbidden.Split(',', StringSplitOptions.RemoveEmptyEntries).Any(names.Contains);
        if (!ok) failures++;
        Console.WriteLine($"{(ok ? "OK" : "FEHLER")}: {title} -> {string.Join(", ", names.Take(8))}");
    }
    Check("string-Variable", "string s = \"abc\"\ns.|", "Length,IndexOf,LastIndexOf,Substring", "Bark,length");
    Check("var s = Literal", "var s = \"abc\"\ns.|", "Length,IndexOf,Substring");
    Check("Literal direkt", "\"abc\".|", "Length,Trim");
    Check("Praefix", "var s = \"abc\"\ns.Sub|", "Substring", "Length");
    Check("Kette Trim().", "var s = \"abc\"\ns.Trim().|", "Length,ToUpper");
    Check("Split() liefert Array", "var s = \"a,b\"\ns.Split(\",\").|", "Length", "Substring");
    Check("Array-Element ist string", "var s = \"a,b\"\nvar p = s.Split(\",\")\np[0].|", "Substring");
    Check("foreach ueber Split", "var s = \"a,b\"\nforeach (p in s.Split(\",\")) { p.| }", "Substring");
    Check("Length ist int", "var s = \"abc\"\nvar n = s.Length\nn.|", "ToChar");
    Console.WriteLine(failures == 0 ? "Alle String-Vervollstaendigungs-Pruefungen bestanden." : $"FEHLER: {failures} Pruefung(en) fehlgeschlagen.");
}

Console.WriteLine();
Console.WriteLine("=== Basistyp-Erweiterungen: Editor ===");
{
    int failures = 0;
    void Check(string title, string source, string expected, string forbidden = "")
    {
        int cursor = source.IndexOf('|');
        source = source.Remove(cursor, 1);
        var index = fire.Editor.ScriptSymbolIndex.Build(source);
        var names = fire.Editor.CompletionEngine.GetSuggestions(source, cursor, index).Select(i => i.Text).ToList();
        bool ok = expected.Split(',', StringSplitOptions.RemoveEmptyEntries).All(names.Contains)
            && !forbidden.Split(',', StringSplitOptions.RemoveEmptyEntries).Any(names.Contains);
        if (!ok) failures++;
        Console.WriteLine($"{(ok ? "OK" : "FEHLER")}: {title} -> {string.Join(", ", names.Take(8))}");
    }
    const string userExt = "class extends string { string Shout() { return this.ToUpper() } }\nclass extends int { bool IsEven() { return true } }\n";
    Check("eigene string-Methode", userExt + "var s = \"a\"\ns.|", "Shout,IndexOf,Length");
    Check("eigene Methode in der Kette", userExt + "var s = \"a\"\ns.Shout().|", "Shout,Trim,Length");
    Check("int-Erweiterung", userExt + "int n = 4\nn.|", "IsEven,ToChar", "Shout");
    Check("char-Methoden aus dem Prelude", "char c = 'x'\nc.|", "IsDigit,IsLetter,ToUpper,ToByte", "Shout");
    Check("Sammelklasse taucht nicht als Typ auf", userExt + "var x = |", "string", "$string,$int");
    Check("Split-Kette liefert string", "var s = \"a,b\"\ns.Split(\",\")[0].|", "Substring,Trim");
    Console.WriteLine(failures == 0 ? "Alle Basistyp-Editor-Pruefungen bestanden." : $"FEHLER: {failures} Pruefung(en) fehlgeschlagen.");
}

Console.WriteLine();
Console.WriteLine("=== Array als Rueckgabetyp: Editor ===");
{
    int failures = 0;
    void Check(string title, string source, string expected, string forbidden = "")
    {
        int cursor = source.IndexOf('|');
        source = source.Remove(cursor, 1);
        var index = fire.Editor.ScriptSymbolIndex.Build(source);
        var names = fire.Editor.CompletionEngine.GetSuggestions(source, cursor, index).Select(i => i.Text).ToList();
        bool ok = expected.Split(',', StringSplitOptions.RemoveEmptyEntries).All(names.Contains)
            && !forbidden.Split(',', StringSplitOptions.RemoveEmptyEntries).Any(names.Contains);
        if (!ok) failures++;
        Console.WriteLine($"{(ok ? "OK" : "FEHLER")}: {title} -> {string.Join(", ", names.Take(8))}");
    }
    const string kennel = "class Dog { Bark() { } }\nclass Kennel {\n  Dog[] Dogs() { return [new Dog()] }\n  string[] Names() { return [\"a\"] }\n  int[8] Small() { return 1 }\n}\nvar k = new Kennel()\n";
    Check("Element eines Array-Rueckgabetyps", kennel + "k.Dogs()[0].|", "Bark", "Length");
    Check("Array-Rueckgabetyp hat Length", kennel + "k.Names().|", "Length", "Bark");
    Check("Element eines string[]", kennel + "k.Names()[0].|", "Substring,Trim");
    Check("Bitbreite ist kein Array", kennel + "k.Small().|", "ToChar", "Length");
    Console.WriteLine(failures == 0 ? "Alle Array-Editor-Pruefungen bestanden." : $"FEHLER: {failures} Pruefung(en) fehlgeschlagen.");
}

Console.WriteLine();
Console.WriteLine("=== Kopieren: Editor ===");
{
    int failures = 0;
    void Check(string title, string source, string expected, string forbidden = "")
    {
        int cursor = source.IndexOf('|');
        source = source.Remove(cursor, 1);
        var index = fire.Editor.ScriptSymbolIndex.Build(source);
        var names = fire.Editor.CompletionEngine.GetSuggestions(source, cursor, index).Select(i => i.Text).ToList();
        bool ok = expected.Split(',', StringSplitOptions.RemoveEmptyEntries).All(names.Contains)
            && !forbidden.Split(',', StringSplitOptions.RemoveEmptyEntries).Any(names.Contains);
        if (!ok) failures++;
        Console.WriteLine($"{(ok ? "OK" : "FEHLER")}: {title} -> {string.Join(", ", names.Take(8))}");
    }
    const string dogs = "class Dog { Bark() { } }\nclass Cat { Purr() { } }\nvar a = new Dog()\n";
    Check("var d = copy a", dogs + "var d = copy a\nd.|", "Bark", "Purr");
    Check("var f = flat a.Self", "class Dog { Bark() { } }\nclass Kennel { Dog dog }\nvar k = new Kennel()\nvar f = flat k.dog\nf.|", "Bark");
    Check("copy/flat als Schluesselwoerter", "var x = 1\nco|", "copy");
    Check("flat als Schluesselwort", "var x = 1\nfla|", "flat");
    Console.WriteLine(failures == 0 ? "Alle Kopier-Editor-Pruefungen bestanden." : $"FEHLER: {failures} Pruefung(en) fehlgeschlagen.");
}

packerTests:
// ---------------------------------------------------------------------------
// Packer: self-contained file (bundle + payload), bridges only when needed, loader instead of Costura
// ---------------------------------------------------------------------------
{
    Console.WriteLine();
    Console.WriteLine("=== Packer / Payload / Lader ===");
    int packFailures = 0;
    void PackCheck(bool ok, string what)
    {
        if (!ok) packFailures++;
        Console.WriteLine(ok ? $"OK: {what}" : $"FEHLER: {what}");
    }

    var baseDir = AppContext.BaseDirectory;

    // (1) - 4), the .NET packer of the C# VM - plan, payload, loader, packed file - went with it, docs/ROADMAP_C_PORT.md M1)
    // `windows` is separate from `graphics`: the window exists only with its own import, which brings `graphics` along; `graphics` alone does not know a Window
    {
        string LinkResult(string src)
        {
            try { var linked = new Linker().CompileAndLink(new[] { src }); return string.Join(",", linked.NativeImports.OrderBy(x => x)); }
            catch (Exception ex) { return "FEHLER " + ex.Message; }
        }
        string onlyGfx = LinkResult("#import \"graphics\"\nvar w = new Window(new Framebuffer(8, 8), \"t\")");
        PackCheck(onlyGfx.StartsWith("FEHLER") && onlyGfx.Contains("Window"), "Import: graphics allein kennt kein Window (" + onlyGfx.Split('\n')[0] + ")");
        string withWin = LinkResult("#import \"windows\"\nvar x = EventType.Close");
        PackCheck(withWin == "graphics,print,windows", "Import: windows bringt graphics mit (" + withWin + ")");
        string withUi = LinkResult("#import \"ui\"\nvar x = EventType.Close");
        PackCheck(withUi == "graphics,print,reflection,ui,windows", "Import: ui bringt graphics, windows und reflection mit (" + withUi + ")");
        string gfxOnly = LinkResult("#import \"graphics\"\nvar fb = new Framebuffer(8, 8)");
        PackCheck(gfxOnly == "graphics,print", "Import: graphics allein ohne Fenster (" + gfxOnly + ")");
    }

    var tmpDir = Path.Combine(Path.GetTempPath(), "fire-packtest-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmpDir);
    try
    {
        // 5) The VM in C (phase B steps 7 and 8, docs/spec/VM_C.md "Gepackte Programme"): `forge build --engine vm` packs `fire` with the program file and the
        //    libraries of its packages behind it; the libraries are written into the cache of the user (here FIRE_VM_CACHE in the test folder).
        Console.WriteLine("--- die VM in C (fire) ---");
        {
            var cvFile = Path.Combine(tmpDir, "cv.bin");
            var cvData = Enumerable.Range(0, 3000).Select(i => (byte)(i * 7)).ToArray();
            using (var fs = new FileStream(cvFile, FileMode.Create))
            {
                fs.Write(System.Text.Encoding.ASCII.GetBytes("FIREVMP1 kein Payload, nur ein Kopf"));
                CVmPayload.Append(fs, new[] { (CVmPayload.ProgramKind, "program.fbc", new byte[] { 1, 2, 3 }), (CVmPayload.LibraryKind, "libx.so", cvData) });
            }
            var cvEntries = CVmPayload.Open(cvFile);
            PackCheck(cvEntries != null && cvEntries.Count == 2 && cvEntries[1].Name == "libx.so" && CVmPayload.Read(cvFile, cvEntries[1])!.SequenceEqual(cvData)
                && CVmPayload.ReadProgram(cvFile)!.SequenceEqual(new byte[] { 1, 2, 3 }), "C-VM-Payload: Index am Ende gefunden, Rundlauf stimmt");
            var cvBytes = File.ReadAllBytes(cvFile);
            cvBytes[(int)cvEntries![1].Offset + 5] ^= 0xFF;
            File.WriteAllBytes(cvFile, cvBytes);
            PackCheck(CVmPayload.Read(cvFile, CVmPayload.Open(cvFile)![1]) == null, "C-VM-Payload: ein beschaedigter Eintrag wird erkannt (FNV-1a)");
            File.WriteAllBytes(Path.Combine(tmpDir, "plain.bin"), new byte[1000]);
            PackCheck(CVmPayload.Open(Path.Combine(tmpDir, "plain.bin")) == null, "C-VM-Payload: eine Datei ohne Payload liefert null");
            bool badName = false;
            try { using var fs = new MemoryStream(); CVmPayload.Append(fs, new[] { (CVmPayload.LibraryKind, "../x.so", new byte[1]) }); } catch (ArgumentException) { badName = true; }
            PackCheck(badName, "C-VM-Payload: ein Name, der den Ordner verlaesst, wird abgelehnt");
            PackCheck(CVm.VariantName(64, 64) == "fire" && CVm.VariantName(32, 32) == "fire-i32-f32" && CVm.VariantName(16, 64) == "fire-i16" && CVm.ParseVariant("i32-f32") == (32, 32)
                && CVm.ParseVariant("default") == (64, 64) && CVm.ParseVariant("i8") == null, "C-VM: die Namen der Varianten nach Breiten");

            string? fireExe = null;
            try { fireExe = CVm.Locate(64, 64); } catch (InvalidOperationException ex) { Console.WriteLine("(die VM in C fehlt: " + ex.Message.Split('\n')[0] + ")"); }
            if (fireExe != null)
            {
                PackCheck(Path.GetDirectoryName(fireExe) == Path.GetDirectoryName(Path.Combine(baseDir, "x")) || fireExe.StartsWith(CVm.CacheFolder), $"C-VM: fire liegt neben forge (der Build, FireVm.targets) oder im Cache ({fireExe})");
                string cvCache = Path.Combine(tmpDir, "vmcache");
                (int Code, string Out, string Err) RunExe(string exe, string workDir, params string[] args)
                {
                    var psi = new System.Diagnostics.ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = workDir };
                    foreach (var a in args) psi.ArgumentList.Add(a);
                    psi.Environment["FIRE_VM_CACHE"] = cvCache;
                    psi.Environment.Remove("FIRE_PKG_PATH");
                    using var proc = System.Diagnostics.Process.Start(psi)!;
                    var stderrTask = proc.StandardError.ReadToEndAsync();
                    var output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(60000);
                    return (proc.ExitCode, output.Replace("\r\n", "\n"), stderrTask.Result.Replace("\r\n", "\n"));
                }
                string Build(string source, string name)
                {
                    var exe = Path.Combine(tmpDir, name + (OperatingSystem.IsWindows() ? ".exe" : ""));
                    new Linker().CompileAndLink(new[] { source }, null, exe);
                    return exe;
                }
                string elsewhere = Path.Combine(tmpDir, "anderswo");
                Directory.CreateDirectory(elsewhere);

                var cHello = Build("print(\"hallo\")\nprint(\"welt\")", "c_hello");
                var rHello = RunExe(cHello, elsewhere);
                long cHelloSize = new FileInfo(cHello).Length;
                PackCheck(rHello.Code == 0 && rHello.Out == "hallo\nwelt\n", $"C-VM Ende-zu-Ende: die gepackte Datei laeuft allein ({rHello.Code}: {rHello.Out.Trim()}{rHello.Err})");
                PackCheck(File.ReadAllBytes(cHello).AsSpan(0, (int)new FileInfo(fireExe).Length).SequenceEqual(File.ReadAllBytes(fireExe)) && CVmPayload.Open(cHello) is { Count: 1 }
                    && fire.Bytecode.ProgramFile.Read(CVmPayload.ReadProgram(cHello)!) != null, "C-VM: die Datei ist fire mit dem Programm dahinter (ohne Bibliotheken), das Programm laesst sich lesen");

                var cArgs = Build("print(Environment.Args.Length)\nforeach (a in Environment.Args) print(\"[\" + a + \"]\")\nterminate(4)", "c_args");
                var rArgs = RunExe(cArgs, elsewhere, "a", "b c", "--stats");
                PackCheck(rArgs.Code == 4 && rArgs.Out == "3\n[a]\n[b c]\n[--stats]\n", $"C-VM: jedes Argument gehoert dem Programm, terminate(4) ist der Exitcode ({rArgs.Code}: {rArgs.Out.Replace("\n", "|")})");
                var cThrow = Build("print(\"vorher\")\nthrow new Exception(\"bumm\")", "c_throw");
                var rThrow = RunExe(cThrow, elsewhere);
                PackCheck(rThrow.Code == 1 && rThrow.Out == "vorher\n" && rThrow.Err.Contains("bumm"), $"C-VM: eine unbehandelte Ausnahme endet mit 1 und ihrem Text ({rThrow.Code}: {rThrow.Err.Trim()})");

                var cIo = Build("#import \"io\"\n#import \"time\"\nIO.Stdio.WriteLine(\"io ok\")\nprint(DateTime.Now.Year > 2000)", "c_io");
                var ioEntries = CVmPayload.Open(cIo)!;
                PackCheck(ioEntries.Any(e => e.Kind == CVmPayload.LibraryKind && e.Name.Contains("fire_io_io")) && ioEntries.Any(e => e.Name.Contains("fire_time_time")), "C-VM: die Bibliotheken der Pakete (io, time) reisen in der Datei mit");
                var rIo = RunExe(cIo, elsewhere);
                PackCheck(rIo.Code == 0 && rIo.Out == "io ok\nTrue\n", $"C-VM: die Natives der Pakete laufen aus den Bibliotheken der Datei ({rIo.Code}: {rIo.Out.Trim()}{rIo.Err})");
                var ioLib = Directory.Exists(cvCache) ? Directory.GetFiles(cvCache, "libfire_pkg_fire_io_io*", SearchOption.AllDirectories).Concat(Directory.GetFiles(cvCache, "fire_pkg_fire_io_io*", SearchOption.AllDirectories)).FirstOrDefault() : null;
                PackCheck(ioLib != null, "C-VM: die Bibliotheken liegen im Cache (ein Ordner nach ihren Pruefsummen)");
                if (ioLib != null)
                {
                    var written = File.GetLastWriteTimeUtc(ioLib);
                    System.Threading.Thread.Sleep(20);
                    RunExe(cIo, elsewhere);
                    PackCheck(File.GetLastWriteTimeUtc(ioLib) == written, "C-VM: der zweite Start nimmt die Bibliothek im Cache, ohne sie neu zu schreiben");
                    File.WriteAllBytes(ioLib, new byte[] { 1, 2, 3 });
                    var rAgain = RunExe(cIo, elsewhere);
                    PackCheck(rAgain.Code == 0 && rAgain.Out == "io ok\nTrue\n" && new FileInfo(ioLib).Length > 3, "C-VM: eine beschaedigte Bibliothek im Cache wird neu geschrieben");
                }

                var cWide = Build("#intwidth 32\nint x = 2147483647\nx = x + 1\nprint(x)", "c_i32");
                var rWide = RunExe(cWide, elsewhere);
                PackCheck(rWide.Code == 0 && rWide.Out == "-2147483648\n", $"C-VM: #intwidth 32 packt die Variante fire-i32 ({rWide.Code}: {rWide.Out.Trim()}{rWide.Err})");

                // `fire` itself: run, the arguments after the program, help, version, the exit code of the command line
                var fbc = Path.Combine(tmpDir, "args.fbc");
                File.WriteAllBytes(fbc, fire.Bytecode.ProgramFile.Write(new Linker().CompileAndLink(new[] { "foreach (a in Environment.Args) print(a)\nterminate(6)" })));
                var rRun = RunExe(fireExe, tmpDir, "run", fbc, "x", "--y");
                var rRun2 = RunExe(fireExe, tmpDir, "run", fbc, "--", "z");
                var rOld = RunExe(fireExe, tmpDir, fbc, "--", "w");
                PackCheck(rRun.Code == 6 && rRun.Out == "x\n--y\n" && rRun2.Out == "z\n" && rOld.Code == 6 && rOld.Out == "w\n", $"fire run x.fbc [--] args: die Argumente hinter dem Programm, der Code von terminate ({rRun.Code} {rRun.Out.Replace("\n", "|")} {rRun2.Out.Replace("\n", "|")} {rOld.Out.Replace("\n", "|")})");
                var rVersion = RunExe(fireExe, tmpDir, "version");
                var rHelp = RunExe(fireExe, tmpDir, "help");
                var rNone = RunExe(fireExe, tmpDir);
                var rBad = RunExe(fireExe, tmpDir, "--gibtsnicht", fbc);
                var rMissing = RunExe(fireExe, tmpDir, "run", Path.Combine(tmpDir, "nix.fbc"));
                PackCheck(rVersion.Code == 0 && rVersion.Out.StartsWith("fire ") && rHelp.Code == 0 && rHelp.Out.Contains("fire run") && rNone.Code == 2 && rBad.Code == 2 && rBad.Err.Contains("--gibtsnicht") && rMissing.Code == 2,
                    "fire: version, help (0), ohne Programm, eine unbekannte Option, eine fehlende Datei (2)");

                Console.WriteLine($"Groesse hello world: VM in C {cHelloSize} B (FIRE_VM_ENGINE=dotnet, die .NET-Runtime, ist mit M1 weg)");
            }
        }
    }
    finally
    {
        try { Directory.Delete(tmpDir, true); } catch (IOException) { }
    }

    Console.WriteLine(packFailures == 0 ? "Alle Packer-Pruefungen bestanden." : $"FEHLER: {packFailures} Packer-Pruefung(en) fehlgeschlagen.");
}

// ---------------------------------------------------------------------------
// Packer: the subsystem of a Windows program (PE header), the native targets (the single-file runtime of the C# VM went with it, M1)
// ---------------------------------------------------------------------------
{
    Console.WriteLine();
    Console.WriteLine("=== Packer: Subsystem und native Ziele ===");
    int sfFailures = 0;
    void SfCheck(bool ok, string what)
    {
        if (!ok) sfFailures++;
        Console.WriteLine(ok ? $"OK: {what}" : $"FEHLER: {what}");
    }

    // a minimal PE file: DOS header, "PE", one section
    byte[] SyntheticPe()
    {
        var pe = new byte[0x400];
        pe[0] = (byte)'M'; pe[1] = (byte)'Z';
        BitConverter.GetBytes(0x40).CopyTo(pe, 0x3C);
        pe[0x40] = (byte)'P'; pe[0x41] = (byte)'E';
        BitConverter.GetBytes((ushort)1).CopyTo(pe, 0x40 + 6);        // sections
        BitConverter.GetBytes((ushort)0xF0).CopyTo(pe, 0x40 + 20);    // size of the optional header
        BitConverter.GetBytes((ushort)0x20B).CopyTo(pe, 0x40 + 24);   // PE32+
        BitConverter.GetBytes((ushort)3).CopyTo(pe, 0x40 + 24 + 68);  // console subsystem
        int table = 0x40 + 24 + 0xF0;
        BitConverter.GetBytes(0x200u).CopyTo(pe, table + 16);         // SizeOfRawData
        BitConverter.GetBytes(0x200u).CopyTo(pe, table + 20);         // PointerToRawData
        return pe;
    }

    var sfDir = Path.Combine(Path.GetTempPath(), "fire-sf-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(sfDir);
    try
    {
        var linkedSf = new Linker().CompileAndLink(new[] { "print(\"sf\")" }, null, null);
        var gui = SyntheticPe();
        var guiPath = Path.Combine(sfDir, "gui.exe");
        File.WriteAllBytes(guiPath, gui);
        fire.Utilities.PeResourceEditor.SetSubsystem(guiPath, fire.Utilities.SubsystemType.GUI);
        SfCheck(BitConverter.ToUInt16(File.ReadAllBytes(guiPath), 0x40 + 24 + 68) == 2, "das Subsystem GUI steht im PE-Kopf");
        File.WriteAllBytes(guiPath, new byte[] { 1, 2, 3 });
        fire.Utilities.PeResourceEditor.SetSubsystem(guiPath, fire.Utilities.SubsystemType.GUI);
        SfCheck(File.ReadAllBytes(guiPath).SequenceEqual(new byte[] { 1, 2, 3 }), "eine Datei ohne PE-Kopf bleibt beim Setzen des Subsystems unberuehrt");

        // the native build: -mwindows / /SUBSYSTEM:WINDOWS only for a GUI program on Windows
        TargetProfile.TryGet("windows", out var winTarget);
        var gccCmd = fire.Compiler.NativeBuilder.CompilerCommand(fire.Native.ToolchainDef.BuiltIn["gcc"], winTarget!, "p.cpp", sfDir, "p.exe", gui: true).Arguments;
        var gccConsole = fire.Compiler.NativeBuilder.CompilerCommand(fire.Native.ToolchainDef.BuiltIn["gcc"], winTarget!, "p.cpp", sfDir, "p.exe", gui: false).Arguments;
        var msvcCmd = fire.Compiler.NativeBuilder.CompilerCommand(fire.Native.ToolchainDef.BuiltIn["msvc"], winTarget!, "p.cpp", sfDir, "p.exe", gui: true).Arguments;
        SfCheck(gccCmd.Contains("-mwindows") && !gccConsole.Contains("-mwindows"), "nativer Build (gcc): #noconsole setzt -mwindows, sonst nicht");
        SfCheck(msvcCmd.Contains("/SUBSYSTEM:WINDOWS") && msvcCmd.Contains("/ENTRY:mainCRTStartup"), "nativer Build (msvc): #noconsole setzt /SUBSYSTEM:WINDOWS");
        // size: executables are stripped by default; "size" is -Os with section garbage collection; strip:false keeps the symbols (debugging); libraries stay as they are
        {
            var gccBase = fire.Native.ToolchainDef.BuiltIn["gcc"];
            string plain = fire.Compiler.NativeBuilder.CompilerCommand(gccBase, winTarget!, "p.cpp", sfDir, "p.exe").Arguments;
            string small = fire.Compiler.NativeBuilder.CompilerCommand(new fire.Native.ToolchainDef { Extends = "gcc", Kind = "gcc", Optimization = "size" }, winTarget!, "p.cpp", sfDir, "p.exe").Arguments;
            string debug = fire.Compiler.NativeBuilder.CompilerCommand(new fire.Native.ToolchainDef { Kind = "gcc", Strip = false }, winTarget!, "p.cpp", sfDir, "p.exe").Arguments;
            string lib = fire.Compiler.NativeBuilder.CompilerCommand(gccBase, winTarget!, "p.cpp", sfDir, "p.dll", sharedLibrary: true).Arguments;
            string msvcSmall = fire.Compiler.NativeBuilder.CompilerCommand(new fire.Native.ToolchainDef { Kind = "msvc", Optimization = "size" }, winTarget!, "p.cpp", sfDir, "p.exe").Arguments;
            SfCheck(plain.Split(' ').Contains("-s") && plain.Contains("-O2") && !plain.Contains("gc-sections"), "nativer Build (gcc): ein Programm wird gestrippt (-s), -O2 bleibt");
            SfCheck(small.Contains("-Os") && small.Contains("-ffunction-sections") && small.Contains("--gc-sections") && !small.Contains("-O2"), "nativer Build (gcc): Optimization size = -Os mit gc-sections");
            SfCheck(!debug.Split(' ').Contains("-s"), "nativer Build (gcc): strip=false laesst die Symbole stehen");
            SfCheck(!lib.Split(' ').Contains("-s") && !lib.Contains("gc-sections"), "nativer Build (gcc): eine Bibliothek wird nicht gestrippt");
            SfCheck(msvcSmall.Contains("/O1") && msvcSmall.Contains("/OPT:REF"), "nativer Build (msvc): Optimization size = /O1 und /OPT:REF");
        }
        // the native build carries version info and icon to the executable
        var nativeInfo = fire.Compiler.NativeBuilder.GenerateLinked(new[] { "#name \"Demo\"\n#author \"ACME\"\n#version \"1.2.3.4\"\n#icon \"x.ico\"\nprint(\"x\")" }, TargetProfile.Host);
        SfCheck(nativeInfo.VersionInfo != null && nativeInfo.VersionInfo.ProductVersion.ToString() == "1.2.3.4" && nativeInfo.IconPath == "x.ico", "nativer Build: Versionsinfo und Icon der Direktiven kommen beim Build an");
        var applyLog = new System.Text.StringBuilder();
        fire.Compiler.NativeBuilder.ApplyPeMetadata(Path.Combine(sfDir, "none.exe"), nativeInfo, winTarget!, applyLog);
        SfCheck(applyLog.Length == 0, "nativer Build: eine fehlende Datei oder ein anderes System laesst die Metadaten in Ruhe");
        // the target wasm (WebAssembly in the browser, Emscripten)
        {
            bool known = TargetProfile.TryGet("wasm", out var wasm);
            SfCheck(known && wasm!.Native.Platform == "wasm" && wasm.Native.SupportsThreads && wasm.Native.Toolchain == "emscripten" && wasm.Symbols.Contains("browser") && wasm.Symbols.Contains("wasm"), "wasm: das Ziel gibt es (Plattform wasm, Toolchain emscripten, Threads ueber den eigenen Scheduler, Symbole wasm und browser)");
            var wasmCmd = fire.Compiler.NativeBuilder.CompilerCommand(fire.Native.ToolchainDef.BuiltIn["emscripten"], wasm!, "p.cpp", sfDir, "p.html");
            SfCheck(wasmCmd.Executable.EndsWith("em++") && wasmCmd.Arguments.Contains("-fexceptions") && wasmCmd.Arguments.Contains("-sASYNCIFY") && wasmCmd.Arguments.Contains("-sSINGLE_FILE=1") && wasmCmd.Arguments.Contains("p.html"),
                "wasm: der Aufruf von em++ (Ausnahmen, ASYNCIFY, eine Datei, Ausgabe .html)");
            SfCheck(fire.Compiler.NativeBuilder.DefaultOutput(wasm) == "out.html", "wasm: die Standardausgabe ist out.html");
            SfCheck(fire.Compiler.NativeBuilder.DefaultOutput(TargetProfile.Dos16) == "out.exe" && fire.Compiler.NativeBuilder.DefaultOutput(TargetProfile.Win16) == "out.exe"
                && fire.Compiler.NativeBuilder.DefaultOutput(TargetProfile.Dos32) == "out.exe"
                && fire.Projects.ProjectOutput.ProgramPath("p", "Demo", "win16", "Release", TargetProfile.Win16.ProgramExtension).EndsWith("Demo.exe", StringComparison.Ordinal),
                "dos16, dos32, win16: das Programm heisst .exe (Standardausgabe und Projektausgabe)");
            string wasmCpp = fire.Native.CppGenerator.Generate(new Linker().CompileAndLink(new[] { "#if wasm\nprint(\"browser\")\n#else\nprint(\"desktop\")\n#endif" }, null, null, VmExecutionMode.Release, null, wasm), wasm);
            SfCheck(wasmCpp.Contains("platform/wasm/fire_platform.h\"") && wasmCpp.Contains("\"browser\"") && !wasmCpp.Contains("\"desktop\""), "wasm: das erzeugte C++ nimmt die Plattform wasm, #if wasm gilt");
            string threadCpp = fire.Native.CppGenerator.Generate(new Linker().CompileAndLink(new[] { "fire { print(1) }" }, null, null, VmExecutionMode.Release, null, wasm), wasm);
            SfCheck(threadCpp.Contains("FIRE_THREADS") && threadCpp.Contains("int main(void)"), "wasm: ein fire-Thread wird uebersetzt (eigener Scheduler), main ohne Argumente (Emscripten ruft main(argc, argv) nicht immer)");
            // with Emscripten and node on this machine: the program is built for the browser's WebAssembly and runs (here under node); it prints what the VM prints (exceptions, floats, Sleep)
            string? FindOnPath(string name) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);
            if (FindOnPath("em++") != null && FindOnPath("node") != null)
            {
                const string wasmSource = "#import \"time\"\nclass Oops : Exception { construct(string m) : base(m) { } }\nvar sum = 0\nfor (var i = 0; i < 5; i++) { sum = sum + i; Sleep(1ms) }\nprint(\"sum \" + sum + \" \" + (sum / 4.0) + \" \" + (1.0 / 3.0))\ntry { throw new Oops(\"caught\") } catch (Oops e) { print(e.message) }\n";
                string wasmDir = Path.Combine(sfDir, "wasm");
                fire.Native.NativeRuntimeFiles.WriteTo(wasmDir, "wasm");
                // (the program is C: emcc with -std=gnu11 - EM_ASM needs the GNU mode)
                string EmBuild(string source, string name, out string log)
                {
                    string c = Path.Combine(wasmDir, name + ".c");
                    File.WriteAllText(c, fire.Native.CppGenerator.GenerateSources(new Linker().CompileAndLink(new[] { source }, null, null, VmExecutionMode.Release, null, wasm), wasm).Program);
                    string output = Path.Combine(wasmDir, name + (name.StartsWith("win") ? ".html" : ".js"));
                    var (emExe, emArgs) = fire.Compiler.NativeBuilder.CompilerCommand(fire.Native.ToolchainDef.BuiltIn["emscripten"], wasm, c, wasmDir, output, language: "c");
                    using var em = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(emExe, emArgs) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = wasmDir })!;
                    var emErr = em.StandardError.ReadToEndAsync();
                    em.StandardOutput.ReadToEnd();
                    em.WaitForExit();
                    log = emExe + " " + emArgs + "\n" + emErr.Result;
                    return File.Exists(output) && em.ExitCode == 0 ? output : "";
                }
                string pJs = EmBuild(wasmSource, "p", out string emLog);
                bool emBuilt = pJs != "";
                SfCheck(emBuilt && !emLog.Contains("warning:"), "wasm: emcc baut das Programm (C, p.js mit eingebetteter WebAssembly) ohne Warnung" + (emBuilt && !emLog.Contains("warning:") ? "" : "\n" + emLog));
                if (emBuilt)
                {
                    using var node = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("node", $"\"{Path.Combine(wasmDir, "p.js")}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
                    var nodeErr = node.StandardError.ReadToEndAsync();
                    string nodeOut = node.StandardOutput.ReadToEnd().Replace("\r\n", "\n");
                    node.WaitForExit();
                    var vmLines = CVmTest.Run(new[] { wasmSource }, VmExecutionMode.Release).Printed;   // (the VM in C as the reference)
                    SfCheck(nodeOut == string.Join("\n", vmLines) + "\n", "wasm: das Programm laeuft (node) und druckt wie die VM (Sleep, Zahlen, Ausnahmen)");
                    // fire threads: tasks of fire's own scheduler on the fibers of Emscripten (std/sched/fire_ctx_emscripten.h)
                    const string wasmThreads = """
                        #import "time"
                        var done = 0
                        fire { var t = 0
                          for (var i = 0; i < 200000; i++) { t = t + i }
                          Sleep(20ms)
                          sync global { done = t } }
                        var n = 0
                        while (done == 0) { n++; Sleep(1ms) }
                        print("thread " + done + " " + (n > 0))
                        """;
                    string thrJs = EmBuild(wasmThreads, "pthr", out string thrLog);
                    string thrOut = "";
                    if (thrJs != "")
                    {
                        using var tn = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("node", $"\"{thrJs}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
                        tn.StandardError.ReadToEndAsync();
                        thrOut = tn.StandardOutput.ReadToEnd().Replace("\r\n", "\n");
                        tn.WaitForExit();
                    }
                    SfCheck(thrOut == "thread 19999900000 True\n", "wasm: ein fire-Thread (eigener Scheduler auf den Fibers von Emscripten), Sleep in Thread und Hauptprogramm, sync global" + (thrJs == "" ? "\n" + thrLog : "\n  node: " + thrOut));
                    // linkage "static" (ROADMAP 9): the modules as static libraries of Emscripten (emar), in a cache of their own; the same output
                    string? cacheBefore = Environment.GetEnvironmentVariable("FIRE_NATIVE_CACHE");
                    Environment.SetEnvironmentVariable("FIRE_NATIVE_CACHE", Path.Combine(wasmDir, "cache"));
                    try
                    {
                        string staticJs = Path.Combine(wasmDir, "pstatic.js");
                        var staticBuilt = fire.Compiler.NativeBuilder.BuildSafe(new[] { wasmSource }, new fire.Native.NativeConfig { Linkage = "static" }, wasm, fire.Native.ToolchainDef.BuiltIn["emscripten"], staticJs);
                        string staticOut = "";
                        if (staticBuilt.Ok)
                        {
                            using var sn = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("node", $"\"{staticJs}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
                            sn.StandardError.ReadToEndAsync();
                            staticOut = sn.StandardOutput.ReadToEnd().Replace("\r\n", "\n");
                            sn.WaitForExit();
                        }
                        SfCheck(staticBuilt.Ok && staticBuilt.Log.Contains("module core: built in") && !staticBuilt.Log.Contains("not possible") && staticOut == nodeOut,
                            "wasm: linkage static - die Module als Bibliotheken von Emscripten (emar), das Programm druckt dasselbe" + (staticBuilt.Ok ? "" : "\n" + staticBuilt.Log));
                    }
                    finally { Environment.SetEnvironmentVariable("FIRE_NATIVE_CACHE", cacheBefore); }
                }
                // a window (fire's own display: packages/windows/native/fire_display_web.h, the canvas of the page):
                // it draws into the canvas of the page; with Playwright (the Chromium of this machine) the page runs headless and says what it printed and drew
                const string winSource = "#import \"windows\"\n#import \"time\"\nvar fb = new Framebuffer(40, 30)\nvar win = new Window(fb, \"wasm\")\nvar r = new Renderer(fb)\nr.Clear()\nfor (var y = 0; y < 30; y++) { for (var x = 0; x < 40; x++) { r.SetPixel(x, y, 0xFFF08020) } }\nfor (var i = 0; i < 3; i++) { win.Tick(); Sleep(10ms) }\nvar px = fb.ReadBytes()\nprint(\"pixel \" + px[0] + \" \" + px[1] + \" \" + px[2])\nprint(\"done\")\nfor (var i = 0; i < 300; i++) { win.Tick(); Sleep(10ms) }\n";
                string winHtml = EmBuild(winSource, "win", out string winLog);
                SfCheck(winHtml != "" && !winLog.Contains("warning:"), "wasm: ein Programm mit Fenster baut mit der eigenen Anzeige (das Canvas der Seite), ohne Warnung" + (winHtml != "" && !winLog.Contains("warning:") ? "" : "\n" + winLog));
                bool playwright = false;
                try
                {
                    using var probe = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("node", "-e \"require('playwright')\"") { RedirectStandardError = true, UseShellExecute = false })!;
                    probe.StandardError.ReadToEnd(); probe.WaitForExit(); playwright = probe.ExitCode == 0;
                }
                catch (System.ComponentModel.Win32Exception) { }
                if (winHtml != "" && playwright)
                {
                    string script = Path.Combine(wasmDir, "page.js");
                    File.WriteAllText(script, """
                        const { chromium } = require('playwright');
                        (async () => {
                          const b = await chromium.launch(); const p = await b.newPage(); const out = [];
                          p.on('console', m => { if (!m.text().includes('GL Driver')) out.push(m.text()); });
                          p.on('pageerror', e => out.push('error ' + e.message));
                          await p.goto('file://' + process.argv[2]);
                          for (let i = 0; i < 100 && !out.includes('done'); i++) await p.waitForTimeout(100);
                          const canvas = await p.evaluate(() => { const c = document.getElementById('canvas'); const t = document.createElement('canvas'); t.width = c.width; t.height = c.height;
                            const g = t.getContext('2d'); g.drawImage(c, 0, 0); const d = g.getImageData(c.width >> 1, c.height >> 1, 1, 1).data; return c.width + 'x' + c.height + ' ' + d[0] + ' ' + d[1] + ' ' + d[2]; });
                          console.log(out.join('\n')); console.log('canvas ' + canvas); await b.close();
                        })();
                        """);
                    using var page = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("node", $"\"{script}\" \"{winHtml}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = wasmDir })!;
                    var pageErr = page.StandardError.ReadToEndAsync();
                    string pageOut = page.StandardOutput.ReadToEnd().Replace("\r\n", "\n");
                    page.WaitForExit();
                    // (the colour r + g*256 + b*65536 + a*2^24: the bytes of the framebuffer are R G B A, the canvas shows the same colour)
                    SfCheck(pageOut.Contains("pixel 32 128 240\ndone\n") && pageOut.Contains("canvas 40x30 32 128 240"), "wasm: das Fenster laeuft im Browser (Chromium, headless): es druckt und zeichnet ins Canvas\n" + pageOut + pageErr.Result);
                }
                else Console.WriteLine("(wasm im Browser: uebersprungen - " + (winHtml == "" ? "das Fenster-Programm baute nicht" : "Playwright fehlt") + ")");
            }
        }
        var noconsole = new Linker().CompileAndLink(new[] { "#noconsole\nprint(\"x\")" }, null, null);
        SfCheck(noconsole.GuiSubsystem && !linkedSf.GuiSubsystem, "der Linker meldet das Subsystem (#noconsole) an das gelinkte Programm");
    }
    finally
    {
        try { Directory.Delete(sfDir, true); } catch (IOException) { }
    }

    Console.WriteLine(sfFailures == 0 ? "Alle Pruefungen von Subsystem und nativen Zielen bestanden." : $"FEHLER: {sfFailures} Pruefung(en) von Subsystem und nativen Zielen fehlgeschlagen.");
}
if (Environment.GetEnvironmentVariable("FIRE_TESTS_ONLY") == "packer") return;

// ---------------------------------------------------------------------------
// Command line of the compiler (run / build)
// ---------------------------------------------------------------------------
{
    Console.WriteLine();
    Console.WriteLine("=== Befehlszeile: run / build ===");
    int cliFailures = 0;
    void CliCheck(bool ok, string what)
    {
        if (!ok) cliFailures++;
        Console.WriteLine(ok ? $"OK: {what}" : $"FEHLER: {what}");
    }

    var r1 = CommandLineParser.Parse(new[] { "run", "code1", "codeN" });
    CliCheck(r1.Error == null && r1.Command == CommandKind.Run && r1.Files.SequenceEqual(new[] { "code1", "codeN" }) && r1.Mode == null, "run code1 codeN");
    var r2 = CommandLineParser.Parse(new[] { "run", "code1", "codeN", "-m", "DEBUG" });
    CliCheck(r2.Error == null && r2.Mode == VmExecutionMode.Debug && r2.Files.Count == 2, "run ... -m DEBUG");
    CliCheck(CommandLineParser.Parse(new[] { "RUN", "a", "-m", "performance" }).Mode == VmExecutionMode.Performance, "Befehl und Modus ohne Beachtung der Gross-/Kleinschreibung");
    CliCheck(CommandLineParser.Parse(new[] { "run", "--mode=release", "a" }).Mode == VmExecutionMode.Release, "--mode=release vor der Datei");
    var b1 = CommandLineParser.Parse(new[] { "build", "code1", "codeN" });
    CliCheck(b1.Error == null && b1.Command == CommandKind.Build && b1.OutputFile == "out.exe", "build ohne -o -> out.exe");
    var b2 = CommandLineParser.Parse(new[] { "build", "code1", "codeN", "-o", "test.exe" });
    CliCheck(b2.OutputFile == "test.exe" && b2.Files.SequenceEqual(new[] { "code1", "codeN" }), "build ... -o test.exe");
    var b3 = CommandLineParser.Parse(new[] { "build", "-o", "x y.exe", "eins zwei.script", "\"drei vier.script\"" });
    CliCheck(b3.OutputFile == "x y.exe" && b3.Files.SequenceEqual(new[] { "eins zwei.script", "drei vier.script" }), "Dateinamen mit Leerzeichen, -o vor den Dateien, umschliessende Anfuehrungszeichen entfernt");
    CliCheck(CommandLineParser.Parse(Array.Empty<string>()).Command == CommandKind.Help && CommandLineParser.Parse(new[] { "--help" }).Command == CommandKind.Help, "ohne Argumente / --help -> Hilfe");
    CliCheck(CommandLineParser.Parse(new[] { "laufen", "a" }).Error != null, "unbekannter Befehl ist ein Fehler");
    CliCheck(CommandLineParser.Parse(new[] { "run" }).Error != null, "run ohne Datei ist ein Fehler");
    CliCheck(CommandLineParser.Parse(new[] { "run", "a", "-m" }).Error != null && CommandLineParser.Parse(new[] { "run", "a", "-m", "TURBO" }).Error != null, "-m ohne/mit falschem Modus ist ein Fehler");
    CliCheck(CommandLineParser.Parse(new[] { "run", "a", "-o", "x.exe" }).Error != null, "-o gibt es nur bei build");
    CliCheck(CommandLineParser.Parse(new[] { "run", "a", "-x" }).Error != null, "unbekannte Option ist ein Fehler");

    // End to end via the runner (without a process): build produces the file, run returns exit codes.
    var cliDir = Path.Combine(Path.GetTempPath(), "fire-cli-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(cliDir);
    try
    {
        var f1 = Path.Combine(cliDir, "eins.script");
        var f2 = Path.Combine(cliDir, "zwei mit leer.script");
        File.WriteAllText(f1, "class G { Hi() { print(\"hi\") } }\n");
        File.WriteAllText(f2, "new G().Hi()\nterminate(5)\n");
        var errW = new StringWriter();
        var prevOut = Console.Out;
        var capture = new StringWriter();
        Console.SetOut(capture);
        int code;
        try { code = CommandLineRunner.Run(new[] { "run", f1, f2 }, capture, errW); }
        finally { Console.SetOut(prevOut); }
        CliCheck(code == 5 && capture.ToString().Replace("\r", "") == "hi\n", $"run: zwei Dateien zu einem Programm, terminate(5) -> Exitcode 5 (Code {code}, Ausgabe '{capture.ToString().Trim()}')");

        var bad = Path.Combine(cliDir, "bad.script");
        File.WriteAllText(bad, "var x = \n");
        var errBad = new StringWriter();
        CliCheck(CommandLineRunner.Run(new[] { "run", bad }, new StringWriter(), errBad) == CommandLineRunner.ExitScriptError && errBad.ToString().Length > 0, "run: Kompilierfehler -> Exitcode 1 mit Meldung");
        CliCheck(CommandLineRunner.Run(new[] { "run", Path.Combine(cliDir, "nix.script") }, new StringWriter(), new StringWriter()) == CommandLineRunner.ExitUsage, "run: fehlende Datei -> Exitcode 2");

        {
            // (packed with the VM in C: its program file is behind `fire`)
            var outFile = Path.Combine(cliDir, "gebaut.exe");
            int bc = CommandLineRunner.Run(new[] { "build", f1, f2, "-o", outFile, "-m", "DEBUG" }, new StringWriter(), new StringWriter());
            CliCheck(bc == 0 && File.Exists(outFile), "build -o: erzeugt die Datei");
            var packed = File.Exists(outFile) && CVmPayload.ReadProgram(outFile) is { } packedBytes ? fire.Bytecode.ProgramFile.Read(packedBytes) : null;
            CliCheck(packed != null && packed.ExecutionMode == VmExecutionMode.Debug, "build -m DEBUG: der Modus steckt im gepackten Programm");
        }
    }
    finally { try { Directory.Delete(cliDir, true); } catch (IOException) { } }

    Console.WriteLine(cliFailures == 0 ? "Alle Befehlszeilen-Pruefungen bestanden." : $"FEHLER: {cliFailures} Befehlszeilen-Pruefung(en) fehlgeschlagen.");
}

// ---------------------------------------------------------------------------
// Images: decode PNG, BMP, GIF (test files from Pillow and own writers, see ImageFixtures)
// ---------------------------------------------------------------------------
{
    Console.WriteLine();
    Console.WriteLine("=== Bilder: PNG, BMP, GIF ===");
    int imgFailures = 0;
    void ImgCheck(bool ok, string what)
    {
        if (!ok) imgFailures++;
        Console.WriteLine(ok ? $"OK: {what}" : $"FEHLER: {what}");
    }

    const int IW = 13, IH = 7;
    // dieselben Formeln wie im Erzeugerskript
    uint Rgb(int x, int y) => (uint)((x * 19 + 3) % 256) | (uint)(((y * 35 + 5) % 256) << 8) | (uint)((((x * 7 + y * 13) * 3) % 256) << 16);
    uint Alpha(int x, int y) => (uint)((x * 37 + y * 91) % 256);
    uint PalEntry(int i) => (uint)((i * 40 + 10) % 256) | (uint)(((i * 70 + 20) % 256) << 8) | (uint)(((i * 110 + 30) % 256) << 16) | 0xFF000000u;
    int PalIndex(int x, int y, int n) => (x * 5 + y * 3) % n;
    uint Opaque(uint c) => c | 0xFF000000u;
    uint Gray(uint v) => v | (v << 8) | (v << 16);

    fire.Imaging.ImageData Load(string name) => fire.Imaging.ImageDecoder.Decode(ImageFixtures.Get(name));
    bool Truecolor(string name, Func<int, int, uint> expected, string? format = null)
    {
        try
        {
            var img = Load(name);
            if (img.IsIndexed || img.Width != IW || img.Height != IH || (format != null && img.Format != format)) return false;
            for (int y = 0; y < IH; y++) for (int x = 0; x < IW; x++)
                if (img.Pixels![y * IW + x] != expected(x, y)) return false;
            return true;
        }
        catch (Exception) { return false; }
    }
    // indexed: the COLOUR of each pixel is right (an encoder may renumber the indices), optionally the indices themselves
    bool Indexed(string name, Func<int, int, uint> expectedColor, bool exactIndices = false, int n = 0, string? format = null)
    {
        try
        {
            var img = Load(name);
            if (!img.IsIndexed || img.Width != IW || img.Height != IH || (format != null && img.Format != format)) return false;
            for (int y = 0; y < IH; y++) for (int x = 0; x < IW; x++)
            {
                int pos = y * IW + x;
                if (img.Palette![img.Indices![pos]] != expectedColor(x, y)) return false;
                if (exactIndices && img.Indices[pos] != PalIndex(x, y, n)) return false;
            }
            return true;
        }
        catch (Exception) { return false; }
    }

    // ---- PNG: own encoder (all colour types and depths, all row filters) ----
    ImgCheck(Truecolor("png_rgba8", (x, y) => Rgb(x, y) | (Alpha(x, y) << 24), "PNG"), "PNG RGBA 8 Bit (alle fuenf Zeilenfilter, IDAT in zwei Chunks)");
    ImgCheck(Truecolor("png_rgb8", (x, y) => Opaque(Rgb(x, y))), "PNG RGB 8 Bit");
    ImgCheck(Truecolor("png_rgba16", (x, y) => Rgb(x, y) | (Alpha(x, y) << 24)), "PNG RGBA 16 Bit (auf 8 Bit gekuerzt)");
    ImgCheck(Truecolor("png_rgb16", (x, y) => Opaque(Rgb(x, y))), "PNG RGB 16 Bit");
    ImgCheck(Truecolor("png_gray8", (x, y) => Opaque(Gray(Rgb(x, y) & 0xFF))), "PNG Grau 8 Bit");
    ImgCheck(Truecolor("png_gray16", (x, y) => Opaque(Gray(Rgb(x, y) & 0xFF))), "PNG Grau 16 Bit");
    ImgCheck(Truecolor("png_graya8", (x, y) => Gray(Rgb(x, y) & 0xFF) | (Alpha(x, y) << 24)), "PNG Grau+Alpha 8 Bit");
    ImgCheck(Truecolor("png_graya16", (x, y) => Gray(Rgb(x, y) & 0xFF) | (Alpha(x, y) << 24)), "PNG Grau+Alpha 16 Bit");
    foreach (var (d, mul) in new[] { (1, 255u), (2, 85u), (4, 17u) })
        ImgCheck(Truecolor($"png_gray{d}", (x, y) => Opaque(Gray((uint)((x * 3 + y) % (1 << d)) * mul))), $"PNG Grau {d} Bit");
    ImgCheck(Truecolor("png_gray4_adam7", (x, y) => Opaque(Gray((uint)((x * 3 + y) % 16) * 17u))), "PNG Grau 4 Bit, verschraenkt (Adam7)");
    ImgCheck(Truecolor("png_rgba8_adam7", (x, y) => Rgb(x, y) | (Alpha(x, y) << 24)), "PNG RGBA verschraenkt (Adam7)");
    ImgCheck(Truecolor("png_rgb16_adam7", (x, y) => Opaque(Rgb(x, y))), "PNG RGB 16 Bit verschraenkt");
    ImgCheck(Truecolor("png_gray8_key", (x, y) => (x == 0 ? 0u : 0xFF000000u) | Gray(Rgb(x, y) & 0xFF)), "PNG Grau mit tRNS-Schluessel (Pixel dieses Grauwerts durchsichtig)");
    ImgCheck(Truecolor("png_rgb8_key", (x, y) => (x == 0 && y == 0 ? 0u : 0xFF000000u) | Rgb(x, y)), "PNG RGB mit tRNS-Schluessel");
    {
        var one = Load("png_rgb8_1x1_adam7");
        ImgCheck(one.Width == 1 && one.Height == 1 && one.Pixels![0] == Opaque(Rgb(5, 5)), "PNG 1x1 verschraenkt (leere Durchgaenge)");
        var small = Load("png_rgb8_3x2_adam7");
        bool ok = small.Width == 3 && small.Height == 2;
        for (int y = 0; y < 2 && ok; y++) for (int x = 0; x < 3; x++) ok &= small.Pixels![y * 3 + x] == Opaque(Rgb(x, y));
        ImgCheck(ok, "PNG 3x2 verschraenkt (nicht alle Durchgaenge besetzt)");
    }
    foreach (var (d, n) in new[] { (1, 2), (2, 4), (4, 16), (8, 11) })
        ImgCheck(Indexed($"png_pal{d}", (x, y) => PalEntry(PalIndex(x, y, n)), true, n, "PNG") && Load($"png_pal{d}").TransparentIndex == -1, $"PNG Palette {d} Bit: indiziert, Indizes und Palette stimmen");
    ImgCheck(Indexed("png_pal2_adam7", (x, y) => PalEntry(PalIndex(x, y, 4)), true, 4), "PNG Palette 2 Bit verschraenkt");
    {
        var t = Load("png_pal8_trns");
        ImgCheck(t.IsIndexed && t.TransparentIndex == 1 && (t.Palette![1] >> 24) == 0 && (t.Palette[2] >> 24) == 128 && (t.Palette[3] >> 24) == 255, "PNG Palette mit tRNS: durchsichtiger Index und Alpha je Eintrag");
    }

    // ---- PNG: Pillow ----
    ImgCheck(Truecolor("pil_png_rgba", (x, y) => Rgb(x, y) | (Alpha(x, y) << 24)), "Pillow-PNG RGBA");
    ImgCheck(Truecolor("pil_png_rgba_optimized", (x, y) => Rgb(x, y) | (Alpha(x, y) << 24)), "Pillow-PNG RGBA (optimiert, adaptive Filter)");
    ImgCheck(Truecolor("pil_png_rgb", (x, y) => Opaque(Rgb(x, y))), "Pillow-PNG RGB");
    ImgCheck(Truecolor("pil_png_gray", (x, y) => Opaque(Gray(Rgb(x, y) & 0xFF))), "Pillow-PNG Grau");
    ImgCheck(Truecolor("pil_png_bilevel", (x, y) => Opaque(Gray((x + y) % 3 == 0 ? 255u : 0u))), "Pillow-PNG schwarzweiss (1 Bit)");
    ImgCheck(Indexed("pil_png_pal", (x, y) => PalEntry(PalIndex(x, y, 11)), true, 11), "Pillow-PNG Palette");
    ImgCheck(Indexed("pil_png_pal_bits4", (x, y) => PalEntry(PalIndex(x, y, 11)), true, 11), "Pillow-PNG Palette 4 Bit");
    {
        var t = Load("pil_png_pal_trns");
        ImgCheck(t.IsIndexed && t.TransparentIndex == 3 && (t.Palette![3] >> 24) == 0, "Pillow-PNG Palette mit Transparenz");
    }

    // ---- BMP ----
    ImgCheck(Indexed("pil_bmp_pal8", (x, y) => PalEntry(PalIndex(x, y, 11)), true, 11, "BMP"), "Pillow-BMP 8 Bit mit Palette");
    ImgCheck(Truecolor("pil_bmp_rgb24", (x, y) => Opaque(Rgb(x, y)), "BMP"), "Pillow-BMP 24 Bit");
    ImgCheck(Truecolor("pil_bmp_rgba32", (x, y) => Rgb(x, y) | (Alpha(x, y) << 24)), "Pillow-BMP 32 Bit mit Alpha");
    ImgCheck(Indexed("pil_bmp_bilevel", (x, y) => Opaque(Gray((x + y) % 3 == 0 ? 255u : 0u))), "Pillow-BMP 1 Bit schwarzweiss");
    ImgCheck(Indexed("pil_bmp_gray8", (x, y) => Opaque(Gray(Rgb(x, y) & 0xFF))), "Pillow-BMP 8 Bit Graustufen");
    ImgCheck(Indexed("bmp_pal4", (x, y) => PalEntry(PalIndex(x, y, 16)), true, 16), "BMP 4 Bit mit Palette");
    ImgCheck(Indexed("bmp_pal4_topdown", (x, y) => PalEntry(PalIndex(x, y, 16)), true, 16), "BMP 4 Bit, Zeilen von oben nach unten (negative Hoehe)");
    ImgCheck(Indexed("bmp_pal1", (x, y) => PalEntry(PalIndex(x, y, 2)), true, 2), "BMP 1 Bit mit Palette");
    {
        var part = Load("bmp_pal8_partial");
        ImgCheck(Indexed("bmp_pal8_partial", (x, y) => PalEntry(PalIndex(x, y, 5)), true, 5) && part.Palette![5] == 0xFF000000u, "BMP 8 Bit mit nur 5 Palette-Eintraegen (Rest deckendes Schwarz)");
    }
    ImgCheck(Truecolor("bmp_rgb24_topdown", (x, y) => Opaque(Rgb(x, y))), "BMP 24 Bit von oben nach unten");
    ImgCheck(Truecolor("bmp_rgb32_alpha", (x, y) => Rgb(x, y) | (Alpha(x, y) << 24)), "BMP 32 Bit (BI_RGB) mit Alpha-Byte");
    ImgCheck(Truecolor("bmp_rgb32_noalpha", (x, y) => Opaque(Rgb(x, y))), "BMP 32 Bit mit unbenutztem Alpha-Byte (0) = deckend");
    ImgCheck(Truecolor("bmp_rgb555", (x, y) => { uint c = Rgb(x, y); return Opaque((((c & 0xFF) >> 3) * 255 / 31) | (((c >> 8 & 0xFF) >> 3) * 255 / 31) << 8 | (((c >> 16 & 0xFF) >> 3) * 255 / 31) << 16); }), "BMP 16 Bit 5-5-5");
    ImgCheck(Truecolor("bmp_rgb565", (x, y) => { uint c = Rgb(x, y); return Opaque((((c & 0xFF) >> 3) * 255 / 31) | (((c >> 8 & 0xFF) >> 2) * 255 / 63) << 8 | (((c >> 16 & 0xFF) >> 3) * 255 / 31) << 16); }), "BMP 16 Bit 5-6-5 (Bitmasken)");
    ImgCheck(Truecolor("bmp_rgba32_masks", (x, y) => Rgb(x, y) | (Alpha(x, y) << 24)), "BMP 32 Bit mit Bitmasken und Alpha");
    ImgCheck(Truecolor("bmp_os2_rgb24", (x, y) => Opaque(Rgb(x, y))), "BMP OS/2-Kopfzeile 24 Bit");
    ImgCheck(Indexed("bmp_os2_pal8", (x, y) => PalEntry(PalIndex(x, y, 7)), true, 7), "BMP OS/2-Kopfzeile 8 Bit mit Palette (3-Byte-Eintraege)");
    ImgCheck(Indexed("bmp_rle8", (x, y) => PalEntry(PalIndex(x, y, 9)), true, 9), "BMP RLE8 (Wiederholungen, absolute Laeufe, Zeilenende)");
    ImgCheck(Indexed("bmp_rle4", (x, y) => PalEntry(PalIndex(x, y, 16)), true, 16), "BMP RLE4");

    // ---- GIF ----
    ImgCheck(Indexed("pil_gif_pal", (x, y) => PalEntry(PalIndex(x, y, 11)), false, 0, "GIF") && Load("pil_gif_pal").TransparentIndex == -1, "Pillow-GIF: Farben stimmen");
    ImgCheck(Indexed("pil_gif_interlaced", (x, y) => PalEntry(PalIndex(x, y, 11))), "Pillow-GIF verschraenkt");
    {
        var g = Load("pil_gif_trans");
        bool ok = g.IsIndexed && g.TransparentIndex >= 0;
        for (int y = 0; y < IH && ok; y++) for (int x = 0; x < IW; x++)
            ok &= (g.Indices![y * IW + x] == g.TransparentIndex) == (PalIndex(x, y, 11) == 2);
        ImgCheck(ok, "Pillow-GIF mit Transparenz: genau die Pixel des durchsichtigen Index");
    }
    ImgCheck(Indexed("pil_gif_anim", (x, y) => PalEntry(PalIndex(x, y, 11))), "Pillow-GIF-Animation: das erste Einzelbild");
    {
        var big = Load("pil_gif_big");
        bool ok = big.Width == 40 && big.Height == 30 && big.IsIndexed;
        for (int y = 0; y < 30 && ok; y++) for (int x = 0; x < 40; x++)
            ok &= big.Palette![big.Indices![y * 40 + x]] == PalEntry((x * 7 + y * 11 + x * y) % 64);
        ImgCheck(ok, "Pillow-GIF 40x30 mit 64 Farben, verschraenkt (LZW mit Wachstum der Codebreite)");
    }

    // ---- Errors: unknown, empty, truncated, damaged ----
    void MustFail(string what, byte[] data)
    {
        try { fire.Imaging.ImageDecoder.Decode(data); ImgCheck(false, what + ": haette scheitern muessen"); }
        catch (fire.Imaging.ImageFormatException) { ImgCheck(true, what); }
        catch (Exception ex) { ImgCheck(false, what + ": falsche Ausnahme " + ex.GetType().Name); }
    }
    MustFail("leere Daten werden abgelehnt", new byte[0]);
    MustFail("unbekanntes Format wird abgelehnt", System.Text.Encoding.ASCII.GetBytes("Das ist kein Bild, sondern Text."));
    foreach (var name in new[] { "png_rgba8", "png_pal4", "png_rgba8_adam7", "pil_png_pal", "pil_bmp_rgba32", "bmp_rle8", "bmp_pal4", "pil_gif_big" })
    {
        var full = ImageFixtures.Get(name);
        bool allFailed = true;
        for (int cut = 1; cut < full.Length; cut += Math.Max(1, full.Length / 40))
        {
            try { fire.Imaging.ImageDecoder.Decode(full[..cut]); allFailed = false; }   // a truncated image may (GIF) be missing leniently, but never throw a foreign exception
            catch (fire.Imaging.ImageFormatException) { }
            catch (Exception) { allFailed = false; ImgCheck(false, $"abgeschnittene {name} wirft eine fremde Ausnahme (bei {cut} Byte)"); }
        }
        if (name != "pil_gif_big" && name != "bmp_rle8") ImgCheck(allFailed, $"abgeschnittene {name} (mehrere Laengen) scheitert mit ImageFormatException");
        else ImgCheck(true, $"abgeschnittene {name}: nur ImageFormatException oder ein lenient dekodiertes Bild");
    }
    {
        var bad = (byte[])ImageFixtures.Get("png_rgb8").Clone();
        bad[bad.Length / 2] ^= 0xFF;
        MustFail("PNG mit beschaedigten Daten (Pruefsumme)", bad);
        var noEnd = ImageFixtures.Get("png_rgb8")[..^12];
        MustFail("PNG ohne IEND", noEnd);
        var big = (byte[])ImageFixtures.Get("png_rgb8").Clone();
        big[16] = 0x7F; // width ~2 billion: CRC error AND too large - in any case ImageFormatException
        MustFail("PNG mit absurder Breite", big);
        var zero = (byte[])ImageFixtures.Get("pil_bmp_rgb24").Clone();
        zero[18] = zero[19] = zero[20] = zero[21] = 0;
        MustFail("BMP mit Breite 0", zero);
        var hugeBmp = (byte[])ImageFixtures.Get("pil_bmp_rgb24").Clone();
        hugeBmp[21] = 0x7F; hugeBmp[19] = 0x7F;
        MustFail("BMP mit absurder Groesse", hugeBmp);
        var badBpp = (byte[])ImageFixtures.Get("pil_bmp_rgb24").Clone();
        badBpp[28] = 7;
        MustFail("BMP mit unbekannter Farbtiefe", badBpp);
        var gifNoImage = System.Text.Encoding.ASCII.GetBytes("GIF89a").Concat(new byte[] { 2, 0, 2, 0, 0, 0, 0, 0x3B }).ToArray();
        MustFail("GIF ohne Bild", gifNoImage);
    }

    // ---- Randomly damaged files: never a foreign exception, never a hang ----
    {
        var rng2 = new Random(11);
        bool clean = true; int tried = 0;
        foreach (var name in ImageFixtures.Data.Keys)
        {
            var original = ImageFixtures.Get(name);
            for (int round = 0; round < 60; round++)
            {
                var copy = (byte[])original.Clone();
                int flips = 1 + rng2.Next(3);
                for (int f = 0; f < flips; f++) copy[rng2.Next(copy.Length)] = (byte)rng2.Next(256);
                tried++;
                try { fire.Imaging.ImageDecoder.Decode(copy); }
                catch (fire.Imaging.ImageFormatException) { }
                catch (Exception ex) { clean = false; Console.WriteLine($"  {name}: {ex.GetType().Name}: {ex.Message}"); }
            }
        }
        ImgCheck(clean, $"{tried} zufaellig beschaedigte Dateien: nur Erfolg oder ImageFormatException");
    }

    Console.WriteLine(imgFailures == 0 ? "Alle Bild-Pruefungen bestanden." : $"FEHLER: {imgFailures} Bild-Pruefung(en) fehlgeschlagen.");
}

// the drawing of the UI library without events: run by the VM (fake renderer) below, natively (fire's own display without a screen) in the native checks
uiTests:
uiDrawScript = """
    var fb = new Framebuffer(640, 300)
    var win = new Window(fb, "Test")
    var ui = new UI.Root(fb, win)
    var panel = new UI.Panel(8, 8, 300, 150)
    ui.Add(panel)
    panel.Add(new UI.Label("Hello UI", 6, 6))
    panel.Add(new UI.Button("OK", 6, 26, 80, 26))
    panel.Add(new UI.CheckBox("check me", 100, 30, true))
    var box = new UI.TextBox("text", 6, 64, 160, 24)
    panel.Add(box)
    var stack = new UI.Stack(180, 60, 100, 80)
    stack.Add(new UI.Button("one", 0, 0, 80, 20))
    stack.Add(new UI.Button("two", 0, 0, 80, 20))
    panel.Add(stack)
    // the layout panels: a grid with a fixed, a star and an auto column, a border with padding, a wrap panel that spans all columns
    var lay = new UI.Grid(8, 170, 300, 120)
    lay.SetColumns("60, *, auto")
    lay.SetRows("24, *")
    lay.AddAt(new UI.Button("grid", 0, 0, -1, -1), 0, 0)
    var bd = new UI.Border()
    bd.Padding = new UI.Thickness(2)
    bd.SetChild(new UI.Label("border"))
    lay.AddAt(bd, 0, 1)
    lay.AddAt(new UI.Label("auto"), 0, 2)
    var wr = new UI.WrapPanel()
    for (var i = 0; i < 6; i++) { var wb = new UI.Button("w" + i, 0, 0, 44, 18); wb.Margin = new UI.Thickness(2); wr.Add(wb) }
    lay.AddAt(wr, 1, 0, 1, 3)
    ui.Add(lay)
    // styles and templates: an implicit style for labels, a button whose look is a template with a part bound to its text and a hover trigger
    var sty = new UI.Style("Label")
    sty.Set("Color", new SolidBrush(UI.Color.Rgb(0, 0, 200)))
    ui.resources.AddStyle(sty)
    var tpl = new UI.ControlTemplate(func (owner) => {
        var tb = new UI.Border()
        tb.Name = "tb"
        tb.Background = new SolidBrush(UI.Color.Rgb(40, 160, 80))
        tb.Padding = new UI.Thickness(2)
        var tl = new UI.Label("")
        tl.Name = "tl"
        tb.SetChild(tl)
        return tb
    })
    tpl.Bind("tl", "Text", "Text")
    var tt = new UI.Trigger("Hover", true)
    tt.Set("Background", new SolidBrush(UI.Color.Rgb(200, 60, 60)), "tb")
    tpl.AddTrigger(tt)
    var tbtn = new UI.Button("tpl", 200, 100, -1, -1)
    tbtn.Template = tpl
    panel.Add(tbtn)
    // scrolling with the clip rectangle, lists, a tree, radio buttons, shapes, a drawing canvas and a menu with its popup
    var sv = new UI.ScrollViewer(330, 8, 120, 90)
    var col = new UI.StackPanel()
    for (var i = 0; i < 9; i++) { col.Add(new UI.Label("row " + i)) }
    sv.SetContent(col)
    ui.Add(sv)
    var lbx = new UI.ListBox(460, 8, 100, 90)
    for (var i = 0; i < 8; i++) { lbx.Add("entry " + i) }
    lbx.Select(2)
    ui.Add(lbx)
    var tv = new UI.TreeView(330, 108, 120, 90)
    var top = tv.AddNode(new UI.TreeNode("root"))
    top.Add(new UI.TreeNode("child a"))
    var cb = top.Add(new UI.TreeNode("child b"))
    cb.Add(new UI.TreeNode("leaf"))
    top.Expanded = true
    ui.Add(tv)
    var rbs = new UI.RadioButtons(460, 108)
    rbs.Add("one")
    rbs.Add("two")
    rbs.Select(1)
    ui.Add(rbs)
    var shp = new UI.Path(undefined, 570, 8)
    shp.SetData("M 0 0 L 50 0 C 60 20 60 40 25 50 Z")
    shp.Fill = new SolidBrush(UI.Color.Rgb(200, 120, 40))
    shp.Stroke = new Pen(UI.Color.Rgb(0, 0, 0))
    ui.Add(shp)
    var dcv = new UI.DrawingCanvas(570, 70, 60, 40)
    dcv.OnPaint = func (c) => {
        c.renderer.FillRect(0, 0, 60, 40, new SolidBrush(UI.Color.Rgb(30, 30, 90)))
        c.renderer.DrawLine(0, 0, 59, 39, new Pen(UI.Color.Rgb(255, 255, 255)))
    }
    ui.Add(dcv)
    var mbar = new UI.MenuBar(330, 210, 300, -1)
    var mfile = new UI.MenuItem("File")
    mfile.Add(new UI.MenuItem("Open"))
    mfile.Add(UI.MenuItem.Separator())
    mfile.Add(new UI.MenuItem("Quit"))
    mbar.Add(mfile)
    ui.Add(mbar)
    // fonts: a TrueType font found on the system (the folder FIRE_FONT_DIRS), a built-in bitmap font, the font and size passed on to the elements inside, a text field that scrolls
    var fnp = new UI.Panel(330, 236, 300, 62)
    fnp.Font = "firesans"
    fnp.FontSize = 16
    fnp.Add(new UI.Label("AVAVHO iAi.", 4, 4))
    var fl2 = new UI.Label("Small", 130, 4)
    fl2.Font = "8x8"
    fnp.Add(fl2)
    var fbt = new UI.Button("HOAV", 4, 28, -1, -1)
    fbt.FontSize = 12
    fnp.Add(fbt)
    fnp.Add(new UI.TextBox("AVA HO iii AVAV", 80, 30, 90, 24))
    ui.Add(fnp)
    print(ui.Tick())
    mbar.Open(ui, 0)
    ui.Tick()
    print("layout " + bd.rx + " " + bd.actualWidth + " " + wr.actualWidth + " " + wr.actualHeight + " " + wr.children[5].rx + "," + wr.children[5].ry)
    var bytes = fb.ReadBytes()
    var h = 17
    for (var i = 0; i < bytes.length; i++) { h = (h * 31 + bytes[i]) % 1000000007 }
    print("hash " + h)
    print(ui.Tick())
    // a watched text field set again and again after the layout: the probes compare the old value with the new one (natively it once was freed before that)
    var probed = new UI.Label("first", 300, 200)
    ui.Add(probed)
    ui.Draw()
    for (var i = 0; i < 20; i = i + 1) {
        probed.Text = "text " + i
        ui.Draw()
    }
    print(probed.Text)
    """;

// the drawing of the UI library in the VM in C: the reference of "UI nativ == VM"
{
    try { uiDrawExpected = CVmTest.Run(new[] { "#import \"ui\"\n" + uiDrawScript }, VmExecutionMode.Release, display: true).Printed.ToArray(); }
    catch (Exception ex) { Console.WriteLine("FEHLER: die UI-Zeichnung in der C-VM: " + CompileErrors.Describe(ex)); }
}
if (Environment.GetEnvironmentVariable("FIRE_TESTS_ONLY") == "ui") return;

// ---------------------------------------------------------------------------
// Geraete: geteilter DeviceManager, Standardgeraet, EnsureConnected, IsShared, Paketverfolgung, Paketprotokoll
// ---------------------------------------------------------------------------
{
    Console.WriteLine();
    Console.WriteLine("=== Geraete ===");
    int devFailures = 0;

    fire.Device.Manager.DeviceManager.DeviceManager NewLoopbackManager(bool shared)
    {
        var manager = new fire.Device.Manager.DeviceManager.DeviceManager { IsShared = shared };
        manager.RegisterDriver(new fire.Device.Manager.Drivers.Loopback.LoopbackDriver());
        manager.RefreshDevices(true);
        return manager;
    }

    List<string> RunDev(string script, fire.Device.Manager.DeviceManager.DeviceManager manager, VmExecutionMode mode)
    {
        // in the VM in C (M1): `fire` takes the devices of the manager through FIRE_DEVICE_HOST, as a program of spark takes spark's (DeviceHostServer)
        var lines = new List<string>();
        using var host = new fire.Device.Manager.DeviceManager.DeviceHostServer(manager);
        var run = CVmTest.Run(new[] { script }, mode, environment: new Dictionary<string, string> { [fire.Device.Manager.DeviceManager.DeviceHostServer.EnvironmentVariable] = host.EnvironmentValue });
        lines.AddRange(run.Printed);
        if (run.ExitCode != 0 && run.UnhandledMessage is { } unhandled) lines.Add("UNBEHANDELT: " + unhandled);
        return lines;
    }

    void CheckDev(string title, string script, string[] expected, bool shared = true, string? defaultId = null,
        Action<fire.Device.Manager.DeviceManager.DeviceManager>? after = null)
    {
        if (after == null && !title.StartsWith("IsShared") && !script.Contains("#import \"graphics\"")) devNativeCases.Add((title, script, expected, defaultId));
        foreach (var mode in new[] { VmExecutionMode.Debug, VmExecutionMode.Release, VmExecutionMode.Performance })
        {
            string[] actual;
            var manager = NewLoopbackManager(shared);
            manager.DefaultIdentifier = defaultId;
            try
            {
                var task = Task.Run(() => RunDev(script, manager, mode).ToArray());
                actual = task.Wait(TimeSpan.FromSeconds(30)) ? task.Result : new[] { "ZEITUEBERSCHREITUNG (haengt)" };
            }
            catch (Exception ex) { actual = new[] { "AUSNAHME: " + (ex.InnerException != null ? CompileErrors.Describe(ex.InnerException) : ex.Message) }; }
            bool ok = actual.SequenceEqual(expected);
            if (ok && after != null)
            {
                try { after(manager); }
                catch (Exception ex) { ok = false; actual = new[] { "NACHPRUEFUNG: " + ex.Message }; }
            }
            if (!ok) devFailures++;
            Console.WriteLine(ok ? $"OK: {title} [{mode}]" : $"FEHLER: {title} [{mode}]\n  erwartet: {string.Join(" | ", expected)}\n  erhalten: {string.Join(" | ", actual)}");
            manager.Shutdown();
        }
    }

    CheckDev("Ohne Standardgeraet: HasDefault ist false, Device.Default wirft DeviceNotFoundException", """
        #import "devices"
        print(Device.HasDefault)
        try {
            var d = Device.Default
            print("kein Fehler")
        } catch (DeviceNotFoundException e) {
            print("keins: " + e.message)
        }
        """, new[] { "False", "keins: No default device selected" });

    CheckDev("Standardgeraet: Device.Default, IsConnected (Property und Methode), EnsureConnected, Senden/Empfangen", """
        #import "devices"
        #import "time"
        var d = Device.Default
        print(d.Identifier())
        print(d.IsConnected)
        print(d.IsConnected())
        d.EnsureConnected().EnsureConnected()
        print(d.IsConnected)
        print(d.DoCommand("hallo"))
        var tries = 0
        while (!d.HasData() && tries < 200) { Sleep(TimeSpan.FromMilliseconds(10)); tries = tries + 1 }
        print(d.ReadString())
        d.Disconnect()
        print(d.IsConnected)
        print(d.DoCommand("weg"))
        """, new[] { "loopback:echo", "False", "False", "True", "True", "hallo\n", "False", "False" }, defaultId: "loopback:echo");

    CheckDev("IsShared: Geraet und Manager eines geteilten Managers melden es, ein eigener Manager nicht", """
        #import "devices"
        print(DeviceManager.IsShared())
        print(DeviceManager.Get("loopback:echo").IsShared)
        """, new[] { "True", "True" }, shared: true);
    CheckDev("IsShared: ein nicht geteilter Manager meldet false", """
        #import "devices"
        print(DeviceManager.IsShared())
        print(DeviceManager.Get("loopback:echo").IsShared)
        """, new[] { "False", "False" }, shared: false);

    CheckDev("Geteilter Manager ueberlebt den Lauf: verbundenes Geraet bleibt verbunden, Manager ist nicht abgebaut", """
        #import "devices"
        Device.Default.EnsureConnected()
        print("verbunden")
        """, new[] { "verbunden" }, shared: true, defaultId: "loopback:echo", after: m =>
        {
            if (m.DeviceCount != 1) throw new Exception("Geraet verschwunden");
            if (!m.GetDeviceByHandle(m.GetHandleByIdentifier("loopback:echo")!.Value)!.IsConnected) throw new Exception("Verbindung wurde getrennt");
            m.Dispose(); // no effect with a shared manager
            if (m.DeviceCount != 1) throw new Exception("Dispose hat den geteilten Manager abgebaut");
        });

    CheckDev("Eigener Manager wird nach dem Lauf freigegeben (Geraete getrennt)", """
        #import "devices"
        var d = DeviceManager.Get("loopback:echo")
        d.EnsureConnected()
        print(d.IsConnected)
        """, new[] { "True" }, shared: false, after: m =>
        {
            // (the C# VM disposed a manager of its own after the run; a program in `fire` takes the host's manager over FIRE_DEVICE_HOST and never disposes it - M1)
        });

    CheckDev("Paketverfolgung: gesendete und empfangene Pakete werden mitgeschnitten", """
        #import "devices"
        var d = Device.Default.EnsureConnected()
        d.DoCommand("ab")
        while (!d.HasData()) { }
        print(d.ReadString())
        """, new[] { "ab\n" }, defaultId: "loopback:echo");

    // ---- Befehlsebene: Write/Read, WaitFor, Command ----
    CheckDev("Write/Read: rohe Bytes hinaus, als Puffer zurueck; WriteString ohne Zeilenende, ReadString als Text", """
        #import "devices"
        var d = Device.Default.EnsureConnected()
        var data = new byte[4]
        data[0] = 1
        data[1] = 200
        data[2] = 0
        data[3] = 255
        print(d.Write(data))
        while (!d.HasData()) { }
        var back = d.Read()
        print(back.length + " " + back[0] + " " + back[1] + " " + back[2] + " " + back[3])
        print(d.Read().length)
        print(d.WriteString("abc"))
        while (!d.HasData()) { }
        print("[" + d.ReadString() + "]")
        print("[" + d.ReadString() + "]")
        d.Disconnect()
        print(d.Write(data))
        print(d.WriteString("x"))
        """, new[] { "True", "4 1 200 0 255", "0", "True", "[abc]", "[]", "False", "False" }, defaultId: "loopback:echo");

    CheckDev("WaitForString: ueber Paketgrenzen, schneidet den Puffer hinter dem Treffer ab, dasselbe Muster zweimal hintereinander", """
        #import "devices"
        var d = Device.Default.EnsureConnected()
        d.WriteString("ok")
        d.WriteString("ay-ab-ab-ende")
        print(d.WaitForString("kay"))
        print(d.WaitForString("ab"))
        print(d.WaitForString("ab"))
        print(d.WaitForString("ab", 150))
        print(d.WaitForString("ende"))
        print("[" + d.ReadString() + "]")
        d.WriteString("xyz")
        print(d.WaitForString(""))
        print(d.WaitForString("z"))
        print("[" + d.ReadString() + "]")
        """, new[] { "True", "True", "True", "False", "True", "[]", "True", "True", "[]" }, defaultId: "loopback:echo");

    CheckDev("WaitForString: was vor dem Treffer lag, ist verbraucht; was danach kam, bleibt lesbar", """
        #import "devices"
        #import "time"
        var d = Device.Default.EnsureConnected()
        d.WriteString("vorspann|nutzlast")
        print(d.WaitForString("|"))
        print("[" + d.ReadString() + "]")
        d.WriteString("eins")
        d.WriteString("zwei")
        print(d.WaitForString("ei"))
        print("[" + d.ReadString() + "]")
        var tries = 0
        while (!d.HasData() && tries < 200) { Sleep(TimeSpan.FromMilliseconds(10)); tries = tries + 1 }   // the echo of "zwei" arrives a moment later
        print("[" + d.ReadString() + "]")
        """, new[] { "True", "[nutzlast]", "True", "[ns]", "[zwei]" }, defaultId: "loopback:echo");

    CheckDev("WaitFor mit Bytes: Muster mit Nullbytes, ueber Pakete", """
        #import "devices"
        var d = Device.Default.EnsureConnected()
        var a = new byte[3]
        a[0] = 9
        a[1] = 0
        a[2] = 7
        var b = new byte[3]
        b[0] = 0
        b[1] = 7
        b[2] = 5
        d.Write(a)
        d.Write(b)
        var pattern = new byte[3]
        pattern[0] = 0
        pattern[1] = 7
        pattern[2] = 0
        print(d.WaitFor(pattern))
        var rest = d.Read()
        print(rest.length + " " + rest[0] + " " + rest[1])
        """, new[] { "True", "2 7 5" }, defaultId: "loopback:echo");

    CheckDev("WaitForString: Wartezeit als Zeitwert, Millisekunden und TimeSpan; Ablauf liefert false; ungueltige Wartezeit ist eine DeviceArgumentException", """
        #import "devices"
        #import "time"
        var d = Device.Default.EnsureConnected()
        var t0 = DateTime.Now
        print(d.WaitForString("nie", 120ms))
        print(d.WaitForString("nie", 120))
        print(d.WaitForString("nie", TimeSpan.FromMilliseconds(120)))
        var ms = (DateTime.Now - t0).TotalMilliseconds
        print((ms >= 330) + " " + (ms < 5000))
        try { d.WaitForString("a", "x") } catch (e) { print((e is of DeviceArgumentException) + " " + e.message) }
        """, new[] { "False", "False", "False", "True True", "True Invalid wait time (expected: TimeSpan, a time value like 5s, or milliseconds)" }, defaultId: "loopback:echo");

    CheckDev("#timeout: die Standard-Wartezeit ohne eigene Zeitangabe (sonst 30 Sekunden)", """
        #import "devices"
        #import "time"
        #timeout 200ms
        var d = Device.Default.EnsureConnected()
        var t0 = DateTime.Now
        print(d.WaitForString("nie"))
        var ms = (DateTime.Now - t0).TotalMilliseconds
        print((ms >= 190) + " " + (ms < 5000))
        """, new[] { "False", "True True" }, defaultId: "loopback:echo");

    CheckDev("WaitFor bei getrenntem Geraet endet sofort mit false", """
        #import "devices"
        #import "time"
        var d = Device.Default.EnsureConnected()
        d.Disconnect()
        var t0 = DateTime.Now
        print(d.WaitForString("x", 20s))
        print((DateTime.Now - t0).TotalMilliseconds < 3000)
        """, new[] { "False", "True" }, defaultId: "loopback:echo");

    CheckDev("DoCommand: Text als Zeile, Command<IDevice> mit dem Geraet als Kontext, abgeleitete Befehle, DoCommands", """
        #import "devices"
        var d = Device.Default.EnsureConnected()
        print(d.DoCommand("M105"))
        print(d.WaitForString("M105\n"))

        var hello = new Command<IDevice>()
        hello.Command = dev => { return dev.WriteString("hallo;") }
        print(d.DoCommand(hello))
        print(d.WaitForString("hallo;"))

        class Ping : Command<IDevice> {
            override Execute(IDevice context) { return context.WriteString("ping;") }
        }
        print(d.DoCommand(new Ping()))
        print(d.WaitForString("ping;"))

        var list = new List()
        list.Add(new Ping())
        list.Add(hello)
        print(d.DoCommands(list))
        print(d.WaitForString("ping;hallo;"))
        print(d.DoCommands([new Ping(), new Ping()]))
        print(d.WaitForString("ping;ping;"))

        var failing = new Command<IDevice>(dev => false)
        var counted = new Command<IDevice>(dev => { dev.WriteString("nie;") })
        print(d.DoCommands([failing, counted]))
        print(d.WaitForString("nie;", 100))
        print(d.DoCommand(new Command<IDevice>()))
        d.Disconnect()
        print(d.DoCommand(hello))
        print(d.DoCommand("weg"))
        """, new[] { "True", "True", "True", "True", "True", "True", "True", "True", "True", "True", "False", "False", "True", "False", "False" }, defaultId: "loopback:echo");

    // Packet tracking directly on the manager (without a script)
    {
        var manager = NewLoopbackManager(true);
        var captured = new List<fire.Device.Manager.DeviceManager.PacketRecord>();
        manager.PacketCaptured += p => { lock (captured) captured.Add(p); };
        var device = manager.GetDeviceByHandle(manager.GetHandleByIdentifier("loopback:echo")!.Value)!;
        device.Connect();
        device.SendCommand("ping");
        for (int i = 0; i < 200 && captured.Count < 2; i++) Thread.Sleep(10);
        bool ok;
        lock (captured)
            ok = captured.Count == 2
                && captured[0].Direction == fire.Device.Manager.DeviceManager.PacketDirection.HostToDevice
                && captured[1].Direction == fire.Device.Manager.DeviceManager.PacketDirection.DeviceToHost
                && captured.All(c => c.DeviceIdentifier == "loopback:echo" && System.Text.Encoding.UTF8.GetString(c.Data) == "ping\n");
        if (!ok) devFailures++;
        Console.WriteLine(ok ? "OK: Paketverfolgung am Manager: Senden und Empfangen werden in Reihenfolge mitgeschnitten" : "FEHLER: Paketverfolgung am Manager");

        // Zustandsaenderungen
        var states = new List<bool>();
        manager.DeviceStateChanged += slot => { lock (states) states.Add(slot.Device.IsConnected); };
        device.Disconnect();
        device.Connect();
        ok = states.SequenceEqual(new[] { false, true });
        if (!ok) devFailures++;
        Console.WriteLine(ok ? "OK: DeviceStateChanged meldet Trennen und Verbinden" : "FEHLER: DeviceStateChanged: " + string.Join(",", states));

        // Standardgeraet
        int changes = 0;
        manager.DefaultChanged += () => changes++;
        manager.DefaultIdentifier = "loopback:echo";
        manager.DefaultIdentifier = "loopback:echo";
        ok = changes == 1 && manager.DefaultHandle == manager.GetHandleByIdentifier("loopback:echo");
        if (!ok) devFailures++;
        Console.WriteLine(ok ? "OK: Standardgeraet: DefaultChanged feuert nur bei einer Aenderung, DefaultHandle passt" : "FEHLER: Standardgeraet");

        // Treiber entfernen
        ok = manager.RemoveDriver("loopback") && manager.DeviceCount == 0 && manager.DefaultHandle == null;
        if (!ok) devFailures++;
        Console.WriteLine(ok ? "OK: RemoveDriver entfernt Treiber samt Geraeten" : "FEHLER: RemoveDriver");
        manager.Shutdown();
    }

    // All extensions together: native functions are jumped to via their index, the order of registration
    // when translating and when executing must match (formerly: graphics + time -> wrong function)
    CheckDev("Alle Erweiterungen in einem Programm (graphics, time, reflection, linq, devices, io)", """
        #import "graphics"
        #import "time"
        #import "reflection"
        #import "linq"
        #import "devices"
        #import "io"
        #import "filesystem"
        var fb = new Framebuffer(8, 4)
        print(fb.Width() + "x" + fb.Height())
        print(DateTime.Now.Year > 2000)
        print(Type.Of(fb).Name)
        print(DeviceManager.Devices().Count)
        print(IO.File.Exists("/gibt/es/nicht"))
        var con = new Renderer(fb)
        con.AlphaBlending = false      // without blending the value including alpha 0 is simply copied
        con.FillRect(0, 0, 2, 2, new SolidBrush(256))
        print(con.GetPixel(1, 1))
        """, new[] { "8x4", "True", "Framebuffer", "1", "False", "256" });

    // Packet log: saving and loading lossless
    {
        var t0 = new DateTime(2026, 10, 3, 12, 0, 0, 123, DateTimeKind.Utc).AddTicks(4567);
        var packets = new List<fire.Device.Manager.DeviceManager.PacketRecord>
        {
            new(t0, "serial:COM3", fire.Device.Manager.DeviceManager.PacketDirection.HostToDevice, System.Text.Encoding.UTF8.GetBytes("M105\n")),
            new(t0.AddMilliseconds(30), "serial:COM3", fire.Device.Manager.DeviceManager.PacketDirection.DeviceToHost, new byte[] { 0x00, 0xFF, 0x7F, 0xC3, 0xA4 }),
            new(t0.AddMilliseconds(40), "serial:COM3", fire.Device.Manager.DeviceManager.PacketDirection.DeviceToHost, Array.Empty<byte>()),
        };
        var text = fire.Device.Manager.DeviceManager.PacketLog.Serialize(packets);
        var back = fire.Device.Manager.DeviceManager.PacketLog.Parse(text);
        bool ok = back.Count == 3 && Enumerable.Range(0, 3).All(i =>
            back[i].Time == packets[i].Time && back[i].Direction == packets[i].Direction && back[i].DeviceIdentifier == packets[i].DeviceIdentifier && back[i].Data.SequenceEqual(packets[i].Data));
        if (!ok) devFailures++;
        Console.WriteLine(ok ? "OK: Paketprotokoll: Speichern und Laden ist verlustfrei (Zeit, Richtung, Geraet, Binaerdaten, leeres Paket)" : "FEHLER: Paketprotokoll Roundtrip\n" + text);

        string[] bad = { "x\tH2D\ta\t00", "2026-10-03T12:00:00Z\tUP\ta\t00", "2026-10-03T12:00:00Z\tH2D\ta\tZZ", "nur eine Spalte" };
        ok = bad.All(b =>
        {
            try { fire.Device.Manager.DeviceManager.PacketLog.Parse(b); return false; }
            catch (FormatException) { return true; }
        });
        if (!ok) devFailures++;
        Console.WriteLine(ok ? "OK: Paketprotokoll: fehlerhafte Zeilen werden mit FormatException abgelehnt" : "FEHLER: Paketprotokoll nimmt fehlerhafte Zeilen an");
    }

    // Terminal: Hexdump (16 Bytes je Zeile + Zeichen), ASCII-Darstellung, Hex-Eingabe, Text mit Escapes, Bloecke je Richtung
    {
        var dump = fire.Device.Manager.DeviceManager.TerminalLog.HexDump(System.Text.Encoding.ASCII.GetBytes("Hello device!\r\n\0\u0001ABCDEFGHIJ"));
        var dumpLines = dump.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        bool ok = dumpLines.Length == 2
            && dumpLines[0] == "0000  48 65 6C 6C 6F 20 64 65  76 69 63 65 21 0D 0A 00  Hello device!..."
            && dumpLines[1].StartsWith("0010  01 41 42 43 44 45 46 47  48 49 4A ") && dumpLines[1].EndsWith(" .ABCDEFGHIJ")
            && dumpLines[1].Length == dumpLines[0].Length - 5;
        if (!ok) devFailures++;
        Console.WriteLine(ok ? "OK: Terminal: Hexdump mit 16 Bytes je Zeile, Luecke nach 8, dann die Zeichen (Steuerzeichen als '.')" : "FEHLER: Terminal-Hexdump\n" + dump);

        ok = fire.Device.Manager.DeviceManager.TerminalLog.Ascii(new byte[] { 72, 105, 13, 10, 1, 9, 0xE4, 13 }) == "Hi\n<01>\t\u00e4<0D>"
            && fire.Device.Manager.DeviceManager.TerminalLog.TryParseHex("48 65,0x6c-6C 6f0a", out var hexBytes, out _) && hexBytes.SequenceEqual(new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F, 0x0A })
            && !fire.Device.Manager.DeviceManager.TerminalLog.TryParseHex("4G", out _, out var hexError) && hexError.Length > 0
            && fire.Device.Manager.DeviceManager.TerminalLog.TextToBytes("a\\tb\\x41", "\r\n").SequenceEqual(new byte[] { 97, 9, 98, 0x41, 13, 10 });
        if (!ok) devFailures++;
        Console.WriteLine(ok ? "OK: Terminal: ASCII-Anzeige (CR vor LF faellt weg, Steuerzeichen als <NN>), Hex-Eingabe in mehreren Schreibweisen, Text mit \\t \\xNN und Zeilenende" : "FEHLER: Terminal-Umwandlungen");

        var tlog = new fire.Device.Manager.DeviceManager.TerminalLog { Limit = 10 };
        tlog.Add(fire.Device.Manager.DeviceManager.PacketDirection.HostToDevice, new byte[] { 1, 2 });
        tlog.Add(fire.Device.Manager.DeviceManager.PacketDirection.HostToDevice, new byte[] { 3 });
        tlog.Add(fire.Device.Manager.DeviceManager.PacketDirection.DeviceToHost, System.Text.Encoding.ASCII.GetBytes("ok\n"));
        bool merged = tlog.Blocks.Count == 2 && tlog.Blocks[0].Data.Count == 3;
        string text = tlog.Render(false, showSent: false);
        tlog.Add(fire.Device.Manager.DeviceManager.PacketDirection.HostToDevice, new byte[8]);
        ok = merged && text == "<< ok\n" && tlog.Blocks.Count == 1 && tlog.Count == 8 && tlog.Blocks[0].Direction == fire.Device.Manager.DeviceManager.PacketDirection.HostToDevice;
        if (!ok) devFailures++;
        Console.WriteLine(ok ? "OK: Terminal: Bytes einer Richtung bilden einen Block, 'Show sent' blendet Gesendetes aus, das Limit wirft die aeltesten Bloecke weg" : "FEHLER: Terminal-Bloecke: " + text);
    }

    // Treiber aus Paketen: Manifest-Feld drivers (Projekt -> Paket), der Treiber-Agent bringt usb:sim in einen DeviceManager, Senden und Echo kommen an
    // (als Plugins und mit #import; DriverAgentTests.cs, allein: FIRE_TESTS_ONLY=drivers)
    devFailures += DriverAgentTests.Run();

    Console.WriteLine(devFailures == 0 ? "Alle Geraete-Pruefungen bestanden." : $"FEHLER: {devFailures} Geraete-Pruefung(en) fehlgeschlagen.");
}

// ---------------------------------------------------------------------------
// Debugger run of the editor: VM.RunUntilBreakpoint/RunUntilEnd (F5), line table (binary search), native forwarding
// ---------------------------------------------------------------------------
TestResume.Group("DebuggerTests.Run", () => { DebuggerTests.Run(); });

// ---------------------------------------------------------------------------------------------------------------------------
// Editor help: anchors for headings (links like "File.md#section") and the bundled "First Steps.md"
// ---------------------------------------------------------------------------------------------------------------------------
{
    Console.WriteLine("=== Markdown-Anker und First Steps ===");
    int mdFailures = 0;
    void CheckMd(string title, string actual, string expected)
    {
        bool ok = actual == expected;
        if (!ok) mdFailures++;
        Console.WriteLine(ok ? $"OK: {title}" : $"FEHLER: {title}\n  erwartet: {expected}\n  erhalten: {actual}");
    }

    CheckMd("Anker: Kleinbuchstaben, Leerzeichen zu Bindestrichen, Satzzeichen weg", fire.Editor.MdAnchors.Slug("First Steps!"), "first-steps");
    CheckMd("Anker: Kommas und Zahlen", fire.Editor.MdAnchors.Slug("Variables, types and 2 units"), "variables-types-and-2-units");
    var usedAnchors = new HashSet<string>();
    CheckMd("Anker: erste Ueberschrift", fire.Editor.MdAnchors.Unique("Setup", usedAnchors), "setup");
    CheckMd("Anker: zweite gleichnamige Ueberschrift bekommt -1", fire.Editor.MdAnchors.Unique("Setup", usedAnchors), "setup-1");
    CheckMd("Anker: dritte gleichnamige Ueberschrift bekommt -2", fire.Editor.MdAnchors.Unique("Setup", usedAnchors), "setup-2");

    // Every link "(#anchor)" of the bundled help pages must point to a heading, every link to another page to an existing file.
    string helpDir = Path.Combine(Path.GetDirectoryName(GetTestDataDir())!, "..", "fire.Editor", "Help");
    foreach (var helpFile in new[] { "First Steps.md" })
    {
        string helpPath = Path.Combine(helpDir, helpFile);
        if (File.Exists(helpPath))
        {
            string helpText = File.ReadAllText(helpPath);
            var anchors = new HashSet<string>();
            foreach (var block in fire.Editor.MarkdownParser.Parse(helpText))
                if (block is fire.Editor.MdHeading h)
                    fire.Editor.MdAnchors.Unique(fire.Editor.MarkdownParser.PlainText(h.Content), anchors);
            var missing = System.Text.RegularExpressions.Regex.Matches(helpText, @"\]\(#([^)]+)\)")
                .Select(m => m.Groups[1].Value).Where(a => !anchors.Contains(a)).ToList();
            CheckMd($"{helpFile}: alle Inhaltsverzeichnis-Links zeigen auf Ueberschriften", string.Join(",", missing), "");
            var brokenFiles = System.Text.RegularExpressions.Regex.Matches(helpText, @"\]\(([^)#:]+\.md)(?:#[^)]*)?\)")
                .Select(m => Uri.UnescapeDataString(m.Groups[1].Value)).Where(f => !File.Exists(Path.Combine(helpDir, f))).ToList();
            CheckMd($"{helpFile}: Links auf andere Hilfeseiten zeigen auf vorhandene Dateien", string.Join(",", brokenFiles), "");
        }
        else
        {
            mdFailures++;
            Console.WriteLine($"FEHLER: {helpFile} nicht gefunden: {helpPath}");
        }
    }

    // The language reference (docs/language, copied to Help\Language): every link stays in the folder and points to an existing page and heading;
    // its contents page gives the entries of Help > The fire Language.
    string languageDir = Path.Combine(Path.GetDirectoryName(GetTestDataDir())!, "..", "..", "docs", "language");
    if (Directory.Exists(languageDir))
    {
        HashSet<string> AnchorsOf(string file)
        {
            var set = new HashSet<string>();
            foreach (var block in fire.Editor.MarkdownParser.Parse(File.ReadAllText(file)))
                if (block is fire.Editor.MdHeading h) fire.Editor.MdAnchors.Unique(fire.Editor.MarkdownParser.PlainText(h.Content), set);
            return set;
        }
        var broken = new List<string>();
        foreach (var page in Directory.GetFiles(languageDir, "*.md").OrderBy(f => f))
        {
            string text = File.ReadAllText(page);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"\]\(([^)\s]*)\)"))
            {
                string target = m.Groups[1].Value;
                if (target.Contains("://")) continue;
                int hash = target.IndexOf('#');
                string file = hash < 0 ? target : target.Substring(0, hash), anchorName = hash < 0 ? "" : target.Substring(hash + 1);
                string targetPath = file.Length == 0 ? page : Path.Combine(languageDir, file);
                if (file.Contains('/') || file.Contains('\\') || !File.Exists(targetPath)) { broken.Add($"{Path.GetFileName(page)} -> {target}"); continue; }
                if (anchorName.Length > 0 && !AnchorsOf(targetPath).Contains(anchorName)) broken.Add($"{Path.GetFileName(page)} -> {target}");
            }
        }
        CheckMd("Sprachdoku: alle Links zeigen auf vorhandene Seiten und Ueberschriften im selben Ordner", string.Join(", ", broken), "");
        var (topics, more) = fire.Editor.LanguageHelp.Contents(File.ReadAllText(Path.Combine(languageDir, "README.md")));
        CheckMd("Sprachdoku: das Inhaltsverzeichnis liefert die Themenseiten fuers Hilfemenue",
            $"{topics.Count} {topics[0].Title}={topics[0].File} {topics[^1].File}", "20 1. Basics=01-basics.md 20-time.md");
        CheckMd("Sprachdoku: weitere Seiten (Roadmap) nach den Themen", string.Join(",", more.Select(p => p.Title + "=" + p.File)), "Roadmap=roadmap.md");
        var unlisted = Directory.GetFiles(languageDir, "*.md").Select(Path.GetFileName)
            .Where(f => f != "README.md" && !topics.Concat(more).Any(t => t.File == f)).ToList();
        CheckMd("Sprachdoku: jede Seite steht im Inhaltsverzeichnis", string.Join(",", unlisted), "");
    }
    else
    {
        mdFailures++;
        Console.WriteLine($"FEHLER: docs/language nicht gefunden: {languageDir}");
    }

    Console.WriteLine(mdFailures == 0 ? "Alle Markdown-Pruefungen bestanden." : $"FEHLER: {mdFailures} Markdown-Pruefung(en) fehlgeschlagen.");
}

// ---------------------------------------------------------------------------------------------------------------------------
// Editor: documentation comments (///) for classes, fields, properties and methods
// ---------------------------------------------------------------------------------------------------------------------------
{
    Console.WriteLine("=== Dokumentationskommentare (///) ===");
    int docFailures = 0;
    void CheckDoc(string title, bool ok, string detail = "")
    {
        if (!ok) docFailures++;
        Console.WriteLine(ok ? $"OK: {title}" : $"FEHLER: {title} {detail}");
    }

    var plain = fire.Editor.DocComments.Parse("Draws a circle.\nSecond line of the same paragraph.\n\nNew paragraph.");
    CheckDoc("Parse: reiner Text, Zeilen eines Absatzes werden verbunden, Leerzeile trennt Absaetze",
        plain?.Summary == "Draws a circle. Second line of the same paragraph.\nNew paragraph.", plain?.Summary);

    var tagged = fire.Editor.DocComments.Parse("<summary>\nAdds <c>two</c> numbers, see <see cref=\"Sub\"/>.\n</summary>\n<param name=\"a\">first &lt;int&gt;</param>\n<param name=\"b\">second</param>\n<returns>the sum</returns>\n<remarks>Fast.</remarks>");
    CheckDoc("Parse: summary, c, see cref, Entities", tagged?.Summary == "Adds two numbers, see Sub.", tagged?.Summary);
    CheckDoc("Parse: Parameter in Reihenfolge mit Text", tagged != null && tagged.Parameters.Count == 2 && tagged.Parameters[0] == ("a", "first <int>") && tagged.Parameters[1] == ("b", "second"));
    CheckDoc("Parse: returns und remarks", tagged?.Returns == "the sum" && tagged?.Remarks == "Fast.");
    CheckDoc("Parse: leerer Kommentar ist kein Dokument", fire.Editor.DocComments.Parse("  \n ") == null);

    string docSource = """
        /// A calculator.
        /// Works on ints.
        class Calc {
            /// <summary>Adds two numbers.</summary>
            /// <param name="a">first</param>
            /// <param name="b">second &amp; last</param>
            /// <returns>the sum</returns>
            int Add(int a, int b) { return a + b }

            /// Current total.
            int total

            /// <summary>Doubled total</summary>
            int Double { get { return this.total * 2 } }

            // an ordinary comment is no documentation
            Plain() { }

            /// separated from its method by a blank line

            Separated() { }
        }
        var c = new Calc()
        c.Add(1, 2)
        c.total
        c.Double
        c.Plain()
        c.Separated()
        """;

    var docIndex = fire.Editor.ScriptSymbolIndex.Build(docSource);
    fire.Editor.ResolvedSymbol? SymbolAt(string needle)
    {
        int offset = docSource.LastIndexOf(needle, StringComparison.Ordinal) + 1;
        return fire.Editor.NavigationEngine.TryResolveSymbol(docSource, offset, docIndex);
    }

    var calcDoc = SymbolAt("Calc()")?.Documentation;
    CheckDoc("Klasse: /// ueber der Deklaration, mehrere Zeilen werden verbunden", calcDoc?.Summary == "A calculator. Works on ints.", calcDoc?.Summary);
    var addSymbol = SymbolAt("Add(1");
    var addDoc = addSymbol?.Documentation;
    CheckDoc("Methode: summary, Parameter und returns", addDoc?.Summary == "Adds two numbers." && addDoc.Parameters.Count == 2
        && addDoc.Parameters[1].Text == "second & last" && addDoc.Returns == "the sum", addDoc?.ToPlainText());
    CheckDoc("Methode: die Kopfzeile nennt Klasse und Signatur", addSymbol?.Header.Contains("Calc.Add(int a, int b)") == true, addSymbol?.Header);
    CheckDoc("Feld: einfacher /// Text", SymbolAt("total\nc.Double")?.Documentation?.Summary == "Current total.");
    CheckDoc("Property: summary", SymbolAt("Double\nc.Plain")?.Documentation?.Summary == "Doubled total");
    CheckDoc("Gewoehnlicher // Kommentar ist keine Dokumentation", SymbolAt("Plain()\nc.Sep")?.Documentation == null);
    CheckDoc("Leerzeile zwischen /// und Deklaration: keine Dokumentation", SymbolAt("Separated()")?.Documentation == null);
    CheckDoc("Die Deklarationsstelle selbst loest ebenfalls auf (Cursor auf dem Namen in der Klasse)",
        fire.Editor.NavigationEngine.TryResolveSymbol(docSource, docSource.IndexOf("Add(int a") + 1, docIndex)?.Documentation?.Summary == "Adds two numbers.");

    var items = fire.Editor.CompletionEngine.GetSuggestions(docSource + "\nc.", docSource.Length + 3, fire.Editor.ScriptSymbolIndex.Build(docSource + "\nc."));
    CheckDoc("Vervollstaendigung: Eintraege tragen ihre Dokumentation, andere nicht",
        items.FirstOrDefault(i => i.Text == "Add")?.Documentation?.Summary == "Adds two numbers." && items.FirstOrDefault(i => i.Text == "Plain")?.Documentation == null);
    var classItems = fire.Editor.CompletionEngine.GetSuggestions(docSource + "\nvar d = new Ca", docSource.Length + 15, fire.Editor.ScriptSymbolIndex.Build(docSource + "\nvar d = new Ca"));
    CheckDoc("Vervollstaendigung: Klassen tragen ihre Dokumentation", classItems.FirstOrDefault(i => i.Text == "Calc")?.Documentation?.Summary == "A calculator. Works on ints.");

    // Lambdas with an expression body (docs/ROADMAP_EXPRESSIONS.md step 7): the parameter's type from the called method's `bool lambda<T>` /
    // `lambda expression<T>` (T from the receiver: `new Q<Person>()`), completion as in a lambda with a block; the hover says "expression tree"
    string treeSource = "class Order { int total }\nclass Person { int age\n string name\n List<Order> orders }\nclass Q<T> {\n string Plain(bool lambda<T> f) { return \"\" }\n string Tree(lambda expression<T> e) { return \"\" }\n string Two(lambda expression<T, Order> e) { return \"\" }\n}\nvar q = new Q<Person>()\nvar qo = new Q<Order>()\n";
    List<string> TreeCompletion(string call)
    {
        string full = treeSource + call;
        return fire.Editor.CompletionEngine.GetSuggestions(full, full.Length, fire.Editor.ScriptSymbolIndex.Build(full)).Select(i => i.Text).ToList();
    }
    fire.Editor.ResolvedSymbol? TreeHover(string call, string at)
    {
        string full = treeSource + call;
        return fire.Editor.NavigationEngine.TryResolveSymbol(full, full.LastIndexOf(at, StringComparison.Ordinal), fire.Editor.ScriptSymbolIndex.Build(full));
    }
    var person = new[] { "age", "name", "orders" };
    CheckDoc("Lambda mit Ausdrucksrumpf: der Parameter hat den Typ aus lambda<T> und aus lambda expression<T> (T vom Empfaenger), auch weiter hinten im Rumpf und mit geschriebenem Typ",
        new[] { "q.Plain(p => p.", "q.Tree(p => p.", "q.Tree(p => p.age > 3 && p.", "q.Tree((Person x) => x.", "new Q<Person>().Tree(p => p." }
            .All(c => TreeCompletion(c) is var names && person.All(names.Contains) && !names.Contains("total")),
        string.Join(" | ", TreeCompletion("q.Tree(p => p.")));
    CheckDoc("Lambda mit Ausdrucksrumpf: zwei Parameter (lambda expression<T, Order>), ein anderes Typargument (Q<Order>); ein Geschwister-Argument sieht den Parameter nicht",
        TreeCompletion("q.Two((p, o) => o.").SequenceEqual(new[] { "total" }) && TreeCompletion("qo.Tree(o => o.").SequenceEqual(new[] { "total" })
            && TreeCompletion("q.Tree(p => p.age > 1, p.").Count > 5,
        string.Join(",", TreeCompletion("q.Two((p, o) => o.")) + " / " + string.Join(",", TreeCompletion("qo.Tree(o => o.")));
    var treeHover = TreeHover("q.Tree(p => p.age > 3)", "p.age");
    var plainHover = TreeHover("q.Plain(p => p.age > 3)", "p.age");
    CheckDoc("Hover ueber einem Lambda-Parameter: Typ und \"expression tree\" bei lambda expression<T>, die Methode mit ihrer Signatur",
        treeHover?.Header == "(lambda parameter) p : Person - expression tree" && treeHover.Documentation?.Summary.Contains("Q.Tree as an expression tree (lambda expression<T>)") == true
            && plainHover?.Header == "(lambda parameter) p : Person" && TreeHover("q.Two((p, o) => o.total > p.age)", "Two")?.Header == "(method) Q.Two(lambda expression<T, Order> e) → string",
        treeHover?.Header + " / " + plainHover?.Header);

    // Call context: tooltip stays during the arguments, new Foo( jumps to the constructor
    string callSource = """
        /// A point.
        class Point {
            int x
            /// <summary>Creates the origin.</summary>
            construct() { this.x = 0 }
            /// <summary>Creates a point.</summary>
            /// <param name="x">the x value</param>
            construct(int x) { this.x = x }
            /// <summary>Moves it.</summary>
            Move(int dx, int dy) { }
        }
        """;
    fire.Editor.ResolvedSymbol? CallAt(string text)
    {
        string full = callSource + "\n" + text;
        var idx = fire.Editor.ScriptSymbolIndex.Build(full);
        var call = fire.Editor.NavigationEngine.FindOpenCall(full, full.Length);
        return call == null ? null : fire.Editor.NavigationEngine.TryResolveCall(full, call, idx);
    }
    CheckDoc("Aufruf: nach 'new Point(' zeigt der Tooltip den Konstruktor", CallAt("var p = new Point(")?.Documentation?.Summary == "Creates the origin.", CallAt("var p = new Point(")?.Documentation?.Summary);
    CheckDoc("Aufruf: mit einem Argument wird der passende Konstruktor gewaehlt", CallAt("var p = new Point(5")?.Documentation?.Summary == "Creates a point.", CallAt("var p = new Point(5")?.Documentation?.Summary);
    CheckDoc("Aufruf: Konstruktor-Kopfzeile", CallAt("var p = new Point(5")?.Header.Contains("new Point(int x)") == true, CallAt("var p = new Point(5")?.Header);
    CheckDoc("Aufruf: Methode bleibt waehrend der Argumente dokumentiert", CallAt("var p = new Point(1)\np.Move(1, ")?.Documentation?.Summary == "Moves it.", CallAt("var p = new Point(1)\np.Move(1, ")?.Documentation?.Summary);
    CheckDoc("Aufruf: nach der schliessenden Klammer gibt es keinen offenen Aufruf", CallAt("var p = new Point(1)") == null);
    CheckDoc("Aufruf: Klammern in Strings zaehlen nicht", CallAt("var p = new Point(\"(\")") == null);

    Console.WriteLine(docFailures == 0 ? "Alle Dokumentationskommentar-Pruefungen bestanden." : $"FEHLER: {docFailures} Dokumentationskommentar-Pruefung(en) fehlgeschlagen.");
}

TestResume.Group("ProjectTests.Run", () => { ProjectTests.Run(); });
TestResume.Group("RegistryTests.Run", () => { RegistryTests.Run(); });
TestResume.Group("NotesTests.Run", () => { NotesTests.Run(); });
TestResume.Group("ExplorerTests.Run", () => { ExplorerTests.Run(); });
TestResume.Group("BackgroundCheckTests.Run", () => { BackgroundCheckTests.Run(); });
TestResume.Group("PublishTests.Run", () => { PublishTests.Run(); });
TestResume.Group("McpServerTests.Run", () => { McpServerTests.Run(); });
TestResume.Group("AiModelsTests.Run", () => { AiModelsTests.Run(); });
TestResume.Group("DiagnosticTests.Run", () => { DiagnosticTests.Run(); });
TestResume.Group("TablesTests.Run", () => { TablesTests.Run(); });
TestResume.Group("ProgramFileTests.Run", () => { ProgramFileTests.Run(); });
TestResume.Group("SyntaxTests.Run", () => { SyntaxTests.Run(); });
TestResume.Group("TypeTests.Run", () => { TypeTests.Run(); });
TestResume.Group("CompletionTests.Run", () => { CompletionTests.Run(); });
TestResume.Group("TestingTests.Run", () => { TestingTests.Run(); });
TestResume.Group("CircuitEditorTests.Run", () => { CircuitEditorTests.Run(); });
TestResume.Group("PreviewTests", PreviewTests.Run, 0);
TestResume.Group("ImageTests.Run", () => { ImageTests.Run(); });
TestResume.Group("GitTests.Run", () => { GitTests.Run(); });
TestResume.Group("GitSyncTests.Run", () => { GitSyncTests.Run(); });

// ---------------------------------------------------------------------------------------------------------------------------
// Native backend (fire.Native): the same source runs in the VM and as generated C++ - the output must be identical
// ---------------------------------------------------------------------------------------------------------------------------
nativeTests:
if (Environment.GetEnvironmentVariable("FIRE_TEST_NO_NATIVE") == "1") Console.WriteLine("(FIRE_TEST_NO_NATIVE: the native checks are skipped)");
else
{
    Console.WriteLine("=== Native-Backend: erzeugtes C++ gegen die VM ===");
    Console.WriteLine($"(bis zu {TestJobs.Count} Faelle zugleich (FIRE_TEST_JOBS), gebaut mit {NativeTestBuild.Opt} (FIRE_TEST_OPT){(NativeTestBuild.Ccache != null ? ", ccache" : "")})");
    // (the async reads of the output of processes complete on the thread pool: enough threads that a worker that waits for a process never waits for them)
    ThreadPool.GetMinThreads(out int minWorkers, out int minIo);
    ThreadPool.SetMinThreads(Math.Max(minWorkers, 8 * TestJobs.Count), Math.Max(minIo, 8 * TestJobs.Count));
    int natFailures = 0;
    void CheckNat(string title, bool ok, string? detail = null)
    {
        if (!ok) Interlocked.Increment(ref natFailures);
        Console.WriteLine(ok ? $"OK: {title}" : $"FEHLER: {title}" + (detail == null ? "" : $"\n{detail}"));
    }

    string? FindCxx()
    {
        foreach (var name in new[] { "g++", "clang++", "c++" })
        {
            try
            {
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(name, "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
                p.WaitForExit();
                if (p.ExitCode == 0) return name;
            }
            catch (Exception) { }
        }
        return null;
    }

    var vmUnhandled = new Dictionary<string, string>();
    const string NotCompiled = "WIRD NICHT UEBERSETZT: ";
    // (the file of a case: its stack traces name it relative to its folder, as `forge run <case>.fire` does - docs/spec/STACKTRACE.md)
    var vmCaseFiles = new Dictionary<string, string>();
    foreach (var f in TestCases.Load("native")) vmCaseFiles.TryAdd(f.Source, f.File);
    // the VM is the VM in C (M1): the reference of the cases "native == VM" is what `fire` prints - and the stored .expected of the case files
    string vmOutput(string source)
    {
        CVmTest.Result run;
        string? caseFile = vmCaseFiles.TryGetValue(source, out var file) ? file : null;
        try { run = CVmTest.Run(new[] { source }, VmExecutionMode.Release, sourcePaths: caseFile != null ? new string?[] { caseFile } : null, workingDirectory: caseFile != null ? Path.GetDirectoryName(caseFile) : null); }
        catch (Exception ex) when (fire.Compiler.CompileDiagnostics.Of(ex).Count > 0)
        {
            // a case that is not a program (any more): its check fails with the errors of the compiler - it does not end the whole run
            return NotCompiled + string.Join("\n", fire.Compiler.CompileDiagnostics.Of(ex).Select(d => d.Code + " " + d.Message)) + "\n";
        }
        // what the command line runner writes to stderr for an exception that nothing catches (an "Abbruch:" case compares it with the native program)
        if (run.ExitCode != 0 && run.Unhandled != null) lock (vmUnhandled) vmUnhandled[source] = string.Join("\n", run.Errors.TrimEnd('\n').Split('\n').Where(l => !l.StartsWith("warning ")));
        return string.Join("\n", run.Printed) + (run.Printed.Count > 0 ? "\n" : "");
    }

    // the cases "native == VM" are files: tests/cases/native (TestCases); here only those whose source is made at run time
    var natCases = TestCases.Load("native").Select(c => (c.Name, c.Source)).Concat(new (string Name, string Source)[]
    {
    }).ToArray();

    // extern: C functions of the C library (Linux; other systems name the library differently)
    if (OperatingSystem.IsLinux())
        natCases = natCases.Append(("extern: Funktionen der C-Bibliothek (Zahlen, Zeichenketten, Zeiger auf Variablen)", """
            #extern "libc.so.6"
            extern int abs(int n)
            extern int atoi(string s)
            extern int strlen(string s)
            extern string getenv(string name)
            extern int toupper(int c)
            extern float atof(string s)
            extern int sscanf(string text, string format, int* out)
            extern int labs(int n)
            print(abs(-5))
            word w = 7
            print(abs(w) + " " + labs(18446744073709551615!word))
            print(atoi("1234") + 1)
            print(strlen("hello"))
            print(strlen("äö"))
            print(getenv("FIRE_SURELY_UNSET_VARIABLE") == undefined)
            print(toupper(97))
            print(atof("2.5") * 2)
            unsafe {
                var n = 0
                var matched = sscanf("42", "%ld", &n)
                print(matched)
                print(n)
            }
            """)).ToArray();

    // Net: an empty host without IPv6 (a container): FIRE_NET_NO_IPV6=1 (set by the program through the C library, so the natives of the VM see it too) makes the IPv6
    // attempt fail - the listener, the UDP socket and Http.Server are on 0.0.0.0 (IPv4) instead of throwing; at the end the switch is off again (the VM runs the cases in this process)
    if (OperatingSystem.IsLinux())
    {
        const string noIpv6Name = "Net: ohne IPv6 (FIRE_NET_NO_IPV6=1) lauscht ein leerer Host auf 0.0.0.0 - TcpListener, UdpSocket, Http.Server";
        const string noIpv6 = """
            #extern "libc.so.6"
            #import "net"
            #import "http"
            extern int setenv(string name, string value, int overwrite)
            setenv("FIRE_NET_NO_IPV6", "1", 1)
            try {
                var l = new Net.TcpListener("", 0)
                print("listener " + l.Host + " " + (l.Port > 0))
                var c = new Net.TcpClient("127.0.0.1", l.Port)
                var s = l.Accept(2000)
                c.WriteString("hi")
                var buf = new byte[8]
                print("tcp " + s.Read(buf, 0, 8))
                s.Close()
                c.Close()
                l.Close()
                var u = new Net.UdpSocket("", 0)
                var v = new Net.UdpSocket("127.0.0.1", 0)
                v.SendString("ping", "127.0.0.1", u.Port)
                print("udp " + u.ReceiveFrom(buf, 0, 8, 2000) + " " + u.FromHost)
                try { u.SendString("x", "::1", v.Port) } catch (Net.NetException e) { print("udp ipv4 only " + e.code) }
                u.Close()
                v.Close()
                var server = new Http.Server("", 0)
                print("http " + (server.Port > 0))
                server.Close()
            } catch (Net.NetException e) { print("error " + e.message) }
            setenv("FIRE_NET_NO_IPV6", "0", 1)
            """;
        natCases = natCases.Append((noIpv6Name, noIpv6)).ToArray();
        if (TestSelected(noIpv6Name))
        {
            string got = vmOutput(noIpv6);
            CheckNat("VM: " + noIpv6Name, got == "listener 0.0.0.0 True\ntcp 2\nudp 4 127.0.0.1\nudp ipv4 only 5\nhttp True\n", got);
        }
    }

    {
        // in the VM a call is an error (a catchable one); the `#if` of a C body is C's (the preprocessor of fire leaves it); the types are checked
        string vmInline = vmOutput("inline c int Add(int a, int b) {\n#if defined(X)\n  return 0;\n#endif\n  return a + b; }\nprint(1)\ntry { print(Add(1, 2)) } catch (e) { print(e.message) }\nvar inline = 3\nprint(inline)");
        string vmInlineMethod = vmOutput("class M {\n  static inline c int Add(int a, int b) { return a + b; }\n}\ntry { print(M.Add(1, 2)) } catch (e) { print(e.message) }");
        CheckNat("inline c als Methode in der VM: der Aufruf ist ein Fehler, der die Methode nennt (M.Add)", vmInlineMethod.StartsWith("'M.Add' is an inline C function"), vmInlineMethod);
        // (the class and the texts of the C# VM: its InvalidOperationException became a catchable ArgumentException - also for an `extern` without a library)
        string vmInlineClass = vmOutput("inline c int Add(int a, int b) { return a + b; }\ntry { Add(1, 2) } catch (ArgumentException e) { print(\"caught\") }\nextern int NoLib(int a)\ntry { NoLib(1) } catch (ArgumentException e) { print(e.message) }\nprint(\"end\")");
        CheckNat("inline c in der VM: eine abfangbare ArgumentException; ein extern ohne Bibliothek ebenso",
            vmInlineClass == "caught\n'NoLib' is declared extern, but neither linked manually (ExternRegistry.Register in the host) nor assigned to a native library with '#extern \"libName\"'.\nend\n", vmInlineClass);
        CheckNat("inline c in der VM: der Aufruf ist ein Fehler, #if im C-Rumpf gehoert C, 'inline' bleibt ein Name",
            vmInline.StartsWith("1\n'Add' is an inline C function: it runs only in a native build") && vmInline.EndsWith("\n3\n"), vmInline);
        string InlineError(string source) { try { new Linker().CompileAndLink(new[] { source }); return ""; } catch (Exception ex) { return ex.Message; } }
        CheckNat("inline c: Typen, die C nicht nehmen kann, sind ein Fehler beim Uebersetzen",
            InlineError("inline c int F(int a[]) { return 0; }").Contains("has a type C cannot take")
            && InlineError("class Dog { }\ninline c Dog G() { return 0; }").Contains("result type C cannot give")
            && InlineError("inline c int H(a) { return 0; }").Contains("needs a type")
            && InlineError("inline c int K(int a)").Contains("C body"), InlineError("inline c int K(int a)"));
        var inlineSpans = fire.Editor.SyntaxHighlighter.Highlight("inline c int Add(int a) { return a + 1; /* c */ }\n");
        string inlineText = "inline c int Add(int a) { return a + 1; /* c */ }\n";
        string SpanAt(string word) { int at = inlineText.IndexOf(word, StringComparison.Ordinal); return inlineSpans.FirstOrDefault(sp => sp.Start == at).Category.ToString(); }
        CheckNat("inline c im Editor: 'inline c' sind Schluesselwoerter, der Rumpf ist als C gefaerbt",
            SpanAt("inline") == "Keyword" && SpanAt("return") == "Keyword" && SpanAt("/* c */") == "Comment" && SpanAt("1;") == "Number",
            string.Join(", ", inlineSpans.Select(sp => $"{inlineText.Substring(sp.Start, sp.Length)}={sp.Category}")));
        var inactiveInline = fire.Compiler.ConditionalSymbols.InactiveLines("inline c int A() {\n#if X\n  return 1;\n#endif\n  return 0; }\n", new HashSet<string>());
        CheckNat("inline c im Editor: das #if des C-Rumpfs graut nichts aus", !inactiveInline.Contains(true));
    }

    // Fire threads (THREADING_DESIGN): only programs whose output does not depend on how the threads interleave

    // The IO bridge (packages/io/native/fire_bridge_io.h); the console is tested separately (a program with input)

    // Graphics (packages/graphics/native/fire_bridge_graphics.h)

    // Ownership (SPEC 2.2, 2.3): dead objects, what `return` takes along, `Takes`

    // Ownership: arrays own, call arguments, List.Take

    // Real enums (not an int), flag enums and flag enums as attributes (docs/language/10-enums-and-namespaces.md)

    // Named arguments (docs/language/08-classes.md)

    // Dictionary, HashSet and the ownership of containers (Owns, docs/language/07-arrays-and-lists.md)

    // Environment: arguments, environment variables, platform (docs/language/19-standard-library.md)

    // Processes (#import "process", docs/stdlib/process.md): POSIX shells and pipes - the same in the VM and natively

    // Hash functions (#import "hash", docs/stdlib/hash.md): the same digests in the VM and natively, errors as ArgumentException

    // Bytes as arguments of natives (fire_rt.h bytesArg/bufferArg, docs/packages/natives.md) and `byte` declarations as buffers (SPEC 8.4.2): the VM and natively the same

    // `word` (SPEC 8.10): the machine word of the target, unsigned and without a unit - 64 bits here (the VM is the machine itself); the VM and natively the same

    // Compression (#import "compression", docs/stdlib/compression.md): the same bytes in the VM and natively (the Deflate is the package's own C)

    // Differences that were aligned

    // Pointers (SPEC 8.3)

    // PseudoRandom (#import "random"): pure fire, the same numbers in the VM and in the native build

    // JSON (#import "json", docs/stdlib/json.md): the VM (JsonMethods) and the native runtime (fire_json.h) write and read the same text

    // The look of the system (SystemTheme; the tests switch the query off: FIRE_SYSTEM_THEME=off) and its overrides Root.Scaling/Dark/Accent

    // The device framework (docs/devices/README.md): drivers of gpio, i2c and spi with their simulated devices
    natCases = natCases.Concat(new (string Name, string Source)[]
    {
        ("Circuits: eine .biscuit-Datei wird eine Klasse (Felder der benannten Elemente, Ausdruck eines Parameters, Bindung)", fire.UI.Markup.BiscuitGenerator.Generate("""
            {
              "biscuit": 1,
              "timeStep": "10us",
              "parameters": {"gain": "10"},
              "elements": [
                {"type": "Rail", "name": "vin", "from": [0, 0], "to": [-4, 0], "voltage": "0.5"},
                {"type": "Resistor", "name": "rin", "from": [0, 0], "to": [8, 0], "resistance": "1k"},
                {"type": "OpAmp", "name": "u1", "from": [8, 2], "to": [16, 2]},
                {"type": "Ground", "from": [8, 4], "to": [8, 8]},
                {"type": "Resistor", "name": "rf", "from": [8, 0], "to": [8, -6], "resistance": "gain*1k"},
                {"type": "Wire", "from": [8, -6], "to": [16, -6]},
                {"type": "Wire", "from": [16, -6], "to": [16, 2]},
                {"type": "LogicOutput", "name": "out", "from": [16, 2], "to": [20, 2]},
                {"type": "Scope", "name": "sc", "from": [24, -6], "to": [40, 4], "probe": "out"}
              ]
            }
            """, "Amp.biscuit") + """
            class H { static float R(float v) { return Math.Round(v * 1000) / 1000 } }
            var amp = new Amp()
            amp.Run(1ms)
            print("out " + H.R(amp.out.PinVoltage(0)) + " rf " + amp.rf.Resistance + " scope " + H.R(amp.sc.Last("y")))
            amp.Parameter("gain", 4)
            amp.Run(1ms)
            print("gain 4: out " + H.R(amp.out.PinVoltage(0)) + " rf " + amp.rf.Resistance)
            amp.vin.Level = 1V
            amp.Run(1ms)
            print("vin 1: " + H.R(amp.out.PinVoltage(0)))
            """),
    }).ToArray();

    // TLS (packages/tls/native/fire_bridge_tls.h) and HTTPS: a certificate for localhost is made here (the tests trust exactly it); needs the development files of OpenSSL on this machine
    {
        bool hasOpenSsl = new[] { "/usr/include/openssl/ssl.h", "/usr/local/include/openssl/ssl.h", "/opt/homebrew/include/openssl/ssl.h", "/usr/include/x86_64-linux-gnu/openssl/ssl.h" }.Any(File.Exists);
        if (!hasOpenSsl)
        {
            Console.WriteLine("(Tls/Https: uebersprungen - die Entwicklerdateien von OpenSSL (openssl/ssl.h) sind auf diesem Rechner nicht da)");
        }
        else
        {
            using var rsa = System.Security.Cryptography.RSA.Create(2048);
            var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=localhost", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            var san = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            san.AddIpAddress(System.Net.IPAddress.Loopback);
            request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension(false, false, 0, true));
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
            string FireText(string pem) => "\"" + pem.Replace("\r", "").Replace("\n", "\\n") + "\"";
            string certText = FireText(certificate.ExportCertificatePem()), keyText = FireText(rsa.ExportPkcs8PrivateKeyPem());
            string tlsHead = "#import \"tls\"\n#import \"http\"\n#import \"time\"\nvar certPem = " + certText + "\nvar keyPem = " + keyText + "\n";
            natCases = natCases.Concat(new (string Name, string Source)[]
            {
                ("Tls/Engine: TLS ueber Puffer (Tls.Engine) - Handshake ueber eine einfache TCP-Verbindung (Drain senden, Feed des Empfangenen), 10 KB in mehreren Records hin, die Antwort zurueck, Zertifikat ohne CA abgelehnt, geschlossen", tlsHead + """
                    // (the engine's records over a plain TCP connection: what Drain gives is sent, what comes is fed)
                    class Shuttle {
                        static undefined Send(Net.TcpClient tcp, Tls.Engine e) {
                            byte out[] = e.Drain()
                            if (out.length > 0) { tcp.Write(out, 0, out.length) }
                        }
                        static bool Receive(Net.TcpClient tcp, Tls.Engine e) {
                            byte buf[] = new byte[16384]
                            var n = tcp.Read(buf, 0, 16384)
                            if (n <= 0) { return false }
                            e.Feed(buf, 0, n)
                            return true
                        }
                    }
                    print("available " + Tls.Support.Available())
                    var probe = new Net.TcpListener("127.0.0.1", 0)
                    int port = trust probe.Port
                    probe.Close()
                    fire {
                        var l = new Net.TcpListener("127.0.0.1", port)
                        var server = new Tls.Server(certPem, keyPem)
                        var served = 0
                        while (served < 2) {
                            var tcp = l.TryAccept(10000)
                            if (tcp == undefined) { break }
                            served = served + 1
                            try {
                                var s = server.Accept(tcp, 5000)
                                var buf = new byte[20000]
                                var n = s.Read(buf, 0, 20000)
                                var got = n
                                while (got < 10000 && n > 0) {
                                    n = s.Read(buf, got, 20000 - got)
                                    if (n > 0) { got = got + n }
                                }
                                s.WriteString("echo " + got + " " + IO.Utf8.GetString(buf, 0, 12))
                                s.Close()
                            } catch (Net.NetException e) { }
                        }
                        server.Close()
                        l.Close()
                    }
                    Sleep(400)
                    var o = new Tls.Options()
                    o.caPem = certPem
                    var tcp = new Net.TcpClient("127.0.0.1", port)
                    tcp.ReadTimeout = 5000
                    var e = new Tls.Engine("localhost", o)
                    var rounds = 0
                    while (!e.Step()) {
                        Shuttle.Send(tcp, e)
                        if (!Shuttle.Receive(tcp, e)) { break }
                        rounds++
                    }
                    Shuttle.Send(tcp, e)
                    print("handshake " + (rounds > 0) + " " + e.Info.StartsWith("TLS"))
                    var text = "hello engine"
                    while (text.Length < 10000) { text = text + " and more" }
                    var data = IO.Utf8.GetBytes(text)
                    e.Write(data, 0, data.length)
                    Shuttle.Send(tcp, e)
                    var answer = ""
                    var plain = new byte[256]
                    while (true) {
                        var n = e.Read(plain, 0, 256)
                        if (n > 0) { answer = answer + IO.Utf8.GetString(plain, 0, n); continue }
                        if (n == 0) { break }
                        if (!Shuttle.Receive(tcp, e)) { break }
                    }
                    print("answer " + answer)
                    e.Close()
                    tcp.Close()
                    try { var x = new Tls.Engine("localhost", o); x.Close(); x.Step() } catch (Net.ClosedException c) { print("closed " + c.code) }
                    var tcp2 = new Net.TcpClient("127.0.0.1", port)
                    tcp2.ReadTimeout = 5000
                    var strict = new Tls.Engine("localhost")
                    try {
                        while (!strict.Step()) {
                            Shuttle.Send(tcp2, strict)
                            if (!Shuttle.Receive(tcp2, strict)) { break }
                        }
                        print("strict accepted?")
                    } catch (Tls.CertificateException c) { print("untrusted " + c.code) }
                    tcp2.Close()
                    """),
                ("Tls: Handshake, verschluesselte Daten, Zertifikatspruefung (unbekannt, falscher Name, ausgeschaltet) ueber Loopback", tlsHead + """
                    print("available " + Tls.Support.Available())
                    var probe = new Net.TcpListener("127.0.0.1", 0)
                    var port = probe.Port
                    probe.Close()
                    fire {
                        var l = new Net.TcpListener("127.0.0.1", port)
                        var server = new Tls.Server(certPem, keyPem)
                        var served = 0
                        while (served < 5) {
                            var tcp = l.TryAccept(10000)
                            if (tcp == undefined) { break }
                            served = served + 1
                            try {
                                var s = server.Accept(tcp, 5000)
                                var buf = new byte[64]
                                var n = s.Read(buf, 0, 64)
                                s.WriteString("echo:" + IO.Utf8.GetString(buf, 0, n))
                                s.Close()
                            } catch (Net.NetException e) { }
                        }
                        server.Close()
                        l.Close()
                    }
                    Sleep(400)
                    var o = new Tls.Options()
                    o.caPem = certPem
                    var s = Tls.Stream.Connect("localhost", port, o)
                    print("connected " + s.Info.StartsWith("TLSv1"))
                    s.WriteString("hello tls")
                    var buf = new byte[64]
                    var n = s.Read(buf, 0, 64)
                    print("got " + IO.Utf8.GetString(buf, 0, n))
                    print("eof " + s.Read(buf, 0, 64))
                    s.Close()
                    try { var t = Tls.Stream.Connect("localhost", port) } catch (Tls.CertificateException e) { print("untrusted " + e.code) }
                    var tcp2 = new Net.TcpClient("127.0.0.1", port)
                    try { var w = new Tls.Stream(tcp2, "wrong.example", o, 5000) } catch (Tls.CertificateException e) { print("wrong name " + e.code) }
                    var o2 = new Tls.Options()
                    o2.verify = false
                    var u = Tls.Stream.Connect("localhost", port, o2)
                    u.WriteString("unchecked")
                    var m = u.Read(buf, 0, 64)
                    print("unchecked " + IO.Utf8.GetString(buf, 0, m))
                    u.Close()
                    var tcp3 = new Net.TcpClient("127.0.0.1", port)
                    var v = new Tls.Stream(tcp3, "127.0.0.1", o, 5000)
                    v.WriteString("by ip")
                    var k = v.Read(buf, 0, 64)
                    print("by ip " + IO.Utf8.GetString(buf, 0, k))
                    Net.IConnection iv = v
                    iv.ReadTimeout = 3000
                    IO.IStream sv = iv
                    print("tls iconnection " + (v is of Net.IConnection) + " " + iv.RemoteHost + " " + (iv.RemotePort == port) + " " + (iv.LocalPort == v.LocalPort) + " " + v.LocalHost + " " + iv.ReadTimeout + " " + (sv is of Net.IConnection))
                    v.Close()
                    """),
                ("Https/AppServer: der TLS-Handshake im Task der Verbindung - ein Client, der nie handshaket, haelt den naechsten nicht auf", tlsHead + """
                    var probe = new Net.TcpListener("127.0.0.1", 0)
                    int port = trust probe.Port
                    probe.Close()
                    fire {
                        var server = new Http.AppServer("127.0.0.1", port)
                        server.UseTls(certPem, keyPem)
                        server.PushTo((new Http.Routes()).Map("GET", "/hello", func (Http.Context ctx) => "secure hello"))
                        while (server.Connections < 3) { server.AcceptOne(10000) }
                        Sleep(1500)
                        server.Close()
                    }
                    Sleep(400)
                    // (a client that connects and never makes its handshake: the server's accepting task does not wait for it)
                    var stall = new Net.TcpClient("127.0.0.1", port)
                    Sleep(200)
                    var o = new Tls.Options()
                    o.caPem = certPem
                    var client = new Http.Client()
                    client.timeout = 10000
                    client.tls = o
                    var before = DateTime.UtcNow.Ticks
                    var r = client.Get("https://localhost:" + port + "/hello")
                    var ms = (DateTime.UtcNow.Ticks - before) / 10000
                    print(r.status + " [" + r.Text() + "] at once " + (ms < 3000))
                    var r2 = client.Get("https://localhost:" + port + "/hello")
                    print("again " + r2.status)
                    stall.Close()
                    """),
                ("Https: Client und Server ueber TLS (https://-URL, Zertifikat des Tests, Zertifikatsfehler)", tlsHead + """
                    var probe = new Net.TcpListener("127.0.0.1", 0)
                    var port = probe.Port
                    probe.Close()
                    fire {
                        var server = new Http.Server("127.0.0.1", port)
                        server.UseTls(certPem, keyPem)
                        server.Route("GET", "/hello", func (r) => Http.Response.FromText("secure " + r.Query("name", "world")))
                        server.Route("POST", "/echo", func (r) => r.Text().ToUpper())
                        var served = 0
                        while (served < 4) {
                            if (server.ServeOne(10000)) { served = served + 1 }
                        }
                        server.Close()
                    }
                    Sleep(400)
                    var client = new Http.Client()
                    client.timeout = 10000
                    var o = new Tls.Options()
                    o.caPem = certPem
                    client.tls = o
                    var site = "https://localhost:" + port
                    var r1 = client.Get(site + "/hello?name=Anna")
                    print(r1.status + " [" + r1.Text() + "] " + r1.url.StartsWith("https://"))
                    var r2 = client.Post(site + "/echo", "grüß dich")
                    print(r2.status + " [" + r2.Text() + "]")
                    var strict = new Http.Client()
                    strict.timeout = 5000
                    try { strict.Get(site + "/hello") } catch (Tls.CertificateException e) { print("untrusted " + e.code) }
                    var loose = new Tls.Options()
                    loose.verify = false
                    strict.tls = loose
                    print(strict.Get(site + "/hello?name=loose").Text())
                    """),
            }).ToArray();
        }
    }

    // GPIO (packages/gpio/native/fire_bridge_gpio.h): the simulated chip "sim" - outputs, inputs, wires, pulls, edges, errors - the same in the VM and in the native build

    // Tracker (packages/tracker/native/fire_bridge_tracker.h): a module built in memory, rendered, played to the simulated audio device - the same in the VM and in the native build

    // Scene3D (packages/scene3d/native/fire_bridge_scene3d.h): the software renderer gives the same pixels in the VM and in the native build (and the hardware renderer, where there is a GPU, a close picture)
    natCases = natCases.Append((Scene3DTests.Name, Scene3DTests.Script)).ToArray();

    // AI (packages/ai/native/fire_ai_engine.h): a tiny model with random weights (AiTests.TinyModel) answers the same in the VM and in the native build
    natCases = natCases.Append((AiTests.Name, AiTests.Script)).ToArray();
    natCases = natCases.Append((AiTests.McpName, AiTests.McpScript)).ToArray();

    // Audio (packages/audio/native/fire_bridge_audio.h): the simulated device "sim" records what is played - the same in the VM and in the native build

    // I2C (packages/i2c/native/fire_bridge_i2c.h): the simulated bus "sim" with register-file devices - the same in the VM and in the native build

    // SPI (packages/spi/native/fire_bridge_spi.h): the simulated device "sim" (loopback, queued answers, log of what was sent) - the same in the VM and in the native build

    // WiFi (packages/wifi/native/fire_bridge_wifi.h): the simulated radio "sim" - scan, join, failures, access point - the same in the VM and in the native build

    // Network (bridges/fire_bridge_net.hpp): TCP, UDP and name resolution on the loopback interface - the same in the VM and in the native build

    // The MySQL/MariaDB driver against a running server - only when there is one: FIRE_TEST_MYSQL=mysql://127.0.0.1:3306/test (an empty database: its tables
    // are dropped), FIRE_TEST_MYSQL_USER, FIRE_TEST_MYSQL_PASSWORD. The mapping, the queries, the sessions and the relations are those of the case of the
    // in-memory database (the same program on another driver), the SQL before them is MySQL's.
    foreach (string mysqlUrl in (Environment.GetEnvironmentVariable("FIRE_TEST_MYSQL") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
    {
        string Lit(string? t) => t == null ? "undefined" : "\"" + t.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        string open = $"MyDb.Open({Lit(mysqlUrl)}, {Lit(Environment.GetEnvironmentVariable("FIRE_TEST_MYSQL_USER"))}, {Lit(Environment.GetEnvironmentVariable("FIRE_TEST_MYSQL_PASSWORD"))})";
        string mem = natCases.First(c => c.Name.StartsWith("Data/memory:", StringComparison.Ordinal)).Source;
        string header = mem.Substring(0, mem.IndexOf("print(\"memory\")", StringComparison.Ordinal)).Replace("#import \"data.memory\"", "#import \"data.mysql\"\n#import \"hash\"");
        string orm = mem.Substring(mem.IndexOf("var cdb = new Data.Connection(\"memory:\")", StringComparison.Ordinal))
            .Replace("new Data.Connection(\"memory:\")", "MyDb.Open(MyDb.url, MyDb.user, MyDb.password)")
            .Replace("cdb.Rows(\"select name, type, pk, notnull from _columns where table_name = 'people'\")", "cdb.Rows(\"select column_name as name, column_type as type, column_key = 'PRI' as pk, is_nullable = 'NO' as notnull from information_schema.columns where table_schema = database() and table_name = 'people' order by ordinal_position\")")
            .Replace("\"select name from _columns where table_name = ?\"", "\"select column_name from information_schema.columns where table_schema = database() and table_name = ?\"")
            .Replace("create table Tag(code text primary key, label text)", "create table Tag(code varchar(20) primary key, label text)")
            .Replace("\\\"Purchase\\\"", "Purchase")
            .Replace("create unique index ix_memo_text on Memo (text)", "create unique index ix_memo_text on Memo (text(100))");
        natCases = natCases.Append(("Data/MySQL " + mysqlUrl + ": der Treiber in fire gegen einen laufenden Server (FIRE_TEST_MYSQL): Handshake, Prepared Statements im Binaerprotokoll (Zahlen, Text, Unicode, NULL, BLOB, DOUBLE/FLOAT, DECIMAL, DATETIME, UNSIGNED), mehrere Anweisungen, Fehlerarten, Transaktionen mit Savepoints, TLS (prefer/require), Streaming (Zeilen vom Socket, geparkt bei Befehlen dazwischen), Prozeduren mit mehreren Ergebnissen, Login-Pruefwerte und RSA-OAEP gegen einen bekannten Schluesseltext, auf MySQL 8 die Logins ohne TLS (Schluessel vom Server, Cache, sha256_password); dann Mapping, Abfragen, Sessions und Beziehungen wie bei der In-Memory-Datenbank", header + $$"""
            class MyDb {
                static string url
                static string user
                static string password
                // (a connection to the test database without its tables)
                static Data.Connection Open(string url, string user, string password) {
                    MyDb.url = url
                    MyDb.user = user
                    MyDb.password = password
                    var c = new Data.Connection(url, new Data.Credentials(user, password))
                    var names = c.Rows("select table_name from information_schema.tables where table_schema = database()")
                    c.Execute("SET FOREIGN_KEY_CHECKS = 0")
                    foreach (n in names) { c.Execute("DROP TABLE `" + n[0] + "`") }
                    c.Execute("SET FOREIGN_KEY_CHECKS = 1")
                    c.Execute("ALTER DATABASE CHARACTER SET utf8mb4")
                    return c
                }
                static string Hex(byte data[]) {
                    var t = ""
                    var digits = "0123456789abcdef"
                    for (var i = 0; i < data.length; i++) {
                        int b = data[i]
                        t = t + digits.Substring(b >> 4, 1) + digits.Substring(b & 15, 1)
                    }
                    return t
                }
            }
            print("mysql")
            var db = {{open}}
            db.Execute("create table person(id bigint primary key auto_increment, name varchar(100) not null, age int, active tinyint); create table item(id bigint primary key auto_increment, title text, price double, blob1 longblob, f float, d datetime(3), n bigint unsigned, s smallint, m decimal(10,2))")
            print("insert " + db.Execute("insert into person(name, age, active) values (?, ?, ?)", ["Ada", 36, true]))
            db.Execute("insert into person(name, age, active) values (?, ?, ?)", ["Grace", 85, false])
            db.Execute("insert into person(name, age, active) values (?, ?, ?)", ["Linus Ünïcødé 🐧", undefined, true])
            print("last id " + db.LastInsertId)
            foreach (row in db.Query("select id, name, age from person where age > ? or age is null order by id", [40])) { print("row " + row) }
            print("count " + db.Scalar("select count(*) from person") + " text " + db.Scalar("select name from person where id = 3"))
            db.Execute("insert into item(title, price, blob1, f, d, n, s, m) values (?, ?, ?, ?, ?, ?, ?, ?)", ["Box", 9.5, [1, 2, 255], 1.25, "2026-11-13 10:20:30.250", 18000000000, -5, "12.50"])
            db.Execute("insert into item(title, price) values (?, ?)", ["Tiny", -0.000123])
            foreach (row in db.Rows("select title, price, f, d, n, s, m, length(blob1) from item order by id")) { print("item " + row) }
            byte blob[] = db.Scalar("select blob1 from item where id = 1")
            print("blob " + blob.length + " " + blob[2])
            print("update " + db.Execute("update person set age = age + 1 where age is not null") + " delete " + db.Execute("delete from item where id = ?", [2]))
            print("multi")
            db.Execute("insert into item(title) values ('a'); insert into item(title) values ('b'); select 1; update item set price = 1 where price is null")
            print("items " + db.Scalar("select count(*) from item") + " priced " + db.Scalar("select count(*) from item where price = 1"))
            try { db.Execute("insert into person(id, name) values (?, ?)", [1, "Dup"]) } catch (Data.ConstraintException e) { print("constraint " + e.Code) }
            try { db.Execute("insert into person(age) values (?)", [1]) } catch (Data.ConstraintException e) { print("not null " + e.Code) }
            try { db.Execute("select * from nothing") } catch (Data.DataException e) { print("error " + e.Code) }
            try { db.Execute("selec 1") } catch (Data.DataException e) { print("syntax " + e.Code) }
            try { db.Execute("insert into person(name) values (?)", []) } catch (ArgumentException e) { print("args " + e.message) }
            db.Transaction(t => { t.Execute("update person set age = age + 1 where id = ?", [1]) })
            print("after commit " + db.Scalar("select age from person where id = 1"))
            try {
                db.Transaction(t => {
                    t.Execute("update person set age = 0 where id = ?", [1])
                    throw new Exception("stop")
                })
            } catch (e) { print("rolled back: " + e.message) }
            print("after rollback " + db.Scalar("select age from person where id = 1"))
            db.Begin()
            db.Execute("delete from person where id = ?", [3])
            db.Begin()
            db.Execute("delete from person where id = ?", [2])
            db.Rollback()
            db.Commit()
            print("nested " + db.Scalar("select count(*) from person"))
            print("case " + db.From<Pupil>().Where(p => p.name.StartsWith("a")).ToSql())
            db.Close()
            var tls = new Data.Connection(MyDb.url + "?tls=require", new Data.Credentials(MyDb.user, MyDb.password))
            print("tls " + (tls.Scalar("show status like 'Ssl_version'") != undefined) + " " + (("" + tls.Rows("show status like 'Ssl_cipher'")[0][1]).Length > 0))
            tls.Close()
            var plain = new Data.Connection(MyDb.url + "?tls=off", new Data.Credentials(MyDb.user, MyDb.password))
            print("plain " + plain.Rows("show status like 'Ssl_cipher'")[0][1])
            plain.Close()
            try { new Data.Connection(MyDb.url, new Data.Credentials(MyDb.user, "wrong")) } catch (Data.ConnectionException e) { print("login " + e.Code) }
            try { new Data.Connection(MyDb.url + "?tls=maybe") } catch (ArgumentException e) { print("option " + e.message) }
            byte salt[] = new byte[20]
            for (var i = 0; i < 20; i++) { salt[i] = i + 1 }
            print("native " + MyDb.Hex(Data.MysqlConnection.Scramble("mysql_native_password", "secret", salt)))
            print("sha2 " + MyDb.Hex(Data.MysqlConnection.Scramble("caching_sha2_password", "secret", salt)))
            // (RSA-OAEP with a fixed seed: the ciphertext computed elsewhere (Python, decrypted again with openssl) - SPKI and PKCS#1 keys)
            byte seed[] = new byte[20]
            for (var i = 0; i < 20; i++) { seed[i] = 100 + i }
            var spki = "-----BEGIN PUBLIC KEY-----\nMIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAtsB8+42pIYlHpvk8nMCQ\n9TdESiuUKoH+siuDhELaokE11/63+18vPgPMxfVvlcRlrHbGCP3m9KfORehTnG4D\nYO6piO5UEBBY6sCtDCC6WenihFQkHIhSjzH1bQPqaVknQvESsKT3PQUwNyEhdGNy\nkLgkBq2VUlvg1N7ppXDa3Onnr1pwix5nc4KAmpbfn+azM9JHin2ddvM0XzpFuiFC\nYleYcgHO37rUe5JdBjR2xQ8Rf5yB5LlSx2BzhP5hG71Gr92KOUWDTEtGLUZX1nKO\nZkOb2LpVF2dYvsQJ/5WIxyerVBo1vi87A1WwY3oVtPaGgUMj81xUFaPYyoD6P/7K\n1wIDAQAB\n-----END PUBLIC KEY-----"
            var pkcs1 = "-----BEGIN RSA PUBLIC KEY-----\nMIIBCgKCAQEAtsB8+42pIYlHpvk8nMCQ9TdESiuUKoH+siuDhELaokE11/63+18v\nPgPMxfVvlcRlrHbGCP3m9KfORehTnG4DYO6piO5UEBBY6sCtDCC6WenihFQkHIhS\njzH1bQPqaVknQvESsKT3PQUwNyEhdGNykLgkBq2VUlvg1N7ppXDa3Onnr1pwix5n\nc4KAmpbfn+azM9JHin2ddvM0XzpFuiFCYleYcgHO37rUe5JdBjR2xQ8Rf5yB5LlS\nx2BzhP5hG71Gr92KOUWDTEtGLUZX1nKOZkOb2LpVF2dYvsQJ/5WIxyerVBo1vi87\nA1WwY3oVtPaGgUMj81xUFaPYyoD6P/7K1wIDAQAB\n-----END RSA PUBLIC KEY-----"
            var expected = "21cde573f17c4b53bee75cabb37561afb9545d456bb0761f986bd0820c876a45849cf7abb7fa60562954afed7490cb33c742d7ec3f9911eb040c441b526456a73fff3dc9dccf44f1e1a73ffd8598e797646f39428b32d64e9c80dbefbac1b27d85fa484809c1a70625f4da8567e6da118f1684eae139fa3472d2098e00eaf556ac746b890b346960cfc0c311bb5697768f99448212c48aa8acbe35cf84a57c232522926e45f929db61f4357e95fd53679282164bb9531253606c6a4e3728ecbc6f041f00751270efed9e6c27eb51e9542b147465d9576723b377307b97279f7777c41e45507287591c3ae839a77dc0ca419b47967e31935f84277a95cf41d142"
            print("rsa " + (MyDb.Hex(Data.MysqlRsa.Password(spki, "secret", salt, seed)) == expected) + " " + (MyDb.Hex(Data.MysqlRsa.Password(pkcs1, "secret", salt, seed)) == expected))
            try { Data.MysqlRsa.Password("-----BEGIN PUBLIC KEY-----\nAAAA\n-----END PUBLIC KEY-----", "x", salt, seed) } catch (Data.ConnectionException e) { print("bad key " + e.message) }
            // (streaming: rows come from the socket one by one; a command in between parks the rest; a reader left early)
            var sdb0 = new Data.Connection(MyDb.url, new Data.Credentials(MyDb.user, MyDb.password))
            sdb0.Execute("create table many(id int primary key, v varchar(20))")
            sdb0.Begin()
            for (var i = 0; i < 300; i++) { sdb0.Execute("insert into many values (?, ?)", [i, "v" + i]) }
            sdb0.Commit()
            int seen = 0
            int sum = 0
            foreach (row in sdb0.Query("select id, v from many order by id")) {
                seen++
                int id = row.Int("id")
                sum = sum + id
                if (id % 100 == 0) { sdb0.Execute("update many set v = ? where id = ?", ["changed", id + 1]) }
            }
            print("streamed " + seen + " sum " + sum + " changed " + sdb0.Scalar("select count(*) from many where v = 'changed'"))
            int early = 0
            foreach (row in sdb0.Query("select id from many order by id")) {
                early++
                if (early == 5) { break }
            }
            print("early " + early + " then " + sdb0.Scalar("select count(*) from many") + " " + sdb0.Rows("select v from many where id = 1")[0][0])
            var outer = sdb0.Query("select id from many where id < 3 order by id")
            var inner = sdb0.Query("select id from many where id >= 297 order by id")
            var both = ""
            foreach (row in outer) {
                both = both + " " + row[0]
                foreach (r2 in inner) { both = both + "/" + r2[0] }
            }
            print("nested readers" + both)
            sdb0.Execute("drop procedure if exists two")
            sdb0.Execute("create procedure two(in lim int) begin select id from many where id < lim order by id; select count(*) from many; end")
            print("call " + sdb0.Rows("call two(?)", [3]).Count + " then " + sdb0.Scalar("select 7"))
            sdb0.Execute("call two(2)")
            print("call exec then " + sdb0.Scalar("select 8"))
            // (the logins without TLS: RSA - on a server with caching_sha2_password (MySQL 8) when the test user may make users (CREATE USER, RELOAD))
            string version = sdb0.Scalar("select version()")
            if (version.IndexOf("MariaDB") >= 0) { print("logins: MariaDB") }
            else {
                string server = MyDb.url.Substring(0, MyDb.url.LastIndexOf("/") + 1)
                try {
                    sdb0.Execute("drop user if exists fire_rsa, fire_sha")
                    sdb0.Execute("create user fire_rsa identified with caching_sha2_password by 'pw!rsa'")
                    sdb0.Execute("create user fire_sha identified with sha256_password by 'pw!sha'")
                    sdb0.Execute("flush privileges")
                    try { new Data.Connection(server + "?tls=off", new Data.Credentials("fire_rsa", "pw!rsa")) } catch (Data.ConnectionException e) { print("no key " + e.Code) }
                    var r1 = new Data.Connection(server + "?tls=off&fetchkey=true", new Data.Credentials("fire_rsa", "pw!rsa"))
                    print("fetched key " + r1.Scalar("select current_user()"))
                    r1.Close()
                    var r2 = new Data.Connection(server + "?tls=off", new Data.Credentials("fire_rsa", "pw!rsa"))
                    print("cached " + r2.Scalar("select current_user()"))
                    r2.Close()
                    try { new Data.Connection(server + "?tls=off&fetchkey=true", new Data.Credentials("fire_rsa", "wrong")) } catch (Data.ConnectionException e) { print("wrong " + e.Code) }
                    var r3 = new Data.Connection(server + "?tls=off&fetchkey=true", new Data.Credentials("fire_sha", "pw!sha"))
                    print("sha256 " + r3.Scalar("select current_user()"))
                    r3.Close()
                    var r4 = new Data.Connection(server + "?tls=require", new Data.Credentials("fire_sha", "pw!sha"))
                    print("sha256 tls " + r4.Scalar("select current_user()"))
                    r4.Close()
                    sdb0.Execute("drop user fire_rsa, fire_sha")
                } catch (Data.DataException e) { print("logins: not allowed " + e.Code) }
            }
            sdb0.Close()
            print("end")
            {{orm}}
            """)).ToArray();
    }

    // The SQL Server driver against a running server - only when there is one: FIRE_TEST_SQLSERVER=sqlserver://127.0.0.1:1433/test?trust=true (an
    // empty database: its tables are dropped; several separated by spaces - each with a database of its own: the native programs run at the same time),
    // FIRE_TEST_SQLSERVER_USER, FIRE_TEST_SQLSERVER_PASSWORD. As for MySQL: the
    // SQL of SQL Server first, then the mapping, queries, sessions and relations of the case of the in-memory database.
    foreach (string sqlUrl in (Environment.GetEnvironmentVariable("FIRE_TEST_SQLSERVER") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
    {
        string Lit2(string? t) => t == null ? "undefined" : "\"" + t.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        string sopen = $"MsDb.Open({Lit2(sqlUrl)}, {Lit2(Environment.GetEnvironmentVariable("FIRE_TEST_SQLSERVER_USER"))}, {Lit2(Environment.GetEnvironmentVariable("FIRE_TEST_SQLSERVER_PASSWORD"))})";
        string smem = natCases.First(c => c.Name.StartsWith("Data/memory:", StringComparison.Ordinal)).Source;
        string sheader = smem.Substring(0, smem.IndexOf("print(\"memory\")", StringComparison.Ordinal)).Replace("#import \"data.memory\"", "#import \"data.sqlserver\"\n#import \"net\"\n#import \"io\"");
        string sorm = smem.Substring(smem.IndexOf("var cdb = new Data.Connection(\"memory:\")", StringComparison.Ordinal))
            .Replace("new Data.Connection(\"memory:\")", "MsDb.Open(MsDb.url, MsDb.user, MsDb.password)")
            .Replace("cdb.Rows(\"select name, type, pk, notnull from _columns where table_name = 'people'\")", "cdb.Rows(\"select c.COLUMN_NAME as name, c.DATA_TYPE as type, case when k.COLUMN_NAME is null then 0 else 1 end as pk, case when c.IS_NULLABLE = 'NO' then 1 else 0 end as notnull from INFORMATION_SCHEMA.COLUMNS c left join INFORMATION_SCHEMA.KEY_COLUMN_USAGE k on k.TABLE_NAME = c.TABLE_NAME and k.COLUMN_NAME = c.COLUMN_NAME and OBJECTPROPERTY(OBJECT_ID(k.CONSTRAINT_SCHEMA + '.' + k.CONSTRAINT_NAME), 'IsPrimaryKey') = 1 where c.TABLE_NAME = 'people' order by c.ORDINAL_POSITION\")")
            .Replace("\"select name from _columns where table_name = ?\"", "\"select COLUMN_NAME from INFORMATION_SCHEMA.COLUMNS where TABLE_NAME = ?\"")
            .Replace("create table Tag(code text primary key, label text)", "create table Tag(code nvarchar(20) primary key, label nvarchar(100))")
            .Replace("create unique index ix_memo_text on Memo (text)", "alter table Memo alter column [text] nvarchar(450); create unique index ix_memo_text on Memo ([text])");
        natCases = natCases.Append(("Data/SqlServer " + sqlUrl + ": der TDS-Treiber (mit mars=true ueber SMP-Sitzungen, ohne mit geparkten Zeilen), benannte Instanzen ueber den SQL Server Browser in fire gegen einen laufenden Server (FIRE_TEST_SQLSERVER): PRELOGIN, LOGIN7, TLS in TDS (mandatory, optional nur der Login, off, Zertifikat geprueft), SQL-Batches und sp_executesql mit typisierten Parametern (Zahlen, Text, Unicode, NULL, VARBINARY, FLOAT, DECIMAL, MONEY, Datum und Zeit, GUID, sql_variant), Texte und Bytes ueber mehrere Pakete und als PLP, Fehlerarten, Transaktionen mit Savepoints (Deskriptor), IDENTITY ueber SCOPE_IDENTITY, Streaming mit geparkten Zeilen, Prozeduren mit mehreren Ergebnissen; dann Mapping, Abfragen, Sessions und Beziehungen wie bei der In-Memory-Datenbank", sheader + $$"""
            class MsDb {
                static string url
                static string user
                static string password
                // (a connection to the test database without its tables)
                static Data.Connection Open(string url, string user, string password) {
                    MsDb.url = url
                    MsDb.user = user
                    MsDb.password = password
                    var c = new Data.Connection(url, new Data.Credentials(user, password))
                    var names = c.Rows("select name from sys.tables")
                    foreach (n in names) { c.Execute("DROP TABLE [" + n[0] + "]") }
                    return c
                }
                // (the URL with a named instance in place of the port: sqlserver://host\fire/db?options&browser=...)
                static string Named(string instance, defined int browser) {
                    string u = MsDb.url.Substring(12)
                    string options = ""
                    int q = u.IndexOf("?")
                    if (q >= 0) {
                        options = u.Substring(q + 1)
                        u = u.Substring(0, q)
                    }
                    int slash = u.IndexOf("/")
                    string hostPort = u.Substring(0, slash)
                    string db = u.Substring(slash + 1)
                    string host = hostPort
                    int colon = hostPort.LastIndexOf(":")
                    if (colon >= 0) { host = hostPort.Substring(0, colon) }
                    return "sqlserver://" + host + "\\" + instance + "/" + db + "?" + options + "&browser=" + browser
                }
                static defined int PortOf() {
                    string u = MsDb.url.Substring(12)
                    int slash = u.IndexOf("/")
                    string hostPort = u.Substring(0, slash)
                    int colon = hostPort.LastIndexOf(":")
                    if (colon < 0) { return 1433 }
                    any p = hostPort.Substring(colon + 1).TryToInt()
                    return trust p
                }
                static string WithOptions(string options) {
                    string u = MsDb.url
                    int q = u.IndexOf("?")
                    if (q >= 0) { u = u.Substring(0, q) }
                    return u + options
                }
            }
            class BrowserJob {
                Net.UdpSocket udp
                defined int port
                construct(Net.UdpSocket udp, defined int port) {
                    this.udp = take udp
                    this.port = port
                }
            }
            class Browser {
                // (a SQL Server Browser for the test: two answers, the instance asked for on the port of the job - "nosuch" is not there)
                static undefined Serve(BrowserJob job) {
                    var udp = job.udp
                    int port = job.port
                    byte q[] = new byte[256]
                    for (var i = 0; i < 2; i++) {
                        int n = udp.ReceiveFrom(q, 0, 256, 10000)
                        string name = IO.Utf8.GetString(q, 1, n - 2)
                        var text = "ServerName;db;InstanceName;OTHER;IsClustered;No;Version;16.0;tcp;9;;ServerName;db;InstanceName;" + name.ToUpper() + ";IsClustered;No;Version;16.0;tcp;" + port + ";;"
                        if (name == "nosuch") { text = "ServerName;db;InstanceName;OTHER;IsClustered;No;Version;16.0;tcp;9;;" }
                        byte t[] = IO.Utf8.GetBytes(text)
                        byte a[] = new byte[t.length + 3]
                        a[0] = 5
                        a[1] = t.length & 255
                        a[2] = t.length >> 8
                        for (var j = 0; j < t.length; j++) { a[3 + j] = t[j] }
                        udp.SendTo(a, udp.FromHost, udp.FromPort)
                    }
                    udp.Close()
                }
            }
            print("sqlserver")
            var db = {{sopen}}
            db.Execute("create table person(id bigint identity(1,1) primary key, name nvarchar(100) not null, age int, active bit)")
            db.Execute("create table item(id bigint identity(1,1) primary key, title nvarchar(max), price float, blob1 varbinary(max), f real, d datetime2, n decimal(20,0), s smallint, m money, sm smallmoney, dm decimal(10,2), dt datetime, sdt smalldatetime, dd date, tt time(3), dto datetimeoffset(0), g uniqueidentifier, vc varchar(20), ch nchar(3), tiny tinyint)")
            print("insert " + db.Execute("insert into person(name, age, active) values (?, ?, ?)", ["Ada", 36, true]))
            print("key " + db.LastInsertId)
            db.Execute("insert into person(name, age, active) values (?, ?, ?)", ["Grace", 85, false])
            db.Execute("insert into person(name, age, active) values (?, ?, ?)", ["Linus Ünïcødé 🐧", undefined, true])
            print("key " + db.LastInsertId)
            foreach (row in db.Query("select id, name, age, active from person where age > ? or age is null order by id", [40])) { print("row " + row) }
            print("count " + db.Scalar("select count(*) from person") + " text " + db.Scalar("select name from person where id = 3") + " len " + db.Scalar("select len(name) from person where id = 3"))
            db.Execute("insert into item(title, price, blob1, f, d, n, s, m, sm, dm, dt, sdt, dd, tt, dto, g, vc, ch, tiny) values (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)", ["Box", 9.5, [1, 2, 255], 1.25, "2026-11-13 10:20:30.1234567", 18000000000, -5, "12.3456", "-7.5", "12.50", "1815-12-10 08:30:00.997", "2024-02-29 23:59:00", "0001-01-01", "13:14:15.678", "2026-11-13 10:00:00 +02:00", "6F9619FF-8B86-D011-B42D-00C04FC964FF", "Käse", "abc", 200])
            db.Execute("insert into item(title, price) values (?, ?)", ["Tiny", -0.000123])
            foreach (row in db.Rows("select title, price, f, d, n, s, m, sm, dm, dt, sdt, dd, tt, dto, g, vc, ch, tiny, datalength(blob1) as bl from item order by id")) { print("item " + row) }
            byte blob[] = db.Scalar("select blob1 from item where id = 1")
            print("blob " + blob.length + " " + blob[2])
            print("floats " + db.Scalar("select price from item where price = ?", [9.5]) + " " + db.Scalar("select ? * 2", [0.1]) + " " + db.Scalar("select ?", [-1.5e300]) + " " + db.Scalar("select ?", [5e-324]) + " " + db.Scalar("select cast(1e-310 as float)"))
            print("numbers " + db.Scalar("select ?", [-9223372036854775807]) + " " + db.Scalar("select cast(12345678901234567890123 as decimal(38,0))") + " " + db.Scalar("select cast(-0.001 as decimal(10,3))") + " " + db.Scalar("select ?", [true]) + " " + db.Scalar("select cast(? as int) + 1", ["41"]))
            print("variant " + db.Rows("select cast(1 as sql_variant) a, cast(N'ü' as sql_variant) b, cast(2.5 as sql_variant) c, cast(null as sql_variant) d")[0])
            // (texts and bytes bigger than a packet, and than 8000 bytes: PLP)
            var big = ""
            for (var i = 0; i < 3000; i++) { big = big + "abc🐧" }
            db.Execute("insert into item(title) values (?)", [big])
            string back = db.Scalar("select title from item where id = ?", [db.LastInsertId])
            print("big text " + back.Length + " " + (back == big) + " " + db.Scalar("select len(title) from item where id = ?", [db.LastInsertId]))
            byte bigBytes[] = new byte[20000]
            for (var i = 0; i < 20000; i++) { bigBytes[i] = i % 251 }
            db.Execute("update item set blob1 = ? where id = 2", [bigBytes])
            byte bb[] = db.Scalar("select blob1 from item where id = 2")
            print("big bytes " + bb.length + " " + bb[19999] + " " + db.Scalar("select datalength(blob1) from item where id = 2"))
            print("update " + db.Execute("update person set age = age + 1 where age is not null") + " delete " + db.Execute("delete from item where id = ?", [2]))
            db.Execute("insert into item(title) values ('a'); insert into item(title) values ('b'); select 1; update item set price = 1 where price is null")
            print("items " + db.Scalar("select count(*) from item") + " priced " + db.Scalar("select count(*) from item where price = 1"))
            try { db.Execute("set identity_insert person on; insert into person(id, name) values (1, 'Dup')") } catch (Data.ConstraintException e) { print("constraint " + e.Code) }
            db.Execute("set identity_insert person off")
            try { db.Execute("insert into person(age) values (?)", [1]) } catch (Data.ConstraintException e) { print("not null " + e.Code) }
            try { db.Execute("select * from nothing") } catch (Data.DataException e) { print("error " + e.Code) }
            try { db.Execute("select from") } catch (Data.DataException e) { print("syntax " + e.Code) }
            try { db.Execute("insert into person(name) values (?)", []) } catch (ArgumentException e) { print("args " + e.message) }
            print("after errors " + db.Scalar("select 7"))
            db.Transaction(t => { t.Execute("update person set age = age + 1 where id = ?", [1]) })
            print("after commit " + db.Scalar("select age from person where id = 1") + " open " + db.Scalar("select @@trancount"))
            try {
                db.Transaction(t => {
                    t.Execute("update person set age = 0 where id = ?", [1])
                    throw new Exception("stop")
                })
            } catch (e) { print("rolled back: " + e.message) }
            print("after rollback " + db.Scalar("select age from person where id = 1"))
            db.Begin()
            db.Execute("delete from person where id = ?", [3])
            db.Begin()
            db.Execute("delete from person where id = ?", [2])
            db.Rollback()
            print("inside " + db.Scalar("select @@trancount") + " " + db.Scalar("select count(*) from person"))
            db.Commit()
            print("nested " + db.Scalar("select count(*) from person") + " open " + db.Scalar("select @@trancount"))
            print("case " + db.From<Pupil>().Where(p => p.name.StartsWith("a")).ToSql())
            // (streaming: rows from the socket one by one; a command in between parks the rest; a reader left early)
            db.Execute("create table many(id int primary key, v nvarchar(20))")
            db.Begin()
            for (var i = 0; i < 300; i++) { db.Execute("insert into many values (?, ?)", [i, "v" + i]) }
            db.Commit()
            int seen = 0
            int sum = 0
            foreach (row in db.Query("select id, v from many order by id")) {
                seen++
                int id = row.Int("id")
                sum = sum + id
                if (id % 100 == 0) { db.Execute("update many set v = ? where id = ?", ["changed", id + 1]) }
            }
            print("streamed " + seen + " sum " + sum + " changed " + db.Scalar("select count(*) from many where v = 'changed'"))
            int early = 0
            foreach (row in db.Query("select id from many order by id")) {
                early++
                if (early == 5) { break }
            }
            print("early " + early + " then " + db.Scalar("select count(*) from many") + " " + db.Rows("select v from many where id = 1")[0][0])
            var both = ""
            foreach (row in db.Query("select id from many where id < 3 order by id")) {
                both = both + " " + row[0]
                foreach (r2 in db.Query("select id from many where id >= ? and id < ? order by id", [297, 299])) { both = both + "/" + r2[0] }
                db.Execute("update many set v = 'n' where id = ?", [row.Int("id") + 10])
            }
            Data.TdsConnection tc = db.driver
            print("nested readers" + both + " updated " + db.Scalar("select count(*) from many where v = 'n'") + " mars " + tc.mars + " sessions " + (tc.Sessions > 1))
            db.Execute("create or alter procedure two @lim int as begin select id from many where id < @lim order by id; select count(*) from many; end")
            print("call " + db.Rows("exec two @lim = ?", [3]).Count + " then " + db.Scalar("select 8"))
            db.Close()
            // (the encryption: everything, only the login, nothing; a certificate that is not trusted; a wrong login)
            any modes = ["?encrypt=optional&trust=true", "?encrypt=off"]
            for (var i = 0; i < modes.length; i++) {
                string m = modes[i]
                try {
                    var e1 = new Data.Connection(MsDb.WithOptions(m), new Data.Credentials(MsDb.user, MsDb.password))
                    Data.TdsConnection t1 = e1.driver
                    print(m + ": " + e1.Scalar("select 1") + " [" + t1.Security + "]")
                    e1.Close()
                } catch (Data.ConnectionException e) { print(m + ": " + e.message) }
            }
            var e2 = new Data.Connection(MsDb.WithOptions("?trust=true"), new Data.Credentials(MsDb.user, MsDb.password))
            Data.TdsConnection t2 = e2.driver
            print("mandatory: " + (t2.Security.Length > 0))
            e2.Close()
            try { new Data.Connection(MsDb.WithOptions(""), new Data.Credentials(MsDb.user, MsDb.password)) } catch (Data.ConnectionException e) { print("untrusted " + e.Code) }
            try { new Data.Connection(MsDb.url, new Data.Credentials(MsDb.user, "wrong")) } catch (Data.ConnectionException e) { print("login " + e.Code) }
            try { new Data.Connection(MsDb.WithOptions("?encrypt=maybe")) } catch (ArgumentException e) { print("option " + e.message) }
            // (a named instance: the port from the SQL Server Browser)
            var udp = new Net.UdpSocket("127.0.0.1", 0)
            int browserPort = udp.Port
            var job = new BrowserJob(take udp, MsDb.PortOf())
            fire handover job { Browser.Serve(job) }
            var spare = new Net.UdpSocket("127.0.0.1", 0)
            int silentPort = spare.Port
            spare.Close()
            var named = new Data.Connection(MsDb.Named("fire", browserPort), new Data.Credentials(MsDb.user, MsDb.password))
            print("named instance " + named.Scalar("select 5"))
            named.Close()
            try { new Data.Connection(MsDb.Named("nosuch", browserPort), new Data.Credentials(MsDb.user, MsDb.password)) } catch (Data.ConnectionException e) { print("unknown instance " + e.Code) }
            try { new Data.Connection(MsDb.Named("x", silentPort) + "&timeout=600", new Data.Credentials(MsDb.user, MsDb.password)) } catch (Data.ConnectionException e) { print("no browser " + e.Code) }
            print("end")
            {{sorm}}
            """)).ToArray();
    }

    // The PostgreSQL driver against a running server - only when there is one: FIRE_TEST_POSTGRES=postgres://127.0.0.1:5432/test (an empty database: the
    // tables of its schema are dropped; one database per URL - the native runs are parallel), FIRE_TEST_POSTGRES_USER, FIRE_TEST_POSTGRES_PASSWORD;
    // FIRE_TEST_POSTGRES_LOGINS="md5user:pw plainuser:pw": more logins to try (MD5, the password as it is, a user only over TLS). The mapping, the queries,
    // the sessions and the relations are those of the case of the in-memory database (the names with capitals quoted: PostgreSQL folds the others).
    foreach (string pgUrl in (Environment.GetEnvironmentVariable("FIRE_TEST_POSTGRES") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
    {
        string Lit3(string? t) => t == null ? "undefined" : "\"" + t.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        string popen = $"PgDb.Open({Lit3(pgUrl)}, {Lit3(Environment.GetEnvironmentVariable("FIRE_TEST_POSTGRES_USER"))}, {Lit3(Environment.GetEnvironmentVariable("FIRE_TEST_POSTGRES_PASSWORD"))})";
        string logins = string.Join(", ", (Environment.GetEnvironmentVariable("FIRE_TEST_POSTGRES_LOGINS") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Lit3));
        string pmem = natCases.First(c => c.Name.StartsWith("Data/memory:", StringComparison.Ordinal)).Source;
        string pheader = pmem.Substring(0, pmem.IndexOf("print(\"memory\")", StringComparison.Ordinal)).Replace("#import \"data.memory\"", "#import \"data.postgres\"");
        string porm = pmem.Substring(pmem.IndexOf("var cdb = new Data.Connection(\"memory:\")", StringComparison.Ordinal))
            .Replace("new Data.Connection(\"memory:\")", "PgDb.Open(PgDb.url, PgDb.user, PgDb.password)")
            .Replace("cdb.Rows(\"select name, type, pk, notnull from _columns where table_name = 'people'\")", "cdb.Rows(\"select c.column_name as name, c.data_type as type, exists (select 1 from information_schema.key_column_usage k join information_schema.table_constraints t on t.constraint_name = k.constraint_name and t.constraint_type = 'PRIMARY KEY' where k.table_name = c.table_name and k.column_name = c.column_name) as pk, c.is_nullable = 'NO' as notnull from information_schema.columns c where c.table_schema = current_schema() and c.table_name = 'people' order by c.ordinal_position\")")
            .Replace("\"select name from _columns where table_name = ?\"", "\"select column_name from information_schema.columns where table_schema = current_schema() and table_name = ?\"")
            .Replace("create table Tag(", "create table \\\"Tag\\\"(")
            .Replace("from Revision", "from \\\"Revision\\\"")
            .Replace("from Pupil", "from \\\"Pupil\\\"")
            .Replace("from Memo", "from \\\"Memo\\\"")
            .Replace("on Memo (text)", "on \\\"Memo\\\" (text)")
            .Replace("from Label", "from \\\"Label\\\"")
            .Replace("ro.id, ro.clientId,", "ro.id, ro.\\\"clientId\\\",")
            .Replace("where clientId = ?", "where \\\"clientId\\\" = ?")
            .Replace("where postId = 1", "where \\\"postId\\\" = 1")
            .Replace("select labelId", "select \\\"labelId\\\"");
        natCases = natCases.Append(("Data/Postgres " + pgUrl + ": der Treiber in fire gegen einen laufenden Server (FIRE_TEST_POSTGRES): Startup, TLS (prefer, require, disable), SCRAM-SHA-256 (RFC 7677) und MD5, erweitertes Protokoll mit ? als $n (nicht in Texten, Namen, Kommentaren, Dollar-Quotes), Typen (Zahlen, Text, Unicode, NULL, bytea binaer, boolean, double, numeric, timestamp), mehrere Anweisungen, Fehlerarten mit SQLSTATE, Transaktionen mit Savepoints, lastval hinter einem Savepoint, Streaming (geparkt bei Befehlen dazwischen, verschachtelte Leser, vorzeitig verlassen); dann Mapping, Abfragen, Sessions und Beziehungen wie bei der In-Memory-Datenbank", pheader + $$"""
            class PgDb {
                static string url
                static string user
                static string password
                // (a connection to the test database without its tables)
                static Data.Connection Open(string url, string user, string password) {
                    PgDb.url = url
                    PgDb.user = user
                    PgDb.password = password
                    var c = new Data.Connection(url, new Data.Credentials(user, password))
                    var names = c.Rows("select table_name from information_schema.tables where table_schema = current_schema() and table_type = 'BASE TABLE'")
                    foreach (n in names) { c.Execute("DROP TABLE IF EXISTS \"" + n[0] + "\" CASCADE") }
                    return c
                }
            }
            print("postgres")
            var db = {{popen}}
            db.Execute("create table person(id bigint generated by default as identity primary key, name varchar(100) not null, age int, active boolean); create table item(id bigserial primary key, title text, price double precision, blob1 bytea, f real, d timestamp(3), n bigint, s smallint, m numeric(10,2), j jsonb)")
            print("insert " + db.Execute("insert into person(name, age, active) values (?, ?, ?)", ["Ada", 36, true]))
            db.Execute("insert into person(name, age, active) values (?, ?, ?)", ["Grace", 85, false])
            db.Execute("insert into person(name, age, active) values (?, ?, ?)", ["Linus Ünïcødé 🐧", undefined, true])
            print("last id " + db.LastInsertId)
            foreach (row in db.Query("select id, name, age, active from person where age > ? or age is null order by id", [40])) { print("row " + row) }
            print("count " + db.Scalar("select count(*) from person") + " text " + db.Scalar("select name from person where id = 3") + " truth " + db.Scalar("select count(*) from person where active = ?", [true]))
            db.Execute("insert into item(title, price, blob1, f, d, n, s, m, j) values (?, ?, ?, ?, ?, ?, ?, ?, ?)", ["Box", 9.5, [1, 2, 0, 255], 1.25, "2026-11-13 10:20:30.250", 18000000000, -5, "12.50", "{\"a\": [1, 2]}"])
            db.Execute("insert into item(title, price) values (?, ?)", ["Tiny", -0.000123])
            foreach (row in db.Rows("select title, price, f, d, n, s, m, length(blob1), j from item order by id")) { print("item " + row) }
            byte blob[] = db.Scalar("select blob1 from item where id = 1")
            print("blob " + blob.length + " " + blob[2] + " " + blob[3])
            print("update " + db.Execute("update person set age = age + 1 where age is not null") + " delete " + db.Execute("delete from item where id = ?", [2]))
            print("marks " + db.Scalar("select 'a?b' || ? || $$c?d$$ /* ? */ -- ?\n", ["!"]) + " " + db.Scalar("select \"title?\" from (select title as \"title?\" from item where id = ?) x", [1]))
            print("multi")
            db.Execute("insert into item(title) values ('a'); insert into item(title) values ('b'); select 1; update item set price = 1 where price is null")
            print("items " + db.Scalar("select count(*) from item") + " priced " + db.Scalar("select count(*) from item where price = 1"))
            db.Execute("DO $$ BEGIN INSERT INTO item(title) VALUES ('from do'); END $$")
            print("do " + db.Scalar("select count(*) from item where title = 'from do'"))
            try { db.Execute("insert into person(id, name) values (?, ?)", [1, "Dup"]) } catch (Data.ConstraintException e) { print("constraint " + e.Code + " " + e.State) }
            try { db.Execute("insert into person(age) values (?)", [1]) } catch (Data.ConstraintException e) { print("not null " + e.State) }
            try { db.Execute("select * from nothing") } catch (Data.DataException e) { print("error " + e.State + " " + e.Code) }
            try { db.Execute("selec 1") } catch (Data.DataException e) { print("syntax " + e.State) }
            try { db.Execute("insert into person(name) values (?)", []) } catch (ArgumentException e) { print("args " + e.message) }
            try { db.Execute("insert into person(name, age) values (?, ?)", ["x", "no number"]) } catch (Data.DataException e) { print("bad value " + e.State) }
            print("still " + db.Scalar("select count(*) from person"))
            db.Transaction(t => { t.Execute("update person set age = age + 1 where id = ?", [1]) })
            print("after commit " + db.Scalar("select age from person where id = 1"))
            try {
                db.Transaction(t => {
                    t.Execute("update person set age = 0 where id = ?", [1])
                    throw new Exception("stop")
                })
            } catch (e) { print("rolled back: " + e.message) }
            print("after rollback " + db.Scalar("select age from person where id = 1"))
            db.Begin()
            db.Execute("delete from person where id = ?", [3])
            db.Begin()
            db.Execute("delete from person where id = ?", [2])
            db.Rollback()
            db.Commit()
            print("nested " + db.Scalar("select count(*) from person"))
            db.Begin()
            db.Execute("create table nokey(v int)")
            db.Execute("insert into nokey values (1)")
            print("lastval without a sequence " + db.LastInsertId + " then " + db.Scalar("select count(*) from nokey"))
            db.Commit()
            print("case " + db.From<Pupil>().Where(p => p.name.StartsWith("a")).ToSql())
            db.Close()
            var tls = new Data.Connection(PgDb.url + "?sslmode=require", new Data.Credentials(PgDb.user, PgDb.password))
            print("tls " + tls.Scalar("select ssl from pg_stat_ssl where pid = pg_backend_pid()"))
            tls.Close()
            var plain = new Data.Connection(PgDb.url + "?sslmode=disable", new Data.Credentials(PgDb.user, PgDb.password))
            print("plain " + plain.Scalar("select ssl from pg_stat_ssl where pid = pg_backend_pid()"))
            plain.Close()
            try { new Data.Connection(PgDb.url, new Data.Credentials(PgDb.user, "wrong")) } catch (Data.ConnectionException e) { print("login " + e.State) }
            try { new Data.Connection(PgDb.url + "?sslmode=maybe") } catch (ArgumentException e) { print("option " + e.message) }
            any scram = Data.PostgresAuth.ScramFinal("pencil", "n=user,r=rOprNGfwEbeRWgbNEkqO", "r=rOprNGfwEbeRWgbNEkqO%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0,s=W22ZaJ0SNY7soEsUEjb6gQ==,i=4096")
            print("scram " + (scram[0] == "c=biws,r=rOprNGfwEbeRWgbNEkqO%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0,p=dHzbZapWIk4jUhN+Ute9ytag9zjfMHgsqmmiz7AndVQ=") + " " + (scram[1] == "6rriTRBi23WpRR/wtup+mMhUZUn/dB5nLTJRsjl95G4="))
            byte salt[] = new byte[4]
            for (var i = 0; i < 4; i++) { salt[i] = i + 1 }
            print("md5 " + (Data.PostgresAuth.Md5("app", "secret", salt) == "md5911f527656472583a006e7727877b33e"))
            foreach (login in [{{logins}}]) {
                string text = login
                int colon = text.IndexOf(":")
                var other = new Data.Connection(PgDb.url, new Data.Credentials(text.Substring(0, colon), text.Substring(colon + 1)))
                print("login as " + (other.Scalar("select current_user") == text.Substring(0, colon)))
                other.Close()
            }
            // (streaming: rows come from the socket one by one; a command in between parks the rest; a reader left early; two readers at once)
            var sdb0 = new Data.Connection(PgDb.url, new Data.Credentials(PgDb.user, PgDb.password))
            sdb0.Execute("create table many(id int primary key, v varchar(20))")
            sdb0.Begin()
            for (var i = 0; i < 300; i++) { sdb0.Execute("insert into many values (?, ?)", [i, "v" + i]) }
            sdb0.Commit()
            int seen = 0
            int sum = 0
            foreach (row in sdb0.Query("select id, v from many order by id")) {
                seen++
                int id = row.Int("id")
                sum = sum + id
                if (id % 100 == 0) { sdb0.Execute("update many set v = ? where id = ?", ["changed", id + 1]) }
            }
            print("streamed " + seen + " sum " + sum + " changed " + sdb0.Scalar("select count(*) from many where v = 'changed'"))
            int early = 0
            foreach (row in sdb0.Query("select id from many order by id")) {
                early++
                if (early == 5) { break }
            }
            print("early " + early + " then " + sdb0.Scalar("select count(*) from many") + " " + sdb0.Rows("select v from many where id = 1")[0][0])
            var outer = sdb0.Query("select id from many where id < 3 order by id")
            var inner = sdb0.Query("select id from many where id >= 297 order by id")
            var both = ""
            foreach (row in outer) {
                both = both + " " + row[0]
                foreach (r2 in inner) { both = both + "/" + r2[0] }
            }
            print("nested readers" + both)
            try {
                foreach (row in sdb0.Query("select 1 / (id - 150) from many order by id")) { }
            } catch (Data.DataException e) { print("error in the rows " + e.State + " then " + sdb0.Scalar("select 9")) }
            sdb0.Close()
            print("end")
            {{porm}}
            """)).ToArray();
    }

    // A data model (.fdata, docs/data/designer.md): the code forge generates from it - entities with the attributes of the mapping, an enum, a record as JSON,
    // relations on both sides, Model and Context - used on SQLite and the in-memory database
    {
        string shopModel = """
            {
              "format": 1,
              "namespace": "Shop",
              "naming": { "tables": "snake_plural", "columns": "snake" },
              "enums": [ { "name": "Level", "values": ["Low", "Mid", "High"], "comment": "How important a customer is" } ],
              "types": [ { "name": "Address", "fields": [ { "name": "city", "type": "string" }, { "name": "zipCode", "type": "string" } ] } ],
              "entities": [
                { "name": "Customer", "set": "Customers", "comment": "Someone who buys",
                  "fields": [
                    { "name": "id", "type": "int", "key": true },
                    { "name": "fullName", "type": "string", "length": 100, "required": true },
                    { "name": "email", "type": "string", "length": 200, "unique": true },
                    { "name": "level", "type": "Level" },
                    { "name": "born", "type": "DateTime" },
                    { "name": "address", "type": "Address", "json": true },
                    { "name": "version", "type": "int", "version": true }
                  ] },
                { "name": "Purchase", "fields": [ { "name": "id", "type": "int", "key": true }, { "name": "total", "type": "float", "required": true }, { "name": "note", "type": "string", "comment": "free text" } ] },
                { "name": "Category", "table": "categories", "fields": [ { "name": "code", "type": "string", "key": true, "length": 20 }, { "name": "label", "type": "string" }, { "name": "picture", "type": "bytes" } ] }
              ],
              "relations": [
                { "kind": "one-to-many", "from": "Customer", "to": "Purchase", "navigation": "purchases", "inverse": "customer" },
                { "kind": "many-to-many", "from": "Purchase", "to": "Category", "navigation": "categories", "inverse": "purchases", "joinTable": "purchase_category" }
              ],
              "diagrams": [ { "name": "Main", "shapes": { "Customer": { "x": 40, "y": 40 }, "Purchase": { "x": 320, "y": 40 } } } ]
            }
            """;
        natCases = natCases.Append(("Data/Model: ein Datenmodell (.fdata) als Code - Entitaeten mit Attributen, Enum, Record als JSON, one-to-many und many-to-many auf beiden Seiten, Benennung snake_plural/snake, Model und Context (Ensure, Script, Abfragen, Session) - SQLite und In-Memory",
            fire.UI.Markup.MarkupFiles.GenerateScript("Shop.fdata", shopModel) + """
            #import "data.sqlite"
            #import "data.memory"
            class App {
                static undefined Run(string url) {
                    print("== " + url)
                    var db = new Data.Connection(url)
                    var ctx = new Shop.Context(db)
                    print(ctx.Script())
                    ctx.Ensure()
                    var s = ctx.Session()
                    var ada = new Shop.Customer()
                    ada.fullName = "Ada"
                    ada.email = "ada@x.org"
                    ada.level = Shop.Level.High
                    ada.address = new Shop.Address("London", "W1")
                    var p = new Shop.Purchase()
                    p.total = 12.5
                    p.customer = ada
                    var c = new Shop.Category()
                    c.code = "books"
                    c.label = "Books"
                    p.categories = new List<Shop.Category>()
                    p.categories.Add(c)
                    s.Add(ada)
                    s.Add(p)
                    print("saved " + s.Save())
                    Shop.Customer back = ctx.Customers.Where(x => x.fullName == "Ada").Include(x => x.purchases).First()
                    print(back.fullName + " " + back.level + " " + back.address.city + " " + back.address.zipCode + " purchases " + back.purchases.Count + " version " + back.version)
                    print("raw " + db.Scalar("select full_name from customers") + " " + db.Scalar("select customer_id from purchases"))
                    int who = back.id
                    print("books " + ctx.Purchases.Where(x => x.categories.Any(k => k.code == "books")).Count() + " by " + ctx.Purchases.Where(x => x.customerId == taking who).Count() + " via " + ctx.Purchases.Where(x => x.customer.fullName == "Ada").Count())
                    print("model " + Shop.Model.Types().length + " " + Shop.Model.Tables()[0] + " " + Shop.Model.Types()[2])
                    db.Close()
                }
            }
            App.Run("sqlite::memory:")
            App.Run("memory:")
            """)).ToArray();
    }

    // Time and Sleep (bridges/fire_bridge_time.hpp)

    // Resources: `new Resource("path")` is read when the program is compiled and travels with it (VM, packed file, native binary)
    {
        string resDir = Path.Combine(Path.GetTempPath(), "fire-resource-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(resDir);
        File.WriteAllText(Path.Combine(resDir, "hello.txt"), "Gr\u00fc\u00dfe, Welt!\n", new System.Text.UTF8Encoding(false));
        File.WriteAllBytes(Path.Combine(resDir, "data.bin"), new byte[] { 0, 1, 2, 250, 255 });
        string rd = resDir.Replace('\\', '/');
        string resScript = $$"""
            var text = new Resource("{{rd}}/hello.txt")
            print(text.Name().EndsWith("hello.txt"))
            print(text.Length())
            print(text.Text().Length)
            print(text.Text().Substring(0, 5))
            var again = new Resource("{{rd}}/hello.txt")
            print(again.id == text.id)
            var bin = new Resource("{{rd}}/data.bin")
            var bytes = bin.Bytes()
            print(bytes.length)
            print(bytes[3])
            // new Resource("{{rd}}/missing.bin")   <- in a comment: not embedded
            print("new Resource(\"{{rd}}/missing.bin\")".Length > 0)
            """;
        string[] resExpected = { "True", "15", "13", "Gr\u00fc\u00dfe", "True", "5", "250", "True" };
        string resOutput = vmOutput(resScript);
        CheckNat("Ressourcen: new Resource(\"pfad\") in der VM (Text UTF-8, Bytes, dieselbe Datei einmal, Kommentar und String bleiben)", resOutput == string.Concat(resExpected.Select(l => l + "\n")), "  erhalten:\n" + resOutput);
        // the program carries the bytes: a packed file (serialized program) and a program without the file at run time
        {
            var linked = new Linker().CompileAndLink(new[] { resScript });
            var back = fire.Bytecode.ProgramFile.Read(fire.Bytecode.ProgramFile.Write(linked));
            CheckNat("Ressourcen: das serialisierte Programm traegt die Dateien (2 Ressourcen, Bytes gleich)",
                back.Program.Resources.Count == 2 && back.Program.Resources[1].Data.SequenceEqual(new byte[] { 0, 1, 2, 250, 255 }) && back.Program.Resources[0].Name.EndsWith("hello.txt"), "");
        }
        // a missing file is a compile error with the line
        {
            string message = "";
            try { new Linker().CompileAndLink(new[] { "var a = 1\nvar r = new Resource(\"" + rd + "/gibt-es-nicht.png\")\n" }); } catch (Exception ex) { message = ex.Message; }
            CheckNat("Ressourcen: eine fehlende Datei ist ein Uebersetzungsfehler mit Zeile", message.Contains("not found") && message.Contains("line 2"), message);
        }
        natCases = natCases.Append(("Ressourcen: new Resource(\"pfad\") ist eingebettet", resScript)).ToArray();
    }

    // Fonts in the graphics library (Fonts, Font, Renderer.DrawText with a font, TextWidth/TextHeight/TextAscent): the VM and the native program must draw the same pixels
    {
        string fontResDir = Path.Combine(Path.GetTempPath(), "fire-font-res-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fontResDir);
        File.WriteAllBytes(Path.Combine(fontResDir, "tester.ttf"), fireTestFont);
        File.WriteAllText(Path.Combine(fontResDir, "notafont.txt"), "this is text, not a font");
        string frd = fontResDir.Replace('\\', '/');
        string fontScript = $$"""
            #import "graphics"
            class H { static int Of(b) { var bytes = b.ReadBytes(); var h = 17; for (var i = 0; i < bytes.length; i++) { h = (h * 31 + bytes[i]) % 1000000007 } return h } }
            var f = Fonts.Add(new Resource("{{frd}}/tester.ttf"), "tester")
            print(f.Name() + " " + f.IsBitmap())
            print(Fonts.Get("TESTER").Name() + " " + Fonts.Get("fire test").Name() + " " + Fonts.Get("FireSans").Name())
            print(Fonts.Get("8x8").Name() + " " + Fonts.Get("8x14").IsBitmap() + " " + Fonts.Get("14x8").Name() + " " + Fonts.Get("").Name() + " " + Fonts.Get("no such font").Name())
            print(Fonts.Has("tester") + " " + Fonts.Has("firesans") + " " + Fonts.Has("8x8") + " " + Fonts.Has("zzz"))
            var fb = new Framebuffer(130, 70)
            var r = new Renderer(fb)
            var white = new SolidBrush(0xFFFFFFFF)
            var ghost = new SolidBrush(0x80FFFFFF)
            var blue = new SolidBrush(0xFFFF0000)
            print(r.TextWidth("AVAVHO iAi.Á", f, 20) + " " + r.TextHeight(f, 20) + " " + r.TextAscent(f, 20) + " " + r.TextWidth("AVA", f, 10) + " " + r.TextHeight(f, 10))
            print(r.TextWidth("AVA", undefined, 20) + " " + r.TextHeight() + " " + r.TextWidth("AVA", Fonts.Get("8x8")) + " " + r.TextHeight(Fonts.Get("8x8")) + " " + r.TextAscent(Fonts.Get("8x8")))
            print(r.TextWidth("AVA", f, 0) + " " + r.TextWidth("AVA", f, -5) + " " + r.TextWidth("AVA", f, 14) + " " + r.TextHeight(f, 100000) + " " + r.TextHeight(f, 512))
            // 1: white on black
            r.DrawText(2, 2, "AVAVHO iAi.Á", white, undefined, f, 20)
            print(H.Of(fb))
            // 2: translucent over a background, with a background brush for the line
            r.ClearTo(0xFF804020)
            r.DrawText(3, 4, "HOAV iA", ghost, blue, f, 18)
            r.DrawText(3, 30, "HOAV iA", white, ghost, f, 11)
            print(H.Of(fb))
            // 3: without blending
            r.AlphaBlending = false
            r.DrawText(1, 40, "AVA", ghost, undefined, f, 24)
            r.AlphaBlending = true
            print(H.Of(fb))
            // 4: clipped, partly outside, odd sizes
            r.ClearTo(0xFF101010)
            r.SetClip(10, 10, 60, 30)
            r.DrawText(0, 0, "AVAVHO iAi.", white, undefined, f, 30)
            r.ResetClip()
            r.DrawText(-6, 50, "HOAV", white, undefined, f, 16)
            r.DrawText(120, 60, "HOAV", white, undefined, f, 16)
            r.DrawText(5, 5, "HO", white, undefined, f, 1)
            r.DrawText(40, 5, "HO", white, undefined, f, 400)
            print(H.Of(fb))
            // 5: bitmap fonts of the library next to the font of the renderer
            r.ClearTo(0xFF000000)
            r.DrawText(2, 2, "console", white)
            r.DrawText(2, 18, "eight by eight", white, blue, Fonts.Get("8x8"))
            r.DrawText(2, 30, "8x14 é中", ghost, blue, Fonts.Get("8x14"), 99)
            r.DrawText(2, 48, "console font", white, undefined, Fonts.Console(), 30)
            print(H.Of(fb))
            // 6: a palette framebuffer: from alpha 128 up a pixel is drawn
            var pfb = new Framebuffer(130, 40, ColorMode.Palette)
            var pr = new Renderer(pfb)
            pr.DrawText(2, 2, "AVAVHO iAi.", new SolidBrush(7), undefined, f, 20)
            pr.DrawText(2, 22, "HOAV", new SolidBrush(0xFFFF0000), new SolidBrush(2), f, 14)
            print(H.Of(pfb))
            // 7: the same font from bytes, a font that is not one, a released font
            var g = Fonts.FromBytes(new Resource("{{frd}}/tester.ttf").Bytes())
            r.ClearTo(0xFF000000)
            r.DrawText(2, 2, "AVAVHO iAi.Á", white, undefined, g, 20)
            print(g.Name() + " " + g.IsBitmap() + " " + (g.id != f.id))
            g.Release()
            try {
                Fonts.Add(new Resource("{{frd}}/notafont.txt"))
                print("no error")
            } catch (GraphicsException e) {
                print("error " + e.message)
            }
            try { Fonts.FromBytes(new byte[3]) } catch (GraphicsException e) { print("error " + e.message) }
            """;
        string fontOut = vmOutput(fontScript);
        string[] fontLines = fontOut.TrimEnd('\n').Split('\n');
        CheckNat("Schriften: Fonts.Add/Get/Has (Alias, Familie, Systemfont, eingebaute, unbekannt = Konsolenschrift), Masse, Palette, Fehler",
            fontLines.Length == 16 && fontLines[0] == "Fire Test False" && fontLines[1] == "Fire Test Fire Test Fire Test" && fontLines[2] == "8x8 True 8x14 console console"
            && fontLines[3] == "True True True False" && fontLines[4] == "128 22 16 19 11" && fontLines[5] == "24 14 24 8 8" && fontLines[6] == "26 26 26 563 563"
            && string.Join(",", fontLines.Skip(7).Take(6)) == "289788651,962691604,823710043,77448282,232389351,870473533"
            && fontLines[13] == "Fire Test False True" && fontLines[14] == "error The file '" + frd + "/notafont.txt' is not a TrueType font." && fontLines[15] == "error The data is not a TrueType font.",
            "  erhalten:\n" + fontOut);
        CheckNat("Schriften: dieselben Pixel bei jedem Aufruf (die Glyphen werden zwischengespeichert)", vmOutput(fontScript) == fontOut, "");
        natCases = natCases.Append(("Schriften: TrueType-Text, eingebaute Schriften, Systemschrift, Palette, Clip - nativ == VM", fontScript)).ToArray();
    }


    string? cxx = FindCxx();
    if (cxx == null)
        Console.WriteLine("(kein C++-Compiler gefunden - die Native-Backend-Pruefungen werden uebersprungen)");
    else
    {
        string workDir = Path.Combine(Path.GetTempPath(), "fire-native-test-" + Guid.NewGuid().ToString("N"));
        fire.Native.NativeRuntimeFiles.WriteTo(workDir);
        fire.Native.NativeRuntimeFiles.WriteSimulatorTo(Path.Combine(workDir, "sim"));

        // Memory errors in the generated code (ownership, freeing) must not slip through: run the cases under the sanitizers when the compiler has them.
        string sanitize = "";
        // (FIRE_TEST_SANITIZE=0: without them - the code paths the runtime keeps for the sanitizers' own checks, e.g. the pool of small objects, run then)
        if (Environment.GetEnvironmentVariable("FIRE_TEST_SANITIZE") != "0")
        {
            string probeFile = Path.Combine(workDir, "probe.cpp");
            File.WriteAllText(probeFile, "int main() { return 0; }\n");
            using var probe = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(cxx, $"-fsanitize=address,undefined \"{probeFile}\" -o \"{probeFile}.bin\"") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false })!;
            probe.StandardError.ReadToEnd(); probe.StandardOutput.ReadToEnd(); probe.WaitForExit();
            if (probe.ExitCode == 0 && System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(probeFile + ".bin") { UseShellExecute = false, RedirectStandardError = true }) is { } run) { run.StandardError.ReadToEnd(); run.WaitForExit(); if (run.ExitCode == 0) sanitize = "-fsanitize=address,undefined -fno-sanitize-recover=undefined "; }
        }
        Console.WriteLine(sanitize.Length > 0 ? "(die Faelle laufen unter AddressSanitizer/UBSan)" : "(Sanitizer nicht verfuegbar)");
        // the key of a check that is code here (FIRE_TESTS_RESUME, TestResume.cs): its title, the build of the test program (in TestResume.Global) and the sanitizers
        string NatUnitKey(string title) => TestResume.Active ? TestResume.Key(title, sanitize) : "";

        // a call through an interface (members public, E4063): its own dispatcher over the implementing classes - no caller class, no access check, not the
        // class outside the interface that has the name privately
        // `word` in the width of the target (SPEC 8.10): 16 and 32 bits wrap in the VM in C as in the native build for that target (and as the C# VM did: Value.WordBits)
        const string wordWidthTitle = "word in der Breite des Ziels (16, 32 Bit): Ueberlauf und Umbruch in der C-VM wie nativ";
        if (TestSelected(wordWidthTitle))
        {
            const string wordScript = "word a = 65535\na = a + 1\nword b = 0\nb = b - 1\nword c = 4000000000\nc = c * 2\nword s = 1\ns = s << 20\nword h = 4294967295\nh = h + 3\n"
                + "word d = 0\nd = d - 1\nd = d / 3\nprint(Word.Bits + \" \" + a + \" \" + b + \" \" + c + \" \" + s + \" \" + h + \" \" + d + \" \" + (b > 70000))";   // (70000 as a word of 16 bits is 4464)
            var detail = new System.Text.StringBuilder();
            foreach (var (bits, want) in new[] { (16, "16 0 65535 20480 0 2 21845 True\n"), (32, "32 65536 4294967295 3705032704 1048576 2 1431655765 True\n"), (64, "64 65536 18446744073709551615 8000000000 1048576 4294967298 6148914691236517205 True\n") })
            {
                var linked = new Linker().CompileAndLink(new[] { wordScript }, null, null, VmExecutionMode.Release, null, TargetProfile.Host with { WordWidth = bits });
                var vmRun = CVmTest.Run(linked);
                string vm = string.Concat(vmRun.Printed.Select(l => l + "\n"));
                string exe = Path.Combine(workDir, $"word{bits}.bin");
                string build = NativeTestBuild.Build(NativeTestBuild.In(workDir), cxx, fire.Native.CppGenerator.GenerateSources(linked), workDir, $"word{bits}", $"-pthread {NativeTestBuild.Opt} -Wall -Wextra {sanitize}", exe, out int wordExit);
                string nat = wordExit == 0 ? TestProcess.Run(exe, "", workDir).Stdout : "C/C++-Compiler: " + build;
                if (vm != want || nat != want) detail.AppendLine($"{bits}: erwartet {want.TrimEnd()}, C-VM {vm.TrimEnd()} {vmRun.Errors.Trim()}, nativ {nat.TrimEnd()}");
            }
            CheckNat(wordWidthTitle, detail.Length == 0, detail.ToString());
        }

        const string interfaceCallTitle = "Interface-Aufrufe: eigener Verteiler ohne Aufruferklasse und Zugriffspruefung (fremde private Methode gleichen Namens)";
        if (TestSelected(interfaceCallTitle))
        {
            string icpp = fire.Native.CppGenerator.GenerateSources(new Linker().CompileAndLink(new[] { """
                interface IShape { int Area(int scale) }
                class Square : IShape { int Area(int scale) { return 4 * scale } }
                class Disc : IShape { int Area(int scale) { return 3 * scale } }
                class Vault { private int Area(int scale) { return 1000 } int Open() { return this.Area(1) } }
                class Report { static int Use(IShape s) { return s.Area(2) } }
                print(Report.Use(new Square()) + Report.Use(new Disc()) + new Vault().Open())
                """ }, null, null, VmExecutionMode.Release)).Program;
            int start = icpp.IndexOf("static Value icall_IShape_Area_1(Value self, Value a0)\n{", StringComparison.Ordinal);
            string body = start < 0 ? "" : icpp.Substring(start, icpp.IndexOf("\n}\n", start, StringComparison.Ordinal) - start);
            CheckNat(interfaceCallTitle, body.Length > 0 && !body.Contains("accessDenied") && !body.Contains("caller") && body.Split("return f").Length == 3
                && System.Text.RegularExpressions.Regex.IsMatch(icpp, @"= icall_IShape_Area_1\(s\d+, s\d+\);"), body.Length > 0 ? body : icpp);
        }

        // FIRE_TESTS_LISTING=<file>: the names and sources of all cases "native == VM" as one text, nothing runs (to compare the cases before and after they move)
        if (Environment.GetEnvironmentVariable("FIRE_TESTS_LISTING") is { Length: > 0 } listingFile) { File.WriteAllText(listingFile, TestCases.Listing(natCases)); Console.WriteLine($"{natCases.Length} cases"); return; }
        // (the thread cases for the run under ThreadSanitizer below - before the filter: that test has a title of its own)
        var natThreadCases = natCases.Where(c => c.Name.StartsWith("Threads:", StringComparison.Ordinal) || c.Name.StartsWith("[NoCopy] Threads:", StringComparison.Ordinal)).ToArray();
        natCases = natCases.Where(c => TestSelected(c.Name)).ToArray();
        // FIRE_TESTS_RESUME / FIRE_TESTS_SHARD (TestResume.cs): every case is a unit "Native == VM: <title>", keyed by its source and expected files; a case of another
        // part or one that passed with the same key is not built (its VM run stays: the parts below take their cases and expected outputs from here)
        var natUnits = new (string Unit, string Key, TestResume.Decision Decision)[natCases.Length];
        {
            var caseFiles = new Dictionary<string, TestCases.Case>();
            foreach (var f in TestCases.Load("native")) caseFiles.TryAdd(f.Name, f);
            for (int i = 0; i < natCases.Length; i++)
            {
                caseFiles.TryGetValue(natCases[i].Name, out var f);
                string unit = "Native == VM: " + natCases[i].Name;
                string key = TestResume.Active ? TestResume.Key(natCases[i].Source, f?.Source == natCases[i].Source ? f.Id : null, f?.Expected, f?.Unhandled, f?.Varies.ToString(), sanitize) : "";
                natUnits[i] = (unit, key, TestResume.Decide(unit, key));
            }
        }
        bool NatSkipped(int i) => natUnits[i].Decision != TestResume.Decision.Run;
        // First the VM runs the cases here, one after the other and alone, as before: the precision of float (Value.SingleFloats) and the handlers of
        // `terminate` are process-wide while a program runs (the compiler must not make its constants while a VM with 32-bit floats runs), and some thread
        // cases of the VM depend on timing that a machine busy with compilers upsets. Then the C of every case is generated (in parallel, in this process) and
        // handed to the workers that build and run it (only processes: FIRE_TEST_JOBS at a time, the biggest program first - a case with the ui library
        // compiles for minutes and must not start last); the outputs are compared in the order of the cases (the output is the same as one case after the other).
        var vmResults = new string[natCases.Length];
        int vmAt = 0;
        using (TestProgress.Heartbeat("VM (Faelle nativ == VM)", () => Volatile.Read(ref vmAt), natCases.Length, () => natCases[Math.Min(Volatile.Read(ref vmAt), natCases.Length - 1)].Name))
            for (int i = 0; i < natCases.Length; i++) { vmResults[i] = vmOutput(natCases[i].Source); Value.SingleFloats = false; Value.Ints32 = false; Value.Ints16 = false; Volatile.Write(ref vmAt, i + 1); }
        Value.SingleFloats = false;
        // The cases that are files have their expected output next to them (tests/cases/native/<case>.expected, docs/tools/testing.md): what the VM prints is that text - on
        // every machine and for every VM that will run these files. FIRE_TESTS_UPDATE=1 records what the VM prints now instead (for new and changed cases) and ends.
        {
            var files = new Dictionary<string, TestCases.Case>();
            foreach (var f in TestCases.Load("native")) files.TryAdd(f.Name, f);
            bool update = Environment.GetEnvironmentVariable("FIRE_TESTS_UPDATE") == "1";
            int recorded = 0;
            for (int i = 0; i < natCases.Length; i++)
            {
                if (!files.TryGetValue(natCases[i].Name, out var file) || file.Source != natCases[i].Source || file.Varies) continue;
                string? unhandled = vmUnhandled.GetValueOrDefault(natCases[i].Source);
                if (update)
                {
                    // (a case the compiler refuses has nothing to record: it stays a failed check until the case or the compiler is right again)
                    if (vmResults[i].StartsWith(NotCompiled, StringComparison.Ordinal)) Console.WriteLine($"FEHLER: {natCases[i].Name} ({file.Id}.fire)\n  {vmResults[i].TrimEnd().Replace("\n", "\n  ")}");
                    else if (file.Expected != vmResults[i] || file.Unhandled != unhandled) { TestCases.Record(file, vmResults[i], unhandled); recorded++; }
                    continue;
                }
                string title = $"VM: {natCases[i].Name}: die erwartete Ausgabe ({file.Id}.expected)";
                if (NatSkipped(i)) continue;
                using var unitScope = TestResume.Unit(natUnits[i].Unit, natUnits[i].Key);
                if (file.Expected == null) CheckNat(title, false, "  Der Fall hat noch keine erwartete Ausgabe: einmal mit FIRE_TESTS_UPDATE=1 laufen lassen (FIRE_TESTS_ONLY=native), die Datei pruefen und mit dem Fall einchecken.");
                else if (file.Expected != vmResults[i]) CheckNat(title, false, TestCases.Difference(file.Expected, vmResults[i]));
                else CheckNat(title, file.Unhandled == unhandled, $"  erwartet am Ende: {file.Unhandled ?? "(keine Ausnahme)"}\n  erhalten: {unhandled ?? "(keine Ausnahme)"}");
            }
            if (update) { Console.WriteLine($"FIRE_TESTS_UPDATE: {recorded} erwartete Ausgabe(n) neu geschrieben (tests/cases/native)."); return; }
        }
        // what the native program of a case did: its output, stderr and exit code (Output null: it did not build - Stderr says why)
        var natRan = new (string? Output, string Stderr, int Exit)[natCases.Length];
        var natRanReady = new ManualResetEventSlim[natCases.Length];
        for (int i = 0; i < natCases.Length; i++) natRanReady[i] = new ManualResetEventSlim(false);
        int natRanCount = 0;
        // (generated first: the cases with big libraries - their C is the biggest)
        string[] natHeavyImports = { "\"ui\"", "\"circuits.ui\"", "\"ui.docking\"", "\"dialogs\"", "\"scene3d\"", "\"circuits\"" };
        var natOrder = Enumerable.Range(0, natCases.Length).OrderByDescending(i => natHeavyImports.Count(h => natCases[i].Source.Contains("#import " + h))).ThenByDescending(i => natCases[i].Source.Length).ToList();
        // the verdict of a case: what the VM printed against what the program did (an "Abbruch:" case also compares the line on stderr, ...)
        (bool Ok, string Detail) NatVerdict(int index)
        {
            var c = natCases[index];
            var (stdout, stderr, exit) = natRan[index];
            string expected = vmResults[index], actual;
            if (stdout == null) actual = stderr;
            else if (c.Name.StartsWith("Abbruch:"))
            {
                // an exception that nothing catches: the program ends with exit code 1 after the output so far and writes the text of the VM's command line runner to
                // stderr ("Unhandled exception of type 'Class': message")
                // (with its stack trace: the native build records the same frames as the VM, docs/spec/STACKTRACE.md)
                expected += "stderr: " + vmUnhandled.GetValueOrDefault(c.Source, "(the VM did not stop with an exception)");
                actual = exit == 1 ? stdout + "stderr: " + stderr.TrimEnd('\n', '\r') : $"Exitcode {exit}: {stdout}{stderr}";
            }
            // (the end of the program ended a thread: the line on stderr is checked on its own - the VM writes it to the console, not to the output)
            else if (c.Name.Contains("(Meldung auf stderr)"))
                actual = exit == 0 && stderr.Contains("ended like with terminate") ? stdout : $"Exitcode {exit}: {stdout}stderr: {stderr}";
            // (Http.Pages without OnError: the errors of the pages are lines on stderr - checked on their own, the VM writes them to the console)
            else if (c.Name.Contains("(Http.Pages-Zeile auf stderr)"))
                actual = exit == 0 && stderr.Contains("Http.Pages: page 'BadPage' (/bad), circuit ") && stderr.Contains(": a handler of the page threw Boom: bang 1") && stderr.Contains(": a handler of the page threw ArgumentException: ") && stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 2
                    ? stdout : $"Exitcode {exit}: {stdout}stderr: {stderr}";
            // `terminate(n)` with a whole number is the exit code of the process (like the command line runner)
            else actual = exit == 0 || (c.Name.StartsWith("Threads:") && c.Source.Contains("terminate(")) ? stdout + stderr : $"Exitcode {exit}: {stdout}{stderr}";
            return (expected == actual, $"  erwartet (VM):\n{expected}\n  erhalten (C++):\n{actual}");
        }
        var natVerdicts = new (bool Ok, string Detail)[natCases.Length];
        var natPipeline = TestJobs.StartPipeline(natOrder, index =>
        {
            if (NatSkipped(index)) return (Sources: (fire.Native.NativeSources?)null, Error: (string?)"");
            // (with the file of the case, as the VM ran it: the stack traces name it)
            try { return (Sources: (fire.Native.NativeSources?)fire.Native.CppGenerator.GenerateSources(new Linker { SourcePaths = vmCaseFiles.TryGetValue(natCases[index].Source, out var natFile) ? new string?[] { natFile } : null }.CompileAndLink(new[] { natCases[index].Source }, null, null, VmExecutionMode.Release)), Error: (string?)null); }
            catch (fire.Native.NativeNotSupportedException ex) { return (Sources: null, Error: $"nicht uebersetzbar: {ex.Message}"); }
            catch (Exception ex) { return (Sources: null, Error: $"Generator: {ex}"); }
        }, item => (item.Sources?.Program.Length ?? 0) + (item.Sources?.Natives?.Length ?? 0), (index, item) =>
        {
            (string? Output, string Stderr, int Exit) Ran()
            {
                if (NatSkipped(index)) return (null, "", 0);
                var generated = item.Sources;
                if (generated == null) return (null, item.Error!, 0);
                string exeFile = Path.Combine(workDir, $"case{index}.bin");
                // the libraries that the program asks for (`// fire-link: ssl` from the `linkLibraries` of a package)
                string linkFlags = string.Concat(generated.Program.Split('\n').Where(l => l.StartsWith("// fire-link: ", StringComparison.Ordinal)).Select(l => " -l" + l.Substring("// fire-link: ".Length).Trim()).Distinct());
                string build = NativeTestBuild.Build(NativeTestBuild.In(workDir), cxx, generated, workDir, $"case{index}", $"-pthread {NativeTestBuild.Opt} -Wall -Wextra {sanitize}", exeFile, out int buildExit, linkFlags);
                if (buildExit != 0 || build.Contains("warning:")) return (null, "C/C++-Compiler: " + build, 0);
                // (stdin closed, as the VM's run has it: a case must not wait for the input of the test run; a case that does not end in NativeCaseTimeoutMs fails
                // with the stacks of its threads instead of holding a worker - and with all of them the whole group - for ever)
                return TestProcess.Run(exeFile, "", workDir, input: "", timeoutMs: NativeTestBuild.CaseTimeoutMs, stacksOnTimeout: true);
            }
            try { natRan[index] = Ran(); }
            catch (Exception ex) { natRan[index] = (null, "Test: " + ex, 0); }
            if (!NatSkipped(index))
            {
                natVerdicts[index] = NatVerdict(index);
                // (FIRE_TESTS_RESUME: the case is finished now - recorded at once, not when its line is printed in the order of the cases: a run that is killed
                // keeps every case that finished, also those after a case that still builds)
                TestResume.Complete(natUnits[index].Unit, natUnits[index].Key, new TestResume.Check(natVerdicts[index].Ok, $"Native == VM: {natCases[index].Name}", natVerdicts[index].Ok ? null : natVerdicts[index].Detail));
                if (natVerdicts[index].Ok) NativeTestBuild.Clean(workDir, $"case{index}", Path.Combine(workDir, $"case{index}.bin"));
            }
            Interlocked.Increment(ref natRanCount);
            natRanReady[index].Set();
        });
        int natWaiting = 0;
        using (TestProgress.Heartbeat("native Faelle gebaut und gelaufen", () => Volatile.Read(ref natRanCount), natCases.Length, () => "wartet auf " + natCases[Volatile.Read(ref natWaiting)].Name))
            for (int index = 0; index < natCases.Length; index++)
            {
                Volatile.Write(ref natWaiting, index);
                natRanReady[index].Wait();
                var c = natCases[index];
                if (NatSkipped(index)) { if (natUnits[index].Decision == TestResume.Decision.Resumed) TestResume.Reprint(natUnits[index].Unit, natUnits[index].Key); continue; }
                var (natOk, natDetail) = natVerdicts[index];
                // (recorded for FIRE_TESTS_RESUME by the worker as soon as the case finished - here only printed)
                using (TestResume.Unrecorded()) CheckNat($"Native == VM: {c.Name}", natOk, natDetail);
            }
        natPipeline.Join();

        // The C in several translation units (docs/spec/NATIVE_SPLIT.md): `auto` keeps a small program the one file it was (byte for byte), `on` splits it the same way
        // every time, and the units build and run like the one file (FIRE_SPLIT=1 runs the whole group so)
        const string splitTitle = "Teilen (NATIVE_SPLIT): auto laesst kleine Programme byte-gleich, on teilt deterministisch, die Einheiten bauen und laufen wie die eine Datei";
        if (TestSelected(splitTitle) && TestResume.Pending(splitTitle, NatUnitKey(splitTitle)))
        {
            // (a unit of FIRE_TESTS_RESUME/FIRE_TESTS_SHARD: TestResume.cs)
            using var resumeUnit1 = TestResume.Unit(splitTitle, NatUnitKey(splitTitle));
            if (fire.Native.SplitModes.FromEnvironment != null) Console.WriteLine("(FIRE_SPLIT ist gesetzt: der Vergleich mit der einen Datei entfaellt)");
            else
            {
                var detail = new System.Text.StringBuilder();
                int compared = 0;
                foreach (var c in natCases.Take(40))
                {
                    LinkedProgram linkedCase;
                    try { linkedCase = new Linker().CompileAndLink(new[] { c.Source }, null, null, VmExecutionMode.Release); } catch (Exception) { continue; }
                    fire.Native.NativeSources one, auto;
                    try { one = fire.Native.CppGenerator.GenerateSources(linkedCase); auto = fire.Native.CppGenerator.GenerateSources(linkedCase, split: fire.Native.SplitMode.Auto, toolchainKind: "gcc"); }
                    catch (fire.Native.NativeNotSupportedException) { continue; }
                    if (one.Program.Length >= 2 * 1024 * 1024) continue;   // (= ProgramSplit.AutoMinBytes)   // (a large program: auto splits it - that is right)
                    compared++;
                    if (one.Program != auto.Program || auto.Units != null) detail.AppendLine($"auto: {c.Name} ist nicht mehr die eine Datei");
                }
                const string splitScript = "class Box {\n    int v\n    construct(int v) { this.v = v }\n    int Twice() { return this.v * 2 }\n}\nvar names = [\"a\", \"b\"]\nvar f = func (x) => { return x + 1 }\nvar b = new Box(20)\nprint(\"box \" + b.Twice() + \" \" + f(1) + \" \" + names[1] + \" \" + \"text\".ToUpper())\n";
                var splitLinked = new Linker().CompileAndLink(new[] { splitScript }, null, null, VmExecutionMode.Release);
                var on1 = fire.Native.CppGenerator.GenerateSources(splitLinked, split: fire.Native.SplitMode.On, toolchainKind: "gcc");
                var on2 = fire.Native.CppGenerator.GenerateSources(splitLinked, split: fire.Native.SplitMode.On, toolchainKind: "gcc");
                var tcc = fire.Native.CppGenerator.GenerateSources(splitLinked, split: fire.Native.SplitMode.Auto, toolchainKind: "tcc");
                bool units = on1.Units is { Count: >= 4 } u && u[0].Kind == fire.Native.SplitUnitKind.Header && u[1].Name == "fire_program.c" && u.Select(x => x.Name).Distinct().Count() == u.Count
                    && u.All(x => !x.Text.Contains("@@fire-split") && !x.Text.Contains("@@fire-line-reset"));
                bool same = on2.Units != null && on1.Units!.Count == on2.Units.Count && on1.Units.Zip(on2.Units).All(p => p.First.Name == p.Second.Name && p.First.Text == p.Second.Text);
                if (!units) detail.AppendLine("on: " + string.Join(", ", on1.Units?.Select(x => x.Name) ?? Array.Empty<string>()));
                if (!same) detail.AppendLine("on: zweimal erzeugt nicht gleich");
                if (tcc.Units != null) detail.AppendLine("auto mit TinyCC: geteilt");
                string exeFile = Path.Combine(workDir, "split.bin");
                string build = NativeTestBuild.Build(NativeTestBuild.In(workDir), cxx, on1, workDir, "split", $"-pthread {NativeTestBuild.Opt} -Wall -Wextra {sanitize}", exeFile, out int splitExit);
                var (splitOut, splitErr, _) = splitExit == 0 ? TestProcess.Run(exeFile, "", workDir) : ("", build, 1);
                if (splitExit != 0 || build.Contains("warning:") || splitOut != "box 40 2 b TEXT\n") detail.AppendLine($"gebaut: {build}\nAusgabe: {splitOut}{splitErr}");
                CheckNat(splitTitle, compared >= 20 && detail.Length == 0, $"{compared} verglichen\n{detail}");
            }
            resumeUnit1.Done();
        }

        // Tasks step 1 (docs/ROADMAP_TASKS.md): the thread cases built with ThreadSanitizer instead of ASan (the two do not go together) - the default build,
        // which runs without the GIL on this platform (step 1g): no data race may show up.
        const string tsanTitle = "ThreadSanitizer: die Thread-Faelle laufen ohne Data Race (ohne GIL, auf allen Kernen)";
        if (TestSelected(tsanTitle) && TestResume.Pending(tsanTitle, NatUnitKey(tsanTitle)))
        {
            // (a unit of FIRE_TESTS_RESUME/FIRE_TESTS_SHARD: TestResume.cs)
            using var resumeUnit2 = TestResume.Unit(tsanTitle, NatUnitKey(tsanTitle));
            string tsan = "-fsanitize=thread -g";
            // (FIRE_TEST_GIL=1: the same cases with the GIL, as the targets with a scheduler of fire itself run them)
            string noGil = Environment.GetEnvironmentVariable("FIRE_TEST_GIL") == "1" ? " -DFIRE_GIL" : "";
            {
                string probeFile = Path.Combine(workDir, "tsanprobe.c");
                File.WriteAllText(probeFile, "#include <pthread.h>\nstatic int n;\nstatic void* run(void* a) { (void)a; n++; return 0; }\nint main(void) { pthread_t t; pthread_create(&t, 0, run, 0); pthread_join(t, 0); return n - 1; }\n");
                var (_, probeErr, probeExit) = TestProcess.Run(fire.Native.ToolchainDef.CCompilerFor(cxx), $"-pthread {tsan} \"{probeFile}\" -o \"{probeFile}.bin\"", workDir);
                if (probeExit != 0 || TestProcess.Run(probeFile + ".bin", "", workDir).Exit != 0) tsan = "";
            }
            if (tsan.Length == 0) Console.WriteLine("(ThreadSanitizer nicht verfuegbar - die Thread-Faelle laufen nicht darunter)");
            else
            {
                var tsanReports = new System.Collections.Concurrent.ConcurrentBag<(int Index, string Text)>();
                Parallel.For(0, natThreadCases.Length, new ParallelOptions { MaxDegreeOfParallelism = TestJobs.Count }, i =>
                {
                    var c = natThreadCases[i];
                    fire.Native.NativeSources generated;
                    try { generated = fire.Native.CppGenerator.GenerateSources(new Linker().CompileAndLink(new[] { c.Source }, null, null, VmExecutionMode.Release)); }
                    catch (fire.Native.NativeNotSupportedException) { return; }
                    string exe = Path.Combine(workDir, $"tsan{i}.bin");
                    string build = NativeTestBuild.Build(NativeTestBuild.In(workDir), cxx, generated, workDir, $"tsan{i}", $"-pthread {NativeTestBuild.Opt} {tsan}{noGil}", exe, out int buildExit);
                    if (buildExit != 0) { tsanReports.Add((i, $"{c.Name}: C/C++-Compiler: {build}")); return; }
                    var (_, stderr, _) = TestProcess.Run(exe, "", workDir, environment: new Dictionary<string, string?> { ["TSAN_OPTIONS"] = "halt_on_error=0 report_signal_unsafe=0" }, timeoutMs: 120_000);
                    int at = stderr.IndexOf("WARNING: ThreadSanitizer", StringComparison.Ordinal);
                    if (at >= 0) tsanReports.Add((i, $"{c.Name}:\n{stderr.Substring(at, Math.Min(4000, stderr.Length - at))}"));
                });
                CheckNat(tsanTitle, tsanReports.IsEmpty, string.Join("\n\n", tsanReports.OrderBy(r => r.Index).Select(r => r.Text)));
            }
            resumeUnit2.Done();
        }

        // the C of numbers (readonly static constants, arithmetic of defined numbers, elements of typed arrays) and fields derived `defined`
        if (TestSelected("Generator: C-Zahlen (readonly static, Rechnen, typisierte Arrays) und hergeleitete defined-Felder") || TestSelected("readonly static mit Konstante")
            || TestSelected("Rechnen auf defined") || TestSelected("Elemente typisierter Arrays") || TestSelected("defined fuer Felder"))
        {
                // a readonly static field with a constant initializer is that constant in the C (`Word.Bits`: no read of a Value, the computation stays in C numbers)
                {
                    var wbLinked = new Linker().CompileAndLink(new[] { "class M {\n    static defined int Mask() {\n        var wb = Word.Bits\n        return (1 << (wb - 2)) + 1\n    }\n}\nprint(M.Mask())" }, null, null, VmExecutionMode.Release);
                    string wbC = fire.Native.CppGenerator.GenerateSources(wbLinked).Program;
                    int wbAt = wbC.IndexOf("static FireInt f", StringComparison.Ordinal);
                    string wbBody = wbAt >= 0 ? wbC.Substring(wbAt, Math.Min(1500, wbC.Length - wbAt)) : "";
                    CheckNat("readonly static mit Konstante: Word.Bits ist im erzeugten C eine C-Zahl (kein Lesen des Feldes, die Rechnung bleibt in C-Zahlen)",
                        wbBody.Contains("(FireInt)64LL") && !wbBody.Contains("SF") && !wbBody.Contains("shiftLeft("), wbBody);
                }
                // arithmetic of defined numbers is C (ROADMAP: as fast as possible natively): Math.*, int division, ^, casts, ~ - no generic Value function
                {
                    var mathLinked = new Linker().CompileAndLink(new[] { "class M {\n    static defined float F(defined float x, defined int n) {\n        defined float r = Math.Sqrt(x * x) + Math.Abs(x) - Math.Max(x - 1.0, 0.0) + Math.Sin(x) + x ^ 2 + Math.PI\n        defined int k = (n / 7) % 13 + (r!int) + (~n & 255) + n ^ 3\n        return r + k + Math.Floor(x)\n    }\n}\nprint(M.F(1.5, 9))" }, null, null, VmExecutionMode.Release);
                    string mathC = fire.Native.CppGenerator.GenerateSources(mathLinked).Program;
                    var mathDef = System.Text.RegularExpressions.Regex.Match(mathC, @"static Real f\d+_c\([^)]*\)\n\{[\s\S]*?\n\}\n");
                    string mathBody = mathDef.Success ? mathDef.Value : "";
                    string[] generic = { "divide(", "mathCall(", "bitNot(", "coerceType(", "power(", "powerF(", "Float(", "Int(", "SF" };
                    CheckNat("Rechnen auf defined Zahlen ist im erzeugten C reines C: Math.*, int-Division, ^, !int, ~, Math.PI (keine Value-Funktion)",
                        mathBody.Contains("sqrt(") && mathBody.Contains("floor(") && mathBody.Contains("fire_realToInt(")
                        && !mathBody.Split('\n').Where(l => !l.Contains("Value m_")).Any(l => generic.Any(g => System.Text.RegularExpressions.Regex.IsMatch(l, @"\b" + System.Text.RegularExpressions.Regex.Escape(g)))), mathBody);   // (the rare paths - a division by 0 - ask the generic one)
                }
                // fields derived `defined` (#117): every write gives a number - the field is compiled like a `defined` one (one without an initializer starts at 0);
                // one the program asks for undefined, one that may get undefined and one nothing writes are not; FIRE_INFER_UNINITIALIZED_DEFINED=0 keeps the ones without an initializer
                {
                    string fieldSrc = "class C {\n    int count\n    int step = 2\n    int lazy\n    int maybe = 1\n    int never\n    int late\n    construct() { count = 0; Tick(); late = 1 }\n    undefined Tick() { }\n    undefined Add(int x) { count++; step = step + x }\n    int Lazy() { if (lazy == undefined) { lazy = 1 } return lazy }\n    undefined Set(any v) { maybe = v }\n}\nvar c = new C()\nc.Add(2)\nc.Set(3)\nprint(c.count + c.step + c.Lazy() + c.maybe)";
                    bool Defined(fire.Bytecode.RuntimeClass rc, string f) => fire.Bytecode.DeclaredKind.IsNonNull(rc.FindFieldRequiredKind(f));
                    var fieldRc = new Linker().CompileAndLink(new[] { fieldSrc }, null, null, VmExecutionMode.Release).Program.Classes["C"];
                    string before = Environment.GetEnvironmentVariable("FIRE_INFER_UNINITIALIZED_DEFINED") ?? "";
                    Environment.SetEnvironmentVariable("FIRE_INFER_UNINITIALIZED_DEFINED", "0");
                    fire.Bytecode.RuntimeClass strictRc;
                    try { strictRc = new Linker().CompileAndLink(new[] { fieldSrc }, null, null, VmExecutionMode.Release).Program.Classes["C"]; }
                    finally { Environment.SetEnvironmentVariable("FIRE_INFER_UNINITIALIZED_DEFINED", before.Length == 0 ? null : before); }
                    var debugRc = new Linker().CompileAndLink(new[] { fieldSrc }, null, null, VmExecutionMode.Debug).Program.Classes["C"];
                    string fieldStates = string.Join(" ", new[] { "count", "step", "lazy", "maybe", "never", "late" }.Select(f => f + "=" + Defined(fieldRc, f) + "/" + Defined(strictRc, f) + "/" + Defined(debugRc, f)));
                    CheckNat("defined fuer Felder herleiten: count (ohne Initialisierer, der Konstruktor setzt es zuerst) und step sind defined, lazy (== undefined), maybe (any), never (nie geschrieben) und late (erst nach einem Aufruf gesetzt) nicht; FIRE_INFER_UNINITIALIZED_DEFINED=0 laesst count aus, ein Debug-Build leitet her wie ein Release-Build (DECISIONS D12)",
                        fieldStates == "count=True/False/True step=True/True/True lazy=False/False/False maybe=False/False/False never=False/False/False late=False/False/False", fieldStates);
                }
                // elements of typed arrays (ROADMAP: as direct as a buffer): a defined array is read with one load into a C number, a C number is stored in place
                {
                    var arrLinked = new Linker().CompileAndLink(new[] { "class A {\n    static defined float Fill(defined float a[], defined int n) {\n        for (defined int i = 0; i < n; i++) { a[i] = i * 0.5 }\n        defined float s = 0.0\n        for (defined int i = 0; i < n; i++) { s = s + a[i] }\n        return s\n    }\n}\ndefined float a[] = new float[4]\nprint(A.Fill(a, 4))" }, null, null, VmExecutionMode.Release);
                    string arrC = fire.Native.CppGenerator.GenerateSources(arrLinked).Program;
                    var arrDef = System.Text.RegularExpressions.Regex.Match(arrC, @"static Real f\d+_c\([^)]*\)\n\{[\s\S]*?\n\}\n");
                    string arrBody = arrDef.Success ? arrDef.Value : "";
                    CheckNat("Elemente typisierter Arrays nativ direkt: ein defined float-Array wird mit einem Laden gelesen, eine C-Zahl an ihren Platz geschrieben (kein Value, kein claimToHolder, keine Liste je Runde)",
                        arrBody.Contains("((Real*)ra_->typed->data)[") && arrBody.Contains("] = cv_;") && !arrBody.Contains("claimToHolder") && !arrBody.Contains(".mark = poolMark()") && !arrBody.Contains("arrPackedAt"), arrBody);
                }
        }

        // TinyCC with tasks: no thread-local storage and no builtins for atomics in tcc - the runtime brings its own (x86 instructions, a few slots on a key
        // of pthreads: FIRE_TLS_EMULATED) - the thread cases built with tcc run without the GIL, on the scheduler of tasks, and print what the VM prints
        const string tccTitle = "TinyCC: die Thread-Faelle laufen mit Tasks (ohne GIL; Atomics und Thread-Slots der Runtime) - dieselbe Ausgabe wie die VM";
        if (TestSelected(tccTitle) && TestResume.Pending(tccTitle, NatUnitKey(tccTitle)) && File.Exists("/usr/bin/tcc") && !OperatingSystem.IsWindows())
        {
            // (a unit of FIRE_TESTS_RESUME/FIRE_TESTS_SHARD: TestResume.cs)
            using var resumeUnit3 = TestResume.Unit(tccTitle, NatUnitKey(tccTitle));
            var expectedOf = new Dictionary<string, string>();
            for (int i = 0; i < natCases.Length; i++) expectedOf[natCases[i].Name] = vmResults[i];
            // (the cases that the filter left out of the run above: their VM output here, one after the other like there)
            foreach (var c in natThreadCases)
                if (!expectedOf.ContainsKey(c.Name)) { expectedOf[c.Name] = vmOutput(c.Source); Value.SingleFloats = false; Value.Ints32 = false; Value.Ints16 = false; }
            var tccReports = new System.Collections.Concurrent.ConcurrentBag<(int Index, string Text)>();
            int tccRan = 0;
            Parallel.For(0, natThreadCases.Length, new ParallelOptions { MaxDegreeOfParallelism = TestJobs.Count }, i =>
            {
                var c = natThreadCases[i];
                if (!expectedOf.TryGetValue(c.Name, out var expected) || c.Source.Contains("terminate(")) return;
                fire.Native.NativeSources generated;
                try { generated = fire.Native.CppGenerator.GenerateSources(new Linker().CompileAndLink(new[] { c.Source }, null, null, VmExecutionMode.Release)); }
                catch (fire.Native.NativeNotSupportedException) { return; }
                if (generated.Natives != null) return;   // (natives in C++: not for tcc)
                string exe = Path.Combine(workDir, $"tcc{i}.bin");
                string cFiles = NativeTestBuild.SourceFiles(generated, workDir, $"tcc{i}");   // (FIRE_SPLIT=1: the units)
                var (bout, berr, bexit) = TestProcess.Run("/usr/bin/tcc", $"-I\"{workDir}\" {cFiles} -o \"{exe}\" -lpthread -lm -ldl", workDir);
                if (bexit != 0) { tccReports.Add((i, $"{c.Name}: tcc: {bout}{berr}")); return; }
                if (!NativeTestBuild.Texts(generated).Any(t => t.Text.Contains("FIRE_THREADS"))) return;
                var (stdout, stderr, exit) = TestProcess.Run(exe, "", workDir, timeoutMs: 120_000);
                Interlocked.Increment(ref tccRan);
                string actual = exit == 0 ? stdout + stderr : $"Exitcode {exit}: {stdout}{stderr}";
                if (actual != expected) tccReports.Add((i, $"{c.Name}:\n  erwartet (VM):\n{expected}\n  erhalten (tcc):\n{actual}"));
            });
            CheckNat(tccTitle + $" ({tccRan} Faelle)", tccReports.IsEmpty && tccRan > 0, string.Join("\n\n", tccReports.OrderBy(r => r.Index).Select(r => r.Text)));
            resumeUnit3.Done();
        }

        // (the trim cases and the TLS case under Wine at the same time)
        TestJobs.Concurrently(() =>
        {
            // Leaving out what the program does not reach (docs/spec/NATIVE_BACKEND.md "Größe des Programms", ROADMAP section 11): the stripping levels 0 (none, = the VM),
            // 1 (protected: every class and public member stays), 2 (methods: every class with constructors, fields and properties, a method only when used) and
            // 3 (all, the default: only what is reached); names given as literals to reflection always stay, a lookup of what was left out is an error that says so
            // (the VM has every class and member, so only a native build can hit it).
            const string trimTitle = "Weglassen (stripping): Reflection, Proben und JSON in den Stufen 0-3, Literale, [Keep], Aliase, Debug, Groesse des ui-Programms";
            if (TestSelected(trimTitle) && TestResume.Pending(trimTitle, NatUnitKey(trimTitle)))
            {
                // (a unit of FIRE_TESTS_RESUME/FIRE_TESTS_SHARD: TestResume.cs)
                using var resumeUnit4 = TestResume.Unit(trimTitle, NatUnitKey(trimTitle));
                string trRunTrim(string source, fire.Native.StrippingLevel? stripping, string tag)
                {
                    fire.Native.NativeSources generated;
                    try { generated = fire.Native.CppGenerator.GenerateSources(new Linker().CompileAndLink(new[] { source }, null, null, VmExecutionMode.Release), stripping: stripping); }
                    catch (fire.Native.NativeNotSupportedException ex) { return "nicht uebersetzbar: " + ex.Message; }
                    string exe = Path.Combine(workDir, $"strip_{tag}.bin");
                    string build = NativeTestBuild.Build(NativeTestBuild.In(workDir), cxx, generated, workDir, $"strip_{tag}", $"-pthread -O1 -Wall -Wextra {sanitize}", exe, out int buildExit);
                    if (buildExit != 0 || build.Contains("warning:")) return "C/C++-Compiler: " + build;
                    using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = workDir })!;
                    var err = p.StandardError.ReadToEndAsync();
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    return p.ExitCode == 0 ? output : $"Exitcode {p.ExitCode}: {output}{err.Result}";
                }
                var trLevels = new[] { fire.Native.StrippingLevel.None, fire.Native.StrippingLevel.Protected, fire.Native.StrippingLevel.Methods, fire.Native.StrippingLevel.All };
                // (the names that are not literals of the function that calls reflection: made at run time by a function that does not)
                const string trByName = """
                    #import "reflection"
                    class Animal {
                        int legs = 4
                        string Speak() { return "..." }
                    }
                    class Dog : Animal {
                        string Bark() { return "wuff" }
                        string Wag() { return "wag" }
                        private string Hidden() { return "hidden" }
                        string CallOwn(string n) { return Reflect.Call(this, n, []) }
                    }
                    class Ghost {
                        int b
                        construct() { this.b = 2 }
                    }
                    class Literal {
                        int c
                        construct() { this.c = 3 }
                    }
                    [Keep]
                    class Kept {
                        int k
                        construct() { this.k = 5 }
                    }
                    class Tool {
                        [Keep] static int Version() { return 4 }
                    }
                    class N {
                        static string Of(string a, string b) { return a + b }
                        static bool Wanted(string n) { return n == "Ghost" || n == "Literal" || n == "Kept" || n == "Tool" || n == "Dog" }
                    }
                    var d = new Dog()
                    print(Reflect.Call(d, "Bark", []) + " " + Reflect.Get(d, "legs"))
                    try { print(Reflect.Call(d, N.Of("Wa", "g"), [])) } catch (ReflectionException e) { print("call: " + e.message) }
                    try { print(d.CallOwn(N.Of("Hid", "den"))) } catch (ReflectionException e) { print("private: " + e.message) }
                    try { any g = Reflect.New(N.Of("Gho", "st"), []); print("ghost " + g.b) } catch (ReflectionException e) { print("new: " + e.message) }
                    try { print(Type.Named(N.Of("Gho", "st")) != undefined) } catch (ReflectionException e) { print("named: " + e.message) }
                    any l = Reflect.New("Literal", [])
                    print("literal " + l.c + " " + (Type.Named("Literal") != undefined))
                    any k = Reflect.New(N.Of("Ke", "pt"), [])
                    print("kept " + k.k)
                    print(Type.Named(N.Of("To", "ol")) != undefined)
                    var has = ""
                    foreach (n in Type.Names()) { if (N.Wanted(n)) { has = has + n + " " } }
                    print(has)
                    var methods = ""
                    foreach (m in Type.Of(d).Methods()) { methods = methods + m.Name + " " }
                    print(methods)
                    print(Reflect.Has(d, "legs") + " " + Reflect.Has(d, "Bark"))
                    """;
                string trVmByName = vmOutput(trByName);
                const string trVmExpected = "wuff 4\nwag\nhidden\nghost 2\nTrue\nliteral 3 True\nkept 5\nTrue\nDog Ghost Kept Literal Tool \nBark Wag Hidden CallOwn Speak \nTrue True\n";
                CheckNat("Weglassen (stripping): die VM findet jede Klasse und jeden Member per Name", trVmByName == trVmExpected, trVmByName);
                string trWag(int level) => $"call: Member 'Dog.Wag' was left out of the build (stripping {level}). Add [Keep] to 'Dog.Wag' or use stripping <= 1.\n";
                string trHidden(int level) => $"private: Member 'Dog.Hidden' was left out of the build (stripping {level}). Add [Keep] to 'Dog.Hidden' or use stripping 0.\n";
                const string trGhost = "Class 'Ghost' was left out of the build (stripping 3). Add [Keep] to 'Ghost' or use stripping <= 2.\n";
                var trExpected = new[]
                {
                    trVmExpected,
                    "wuff 4\nwag\n" + trHidden(1) + "ghost 2\nTrue\nliteral 3 True\nkept 5\nTrue\nDog Ghost Kept Literal Tool \nBark Wag CallOwn Speak \nTrue True\n",
                    "wuff 4\n" + trWag(2) + trHidden(2) + "ghost 2\nTrue\nliteral 3 True\nkept 5\nTrue\nDog Ghost Kept Literal Tool \nBark CallOwn \nTrue True\n",
                    "wuff 4\n" + trWag(3) + trHidden(3) + "new: " + trGhost + "named: " + trGhost + "literal 3 True\nkept 5\nTrue\nDog Kept Literal Tool \nBark CallOwn \nTrue True\n",
                };
                string[] trTitles =
                {
                    "0 none - alles bleibt (wie die VM)",
                    "1 protected - jede Klasse und jeder oeffentliche Member bleibt, ein privater per Name ist ein klarer Fehler",
                    "2 methods - jede Klasse mit Konstruktoren und Feldern bleibt, Reflect.Call einer nicht benutzten Methode per Laufzeitname ist ein klarer Fehler",
                    "3 all (Standard) - eine nie erreichte Klasse per Laufzeitname ist ein klarer Fehler; Literale, [Keep] und Type.Names() wie erwartet",
                };
                for (int lv = 0; lv < 4; lv++)
                {
                    string got = trRunTrim(trByName, lv == 3 ? null : trLevels[lv], $"byname{lv}");
                    CheckNat($"Weglassen (stripping): {trTitles[lv]}", got == trExpected[lv], got);
                }

                // probes at the levels 2 and 3 (fields, properties, computed properties, Reflect.Set, probes on every member)
                const string trProbes = """
                    #import "reflection"
                    class Cfg {
                        int volume = 1
                        string name = "n"
                        int Level { get { return this.volume * 10 } set { this.volume = value / 10 } }
                        int Twice { get { return this.volume * 2 } }
                        string Unused() { return "u" }
                    }
                    class Other { int x = 1 }
                    var cfg = new Cfg()
                    probe cfg.volume changed { print("volume " + old + " -> " + value) }
                    probe cfg.Twice changed { print("twice " + value) }
                    cfg.volume = 5
                    cfg.Level = 70
                    Reflect.Set(cfg, "volume", 9)
                    probe cfg.* changed (obj, member, o, n) => print("any " + member + " " + n)
                    cfg.name = "z"
                    silence cfg.*
                    cfg.volume = 2
                    var o = new Other()
                    Reflect.ProbeAll(o, "changed", func (any obj, any member, any a, any b) => print("other " + member + " " + b))
                    o.x = 3
                    print(Reflect.Get(cfg, "Level") + " " + cfg.volume)
                    """;
                string trVmProbes = vmOutput(trProbes);
                foreach (var lv in new[] { fire.Native.StrippingLevel.Methods, fire.Native.StrippingLevel.All })
                {
                    string got = trRunTrim(trProbes, lv, $"probes{(int)lv}");
                    CheckNat($"Weglassen (stripping): Proben in Stufe {(int)lv} wie in der VM", got == trVmProbes && trVmProbes.Contains("twice 18"), $"VM:\n{trVmProbes}nativ:\n{got}");
                }

                // JSON: "$type" names only the classes of the declared types (with their subclasses) at level 3 - runtime data, a class outside them is unknown; at 0-2
                // every class is there
                const string trJson = """
                    #import "json"
                    class Shape { string name = "s" }
                    class Square : Shape { int side = 2 }
                    class Lonely { int never = 5 }
                    class Box {
                        Shape shape
                        construct() { }
                    }
                    print(Json.Serialize(new Square()))
                    try {
                        var b = Json.Deserialize<Box>("{\"shape\":{\"$type\":\"Lonely\"}}")
                        any x = b.shape
                        print("lonely " + x.never)
                    } catch (JsonException e) { print("json: " + e.message) }
                    """;
                string trVmJson = vmOutput(trJson);
                CheckNat("Weglassen (stripping): JSON in der VM - $type darf jede Klasse nennen", trVmJson == "{\"$type\":\"Square\",\"name\":\"s\",\"side\":2}\nlonely undefined\n", trVmJson);
                string trJsonReached = trRunTrim(trJson, null, "json3");
                CheckNat("Weglassen (stripping): JSON in Stufe 3 - $type ausserhalb der deklarierten Typen (und ihrer Unterklassen) ist eine unbekannte Klasse",
                    trJsonReached == "{\"$type\":\"Square\",\"name\":\"s\",\"side\":2}\njson: $.shape: unknown class 'Lonely'.\n", trJsonReached);
                foreach (var lv in new[] { fire.Native.StrippingLevel.None, fire.Native.StrippingLevel.Methods })
                {
                    string got = trRunTrim(trJson, lv, $"json{(int)lv}");
                    CheckNat($"Weglassen (stripping): JSON in Stufe {(int)lv} - wie die VM", got == trVmJson, got);
                }
                // what JSON pulls in: writing only the objects the program makes; reading the declared classes, their subclasses and the classes of their fields
                string trGenerated(string source) => fire.Native.CppGenerator.GenerateSources(new Linker().CompileAndLink(new[] { source }, null, null, VmExecutionMode.Release)).Program;
                string trWriteOnly = trGenerated("#import \"json\"\nclass P { int x = 1 }\nclass Unused { int y = 2 }\nclass Other : P { }\nprint(Json.Serialize(new P()))");
                string trReading = trGenerated("#import \"json\"\nclass P { int x = 1\n Q q }\nclass Q { int z }\nclass Unused { int y = 2 }\nclass Other : P { }\nprint(Json.Deserialize<P>(\"{}\").x)");
                string trAnySlot = trGenerated("#import \"json\"\nclass P { int x = 1\n any free }\nclass Unused { int y = 2 }\nprint(Json.Deserialize<P>(\"{}\").x)");
                CheckNat("Weglassen (stripping): JSON - Schreiben zieht keine Klassen nach, Lesen die deklarierten (mit Unterklassen und Feldtypen), ein any-Feld alle",
                    !trWriteOnly.Contains("{\"Unused\"") && !trWriteOnly.Contains("{\"Other\"") && trWriteOnly.Contains("{\"P\"")
                    && trReading.Contains("{\"Other\"") && trReading.Contains("{\"Q\"") && !trReading.Contains("{\"Unused\"") && trAnySlot.Contains("{\"Unused\""), "");

                // a debug build keeps everything (the debugger, ROADMAP section 8); the configuration (numbers, names, the old "trim"), the project and the command line
                var trDebugLinked = new Linker().CompileAndLink(new[] { "#debug\nprint(1)" }, null, null, null);
                var trReleaseLinked = new Linker().CompileAndLink(new[] { "print(1)" }, null, null, VmExecutionMode.Release);
                fire.Native.StrippingLevel? trParsed(string json) => fire.Native.NativeConfig.Parse(json).Stripping;
                bool trThrows(Action a) { try { a(); return false; } catch (Exception) { return true; } }
                var trProject = System.Text.Json.JsonSerializer.Deserialize<fire.Projects.ProjectSettings>("{\"stripping\": 2}", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                var trProjectName = System.Text.Json.JsonSerializer.Deserialize<fire.Projects.ProjectSettings>("{\"stripping\": \"protected\"}", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                var trCliConfig = new fire.Native.NativeConfig { Stripping = fire.Native.StrippingLevel.Methods, StrippingFromCommandLine = true };
                var trAliasChecks = new (string Name, bool Ok)[]
                {
                    ("debug 0", fire.Native.CppGenerator.EffectiveStripping(trDebugLinked, fire.Native.StrippingLevel.All) == fire.Native.StrippingLevel.None),
                    ("default 3", fire.Native.CppGenerator.EffectiveStripping(trReleaseLinked, null) == fire.Native.StrippingLevel.All),
                    ("given", fire.Native.CppGenerator.EffectiveStripping(trReleaseLinked, fire.Native.StrippingLevel.Methods) == fire.Native.StrippingLevel.Methods),
                    ("number", trParsed("{\"stripping\": 2}") == fire.Native.StrippingLevel.Methods && trParsed("{\"stripping\": 0}") == fire.Native.StrippingLevel.None),
                    ("name", trParsed("{\"stripping\": \"protected\"}") == fire.Native.StrippingLevel.Protected && trParsed("{\"stripping\": \"3\"}") == fire.Native.StrippingLevel.All),
                    ("trim all", trParsed("{\"trim\": \"all\"}") == fire.Native.StrippingLevel.None),
                    ("trim reflection", trParsed("{\"trim\": \"reflection\"}") == fire.Native.StrippingLevel.Protected),
                    ("trim reached", trParsed("{\"trim\": \"reached\"}") == fire.Native.StrippingLevel.All),
                    ("stripping before trim", trParsed("{\"stripping\": 2, \"trim\": \"all\"}") == fire.Native.StrippingLevel.Methods && trParsed("{\"trim\": \"all\", \"stripping\": 2}") == fire.Native.StrippingLevel.Methods),
                    ("none", trParsed("{}") == null),
                    ("wrong", trThrows(() => trParsed("{\"stripping\": 7}")) && trThrows(() => trParsed("{\"trim\": \"some\"}"))),
                    ("saved as name", fire.Native.NativeConfig.Parse("{\"trim\": \"reflection\"}").ToJson().Contains("\"stripping\": \"protected\"") && !fire.Native.NativeConfig.Parse("{\"trim\": \"all\"}").ToJson().Contains("trim")),
                    ("--keep-all", CommandLineParser.Parse(new[] { "build", "a.fire", "--keep-all" }).KeepAll && CommandLineParser.Parse(new[] { "run", "a.fire", "--keep-all" }).Error != null),
                    ("--stripping", CommandLineParser.Parse(new[] { "build", "a.fire", "--stripping", "1" }).Stripping == fire.Native.StrippingLevel.Protected
                        && CommandLineParser.Parse(new[] { "native", "a.fire", "--stripping", "methods" }).Stripping == fire.Native.StrippingLevel.Methods
                        && CommandLineParser.Parse(new[] { "build", "a.fire", "--stripping", "9" }).Error != null && CommandLineParser.Parse(new[] { "run", "a.fire", "--stripping", "1" }).Error != null),
                    ("project", trProject.Stripping == "2" && trProject.Validate().Count == 0 && trProjectName.Stripping == "protected" && trProjectName.Validate().Count == 0
                        && new fire.Projects.ProjectSettings { Stripping = "lots" }.Validate().Count == 1 && fire.Native.Strippings.Parse(trProject.Stripping) == fire.Native.StrippingLevel.Methods),
                    ("command line before project", fire.Compiler.NativeBuilder.StrippingOf(trCliConfig, null) == fire.Native.StrippingLevel.Methods),
                };
                CheckNat("Weglassen (stripping): ein Debug-Build behaelt alles; \"stripping\" als Zahl und Name, \"trim\" und --keep-all als Aliase, --stripping, das Projekt",
                    trAliasChecks.All(c => c.Ok), string.Join(", ", trAliasChecks.Where(c => !c.Ok).Select(c => c.Name)));

                // the direction (docs/spec/NATIVE_BACKEND.md "Optimierungsrichtung"): --optimize, the project, fire.native.json, the target; the flags of the toolchain and the define
                {
                    var optSpeed = fire.Runtime.OptimizeFor.Speed;
                    var optSize = fire.Runtime.OptimizeFor.Size;
                    var linux = TargetProfile.TryGet("linux", out var lx) ? lx : TargetProfile.Host;
                    var optCli = fire.Native.NativeConfig.Parse("{\"optimize\": \"speed\"}");
                    optCli.Optimize = "size"; optCli.OptimizeFromCommandLine = true;
                    var optPlan = new fire.Projects.ProjectSettings { Optimize = "speed" };
                    var gccTc = fire.Native.ToolchainDef.BuiltIn["gcc"];
                    var watcomTc = fire.Native.ToolchainDef.BuiltIn["watcom"];
                    var optLinked = new Linker { Optimize = optSize }.CompileAndLink(new[] { "print(1)" }, null, null, VmExecutionMode.Release);
                    var optChecks = new (string Name, bool Ok)[]
                    {
                        ("--optimize", CommandLineParser.Parse(new[] { "build", "a.fire", "--optimize", "size" }).Optimize == optSize
                            && CommandLineParser.Parse(new[] { "native", "a.fire", "--optimize", "SPEED" }).Optimize == optSpeed
                            && CommandLineParser.Parse(new[] { "build", "a.fire", "--optimize", "fast" }).Error != null && CommandLineParser.Parse(new[] { "run", "a.fire", "--optimize", "size" }).Error != null),
                        ("targets", linux.Optimize == optSpeed && TargetProfile.Dos16.Optimize == optSize && TargetProfile.Win16.Optimize == optSize && TargetProfile.Dos32.Optimize == optSize && TargetProfile.Bios16.Optimize == optSize),
                        ("target of the file", fire.Native.NativeConfig.Parse("{\"targets\": {\"small\": {\"extends\": \"linux\", \"optimize\": \"size\"}}}").ResolveTarget("small").Optimize == optSize
                            && trThrows(() => fire.Native.NativeConfig.Parse("{\"targets\": {\"small\": {\"extends\": \"linux\", \"optimize\": \"tiny\"}}}").ResolveTarget("small"))),
                        ("order", fire.Compiler.NativeBuilder.OptimizeOf(optCli, optPlan, linux) == optSize
                            && fire.Compiler.NativeBuilder.OptimizeOf(fire.Native.NativeConfig.Parse("{\"optimize\": \"size\"}"), optPlan, linux) == optSpeed
                            && fire.Compiler.NativeBuilder.OptimizeOf(fire.Native.NativeConfig.Parse("{\"optimize\": \"size\"}"), (fire.Projects.ProjectSettings?)null, linux) == optSize
                            && fire.Compiler.NativeBuilder.OptimizeOf(null, (fire.Projects.ProjectSettings?)null, TargetProfile.Dos16) == optSize && fire.Compiler.NativeBuilder.OptimizeOf(null, (fire.Projects.ProjectSettings?)null, linux) == optSpeed),
                        ("wrong", trThrows(() => fire.Native.NativeConfig.Parse("{\"optimize\": \"tiny\"}")) && new fire.Projects.ProjectSettings { Optimize = "tiny" }.Validate().Count == 1),
                        ("flags", fire.Compiler.NativeBuilder.WithDirection(gccTc, optSize).Optimization == "size" && fire.Compiler.NativeBuilder.WithDirection(gccTc, optSpeed).Optimization == "-O2"
                            && fire.Compiler.NativeBuilder.WithDirection(new fire.Native.ToolchainDef { Kind = "gcc", Optimization = "-O3" }, optSpeed).Optimization == "-O3"
                            && fire.Compiler.NativeBuilder.WithDirection(watcomTc, optSize).Optimization == "size" && fire.Compiler.NativeBuilder.WithDirection(watcomTc, optSpeed).Optimization == "-Ot"
                            && gccTc.Optimization == "-O2"),
                        ("define", fire.Native.CppGenerator.Generate(optLinked).Contains("#define FIRE_OPTIMIZE_SIZE 1") && !fire.Native.CppGenerator.Generate(new Linker().CompileAndLink(new[] { "print(1)" }, null, null, VmExecutionMode.Release)).Contains("#define FIRE_OPTIMIZE_SIZE")),
                    };
                    CheckNat("Optimierungsrichtung: --optimize, Projekt, fire.native.json, Ziel (Watcom-Ziele: size); Flags der Toolchain (-Os/-O2, Watcom -Os/-Ot) und FIRE_OPTIMIZE_SIZE",
                        optChecks.All(c => c.Ok), string.Join(", ", optChecks.Where(c => !c.Ok).Select(c => c.Name)));
                }

                // the size: the minimal ui program (a window, a root, one label) at the four levels - 7.85 MB of C before section 11 (the style of part 3 - corners,
                // shadows, the surfaces of popups, docs/spec/UI_PLATFORM.md - added about 25 KB; it grew to 4.06 MB with the work of http-server: up to 5 MB is
                // fine for now, what grew is to be checked - docs/ROADMAP.md section 11)
                const string trUiMinimal = "#import \"ui\"\nvar fb = new Framebuffer(480, 320)\nvar win = new Window(fb, \"x\")\nvar ui = new UI.Root(fb, win)\nvar label = new UI.Label(\"Hello\", 20, 20)\nui.Add(label)\nui.Run()";
                var trUiLinked = new Linker().CompileAndLink(new[] { trUiMinimal }, null, null, VmExecutionMode.Release);
                var trUiSizes = trLevels.Select(lv => fire.Native.CppGenerator.GenerateSources(trUiLinked, stripping: lv).Program.Length).ToArray();
                int trUiSize = fire.Native.CppGenerator.GenerateSources(trUiLinked).Program.Length;
                // (level 1 is about level 0 here: ui keeps little private code that no public member reaches; the names of the left out members cost a few bytes)
                // `--stats` with linkage static (ROADMAP 17): the code of the library modules by module (from the map), the program's functions; `stats` compares two builds
                {
                    string stDir = Path.Combine(Path.GetTempPath(), "fire-stats-static-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(stDir);
                    try
                    {
                        string stSrc = Path.Combine(stDir, "a"), stStatic = Path.Combine(stDir, "b");
                        var gcc = fire.Native.ToolchainDef.BuiltIn["gcc"];
                        var srcBuilt = fire.Compiler.NativeBuilder.Build(new[] { "print(\"hi\")" }, new fire.Native.NativeConfig { Stats = true }, TargetProfile.Host, gcc, stSrc, VmExecutionMode.Release);
                        var stBuilt = fire.Compiler.NativeBuilder.Build(new[] { "print(\"hi\")" }, new fire.Native.NativeConfig { Stats = true, Linkage = "static" }, TargetProfile.Host, gcc, stStatic, VmExecutionMode.Release);
                        string stText = File.Exists(stStatic + ".stats.txt") ? File.ReadAllText(stStatic + ".stats.txt") : "";
                        var a = File.Exists(stSrc + ".stats.json") ? fire.Native.NativeStats.FromJson(File.ReadAllText(stSrc + ".stats.json")) : null;
                        var b = File.Exists(stStatic + ".stats.json") ? fire.Native.NativeStats.FromJson(File.ReadAllText(stStatic + ".stats.json")) : null;
                        string cmp = a != null && b != null ? fire.Native.NativeStats.Compare(a, b) : "";
                        // (the heap at the end: FIRE_STATS=1 when the program built with --stats starts)
                        string heap = "";
                        if (srcBuilt.Ok)
                        {
                            var psi = new System.Diagnostics.ProcessStartInfo(stSrc) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
                            psi.Environment["FIRE_STATS"] = "1";
                            using var proc = System.Diagnostics.Process.Start(psi)!;
                            heap = proc.StandardError.ReadToEnd();
                            proc.WaitForExit(30_000);
                        }
                        CheckNat("--stats mit linkage static: der Code der Module nach Modul (aus der Map), das Programm, die Kopien der Runtime; forge stats vergleicht zwei Builds; FIRE_STATS=1 berichtet den Heap",
                            srcBuilt.Ok && stBuilt.Ok && stText.Contains("fire: core (library)") && stText.Contains("fire: program") && stText.Contains("runtime (copy in core)")
                            && cmp.StartsWith("Comparison", StringComparison.Ordinal) && cmp.Contains("fire: core (library)") && b!.Code.Sum(o => o.Bytes) < b.FileBytes
                            && heap.Contains("Heap at the end (FIRE_STATS):") && heap.Contains("strings"),
                            stText + "\n" + cmp + "\n" + heap + (stBuilt.Log.Length > 2000 ? stBuilt.Log[^2000..] : stBuilt.Log));
                    }
                    finally { try { Directory.Delete(stDir, true); } catch (IOException) { } }
                }
                CheckNat("Weglassen (stripping): das minimale ui-Programm bleibt klein (Stufe 3 unter 5 MB C; Stufe 2 kleiner als 1, 3 kleiner als 2, 1 etwa wie 0 und mehr als doppelt so gross wie 3)",
                    trUiSize == trUiSizes[3] && trUiSize < 5_000_000 && trUiSizes[1] <= trUiSizes[0] + 20_000 && trUiSizes[2] < trUiSizes[1] && trUiSizes[3] < trUiSizes[2] && trUiSizes[0] > 2 * trUiSize,
                    string.Join(" / ", trUiSizes.Select((s, i) => $"{i}: {s}")));
                resumeUnit4.Done();
            }
        }, () =>
        {
            // SChannel, the TLS of the Windows platform: the TLS case built for Windows with MinGW and run under Wine (when both are on this machine) must print what the VM prints
            lock (TestJobs.Wine)
            {
                string? FindTool(string name) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);
                string? mingw = FindTool("x86_64-w64-mingw32-g++");
                string? wine = new[] { "/usr/lib/wine/wine64", "/usr/bin/wine64", "/usr/bin/wine" }.FirstOrDefault(File.Exists) ?? FindTool("wine64") ?? FindTool("wine");
                int tlsIndex = Array.FindIndex(natCases, c => c.Name.StartsWith("Tls: Handshake", StringComparison.Ordinal));
                if (mingw == null || wine == null || tlsIndex < 0)
                {
                    Console.WriteLine("(Tls mit SChannel: uebersprungen - MinGW (x86_64-w64-mingw32-g++) und Wine sind auf diesem Rechner nicht da)");
                }
                else
                {
                    string name = "Tls (SChannel, fuer Windows gebaut, unter Wine)";
                    try
                    {
                        var generated = fire.Native.CppGenerator.GenerateSources(new Linker().CompileAndLink(new[] { natCases[tlsIndex].Source }, null, null, VmExecutionMode.Release, null, TargetProfile.Windows), TargetProfile.Windows);
                        string cpp = generated.Program;
                        string exeFile = Path.Combine(workDir, "schannel.exe");
                        string linkFlags = string.Concat(cpp.Split('\n').Where(l => l.StartsWith("// fire-link: ", StringComparison.Ordinal)).Select(l => " -l" + l.Substring("// fire-link: ".Length).Trim()).Distinct());
                        string Run(string tool, string arguments, out int exit)
                        {
                            var info = new System.Diagnostics.ProcessStartInfo(tool, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = workDir };
                            info.Environment["WINEPREFIX"] = Path.Combine(workDir, "wineprefix");
                            info.Environment["WINEDEBUG"] = "-all";
                            using var p = System.Diagnostics.Process.Start(info)!;
                            var errTask = p.StandardError.ReadToEndAsync();
                            string output = p.StandardOutput.ReadToEnd();
                            if (!p.WaitForExit(240000)) { try { p.Kill(true); } catch { } exit = -1; return output + "(Zeitueberschreitung)"; }
                            exit = p.ExitCode;
                            return output + errTask.Result;
                        }
                        string build = NativeTestBuild.Build((tool, arguments) => (Run(tool, arguments, out int code), code), mingw, generated, workDir, "schannel", $"{NativeTestBuild.Opt} -Wall -Wextra -static", exeFile, out int buildExit, linkFlags);
                        if (buildExit != 0 || build.Contains("warning:")) CheckNat(name, false, "MinGW: " + build);
                        else
                        {
                            string actual = Run(wine, $"\"{exeFile}\"", out int runExit);
                            // (the Wine output has no stray lines: WINEDEBUG is off; lines Wine itself prints about the prefix are dropped)
                            actual = string.Join("\n", actual.Replace("\r", "").Split('\n').Where(l => !l.StartsWith("wine:", StringComparison.Ordinal)));
                            string expected = vmResults[tlsIndex].Replace("\r", "");
                            CheckNat(name, runExit == 0 && expected.TrimEnd() == actual.TrimEnd(), $"  erwartet (VM):\n{expected}\n  erhalten (Wine, Exitcode {runExit}):\n{actual}");
                        }
                    }
                    catch (Exception ex) { CheckNat(name, false, ex.Message); }
                }
            }
        });

        // The switch itself: VM output differs between the precisions, the directive is validated, the override wins
        string out64 = vmOutput("print(0.1 + 0.2)\nprint(1.0 / 3)"), out32 = vmOutput("#floatwidth 32\nprint(0.1 + 0.2)\nprint(1.0 / 3)");
        Value.SingleFloats = false; Value.Ints32 = false; Value.Ints16 = false;
        CheckNat("#floatwidth: 64 Bit ist der Standard", out64 == "0.30000000000000004\n0.3333333333333333\n", out64);
        CheckNat("#floatwidth 32: float rechnet und druckt mit 32 Bit", out32 == "0.3\n0.33333334\n", out32);
        var overridden = new Linker().CompileAndLink(new[] { "#floatwidth 64\nprint(1)" }, null, null, null, floatWidthOverride: 32);
        CheckNat("Linker: floatWidthOverride gewinnt gegen die Direktive", overridden.FloatWidth == 32 && new Linker().CompileAndLink(new[] { "print(1)" }).FloatWidth == 64);
        try { new Linker().CompileAndLink(new[] { "#floatwidth 16\nprint(1)" }); CheckNat("#floatwidth 16 wird abgelehnt", false); }
        catch (Exception ex) { CheckNat("#floatwidth 16 wird abgelehnt", ex.Message.Contains("32 or 64"), ex.Message); }
        var cli32 = CommandLineParser.Parse(new[] { "run", "a.script", "-f", "32" });
        var cliBad = CommandLineParser.Parse(new[] { "run", "a.script", "-f", "16" });
        CheckNat("Befehlszeile: -f 32 / -f 16", cli32.Error == null && cli32.FloatWidth == 32 && cliBad.Error != null && CommandLineParser.Parse(new[] { "run", "a.script" }).FloatWidth == null);
        var narrowed = new Linker().CompileAndLink(new[] { "var x = 0.1\nprint(x)" }, null, null, null, floatWidthOverride: 32);
        CheckNat("Konstanten werden auf 32 Bit gerundet", narrowed.Program.TopLevel.Constants.Any(c => c.Kind == ValueKind.Float && c.AsFloat() == (double)0.1f));

        // #intwidth (SPEC 8.2.1): int is 64 bits everywhere unless the program asks for 32
        {
            string int64 = vmOutput("print(2147483647 + 1)"), int32 = vmOutput("#intwidth 32\nprint(2147483647 + 1)\nprint(65536 * 65536)\nprint(3000000000)");
            Value.SingleFloats = false; Value.Ints32 = false; Value.Ints16 = false;
            CheckNat("#intwidth: 64 Bit ist der Standard, auch auf dem esp32", int64 == "2147483648\n" && new Linker().CompileAndLink(new[] { "print(1)" }).IntWidth == 64
                && new Linker().CompileAndLink(new[] { "print(1)" }, null, null, null, null, TargetProfile.Esp32).IntWidth == 64, int64);
            CheckNat("#intwidth 32: int rechnet mit 32 Bit (Ergebnis abgeschnitten, Konstanten auch)", int32 == "-2147483648\n0\n-1294967296\n", int32);
            string int16 = vmOutput("#intwidth 16\nprint(32767 + 1)\nprint(256 * 256)\nprint(70000)");
            CheckNat("#intwidth 16: int rechnet mit 16 Bit (Ergebnis abgeschnitten, Konstanten auch), das C bekommt FIRE_INT16",
                int16 == "-32768\n0\n4464\n" && new Linker().CompileAndLink(new[] { "#intwidth 16\nprint(1)" }) is var l16 && l16.IntWidth == 16
                && fire.Native.CppGenerator.Generate(l16).Contains("#define FIRE_INT16 1"), int16);
            Value.Ints32 = false; Value.Ints16 = false;
            try { new Linker().CompileAndLink(new[] { "#intwidth 8\nprint(1)" }); CheckNat("#intwidth 8 wird abgelehnt", false); }
            catch (Exception ex) { CheckNat("#intwidth 8 wird abgelehnt", ex.Message.Contains("16, 32 or 64"), ex.Message); }
            var cliInt = CommandLineParser.Parse(new[] { "run", "a.script", "-i", "32" });
            CheckNat("Befehlszeile: -i 32 / -i 16 / -i 8", cliInt.Error == null && cliInt.IntWidth == 32 && CommandLineParser.Parse(new[] { "run", "a.script", "-i", "16" }).IntWidth == 16
                && CommandLineParser.Parse(new[] { "run", "a.script", "-i", "8" }).Error != null
                && CommandLineParser.Parse(new[] { "run", "a.script" }).IntWidth == null);
            var intLinked = new Linker().CompileAndLink(new[] { "#intwidth 32\nvar x = 3000000000\nprint(x)" });
            CheckNat("#intwidth 32: Konstanten werden auf 32 Bit abgeschnitten, das C bekommt FIRE_INT32", intLinked.IntWidth == 32
                && intLinked.Program.TopLevel.Constants.Any(c => c.Kind == ValueKind.Int && c.AsInt() == -1294967296)
                && fire.Native.CppGenerator.Generate(intLinked, TargetProfile.Host).Contains("#define FIRE_INT32 1"));
            Value.SingleFloats = false; Value.Ints32 = false; Value.Ints16 = false;

            // `word` (SPEC 8.10): the target's machine word - 64 bits on the desktop, 32 on the esp32 (the VM follows the target of the build), `wordWidth` in a target
            {
                var hostWord = new Linker().CompileAndLink(new[] { "print(Word.Bits)" });
                var espWord = new Linker().CompileAndLink(new[] { "#if word32\nprint(Word.Bits)\n#endif" }, null, null, null, null, TargetProfile.Esp32);
                CheckNat("word: 64 Bit auf dem Rechner, 32 auf esp32/freertos/wasm; #if word32/word64, FIRE_WORD_BITS im C", hostWord.WordWidth == 64 && espWord.WordWidth == 32
                    && TargetProfile.FreeRtos.WordWidth == 32 && TargetProfile.Wasm.WordWidth == 32 && TargetProfile.Linux.WordWidth == 64
                    && fire.Native.CppGenerator.Generate(espWord, TargetProfile.Esp32).Contains("#define FIRE_WORD_BITS 32")
                    && fire.Native.CppGenerator.Generate(hostWord, TargetProfile.Host).Contains("#define FIRE_WORD_BITS 64")
                    && fire.Compiler.ConditionalSymbols.For(TargetProfile.Esp32).Contains("word32") && fire.Compiler.ConditionalSymbols.For(TargetProfile.Linux).Contains("word64"));
                var wordConfig = fire.Native.NativeConfig.Parse("""{ "targets": { "avr": { "extends": "freertos", "wordWidth": 16 } } }""");
                bool badRejected = false;
                try { fire.Native.NativeConfig.Parse("""{ "targets": { "x": { "extends": "linux", "wordWidth": 8 } } }""").ResolveTarget("x"); }
                catch (fire.Native.NativeConfigException) { badRejected = true; }
                CheckNat("word: wordWidth in fire.native.json (16), 8 wird abgelehnt", wordConfig.ResolveTarget("avr").WordWidth == 16 && badRejected);
                const string word32Source = """
                    word m = -1
                    word a = 4000000000
                    print(m + " " + Word.Bits + " " + (m + 1) + " " + (a + a) + " " + a * 3 + " " + (m >> 31) + " " + (m << 31) + " " + (m << 32))
                    print((a * 2) / 7 + " " + (a > 5) + " " + (m == -1) + " " + m!int + " " + (-a) + " " + ~a + " " + 5000000000!word)
                    word wb[] = [4294967301, -1]
                    wb[1]++
                    print(wb[0] + " " + wb[1] + " " + wb.length + " " + wb)
                    #if word32
                    print("word32")
                    #endif
                    """;
                // (what the C# VM printed with the words of esp32 - the VM in C has words of 64 bits only, FIRE_WORD_BITS)
                const string vm32 = "4294967295 32 0 3705032704 3410065408 1 2147483648 0\n529290386 True True 4294967295 294967296 294967295 705032704\n5 0 2 <buffer 2 words>\nword32\n";
                var target32 = TargetProfile.Host with { WordWidth = 32 };
                var generated32 = fire.Native.CppGenerator.GenerateSources(new Linker().CompileAndLink(new[] { word32Source }, null, null, VmExecutionMode.Release, null, target32), target32);
                Value.WordBits = 64;
                string exe32 = Path.Combine(workDir, "word32.bin");
                string build32 = NativeTestBuild.Build(NativeTestBuild.In(workDir), cxx, generated32, workDir, "word32", $"-pthread {NativeTestBuild.Opt} -Wall -Wextra {sanitize}", exe32, out int build32Exit);
                string nat32 = build32Exit == 0 && !build32.Contains("warning:") ? RunProc(exe32, "", workDir, out _) : "C-Compiler: " + build32;
                // 16 bits (no such target yet: a target with wordWidth 16 - the VM and natively)
                const string word16Source = """
                    word m = -1
                    word a = 40000
                    word wb[] = [65541, -1]
                    print(m + " " + Word.Bits + " " + (a + a) + " " + a * 3 + " " + (m >> 15) + " " + (m << 15) + " " + (m << 16) + " " + (a / 7) + " " + (-a) + " " + 70000!word + " " + wb[0] + " " + wb[1] + " " + (a > 30000))
                    #if word16
                    print("word16")
                    #endif
                    """;
                var target16 = TargetProfile.Host with { WordWidth = 16 };
                const string vm16 = "65535 16 14464 54464 1 32768 0 5714 25536 4464 5 65535 True\nword16\n";   // (what the C# VM printed with words of 16 bits)
                var generated16 = fire.Native.CppGenerator.GenerateSources(new Linker().CompileAndLink(new[] { word16Source }, null, null, VmExecutionMode.Release, null, target16), target16);
                Value.WordBits = 64;
                string exe16 = Path.Combine(workDir, "word16.bin");
                string build16 = NativeTestBuild.Build(NativeTestBuild.In(workDir), cxx, generated16, workDir, "word16", $"-pthread {NativeTestBuild.Opt} -Wall -Wextra {sanitize}", exe16, out int build16Exit);
                string nat16 = build16Exit == 0 && !build16.Contains("warning:") ? RunProc(exe16, "", workDir, out _) : "C-Compiler: " + build16;
                CheckNat("word: 16 Bit (ein Ziel mit wordWidth 16) - nativ wie ein 16-Bit-Rechner",
                    nat16 == vm16, $"  erwartet:\n{vm16}\n  nativ:\n{nat16}");
                CheckNat("word: 32 Bit (ein Ziel mit wordWidth 32 nativ) rechnet mit 32 Bit: Wrap, Schieben, vorzeichenlos",
                    nat32 == vm32, $"  erwartet:\n{vm32}\n  nativ:\n{nat32}");
            }

            // a value of 8 bytes (FIRE_COMPACT: int32, float32, 32-bit pointers): the same program as a 32-bit build, when the compiler can make one
            string probe = Path.Combine(workDir, "m32probe.c");
            File.WriteAllText(probe, "int main(void) { return sizeof(void*) == 4 ? 0 : 1; }\n");
            RunProc(fire.Native.ToolchainDef.CCompilerFor(cxx), $"-m32 \"{probe}\" -o \"{probe}.bin\"", workDir, out int probeExit);
            if (probeExit != 0 || RunProc($"{probe}.bin", "", workDir, out int probeRun) is var _ && probeRun != 0)
                Console.WriteLine("SKIP: 8-Byte-Value - der C-Compiler baut keine 32-Bit-Programme (gcc-multilib)");
            else
            {
                // (a target of 32-bit words: an 8-byte value needs a machine word of at most 32 bits)
                var compactTarget = TargetProfile.Host with { WordWidth = 32 };
                const string compactSource = """
                    #intwidth 32
                    #floatwidth 32
                    var a = 2147483647
                    a = a + 1
                    print(a)
                    var m = -2147483647 - 1
                    print(m / -1)
                    print(1 << 31)
                    int[8] t[] = [300, 2]
                    print(t[0])
                    class P { int x; float y; string s }
                    var p = new P()
                    p.x = 70000 * 70000
                    p.y = 1.1
                    p.s = "s" + p.x
                    print(p.x + " " + p.y + " " + p.s)
                    var sum = 0
                    for (var i = 0; i < 100000; i++) { sum += i * i }
                    print(sum)
                    print(5cm + 3mm)
                    var arr = [1, "x", 2.5]
                    print(arr[1] + arr.length)
                    class U { static Inc(ref int x) { x++ } }
                    int box = 1
                    U.Inc(box)
                    print(box)
                    """;
                string expectedCompact = vmOutput(compactSource);
                Value.SingleFloats = false; Value.Ints32 = false; Value.Ints16 = false;
                string compactC = Path.Combine(workDir, "compact.c");
                File.WriteAllText(compactC, fire.Native.CppGenerator.Generate(new Linker().CompileAndLink(new[] { compactSource }, null, null, null, null, compactTarget), compactTarget)
                    + "\nFIRE_STATIC_ASSERT(sizeof(Value) == 16 && sizeof(NValue) == 8, \"a value has 16 bytes, a narrow field 8\");\n");
                Value.SingleFloats = false; Value.Ints32 = false; Value.Ints16 = false;
                string compactBuild = RunProc(fire.Native.ToolchainDef.CCompilerFor(cxx), $"-m32 -std=c11 -O1 -Wall -Wextra -pthread \"{compactC}\" -I\"{workDir}\" -o \"{compactC}.bin\" -lm", workDir, out int compactExit);
                string compactOut = compactExit == 0 ? RunProc($"{compactC}.bin", "", workDir, out _) : compactBuild;
                CheckNat("32-Bit-Build (#intwidth 32, #floatwidth 32, 32-Bit-Zeiger): 16-Byte-Werte, schmale Felder - dasselbe wie die VM", compactExit == 0 && !compactBuild.Contains("warning") && compactOut.Replace("\r", "") == expectedCompact,
                    $"  erwartet (VM):\n{expectedCompact}\n  erhalten (32 Bit):\n{compactOut}");

                // ROADMAP 13.1: no limit of 65535 live objects, arrays and buffers (the slot in the payload, 28 bits of generation in the head), pointers into
                // arrays and buffers as counted boxes - checked and with #performance (no table: the address in the value; `ref a[i]` finds its typed array)
                const string slotSource = """
                    #intwidth 32
                    #floatwidth 32
                    class Node { int v
                        construct(int v) { this.v = v } }
                    class F {
                      static Sum(ref int first, int n) {
                        var t = 0
                        unsafe { int* p = &first
                          for (var i = 0; i < n; i++) { t = t + *p; p = p + 1 } }
                        return t
                      }
                      static Same(ref int a, ref int b) { unsafe { int* p = &a
                        int* q = &b
                        return (p + 1 == q) + " " + (q - p) } }
                      static Put(ref float x) { unsafe { float* p = &x
                        *p = 3 } }
                      static Bytes(ref byte first, int n) {
                        var t = 0
                        unsafe { byte* p = &first
                          for (var i = 0; i < n; i++) { t = t + *p; p = p + 1 } }
                        return t
                      }
                    }
                    // more than 65535 live objects at once
                    var all = new int[70000][1]
                    for (var i = 0; i < 70000; i++) { all[i][0] = i }
                    var total = 0
                    for (var i = 0; i < 70000; i++) { total = total + all[i][0] % 7 }
                    var nodes = new Node[70000]
                    for (var i = 0; i < 70000; i++) { nodes[i] = take new Node(i) }
                    var one = nodes[69999]
                    print("live: " + all.length + " " + total + " " + all[69999][0] + " " + one.v + " " + nodes[12345].v)
                    // generations past 4096: one slot made and destroyed again and again, a stale reference stays dead
                    var keep = new Node(1)
                    var stale = new Node(2)
                    delete stale
                    for (var i = 0; i < 5000; i++) { var t = new Node(i)
                      delete t }
                    try { print(stale.v) } catch (DestroyedException e) { print("stale: " + e.message) }
                    print("keep: " + keep.v + " " + (keep == keep))
                    // many pointers: every `p + 1` is a new box, the old ones come back
                    var a = new int[100]
                    for (var i = 0; i < 100; i++) { a[i] = i }
                    var s = 0
                    for (var k = 0; k < 2000; k++) { s = s + F.Sum(a[k % 50], 50) }
                    print("sum: " + s)
                    print("ptr: " + F.Same(a[3], a[4]))
                    float fs[] = new float[3]
                    F.Put(fs[1])
                    print("typed: " + fs[1])
                    var buf = new byte[4]
                    buf[0] = 1; buf[1] = 2; buf[2] = 3; buf[3] = 4
                    print("bytes: " + F.Bytes(buf[1], 3))
                    delete a
                    try { print(F.Sum(a[0], 1)) } catch (DestroyedException e) { print("gone: " + e.message) }
                    """;
                foreach (bool perf in new[] { false, true })
                {
                    string source = perf ? "#performance\n" + string.Join("\n", slotSource.Split('\n').Where(l => !l.Contains("stale") && !l.Contains("gone"))) : slotSource;
                    string expected = vmOutput(source);
                    Value.SingleFloats = false; Value.Ints32 = false; Value.Ints16 = false;
                    string slotC = Path.Combine(workDir, perf ? "compactslotsperf.c" : "compactslots.c");
                    File.WriteAllText(slotC, fire.Native.CppGenerator.Generate(new Linker().CompileAndLink(new[] { source }, null, null, null, null, compactTarget), compactTarget)
                        + "\nFIRE_STATIC_ASSERT(sizeof(Value) == 16 && sizeof(NValue) == 8, \"a value has 16 bytes, a narrow field 8\");\n");
                    Value.SingleFloats = false; Value.Ints32 = false; Value.Ints16 = false;
                    string slotBuild = RunProc(fire.Native.ToolchainDef.CCompilerFor(cxx), $"-m32 -std=c11 -O1 -Wall -Wextra -pthread \"{slotC}\" -I\"{workDir}\" -o \"{slotC}.bin\" -lm", workDir, out int slotExit);
                    string slotOut = slotExit == 0 ? RunProc($"{slotC}.bin", "", workDir, out _) : slotBuild;
                    CheckNat($"32-Bit-Build{(perf ? " (#performance)" : "")}: mehr als 65535 lebende Objekte/Arrays, Generationen ueber 4096, Zeiger in Arrays und Puffer (ohne Kaesten)",
                        slotExit == 0 && !slotBuild.Contains("warning") && slotOut.Replace("\r", "") == expected && expected.StartsWith("live: 70000"),
                        $"  erwartet (VM):\n{expected}\n  erhalten (32 Bit):\n{slotOut}");
                }
            }
        }

        // Zielprofil
        var esp = TargetProfile.Esp32;
        var espLinked = new Linker().CompileAndLink(new[] { "print(0.1 + 0.2)" }, null, null, null, null, esp);
        CheckNat("Zielprofil esp32: float ist 32 Bit, wenn nichts anderes gesagt wird", espLinked.FloatWidth == 32);
        CheckNat("Zielprofil: #floatwidth gewinnt gegen das Ziel, -f gegen beides",
            new Linker().CompileAndLink(new[] { "#floatwidth 64\nprint(1)" }, null, null, null, null, esp).FloatWidth == 64
            && new Linker().CompileAndLink(new[] { "#floatwidth 64\nprint(1)" }, null, null, null, 32, TargetProfile.Windows).FloatWidth == 32
            && new Linker().CompileAndLink(new[] { "print(1)" }, null, null, null, null, TargetProfile.Windows).FloatWidth == 64);
        try { new Linker().CompileAndLink(new[] { "#import \"graphics\"\nprint(1)" }, null, null, null, null, esp); CheckNat("Zielprofil: nicht verfuegbare Bibliothek wird abgelehnt", false); }
        catch (NotSupportedException ex) { CheckNat("Zielprofil: nicht verfuegbare Bibliothek wird abgelehnt", ex.Message.Contains("'graphics'") && ex.Message.Contains("'esp32'"), ex.Message); }
        CheckNat("Zielprofil: Windows erlaubt die Grafik, esp32 hat Devices/IO/Time", TargetProfile.Windows.HasImport("graphics") && esp.HasImport("devices") && esp.HasImport("io") && esp.HasImport("time") && !esp.HasImport("ui"));
        CheckNat("Zielprofil: Namen und Host", TargetProfile.TryGet("ESP32", out var byName) && byName == esp && !TargetProfile.TryGet("amiga", out _) && TargetProfile.All.Contains(TargetProfile.Host));
        // the text console (#import "console", docs/stdlib/console.md): without a console (redirected) plain text and the characters of standard input as keys -
        // the VM and the native program alike; in a terminal (a pseudo-terminal through `script`) the escape sequences and the keys of their sequences
        if (TestSelected("console") && TestResume.Pending("console", NatUnitKey("console")))
        {
            // (a unit of FIRE_TESTS_RESUME/FIRE_TESTS_SHARD: TestResume.cs)
            using var resumeUnit5 = TestResume.Unit("console", NatUnitKey("console"));
            const string conSource = """
                #import "console"
                Console.WriteLine("console " + Console.IsConsole() + " " + Console.Width() + "x" + Console.Height())
                Console.SetColor(ConsoleColor.Yellow, ConsoleColor.DarkBlue)
                Console.Write("gelb ")
                Console.ResetColor()
                Console.WriteLine("normal äöü ╔═╗")
                Console.WriteAt(5, 3, "da")
                Console.WriteLine(" " + Console.Column() + "," + Console.Row())
                Console.Clear()
                Console.ShowCursor(false)
                while (Console.KeyAvailable()) {
                    var k = Console.ReadKey()
                    Console.WriteLine(k.ToString() + " " + k.Key + " " + k.Control)
                }
                Console.WriteLine(Console.ReadKey())
                """;
            const string conInput = "a\u0001\tZä\r\nx";
            const string conExpected = "console False 80x25\ngelb normal äöü ╔═╗\nda 0,0\na Character False\nCtrl+a Character True\nTab Tab False\nZ Character False\n"
                + "ä Character False\nEnter Enter False\nx Character False\nundefined\n";
            var conLines = new List<string>();
            try
            {
                // (in `fire` with its stdin and stdout redirected: no console)
                conLines.AddRange(CVmTest.Run(new[] { conSource }, VmExecutionMode.Release, input: conInput).Printed.SelectMany(p => p.Split('\n')));
            }
            catch (Exception ex) { conLines.Add("AUSNAHME: " + ex.Message); }
            string conVm = string.Concat(conLines.Select(l => l + "\n"));
            CheckNat("console: in der VM ohne Konsole - Text wie geschrieben, Farben/Cursor/Clear ohne Wirkung, Tasten aus der Standardeingabe (Strg, Tab, UTF-8, \\r\\n)",
                conVm == conExpected, $"  erwartet:\n{conExpected}\n  VM:\n{conVm}");
            // (built like the cases "nativ == VM")
            string? conExe = null, conBuild = "";
            try
            {
                var conGen = fire.Native.CppGenerator.GenerateSources(new Linker().CompileAndLink(new[] { conSource }, null, null, VmExecutionMode.Release));
                string exe = Path.Combine(workDir, "console.bin");
                conBuild = NativeTestBuild.Build(NativeTestBuild.In(workDir), cxx, conGen, workDir, "console", $"-pthread {NativeTestBuild.Opt} -Wall -Wextra", exe, out int conExit);
                if (conExit == 0 && !conBuild.Contains("warning:")) conExe = exe;
            }
            catch (Exception ex) { conBuild = ex.ToString(); }
            string conNat = conExe == null ? "C-Compiler: " + conBuild : TestProcess.Run(conExe, "", workDir, conInput).Stdout;
            CheckNat("console: nativ umgeleitet - dieselbe Ausgabe und dieselben Tasten wie die VM", conExe != null && conNat == conExpected, $"  erwartet:\n{conExpected}\n  nativ:\n{conNat}");
            // a terminal: `script` gives the program a pseudo-terminal (its input is what we write, its echo is in the output)
            string? scriptTool = OperatingSystem.IsLinux() ? fire.Native.ToolchainSetup.FindProgram("script") : null;
            if (conExe == null || scriptTool == null) Console.WriteLine("SKIP: console im Terminal - kein `script` (util-linux) oder das Programm baute nicht");
            else
            {
                const string termSource = """
                    #import "console"
                    Console.WriteLine("console " + Console.IsConsole())
                    Console.SetColor(ConsoleColor.Yellow, ConsoleColor.DarkBlue)
                    Console.Write("gelb")
                    Console.ResetColor()
                    Console.SetCursor(5, 3)
                    Console.WriteLine("da")
                    for (var i = 0; i < 6; i++) {
                        var k = Console.ReadKey()
                        Console.WriteLine("key " + k.ToString() + " " + k.Key)
                    }
                    """;
                string termOut = "";
                try
                {
                    var termGen = fire.Native.CppGenerator.GenerateSources(new Linker().CompileAndLink(new[] { termSource }, null, null, VmExecutionMode.Release));
                    string exe = Path.Combine(workDir, "console-term.bin");
                    string termLog = NativeTestBuild.Build(NativeTestBuild.In(workDir), cxx, termGen, workDir, "console-term", $"-pthread {NativeTestBuild.Opt} -Wall -Wextra", exe, out int termExit);
                    termOut = termExit != 0 ? "C-Compiler: " + termLog
                        : TestProcess.Run(scriptTool, $"-qec \"{exe}\" /dev/null", workDir, "x\u001b[A\u001b[15~\u001b[1;5C\u001bq\r", new Dictionary<string, string?> { ["TERM"] = "xterm" }, 30_000).Stdout;
                }
                catch (Exception ex) { termOut = ex.ToString(); }
                string[] termWant = { "console True", "\u001b[93m\u001b[44mgelb\u001b[0m", "\u001b[4;6Hda", "key x Character", "key Up Up", "key F5 F5", "key Ctrl+Right Right", "key Alt+q Character", "key Enter Enter" };
                CheckNat("console: im Terminal - ANSI-Farben und -Cursor, Tasten aus ihren Folgen (Pfeil, F5, Strg+Pfeil, Alt+Taste, Enter)",
                    termWant.All(termOut.Contains), $"  erwartet (enthalten): {string.Join(" | ", termWant.Select(w => w.Replace("\u001b", "ESC")))}\n  Terminal:\n{termOut.Replace("\u001b", "ESC")}");
            }
            resumeUnit5.Done();
        }
        // dos16 (ROADMAP 13.3): DOS in real mode - the profile, the generated C, and with Open Watcom and DOSBox-X on the machine: built and run like the VM
        {
            CheckNat("dos16: Zielprofil (Plattform dos16, Toolchain watcom, int/float 32, word 16, Threads ueber den eigenen Scheduler)", TargetProfile.TryGet("dos16", out var dos16)
                && dos16.Native.Platform == "dos16" && dos16.Native.Toolchain == "watcom" && dos16.IntWidth == 32 && dos16.FloatWidth == 32 && dos16.WordWidth == 16 && dos16.Native.SupportsThreads
                && fire.Native.ToolchainDef.BuiltIn.ContainsKey("watcom") && !fire.Native.ToolchainDef.BuiltIn["watcom"].CompilesCpp);
            const string dosSource = """
                class Animal { string name
                    construct(string n) { this.name = n }
                    string Speak() { return this.name + " says hi" } }
                var a = new Animal("Rex")
                print(a.Speak())
                int big = 100000
                print(big * 3 + " " + (7.5 / 2) + " " + 5cm + 3mm)
                var list = [3, 1, 2]
                var sum = 0
                foreach (x in list) { sum += x }
                print(sum + " " + list.length)
                try { throw new Exception("boom") } catch (e) { print("caught " + e.message) }
                word w = 65535
                print(w + 1)
                var done = 0
                fire { var t = 0
                  for (var i = 0; i < 2000; i++) { t = t + i }
                  sync global { done = t } }
                while (done == 0) { sync globals }
                print("thread " + done)
                """;
            var dosLinked = new Linker().CompileAndLink(new[] { dosSource }, null, null, VmExecutionMode.Release, null, dos16);
            string dosC = fire.Native.CppGenerator.Generate(dosLinked, dos16);
            CheckNat("dos16: das C nennt die Plattform dos16 und die Breiten", dosC.Contains("platform/dos16/fire_platform.h") && dosC.Contains("#define FIRE_INT32 1") && dosC.Contains("#define FIRE_WORD_BITS 16"));
            string dosVm = CVmTest.TargetOutput(dosLinked);
            var watcom = new fire.Native.WatcomToolchain();
            string? dosbox = fire.Native.ToolchainSetup.FindProgram("dosbox-x");
            if (watcom.Locate(fire.Native.ToolchainDef.BuiltIn["watcom"]) == null || dosbox == null)
                Console.WriteLine("SKIP: dos16 bauen und laufen lassen - Open Watcom (owcc) oder DOSBox-X fehlt");
            else
            {
                // builds the program as PROG.EXE in its own folder (drive C:) and runs it in DOSBox-X (at most 2 minutes): its output, or the log of the build
                // (another `outputFile`: the program runs on the screen, not redirected, and writes what it found into that file)
                (bool Ok, string Text) BuildAndRunDos(string source, string folder, string outputFile = "OUT.TXT", string dosboxSettings = "", TargetProfile? target = null)
                {
                    string dir = Path.Combine(workDir, folder);
                    Directory.CreateDirectory(dir);
                    var built = fire.Compiler.NativeBuilder.Build(new[] { source }, new fire.Native.NativeConfig(), target ?? dos16, fire.Native.ToolchainDef.BuiltIn["watcom"], Path.Combine(dir, "PROG.EXE"), VmExecutionMode.Release);
                    if (!built.Ok) return (false, built.Log);
                    // (DOSBox-X's own video and audio drivers: no window, no sound)
                    var env = new Dictionary<string, string?> { ["SDL_VIDEODRIVER"] = "dummy", ["SDL_AUDIODRIVER"] = "dummy" };
                    var run = TestProcess.Run(dosbox, $"-silent -nogui -nomenu -set \"cpu cycles=max\" {dosboxSettings.Replace("{dir}", dir)} -c \"mount c {dir}\" -c \"c:\" -c \"{(outputFile == "OUT.TXT" ? "PROG.EXE > OUT.TXT" : "PROG.EXE")}\" -c \"exit\"", dir, null, env, 120_000);
                    string outFile = Directory.GetFiles(dir).FirstOrDefault(f => Path.GetFileName(f).Equals(outputFile, StringComparison.OrdinalIgnoreCase)) ?? "";
                    string text = outFile.Length > 0 ? File.ReadAllText(outFile).Replace("\r", "") : "(keine Ausgabe)";
                    return (true, run.Exit == -1 ? text + "(DOSBox-X nach 2 Minuten beendet)" : text);
                }
                var dosRun = BuildAndRunDos(dosSource, "dos16");
                CheckNat("dos16: mit Open Watcom gebaut, in DOSBox-X gelaufen - dieselbe Ausgabe wie die VM (Objekte, Strings, Einheiten, Ausnahmen, word 16 Bit, ein fire-Thread)",
                    dosRun.Ok && dosRun.Text == dosVm && dosVm.Length > 0, $"  VM:\n{dosVm}\n  DOS:\n{dosRun.Text}");

                // `--stats` (docs/ROADMAP.md section 16): the map of Open Watcom, the generated code by module, the typing (a `defined int` method has a C interface),
                // the memory the program needs to load
                {
                    string statsDir = Path.Combine(workDir, "dos16stats");
                    Directory.CreateDirectory(statsDir);
                    string statsExe = Path.Combine(statsDir, "PROG.EXE");
                    var statsBuilt = fire.Compiler.NativeBuilder.Build(new[] { "class Calc {\n    static defined int Twice(defined int x) { return x * 2 }\n}\nprint(Calc.Twice(21))" },
                        new fire.Native.NativeConfig { Stats = true }, dos16, fire.Native.ToolchainDef.BuiltIn["watcom"], statsExe, VmExecutionMode.Release);
                    string statsText = File.Exists(statsExe + ".stats.txt") ? File.ReadAllText(statsExe + ".stats.txt") : "";
                    string statsJson = File.Exists(statsExe + ".stats.json") ? File.ReadAllText(statsExe + ".stats.json") : "";
                    CheckNat("--stats: dos16 - Code nach Herkunft aus der Watcom-Map, Typisierung (C-Schnittstelle einer defined-int-Methode), Speicher zum Laden",
                        statsBuilt.Ok && statsText.Contains("fire: program") && statsText.Contains("runtime") && statsText.Contains("C library") && statsText.Contains(", loaded ")
                        && statsText.Contains("C interface 1/") && statsJson.Contains("\"Calc.Twice\"") && statsBuilt.Log.Contains("Statistics (dos16)"),
                        statsBuilt.Log.Length > 3000 ? statsBuilt.Log[^3000..] : statsBuilt.Log);
                }

                // files with the paths of DOS ("X:\FOLDER\FILE.EXT", "/" too, upper or lower case alike, what DOS gives back in upper case) - a fixed expectation:
                // the VM works with the paths of this machine
                const string dosFiles = """
                    #import "io"
                    #import "filesystem"
                    print(IO.Path.DirectorySeparatorChar)
                    IO.Directory.CreateDirectory("c:\\fire\\sub")
                    IO.File.WriteAllText("C:\\Fire\\Sub\\hello.txt", "Hallo DOS")
                    print(IO.File.ReadAllText("c:/fire/sub/HELLO.TXT"))
                    print(IO.File.Exists("C:\\FIRE\\SUB\\HELLO.TXT") + " " + IO.Directory.Exists("c:\\fire"))
                    print(IO.Path.GetFullPath("c:\\fire\\sub\\..\\x.txt"))
                    print(IO.Path.Combine("C:\\FIRE", "A.TXT") + " " + IO.Path.GetDirectoryName("C:\\FIRE\\SUB\\HELLO.TXT") + " " + IO.Path.GetFileName("c:\\fire\\b.dat"))
                    foreach (f in IO.Directory.GetFiles("c:\\fire\\sub")) { print(f) }
                    IO.File.Copy("C:\\FIRE\\SUB\\HELLO.TXT", "C:\\FIRE\\COPY.TXT")
                    print(IO.File.Size("c:\\fire\\copy.txt"))
                    IO.Directory.SetCurrentDirectory("C:\\FIRE")
                    print(IO.Directory.GetCurrentDirectory())
                    try { IO.File.ReadAllText("C:\\NOPE\\X.TXT") } catch (e) { print("error: " + e.message) }
                    IO.Directory.SetCurrentDirectory("C:\\")
                    IO.Directory.Delete("C:\\FIRE", true)
                    print(IO.Directory.Exists("C:\\FIRE"))
                    """;
                const string dosFilesExpected = "\\\nHallo DOS\nTrue True\nC:\\FIRE\\X.TXT\nC:\\FIRE\\A.TXT C:\\FIRE\\SUB b.dat\nC:\\FIRE\\SUB\\HELLO.TXT\n9\nC:\\FIRE\n"
                    + "error: Could not find a part of the path 'C:\\NOPE\\X.TXT'.\nFalse\n";
                var filesRun = BuildAndRunDos(dosFiles, "dos16files");
                CheckNat("dos16: Dateien mit DOS-Pfaden (Laufwerk, \\ und /, Gross/Klein gleich, was DOS liefert gross; anlegen, lesen, kopieren, auflisten, Verzeichnis wechseln, loeschen)",
                    filesRun.Ok && filesRun.Text == dosFilesExpected, $"  erwartet:\n{dosFilesExpected}\n  DOS:\n{filesRun.Text}");

                // more than 64 KB in one array or buffer (the huge model: halloc, copies across segment ends)
                const string dosBig = """
                    #import "io"
                    #import "filesystem"
                    class Big {
                        static Bytes() {
                            var buf = new byte[100000]
                            for (var i = 0; i < 100000; i++) { buf[i] = i % 251 }
                            var c = copy buf
                            var t = 0
                            for (var i = 0; i < 100000; i += 997) { t = t + c[i] }
                            print(c.length + " " + c[99999] + " " + c[65536] + " " + t)
                            IO.File.WriteAllBytes("C:\\BIG.DAT", c)
                            var back = IO.File.ReadAllBytes("C:\\BIG.DAT")
                            print(back.length + " " + back[99999] + " " + back[65536] + " " + back[70001])
                        }
                        static Ints() {
                            int big[] = new int[30000]
                            big[29999] = 42
                            var bc = copy big
                            print(bc[29999] + " " + bc.length + " " + bc[17000])
                        }
                        static Values() {
                            var plain = new any[9000]
                            plain[8999] = "end"
                            var pc = copy plain
                            print(pc[8999])
                        }
                    }
                    Big.Bytes()
                    Big.Ints()
                    Big.Values()
                    """;
                const string dosBigExpected = "100000 101 25 13344\n100000 101 25 223\n42 30000 undefined\nend\n";
                var bigRun = BuildAndRunDos(dosBig, "dos16big");
                CheckNat("dos16: Arrays und Puffer ueber 64 KB (100000 Bytes, int[30000], any[9000]; kopieren ueber Segmentgrenzen, eine Datei mit 100000 Bytes)",
                    bigRun.Ok && bigRun.Text == dosBigExpected, $"  erwartet:\n{dosBigExpected}\n  DOS:\n{bigRun.Text}");

                // scene3d in the huge model (its buffers over s3d_size and the allocator of the runtime): a small picture - the conventional memory holds the
                // program (about 500 KB with scene3d), the framebuffer, the depth buffer and the camera's pixels only for small frames. Counted, not summed: the
                // x87 rounds a colour here and there differently from the SSE of the VM (by 1), the coverage is the same
                const string dosScene3d = """
                    #import "graphics"
                    #import "scene3d"
                    // the pixels that are not the background, above and below the first 64 KB of the picture (row 102 of 640 bytes)
                    class H {
                        static string Count(Framebuffer f) {
                            var b = f.ReadBytes()
                            var top = 0
                            var low = 0
                            for (var i = 0; i < b.length; i += 4) {
                                if (b[i] != 24 || b[i + 1] != 42 || b[i + 2] != 61) { if (i < 65536) { top++ } else { low++ } }
                            }
                            return top + " " + low
                        }
                    }
                    var frame = new Framebuffer({SIZE})
                    var scene = new Scene()
                    var box = new Object(Template.Box, 0m, 0.5m, 0m, 1m, 1m, 1m)
                    box.RotationY = 30deg
                    scene.Add(box, "crate")
                    var floor = new Object(Template.Plane, 0m, 0m, 0m, 6m, 0m, 6m)
                    floor.Texture = Texture.Checker(32, 4)
                    scene.Add(floor, "floor")
                    scene.Add(Light.Directional(-0.4, -1.0, -0.6))
                    var cam = new Camera(frame)
                    cam.Y = 1.5m
                    cam.Z = 3m
                    cam.RotationX = -25deg
                    scene.Add(cam)
                    cam.Render()
                    print("drawn " + H.Count(frame))
                    var px = frame.ReadBytes()
                    print("corner " + px[0] + "," + px[1] + "," + px[2] + "," + px[3])
                    scene.Remove(box)
                    cam.Render()
                    print("floor " + H.Count(frame))
                    """;
                var s3dRun = BuildAndRunDos(dosScene3d.Replace("{SIZE}", "64, 48"), "dos16s3d");
                const string dosScene3dExpected = "drawn 2059 0\ncorner 24,42,61,255\nfloor 1978 0\n";
                CheckNat("dos16: scene3d - eine Szene mit Box, Boden und Licht gerendert (wie in der VM)",
                    s3dRun.Ok && s3dRun.Text == dosScene3dExpected, $"  erwartet:\n{dosScene3dExpected}\n  DOS:\n{s3dRun.Text}");

                // the hardware (#import "machine"): CPU and CPUID, the BIOS data area and the video memory (physical addresses), PCI over 32-bit ports, the CMOS
                // clock, IRQ 0 (the clock, chained: DOS keeps its time), the speaker; the arguments and an address out of reach are exceptions
                const string dosMachine = """
                    #import "machine"
                    print("cpu " + Machine.Cpu.Level() + " " + Machine.Cpu.Vendor())
                    print("com1 " + Machine.Memory.Peek16(0x400))
                    Machine.Memory.Poke16(0xB8000 + 158, 0x4E41)
                    print("screen " + Machine.Memory.Peek16(0xB8000 + 158) + " " + Machine.Memory.Read(0xB8000 + 158, 2)[0])
                    print("pci " + Machine.Pci.Present(0, 0, 0) + " " + ((Machine.Pci.Read32(0, 0, 0, 0) & 65535) != 65535))
                    var c = Machine.Cmos.Clock()
                    print("year ok " + (c[0] >= 2020) + " month ok " + (c[1] >= 1 && c[1] <= 12))
                    var irq = new Machine.Irq(0, true)
                    print("irq0 " + (irq.Wait(1000) > 0))
                    irq.Close()
                    try { new Machine.Irq(16) } catch (ArgumentException e) { print("arg: " + e.message) }
                    try { Machine.Memory.Peek8(0x200000) } catch (Machine.MachineException e) { print("far: " + e.message) }
                    Machine.Speaker.Tone(440)
                    Machine.Speaker.Off()
                    print("done")
                    """;
                const string dosMachineExpected = "cpu 586 GenuineIntel\ncom1 1016\nscreen 20033 65\npci True True\nyear ok True month ok True\nirq0 True\n"
                    + "arg: An IRQ is 0 to 15, not 16.\nfar: The address 2097152 cannot be reached from here.\ndone\n";
                var machRun = BuildAndRunDos(dosMachine, "dos16mach");
                CheckNat("dos16: machine - CPU/CPUID, physischer Speicher (BIOS-Daten, Videospeicher), PCI ueber 32-Bit-Ports, CMOS-Uhr, IRQ 0 gezaehlt, Lautsprecher",
                    machRun.Ok && machRun.Text == dosMachineExpected, $"  erwartet:\n{dosMachineExpected}\n  DOS:\n{machRun.Text}");

                // DOS (#import "dos": interrupts with registers, memory, vectors, a buffer in DS:SI) and XMS (#import "xms": a block of 200 KB, 70001 bytes there and back -
                // more than a segment, an odd length and offset)
                const string dosXms = """
                    #import "dos"
                    #import "xms"
                    var v = Dos.Version()
                    print("dos " + (v[0] >= 5))
                    var r = Dos.Interrupt(0x1A, new DosRegisters(0))
                    print("ticks " + (r.cx * 65536 + r.dx > 0))
                    var seg = Dos.Alloc(256)
                    print("alloc " + (seg > 0) + " largest " + (Dos.LargestFree() > 1000))
                    Dos.Free(seg)
                    try { Dos.Free(1) } catch (DosException e) { print("free: " + (e.message.Length > 0)) }
                    print("vector " + (Dos.GetVector(0x21) != 0))
                    var name = new byte[64]
                    var rr = new DosRegisters(0x4700)
                    rr.ds = Dos.SegmentOf(name)
                    rr.si = Dos.OffsetOf(name)
                    Dos.Call(rr)
                    print("cwd carry " + rr.Carry)
                    print("xms " + Xms.Available() + " " + Xms.Version()[0] + " free " + (Xms.FreeKb() > 1000))
                    var b = new XmsBlock(200)
                    var data = new byte[70001]
                    for (var i = 0; i < data.length; i++) { data[i] = i % 253 }
                    b.Write(100001, data)
                    var back = b.Read(100001, 70001)
                    var same = true
                    for (var i = 0; i < back.length; i += 7) { if (back[i] != data[i]) { same = false } }
                    print("block " + b.Kb + " same " + same + " last " + back[70000] + " " + data[70000])
                    try { b.Read(204000, 1000) } catch (ArgumentException e) { print("outside") }
                    b.Close()
                    try { new XmsBlock(9999999) } catch (ArgumentException e) { print("too big") }
                    print("done")
                    """;
                const string dosXmsExpected = "dos True\nticks True\nalloc True largest True\nfree: True\nvector True\ncwd carry False\nxms True 3 free True\n"
                    + "block 200 same True last 172 172\noutside\ntoo big\ndone\n";
                var xmsRun = BuildAndRunDos(dosXms, "dos16xms");
                CheckNat("dos16: dos (Interrupts mit Registern, Speicher, Vektoren, Puffer in DS:SI) und xms (Block von 200 KB, 70001 Bytes hin und zurueck, ungerade Laenge)",
                    xmsRun.Ok && xmsRun.Text == dosXmsExpected, $"  erwartet:\n{dosXmsExpected}\n  DOS:\n{xmsRun.Text}");

                // the protected mode (#import "x86"): a routine with 32 bits (registers in and out, memory above 1 MB, a buffer of the program by its linear address),
                // ReadLinear/WriteLinear across the first megabyte, the screen of the text mode read through its linear address - and back in real mode the program goes on
                const string dosX86 = """
                    #import "x86"
                    print(X86.Mode() + " " + X86.CanSwitch() + " a20 " + X86.EnableA20())
                    // mov [eax], ebx / mov edx, [eax] / add edx, ecx / mov eax, edx / mov ecx, 12345678h / ret
                    byte code[] = new byte[14]
                    var bytes = [137, 24, 139, 16, 1, 202, 137, 208, 185, 120, 86, 52, 18, 195]
                    for (var i = 0; i < 14; i++) { code[i] = bytes[i] }
                    var r = new X86Registers(1179648)
                    r.ebx = 1000000
                    r.ecx = 23
                    X86.RunProtected(code, r)
                    print("eax " + r.eax + " ecx " + r.ecx)
                    byte data[] = new byte[70000]
                    for (var i = 0; i < data.length; i++) { data[i] = i % 251 }
                    X86.WriteLinear(2097152 + 3, data)
                    var back = X86.ReadLinear(2097152 + 3, 70000)
                    var same = true
                    for (var i = 0; i < back.length; i += 13) { if (back[i] != data[i]) { same = false } }
                    print("above 1 MB " + same + " " + back[69999] + " " + data[69999])
                    // the routine fills a buffer of the program: mov ecx, 8 / mov al, 'A' / rep stosb (edi = the buffer) / ret
                    byte fill[] = new byte[8]
                    byte code2[] = new byte[10]
                    var bytes2 = [185, 8, 0, 0, 0, 176, 65, 243, 170, 195]
                    for (var i = 0; i < 10; i++) { code2[i] = bytes2[i] }
                    var r2 = new X86Registers()
                    r2.edi = X86.LinearOf(fill)
                    X86.RunProtected(code2, r2)
                    print("filled " + fill[0] + " " + fill[7])
                    try { X86.LinearOf(fill, 99) } catch (ArgumentException e) { print("outside") }
                    print("long " + X86.CanLongMode())
                    try { X86.RunLong(code2, r2) } catch (X86Exception e) { print("no long: " + e.message) }
                    print("done")
                    """;
                const string dosX86Expected = "real True a20 True\neax 1000023 ecx 305419896\nabove 1 MB True 221 221\nfilled 65 65\noutside\nlong False\n"
                    + "no long: This processor has no long mode (64 bit).\ndone\n";
                var x86Run = BuildAndRunDos(dosX86, "dos16x86");
                CheckNat("dos16: x86 - eine Routine im 32-Bit-Protected-Mode (Register hin und zurueck, Speicher ueber 1 MB, ein Puffer des Programms) und zurueck im Real Mode",
                    x86Run.Ok && x86Run.Text == dosX86Expected, $"  erwartet:\n{dosX86Expected}\n  DOS:\n{x86Run.Text}");

                // the long mode needs a 64-bit processor (DOSBox-X has none): the two stubs of the x86 package under QEMU, booted without DOS
                // (packages/x86/native/tools/test_boot.asm) - the registers of a 32-bit routine and of a 64-bit one (EFER with LME and LMA, a 64-bit register)
                string? qemu = fire.Native.ToolchainSetup.FindProgram("qemu-system-x86_64"), nasm = fire.Native.ToolchainSetup.FindProgram("nasm");
                string? x86Tools = null;
                for (var dirInfo = new DirectoryInfo(AppContext.BaseDirectory); dirInfo != null && x86Tools == null; dirInfo = dirInfo.Parent)
                    if (File.Exists(Path.Combine(dirInfo.FullName, "packages", "x86", "native", "tools", "test_boot.asm"))) x86Tools = Path.Combine(dirInfo.FullName, "packages", "x86", "native", "tools");
                if (qemu == null || nasm == null || x86Tools == null) Console.WriteLine("(x86 unter QEMU: uebersprungen - qemu-system-x86_64, nasm oder packages/x86/native/tools fehlen)");
                else
                {
                    string qdir = Path.Combine(workDir, "x86qemu");
                    Directory.CreateDirectory(qdir);
                    var a1 = TestProcess.Run(nasm, $"-f bin \"{Path.Combine(x86Tools, "x86_protected.asm")}\" -o \"{Path.Combine(qdir, "protected.bin")}\"", qdir);
                    var a2 = TestProcess.Run(nasm, $"-f bin \"{Path.Combine(x86Tools, "x86_long.asm")}\" -o \"{Path.Combine(qdir, "long.bin")}\"", qdir);
                    var a3 = TestProcess.Run(nasm, $"-f bin -i \"{qdir}/\" \"{Path.Combine(x86Tools, "test_boot.asm")}\" -o \"{Path.Combine(qdir, "boot.img")}\"", qdir);
                    string serial = Path.Combine(qdir, "serial.txt");
                    if (File.Exists(serial)) File.Delete(serial);
                    if (a1.Exit == 0 && a2.Exit == 0 && a3.Exit == 0)
                    {
                        using (var img = File.OpenWrite(Path.Combine(qdir, "boot.img"))) img.SetLength(Math.Max(img.Length, 65536));
                        TestProcess.Run(qemu, $"-drive format=raw,file=\"{Path.Combine(qdir, "boot.img")}\" -serial file:\"{serial}\" -display none -device isa-debug-exit,iobase=0xf4,iosize=0x04 -no-reboot", qdir, timeoutMs: 60000);
                    }
                    string said = File.Exists(serial) ? File.ReadAllText(serial).Trim() : $"(nichts)\n{a1.Stderr}{a2.Stderr}{a3.Stderr}";
                    CheckNat("x86 unter QEMU ohne DOS: der Stub des Protected Mode (eine 32-Bit-Routine, Speicher ueber 1 MB) und der des Long Mode (EFER: LME und LMA, ein 64-Bit-Register) und zurueck",
                        said == "000F4257 12345678 00000005 00000500 00000001 OK", said);
                }

                // FreeDOS under QEMU (a 64-bit processor, which DOSBox-X does not have): the image of FreeDOS 1.4 (FD14-LiteUSB, FD14LITE.img) from FIRE_FREEDOS_IMAGE or
                // ~/.cache/fire/freedos/FD14LITE.img, mtools to put the program in; CONFIG without EMM386 (the processor stays in real mode), AUTOEXEC runs PROG.EXE > OUT.TXT
                // and QUIT.COM (out to the isa-debug-exit of QEMU: B0 00 E6 F4 C3)
                string freedosImage = Environment.GetEnvironmentVariable("FIRE_FREEDOS_IMAGE") is { Length: > 0 } fdi ? fdi
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "fire", "freedos", "FD14LITE.img");
                string? mcopy = fire.Native.ToolchainSetup.FindProgram("mcopy"), mtype = fire.Native.ToolchainSetup.FindProgram("mtype");
                (bool Ok, string Text)? BuildAndRunFreeDos(string source, string folder)
                {
                    if (qemu == null || mcopy == null || mtype == null || !File.Exists(freedosImage)) return null;
                    string dir = Path.Combine(workDir, folder);
                    Directory.CreateDirectory(dir);
                    var built = fire.Compiler.NativeBuilder.Build(new[] { source }, new fire.Native.NativeConfig(), dos16, fire.Native.ToolchainDef.BuiltIn["watcom"], Path.Combine(dir, "PROG.EXE"), VmExecutionMode.Release);
                    if (!built.Ok) return (false, built.Log);
                    string img = Path.Combine(dir, "disk.img");
                    File.Copy(freedosImage, img, true);
                    File.WriteAllText(Path.Combine(dir, "FDCONFIG.SYS"), "FILES=40\r\nBUFFERS=20\r\nDOS=HIGH\r\nDEVICE=\\FREEDOS\\BIN\\HIMEMX.EXE\r\nSHELL=\\COMMAND.COM \\ /E:1024 /P=\\FDAUTO.BAT\r\n");
                    File.WriteAllText(Path.Combine(dir, "FDAUTO.BAT"), "@echo off\r\nC:\r\ncd \\\r\nPROG.EXE > OUT.TXT\r\nQUIT.COM\r\n");
                    File.WriteAllBytes(Path.Combine(dir, "QUIT.COM"), new byte[] { 0xB0, 0x00, 0xE6, 0xF4, 0xC3 });
                    string rc = Path.Combine(dir, "mtoolsrc");
                    File.WriteAllText(rc, $"drive c: file=\"{img}\" offset=32256\nmtools_skip_check=1\n");
                    var env = new Dictionary<string, string?> { ["MTOOLSRC"] = rc };
                    var put = TestProcess.Run(mcopy, "-o FDCONFIG.SYS FDAUTO.BAT QUIT.COM PROG.EXE c:/", dir, environment: env);
                    if (put.Exit != 0) return (false, "mcopy: " + put.Stderr);
                    var run = TestProcess.Run(qemu, $"-drive format=raw,file=\"{img}\" -display none -device isa-debug-exit,iobase=0xf4,iosize=0x04 -no-reboot -m 64", dir, timeoutMs: 120000);
                    var got = TestProcess.Run(mtype, "c:/OUT.TXT", dir, environment: env);
                    string text = got.Exit == 0 ? got.Stdout.Replace("\r", "") : "(keine Ausgabe)";
                    return (true, run.Exit == -1 ? text + "(QEMU nach 2 Minuten beendet)" : text);
                }
                // the long mode from a fire program under DOS: a 64-bit routine (EFER: LME and LMA, a 64-bit register), and back
                const string dosLong = """
                    #import "x86"
                    print(X86.Mode() + " " + X86.CanSwitch() + " long " + X86.CanLongMode())
                    // mov ecx, C0000080h / rdmsr / mov ebx, eax / mov r8, 100000005h / mov rax, r8 / shr rax, 32 / mov edx, eax / mov eax, r8d / ret
                    var bytes = [185, 128, 0, 0, 192, 15, 50, 137, 195, 73, 184, 5, 0, 0, 0, 1, 0, 0, 0, 76, 137, 192, 72, 193, 232, 32, 137, 194, 68, 137, 192, 195]
                    byte code[] = new byte[bytes.length]
                    for (var i = 0; i < bytes.length; i++) { code[i] = bytes[i] }
                    var r = X86.RunLong(code, new X86Registers(7))
                    print("eax " + r.eax + " edx " + r.edx + " efer " + r.ebx)
                    print("done")
                    """;
                var longRun = BuildAndRunFreeDos(dosLong, "freedoslong");
                if (longRun == null) Console.WriteLine($"(FreeDOS unter QEMU: uebersprungen - qemu-system-x86_64, mtools oder {freedosImage} fehlen)");
                else
                {
                    const string dosLongExpected = "real True long True\neax 5 edx 1 efer 1280\ndone\n";
                    CheckNat("FreeDOS unter QEMU: x86 - eine 64-Bit-Routine im Long Mode aus einem fire-Programm (EFER: LME und LMA, ein 64-Bit-Register) und zurueck im Real Mode",
                        longRun.Value.Ok && longRun.Value.Text == dosLongExpected, $"  erwartet:\n{dosLongExpected}\n  DOS:\n{longRun.Value.Text}");
                    var pmRun = BuildAndRunFreeDos(dosX86.Replace("print(\"long \" + X86.CanLongMode())\n", "").Replace("try { X86.RunLong(code2, r2) } catch (X86Exception e) { print(\"no long: \" + e.message) }\n", ""), "freedospm");
                    string pmExpected = dosX86Expected.Replace("long False\nno long: This processor has no long mode (64 bit).\n", "");
                    CheckNat("FreeDOS unter QEMU: x86 - der Protected Mode wie in DOSBox-X (mit HIMEMX)", pmRun!.Value.Ok && pmRun.Value.Text == pmExpected, $"  erwartet:\n{pmExpected}\n  DOS:\n{pmRun.Value.Text}");
                }

                // the serial and the printer port as devices (#import "devices"): COM1 and LPT1 from the BIOS, written into the files of DOSBox-X; read through the
                // interrupt of the UART (its loopback, one byte at a time - DOSBox repeats the first byte of a burst there)
                const string dosSerial = """
                    #import "devices"
                    #import "machine"
                    var list = DeviceManager.Devices()
                    var names = ""
                    for (var i = 0; i < list.Count; i++) { names = names + list[i].Identifier() + " " }
                    print(names)
                    var com = DeviceManager.Get("serial:COM1")
                    print("connect " + com.Connect())
                    print("write " + com.WriteString("Hallo COM1"))
                    Machine.Port.Out8(0x3FC, 0x1B)
                    var got = ""
                    for (var k = 0; k < 3; k++) {
                        var one = new byte[1]
                        one[0] = 65 + k
                        com.Write(one)
                        var tries = 0
                        while (!com.HasData() && tries < 500) { tries = tries + 1 }
                        got = got + com.ReadString()
                    }
                    print("loopback " + got)
                    Machine.Port.Out8(0x3FC, 0x0B)
                    com.Disconnect()
                    var lpt = DeviceManager.Get("serial:LPT1")
                    print("lpt " + lpt.Connect() + " " + lpt.WriteString("Drucker"))
                    lpt.Disconnect()
                    """;
                const string dosSerialExpected = "serial:COM1 serial:COM2 serial:LPT1 \nconnect True\nwrite True\nloopback ABC\nlpt True True\n";
                var serRun = BuildAndRunDos(dosSerial, "dos16ser", "OUT.TXT", "-set \"serial1=file file:{dir}/SER.TXT\" -set \"parallel1=file file:{dir}/PAR.TXT\"");
                string serFile = Path.Combine(workDir, "dos16ser", "SER.TXT"), parFile = Path.Combine(workDir, "dos16ser", "PAR.TXT");
                string serOut = File.Exists(serFile) ? File.ReadAllText(serFile) : "(keine Datei)", parOut = File.Exists(parFile) ? File.ReadAllText(parFile) : "(keine Datei)";
                CheckNat("dos16: serieller und paralleler Port als Geraete (COM1/LPT1 aus dem BIOS, gesendet, ueber den Interrupt des UART empfangen)",
                    serRun.Ok && serRun.Text == dosSerialExpected && serOut == "Hallo COM1" && parOut == "Drucker",
                    $"  erwartet:\n{dosSerialExpected}\n  DOS:\n{serRun.Text}\n  COM1: {serOut}\n  LPT1: {parOut}");

                // the graphics library in 16 bits (#import "graphics"): the same pixels as natively with the same widths (checksums of the framebuffers - lines, circles,
                // polygons, fills, text, blits, resize, shadow/blur, masks; PNG there and back). Two programs: with the library the code is about 450 KB, beside it
                // there is room for one palette framebuffer of 320x200 and smaller ones
                const string dosGfxDraw = """
                    #import "graphics"
                    class T {
                        static int Sum(Framebuffer fb) {
                            var data = fb.ReadBytes()
                            var s = 0
                            for (var i = 0; i < data.length; i++) { s = (s * 31 + data[i]) & 16777215 }
                            return s
                        }
                        static undefined Show(string what, Framebuffer fb) { print(what + " " + T.Sum(fb)) }
                    }
                    var big = new Framebuffer(320, 200, 1)
                    var r = new Renderer(big)
                    r.ClearTo(1)
                    T.Show("pal clear", big)
                    r.DrawLine(0, 0, 319, 199, new Pen(4))
                    r.DrawLine(319, 0, 0, 199, new Pen(5, 3))
                    T.Show("pal lines", big)
                    r.FillCircle(160, 100, 90, new SolidBrush(9))
                    r.DrawCircle(160, 100, 95, new Pen(10, 2))
                    r.FillEllipse(60, 150, 50, 30, new SolidBrush(11))
                    r.DrawEllipse(260, 50, 55, 35, new Pen(12))
                    T.Show("pal circles", big)
                    r.FillTriangle(10, 10, 300, 20, 150, 190, new SolidBrush(13))
                    r.FillPolygon([5.0, 5.0, 100.0, 30.0, 60.0, 120.0, 20.0, 80.0], new SolidBrush(14))
                    r.DrawPolygon([200.0, 150.0, 310.0, 160.0, 250.0, 195.0], new Pen(15, 2))
                    T.Show("pal polygons", big)
                    r.FillRoundRect(20, 20, 280, 160, 25, new SolidBrush(2))
                    r.DrawRoundRect(30, 30, 260, 140, 20, new Pen(3, 2))
                    r.FloodFill(160, 100, new SolidBrush(7))
                    T.Show("pal round flood", big)
                    r.DrawText(40, 60, "Hallo DOS 16 Bit", new SolidBrush(15))
                    r.Locate(2, 3)
                    r.Print("Konsole")
                    T.Show("pal text", big)
                    var small = new Framebuffer(64, 48, 1)
                    var rs = new Renderer(small)
                    rs.ClearTo(6)
                    rs.FillCircle(32, 24, 20, new SolidBrush(12))
                    r.Blit(small, 10, 10)
                    r.BlitScaled(small, 0, 0, 64, 48, 100, 20, 200, 150)
                    r.BlitScaled(small, 0, 0, 64, 48, 300, 190, -150, -120)
                    T.Show("pal blits", big)
                    big.Resize(330, 190)
                    T.Show("pal resize", big)
                    var rgba = new Framebuffer(120, 80)
                    var q = new Renderer(rgba)
                    q.ClearTo(0xFF102030)
                    q.FillRect(10, 10, 100, 60, new SolidBrush(0x80FF8040))
                    q.FillCircle(60, 40, 30, new SolidBrush(0xC000FF00))
                    q.DrawLine(0, 0, 119, 79, new Pen(0xFFFFFFFF, 4))
                    T.Show("rgba draw", rgba)
                    q.DrawShadow(20, 20, 60, 30, 10, 8, 0x80000000, 4)
                    q.Blur(10, 10, 90, 50, 6, 5)
                    T.Show("rgba fx", rgba)
                    q.DrawText(5, 60, "RGBA Text", new SolidBrush(0xFFFFFF00))
                    T.Show("rgba text", rgba)
                    var mask = small.ToMask()
                    T.Show("mask", mask)
                    """;
                const string dosGfxImages = """
                    #import "graphics"
                    class T {
                        static int Sum(Framebuffer fb) {
                            var data = fb.ReadBytes()
                            var s = 0
                            for (var i = 0; i < data.length; i++) { s = (s * 31 + data[i]) & 16777215 }
                            return s
                        }
                        static undefined Show(string what, Framebuffer fb) { print(what + " " + T.Sum(fb)) }
                    }
                    var rgba = new Framebuffer(120, 80)
                    var q = new Renderer(rgba)
                    q.ClearTo(0xFF102030)
                    q.FillCircle(60, 40, 30, new SolidBrush(0xC000FF00))
                    q.DrawText(5, 60, "PNG", new SolidBrush(0xFFFFFF00))
                    var png = rgba.ToPng()
                    var back = Framebuffer.FromImage(png)
                    print("png " + back.Width() + "x" + back.Height() + " " + (T.Sum(back) == T.Sum(rgba)))
                    var small = new Framebuffer(64, 48, 1)
                    var rs = new Renderer(small)
                    rs.ClearTo(6)
                    rs.FillCircle(32, 24, 20, new SolidBrush(12))
                    var palPng = Framebuffer.FromImage(small.ToPng())
                    print("pal png " + palPng.Width() + " " + T.Sum(palPng))
                    """;
                const string gfxDrawExpected = "pal clear 12820480\npal lines 14068416\npal circles 9446186\npal polygons 715367\npal round flood 11109143\npal text 4903732\npal blits 1318769\npal resize 2394223\nrgba draw 7557237\nrgba fx 12353285\nrgba text 11443732\nmask 8222753\n";
                var gfxDraw = BuildAndRunDos(dosGfxDraw, "dos16gfx");
                CheckNat("dos16: graphics - Zeichnen wie nativ (Palette 320x200 und RGBA: Linien, Kreise, Polygone, Fuellen, Text, Blits, Resize, Schatten/Unschaerfe, Maske)",
                    gfxDraw.Ok && gfxDraw.Text == gfxDrawExpected, $"  erwartet:\npal clear 12820480\npal lines 14068416\npal circles 9446186\npal polygons 715367\npal round flood 11109143\npal text 4903732\npal blits 1318769\npal resize 2394223\nrgba draw 7557237\nrgba fx 12353285\nrgba text 11443732\nmask 8222753\n\n  DOS:\n{gfxDraw.Text}");
                var gfxImages = BuildAndRunDos(dosGfxImages, "dos16gfximg");
                CheckNat("dos16: graphics - PNG schreiben und lesen (RGBA, Palette)", gfxImages.Ok && gfxImages.Text == "png 120x80 True\npal png 64 4434326\n", $"  erwartet:\npng 120x80 True\npal png 64 4434326\n\n  DOS:\n{gfxImages.Text}");

                // windows on DOS (#import "windows"): the screen in Mode 13h - a palette framebuffer straight into the video memory and its palette into the DAC (read
                // back), keys from the BIOS as on Windows (the code of Up has bit 30); an RGBA framebuffer through the colour cube, stretched; a larger one in VESA 101h
                const string dosWindow = """
                    #import "graphics"
                    #import "windows"
                    inline c int Vga(int x, int y) { return *(unsigned char FIRE_DOS_FAR*)FIRE_DOS_LOW(0xA000, (unsigned)(y * 320 + x)); }
                    inline c int Dac(int index) { outp(0x3C7, index); { int r = inp(0x3C9), g = inp(0x3C9), b = inp(0x3C9); return (FireInt)(((long)r << 16) | (g << 8) | b); } }
                    inline c Push(int code) { union REGS r; r.h.ah = 5; r.FIRE_DOS_W.cx = (unsigned short)code; fire_dos_int(0x16, &r); }
                    var fb = new Framebuffer(320, 200, 1)
                    fb.SetPaletteRgb(200, 255, 128, 0)
                    var r = new Renderer(fb)
                    r.ClearTo(1)
                    r.FillRect(10, 20, 30, 40, new SolidBrush(200))
                    var keys = ""
                    var w = new Window(fb, "DOS")
                    w.RegisterKeyDown((code, scan, mods, repeated) => { keys = keys + scan + "/" + code + " " })
                    Push(0x1E61)
                    Push(0x48E0)
                    Push(0x011B)
                    for (var i = 0; i < 3; i++) { w.Tick() }
                    var at = Vga(15, 30)
                    var bg = Vga(100, 100)
                    var dac = Dac(200)
                    w.Close()
                    w.Tick()
                    print("vga " + at + " " + bg + " dac " + dac)
                    print("keys " + keys)
                    """;
                const string dosWindow2 = """
                    #import "graphics"
                    #import "windows"
                    inline c int Vga(int x, int y) { return *(unsigned char FIRE_DOS_FAR*)FIRE_DOS_LOW(0xA000, (unsigned)(y * 320 + x)); }
                    inline c int VesaMode() { union REGS r; r.FIRE_DOS_W.ax = 0x4F03; fire_dos_int(0x10, &r); return r.FIRE_DOS_W.ax == 0x004F ? (FireInt)(r.FIRE_DOS_W.bx & 0x3FFF) : -1; }
                    inline c int VesaPixel(int x, int y) { union REGS r; long at = (long)y * 640 + x; r.FIRE_DOS_W.ax = 0x4F05; r.FIRE_DOS_W.bx = 0; r.FIRE_DOS_W.dx = (unsigned)(at >> 16); fire_dos_int(0x10, &r); return *(unsigned char FIRE_DOS_FAR*)FIRE_DOS_LOW(0xA000, (unsigned)(at & 0xFFFF)); }
                    var fb = new Framebuffer(160, 100)
                    var r = new Renderer(fb)
                    r.ClearTo(0xFF000000)
                    r.FillRect(0, 0, 80, 50, new SolidBrush(0xFF0000FF))
                    var w = new Window(fb, "RGBA")
                    w.Tick()
                    print("rgba " + Vga(10, 10) + " " + Vga(300, 150))
                    w.Close()
                    w.Tick()
                    var big = new Framebuffer(400, 300, 1)
                    var rb = new Renderer(big)
                    rb.ClearTo(3)
                    rb.FillRect(200, 150, 200, 150, new SolidBrush(9))
                    var w2 = new Window(big, "VESA")
                    w2.Tick()
                    print("vesa " + VesaMode() + " " + VesaPixel(10, 10) + " " + VesaPixel(600, 400))
                    w2.Close()
                    w2.Tick()
                    """;
                var winRun = BuildAndRunDos(dosWindow, "dos16win");
                CheckNat("dos16: windows - Mode 13h (Indizes in den Videospeicher, Palette in die DAC-Register), Tasten des BIOS als KeyDown (a, Pfeil hoch, Esc)",
                    winRun.Ok && winRun.Text == "vga 200 1 dac 4136960\nkeys 4/97 82/1073741906 41/27 \n", $"  DOS:\n{winRun.Text}");
                var winRun2 = BuildAndRunDos(dosWindow2, "dos16win2");
                CheckNat("dos16: windows - RGBA ueber den Farbwuerfel (gestreckt), ein Framebuffer ueber 320x200 in VESA 101h (640x480)",
                    winRun2.Ok && winRun2.Text == "rgba 180 0\nvesa 257 3 9\n", $"  DOS:\n{winRun2.Text}");

                // #intwidth 16 on the 8086 (SPEC 8.2.2): the same as the VM and natively (overflow, constants, arrays, fields, loops)
                const string dosInt16 = """
                    #intwidth 16
                    var a = 32767
                    a = a + 1
                    print(a)
                    print(256 * 256)
                    var m = -32767 - 1
                    print(m / -1)
                    print(m % -1)
                    print(1 << 15)
                    print(1 << 16)
                    print(70000)
                    var big = 1000
                    print(big * big)
                    int u[3]
                    u[0] = 32767
                    u[0]++
                    print(u[0])
                    class P { int x }
                    var p = new P()
                    p.x = 300 * 300
                    print(p.x)
                    var s = 0
                    for (var i = 0; i < 1000; i++) { s += i }
                    print(s)
                    var buf = new byte[30000]
                    print(buf.length)
                    var smile = 128512.ToChar()
                    print("x".Length + " " + smile + " " + (smile!int))
                    print(5cm + 3mm)
                    print(0.5 + 1)
                    print(2147483647 + 0)
                    """;
                var int16Run = BuildAndRunDos(dosInt16, "dos16int16");
                CheckNat("dos16: #intwidth 16 - dieselben Ergebnisse wie VM und gcc (Ueberlauf, Konstanten, Schieben, Arrays, Felder, Schleifen)",
                    int16Run.Ok && int16Run.Text == "-32768\n0\n-32768\n0\n-32768\n0\n4464\n16960\n-32768\n24464\n-24788\n30000\n1   0\n53mm\n1.5\n-1\n", $"  DOS:\n{int16Run.Text}");

                // the console of DOS (#import "console"): the screen through the BIOS (code page 437, attributes - read back from the video memory) and keys from the
                // keyboard buffer of the BIOS (INT 16h AH=05h puts them there, as if typed)
                const string dosCon = """
                    #import "console"
                    #import "io"
                    #import "filesystem"
                    inline c int Cell(int col, int row) { unsigned short __far* v = (unsigned short FIRE_DOS_FAR*)FIRE_DOS_LOW(0xB800, 0); return (FireInt)v[row * 80 + col]; }
                    inline c Push(int code) { union REGS r; r.h.ah = 5; r.FIRE_DOS_W.cx = (unsigned short)code; fire_dos_int(0x16, &r); }
                    var log = ""
                    Console.Clear()
                    log = log + "console " + Console.IsConsole() + " " + Console.Width() + "x" + Console.Height() + "\n"
                    Console.SetColor(ConsoleColor.Yellow, ConsoleColor.DarkBlue)
                    Console.WriteAt(2, 1, "Hi╔")
                    Console.ResetColor()
                    log = log + "at " + Console.Column() + "," + Console.Row() + "\n"
                    var c = Cell(2, 1)
                    log = log + "cell " + (c & 255) + " attr " + (c >> 8) + " box " + (Cell(4, 1) & 255) + "\n"
                    Push(0x1E61)
                    Push(0x48E0)
                    Push(0x3F00)
                    Push(0x7400)
                    Push(0x011B)
                    Push(0x1C0D)
                    Push(0x1E01)
                    Push(0x0E08)
                    log = log + "avail " + Console.KeyAvailable() + "\n"
                    for (var i = 0; i < 8; i++) {
                        var k = Console.ReadKey()
                        log = log + k.ToString() + " " + k.Key + "\n"
                    }
                    IO.File.WriteAllText("C:\\RES.TXT", log)
                    """;
                const string dosConExpected = "console True 80x25\nat 5,1\ncell 72 attr 30 box 201\navail True\na Character\nUp Up\nF5 F5\nCtrl+Right Right\nEscape Escape\n"
                    + "Enter Enter\nCtrl+a Character\nBackspace Backspace\n";
                var conRun = BuildAndRunDos(dosCon, "dos16con", "RES.TXT");
                CheckNat("dos16: Konsole ueber das BIOS (Farben als Attribut, Codepage 437, Cursor) und Tasten aus dem Tastaturpuffer (Pfeil, F5, Strg+Pfeil, Esc, Enter, Strg+A, Backspace)",
                    conRun.Ok && conRun.Text == dosConExpected, $"  erwartet:\n{dosConExpected}\n  DOS:\n{conRun.Text}");

                // ---- dos32 (ROADMAP 13.4): the same programs with Open Watcom 386 and the extender DOS/32A in the program - flat memory, int and float of 64 bits
                var dos32 = TargetProfile.Dos32;
                (bool Ok, string Text) RunDos32(string source, string folder, string outputFile = "OUT.TXT", string settings = "") => BuildAndRunDos(source, folder, outputFile, settings, dos32);
                // what the VM prints with the widths of a target (CVmTest.TargetOutput: int and float as the target has them)
                string VmOf(string source, TargetProfile target) => CVmTest.TargetOutput(new Linker().CompileAndLink(new[] { source }, null, null, VmExecutionMode.Release, null, target));
                CheckNat("dos32: Zielprofil (Plattform dos32, Toolchain watcom, int/float 64, word 32, Threads ueber den eigenen Scheduler)",
                    dos32.Native.Platform == "dos32" && dos32.Native.Toolchain == "watcom" && dos32.IntWidth == 64 && dos32.FloatWidth == 64 && dos32.WordWidth == 32 && dos32.Native.SupportsThreads
                    && dos32.Native.CompileArgs.Contains("-bdos32a"));
                string dos32Vm = VmOf(dosSource, dos32);
                var d32Run = RunDos32(dosSource, "dos32");
                CheckNat("dos32: mit Open Watcom 386 und DOS/32A gebaut, in DOSBox-X gelaufen - dieselbe Ausgabe wie die VM mit den Breiten von dos32 (Objekte, Strings, Einheiten, Ausnahmen, word 32 Bit, ein fire-Thread)",
                    d32Run.Ok && d32Run.Text == dos32Vm && dos32Vm.Length > 0, $"  VM:\n{dos32Vm}\n  DOS:\n{d32Run.Text}");
                var d32Files = RunDos32(dosFiles, "dos32files");
                CheckNat("dos32: Dateien mit DOS-Pfaden (wie dos16)", d32Files.Ok && d32Files.Text == dosFilesExpected, $"  erwartet:\n{dosFilesExpected}\n  DOS:\n{d32Files.Text}");
                var d32Big = RunDos32(dosBig, "dos32big");
                CheckNat("dos32: grosse Arrays und Puffer im flachen Speicher (wie dos16)", d32Big.Ok && d32Big.Text == dosBigExpected, $"  erwartet:\n{dosBigExpected}\n  DOS:\n{d32Big.Text}");
                var d32Mach = RunDos32(dosMachine, "dos32mach");
                string dos32MachineExpected = dosMachineExpected.Replace("far: The address 2097152 cannot be reached from here.\n", "");
                CheckNat("dos32: machine - wie dos16; der Speicher ueber 1 MB ist ueber DPMI erreichbar (eine Seite abgebildet)", d32Mach.Ok && d32Mach.Text == dos32MachineExpected,
                    $"  erwartet:\n{dos32MachineExpected}\n  DOS:\n{d32Mach.Text}");
                const string dos32Dos = """
                    #import "dos"
                    #import "xms"
                    #import "x86"
                    var v = Dos.Version()
                    print("dos " + (v[0] >= 5))
                    var r = Dos.Interrupt(0x1A, new DosRegisters(0))
                    print("ticks " + (r.cx * 65536 + r.dx > 0))
                    var seg = Dos.Alloc(256)
                    print("alloc " + (seg > 0))
                    Dos.Free(seg)
                    print("vector " + (Dos.GetVector(0x21) != 0))
                    var name = new byte[64]
                    try { Dos.SegmentOf(name) } catch (DosException e) { print("high: " + (e.message.IndexOf("first megabyte") > 0)) }
                    print("xms " + Xms.Available())
                    print("x86 " + X86.Mode() + " " + X86.CanSwitch())
                    print("done")
                    """;
                var d32Dos = RunDos32(dos32Dos, "dos32dos");
                CheckNat("dos32: dos ueber DPMI (Interrupts im Real Mode, DOS-Speicher, Vektoren; ein Puffer des Programms liegt ueber 1 MB), ohne xms und x86",
                    d32Dos.Ok && d32Dos.Text == "dos True\nticks True\nalloc True\nvector True\nhigh: True\nxms False\nx86 none False\ndone\n", $"  DOS:\n{d32Dos.Text}");
                var d32Ser = RunDos32(dosSerial, "dos32ser", "OUT.TXT", "-set \"serial1=file file:{dir}/SER.TXT\" -set \"parallel1=file file:{dir}/PAR.TXT\"");
                string ser32 = Path.Combine(workDir, "dos32ser", "SER.TXT"), par32 = Path.Combine(workDir, "dos32ser", "PAR.TXT");
                string ser32Out = File.Exists(ser32) ? File.ReadAllText(ser32) : "(keine Datei)", par32Out = File.Exists(par32) ? File.ReadAllText(par32) : "(keine Datei)";
                CheckNat("dos32: serieller und paralleler Port (wie dos16; die IRQ des UART im Handler des Protected Mode)",
                    d32Ser.Ok && d32Ser.Text == dosSerialExpected && ser32Out == "Hallo COM1" && par32Out == "Drucker", $"  DOS:\n{d32Ser.Text}\n  COM1: {ser32Out}\n  LPT1: {par32Out}");
                var d32Gfx = RunDos32(dosGfxDraw, "dos32gfx");
                CheckNat("dos32: graphics - dieselben Pixel wie dos16 und nativ", d32Gfx.Ok && d32Gfx.Text == gfxDrawExpected, $"  erwartet:\n{gfxDrawExpected}\n  DOS:\n{d32Gfx.Text}");
                var d32Win = RunDos32(dosWindow, "dos32win");
                CheckNat("dos32: windows - Mode 13h und die Tasten des BIOS (wie dos16)", d32Win.Ok && d32Win.Text == "vga 200 1 dac 4136960\nkeys 4/97 82/1073741906 41/27 \n", $"  DOS:\n{d32Win.Text}");
                var d32Win2 = RunDos32(dosWindow2, "dos32win2");
                CheckNat("dos32: windows - RGBA ueber den Farbwuerfel, VESA 101h (die Information des Modus ueber DPMI im Real Mode)", d32Win2.Ok && d32Win2.Text == "rgba 180 0\nvesa 257 3 9\n", $"  DOS:\n{d32Win2.Text}");
                var d32Con = RunDos32(dosCon, "dos32con", "RES.TXT");
                CheckNat("dos32: Konsole ueber das BIOS (wie dos16)", d32Con.Ok && d32Con.Text == dosConExpected, $"  erwartet:\n{dosConExpected}\n  DOS:\n{d32Con.Text}");

                // ---- win16 (ROADMAP 13.7): Windows 3.x with Open Watcom, run under Wine (its 32-bit part runs programs of 16 bits) in Xvfb; the output through
                // FIRE_STDOUT into a file (a Windows program has no console)
                string? wine = fire.Native.ToolchainSetup.FindProgram("wine"), xvfbRunW = fire.Native.ToolchainSetup.FindProgram("xvfb-run");
                var win16 = TargetProfile.Win16;
                CheckNat("win16: Zielprofil (Plattform win16, Toolchain watcom, -bwindows, Einstieg ueber WinMain, int/float 32, word 16, Threads ueber den eigenen Scheduler mit -zu)",
                    win16.Native.Platform == "win16" && win16.Native.Toolchain == "watcom" && win16.Native.CompileArgs.Contains("-bwindows") && win16.Native.Entry.Name == "fire_win16_main"
                    && win16.IntWidth == 32 && win16.WordWidth == 16 && win16.Native.SupportsThreads && win16.Native.CompileArgs.Contains("-Wc,-zu"));
                if (wine == null || xvfbRunW == null) Console.WriteLine("(win16: uebersprungen - wine (mit seinem 32-Bit-Teil) oder xvfb-run fehlen)");
                else
                {
                    string winePrefix = Path.Combine(workDir, "wineprefix");
                    (bool Ok, string Text) RunWin16(string source, string folder)
                    {
                        string dir = Path.Combine(workDir, folder);
                        Directory.CreateDirectory(dir);
                        var built = fire.Compiler.NativeBuilder.Build(new[] { source }, new fire.Native.NativeConfig(), win16, fire.Native.ToolchainDef.BuiltIn["watcom"], Path.Combine(dir, "PROG.EXE"), VmExecutionMode.Release);
                        if (!built.Ok) return (false, built.Log);
                        string outFile = Path.Combine(dir, "OUT.TXT");
                        if (File.Exists(outFile)) File.Delete(outFile);
                        var env = new Dictionary<string, string?> { ["WINEDEBUG"] = "-all", ["WINEPREFIX"] = winePrefix, ["FIRE_STDOUT"] = "OUT.TXT" };
                        var run = TestProcess.Run(xvfbRunW, $"-a \"{wine}\" PROG.EXE", dir, environment: env, timeoutMs: 120000);
                        string text = File.Exists(outFile) ? File.ReadAllText(outFile).Replace("\r", "") : "(keine Ausgabe)\n" + run.Stdout + run.Stderr;
                        return (true, run.Exit == -1 ? text + "(Wine nach 2 Minuten beendet)" : text);
                    }
                    // (the whole program of dos16, its fire thread included: fire's own scheduler, a stack segment per task - std/sched/fire_ctx_win16.h)
                    string win16Source = dosSource;
                    string win16Vm = VmOf(win16Source, win16);
                    var w16Run = RunWin16(win16Source, "win16");
                    CheckNat("win16: mit Open Watcom fuer Windows 3.x gebaut, unter Wine gelaufen - dieselbe Ausgabe wie die VM mit den Breiten von win16 (mit einem fire-Thread)",
                        w16Run.Ok && w16Run.Text == win16Vm && win16Vm.Length > 0, $"  VM:\n{win16Vm}\n  Wine:\n{w16Run.Text}");
                    const string win16Big = """
                        var big = new byte[3000000]
                        big[2999999] = 7
                        int many[] = new int[40000]
                        many[39999] = 42
                        var c = copy big
                        print("big " + big.length + " " + c[2999999] + " " + many[39999])
                        print("args " + Environment.Args.length)
                        """;
                    var w16Big = RunWin16(win16Big, "win16big");
                    CheckNat("win16: grosse Arrays (3 MB, int[40000]) im Huge Model von Windows, die Argumente (WinMain)",
                        w16Big.Ok && w16Big.Text == "big 3000000 7 42\nargs 0\n", $"  Wine:\n{w16Big.Text}");
                    // scene3d with a picture of 160 x 120: the framebuffer, the depth buffer and the camera's pixels pass 64 KB each
                    var w16S3d = RunWin16(dosScene3d.Replace("{SIZE}", "160, 120"), "win16s3d");
                    const string w16S3dExpected = "drawn 10047 2816\ncorner 24,42,61,255\nfloor 9538 2816\n";
                    CheckNat("win16: scene3d - 160 x 120 Pixel, die Puffer der Engine ueber 64 KB (wie in der VM)",
                        w16S3d.Ok && w16S3d.Text == w16S3dExpected, $"  erwartet:\n{w16S3dExpected}\n  Wine:\n{w16S3d.Text}");
                    // the window through GDI (#import "windows"): a palette framebuffer stretched into the client area (read back with GetPixel), a key posted to the
                    // window arrives as KeyDown with its scan code
                    const string win16Window = """
                        #import "graphics"
                        #import "windows"
                        inline c int Pixel(int x, int y) { HWND h = FindWindow((LPCSTR)"FireWindow16", (LPCSTR)"Win16"); HDC dc; COLORREF c; if (h == 0) return -1; dc = GetDC(h); c = GetPixel(dc, (int)x, (int)y); ReleaseDC(h, dc); return (FireInt)(c & 0xFFFFFFL); }
                        inline c Key(int vk, int scan) { HWND h = FindWindow((LPCSTR)"FireWindow16", (LPCSTR)"Win16"); if (h != 0) { PostMessage(h, WM_KEYDOWN, (WPARAM)vk, ((LPARAM)scan << 16) | 1); PostMessage(h, WM_KEYUP, (WPARAM)vk, ((LPARAM)scan << 16) | 0xC0000001L); } }
                        var fb = new Framebuffer(80, 60, 1)
                        fb.SetPaletteRgb(7, 255, 0, 0)
                        fb.SetPaletteRgb(9, 0, 0, 255)
                        var r = new Renderer(fb)
                        r.ClearTo(9)
                        r.FillRect(0, 0, 40, 60, new SolidBrush(7))
                        var keys = ""
                        var w = new Window(fb, "Win16")
                        w.RegisterKeyDown((code, scan, mods, repeated) => { keys = keys + scan + "/" + code + " " })
                        w.Tick()
                        print("left " + Pixel(10, 30) + " right " + Pixel(70, 30))
                        Key(65, 30)
                        Key(27, 1)
                        w.Tick()
                        w.Tick()
                        print("keys " + keys)
                        w.Close()
                        print("done")
                        """;
                    var w16Win = RunWin16(win16Window, "win16win");
                    CheckNat("win16: windows - ein Fenster ueber GDI (Palette-Framebuffer gestreckt, mit GetPixel zurueckgelesen), Tasten aus WM_KEYDOWN (Scancode wie unter Win32)",
                        w16Win.Ok && w16Win.Text == "left 255 right 16711680\nkeys 4/97 41/27 \ndone\n", $"  Wine:\n{w16Win.Text}");
                    // the clipboard of Windows: text (Latin-1, the line break as CR LF and back) and a picture (CF_DIB)
                    const string win16Clip = """
                        #import "graphics"
                        #import "windows"
                        var fb = new Framebuffer(10, 6)
                        var w = new Window(fb, "Clip")
                        print("set " + Clipboard.SetText("Grüße\nzwei"))
                        print("has " + Clipboard.HasText + " [" + Clipboard.GetText() + "]")
                        var pic = new Framebuffer(10, 6)
                        new Renderer(pic).ClearTo(255)
                        print("image " + Clipboard.SetImage(pic) + " " + Clipboard.HasImage)
                        var back = Clipboard.GetImage()
                        print("back " + back.Width() + "x" + back.Height())
                        w.Close()
                        """;
                    var w16Clip = RunWin16(win16Clip, "win16clip");
                    CheckNat("win16: die Zwischenablage von Windows - Text (Latin-1, Zeilenumbruch) und ein Bild (CF_DIB) hin und zurueck",
                        w16Clip.Ok && w16Clip.Text == "set True\nhas True [Grüße\nzwei]\nimage True True\nback 10x6\n", $"  Wine:\n{w16Clip.Text}");
                    // the user interface library (#import "ui", "dialogs") in 16 bits: a panel with a label and a button, drawn into the GDI window
                    const string win16Ui = """
                        #import "graphics"
                        #import "windows"
                        #import "ui"
                        #import "dialogs"
                        var fb = new Framebuffer(240, 120)
                        var win = new Window(fb, "UI16")
                        var ui = new UI.Root(fb, win)
                        var panel = new UI.Panel(8, 8, 220, 100)
                        ui.Add(panel)
                        panel.Add(new UI.Label("Hello Windows 3.x", 6, 6))
                        panel.Add(new UI.Button("OK", 6, 30, 80, 26))
                        ui.Tick()
                        var rd = new Renderer(fb)
                        print("ui " + (rd.GetPixel(20, 50) != rd.GetPixel(200, 90)) + " " + (Dialogs.DialogResult.OK != Dialogs.DialogResult.Cancel))
                        win.Close()
                        """;
                    var w16Ui = RunWin16(win16Ui, "win16ui");
                    CheckNat("win16: die UI-Bibliothek (ui, dialogs) im Huge Model - ein Panel mit Label und Knopf, ueber GDI gezeigt",
                        w16Ui.Ok && w16Ui.Text == "ui True True\n", $"  Wine:\n{w16Ui.Text}");
                }
            }
            // bios16 (ROADMAP 13.5): BiosTests (also alone: FIRE_TESTS_ONLY=bios16)
            BiosTests.Run(CheckNat, workDir, dosSource);
        }
        var espSources = fire.Native.CppGenerator.GenerateSources(espLinked, esp);
        string espCpp = espSources.Program;
        CheckNat("esp32: Einstieg app_main statt main, Defines, 32-Bit-float",
            espCpp.Contains("\nvoid app_main(void)") && !espCpp.Contains("int main(") && espCpp.Contains("#define FIRE_TARGET_ESP32 1")
            && espCpp.Contains("#define FIRE_HAL_ESP32 1") && espCpp.Contains("#define FIRE_DEFAULT_STACK_BYTES 8192") && espCpp.Contains("#define FIRE_FLOAT32 1"));
        {
            // (a split program, FIRE_SPLIT=1: every unit on its own)
            string espBuild = "";
            int espExit = 0;
            foreach (var espFile in NativeTestBuild.SourceFiles(espSources, workDir, "esp").Split("\" \"").Select(f => f.Trim('"')))
            {
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fire.Native.ToolchainDef.CCompilerFor(cxx), $"-std=c11 -Wall -Wextra -c \"{espFile}\" -I\"{workDir}\" -I\"{Path.Combine(workDir, "sim")}\" -o \"{espFile}.o\"") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false })!;
                espBuild += p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0) espExit = p.ExitCode;
            }
            CheckNat("esp32: das erzeugte C uebersetzt ohne Warnung", espExit == 0 && !espBuild.Contains("warning"), espBuild);
        }
        var cliEsp = CommandLineParser.Parse(new[] { "native", "a.script", "-t", "esp32" });
        CheckNat("Befehlszeile: -t esp32 / unbekanntes Ziel / -t ausserhalb von native",
            cliEsp.Error == null && cliEsp.Target == esp && CommandLineParser.Parse(new[] { "native", "a.script", "-t", "amiga" }).TargetName == "amiga"
            && CommandLineParser.Parse(new[] { "run", "a.script", "-t", "esp32" }).Error != null && CommandLineParser.Parse(new[] { "native", "a.script" }).Target == null);

        // (variables for the program in `environment`: cases run at the same time, Environment.SetEnvironmentVariable would reach the others)
        string RunProc(string tool, string arguments, string dir, out int exit, string? input = null, IReadOnlyDictionary<string, string?>? environment = null)
        {
            var (stdout, stderr, code) = TestProcess.Run(tool, arguments, dir, input, environment);
            exit = code;
            return stdout + stderr;
        }
        // The debugger, the devices, the windows (with the UI) and FreeRTOS at the same time (TestJobs.Concurrently: the output stays in this order). They share no
        // files, the VM runs only in the debugger part, Wine is taken in turn (TestJobs.Wine), every X server is its own (TestJobs.XvfbServer); the programs
        // get their variables per process. (What the window cases left behind before they did so - the sections after them expect it:)
        Environment.SetEnvironmentVariable("FIRE_DISPLAY", "headless");
        TestJobs.Concurrently(() =>
        {
            // ---- The debugger of native debug builds (docs/spec/NATIVE_DEBUG_PROTOCOL.md): the program driven over the protocol, against the VM (NativeDebugTests.cs)
            NativeDebugTests.Run(CheckNat, cxx, workDir, sanitize);
        }, () =>
        {
            // ---- Devices: the same scripts as in the device checks of the VM, natively with the loopback device (FIRE_DEVICES), against the expected outputs
            {
                if (devNativeCases.Count == 0) Console.WriteLine("(Geraete nativ: keine Faelle - die Geraete-Pruefungen der VM liefen nicht)");
                // (every case a unit of FIRE_TESTS_RESUME/FIRE_TESTS_SHARD: TestResume.cs)
                string DevKey((string Title, string Script, string[] Expected, string? DefaultId) c) => TestResume.Active ? TestResume.Key(c.Script, string.Join("\n", c.Expected), c.DefaultId, sanitize) : "";
                var devTasks = TestJobs.Map(devNativeCases.Where(c => TestSelected(c.Title) && TestResume.Pending("Geraete nativ: " + c.Title, DevKey(c))).ToList(), (c, index) =>
                {
                    var defines = new List<string> { "FIRE_DEVICES=\"loopback\"" };
                    if (c.DefaultId != null) defines.Add($"FIRE_DEFAULT_DEVICE=\"{c.DefaultId}\"");
                    var target = TargetProfile.Host with { Native = TargetProfile.Host.Native with { Defines = defines } };
                    fire.Native.NativeSources generated;
                    try { generated = fire.Native.CppGenerator.GenerateSources(new Linker().CompileAndLink(new[] { c.Script }, null, null, VmExecutionMode.Release, null, target), target); }
                    catch (fire.Native.NativeNotSupportedException ex) { return (c.Title, "", $"nicht uebersetzbar: {ex.Message}"); }
                    string exe = Path.Combine(workDir, $"dev{index}.bin");
                    string build = NativeTestBuild.Build(NativeTestBuild.In(workDir), cxx, generated, workDir, $"dev{index}", $"-pthread {NativeTestBuild.Opt} -Wall -Wextra {sanitize}", exe, out int buildExit);
                    if (buildExit != 0 || build.Contains("warning:")) return (c.Title, "", "C/C++-Compiler: " + build);
                    string actual = RunProc(exe, "", workDir, out int runExit);
                    string expectedText = string.Concat(c.Expected.Select(l => l + "\n"));
                    if (runExit == 0 && actual == expectedText) NativeTestBuild.Clean(workDir, $"dev{index}", exe);
                    return (c.Title, expectedText, runExit == 0 ? actual : $"Exitcode {runExit}: {actual}");
                });
                foreach (var task in devTasks)
                {
                    var (title, expected, actual) = task;
                    var devCase = devNativeCases.First(d => d.Title == title);
                    TestResume.Run("Geraete nativ: " + title, DevKey(devCase), () => CheckNat($"Geraete nativ: {title}", expected == actual, $"  erwartet:\n{expected}\n  erhalten:\n{actual}"));
                }
                if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FIRE_TESTS_FILTER")) && Environment.GetEnvironmentVariable("FIRE_TESTS_ONLY") == null) CheckNat("Geraete nativ: es gibt Faelle", devNativeCases.Count >= 10, devNativeCases.Count.ToString());
            }
        }, () =>
        {
            // ---- Window (packages/windows/native/fire_bridge_windows.h): fire's own display without a screen (FIRE_DISPLAY=headless) - the events are provided by
            // FIRE_DISPLAY_SELFTEST -, on Wayland (weston), the clipboard of X11 under Xvfb, and Windows (MinGW, fire_display_win32.h) under Wine
            {
                var windowCases = new (string Title, string Script, string[] Expected, string Define)[]
                {
                    ("Fenster: Framebuffer anzeigen, VSync, Tick, Ereignisse anmelden", """
                #import "windows"
                var fb = new Framebuffer(64, 48)
                var con = new Renderer(fb)
                con.Clear()
                con.FillRect(4, 4, 20, 10, new SolidBrush(12))
                var win = new Window(fb, "fire test")
                print(win.VSync)
                win.VSync = false
                print(win.VSync)
                print(win.EnableEvents())
                print(win.Tick())
                print(win.NextEvent())
                print(win.RegisterKeyDown(func(int k, int s, int m, bool r) => { print("key") }))
                print(win.RegisterClose(func() => { print("close") }))
                print(win.RegisterTextInput(func(string t) => { print("text " + t) }))
                for (var i = 0; i < 3; i++) { con.FillRect(i * 5, 20, 4, 4, new SolidBrush(9)); win.Tick() }
                print(win.Tick())
                """, new[] { "True", "False", "True", "True", "undefined", "True", "True", "True", "True" }, ""),
                    ("Fenster: ein Handler, der wartet, haelt das Fenster nicht an (Task der Gruppe des Hauptprogramms, docs/ROADMAP_TASKS.md Schritt 5)", """
                #import "windows"
                #import "time"
                var fb = new Framebuffer(64, 48)
                var win = new Window(fb, "group")
                win.VSync = false
                var ticks = 0
                var during = -1
                var finished = false
                var order = ""
                win.RegisterKeyDown(func(int k, int s, int m, bool r) => {
                    order = order + "down "
                    var before = ticks
                    Sleep(40ms)
                    during = ticks - before
                    order = order + "after "
                    finished = true
                })
                win.RegisterKeyUp(func(int k, int s, int m, bool r) => { order = order + "up " })
                var open = true
                var n = 0
                while (!finished && n < 2000) {
                    if (open) { open = win.Tick() }
                    ticks = ticks + 1
                    n = n + 1
                    Sleep(2ms)
                }
                print(order)
                print(during > 0)
                """, new[] { "down up after ", "True" }, "-DFIRE_DISPLAY_SELFTEST "),
                    ("Fenster: AutoResize bringt den Framebuffer auf die Groesse des Fensters", """
                #import "windows"
                var fb = new Framebuffer(64, 48)
                var win = new Window(fb, "resize")
                print(win.AutoResize)
                win.AutoResize = true
                print(win.AutoResize)
                win.EnableEvents()
                win.RegisterResize(func(int w, int h) => { print("cb " + w + " " + h) })
                print(win.Tick())
                print(fb.Width() + "x" + fb.Height())
                var e = win.NextEvent()
                print(e[0] + " " + e[1] + " " + e[2])
                print(fb.Resize(0, 5))
                print(fb.Resize(20000, 5))
                print(fb.Width() + "x" + fb.Height())
                print(fb.Resize(100, 80))
                print(fb.Width() + "x" + fb.Height())
                """, new[] { "False", "True", "cb 90 70", "True", "90x70", "4 90 70", "False", "False", "90x70", "True", "100x80" }, "-DFIRE_DISPLAY_SELFTEST_RESIZE "),
                    ("Fenster: Touch- und Joystick-Ereignisse, TouchMouse", """
                #import "windows"
                var fb = new Framebuffer(64, 48)
                var win = new Window(fb, "input")
                print(win.TouchMouse)
                win.TouchMouse = false
                print(win.TouchMouse)
                win.RegisterTouchDown(func(int f, float x, float y, float p) => { print("down " + f + " " + x + " " + y + " " + p) })
                win.RegisterJoystickAxis(func(int j, int a, float v) => { print("axis " + j + " " + a + " " + v) })
                win.RegisterJoystickHat(func(int j, int h, int m) => { print("hat " + j + " " + h + " " + m) })
                win.RegisterJoystickButtonDown(func(int j, int b) => { print("button " + j + " " + b) })
                win.RegisterJoystickRemoved(func(int j) => { print("removed " + j) })
                win.EnableEvents()
                print(win.Tick())
                var e = win.NextEvent()
                while (e != undefined) {
                    var line = ""
                    for (var i = 0; i < e.length; i++) { line = line + e[i] + " " }
                    print("queued " + line)
                    e = win.NextEvent()
                }
                """, new[] { "True", "False", "down 5 32 12 1", "axis 7 1 -1", "button 7 3", "hat 7 0 3", "removed 7", "True", "queued 16 5 32 12 1 ", "queued 17 5 48 24 0.5 ", "queued 18 5 48 24 0 ", "queued 32 7 1 -1 ", "queued 33 7 3 ", "queued 34 7 3 ", "queued 35 7 0 3 ", "queued 37 7 " }, "-DFIRE_DISPLAY_SELFTEST_INPUT "),
                    ("Fenster: Drop von Dateien und Text aus einem anderen Programm (Callbacks, Warteschlange, Position), die Zwischenablage", """
                #import "windows"
                var fb = new Framebuffer(64, 48)
                var win = new Window(fb, "drop")
                win.RegisterDropFiles(func(any files, float x, float y) => {
                    var line = "files"
                    foreach (f in files) { line = line + " [" + f + "]" }
                    print(line + " at " + x + " " + y)
                })
                win.RegisterDropText(func(string text, float x, float y) => { print("text [" + text + "] at " + x + " " + y) })
                win.EnableEvents()
                print(win.Tick())
                var e = win.NextEvent()
                while (e != undefined) {
                    if (e[0] == 40) { print("queued 40 " + e[1].length + " " + e[1][0] + " " + e[2] + " " + e[3]) }
                    if (e[0] == 41) { print("queued 41 " + e[1] + " " + e[2] + " " + e[3]) }
                    e = win.NextEvent()
                }
                print(Clipboard.HasText)
                print(Clipboard.SetText("Grüße aus fire 😀"))
                print(Clipboard.HasText + " " + Clipboard.GetText() + " " + Clipboard.GetText().Length)
                """, new[] { "files [/tmp/eins.txt] [/tmp/zwei ä.png] at 40 30", "text [gezogener Text] at 40 30", "files [/tmp/allein.txt] at 40 30", "False", "queued 40 2 /tmp/eins.txt 40 30", "queued 41 gezogener Text 40 30", "queued 40 1 /tmp/allein.txt 40 30", "False", "True", "True Grüße aus fire 😀 17" }, "-DFIRE_DISPLAY_SELFTEST_DROP "),
                    ("UI: ein Drop aus einem anderen Programm erreicht das Element darunter, ein Textfeld kopiert und fuegt ueber die Zwischenablage ein", """
                #import "ui"
                var fb = new Framebuffer(64, 48)
                var win = new Window(fb, "ui drop")
                var ui = new UI.Root(fb, win)
                var area = new UI.Panel(0, 0, 60, 40)
                area.AllowDrop = true
                area.OnDrop = func (UI.DragData d, int x, int y) => {
                    print("area " + d.Files.Count + " [" + d.Text + "] " + d.External + " " + x + " " + y)
                    return UI.DropEffect.Copy
                }
                ui.Add(area)
                var tb = new UI.TextBox("kopier mich", 0, 0, 10, 10)
                tb.Visible = false
                ui.Add(tb)
                ui.Draw()
                ui.Tick()
                tb.SelectAll()
                tb.Copy()
                print(Clipboard.GetText())
                tb.Select(0, 6)
                tb.Paste()
                print(tb.Text)
                """, new[] { "area 2 [] True 40 30", "area 0 [gezogener Text] True 40 30", "area 1 [] True 40 30", "kopier mich", "kopier mich mich" }, "-DFIRE_DISPLAY_SELFTEST_DROP "),
                    ("Fenster: Ereignisse als Callbacks und aus der Warteschlange, Schliessen beendet Tick", """
                #import "windows"
                var fb = new Framebuffer(64, 48)
                var win = new Window(fb, "events")
                var log = ""
                win.RegisterKeyDown(func(int k, int s, int m, bool r) => { print("keydown " + k + " " + s + " " + m + " " + r) })
                win.RegisterKeyUp(func(int k, int s, int m, bool r) => { print("keyup " + k + " " + s + " " + m + " " + r) })
                win.RegisterMouseDown(func(int b, float x, float y) => { print("down " + b + " " + x + " " + y) })
                win.RegisterMouseUp(func(int b, float x, float y) => { print("up " + b + " " + x + " " + y) })
                win.RegisterMouseMove(func(float x, float y, int st) => { print("move " + x + " " + y + " " + st) })
                win.RegisterMouseMoveRelative(func(float x, float y, int st) => { print("rel " + x + " " + y + " " + st) })
                win.RegisterMouseScroll(func(float sx, float sy, float x, float y) => { print("scroll " + sx + " " + sy) })
                win.RegisterTextInput(func(string t) => { print("text " + t + " " + t.Length) })
                win.RegisterCloseRequest(func() => { print("closerequest") })
                win.RegisterClose(func() => { print("close") })
                win.EnableEvents()
                var open = win.Tick()
                print("open " + open)
                var e = win.NextEvent()
                while (e != undefined) {
                    var line = ""
                    for (var i = 0; i < e.length; i++) { line = line + e[i] + " " }
                    print("queued " + line)
                    e = win.NextEvent()
                }
                print(win.NextEvent())
                """, new[] { "keydown 97 4 1 False", "keyup 97 4 0 False", "down 1 32 24", "up 1 32 24", "move 10 12 1", "rel 3 -2 1", "scroll 0 1.5", "text éx 2", "closerequest", "close", "open False", "queued 24 97 4 1 False ", "queued 25 97 4 0 False ", "queued 8 1 32 24 ", "queued 11 1 32 24 ", "queued 9 10 12 1 ", "queued 10 3 -2 1 ", "queued 12 0 1.5 0 0 ", "queued 3 éx ", "queued 2 ", "queued 1 ", "undefined" }, "-DFIRE_DISPLAY_SELFTEST "),
                    ("Fenster: WindowManager.Run auf dem Programm-Thread (Konstruktor-Parameter, onCycle, Callbacks)", """
                #import "windows"
                var fb = new Framebuffer(64, 48)
                var mine = new WindowManager()
                var win = new Window(fb, "run", mine)
                var cycles = 0
                win.onCycle = func () => { cycles++ }
                win.RegisterKeyDown(func(int k, int s, int m, bool r) => { print("key " + k) })
                win.RegisterMouseDown(func(int b, float x, float y) => { print("down " + b + " " + x + " " + y) })
                print("mine " + (win.Manager == mine) + ", started " + mine.IsStarted)
                win.Run()
                print("done: cycles " + (cycles > 0) + ", closed " + win.IsClosed + ", left " + mine.Count)
                """, new[] { "mine True, started False", "key 97", "down 1 32 24", "done: cycles True, closed True, left 0" }, "-DFIRE_DISPLAY_SELFTEST "),
                    ("Fenster: Window.Show startet den Thread des Managers, das Programm geht weiter und wartet am Ende", """
                #import "windows"
                var fb = new Framebuffer(64, 48)
                var win = new Window(fb, "shown")
                var cycles = 0
                win.onCycle = func () => { cycles++ }
                win.RegisterKeyDown(func(int k, int s, int m, bool r) => { print("key " + k + " in cycle " + (cycles >= 0)) })
                win.RegisterMouseDown(func(int b, float x, float y) => { print("down " + b + " " + x + " " + y) })
                win.Show()
                print("goes on, started " + win.Manager.IsStarted + ", default " + (win.Manager == WindowManager.Default))
                """, new[] { "goes on, started True, default True", "key 97 in cycle True", "down 1 32 24" }, "-DFIRE_DISPLAY_SELFTEST "),
                    // SPEC 8.1.4: the exception of an event handler goes to the call of Tick once Tick has returned (the same object); the other handlers of the
                    // cycle do not run
                    ("Fenster: ein Handler wirft - das try um Tick faengt die Exception, nachdem Tick fertig ist; die weiteren Handler laufen nicht", """
                #import "windows"
                class Boom : Exception { construct(string m) : base(m) { } }
                var fb = new Framebuffer(64, 48)
                var win = new Window(fb, "throw")
                win.VSync = false
                var thrown = new Boom("keydown")
                var downs = 0
                var ups = 0
                var clicks = 0
                win.RegisterKeyDown(func(int k, int s, int m, bool r) => { downs = downs + 1; throw thrown })
                win.RegisterKeyDown(func(int k, int s, int m, bool r) => { downs = downs + 10 })
                win.RegisterKeyUp(func(int k, int s, int m, bool r) => { ups = ups + 1 })
                win.RegisterMouseDown(func(int b, float x, float y) => { clicks = clicks + 1 })
                for (var i = 0; i < 2; i++) {
                    try {
                        var o = win.Tick()
                        print("tick " + i + " downs=" + downs + " ups=" + ups + " clicks=" + clicks)
                    } catch (Boom b) {
                        print("caught " + b.message + " same " + (b == thrown) + " downs=" + downs + " ups=" + ups + " clicks=" + clicks)
                    }
                }
                print("end")
                """, new[] { "caught keydown same True downs=1 ups=0 clicks=0", "tick 1 downs=1 ups=0 clicks=0", "end" }, "-DFIRE_DISPLAY_SELFTEST "),
                    // docs/spec/STACKTRACE.md section 4: the trace of the handler's exception has the frame of the native (Tick) below the handler's, then the
                    // frames of the fire code that called the native - here the handler runs as a member of the main program's group (fire_groupCall)
                    ("Fenster: der Trace eines Handlers hat den Frame des Natives (at __GRPHWinTick (native)) und die Frames darunter", """
                #import "windows"
                class H { static undefined Step(Window w) { w.Tick() } }
                var fb = new Framebuffer(64, 48)
                var win = new Window(fb, "trace")
                win.VSync = false
                win.RegisterKeyDown(func(int k, int s, int m, bool r) => { throw new Exception("key " + k) })
                try { H.Step(win) } catch (e) { print(e); print(e.StackTrace) }
                """, new[] { "Exception: key 97", "   at <lambda> (fire:source/0:6)", "   at __GRPHWinTick (native)", "   at Window.Tick (prelude)", "   at H.Step (fire:source/0:2)", "   at <main> (fire:source/0:7)" }, "-DFIRE_DISPLAY_SELFTEST "),
                    ("Fenster: ein Handler wirft unter WindowManager.Run - Run gibt die Exception weiter, der Manager ist danach wieder bereit", """
                #import "windows"
                var fb = new Framebuffer(64, 48)
                var mine = new WindowManager()
                var win = new Window(fb, "run", mine)
                var keys = 0
                win.RegisterKeyDown(func(int k, int s, int m, bool r) => { keys = keys + 1; throw new Exception("key " + k) })
                try { win.Run() } catch (e) { print("caught " + e.message + ", running " + mine.IsRunning + ", closed " + win.IsClosed) }
                win.Close()
                win.Run()
                print("second run done: left " + mine.Count + ", keys " + keys)
                """, new[] { "caught key 97, running False, closed False", "second run done: left 0, keys 1" }, "-DFIRE_DISPLAY_SELFTEST "),
                    ("Fenster: ein Handler wirft im Zyklus des gestarteten Managers (sicherer Punkt) - gemeldet, das Programm geht weiter", """
                #import "windows"
                var fb = new Framebuffer(64, 48)
                var win = new Window(fb, "shown")
                win.RegisterKeyDown(func(int k, int s, int m, bool r) => { print("key " + k); win.Close(); throw new Exception("async key") })
                win.Show()
                print("goes on")
                """, new[] { "goes on", "key 97", "(unhandled exception in the callback: Unhandled exception of type 'Exception': async key)" }, "-DFIRE_DISPLAY_SELFTEST "),
                };
                string? FindProgram(string name) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);
                string? xvfbRun = FindProgram("xvfb-run");
                // (the VM (forge run) installs the standard packages and builds the C of graphics and windows on its first start after a build - two minutes on a busy machine,
                // longer than the cases that run the VM give it on their clock (the reader of a case waits 90 s for the VM's text); and two first starts at the same time
                // install into the same folder. Done once, without a clock, before the first case that runs the VM; the others wait for it.)
                var vmFirstStart = new Lazy<string>(() =>
                {
                    string forgeTool = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "forge.exe" : "forge");
                    if (!File.Exists(forgeTool)) return "";
                    string warm = Path.Combine(workDir, "vm_first_start.fire");
                    File.WriteAllText(warm, "#import \"windows\"\n#import \"time\"\nprint(Clipboard.HasText)\n");
                    string said = RunProc(forgeTool, $"run \"{warm}\"", workDir, out int warmExit, null, new Dictionary<string, string?> { ["FIRE_DISPLAY"] = "headless" });
                    return warmExit == 0 ? "" : $"(der erste Start der VM, Exitcode {warmExit}:)\n{said}";
                }, LazyThreadSafetyMode.ExecutionAndPublication);
                // (what runs: the native window cases and the cases under Wine at the same time - see the end of this block)
                var windowRuns = new List<Action>();
                // Builds a case with fire's own display (packages/windows/native/fire_display_desktop.h) and runs it: `display` "dummy" (headless: the events of FIRE_DISPLAY_SELFTEST...), "x11" (a real X server: Xvfb), "x11-two"
                // (`script` is the reader, `second` the writer that starts 0.3 s after it on the same X server; the output is the reader's) or "x11-late" (the writer
                // first, the reader 3 s later - under the load of the parallel builds the writer needs a while for the clipboard: what was copied before the reader started). `secondCommand` runs instead of the writer built from `second`,
                // `firstCommand` instead of the reader built from `script` (the VM: forge run).
                // PrepareWindowCase generates, builds and runs a case (in parallel with other cases: TestJobs.Ordered - each run has an X server of its own,
                // TestJobs.XvfbServer) and returns the step that reports what happened, in the order of the cases.
                Action PrepareWindowCase(string title, string script, string[] expected, string define, string display = "dummy", string? second = null, string? secondCommand = null, string? firstCommand = null)
                {
                    var said = new List<Action>();
                    // (a unit of FIRE_TESTS_RESUME/FIRE_TESTS_SHARD, TestResume.cs: keyed by what is built and run - the folder of this run left out)
                    string unitName = "Fenster: " + title;
                    string unitKey = TestResume.Active ? TestResume.Key(script, string.Join("\n", expected), define, display, second, secondCommand?.Replace(workDir, ""), firstCommand?.Replace(workDir, ""), sanitize) : "";
                    switch (TestResume.Decide(unitName, unitKey))
                    {
                        case TestResume.Decision.Resumed: return () => TestResume.Reprint(unitName, unitKey);
                        case TestResume.Decision.OtherShard: return () => { };
                    }
                    Action Replay() => () => TestResume.Run(unitName, unitKey, () => { foreach (var say in said) say(); });
                    string label = title;
                    var target = TargetProfile.Host;
                    fire.Native.NativeSources Generate(string source) => fire.Native.CppGenerator.GenerateSources(new Linker().CompileAndLink(new[] { source }, null, null, VmExecutionMode.Release, null, target), target);
                    var generated = Generate(script);
                    string cpp = generated.Program;
                    said.Add(() => CheckNat($"Fenster: {label}: das Programm bittet nur um das, was das System hat (dl)", cpp.Contains("// fire-link: dl") && cpp.Contains("fire_display_c.h") && generated.Natives == null, ""));
                    if (display == "wayland" && FindProgram("weston") == null) { said.Add(() => Console.WriteLine($"(weston nicht da: {label} wird nicht ausgefuehrt)")); return Replay(); }
                    if (display != "dummy" && display != "wayland" && xvfbRun == null) { said.Add(() => Console.WriteLine($"(xvfb-run nicht da: {label} wird nicht ausgefuehrt)")); return Replay(); }
                    string Build(fire.Native.NativeSources sources, string name, out string exe)
                    {
                        exe = Path.Combine(workDir, name + ".bin");
                        File.WriteAllText(Path.Combine(workDir, name + ".c"), sources.Program);
                        var command = fire.Compiler.NativeBuilder.CompilerCommand(fire.Native.ToolchainDef.BuiltIn["gcc"], target, Path.Combine(workDir, name + ".c"), workDir, exe);
                        said.Add(() => CheckNat($"Fenster: {label}: der Build haengt -ldl an", command.Arguments.Contains("-ldl"), command.Arguments));
                        string log = NativeTestBuild.Build(NativeTestBuild.In(workDir), cxx, sources, workDir, name, $"-pthread {NativeTestBuild.Opt} -Wall -Wextra {sanitize}{define}", exe, out int buildExit, " -ldl -lpthread");
                        return buildExit != 0 || log.Contains("warning:") ? "C/C++-Compiler: " + log : "";
                    }
                    string name = "window_" + Math.Abs(title.GetHashCode());
                    string exe = "";
                    string failed = firstCommand == null ? Build(generated, name, out exe) : "";
                    string exe2 = "";
                    if (failed == "" && second != null && secondCommand == null) failed = Build(Generate(second), name + "_w", out exe2);
                    if (failed != "") { said.Add(() => CheckNat($"Fenster: {label}", false, failed)); return Replay(); }
                    // (the program runs here, on the worker: every X server is its own; what it did is reported by the step)
                    {
                        string actual;
                        int runExit;
                        if (display == "dummy") actual = RunProc(exe, "", workDir, out runExit, null, new Dictionary<string, string?> { ["FIRE_DISPLAY"] = "headless" });
                        else if (display == "wayland")
                        {
                            // (fire's own display on Wayland: a weston of its own without a screen - headless backend -, the program on its socket)
                            string runtime = Path.Combine(workDir, name + ".xdg");
                            Directory.CreateDirectory(runtime);
                            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(runtime, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                            string run = Path.Combine(workDir, name + ".sh");
                            File.WriteAllText(run, $"export XDG_RUNTIME_DIR=\"{runtime}\"\nweston --backend=headless-backend.so --socket=fire-test --idle-time=0 > /dev/null 2>&1 &\nw=$!\n" +
                                "for i in $(seq 50); do [ -S \"$XDG_RUNTIME_DIR/fire-test\" ] && break; sleep 0.1; done\n" +
                                $"WAYLAND_DISPLAY=fire-test FIRE_DISPLAY=wayland \"{exe}\"\nr=$?\nkill $w 2>/dev/null\nwait $w 2>/dev/null\nexit $r\n");
                            actual = RunProc("sh", $"\"{run}\"", workDir, out runExit);
                        }
                        else
                        {
                            var x11 = new Dictionary<string, string?> { ["FIRE_DISPLAY"] = "x11" };
                            if (second == null) actual = RunProc(xvfbRun!, $"{TestJobs.XvfbServer()} \"{exe}\"", workDir, out runExit, null, x11);
                            else
                            {
                                string readerOut = Path.Combine(workDir, name + ".out");
                                // (a script: the reader in the background, the writer 0.3 s later; the exit code says whether both ended well)
                                string both = Path.Combine(workDir, name + ".sh");
                                string reader = firstCommand ?? $"\"{exe}\"", writer = secondCommand ?? $"\"{exe2}\"";
                                File.WriteAllText(both, display == "x11-late"
                                    ? $"{writer} > /dev/null 2>&1 &\nw=$!\nsleep 3\n{reader} > \"{readerOut}\" 2>&1\nr=$?\nwait $w\nexit $(( $? + r ))\n"
                                    : $"{reader} > \"{readerOut}\" 2>&1 &\nr=$!\nsleep 0.3\n{writer} > /dev/null 2>&1\nw=$?\nwait $r\nexit $(( $? + w ))\n");
                                string writerLog = RunProc(xvfbRun!, $"{TestJobs.XvfbServer()} sh \"{both}\"", workDir, out runExit, null, x11);
                                actual = (File.Exists(readerOut) ? File.ReadAllText(readerOut) : "") + writerLog;
                                // (the VM as the reader: its first start after a build installs the standard packages and says so)
                                if (firstCommand != null)
                                    actual = string.Concat(actual.Split('\n').Where(l => l != "" && !l.StartsWith("Installing the standard package") && !l.StartsWith("Building the native part")).Select(l => l + "\n"));
                            }
                        }
                        string want = string.Concat(expected.Select(l => l + "\n"));
                        if (runExit == 0 && actual == want) { NativeTestBuild.Clean(workDir, name, exe); NativeTestBuild.Clean(workDir, name + "_w", exe2); }
                        said.Add(() => CheckNat($"Fenster: {label}", runExit == 0 && actual == want, $"  erwartet:\n{want}\n  erhalten (Exitcode {runExit}):\n{actual}"));
                    }
                    return Replay();
                }
                void RunWindowCase(string title, string script, string[] expected, string define, string display = "dummy", string? second = null, string? secondCommand = null, string? firstCommand = null) =>
                    PrepareWindowCase(title, script, expected, define, display, second, secondCommand, firstCommand)();
                // (the cases of windowCases and clipboardCases below: built and run in parallel, reported in their order)
                var windowSteps = new List<Func<Action>>();
                foreach (var wc in windowCases)
                {
                    if (!TestSelected(wc.Title)) continue;
                    windowSteps.Add(() => PrepareWindowCase(wc.Title, wc.Script, wc.Expected, wc.Define));
                    windowSteps.Add(() => PrepareWindowCase(wc.Title + " (Wayland)", wc.Script, wc.Expected, wc.Define, "wayland"));
                }

                // ---- hover, pictures and files on the clipboard; the real clipboard of X11 (Xvfb), also between two programs
                const string clipScript = """
                #import "windows"
                print(Clipboard.SetText("Grüße aus fire 😀"))
                print(Clipboard.HasText + " " + Clipboard.GetText() + " " + Clipboard.GetText().Length)
                var img = new Framebuffer(3, 2)
                var pr = new Renderer(img)
                pr.Clear()
                pr.SetPixel(0, 0, 0xFF0000FF)
                pr.SetPixel(1, 0, 0x8000FF00)
                var x0 = img.ReadBytes()
                var png = img.ToPng()
                var again = Framebuffer.FromImage(png)
                var x1 = again.ReadBytes()
                var same = x0.Length == x1.Length && png[1] == 80
                for (var i = 0; i < x0.Length; i++) { if (x0[i] != x1[i]) { same = false } }
                print("png " + same)
                print(Clipboard.SetImage(img) + " " + Clipboard.HasImage + " " + Clipboard.HasText + " " + Clipboard.HasFiles)
                var back = Clipboard.GetImage()
                if (back == undefined) { print("no image") } else {
                    var y1 = back.ReadBytes()
                    var same2 = x0.Length == y1.Length
                    for (var i = 0; i < x0.Length; i++) { if (x0[i] != y1[i]) { same2 = false } }
                    print(back.Width() + "x" + back.Height() + " " + same2)
                }
                var paths = new List<string>()
                paths.Add("/tmp/ein Ordner/datei ä.txt")
                paths.Add("/tmp/zwei")
                print(Clipboard.SetFiles(paths) + " " + Clipboard.HasFiles + " " + Clipboard.HasImage)
                var got = Clipboard.GetFiles()
                var line = "files " + got.length
                foreach (f in got) { line = line + " [" + f + "]" }
                print(line)
                Clipboard.SetText("Text")
                print(Clipboard.HasFiles + " " + Clipboard.GetFiles().length + " " + Clipboard.HasText + " " + (Clipboard.GetImage() == undefined))
                """;
                string[] clipExpected = { "True", "True Grüße aus fire 😀 17", "png True", "True True False False", "3x2 True", "True True False", "files 2 [/tmp/ein Ordner/datei ä.txt] [/tmp/zwei]", "False 0 True True" };
                const string writerScript = """
                #import "windows"
                #import "time"
                class W {
                    // answers the questions of the other program (each call takes in the events of the clipboard)
                    static undefined Serve(int ms) {
                        for (var i = 0; i < ms / 20; i++) {
                            var unused = Clipboard.HasText
                            Sleep(20ms)
                        }
                    }
                }
                W.Serve(500)
                Clipboard.SetText("Text aus dem anderen Programm ä")
                W.Serve(1500)
                var img = new Framebuffer(5, 4)
                var pr = new Renderer(img)
                pr.Clear()
                pr.SetPixel(0, 0, 0xFF0000FF)
                pr.SetPixel(4, 3, 0xFF00FF00)
                Clipboard.SetImage(img)
                W.Serve(1500)
                var paths = new List<string>()
                paths.Add("/tmp/ein Ordner/datei ä.txt")
                paths.Add("/tmp/zwei")
                Clipboard.SetFiles(paths)
                W.Serve(1500)
                """;
                const string readerScript = """
                #import "windows"
                #import "time"
                var t = 0
                while (!Clipboard.HasText && t < 4500) { Sleep(20ms); t++ }   // (up to 90 s: the writer may be the VM, whose first start installs the standard packages and builds their native parts - next to the other builds of the run)
                print("text [" + Clipboard.GetText() + "]")
                t = 0
                while (!Clipboard.HasImage && t < 120) { Sleep(20ms); t++ }
                var img = Clipboard.GetImage()
                if (img == undefined) { print("image none") } else {
                    var bytes = img.ReadBytes()
                    print("image " + img.Width() + "x" + img.Height() + " " + bytes[0] + " " + bytes[3] + " " + bytes[(3 * 5 + 4) * 4 + 1] + " " + Clipboard.HasText)
                }
                t = 0
                while (!Clipboard.HasFiles && t < 120) { Sleep(20ms); t++ }
                var files = Clipboard.GetFiles()
                var line = "files " + files.length
                foreach (f in files) { line = line + " [" + f + "]" }
                print(line)
                """;
                // (the writer copies first and serves until another program takes the clipboard; the reader starts 1 s later, asks once, then takes the clipboard)
                const string lateWriterScript = """
                #import "windows"
                #import "time"
                Clipboard.SetText("Text von vorher ä")
                for (var t = 0; t < 1500; t++) {
                    var s = Clipboard.GetText()
                    if (s == undefined) { break }
                    if (s != "Text von vorher ä") { break }
                    Sleep(20ms)
                }
                """;
                const string lateReaderScript = """
                #import "windows"
                print("has " + Clipboard.HasText)
                print("text [" + Clipboard.GetText() + "]")
                Clipboard.SetText("fertig")
                """;
                string[] lateExpected = { "has True", "text [Text von vorher ä]" };
                var clipboardCases = new (string Title, string Script, string[] Expected, string Define, string Display, string? Second)[]
                {
                    ("Fenster: DragHover und DragLeave, solange etwas aus einem anderen Programm ueber dem Fenster ist (Callbacks, Warteschlange)", """
                #import "windows"
                var fb = new Framebuffer(64, 48)
                var win = new Window(fb, "hover")
                win.RegisterDropFiles(func(any files, float x, float y) => { print("files [" + files[0] + "] at " + x + " " + y) })
                win.RegisterDragHover(func(float x, float y) => { print("hover " + x + " " + y) })
                win.RegisterDragLeave(func() => { print("leave") })
                win.EnableEvents()
                print(win.Tick())
                var e = win.NextEvent()
                while (e != undefined) {
                    var line = ""
                    for (var i = 0; i < e.length; i++) { if (e[0] != 40 || i != 1) { line = line + e[i] + " " } }
                    print("queued " + line)
                    e = win.NextEvent()
                }
                """, new[] { "hover 10 20", "hover 30 22", "leave", "hover 40 30", "files [/tmp/hier.txt] at 40 30", "True", "queued 42 10 20 ", "queued 42 30 22 ", "queued 43 ", "queued 42 40 30 ", "queued 40 40 30 " },
                        "-DFIRE_DISPLAY_SELFTEST_HOVER ", "dummy", null),
                    ("Fenster: Zwischenablage mit Bildern (PNG) und Dateien in einem Programm", clipScript, clipExpected, "", "dummy", null),
                    ("Fenster: die Zwischenablage von X11 (Xvfb): Text, Bild und Dateien in einem Programm", clipScript, clipExpected, "", "x11", null),
                    ("Fenster: die Zwischenablage von X11 (Xvfb) zwischen zwei Programmen: Text, Bild, Dateien", readerScript,
                        new[] { "text [Text aus dem anderen Programm ä]", "image 5x4 255 255 255 False", "files 2 [/tmp/ein Ordner/datei ä.txt] [/tmp/zwei]" }, "", "x11-two", writerScript),
                    ("Fenster: die Zwischenablage von X11 (Xvfb): was ein anderes Programm vor dem Start kopiert hat, ist gleich bei der ersten Frage da", lateReaderScript,
                        lateExpected, "", "x11-late", lateWriterScript),
                };
                // ---- Window.Fullscreen: the window goes over the whole screen and back - the sizes come as resize events (AutoResize: the framebuffer follows);
                //      headless: the screen of FIRE_DISPLAY_SCREEN (1920 x 1080), X11 without a window manager (Xvfb, 1280 x 1024 of xvfb-run): an override-redirect
                //      window, Wayland: xdg_toplevel.set_fullscreen on weston's headless output (1024 x 640; fire's own title bar is away meanwhile)
                const string fullscreenScript = """
                #import "windows"
                #import "time"
                var fb = new Framebuffer(64, 48)
                var win = new Window(fb, "fullscreen")
                win.VSync = false
                win.AutoResize = true
                print(win.Fullscreen + " " + win.IsFullscreen)
                print(win.Tick())
                print("before " + fb.Width() + "x" + fb.Height() + " " + win.Fullscreen + " " + win.IsFullscreen)
                win.Fullscreen = true
                print("asked " + win.Fullscreen)
                var n = 0
                while ((!win.IsFullscreen || fb.Width() == 64) && n < 500) { win.Tick(); Sleep(10ms); n = n + 1 }
                print("during " + fb.Width() + "x" + fb.Height() + " " + win.Fullscreen + " " + win.IsFullscreen)
                win.Fullscreen = false
                n = 0
                while ((win.IsFullscreen || fb.Width() != 64 || fb.Height() != 48) && n < 500) { win.Tick(); Sleep(10ms); n = n + 1 }
                print("after " + fb.Width() + "x" + fb.Height() + " " + win.Fullscreen + " " + win.IsFullscreen)
                print(win.Tick())
                """;
                string[] FullscreenExpected(string screen) => new[] { "False False", "True", "before 64x48 False False", "asked True", $"during {screen} True True", "after 64x48 False False", "True" };
                var fullscreenCases = new (string Title, string Script, string[] Expected, string Define, string Display, string? Second)[]
                {
                    ("Fenster: Fullscreen ohne Bildschirm (headless) - die Groesse des Bildschirms als Resize, danach die alte Groesse", fullscreenScript, FullscreenExpected("1920x1080"), "", "dummy", null),
                    ("Fenster: Fullscreen auf X11 ohne Fenstermanager (Xvfb) - ein override-redirect-Fenster ueber den Bildschirm und zurueck", fullscreenScript, FullscreenExpected("1280x1024"), "", "x11", null),
                    ("Fenster: Fullscreen auf Wayland (weston) - xdg_toplevel.set_fullscreen, die Groesse aus configure, ohne eigene Titelleiste, danach die alte Groesse", fullscreenScript, FullscreenExpected("1024x640"), "", "wayland", null),
                };
                foreach (var fc in fullscreenCases)
                {
                    if (!TestSelected(fc.Title)) continue;
                    windowSteps.Add(() => PrepareWindowCase(fc.Title, fc.Script, fc.Expected, fc.Define, fc.Display, fc.Second));
                }
                foreach (var cc in clipboardCases)
                {
                    if (!TestSelected(cc.Title)) continue;
                    windowSteps.Add(() => PrepareWindowCase(cc.Title, cc.Script, cc.Expected, cc.Define, cc.Display, cc.Second));
                }
                // ---- the waits of the main program serve its windows (ROADMAP_TASKS 5b): while it sleeps, the window is unmapped and mapped again (xdotool) - the
                //      Expose that follows is answered at once (the window shows its last frame again - blue: the brush's colour is ABGR), not only at the next
                //      Tick (before: black until then)
                {
                    // (the window, once there: unmapped, mapped again; then the colour of a pixel - the exit code says whether it is the frame's)
                    string ExposeCheck(string name)
                    {
                        string file = Path.Combine(workDir, name + ".sh");
                        File.WriteAllText(file, $"id=$(xdotool search --sync --name '^idlepump$' | head -1)\nsleep 1\nxdotool windowunmap --sync $id\nxdotool windowmap --sync $id\nsleep 0.5\n" +
                            $"c=$(import -window $id -depth 8 -crop 1x1+5+5 txt:- | tail -1)\necho \"$c\" > \"{workDir}/{name}.txt\"\ncase \"$c\" in *0000FF*) exit 0;; esac\nexit 1\n");
                        return file;
                    }
                    string idleTitle = "X11 (Xvfb) - waehrend das Hauptprogramm wartet (Sleep), liest es die Ereignisse seiner Fenster: ein Expose zeigt das letzte Bild wieder";
                    if (TestSelected(idleTitle) && FindProgram("xdotool") != null && FindProgram("import") != null && !OperatingSystem.IsWindows())
                    {
                        string check = ExposeCheck("idle_expose");
                        windowSteps.Add(() => PrepareWindowCase(idleTitle, """
                            #import "windows"
                            #import "time"
                            var fb = new Framebuffer(64, 48)
                            var r = new Renderer(fb)
                            var win = new Window(fb, "idlepump")
                            win.VSync = false
                            r.FillRect(0, 0, 64, 48, new SolidBrush(0xFFFF0000))
                            win.Tick()
                            win.Tick()
                            Sleep(4000)
                            print(win.Tick())
                            """, new[] { "True" }, "", "x11-two", "", $"sh \"{check}\""));
                    }
                    else if (TestSelected(idleTitle)) Console.WriteLine("(Expose waehrend Sleep: uebersprungen - xdotool oder import fehlt)");
                    // (the same in the VM: its windows are those of the C library, its waits give the library a turn - fire_pkg_idle)
                    string idleVmTitle = idleTitle + " [die VM]";
                    string idleForge = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "forge.exe" : "forge");
                    if (TestSelected(idleVmTitle) && File.Exists(idleForge) && FindProgram("xdotool") != null && FindProgram("import") != null && !OperatingSystem.IsWindows())
                    {
                        string check = ExposeCheck("idle_expose_vm");
                        string script = Path.Combine(workDir, "idle_expose_vm.fire");
                        string vmSource = """
                            #import "windows"
                            #import "time"
                            var fb = new Framebuffer(64, 48)
                            var r = new Renderer(fb)
                            var win = new Window(fb, "idlepump")
                            win.VSync = false
                            r.FillRect(0, 0, 64, 48, new SolidBrush(0xFFFF0000))
                            win.Tick()
                            win.Tick()
                            Sleep(6000)
                            print(win.Tick())
                            """;
                        File.WriteAllText(script, vmSource);
                        windowSteps.Add(() => { _ = vmFirstStart.Value; return PrepareWindowCase(idleVmTitle, vmSource, new[] { "True" }, "", "x11-two", "", $"sh \"{check}\"", $"\"{idleForge}\" run \"{script}\""); });
                    }
                }
                windowRuns.Add(() => TestJobs.Ordered(windowSteps.Count, i => windowSteps[i](), (_, run) => run(), what: "Fenster: gebaut"));

                // ---- the VM (the C library: fire's own display) on X11: it puts text, a picture and files on the clipboard (forge run under Xvfb), a native program reads them
                windowRuns.Add(() =>
                {
                    string title = "Fenster: die VM (die C-Bibliothek) auf X11 (Xvfb) gibt einem nativen Programm Text, Bild und Dateien ueber die Zwischenablage";
                    string forge = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "forge.exe" : "forge");
                    if (!TestSelected(title)) { }
                    else if (!File.Exists(forge)) Console.WriteLine("(die VM unter Xvfb: uebersprungen - forge fehlt)");
                    else
                    {
                        string writer = Path.Combine(workDir, "vmwriter.fire");
                        File.WriteAllText(writer, writerScript);
                        if (vmFirstStart.Value is { Length: > 0 } firstStart) Console.WriteLine(firstStart);
                        RunWindowCase(title, readerScript, new[] { "text [Text aus dem anderen Programm ä]", "image 5x4 255 255 255 False", "files 2 [/tmp/ein Ordner/datei ä.txt] [/tmp/zwei]" },
                            "", "x11-two", writerScript, $"\"{forge}\" run \"{writer}\"");
                    }
                });
                // ---- the VM as the reader that starts after a native program copied (the VM's windows are the C library's: the same display as the native program)
                windowRuns.Add(() =>
                {
                    string title = "Fenster: die VM (die C-Bibliothek, fire's eigene Anzeige) auf X11 (Xvfb) sieht, was ein natives Programm vor ihrem Start kopiert hat";
                    string forge = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "forge.exe" : "forge");
                    if (!TestSelected(title)) { }
                    else if (!File.Exists(forge)) Console.WriteLine("(die VM unter Xvfb: uebersprungen - forge fehlt)");
                    else
                    {
                        string reader = Path.Combine(workDir, "vmlatereader.fire");
                        File.WriteAllText(reader, lateReaderScript);
                        if (vmFirstStart.Value is { Length: > 0 } firstStart) Console.WriteLine(firstStart);
                        RunWindowCase(title, lateReaderScript, lateExpected, "", "x11-late", lateWriterScript, null, $"\"{forge}\" run \"{reader}\"");
                    }
                });

                // ---- Window.PresentArea on X11: only the reported area goes to the X server (a picture of the screen by ImageMagick's import shows it); idle frames
                //      with an empty area overwrite nothing
                windowRuns.Add(() =>
                {
                    string title = "Fenster: PresentArea auf X11 (Xvfb) - nur der gemeldete Bereich geht an den X-Server, leere Bereiche im Leerlauf ueberschreiben nichts";
                    string forge = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "forge.exe" : "forge");
                    string? xvfbRunTool = FindProgram("xvfb-run"), importTool = FindProgram("import");
                    if (!TestSelected(title) || !TestResume.Pending(title, NatUnitKey(title))) { }
                    else if (!File.Exists(forge) || xvfbRunTool == null || importTool == null || OperatingSystem.IsWindows()) Console.WriteLine("(PresentArea unter Xvfb: uebersprungen - forge, xvfb-run oder import fehlt)");
                    else
                    {
                        // (a unit of FIRE_TESTS_RESUME/FIRE_TESTS_SHARD: TestResume.cs)
                        using var resumeUnit6 = TestResume.Unit(title, NatUnitKey(title));
                        string dir = Path.Combine(workDir, "presentarea");
                        Directory.CreateDirectory(dir);
                        File.WriteAllText(Path.Combine(dir, "area.fire"), """
                            #import "windows"
                            var fb = new Framebuffer(200, 100)
                            var r = new Renderer(fb)
                            var win = new Window(fb, "area")
                            win.VSync = false
                            r.FillRect(0, 0, 200, 100, new SolidBrush(0xFF0000FF))
                            win.Tick()
                            win.Tick()
                            r.FillRect(20, 20, 30, 30, new SolidBrush(0xFFFF0000))
                            r.FillRect(120, 20, 30, 30, new SolidBrush(0xFF00FF00))
                            win.PresentArea(20, 20, 30, 30)
                            win.Tick()
                            print("ready")
                            win.VSync = true
                            for (var i = 0; i < 120; i = i + 1) {
                                win.PresentArea(0, 0, 0, 0)
                                win.Tick()
                            }
                            """);
                        File.WriteAllText(Path.Combine(dir, "run.sh"),
                            $"FIRE_DISPLAY=x11 \"{forge}\" run \"{dir}/area.fire\" > \"{dir}/out.txt\" 2>&1 &\n" +
                            $"for i in $(seq 1 300); do grep -q ready \"{dir}/out.txt\" 2>/dev/null && break; sleep 0.1; done\nsleep 0.5\n" +
                            $"import -window root -depth 8 \"{dir}/screen.ppm\"\nwait\n");
                        var psi = new System.Diagnostics.ProcessStartInfo(xvfbRunTool, $"-a -s \"-screen 0 400x300x24\" sh \"{dir}/run.sh\"") { RedirectStandardOutput = true, RedirectStandardError = true };
                        using var proc = System.Diagnostics.Process.Start(psi)!;
                        proc.StandardOutput.ReadToEnd(); proc.StandardError.ReadToEnd();
                        proc.WaitForExit(120000);
                        // the picture: inside the area blue, outside it still red (the green was not reported)
                        string screenFile = Path.Combine(dir, "screen.ppm");
                        string got = File.Exists(screenFile) ? ScreenPictures.Area(File.ReadAllBytes(screenFile)) : "kein Bild";
                        CheckNat(title, got == "innen (0, 0, 255) aussen (255, 0, 0)", got + "\n" + (File.Exists(Path.Combine(dir, "out.txt")) ? File.ReadAllText(Path.Combine(dir, "out.txt")) : ""));
                        // part 4.4: an accelerated framebuffer in a window on X11 - its frames go from the GPU into the window (a swap chain of lavapipe: FIRE_GFX_GPU=cpu);
                        // after the first frames nothing is read back, and the picture of the screen shows what the GPU drew
                        string gpuTitle = "Fenster: ein beschleunigter Framebuffer auf X11 (Xvfb, lavapipe) - die Frames gehen von der GPU ins Fenster, nichts wird mehr zurueckgelesen";
                        File.WriteAllText(Path.Combine(dir, "gpu.fire"), """
                            #import "windows"
                            #import "time"
                            var fb = new Framebuffer(200, 100)
                            fb.Accelerated = true
                            var r = new Renderer(fb)
                            var win = new Window(fb, "gpu")
                            win.VSync = true
                            r.FillRect(0, 0, 200, 100, new SolidBrush(0xFF0000FF))
                            win.Tick()
                            win.Tick()
                            win.Tick()
                            var before = Framebuffer.GpuReadbacks()
                            r.FillRect(20, 20, 30, 30, new SolidBrush(0xFFFF0000))
                            for (var i = 0; i < 10; i = i + 1) { win.Tick() }
                            r.FillRect(120, 20, 30, 30, new SolidBrush(0xFF00FF00))
                            for (var i = 0; i < 10; i = i + 1) { win.Tick() }
                            print("gpu " + fb.Accelerated + " " + (Framebuffer.GpuReadbacks() - before) + " " + Framebuffer.GpuError())
                            print("ready")
                            for (var i = 0; i < 100; i = i + 1) {
                                win.Tick()
                                Sleep(16)
                            }
                            """);
                        File.WriteAllText(Path.Combine(dir, "gpu.sh"),
                            $"FIRE_GFX_GPU=cpu FIRE_DISPLAY=x11 \"{forge}\" run \"{dir}/gpu.fire\" > \"{dir}/gpu.txt\" 2>&1 &\n" +
                            $"for i in $(seq 1 600); do grep -q ready \"{dir}/gpu.txt\" 2>/dev/null && break; sleep 0.1; done\nsleep 0.5\n" +
                            $"import -window root -depth 8 \"{dir}/gpu.ppm\"\nwait\n");
                        var gpsi = new System.Diagnostics.ProcessStartInfo(xvfbRunTool, $"-a -s \"-screen 0 400x300x24\" sh \"{dir}/gpu.sh\"") { RedirectStandardOutput = true, RedirectStandardError = true };
                        using (var gproc = System.Diagnostics.Process.Start(gpsi)!)
                        {
                            gproc.StandardOutput.ReadToEnd(); gproc.StandardError.ReadToEnd();
                            gproc.WaitForExit(180000);
                        }
                        string gpuOut = File.Exists(Path.Combine(dir, "gpu.txt")) ? File.ReadAllText(Path.Combine(dir, "gpu.txt")) : "";
                        if (gpuOut.Contains("gpu False")) Console.WriteLine("(beschleunigter Framebuffer unter Xvfb: uebersprungen - kein Vulkan-Geraet)");
                        else
                        {
                            string gpuFile = Path.Combine(dir, "gpu.ppm");
                            string gpuGot = File.Exists(gpuFile) ? ScreenPictures.Area(File.ReadAllBytes(gpuFile)) : "kein Bild";
                            CheckNat(gpuTitle, gpuGot == "innen (0, 0, 255) aussen (0, 255, 0)" && gpuOut.Contains("gpu True 0 "), gpuGot + "\n" + gpuOut);
                        }
                        // Wayland (weston with its X11 backend in Xvfb): two buffers in turn - each gets what it missed when it is drawn next; three squares reported one after
                        // the other must all be there (and what is between them unchanged)
                        string wlTitle = "Fenster: PresentArea auf Wayland (weston unter Xvfb) - zwei Puffer im Wechsel: drei nacheinander gemeldete Bereiche sind alle da, der Rest unveraendert";
                        if (FindProgram("weston") is not { } westonTool) Console.WriteLine("(PresentArea unter Wayland: uebersprungen - weston fehlt)");
                        else
                        {
                            string runtime = Path.Combine(dir, "wlrun");
                            Directory.CreateDirectory(runtime);
                            File.WriteAllText(Path.Combine(dir, "squares.fire"), """
                                #import "windows"
                                var fb = new Framebuffer(200, 100)
                                var r = new Renderer(fb)
                                var win = new Window(fb, "area")
                                win.VSync = false
                                r.FillRect(0, 0, 200, 100, new SolidBrush(0xFF0000FF))
                                win.Tick()
                                win.Tick()
                                var blue = new SolidBrush(0xFFFF0000)
                                r.FillRect(20, 20, 30, 30, blue)
                                win.PresentArea(20, 20, 30, 30)
                                win.Tick()
                                r.FillRect(120, 20, 30, 30, blue)
                                win.PresentArea(120, 20, 30, 30)
                                win.Tick()
                                r.FillRect(70, 60, 30, 30, blue)
                                win.PresentArea(70, 60, 30, 30)
                                win.Tick()
                                print("ready")
                                win.VSync = true
                                for (var i = 0; i < 120; i = i + 1) {
                                    win.PresentArea(0, 0, 0, 0)
                                    win.Tick()
                                }
                                """);
                            string socket = $"wl-area-{Environment.ProcessId}";
                            File.WriteAllText(Path.Combine(dir, "wl.sh"),
                                $"export XDG_RUNTIME_DIR=\"{runtime}\"\nchmod 700 \"$XDG_RUNTIME_DIR\"\n" +
                                $"printf '[core]\\nidle-time=0\\n[shell]\\npanel-position=none\\nbackground-color=0xff000000\\n' > \"{runtime}/weston.ini\"\n" +
                                $"\"{westonTool}\" --backend=x11 --renderer=pixman --width=640 --height=480 --socket={socket} --config=\"{runtime}/weston.ini\" > \"{runtime}/weston.log\" 2>&1 &\nwp=$!\n" +
                                $"for i in $(seq 1 100); do [ -S \"$XDG_RUNTIME_DIR/{socket}\" ] && break; sleep 0.1; done\n" +
                                $"WAYLAND_DISPLAY={socket} FIRE_DISPLAY=wayland \"{forge}\" run \"{dir}/squares.fire\" > \"{dir}/wl.txt\" 2>&1 &\npp=$!\n" +
                                $"for i in $(seq 1 300); do grep -q ready \"{dir}/wl.txt\" 2>/dev/null && break; sleep 0.1; done\nsleep 1\n" +
                                $"import -window root -depth 8 \"{dir}/wl.ppm\"\nwait $pp\nkill $wp\n");
                            var wpsi = new System.Diagnostics.ProcessStartInfo(xvfbRunTool, $"-a -s \"-screen 0 800x600x24\" sh \"{dir}/wl.sh\"") { RedirectStandardOutput = true, RedirectStandardError = true };
                            using var wproc = System.Diagnostics.Process.Start(wpsi)!;
                            wproc.StandardOutput.ReadToEnd(); wproc.StandardError.ReadToEnd();
                            wproc.WaitForExit(120000);
                            string wlGot = File.Exists(Path.Combine(dir, "wl.ppm")) ? ScreenPictures.Squares(File.ReadAllBytes(Path.Combine(dir, "wl.ppm"))) : "kein Bild";
                            CheckNat(wlTitle, wlGot == "(0, 0, 255) (0, 0, 255) (0, 0, 255) (255, 0, 0) (255, 0, 0)", wlGot + "\n" + (File.Exists(Path.Combine(dir, "wl.txt")) ? File.ReadAllText(Path.Combine(dir, "wl.txt")) : ""));
                            // part 4.4 with a transparent backdrop: an accelerated framebuffer goes premultiplied from the GPU into a swap chain that mixes (weston shows it over
                            // its black background) - the same picture as in software, and nothing is read back (FIRE_WAYLAND_CSD=0: no title bar of fire's own, which keeps
                            // a window on the pixels)
                            string trTitle = "Fenster: ein beschleunigter Framebuffer mit transparentem Hintergrund auf Wayland (weston, lavapipe) - vormultipliziert von der GPU ins Fenster, wie in Software, nichts zurueckgelesen";
                            var trGot = new List<string>();
                            foreach (bool acc in new[] { true, false })
                            {
                                string name = acc ? "trgpu" : "trsw";
                                File.WriteAllText(Path.Combine(dir, name + ".fire"), """
                                    #import "windows"
                                    #import "time"
                                    var fb = new Framebuffer(200, 100)
                                    fb.Accelerated = ACC
                                    var r = new Renderer(fb)
                                    var win = new Window(fb, "tr")
                                    win.VSync = true
                                    win.Backdrop = Backdrop.Transparent
                                    r.AlphaBlending = false
                                    r.FillRect(0, 0, 200, 100, new SolidBrush(0x800000FF))
                                    r.FillRect(20, 20, 30, 30, new SolidBrush(0xFFFF0000))
                                    win.Tick()
                                    win.Tick()
                                    win.Tick()
                                    var before = Framebuffer.GpuReadbacks()
                                    for (var i = 0; i < 20; i = i + 1) {
                                        r.FillRect(120, 20, 30, 30, new SolidBrush(0x4000FF00))
                                        win.Tick()
                                    }
                                    print("gpu " + fb.Accelerated + " " + (Framebuffer.GpuReadbacks() - before) + " " + win.Backdrop)
                                    print("ready")
                                    for (var i = 0; i < 100; i = i + 1) {
                                        win.Tick()
                                        Sleep(16)
                                    }
                                    """.Replace("ACC", acc ? "true" : "false"));
                                string trSocket = $"wl-{name}-{Environment.ProcessId}";
                                File.WriteAllText(Path.Combine(dir, name + ".sh"),
                                    $"export XDG_RUNTIME_DIR=\"{runtime}\"\nchmod 700 \"$XDG_RUNTIME_DIR\"\n" +
                                    $"\"{westonTool}\" --backend=x11 --renderer=pixman --width=640 --height=480 --socket={trSocket} --config=\"{runtime}/weston.ini\" > \"{runtime}/{name}.log\" 2>&1 &\nwp=$!\n" +
                                    $"for i in $(seq 1 100); do [ -S \"$XDG_RUNTIME_DIR/{trSocket}\" ] && break; sleep 0.1; done\n" +
                                    $"FIRE_WAYLAND_CSD=0 FIRE_GFX_GPU=cpu WAYLAND_DISPLAY={trSocket} FIRE_DISPLAY=wayland \"{forge}\" run \"{dir}/{name}.fire\" > \"{dir}/{name}.txt\" 2>&1 &\npp=$!\n" +
                                    $"for i in $(seq 1 300); do grep -q ready \"{dir}/{name}.txt\" 2>/dev/null && break; sleep 0.1; done\nsleep 1\n" +
                                    $"import -window root -depth 8 \"{dir}/{name}.ppm\"\nwait $pp\nkill $wp\n");
                                var tpsi = new System.Diagnostics.ProcessStartInfo(xvfbRunTool, $"-a -s \"-screen 0 800x600x24\" sh \"{dir}/{name}.sh\"") { RedirectStandardOutput = true, RedirectStandardError = true };
                                using (var tproc = System.Diagnostics.Process.Start(tpsi)!)
                                {
                                    tproc.StandardOutput.ReadToEnd(); tproc.StandardError.ReadToEnd();
                                    tproc.WaitForExit(120000);
                                }
                                string outText = File.Exists(Path.Combine(dir, name + ".txt")) ? File.ReadAllText(Path.Combine(dir, name + ".txt")) : "";
                                string line = outText.Split('\n').FirstOrDefault(l => l.StartsWith("gpu ")) ?? "keine Ausgabe";
                                trGot.Add(line.Trim() + " | " + (File.Exists(Path.Combine(dir, name + ".ppm")) ? ScreenPictures.Mixed(File.ReadAllBytes(Path.Combine(dir, name + ".ppm"))) : "kein Bild"));
                            }
                            if (trGot[0].StartsWith("gpu False")) Console.WriteLine("(transparenter Hintergrund auf der GPU: uebersprungen - kein Vulkan-Geraet)");
                            else CheckNat(trTitle, trGot[0] == "gpu True 0 Transparent | (0, 0, 255) (0, 64, 0)" && trGot[1] == "gpu False 0 Transparent | (0, 0, 255) (0, 64, 0)", string.Join("\n", trGot));
                        }
                        resumeUnit6.Done();
                    }
                });

                // ---- drag-out (Window.StartDrag): a fire window drags a text and then two files into another program. X11 under Xvfb (XDND): the target is a native fire
                // program that prints what is dropped on it; the source a native fire window (and once on the thread of its manager: Window.Show), and the VM's
                // window (forge run) - all with fire's own display (its XDND part, fire_dragout.h). The hand is a small X program that presses, moves and lets go with XTest (libXtst, loaded at run time).
                // Both windows open in the middle of the screen: the hand drags from the middle of the source to a point near the left edge of the bigger target.
                const string dragDriverSource = """
                    #include <X11/Xlib.h>
                    #include <dlfcn.h>
                    #include <stdio.h>
                    #include <stdlib.h>
                    #include <string.h>
                    #include <time.h>
                    typedef int (*FakeButton)(Display*, unsigned int, int, unsigned long);
                    typedef int (*FakeMotion)(Display*, int, int, int, unsigned long);
                    static void pause_ms(int ms) { struct timespec t; t.tv_sec = ms / 1000; t.tv_nsec = (long)(ms % 1000) * 1000000L; nanosleep(&t, NULL); }
                    static Window find(Display* d, Window w, const char* name) {
                        Window root, parent, *kids = NULL, found = 0;
                        unsigned int n = 0, i;
                        char* title = NULL;
                        if (XFetchName(d, w, &title) && title) { int same = strcmp(title, name) == 0; XFree(title); if (same) return w; }
                        if (!XQueryTree(d, w, &root, &parent, &kids, &n)) return 0;
                        for (i = 0; i < n && !found; i++) found = find(d, kids[i], name);
                        if (kids) XFree(kids);
                        return found;
                    }
                    static int point(Display* d, Window w, int* x, int* y, int left) {
                        XWindowAttributes a;
                        Window child;
                        if (!XGetWindowAttributes(d, w, &a)) return 0;
                        XTranslateCoordinates(d, w, DefaultRootWindow(d), left ? 20 : a.width / 2, a.height / 2, x, y, &child);
                        return 1;
                    }
                    int main(int argc, char** argv) {
                        Display* d = XOpenDisplay(NULL);
                        void* xtst = dlopen("libXtst.so.6", RTLD_NOW);
                        FakeButton button;
                        FakeMotion motion;
                        Window src = 0, dst = 0;
                        int times = argc > 3 ? atoi(argv[3]) : 1, k, i, sx, sy, dx, dy;
                        if (d == NULL || xtst == NULL || argc < 3) { fprintf(stderr, "driver: no display or no libXtst\n"); return 2; }
                        button = (FakeButton)dlsym(xtst, "XTestFakeButtonEvent");
                        motion = (FakeMotion)dlsym(xtst, "XTestFakeMotionEvent");
                        if (!button || !motion) { fprintf(stderr, "driver: XTest incomplete\n"); return 2; }
                        for (k = 0; k < 300 && (!src || !dst); k++) {
                            src = find(d, DefaultRootWindow(d), argv[1]);
                            dst = find(d, DefaultRootWindow(d), argv[2]);
                            if (!src || !dst) pause_ms(100);
                        }
                        if (!src || !dst) { fprintf(stderr, "driver: windows not found\n"); return 2; }
                        pause_ms(1000);
                        for (k = 0; k < times; k++) {
                            if (!point(d, src, &sx, &sy, 0) || !point(d, dst, &dx, &dy, 1)) return 2;
                            motion(d, -1, sx, sy, 0); XSync(d, False); pause_ms(300);
                            button(d, 1, True, 0); XSync(d, False); pause_ms(400);
                            for (i = 1; i <= 12; i++) { motion(d, -1, sx + (dx - sx) * i / 12, sy + (dy - sy) * i / 12, 0); XSync(d, False); pause_ms(40); }
                            pause_ms(300);
                            button(d, 1, False, 0); XSync(d, False);
                            pause_ms(1500);
                        }
                        XCloseDisplay(d);
                        return 0;
                    }
                    """;
                string? dragDriver = null;
                bool dragDriverTried = false;
                // (built once, when a case needs it - the native drag cases and those under Wine ask at the same time: the headers of X11 are needed; libXtst only at run time)
                string? DragDriver()
                {
                    lock (windowRuns) return DragDriverOnce();
                }
                string? DragDriverOnce()
                {
                    if (dragDriverTried) return dragDriver;
                    dragDriverTried = true;
                    string c = Path.Combine(workDir, "dragdriver.c");
                    File.WriteAllText(c, dragDriverSource);
                    string exe = Path.Combine(workDir, "dragdriver.bin");
                    RunProc(fire.Native.ToolchainDef.CCompilerFor(cxx), $"-O1 \"{c}\" -o \"{exe}\" -lX11 -ldl", workDir, out int code);
                    dragDriver = code == 0 ? exe : null;
                    return dragDriver;
                }
                string DragSourceScript(string path1, string path2, bool show) => """
                    #import "windows"
                    #import "time"
                    var fb = new Framebuffer(64, 48)
                    var win = new Window(fb, "fire drag source")
                    var drags = 0
                    win.RegisterMouseDown(func(int b, float x, float y) => {
                        drags++
                        if (drags == 1) { print("text " + win.StartDrag("Text aus fire ä", undefined)) }
                        else {
                            var files = new List<string>()
                            files.Add("PATH1")
                            files.Add("PATH2")
                            print("files " + win.StartDrag("", files))
                            CLOSE
                        }
                    })

                    """.Replace("PATH1", path1).Replace("PATH2", path2).Replace("CLOSE", show ? "win.Close()" : "")
                    + (show ? "win.Show()\n" : "var t = 0\nwhile (drags < 2 && t < 1500) { win.Tick(); Sleep(10ms); t++ }\n");
                const string dragTargetScript = """
                    #import "windows"
                    #import "time"
                    var fb = new Framebuffer(400, 300)
                    var win = new Window(fb, "fire drop target")
                    var got = 0
                    win.RegisterDropText(func(string text, float x, float y) => {
                        print("dropped text [" + text + "]")
                        got++
                    })
                    win.RegisterDropFiles(func(any files, float x, float y) => {
                        var line = "dropped files " + files.length
                        foreach (f in files) { line = line + " [" + f + "]" }
                        print(line)
                        got++
                    })
                    var t = 0
                    while (got < 2 && t < 1500) { win.Tick(); Sleep(10ms); t++ }
                    """;
                // (the script for the X server: the target, half a second later the source, then the hand; the outputs of source and target; the exit code: all three ended well)
                // (what the programs printed about the drag - not what forge or Wine say besides: packages it installs, the end of the X connection)
                string DragLines(string file) => File.Exists(file)
                    ? string.Concat(File.ReadAllText(file).Replace("\r", "").Split('\n').Where(l => l.StartsWith("text ", StringComparison.Ordinal) || l.StartsWith("files ", StringComparison.Ordinal) || l.StartsWith("dropped ", StringComparison.Ordinal)).Select(l => l + "\n"))
                    : "";
                string DragScript(string target, string source, string driver, string outSource, string outTarget, string delay = "0.5") =>
                    $"{target} > \"{outTarget}\" 2>&1 &\nt=$!\nsleep {delay}\n{source} > \"{outSource}\" 2>&1 &\ns=$!\n\"{driver}\" \"fire drag source\" \"fire drop target\" 2\nd=$?\nwait $s\nse=$?\nwait $t\nte=$?\nexit $(( d + se + te ))\n";
                windowRuns.Add(() =>
                {
                    string forge = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "forge.exe" : "forge");
                    var dragCases = new (string Label, bool Show, bool Vm)[] { ("die VM (die C-Bibliothek)", false, true), ("fire", false, false), ("fire, Window.Show: der Thread des Managers zieht", true, false) };
                    string[] dragExpected = { "text 1", "files 1", "dropped text [Text aus fire ä]", "dropped files 2 [/tmp/ein Ordner/datei ä.txt] [/tmp/zwei]" };
                    string DragBuild(string script, string name)
                    {
                        var target = TargetProfile.Host;
                        var sources = fire.Native.CppGenerator.GenerateSources(new Linker().CompileAndLink(new[] { script }, null, null, VmExecutionMode.Release, null, target), target);
                        string exe = Path.Combine(workDir, name + ".bin");
                        string buildLog = NativeTestBuild.Build(NativeTestBuild.In(workDir), cxx, sources, workDir, name, $"-pthread {NativeTestBuild.Opt} -Wall -Wextra {sanitize}", exe, out int buildExit, " -ldl -lpthread");
                        if (buildExit != 0 || buildLog.Contains("warning:")) throw new InvalidOperationException("C/C++-Compiler: " + buildLog);
                        return exe;
                    }
                    // (the target once for all cases; the cases built in parallel, run one after the other - one X server at a time)
                    var dragTarget = new Lazy<string>(() => DragBuild(dragTargetScript, "dragtarget"));
                    var dragSteps = new List<Func<Action>>();
                    foreach (var dc in dragCases)
                    {
                        string title = $"Fenster: Drag aus dem Fenster in ein anderes Programm (X11, Xvfb, XDND): Text und Dateien, die Antwort Kopie [{dc.Label}]";
                        if (!TestSelected(title)) continue;
                        dragSteps.Add(() =>
                        {
                            if (xvfbRun == null) return () => Console.WriteLine($"(uebersprungen - xvfb-run fehlt: {title})");
                            if (dc.Vm && !File.Exists(forge)) return () => Console.WriteLine($"(uebersprungen - forge fehlt: {title})");
                            string? driver = DragDriver();
                            if (driver == null) return () => Console.WriteLine($"(uebersprungen - die Hand (X11-Programm) laesst sich nicht bauen: {title})");
                            string sourceCommand;
                            try
                            {
                                _ = dragTarget.Value;
                                string sourceScript = DragSourceScript("/tmp/ein Ordner/datei ä.txt", "/tmp/zwei", dc.Show);
                                if (dc.Vm)
                                {
                                    string file = Path.Combine(workDir, "dragsource.fire");
                                    File.WriteAllText(file, sourceScript);
                                    sourceCommand = $"\"{forge}\" run \"{file}\"";
                                }
                                else sourceCommand = "\"" + DragBuild(sourceScript, $"dragsource{(dc.Show ? "s" : "")}") + "\"";
                            }
                            catch (InvalidOperationException ex) { return () => CheckNat(title, false, ex.Message); }
                            return () =>
                            {
                                string outSource = Path.Combine(workDir, "dragsource.out"), outTarget = Path.Combine(workDir, "dragtarget.out");
                                string script = Path.Combine(workDir, "drag.sh");
                                File.WriteAllText(script, DragScript($"\"{dragTarget.Value}\"", sourceCommand, driver, outSource, outTarget));
                                // (the hand presses and moves on a clock: no compiler of this run at the same time - TestJobs.Alone)
                                int runExit = 0;
                                string runLog = TestJobs.Alone(() => RunProc(xvfbRun, $"{TestJobs.XvfbServer()} sh \"{script}\"", workDir, out runExit, null,
                                    new Dictionary<string, string?> { ["FIRE_DISPLAY"] = "x11" }));
                                string actual = DragLines(outSource) + DragLines(outTarget);
                                string want = string.Concat(dragExpected.Select(l => l + "\n"));
                                CheckNat(title, runExit == 0 && actual == want, $"  erwartet:\n{want}\n  erhalten (Exitcode {runExit}):\n{actual}{runLog}");
                            };
                        });
                    }
                    TestJobs.Ordered(dragSteps.Count, i => dragSteps[i](), (_, run) => run());
                });

                // ---- drag-out on Wayland (wl_data_device.start_drag, fire_dragout_wayland): weston with its X11 backend draws into Xvfb; source and target are native fire
                // programs with fire's own display on Wayland (fire_display_wayland.h). Wayland clients cannot know where their windows are: the source is magenta,
                // the target green, and the hand finds them on the X screen (XGetImage) - it presses in the source and lets go on the point of the target farthest
                // from it, twice (text, then files); a third program drags onto the black background (no target: 0).
                windowRuns.Add(() =>
                {
                    const string handSource = """
                        #include <X11/Xlib.h>
                        #include <X11/Xutil.h>
                        #include <dlfcn.h>
                        #include <stdio.h>
                        #include <stdlib.h>
                        #include <time.h>
                        typedef int (*FakeButton)(Display*, unsigned int, int, unsigned long);
                        typedef int (*FakeMotion)(Display*, int, int, int, unsigned long);
                        static void pause_ms(int ms) { struct timespec t; t.tv_sec = ms / 1000; t.tv_nsec = (long)(ms % 1000) * 1000000L; nanosleep(&t, NULL); }
                        typedef struct Box { int x0, y0, x1, y1, n; } Box;
                        static void scan(Display* d, unsigned long src, unsigned long dst, Box* s, Box* t, int* tx, int* ty) {
                            XWindowAttributes a;
                            XImage* img;
                            int x, y;
                            long best = -1;
                            XGetWindowAttributes(d, DefaultRootWindow(d), &a);
                            img = XGetImage(d, DefaultRootWindow(d), 0, 0, (unsigned)a.width, (unsigned)a.height, AllPlanes, ZPixmap);
                            s->n = t->n = 0;
                            if (img == NULL) return;
                            for (y = 0; y < a.height; y++)
                                for (x = 0; x < a.width; x++) {
                                    unsigned long p = XGetPixel(img, x, y) & 0xFFFFFF;
                                    Box* b = p == src ? s : p == dst ? t : NULL;
                                    if (b == NULL) continue;
                                    if (b->n == 0) { b->x0 = b->x1 = x; b->y0 = b->y1 = y; }
                                    if (x < b->x0) b->x0 = x;
                                    if (x > b->x1) b->x1 = x;
                                    if (y < b->y0) b->y0 = y;
                                    if (y > b->y1) b->y1 = y;
                                    b->n++;
                                }
                            for (y = t->y0 + 10; t->n && y <= t->y1 - 10; y += 8)
                                for (x = t->x0 + 10; x <= t->x1 - 10; x += 8) {
                                    long dx, dy, dist;
                                    if ((XGetPixel(img, x, y) & 0xFFFFFF) != dst) continue;
                                    dx = x - (s->x0 + s->x1) / 2; dy = y - (s->y0 + s->y1) / 2;
                                    dist = dx * dx + dy * dy;
                                    if (dist > best) { best = dist; *tx = x; *ty = y; }
                                }
                            XDestroyImage(img);
                        }
                        int main(int argc, char** argv) {
                            Display* d = XOpenDisplay(NULL);
                            void* xtst = dlopen("libXtst.so.6", RTLD_NOW);
                            FakeButton button;
                            FakeMotion motion;
                            unsigned long src, dst;
                            int times = argc > 3 ? atoi(argv[3]) : 1, k, i, tx = 0, ty = 0;
                            Box s, t;
                            if (d == NULL || xtst == NULL || argc < 3) { fprintf(stderr, "hand: no display or no libXtst\n"); return 2; }
                            src = strtoul(argv[1], NULL, 16); dst = strtoul(argv[2], NULL, 16);
                            button = (FakeButton)dlsym(xtst, "XTestFakeButtonEvent");
                            motion = (FakeMotion)dlsym(xtst, "XTestFakeMotionEvent");
                            if (!button || !motion) { fprintf(stderr, "hand: XTest incomplete\n"); return 2; }
                            for (k = 0; k < 300; k++) {
                                scan(d, src, dst, &s, &t, &tx, &ty);
                                if (s.n > 100 && t.n > 1000) break;
                                pause_ms(100);
                            }
                            if (s.n <= 100 || t.n <= 1000) { fprintf(stderr, "hand: windows not found (%d, %d)\n", s.n, t.n); return 2; }
                            pause_ms(700);
                            for (k = 0; k < times; k++) {
                                int sx, sy;
                                scan(d, src, dst, &s, &t, &tx, &ty);
                                sx = (s.x0 + s.x1) / 2; sy = (s.y0 + s.y1) / 2;
                                motion(d, -1, sx, sy, 0); XSync(d, False); pause_ms(300);
                                button(d, 1, True, 0); XSync(d, False); pause_ms(400);
                                for (i = 1; i <= 12; i++) { motion(d, -1, sx + (tx - sx) * i / 12, sy + (ty - sy) * i / 12, 0); XSync(d, False); pause_ms(40); }
                                pause_ms(300);
                                button(d, 1, False, 0); XSync(d, False);
                                pause_ms(1500);
                            }
                            XCloseDisplay(d);
                            return 0;
                        }
                        """;
                    string title = "Fenster: Drag aus dem Fenster in ein anderes Programm (Wayland: weston unter Xvfb, wl_data_device): Text und Dateien, die Antwort Kopie; ins Leere 0";
                    string? weston = FindProgram("weston");
                    if (!TestSelected(title) || !TestResume.Pending(title, NatUnitKey(title))) { }
                    else if (xvfbRun == null || weston == null) Console.WriteLine($"(uebersprungen - xvfb-run oder weston fehlen: {title})");
                    else
                    {
                        // (a unit of FIRE_TESTS_RESUME/FIRE_TESTS_SHARD: TestResume.cs)
                        using var resumeUnit7 = TestResume.Unit(title, NatUnitKey(title));
                        string handC = Path.Combine(workDir, "wlhand.c"), hand = Path.Combine(workDir, "wlhand.bin");
                        File.WriteAllText(handC, handSource);
                        RunProc(fire.Native.ToolchainDef.CCompilerFor(cxx), $"-O1 \"{handC}\" -o \"{hand}\" -lX11 -ldl", workDir, out int handExit);
                        if (handExit != 0) Console.WriteLine($"(uebersprungen - die Hand (X11-Programm) laesst sich nicht bauen: {title})");
                        else
                        {
                            string Colored(string script, int w, int h, string color) => script.Replace("var win = new Window(", $"var paint = new Renderer(fb)\nfor (var py = 0; py < {h}; py++) {{ for (var px = 0; px < {w}; px++) {{ paint.SetPixel(px, py, {color}) }} }}\nvar win = new Window(");
                            string Build(string script, string name)
                            {
                                var target = TargetProfile.Host;
                                var sources = fire.Native.CppGenerator.GenerateSources(new Linker().CompileAndLink(new[] { script }, null, null, VmExecutionMode.Release, null, target), target);
                                string exe = Path.Combine(workDir, name + ".bin");
                                string buildLog = NativeTestBuild.Build(NativeTestBuild.In(workDir), cxx, sources, workDir, name, $"-pthread {NativeTestBuild.Opt} -Wall -Wextra {sanitize}", exe, out int buildExit, " -ldl -lpthread");
                                if (buildExit != 0 || buildLog.Contains("warning:")) throw new InvalidOperationException("C/C++-Compiler: " + buildLog);
                                return exe;
                            }
                            try
                            {
                                // (both at once)
                                var wlBuilt = new string[2];
                                TestJobs.Ordered(2, i => i == 0 ? Build(Colored(DragSourceScript("/tmp/ein Ordner/datei ä.txt", "/tmp/zwei", false), 64, 48, "0xFFFF00FF"), "wldragsource")
                                    : Build(Colored(dragTargetScript, 400, 300, "0xFF00FF00"), "wldragtarget"), (i, exe) => wlBuilt[i] = exe);
                                string source = wlBuilt[0], target = wlBuilt[1];
                                string run = Path.Combine(workDir, "wldrag.sh"), runtime = Path.Combine(workDir, "wlrun");
                                string outSource = Path.Combine(workDir, "wlsource.out"), outTarget = Path.Combine(workDir, "wltarget.out"), outEmpty = Path.Combine(workDir, "wlempty.out");
                                string socket = $"wl-fire-{Environment.ProcessId}";
                                // (weston first - its socket says it is there -, then target, source and hand as for X11; then a source alone over the black background)
                                File.WriteAllText(run, $"""
                                    export XDG_RUNTIME_DIR="{runtime}"
                                    mkdir -p "$XDG_RUNTIME_DIR"; chmod 700 "$XDG_RUNTIME_DIR"
                                    printf '[core]\nidle-time=0\n[shell]\npanel-position=none\nbackground-color=0xff000000\n' > "{runtime}/weston.ini"
                                    "{weston}" --backend=x11 --renderer=pixman --width=640 --height=480 --socket={socket} --config="{runtime}/weston.ini" > "{runtime}/weston.log" 2>&1 &
                                    wp=$!
                                    for i in $(seq 1 100); do [ -S "$XDG_RUNTIME_DIR/{socket}" ] && break; sleep 0.1; done
                                    export WAYLAND_DISPLAY={socket} FIRE_DISPLAY=wayland
                                    "{target}" > "{outTarget}" 2>&1 &
                                    t=$!
                                    sleep 0.5
                                    "{source}" > "{outSource}" 2>&1 &
                                    s=$!
                                    "{hand}" FF00FF 00FF00 2
                                    d=$?
                                    wait $s; se=$?
                                    wait $t; te=$?
                                    sleep 0.5
                                    "{source}" > "{outEmpty}" 2>&1 &
                                    s=$!
                                    "{hand}" FF00FF 000000 2
                                    d2=$?
                                    wait $s; se2=$?
                                    kill $wp; wait $wp
                                    exit $(( d + se + te + d2 + se2 ))
                                    """.Replace("\r", ""));
                                string runLog = RunProc(xvfbRun, $"{TestJobs.XvfbServer()} -s \"-screen 0 1024x768x24\" sh \"{run}\"", workDir, out int runExit);
                                string actual = DragLines(outSource) + DragLines(outTarget) + DragLines(outEmpty);
                                string want = string.Concat(new[] { "text 1", "files 1", "dropped text [Text aus fire ä]", "dropped files 2 [/tmp/ein Ordner/datei ä.txt] [/tmp/zwei]", "text 0", "files 0" }.Select(l => l + "\n"));
                                CheckNat(title, runExit == 0 && actual == want, $"  erwartet:\n{want}\n  erhalten (Exitcode {runExit}):\n{actual}{runLog}"
                                    + (File.Exists(outSource) ? File.ReadAllText(outSource) : "") + (File.Exists(outTarget) ? File.ReadAllText(outTarget) : ""));
                            }
                            catch (InvalidOperationException ex) { CheckNat(title, false, ex.Message); }
                        }
                        resumeUnit7.Done();
                    }
                });

                // the cases under Wine (one prefix: TestJobs.Wine) at the same time as the native window cases above
                Action wineRuns = () =>
                {
                    lock (TestJobs.Wine)
                    {
                        // ---- Windows with fire's own display (fire_display_win32.h): MinGW, only user32/gdi32 linked, run under Wine - a real window (keys, mouse, text and the close
                        // of the tests' events, the frame shown), the clipboard (text, picture as PNG/CF_DIB, files as CF_HDROP) and the drops of the tests without a screen
                        {
                            string title = "Fenster: Windows (MinGW, eigene Anzeige, unter Wine): Fenster, Zwischenablage mit Text, Bild und Dateien, Drop-Ereignisse";
                            string? mingw = FindProgram("x86_64-w64-mingw32-gcc");
                            string? wine = new[] { "/usr/lib/wine/wine64", "/usr/bin/wine64", "/usr/bin/wine" }.FirstOrDefault(File.Exists) ?? FindProgram("wine64") ?? FindProgram("wine");
                            if (!TestSelected(title) || !TestResume.Pending(title, NatUnitKey(title))) { }
                            else if (mingw == null || wine == null || xvfbRun == null) Console.WriteLine("(Fenster unter Wine, eigene Anzeige: uebersprungen - MinGW, Wine oder xvfb-run fehlen)");
                            else
                            {
                                // (a unit of FIRE_TESTS_RESUME/FIRE_TESTS_SHARD: TestResume.cs)
                                using var resumeUnit8 = TestResume.Unit(title, NatUnitKey(title));
                                var winTarget = TargetProfile.Windows;
                                string winDir = Path.Combine(workDir, "wine_fire");
                                Directory.CreateDirectory(winDir);
                                fire.Native.NativeRuntimeFiles.WriteTo(winDir, "windows");
                                string RunWine(string tool, string arguments, out int exit)
                                {
                                    var info = new System.Diagnostics.ProcessStartInfo(tool, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = winDir };
                                    info.Environment["WINEPREFIX"] = Path.Combine(workDir, "wineprefix");
                                    info.Environment["WINEDEBUG"] = "-all";
                                    info.Environment["LANG"] = "C.UTF-8";
                                    using var p = System.Diagnostics.Process.Start(info)!;
                                    var errTask = p.StandardError.ReadToEndAsync();
                                    string output = p.StandardOutput.ReadToEnd();
                                    if (!p.WaitForExit(300000)) { try { p.Kill(true); } catch { } exit = -1; return output + "(Zeitueberschreitung)"; }
                                    exit = p.ExitCode;
                                    return output + errTask.Result;
                                }
                                RunWine(wine, "wineboot --init", out _);
                                const string realWindow = """
                                #import "windows"
                                var fb = new Framebuffer(320, 200)
                                var r = new Renderer(fb)
                                r.Clear()
                                var win = new Window(fb, "fire unter Wine")
                                win.RegisterKeyDown(func(int k, int s, int m, bool rep) => { print("key " + k + " " + s + " " + m) })
                                win.RegisterMouseDown(func(int b, float x, float y) => { print("down " + b + " " + x + " " + y) })
                                win.RegisterTextInput(func(string t) => { print("text " + t) })
                                win.RegisterCloseRequest(func() => { print("close request") })
                                print(win.Tick())
                                """;
                                var winCases = new (string Name, string Script, string Define, string Display, string[] Expected)[]
                                {
                                    ("window", realWindow, "-DFIRE_DISPLAY_SELFTEST ", "win32", new[] { "key 97 4 1", "down 1 32 24", "text éx", "close request", "False" }),
                                    ("drop", windowCases.First(c => c.Define.Contains("SELFTEST_DROP") && c.Title.StartsWith("Fenster: Drop", StringComparison.Ordinal)).Script, "-DFIRE_DISPLAY_SELFTEST_DROP ", "headless",
                                        windowCases.First(c => c.Define.Contains("SELFTEST_DROP") && c.Title.StartsWith("Fenster: Drop", StringComparison.Ordinal)).Expected),
                                    ("clip", clipScript.Replace("/tmp/ein Ordner/datei ä.txt", "C:\\\\fire\\\\ein Ordner\\\\datei ä.txt").Replace("/tmp/zwei", "C:\\\\zwei"), "", "win32",
                                        clipExpected.Select(l => l.Replace("/tmp/ein Ordner/datei ä.txt", "C:\\fire\\ein Ordner\\datei ä.txt").Replace("/tmp/zwei", "C:\\zwei")).ToArray()),
                                    // DirectComposition (part 4.4) where it is there; Wine's is not: the window is made anew without it and shows its pixels (GDI)
                                    ("path", """
                                        #import "windows"
                                        var fb = new Framebuffer(160, 100)
                                        var r = new Renderer(fb)
                                        r.FillRect(0, 0, 160, 100, new SolidBrush(0xFF0000FF))
                                        var win = new Window(fb, "path")
                                        win.Tick()
                                        win.Tick()
                                        print(win.PresentPath())
                                        """, "", "win32", new[] { "pixels" }),
                                };
                                foreach (var wcase in winCases)
                                {
                                    var generated = fire.Native.CppGenerator.GenerateSources(new Linker().CompileAndLink(new[] { wcase.Script }, null, null, VmExecutionMode.Release, null, winTarget), winTarget);
                                    string exeFile = Path.Combine(winDir, wcase.Name + ".exe");
                                    string links = string.Concat(generated.Program.Split('\n').Where(l => l.StartsWith("// fire-link: ", StringComparison.Ordinal)).Select(l => " -l" + l.Substring("// fire-link: ".Length).Trim()));
                                    string build = NativeTestBuild.Build((tool, arguments) => (RunWine(tool, arguments, out int code), code), mingw.Replace("-gcc", "-g++"), generated, winDir, wcase.Name,
                                        $"{NativeTestBuild.Opt} -Wall -Wextra {wcase.Define}", exeFile, out int buildExit, links);
                                    if (buildExit != 0 || build.Contains("warning:") || !links.Contains("-luser32")) { CheckNat($"{title}: {wcase.Name}", false, "MinGW: " + links + "\n" + build); continue; }
                                    string actual = RunWine(xvfbRun, $"{TestJobs.XvfbServer()} env FIRE_DISPLAY={wcase.Display} \"{wine}\" \"{exeFile}\"", out int runExit);
                                    // (Wine's own goodbye when xvfb-run takes the X server away is not the program's)
                                    actual = string.Concat(actual.Replace("\r", "").Split('\n').Where(l => !l.StartsWith("X connection to ", StringComparison.Ordinal)).Select(l => l + "\n"));
                                    string want = string.Concat(wcase.Expected.Select(l => l + "\n"));
                                    CheckNat($"{title}: {wcase.Name}", runExit == 0 && actual.TrimEnd() == want.TrimEnd(), $"  erwartet:\n{want}\n  erhalten (Wine, Exitcode {runExit}):\n{actual}");
                                }
                                // DirectComposition in C (packages/windows/native/fire_display_dcomp.h): the places of the COM methods, the GUIDs and the structures as in the SDK of MinGW
                                {
                                    string check = Path.Combine(winDir, "check_dcomp.exe");
                                    // (the source is in the repository: the first folder above the program that has it)
                                    string? cpp = null;
                                    for (var dirInfo = new DirectoryInfo(AppContext.BaseDirectory); dirInfo != null && cpp == null; dirInfo = dirInfo.Parent)
                                        if (File.Exists(Path.Combine(dirInfo.FullName, "packages", "windows", "native", "tools", "check_dcomp.cpp"))) cpp = Path.Combine(dirInfo.FullName, "packages", "windows", "native", "tools", "check_dcomp.cpp");
                                    if (cpp == null) Console.WriteLine("(DirectComposition gegen das SDK: uebersprungen - tools/check_dcomp.cpp fehlt)");
                                    else
                                    {
                                        string built = RunWine(mingw.Replace("-gcc", "-g++"), $"-O1 -static -Wno-unused-function -o \"{check}\" \"{cpp}\"", out int checkBuild);
                                        string ran = checkBuild == 0 ? RunWine(wine, $"\"{check}\"", out _).Replace("\r", "") : built;
                                        CheckNat("Fenster: DirectComposition (Windows) - Plaetze der COM-Methoden, GUIDs und Strukturen wie im SDK (MinGW, unter Wine)", ran.Contains("dcomp ok"), ran);
                                    }
                                }
                                // Window.PresentArea under Windows: only the reported area reaches the window (the band of its rows, SetDIBitsToDevice), idle frames overwrite
                                // nothing - a picture of the X server Wine draws on (ImageMagick's import)
                                if (FindProgram("import") == null) Console.WriteLine("(PresentArea unter Wine: uebersprungen - ImageMagick import fehlt)");
                                else try
                                {
                                    const string areaScript = """
                                    #import "windows"
                                    var fb = new Framebuffer(200, 100)
                                    var r = new Renderer(fb)
                                    var win = new Window(fb, "area")
                                    win.VSync = false
                                    r.FillRect(0, 0, 200, 100, new SolidBrush(0xFF0000FF))
                                    win.Tick()
                                    win.Tick()
                                    r.FillRect(20, 20, 30, 30, new SolidBrush(0xFFFF0000))
                                    r.FillRect(120, 20, 30, 30, new SolidBrush(0xFF00FF00))
                                    win.PresentArea(20, 20, 30, 30)
                                    win.Tick()
                                    print("ready")
                                    win.VSync = true
                                    for (var i = 0; i < 180; i = i + 1) {
                                        win.PresentArea(0, 0, 0, 0)
                                        win.Tick()
                                    }
                                    """;
                                    var generated = fire.Native.CppGenerator.GenerateSources(new Linker().CompileAndLink(new[] { areaScript }, null, null, VmExecutionMode.Release, null, winTarget), winTarget);
                                    string exeFile = Path.Combine(winDir, "area.exe");
                                    string links = string.Concat(generated.Program.Split('\n').Where(l => l.StartsWith("// fire-link: ", StringComparison.Ordinal)).Select(l => " -l" + l.Substring("// fire-link: ".Length).Trim()));
                                    string build = NativeTestBuild.Build((tool, arguments) => (RunWine(tool, arguments, out int code), code), mingw.Replace("-gcc", "-g++"), generated, winDir, "area",
                                        $"{NativeTestBuild.Opt} -Wall -Wextra ", exeFile, out int buildExit, links);
                                    if (buildExit != 0 || build.Contains("warning:")) CheckNat($"{title}: PresentArea", false, "MinGW: " + links + "\n" + build);
                                    else
                                    {
                                        string outFile = Path.Combine(winDir, "area.txt"), screen = Path.Combine(winDir, "area.ppm"), script = Path.Combine(winDir, "area.sh");
                                        File.WriteAllText(script, $"FIRE_DISPLAY=win32 \"{wine}\" \"{exeFile}\" > \"{outFile}\" 2>&1 &\n" +
                                            $"for i in $(seq 1 600); do grep -q ready \"{outFile}\" 2>/dev/null && break; sleep 0.1; done\nsleep 1\n" +
                                            $"import -window root -depth 8 \"{screen}\"\nwait\n");
                                        RunWine(xvfbRun, $"{TestJobs.XvfbServer()} sh \"{script}\"", out _);
                                        string got = File.Exists(screen) ? ScreenPictures.Area(File.ReadAllBytes(screen)) : "kein Bild";
                                        CheckNat($"{title}: PresentArea - nur der gemeldete Bereich kommt im Fenster an, leere Bereiche im Leerlauf ueberschreiben nichts",
                                            got == "innen (0, 0, 255) aussen (255, 0, 0)", got + "\n" + (File.Exists(outFile) ? File.ReadAllText(outFile) : ""));
                                    }
                                }
                                catch (Exception ex) { CheckNat($"{title}: PresentArea", false, ex.ToString()); }
                                resumeUnit8.Done();
                            }
                        }

                        // ---- drag-out under Wine (OLE: DoDragDrop with our IDataObject and IDropSource): the same as on X11 - source and target built with MinGW, fire's own
                        // display (fire_display_win32.h: the target is its OLE drop target), the hand the X program above on the X server that Wine draws on
                        {
                            string? mingw = FindProgram("x86_64-w64-mingw32-gcc");
                            string? wine = new[] { "/usr/lib/wine/wine64", "/usr/bin/wine64", "/usr/bin/wine" }.FirstOrDefault(File.Exists) ?? FindProgram("wine64") ?? FindProgram("wine");
                            string title = "Fenster: Drag aus dem Fenster in ein anderes Programm (Windows unter Wine, OLE): Text (CF_UNICODETEXT) und Dateien (CF_HDROP)";
                            string? driver = null;
                            if (!TestSelected(title) || !TestResume.Pending(title, NatUnitKey(title))) { }
                            else if (mingw == null || wine == null || xvfbRun == null) Console.WriteLine($"(uebersprungen - MinGW, Wine oder xvfb-run fehlen: {title})");
                            else if ((driver = DragDriver()) == null) Console.WriteLine($"(uebersprungen - die Hand (X11-Programm) laesst sich nicht bauen: {title})");
                            else
                            {
                                // (a unit of FIRE_TESTS_RESUME/FIRE_TESTS_SHARD: TestResume.cs)
                                using var resumeUnit9 = TestResume.Unit(title, NatUnitKey(title));
                                string winDir = Path.Combine(workDir, "wine_drag");
                                Directory.CreateDirectory(winDir);
                                fire.Native.NativeRuntimeFiles.WriteTo(winDir, "windows");
                                string RunWine(string tool, string arguments, out int exit)
                                {
                                    var info = new System.Diagnostics.ProcessStartInfo(tool, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = winDir };
                                    info.Environment["WINEPREFIX"] = Path.Combine(workDir, "wineprefix");
                                    info.Environment["WINEDEBUG"] = "-all";
                                    info.Environment["LANG"] = "C.UTF-8";
                                    info.Environment["FIRE_DISPLAY"] = "win32";
                                    using var p = System.Diagnostics.Process.Start(info)!;
                                    var errTask = p.StandardError.ReadToEndAsync();
                                    string output = p.StandardOutput.ReadToEnd();
                                    if (!p.WaitForExit(300000)) { try { p.Kill(true); } catch { } exit = -1; return output + "(Zeitueberschreitung)"; }
                                    exit = p.ExitCode;
                                    return output + errTask.Result;
                                }
                                string BuildWin(string script, string name, out string error)
                                {
                                    var winTarget = TargetProfile.Windows;
                                    var generated = fire.Native.CppGenerator.GenerateSources(new Linker().CompileAndLink(new[] { script }, null, null, VmExecutionMode.Release, null, winTarget), winTarget);
                                    string exeFile = Path.Combine(winDir, name + ".exe");
                                    string links = string.Concat(generated.Program.Split('\n').Where(l => l.StartsWith("// fire-link: ", StringComparison.Ordinal)).Select(l => " -l" + l.Substring("// fire-link: ".Length).Trim()).Distinct());
                                    string build = NativeTestBuild.Build((tool, arguments) => (RunWine(tool, arguments, out int code), code), mingw.Replace("-gcc", "-g++"), generated, winDir, name,
                                        $"{NativeTestBuild.Opt} -Wall -Wextra", exeFile, out int buildExit, links);
                                    error = buildExit != 0 || build.Contains("warning:") || !links.Contains("-luser32") ? "MinGW: " + links + "\n" + build : "";
                                    return exeFile;
                                }
                                RunWine(wine, "wineboot --init", out _);
                                string targetExe = BuildWin(dragTargetScript, "dragtarget", out string targetError);
                                string sourceExe = BuildWin(DragSourceScript("C:\\\\fire\\\\ein Ordner\\\\datei ä.txt", "C:\\\\zwei", false), "dragsource", out string sourceError);
                                if (targetError != "" || sourceError != "") CheckNat(title, false, targetError + sourceError);
                                else
                                {
                                    string outSource = Path.Combine(winDir, "dragsource.out"), outTarget = Path.Combine(winDir, "dragtarget.out");
                                    string script = Path.Combine(winDir, "drag.sh");
                                    // (Wine's windows need a moment more: the source starts 2 s after the target)
                                    File.WriteAllText(script, DragScript($"\"{wine}\" \"{targetExe}\"", $"\"{wine}\" \"{sourceExe}\"", driver, outSource, outTarget, "2"));
                                    // (the hand presses and moves on a clock: no compiler of this run at the same time - TestJobs.Alone)
                                    int runExit = 0;
                                    string runLog = TestJobs.Alone(() => RunWine(xvfbRun, $"{TestJobs.XvfbServer()} sh \"{script}\"", out runExit));
                                    string actual = DragLines(outSource) + DragLines(outTarget);
                                    string want = string.Concat(new[] { "text 1", "files 1", "dropped text [Text aus fire ä]", "dropped files 2 [C:\\fire\\ein Ordner\\datei ä.txt] [C:\\zwei]" }.Select(l => l + "\n"));
                                    CheckNat(title, runExit == 0 && actual == want, $"  erwartet:\n{want}\n  erhalten (Wine, Exitcode {runExit}):\n{actual}{runLog}");
                                }
                                resumeUnit9.Done();
                            }
                        }
                    }
                };
                // (at the same time: the window cases; the VM on X11, PresentArea and the drag cases one after the other - the VM (forge run) installs the
                // standard packages on its first start, the drag cases share their files -; Wayland; the cases under Wine)
                if (windowRuns.Count != 6) throw new InvalidOperationException("the window runs changed: " + windowRuns.Count);
                TestJobs.Concurrently(windowRuns[0], () => { for (int i = 1; i < 5; i++) windowRuns[i](); }, windowRuns[5], wineRuns);
            }

            // ---- UI library: the same elements natively (fire's own display without a screen: FIRE_DISPLAY=headless) against the VM with the dummy
            {
                if (uiDrawExpected.Length == 0 || !TestSelected("UI nativ == VM") || !TestResume.Pending("UI nativ == VM", NatUnitKey("UI nativ == VM"))) Console.WriteLine("(UI nativ == VM: uebersprungen - die UI-Pruefungen der VM liefen nicht, oder der Filter schliesst es aus)");
                else
                {
                    // (a unit of FIRE_TESTS_RESUME/FIRE_TESTS_SHARD: TestResume.cs)
                    using var resumeUnit10 = TestResume.Unit("UI nativ == VM", NatUnitKey("UI nativ == VM"));
                    var generated = fire.Native.CppGenerator.GenerateSources(new Linker().CompileAndLink(new[] { "#import \"ui\"\n" + uiDrawScript }, null, null, VmExecutionMode.Release));
                    string exe = Path.Combine(workDir, "uidraw.bin");
                    string build = NativeTestBuild.Build(NativeTestBuild.In(workDir), cxx, generated, workDir, "uidraw", $"-pthread {NativeTestBuild.Opt} -Wall -Wextra {sanitize}", exe, out int buildExit, " -ldl -lpthread");
                    if (buildExit != 0 || build.Contains("warning:")) CheckNat("UI nativ == VM", false, "C/C++-Compiler: " + build);
                    else
                    {
                        string actual = RunProc(exe, "", workDir, out int runExit, null, new Dictionary<string, string?> { ["FIRE_DISPLAY"] = "headless" });
                        string expected = string.Concat(uiDrawExpected.Select(l => l + "\n"));
                        CheckNat("UI nativ == VM: Label, Button, CheckBox, TextBox, Stack zeichnen", runExit == 0 && actual == expected && uiDrawExpected.Length == 5, $"  erwartet (VM):\n{expected}\n  erhalten:\n{actual}");
                    }
                    resumeUnit10.Done();
                }
            }
        }, () =>
        {
            // ---- Platform layer: the same thread programs on FreeRTOS (tasks, semaphores) - here on the simulator (native/sim, pthreads)
            {
                string[] rtosCases = { "Actor: fire with", "Actor: mehrere", "sync: die Kopie", "sync: Arrays", "taking: der Thread", "terminate im Hauptprogramm", "catch threads()",
                    "Globals: sync global ist atomar", "Globals: fire global mit taking", "Thread startet Thread", "Globals: #nosync haelt", "leave aus einer Funktion" };
                var rtosIndexes = Enumerable.Range(0, natCases.Length).Where(i => natCases[i].Name.StartsWith("Threads:") && rtosCases.Any(c => natCases[i].Name.Contains(c))).ToArray();
                // (every case a unit of FIRE_TESTS_RESUME/FIRE_TESTS_SHARD, TestResume.cs: keyed by its source and what the VM printed)
                string RtosKey(int index) => TestResume.Active ? TestResume.Key(natCases[index].Source, vmResults[index], sanitize) : "";
                var rtosTasks = TestJobs.Map(rtosIndexes.Where(i => TestResume.Pending("FreeRTOS == VM: " + natCases[i].Name, RtosKey(i))).ToList(), (index, _) =>
                {
                    string expected = vmResults[index];
                    var generated = fire.Native.CppGenerator.GenerateSources(new Linker().CompileAndLink(new[] { natCases[index].Source }, null, null, VmExecutionMode.Release, null, TargetProfile.FreeRtos), TargetProfile.FreeRtos);
                    string exe = Path.Combine(workDir, $"rtos{index}.bin");
                    // the board's startup code would call the entry point from a task; here main does
                    generated = NativeTestBuild.Append(generated, "\nint main(void) { fire_start(); return 0; }\n");
                    string build = NativeTestBuild.Build(NativeTestBuild.In(workDir), cxx, generated, workDir, $"rtos{index}", $"-pthread {NativeTestBuild.Opt} -Wall -Wextra {sanitize}-I\"{Path.Combine(workDir, "sim")}\"", exe, out int buildExit);
                    if (buildExit != 0 || build.Contains("warning:")) return (index, expected, "C/C++-Compiler: " + build);
                    string actual = RunProc(exe, "", workDir, out _);   // (a FreeRTOS program has no exit code)
                    if (actual == expected) NativeTestBuild.Clean(workDir, $"rtos{index}", exe);
                    return (index, expected, actual);
                });
                foreach (var task in rtosTasks)
                {
                    var (index, expected, actual) = task;
                    string name = natCases[index].Name;
                    TestResume.Run("FreeRTOS == VM: " + name, RtosKey(index), () => CheckNat($"FreeRTOS == VM: {name}", expected == actual, $"  erwartet (VM):\n{expected}\n  erhalten (FreeRTOS):\n{actual}"));
                }
                if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FIRE_TESTS_FILTER"))) CheckNat("FreeRTOS: es gibt Faelle", rtosIndexes.Length >= 10, rtosIndexes.Length.ToString());
            }
            // the scheduler of fire itself (ROADMAP 13.2b, std/fire_sched.h: green threads for targets without threads) - tried on this machine: every thread case
            // built with FIRE_GREEN_THREADS (ucontext instead of pthreads) gives what the VM gives
            if (!OperatingSystem.IsWindows())
            {
                var greenIndexes = Enumerable.Range(0, natCases.Length).Where(i => natCases[i].Name.StartsWith("Threads:") && TestSelected("Gruene Threads " + natCases[i].Name)).ToArray();
                string GreenKey(int index) => TestResume.Active ? TestResume.Key(natCases[index].Source, vmResults[index]) : "";
                var greenTasks = TestJobs.Map(greenIndexes.Where(i => TestResume.Pending("Gruene Threads (eigener Scheduler) == VM: " + natCases[i].Name, GreenKey(i))).ToList(), (index, _) =>
                {
                    string expected = vmResults[index];
                    var generated = fire.Native.CppGenerator.GenerateSources(new Linker().CompileAndLink(new[] { natCases[index].Source }, null, null, VmExecutionMode.Release));
                    string exe = Path.Combine(workDir, $"green{index}.bin");
                    string build = NativeTestBuild.Build(NativeTestBuild.In(workDir), cxx, generated, workDir, $"green{index}", $"-pthread -DFIRE_GREEN_THREADS {NativeTestBuild.Opt} -Wall -Wextra", exe, out int buildExit);
                    if (buildExit != 0 || build.Contains("warning:")) return (index, expected, "C-Compiler: " + build);
                    string actual = RunProc(exe, "", workDir, out _);
                    if (actual == expected) NativeTestBuild.Clean(workDir, $"green{index}", exe);
                    return (index, expected, actual);
                });
                foreach (var (index, expected, actual) in greenTasks)
                {
                    string name = natCases[index].Name;
                    TestResume.Run("Gruene Threads (eigener Scheduler) == VM: " + name, GreenKey(index), () => CheckNat($"Gruene Threads (eigener Scheduler) == VM: {name}", expected == actual, $"  erwartet (VM):\n{expected}\n  erhalten (eigener Scheduler):\n{actual}"));
                }
                if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FIRE_TESTS_FILTER"))) CheckNat("Gruene Threads: es gibt Faelle", greenIndexes.Length >= 30, greenIndexes.Length.ToString());
            }
        });

        // ---- Target configuration (fire.native.json) and `build` with the native engine
        {
            var config = fire.Native.NativeConfig.Parse("""
                {
                  // a comment, and a trailing comma
                  "engine": "native", "target": "board",
                  "targets": {
                    "board": { "extends": "freertos", "includes": ["my_rtos.h"], "defines": ["BOARD_X=3", "FIRE_THREAD_PRIORITY=5"], "stackBytes": 6000,
                               "entry": { "name": "board_main", "externC": false }, "toolchain": "mine" },
                    "pc": { "platform": "posix", "compileArgs": ["-pthread"], },
                  },
                  "toolchains": { "mine": { "extends": "gcc", "optimization": "-O1", "args": ["-Wall"] } }
                }
                """);
            var board = config.ResolveTarget();
            CheckNat("Konfiguration: Ziel erbt vom eingebauten und ueberschreibt", board.Name == "board" && board.Native.Platform == "freertos" && board.FloatWidth == 32
                && board.DefaultStackBytes == 6000 && board.Native.Includes.SequenceEqual(new[] { "my_rtos.h" }) && board.Native.Entry.Name == "board_main"
                && !board.Native.Entry.ExternC && board.Native.Entry.Kind == fire.Runtime.EntryKind.Function && board.IsEmbedded);
            var tc = config.ResolveToolchain(board);
            CheckNat("Konfiguration: Toolchain erbt vom eingebauten", tc.EffectiveKind == "gcc" && tc.Optimization == "-O1" && tc.EffectiveCompiler == "g++" && tc.Std == "c++17" && tc.Args!.SequenceEqual(new[] { "-Wall" }));
            bool unknownTarget = false;
            try { config.ResolveTarget("amiga"); } catch (fire.Native.NativeConfigException) { unknownTarget = true; }
            CheckNat("Konfiguration: unbekanntes Ziel wird abgelehnt, Standard ist der Rechner", unknownTarget && new fire.Native.NativeConfig().ResolveTarget() == TargetProfile.Host);
            var roundTrip = fire.Native.NativeConfig.Parse(config.ToJson());
            CheckNat("Konfiguration: speichern und wieder lesen", roundTrip.ResolveTarget("board").Native.Defines.SequenceEqual(new[] { "BOARD_X=3", "FIRE_THREAD_PRIORITY=5" }) && roundTrip.Engine == "native");
            string savedJson = new fire.Native.NativeConfig { Toolchains = { ["x"] = new fire.Native.ToolchainDef { Compiler = "g++", Std = "c++20" } } }.ToJson();
            CheckNat("Konfiguration: gespeichert wird nur, was eingestellt ist (keine abgeleiteten Namen), g++ steht lesbar da", savedJson.Contains("\"g++\"") && savedJson.Contains("\"c++20\"")
                && !savedJson.Contains("targetNames") && !savedJson.Contains("toolchainNames") && !savedJson.Contains("effectiveKind") && !savedJson.Contains("compilesCpp"));
            string boardCpp = fire.Native.CppGenerator.Generate(new Linker().CompileAndLink(new[] { "print(1)" }, null, null, VmExecutionMode.Release, null, board), board);
            int incAt = boardCpp.IndexOf("#include <my_rtos.h>", StringComparison.Ordinal), platAt = boardCpp.IndexOf("#define FIRE_PLATFORM_HEADER \"platform/freertos/fire_platform.h\"", StringComparison.Ordinal);
            CheckNat("Konfiguration: Defines, Includes vor der Plattform und Einsprung stehen im C++", boardCpp.Contains("#define BOARD_X 3") && incAt > 0 && platAt > incAt
                && boardCpp.Contains("void board_main(void) {") && !boardCpp.Contains("extern \"C\" void board_main") && boardCpp.IndexOf("#define BOARD_X 3", StringComparison.Ordinal) < incAt);

            // conditional compilation (#if): symbols come from the target, the engine and -D
            {
                string Pp(string src, TargetProfile t, string engine = "vm", params string[] defs)
                {
                    var reg = new DirectiveRegistry();
                    foreach (var sym in ConditionalSymbols.For(t, engine, null, defs)) reg.Symbols.Add(sym);
                    return Preprocessor.Process(src, ".", reg).Source;
                }
                string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
                string pick = "#if windows\nW\n#elif esp32 && native\nE\n#elif linux || macos\nP\n#else\nO\n#endif\n";
                CheckNat("#if: das Ziel waehlt den Zweig", Lines(Pp(pick, TargetProfile.Windows)).SequenceEqual(new[] { "W" }) && Lines(Pp(pick, TargetProfile.Esp32, "native")).SequenceEqual(new[] { "E" })
                    && Lines(Pp(pick, TargetProfile.Esp32, "vm")).SequenceEqual(new[] { "O" }) && Lines(Pp(pick, TargetProfile.Linux)).SequenceEqual(new[] { "P" }) && Lines(Pp(pick, TargetProfile.MacOs)).SequenceEqual(new[] { "P" }));
                string numbered = "a\n#if false\nb\nc\n#endif\nd\n";
                CheckNat("#if: die Zeilennummern bleiben erhalten", Pp(numbered, TargetProfile.Linux).Split('\n').ToList().IndexOf("d") == 5 && Lines(Pp(numbered, TargetProfile.Linux)).SequenceEqual(new[] { "a", "d" }));
                CheckNat("#if: Ausdruecke (!, &&, ||, Klammern, true/false, Gross-/Kleinschreibung)",
                    Lines(Pp("#if !(windows || macos) && LINUX\nyes\n#endif\n#if false || (true && !linux)\nno\n#endif\n#if float32\nf32\n#endif\n", TargetProfile.Linux)).SequenceEqual(new[] { "yes" })
                    && Lines(Pp("#if float32\nf32\n#endif\n", TargetProfile.Esp32)).SequenceEqual(new[] { "f32" }));
                string nested = "#if linux\n1\n#if windows\n2\n#else\n3\n#endif\n#else\n4\n#if broken ((\n5\n#elif also broken\n6\n#endif\n#unknownthing\n#endif\n";
                CheckNat("#if: verschachtelt, ein nicht gewaehlter Zweig wird nicht gelesen", Lines(Pp(nested, TargetProfile.Linux)).SequenceEqual(new[] { "1", "3" }));
                CheckNat("#define, #undef, #ifdef, #ifndef", Lines(Pp("#ifdef X\nA\n#endif\n#define X\n#ifdef X\nB\n#endif\n#ifndef X\nC\n#endif\n#undef X\n#ifndef X\nD\n#endif\n#if Y\nE\n#endif\n", TargetProfile.Linux, "vm", "Y"))
                    .SequenceEqual(new[] { "B", "D", "E" }));
                string ifErr(string src) { try { Pp(src, TargetProfile.Linux); return "no error"; } catch (PreprocessorException ex) { return ex.Message; } }
                CheckNat("#if: Fehler (fehlendes #endif, #else/#endif/#elif ohne #if, zweites #else, #elif nach #else, falscher Ausdruck, #error)",
                    ifErr("#if linux\nx\n").Contains("no '#endif'") && ifErr("#else\n").Contains("without '#if'") && ifErr("#endif\n").Contains("without '#if'") && ifErr("#elif a\n").Contains("without '#if'")
                    && ifErr("#if a\n#else\n#else\n#endif\n").Contains("second '#else'") && ifErr("#if a\n#else\n#elif b\n#endif\n").Contains("after '#else'") && ifErr("#if a ||\n#endif\n").Contains("ends too early")
                    && ifErr("#if (a\n#endif\n").Contains("')' is missing") && ifErr("#if a $ b\n#endif\n").Contains("unexpected character") && ifErr("#if linux\n#error nur Windows\n#endif\n").Contains("#error nur Windows") && ifErr("#if windows\n#error nicht gelesen\n#endif\n") == "no error");
                var gated = "#if esp32\n#import \"graphics\"\n#endif\n#if linux\n#import \"time\"\n#endif\n";
                CheckNat("#if: ein #import in einem nicht gewaehlten Zweig zaehlt nicht (Editor)", ImportedPreludes.FindImportNames(gated, ConditionalSymbols.For(TargetProfile.Linux)).SequenceEqual(new[] { "time" })
                    && ImportedPreludes.FindImportNames(gated, ConditionalSymbols.For(TargetProfile.Esp32)).SequenceEqual(new[] { "graphics" }));
                var greySource = "a\n#if esp32\nb\n#else\nc\n#endif\n#ifdef EXTRA\nd\n#endif\ne";
                var greyLinux = ConditionalSymbols.InactiveLines(greySource, ConditionalSymbols.For(TargetProfile.Linux));
                var greyDefined = ConditionalSymbols.InactiveLines(greySource, ConditionalSymbols.For(TargetProfile.Esp32, "native", null, new[] { "EXTRA" }));
                CheckNat("#if: die Zeilen nicht gewaehlter Zweige werden erkannt (Editor: ausgegraut)",
                    string.Join(",", greyLinux.Select(x => x ? "1" : "0")) == "0,0,1,0,0,0,0,1,0,0" && string.Join(",", greyDefined.Select(x => x ? "1" : "0")) == "0,0,0,0,1,0,0,0,0,0");
                string ifDir = Path.Combine(workDir, "ifbuild");
                Directory.CreateDirectory(ifDir);
                string ifScript = Path.Combine(ifDir, "cond.script");
                File.WriteAllText(ifScript, "#if native\nprint(\"engine native\")\n#elif vm\nprint(\"engine vm\")\n#endif\n#if EXTRA\nprint(\"extra\")\n#endif\n#if windows\nprint(\"windows\")\n#elif posix\nprint(\"posix\")\n#endif\n");
                File.WriteAllText(Path.Combine(ifDir, "fire.native.json"), "{ \"engine\": \"native\", \"target\": \"" + TargetProfile.Host.Name + "\", \"toolchain\": \"" + (cxx.Contains("clang") ? "clang" : "gcc") + "\" }");
                string ifExe = Path.Combine(ifDir, "cond.out");
                int ifCode = CommandLineRunner.Run(new[] { "build", ifScript, "-D", "EXTRA", "-o", ifExe }, new StringWriter(), new StringWriter());
                string ifRan = ifCode == 0 ? RunProc(ifExe, "", ifDir, out _) : "";
                CheckNat("#if: build --engine native setzt native, das Ziel und -D", ifCode == 0 && ifRan == "engine native\nextra\n" + (TargetProfile.Host.Name == "windows" ? "windows" : "posix") + "\n", ifRan);
                var badDefine = CommandLineParser.Parse(new[] { "run", ifScript, "-D", "1x" });
                CheckNat("#if: -D braucht einen Namen", badDefine.Error != null && CommandLineParser.Parse(new[] { "run", ifScript, "-DA", "--define", "B", "-D=C" }).Defines.SequenceEqual(new[] { "A", "B", "C" }));
            }

            // the console through IO.Stdio: the standard input is read (lines end with \n, \r\n or \r), output and errors go to their streams
            {
                string ioDir = Path.Combine(workDir, "iobuild");
                Directory.CreateDirectory(ioDir);
                string ioScript = Path.Combine(ioDir, "console.script");
                File.WriteAllText(ioScript, "#import \"io\"\nprint(\"first\")\nvar a = IO.Stdio.ReadLine()\nIO.Stdio.WriteLine(\"a=\" + a)\nIO.Stdio.ErrorLine(\"to stderr\")\nvar b = IO.Stdio.ReadLine()\nvar rest = IO.Stdio.ReadAll()\nprint(\"b=\" + b + \" rest=\" + rest.length)\nprint(IO.Stdio.ReadLine() == undefined)\nvar so = IO.Stdio.Out()\nvar w = new IO.TextWriter(so, true)\nw.WriteLine(\"via writer\")\nw.Flush()\n");
                File.WriteAllText(Path.Combine(ioDir, "fire.native.json"), "{ \"engine\": \"native\", \"target\": \"" + TargetProfile.Host.Name + "\", \"toolchain\": \"" + (cxx.Contains("clang") ? "clang" : "gcc") + "\" }");
                string ioExe = Path.Combine(ioDir, "console.out");
                int ioCode = CommandLineRunner.Run(new[] { "build", ioScript, "-o", ioExe }, new StringWriter(), new StringWriter());
                string ioRan = ioCode == 0 ? RunProc(ioExe, "", ioDir, out _, "one\r\ntwo\nthree\rfour") : "";
                string[] ioLines = ioRan.Split('\n');
                CheckNat("IO: Standardeingabe, -ausgabe und -fehler", ioCode == 0 && ioLines.Contains("first") && ioLines.Contains("a=one") && ioLines.Contains("to stderr") && ioLines.Contains("b=two rest=10") && ioLines.Contains("True") && ioLines.Contains("via writer"), ioRan);
            }

            // build --engine native through the command line runner: a program, and the files of a project
            string dir = Path.Combine(workDir, "build");
            Directory.CreateDirectory(dir);
            string script = Path.Combine(dir, "hello.script");
            File.WriteAllText(script, "var n = 0\nfire { sync global { n = 41 + 1 } }\nwhile (n == 0) { sync globals }\nprint(\"n \" + n)\n");
            File.WriteAllText(Path.Combine(dir, "fire.native.json"), "{ \"engine\": \"native\", \"target\": \"" + TargetProfile.Host.Name + "\", \"toolchain\": \"" + (cxx.Contains("clang") ? "clang" : "gcc") + "\" }");
            string exeOut = Path.Combine(dir, "hello.out");
            var outW = new StringWriter(); var errW = new StringWriter();
            int code = CommandLineRunner.Run(new[] { "build", script, "-o", exeOut }, outW, errW);
            string ran = code == 0 ? RunProc(exeOut, "", dir, out _) : "";
            CheckNat("build: der native Motor aus fire.native.json baut ein Programm", code == 0 && ran == "n 42\n", $"{code}\n{outW}\n{errW}\n{ran}");
            string projectOut = Path.Combine(dir, "project");
            var out2 = new StringWriter(); var err2 = new StringWriter();
            int code2 = CommandLineRunner.Run(new[] { "build", script, "-t", "esp32", "-o", projectOut }, out2, err2);
            CheckNat("build: Ziel esp32 schreibt die Dateien eines ESP-IDF-Komponenten", code2 == 0 && File.Exists(Path.Combine(projectOut, "fire_program.c")) && File.Exists(Path.Combine(projectOut, "fire_rt.h"))
                && File.Exists(Path.Combine(projectOut, "platform", "esp32", "fire_platform.h")) && File.Exists(Path.Combine(projectOut, "platform", "freertos", "fire_platform.h"))
                && File.ReadAllText(Path.Combine(projectOut, "CMakeLists.txt")).Contains("idf_component_register") && File.ReadAllText(Path.Combine(projectOut, "fire_program.c")).Contains("void app_main(void)"), $"{code2}\n{out2}\n{err2}");
            var err3 = new StringWriter();
            int code3 = CommandLineRunner.Run(new[] { "build", script, "-t", "amiga", "-o", Path.Combine(dir, "x") }, new StringWriter(), err3);
            CheckNat("build: unbekanntes Ziel ist ein Fehler der Befehlszeile", code3 == CommandLineRunner.ExitUsage && err3.ToString().Contains("amiga"), err3.ToString());
            var out4 = new StringWriter();
            int code4 = CommandLineRunner.Run(new[] { "build", script, "--engine", "vm", "-o", Path.Combine(dir, "vm.exe") }, out4, new StringWriter());
            CheckNat("build: --engine vm gewinnt gegen die Konfiguration", code4 == 0 && File.Exists(Path.Combine(dir, "vm.exe")));
            // a target other than this machine is always native (the VM exists only for this machine; esp32, wasm and other systems need no .NET): without
            // --engine and without a configuration, -t esp32 writes the component; --engine vm with such a target is a clear error of the command line
            string bare = Path.Combine(workDir, "build-bare");
            Directory.CreateDirectory(bare);
            string bareScript = Path.Combine(bare, "hello.fire");
            File.WriteAllText(bareScript, "print(1)\n");
            string bareOut = Path.Combine(bare, "component");
            var out5 = new StringWriter(); var err5 = new StringWriter();
            int code5 = CommandLineRunner.Run(new[] { "build", bareScript, "-t", "esp32", "-o", bareOut }, out5, err5);
            CheckNat("build: ein fremdes Ziel (-t esp32) baut ohne --engine nativ", code5 == 0 && File.Exists(Path.Combine(bareOut, "fire_program.c")) && File.Exists(Path.Combine(bareOut, "CMakeLists.txt")), $"{code5}\n{out5}\n{err5}");
            var err6 = new StringWriter();
            int code6 = CommandLineRunner.Run(new[] { "build", bareScript, "--engine", "vm", "-t", "esp32", "-o", Path.Combine(bare, "vm.exe") }, new StringWriter(), err6);
            CheckNat("build: --engine vm mit einem fremden Ziel ist ein Fehler der Befehlszeile", code6 == CommandLineRunner.ExitUsage && err6.ToString().Contains("esp32") && !File.Exists(Path.Combine(bare, "vm.exe")), err6.ToString());
            var out7 = new StringWriter();
            int code7 = CommandLineRunner.Run(new[] { "build", bareScript, "-t", TargetProfile.Host.Name, "-o", Path.Combine(bare, "host.exe") }, out7, new StringWriter());
            CheckNat("build: das eigene Ziel (-t " + TargetProfile.Host.Name + ") bleibt ohne --engine bei der VM", code7 == 0 && !out7.ToString().Contains("(native"), out7.ToString());
            // ---- a real ESP32 build (ESP-IDF: IDF_PATH, its export.sh) that runs on the ESP32 emulator of Espressif (qemu-system-xtensa, `idf_tools.py install qemu-xtensa`):
            // the program uses the runtime, exceptions, a fire thread (a FreeRTOS task), hash and Deflate (with the small window of a board) and prints like the VM.
            // The ESP-IDF project stays in ~/.cache/fire-tests/esp32 (the first build takes minutes, later ones only the program).
            string esp32Title = "ESP32 (ESP-IDF, QEMU): das Programm baut ohne Warnung und laeuft auf dem Emulator wie in der VM";
            string? idfPath = Environment.GetEnvironmentVariable("IDF_PATH");
            if (!TestSelected(esp32Title) || !TestResume.Pending(esp32Title, NatUnitKey(esp32Title))) { }
            else if (idfPath == null || !File.Exists(Path.Combine(idfPath, "export.sh"))) Console.WriteLine("(ESP32 mit ESP-IDF: uebersprungen - IDF_PATH fehlt)");
            else
            {
                // (a unit of FIRE_TESTS_RESUME/FIRE_TESTS_SHARD: TestResume.cs)
                using var resumeUnit11 = TestResume.Unit(esp32Title, NatUnitKey(esp32Title));
                const string espSource = "#import \"time\"\n#import \"hash\"\n#import \"compression\"\nclass Counter {\n    private int count = 0\n    int Next() { this.count++; return this.count }\n}\n"
                    + "var c = new Counter()\nvar l = new List<string>()\nfor (var i = 0; i < 3; i++) { l.Add(\"n\" + c.Next()) }\nprint(l[0] + \" \" + l[2])\n"
                    + "try { var a = [1]; print(a[5]) } catch (IndexOutOfBoundsException e) { print(\"caught\") }\nprint(Hash.Sha256.HexOf(\"abc\").Substring(0, 16))\n"
                    + "var text = \"\"\nfor (var i = 0; i < 200; i++) { text = text + \"fire \" + (i % 7) + \" \" }\nvar packed = Compression.GZip.Compress(text)\nprint((packed.Length < text.Length) + \" \" + (Compression.GZip.DecompressText(packed) == text))\n"
                    + "var n = 0\nfire { sync global { n = 42 } }\nwhile (n == 0) { Sleep(1ms) }\nprint(\"thread \" + n)\nprint(\"done\")\n";
                string espProject = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "fire-tests", "esp32");
                Directory.CreateDirectory(espProject);
                File.WriteAllText(Path.Combine(espProject, "CMakeLists.txt"), "cmake_minimum_required(VERSION 3.16)\ninclude($ENV{IDF_PATH}/tools/cmake/project.cmake)\nproject(firetest)\n");
                string espScript = Path.Combine(espProject, "test.fire");
                File.WriteAllText(espScript, espSource);
                if (Directory.Exists(Path.Combine(espProject, "main"))) Directory.Delete(Path.Combine(espProject, "main"), true);
                var espOut = new StringWriter(); var espErr = new StringWriter();
                int espCode = CommandLineRunner.Run(new[] { "build", espScript, "-t", "esp32", "-o", Path.Combine(espProject, "main") }, espOut, espErr);
                string shell = $"set -e; . \"{idfPath}/export.sh\" > /dev/null 2>&1; cd \"{espProject}\"; [ -f sdkconfig ] || idf.py set-target esp32 > idf.log 2>&1; idf.py build > idf.log 2>&1 || {{ tail -40 idf.log; exit 1; }}; "
                    + "grep -c 'warning:' idf.log > warnings.txt || true; cd build; python -m esptool --chip esp32 merge_bin --fill-flash-size 4MB -o ../flash.bin @flash_args > /dev/null; cd ..; "
                    + "command -v qemu-system-xtensa > /dev/null || { echo NOQEMU; exit 0; }; timeout 30 qemu-system-xtensa -nographic -machine esp32 -m 4M -drive file=flash.bin,if=mtd,format=raw -serial mon:stdio > qemu.log 2>&1 || true; "
                    + "sed -n '/main_task: Calling app_main/,/^done/p' qemu.log | grep -vE '^(I|W) \\(|main_task' ";
                string espRun = "";
                int espExit = -1;
                if (espCode == 0)
                {
                    using var bash = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("bash", new[] { "-c", shell }) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
                    var bashErr = bash.StandardError.ReadToEndAsync();
                    espRun = bash.StandardOutput.ReadToEnd();
                    bash.WaitForExit();
                    espExit = bash.ExitCode;
                    espRun += bashErr.Result;
                }
                string warnings = File.Exists(Path.Combine(espProject, "warnings.txt")) ? File.ReadAllText(Path.Combine(espProject, "warnings.txt")).Trim() : "?";
                if (espRun.Contains("NOQEMU")) CheckNat("ESP32 (ESP-IDF): das Programm baut ohne Warnung (QEMU fehlt: nicht ausgefuehrt)", espCode == 0 && espExit == 0 && warnings == "0", espOut + "\n" + espErr + "\n" + espRun);
                else
                {
                    var vmLines = CVmTest.Run(new[] { espSource }, VmExecutionMode.Release).Printed;   // (the VM in C as the reference)
                    string got = string.Join("\n", espRun.Replace("\r", "").Split('\n').Where(line => line.Length > 0));
                    CheckNat(esp32Title, espCode == 0 && espExit == 0 && warnings == "0" && got == string.Join("\n", vmLines), $"build {espCode}, warnings {warnings}\n  VM:\n{string.Join("\n", vmLines)}\n  ESP32:\n{got}\n{espErr}");
                    // linkage "static" (ROADMAP 9): the library modules as translation units of their own in the component (idf.py compiles them into its library), their C
                    // from the cache; a project of its own in ~/.cache/fire-tests/esp32-static
                    string espStatic = Path.Combine(Path.GetDirectoryName(espProject)!, "esp32-static");
                    Directory.CreateDirectory(espStatic);
                    File.WriteAllText(Path.Combine(espStatic, "CMakeLists.txt"), "cmake_minimum_required(VERSION 3.16)\ninclude($ENV{IDF_PATH}/tools/cmake/project.cmake)\nproject(firetest)\n");
                    File.WriteAllText(Path.Combine(espStatic, "test.fire"), espSource);
                    string? cacheBefore = Environment.GetEnvironmentVariable("FIRE_NATIVE_CACHE");
                    Environment.SetEnvironmentVariable("FIRE_NATIVE_CACHE", Path.Combine(espStatic, "cache"));
                    var stOut = new StringWriter(); var stErr = new StringWriter();
                    int stCode;
                    try { stCode = CommandLineRunner.Run(new[] { "build", Path.Combine(espStatic, "test.fire"), "-t", "esp32", "--linkage", "static", "-o", Path.Combine(espStatic, "main") }, stOut, stErr); }
                    finally { Environment.SetEnvironmentVariable("FIRE_NATIVE_CACHE", cacheBefore); }
                    string stRun = "";
                    if (stCode == 0)
                    {
                        using var bash = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("bash", new[] { "-c", shell.Replace(espProject, espStatic) }) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
                        var bashErr = bash.StandardError.ReadToEndAsync();
                        stRun = bash.StandardOutput.ReadToEnd() + bashErr.Result;
                        bash.WaitForExit();
                    }
                    string stWarnings = File.Exists(Path.Combine(espStatic, "warnings.txt")) ? File.ReadAllText(Path.Combine(espStatic, "warnings.txt")).Trim() : "?";
                    string stGot = string.Join("\n", stRun.Replace("\r", "").Split('\n').Where(line => line.Length > 0));
                    bool stModules = stOut.ToString().Contains("module core:") && File.Exists(Path.Combine(espStatic, "main", "fire_mcore.c"));
                    CheckNat("ESP32 (ESP-IDF, QEMU): linkage static - die Module als eigene Uebersetzungseinheiten der Komponente, laeuft wie in der VM",
                        stCode == 0 && stModules && stWarnings == "0" && stGot == string.Join("\n", vmLines), $"build {stCode}, warnings {stWarnings}\n{stOut}\n{stErr}\n  ESP32:\n{stGot}");
                }
                resumeUnit11.Done();
            }
        }

        // What is not yet translated must be rejected clearly - never translated wrongly
        try
        {
            fire.Native.CppGenerator.Generate(new Linker().CompileAndLink(new[] { "#import \"windows\"\nvar x = __GRPHWinCreate(1)" }, null, null, VmExecutionMode.Release));
            CheckNat("Nicht unterstuetzte Opcodes werden abgelehnt", false, "keine Ausnahme");
        }
        catch (fire.Native.NativeNotSupportedException ex)
        {
            CheckNat("Nicht unterstuetzte native Funktionen werden abgelehnt", ex.Message.Contains("native function"), ex.Message);
        }
        try { Directory.Delete(workDir, true); } catch (IOException) { }
    }

    // ---- Packages (ember): fpk, store, sources, #import "name" in VM and native ----
    {
        Console.WriteLine("=== Pakete (ember) ===");
        string pkgDir = Path.Combine(Path.GetTempPath(), "fire-pkg-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(pkgDir);
        var savedStore = fire.Package.Manager.PackageStore.Default;
        try
        {
            // a package whose parts are files in a folder of its own; returns the path of the forge file
            string MakeForge(string name, string version, string importName, string? prelude, string? nativeCpp, Action<fire.Package.Manager.PackageManifest>? tweak = null)
            {
                string dir = Path.Combine(pkgDir, "src-" + name + "-" + version);
                Directory.CreateDirectory(dir);
                var m = new fire.Package.Manager.PackageManifest { Name = name, Version = version, Author = "tester", Description = "test package " + name };
                var import = new fire.Package.Manager.PackageImport { Name = importName };
                if (prelude != null) { File.WriteAllText(Path.Combine(dir, importName + ".fire"), prelude); import.Prelude = Path.Combine(dir, importName + ".fire"); }
                if (nativeCpp != null)
                {
                    File.WriteAllText(Path.Combine(dir, importName + ".hpp"), nativeCpp);
                    import.Native = new fire.Package.Manager.PackageNative { Sources = { Path.Combine(dir, importName + ".hpp") } };
                }
                m.Imports.Add(import);
                tweak?.Invoke(m);
                string json = Path.Combine(dir, "forge.json");
                m.Save(json);
                return json;
            }

            // the template: forging it works as it is, the package.json has relative paths, the kept description absolute ones and forges the same package again
            string exJson = Path.Combine(pkgDir, "example", "mathkit.json");
            fire.Package.Manager.Templates.Example(exJson, writeFiles: true).Save(exJson);
            var forged = fire.Package.Manager.Fpk.Forge(exJson, Path.Combine(pkgDir, "out"));
            var inside = fire.Package.Manager.Fpk.ReadManifest(forged.PackagePath);
            var kept = fire.Package.Manager.PackageManifest.Load(forged.JsonCopyPath);
            CheckNat("Paket: ember create + forge: package.json im fpk mit relativen Pfaden, die Beschreibung daneben mit absoluten",
                File.Exists(forged.PackagePath) && inside.Name == "mathkit" && inside.Imports[0].Prelude == "mathkit/mathkit.fire" && inside.Imports[0].Native!.Sources[0] == "mathkit/mathkit.hpp"
                && Path.IsPathRooted(kept.Imports[0].Prelude!) && Path.IsPathRooted(kept.Imports[0].Native!.Sources[0]) && forged.JsonCopyPath.Replace('\\', '/').EndsWith("out/json/mathkit-1.0.0.json"));
            var again = fire.Package.Manager.Fpk.Forge(forged.JsonCopyPath, Path.Combine(pkgDir, "out2"));
            CheckNat("Paket: die kopierte Beschreibung baut dasselbe Paket noch einmal", again.Manifest.ToJson() == forged.Manifest.ToJson());
            var blank = fire.Package.Manager.Templates.Blank();
            CheckNat("Paket: ember blank hat alle Felder, aber leer, und ist ungueltig", blank.Imports.Count == 1 && blank.Name == "" && blank.Validate().Count >= 3);
            var reserved = new fire.Package.Manager.PackageManifest { Name = "x", Version = "1.0", Imports = { new fire.Package.Manager.PackageImport { Name = "io", Prelude = "a.fire" } } };
            CheckNat("Paket: ein Importname des Compilers ist verboten, eine fehlende Datei beim Schmieden ein Fehler",
                reserved.Validate().Any(p => p.Contains("belongs to the compiler")) && PkgThrows(() => fire.Package.Manager.Fpk.Forge(MakeForge("missing", "1.0.0", "missing", "class A {}", null, m => m.Imports[0].Prelude = Path.Combine(pkgDir, "nothere.fire")), Path.Combine(pkgDir, "out"))));

            // the store: install, find the import (not case sensitive), replace, remove
            var store = new fire.Package.Manager.PackageStore(Path.Combine(pkgDir, "Packages"));
            string demoFpk = fire.Package.Manager.Fpk.Forge(MakeForge("pkgdemo", "1.0.0", "pkgdemo", "class PkgDemo { static Twice(x) { return x * 2 } }", null), Path.Combine(pkgDir, "out")).PackagePath;
            store.Install(demoFpk);
            CheckNat("Paket: installieren, den Import finden (Gross-/Kleinschreibung egal) und entfernen",
                store.Find("PKGDEMO") is { Version: "1.0.0" } && store.FindImport("PkgDemo")?.Key == "pkg:pkgdemo" && store.FindImport("PkgDemo")!.ReadPrelude()!.Contains("Twice")
                && store.Remove("pkgdemo") && store.Find("pkgdemo") == null && !store.Remove("pkgdemo"));

            // a zip that leaves its folder is refused
            string evil = Path.Combine(pkgDir, "evil.fpk");
            using (var zip = System.IO.Compression.ZipFile.Open(evil, System.IO.Compression.ZipArchiveMode.Create))
            {
                var m = new fire.Package.Manager.PackageManifest { Name = "evil", Version = "1.0.0", Imports = { new fire.Package.Manager.PackageImport { Name = "evil", Prelude = "../evil.fire" } } };
                using (var w = new StreamWriter(zip.CreateEntry("package.json").Open())) w.Write(m.ToJson());
                using (var w = new StreamWriter(zip.CreateEntry("../evil.fire").Open())) w.Write("class E {}");
            }
            CheckNat("Paket: ein fpk mit einem Pfad aus dem Ordner heraus wird abgelehnt", PkgThrows(() => store.Install(evil)) && !File.Exists(Path.Combine(pkgDir, "evil.fire")));

            // the sources: a folder of packages (two versions, a dependency) and an index with checksums
            string sourceDir = Path.Combine(pkgDir, "PackageSource");
            Directory.CreateDirectory(sourceDir);
            foreach (var (n, v, dep) in new[] { ("libbase", "1.0.0", ""), ("libtop", "1.0.0", "libbase"), ("libtop", "1.2.0", "libbase"), ("libtop", "1.10.0", "libbase") })
            {
                string forge = MakeForge(n, v, n, $"class {n}V{v.Replace(".", "_")} {{ }}", null, m => { if (dep.Length > 0) m.Dependencies.Add(dep); });
                File.Copy(fire.Package.Manager.Fpk.Forge(forge, Path.Combine(pkgDir, "built")).PackagePath, Path.Combine(sourceDir, $"{n}-{v}.fpk"), true);
            }
            var service = new fire.Package.Manager.PackageManagerService(new fire.Package.Manager.PackageStore(Path.Combine(pkgDir, "P2")), new fire.Package.Manager.IPackageSource[] { new fire.Package.Manager.FolderSource(sourceDir) });
            var found = service.Find("lib");
            var top = found.First(l => l.Name == "libtop");
            CheckNat("Paket: find sucht in Namen und Beschreibung, die neueste Version zaehlt numerisch (1.10 > 1.2)", found.Count == 2 && top.Versions.Count == 3 && top.Latest!.Version == "1.10.0" && service.Find("LIBTOP").Count == 1 && service.Find("nothing like it").Count == 0);
            var pkgLog = new List<string>();
            service.Install("libtop@1.2.0", pkgLog.Add);
            CheckNat("Paket: install holt die gewuenschte Version samt Abhaengigkeit, ein zweites Mal ist nichts zu tun",
                service.Store.Find("libtop")?.Version == "1.2.0" && service.Store.Find("libbase") != null && PkgThrows(() => service.Install("libtop@9.9.9")) && service.Install("libtop@1.2.0", pkgLog.Add).Count == 0);
            bool refusedDependency = PkgThrows(() => service.Remove("libbase"));
            service.Remove("libtop");
            service.Remove("libbase");
            CheckNat("Paket: remove verweigert, was ein anderes Paket braucht", refusedDependency && service.Store.Installed().Count == 0);
            string indexFile = Path.Combine(sourceDir, "index.json");
            File.WriteAllText(indexFile, fire.Package.Manager.PackageIndex.Build(sourceDir, null));
            var indexed = new fire.Package.Manager.IndexSource(indexFile).List();
            CheckNat("Paket: ein Index (ember index) nennt Namen, Autor, Beschreibung, Versionen und Pruefsummen", indexed.Count == 2 && indexed.First(l => l.Name == "libtop").Versions.Count == 3 && indexed.First(l => l.Name == "libtop").Author == "tester"
                && indexed.All(l => l.Versions.All(v => v.Sha256 is { Length: 64 } && File.Exists(v.Url))));
            var viaIndex = new fire.Package.Manager.PackageManagerService(new fire.Package.Manager.PackageStore(Path.Combine(pkgDir, "P3")), new fire.Package.Manager.IPackageSource[] { new fire.Package.Manager.IndexSource(indexFile) });
            viaIndex.Install("libbase");
            var broken = new fire.Package.Manager.PackageVersion("1.0.0", Path.Combine(sourceDir, "libbase-1.0.0.fpk"), new string('0', 64));
            CheckNat("Paket: install ueber einen Index, eine falsche Pruefsumme wird abgelehnt", viaIndex.Store.Find("libbase") != null && PkgThrows(() => new fire.Package.Manager.IndexSource(indexFile).Fetch(broken, Path.Combine(pkgDir, "dl"))));

            // #import "name": the prelude of a package in the VM; an unknown import points to ember
            fire.Package.Manager.PackageStore.Default = new fire.Package.Manager.PackageStore(Path.Combine(pkgDir, "Packages"));
            fire.Package.Manager.PackageStore.Default.Install(demoFpk);
            string vmPkg = vmOutput("#import \"pkgdemo\"\nprint(PkgDemo.Twice(21))");
            string unknown;
            try { string res = vmOutput("#import \"nosuchpackage\"\nprint(1)"); unknown = res.StartsWith(NotCompiled) ? res : ""; } catch (Exception ex) { unknown = ex.Message; }   // (vmOutput gives the errors of the compiler as its result)
            CheckNat("Paket: #import \"name\" bringt die Prelude eines installierten Pakets (VM); ein unbekannter Import verweist auf ember", vmPkg == "42\n" && unknown.Contains("not a known extension") && unknown.Contains("ember"), vmPkg + unknown);
            var onEsp = new Linker().CompileAndLink(new[] { "#import \"pkgdemo\"\nprint(PkgDemo.Twice(2))" }, null, null, VmExecutionMode.Release, null, TargetProfile.Esp32);
            CheckNat("Paket: ein Import eines Pakets ist auf jedem Ziel erlaubt (die Plattformen nennt der native Teil)", onEsp.NativeImports.Contains("pkg:pkgdemo"));

            // the natives (C++): not in the VM, in the native backend the source is part of the generated file; needsList gives the native the list of the scope
            string natPrelude = "class PkgNat { static Twice(x) { return __pk_twice(x) }\n static Squares(n) { return __pk_squares(n) } }";
            string natCpp = "#include <cstdint>\nnamespace fire {\ninline Value pk_twice(Value a) { return Int(a.i * 2); }\n"
                + "inline Value pk_squares(Value n, OwnList* list) { Arr* a = allocArr((uint32_t)n.i, list); for (int64_t i = 0; i < n.i; i++) a->items()[i] = Int(i * i); return ArrV(a); }\n}\n";
            string natFpk = fire.Package.Manager.Fpk.Forge(MakeForge("pknat", "1.0.0", "pknat", natPrelude, natCpp, m =>
            {
                m.Imports[0].Native!.Functions.Add(new fire.Package.Manager.PackageNativeFunction { Name = "__pk_twice", Arguments = 1, Cpp = "pk_twice" });
                m.Imports[0].Native!.Functions.Add(new fire.Package.Manager.PackageNativeFunction { Name = "__pk_squares", Arguments = 1, Cpp = "pk_squares", NeedsList = true, ReturnsReference = true });
            }), Path.Combine(pkgDir, "out")).PackagePath;
            fire.Package.Manager.PackageStore.Default.Install(natFpk);
            string natScript = "#import \"pknat\"\nprint(PkgNat.Twice(21))\nvar s = PkgNat.Squares(4)\nprint(s.length)\nprint(s[3])";
            var pkgSources = fire.Native.CppGenerator.GenerateSources(new Linker().CompileAndLink(new[] { natScript }, null, null, VmExecutionMode.Release));
            string pkgCpp = pkgSources.Natives ?? "";
            CheckNat("Paket: der C++-Quelltext des Natives steht in den Natives (C++) des Programms, das Programm (C) ruft sie ueber eine C-Funktion",
                pkgCpp.Contains("---- package pknat 1.0.0, import \"pknat\"") && pkgCpp.Contains("inline Value pk_twice") && pkgCpp.Contains("return pk_squares(a0, list); }")
                && pkgSources.Program.Contains("Value fire_nat_1(Value a0, OwnList* list);   // pk_squares"), pkgSources.Program + pkgCpp);
            string? pkgCxx = FindCxx();
            if (pkgCxx != null)
            {
                string run = Path.Combine(pkgDir, "run");
                fire.Native.NativeRuntimeFiles.WriteTo(run);

                string RunTool(string tool, string args, string? workDirectory = null)
                {
                    using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(tool, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = workDirectory ?? run })!;
                    var err = p.StandardError.ReadToEndAsync();
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    return (p.ExitCode == 0 ? "" : $"[exit {p.ExitCode}] ") + output + err.Result;
                }
                string build = NativeTestBuild.Build(NativeTestBuild.In(run), pkgCxx, pkgSources, run, "pkg", "-pthread -O1 -Wall -Wextra", Path.Combine(run, "pkg.bin"), out int pkgExit);
                string result = pkgExit == 0 ? RunTool(Path.Combine(run, "pkg.bin"), "") : "C/C++-Compiler: " + build;
                CheckNat("Paket: das C++-Native laeuft im uebersetzten Programm (Zahlen, ein Array aus der Scope-Liste)", result == "42\n4\n9\n", result);

                // the same natives in the virtual machine: built into a shared library with the C ABI, called through it (numbers, text, arrays, buffers cross; errors are reported)
                string vmNative;
                try { vmNative = vmOutput(natScript); } catch (Exception ex) { vmNative = ex.Message; }
                CheckNat("Paket: dasselbe C++-Native laeuft in der VM ueber eine Shared Library (C-ABI) und liefert dasselbe wie nativ", vmNative == result, vmNative);
                string abiCpp = "#include <cstdint>\nnamespace fire {\n"
                    + "inline Value ab_upper(Value s, OwnList* list) { const Str* t = strOf(s); Str* r = allocStr(t->length, list); for (uint32_t i = 0; i < t->length; i++) strChars(r)[i] = t->data[i] >= 'a' && t->data[i] <= 'z' ? (char16_t)(t->data[i] - 32) : t->data[i]; return StrV(r); }\n"
                    + "inline Value ab_sum(Value arr) { Arr* a = arrOf(arr); double t = 0; for (uint32_t i = 0; i < a->length; i++) t += (double)toR(a->items()[i]); return Float((Real)t); }\n"
                    + "inline Value ab_bytes(Value buf, OwnList* list) { Buf* b = bufOf(buf); Buf* r = allocBuf(b->length, list); for (uint32_t i = 0; i < b->length; i++) r->bytes()[i] = (uint8_t)(b->bytes()[i] + 1); return BufV(r); }\n"
                    + "inline Value ab_fill(Value buf, Value v) { Buf* b = bufOf(buf); for (uint32_t i = 0; i < b->length; i++) b->bytes()[i] = (uint8_t)v.i; return Int(b->length); }\n"
                    + "inline Value ab_fail(Value n) { return indexError(\"Array index\", n.i, 3); }\n}\n";
                string abiFpk = fire.Package.Manager.Fpk.Forge(MakeForge("pkabi", "1.0.0", "pkabi", "class PkAbi { static Upper(s) { return __ab_upper(s) }\n static Sum(a) { return __ab_sum(a) }\n static Bytes(b) { return __ab_bytes(b) }\n static Fill(b, v) { return __ab_fill(b, v) }\n static Fail(n) { return __ab_fail(n) } }", abiCpp, m =>
                {
                    foreach (var (n, c, argc, list) in new[] { ("__ab_upper", "ab_upper", 1, true), ("__ab_sum", "ab_sum", 1, false), ("__ab_bytes", "ab_bytes", 1, true), ("__ab_fill", "ab_fill", 2, false), ("__ab_fail", "ab_fail", 1, false) })
                        m.Imports[0].Native!.Functions.Add(new fire.Package.Manager.PackageNativeFunction { Name = n, Arguments = argc, Cpp = c, NeedsList = list, ReturnsReference = list });
                }), Path.Combine(pkgDir, "out")).PackagePath;
                fire.Package.Manager.PackageStore.Default.Install(abiFpk);
                string abiScript = "#import \"pkabi\"\nprint(PkAbi.Upper(\"hello w\u00f6rld\"))\nprint(PkAbi.Sum([1, 2.5, 3]))\nvar b = new byte[3]\nb[0] = 1; b[1] = 2; b[2] = 255\nvar c = PkAbi.Bytes(b)\nprint(c[0] + \" \" + c[1] + \" \" + c[2] + \" \" + b[2])";
                string abiVm;
                try { abiVm = vmOutput(abiScript); } catch (Exception ex) { abiVm = ex.Message; }
                string abiFail;
                try { abiFail = vmOutput("#import \"pkabi\"\ntry { PkAbi.Fail(7) } catch (IndexOutOfBoundsException e) { print(\"caught: \" + e.message) }"); } catch (Exception ex) { abiFail = ex.Message; }
                CheckNat("Paket: Zahlen, Text (UTF-16), Arrays und Byte-Puffer gehen ueber die C-ABI hin und zurueck",
                    abiVm == "HELLO W\u00f6RLD\n6.5\n2 3 0 255\n", abiVm);
                string fillVm;
                try { fillVm = vmOutput("#import \"pkabi\"\nvar big = new byte[1000000]\nprint(PkAbi.Fill(big, 7))\nprint(big[0] + \" \" + big[500000] + \" \" + big[999999])\nvar small = new byte[4]\nPkAbi.Fill(small, 9)\nprint(small[3])"); } catch (Exception ex) { fillVm = ex.Message; }
                CheckNat("Paket: ein Byte-Puffer geht ohne Kopie an die Native (in place beschrieben, auch ein grosser wie ein Framebuffer)", fillVm == "1000000\n7 7 7\n9\n", fillVm);
                CheckNat("Paket: ein Fehler der Native (hier IndexOutOfBounds) wird eine abfangbare Ausnahme seiner Klasse mit seinem Text", abiFail.StartsWith("caught: ") && abiFail.Contains("Array index 7 out of range"), abiFail);

                // machine words as arguments (fire_rt.h wordsArg, SPEC 8.10): a word buffer without a copy (written in place), an array copied, a byte buffer refused
                string wordsCpp = "#include <cstdint>\nnamespace fire {\n"
                    + "inline Value pw_sum(Value v) { FireWords w; if (!wordsArg(v, \"PkWords.Sum\", &w)) return Undef(); FireWord t = 0; for (uint32_t i = 0; i < w.length; i++) t = (FireWord)(t + w.data[i]); return WordV(t); }\n"
                    + "inline Value pw_inc(Value v) { FireWords w; if (!wordsArg(v, \"PkWords.Inc\", &w)) return Undef(); for (uint32_t i = 0; i < w.length; i++) ((FireWord*)w.data)[i]++; return Int(w.length); }\n}\n";
                string wordsFpk = fire.Package.Manager.Fpk.Forge(MakeForge("pkwords", "1.0.0", "pkwords", "class PkWords { static Sum(w) { return __pw_sum(w) }\n static Inc(w) { return __pw_inc(w) } }", wordsCpp, m =>
                {
                    m.Imports[0].Native!.Functions.Add(new fire.Package.Manager.PackageNativeFunction { Name = "__pw_sum", Arguments = 1, Cpp = "pw_sum" });
                    m.Imports[0].Native!.Functions.Add(new fire.Package.Manager.PackageNativeFunction { Name = "__pw_inc", Arguments = 1, Cpp = "pw_inc" });
                }), Path.Combine(pkgDir, "out")).PackagePath;
                fire.Package.Manager.PackageStore.Default.Install(wordsFpk);
                string wordsScript = "#import \"pkwords\"\nword w[] = [1, 2, 18446744073709551615]\nprint(PkWords.Sum(w) + \" \" + PkWords.Sum([5, 6]) + \" \" + (PkWords.Sum(w) is of word))\n"
                    + "print(PkWords.Inc(w) + \" \" + w[0] + \" \" + w[2])\ntry { PkWords.Sum(new byte[2]) } catch (ArgumentException e) { print(e.message) }\ntry { PkWords.Sum([1, \"x\"]) } catch (ArgumentException e) { print(e.message) }\n";
                string wordsVm;
                try { wordsVm = vmOutput(wordsScript); } catch (Exception ex) { wordsVm = ex.Message; }
                var wordsSources = fire.Native.CppGenerator.GenerateSources(new Linker().CompileAndLink(new[] { wordsScript }, null, null, VmExecutionMode.Release));
                string wordsBuild = NativeTestBuild.Build(NativeTestBuild.In(run), pkgCxx, wordsSources, run, "pkwords", "-pthread -O1 -Wall -Wextra", Path.Combine(run, "pkwords.bin"), out int wordsExit);
                string wordsNat = wordsExit == 0 ? RunTool(Path.Combine(run, "pkwords.bin"), "") : "C/C++-Compiler: " + wordsBuild;
                CheckNat("Paket: wordsArg - ein Wortpuffer ohne Kopie (in place beschrieben), ein Array kopiert, ein Bytepuffer abgelehnt; VM (C-ABI) und nativ gleich",
                    wordsVm == "2 11 True\n3 2 0\nPkWords.Sum expects words (a word buffer or an array of ints), not a byte buffer.\nPkWords.Sum: element 1 of the array is a String, not an int or a word.\n" && wordsNat == wordsVm,
                    $"  VM:\n{wordsVm}\n  nativ:\n{wordsNat}");
                // (a VM for a target of 16-bit words - its word buffer to the library as a copy in 64 bits and back cut to 16 - was the C# VM's: the VM in C has words of
                // 64 bits only, M1)

                // per platform: sources of the target are added; the VM uses those of this machine
                string whichFpk = fire.Package.Manager.Fpk.Forge(MakeForge("pkwhich", "1.0.0", "pkwhich", "class PkWhich { static Which() { return __pk_which() } }", "namespace fire { }\n", m =>
                {
                    string d = Path.Combine(pkgDir, "which");
                    Directory.CreateDirectory(d);
                    var native = m.Imports[0].Native!;
                    foreach (var (key, value) in new[] { ("posix", 1), ("windows", 2), ("freertos", 3) })
                    {
                        File.WriteAllText(Path.Combine(d, key + ".hpp"), $"namespace fire {{ inline Value pk_which_{key}() {{ return Int({value}); }} }}\n");
                        native.PlatformSources[key] = new fire.Package.Manager.NativeSourceList { Path.Combine(d, key + ".hpp") };
                    }
                    File.WriteAllText(Path.Combine(d, "common.hpp"), "namespace fire {\n#if defined(FIRE_HAL_WINDOWS)\ninline Value pk_which() { return pk_which_windows(); }\n#elif defined(FIRE_HAL_FREERTOS)\ninline Value pk_which() { return pk_which_freertos(); }\n#else\ninline Value pk_which() { return pk_which_posix(); }\n#endif\n}\n");
                    native.Sources.Clear();
                    native.Sources.Add(Path.Combine(d, "common.hpp"));
                    native.Functions.Add(new fire.Package.Manager.PackageNativeFunction { Name = "__pk_which", Arguments = 0, Cpp = "pk_which" });
                }), Path.Combine(pkgDir, "out")).PackagePath;
                fire.Package.Manager.PackageStore.Default.Install(whichFpk);
                string whichScript = "#import \"pkwhich\"\nprint(PkWhich.Which())";
                string cppLinux = fire.Native.CppGenerator.GenerateSources(new Linker().CompileAndLink(new[] { whichScript }, null, null, VmExecutionMode.Release, null, TargetProfile.Linux), TargetProfile.Linux).Natives ?? "";
                string cppRtos = fire.Native.CppGenerator.GenerateSources(new Linker().CompileAndLink(new[] { whichScript }, null, null, VmExecutionMode.Release, null, TargetProfile.FreeRtos), TargetProfile.FreeRtos).Natives ?? "";
                string vmWhich;
                try { vmWhich = vmOutput(whichScript); } catch (Exception ex) { vmWhich = ex.Message; }
                CheckNat("Paket: platformSources - jedes Ziel bekommt seine Quellen (Linux: posix, FreeRTOS: freertos), die VM die der eigenen Plattform",
                    cppLinux.Contains("pk_which_posix() {") && !cppLinux.Contains("pk_which_freertos() {") && !cppLinux.Contains("pk_which_windows() {") && cppRtos.Contains("pk_which_freertos() {") && !cppRtos.Contains("pk_which_posix() {")
                    && vmWhich == (OperatingSystem.IsWindows() ? "2\n" : "1\n"), vmWhich);

                // a program that is packed carries the library of its package: it runs where the package is not installed
                string packed = Path.Combine(pkgDir, "packed.bin");
                new Linker().CompileAndLink(new[] { natScript }, null, packed, VmExecutionMode.Release);
                var storeForPack = fire.Package.Manager.PackageStore.Default;
                fire.Package.Manager.PackageStore.Default = new fire.Package.Manager.PackageStore(Path.Combine(pkgDir, "EmptyStore"));
                string packedRun = "";
                if (File.Exists(packed) && !OperatingSystem.IsWindows())
                {
                    using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(packed) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
                    var err = p.StandardError.ReadToEndAsync();
                    packedRun = p.StandardOutput.ReadToEnd() + err.Result;
                    p.WaitForExit();
                }
                CheckNat("Paket: ein gepacktes Programm bringt die Bibliothek seiner Pakete mit (laeuft ohne das Paket)", OperatingSystem.IsWindows() || packedRun == "42\n4\n9\n", packedRun);
                fire.Package.Manager.PackageStore.Default = storeForPack;
            }
            // the platform of the native part: a package for windows only is refused for another target
            string winOnly = fire.Package.Manager.Fpk.Forge(MakeForge("pkgwin", "1.0.0", "pkgwin", "class PkgWin { static F(x) { return __pk_win(x) } }", "namespace fire { inline Value pk_win(Value a) { return a; } }\n", m =>
            {
                m.Imports[0].Native!.Platforms.Add("windows");
                m.Imports[0].Native!.Functions.Add(new fire.Package.Manager.PackageNativeFunction { Name = "__pk_win", Arguments = 1, Cpp = "pk_win" });
            }), Path.Combine(pkgDir, "out")).PackagePath;
            fire.Package.Manager.PackageStore.Default.Install(winOnly);
            bool refused = false;
            try { fire.Native.CppGenerator.Generate(new Linker().CompileAndLink(new[] { "#import \"pkgwin\"\nprint(PkgWin.F(1))" }, null, null, VmExecutionMode.Release, null, TargetProfile.Linux), TargetProfile.Linux); }
            catch (fire.Native.NativeNotSupportedException ex) { refused = ex.Message.Contains("pkgwin") && ex.Message.Contains("windows"); }
            CheckNat("Paket: ein natives Paket nur fuer andere Plattformen wird fuers Ziel abgelehnt", refused);

            // the standard libraries as projects: packed and installed when missing, not again when nothing changed, again when a file changed
            {
                string sourceCopy = Path.Combine(pkgDir, "Source");
                CopyTree(StandardSources.DefaultFolder, sourceCopy);
                var bridgeStore = new fire.Package.Manager.PackageStore(Path.Combine(pkgDir, "BridgePackages"));
                var savedDefault = fire.Package.Manager.PackageStore.Default;
                fire.Package.Manager.PackageStore.Default = bridgeStore;
                try
                {
                    var first = StandardSources.EnsureInstalled(null, sourceCopy, bridgeStore, prebuiltFolder: null);
                    var second = StandardSources.EnsureInstalled(null, sourceCopy, bridgeStore, prebuiltFolder: null);
                    File.AppendAllText(Path.Combine(sourceCopy, "random", "random.fire"), "\nclass AddedByTheTest { }\n");
                    var third = StandardSources.EnsureInstalled(null, sourceCopy, bridgeStore, prebuiltFolder: null);
                    var io = bridgeStore.FindImport("io")!;
                    var linq = bridgeStore.Find("fire-linq")!;
                    CheckNat("Standardpakete: je Projekt ein Paket mit Prelude und C++-Quellen, als standard markiert, ohne die Tests",
                        io.Import.Standard && io.Import.Prelude != null && io.Import.Native?.Sources.Count > 0 && linq.Manifest.Dependencies.Contains("fire-reflection")
                        && bridgeStore.FindImport("random")!.Import.Requires.Contains("time") && !Directory.Exists(Path.Combine(bridgeStore.Find("fire-random")!.Directory, "testing"))
                        && !(bridgeStore.FindImport("random")!.ReadPrelude() ?? "").Contains("RandomTests"));
                    CheckNat("Standardpakete: beim Start werden fehlende installiert, danach nichts mehr, nach einer Aenderung nur das geaenderte",
                        first.Count == fire.Package.Manager.StandardPackages.Bridges.Count && bridgeStore.Installed().Count == fire.Package.Manager.StandardPackages.Bridges.Count && second.Count == 0
                        && third.SequenceEqual(new[] { "fire-random" }) && (bridgeStore.FindImport("random")!.ReadPrelude() ?? "").Contains("AddedByTheTest"), string.Join(",", first) + " / " + string.Join(",", second) + " / " + string.Join(",", third));
                    var windowsPackage = bridgeStore.Find("fire-windows");
                    CheckNat("Bruecken-Pakete: fire-windows bringt die Vorlage Desktop mit; sie steht nach dem Installieren im Katalog",
                        windowsPackage != null && File.Exists(Path.Combine(windowsPackage.Directory, "templates", "Project", "Desktop", "template.json"))
                        && fire.Projects.TemplateCatalog.Load(builtinRoot: Path.Combine(pkgDir, "none"), userRoot: Path.Combine(pkgDir, "none"), store: bridgeStore).Find(fire.Projects.TemplateScope.Project, "Desktop") is { } desktopTemplate && desktopTemplate.Source.Contains("fire-windows"));
                    string builtIn = vmOutput("#import \"time\"\n#import \"ui\"\nprint(TimeSpan.FromSeconds(90).TotalSeconds)");
                    CheckNat("Standardpakete: #import \"time\" und \"ui\" (Prelude aus dem Paket, Natives der VM) laufen", builtIn == "90\n", builtIn);
                }
                finally { fire.Package.Manager.PackageStore.Default = savedDefault; }
            }

            // the toolchain provider: without a compiler the host is asked (cancel / change / install); the answer decides
            {
                string? savedPath = Environment.GetEnvironmentVariable("PATH");
                var savedAsk = fire.Native.ToolchainProvider.Ask;
                var savedChange = fire.Native.ToolchainProvider.ChangeToolchain;
                try
                {
                    var wantedToolchain = fire.Native.ToolchainDef.BuiltIn["gcc"];
                    Environment.SetEnvironmentVariable("PATH", "");
                    bool noneFound = fire.Native.ToolchainDetector.Find(wantedToolchain) == null && fire.Native.ToolchainSetup.Detect() == null;
                    if (noneFound)
                    {
                        fire.Native.ToolchainRequest? seen = null;
                        fire.Native.ToolchainProvider.Ask = r => { seen = r; return fire.Native.ToolchainChoice.Cancel; };
                        var canceled = fire.Native.ToolchainProvider.Require(wantedToolchain, "A package needs it (test).");
                        bool cancelOk = canceled == null && seen != null && seen.Message.Contains("A package needs it (test).") && seen.Message.Contains("none was found");
                        fire.Native.ToolchainProvider.Ask = r => fire.Native.ToolchainChoice.Change;
                        fire.Native.ToolchainProvider.ChangeToolchain = () => { Environment.SetEnvironmentVariable("PATH", savedPath); return true; };
                        var changed = fire.Native.ToolchainProvider.Require(wantedToolchain, "test");
                        Environment.SetEnvironmentVariable("PATH", "");
                        fire.Native.ToolchainProvider.Ask = null;
                        var unasked = fire.Native.ToolchainProvider.Require(wantedToolchain, "test");
                        CheckNat("Toolchain: ohne Compiler wird der Host gefragt (abbrechen = nichts; aendern = es wird neu gesucht; ohne Host keine Frage)", cancelOk && changed != null && unasked == null);
                    }
                    else CheckNat("Toolchain: (uebersprungen - ein Compiler liegt ausserhalb des PATH)", true);
                }
                finally
                {
                    Environment.SetEnvironmentVariable("PATH", savedPath);
                    fire.Native.ToolchainProvider.Ask = savedAsk;
                    fire.Native.ToolchainProvider.ChangeToolchain = savedChange;
                }
                if (fire.Native.ToolchainSetup.Detect() is { } detected)
                {
                    var test = fire.Compiler.NativeBuilder.TestToolchain(detected.Toolchain);
                    CheckNat("Toolchain: der Test uebersetzt und startet ein kleines Programm", test.Ok, test.Log);
                }
            }
        }
        finally
        {
            fire.Package.Manager.PackageStore.Default = savedStore;
            try { Directory.Delete(pkgDir, true); } catch (IOException) { }
        }

        bool PkgThrows(Action action)
        {
            try { action(); return false; }
            catch (fire.Package.Manager.PackageException) { return true; }
        }
    }

    Console.WriteLine(natFailures == 0 ? "Alle Native-Backend-Pruefungen bestanden." : $"FEHLER: {natFailures} Native-Backend-Pruefung(en) fehlgeschlagen.");
}

static class PackerNativeProbe
{
    [System.Runtime.InteropServices.DllImport("libfiretestnative")] private static extern int Nonexistent();
    public static int Call() => Nonexistent();
}

sealed class volatile_bool
{
    private volatile bool _value;
    public bool Value { get => _value; set => _value = value; }
}

/// <summary>Pictures of the screen of the window tests (ImageMagick's import, PPM with 8 bits).</summary>
static class ScreenPictures
{
    /// <summary>The PresentArea cases: the window's top left corner is the first red pixel; what is inside the reported area (35, 35) and outside it (135, 35).</summary>
    public static string Area(byte[] ppm)
    {
        int pos = 2, field = 0;
        var header = new int[3];
        while (field < 3)
        {
            while (pos < ppm.Length && char.IsWhiteSpace((char)ppm[pos])) pos++;
            if (pos < ppm.Length && ppm[pos] == '#') { while (pos < ppm.Length && ppm[pos] != '\n') pos++; continue; }
            int v = 0;
            while (pos < ppm.Length && char.IsDigit((char)ppm[pos])) v = v * 10 + (ppm[pos++] - '0');
            header[field++] = v;
        }
        pos++;
        int width = header[0], height = header[1], start = pos;
        if (header[2] != 255) return "kein 8-Bit-Bild (" + header[2] + ")";
        (int, int, int) Px(int x, int y) { int i = start + (y * width + x) * 3; return i + 2 < ppm.Length ? (ppm[i], ppm[i + 1], ppm[i + 2]) : (-1, -1, -1); }
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (Px(x, y) == (255, 0, 0)) return $"innen {Px(x + 35, y + 35)} aussen {Px(x + 135, y + 35)}";
        return "kein Fenster";
    }

    /// <summary>A window with a transparent backdrop over a black desktop: its top left corner is the first pixel of half red (128, 0, 0); what lies at (35, 35) and (135, 35).</summary>
    public static string Mixed(byte[] ppm)
    {
        int pos = 2, field = 0;
        var header = new int[3];
        while (field < 3)
        {
            while (pos < ppm.Length && char.IsWhiteSpace((char)ppm[pos])) pos++;
            if (pos < ppm.Length && ppm[pos] == '#') { while (pos < ppm.Length && ppm[pos] != '\n') pos++; continue; }
            int v = 0;
            while (pos < ppm.Length && char.IsDigit((char)ppm[pos])) v = v * 10 + (ppm[pos++] - '0');
            header[field++] = v;
        }
        pos++;
        int width = header[0], height = header[1], start = pos;
        (int, int, int) Px(int x, int y) { int i = start + (y * width + x) * 3; return i + 2 < ppm.Length ? (ppm[i], ppm[i + 1], ppm[i + 2]) : (-1, -1, -1); }
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (Px(x, y) == (128, 0, 0)) return $"{Px(x + 35, y + 35)} {Px(x + 135, y + 35)}";
        return "kein Fenster";
    }

    /// <summary>The PresentArea case of a display with two buffers (Wayland): three squares reported one after the other are all there, the red between them too.</summary>
    public static string Squares(byte[] ppm)
    {
        int pos = 2, field = 0;
        var header = new int[3];
        while (field < 3)
        {
            while (pos < ppm.Length && char.IsWhiteSpace((char)ppm[pos])) pos++;
            if (pos < ppm.Length && ppm[pos] == '#') { while (pos < ppm.Length && ppm[pos] != '\n') pos++; continue; }
            int v = 0;
            while (pos < ppm.Length && char.IsDigit((char)ppm[pos])) v = v * 10 + (ppm[pos++] - '0');
            header[field++] = v;
        }
        pos++;
        int width = header[0], height = header[1], start = pos;
        (int, int, int) Px(int x, int y) { int i = start + (y * width + x) * 3; return i + 2 < ppm.Length ? (ppm[i], ppm[i + 1], ppm[i + 2]) : (-1, -1, -1); }
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (Px(x, y) == (255, 0, 0)) return string.Join(" ", new[] { (35, 35), (135, 35), (85, 75), (100, 10), (10, 80) }.Select(p => Px(x + p.Item1, y + p.Item2)));
        return "kein Fenster";
    }
}
