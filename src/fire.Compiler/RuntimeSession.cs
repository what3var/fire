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

        /// <summary>The file of each source by source index (see LinkedProgram.SourceFiles): the debugger maps a file to its index and the index of a paused line to its file.</summary>
        public IReadOnlyList<string?>? SourceFiles { get; private set; }




        protected NativeRegistry? nativeRegistry { get; set; }

        /// <summary>Policy and console of the host for the natives of packages (see PackageHost): at the end closes all streams that a script left open
        /// open. Whoever drives the VM itself (e.g. the step debugger) calls this after the run.</summary>
        protected IDisposable? IoResources { get; set; }

        /// <summary>Cleans up the device bridge after the run: releases the receive hooks and releases a NON-shared
        /// manager (see DeviceBridge.RegisterAll).</summary>
        protected IDisposable? DeviceResources { get; set; }

        /// <summary>The copy of the C library of graphics and windows that the program runs on when the host drives its windows (null: the library of the process, or C#).</summary>
        public GraphicsLibrary.Instance? GraphicsInstance { get; private set; }

        public void CloseHostResources()
        {
            WindowManager?.StopManagers();   // the threads of fire's window managers end with the run
            IoResources?.Dispose();          // (also resets the libraries of the program: the windows of the C library close)
            IoResources = null;
            DeviceResources?.Dispose();
            GraphicsInstance?.Dispose();     // free for the next program
            GraphicsInstance = null;
        }

        /// <summary>The pixels of framebuffer <paramref name="id"/> of the program (R,G,B,A per word), wherever graphics run (C# or C); null: there is none.</summary>
        public uint[]? FramebufferPixels(int id, out int width, out int height)
        {
            if (FramebufferManager?.GetFramebuffer(id) is { } fb)
            {
                width = fb.Width; height = fb.Height;
                return (uint[])fb.Pixels.Clone();
            }
            if (UsesGraphicsLibrary) return GraphicsLibrary.Pixels(id, out width, out height, GraphicsInstance);
            width = height = 0;
            return null;
        }

        private bool UsesGraphicsLibrary { get; set; }

        /// <summary>Runs the program on the calling thread to the end (normal end, `leave`,
        /// `terminate` or unhandled exception, see <see cref="VM.UnhandledException"/>) and afterwards closes the
        /// handles left open by the script.</summary>
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

        /// <summary>Builds the DirectiveRegistry that real programs (see
        /// Build) AND the live diagnostics (see Editor.LiveDiagnostics)
        /// use alike - exactly ONE place that knows which
        /// user-defined preprocessor directives exist (currently:
        /// `#import "extension"`, see ImportedPreludes), so that both ALWAYS
        /// stay in step. Without that, a directive recognised here would
        /// continue to be wrongly squiggled in the live diagnostics as "unknown directive",
        /// although it is long
        /// accepted in real compiling (the preprocessor leaves every directive NOT
        /// registered here unchanged in the text, the parser
        /// then does not know it either and throws, see Parsing.
        /// Preprocessor class documentation/Parser.ParseDirective).
        ///
        /// `onImport`: callback for a RECOGNISED `#import "name"`
        /// directive, with the key of the extension from NativeImports
        /// (e.g. to actually load the graphics bridge, or - in the
        /// live diagnostics - to insert its prelude) - `null` if
        /// the caller only wants to know "is this syntactically a valid
        /// directive", without triggering its actual effect. An UNKNOWN extension (`#import
        /// "nonsense"`) still throws - that is a REAL error, not
        /// merely "the live diagnostics just do not know it (yet)".</summary>
        public static DirectiveRegistry CreateProjectDirectiveRegistry(Action<string>? onImport = null, IEnumerable<string>? defines = null, Func<string, bool>? tryLibrary = null)
        {
            var registry = new DirectiveRegistry(); // completely empty, NOT CreateDefault()
            foreach (var symbol in ConditionalSymbols.For(null, ConditionalSymbols.DefaultEngine, null, defines)) registry.Symbols.Add(symbol); // `#if windows`: the machine the VM runs on
            registry.Register("import", 1, (ctx, args, line) =>
            {
                if (args[0].Kind == ValueKind.String)
                {
                    if (tryLibrary?.Invoke(args[0].AsString()) == true) return null;   // a library of the project (handled by the caller)
                    foreach (var key in ImportedPreludes.WithDependencies(ImportedPreludes.ParseImportName(args[0].AsString()))) onImport?.Invoke(key);
                    return null;
                }
                throw new Exception($"Wrong arguments for the 'import' directive.");
            });
            return registry;
        }

        public static RuntimeSession Build(IReadOnlyList<string> sources, VmExecutionMode? executionMode, Func<Value[], Value>? debugWriter = null, string? outname = null, fire.IO.Bridge.IoPolicy? ioPolicy = null, fire.IO.Bridge.IoStdio? ioStdio = null, string? basePath = null, fire.Device.Manager.DeviceManager.DeviceManager? deviceManager = null, int? floatWidth = null, IReadOnlyList<string>? defines = null, Func<IFramebufferRenderer>? windowRenderer = null, fire.Runtime.NetPolicy? netPolicy = null, fire.Projects.BuildPlan? plan = null, ExternRegistry? externs = null, IReadOnlyList<string?>? sourcePaths = null, fire.Standard.ProgramEnvironment? environment = null, HostWindows? hostWindows = null)
        {
            // with a project: its files are the sources (`sources` is then the text of those files, see BuildPlan.SourceTexts) and its settings go before the tags
            // (`sourcePaths`: the files of `sources` - each `#include` is relative to the file that has it)
            var linker = new Linker { BasePath = basePath, Defines = defines, Plan = plan, SourcePaths = plan?.SourcePaths.Cast<string?>().ToList() ?? sourcePaths };
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
            natives.RegisterBaseTypeNatives(linkedProgram.Program.Resources, environment, linkedProgram.Program.CustomUnits);   // (Environment.Args/ProgramPath: what the caller passes; none without)

            // IMPORTANT: native functions are jumped to via their INDEX - the order of registration must
            // exactly match that when translating (see ImportedPreludes.Insert): graphics, windows, reflection, time, devices, io.
            FramebufferManager? fbManager = null;
            RendererManager? rendererManager = null;
            WindowManager? windowManager = null;

            var session = new RuntimeSession(linkedProgram.Program);
            
            // the C implementation of graphics and windows (the same as a native build: fire's own display, no SDL) - docs/UI_PLATFORM.md, part 2. Where the host drives the
            // windows (`hostWindows`: the editor's live and design views), the program gets a copy of the library of its own; the C# implementation stays where the host
            // renders them itself (`windowRenderer` alone: the tests' renderers), where FIRE_VM_GRAPHICS=cs asks for it, and where the library cannot be had (no C compiler)
            string? graphicsFile = null;
            bool graphics = linkedProgram.NativeImports.Contains(NativeImports.Graphics);
            if (graphics && GraphicsLibrary.Wanted(windowRenderer != null && hostWindows == null))
            {
                if (hostWindows != null)
                {
                    session.GraphicsInstance = GraphicsLibrary.Instance.Acquire();
                    session.GraphicsInstance?.Attach(hostWindows.Display);
                    if (session.GraphicsInstance != null)
                        graphicsFile = GraphicsLibrary.Register(natives, linkedProgram.NativeImports.Contains(NativeImports.Windows), instance: session.GraphicsInstance);
                }
                else if (GraphicsLibrary.Locate() != null)
                    graphicsFile = GraphicsLibrary.Register(natives, linkedProgram.NativeImports.Contains(NativeImports.Windows));
                session.UsesGraphicsLibrary = graphicsFile != null;
                if (graphicsFile == null) GraphicsLibrary.WarnFallback();
            }
            if (graphics && graphicsFile == null)
            {
                if (windowRenderer == null && hostWindows == null) PackageHost.GraphicsInC = false;
                var font = new IntegratedGlyphFont();
                fbManager = new FramebufferManager();
                rendererManager = new RendererManager(fbManager, font);

                // The program reads image files (Framebuffer.FromFile) only where the host's IoPolicy allows reading (like IO.File)
                var imagePolicy = ioPolicy ?? fire.IO.Bridge.IoPolicy.AllowAll;
                GraphicsBridge.RegisterAll(natives, fbManager, rendererManager, path =>
                {
                    string fullPath = Path.GetFullPath(path);
                    if (!imagePolicy.IsAllowed(fullPath, fire.IO.Bridge.IoAccess.Read, out var reason))
                        throw new UnauthorizedAccessException(reason ?? $"Access to '{fullPath}' is not allowed.");
                    return File.ReadAllBytes(fullPath);
                });

                // `#import "windows"`: the SDL window for the framebuffer (registered directly behind graphics, as when translating)
                if (linkedProgram.NativeImports.Contains(NativeImports.Windows))
                {
                    windowManager = new WindowManager(fbManager, (l, v) => session.CallLambda(l, v), windowRenderer ?? (hostWindows != null ? () => new HostRenderer(hostWindows.Display) : null), d => session.VirtualMachine?.AddHostHold(d));   // windowRenderer: null = a SDL window
                    fire.Windows.Bridge.WindowsBridge.RegisterAll(natives, windowManager);
                }
            }

            if (linkedProgram.NativeImports.Contains(NativeImports.Reflection))
                ReflectionNatives.Register(natives);

            // `ioPolicy`: what scripts may touch in the file system, `ioStdio`: where IO.Stdio leads - both decided by the HOST (see IoPolicy/IoStdio),
            // default: everything allowed, real console. The natives of `io` are C++ in a library and ask the host via PackageHost.
            IDisposable? ioResources = null;
            if (linkedProgram.PackageNatives is { Count: > 0 } || graphicsFile != null)
            {
                var libraries = (linkedProgram.PackageNativeLibraries ?? new List<string>()).Append(graphicsFile).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                ioResources = PackageHost.Begin(ioPolicy, ioStdio, linkedProgram.NativeImports.Contains("pkg:devices"), deviceManager, netPolicy, libraries);
            }   // `deviceManager`: the manager of the host (e.g. the shared one of the editor); without it the program gets one with the built-in drivers, freed after the run

            PackageImports.RegisterForRun(natives, linkedProgram);   // the natives of imports of packages: names only (they are C++)

            var globalScope = new Scope(null, isGlobal: true);
            
            fire.Values.Unit.UseCustomUnits(linkedProgram.Program.CustomUnits);   // the units the program declares (`unit Inch: in { ... }`)
            VM.RegisterProgram(linkedProgram);   // (the interfaces of its modules: what a fire plugin is checked against)
            var mainVm = new VM(linkedProgram.Program.TopLevel, globalScope, natives, linkedProgram.Program.Classes,
                externs: externs, externSignatures: linkedProgram.Program.ExternSignatures, isMainThreadVm: true, executionMode: linkedProgram.ExecutionMode);

            session.SetVM(mainVm, windowManager, globalScope, natives, fbManager, rendererManager, linkedProgram.FirstUserSource);
            session.SourceFiles = linkedProgram.SourceFiles;
            session.IoResources = ioResources;

            return session;
        }
    }
}
