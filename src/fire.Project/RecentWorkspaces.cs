using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace fire.Projects
{
    /// <summary>A project or solution that was opened (or made) before.</summary>
    public sealed class RecentEntry
    {
        public string Path { get; set; } = "";
        public DateTime LastOpened { get; set; }

        [JsonIgnore] public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
        [JsonIgnore] public bool IsSolution => string.Equals(System.IO.Path.GetExtension(Path), FireSolution.Extension, StringComparison.OrdinalIgnoreCase);
        [JsonIgnore] public bool Exists => File.Exists(Path);
        /// <summary>The folder the file is in.</summary>
        [JsonIgnore] public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
    }

    /// <summary>
    /// The projects and solutions that were opened last, newest first (`recent.json` in the application data folder of fire): what the welcome window and File > Open Recent show.
    /// A file that does not exist any more stays in the list (a drive may only be missing for now) until the user removes it. At most <see cref="Max"/> are kept.
    /// </summary>
    public sealed class RecentWorkspaces
    {
        public const int Max = 20;

        public static string DefaultPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "fire", "recent.json");

        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        private readonly string _file;
        private readonly List<RecentEntry> _entries = new();

        /// <summary>Reads the list from `path` (default: <see cref="DefaultPath"/>); a missing or broken file is an empty list.</summary>
        public RecentWorkspaces(string? path = null)
        {
            _file = path ?? DefaultPath;
            try
            {
                if (File.Exists(_file))
                {
                    var read = JsonSerializer.Deserialize<List<RecentEntry>>(File.ReadAllText(_file), Options);
                    if (read != null) _entries.AddRange(read.Where(e => !string.IsNullOrWhiteSpace(e.Path)).OrderByDescending(e => e.LastOpened).Take(Max));
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { _entries.Clear(); }
        }

        public IReadOnlyList<RecentEntry> Entries => _entries;

        /// <summary>Puts a project or solution file at the top (a file that was in the list moves up) and saves.</summary>
        public void Add(string path, DateTime? when = null)
        {
            string full = System.IO.Path.GetFullPath(path);
            _entries.RemoveAll(e => ProjectFiles.PathComparer.Equals(e.Path, full));
            _entries.Insert(0, new RecentEntry { Path = full, LastOpened = when ?? DateTime.Now });
            if (_entries.Count > Max) _entries.RemoveRange(Max, _entries.Count - Max);
            Save();
        }

        public bool Remove(string path)
        {
            string full = System.IO.Path.GetFullPath(path);
            bool removed = _entries.RemoveAll(e => ProjectFiles.PathComparer.Equals(e.Path, full)) > 0;
            if (removed) Save();
            return removed;
        }

        public void Clear()
        {
            _entries.Clear();
            Save();
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(_file))!);
                File.WriteAllText(_file, JsonSerializer.Serialize(_entries, Options));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a list that cannot be saved is only forgotten */ }
        }
    }

    /// <summary>Small settings of the editor that are kept between runs (`spark-settings.json` in the application data folder of fire).</summary>
    public sealed class EditorSettings
    {
        /// <summary>Show the welcome window when the editor starts.</summary>
        public bool ShowWelcome { get; set; } = true;

        public static string DefaultPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "fire", "spark-settings.json");

        private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

        public static EditorSettings Load(string? path = null)
        {
            try
            {
                string file = path ?? DefaultPath;
                if (File.Exists(file)) return JsonSerializer.Deserialize<EditorSettings>(File.ReadAllText(file), Options) ?? new EditorSettings();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
            return new EditorSettings();
        }

        public void Save(string? path = null)
        {
            try
            {
                string file = System.IO.Path.GetFullPath(path ?? DefaultPath);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
                File.WriteAllText(file, JsonSerializer.Serialize(this, Options));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
