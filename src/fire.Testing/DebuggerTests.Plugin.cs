using System.Collections.Concurrent;
using fire.Debugging;
using fire.Tooling;

/// <summary>Debugging across modules over the debug protocol (docs/spec/NATIVE_DEBUG_PROTOCOL.md "sources"), as LinkageTests (PluginDebug, DebugInLibrary) and
/// DebuggerPluginTests checked them before M3 - now with forge-c building the programs: a debug build of a program with linkage shared loads the debug build of the
/// plugin Kit, natively and in `fire` (the VM in C with the bytecode of the plugin); the event `sources` gives its file the next index, the breakpoints by file reach
/// it, Step Into goes into its method (scopes, `this` with the field names of the plugin's own table), Step Out comes back. And a breakpoint in a static library of
/// a project (linkage static, debug: the tables of each module, the sources of the program). Part of the group `debugger`.</summary>
static partial class DebuggerTests
{
    private const string PluginKitSource = """
        namespace Kit {
            class Worker {
                int factor = 3
                construct() { }
                int Work(int x) {
                    var y = x * factor
                    var z = y + 1
                    return z
                }
            }
        }
        """;

    /// <summary>The plan for a debug target whose program loads the plugin Kit (<see cref="PluginKitSource"/> in <paramref name="kitFile"/>) at line 2 of its own
    /// source and calls Work(5) at line 4: the sources event, Step Into, scopes, `this`, a breakpoint by file in the plugin, Step Out.</summary>
    private static void PluginPlan(IDebugTarget target, string kitFile, int user, string engine)
    {
        int added = 0;
        target.SourcesAdded += () => Interlocked.Increment(ref added);
        int programSources = target.SourceFiles.Count;
        target.SetFileBreakpoints(new[] { (kitFile, 7) });
        var main = target.MainThread;
        bool ok = main.RunUntilBreakpoint(new HashSet<(int, int)> { (user, 4) }, () => false);
        int kit = Enumerable.Range(0, target.SourceFiles.Count).FirstOrDefault(i => target.SourceFiles[i] is { } f && Path.GetFullPath(f) == Path.GetFullPath(kitFile), -1);
        Check(ok && main.CurrentLocation == (user, 4) && added == 1 && kit >= programSources && target.SourceFiles.Count == programSources + 1,
            $"Plugin-Debug ({engine}): Library.Load - die Quelle des Plugins bekommt den naechsten Index (SourcesAdded), der Haltepunkt im Programm haelt",
            $"{ok} {main.CurrentLocation} added {added}, kit {kit}, sources {programSources} -> {target.SourceFiles.Count}");
        ok = main.StepInto();
        if (main.CurrentLocation.Line == 0) ok = main.StepInto();
        var scopes = ok ? main.Scopes() : Array.Empty<DebugScope>();
        var x = scopes.SelectMany(s => s.Variables).FirstOrDefault(v => v.Name == "x");
        var factor = main.This?.Children().FirstOrDefault(v => v.Name == "factor");
        var trace = main.CurrentTrace();
        Check(ok && main.CurrentLocation == (kit, 6) && x?.Display == "5" && factor?.Display == "3" && trace.Count == 2 && trace[1] == (user, 4),
            $"Plugin-Debug ({engine}): Step Into geht in die Methode des Plugins (seine Quelle und Zeile), Scopes und this (Feld factor), der Aufrufer im Trace",
            $"{main.CurrentLocation} x={x?.Display} factor={factor?.Display} trace {string.Join(" ", trace)}");
        ok = main.RunUntilBreakpoint(new HashSet<(int, int)> { (user, 4) }, () => false);
        var y = ok ? main.Scopes().SelectMany(s => s.Variables).FirstOrDefault(v => v.Name == "y") : null;
        Check(ok && main.CurrentLocation == (kit, 7) && y?.Display == "15", $"Plugin-Debug ({engine}): ein Haltepunkt nach Datei im Plugin haelt (Zeile 7, y = 15)",
            $"{ok} {main.CurrentLocation} y={y?.Display}");
        main.StepOut();
        Check(main.CurrentLocation.SourceIndex == user, $"Plugin-Debug ({engine}): Step Out kommt ins Programm zurueck", $"{main.CurrentLocation}");
    }

    private static string WriteFile(string root, string rel, string text)
    {
        string full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        return full;
    }

    /// <summary>The plugin Kit debugged natively and in `fire`; a breakpoint in a static library. Skipped (with the reason) without a C compiler for the debug builds.</summary>
    public static void ModuleRun()
    {
        Console.WriteLine();
        Console.WriteLine("=== Debuggen ueber Module (Plugin, statische Bibliothek) ===");
        if (!OperatingSystem.IsLinux()) { Console.WriteLine("(uebersprungen: die Plugins und Bibliotheken der Pruefung sind fuer Linux gebaut)"); return; }
        if (ToolchainDetector.Find(new NativeConfig().ResolveToolchain(TargetProfile.Host, "gcc")) == null) { Console.WriteLine("(uebersprungen: kein gcc)"); return; }
        string root = Path.Combine(Path.GetTempPath(), "fire-dbg-modules-" + Guid.NewGuid().ToString("N"));
        string cache = Path.Combine(root, "cache");
        var env = new Dictionary<string, string> { ["FIRE_NATIVE_CACHE"] = cache };
        try
        {
            PluginDebug(root, env);
            DebugInLibrary(root, env);
            ProfileAndPause(root, env);
        }
        catch (Exception ex) { Check(false, "Debuggen ueber Module: ohne Ausnahme", ex.ToString()); }
        finally { try { Directory.Delete(root, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }

    private static (int Code, string Log) ForgeIn(string dir, IReadOnlyDictionary<string, string> env, params string[] args)
    {
        foreach (var (k, v) in env) Environment.SetEnvironmentVariable(k, v);
        try { var (code, output, errors) = Forge.Run(args, dir); return (code, output + errors); }
        finally { foreach (var k in env.Keys) Environment.SetEnvironmentVariable(k, null); }
    }

    private static void PluginDebug(string root, IReadOnlyDictionary<string, string> env)
    {
        string s = Path.Combine(root, "plugdbg");
        string kitFile = WriteFile(s, "Kit/kit.fire", PluginKitSource);
        WriteFile(s, "Kit/Kit.fireproj", """{ "name": "Kit", "settings": { "linkage": "plugin" } }""");
        WriteFile(s, "Host/Host.fireproj", """{ "name": "Host", "settings": { "linkage": "shared" } }""");
        WriteFile(s, "Host/main.fire", "#import \"reflection\"\nvar lib = Library.Load(Environment.Get(\"FIRE_TEST_PLUGIN\"))\nany w = Reflect.New(\"Kit.Worker\", [])\nvar r = w.Work(5)\nprint(\"result \" + r)\n");
        WriteFile(s, "All.firesln", """{ "name": "All", "projects": ["Kit/Kit.fireproj", "Host/Host.fireproj"], "startup": "Host" }""");
        string outDir = Path.Combine(root, "plugdbg-out");
        Directory.CreateDirectory(outDir);
        string host = Path.Combine(outDir, "host");
        var builtHost = ForgeIn(s, env, "build", "All.firesln", "-p", "Host", "--engine", "native", "--toolchain", "gcc", "-m", "debug", "--debug-info", "-o", host);
        var builtKit = builtHost.Code == 0
            ? ForgeIn(s, env, "build", "All.firesln", "-p", "Kit", "--engine", "native", "--toolchain", "gcc", "-m", "debug", "--debug-info", "--host", host, "-o", Path.Combine(outDir, "kit"))
            : (1, "");
        if (builtHost.Code != 0 || builtKit.Item1 != 0) { Check(false, "Plugin-Debug: Debug-Build von Programm und Plugin (forge)", builtHost.Log + "\n" + builtKit.Item2); return; }
        Check(builtKit.Item2.Contains("-O0") && builtKit.Item2.Contains("-g"), "Plugin-Debug: das Plugin ist ein Debug-Build (-O0 -g)", builtKit.Item2);
        // natively: the debug build of the program loads the debug build of the plugin
        try
        {
            var output = new ConcurrentQueue<string>();
            using var target = NativeLaunch.LaunchDebug(host, Array.Empty<string>(), root, TimeSpan.FromSeconds(30),
                new Dictionary<string, string> { ["FIRE_TEST_PLUGIN"] = Path.Combine(outDir, "kit.so") });
            target.Output += (_, line) => output.Enqueue(line);
            int user = Enumerable.Range(0, target.SourceFiles.Count).First(i => target.SourceFiles[i] is { } f && Path.GetFileName(f) == "main.fire");
            PluginPlan(target, kitFile, user, "nativ");
            target.MainThread.RunUntilEnd(() => false);
            Check(Soon(() => output.Contains("result 16")), "Plugin-Debug (nativ): das Programm endet mit dem Ergebnis", string.Join(" | ", output));
        }
        catch (NativeDebugException ex) { Check(false, "Plugin-Debug (nativ): ohne Ausnahme", ex.Message); }
        // the same in `fire`: the program file of the debug build (with its sources), the bytecode of the plugin (its line tables, its sources)
        try
        {
            string fbc = Path.Combine(outDir, "host.fbc");
            var compiled = ForgeIn(s, env, "bytecode", "All.firesln", "-p", "Host", "-m", "DEBUG", "-o", fbc);
            if (compiled.Code != 0) { Check(false, "Plugin-Debug (VM): forge bytecode", compiled.Log); return; }
            var output = new ConcurrentQueue<string>();
            using var target = CVmLaunch.LaunchDebug(Forge.LocateVm(), fbc, Array.Empty<string>(), root, TimeSpan.FromSeconds(30),
                CVmLaunch.Environment(Array.Empty<string>(), new Dictionary<string, string> { ["FIRE_TEST_PLUGIN"] = Path.Combine(outDir, "kit.fireplugin") }));
            target.Output += (_, line) => output.Enqueue(line);
            Check(target.Engine == "vm", "Plugin-Debug (VM): der Debugger spricht mit fire (engine vm)", target.Engine);
            int user = Enumerable.Range(0, target.SourceFiles.Count).First(i => target.SourceFiles[i] is { } f && Path.GetFileName(f) == "main.fire");
            PluginPlan(target, kitFile, user, "VM");
            target.MainThread.RunUntilEnd(() => false);
            Check(Soon(() => output.Contains("result 16")), "Plugin-Debug (VM): das Programm endet mit dem Ergebnis", string.Join(" | ", output));
        }
        catch (NativeDebugException ex) { Check(false, "Plugin-Debug (VM): ohne Ausnahme", ex.Message); }
    }

    /// <summary>The agent of a native debug build (NativeDebugTests before M3): `profile` counts the lines (the body of a loop 100 times) and sends them before
    /// `exited`; `pause` holds an endless loop, `terminate` (the end of the session) ends the program.</summary>
    private static void ProfileAndPause(string root, IReadOnlyDictionary<string, string> env)
    {
        string dir = Path.Combine(root, "profile");
        string counted = WriteFile(dir, "counted.fire", "var n = 0\nfor (var i = 0; i < 100; i++) {\n    n = n + i\n}\nprint(\"sum \" + n)\n");
        string endless = WriteFile(dir, "endless.fire", "var spin = 0\nwhile (true) {\n    spin = spin + 1\n}\n");
        foreach (var (file, exe) in new[] { (counted, "counted"), (endless, "endless") })
        {
            var built = ForgeIn(dir, env, "build", file, "--engine", "native", "--toolchain", "gcc", "-m", "debug", "--debug-info", "-o", Path.Combine(dir, exe));
            if (built.Code != 0) { Check(false, $"Profil/Pause: Debug-Build von {exe}", built.Log); return; }
        }
        try
        {
            var output = new ConcurrentQueue<string>();
            using var target = NativeLaunch.LaunchDebug(Path.Combine(dir, "counted"), Array.Empty<string>(), dir, TimeSpan.FromSeconds(30));
            target.Output += (_, line) => output.Enqueue(line);
            int user = target.FirstUserSourceIndex;
            bool on = target.StartProfile();
            target.MainThread.RunUntilEnd(() => false);
            Soon(() => target.Profile != null && output.Contains("sum 4950"));
            var body = target.Profile?.FirstOrDefault(p => p.Source == user && p.Line == 3);
            Check(on && body is { Hits: 100 } && output.Contains("sum 4950"),
                "Profil: profile zaehlt die Zeilen (der Schleifenrumpf 100-mal) und sendet sie vor exited",
                $"{on} {(target.Profile == null ? "no profile" : string.Join(" ", target.Profile.Where(p => p.Source == user).Select(p => $"{p.Line}:{p.Hits}")))} | {string.Join(" | ", output)}");
        }
        catch (NativeDebugException ex) { Check(false, "Profil: ohne Ausnahme", ex.Message); }
        try
        {
            System.Diagnostics.Process? process;
            using (var target = NativeLaunch.LaunchDebug(Path.Combine(dir, "endless"), Array.Empty<string>(), dir, TimeSpan.FromSeconds(30)))
            {
                process = target.Process;
                var asked = System.Diagnostics.Stopwatch.StartNew();
                bool paused = target.MainThread.RunUntilEnd(() => asked.ElapsedMilliseconds > 500);
                var at = target.MainThread.CurrentLocation;
                var spin = paused ? target.MainThread.Globals().FirstOrDefault(v => v.Name == "spin") : null;
                Check(paused && at.SourceIndex == target.FirstUserSourceIndex && at.Line is 2 or 3 && spin != null && long.TryParse(spin.Display, out long n) && n > 0,
                    "Pause: pause haelt eine Endlosschleife an (in der Schleife, spin gezaehlt)", $"{paused} {at} spin={spin?.Display}");
            }
            bool ended = process == null || process.WaitForExit(10000);
            Check(ended, "Pause: das Ende der Sitzung (terminate) beendet das Programm", process == null ? "no process" : $"exited {process.HasExited}");
        }
        catch (NativeDebugException ex) { Check(false, "Pause: ohne Ausnahme", ex.Message); }
    }

    /// <summary>A debug build with linkage static: a breakpoint in a file of a library of the project holds there (the source of the library, its line), the caller
    /// is in the trace, the program then ends.</summary>
    private static void DebugInLibrary(string root, IReadOnlyDictionary<string, string> env)
    {
        string s = Path.Combine(root, "libdbg");
        WriteFile(s, "Lib/Lib.fireproj", """{ "name": "Lib", "type": "library" }""");
        string libSource = WriteFile(s, "Lib/lib.fire", "namespace Lib {\n    class Counter {\n        int n = 0\n        construct() { }\n        Add(int k) {\n            this.n = this.n + k\n        }\n        int Get() { return this.n }\n    }\n}\n");
        WriteFile(s, "App/App.fireproj", """{ "name": "App", "references": [ { "project": "../Lib/Lib.fireproj" } ], "settings": { "linkage": "static" } }""");
        WriteFile(s, "App/main.fire", "#import \"Lib\"\nvar c = new Lib.Counter()\nc.Add(5)\nprint(\"got \" + c.Get())\n");
        WriteFile(s, "All.firesln", """{ "name": "All", "projects": ["Lib/Lib.fireproj", "App/App.fireproj"], "startup": "App" }""");
        string exe = Path.Combine(root, "libdbg-out", "app");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        var built = ForgeIn(s, env, "build", "All.firesln", "-p", "App", "--engine", "native", "--toolchain", "gcc", "-m", "debug", "--debug-info", "-o", exe);
        if (built.Code != 0 || built.Log.Contains("not possible") || !built.Log.Contains("module Lib:"))
        {
            Check(false, "Debug: Debug-Build mit statischen Bibliotheken (forge, linkage static)", built.Log);
            return;
        }
        try
        {
            var output = new ConcurrentQueue<string>();
            using var target = NativeLaunch.LaunchDebug(exe, Array.Empty<string>(), root, TimeSpan.FromSeconds(30));
            target.Output += (_, line) => output.Enqueue(line);
            int lib = Enumerable.Range(0, target.SourceFiles.Count).FirstOrDefault(i => target.SourceFiles[i] is { } f && Path.GetFullPath(f) == Path.GetFullPath(libSource), -1);
            int user = Enumerable.Range(0, target.SourceFiles.Count).FirstOrDefault(i => target.SourceFiles[i] is { } f && Path.GetFileName(f) == "main.fire", -1);
            var main = target.MainThread;
            bool hit = lib >= 0 && main.RunUntilBreakpoint(new HashSet<(int, int)> { (lib, 6) }, () => false);
            var trace = hit ? main.CurrentTrace() : Array.Empty<(int SourceIndex, int Line)>();
            var k = hit ? main.Scopes().SelectMany(sc => sc.Variables).FirstOrDefault(v => v.Name == "k") : null;
            Check(hit && main.CurrentLocation == (lib, 6) && trace.Count == 2 && trace[1] == (user, 3) && k?.Display == "5",
                "Debug: ein Haltepunkt in einer statischen Bibliothek des Projekts haelt dort (Quelle der Bibliothek, Zeile, der Aufrufer im Programm, k = 5)",
                $"lib source {lib}, user {user}: {hit} {main.CurrentLocation} trace {string.Join(" ", trace)} k={k?.Display} | {string.Join(",", target.SourceFiles)}");
            main.RunUntilEnd(() => false);
            Check(Soon(() => output.Contains("got 5")), "Debug: das Programm mit der statischen Bibliothek endet mit seiner Ausgabe", string.Join(" | ", output));
        }
        catch (NativeDebugException ex) { Check(false, "Debug: ein Haltepunkt in einer statischen Bibliothek", ex.Message); }
    }
}
