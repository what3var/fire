using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using fire.Compiler;
using fire.Editor;
using fire.Native;
using fire.Package.Manager;
using fire.Runtime;

/// <summary>
/// The VM in C on Windows (docs/ROADMAP_C_PORT.md, M1 (7); docs/spec/VM_C.md "Windows"): `fire.exe` built with MinGW from the sources forge carries
/// (CVm.WriteSources for Windows), the libraries of the packages and of graphics/windows built the same way, everything run under Wine - the command line (exit codes,
/// UTF-16 arguments and paths, the variant of other widths, FIRE_STDOUT_LINES through a pipe), a sample of the corpus (tests/cases/native, compared with .expected),
/// a packed program (the payload with its DLLs unpacked into the cache, `forge build` with FIRE_VM_EXE), the display of the host (FIRE_DISPLAY_SHM written by
/// fire.exe, read by HostDisplayFile - the code of spark), `__HostCommand` over stdin, FIRE_PRINT_RECORDS, and the device host (FIRE_DEVICE_HOST, Winsock).
/// FIRE_TESTS_ONLY=cvmwin; FIRE_CVMWIN_CASES=<text>: only the cases whose file names contain it; FIRE_CVMWIN_LIMIT=<n>: at most n cases. Skipped without Wine
/// or MinGW (the output says which is missing).
/// </summary>
static class CVmWindowsTests
{
    private static int _failures;
    private static string _root = "", _prefix = "", _bin = "", _libs = "";
    private static string _wine = "", _mingw = "", _mingwCxx = "";
    private static string? _display;
    private static (string Program, IReadOnlyList<string> Prefix) _forge;

    private static void Check(string title, bool ok, string? detail = null)
    {
        Console.WriteLine($"[{(ok ? "OK" : "FEHLER")}] C-VM unter Windows (Wine): {title}");
        if (!ok)
        {
            Interlocked.Increment(ref _failures);
            if (detail != null) Console.WriteLine("    " + (detail.Length > 6000 ? detail.Substring(0, 6000) + " ..." : detail).Replace("\n", "\n    "));
        }
    }

    private static bool HasProgram(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Any(d => d.Length > 0 && File.Exists(Path.Combine(d, name)));

    /// <summary>A path of this machine as Wine sees it (the drive Z: is /).</summary>
    private static string Win(string path) => "Z:" + Path.GetFullPath(path).Replace('/', '\\');

    public static int Run()
    {
        if (!OperatingSystem.IsLinux()) { Console.WriteLine("(C-VM unter Windows: uebersprungen - nur unter Linux mit Wine)"); return 0; }
        _wine = new[] { "/usr/lib/wine/wine64", "/usr/bin/wine64", "/usr/bin/wine" }.FirstOrDefault(File.Exists) ?? "";
        _mingw = HasProgram("x86_64-w64-mingw32-gcc") ? "x86_64-w64-mingw32-gcc" : "";
        _mingwCxx = HasProgram("x86_64-w64-mingw32-g++") ? "x86_64-w64-mingw32-g++" : "";
        if (_wine.Length == 0 || _mingw.Length == 0) { Console.WriteLine("(C-VM unter Windows: uebersprungen - Wine oder MinGW (x86_64-w64-mingw32-gcc) fehlen)"); return 0; }
        if (ForgeProcess.Locate(AppContext.BaseDirectory) is not { } forge) { Check("forge neben den Tests", false, AppContext.BaseDirectory); return _failures; }
        _forge = forge;
        _root = Path.Combine(Path.GetTempPath(), "fire-cvmwin-" + Guid.NewGuid().ToString("N"));
        _prefix = Path.Combine(_root, "wineprefix");
        _bin = Path.Combine(_root, "bin");
        _libs = Path.Combine(_root, "libs");
        Directory.CreateDirectory(_bin);
        Directory.CreateDirectory(_libs);
        Process? xvfb = null;
        try
        {
            xvfb = StartXvfb();
            var total = Stopwatch.StartNew();
            // the prefix, the VM (and its variant of 32-bit ints) and the libraries the checks need come up together
            var boot = Task.Run(() => RunWine("wineboot", new[] { "--init" }, _root, timeoutMs: 300000));
            var vms = CVm.AllVariants.Select(v => Task.Run(() => BuildVm(v.IntWidth, v.FloatWidth))).ToList();
            var io = Task.Run(() => Library("io"));
            boot.Wait();
            Check($"die VM fuer Windows baut (MinGW, die Quellen von forge: CVm.WriteSources fuer windows) - fire.exe und seine {vms.Count - 1} Varianten der Breiten",
                vms.All(v => v.Result.Ok), string.Join("\n", vms.Where(v => !v.Result.Ok).Select(v => v.Result.Log)));
            if (!vms[0].Result.Ok) return _failures;
            Console.WriteLine($"    Wine-Prefix, VM und io: {total.Elapsed.TotalSeconds:0.0} s");
            CommandLine(io.Result);
            Packed(io.Result);
            HostDisplay();
            Devices();
            Corpus();
            Console.WriteLine($"    zusammen {total.Elapsed.TotalSeconds:0.0} s");
        }
        catch (Exception ex) { Check("ohne Ausnahme", false, ex.ToString()); }
        finally
        {
            // (the wineserver of this prefix - only this one)
            try { RunWine("wineserver", new[] { "-k" }, _root, timeoutMs: 30000, server: true); } catch (Exception) { }
            if (xvfb != null) { try { if (!xvfb.HasExited) xvfb.Kill(); xvfb.WaitForExit(5000); } catch (Exception) { } xvfb.Dispose(); }
            if (Environment.GetEnvironmentVariable("FIRE_CVMWIN_KEEP") is not { Length: > 0 })
                try { Directory.Delete(_root, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.WriteLine($"    ({_root} bleibt: {ex.Message})"); }
            else Console.WriteLine($"    (bleibt: {_root})");
        }
        Console.WriteLine(_failures == 0 ? "Alle Pruefungen der C-VM unter Windows bestanden." : $"FEHLER: {_failures} Pruefung(en) der C-VM unter Windows fehlgeschlagen.");
        return _failures;
    }

    // ---- the tools ----------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>An X server of its own for the windows of the cases (Wine draws them with X11); null without Xvfb - those cases then have no screen.</summary>
    private static Process? StartXvfb()
    {
        if (!HasProgram("Xvfb")) return null;
        for (int n = 90 + Random.Shared.Next(400); n < 1000; n += 7)
        {
            if (File.Exists($"/tmp/.X{n}-lock")) continue;
            var p = Process.Start(new ProcessStartInfo("Xvfb", $":{n} -screen 0 1024x768x24 -nolisten tcp") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true });
            if (p == null) return null;
            p.ErrorDataReceived += (_, _) => { };
            p.BeginErrorReadLine();
            Thread.Sleep(700);
            if (!p.HasExited) { _display = ":" + n; return p; }
            p.Dispose();
        }
        return null;
    }

    private sealed record Result(string Out, string Err, int Code, bool TimedOut)
    {
        /// <summary>stdout with the line ends of this side (the C runtime of Windows writes \r\n in text mode).</summary>
        public string Text => Out.Replace("\r\n", "\n");
        public override string ToString() => $"code {Code}{(TimedOut ? " (timeout)" : "")}\n--- out\n{Out}\n--- err\n{Err}";
    }

    private static ProcessStartInfo WineInfo(string program, IEnumerable<string> args, string cwd, IReadOnlyDictionary<string, string>? env, bool server = false)
    {
        var info = new ProcessStartInfo(server ? Path.Combine(Path.GetDirectoryName(_wine)!, "wineserver") : _wine)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = cwd,
        };
        if (!server) info.ArgumentList.Add(program);
        foreach (string a in args) info.ArgumentList.Add(a);
        info.Environment["WINEPREFIX"] = _prefix;
        info.Environment["WINEDEBUG"] = "-all";
        // (a program that crashes ends, no winedbg that waits; wineboot installs no Mono and no Gecko - forge.exe of .NET needs mscoree)
        info.Environment["WINEDLLOVERRIDES"] = program == "wineboot" ? "mscoree,mshtml=;winedbg.exe=d" : "winedbg.exe=d";
        info.Environment["LANG"] = "C.UTF-8";
        info.Environment["FIRE_SYSTEM_THEME"] = "off";
        info.Environment["FIRE_PKG_PATH"] = Win(_libs);
        info.Environment.Remove("TMPDIR");   // (Wine of Ubuntu aborts with TMPDIR set: Program.cs)
        info.Environment.Remove("FIRE_DISPLAY_SHM");
        info.Environment.Remove("FIRE_DEVICE_HOST");
        info.Environment.Remove("FIRE_DEBUG");
        if (_display != null) info.Environment["DISPLAY"] = _display; else info.Environment.Remove("DISPLAY");
        if (env != null) foreach (var (k, v) in env) info.Environment[k] = v;
        return info;
    }

    /// <summary>Runs a program under Wine to its end (a path of this machine or a program Wine knows).</summary>
    private static Result RunWine(string program, IEnumerable<string> args, string cwd, IReadOnlyDictionary<string, string>? env = null, string? stdin = null, int timeoutMs = 60000, bool server = false)
    {
        using var p = Process.Start(WineInfo(program, args, cwd, env, server))!;
        var err = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEndAsync();
        try
        {
            if (stdin != null) p.StandardInput.Write(stdin);
            p.StandardInput.Close();
        }
        catch (IOException) { }
        bool ended = p.WaitForExit(timeoutMs);
        if (!ended) { try { p.Kill(true); } catch (Exception) { } p.WaitForExit(5000); }
        return new Result(output.Wait(5000) ? output.Result : "", err.Wait(5000) ? err.Result : "", ended ? p.ExitCode : -1, !ended);
    }

    private static Result Fire(IEnumerable<string> args, string cwd, IReadOnlyDictionary<string, string>? env = null, string? stdin = null, int timeoutMs = 60000) =>
        RunWine(Path.Combine(_bin, "fire.exe"), args, cwd, env, stdin, timeoutMs);

    private static (bool Ok, string Log) Run(string exe, string args, string cwd)
    {
        var info = new ProcessStartInfo(exe, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = cwd };
        using var p = Process.Start(info)!;
        var err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode == 0, $"{exe} {args}\n{output}{err.Result}");
    }

    /// <summary>fire.exe (or a variant) in bin/, built from the sources forge carries - for Windows (its platform package, ws2_32).</summary>
    private static (bool Ok, string Log) BuildVm(int intWidth, int floatWidth)
    {
        string src = Path.Combine(_root, "vmsrc-" + CVm.VariantName(intWidth, floatWidth));
        string main = CVm.WriteSources(src, intWidth, floatWidth, TargetProfile.Windows);
        string exe = Path.Combine(_bin, CVm.VariantName(intWidth, floatWidth) + ".exe");
        return Run(_mingw, $"-O2 -w \"{main}\" -o \"{exe}\" -lws2_32", src);
    }

    private static readonly ConcurrentDictionary<string, Lazy<(string? Path, string Log)>> Built = new(StringComparer.Ordinal);

    /// <summary>The library of an import (or "fire_gfx": graphics and windows) for Windows in libs/, built once with MinGW like PackageLibrary.Ensure does on
    /// Windows; null with the log when it cannot be built.</summary>
    private static (string? Path, string Log) Library(string import) => Built.GetOrAdd(import, i => new Lazy<(string?, string)>(() => BuildLibrary(i))).Value;

    private static (string? Path, string Log) BuildLibrary(string importName)
    {
        var target = TargetProfile.Windows;
        string work = Path.Combine(_root, "libbuild", importName);
        Directory.CreateDirectory(work);
        NativeRuntimeFiles.WriteTo(work, target.Native.Platform);
        string source, dll;
        bool isC;
        if (importName == "fire_gfx")
        {
            var graphics = PackageStore.Default.FindImport("graphics");
            var windows = PackageStore.Default.FindImport("windows");
            if (graphics == null || windows == null) return (null, "graphics/windows are not installed");
            isC = true;
            source = Path.Combine(work, "wrapper.c");
            File.WriteAllText(source, PackageLibrary.GenerateGraphicsWrapper(graphics, windows, target));
            dll = Path.Combine(_libs, "fire_gfx.dll");
        }
        else
        {
            var import = PackageStore.Default.FindImport(importName);
            if (import?.Import.Native == null) return (null, $"the import '{importName}' has no natives here");
            if (!import.Import.Native.SupportsAny(PackageLibrary.PlatformKeysOf(target))) return (null, $"the package '{import.Package.Name}' has no natives for Windows");
            isC = import.Import.Native.IsC;
            if (!isC && _mingwCxx.Length == 0) return (null, "no MinGW C++ compiler");
            source = Path.Combine(work, isC ? "wrapper.c" : "wrapper.cpp");
            File.WriteAllText(source, isC ? PackageLibrary.GenerateCWrapper(import, target) : PackageLibrary.GenerateWrapper(import, target));
            dll = Path.Combine(_libs, WindowsName(PackageLibrary.FileNameFor(import)));
        }
        var links = File.ReadLines(source).Where(l => l.StartsWith("// fire-link: ", StringComparison.Ordinal)).Select(l => "-l" + l.Substring("// fire-link: ".Length).Trim())
            .Concat(target.Native.LinkLibs).Distinct();
        string args = $"-std={(isC ? "c11" : "c++17")} -O1 -w -shared -static-libgcc -static-libstdc++ {string.Join(" ", target.Native.CompileArgs)} \"{source}\" -I\"{work}\" -I\"{Path.Combine(work, "abi")}\" -o \"{dll}\" {string.Join(" ", links)}";
        var (ok, log) = Run(isC ? _mingw : _mingwCxx, args, work);
        return ok && File.Exists(dll) ? (dll, log) : (null, log);
    }

    /// <summary>The name a library of this machine (libfire_pkg_x.so) has on Windows (fire_pkg_x.dll) - the VM maps the names of a program file the same way (vm_pkg.c).</summary>
    private static string WindowsName(string file)
    {
        string stem = Path.GetFileNameWithoutExtension(file);
        if (stem.StartsWith("lib", StringComparison.Ordinal) && !file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) stem = stem.Substring(3);
        return stem + ".dll";
    }

    private static Dictionary<string, string>? _importOfFile;

    /// <summary>The import a library file of this machine belongs to ("fire_gfx" for graphics and windows); null: none known.</summary>
    private static string? ImportOf(string libraryPath)
    {
        string name = Path.GetFileName(libraryPath);
        if (name.Contains("fire_gfx", StringComparison.Ordinal)) return "fire_gfx";
        if (_importOfFile == null)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var package in PackageStore.Default.Installed())
                foreach (var import in package.Manifest.Imports)
                    if (import.Native != null) map.TryAdd(PackageLibrary.FileNameFor(new InstalledImport(package, import)), import.Name);
            _importOfFile = map;
        }
        return _importOfFile.TryGetValue(name, out var i) ? i : null;
    }

    /// <summary>`forge bytecode` of a source into a program file (the libraries it needs on this machine - their imports are built for Windows).</summary>
    private static (bool Ok, string Fbc, List<string> Imports, string Log) Bytecode(string source, string fbc, string mode = "RELEASE", IEnumerable<string>? extra = null)
    {
        var args = new List<string> { "bytecode", source, "-m", mode, "-o", fbc };
        if (extra != null) args.AddRange(extra);
        var built = ForgeProcess.Run(args, new ForgeSink(), CancellationToken.None, Path.GetDirectoryName(source), _forge);
        var imports = built.Outputs.Where(o => o.Kind == "library").Select(o => ImportOf(o.Path) ?? "?" + Path.GetFileName(o.Path)).Distinct().ToList();
        return (built.Ok && File.Exists(fbc), fbc, imports, built.Error + "\n" + built.Log);
    }

    private static string Source(string name, string text)
    {
        string dir = Path.Combine(_root, "src");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    // ---- the command line --------------------------------------------------------------------------------------------------------------------------------

    private static void CommandLine((string? Path, string Log) io)
    {
        var version = Fire(new[] { "version" }, _root);
        Check("fire.exe version: das Format, die Breiten, win-x64", version.Code == 0 && version.Text.Contains("int 64, float 64, win-x64"), version.ToString());
        var help = Fire(new[] { "help" }, _root);
        var none = Fire(new[] { "run" }, _root);
        var missing = Fire(new[] { Win(Path.Combine(_root, "nothing.fbc")) }, _root);
        var unknown = Fire(new[] { "--nope", "x.fbc" }, _root);
        Check("Exitcodes der Befehlszeile: help 0, run ohne Programm 2, eine fehlende Datei 2, eine unbekannte Option 2",
            help.Code == 0 && help.Text.Contains("fire run") && none.Code == 2 && missing.Code == 2 && missing.Err.Contains("cannot read") && unknown.Code == 2,
            $"{help}\n{none}\n{missing}\n{unknown}");

        string args = Source("args.fire", """
            print("args " + Environment.Args.length)
            for (var i = 0; i < Environment.Args.length; i = i + 1) { print("[" + Environment.Args[i] + "] " + Environment.Args[i].length) }
            print("päh 日本 😀")
            terminate(3)
            """);
        var (ok, fbc, _, log) = Bytecode(args, Path.Combine(_root, "src", "args.fbc"));
        if (!ok) { Check("args.fbc", false, log); return; }
        string[] given = { "a", "b c", "ä", "日本", "😀", "q\"x", "back\\slash\\", "" };
        string want = "args 8\n[a] 1\n[b c] 3\n[ä] 1\n[日本] 2\n[😀] 2\n[q\"x] 3\n[back\\slash\\] 11\n[] 0\npäh 日本 😀\n";
        var run = Fire(new[] { "run", Win(fbc), "--" }.Concat(given), _root);
        Check("Environment.Args: nur die Argumente des Programms (nicht 'run x.fbc'), UTF-16 der Befehlszeile als UTF-8 - Leerzeichen, Umlaute, CJK, Emoji, Anfuehrungszeichen, Backslashes, leer; terminate(3)",
            run.Code == 3 && run.Text == want, run.ToString());
        var plain = Fire(new[] { "run", Win(fbc), "x", "y z" }, _root);
        Check("fire run x.fbc a b (ohne --): die Argumente nach dem Programm", plain.Code == 3 && plain.Text.StartsWith("args 2\n[x] 1\n[y z] 3\n"), plain.ToString());

        // a program file in a folder with other letters, and two programs in one process (the arguments after --)
        string odd = Path.Combine(_root, "Ördner 日本");
        Directory.CreateDirectory(odd);
        string oddFbc = Path.Combine(odd, "prögram.fbc");
        File.Copy(fbc, oddFbc);
        var oddRun = Fire(new[] { "run", Win(oddFbc), "ü" }, odd);
        var two = Fire(new[] { Win(fbc), Win(oddFbc), "--", "z" }, _root);
        Check("ein Programm in einem Ordner mit Umlauten und CJK (die Pfade UTF-8, geoeffnet mit _wfopen)", oddRun.Code == 3 && oddRun.Text.StartsWith("args 1\n[ü] 1\n"), oddRun.ToString());
        Check("zwei Programme in einem Prozess, die Argumente nach --", two.Code == 3 && two.Text == "args 1\n[z] 1\npäh 日本 😀\nargs 1\n[z] 1\npäh 日本 😀\n", two.ToString());

        // a program of 32-bit ints: fire.exe starts fire-i32.exe beside it with the same command line (CreateProcessW), ends with its code
        string i32 = Source("i32.fire", """
            #intwidth 32
            var x = 2147483647
            print("max " + x + " args " + Environment.Args.length + " " + Environment.Args[0])
            terminate(5)
            """);
        var (ok32, fbc32, _, log32) = Bytecode(i32, Path.Combine(_root, "src", "i32.fbc"));
        var r32 = ok32 ? Fire(new[] { "run", Win(fbc32), "a b", "ö" }, _root) : new Result("", log32, -1, false);
        Check("#intwidth 32: fire.exe laesst fire-i32.exe laufen (dieselben Argumente, ihr Exitcode)", r32.Code == 5 && r32.Text == "max 2147483647 args 2 a b\n", r32.ToString());

        // FIRE_STDOUT_LINES=1 through a pipe: the first line comes before the program reads its input (the C runtime of Windows has no line buffering)
        if (io.Path == null) { Check("die Bibliothek von io fuer Windows baut", false, io.Log); return; }
        string lines = Source("lines.fire", """
            #import "io"
            print("first")
            var line = IO.Stdio.ReadLine()
            print("second " + line)
            """);
        var (okl, fbcl, impl, logl) = Bytecode(lines, Path.Combine(_root, "src", "lines.fbc"));
        if (!okl) { Check("lines.fbc", false, logl); return; }
        using (var p = Process.Start(WineInfo(Path.Combine(_bin, "fire.exe"), new[] { "run", Win(fbcl) }, _root, new Dictionary<string, string> { ["FIRE_STDOUT_LINES"] = "1" }))!)
        {
            var got = new List<string>();
            var first = new ManualResetEventSlim();
            p.OutputDataReceived += (_, e) => { if (e.Data == null) return; lock (got) got.Add(e.Data.TrimEnd('\r')); if (e.Data.StartsWith("first")) first.Set(); };
            p.BeginOutputReadLine();
            var err = p.StandardError.ReadToEndAsync();
            bool early = first.Wait(30000);
            p.StandardInput.WriteLine("über");
            p.StandardInput.Close();
            bool ended = p.WaitForExit(30000);
            if (!ended) try { p.Kill(true); } catch (Exception) { }
            p.WaitForExit();
            Check("FIRE_STDOUT_LINES=1: die erste Zeile kommt durch die Pipe, bevor das Programm liest; die Eingabe (UTF-8) kommt an",
                early && ended && p.ExitCode == 0 && got.SequenceEqual(new[] { "first", "second über" }), $"imports {string.Join(",", impl)} early {early} ended {ended}: {string.Join(" | ", got)}\n{err.Result}");
        }
    }

    // ---- packed programs ----------------------------------------------------------------------------------------------------------------------------------

    private static void Packed((string? Path, string Log) io)
    {
        // `forge build` (engine vm) with FIRE_VM_EXE: fire.exe with the program behind it - every argument is the program's, the exit code of terminate, an
        // exception nothing catches
        string hello = Source("hello.fire", """
            print("hello " + Environment.Args.length)
            for (var i = 0; i < Environment.Args.length; i = i + 1) { print(Environment.Args[i]) }
            if (Environment.Args.length > 0 && Environment.Args[0] == "throw") { throw new ArgumentException("thrown") }
            terminate(6)
            """);
        string exe = Path.Combine(_root, "src", "hello.exe");
        var env = new Dictionary<string, string> { ["FIRE_VM_EXE"] = Path.Combine(_bin, "fire.exe") };
        // (`--engine vm -t windows` is refused on Linux - the VM is packed for the machine forge runs on; FIRE_VM_EXE names the VM of Windows, a program
        // without natives of packages needs nothing else of that machine)
        var (ok, log) = ForgeBuild(hello, exe, env);
        if (!ok) { Check("forge build (engine vm) mit FIRE_VM_EXE=fire.exe", false, log); }
        else
        {
            var run = RunWine(exe, new[] { "run", "--stats", "ä b" }, _root);
            Check("ein gepacktes Programm (forge build, FIRE_VM_EXE=fire.exe): jedes Argument gehoert dem Programm, terminate(6)", run.Code == 6 && run.Text == "hello 3\nrun\n--stats\nä b\n", run.ToString());
            var thrown = RunWine(exe, new[] { "throw" }, _root);
            Check("ein gepacktes Programm: eine unbehandelte Ausnahme - Code 1 und die Meldung", thrown.Code == 1 && thrown.Err.Contains("thrown"), thrown.ToString());
        }

        // forge for Windows itself (FIRE_TEST_WIN_FORGE: a folder with forge.exe for win-x64) under Wine: `forge build` packs fire.exe (FIRE_VM_EXE) with its
        // subsystem and version info on Windows (PeResourceEditor), `forge bytecode` writes a program file with the names of Windows (fire_pkg_*.dll)
        if (Environment.GetEnvironmentVariable("FIRE_TEST_WIN_FORGE") is { Length: > 0 } winForge && File.Exists(Path.Combine(winForge, "forge.exe")))
        {
            string winExe = Path.Combine(_root, "src", "hello-win.exe");
            var wenv = new Dictionary<string, string> { ["FIRE_VM_EXE"] = Win(Path.Combine(_bin, "fire.exe")) };
            // (the first run of a forge.exe installs the standard packages into its folder: some minutes under Wine)
            var built = RunWine(Path.Combine(winForge, "forge.exe"), new[] { "build", Win(hello), "-o", Win(winExe) }, Path.Combine(_root, "src"), wenv, timeoutMs: 1500000);
            var run = File.Exists(winExe) ? RunWine(winExe, new[] { "x" }, _root) : new Result("", "", -1, false);
            Check("forge.exe (win-x64) unter Wine packt mit fire.exe (FIRE_VM_EXE): das Programm laeuft", built.Code == 0 && run.Code == 6 && run.Text == "hello 1\nx\n", $"{built}\n{run}");
        }
        else Console.WriteLine("    (forge.exe fuer Windows: uebersprungen - FIRE_TEST_WIN_FORGE fehlt)");

        // a payload with the DLLs of its packages (as forge.exe packs on Windows): unpacked into the cache of the user (%LOCALAPPDATA%\fire\native), loaded from there
        if (io.Path == null) return;
        string reader = Source("reader.fire", """
            #import "io"
            #import "time"
            var line = IO.Stdio.ReadLine()
            print("read " + line + " " + (DateTime.Now.Year > 2000))
            """);
        var (okr, fbc, imports, logr) = Bytecode(reader, Path.Combine(_root, "src", "reader.fbc"));
        var dlls = imports.Select(Library).ToList();
        if (!okr || dlls.Any(d => d.Path == null)) { Check("reader.fbc und die DLLs von io und time", false, logr + string.Concat(dlls.Select(d => d.Log))); return; }
        string packed = Path.Combine(_root, "src", "reader.exe");
        File.Copy(Path.Combine(_bin, "fire.exe"), packed, true);
        var items = new List<(byte, string, byte[])> { (fire.Runtime.CVmPayload.ProgramKind, "program.fbc", File.ReadAllBytes(fbc)) };
        foreach (var d in dlls) items.Add((fire.Runtime.CVmPayload.LibraryKind, Path.GetFileName(d.Path!), File.ReadAllBytes(d.Path!)));
        using (var fs = new FileStream(packed, FileMode.Open, FileAccess.ReadWrite)) fire.Runtime.CVmPayload.Append(fs, items);
        string cache = Path.Combine(_prefix, "drive_c", "users", Environment.UserName, "AppData", "Local", "fire", "native");
        var first = RunWine(packed, Array.Empty<string>(), _root, new Dictionary<string, string> { ["FIRE_PKG_PATH"] = "" }, stdin: "eins\n");
        var written = Directory.Exists(cache) ? Directory.GetFiles(cache, "*.dll", SearchOption.AllDirectories).ToList() : new List<string>();
        Check($"ein gepacktes Programm mit den DLLs von io und time: ausgepackt in %LOCALAPPDATA%\\fire\\native, geladen, die Eingabe gelesen",
            first.Code == 0 && first.Text == "read eins True\n" && written.Count == dlls.Count, $"{first}\ncache: {string.Join(", ", written)}");
        if (written.Count == 0) return;
        var stamp = written.Select(File.GetLastWriteTimeUtc).ToList();
        Thread.Sleep(1100);
        var second = RunWine(packed, Array.Empty<string>(), _root, new Dictionary<string, string> { ["FIRE_PKG_PATH"] = "" }, stdin: "zwei\n");
        Check("der zweite Start schreibt nichts in den Cache", second.Text == "read zwei True\n" && written.Select(File.GetLastWriteTimeUtc).SequenceEqual(stamp), second.ToString());
        File.WriteAllBytes(written[0], new byte[] { 1, 2, 3 });
        var third = RunWine(packed, Array.Empty<string>(), _root, new Dictionary<string, string> { ["FIRE_PKG_PATH"] = "" }, stdin: "drei\n");
        Check("eine beschaedigte DLL im Cache wird ersetzt (MoveFileExW)", third.Text == "read drei True\n" && new FileInfo(written[0]).Length > 3, third.ToString());
        string own = Path.Combine(_root, "cache ö");
        var fourth = RunWine(packed, Array.Empty<string>(), _root, new Dictionary<string, string> { ["FIRE_PKG_PATH"] = "", ["FIRE_VM_CACHE"] = Win(own) }, stdin: "vier\n");
        Check("FIRE_VM_CACHE (ein Ordner mit Umlaut): die DLLs dort", fourth.Text == "read vier True\n" && Directory.Exists(own) && Directory.GetFiles(own, "*.dll", SearchOption.AllDirectories).Length == dlls.Count, fourth.ToString());
    }

    private static (bool Ok, string Log) ForgeBuild(string source, string exe, Dictionary<string, string> env, params string[] extra)
    {
        var info = new ProcessStartInfo(_forge.Program) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(source)! };
        foreach (string a in _forge.Prefix) info.ArgumentList.Add(a);
        foreach (string a in new[] { "build", source, "-o", exe }.Concat(extra)) info.ArgumentList.Add(a);
        foreach (var (k, v) in env) info.Environment[k] = v;
        using var p = Process.Start(info)!;
        var err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode == 0 && File.Exists(exe), output + err.Result);
    }

    // ---- the display of the host, __HostCommand, FIRE_PRINT_RECORDS -------------------------------------------------------------------------------------------------

    private static void HostDisplay()
    {
        var gfx = Library("fire_gfx");
        if (gfx.Path == null) { Check("die Bibliothek von graphics und windows fuer Windows (fire_gfx.dll) baut", false, gfx.Log); return; }
        string source = Source("view.fire", """
            #import "ui"
            extern string __HostCommand()
            var fb = new Framebuffer(160, 100)
            var win = new Window(fb, "view")
            var ui = new UI.Root(fb, win)
            var b = new UI.Button("OK", 20, 20, 80, 30)
            b.OnClick = func () => print("clicked")
            ui.Add(b)
            print("two\nlines")
            var running = true
            while (running && ui.Tick()) {
                var c = __HostCommand()
                while (c != "") {
                    print("command " + c)
                    if (c == "quit") { running = false }
                    c = __HostCommand()
                }
            }
            print("end")
            """);
        var (ok, fbc, imports, log) = Bytecode(source, Path.Combine(_root, "src", "view.fbc"));
        if (!ok) { Check("view.fbc", false, log); return; }
        foreach (string import in imports.Where(i => i != "fire_gfx")) if (Library(import).Path == null) { Check($"die Bibliothek von {import} fuer Windows", false, Library(import).Log); return; }
        using var display = HostDisplayFile.Create(_root);
        var env = new Dictionary<string, string> { [HostDisplayFile.EnvironmentVariable] = Win(display.Path), ["FIRE_PRINT_RECORDS"] = "1" };
        var info = WineInfo(Path.Combine(_bin, "fire.exe"), new[] { "run", Win(fbc) }, _root, env);
        info.Environment.Remove("DISPLAY");   // (the windows are the host's: no screen needed)
        using var p = Process.Start(info)!;
        var lines = new List<string>();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (lines) lines.Add(e.Data.TrimEnd('\r').Replace('\x1F', '\n')); };
        p.BeginOutputReadLine();
        var err = p.StandardError.ReadToEndAsync();
        try
        {
            uint[] frame = Array.Empty<uint>();
            int w = 0, h = 0, window = 0;
            long? seq = null;
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline && !p.HasExited && (seq = display.TryCopyFrame(ref frame, out w, out h, out window)) == null) Thread.Sleep(10);
            int distinct = frame.Distinct().Count();
            Check("FIRE_DISPLAY_SHM: fire.exe bildet die Datei des Hosts ab (CreateFileMapping/MapViewOfFile), HostDisplayFile (spark) liest ein Bild 160x100 mit dem Button, die pid",
                seq != null && w == 160 && h == 100 && window == 1 && distinct > 2 && display.ProgramPid != 0, $"seq {seq} {w}x{h} window {window}, {distinct} colors, pid {display.ProgramPid}\n{err.IsCompleted}");
            // a click on the button: the events of the host through the ring (MouseMove, MouseDown, MouseUp)
            display.Post(new HostDisplayEvent { Type = 9, X = 50, Y = 30 });
            display.Post(new HostDisplayEvent { Type = 8, Button = 1, X = 50, Y = 30 });
            display.Post(new HostDisplayEvent { Type = 11, Button = 1, X = 50, Y = 30 });
            bool clicked = WaitFor(lines, l => l == "clicked", 30000);
            Check("die Ereignisse des Hosts kommen ueber den Ring an (ein Klick auf den Button)", clicked && display.EventsTaken >= 3, $"taken {display.EventsTaken}: {Text(lines)}");
            Check("FIRE_PRINT_RECORDS=1: ein print mit Zeilenumbruch ist eine Zeile (\\x1F)", WaitFor(lines, l => l == "two\nlines", 1000), Text(lines));
            // the commands of the host: lines on stdin, __HostCommand never waits (the program keeps drawing meanwhile)
            var before = display.TryCopyFrame(ref frame, out _, out _, out _);
            p.StandardInput.Write("hello host\n");
            p.StandardInput.Flush();
            bool hello = WaitFor(lines, l => l == "command hello host", 30000);
            p.StandardInput.Write("quit\n");
            p.StandardInput.Flush();
            bool ended = p.WaitForExit(30000);
            Check("__HostCommand: die Befehle des Hosts ueber stdin (PeekNamedPipe, ohne zu warten), das Programm endet auf 'quit'",
                hello && ended && p.ExitCode == 0 && WaitFor(lines, l => l == "end", 5000), $"ended {ended}: {Text(lines)}\n{(ended ? err.Result : "")}");
        }
        finally
        {
            if (!p.HasExited) try { p.Kill(true); } catch (Exception) { }
            p.WaitForExit(10000);
        }
    }

    private static bool WaitFor(List<string> lines, Func<string, bool> match, int ms)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < deadline)
        {
            lock (lines) if (lines.Any(match)) return true;
            Thread.Sleep(20);
        }
        lock (lines) return lines.Any(match);
    }

    private static string Text(List<string> lines) { lock (lines) return string.Join(" | ", lines); }

    // ---- the device host: FIRE_DEVICE_HOST over Winsock to spark's DeviceManager ------------------------------------------------------------------------------

    private static void Devices()
    {
        string source = Source("devices.fire", """
            #import "devices"
            print("shared " + DeviceManager.IsShared())
            print("default " + DeviceManager.Host.DefaultIdentifier())
            var d = DeviceManager.Get("loopback:echo")
            print("connect " + d.Connect() + " " + d.Connected())
            print("write " + d.WriteString("ping from windows"))
            print("echo " + d.WaitForString("ping from windows", 5000))
            """);
        var (ok, fbc, imports, log) = Bytecode(source, Path.Combine(_root, "src", "devices.fbc"));
        if (!ok) { Check("devices.fbc", false, log); return; }
        foreach (string import in imports) if (Library(import).Path == null) { Check($"die Bibliothek von {import} fuer Windows", false, Library(import).Log); return; }
        using var manager = fire.Device.Manager.DeviceManager.DeviceManager.CreateDefault(isShared: true);
        manager.RegisterDriver(new fire.Device.Manager.Drivers.Loopback.LoopbackDriver());
        manager.RefreshDevices(true);
        manager.DefaultIdentifier = "loopback:echo";
        var traced = new List<string>();
        manager.PacketCaptured += r => { lock (traced) traced.Add(System.Text.Encoding.UTF8.GetString(r.Data).Trim()); };
        using var server = new fire.Device.Manager.DeviceManager.DeviceHostServer(manager);
        var run = Fire(new[] { "run", Win(fbc) }, _root, new Dictionary<string, string> { [fire.Device.Manager.DeviceManager.DeviceHostServer.EnvironmentVariable] = server.EnvironmentValue });
        string t = run.Text;
        bool traceOk;
        lock (traced) traceOk = traced.Any(x => x.Contains("ping from windows"));
        Check("FIRE_DEVICE_HOST: fire.exe verbindet sich ueber Winsock mit dem DeviceManager (geteilt, Standardgeraet, Echo, Paketmitschnitt)",
            run.Code == 0 && t.Contains("shared True\n") && t.Contains("default loopback:echo\n") && t.Contains("connect True True\n") && t.Contains("write True\n") && t.Contains("echo True\n") && traceOk
            && server.ConnectionsSoFar == 1, $"{run}\nconnections {server.ConnectionsSoFar}");
        var wrong = Fire(new[] { "run", Win(fbc) }, _root, new Dictionary<string, string> { [fire.Device.Manager.DeviceManager.DeviceHostServer.EnvironmentVariable] = $"tcp:127.0.0.1:{server.Port};token=nope" });
        Check("FIRE_DEVICE_HOST mit falschem Token: die Geraete der Maschine", wrong.Text.Contains("shared False") && wrong.Err.Contains("did not take the connection"), wrong.ToString());
    }

    // ---- the corpus ------------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>The cases whose output on Windows is another one by the nature of the system, not by the VM: what they print there, and why.</summary>
    private static readonly (string Case, string Why)[] OtherOnWindows =
    {
        ("2160-", "Path.Combine joins with \\ on Windows (a\\b)"),
        ("2630-", "Process.Run starts 'sh', which Windows does not have"),
        ("3470-", "the simulated device of audio is ALSA's null device of Linux"),
        ("3380-", "the circuit simulation over 20 ms: the functions of msvcrt's libm (exp, pow) round other than glibc's - 27 instead of 28 firings of the UJT"),
    };

    private static void Corpus()
    {
        string filter = Environment.GetEnvironmentVariable("FIRE_CVMWIN_CASES") ?? "";
        int limit = int.TryParse(Environment.GetEnvironmentVariable("FIRE_CVMWIN_LIMIT"), out int l) ? l : int.MaxValue;
        var cases = TestCases.Load("native").Where(c => Path.GetFileName(c.File).Contains(filter, StringComparison.Ordinal)).Take(limit).ToList();
        var runs = new List<(TestCases.Case Case, string Mode)>();
        int skipped = 0;
        foreach (var c in cases)
        {
            string tail = string.Join("\n", File.ReadLines(c.File).TakeLast(12));
            // (as tests/run-cases-vm.sh: not for forge run, output that varies, the messages of the compiler - those are no question of the VM)
            if (c.Varies || c.Error != null || tail.Contains("//# skip: forge run") || File.Exists(Path.ChangeExtension(c.File, ".diagnostics"))) { skipped++; continue; }
            foreach (var mode in c.Modes ?? new[] { fire.Runtime.VmExecutionMode.Release }) runs.Add((c, mode.ToString().ToUpperInvariant()));
        }
        var clock = Stopwatch.StartNew();
        string dir = Path.Combine(_root, "corpus");
        Directory.CreateDirectory(dir);
        var results = new ConcurrentBag<(string Name, string? Problem, bool NotRun)>();
        Parallel.ForEach(runs, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount) }, r =>
        {
            string id = Path.GetFileNameWithoutExtension(r.Case.File) + "." + r.Mode;
            var built = Bytecode(r.Case.File, Path.Combine(dir, id + ".fbc"), r.Mode);
            if (!built.Ok) { results.Add((id, "forge bytecode (Linux): " + built.Log.Trim(), true)); return; }
            foreach (string import in built.Imports)
                if (import.StartsWith('?') || Library(import).Path == null) { results.Add((id, $"the library of '{import}' cannot be built for Windows: {(import.StartsWith('?') ? "unknown" : Library(import).Log.Split('\n').LastOrDefault(x => x.Contains("error")) ?? "")}", true)); return; }
            var run = Fire(new[] { built.Fbc }.Select(Win), Path.GetDirectoryName(r.Case.File)!, timeoutMs: 120000);
            string output = run.Text;
            string err = string.Join("\n", run.Err.Replace("\r\n", "\n").Split('\n').Where(x => !x.StartsWith("warning ", StringComparison.Ordinal))).Trim();
            string? problem = null;
            if (run.TimedOut) problem = "timeout";
            else if (run.Code != 0 && err.Length > 0)
            {
                if (r.Case.Unhandled == null || err != r.Case.Unhandled) problem = $"ended with {run.Code}: {err}";
            }
            else if (r.Case.Unhandled != null) problem = $"expected the unhandled exception: {r.Case.Unhandled} (code {run.Code})";
            if (problem == null && output != (r.Case.Expected ?? "")) problem = TestCases.Difference(r.Case.Expected ?? "", output);
            results.Add((id, problem, false));
        });
        var all = results.OrderBy(x => x.Name, StringComparer.Ordinal).ToList();
        bool Other(string name) => OtherOnWindows.Any(o => name.StartsWith(o.Case, StringComparison.Ordinal));
        int right = all.Count(x => x.Problem == null), notRun = all.Count(x => x.NotRun), other = all.Count(x => x.Problem != null && !x.NotRun && Other(x.Name));
        int wrong = all.Count - right - notRun - other;
        Console.WriteLine($"    Korpus (native) unter Wine: {right} richtig, {wrong} falsch, {other} anders unter Windows, {notRun} nicht gelaufen, {skipped} ausgelassen (wie run-cases-vm.sh), {clock.Elapsed.TotalSeconds:0} s");
        foreach (var x in all.Where(x => x.NotRun)) Console.WriteLine($"    (nicht gelaufen: {x.Name} - {x.Problem!.Split('\n')[0]})");
        foreach (var x in all.Where(x => x.Problem != null && !x.NotRun && Other(x.Name)))
            Console.WriteLine($"    (anders unter Windows: {x.Name} - {OtherOnWindows.First(o => x.Name.StartsWith(o.Case, StringComparison.Ordinal)).Why})");
        Check($"der Korpus (tests/cases/native) in fire.exe unter Wine: {right} von {right + wrong} wie .expected (dazu {other} mit einer Ausgabe von Windows)",
            wrong == 0 && right > 0, string.Join("\n", all.Where(x => x.Problem != null && !x.NotRun && !Other(x.Name)).Select(x => $"{x.Name}: {x.Problem}")));
    }
}
