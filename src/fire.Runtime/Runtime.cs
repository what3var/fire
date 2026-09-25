using fire.Bytecode;
using fire.Lexing;
using fire.Parsing;
using fire.Resolving;
using fire.Terminal;
using fire.Terminal.Bridge;
using fire.Terminal.Windows;
using fire.Values;
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

        public void ExecuteInternal()
        {
            var prog = Packer.UnpackProgram(Path.Combine(Path.GetDirectoryName(Environment.ProcessPath), "tempout.exe"));

            if (prog == null)
                return;

            var session = Session.Build(prog, _executionMode, args =>
            {
                if (args.Length > 0)
                {
                    Console.WriteLine(args[0].AsString());
                }
                return Value.MakeUndefined();
            });

            if (session == null)
                return;

            session.Run();
        }
    }
}
