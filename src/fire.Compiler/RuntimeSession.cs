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

        public RendererManager? RendererManager { get; private set; }

        public int FirstUserSourceIndex { get; private set; }




        protected NativeRegistry? nativeRegistry { get; set; }

        /// <summary>Policy und Konsole des Hosts für die Natives von Paketen (siehe PackageHost): schließt am Ende alle Streams, die ein Skript offen
        /// gelassen hat. Wer die VM selbst treibt (z.B. der Step-Debugger), ruft das nach dem Lauf auf.</summary>
        protected IDisposable? IoResources { get; set; }

        /// <summary>Räumt die Geräte-Brücke nach dem Lauf auf: löst die Empfangs-Haken und gibt einen NICHT geteilten
        /// Manager frei (siehe DeviceBridge.RegisterAll).</summary>
        protected IDisposable? DeviceResources { get; set; }

        public void CloseHostResources()
        {
            IoResources?.Dispose();
            DeviceResources?.Dispose();
        }

        /// <summary>Führt das Programm auf dem aufrufenden Thread bis zum Ende aus (normales Ende, `leave`,
        /// `terminate` oder unbehandelte Exception, siehe <see cref="VM.UnhandledException"/>) und schließt danach die
        /// vom Skript offen gelassenen Handles.</summary>
        public void Run()
        {
            if (VirtualMachine == null) return;
            try { VirtualMachine.Run(); }
            finally { CloseHostResources(); }
        }

        
        private RuntimeSession(CompiledProgram compiledProgram)
        {
            CompiledProgram = compiledProgram;
            // Private constructor to prevent direct instantiation
        }

        protected void SetVM(VM virtualMachine, WindowManager? wm, Scope globalScope, NativeRegistry natives, FramebufferManager? framebufferManager, RendererManager? rendererManager, int firstUserSourceIndex)
        {
            VirtualMachine = virtualMachine;
            WindowManager = wm;
            GlobalScope = globalScope;
            FramebufferManager = framebufferManager;
            RendererManager = rendererManager;
            nativeRegistry = natives;
            FirstUserSourceIndex = firstUserSourceIndex;
        }

        public void CallLambda(LambdaValue lambda, Value[] args)
        {
            FireRuntime.RunCallback(lambda, args, nativeRegistry, CompiledProgram.Classes, () => VirtualMachine.SnapshotGlobals(),
                message => Console.WriteLine($"(unhandled exception in the callback: {message})"), owner: VirtualMachine);
        }

        /// <summary>Baut die DirectiveRegistry, die reale Programme (siehe
        /// Build) UND die Live-Diagnostik (siehe Editor.LiveDiagnostics)
        /// gleichermaßen nutzen - genau EINE Stelle, die weiß, welche
        /// benutzerdefinierten Präprozessor-Direktiven es gibt (aktuell:
        /// `#import "extension"`, siehe ImportedPreludes), damit beide IMMER
        /// im Gleichschritt bleiben. Ohne das würde eine hier erkannte Direktive in der
        /// Live-Diagnostik weiterhin fälschlich als "unbekannte Direktive"
        /// unterkringelt, obwohl sie beim echten Kompilieren längst
        /// akzeptiert wird (der Preprocessor lässt jede NICHT hier
        /// registrierte Direktive unverändert im Text stehen, der Parser
        /// kennt sie dann seinerseits nicht und wirft, siehe Parsing.
        /// Preprocessor-Klassendoku/Parser.ParseDirective).
        ///
        /// `onImport`: Callback für eine ERKANNTE `#import "name"`-
        /// Direktive, mit dem Schlüssel der Erweiterung aus NativeImports
        /// (z.B. um die Grafik-Bridge tatsächlich zu laden, oder - in der
        /// Live-Diagnostik - um deren Prelude einzusetzen) - `null`, wenn
        /// der Aufrufer nur wissen will "ist das syntaktisch eine gültige
        /// Direktive", ohne ihre eigentliche Wirkung auszulösen. Eine UNBEKANNTE Erweiterung (`#import
        /// "unfug"`) wirft weiterhin - das ist ein ECHTER Fehler, kein
        /// reines "kennt die Live-Diagnostik das nur (noch) nicht".</summary>
        public static DirectiveRegistry CreateProjectDirectiveRegistry(Action<string>? onImport = null, IEnumerable<string>? defines = null)
        {
            var registry = new DirectiveRegistry(); // komplett leer, NICHT CreateDefault()
            foreach (var symbol in ConditionalSymbols.For(null, ConditionalSymbols.DefaultEngine, null, defines)) registry.Symbols.Add(symbol); // `#if windows`: the machine the VM runs on
            registry.Register("import", 1, (ctx, args, line) =>
            {
                if (args[0].Kind == ValueKind.String)
                {
                    foreach (var key in ImportedPreludes.WithDependencies(ImportedPreludes.ParseImportName(args[0].AsString()))) onImport?.Invoke(key);
                    return null;
                }
                throw new Exception($"Wrong arguments for the 'import' directive.");
            });
            return registry;
        }

        public static RuntimeSession Build(IReadOnlyList<string> sources, VmExecutionMode? executionMode, Func<Value[], Value>? debugWriter = null, string? outname = null, fire.IO.Bridge.IoPolicy? ioPolicy = null, fire.IO.Bridge.IoStdio? ioStdio = null, string? basePath = null, fire.Device.Manager.DeviceManager.DeviceManager? deviceManager = null, int? floatWidth = null, IReadOnlyList<string>? defines = null, Func<IFramebufferRenderer>? windowRenderer = null)
        {
            var linker = new Linker { BasePath = basePath, Defines = defines };
            var natives = new NativeRegistry();

            var linkedProgram = linker.CompileAndLink(sources, debugWriter, outname, executionMode, floatWidth);
            Value.SingleFloats = linkedProgram.FloatWidth == 32; // the precision of float is process-wide while a program runs

            if (linkedProgram.NativeImports.Contains(NativeImports.Print))
            {
                if (debugWriter == null)
                    natives.Register("print", args => Value.MakeUndefined());
                else
                    natives.Register("print", args => VM.StringifyForPrint(args) is { } shown ? debugWriter(shown) : Value.MakeUndefined());
            }
            natives.RegisterBaseTypeNatives(linkedProgram.Program.Resources);

            // WICHTIG: native Funktionen werden über ihren INDEX angesprungen - die Reihenfolge der Registrierung muss
            // exakt der beim Übersetzen entsprechen (siehe ImportedPreludes.Insert): graphics, windows, reflection, time, devices, io.
            FramebufferManager? fbManager = null;
            RendererManager? rendererManager = null;
            WindowManager? windowManager = null;

            var session = new RuntimeSession(linkedProgram.Program);
            
            if (linkedProgram.NativeImports.Contains(NativeImports.Graphics))
            {
                var font = new IntegratedGlyphFont();
                fbManager = new FramebufferManager();
                rendererManager = new RendererManager(fbManager, font);

                // Bilddateien (Framebuffer.FromFile) liest das Programm nur, wo die IoPolicy des Hosts das Lesen erlaubt (wie IO.File)
                var imagePolicy = ioPolicy ?? fire.IO.Bridge.IoPolicy.AllowAll;
                GraphicsBridge.RegisterAll(natives, fbManager, rendererManager, path =>
                {
                    string fullPath = Path.GetFullPath(path);
                    if (!imagePolicy.IsAllowed(fullPath, fire.IO.Bridge.IoAccess.Read, out var reason))
                        throw new UnauthorizedAccessException(reason ?? $"Access to '{fullPath}' is not allowed.");
                    return File.ReadAllBytes(fullPath);
                });

                // `#import "windows"`: das SDL-Fenster zum Framebuffer (direkt hinter graphics registriert, wie beim Uebersetzen)
                if (linkedProgram.NativeImports.Contains(NativeImports.Windows))
                {
                    windowManager = new WindowManager(fbManager, (l, v) => session.CallLambda(l, v), windowRenderer);   // windowRenderer: null = a SDL window
                    fire.Windows.Bridge.WindowsBridge.RegisterAll(natives, windowManager);
                }
            }

            if (linkedProgram.NativeImports.Contains(NativeImports.Reflection))
                ReflectionNatives.Register(natives);

            // `ioPolicy`: was Skripte im Dateisystem anfassen dürfen, `ioStdio`: wohin IO.Stdio führt - beides entscheidet der HOST (siehe IoPolicy/IoStdio),
            // Vorgabe: alles erlaubt, echte Konsole. Die Natives von `io` sind C++ in einer Bibliothek und fragen den Host über PackageHost.
            IDisposable? ioResources = null;
            if (linkedProgram.PackageNatives is { Count: > 0 })
                ioResources = PackageHost.Begin(ioPolicy, ioStdio, linkedProgram.NativeImports.Contains("pkg:devices"), deviceManager);   // `deviceManager`: the manager of the host (e.g. the shared one of the editor); without it the program gets one with the built-in drivers, freed after the run

            PackageImports.RegisterForRun(natives, linkedProgram);   // the natives of imports of packages: names only (they are C++)

            var globalScope = new Scope(null, isGlobal: true);
            
            var mainVm = new VM(linkedProgram.Program.TopLevel, globalScope, natives, linkedProgram.Program.Classes,
                externSignatures: linkedProgram.Program.ExternSignatures, isMainThreadVm: true, executionMode: linkedProgram.ExecutionMode);

            session.SetVM(mainVm, windowManager, globalScope, natives, fbManager, rendererManager, linkedProgram.FirstUserSource);
            session.IoResources = ioResources;

            return session;
        }
    }
}
