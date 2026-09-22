using fire.Compiler;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace fire.Compiler
{
    public static class Packer
    {
        public static void PackProgram(LinkedProgram program)
        {
            var bin = MessagePack.MessagePackSerializer.Serialize(program);

            Debug.WriteLine($"Serialized {bin.Length} bytes.");
        }
    }
}
