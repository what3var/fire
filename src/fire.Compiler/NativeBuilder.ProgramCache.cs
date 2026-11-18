using System.Security.Cryptography;
using System.Text;
using fire.Native;
using fire.Runtime;

namespace fire.Compiler
{
    public static partial class NativeBuilder
    {
        /// <summary>
        /// The programs of linkage "source" that were compiled last (docs/spec/NATIVE_BACKEND.md "Getrennte Übersetzung"): a build whose files and compiler commands are
        /// exactly those of one of them takes that program instead of compiling the C again - a rebuild without a change, the same program built again (a Clean, a
        /// start of the assistant). The key is the content hash of every file the compiler reads in the build folder (the program's C, the natives, the runtime and the
        /// platform package), the commands (with the folders of the build in neutral form) and the compilers (path, size, time). Only gcc, clang and TinyCC (their
        /// output is the program alone), not with `--stats`; a debug build also by its folder (its debug information names the files). The newest
        /// <see cref="KeptPrograms"/> stay, in `programs/` of the cache of the static libraries (FIRE_NATIVE_CACHE, `ember cache clean`).
        /// FIRE_PROGRAM_CACHE=0 switches it off.
        /// </summary>
        internal static class ProgramCache
        {
            public const int KeptPrograms = 8;

            private static bool Enabled => Environment.GetEnvironmentVariable("FIRE_PROGRAM_CACHE") is not "0";

            /// <summary>The key of the build (null: it is not cached).</summary>
            public static string? Key(ToolchainDef toolchain, TargetProfile target, string workDir, string exe, IReadOnlyList<(string Executable, string Arguments)> commands, bool stats)
            {
                if (!Enabled || stats || toolchain.EffectiveKind is not ("gcc" or "clang" or "tcc")) return null;
                try
                {
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    void Add(string text) { var bytes = Encoding.UTF8.GetBytes(text); hash.AppendData(BitConverter.GetBytes(bytes.Length)); hash.AppendData(bytes); }
                    string root = Path.GetFullPath(workDir);
                    Add("fire program cache 1");
                    Add(target.Name);
                    Add(ToolchainStamp(toolchain));
                    foreach (var (executable, arguments) in commands)
                    {
                        Add(Stamp(executable));
                        Add(arguments.Replace(exe, "<program>", StringComparison.Ordinal).Replace(root, "<build>", StringComparison.Ordinal));
                    }
                    var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                        .Select(f => (Path: f, Name: Path.GetRelativePath(root, f).Replace('\\', '/'))).OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
                    bool debug = false;
                    foreach (var (path, name) in files)
                    {
                        Add(name);
                        byte[] content = File.ReadAllBytes(path);
                        hash.AppendData(BitConverter.GetBytes(content.LongLength));
                        hash.AppendData(content);
                        if (name == SourceName && IsDebugProgram(content)) debug = true;
                    }
                    // (a debug build: its debug information names the files where they were compiled)
                    if (debug) Add(root);
                    return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
            }

            /// <summary>The program of this key, copied to <paramref name="exe"/>; false when the cache does not have it.</summary>
            public static bool TryRestore(TargetProfile target, string key, string exe)
            {
                string dir = Path.Combine(Folder(target), key);
                string cached = Path.Combine(dir, "program");
                if (!File.Exists(cached)) return false;
                try
                {
                    string temp = exe + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.Copy(cached, temp, true);
                    File.Move(temp, exe, true);
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(exe, File.GetUnixFileMode(cached));
                    Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow);   // (the newest stay)
                    return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
            }

            /// <summary>Puts the program that was just built into the cache, and leaves only the newest programs there.</summary>
            public static void Store(TargetProfile target, string key, string exe)
            {
                try
                {
                    string folder = Folder(target);
                    string dir = Path.Combine(folder, key);
                    Directory.CreateDirectory(dir);
                    string temp = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".tmp");
                    File.Copy(exe, temp, true);
                    File.Move(temp, Path.Combine(dir, "program"), true);
                    foreach (var stale in new DirectoryInfo(folder).GetDirectories().OrderByDescending(d => d.LastWriteTimeUtc).Skip(KeptPrograms))
                        try { stale.Delete(true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }

            private static string Folder(TargetProfile target) => Path.Combine(fire.Package.Manager.NativeLibraryCache.Root, target.Name, "programs");

            /// <summary>A compiler as the key sees it: its path, size and time.</summary>
            private static string Stamp(string executable)
            {
                string path = ToolchainSetup.FindProgram(executable) ?? executable;
                try { var info = new FileInfo(path); return info.Exists ? $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}" : path; }
                catch (Exception) { return path; }
            }

            private static bool IsDebugProgram(byte[] c)
            {
                // (the head of the program: `#define FIRE_DEBUG 1` stands in its first lines, see CompilerCommand)
                string head = Encoding.UTF8.GetString(c, 0, Math.Min(c.Length, 8192));
                return head.Contains("\n#define FIRE_DEBUG 1", StringComparison.Ordinal) || head.StartsWith("#define FIRE_DEBUG 1", StringComparison.Ordinal);
            }
        }
    }
}
