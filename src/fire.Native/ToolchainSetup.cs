using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace fire.Native
{
    /// <summary>
    /// Finding and providing a C++ toolchain on the machine of the user (docs/PACKAGE_NATIVES.md): native builds and the natives of packages need one. The compiler looks for it
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

        private static string ExeName(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

        /// <summary>The bin folders of the toolchains to look in before the PATH: the portable ones next to the program, then well-known places.</summary>
        public static IEnumerable<string> SearchDirectories()
        {
            string w = Path.Combine(W64devkitDirectory, "bin");
            if (Directory.Exists(w)) yield return w;
            if (Directory.Exists(Root))
                foreach (var dir in Directory.GetDirectories(Root))
                {
                    string bin = Path.Combine(dir, "bin");
                    if (!string.Equals(dir, W64devkitDirectory, StringComparison.OrdinalIgnoreCase) && Directory.Exists(bin)) yield return bin;
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
            foreach (string kind in new[] { "gcc", "clang", "msvc" })
                if (ToolchainDef.BuiltIn[kind] is { } def && ToolchainDetector.Find(def) is { } path) return (def, path);
            return null;
        }

        private static ToolchainDef Gcc(string compiler) => new() { Extends = "gcc", Kind = "gcc", Compiler = compiler, Std = "c++17", Optimization = "-O2", Args = new() { "-Wall", "-Wextra" } };

        /// <summary>The toolchain chosen for the whole machine (written by the installer, by "Detect" and by the editor's toolchain dialog), or null.</summary>
        public static ToolchainDef? LoadMachineToolchain()
        {
            try
            {
                if (!File.Exists(MachineFile)) return null;
                var def = JsonSerializer.Deserialize<ToolchainDef>(File.ReadAllText(MachineFile), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return def?.WithBase(def.Kind is { } k && ToolchainDef.BuiltIn.TryGetValue(k, out var b) ? b : null);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
        }

        public static void SaveMachineToolchain(ToolchainDef def)
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(MachineFile, JsonSerializer.Serialize(def, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
        }

        // -------------------------------------------------------------------------------------------------------------
        // w64devkit
        // -------------------------------------------------------------------------------------------------------------
        private const string LatestReleaseApi = "https://api.github.com/repos/skeeto/w64devkit/releases/latest";
        private const string FallbackUrl = "https://github.com/skeeto/w64devkit/releases/download/v2.0.0/w64devkit-x64-2.0.0.7z.exe";

        private static string FindDownloadUrl(HttpClient http)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
                request.Headers.UserAgent.ParseAdd("fire-toolchain-setup");
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var response = http.Send(request);
                response.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
                {
                    string name = asset.GetProperty("name").GetString() ?? "";
                    if (name.StartsWith("w64devkit-x64-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".7z.exe", StringComparison.OrdinalIgnoreCase))
                        return asset.GetProperty("browser_download_url").GetString()!;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException) { /* the pinned release below */ }
            return FallbackUrl;
        }

        /// <summary>Downloads the portable w64devkit (a self-extracting archive of GCC for Windows) and unpacks it to <see cref="W64devkitDirectory"/>. Returns the compiler.
        /// <paramref name="log"/> gets progress lines. A failure is an <see cref="InvalidOperationException"/> with the reason.</summary>
        public static string InstallW64devkit(Action<string>? log = null)
        {
            if (!CanInstall) throw new InvalidOperationException("The portable toolchain (w64devkit) can only be downloaded on Windows (x64). Install a C++ compiler with your system's package manager.");
            Directory.CreateDirectory(Root);
            string temp = Path.Combine(Path.GetTempPath(), "fire-w64devkit-" + Guid.NewGuid().ToString("N") + ".7z.exe");
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
                string url = FindDownloadUrl(http);
                log?.Invoke($"Downloading {url} ...");
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    request.Headers.UserAgent.ParseAdd("fire-toolchain-setup");
                    using var response = http.Send(request, HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();
                    long total = response.Content.Headers.ContentLength ?? 0;
                    using var input = response.Content.ReadAsStream();
                    using var output = File.Create(temp);
                    var buffer = new byte[81920];
                    long done = 0; int lastPercent = -1;
                    int n;
                    while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        output.Write(buffer, 0, n);
                        done += n;
                        int percent = total > 0 ? (int)(done * 100 / total) : -1;
                        if (percent != lastPercent && (percent % 5 == 0 || percent < 0)) { lastPercent = percent; log?.Invoke(total > 0 ? $"Downloading... {percent}% ({done / 1048576} MB)" : $"Downloading... {done / 1048576} MB"); }
                    }
                    if (total > 0 && done != total) throw new IOException($"The download is incomplete ({done} of {total} bytes). Check the connection (or a proxy / virus scanner) and try again.");
                }
                log?.Invoke($"Unpacking to {Root} ...");
                if (Directory.Exists(W64devkitDirectory)) Directory.Delete(W64devkitDirectory, recursive: true);
                var psi = new ProcessStartInfo(temp, $"-y -o\"{Root}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi)!)
                {
                    p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    if (p.ExitCode != 0) throw new InvalidOperationException($"Unpacking the toolchain failed (exit code {p.ExitCode}).");
                }
                string gpp = Path.Combine(W64devkitDirectory, "bin", "g++.exe");
                if (!File.Exists(gpp)) throw new InvalidOperationException($"The toolchain was unpacked, but '{gpp}' is not there.");
                SaveMachineToolchain(Gcc(gpp));
                log?.Invoke("The toolchain is ready: " + gpp);
                return gpp;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                throw new InvalidOperationException("Installing the toolchain failed: " + ex.Message, ex);
            }
            finally
            {
                try { File.Delete(temp); } catch (IOException) { }
            }
        }
    }

    /// <summary>What the user is asked when a C++ toolchain is needed and there is none.</summary>
    public enum ToolchainChoice { Install, Change, Cancel }

    /// <param name="Reason">Why it is needed.</param>
    /// <param name="CanInstall">Can fire provide it itself (download)?</param>
    /// <param name="InstallDirectory">Where a download is kept.</param>
    /// <param name="Component">What is missing: the C++ toolchain, or a library of the build (SDL2).</param>
    /// <param name="AllowChange">Is there a settings page to change it (the toolchain has one)?</param>
    public sealed record ToolchainRequest(string Reason, bool CanInstall, string InstallDirectory, string Component = "C++ toolchain", bool AllowChange = true)
    {
        public bool IsToolchain => Component == "C++ toolchain";

        /// <summary>The text for the user: why it is needed, and what can be done.</summary>
        public string Message => IsToolchain
            ? Reason + "\n\nA C++ toolchain (compiler) is needed for that, and none was found on this machine.\n\n" +
              (CanInstall
                  ? $"fire can download the portable w64devkit ({ToolchainSetup.DownloadSizeHint}, a GCC for Windows) and keep it in '{InstallDirectory}'. Nothing is installed on the system; it is only used by fire."
                  : "Install a C++ compiler with the package manager of your system (Linux: g++ or clang++, macOS: `xcode-select --install`), or name the compiler yourself.")
            : Reason + $"\n\n{Component} was not found on this machine.\n\n" +
              (CanInstall
                  ? $"fire can download {Component} ({SdlSetup.DownloadSizeHint}) and keep it in '{InstallDirectory}'. Nothing is installed on the system; it is only used by fire."
                  : SdlSetup.HelpText);

        /// <summary>The line below the button "install automatically".</summary>
        public string InstallHint => IsToolchain
            ? (CanInstall ? $"w64devkit is downloaded ({ToolchainSetup.DownloadSizeHint}) and kept locally in {InstallDirectory}. Nothing is installed on the system."
                          : "Only available on Windows. Install a C++ compiler with the package manager of your system.")
            : (CanInstall ? $"{Component} is downloaded ({SdlSetup.DownloadSizeHint}) and kept locally in {InstallDirectory}. Nothing is installed on the system."
                          : "Only available on Windows. Install the development package of your system (see above).");
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

        /// <summary>The libraries that the generated program asks for (<c>// fire-link:</c>) and the machine may lack: today SDL2 for a window. Looks for the development files; if they
        /// are missing the user is asked (download on Windows). Returns null when everything is there, else the reason why the build cannot go on.</summary>
        public static string? RequireLibraries(ToolchainDef toolchain, string cppFile)
        {
            if (toolchain.EffectiveKind is "custom" or "files" || !SdlSetup.IsRequiredBy(cppFile)) return null;
            const string reason = "The program shows a window on the desktop (#import \"windows\"), which is built against SDL2.";
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (SdlSetup.Locate(toolchain) != null) return null;
                if (Ask == null || !SdlSetup.CanInstall) break;
                var choice = Ask(new ToolchainRequest(reason, SdlSetup.CanInstall, SdlSetup.Root, "SDL2", AllowChange: false));
                if (choice != ToolchainChoice.Install) break;
                try
                {
                    Action<Action<string>> work = log => SdlSetup.InstallMingw(log);
                    if (RunInstall != null) { if (!RunInstall(work)) break; }
                    else work(m => Log?.Invoke(m));
                }
                catch (InvalidOperationException ex) { Log?.Invoke(ex.Message); break; }
            }
            if (SdlSetup.Locate(toolchain) != null) return null;
            return reason + "\n" + SdlSetup.HelpText;
        }
    }
}
