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
using System.Numerics;
using System.Reflection.Metadata;
using System.Text;
using System.Threading.Tasks;
using static fire.Resolving.ResolvedRef;

namespace fire.Compiler
{
    public class RuntimeSession
    {
        public VM? VirtualMachine { get; private set; }

        public CompiledProgram CompiledProgram { get; private set; }

        public Scope? GlobalScope { get; private set; }


        public WindowManager? WindowManager { get; private set; }

        public FramebufferManager? FramebufferManager { get; private set; }

        public ConsoleManager? ConsoleManager { get; private set; }

        public int FirstUserSourceIndex { get; private set; }




        protected NativeRegistry? nativeRegistry { get; set; }

        
        private RuntimeSession(CompiledProgram compiledProgram)
        {
            CompiledProgram = compiledProgram;
            // Private constructor to prevent direct instantiation
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

        public void CallLambda(LambdaValue lambda, Value[] args)
        {
            var snapshot = VirtualMachine.SnapshotGlobals();

            FireRuntime.CallCallback(lambda, args, nativeRegistry, CompiledProgram.Classes, snapshot,
                ex => Console.WriteLine($"(unbehandelte Exception im Callback: {ex.Message})"));
        }

        /// <summary>Baut die DirectiveRegistry, die reale Programme (siehe
        /// Build) UND die Live-Diagnostik (siehe Editor.LiveDiagnostics)
        /// gleichermaßen nutzen - genau EINE Stelle, die weiß, welche
        /// benutzerdefinierten Präprozessor-Direktiven es gibt (aktuell:
        /// `#import "extension"`), damit beide IMMER im Gleichschritt
        /// bleiben. Ohne das würde eine hier erkannte Direktive in der
        /// Live-Diagnostik weiterhin fälschlich als "unbekannte Direktive"
        /// unterkringelt, obwohl sie beim echten Kompilieren längst
        /// akzeptiert wird (der Preprocessor lässt jede NICHT hier
        /// registrierte Direktive unverändert im Text stehen, der Parser
        /// kennt sie dann seinerseits nicht und wirft, siehe Parsing.
        /// Preprocessor-Klassendoku/Parser.ParseDirective).
        ///
        /// `onImport`: Callback für eine ERKANNTE `#import "name"`-
        /// Direktive (z.B. um die Grafik-Bridge tatsächlich zu laden, siehe
        /// Build) - `null`, wenn der Aufrufer nur wissen will "ist das
        /// syntaktisch eine gültige Direktive", ohne ihre eigentliche
        /// Wirkung auszulösen (siehe LiveDiagnostics: dort reicht "wird
        /// nicht als Fehler markiert", ohne dass tatsächlich irgendetwas
        /// geladen werden müsste). Eine UNBEKANNTE Erweiterung (`#import
        /// "unfug"`) wirft weiterhin - das ist ein ECHTER Fehler, kein
        /// reines "kennt die Live-Diagnostik das nur (noch) nicht".</summary>
        public static DirectiveRegistry CreateProjectDirectiveRegistry(Action<string>? onImport = null)
        {
            var registry = new DirectiveRegistry(); // komplett leer, NICHT CreateDefault()
            registry.Register("import", 1, (ctx, args, line) =>
            {
                if (args[0].Kind == ValueKind.String)
                {
                    string name = args[0].AsString().ToLower();
                    switch (name)
                    {
                        case "graphics":
                            onImport?.Invoke(name);
                            return null;
                        default:
                            throw new Exception($"'{args[0].AsString()}' ist keine bekannte Erweiterung.");
                    }
                }
                throw new Exception($"Falsche Argumente für 'import'-Direktive.");
            });
            return registry;
        }

        public static RuntimeSession Build(IReadOnlyList<string> sources, VmExecutionMode executionMode, Func<Value[], Value>? debugWriter = null, string? outname = null)
        {
            var linker = new Linker();
            var natives = new NativeRegistry();

            var linkedProgram = linker.CompileAndLink(sources, debugWriter, outname);

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

            var session = new RuntimeSession(linkedProgram.Program);
            
            if (linkedProgram.NativeImports.Contains(NativeImports.Graphics))
            {
                var font = new IntegratedGlyphFont();
                fbManager = new FramebufferManager();
                consoleManager = new ConsoleManager(fbManager, font);
                windowManager = new WindowManager(fbManager, (l,v) => session.CallLambda(l,v));

                GraphicsBridge.RegisterAll(natives, fbManager, consoleManager, windowManager);
            }

            var globalScope = new Scope(null, isGlobal: true);
            
            var mainVm = new VM(linkedProgram.Program.TopLevel, globalScope, natives, linkedProgram.Program.Classes,
                externSignatures: linkedProgram.Program.ExternSignatures, isMainThreadVm: true, executionMode: executionMode);

            session.SetVM(mainVm, windowManager, globalScope, natives, fbManager, consoleManager, linkedProgram.FirstUserSource);

            return session;
        }
    }
}
