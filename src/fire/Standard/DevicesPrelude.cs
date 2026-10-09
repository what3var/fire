namespace fire.Standard
{
    public static class DevicesPrelude
    {
        /// <summary>
        /// fire source for `#import "devices"`: hides the native functions (C++, see native/bridges/fire_bridge_devices.hpp) behind ordinary classes
        /// - to be placed BEFORE the actual user script.
        ///
        /// 'Device' itself has only ONE constructor (by handle) - a second, equally 1-argument constructor for the identifier would not be overloadable
        /// (constructor overloading here resolves only by ARGUMENT COUNT, not by type). Instead two FACTORY METHODS on 'DeviceManagerFacade'
        /// (GetByHandle/GetByIdentifier) - both return perfectly normal Device instances.</summary>
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

            // An invalid specification passed to a device function (e.g. a wait time that is not a duration).
            class DeviceArgumentException : Exception {
                string message

                construct(string message) {
                    this.message = message
                }
            }

            // What a device can do at command level: send commands (text or Command objects), write and read bytes and text, wait for characters.
            // `Command<IDevice>` is a command that DoCommand executes with the device as context.
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

                // Whether the host (e.g. the editor) has chosen a default device.
                static bool HasDefault {
                    get { return __DEVMgrDefaultHandle() != -1 }
                }

                // The default device chosen by the host (in the editor: device overview -> "Set as Default Device" or the selection in
                // the toolbar). Throws DeviceNotFoundException if none is chosen (e.g. in a standalone program).
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

                // Connected? (property; the method of the same name IsConnected() remains for compatibility.)
                bool IsConnected { get { return __DEVIsConnected(this.handle) } }
                bool IsConnected() { return __DEVIsConnected(this.handle) }

                // Does the device belong to a shared DeviceManager (of the editor)? Then it persists beyond the run.
                bool IsShared { get { return __DEVIsShared(this.handle) } }
                string PortName() { return __DEVPortName(this.handle) }

                // 0 = Unavailable, 1 = Unchecked, 2 = Available (siehe
                // Drivers.DeviceAvailability - dieselben Zahlenwerte).
                int Availability() { return __DEVAvailability(this.handle) }
                int TestAvailability() { return __DEVTestAvailability(this.handle) }

                bool Connect() { return __DEVConnect(this.handle) }

                // Connects only if not yet connected; throws DeviceConnectionException if that does not work.
                // Returns the device itself (Device.Default.EnsureConnected().DoCommand("...")).
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

                // Does the manager belong to the host (editor) and is only co-used by scripts?
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
