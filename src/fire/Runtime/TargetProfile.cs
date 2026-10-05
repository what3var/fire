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
        };

        public static readonly TargetProfile Linux = new()
        {
            Name = "linux", Symbols = new[] { "linux", "posix" }, HalPackage = "posix",
        };

        public static readonly TargetProfile MacOs = new()
        {
            Name = "macos", Symbols = new[] { "macos", "posix" }, HalPackage = "posix",
        };

        /// <summary>ESP32 with FreeRTOS (ESP-IDF): single-precision FPU, so `float` is 32 bits; small thread stacks; no windows or
        /// framebuffer (`graphics`, `ui`).</summary>
        public static readonly TargetProfile Esp32 = new()
        {
            Name = "esp32", Symbols = new[] { "esp32", "freertos" }, HalPackage = "esp32",
            FloatWidth = 32, DefaultStackBytes = 4096, IsEmbedded = true,
            Imports = new[] { NativeImports.Print, NativeImports.IO, NativeImports.Devices, NativeImports.Time, NativeImports.Reflection, NativeImports.Linq },
        };

        private static readonly TargetProfile[] BuiltIn = { Windows, Linux, MacOs, Esp32 };

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
