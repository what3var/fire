// fire native platform layer, network part for the BSD sockets API: Linux, macOS, Windows (Winsock) and the ESP32 (lwIP; the platform header includes lwip/sockets.h and
// lwip/netdb.h first and defines FIRE_NET_LWIP). A platform package offers `fire_net.hpp` (the generated file defines FIRE_PLATFORM_NET_HEADER for it) that provides, in fire::plat::net:
//
//   enum Err                      the error codes (the same numbers as in the prelude of the net package: Net.NetException.code)
//   struct Status { int code; std::string message; }     code 0 = ok
//   typedef intptr_t Sock; constexpr Sock kInvalid
//   bool supported()              false: every call fails with Err::Unsupported (a board without a network)
//   Status resolve(host, std::vector<std::string>& addresses)
//   Status connectBegin(host, port, ConnectState*&) / connectStep(state, bool& done) / takeConnected(state) / freeConnect(state)   a connection that is made without waiting
//   Status tcpListen(host, port, backlog, Sock&)           host "" = every interface; port 0 = a free port (ask localAddress)
//   Status acceptOne(listener, timeoutMs, Sock&)
//   Status sendBytes(sock, data, count, timeoutMs, int& sent)       waits (up to the timeout) until at least one byte can be sent
//   Status recvBytes(sock, data, count, timeoutMs, int& got)        got 0: the other side closed; Err::TimedOut when nothing came in time
//   Status udpOpen(host, port, Sock&)                       bound to host:port ("" and 0: any address, a free port)
//   Status sendDatagram(sock, data, count, host, port, int& sent)
//   Status recvDatagram(sock, data, count, timeoutMs, int& got, std::string& fromHost, int& fromPort)
//   Status waitFor(sock, wantRead, wantWrite, timeoutMs, bool& readable, bool& writable)
//   Status available(sock, int& bytes)
//   Status localAddress(sock, std::string& host, int& port) / peerAddress(...)
//   Status setOption(sock, option, value)    option: 1 NoDelay, 2 KeepAlive, 3 Broadcast, 4 ReuseAddress
//   Status shutdownSock(sock, how)           how: 0 receive, 1 send, 2 both
//   void closeSock(sock)
//
// Every socket is non-blocking; the waiting (with a timeout) is done with select(), so a native never blocks longer than the time it is given and the fire code above it
// (which asks again in short slices) stays abortable.
#pragma once

#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#if defined(_WIN32)
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <winsock2.h>
#include <ws2tcpip.h>
#elif defined(FIRE_NET_LWIP)
// (lwip/sockets.h and lwip/netdb.h are included by the platform header)
#include <errno.h>
#include <fcntl.h>
#else
#include <arpa/inet.h>
#include <errno.h>
#include <fcntl.h>
#include <netdb.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <sys/ioctl.h>
#include <sys/select.h>
#include <sys/socket.h>
#include <sys/time.h>
#include <sys/types.h>
#include <unistd.h>
#endif

namespace fire {
namespace plat {
namespace net {

enum Err {
    E_None = 0, E_InvalidArgument = 1, E_InvalidHandle = 2, E_Refused = 3, E_TimedOut = 4, E_Unreachable = 5, E_AddressInUse = 6, E_Closed = 7,
    E_Denied = 8, E_Unsupported = 9, E_ResolveFailed = 10, E_Other = 11, E_NotConnected = 12, E_Permission = 13
};

struct Status {
    int code = 0;
    std::string message;
    bool ok() const { return code == 0; }
};

typedef intptr_t Sock;
constexpr Sock kInvalid = -1;

inline bool supported() { return true; }

#if defined(_WIN32)
inline int lastError() { return WSAGetLastError(); }
inline bool wouldBlock(int e) { return e == WSAEWOULDBLOCK || e == WSAEINPROGRESS || e == WSAEINTR; }
inline bool inProgress(int e) { return e == WSAEWOULDBLOCK || e == WSAEINPROGRESS; }
inline int closeFd(Sock s) { return ::closesocket((SOCKET)s); }
inline SOCKET fdOf(Sock s) { return (SOCKET)s; }
inline std::string errorText(int e) { return "Winsock error " + std::to_string(e); }
#else
inline int lastError() { return errno; }
inline bool wouldBlock(int e) { return e == EAGAIN || e == EWOULDBLOCK || e == EINTR; }
inline bool inProgress(int e) { return e == EINPROGRESS || e == EINTR; }
inline int closeFd(Sock s) { return ::close((int)s); }
inline int fdOf(Sock s) { return (int)s; }
inline std::string errorText(int e) { return std::strerror(e); }
#endif

inline int mapError(int e) {
#if defined(_WIN32)
    switch (e) {
        case WSAECONNREFUSED: return E_Refused;
        case WSAETIMEDOUT: return E_TimedOut;
        case WSAENETUNREACH: case WSAEHOSTUNREACH: case WSAENETDOWN: case WSAEHOSTDOWN: return E_Unreachable;
        case WSAEADDRINUSE: return E_AddressInUse;
        case WSAECONNRESET: case WSAECONNABORTED: case WSAESHUTDOWN: return E_Closed;
        case WSAENOTCONN: return E_NotConnected;
        case WSAEACCES: return E_Permission;
        case WSAENOTSOCK: case WSAEBADF: return E_InvalidHandle;
        case WSAEINVAL: case WSAEAFNOSUPPORT: case WSAEDESTADDRREQ: return E_InvalidArgument;
        default: return E_Other;
    }
#else
    switch (e) {
        case ECONNREFUSED: return E_Refused;
        case ETIMEDOUT: return E_TimedOut;
        case ENETUNREACH: case EHOSTUNREACH: case ENETDOWN: return E_Unreachable;
        case EADDRINUSE: return E_AddressInUse;
        case ECONNRESET: case ECONNABORTED: case EPIPE: return E_Closed;
        case ENOTCONN: return E_NotConnected;
        case EACCES: case EPERM: return E_Permission;
        case EBADF: case ENOTSOCK: return E_InvalidHandle;
        case EINVAL: case EAFNOSUPPORT: case EDESTADDRREQ: return E_InvalidArgument;
        default: return E_Other;
    }
#endif
}

inline Status fail(int code, const std::string& message) { Status s; s.code = code; s.message = message; return s; }
inline Status failErrno(int e, const std::string& what) { return fail(mapError(e), what + ": " + errorText(e)); }
inline Status success() { return Status(); }

/// Winsock must be started once; elsewhere there is nothing to do.
inline bool init() {
#if defined(_WIN32)
    static bool started = [] { WSADATA data; return WSAStartup(MAKEWORD(2, 2), &data) == 0; }();
    return started;
#else
    return true;
#endif
}

inline int64_t nowMillis() {
#if defined(_WIN32)
    return (int64_t)GetTickCount64();
#else
    struct timeval tv;
    gettimeofday(&tv, nullptr);
    return (int64_t)tv.tv_sec * 1000 + tv.tv_usec / 1000;
#endif
}

inline bool setNonBlocking(Sock s) {
#if defined(_WIN32)
    u_long on = 1;
    return ioctlsocket(fdOf(s), FIONBIO, &on) == 0;
#else
    int flags = fcntl(fdOf(s), F_GETFL, 0);
    return flags >= 0 && fcntl(fdOf(s), F_SETFL, flags | O_NONBLOCK) == 0;
#endif
}

inline void noSigPipe(Sock s) {
#if defined(SO_NOSIGPIPE)
    int on = 1;
    ::setsockopt(fdOf(s), SOL_SOCKET, SO_NOSIGPIPE, (const char*)&on, sizeof on);
#else
    (void)s;
#endif
}

/// Waits until the socket can be read and/or written (or the time is up): select(). timeoutMs < 0: no limit. Returns false on an error (status filled).
inline bool selectWait(Sock s, bool wantRead, bool wantWrite, int64_t timeoutMs, bool& readable, bool& writable, Status& status) {
    readable = writable = false;
#if !defined(_WIN32)
    if (fdOf(s) >= FD_SETSIZE) { status = fail(E_Other, "The socket number is too large for select()."); return false; }
#endif
    fd_set rs, ws, es;
    FD_ZERO(&rs); FD_ZERO(&ws); FD_ZERO(&es);
    if (wantRead) FD_SET(fdOf(s), &rs);
    if (wantWrite) FD_SET(fdOf(s), &ws);
    FD_SET(fdOf(s), &es);
    struct timeval tv;
    struct timeval* ptv = nullptr;
    if (timeoutMs >= 0) { tv.tv_sec = (long)(timeoutMs / 1000); tv.tv_usec = (long)((timeoutMs % 1000) * 1000); ptv = &tv; }
    int n = ::select((int)fdOf(s) + 1, wantRead ? &rs : nullptr, wantWrite ? &ws : nullptr, &es, ptv);
    if (n < 0) {
        int e = lastError();
        if (wouldBlock(e)) return true;   // interrupted: nothing is ready, the caller asks again
        status = failErrno(e, "select");
        return false;
    }
    readable = wantRead && FD_ISSET(fdOf(s), &rs);
    writable = wantWrite && FD_ISSET(fdOf(s), &ws);
    if (FD_ISSET(fdOf(s), &es)) { readable = wantRead; writable = wantWrite; }   // an error condition: the next call reports it
    return true;
}

inline std::string portText(int port) { return std::to_string(port); }

/// Host and port of an address.
inline void describe(const sockaddr* sa, std::string& host, int& port) {
    char buf[NI_MAXHOST] = {0};
    host.clear();
    port = 0;
    if (sa->sa_family == AF_INET) {
        const sockaddr_in* in = reinterpret_cast<const sockaddr_in*>(sa);
        inet_ntop(AF_INET, &in->sin_addr, buf, sizeof buf);
        port = ntohs(in->sin_port);
    } else if (sa->sa_family == AF_INET6) {
        const sockaddr_in6* in6 = reinterpret_cast<const sockaddr_in6*>(sa);
        inet_ntop(AF_INET6, &in6->sin6_addr, buf, sizeof buf);
        port = ntohs(in6->sin6_port);
        // an IPv4 client of a dual-stack socket: ::ffff:a.b.c.d -> a.b.c.d
        std::string text = buf;
        if (text.rfind("::ffff:", 0) == 0 && text.find('.') != std::string::npos) { host = text.substr(7); return; }
    }
    host = buf;
}

/// The addresses of a host name (or the literal address itself), stream or datagram, as sockaddr storage.
struct AddrList {
    struct addrinfo* head = nullptr;
    ~AddrList() { if (head) ::freeaddrinfo(head); }
};

inline Status lookup(const std::string& host, int port, int socktype, bool passive, AddrList& out) {
    if (!init()) return fail(E_Other, "The network could not be started.");
    struct addrinfo hints;
    std::memset(&hints, 0, sizeof hints);
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = socktype;
    if (passive) hints.ai_flags |= AI_PASSIVE;
    std::string service = portText(port);
    int rc = ::getaddrinfo(host.empty() ? nullptr : host.c_str(), service.c_str(), &hints, &out.head);
    if (rc != 0 || !out.head) {
#if defined(_WIN32)
        return fail(E_ResolveFailed, "The name '" + host + "' could not be resolved (error " + std::to_string(rc) + ").");
#else
        return fail(E_ResolveFailed, "The name '" + host + "' could not be resolved: " + ::gai_strerror(rc));
#endif
    }
    return success();
}

inline Status resolve(const std::string& host, std::vector<std::string>& addresses) {
    AddrList list;
    Status st = lookup(host, 0, SOCK_STREAM, false, list);
    if (!st.ok()) return st;
    for (struct addrinfo* ai = list.head; ai; ai = ai->ai_next) {
        std::string text;
        int port;
        describe(ai->ai_addr, text, port);
        bool seen = false;
        for (const std::string& a : addresses) if (a == text) seen = true;
        if (!seen && !text.empty()) addresses.push_back(text);
    }
    return success();
}

inline Sock makeSocket(int family, int type) {
#if defined(_WIN32)
    SOCKET s = ::socket(family, type, 0);
    if (s == INVALID_SOCKET) return kInvalid;
#else
    int s = ::socket(family, type, 0);
    if (s < 0) return kInvalid;
#endif
    Sock sock = (Sock)s;
    if (!setNonBlocking(sock)) { closeFd(sock); return kInvalid; }
    noSigPipe(sock);
    return sock;
}

inline void closeSock(Sock s) { if (s != kInvalid) closeFd(s); }

/// A connection that is being made: the addresses of the host are tried one after the other, each with a non-blocking connect that `connectStep` finishes. Nothing here waits (only the name
/// lookup of `connectBegin` can take a while), so a program that connects stays abortable and other threads keep running.
struct ConnectState {
    std::vector<sockaddr_storage> addresses;
    std::vector<socklen_t> lengths;
    size_t next = 0;
    Sock fd = kInvalid;
    std::string host;
    int port = 0;
    Status last;
    ~ConnectState() { if (fd != kInvalid) closeFd(fd); }
};

/// Starts the next attempt; false when no address is left (`state.last` says why).
inline bool connectNextAttempt(ConnectState& state) {
    while (state.next < state.addresses.size()) {
        const sockaddr* sa = reinterpret_cast<const sockaddr*>(&state.addresses[state.next]);
        socklen_t len = state.lengths[state.next];
        state.next++;
        Sock s = makeSocket(sa->sa_family, SOCK_STREAM);
        if (s == kInvalid) { state.last = failErrno(lastError(), "socket"); continue; }
        int rc = ::connect(fdOf(s), sa, (int)len);
        if (rc != 0 && !inProgress(lastError())) { state.last = failErrno(lastError(), "connect to " + state.host + ":" + portText(state.port)); closeSock(s); continue; }
        state.fd = s;
        return true;
    }
    return false;
}

inline Status connectBegin(const std::string& host, int port, ConnectState*& out) {
    out = nullptr;
    AddrList list;
    Status st = lookup(host, port, SOCK_STREAM, false, list);
    if (!st.ok()) return st;
    ConnectState* state = new ConnectState();
    state->host = host;
    state->port = port;
    state->last = fail(E_Unreachable, "No address of '" + host + "' could be reached.");
    for (struct addrinfo* ai = list.head; ai; ai = ai->ai_next) {
        sockaddr_storage ss;
        std::memset(&ss, 0, sizeof ss);
        std::memcpy(&ss, ai->ai_addr, ai->ai_addrlen);
        state->addresses.push_back(ss);
        state->lengths.push_back((socklen_t)ai->ai_addrlen);
    }
    if (!connectNextAttempt(*state) && state->fd == kInvalid) { Status last = state->last; delete state; return last; }
    out = state;
    return success();
}

/// Asks (without waiting) whether the connection is made: `done` is true when it is (then `state.fd` is the connected socket, `takeConnected` hands it over), false when the attempt is
/// still going on; an error when no address could be connected.
inline Status connectStep(ConnectState& state, bool& done) {
    done = false;
    for (;;) {
        if (state.fd == kInvalid) {
            if (!connectNextAttempt(state)) return state.last;
        }
        bool r, w;
        Status st;
        if (!selectWait(state.fd, false, true, 0, r, w, st)) return st;
        if (!w) return success();
        int err = 0;
        socklen_t len = sizeof err;
        ::getsockopt(fdOf(state.fd), SOL_SOCKET, SO_ERROR, (char*)&err, &len);
        if (err == 0) { done = true; return success(); }
        state.last = failErrno(err, "connect to " + state.host + ":" + portText(state.port));
        closeSock(state.fd);
        state.fd = kInvalid;
    }
}

inline Sock takeConnected(ConnectState& state) {
    Sock s = state.fd;
    state.fd = kInvalid;
    return s;
}

inline void freeConnect(ConnectState* state) { delete state; }

inline Status tcpListen(const std::string& host, int port, int backlog, Sock& out) {
    out = kInvalid;
    AddrList list;
    Status st;
    if (host.empty()) {
        // every interface: IPv6 with IPv4 mapped (dual stack) if the system has it, else IPv4
        Sock s = makeSocket(AF_INET6, SOCK_STREAM);
        if (s != kInvalid) {
            int off = 0;
            ::setsockopt(fdOf(s), IPPROTO_IPV6, IPV6_V6ONLY, (const char*)&off, sizeof off);
            int on = 1;
#if !defined(_WIN32)
            ::setsockopt(fdOf(s), SOL_SOCKET, SO_REUSEADDR, (const char*)&on, sizeof on);
#else
            (void)on;
#endif
            sockaddr_in6 a6;
            std::memset(&a6, 0, sizeof a6);
            a6.sin6_family = AF_INET6;
            a6.sin6_addr = in6addr_any;
            a6.sin6_port = htons((uint16_t)port);
            if (::bind(fdOf(s), (sockaddr*)&a6, sizeof a6) == 0 && ::listen(fdOf(s), backlog) == 0) { out = s; return success(); }
            int e = lastError();
            closeSock(s);
            if (mapError(e) == E_AddressInUse || mapError(e) == E_Permission) return failErrno(e, "listen on port " + portText(port));
        }
    }
    st = lookup(host, port, SOCK_STREAM, true, list);
    if (!st.ok()) return st;
    Status last = fail(E_Other, "No address to listen on.");
    for (struct addrinfo* ai = list.head; ai; ai = ai->ai_next) {
        Sock s = makeSocket(ai->ai_family, ai->ai_socktype);
        if (s == kInvalid) { last = failErrno(lastError(), "socket"); continue; }
#if !defined(_WIN32)
        int on = 1;
        ::setsockopt(fdOf(s), SOL_SOCKET, SO_REUSEADDR, (const char*)&on, sizeof on);   // (on Windows SO_REUSEADDR would allow stealing a port)
#endif
        if (::bind(fdOf(s), ai->ai_addr, (int)ai->ai_addrlen) != 0 || ::listen(fdOf(s), backlog) != 0) {
            last = failErrno(lastError(), "listen on " + (host.empty() ? std::string("*") : host) + ":" + portText(port));
            closeSock(s);
            continue;
        }
        out = s;
        return success();
    }
    return last;
}

inline Status acceptOne(Sock listener, int64_t timeoutMs, Sock& out) {
    out = kInvalid;
    bool r, w;
    Status st;
    if (!selectWait(listener, true, false, timeoutMs, r, w, st)) return st;
    if (!r) return fail(E_TimedOut, "No connection arrived in time.");
    sockaddr_storage ss;
    socklen_t len = sizeof ss;
    auto s = ::accept(fdOf(listener), (sockaddr*)&ss, &len);
#if defined(_WIN32)
    if (s == INVALID_SOCKET) {
#else
    if (s < 0) {
#endif
        int e = lastError();
        if (wouldBlock(e)) return fail(E_TimedOut, "No connection arrived in time.");
        return failErrno(e, "accept");
    }
    Sock sock = (Sock)s;
    if (!setNonBlocking(sock)) { closeSock(sock); return failErrno(lastError(), "accept"); }
    noSigPipe(sock);
    out = sock;
    return success();
}

inline Status sendBytes(Sock s, const uint8_t* data, int count, int64_t timeoutMs, int& sent) {
    sent = 0;
    if (count <= 0) return success();
    int64_t deadline = timeoutMs >= 0 ? nowMillis() + timeoutMs : -1;
    for (;;) {
#if defined(MSG_NOSIGNAL)
        const int flags = MSG_NOSIGNAL;
#else
        const int flags = 0;
#endif
        int n = (int)::send(fdOf(s), (const char*)data, count, flags);
        if (n >= 0) { sent = n; return success(); }
        int e = lastError();
        if (!wouldBlock(e)) return failErrno(e, "send");
        int64_t left = deadline < 0 ? -1 : deadline - nowMillis();
        if (deadline >= 0 && left <= 0) return fail(E_TimedOut, "Sending timed out.");
        bool r, w;
        Status st;
        if (!selectWait(s, false, true, left, r, w, st)) return st;
    }
}

inline Status recvBytes(Sock s, uint8_t* data, int count, int64_t timeoutMs, int& got) {
    got = 0;
    if (count <= 0) return success();
    int64_t deadline = timeoutMs >= 0 ? nowMillis() + timeoutMs : -1;
    for (;;) {
        int n = (int)::recv(fdOf(s), (char*)data, count, 0);
        if (n >= 0) { got = n; return success(); }
        int e = lastError();
        if (!wouldBlock(e)) return failErrno(e, "recv");
        int64_t left = deadline < 0 ? -1 : deadline - nowMillis();
        if (deadline >= 0 && left <= 0) return fail(E_TimedOut, "Nothing was received in time.");
        bool r, w;
        Status st;
        if (!selectWait(s, true, false, left, r, w, st)) return st;
    }
}

inline Status udpOpen(const std::string& host, int port, Sock& out) {
    out = kInvalid;
    AddrList list;
    Status st;
    if (host.empty()) {
        Sock s = makeSocket(AF_INET6, SOCK_DGRAM);
        if (s != kInvalid) {
            int off = 0;
            ::setsockopt(fdOf(s), IPPROTO_IPV6, IPV6_V6ONLY, (const char*)&off, sizeof off);
            sockaddr_in6 a6;
            std::memset(&a6, 0, sizeof a6);
            a6.sin6_family = AF_INET6;
            a6.sin6_addr = in6addr_any;
            a6.sin6_port = htons((uint16_t)port);
            if (::bind(fdOf(s), (sockaddr*)&a6, sizeof a6) == 0) { out = s; return success(); }
            int e = lastError();
            closeSock(s);
            if (mapError(e) == E_AddressInUse || mapError(e) == E_Permission) return failErrno(e, "bind to port " + portText(port));
        }
    }
    st = lookup(host, port, SOCK_DGRAM, true, list);
    if (!st.ok()) return st;
    Status last = fail(E_Other, "No address to bind to.");
    for (struct addrinfo* ai = list.head; ai; ai = ai->ai_next) {
        Sock s = makeSocket(ai->ai_family, ai->ai_socktype);
        if (s == kInvalid) { last = failErrno(lastError(), "socket"); continue; }
        if (::bind(fdOf(s), ai->ai_addr, (int)ai->ai_addrlen) != 0) { last = failErrno(lastError(), "bind to " + (host.empty() ? std::string("*") : host) + ":" + portText(port)); closeSock(s); continue; }
        out = s;
        return success();
    }
    return last;
}

inline Status sendDatagram(Sock s, const uint8_t* data, int count, const std::string& host, int port, int& sent) {
    sent = 0;
    AddrList list;
    Status st = lookup(host, port, SOCK_DGRAM, false, list);
    if (!st.ok()) return st;
    // the socket has one family: use the first address of it (a dual-stack socket takes IPv4 as ::ffff:a.b.c.d)
    sockaddr_storage local;
    socklen_t llen = sizeof local;
    int family = AF_INET;
    if (::getsockname(fdOf(s), (sockaddr*)&local, &llen) == 0) family = local.ss_family;
    Status last = fail(E_Unreachable, "No address of '" + host + "' fits the socket.");
    for (struct addrinfo* ai = list.head; ai; ai = ai->ai_next) {
        sockaddr_storage to;
        socklen_t tlen;
        if (ai->ai_family == family) { std::memcpy(&to, ai->ai_addr, ai->ai_addrlen); tlen = (socklen_t)ai->ai_addrlen; }
        else if (family == AF_INET6 && ai->ai_family == AF_INET) {
            sockaddr_in6 m;
            std::memset(&m, 0, sizeof m);
            m.sin6_family = AF_INET6;
            m.sin6_port = reinterpret_cast<sockaddr_in*>(ai->ai_addr)->sin_port;
            uint8_t* b = reinterpret_cast<uint8_t*>(&m.sin6_addr);
            b[10] = b[11] = 0xFF;
            std::memcpy(b + 12, &reinterpret_cast<sockaddr_in*>(ai->ai_addr)->sin_addr, 4);
            std::memcpy(&to, &m, sizeof m);
            tlen = sizeof m;
        } else continue;
#if defined(MSG_NOSIGNAL)
        const int flags = MSG_NOSIGNAL;
#else
        const int flags = 0;
#endif
        int n = (int)::sendto(fdOf(s), (const char*)data, count, flags, (sockaddr*)&to, (int)tlen);
        if (n >= 0) { sent = n; return success(); }
        last = failErrno(lastError(), "sendto " + host + ":" + portText(port));
    }
    return last;
}

inline Status recvDatagram(Sock s, uint8_t* data, int count, int64_t timeoutMs, int& got, std::string& fromHost, int& fromPort) {
    got = 0;
    int64_t deadline = timeoutMs >= 0 ? nowMillis() + timeoutMs : -1;
    for (;;) {
        sockaddr_storage from;
        socklen_t flen = sizeof from;
        int n = (int)::recvfrom(fdOf(s), (char*)data, count, 0, (sockaddr*)&from, &flen);
        if (n >= 0) { got = n; describe((sockaddr*)&from, fromHost, fromPort); return success(); }
        int e = lastError();
#if defined(_WIN32)
        if (e == WSAEMSGSIZE) { got = count; describe((sockaddr*)&from, fromHost, fromPort); return success(); }   // the datagram was longer: the rest is lost, like everywhere
        if (e == WSAECONNRESET) continue;   // an earlier send was answered with ICMP "port unreachable": not an error of this receive
#endif
        if (!wouldBlock(e)) return failErrno(e, "recvfrom");
        int64_t left = deadline < 0 ? -1 : deadline - nowMillis();
        if (deadline >= 0 && left <= 0) return fail(E_TimedOut, "No datagram arrived in time.");
        bool r, w;
        Status st;
        if (!selectWait(s, true, false, left, r, w, st)) return st;
    }
}

inline Status waitFor(Sock s, bool wantRead, bool wantWrite, int64_t timeoutMs, bool& readable, bool& writable) {
    Status st;
    if (!selectWait(s, wantRead, wantWrite, timeoutMs, readable, writable, st)) return st;
    return success();
}

inline Status available(Sock s, int& bytes) {
    bytes = 0;
#if defined(_WIN32)
    u_long n = 0;
    if (ioctlsocket(fdOf(s), FIONREAD, &n) != 0) return failErrno(lastError(), "ioctl");
    bytes = (int)n;
#else
    int n = 0;
    if (::ioctl(fdOf(s), FIONREAD, &n) != 0) return failErrno(lastError(), "ioctl");
    bytes = n;
#endif
    return success();
}

inline Status localAddress(Sock s, std::string& host, int& port) {
    sockaddr_storage ss;
    socklen_t len = sizeof ss;
    if (::getsockname(fdOf(s), (sockaddr*)&ss, &len) != 0) return failErrno(lastError(), "getsockname");
    describe((sockaddr*)&ss, host, port);
    return success();
}

inline Status peerAddress(Sock s, std::string& host, int& port) {
    sockaddr_storage ss;
    socklen_t len = sizeof ss;
    if (::getpeername(fdOf(s), (sockaddr*)&ss, &len) != 0) return failErrno(lastError(), "getpeername");
    describe((sockaddr*)&ss, host, port);
    return success();
}

inline Status setOption(Sock s, int option, int value) {
    int level = SOL_SOCKET, name = 0;
    switch (option) {
        case 1: level = IPPROTO_TCP; name = TCP_NODELAY; break;
        case 2: name = SO_KEEPALIVE; break;
        case 3: name = SO_BROADCAST; break;
        case 4: name = SO_REUSEADDR; break;
        default: return fail(E_InvalidArgument, "Unknown socket option " + std::to_string(option) + ".");
    }
    if (::setsockopt(fdOf(s), level, name, (const char*)&value, sizeof value) != 0) return failErrno(lastError(), "setsockopt");
    return success();
}

inline Status shutdownSock(Sock s, int how) {
#if defined(_WIN32)
    int h = how == 0 ? SD_RECEIVE : how == 1 ? SD_SEND : SD_BOTH;
#else
    int h = how == 0 ? SHUT_RD : how == 1 ? SHUT_WR : SHUT_RDWR;
#endif
    if (::shutdown(fdOf(s), h) != 0) return failErrno(lastError(), "shutdown");
    return success();
}

}  // namespace net
}  // namespace plat
}  // namespace fire
