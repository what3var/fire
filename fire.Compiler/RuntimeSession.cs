using fire.Bytecode;
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
using System.Text;
using System.Threading.Tasks;

namespace fire.Compiler
{
    public class RuntimeSession
    {
        public VM VirtualMachine { get; private set; }

        public CompiledProgram CompiledProgram { get; private set; }

        public Scope GlobalScope { get; private set; }


        public WindowManager? WindowManager { get; private set; }

        public FramebufferManager? FramebufferManager { get; private set; }

        public ConsoleManager? ConsoleManager { get; private set; }

        
        private RuntimeSession(VM virtualMachine, CompiledProgram compiledProgram, Scope globalScope, WindowManager? windowManager, FramebufferManager? framebufferManager, ConsoleManager? consoleManager)
        {
            VirtualMachine = virtualMachine;
            CompiledProgram = compiledProgram;
            GlobalScope = globalScope;
            WindowManager = windowManager;
            FramebufferManager = framebufferManager;
            ConsoleManager = consoleManager;
            // Private constructor to prevent direct instantiation
        }

        public static RuntimeSession Build(IReadOnlyList<string> sources, VmExecutionMode executionMode, Func<Value[], Value>? debugWriter = null)
        {
            var linker = new Linker();
            var natives = new NativeRegistry();

            var linkedProgram = linker.CompileAndLink(sources, debugWriter);

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

            if (linkedProgram.NativeImports.Contains(NativeImports.Graphics))
            {
                var font = new IntegratedGlyphFont();
                fbManager = new FramebufferManager();
                consoleManager = new ConsoleManager(fbManager, font);
                windowManager = new WindowManager(fbManager);

                GraphicsBridge.RegisterAll(natives, fbManager, consoleManager, windowManager);
            }

            var globalScope = new Scope(null, isGlobal: true);
            var mainVm = new VM(linkedProgram.Program.TopLevel, globalScope, natives, linkedProgram.Program.Classes,
                externSignatures: linkedProgram.Program.ExternSignatures, isMainThreadVm: true, executionMode: executionMode);

            return new RuntimeSession(mainVm, linkedProgram.Program, globalScope, windowManager, fbManager, consoleManager);
        }
    }
}
