using System.Runtime.InteropServices;
using System;
using System.Linq;

namespace fire.Runtime
{
    /// <summary>
    /// Describes a target a fire program is built for: the operating system or board, the defaults that depend on it
    /// (precision of `float`, stack size of threads), which `#import` libraries exist there, and which platform package
    /// of the C++ runtime (HAL, see docs/spec/NATIVE_BACKEND.md) implements them.
    ///
    /// One description is shared by the linker (validates the imports and picks the default float precision), the native
    /// backend (entry point, defines, platform package) and - later - the preprocessor, whose `#if` symbols are
    /// <see cref="Symbols"/>. The VM that runs a script in the editor uses <see cref="Host"/>.
    /// </summary>
    public sealed partial record TargetProfile
    {
        /// <summary>What the native backend needs to build for this target: which platform package of the C++ runtime (native/platform/&lt;name&gt;/), what to
        /// include before it, the entry point, extra compiler arguments. A target configuration file (fire.native.json) can change all of it.</summary>
        public NativeTarget Native { get; init; } = new();

        /// <summary>Name on the command line (`--target esp32`).</summary>
        public required string Name { get; init; }

        /// <summary>The name as a part of a C macro (`FIRE_TARGET_&lt;it&gt;`): upper case, every character that is no letter or digit an underscore
        /// (`bios16-dos`: BIOS16_DOS).</summary>
        public string MacroName => new string(Name.ToUpperInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());

        /// <summary>Symbols for conditional compilation (`#if windows`): the operating system or board.</summary>
        public required IReadOnlyList<string> Symbols { get; init; }

        /// <summary>Default precision of `float` in bits (32 or 64); `#floatwidth` in the program and `-f` override it.</summary>
        public int FloatWidth { get; init; } = 64;

        /// <summary>Default width of `int` in bits (32 or 64; 64 on every built-in target); `#intwidth` in the program and `-i` override it.</summary>
        public int IntWidth { get; init; } = 64;

        /// <summary>Width of `word` in bits (SPEC 8.10): the target's machine word, unsigned - 16 (AVR, DOS real mode, Win16), 32 (ESP32, Cortex-M, wasm, DPMI,
        /// Win9x), 64 (x86-64, ARM64: the desktop targets). `wordWidth` in a target of fire.native.json changes it; `#if word16|word32|word64` sees it.</summary>
        public int WordWidth { get; init; } = 64;

        /// <summary>The direction a native build for the target is optimised in when nothing else says (OptimizeFor: `"optimize"` of a target in fire.native.json).</summary>
        public OptimizeFor Optimize { get; init; } = OptimizeFor.Speed;

        /// <summary>Default stack size of a fire thread in bytes (used when `#stacksize` is not given and cannot be derived).</summary>
        public int DefaultStackBytes { get; init; } = 64 * 1024;

        /// <summary>The `#import` libraries that exist on this target; null means all of them.</summary>
        public IReadOnlyList<string>? Imports { get; init; }

        /// <summary>Platform package of the C++ runtime that implements the HAL (`windows`, `posix`, `esp32`).</summary>
        public required string HalPackage { get; init; }

        /// <summary>A small target without an operating system shell: no console program, the entry point is the board's.</summary>
        public bool IsEmbedded { get; init; }

        /// <summary>Where the target stands in the choice of targets (the tree of the build tool strip): a system with an operating system, a PC without one,
        /// a board.</summary>
        public TargetCategory Category { get; init; } = TargetCategory.Os;

        /// <summary>The extension of the program file a native build makes (`.exe` for Windows, Windows 3.x and DOS, `.html` for the browser; empty: none).</summary>
        public string ProgramExtension { get; init; } = "";

        /// <summary>The name shown in the choice of targets ("BIOS16 (+IO)"); null: <see cref="Name"/>.</summary>
        public string? DisplayName { get; init; }

        /// <summary>The name to show: <see cref="DisplayName"/>, else <see cref="Name"/>.</summary>
        public string Title => DisplayName ?? Name;

        /// <summary>The kinds of toolchain that can build for this target (`watcom` for DOS and Windows 3.x); null: any. The toolchain `files` (the sources only)
        /// always can. A toolchain chosen for the editor or the machine that cannot is not used: the target's own is (NativeConfig.ResolveToolchain).</summary>
        public IReadOnlyList<string>? Toolchains { get; init; }

        /// <summary>Can a toolchain of this kind build for the target?</summary>
        public bool AllowsToolchain(string kind) =>
            Toolchains == null || string.Equals(kind, "files", StringComparison.OrdinalIgnoreCase) || Toolchains.Contains(kind, StringComparer.OrdinalIgnoreCase);

        public bool HasImport(string import) => Imports == null || Imports.Contains(import) || import.StartsWith("pkg:", StringComparison.Ordinal);   // imports of packages: their native part names the platforms it supports

        // -------------------------------------------------------------------------------------------------------------
        public static readonly TargetProfile Windows = new()
        {
            Name = "windows", Symbols = new[] { "windows" }, HalPackage = "windows", ProgramExtension = ".exe",
            DisplayName = "Windows", Category = TargetCategory.Os,
            Native = new NativeTarget { Platform = "windows" },
        };

        public static readonly TargetProfile Linux = new()
        {
            Name = "linux", Symbols = new[] { "linux", "posix" }, HalPackage = "posix",
            DisplayName = "Linux", Category = TargetCategory.Os,
            Native = new NativeTarget { Platform = "posix", CompileArgs = new[] { "-pthread" } },
        };

        public static readonly TargetProfile MacOs = new()
        {
            Name = "macos", Symbols = new[] { "macos", "posix" }, HalPackage = "posix",
            DisplayName = "macOS", Category = TargetCategory.Os,
            Native = new NativeTarget { Platform = "posix", CompileArgs = new[] { "-pthread" } },
        };

        /// <summary>ESP32 with FreeRTOS (ESP-IDF): single-precision FPU, so `float` is 32 bits; small thread stacks; no windows or
        /// framebuffer (`graphics`, `ui`).</summary>
        public static readonly TargetProfile Esp32 = new()
        {
            Name = "esp32", Symbols = new[] { "esp32", "freertos" }, HalPackage = "esp32",
            DisplayName = "ESP32", Category = TargetCategory.Embedded, Toolchains = new[] { "esp-idf" },
            FloatWidth = 32, WordWidth = 32, DefaultStackBytes = 8192, IsEmbedded = true,
            Imports = new[] { NativeImports.Print, "io", "filesystem", NativeImports.Devices, "time", NativeImports.Reflection, NativeImports.Linq },
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
            DisplayName = "FreeRTOS (sources)", Category = TargetCategory.Embedded,
            FloatWidth = 32, WordWidth = 32, DefaultStackBytes = 8192, IsEmbedded = true,
            Imports = new[] { NativeImports.Print, "io", "filesystem", NativeImports.Devices, "time", NativeImports.Reflection, NativeImports.Linq },
            Native = new NativeTarget
            {
                Platform = "freertos",
                Includes = new[] { "FreeRTOS.h", "task.h", "semphr.h" },
                Entry = new EntryPoint { Name = "fire_start", Kind = EntryKind.Function, ExternC = true },
                Toolchain = "files",
            },
        };

        /// <summary>A program in the browser (WebAssembly, built with Emscripten): one thread (fire threads are tasks of fire's own scheduler on the fibers of Emscripten), `Sleep` hands the thread back to the page, the window is the canvas of the page (fire's
        /// own display), the files are in memory, there are no sockets. The result is a single `.html` file that opens as it is.</summary>
        public static readonly TargetProfile Wasm = new()
        {
            Name = "wasm", Symbols = new[] { "wasm", "browser" }, HalPackage = "wasm", ProgramExtension = ".html", WordWidth = 32,
            DisplayName = "WebAssembly (browser)", Category = TargetCategory.Os, Toolchains = new[] { "emscripten" },
            Native = new NativeTarget { Platform = "wasm", Toolchain = "emscripten" },
        };

        /// <summary>The built-in targets: the desktop and the boards here, the old PCs (DOS, BIOS, UEFI, old Windows) in TargetProfile.Pc.cs - a family of targets
        /// is a file of its own that adds its list.</summary>
        /// (Built at the first use: the static fields of the partial files are all set by then - their order between files is not fixed.)
        private static TargetProfile[] BuiltIn => s_builtIn ??= new[] { Windows, Linux, MacOs, Esp32, FreeRtos, Wasm }.Concat(PcTargets()).ToArray();
        private static TargetProfile[]? s_builtIn;

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

        /// <summary>`#line` directives in the generated C (fire file and line of every instruction): gdb/lldb, the messages of the C compiler and crash reports name
        /// fire lines. `"lineDirectives": false` in the target of fire.native.json switches them off (the C file is then read as C).</summary>
        public bool LineDirectives { get; init; } = true;

        /// <summary>The name of the toolchain that builds for this target (a key of the configuration's `toolchains`, or `files`: only write the sources);
        /// null: the toolchain of the machine.</summary>
        public string? Toolchain { get; init; }

        /// <summary>A board: the serial port it is on (spark flashes it and talks to its console there); null: found by itself (see Esp32Launch.ResolvePort).</summary>
        public string? Port { get; init; }

        /// <summary>A board: the baud rate of its console (null: 115200).</summary>
        public int? Baud { get; init; }
    }

    /// <summary>The groups of the choice of targets.</summary>
    public enum TargetCategory
    {
        /// <summary>A system with an operating system: Windows, Linux, macOS, the browser, DOS, Windows 3.x.</summary>
        Os,
        /// <summary>A PC without an operating system, started by its firmware (BIOS, UEFI).</summary>
        Native,
        /// <summary>A board: a microcontroller, with or without an RTOS.</summary>
        Embedded,
    }
}
