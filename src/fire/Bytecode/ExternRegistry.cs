using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ScriptLang.Bytecode
{
    /// <summary>Eine tatsächlich verlinkte native Implementierung einer per
    /// `extern` deklarierten Funktion. Nimmt die bereits MARSHALLTEN Argumente
    /// entgegen (siehe VM.CallExtern: bool/long/double/char/string für
    /// Werttypen, IntPtr für Pointer-Argumente - echter, gepinnter nativer
    /// Speicher, kein Skript-internes PointerTarget mehr) und liefert den
    /// (ebenfalls bereits nativen) Rückgabewert, oder null für "kein
    /// Rückgabewert" (ReturnType in der `extern`-Deklaration fehlt).</summary>
    public delegate object? ExternFunction(object?[] args);

    /// <summary>
    /// Verzeichnis tatsächlich verlinkter `extern`-Implementierungen - getrennt
    /// von NativeRegistry, weil `extern` bewusst ZWEI Phasen hat: die
    /// Deklaration (Teil des Skripts, vom Resolver/Compiler schon vorher
    /// verarbeitet, siehe ExternDecl/ResolvedRef.Extern) und die eigentliche
    /// Verlinkung (host-seitig, NACH dem Kompilieren, sogar optional - ein
    /// Skript kann `extern` deklarieren und kompilieren, ohne dass die
    /// Implementierung je registriert wird; das schlägt erst beim
    /// TATSÄCHLICHEN Aufruf fehl, nicht beim Kompilieren). Deshalb Lookup per
    /// Name zur Laufzeit statt per Compile-Zeit-Index wie bei NativeRegistry.
    /// </summary>
    public sealed class ExternRegistry
    {
        private readonly Dictionary<string, ExternFunction> _fns = new();

        public void Register(string name, ExternFunction fn) => _fns[name] = fn;

        public bool TryGet(string name, out ExternFunction fn) => _fns.TryGetValue(name, out fn!);

        /// <summary>Eine kleine Demo-Verlinkung gegen echte native Windows-APIs
        /// per P/Invoke (user32/kernel32) - zeigt den vollen Marshalling-Pfad
        /// (Skript-Wert -> echter nativer Typ/Adresse -> echter OS-Aufruf ->
        /// zurück in einen Skript-Wert). Läuft nur unter Windows (die DLLs
        /// existieren nur dort) - unter Linux/macOS stattdessen eine eigene
        /// Registry mit passenden Plattform-APIs bauen, das Muster bleibt
        /// exakt dasselbe.</summary>
        public static ExternRegistry CreateWinApiDemo()
        {
            var reg = new ExternRegistry();

            // extern int ShowMessageBox(string text, string caption)
            reg.Register("ShowMessageBox", args =>
                (long)WinApiNative.MessageBoxW(IntPtr.Zero, (string)args[0]!, (string)args[1]!, 0));

            // extern int GetTickCount()
            reg.Register("GetTickCount", args => (long)WinApiNative.GetTickCount());

            // extern int QueryPerformanceCounter(int* counter) - schreibt
            // einen 64-Bit-Zähler über den Pointer, also 8 Byte - passt exakt
            // zur generischen 8-Byte-Slot-Kopie in VM.WriteNativeValue/
            // ReadNativeValue (anders als z.B. ein 4-Byte int* wie bei manch
            // anderer WinAPI-Funktion, das würde NICHT sauber zusammenpassen).
            reg.Register("QueryPerformanceCounter", args =>
                (long)WinApiNative.QueryPerformanceCounter((IntPtr)args[0]!));

            return reg;
        }

        private static class WinApiNative
        {
            [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
            public static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

            [DllImport("kernel32.dll", EntryPoint = "GetTickCount")]
            public static extern uint GetTickCount();

            [DllImport("kernel32.dll", EntryPoint = "QueryPerformanceCounter")]
            public static extern int QueryPerformanceCounter(IntPtr lpPerformanceCount);
        }
    }
}
