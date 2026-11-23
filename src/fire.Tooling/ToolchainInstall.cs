using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace fire.Tooling
{
    /// <summary>
    /// The portable toolchains next to spark (`Toolchain/` - nothing is installed system-wide) are downloaded and unpacked by forge (M3; native/forge/tools/setup.c, Phase D 5):
    /// `forge toolchain-setup w64devkit|watcom|tcc`. Its lines (`Downloading... n% (m MB)`) are the progress.
    /// </summary>
    public static class ToolchainInstall
    {
        private static readonly Regex Percent = new(@"Downloading\.\.\. (\d+)%", RegexOptions.Compiled);

        /// <summary>Runs `forge toolchain-setup <paramref name="which"/>` to its end; the result is <paramref name="result"/>() when forge ended well, else an
        /// <see cref="InvalidOperationException"/> with what forge said. <paramref name="token"/> ends forge (an <see cref="OperationCanceledException"/>).</summary>
        public static string Run(string which, Action<string>? log, Action<long, long>? progress, long downloadBytes, CancellationToken token, Func<string> result)
        {
            var (code, output, errors) = Forge.Run(new[] { "toolchain-setup", which }, timeoutMs: 3600000, cancel: token, line: line =>
            {
                var m = Percent.Match(line);
                if (m.Success && progress != null) progress(downloadBytes * int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / 100, downloadBytes);
                else log?.Invoke(line);
            });
            token.ThrowIfCancellationRequested();
            if (code != 0) throw new InvalidOperationException((errors.Trim().Length > 0 ? errors.Trim() : output.Trim().Split('\n').LastOrDefault() ?? "") is { Length: > 0 } why ? why : $"forge toolchain-setup {which} ended with exit code {code}.");
            return result();
        }
    }

    /// <summary>The portable TinyCC for Windows (`Toolchain\tcc\`, what `forge toolchain-setup tcc` unpacks, checked against its pinned SHA-256).</summary>
    public static class TinyccSetup
    {
        public const string Version = "0.9.27";
        public const string DownloadSizeHint = "about 1 MB";
        public const long DownloadBytes = 1_000_000;

        /// <summary>The pinned SHA-256 of the official zips (lower case hex) - forge checks them (native/forge/tools/setup.c holds the same).</summary>
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

        /// <summary>Is it there?</summary>
        public static bool Installed => File.Exists(Compiler);

        /// <summary>forge downloads the zip, checks its SHA-256 and unpacks it to <see cref="Directory"/>. Returns the compiler.</summary>
        public static string Install(Action<string>? log = null, Action<long, long>? progress = null, CancellationToken token = default)
        {
            if (!CanInstall)
                throw new InvalidOperationException(OperatingSystem.IsWindows()
                    ? "The download of TinyCC is not pinned to a SHA-256 in this version of fire; install tcc yourself and put it on the PATH."
                    : "The portable TinyCC is only for Windows. Install tcc with the package manager (e.g. sudo apt install tcc).");
            return ToolchainInstall.Run("tcc", log, progress, DownloadBytes, token, () => Compiler);
        }
    }

    /// <summary>Where Open Watcom (the toolchain of the old PC targets) is: what the dialogs show and look for. Building with it is forge's.</summary>
    public static class WatcomToolchain
    {
        public static ToolchainDef Default { get; } = new() { Kind = "watcom", Compiler = "owcc", CCompiler = "owcc", CStd = "c99" };

        public static string? Locate(ToolchainDef toolchain)
        {
            string name = toolchain.Compiler ?? "owcc";
            if (ToolchainSetup.FindProgram(name) is { } onPath) return onPath;
            foreach (string root in Roots())
                if (CompilerIn(root, name) is { } found) return found;
            return null;
        }

        /// <summary>The folders of an Open Watcom tree that hold the compilers for this machine, in the order they are tried: the 64-bit ones first
        /// (Windows: binnt64, binnt; Linux: binl64, binl). The portable archive brings only the 32-bit ones (binnt, binl).</summary>
        public static IReadOnlyList<string> CompilerFolders { get; } = OperatingSystem.IsWindows() ? new[] { "binnt64", "binnt" } : new[] { "binl64", "binl" };

        /// <summary>The compiler <paramref name="name"/> (default owcc) in the Open Watcom tree <paramref name="root"/>, or null.</summary>
        public static string? CompilerIn(string root, string name = "owcc")
        {
            foreach (string bin in CompilerFolders)
            {
                string candidate = Path.Combine(root, bin, OperatingSystem.IsWindows() ? name + ".exe" : name);
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        /// <summary>Where <see cref="Locate"/> looks for an Open Watcom tree (for a message that says where nothing was found).</summary>
        public static IEnumerable<string> SearchedFolders() => Roots().SelectMany(r => CompilerFolders.Select(b => Path.Combine(r, b)));

        /// <summary>Where an Open Watcom tree may be: %WATCOM%, Toolchain/watcom next to the program, /opt/watcom, C:\WATCOM.</summary>
        private static IEnumerable<string> Roots()
        {
            if (Environment.GetEnvironmentVariable("WATCOM") is { Length: > 0 } env) yield return env;
            yield return Path.Combine(AppContext.BaseDirectory, "Toolchain", "watcom");
            yield return OperatingSystem.IsWindows() ? @"C:\WATCOM" : "/opt/watcom";
        }
    }
}
