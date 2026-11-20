using fire.Native;

namespace fire.Compiler
{
    public static partial class NativeBuilder
    {
        /// <summary>
        /// The quick build (`--quick`, spark's hare switch; docs/spec/NATIVE_SPLIT.md "Schnellbuild"): for test builds and test publishes, not for releases. Only the C compiler
        /// changes - its optimisation level (-O0, or -O1 with `--quick=1`; msvc /Od, /O1) and the units, forced on where the toolchain splits (gcc, clang, MinGW, Emscripten) -,
        /// not the program: its mode (Debug, Release, Performance: asserts, checks) and the generated C stay. The natives of the packages in a unit of their own keep the
        /// toolchain's optimisation (the C engine of ai, the rasterizer of graphics); the libraries of the packages for the VM are not built here at all. TinyCC (fast anyway,
        /// it hardly optimises), Open Watcom, `files` and `custom` build as without it. Only linkage `source`: static, shared and plugin build as they would.
        /// </summary>
        public static class QuickBuild
        {
            /// <summary>The level of `--quick` / `--quick=<level>`: 0 (default) or 1 (`0`, `1`, `O0`, `O1`, `-O0`, `-O1`); null: not a level.</summary>
            public static int? ParseLevel(string? text) => (text ?? "").Trim().TrimStart('-').TrimStart('O', 'o') switch
            {
                "" or "0" => 0,
                "1" => 1,
                _ => null,
            };

            /// <summary>The optimisation flag of a quick build with this toolchain kind (null: the kind keeps its own - TinyCC, Open Watcom, files, custom).</summary>
            public static string? Flag(string kind, int level) => kind switch
            {
                "gcc" or "clang" or "emscripten" => level == 0 ? "-O0" : "-O1",
                "msvc" => level == 0 ? "/Od" : "/O1",
                _ => null,
            };

            /// <summary>Whether a quick build with this kind is split (forced on where `auto` would split a large program).</summary>
            public static bool Splits(string kind) => SplitModes.AutoToolchain(kind);

            /// <summary>The split mode of a build: a quick build with a toolchain that splits is split, every other build keeps the configuration's mode.</summary>
            public static SplitMode SplitOf(NativeConfig config, string kind) => config.Quick != null && Splits(kind) ? SplitMode.On : config.SplitValue;

            /// <summary>The toolchain of a quick build: a copy with the optimisation flag of the level (the toolchain itself when the kind keeps its own).</summary>
            public static ToolchainDef Apply(ToolchainDef toolchain, int level)
            {
                if (Flag(toolchain.EffectiveKind, level) is not { } flag || toolchain.Optimization == flag) return toolchain;
                var copy = new ToolchainDef { Extends = toolchain.Extends }.WithBase(toolchain);   // (a copy: the definition of the machine stays as it is)
                copy.Optimization = flag;
                return copy;
            }

            /// <summary>The mark of a quick build in the logs and the publish summary: "Schnellbuild (-O0, geteilt)" (with a kind: its flag, and "geteilt" only where it splits).</summary>
            public static string Label(int level, string? kind = null)
            {
                if (kind == null) return $"Schnellbuild (-O{level}, geteilt)";
                string? flag = Flag(kind, level);
                if (flag == null) return $"Schnellbuild ({kind}: wie ein normaler Build)";
                return Splits(kind) ? $"Schnellbuild ({flag}, geteilt)" : $"Schnellbuild ({flag}, eine Datei)";
            }

            /// <summary>The line of the build log: what the quick build changed, and that it is no release.</summary>
            public static string LogLine(int level, string kind) => Flag(kind, level) == null
                ? $"{Label(level, kind)}: the toolchain '{kind}' keeps its optimisation and builds one file (only gcc, clang, Emscripten and msvc build quicker)."
                : $"{Label(level, kind)}: a test build, not a release - the C compiler with {Flag(kind, level)}{(Splits(kind) ? ", the program in units built side by side" : "")}; the natives of the packages keep their optimisation.";
        }
    }
}
