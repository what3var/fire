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
        public Dictionary<string, TargetDef> Targets { get; set; } = new();
        public Dictionary<string, ToolchainDef> Toolchains { get; set; } = new();

        /// <summary>Where the configuration was read from (null: built-in defaults); not saved.</summary>
        [JsonIgnore] public string? Path { get; set; }

        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
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
        public IEnumerable<string> ToolchainNames => ToolchainDef.BuiltIn.Keys.Concat(Toolchains.Keys).Distinct(StringComparer.OrdinalIgnoreCase);

        /// <summary>The toolchain to build <paramref name="target"/> with: the one asked for, the configured default, the target's, else the first compiler found on this machine.</summary>
        public ToolchainDef ResolveToolchain(TargetProfile target, string? name = null)
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
        /// <summary>Stack of a fire thread in bytes.</summary>
        public int? StackBytes { get; set; }
        /// <summary>The `#import` libraries that exist on the target; null: as the base.</summary>
        public List<string>? Imports { get; set; }
        public bool? Embedded { get; set; }
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
                DefaultStackBytes = StackBytes ?? b.DefaultStackBytes,
                Imports = Imports ?? b.Imports,
                IsEmbedded = Embedded ?? b.IsEmbedded,
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
