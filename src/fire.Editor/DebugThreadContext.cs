using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using fire.Bytecode;
using fire.Runtime;

namespace fire.Editor
{
    /// <summary>
    /// Kapselt EINE VM-Instanz für den thread-fähigen Debugger - sowohl der
    /// Main-Thread als auch jeder per `fire` entstandene Thread laufen auf
    /// ihrem EIGENEN echten Hintergrund-Thread (siehe RunLoop), NIEMALS auf
    /// dem UI-Thread selbst - sonst würde jeder länger laufende Schritt
    /// ("Weiter", "Bis Ende durchlaufen", oder sogar nur "Step Over" über
    /// eine lang laufende Zeile) die komplette Anwendung einfrieren, da WPF
    /// währenddessen keine Maus-/Tastatur-/Zeichenereignisse mehr verarbeiten
    /// kann.
    ///
    /// Steuerung von der UI aus (RequestStep) ist deshalb bewusst FIRE-AND-
    /// FORGET, NICHT blockierend: die UI stößt einen Schritt an und kehrt
    /// sofort zurück, das Ergebnis kommt asynchron über das Paused-Event
    /// zurück (das der Aufrufer selbst per Dispatcher auf den UI-Thread
    /// holen muss). Innerhalb EINES Threads passiert dabei nie etwas
    /// gleichzeitig (die Gates stellen sicher, dass der Hintergrund-Thread
    /// jeweils GENAU EINEN Schritt nach dem anderen ausführt).
    ///
    /// Verhalten eines Fire-Threads standardmäßig ("niemand hat sich im
    /// Threads-Panel dafür entschieden, ihn einzeln zu steuern"): läuft
    /// automatisch weiter (wie ein "Weiter" im normalen Debugger), bis er
    /// entweder einen Haltepunkt erreicht, fertig ist, abstürzt, oder die
    /// UI ihn explizit pausiert (RequestPause). Der Main-Thread startet
    /// dagegen immer im wartenden Zustand (erst der erste Knopfdruck stößt
    /// überhaupt eine Ausführung an).
    /// </summary>
    public sealed class DebugThreadContext
    {
        public VM Vm { get; }
        public string Name { get; }
        public bool IsMain { get; }
        public bool IsFinished { get; private set; }
        public string? RuntimeError { get; private set; }

        /// <summary>Wird ausgelöst, sobald dieser Thread einen angeforderten
        /// Schritt beendet hat - feuert auf DIESES Threads EIGENEM
        /// Hintergrund-Thread, NIEMALS auf dem UI-Thread; der Abonnent muss
        /// selbst für Dispatcher.InvokeAsync (NICHT das blockierende Invoke -
        /// siehe MainWindow-Konstruktor für die Deadlock-Begründung)
        /// sorgen.</summary>
        public event Action<DebugThreadContext>? Paused;

        private readonly SemaphoreSlim _resumeGate = new(0);
        private volatile Func<VM, bool>? _pendingStep;
        private volatile bool _pauseRequested;
        private volatile bool _abandoned;

        private DebugThreadContext(VM vm, string name, bool isMain)
        {
            Vm = vm;
            Name = name;
            IsMain = isMain;

            var thread = new Thread(RunLoop)
            {
                Name = $"fire-Debug-{name}",
                // Vordergrund-Thread (.NET-Standard) - eine laufende
                // Debug-Sitzung soll den Prozess nicht stillschweigend am
                // Leben halten oder umgekehrt abrupt sterben, während noch
                // etwas läuft; DebugSession.Reset/Abandon sorgt dafür, dass
                // jeder Thread sauber (statt für immer wartend) endet.
                IsBackground = false,
            };
            thread.Start();
        }

        /// <summary>Für den Main-Thread - startet WARTEND (kein
        /// automatischer Lauf, siehe Klassenkommentar).</summary>
        public static DebugThreadContext ForMain(VM vm) => new(vm, "Main", isMain: true);

        /// <summary>Für einen per `fire` entstandenen Thread - startet
        /// SOFORT im automatischen "Weiter"-Modus (siehe Klassenkommentar).
        /// Muss von Runtime.FireRuntime.ThreadBodyInterceptor aus
        /// aufgerufen werden (beliebiger Thread - der Konstruktor startet
        /// selbst einen NEUEN, eigenen Hintergrund-Thread für die
        /// eigentliche Ausführung, läuft also nicht auf dem aufrufenden
        /// Thread).</summary>
        public static DebugThreadContext ForFireThread(VM vm, string name, ISet<(int SourceIndex, int Line)> breakpointsSnapshot)
        {
            var ctx = new DebugThreadContext(vm, name, isMain: false);
            ctx.RequestStep(MakeContinueStep(breakpointsSnapshot, ctx.ConsumePauseRequest));
            return ctx;
        }

        /// <summary>Verbraucht eine evtl. ausstehende Pausier-Anfrage (siehe
        /// RequestPause) - `true` genau einmal pro RequestPause-Aufruf,
        /// danach wieder `false`, bis erneut angefragt wird. Öffentlich,
        /// damit DebugSession eigene, unterbrechbare Schritt-Funktionen
        /// (Continue/RunToCompletion) für einen BELIEBIGEN Thread bauen
        /// kann, nicht nur für den automatischen Fire-Thread-Lauf.</summary>
        public bool ConsumePauseRequest()
        {
            if (!_pauseRequested) return false;
            _pauseRequested = false;
            return true;
        }

        /// <summary>Bittet einen gerade laufenden Schritt (typischerweise
        /// "Weiter" oder "Bis Ende durchlaufen" - siehe MakeContinueStep/
        /// MakeRunToCompletionStep, beide prüfen das kooperativ), an der
        /// NÄCHSTEN Instruktion anzuhalten. `Step Line/Into/Out` selbst
        /// prüfen das NICHT (sie nutzen VM.StepLine/StepInto/StepOut direkt,
        /// deren interne Schleife dem Debugger nicht zugänglich ist) - ein
        /// einzelner Schritt sollte aber ohnehin fast immer schnell fertig
        /// sein, außer bei einer Endlosschleife auf EINER einzigen
        /// Quelltextzeile (bekannte, hingenommene Einschränkung).</summary>
        public void RequestPause() => _pauseRequested = true;

        /// <summary>Läuft auf dem EIGENEN Hintergrund-Thread dieses Kontexts
        /// (siehe Konstruktor) - wartet abwechselnd (per _resumeGate) und
        /// führt den jeweils angeforderten Schritt aus, bis das Programm
        /// beendet ist.</summary>
        private void RunLoop()
        {
            while (true)
            {
                _resumeGate.Wait();
                if (_abandoned)
                {
                    // Kein noch ausstehender Schritt wird mehr ausgeführt -
                    // egal, ob gerade einer anlag oder nicht, dieser Thread
                    // endet jetzt (siehe Abandon-Doku).
                    IsFinished = true;
                    Paused?.Invoke(this);
                    return;
                }

                var step = _pendingStep;
                if (step != null) RunStepNow(step);

                // Abandon() kann WÄHREND RunStepNow lief gesetzt worden sein
                // (der Thread war beschäftigt, nicht wartend) - der eigentliche
                // step selbst weiß davon nichts und könnte "more work"
                // zurückgegeben haben (z.B. weil RequestPause ihn nur
                // UNTERBROCHEN, nicht beendet hat) - IsFinished hier notfalls
                // erzwingen, sonst würde die Schleife gleich wieder auf das
                // Gate warten, ohne dass je wieder jemand es öffnet.
                if (_abandoned) IsFinished = true;

                Paused?.Invoke(this);
                if (IsFinished) return;
            }
        }

        private void RunStepNow(Func<VM, bool> step)
        {
            try
            {
                bool more = step(Vm);
                IsFinished = !more;

                // Seit VM.UnhandledException (siehe dort) wirft eine
                // unbehandelte Skript-Exception NICHT mehr - sie muss hier
                // explizit geprüft werden, sonst würde der Debugger den
                // Thread einfach als "fertig, kein Fehler" anzeigen, obwohl
                // das Skript tatsächlich mit einer nicht gefangenen Exception
                // abgebrochen ist. Reine Formatierungs-Bequemlichkeit über
                // UncaughtScriptException (die selbst nicht mehr geworfen
                // wird, siehe dort) für dieselbe Fehlermeldung wie vorher.
                if (Vm.UnhandledException != null)
                    RuntimeError = new UncaughtScriptException(Vm.UnhandledException).Message;
            }
            catch (Exception ex)
            {
                // Absichtlich breit gefangen (wie DebugSession.RunGuarded
                // schon immer für den Single-Thread-Fall) - jeder interne
                // VM-Fehler (z.B. ein VmInvariantViolationException-Bug)
                // soll hier als klare Fehlermeldung landen, statt den Thread
                // (und damit potenziell die ganze Anwendung, falls
                // unbeobachtet) mitzureißen. Eine unbehandelte SKRIPT-
                // Exception läuft dagegen über UnhandledException oben, nicht
                // mehr über diesen catch-Zweig.
                Debug.WriteLine($"{ex.Message}\r\n{ex.StackTrace}");
                RuntimeError = ex.Message;
                IsFinished = true;
            }
            _pendingStep = null; // nach diesem Lauf: NICHT automatisch weiter, sondern auf die nächste explizite Anweisung warten
        }

        /// <summary>Fordert einen einzelnen Schritt an (Step Line/Into/Out,
        /// Continue, RunToCompletion, ... - siehe DebugSession für die
        /// konkreten step-Funktionen) - FIRE-AND-FORGET, kehrt SOFORT
        /// zurück, ohne auf den Abschluss zu warten (siehe Klassenkommentar
        /// für die Begründung). Das Ergebnis kommt über das Paused-Event.
        /// Wirkungslos, wenn dieser Thread bereits fertig ist.</summary>
        public void RequestStep(Func<VM, bool> step)
        {
            if (IsFinished) return;
            _pendingStep = step;
            _resumeGate.Release();
        }

        /// <summary>Löst einen Thread aus seiner aktuellen Warte-/Lauf-
        /// position, ohne auf dessen Abschluss zu warten (siehe
        /// DebugSession.Reset) - setzt ein dauerhaftes "abgebrochen"-Flag,
        /// das RunLoop bei der NÄCHSTEN Gelegenheit (egal ob der Thread
        /// gerade wartet oder mitten in einem Schritt steckt) ERZWUNGEN zu
        /// IsFinished macht - unabhängig davon, was ein gerade laufender
        /// Schritt selbst zurückgeben würde. `RequestPause` zusätzlich, damit
        /// ein GERADE laufendes Continue/RunToCompletion (siehe
        /// MakeContinueStep/MakeRunToCompletionStep) möglichst zeitnah
        /// überhaupt erst zu dieser Prüfung zurückkehrt, statt erst beim
        /// natürlichen Ende der Schleife. Ohne das dauerhafte Flag (frühere,
        /// fehlerhafte Fassung) konnte ein Thread, der GENAU WÄHREND des
        /// Abandon-Aufrufs beschäftigt war, durch das Pausieren zwar
        /// kurz anhalten, dabei aber `more=true` (nicht fertig)
        /// zurückgeben und danach für immer auf das Gate warten - genau das
        /// Leck, das Abandon eigentlich verhindern soll. Ein bereits mitten
        /// in einem NICHT unterbrechbaren Schritt (Step Line/Into/Out)
        /// steckender Thread endet trotzdem erst, sobald DIESER Schritt von
        /// selbst fertig wird (bekannte, hingenommene Einschränkung, siehe
        /// RequestPause-Doku) - danach greift das Flag aber zuverlässig.</summary>
        public void Abandon()
        {
            if (IsFinished) return;
            _abandoned = true;
            RequestPause();
            _resumeGate.Release();
        }

        /// <summary>Wie VM.Continue, aber zusätzlich kooperativ unterbrechbar
        /// durch RequestPause/ConsumePauseRequest - Grundlage sowohl für den
        /// automatischen "Weiter"-Lauf, mit dem jeder Fire-Thread
        /// standardmäßig startet, als auch für einen expliziten "Weiter"-
        /// Knopfdruck auf einem BELIEBIGEN Thread (siehe DebugSession.
        /// Continue).</summary>
        public static Func<VM, bool> MakeContinueStep(ISet<(int SourceIndex, int Line)> breakpoints, Func<bool> isPauseRequested) =>
            vm => vm.RunUntilBreakpoint(breakpoints, isPauseRequested);

        /// <summary>Wie VM.StepInstruction in einer Schleife bis zum
        /// Programmende, aber ebenfalls kooperativ unterbrechbar (siehe
        /// MakeContinueStep-Doku) - Grundlage für "Bis Ende durchlaufen".</summary>
        public static Func<VM, bool> MakeRunToCompletionStep(Func<bool> isPauseRequested) =>
            vm => vm.RunUntilEnd(isPauseRequested);
    }
}
