using System;
using System.Collections.Generic;
using System.Linq;
using fire.Ast;
using fire.Runtime;
using fire.Values;

namespace fire.Bytecode
{
    /// <summary>Ein Eintrag im Aufruf-Stack: alles, was beim RETURN
    /// wiederhergestellt werden muss, um beim Aufrufer genau dort weiterzumachen,
    /// wo der Aufruf stattfand. ConstructedInstance ist nur bei NewObject/
    /// NewObjectOwned gesetzt: dort soll RETURN nicht den (verworfenen)
    /// Rückgabewert des Konstruktors pushen, sondern die neu erzeugte Instanz.</summary>
    internal readonly struct CallFrame
    {
        public readonly Chunk ReturnChunk;
        public readonly int ReturnIp;
        public readonly Scope ReturnScope;
        public readonly object? ReturnThis;
        public readonly ObjectInstance? ConstructedInstance;

        public CallFrame(Chunk returnChunk, int returnIp, Scope returnScope, object? returnThis, ObjectInstance? constructedInstance)
        {
            ReturnChunk = returnChunk;
            ReturnIp = returnIp;
            ReturnScope = returnScope;
            ReturnThis = returnThis;
            ConstructedInstance = constructedInstance;
        }
    }

    /// <summary>
    /// Führt einen Chunk aus. Bewusst simpel gehalten: eine Stack-Maschine mit
    /// einer flachen switch-Anweisung über die Opcodes (in <see cref="Execute"/>)
    /// - jede Instruktion ist ein kleiner, abgeschlossener Schritt, der sich 1:1
    /// in eine kurze Sequenz nativer Instruktionen übersetzen lassen soll (das
    /// war die Vorgabe für den späteren "Bytecode -> nativer Code"-Schritt).
    ///
    /// Implementiert selbst IDestructRunner: wenn die Ownership-Kaskade
    /// (Scope.Release/ObjectInstance.Destroy, siehe Runtime-Schicht) mitten in
    /// einem ExitScope/Return-Opcode einen destruct()-Body ausführen muss,
    /// geschieht das über <see cref="RunNestedUntil"/> - eine VERSCHACHTELTE,
    /// aber mit derselben Instruktionsschleife und demselben Frame-Stack
    /// arbeitende Ausführung, die erst zurückkehrt, sobald der Destruktor-Frame
    /// (und alles, was er selbst noch aufgerufen hat) wieder abgebaut ist. Das
    /// vermeidet das Reentrancy-Problem: der äußere Opcode-Handler (ExitScope)
    /// sieht danach wieder konsistenten Zustand (_currentScope etc.), weil die
    /// verschachtelte Ausführung erst zurückkehrt, wenn genau das der Fall ist.
    ///
    /// 'this' (_currentThis) ist entweder eine ObjectInstance (Methoden-/
    /// Konstruktor-/Feld-Init-/Destruktor-Ausführung) oder ein geboxter Value
    /// (Lambda mit 'on'-Bindung auf einen Nicht-Objekt-Wert) oder null.
    /// </summary>
    /// <summary>Ein aktiv registrierter try-Handler (VM-Laufzeit-Gegenstück zu
    /// HandlerTemplate): Frame-Tiefe + Ziel-Scope zum Zeitpunkt der
    /// Registrierung, plus Referenz auf die kompilierten Catch-/Finally-Daten.</summary>
    internal readonly struct ActiveHandler
    {
        public readonly Chunk Chunk;
        public readonly int FrameDepthAtEntry;
        public readonly Scope TargetScope;
        public readonly HandlerTemplate Template;

        public ActiveHandler(Chunk chunk, int frameDepthAtEntry, Scope targetScope, HandlerTemplate template)
        {
            Chunk = chunk;
            FrameDepthAtEntry = frameDepthAtEntry;
            TargetScope = targetScope;
            Template = template;
        }
    }

    /// <summary>Der beim Werfen "eingefrorene" Ausführungszustand an der
    /// Wurfstelle selbst (nicht am Handler!) - für ein mögliches späteres
    /// `resume()`. Entspricht genau dem, was `CallFrame` für einen normalen
    /// Aufruf festhält, nur zusätzlich mit den POPPED (aber NICHT per
    /// Release() zerstörten) Frames zwischen Wurfstelle und Handler.</summary>
    internal sealed class SavedContinuation
    {
        public readonly Chunk Chunk;
        public readonly int Ip;
        public readonly Scope Scope;
        public readonly object? This;
        public readonly List<CallFrame> Frames;

        public SavedContinuation(Chunk chunk, int ip, Scope scope, object? thisObj, List<CallFrame> frames)
        {
            Chunk = chunk;
            Ip = ip;
            Scope = scope;
            This = thisObj;
            Frames = frames;
        }
    }

    /// <summary>Eine Exception, die gerade in einem `catch` behandelt wird und
    /// noch per `resume()` fortgesetzt werden könnte - solange dieser Eintrag
    /// existiert, ist SavedContinuation "am Leben" (nichts davon wurde
    /// freigegeben). HandlerFrameDepthAtEntry/TargetScope werden gebraucht, um
    /// beim tatsächlichen `resume()`-Aufruf den GERADE LAUFENDEN catch-Kontext
    /// sauber abzuwickeln (per UnwindTo, genau wie beim Handler-Einstieg
    /// selbst) - auch wenn `resume()` aus einem verschachtelten Funktionsaufruf
    /// INNERHALB des catch-Blocks heraus aufgerufen wird. Handler wird
    /// zusätzlich mitgeführt, um ihn beim resume() wieder scharf zu schalten -
    /// die fortgesetzte Wurfstelle ist ja konzeptionell "immer noch im
    /// try-Block", ein erneuter throw darin muss wieder denselben catch
    /// erreichen können, und `UnregisterHandler` am Ende des try-Blocks
    /// erwartet, dass sein Eintrag beim normalen Durchlauf noch da ist.</summary>
    internal sealed class PendingResume
    {
        public readonly SavedContinuation Continuation;
        public readonly ActiveHandler Handler;

        public PendingResume(SavedContinuation continuation, ActiveHandler handler)
        {
            Continuation = continuation;
            Handler = handler;
        }
    }

    public sealed class VM : IDestructRunner
    {
        private Chunk _currentChunk;
        private readonly Scope _globalScope;
        private readonly NativeRegistry _natives;
        private readonly ExternRegistry _externs;
        private readonly IReadOnlyDictionary<string, RuntimeClass> _classes;
        private readonly IReadOnlyDictionary<string, ExternSignature> _externSignatures;

        private readonly List<Value> _stack = new();
        private readonly Stack<CallFrame> _frames = new();
        private readonly List<ActiveHandler> _handlers = new();
        private readonly Dictionary<ObjectInstance, PendingResume> _pendingResumes = new();

        // Caches fürs dynamische extern-Linking (siehe CallExtern/
        // ResolveDynamicExtern) - eine Bibliothek wird nur EINMAL geladen
        // (NativeLibrary.Load ist nicht ganz billig), ein Delegate nur einmal
        // pro extern-Namen gebaut (Reflection/MakeGenericType ebenfalls).
        private readonly Dictionary<string, IntPtr> _loadedNativeLibraries = new();
        private readonly Dictionary<string, Delegate> _dynamicExternDelegates = new();

        private Scope _currentScope;
        private object? _currentThis;
        private int _ip;

        // -----------------------------------------------------------
        // leave/terminate (docs/THREADING_DESIGN.md Abschnitt 6) - kooperative
        // Prüfpunkte statt echter Unterbrechung: jede laufende VM-Instanz
        // bemerkt ein Signal spätestens an der nächsten Instruktion (siehe
        // Run()) und wickelt sich dann selbst sauber ab (UnwindForShutdown),
        // OHNE dabei irgendeinen normalen `catch`/`catch(e)` zu durchlaufen -
        // beide Signale werden absichtlich NIE gegen HandlerTemplate.Catches
        // geprüft, sie laufen nur durch etwaige `finally`-Blöcke hindurch.
        // -----------------------------------------------------------

        /// <summary>Nur für DIESE eine VM-Instanz (== diesen einen Thread) -
        /// `leave` betrifft ausschließlich den Thread, der es aufruft.</summary>
        private bool _leaveRequested;

        /// <summary>Über ALLE VM-Instanzen/Threads hinweg geteilt (`terminate`
        /// ist ein globaler Not-Aus) - `volatile`, da von JEDEM Thread aus
        /// gesetzt und von JEDEM Thread an seinem eigenen Prüfpunkt gelesen
        /// wird. "Erster Aufruf gewinnt" (THREADING_DESIGN.md 6.3) wird über
        /// Interlocked.CompareExchange in RequestTerminate sichergestellt -
        /// dieses Feld selbst wird deshalb nur EINMAL, vom Gewinner, auf
        /// einen Wert ungleich 0 gesetzt.</summary>
        private static volatile bool _terminateRequested;
        private static Value _terminateValue = Value.MakeUndefined();
        private static readonly object _terminateGate = new();

        /// <summary>Der an `terminate(wert)` übergebene Wert, nach vollständig
        /// synchronisiertem Herunterfahren ALLER Threads auslesbar (siehe
        /// THREADING_DESIGN.md 6.3) - `undefined`, falls das Programm ohne
        /// `terminate` reguär durchgelaufen ist. Das "Warten, bis alle Threads
        /// fertig sind" ist Aufgabe des Hosts (siehe FireThreadHandle.Join in
        /// den Program.cs-Tests) - die VM selbst kann das nicht erzwingen, sie
        /// stellt nur sicher, dass IHRE EIGENE Abwicklung (inkl. finally)
        /// abgeschlossen ist, bevor Run() zurückkehrt.</summary>
        public static Value ExitValue => _terminateValue;

        /// <summary>Merkt sich für DIESEN Thread, dass beim nächsten Prüfpunkt
        /// `leave` ausgelöst werden soll (siehe VM.CurrentThreadVm für die
        /// Zuordnung "welche VM-Instanz gehört zum aufrufenden Thread" -
        /// Grundlage einer nativen Brücken-Funktion wie `__leave()`, siehe
        /// Program.cs-Test, solange es noch keine echte `leave`-Sprachsyntax
        /// gibt).</summary>
        public void RequestLeave() => _leaveRequested = true;

        /// <summary>Globaler Not-Aus (siehe THREADING_DESIGN.md 6.3) - "erster
        /// Aufruf gewinnt", alle weiteren werden zu No-Ops.</summary>
        public static void RequestTerminate(Value value)
        {
            lock (_terminateGate)
            {
                if (_terminateRequested) return;
                _terminateValue = value;
                _terminateRequested = true;
            }
        }

        /// <summary>Nur für Tests/eine frische Programmausführung gedacht -
        /// setzt das GLOBALE terminate-Signal zurück. Multithreading-
        /// Not-Aus ist bewusst "erster Aufruf gewinnt, endgültig" (siehe
        /// RequestTerminate) - dieser Reset existiert NICHT für die normale
        /// Sprachsemantik, sondern rein damit mehrere, voneinander
        /// unabhängige Programmläufe (wie unsere Program.cs-Tests) einander
        /// nicht über das statische Feld hinweg beeinflussen.</summary>
        public static void ResetTerminateForTests()
        {
            lock (_terminateGate)
            {
                _terminateRequested = false;
                _terminateValue = Value.MakeUndefined();
            }
        }

        /// <summary>Die VM-Instanz, die AKTUELL auf dem aufrufenden Thread
        /// läuft (von Run() beim Start gesetzt) - Grundlage dafür, dass eine
        /// native Brücken-Funktion (die selbst keinen VM-Zugriff hat, siehe
        /// NativeFunction-Delegate) trotzdem `RequestLeave()` auf der
        /// RICHTIGEN (der eigenen) VM-Instanz aufrufen kann, ohne dass die
        /// Sprache selbst schon eine `leave`-Syntax bräuchte.</summary>
        [ThreadStatic]
        private static VM? _currentThreadVm;

        public static VM? CurrentThreadVm => _currentThreadVm;

        /// <summary>Markiert GENAU eine VM-Instanz im ganzen Programm als "die"
        /// Main-Thread-Instanz - Grundlage für `catch threads(...)`/`catch
        /// terminate(v)` (docs/THREADING_DESIGN.md 6.2/6.3), die laut Design
        /// AUSSCHLIESSLICH dort laufen, nicht auf irgendeinem Fire-Thread.
        /// Muss vom Aufrufer explizit gesetzt werden (kein automatisches
        /// "erste erzeugte VM ist die Haupt-VM" - das wäre bei Tests, die
        /// mehrere VMs unabhängig voneinander laufen lassen, fragil).</summary>
        public bool IsMainThreadVm { get; }

        /// <summary>Explizit gesetzt NUR von FireRuntime.FireVm (siehe dort) -
        /// bewusst NICHT einfach "!IsMainThreadVm": die meisten VM-Instanzen
        /// im gesamten restlichen Code (jeder einfache Einzel-VM-Testlauf,
        /// jede VM ohne jeden Multithreading-Bezug) setzen `isMainThreadVm`
        /// nie und sind trotzdem KEIN Fire-Thread - eine unbehandelte
        /// Exception dort muss weiterhin ganz normal über UnhandledException
        /// signalisiert werden (das ursprüngliche, überall vorausgesetzte
        /// Verhalten), nicht still in die globale Fire-Thread-Warteschlange
        /// umgeleitet werden. Nur eine VM-Instanz, die WIRKLICH über `fire`
        /// entstanden ist, soll dieses besondere Verhalten bekommen.</summary>
        public bool IsFireThreadVm { get; }

        /// <summary>Eine Skript-`throw`, für die in DIESER VM-Instanz kein
        /// passender `catch` gefunden wurde - gesetzt statt geworfen (siehe
        /// ThrowException, letzter Zweig): bewusst KEINE C#-Exception mehr
        /// über die Run()-Aufrufstelle hinaus (siehe docs/PORTING.md,
        /// Abschnitt "VM-interner Kontrollfluss") - in einer C++-Fassung
        /// ohne Exceptions (üblich auf Embedded-Targets) gäbe es dafür
        /// ohnehin keine Entsprechung. Run() kehrt in diesem Fall ganz
        /// normal zurück (siehe CheckShutdownSignals/_stopExecutionRequested);
        /// der Aufrufer prüft nach Run() dieses Feld, statt einen `try`/
        /// `catch` um den Aufruf zu legen. `null` bedeutet "kein unbehandelter
        /// Fehler" (der Normalfall). Eine reine C#-Bequemlichkeit für
        /// Host-Code, der lieber mit einer echten Exception arbeitet, bleibt
        /// über Bytecode.UncaughtScriptException möglich - die KONSTRUIERT
        /// (aber nicht mehr intern geworfen) werden kann, z.B.
        /// `throw new UncaughtScriptException(vm.UnhandledException)`.</summary>
        public ObjectInstance? UnhandledException { get; private set; }

        /// <summary>Über ALLE VM-Instanzen/Threads hinweg geteilte Warteschlange
        /// für unbehandelte Fire-Thread-Exceptions (siehe ThrowException),
        /// vom Main-Thread an seinem nächsten Prüfpunkt abgearbeitet (siehe
        /// CheckShutdownSignals/HandleDeliveredThreadException) - thread-sicher
        /// per ConcurrentQueue, da mehrere Fire-Threads gleichzeitig werfen
        /// können.</summary>
        private static readonly System.Collections.Concurrent.ConcurrentQueue<ObjectInstance> _pendingThreadExceptions = new();

        /// <summary>Gesetzt, wenn DIESE VM-Instanz ihre Abwicklung (Unwind
        /// inkl. finally) bereits SELBST durchgeführt hat (siehe
        /// ThrowException's Fire-Thread-Zweig) und beim nächsten Prüfpunkt in
        /// Run() nur noch sauber STOPPEN muss, ohne den (möglicherweise
        /// inzwischen bedeutungslosen) `_ip`/`_currentChunk`-Zustand
        /// weiterzuverwenden - anders als bei `_leaveRequested`/
        /// `_terminateRequested`, wo CheckShutdownSignals selbst den Unwind
        /// noch durchführt.</summary>
        private bool _stopExecutionRequested;

        public VM(
            Chunk chunk,
            Scope globalScope,
            NativeRegistry natives,
            IReadOnlyDictionary<string, RuntimeClass>? classes = null,
            ExternRegistry? externs = null,
            IReadOnlyDictionary<string, ExternSignature>? externSignatures = null,
            bool isMainThreadVm = false,
            bool isFireThreadVm = false,
            VmExecutionMode executionMode = VmExecutionMode.Debug)
        {
            _currentChunk = chunk;
            _globalScope = globalScope;
            _natives = natives;
            _classes = classes ?? new Dictionary<string, RuntimeClass>();
            _externs = externs ?? new ExternRegistry();
            _externSignatures = externSignatures ?? new Dictionary<string, ExternSignature>();
            _currentScope = globalScope;
            IsMainThreadVm = isMainThreadVm;
            IsFireThreadVm = isFireThreadVm;
            ExecutionMode = executionMode;
        }

        /// <summary>Siehe VmExecutionMode-Doku - steuert u.a. wie oft
        /// CheckShutdownSignals läuft (ShutdownCheckInterval) und ob
        /// ArrayGet/ArraySet/Puffer-Zugriffe ihre Bounds-Prüfung überspringen
        /// (siehe die jeweiligen Opcode-Handler).</summary>
        public VmExecutionMode ExecutionMode { get; }

        /// <summary>Alle wie viele Instruktionen CheckShutdownSignals in
        /// Run() tatsächlich läuft (siehe dort) - 1 bedeutet "vor jeder
        /// Instruktion" (Debug, unverändertes bisheriges Verhalten). Reine
        /// Zahlenwerte statt eines Schaltverhaltens pro Fall, damit Run()
        /// selbst einfach bleibt (ein Modulo-Vergleich statt einer
        /// Fallunterscheidung nach ExecutionMode in der heißesten Schleife
        /// der gesamten VM).</summary>
        private int ShutdownCheckInterval => ExecutionMode switch
        {
            VmExecutionMode.Debug => 1,
            VmExecutionMode.Release => 64,
            VmExecutionMode.Performance => 4096,
            _ => 1,
        };

        private long _instructionsSinceShutdownCheck;

        public void Run()
        {
            _currentThreadVm = this;
            _ip = 0;
            int interval = ShutdownCheckInterval;
            while (true)
            {
                if (interval <= 1 || ++_instructionsSinceShutdownCheck >= interval)
                {
                    _instructionsSinceShutdownCheck = 0;
                    if (CheckShutdownSignals()) return;
                }
                var op = (OpCode)ReadByte();
                if (op == OpCode.Halt) return;
                Execute(op);
            }
        }

        /// <summary>Der kooperative Prüfpunkt für `leave`/`terminate` (siehe
        /// Feld-Doku oben) - bewusst vor JEDER einzelnen Instruktion geprüft
        /// (nicht nur bei Funktionsaufrufen/Schleifen-Rücksprüngen), das ist
        /// die einfachste, garantiert korrekte Variante ("verpasst" nie ein
        /// Signal) - eine spätere Optimierung könnte das auf seltenere,
        /// dafür strukturell sinnvollere Punkte einschränken, falls der
        /// Overhead je relevant werden sollte.</summary>
        private bool CheckShutdownSignals()
        {
            // Bereits SELBST abgewickelt (siehe ThrowException) - nur noch
            // sauber stoppen, der aktuelle _ip/_currentChunk-Zustand ist ab
            // hier bedeutungslos und wird nicht mehr verwendet.
            if (_stopExecutionRequested) return true;

            // Nur der Main-Thread verarbeitet zugestellte Fire-Thread-
            // Exceptions (siehe HandleDeliveredThreadException) - läuft dabei
            // GENESTET (wie RunFinallyNested), der Main-Thread macht danach
            // ganz normal weiter, wird also NICHT gestoppt.
            if (IsMainThreadVm)
                while (_pendingThreadExceptions.TryDequeue(out var excInstance))
                {
                    HandleDeliveredThreadException(excInstance);
                    if (_stopExecutionRequested) return true;
                }

            if (_terminateRequested)
            {
                UnwindForShutdown();
                if (IsMainThreadVm) RunTerminateHandlerIfAny();
                return true;
            }
            if (_leaveRequested)
            {
                _leaveRequested = false;
                UnwindForShutdown();
                return true;
            }
            return false;
        }

        /// <summary>Wickelt den GESAMTEN aktuellen Zustand DIESER VM-Instanz
        /// sauber ab: erst alle noch aktiven try-Handler (in der üblichen
        /// Reihenfolge, siehe ThrowException), dabei aber - anders als bei
        /// einer echten Exception - NIE einen `catch` matchen (jeder Handler
        /// wird also wie "kein passender catch" behandelt), sein `finally`
        /// läuft aber ganz normal. Danach können noch Frames/Scopes OHNE
        /// eigenes try/finally aktiv sein (ein einfacher Methodenaufruf ohne
        /// try-Block) - die werden abschließend bis zur Basis (globaler
        /// Scope, Frame-Tiefe 0) reguär abgewickelt (Release() pro Scope,
        /// inkl. Destruktor-Kaskade), nur ohne weiteres finally (da keins
        /// mehr registriert ist). Gemeinsame Grundlage für `leave` (nur
        /// diese eine VM-Instanz) und `terminate` (jede VM-Instanz bemerkt
        /// das globale Signal an ihrem eigenen nächsten Prüfpunkt und wickelt
        /// sich GENAUSO ab - nur die Auslösung unterscheidet sich).</summary>
        private void UnwindForShutdown()
        {
            while (_handlers.Count > 0)
            {
                var handler = _handlers[^1];
                _handlers.RemoveAt(_handlers.Count - 1);

                UnwindTo(handler.FrameDepthAtEntry, handler.TargetScope);
                if (handler.Template.FinallyProtoIdx is int protoIdx)
                    RunFinallyNested(handler.Chunk.Functions[protoIdx]);
            }

            UnwindTo(0, _globalScope);
        }

        /// <summary>Führt einen zugestellten, unbehandelten Fire-Thread-
        /// Exception-Handler (`catch threads(...)`) genestet aus - wie
        /// RunFinallyNested: läuft, DANACH macht der Main-Thread an exakt der
        /// unterbrochenen Stelle normal weiter (kein Stoppen, anders als bei
        /// leave/terminate). Kein passender Handler registriert -> kompletter
        /// Programmabbruch, wie eine unbehandelte Exception im Main-Thread
        /// selbst (docs/THREADING_DESIGN.md 6.2).</summary>
        private void HandleDeliveredThreadException(ObjectInstance excInstance)
        {
            var handlerProto = FindGlobalThreadsCatch(excInstance);
            if (handlerProto == null)
            {
                // Kompletter Programmabbruch - gesetzt statt geworfen, siehe
                // UnhandledException-Doku. Der Aufrufer (CheckShutdownSignals)
                // MUSS danach sofort stoppen, statt evtl. weitere in der
                // Warteschlange stehende Exceptions noch zu verarbeiten.
                UnhandledException = excInstance;
                _stopExecutionRequested = true;
                return;
            }

            _frames.Push(new CallFrame(_currentChunk, _ip, _currentScope, _currentThis, null));
            int targetDepth = _frames.Count;

            var handlerScope = new Scope(_globalScope);
            if (handlerProto.ParamCount >= 1)
                handlerScope.DefineSlot(Value.MakeClassRef(excInstance));

            _currentThis = null;
            _currentScope = handlerScope;
            _currentChunk = handlerProto.Chunk;
            _ip = 0;

            RunNestedUntil(targetDepth);
            Pop(); // Rückgabewert des Handler-Protos unbenutzt, wie RunFinallyNested/RunDestructor.
        }

        /// <summary>`catch threads(...)`-Auflösung - exakt wie FindMatchingCatch
        /// bei einem normalen try/catch (erster Treffer gewinnt, `typeName ==
        /// null` matcht alles, siehe InstanceMatchesClassName für 'Exception'
        /// als Sonderfall), nur gegen die GLOBALE Registrierung statt gegen
        /// ein einzelnes HandlerTemplate.</summary>
        private FunctionProto? FindGlobalThreadsCatch(ObjectInstance exc)
        {
            foreach (var (typeName, proto) in GlobalHandlers.ThreadsCatches)
            {
                if (typeName == null) return proto;
                if (InstanceMatchesClassName(exc, typeName)) return proto;
            }
            return null;
        }

        /// <summary>Führt `catch terminate(v)` genestet aus, falls registriert
        /// - anders als HandleDeliveredThreadException MUSS hier NICHTS
        /// "danach normal weitermachen", da der Aufrufer (CheckShutdownSignals)
        /// direkt im Anschluss ohnehin `true` (= Run() soll stoppen)
        /// zurückgibt - der hier gepushte Rücksprung-Frame wird also nie
        /// wirklich gebraucht, ist aber nötig, damit RunNestedUntil überhaupt
        /// weiß, wann der Handler-Aufruf per Return beendet ist.</summary>
        private void RunTerminateHandlerIfAny()
        {
            var proto = GlobalHandlers.TerminateHandler;
            if (proto == null) return;

            _frames.Push(new CallFrame(_currentChunk, _ip, _currentScope, _currentThis, null));
            int targetDepth = _frames.Count;

            var handlerScope = new Scope(_globalScope);
            if (proto.ParamCount >= 1)
                handlerScope.DefineSlot(_terminateValue);

            _currentThis = null;
            _currentScope = handlerScope;
            _currentChunk = proto.Chunk;
            _ip = 0;

            RunNestedUntil(targetDepth);
            Pop();
        }

        /// <summary>`process X`/`try process X` (siehe Ast.ProcessStmt/
        /// TryProcessExpr-Doku) - holt GENAU EINE Nachricht aus der Mailbox
        /// des Actors (siehe Runtime.ActorMailbox.TryProcessOne) und führt
        /// die passende Methode GENESTET aus (wie CallMethodNested-Muster:
        /// `this` = der Actor, Rückgabewert unbenutzt - eine Nachricht hat
        /// kein Ergebnis, das irgendwer abholen könnte). Liefert `false` nur
        /// im nicht-blockierenden Fall, wenn die Mailbox gerade leer ist -
        /// blockierend liefert diese Methode immer `true` (wartet ja, bis
        /// etwas da ist).</summary>
        private bool ProcessOneMessage(ObjectInstance actor, bool blocking)
        {
            var mailbox = actor.Mailbox
                ?? throw new InvalidOperationException(
                    $"'process'/'try process' auf einer Instanz von '{actor.ClassDef.Name}', die kein Actor ist.");

            if (!mailbox.TryProcessOne(blocking, out var message))
                return false;

            var rc = ResolveClass(actor.ClassDef.Name);
            var proto = rc.FindMethod(message.MethodName, message.Args.Length)
                ?? throw new InvalidOperationException(
                    DescribeMethodNotFound(rc, message.MethodName, message.Args.Length));
            CheckArity(proto, message.Args.Length);
            var args = FillDefaultArgs(proto, message.Args, actor);

            _frames.Push(new CallFrame(_currentChunk, _ip, _currentScope, _currentThis, null));
            int targetDepth = _frames.Count;

            var scope = new Scope(_globalScope);
            foreach (var a in args) scope.DefineSlot(a);

            _currentThis = actor;
            _currentScope = scope;
            _currentChunk = proto.Chunk;
            _ip = 0;

            RunNestedUntil(targetDepth);
            Pop(); // Rückgabewert unbenutzt, siehe Doku oben.
            return true;
        }

        // -----------------------------------------------------------
        // Stepping-/Inspektions-API für externe Werkzeuge (Step-Debugger im
        // Editor-Unterprojekt) - Run() bleibt für den normalen "einfach
        // durchlaufen"-Fall unverändert, das hier ist ein ALTERNATIVER,
        // Schritt-für-Schritt-fähiger Einstieg in dieselbe Execute()-Schleife.
        // -----------------------------------------------------------

        private bool _steppingStarted;

        public bool IsHalted { get; private set; }

        /// <summary>Aktuelle Quelltextzeile an der Ausführungsposition (per
        /// Chunk-Zeilentabelle, siehe Chunk.MarkLine) - 0, wenn keine
        /// Information vorhanden ist (z.B. programmatisch gebaute Chunks ohne
        /// Compiler-Lauf). Für die Zeilen-Hervorhebung im Editor.</summary>
        public int CurrentLine => _currentChunk.GetLine(_ip);

        /// <summary>Führt GENAU eine Instruktion aus - Gegenstück zu Run()s
        /// Schleifenkörper, nur einzeln aufrufbar mit explizitem Halt-Status
        /// statt einer Endlosschleife. Der erste Aufruf initialisiert `_ip`
        /// wie Run() das auch tut. Liefert false, sobald das Programm beendet
        /// ist (danach bleibt jeder weitere Aufruf ein No-op mit false).</summary>
        public bool StepInstruction()
        {
            if (IsHalted) return false;
            if (!_steppingStarted)
            {
                // Wie Run() (siehe dort) - _currentThreadVm muss auch beim
                // schrittweisen Debuggen korrekt auf DIESE Instanz zeigen,
                // sonst würde jede native Brücke, die sich darauf verlässt
                // (siehe CurrentThreadVm-Doku), beim Einzelschritt-Debuggen
                // fälschlich null sehen, obwohl eindeutig EINE VM-Instanz
                // gerade aktiv ist.
                _currentThreadVm = this;
                _ip = 0;
                _steppingStarted = true;
            }

            var op = (OpCode)ReadByte();
            if (op == OpCode.Halt)
            {
                IsHalted = true;
                return false;
            }
            Execute(op);

            // Eine unbehandelte Skript-Exception wird seit UnhandledException
            // (siehe dort) nicht mehr geworfen, sondern nur noch GESETZT -
            // Run() bemerkt das über CheckShutdownSignals (hier bewusst NICHT
            // aufgerufen, siehe Feld-Doku), beim schrittweisen Debuggen muss
            // das deshalb HIER explizit geprüft werden, sonst würde
            // StepLine/StepInto/StepOut/Continue (alle bauen auf dieser
            // Methode auf) einfach immer weiterlaufen, als wäre nichts
            // passiert, statt sauber zu stoppen.
            if (_stopExecutionRequested)
            {
                IsHalted = true;
                return false;
            }

            return true;
        }

        /// <summary>Führt Instruktionen aus, bis entweder die aktuelle
        /// Quelltextzeile wechselt (Ziel wieder auf derselben oder einer
        /// FLACHEREN Aufruf-Tiefe - "Step Over", läuft also nicht in tiefer
        /// verschachtelte Aufrufe hinein) oder das Programm beendet ist. Für
        /// den "Step"-Knopf im Editor.</summary>
        public bool StepLine()
        {
            int startLine = CurrentLine;
            int startDepth = _frames.Count;
            while (StepInstruction())
            {
                if (_frames.Count <= startDepth && CurrentLine != startLine)
                    return true;
            }
            return false;
        }

        /// <summary>Wie StepLine ("Step Over"), springt bei einem Aufruf aber
        /// HINEIN statt ihn zu überspringen ("Step Into") - stoppt bei JEDEM
        /// Wechsel der aktuellen Zeile ODER Aufruf-Tiefe. Ein Aufruf einer
        /// nativen Funktion (z.B. `print(...)`) hat dabei nichts zum
        /// "Hineinspringen" (keine eigene Chunk/Zeile) - verhält sich für den
        /// Debugger dann automatisch wie ein normaler Schritt.</summary>
        public bool StepInto()
        {
            int startLine = CurrentLine;
            int startDepth = _frames.Count;
            while (StepInstruction())
            {
                if (CurrentLine != startLine || _frames.Count != startDepth)
                    return true;
            }
            return false;
        }

        /// <summary>Läuft, bis die aktuelle Funktion/Methode/das aktuelle
        /// Lambda verlassen wurde (Aufruf-Tiefe fällt unter die
        /// Ausgangstiefe) - "Step Out", ergänzt Step Into/Over sinnvoll.</summary>
        public bool StepOut()
        {
            int startDepth = _frames.Count;
            if (startDepth == 0) return false; // schon auf Top-Level, nichts zu verlassen
            while (StepInstruction())
            {
                if (_frames.Count < startDepth)
                    return true;
            }
            return false;
        }

        /// <summary>Führt Instruktionen aus, bis entweder eine Zeile aus
        /// `breakpointLines` erreicht ist (nur beim WECHSEL der Zeile geprüft,
        /// damit ein Breakpoint auf der gerade verlassenen Zeile nicht sofort
        /// erneut triggert) oder das Programm beendet ist. Für den
        /// "Weiter"-Knopf im Editor.</summary>
        public bool Continue(ISet<int> breakpointLines)
        {
            int lastLine = CurrentLine;
            while (StepInstruction())
            {
                int line = CurrentLine;
                if (line != lastLine)
                {
                    lastLine = line;
                    if (breakpointLines.Contains(line)) return true;
                }
            }
            return false;
        }

        /// <summary>Unveränderlicher Blick auf den aktuellen Wert-Stack - rein
        /// zur Inspektion, keine Kopie (Werte selbst sind ohnehin unveränderliche
        /// Structs).</summary>
        public IReadOnlyList<Value> DebugStackSnapshot => _stack;

        /// <summary>Aktuelle Aufruf-Tiefe (Anzahl aktiver CallFrames) - für eine
        /// einfache Anzeige "wie tief verschachtelt bin ich gerade".</summary>
        public int DebugCallDepth => _frames.Count;

        /// <summary>Kurzbeschreibung des aktuell gebundenen `this` - null
        /// (kein Text), wenn an dieser Stelle kein `this` gebunden ist (z.B.
        /// Top-Level-Code oder ein Lambda ohne `on`-Bindung).</summary>
        public string? DebugThisDescription => _currentThis switch
        {
            null => null,
            ObjectInstance oi => $"{oi.ClassDef.Name}-Instanz",
            Value v => $"this (per 'on' gebunden) = {v}",
            _ => _currentThis.ToString(),
        };

        /// <summary>Wie DebugThisDescription, aber als echter Value statt nur
        /// einer Textbeschreibung - Grundlage für die aufklappbare Feld-
        /// Anzeige im Editor (siehe MainWindow.RefreshDebugPanels/
        /// BuildVariableTreeItem). Null unter denselben Bedingungen wie
        /// DebugThisDescription.</summary>
        public Value? DebugThisValue => _currentThis switch
        {
            null => null,
            ObjectInstance oi => Value.MakeClassRef(oi),
            Value v => v,
            _ => null,
        };

        /// <summary>Eine Ebene der Scope-Kette an der aktuellen Ausführungs-
        /// position, für eine strukturierte Scope-Ansicht im Editor (jede
        /// Ebene separat statt einer einzigen flachen Liste - macht sichtbar,
        /// welche Variablen zum GERADE AKTIVEN (innersten) Block gehören und
        /// welche aus einer umschließenden Ebene "durchgereicht" werden).
        /// Depth 0 = der aktuell innerste Scope (`_currentScope` selbst).</summary>
        public sealed record ScopeLevel(int Depth, bool IsFunctionTopLevel, IReadOnlyList<(string Name, Value Value)> Variables);

        /// <summary>Die komplette Scope-Kette der aktuellen Funktion/Methode/
        /// des aktuellen Lambdas, vom aktuell aktiven (innersten) Block bis zu
        /// deren Top-Level-Scope (danach kommt global, siehe DebugGlobals) -
        /// jede Ebene als eigener Eintrag, damit der Editor sie getrennt
        /// darstellen kann. Namen zuverlässig nur für Parameter der Top-
        /// Level-Ebene (siehe Chunk.DebugLocalNames-Kommentar), alles andere
        /// als "(local N)".</summary>
        public IReadOnlyList<ScopeLevel> DebugScopeChain()
        {
            var result = new List<ScopeLevel>();
            var scope = _currentScope;
            int depth = 0;
            while (scope != null && !scope.IsGlobal)
            {
                bool isFunctionTopLevel = scope.Parent != null && scope.Parent.IsGlobal;
                var vars = new List<(string, Value)>();
                for (int slot = 0; slot < scope.SlotCount; slot++)
                {
                    string name = isFunctionTopLevel && _currentChunk.DebugLocalNames.TryGetValue((0, slot), out var n)
                        ? n
                        : $"(local {slot})";
                    vars.Add((name, scope.GetSlot(slot)));
                }
                result.Add(new ScopeLevel(depth, isFunctionTopLevel, vars));
                scope = scope.Parent;
                depth++;
            }
            return result;
        }

        /// <summary>Globale Variablen - anders als bei Funktions-Parametern
        /// gibt es dafür (noch) kein Namens-Register (siehe Chunk.
        /// DebugLocalNames-Kommentar: dort wird bewusst nur die TOP-LEVEL-
        /// Scope einer FUNKTION erfasst, nicht der globale Top-Level-Code) -
        /// erscheinen deshalb komplett als "(global N)".</summary>
        public IEnumerable<(string Name, Value Value)> DebugGlobals()
        {
            for (int slot = 0; slot < _globalScope.SlotCount; slot++)
                yield return ($"(global {slot})", _globalScope.GetSlot(slot));
        }

        /// <summary>Führt Instruktionen aus, bis der Aufruf-Stack wieder unter
        /// <paramref name="targetFrameDepth"/> gefallen ist - also bis genau der
        /// Frame, der unmittelbar vor diesem Aufruf gepusht wurde (plus alles,
        /// was der ausgeführte Code selbst weiter aufgerufen hat), per RETURN
        /// wieder abgebaut ist. So lässt sich "ruf diesen einen Proto auf und
        /// warte synchron auf sein Return" mitten aus einem C#-Methodenaufruf
        /// heraus realisieren (für Destruktoren, siehe RunDestructor), ohne die
        /// Hauptschleife selbst rekursiv verschachteln zu müssen - Rekursion
        /// entsteht stattdessen ganz natürlich über verschachtelte C#-Aufrufe
        /// von RunDestructor selbst, falls ein Destruktor seinerseits weitere
        /// Destruktoren auslöst.</summary>
        private void RunNestedUntil(int targetFrameDepth)
        {
            while (_frames.Count >= targetFrameDepth)
            {
                var op = (OpCode)ReadByte();
                if (op == OpCode.Halt)
                    throw new InvalidOperationException(
                        "Unerwarteter Halt in verschachtelter Ausführung (z.B. während eines Destruktor-Aufrufs).");
                Execute(op);
            }
        }

        /// <summary>IDestructRunner: wird von Scope.Release/ObjectInstance.Destroy
        /// aufgerufen, wenn ein Objekt durch die Ownership-Kaskade zerstört wird.
        /// Führt den kompilierten destruct()-Body aus (falls die Klasse einen
        /// deklariert), mit 'this' = dem zu zerstörenden Objekt.</summary>
        public void RunDestructor(ObjectInstance instance)
        {
            var rc = ResolveClass(instance.ClassDef.Name);
            if (rc.Destructor == null) return;

            _frames.Push(new CallFrame(_currentChunk, _ip, _currentScope, _currentThis, null));
            int targetDepth = _frames.Count;

            var scope = new Scope(_globalScope);
            _currentThis = instance;
            _currentScope = scope;
            _currentChunk = rc.Destructor.Chunk;
            _ip = 0;

            RunNestedUntil(targetDepth);

            // Das abschließende RETURN des Destruktor-Bodys pusht seinen (hier
            // bedeutungslosen) Rückgabewert auf den Werte-Stack - anders als bei
            // einem normalen Call/CallMethod/etc. gibt es hier aber keinen
            // Ausdruckskontext, der ihn abholt. Ohne dieses Pop würde der Stack
            // bei jeder Destruktor-Ausführung um einen Wert "verwachsen".
            Pop();
        }

        private void Execute(OpCode op)
        {
            switch (op)
            {
                case OpCode.LoadConst:
                    Push(_currentChunk.Constants[ReadU16()]);
                    break;

                case OpCode.Pop:
                    Pop();
                    break;

                case OpCode.Dup:
                    Push(Peek());
                    break;

                case OpCode.Swap:
                {
                    var top = Pop();
                    var below = Pop();
                    Push(top);
                    Push(below);
                    break;
                }

                case OpCode.LoadLocal:
                {
                    int depth = ReadU16(); int slot = ReadU16();
                    Push(_currentScope.GetAncestor(depth).GetSlot(slot));
                    break;
                }

                case OpCode.StoreLocal:
                {
                    int depth = ReadU16(); int slot = ReadU16();
                    _currentScope.GetAncestor(depth).SetSlot(slot, Peek());
                    break;
                }

                case OpCode.LoadGlobal:
                    Push(_globalScope.GetSlot(ReadU16()));
                    break;

                case OpCode.StoreGlobal:
                    _globalScope.SetSlot(ReadU16(), Peek());
                    break;

                case OpCode.DeclareLocal:
                    _currentScope.DefineSlot(Pop());
                    break;

                case OpCode.Add: BinaryNumericOrOperator(Value.Add, "operator+"); break;
                case OpCode.Sub: BinaryNumericOrOperator(Value.Subtract, "operator-"); break;
                case OpCode.Mul: BinaryNumericOrOperator(Value.Multiply, "operator*"); break;
                case OpCode.Div: BinaryNumericOrOperator(Value.Divide, "operator/"); break;
                case OpCode.Mod: BinaryNumericOrOperator(Value.Modulo, "operator%"); break;
                case OpCode.Power: BinaryNumericOrOperator(Value.Power, "operator^"); break;
                case OpCode.BitAnd: BinaryNumericOrOperator(Value.BitAnd, "operator&"); break;
                case OpCode.BitOr: BinaryNumericOrOperator(Value.BitOr, "operator|"); break;
                case OpCode.BitXor: BinaryNumericOrOperator(Value.BitXor, "operator#"); break;
                case OpCode.ShiftLeft: BinaryNumericOrOperator(Value.ShiftLeft, "operator<<"); break;
                case OpCode.ShiftRight: BinaryNumericOrOperator(Value.ShiftRight, "operator>>"); break;

                case OpCode.FormatValue:
                {
                    string format = _currentChunk.Constants[ReadU16()].AsString();
                    var v = Pop();
                    Push(Value.MakeString(v.Format(format)));
                    break;
                }


                case OpCode.Neg: Push(Value.Negate(Pop())); break;
                case OpCode.LogicalNot: Push(Value.LogicalNot(Pop())); break;
                case OpCode.BitNot: Push(Value.BitNot(Pop())); break;

                case OpCode.Eq:
                    BinaryNumericOrOperator((a, b) => Value.MakeBool(Value.ValuesEqual(a, b)), "operator==");
                    break;
                case OpCode.NotEq:
                    BinaryNumericOrOperator((a, b) => Value.MakeBool(!Value.ValuesEqual(a, b)), "operator!=");
                    break;
                case OpCode.Lt:
                    BinaryNumericOrOperator((a, b) => Value.MakeBool(Value.Compare(a, b) < 0), "operator<");
                    break;
                case OpCode.LtEq:
                    BinaryNumericOrOperator((a, b) => Value.MakeBool(Value.Compare(a, b) <= 0), "operator<=");
                    break;
                case OpCode.Gt:
                    BinaryNumericOrOperator((a, b) => Value.MakeBool(Value.Compare(a, b) > 0), "operator>");
                    break;
                case OpCode.GtEq:
                    BinaryNumericOrOperator((a, b) => Value.MakeBool(Value.Compare(a, b) >= 0), "operator>=");
                    break;

                case OpCode.CoerceUnit:
                {
                    var unit = _currentChunk.Units[ReadU16()];
                    Push(Pop().CoerceUnit(unit));
                    break;
                }
                case OpCode.CoerceUnitDynamic:
                {
                    var candidate = Pop();
                    var anchor = Peek();
                    var targetUnit = anchor.Unit ?? Unit.Unitless;
                    Push(candidate.CoerceUnit(targetUnit));
                    break;
                }
                case OpCode.CoerceType:
                {
                    var tag = (TypeTag)ReadByte();
                    Push(Pop().CoerceType(TagToKind(tag)));
                    break;
                }
                case OpCode.CoerceTypeDynamic:
                {
                    var candidate = Pop();
                    var anchor = Peek();
                    Push(candidate.CoerceType(anchor.Kind));
                    break;
                }

                case OpCode.Jump:
                    _ip = ReadU16();
                    break;

                case OpCode.JumpIfFalse:
                {
                    int addr = ReadU16();
                    if (!Pop().AsBool()) _ip = addr;
                    break;
                }
                case OpCode.JumpIfFalsePeek:
                {
                    int addr = ReadU16();
                    if (!Peek().AsBool()) _ip = addr;
                    break;
                }
                case OpCode.JumpIfTruePeek:
                {
                    int addr = ReadU16();
                    if (Peek().AsBool()) _ip = addr;
                    break;
                }

                case OpCode.EnterScope:
                    _currentScope = new Scope(_currentScope);
                    break;

                case OpCode.ExitScope:
                    _currentScope.Release(this);
                    _currentScope = _currentScope.Parent
                        ?? throw new InvalidOperationException("ExitScope auf dem globalen Scope aufgerufen.");
                    break;

                case OpCode.CallNative:
                {
                    int nativeIdx = ReadU16();
                    int argCount = ReadByte();
                    var args = new Value[argCount];
                    for (int i = argCount - 1; i >= 0; i--) args[i] = Pop();
                    Push(_natives[nativeIdx](args));
                    break;
                }

                case OpCode.CallTryableNative:
                {
                    int tryableIdx = ReadU16();
                    int argCount = ReadByte();
                    var args = new Value[argCount];
                    for (int i = argCount - 1; i >= 0; i--) args[i] = Pop();
                    // KEINE Exception bei Fehlschlag/Timeout (siehe
                    // TryableNativeFunction-Doku) - die Host-Implementierung
                    // meldet das über den Rückgabewert 'false', nicht über
                    // einen Wurf; das Skript sieht dafür einfach 'undefined'.
                    bool success = _natives.TryableAt(tryableIdx)(args, out Value tryResult);
                    Push(success ? tryResult : Value.MakeUndefined());
                    break;
                }

                case OpCode.CallExtern:
                {
                    int nameIdx = ReadU16();
                    int argCount = ReadByte();
                    string externName = _currentChunk.Constants[nameIdx].AsString();

                    var scriptArgs = new Value[argCount];
                    for (int i = argCount - 1; i >= 0; i--) scriptArgs[i] = Pop();

                    var (nativeArgs, cleanups) = MarshalArgsOut(scriptArgs);
                    object? nativeResult;
                    try
                    {
                        if (_externs.TryGet(externName, out var fn))
                        {
                            // Manuell vom Host registriert (ExternRegistry) - wie bisher.
                            nativeResult = fn(nativeArgs);
                        }
                        else if (_externSignatures.TryGetValue(externName, out var sig) && sig.LibName != null)
                        {
                            // Dynamisch gegen eine per '#extern "libName"' benannte
                            // native Bibliothek verlinkt - kein Host-Code nötig.
                            var del = ResolveDynamicExtern(externName, sig);
                            nativeResult = del.DynamicInvoke(nativeArgs);
                        }
                        else
                        {
                            throw new InvalidOperationException(
                                $"'{externName}' ist extern deklariert, aber weder manuell verlinkt " +
                                "(ExternRegistry.Register beim Host) noch per '#extern \"libName\"' einer " +
                                "nativen Bibliothek zugeordnet.");
                        }
                    }
                    finally
                    {
                        // Copy-Out für Pointer-Argumente (siehe MarshalArgsOut)
                        // UND Freigabe des dafür allozierten nativen Speichers -
                        // muss auch bei einer C#-Exception aus der nativen
                        // Funktion passieren, sonst native Speicherlecks.
                        foreach (var cleanup in cleanups) cleanup();
                    }

                    Push(MarshalResultIn(nativeResult));
                    break;
                }

                case OpCode.MakeLambda:
                {
                    int protoIdx = ReadU16();
                    bool hasOnTarget = ReadByte() != 0;
                    var proto = _currentChunk.Functions[protoIdx];
                    object? onTarget = hasOnTarget ? BoxValueForOnTarget(Pop()) : null;
                    var lambdaValue = new LambdaValue(proto, onTarget);
                    Push(Value.MakeLambda(lambdaValue));
                    break;
                }

                case OpCode.Call:
                {
                    int argCount = ReadByte();
                    var args = new Value[argCount];
                    for (int i = argCount - 1; i >= 0; i--) args[i] = Pop();
                    var calleeVal = Pop();

                    if (calleeVal.Kind != ValueKind.Lambda)
                        throw new InvalidOperationException(
                            $"Aufruf eines Werts vom Typ {calleeVal.Kind}, der kein Lambda ist.");

                    var lambda = (LambdaValue)calleeVal.AsLambda();
                    CheckArity(lambda.Proto, args.Length);
                    args = FillDefaultArgs(lambda.Proto, args, lambda.OnTarget);

                    _frames.Push(new CallFrame(_currentChunk, _ip, _currentScope, _currentThis, null));

                    var funcScope = new Scope(_globalScope);
                    foreach (var a in args) funcScope.DefineSlot(a);

                    _currentThis = lambda.OnTarget;
                    _currentScope = funcScope;
                    _currentChunk = lambda.Proto.Chunk;
                    _ip = 0;
                    break;
                }

                case OpCode.Return:
                {
                    var retVal = Pop();

                    // SPEC 2.3: Wird eine Objektinstanz zurückgegeben, deren
                    // Owner der gerade verlassene Scope ist, geht das Ownership
                    // an den AUFRUFENDEN Scope über (nicht einfach '.Parent' -
                    // Funktions-/Methoden-Scopes haben als Parent immer global,
                    // das wäre hier nicht die gewünschte "eine Ebene höher").
                    // Ohne das würde das zurückgegebene Objekt durch das gleich
                    // folgende Release() des eigenen Scopes sofort mit zerstört.
                    if (retVal.Kind == ValueKind.Class && _frames.Count > 0)
                    {
                        var retInstance = (ObjectInstance)retVal.AsObjectRef();
                        if (ReferenceEquals(retInstance.Owner, _currentScope))
                            retInstance.ReparentTo(_frames.Peek().ReturnScope);
                    }

                    _currentScope.Release(this);

                    var frame = _frames.Pop();
                    _currentChunk = frame.ReturnChunk;
                    _ip = frame.ReturnIp;
                    _currentScope = frame.ReturnScope;
                    _currentThis = frame.ReturnThis;

                    Push(frame.ConstructedInstance != null
                        ? Value.MakeClassRef(frame.ConstructedInstance)
                        : retVal);
                    break;
                }

                case OpCode.NewObject:
                {
                    int classNameIdx = ReadU16();
                    int argCount = ReadByte();
                    var args = new Value[argCount];
                    for (int i = argCount - 1; i >= 0; i--) args[i] = Pop();

                    var rc = ResolveClass(_currentChunk.Constants[classNameIdx].AsString());
                    var ctorProto = rc.FindConstructor(args.Length)
                        ?? throw new InvalidOperationException(DescribeConstructorNotFound(rc, args.Length));
                    if (ExecutionMode != VmExecutionMode.Performance
                        && rc.ConstructorAccess.TryGetValue(ctorProto.ParamCount, out var ctorAccess)
                        && !IsMemberAccessAllowed(rc, ctorAccess))
                    {
                        ThrowAccessDenied(
                            $"Konstruktor von '{rc.Name}' ist {DescribeAccess(ctorAccess)} und von hier aus nicht aufrufbar.");
                        break;
                    }

                    var instance = new ObjectInstance(rc.Decl, _currentScope, rc);
                    if (rc.IsActor) instance.Mailbox = new ActorMailbox();
                    args = FillDefaultArgs(ctorProto, args, instance);
                    BeginConstruction(instance, ctorProto, args);
                    break;
                }

                case OpCode.NewObjectOwned:
                {
                    int classNameIdx = ReadU16();
                    int argCount = ReadByte();
                    var args = new Value[argCount];
                    for (int i = argCount - 1; i >= 0; i--) args[i] = Pop();
                    var owner = RequireObjectInstance(Pop(), "Objekt-Erzeugung mit Owner");

                    var rc = ResolveClass(_currentChunk.Constants[classNameIdx].AsString());
                    var ctorProto = rc.FindConstructor(args.Length)
                        ?? throw new InvalidOperationException(DescribeConstructorNotFound(rc, args.Length));
                    if (ExecutionMode != VmExecutionMode.Performance
                        && rc.ConstructorAccess.TryGetValue(ctorProto.ParamCount, out var ctorAccessOwned)
                        && !IsMemberAccessAllowed(rc, ctorAccessOwned))
                    {
                        ThrowAccessDenied(
                            $"Konstruktor von '{rc.Name}' ist {DescribeAccess(ctorAccessOwned)} und von hier aus nicht aufrufbar.");
                        break;
                    }

                    var instance = new ObjectInstance(rc.Decl, owner, rc);
                    if (rc.IsActor) instance.Mailbox = new ActorMailbox();
                    args = FillDefaultArgs(ctorProto, args, instance);
                    BeginConstruction(instance, ctorProto, args);
                    break;
                }

                case OpCode.ConstructBase:
                {
                    int classNameIdx = ReadU16();
                    int argCount = ReadByte();
                    var args = new Value[argCount];
                    for (int i = argCount - 1; i >= 0; i--) args[i] = Pop();

                    var rc = ResolveClass(_currentChunk.Constants[classNameIdx].AsString());
                    var ctorProto = rc.FindConstructor(args.Length)
                        ?? throw new InvalidOperationException(DescribeConstructorNotFound(rc, args.Length));
                    args = FillDefaultArgs(ctorProto, args, _currentThis);

                    // Dieselbe Instanz wird weiter konstruiert - 'this' bleibt
                    // unverändert (wird trotzdem in den Frame geschrieben, damit
                    // RETURN einheitlich wiederherstellen kann).
                    _frames.Push(new CallFrame(_currentChunk, _ip, _currentScope, _currentThis, null));

                    var baseScope = new Scope(_globalScope);
                    foreach (var a in args) baseScope.DefineSlot(a);

                    _currentScope = baseScope;
                    _currentChunk = ctorProto.Chunk;
                    _ip = 0;
                    break;
                }

                case OpCode.GetField:
                {
                    string fieldName = _currentChunk.Constants[ReadU16()].AsString();
                    var target = Pop();

                    if (target.Kind == ValueKind.Array)
                    {
                        if (fieldName == "length")
                        {
                            Push(Value.MakeInt(target.AsArray().Length));
                            break;
                        }
                        throw new InvalidOperationException($"Arrays haben kein Feld '{fieldName}' (nur 'length').");
                    }

                    if (target.Kind == ValueKind.Buffer)
                    {
                        var buf = target.AsBuffer();
                        if (fieldName == "length")
                        {
                            Push(Value.MakeInt(buf.Length));
                            break;
                        }
                        if (fieldName == "littleEndian")
                        {
                            Push(Value.MakeBool(buf.Order == ByteOrder.Little));
                            break;
                        }
                        throw new InvalidOperationException(
                            $"Byte-Puffer haben kein Feld '{fieldName}' (nur 'length', 'littleEndian').");
                    }

                    var obj = RequireObjectInstance(target, "Feldzugriff");
                    if (obj.TryGetFieldLocked(fieldName, out var val))
                    {
                        if (ExecutionMode != VmExecutionMode.Performance && obj.RtClass != null)
                        {
                            var fieldAccess = obj.RtClass.FindFieldAccess(fieldName);
                            if (fieldAccess is (var declaringRcGet, var accessGet) && !IsMemberAccessAllowed(declaringRcGet, accessGet))
                            {
                                ThrowAccessDenied(
                                    $"Feld '{fieldName}' von '{declaringRcGet.Name}' ist {DescribeAccess(accessGet)} " +
                                    "und von hier aus nicht zugreifbar.");
                                break;
                            }
                        }
                        Push(val);
                        break;
                    }

                    // Kein Feld dieses Namens - Property-Getter versuchen
                    // (Namenskonvention 'get_'+Name, siehe Ast.PropertyDecl).
                    // Properties haben absichtlich NIE einen eigenen Fields-
                    // Eintrag, landen also immer hier.
                    var rcGet = ResolveClass(obj.ClassDef.Name);
                    if (rcGet.FindMethod("get_" + fieldName, 0) != null)
                    {
                        var result = CallMethodNested(obj, "get_" + fieldName, Array.Empty<Value>());
                        if (result != null) Push(result.Value);
                        break;
                    }

                    throw new InvalidOperationException(
                        $"Feld '{fieldName}' existiert nicht auf einer Instanz von '{obj.ClassDef.Name}' " +
                        $"(auch keine 'get_{fieldName}'-Property).");
                }

                case OpCode.SetField:
                {
                    string fieldName = _currentChunk.Constants[ReadU16()].AsString();
                    var value = Pop();
                    var obj = RequireObjectInstance(Pop(), "Feldzuweisung");

                    if (obj.HasFieldLocked(fieldName))
                    {
                        if (ExecutionMode != VmExecutionMode.Performance && obj.RtClass != null)
                        {
                            var fieldAccess = obj.RtClass.FindFieldAccess(fieldName);
                            if (fieldAccess is (var declaringRcSet, var accessSet) && !IsMemberAccessAllowed(declaringRcSet, accessSet))
                            {
                                ThrowAccessDenied(
                                    $"Feld '{fieldName}' von '{declaringRcSet.Name}' ist {DescribeAccess(accessSet)} " +
                                    "und von hier aus nicht zugreifbar.");
                                break;
                            }
                        }
                        obj.SetFieldLocked(fieldName, value);
                        Push(value);
                        break;
                    }

                    // Kein existierendes Feld dieses Namens - Property-Setter
                    // versuchen (Namenskonvention 'set_'+Name).
                    var rcSet = ResolveClass(obj.ClassDef.Name);
                    if (rcSet.FindMethod("set_" + fieldName, 1) != null)
                    {
                        var result = CallMethodNested(obj, "set_" + fieldName, new[] { value });
                        // Rückgabewert des Setters selbst unbenutzt - eine
                        // Zuweisung wertet immer zum ZUGEWIESENEN Wert aus,
                        // nicht zu dem, was der Setter zurückgibt. null ==
                        // per Exception umgeleitet (siehe CallMethodNested-
                        // Doku) - dann NICHT pushen.
                        if (result != null) Push(value);
                        break;
                    }

                    // Eine gleichnamige Property MIT Getter, aber OHNE Setter,
                    // existiert - das ist ein Fehler, KEIN "neues Feld anlegen"
                    // (sonst würde die Property ab hier unbemerkt durch ein
                    // gleichnamiges Feld überschattet, auch für künftige
                    // Lesezugriffe über GetField, das Felder vor Properties
                    // prüft).
                    if (rcSet.FindMethod("get_" + fieldName, 0) != null)
                        throw new InvalidOperationException(
                            $"Property '{fieldName}' auf '{obj.ClassDef.Name}' hat keinen Setter (nur 'get').");

                    // Weder existierendes Feld noch Property - wie bisher:
                    // neues Feld einfach anlegen (dynamische Sprache, keine
                    // Vorab-Deklarationspflicht für Felder).
                    obj.SetFieldLocked(fieldName, value);
                    Push(value);
                    break;
                }

                case OpCode.LoadThis:
                    Push(_currentThis switch
                    {
                        null => throw new InvalidOperationException("'this' ist an dieser Stelle nicht gebunden."),
                        ObjectInstance oi => Value.MakeClassRef(oi),
                        Value v => v,
                        _ => throw new InvalidOperationException("Unerwarteter 'this'-Wert."),
                    });
                    break;

                case OpCode.SetFieldOnThis:
                {
                    string fieldName = _currentChunk.Constants[ReadU16()].AsString();
                    var value = Pop();
                    if (_currentThis is not ObjectInstance oi)
                        throw new InvalidOperationException("SetFieldOnThis ohne gebundene ObjectInstance als 'this'.");
                    oi.SetFieldLocked(fieldName, value);
                    break;
                }

                case OpCode.CallMethod:
                {
                    string methodName = _currentChunk.Constants[ReadU16()].AsString();
                    int argCount = ReadByte();
                    var args = new Value[argCount];
                    for (int i = argCount - 1; i >= 0; i--) args[i] = Pop();
                    var target = Pop();

                    // Eingebaute Methoden auf primitiven Werten (String/Char/
                    // Int(als byte)/Buffer, siehe TryCallBuiltinMethod, SPEC
                    // 8.10) - GETRENNT vom normalen Klassen-Methodenaufruf
                    // unten, da ein primitiver Wert keine ObjectInstance ist
                    // und nie eine war (RequireObjectInstance würde hier
                    // sonst fälschlich ablehnen).
                    if (target.Kind != ValueKind.Class)
                    {
                        if (TryCallBuiltinMethod(target, methodName, args, out Value builtinResult))
                        {
                            Push(builtinResult);
                            break;
                        }
                        throw new InvalidOperationException(
                            $"'{methodName}' ({args.Length} Argument(e)) ist keine bekannte eingebaute Methode " +
                            $"auf einem Wert vom Typ {target.Kind}.");
                    }

                    var obj = (ObjectInstance)target.AsObjectRef();

                    // Actor-Ziel (siehe Runtime.ObjectInstance.Mailbox-Doku):
                    // JEDER Methodenaufruf wird zu einer asynchronen Nachricht
                    // statt eines direkten Aufrufs, unabhängig vom rufenden
                    // Thread - dieser Aufruf selbst liefert 'undefined' und
                    // läuft normal weiter (kein Sprung in irgendeinen Chunk).
                    if (obj.Mailbox != null)
                    {
                        obj.Mailbox.Enqueue(new ActorMessage(methodName, args));
                        Push(Value.MakeUndefined());
                        break;
                    }

                    var rc = ResolveClass(obj.ClassDef.Name);
                    var (proto, declaringRcCall, accessCall) = rc.FindMethodWithAccess(methodName, args.Length);
                    if (proto == null)
                        throw new InvalidOperationException(DescribeMethodNotFound(rc, methodName, args.Length));
                    if (ExecutionMode != VmExecutionMode.Performance && !IsMemberAccessAllowed(declaringRcCall!, accessCall))
                    {
                        ThrowAccessDenied(
                            $"Methode '{methodName}' von '{declaringRcCall!.Name}' ist {DescribeAccess(accessCall)} " +
                            "und von hier aus nicht aufrufbar.");
                        break;
                    }
                    CheckArity(proto, args.Length);
                    args = FillDefaultArgs(proto, args, obj);

                    _frames.Push(new CallFrame(_currentChunk, _ip, _currentScope, _currentThis, null));
                    var scope = new Scope(_globalScope);
                    foreach (var a in args) scope.DefineSlot(a);

                    _currentThis = obj;
                    _currentScope = scope;
                    _currentChunk = proto.Chunk;
                    _ip = 0;
                    break;
                }

                case OpCode.CallBaseMethod:
                {
                    string baseClassName = _currentChunk.Constants[ReadU16()].AsString();
                    string methodName = _currentChunk.Constants[ReadU16()].AsString();
                    int argCount = ReadByte();
                    var args = new Value[argCount];
                    for (int i = argCount - 1; i >= 0; i--) args[i] = Pop();

                    var rc = ResolveClass(baseClassName);
                    var (proto, declaringRcBase, accessBase) = rc.FindMethodWithAccess(methodName, args.Length);
                    if (proto == null)
                        throw new InvalidOperationException(DescribeMethodNotFound(rc, methodName, args.Length));
                    if (ExecutionMode != VmExecutionMode.Performance && !IsMemberAccessAllowed(declaringRcBase!, accessBase))
                    {
                        ThrowAccessDenied(
                            $"Methode '{methodName}' von '{declaringRcBase!.Name}' ist {DescribeAccess(accessBase)} " +
                            "und von hier aus nicht aufrufbar.");
                        break;
                    }
                    CheckArity(proto, args.Length);
                    args = FillDefaultArgs(proto, args, _currentThis);

                    // 'this' bleibt dasselbe Objekt (nicht-virtueller Aufruf).
                    _frames.Push(new CallFrame(_currentChunk, _ip, _currentScope, _currentThis, null));
                    var scope = new Scope(_globalScope);
                    foreach (var a in args) scope.DefineSlot(a);

                    _currentScope = scope;
                    _currentChunk = proto.Chunk;
                    _ip = 0;
                    break;
                }

                case OpCode.CallProtoWithThis:
                {
                    int protoIdx = ReadU16();
                    int argCount = ReadByte();
                    var args = new Value[argCount];
                    for (int i = argCount - 1; i >= 0; i--) args[i] = Pop();
                    var thisVal = Pop();
                    var proto = _currentChunk.Functions[protoIdx];
                    CheckArity(proto, args.Length);

                    _frames.Push(new CallFrame(_currentChunk, _ip, _currentScope, _currentThis, null));
                    var scope = new Scope(_globalScope);
                    foreach (var a in args) scope.DefineSlot(a);

                    _currentThis = thisVal.Kind == ValueKind.Class ? thisVal.AsObjectRef() : (object)thisVal;
                    _currentScope = scope;
                    _currentChunk = proto.Chunk;
                    _ip = 0;
                    break;
                }

                case OpCode.AddressOfLocal:
                {
                    int depth = ReadU16(); int slot = ReadU16();
                    Push(Value.MakePointer(new ScopeSlotPointerTarget(_currentScope.GetAncestor(depth), slot)));
                    break;
                }

                case OpCode.AddressOfGlobal:
                {
                    int slot = ReadU16();
                    Push(Value.MakePointer(new ScopeSlotPointerTarget(_globalScope, slot)));
                    break;
                }

                case OpCode.AddressOfField:
                {
                    string fieldName = _currentChunk.Constants[ReadU16()].AsString();
                    var obj = RequireObjectInstance(Pop(), "Address-of auf Feld");
                    Push(Value.MakePointer(new FieldPointerTarget(obj, fieldName)));
                    break;
                }

                case OpCode.PtrRead:
                {
                    var ptr = Pop();
                    Push(ptr.AsPointer().Read());
                    break;
                }

                case OpCode.PtrWrite:
                {
                    var value = Pop();
                    var ptr = Pop();
                    ptr.AsPointer().Write(value);
                    Push(value);
                    break;
                }

                case OpCode.NewArray:
                {
                    long size = Pop().AsInt();
                    if (size < 0)
                        throw new InvalidOperationException($"Ungültige Array-Größe {size}.");
                    Push(Value.MakeArray(new ScriptArray((int)size)));
                    break;
                }

                case OpCode.MakeArrayLiteral:
                {
                    int count = ReadU16();
                    var arr = new ScriptArray(count);
                    for (int i = count - 1; i >= 0; i--)
                        arr.Items[i] = Pop();
                    Push(Value.MakeArray(arr));
                    break;
                }

                case OpCode.MakeBuffer:
                {
                    long size = Pop().AsInt();
                    if (size < 0)
                        throw new InvalidOperationException($"Ungültige Byte-Puffer-Größe {size} (muss >= 0 sein).");
                    Push(Value.MakeBuffer(new ByteBuffer((int)size, ByteConversions.HostByteOrder)));
                    break;
                }

                case OpCode.ArrayGet:
                {
                    var indexVal = Pop();
                    var target = Pop();

                    if (target.Kind == ValueKind.Array)
                    {
                        if (ExecutionMode == VmExecutionMode.Performance)
                        {
                            // Siehe VmExecutionMode.Performance-Doku - KEINE
                            // Bounds-Prüfung, ein ungültiger Index führt zu
                            // einer rohen .NET-IndexOutOfRangeException statt
                            // einer fangbaren Skript-Exception.
                            Push(target.AsArray().GetUnchecked(indexVal.AsInt()));
                        }
                        else
                        {
                            long idx = indexVal.AsInt();
                            if (target.AsArray().TryGet(idx, out var v))
                                Push(v);
                            else
                                // Macht einen ungültigen Index zu einer echten,
                                // fangbaren Skript-Exception statt eines rohen
                                // C#-Fehlers - KEIN Push hier, ThrowIndexOutOfBounds
                                // hat _currentChunk/_ip bereits umgeleitet.
                                ThrowIndexOutOfBounds(idx, target.AsArray().Length);
                        }
                    }
                    else if (target.Kind == ValueKind.Buffer)
                    {
                        // Liefert IMMER int[8] (Width W8, siehe Values.NumericWidth) -
                        // 'byte' ist reines Typ-Sugar für int[8] (SPEC 8.10),
                        // kein eigener ValueKind, ein einzelnes Byte ist deshalb
                        // einfach ein normaler int-Wert mit dieser Breite.
                        if (ExecutionMode == VmExecutionMode.Performance)
                        {
                            byte bFast = target.AsBuffer().GetUnchecked(indexVal.AsInt());
                            Push(Value.MakeInt(bFast, width: NumericWidth.W8));
                        }
                        else
                        {
                            long idx = indexVal.AsInt();
                            if (target.AsBuffer().TryGet(idx, out byte b))
                                Push(Value.MakeInt(b, width: NumericWidth.W8));
                            else
                                ThrowIndexOutOfBounds(idx, target.AsBuffer().Length);
                        }
                    }
                    else if (target.Kind == ValueKind.Class)
                    {
                        // '[]'-Operator-Überladung per Namenskonvention (wie
                        // GetEnumerator/MoveNext/GetCurrent bei foreach): eine
                        // Klasse mit einer GetIndex(i)-Methode wird für Lesezugriffe
                        // benutzt - rein dynamisch, funktioniert auf jeder Klasse
                        // mit passender Methode, nicht nur auf 'List'.
                        var obj = (ObjectInstance)target.AsObjectRef();
                        var result = CallMethodNested(obj, "GetIndex", new[] { indexVal });
                        // null == GetIndex() wurde durch eine geworfene Exception
                        // verlassen (siehe CallMethodNested-Doku) - dann NICHT
                        // pushen, die Ausführung läuft bereits anderswo weiter.
                        if (result != null) Push(result.Value);
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            $"Index-Zugriff ('[]') auf einem Wert vom Typ {target.Kind} nicht möglich " +
                            "(weder Array noch eine Klasse mit 'GetIndex'-Methode).");
                    }
                    break;
                }

                case OpCode.ArraySet:
                {
                    var value = Pop();
                    var indexVal = Pop();
                    var target = Pop();

                    if (target.Kind == ValueKind.Array)
                    {
                        if (ExecutionMode == VmExecutionMode.Performance)
                        {
                            target.AsArray().SetUnchecked(indexVal.AsInt(), value);
                            Push(value);
                        }
                        else
                        {
                            long idx = indexVal.AsInt();
                            if (target.AsArray().TrySet(idx, value))
                                Push(value);
                            else
                                // Kein Push hier - ThrowIndexOutOfBounds hat
                                // _currentChunk/_ip bereits umgeleitet, ein
                                // zusätzlicher Push würde den Stack dort verschieben.
                                ThrowIndexOutOfBounds(idx, target.AsArray().Length);
                        }
                    }
                    else if (target.Kind == ValueKind.Buffer)
                    {
                        if (value.Kind != ValueKind.Int)
                            throw new InvalidOperationException(
                                $"Byte-Puffer-Zuweisung erwartet einen int-Wert (byte = int[8]), nicht {value.Kind}.");
                        if (ExecutionMode == VmExecutionMode.Performance)
                        {
                            target.AsBuffer().SetUnchecked(indexVal.AsInt(), (byte)value.AsInt());
                            Push(value);
                        }
                        else
                        {
                            long idx = indexVal.AsInt();
                            if (target.AsBuffer().TrySet(idx, (byte)value.AsInt()))
                                Push(value);
                            else
                                ThrowIndexOutOfBounds(idx, target.AsBuffer().Length);
                        }
                    }
                    else if (target.Kind == ValueKind.Class)
                    {
                        var obj = (ObjectInstance)target.AsObjectRef();
                        var result = CallMethodNested(obj, "SetIndex", new[] { indexVal, value }); // Rückgabewert unbenutzt
                        // null == SetIndex() wurde durch eine geworfene Exception
                        // verlassen (siehe CallMethodNested-Doku) - dann NICHT
                        // pushen, die Ausführung läuft bereits anderswo weiter.
                        if (result != null) Push(value);
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            $"Index-Zuweisung ('[]=') auf einem Wert vom Typ {target.Kind} nicht möglich " +
                            "(weder Array noch eine Klasse mit 'SetIndex'-Methode).");
                    }
                    break;
                }

                case OpCode.RegisterHandler:
                {
                    int templateIdx = ReadU16();
                    var template = _currentChunk.Handlers[templateIdx];
                    _handlers.Add(new ActiveHandler(_currentChunk, _frames.Count, _currentScope, template));
                    break;
                }

                case OpCode.UnregisterHandler:
                    _handlers.RemoveAt(_handlers.Count - 1);
                    break;

                case OpCode.Throw:
                {
                    var thrown = Pop();
                    ThrowException(thrown);
                    break;
                }

                case OpCode.IsInUnit:
                {
                    var unit = _currentChunk.Units[ReadU16()];
                    var v = Pop();
                    var valueUnit = v.Unit ?? Unit.Unitless;
                    Push(Value.MakeBool(valueUnit.IsCompatibleWith(unit)));
                    break;
                }

                case OpCode.IsOfType:
                {
                    string typeName = _currentChunk.Constants[ReadU16()].AsString();
                    var v = Pop();
                    Push(Value.MakeBool(IsOfType(v, typeName)));
                    break;
                }

                case OpCode.IsFrom:
                {
                    bool transitive = ReadByte() != 0;
                    var ownerVal = Pop();
                    var operandVal = Pop();
                    var operandObj = RequireObjectInstance(operandVal, "'is from'/'is under'");
                    var ownerObj = RequireObjectInstance(ownerVal, "'is from'/'is under' (Owner-Ausdruck)");
                    bool result = transitive
                        ? operandObj.IsTransitivelyOwnedBy(ownerObj)
                        : operandObj.IsOwnedBy(ownerObj);
                    Push(Value.MakeBool(result));
                    break;
                }

                case OpCode.ResumeException:
                {
                    var resumeValue = Pop();
                    var excVal = Pop();
                    var excInstance = RequireObjectInstance(excVal, "resume");

                    if (!_pendingResumes.TryGetValue(excInstance, out var pending))
                        throw new InvalidOperationException(
                            "resume() aufgerufen, aber diese Exception wird gerade nicht behandelt " +
                            "(entweder schon fortgesetzt, oder kein aktiver catch dafür).");

                    _pendingResumes.Remove(excInstance);

                    // Den GERADE laufenden catch-Kontext abwickeln, den resume()
                    // verlässt - exakt wie beim Betreten des Handlers selbst,
                    // funktioniert daher auch, wenn resume() aus einem
                    // verschachtelten Funktionsaufruf INNERHALB des catch heraus
                    // aufgerufen wird.
                    UnwindTo(pending.Handler.FrameDepthAtEntry, pending.Handler.TargetScope);

                    // Eingefrorenen Wurfstellen-Zustand exakt zurückspielen.
                    for (int i = pending.Continuation.Frames.Count - 1; i >= 0; i--)
                        _frames.Push(pending.Continuation.Frames[i]);

                    _currentChunk = pending.Continuation.Chunk;
                    _ip = pending.Continuation.Ip;
                    _currentScope = pending.Continuation.Scope;
                    _currentThis = pending.Continuation.This;

                    // Die fortgesetzte Stelle ist konzeptionell "immer noch im
                    // try-Block" - Handler wieder scharf schalten (siehe
                    // PendingResume-Kommentar), sonst reißt ein erneuter throw
                    // dort keinen passenden catch mehr und UnregisterHandler am
                    // Ende des try-Blocks entfernt versehentlich einen fremden
                    // Eintrag.
                    _handlers.Add(pending.Handler);

                    Push(resumeValue); // das ist der Wert, zu dem 'throw' jetzt auswertet
                    break;
                }

                case OpCode.ClearPendingResume:
                {
                    var excVal = Pop();
                    var excInstance = (ObjectInstance)excVal.AsObjectRef();
                    if (_pendingResumes.TryGetValue(excInstance, out var pending))
                    {
                        _pendingResumes.Remove(excInstance);
                        DiscardContinuation(pending.Continuation, pending.Handler.TargetScope);
                    }
                    break;
                }

                case OpCode.CheckLambdaSignature:
                {
                    // Prüft Peek() (NICHT Pop() - der Wert wird direkt danach
                    // noch normal weiterverwendet, z.B. per DeclareLocal/
                    // StoreLocal, das hier nur eine zusätzliche Validierung
                    // VOR dieser Weiterverwendung ist) gegen die erwartete
                    // Parameterzahl aus der Typ-Annotation (siehe Ast.
                    // TypeRef.LambdaSignature, Compiler.EmitCheckLambdaSignature).
                    int expectedParamCount = ReadByte();
                    var v = Peek();
                    if (v.Kind != ValueKind.Lambda)
                        throw new InvalidOperationException(
                            $"Erwarte einen Lambda-Wert (Typ 'lambda'), erhalten: {v.Kind}.");
                    var lambdaVal = (LambdaValue)v.AsLambda();
                    if (lambdaVal.Proto.ParamCount != expectedParamCount)
                        throw new InvalidOperationException(
                            $"Lambda-Signatur passt nicht: erwartet {expectedParamCount} Parameter, " +
                            $"das Lambda hat {lambdaVal.Proto.ParamCount}.");
                    break;
                }

                case OpCode.Fire:
                {
                    // Startet einen ECHTEN Thread (siehe Runtime.FireRuntime) -
                    // `_natives`/`_classes`/`_externs`/`_externSignatures`
                    // werden mit der NEUEN VM-Instanz geteilt (unveränderlich
                    // nach dem Kompilieren, sicher über Threads hinweg, siehe
                    // FireRuntime-Klassenkommentar) - fire selbst blockiert
                    // NICHT (keine Rückgabewerte, kein Join, siehe
                    // docs/THREADING_DESIGN.md Abschnitt 1).
                    int protoIdx = ReadU16();
                    int globalSlotCount = ReadU16();
                    int takingCount = ReadByte();
                    bool hasWith = ReadByte() != 0;
                    // Reihenfolge umgekehrt zum Kompilieren (Stack!): with
                    // wurde ALS LETZTES gepusht, liegt also oben, wird ZUERST
                    // gepoppt; danach die taking-Werte in UMGEKEHRTER
                    // Listenreihenfolge (letzter zuerst) - beides zusammen
                    // wieder in die richtige Reihenfolge gebracht (siehe
                    // Compiler.CompileFireStmt).
                    Value? withValue = hasWith ? Pop() : null;
                    var takingValues = new Value[takingCount];
                    for (int i = takingCount - 1; i >= 0; i--) takingValues[i] = Pop();

                    // Read-only-Snapshot ALLER Hauptprogramm-Globals (siehe
                    // Resolving.Resolver.ResolveFireStmt/Runtime.FireRuntime.
                    // FireVmTaking-Doku) - MUSS hier, SYNCHRON auf DIESEM
                    // (dem aufrufenden) Thread gelesen werden, BEVOR der neue
                    // Thread überhaupt gestartet wird - ein späteres, lazy
                    // Lesen AUS dem neuen Thread heraus wäre ein echter
                    // Daten-Wettlauf mit diesem, hier weiterlaufenden Thread.
                    // `globalSlotCount` kann GRÖSSER sein als der aktuelle
                    // Füllstand von `_globalScope` (steht dieses `fire` VOR
                    // später im Quelltext folgenden globalen `var`-
                    // Deklarationen, wurden die zu DIESEM Zeitpunkt der
                    // Ausführung noch nicht erreicht) - mit `undefined`
                    // aufgefüllt, damit die vom Compiler fest vergebenen
                    // Folge-Slots (taking/with) immer an der erwarteten
                    // Position landen.
                    var globalSnapshot = new Value[globalSlotCount];
                    for (int i = 0; i < globalSlotCount; i++)
                        globalSnapshot[i] = i < _globalScope.SlotCount ? _globalScope.GetSlot(i) : Value.MakeUndefined();

                    var fireProto = _currentChunk.Functions[protoIdx];
                    // Der neue Fire-Thread erbt den ExecutionMode DIESER VM -
                    // sonst würde jeder `fire`-Thread stillschweigend wieder
                    // im (langsamsten) Debug-Modus laufen, unabhängig davon,
                    // in welchem Modus das Hauptprogramm selbst läuft (siehe
                    // VmExecutionMode-Doku).
                    FireRuntime.FireVmTaking(
                        fireProto.Chunk, _natives, _classes, globalSnapshot, takingValues, withValue,
                        executionMode: ExecutionMode);
                    break;
                }

                case OpCode.Sync:
                {
                    // Siehe Ast.SyncExpr-Doku / Runtime.SyncEngine - `this`
                    // (die VM implementiert IDestructRunner, siehe
                    // Klassensignatur) wird als Destruktor-Runner
                    // durchgereicht, damit ein Fall-B-Objektverlust (siehe
                    // SyncEngine.SyncSingleValue) über DIESELBE
                    // Destruktor-Ausführung läuft wie der Rest dieser
                    // VM-Instanz.
                    byte flags = ReadByte();
                    bool isTry = (flags & 1) != 0;
                    bool isFlat = (flags & 2) != 0;
                    var syncTarget = RequireObjectInstance(Pop(), "sync");

                    var syncResult = isFlat
                        ? SyncEngine.SyncFlat(syncTarget, blocking: !isTry, this)
                        : SyncEngine.Sync(syncTarget, blocking: !isTry, this);

                    Push(syncResult switch
                    {
                        SyncResult.Success => Value.MakeBool(true),
                        SyncResult.LockBusy => Value.MakeBool(false),
                        _ => Value.MakeUndefined(),
                    });
                    break;
                }

                case OpCode.Leave:
                    // Setzt nur das Flag (siehe VM.RequestLeave) - die
                    // eigentliche Abwicklung passiert am NÄCHSTEN Prüfpunkt
                    // (CheckShutdownSignals, am Anfang der nächsten
                    // Schleifen-Iteration in Run()), nicht sofort hier.
                    RequestLeave();
                    break;

                case OpCode.Terminate:
                {
                    var terminateValue = Pop();
                    RequestTerminate(terminateValue);
                    break;
                }

                case OpCode.RegisterThreadsCatch:
                {
                    int protoIdx = ReadU16();
                    bool hasType = ReadByte() != 0;
                    string? typeName = hasType ? _currentChunk.Constants[ReadU16()].AsString() : null;
                    GlobalHandlers.RegisterThreadsCatch(typeName, _currentChunk.Functions[protoIdx]);
                    break;
                }

                case OpCode.RegisterTerminateCatch:
                {
                    int protoIdx = ReadU16();
                    GlobalHandlers.RegisterTerminateCatch(_currentChunk.Functions[protoIdx]);
                    break;
                }

                case OpCode.Process:
                {
                    var actorTarget = RequireObjectInstance(Pop(), "process");
                    ProcessOneMessage(actorTarget, blocking: true);
                    break;
                }

                case OpCode.TryProcess:
                {
                    var actorTarget = RequireObjectInstance(Pop(), "try process");
                    bool processed = ProcessOneMessage(actorTarget, blocking: false);
                    Push(Value.MakeBool(processed));
                    break;
                }

                default:
                    throw new InvalidOperationException($"Unbekannter Opcode {op}");
            }
        }

        /// <summary>Gemeinsamer Sprung in den Konstruktor-Proto für NewObject und
        /// NewObjectOwned - unterscheiden sich nur darin, welchen Owner die neue
        /// Instanz bekommt (schon vor diesem Aufruf entschieden).</summary>
        // -----------------------------------------------------------
        // extern-Linking: Marshalling Skript-Wert <-> echter nativer Typ
        // -----------------------------------------------------------

        /// <summary>Wandelt Skript-Argumente in echte native CLR-Typen für einen
        /// extern-Aufruf um. Werttypen (bool/int/float/char/string) werden
        /// direkt in ihr natives Gegenstück kopiert. Ein Pointer-Argument
        /// bekommt dagegen ECHTEN unmanaged Speicher (Marshal.AllocHGlobal) -
        /// der aktuelle Wert wird hineingeschrieben, die native Funktion
        /// bekommt die rohe Adresse (IntPtr), und über den zurückgegebenen
        /// Cleanup-Delegate wird nach dem Aufruf der (möglicherweise von der
        /// nativen Seite veränderte) Wert zurück in das PointerTarget
        /// geschrieben (Copy-Out) und der native Speicher wieder freigegeben -
        /// echtes Pointer-Marshalling statt nur eine Adresse durchzureichen,
        /// da unsere Pointer auf verwaltete Scope-Slots/Felder zeigen, nicht
        /// auf schon-native Adressen (siehe PointerTarget-Doku).</summary>
        private (object?[] nativeArgs, List<Action> cleanups) MarshalArgsOut(Value[] args)
        {
            var nativeArgs = new object?[args.Length];
            var cleanups = new List<Action>();

            for (int i = 0; i < args.Length; i++)
            {
                var v = args[i];
                switch (v.Kind)
                {
                    case ValueKind.Bool:
                        nativeArgs[i] = v.AsBool();
                        break;
                    case ValueKind.Int:
                        nativeArgs[i] = v.AsInt();
                        break;
                    case ValueKind.Float:
                        nativeArgs[i] = v.AsFloat();
                        break;
                    case ValueKind.Char:
                        nativeArgs[i] = v.AsChar();
                        break;
                    case ValueKind.String:
                        nativeArgs[i] = v.AsString();
                        break;
                    case ValueKind.Undefined:
                        nativeArgs[i] = null;
                        break;
                    case ValueKind.Pointer:
                    {
                        var target = v.AsPointer();
                        var current = target.Read();
                        IntPtr native = System.Runtime.InteropServices.Marshal.AllocHGlobal(8);
                        WriteNativeValue(native, current);
                        nativeArgs[i] = native;

                        var targetKind = current.Kind;
                        cleanups.Add(() =>
                        {
                            target.Write(ReadNativeValue(native, targetKind));
                            System.Runtime.InteropServices.Marshal.FreeHGlobal(native);
                        });
                        break;
                    }
                    default:
                        throw new InvalidOperationException(
                            $"Werte vom Typ {v.Kind} können nicht an eine extern-Funktion übergeben werden.");
                }
            }

            return (nativeArgs, cleanups);
        }

        /// <summary>Wandelt den nativen Rückgabewert einer extern-Funktion in
        /// einen Skript-Value um - anhand des tatsächlichen CLR-Laufzeittyps,
        /// da extern-Deklarationen keinen strikt durchgesetzten Rückgabetyp
        /// haben (dynamisch, wie der Rest der Sprache).</summary>
        private static Value MarshalResultIn(object? nativeResult) => nativeResult switch
        {
            null => Value.MakeUndefined(),
            bool b => Value.MakeBool(b),
            long l => Value.MakeInt(l),
            int i => Value.MakeInt(i),
            double d => Value.MakeFloat(d),
            float f => Value.MakeFloat(f),
            char c => Value.MakeChar(c),
            string s => Value.MakeString(s),
            _ => throw new InvalidOperationException(
                $"Rückgabewert vom nativen Typ {nativeResult.GetType().Name} kann nicht in einen Skript-Wert " +
                "umgewandelt werden (unterstützt: bool/int/float/char/string)."),
        };

        // -----------------------------------------------------------
        // extern-Linking: dynamisches Laden gegen '#extern "libName"'
        // -----------------------------------------------------------

        // WICHTIGE FALLE (erst zur Laufzeit entdeckt): Marshal.
        // GetDelegateForFunctionPointer lehnt JEDEN generischen Delegate-Typ ab
        // - auch einen bereits vollständig GESCHLOSSENEN wie Func<long> (die
        // Fehlermeldung "The specified Type must not be a generic type" prüft
        // offenbar Type.IsGenericType, nicht ContainsGenericParameters). Die
        // BCL-Delegates Action<...>/Func<...> sind also für diesen Zweck
        // NICHT nutzbar, obwohl geschlossen-generische Typen sonst überall
        // sonst wie normale Typen behandelt werden. Statt dessen wird hier
        // per System.Reflection.Emit ein ECHTER, NICHT-generischer Delegate-
        // Typ zur Laufzeit erzeugt (Standard-Pattern: TypeBuilder von
        // MulticastDelegate ableiten, Konstruktor + virtuelle Invoke-Methode
        // mit der gewünschten Signatur definieren, beide als 'runtime-
        // implementiert' markieren) - dieselbe Technik, die auch .NETs
        // eigener C#-Compiler für ein `delegate`-Schlüsselwort verwendet,
        // nur eben zur LAUFZEIT statt zur Compile-Zeit, da die Signatur erst
        // durch die geparste `extern`-Deklaration feststeht.
        private static readonly System.Reflection.Emit.ModuleBuilder DynamicDelegateModule =
            System.Reflection.Emit.AssemblyBuilder
                .DefineDynamicAssembly(
                    new System.Reflection.AssemblyName("ScriptLangDynamicExterns"),
                    System.Reflection.Emit.AssemblyBuilderAccess.Run)
                .DefineDynamicModule("DynamicExterns");

        /// <summary>ModuleBuilder.DefineType/TypeBuilder.CreateType sind laut
        /// .NET-Dokumentation NICHT sicher für gleichzeitige Aufrufe von
        /// mehreren Threads - da DynamicDelegateModule statisch (über ALLE
        /// VM-Instanzen/Threads hinweg geteilt) ist, muss der GESAMTE
        /// Typ-Bau-Vorgang (DefineType bis CreateType) für die Dauer eines
        /// einzelnen ResolveDynamicExtern-Aufrufs exklusiv laufen - dieser
        /// Lock schützt genau das. Betrifft nur den (seltenen) Erstaufbau
        /// eines dynamisch verlinkten Delegate-Typs, nicht den eigentlichen
        /// nativen Aufruf selbst (der danach über den bereits fertigen,
        /// gecachten Delegate läuft).</summary>
        private static readonly object DynamicDelegateModuleLock = new();

        private static int _dynamicDelegateCounter;

        /// <summary>Löst (und cached) den Delegate für einen dynamisch
        /// verlinkten `extern`-Aufruf: lädt die Bibliothek (per Namen
        /// gecached, `NativeLibrary.Load` ist nicht ganz billig), sucht den
        /// Export, baut per Reflection.Emit einen echten, nicht-generischen
        /// Delegate-Typ passend zur Skript-Signatur (`ExternSignature`,
        /// siehe BuildNonGenericDelegateType) und macht daraus per `Marshal.
        /// GetDelegateForFunctionPointer` einen aufrufbaren Delegate. Die
        /// eigentliche native Aufruf-Mechanik (Calling Convention, Argument-
        /// Marshalling pro Parametertyp) übernimmt damit komplett die
        /// eingebaute .NET-Interop-Schicht - hier wird nur zur Laufzeit die
        /// PASSENDE Delegate-Form zusammengebaut, was zur Compile-Zeit nicht
        /// möglich wäre.</summary>
        private Delegate ResolveDynamicExtern(string externName, ExternSignature sig)
        {
            if (_dynamicExternDelegates.TryGetValue(externName, out var cached))
                return cached;

            string libName = sig.LibName!;
            if (!_loadedNativeLibraries.TryGetValue(libName, out var libHandle))
            {
                try
                {
                    libHandle = System.Runtime.InteropServices.NativeLibrary.Load(libName);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Native Bibliothek '{libName}' konnte nicht geladen werden (für extern '{externName}', " +
                        $"per '#extern \"{libName}\"' deklariert): {ex.Message}", ex);
                }
                _loadedNativeLibraries[libName] = libHandle;
            }

            IntPtr fnPtr;
            try
            {
                fnPtr = System.Runtime.InteropServices.NativeLibrary.GetExport(libHandle, externName);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Funktion '{externName}' wurde in '{libName}' nicht gefunden: {ex.Message}", ex);
            }

            var paramClrTypes = sig.ParamTypes.Select(t => MapExternClrType(t, externName)).ToArray();
            Type returnClrType = sig.ReturnType != null ? MapExternClrType(sig.ReturnType, externName) : typeof(void);
            Type delegateType = BuildNonGenericDelegateType(paramClrTypes, returnClrType, externName);

            Delegate del;
            try
            {
                del = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer(fnPtr, delegateType);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Konnte für extern '{externName}' aus '{libName}' keinen aufrufbaren Delegate bauen: {ex.Message}", ex);
            }

            _dynamicExternDelegates[externName] = del;
            return del;
        }

        /// <summary>Baut einen echten, nicht-generischen Delegate-Typ (siehe
        /// Klassen-Kommentar oben für WARUM das nötig ist statt einfach
        /// Action&lt;...&gt;/Func&lt;...&gt; zu nutzen) mit exakt der
        /// gewünschten Parameter-/Rückgabe-Signatur.</summary>
        private static Type BuildNonGenericDelegateType(Type[] paramTypes, Type returnType, string externName)
        {
            // Siehe DynamicDelegateModuleLock-Doku: der GESAMTE Aufbau (nicht nur
            // DefineType) muss exklusiv laufen, da ModuleBuilder/TypeBuilder
            // laut .NET-Doku nicht für gleichzeitige Nutzung von mehreren
            // Threads ausgelegt sind - relevant, sobald mehrere VM-Instanzen
            // (ein Fire-Thread bringt seine eigene mit) gleichzeitig zum
            // ersten Mal denselben oder verschiedene externs dynamisch linken.
            lock (DynamicDelegateModuleLock)
            {
                string typeName = $"ScriptLangExtern_{externName}_{System.Threading.Interlocked.Increment(ref _dynamicDelegateCounter)}";

                var typeBuilder = DynamicDelegateModule.DefineType(
                    typeName,
                    System.Reflection.TypeAttributes.Public | System.Reflection.TypeAttributes.Sealed
                        | System.Reflection.TypeAttributes.AnsiClass | System.Reflection.TypeAttributes.AutoClass,
                    typeof(MulticastDelegate));

                var ctor = typeBuilder.DefineConstructor(
                    System.Reflection.MethodAttributes.RTSpecialName | System.Reflection.MethodAttributes.HideBySig
                        | System.Reflection.MethodAttributes.Public,
                    System.Reflection.CallingConventions.Standard,
                    new[] { typeof(object), typeof(IntPtr) });
                ctor.SetImplementationFlags(System.Reflection.MethodImplAttributes.Runtime | System.Reflection.MethodImplAttributes.Managed);

                var invoke = typeBuilder.DefineMethod(
                    "Invoke",
                    System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.HideBySig
                        | System.Reflection.MethodAttributes.NewSlot | System.Reflection.MethodAttributes.Virtual,
                    returnType,
                    paramTypes);
                invoke.SetImplementationFlags(System.Reflection.MethodImplAttributes.Runtime | System.Reflection.MethodImplAttributes.Managed);

                return typeBuilder.CreateType()!;
            }
        }

        /// <summary>Bildet einen Skript-Typ (Parameter- oder Rückgabetyp einer
        /// `extern`-Deklaration) auf den passenden CLR-Typ für dynamisches
        /// Linking ab - dieselbe Menge unterstützter Typen wie beim
        /// Pointer-Marshalling (MarshalArgsOut/WriteNativeValue): bool/int/
        /// float/char/string direkt, jeder Pointer-Typ als IntPtr (die echte,
        /// per MarshalArgsOut bereitgestellte native Adresse).</summary>
        private static Type MapExternClrType(TypeRef? t, string externName)
        {
            if (t == null)
                throw new InvalidOperationException(
                    $"extern '{externName}': ein Parameter-/Rückgabetyp fehlt oder ist zu unspezifisch " +
                    "für dynamisches Linking (bool/int/float/char/string oder ein Pointer-Typ nötig).");
            if (t.PointerDepth > 0) return typeof(IntPtr);
            return t.BaseName switch
            {
                "bool" => typeof(bool),
                "int" => typeof(long),
                "float" => typeof(double),
                "char" => typeof(char),
                "string" => typeof(string),
                _ => throw new InvalidOperationException(
                    $"extern '{externName}': Typ '{t.BaseName}' kann nicht dynamisch verlinkt werden " +
                    "(unterstützt: bool/int/float/char/string/Pointer)."),
            };
        }

        /// <summary>Schreibt einen primitiven Skript-Wert in 8 Byte natives
        /// Speicher (reicht für alle unterstützten Primitivtypen).</summary>
        private static void WriteNativeValue(IntPtr ptr, Value v)
        {
            switch (v.Kind)
            {
                case ValueKind.Int:
                    System.Runtime.InteropServices.Marshal.WriteInt64(ptr, v.AsInt());
                    break;
                case ValueKind.Float:
                    var bytes = BitConverter.GetBytes(v.AsFloat());
                    System.Runtime.InteropServices.Marshal.Copy(bytes, 0, ptr, bytes.Length);
                    break;
                case ValueKind.Bool:
                    System.Runtime.InteropServices.Marshal.WriteByte(ptr, (byte)(v.AsBool() ? 1 : 0));
                    break;
                case ValueKind.Char:
                    System.Runtime.InteropServices.Marshal.WriteInt32(ptr, v.AsChar());
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Ein Zeiger auf einen Wert vom Typ {v.Kind} kann nicht an eine extern-Funktion " +
                        "marshalled werden (unterstützt: int/float/bool/char).");
            }
        }

        private static Value ReadNativeValue(IntPtr ptr, ValueKind kind) => kind switch
        {
            ValueKind.Int => Value.MakeInt(System.Runtime.InteropServices.Marshal.ReadInt64(ptr)),
            ValueKind.Float => Value.MakeFloat(ReadNativeDouble(ptr)),
            ValueKind.Bool => Value.MakeBool(System.Runtime.InteropServices.Marshal.ReadByte(ptr) != 0),
            ValueKind.Char => Value.MakeChar((char)System.Runtime.InteropServices.Marshal.ReadInt32(ptr)),
            _ => throw new InvalidOperationException(
                $"Nicht unterstützter Zeiger-Zieltyp {kind} beim Zurücklesen aus nativem Speicher."),
        };

        private static double ReadNativeDouble(IntPtr ptr)
        {
            var buf = new byte[8];
            System.Runtime.InteropServices.Marshal.Copy(ptr, buf, 0, 8);
            return BitConverter.ToDouble(buf, 0);
        }

        private void BeginConstruction(ObjectInstance instance, FunctionProto ctorProto, Value[] args)
        {
            _frames.Push(new CallFrame(_currentChunk, _ip, _currentScope, _currentThis, instance));

            var ctorScope = new Scope(_globalScope);
            foreach (var a in args) ctorScope.DefineSlot(a);

            _currentThis = instance;
            _currentScope = ctorScope;
            _currentChunk = ctorProto.Chunk;
            _ip = 0;
        }

        // -----------------------------------------------------------
        // Exceptions
        // -----------------------------------------------------------

        /// <summary>Sucht - von innen nach außen - einen registrierten Handler mit
        /// passender Catch-Klausel. Handler, an denen dabei vorbeipropagiert wird,
        /// werden verworfen (samt ihres finally, falls vorhanden). Wird kein
        /// Handler gefunden, bricht die Ausführung mit UncaughtScriptException ab.</summary>
        private void ThrowException(Value exceptionValue)
        {
            var excInstance = RequireObjectInstance(exceptionValue, "throw");

            // Das Exception-Objekt gehört noch dem werfenden Scope - der wird
            // beim (späteren, evtl. verzögerten) Unwinding aufgelöst. Ohne
            // diesen Ownership-Transfer würde die Exception dabei selbst
            // mit-zerstört, bevor der catch-Block sie lesen kann.
            excInstance.TakeGlobal(_globalScope);

            while (_handlers.Count > 0)
            {
                var handler = _handlers[^1];
                _handlers.RemoveAt(_handlers.Count - 1);

                int? matchedAddr = FindMatchingCatch(handler.Template, excInstance);

                if (matchedAddr != null)
                {
                    // WICHTIG: hier NICHT destruktiv abwickeln (UnwindTo) -
                    // stattdessen den Wurfstellen-Zustand einfrieren
                    // (CaptureContinuation), damit ein mögliches `resume()`
                    // später exakt hierher zurückspringen kann. Nichts wird
                    // zerstört, solange nicht klar ist, ob resume() aufgerufen
                    // wird oder nicht (siehe ClearPendingResume).
                    var continuation = CaptureContinuation(handler.FrameDepthAtEntry);
                    _pendingResumes[excInstance] = new PendingResume(continuation, handler);

                    var catchScope = new Scope(handler.TargetScope);
                    catchScope.DefineSlot(exceptionValue);
                    _currentScope = catchScope;
                    _currentChunk = handler.Chunk;
                    _ip = matchedAddr.Value;
                    return;
                }

                // Kein Match an diesem Handler - der ist damit endgültig
                // verworfen (kein resume() für nicht-passende Handler
                // möglich), also ganz normal destruktiv abwickeln.
                UnwindTo(handler.FrameDepthAtEntry, handler.TargetScope);

                if (handler.Template.FinallyProtoIdx is int protoIdx)
                    RunFinallyNested(handler.Chunk.Functions[protoIdx]);
            }

            // Kein Handler in DIESER VM-Instanz hat gematcht. Auf einem
            // Fire-Thread (nicht dem Main-Thread) heißt das laut Design NICHT
            // "Programm abbrechen", sondern "diesen Thread sauber beenden und
            // die Exception (ohne Resumability - die ist ohnehin nie mehr als
            // rein LOKALER VM-Zustand entstanden, siehe _pendingResumes-Doku)
            // an den Main-Thread zustellen" (docs/THREADING_DESIGN.md 6.2).
            if (IsFireThreadVm)
            {
                _pendingThreadExceptions.Enqueue(excInstance);
                UnwindForShutdown(); // _handlers ist an dieser Stelle ohnehin schon leer, siehe Schleife oben - äquivalent zu UnwindTo(0, _globalScope), aber ein Aufruf statt Code-Duplikat.
                _stopExecutionRequested = true;
                return;
            }

            // Kein Handler in DIESER VM-Instanz hat gematcht - das Programm
            // hält hier an (siehe UnhandledException-Doku: gesetzt statt
            // geworfen, Run() kehrt gleich danach über den nächsten
            // CheckShutdownSignals-Prüfpunkt ganz normal zurück).
            UnhandledException = excInstance;
            _stopExecutionRequested = true;
        }

        /// <summary>Friert den aktuellen Ausführungszustand ein, indem die
        /// Frames bis zur Ziel-Tiefe von `_frames` abgehoben werden - OHNE sie
        /// (oder die durchlaufenen Scopes) per Release() freizugeben. `this`
        /// wird dabei für die LIVE-VM genauso nachgezogen wie bei UnwindTo,
        /// damit der Handler-Kontext danach korrekt dasteht; die
        /// zurückgegebene SavedContinuation trägt dagegen den ORIGINALEN
        /// Wurfstellen-Zustand (vor dem Nachziehen).</summary>
        private SavedContinuation CaptureContinuation(int targetFrameDepth)
        {
            var savedChunk = _currentChunk;
            var savedIp = _ip;
            var savedScope = _currentScope;
            var savedThis = _currentThis;
            var frames = new List<CallFrame>();

            while (_frames.Count > targetFrameDepth)
            {
                var frame = _frames.Pop();
                frames.Add(frame);
                _currentThis = frame.ReturnThis;
            }

            return new SavedContinuation(savedChunk, savedIp, savedScope, savedThis, frames);
        }

        /// <summary>Löst eine eingefrorene, nie fortgesetzte Wurfstellen-
        /// Continuation nachträglich sauber auf (Ownership-Kaskade inkl.
        /// Destruktoren) - spiegelt exakt UnwindTo's Logik, nur auf den
        /// GESICHERTEN (kopierten) Daten statt auf dem LIVE-VM-Zustand.</summary>
        private void DiscardContinuation(SavedContinuation continuation, Scope targetScope)
        {
            var scope = continuation.Scope;
            int frameIdx = 0;

            while (frameIdx < continuation.Frames.Count || !ReferenceEquals(scope, targetScope))
            {
                bool atFrameBase = ReferenceEquals(scope!.Parent, _globalScope);
                scope.Release(this);

                if (atFrameBase && frameIdx < continuation.Frames.Count)
                {
                    scope = continuation.Frames[frameIdx].ReturnScope;
                    frameIdx++;
                }
                else
                {
                    scope = scope.Parent;
                }
            }
        }

        private int? FindMatchingCatch(HandlerTemplate template, ObjectInstance exc)
        {
            foreach (var (typeName, addr) in template.Catches)
            {
                if (typeName == null) return addr;
                if (InstanceMatchesClassName(exc, typeName)) return addr;
            }
            return null;
        }

        /// <summary>Läuft die Basisklassen-Kette einer Instanz hoch und prüft auf
        /// Namensgleichheit mit `typeName` - Grundlage von typisiertem `catch`
        /// UND von `is of` (siehe IsOfType). 'Exception' matcht immer
        /// (eingebaute Basisklasse ohne eigene RuntimeClass, siehe
        /// Compiler.CompileClasses-Kommentar) - das gilt bewusst pauschal für
        /// jede Instanz, nicht nur für tatsächlich von 'Exception' abgeleitete
        /// Klassen, da diese Information (die rohe BaseNames-Liste) auf
        /// RuntimeClass-Ebene nicht mehr vorliegt.</summary>
        private bool InstanceMatchesClassName(ObjectInstance instance, string typeName)
        {
            if (typeName == "Exception") return true;
            if (!_classes.TryGetValue(instance.ClassDef.Name, out var rc)) return false;
            for (; rc != null; rc = rc.Base)
                if (rc.Name == typeName) return true;
            return false;
        }

        /// <summary>`wert is of Typ` (SPEC 6): bei Basistyp-Namen einfacher
        /// Kind-Vergleich, bei Klassennamen rekursiv über die Basisklassen-Kette
        /// (InstanceMatchesClassName).</summary>
        private bool IsOfType(Value v, string typeName)
        {
            switch (typeName)
            {
                case "bool": return v.Kind == ValueKind.Bool;
                case "int": return v.Kind == ValueKind.Int;
                case "float": return v.Kind == ValueKind.Float;
                case "char": return v.Kind == ValueKind.Char;
                case "string": return v.Kind == ValueKind.String;
                case "undefined": return v.Kind == ValueKind.Undefined;
                case "class": return v.Kind == ValueKind.Class;
            }
            if (v.Kind != ValueKind.Class) return false;
            return InstanceMatchesClassName((ObjectInstance)v.AsObjectRef(), typeName);
        }

        /// <summary>Wickelt Scopes/Frames ab, bis genau `targetFrameDepth`/
        /// `targetScope` erreicht ist - dabei wird für jede verlassene Scope ganz
        /// normal Release() aufgerufen (Ownership-Kaskade inkl. Destruktoren
        /// laufen also auch beim Abbruch durch eine Exception korrekt). Nutzt
        /// aus, dass JEDE Frame-Basis-Scope als Parent immer direkt den globalen
        /// Scope hat (so legen Call/CallMethod/NewObject/etc. ihre Scopes an) -
        /// das erkennt eine Frame-Grenze, ohne sie separat mitführen zu müssen.</summary>
        private void UnwindTo(int targetFrameDepth, Scope targetScope)
        {
            while (_frames.Count > targetFrameDepth || !ReferenceEquals(_currentScope, targetScope))
            {
                bool atFrameBase = ReferenceEquals(_currentScope.Parent, _globalScope);
                _currentScope.Release(this);

                if (atFrameBase && _frames.Count > targetFrameDepth)
                {
                    var frame = _frames.Pop();
                    _currentChunk = frame.ReturnChunk;
                    _ip = frame.ReturnIp;
                    _currentScope = frame.ReturnScope;
                    _currentThis = frame.ReturnThis;
                }
                else
                {
                    _currentScope = _currentScope.Parent
                        ?? throw new InvalidOperationException(
                            "Unwind über den globalen Scope hinaus (inkonsistenter Handler-Zustand).");
                }
            }
        }

        /// <summary>Führt einen finally-Block verschachtelt aus (wie
        /// RunDestructor) - für den Fall, dass eine Exception an einem Handler
        /// vorbei nach außen propagiert, dessen finally aber trotzdem laufen
        /// muss, bevor die Suche nach einem passenden Handler weitergeht.</summary>
        private void RunFinallyNested(FunctionProto proto)
        {
            _frames.Push(new CallFrame(_currentChunk, _ip, _currentScope, _currentThis, null));
            int targetDepth = _frames.Count;

            _currentScope = new Scope(_globalScope);
            _currentChunk = proto.Chunk;
            _ip = 0;

            RunNestedUntil(targetDepth);
            Pop(); // bedeutungsloser Rückgabewert des finally-Protos, siehe RunDestructor
        }

        /// <summary>Ruft eine Methode auf `obj` verschachtelt auf (siehe
        /// RunDestructor/RunFinallyNested) und liefert deren Rückgabewert -
        /// oder `null`, falls der Aufruf NICHT normal per `Return` beendet
        /// wurde, sondern eine Exception per Continuation-Sprung (ThrowException/
        /// ResumeException) die Ausführung komplett woanders hin umgeleitet hat
        /// (z.B. in einen `catch` außerhalb dieses Aufrufs). In dem Fall wurde
        /// NIE ein Rückgabewert gepusht, und der Aufrufer darf selbst nichts
        /// mehr tun (kein eigenes Push, keine weitere Verarbeitung) - die
        /// Ausführung läuft ja bereits an anderer Stelle weiter. Erkannt wird
        /// das NICHT über die Frame-Tiefe (die könnte durch eine Verschachtelung
        /// zufällig wieder exakt passen, siehe z.B. ein try/catch auf genau
        /// dieser Ebene), sondern robust darüber, ob Chunk/Ip/Scope nach der
        /// verschachtelten Ausführung exakt wieder beim Ausgangszustand
        /// gelandet sind - das gilt garantiert nur bei einem echten, normalen
        /// Return (der genau diese drei Werte aus dem gepushten Frame
        /// wiederherstellt).</summary>
        /// <summary>Nimmt einen Snapshot ALLER aktuellen Werte des globalen
        /// Scopes DIESER VM auf - für native Callback-Registrierung gedacht
        /// (siehe Runtime.FireRuntime.CallCallback), dieselbe Grund-Idee wie
        /// der interne Snapshot vor einem 'fire'-Block (OpCode.Fire), nur
        /// von AUSSEN (Host-C#-Code) statt von einem Skript-Opcode
        /// ausgelöst. Der Aufrufer MUSS sicherstellen, dass dieser Aufruf
        /// NICHT gleichzeitig mit einer laufenden Bytecode-Ausführung DIESER
        /// VM auf einem ANDEREN Thread passiert (kein eingebautes Locking
        /// hier, aus demselben Race-Grund wie beim 'fire'-Snapshot) - für
        /// die übliche Verwendung (der Host registriert einen Callback,
        /// direkt im Anschluss an den nativen Registrierungsaufruf, während
        /// das Skript also gerade in genau diesem Aufruf steht, nicht
        /// nebenläufig woanders läuft) ist das automatisch gegeben.</summary>
        public IReadOnlyList<Value> SnapshotGlobals()
        {
            var snapshot = new Value[_globalScope.SlotCount];
            for (int i = 0; i < snapshot.Length; i++)
                snapshot[i] = _globalScope.GetSlot(i);
            return snapshot;
        }

        /// <summary>Ruft eine Lambda als die EINZIGE Ausführung DIESER VM-
        /// Instanz auf - anders als CallMethodNested (verschachtelt in ein
        /// bereits laufendes Hauptprogramm) für eine FRISCH dafür angelegte
        /// VM ohne eigenes "Hauptprogramm" (siehe Runtime.FireRuntime.
        /// CallCallback, für native Callbacks). `this` im Lambda-Körper ist
        /// `lambda.OnTarget`, wie bei jedem anderen Lambda-Aufruf (SPEC
        /// 4.2/Runtime.LambdaValue) - die Lambda sieht dabei laut
        /// Sprachdefinition ohnehin nur ihren eigenen Scope plus DIESER
        /// VM-Instanz globalen Scope (`_globalScope`, hier beim Konstruieren
        /// übergeben), nie die Locals eines wie auch immer gearteten
        /// "aufrufenden" Kontexts - der Aufrufer dieser Methode ist reiner
        /// C#-Code, kein Skript-Scope.</summary>
        public Value CallLambdaEntry(LambdaValue lambda, Value[] args)
        {
            CheckArity(lambda.Proto, args.Length);
            args = FillDefaultArgs(lambda.Proto, args, lambda.OnTarget);

            var savedChunk = _currentChunk;
            var savedIp = _ip;
            var savedScope = _currentScope;

            _frames.Push(new CallFrame(savedChunk, savedIp, savedScope, _currentThis, null));
            int targetDepth = _frames.Count;

            var funcScope = new Scope(_globalScope);
            foreach (var a in args) funcScope.DefineSlot(a);

            _currentThis = lambda.OnTarget;
            _currentScope = funcScope;
            _currentChunk = lambda.Proto.Chunk;
            _ip = 0;

            RunNestedUntil(targetDepth);

            return Pop();
        }

        private Value? CallMethodNested(ObjectInstance obj, string methodName, Value[] args)
        {
            var rc = ResolveClass(obj.ClassDef.Name);
            var (proto, declaringRcNested, accessNested) = rc.FindMethodWithAccess(methodName, args.Length);
            if (proto == null)
                throw new InvalidOperationException(
                    $"Methode '{methodName}' nicht gefunden auf '{rc.Name}' (für eine Property oder Operator-Überladung benötigt).");
            // Deckt sowohl Property-Zugriffe (get_X/set_X, siehe VM.GetField/
            // SetField) als auch Operator-Überladungen ab (siehe
            // Parser.ParseOperatorMember) - Operatoren bekommen nie einen
            // expliziten Modifikator (immer Public), die Prüfung greift hier
            // also praktisch nur für Properties.
            if (ExecutionMode != VmExecutionMode.Performance && !IsMemberAccessAllowed(declaringRcNested!, accessNested))
            {
                ThrowAccessDenied(
                    $"'{methodName}' von '{declaringRcNested!.Name}' ist {DescribeAccess(accessNested)} " +
                    "und von hier aus nicht zugreifbar.");
                return null;
            }
            CheckArity(proto, args.Length);
            args = FillDefaultArgs(proto, args, obj);

            var savedChunk = _currentChunk;
            var savedIp = _ip;
            var savedScope = _currentScope;

            _frames.Push(new CallFrame(savedChunk, savedIp, savedScope, _currentThis, null));
            int targetDepth = _frames.Count;

            var scope = new Scope(_globalScope);
            foreach (var a in args) scope.DefineSlot(a);

            _currentThis = obj;
            _currentScope = scope;
            _currentChunk = proto.Chunk;
            _ip = 0;

            RunNestedUntil(targetDepth);

            bool completedNormally =
                ReferenceEquals(_currentChunk, savedChunk) && _ip == savedIp && ReferenceEquals(_currentScope, savedScope);
            return completedNormally ? Pop() : (Value?)null;
        }

        /// <summary>Konstruiert eine neue Instanz von `rc` verschachtelt (wie
        /// CallMethodNested) und liefert sie fertig konstruiert zurück - für
        /// von der VM SELBST erzeugte Exceptions (siehe ThrowIndexOutOfBounds),
        /// wo kein Skript-`new` im Bytecode steht, das die Instanz erzeugen
        /// könnte. Wie CallMethodNested robust gegen eine Exception, die WÄHREND
        /// der Konstruktion auftritt (Continuation-Sprung statt normalem
        /// Return) - in dem (sehr seltenen) Fall gibt es keine fertige Instanz,
        /// das wird als harter interner Fehler behandelt statt versucht,
        /// rekursiv noch eine WEITERE Exception dafür zu bauen.</summary>
        private ObjectInstance ConstructNested(RuntimeClass rc, Value[] args)
        {
            var ctorProto = rc.FindConstructor(args.Length)
                ?? throw new InvalidOperationException(DescribeConstructorNotFound(rc, args.Length));
            var instance = new ObjectInstance(rc.Decl, _currentScope, rc);
            if (rc.IsActor) instance.Mailbox = new ActorMailbox();
            args = FillDefaultArgs(ctorProto, args, instance);

            var savedChunk = _currentChunk;
            var savedIp = _ip;
            var savedScope = _currentScope;

            _frames.Push(new CallFrame(savedChunk, savedIp, savedScope, _currentThis, instance));
            int targetDepth = _frames.Count;

            var ctorScope = new Scope(_globalScope);
            foreach (var a in args) ctorScope.DefineSlot(a);

            _currentThis = instance;
            _currentScope = ctorScope;
            _currentChunk = ctorProto.Chunk;
            _ip = 0;

            RunNestedUntil(targetDepth);

            bool completedNormally =
                ReferenceEquals(_currentChunk, savedChunk) && _ip == savedIp && ReferenceEquals(_currentScope, savedScope);
            if (!completedNormally)
                throw new InvalidOperationException(
                    $"Interner Fehler: Konstruktion von '{rc.Name}' wurde durch eine Exception unterbrochen " +
                    "(Exception während des Baus einer VM-internen Exception-Instanz).");

            Pop(); // Return am Ende des Konstruktors pusht 'instance' selbst (siehe BeginConstruction/frame.ConstructedInstance) - haben wir schon direkt, hier verwerfen
            return instance;
        }

        /// <summary>Baut eine `IndexOutOfBoundsException`-Instanz (Prelude) und
        /// wirft sie ganz normal über ThrowException - macht einen ungültigen
        /// Array-Index zu einer echten, per `try`/`catch` fangbaren Skript-
        /// Exception statt eines rohen C#-Fehlers, der das ganze Programm
        /// abbrechen würde. Aufgerufen aus ArrayGet/ArraySet, wenn
        /// ScriptArray/ByteBuffer.TryGet/TrySet `false` liefert (bewusst kein
        /// throw/catch dort selbst - siehe ScriptArray-Doku, C++-Portier-
        /// barkeit).</summary>
        private void ThrowIndexOutOfBounds(long index, int length)
        {
            var rc = ResolveClass("IndexOutOfBoundsException");
            string msg = $"Array-Index {index} außerhalb des gültigen Bereichs (Länge {length}).";
            var args = new[] { Value.MakeString(msg), Value.MakeInt(index), Value.MakeInt(length) };
            var instance = ConstructNested(rc, args);
            ThrowException(Value.MakeClassRef(instance));
        }

        private void ThrowAccessDenied(string message)
        {
            var rc = ResolveClass("AccessDeniedException");
            var args = new[] { Value.MakeString(message) };
            var instance = ConstructNested(rc, args);
            ThrowException(Value.MakeClassRef(instance));
        }

        /// <summary>Prüft, ob der GERADE ausführende Code laut
        /// Zugriffsmodifikator auf ein Mitglied zugreifen darf, das
        /// `declaringRc` selbst deklariert hat. `Public` (oder gar kein
        /// Eintrag vorhanden - Rückwärtskompatibilität) ist immer erlaubt.
        ///
        /// "Wer greift gerade zu" ist `_currentChunk.OwnerClass` - die
        /// Klasse, deren Methode/Konstruktor/Property-Accessor GERADE
        /// ausführt (siehe Chunk.OwnerClass-Doku). BEWUSST NICHT
        /// `_currentThis`s konkrete Klasse: eine `Derived`-Instanz, die eine
        /// geerbte, nicht überschriebene `Base`-Methode aufruft (oder deren
        /// Konstruktion gerade `Base`s eigenen Konstruktor-Code über
        /// ConstructBase durchläuft), hat `this` konkret als `Derived`
        /// gebunden, obwohl `Base`s eigener Code läuft - für "darf DIESER
        /// Code auf Base's privates Mitglied zugreifen" zählt, WESSEN CODE
        /// läuft, nicht welcher konkreten Klasse die Instanz angehört (sonst
        /// würde z.B. jede Konstruktion einer abgeleiteten Klasse an einem
        /// privaten Feld-Initialisierer der Basisklasse scheitern - genau
        /// dieser Fehler wurde hier gefunden und korrigiert).</summary>
        private bool IsMemberAccessAllowed(RuntimeClass declaringRc, AccessModifier access)
        {
            if (access == AccessModifier.Public) return true;

            var callerRc = _currentChunk.OwnerClass;
            if (callerRc == null) return false;

            if (access == AccessModifier.Private)
                return ReferenceEquals(callerRc, declaringRc);

            // Protected: callerRc selbst oder irgendeine davon abgeleitete Klasse.
            for (var rc = callerRc; rc != null; rc = rc.Base)
                if (ReferenceEquals(rc, declaringRc)) return true;
            return false;
        }

        private static string DescribeAccess(AccessModifier access) => access switch
        {
            AccessModifier.Private => "private",
            AccessModifier.Protected => "protected",
            _ => "public",
        };

        /// <summary>Grundlage für JEDE arithmetische/bitweise/Vergleichs-
        /// Operation (siehe die entsprechenden OpCode-Handler): ist der LINKE
        /// Operand ein Objekt MIT einer passenden Operator-Überladungs-
        /// Methode (siehe Ast.MethodDecl-Namenskonvention "operator+" etc.,
        /// erzeugt von Parser.ParseOperatorMember), wird DIESE genestet
        /// aufgerufen (`this` = der linke Operand, ein Parameter = der
        /// rechte) - GENAU dasselbe Muster, das ArrayGet/ArraySet schon immer
        /// für 'GetIndex'/'SetIndex' verwenden, hier verallgemeinert für ALLE
        /// binären Operatoren. Sonst (kein Objekt, oder ein Objekt ohne
        /// passende Methode) die eingebaute Operation `op`. Bewusst NUR der
        /// LINKE Operand wird auf eine Überladung geprüft (kein Pythons
        /// `__radd__`-Äquivalent für den rechten Operanden) - siehe
        /// ParseOperatorMember-Doku für die Begründung.</summary>
        /// <summary>Eingebaute Methoden auf primitiven Werten (String/Char/
        /// Int/Buffer, siehe SPEC 8.10 für die vollständige Tabelle) -
        /// 'obj.Method()' geht normalerweise auf eine ObjectInstance (siehe
        /// OpCode.CallMethod); für jeden anderen ValueKind prüft diese Liste
        /// stattdessen die eingebauten Konvertierungen. Liefert false (statt
        /// zu werfen), wenn kein Treffer vorliegt - der Aufrufer entscheidet
        /// dann selbst, wie er das meldet.</summary>
        private static bool TryCallBuiltinMethod(Value target, string methodName, Value[] args, out Value result)
        {
            result = default;
            switch (target.Kind)
            {
                case ValueKind.String:
                    if (methodName == "ToBytes" && args.Length == 0)
                    {
                        result = Value.MakeBuffer(ByteConversions.AsciiEncode(target.AsString()));
                        return true;
                    }
                    if (methodName == "ToUnicode" && args.Length == 1)
                    {
                        result = Value.MakeBuffer(ByteConversions.UnicodeEncodeString(target.AsString(), (int)args[0].AsInt()));
                        return true;
                    }
                    return false;

                case ValueKind.Char:
                    if (methodName == "ToByte" && args.Length == 0)
                    {
                        result = Value.MakeInt((byte)target.AsChar(), width: NumericWidth.W8);
                        return true;
                    }
                    if (methodName == "ToUnicode" && args.Length == 1)
                    {
                        result = Value.MakeBuffer(ByteConversions.UnicodeEncodeChar(target.AsChar(), (int)args[0].AsInt()));
                        return true;
                    }
                    return false;

                case ValueKind.Int:
                    if (methodName == "ToChar" && args.Length == 0)
                    {
                        result = Value.MakeChar((char)(byte)target.AsInt());
                        return true;
                    }
                    return false;

                case ValueKind.Buffer:
                {
                    var buf = target.AsBuffer();
                    if (methodName == "ToString" && args.Length == 0)
                    {
                        result = Value.MakeString(ByteConversions.AsciiDecode(buf));
                        return true;
                    }
                    if (methodName == "ToUnicode" && args.Length == 0)
                    {
                        result = Value.MakeString(ByteConversions.UnicodeDecodeString(buf, 2));
                        return true;
                    }
                    if (methodName == "ToUnicode" && args.Length == 1)
                    {
                        result = Value.MakeString(ByteConversions.UnicodeDecodeString(buf, (int)args[0].AsInt()));
                        return true;
                    }
                    if (methodName == "ToUnicodeChar" && args.Length == 0)
                    {
                        result = Value.MakeChar(ByteConversions.UnicodeDecodeChar(buf, 2));
                        return true;
                    }
                    if (methodName == "ToUnicodeChar" && args.Length == 1)
                    {
                        result = Value.MakeChar(ByteConversions.UnicodeDecodeChar(buf, (int)args[0].AsInt()));
                        return true;
                    }
                    // Endianness (siehe ByteBuffer-Doku): stimmt die
                    // AKTUELLE Order schon, liefert eine reine Kopie (kein
                    // Byte-Swap nötig); sonst eine gespiegelte Kopie mit der
                    // neuen Order. Das Original bleibt in JEDEM Fall
                    // unverändert - wie jede andere "gibt einen neuen Wert
                    // zurück"-Konvertierung in dieser Sprache.
                    if (methodName == "ToLittleEndian" && args.Length == 0)
                    {
                        result = Value.MakeBuffer(buf.Order == ByteOrder.Little ? buf.Clone() : buf.Reversed(ByteOrder.Little));
                        return true;
                    }
                    if (methodName == "ToBigEndian" && args.Length == 0)
                    {
                        result = Value.MakeBuffer(buf.Order == ByteOrder.Big ? buf.Clone() : buf.Reversed(ByteOrder.Big));
                        return true;
                    }
                    return false;
                }

                default:
                    return false;
            }
        }

        private void BinaryNumericOrOperator(Func<Value, Value, Value> op, string operatorMethodName)
        {
            var b = Pop();
            var a = Pop();
            if (a.Kind == ValueKind.Class)
            {
                var obj = (ObjectInstance)a.AsObjectRef();
                var rc = ResolveClass(obj.ClassDef.Name);
                if (rc.FindMethod(operatorMethodName, 1) != null)
                {
                    var result = CallMethodNested(obj, operatorMethodName, new[] { b });
                    // null == die Überladung wurde durch eine geworfene
                    // Exception verlassen (siehe CallMethodNested-Doku) -
                    // dann NICHT pushen, die Ausführung läuft bereits
                    // anderswo weiter.
                    if (result != null) Push(result.Value);
                    return;
                }
            }
            Push(op(a, b));
        }

        private RuntimeClass ResolveClass(string name) =>
            _classes.TryGetValue(name, out var rc)
                ? rc
                : throw new InvalidOperationException($"Unbekannte Klasse '{name}' zur Laufzeit.");

        private static ObjectInstance RequireObjectInstance(Value v, string context)
        {
            if (v.Kind != ValueKind.Class)
                throw new InvalidOperationException($"{context} auf einem Wert vom Typ {v.Kind}, der kein Objekt ist.");
            return (ObjectInstance)v.AsObjectRef();
        }

        private static void CheckArity(FunctionProto proto, int argCount)
        {
            if (argCount == proto.ParamCount) return;
            if (argCount < proto.ParamCount && RuntimeClass.AllTrailingHaveDefaults(proto, argCount)) return;
            throw new InvalidOperationException(
                $"Falsche Argumentanzahl: erwartet {proto.ParamCount}" +
                (argCount < proto.ParamCount ? " (die fehlenden Parameter haben keinen Standardwert)" : "") +
                $", erhalten {argCount}.");
        }

        /// <summary>Baut bei Bedarf das vollständige Argument-Array für einen
        /// Aufruf gegen `proto` auf - ergänzt fehlende TRAILING Parameter um
        /// ihre ausgewerteten Standardwerte (siehe FunctionProto.ParamDefaults,
        /// Ast.LambdaParam.DefaultValue-Doku). Der Aufrufer muss VORHER schon
        /// per CheckArity/RuntimeClass.FindMethod/FindConstructor sichergestellt
        /// haben, dass genug Standardwerte vorhanden sind. `thisForDefaults`
        /// wird beim Auswerten gebunden (z.B. für einen Standardwert wie
        /// `= this.irgendwas`) - null, wenn an dieser Stelle kein `this`
        /// sinnvoll ist (z.B. freistehende Lambdas ohne `on`-Bindung).</summary>
        private Value[] FillDefaultArgs(FunctionProto proto, Value[] suppliedArgs, object? thisForDefaults)
        {
            if (suppliedArgs.Length == proto.ParamCount) return suppliedArgs;

            var result = new Value[proto.ParamCount];
            Array.Copy(suppliedArgs, result, suppliedArgs.Length);
            for (int i = suppliedArgs.Length; i < proto.ParamCount; i++)
            {
                var defaultProto = i < proto.ParamDefaults.Count ? proto.ParamDefaults[i] : null;
                if (defaultProto == null)
                    throw new InvalidOperationException(
                        $"Interner Fehler: Parameter {i} von Aufruf-Ziel hat keinen Standardwert " +
                        "(CheckArity hätte das schon abfangen müssen).");
                result[i] = EvaluateDefaultNested(defaultProto, thisForDefaults);
            }
            return result;
        }

        /// <summary>Wertet einen Standardwert-Proto (0 Argumente) verschachtelt
        /// aus - dieselbe Technik wie CallMethodNested/ConstructNested (siehe
        /// dort für die Erklärung, warum robust gegen eine Exception geprüft
        /// wird, die die Auswertung per Continuation-Sprung verlässt: bei
        /// einem einfachen Standardwert-Ausdruck ist das zwar ein sehr seltener
        /// Fall, aber kein grundsätzlich unmöglicher - z.B. ein Standardwert,
        /// der selbst einen Methodenaufruf enthält, der wirft).</summary>
        private Value EvaluateDefaultNested(FunctionProto defaultProto, object? thisForDefaults)
        {
            var savedChunk = _currentChunk;
            var savedIp = _ip;
            var savedScope = _currentScope;
            var savedThis = _currentThis;

            _frames.Push(new CallFrame(savedChunk, savedIp, savedScope, savedThis, null));
            int targetDepth = _frames.Count;

            _currentThis = thisForDefaults;
            _currentScope = new Scope(_globalScope);
            _currentChunk = defaultProto.Chunk;
            _ip = 0;

            RunNestedUntil(targetDepth);

            bool completedNormally =
                ReferenceEquals(_currentChunk, savedChunk) && _ip == savedIp && ReferenceEquals(_currentScope, savedScope);
            if (!completedNormally)
                throw new InvalidOperationException(
                    "Interner Fehler: Auswertung eines Standardwerts wurde durch eine Exception unterbrochen.");

            _currentThis = savedThis;
            return Pop();
        }

        /// <summary>Baut eine hilfreiche Fehlermeldung für einen gescheiterten
        /// Methodenaufruf (RuntimeClass.FindMethod hat nichts gefunden) -
        /// unterscheidet "Methode existiert unter diesem Namen gar nicht" von
        /// "Methode existiert, aber keine Überladung mit dieser Argumentzahl"
        /// (und listet im zweiten Fall die tatsächlich vorhandenen
        /// Argumentzahlen mit auf, über die ganze Basisklassen-Kette).</summary>
        private static string DescribeMethodNotFound(RuntimeClass rc, string methodName, int argCount)
        {
            var arities = new List<int>();
            for (var c = rc; c != null; c = c.Base)
                if (c.Methods.TryGetValue(methodName, out var overloads))
                    arities.AddRange(overloads.Select(p => p.ParamCount));

            if (arities.Count == 0)
                return $"Methode '{methodName}' nicht gefunden auf '{rc.Name}'.";

            var distinctArities = arities.Distinct().OrderBy(x => x);
            return $"Methode '{methodName}' auf '{rc.Name}' hat keine Überladung mit {argCount} Argument(en) " +
                   $"(vorhanden: {string.Join(", ", distinctArities)} Argument(e)).";
        }

        /// <summary>Analog zu DescribeMethodNotFound, für Konstruktoren (keine
        /// Basisklassen-Kette, siehe RuntimeClass.Constructors-Doku).</summary>
        private static string DescribeConstructorNotFound(RuntimeClass rc, int argCount)
        {
            if (rc.Constructors.Count == 0)
                return $"Klasse '{rc.Name}' hat keinen Konstruktor."; // sollte nie vorkommen, immer mind. 1 synthetisiert
            var arities = rc.Constructors.Keys.OrderBy(x => x);
            return $"Klasse '{rc.Name}' hat keinen Konstruktor mit {argCount} Argument(en) " +
                   $"(vorhanden: {string.Join(", ", arities)} Argument(e)).";
        }

        private static ValueKind TagToKind(TypeTag tag) => tag switch
        {
            TypeTag.Bool => ValueKind.Bool,
            TypeTag.Int => ValueKind.Int,
            TypeTag.Float => ValueKind.Float,
            TypeTag.Char => ValueKind.Char,
            TypeTag.String => ValueKind.String,
            _ => throw new InvalidOperationException($"Unbekannter TypeTag {tag}"),
        };

        /// <summary>Verpackt einen 'on'-Zielwert für LambdaValue.OnTarget. Bei
        /// einer Objektreferenz die ObjectInstance direkt, sonst den Value als
        /// object geboxt.</summary>
        private static object BoxValueForOnTarget(Value v) =>
            v.Kind == ValueKind.Class ? v.AsObjectRef() : v;

        // -----------------------------------------------------------
        // Stack- & Code-Zugriff
        // -----------------------------------------------------------
        private void Push(Value v) => _stack.Add(v);

        private Value Pop()
        {
            var v = _stack[^1];
            _stack.RemoveAt(_stack.Count - 1);
            return v;
        }

        private Value Peek() => _stack[^1];

        private byte ReadByte() => _currentChunk.Code[_ip++];

        private int ReadU16()
        {
            int lo = _currentChunk.Code[_ip++];
            int hi = _currentChunk.Code[_ip++];
            return lo | (hi << 8);
        }
    }
}
