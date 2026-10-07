namespace fire.Standard
{
    /// <summary>
    /// `#import "tls"`: TLS over a TCP connection of the net package (docs/NETWORK.md). The natives are C++ (native/bridges/fire_bridge_tls.hpp over plat::tls: OpenSSL, or mbedTLS on the ESP32);
    /// this fire source wraps them in classes in `namespace Tls`. A `Tls.Stream` is an `IO.Stream` like the `Net.TcpClient` it wraps, so everything that works on streams works encrypted.
    ///
    ///   var s = Tls.Stream.Connect("example.org", 443)                     // handshake: certificate and host name are checked against the system's certificates
    ///   s.WriteString("GET / HTTP/1.0\r\nHost: example.org\r\n\r\n")
    ///   var data = s.ReadBytes(100)
    ///
    ///   var options = new Tls.Options()                              // other certificates, or no checking at all (tests only)
    ///   options.caPem = File.ReadAllText("ca.pem")                   // trust exactly these
    ///   options.verify = false
    ///   var t = Tls.Stream.Connect("localhost", 8443, options)
    ///
    ///   var server = new Tls.Server(certPem, keyPem)                 // the certificate (and chain) and the private key as PEM text
    ///   var client = listener.Accept()                               // a Net.TcpClient
    ///   var secure = server.Accept(client)                           // handshake as the server
    ///
    /// Errors: `Tls.TlsException` (code 14: the handshake or the protocol failed), `Tls.CertificateException` (15: the certificate was not accepted - expired, untrusted, wrong host name) and the
    /// exceptions of the net package. The stream owns the TCP connection: closing it closes both.
    /// </summary>
    public static class TlsPrelude
    {
        public const string Source = """
            namespace Tls {
                // The handshake or the protocol failed (code 14).
                class TlsException : Net.NetException {
                    construct(string message, int code = 14) : base(message, code) { }
                }

                // The certificate of the other side was not accepted (code 15): expired, not signed by a trusted authority, not for this host name.
                class CertificateException : TlsException {
                    construct(string message) : base(message, 15) { }
                }

                class Errors {
                    // Throws the exception that fits the last error of a native function.
                    static Throw() {
                        var code = __TlsLastError()
                        var message = __TlsLastErrorMessage()
                        if (code == 15) { throw new CertificateException(message) }
                        if (code == 14) { throw new TlsException(message, 14) }
                        if (code == 2 || code == 7 || code == 12) { throw new Net.ClosedException(message, code) }
                        if (code == 3) { throw new Net.RefusedException(message) }
                        if (code == 4) { throw new Net.TimeoutException(message) }
                        if (code == 8 || code == 13) { throw new Net.PermissionException(message, code) }
                        throw new Net.NetException(message, code)
                    }

                    static int Handle(int handle) {
                        if (handle < 0) { Throw() }
                        return handle
                    }
                }

                // How a client checks the server (and, with certPem and keyPem, shows a certificate of its own).
                class Options {
                    // false: accept any certificate (for tests: nothing is protected then).
                    bool verify
                    // A file with the certificates of the authorities to trust (PEM) and/or the same as text; without both: the system's certificates.
                    string caFile
                    string caPem
                    // A client certificate and its private key (PEM text).
                    string certPem
                    string keyPem

                    construct() {
                        this.verify = true
                        this.caFile = ""
                        this.caPem = ""
                        this.certPem = ""
                        this.keyPem = ""
                    }
                }

                // true if this build has TLS (OpenSSL or mbedTLS), false on a platform without.
                class Support {
                    static bool Available() { return __TlsSupported() == 1 }
                }

                // An encrypted connection over a Net.TcpClient. Read returns 0 when the other side has ended the connection.
                class Stream : IO.Stream {
                    int handle
                    bool closed
                    var tcp
                    int readLimit
                    int writeLimit

                    // A TLS connection to host:port in one call (the TCP connection is made and the handshake done).
                    static Connect(string host, int port, options = undefined, timeout = 10000) {
                        var tcp = new Net.TcpClient(host, port, timeout)
                        tcp.NoDelay = true
                        return new Tls.Stream(tcp, host, options, timeout)
                    }

                    // The handshake as a client over `tcp`: `serverName` is the host name that the certificate must be for (and that SNI announces); the connection belongs to this stream from now on.
                    // Without a serverName nothing is done (the server side fills the stream, see Tls.Server.Accept).
                    construct(tcp, string serverName = undefined, options = undefined, timeout = 10000) {
                        this.handle = -1
                        this.closed = false
                        this.readLimit = -1
                        this.writeLimit = -1
                        this.tcp = tcp
                        tcp.TakeTo(this)
                        if (serverName != undefined) {
                            var o = options
                            if (o == undefined) { o = new Tls.Options() }
                            var verify = 0
                            if (o.verify) { verify = 1 }
                            this.handle = Tls.Errors.Handle(__TlsOpen(tcp.NativeHandle, serverName, verify, o.caFile, o.caPem, o.certPem, o.keyPem))
                            this.Handshake(timeout)
                        }
                    }

                    // Does the handshake: asks the natives again and again (sleeping a moment in between) until it is complete, fails or the time runs out (undefined: no limit).
                    Handshake(timeout) {
                        var deadline = Net.Clock.Deadline(Net.Clock.Millis(timeout))
                        var tries = 0
                        while (true) {
                            var state = __TlsHandshake(this.handle)
                            if (state == 1) { return }
                            if (state < 0) { Tls.Errors.Throw() }
                            if (Net.Clock.Expired(deadline)) { throw new Net.TimeoutException("The TLS handshake timed out.") }
                            Net.Clock.Pause(deadline, tries)
                            tries = tries + 1
                        }
                    }

                    destruct() { try { this.Close() } catch (Net.NetException e) { } }

                    bool CanRead { get { return !this.closed } }
                    bool CanWrite { get { return !this.closed } }
                    bool IsClosed { get { return this.closed } }

                    Check() {
                        if (this.closed) { throw new Net.ClosedException("The connection is closed.", 2) }
                    }

                    ReadTimeout {
                        get { return this.readLimit }
                        set { this.readLimit = Net.Clock.Millis(value) }
                    }

                    WriteTimeout {
                        get { return this.writeLimit }
                        set { this.writeLimit = Net.Clock.Millis(value) }
                    }

                    // The TCP connection underneath (do not read or write it: that would break the encryption).
                    Connection { get { return this.tcp } }

                    string RemoteHost { get { return this.tcp.RemoteHost } }
                    int RemotePort { get { return this.tcp.RemotePort } }

                    // "TLSv1.3 TLS_AES_256_GCM_SHA384": the protocol and the cipher that were agreed on.
                    string Info {
                        get {
                            this.Check()
                            var text = __TlsInfo(this.handle)
                            if (text == undefined) { Tls.Errors.Throw() }
                            return text
                        }
                    }

                    // true if something can be read within the time (default: do not wait).
                    bool WaitReadable(timeout = 0) {
                        this.Check()
                        if (__TlsPending(this.handle) > 0) { return true }
                        return this.tcp.WaitReadable(timeout)
                    }

                    // Up to `count` bytes (at least one unless the other side has ended the connection: then 0). Waits up to ReadTimeout, then Net.TimeoutException.
                    int Read(buffer, offset, count) {
                        this.Check()
                        var deadline = Net.Clock.Deadline(this.readLimit)
                        var tries = 0
                        while (true) {
                            var n = __TlsRead(this.handle, buffer, offset, count, 0)
                            if (n >= 0) { return n }
                            if (__TlsLastError() != 4) { Tls.Errors.Throw() }
                            if (Net.Clock.Expired(deadline)) { throw new Net.TimeoutException("Nothing was received in time.") }
                            Net.Clock.Pause(deadline, tries)
                            tries = tries + 1
                        }
                    }

                    // All `count` bytes.
                    int Write(buffer, offset, count) {
                        this.Check()
                        var done = 0
                        while (done < count) {
                            var deadline = Net.Clock.Deadline(this.writeLimit)
                            var tries = 0
                            var n = -1
                            while (true) {
                                n = __TlsWrite(this.handle, buffer, offset + done, count - done, 0)
                                if (n >= 0) { break }
                                if (__TlsLastError() != 4) { Tls.Errors.Throw() }
                                if (Net.Clock.Expired(deadline)) { throw new Net.TimeoutException("Sending timed out.") }
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

                    // Ends the TLS session (close_notify) and closes the TCP connection.
                    Close() {
                        if (this.closed) { return }
                        this.closed = true
                        if (this.handle >= 0) { __TlsClose(this.handle) }
                        this.tcp.Close()
                    }
                }

                // The server side: a certificate (with its chain, PEM text) and the private key.
                class Server {
                    int context
                    bool closed

                    construct(string certPem, string keyPem) {
                        this.context = -1
                        this.closed = false
                        this.context = Tls.Errors.Handle(__TlsServerContext(certPem, keyPem))
                    }

                    destruct() { this.Close() }

                    // The handshake as the server over a connection that a Net.TcpListener accepted; returns the encrypted stream (it owns the connection). Close the streams before the server.
                    Accept(tcp, timeout = 10000) {
                        if (this.closed) { throw new Net.ClosedException("The TLS server is closed.", 2) }
                        var stream = new Tls.Stream(tcp)
                        stream.handle = Tls.Errors.Handle(__TlsAccept(this.context, tcp.NativeHandle))
                        stream.Handshake(timeout)
                        return stream
                    }

                    Close() {
                        if (this.closed) { return }
                        this.closed = true
                        if (this.context >= 0) { __TlsFreeContext(this.context) }
                    }
                }
            }
            """;
    }
}
