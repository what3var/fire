using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace fire.Projects
{
    /// <summary>
    /// A solution (`name.firesln`, JSON): several projects that belong together, which one runs (`startup`) and settings that apply to all of them unless a project says otherwise.
    /// Solutions are optional: a single `.fireproj` can be opened on its own, and a single file without any project as well.
    /// </summary>
    public sealed class FireSolution
    {
        public const string Extension = ".firesln";

        public int Format { get; set; } = FireProject.CurrentFormat;
        public string Name { get; set; } = "";
        /// <summary>The project files (relative to the solution file), in the order they are shown.</summary>
        public List<string> Projects { get; set; } = new();
        /// <summary>Folders of the solution (relative to the solution file) that hold projects; projects made in a folder live in a subfolder of it. Folders that hold a project are shown anyway - this
        /// list keeps the empty ones.</summary>
        public List<string> Folders { get; set; } = new();
        /// <summary>The project that runs when the solution is run: its name; not set: the first program in the list.</summary>
        public string? Startup { get; set; }
        /// <summary>Settings for every project of the solution (a project's own settings go before them).</summary>
        public ProjectSettings Settings { get; set; } = new();

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
        };

        public static FireSolution Parse(string json)
        {
            try { return JsonSerializer.Deserialize<FireSolution>(json, Options) ?? throw new ProjectException("The solution file is empty."); }
            catch (JsonException ex) { throw new ProjectException("The solution file is not valid: " + ex.Message); }
        }

        public static FireSolution Load(string path)
        {
            string full = Path.GetFullPath(path);
            string text;
            try { text = File.ReadAllText(full); }
            catch (IOException ex) { throw new ProjectException($"Cannot read '{full}': {ex.Message}"); }
            catch (UnauthorizedAccessException ex) { throw new ProjectException($"Cannot read '{full}': {ex.Message}"); }
            var solution = Parse(text);
            solution.FilePath = full;
            if (string.IsNullOrWhiteSpace(solution.Name)) solution.Name = Path.GetFileNameWithoutExtension(full);
            return solution;
        }

        public string ToJson()
        {
            var shape = new Dictionary<string, object?>();
            shape["format"] = Format;
            shape["name"] = Name;
            shape["projects"] = Projects;
            if (Folders.Count > 0) shape["folders"] = Folders;
            if (Startup != null) shape["startup"] = Startup;
            if (Settings != null && !Settings.IsEmpty) shape["settings"] = Settings;
            return JsonSerializer.Serialize(shape, Options);
        }

        public void Save(string? path = null)
        {
            path ??= FilePath ?? throw new ProjectException("The solution has no file name.");
            string full = Path.GetFullPath(path);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, ToJson() + Environment.NewLine);
            FilePath = full;
        }
    }
}
