using System.Text.Json;
using System.Text.Json.Serialization;
using fire.Runtime;

namespace fire.Native
{
    /// <summary>
    /// The native build configuration (<c>fire.native.json</c>): which engine <c>build</c> uses, the default target and toolchain, own targets (a board, a
    /// FreeRTOS port) and toolchains (which C++ compiler, with what arguments). Everything platform specific lives in the platform package of the runtime
    /// (<c>native/platform/&lt;name&gt;/</c>); a target only names the package, what is included before it and how the program starts. The editor edits this file.
    /// </summary>
    public sealed class NativeConfig
    {
        public const string FileName = "fire.native.json";

        /// <summary>What <c>build</c> produces: <c>vm</c> (a self-contained file that carries the VM, the default) or <c>native</c> (C++ translated and built with the toolchain).</summary>
        public string? Engine { get; set; }
        /// <summary>The target to build for (a built-in name or a key of <see cref="Targets"/>); null: this machine.</summary>
        public string? Target { get; set; }
        /// <summary>The default toolchain (a built-in name or a key of <see cref="Toolchains"/>) for targets that name none themselves; null: the first one found on this machine.</summary>
        public string? Toolchain { get; set; }
        /// <summary>How much a native build leaves out (`"stripping"`, docs/spec/NATIVE_BACKEND.md "Größe des Programms"): 0 `none` .. 3 `all` (null: the default, 3), as a
        /// number or a name; a debug build always keeps everything (0).</summary>
        [JsonConverter(typeof(StrippingJsonConverter))]
        public StrippingLevel? Stripping { get; set; }
        /// <summary>The old name of <see cref="Stripping"/> (`"trim"`): `"all"` is 0, `"reflection"` 1, `"reached"` 3. Read only - <see cref="Stripping"/> goes before it,
        /// and saving writes `"stripping"`.</summary>
        public string? Trim
        {
            get => null;
            set
            {
                if (value == null || StrippingExplicit) return;
                Stripping = Strippings.FromTrim(value) ?? throw new NativeConfigException($"{FileName}: \"trim\": '{value}' is not one of: all, reflection, reached (better: \"stripping\" 0-3).");
                _strippingFromTrim = true;
            }
        }
        private bool _strippingFromTrim;
        /// <summary><see cref="Stripping"/> was given on the command line (`--stripping`, `--keep-all`): it goes before the project's setting. Not saved.</summary>
        [JsonIgnore] public bool StrippingFromCommandLine { get; set; }
        /// <summary>(`"stripping"` was given: it goes before `"trim"`, whatever comes first in the file)</summary>
        private bool StrippingExplicit => Stripping != null && !_strippingFromTrim;
        /// <summary>When the main program runs the `fire global` jobs by itself (`"globalJobs"`: `safepoints`, `sleep` or `sync`; docs/spec/THREADING_DESIGN.md section 7): the default of a
        /// native build whose program says nothing (`#globaljobs`, `#nosync`); the project and `--global-jobs` go before it. Null: safepoints.</summary>
        public string? GlobalJobs
        {
            get => _globalJobs;
            set
            {
                if (value != null && GlobalJobsModes.Parse(value) == null)
                    throw new NativeConfigException($"{FileName}: \"globalJobs\": '{value}' is not one of: {string.Join(", ", GlobalJobsModes.Names)}.");
                _globalJobs = value;
            }
        }
        private string? _globalJobs;
        /// <summary><see cref="GlobalJobs"/> as a mode.</summary>
        [JsonIgnore] public GlobalJobsMode? GlobalJobsValue => GlobalJobsModes.Parse(_globalJobs);
        /// <summary>How the libraries and packages are linked (ROADMAP 9): `source` (null, the default: everything is one C file) or `static` (each library a static library of
        /// its own in the user-wide cache, built once per content and switches, linked into the program; docs/spec/NATIVE_BACKEND.md "Getrennte Übersetzung").</summary>
        public string? Linkage { get; set; }
        /// <summary>`--linkage plugin`: the program the plugin is built for (its path, or its `.firehost` file) - its switches and the interfaces of its libraries.</summary>
        public string? Host { get; set; }
        public Dictionary<string, TargetDef> Targets { get; set; } = new();
        public Dictionary<string, ToolchainDef> Toolchains { get; set; } = new();

        /// <summary>Where the configuration was read from (null: built-in defaults); not saved.</summary>
        [JsonIgnore] public string? Path { get; set; }

        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // g++ and c++17 as they are, not g\u002B\u002B
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };

        public static NativeConfig Parse(string json)
        {
            try { return JsonSerializer.Deserialize<NativeConfig>(json, Options) ?? new NativeConfig(); }
            catch (JsonException ex) { throw new NativeConfigException($"{FileName}: {ex.Message}"); }
        }

        public static NativeConfig Load(string path)
        {
            var config = Parse(File.ReadAllText(path));
            config.Path = System.IO.Path.GetFullPath(path);
            return config;
        }

        public string ToJson() => JsonSerializer.Serialize(this, Options);

        public void Save(string? path = null)
        {
            path ??= Path ?? throw new InvalidOperationException("no file name");
            File.WriteAllText(path, ToJson() + Environment.NewLine);
            Path = System.IO.Path.GetFullPath(path);
        }

        /// <summary>The configuration that applies to a source file: the nearest <c>fire.native.json</c> in its folder or above; without one, built-in defaults.</summary>
        public static NativeConfig FindFor(string sourceFile)
        {
            for (var dir = new DirectoryInfo(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(sourceFile)) ?? "."); dir != null; dir = dir.Parent)
            {
                string candidate = System.IO.Path.Combine(dir.FullName, FileName);
                if (File.Exists(candidate)) return Load(candidate);
            }
            return new NativeConfig();
        }

        // -------------------------------------------------------------------------------------------------------------
        // Targets
        // -------------------------------------------------------------------------------------------------------------
        /// <summary>The names of all targets: the built-in ones and the configured ones.</summary>
        [JsonIgnore]
        public IEnumerable<string> TargetNames => TargetProfile.All.Select(p => p.Name).Concat(Targets.Keys).Distinct(StringComparer.OrdinalIgnoreCase);

        /// <summary>The target <paramref name="name"/> (null: the configured default target, else this machine), with the configuration's changes applied.</summary>
        public TargetProfile ResolveTarget(string? name = null)
        {
            name ??= Target;
            if (name == null) return TargetProfile.Host;
            return Resolve(name, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        private TargetProfile Resolve(string name, HashSet<string> chain)
        {
            if (!chain.Add(name)) throw new NativeConfigException($"target '{name}' extends itself");
            var def = Targets.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
            TargetProfile baseProfile;
            if (def?.Extends is { } baseName) baseProfile = Resolve(baseName, chain);
            else if (TargetProfile.TryGet(name, out var builtIn)) baseProfile = builtIn;
            else if (def != null) baseProfile = TargetProfile.Host with { Name = name };
            else throw new NativeConfigException($"unknown target '{name}' (known: {string.Join(", ", TargetNames)})");
            return def == null ? baseProfile : def.Apply(baseProfile, name);
        }

        // -------------------------------------------------------------------------------------------------------------
        // Toolchains
        // -------------------------------------------------------------------------------------------------------------
        [JsonIgnore]
        public IEnumerable<string> ToolchainNames => ToolchainDef.BuiltIn.Keys.Concat(Toolchains.Keys).Distinct(StringComparer.OrdinalIgnoreCase);

        /// <summary>The toolchain to build <paramref name="target"/> with: the one asked for, the configured default, the target's, else the first compiler found on this machine.</summary>
        public ToolchainDef ResolveToolchain(TargetProfile target, string? name = null)
        {
            var chosen = ResolveToolchainNamed(target, name);
            // a toolchain that cannot build for the target (gcc for DOS) is not used: the target's own, else the first it allows
            if (target.AllowsToolchain(chosen.EffectiveKind)) return chosen;
            string own = target.Native.Toolchain is { } t && target.AllowsToolchain(ResolveToolchainNamed(target, t).EffectiveKind) ? t : target.Toolchains![0];
            return ResolveToolchainNamed(target, own);
        }

        /// <summary>The targets in the order of their choice: the built-in ones and the configured ones, resolved (a broken one is left out).</summary>
        [JsonIgnore]
        public IEnumerable<TargetProfile> TargetProfiles
        {
            get
            {
                foreach (string n in TargetNames)
                {
                    TargetProfile? t = null;
                    try { t = ResolveTarget(n); } catch (NativeConfigException) { }
                    if (t != null) yield return t;
                }
            }
        }

        private ToolchainDef ResolveToolchainNamed(TargetProfile target, string? name)
        {
            name ??= target.Native.Toolchain ?? Toolchain;
            if (name != null)
            {
                var found = Toolchains.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
                if (found != null) return found.WithBase(ToolchainDef.BuiltIn.GetValueOrDefault(found.Extends ?? name));
                if (ToolchainDef.BuiltIn.TryGetValue(name, out var builtIn)) return builtIn;
                throw new NativeConfigException($"unknown toolchain '{name}' (known: {string.Join(", ", ToolchainNames)})");
            }
            if (ToolchainSetup.LoadMachineToolchain() is { } chosen && ToolchainDetector.Find(chosen) != null) return chosen;   // the toolchain chosen for the machine (or installed by fire)
            if (ToolchainSetup.Detect() is { } detected) return detected.Toolchain;
            return ToolchainDef.BuiltIn["gcc"];
        }
    }

    /// <summary>How much a native build leaves out (`"stripping"` in fire.native.json, in the settings of a project, `--stripping` on the command line;
    /// docs/spec/NATIVE_BACKEND.md "Größe des Programms"). Each level leaves out what the one before keeps, and more.</summary>
    public enum StrippingLevel
    {
        /// <summary>0 `none`: everything stays (as in the VM): every class with all its members (`--keep-all`, debug builds).</summary>
        None = 0,
        /// <summary>1 `protected`: every class and every public member stays; a private or protected member only when the program reaches it (the rule of a library
        /// built on its own: its API and what it reaches).</summary>
        Protected = 1,
        /// <summary>2 `methods`: every class with its constructors, fields and properties stays; a method only when the program reaches it (or names it as a literal
        /// to reflection).</summary>
        Methods = 2,
        /// <summary>3 `all` (the default): only what the program reaches - also a class the program never makes is left out - plus `[Keep]` and the names given
        /// as literals to reflection.</summary>
        All = 3,
    }

    /// <summary>The names and numbers of the <see cref="StrippingLevel"/>s.</summary>
    public static class Strippings
    {
        public static readonly IReadOnlyList<string> Names = new[] { "none", "protected", "methods", "all" };

        /// <summary>A level from its number (`0`-`3`) or its name (`none`, `protected`, `methods`, `all`); null: neither.</summary>
        public static StrippingLevel? Parse(string? text)
        {
            text = text?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(text)) return null;
            if (text.Length == 1 && text[0] is >= '0' and <= '3') return (StrippingLevel)(text[0] - '0');
            int i = Names.ToList().IndexOf(text);
            return i < 0 ? null : (StrippingLevel)i;
        }

        /// <summary>The old values of `"trim"`: `all` keeps everything (0), `reflection` (keep every class once one is looked up by name) is 1, `reached` is 3.</summary>
        public static StrippingLevel? FromTrim(string text) => text.Trim().ToLowerInvariant() switch
        {
            "all" => StrippingLevel.None,
            "reflection" => StrippingLevel.Protected,
            "reached" => StrippingLevel.All,
            _ => null,
        };

        public static string Name(StrippingLevel level) => Names[(int)level];
    }

    /// <summary>`"stripping"`: a number (0-3) or a name; written as the name.</summary>
    public sealed class StrippingJsonConverter : JsonConverter<StrippingLevel?>
    {
        public override StrippingLevel? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string? text = reader.TokenType switch
            {
                JsonTokenType.Null => null,
                JsonTokenType.Number => reader.TryGetInt32(out int n) ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : "?",
                JsonTokenType.String => reader.GetString(),
                _ => "?",
            };
            if (text == null) return null;
            return Strippings.Parse(text) ?? throw new JsonException($"\"stripping\": '{text}' is not one of 0-3 or {string.Join(", ", Strippings.Names)}.");
        }

        public override void Write(Utf8JsonWriter writer, StrippingLevel? value, JsonSerializerOptions options)
        {
            if (value is { } level) writer.WriteStringValue(Strippings.Name(level));
            else writer.WriteNullValue();
        }
    }

    public sealed class NativeConfigException : Exception
    {
        public NativeConfigException(string message) : base(message) { }
    }

    /// <summary>The JSON form of a target: only what differs from its base (<see cref="Extends"/>, else the built-in target of the same name).</summary>
    public sealed class TargetDef
    {
        public string? Extends { get; set; }
        /// <summary>Symbols for `#if`.</summary>
        public List<string>? Symbols { get; set; }
        public int? FloatWidth { get; set; }
        /// <summary>Width of `int` (32 or 64; default 64).</summary>
        public int? IntWidth { get; set; }
        /// <summary>Width of `word`, the machine word (16, 32 or 64; default: the base target's - 64 on the desktop, 32 on esp32/freertos/wasm).</summary>
        public int? WordWidth { get; set; }
        /// <summary>Stack of a fire thread in bytes.</summary>
        public int? StackBytes { get; set; }
        /// <summary>The `#import` libraries that exist on the target; null: as the base.</summary>
        public List<string>? Imports { get; set; }
        public bool? Embedded { get; set; }
        /// <summary>The name shown in the choice of targets.</summary>
        public string? DisplayName { get; set; }
        /// <summary>The group in the choice of targets: "os", "native" or "embedded".</summary>
        public string? Category { get; set; }
        /// <summary>The kinds of toolchain that can build for the target ("watcom"); the others are not used for it.</summary>
        public List<string>? Toolchains { get; set; }
        /// <summary>The platform package of the runtime (`posix`, `windows`, `freertos`, `esp32`, or your own: see <see cref="PlatformPath"/>).</summary>
        public string? Platform { get; set; }
        /// <summary>A folder with your own platform package (it holds `fire_platform.hpp`); it is copied to `platform/&lt;platform&gt;/` next to the generated file.</summary>
        public string? PlatformPath { get; set; }
        public List<string>? Includes { get; set; }
        public List<string>? Defines { get; set; }
        public EntryDef? Entry { get; set; }
        public List<string>? CompileArgs { get; set; }
        public List<string>? LinkLibs { get; set; }
        public bool? SupportsThreads { get; set; }
        public string? Toolchain { get; set; }
        /// <summary>The SDL of a window on the desktop: 2 or 3 (null: the build decides, SDL3 when the toolchain has it).</summary>
        public int? Sdl { get; set; }
        /// <summary>`#line` directives in the generated C (null: on, see NativeTarget.LineDirectives).</summary>
        public bool? LineDirectives { get; set; }
        /// <summary>A board: its serial port (ESP32: Run and Debug in spark).</summary>
        public string? Port { get; set; }
        /// <summary>A board: the baud rate of its console.</summary>
        public int? Baud { get; set; }

        internal TargetProfile Apply(TargetProfile b, string name)
        {
            var native = b.Native with
            {
                Platform = Platform ?? b.Native.Platform,
                PlatformPath = PlatformPath ?? b.Native.PlatformPath,
                Includes = Includes ?? b.Native.Includes,
                Defines = Defines ?? b.Native.Defines,
                CompileArgs = CompileArgs ?? b.Native.CompileArgs,
                LinkLibs = LinkLibs ?? b.Native.LinkLibs,
                SupportsThreads = SupportsThreads ?? b.Native.SupportsThreads,
                Toolchain = Toolchain ?? b.Native.Toolchain,
                Sdl = Sdl ?? b.Native.Sdl,
                LineDirectives = LineDirectives ?? b.Native.LineDirectives,
                Port = Port ?? b.Native.Port,
                Baud = Baud ?? b.Native.Baud,
                Entry = Entry == null ? b.Native.Entry : b.Native.Entry with
                {
                    Name = Entry.Name ?? b.Native.Entry.Name,
                    Kind = Entry.Kind ?? b.Native.Entry.Kind,
                    ExternC = Entry.ExternC ?? b.Native.Entry.ExternC,
                },
            };
            return b with
            {
                Name = name,
                Symbols = Symbols ?? b.Symbols,
                FloatWidth = FloatWidth ?? b.FloatWidth,
                IntWidth = IntWidth ?? b.IntWidth,
                WordWidth = WordWidth is null or 16 or 32 or 64 ? WordWidth ?? b.WordWidth : throw new NativeConfigException($"Target '{name}': \"wordWidth\" must be 16, 32 or 64, not {WordWidth}."),
                DefaultStackBytes = StackBytes ?? b.DefaultStackBytes,
                Imports = Imports ?? b.Imports,
                IsEmbedded = Embedded ?? b.IsEmbedded,
                DisplayName = DisplayName ?? (string.Equals(name, b.Name, StringComparison.OrdinalIgnoreCase) ? b.DisplayName : null),
                Category = Category == null ? (Embedded == true ? TargetCategory.Embedded : b.Category)
                    : Enum.TryParse<TargetCategory>(Category, ignoreCase: true, out var category) ? category
                    : throw new NativeConfigException($"Target '{name}': \"category\" must be \"os\", \"native\" or \"embedded\", not \"{Category}\"."),
                Toolchains = Toolchains ?? b.Toolchains,
                HalPackage = Platform ?? b.HalPackage,
                Native = native,
            };
        }
    }

    public sealed class EntryDef
    {
        public string? Name { get; set; }
        public EntryKind? Kind { get; set; }
        public bool? ExternC { get; set; }
    }
}
