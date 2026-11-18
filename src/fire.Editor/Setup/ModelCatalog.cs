using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace fire.Editor.Setup
{
    /// <summary>A model of the catalog of the package ai.models (docs/ai/models.md), as its <c>AI.ModelInfo</c> has it.</summary>
    public sealed record SetupModelInfo(string Id, string Name, string Description, string Repo, string File, long Size, string Sha256, string License, string LicenseUrl,
        int MinMemoryMB, int GoodMemoryMB)
    {
        /// <summary>"507 MB", "1.0 GB" - as <c>ModelInfo.SizeText()</c>.</summary>
        public string SizeText => SetupFormat.Size(Size);
    }

    /// <summary>
    /// The catalog of the package ai.models for spark's first-start setup: read from the package's source (<c>Source/ai.models/models.fire</c> next to spark - the list belongs to the
    /// package, it is not repeated here), with the same choice for this machine (<c>Models.Recommended</c>, <c>Stronger</c>, <c>Fits</c>), the same folder (<c>Models.Folder</c>,
    /// FIRE_MODELS) and the same address (<c>Models.MirrorUrl</c>, FIRE_MODELS_MIRROR). A model it downloads is the file the assistant looks for.
    /// </summary>
    public static class ModelCatalog
    {
        /// <summary>The source of the catalog: the package ai.models in the standard sources next to the program.</summary>
        public static string DefaultSource => Path.Combine(AppContext.BaseDirectory, "Source", "ai.models", "models.fire");

        /// <summary>The models of <paramref name="file"/> (default: <see cref="DefaultSource"/>), in its order (the default first); empty when it is not there.</summary>
        public static IReadOnlyList<SetupModelInfo> Load(string? file = null)
        {
            try
            {
                string path = file ?? DefaultSource;
                return File.Exists(path) ? Parse(File.ReadAllText(path)) : Array.Empty<SetupModelInfo>();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<SetupModelInfo>(); }
        }

        /// <summary>The models of the source of ai.models: every <c>new AI.ModelInfo(...)</c> with its eleven arguments (strings and numbers); others are left out.</summary>
        public static IReadOnlyList<SetupModelInfo> Parse(string source)
        {
            var result = new List<SetupModelInfo>();
            const string marker = "new AI.ModelInfo(";
            int at = 0;
            while ((at = source.IndexOf(marker, at, StringComparison.Ordinal)) >= 0)
            {
                at += marker.Length;
                var args = Arguments(source, ref at);
                if (args == null || args.Count != 11) continue;
                if (!long.TryParse(args[5], out long size) || !int.TryParse(args[9], out int min) || !int.TryParse(args[10], out int good)) continue;
                result.Add(new SetupModelInfo(args[0], args[1], args[2], args[3], args[4], size, args[6], args[7], args[8], min, good));
            }
            return result;
        }

        /// <summary>The arguments of a call up to its ')': string literals (their text) and numbers; null for anything else (an expression).</summary>
        private static List<string>? Arguments(string s, ref int i)
        {
            var args = new List<string>();
            while (i < s.Length)
            {
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
                if (i >= s.Length) return null;
                if (s[i] == ')') { i++; return args; }
                if (s[i] == '"')
                {
                    var text = new StringBuilder();
                    i++;
                    while (i < s.Length && s[i] != '"')
                    {
                        if (s[i] == '\\' && i + 1 < s.Length)
                        {
                            i++;
                            text.Append(s[i] switch { 'n' => '\n', 't' => '\t', _ => s[i] });
                        }
                        else text.Append(s[i]);
                        i++;
                    }
                    i++;
                    args.Add(text.ToString());
                }
                else if (char.IsDigit(s[i]) || s[i] == '-')
                {
                    int start = i++;
                    while (i < s.Length && char.IsDigit(s[i])) i++;
                    args.Add(s.Substring(start, i - start));
                }
                else return null;
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
                if (i < s.Length && s[i] == ',') i++;
                else if (i < s.Length && s[i] != ')') return null;
            }
            return null;
        }

        /// <summary>The model for this machine without asking (as <c>Models.Recommended</c>): the smallest on a weak one (less than 3 GB memory, or no SIMD with less than 4 threads),
        /// else the default (the first of the catalog, Qwen3.5 0.8B).</summary>
        public static SetupModelInfo? Recommended(IReadOnlyList<SetupModelInfo> catalog, MachineInfo machine)
        {
            if (catalog.Count == 0) return null;
            bool weak = (machine.MemoryMB > 0 && machine.MemoryMB < 3072) || (!machine.Simd && machine.Cores < 4);
            return (weak ? Find(catalog, "qwen2.5-0.5b") : Find(catalog, "qwen3.5-0.8b")) ?? catalog[0];
        }

        /// <summary>The stronger model to offer when the machine is good enough for it (as <c>Models.Stronger</c>); null when it is not.</summary>
        public static SetupModelInfo? Stronger(IReadOnlyList<SetupModelInfo> catalog, MachineInfo machine)
        {
            var m = Find(catalog, "lfm3-1.7b");
            if (m == null) return null;
            return (machine.MemoryMB < 0 || machine.MemoryMB >= m.GoodMemoryMB) && machine.Cores >= 4 && machine.Simd ? m : null;
        }

        /// <summary>Can the model run here at all (enough memory)?</summary>
        public static bool Fits(SetupModelInfo m, MachineInfo machine) => machine.MemoryMB < 0 || machine.MemoryMB >= m.MinMemoryMB;

        public static SetupModelInfo? Find(IReadOnlyList<SetupModelInfo> catalog, string id) =>
            catalog.FirstOrDefault(m => m.Id == id || m.File == id);

        /// <summary>The folder of the models (as <c>Models.Folder</c>): FIRE_MODELS, else <c>fire/models</c> in the folder of the user's application data.</summary>
        public static string Folder()
        {
            string? env = Environment.GetEnvironmentVariable("FIRE_MODELS");
            return !string.IsNullOrEmpty(env) ? env : Path.Combine(DataFolder(), "fire", "models");
        }

        /// <summary>The folder of the user's application data as ai.models has it (%AppData%, ~/Library/Application Support, XDG_CONFIG_HOME or ~/.config).</summary>
        public static string DataFolder()
        {
            if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("APPDATA") is { Length: > 0 } appData) return appData;
            string? home = Environment.GetEnvironmentVariable("HOME");
            if (string.IsNullOrEmpty(home)) home = Environment.GetEnvironmentVariable("USERPROFILE");
            if (string.IsNullOrEmpty(home)) return Path.GetTempPath();
            if (OperatingSystem.IsMacOS()) return Path.Combine(home, "Library", "Application Support");
            if (Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg) return xdg;
            return Path.Combine(home, ".config");
        }

        public static string PathOf(SetupModelInfo m, string? folder = null) => Path.Combine(folder ?? Folder(), m.File);

        /// <summary>Is it there in full (the download leaves a ".part" file until it is done)?</summary>
        public static bool Installed(SetupModelInfo m, string? folder = null)
        {
            string path = PathOf(m, folder);
            try { return File.Exists(path) && new FileInfo(path).Length == m.Size; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }

        /// <summary>The address of the file (as <c>ModelInfo.Url</c>): Hugging Face, or the mirror of FIRE_MODELS_MIRROR.</summary>
        public static string UrlOf(SetupModelInfo m, string? mirror = null)
        {
            string url = mirror ?? Environment.GetEnvironmentVariable("FIRE_MODELS_MIRROR") ?? "";
            if (url == "") url = "https://huggingface.co/";
            if (!url.EndsWith('/')) url += "/";
            return url + m.Repo + "/resolve/main/" + m.File;
        }
    }

    /// <summary>What the choice of a model looks at: the memory of the machine (MB, -1 unknown), its threads and whether it has SIMD (AVX2, NEON).</summary>
    public readonly record struct MachineInfo(long MemoryMB, int Cores, bool Simd)
    {
        public static MachineInfo Current()
        {
            long memory = -1;
            try
            {
                long bytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
                if (bytes > 0) memory = bytes / (1024 * 1024);
            }
            catch (Exception) { }
            bool simd = System.Runtime.Intrinsics.X86.Avx2.IsSupported || System.Runtime.Intrinsics.Arm.AdvSimd.IsSupported;
            return new MachineInfo(memory, Environment.ProcessorCount, simd);
        }
    }
}
