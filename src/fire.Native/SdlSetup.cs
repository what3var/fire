using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace fire.Native
{
    /// <summary>Where the SDL development files (headers, import library) of a native build are, and what the compiler needs to be told about them.</summary>
    /// <param name="IncludeDirs">Folders for <c>-I</c> (the header is included as <c>&lt;SDL2/SDL.h&gt;</c> or <c>&lt;SDL.h&gt;</c>); empty when the compiler finds them itself.</param>
    /// <param name="LibDirs">Folders for <c>-L</c>.</param>
    /// <param name="RuntimeDll">The <c>SDL2.dll</c> (<c>SDL3.dll</c>) that a Windows program needs next to it, or null.</param>
    /// <param name="Source">Where it was found (for the log).</param>
    public sealed record SdlLocation(IReadOnlyList<string> IncludeDirs, IReadOnlyList<string> LibDirs, string? RuntimeDll, string Source);

    /// <summary>
    /// SDL for native builds: a program that shows a window on the desktop (<c>#import "windows"</c>) is compiled against SDL3 or SDL2 (<c>// fire-link: SDL3</c> or <c>SDL2</c>;
    /// the build takes SDL3 when the toolchain can compile and link it, see NativeBuilder.ChooseSdl). The headers and the import library of a version are looked for in this
    /// order: <c>SDL3_DIR</c>/<c>SDL2_DIR</c>, <c>Toolchain\SDL3\</c> or <c>Toolchain\SDL2\</c> next to the program (what <see cref="InstallMingw"/> downloads), the prefix of
    /// the compiler (MSYS2: <c>ucrt64\include\SDL3</c>), the system (<c>/usr/include/SDL3</c>, <c>/usr/local</c>, Homebrew), <c>pkg-config</c>. On Windows with the portable
    /// w64devkit, which brings no SDL, the official MinGW development package can be downloaded - nothing is installed system-wide. The functions without a version are SDL2's.
    /// </summary>
    public static class SdlSetup
    {
        public static string Root => RootFor(2);

        /// <summary>Where fire keeps the downloaded MinGW package of this version (Toolchain\SDL2, Toolchain\SDL3).</summary>
        public static string RootFor(int version) => Path.Combine(ToolchainSetup.Root, "SDL" + version);

        /// <summary>Can this machine download the MinGW development package of SDL (Windows on x64)?</summary>
        public static bool CanInstall => OperatingSystem.IsWindows() && RuntimeInformation.OSArchitecture == Architecture.X64;

        public const string DownloadSizeHint = "about 7 MB";

        private const string Triplet = "x86_64-w64-mingw32";
        private static string FallbackVersion(int version) => version == 3 ? "3.2.26" : "2.30.9";
        private const string ReleasesApi = "https://api.github.com/repos/libsdl-org/SDL/releases?per_page=40";

        /// <summary>What to do when SDL2 is missing and cannot be provided: the text for the log or the dialog.</summary>
        public static string HelpText => HelpTextFor(2);

        /// <summary>What to do when this version of SDL is missing.</summary>
        public static string HelpTextFor(int version) => version == 3
            ? "SDL3 (SDL3/SDL.h) was not found. A program with a window is built against the SDL3 development files (or SDL2's).\n" +
              "  Linux:   install the development package (Debian/Ubuntu 25.04 and newer: sudo apt install libsdl3-dev; Fedora: sudo dnf install SDL3-devel), or build SDL3 from its source release\n" +
              "  macOS:   brew install sdl3\n" +
              "  Windows: let fire download SDL3 (it is kept in the folder Toolchain\\SDL3 next to the program), or unpack the MinGW package of SDL3 yourself\n" +
              "  and set the environment variable SDL3_DIR to its folder (the one with the include and lib folders, e.g. SDL3-3.2.26\\x86_64-w64-mingw32)."
            : "SDL2 (SDL.h) was not found. A program with a window is built against the SDL2 development files.\n" +
              "  Linux:   install the development package (Debian/Ubuntu: sudo apt install libsdl2-dev; Fedora: sudo dnf install SDL2-devel)\n" +
              "  macOS:   brew install sdl2\n" +
              "  Windows: let fire download SDL2 (it is kept in the folder Toolchain\\SDL2 next to the program), or unpack the MinGW package of SDL2 yourself\n" +
              "  and set the environment variable SDL2_DIR to its folder (the one with the include and lib folders, e.g. SDL2-2.30.9\\x86_64-w64-mingw32).";

        /// <summary>Does the generated program ask for SDL2 (a line <c>// fire-link: SDL2</c>)?</summary>
        public static bool IsRequiredBy(string cppFile) => RequiredVersion(cppFile) == 2;

        /// <summary>The SDL the generated program asks for: 3 (<c>// fire-link: SDL3</c>), 2 (<c>SDL2</c>), 0 (none).</summary>
        public static int RequiredVersion(string cppFile)
        {
            if (!File.Exists(cppFile)) return 0;
            foreach (string line in File.ReadLines(cppFile))
                if (line.StartsWith("// fire-link: ", StringComparison.Ordinal))
                    switch (line.Substring("// fire-link: ".Length).Trim())
                    {
                        case "SDL2": return 2;
                        case "SDL3": return 3;
                    }
            return 0;
        }

        // -------------------------------------------------------------------------------------------------------------
        // looking for it
        // -------------------------------------------------------------------------------------------------------------

        /// <summary>The SDL development files (<paramref name="version"/> 2 or 3) for this toolchain, or null when there are none.</summary>
        public static SdlLocation? Locate(ToolchainDef? toolchain = null, int version = 2)
        {
            // 1. the environment variable
            if (Environment.GetEnvironmentVariable($"SDL{version}_DIR") is { Length: > 0 } env)
                foreach (string candidate in new[] { env, Path.Combine(env, Triplet), Path.Combine(env, "i686-w64-mingw32") })
                    if (FromPrefix(candidate, $"SDL{version}_DIR", version) is { } fromEnv) return fromEnv;

            // 2. the copy that fire downloaded
            if (FromPrefix(RootFor(version), $"Toolchain\\SDL{version}", version) is { } own) return own;

            // 3. the prefix of the compiler (MSYS2 ucrt64/mingw64, a MinGW installation, LLVM)
            string? compiler = toolchain == null ? null : ToolchainDetector.Find(toolchain);
            if (compiler != null && Path.GetDirectoryName(compiler) is { } bin && Path.GetDirectoryName(bin) is { } prefix)
                foreach (string candidate in new[] { prefix, Path.Combine(prefix, Triplet) })
                    if (HasHeader(candidate, version)) return new SdlLocation(Array.Empty<string>(), Array.Empty<string>(), FindDll(candidate, version), "the prefix of the compiler");

            // 4. the system
            foreach (string prefix2 in new[] { "/usr", "/usr/local" })
                if (HasHeader(prefix2, version)) return new SdlLocation(Array.Empty<string>(), Array.Empty<string>(), null, prefix2);
            if (OperatingSystem.IsMacOS())
                foreach (string brew in new[] { "/opt/homebrew", "/usr/local" })
                    if (HasHeader(brew, version)) return new SdlLocation(new[] { Path.Combine(brew, "include") }, new[] { Path.Combine(brew, "lib") }, null, "Homebrew");

            // 5. pkg-config: -I and -L only (the library itself is named by the build)
            if (!OperatingSystem.IsWindows() && FromPkgConfig(version) is { } pkg) return pkg;
            return null;
        }

        private static bool HasHeader(string prefix, int version = 2) => version == 3
            ? File.Exists(Path.Combine(prefix, "include", "SDL3", "SDL.h"))
            : File.Exists(Path.Combine(prefix, "include", "SDL2", "SDL.h")) || File.Exists(Path.Combine(prefix, "include", "SDL.h"));

        private static string? FindDll(string prefix, int version = 2)
        {
            string dll = Path.Combine(prefix, "bin", $"SDL{version}.dll");
            return File.Exists(dll) ? dll : null;
        }

        private static SdlLocation? FromPrefix(string prefix, string source, int version = 2)
        {
            if (!Directory.Exists(prefix) || !HasHeader(prefix, version)) return null;
            var includes = new List<string> { Path.Combine(prefix, "include") };
            // <SDL.h> lies directly in the folder of some SDL2 packages, in SDL2/ in others: both are looked in (SDL3 is always <SDL3/SDL.h>)
            string inner = Path.Combine(prefix, "include", "SDL2");
            if (version == 2 && Directory.Exists(inner)) includes.Add(inner);
            return new SdlLocation(includes, new[] { Path.Combine(prefix, "lib") }, FindDll(prefix, version), source);
        }

        private static SdlLocation? FromPkgConfig(int version = 2)
        {
            try
            {
                var psi = new ProcessStartInfo("pkg-config", $"--cflags --libs-only-L sdl{version}") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
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

        /// <summary>A Windows program needs SDL2.dll (or SDL3.dll) next to it: copies the DLL of this location there (when it has one and the program is not there yet). Returns
        /// true when it is there afterwards.</summary>
        public static bool CopyRuntimeNextTo(string exe, SdlLocation? location)
        {
            if (location?.RuntimeDll == null || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
            try
            {
                string dest = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(exe))!, Path.GetFileName(location.RuntimeDll));
                if (!File.Exists(dest)) File.Copy(location.RuntimeDll, dest);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }

        // -------------------------------------------------------------------------------------------------------------
        // the download (Windows, MinGW)
        // -------------------------------------------------------------------------------------------------------------

        private static (string Url, string Version) FindDownload(HttpClient http, int version)
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
                    if (!tag.StartsWith($"release-{version}.", StringComparison.Ordinal) || release.GetProperty("prerelease").GetBoolean()) continue;
                    foreach (var asset in release.GetProperty("assets").EnumerateArray())
                    {
                        string name = asset.GetProperty("name").GetString() ?? "";
                        if (name.StartsWith($"SDL{version}-devel-", StringComparison.Ordinal) && name.EndsWith("-mingw.tar.gz", StringComparison.Ordinal))
                            return (asset.GetProperty("browser_download_url").GetString()!, tag.Substring("release-".Length));
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException) { /* the pinned release below */ }
            string fallback = FallbackVersion(version);
            return ($"https://github.com/libsdl-org/SDL/releases/download/release-{fallback}/SDL{version}-devel-{fallback}-mingw.tar.gz", fallback);
        }

        /// <summary>Downloads the MinGW development package of SDL (<paramref name="version"/> 2 or 3) and keeps the parts a build needs in <see cref="RootFor"/> (include, lib,
        /// bin\SDLn.dll). Returns the folder. <paramref name="log"/> gets progress lines. A failure is an <see cref="InvalidOperationException"/> with the reason.</summary>
        public static string InstallMingw(Action<string>? log = null, int version = 2)
        {
            if (!CanInstall) throw new InvalidOperationException($"SDL{version} can only be downloaded on Windows (x64). " + HelpTextFor(version));
            string root = RootFor(version);
            string temp = Path.Combine(Path.GetTempPath(), $"fire-sdl{version}-" + Guid.NewGuid().ToString("N") + ".tar.gz");
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                var (url, release) = FindDownload(http, version);
                // A connection (or a proxy, or a virus scanner) can cut the download short without an error; a package that is not complete is noticed here and fetched again.
                const int attempts = 3;
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        log?.Invoke(attempt == 1 ? $"Downloading {url} ..." : $"Downloading {url} again (attempt {attempt} of {attempts}) ...");
                        Download(http, url, temp);
                        break;
                    }
                    catch (Exception ex) when (attempt < attempts && ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException)
                    {
                        log?.Invoke("The download failed: " + ex.Message);
                    }
                }
                log?.Invoke($"Unpacking SDL{version} {release} to {root} ...");
                Unpack(temp, root, version);
                if (!HasHeader(root, version)) throw new InvalidOperationException($"SDL{version} was unpacked, but '{Path.Combine(root, "include", "SDL" + version, "SDL.h")}' is not there.");
                File.WriteAllText(Path.Combine(root, "VERSION.txt"), release + "\n");
                log?.Invoke($"SDL{version} is ready: " + root);
                return root;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidDataException)
            {
                throw new InvalidOperationException($"Installing SDL{version} failed: " + ex.Message + "\n" + HelpTextFor(version), ex);
            }
            finally
            {
                try { File.Delete(temp); } catch (IOException) { }
            }
        }

        /// <summary>Downloads <paramref name="url"/> into <paramref name="file"/> and checks that all of it arrived: the length the server announced, and the end of the gzip stream (see <see cref="VerifyGzip"/>).</summary>
        private static void Download(HttpClient http, string url, string file)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("fire-sdl-setup");
            using var response = http.Send(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            long? expected = response.Content.Headers.ContentLength;
            using (var input = response.Content.ReadAsStream())
            using (var output = File.Create(file))
                input.CopyTo(output);
            long received = new FileInfo(file).Length;
            if (expected is { } announced && received != announced) throw new IOException($"The download is incomplete ({received} of {announced} bytes).");
            VerifyGzip(file);
        }

        /// <summary>Checks that a .gz file is whole: decompresses it and compares the number of bytes with the length that the trailer of the file states. (A gzip stream that is cut off
        /// ends without an error in <see cref="GZipStream"/>; the tar reader would then fail in the middle of a header with "Unable to read beyond the end of the stream".)</summary>
        public static void VerifyGzip(string file)
        {
            using var stream = File.OpenRead(file);
            if (stream.Length < 18) throw new InvalidDataException($"The archive is incomplete or damaged ({stream.Length} bytes).");
            var trailer = new byte[4];
            stream.Seek(-4, SeekOrigin.End);
            stream.ReadExactly(trailer, 0, 4);
            uint stated = BitConverter.ToUInt32(trailer, 0);   // the size of the data modulo 2^32, little endian
            stream.Seek(0, SeekOrigin.Begin);
            long total = 0;
            try
            {
                using var gzip = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
                var buffer = new byte[81920];
                int n;
                while ((n = gzip.Read(buffer, 0, buffer.Length)) > 0) total += n;
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidDataException("The archive is damaged: " + ex.Message, ex);
            }
            if ((uint)total != stated) throw new InvalidDataException("The archive is incomplete (the download was cut off).");
        }

        /// <summary>Takes include/, lib/libSDLn.dll.a and bin/SDLn.dll of the 64-bit part (<c>SDL2-x.y.z/x86_64-w64-mingw32/</c>) out of the package.</summary>
        public static void Unpack(string archive, string destination, int version = 2)
        {
            VerifyGzip(archive);   // (before anything is touched)
            string staging = destination + ".new";
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);

            try
            {
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
                        bool wanted = relative.StartsWith("include/", StringComparison.Ordinal) || relative == $"bin/SDL{version}.dll" || relative == $"lib/libSDL{version}.dll.a";
                        if (!wanted) continue;
                        string target = Path.GetFullPath(Path.Combine(staging, relative));
                        if (!target.StartsWith(Path.GetFullPath(staging) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        using var output = File.Create(target);
                        entry.DataStream.CopyTo(output);
                    }
                }
            }
            catch (EndOfStreamException) when (HasHeader(staging, version) && File.Exists(Path.Combine(staging, "bin", $"SDL{version}.dll")) && File.Exists(Path.Combine(staging, "lib", $"libSDL{version}.dll.a")))
            {
                // The packages from SDL 2.30.11 on end without the two empty blocks that close a tar file (the last file, .git-hash, is not even padded to the end of its block): the reader
                // reports the end of the stream in the middle of a header. The gzip stream itself was checked above; what we came for is all there.
            }

            if (Directory.Exists(destination)) Directory.Delete(destination, true);
            Directory.Move(staging, destination);
        }
    }

    /// <summary>
    /// The answers of the probes of native builds (can this toolchain compile and link SDL3?), remembered so that the tiny test program is built only once: in memory, and in
    /// <c>native-probes.json</c> in the folder of fire's local data (LocalApplicationData/fire). The key names everything the answer depends on - the compiler (its path and
    /// time), the target and where the library was found (its folders and the time of its header) -, so that installing a compiler or a library asks again.
    /// FIRE_PROBE_CACHE=0 switches the file off (the memory stays).
    /// </summary>
    public static class NativeProbeCache
    {
        private static readonly object Gate = new();
        private static Dictionary<string, bool>? _answers;

        /// <summary>The file (null: no file).</summary>
        public static string? FilePath { get; set; } = DefaultPath();

        private static string? DefaultPath()
        {
            if (Environment.GetEnvironmentVariable("FIRE_PROBE_CACHE") == "0") return null;
            string data = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrEmpty(data) ? null : Path.Combine(data, "fire", "native-probes.json");
        }

        private static Dictionary<string, bool> Answers()
        {
            if (_answers != null) return _answers;
            _answers = new Dictionary<string, bool>();
            try
            {
                if (FilePath != null && File.Exists(FilePath))
                    _answers = JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(FilePath)) ?? new();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            return _answers;
        }

        /// <summary>The remembered answer for <paramref name="key"/>, or null.</summary>
        public static bool? Get(string key)
        {
            lock (Gate) return Answers().TryGetValue(key, out bool answer) ? answer : null;
        }

        /// <summary>Remembers an answer (and writes the file, when there is one).</summary>
        public static void Set(string key, bool answer)
        {
            lock (Gate)
            {
                var answers = Answers();
                answers[key] = answer;
                try
                {
                    if (FilePath == null) return;
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                    File.WriteAllText(FilePath, JsonSerializer.Serialize(answers, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }

        /// <summary>Forgets what is in memory (the file stays; tests set <see cref="FilePath"/>).</summary>
        public static void Reset() { lock (Gate) _answers = null; }

        /// <summary>A file's path with its time of change: part of a key.</summary>
        public static string Stamp(string? path)
        {
            if (string.IsNullOrEmpty(path)) return "-";
            try { return File.Exists(path) ? $"{path}@{File.GetLastWriteTimeUtc(path).Ticks}" : path + "@-"; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return path + "@?"; }
        }
    }
}
