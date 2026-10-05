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

        /// <summary>Ausführungsmodus, mit dem das Programm läuft (`#debug`/`#performance` im Skript oder `-m` der
        /// Befehlszeile); die gepackte Runtime übernimmt ihn von hier.</summary>
        public VmExecutionMode ExecutionMode { get; init; }

        /// <summary>Names of the native functions in registration order (the index used by `CallNative`). Only known while linking
        /// (not serialized): code generators (fire.Native) need it to map an index back to the function.</summary>
        [MemoryPackIgnore]
        public IReadOnlyList<string>? NativeNames { get; init; }

        public LinkedProgram(CompiledProgram program, HashSet<string> nativeImports, int firstUserSource, VmExecutionMode executionMode = VmExecutionMode.Release)
        {
            Program = program;
            NativeImports = nativeImports;
            FirstUserSource = firstUserSource;
            ExecutionMode = executionMode;
        }
    }
}
