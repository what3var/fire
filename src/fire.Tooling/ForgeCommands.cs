using System.Text.RegularExpressions;

namespace fire.Tooling
{
    /// <summary>The standard packages (packages/ of the repository; Source/ next to forge): packed and installed by forge (`forge install-standard`, M3).</summary>
    public static class StandardSources
    {
        /// <summary>The solution of the standard packages in <see cref="DefaultFolder"/>.</summary>
        public const string SolutionFile = "Packages.firesln";

        /// <summary>The folder of the standard packages as projects: Source/ next to forge (a distribution), else packages/ of the repository a forge built from its
        /// sources reads.</summary>
        public static string DefaultFolder
        {
            get
            {
                if (Forge.Locate(out _) is { } forge && Path.Combine(Path.GetDirectoryName(forge)!, "Source") is var source && File.Exists(Path.Combine(source, SolutionFile))) return source;
                if (Forge.Environment().TryGetValue("FIRE_NATIVE_ROOT", out var native) && Path.Combine(Path.GetDirectoryName(native)!, "packages") is var repo && Directory.Exists(repo)) return repo;
                return Path.Combine(AppContext.BaseDirectory, "Source");
            }
        }

        /// <summary>`forge install-standard`: the standard packages that are missing or changed are packed and installed. Returns what forge installed (its lines
        /// "Installing the standard package ..."); what it says goes to <paramref name="log"/>. Never throws: a forge that cannot run is a line of the log.</summary>
        public static IReadOnlyList<string> EnsureInstalled(Action<string>? log = null)
        {
            var installed = new List<string>();
            try
            {
                var (code, output, errors) = Forge.Run(new[] { "install-standard" }, line: line =>
                {
                    if (line.StartsWith("Installing", StringComparison.Ordinal)) lock (installed) installed.Add(line);
                    log?.Invoke(line);
                });
                foreach (string line in errors.Split('\n')) if (line.Trim().Length > 0) log?.Invoke(line);
                if (code != 0) log?.Invoke($"forge install-standard ended with exit code {code}.");
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
            {
                log?.Invoke("The standard packages could not be checked: " + ex.Message);
            }
            lock (installed) return installed.ToList();
        }
    }

    /// <summary>The `#import` lines of a text (which packages it uses): for what the editor sends the server as the context of a document.</summary>
    public static class ImportedPreludes
    {
        private static readonly Regex ImportLine = new(@"^[ \t]*#import[ \t]+""([^""\r\n]+)""", RegexOptions.Multiline | RegexOptions.Compiled);

        /// <summary>The names of the `#import` lines of <paramref name="source"/>, in their order.</summary>
        public static IEnumerable<string> FindImportNames(string source) => ImportLine.Matches(source).Select(m => m.Groups[1].Value.Trim());
    }

    /// <summary>What a test of a toolchain said (<see cref="NativeBuilder.TestToolchain"/>).</summary>
    public sealed record NativeBuildResult(bool Ok, string Log);

    /// <summary>Native builds are forge's (`forge build --engine native`); what the dialogs of spark ask of it.</summary>
    public static class NativeBuilder
    {
        /// <summary>Compiles and runs a small program with <paramref name="toolchain"/> as it is set up (not saved): `forge toolchaintest` in a folder of its own with a
        /// fire.native.json that names it.</summary>
        public static NativeBuildResult TestToolchain(ToolchainDef toolchain)
        {
            string folder = Path.Combine(Path.GetTempPath(), $"fire-toolchaintest-{Environment.ProcessId}-{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(folder);
                var config = new NativeConfig();
                config.Toolchains["spark-test"] = toolchain;
                File.WriteAllText(Path.Combine(folder, "fire.native.json"), config.ToJson());
                var (code, output, errors) = Forge.Run(new[] { "toolchaintest", "spark-test" }, folder, timeoutMs: 300000);
                var lines = output.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
                bool ok = code == 0 && lines.Length > 0 && lines[0] == "ok";
                string log = string.Join("\n", lines.Skip(1)) + (errors.Trim().Length > 0 ? "\n" + errors.Trim() : "");
                return new NativeBuildResult(ok, log.Trim());
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception) { return new NativeBuildResult(false, ex.Message); }
            finally { try { Directory.Delete(folder, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
        }
    }
}
