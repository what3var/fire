using fire.Bytecode;
using fire.Compiler;
using fire.Parsing;
using fire.Resolving;
using fire.Runtime;
using fire.Terminal;
using fire.Terminal.Bridge;
using fire.Terminal.Windows;
using fire.Values;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using static fire.Resolving.ResolvedRef;

namespace fire.Editor
{
    /// <summary>
    /// Kapselt einen Kompilier+Ausführungs-Lauf eines Skripts für den
    /// thread-fähigen Step-Debugger: kompiliert einmalig (inkl. Prelude),
    /// hält danach den Main-Thread PLUS jeden zur Laufzeit per `fire`
    /// entstehenden Thread als je eine DebugThreadContext-Instanz (siehe
    /// dort - JEDE davon läuft auf ihrem EIGENEN Hintergrund-Thread, NIE auf
    /// dem UI-Thread, siehe dortigen Klassenkommentar zur Begründung).
    /// Steuerung (Step/Continue/...) wirkt immer auf den gerade AKTIVEN
    /// Thread (siehe ActiveThread) - welcher das ist, wählt die UI über das
    /// Threads-Panel. Alle Steuer-Methoden sind FIRE-AND-FORGET (kehren
    /// sofort zurück) - das Ergebnis kommt asynchron über ThreadPaused.
    ///
    /// `print()` wird umgeleitet (OutputWritten-Event) statt auf die echte
    /// Konsole zu gehen, mit dem Namen des jeweils AUSFÜHRENDEN Threads
    /// vorangestellt (siehe VM.CurrentThreadVm - das [ThreadStatic]-Feld,
    /// das jede VM-Instanz beim Start von Run()/StepInstruction() auf sich
    /// selbst setzt), damit im Ausgabefenster nachvollziehbar bleibt, welche
    /// Zeile von welchem Thread kam.
    /// </summary>
    public sealed class DebugSession
    {
        private readonly object _threadsLock = new();
        private readonly List<DebugThreadContext> _threads = new();
        private int _fireThreadCounter;
        private HashSet<(int SourceIndex, int Line)> _breakpointsSnapshot = new();

        public IReadOnlyList<DebugThreadContext> Threads
        {
            get { lock (_threadsLock) return _threads.ToList(); }
        }

        public DebugThreadContext? ActiveThread { get; private set; }

        public VM? Vm => ActiveThread?.Vm;
        public bool IsStarted { get; private set; }
        public bool IsFinished => ActiveThread?.IsFinished ?? false;
        public string? CompileError { get; private set; }
        public string? RuntimeError => ActiveThread?.RuntimeError;

        public VmExecutionMode ExecutionMode { get; set; } = VmExecutionMode.Debug;

        public VmExecutionMode? ActiveExecutionMode { get; private set; }

        /// <summary>Quell-Index (siehe Bytecode.Chunk.MarkLine/VM.CurrentLocation),
        /// ab dem der erste EIGENE Quelltext des Aufrufers (die `sources`, die
        /// an Compile() gingen) im kompilierten Programm beginnt - Weiterleitung
        /// von Runtime.RuntimeSession.FirstUserSourceIndex (siehe dort), erst
        /// nach einem erfolgreichen Compile()-Aufruf gültig (vorher 0).</summary>
        public int FirstUserSourceIndex { get; private set; }

        public event Action<string>? OutputWritten;

        /// <summary>Ein neuer Fire-Thread ist entstanden - KANN auf dessen
        /// eigenem Hintergrund-Thread feuern, Abonnent muss selbst für
        /// Dispatcher.InvokeAsync sorgen (siehe DebugThreadContext.Paused-
        /// Doku, NICHT das blockierende Invoke).</summary>
        public event Action<DebugThreadContext>? ThreadAdded;

        /// <summary>Wie ThreadAdded, aber für JEDEN abgeschlossenen Schritt
        /// eines beliebigen Threads (nicht nur bei dessen Entstehung) -
        /// separates Event, weil die UI hierauf i.d.R. nur reagieren muss,
        /// wenn der GERADE AKTIVE Thread betroffen ist (siehe MainWindow),
        /// während ThreadAdded IMMER die Threads-Liste neu aufbauen muss.</summary>
        public event Action<DebugThreadContext>? ThreadPaused;

        public DebugSession()
        {
        }

        private DebugThreadContext? FindContextFor(VM? vm)
        {
            if (vm == null) return null;
            lock (_threadsLock) return _threads.FirstOrDefault(t => ReferenceEquals(t.Vm, vm));
        }

        /// <summary>Aktuelle Haltepunkt-Zeilen, wie sie der Editor zuletzt
        /// gemeldet hat - jeder NEU entstehende Fire-Thread startet seinen
        /// automatischen "Weiter"-Lauf (siehe DebugThreadContext.
        /// ForFireThread) mit einem Schnappschuss DIESER Menge. Nachträglich
        /// im Editor gesetzte/entfernte Haltepunkte wirken sich auf einen
        /// BEREITS automatisch laufenden Fire-Thread deshalb erst aus,
        /// nachdem er das nächste Mal neu angestoßen wird - eine bewusste
        /// Vereinfachung, um keine über mehrere Threads hinweg geteilte,
        /// nebenläufig veränderliche Breakpoint-Menge synchronisieren zu
        /// müssen.</summary>
        public void UpdateBreakpoints(IEnumerable<(int SourceIndex, int Line)> locations) => _breakpointsSnapshot = new HashSet<(int, int)>(locations);

        /// <summary>Kompiliert den Quelltext neu und setzt eine frische
        /// Main-Thread-VM auf (auf ihrem eigenen Hintergrund-Thread, siehe
        /// DebugThreadContext.ForMain - startet wartend, noch nichts läuft).
        /// Bei einem Parse-/Resolve-Fehler bleibt Vm null, CompileError
        /// enthält die Meldung.</summary>
        public bool Compile(string[] sources, string? outname = null)
        {
            Reset();

            try
            {
                var session = RuntimeSession.Build(sources, ExecutionMode, args =>
                {
                    string text = args.Length > 0 ? args[0].ToString() : "";
                    string? threadName = FindContextFor(VM.CurrentThreadVm)?.Name;
                    OutputWritten?.Invoke(threadName != null && threadName != "Main" ? $"[{threadName}] {text}" : text);
                    return Value.MakeUndefined();
                }, outname,
                // IO.Stdio (`#import "io"`) landet im selben Ausgabefenster wie print():
                // Ausgabe und Fehler zeilenweise, Eingabe ist leer (sofort Ende).
                ioStdio: fire.IO.Bridge.IoStdio.Custom(line =>
                {
                    string? threadName = FindContextFor(VM.CurrentThreadVm)?.Name;
                    OutputWritten?.Invoke(threadName != null && threadName != "Main" ? $"[{threadName}] {line}" : line);
                }));

                ActiveExecutionMode = ExecutionMode;

                FirstUserSourceIndex = session.FirstUserSourceIndex;

                var mainCtx = DebugThreadContext.ForMain(session.VirtualMachine);
                mainCtx.Paused += ctx => ThreadPaused?.Invoke(ctx);

                lock (_threadsLock)
                {
                    _threads.Clear();
                    _threads.Add(mainCtx);
                }
                ActiveThread = mainCtx;

                // Ab jetzt übernimmt DIESE Session jeden neu entstehenden
                // Fire-Thread (siehe InterceptNewFireThread) - wichtig: in
                // Reset() wieder abmelden, sonst würde ein späterer,
                // komplett unabhängiger Lauf (oder sogar ein anderes
                // Skript) im selben Prozess versehentlich noch immer über
                // DIESE (dann veraltete) Session laufen.
                FireRuntime.ThreadBodyInterceptor = InterceptNewFireThread;

                return true;
            }
            catch (Exception ex) when (ex is ParseException or ResolverException
                or NotSupportedException or PreprocessorException)
            {
                // Alle gesammelten Fehler (Resolver/Compiler brechen nicht beim
                // ersten ab, siehe CompileErrors), nicht nur den ersten.
                CompileError = CompileErrors.Describe(ex);
                return false;
            }
        }

        /// <summary>Läuft auf dem Thread, der GERADE `fire` ausführt (also
        /// z.B. auf dem Hintergrund-Thread eines DebugThreadContext, siehe
        /// Runtime.FireRuntime.ThreadBodyInterceptor-Doku) - registriert
        /// den neuen Thread im Threads-Panel. Der eigentliche neue
        /// Hintergrund-Thread für die Fire-Thread-VM selbst entsteht INNERHALB
        /// von DebugThreadContext.ForFireThread (siehe dessen Konstruktor) -
        /// diese Methode hier kehrt deshalb SOFORT zurück, der Aufrufer
        /// (Runtime.FireRuntime.Fire) muss NICHT mehr blockieren.</summary>
        private void InterceptNewFireThread(VM vm, Action runNormally)
        {
            string name = $"Fire #{Interlocked.Increment(ref _fireThreadCounter)}";
            var ctx = DebugThreadContext.ForFireThread(vm, name, _breakpointsSnapshot);
            ctx.Paused += c => ThreadPaused?.Invoke(c);

            lock (_threadsLock) { _threads.Add(ctx); }
            ThreadAdded?.Invoke(ctx);
        }

        public void SelectThread(DebugThreadContext thread) => ActiveThread = thread;

        public void Reset()
        {
            if (FireRuntime.ThreadBodyInterceptor != null)
                FireRuntime.ThreadBodyInterceptor = null;

            lock (_threadsLock)
            {
                // Jeden noch laufenden/pausierten Thread aus seiner
                // Warteposition lösen (siehe DebugThreadContext.Abandon-Doku)
                // - sonst bliebe ein gerade wartender Thread für immer auf
                // sein Gate hängen, da ab hier niemand mehr RequestStep für
                // ihn aufruft.
                foreach (var t in _threads)
                    t.Abandon();
                _threads.Clear();
            }
            ActiveThread = null;
            IsStarted = false;
            CompileError = null;
        }

        /// <summary>Ein Debugger-"Schritt" - eine Quelltextzeile weiter (Step
        /// Over, siehe VM.StepLine) AUF DEM AKTIVEN THREAD. FIRE-AND-FORGET -
        /// das Ergebnis kommt über ThreadPaused.</summary>
        public void StepLine() => RunGuarded(_ => vm => vm.StepLine());

        /// <summary>Wie StepLine, springt bei einem Aufruf aber hinein statt
        /// ihn zu überspringen (siehe VM.StepInto).</summary>
        public void StepInto() => RunGuarded(_ => vm => vm.StepInto());

        /// <summary>Läuft bis zum Verlassen der aktuellen Funktion/Methode/
        /// des aktuellen Lambdas (siehe VM.StepOut).</summary>
        public void StepOut() => RunGuarded(_ => vm => vm.StepOut());

        /// <summary>Läuft bis zum nächsten Haltepunkt oder Programmende, auf
        /// dem AKTIVEN Thread - unterbrechbar über PauseActiveThread.</summary>
        public void Continue(ISet<(int SourceIndex, int Line)> breakpoints) =>
            RunGuarded(ctx => DebugThreadContext.MakeContinueStep(breakpoints, ctx.ConsumePauseRequest));

        /// <summary>Läuft ohne Unterbrechung bis zum Programmende (kein
        /// Debugging, einfach nur ausführen) - AUF DEM AKTIVEN Thread,
        /// unterbrechbar über PauseActiveThread; andere Threads laufen
        /// unabhängig davon in ihrem eigenen, ggf. weiterhin automatischen
        /// Modus weiter.</summary>
        public void RunToCompletion() =>
            RunGuarded(ctx => DebugThreadContext.MakeRunToCompletionStep(ctx.ConsumePauseRequest));

        /// <summary>Bittet den aktiven Thread, einen gerade laufenden
        /// unterbrechbaren Schritt (Continue/RunToCompletion - siehe
        /// DebugThreadContext.RequestPause-Doku für die Einschränkung bei
        /// Step Line/Into/Out) an der nächsten Gelegenheit zu beenden.</summary>
        public void PauseActiveThread() => ActiveThread?.RequestPause();

        private void RunGuarded(Func<DebugThreadContext, Func<VM, bool>> makeStep)
        {
            var ctx = ActiveThread;
            if (ctx == null) return;
            IsStarted = true;
            ctx.RequestStep(makeStep(ctx));
        }
    }
}
