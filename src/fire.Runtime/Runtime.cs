using fire.Bytecode;
using fire.Values;
using System;
using System.Runtime.CompilerServices;

namespace fire.Runtime
{
    /// <summary>Entry of the packed file. The method `Main` (Program.cs) must not mention any type from fire.dll - that
    /// only comes from the payload of its own file once the <see cref="PayloadLoader"/> is installed. Everything that
    /// fire.dll needs therefore lies behind this [NoInlining] boundary.</summary>
    public static class Bootstrap
    {
        /// <summary>Installs the loader and runs the program from its own file. Return value: process exit code.</summary>
        public static int Start()
        {
            if (!PayloadLoader.Install())
            {
                Console.Error.WriteLine("This file does not contain a fire program (payload missing). Fire programs are produced with the compiler.");
                return 1;
            }
            return Run();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Run()
        {
            new Runtime().ExecuteInternal();
            return 0;
        }
    }

    public class Runtime
    {
        public void ExecuteInternal()
        {
            var bin = PayloadLoader.ReadProgram();
            var prog = bin == null ? null : Packer.Deserialize(bin);

            if (prog == null)
            {
                Console.Error.WriteLine("The program in this file is corrupt or unreadable.");
                return;
            }

            var session = Session.Build(prog, prog.ExecutionMode, args =>
            {
                if (args.Length > 0)
                {
                    Console.WriteLine(args[0].ToString());
                }
                return Value.MakeUndefined();
            });

            session.Run();
        }
    }
}
