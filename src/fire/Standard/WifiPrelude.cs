namespace fire.Standard
{
    /// <summary>
    /// `#import "wifi"`: the WiFi radio of a board (docs/NETWORK.md, "WiFi"): scan for networks, join one as a station, be an access point. The natives are C++ (native/bridges/fire_bridge_wifi.hpp over plat::wifi:
    /// the WiFi driver of ESP-IDF on an ESP32); this fire source wraps them in classes in `namespace WiFi`. It needs `time` (the time limits). Once the station has an address the sockets of the `net` package
    /// work - `net` does not know about WiFi. Where the operating system owns the network (Windows, Linux, macOS) there is no radio to control: `WiFi.Board.Available()` is false, and only the simulated "sim" works.
    ///
    ///   var wifi = new WiFi.Station()                              // the first real interface, or "sim"
    ///   foreach (var n in wifi.Scan()) { print(n.ssid + " " + n.rssi + " dBm, channel " + n.channel) }
    ///   wifi.Connect("home", "secret-password", 20s)                // waits until joined; WiFi.AuthException, WiFi.NotFoundException, WiFi.TimeoutException
    ///   print(wifi.Ip)                                              // "192.168.1.23"
    ///   wifi.Disconnect()
    ///
    ///   var ap = new WiFi.AccessPoint()
    ///   ap.Start("fire-board", "password123")                       // an open network without a password; channel 1, up to 4 stations
    ///   print(ap.Ip + " " + ap.Clients)
    ///
    /// Scanning and joining take seconds on a real radio: the natives are only asked, with a pause of 1 to 20 ms in between, so the program stays abortable (`terminate`) and the other threads keep running.
    /// Time limits are a `TimeSpan`, a time value (`500ms`, `5s`) or a number (milliseconds). `Station.Start(ssid, password)` joins without waiting; `State` and `Poll()` (which calls `onState`) follow it.
    /// The simulated radio "sim" has the networks that `WiFi.Sim.AddNetwork(...)` puts in the air. Credentials are never part of a generated file: pass them at run time.
    /// Errors are exceptions: `WiFi.WiFiException` (with a `code`) and its subclasses.
    /// </summary>
    public static class WifiPrelude
    {
        public const string Source = """
            namespace WiFi {
                enum Auth { Open, Wep, Wpa, Wpa2, Wpa3, Other }
                enum State { Idle, Connecting, Connected }

                class WiFiException : Exception {
                    string message
                    int code

                    construct(string message, int code = 7) {
                        this.message = message
                        this.code = code
                    }
                }

                // The network was not found (code 3): not in range, wrong name.
                class NotFoundException : WiFiException {
                    construct(string message) : base(message, 3) { }
                }

                // The radio is busy (code 4): e.g. a scan while joining.
                class BusyException : WiFiException {
                    construct(string message) : base(message, 4) { }
                }

                // This platform has no radio that a program controls (code 6).
                class UnsupportedException : WiFiException {
                    construct(string message) : base(message, 6) { }
                }

                // The network refused the login (code 8): wrong password.
                class AuthException : WiFiException {
                    construct(string message) : base(message, 8) { }
                }

                // It took too long (code 9).
                class TimeoutException : WiFiException {
                    construct(string message) : base(message, 9) { }
                }

                // There is no connection (code 10).
                class NotConnectedException : WiFiException {
                    construct(string message) : base(message, 10) { }
                }

                class Errors {
                    // Throws the exception that fits the last error of a native function.
                    static Throw() {
                        var code = __WiFiLastError()
                        var message = __WiFiLastErrorMessage()
                        if (code == 3) { throw new NotFoundException(message) }
                        if (code == 4) { throw new BusyException(message) }
                        if (code == 6) { throw new UnsupportedException(message) }
                        if (code == 8) { throw new AuthException(message) }
                        if (code == 9) { throw new TimeoutException(message) }
                        if (code == 10) { throw new NotConnectedException(message) }
                        throw new WiFiException(message, code)
                    }

                    static int Handle(int handle) {
                        if (handle < 0) { Throw() }
                        return handle
                    }
                }

                class Clock {
                    static int Millis(limit) {
                        if (limit == undefined) { return -1 }
                        var span = TimeSpan.Of(limit)
                        var ms = span.Ticks / 10000
                        if (ms < 0) { return 0 }
                        return ms
                    }

                    static int Now() { return DateTime.UtcNow().Ticks }

                    // The moment (ticks) a limit of `ms` ends, -1: never.
                    static int Deadline(int ms) {
                        if (ms < 0) { return -1 }
                        return WiFi.Clock.Now() + ms * 10000
                    }

                    static bool Expired(int deadline) {
                        if (deadline < 0) { return false }
                        return WiFi.Clock.Now() >= deadline
                    }

                    // A moment before the next question: 1 ms at first, up to 20 ms when it takes long (and not past the deadline).
                    static Pause(int deadline, int tries) {
                        var ms = 1
                        if (tries >= 3) { ms = 5 }
                        if (tries >= 20) { ms = 20 }
                        if (deadline >= 0) {
                            var left = (deadline - WiFi.Clock.Now()) / 10000
                            if (left < ms) { ms = left }
                        }
                        if (ms > 0) { Sleep(ms) }
                    }
                }

                // The radios of this machine.
                class Board {
                    // true if this machine has a radio that a program can control (the interface "sim" is always there).
                    static bool Available() { return __WiFiSupported() == 1 }

                    // The names of the interfaces: "sim" first, then the real ones ("wifi" on an ESP32).
                    static Interfaces() {
                        var all = __WiFiInterfaces()
                        if (all == undefined) { WiFi.Errors.Throw() }
                        var list = new List()
                        for (var i = 0; i < all.length; i++) { list.Add(all[i]) }
                        return list
                    }

                    // The first real interface, or "sim" when there is none.
                    static string DefaultInterface() {
                        var all = __WiFiInterfaces()
                        if (all == undefined) { WiFi.Errors.Throw() }
                        if (all.length > 1) { return all[1] }
                        return "sim"
                    }
                }

                // A network that a scan found.
                class Network {
                    string ssid
                    string bssid
                    int rssi
                    int channel
                    int auth

                    construct(string ssid, string bssid, int rssi, int channel, int auth) {
                        this.ssid = ssid
                        this.bssid = bssid
                        this.rssi = rssi
                        this.channel = channel
                        this.auth = auth
                    }

                    // true if it needs a password.
                    bool Secure { get { return this.auth != 0 } }
                }

                // The station: joins a network.
                class Station {
                    int handle
                    bool closed
                    string name
                    int lastState
                    var onState

                    construct(string name = undefined) {
                        this.handle = -1
                        this.closed = false
                        this.lastState = 0
                        this.onState = undefined
                        if (name == undefined) { name = WiFi.Board.DefaultInterface() }
                        this.name = name
                        this.handle = WiFi.Errors.Handle(__WiFiOpen(name))
                    }

                    destruct() { try { this.Close() } catch (WiFiException e) { } }

                    string Name { get { return this.name } }
                    bool IsClosed { get { return this.closed } }

                    Check() {
                        if (this.closed) { throw new WiFiException("The station is closed.", 2) }
                    }

                    // The networks in range, strongest first (a list of WiFi.Network). Waits up to `timeout` (default 15 s) for the scan to finish: WiFi.TimeoutException.
                    Scan(timeout = 15s) {
                        this.Check()
                        if (!__WiFiScanBegin(this.handle)) { WiFi.Errors.Throw() }
                        var deadline = WiFi.Clock.Deadline(WiFi.Clock.Millis(timeout))
                        var tries = 0
                        while (true) {
                            var done = __WiFiScanStep(this.handle)
                            if (done == 1) { break }
                            if (done < 0) { WiFi.Errors.Throw() }
                            if (WiFi.Clock.Expired(deadline)) { throw new TimeoutException("The scan did not finish in time.") }
                            WiFi.Clock.Pause(deadline, tries)
                            tries = tries + 1
                        }
                        var list = new List()
                        var count = __WiFiScanCount(this.handle)
                        for (var i = 0; i < count; i++) {
                            var n = new WiFi.Network(__WiFiScanSsid(this.handle, i), __WiFiScanBssid(this.handle, i), __WiFiScanInfo(this.handle, i, 0), __WiFiScanInfo(this.handle, i, 1), __WiFiScanInfo(this.handle, i, 2))
                            n.TakeTo(list)
                            // strongest first: insert before the first weaker one
                            var at = list.count
                            for (var k = 0; k < list.count; k++) {
                                if (list[k].rssi < n.rssi) { at = k; break }
                            }
                            list.Insert(at, n)
                        }
                        return list
                    }

                    // Joins the network and waits (up to `timeout`, default 20 s) until it is connected: WiFi.NotFoundException, WiFi.AuthException, WiFi.TimeoutException. An empty password: an open network.
                    Connect(string ssid, string password = "", timeout = 20s) {
                        this.Start(ssid, password)
                        var deadline = WiFi.Clock.Deadline(WiFi.Clock.Millis(timeout))
                        var tries = 0
                        while (true) {
                            var s = __WiFiConnectState(this.handle)
                            if (s == 2) { this.lastState = 2; return }
                            if (s < 0) { WiFi.Errors.Throw() }
                            if (s == 0) { throw new NotConnectedException("The connection was lost while joining.") }
                            if (WiFi.Clock.Expired(deadline)) {
                                __WiFiDisconnect(this.handle)
                                throw new TimeoutException("Joining '" + ssid + "' did not finish in time.")
                            }
                            WiFi.Clock.Pause(deadline, tries)
                            tries = tries + 1
                        }
                    }

                    // Starts joining and returns at once; State, IsConnected and Poll() follow it.
                    Start(string ssid, string password = "") {
                        this.Check()
                        if (!__WiFiConnectBegin(this.handle, ssid, password)) { WiFi.Errors.Throw() }
                        this.lastState = 1
                    }

                    // WiFi.State.Idle / Connecting / Connected; a failed join throws the exception of the failure (NotFoundException, AuthException, ...).
                    int State {
                        get {
                            this.Check()
                            var s = __WiFiConnectState(this.handle)
                            if (s < 0) { WiFi.Errors.Throw() }
                            return s
                        }
                    }

                    bool IsConnected { get { return this.State == 2 } }

                    // Calls `onState(state)` when the state has changed since the last call (also with a failed join: the exception is thrown). Call it from the loop of the program.
                    Poll() {
                        var s = this.State
                        if (s != this.lastState) {
                            this.lastState = s
                            var handler = this.onState
                            if (handler != undefined) { handler(s) }
                        }
                    }

                    Disconnect() {
                        this.Check()
                        if (!__WiFiDisconnect(this.handle)) { WiFi.Errors.Throw() }
                        this.lastState = 0
                    }

                    // The name of the network it is joined to ("" if none).
                    string Ssid { get { return this.Text(0) } }

                    // Its IP address ("" if it has none).
                    string Ip { get { return this.Text(1) } }

                    // Its own MAC address.
                    string Mac { get { return this.Text(2) } }

                    // The signal strength of the joined network in dBm (0: not connected).
                    int Rssi {
                        get {
                            this.Check()
                            var r = __WiFiStationRssi(this.handle)
                            if (r == -1000) { WiFi.Errors.Throw() }
                            return r
                        }
                    }

                    string Text(int which) {
                        this.Check()
                        var t = __WiFiStationText(this.handle, which)
                        if (t == undefined) { WiFi.Errors.Throw() }
                        return t
                    }

                    Close() {
                        if (this.closed) { return }
                        this.closed = true
                        if (this.handle >= 0) { __WiFiClose(this.handle) }
                    }
                }

                // The access point: the board is a network that others join.
                class AccessPoint {
                    int handle
                    bool closed
                    string name

                    construct(string name = undefined) {
                        this.handle = -1
                        this.closed = false
                        if (name == undefined) { name = WiFi.Board.DefaultInterface() }
                        this.name = name
                        this.handle = WiFi.Errors.Handle(__WiFiOpen(name))
                    }

                    destruct() { try { this.Close() } catch (WiFiException e) { } }

                    string Name { get { return this.name } }
                    bool IsClosed { get { return this.closed } }

                    Check() {
                        if (this.closed) { throw new WiFiException("The access point is closed.", 2) }
                    }

                    // Starts the network `ssid` (without a password: open; else 8 to 63 characters, WPA2) on a channel (1 .. 13) for up to `maxClients` stations (1 .. 10).
                    Start(string ssid, string password = "", int channel = 1, int maxClients = 4) {
                        this.Check()
                        if (!__WiFiApStart(this.handle, ssid, password, channel, maxClients)) { WiFi.Errors.Throw() }
                    }

                    Stop() {
                        this.Check()
                        if (!__WiFiApStop(this.handle)) { WiFi.Errors.Throw() }
                    }

                    bool IsRunning {
                        get {
                            this.Check()
                            var r = __WiFiApRunning(this.handle)
                            if (r < 0) { WiFi.Errors.Throw() }
                            return r == 1
                        }
                    }

                    // Its IP address (the stations are given addresses from its network).
                    string Ip { get { return this.Text(0) } }
                    string Mac { get { return this.Text(1) } }

                    // How many stations are joined.
                    int Clients {
                        get {
                            this.Check()
                            var n = __WiFiApClients(this.handle)
                            if (n < 0) { WiFi.Errors.Throw() }
                            return n
                        }
                    }

                    string Text(int which) {
                        this.Check()
                        var t = __WiFiApText(this.handle, which)
                        if (t == undefined) { WiFi.Errors.Throw() }
                        return t
                    }

                    Close() {
                        if (this.closed) { return }
                        this.closed = true
                        if (this.handle >= 0) { __WiFiClose(this.handle) }
                    }
                }

                // The simulated radio "sim" from the outside: the networks in the air (for tests, and to try a program without the board).
                class Sim {
                    // Puts a network in the air: an empty password makes it open; a network of the same name is replaced.
                    static AddNetwork(string ssid, string password = "", int rssi = -60, int channel = 1) {
                        if (!__WiFiSimAddNetwork(ssid, password, rssi, channel)) { WiFi.Errors.Throw() }
                    }

                    // Takes a network out of the air (a station that is joined to it loses the connection).
                    static RemoveNetwork(string ssid) { __WiFiSimRemoveNetwork(ssid) }

                    // The connection of the station is lost (the router went away).
                    static Drop() { __WiFiSimDrop() }

                    // How many questions a join and a scan take to answer (default 2 and 1).
                    static Delays(int joinQuestions, int scanQuestions) {
                        if (!__WiFiSimDelays(joinQuestions, scanQuestions)) { WiFi.Errors.Throw() }
                    }

                    // A station joins the simulated access point; returns how many are joined now.
                    static int ClientJoins() {
                        var n = __WiFiSimApClients(1)
                        if (n < 0) { WiFi.Errors.Throw() }
                        return n
                    }

                    static int ClientLeaves() {
                        var n = __WiFiSimApClients(-1)
                        if (n < 0) { WiFi.Errors.Throw() }
                        return n
                    }

                    // Removes all networks and takes the radio back to the start.
                    static Reset() { __WiFiSimReset() }
                }
            }
            """;
    }
}
