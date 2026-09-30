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
    /// Ausführungsumgebung der gepackten Runtime (die Variante mit Editor/Debugger liegt in fire.Compiler).
    ///
    /// WICHTIG für die schlanke Ausgabedatei: Die Bridges (Terminal/SDL, Geräte, IO) sind NICHT mehr in jede
    /// gepackte Datei eingebettet, sondern nur, wenn das Programm sie per `#import` braucht (siehe Packer,
    /// PackagePlan). Fehlt eine Bridge-DLL, darf sie deshalb auch nie geladen werden - der JIT löst einen Typ
    /// aber schon beim Übersetzen einer Methode auf, die ihn in Signatur, lokaler Variable oder Aufruf erwähnt.
    /// Darum steht jeder Zugriff auf eine Bridge in einer eigenen [NoInlining]-Methode (RegisterGraphics/
    /// RegisterDevices/RegisterIo), die nur betreten wird, wenn der Import da ist; `Build`, `Session` und alles
    /// davor erwähnen keinen Bridge-Typ (auch nicht in Feldern, Properties oder Parametern).
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

        /// <summary>Sicherheitsnetz des Hosts: schließt nach dem Lauf alle Streams, die ein Skript offen gelassen hat
        /// (siehe IoBridge.RegisterAll). Der Destruktor von `IO.FileStream` &amp; Co. schließt sie normalerweise schon.</summary>
        protected IDisposable? IoResources { get; set; }

        public void Run()
        {
            if (VirtualMachine == null) return;
            try { VirtualMachine.Run(); }
            finally { IoResources?.Dispose(); }
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
            natives.RegisterBaseTypeNatives();

            var session = new Session(linkedProgram.Program);

            if (linkedProgram.NativeImports.Contains(NativeImports.Graphics))
                RegisterGraphics(session, natives);

            if (linkedProgram.NativeImports.Contains(NativeImports.Devices))
                RegisterDevices(natives);

            // Dateisystem-/Stdio-Policy: die gepackte Runtime nutzt die Vorgabe (alles erlaubt, echte Konsole) -
            // Hosts mit eigener Policy (Editor) bauen ihre Session über fire.Compiler.RuntimeSession.
            if (linkedProgram.NativeImports.Contains(NativeImports.IO))
                session.IoResources = RegisterIo(natives);

            var globalScope = new Scope(null, isGlobal: true);

            var mainVm = new VM(linkedProgram.Program.TopLevel, globalScope, natives, linkedProgram.Program.Classes,
                externSignatures: linkedProgram.Program.ExternSignatures, isMainThreadVm: true, executionMode: executionMode);

            session.SetVM(mainVm, globalScope, natives, linkedProgram.FirstUserSource);

            return session;
        }

        // Jede dieser Methoden ist die EINZIGE Stelle, die ihre Bridge-Typen erwähnt (siehe Klassen-Doku).

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RegisterGraphics(Session session, NativeRegistry natives)
        {
            var font = new fire.Terminal.IntegratedGlyphFont();
            var fbManager = new fire.Terminal.FramebufferManager();
            var consoleManager = new fire.Terminal.ConsoleManager(fbManager, font);
            var windowManager = new fire.Terminal.Windows.WindowManager(fbManager, (l, v) => session.CallLambda(l, v));

            fire.Terminal.Bridge.GraphicsBridge.RegisterAll(natives, fbManager, consoleManager, windowManager);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RegisterDevices(NativeRegistry natives)
        {
            var deviceManager = new fire.Device.Manager.DeviceManager.DeviceManager();

            fire.Device.Bridge.DeviceBridge.RegisterAll(natives, deviceManager);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static IDisposable? RegisterIo(NativeRegistry natives) =>
            fire.IO.Bridge.IoBridge.RegisterAll(natives, null, null);
    }
}
