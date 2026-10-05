using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using fire.Bytecode;
using fire.Device.Manager.DeviceManager;
using fire.Device.Manager.Drivers;
using fire.Values;

namespace fire.Device.Bridge
{
    /// <summary>
    /// Die Brücke zwischen fire und dem KobraMill-Gerätekommunikations-
    /// Framework (siehe DeviceManager/Drivers) - registriert DeviceManager
    /// als native Funktionen (über NativeRegistry.RegisterGroup) und liefert
    /// dazu passenden fire-Quelltext (<see cref="PreludeSource"/>), der ein
    /// Gerät hinter einer gewöhnlichen 'Device'-Klasse versteckt, ansprechbar
    /// per Handle ODER Identifier (über 'DeviceManagerFacade.GetByHandle'/
    /// 'GetByIdentifier' - siehe unten, warum das ZWEI Methoden statt zwei
    /// überladener Konstruktoren sind).
    ///
    /// Empfangene Rohdaten (IDevice.OnRawDataReceived) laufen NICHT als
    /// Callback direkt in die VM hinein - IDevice ruft diesen Callback vom
    /// EIGENEN Hintergrund-Thread des Geräts aus auf (siehe SerialDevice.
    /// PollyPocket), ein Aufruf mitten in eine laufende VM hinein wäre von
    /// dort aus nicht sicher synchronisierbar. Stattdessen sammelt diese
    /// Brücke jedes empfangene Byte-Paket PRO Gerät in einer eigenen,
    /// nebenläufigkeitssicheren Empfangspuffer (siehe <see cref="ReceiveBuffer"/>) - fire-
    /// Code fragt aktiv ab (Device.HasData()/ReadString()/Read()) oder wartet
    /// auf bestimmte Zeichen (WaitForString/WaitFor), aus einem
    /// eigenen 'fire { }'-Hintergrund-Thread heraus, falls gewünscht.
    /// </summary>
    public static class DeviceBridge
    {
        public const string ManagerPrefix = "__DEVMgr";
        public const string DevicePrefix = "__DEV";

        /// <summary>Ungültiges/nicht gefundenes Handle - siehe
        /// Terminal.Bridge.GraphicsBridge.InvalidHandle für dieselbe
        /// Konvention (dort ausführlicher begründet). PreludeSource prüft
        /// darauf und wirft dafür eine echte, per `try`/`catch` fangbare
        /// DeviceNotFoundException.</summary>
        public const int InvalidHandle = -1;

        public static void RegisterStubs(NativeRegistry natives)
        {
            natives.RegisterGroup(ManagerPrefix, BuildManagerStubs());
            natives.RegisterGroup(DevicePrefix, BuildDeviceStubs());
        }

        /// <summary>Registriert die Geräte-Funktionen für EINEN Programmlauf. Das Ergebnis räumt nach dem Lauf auf: es löst
        /// die Empfangs-Haken von den Geräten (ein geteilter Manager überlebt den Lauf, die Haken dürfen es nicht) und
        /// gibt einen NICHT geteilten Manager frei (trennt die Geräte). Ein geteilter Manager bleibt unberührt -
        /// ein Skript kann ihn und seine Geräte nicht zerstören.</summary>
        /// <summary>Wartet, bis die Bedingung wahr wird, höchstens `timeout` (undefined = die Standard-Wartezeit des Programms); true, wenn sie wahr wurde.
        /// Der Host liefert hier ein Warten, das `leave`/`terminate` des Programms beachtet (siehe VM.WaitUntil); ohne Angabe wird einfach gepollt
        /// (Zeitangaben dann nur als Zahl in Millisekunden). Wirft ArgumentException bei einer ungültigen Zeitangabe.</summary>
        public delegate bool WaitUntilFunction(Func<bool> condition, Value timeout);

        private static bool DefaultWaitUntil(Func<bool> condition, Value timeout)
        {
            long milliseconds = 30_000;
            if (timeout.Kind is ValueKind.Int or ValueKind.Float) milliseconds = (long)(timeout.Kind == ValueKind.Int ? timeout.AsInt() : timeout.AsFloat());
            else if (timeout.Kind != ValueKind.Undefined) throw new ArgumentException("Invalid wait time: expected milliseconds.");
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            while (!condition())
            {
                if (stopwatch.ElapsedMilliseconds >= milliseconds) return false;
                System.Threading.Thread.Sleep(2);
            }
            return true;
        }

        public static IDisposable RegisterAll(NativeRegistry natives, DeviceManager manager, WaitUntilFunction? waitUntil = null)
        {
            waitUntil ??= DefaultWaitUntil;
            // Pro Geräte-Handle ein eigener Empfangspuffer (siehe ReceiveBuffer) - und
            // welche Handles schon "angeschlossen" sind (OnRawDataReceived gehookt), damit ein
            // Gerät nicht bei jedem einzelnen nativen Aufruf erneut gehookt
            // wird (das würde denselben Callback mehrfach registrieren und
            // jedes empfangene Paket ebenso oft vervielfachen). Beides
            // nebenläufigkeitssicher (ConcurrentDictionary statt Dictionary/
            // HashSet) - native Funktionen können auch aus MEHREREN
            // gleichzeitig laufenden 'fire {}'-Threads heraus aufgerufen
            // werden, nicht nur vom Hauptthread.
            var receiveQueues = new ConcurrentDictionary<int, ReceiveBuffer>();
            var hookedHandles = new ConcurrentDictionary<int, byte>(); // Wert ungenutzt - dient nur als nebenläufigkeitssicheres Set
            var hooks = new ConcurrentBag<(IDevice Device, Action<byte[]> Handler)>();

            IDevice? ResolveDevice(int handle)
            {
                var device = manager.GetDeviceByHandle(handle);
                if (device == null) return null;

                if (hookedHandles.TryAdd(handle, 0))
                {
                    var queue = new ReceiveBuffer();
                    receiveQueues[handle] = queue;
                    // WICHTIG: läuft auf dem Hintergrund-Thread DES GERÄTS
                    // (siehe SerialDevice.PollyPocket), NICHT auf dem
                    // VM-Thread - deshalb hier nur ein simples, threadsicheres
                    // Anhängen, KEIN Zugriff auf irgendetwas VM-Seitiges.
                    Action<byte[]> handler = bytes => queue.Add(bytes);
                    device.OnRawDataReceived += handler;
                    hooks.Add((device, handler));
                }
                return device;
            }

            natives.RegisterGroup(ManagerPrefix, BuildManagerFunctions(manager));
            natives.RegisterGroup(DevicePrefix, BuildDeviceFunctions(manager, ResolveDevice, receiveQueues, waitUntil));

            return new Cleanup(manager, hooks);
        }

        private sealed class Cleanup : IDisposable
        {
            private readonly DeviceManager _manager;
            private readonly ConcurrentBag<(IDevice Device, Action<byte[]> Handler)> _hooks;

            public Cleanup(DeviceManager manager, ConcurrentBag<(IDevice, Action<byte[]>)> hooks)
            {
                _manager = manager;
                _hooks = hooks;
            }

            public void Dispose()
            {
                foreach (var (device, handler) in _hooks) device.OnRawDataReceived -= handler;
                while (_hooks.TryTake(out _)) { }
                _manager.Dispose(); // wirkungslos bei einem geteilten Manager
            }
        }

        private static Dictionary<string, NativeFunction> BuildManagerStubs()
        {
            return new Dictionary<string, NativeFunction>
            {
                ["Refresh"] = args => Value.MakeUndefined(),
                ["HandleForIdentifier"] = args => Value.MakeUndefined(),
                ["Count"] = args => Value.MakeUndefined(),
                ["HandleAt"] = args => Value.MakeUndefined(),
                ["IsShared"] = args => Value.MakeUndefined(),
                ["DefaultHandle"] = args => Value.MakeUndefined(),
            };
        }

        private static Dictionary<string, NativeFunction> BuildManagerFunctions(DeviceManager manager)
        {
            return new Dictionary<string, NativeFunction>
            {
                ["Refresh"] = args =>
                {
                    manager.RefreshDevices(args[0].AsBool());
                    return Value.MakeUndefined();
                },
                ["HandleForIdentifier"] = args =>
                    Value.MakeInt(manager.GetHandleByIdentifier(args[0].AsString()) ?? InvalidHandle),
                ["Count"] = args => Value.MakeInt(manager.DeviceCount),
                ["HandleAt"] = args =>
                {
                    var handles = manager.GetAllDeviceHandles();
                    long index = args[0].AsInt();
                    return Value.MakeInt(index >= 0 && index < handles.Count ? handles[(int)index] : InvalidHandle);
                },
                // WICHTIG: dieselbe REIHENFOLGE wie in BuildManagerStubs - der Compiler legt Funktionen nach Position fest.
                ["IsShared"] = args => Value.MakeBool(manager.IsShared),
                ["DefaultHandle"] = args => Value.MakeInt(manager.DefaultHandle ?? InvalidHandle),
            };
        }

        private static Dictionary<string, NativeFunction> BuildDeviceStubs()
        {
            return new Dictionary<string, NativeFunction>
            {
                ["Identifier"] = args => Value.MakeUndefined(),
                ["IsShared"] = args => Value.MakeUndefined(),
                ["IsConnected"] = args => Value.MakeUndefined(),
                ["PortName"] = args => Value.MakeUndefined(),
                ["Availability"] = args => Value.MakeUndefined(),
                ["TestAvailability"] = args => Value.MakeUndefined(),
                ["Connect"] = args => Value.MakeUndefined(),
                ["Disconnect"] = args => Value.MakeUndefined(),
                ["DoCommand"] = args => Value.MakeUndefined(),
                ["HasData"] = args => Value.MakeUndefined(),
                ["ReadString"] = args => Value.MakeUndefined(),
                // WICHTIG: neue Funktionen immer ANS ENDE, in BuildDeviceFunctions in derselben Reihenfolge (Index = Position).
                ["Read"] = args => Value.MakeUndefined(),
                ["WriteString"] = args => Value.MakeUndefined(),
                ["Write"] = args => Value.MakeUndefined(),
                ["WaitForString"] = args => Value.MakeUndefined(),
                ["WaitFor"] = args => Value.MakeUndefined(),
            };
        }

        private static Dictionary<string, NativeFunction> BuildDeviceFunctions(
            DeviceManager manager, System.Func<int, IDevice?> resolve, ConcurrentDictionary<int, ReceiveBuffer> receiveQueues, WaitUntilFunction waitUntil)
        {
            return new Dictionary<string, NativeFunction>
            {
                ["Identifier"] = args =>
                    Value.MakeString(manager.GetIdentifierByHandle((int)args[0].AsInt()) ?? ""),
                ["IsShared"] = args => Value.MakeBool(manager.GetSlotByIdentifier(manager.GetIdentifierByHandle((int)args[0].AsInt()) ?? "")?.IsShared ?? false),
                ["IsConnected"] = args => Value.MakeBool(resolve((int)args[0].AsInt())?.IsConnected ?? false),
                ["PortName"] = args => Value.MakeString(resolve((int)args[0].AsInt())?.PortName ?? ""),
                ["Availability"] = args => Value.MakeInt((int)(resolve((int)args[0].AsInt())?.Availability ?? DeviceAvailability.Unavailable)),
                ["TestAvailability"] = args =>
                {
                    var device = resolve((int)args[0].AsInt());
                    return Value.MakeInt(device != null ? (int)device.TestAvailability() : (int)DeviceAvailability.Unavailable);
                },
                ["Connect"] = args =>
                {
                    var device = resolve((int)args[0].AsInt());
                    if (device == null) return Value.MakeBool(false);
                    try { device.Connect(); return Value.MakeBool(true); }
                    catch { return Value.MakeBool(false); }
                },
                ["Disconnect"] = args =>
                {
                    try { resolve((int)args[0].AsInt())?.Disconnect(); }
                    catch { /* absichtlich verschluckt - Trennen soll nie fehlschlagen können */ }
                    return Value.MakeUndefined();
                },
                // Eine Zeile senden (mit Zeilenende, in der Kodierung des Treibers); ob ein Befehlsobjekt oder ein Text ankommt, entscheidet die Prelude.
                ["DoCommand"] = args =>
                {
                    var device = resolve((int)args[0].AsInt());
                    if (device == null) return Value.MakeBool(false);
                    try { return Value.MakeBool(device.SendCommand(args[1].AsString())); }
                    catch { return Value.MakeBool(false); }
                },
                ["HasData"] = args =>
                {
                    resolve((int)args[0].AsInt()); // stellt sicher, dass der Puffer existiert (siehe RegisterAll)
                    return Value.MakeBool(receiveQueues.TryGetValue((int)args[0].AsInt(), out var q) && q.HasData);
                },
                ["ReadString"] = args =>
                {
                    // Latin1 (ISO-8859-1) statt UTF-8: bildet JEDEN Byte-Wert
                    // 0..255 verlustfrei auf GENAU ein Zeichen ab - anders als
                    // UTF-8 (das Mehrbyte-Folgen für alles über 127 erwartet
                    // und bei zufälligen Binärdaten leicht ungültige Folgen
                    // erzeugt) verträgt sich das sowohl mit reinem Text
                    // (der übliche Fall bei einem zeilenbasierten Protokoll)
                    // als auch mit rohen Binärdaten - fire-Code kann bei
                    // Bedarf mit `str[i]` byteweise zurückrechnen.
                    resolve((int)args[0].AsInt());
                    var bytes = receiveQueues.TryGetValue((int)args[0].AsInt(), out var q) ? q.TakePacket() : null;
                    return Value.MakeString(bytes == null ? "" : System.Text.Encoding.Latin1.GetString(bytes));
                },
                // WICHTIG: dieselbe REIHENFOLGE wie in BuildDeviceStubs - der Compiler legt Funktionen nach Position fest.
                ["Read"] = args =>
                {
                    resolve((int)args[0].AsInt());
                    var bytes = receiveQueues.TryGetValue((int)args[0].AsInt(), out var q) ? q.TakePacket() : null;
                    return Value.MakeBuffer(new ByteBuffer(bytes ?? Array.Empty<byte>(), ByteConversions.HostByteOrder));
                },
                // Wie ReadString/WaitForString: ein Zeichen = ein Byte (Latin1); für UTF-8-Text `text.ToBytes()` und Write
                ["WriteString"] = args =>
                {
                    var device = resolve((int)args[0].AsInt());
                    if (device == null) return Value.MakeBool(false);
                    try { return Value.MakeBool(device.Write(System.Text.Encoding.Latin1.GetBytes(args[1].AsString()))); }
                    catch { return Value.MakeBool(false); }
                },
                ["Write"] = args =>
                {
                    var device = resolve((int)args[0].AsInt());
                    if (device == null) return Value.MakeBool(false);
                    try { return Value.MakeBool(device.Write((byte[])args[1].AsBuffer().Bytes.Clone())); }
                    catch { return Value.MakeBool(false); }
                },
                // 1 = gefunden (der Puffer ist dahinter abgeschnitten), 0 = Zeit abgelaufen / Gerät getrennt / Programm beendet, -1 = ungültige Wartezeit
                ["WaitForString"] = args => WaitFor((int)args[0].AsInt(), System.Text.Encoding.Latin1.GetBytes(args[1].AsString()), args[2]),
                ["WaitFor"] = args => WaitFor((int)args[0].AsInt(), args[1].AsBuffer().Bytes, args[2]),
            };

            Value WaitFor(int handle, byte[] pattern, Value timeout)
            {
                var device = resolve(handle);
                if (device == null || !receiveQueues.TryGetValue(handle, out var buffer)) return Value.MakeInt(0);
                bool found = false;
                try
                {
                    // Ein getrenntes Gerät liefert nichts mehr: nach einem letzten Blick in den Puffer endet das Warten sofort.
                    waitUntil(() =>
                    {
                        if (buffer.TryConsumeThrough(pattern)) { found = true; return true; }
                        return !device.IsConnected;
                    }, timeout);
                }
                catch (ArgumentException) { return Value.MakeInt(-1); }
                return Value.MakeInt(found ? 1 : 0);
            }
        }

        /// <summary>fire-Quelltext, der die per <see cref="RegisterAll"/>
        /// registrierten nativen Funktionen hinter gewöhnlichen Klassen
        /// versteckt (analog zu Terminal.Bridge.GraphicsBridge.PreludeSource)
        /// - VOR das eigentliche Nutzer-Skript zu setzen.
        ///
        /// 'Device' selbst hat nur EINEN Konstruktor (per Handle) - ein
        /// zweiter, gleich-1-argumentiger Konstruktor für den Identifier
        /// wäre nicht überladbar (Konstruktor-Überladung löst hier nur über
        /// die ARGUMENTANZAHL auf, nicht über den Typ - zwei 1-Parameter-
        /// Konstruktoren wären also nicht unterscheidbar). Stattdessen zwei
        /// FABRIKMETHODEN auf 'DeviceManagerFacade' (GetByHandle/
        /// GetByIdentifier, siehe SPEC-Anforderung "per Handle oder
        /// Identifier ansprechbar") - beide liefern ganz normale
        /// Device-Instanzen zurück.</summary>
        public const string PreludeSource = """
            class DeviceNotFoundException : Exception {
                string message

                construct(string message) {
                    this.message = message
                }
            }

            class DeviceConnectionException : Exception {
                string message

                construct(string message) {
                    this.message = message
                }
            }

            // Eine ungültige Angabe an einer Geräte-Funktion (z.B. eine Wartezeit, die keine Zeitangabe ist).
            class DeviceArgumentException : Exception {
                string message

                construct(string message) {
                    this.message = message
                }
            }

            // Was ein Gerät auf Befehlsebene kann: Befehle (Text oder Command-Objekte) senden, Bytes und Text schreiben und lesen, auf Zeichen warten.
            // `Command<IDevice>` ist ein Befehl, den DoCommand mit dem Gerät als Kontext ausführt.
            interface IDevice {
                string Identifier()
                bool Connect()
                Disconnect()
                DoCommand(command)
                DoCommands(commands)
                bool HasData()
                string ReadString()
                Read()
                bool WriteString(string text)
                bool Write(data)
                bool WaitForString(string text, timeout)
                bool WaitFor(data, timeout)
            }

            class Device : IDevice {
                int handle

                construct(int handle) {
                    this.handle = handle
                }

                // Ob der Host (z.B. der Editor) ein Standardgerät gewählt hat.
                static bool HasDefault {
                    get { return __DEVMgrDefaultHandle() != -1 }
                }

                // Das vom Host gewählte Standardgerät (im Editor: Geräte-Übersicht -> "Als Standard" oder die Auswahl in
                // der Symbolleiste). Wirft DeviceNotFoundException, wenn keins gewählt ist (z.B. in einem eigenständigen Programm).
                static Device Default {
                    get {
                        var h = __DEVMgrDefaultHandle()
                        if (h == -1) {
                            throw new DeviceNotFoundException("No default device selected")
                        }
                        return new Device(h)
                    }
                }

                string Identifier() { return __DEVIdentifier(this.handle) }

                // Verbunden? (Property; die gleichnamige Methode IsConnected() bleibt aus Kompatibilität bestehen.)
                bool IsConnected { get { return __DEVIsConnected(this.handle) } }
                bool IsConnected() { return __DEVIsConnected(this.handle) }

                // Gehört das Gerät einem geteilten DeviceManager (des Editors)? Dann bleibt es über den Lauf hinaus bestehen.
                bool IsShared { get { return __DEVIsShared(this.handle) } }
                string PortName() { return __DEVPortName(this.handle) }

                // 0 = Unavailable, 1 = Unchecked, 2 = Available (siehe
                // Drivers.DeviceAvailability - dieselben Zahlenwerte).
                int Availability() { return __DEVAvailability(this.handle) }
                int TestAvailability() { return __DEVTestAvailability(this.handle) }

                bool Connect() { return __DEVConnect(this.handle) }

                // Verbindet nur, wenn noch nicht verbunden; wirft DeviceConnectionException, wenn das nicht klappt.
                // Liefert das Gerät selbst zurück (Device.Default.EnsureConnected().DoCommand("...")).
                Device EnsureConnected() {
                    if (!__DEVIsConnected(this.handle)) {
                        if (!__DEVConnect(this.handle)) {
                            throw new DeviceConnectionException("Connection to '" + __DEVIdentifier(this.handle) + "' failed")
                        }
                    }
                    return this
                }
                Disconnect() { __DEVDisconnect(this.handle) }

                /// <summary>Sends a command. A text goes out as a line (with a line ending, in the device's encoding). A command object
                /// (`Command<IDevice>`, derived from it, or any object with `Execute(device)`) is executed with the device as its context.</summary>
                /// <param name="command">The text to send, or the command object to execute.</param>
                /// <returns>`false` if the device is not connected (or `Execute` returned `false`), otherwise `true`.</returns>
                bool DoCommand(command) {
                    if (command is of string) {
                        return __DEVDoCommand(this.handle, command)
                    }
                    var result = command.Execute(this)
                    if (result is of bool) {
                        return result
                    }
                    return true
                }

                /// <summary>Executes the commands in order and stops at the first one that returns `false` (e.g. because the device is not connected).</summary>
                /// <param name="commands">An array, a List, or anything that can be iterated with foreach.</param>
                /// <returns>`true` if all commands ran.</returns>
                bool DoCommands(commands) {
                    foreach (c in commands) {
                        if (!this.DoCommand(c)) {
                            return false
                        }
                    }
                    return true
                }

                /// <summary>Is received data waiting? Data is read one packet at a time (after a WaitFor: the rest of the packet that was cut).</summary>
                bool HasData() { return __DEVHasData(this.handle) }
                /// <summary>Reads the next received packet as text, one character per byte (Latin1).</summary>
                /// <returns>The text, or "" if nothing is waiting.</returns>
                string ReadString() { return __DEVReadString(this.handle) }
                /// <summary>Reads the next received packet as a byte buffer.</summary>
                /// <returns>The buffer, empty if nothing is waiting.</returns>
                Read() { return __DEVRead(this.handle) }

                /// <summary>Writes exactly these characters WITHOUT a line ending - one character per byte (Latin1). For UTF-8 use `text.ToBytes()` with Write.</summary>
                /// <returns>`false` if the device is not connected.</returns>
                bool WriteString(string text) { return __DEVWriteString(this.handle, text) }
                /// <summary>Writes exactly these bytes.</summary>
                /// <param name="data">A byte buffer.</param>
                /// <returns>`false` if the device is not connected.</returns>
                bool Write(data) { return __DEVWrite(this.handle, data) }

                /// <summary>Waits until the characters appear in the receive buffer (also across packet boundaries) and cuts the buffer behind them:
                /// everything before them and the match are consumed, what came after stays - also a second occurrence, so the same call can succeed again right away.</summary>
                /// <param name="text">The characters to wait for.</param>
                /// <param name="timeout">A TimeSpan, a time value (`5s`, `500ms`) or milliseconds. Without it the program's `#timeout` applies, otherwise 30 seconds.</param>
                /// <returns>`true` if they arrived; `false` after the time ran out, if the device is disconnected, or if the program ends.</returns>
                bool WaitForString(string text, timeout = undefined) {
                    var r = __DEVWaitForString(this.handle, text, timeout)
                    if (r < 0) {
                        throw new DeviceArgumentException("Invalid wait time (expected: TimeSpan, a time value like 5s, or milliseconds)")
                    }
                    return r == 1
                }
                /// <summary>Like WaitForString, but waits for a sequence of bytes.</summary>
                /// <param name="data">A byte buffer with the bytes to wait for.</param>
                /// <param name="timeout">A TimeSpan, a time value (`5s`, `500ms`) or milliseconds.</param>
                bool WaitFor(data, timeout = undefined) {
                    var r = __DEVWaitFor(this.handle, data, timeout)
                    if (r < 0) {
                        throw new DeviceArgumentException("Invalid wait time (expected: TimeSpan, a time value like 5s, or milliseconds)")
                    }
                    return r == 1
                }
            }

            class DeviceManagerFacade {
                construct() { }

                Refresh(bool fastScan) { __DEVMgrRefresh(fastScan) }

                int Count() { return __DEVMgrCount() }

                // Gehört der Manager dem Host (Editor) und wird von Skripten nur mitbenutzt?
                bool IsShared() { return __DEVMgrIsShared() }

                Device GetAt(int index) {
                    var h = __DEVMgrHandleAt(index)
                    if (h == -1) {
                        throw new DeviceNotFoundException("No device with index " + index)
                    }
                    return new Device(h)
                }

                Device GetByHandle(int handle) {
                    if (__DEVIdentifier(handle) == "") {
                        throw new DeviceNotFoundException("No device with handle " + handle)
                    }
                    return new Device(handle)
                }

                Device GetByIdentifier(string identifier) {
                    var h = __DEVMgrHandleForIdentifier(identifier)
                    if (h == -1) {
                        throw new DeviceNotFoundException("No device with identifier '" + identifier + "'")
                    }
                    return new Device(h)
                }
            }
            """;
    }
}
