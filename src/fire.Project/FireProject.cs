using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace fire.Projects
{
    public enum OutputType
    {
        /// <summary>A program: its files run one after the other, the entry file last.</summary>
        Exe,
        /// <summary>A library: classes, functions and namespaces without an entry point (no statements at the top level); other projects reference it, and it can be packed as a package (`.fpk`).</summary>
        Library,
    }

    /// <summary>A reference of a project to something it uses: another project of the solution (a library) or a package that is installed (see `ember`).</summary>
    public sealed class ProjectReference
    {
        /// <summary>The project file of a library (relative to the project file).</summary>
        public string? Project { get; set; }
        /// <summary>The name of a package (`fire-http`).</summary>
        public string? Package { get; set; }
        /// <summary>The version a package has to have at least (`1.2`); not set: any.</summary>
        public string? Version { get; set; }

        [JsonIgnore] public bool IsProject => !string.IsNullOrEmpty(Project);
        [JsonIgnore] public string Display => IsProject ? Path.GetFileNameWithoutExtension(Project!) : Package ?? "";
    }

    /// <summary>
    /// The C++ natives of a project (docs/PROJECTS.md): the sources live in the folder `native/` of the project. The project's functions are found in the source - every
    /// `inline Value name(Value a, ...)` becomes the native `__name` (a last parameter `OwnList* list` says that the function allocates its result); `functions` describes the ones that
    /// need more (and replaces the finding for the same C++ name). The rest is as in a package (docs/PACKAGE_NATIVES.md).
    /// </summary>
    public sealed class ProjectNative
    {
        public static readonly string[] DefaultSources = { "native/**/*.{h,hpp,hh,c,cc,cpp,cxx}" };

        /// <summary>Files and patterns relative to the project file, in this order; not set: everything in `native/` - the headers first.</summary>
        public List<string>? Sources { get; set; }
        /// <summary>More files for one platform or target (the key as in a package).</summary>
        public Dictionary<string, List<string>>? PlatformSources { get; set; }
        public List<string>? Platforms { get; set; }
        public Dictionary<string, List<string>>? LinkLibraries { get; set; }
        public List<fire.Package.Manager.PackageNativeFunction>? Functions { get; set; }
        public List<string>? Exceptions { get; set; }
        public string? Reset { get; set; }
    }

    /// <summary>
    /// A project (`name.fireproj`, JSON): the files that make up a program or a library, what it references and the build settings that apply to it (docs/PROJECTS.md).
    ///
    /// The files are `files` - paths and patterns (`*`, `**`, `?`) relative to the project file, in this order; none given: every fire file (`*.script`, `*.fi`, `*.fic`) below the project folder
    /// (`bin`, `obj` and the folders of other projects are left out), by name. `exclude` takes files out again. A program runs its files one after the other, so declarations in the files before
    /// are known when the entry file runs: `entry` (default: the last file) is always put last.
    /// </summary>
    public sealed class FireProject
    {
        public const string Extension = ".fireproj";
        public const int CurrentFormat = 1;
        public static readonly string[] DefaultFiles = { "**/*.{script,fi,fic,fxml}" };
        /// <summary>The files that are compiled: scripts and the markup of user interfaces (`.fxml`, compiled to the script that is generated from it - no `#include` needed).</summary>
        public static readonly string[] SourceExtensions = { ".script", ".fi", ".fic", ".fxml" };

        public static bool IsSourceFile(string path) => SourceExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
        public static bool IsMarkupFile(string path) => string.Equals(Path.GetExtension(path), ".fxml", StringComparison.OrdinalIgnoreCase);

        public int Format { get; set; } = CurrentFormat;
        public string Name { get; set; } = "";
        public OutputType Type { get; set; } = OutputType.Exe;
        /// <summary>The import name of a library (`#import "name"`); default: the project name (characters that are not allowed become `_`).</summary>
        public string? Import { get; set; }
        public List<string> Files { get; set; } = new();
        public List<string> Exclude { get; set; } = new();
        public string? Entry { get; set; }
        public List<ProjectReference> References { get; set; } = new();
        public ProjectSettings Settings { get; set; } = new();
        /// <summary>The C++ natives of the project (folder `native/`); null: none.</summary>
        public ProjectNative? Native { get; set; }

        /// <summary>Where the project was read from (null: not saved yet); not part of the file.</summary>
        [JsonIgnore] public string? FilePath { get; set; }
        [JsonIgnore] public string? Directory => FilePath == null ? null : Path.GetDirectoryName(Path.GetFullPath(FilePath));

        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };

        public static FireProject Parse(string json)
        {
            try { return JsonSerializer.Deserialize<FireProject>(json, Options) ?? throw new ProjectException("The project file is empty."); }
            catch (JsonException ex) { throw new ProjectException("The project file is not valid: " + ex.Message); }
        }

        public static FireProject Load(string path)
        {
            string full = Path.GetFullPath(path);
            string text;
            try { text = File.ReadAllText(full); }
            catch (IOException ex) { throw new ProjectException($"Cannot read '{full}': {ex.Message}"); }
            catch (UnauthorizedAccessException ex) { throw new ProjectException($"Cannot read '{full}': {ex.Message}"); }
            var project = Parse(text);
            project.FilePath = full;
            if (string.IsNullOrWhiteSpace(project.Name)) project.Name = Path.GetFileNameWithoutExtension(full);
            return project;
        }

        public string ToJson()
        {
            // an empty settings object and empty lists are left out of the file
            var copy = new FireProject
            {
                Format = Format, Name = Name, Type = Type, Import = Import, Entry = Entry,
                Files = Files, Exclude = Exclude, References = References, Settings = Settings, Native = Native,
            };
            return JsonSerializer.Serialize(new SaveShape(copy), Options);
        }

        public void Save(string? path = null)
        {
            path ??= FilePath ?? throw new ProjectException("The project has no file name.");
            string full = Path.GetFullPath(path);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, ToJson() + Environment.NewLine);
            FilePath = full;
        }

        // The shape that is written: what is empty is not written.
        private sealed class SaveShape
        {
            public SaveShape(FireProject p)
            {
                Format = p.Format; Name = p.Name; Type = p.Type; Import = p.Import; Entry = p.Entry;
                Files = p.Files.Count > 0 ? p.Files : null;
                Exclude = p.Exclude.Count > 0 ? p.Exclude : null;
                References = p.References.Count > 0 ? p.References : null;
                Settings = p.Settings.IsEmpty ? null : p.Settings;
                Native = p.Native;
            }
            public int Format { get; }
            public string Name { get; }
            public OutputType Type { get; }
            public string? Import { get; }
            public List<string>? Files { get; }
            public List<string>? Exclude { get; }
            public string? Entry { get; }
            public List<ProjectReference>? References { get; }
            public ProjectSettings? Settings { get; }
            public ProjectNative? Native { get; }
        }

        /// <summary>The name a library is imported by: `import`, else the name of the project with every character that a name cannot have replaced by `_`.</summary>
        public string ImportName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Import)) return Import!;
                var chars = Name.Select(c => char.IsLetterOrDigit(c) && c < 128 || c == '_' ? c : '_').ToArray();
                string s = new string(chars);
                if (s.Length == 0 || !char.IsLetter(s[0])) s = "L" + s;
                return s;
            }
        }

        /// <summary>All problems of the description itself (empty: fine); the files are checked when they are resolved (see ProjectFiles).</summary>
        public IReadOnlyList<string> Validate()
        {
            var problems = new List<string>();
            if (Format != CurrentFormat) problems.Add($"Unknown format {Format} (this version reads {CurrentFormat}).");
            if (string.IsNullOrWhiteSpace(Name)) problems.Add("'name' is missing.");
            else if (Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) problems.Add($"The name '{Name}' has characters that a file name cannot have.");
            problems.AddRange((Settings ?? new ProjectSettings()).Validate());
            if (Type == OutputType.Library)
            {
                string import = ImportName;
                if (!fire.Package.Manager.PackageManifest.ImportNamePattern.IsMatch(import)) problems.Add($"The import name '{import}' is not a name (letters, digits and '_', starting with a letter).");
                else if (fire.Package.Manager.PackageManifest.ReservedImportNames.Contains(import)) problems.Add($"The import name '{import}' belongs to the compiler; set 'import' to another name.");
            }
            foreach (var r in References ?? new List<ProjectReference>())
            {
                if (r.IsProject == !string.IsNullOrEmpty(r.Package)) problems.Add("A reference names either a 'project' or a 'package'.");
            }
            return problems;
        }
    }

    public sealed class ProjectException : Exception
    {
        public ProjectException(string message) : base(message) { }
    }
}
