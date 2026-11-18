using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace fire.Native
{
    /// <summary>
    /// The portable TinyCC for Windows (the official binary zip of tinycc 0.9.27 from download.savannah.gnu.org): a small, very fast C compiler - it compiles C only, no C++
    /// (enough for programs whose packages are all in C). Unpacked to <c>Toolchain\tcc\</c> next to the program, where <see cref="ToolchainSetup.SearchDirectories"/> finds
    /// <c>tcc.exe</c>; nothing is installed system-wide. The archive is checked against its pinned SHA-256 before it is unpacked. Linux and macOS: the package manager (no official binary).
    /// </summary>
    public static class TinyccSetup
    {
        public const string Version = "0.9.27";
        public const string DownloadSizeHint = "about 1 MB";
        public const long DownloadBytes = 1_000_000;

        /// <summary>The pinned SHA-256 of the official zips (lower case hex). Empty: not pinned yet - then <see cref="CanInstall"/> is false and nothing is downloaded unchecked.</summary>
        public const string Sha256Win64 = "34a721949a2583fdff725312da092fa0f5f1f284b702e6f811c6954714faabb2";
        public const string Sha256Win32 = "02e2bfe8c272a549b15e4bfa4507bd7e05304692af1761db6c1e8e88af675651";

        public static string Directory => Path.Combine(ToolchainSetup.Root, "tcc");
        public static string Compiler => Path.Combine(Directory, "tcc.exe");

        private static bool Is64 => RuntimeInformation.OSArchitecture == Architecture.X64 || RuntimeInformation.OSArchitecture == Architecture.Arm64;

        /// <summary>The zip and its hash for this machine (win64 on x64/arm64 Windows, else win32).</summary>
        public static (string Url, string Sha256) Download => Is64
            ? ($"https://download.savannah.gnu.org/releases/tinycc/tcc-{Version}-win64-bin.zip", Sha256Win64)
            : ($"https://download.savannah.gnu.org/releases/tinycc/tcc-{Version}-win32-bin.zip", Sha256Win32);

        /// <summary>Can this machine download it (Windows, and the hash of its zip is pinned)?</summary>
        public static bool CanInstall => OperatingSystem.IsWindows() && Download.Sha256.Length == 64;

        /// <summary>Is it there (unpacked by <see cref="Install"/>)?</summary>
        public static bool Installed => File.Exists(Compiler);

        /// <summary>Downloads the zip, checks its SHA-256, unpacks it to <see cref="Directory"/>. Returns the compiler. A failure is an <see cref="InvalidOperationException"/>
        /// with the reason; <paramref name="token"/> stops it (<see cref="OperationCanceledException"/>, nothing is kept).</summary>
        public static string Install(Action<string>? log = null, Action<long, long>? progress = null, CancellationToken token = default)
        {
            if (!CanInstall)
                throw new InvalidOperationException(OperatingSystem.IsWindows()
                    ? "The download of TinyCC is not pinned to a SHA-256 in this version of fire; install tcc yourself and put it on the PATH."
                    : "The portable TinyCC is only for Windows. Install tcc with the package manager (e.g. sudo apt install tcc).");
            var (url, sha) = Download;
            System.IO.Directory.CreateDirectory(ToolchainSetup.Root);
            string zip = Path.Combine(Path.GetTempPath(), "fire-tcc-" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
                {
                    log?.Invoke($"Downloading {url} ...");
                    DownloadTransfer.Fetch(http, url, zip, progress, log, token);
                }
                token.ThrowIfCancellationRequested();
                log?.Invoke("Checking the SHA-256 ...");
                Verify(zip, sha);
                return Unpack(zip, log);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidDataException)
            {
                if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                throw new InvalidOperationException("Installing TinyCC failed: " + ex.Message, ex);
            }
            finally
            {
                try { File.Delete(zip); } catch (IOException) { }
            }
        }

        /// <summary>The SHA-256 of <paramref name="file"/> against <paramref name="expected"/>: an <see cref="InvalidDataException"/> when it differs.</summary>
        public static void Verify(string file, string expected)
        {
            string sum;
            using (var input = File.OpenRead(file)) sum = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
            if (!string.Equals(sum, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The download is not the expected file (its SHA-256 is {sum}, expected {expected}).");
        }

        /// <summary>Unpacks the zip (it holds a folder `tcc` with tcc.exe, include\ and lib\) so that tcc.exe is in <paramref name="directory"/> (default:
        /// <see cref="Directory"/>). Returns the compiler.</summary>
        public static string Unpack(string zip, Action<string>? log = null, string? directory = null)
        {
            string target = directory ?? Directory;
            const string exe = "tcc.exe";   // (the zips are for Windows)
            string temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(target))!, ".tcc-unpack-" + Guid.NewGuid().ToString("N"));
            try
            {
                log?.Invoke($"Unpacking to {target} ...");
                ZipFile.ExtractToDirectory(zip, temp, overwriteFiles: true);
                string root = FindRoot(temp, exe) ?? throw new InvalidOperationException("The archive holds no tcc.exe.");
                if (System.IO.Directory.Exists(target)) System.IO.Directory.Delete(target, recursive: true);
                System.IO.Directory.Move(root, target);
                string compiler = Path.Combine(target, exe);
                log?.Invoke("TinyCC is ready: " + compiler);
                return compiler;
            }
            finally
            {
                try { if (System.IO.Directory.Exists(temp)) System.IO.Directory.Delete(temp, true); } catch (IOException) { }
            }
        }

        private static string? FindRoot(string dir, string exe)
        {
            if (File.Exists(Path.Combine(dir, exe))) return dir;
            foreach (string sub in System.IO.Directory.GetDirectories(dir))
                foreach (string candidate in new[] { sub }.Concat(System.IO.Directory.GetDirectories(sub)))
                    if (File.Exists(Path.Combine(candidate, exe))) return candidate;
            return null;
        }
    }
}
