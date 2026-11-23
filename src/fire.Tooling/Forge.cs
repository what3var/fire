using System.Diagnostics;
using System.Text;

namespace fire.Tooling
{
    /// <summary>The VM in C for a program is not there and cannot be had (FIRE_VM_EXE names nothing, no forge to build it): its message says what to do.</summary>
    public sealed class VmUnavailableException : InvalidOperationException
    {
        public VmUnavailableException(string message) : base(message) { }
    }

    /// <summary>What forge said when it compiled a script (<see cref="Forge.Compile"/>): the program file, the libraries of its packages, the VM of its widths, or the errors.</summary>
    public sealed class CompiledScript
    {
        public bool Ok { get; init; }
        /// <summary>The program file (.fbc); null when it did not compile.</summary>
        public string? Program { get; init; }
        /// <summary>The libraries of the program's packages for the VM (FIRE_PKG_PATH: their folders).</summary>
        public IReadOnlyList<string> Libraries { get; init; } = Array.Empty<string>();
        /// <summary>The VM of the program's widths, as forge found or built it (null: it did not say).</summary>
        public string? Vm { get; init; }
        public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = Array.Empty<Diagnostic>();
        /// <summary>The errors as text, one per line (<see cref="Ok"/> false); what forge wrote when it crashed.</summary>
        public string Errors { get; init; } = "";
        /// <summary>Milliseconds forge took.</summary>
        public double Milliseconds { get; init; }
    }

    /// <summary>
    /// forge and the VM are C programs (docs/ROADMAP_C_PORT.md, M3): spark compiles, builds, tests and serves its language services through `forge` (native/forge) as a child
    /// process and runs programs in `fire` (native/vm). Where they are:
    /// <list type="bullet">
    /// <item>forge: <c>FIRE_FORGE</c> (a program), else next to spark (the folder `forge package-dist` makes: forge, fire, native/, Source/, Prebuilt/ - what setup.iss installs and
    /// what the build of spark puts into its output), else <c>FIRE_FORGE_C</c>, else built from native/forge/forge.c of the repository above spark or the working folder with the
    /// C compiler of the machine (once, into a cache keyed by the sources; it then reads native/ of the repository: <see cref="Environment"/>).</item>
    /// <item>fire: <c>FIRE_VM_EXE</c>, else next to forge, else next to spark, else `forge build-vm` into the cache of the user.</item>
    /// </list>
    /// </summary>
    public static class Forge
    {
        /// <summary>A forge of its own (a program): for tests, and to try another compiler.</summary>
        public const string ForgeVariable = "FIRE_FORGE";

        private static string Extension => OperatingSystem.IsWindows() ? ".exe" : "";

        private static readonly object FindLock = new();
        private static string? _found;
        private static string? _repository;   // the repository whose native/ a forge built from its sources reads

        /// <summary>The forge to run (null: none - <paramref name="error"/> says why).</summary>
        public static string? Locate(out string error)
        {
            lock (FindLock)
            {
                error = "";
                if (_found != null && File.Exists(_found)) return _found;
                _found = Find(out error);
                return _found;
            }
        }

        /// <summary>The forge to run; an <see cref="InvalidOperationException"/> that says why when there is none.</summary>
        public static string Require() => Locate(out string error) ?? throw new InvalidOperationException("forge was not found: " + error);

        /// <summary>Forget what was found (a test that sets <see cref="ForgeVariable"/> anew).</summary>
        public static void Reset() { lock (FindLock) { _found = null; _repository = null; } }

        private static string? Find(out string error)
        {
            error = "";
            _repository = null;
            if (System.Environment.GetEnvironmentVariable(ForgeVariable) is { Length: > 0 } own)
            {
                if (File.Exists(own)) return Path.GetFullPath(own);
                error = $"{ForgeVariable}={own} does not exist";
                return null;
            }
            string beside = Path.Combine(AppContext.BaseDirectory, "forge" + Extension);
            if (File.Exists(beside)) return beside;
            if (System.Environment.GetEnvironmentVariable("FIRE_FORGE_C") is { Length: > 0 } c && File.Exists(c)) return Path.GetFullPath(c);
            string? source = null;
            foreach (var start in new[] { AppContext.BaseDirectory, System.Environment.CurrentDirectory })
                for (var dir = new DirectoryInfo(start); dir != null && source == null; dir = dir.Parent)
                    if (File.Exists(Path.Combine(dir.FullName, "native", "forge", "forge.c"))) { source = Path.Combine(dir.FullName, "native", "forge", "forge.c"); _repository = dir.FullName; }
            if (source == null) { error = $"there is no forge next to {AppContext.BaseDirectory} and no native/forge/forge.c to build one from (set {ForgeVariable})"; return null; }
            return BuildFromSource(source, out error);
        }

        /// <summary>forge-c built from the sources of the repository into a cache (named by the sources' names, sizes and times).</summary>
        private static string? BuildFromSource(string source, out string error)
        {
            error = "";
            string forgeDir = Path.GetDirectoryName(source)!;
            var stamp = new StringBuilder();
            foreach (var f in Directory.GetFiles(forgeDir, "*", SearchOption.AllDirectories).Concat(Directory.GetFiles(Path.Combine(forgeDir, "..", "tables"))).Concat(Directory.GetFiles(Path.Combine(forgeDir, "..", "base"))).OrderBy(f => f, StringComparer.Ordinal))
            {
                var fi = new FileInfo(f);
                stamp.Append(f).Append(fi.Length).Append(fi.LastWriteTimeUtc.Ticks).Append(';');
            }
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(stamp.ToString())))[..16];
            string cache = Path.Combine(Path.GetTempPath(), "fire-forge-c-" + hash);
            string exe = Path.Combine(cache, "forge" + Extension);
            if (File.Exists(exe)) return exe;
            Directory.CreateDirectory(cache);
            foreach (var cc in OperatingSystem.IsWindows() ? new[] { "gcc", "clang" } : new[] { "cc", "gcc", "clang" })
            {
                try
                {
                    var info = new ProcessStartInfo(cc) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
                    string temp = exe + "." + System.Environment.ProcessId + ".tmp";
                    foreach (var a in new[] { "-O2", source, "-o", temp, "-lm" }) info.ArgumentList.Add(a);
                    if (!OperatingSystem.IsWindows()) info.ArgumentList.Add("-lpthread");
                    else info.ArgumentList.Add("-static");
                    using var p = Process.Start(info);
                    if (p == null) continue;
                    var err = p.StandardError.ReadToEndAsync();
                    p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(900000)) { try { p.Kill(); } catch (Exception) { } error = cc + ": timeout"; continue; }
                    if (p.ExitCode != 0) { error = cc + ": " + err.Result; continue; }
                    try { File.Move(temp, exe, true); } catch (IOException) { /* another process built it */ }
                    return exe;
                }
                catch (System.ComponentModel.Win32Exception) { error = "no C compiler (cc, gcc, clang) to build forge with"; }
            }
            return null;
        }

        /// <summary>The environment a forge built from the repository's sources needs (its native/ and the store next to spark); empty for a forge of a distribution,
        /// which finds them next to itself. Added to the environment of every forge spark starts.</summary>
        public static IReadOnlyDictionary<string, string> Environment()
        {
            var env = new Dictionary<string, string>();
            Locate(out _);
            string? repo;
            lock (FindLock) repo = _repository;
            if (repo == null) return env;
            if (System.Environment.GetEnvironmentVariable("FIRE_TABLES") is not { Length: > 0 }) env["FIRE_TABLES"] = Path.Combine(repo, "native", "tables");
            if (System.Environment.GetEnvironmentVariable("FIRE_NATIVE_ROOT") is not { Length: > 0 }) env["FIRE_NATIVE_ROOT"] = Path.Combine(repo, "native");
            if (System.Environment.GetEnvironmentVariable("FIRE_PACKAGES") is not { Length: > 0 }) env["FIRE_PACKAGES"] = fire.Projects.PackageStore.Default.Root;
            return env;
        }

        /// <summary>A start of forge with <paramref name="args"/> (the environment of <see cref="Environment"/>, the output redirected, no window).</summary>
        public static ProcessStartInfo StartInfo(IEnumerable<string> args, string? workingDirectory = null, IReadOnlyDictionary<string, string>? environment = null)
        {
            var info = new ProcessStartInfo(Require())
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
                WorkingDirectory = workingDirectory ?? System.Environment.CurrentDirectory,
            };
            foreach (string a in args) info.ArgumentList.Add(a);
            foreach (var (k, v) in Environment()) info.Environment[k] = v;
            if (environment != null) foreach (var (k, v) in environment) info.Environment[k] = v;
            return info;
        }

        /// <summary>Runs forge to its end: its exit code, its output, its error output. <paramref name="cancel"/> or <paramref name="timeoutMs"/> end it (exit code -1).</summary>
        public static (int ExitCode, string Output, string Errors) Run(IEnumerable<string> args, string? workingDirectory = null, int timeoutMs = 600000, CancellationToken cancel = default,
            Action<string>? line = null, IReadOnlyDictionary<string, string>? environment = null)
        {
            var info = StartInfo(args, workingDirectory, environment);
            var output = new StringBuilder();
            var errors = new StringBuilder();
            using var process = new Process { StartInfo = info };
            process.OutputDataReceived += (_, e) => { if (e.Data != null) { lock (output) output.Append(e.Data).Append('\n'); line?.Invoke(e.Data); } };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errors) errors.Append(e.Data).Append('\n'); };
            process.Start();
            fire.Debugging.ChildProcesses.Add(process);
            process.StandardInput.Close();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            bool killed = false;
            using (cancel.Register(() => { killed = true; Kill(process); }))
            {
                if (!process.WaitForExit(timeoutMs)) { killed = true; Kill(process); }
                process.WaitForExit();
            }
            lock (output) lock (errors) return (killed ? -1 : process.ExitCode, output.ToString(), errors.ToString());
        }

        private static void Kill(Process process)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }

        /// <summary>Compiles files, a project or a solution with forge into <paramref name="program"/>: `forge bytecode &lt;inputs&gt; -o &lt;program&gt; --report json [more]`.
        /// <paramref name="mode"/>: DEBUG, RELEASE, PERFORMANCE (null: what the sources say).</summary>
        public static CompiledScript CompileFiles(IReadOnlyList<string> inputs, string program, string? mode = null, IEnumerable<string>? more = null, string? workingDirectory = null,
            CancellationToken cancel = default)
        {
            var clock = Stopwatch.StartNew();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(program))!);
            var args = new List<string> { "bytecode" };
            args.AddRange(inputs);
            args.Add("-o"); args.Add(program);
            if (mode != null) { args.Add("-m"); args.Add(mode); }
            args.Add("--report"); args.Add("json");
            if (more != null) args.AddRange(more);
            var (code, output, errors) = Run(args, workingDirectory ?? Path.GetDirectoryName(Path.GetFullPath(inputs[0])), cancel: cancel);
            var diagnostics = new List<Diagnostic>();
            var libraries = new List<string>();
            string? vm = null, built = null;
            bool ok = false, ended = false;
            var text = new StringBuilder();
            foreach (string l in output.Split('\n'))
            {
                if (l.Length == 0) continue;
                if (!ForgeReport.TryParse(l, out var ev)) { text.Append(l).Append('\n'); continue; }
                switch (ev.Type)
                {
                    case "diagnostic": if (ev.Diagnostic is { } d) diagnostics.Add(d); break;
                    case "output":
                        if (ev.Kind == "program") built = ev.Path;
                        else if (ev.Kind == "library" && ev.Path != null) libraries.Add(ev.Path);
                        else if (ev.Kind == "vm") vm = ev.Path;
                        break;
                    case "end": ended = true; ok = ev.Ok; if (ev.Error is { Length: > 0 } e && !ev.Ok) text.Append(e).Append('\n'); break;
                }
            }
            var errorText = new StringBuilder();
            foreach (var d in diagnostics.Where(d => d.IsError)) errorText.Append(d.ToLogLine()).Append('\n');
            if (errorText.Length == 0 && !(ok && code == 0)) errorText.Append(text).Append(errors);
            if (!ended && errorText.Length == 0) errorText.Append($"forge ended with exit code {code}.");
            return new CompiledScript
            {
                Ok = ok && code == 0 && built != null && File.Exists(built), Program = built, Libraries = libraries, Vm = vm, Diagnostics = diagnostics,
                Errors = errorText.ToString().TrimEnd('\n'), Milliseconds = clock.Elapsed.TotalMilliseconds,
            };
        }

        /// <summary>Compiles a project as a build does, without writing a program: a program with `forge bytecode` (into <paramref name="folder"/>), a library with
        /// `forge build --check` (only compiled, not packed). <paramref name="more"/>: more options (`--overlay`). The diagnostics and whether it compiled.</summary>
        public static CompiledScript CheckProject(string projectFile, string folder, IEnumerable<string>? more = null, CancellationToken cancel = default)
        {
            bool library;
            try { library = fire.Projects.FireProject.Load(projectFile).Type == fire.Projects.OutputType.Library; }
            catch (Exception ex) when (ex is fire.Projects.ProjectException or IOException) { library = false; }
            if (!library) return CompileFiles(new[] { projectFile }, Path.Combine(folder, "check.fbc"), more: more, cancel: cancel);
            var clock = Stopwatch.StartNew();
            var args = new List<string> { "build", projectFile, "--check", "-o", folder, "--report", "json" };
            if (more != null) args.AddRange(more);
            var (code, output, errors) = Run(args, Path.GetDirectoryName(Path.GetFullPath(projectFile)), cancel: cancel);
            var diagnostics = new List<Diagnostic>();
            bool ok = false;
            var text = new StringBuilder();
            foreach (string l in output.Split('\n'))
            {
                if (l.Length == 0) continue;
                if (!ForgeReport.TryParse(l, out var ev)) { text.Append(l).Append('\n'); continue; }
                if (ev.Type == "diagnostic" && ev.Diagnostic is { } d) diagnostics.Add(d);
                else if (ev.Type == "log" && ev.Text != null) text.Append(ev.Text).Append('\n');
                else if (ev.Type == "end") { ok = ev.Ok; if (ev.Error is { Length: > 0 } e && !ev.Ok) text.Append(e).Append('\n'); }
            }
            var errorText = new StringBuilder();
            foreach (var d in diagnostics.Where(d => d.IsError)) errorText.Append(d.ToLogLine()).Append('\n');
            if (errorText.Length == 0 && !(ok && code == 0)) errorText.Append(text).Append(errors);
            return new CompiledScript { Ok = ok && code == 0, Diagnostics = diagnostics, Errors = errorText.ToString().TrimEnd('\n'), Milliseconds = clock.Elapsed.TotalMilliseconds };
        }

        /// <summary>Compiles a script with forge into a program file in <paramref name="folder"/>: `forge bytecode &lt;script&gt; -o &lt;folder&gt;/script.fbc --report json`
        /// (the report names the program, the libraries of its packages - built now - and the VM of its widths). The script is written next to its includes
        /// (<paramref name="basePath"/>) when there is one, else into the folder.</summary>
        public static CompiledScript Compile(string script, string? basePath, string folder, string mode = "RELEASE", CancellationToken cancel = default)
        {
            var clock = Stopwatch.StartNew();
            Directory.CreateDirectory(folder);
            string dir = basePath != null && Directory.Exists(basePath) ? basePath : folder;
            // (the script as a file of the folder of its includes - a name nobody else uses, gone afterwards; its errors name it "script")
            string file = Path.Combine(dir, $".fire-script-{System.Environment.ProcessId}-{Guid.NewGuid():N}.fire");
            string program = Path.Combine(folder, "script.fbc");
            try
            {
                File.WriteAllText(file, script, new UTF8Encoding(false));
                var (code, output, errors) = Run(new[] { "bytecode", file, "-o", program, "-m", mode, "--report", "json" }, dir, cancel: cancel);
                var diagnostics = new List<Diagnostic>();
                var libraries = new List<string>();
                string? vm = null, built = null;
                bool ok = false, ended = false;
                var text = new StringBuilder();
                foreach (string l in output.Split('\n'))
                {
                    if (l.Length == 0) continue;
                    if (!ForgeReport.TryParse(l, out var ev)) { text.Append(l).Append('\n'); continue; }
                    switch (ev.Type)
                    {
                        case "diagnostic": if (ev.Diagnostic is { } d) { diagnostics.Add(d with { File = d.File == file || d.File == Path.GetFileName(file) ? null : d.File }); } break;
                        case "output":
                            if (ev.Kind == "program") built = ev.Path;
                            else if (ev.Kind == "library" && ev.Path != null) libraries.Add(ev.Path);
                            else if (ev.Kind == "vm") vm = ev.Path;
                            break;
                        case "end": ended = true; ok = ev.Ok; if (ev.Error is { Length: > 0 } e && !ev.Ok) text.Append(e).Append('\n'); break;
                    }
                }
                var errorText = new StringBuilder();
                foreach (var d in diagnostics.Where(d => d.IsError)) errorText.Append(d.Line > 0 ? $"{d.Message} (line {d.Line})" : d.Message).Append('\n');
                if (errorText.Length == 0 && !(ok && code == 0)) errorText.Append(text).Append(errors);
                if (!ended && errorText.Length == 0) errorText.Append($"forge ended with exit code {code}.");
                return new CompiledScript
                {
                    Ok = ok && code == 0 && built != null && File.Exists(built), Program = built, Libraries = libraries, Vm = vm, Diagnostics = diagnostics,
                    Errors = errorText.ToString().Replace(Path.GetFileName(file), "script").TrimEnd('\n'), Milliseconds = clock.Elapsed.TotalMilliseconds,
                };
            }
            finally { try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
        }

        // ---- the VM ----------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>The name of the VM for a pair of widths: fire, fire-i32, fire-f32, fire-i32-f32, fire-i16, fire-i16-f32 (without .exe).</summary>
        public static string VariantName(int intWidth, int floatWidth) => "fire" + (intWidth == 64 ? "" : "-i" + intWidth) + (floatWidth == 64 ? "" : "-f32");

        /// <summary>The cache of the VMs that spark has forge build: &lt;LocalApplicationData&gt;/fire/spark-vm.</summary>
        public static string CacheFolder => Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData, System.Environment.SpecialFolderOption.Create) is { Length: > 0 } local ? local : Path.GetTempPath(), "fire", "spark-vm");

        /// <summary>The executable of the VM for the widths (see the class); a <see cref="VmUnavailableException"/> that says what to do when there is none.</summary>
        public static string LocateVm(int intWidth = 64, int floatWidth = 64)
        {
            string name = VariantName(intWidth, floatWidth) + Extension;
            if (System.Environment.GetEnvironmentVariable("FIRE_VM_EXE") is { Length: > 0 } given)
            {
                string path = intWidth == 64 && floatWidth == 64 ? given : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(given))!, name);
                if (File.Exists(path)) return path;
                if (intWidth == 64 && floatWidth == 64) throw new VmUnavailableException($"FIRE_VM_EXE names '{given}', which does not exist.");
            }
            string? forge = Locate(out string why);
            if (forge != null && Path.Combine(Path.GetDirectoryName(forge)!, name) is var nextToForge && File.Exists(nextToForge)) return nextToForge;
            string beside = Path.Combine(AppContext.BaseDirectory, name);
            if (File.Exists(beside)) return beside;
            if (forge == null) throw new VmUnavailableException($"There is no `fire` (the VM in C) and no forge to build it: {why}");
            // (forge builds it - only when its sources changed: build-vm keeps it up to date by its stamp)
            string folder = Path.Combine(CacheFolder, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(forge)))[..12]);
            string cached = Path.Combine(folder, name);
            var (code, output, errors) = Run(new[] { "build-vm", folder, "--variant", intWidth == 64 && floatWidth == 64 ? "default" : VariantName(intWidth, floatWidth) });
            if (code == 0 && File.Exists(cached)) return cached;
            throw new VmUnavailableException($"The VM in C ({VariantName(intWidth, floatWidth)}) could not be built on this machine:\n{(output + errors).Trim()}\n" +
                "Install a C compiler (gcc, clang, tcc or cl), or set FIRE_VM_EXE to a `fire` built elsewhere.");
        }
    }
}
