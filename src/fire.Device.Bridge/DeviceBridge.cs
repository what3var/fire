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
    /// nebenläufigkeitssicheren Warteschlange (siehe RegisterAll) - fire-
    /// Code fragt aktiv ab (Device.HasData()/ReadData()), aus einem
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
        public static IDisposable RegisterAll(NativeRegistry natives, DeviceManager manager)
        {
            // Pro Geräte-Handle eine eigene Warteschlange empfangener
            // Byte-Pakete (siehe Klassendoku) - und welche Handles schon
            // "angeschlossen" sind (OnRawDataReceived gehookt), damit ein
            // Gerät nicht bei jedem einzelnen nativen Aufruf erneut gehookt
            // wird (das würde denselben Callback mehrfach registrieren und
            // jedes empfangene Paket ebenso oft vervielfachen). Beides
            // nebenläufigkeitssicher (ConcurrentDictionary statt Dictionary/
            // HashSet) - native Funktionen können auch aus MEHREREN
            // gleichzeitig laufenden 'fire {}'-Threads heraus aufgerufen
            // werden, nicht nur vom Hauptthread.
            var receiveQueues = new ConcurrentDictionary<int, ConcurrentQueue<byte[]>>();
            var hookedHandles = new ConcurrentDictionary<int, byte>(); // Wert ungenutzt - dient nur als nebenläufigkeitssicheres Set
            var hooks = new ConcurrentBag<(IDevice Device, Action<byte[]> Handler)>();

            IDevice? ResolveDevice(int handle)
            {
                var device = manager.GetDeviceByHandle(handle);
                if (device == null) return null;

                if (hookedHandles.TryAdd(handle, 0))
                {
                    var queue = new ConcurrentQueue<byte[]>();
                    receiveQueues[handle] = queue;
                    // WICHTIG: läuft auf dem Hintergrund-Thread DES GERÄTS
                    // (siehe SerialDevice.PollyPocket), NICHT auf dem
                    // VM-Thread - deshalb hier nur ein simples, threadsicheres
                    // Enqueue, KEIN Zugriff auf irgendetwas VM-Seitiges.
                    Action<byte[]> handler = bytes => queue.Enqueue(bytes);
                    device.OnRawDataReceived += handler;
                    hooks.Add((device, handler));
                }
                return device;
            }

            natives.RegisterGroup(ManagerPrefix, BuildManagerFunctions(manager));
            natives.RegisterGroup(DevicePrefix, BuildDeviceFunctions(manager, ResolveDevice, receiveQueues));

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
                ["SendCommand"] = args => Value.MakeUndefined(),
                ["HasData"] = args => Value.MakeUndefined(),
                ["ReadData"] = args => Value.MakeUndefined(),
            };
        }

        private static Dictionary<string, NativeFunction> BuildDeviceFunctions(
            DeviceManager manager, System.Func<int, IDevice?> resolve, ConcurrentDictionary<int, ConcurrentQueue<byte[]>> receiveQueues)
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
                ["SendCommand"] = args =>
                {
                    var device = resolve((int)args[0].AsInt());
                    if (device == null) return Value.MakeBool(false);
                    try { return Value.MakeBool(device.SendCommand(args[1].AsString())); }
                    catch { return Value.MakeBool(false); }
                },
                ["HasData"] = args =>
                {
                    resolve((int)args[0].AsInt()); // stellt sicher, dass die Queue existiert (siehe RegisterAll)
                    return Value.MakeBool(receiveQueues.TryGetValue((int)args[0].AsInt(), out var q) && !q.IsEmpty);
                },
                ["ReadData"] = args =>
                {
                    // Latin1 (ISO-8859-1) statt UTF-8: bildet JEDEN Byte-Wert
                    // 0..255 verlustfrei auf GENAU ein Zeichen ab - anders als
                    // UTF-8 (das Mehrbyte-Folgen für alles über 127 erwartet
                    // und bei zufälligen Binärdaten leicht ungültige Folgen
                    // erzeugt) verträgt sich das sowohl mit reinem Text
                    // (der übliche Fall bei einem SendCommand(string)-
                    // basierten Protokoll) als auch mit rohen Binärdaten -
                    // fire-Code kann bei Bedarf mit `str[i]` byteweise
                    // zurückrechnen.
                    resolve((int)args[0].AsInt());
                    if (!receiveQueues.TryGetValue((int)args[0].AsInt(), out var q) || !q.TryDequeue(out var bytes))
                        return Value.MakeString("");
                    return Value.MakeString(System.Text.Encoding.Latin1.GetString(bytes));
                },
            };
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

            class Device {
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
                            throw new DeviceNotFoundException("Kein Standardgerät gewählt")
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
                // Liefert das Gerät selbst zurück (Device.Default.EnsureConnected().SendCommand("...")).
                Device EnsureConnected() {
                    if (!__DEVIsConnected(this.handle)) {
                        if (!__DEVConnect(this.handle)) {
                            throw new DeviceConnectionException("Verbindung zu '" + __DEVIdentifier(this.handle) + "' fehlgeschlagen")
                        }
                    }
                    return this
                }
                Disconnect() { __DEVDisconnect(this.handle) }
                bool SendCommand(string command) { return __DEVSendCommand(this.handle, command) }

                bool HasData() { return __DEVHasData(this.handle) }
                string ReadData() { return __DEVReadData(this.handle) }
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
                        throw new DeviceNotFoundException("Kein Gerät mit Index " + index)
                    }
                    return new Device(h)
                }

                Device GetByHandle(int handle) {
                    if (__DEVIdentifier(handle) == "") {
                        throw new DeviceNotFoundException("Kein Gerät mit Handle " + handle)
                    }
                    return new Device(handle)
                }

                Device GetByIdentifier(string identifier) {
                    var h = __DEVMgrHandleForIdentifier(identifier)
                    if (h == -1) {
                        throw new DeviceNotFoundException("Kein Gerät mit Identifier '" + identifier + "'")
                    }
                    return new Device(h)
                }
            }
            """;
    }
}
