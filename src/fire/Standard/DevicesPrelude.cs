namespace fire.Standard
{
    public static class DevicesPrelude
    {
        /// <summary>
        /// fire-Quelltext zu `#import "devices"`: versteckt die nativen Funktionen (C++, siehe native/bridges/fire_bridge_devices.hpp) hinter gewöhnlichen Klassen
        /// - VOR das eigentliche Nutzer-Skript zu setzen.
        ///
        /// 'Device' selbst hat nur EINEN Konstruktor (per Handle) - ein zweiter, gleich-1-argumentiger Konstruktor für den Identifier wäre nicht überladbar
        /// (Konstruktor-Überladung löst hier nur über die ARGUMENTANZAHL auf, nicht über den Typ). Stattdessen zwei FABRIKMETHODEN auf 'DeviceManagerFacade'
        /// (GetByHandle/GetByIdentifier) - beide liefern ganz normale Device-Instanzen zurück.</summary>
        public const string Source = """
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
