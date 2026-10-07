using System.Text.Json.Serialization;

namespace fire.Projects
{
    /// <summary>
    /// The build settings that a project (or a solution, for all of its projects) can fix. Every field is optional: a field that is not set leaves the decision to the next level.
    /// The order of precedence is **project, then solution, then the tags in the source (`#debug`, `#name "..."`, `#floatwidth 32`, ...), then the built-in defaults**:
    /// what a project says is not overridden by a tag in a file of it. These are the same things the build settings dialog of the editor writes as tags into a single script.
    /// </summary>
    public sealed class ProjectSettings
    {
        /// <summary>`console` or `gui` (a program without a console window; the tag `#noconsole`).</summary>
        public string? Subsystem { get; set; }
        /// <summary>`debug`, `release` or `performance` (the tags `#debug` and `#performance`).</summary>
        public string? Mode { get; set; }
        /// <summary>32 or 64: the precision of `float` (the tag `#floatwidth`).</summary>
        public int? FloatWidth { get; set; }
        public string? Name { get; set; }
        public string? Codename { get; set; }
        public string? Description { get; set; }
        public string? Author { get; set; }
        public string? Comments { get; set; }
        /// <summary>The icon of the program (a path relative to the file that holds the setting).</summary>
        public string? Icon { get; set; }
        public string? Version { get; set; }
        public string? FileVersion { get; set; }
        /// <summary>Symbols for `#if` (like `-D name` on the command line); those of the levels are added up.</summary>
        public List<string>? Defines { get; set; }
        /// <summary>`vm` (a program that carries the virtual machine) or `native` (C++ built with a toolchain).</summary>
        public string? Engine { get; set; }
        /// <summary>The target of a native build (a built-in one or one of `fire.native.json`).</summary>
        public string? Target { get; set; }
        /// <summary>The toolchain of a native build.</summary>
        public string? Toolchain { get; set; }
        /// <summary>Where the result of a build is written (a path relative to the file that holds the setting; for a library the folder of the package).</summary>
        public string? Output { get; set; }

        [JsonIgnore]
        public bool IsEmpty => Subsystem == null && Mode == null && FloatWidth == null && Name == null && Codename == null && Description == null && Author == null && Comments == null && Icon == null
            && Version == null && FileVersion == null && (Defines == null || Defines.Count == 0) && Engine == null && Target == null && Toolchain == null && Output == null;

        public static readonly IReadOnlyList<string> Subsystems = new[] { "console", "gui" };
        public static readonly IReadOnlyList<string> Modes = new[] { "debug", "release", "performance" };
        public static readonly IReadOnlyList<string> Engines = new[] { "vm", "native" };

        /// <summary>`high` over `low`: every field that `high` sets, else the one of `low` (the defines of both are added up). Either may be null.</summary>
        public static ProjectSettings Merge(ProjectSettings? high, ProjectSettings? low)
        {
            var r = new ProjectSettings();
            r.Subsystem = high?.Subsystem ?? low?.Subsystem;
            r.Mode = high?.Mode ?? low?.Mode;
            r.FloatWidth = high?.FloatWidth ?? low?.FloatWidth;
            r.Name = high?.Name ?? low?.Name;
            r.Codename = high?.Codename ?? low?.Codename;
            r.Description = high?.Description ?? low?.Description;
            r.Author = high?.Author ?? low?.Author;
            r.Comments = high?.Comments ?? low?.Comments;
            r.Icon = high?.Icon ?? low?.Icon;
            r.Version = high?.Version ?? low?.Version;
            r.FileVersion = high?.FileVersion ?? low?.FileVersion;
            r.Engine = high?.Engine ?? low?.Engine;
            r.Target = high?.Target ?? low?.Target;
            r.Toolchain = high?.Toolchain ?? low?.Toolchain;
            r.Output = high?.Output ?? low?.Output;
            var defines = new List<string>();
            foreach (var d in (high?.Defines ?? new List<string>()).Concat(low?.Defines ?? new List<string>())) if (!defines.Contains(d)) defines.Add(d);
            if (defines.Count > 0) r.Defines = defines;
            return r;
        }

        public ProjectSettings Clone() => Merge(this, null);

        /// <summary>A copy in which the paths (`icon`, `output`) are made absolute relative to `directory` (the folder of the file that holds them).</summary>
        public ProjectSettings WithAbsolutePaths(string directory)
        {
            var r = Clone();
            if (!string.IsNullOrEmpty(r.Icon)) r.Icon = Path.GetFullPath(r.Icon, directory);
            if (!string.IsNullOrEmpty(r.Output)) r.Output = Path.GetFullPath(r.Output, directory);
            return r;
        }

        /// <summary>The problems of the values (empty: fine).</summary>
        public IReadOnlyList<string> Validate()
        {
            var problems = new List<string>();
            if (Subsystem != null && !Subsystems.Contains(Subsystem)) problems.Add($"settings.subsystem '{Subsystem}' is not one of: {string.Join(", ", Subsystems)}.");
            if (Mode != null && !Modes.Contains(Mode)) problems.Add($"settings.mode '{Mode}' is not one of: {string.Join(", ", Modes)}.");
            if (FloatWidth != null && FloatWidth != 32 && FloatWidth != 64) problems.Add("settings.floatWidth must be 32 or 64.");
            if (Engine != null && !Engines.Contains(Engine)) problems.Add($"settings.engine '{Engine}' is not one of: {string.Join(", ", Engines)}.");
            foreach (var d in Defines ?? new List<string>()) if (string.IsNullOrWhiteSpace(d) || d.Any(char.IsWhiteSpace)) problems.Add($"settings.defines: '{d}' is not a symbol name.");
            return problems;
        }
    }
}
