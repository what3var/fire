using fire.Debugging;
using fire.Runtime;

/// <summary>Debugging a fire plugin in the VM (docs/spec/NATIVE_BACKEND.md "Plugins"): the plugin's bytecode carries its line tables, its sources get the next indexes of
/// the program when it is loaded (VM.PluginSourcesLinked), VmDebugTarget grows SourceFiles, a breakpoint by file in the plugin applies at once, a step goes into its
/// methods. Part of `FIRE_TESTS_ONLY=debugger`.</summary>
static partial class DebuggerTests
{
    internal const string PluginKitText = PluginKitSource;
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

    public static void PluginRun()
    {
        Console.WriteLine();
        Console.WriteLine("=== Debugger: Plugins in der VM ===");
        int failures = 0;
        void Check(bool ok, string title, string? detail = null)
        {
            if (!ok) failures++;
            Console.WriteLine(ok ? $"OK: Plugin-Debug (VM): {title}" : $"FEHLER: Plugin-Debug (VM): {title}{(detail != null ? "\n  " + detail : "")}");
        }

        string dir = Path.Combine(Path.GetTempPath(), "fire-dbgplugin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string kitFile = Path.Combine(dir, "kit.fire");
            File.WriteAllText(kitFile, PluginKitSource);
            string plugin = Path.Combine(dir, "kit.fireplugin");
            var compiled = fire.Compiler.NativeBuilder.WriteVmPlugin(plugin, "Kit", new[] { PluginKitSource }, new string?[] { kitFile }, VmExecutionMode.Debug);
            var read = PluginBytecode.Read(plugin);
            var work = read?.Program?.Program.Classes["Kit.Worker"].Methods["Work"][0].Chunk;
            Check(work != null && work.GetLocation(work.Code.Count - 1).Line == 8 && read!.Sources.Count == 1 && read.Sources[0][1] == kitFile,
                "die .fireplugin-Datei traegt die Zeilentabellen und die eigenen Quellen", $"{work?.GetLocation(work.Code.Count - 1)} {compiled.Program.Classes["Kit.Worker"].Methods["Work"][0].Chunk.GetLocation(0)} / {string.Join(",", read?.Sources.Select(s => string.Join(":", s)) ?? Array.Empty<string>())}");
            string host = "#import \"reflection\"\nvar lib = Library.Load(\"" + plugin.Replace('\\', '/') + "\")\nany w = Reflect.New(\"Kit.Worker\", [])\nvar r = w.Work(5)\nprint(\"result \" + r)\n";
            // (the same class in the program itself: where Step Into lands there - the plugin's method must land on the same line of its own source)
            (int, int) reference;
            using (var plain = VmDebugTarget.Build(new[] { PluginKitSource + "\nany w = new Kit.Worker()\nvar r = w.Work(5)\n" }, VmExecutionMode.Debug))
            {
                int u = plain.FirstUserSourceIndex;
                plain.MainThread.RunUntilBreakpoint(new HashSet<(int, int)> { (u, 13) }, () => false);
                plain.MainThread.StepInto();
                reference = plain.MainThread.CurrentLocation;
                plain.MainThread.RunUntilEnd(() => false);
            }
            VM.ResetTerminateForTests();
            using var target = VmDebugTarget.Build(new[] { host }, VmExecutionMode.Debug);
            var output = new List<string>();
            target.Output += (_, text) => { lock (output) output.Add(text); };
            int user = target.FirstUserSourceIndex;
            PluginPlan(target, kitFile, user, reference, Check);
            target.MainThread.RunUntilEnd(() => false);
            lock (output) Check(output.Contains("result 16"), "das Programm endet mit dem Ergebnis", string.Join(" | ", output));
        }
        catch (Exception ex) { Check(false, "ohne Ausnahme", ex.ToString()); }
        finally { VM.ResetTerminateForTests(); try { Directory.Delete(dir, true); } catch (IOException) { } }
        Console.WriteLine(failures == 0 ? "Alle Plugin-Debug-Pruefungen (VM) bestanden." : $"FEHLER: {failures} Plugin-Debug-Pruefung(en) (VM) fehlgeschlagen.");
    }

    /// <summary>The plan for a debug target whose program loads the plugin Kit (PluginKitSource in `kitFile`) at line 2 of its own source and calls Work(5) at line 4:
    /// the sources event, Step Into, scopes, `this`, a breakpoint by file in the plugin, Step Out (the VM and a native debug build, LinkageTests).</summary>
    internal static void PluginPlan(fire.Debugging.IDebugTarget target, string kitFile, int user, (int, int)? reference, Action<bool, string, string?> Check)
    {
        int added = 0;
        target.SourcesAdded += () => Interlocked.Increment(ref added);
        int programSources = target.SourceFiles.Count;
        target.SetFileBreakpoints(new[] { (kitFile, 7) });
        var main = target.MainThread;
        bool ok = main.RunUntilBreakpoint(new HashSet<(int, int)> { (user, 4) }, () => false);
        int kit = Enumerable.Range(0, target.SourceFiles.Count).FirstOrDefault(i => target.SourceFiles[i] is { } f && Path.GetFullPath(f) == Path.GetFullPath(kitFile), -1);
        Check(ok && main.CurrentLocation == (user, 4) && added == 1 && kit >= programSources && target.SourceFiles.Count == programSources + 1,
            "Library.Load: die Quelle des Plugins bekommt den naechsten Index (SourcesAdded), der Haltepunkt im Programm haelt",
            $"{ok} {main.CurrentLocation} added {added}, kit {kit}, sources {programSources} -> {target.SourceFiles.Count}");
        ok = main.StepInto();
        if (reference is { } r) Check(r.Item2 == 0 ? main.CurrentLocation == (0, 0) : main.CurrentLocation == (kit, r.Item2), "Step Into landet wie im Programm selbst", $"{main.CurrentLocation} / {r}");
        if (main.CurrentLocation.Line == 0) ok = main.StepInto();
        var scopes = ok ? main.Scopes() : Array.Empty<DebugScope>();
        var x = scopes.SelectMany(s => s.Variables).FirstOrDefault(v => v.Name == "x");
        var factor = main.This?.Children().FirstOrDefault(v => v.Name == "factor");
        var trace = main.CurrentTrace();
        Check(ok && main.CurrentLocation == (kit, 6) && x?.Display == "5" && factor?.Display == "3" && trace.Count == 2 && trace[1] == (user, 4),
            "Step Into geht in die Methode des Plugins (seine Quelle und Zeile), Scopes und this (Feld factor), der Aufrufer im Trace",
            $"{main.CurrentLocation} x={x?.Display} factor={factor?.Display} trace {string.Join(" ", trace)}");
        ok = main.RunUntilBreakpoint(new HashSet<(int, int)> { (user, 4) }, () => false);
        var y = ok ? main.Scopes().SelectMany(s => s.Variables).FirstOrDefault(v => v.Name == "y") : null;
        Check(ok && main.CurrentLocation == (kit, 7) && y?.Display == "15", "ein Haltepunkt nach Datei im Plugin haelt (Zeile 7, y = 15)", $"{ok} {main.CurrentLocation} y={y?.Display}");
        main.StepOut();
        Check(main.CurrentLocation.SourceIndex == user, "Step Out kommt ins Programm zurueck", $"{main.CurrentLocation}");
    }
}
