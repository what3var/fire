using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace fire.Editor.Assistant
{
    /// <summary>
    /// The assistant as a native program (docs/ai/assistant.md): spark builds Assistant/assistant.fire once with <c>forge build --engine native</c> into
    /// <see cref="Folder"/> and starts that binary from then on instead of <c>forge run</c> - the program and its packages (ui, the C engine of ai, mcp, ...) run
    /// as machine code, not in the virtual machine. The build is made again only when what it is made of changed (<see cref="Key"/>: the script, the sources of
    /// the packages it imports - directly or through other packages - and the compiler); a build that failed is not tried again for the same key.
    /// While the build runs the assistant waits (spark starts it when the build is done); only without a C toolchain, or when the build failed, does it run in the VM.
    /// <para>The compiler is not simply the machine's Build toolchain (<see cref="BuildToolchain"/>): with TinyCC the model's engine (packages/ai) computes ten times
    /// slower and keeps the machine's cores busy that much longer (the instructions it reads at the start: minutes). GCC builds the program without optimisation
    /// (about a minute - the engine optimises itself, fire_ai_engine.h), Clang and MSVC as for publishing; TinyCC only when there is no other compiler.</para>
    /// </summary>
    public static class AssistantNative
    {
        /// <summary>Where the build is kept (beside spark's settings): the binary, the key it was built from, the key of a build that failed.</summary>
        public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "fire", "assistant-native");

        public static string BinaryName => OperatingSystem.IsWindows() ? "assistant.exe" : "assistant";

        public static string BinaryIn(string folder) => Path.Combine(folder, BinaryName);
        private static string StampIn(string folder) => Path.Combine(folder, "assistant.key");
        private static string FailedIn(string folder) => Path.Combine(folder, "assistant.failed");

        /// <summary>The key of a build: SHA-256 over the script, every file of the packages it uses (their folders in <paramref name="packagesFolder"/>, the standard
        /// packages' sources beside forge) and the compiler (<paramref name="compilerFiles"/>: their contents - or, for a large single file, its size and time).</summary>
        public static string Key(string script, string packagesFolder, IEnumerable<string> compilerFiles, string? toolchain = null)
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            void Add(string text) => sha.AppendData(Encoding.UTF8.GetBytes(text + "\n"));
            if (toolchain != null) Add("toolchain " + toolchain);
            Add("script");
            sha.AppendData(File.ReadAllBytes(script));
            foreach (string folder in PackageFolders(script, packagesFolder))
            {
                foreach (string file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
                             .Where(f => !IsBuildOutput(folder, f))
                             .OrderBy(f => Path.GetRelativePath(folder, f).Replace('\\', '/'), StringComparer.Ordinal))
                {
                    Add("package " + Path.GetFileName(folder) + "/" + Path.GetRelativePath(folder, file).Replace('\\', '/'));
                    sha.AppendData(File.ReadAllBytes(file));
                }
            }
            foreach (string file in compilerFiles.Where(File.Exists).OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal))
            {
                var info = new FileInfo(file);
                Add("compiler " + info.Name);
                if (info.Length <= 32 * 1024 * 1024) sha.AppendData(File.ReadAllBytes(file));
                else Add(info.Length + " " + info.LastWriteTimeUtc.Ticks);   // (a single-file forge: what an update changes)
            }
            return Convert.ToHexString(sha.GetHashAndReset());
        }

        private static bool IsBuildOutput(string folder, string file)
        {
            string first = Path.GetRelativePath(folder, file).Replace('\\', '/').Split('/')[0];
            return first is "bin" or "obj";
        }

        /// <summary>The compiler files of forge beside spark (<paramref name="baseDirectory"/>): forge (a C program, M3) - else the one spark runs.</summary>
        public static IReadOnlyList<string> CompilerFiles(string baseDirectory)
        {
            string beside = Path.Combine(baseDirectory, OperatingSystem.IsWindows() ? "forge.exe" : "forge");
            if (File.Exists(beside)) return new[] { beside };
            return Forge.Locate(out _) is { } found ? new[] { found } : new[] { beside };
        }

        private static readonly Regex ImportLine = new(@"^\s*#import\s+""([^""]+)""", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex ImportName = new(@"""import""\s*:\s*""([^""]+)""", RegexOptions.Compiled);
        private static readonly Regex UsesList = new(@"""uses""\s*:\s*\[([^\]]*)\]", RegexOptions.Compiled);

        /// <summary>The folders of the packages the script uses: its imports and theirs (the `#import` lines of their sources, the `uses` of their natives), found by
        /// the `import` name in the fireproj files of <paramref name="packagesFolder"/>.</summary>
        public static IReadOnlyList<string> PackageFolders(string script, string packagesFolder)
        {
            var byImport = new Dictionary<string, string>(StringComparer.Ordinal);
            if (Directory.Exists(packagesFolder))
                foreach (string dir in Directory.GetDirectories(packagesFolder))
                    foreach (string proj in Directory.GetFiles(dir, "*.fireproj"))
                    {
                        var m = ImportName.Match(File.ReadAllText(proj));
                        if (m.Success) byImport.TryAdd(m.Groups[1].Value, dir);
                    }
            var found = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var due = new Queue<string>(Imports(File.ReadAllText(script)));
            while (due.Count > 0)
            {
                string name = due.Dequeue();
                if (!seen.Add(name) || !byImport.TryGetValue(name, out var dir)) continue;
                found.Add(dir);
                foreach (string file in Directory.GetFiles(dir, "*.fire", SearchOption.AllDirectories).Where(f => !IsBuildOutput(dir, f)))
                    foreach (string next in Imports(File.ReadAllText(file))) due.Enqueue(next);
                foreach (string proj in Directory.GetFiles(dir, "*.fireproj"))
                    foreach (Match uses in UsesList.Matches(File.ReadAllText(proj)))
                        foreach (Match item in Regex.Matches(uses.Groups[1].Value, @"""([^""]+)"""))
                            due.Enqueue(item.Groups[1].Value);
            }
            return found.OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal).ToList();
        }

        private static IEnumerable<string> Imports(string source) => ImportLine.Matches(source).Select(m => m.Groups[1].Value);

        /// <summary>The binary when it was built from <paramref name="key"/> (null: none, or an older one).</summary>
        public static string? Current(string folder, string key)
        {
            try
            {
                string binary = BinaryIn(folder);
                if (File.Exists(binary) && File.Exists(StampIn(folder)) && File.ReadAllText(StampIn(folder)).Trim() == key) return binary;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return null;
        }

        /// <summary>Did the build of <paramref name="key"/> fail before (then the assistant stays in the VM until something changes)?</summary>
        public static bool FailedBefore(string folder, string key)
        {
            try { return File.Exists(FailedIn(folder)) && File.ReadAllText(FailedIn(folder)).Trim() == key; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }

        /// <summary>A C compiler fire finds on this machine (as a native build looks for one): without one the assistant is not built.</summary>
        public static bool HasCToolchain()
        {
            foreach (string kind in new[] { "gcc", "clang", "msvc", "tcc" })
            {
                try
                {
                    if (fire.Tooling.ToolchainDef.BuiltIn.TryGetValue(kind, out var def) && fire.Tooling.ToolchainDetector.FindC(def) != null) return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
            }
            return false;
        }

        /// <summary>The name of the toolchain in the build's configuration (<see cref="ConfigIn"/>).</summary>
        public const string ToolchainName = "assistant";

        private static string ConfigIn(string folder) => Path.Combine(folder, "assistant.native.json");

        /// <summary>
        /// The compiler the assistant is built with (null: the machine's Build toolchain, which forge takes itself): GCC - the machine's choice for publishing or
        /// building when it is GCC, else a GCC that is there - without optimisation of the program (-O0, about a minute for its 400 000 lines; TinyCC takes seconds,
        /// -O2 minutes) while the engine of the model optimises itself; else Clang or MSVC with their optimisation (as for publishing). Measured (4 cores,
        /// Qwen3.5 0.8B): TinyCC reads a prompt at 2.8 tokens a second and writes 1.6 a second, GCC -O0 with the engine optimised 41 and 14, GCC -O2 40 and 13.
        /// <paramref name="find"/> looks for a compiler (default: <see cref="fire.Tooling.ToolchainDetector.FindC"/>).
        /// </summary>
        public static fire.Tooling.ToolchainDef? BuildToolchain(fire.Tooling.MachineToolchains? machine = null, Func<fire.Tooling.ToolchainDef, string?>? find = null)
        {
            machine ??= fire.Tooling.MachineToolchains.Load();
            find ??= fire.Tooling.ToolchainDetector.FindC;   // (the program is C)
            bool There(string kind)
            {
                try { return fire.Tooling.ToolchainDef.BuiltIn.ContainsKey(kind) && find(machine.DefFor(kind)) != null; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return false; }
            }
            fire.Tooling.ToolchainDef With(string kind, string? optimization, List<string>? args)
            {
                var def = machine.DefFor(kind);
                var copy = new fire.Tooling.ToolchainDef { Extends = kind, Kind = kind }.WithBase(def);
                copy.Extends = kind;
                if (optimization != null) copy.Optimization = optimization;
                if (args != null) copy.Args = args;
                return copy;
            }
            if (There("gcc")) return With("gcc", "-O0", new List<string> { "-w" });
            string publish = fire.Tooling.MachineToolchains.Pick(machine.Publish, fire.Tooling.ToolchainPurpose.Publish, There).Kind ?? "";
            if (publish is "clang" or "msvc") return With(publish, null, null);
            foreach (string kind in new[] { "clang", "msvc" })
                if (There(kind)) return With(kind, null, null);
            return null;
        }

        /// <summary>What a toolchain is for the key of the build (a change builds anew): its kind, compiler and flags.</summary>
        public static string Describe(fire.Tooling.ToolchainDef? toolchain) => toolchain == null ? "machine build"
            : string.Join(" ", toolchain.EffectiveKind, toolchain.Compiler ?? "", toolchain.CCompiler ?? "", toolchain.Optimization ?? "", string.Join(" ", toolchain.Args ?? new List<string>()));

        /// <summary>What <see cref="Look"/> found: the key of the build that fits, the binary when it is built (null: not yet), whether that build failed before,
        /// whether there is a C toolchain for it, or why nothing could be looked at.</summary>
        public readonly record struct State(string? Key, string? Binary, bool FailedBefore, bool Toolchain, string? Error);

        /// <summary>Looks for the native build of <paramref name="script"/> with the forge in <paramref name="baseDirectory"/> (its Source/ folder: the standard
        /// packages) in <paramref name="folder"/>. Reads files: off the UI thread.</summary>
        public static State Look(string script, string baseDirectory, string folder)
        {
            try
            {
                string key = Key(script, Path.Combine(baseDirectory, "Source"), CompilerFiles(baseDirectory), Describe(BuildToolchain()));
                string? binary = Current(folder, key);
                bool failed = binary == null && FailedBefore(folder, key);
                return new State(key, binary, failed, binary == null && !failed && HasCToolchain(), null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new State(null, null, false, false, ex.Message);
            }
        }

        /// <summary>The result of <see cref="Build"/>: the binary (null when it failed) and what forge said.</summary>
        public sealed record BuildResult(string? Binary, string Output, bool Cancelled = false);

        /// <summary>
        /// Builds the script natively with <paramref name="forge"/> into <paramref name="folder"/> (blocking: call it off the UI thread): into a fresh file first, then in
        /// place of the binary, and the key beside it. A failure is remembered for the key (<see cref="FailedBefore"/>). <paramref name="started"/> gets forge's
        /// process (spark ends it when it closes), <paramref name="line"/> every line it writes. Only one build at a time (a lock file): a second one returns null at once.
        /// </summary>
        public static BuildResult Build(string forge, string script, string folder, string key, Action<Process>? started = null, Action<string>? line = null, CancellationToken token = default,
            fire.Tooling.ToolchainDef? toolchain = null)
        {
            Directory.CreateDirectory(folder);
            FileStream gate;
            try { gate = new FileStream(Path.Combine(folder, "build.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { return new BuildResult(null, "Another spark builds the assistant already.", Cancelled: true); }
            using (gate)
            {
                // (what a build left that did not end - spark ended meanwhile)
                foreach (string stale in Directory.GetFiles(folder, "build-*")) try { File.Delete(stale); } catch (Exception) { }
                string fresh = Path.Combine(folder, "build-" + Environment.ProcessId + (OperatingSystem.IsWindows() ? ".exe" : ""));
                var output = new StringBuilder();
                try
                {
                    var info = new ProcessStartInfo(forge) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = folder };
                    info.Environment[fire.Tooling.BackgroundWork.LinesVariable] = "1";
                    foreach (string a in new[] { "build", script, "--engine", "native", "-o", fresh }) info.ArgumentList.Add(a);
                    if (toolchain != null)
                    {
                        // (the compiler as a toolchain of a configuration of its own: forge takes it instead of the machine's Build toolchain)
                        var config = new fire.Tooling.NativeConfig();
                        config.Toolchains[ToolchainName] = toolchain;
                        config.Save(ConfigIn(folder));
                        foreach (string a in new[] { "--config", ConfigIn(folder), "--toolchain", ToolchainName }) info.ArgumentList.Add(a);
                    }
                    using var p = fire.Debugging.ChildProcesses.Add(Process.Start(info) ?? throw new IOException("forge did not start."));
                    try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { }   // (the editor stays quick while the C compiler works)
                    started?.Invoke(p);
                    void Take(string? text)
                    {
                        if (text == null) return;
                        line?.Invoke(text);
                        if (!text.StartsWith(fire.Tooling.BackgroundWork.LinePrefix, StringComparison.Ordinal)) lock (output) output.AppendLine(text);
                    }
                    p.OutputDataReceived += (_, a) => Take(a.Data);
                    p.ErrorDataReceived += (_, a) => Take(a.Data);
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    using (token.Register(() => Stop(p)))
                        p.WaitForExit();
                    string text;
                    lock (output) text = output.ToString().Trim();
                    if (token.IsCancellationRequested) return new BuildResult(null, text, Cancelled: true);
                    if (p.ExitCode != 0 || !File.Exists(fresh))
                    {
                        Remember(FailedIn(folder), key);
                        return new BuildResult(null, text.Length > 0 ? text : $"forge ended with exit code {p.ExitCode}.");
                    }
                    string binary = BinaryIn(folder);
                    File.Move(fresh, binary, true);   // (a binary that runs still - another spark's assistant - keeps the old one: the move fails, the VM runs this time)
                    Remember(StampIn(folder), key);
                    try { File.Delete(FailedIn(folder)); } catch (IOException) { }
                    return new BuildResult(binary, text);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    lock (output) output.AppendLine(ex.Message);
                    if (token.IsCancellationRequested) return new BuildResult(null, output.ToString().Trim(), Cancelled: true);
                    return new BuildResult(null, output.ToString().Trim());
                }
                finally
                {
                    try { if (File.Exists(fresh)) File.Delete(fresh); } catch (Exception) { }
                }
            }
        }

        /// <summary>Ends a build: on Linux first the compilers forge started (forge then sees them fail and removes its folder in the temp directory itself), at the
        /// latest after two seconds forge with everything it started.</summary>
        public static void Stop(Process forge)
        {
            try
            {
                if (OperatingSystem.IsLinux())
                {
                    foreach (int child in ChildrenOf(forge.Id))
                        try { using var c = Process.GetProcessById(child); c.Kill(true); } catch (Exception) { }
                    if (forge.WaitForExit(2000)) return;
                }
                forge.Kill(true);
            }
            catch (Exception) { }
        }

        private static IEnumerable<int> ChildrenOf(int pid)
        {
            var list = new List<int>();
            try
            {
                foreach (string task in Directory.GetDirectories($"/proc/{pid}/task"))
                {
                    string file = Path.Combine(task, "children");
                    if (!File.Exists(file)) continue;
                    foreach (string id in File.ReadAllText(file).Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        if (int.TryParse(id, out int child)) list.Add(child);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return list;
        }

        private static void Remember(string file, string key)
        {
            try { File.WriteAllText(file, key); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
