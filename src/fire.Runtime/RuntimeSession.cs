using fire.Bytecode;
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
            var natives = new NativeRegistry();
            var alreadyIncluded = new HashSet<string>();

            if (debugWriter == null)
                natives.Register("print", args => Value.MakeUndefined());
            else
                natives.Register("print", args => debugWriter(args));

            var processedSources = new List<string>();
            var inputSources = new List<string>() { fire.Standard.Prelude.Source };

            inputSources.AddRange(sources);

            bool importsGraphicsBridge = false;

            var registry = new DirectiveRegistry(); // komplett leer, NICHT CreateDefault()
            registry.Register("import", 1, (ctx, args, line) =>
            {
                if (args[0].Kind == ValueKind.String)
                {
                    switch (args[0].AsString().ToLower())
                    {
                        case "graphics":
                            importsGraphicsBridge = true;
                            return null;
                        default:
                            throw new Exception($"'{args[0].AsString()}' ist keine bekannte Erweiterung.");
                    }
                }
                throw new Exception($"Falsche Argumente für 'import'-Direktive.");
            });

            foreach (var source in inputSources)
            {
                string preprocessed = Preprocessor.Process(source, Directory.GetCurrentDirectory(), alreadyIncluded, registry);
                processedSources.Add(preprocessed);
            }

            FramebufferManager? fbManager = null;
            ConsoleManager? consoleManager = null;
            WindowManager? windowManager = null;

            if (importsGraphicsBridge)
            {
                string preprocessed = Preprocessor.Process(GraphicsBridge.PreludeSource, Directory.GetCurrentDirectory(), alreadyIncluded, registry);
                processedSources.Insert(1, preprocessed);

                var font = new IntegratedGlyphFont();
                fbManager = new FramebufferManager();
                consoleManager = new ConsoleManager(fbManager, font);
                windowManager = new WindowManager(fbManager);

                GraphicsBridge.RegisterAll(natives, fbManager, consoleManager, windowManager);
            }

            var program = Parser.ParseMultiple(processedSources, Directory.GetCurrentDirectory(), alreadyPreprocessed: true, out var activeUsings, out var usingsByStmt);
            var resolveResult = Resolver.Resolve(program, natives.Names, null, usingsByStmt: usingsByStmt);
            var compiled = Compiler.Compile(program, resolveResult, natives, null, usingsByStmt);

            var globalScope = new Scope(null, isGlobal: true);
            var mainVm = new VM(compiled.TopLevel, globalScope, natives, compiled.Classes,
                externSignatures: compiled.ExternSignatures, isMainThreadVm: true, executionMode: executionMode);

            return new RuntimeSession(mainVm, compiled, globalScope, windowManager, fbManager, consoleManager);
        }
    }
}
