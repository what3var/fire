using fire.Bytecode;
using fire.Values;
using System;
using System.Runtime.CompilerServices;

namespace fire.Runtime
{
    /// <summary>Einstieg der gepackten Datei. Die Methode `Main` (Program.cs) darf keinen Typ aus fire.dll erwähnen - die
    /// kommt erst aus dem Payload der eigenen Datei, sobald der <see cref="PayloadLoader"/> installiert ist. Alles, was
    /// fire.dll braucht, steht deshalb hinter dieser [NoInlining]-Grenze.</summary>
    public static class Bootstrap
    {
        /// <summary>Installiert den Lader und führt das Programm aus der eigenen Datei aus. Rückgabe: Prozess-Exitcode.</summary>
        public static int Start()
        {
            if (!PayloadLoader.Install())
            {
                Console.Error.WriteLine("Diese Datei enthält kein Fire-Programm (Payload fehlt). Fire-Programme werden mit dem Compiler erzeugt.");
                return 1;
            }
            return Run();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Run()
        {
            new Runtime(VmExecutionMode.Release).ExecuteInternal();
            return 0;
        }
    }

    public class Runtime
    {
        private VmExecutionMode _executionMode;

        public Runtime(VmExecutionMode executionMode = VmExecutionMode.Release)
        {
            _executionMode = executionMode;
        }

        public void ExecuteInternal()
        {
            var bin = PayloadLoader.ReadProgram();
            var prog = bin == null ? null : Packer.Deserialize(bin);

            if (prog == null)
            {
                Console.Error.WriteLine("Das Programm in dieser Datei ist beschädigt oder unlesbar.");
                return;
            }

            var session = Session.Build(prog, _executionMode, args =>
            {
                if (args.Length > 0)
                {
                    Console.WriteLine(args[0].AsString());
                }
                return Value.MakeUndefined();
            });

            session.Run();
        }
    }
}
