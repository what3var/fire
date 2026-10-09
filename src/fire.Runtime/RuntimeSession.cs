using fire.Bytecode;
using fire.Parsing;
using fire.Resolving;
using fire.Runtime;
using fire.Values;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace fire.Runtime
{
    /// <summary>
    /// Execution environment of the packed runtime (the variant with editor/debugger lies in fire.Compiler).
    ///
    /// IMPORTANT for the lean output file: the bridges (terminal/SDL, devices, IO) are NO longer embedded in every
    /// packed file, but only if the program needs them via `#import` (see Packer,
    /// PackagePlan). If a bridge DLL is missing, it must therefore never be loaded either - the JIT resolves a type
    /// already when compiling a method that mentions it in a signature, local variable or call.
    /// That is why every access to a bridge sits in a [NoInlining] method of its own (RegisterGraphics/
    /// RegisterDevices/RegisterIo), which is only entered if the import is there; `Build`, `Session` and everything
    /// before it mention no bridge type (not even in fields, properties or parameters).
    /// </summary>
    public class Session
    {
        public VM? VirtualMachine { get; private set; }

        public CompiledProgram CompiledProgram { get; private set; }

        public Scope? GlobalScope { get; private set; }

        public int FirstUserSourceIndex { get; private set; }

        protected NativeRegistry? nativeRegistry { get; set; }

        private Session(CompiledProgram compiledProgram)
        {
            CompiledProgram = compiledProgram;
            // Private constructor to prevent direct instantiation
        }

        /// <summary>Policy and console of the host for the natives of packages (see PackageHost); at the end of the run it closes what the script left open.
        /// The destructor of `IO.FileStream` &amp; co. normally closes streams already.</summary>
        protected IDisposable? IoResources { get; set; }

        /// <summary>Cleans up the device bridge after the run (see DeviceBridge.RegisterAll).</summary>
        protected IDisposable? DeviceResources { get; set; }

        public void Run()
        {
            if (VirtualMachine == null) return;
            try { VirtualMachine.Run(); }
            finally { IoResources?.Dispose(); DeviceResources?.Dispose(); }
        }

        protected void SetVM(VM virtualMachine, Scope globalScope, NativeRegistry natives, int firstUserSourceIndex)
        {
            VirtualMachine = virtualMachine;
            GlobalScope = globalScope;
            nativeRegistry = natives;
            FirstUserSourceIndex = firstUserSourceIndex;
        }

        protected void CallLambda(LambdaValue lambda, Value[] args)
        {
            if (VirtualMachine == null || nativeRegistry == null)
                return;
            FireRuntime.RunCallback(lambda, args, nativeRegistry, CompiledProgram.Classes, () => VirtualMachine.SnapshotGlobals(),
                message => Console.WriteLine($"(unbehandelte Exception im Callback: {message})"), owner: VirtualMachine);
        }

        public static Session Build(LinkedProgram linkedProgram, VmExecutionMode executionMode, Func<Value[], Value>? debugWriter = null)
        {
            Value.SingleFloats = linkedProgram.FloatWidth == 32; // the precision of float is process-wide while a program runs
            var natives = new NativeRegistry();

            if (linkedProgram.NativeImports.Contains(NativeImports.Print))
            {
                if (debugWriter == null)
                    natives.Register("print", args => Value.MakeUndefined());
                else
                    natives.Register("print", args => VM.StringifyForPrint(args) is { } shown ? debugWriter(shown) : Value.MakeUndefined());
            }
            natives.RegisterBaseTypeNatives(linkedProgram.Program.Resources);

            var session = new Session(linkedProgram.Program);

            // IMPORTANT: native functions are jumped to via their INDEX - the order of registration must
            // exactly match that when translating (see ImportedPreludes.Insert): graphics, reflection, time, devices, io.
            if (linkedProgram.NativeImports.Contains(NativeImports.Graphics))
            {
                // the manager travels as object: every method that names the type loads fire.Terminal when JIT-compiling (see class documentation)
                object fbManager = RegisterGraphics(natives);
                if (linkedProgram.NativeImports.Contains(NativeImports.Windows))
                    RegisterWindows(session, natives, fbManager);
            }

            if (linkedProgram.NativeImports.Contains(NativeImports.Reflection))
                ReflectionNatives.Register(natives);

            // File system/stdio policy: the packed runtime uses the default (everything allowed, real console) -
            // hosts with their own policy (editor) build their session via fire.Compiler.RuntimeSession.
            // what the natives of packages (the io package) ask of the host: the real console, everything allowed - the packed runtime has no other host
            if (linkedProgram.PackageNatives is { Count: > 0 })
                session.IoResources = PackageHost.Begin(null, null, linkedProgram.NativeImports.Contains("pkg:devices"));

            // the natives of imports of packages (C++ in shared libraries, the libraries of a packed program come from its payload): same names, same order
            PackageNativeBinding.Register(natives, linkedProgram.PackageNatives, linkedProgram.PackageNativeLibraries, null);

            var globalScope = new Scope(null, isGlobal: true);

            var mainVm = new VM(linkedProgram.Program.TopLevel, globalScope, natives, linkedProgram.Program.Classes,
                externSignatures: linkedProgram.Program.ExternSignatures, isMainThreadVm: true, executionMode: executionMode);

            session.SetVM(mainVm, globalScope, natives, linkedProgram.FirstUserSource);

            return session;
        }

        // Each of these methods is the ONLY place that mentions its bridge types (see class documentation).

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static object RegisterGraphics(NativeRegistry natives)
        {
            var font = new fire.Terminal.IntegratedGlyphFont();
            var fbManager = new fire.Terminal.FramebufferManager();
            var rendererManager = new fire.Terminal.RendererManager(fbManager, font);

            fire.Terminal.Bridge.GraphicsBridge.RegisterAll(natives, fbManager, rendererManager);
            return fbManager;
        }

        /// <summary>`#import "windows"`: the SDL window (own assembly together with SDL - a program with only `graphics` never loads it).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RegisterWindows(Session session, NativeRegistry natives, object framebuffers)
        {
            var windowManager = new fire.Terminal.Windows.WindowManager((fire.Terminal.FramebufferManager)framebuffers, (l, v) => session.CallLambda(l, v));
            fire.Windows.Bridge.WindowsBridge.RegisterAll(natives, windowManager);
        }

    }
}
