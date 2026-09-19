using fire.Bytecode;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace fire.Runtime
{
    public class Runtime
    {
        private VmExecutionMode _executionMode;

        public Runtime(VmExecutionMode executionMode = VmExecutionMode.Release)
        {
            _executionMode = executionMode;
        }
    }
}
