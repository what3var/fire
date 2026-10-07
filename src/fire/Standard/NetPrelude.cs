namespace fire.Standard
{
    /// <summary>
    /// `#import "net"`: TCP connections and listeners, UDP sockets and name resolution (docs/NETWORK.md). The natives are C++ (native/bridges/fire_bridge_net.hpp over plat::net: BSD sockets,
    /// Winsock, lwIP); this fire source wraps them in classes in `namespace Net`. It needs `io` (a connection is an `IO.Stream`) and `time` (the clock of the time limits).
    ///
    ///   var c = new Net.TcpClient("example.org", 80)            // connects (10 s limit by default)
    ///   c.WriteString("GET / HTTP/1.0\r\n\r\n")
    ///   var reply = c.ReadBytes(100)                             // like any IO.Stream
    ///
    ///   var l = new Net.TcpListener("127.0.0.1", 0)              // port 0: a free port, l.Port tells which
    ///   var peer = l.Accept(5s)                                  // a Net.TcpClient, Net.TimeoutException after 5 seconds
    ///
    /// Time limits are a `TimeSpan`, a time value (`500ms`, `5s`) or a number (milliseconds); undefined means "no limit". Waiting asks the natives and sleeps a moment between the attempts (1 to 10 ms), so the program stays
    /// abortable (`terminate`) and the other threads keep running. Errors are exceptions: `Net.NetException` (with a `code`) and its subclasses.
    /// </summary>
    public static class NetPrelude
    {
        public const string Source = """
            namespace Net {
                // The codes of NetException.code (the same numbers as in the bridge).
                class NetCodes {
                    static int InvalidArgument() { return 1 }
                    static int InvalidHandle() { return 2 }
                    static int Refused() { return 3 }
                    static int TimedOut() { return 4 }
                    static int Unreachable() { return 5 }
                    static int AddressInUse() { return 6 }
                    static int Closed() { return 7 }
                    static int Denied() { return 8 }
                    static int Unsupported() { return 9 }
                    static int ResolveFailed() { return 10 }
                    static int Other() { return 11 }
                    static int NotConnected() { return 12 }
                    static int Permission() { return 13 }
                    static int Tls() { return 14 }
                    static int Certificate() { return 15 }
                }

                class NetException : Exception {
                    string message
                    int code

                    construct(string message, int code = 11) {
                        this.message = message
                        this.code = code
                    }
                }

                // The other side refused the connection (nobody listens there).
                class RefusedException : NetException {
                    construct(string message) : base(message, 3) { }
                }

                // A time limit ran out.
                class TimeoutException : NetException {
                    construct(string message) : base(message, 4) { }
                }

                // The socket is closed (by this side, or the connection was reset or ended), or not connected.
                class ClosedException : NetException {
                    construct(string message, int code = 7) : base(message, code) { }
                }

                // The host's policy (8) or the operating system (13) does not allow it.
                class PermissionException : NetException {
                    construct(string message, int code = 8) : base(message, code) { }
                }

                // A name could not be resolved.
                class ResolveException : NetException {
                    construct(string message) : base(message, 10) { }
                }

                class NetErrors {
                    // Throws the exception that fits the last error of a native function.
                    static Throw() {
                        var code = __NetLastError()
                        var message = __NetLastErrorMessage()
                        if (code == 2 || code == 7 || code == 12) { throw new ClosedException(message, code) }
                        if (code == 3) { throw new RefusedException(message) }
                        if (code == 4) { throw new TimeoutException(message) }
                        if (code == 8 || code == 13) { throw new PermissionException(message, code) }
                        if (code == 10) { throw new ResolveException(message) }
                        throw new NetException(message, code)
                    }

                    // A valid handle, or the error as an exception (the natives return -1).
                    static int Handle(int handle) {
                        if (handle < 0) { Throw() }
                        return handle
                    }
                }

                // Time limits: milliseconds from a number, a time value or a TimeSpan (undefined: no limit, -1).
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
                        return Net.Clock.Now() + ms * 10000
                    }

                    // Waits a moment before the next attempt: 1 ms at first, up to 10 ms when it takes long (and not past the deadline). The natives are only asked, never made to wait: a program
                    // that waits for the network does not hold the library, so the other fire threads, `terminate` and the main queue keep working.
                    static Pause(int deadline, int tries) {
                        var ms = 1
                        if (tries >= 3) { ms = 2 }
                        if (tries >= 8) { ms = 5 }
                        if (tries >= 20) { ms = 10 }
                        if (deadline >= 0) {
                            var left = (deadline - Net.Clock.Now()) / 10000
                            if (left < ms) { ms = left }
                        }
                        if (ms > 0) { Sleep(ms) }
                    }

                    static bool Expired(int deadline) {
                        if (deadline < 0) { return false }
                        return Net.Clock.Now() >= deadline
                    }
                }

                // Name resolution.
                class Dns {
                    // The addresses of a name as text (IPv4 and IPv6); a literal address gives itself.
                    static Resolve(string host) {
                        var all = __NetResolve(host)
                        if (all == undefined) { Net.NetErrors.Throw() }
                        var list = new List()
                        for (var i = 0; i < all.length; i++) { list.Add(all[i]) }
                        return list
                    }

                    // The first address of a name.
                    static string First(string host) { return Net.Dns.Resolve(host)[0] }
                }

                // A connection (TCP): an IO.Stream. Read returns 0 when the other side has closed its end.
                class TcpClient : IO.Stream {
                    int handle
                    bool closed
                    int readLimit
                    int writeLimit

                    // new TcpClient("host", 80) / ("host", 80, 5s): connects; the limit of connecting is 10 seconds unless given (undefined: no limit).
                    construct(string host, int port, timeout = 10000) {
                        this.handle = -1
                        this.closed = false
                        this.readLimit = -1
                        this.writeLimit = -1
                        this.handle = Net.NetErrors.Handle(__NetConnectBegin(host, port))
                        var deadline = Net.Clock.Deadline(Net.Clock.Millis(timeout))
                        var tries = 0
                        while (true) {
                            var state = __NetConnectStep(this.handle)
                            if (state == 1) { break }
                            if (state < 0) { Net.NetErrors.Throw() }
                            if (Net.Clock.Expired(deadline)) { throw new TimeoutException("Connecting to " + host + ":" + port + " timed out.") }
                            Net.Clock.Pause(deadline, tries)
                            tries = tries + 1
                        }
                    }

                    // A connection that a listener accepted (do not call this yourself).
                    construct(int handle) {
                        this.handle = handle
                        this.closed = false
                        this.readLimit = -1
                        this.writeLimit = -1
                    }

                    destruct() { try { this.Close() } catch (NetException e) { } }

                    bool CanRead { get { return !this.closed } }
                    bool CanWrite { get { return !this.closed } }

                    bool IsClosed { get { return this.closed } }

                    Check() {
                        if (this.closed) { throw new ClosedException("The connection is closed.", 2) }
                    }

                    // The limits of Read and Write (undefined: no limit; the default).
                    ReadTimeout {
                        get { return this.readLimit }
                        set { this.readLimit = Net.Clock.Millis(value) }
                    }

                    WriteTimeout {
                        get { return this.writeLimit }
                        set { this.writeLimit = Net.Clock.Millis(value) }
                    }

                    string RemoteHost {
                        get {
                            this.Check()
                            var h = __NetPeerHost(this.handle)
                            if (h == undefined) { Net.NetErrors.Throw() }
                            return h
                        }
                    }

                    int RemotePort {
                        get {
                            this.Check()
                            return Net.NetErrors.Handle(__NetPeerPort(this.handle))
                        }
                    }

                    string LocalHost {
                        get {
                            this.Check()
                            var h = __NetLocalHost(this.handle)
                            if (h == undefined) { Net.NetErrors.Throw() }
                            return h
                        }
                    }

                    int LocalPort {
                        get {
                            this.Check()
                            return Net.NetErrors.Handle(__NetLocalPort(this.handle))
                        }
                    }

                    // The number the operating system gave the socket (for the tls package, which works on it): do not close or use it yourself.
                    int NativeHandle {
                        get {
                            this.Check()
                            return Net.NetErrors.Handle(__NetNativeHandle(this.handle))
                        }
                    }

                    // Bytes that Read would return without waiting.
                    int Available {
                        get {
                            this.Check()
                            return Net.NetErrors.Handle(__NetAvailable(this.handle))
                        }
                    }

                    // true if something can be read (or the other side has closed) within the time (default: do not wait).
                    bool WaitReadable(timeout = 0) {
                        this.Check()
                        var ms = Net.Clock.Millis(timeout)
                        var deadline = Net.Clock.Deadline(ms)
                        var tries = 0
                        while (true) {
                            var bits = __NetPoll(this.handle, 1, 0, 0)
                            if (bits < 0) { Net.NetErrors.Throw() }
                            if ((bits & 1) != 0) { return true }
                            if (Net.Clock.Expired(deadline)) { return false }
                            Net.Clock.Pause(deadline, tries)
                            tries = tries + 1
                        }
                    }

                    bool NoDelay {
                        set {
                            this.Check()
                            var flag = 0
                            if (value) { flag = 1 }
                            if (!__NetSetOption(this.handle, 1, flag)) { Net.NetErrors.Throw() }
                        }
                    }

                    bool KeepAlive {
                        set {
                            this.Check()
                            var flag = 0
                            if (value) { flag = 1 }
                            if (!__NetSetOption(this.handle, 2, flag)) { Net.NetErrors.Throw() }
                        }
                    }

                    // Up to `count` bytes (at least one unless the other side has closed: then 0). Waits up to ReadTimeout, then Net.TimeoutException.
                    int Read(buffer, offset, count) {
                        this.Check()
                        var deadline = Net.Clock.Deadline(this.readLimit)
                        var tries = 0
                        while (true) {
                            var n = __NetRecv(this.handle, buffer, offset, count, 0)
                            if (n >= 0) { return n }
                            if (__NetLastError() != 4) { Net.NetErrors.Throw() }
                            if (Net.Clock.Expired(deadline)) { throw new TimeoutException("Nothing was received in time.") }
                            Net.Clock.Pause(deadline, tries)
                            tries = tries + 1
                        }
                    }

                    // All `count` bytes (waits as long as the system needs; WriteTimeout limits each wait).
                    int Write(buffer, offset, count) {
                        this.Check()
                        var done = 0
                        while (done < count) {
                            var deadline = Net.Clock.Deadline(this.writeLimit)
                            var n = -1
                            var tries = 0
                            while (true) {
                                n = __NetSend(this.handle, buffer, offset + done, count - done, 0)
                                if (n >= 0) { break }
                                if (__NetLastError() != 4) { Net.NetErrors.Throw() }
                                if (Net.Clock.Expired(deadline)) { throw new TimeoutException("Sending timed out.") }
                                Net.Clock.Pause(deadline, tries)
                                tries = tries + 1
                            }
                            done = done + n
                        }
                        return count
                    }

                    // The text as UTF-8.
                    WriteString(string text) {
                        var bytes = IO.Utf8.GetBytes(text)
                        this.Write(bytes, 0, bytes.length)
                    }

                    Flush() { }

                    // Ends one direction: 0 receiving, 1 sending (the other side then reads 0), 2 both.
                    Shutdown(int how) {
                        this.Check()
                        if (!__NetShutdown(this.handle, how)) { Net.NetErrors.Throw() }
                    }

                    Close() {
                        if (this.closed) { return }
                        this.closed = true
                        if (this.handle >= 0) { __NetClose(this.handle) }
                    }
                }

                // Waits for connections (TCP).
                class TcpListener {
                    int handle
                    bool closed

                    // new TcpListener("127.0.0.1", 8080): host "" listens on every interface, port 0 takes a free port (see Port).
                    construct(string host, int port, int backlog = 16) {
                        this.handle = -1
                        this.closed = false
                        this.handle = Net.NetErrors.Handle(__NetTcpListen(host, port, backlog))
                    }

                    destruct() { try { this.Close() } catch (NetException e) { } }

                    bool IsClosed { get { return this.closed } }

                    Check() {
                        if (this.closed) { throw new ClosedException("The listener is closed.", 2) }
                    }

                    // The port it listens on (what a port 0 became).
                    int Port {
                        get {
                            this.Check()
                            return Net.NetErrors.Handle(__NetLocalPort(this.handle))
                        }
                    }

                    string Host {
                        get {
                            this.Check()
                            var h = __NetLocalHost(this.handle)
                            if (h == undefined) { Net.NetErrors.Throw() }
                            return h
                        }
                    }

                    // true if a connection is waiting (within the time, default: do not wait).
                    bool Pending(timeout = 0) {
                        this.Check()
                        var deadline = Net.Clock.Deadline(Net.Clock.Millis(timeout))
                        var tries = 0
                        while (true) {
                            var bits = __NetPoll(this.handle, 1, 0, 0)
                            if (bits < 0) { Net.NetErrors.Throw() }
                            if ((bits & 1) != 0) { return true }
                            if (Net.Clock.Expired(deadline)) { return false }
                            Net.Clock.Pause(deadline, tries)
                            tries = tries + 1
                        }
                    }

                    // The next connection as a TcpClient; waits without a limit unless one is given, then Net.TimeoutException.
                    Accept(timeout = undefined) {
                        var c = this.TryAccept(timeout)
                        if (c == undefined) { throw new TimeoutException("No connection arrived in time.") }
                        return c
                    }

                    // Like Accept, but undefined instead of the exception when the time runs out.
                    TryAccept(timeout = undefined) {
                        this.Check()
                        var deadline = Net.Clock.Deadline(Net.Clock.Millis(timeout))
                        var tries = 0
                        while (true) {
                            var h = __NetAccept(this.handle, 0)
                            if (h >= 0) { return new TcpClient(h) }
                            if (__NetLastError() != 4) { Net.NetErrors.Throw() }
                            if (Net.Clock.Expired(deadline)) { return undefined }
                            Net.Clock.Pause(deadline, tries)
                            tries = tries + 1
                        }
                    }

                    Close() {
                        if (this.closed) { return }
                        this.closed = true
                        if (this.handle >= 0) { __NetClose(this.handle) }
                    }
                }

                // A datagram socket (UDP): messages, not a stream.
                class UdpSocket {
                    int handle
                    bool closed
                    string fromHost
                    int fromPort

                    // new UdpSocket() takes a free port on every interface; ("127.0.0.1", 9000) binds to an address.
                    construct(string host = "", int port = 0) {
                        this.handle = -1
                        this.closed = false
                        this.fromHost = ""
                        this.fromPort = 0
                        this.handle = Net.NetErrors.Handle(__NetUdpOpen(host, port))
                    }

                    destruct() { try { this.Close() } catch (NetException e) { } }

                    bool IsClosed { get { return this.closed } }

                    Check() {
                        if (this.closed) { throw new ClosedException("The socket is closed.", 2) }
                    }

                    int Port {
                        get {
                            this.Check()
                            return Net.NetErrors.Handle(__NetLocalPort(this.handle))
                        }
                    }

                    // The sender of the datagram that ReceiveFrom received last.
                    string FromHost { get { return this.fromHost } }
                    int FromPort { get { return this.fromPort } }

                    // The size of the next datagram, 0: none waiting.
                    int Available {
                        get {
                            this.Check()
                            return Net.NetErrors.Handle(__NetAvailable(this.handle))
                        }
                    }

                    bool Broadcast {
                        set {
                            this.Check()
                            var flag = 0
                            if (value) { flag = 1 }
                            if (!__NetSetOption(this.handle, 3, flag)) { Net.NetErrors.Throw() }
                        }
                    }

                    // Sends `count` bytes of the buffer as one datagram; returns the number of bytes sent.
                    int SendTo(buffer, int offset, int count, string host, int port) {
                        this.Check()
                        return Net.NetErrors.Handle(__NetSendTo(this.handle, buffer, offset, count, host, port))
                    }

                    int SendTo(buffer, string host, int port) { return this.SendTo(buffer, 0, buffer.length, host, port) }

                    // The text as UTF-8 in one datagram.
                    int SendString(string text, string host, int port) {
                        var bytes = IO.Utf8.GetBytes(text)
                        return this.SendTo(bytes, 0, bytes.length, host, port)
                    }

                    // true if a datagram can be received within the time (default: do not wait).
                    bool WaitReadable(timeout = 0) {
                        this.Check()
                        var deadline = Net.Clock.Deadline(Net.Clock.Millis(timeout))
                        var tries = 0
                        while (true) {
                            var bits = __NetPoll(this.handle, 1, 0, 0)
                            if (bits < 0) { Net.NetErrors.Throw() }
                            if ((bits & 1) != 0) { return true }
                            if (Net.Clock.Expired(deadline)) { return false }
                            Net.Clock.Pause(deadline, tries)
                            tries = tries + 1
                        }
                    }

                    // Receives one datagram into the buffer (a longer one is cut) and returns its size; the sender is FromHost/FromPort. Waits without a limit unless one is given, then Net.TimeoutException.
                    int ReceiveFrom(buffer, int offset, int count, timeout = undefined) {
                        this.Check()
                        var deadline = Net.Clock.Deadline(Net.Clock.Millis(timeout))
                        var tries = 0
                        while (true) {
                            var n = __NetRecvFrom(this.handle, buffer, offset, count, 0)
                            if (n >= 0) {
                                this.fromHost = __NetPeerHost(this.handle)
                                this.fromPort = __NetPeerPort(this.handle)
                                return n
                            }
                            if (__NetLastError() != 4) { Net.NetErrors.Throw() }
                            if (Net.Clock.Expired(deadline)) { throw new TimeoutException("No datagram arrived in time.") }
                            Net.Clock.Pause(deadline, tries)
                            tries = tries + 1
                        }
                    }

                    Close() {
                        if (this.closed) { return }
                        this.closed = true
                        if (this.handle >= 0) { __NetClose(this.handle) }
                    }
                }
            }
            """;
    }
}
