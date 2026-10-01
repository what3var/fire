using System;
using System.Collections.Generic;
using System.Threading;
using fire.Bytecode;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>Handle auf einen laufenden/beendeten Fire-Thread. Da `fire`
    /// keine Rückgabewerte kennt (docs/THREADING_DESIGN.md Abschnitt 1),
    /// dient dieses Handle in der Sprache selbst später zu nichts (kein
    /// Join-Sprachkonstrukt) - hier, in dieser Ausbaustufe ohne
    /// Sprachsyntax, ist `Join`/`Error` das Werkzeug für TESTS, um auf das
    /// Ende zu warten und Fehler sichtbar zu machen, statt sie sonst still
    /// im Thread verschwinden zu lassen.</summary>
    public sealed class FireThreadHandle
    {
        private readonly Thread _thread;

        /// <summary>Eine im Thread-Delegate unbehandelt durchgeschlagene
        /// Exception - in der späteren Sprachanbindung entspricht das genau
        /// dem Fall "unbehandelte Nutzer-Exception in einem Fire-Thread"
        /// (docs/THREADING_DESIGN.md Abschnitt 6.2), hier noch ohne die
        /// eigentliche `catch threads(...)`-Zustellung (die braucht die
        /// Sprachanbindung/kooperative Prüfpunkte, siehe dortige Doku) -
        /// bewusst nur als Test-Sichtbarkeit vorgehalten.</summary>
        public Exception? Error { get; private set; }

        internal FireThreadHandle(Thread thread) => _thread = thread;

        public void Join() => _thread.Join();

        internal void SetError(Exception ex) => Error = ex;
    }

    /// <summary>
    /// Spawnt einen ECHTEN Thread für `fire` (docs/THREADING_DESIGN.md
    /// Abschnitt 1). Unveränderliche, bereits kompilierte Daten (Chunk,
    /// RuntimeClass-Definitionen, NativeRegistry, ...) sind sicher zwischen
    /// beliebig vielen VM-Instanzen/Threads teilbar (werden nach dem
    /// Kompilieren nie mehr verändert) - alles Laufzeit-Veränderliche
    /// (Frames, aktueller Scope, IP) bekommt jeder Thread über eine EIGENE
    /// VM-Instanz (siehe VM-Konstruktor, der genau dafür schon alle nötigen
    /// Teile als Parameter statt als Singleton/statischen Zustand nimmt).
    ///
    /// Nimmt aktuell eine reine C#-`Action` als Thread-Körper entgegen (statt
    /// z.B. eines FunctionProto + Argumente) - das ist eine bewusste
    /// Ausbaustufen-Entscheidung: diese Klasse ist für den direkten Test der
    /// Architektur über die C#-API gedacht (siehe Programm.cs-Tests), die
    /// eigentliche `fire { ... }`-Sprachsyntax (Parser/Compiler/VM-Opcode)
    /// ist noch nicht angebunden.
    /// </summary>
    public static class FireRuntime
    {
        /// <summary>Optionaler Hook für Werkzeuge außerhalb der Sprache
        /// selbst (aktuell: der Editor-Debugger, siehe fire.Editor.
        /// DebugThreadContext) - wird, wenn gesetzt, für JEDE neu erzeugte
        /// Fire-Thread-VM-Instanz aufgerufen, ANSTATT sie sofort frei laufen
        /// zu lassen. Bekommt die fertig aufgebaute VM-Instanz sowie einen
        /// Delegaten, der "normal, ungebremst laufen lassen" bedeutet
        /// (`vm.Run()`) - der Hook MUSS diesen Delegaten irgendwann
        /// aufrufen (direkt, verzögert, schrittweise über VM.StepInstruction
        /// etc. - völlig dem Hook überlassen), sonst bleibt der Thread für
        /// immer ohne sichtbare Wirkung stehen. Ist KEIN Hook registriert
        /// (der Normalfall, z.B. bei allen Program.cs-Tests), verhält sich
        /// alles exakt wie vorher (der Delegat wird direkt aufgerufen).
        ///
        /// Bewusst ein einfacher, globaler (statischer) Hook statt z.B.
        /// einer Instanz-Eigenschaft auf FireRuntime - FireRuntime kennt
        /// (und soll auch weiterhin nichts kennen) über "Editor"/"Debugger"
        /// als Konzept; das bleibt vollständig auf der aufrufenden Seite.</summary>
        public static Action<VM, Action>? ThreadBodyInterceptor { get; set; }

        private static void RunVm(VM vm)
        {
            var interceptor = ThreadBodyInterceptor;
            if (interceptor != null)
                interceptor(vm, vm.Run);
            else
                vm.Run();
        }

        // Lebende Fire-Threads - das Hauptprogramm wartet an seinem Ende auf sie, bevor es seinen globalen Scope freigibt
        // (siehe VM.FinishProgram): ein Thread, der per `sync` in Objekte des Hauptprogramms zurückschreibt, darf sie nicht
        // schon zerstört vorfinden.
        private static int _liveThreads;
        private static readonly object _liveThreadsGate = new();

        /// <summary>Blockiert, bis alle Fire-Threads beendet sind (sofort, wenn keiner läuft). Mit `broker` (das Hauptprogramm, dem die
        /// Globals gehören) bedient es dabei die Warteschlange der Threads - sie warten ja auf ein `sync globals` -, sonst würde das Programmende
        /// an einem Thread hängen, der gerade eine Sektion angemeldet hat.</summary>
        public static void WaitForAllFireThreads(GlobalsBroker? broker = null)
        {
            while (true)
            {
                broker?.Drain();
                lock (_liveThreadsGate)
                {
                    bool pending = broker != null && broker.HasPending;
                    if (_liveThreads == 0 && !pending) return;
                    if (!pending) Monitor.Wait(_liveThreadsGate, 50);
                }
            }
        }

        /// <summary>Weckt ein Hauptprogramm, das in <see cref="WaitForAllFireThreads"/> wartet (ein Thread hat etwas in die Warteschlange gestellt).</summary>
        internal static void WakeWaitingOwner()
        {
            lock (_liveThreadsGate) Monitor.PulseAll(_liveThreadsGate);
        }

        /// <summary>Wartet höchstens `milliseconds`, wacht aber früher auf, wenn etwas in die Warteschlange des Hauptprogramms gestellt wird
        /// (siehe <see cref="WakeWaitingOwner"/>) - für `Sleep`.</summary>
        internal static void WaitForWake(int milliseconds)
        {
            lock (_liveThreadsGate) Monitor.Wait(_liveThreadsGate, milliseconds);
        }

        public static FireThreadHandle Fire(Action body)
        {
            FireThreadHandle? handle = null;
            lock (_liveThreadsGate) _liveThreads++;
            var thread = new Thread(() =>
            {
                try
                {
                    body();
                }
                catch (Exception ex)
                {
                    handle!.SetError(ex);
                }
                finally
                {
                    lock (_liveThreadsGate)
                    {
                        _liveThreads--;
                        Monitor.PulseAll(_liveThreadsGate);
                    }
                }
            })
            {
                // Bewusst ein Vordergrund-Thread (der .NET-Standard ohnehin,
                // hier nur explizit dokumentiert): `fire` kennt kein Join,
                // aber laufende Arbeit soll beim normalen Prozessende nicht
                // stillschweigend abgebrochen werden - das deckt sich mit dem
                // synchronisierten Shutdown-Gedanken aus THREADING_DESIGN.md
                // Abschnitt 6.3 (erst räumen alle auf, dann endet der Prozess).
                IsBackground = false,
            };
            handle = new FireThreadHandle(thread);
            thread.Start();
            return handle;
        }

        /// <summary>Wie Fire(Action), aber führt ECHTEN, kompilierten Bytecode
        /// auf einer FRISCHEN, eigenen VM-Instanz aus - der eigentliche
        /// Vorgeschmack auf ein späteres `fire { ... }`. `natives`/`classes`
        /// sind unveränderliche, bereits kompilierte Daten und werden direkt
        /// weitergereicht (sicher geteilt, siehe Klassenkommentar); der
        /// globale Scope wird für DIESEN Thread neu angelegt (kein Shared
        /// Memory). Werte von "außen" (z.B. eine `taking`-Kopie) werden
        /// aktuell noch NICHT über echte Sprachsyntax gebunden, sondern über
        /// eine gewöhnliche, in `natives` registrierte native Funktion
        /// hereingereicht (siehe Program.cs-Test) - das braucht keine
        /// Parser-/Resolver-Änderung, weil native Funktionen ohnehin schon
        /// der vorgesehene Weg sind, wie Bytecode mit der "Außenwelt"
        /// spricht (siehe NativeRegistry-Klassenkommentar).</summary>
        public static FireThreadHandle FireVm(
            Chunk chunk,
            NativeRegistry natives,
            IReadOnlyDictionary<string, RuntimeClass>? classes = null,
            VmExecutionMode executionMode = VmExecutionMode.Debug)
        {
            return Fire(() =>
            {
                var scope = new Scope(null, isGlobal: true);
                var vm = new VM(chunk, scope, natives, classes, isFireThreadVm: true, executionMode: executionMode);
                RunVm(vm);
            });
        }

        /// <summary>Wie FireVm, aber mit `taking`/`with`-Bindungen und der Verbindung zu den Globals des Hauptprogramms (siehe
        /// docs/THREADING_DESIGN.md Abschnitt 7, GlobalsBroker):
        ///
        /// `broker`/`sharedGlobalCount`: die Globals des Hauptprogramms (Slots 0..sharedGlobalCount-1) werden NICHT kopiert - der Thread liest sie
        /// direkt und ändert sie in Sektionen (siehe VM.AttachToGlobals). Der private globale Scope des Threads hält dafür nur Platzhalter, damit die
        /// Slots der Erfassungen dahinter an den Stellen liegen, die Resolving.Resolver.ResolveFireStmt/Compiler.CompileFireStmt vergeben haben.
        ///
        /// `takingValues`: direkt im Anschluss an die Platzhalter gebunden. Ein Objekt (ValueKind.Class) wird als isolierte Tiefenkopie gebunden
        /// (Runtime.ObjectCopier.Take - das Kopieren selbst aktiviert das Thread-Sharing auf dem Original), sonst direkt (Value ist ein unveränderlicher
        /// struct).
        ///
        /// `withValue` (eine Actor-Referenz) wird dagegen IMMER DIREKT weitergegeben, OHNE Kopie, als letzter Slot - ein Actor verwaltet seine eigene
        /// Thread-Sicherheit über seine Mailbox (siehe Runtime.ActorMailbox), nicht über das taking/sync-Ownership-Modell.</summary>
        public static FireThreadHandle FireVmTaking(
            Chunk chunk,
            NativeRegistry natives,
            IReadOnlyDictionary<string, RuntimeClass>? classes,
            GlobalsBroker? broker,
            int sharedGlobalCount,
            IReadOnlyList<Value> takingValues,
            Value? withValue = null,
            VmExecutionMode executionMode = VmExecutionMode.Debug)
        {
            return Fire(() =>
            {
                // Die Slots 0..sharedGlobalCount-1 sind die echten Globals des Hauptprogramms (die VM liest/schreibt sie über den Broker,
                // siehe GlobalsBroker/VM.AttachToGlobals) - hier nur Platzhalter, damit taking/with an den vom Compiler vergebenen Slots liegen.
                var scope = new Scope(null, isGlobal: true);
                for (int i = 0; i < sharedGlobalCount; i++) scope.DefineSlot(Value.MakeUndefined());
                foreach (var tv in takingValues) DefineSnapshotSlot(scope, tv);
                if (withValue is Value wv)
                    scope.DefineSlot(wv);
                var vm = new VM(chunk, scope, natives, classes, isFireThreadVm: true, executionMode: executionMode);
                if (broker != null) vm.AttachToGlobals(broker, sharedGlobalCount);
                try { RunVm(vm); }
                finally { vm.ReleaseGlobalsSections(); }
            });
        }

        /// <summary>Ruft eine registrierte Callback-Lambda SYNCHRON auf dem
        /// AUFRUFENDEN (nativen) Thread auf, auf einer FRISCHEN, eigenen
        /// VM-Instanz (VM.CallLambdaEntry) - anders als Fire/FireVm wird
        /// HIER selbst KEIN neuer Thread gestartet: der native Host-Code hat
        /// den Aufruf-Zeitpunkt/-Thread bereits selbst bestimmt (z.B. ein
        /// .NET-Threadpool-Thread bei SerialPort.DataReceived), diese
        /// Methode reiht sich nur ein.
        ///
        /// `globalSnapshot` MUSS - wie bei FireVmTaking - bereits VOR diesem
        /// Aufruf SYNCHRON auf einem "sicheren" Thread gelesen worden sein
        /// (kein lazy Zugriff auf die lebendige Scope-Instanz des
        /// Hauptprogramms von hier aus, echter Daten-Wettlauf sonst) - der
        /// Callback bekommt daraus einen NEUEN, eigenen globalen Scope
        /// (Objekte als isolierte Tiefenkopie, siehe DefineSnapshotSlot),
        /// NIE den echten globalen Scope des Hauptprogramms. Das deckt sich
        /// exakt mit SPEC 4.2: eine Lambda sieht ohnehin nur ihren eigenen
        /// Scope plus den globalen, nie umgebende Locals - hier ist "der
        /// globale Scope" eben dieser Snapshot statt des Originals.
        ///
        /// Wirft NICHTS bei einer unbehandelten Skript-Exception im
        /// Callback-Body selbst weiter (die würde sonst unkontrolliert in
        /// fremden, nativen Aufrufer-Code durchschlagen) - stattdessen wird
        /// sie an `onUnhandled` gemeldet (falls gesetzt) und `undefined`
        /// geliefert.</summary>
        public static Value CallCallback(
            LambdaValue callback,
            Value[] args,
            NativeRegistry natives,
            IReadOnlyDictionary<string, RuntimeClass>? classes,
            IReadOnlyList<Value> globalSnapshot,
            Action<Exception>? onUnhandled = null,
            VmExecutionMode executionMode = VmExecutionMode.Debug)
        {
            var scope = new Scope(null, isGlobal: true);
            foreach (var v in globalSnapshot) DefineSnapshotSlot(scope, v);

            var vm = new VM(callback.Proto.Chunk, scope, natives, classes, isFireThreadVm: true, executionMode: executionMode);
            try
            {
                return vm.CallLambdaEntry(callback, args);
            }
            catch (Exception ex)
            {
                onUnhandled?.Invoke(ex);
                return Value.MakeUndefined();
            }
        }

        /// <summary>Führt das Lambda eines nativen Callbacks aus (z.B. ein Fenster-Ereignis) - die eine Stelle, die entscheidet, WO:
        ///
        /// - Auf dem Thread einer laufenden VM (der Normalfall: das Skript selbst ruft z.B. `Window.Tick`, und dabei feuern die
        ///   Ereignisse) läuft es VERSCHACHTELT auf dieser VM (<see cref="VM.CallLambdaInline"/>): mit den echten globalen Variablen,
        ///   lesend und schreibend, ohne Kopie. Es gibt keine nebenläufige Ausführung, also nichts zu isolieren.
        /// - Auf einem Thread ohne laufende VM (ein Host-Thread, z.B. ein Seriell-Ereignis) wäre Zugriff auf die Globals ein Datenrennen:
        ///   dort wird es der Besitzer-VM (`owner`, das Hauptprogramm) eingereiht und von ihr an einem sicheren Punkt ausgeführt
        ///   (`sync globals` oder - ohne `#nosync` - automatisch, siehe <see cref="VM.PostCallback"/>). Läuft das Hauptprogramm nicht (mehr)
        ///   oder gibt es keinen Besitzer, läuft es wie <see cref="CallCallback"/> auf einer isolierten Kopie (`snapshotGlobals` liefert sie).
        ///
        /// Eine unbehandelte Exception im Callback geht nie an den Aufrufer, sondern als Text an `onUnhandled`.</summary>
        public static void RunCallback(
            LambdaValue callback,
            Value[] args,
            NativeRegistry natives,
            IReadOnlyDictionary<string, RuntimeClass>? classes,
            Func<IReadOnlyList<Value>> snapshotGlobals,
            Action<string>? onUnhandled = null,
            VmExecutionMode executionMode = VmExecutionMode.Debug,
            VM? owner = null)
        {
            var vm = VM.CurrentThreadVm;
            if (vm != null && !vm.IsHalted)
            {
                try
                {
                    var error = vm.CallLambdaInline(callback, args);
                    if (error != null) onUnhandled?.Invoke(new UncaughtScriptException(error).Message);
                }
                catch (Exception ex)
                {
                    onUnhandled?.Invoke(ex.Message);
                }
                return;
            }

            if (owner != null && owner.PostCallback(callback, args, onUnhandled)) return;

            CallCallback(callback, args, natives, classes, snapshotGlobals(), ex => onUnhandled?.Invoke(ex.Message), executionMode);
        }

        /// <summary>Gemeinsame Bindungslogik für sowohl den Globals-Snapshot
        /// als auch taking-Erfassungen (siehe FireVmTaking-Doku) - bei einem
        /// Objekt eine isolierte Tiefenkopie, sonst der Wert direkt.</summary>
        private static void DefineSnapshotSlot(Scope scope, Value v)
        {
            if (v.Kind == ValueKind.Class)
            {
                var source = (ObjectInstance)v.AsObjectRef();
                var copy = ObjectCopier.Take(source, scope);
                scope.DefineSlot(Value.MakeClassRef(copy));
            }
            else
            {
                scope.DefineSlot(v);
            }
        }
    }
}
