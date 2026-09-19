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

        public void Execute(string[] sourceCodes)
        {
            var processedSources = new List<string>();
            var inputSources = new List<string>() { fire.Standard.Prelude.Source };
            
            inputSources.AddRange(sourceCodes);

            bool importsGraphicsBridge = false;

            var registry = new DirectiveRegistry(); // komplett leer, NICHT CreateDefault()
            registry.Register("import", 1, (ctx, args, line) =>
            {
                if (args[0].Kind == ValueKind.String)
                {
                    switch(args[0].AsString().ToLower())
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
                string preprocessed = Preprocessor.Process(source, Directory.GetCurrentDirectory(), registry);
                processedSources.Add(preprocessed);
            }

            var natives = NativeRegistry.CreateDefault();

            if (importsGraphicsBridge)
            {
                string preprocessed = Preprocessor.Process(GraphicsBridge.PreludeSource, Directory.GetCurrentDirectory(), registry);
                processedSources.Insert(1, preprocessed);

                using var font = new GdiGlyphFont();
                var fbManager = new FramebufferManager();
                var consoleManager = new ConsoleManager(fbManager, font);
                var windowManager = new WindowManager(fbManager);

                GraphicsBridge.RegisterAll(natives, fbManager, consoleManager, windowManager);
            }

            var listOfSources = new List<string> { fire.Standard.Prelude.Source };

            var program = Parser.ParseMultiple(listOfSources);
            
            var resolveResult = Resolver.Resolve(program, natives.Names);
            var compiled = Compiler.Compile(program, resolveResult, natives);

            var globalScope = new Scope(null, isGlobal: true);
            var vm = new VM(compiled.TopLevel, globalScope, natives, compiled.Classes, executionMode: _executionMode);
            
            vm.Run();
        }
    }
}
