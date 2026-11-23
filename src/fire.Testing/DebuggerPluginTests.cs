using fire.Debugging;

/// <summary>The plugin of the debug checks: LinkageTests "plugindebug" debugs it in `fire`, the VM in C (docs/spec/NATIVE_BACKEND.md "Plugins"): its sources, a breakpoint
/// by file in the plugin, a step into its methods.</summary>
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

    /// <summary>The plan for a debug target whose program loads the plugin Kit (PluginKitSource in `kitFile`) at line 2 of its own source and calls Work(5) at line 4:
    /// the sources event, Step Into, scopes, `this`, a breakpoint by file in the plugin, Step Out (a native debug build and `fire`, LinkageTests).</summary>
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
