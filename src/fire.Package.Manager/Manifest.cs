using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace fire.Package.Manager
{
    /// <summary>The `package.json` in the root of a package (.fpk): who made it and which imports it brings. In a package the paths are relative to the root of the
    /// archive; in the file that `ember forge` reads (and keeps next to the package) they are absolute paths (or relative to the file).</summary>
    public sealed class PackageManifest
    {
        /// <summary>The version of this file format.</summary>
        public int Format { get; set; } = 1;
        public string Name { get; set; } = "";
        public string Version { get; set; } = "";
        public string Author { get; set; } = "";
        public string Description { get; set; } = "";
        public string License { get; set; } = "";
        public string Homepage { get; set; } = "";
        /// <summary>Names of other packages that have to be installed too.</summary>
        public List<string> Dependencies { get; set; } = new();
        /// <summary>The imports (`#import "name"`) the package provides.</summary>
        public List<PackageImport> Imports { get; set; } = new();

        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public static PackageManifest Parse(string json)
        {
            try { return JsonSerializer.Deserialize<PackageManifest>(json, Options) ?? throw new PackageException("The package description is empty."); }
            catch (JsonException ex) { throw new PackageException("The package description is not valid JSON: " + ex.Message); }
        }

        public static PackageManifest Load(string path)
        {
            try { return Parse(File.ReadAllText(path)); }
            catch (IOException ex) { throw new PackageException($"Cannot read '{path}': {ex.Message}"); }
        }

        public string ToJson() => JsonSerializer.Serialize(this, Options);

        public void Save(string path)
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, ToJson());
        }

        public static readonly Regex NamePattern = new(@"^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.Compiled);
        public static readonly Regex ImportNamePattern = new(@"^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.Compiled);

        /// <summary>The imports of the compiler itself: a package must not take one of these names.</summary>
        public static readonly IReadOnlySet<string> ReservedImportNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "print", "graphics", "windows", "devices", "io", "ui", "linq", "reflection", "time",
        };

        /// <summary>All problems of the description (empty: it is fine). <paramref name="fileExists"/> decides whether a referenced file exists (relative to the root of the
        /// package, or - for the forge file - to where the paths point); null: the files are not checked.</summary>
        public IReadOnlyList<string> Validate(Func<string, bool>? fileExists = null)
        {
            var problems = new List<string>();
            if (Format != 1) problems.Add($"Unknown format {Format} (this version of ember reads 1).");
            if (!NamePattern.IsMatch(Name ?? "")) problems.Add("'name' is missing or has characters other than letters, digits, '.', '_' and '-'.");
            if (!SemanticVersion.TryParse(Version, out _)) problems.Add("'version' is missing or not a version (1.2.3).");
            if (Imports == null || Imports.Count == 0) problems.Add("The package needs at least one entry in 'imports'.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var import in Imports ?? new List<PackageImport>())
            {
                string where = $"Import '{import.Name}': ";
                if (!ImportNamePattern.IsMatch(import.Name ?? "")) { problems.Add($"Import name '{import.Name}' is not a name (letters, digits and '_', starting with a letter)."); continue; }
                if (ReservedImportNames.Contains(import.Name!)) problems.Add(where + "this name belongs to the compiler.");
                if (!seen.Add(import.Name)) problems.Add(where + "the name is used twice.");
                if (string.IsNullOrWhiteSpace(import.Prelude) && import.Native == null) problems.Add(where + "needs a 'prelude' and/or a 'native' part.");
                if (!string.IsNullOrWhiteSpace(import.Prelude) && fileExists != null && !fileExists(import.Prelude)) problems.Add(where + $"the prelude '{import.Prelude}' does not exist.");
                if (import.Native is { } native)
                {
                    if (native.Sources.Count == 0) problems.Add(where + "'native' needs at least one file in 'sources'.");
                    foreach (var src in native.Sources)
                        if (fileExists != null && !fileExists(src)) problems.Add(where + $"the native source '{src}' does not exist.");
                    var functions = new HashSet<string>();
                    foreach (var fn in native.Functions)
                    {
                        if (string.IsNullOrWhiteSpace(fn.Name) || string.IsNullOrWhiteSpace(fn.Cpp)) problems.Add(where + "a native function needs 'name' and 'cpp'.");
                        else if (!functions.Add(fn.Name)) problems.Add(where + $"the native function '{fn.Name}' is declared twice.");
                        if (fn.Arguments < 0 || fn.Arguments > 16) problems.Add(where + $"the native function '{fn.Name}' takes {fn.Arguments} arguments (0-16).");
                    }
                }
            }
            return problems;
        }
    }

    public sealed class PackageImport
    {
        /// <summary>The name in `#import "name"` (not case sensitive).</summary>
        public string Name { get; set; } = "";
        /// <summary>The prelude: fire source that is added to the program (classes, functions). Optional.</summary>
        public string? Prelude { get; set; }
        /// <summary>Other imports (of the compiler or of packages) that this one switches on, as `ui` switches on `graphics`.</summary>
        public List<string> Requires { get; set; } = new();
        /// <summary>The natives as C++ source (only for the native backend). Optional.</summary>
        public PackageNative? Native { get; set; }
    }

    /// <summary>C++ source for the native backend. The files are put into the generated file after the runtime, outside of any namespace: they include what they need
    /// themselves and put their functions into `namespace fire`, taking and returning `Value` (see native/bridges/ of the compiler for examples). The virtual machine has no
    /// way to run C++: calling such a native there is an error that says so; a prelude that has no natives works in both.</summary>
    public sealed class PackageNative
    {
        public List<string> Sources { get; set; } = new();
        /// <summary>The platforms of the target (`posix`, `windows`, `freertos`, ...) the source is written for; empty: all.</summary>
        public List<string> Platforms { get; set; } = new();
        public List<PackageNativeFunction> Functions { get; set; } = new();
    }

    public sealed class PackageNativeFunction
    {
        /// <summary>The name that fire code calls (the prelude of the package, or a script directly).</summary>
        public string Name { get; set; } = "";
        public int Arguments { get; set; }
        /// <summary>The C++ function: `Value cpp(Value a, Value b)`.</summary>
        public string Cpp { get; set; } = "";
        /// <summary>The function gets the list of the scope (`OwnList* list`) as its last argument: what it allocates for its result (arrays, buffers) belongs to the caller.</summary>
        public bool NeedsList { get; set; }
        /// <summary>The result can be an object, array or buffer that has to be adopted (set it with `NeedsList`).</summary>
        public bool ReturnsReference { get; set; }
    }

    public sealed class PackageException : Exception
    {
        public PackageException(string message) : base(message) { }
    }

    /// <summary>`1.2.3`, `1.2`, `2.0.0-beta1`: numbers compared one by one; a version with a suffix is older than the same one without.</summary>
    public readonly struct SemanticVersion : IComparable<SemanticVersion>
    {
        private readonly int[] _parts;
        public string Suffix { get; }
        public string Text { get; }

        private SemanticVersion(int[] parts, string suffix, string text) { _parts = parts; Suffix = suffix; Text = text; }

        public static bool TryParse(string? text, out SemanticVersion version)
        {
            version = default;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string t = text.Trim();
            int dash = t.IndexOf('-');
            string numbers = dash < 0 ? t : t.Substring(0, dash);
            string suffix = dash < 0 ? "" : t.Substring(dash + 1);
            var parts = new List<int>();
            foreach (var p in numbers.Split('.'))
            {
                if (!int.TryParse(p, out int n) || n < 0) return false;
                parts.Add(n);
            }
            version = new SemanticVersion(parts.ToArray(), suffix, t);
            return true;
        }

        public int CompareTo(SemanticVersion other)
        {
            int n = Math.Max(_parts.Length, other._parts.Length);
            for (int i = 0; i < n; i++)
            {
                int a = i < _parts.Length ? _parts[i] : 0, b = i < other._parts.Length ? other._parts[i] : 0;
                if (a != b) return a.CompareTo(b);
            }
            if (Suffix == other.Suffix) return 0;
            if (Suffix.Length == 0) return 1;
            if (other.Suffix.Length == 0) return -1;
            return string.CompareOrdinal(Suffix, other.Suffix);
        }

        public override string ToString() => Text;
    }
}
