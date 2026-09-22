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
            try
            {

                var bin = MemoryPack.MemoryPackSerializer.Serialize(program);

                Debug.WriteLine($"Serialized {bin.Length} bytes.");

            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }
    }
}
