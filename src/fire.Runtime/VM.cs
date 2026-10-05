using System;
using System.Collections.Generic;
using System.Linq;
using fire.Ast;
using fire.Bytecode;
using fire.Standard;
using fire.Runtime;
using fire.Values;

namespace fire.Runtime
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

        /// <summary>Höhe des Operanden-Stacks beim Registrieren: ein `catch` beginnt wieder auf dieser Höhe - was die Wurfstelle darüber hinterlassen
        /// hat (angefangene Ausdrücke, die Enumeratoren eines `foreach`, Operanden tieferer Aufrufe), wird für ein mögliches `resume()` beiseitegelegt.</summary>
        public readonly int StackPointer;

        /// <summary>Während eines `catch`-Blocks bleibt nur das `finally` des `try` aktiv (der `catch` selbst fängt keine weitere Exception desselben `try`).</summary>
        public readonly bool FinallyOnly;

        public ActiveHandler(Chunk chunk, int frameDepthAtEntry, Scope targetScope, HandlerTemplate template, int stackPointer, bool finallyOnly = false)
        {
            FinallyOnly = finallyOnly;
            Chunk = chunk;
            FrameDepthAtEntry = frameDepthAtEntry;
            TargetScope = targetScope;
            Template = template;
            StackPointer = stackPointer;
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

        /// <summary>Die Operanden der Wurfstelle oberhalb von <see cref="ActiveHandler.StackPointer"/> (siehe dort), beim Fortsetzen zurückgespielt.</summary>
        public Value[] Stack = Array.Empty<Value>();

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

        /// <summary>Anzahl der aktiven Handler, als der `catch` begann (ohne das finally-only des `try`): `resume()` verwirft alles darüber.</summary>
        public readonly int HandlerCount;

        public PendingResume(SavedContinuation continuation, ActiveHandler handler, int handlerCount)
        {
            Continuation = continuation;
            Handler = handler;
            HandlerCount = handlerCount;
        }
    }

    public sealed partial class VM : IDestructRunner
    {
        // Der gerade laufende Chunk samt Array-Kopien von Code/Konstanten (siehe Chunk.CodeArray) - jede
        // Zuweisung an _currentChunk (Aufruf, Return, Exception-Sprung, ...) aktualisiert sie mit.
        private Chunk _chunk;
        private byte[] _code;
        private Value[] _constants;
        private Chunk _currentChunk
        {
            get => _chunk;
            set
            {
                _chunk = value;
                _code = value.CodeArray;
                _constants = value.ConstantsArray;
            }
        }
        private readonly Scope _globalScope;
        private readonly NativeRegistry _natives;
        private readonly ExternRegistry _externs;
        private readonly IReadOnlyDictionary<string, RuntimeClass> _classes;
        private readonly IReadOnlyDictionary<string, ExternSignature> _externSignatures;

        /// <summary>Die Sammelklassen der Basistyp-Erweiterungen (`class extends string { ... }`, SPEC
        /// 5.5.1), indiziert über `(int)ValueKind` - ein Array statt eines Namens-Lookups, weil CallMethod
        /// für JEDEN Methodenaufruf auf einem Nicht-Objekt hier nachsieht. `null` = für diese Werteart gibt
        /// es keine Erweiterung.</summary>
        private readonly RuntimeClass?[] _baseTypeClasses;

        // Der Werte-Stack: ein Array mit Stackzeiger statt einer List<Value> (kein Versionszähler, keine
        // doppelte Bereichsprüfung, kein Nullen beim Entfernen) - Push/Pop sind der heißeste Pfad der VM.
        private int _copyArgMask; // gesetzt vom Präfix CopyArgs, abgeholt vom nächsten Aufruf-Opcode (TakeCopyMask)
        private Value[] _stack = new Value[256];
        private int _sp;
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
        // bemerkt ein Signal spätestens am nächsten sicheren Punkt (siehe
        // PollSignals) und wickelt sich dann selbst sauber ab (UnwindForShutdown),
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
        public void RequestLeave()
        {
            _leaveRequested = true;
            RaiseSignal();
        }

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
            RaiseSignal();
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

        /// <summary>`leave`/`terminate` hat diese VM beendet: am Halt wird wie beim normalen Programmende der globale Scope
        /// freigegeben (das Hauptprogramm wartet vorher auf alle Fire-Threads) - anders als nach einer unbehandelten Exception.</summary>
        private bool _shutdownReleasePending;

        /// <summary>`leave`/`terminate` wurde in einer VERSCHACHTELTEN Ausführung (Destruktor, Property, Operator, Callback)
        /// aufgerufen: die VM hält dort sofort an (Halt), das geordnete Abwickeln (finally, Destruktoren) holt
        /// <see cref="FinishDeferredShutdown"/> nach, sobald die Verschachtelung zurück ist.</summary>
        private bool _shutdownDeferred;

        /// <summary>Ein Lambda, das ein nativer Aufruf auf DIESER VM verschachtelt ausführt (siehe <see cref="CallLambdaInline"/>): wo es
        /// begonnen hat - Frame-Tiefe, Scope und Stackhöhe des Aufrufers und wie viele Handler der Aufrufer schon registriert hat
        /// (ein `throw` im Callback darf die try/catch des Aufrufers nicht sehen).</summary>
        private readonly record struct CallbackBoundary(int FrameDepth, Scope Scope, int HandlerFloor, int StackPointer);
        private readonly Stack<CallbackBoundary> _callbackBoundaries = new();
        private ObjectInstance? _callbackError;

        // -----------------------------------------------------------
        // Globale Variablen und Fire-Threads (docs/THREADING_DESIGN.md Abschnitt 7, Runtime.GlobalsBroker)
        // -----------------------------------------------------------

        /// <summary>Im Hauptprogramm: gesetzt, sobald zum ersten Mal ein `fire` (oder `fire global`) läuft. Ab dann schreibt diese VM Globals
        /// unter dem Baum-Lock (Fire-Threads lesen sie gleichzeitig).</summary>
        private GlobalsBroker? _ownerBroker;

        /// <summary>In einem Fire-Thread: die Vermittlung zum Hauptprogramm. Die Globals-Slots 0 bis `_sharedCount` - 1 sind die echten
        /// Globals des Hauptprogramms (Lesen direkt, Schreiben in einer Sektion); darüber liegen die eigenen (taking/with, Top-Level-Variablen
        /// des Blocks) im privaten `_globalScope`.</summary>
        private GlobalsBroker? _threadBroker;
        private int _sharedCount;

        /// <summary>Tiefe der Sektion, die dieser Thread gerade hält (0 = keine). Verschachtelte Zugriffe (eine Methode, die `this.x = ...`
        /// schreibt, in einer schon erteilten Sektion) laufen direkt.</summary>
        private int _sectionDepth;
        private object? _sectionHandle;

        /// <summary>Hängt diese (Fire-Thread-)VM an die Globals des Hauptprogramms an (siehe FireRuntime.FireVmTaking).</summary>
        internal void AttachToGlobals(GlobalsBroker broker, int sharedGlobalCount)
        {
            _threadBroker = broker;
            _sharedCount = sharedGlobalCount;
        }

        private GlobalsBroker EnsureOwnerBroker()
        {
            if (_ownerBroker != null) return _ownerBroker;
            var broker = new GlobalsBroker(this, _globalScope);
            _ownerBroker = broker;
            ShareGlobals(broker);
            return broker;
        }

        /// <summary>Nimmt alles, was die Globals erreichen (Objekte samt Besitz, Felder, Arrays, statische Felder), in den geteilten Bereich auf.</summary>
        private void ShareGlobals(GlobalsBroker broker)
        {
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (var owned in _globalScope.OwnedObjects.ToArray())
                ShareValue(Value.MakeClassRef(owned), broker.Lock, seen);
            for (int i = 0; i < _globalScope.SlotCount; i++)
                ShareValue(_globalScope.GetSlot(i), broker.Lock, seen);
            foreach (var rc in _classes.Values)
                foreach (var staticValue in rc.StaticFieldValues.Values.ToArray())
                    ShareValue(staticValue, broker.Lock, seen);
        }

        private static void ShareValue(Value value, ThreadShareLock treeLock, HashSet<object> seen)
        {
            if (value.Kind == ValueKind.Class)
            {
                var obj = (ObjectInstance)value.AsObjectRef();
                if (!seen.Add(obj)) return;
                obj.MarkGlobalsDomain(treeLock);
                foreach (var field in obj.Fields.ToArray()) ShareValue(field.Value, treeLock, seen);
                foreach (var child in obj.OwnedObjects.ToArray()) ShareValue(Value.MakeClassRef(child), treeLock, seen);
            }
            else if (value.Kind == ValueKind.Array)
            {
                var array = value.AsArray();
                if (!seen.Add(array)) return;
                array.IsShared = true;
                foreach (var item in array.Items) ShareValue(item, treeLock, seen);
            }
        }

        /// <summary>Ein Array, das ein Fire-Thread über die Globals erreicht: ab jetzt geteilt (Elementzugriffe unter dem Lock).</summary>
        private static void MarkShared(Value value)
        {
            if (value.Kind != ValueKind.Array) return;
            var array = value.AsArray();
            if (array.IsShared) return;
            array.IsShared = true;
            foreach (var item in array.Items) MarkShared(item);
        }

        /// <summary>Meldet den Thread für eine Sektion an (oder zählt nur hoch, wenn er schon eine hält). Erst wenn das Hauptprogramm bei
        /// `sync globals` die Sektion erteilt, kehrt der Aufruf zurück.</summary>
        private void EnterGlobalsSection()
        {
            if (_sectionDepth++ == 0) _sectionHandle = _threadBroker!.EnterSection();
        }

        private void ExitGlobalsSection()
        {
            if (--_sectionDepth == 0)
            {
                var handle = _sectionHandle;
                _sectionHandle = null;
                _threadBroker!.ExitSection(handle);
            }
        }

        /// <summary>Sicherheitsnetz am Ende eines Fire-Threads: eine noch gehaltene Sektion wird freigegeben, sonst wartet das Hauptprogramm ewig.</summary>
        internal void ReleaseGlobalsSections()
        {
            if (_threadBroker == null || _sectionDepth == 0) return;
            _sectionDepth = 0;
            var handle = _sectionHandle;
            _sectionHandle = null;
            _threadBroker.ExitSection(handle);
        }

        private bool NeedsSection(ObjectInstance obj) => _threadBroker != null && _sectionDepth == 0 && obj.InGlobalsDomain;

        private Value LoadSharedGlobal(int slot)
        {
            var broker = _threadBroker!;
            Value value;
            broker.Lock.Enter();
            try { value = slot < broker.Scope.SlotCount ? broker.Scope.GetSlot(slot) : Value.MakeUndefined(); }
            finally { broker.Lock.Exit(); }
            MarkShared(value);
            return value;
        }

        private void StoreSharedGlobal(int slot, Value value)
        {
            var broker = _threadBroker!;
            EnterGlobalsSection();
            try
            {
                broker.Lock.Enter();
                try
                {
                    if (slot >= broker.Scope.SlotCount)
                        throw new InvalidOperationException("This global variable has not been declared in the main program yet.");
                    broker.Scope.SetSlot(slot, value);
                }
                finally { broker.Lock.Exit(); }
            }
            finally { ExitGlobalsSection(); }
        }

        /// <summary>Hauptprogramm, nach dem ersten `fire`: Schreiben der Globals unter dem Lock (Fire-Threads lesen gleichzeitig).</summary>
        private void StoreOwnerGlobal(int slot, Value value)
        {
            var broker = _ownerBroker!;
            broker.Lock.Enter();
            try { _globalScope.SetSlot(slot, value); }
            finally { broker.Lock.Exit(); }
        }

        private void DeclareOwnerGlobal(Value value)
        {
            var broker = _ownerBroker!;
            broker.Lock.Enter();
            try { _globalScope.DefineSlot(value); }
            finally { broker.Lock.Exit(); }
        }

        /// <summary>Ein Element eines geteilten Arrays lesen: im Fire-Thread unter dem Lock, im Hauptprogramm (einziger Schreiber) direkt.</summary>
        private bool TryGetSharedElement(ScriptArray array, long index, out Value value)
        {
            var broker = _threadBroker;
            if (broker == null) return array.TryGet(index, out value);
            broker.Lock.Enter();
            try { return array.TryGet(index, out value); }
            finally { broker.Lock.Exit(); }
        }

        /// <summary>Ein Element eines geteilten Arrays schreiben: im Fire-Thread in einer Sektion, überall unter dem Lock.</summary>
        private bool TrySetSharedElement(ScriptArray array, long index, Value value)
        {
            var broker = _threadBroker ?? _ownerBroker;
            if (broker == null) return array.TrySet(index, value);
            bool inThread = _threadBroker != null;
            if (inThread) EnterGlobalsSection();
            try
            {
                broker.Lock.Enter();
                try { return array.TrySet(index, value); }
                finally { broker.Lock.Exit(); }
            }
            finally { if (inThread) ExitGlobalsSection(); }
        }

        /// <summary>Methodenaufruf eines Fire-Threads auf ein Objekt des geteilten Bereichs: die Methode läuft, solange der Thread die Sektion
        /// hält, als Ganzes - auch ihr Lesen-Ändern-Schreiben ist damit atomar.</summary>
        private Value? CallGlobalsMethodInSection(ObjectInstance obj, string methodName, Value[] args)
        {
            EnterGlobalsSection();
            try
            {
                var rc = ResolveClass(obj.ClassName);
                if (rc.FindMethodWithAccess(methodName, args.Length).Item1 == null && TryCallOwnershipMethod(obj, methodName, args))
                    return Value.MakeUndefined();
                return CallMethodNested(obj, methodName, args);
            }
            finally { ExitGlobalsSection(); }
        }

        /// <summary>`sync globals` im Hauptprogramm: arbeitet ab, was Fire-Threads angemeldet haben (Sektionen erteilen, Aufträge ausführen) und
        /// was Host-Threads als Callback eingereiht haben. Liefert die Anzahl der Einträge.</summary>
        private int SyncGlobalsNow() => DrainInbound() + (_ownerBroker?.Drain() ?? 0);

        // ---- Callbacks von Host-Threads (z.B. ein Seriell-Ereignis): sie laufen NICHT auf dem fremden Thread, sondern werden hier eingereiht
        // und vom Hauptprogramm ausgeführt - bei `sync globals` oder (ohne `#nosync`) automatisch an einem sicheren Punkt. So sehen sie die echten
        // Globals, und es gibt keinen nebenläufigen Zugriff darauf.

        private readonly record struct InboundCallback(LambdaValue Lambda, Value[] Args, Action<string>? OnUnhandled);
        private readonly System.Collections.Concurrent.ConcurrentQueue<InboundCallback> _inbound = new();
        private volatile bool _acceptingCallbacks;

        /// <summary>Soll das Hauptprogramm die Warteschlange an sicheren Punkten selbst abarbeiten? Vorgabe ja; `#nosync` schaltet es ab.</summary>
        private bool _autoSync = true;

        /// <summary>Nimmt ein Callback eines BELIEBIGEN Threads entgegen (threadsicher) und reiht es für diese VM ein. false, wenn die VM nicht (mehr)
        /// läuft - dann hat der Aufrufer einen anderen Weg zu wählen.</summary>
        public bool PostCallback(LambdaValue lambda, Value[] args, Action<string>? onUnhandled)
        {
            if (!_acceptingCallbacks) return false;
            _inbound.Enqueue(new InboundCallback(lambda, args, onUnhandled));
            RaiseSignal();
            FireRuntime.WakeWaitingOwner();
            return true;
        }

        private int DrainInbound()
        {
            int handled = 0;
            while (_inbound.TryDequeue(out var callback))
            {
                handled++;
                try
                {
                    var error = CallLambdaInline(callback.Lambda, callback.Args);
                    if (error != null) callback.OnUnhandled?.Invoke(new UncaughtScriptException(error).Message);
                }
                catch (Exception ex)
                {
                    callback.OnUnhandled?.Invoke(ex.Message);
                }
                if (_stopExecutionRequested) break;
            }
            return handled;
        }

        /// <summary>Automatisches Abarbeiten an einem sicheren Punkt (nicht in verschachtelter Ausführung, nicht mit `#nosync`): erst Host-Callbacks, dann
        /// die Sektionen und Aufträge der Fire-Threads. true, wenn das Programm dabei beendet wurde (`leave`/`terminate` in einem Auftrag).</summary>
        private bool AutoSyncNow()
        {
            if (!_autoSync || _nestedDepth > 0) return false;
            if (!_inbound.IsEmpty) DrainInbound();
            if (!_stopExecutionRequested && _ownerBroker != null && _ownerBroker.HasPending) _ownerBroker.Drain();
            return _stopExecutionRequested;
        }

        /// <summary>Führt einen `fire global`-Auftrag auf dieser (der Besitzer-)VM aus. Eine unbehandelte Exception darin wird wie die eines
        /// Fire-Threads behandelt: sie geht an das Hauptprogramm (`catch threads`), sonst bricht es ab.</summary>
        internal void RunGlobalsJob(LambdaValue lambda, Value[] args)
        {
            var error = CallLambdaInline(lambda, args);
            if (error != null)
            {
                _pendingThreadExceptions.Enqueue(error);
                RaiseSignal();
            }
        }
        // (Nur noch in den VERSCHACHTELTEN Schleifen und im Einzelschritt geprüft - Run() liest nach StopExecution() das Halt.)

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
            _baseTypeClasses = new RuntimeClass?[Enum.GetValues<ValueKind>().Length];
            foreach (var kind in Enum.GetValues<ValueKind>())
                if (BaseTypeExtensions.ClassNameFor(kind) is { } extensionClassName
                    && _classes.TryGetValue(extensionClassName, out var extensionClass))
                    _baseTypeClasses[(int)kind] = extensionClass;
            IsMainThreadVm = isMainThreadVm;
            if (isMainThreadVm) ResetDefaultTimeout(); // ein neues Programm beginnt wieder mit der Standard-Wartezeit (`#timeout` setzt sie neu)
            IsFireThreadVm = isFireThreadVm;
            ExecutionMode = executionMode;
        }

        /// <summary>Siehe VmExecutionMode-Doku - steuert u.a., ob
        /// ArrayGet/ArraySet/Puffer-Zugriffe ihre Bounds-Prüfung überspringen
        /// (siehe die jeweiligen Opcode-Handler).</summary>
        public VmExecutionMode ExecutionMode { get; }

        // -----------------------------------------------------------
        // Shutdown-Signale: Prüfung nur an sicheren Punkten, Beenden über den Halt-Chunk
        // -----------------------------------------------------------
        //
        // Signale kommen meist von ANDEREN Threads (`terminate`, eine unbehandelte Fire-Thread-Exception für den
        // Main-Thread) - in einen laufenden Thread lässt sich keine Ausnahme "hineinwerfen", er muss sie selbst
        // bemerken. Das passiert nicht mehr vor jeder Instruktion, sondern nur an den sicheren Punkten (Schleifen-
        // Rücksprung, Aufruf, `leave`/`terminate`), und dort mit EINEM Vergleich: jedes Signal erhöht den globalen
        // Zähler `s_signalEpoch`, jede VM merkt sich den zuletzt gesehenen Stand (`_seenEpoch`) - nur bei einer
        // Abweichung läuft die eigentliche Prüfung (CheckShutdownSignals).
        //
        // Das BEENDEN ist bewusst keine C#-Ausnahme (docs/PORTING.md, "VM-interner Kontrollfluss": in einer C++-Fassung
        // ohne Exceptions gäbe es dafür keine Entsprechung), sondern reine Zustandsumschaltung wie beim Sprung in einen
        // `catch`: StopExecution() stellt Chunk/ip auf einen Chunk, der nur aus `Halt` besteht - Run() liest als Nächstes
        // dieses `Halt` und kehrt zurück, ohne dass irgendeine Instruktion ein Stop-Flag abfragen müsste.

        private static int s_signalEpoch;
        private int _seenEpoch = int.MinValue;

        /// <summary>Tiefe der verschachtelten Ausführungen (RunNestedUntil) - darin wird nicht auf Signale geprüft (wie bisher).</summary>
        private int _nestedDepth;

        /// <summary>Der Chunk, auf den StopExecution() umschaltet: nur ein `Halt`.</summary>
        private static readonly Chunk StopChunk = BuildStopChunk();

        private static Chunk BuildStopChunk()
        {
            var chunk = new Chunk();
            chunk.EmitOp(OpCode.Halt);
            return chunk;
        }

        internal static void RaiseSignal() => System.Threading.Interlocked.Increment(ref s_signalEpoch);

        /// <summary>Beendet die Ausführung dieser VM: merkt den Stopp vor und springt auf den Halt-Chunk. Aufrufer müssen danach
        /// sofort aus ihrer Instruktion zurückkehren (wie nach ThrowException).</summary>
        /// <summary>Verwirft den (bedeutungslosen) Rückgabewert einer verschachtelten Ausführung - außer die VM wurde dabei
        /// beendet (unbehandelte Exception): dann hat der Aufruf nichts zurückgegeben.</summary>
        private void PopNestedResult()
        {
            if (!_stopExecutionRequested) Pop();
        }

        private void StopExecution()
        {
            _stopExecutionRequested = true;
            _currentChunk = StopChunk;
            _ip = 0;
        }

        /// <summary>Sicherer Punkt: hat sich seit dem letzten Mal ein Signal gemeldet (ein Vergleich, sonst nichts)? Liefert true,
        /// wenn die VM dadurch beendet wurde - die aufrufende Instruktion muss dann sofort zurückkehren.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private bool PollSignals() =>
            _seenEpoch != System.Threading.Volatile.Read(ref s_signalEpoch) && PollSignalsSlow();

        /// <summary>Wie <see cref="PollSignals"/>, aber NACH einer vollständig ausgeführten Instruktion (`_ip` steht schon an der nächsten
        /// Instruktionsgrenze) - für native Aufrufe: eine native Funktion darf `leave`/`terminate` auslösen (z.B. `VM.RequestLeave`), das muss
        /// sofort danach wirken.</summary>
        private void PollSignalsAfterOp()
        {
            if (_seenEpoch == System.Threading.Volatile.Read(ref s_signalEpoch) || _nestedDepth > 0) return;
            _seenEpoch = System.Threading.Volatile.Read(ref s_signalEpoch);
            if (CheckShutdownSignals()) StopExecution();
            else AutoSyncNow(); // Host-Callbacks und Fire-Threads, die auf das Hauptprogramm warten (siehe `#nosync`)
        }

        private bool PollSignalsSlow()
        {
            // In einer verschachtelten Ausführung (Destruktor, Operator-Überladung, Property, ...) wird nicht geprüft - wie
            // bisher; das Signal bleibt stehen und gilt am nächsten sicheren Punkt außerhalb.
            if (_nestedDepth > 0) return false;
            _seenEpoch = System.Threading.Volatile.Read(ref s_signalEpoch);

            // Der Aufruf kommt aus dem Innern einer Instruktion, deren Opcode schon gelesen ist: für einen evtl. genesteten
            // Handler muss `_ip` auf die Instruktionsgrenze zeigen, damit dessen Rücksprung an der richtigen Stelle landet.
            _ip--;
            if (CheckShutdownSignals())
            {
                StopExecution();
                return true;
            }
            _ip++;
            // Host-Callbacks und Fire-Threads, die auf das Hauptprogramm warten (siehe `#nosync`): an der Stelle, die der Aufrufer gleich
            // fortsetzt, läuft die Abarbeitung verschachtelt und kehrt unverändert hierher zurück.
            return AutoSyncNow();
        }

        /// <summary>Der `leave`-/`terminate`-Aufruf der eigenen VM: sie geht SOFORT in den Halt, unabhängig davon, ob das Signal
        /// schon länger anliegt (z.B. `terminate` ist bereits von einem anderen Thread ausgelöst worden - der Aufrufer darf trotzdem
        /// keine weitere Anweisung ausführen). Der Opcode hat keine Operanden, `_ip - 1` ist also die Instruktionsgrenze.</summary>
        private void ShutdownSelfNow()
        {
            _seenEpoch = System.Threading.Volatile.Read(ref s_signalEpoch);
            if (_nestedDepth > 0)
            {
                // Mitten in einem Destruktor/einer Property/einem Operator/Callback: hier nur anhalten (die Aufrufer kennen das
                // vom Stopp durch eine unbehandelte Exception), das Abwickeln folgt in FinishDeferredShutdown.
                _shutdownDeferred = true;
                StopExecution();
                return;
            }
            _ip--;
            CheckShutdownSignals();
            StopExecution();
        }

        /// <summary>Holt das Abwickeln eines in verschachtelter Ausführung ausgelösten `leave`/`terminate` nach (siehe
        /// <see cref="_shutdownDeferred"/>). true = es wurde etwas getan, die VM steht danach wieder auf dem Halt-Chunk.</summary>
        private bool FinishDeferredShutdown()
        {
            if (!_shutdownDeferred || _nestedDepth > 0) return false;
            _shutdownDeferred = false;
            _stopExecutionRequested = false; // die verschachtelten Läufe beim Abwickeln (finally, Destruktoren) müssen wieder laufen dürfen
            _currentChunk = StopChunk;
            _ip = 0;
            CheckShutdownSignals();
            StopExecution();
            return true;
        }

        public void Run()
        {
            _currentThreadVm = this;
            _acceptingCallbacks = true;
            try { RunLoop(); }
            finally
            {
                _acceptingCallbacks = false; // Host-Callbacks nehmen danach den anderen Weg
                _currentThreadVm = null; // ein später auf diesem Thread feuernder Callback sucht keine beendete VM
                _ownerBroker?.Close();   // kein Besitzer mehr: wartende Fire-Threads werden freigegeben
            }
        }

        private void RunLoop()
        {
            _ip = 0;

            while (true)
            {
                var op = (OpCode)ReadByte();
                if (op == OpCode.Halt)
                {
                    // Ein in verschachtelter Ausführung aufgerufenes leave/terminate wird erst jetzt geordnet abgewickelt;
                    // danach steht wieder der Halt-Chunk da und das nächste Lesen landet erneut hier.
                    if (FinishDeferredShutdown()) continue;
                    // Normales Ende oder geordnetes leave/terminate (nicht der Halt-Chunk einer unbehandelten Exception).
                    if (!_stopExecutionRequested || _shutdownReleasePending) ReleaseGlobalScopeAtEnd();
                    return;
                }
                Step(op);
            }
        }

        /// <summary>Normales Programmende (oder Ende eines Threads): der globale Scope wird wie jeder andere Scope beim
        /// Verlassen freigegeben - `destruct()` läuft für alles, was ihm gehört, offene Streams werden geschlossen.
        /// Das Hauptprogramm wartet vorher auf alle noch laufenden Fire-Threads (sie können per `sync` in seine Objekte
        /// zurückschreiben). Ein Host, der den Zustand NACH dem Lauf noch braucht (Tests, Inspektion), schaltet das mit
        /// <see cref="DestroyGlobalsAtEnd"/> ab.</summary>
        private void ReleaseGlobalScopeAtEnd()
        {
            bool afterShutdown = _shutdownReleasePending;
            _shutdownReleasePending = false;
            if (!DestroyGlobalsAtEnd) return;
            if (!IsFireThreadVm) FireRuntime.WaitForAllFireThreads(_ownerBroker); // das Hauptprogramm (jede VM, die kein Fire-Thread ist); währenddessen bedient es die Warteschlange der Threads
            ReleaseGlobalScopeAfterStop(afterShutdown);
        }

        /// <summary>Gibt den globalen Scope frei; nach einem leave/terminate steht die VM schon im Stopp-Zustand, in dem
        /// Destruktoren nicht mehr laufen (siehe RunDestructor) - er wird dafür kurz aufgehoben.</summary>
        private void ReleaseGlobalScopeAfterStop(bool afterShutdown)
        {
            if (!afterShutdown) { ReleaseGlobalScope(); return; }
            _stopExecutionRequested = false;
            try { ReleaseGlobalScope(); }
            finally { _stopExecutionRequested = true; }
        }

        /// <summary>Gibt den globalen Scope frei. Bei einem Fire-Thread sind die Objekte mit `SyncOrigin` Kopien von Objekten des
        /// Hauptprogramms (Globals-Schnappschuss, `taking`) - sie bleiben unberührt, nur was der Thread selbst angelegt hat wird
        /// zerstört.</summary>
        private void ReleaseGlobalScope()
        {
            if (IsFireThreadVm) _globalScope.ReleaseWhere(this, o => o.SyncOrigin == null);
            else _globalScope.Release(this);
        }

        /// <summary>Soll das normale Programmende den globalen Scope freigeben (Vorgabe: ja)? `false` für Hosts, die die
        /// Objekte nach dem Lauf noch lesen oder weiterverwenden (z.B. Tests, die danach Threads auf ihnen arbeiten lassen).</summary>
        public bool DestroyGlobalsAtEnd { get; set; } = true;

        /// <summary>Der kooperative Prüfpunkt für `leave`/`terminate` (siehe
        /// Feld-Doku oben) - bewusst vor JEDER einzelnen Instruktion geprüft
        /// (nicht nur bei Funktionsaufrufen/Schleifen-Rücksprüngen), das ist
        /// die einfachste, garantiert korrekte Variante ("verpasst" nie ein
        /// Signal) - eine spätere Optimierung könnte das auf seltenere,
        /// dafür strukturell sinnvollere Punkte einschränken, falls der
        /// Overhead je relevant werden sollte.</summary>
        private bool CheckShutdownSignals()
        {
            // Nur der Main-Thread verarbeitet zugestellte Fire-Thread-
            // Exceptions (siehe HandleDeliveredThreadException) - läuft dabei
            // GENESTET (wie RunFinallyNested), der Main-Thread macht danach
            // ganz normal weiter, wird also NICHT gestoppt.
            if (IsMainThreadVm)
                while (_pendingThreadExceptions.TryDequeue(out var excInstance))
                {
                    if (HandleDeliveredThreadException(excInstance)) return true;
                }

            // `terminate` und `leave` enden beide wie das normale Programmende: Scopes abwickeln (finally, Destruktoren),
            // danach - am Halt, nach dem Ende aller Fire-Threads - den globalen Scope freigeben (siehe _shutdownReleasePending).
            if (_terminateRequested)
            {
                UnwindForShutdown();
                _shutdownReleasePending = true;
                if (IsMainThreadVm) RunTerminateHandlerIfAny();
                return true;
            }
            if (_leaveRequested)
            {
                _leaveRequested = false;
                UnwindForShutdown();
                _shutdownReleasePending = true;
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
        private void UnwindForShutdown(bool destroyGlobalScope = false)
        {
            while (_handlers.Count > 0)
            {
                var handler = _handlers[^1];
                _handlers.RemoveAt(_handlers.Count - 1);

                UnwindTo(handler.FrameDepthAtEntry, handler.TargetScope);
                if (handler.Template.FinallyAddr is int finallyAddr)
                    RunFinallyInlineNested(handler.Chunk, finallyAddr);
            }

            UnwindTo(0, _globalScope);
            if (destroyGlobalScope) ReleaseGlobalScope();
        }

        /// <summary>Führt einen zugestellten, unbehandelten Fire-Thread-
        /// Exception-Handler (`catch threads(...)`) genestet aus - wie
        /// RunFinallyNested: läuft, DANACH macht der Main-Thread an exakt der
        /// unterbrochenen Stelle normal weiter (kein Stoppen, anders als bei
        /// leave/terminate). Kein passender Handler registriert -> kompletter
        /// Programmabbruch, wie eine unbehandelte Exception im Main-Thread
        /// selbst (docs/THREADING_DESIGN.md 6.2).</summary>
        private bool HandleDeliveredThreadException(ObjectInstance excInstance)
        {
            var handlerProto = FindGlobalThreadsCatch(excInstance);
            if (handlerProto == null)
            {
                // Kompletter Programmabbruch - gesetzt statt geworfen, siehe
                // UnhandledException-Doku. Der Aufrufer (CheckShutdownSignals)
                // MUSS danach sofort stoppen, statt evtl. weitere in der
                // Warteschlange stehende Exceptions noch zu verarbeiten.
                UnhandledException = excInstance;
                return true;
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
            PopNestedResult(); // Rückgabewert des Handler-Protos unbenutzt, wie RunFinallyNested/RunDestructor.
            return false;
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
            PopNestedResult();
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
                    $"'process'/'try process' on an instance of '{actor.ClassName}', which is not an actor.");

            if (!mailbox.TryProcessOne(blocking, out var message))
                return false;

            var rc = ResolveClass(actor.ClassName);
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
            PopNestedResult(); // Rückgabewert unbenutzt, siehe Doku oben.
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
        
        /// <summary>Aktuelle Quell-Position (Quell-Index + Quelltextzeile) an der
                                                  /// Ausführungsposition (per Chunk-Zeilentabelle, siehe Chunk.MarkLine) -
                                                  /// (0, 0), wenn keine Information vorhanden ist (z.B. programmatisch
                                                  /// gebaute Chunks ohne Compiler-Lauf). Grundlage für die Zeilen-
                                                  /// Hervorhebung im Editor UND für die Step-/Haltepunkt-Logik unten -
                                                  /// WICHTIG: seit ein Programm aus mehreren Dateien bestehen kann (SPEC
                                                  /// "Mehrere Quelldateien") reicht der bloße Zeilenvergleich allein
                                                  /// nicht mehr aus (Zeile 5 in Datei A und Zeile 5 in Datei B sind
                                                  /// unterschiedliche Stellen) - IMMER beide Werte zusammen vergleichen.</summary>
        public (int SourceIndex, int Line) CurrentLocation => _currentChunk.GetLocation(_ip);

        /// <summary>Kurzform für `CurrentLocation.Line` - für Aufrufer, denen
        /// (noch) nur EINE Datei bekannt ist (z.B. das alte Einzeldatei-
        /// Editorfenster) und die deshalb den Quell-Index ignorieren können.</summary>
        public int CurrentLine => CurrentLocation.Line;



        /// <summary>Führt GENAU eine Instruktion aus - Gegenstück zu Run()s
        /// Schleifenkörper, nur einzeln aufrufbar mit explizitem Halt-Status
        /// statt einer Endlosschleife. Der erste Aufruf initialisiert `_ip`
        /// wie Run() das auch tut. Liefert false, sobald das Programm beendet
        /// ist (danach bleibt jeder weitere Aufruf ein No-op mit false).</summary>
        public bool StepInstruction()
        {
            if (IsHalted) return false;
            if (!_steppingStarted) BeginStepping();

            var op = (OpCode)ReadByte();
            if (op == OpCode.Halt) return FinishAtHalt();
            Step(op);
            return AfterStep();
        }

        /// <summary>Der erste Schritt: wie Run() (siehe dort) - _currentThreadVm muss auch beim schrittweisen Debuggen korrekt auf DIESE
        /// Instanz zeigen, sonst würde jede native Brücke, die sich darauf verlässt (siehe CurrentThreadVm-Doku), beim Einzelschritt-Debuggen
        /// fälschlich null sehen, obwohl eindeutig EINE VM-Instanz gerade aktiv ist.</summary>
        private void BeginStepping()
        {
            _currentThreadVm = this;
            _ip = 0;
            _steppingStarted = true;
            _acceptingCallbacks = true;
        }

        /// <summary>`Halt` gelesen: wie Run() räumt das normale Ende den globalen Scope ab - im Einzelschritt ohne auf Threads zu warten (der
        /// Debugger hält sie evtl. an). Liefert false (beendet).</summary>
        private bool FinishAtHalt()
        {
            if ((!_stopExecutionRequested || _shutdownReleasePending) && DestroyGlobalsAtEnd) ReleaseGlobalScopeAfterStop(_shutdownReleasePending);
            _shutdownReleasePending = false;
            IsHalted = true;
            _acceptingCallbacks = false;
            return false;
        }

        /// <summary>Nach jeder Instruktion im Einzelschritt: Warteschlangen abarbeiten (Fire-Threads, Host-Callbacks) und ein Programmende
        /// durch `leave`/`terminate`/unbehandelte Exception bemerken. false = beendet.</summary>
        private bool AfterStep()
        {
            // Nur wenn seit dem letzten sicheren Punkt ein Signal eingegangen ist (jedes Einreihen löst eines aus, siehe RaiseSignal):
            // die Abfrage der Warteschlangen bei JEDER Instruktion war der größte Posten der Einzelschritt-Schleife.
            if (_autoSync && _seenEpoch != System.Threading.Volatile.Read(ref s_signalEpoch) && _nestedDepth == 0 && !_stopExecutionRequested
                && (!_inbound.IsEmpty || (_ownerBroker != null && _ownerBroker.HasPending)))
                AutoSyncNow();

            // Eine unbehandelte Skript-Exception wird seit UnhandledException (siehe dort) nicht mehr geworfen, sondern nur noch GESETZT -
            // Run() bemerkt das über CheckShutdownSignals (hier bewusst NICHT aufgerufen, siehe Feld-Doku), beim schrittweisen Debuggen
            // muss das deshalb HIER explizit geprüft werden, sonst würde StepLine/StepInto/StepOut/Continue (alle bauen auf dieser
            // Methode auf) einfach immer weiterlaufen, als wäre nichts passiert, statt sauber zu stoppen.
            if (_stopExecutionRequested)
            {
                FinishDeferredShutdown();
                // leave/terminate enden wie das normale Programmende (ohne auf Threads zu warten, der Debugger hält sie evtl. an).
                if (_shutdownReleasePending && DestroyGlobalsAtEnd) ReleaseGlobalScopeAfterStop(true);
                _shutdownReleasePending = false;
                IsHalted = true;
                _acceptingCallbacks = false;
                return false;
            }

            return true;
        }

        /// <summary>Läuft bis zu einem Haltepunkt, einer Pause-Anforderung oder dem Programmende (der "Weiter"-Lauf des
        /// Debuggers). true = angehalten (am Haltepunkt oder auf Anforderung, es gibt noch etwas auszuführen), false = beendet.
        ///
        /// Schnell, weil nur an einem Zeilenwechsel nachgeschlagen wird: die Stelle gilt für den ganzen Byte-Bereich ihrer
        /// Zeile (siehe Chunk.GetLocationRange), und die Pause-Abfrage kommt nur alle 256 Instruktionen. Ein Haltepunkt
        /// zählt nur beim EINTRITT in seine Zeile, nicht bei jeder Instruktion darin - und nicht für die Zeile, auf der der Lauf beginnt.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)] // eine lange Schleife: gleich voll optimiert übersetzen
        public bool RunUntilBreakpoint(ISet<(int SourceIndex, int Line)> breakpoints, Func<bool> isPauseRequested)
        {
            if (IsHalted) return false;
            if (!_steppingStarted) BeginStepping();

            var last = CurrentLocation;
            Chunk? rangeChunk = null;
            int rangeStart = 0, rangeEnd = 0, counter = 0;

            while (true)
            {
                var op = (OpCode)ReadByte();
                if (op == OpCode.Halt) return FinishAtHalt();
                Step(op);
                if (!AfterStep()) return false;

                if ((++counter & 255) == 0 && isPauseRequested()) return true;

                if (!ReferenceEquals(_currentChunk, rangeChunk) || _ip < rangeStart || _ip >= rangeEnd)
                {
                    var location = _currentChunk.GetLocationRange(_ip, out rangeStart, out rangeEnd);
                    rangeChunk = _currentChunk;
                    if (location != last)
                    {
                        last = location;
                        if (breakpoints.Contains(location)) return true;
                    }
                }
            }
        }

        /// <summary>Läuft bis zum Programmende oder einer Pause-Anforderung ("Bis Ende durchlaufen"). true = angehalten, false = beendet.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
        public bool RunUntilEnd(Func<bool> isPauseRequested)
        {
            if (IsHalted) return false;
            if (!_steppingStarted) BeginStepping();

            int counter = 0;
            while (true)
            {
                var op = (OpCode)ReadByte();
                if (op == OpCode.Halt) return FinishAtHalt();
                Step(op);
                if (!AfterStep()) return false;
                if ((++counter & 255) == 0 && isPauseRequested()) return true;
            }
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
        public IReadOnlyList<Value> DebugStackSnapshot => new ArraySegment<Value>(_stack, 0, _sp);

        /// <summary>Aktuelle Aufruf-Tiefe (Anzahl aktiver CallFrames) - für eine
        /// einfache Anzeige "wie tief verschachtelt bin ich gerade".</summary>
        public int DebugCallDepth => _frames.Count;

        /// <summary>Kurzbeschreibung des aktuell gebundenen `this` - null
        /// (kein Text), wenn an dieser Stelle kein `this` gebunden ist (z.B.
        /// Top-Level-Code oder ein Lambda ohne `on`-Bindung).</summary>
        public string? DebugThisDescription => _currentThis switch
        {
            null => null,
            ObjectInstance oi => $"{oi.ClassName}-Instanz",
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
            _nestedDepth++;
            try { RunNestedLoop(targetFrameDepth); }
            finally { _nestedDepth--; } // auch bei einer C#-Ausnahme (z.B. im Performance-Modus ohne Prüfungen) wieder freigeben
        }

        private void RunNestedLoop(int targetFrameDepth)
        {
            while (_frames.Count >= targetFrameDepth)
            {
                var op = (OpCode)ReadByte();
                if (op == OpCode.Halt)
                    throw new InvalidOperationException(
                        "Unexpected halt in nested execution (e.g. during a destructor call).");
                Step(op);

                // Wie Run() (siehe dort für die ausführliche Begründung) -
                // eine unbehandelte Exception setzt _stopExecutionRequested
                // sofort, OHNE die Frames/den Stack selbst schon
                // vollständig abzuwickeln (das macht erst der nächste
                // CheckShutdownSignals-Aufruf) - normalerweise würde die
                // Schleifenbedingung oben (_frames.Count >= targetFrameDepth)
                // das nach dem Abwickeln von selbst auffangen, aber falls
                // der Frame-Stand GENAU auf targetFrameDepth steht, wenn das
                // passiert, würde die Schleife sonst fälschlich weiterlaufen
                // und mit demselben "Pop() auf leerem Stack"-Symptom enden
                // wie beim Bugreport, der zu diesem Fix geführt hat.
                if (_stopExecutionRequested) return;
            }
        }

        /// <summary>IDestructRunner: wird von Scope.Release/ObjectInstance.Destroy
        /// aufgerufen, wenn ein Objekt durch die Ownership-Kaskade zerstört wird.
        /// Führt den kompilierten destruct()-Body aus (falls die Klasse einen
        /// deklariert), mit 'this' = dem zu zerstörenden Objekt.</summary>
        public void RunDestructor(ObjectInstance instance)
        {
            // Die Destruktoren der GANZEN Klassenkette, abgeleitete Klasse
            // zuerst, dann jede Basisklasse (wie in C#) - eine Basisklasse, die
            // Ressourcen hält (z.B. einen Datei-Handle) räumt sie so auch für
            // abgeleitete Klassen auf, die selbst keinen destruct() haben.
            if (_stopExecutionRequested) return;
            for (var rc = instance.RtClass ?? ResolveClass(instance.ClassName); rc != null; rc = rc.Base)
            {
                if (rc.Destructor == null) continue;

                _frames.Push(new CallFrame(_currentChunk, _ip, _currentScope, _currentThis, null));
                int targetDepth = _frames.Count;

                var scope = RentCallScope(SlotSlack, 0);
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
                PopNestedResult();
                if (_stopExecutionRequested) return; // ein Destruktor hat die VM beendet (unbehandelte Exception) - nichts mehr aufrufen
            }
        }

        /// <summary>Führt EINE Instruktion aus. Die häufigsten (Laden/Speichern, Grundrechenarten, Vergleiche,
        /// Sprünge, Scopes) sind hier direkt ausgeschrieben, alles andere geht an <see cref="Execute"/>.
        /// Der Grund für die Trennung: Execute ist eine riesige Methode mit sehr vielen lokalen Variablen, und
        /// deren Stackframe wird bei JEDEM Aufruf neu genullt - das kostete pro Instruktion ein Vielfaches
        /// der eigentlichen Arbeit. Diese Methode hat fast keine Locals und bleibt billig. Jeder Fall hier
        /// verhält sich exakt wie sein Gegenstück in Execute; wo ein Fall nicht zutrifft (z.B. ein Objekt als
        /// linker Operand mit Operator-Überladung, eine Scope mit Besitz), fällt er nach Execute durch.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private void Step(OpCode op)
        {
            switch (op)
            {
                case OpCode.LoadConst:
                    Push(_constants[ReadU16()]);
                    return;

                case OpCode.Pop:
                    _sp--;
                    return;

                case OpCode.Dup:
                    Push(_stack[_sp - 1]);
                    return;

                case OpCode.LoadLocal:
                {
                    int depth = ReadU16(); int slot = ReadU16();
                    if (_sp == _stack.Length) Array.Resize(ref _stack, _stack.Length * 2);
                    _stack[_sp] = _currentScope.GetAncestor(depth).SlotRef(slot);
                    _sp++;
                    return;
                }

                case OpCode.StoreLocal:
                {
                    int depth = ReadU16(); int slot = ReadU16();
                    _currentScope.GetAncestor(depth).SlotRef(slot) = _stack[_sp - 1];
                    return;
                }

                case OpCode.LoadGlobal:
                {
                    int slot = ReadU16();
                    if (_sp == _stack.Length) Array.Resize(ref _stack, _stack.Length * 2);
                    if (slot < _sharedCount) { _stack[_sp] = LoadSharedGlobal(slot); _sp++; return; } // Fire-Thread: die echten Globals
                    _stack[_sp] = _globalScope.SlotRef(slot);
                    _sp++;
                    return;
                }

                case OpCode.StoreGlobal:
                {
                    int slot = ReadU16();
                    if (slot < _sharedCount) { StoreSharedGlobal(slot, _stack[_sp - 1]); return; }
                    if (_ownerBroker != null) { StoreOwnerGlobal(slot, _stack[_sp - 1]); return; }
                    _globalScope.SlotRef(slot) = _stack[_sp - 1];
                    return;
                }

                case OpCode.DeclareLocal:
                    if (_ownerBroker != null && ReferenceEquals(_currentScope, _globalScope)) { DeclareOwnerGlobal(Pop()); return; }
                    _currentScope.DefineSlot(Pop());
                    return;

                // Binäre Operatoren: der linke Operand liegt bei _sp-2, der rechte bei _sp-1; das Ergebnis
                // ersetzt beide. Ein Objekt links (Operator-Überladung) geht durch nach Execute.
                case OpCode.Add:
                    if (Value.TryAddInPlace(ref _stack[_sp - 2], in _stack[_sp - 1])) { _sp--; return; }
                    if (_stack[_sp - 2].Kind != ValueKind.Class && _stack[_sp - 1].Kind != ValueKind.Class) { ReplaceTwoWith(Value.Add(_stack[_sp - 2], _stack[_sp - 1])); return; }
                    break;
                case OpCode.Sub:
                    if (Value.TrySubtractInPlace(ref _stack[_sp - 2], in _stack[_sp - 1])) { _sp--; return; }
                    if (_stack[_sp - 2].Kind != ValueKind.Class) { ReplaceTwoWith(Value.Subtract(_stack[_sp - 2], _stack[_sp - 1])); return; }
                    break;
                case OpCode.Mul:
                    if (Value.TryMultiplyInPlace(ref _stack[_sp - 2], in _stack[_sp - 1])) { _sp--; return; }
                    if (_stack[_sp - 2].Kind != ValueKind.Class) { ReplaceTwoWith(Value.Multiply(_stack[_sp - 2], _stack[_sp - 1])); return; }
                    break;
                case OpCode.Div:
                    if (_stack[_sp - 2].Kind != ValueKind.Class) { ReplaceTwoWith(Value.Divide(_stack[_sp - 2], _stack[_sp - 1])); return; }
                    break;
                case OpCode.Mod:
                    if (Value.TryModuloInPlace(ref _stack[_sp - 2], in _stack[_sp - 1])) { _sp--; return; }
                    if (_stack[_sp - 2].Kind != ValueKind.Class) { ReplaceTwoWith(Value.Modulo(_stack[_sp - 2], _stack[_sp - 1])); return; }
                    break;
                case OpCode.Eq:
                    if (_stack[_sp - 2].Kind != ValueKind.Class) { ReplaceTwoWith(Value.MakeBool(Value.ValuesEqual(_stack[_sp - 2], _stack[_sp - 1]))); return; }
                    break;
                case OpCode.NotEq:
                    if (_stack[_sp - 2].Kind != ValueKind.Class) { ReplaceTwoWith(Value.MakeBool(!Value.ValuesEqual(_stack[_sp - 2], _stack[_sp - 1]))); return; }
                    break;
                case OpCode.Lt:
                    if (Value.TryCompareInPlace(ref _stack[_sp - 2], in _stack[_sp - 1], 0)) { _sp--; return; }
                    if (_stack[_sp - 2].Kind != ValueKind.Class) { ReplaceTwoWith(Value.MakeBool(Value.Compare(_stack[_sp - 2], _stack[_sp - 1]) < 0)); return; }
                    break;
                case OpCode.LtEq:
                    if (Value.TryCompareInPlace(ref _stack[_sp - 2], in _stack[_sp - 1], 1)) { _sp--; return; }
                    if (_stack[_sp - 2].Kind != ValueKind.Class) { ReplaceTwoWith(Value.MakeBool(Value.Compare(_stack[_sp - 2], _stack[_sp - 1]) <= 0)); return; }
                    break;
                case OpCode.Gt:
                    if (Value.TryCompareInPlace(ref _stack[_sp - 2], in _stack[_sp - 1], 2)) { _sp--; return; }
                    if (_stack[_sp - 2].Kind != ValueKind.Class) { ReplaceTwoWith(Value.MakeBool(Value.Compare(_stack[_sp - 2], _stack[_sp - 1]) > 0)); return; }
                    break;
                case OpCode.GtEq:
                    if (Value.TryCompareInPlace(ref _stack[_sp - 2], in _stack[_sp - 1], 3)) { _sp--; return; }
                    if (_stack[_sp - 2].Kind != ValueKind.Class) { ReplaceTwoWith(Value.MakeBool(Value.Compare(_stack[_sp - 2], _stack[_sp - 1]) >= 0)); return; }
                    break;

                case OpCode.CallMethod:
                    OpCallMethod();
                    return;

                case OpCode.CallStaticMethod:
                    OpCallStaticMethod();
                    return;

                case OpCode.Call:
                    OpCall();
                    return;

                case OpCode.Return:
                    OpReturn();
                    return;

                case OpCode.GetField:
                    OpGetField();
                    return;

                case OpCode.SetField:
                    OpSetField();
                    return;

                case OpCode.LoadThis:
                    OpLoadThis();
                    return;

                case OpCode.SetFieldOnThis:
                    OpSetFieldOnThis();
                    return;

                case OpCode.NewObject:
                    OpNewObject();
                    return;

                case OpCode.GetStaticField:
                    OpGetStaticField();
                    return;

                case OpCode.SetStaticField:
                    OpSetStaticField();
                    return;

                case OpCode.ArrayGet:
                    OpArrayGet();
                    return;

                case OpCode.ArraySet:
                    OpArraySet();
                    return;

                case OpCode.IncDecIndex:
                    OpIncDecIndex();
                    return;

                case OpCode.NewArray:
                    OpNewArray();
                    return;

                case OpCode.MakeArrayLiteral:
                    OpMakeArrayLiteral();
                    return;

                case OpCode.CallNative:
                    OpCallNative();
                    return;

                case OpCode.MakeLambda:
                    OpMakeLambda();
                    return;

                case OpCode.MakeLambdaCapturing:
                    OpMakeLambdaCapturing();
                    return;

                case OpCode.Probe:
                    OpProbe();
                    return;

                case OpCode.SilenceMember:
                    OpSilenceMember();
                    return;

                case OpCode.SilenceValue:
                    OpSilenceValue();
                    return;

                case OpCode.CallBaseMethod:
                    OpCallBaseMethod();
                    return;

                case OpCode.NewObjectOwned:
                    OpNewObjectOwned();
                    return;

                case OpCode.ConstructBase:
                    OpConstructBase();
                    return;

                case OpCode.CallProtoWithThis:
                    OpCallProtoWithThis();
                    return;

                case OpCode.Neg:
                    _stack[_sp - 1] = Value.Negate(_stack[_sp - 1]);
                    return;
                case OpCode.LogicalNot:
                    _stack[_sp - 1] = Value.LogicalNot(_stack[_sp - 1]);
                    return;

                case OpCode.Jump:
                {
                    // Ein Rücksprung (Schleife) ist ein sicherer Punkt für Shutdown-Signale (siehe PollSignals); `_ip` zeigt hier
                    // noch auf den Operanden, PollSignalsSlow rechnet mit der Instruktionsgrenze davor.
                    int target = _code[_ip] | (_code[_ip + 1] << 8);
                    if (target <= _ip && PollSignals()) return;
                    _ip = target;
                    return;
                }

                case OpCode.JumpIfFalse:
                {
                    int addr = ReadU16();
                    if (!Pop().AsBool()) _ip = addr;
                    return;
                }

                case OpCode.JumpIfFalsePeek:
                {
                    int addr = ReadU16();
                    if (!_stack[_sp - 1].AsBool()) _ip = addr;
                    return;
                }

                case OpCode.JumpIfTruePeek:
                {
                    int addr = ReadU16();
                    if (_stack[_sp - 1].AsBool()) _ip = addr;
                    return;
                }

                case OpCode.EnterScope:
                    _currentScope = RentScope(_currentScope);
                    return;

                case OpCode.ExitScope:
                {
                    var scope = _currentScope;
                    if (scope.HasOwned) { ExitScopeOwning(); return; } // Release kann Destruktoren ausführen - der ausführliche Weg
                    _currentScope = scope.Parent
                        ?? throw new InvalidOperationException("ExitScope called on the global scope.");
                    if (scope.CanRecycle) ReturnScopeToPool(scope);
                    return;
                }

                // ---- Verschmolzene Instruktionen (siehe OpCode.StoreLocalPop ff.) ----

                case OpCode.StoreLocalPop:
                {
                    int depth = ReadU16(); int slot = ReadU16();
                    _currentScope.GetAncestor(depth).SlotRef(slot) = _stack[--_sp];
                    return;
                }

                case OpCode.StoreGlobalPop:
                {
                    int slot = ReadU16();
                    var value = _stack[_sp - 1];
                    if (slot < _sharedCount) StoreSharedGlobal(slot, value);
                    else if (_ownerBroker != null) StoreOwnerGlobal(slot, value);
                    else _globalScope.SlotRef(slot) = value;
                    _sp--;
                    return;
                }

                case OpCode.JumpIfNotLt:
                case OpCode.JumpIfNotLtEq:
                case OpCode.JumpIfNotGt:
                case OpCode.JumpIfNotGtEq:
                {
                    int addr = ReadU16();
                    int kind = op - OpCode.JumpIfNotLt; // Lt, LtEq, Gt, GtEq liegen in dieser Reihenfolge hintereinander
                    bool result;
                    if (!Value.TryCompareFast(in _stack[_sp - 2], in _stack[_sp - 1], kind, out result))
                    {
                        // Schnellpfad trifft nicht zu (andere Einheit/Art, Objekt mit Operator-Überladung ...): der gewöhnliche Vergleich
                        ExecuteCompareSlow(kind switch { 0 => OpCode.Lt, 1 => OpCode.LtEq, 2 => OpCode.Gt, _ => OpCode.GtEq });
                        result = _stack[--_sp].AsBool();
                    }
                    else _sp -= 2;
                    if (!result) _ip = addr;
                    return;
                }

                case OpCode.JumpIfNotEq:
                case OpCode.JumpIfNotNotEq:
                {
                    int addr = ReadU16();
                    bool equal;
                    if (_stack[_sp - 2].Kind != ValueKind.Class)
                    {
                        equal = Value.ValuesEqual(_stack[_sp - 2], _stack[_sp - 1]);
                        _sp -= 2;
                    }
                    else
                    {
                        ExecuteCompareSlow(op == OpCode.JumpIfNotEq ? OpCode.Eq : OpCode.NotEq);
                        equal = _stack[--_sp].AsBool();
                        if (op == OpCode.JumpIfNotNotEq) equal = !equal; // das Ergebnis war `a != b`; unten wird `a == b` erwartet
                    }
                    // JumpIfNotEq springt, wenn a == b NICHT gilt; JumpIfNotNotEq, wenn a != b NICHT gilt (also a == b)
                    if (op == OpCode.JumpIfNotEq ? !equal : equal) _ip = addr;
                    return;
                }

                case OpCode.ArithLocalConstPop:
                {
                    int depth = ReadU16(); int slot = ReadU16(); int constIdx = ReadU16(); bool subtract = ReadByte() != 0;
                    ref Value variable = ref _currentScope.GetAncestor(depth).SlotRef(slot);
                    if (subtract ? Value.TrySubtractInPlace(ref variable, in _constants[constIdx]) : Value.TryAddInPlace(ref variable, in _constants[constIdx]))
                        return;
                    ArithSlow(depth, slot, constIdx, subtract);
                    return;
                }

                case OpCode.ArithGlobalConstPop:
                {
                    int slot = ReadU16(); int constIdx = ReadU16(); bool subtract = ReadByte() != 0;
                    // Fire-Threads und ein Hauptprogramm mit laufenden Threads lesen/schreiben Globals über den Broker: gewöhnlicher Weg
                    if (slot < _sharedCount || _ownerBroker != null) { ArithGlobalSlow(slot, constIdx, subtract); return; }
                    ref Value variable = ref _globalScope.SlotRef(slot);
                    if (subtract ? Value.TrySubtractInPlace(ref variable, in _constants[constIdx]) : Value.TryAddInPlace(ref variable, in _constants[constIdx]))
                        return;
                    ArithGlobalSlow(slot, constIdx, subtract);
                    return;
                }
            }

            Execute(op);
        }

        /// <summary>Langsamer Weg der verschmolzenen Vergleichssprünge: der Vergleich `op` über die zwei obersten Stack-Werte, genau wie
        /// die gewöhnliche Instruktion (auch mit Operator-Überladung); das Ergebnis liegt danach oben auf dem Stack.</summary>
        private void ExecuteCompareSlow(OpCode op) => Step(op);

        /// <summary>Langsamer Weg von `x = x + c`/`x++` auf einer Lokalen: gewöhnliches Laden, Rechnen (Strings, Einheiten, Fehler) und Speichern.</summary>
        private void ArithSlow(int depth, int slot, int constIdx, bool subtract)
        {
            Push(_currentScope.GetAncestor(depth).SlotRef(slot));
            Push(_constants[constIdx]);
            Step(subtract ? OpCode.Sub : OpCode.Add);
            _currentScope.GetAncestor(depth).SlotRef(slot) = _stack[--_sp];
        }

        /// <summary>Wie <see cref="ArithSlow"/> für eine globale Variable (auch über den Broker der Fire-Threads).</summary>
        private void ArithGlobalSlow(int slot, int constIdx, bool subtract)
        {
            if (_sp == _stack.Length) Array.Resize(ref _stack, _stack.Length * 2);
            if (slot < _sharedCount) _stack[_sp] = LoadSharedGlobal(slot); else _stack[_sp] = _globalScope.SlotRef(slot);
            _sp++;
            Push(_constants[constIdx]);
            Step(subtract ? OpCode.Sub : OpCode.Add);
            var value = _stack[_sp - 1];
            if (slot < _sharedCount) StoreSharedGlobal(slot, value);
            else if (_ownerBroker != null) StoreOwnerGlobal(slot, value);
            else _globalScope.SlotRef(slot) = value;
            _sp--;
        }

        /// <summary>Verlässt die aktuelle Scope und zerstört dabei die Objekte, die ihr gehören (Destruktoren laufen verschachtelt).</summary>
        private void ExitScopeOwning()
        {
            var scope = _currentScope;
            scope.Release(this);
            _currentScope = scope.Parent
                ?? throw new InvalidOperationException("ExitScope called on the global scope.");
            if (scope.CanRecycle) ReturnScopeToPool(scope);
        }

        /// <summary>Ersetzt die obersten ZWEI Stack-Werte durch `result` (Ergebnis einer binären Operation).</summary>
        private void ReplaceTwoWith(Value result)
        {
            _sp--;
            _stack[_sp - 1] = result;
        }

        private void OpIncDecIndex()
        {
        {
            bool isIncrement = ReadByte() != 0;
            bool isPrefix = ReadByte() != 0;
            var indexVal = Pop();
            var target = Pop();
            long idx = indexVal.AsInt();

            if (target.Kind == ValueKind.Array)
            {
                var arr = target.AsArray();
                if (arr.IsShared && _threadBroker != null && _sectionDepth == 0)
                    throw new InvalidOperationException(
                        "'++'/'--' on an array element of the globals is only possible in a fire thread inside 'sync global { ... }' (reading and writing must happen together).");
                if (!arr.TryGet(idx, out var oldVal))
                {
                    ThrowIndexOutOfBounds(idx, arr.Length);
                    return;
                }
                var newVal = isIncrement ? Value.Add(oldVal, Value.MakeInt(1)) : Value.Subtract(oldVal, Value.MakeInt(1));
                if (arr.IsShared) TrySetSharedElement(arr, idx, newVal); else arr.TrySet(idx, newVal);
                Push(isPrefix ? newVal : oldVal);
            }
            else if (target.Kind == ValueKind.Buffer)
            {
                var buf = target.AsBuffer();
                if (!buf.TryGet(idx, out byte oldByte))
                {
                    ThrowIndexOutOfBounds(idx, buf.Length);
                    return;
                }
                byte newByte = (byte)(isIncrement ? oldByte + 1 : oldByte - 1);
                buf.TrySet(idx, newByte);
                Push(Value.MakeInt(isPrefix ? newByte : oldByte, width: NumericWidth.W8));
            }
            else
            {
                throw new InvalidOperationException(
                    $"'++'/'--' on an index target expects an array or a byte buffer, not {target.Kind}.");
            }
            return;
        }
        }

        private void OpCallNative()
        {
        {
            int nativeIdx = ReadU16();
            int argCount = ReadByte();
            var args = new Value[argCount];
            for (int i = argCount - 1; i >= 0; i--) args[i] = Pop();
            if (CallNativeGuarded(nativeIdx, args, out Value nativeResult))
                Push(nativeResult);
            PollSignalsAfterOp();
            return;
        }
        }

        private void OpMakeLambda()
        {
        {
            int protoIdx = ReadU16();
            bool hasOnTarget = ReadByte() != 0;
            var proto = _currentChunk.Functions[protoIdx];
            object? onTarget = hasOnTarget ? BoxValueForOnTarget(Pop()) : null;
            var lambdaValue = new LambdaValue(proto, onTarget);
            Push(Value.MakeLambda(lambdaValue));
            return;
        }
        }

        private void OpMakeLambdaCapturing()
        {
            int protoIdx = ReadU16();
            bool hasOnTarget = ReadByte() != 0;
            int captureCount = ReadByte();
            var proto = _currentChunk.Functions[protoIdx];
            object? onTarget = hasOnTarget ? BoxValueForOnTarget(Pop()) : null;
            var captures = new Value[captureCount];
            for (int i = captureCount - 1; i >= 0; i--) captures[i] = Pop();
            Push(Value.MakeLambda(new LambdaValue(proto, onTarget, null, captures)));
        }

        // -----------------------------------------------------------
        // Inline-Caches der Aufrufstellen (siehe Bytecode.SiteCache)
        // -----------------------------------------------------------

        /// <summary>Platz für lokale Variablen, den eine Aufruf-Scope über die Parameter hinaus gleich
        /// mitbekommt (spart das Vergrößern des Slot-Arrays bei den ersten `var`s im Body).</summary>
        private const int SlotSlack = 4;

        /// <summary>Wechselt in den Aufruf von `proto`: die obersten `argCount` Stack-Werte werden direkt als
        /// Parameter-Slots der neuen Scope übernommen und (zusammen mit dem darunterliegenden Empfänger/Callee, falls
        /// `dropBelow`) vom Stack genommen. Nur für Aufrufe mit EXAKT passender Argumentanzahl (kein Standardwert nötig).</summary>
        private void EnterCall(FunctionProto proto, int argCount, bool dropBelow, object? newThis, ObjectInstance? constructed = null, int copyMask = 0, Value[]? captures = null)
        {
            int captureCount = captures?.Length ?? 0;
            int paramCount = argCount + captureCount;
            var scope = RentCallScope(paramCount + SlotSlack, paramCount);
            var slots = scope.SlotArray;
            Array.Copy(_stack, _sp - argCount, slots, 0, argCount);
            if (captureCount != 0) Array.Copy(captures!, 0, slots, argCount, captureCount); // Lambda-Captures direkt hinter den Parametern
            _sp -= argCount + (dropBelow ? 1 : 0);

            _frames.Push(new CallFrame(_currentChunk, _ip, _currentScope, _currentThis, constructed));
            _currentThis = newThis;
            _currentScope = scope;
            if (copyMask != 0) ApplyCopyMask(_currentScope, copyMask);
            _currentChunk = proto.Chunk;
            _ip = 0;
        }

        // -----------------------------------------------------------
        // `flat x` / `copy x` als Argument (SPEC 2.4, Opcode CopyArgs)
        // -----------------------------------------------------------

        /// <summary>Die Kopier-Maske, die das Präfix `CopyArgs` für den Aufruf-Opcode hinterlegt hat - hier gelesen UND
        /// gelöscht (jeder Aufruf-Opcode holt sie gleich zu Beginn ab, damit sie nie an einen späteren Aufruf gerät).</summary>
        private int TakeCopyMask()
        {
            int mask = _copyArgMask;
            _copyArgMask = 0;
            return mask;
        }

        /// <summary>Kopiert die markierten Parameter einer frisch aufgebauten Aufruf-Scope: die Kopie gehört dieser Scope
        /// (wird also mit dem Verlassen der Funktion zerstört, außer die Funktion gibt sie zurück oder übergibt sie per TakeTo).</summary>
        private void ApplyCopyMask(Scope scope, int mask)
        {
            if (mask == 0) return;
            for (int i = 0; i < 16; i++)
            {
                int bits = (mask >> (2 * i)) & 3;
                if (bits == 0) continue;
                scope.SlotRef(i) = ObjectCloner.Clone(scope.SlotRef(i), scope, deep: bits == 2);
            }
        }

        /// <summary>Für Aufrufe OHNE Funktions-Scope (eingebaute Methoden, Actor-Nachrichten): die Kopie gehört dem
        /// aktuellen Scope, wie bei einer gewöhnlichen Kopie.</summary>
        private void ApplyCopyMaskToArgs(Value[] args, int mask)
        {
            for (int i = 0; i < args.Length && i < 16; i++)
            {
                int bits = (mask >> (2 * i)) & 3;
                if (bits != 0) args[i] = ObjectCloner.Clone(args[i], _currentScope, deep: bits == 2);
            }
        }

        private void StoreSite(int site, SiteCache entry) => _chunk.EnsureSiteCaches()[site] = entry;

        private SiteCache? LookupSite(int site) => _chunk.SiteCaches?[site];

        private void OpCall()
        {
        {
            if (PollSignals()) return;
            int argCount = ReadByte();
            int copyMask = TakeCopyMask();

            // Schnellpfad: ein Lambda mit genau dieser Parameterzahl (kein Standardwert nötig).
            if (_stack[_sp - 1 - argCount] is { Kind: ValueKind.Lambda } fastCallee
                && fastCallee.AsLambda() is LambdaValue fastLambda
                && fastLambda.Proto.ParamCount == argCount)
            {
                EnterCall(fastLambda.Proto, argCount, dropBelow: true, fastLambda.OnTarget, copyMask: copyMask, captures: fastLambda.Captures);
                return;
            }

            OpCallSlow(argCount, copyMask);
            return;
        }
        }

        private void OpCallSlow(int argCount, int copyMask)
        {
        {
            var args = new Value[argCount];
            for (int i = argCount - 1; i >= 0; i--) args[i] = Pop();
            var calleeVal = Pop();

            if (calleeVal.Kind != ValueKind.Lambda)
                throw new InvalidOperationException(
                    $"Call of a value of type {calleeVal.Kind} which is not a lambda.");

            var lambda = (LambdaValue)calleeVal.AsLambda();
            CheckArity(lambda.Proto, args.Length);
            args = FillDefaultArgs(lambda.Proto, args, lambda.OnTarget);

            _frames.Push(new CallFrame(_currentChunk, _ip, _currentScope, _currentThis, null));

            var funcScope = new Scope(_globalScope);
            foreach (var a in args) funcScope.DefineSlot(a);
            if (lambda.Captures != null) foreach (var c in lambda.Captures) funcScope.DefineSlot(c);
            ApplyCopyMask(funcScope, copyMask);

            _currentThis = lambda.OnTarget;
            _currentScope = funcScope;
            _currentChunk = lambda.Proto.Chunk;
            _ip = 0;
            return;
        }
        }

        // Abschluss-Arten, die ein `finally`-Block oben auf dem Operanden-Stack vorfindet (Nutzlast, Art) - siehe OpCode.EndFinally
        private const int FinallyNormal = 0, FinallyThrow = 1, FinallyReturn = 2, FinallyJump = 3, FinallyNestedReturn = 4;

        private void OpReturn() => DoReturn(Pop());

        /// <summary>Gehört `candidate` zu den Scopes des laufenden Aufrufs (von der aktuellen Scope aufwärts bis einschließlich der
        /// Funktions-Scope, deren Parent der globale Scope ist)?</summary>
        private bool OwnsWithinCall(Scope candidate)
        {
            for (var scope = _currentScope; scope != null; scope = scope.Parent)
            {
                if (ReferenceEquals(scope, candidate)) return true;
                if (scope.Parent == null || scope.Parent.IsGlobal) return false; // die Funktions-Scope war die letzte
            }
            return false;
        }

        /// <summary>Verlässt beim `return` ALLE Scopes des Aufrufs (innerster zuerst bis zur Funktions-Scope): die Objekte, die ihnen
        /// gehören, werden zerstört. Ohne das blieben die Objekte der umgebenden Blöcke (`if`/`for`/`try` um das `return`) ewig liegen.</summary>
        private void ReleaseCallScopes()
        {
            var scope = _currentScope;
            while (true)
            {
                var parent = scope.Parent;
                scope.Release(this);
                if (scope.CanRecycle) ReturnScopeToPool(scope);
                if (parent == null || parent.IsGlobal) return;
                scope = parent;
            }
        }

        // -----------------------------------------------------------
        // Pool der Scopes (siehe Scope.CanRecycle): Blöcke, Schleifendurchläufe und Aufrufe legen sonst bei jedem Eintritt eine Scope
        // samt Slot-Array neu an. Der Pool gehört der VM (jede VM läuft auf genau einem Thread) und ist klein - tiefe Rekursion
        // erzeugt darüber hinaus einfach neue Scopes, die der GC wieder einsammelt.
        // -----------------------------------------------------------
        private const int ScopePoolMax = 256;
        private readonly Scope[] _scopePool = new Scope[ScopePoolMax];
        private int _scopePoolCount;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private Scope RentScope(Scope parent)
        {
            if (_scopePoolCount == 0) return Scope.CreatePooled(parent);
            var scope = _scopePool[--_scopePoolCount];
            scope.Reinit(parent);
            return scope;
        }

        /// <summary>Eine Scope für einen Aufruf mit Platz für `capacity` Slots, die ersten `paramCount` belegt (die Parameter kopiert der Aufrufer in <see cref="Scope.SlotArray"/>).</summary>
        private Scope RentCallScope(int capacity, int paramCount)
        {
            Scope scope;
            if (_scopePoolCount == 0) scope = Scope.CreatePooled(null);
            else scope = _scopePool[--_scopePoolCount];
            scope.ReinitForCall(_globalScope, paramCount, capacity);
            return scope;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private void ReturnScopeToPool(Scope scope)
        {
            scope.Recycle();
            if (_scopePoolCount < ScopePoolMax) _scopePool[_scopePoolCount++] = scope;
        }

        /// <summary>Beendet die aktuelle Funktion mit `retVal`. Liegt dabei noch ein `try` mit `finally` dieses Frames offen (auch ein `catch`-Block, der
        /// noch zu einem solchen `try` gehört), wird nicht zurückgekehrt, sondern erst sein `finally` ausgeführt (Abschluss "return"): dessen `EndFinally`
        /// ruft diese Methode erneut auf, bis kein `finally` mehr offen ist. Handler ohne `finally` werden einfach abgemeldet.</summary>
        private void DoReturn(Value retVal)
        {
        {
            if (_handlers.Count > 0)
            {
                int handlerFloor = _callbackBoundaries.Count > 0 ? _callbackBoundaries.Peek().HandlerFloor : 0;
                while (_handlers.Count > handlerFloor && _handlers[^1].FrameDepthAtEntry >= _frames.Count)
                {
                    var handler = _handlers[^1];
                    _handlers.RemoveAt(_handlers.Count - 1);
                    if (handler.Template.FinallyAddr is not int finallyAddr) continue;

                    // Ein zurückgegebenes Objekt, das einem der Scopes gehört, die gleich verlassen werden, geht an den Aufrufer (wie unten bei `Return`)
                    if (retVal.Kind == ValueKind.Class && _frames.Count > 0)
                    {
                        var retInstance = (ObjectInstance)retVal.AsObjectRef();
                        if (retInstance.Owner is Scope ownerScope)
                            for (var sc = _currentScope; sc != null && !ReferenceEquals(sc, handler.TargetScope); sc = sc.Parent)
                                if (ReferenceEquals(sc, ownerScope)) { retInstance.ReparentTo(_frames.Peek().ReturnScope); break; }
                    }
                    UnwindTo(handler.FrameDepthAtEntry, handler.TargetScope);
                    if (_sp > handler.StackPointer) _sp = handler.StackPointer;
                    Push(retVal);
                    Push(Value.MakeInt(FinallyReturn));
                    _currentChunk = handler.Chunk;
                    _ip = finallyAddr;
                    return;
                }
            }

            // SPEC 2.3: Wird eine Objektinstanz zurückgegeben, deren
            // Owner der gerade verlassene Scope ist, geht das Ownership
            // an den AUFRUFENDEN Scope über (nicht einfach '.Parent' -
            // Funktions-/Methoden-Scopes haben als Parent immer global,
            // das wäre hier nicht die gewünschte "eine Ebene höher").
            // Ohne das würde das zurückgegebene Objekt durch das gleich
            // folgende Release() des eigenen Scopes sofort mit zerstört.
            // Das gilt für JEDEN Scope dieses Aufrufs (innerster Block bis Funktions-Scope): ein `return` mitten in verschachtelten
            // Blöcken verlässt sie alle auf einmal.
            if (retVal.Kind == ValueKind.Class && _frames.Count > 0)
            {
                var retInstance = (ObjectInstance)retVal.AsObjectRef();
                if (retInstance.Owner is Scope retOwner && OwnsWithinCall(retOwner))
                    retInstance.ReparentTo(_frames.Peek().ReturnScope);
            }

            ReleaseCallScopes();

            var frame = _frames.Pop();
            _currentChunk = frame.ReturnChunk;
            _ip = frame.ReturnIp;
            _currentScope = frame.ReturnScope;
            _currentThis = frame.ReturnThis;

            Push(frame.ConstructedInstance != null
                ? Value.MakeClassRef(frame.ConstructedInstance)
                : retVal);
            return;
        }
        }

        private void OpNewObject()
        {
            if (PollSignals()) return;
            int site = _ip - 1;
            int classNameIdx = ReadU16();
            int argCount = ReadByte();
            int copyMask = TakeCopyMask();

            // Schnellpfad (Inline-Cache): Klasse und Konstruktor dieser Stelle sind bekannt, Zugriffs- und
            // Argumentprüfung schon bestanden.
            if (LookupSite(site) is { Class: { } cachedClass, Proto: { } cachedCtor })
            {
                var created = new ObjectInstance(cachedClass.Name, _currentScope, cachedClass);
                if (cachedClass.IsActor) created.Mailbox = new ActorMailbox();
                EnterCall(cachedCtor, argCount, dropBelow: false, newThis: created, constructed: created, copyMask: copyMask);
                return;
            }
            OpNewObjectSlow(site, classNameIdx, argCount, copyMask);
        }

        private void OpNewObjectSlow(int site, int classNameIdx, int argCount, int copyMask)
        {
        {
            var args = new Value[argCount];
            for (int i = argCount - 1; i >= 0; i--) args[i] = Pop();

            var rc = ResolveClass(_constants[classNameIdx].AsString());
            var ctorProto = rc.FindConstructor(args.Length)
                ?? throw new InvalidOperationException(DescribeConstructorNotFound(rc, args.Length));
            if (ExecutionMode != VmExecutionMode.Performance
                && !IsMemberAccessAllowed(rc, ctorProto.Access ?? AccessModifier.Public))
            {
                ThrowAccessDenied(
                    $"Constructor of '{rc.Name}' is {DescribeAccess(ctorProto.Access ?? AccessModifier.Public)} and cannot be called from here.");
                return;
            }

            if (ctorProto.ParamCount == argCount)
                StoreSite(site, new SiteCache(rc, ctorProto, 0));
            var instance = new ObjectInstance(rc.Name, _currentScope, rc);
            if (rc.IsActor) instance.Mailbox = new ActorMailbox();
            args = FillDefaultArgs(ctorProto, args, instance);
            BeginConstruction(instance, ctorProto, args, copyMask);
            return;
        }
        }

        private void OpNewObjectOwned()
        {
        {
            int classNameIdx = ReadU16();
            int argCount = ReadByte();
            int copyMask = TakeCopyMask();
            var args = new Value[argCount];
            for (int i = argCount - 1; i >= 0; i--) args[i] = Pop();
            var owner = RequireObjectInstance(Pop(), "Object creation with owner");

            var rc = ResolveClass(_constants[classNameIdx].AsString());
            var ctorProto = rc.FindConstructor(args.Length)
                ?? throw new InvalidOperationException(DescribeConstructorNotFound(rc, args.Length));
            if (ExecutionMode != VmExecutionMode.Performance
                && !IsMemberAccessAllowed(rc, ctorProto.Access ?? AccessModifier.Public))
            {
                ThrowAccessDenied(
                    $"Constructor of '{rc.Name}' is {DescribeAccess(ctorProto.Access ?? AccessModifier.Public)} and cannot be called from here.");
                return;
            }

            var instance = new ObjectInstance(rc.Name, owner, rc);
            if (rc.IsActor) instance.Mailbox = new ActorMailbox();
            args = FillDefaultArgs(ctorProto, args, instance);
            BeginConstruction(instance, ctorProto, args, copyMask);
            return;
        }
        }

        private void OpConstructBase()
        {
        {
            int classNameIdx = ReadU16();
            int argCount = ReadByte();
            int copyMask = TakeCopyMask();
            var args = new Value[argCount];
            for (int i = argCount - 1; i >= 0; i--) args[i] = Pop();

            var rc = ResolveClass(_constants[classNameIdx].AsString());
            var ctorProto = rc.FindConstructor(args.Length)
                ?? throw new InvalidOperationException(DescribeConstructorNotFound(rc, args.Length));
            args = FillDefaultArgs(ctorProto, args, _currentThis);

            // Dieselbe Instanz wird weiter konstruiert - 'this' bleibt
            // unverändert (wird trotzdem in den Frame geschrieben, damit
            // RETURN einheitlich wiederherstellen kann).
            _frames.Push(new CallFrame(_currentChunk, _ip, _currentScope, _currentThis, null));

            var baseScope = new Scope(_globalScope);
            foreach (var a in args) baseScope.DefineSlot(a);
            ApplyCopyMask(baseScope, copyMask);

            _currentScope = baseScope;
            _currentChunk = ctorProto.Chunk;
            _ip = 0;
            return;
        }
        }

        private void OpGetField()
        {
        {
            int site = _ip - 1;
            int fieldNameIdx = ReadU16();

            // Schnellpfad (Inline-Cache): Objekt derselben Klasse wie zuvor, Feld liegt an bekanntem Index.
            if (_stack[_sp - 1] is { Kind: ValueKind.Class } cachedTarget
                && LookupSite(site) is { } fieldEntry
                && cachedTarget.AsObjectRef() is ObjectInstance cachedObj
                && ReferenceEquals(cachedObj.RtClass, fieldEntry.Class)
                && cachedObj.ThreadLock == null)
            {
                _stack[_sp - 1] = cachedObj.Fields.GetAt(fieldEntry.FieldIndex);
                return;
            }

            OpGetFieldSlow(site, fieldNameIdx);
            return;
        }
        }

        private void OpGetFieldSlow(int site, int fieldNameIdx) => GetFieldSlowCore(_constants[fieldNameIdx].AsString(), site);

        /// <summary>Feldzugriff `target.fieldName` (Wert OBEN auf dem Stack) samt Zugriffsprüfung und Property-Getter - der Langsam-Pfad von
        /// GetField, auch für die Reflection (site &lt; 0: kein Inline-Cache). true, wenn das Ergebnis auf dem Stack liegt; false, wenn
        /// stattdessen eine Exception in einen Handler umgeleitet wurde.</summary>
        private bool GetFieldSlowCore(string fieldName, int site)
        {
        {
            var target = Pop();

            // `Length` ist die Schreibweise der Eigenschaften (wie bei `string`),
            // `length` die ältere - beide bei Array/Puffer/String gleichwertig.
            if (target.Kind == ValueKind.String)
            {
                if (fieldName is "Length" or "length")
                {
                    Push(Value.MakeInt(target.AsString().Length));
                    return true;
                }
                throw new InvalidOperationException($"Strings have no field '{fieldName}' (only 'Length').");
            }

            if (target.Kind == ValueKind.Array)
            {
                if (fieldName is "Length" or "length")
                {
                    Push(Value.MakeInt(target.AsArray().Length));
                    return true;
                }
                throw new InvalidOperationException($"Arrays have no field '{fieldName}' (only 'Length').");
            }

            if (target.Kind == ValueKind.Buffer)
            {
                var buf = target.AsBuffer();
                if (fieldName is "Length" or "length")
                {
                    Push(Value.MakeInt(buf.Length));
                    return true;
                }
                if (fieldName == "littleEndian")
                {
                    Push(Value.MakeBool(buf.Order == ByteOrder.Little));
                    return true;
                }
                throw new InvalidOperationException(
                    $"Byte buffers have no field '{fieldName}' (only 'Length', 'littleEndian').");
            }

            var obj = RequireObjectInstance(target, "Feldzugriff");
            if (obj.TryGetFieldLocked(fieldName, out var val))
            {
                if (_threadBroker != null && obj.InGlobalsDomain) MarkShared(val); // ein Array des geteilten Bereichs
                if (ExecutionMode != VmExecutionMode.Performance && obj.RtClass != null)
                {
                    var fieldAccess = obj.RtClass.FindFieldAccess(fieldName);
                    if (fieldAccess is (var declaringRcGet, var accessGet) && !IsMemberAccessAllowed(declaringRcGet, accessGet))
                    {
                        ThrowAccessDenied(
                            $"Field '{fieldName}' of '{declaringRcGet.Name}' is {DescribeAccess(accessGet)} " +
                            "and cannot be accessed from here.");
                        return false;
                    }
                }
                if (site >= 0 && obj.RtClass != null && obj.ThreadLock == null && obj.RtClass.FieldIndex.TryGetValue(fieldName, out int getIndex))
                    StoreSite(site, new SiteCache(obj.RtClass, null, getIndex));
                Push(val);
                return true;
            }

            // Kein Feld dieses Namens - Property-Getter versuchen
            // (Namenskonvention 'get_'+Name, siehe Ast.PropertyDecl).
            // Properties haben absichtlich NIE einen eigenen Fields-
            // Eintrag, landen also immer hier.
            var rcGet = ResolveClass(obj.ClassName);
            if (rcGet.FindMethod("get_" + fieldName, 0) != null)
            {
                var result = CallMethodNested(obj, "get_" + fieldName, Array.Empty<Value>());
                if (result != null) Push(result.Value);
                return result != null;
            }

            throw new InvalidOperationException(
                $"Field '{fieldName}' does not exist on an instance of '{obj.ClassName}' " +
                $"(and there is no 'get_{fieldName}' property either).");
        }
        }

        private void OpSetField()
        {
        {
            int site = _ip - 1;
            int fieldNameIdx = ReadU16();

            // Schnellpfad (Inline-Cache, siehe OpGetField): Objekt derselben Klasse wie zuvor.
            if (_stack[_sp - 2] is { Kind: ValueKind.Class } cachedTarget
                && LookupSite(site) is { } fieldEntry
                && cachedTarget.AsObjectRef() is ObjectInstance cachedObj
                && ReferenceEquals(cachedObj.RtClass, fieldEntry.Class)
                && cachedObj.AccessGuard == null)
            {
                var assigned = _stack[_sp - 1];
                cachedObj.Fields.SetAt(fieldEntry.FieldIndex, assigned);
                _sp--;
                _stack[_sp - 1] = assigned;
                return;
            }

            OpSetFieldSlow(site, fieldNameIdx);
            return;
        }
        }

        private void OpSetFieldSlow(int site, int fieldNameIdx) => SetFieldSlow(_constants[fieldNameIdx].AsString(), site);

        /// <summary>`obj.fieldName = value` (Stack: obj, value) - Langsam-Pfad von SetField, auch für die Reflection (site &lt; 0: kein
        /// Inline-Cache). true, wenn der zugewiesene Wert auf dem Stack liegt; false bei einer in einen Handler umgeleiteten Exception.</summary>
        private bool SetFieldSlow(string fieldName, int site)
        {
            // Hat das Objekt Proben auf dieses Mitglied: `changing`-Handler, Schreiben, `changed`-Handler (siehe SetFieldProbed)
            if (_stack[_sp - 2] is { Kind: ValueKind.Class } probeTarget
                && ((ObjectInstance)probeTarget.AsObjectRef()).Probes is { } probes && probes.Affects(fieldName))
                return SetFieldProbed(fieldName, probes);
            return SetFieldSlowSections(fieldName, site);
        }

        private bool SetFieldSlowSections(string fieldName, int site)
        {
            // Ein Fire-Thread ändert ein Objekt des geteilten Bereichs nur in einer Sektion (siehe GlobalsBroker).
            if (_threadBroker != null && _sectionDepth == 0
                && _stack[_sp - 2] is { Kind: ValueKind.Class } sectionTarget
                && ((ObjectInstance)sectionTarget.AsObjectRef()).InGlobalsDomain)
            {
                EnterGlobalsSection();
                try { return SetFieldSlowCore(fieldName, site); }
                finally { ExitGlobalsSection(); }
            }
            return SetFieldSlowCore(fieldName, site);
        }

        private bool SetFieldSlowCore(string fieldName, int site)
        {
        {
            var value = Pop();
            var obj = RequireObjectInstance(Pop(), "Feldzuweisung");

            if (obj.HasFieldLocked(fieldName))
            {
                bool hasUnitRule = false;
                if (ExecutionMode != VmExecutionMode.Performance && obj.RtClass != null)
                {
                    var fieldAccess = obj.RtClass.FindFieldAccess(fieldName);
                    if (fieldAccess is (var declaringRcSet, var accessSet) && !IsMemberAccessAllowed(declaringRcSet, accessSet))
                    {
                        ThrowAccessDenied(
                            $"Field '{fieldName}' of '{declaringRcSet.Name}' is {DescribeAccess(accessSet)} " +
                            "and cannot be accessed from here.");
                        return false;
                    }

                    // SPEC "Einheiten-Deklarationen" - Feldzugriff ist
                    // grundsätzlich dynamisch (die tatsächliche Klasse
                    // steht erst hier, zur Laufzeit, fest), deshalb
                    // anders als bei lokalen/globalen Variablen KEINE
                    // Compile-Zeit-Prüfung möglich (siehe Compiler.
                    // CompileClassBody-Kommentar) - die Prüfung selbst
                    // ist aber inhaltlich identisch zu OpCode.CheckUnit.
                    string? requiredUnitName = obj.RtClass.FindFieldRequiredUnit(fieldName);
                    if (requiredUnitName != null)
                    {
                        hasUnitRule = true; // jede Zuweisung muss die Einheit prüfen - nicht cachen
                        var requiredUnit = Values.Unit.Parse(requiredUnitName);
                        var actualUnit = value.Unit ?? Values.Unit.Unitless;
                        if (!actualUnit.Equals(requiredUnit))
                        {
                            ThrowUnitMismatch(requiredUnitName, actualUnit);
                            return false;
                        }
                    }
                }
                if (site >= 0 && !hasUnitRule && obj.RtClass != null && obj.AccessGuard == null
                    && obj.RtClass.FieldIndex.TryGetValue(fieldName, out int setIndex))
                    StoreSite(site, new SiteCache(obj.RtClass, null, setIndex));
                obj.SetFieldLocked(fieldName, value);
                Push(value);
                return true;
            }

            // Kein existierendes Feld dieses Namens - Property-Setter
            // versuchen (Namenskonvention 'set_'+Name).
            var rcSet = ResolveClass(obj.ClassName);
            if (rcSet.FindMethod("set_" + fieldName, 1) != null)
            {
                var result = CallMethodNested(obj, "set_" + fieldName, new[] { value });
                // Rückgabewert des Setters selbst unbenutzt - eine
                // Zuweisung wertet immer zum ZUGEWIESENEN Wert aus,
                // nicht zu dem, was der Setter zurückgibt. null ==
                // per Exception umgeleitet (siehe CallMethodNested-
                // Doku) - dann NICHT pushen.
                if (result != null) Push(value);
                return result != null;
            }

            // Eine gleichnamige Property MIT Getter, aber OHNE Setter,
            // existiert - das ist ein Fehler, KEIN "neues Feld anlegen"
            // (sonst würde die Property ab hier unbemerkt durch ein
            // gleichnamiges Feld überschattet, auch für künftige
            // Lesezugriffe über GetField, das Felder vor Properties
            // prüft).
            if (rcSet.FindMethod("get_" + fieldName, 0) != null)
                throw new InvalidOperationException(
                    $"Property '{fieldName}' on '{obj.ClassName}' has no setter (only 'get').");

            // Weder existierendes Feld noch Property - wie bisher:
            // neues Feld einfach anlegen (dynamische Sprache, keine
            // Vorab-Deklarationspflicht für Felder).
            obj.SetFieldLocked(fieldName, value);
            Push(value);
            return true;
        }
        }

        private void OpLoadThis()
        {
            Push(_currentThis switch
            {
                null => throw new InvalidOperationException("'this' is not bound at this point."),
                ObjectInstance oi => Value.MakeClassRef(oi),
                Value v => v,
                _ => throw new InvalidOperationException("Unexpected 'this' value."),
            });
            return;
        }

        private void OpSetFieldOnThis()
        {
            int site = _ip - 1;
            int fieldNameIdx = ReadU16();

            // Schnellpfad (Inline-Cache, siehe OpSetField): `this` hat dieselbe Klasse wie zuvor.
            if (_currentThis is ObjectInstance fastThis
                && LookupSite(site) is { } thisEntry
                && ReferenceEquals(fastThis.RtClass, thisEntry.Class)
                && fastThis.ThreadLock == null)
            {
                fastThis.Fields.SetAt(thisEntry.FieldIndex, _stack[--_sp]);
                return;
            }
            OpSetFieldOnThisSlow(site, fieldNameIdx);
        }

        private void OpSetFieldOnThisSlow(int site, int fieldNameIdx)
        {
        {
            string fieldName = _constants[fieldNameIdx].AsString();
            var value = Pop();
            if (_currentThis is not ObjectInstance oi)
                throw new InvalidOperationException("SetFieldOnThis without a bound ObjectInstance as 'this'.");

            // SPEC "Einheiten-Deklarationen" - dieselbe Prüfung wie in
            // SetField (siehe dort für die Begründung, warum das zur
            // Laufzeit statt zur Compile-Zeit passiert). Dieser Opcode
            // wird für die Feld-INITIALISIERER selbst benutzt (siehe
            // Compiler.CompileConstructorProto) - `int x : mm = 5`
            // würde ohne diese Prüfung hier den ersten, deklarierten
            // Wert komplett ungeprüft durchlassen.
            bool hasUnitRule = false;
            if (ExecutionMode != VmExecutionMode.Performance && oi.RtClass != null)
            {
                string? requiredUnitName = oi.RtClass.FindFieldRequiredUnit(fieldName);
                if (requiredUnitName != null)
                {
                    hasUnitRule = true; // jede Zuweisung muss die Einheit prüfen - nicht cachen
                    var requiredUnit = Values.Unit.Parse(requiredUnitName);
                    var actualUnit = value.Unit ?? Values.Unit.Unitless;
                    if (!actualUnit.Equals(requiredUnit))
                    {
                        ThrowUnitMismatch(requiredUnitName, actualUnit);
                        return;
                    }
                }
            }

            if (!hasUnitRule && oi.RtClass != null && oi.ThreadLock == null
                && oi.RtClass.FieldIndex.TryGetValue(fieldName, out int thisIndex))
                StoreSite(site, new SiteCache(oi.RtClass, null, thisIndex));
            oi.SetFieldLocked(fieldName, value);
            return;
        }
        }

        private void OpCallMethod()
        {
        {
            if (PollSignals()) return;
            int site = _ip - 1;
            int methodNameIdx = ReadU16();
            int argCount = ReadByte();
            int copyMask = TakeCopyMask();

            // Schnellpfad (Inline-Cache, siehe SiteCache): ein Objekt derselben Klasse wie beim letzten Aufruf
            // dieser Stelle - Methode, Zugriffs- und Argumentprüfung sind schon erledigt.
            if (_stack[_sp - 1 - argCount] is { Kind: ValueKind.Class } cachedTarget
                && LookupSite(site) is { Proto: { } cachedMethod } siteEntry
                && cachedTarget.AsObjectRef() is ObjectInstance cachedObj
                && ReferenceEquals(cachedObj.RtClass, siteEntry.Class)
                && cachedObj.Mailbox == null
                && (_threadBroker == null || _sectionDepth > 0 || !cachedObj.InGlobalsDomain))
            {
                // Methode, die nur eine native Funktion mit `this.feld` und ihren Parametern aufruft (alle Methoden der
                // Brücken-Preludes): direkt die native Funktion aufrufen, ohne Scope/Frame (siehe NativeForwarder).
                if (siteEntry.Forwarder is { } forwarder && copyMask == 0 && cachedObj.ThreadLock == null)
                {
                    var nativeArgs = new Value[argCount + 1];
                    nativeArgs[0] = cachedObj.Fields.GetAt(siteEntry.ForwarderFieldIndex);
                    Array.Copy(_stack, _sp - argCount, nativeArgs, 1, argCount);
                    _sp -= argCount + 1; // Argumente und Empfänger
                    if (CallNativeGuarded(forwarder.NativeIndex, nativeArgs, out Value forwarded))
                        Push(forwarder.ReturnsResult ? forwarded : Value.MakeUndefined());
                    PollSignalsAfterOp();
                    return;
                }

                EnterCall(cachedMethod, argCount, dropBelow: true, cachedObj, copyMask: copyMask);
                return;
            }

            OpCallMethodSlow(site, methodNameIdx, argCount, copyMask);
            return;
        }
        }

        private void OpCallMethodSlow(int site, int methodNameIdx, int argCount, int copyMask)
        {
        {
            string methodName = _constants[methodNameIdx].AsString();
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
                // `foreach (x in array)` / `foreach (b in buffer)`: ein Array/Puffer ist
                // keine Objektinstanz mit eigenem GetEnumerator() - hier ein
                // ListEnumerator der Prelude darüber (dieselbe Klasse, die `List`
                // benutzt; sie liest nur `items[index]`/`count`). Ohne Prelude (reine
                // Kernprogramme) bleibt es beim Fehler unten.
                if (methodName == "GetEnumerator" && args.Length == 0
                    && target.Kind is ValueKind.Array or ValueKind.Buffer
                    && _classes.TryGetValue("ListEnumerator", out var enumeratorClass))
                {
                    long itemCount = target.Kind == ValueKind.Array ? target.AsArray().Length : target.AsBuffer().Length;
                    var enumerator = ConstructNested(enumeratorClass, new[] { target, Value.MakeInt(itemCount) });
                    Push(Value.MakeClassRef(enumerator));
                    return;
                }

                // Methoden aus einer Basistyp-Erweiterung (`class extends string { ... }`,
                // SPEC 5.5.1 - z.B. IndexOf/Substring im Prelude): wie ein Objekt-Aufruf, nur
                // ist `this` der Wert selbst. Vor den fest eingebauten Konvertierungen unten.
                if (_baseTypeClasses[(int)target.Kind] is { } extensionRc)
                {
                    var (extProto, extDeclaringRc, extAccess) = extensionRc.FindMethodWithAccess(methodName, args.Length);
                    if (extProto != null)
                    {
                        if (ExecutionMode != VmExecutionMode.Performance && !IsMemberAccessAllowed(extDeclaringRc!, extAccess))
                        {
                            ThrowAccessDenied(
                                $"Method '{methodName}' of the extension of '{extensionRc.Name.Substring(1)}' is " +
                                $"{DescribeAccess(extAccess)} and cannot be called from here.");
                            return;
                        }
                        CheckArity(extProto, args.Length);
                        args = FillDefaultArgs(extProto, args, target);

                        _frames.Push(new CallFrame(_currentChunk, _ip, _currentScope, _currentThis, null));
                        var extScope = new Scope(_globalScope);
                        foreach (var a in args) extScope.DefineSlot(a);
                        ApplyCopyMask(extScope, copyMask);

                        _currentThis = target;
                        _currentScope = extScope;
                        _currentChunk = extProto.Chunk;
                        _ip = 0;
                        return;
                    }
                }

                if (copyMask != 0) ApplyCopyMaskToArgs(args, copyMask);
                if (TryCallBuiltinMethod(target, methodName, args, out Value builtinResult))
                {
                    Push(builtinResult);
                    return;
                }
                throw new InvalidOperationException(
                    $"'{methodName}' ({args.Length} argument(s)) is not a known built-in method " +
                    $"on a value of type {target.Kind}.");
            }

            var obj = (ObjectInstance)target.AsObjectRef();

            // Actor-Ziel (siehe Runtime.ObjectInstance.Mailbox-Doku):
            // JEDER Methodenaufruf wird zu einer asynchronen Nachricht
            // statt eines direkten Aufrufs, unabhängig vom rufenden
            // Thread - dieser Aufruf selbst liefert 'undefined' und
            // läuft normal weiter (kein Sprung in irgendeinen Chunk).
            if (obj.Mailbox != null)
            {
                if (copyMask != 0) ApplyCopyMaskToArgs(args, copyMask);
                obj.Mailbox.Enqueue(new ActorMessage(methodName, args));
                Push(Value.MakeUndefined());
                return;
            }

            // Fire-Thread ruft eine Methode eines Objekts des geteilten Bereichs: sie läuft als Ganzes in einer Sektion (atomar).
            if (NeedsSection(obj))
            {
                if (copyMask != 0) ApplyCopyMaskToArgs(args, copyMask);
                var sectionResult = CallGlobalsMethodInSection(obj, methodName, args);
                if (sectionResult != null) Push(sectionResult.Value); // null: eine Exception hat den Ablauf umgeleitet
                return;
            }

            var rc = ResolveClass(obj.ClassName);
            var (proto, declaringRcCall, accessCall) = rc.FindMethodWithAccess(methodName, args.Length);
            if (proto == null)
            {
                // Die Ownership-Übergabe (SPEC 2.2) ist für jedes Objekt da, ohne dass die Klasse sie deklariert.
                if (TryCallOwnershipMethod(obj, methodName, args))
                {
                    Push(Value.MakeUndefined());
                    return;
                }
                throw new InvalidOperationException(DescribeMethodNotFound(rc, methodName, args.Length));
            }
            if (ExecutionMode != VmExecutionMode.Performance && !IsMemberAccessAllowed(declaringRcCall!, accessCall))
            {
                ThrowAccessDenied(
                    $"Method '{methodName}' of '{declaringRcCall!.Name}' is {DescribeAccess(accessCall)} " +
                    "and cannot be called from here.");
                return;
            }
            CheckArity(proto, args.Length);
            if (proto.ParamCount == argCount && obj.RtClass != null)
            {
                var forwarder = proto.Forwarder;
                int forwarderField = -1;
                if (forwarder != null && !obj.RtClass.FieldIndex.TryGetValue(forwarder.FieldName, out forwarderField))
                    forwarder = null;
                StoreSite(site, new SiteCache(obj.RtClass, proto, 0, forwarder, forwarderField));
            }
            args = FillDefaultArgs(proto, args, obj);

            _frames.Push(new CallFrame(_currentChunk, _ip, _currentScope, _currentThis, null));
            var scope = new Scope(_globalScope);
            foreach (var a in args) scope.DefineSlot(a);
            ApplyCopyMask(scope, copyMask);

            _currentThis = obj;
            _currentScope = scope;
            _currentChunk = proto.Chunk;
            _ip = 0;
            return;
        }
        }

        /// <summary>`obj.TakeUpwards()`, `obj.TakeGlobal()`, `obj.TakeTo(other)` (SPEC 2.2): eingebaute Methoden jedes Objekts, die
        /// nur greifen, wenn die Klasse nichts Gleichnamiges deklariert. Liefert false, wenn `name`/Argumentzahl keine davon ist.</summary>
        private bool TryCallOwnershipMethod(ObjectInstance obj, string name, Value[] args)
        {
            switch (name)
            {
                case "TakeUpwards" when args.Length == 0:
                    obj.TakeUpwards();
                    return true;
                case "TakeGlobal" when args.Length == 0:
                    obj.TakeGlobal(_globalScope);
                    return true;
                case "TakeTo" when args.Length == 1:
                    obj.TakeTo(RequireObjectInstance(args[0], "TakeTo"), this);
                    return true;
                default:
                    return false;
            }
        }

        private void OpCallBaseMethod()
        {
        {
            string baseClassName = _constants[ReadU16()].AsString();
            string methodName = _constants[ReadU16()].AsString();
            int argCount = ReadByte();
            int copyMask = TakeCopyMask();
            var args = new Value[argCount];
            for (int i = argCount - 1; i >= 0; i--) args[i] = Pop();

            var rc = ResolveClass(baseClassName);
            var (proto, declaringRcBase, accessBase) = rc.FindMethodWithAccess(methodName, args.Length);
            if (proto == null)
                throw new InvalidOperationException(DescribeMethodNotFound(rc, methodName, args.Length));
            if (ExecutionMode != VmExecutionMode.Performance && !IsMemberAccessAllowed(declaringRcBase!, accessBase))
            {
                ThrowAccessDenied(
                    $"Method '{methodName}' of '{declaringRcBase!.Name}' is {DescribeAccess(accessBase)} " +
                    "and cannot be called from here.");
                return;
            }
            CheckArity(proto, args.Length);
            args = FillDefaultArgs(proto, args, _currentThis);

            // 'this' bleibt dasselbe Objekt (nicht-virtueller Aufruf).
            _frames.Push(new CallFrame(_currentChunk, _ip, _currentScope, _currentThis, null));
            var scope = new Scope(_globalScope);
            foreach (var a in args) scope.DefineSlot(a);
            ApplyCopyMask(scope, copyMask);

            _currentScope = scope;
            _currentChunk = proto.Chunk;
            _ip = 0;
            return;
        }
        }

        private void OpGetStaticField()
        {
        {
            // SPEC "Statische Mitglieder" - kein Objekt auf dem Stack
            // (der Klassenname steht schon als Konstante im Bytecode,
            // siehe Resolver.TryResolveStaticMemberAccess/Compiler),
            // die eigentliche Speicherstelle liegt direkt auf der
            // RuntimeClass (siehe FindStaticFieldOwner - teilt sich
            // ggf. mit einer Basisklasse dieselbe Speicherstelle).
            string className = _constants[ReadU16()].AsString();
            string fieldName = _constants[ReadU16()].AsString();
            var staticRc = ResolveClass(className);
            var owner = staticRc.FindStaticFieldOwner(fieldName);

            if (owner == null)
            {
                // Kein statisches Feld dieses Namens - Property-
                // Getter versuchen (Namenskonvention 'get_'+Name,
                // genau wie bei GetField), diesmal als STATISCHER
                // Aufruf (keine Instanz).
                if (staticRc.FindMethod("get_" + fieldName, 0) is { IsStatic: true })
                {
                    var result = CallStaticMethodNested(staticRc, "get_" + fieldName, Array.Empty<Value>());
                    if (result != null) Push(result.Value);
                    return;
                }
                throw new InvalidOperationException(
                    $"'{className}' has no static field '{fieldName}' (and no static " +
                    $"'get_{fieldName}' property).");
            }

            if (ExecutionMode != VmExecutionMode.Performance)
            {
                var fieldAccess = owner.FindFieldAccess(fieldName);
                if (fieldAccess is (var declaringRc, var access) && !IsMemberAccessAllowed(declaringRc, access))
                {
                    ThrowAccessDenied(
                        $"Static field '{fieldName}' of '{declaringRc.Name}' is {DescribeAccess(access)} " +
                        "and cannot be accessed from here.");
                    return;
                }
            }

            Value staticVal;
            var staticBroker = _threadBroker;
            if (staticBroker == null)
            {
                staticVal = owner.StaticFieldValues.TryGetValue(fieldName, out var plain) ? plain : Value.MakeUndefined();
            }
            else
            {
                // Fire-Thread: statische Felder sind Teil des geteilten Bereichs - Lesen unter dem Lock
                staticBroker.Lock.Enter();
                try { staticVal = owner.StaticFieldValues.TryGetValue(fieldName, out var shared) ? shared : Value.MakeUndefined(); }
                finally { staticBroker.Lock.Exit(); }
                MarkShared(staticVal);
            }
            Push(staticVal);
            return;
        }
        }

        private void OpSetStaticField()
        {
        {
            string setClassName = _constants[ReadU16()].AsString();
            string setFieldName = _constants[ReadU16()].AsString();
            var setValue = Pop();
            var setRc = ResolveClass(setClassName);
            var setOwner = setRc.FindStaticFieldOwner(setFieldName);

            if (setOwner == null)
            {
                // Kein statisches Feld dieses Namens - statischen
                // Property-Setter versuchen (Namenskonvention
                // 'set_'+Name, Gegenstück zum Getter-Fallback in
                // GetStaticField, siehe auch SetField).
                if (setRc.FindMethod("set_" + setFieldName, 1) is { IsStatic: true })
                {
                    var setterResult = CallStaticMethodNested(setRc, "set_" + setFieldName, new[] { setValue });
                    // Eine Zuweisung wertet zum ZUGEWIESENEN Wert aus,
                    // nicht zum Rückgabewert des Setters. null == per
                    // Exception umgeleitet - dann NICHT pushen.
                    if (setterResult != null) Push(setValue);
                    return;
                }
                throw new InvalidOperationException(
                    $"'{setClassName}' has no static field '{setFieldName}' (and no static " +
                    $"'set_{setFieldName}' property).");
            }

            if (ExecutionMode != VmExecutionMode.Performance)
            {
                var fieldAccess = setOwner.FindFieldAccess(setFieldName);
                if (fieldAccess is (var declaringRc, var access) && !IsMemberAccessAllowed(declaringRc, access))
                {
                    ThrowAccessDenied(
                        $"Static field '{setFieldName}' of '{declaringRc.Name}' is {DescribeAccess(access)} " +
                        "and cannot be accessed from here.");
                    return;
                }

                // SPEC "Einheiten-Deklarationen" - inhaltlich identisch
                // zu SetField, siehe dort.
                string? requiredUnitName = setOwner.FindFieldRequiredUnit(setFieldName);
                if (requiredUnitName != null)
                {
                    var requiredUnit = Values.Unit.Parse(requiredUnitName);
                    var actualUnit = setValue.Unit ?? Values.Unit.Unitless;
                    if (!actualUnit.Equals(requiredUnit))
                    {
                        ThrowUnitMismatch(requiredUnitName, actualUnit);
                        return;
                    }
                }
            }

            var staticWriteBroker = _threadBroker ?? _ownerBroker;
            if (staticWriteBroker == null)
            {
                setOwner.StaticFieldValues[setFieldName] = setValue;
            }
            else
            {
                // Statische Felder gehören zum geteilten Bereich: ein Fire-Thread schreibt in einer Sektion, überall unter dem Lock
                bool staticInThread = _threadBroker != null;
                if (staticInThread) EnterGlobalsSection();
                try
                {
                    staticWriteBroker.Lock.Enter();
                    try { setOwner.StaticFieldValues[setFieldName] = setValue; }
                    finally { staticWriteBroker.Lock.Exit(); }
                }
                finally { if (staticInThread) ExitGlobalsSection(); }
            }
            Push(setValue);
            return;
        }
        }

        private void OpCallStaticMethod()
        {
        {
            if (PollSignals()) return;
            int site = _ip - 1;
            int classNameIdx = ReadU16();
            int methodNameIdx = ReadU16();
            int callArgCount = ReadByte();
            int copyMask = TakeCopyMask();

            // Schnellpfad: dieselbe Stelle hat sich schon einmal aufgelöst (Klasse/Methode stehen als Konstanten
            // im Bytecode fest, siehe SiteCache) - kein Lookup nach Klassen- und Methodenname mehr.
            if (LookupSite(site) is { Proto: { } cachedStatic })
            {
                EnterCall(cachedStatic, callArgCount, dropBelow: false, newThis: null, copyMask: copyMask);
                return;
            }

            OpCallStaticMethodSlow(site, classNameIdx, methodNameIdx, callArgCount, copyMask);
            return;
        }
        }

        private void OpCallStaticMethodSlow(int site, int classNameIdx, int methodNameIdx, int callArgCount, int copyMask)
        {
        {
            string callClassName = _constants[classNameIdx].AsString();
            string callMethodName = _constants[methodNameIdx].AsString();
            var callArgs = new Value[callArgCount];
            for (int i = callArgCount - 1; i >= 0; i--) callArgs[i] = Pop();

            var callRc = ResolveClass(callClassName);
            var (callProto, declaringRcCall, accessCall) = callRc.FindMethodWithAccess(callMethodName, callArgs.Length);
            if (callProto == null)
                throw new InvalidOperationException(DescribeMethodNotFound(callRc, callMethodName, callArgs.Length));
            if (!callProto.IsStatic)
                throw new InvalidOperationException(
                    $"'{callMethodName}' on '{callClassName}' is not a static method - " +
                    $"'ClassName.{callMethodName}(...)' can only be used to call 'static' methods.");
            if (ExecutionMode != VmExecutionMode.Performance && !IsMemberAccessAllowed(declaringRcCall!, accessCall))
            {
                ThrowAccessDenied(
                    $"Static method '{callMethodName}' of '{declaringRcCall!.Name}' is " +
                    $"{DescribeAccess(accessCall)} and cannot be called from here.");
                return;
            }
            CheckArity(callProto, callArgs.Length);
            callArgs = FillDefaultArgs(callProto, callArgs, null);

            if (callProto.ParamCount == callArgCount)
                StoreSite(site, new SiteCache(null, callProto, 0));

            _frames.Push(new CallFrame(_currentChunk, _ip, _currentScope, _currentThis, null));
            var callScope = new Scope(_globalScope);
            foreach (var a in callArgs) callScope.DefineSlot(a);
            ApplyCopyMask(callScope, copyMask);

            // Explizit KEIN 'this' (anders als oben bei CallBaseMethod,
            // das die aufrufende Instanz beibehält) - der Resolver
            // verbietet 'this'/'super' im Körper einer statischen
            // Methode bereits (siehe Resolver.ResolveExpr/ThisExpr),
            // das hier ist die zusätzliche Laufzeit-Absicherung dafür.
            _currentThis = null;
            _currentScope = callScope;
            _currentChunk = callProto.Chunk;
            _ip = 0;
            return;
        }
        }

        private void OpCallProtoWithThis()
        {
        {
            int protoIdx = ReadU16();
            int argCount = ReadByte();
            var proto = _currentChunk.Functions[protoIdx];
            Scope scope;
            Value thisVal;
            if (argCount == 0)
            {
                // Feld-Initialisierer (keine Parameter): kein Argument-Array, Scope aus dem Pool
                thisVal = Pop();
                CheckArity(proto, 0);
                _frames.Push(new CallFrame(_currentChunk, _ip, _currentScope, _currentThis, null));
                scope = RentCallScope(SlotSlack, 0);
            }
            else
            {
                var args = new Value[argCount];
                for (int i = argCount - 1; i >= 0; i--) args[i] = Pop();
                thisVal = Pop();
                CheckArity(proto, args.Length);

                _frames.Push(new CallFrame(_currentChunk, _ip, _currentScope, _currentThis, null));
                scope = RentCallScope(argCount + SlotSlack, argCount);
                Array.Copy(args, scope.SlotArray, argCount);
            }

            _currentThis = thisVal.Kind == ValueKind.Class ? thisVal.AsObjectRef() : (object)thisVal;
            _currentScope = scope;
            _currentChunk = proto.Chunk;
            _ip = 0;
            return;
        }
        }

        private void OpNewArray()
        {
        {
            long size = Pop().AsInt();
            if (size < 0)
                throw new InvalidOperationException($"Invalid array size {size}.");
            Push(Value.MakeArray(new ScriptArray((int)size)));
            return;
        }
        }

        private void OpMakeArrayLiteral()
        {
        {
            int count = ReadU16();
            var arr = new ScriptArray(count);
            for (int i = count - 1; i >= 0; i--)
                arr.Items[i] = Pop();
            Push(Value.MakeArray(arr));
            return;
        }
        }

        private void OpArrayGet()
        {
            // Schnellpfad: Array mit int-Index im gültigen Bereich (alles andere - auch der Fehlerfall - unten).
            ref Value fastTarget = ref _stack[_sp - 2];
            ref Value fastIndex = ref _stack[_sp - 1];
            if (fastTarget.Kind == ValueKind.Array && fastIndex.Kind == ValueKind.Int)
            {
                var fastArray = fastTarget.AsArray();
                var items = fastArray.Items;
                long i = fastIndex.AsInt();
                if ((ulong)i < (ulong)items.Length && !fastArray.IsShared)
                {
                    fastTarget = items[i];
                    _sp--;
                    return;
                }
            }
            OpArrayGetSlow();
        }

        private void OpArrayGetSlow()
        {
        {
            var indexVal = Pop();
            var target = Pop();

            if (target.Kind == ValueKind.Array && target.AsArray().IsShared)
            {
                // Array des geteilten Bereichs (siehe GlobalsBroker): ein Fire-Thread liest es unter dem Lock
                long sharedIdx = indexVal.AsInt();
                if (TryGetSharedElement(target.AsArray(), sharedIdx, out var sharedValue))
                {
                    if (_threadBroker != null) MarkShared(sharedValue);
                    Push(sharedValue);
                }
                else ThrowIndexOutOfBounds(sharedIdx, target.AsArray().Length);
            }
            else if (target.Kind == ValueKind.Array)
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
            else if (target.Kind == ValueKind.String)
            {
                // `s[i]` liest das Zeichen an Index i (nur lesend - Zeichenketten sind
                // unveränderlich, siehe ArraySet).
                long idx = indexVal.AsInt();
                string text = target.AsString();
                if (idx >= 0 && idx < text.Length)
                    Push(Value.MakeChar(text[(int)idx]));
                else
                    ThrowIndexOutOfBounds(idx, text.Length, "String index");
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
                    $"Index access ('[]') on a value of type {target.Kind} is not possible " +
                    "(neither an array nor a class with a 'GetIndex' method).");
            }
            return;
        }
        }

        private void OpArraySet()
        {
            // Schnellpfad wie bei OpArrayGet.
            ref Value fastTarget = ref _stack[_sp - 3];
            ref Value fastIndex = ref _stack[_sp - 2];
            if (fastTarget.Kind == ValueKind.Array && fastIndex.Kind == ValueKind.Int)
            {
                var fastArray = fastTarget.AsArray();
                var items = fastArray.Items;
                long i = fastIndex.AsInt();
                if ((ulong)i < (ulong)items.Length && !fastArray.IsShared)
                {
                    var assigned = _stack[_sp - 1];
                    items[i] = assigned;
                    _sp -= 2;
                    _stack[_sp - 1] = assigned;
                    return;
                }
            }
            OpArraySetSlow();
        }

        private void OpArraySetSlow()
        {
        {
            var value = Pop();
            var indexVal = Pop();
            var target = Pop();

            if (target.Kind == ValueKind.Array && target.AsArray().IsShared)
            {
                // Array des geteilten Bereichs: ein Fire-Thread ändert es nur in einer Sektion, überall unter dem Lock
                long sharedIdx = indexVal.AsInt();
                if (TrySetSharedElement(target.AsArray(), sharedIdx, value)) Push(value);
                else ThrowIndexOutOfBounds(sharedIdx, target.AsArray().Length);
            }
            else if (target.Kind == ValueKind.Array)
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
                        $"Assigning to a byte buffer expects an int value (byte = int[8]), not {value.Kind}.");
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
            else if (target.Kind == ValueKind.String)
            {
                throw new InvalidOperationException(
                    "Strings are immutable - 's[i] = ...' is not possible " +
                    "(Replace/Substring return a new string).");
            }
            else
            {
                throw new InvalidOperationException(
                    $"Index assignment ('[]=') on a value of type {target.Kind} is not possible " +
                    "(neither an array nor a class with a 'SetIndex' method).");
            }
            return;
        }
        }

        private void Execute(OpCode op)
        {
            switch (op)
            {
                case OpCode.LoadConst:
                    Push(_constants[ReadU16()]);
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

                case OpCode.RotateUnderTop:
                {
                    var c = Pop(); // oben
                    var b = Pop();
                    var a = Pop(); // unten
                    Push(b);
                    Push(a);
                    Push(c);
                    break;
                }

                case OpCode.IncDecIndex:
                    OpIncDecIndex();
                    break;

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
                    {
                        int loadSlot = ReadU16();
                        Push(loadSlot < _sharedCount ? LoadSharedGlobal(loadSlot) : _globalScope.GetSlot(loadSlot));
                    }
                    break;

                case OpCode.StoreGlobal:
                    {
                        int storeSlot = ReadU16();
                        if (storeSlot < _sharedCount) StoreSharedGlobal(storeSlot, Peek());
                        else if (_ownerBroker != null) StoreOwnerGlobal(storeSlot, Peek());
                        else _globalScope.SetSlot(storeSlot, Peek());
                    }
                    break;

                case OpCode.DeclareLocal:
                    if (_ownerBroker != null && ReferenceEquals(_currentScope, _globalScope)) DeclareOwnerGlobal(Pop());
                    else _currentScope.DefineSlot(Pop());
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
                    string format = _constants[ReadU16()].AsString();
                    var v = Pop();
                    if (v.Kind == ValueKind.Class && string.IsNullOrEmpty(format))
                    {
                        // `$"{objekt}"`: das Ergebnis von ToString() des Objekts
                        var text = StringifyForText(v);
                        if (text == null) break;
                        Push(Value.MakeString(text));
                        break;
                    }
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
                    _currentScope = RentScope(_currentScope);
                    break;

                case OpCode.ExitScope:
                    ExitScopeOwning();
                    break;

                case OpCode.CallNative:
                    OpCallNative();
                    break;

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
                    PollSignalsAfterOp();
                    break;
                }

                case OpCode.CallExtern:
                {
                    int nameIdx = ReadU16();
                    int argCount = ReadByte();
                    string externName = _constants[nameIdx].AsString();

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
                                $"'{externName}' is declared extern, but neither linked manually " +
                                "(ExternRegistry.Register in the host) nor assigned to a " +
                                "native library with '#extern \"libName\"'.");
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
                    PollSignalsAfterOp();
                    break;
                }

                case OpCode.MakeLambda:
                    OpMakeLambda();
                    break;

                case OpCode.MakeLambdaCapturing:
                    OpMakeLambdaCapturing();
                    break;

                case OpCode.Probe:
                    OpProbe();
                    break;

                case OpCode.SilenceMember:
                    OpSilenceMember();
                    break;

                case OpCode.SilenceValue:
                    OpSilenceValue();
                    break;

                case OpCode.Call:
                    OpCall();
                    break;

                case OpCode.Return:
                    OpReturn();
                    break;

                case OpCode.NewObject:
                    OpNewObject();
                    break;

                case OpCode.NewObjectOwned:
                    OpNewObjectOwned();
                    break;

                case OpCode.CopyValue:
                {
                    // `flat x` / `copy x` (SPEC 2.4) - die Kopie gehört dem aktuellen Scope (SPEC 2.1).
                    bool deep = (ReadByte() & 1) != 0;
                    var source = Pop();
                    Push(ObjectCloner.Clone(source, _currentScope, deep));
                    break;
                }

                case OpCode.CopyArgs:
                {
                    int lo = ReadU16();
                    int hi = ReadU16();
                    _copyArgMask = lo | (hi << 16);
                    break;
                }

                case OpCode.CopyValueOwned:
                {
                    // Direkt einem Feld zugewiesen: die Kopie gehört dem Zielobjekt (wie NewObjectOwned).
                    bool deep = (ReadByte() & 1) != 0;
                    var source = Pop();
                    var owner = RequireObjectInstance(Pop(), "Copy with owner");
                    Push(ObjectCloner.CloneOwnedBy(source, owner, deep, this));
                    break;
                }

                case OpCode.ConstructBase:
                    OpConstructBase();
                    break;

                case OpCode.GetField:
                    OpGetField();
                    break;

                case OpCode.SetField:
                    OpSetField();
                    break;

                case OpCode.LoadThis:
                    OpLoadThis();
                    break;

                case OpCode.SetFieldOnThis:
                    OpSetFieldOnThis();
                    break;

                case OpCode.CallMethod:
                    OpCallMethod();
                    break;

                case OpCode.CallBaseMethod:
                    OpCallBaseMethod();
                    break;

                case OpCode.GetStaticField:
                    OpGetStaticField();
                    break;

                case OpCode.SetStaticField:
                    OpSetStaticField();
                    break;

                case OpCode.SetStaticFieldOnInit:
                {
                    // Wie SetStaticField, aber OHNE Zugriffsmodifikator-Prüfung
                    // (siehe OpCode.SetStaticFieldOnInit-Doku) - NUR für die
                    // einmalige Initialisierung eines statischen Feldes beim
                    // Programmstart (siehe Compiler.Compile), analog zu
                    // SetFieldOnThis bei Instanzfeldern. Einheiten-Prüfung
                    // bleibt (wie bei SetFieldOnThis) trotzdem bestehen - die
                    // gilt unabhängig davon, WER schreibt.
                    string initClassName = _constants[ReadU16()].AsString();
                    string initFieldName = _constants[ReadU16()].AsString();
                    var initValue = Pop();
                    var initRc = ResolveClass(initClassName);
                    var initOwner = initRc.FindStaticFieldOwner(initFieldName);

                    if (initOwner == null)
                        throw new InvalidOperationException($"'{initClassName}' has no static field '{initFieldName}'.");

                    if (ExecutionMode != VmExecutionMode.Performance)
                    {
                        string? requiredUnitName = initOwner.FindFieldRequiredUnit(initFieldName);
                        if (requiredUnitName != null)
                        {
                            var requiredUnit = Values.Unit.Parse(requiredUnitName);
                            var actualUnit = initValue.Unit ?? Values.Unit.Unitless;
                            if (!actualUnit.Equals(requiredUnit))
                            {
                                ThrowUnitMismatch(requiredUnitName, actualUnit);
                                break;
                            }
                        }
                    }

                    initOwner.StaticFieldValues[initFieldName] = initValue;
                    break;
                }

                case OpCode.CallStaticMethod:
                    OpCallStaticMethod();
                    break;

                case OpCode.CallProtoWithThis:
                    OpCallProtoWithThis();
                    break;

                case OpCode.AddressOfLocal:
                {
                    int depth = ReadU16(); int slot = ReadU16();
                    Push(Value.MakePointer(new ScopeSlotPointerTarget(_currentScope.GetAncestor(depth), slot)));
                    break;
                }

                case OpCode.AddressOfGlobal:
                {
                    int slot = ReadU16();
                    if (slot < _sharedCount)
                        throw new InvalidOperationException("A fire thread cannot take a pointer to a global variable of the main program ('&').");
                    Push(Value.MakePointer(new ScopeSlotPointerTarget(_globalScope, slot)));
                    break;
                }

                case OpCode.AddressOfField:
                {
                    string fieldName = _constants[ReadU16()].AsString();
                    var obj = RequireObjectInstance(Pop(), "Address-of on field");
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
                    OpNewArray();
                    break;

                case OpCode.MakeArrayLiteral:
                    OpMakeArrayLiteral();
                    break;

                case OpCode.MakeBuffer:
                {
                    long size = Pop().AsInt();
                    if (size < 0)
                        throw new InvalidOperationException($"Invalid byte buffer size {size} (must be >= 0).");
                    Push(Value.MakeBuffer(new ByteBuffer((int)size, ByteConversions.HostByteOrder)));
                    break;
                }

                case OpCode.ArrayGet:
                    OpArrayGet();
                    break;

                case OpCode.ArraySet:
                    OpArraySet();
                    break;

                case OpCode.RegisterHandler:
                {
                    int templateIdx = ReadU16();
                    var template = _currentChunk.Handlers[templateIdx];
                    _handlers.Add(new ActiveHandler(_currentChunk, _frames.Count, _currentScope, template, _sp));
                    break;
                }

                case OpCode.UnregisterHandler:
                    _handlers.RemoveAt(_handlers.Count - 1);
                    break;

                case OpCode.EnterFinallyNormal:
                    Push(Value.MakeUndefined());
                    Push(Value.MakeInt(FinallyNormal));
                    break;

                case OpCode.PushJump:
                    Push(Value.MakeInt(ReadU16()));
                    Push(Value.MakeInt(FinallyJump));
                    break;

                case OpCode.EndFinally:
                {
                    int kind = (int)Pop().AsInt();
                    var payload = Pop();
                    switch (kind)
                    {
                        case FinallyNormal:
                            break;
                        case FinallyThrow:
                            ThrowException(payload);
                            break;
                        case FinallyReturn:
                            DoReturn(payload);
                            break;
                        case FinallyJump:
                            _ip = (int)payload.AsInt();
                            break;
                        case FinallyNestedReturn:
                        {
                            // Ende eines verschachtelt gestarteten finally (leave/terminate, siehe RunFinallyInlineNested): zurück zum Aufrufer
                            var frame = _frames.Pop();
                            _currentChunk = frame.ReturnChunk;
                            _ip = frame.ReturnIp;
                            _currentScope = frame.ReturnScope;
                            _currentThis = frame.ReturnThis;
                            break;
                        }
                    }
                    break;
                }

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
                    string typeName = _constants[ReadU16()].AsString();
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
                    var ownerObj = RequireObjectInstance(ownerVal, "'is from'/'is under' (owner expression)");
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
                            "resume() was called, but this exception is not being handled right now " +
                            "(either it was already resumed, or there is no active catch for it).");

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

                    // Operanden der Wurfstelle zurück auf den Stack (der catch-Kontext, den resume() verlässt, wird verworfen)
                    _sp = pending.Handler.StackPointer;
                    foreach (var operand in pending.Continuation.Stack) Push(operand);

                    // Die fortgesetzte Stelle ist konzeptionell "immer noch im
                    // try-Block" - Handler wieder scharf schalten (siehe
                    // PendingResume-Kommentar), sonst reißt ein erneuter throw
                    // dort keinen passenden catch mehr und UnregisterHandler am
                    // Ende des try-Blocks entfernt versehentlich einen fremden
                    // Eintrag.
                    while (_handlers.Count > pending.HandlerCount) _handlers.RemoveAt(_handlers.Count - 1);
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
                            $"Expected a lambda value (type 'lambda'), got: {v.Kind}.");
                    var lambdaVal = (LambdaValue)v.AsLambda();
                    if (lambdaVal.Proto.ParamCount != expectedParamCount)
                        throw new InvalidOperationException(
                            $"Lambda signature does not match: expected {expectedParamCount} parameters, " +
                            $"the lambda has {lambdaVal.Proto.ParamCount}.");
                    break;
                }

                case OpCode.CheckUnit:
                {
                    // Wie CheckLambdaSignature: prüft nur Peek() (NICHT Pop()),
                    // der Wert wird direkt danach noch normal weiterverwendet
                    // (siehe OpCode.CheckUnit-Doku/Compiler.EmitCheckUnitIfNeeded).
                    // Anders als bei CheckLambdaSignature (roher C#-Fehler) wirft
                    // ein Mismatch hier aber eine ECHTE, per try/catch fangbare
                    // Skript-Exception (siehe ThrowUnitMismatch) - explizit vom
                    // Nutzer per SPEC "Einheiten-Deklarationen" so gewünscht.
                    // Im Performance-Modus übersprungen - wie jede andere
                    // "zusätzliche Sicherheit statt Geschwindigkeit"-Prüfung in
                    // dieser VM (Zugriffsmodifikatoren, Array-/Puffer-Bounds).
                    string requiredUnitName = _constants[ReadU16()].AsString();
                    if (ExecutionMode != VmExecutionMode.Performance)
                    {
                        var checkedValue = Peek();
                        var requiredUnit = Values.Unit.Parse(requiredUnitName);
                        var actualUnit = checkedValue.Unit ?? Values.Unit.Unitless;
                        if (!actualUnit.Equals(requiredUnit))
                        {
                            ThrowUnitMismatch(requiredUnitName, actualUnit);
                            break;
                        }
                    }
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

                    // Die Globals des Hauptprogramms werden NICHT kopiert: der Thread liest sie direkt (unter dem Lock) und ändert sie nur in
                    // einer Sektion, die das Hauptprogramm bei `sync globals` erteilt (siehe Runtime.GlobalsBroker). Beim ersten `fire`
                    // wird dafür alles, was die Globals erreichen, in den geteilten Bereich aufgenommen (Locking aktiv). Ein Thread, der selbst
                    // `fire` ausführt, reicht seine Verbindung weiter.
                    var broker = IsFireThreadVm ? _threadBroker : EnsureOwnerBroker();

                    var fireProto = _currentChunk.Functions[protoIdx];
                    // Der neue Fire-Thread erbt den ExecutionMode DIESER VM -
                    // sonst würde jeder `fire`-Thread stillschweigend wieder
                    // im (langsamsten) Debug-Modus laufen, unabhängig davon,
                    // in welchem Modus das Hauptprogramm selbst läuft (siehe
                    // VmExecutionMode-Doku).
                    FireRuntime.FireVmTaking(
                        fireProto.Chunk, _natives, _classes, broker, globalSlotCount, takingValues, withValue,
                        executionMode: ExecutionMode);
                    break;
                }

                case OpCode.SyncGlobals:
                    Push(Value.MakeInt(SyncGlobalsNow()));
                    break;

                case OpCode.SetAutoSync:
                    _autoSync = ReadByte() != 0;
                    break;

                case OpCode.SetTimeout:
                {
                    var timeout = Pop();
                    if (!TimeNatives.TryTimeTicks(timeout, out long timeoutTicks, out var timeoutError))
                        throw new InvalidOperationException("#timeout " + timeoutError);
                    SetDefaultTimeout(TimeSpan.FromTicks(timeoutTicks));
                    break;
                }

                case OpCode.SectionEnter:
                    if (_threadBroker != null) EnterGlobalsSection(); // im Hauptprogramm: wirkungslos (es ist selbst der Besitzer)
                    break;

                case OpCode.SectionExit:
                    if (_threadBroker != null && _sectionDepth > 0) ExitGlobalsSection();
                    break;

                case OpCode.PostGlobal:
                {
                    int jobArgCount = ReadByte();
                    var jobLambda = (LambdaValue)Pop().AsLambda();
                    var jobArgs = new Value[jobArgCount];
                    for (int i = jobArgCount - 1; i >= 0; i--) jobArgs[i] = Pop();

                    // Die Argumente gehören dem Auftrag, nicht diesem Thread: Objekte werden tief kopiert, ihr Besitzer ist ein Halter-Scope, den
                    // das Hauptprogramm nach dem Lauf freigibt.
                    var holder = new Scope(null);
                    for (int i = 0; i < jobArgs.Length; i++)
                        if (jobArgs[i].Kind == ValueKind.Class) jobArgs[i] = ObjectCloner.Clone(jobArgs[i], holder, deep: true);

                    var postBroker = IsFireThreadVm ? _threadBroker! : EnsureOwnerBroker();
                    postBroker.PostJob(jobLambda, jobArgs, holder);
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
                    // Der aufrufende Thread geht sofort in den Halt (ShutdownSelfNow) - nach `leave` läuft keine Anweisung mehr.
                    RequestLeave();
                    ShutdownSelfNow();
                    break;

                case OpCode.Terminate:
                {
                    // Auch wenn ein anderer Thread schneller war (erster Aufruf gewinnt): dieser Thread hält hier an.
                    var terminateValue = Pop();
                    RequestTerminate(terminateValue);
                    ShutdownSelfNow();
                    break;
                }

                case OpCode.RegisterThreadsCatch:
                {
                    int protoIdx = ReadU16();
                    bool hasType = ReadByte() != 0;
                    string? typeName = hasType ? _constants[ReadU16()].AsString() : null;
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
                    throw new InvalidOperationException($"Unknown opcode {op}");
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
                            $"Values of type {v.Kind} cannot be passed to an extern function.");
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
                $"A return value of the native type {nativeResult.GetType().Name} cannot be converted to a script value " +
                "(supported: bool/int/float/char/string)."),
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
                    new System.Reflection.AssemblyName("fireDynamicExterns"),
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
                        $"Native library '{libName}' could not be loaded (for extern '{externName}', " +
                        $"declared with '#extern \"{libName}\"'): {ex.Message}", ex);
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
                    $"Function '{externName}' was not found in '{libName}': {ex.Message}", ex);
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
                    $"Could not build a callable delegate for extern '{externName}' from '{libName}': {ex.Message}", ex);
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
                string typeName = $"fireExtern_{externName}_{System.Threading.Interlocked.Increment(ref _dynamicDelegateCounter)}";

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
                    $"extern '{externName}': a parameter/return type is missing or too unspecific " +
                    "for dynamic linking (bool/int/float/char/string or a pointer type is required).");
            if (t.PointerDepth > 0) return typeof(IntPtr);
            return t.BaseName switch
            {
                "bool" => typeof(bool),
                "int" => typeof(long),
                "float" => typeof(double),
                "char" => typeof(char),
                "string" => typeof(string),
                _ => throw new InvalidOperationException(
                    $"extern '{externName}': type '{t.BaseName}' cannot be linked dynamically " +
                    "(supported: bool/int/float/char/string/pointer)."),
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
                        $"A pointer to a value of type {v.Kind} cannot be marshalled to an extern function " +
                        "(supported: int/float/bool/char).");
            }
        }

        private static Value ReadNativeValue(IntPtr ptr, ValueKind kind) => kind switch
        {
            ValueKind.Int => Value.MakeInt(System.Runtime.InteropServices.Marshal.ReadInt64(ptr)),
            ValueKind.Float => Value.MakeFloat(ReadNativeDouble(ptr)),
            ValueKind.Bool => Value.MakeBool(System.Runtime.InteropServices.Marshal.ReadByte(ptr) != 0),
            ValueKind.Char => Value.MakeChar((char)System.Runtime.InteropServices.Marshal.ReadInt32(ptr)),
            _ => throw new InvalidOperationException(
                $"Unsupported pointer target type {kind} when reading back from native memory."),
        };

        private static double ReadNativeDouble(IntPtr ptr)
        {
            var buf = new byte[8];
            System.Runtime.InteropServices.Marshal.Copy(ptr, buf, 0, 8);
            return BitConverter.ToDouble(buf, 0);
        }

        private void BeginConstruction(ObjectInstance instance, FunctionProto ctorProto, Value[] args, int copyMask = 0)
        {
            _frames.Push(new CallFrame(_currentChunk, _ip, _currentScope, _currentThis, instance));

            var ctorScope = new Scope(_globalScope);
            foreach (var a in args) ctorScope.DefineSlot(a);
            if (copyMask != 0) ApplyCopyMask(ctorScope, copyMask);

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

            // In einem verschachtelten Callback (CallLambdaInline) gehören die Handler bis zur Untergrenze dem Aufrufer.
            int handlerFloor = _callbackBoundaries.Count > 0 ? _callbackBoundaries.Peek().HandlerFloor : 0;
            while (_handlers.Count > handlerFloor)
            {
                var handler = _handlers[^1];
                _handlers.RemoveAt(_handlers.Count - 1);

                int? matchedAddr = handler.FinallyOnly ? null : FindMatchingCatch(handler.Template, excInstance);

                if (matchedAddr != null)
                {
                    // WICHTIG: hier NICHT destruktiv abwickeln (UnwindTo) -
                    // stattdessen den Wurfstellen-Zustand einfrieren
                    // (CaptureContinuation), damit ein mögliches `resume()`
                    // später exakt hierher zurückspringen kann. Nichts wird
                    // zerstört, solange nicht klar ist, ob resume() aufgerufen
                    // wird oder nicht (siehe ClearPendingResume).
                    var continuation = CaptureContinuation(handler.FrameDepthAtEntry);
                    _pendingResumes[excInstance] = new PendingResume(continuation, handler, _handlers.Count);

                    // Der `catch` beginnt auf der Stack-Höhe des `try`: die Operanden der Wurfstelle (z.B. der Enumerator eines `foreach`, aus dem
                    // geworfen wurde, oder halb ausgewertete Ausdrücke des Aufrufers tieferer Frames) bleiben nicht als Leichen liegen und
                    // verschieben später keine Operanden. `resume()` spielt sie zurück.
                    if (_sp > handler.StackPointer)
                    {
                        continuation.Stack = new Value[_sp - handler.StackPointer];
                        Array.Copy(_stack, handler.StackPointer, continuation.Stack, 0, continuation.Stack.Length);
                        _sp = handler.StackPointer;
                    }

                    // Hat der `try` ein `finally`, bleibt es für die Dauer des `catch`-Blocks aktiv (eine Exception AUS dem catch muss es auslösen)
                    if (handler.Template.FinallyAddr != null)
                        _handlers.Add(new ActiveHandler(handler.Chunk, handler.FrameDepthAtEntry, handler.TargetScope, handler.Template, handler.StackPointer, finallyOnly: true));

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

                // Die Exception geht an diesem `try` vorbei - sein `finally` läuft (im selben Chunk, mit den lokalen Variablen), und `EndFinally`
                // wirft sie danach weiter (Abschluss "Exception"). Die Operanden der Wurfstelle verfallen (kein resume() über ein finally hinweg).
                if (handler.Template.FinallyAddr is int finallyAddr)
                {
                    if (_sp > handler.StackPointer) _sp = handler.StackPointer;
                    Push(exceptionValue);
                    Push(Value.MakeInt(FinallyThrow));
                    _currentChunk = handler.Chunk;
                    _ip = finallyAddr;
                    return;
                }
            }

            // Unbehandelt in einem Callback: nur der Callback bricht ab (Abwickeln bis zu seinem Aufrufer, dort hört CallLambdaInline
            // den Fehler ab) - das Programm läuft weiter, und der Aufrufer des Callbacks sieht keine Ausnahme (SPEC 8.1.4).
            if (_callbackBoundaries.Count > 0)
            {
                var boundary = _callbackBoundaries.Peek();
                UnwindTo(boundary.FrameDepth, boundary.Scope);
                _sp = boundary.StackPointer;
                _callbackError = excInstance;
                return;
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
                RaiseSignal();
                // Der globale Scope bleibt stehen: die Exception gehört ihm (TakeGlobal oben) und wird noch an den Main-Thread zugestellt.
                UnwindForShutdown(); // _handlers ist an dieser Stelle ohnehin schon leer, siehe Schleife oben - äquivalent zu UnwindTo(0, _globalScope), aber ein Aufruf statt Code-Duplikat.
                StopExecution();
                return;
            }

            // Kein Handler in DIESER VM-Instanz hat gematcht - das Programm
            // hält hier an (siehe UnhandledException-Doku: gesetzt statt
            // geworfen, Run() kehrt gleich danach über den nächsten
            // CheckShutdownSignals-Prüfpunkt ganz normal zurück).
            UnhandledException = excInstance;
            StopExecution();
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
            if (!_classes.TryGetValue(instance.ClassName, out var rc)) return false;
            for (; rc != null; rc = rc.Base)
                if (rc.Name == typeName || rc.Interfaces.Contains(typeName)) return true;
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
            // Arrays und Puffer sind durchlaufbar (GetEnumerator, foreach): sie erfüllen das Interface IEnumerable der Prelude
            if (v.Kind is ValueKind.Array or ValueKind.Buffer && typeName == "IEnumerable") return true;
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
                            "Unwind beyond the global scope (inconsistent handler state).");
                }
            }
        }

        /// <summary>Führt einen `finally`-Block im Chunk `chunk` an `addr` verschachtelt aus (für `leave`/`terminate`, die jedes offene `finally` ablaufen lassen,
        /// ohne zurückzukehren): wie ein Aufruf, der Block selbst läuft im Scope des `try`; sein `EndFinally` (Abschluss 4) kehrt hierher zurück.</summary>
        private void RunFinallyInlineNested(Chunk chunk, int addr)
        {
            _frames.Push(new CallFrame(_currentChunk, _ip, _currentScope, _currentThis, null));
            int targetDepth = _frames.Count;

            Push(Value.MakeUndefined());
            Push(Value.MakeInt(FinallyNestedReturn));
            _currentChunk = chunk;
            _ip = addr;

            RunNestedUntil(targetDepth);
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

        /// <summary>Führt ein Lambda, das ein NATIVER Aufruf dieser VM auslöst (z.B. ein Fenster-Ereignis, das `Window.Tick` liefert),
        /// verschachtelt auf dieser VM aus: es sieht die echten globalen Variablen (lesend UND schreibend, wie jedes Lambda, SPEC 4.2),
        /// ohne Kopie und ohne Thread-Sperren, und `leave`/`terminate` darin wirken auf dieses Programm. Nur aufrufen, wenn der Aufruf
        /// auf dem Thread dieser VM geschieht (`VM.CurrentThreadVm`). Liefert null, wenn das Lambda normal endete (oder das Programm
        /// beendet wurde), sonst die unbehandelte Exception des Lambdas - die den Aufrufer NICHT unterbricht: wie bei jedem Callback
        /// meldet sie der Host, das Programm läuft weiter.</summary>
        public ObjectInstance? CallLambdaInline(LambdaValue lambda, Value[] args)
        {
            CheckArity(lambda.Proto, args.Length);
            args = FillDefaultArgs(lambda.Proto, args, lambda.OnTarget);

            int frameDepth = _frames.Count;
            var callerScope = _currentScope;
            int stackPointer = _sp;

            _frames.Push(new CallFrame(_currentChunk, _ip, _currentScope, _currentThis, null));
            int targetDepth = _frames.Count;
            _callbackBoundaries.Push(new CallbackBoundary(frameDepth, callerScope, _handlers.Count, stackPointer));
            _callbackError = null;

            var funcScope = new Scope(_globalScope);
            foreach (var a in args) funcScope.DefineSlot(a);
            if (lambda.Captures != null) foreach (var c in lambda.Captures) funcScope.DefineSlot(c);

            _currentThis = lambda.OnTarget;
            _currentScope = funcScope;
            _currentChunk = lambda.Proto.Chunk;
            _ip = 0;

            try
            {
                RunNestedUntil(targetDepth);
            }
            catch
            {
                // Eine C#-Ausnahme mitten im Callback (z.B. ein Zugriff außerhalb des Arrays im Performance-Modus, der nichts prüft):
                // den Zustand des Aufrufers wiederherstellen, damit das Programm weiterlaufen kann, und die Ausnahme dem Host melden.
                while (_frames.Count > frameDepth + 1) _frames.Pop();
                var callerFrame = _frames.Pop();
                _currentChunk = callerFrame.ReturnChunk;
                _ip = callerFrame.ReturnIp;
                _currentScope = callerFrame.ReturnScope;
                _currentThis = callerFrame.ReturnThis;
                _sp = stackPointer;
                var floor = _callbackBoundaries.Pop().HandlerFloor;
                while (_handlers.Count > floor) _handlers.RemoveAt(_handlers.Count - 1);
                throw;
            }
            _callbackBoundaries.Pop();

            // `leave`/`terminate` im Callback: das Programm ordentlich abwickeln (beim Zurückkehren in die Hauptschleife steht der Halt).
            if (_shutdownDeferred) FinishDeferredShutdown();

            var error = _callbackError;
            _callbackError = null;
            if (error != null) return error;

            PopNestedResult(); // der (unbenutzte) Rückgabewert des Lambdas
            return null;
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
            if (lambda.Captures != null) foreach (var c in lambda.Captures) funcScope.DefineSlot(c);

            _currentThis = lambda.OnTarget;
            _currentScope = funcScope;
            _currentChunk = lambda.Proto.Chunk;
            _ip = 0;

            RunNestedUntil(targetDepth);

            // `leave`/`terminate` im Callback: sauber abwickeln (kein Fehler, der Callback liefert nichts).
            if (_shutdownDeferred)
            {
                FinishDeferredShutdown();
                return Value.MakeUndefined();
            }

            // Ein unbehandelter Fehler im Callback beendet die VM (StopExecution): der Host bekommt ihn als Ausnahme, die er
            // bewusst fangen kann (siehe FireRuntime.CallCallback) - statt dass hier ein Rückgabewert fehlt.
            if (_stopExecutionRequested)
                throw UnhandledException != null
                    ? new UncaughtScriptException(UnhandledException)
                    : new InvalidOperationException("The callback was terminated early.");

            return Pop();
        }

        private Value? CallMethodNested(ObjectInstance obj, string methodName, Value[] args)
        {
            var rc = ResolveClass(obj.ClassName);
            var (proto, declaringRcNested, accessNested) = rc.FindMethodWithAccess(methodName, args.Length);
            if (proto == null)
                throw new InvalidOperationException(
                    $"Method '{methodName}' not found on '{rc.Name}' (needed for a property or operator overload).");
            // Deckt sowohl Property-Zugriffe (get_X/set_X, siehe VM.GetField/
            // SetField) als auch Operator-Überladungen ab (siehe
            // Parser.ParseOperatorMember) - Operatoren bekommen nie einen
            // expliziten Modifikator (immer Public), die Prüfung greift hier
            // also praktisch nur für Properties.
            if (ExecutionMode != VmExecutionMode.Performance && !IsMemberAccessAllowed(declaringRcNested!, accessNested))
            {
                ThrowAccessDenied(
                    $"'{methodName}' of '{declaringRcNested!.Name}' is {DescribeAccess(accessNested)} " +
                    "and cannot be accessed from here.");
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

        /// <summary>Wie CallMethodNested, aber für eine STATISCHE Methode
        /// (SPEC "Statische Mitglieder") - kein ObjectInstance, kein
        /// gebundenes 'this' (siehe OpCode.CallStaticMethod für dieselbe
        /// Begründung). Für den Property-Getter-Fallback in GetStaticField
        /// (statisches 'get_X', analog zu CallMethodNested dort für
        /// Instanz-Properties).</summary>
        private Value? CallStaticMethodNested(RuntimeClass rc, string methodName, Value[] args)
        {
            var (proto, declaringRcNested, accessNested) = rc.FindMethodWithAccess(methodName, args.Length);
            if (proto == null)
                throw new InvalidOperationException(
                    $"Static method '{methodName}' not found on '{rc.Name}' (needed for a property).");
            if (ExecutionMode != VmExecutionMode.Performance && !IsMemberAccessAllowed(declaringRcNested!, accessNested))
            {
                ThrowAccessDenied(
                    $"'{methodName}' of '{declaringRcNested!.Name}' is {DescribeAccess(accessNested)} " +
                    "and cannot be accessed from here.");
                return null;
            }
            CheckArity(proto, args.Length);
            args = FillDefaultArgs(proto, args, null);

            var savedChunk = _currentChunk;
            var savedIp = _ip;
            var savedScope = _currentScope;
            var savedThis = _currentThis;

            _frames.Push(new CallFrame(savedChunk, savedIp, savedScope, savedThis, null));
            int targetDepth = _frames.Count;

            var scope = new Scope(_globalScope);
            foreach (var a in args) scope.DefineSlot(a);

            _currentThis = null;
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
            var instance = new ObjectInstance(rc.Name, _currentScope, rc);
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
                    $"Internal error: construction of '{rc.Name}' was interrupted by an exception " +
                    "(exception while building a VM-internal exception instance).");

            Pop(); // Return am Ende des Konstruktors pusht 'instance' selbst (siehe BeginConstruction/frame.ConstructedInstance) - haben wir schon direkt, hier verwerfen
            return instance;
        }

        /// <summary>Ruft eine native Funktion auf. Meldet sie einen ungültigen Index
        /// (<see cref="NativeIndexOutOfRangeException"/>, z.B. `"abc".Substring(9)`), wird daraus eine
        /// fangbare `IndexOutOfBoundsException` des Skripts und `false` geliefert (KEIN Ergebnis pushen -
        /// die Ausführung läuft schon im Handler weiter). Eigene Methode statt try/catch mitten in
        /// Execute, damit dessen Register-Zuteilung unberührt bleibt.</summary>
        private bool CallNativeGuarded(int nativeIdx, Value[] args, out Value result)
        {
            try
            {
                result = _natives[nativeIdx](args);
                if (_nativeRedirected)
                {
                    // Die native Funktion (Reflection) hat eine Exception ausgelöst, die schon in einen Handler umgeleitet ist
                    _nativeRedirected = false;
                    return false;
                }
                return true;
            }
            catch (NativeIndexOutOfRangeException ex)
            {
                result = default;
                ThrowIndexOutOfBounds(ex.Index, ex.Length, ex.What);
                return false;
            }
        }

        /// <summary>Baut eine `IndexOutOfBoundsException`-Instanz (Prelude) und
        /// wirft sie ganz normal über ThrowException - macht einen ungültigen
        /// Array-Index zu einer echten, per `try`/`catch` fangbaren Skript-
        /// Exception statt eines rohen C#-Fehlers, der das ganze Programm
        /// abbrechen würde. Aufgerufen aus ArrayGet/ArraySet, wenn
        /// ScriptArray/ByteBuffer.TryGet/TrySet `false` liefert (bewusst kein
        /// throw/catch dort selbst - siehe ScriptArray-Doku, C++-Portier-
        /// barkeit).</summary>
        private void ThrowIndexOutOfBounds(long index, int length, string what = "Array index")
        {
            var rc = ResolveClass("IndexOutOfBoundsException");
            string msg = $"{what} {index} out of range (length {length}).";
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

        /// <summary>Baut eine `UnitMismatchException`-Instanz (Prelude) und
        /// wirft sie ganz normal über ThrowException (SPEC "Einheiten-
        /// Deklarationen") - macht eine Einheiten-Verletzung bei einer
        /// Deklaration mit explizitem `: einheit` zu einer echten, per
        /// `try`/`catch` fangbaren Skript-Exception. Aufgerufen aus
        /// OpCode.CheckUnit (lokale/globale Variablen, Parameter) sowie
        /// SetField/SetFieldOnThis (Felder, siehe dort für die Begründung,
        /// warum das dort statt zur Compile-Zeit geprüft wird).</summary>
        private void ThrowUnitMismatch(string requiredUnitName, Values.Unit actualUnit)
        {
            var rc = ResolveClass("UnitMismatchException");
            string actualDescription = actualUnit.IsUnitless ? "(no unit)" : actualUnit.ToString();
            string msg = $"Expected unit '{requiredUnitName}', got: {actualDescription}.";
            var args = new[] { Value.MakeString(msg), Value.MakeString(requiredUnitName), Value.MakeString(actualDescription) };
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
            // Läuft der Zugriff über die Reflection-Bibliothek (`Reflect.Get` &amp; Co.), zählt der Code, der sie aufgerufen hat
            if (callerRc is { IsReflectionHelper: true }) callerRc = ReflectionCallerClass();
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

            // `"text" + objekt` / `objekt + "text"`: ein Objekt mit `ToString()` geht mit diesem Text in die Verkettung ein
            if (operatorMethodName == "operator+"
                && ((a.Kind == ValueKind.String && b.Kind == ValueKind.Class)
                    || (b.Kind == ValueKind.String && a.Kind == ValueKind.Class
                        && ResolveClass(((ObjectInstance)a.AsObjectRef()).ClassName).FindMethod("operator+", 1) == null)))
            {
                bool objectIsRight = a.Kind == ValueKind.String;
                var text = StringifyForText(objectIsRight ? b : a);
                if (text == null) return;
                if (objectIsRight) b = Value.MakeString(text); else a = Value.MakeString(text);
            }

            if (a.Kind == ValueKind.Class)
            {
                var obj = (ObjectInstance)a.AsObjectRef();
                var rc = ResolveClass(obj.ClassName);
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
                : throw new InvalidOperationException($"Unknown class '{name}' at run time.");

        private static ObjectInstance RequireObjectInstance(Value v, string context)
        {
            if (v.Kind != ValueKind.Class)
                throw new InvalidOperationException($"{context} on a value of type {v.Kind} which is not an object.");
            return (ObjectInstance)v.AsObjectRef();
        }

        private static void CheckArity(FunctionProto proto, int argCount)
        {
            if (argCount == proto.ParamCount) return;
            if (argCount < proto.ParamCount && RuntimeClass.AllTrailingHaveDefaults(proto, argCount)) return;
            throw new InvalidOperationException(
                $"Wrong number of arguments: expected {proto.ParamCount}" +
                (argCount < proto.ParamCount ? " (the missing parameters have no default value)" : "") +
                $", got {argCount}.");
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
                        $"Internal error: parameter {i} of the call target has no default value " +
                        "(CheckArity should have caught this).");
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
                    "Internal error: evaluating a default value was interrupted by an exception.");

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
                return $"Method '{methodName}' not found on '{rc.Name}'.";

            var distinctArities = arities.Distinct().OrderBy(x => x);
            return $"Method '{methodName}' on '{rc.Name}' has no overload with {argCount} argument(s) " +
                   $"(available: {string.Join(", ", distinctArities)} argument(s)).";
        }

        /// <summary>Analog zu DescribeMethodNotFound, für Konstruktoren (keine
        /// Basisklassen-Kette, siehe RuntimeClass.Constructors-Doku).</summary>
        private static string DescribeConstructorNotFound(RuntimeClass rc, int argCount)
        {
            if (rc.Constructors.Count == 0)
                return $"Class '{rc.Name}' has no constructor."; // sollte nie vorkommen, immer mind. 1 synthetisiert
            var arities = rc.Constructors.Keys.OrderBy(x => x);
            return $"Class '{rc.Name}' has no constructor with {argCount} argument(s) " +
                   $"(available: {string.Join(", ", arities)} argument(s)).";
        }

        private static ValueKind TagToKind(TypeTag tag) => tag switch
        {
            TypeTag.Bool => ValueKind.Bool,
            TypeTag.Int => ValueKind.Int,
            TypeTag.Float => ValueKind.Float,
            TypeTag.Char => ValueKind.Char,
            TypeTag.String => ValueKind.String,
            _ => throw new InvalidOperationException($"Unknown TypeTag {tag}"),
        };

        /// <summary>Verpackt einen 'on'-Zielwert für LambdaValue.OnTarget. Bei
        /// einer Objektreferenz die ObjectInstance direkt, sonst den Value als
        /// object geboxt.</summary>
        private static object BoxValueForOnTarget(Value v) =>
            v.Kind == ValueKind.Class ? v.AsObjectRef() : v;

        // -----------------------------------------------------------
        // Stack- & Code-Zugriff
        // -----------------------------------------------------------
        private void Push(Value v)
        {
            if (_sp == _stack.Length) Array.Resize(ref _stack, _stack.Length * 2);
            _stack[_sp++] = v;
        }

        private Value Pop() => _stack[--_sp];

        private Value Peek() => _stack[_sp - 1];

        private byte ReadByte() => _code[_ip++];

        private int ReadU16()
        {
            var code = _code;
            int ip = _ip;
            _ip = ip + 2;
            return code[ip] | (code[ip + 1] << 8);
        }
    }
}
