using System.Diagnostics;

namespace fire.Projects
{
    /// <summary>
    /// The standard libraries of fire as packages (docs/packages/README.md): one package per import (<c>fire-io</c>, <c>fire-time</c>, ...). Their sources are projects - the folder <c>Source</c> next to
    /// the programs holds <c>Packages.firesln</c> with one library project per import; when spark or forge start they pack and install the projects that are missing or changed
    /// (`forge install-standard`): <see cref="EnsureInstalled"/> asks forge next to the program (or <c>FIRE_FORGE</c>) to do that.
    /// </summary>
    public static class StandardPackages
    {
        public const string Prefix = "fire-";

        /// <summary>The standard libraries: the names of their imports.</summary>
        public static readonly IReadOnlyList<string> Bridges = new[] { "graphics", "windows", "devices", "io", "filesystem", "dialogs", "ui", "linq", "reflection", "time", "random", "testing", "net", "process", "hash", "compression", "ai", "ai.agent", "ai.models", "assistant", "mcp", "tls", "http", "http.rpc", "http.pages", "data", "data.sqlite", "data.memory", "data.mysql", "data.sqlserver", "data.postgres", "http.data", "gpio", "i2c", "spi", "wifi", "audio", "usb", "tracker", "scene3d", "scene3d.terrain", "scene3d.effects", "scene3d.physics", "ui.docking", "json", "net.channel", "circuits", "circuits.ui", "console", "machine", "dos", "xms", "x86", "regex", "forge", "script" };

        public static string PackageNameOf(string bridge) => Prefix + bridge;

        /// <summary>For a program without the compiler (ember): runs `forge install-standard` next to it, which packs and installs the standard libraries from <c>Source</c> when they
        /// are missing or changed. Never throws; what forge says goes to <paramref name="log"/>.</summary>
        public static void EnsureInstalled(Action<string>? log = null)
        {
            try
            {
                string dir = AppContext.BaseDirectory;
                string? forge = Environment.GetEnvironmentVariable("FIRE_FORGE") is { Length: > 0 } own && File.Exists(own) ? own
                    : new[] { "forge.exe", "forge" }.Select(n => Path.Combine(dir, n)).FirstOrDefault(File.Exists);
                if (forge == null || !File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(forge))!, "Source", "Packages.firesln"))) return;
                var psi = new ProcessStartInfo(forge);
                psi.ArgumentList.Add("install-standard");
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using var process = Process.Start(psi);
                if (process == null) return;
                process.OutputDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) log?.Invoke(e.Data); };
                process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) log?.Invoke(e.Data); };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
            }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                log?.Invoke("The standard packages could not be checked: " + ex.Message);
            }
        }
    }
}
