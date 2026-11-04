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
        Value.SingleFloats = false;
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

    private static Built Build(IReadOnlyList<string> sources, string linkage, string exe, ToolchainDef? toolchain = null, TargetProfile? target = null, BuildPlan? plan = null, VmExecutionMode? mode = null,
        StrippingLevel? stripping = null)
    {
        var config = new NativeConfig { Linkage = linkage, Stripping = stripping };
        target ??= TargetProfile.Host;
        toolchain ??= config.ResolveToolchain(target, "gcc");
        var watch = Stopwatch.StartNew();
        var result = NativeBuilder.BuildSafe(sources, config, target, toolchain, exe, mode, plan: plan);
        return new Built(result.Ok, exe, result.Log, watch.Elapsed.TotalSeconds, File.Exists(exe) ? new FileInfo(exe).Length : 0);
    }

    /// <summary>The processes of this machine whose command line contains <paramref name="text"/> (Linux: /proc; elsewhere none are seen).</summary>
    private static List<string> ProcessesWith(string text)
    {
        var found = new List<string>();
        if (!OperatingSystem.IsLinux()) return found;
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out int pid) || pid == Environment.ProcessId) continue;
            try
            {
                string cmd = File.ReadAllText(Path.Combine(dir, "cmdline")).Replace('\0', ' ');
                string stat = File.ReadAllText(Path.Combine(dir, "stat"));
                if (cmd.Contains(text, StringComparison.Ordinal) && !stat.Contains(") Z ", StringComparison.Ordinal)) found.Add($"{pid}: {cmd.Substring(0, Math.Min(cmd.Length, 160))}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return found;
    }

    /// <summary>spark's build window (docs/NATIVE_BACKEND.md "Fortschritt und Abbrechen"): a build with linkage "static" and an empty cache is cancelled while gcc compiles
    /// its library modules side by side - gcc (with cc1, as) ends within seconds, the build says it was cancelled, the cache has no half library (no temporary file, every
    /// library with its interface) and no program is left; the next build into the same cache works, its progress arrives in order (compile fire in steps, generate C,
    /// compile C counting 0..N modules - N the modules that are new -, link) and its log has the commands as they ran.</summary>
    private static void ProgressAndCancel(string root)
    {
        string? before = Environment.GetEnvironmentVariable("FIRE_NATIVE_CACHE");
        string cache = Path.Combine(root, "cancel-cache-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Environment.SetEnvironmentVariable("FIRE_NATIVE_CACHE", cache);
        try
        {
            var config = new NativeConfig { Linkage = "static" };
            var target = TargetProfile.Host;
            var toolchain = config.ResolveToolchain(target, "gcc");
            string exe = Path.Combine(root, "cancel-cli");

            // ---- cancelled while gcc compiles the library modules ----
            using var cancel = new CancellationTokenSource();
            using var compiling = new ManualResetEventSlim();
            var cancelMonitor = new BuildMonitor(cancel.Token, p => { if (p.Phase == BuildPhase.CompileC && p.Done == 0) compiling.Set(); });
            var build = Task.Run(() => NativeBuilder.BuildSafe(new[] { Cli }, config, target, toolchain, exe, monitor: cancelMonitor));
            bool reached = compiling.Wait(TimeSpan.FromMinutes(3));
            Thread.Sleep(800);   // (gcc runs: cc1 is at work on the modules)
            var running = ProcessesWith(cache);
            var watch = Stopwatch.StartNew();
            cancel.Cancel();
            bool ended = build.Wait(TimeSpan.FromSeconds(60));
            double seconds = watch.Elapsed.TotalSeconds;
            var cancelled = ended ? build.Result : null;
            // (a process that is ending may still be listed for a moment)
            var left = ProcessesWith(cache);
            for (int i = 0; i < 20 && left.Count > 0; i++) { Thread.Sleep(100); left = ProcessesWith(cache); }
            Console.WriteLine($"  (abgebrochen: {running.Count} Prozesse des Baus liefen, nach {seconds:0.0} s war der Bau zu Ende)");
            Check("Abbrechen: der Bau erreicht die Phase \"C uebersetzen\" (gcc laeuft fuer die Module)", reached && (!OperatingSystem.IsLinux() || running.Count > 0), string.Join("\n", running));
            Check("Abbrechen: der Bau endet binnen weniger Sekunden als abgebrochen, gcc/cc1 laufen nicht mehr", ended && cancelled is { Cancelled: true, Ok: false } && seconds < 6 && left.Count == 0,
                $"{seconds:0.0} s, {cancelled?.Log}\n{string.Join("\n", left)}");
            var files = Directory.Exists(cache) ? Directory.EnumerateFiles(cache, "*", SearchOption.AllDirectories).ToList() : new List<string>();
            var temporaries = files.Where(f => f.EndsWith(".tmp", StringComparison.Ordinal) || System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileNameWithoutExtension(f), "^[0-9a-f]{32}$")).ToList();
            var orphans = files.Where(f => f.EndsWith(".a", StringComparison.Ordinal) && !File.Exists(Path.Combine(Path.GetDirectoryName(f)!, Path.GetFileNameWithoutExtension(f).Substring("fire_".Length) + NativeLibraryCache.InterfaceExtension))).ToList();
            Check("Abbrechen: keine halbe Bibliothek im Cache (keine temporaere Datei, jede Bibliothek mit ihrer Schnittstelle) und kein Programm", temporaries.Count == 0 && orphans.Count == 0 && !File.Exists(exe),
                string.Join("\n", temporaries.Concat(orphans)));

            // ---- the next build into the same cache: works; its progress in order ----
            var events = new List<BuildProgress>();
            var lines = new List<string>();
            var monitor = new BuildMonitor(default, p => { lock (events) events.Add(p); }, l => { lock (lines) lines.Add(l); });
            var again = NativeBuilder.BuildSafe(new[] { Cli }, config, target, toolchain, exe, monitor: monitor);
            string vm = Vm(new[] { Cli });
            Check("Abbrechen: der naechste Bau in denselben Cache gelingt, das Programm gibt aus, was die VM ausgibt", again.Ok && !again.Cancelled && Exec(exe) == vm, again.Log);
            var phases = events.Select(e => e.Phase).ToList();
            var modules = events.Where(e => e.Phase == BuildPhase.CompileC).ToList();
            int fresh = ModuleLines(again.Log, "built in").Count;
            Check("Fortschritt: die Phasen kommen in Reihenfolge (fire uebersetzen, C erzeugen, C uebersetzen, linken)",
                phases.Count > 0 && phases.Zip(phases.Skip(1)).All(z => z.First <= z.Second) && phases.Contains(BuildPhase.CompileFire) && phases.Contains(BuildPhase.GenerateC) && phases[^1] == BuildPhase.Link,
                string.Join(" | ", events.Select(e => $"{e.Phase} {e.Done}/{e.Total} {e.Text}")));
            Check("Fortschritt: \"C uebersetzen\" zaehlt die neuen Module von 0 bis N (der Reihe nach, N = die Module, die der Bau uebersetzt)",
                fresh > 0 && modules.Count == fresh + 1 && modules.Select(m => m.Done).SequenceEqual(Enumerable.Range(0, fresh + 1)) && modules.All(m => m.Total == fresh),
                $"{fresh} new: " + string.Join(" | ", modules.Select(e => $"{e.Done}/{e.Total}")));
            Check("Fortschritt: das Protokoll hat die Befehle, wie sie liefen (gcc fuer jedes neue Modul und fuer das Programm)",
                lines.Count(l => l.Contains(" -c ", StringComparison.Ordinal) && l.Contains(cache, StringComparison.Ordinal)) == fresh && lines.Any(l => l.Contains(exe, StringComparison.Ordinal)),
                string.Join("\n", lines.Take(30)));
        }
        finally { Environment.SetEnvironmentVariable("FIRE_NATIVE_CACHE", before); }
    }

    private static List<string> ModuleLines(string log, string what) =>
        log.Split('\n').Where(l => l.StartsWith("module ", StringComparison.Ordinal) && l.Contains(what)).Select(l => l.Substring(7, l.IndexOf(": ", StringComparison.Ordinal) - 7)).ToList();

    /// <summary>The library of graphics and windows that the VM builds on Windows (PackageLibrary.EnsureGraphics: wrapper.c with w64devkit's gcc) - built here for Windows with
    /// MinGW and the flags of that build; it has to compile without errors (a failed build left spark's VM without windows). `FIRE_TESTS_FILTER=vmgraphics`.</summary>
    private static void VmGraphicsForWindows(string root)
    {
        string? mingw = HasProgram("x86_64-w64-mingw32-gcc") ? "x86_64-w64-mingw32-gcc" : null;
        var graphics = PackageStore.Default.FindImport("graphics");
        var windows = PackageStore.Default.FindImport("windows");
        if (mingw == null || graphics == null || windows == null) { Console.WriteLine("(VM-Bibliothek von graphics und windows fuer Windows: uebersprungen - MinGW oder die Pakete fehlen)"); return; }
        string work = Path.Combine(root, "vmgfx-win");
        Directory.CreateDirectory(work);
        NativeRuntimeFiles.WriteTo(work, TargetProfile.Windows.Native.Platform);
        string c = Path.Combine(work, "wrapper.c");
        File.WriteAllText(c, PackageLibrary.GenerateGraphicsWrapper(graphics, windows, TargetProfile.Windows));
        string links = string.Concat(File.ReadLines(c).Where(l => l.StartsWith("// fire-link: ", StringComparison.Ordinal)).Select(l => " -l" + l.Substring("// fire-link: ".Length).Trim()));
        string dll = Path.Combine(work, "fire_graphics.dll");
        // (the command line of EnsureGraphics on Windows, NativeBuilder.CompilerCommand)
        var psi = new ProcessStartInfo(mingw, $"-std=c11 -O2 -shared -static-libgcc -static-libstdc++ \"{c}\" -I\"{work}\" -I\"{Path.Combine(work, "abi")}\" -o \"{dll}\"{links}")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = work };
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        string log = output + err.Result;
        Check("VM: die Bibliothek von graphics und windows fuer Windows (wrapper.c wie EnsureGraphics, MinGW gcc -std=c11 -O2 -shared) baut ohne Fehler",
            p.ExitCode == 0 && File.Exists(dll), $"{links}\n{(log.Length > 8000 ? log.Substring(0, 8000) : log)}");
        if (Environment.GetEnvironmentVariable("FIRE_TEST_KEEP_VMGFX") is { Length: > 0 } keep && File.Exists(dll)) File.Copy(dll, keep, true);

        // the same library for this machine, linked with --no-undefined: a symbol that only a program has (the class table: className) is found here too, not first on Windows
        if (OperatingSystem.IsLinux() && HasProgram("gcc"))
        {
            string hostWork = Path.Combine(root, "vmgfx-host");
            Directory.CreateDirectory(hostWork);
            NativeRuntimeFiles.WriteTo(hostWork, TargetProfile.Host.Native.Platform);
            string hc = Path.Combine(hostWork, "wrapper.c");
            File.WriteAllText(hc, PackageLibrary.GenerateGraphicsWrapper(graphics, windows));
            string hostLinks = string.Concat(File.ReadLines(hc).Where(l => l.StartsWith("// fire-link: ", StringComparison.Ordinal)).Select(l => " -l" + l.Substring("// fire-link: ".Length).Trim()));
            var hp = Process.Start(new ProcessStartInfo("gcc", $"-std=c11 -O0 -shared -fPIC -pthread -Wl,--no-undefined \"{hc}\" -I\"{hostWork}\" -I\"{Path.Combine(hostWork, "abi")}\" -o \"{Path.Combine(hostWork, "libfire_gfx.so")}\"{hostLinks} -lm -ldl")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = hostWork })!;
            var herr = hp.StandardError.ReadToEndAsync();
            string hout = hp.StandardOutput.ReadToEnd();
            hp.WaitForExit();
            string hlog = hout + herr.Result;
            Check("VM: die Bibliothek von graphics und windows fuer diese Maschine hat kein undefiniertes Symbol (gcc -Wl,--no-undefined)", hp.ExitCode == 0, hlog.Length > 8000 ? hlog.Substring(0, 8000) : hlog);
            hp.Dispose();
        }

        // without a display the program hears it: `new Window` throws why (FIRE_VM_GRAPHICS=cs: the C# implementation, which has no windows of the desktop) - never a window
        // that silently counts as closed while the program runs on
        string? before = Environment.GetEnvironmentVariable("FIRE_VM_GRAPHICS");
        Environment.SetEnvironmentVariable("FIRE_VM_GRAPHICS", "cs");
        try
        {
            string said = Vm(new[] { """
                #import "windows"
                var fb = new Framebuffer(64, 48)
                try { var win = new Window(fb, "nothing to show it on")
                    print("opened " + win.Tick()) }
                catch (e) { print("caught: " + e.message) }
                print("end")
                """ });
            Check("VM: ein Fenster ohne Anzeige - new Window wirft mit dem Grund (No display ...), nichts bleibt stumm", said.StartsWith("caught: No display", StringComparison.Ordinal) && said.Contains("FIRE_VM_GRAPHICS=cs") && said.EndsWith("end\n", StringComparison.Ordinal), said);
        }
        finally { Environment.SetEnvironmentVariable("FIRE_VM_GRAPHICS", before); }

        // the VM of Windows under Wine (FIRE_TEST_WIN_FORGE: a folder with forge.exe for win-x64, e.g. `dotnet build -c Release -r win-x64` of fire.Compiler): with the library
        // built above a window opens (Tick: True); without it and without a C compiler `new Window` says why
        string? winForge = Environment.GetEnvironmentVariable("FIRE_TEST_WIN_FORGE");
        string? wine = new[] { "/usr/lib/wine/wine64", "/usr/bin/wine64", "/usr/bin/wine" }.FirstOrDefault(File.Exists);
        string? xvfb = HasProgram("xvfb-run") ? "xvfb-run" : null;
        if (winForge == null || !File.Exists(Path.Combine(winForge, "forge.exe")) || wine == null || xvfb == null || !File.Exists(dll))
        {
            Console.WriteLine("(VM unter Wine mit Fenster: uebersprungen - FIRE_TEST_WIN_FORGE (forge.exe fuer win-x64), Wine oder xvfb-run fehlen)");
            return;
        }
        string prefix = Path.Combine(root, "wineprefix-vm");
        string RunWine(string arguments)
        {
            var info = new ProcessStartInfo(xvfb, $"-a -s \"-screen 0 800x600x24\" \"{wine}\" {arguments}") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = work };
            info.Environment["WINEPREFIX"] = prefix;
            info.Environment["WINEDEBUG"] = "-all";
            info.Environment["LANG"] = "C.UTF-8";
            using var w = Process.Start(info)!;
            var werr = w.StandardError.ReadToEndAsync();
            string wout = w.StandardOutput.ReadToEnd();
            if (!w.WaitForExit(300000)) { try { w.Kill(true); } catch (Exception) { } return wout + "(timeout)"; }
            return (wout + werr.Result).Replace("\r", "");
        }
        RunWine("wineboot --init");
        string program = Path.Combine(work, "window.fire");
        File.WriteAllText(program, """
            #import "ui"
            var fb = new Framebuffer(320, 200)
            try { var win = new Window(fb, "fire vm wine")
                var ui = new UI.Root(fb, win)
                ui.Add(new UI.Label("Hallo", 20, 20))
                print("open " + win.Tick()) }
            catch (e) { print("caught: " + e.message) }
            print("end")
            """);
        // (where the VM of Windows looks for the library it built: %TEMP%\fire-packages\<package>\win-x64)
        string built = Path.Combine(prefix, "drive_c", "users", Environment.UserName, "Temp", "fire-packages", "fire_graphics-1_0_0", "win-x64");
        Directory.CreateDirectory(built);
        string placed = Path.Combine(built, "fire_gfx.dll");
        File.Copy(dll, placed, true);
        File.SetLastWriteTimeUtc(placed, DateTime.UtcNow.AddDays(1));   // (newer than the sources and the compiler: taken as it is)
        string shown = RunWine($"\"{Path.Combine(winForge, "forge.exe")}\" run window.fire");
        Check("VM unter Wine (forge.exe win-x64): ein ui-Fenster oeffnet (Tick True) mit der Bibliothek von MinGW", shown.Contains("open True\nend"), shown);
        File.Delete(placed);
        string missing = RunWine($"\"{Path.Combine(winForge, "forge.exe")}\" run window.fire");
        Check("VM unter Wine ohne C-Compiler: new Window sagt, warum kein Fenster kommt (die Bibliothek kann nicht gebaut werden)",
            missing.Contains("caught: No display") && missing.Contains("C compiler") && missing.Contains("end"), missing);
    }

    public static void Run()
    {
        Console.WriteLine("=== Linkage \"static\": Bibliotheken im Cache ===");
        string root = Path.Combine(Path.GetTempPath(), "fire-linkage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string? before = Environment.GetEnvironmentVariable("FIRE_NATIVE_CACHE");
        Environment.SetEnvironmentVariable("FIRE_NATIVE_CACHE", Path.Combine(root, "cache"));
        try
        {
            // (FIRE_TESTS_FILTER=plugin: only the fire plugins; =library: only Library.Load/Get)
            string filter = Environment.GetEnvironmentVariable("FIRE_TESTS_FILTER") ?? "";
            // (=cancel: only the progress and the cancelling of a build, spark's build window)
            if (filter.Contains("plugin", StringComparison.OrdinalIgnoreCase)) Plugins(root);
            else if (filter.Contains("library", StringComparison.OrdinalIgnoreCase)) LibraryLoad(root);
            else if (filter.Contains("cancel", StringComparison.OrdinalIgnoreCase)) ProgressAndCancel(root);
            else if (filter.Contains("vmgraphics", StringComparison.OrdinalIgnoreCase)) VmGraphicsForWindows(root);
            else { RunIn(root); ProgressAndCancel(root); VmGraphicsForWindows(root); }
        }
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

    /// <summary>The classes of the program and of the libraries meet (ROADMAP 9 B2): a library calls members of objects of the program (OrderBy, Reflect.Get/Set/Call/New,
    /// probes), the program extends a generic List of core, overrides an operator, throws its own subclass of an exception of core, calls by name what a class
    /// lacks.</summary>
    private const string Meet = """
        #import "json"
        #import "linq"
        #import "reflection"
        class Pt {
            int x
            int y
            construct(int x, int y) { this.x = x; this.y = y }
            int CompareTo(Pt o) { return this.x - o.x }
            string ToString() { return "(" + x + "," + y + ")" }
            Pt operator+(Pt o) { return new Pt(x + o.x, y + o.y) }
            int Sum { get { return x + y } }
        }
        class MyError : ArgumentException {
            int code
            construct(string m, int c) : base(m) { code = c }
        }
        class Bag<T> : List<T> {
            string label = "bag"
            construct() : base() { }
            int Twice() { return Count * 2 }
        }
        var pts = new List<Pt>()
        pts.Add(new Pt(3, 1)); pts.Add(new Pt(1, 5)); pts.Add(new Pt(2, 2))
        pts = pts.OrderBy((Pt p) => p.x).ToList()
        print(pts[0].ToString() + " " + pts[2])
        print("sum " + (pts[0] + pts[1]))
        var big = pts.Where(p => p.Sum > 3).Select(p => p.x).ToList()
        print("big " + big.Count + " " + big[0])
        print("get " + Reflect.Get(pts[0], "y") + " " + Reflect.Get(pts[1], "Sum"))
        Reflect.Set(pts[0], "y", 42)
        print("set " + pts[0].y)
        print("call " + Reflect.Call(pts[1], "CompareTo", [pts[2]]))
        var b = new Bag<int>()
        b.Add(4); b.Add(5)
        print("bag " + b.Count + " " + b.Twice() + " " + b.label + " " + (b is of List) + " " + (b is of IEnumerable))
        var any = b
        var total = 0
        foreach (var v in any) { total += v }
        print("total " + total)
        try { throw new MyError("bad", 7) } catch (ArgumentException e) { print("caught " + e.message) }
        try { b.Add(Untyped.Text()) } catch (Exception e) { print("type " + e.message) }
        var o = pts[1]
        var holder = new List()
        holder.Add(o)
        var d = holder[0]
        print("dyn " + d.x + " " + d.Sum)
        try { d.Nope() } catch (Exception e) { print("miss " + e.message) }
        try { d.CompareTo() } catch (Exception e) { print("arity " + e.message) }
        print(Json.Serialize(pts[2]))
        print(Type.Of(pts[0]).Name)
        var np = Reflect.New("Pt", [7, 8])
        print("new " + np)
        var h = Reflect.Probe(np, "x", "changed", (any ob, any n, any ov, any nv) => print("probe " + n + " " + ov + "->" + nv))
        np.x = 9
        Reflect.Set(np, "x", 10)
        class Untyped {
            static any Text() { return "x" }
        }
        """;

    /// <summary>Stripping (docs/NATIVE_BACKEND.md "Größe des Programms") with libraries: the program leaves out like with "source" (a method of its own by a name
    /// made at run time, a class it never makes); a library keeps its API at every level (List.IndexOf by a name made at run time).</summary>
    private const string Strip = """
        #import "reflection"
        class Dog {
            string Bark() { return "wuff" }
            string Wag() { return "wag" }
        }
        class Ghost {
            int b
            construct() { this.b = 2 }
        }
        class N { static string Of(string a, string b) { return a + b } }
        var d = new Dog()
        print(Reflect.Call(d, "Bark", []))
        try { print(Reflect.Call(d, N.Of("Wa", "g"), [])) } catch (ReflectionException e) { print("call: " + e.message) }
        try { any g = Reflect.New(N.Of("Gho", "st"), []); print("ghost " + g.b) } catch (ReflectionException e) { print("new: " + e.message) }
        var l = new List()
        l.Add(1); l.Add(2)
        print("lib " + Reflect.Call(l, N.Of("Index", "Of"), [2]))
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

        // ---- every module serves its own classes (ROADMAP 9 B2): the program's C has no helpers for the classes of the libraries - the libraries reach the objects of
        // the program, and the program those of the libraries it does not know by type, through the tables of the members in the class descriptors ----
        string meetVm = Vm(new[] { Meet });
        var meet = Build(new[] { Meet }, "static", Path.Combine(root, "meet"));
        string meetOut = meet.Ok ? Exec(meet.Exe) : "";
        Check("Module: Klassen des Programms und der Libraries treffen sich (OrderBy, Reflect.Get/Set/Call/New, Probe, Operator, List<T> erweitert, Ausnahme, fehlende Methode) wie die VM",
            meet.Ok && meetOut == meetVm && !meet.Log.Contains("not possible"), $"{meet.Log}\nnativ:\n{meetOut}\nVM:\n{meetVm}");
        // ---- stripping: the program like with "source", a library keeps its API ----
        string stripVm = Vm(new[] { Strip });
        Check("Stripping: die VM", stripVm == "wuff\nwag\nghost 2\nlib 1\n", stripVm);
        foreach (var level in new[] { StrippingLevel.Methods, StrippingLevel.All })
        {
            var built = Build(new[] { Strip }, "static", Path.Combine(root, $"strip{(int)level}"), stripping: level);
            string got = built.Ok ? Exec(built.Exe) : "";
            int n = (int)level;
            string expected = $"wuff\ncall: Member 'Dog.Wag' was left out of the build (stripping {n}). Add [Keep] to 'Dog.Wag' or use stripping <= 1.\n"
                + (level == StrippingLevel.All ? "new: Class 'Ghost' was left out of the build (stripping 3). Add [Keep] to 'Ghost' or use stripping <= 2.\n" : "ghost 2\n") + "lib 1\n";
            Check($"Stripping {n} mit static: das Programm laesst weg wie mit source (klare Fehler), eine Library behaelt ihre Schnittstelle (List.IndexOf per Laufzeitname)",
                built.Ok && got == expected && !built.Log.Contains("not possible"), $"{built.Log}\nnativ:\n{got}\nerwartet:\n{expected}");
        }

        var linkedCli = new Linker { Engine = "native" }.CompileAndLink(new[] { Cli });
        string sourceC = CppGenerator.Generate(linkedCli);
        var cliPlan = CppGenerator.GenerateStatic(linkedCli, TargetProfile.Host, "linkage-test", cacheRoot: Path.Combine(root, "plan-cache"));
        Check("Module: das C des Programms ist kleiner als zwei Drittel von dem mit source (es bedient nur seine eigenen Klassen; Natives in C und die JSON-Engine in den Bibliotheken)",
            cliPlan.ProgramC.Length * 3 < sourceC.Length * 2 && !cliPlan.ProgramC.Contains("FIRE_JSON_IMPLEMENTATION 1") && !cliPlan.ProgramC.Contains("fire_bridge_hash.h"),
            $"Programm {cliPlan.ProgramC.Length} Zeichen, source {sourceC.Length}");

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

        // ---- linkage "shared" (phase C, Linux/macOS): the libraries as shared libraries next to the program; two programs share them; a library that does not fit
        // the program (another interface) is a readable error when the program starts ----
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            plan = Plan();
            string shDir1 = Path.Combine(root, "shared1");
            Directory.CreateDirectory(shDir1);
            var sh1 = Build(plan.SourceTexts, "shared", Path.Combine(shDir1, "app"), plan: plan);
            string sh1Vm = Vm(plan.SourceTexts, plan);
            var soFiles = Directory.Exists(shDir1) ? Directory.GetFiles(shDir1, "libfire_*").Select(Path.GetFileName).ToList() : new List<string?>();
            Check("Shared: Programm mit Bibliotheken des Projekts als Shared Libraries neben dem Programm, wie die VM",
                sh1.Ok && Exec(sh1.Exe) == sh1Vm && soFiles.Any(f => f!.StartsWith("libfire_mLib_")) && soFiles.Any(f => f!.StartsWith("libfire_mcore_")) && !sh1.Log.Contains("only available"),
                $"VM: {sh1Vm}\n{string.Join(" ", soFiles)}\n{sh1.Log}");
            foreach (var dir in new[] { "sharedA", "sharedB", "shared2" }) Directory.CreateDirectory(Path.Combine(root, dir));
            const string smallA = "#import \"json\"\nclass Q { int x = 4 }\nprint(Json.Serialize(new Q()))\n";
            const string smallB = "#import \"json\"\nclass R { string t = \"r\" }\nprint(Json.Serialize(new R()) + \"!\")\n";
            var shA = Build(new[] { smallA }, "shared", Path.Combine(root, "sharedA", "a"));
            var shB = Build(new[] { smallB }, "shared", Path.Combine(root, "sharedB", "b"));
            Check("Shared: zwei Programme teilen sich die Bibliotheken (das zweite baut keine), beide wie die VM",
                shA.Ok && shB.Ok && ModuleLines(shB.Log, "built in").Count == 0 && ModuleLines(shB.Log, "from the cache").Contains("pkg:json")
                && Exec(shA.Exe) == Vm(new[] { smallA }) && Exec(shB.Exe) == Vm(new[] { smallB }), shA.Log + "\n" + shB.Log);
            // another layout of Lib (a field more): its library put in place of the one the first program was built with
            string before = File.ReadAllText(libSource);
            File.WriteAllText(libSource, before.Replace("        int n = 0\n", "        int n = 0\n        int extra = 1\n"));
            plan = Plan();
            var sh2 = Build(plan.SourceTexts, "shared", Path.Combine(root, "shared2", "app"), plan: plan);
            string? newLib = sh2.Ok ? Directory.GetFiles(Path.Combine(root, "shared2"), "libfire_mLib_*").FirstOrDefault() : null;
            string? oldLib = Directory.Exists(shDir1) ? Directory.GetFiles(shDir1, "libfire_mLib_*").FirstOrDefault() : null;
            string mismatch = "";
            if (newLib != null && oldLib != null) { File.Copy(newLib, oldLib, true); mismatch = Exec(sh1.Exe); }
            Check("Shared: eine Bibliothek mit einer anderen Schnittstelle neben dem Programm ist beim Start ein lesbarer Fehler (kein Absturz)",
                mismatch.Contains("does not fit this program") && mismatch.Contains("(exit 70)") && mismatch.Contains("'Lib'"), mismatch + "\n" + sh2.Log);
            File.WriteAllText(libSource, before);
        }

        // ---- a debug build: a breakpoint inside a static library ----
        DebugInLibrary(root, Plan(), libSource);

        // ---- Library.Load / Get (phase D): a C library loaded while the program runs ----
        LibraryLoad(root);

        // ---- fire plugins (phase D b) ----
        Plugins(root);

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
            var tccShared = Build(new[] { small }, "shared", Path.Combine(root, "tcc-shared"), ToolchainDef.BuiltIn["tcc"]);
            Check("TinyCC: linkage shared gibt es dort nicht - gebaut wird static, das Protokoll sagt es", tccShared.Ok && tccShared.Log.Contains("only available") && Exec(tccShared.Exe) == smallVm, tccShared.Log);
        }
        else Console.WriteLine("(TinyCC: uebersprungen - tcc fehlt)");
        var mingw = new ToolchainDef { Kind = "gcc", Compiler = "x86_64-w64-mingw32-g++", CCompiler = "x86_64-w64-mingw32-gcc", Std = "c++17", CStd = "c11", Optimization = "-O2" };
        // (Debian/Ubuntu's wine64 package puts the loader into /usr/lib/wine without a `wine` on the PATH)
        string? wine = new[] { "/usr/lib/wine/wine64", "/usr/bin/wine64", "/usr/bin/wine" }.FirstOrDefault(File.Exists) ?? (HasProgram("wine") ? "wine" : null);
        if (ToolchainDetector.Find(mingw) != null && wine != null)
        {
            var win = Build(new[] { small }, "static", Path.Combine(root, "win.exe"), mingw, TargetProfile.Windows);
            _winePrefix = Path.Combine(root, "wineprefix");
            Check("MinGW: linkage static fuer Windows (x86_64-w64-mingw32-ar), unter Wine wie die VM", win.Ok && Exec(win.Exe, wine) == smallVm, win.Log);
            WindowsShared(root, mingw, wine, Plan, libSource);
        }
        else Console.WriteLine("(MinGW/Wine: uebersprungen)");

        // ---- ember cache clean ----
        var (libraries, _) = NativeLibraryCache.Clean();
        Check("ember cache clean: leert den Cache", libraries > 0 && !Directory.Exists(NativeLibraryCache.Root));
        var again = Build(new[] { other }, "static", Path.Combine(root, "other2"));
        Check("Nach dem Leeren: die Bibliotheken werden neu gebaut", again.Ok && ModuleLines(again.Log, "built in").Contains("core"), again.Log);
    }

    private static bool HasProgram(string name) => ToolchainSetup.FindProgram(name) != null;

    // ---- fire plugins (ROADMAP 9 phase D b): a library module built on its own and loaded while the program runs ----
    private const string PluginApi = """
        prefixed unit Watt: W
        unit DecibelMilliwatt: dBm
        {
            coerce to mW => 10 ^ (value / 10)
            coerce from mW => 10 * Math.Log10(value)
        }
        namespace Api {
            interface IGauge {
                string Report()
                any Convert(any x)
            }
            interface IDriver {
                string Name()
                int Run(int x)
            }
            // (a property in an interface, an interface that extends another: implemented by a class of the plugin, used by the program)
            interface INamed { string Title { get; } }
            interface ITuned : Api.INamed { int Level { get; set; } }
            class Device {
                string id
                construct(string id) { this.id = id }
                string Describe() { return "device " + id }
                int Speed() { return 1 }
            }
            class DriverError : Exception {
                int code
                construct(string m, int code) : base(m) { this.code = code }
            }
        }
        """;

    private const string PluginKit = """
        #import "Api"
        // (class extends of a class of a library and of base types: methods, a property with a body, a static method - the objects are the program's)
        class extends Api.Device {
            string Tag() { return "tag " + this.id + " " + this.Speed() }
            string Label { get { return "L" + this.id } }
            static string Kind() { return "devices" }
        }
        class extends string { string Shout() { return this.ToUpper() + "!" } }
        class extends int { bool Big() { return this > 100 } }
        class extends List { int Twice() { return this.Count * 2 } }
        namespace Kit {
            class Probe {
                construct() { }
                string Look(Api.Device d) { var l = new List<int>(); l.Add(1); return d.Tag() + " " + "hi".Shout() + " " + l.Twice() + " " + d.Label + " " + Api.Device.Kind() + " " + 500.Big() }
            }
            enum Mode { Slow, Fast }
            class MyDriver : Api.IDriver {
                static int made = 0
                static List<string> log = new List<string>()
                Kit.Mode mode = Kit.Mode.Fast
                construct() { Kit.MyDriver.made = Kit.MyDriver.made + 1; Kit.MyDriver.log.Add("made") }
                string Name() { return "my driver " + Kit.MyDriver.made + " " + mode + " " + Kit.MyDriver.log.Count }
                int Run(int x) {
                    if (x < 0) { throw new Api.DriverError("negative", x) }
                    try { if (x == 0) { throw new Kit.KitError("zero") } } catch (Kit.KitError e) { return -100 }
                    return x * 2
                }
            }
            class FastDevice : Api.Device {
                construct(string id) : base(id) { }
                override string Describe() { return "fast " + this.id }
                override int Speed() { return 9 }
            }
            class KitError : Exception {
                construct(string m) : base(m) { }
            }
            class Tuner : Api.ITuned {
                string Title { get { return "tuner" } }
                int Level { get; set; }
            }
            class Settings {
                string title = "kit"
                int level = 3
                List<int> marks = new List<int>()
                Api.Device dev = undefined
                construct() { marks.Add(1); marks.Add(2); dev = new Kit.FastDevice("j0") }
                string Show() { return title + " " + level + " " + marks.Count + " " + marks[1] + " " + dev.Describe() + " " + dev.Speed() }
            }
        }
        """;

    private const string PluginHost = """
        #import "Api"
        #import "reflection"
        unit Foot: ft { coerce m => 0.3048 }
        class Mine : Api.Device {
            construct() : base("mine") { }
        }
        class Quick : Api.Device {
            construct() : base("quick") { }
            override int Speed() { return 3 }
        }
        any lib = undefined
        try { lib = Library.Load(Environment.Get("FIRE_TEST_PLUGIN")) } catch (LibraryException e) { print("load: " + e.message); terminate(3) }
        var names = lib.Classes()
        var listed = ""
        foreach (var n in names) { listed = listed + " " + n }
        print("classes" + listed + " " + lib.IsPlugin)
        Api.IDriver d = Reflect.New("Kit.MyDriver", [])
        print(d.Name())
        print(d.Run(21) + " " + d.Run(0))
        try { d.Run(-3) } catch (Api.DriverError e) { print("driver error " + e.message + " " + e.code) }
        print(d is of Api.IDriver)
        any again = Reflect.New("Kit.MyDriver", [])
        print(again.Name())
        Api.Device dev = Reflect.New("Kit.FastDevice", ["x1"])
        var devices = new List<Api.Device>()
        devices.Add(new Mine())
        devices.Add(dev)
        foreach (var one in devices) { print(one.Describe() + " " + one.Speed() + " " + (one is of Api.Device)) }
        print(Reflect.Get(dev, "id") + " " + Reflect.Call(d, "Run", [5]) + " " + Type.Of(dev).Name)
        print("again " + Library.Load(Environment.Get("FIRE_TEST_PLUGIN")).Classes().Length)
        try { Reflect.New("Kit.Nope", []) } catch (ReflectionException e) { print("nope: " + e.message) }
        // (Kit does not import reflection: its classes still carry their metadata - fields by name, Type.Of(x).Fields())
        any kitSettings = Reflect.New("Kit.Settings", [])
        Reflect.Set(kitSettings, "level", 7)
        var kitFields = ""
        foreach (var kf in Type.Of(kitSettings).Fields()) { kitFields = kitFields + " " + kf.Name + ":" + kf.TypeName }
        print(Reflect.Get(kitSettings, "level") + " " + Reflect.Get(kitSettings, "title") + kitFields + " " + Type.Of(kitSettings).Find("Show").Kind)
        Api.ITuned tuned = Reflect.New("Kit.Tuner", [])
        tuned.Level = 4
        tuned.Level = tuned.Level + 1
        Api.INamed named = tuned
        print(named.Title + " " + tuned.Level + " " + (named is of Api.ITuned) + " " + (tuned is of Api.INamed))
        // (what Kit's class extends adds to Api.Device, string, int, List: on objects of the program - a subclass that overrides Speed - and of the plugin)
        any probe = Reflect.New("Kit.Probe", [])
        print(probe.Look(new Quick()) + " | " + probe.Look(dev))
        any qd = new Quick()
        any sx = "abc"
        any nx = 7
        print(qd.Tag() + " " + Reflect.Call(new Api.Device("r"), "Tag", []) + " " + qd.Label + " " + sx.Shout() + " " + nx.Big())
        any up = Environment.Get("FIRE_TEST_UNITS")
        if (up != undefined) {
            Library.Load(up)
            Api.IGauge g = Reflect.New("Meter.Gauge", [])
            print(g.Report())
            print(g.Convert(1.0m) + " " + g.Convert(254mm) + " " + g.Convert(3.0ft) + " " + g.Convert(2.0km):m)
            any m = Reflect.Call(g, "Measure", [])
            print(m + " " + m:mW + " " + (m * 2) + " " + (m * 1s) + " " + Reflect.Call(g, "Level", []):W + " " + (Reflect.Call(g, "Level", []) == 30.0dBm))
            any c = Reflect.Call(g, "Pears", [])
            print(Reflect.Call(g, "Native", [21]))
            print(c + " " + (c * 4apples) + " " + (c * 1m / 1s) + " " + (4apples * 1s) + " " + "3mm".ToFloat() + " " + "2ft".ToFloat() + " " + (1.0ft + 1.0m) + " " + (m * c))
        }
        any dupPlugin = Environment.Get("FIRE_TEST_DUP")
        if (dupPlugin != undefined) { try { Library.Load(dupPlugin) } catch (LibraryException e) { print("dup: " + e.message) } }
        """;

    /// <summary>JSON of the plugins' objects (a program with `#import "json"`): Kit.Settings (a list, a field with an object of the plugin), a subclass of a class of
    /// Api by "$type", Meter.Reading (fields with units of the plugin and of the program).</summary>
    private const string PluginJsonChecks = """
        if (Environment.Get("FIRE_TEST_JSON") != undefined) {
            any st = Reflect.New("Kit.Settings", [])
            string text = Json.Serialize(st)
            print(text)
            any back = Json.Deserialize(text, "Kit.Settings")
            print(Reflect.Call(back, "Show", []) + " " + Type.Of(back).Name)
            Api.Device dd = Json.Deserialize<Api.Device>("{\"$type\": \"Kit.FastDevice\", \"id\": \"j1\"}")
            print(dd.Describe() + " " + dd.Speed())
            any r = Reflect.New("Meter.Reading", [])
            string rt = Json.Serialize(r)
            print(rt)
            print(Reflect.Call(Json.Deserialize(rt, "Meter.Reading"), "Show", []))
        }
        """;

    /// <summary>The natives in C++ of the plugin Meter (its project's folder native/): the C++ library, state of its own.</summary>
    private const string PluginMeterCpp = """
        #include <string>
        namespace fire {
        static int mt_count = 0;
        inline Value mt_twice(Value a) { mt_count++; return Int(a.i * 2); }
        inline Value mt_label(Value a, OwnList* list) {
            std::string text = "n=" + std::to_string((long long)a.i);
            Str* s = allocStr((uint32_t)text.size(), list);
            for (size_t i = 0; i < text.size(); i++) strChars(s)[i] = (fire_char16)text[i];
            return StrV(s);
        }
        inline Value mt_calls() { return Int(mt_count); }
        inline Value mt_check(Value a) { if (a.i < 0) return fireError("ArgumentException", "negative in C++"); return a; }
        }
        """;

    /// <summary>A plugin with units (ROADMAP 9): its own (a new dimension, a factor, an offset, conversion functions of its own), those of a library (dBm of Api, with
    /// conversion functions) and of fire, units of the program passed in and back, a text that names its units.</summary>
    private const string PluginMeter = """
        #import "Api"
        prefixed unit Volt: V
        unit Inch: in { coerce m => 0.0254 }
        unit Kelvin: K
        unit Celsius: C { coerce K => value + 273.15 }
        unit Binary: bn
        {
            coerce to mW => 2 ^ value
            coerce from mW => Math.Log(value) / Math.Log(2)
        }
        namespace Meter {
            class Gauge : Api.IGauge {
                construct() { }
                string Report() {
                    float t = 20.0C
                    return (5V + 300mV) + " " + (1.0m:in) + " " + (10in + 2.54cm) + " " + t:K + " " + (30.0dBm:W) + " " + (3.0bn:mW) + " " + (3pears * 2m) + " " + (2km + 5m) + " " + "7in".ToFloat() + " " + "2kV".ToFloat() + " " + "5bn".ToFloat():mW
                }
                any Convert(any x) { return x:in }
                any Measure() { return 4.0bn }
                any Level() { return 30.0dBm }
                any Pears() { return 3.0pears / 1kV }
                string Native(int x) {
                    var thrown = ""
                    int before = __mt_calls()
                    try { __mt_check(-1) } catch (ArgumentException e) { thrown = e.message }
                    return __mt_twice(x) + " " + __mt_label(x) + " " + __mt_twice(1) + " " + (__mt_calls() - before) + " " + __mt_check(5) + " " + thrown
                }
            }
            class Reading {
                float level : pears = 3.0pears
                float span : km = 2.5km
                float heat : C = 20.0C
                any loose = 4.0bn
                construct() { }
                string Show() { return level + " " + span + " " + heat + " " + heat:K + " " + loose }
            }
        }
        """;

    private const string PluginUnitsExpected = "5300mV 39.37007874015748in 11in 293.15K 1W 8mW 6pears*m 2005m 7in 2kV 32mW\n39.37007874015748in 10in 36in 2000.0000000000002m\n"
        + "4bn 16mW 8bn 4s*W(×0.001) 1W True\n42 n=21 2 2 5 negative in C++\n3pears/kV 12pears*apples/kV 3pears*m/kV*s 4apples*s 3mm 2ft 4.2808398950131235ft 12pears*W/V(×1E-06)\n";

    private const string PluginJsonExpected = "{\"$type\":\"Kit.Settings\",\"title\":\"kit\",\"level\":3,\"marks\":[1,2],\"dev\":{\"$type\":\"Kit.FastDevice\",\"id\":\"j0\"}}\n"
        + "kit 3 2 2 fast j0 9 Kit.Settings\nfast j1 9\n{\"$type\":\"Meter.Reading\",\"level\":3.0,\"span\":2.5,\"heat\":20.0,\"loose\":{\"$value\":4.0,\"$unit\":\"bn\"}}\n3pears 2.5km 20C 293.15K 4bn\n";

    private const string PluginExpected = "classes Kit.FastDevice Kit.KitError Kit.MyDriver Kit.Probe Kit.Settings Kit.Tuner True\nmy driver 1 Fast 1\n42 -100\ndriver error negative -3\nTrue\nmy driver 2 Fast 2\n"
        + "device mine 1 True\nfast x1 9 True\nx1 10 Kit.FastDevice\nagain 6\nnope: Unknown class 'Kit.Nope'.\n"
        + "7 kit title:string level:int marks:List<int> dev:Api.Device method\ntuner 5 True True\n"
        + "tag quick 3 HI! 2 Lquick devices True | tag x1 9 HI! 2 Lx1 devices True\ntag quick 3 tag r 1 Lquick ABC! False\n";

    /// <summary>fire plugins (ROADMAP 9 phase D b): a project with a library `Api` (an interface, a class, an exception), a program with linkage "shared" that loads the
    /// plugin `Kit` (built with `--linkage plugin --host`) and uses its classes through the interface, the base class, reflection and catch; the errors when the plugin does
    /// not fit (another interface of Api, a program without linkage "shared", other switches).</summary>
    private static void Plugins(string root)
    {
        string s = Path.Combine(root, "plug");
        string apiSource = Write(s, "Api/api.script", PluginApi);
        Write(s, "Api/Api.fireproj", """{ "name": "Api", "type": "library" }""");
        Write(s, "Kit/Kit.fireproj", """{ "name": "Kit", "references": [ { "project": "../Api/Api.fireproj" } ], "settings": { "linkage": "plugin" } }""");
        Write(s, "Kit/kit.script", PluginKit);
        Write(s, "Kit2/Kit2.fireproj", """{ "name": "Kit2", "references": [ { "project": "../Api/Api.fireproj" } ], "settings": { "linkage": "plugin" } }""");
        Write(s, "Kit2/kit2.script", "#import \"Api\"\nclass extends Api.Device { string Tag() { return \"other\" } }\nnamespace Kit2 { class Other { construct() { } } }\n");
        Write(s, "Meter/Meter.fireproj", """{ "name": "Meter", "references": [ { "project": "../Api/Api.fireproj" } ], "settings": { "linkage": "plugin" }, "native": { } }""");
        Write(s, "Meter/native/meter.hpp", PluginMeterCpp);
        Write(s, "Meter/meter.script", PluginMeter);
        Write(s, "Host/Host.fireproj", """{ "name": "Host", "references": [ { "project": "../Api/Api.fireproj" } ], "settings": { "linkage": "shared" } }""");
        Write(s, "Host/main.script", PluginHost);
        string sln = Write(s, "All.firesln", """{ "name": "All", "projects": ["Api/Api.fireproj", "Kit/Kit.fireproj", "Kit2/Kit2.fireproj", "Meter/Meter.fireproj", "Host/Host.fireproj"], "startup": "Host" }""");
        BuildPlan Plan(string name) { var ws = Workspace.Open(sln); return BuildPlan.Create(ws, ws.FindByName(name)!); }
        Built BuildPlugin(string output, string? host, ToolchainDef? toolchain = null, TargetProfile? target = null, string project = "Kit")
        {
            var plan = Plan(project);
            var config = new NativeConfig { Linkage = "plugin", Host = host };
            target ??= TargetProfile.Host;
            toolchain ??= config.ResolveToolchain(target, "gcc");
            var result = NativeBuilder.BuildSafe(plan.SourceTexts, config, target, toolchain, output, plan: plan);
            return new Built(result.Ok, result.Output, result.Log, 0, 0);
        }
        string? before = Environment.GetEnvironmentVariable("FIRE_TEST_PLUGIN");
        Environment.SetEnvironmentVariable("FIRE_TEST_UNITS", null);
        try
        {
            if (OperatingSystem.IsLinux())
            {
                string hostDir = Path.Combine(root, "plug-host");
                Directory.CreateDirectory(hostDir);
                var hostPlan = Plan("Host");
                var host = Build(hostPlan.SourceTexts, "shared", Path.Combine(hostDir, "host"), plan: hostPlan);
                var kit = BuildPlugin(Path.Combine(root, "plug-kit", "kit"), host.Exe);
                Environment.SetEnvironmentVariable("FIRE_TEST_PLUGIN", Path.Combine(root, "plug-kit", "kit.so"));
                string native = host.Ok && kit.Ok ? Exec(host.Exe) : "";
                Check("Plugin: ein Programm mit linkage shared laedt das Plugin Kit (Interface, Basisklasse, Reflect.New/Get/Call, Type.Of, catch, Enum, statische Felder) - nativ",
                    native == PluginExpected && File.Exists(Path.Combine(root, "plug-kit", "kit.fireplugin")), $"{native}\n{host.Log}\n{kit.Log}");
                // the VM: the bytecode next to the library
                Environment.SetEnvironmentVariable("FIRE_TEST_PLUGIN", Path.Combine(root, "plug-kit", "kit.fireplugin"));
                string vm = Vm(hostPlan.SourceTexts, hostPlan);
                Check("Plugin: die VM laedt den Bytecode des Plugins (.fireplugin) wie nativ", vm == PluginExpected, vm);
                // (Library.Load of the .fireplugin file natively: the library next to it)
                string viaBytecode = host.Ok && kit.Ok ? Exec(host.Exe) : "";
                Check("Plugin: nativ laedt Library.Load(\"kit.fireplugin\") die Bibliothek daneben", viaBytecode == PluginExpected, viaBytecode);

                // a plugin with units: its own, a library's, fire's, the program's - mapped to the program's by dimensions, scale and symbol (natively), by name (VM)
                var meter = BuildPlugin(Path.Combine(root, "plug-meter", "meter"), host.Exe, project: "Meter");
                Environment.SetEnvironmentVariable("FIRE_TEST_PLUGIN", Path.Combine(root, "plug-kit", "kit.so"));
                Environment.SetEnvironmentVariable("FIRE_TEST_UNITS", Path.Combine(root, "plug-meter", "meter.so"));
                string unitsNative = host.Ok && meter.Ok ? Exec(host.Exe) : "";
                Environment.SetEnvironmentVariable("FIRE_TEST_PLUGIN", Path.Combine(root, "plug-kit", "kit.fireplugin"));
                Environment.SetEnvironmentVariable("FIRE_TEST_UNITS", Path.Combine(root, "plug-meter", "meter.fireplugin"));
                string unitsVm = Vm(hostPlan.SourceTexts, hostPlan);
                Environment.SetEnvironmentVariable("FIRE_TEST_UNITS", null);
                Check("Plugin: Einheiten im Plugin (eigene, neue Dimension, Offset, eigene und Library-Umrechnungsfunktionen, Text) und des Programms - nativ",
                    unitsNative == PluginExpected + PluginUnitsExpected, $"{unitsNative}\n{meter.Log}");
                Check("Plugin: Einheiten im Plugin - die VM wie nativ", unitsVm == PluginExpected + PluginUnitsExpected, unitsVm);

                // a plugin built against another interface of Api (a field more): readable error, in the VM too
                string apiBefore = File.ReadAllText(apiSource);
                try
                {
                    File.WriteAllText(apiSource, apiBefore.Replace("        string id\n", "        string id\n        int extra = 0\n"));
                    var other = BuildPlugin(Path.Combine(root, "plug-other", "kit"), host.Exe);
                    Environment.SetEnvironmentVariable("FIRE_TEST_PLUGIN", Path.Combine(root, "plug-other", "kit.so"));
                    string mismatch = other.Ok ? Exec(host.Exe) : "";
                    Check("Plugin: gegen eine andere Schnittstelle von Api gebaut - eine lesbare LibraryException statt eines Absturzes",
                        mismatch.Contains("The plugin 'Kit' cannot be loaded: it was built against another interface of the module 'Api'"), $"{mismatch}\n{other.Log}");
                    Environment.SetEnvironmentVariable("FIRE_TEST_PLUGIN", Path.Combine(root, "plug-other", "kit.fireplugin"));
                    string vmMismatch = Vm(hostPlan.SourceTexts, hostPlan);
                    Check("Plugin: die VM prueft die Schnittstelle genauso", vmMismatch.Contains("The plugin 'Kit' cannot be loaded: it was built against another interface of the module 'Api'"), vmMismatch);
                }
                finally { File.WriteAllText(apiSource, apiBefore); }

                // class extends in a plugin: a field cannot be added to a class of a library (its objects are made by the program) - the build says so
                string kitSource = Path.Combine(s, "Kit", "kit.script");
                string kitBefore = File.ReadAllText(kitSource);
                try
                {
                    File.WriteAllText(kitSource, kitBefore.Replace("    static string Kind()", "    int age = 3\n    static string Kind()"));
                    var withField = BuildPlugin(Path.Combine(root, "plug-field", "kit"), host.Exe);
                    Check("Plugin: class extends einer Klasse einer Library mit einem Feld - der Bau sagt, dass ein Plugin nur Methoden und Properties hinzufuegt",
                        !withField.Ok && withField.Log.Contains("'class extends Api.Device' in a plugin") && withField.Log.Contains("the field 'age' cannot be added"), withField.Log);
                }
                finally { File.WriteAllText(kitSource, kitBefore); }
                // a second plugin that adds the same method to Api.Device: readable error (natively and in the VM)
                var kit2 = BuildPlugin(Path.Combine(root, "plug-kit2", "kit2"), host.Exe, project: "Kit2");
                Environment.SetEnvironmentVariable("FIRE_TEST_PLUGIN", Path.Combine(root, "plug-kit", "kit.so"));
                Environment.SetEnvironmentVariable("FIRE_TEST_DUP", Path.Combine(root, "plug-kit2", "kit2.so"));
                string dupNative = host.Ok && kit2.Ok ? Exec(host.Exe) : "";
                Environment.SetEnvironmentVariable("FIRE_TEST_PLUGIN", Path.Combine(root, "plug-kit", "kit.fireplugin"));
                Environment.SetEnvironmentVariable("FIRE_TEST_DUP", Path.Combine(root, "plug-kit2", "kit2.fireplugin"));
                string dupVm = Vm(hostPlan.SourceTexts, hostPlan);
                Environment.SetEnvironmentVariable("FIRE_TEST_DUP", null);
                const string dupText = "dup: The plugin 'Kit2' cannot be loaded: it adds the method 'Tag' (0 arguments) to 'Api.Device', which has it already";
                Check("Plugin: zwei Plugins fuegen Api.Device dieselbe Methode hinzu - eine lesbare LibraryException (nativ)", dupNative.Contains(dupText), $"{dupNative}\n{kit2.Log}");
                Check("Plugin: zwei Plugins fuegen dieselbe Methode hinzu - die VM wie nativ", dupVm.Contains(dupText), dupVm);

                // a program with linkage "static" cannot load plugins
                Directory.CreateDirectory(Path.Combine(root, "plug-static"));
                var hostStatic = Build(hostPlan.SourceTexts, "static", Path.Combine(root, "plug-static", "host"), plan: hostPlan);
                Environment.SetEnvironmentVariable("FIRE_TEST_PLUGIN", Path.Combine(root, "plug-kit", "kit.so"));
                string noShared = hostStatic.Ok ? Exec(hostStatic.Exe) : "";
                Check("Plugin: ein Programm ohne linkage shared sagt, dass es keine Plugins laden kann", noShared.Contains("this program cannot load fire plugins"), $"{noShared}\n{hostStatic.Log}");

                // a plugin built for other switches (a program that uses JSON)
                string threadsDir = Path.Combine(root, "plug-threads");
                Directory.CreateDirectory(threadsDir);
                string mainFile = Path.Combine(s, "Host", "main.script");
                File.WriteAllText(mainFile, "#import \"json\"\nvar js = Json.Serialize(1)\n" + PluginHost + PluginJsonChecks);
                var threadsPlan = Plan("Host");
                File.WriteAllText(mainFile, PluginHost);
                var hostThreads = Build(threadsPlan.SourceTexts, "shared", Path.Combine(threadsDir, "host"), plan: threadsPlan);
                string otherSwitches = hostThreads.Ok ? Exec(hostThreads.Exe) : "";
                Check("Plugin: fuer andere Schalter gebaut - eine lesbare LibraryException (die Konfiguration der Runtime)",
                    otherSwitches.Contains("it was built for another configuration of the runtime") || otherSwitches.Contains("it needs the module"), $"{otherSwitches}\n{hostThreads.Log}");

                // JSON of the plugins' objects: Kit and Meter built for that program (the JSON rows of their classes, the program's jsonClassAt asks the runtime)
                var kitJson = hostThreads.Ok ? BuildPlugin(Path.Combine(root, "plug-json", "kit"), hostThreads.Exe) : new Built(false, "", "", 0, 0);
                var meterJson = hostThreads.Ok ? BuildPlugin(Path.Combine(root, "plug-json", "meter"), hostThreads.Exe, project: "Meter") : new Built(false, "", "", 0, 0);
                Environment.SetEnvironmentVariable("FIRE_TEST_PLUGIN", Path.Combine(root, "plug-json", "kit.so"));
                Environment.SetEnvironmentVariable("FIRE_TEST_UNITS", Path.Combine(root, "plug-json", "meter.so"));
                Environment.SetEnvironmentVariable("FIRE_TEST_JSON", "1");
                string jsonNative = kitJson.Ok && meterJson.Ok ? Exec(hostThreads.Exe) : "";
                Environment.SetEnvironmentVariable("FIRE_TEST_PLUGIN", Path.Combine(root, "plug-json", "kit.fireplugin"));
                Environment.SetEnvironmentVariable("FIRE_TEST_UNITS", Path.Combine(root, "plug-json", "meter.fireplugin"));
                string jsonVm = Vm(threadsPlan.SourceTexts, threadsPlan);
                Environment.SetEnvironmentVariable("FIRE_TEST_UNITS", null);
                Environment.SetEnvironmentVariable("FIRE_TEST_JSON", null);
                Check("Plugin: JSON von Objekten der Plugins (Serialize, Deserialize mit $type und Klassenname, Felder mit Einheiten des Plugins) - nativ",
                    jsonNative == PluginExpected + PluginUnitsExpected + PluginJsonExpected, $"{jsonNative}\n{kitJson.Log}\n{meterJson.Log}");
                Check("Plugin: JSON von Objekten der Plugins - die VM wie nativ", jsonVm == PluginExpected + PluginUnitsExpected + PluginJsonExpected, jsonVm);

                // Windows (MinGW, under Wine): the plugin's DLL imports from the runtime's DLL and those of the program's modules, its pointers to the program are filled
                // by its fire_link function from the program's table
                var mingw = new ToolchainDef { Kind = "gcc", Compiler = "x86_64-w64-mingw32-g++", CCompiler = "x86_64-w64-mingw32-gcc", Std = "c++17", CStd = "c11", Optimization = "-O2" };
                string? wine = new[] { "/usr/lib/wine/wine64", "/usr/bin/wine64", "/usr/bin/wine" }.FirstOrDefault(File.Exists);
                if (ToolchainDetector.Find(mingw) != null && wine != null)
                {
                    _winePrefix ??= Path.Combine(root, "wineprefix");
                    string winDir = Path.Combine(root, "plug-win");
                    Directory.CreateDirectory(winDir);
                    // (the program with JSON: the JSON rows of the plugins' classes are checked there too)
                    var winHost = Build(threadsPlan.SourceTexts, "shared", Path.Combine(winDir, "host.exe"), mingw, TargetProfile.Windows, threadsPlan);
                    var winKit = winHost.Ok ? BuildPlugin(Path.Combine(winDir, "kit"), winHost.Exe, mingw, TargetProfile.Windows) : new Built(false, "", "", 0, 0);
                    var winMeter = winHost.Ok ? BuildPlugin(Path.Combine(winDir, "meter"), winHost.Exe, mingw, TargetProfile.Windows, "Meter") : new Built(false, "", "", 0, 0);
                    Environment.SetEnvironmentVariable("FIRE_TEST_PLUGIN", "kit.dll");
                    string winOut = winHost.Ok && winKit.Ok ? Exec(winHost.Exe, wine) : "";
                    Check("Plugin: Windows (MinGW, DLL des Plugins gegen die DLLs des Programms, fire_link) unter Wine wie die VM", winOut == PluginExpected, $"{winOut}\n{winHost.Log}\n{winKit.Log}");
                    Environment.SetEnvironmentVariable("FIRE_TEST_UNITS", "meter.dll");
                    Environment.SetEnvironmentVariable("FIRE_TEST_JSON", "1");
                    string winUnits = winHost.Ok && winMeter.Ok ? Exec(winHost.Exe, wine) : "";
                    Environment.SetEnvironmentVariable("FIRE_TEST_UNITS", null);
                    Environment.SetEnvironmentVariable("FIRE_TEST_JSON", null);
                    Check("Plugin: Einheiten, Natives in C++ und JSON der Plugins unter Windows (MinGW, Wine) wie die VM", winUnits == PluginExpected + PluginUnitsExpected + PluginJsonExpected, $"{winUnits}\n{winMeter.Log}");
                    var noHost = BuildPlugin(Path.Combine(winDir, "nohost"), null, mingw, TargetProfile.Windows);
                    Check("Plugin: fuer Windows ohne --host - der Bau sagt, dass das Plugin fuer ein Programm gebaut wird", !noHost.Ok && noHost.Log.Contains("--host"), noHost.Log);
                }
                else Console.WriteLine("(Plugin unter Windows: uebersprungen - MinGW/Wine fehlt)");
                PluginNativesC(root);
            }
        }
        finally { Environment.SetEnvironmentVariable("FIRE_TEST_PLUGIN", before); Environment.SetEnvironmentVariable("FIRE_TEST_UNITS", null); Environment.SetEnvironmentVariable("FIRE_TEST_JSON", null); }
    }

    /// <summary>A plugin that calls natives in C of a library (they live in the program, `fire_cn_<function>`): one that neither the program nor the library calls
    /// itself (a program that loads plugins exports every native in C of the packages in its file); one the program does not have (it was built before the package
    /// got it) - a readable LibraryException. Linux, the VM, Windows (MinGW, Wine).</summary>
    private static void PluginNativesC(string root)
    {
        string s = Path.Combine(root, "plugc");
        Write(s, "CApi/CApi.fireproj", """{ "name": "CApi", "type": "library", "native": { "language": "c" } }""");
        string header = Write(s, "CApi/native/capi.h", "static inline Value capi_up(Value x) { return Int(x.i + 1); }\nstatic inline Value capi_ten(Value x) { return Int(x.i * 10); }\n");
        Write(s, "CApi/capi.script", "namespace CApi {\n    class Tool {\n        construct() { }\n        int Up(int x) { return __capi_up(x) }\n    }\n}\n");
        Write(s, "CPlug/CPlug.fireproj", """{ "name": "CPlug", "references": [ { "project": "../CApi/CApi.fireproj" } ], "settings": { "linkage": "plugin" } }""");
        string plugSource = Write(s, "CPlug/cplug.script", "#import \"CApi\"\nnamespace CPlug {\n    class P {\n        construct() { }\n        int Ten(int x) { return __capi_ten(x) }\n    }\n}\n");
        Write(s, "CHost/CHost.fireproj", """{ "name": "CHost", "references": [ { "project": "../CApi/CApi.fireproj" } ], "settings": { "linkage": "shared" } }""");
        Write(s, "CHost/main.script", """
            #import "CApi"
            #import "reflection"
            print(new CApi.Tool().Up(1))
            try { Library.Load(Environment.Get("FIRE_TEST_PLUGIN")) } catch (LibraryException e) { print("load: " + e.message); terminate(3) }
            print(Reflect.Call(Reflect.New("CPlug.P", []), "Ten", [4]))
            """);
        string sln = Write(s, "C.firesln", """{ "name": "C", "projects": ["CApi/CApi.fireproj", "CPlug/CPlug.fireproj", "CHost/CHost.fireproj"], "startup": "CHost" }""");
        BuildPlan Plan(string name) { var ws = Workspace.Open(sln); return BuildPlan.Create(ws, ws.FindByName(name)!); }
        Built Plugin(string output, string host, ToolchainDef? toolchain = null, TargetProfile? target = null)
        {
            var config = new NativeConfig { Linkage = "plugin", Host = host };
            target ??= TargetProfile.Host;
            toolchain ??= config.ResolveToolchain(target, "gcc");
            var result = NativeBuilder.BuildSafe(Plan("CPlug").SourceTexts, config, target, toolchain, output, plan: Plan("CPlug"));
            return new Built(result.Ok, result.Output, result.Log, 0, 0);
        }
        string headerBefore = File.ReadAllText(header), plugBefore = File.ReadAllText(plugSource);
        const string Missing = "cannot be loaded: it calls the native function 'capi_hundred' in C, which this program does not export to plugins";
        var mingw = new ToolchainDef { Kind = "gcc", Compiler = "x86_64-w64-mingw32-g++", CCompiler = "x86_64-w64-mingw32-gcc", Std = "c++17", CStd = "c11", Optimization = "-O2" };
        string? wine = new[] { "/usr/lib/wine/wine64", "/usr/bin/wine64", "/usr/bin/wine" }.FirstOrDefault(File.Exists);
        bool windows = ToolchainDetector.Find(mingw) != null && wine != null;
        try
        {
            var hostPlan = Plan("CHost");
            Directory.CreateDirectory(Path.Combine(root, "plugc-host"));
            var host = Build(hostPlan.SourceTexts, "shared", Path.Combine(root, "plugc-host", "host"), plan: hostPlan);
            var plug = host.Ok ? Plugin(Path.Combine(root, "plugc-plug", "cplug"), host.Exe) : new Built(false, "", "", 0, 0);
            string winDir = Path.Combine(root, "plugc-win");
            Directory.CreateDirectory(winDir);
            var winHost = windows ? Build(hostPlan.SourceTexts, "shared", Path.Combine(winDir, "host.exe"), mingw, TargetProfile.Windows, hostPlan) : new Built(false, "", "", 0, 0);
            var winPlug = winHost.Ok ? Plugin(Path.Combine(winDir, "cplug"), winHost.Exe, mingw, TargetProfile.Windows) : new Built(false, "", "", 0, 0);
            Environment.SetEnvironmentVariable("FIRE_TEST_PLUGIN", Path.Combine(root, "plugc-plug", "cplug.so"));
            string native = plug.Ok ? Exec(host.Exe) : "";
            Check("Plugin: ruft ein Native in C einer Library, das weder das Programm noch die Library ruft (fire_cn_ des Programms) - nativ", native == "2\n40\n", $"{native}\n{host.Log}\n{plug.Log}");
            Environment.SetEnvironmentVariable("FIRE_TEST_PLUGIN", Path.Combine(root, "plugc-plug", "cplug.fireplugin"));
            string vm = Vm(hostPlan.SourceTexts, hostPlan);
            Check("Plugin: ein Native in C einer Library - die VM wie nativ", vm == "2\n40\n", vm);
            // the package gets a native the program (built before) does not have
            File.WriteAllText(header, headerBefore + "static inline Value capi_hundred(Value x) { return Int(x.i * 100); }\n");
            File.WriteAllText(plugSource, plugBefore.Replace("__capi_ten(x)", "__capi_hundred(x)"));
            var newer = host.Ok ? Plugin(Path.Combine(root, "plugc-newer", "cplug"), host.Exe) : new Built(false, "", "", 0, 0);
            Environment.SetEnvironmentVariable("FIRE_TEST_PLUGIN", Path.Combine(root, "plugc-newer", "cplug.so"));
            string missing = newer.Ok ? Exec(host.Exe) : "";
            Check("Plugin: ein Native in C, das das Programm nicht hat - eine lesbare LibraryException statt 'could not be loaded'", missing.Contains("load: The plugin library '") && missing.Contains(Missing), $"{missing}\n{newer.Log}");
            if (windows)
            {
                var winNewer = winHost.Ok ? Plugin(Path.Combine(winDir, "cplug2"), winHost.Exe, mingw, TargetProfile.Windows) : new Built(false, "", "", 0, 0);
                Environment.SetEnvironmentVariable("FIRE_TEST_PLUGIN", "cplug.dll");
                string winOut = winHost.Ok && winPlug.Ok ? Exec(winHost.Exe, wine) : "";
                Check("Plugin: ein Native in C einer Library unter Windows (MinGW, Wine)", winOut == "2\n40\n", $"{winOut}\n{winHost.Log}\n{winPlug.Log}");
                Environment.SetEnvironmentVariable("FIRE_TEST_PLUGIN", "cplug2.dll");
                string winMissing = winNewer.Ok ? Exec(winHost.Exe, wine) : "";
                Check("Plugin: ein Native in C, das das Programm nicht hat, unter Windows - dieselbe Meldung", winMissing.Contains("load: The plugin '") && winMissing.Contains(Missing), $"{winMissing}\n{winNewer.Log}");
            }
        }
        finally { File.WriteAllText(header, headerBefore); File.WriteAllText(plugSource, plugBefore); }
    }

    private const string CLibrary = """
        #include <stdint.h>
        #include <stdlib.h>
        #include <string.h>
        #include <stdbool.h>
        #ifdef _WIN32
        #define EXPORT __declspec(dllexport)
        #else
        #define EXPORT
        #endif
        EXPORT int64_t add(int64_t a, int64_t b) { return a + b; }
        EXPORT int neg32(int a) { return -a; }
        EXPORT double scale(double x, float f) { return x * f; }
        EXPORT int64_t len(const char* s) { return (int64_t)strlen(s); }
        EXPORT const char* greet(void) { return "hello from C"; }
        EXPORT bool isodd(int64_t x) { return x % 2 != 0; }
        EXPORT void twice(int64_t* p) { *p = *p * 2; }
        EXPORT uint16_t upper(uint16_t c) { return c >= 97 && c <= 122 ? c - 32 : c; }
        /* release: (Get's release function) - every allocation counted */
        static int live;
        EXPORT int live_count(void) { return live; }
        EXPORT void* obj_new(int64_t v) { int64_t* p = (int64_t*)malloc(sizeof *p); *p = v; live++; return p; }
        EXPORT void* obj_none(void) { return NULL; }
        EXPORT int64_t obj_get(void* p) { return *(int64_t*)p; }
        EXPORT int64_t obj_sum(void* a, int64_t k, void* b) { return *(int64_t*)a + k + *(int64_t*)b; }
        EXPORT void obj_free(void* p) { live--; free(p); }
        EXPORT char* text_dup(const char* s) { char* r = (char*)malloc(strlen(s) + 1); strcpy(r, s); live++; return r; }
        EXPORT char* text_none(void) { return NULL; }
        """;

    private const string UseRelease = """
        var lib = Library.Load(Environment.Get("FIRE_TEST_CLIB"))
        var make = lib.Get("obj_new", "void*(int)", release: "obj_free")
        var get = lib.Get("obj_get", "int(void*)")
        var sum = lib.Get("obj_sum", "int(void*, int, void*)")
        var live = lib.Get("live_count", "int[32]()")
        class Holder { NativeHandle h }
        {
            var a = make(5)
            var b = make(30)
            print(get(a) + " " + sum(a, 7, b) + " " + live() + " " + (a is of NativeHandle) + " " + (a.Address != 0))
        }
        print("block " + live())
        var h = make(7)
        print(get(h) + " " + live())
        delete h
        print("delete " + live())
        try { print(get(h)) } catch (DestroyedException e) { print("use: " + e.message) }
        try { print(h.Address) } catch (DestroyedException e) { print("address: " + e.message) }
        try { delete h; print("delete again: " + live()) } catch (DestroyedException e) { print("delete again: " + e.message) }
        try { print(h is of NativeHandle) } catch (DestroyedException e) { print("is of: " + e.message) }
        var one = make(3)
        try { var two = copy one } catch (ArgumentException e) { print("copy: " + e.message) }
        try { var two = flat one } catch (ArgumentException e) { print("flat: " + e.message) }
        {
            var hb = new Holder()
            hb.h = make(4)
            try { var hc = copy hb } catch (ArgumentException e) { print("deep: " + e.message) }
            var hf = flat hb
            print("flat shares " + (hf.h == hb.h) + " " + live())
        }
        delete one
        print("no copies " + live())
        {
            var box = new Holder()
            box.h = make(9)
            print("holder " + get(box.h) + " " + live())
        }
        print("holder gone " + live())
        var dup = lib.Get("text_dup", "string(string)", release: "obj_free")
        print(dup("copied") + " " + live())
        print(lib.Get("text_none", "string()", release: "obj_free")() == undefined)
        print(lib.Get("obj_none", "void*()", release: "obj_free")() == undefined)
        var raw = lib.Get("obj_new", "void*(int)")(1)
        print((raw is of int) + " " + live())
        lib.Get("obj_free", "void(void*)")(raw)
        try { lib.Get("obj_new", "void*(int)", release: "nope") } catch (LibraryException e) { print("rel: " + e.message.Replace(lib.Path, "LIB")) }
        try { lib.Get("add", "int(int, int)", release: "obj_free") } catch (LibraryException e) { print("kind: " + e.message) }
        var g = make(11)
        print("end " + live())
        """;

    /// <summary>The output of <see cref="UseRelease"/> - the same in the VM, natively (source, static, under ASan) and under Wine.</summary>
    private const string ReleaseExpected = "5 42 2 True True\nblock 0\n7 1\ndelete 0\nuse: Access to a destroyed object.\naddress: Access to a destroyed object.\n"
        + "delete again: 0\nis of: Access to a destroyed object.\n"
        + "copy: 'NativeHandle' cannot be copied ([NoCopy]) - an instance of it exists only once; share the reference or pass it on with 'take'.\n"
        + "flat: 'NativeHandle' cannot be copied ([NoCopy]) - an instance of it exists only once; share the reference or pass it on with 'take'.\n"
        + "deep: 'NativeHandle' cannot be copied ([NoCopy]) - 'copy' reaches an instance of it (a field or an element of the copied value); it exists only once, share the reference instead.\n"
        + "flat shares True 2\nno copies 0\nholder 9 1\nholder gone 0\ncopied 0\nTrue\nTrue\nTrue 1\n"
        + "rel: The library 'LIB' has no function 'nope' (release:).\n"
        + "kind: release: frees what a function returns - the signature 'int(int, int)' returns no pointer and no string.\nend 1\n";

    private const string UseLibrary = """
        var path = Environment.Get("FIRE_TEST_CLIB")
        var lib = Library.Load(path)
        var add = lib.Get("add", "int(int, int)")
        print(add(40, 2))
        var neg = lib.Get("neg32", "int[32](int[32])")
        print(neg(7))
        var scale = lib.Get("scale", "float(float, float[32])")
        print(scale(2.5, 4.0))
        print(lib.Get("len", "int(string)")("hello"))
        print(lib.Get("greet", "string()")())
        print(lib.Get("isodd", "bool(int)")(7))
        print(lib.Get("upper", "char(char)")('q'))
        int x = 21
        var twice = lib.Get("twice", "void(int*)")
        unsafe { twice(&x) }
        print(x)
        print(lib.Path == path)
        try { Library.Load("/nonexistent/libnope.so") } catch (LibraryException e) { print("load: " + e.message) }
        try { lib.Get("nope", "int()") } catch (LibraryException e) { print("symbol: " + e.message.Replace(path, "LIB")) }
        try { lib.Get("add", "int(int,") } catch (LibraryException e) { print("sig: " + e.message) }
        var s = "int(" + "int)"
        try { var f = lib.Get("add", s); print("made") } catch (LibraryException e) { print("dyn: " + e.message) }
        """;

    /// <summary>`Library.Load(path)` / `lib.Get(name, signature)` (ROADMAP 9, phase D): a C library built here, loaded while the program runs - in the VM, natively
    /// (source, static) and for Windows (MinGW, under Wine); the errors (no library, no function, no signature) are the same everywhere; a signature made at run time
    /// works in the VM, the native build says it has no thunk for it.</summary>
    private static void LibraryLoad(string root)
    {
        string dir = Path.Combine(root, "clib");
        string cFile = Write(dir, "t.c", CLibrary);
        string so = Path.Combine(dir, "libt.so");
        var (ok, text) = NativeBuilderRun("gcc", $"-shared -fPIC -o \"{so}\" \"{cFile}\"", dir);
        if (!ok) { Check("Library: die C-Bibliothek des Tests baut", false, text); return; }
        string? before = Environment.GetEnvironmentVariable("FIRE_TEST_CLIB");
        Environment.SetEnvironmentVariable("FIRE_TEST_CLIB", so);
        try
        {
            const string common = "42\n-7\n10\n5\nhello from C\nTrue\nQ\n42\nTrue\nload: The library '/nonexistent/libnope.so' could not be loaded.\nsymbol: The library 'LIB' has no function 'nope'.\n"
                + "sig: The signature 'int(int,' is not valid: write the result type and the parameter types in C order, like \"int[32](string, float)\" (bool, int, int[8|16|32|64], float, float[32], char, string, pointers with *, void).\n";
            const string native = common + "dyn: The signature 'int(int)' is not known to this native build: pass it to Get as a text constant.\n";
            string vm = Vm(new[] { UseLibrary });
            Check("Library: Load/Get in der VM (int, int[32], float[32], string hin und zurueck, bool, char, Zeiger; die Fehler)", vm == common + "made\n", vm);
            var src = Build(new[] { UseLibrary }, "source", Path.Combine(root, "clib-source"));
            string srcOut = src.Ok ? Exec(src.Exe) : "";
            Check("Library: nativ wie die VM (ein Thunk je Signatur, die ein Text des Programms ist; eine zur Laufzeit gebaute Signatur sagt, dass sie fehlt)", srcOut == native, $"{srcOut}\n{src.Log}");
            var st = Build(new[] { UseLibrary }, "static", Path.Combine(root, "clib-static"));
            string stOut = st.Ok ? Exec(st.Exe) : "";
            Check("Library: mit linkage static (Library in core, die Thunks im Programm)", stOut == native, $"{stOut}\n{st.Log}");

            // ---- release: (what the library returns, freed by its own function): a NativeHandle follows ownership, a string is copied and freed at once ----
            string relVm = Vm(new[] { UseRelease });
            Check("Library release: in der VM (NativeHandle: delete, Ende des Blocks und des Besitzers geben frei, einmal; danach DestroyedException; String kopiert und freigegeben)",
                relVm == ReleaseExpected, relVm);
            var relSrc = Build(new[] { UseRelease }, "source", Path.Combine(root, "rel-source"));
            string relSrcOut = relSrc.Ok ? Exec(relSrc.Exe) : "";
            Check("Library release: nativ (source) wie die VM", relSrcOut == ReleaseExpected, $"{relSrcOut}\n{relSrc.Log}");
            var relSt = Build(new[] { UseRelease }, "static", Path.Combine(root, "rel-static"));
            string relStOut = relSt.Ok ? Exec(relSt.Exe) : "";
            Check("Library release: nativ (static) wie die VM", relStOut == ReleaseExpected, $"{relStOut}\n{relSt.Log}");
            // under AddressSanitizer (LeakSanitizer at the end): no leak, no double free - the library's malloc/free are intercepted too
            const string asanFlags = "-fsanitize=address,undefined";
            var asanTool = new ToolchainDef { Kind = "gcc", Compiler = "g++", CCompiler = "gcc", Std = "c++17", CStd = "c11", Optimization = "-O1 -g", Strip = false, Args = new() { asanFlags }, Libs = new() { asanFlags } };
            var relAsan = Build(new[] { UseRelease }, "source", Path.Combine(root, "rel-asan"), asanTool);
            string relAsanOut = relAsan.Ok ? Exec(relAsan.Exe) : "";
            Check("Library release: nativ unter AddressSanitizer (kein Leck, keine doppelte Freigabe)", relAsanOut == ReleaseExpected, $"{relAsanOut}\n{relAsan.Log}");

            var mingw = new ToolchainDef { Kind = "gcc", Compiler = "x86_64-w64-mingw32-g++", CCompiler = "x86_64-w64-mingw32-gcc", Std = "c++17", CStd = "c11", Optimization = "-O2" };
            string? wine = new[] { "/usr/lib/wine/wine64", "/usr/bin/wine64", "/usr/bin/wine" }.FirstOrDefault(File.Exists);
            if (ToolchainDetector.Find(mingw) != null && wine != null)
            {
                string winDir = Path.Combine(root, "clib-win");
                Directory.CreateDirectory(winDir);
                var (dllOk, dllText) = NativeBuilderRun("x86_64-w64-mingw32-gcc", $"-shared -o \"{Path.Combine(winDir, "t.dll")}\" \"{cFile}\"", dir);
                var win = Build(new[] { UseLibrary }, "static", Path.Combine(winDir, "use.exe"), mingw, TargetProfile.Windows);
                _winePrefix ??= Environment.GetEnvironmentVariable("WINEPREFIX") ?? Path.Combine(root, "wineprefix");
                Environment.SetEnvironmentVariable("FIRE_TEST_CLIB", "t.dll");
                string winOut = dllOk && win.Ok ? Exec(win.Exe, wine) : "";
                Check("Library: Windows (MinGW, LoadLibrary) unter Wine wie nativ", winOut == native, $"{winOut}\n{dllText}\n{win.Log}");
                var relWin = Build(new[] { UseRelease }, "static", Path.Combine(winDir, "rel.exe"), mingw, TargetProfile.Windows);
                string relWinOut = dllOk && relWin.Ok ? Exec(relWin.Exe, wine) : "";
                Check("Library release: Windows (MinGW, die Freigabe der DLL) unter Wine wie die VM", relWinOut == ReleaseExpected, $"{relWinOut}\n{relWin.Log}");
            }
        }
        finally { Environment.SetEnvironmentVariable("FIRE_TEST_CLIB", before); }
    }

    private static (bool Ok, string Text) NativeBuilderRun(string exe, string arguments, string dir)
    {
        var psi = new ProcessStartInfo(exe, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = dir };
        using var p = Process.Start(psi)!;
        string text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode == 0, text);
    }

    /// <summary>Linkage "shared" on Windows (MinGW, under Wine): the runtime and every library a DLL next to the program, the program like the VM; two programs share the
    /// DLLs; a DLL with another interface next to the program is the readable error at the start (exit code 70), like on Linux.</summary>
    private static void WindowsShared(string root, ToolchainDef mingw, string wine, Func<BuildPlan> makePlan, string libSource)
    {
        var plan = makePlan();
        string dir1 = Path.Combine(root, "win-shared1");
        Directory.CreateDirectory(dir1);
        var sh1 = Build(plan.SourceTexts, "shared", Path.Combine(dir1, "app.exe"), mingw, TargetProfile.Windows, plan);
        string vm = Vm(plan.SourceTexts, plan);
        var dlls = Directory.GetFiles(dir1, "libfire_*.dll").Select(Path.GetFileName).ToList();
        string out1 = sh1.Ok ? Exec(sh1.Exe, wine) : "";
        Check("MinGW: linkage shared fuer Windows - die Runtime und jede Library eine DLL neben dem Programm, unter Wine wie die VM",
            sh1.Ok && out1 == vm && dlls.Any(f => f!.StartsWith("libfire_rt_")) && dlls.Any(f => f!.StartsWith("libfire_mLib_")) && dlls.Any(f => f!.StartsWith("libfire_mcore_")) && !sh1.Log.Contains("only available"),
            $"VM: {vm}\nnativ: {out1}\n{string.Join(" ", dlls)}\n{sh1.Log}");
        // (threads: the thread-local state of the runtime through fire_tl(); reflection, JSON and an exception in a thread through the DLLs)
        const string threads = """
            #import "json"
            #import "reflection"
            class Box { int n = 0 }
            var counter = 0
            var results = new List<string>()
            fire {
                try { throw new ArgumentException("in thread") } catch (ArgumentException e) { sync global { results.Add("caught " + e.message) } }
                sync global { counter = counter + 1 }
            }
            fire {
                var b = new Box()
                Reflect.Set(b, "n", 41)
                sync global { results.Add(Json.Serialize(b)); counter = counter + 1 }
            }
            while (counter < 2) { sync globals }
            if (results[0].StartsWith("{")) { var t = results[0]; results[0] = results[1]; results[1] = t }
            print(results[0] + " " + results[1] + " " + counter)
            """;
        foreach (var d in new[] { "win-sharedA", "win-sharedB", "win-shared2" }) Directory.CreateDirectory(Path.Combine(root, d));
        var shA = Build(new[] { threads }, "shared", Path.Combine(root, "win-sharedA", "a.exe"), mingw, TargetProfile.Windows);
        var shB = Build(new[] { threads.Replace("41", "42") }, "shared", Path.Combine(root, "win-sharedB", "b.exe"), mingw, TargetProfile.Windows);
        string outA = shA.Ok ? Exec(shA.Exe, wine) : "", outB = shB.Ok ? Exec(shB.Exe, wine) : "";
        Check("MinGW: shared - Threads (thread-lokaler Zustand ueber fire_tl), Reflection, JSON; ein zweites Programm baut keine DLL, beide wie die VM",
            shA.Ok && shB.Ok && ModuleLines(shB.Log, "built in").Count == 0 && outA == Vm(new[] { threads }) && outB == Vm(new[] { threads.Replace("41", "42") }),
            $"{outA}\n{outB}\n{shA.Log}\n{shB.Log}");
        // another layout of Lib (a field more): its DLL put in place of the one the first program was built with
        string before = File.ReadAllText(libSource);
        try
        {
            File.WriteAllText(libSource, before.Replace("        int n = 0\n", "        int n = 0\n        int extra = 1\n"));
            var plan2 = makePlan();
            var sh2 = Build(plan2.SourceTexts, "shared", Path.Combine(root, "win-shared2", "app.exe"), mingw, TargetProfile.Windows, plan2);
            string? newLib = sh2.Ok ? Directory.GetFiles(Path.Combine(root, "win-shared2"), "libfire_mLib_*.dll").FirstOrDefault() : null;
            string? oldLib = Directory.GetFiles(dir1, "libfire_mLib_*.dll").FirstOrDefault();
            string mismatch = "";
            if (newLib != null && oldLib != null) { File.Copy(newLib, oldLib, true); mismatch = Exec(sh1.Exe, wine); }
            Check("MinGW: shared - eine DLL mit einer anderen Schnittstelle neben dem Programm ist beim Start ein lesbarer Fehler (Exitcode 70)",
                mismatch.Contains("does not fit this program") && mismatch.Contains("(exit 70)") && mismatch.Contains("'Lib'"), mismatch + "\n" + sh2.Log);
        }
        finally { File.WriteAllText(libSource, before); }
    }

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
