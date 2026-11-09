using fire.Runtime;

namespace fire.Native
{
    /// <summary>What a toolchain driver is asked for: compile (and link) one generated C file for a target.</summary>
    public sealed record CompileRequest(ToolchainDef Toolchain, TargetProfile Target, string SourceFile, string IncludeDir, string Output, IReadOnlyList<string> IncludeDirs,
        IReadOnlyList<string> Libraries, bool CompileOnly, IReadOnlyList<string> ExtraInputs, bool DebugBuild);

    /// <summary>
    /// A kind of toolchain that is not one of the first ones (gcc, clang, msvc, tcc, emscripten - their command lines are in NativeBuilder.CompilerCommand): its command
    /// line, where its compiler is and what its programs need in their environment. A new compiler is one class of this kind in this folder and one line in
    /// <see cref="ToolchainDrivers"/> - nothing else of the build changes.
    /// </summary>
    public interface IToolchainDriver
    {
        /// <summary>The kind of toolchain (`kind` in fire.native.json).</summary>
        string Kind { get; }

        /// <summary>The built-in toolchain of this kind (its defaults).</summary>
        ToolchainDef Default { get; }

        /// <summary>Whether it compiles C++ too (the natives of packages in C++).</summary>
        bool CompilesCpp { get; }

        /// <summary>The command that builds the request: the program and its arguments.</summary>
        (string Executable, string Arguments) Command(CompileRequest request);

        /// <summary>The compiler of this toolchain on this machine (null: not found - the generic search of the PATH runs after).</summary>
        string? Locate(ToolchainDef toolchain);

        /// <summary>Adds what the programs of the toolchain need to their environment (Open Watcom: WATCOM and INCLUDE) when <paramref name="executable"/> is one of them.</summary>
        void PrepareEnvironment(string executable, IDictionary<string, string?> environment);

        /// <summary>Commands that run before the program is compiled (in the folder of the build, <paramref name="includeDir"/>): Open Watcom assembles the startup code
        /// of a platform without an operating system. None by default.</summary>
        IReadOnlyList<(string Executable, string Arguments)> PreCommands(ToolchainDef toolchain, TargetProfile target, string includeDir) => Array.Empty<(string, string)>();

        /// <summary>What happens after the program was linked to <paramref name="exe"/>: Open Watcom writes the disk image of a target without an operating system
        /// next to it. The text goes into the log of the build (null: nothing happened).</summary>
        string? AfterLink(ToolchainDef toolchain, TargetProfile target, string exe) => null;
    }

    /// <summary>The drivers of the toolchain kinds that have one.</summary>
    public static class ToolchainDrivers
    {
        private static readonly IToolchainDriver[] All = { new WatcomToolchain() };

        public static IToolchainDriver? For(string kind) => All.FirstOrDefault(d => string.Equals(d.Kind, kind, StringComparison.OrdinalIgnoreCase));

        public static IEnumerable<IToolchainDriver> Each => All;

        /// <summary>The commands of the driver of <paramref name="toolchain"/> before the program is compiled (none without a driver).</summary>
        public static IReadOnlyList<(string Executable, string Arguments)> PreCommands(ToolchainDef toolchain, TargetProfile target, string includeDir) =>
            For(toolchain.EffectiveKind)?.PreCommands(toolchain, target, includeDir) ?? Array.Empty<(string, string)>();

        /// <summary>The step of the driver of <paramref name="toolchain"/> after the link (its text for the log, or null).</summary>
        public static string? AfterLink(ToolchainDef toolchain, TargetProfile target, string exe) => For(toolchain.EffectiveKind)?.AfterLink(toolchain, target, exe);

        /// <summary>The environment of a tool of a build: every driver adds what its programs need.</summary>
        public static void PrepareEnvironment(string executable, IDictionary<string, string?> environment)
        {
            foreach (var driver in All) driver.PrepareEnvironment(executable, environment);
        }
    }
}
