using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace fire.Native
{
    /// <summary>What a native build is for: <see cref="Build"/> (spark's Build, Run, Debug, Build Solution, `forge build`, native tests) or <see cref="Publish"/>
    /// (Publish in spark, `forge publish`). The machine chooses a compiler for each (<see cref="MachineToolchains"/>); Debug and Release stay build modes.</summary>
    public enum ToolchainPurpose { Build, Publish }

    /// <summary>
    /// The compilers chosen for this machine, one for building and one for publishing (spark's Tools > Options > Compilers and the setup wizard): kept in
    /// <c>Toolchain\toolchain.json</c> next to the program as <c>"build"</c> and <c>"publish"</c> - a kind (<c>tcc</c>, <c>gcc</c>, <c>clang</c>, <c>msvc</c>) or
    /// <c>"auto"</c> - beside the toolchain the machine had before (its other fields: the compiler that w64devkit or the toolchain settings left there, <see cref="Legacy"/>).
    /// <para>A project's own toolchain (its settings, <c>fire.native.json</c>, a target that names one) and <c>--toolchain</c> go before this choice. Automatic is the recommended
    /// compiler when it is there (Build: TinyCC, Publish: GCC), else the best one that is there. A file of an older fire without the two fields: the toolchain it holds is the
    /// choice for both.</para>
    /// </summary>
    public sealed record MachineToolchains(string Build, string Publish, ToolchainDef? Legacy = null)
    {
        public const string Automatic = "auto";

        /// <summary>The kinds that can be chosen (the rows of the grid).</summary>
        public static readonly IReadOnlyList<string> Kinds = new[] { "tcc", "gcc", "clang", "msvc" };

        public static MachineToolchains Default { get; } = new(Automatic, Automatic);

        public string For(ToolchainPurpose purpose) => purpose == ToolchainPurpose.Build ? Build : Publish;

        public MachineToolchains With(ToolchainPurpose purpose, string choice) => purpose == ToolchainPurpose.Build ? this with { Build = choice } : this with { Publish = choice };

        /// <summary>The recommended compiler: TinyCC for building (fast, barely optimised), GCC for publishing (slow, proven, optimised; on Windows w64devkit).</summary>
        public static string Recommended(ToolchainPurpose purpose) => purpose == ToolchainPurpose.Build ? "tcc" : "gcc";

        /// <summary>The order in which "automatic" takes the compilers that are there.</summary>
        public static IReadOnlyList<string> AutomaticOrder(ToolchainPurpose purpose, bool windows) => purpose == ToolchainPurpose.Build
            ? new[] { "tcc", "gcc", "clang", "msvc" }
            : windows ? new[] { "gcc", "msvc", "clang", "tcc" } : new[] { "gcc", "clang", "msvc", "tcc" };

        /// <summary>The kind a choice comes to on a machine where <paramref name="available"/> says which kinds are there, and why ("chosen", "automatic", ...). Null: none is there.</summary>
        public static (string? Kind, string Source) Pick(string choice, ToolchainPurpose purpose, Func<string, bool> available, bool? windows = null)
        {
            bool win = windows ?? OperatingSystem.IsWindows();
            if (choice != Automatic && available(choice)) return (choice, "chosen in the IDE options");
            foreach (string kind in AutomaticOrder(purpose, win))
                if (available(kind)) return (kind, choice != Automatic ? $"automatic: {choice} is not installed" : kind == Recommended(purpose) ? "automatic, recommended" : "automatic");
            return (null, "no compiler found");
        }

        /// <summary>The kind that compiles C++ which stands in for a C-only compiler (TinyCC) when a program has natives in C++: the first of Publish's automatic order.</summary>
        public static string? PickCpp(Func<string, bool> available, bool? windows = null) =>
            AutomaticOrder(ToolchainPurpose.Publish, windows ?? OperatingSystem.IsWindows()).FirstOrDefault(k => k != "tcc" && available(k));

        /// <summary>The toolchain of a kind: the one in toolchain.json when it is of that kind (it may name the compiler with its path), else the built-in one.</summary>
        public ToolchainDef DefFor(string kind) =>
            Legacy != null && string.Equals(Legacy.EffectiveKind, kind, StringComparison.OrdinalIgnoreCase) ? Legacy : ToolchainDef.BuiltIn[kind];

        /// <summary>The toolchain for <paramref name="purpose"/> on this machine, marked <see cref="ToolchainDef.MachineChoice"/> (a copy), with where it comes from; null when no
        /// compiler is there. <paramref name="find"/> looks for the compiler (default: <see cref="ToolchainDetector.Find"/>).</summary>
        public (ToolchainDef? Toolchain, string Source) Resolve(ToolchainPurpose purpose, Func<ToolchainDef, string?>? find = null, bool? windows = null)
        {
            find ??= ToolchainDetector.Find;
            var (kind, source) = Pick(For(purpose), purpose, k => Found(k, find), windows);
            return (kind == null ? null : Mark(DefFor(kind)), source);
        }

        /// <summary>The toolchain that compiles C++ for a program whose natives are C++ when the chosen one is C only (TinyCC), marked as the machine's; null: none is there.</summary>
        public ToolchainDef? CppFallback(Func<ToolchainDef, string?>? find = null, bool? windows = null)
        {
            find ??= ToolchainDetector.Find;
            return PickCpp(k => Found(k, find), windows) is { } kind ? Mark(DefFor(kind)) : null;
        }

        private bool Found(string kind, Func<ToolchainDef, string?> find)
        {
            try { return ToolchainDef.BuiltIn.ContainsKey(kind) && find(DefFor(kind)) != null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return false; }
        }

        private static ToolchainDef Mark(ToolchainDef def)
        {
            var copy = new ToolchainDef { Extends = def.Extends }.WithBase(def);
            copy.MachineChoice = true;
            return copy;
        }

        // ---- the file ----

        /// <summary>toolchain.json next to the program (<see cref="ToolchainSetup.Root"/>).</summary>
        public static string DefaultFile => Path.Combine(ToolchainSetup.Root, "toolchain.json");

        public static readonly JsonNodeOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

        private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

        /// <summary>The choice of the machine (no file, or a broken one: automatic for both).</summary>
        public static MachineToolchains Load(string? file = null)
        {
            file ??= DefaultFile;
            try
            {
                if (!File.Exists(file)) return Default;
                if (JsonNode.Parse(File.ReadAllText(file), CaseInsensitive) is not JsonObject json) return Default;
                ToolchainDef? legacy = null;
                if (json.ContainsKey("kind") || json.ContainsKey("compiler") || json.ContainsKey("extends"))
                {
                    var def = json.Deserialize<ToolchainDef>(ReadOptions);
                    legacy = def?.WithBase(def.Kind is { } k && ToolchainDef.BuiltIn.TryGetValue(k, out var b) ? b : null);
                }
                // (a file of an older fire: the toolchain it holds is the choice for both)
                string old = legacy != null && Kinds.Contains(legacy.EffectiveKind, StringComparer.OrdinalIgnoreCase) ? legacy.EffectiveKind.ToLowerInvariant() : Automatic;
                return new MachineToolchains(Normalize(Text(json, "build")) ?? old, Normalize(Text(json, "publish")) ?? old, legacy);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException or FormatException) { return Default; }
        }

        private static string? Text(JsonObject json, string key) =>
            json[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

        /// <summary>A known kind (lower case) or "auto"; anything else: null (the file is read as if the field were not there).</summary>
        public static string? Normalize(string? choice)
        {
            if (string.IsNullOrWhiteSpace(choice)) return null;
            string c = choice.Trim().ToLowerInvariant();
            return c == Automatic || Kinds.Contains(c) ? c : c == "automatic" ? Automatic : c == "w64devkit" ? "gcc" : null;
        }

        /// <summary>Writes "build" and "publish" into the file; what else it holds (the machine's toolchain) stays.</summary>
        public void Save(string? file = null)
        {
            file ??= DefaultFile;
            JsonObject json;
            try { json = File.Exists(file) && JsonNode.Parse(File.ReadAllText(file), CaseInsensitive) is JsonObject o ? o : new JsonObject(CaseInsensitive); }
            catch (JsonException) { json = new JsonObject(CaseInsensitive); }
            json["build"] = Build;
            json["publish"] = Publish;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            File.WriteAllText(file, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        /// <summary>The first line of what the compiler says about its version ("gcc (GCC) 13.2.0", "tcc version 0.9.27"), or null - asked with a short time limit.</summary>
        public static string? VersionOf(string compiler, string kind)
        {
            try
            {
                var psi = new ProcessStartInfo(compiler, kind switch { "msvc" => "", "tcc" => "-v", _ => "--version" })
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using var p = Process.Start(psi);
                if (p == null) return null;
                var stdout = p.StandardOutput.ReadToEndAsync();
                var stderr = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(5000)) { try { p.Kill(true); } catch (InvalidOperationException) { } return null; }
                string text = stdout.Result.Trim().Length > 0 ? stdout.Result : stderr.Result;
                return text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return null; }
        }
    }
}
