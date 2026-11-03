using System.Diagnostics;
using System.Text;
using System.Text.Json;
using fire.Compiler;
using fire.Native;
using fire.Package.Manager;
using fire.Projects;
using fire.Runtime;
using fire.Values;

/// <summary>Linkage "static" (ROADMAP 9, docs/NATIVE_BACKEND.md "Getrennte Übersetzung"): every library module a static library in the user-wide cache - built once, then
/// reused; the program's output the same as with linkage "source" (and the VM); a library that changes is built again, the others not; a debug build stops in a library.
/// `FIRE_TESTS_ONLY=linkage`.</summary>
static class LinkageTests
{
    private static int _failures;
    private static string? _winePrefix;

    private static void Check(string title, bool ok, string? detail = null)
    {
        if (!ok) _failures++;
        Console.WriteLine(ok ? $"OK: Linkage: {title}" : $"FEHLER: Linkage: {title}" + (detail == null ? "" : $"\n{detail}"));
    }

    private static string Write(string root, string rel, string text)
    {
        string full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        return full;
    }

    private static string Vm(IReadOnlyList<string> sources, BuildPlan? plan = null)
    {
        var output = new StringBuilder();
        var session = RuntimeSession.Build(sources, null, args => { output.AppendLine(args.Length > 0 ? args[0].ToString() : ""); return Value.MakeUndefined(); }, plan: plan);
        session.Run();
        Value.SingleFloats = false; Value.Ints32 = false;
        return output.ToString().Replace("\r\n", "\n");
    }

    private static string Exec(string exe, string? launcher = null)
    {
        var psi = new ProcessStartInfo(launcher ?? exe, launcher != null ? $"\"{exe}\"" : "") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (launcher != null) { psi.Environment["WINEDEBUG"] = "-all"; psi.Environment["LANG"] = "C.UTF-8"; if (_winePrefix != null) psi.Environment["WINEPREFIX"] = _winePrefix; }
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(120000)) { try { p.Kill(true); } catch (Exception) { } return "(timeout)"; }
        return output.Replace("\r\n", "\n") + (p.ExitCode != 0 ? $"(exit {p.ExitCode}) {err.Result}" : "");
    }

    private sealed record Built(bool Ok, string Exe, string Log, double Seconds, long Size);

    private static Built Build(IReadOnlyList<string> sources, string linkage, string exe, ToolchainDef? toolchain = null, TargetProfile? target = null, BuildPlan? plan = null, VmExecutionMode? mode = null)
    {
        var config = new NativeConfig { Linkage = linkage };
        target ??= TargetProfile.Host;
        toolchain ??= config.ResolveToolchain(target, "gcc");
        var watch = Stopwatch.StartNew();
        var result = NativeBuilder.BuildSafe(sources, config, target, toolchain, exe, mode, plan: plan);
        return new Built(result.Ok, exe, result.Log, watch.Elapsed.TotalSeconds, File.Exists(exe) ? new FileInfo(exe).Length : 0);
    }

    private static List<string> ModuleLines(string log, string what) =>
        log.Split('\n').Where(l => l.StartsWith("module ", StringComparison.Ordinal) && l.Contains(what)).Select(l => l.Substring(7, l.IndexOf(": ", StringComparison.Ordinal) - 7)).ToList();

    public static void Run()
    {
        Console.WriteLine("=== Linkage \"static\": Bibliotheken im Cache ===");
        string root = Path.Combine(Path.GetTempPath(), "fire-linkage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string? before = Environment.GetEnvironmentVariable("FIRE_NATIVE_CACHE");
        Environment.SetEnvironmentVariable("FIRE_NATIVE_CACHE", Path.Combine(root, "cache"));
        try { RunIn(root); }
        catch (Exception ex) { Check("ohne Ausnahme", false, ex.ToString()); }
        finally
        {
            Environment.SetEnvironmentVariable("FIRE_NATIVE_CACHE", before);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
        Console.WriteLine(_failures == 0 ? "Alle Linkage-Pruefungen bestanden." : $"FEHLER: {_failures} Linkage-Pruefung(en) fehlgeschlagen.");
    }

    private const string Cli = """
        #import "json"
        #import "linq"
        #import "hash"
        #import "compression"
        class Item {
            string name
            int count
            construct(string name, int count) { this.name = name; this.count = count }
        }
        class Store {
            List<Item> items = new List<Item>()
            construct() { }
        }
        var s = new Store()
        for (var i = 0; i < 5; i++) { s.items.Add(new Item("item" + i, i * 3)) }
        string text = Json.Serialize(s)
        print(text)
        var back = Json.Deserialize<Store>(text)
        print(back.items.Count + " " + back.items[4].name)
        print("big " + back.items.Where(x => x.count > 5).ToList().Count)
        print(Hash.Sha256.HexOf(text))
        var packed = Compression.Deflate.Compress(text)
        print("deflate " + (packed.Length < text.Length) + " " + Compression.Deflate.DecompressText(packed).Length)
        try { Json.Deserialize<Store>("{") } catch (JsonException e) { print("bad: " + e.message) }
        """;

    private static void RunIn(string root)
    {
        // ---- a program with several packages: the same output as "source" and the VM; the second build takes every library from the cache ----
        string vm = Vm(new[] { Cli });
        var source = Build(new[] { Cli }, "source", Path.Combine(root, "cli-source"));
        Check("CLI: linkage source baut", source.Ok, source.Log);
        var first = Build(new[] { Cli }, "static", Path.Combine(root, "cli-static1"));
        string firstOut = first.Ok ? Exec(first.Exe) : "";
        Check("CLI: linkage static baut (jedes Modul eine Bibliothek: core, json, linq, hash, compression, ...)", first.Ok && ModuleLines(first.Log, "built in").Count >= 5 && !first.Log.Contains("not possible"), first.Log);
        Check("CLI: die Ausgabe ist die der VM und die von linkage source", first.Ok && firstOut == vm && (!source.Ok || Exec(source.Exe) == vm), $"VM:\n{vm}\nstatic:\n{firstOut}");
        var second = Build(new[] { Cli }, "static", Path.Combine(root, "cli-static2"));
        Check("CLI: der zweite Bau nimmt jede Bibliothek aus dem Cache (kein Modul wird neu erzeugt oder uebersetzt)",
            second.Ok && ModuleLines(second.Log, "new").Count == 0 && ModuleLines(second.Log, "built in").Count == 0 && ModuleLines(second.Log, "from the cache").Count >= 5 && Exec(second.Exe) == vm, second.Log);
        Console.WriteLine($"  (Messung CLI: source {source.Seconds:F1} s, {source.Size / 1024} KB; static erster Bau {first.Seconds:F1} s, zweiter {second.Seconds:F1} s, {second.Size / 1024} KB)");

        // ---- the interface files (ROADMAP 9.7) and the cache (9.2) ----
        var ifaces = Directory.EnumerateFiles(NativeLibraryCache.Root, "*" + NativeLibraryCache.InterfaceExtension, SearchOption.AllDirectories).ToList();
        bool ifaceOk = ifaces.Count > 0 && ifaces.All(f =>
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(f));
            var r = doc.RootElement;
            return r.GetProperty("Format").GetInt32() == CppGenerator.InterfaceFormat && r.GetProperty("Abi").GetString()!.Length == 64 && r.GetProperty("Key").GetString()!.Length > 0;
        });
        Check("Schnittstelle: jede Bibliothek hat eine Datei mit Formatversion, ABI-Hash und Schluessel", ifaceOk, string.Join("\n", ifaces));
        Check("Cache: die Bibliotheken liegen im Cache des Benutzers (FIRE_NATIVE_CACHE)", NativeLibraryCache.Size().Libraries >= 5, NativeLibraryCache.Root);

        // ---- another program with some of the same packages shares their libraries ----
        // (the same packages - the same switches of the program, ROADMAP 9.4: a program that needs other switches gets variants of its own)
        string other = Cli.Replace("print(text)", "print(\"other \" + text.Length)");
        var shared = Build(new[] { other }, "static", Path.Combine(root, "other"));
        string sharedOut = shared.Ok ? Exec(shared.Exe) : "", sharedVm = Vm(new[] { other });
        Check("Teilen: ein anderes Programm mit denselben Packages nimmt alle Bibliotheken aus dem Cache (der Text eines Moduls haengt nicht vom Programm ab)",
            shared.Ok && ModuleLines(shared.Log, "new").Count == 0 && ModuleLines(shared.Log, "from the cache").Contains("core") && ModuleLines(shared.Log, "from the cache").Contains("pkg:json") && sharedOut == sharedVm,
            $"{shared.Log}\nnativ:\n{sharedOut}\nVM:\n{sharedVm}");

        // ---- a project with two libraries: a change of the code of a library builds only it, a change of its interface also the libraries that need it ----
        string s = Path.Combine(root, "sln");
        string libFile = Write(s, "Lib/Lib.fireproj", """{ "name": "Lib", "type": "library" }""");
        string libSource = Write(s, "Lib/lib.script", "namespace Lib {\n    class Counter {\n        int n = 0\n        construct() { }\n        Add(int k) {\n            this.n = this.n + k\n        }\n        int Get() { return this.n }\n        static string Version() { return \"v1\" }\n    }\n}\n");
        Write(s, "Lib2/Lib2.fireproj", """{ "name": "Lib2", "type": "library", "references": [ { "project": "../Lib/Lib.fireproj" } ] }""");
        Write(s, "Lib2/lib2.script", "#import \"Lib\"\nnamespace Lib2 {\n    class Wrap {\n        static int Twice(int x) { var c = new Lib.Counter(); c.Add(x); c.Add(x); return c.Get() }\n    }\n}\n");
        Write(s, "App/App.fireproj", """{ "name": "App", "references": [ { "project": "../Lib2/Lib2.fireproj" }, { "project": "../Lib/Lib.fireproj" } ], "settings": { "linkage": "static" } }""");
        Write(s, "App/main.script", "#import \"Lib2\"\n#import \"Lib\"\nclass Mine : Lib.Counter { construct() : base() { } }\nvar m = new Mine()\nm.Add(5)\nprint(Lib2.Wrap.Twice(21) + \" \" + m.Get() + \" \" + Lib.Counter.Version())\n");
        string sln = Write(s, "All.firesln", """{ "name": "All", "projects": ["Lib/Lib.fireproj", "Lib2/Lib2.fireproj", "App/App.fireproj"], "startup": "App" }""");
        BuildPlan Plan() { var ws = Workspace.Open(sln); return BuildPlan.Create(ws, ws.FindByName("App")!); }
        var plan = Plan();
        Check("Projekt: die Einstellung linkage des Projekts", plan.Settings.Linkage == "static", plan.Settings.Linkage);
        var p1 = Build(plan.SourceTexts, plan.Settings.Linkage!, Path.Combine(root, "app1"), plan: plan);
        string appVm = Vm(plan.SourceTexts, plan);
        Check("Projekt: Programm mit zwei Bibliotheken des Projekts (eine Klasse des Programms erbt von einer der Bibliothek) wie die VM",
            p1.Ok && Exec(p1.Exe) == appVm && ModuleLines(p1.Log, "built in").Contains("Lib") && ModuleLines(p1.Log, "built in").Contains("Lib2"), $"VM: {appVm}\n{p1.Log}");
        File.WriteAllText(libSource, File.ReadAllText(libSource).Replace("\"v1\"", "\"v2\""));
        plan = Plan();
        var p2 = Build(plan.SourceTexts, "static", Path.Combine(root, "app2"), plan: plan);
        Check("Projekt: der Code einer Bibliothek aendert sich - nur sie wird neu gebaut (Lib2 und core aus dem Cache)",
            p2.Ok && Exec(p2.Exe).Contains("v2") && ModuleLines(p2.Log, "built in").SequenceEqual(new[] { "Lib" }) && ModuleLines(p2.Log, "from the cache").Contains("Lib2"), p2.Log);
        File.WriteAllText(libSource, File.ReadAllText(libSource).Replace("        int Get() { return this.n }", "        int Get() { return this.n }\n        int Twice() { return this.n * 2 }"));
        plan = Plan();
        var p3 = Build(plan.SourceTexts, "static", Path.Combine(root, "app3"), plan: plan);
        Check("Projekt: die Schnittstelle einer Bibliothek aendert sich - auch die Bibliothek, die sie braucht, wird neu gebaut",
            p3.Ok && ModuleLines(p3.Log, "built in").Contains("Lib") && ModuleLines(p3.Log, "built in").Contains("Lib2") && Exec(p3.Exe) == Vm(plan.SourceTexts, plan), p3.Log);

        // ---- a debug build: a breakpoint inside a static library ----
        DebugInLibrary(root, Plan(), libSource);

        // ---- a program that cannot be split (it declares a class of the prelude anew): linkage source, with a note ----
        const string own = "class Exception { string message; construct(string message = \"\") { this.message = message } }\ntry { throw new Exception(\"x\") } catch (e) { print(\"caught \" + e.message) }\n";
        var fallback = Build(new[] { own }, "static", Path.Combine(root, "own"));
        Check("Rueckfall: ein Programm, das sich nicht in Module teilen laesst, wird mit linkage source gebaut und sagt es", fallback.Ok && fallback.Log.Contains("not possible") && Exec(fallback.Exe) == Vm(new[] { own }), fallback.Log);

        // ---- other toolchains: TinyCC (tcc -ar), MinGW under Wine ----
        const string small = "#import \"json\"\nclass Q { int x = 4; string t = \"q\" }\nvar q = new Q()\nprint(Json.Serialize(q))\nprint(q.x * 10)\n";
        string smallVm = Vm(new[] { small });
        var config = new NativeConfig();
        if (ToolchainDetector.Find(ToolchainDef.BuiltIn["tcc"]) != null)
        {
            var tcc = Build(new[] { small }, "static", Path.Combine(root, "tcc"), ToolchainDef.BuiltIn["tcc"]);
            Check("TinyCC: linkage static (Bibliotheken mit tcc -ar)", tcc.Ok && Exec(tcc.Exe) == smallVm, tcc.Log);
        }
        else Console.WriteLine("(TinyCC: uebersprungen - tcc fehlt)");
        var mingw = new ToolchainDef { Kind = "gcc", Compiler = "x86_64-w64-mingw32-g++", CCompiler = "x86_64-w64-mingw32-gcc", Std = "c++17", CStd = "c11", Optimization = "-O2" };
        if (ToolchainDetector.Find(mingw) != null && HasProgram("wine"))
        {
            var win = Build(new[] { small }, "static", Path.Combine(root, "win.exe"), mingw, TargetProfile.Windows);
            _winePrefix = Path.Combine(root, "wineprefix");
            Check("MinGW: linkage static fuer Windows (x86_64-w64-mingw32-ar), unter Wine wie die VM", win.Ok && Exec(win.Exe, "wine") == smallVm, win.Log);
        }
        else Console.WriteLine("(MinGW/Wine: uebersprungen)");

        // ---- ember cache clean ----
        var (libraries, _) = NativeLibraryCache.Clean();
        Check("ember cache clean: leert den Cache", libraries > 0 && !Directory.Exists(NativeLibraryCache.Root));
        var again = Build(new[] { other }, "static", Path.Combine(root, "other2"));
        Check("Nach dem Leeren: die Bibliotheken werden neu gebaut", again.Ok && ModuleLines(again.Log, "built in").Contains("core"), again.Log);
    }

    private static bool HasProgram(string name) => ToolchainSetup.FindProgram(name) != null;

    /// <summary>A debug build with linkage "static": the debugger sets a breakpoint in a file of a library of the project and the program stops there (ROADMAP 9.9: the
    /// tables of each module, the sources of the program).</summary>
    private static void DebugInLibrary(string root, BuildPlan plan, string libSource)
    {
        var dbg = Build(plan.SourceTexts, "static", Path.Combine(root, "app-debug"), plan: plan, mode: VmExecutionMode.Debug);
        if (!dbg.Ok || dbg.Log.Contains("not possible") || !ModuleLines(dbg.Log, "").Contains("Lib")) { Check("Debug: Debug-Build mit statischen Bibliotheken", false, dbg.Log); return; }
        try
        {
            using var c = NativeDebugClient.Launch(dbg.Exe, root);
            int main = c.Hello.GetProperty("mainThread").GetInt32();
            int libIndex = c.Hello.GetProperty("sources").EnumerateArray()
                .Where(x => x.GetProperty("file").ValueKind == JsonValueKind.String && Path.GetFullPath(x.GetProperty("file").GetString()!) == Path.GetFullPath(libSource))
                .Select(x => x.GetProperty("index").GetInt32()).DefaultIfEmpty(-1).First();
            c.NextEvent("stopped");
            c.Request("breakpoints", new() { ["breakpoints"] = new[] { new[] { libIndex, 6 } } });
            c.Request("continue", new() { ["thread"] = main });
            var stop = c.NextEventExcept("thread", "threadExited");
            bool atLib = stop.GetProperty("event").GetString() == "stopped" && stop.GetProperty("source").GetInt32() == libIndex && stop.GetProperty("line").GetInt32() == 6;
            var trace = atLib ? c.Request("trace", new() { ["thread"] = main }) : default;
            bool named = atLib && trace.GetProperty("frames").EnumerateArray().First().GetProperty("function").GetString() == "Lib.Counter.Add";
            Check("Debug: ein Haltepunkt in einer statischen Bibliothek des Projekts haelt dort (Quelle der Bibliothek, Zeile, Funktion)", libIndex >= 0 && atLib && named, $"lib source {libIndex}: {stop} {(atLib ? trace.ToString() : "")}");
            c.Request("breakpoints", new() { ["breakpoints"] = Array.Empty<int[]>() });
            c.Request("continue", new() { ["thread"] = main, ["breakpoints"] = false });
            while (c.NextEvent().GetProperty("event").GetString() != "exited") c.Request("continue", new() { ["thread"] = main, ["breakpoints"] = false });
        }
        catch (Exception ex) { Check("Debug: ein Haltepunkt in einer statischen Bibliothek", false, ex.ToString()); }
    }
}
