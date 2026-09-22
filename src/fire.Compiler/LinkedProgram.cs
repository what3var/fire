using fire.Bytecode;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace fire.Compiler
{
    public class LinkedProgram
    {
        public CompiledProgram Program { get; init; }

        public HashSet<string> NativeImports { get; init; }

        public int FirstUserSource { get; init; }
    
        public LinkedProgram(CompiledProgram program, HashSet<string> nativeImports, int firstUserSource)
        {
            Program = program;
            NativeImports = nativeImports;
            FirstUserSource = firstUserSource;
        }
    }
}
