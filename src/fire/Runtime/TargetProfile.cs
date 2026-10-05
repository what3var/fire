using System.Runtime.InteropServices;

namespace fire.Runtime
{
    /// <summary>
    /// Describes a target a fire program is built for: the operating system or board, the defaults that depend on it
    /// (precision of `float`, stack size of threads), which `#import` libraries exist there, and which platform package
    /// of the C++ runtime (HAL, see docs/NATIVE_BACKEND.md) implements them.
    ///
    /// One description is shared by the linker (validates the imports and picks the default float precision), the native
    /// backend (entry point, defines, platform package) and - later - the preprocessor, whose `#if` symbols are
    /// <see cref="Symbols"/>. The VM that runs a script in the editor uses <see cref="Host"/>.
    /// </summary>
    public sealed record TargetProfile
    {
        /// <summary>What the native backend needs to build for this target: which platform package of the C++ runtime (native/platform/&lt;name&gt;/), what to
        /// include before it, the entry point, extra compiler arguments. A target configuration file (fire.native.json) can change all of it.</summary>
        public NativeTarget Native { get; init; } = new();

        /// <summary>Name on the command line (`--target esp32`).</summary>
        public required string Name { get; init; }

        /// <summary>Symbols for conditional compilation (`#if windows`): the operating system or board.</summary>
        public required IReadOnlyList<string> Symbols { get; init; }

        /// <summary>Default precision of `float` in bits (32 or 64); `#floatwidth` in the program and `-f` override it.</summary>
        public int FloatWidth { get; init; } = 64;

        /// <summary>Default stack size of a fire thread in bytes (used when `#stacksize` is not given and cannot be derived).</summary>
        public int DefaultStackBytes { get; init; } = 64 * 1024;

        /// <summary>The `#import` libraries that exist on this target; null means all of them.</summary>
        public IReadOnlyList<string>? Imports { get; init; }

        /// <summary>Platform package of the C++ runtime that implements the HAL (`windows`, `posix`, `esp32`).</summary>
        public required string HalPackage { get; init; }

        /// <summary>A small target without an operating system shell: no console program, the entry point is the board's.</summary>
        public bool IsEmbedded { get; init; }

        public bool HasImport(string import) => Imports == null || Imports.Contains(import);

        // -------------------------------------------------------------------------------------------------------------
        public static readonly TargetProfile Windows = new()
        {
            Name = "windows", Symbols = new[] { "windows" }, HalPackage = "windows",
            Native = new NativeTarget { Platform = "windows" },
        };

        public static readonly TargetProfile Linux = new()
        {
            Name = "linux", Symbols = new[] { "linux", "posix" }, HalPackage = "posix",
            Native = new NativeTarget { Platform = "posix", CompileArgs = new[] { "-pthread" } },
        };

        public static readonly TargetProfile MacOs = new()
        {
            Name = "macos", Symbols = new[] { "macos", "posix" }, HalPackage = "posix",
            Native = new NativeTarget { Platform = "posix", CompileArgs = new[] { "-pthread" } },
        };

        /// <summary>ESP32 with FreeRTOS (ESP-IDF): single-precision FPU, so `float` is 32 bits; small thread stacks; no windows or
        /// framebuffer (`graphics`, `ui`).</summary>
        public static readonly TargetProfile Esp32 = new()
        {
            Name = "esp32", Symbols = new[] { "esp32", "freertos" }, HalPackage = "esp32",
            FloatWidth = 32, DefaultStackBytes = 8192, IsEmbedded = true,
            Imports = new[] { NativeImports.Print, NativeImports.IO, NativeImports.Devices, NativeImports.Time, NativeImports.Reflection, NativeImports.Linq },
            Native = new NativeTarget
            {
                Platform = "esp32",
                Includes = new[] { "freertos/FreeRTOS.h", "freertos/task.h", "freertos/semphr.h" },
                Entry = new EntryPoint { Name = "app_main", Kind = EntryKind.Function, ExternC = true },
                Toolchain = "esp-idf",
            },
        };

        /// <summary>Any board with FreeRTOS: fire threads are tasks. The entry point is a function that board code calls from a task (it is not `main`: the
        /// board decides how the scheduler starts). The headers are the plain FreeRTOS ones; a configuration for a port with other names changes `includes`.</summary>
        public static readonly TargetProfile FreeRtos = new()
        {
            Name = "freertos", Symbols = new[] { "freertos" }, HalPackage = "freertos",
            FloatWidth = 32, DefaultStackBytes = 8192, IsEmbedded = true,
            Imports = new[] { NativeImports.Print, NativeImports.IO, NativeImports.Devices, NativeImports.Time, NativeImports.Reflection, NativeImports.Linq },
            Native = new NativeTarget
            {
                Platform = "freertos",
                Includes = new[] { "FreeRTOS.h", "task.h", "semphr.h" },
                Entry = new EntryPoint { Name = "fire_start", Kind = EntryKind.Function, ExternC = true },
                Toolchain = "files",
            },
        };

        private static readonly TargetProfile[] BuiltIn = { Windows, Linux, MacOs, Esp32, FreeRtos };

        public static IReadOnlyList<TargetProfile> All => BuiltIn;

        /// <summary>The target the current process runs on (what the VM in the editor is).</summary>
        public static TargetProfile Host =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? Windows
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? MacOs
            : Linux;

        public static bool TryGet(string name, out TargetProfile profile)
        {
            var found = BuiltIn.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            profile = found!;
            return found != null;
        }
    }
}

namespace fire.Runtime
{
    /// <summary>How the program starts: a process (`int main()`) or a function that the board's startup code calls (`app_main` in ESP-IDF).</summary>
    public enum EntryKind { Process, Function }

    public sealed record EntryPoint
    {
        public string Name { get; init; } = "main";
        public EntryKind Kind { get; init; } = EntryKind.Process;
        /// <summary>`extern "C"` (the board's startup code is C).</summary>
        public bool ExternC { get; init; }
    }

    /// <summary>The native-build part of a target (see <see cref="TargetProfile.Native"/>): everything platform specific is in the platform package
    /// (native/platform/&lt;Platform&gt;/); what is left for the generated file is to name what gets included and how the program starts.</summary>
    public sealed record NativeTarget
    {
        /// <summary>The platform package: the folder native/platform/&lt;name&gt; of the runtime (`posix`, `windows`, `freertos`, `esp32` or one of your own).</summary>
        public string Platform { get; init; } = "posix";

        /// <summary>Headers included before the platform header (e.g. the FreeRTOS headers); `name.h` becomes `#include &lt;name.h&gt;`.</summary>
        public IReadOnlyList<string> Includes { get; init; } = Array.Empty<string>();

        /// <summary>A folder with your own platform package (it holds <c>fire_platform.hpp</c>); it is copied to <c>platform/&lt;Platform&gt;/</c> next to the generated file.</summary>
        public string? PlatformPath { get; init; }

        /// <summary>Preprocessor definitions before the includes: `NAME` or `NAME=VALUE`.</summary>
        public IReadOnlyList<string> Defines { get; init; } = Array.Empty<string>();

        public EntryPoint Entry { get; init; } = new();

        /// <summary>Arguments the compiler always needs for this platform (`-pthread`).</summary>
        public IReadOnlyList<string> CompileArgs { get; init; } = Array.Empty<string>();

        /// <summary>Libraries to link (`-lm`).</summary>
        public IReadOnlyList<string> LinkLibs { get; init; } = Array.Empty<string>();

        /// <summary>The platform package implements fire threads.</summary>
        public bool SupportsThreads { get; init; } = true;

        /// <summary>The name of the toolchain that builds for this target (a key of the configuration's `toolchains`, or `files`: only write the sources);
        /// null: the toolchain of the machine.</summary>
        public string? Toolchain { get; init; }
    }
}
