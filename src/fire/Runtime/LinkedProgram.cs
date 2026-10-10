using fire.Bytecode;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MemoryPack;

namespace fire.Runtime
{
    [MemoryPackable]
    public partial class LinkedProgram
    {
        public CompiledProgram Program { get; init; }

        public HashSet<string> NativeImports { get; init; }

        public int FirstUserSource { get; init; }

        /// <summary>The natives of the imports of packages (C++ for the native backend) in registration order: they come after the natives of the compiler's own imports. A run in the virtual
        /// machine registers them as functions that fail with a clear message, so that the indexes of the calls stay right.</summary>
        public List<string>? PackageNatives { get; init; }

        /// <summary>For each of <see cref="PackageNatives"/> the file name of the shared library (C ABI, native/abi/fire_pkg_abi.h) that holds it; the virtual machine loads them.</summary>
        public List<string>? PackageNativeLibraries { get; init; }

        /// <summary>The full paths of those libraries on the machine that linked the program (not serialized): a packed program carries them in its payload.</summary>
        [MemoryPackIgnore]
        public List<string>? PackageLibraryFiles { get; init; }

        /// <summary>The program asks for the GUI subsystem (`#noconsole`, the subsystem of the project): no console window on Windows. Only known while linking (not serialized): the packer and the
        /// native build apply it to the executable they make.</summary>
        [MemoryPackIgnore]
        public bool GuiSubsystem { get; init; }

        /// <summary>Version info (company, product, file version, texts) and icon of the executable that the program asks for (`#company`, `#version`, `#icon`, the project's settings). Only known
        /// while linking a native build (not serialized): the native build writes them into the executable it makes, the packer reads them from the assembly info itself.</summary>
        [MemoryPackIgnore]
        public fire.Utilities.PeVersionInfo? VersionInfo { get; init; }

        [MemoryPackIgnore]
        public string? IconPath { get; init; }

        /// <summary>Execution mode the program runs with (`#debug`/`#performance` in the script or `-m` on the
        /// command line); the packed runtime takes it over from here.</summary>
        public VmExecutionMode ExecutionMode { get; init; }

        /// <summary>Names of the native functions in registration order (the index used by `CallNative`). Only known while linking
        /// (not serialized): code generators (fire.Native) need it to map an index back to the function.</summary>
        [MemoryPackIgnore]
        public IReadOnlyList<string>? NativeNames { get; init; }

        /// <summary>Precision of `float`: 64 (double, the default) or 32 (single, `#floatwidth 32`, the default for small targets such as
        /// the ESP32). The VM and the native backend compute with the same precision (SPEC 8.2.1).</summary>
        public int FloatWidth { get; init; } = 64;

        /// <summary>The file each source of the program comes from, by source index (the index of `Chunk.MarkLine`, of a breakpoint): null for the prelude and the preludes of imports and for
        /// a source that is no file. Only known while linking (not serialized): the debugger maps a file to its source index with it.</summary>
        [MemoryPackIgnore]
        public IReadOnlyList<string?>? SourceFiles { get; init; }

        public LinkedProgram(CompiledProgram program, HashSet<string> nativeImports, int firstUserSource, VmExecutionMode executionMode = VmExecutionMode.Release)
        {
            Program = program;
            NativeImports = nativeImports;
            FirstUserSource = firstUserSource;
            ExecutionMode = executionMode;
        }
    }
}
