using System.Diagnostics;
using System.Runtime.InteropServices;

namespace fire.Native
{
    /// <summary>
    /// A C++ toolchain: how the generated file is turned into a program. <c>gcc</c>, <c>clang</c> and <c>msvc</c> run the compiler; <c>custom</c> runs a command of your own;
    /// <c>files</c> only writes the sources (and, with a layout, the build files of a project, e.g. an ESP-IDF component) and optionally runs a build command.
    /// Fields that are not set come from <see cref="Extends"/> (a built-in toolchain) or the defaults of the kind.
    /// </summary>
    public sealed class ToolchainDef
    {
        public string? Extends { get; set; }
        /// <summary><c>gcc</c>, <c>clang</c>, <c>msvc</c>, <c>custom</c> or <c>files</c>.</summary>
        public string? Kind { get; set; }
        /// <summary>The compiler: a name on the PATH or a full path (default by kind: <c>g++</c>, <c>clang++</c>, <c>cl</c>).</summary>
        public string? Compiler { get; set; }
        public string? Std { get; set; }
        public string? Optimization { get; set; }
        /// <summary>Further compiler arguments.</summary>
        public List<string>? Args { get; set; }
        /// <summary>Libraries and linker arguments (after the source).</summary>
        public List<string>? Libs { get; set; }
        public List<string>? IncludeDirs { get; set; }
        /// <summary><c>custom</c>: the command, with <c>{cpp}</c> (the file), <c>{dir}</c> (its folder), <c>{out}</c> (the program), <c>{args}</c> (compile arguments), <c>{libs}</c>.</summary>
        public string? Command { get; set; }
        /// <summary><c>files</c>: <c>flat</c> (just the sources) or <c>idf-component</c> (also a CMakeLists.txt that registers them as an ESP-IDF component).</summary>
        public string? Layout { get; set; }
        /// <summary>A command that runs in the output folder after the files are written (e.g. <c>idf.py build</c>).</summary>
        public string? BuildCommand { get; set; }

        public string EffectiveKind => Kind ?? "gcc";

        public static readonly IReadOnlyDictionary<string, ToolchainDef> BuiltIn = new Dictionary<string, ToolchainDef>(StringComparer.OrdinalIgnoreCase)
        {
            ["gcc"] = new() { Kind = "gcc", Compiler = "g++", Std = "c++17", Optimization = "-O2", Args = new() { "-Wall", "-Wextra" } },
            ["clang"] = new() { Kind = "clang", Compiler = "clang++", Std = "c++17", Optimization = "-O2", Args = new() { "-Wall", "-Wextra" } },
            ["msvc"] = new() { Kind = "msvc", Compiler = "cl", Std = "c++17", Optimization = "/O2", Args = new() { "/nologo", "/EHsc" } },
            ["files"] = new() { Kind = "files", Layout = "flat" },
            ["esp-idf"] = new() { Kind = "files", Layout = "idf-component" },
        };

        /// <summary>This toolchain with the fields it leaves open taken from <paramref name="b"/>.</summary>
        public ToolchainDef WithBase(ToolchainDef? b)
        {
            if (b == null) return this;
            return new ToolchainDef
            {
                Extends = Extends, Kind = Kind ?? b.Kind, Compiler = Compiler ?? b.Compiler, Std = Std ?? b.Std, Optimization = Optimization ?? b.Optimization,
                Args = Args ?? b.Args, Libs = Libs ?? b.Libs, IncludeDirs = IncludeDirs ?? b.IncludeDirs, Command = Command ?? b.Command,
                Layout = Layout ?? b.Layout, BuildCommand = BuildCommand ?? b.BuildCommand,
            };
        }

        /// <summary>The compiler to run (the default of the kind when none is named).</summary>
        public string EffectiveCompiler => Compiler ?? EffectiveKind switch { "clang" => "clang++", "msvc" => "cl", _ => "g++" };
    }

    public static class ToolchainDetector
    {
        /// <summary>The full path of the compiler of <paramref name="toolchain"/> on this machine, or null (for `files` there is nothing to find).</summary>
        public static string? Find(ToolchainDef toolchain)
        {
            if (toolchain.EffectiveKind is "files" or "custom") return null;
            string name = toolchain.EffectiveCompiler;
            return ToolchainSetup.FindProgram(name);
        }

        /// <summary>The built-in toolchains that can be used on this machine right now.</summary>
        public static IReadOnlyList<string> Available() =>
            ToolchainDef.BuiltIn.Where(kv => kv.Value.EffectiveKind == "files" || Find(kv.Value) != null).Select(kv => kv.Key).ToList();
    }
}
