using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace fire.Projects
{
    /// <summary>What a template makes: files for a project that exists (<see cref="Code"/>: a script, a class, a window) or a whole project (<see cref="Project"/>).</summary>
    public enum TemplateScope { Code, Project }

    /// <summary>The optional `template.json` of a template folder (docs/TEMPLATES.md): every field is optional - a folder without it is a template too.</summary>
    public sealed class TemplateDescription
    {
        public string? Title { get; set; }
        public string? Description { get; set; }
        /// <summary>A picture in the template folder (`icon.png`, `template.png` are found without naming them) or the key of a built-in icon (`script`, `window`, `project`, ...).</summary>
        public string? Icon { get; set; }
        /// <summary>The name that the dialog proposes (a code template; default: the title without spaces).</summary>
        public string? DefaultName { get; set; }
        /// <summary>The extension that the dialog shows behind the name (`.script`); default: the one of the first file that has `$name$` in its name.</summary>
        public string? Extension { get; set; }
        /// <summary>The files (relative to what the template makes) that the editor opens afterwards; default: the first one (a code template) or the main file of the project.</summary>
        public List<string>? Open { get; set; }
        /// <summary>Sorting in the list (smaller first), then by title.</summary>
        public int Order { get; set; } = 100;
        public bool Hidden { get; set; }
        /// <summary>A project template that makes no project (the "Empty" solution).</summary>
        public bool Empty { get; set; }
        /// <summary>`exe` or `library`: for a project template without a project file of its own, which one is made.</summary>
        public string? Type { get; set; }
        /// <summary>Packages that the made project references (besides the package that brought the template).</summary>
        public List<string>? References { get; set; }
        /// <summary>false: a project made from the template of a package does not reference that package (it does by default).</summary>
        public bool? AddPackageReference { get; set; }

        public const string FileName = "template.json";

        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>Writes the description as the `template.json` of a folder.</summary>
        public void Save(string folder)
        {
            System.IO.Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, FileName), JsonSerializer.Serialize(this, Options) + Environment.NewLine);
        }

        /// <summary>Reads the description of a template folder: none, or a file that cannot be read, is an empty description.</summary>
        public static TemplateDescription Load(string folder)
        {
            string path = Path.Combine(folder, FileName);
            if (!File.Exists(path)) return new TemplateDescription();
            try { return JsonSerializer.Deserialize<TemplateDescription>(File.ReadAllText(path), Options) ?? new TemplateDescription(); }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return new TemplateDescription(); }
        }
    }

    /// <summary>A template: a folder with the files that are made (names and contents may have placeholders, see <see cref="TemplateValues"/>) and, optionally, a `template.json`.</summary>
    public sealed class FireTemplate
    {
        public required TemplateScope Scope { get; init; }
        public required string Title { get; init; }
        public string? Description { get; init; }
        /// <summary>The folder of the template.</summary>
        public required string Directory { get; init; }
        /// <summary>A picture file, or null (then <see cref="IconKey"/> names a built-in icon).</summary>
        public string? IconPath { get; init; }
        public string IconKey { get; init; } = "file";
        /// <summary>"Local" (the program folder or the folder of the user) or the package: "From Name 1.2.3".</summary>
        public required string Source { get; init; }
        public string? PackageName { get; init; }
        public string? PackageVersion { get; init; }
        public bool IsFromPackage => PackageName != null;
        /// <summary>The name proposed by the dialog (a code template).</summary>
        public string DefaultName { get; init; } = "New";
        /// <summary>The extension behind the name, with the dot (a code template); "" when the files bring theirs.</summary>
        public string Extension { get; init; } = "";
        public bool Empty { get; init; }
        public int Order { get; init; } = 100;
        public string? Type { get; init; }
        public IReadOnlyList<string> References { get; init; } = Array.Empty<string>();
        public bool AddPackageReference { get; init; }
        public IReadOnlyList<string> Open { get; init; } = Array.Empty<string>();
        /// <summary>The files below <see cref="Directory"/> that are made (relative, with `/`), without the description and the icon.</summary>
        public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();

        /// <summary>One line for lists and messages: the title and where it comes from.</summary>
        public string Display => $"{Title} ({Source})";

        /// <summary>Does the template match a search text (title, description and source; every word has to occur)?</summary>
        public bool Matches(string? search)
        {
            if (string.IsNullOrWhiteSpace(search)) return true;
            string hay = $"{Title} {Description} {Source}";
            return search.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w => hay.Contains(w, StringComparison.OrdinalIgnoreCase));
        }
    }
}
