using System.Diagnostics;

namespace fire.Package.Manager
{
    /// <summary>
    /// The standard libraries of fire as packages (docs/PACKAGES.md): one package per import (<c>fire-io</c>, <c>fire-time</c>, ...). Their sources are projects - the folder <c>Source</c> next to
    /// the programs holds <c>Packages.firesln</c> with one library project per import; when spark or forge start they pack and install the projects that are missing or changed
    /// (fire.Compiler's <c>StandardSources</c>). ember has no compiler: <see cref="EnsureInstalled"/> asks forge next to it to do that.
    /// </summary>
    public static class StandardPackages
    {
        public const string Prefix = "fire-";

        /// <summary>The standard libraries: the names of their imports.</summary>
        public static readonly IReadOnlyList<string> Bridges = new[] { "graphics", "windows", "devices", "io", "filesystem", "dialogs", "ui", "linq", "reflection", "time", "random", "testing", "net", "tls", "http", "gpio", "i2c", "spi", "wifi", "audio", "tracker", "scene3d", "ui.docking", "json", "circuits", "circuits.ui" };

        public static string PackageNameOf(string bridge) => Prefix + bridge;

        /// <summary>For a program without the compiler (ember): runs `forge install-standard` next to it, which packs and installs the standard libraries from <c>Source</c> when they
        /// are missing or changed. Never throws; what forge says goes to <paramref name="log"/>.</summary>
        public static void EnsureInstalled(Action<string>? log = null)
        {
            try
            {
                string dir = AppContext.BaseDirectory;
                if (!File.Exists(Path.Combine(dir, "Source", "Packages.firesln"))) return;
                string? forge = new[] { "forge.exe", "forge" }.Select(n => Path.Combine(dir, n)).FirstOrDefault(File.Exists);
                ProcessStartInfo psi;
                if (forge != null) psi = new ProcessStartInfo(forge);
                else if (File.Exists(Path.Combine(dir, "forge.dll"))) { psi = new ProcessStartInfo("dotnet"); psi.ArgumentList.Add(Path.Combine(dir, "forge.dll")); }
                else return;
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
