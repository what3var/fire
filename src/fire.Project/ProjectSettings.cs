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
        /// <summary>32 or 64: the width of `int` (the tag `#intwidth`; default 64).</summary>
        public int? IntWidth { get; set; }
        public string? Name { get; set; }
        public string? Codename { get; set; }
        public string? Description { get; set; }
        public string? Author { get; set; }
        /// <summary>The license of a library that is packed (an identifier like `MIT`, or a short text); goes into the package.</summary>
        public string? License { get; set; }
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
        /// <summary>How a native build links the libraries and packages (ROADMAP 9, docs/spec/NATIVE_BACKEND.md "Getrennte Übersetzung"): `source` (the default: all in one C file),
        /// `static` (each library once as a static library in the user-wide cache, linked into the program).</summary>
        public string? Linkage { get; set; }
        /// <summary>How much a native build leaves out (docs/spec/NATIVE_BACKEND.md "Größe des Programms"): `0`-`3` or `none`, `protected`, `methods`, `all` (the default 3);
        /// in the file a number or a name.</summary>
        [JsonConverter(typeof(StrippingTextConverter))]
        public string? Stripping { get; set; }
        /// <summary>Where the result of a build is written (a path relative to the file that holds the setting; for a library the folder of the package).</summary>
        public string? Output { get; set; }
        /// <summary>`lax` (a type conflict is a warning, the default) or `strict` (an error); docs/spec/TYPES.md. A `#lax` / `#strict` in a file decides for that file.</summary>
        public string? TypeChecking { get; set; }
        /// <summary>When the main program runs the `fire global` jobs (and lets in the sections of its threads, the host callbacks) by itself: `safepoints` (the default),
        /// `sleep` or `sync` (docs/spec/THREADING_DESIGN.md section 7; the tag `#globaljobs`). The same in the VM and in a native build.</summary>
        public string? GlobalJobs { get; set; }
        /// <summary>The arguments a run of the program gets (`forge run`, *Run* in spark; `Environment.Args`); arguments after `--` on the command line replace them.</summary>
        public List<string>? Arguments { get; set; }
        /// <summary>The serial port of a board (ESP32: Run and Debug in spark flash it and talk to it there); null: the only USB serial port, else the one esptool picks.</summary>
        public string? Port { get; set; }
        /// <summary>The baud rate of the board's console (null: 115200).</summary>
        public int? Baud { get; set; }

        [JsonIgnore]
        public bool IsEmpty => Subsystem == null && Mode == null && FloatWidth == null && IntWidth == null && Name == null && Codename == null && Description == null && Author == null && License == null && Comments == null && Icon == null
            && Version == null && FileVersion == null && (Defines == null || Defines.Count == 0) && Engine == null && Target == null && Toolchain == null && Linkage == null && Stripping == null && Output == null && TypeChecking == null && GlobalJobs == null && Arguments == null && Port == null && Baud == null;

        public static readonly IReadOnlyList<string> Subsystems = new[] { "console", "gui" };
        public static readonly IReadOnlyList<string> Modes = new[] { "debug", "release", "performance" };
        public static readonly IReadOnlyList<string> Engines = new[] { "vm", "native" };
        public static readonly IReadOnlyList<string> TypeCheckings = new[] { "lax", "strict" };
        public static readonly IReadOnlyList<string> GlobalJobsModes = new[] { "safepoints", "sleep", "sync" };
        public static readonly IReadOnlyList<string> Linkages = new[] { "source", "static", "shared" };
        /// <summary>The stripping levels by their names (index = number; fire.Native.Strippings has the same).</summary>
        public static readonly IReadOnlyList<string> Strippings = new[] { "none", "protected", "methods", "all" };

        /// <summary>`high` over `low`: every field that `high` sets, else the one of `low` (the defines of both are added up). Either may be null.</summary>
        public static ProjectSettings Merge(ProjectSettings? high, ProjectSettings? low)
        {
            var r = new ProjectSettings();
            r.Subsystem = high?.Subsystem ?? low?.Subsystem;
            r.Mode = high?.Mode ?? low?.Mode;
            r.FloatWidth = high?.FloatWidth ?? low?.FloatWidth;
            r.IntWidth = high?.IntWidth ?? low?.IntWidth;
            r.Name = high?.Name ?? low?.Name;
            r.Codename = high?.Codename ?? low?.Codename;
            r.Description = high?.Description ?? low?.Description;
            r.Author = high?.Author ?? low?.Author;
            r.License = high?.License ?? low?.License;
            r.Comments = high?.Comments ?? low?.Comments;
            r.Icon = high?.Icon ?? low?.Icon;
            r.Version = high?.Version ?? low?.Version;
            r.FileVersion = high?.FileVersion ?? low?.FileVersion;
            r.Engine = high?.Engine ?? low?.Engine;
            r.Target = high?.Target ?? low?.Target;
            r.Toolchain = high?.Toolchain ?? low?.Toolchain;
            r.Linkage = high?.Linkage ?? low?.Linkage;
            r.Stripping = high?.Stripping ?? low?.Stripping;
            r.Output = high?.Output ?? low?.Output;
            r.TypeChecking = high?.TypeChecking ?? low?.TypeChecking;
            r.GlobalJobs = high?.GlobalJobs ?? low?.GlobalJobs;
            r.Arguments = high?.Arguments ?? low?.Arguments;
            r.Port = high?.Port ?? low?.Port;
            r.Baud = high?.Baud ?? low?.Baud;
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

        /// <summary>`"stripping"`: a number or a name in the file, kept as text (written as the file had it: a number as a number).</summary>
        public sealed class StrippingTextConverter : JsonConverter<string?>
        {
            public override string? Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options) => reader.TokenType switch
            {
                System.Text.Json.JsonTokenType.Null => null,
                System.Text.Json.JsonTokenType.Number => reader.TryGetInt64(out long n) ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
                System.Text.Json.JsonTokenType.String => reader.GetString(),
                _ => throw new System.Text.Json.JsonException("settings.stripping: a number (0-3) or a name (none, protected, methods, all)."),
            };

            public override void Write(System.Text.Json.Utf8JsonWriter writer, string? value, System.Text.Json.JsonSerializerOptions options)
            {
                if (value == null) writer.WriteNullValue();
                else if (value.Length == 1 && char.IsDigit(value[0])) writer.WriteNumberValue(value[0] - '0');
                else writer.WriteStringValue(value);
            }
        }

        /// <summary>The problems of the values (empty: fine).</summary>
        public IReadOnlyList<string> Validate()
        {
            var problems = new List<string>();
            if (Subsystem != null && !Subsystems.Contains(Subsystem)) problems.Add($"settings.subsystem '{Subsystem}' is not one of: {string.Join(", ", Subsystems)}.");
            if (Mode != null && !Modes.Contains(Mode)) problems.Add($"settings.mode '{Mode}' is not one of: {string.Join(", ", Modes)}.");
            if (FloatWidth != null && FloatWidth != 32 && FloatWidth != 64) problems.Add("settings.floatWidth must be 32 or 64.");
            if (IntWidth != null && IntWidth != 32 && IntWidth != 64) problems.Add("settings.intWidth must be 32 or 64.");
            if (TypeChecking != null && !TypeCheckings.Contains(TypeChecking)) problems.Add("settings.typeChecking must be lax or strict.");
            if (GlobalJobs != null && !GlobalJobsModes.Contains(GlobalJobs.ToLowerInvariant())) problems.Add($"settings.globalJobs '{GlobalJobs}' is not one of: {string.Join(", ", GlobalJobsModes)}.");
            if (Engine != null && !Engines.Contains(Engine)) problems.Add($"settings.engine '{Engine}' is not one of: {string.Join(", ", Engines)}.");
            if (Stripping != null && !(Stripping.Length == 1 && Stripping[0] is >= '0' and <= '3') && !Strippings.Contains(Stripping.ToLowerInvariant()))
                problems.Add($"settings.stripping '{Stripping}' is not one of: 0, 1, 2, 3, {string.Join(", ", Strippings)}.");
            if (Baud != null && Baud <= 0) problems.Add("settings.baud must be a positive number (e.g. 115200).");
            foreach (var d in Defines ?? new List<string>()) if (string.IsNullOrWhiteSpace(d) || d.Any(char.IsWhiteSpace)) problems.Add($"settings.defines: '{d}' is not a symbol name.");
            return problems;
        }
    }
}
