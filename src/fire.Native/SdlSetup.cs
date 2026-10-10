using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace fire.Native
{
    /// <summary>Where the SDL2 development files (headers, import library) of a native build are, and what the compiler needs to be told about them.</summary>
    /// <param name="IncludeDirs">Folders for <c>-I</c> (the header is included as <c>&lt;SDL2/SDL.h&gt;</c> or <c>&lt;SDL.h&gt;</c>); empty when the compiler finds them itself.</param>
    /// <param name="LibDirs">Folders for <c>-L</c>.</param>
    /// <param name="RuntimeDll">The <c>SDL2.dll</c> that a Windows program needs next to it, or null.</param>
    /// <param name="Source">Where it was found (for the log).</param>
    public sealed record SdlLocation(IReadOnlyList<string> IncludeDirs, IReadOnlyList<string> LibDirs, string? RuntimeDll, string Source);

    /// <summary>
    /// SDL2 for native builds: a program that shows a window on the desktop (<c>#import "windows"</c>) is compiled against SDL2 (<c>// fire-link: SDL2</c>). The headers and the import library
    /// are looked for in this order: <c>SDL2_DIR</c>, <c>Toolchain\SDL2\</c> next to the program (what <see cref="InstallMingw"/> downloads), the prefix of the compiler (MSYS2:
    /// <c>ucrt64\include\SDL2</c>), the system (<c>/usr/include/SDL2</c>, Homebrew), <c>pkg-config</c>. On Windows with the portable w64devkit, which brings no SDL2, the official
    /// MinGW development package can be downloaded - nothing is installed system-wide.
    /// </summary>
    public static class SdlSetup
    {
        public static string Root => Path.Combine(ToolchainSetup.Root, "SDL2");

        /// <summary>Can this machine download the MinGW development package of SDL2 (Windows on x64)?</summary>
        public static bool CanInstall => OperatingSystem.IsWindows() && RuntimeInformation.OSArchitecture == Architecture.X64;

        public const string DownloadSizeHint = "about 7 MB";

        private const string Triplet = "x86_64-w64-mingw32";
        private const string FallbackVersion = "2.30.9";
        private const string ReleasesApi = "https://api.github.com/repos/libsdl-org/SDL/releases?per_page=40";

        /// <summary>What to do when SDL2 is missing and cannot be provided: the text for the log or the dialog.</summary>
        public static string HelpText =>
            "SDL2 (SDL.h) was not found. A program with a window is built against the SDL2 development files.\n" +
            "  Linux:   install the development package (Debian/Ubuntu: sudo apt install libsdl2-dev; Fedora: sudo dnf install SDL2-devel)\n" +
            "  macOS:   brew install sdl2\n" +
            "  Windows: let fire download SDL2 (it is kept in the folder Toolchain\\SDL2 next to the program), or unpack the MinGW package of SDL2 yourself\n" +
            "  and set the environment variable SDL2_DIR to its folder (the one with the include and lib folders, e.g. SDL2-2.30.9\\x86_64-w64-mingw32).";

        /// <summary>Does the generated program ask for SDL2 (<c>// fire-link: SDL2</c> near its start)?</summary>
        public static bool IsRequiredBy(string cppFile)
        {
            if (!File.Exists(cppFile)) return false;
            foreach (string line in File.ReadLines(cppFile).Take(400))
                if (line.StartsWith("// fire-link: ", StringComparison.Ordinal) && line.Substring("// fire-link: ".Length).Trim() == "SDL2") return true;
            return false;
        }

        // -------------------------------------------------------------------------------------------------------------
        // looking for it
        // -------------------------------------------------------------------------------------------------------------

        /// <summary>The SDL2 development files for this toolchain, or null when there are none.</summary>
        public static SdlLocation? Locate(ToolchainDef? toolchain = null)
        {
            // 1. the environment variable
            if (Environment.GetEnvironmentVariable("SDL2_DIR") is { Length: > 0 } env)
                foreach (string candidate in new[] { env, Path.Combine(env, Triplet), Path.Combine(env, "i686-w64-mingw32") })
                    if (FromPrefix(candidate, "SDL2_DIR") is { } fromEnv) return fromEnv;

            // 2. the copy that fire downloaded
            if (FromPrefix(Root, "Toolchain\\SDL2") is { } own) return own;

            // 3. the prefix of the compiler (MSYS2 ucrt64/mingw64, a MinGW installation, LLVM)
            string? compiler = toolchain == null ? null : ToolchainDetector.Find(toolchain);
            if (compiler != null && Path.GetDirectoryName(compiler) is { } bin && Path.GetDirectoryName(bin) is { } prefix)
                foreach (string candidate in new[] { prefix, Path.Combine(prefix, Triplet) })
                    if (HasHeader(candidate)) return new SdlLocation(Array.Empty<string>(), Array.Empty<string>(), FindDll(candidate), "the prefix of the compiler");

            // 4. the system
            foreach (string prefix2 in new[] { "/usr", "/usr/local" })
                if (HasHeader(prefix2)) return new SdlLocation(Array.Empty<string>(), Array.Empty<string>(), null, prefix2);
            if (OperatingSystem.IsMacOS())
                foreach (string brew in new[] { "/opt/homebrew", "/usr/local" })
                    if (HasHeader(brew)) return new SdlLocation(new[] { Path.Combine(brew, "include") }, new[] { Path.Combine(brew, "lib") }, null, "Homebrew");

            // 5. pkg-config (and sdl2-config): -I and -L only (the library itself is named by the build)
            if (!OperatingSystem.IsWindows() && FromPkgConfig() is { } pkg) return pkg;
            return null;
        }

        private static bool HasHeader(string prefix) =>
            File.Exists(Path.Combine(prefix, "include", "SDL2", "SDL.h")) || File.Exists(Path.Combine(prefix, "include", "SDL.h"));

        private static string? FindDll(string prefix)
        {
            string dll = Path.Combine(prefix, "bin", "SDL2.dll");
            return File.Exists(dll) ? dll : null;
        }

        private static SdlLocation? FromPrefix(string prefix, string source)
        {
            if (!Directory.Exists(prefix) || !HasHeader(prefix)) return null;
            var includes = new List<string> { Path.Combine(prefix, "include") };
            // <SDL.h> lies directly in the folder of some packages, in SDL2/ in others: both are looked in
            string inner = Path.Combine(prefix, "include", "SDL2");
            if (Directory.Exists(inner)) includes.Add(inner);
            return new SdlLocation(includes, new[] { Path.Combine(prefix, "lib") }, FindDll(prefix), source);
        }

        private static SdlLocation? FromPkgConfig()
        {
            try
            {
                var psi = new ProcessStartInfo("pkg-config", "--cflags --libs-only-L sdl2") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                using var p = Process.Start(psi);
                if (p == null) return null;
                string output = p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                if (!p.WaitForExit(5000) || p.ExitCode != 0) return null;
                var includes = new List<string>();
                var libs = new List<string>();
                foreach (string token in output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                    if (token.StartsWith("-I", StringComparison.Ordinal)) includes.Add(token.Substring(2));
                    else if (token.StartsWith("-L", StringComparison.Ordinal)) libs.Add(token.Substring(2));
                return new SdlLocation(includes, libs, null, "pkg-config");
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { return null; }
        }

        /// <summary>A Windows program needs SDL2.dll next to it: copies it there (when this location has one and the program is not there yet). Returns true when it is there afterwards.</summary>
        public static bool CopyRuntimeNextTo(string exe, SdlLocation? location)
        {
            if (location?.RuntimeDll == null || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
            try
            {
                string dest = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(exe))!, "SDL2.dll");
                if (!File.Exists(dest)) File.Copy(location.RuntimeDll, dest);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }

        // -------------------------------------------------------------------------------------------------------------
        // the download (Windows, MinGW)
        // -------------------------------------------------------------------------------------------------------------

        private static (string Url, string Version) FindDownload(HttpClient http)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApi);
                request.Headers.UserAgent.ParseAdd("fire-sdl-setup");
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var response = http.Send(request);
                response.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                foreach (var release in doc.RootElement.EnumerateArray())
                {
                    string tag = release.GetProperty("tag_name").GetString() ?? "";
                    if (!tag.StartsWith("release-2.", StringComparison.Ordinal) || release.GetProperty("prerelease").GetBoolean()) continue;
                    foreach (var asset in release.GetProperty("assets").EnumerateArray())
                    {
                        string name = asset.GetProperty("name").GetString() ?? "";
                        if (name.StartsWith("SDL2-devel-", StringComparison.Ordinal) && name.EndsWith("-mingw.tar.gz", StringComparison.Ordinal))
                            return (asset.GetProperty("browser_download_url").GetString()!, tag.Substring("release-".Length));
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException) { /* the pinned release below */ }
            return ($"https://github.com/libsdl-org/SDL/releases/download/release-{FallbackVersion}/SDL2-devel-{FallbackVersion}-mingw.tar.gz", FallbackVersion);
        }

        /// <summary>Downloads the MinGW development package of SDL2 and keeps the parts a build needs in <see cref="Root"/> (include, lib, bin\SDL2.dll). Returns the folder.
        /// <paramref name="log"/> gets progress lines. A failure is an <see cref="InvalidOperationException"/> with the reason.</summary>
        public static string InstallMingw(Action<string>? log = null)
        {
            if (!CanInstall) throw new InvalidOperationException("SDL2 can only be downloaded on Windows (x64). " + HelpText);
            string temp = Path.Combine(Path.GetTempPath(), "fire-sdl2-" + Guid.NewGuid().ToString("N") + ".tar.gz");
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                var (url, version) = FindDownload(http);
                log?.Invoke($"Downloading {url} ...");
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    request.Headers.UserAgent.ParseAdd("fire-sdl-setup");
                    using var response = http.Send(request, HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();
                    using var input = response.Content.ReadAsStream();
                    using var output = File.Create(temp);
                    input.CopyTo(output);
                }
                log?.Invoke($"Unpacking SDL2 {version} to {Root} ...");
                Unpack(temp, Root);
                if (!HasHeader(Root)) throw new InvalidOperationException($"SDL2 was unpacked, but '{Path.Combine(Root, "include", "SDL2", "SDL.h")}' is not there.");
                File.WriteAllText(Path.Combine(Root, "VERSION.txt"), version + "\n");
                log?.Invoke("SDL2 is ready: " + Root);
                return Root;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidDataException)
            {
                throw new InvalidOperationException("Installing SDL2 failed: " + ex.Message, ex);
            }
            finally
            {
                try { File.Delete(temp); } catch (IOException) { }
            }
        }

        /// <summary>Takes include/, lib/*.dll.a and bin/SDL2.dll of the 64-bit part (<c>SDL2-x.y.z/x86_64-w64-mingw32/</c>) out of the package.</summary>
        public static void Unpack(string archive, string destination)
        {
            string staging = destination + ".new";
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);
            using (var file = File.OpenRead(archive))
            using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            using (var tar = new TarReader(gzip))
            {
                TarEntry? entry;
                while ((entry = tar.GetNextEntry()) != null)
                {
                    if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream == null) continue;
                    string name = entry.Name.Replace('\\', '/');
                    int at = name.IndexOf(Triplet + "/", StringComparison.Ordinal);
                    if (at < 0) continue;
                    string relative = name.Substring(at + Triplet.Length + 1);
                    bool wanted = relative.StartsWith("include/", StringComparison.Ordinal) || relative == "bin/SDL2.dll" || relative == "lib/libSDL2.dll.a";
                    if (!wanted) continue;
                    string target = Path.GetFullPath(Path.Combine(staging, relative));
                    if (!target.StartsWith(Path.GetFullPath(staging) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using var output = File.Create(target);
                    entry.DataStream.CopyTo(output);
                }
            }
            if (Directory.Exists(destination)) Directory.Delete(destination, true);
            Directory.Move(staging, destination);
        }
    }
}
