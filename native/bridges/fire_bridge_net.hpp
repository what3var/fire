// fire native bridge "net": the natives behind `#import "net"` (docs/NETWORK.md) - TCP connections and listeners, UDP sockets, name resolution. The fire side (Net.TcpClient,
// Net.TcpListener, Net.UdpSocket, Net.Dns, ... - fire source) is the same in the VM and in a native build; this file is what its `__Net...` functions do.
//
// This file is the one implementation of the net natives: the native build includes it (the package "net" brings it as its C++ source) and the virtual machine runs it in a shared
// library built from it (native/abi/fire_pkg_abi.h). The sockets come from the platform package (FIRE_PLATFORM_NET_HEADER: plat::net, see platform/std/fire_net_sockets.hpp).
// A socket is an integer handle in a table of this bridge (like the streams of io); handles are not used again. Every function reports an error the same way: a result of -1/false/
// undefined and the code and message of this thread (`__NetLastError`, `__NetLastErrorMessage`, the same slot as the io bridge), from which the fire code throws a typed exception.
//
// Time limits: every call that waits takes a timeout in milliseconds (< 0: no limit) and never blocks longer. The fire code asks again in short slices, so a program stays abortable
// (`terminate`, threads, the main queue) while it waits for the network.
//
// What the host decides (which hosts and ports a script may talk to) differs by build:
//   - a native build: everything is allowed unless the target defines FIRE_NET_POLICY (a function `bool(const std::string& host, int port, int access, std::string& reason)`,
//     access bits 1 Connect (also: send a datagram), 2 Listen (also: bind a UDP socket), 4 Resolve; define it before the includes in the target configuration);
//   - in the library for the VM: the host (the editor, the runtime) is asked through the callback `net_allow` of the ABI (`fire_host`).
#pragma once

#include <cstddef>
#include <cstring>
#include FIRE_PLATFORM_NET_HEADER
#include <string>
#include <vector>

namespace fire {
namespace net {

enum Err {
    None = 0, InvalidArgument = 1, InvalidHandle = 2, Refused = 3, TimedOut = 4, Unreachable = 5, AddressInUse = 6, Closed = 7,
    Denied = 8, Unsupported = 9, ResolveFailed = 10, Other = 11, NotConnected = 12, Permission = 13
};
enum Access { A_Connect = 1, A_Listen = 2, A_Resolve = 4 };
enum Kind { K_Tcp = 1, K_Listener = 2, K_Udp = 3 };

inline int64_t fail(int code, const std::string& message) {
    g_ioError.code = code;
    size_t n = message.size() < sizeof g_ioError.message - 1 ? message.size() : sizeof g_ioError.message - 1;
    std::memcpy(g_ioError.message, message.data(), n);
    g_ioError.message[n] = 0;
    return -1;
}
inline void ok() { g_ioError.code = 0; g_ioError.message[0] = 0; }
inline int64_t fail(const plat::net::Status& s) { return fail(s.code, s.message); }

// ---- text -------------------------------------------------------------------------------------------------------------------------------
inline std::string toUtf8(Value v) {
    const Str* s = strOf(v);
    std::string out;
    out.reserve(s->length);
    for (uint32_t i = 0; i < s->length; i++) {
        uint32_t c = s->data[i];
        if (c >= 0xD800 && c <= 0xDBFF && i + 1 < s->length && s->data[i + 1] >= 0xDC00 && s->data[i + 1] <= 0xDFFF) { c = 0x10000 + ((c - 0xD800) << 10) + (s->data[i + 1] - 0xDC00); i++; }
        else if (c >= 0xD800 && c <= 0xDFFF) c = 0xFFFD;
        if (c < 0x80) out.push_back((char)c);
        else if (c < 0x800) { out.push_back((char)(0xC0 | (c >> 6))); out.push_back((char)(0x80 | (c & 0x3F))); }
        else if (c < 0x10000) { out.push_back((char)(0xE0 | (c >> 12))); out.push_back((char)(0x80 | ((c >> 6) & 0x3F))); out.push_back((char)(0x80 | (c & 0x3F))); }
        else { out.push_back((char)(0xF0 | (c >> 18))); out.push_back((char)(0x80 | ((c >> 12) & 0x3F))); out.push_back((char)(0x80 | ((c >> 6) & 0x3F))); out.push_back((char)(0x80 | (c & 0x3F))); }
    }
    return out;
}
/// (host names and messages are ASCII or UTF-8 from the system; a byte above 127 becomes the Latin-1 character of the same number)
inline Value str8(const std::string& text, OwnList* list) {
    Str* s = allocStr((uint32_t)text.size(), list);
    for (size_t i = 0; i < text.size(); i++) strChars(s)[i] = (char16_t)(uint8_t)text[i];
    return StrV(s);
}

// ---- the sockets ------------------------------------------------------------------------------------------------------------------------
struct Socket {
    plat::net::Sock fd = plat::net::kInvalid;
    int kind = K_Tcp;
    std::string peerHost;
    int peerPort = 0;
};
/// The open sockets; the handle is the index. What the script left open is closed when the program ends (the safety net of the VM's host).
struct SocketTable {
    std::vector<Socket*> items{1, nullptr};
    ~SocketTable() { for (Socket* s : items) if (s) { plat::net::closeSock(s->fd); delete s; } }
};
inline std::vector<Socket*>& sockets() { static SocketTable table; return table.items; }

/// The program has ended (the library for the VM stays loaded for the next one): closes what it left open.
inline void reset() {
    std::vector<Socket*>& all = sockets();
    for (size_t i = 1; i < all.size(); i++) {
        if (!all[i]) continue;
        plat::net::closeSock(all[i]->fd);
        delete all[i];
        all[i] = nullptr;
    }
}

inline int add(Socket* s) { sockets().push_back(s); return (int)sockets().size() - 1; }
inline Socket* find(Value h) {
    int64_t i = h.i;
    if (i > 0 && (size_t)i < sockets().size() && sockets()[(size_t)i]) return sockets()[(size_t)i];
    fail(InvalidHandle, "Invalid or already closed socket handle.");
    return nullptr;
}
inline Socket* findKind(Value h, int kind, const char* what) {
    Socket* s = find(h);
    if (!s) return nullptr;
    if (s->kind != kind) { fail(InvalidArgument, std::string("This socket is not ") + what + "."); return nullptr; }
    return s;
}
inline bool checkRange(Value bufferValue, int64_t offset, int64_t count) {
    int64_t length = bufOf(bufferValue)->length;
    if (offset < 0 || count < 0 || offset > length || count > length - offset) {
        fail(InvalidArgument, "offset/count (" + std::to_string(offset) + "/" + std::to_string(count) + ") are outside of the buffer (length " + std::to_string(length) + ").");
        return false;
    }
    return true;
}
inline bool checkPort(int64_t port, bool allowZero) {
    if (port < (allowZero ? 0 : 1) || port > 65535) { fail(InvalidArgument, "Invalid port " + std::to_string(port) + " (1..65535" + (allowZero ? ", 0 = any" : "") + ")."); return false; }
    return true;
}

// ---- the host: which hosts and ports a script may talk to -------------------------------------------------------------------------------
inline bool allowed(const std::string& host, int port, int access, std::string& reason) {
#ifdef FIRE_LIBRARY
    const fire_host* h = libraryHost();
    if (!h || h->size < (int32_t)(offsetof(fire_host, net_allow) + sizeof(h->net_allow)) || !h->net_allow) return true;
    char text[256] = {0};
    if (h->net_allow(host.c_str(), port, access, text, (int)sizeof text)) return true;
    reason = text;
    return false;
#elif defined(FIRE_NET_POLICY)
    return FIRE_NET_POLICY(host, port, access, reason);
#else
    (void)host; (void)port; (void)access; (void)reason;
    return true;
#endif
}
inline bool authorize(const std::string& host, int port, int access) {
    std::string reason;
    if (allowed(host, port, access, reason)) return true;
    std::string what = host.empty() ? std::string("*") : host;
    if (port > 0) what += ":" + std::to_string(port);
    fail(Denied, reason.empty() ? "Network access to '" + what + "' is not allowed." : reason);
    return false;
}

// ---- natives ----------------------------------------------------------------------------------------------------------------------------
inline Value LastError() { return Int(g_ioError.code); }
inline Value LastErrorMessage(OwnList* list) { return str8(g_ioError.message, list); }
inline Value OpenCount() {
    int64_t n = 0;
    for (Socket* s : sockets()) if (s) n++;
    return Int(n);
}
/// 1 if the platform has a network, else 0.
inline Value Supported() { return Int(plat::net::supported() ? 1 : 0); }

inline Value TcpConnect(Value host, Value port, Value timeoutMs) {
    if (!checkPort(port.i, false)) return Int(-1);
    std::string h = toUtf8(host);
    if (h.empty()) return Int(fail(InvalidArgument, "The host is empty."));
    if (!authorize(h, (int)port.i, A_Connect)) return Int(-1);
    plat::net::Sock fd = plat::net::kInvalid;
    plat::net::Status st = plat::net::tcpConnect(h, (int)port.i, timeoutMs.i, fd);
    if (!st.ok()) return Int(fail(st));
    Socket* s = new Socket();
    s->fd = fd;
    s->kind = K_Tcp;
    plat::net::peerAddress(fd, s->peerHost, s->peerPort);
    ok();
    return Int(add(s));
}

inline Value TcpListen(Value host, Value port, Value backlog) {
    if (!checkPort(port.i, true)) return Int(-1);
    std::string h = toUtf8(host);
    if (!authorize(h, (int)port.i, A_Listen)) return Int(-1);
    plat::net::Sock fd = plat::net::kInvalid;
    plat::net::Status st = plat::net::tcpListen(h, (int)port.i, backlog.i > 0 ? (int)backlog.i : 16, fd);
    if (!st.ok()) return Int(fail(st));
    Socket* s = new Socket();
    s->fd = fd;
    s->kind = K_Listener;
    ok();
    return Int(add(s));
}

inline Value Accept(Value h, Value timeoutMs) {
    Socket* l = findKind(h, K_Listener, "a listener");
    if (!l) return Int(-1);
    plat::net::Sock fd = plat::net::kInvalid;
    plat::net::Status st = plat::net::acceptOne(l->fd, timeoutMs.i, fd);
    if (!st.ok()) return Int(fail(st));
    Socket* s = new Socket();
    s->fd = fd;
    s->kind = K_Tcp;
    plat::net::peerAddress(fd, s->peerHost, s->peerPort);
    ok();
    return Int(add(s));
}

/// Sends up to `count` bytes (waiting up to the timeout until the system takes at least one); the number sent, -1 on an error.
inline Value Send(Value h, Value buffer, Value offset, Value count, Value timeoutMs) {
    if (!leafAlive(buffer)) return destroyedError(buffer);
    if (!checkRange(buffer, offset.i, count.i)) return Int(-1);
    Socket* s = findKind(h, K_Tcp, "a connection");
    if (!s) return Int(-1);
    int sent = 0;
    plat::net::Status st = plat::net::sendBytes(s->fd, bufOf(buffer)->bytes() + offset.i, (int)count.i, timeoutMs.i, sent);
    if (!st.ok()) return Int(fail(st));
    ok();
    return Int(sent);
}

/// Receives up to `count` bytes: the number (0: the other side closed the connection), -1 on an error (TimedOut: nothing came in time).
inline Value Recv(Value h, Value buffer, Value offset, Value count, Value timeoutMs) {
    if (!leafAlive(buffer)) return destroyedError(buffer);
    if (!checkRange(buffer, offset.i, count.i)) return Int(-1);
    Socket* s = findKind(h, K_Tcp, "a connection");
    if (!s) return Int(-1);
    int got = 0;
    plat::net::Status st = plat::net::recvBytes(s->fd, bufOf(buffer)->bytes() + offset.i, (int)count.i, timeoutMs.i, got);
    if (!st.ok()) return Int(fail(st));
    ok();
    return Int(got);
}

inline Value UdpOpen(Value host, Value port) {
    if (!checkPort(port.i, true)) return Int(-1);
    std::string h = toUtf8(host);
    if (!authorize(h, (int)port.i, A_Listen)) return Int(-1);
    plat::net::Sock fd = plat::net::kInvalid;
    plat::net::Status st = plat::net::udpOpen(h, (int)port.i, fd);
    if (!st.ok()) return Int(fail(st));
    Socket* s = new Socket();
    s->fd = fd;
    s->kind = K_Udp;
    ok();
    return Int(add(s));
}

inline Value SendTo(Value h, Value buffer, Value offset, Value count, Value host, Value port) {
    if (!leafAlive(buffer)) return destroyedError(buffer);
    if (!checkRange(buffer, offset.i, count.i)) return Int(-1);
    if (!checkPort(port.i, false)) return Int(-1);
    Socket* s = findKind(h, K_Udp, "a datagram socket");
    if (!s) return Int(-1);
    std::string to = toUtf8(host);
    if (to.empty()) return Int(fail(InvalidArgument, "The host is empty."));
    if (!authorize(to, (int)port.i, A_Connect)) return Int(-1);
    int sent = 0;
    plat::net::Status st = plat::net::sendDatagram(s->fd, bufOf(buffer)->bytes() + offset.i, (int)count.i, to, (int)port.i, sent);
    if (!st.ok()) return Int(fail(st));
    ok();
    return Int(sent);
}

/// Receives one datagram (a longer one is cut to `count`); the sender is then `PeerHost`/`PeerPort` of the socket.
inline Value RecvFrom(Value h, Value buffer, Value offset, Value count, Value timeoutMs) {
    if (!leafAlive(buffer)) return destroyedError(buffer);
    if (!checkRange(buffer, offset.i, count.i)) return Int(-1);
    Socket* s = findKind(h, K_Udp, "a datagram socket");
    if (!s) return Int(-1);
    int got = 0;
    std::string from;
    int fromPort = 0;
    plat::net::Status st = plat::net::recvDatagram(s->fd, bufOf(buffer)->bytes() + offset.i, (int)count.i, timeoutMs.i, got, from, fromPort);
    if (!st.ok()) return Int(fail(st));
    s->peerHost = from;
    s->peerPort = fromPort;
    ok();
    return Int(got);
}

inline Value PeerHost(Value h, OwnList* list) {
    Socket* s = find(h);
    if (!s) return Undef();
    ok();
    return str8(s->peerHost, list);
}
inline Value PeerPort(Value h) {
    Socket* s = find(h);
    if (!s) return Int(-1);
    ok();
    return Int(s->peerPort);
}
inline Value LocalHost(Value h, OwnList* list) {
    Socket* s = find(h);
    if (!s) return Undef();
    std::string host;
    int port;
    plat::net::Status st = plat::net::localAddress(s->fd, host, port);
    if (!st.ok()) { fail(st); return Undef(); }
    ok();
    return str8(host, list);
}
inline Value LocalPort(Value h) {
    Socket* s = find(h);
    if (!s) return Int(-1);
    std::string host;
    int port;
    plat::net::Status st = plat::net::localAddress(s->fd, host, port);
    if (!st.ok()) return Int(fail(st));
    ok();
    return Int(port);
}

/// Bytes that can be read without waiting (a datagram socket: the size of the next datagram); -1 on an error.
inline Value Available(Value h) {
    Socket* s = find(h);
    if (!s) return Int(-1);
    int n = 0;
    plat::net::Status st = plat::net::available(s->fd, n);
    if (!st.ok()) return Int(fail(st));
    ok();
    return Int(n);
}

/// Waits up to the timeout: the result has bit 1 if the socket can be read (a listener: a connection is waiting; a connection: data or the end came in), bit 2 if it can be written.
inline Value Poll(Value h, Value wantRead, Value wantWrite, Value timeoutMs) {
    Socket* s = find(h);
    if (!s) return Int(-1);
    bool r = false, w = false;
    plat::net::Status st = plat::net::waitFor(s->fd, wantRead.i != 0, wantWrite.i != 0, timeoutMs.i, r, w);
    if (!st.ok()) return Int(fail(st));
    ok();
    return Int((r ? 1 : 0) | (w ? 2 : 0));
}

inline Value SetOption(Value h, Value option, Value value) {
    Socket* s = find(h);
    if (!s) return Bool(false);
    plat::net::Status st = plat::net::setOption(s->fd, (int)option.i, (int)value.i);
    if (!st.ok()) { fail(st); return Bool(false); }
    ok();
    return Bool(true);
}

inline Value Shutdown(Value h, Value how) {
    Socket* s = findKind(h, K_Tcp, "a connection");
    if (!s) return Bool(false);
    if (how.i < 0 || how.i > 2) { fail(InvalidArgument, "Invalid shutdown direction."); return Bool(false); }
    plat::net::Status st = plat::net::shutdownSock(s->fd, (int)how.i);
    if (!st.ok()) { fail(st); return Bool(false); }
    ok();
    return Bool(true);
}

inline Value Close(Value h) {
    int64_t i = h.i;
    if (i <= 0 || (size_t)i >= sockets().size() || !sockets()[(size_t)i]) { fail(InvalidHandle, "Invalid or already closed socket handle."); return Bool(false); }
    plat::net::closeSock(sockets()[(size_t)i]->fd);
    delete sockets()[(size_t)i];
    sockets()[(size_t)i] = nullptr;
    ok();
    return Bool(true);
}

/// The addresses of a name (IPv4 and IPv6, as text); a literal address gives itself.
inline Value Resolve(Value host, OwnList* list) {
    std::string h = toUtf8(host);
    if (h.empty()) { fail(InvalidArgument, "The host is empty."); return Undef(); }
    if (!authorize(h, 0, A_Resolve)) return Undef();
    std::vector<std::string> addresses;
    plat::net::Status st = plat::net::resolve(h, addresses);
    if (!st.ok()) { fail(st); return Undef(); }
    ok();
    Arr* a = allocArr((uint32_t)addresses.size(), list);
    for (size_t i = 0; i < addresses.size(); i++) { a->items()[i] = str8(addresses[i], list); retain(a->items()[i]); }
    return ArrV(a);
}

}  // namespace net
}  // namespace fire
