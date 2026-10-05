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

        /// <summary>Ausführungsmodus, mit dem das Programm läuft (`#debug`/`#performance` im Skript oder `-m` der
        /// Befehlszeile); die gepackte Runtime übernimmt ihn von hier.</summary>
        public VmExecutionMode ExecutionMode { get; init; }

        /// <summary>Names of the native functions in registration order (the index used by `CallNative`). Only known while linking
        /// (not serialized): code generators (fire.Native) need it to map an index back to the function.</summary>
        [MemoryPackIgnore]
        public IReadOnlyList<string>? NativeNames { get; init; }

        /// <summary>Precision of `float`: 64 (double, the default) or 32 (single, `#floatwidth 32`, the default for small targets such as
        /// the ESP32). The VM and the native backend compute with the same precision (SPEC 8.2.1).</summary>
        public int FloatWidth { get; init; } = 64;

        public LinkedProgram(CompiledProgram program, HashSet<string> nativeImports, int firstUserSource, VmExecutionMode executionMode = VmExecutionMode.Release)
        {
            Program = program;
            NativeImports = nativeImports;
            FirstUserSource = firstUserSource;
            ExecutionMode = executionMode;
        }
    }
}
