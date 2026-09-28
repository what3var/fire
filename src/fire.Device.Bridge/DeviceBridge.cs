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

        public static void RegisterAll(NativeRegistry natives, DeviceManager manager)
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
                    device.OnRawDataReceived += bytes => queue.Enqueue(bytes);
                }
                return device;
            }

            natives.RegisterGroup(ManagerPrefix, BuildManagerFunctions(manager));
            natives.RegisterGroup(DevicePrefix, BuildDeviceFunctions(manager, ResolveDevice, receiveQueues));
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
            };
        }

        private static Dictionary<string, NativeFunction> BuildDeviceFunctions(
            DeviceManager manager, System.Func<int, IDevice?> resolve, ConcurrentDictionary<int, ConcurrentQueue<byte[]>> receiveQueues)
        {
            return new Dictionary<string, NativeFunction>
            {
                ["Identifier"] = args =>
                    Value.MakeString(manager.GetIdentifierByHandle((int)args[0].AsInt()) ?? ""),
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
                    try { device.SendCommand(args[1].AsString()); return Value.MakeBool(true); }
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

            class Device {
                int handle

                construct(int handle) {
                    this.handle = handle
                }

                string Identifier() { return __DEVIdentifier(this.handle) }
                bool IsConnected() { return __DEVIsConnected(this.handle) }
                string PortName() { return __DEVPortName(this.handle) }

                // 0 = Unavailable, 1 = Unchecked, 2 = Available (siehe
                // Drivers.DeviceAvailability - dieselben Zahlenwerte).
                int Availability() { return __DEVAvailability(this.handle) }
                int TestAvailability() { return __DEVTestAvailability(this.handle) }

                bool Connect() { return __DEVConnect(this.handle) }
                Disconnect() { __DEVDisconnect(this.handle) }
                bool SendCommand(string command) { return __DEVSendCommand(this.handle, command) }

                bool HasData() { return __DEVHasData(this.handle) }
                string ReadData() { return __DEVReadData(this.handle) }
            }

            class DeviceManagerFacade {
                construct() { }

                Refresh(bool fastScan) { __DEVMgrRefresh(fastScan) }

                int Count() { return __DEVMgrCount() }

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
