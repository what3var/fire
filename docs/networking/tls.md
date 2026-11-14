# TLS (`#import "tls"`)


TLS over a TCP connection of the net package. The natives are C (`packages/tls/native/fire_bridge_tls.h` over `fire_tls_*` of the platform files of the package, one backend per platform; see `packages/tls/native/fire_tls_c.h`); the classes are in `namespace Tls`. The package needs `net` (`io`, `time`).
`http` uses it for `https://` URLs and for `Http.Server.UseTls`.

```
var s = Tls.Stream.Connect("example.org", 443)            // TCP connect and handshake; certificate chain and host name are checked against the system's certificates
s.WriteString("GET / HTTP/1.0\r\nHost: example.org\r\n\r\n")
print(s.Info)                                              // "TLSv1.3 TLS_AES_256_GCM_SHA384"

var o = new Tls.Options()
o.caPem = IO.File.ReadAllText("my-ca.pem")                 // trust exactly these (or o.caFile = "path"); IO.File needs #import "filesystem"
// o.verify = false                                        // no checking at all: for tests only
var t = Tls.Stream.Connect("intranet", 8443, o)

var server = new Tls.Server(certPem, keyPem)               // PEM text: the certificate (then its chain) and the private key
var secure = server.Accept(listener.Accept())              // handshake as the server; the stream owns the connection
```

| class | |
|---|---|
| `Tls.Stream` | an `IO.Stream` like `Net.TcpClient`: `Read` (0 when the other side has ended the connection), `Write`, `WriteString`, `ReadTimeout`/`WriteTimeout`, `WaitReadable`, `Info`, `RemoteHost`, `RemotePort`, `LocalHost`, `LocalPort` (a `Net.IConnection`), `Connection` (the TCP connection - do not use it), `Close()` (close_notify, then the connection is closed). `Tls.Stream.Connect(host, port, options = undefined, timeout = 10000)`; `new Tls.Stream(tcp, serverName, options, timeout)` wraps a connection you made (it belongs to the stream from then on) |
| `Tls.Options` | `verify` (true), `caFile`, `caPem` (PEM text; both empty: the system's certificates), `certPem` + `keyPem` (a client certificate) |
| `Tls.Server(certPem, keyPem)` | `Accept(tcp, timeout = 10000)` returns the `Tls.Stream`, `Close()` (close the streams first) |
| `Tls.Support.Available()` | false on a build or platform without TLS |

Errors: `Tls.TlsException` (code 14: handshake or protocol failure), `Tls.CertificateException` (15: expired, not signed by a trusted authority, wrong host name) - both are `Net.NetException`s - and the exceptions of net (a time limit is `Net.TimeoutException`).
The versions are TLS 1.2 and 1.3; SNI is sent; a server that closes without `close_notify` (HTTP/1.0 style) ends the data like a normal close. Everything runs without waiting inside the library (the handshake is done step by step from fire), so other threads and `terminate` keep working.

**Backends** (`packages/tls/native/`, gewählt von `fire_tls_desktop.h`): *SChannel* (`fire_tls_schannel.h`) on Windows - the default there, nothing to install; *OpenSSL* (`fire_tls_openssl.h`, 1.1.1 and 3.x) on Linux, macOS and on Windows with `FIRE_TLS_OPENSSL`; *mbedTLS* (`fire_tls_mbedtls.h`, 2.28 and 3.x) on the ESP32 (it comes with ESP-IDF) and on a desktop with `FIRE_TLS_MBEDTLS`; *none*
(`FIRE_NO_TLS`, and `freertos` without a board package of your own).

* **Linux**: `libssl-dev` (Debian/Ubuntu) or `openssl-devel` must be installed where the library or the native program is built - the package's library for the VM is built at first use (or comes prebuilt in the package). Without the headers the build says so; a program
  that only uses `http://` is not affected (the error only shows when a TLS function is called, and a failed build is remembered for five minutes so that it is not retried at every start).
* **macOS**: OpenSSL from Homebrew/MacPorts; add its `include` and `lib` folders to the toolchain (`includeDirs`, `libs`). A backend on Apple's own stack was looked at and not made: Secure Transport is deprecated since macOS 10.15 and its replacement (Network.framework) is an Objective-C/Swift API
  around its own connection objects that does not fit the socket-based interface of `fire_tls_*`; mbedTLS (`FIRE_TLS_MBEDTLS`) is the alternative without OpenSSL.
* **Windows**: **SChannel**, the TLS of Windows, through SSPI (`secur32`, `crypt32`, `ncrypt`; the package brings the link libraries). Nothing has to be installed and the certificates are those of the Windows certificate store (the trust of the browsers and .NET). TLS 1.2 and 1.3
  (1.3 where the Windows version has it: Windows 11 and Server 2022). The certificate of a server is checked by the backend itself (chain to the system's roots, or - with `caPem`/`caFile` - only to those authorities; dates, purpose, host name, and an IP address as host name against the IP
  entries of the certificate; no revocation checks, as with OpenSSL). `Tls.Server` and a client certificate take PEM text as everywhere: the keys of the kinds `PRIVATE KEY` (PKCS#8), `RSA PRIVATE KEY` and `EC PRIVATE KEY` without a password are understood; an RSA key goes into a key container of
  the CryptoAPI that is named for the process and deleted when the server is freed (a crash leaves it behind in the user's key store), an ECDSA key (P-256, P-384, P-521) into a CNG key. `Info` gives the protocol and the cipher suite as Windows names them.
  To use OpenSSL on Windows instead (a MinGW OpenSSL, e.g. MSYS2's `mingw-w64-x86_64-openssl`) define `FIRE_TLS_OPENSSL` in the target and add `ssl` and `crypto` to its libs; OpenSSL does not use the Windows store, so give `caFile`/`caPem` then.
  *Tested under Wine* (MinGW build, run against OpenSSL and Python servers and with `Tls.Server`/`Http.Server`: handshake, custom CA, host name and IP checks, large transfers, close): the suite builds the TLS case for Windows and runs it under Wine when both are installed. Not tried on a real
  Windows yet: the ECDSA server keys (Wine has no ECC key import), TLS 1.3 and the use of the real certificate store.
* **ESP32**: the mbedTLS of ESP-IDF; add the component `mbedtls` to the project. To check servers without giving CA certificates define `FIRE_TLS_CA_ATTACH(conf)` as `esp_crt_bundle_attach(conf)` in the target (`includes`: `esp_crt_bundle.h`); there is no system
  store on a microcontroller. (Not tried on a board yet.)
* mbedTLS checks host *names* only, not IP addresses in the certificate: connecting to `127.0.0.1` against a certificate with an IP entry works with OpenSSL, not with mbedTLS.

## TLS over buffers: `Tls.Engine`

Some protocols carry the TLS records themselves instead of putting TLS under them - SQL Server's TDS sends the handshake inside its PRELOGIN packets and
then the packets inside TLS. `new Tls.Engine(serverName, options)` is a client session without a socket:

```fire
var e = new Tls.Engine("db.local", options)
while (!e.Step()) {                 // the handshake, step by step
    send(e.Drain())                 // the records to send (empty: none)
    var answer = receive()
    e.Feed(answer, 0, answer.length)  // the records that came
}
send(e.Drain())
e.Write(data, 0, data.length)       // encrypts: Drain gives the records
int n = e.Read(buffer, 0, buffer.length)   // decrypts what was fed: -1 more must be fed first, 0 the other side ended the session
```

The certificate is checked as with `Tls.Stream` (`options.verify`, `caFile`, `caPem`); `Info` gives the protocol and the cipher. It runs on every TLS
library of the package: OpenSSL (two memory BIOs), mbedTLS (its I/O callbacks on two buffers) and SChannel (its records kept in buffers instead of
the socket - tried under Wine); only a build without TLS (`FIRE_NO_TLS`) throws a `Net.NetException` that says so.

