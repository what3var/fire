using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace fire.Tooling
{
    /// <summary>
    /// Finding and providing a C++ toolchain on the machine of the user (docs/packages/natives.md): native builds and the natives of packages need one. The compiler looks for it
    /// in this order: the toolchain that was chosen for the machine (<c>Toolchain\toolchain.json</c>), the portable toolchains below <c>Toolchain\</c> next to the program
    /// (w64devkit first), well-known places, the PATH. On Windows the portable <b>w64devkit</b> (GCC) can be downloaded into <c>Toolchain\w64devkit\</c> - nothing is installed system-wide.
    /// </summary>
    public static class ToolchainSetup
    {
        /// <summary>The folder next to the program where toolchains are kept.</summary>
        public static string Root => Path.Combine(AppContext.BaseDirectory, "Toolchain");
        public static string W64devkitDirectory => Path.Combine(Root, "w64devkit");
        private static string MachineFile => Path.Combine(Root, "toolchain.json");

        /// <summary>Can this machine download the portable toolchain (Windows on x64)?</summary>
        public static bool CanInstall => OperatingSystem.IsWindows() && RuntimeInformation.OSArchitecture == Architecture.X64;

        public const string DownloadSizeHint = "about 80 MB";
        /// <summary>The download in bytes, about (<see cref="DownloadSizeHint"/>; the setup adds the downloads up).</summary>
        public const long DownloadBytes = 80_000_000;

        private static string ExeName(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

        /// <summary>The bin folders of the toolchains to look in before the PATH: the portable ones next to the program, then well-known places.</summary>
        public static IEnumerable<string> SearchDirectories()
        {
            string w = Path.Combine(W64devkitDirectory, "bin");
            if (Directory.Exists(w)) yield return w;
            if (Directory.Exists(Root))
                foreach (var dir in Directory.GetDirectories(Root))
                {
                    if (string.Equals(dir, W64devkitDirectory, StringComparison.OrdinalIgnoreCase)) continue;
                    // Open Watcom has no bin: its compilers are in binnt64/binnt (Windows) or binl64/binl (Linux) - the portable archive brings only the 32-bit ones
                    if (string.Equals(dir, WatcomSetup.Directory, StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (string ow in WatcomToolchain.CompilerFolders.Select(b => Path.Combine(dir, b)).Where(Directory.Exists)) yield return ow;
                        continue;
                    }
                    // the portable TinyCC (TinyccSetup) has tcc.exe at its top, no bin
                    if (string.Equals(dir, TinyccSetup.Directory, StringComparison.OrdinalIgnoreCase)) { yield return dir; continue; }
                    string bin = Path.Combine(dir, "bin");
                    if (Directory.Exists(bin)) yield return bin;
                }
            if (OperatingSystem.IsWindows())
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                foreach (var dir in new[]
                {
                    @"C:\w64devkit\bin", Path.Combine(home, "w64devkit", "bin"),
                    @"C:\msys64\ucrt64\bin", @"C:\msys64\mingw64\bin", @"C:\mingw64\bin", @"C:\MinGW\bin",
                    @"C:\Program Files\LLVM\bin",
                })
                    if (Directory.Exists(dir)) yield return dir;
            }
        }

        /// <summary>The full path of the program <paramref name="name"/> (`g++`, `clang++`, `cl`) in the toolchain folders or on the PATH, or null.</summary>
        public static string? FindProgram(string name)
        {
            if (Path.IsPathRooted(name)) return File.Exists(name) ? name : File.Exists(name + ".exe") ? name + ".exe" : null;
            foreach (string dir in SearchDirectories().Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(d => d.Trim('"'))))
            {
                string candidate = Path.Combine(dir, name);
                if (File.Exists(candidate) && !OperatingSystem.IsWindows()) return candidate;
                if (OperatingSystem.IsWindows() && File.Exists(candidate + ".exe")) return candidate + ".exe";
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        /// <summary>Looks for a toolchain on this machine; w64devkit (in Toolchain\ or a well-known place) wins, then g++, clang++, cl. Null when there is none.</summary>
        public static (ToolchainDef Toolchain, string Path)? Detect()
        {
            if (LoadMachineToolchain() is { } chosen && ToolchainDetector.Find(chosen) is { } chosenPath) return (chosen, chosenPath);
            foreach (string dir in SearchDirectories())
                if (File.Exists(Path.Combine(dir, ExeName("g++")))) return (Gcc(Path.Combine(dir, ExeName("g++"))), Path.Combine(dir, ExeName("g++")));
            foreach (string kind in new[] { "gcc", "clang", "msvc", "tcc" })   // (TinyCC last: it compiles no C++)
                if (ToolchainDef.BuiltIn[kind] is { } def && ToolchainDetector.Find(def) is { } path) return (def, path);
            return null;
        }

        private static ToolchainDef Gcc(string compiler) => new() { Extends = "gcc", Kind = "gcc", Compiler = compiler, Std = "c++17", Optimization = "-O2", Args = new() { "-Wall", "-Wextra" } };

        /// <summary>The toolchain chosen for the whole machine (written by the installer, by "Detect" and by the editor's toolchain dialog), or null. The same file keeps the
        /// compilers chosen for building and publishing (<see cref="MachineToolchains"/>).</summary>
        public static ToolchainDef? LoadMachineToolchain() => MachineToolchains.Load(MachineFile).Legacy;

        /// <summary>Writes the machine's toolchain; the choice for building and publishing in the file stays.</summary>
        public static void SaveMachineToolchain(ToolchainDef def)
        {
            Directory.CreateDirectory(Root);
            var json = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(def, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }))!.AsObject();
            try
            {
                if (File.Exists(MachineFile) && System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(MachineFile), MachineToolchains.CaseInsensitive) is System.Text.Json.Nodes.JsonObject old)
                    foreach (string key in new[] { "build", "publish" })
                        if (old[key] is { } value) json[key] = value.DeepClone();
            }
            catch (JsonException) { }
            File.WriteAllText(MachineFile, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        /// <summary>Downloads the portable w64devkit (a self-extracting archive of GCC for Windows) and unpacks it to <see cref="W64devkitDirectory"/>. Returns the compiler.
        /// <paramref name="log"/> gets progress lines. A failure is an <see cref="InvalidOperationException"/> with the reason.
        /// <paramref name="progress"/> gets the bytes of the download (done, total); <paramref name="token"/> stops it (an <see cref="OperationCanceledException"/>, nothing is kept).</summary>
        public static string InstallW64devkit(Action<string>? log = null, Action<long, long>? progress = null, CancellationToken token = default)
        {
            if (!CanInstall) throw new InvalidOperationException("The portable toolchain (w64devkit) can only be downloaded on Windows (x64). Install a C++ compiler with your system's package manager.");
            return ToolchainInstall.Run("w64devkit", log, progress, DownloadBytes, token, () => Path.Combine(W64devkitDirectory, "bin", "g++.exe"));
        }
    }

    /// <summary>
    /// The portable Open Watcom v2 (the toolchain of the old PC targets: dos16, dos32, win16, bios16; ROADMAP 13): downloaded as one zip into
    /// <c>Toolchain\watcom\</c> next to the program, where <see cref="WatcomToolchain"/> looks for it - nothing is installed system-wide. The archive brings the compilers
    /// for Windows (binnt64) and Linux (binl64) and the headers and libraries of every target.
    /// </summary>
    public static class WatcomSetup
    {
        public const string DownloadUrl = "http://openwatcom.org/ftp/source/ow_portable_v2_stable.zip";
        public const string DownloadSizeHint = "about 150 MB";
        public const long DownloadBytes = 150_000_000;
        public static string Directory => Path.Combine(ToolchainSetup.Root, "watcom");

        /// <summary>Can this machine use the portable archive (Windows or Linux on x64)?</summary>
        public static bool CanInstall => RuntimeInformation.OSArchitecture == Architecture.X64 && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux());

        /// <summary>Downloads the portable Open Watcom and unpacks it to <see cref="Directory"/>. Returns its root (the WATCOM folder). A failure is an
        /// <see cref="InvalidOperationException"/> with the reason. <paramref name="progress"/> gets the bytes of the download (done, total); <paramref name="token"/> stops it
        /// (an <see cref="OperationCanceledException"/>, nothing is kept).</summary>
        public static string Install(Action<string>? log = null, Action<long, long>? progress = null, CancellationToken token = default)
        {
            if (!CanInstall) throw new InvalidOperationException("The portable Open Watcom can only be downloaded on Windows or Linux (x64). Install Open Watcom v2 yourself and set WATCOM.");
            return ToolchainInstall.Run("watcom", log, progress, DownloadBytes, token, () => Directory);
        }
    }

    /// <summary>What the user is asked when a C++ toolchain is needed and there is none.</summary>
    public enum ToolchainChoice { Install, Change, Cancel }

    /// <param name="Reason">Why it is needed.</param>
    /// <param name="CanInstall">Can fire provide it itself (download)?</param>
    /// <param name="InstallDirectory">Where a download is kept.</param>
    /// <param name="Component">What is missing: the C++ toolchain, or the compiler of a target (Open Watcom).</param>
    /// <param name="AllowChange">Is there a settings page to change it (the toolchain has one)?</param>
    public sealed record ToolchainRequest(string Reason, bool CanInstall, string InstallDirectory, string Component = "C++ toolchain", bool AllowChange = true)
    {
        public bool IsToolchain => Component == "C++ toolchain";

        /// <summary>The text for the user: why it is needed, and what can be done.</summary>
        public bool IsWatcom => Component == WatcomComponent;
        public const string WatcomComponent = "Open Watcom";

        public string Message => IsWatcom
            ? Reason + "\n\nThis target is built with Open Watcom v2, and it was not found on this machine (on the PATH, in %WATCOM%, in Toolchain\\watcom or /opt/watcom).\n\n" +
              (CanInstall
                  ? $"fire can download the portable Open Watcom ({WatcomSetup.DownloadSizeHint}) and keep it in '{InstallDirectory}'. Nothing is installed on the system; it is only used by fire."
                  : "Install Open Watcom v2 (open-watcom.github.io) and set WATCOM to its folder.")
            : Reason + "\n\nA C++ toolchain (compiler) is needed for that, and none was found on this machine.\n\n" +
              (CanInstall
                  ? $"fire can download the portable w64devkit ({ToolchainSetup.DownloadSizeHint}, a GCC for Windows) and keep it in '{InstallDirectory}'. Nothing is installed on the system; it is only used by fire."
                  : "Install a C++ compiler with the package manager of your system (Linux: g++ or clang++, macOS: `xcode-select --install`), or name the compiler yourself.");

        /// <summary>The line below the button "install automatically".</summary>
        public string InstallHint => IsWatcom
            ? (CanInstall ? $"Open Watcom is downloaded ({WatcomSetup.DownloadSizeHint}) and kept locally in {InstallDirectory}. Nothing is installed on the system."
                          : "Only available on Windows and Linux (x64). Install Open Watcom v2 yourself.")
            : (CanInstall ? $"w64devkit is downloaded ({ToolchainSetup.DownloadSizeHint}) and kept locally in {InstallDirectory}. Nothing is installed on the system."
                          : "Only available on Windows. Install a C++ compiler with the package manager of your system.");
    }

    /// <summary>
    /// Makes sure that a C++ toolchain is there when one is needed. The host (the editor, the command line) decides how to ask the user: <see cref="Ask"/>; without one nothing is
    /// asked and the caller reports that there is no toolchain. A native build and the natives of packages call <see cref="Require"/>.
    /// </summary>
    public static class ToolchainProvider
    {
        /// <summary>Asks the user: install automatically, change the toolchain, or cancel.</summary>
        public static Func<ToolchainRequest, ToolchainChoice>? Ask { get; set; }
        /// <summary>The user wants to configure the toolchain: shows the configuration; true when it was saved (then the toolchain is looked for again).</summary>
        public static Func<bool>? ChangeToolchain { get; set; }
        /// <summary>Runs the download (a host can show progress and keep its window alive); default: right here.</summary>
        public static Func<Action<Action<string>>, bool>? RunInstall { get; set; }
        public static Action<string>? Log { get; set; }

        /// <summary>A toolchain that can be used: <paramref name="wanted"/> if it is there, else any that is found; else the user is asked (install / change / cancel) until there is one or
        /// the user cancels (null). <paramref name="reason"/> says why a toolchain is needed.</summary>
        public static ToolchainDef? Require(ToolchainDef wanted, string reason)
        {
            if (string.Equals(wanted.EffectiveKind, "watcom", StringComparison.OrdinalIgnoreCase)) return RequireWatcom(wanted, reason);
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (ToolchainDetector.Find(wanted) != null) return wanted;
                if (ToolchainSetup.Detect() is { } found) return found.Toolchain;
                if (Ask == null) return null;
                var choice = Ask(new ToolchainRequest(reason, ToolchainSetup.CanInstall, ToolchainSetup.W64devkitDirectory));
                switch (choice)
                {
                    case ToolchainChoice.Cancel: return null;
                    case ToolchainChoice.Change:
                        if (ChangeToolchain == null || !ChangeToolchain()) return null;
                        break;
                    case ToolchainChoice.Install:
                        if (!ToolchainSetup.CanInstall) return null;
                        try
                        {
                            Action<Action<string>> work = log => ToolchainSetup.InstallW64devkit(log);
                            if (RunInstall != null) { if (!RunInstall(work)) return null; }
                            else work(m => Log?.Invoke(m));
                        }
                        catch (InvalidOperationException ex) { Log?.Invoke(ex.Message); return null; }
                        break;
                }
            }
            return null;
        }

        /// <summary>Open Watcom for a target that only it builds (DOS, Windows 3.x, BIOS): no other compiler stands in for it; the user is asked to download it
        /// (Windows, Linux), to change the toolchain, or to cancel.</summary>
        private static ToolchainDef? RequireWatcom(ToolchainDef wanted, string reason)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (ToolchainDetector.Find(wanted) != null) return wanted;
                if (Ask == null) return null;
                var choice = Ask(new ToolchainRequest(reason, WatcomSetup.CanInstall, WatcomSetup.Directory, ToolchainRequest.WatcomComponent));
                switch (choice)
                {
                    case ToolchainChoice.Cancel: return null;
                    case ToolchainChoice.Change:
                        if (ChangeToolchain == null || !ChangeToolchain()) return null;
                        break;
                    case ToolchainChoice.Install:
                        if (!WatcomSetup.CanInstall) return null;
                        try
                        {
                            Action<Action<string>> work = log => WatcomSetup.Install(log);
                            if (RunInstall != null) { if (!RunInstall(work)) return null; }
                            else work(m => Log?.Invoke(m));
                        }
                        catch (InvalidOperationException ex) { Log?.Invoke(ex.Message); return null; }
                        // installed, but the build does not find it: say where it looked (instead of asking again without a reason)
                        if (ToolchainDetector.Find(wanted) == null)
                        {
                            Log?.Invoke($"Open Watcom was installed to '{WatcomSetup.Directory}', but '{wanted.Compiler ?? "owcc"}' was not found on the PATH or in: "
                                + string.Join(", ", WatcomToolchain.SearchedFolders()));
                            return null;
                        }
                        break;
                }
            }
            return null;
        }
    }
}
