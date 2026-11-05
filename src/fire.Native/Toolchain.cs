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
        /// <summary><c>gcc</c>, <c>clang</c>, <c>msvc</c>, <c>tcc</c> (TinyCC, C only), <c>custom</c> or <c>files</c>.</summary>
        public string? Kind { get; set; }
        /// <summary>The compiler: a name on the PATH or a full path (default by kind: <c>g++</c>, <c>clang++</c>, <c>cl</c>).</summary>
        public string? Compiler { get; set; }
        /// <summary>The C++ standard of the natives in C++ (default <c>c++17</c>).</summary>
        public string? Std { get; set; }
        /// <summary>The C standard of the program (default <c>c11</c>).</summary>
        public string? CStd { get; set; }
        /// <summary>The C compiler (default: derived from <see cref="Compiler"/> - <c>g++</c> -> <c>gcc</c>, <c>clang++</c> -> <c>clang</c>, <c>em++</c> -> <c>emcc</c>; <c>cl</c> compiles both).</summary>
        public string? CCompiler { get; set; }
        /// <summary>The optimisation flag (<c>-O2</c>), or <c>size</c>: a small program (gcc/clang: <c>-Os</c>, every function and datum in its own section, the linker drops what nothing uses; msvc: <c>/O1</c> and <c>/OPT:REF</c>).</summary>
        public string? Optimization { get; set; }
        /// <summary>Remove symbols and debug information from a program (gcc/clang: <c>-s</c>); null: yes (a program built for a debugger sets <c>false</c>).</summary>
        public bool? Strip { get; set; }
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
        /// <summary>The SDL a window is built with when the target does not say: 2 or 3 (null: SDL3 when this toolchain can compile and link it, else SDL2).</summary>
        public int? Sdl { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public string EffectiveKind => Kind ?? "gcc";

        public static readonly IReadOnlyDictionary<string, ToolchainDef> BuiltIn = WithDrivers(new Dictionary<string, ToolchainDef>(StringComparer.OrdinalIgnoreCase)
        {
            ["gcc"] = new() { Kind = "gcc", Compiler = "g++", Std = "c++17", Optimization = "-O2", Args = new() { "-Wall", "-Wextra" } },
            ["clang"] = new() { Kind = "clang", Compiler = "clang++", Std = "c++17", Optimization = "-O2", Args = new() { "-Wall", "-Wextra" } },
            ["msvc"] = new() { Kind = "msvc", Compiler = "cl", Std = "c++17", Optimization = "/O2", Args = new() { "/nologo", "/EHsc" } },
            // Emscripten (emsdk): C++ to WebAssembly. -fexceptions: fire exceptions are C++ exceptions, which Emscripten leaves off by default. ASYNCIFY lets `Sleep` and the window loop hand the thread back to the browser; SINGLE_FILE puts the .wasm into the page, so that the
            // .html opens as it is (also from a folder, without a web server).
            ["emscripten"] = new() { Kind = "emscripten", Compiler = "em++", Std = "c++17", CStd = "gnu11", Optimization = "-O2", Args = new() { "-Wall", "-Wextra", "-fexceptions", "-sASYNCIFY", "-sALLOW_MEMORY_GROWTH=1", "-sSINGLE_FILE=1", "-sMINIFY_HTML=0" } },
            // TinyCC: a small, fast C compiler - enough for programs whose packages are all in C (it compiles no C++: a program with natives in C++ needs one of the
            // others). -w: tcc warns about every call of a function that does not return (it does not know the attribute).
            ["tcc"] = new() { Kind = "tcc", Compiler = "tcc", CCompiler = "tcc", CStd = "c11", Optimization = "-O2", Args = new() { "-w" } },
            ["files"] = new() { Kind = "files", Layout = "flat" },
            ["esp-idf"] = new() { Kind = "files", Layout = "idf-component" },
        });

        /// <summary>The built-in toolchains of the drivers (Toolchains/: Open Watcom, ...) join the first ones.</summary>
        private static Dictionary<string, ToolchainDef> WithDrivers(Dictionary<string, ToolchainDef> builtIn)
        {
            foreach (var driver in ToolchainDrivers.Each) builtIn[driver.Kind] = driver.Default;
            return builtIn;
        }

        /// <summary>This toolchain with the fields it leaves open taken from <paramref name="b"/>.</summary>
        public ToolchainDef WithBase(ToolchainDef? b)
        {
            if (b == null) return this;
            return new ToolchainDef
            {
                Extends = Extends, Kind = Kind ?? b.Kind, Compiler = Compiler ?? b.Compiler, Std = Std ?? b.Std, CStd = CStd ?? b.CStd,
                CCompiler = CCompiler ?? (Compiler != null ? null : b.CCompiler), Optimization = Optimization ?? b.Optimization, Strip = Strip ?? b.Strip,
                Args = Args ?? b.Args, Libs = Libs ?? b.Libs, IncludeDirs = IncludeDirs ?? b.IncludeDirs, Command = Command ?? b.Command,
                Layout = Layout ?? b.Layout, BuildCommand = BuildCommand ?? b.BuildCommand, Sdl = Sdl ?? b.Sdl,
            };
        }

        /// <summary>The compiler to run (the default of the kind when none is named).</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string EffectiveCompiler => Compiler ?? ToolchainDrivers.For(EffectiveKind)?.Default.Compiler ?? EffectiveKind switch { "clang" => "clang++", "msvc" => "cl", "emscripten" => "em++", "tcc" => "tcc", _ => "g++" };

        /// <summary>Whether the toolchain compiles C++ too (TinyCC does not).</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool CompilesCpp => EffectiveKind != "tcc" && (ToolchainDrivers.For(EffectiveKind)?.CompilesCpp ?? true);

        /// <summary>The C compiler to run: <see cref="CCompiler"/>, or the one that belongs to the C++ compiler (same folder, same prefix: <c>x86_64-w64-mingw32-g++</c> ->
        /// <c>x86_64-w64-mingw32-gcc</c>).</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string EffectiveCCompiler => CCompiler ?? CCompilerFor(EffectiveCompiler);

        /// <summary>The C compiler that goes with the C++ compiler <paramref name="cxx"/>.</summary>
        public static string CCompilerFor(string cxx)
        {
            string dir = Path.GetDirectoryName(cxx) ?? "";
            string file = Path.GetFileName(cxx);
            string ext = file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? ".exe" : "";
            string stem = ext.Length > 0 ? file.Substring(0, file.Length - 4) : file;
            string c = stem.EndsWith("clang++", StringComparison.Ordinal) ? stem.Substring(0, stem.Length - 2)
                : stem.EndsWith("g++", StringComparison.Ordinal) ? stem.Substring(0, stem.Length - 3) + "gcc"
                : stem.EndsWith("em++", StringComparison.Ordinal) ? stem.Substring(0, stem.Length - 4) + "emcc"
                : stem.EndsWith("c++", StringComparison.Ordinal) ? stem.Substring(0, stem.Length - 3) + "cc"
                : stem;   // (cl compiles C as well)
            return dir.Length > 0 ? Path.Combine(dir, c + ext) : c + ext;
        }
    }

    public static class ToolchainDetector
    {
        /// <summary>The full path of the compiler of <paramref name="toolchain"/> on this machine, or null (for `files` there is nothing to find).</summary>
        public static string? Find(ToolchainDef toolchain)
        {
            if (toolchain.EffectiveKind is "files" or "custom") return null;
            if (ToolchainDrivers.For(toolchain.EffectiveKind) is { } driver) return driver.Locate(toolchain);
            string name = toolchain.EffectiveCompiler;
            return ToolchainSetup.FindProgram(name);
        }

        /// <summary>The full path of the C compiler of <paramref name="toolchain"/>, or null.</summary>
        public static string? FindC(ToolchainDef toolchain)
        {
            if (toolchain.EffectiveKind is "files" or "custom") return null;
            if (ToolchainDrivers.For(toolchain.EffectiveKind) is { } driver) return driver.Locate(toolchain);
            if (toolchain.CCompiler == null && Find(toolchain) is { } cxx)
            {
                // next to the C++ compiler that was found (a portable toolchain is not on the PATH)
                string beside = ToolchainDef.CCompilerFor(cxx);
                if (File.Exists(beside)) return beside;
            }
            return ToolchainSetup.FindProgram(toolchain.EffectiveCCompiler);
        }

        /// <summary>The built-in toolchains that can be used on this machine right now.</summary>
        public static IReadOnlyList<string> Available() =>
            ToolchainDef.BuiltIn.Where(kv => kv.Value.EffectiveKind == "files" || Find(kv.Value) != null).Select(kv => kv.Key).ToList();
    }
}
