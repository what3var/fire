using fire.Bytecode;
using fire.Device.Bridge;
using fire.Device.Manager.DeviceManager;
using fire.Parsing;
using fire.Resolving;
using fire.Runtime;
using fire.Terminal;
using fire.Terminal.Bridge;
using fire.Terminal.Windows;
using fire.Values;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection.Metadata;
using System.Text;
using System.Threading.Tasks;
using static fire.Resolving.ResolvedRef;

namespace fire.Runtime
{
    public class Session
    {
        public VM? VirtualMachine { get; private set; }

        public CompiledProgram CompiledProgram { get; private set; }

        public Scope? GlobalScope { get; private set; }


        public WindowManager? WindowManager { get; private set; }

        public FramebufferManager? FramebufferManager { get; private set; }

        public ConsoleManager? ConsoleManager { get; private set; }

        public int FirstUserSourceIndex { get; private set; }




        protected NativeRegistry? nativeRegistry { get; set; }

        
        private Session(CompiledProgram compiledProgram)
        {
            CompiledProgram = compiledProgram;
            // Private constructor to prevent direct instantiation
        }

        public void Run()
        {
            if (VirtualMachine != null)
                VirtualMachine.Run();
        }

        protected void SetVM(VM virtualMachine, WindowManager? wm, Scope globalScope, NativeRegistry natives, FramebufferManager? framebufferManager, ConsoleManager? consoleManager, int firstUserSourceIndex)
        {
            VirtualMachine = virtualMachine;
            WindowManager = wm;
            GlobalScope = globalScope;
            FramebufferManager = framebufferManager;
            ConsoleManager = consoleManager;
            nativeRegistry = natives;
            FirstUserSourceIndex = firstUserSourceIndex;
        }

        protected void CallLambda(LambdaValue lambda, Value[] args)
        {
            if (VirtualMachine == null || nativeRegistry == null)
                return;
            var snapshot = VirtualMachine.SnapshotGlobals();

            FireRuntime.CallCallback(lambda, args, nativeRegistry, CompiledProgram.Classes, snapshot,
                ex => Console.WriteLine($"(unbehandelte Exception im Callback: {ex.Message})"));
        }

        public static Session Build(LinkedProgram linkedProgram, VmExecutionMode executionMode, Func<Value[], Value>? debugWriter = null)
        {
            var natives = new NativeRegistry();
            
            if (linkedProgram.NativeImports.Contains(NativeImports.Print))
            {
                if (debugWriter == null)
                    natives.Register("print", args => Value.MakeUndefined());
                else
                    natives.Register("print", args => debugWriter(args));
            }

            FramebufferManager? fbManager = null;
            ConsoleManager? consoleManager = null;
            WindowManager? windowManager = null;

            DeviceManager? deviceManager = null;

            var session = new Session(linkedProgram.Program);

            if (linkedProgram.NativeImports.Contains(NativeImports.Graphics))
            {
                var font = new IntegratedGlyphFont();
                fbManager = new FramebufferManager();
                consoleManager = new ConsoleManager(fbManager, font);
                windowManager = new WindowManager(fbManager, (l, v) => session.CallLambda(l, v));

                GraphicsBridge.RegisterAll(natives, fbManager, consoleManager, windowManager);
            }

            if (linkedProgram.NativeImports.Contains(NativeImports.Devices))
            {
                deviceManager = new DeviceManager();

                DeviceBridge.RegisterAll(natives, deviceManager);
            }

            var globalScope = new Scope(null, isGlobal: true);
            
            var mainVm = new VM(linkedProgram.Program.TopLevel, globalScope, natives, linkedProgram.Program.Classes,
                externSignatures: linkedProgram.Program.ExternSignatures, isMainThreadVm: true, executionMode: executionMode);

            session.SetVM(mainVm, windowManager, globalScope, natives, fbManager, consoleManager, linkedProgram.FirstUserSource);

            return session;
        }
    }
}
