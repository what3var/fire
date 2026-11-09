namespace fire.Projects
{
    /// <summary>Where a program of a project is built when nothing names its place (no `output` setting, no `-o`): <c>bin/&lt;target&gt;-&lt;mode&gt;/&lt;name&gt;</c> below the
    /// project folder (docs/tools/projects.md). The same for spark and forge.</summary>
    public static class ProjectOutput
    {
        /// <summary>The target of a program that carries the virtual machine (a native build names the target it was built for: linux, windows, esp32, ...).</summary>
        public const string VmTarget = "vm";

        /// <summary>The short name of a mode (<c>debug</c>, <c>release</c>, <c>performance</c>, any case) in the name of the folder: dbg, rel, perf.</summary>
        public static string ModeTag(string mode) => mode.ToLowerInvariant() switch
        {
            "debug" => "dbg",
            "performance" => "perf",
            _ => "rel",
        };

        /// <summary><c>&lt;project&gt;/bin/&lt;target&gt;-&lt;mode&gt;</c>.</summary>
        public static string Folder(string projectDirectory, string target, string mode) =>
            Path.Combine(projectDirectory, "bin", $"{target}-{ModeTag(mode)}");

        /// <summary>The file of the program in that folder: the name of the project (characters a file name cannot have become `_`) with the extension of the target
        /// (<c>.exe</c> for Windows, <c>.html</c> for the browser).</summary>
        public static string ProgramPath(string projectDirectory, string projectName, string target, string mode, bool windows) =>
            ProgramPath(projectDirectory, projectName, target, mode, target == "wasm" ? ".html" : windows ? ".exe" : "");

        /// <summary>The same with the extension of the target (TargetProfile.ProgramExtension: `.exe` also for Windows 3.x and DOS).</summary>
        public static string ProgramPath(string projectDirectory, string projectName, string target, string mode, string extension)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) projectName = projectName.Replace(c, '_');
            return Path.Combine(Folder(projectDirectory, target, mode), projectName + extension);
        }
    }
}
