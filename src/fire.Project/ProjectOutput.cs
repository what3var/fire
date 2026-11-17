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

        /// <summary>The folders below a project folder that builds write into and Clean removes (docs/tools/projects.md, "Clean"): <c>bin/</c> - the programs of Build, Build virtual
        /// and the native runs (<c>bin/&lt;target&gt;-&lt;mode&gt;/</c>), the C sources a native build keeps next to a program (<c>*.src/</c>), the packages of a library
        /// (<c>.fpk</c>) - and <c>obj/</c>, where the C++ natives of the project are built for the virtual machine. The file lists of a project never take files from them.</summary>
        public static readonly IReadOnlyList<string> BuildFolders = new[] { "bin", "obj" };

        /// <summary>
        /// Clean: removes the build folders of a project (<see cref="BuildFolders"/>), nothing else - an `output` setting that points somewhere else is left alone, and so is a build folder
        /// that holds a file the project compiles, a test of it, any fire source or a project or solution file (builds write none of them: it is no build folder then; it is reported in
        /// <see cref="CleanResult.Kept"/>). A build folder that is a link: the link is removed, not what it points to.
        /// </summary>
        public static CleanResult Clean(LoadedProject project)
        {
            var removed = new List<string>();
            var kept = new List<string>();
            foreach (string name in BuildFolders)
            {
                string folder = Path.Combine(project.Directory, name);
                var info = new DirectoryInfo(folder);
                if (!info.Exists) continue;
                if (info.LinkTarget != null)
                {
                    try { info.Delete(); removed.Add(folder); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { kept.Add($"{folder}: {ex.Message}"); }
                    continue;
                }
                string prefix = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                bool Inside(string file) => Path.GetFullPath(file).StartsWith(prefix, ProjectFiles.PathComparison);
                string? source = project.Files.Concat(project.TestFiles).FirstOrDefault(Inside);
                // (builds write no fire sources and no project files there: one that is there was put there by someone)
                source ??= SafeFiles(folder, "*").FirstOrDefault(f => FireProject.IsSourceFile(f) || f.EndsWith(FireProject.Extension, StringComparison.OrdinalIgnoreCase) || f.EndsWith(FireSolution.Extension, StringComparison.OrdinalIgnoreCase));
                if (source != null)
                {
                    kept.Add($"{folder}: it holds {ProjectFiles.Relative(project.Directory, source)}, which is no build output - delete it yourself if it is one.");
                    continue;
                }
                try { Directory.Delete(folder, recursive: true); removed.Add(folder); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { kept.Add($"{folder}: {ex.Message}"); }
            }
            return new CleanResult(removed, kept);
        }

        private static IEnumerable<string> SafeFiles(string folder, string pattern)
        {
            try { return Directory.EnumerateFiles(folder, pattern, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
        }
    }

    /// <summary>What <see cref="ProjectOutput.Clean"/> did: the folders it removed, and those it left (with the reason).</summary>
    public sealed record CleanResult(IReadOnlyList<string> Removed, IReadOnlyList<string> Kept);
}
