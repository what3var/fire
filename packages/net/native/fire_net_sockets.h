/* fire net package: network part in C for the BSD sockets API: Linux, macOS, Windows (Winsock) and the ESP32 (lwIP; the platform header includes
 * lwip/sockets.h and lwip/netdb.h first and defines FIRE_NET_LWIP). The interface: fire_net_c.h.
 *
 * Every socket is non-blocking; the waiting (with a timeout) is done with select(), so a native never blocks longer than the time it is given and the fire code
 * above it (which asks again in short slices) stays abortable. */
#ifndef FIRE_NET_SOCKETS_H
#define FIRE_NET_SOCKETS_H

#include "fire_net_c.h"

#if defined(_WIN32)
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <winsock2.h>
#include <ws2tcpip.h>
#elif defined(FIRE_NET_LWIP)
/* (lwip/sockets.h and lwip/netdb.h are included by the platform header) */
#include <errno.h>
#include <fcntl.h>
#include <sys/time.h>
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

#ifndef NI_MAXHOST
#define NI_MAXHOST 1025
#endif

static int fire_net_supported(void) { return 1; }

#if defined(_WIN32)
typedef SOCKET fire_net_fd;
static int fire_net_lastError(void) { return WSAGetLastError(); }
static int fire_net_wouldBlock(int e) { return e == WSAEWOULDBLOCK || e == WSAEINPROGRESS || e == WSAEINTR; }
static int fire_net_inProgress(int e) { return e == WSAEWOULDBLOCK || e == WSAEINPROGRESS; }
static void fire_net_closeFd(FireNetSock s) { closesocket((SOCKET)s); }
#define FIRE_NET_FD(s) ((SOCKET)(s))
static void fire_net_errorText(int e, char* out, size_t size) { snprintf(out, size, "Winsock error %d", e); }
#else
typedef int fire_net_fd;
static int fire_net_lastError(void) { return errno; }
static int fire_net_wouldBlock(int e) { return e == EAGAIN || e == EWOULDBLOCK || e == EINTR; }
static int fire_net_inProgress(int e) { return e == EINPROGRESS || e == EINTR; }
static void fire_net_closeFd(FireNetSock s) { close((int)s); }
#define FIRE_NET_FD(s) ((int)(s))
static void fire_net_errorText(int e, char* out, size_t size) { fire_net_copy(out, size, strerror(e)); }
#endif

static int fire_net_mapError(int e) {
#if defined(_WIN32)
    switch (e) {
        case WSAECONNREFUSED: return FIRE_NET_E_REFUSED;
        case WSAETIMEDOUT: return FIRE_NET_E_TIMED_OUT;
        case WSAENETUNREACH: case WSAEHOSTUNREACH: case WSAENETDOWN: case WSAEHOSTDOWN: return FIRE_NET_E_UNREACHABLE;
        case WSAEADDRINUSE: return FIRE_NET_E_ADDRESS_IN_USE;
        case WSAECONNRESET: case WSAECONNABORTED: case WSAESHUTDOWN: return FIRE_NET_E_CLOSED;
        case WSAENOTCONN: return FIRE_NET_E_NOT_CONNECTED;
        case WSAEACCES: return FIRE_NET_E_PERMISSION;
        case WSAENOTSOCK: case WSAEBADF: return FIRE_NET_E_INVALID_HANDLE;
        case WSAEINVAL: case WSAEAFNOSUPPORT: case WSAEDESTADDRREQ: return FIRE_NET_E_INVALID_ARGUMENT;
        default: return FIRE_NET_E_OTHER;
    }
#else
    switch (e) {
        case ECONNREFUSED: return FIRE_NET_E_REFUSED;
        case ETIMEDOUT: return FIRE_NET_E_TIMED_OUT;
        case ENETUNREACH: case EHOSTUNREACH: case ENETDOWN: return FIRE_NET_E_UNREACHABLE;
        case EADDRINUSE: return FIRE_NET_E_ADDRESS_IN_USE;
        case ECONNRESET: case ECONNABORTED: case EPIPE: return FIRE_NET_E_CLOSED;
        case ENOTCONN: return FIRE_NET_E_NOT_CONNECTED;
        case EACCES: case EPERM: return FIRE_NET_E_PERMISSION;
        case EBADF: case ENOTSOCK: return FIRE_NET_E_INVALID_HANDLE;
        case EINVAL: case EAFNOSUPPORT: case EDESTADDRREQ: return FIRE_NET_E_INVALID_ARGUMENT;
        default: return FIRE_NET_E_OTHER;
    }
#endif
}

/* An error of the system: "<what>: <text of the system>"; always 0. */
static int fire_net_failErr(FireNetStatus* st, int e, const char* what) {
    char text[120];
    fire_net_errorText(e, text, sizeof text);
    return fire_net_fail(st, fire_net_mapError(e), "%s: %s", what, text);
}

/* Winsock must be started once; elsewhere there is nothing to do. */
static int fire_net_init(void) {
#if defined(_WIN32)
    static volatile LONG state = 0;   /* 0 not yet, 1 started, 2 failed */
    if (state == 0) {
        WSADATA data;
        LONG result = WSAStartup(MAKEWORD(2, 2), &data) == 0 ? 1 : 2;
        InterlockedCompareExchange(&state, result, 0);
    }
    return state == 1;
#else
    return 1;
#endif
}

static int64_t fire_net_nowMillis(void) {
#if defined(_WIN32)
    return (int64_t)GetTickCount64();
#else
    struct timeval tv;
    gettimeofday(&tv, NULL);
    return (int64_t)tv.tv_sec * 1000 + tv.tv_usec / 1000;
#endif
}

static int fire_net_setNonBlocking(FireNetSock s) {
#if defined(_WIN32)
    u_long on = 1;
    return ioctlsocket(FIRE_NET_FD(s), FIONBIO, &on) == 0;
#else
    int flags = fcntl(FIRE_NET_FD(s), F_GETFL, 0);
    return flags >= 0 && fcntl(FIRE_NET_FD(s), F_SETFL, flags | O_NONBLOCK) == 0;
#endif
}

static void fire_net_noSigPipe(FireNetSock s) {
#if defined(SO_NOSIGPIPE)
    int on = 1;
    setsockopt(FIRE_NET_FD(s), SOL_SOCKET, SO_NOSIGPIPE, (const char*)&on, sizeof on);
#else
    (void)s;
#endif
}

#if defined(MSG_NOSIGNAL)
#define FIRE_NET_SEND_FLAGS MSG_NOSIGNAL
#else
#define FIRE_NET_SEND_FLAGS 0
#endif

/* Waits until the socket can be read and/or written (or the time is up): select(). timeoutMs < 0: no limit. 0 on an error (status filled). */
static int fire_net_select(FireNetSock s, int wantRead, int wantWrite, int64_t timeoutMs, int* readable, int* writable, FireNetStatus* st) {
    fd_set rs, ws, es;
    struct timeval tv;
    struct timeval* ptv = NULL;
    int n;
    *readable = *writable = 0;
#if !defined(_WIN32)
    if (FIRE_NET_FD(s) >= FD_SETSIZE) return fire_net_fail(st, FIRE_NET_E_OTHER, "The socket number is too large for select().");
#endif
    FD_ZERO(&rs); FD_ZERO(&ws); FD_ZERO(&es);
    if (wantRead) FD_SET(FIRE_NET_FD(s), &rs);
    if (wantWrite) FD_SET(FIRE_NET_FD(s), &ws);
    FD_SET(FIRE_NET_FD(s), &es);
    if (timeoutMs >= 0) { tv.tv_sec = (long)(timeoutMs / 1000); tv.tv_usec = (long)((timeoutMs % 1000) * 1000); ptv = &tv; }
    n = select((int)FIRE_NET_FD(s) + 1, wantRead ? &rs : NULL, wantWrite ? &ws : NULL, &es, ptv);
    if (n < 0) {
        int e = fire_net_lastError();
        if (fire_net_wouldBlock(e)) return 1;   /* interrupted: nothing is ready, the caller asks again */
        return fire_net_failErr(st, e, "select");
    }
    *readable = wantRead && FD_ISSET(FIRE_NET_FD(s), &rs);
    *writable = wantWrite && FD_ISSET(FIRE_NET_FD(s), &ws);
    if (FD_ISSET(FIRE_NET_FD(s), &es)) { *readable = wantRead; *writable = wantWrite; }   /* an error condition: the next call reports it */
    return 1;
}

/* Host and port of an address. */
static void fire_net_describe(const struct sockaddr* sa, char* host, size_t size, int* port) {
    char buf[NI_MAXHOST];
    buf[0] = 0;
    *port = 0;
    if (sa->sa_family == AF_INET) {
        const struct sockaddr_in* in = (const struct sockaddr_in*)sa;
        inet_ntop(AF_INET, (void*)&in->sin_addr, buf, sizeof buf);
        *port = ntohs(in->sin_port);
    } else if (sa->sa_family == AF_INET6) {
        const struct sockaddr_in6* in6 = (const struct sockaddr_in6*)sa;
        inet_ntop(AF_INET6, (void*)&in6->sin6_addr, buf, sizeof buf);
        *port = ntohs(in6->sin6_port);
        /* an IPv4 client of a dual-stack socket: ::ffff:a.b.c.d -> a.b.c.d */
        if (strncmp(buf, "::ffff:", 7) == 0 && strchr(buf, '.') != NULL) { fire_net_copy(host, size, buf + 7); return; }
    }
    fire_net_copy(host, size, buf);
}

/* The addresses of a host name (or the literal address itself), stream or datagram (freeaddrinfo frees them). */
static int fire_net_lookup(const char* host, int port, int socktype, int passive, struct addrinfo** out, FireNetStatus* st) {
    struct addrinfo hints;
    char service[16];
    int rc;
    *out = NULL;
    if (!fire_net_init()) return fire_net_fail(st, FIRE_NET_E_OTHER, "The network could not be started.");
    memset(&hints, 0, sizeof hints);
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = socktype;
    if (passive) hints.ai_flags |= AI_PASSIVE;
    snprintf(service, sizeof service, "%d", port);
    rc = getaddrinfo(host[0] ? host : NULL, service, &hints, out);
    if (rc != 0 || *out == NULL) {
        if (*out != NULL) { freeaddrinfo(*out); *out = NULL; }
#if defined(_WIN32) || defined(FIRE_NET_LWIP)
        return fire_net_fail(st, FIRE_NET_E_RESOLVE_FAILED, "The name '%s' could not be resolved (error %d).", host, rc);
#else
        return fire_net_fail(st, FIRE_NET_E_RESOLVE_FAILED, "The name '%s' could not be resolved: %s", host, gai_strerror(rc));
#endif
    }
    return 1;
}

static int fire_net_resolve(const char* host, FireNetList* addresses, FireNetStatus* st) {
    struct addrinfo* head;
    struct addrinfo* ai;
    if (!fire_net_lookup(host, 0, SOCK_STREAM, 0, &head, st)) return 0;
    for (ai = head; ai != NULL; ai = ai->ai_next) {
        char text[NI_MAXHOST];
        int port;
        fire_net_describe(ai->ai_addr, text, sizeof text, &port);
        if (text[0] && !fire_net_listHas(addresses, text)) fire_net_listAdd(addresses, text);
    }
    freeaddrinfo(head);
    return 1;
}

static FireNetSock fire_net_makeSocket(int family, int type) {
    FireNetSock sock;
#if defined(_WIN32)
    SOCKET s = socket(family, type, 0);
    if (s == INVALID_SOCKET) return FIRE_NET_INVALID;
#else
    int s = socket(family, type, 0);
    if (s < 0) return FIRE_NET_INVALID;
#endif
    sock = (FireNetSock)s;
    if (!fire_net_setNonBlocking(sock)) { fire_net_closeFd(sock); return FIRE_NET_INVALID; }
    fire_net_noSigPipe(sock);
    return sock;
}

static void fire_net_close(FireNetSock s) { if (s != FIRE_NET_INVALID) fire_net_closeFd(s); }

/* A connection that is being made: the addresses of the host are tried one after the other, each with a non-blocking connect that `connectStep` finishes. Nothing
 * here waits (only the name lookup of `connectBegin` can take a while), so a program that connects stays abortable and other threads keep running. */
typedef struct FireNetConnect {
    struct sockaddr_storage* addresses;
    socklen_t* lengths;
    size_t count, next;
    FireNetSock fd;
    char host[256];
    int port;
    FireNetStatus last;
} FireNetConnect;

/* Starts the next attempt; 0 when no address is left (`c->last` says why). */
static int fire_net_connectNext(FireNetConnect* c) {
    while (c->next < c->count) {
        const struct sockaddr* sa = (const struct sockaddr*)&c->addresses[c->next];
        socklen_t len = c->lengths[c->next];
        FireNetSock s;
        int rc;
        c->next++;
        s = fire_net_makeSocket(sa->sa_family, SOCK_STREAM);
        if (s == FIRE_NET_INVALID) { fire_net_failErr(&c->last, fire_net_lastError(), "socket"); continue; }
        rc = connect(FIRE_NET_FD(s), sa, (int)len);
        if (rc != 0 && !fire_net_inProgress(fire_net_lastError())) {
            char what[300];
            snprintf(what, sizeof what, "connect to %s:%d", c->host, c->port);
            fire_net_failErr(&c->last, fire_net_lastError(), what);
            fire_net_close(s);
            continue;
        }
        c->fd = s;
        return 1;
    }
    return 0;
}

static void fire_net_freeConnect(FireNetConnect* c) {
    if (c == NULL) return;
    fire_net_close(c->fd);
    free(c->addresses);
    free(c->lengths);
    free(c);
}

static FireNetConnect* fire_net_connectBegin(const char* host, int port, FireNetStatus* st) {
    struct addrinfo* head;
    struct addrinfo* ai;
    FireNetConnect* c;
    size_t n = 0;
    if (!fire_net_lookup(host, port, SOCK_STREAM, 0, &head, st)) return NULL;
    for (ai = head; ai != NULL; ai = ai->ai_next) n++;
    c = (FireNetConnect*)calloc(1, sizeof(FireNetConnect));
    if (c != NULL) {
        c->addresses = (struct sockaddr_storage*)calloc(n ? n : 1, sizeof(struct sockaddr_storage));
        c->lengths = (socklen_t*)calloc(n ? n : 1, sizeof(socklen_t));
    }
    if (c == NULL || c->addresses == NULL || c->lengths == NULL) {
        freeaddrinfo(head);
        if (c != NULL) { free(c->addresses); free(c->lengths); free(c); }
        fire_net_fail(st, FIRE_NET_E_OTHER, "Out of memory.");
        return NULL;
    }
    c->fd = FIRE_NET_INVALID;
    fire_net_copy(c->host, sizeof c->host, host);
    c->port = port;
    fire_net_fail(&c->last, FIRE_NET_E_UNREACHABLE, "No address of '%s' could be reached.", host);
    for (ai = head; ai != NULL; ai = ai->ai_next) {
        memcpy(&c->addresses[c->count], ai->ai_addr, ai->ai_addrlen);
        c->lengths[c->count] = (socklen_t)ai->ai_addrlen;
        c->count++;
    }
    freeaddrinfo(head);
    if (!fire_net_connectNext(c)) {
        *st = c->last;
        fire_net_freeConnect(c);
        return NULL;
    }
    return c;
}

/* Asks (without waiting) whether the connection is made: `*done` is 1 when it is (`takeConnected` hands the socket over), 0 while the attempt is still going on;
 * an error when no address could be connected. */
static int fire_net_connectStep(FireNetConnect* c, int* done, FireNetStatus* st) {
    *done = 0;
    for (;;) {
        int r, w, err = 0;
        socklen_t len = sizeof err;
        char what[300];
        if (c->fd == FIRE_NET_INVALID && !fire_net_connectNext(c)) { *st = c->last; return 0; }
        if (!fire_net_select(c->fd, 0, 1, 0, &r, &w, st)) return 0;
        if (!w) return 1;
        getsockopt(FIRE_NET_FD(c->fd), SOL_SOCKET, SO_ERROR, (char*)&err, &len);
        if (err == 0) { *done = 1; return 1; }
        snprintf(what, sizeof what, "connect to %s:%d", c->host, c->port);
        fire_net_failErr(&c->last, err, what);
        fire_net_close(c->fd);
        c->fd = FIRE_NET_INVALID;
    }
}

static FireNetSock fire_net_takeConnected(FireNetConnect* c) {
    FireNetSock s = c->fd;
    c->fd = FIRE_NET_INVALID;
    return s;
}

/* "listen on <host or *>:<port>" and the like. */
static void fire_net_where(char* out, size_t size, const char* verb, const char* host, int port) { snprintf(out, size, "%s %s:%d", verb, host[0] ? host : "*", port); }

/* Every interface, IPv6 with IPv4 mapped (dual stack) if the system has it: the socket, or FIRE_NET_INVALID (then `*fatal` says whether trying IPv4 makes no sense). */
static FireNetSock fire_net_dualStack(int type, int port, int listenBacklog, int* fatal, FireNetStatus* st) {
    FireNetSock s = fire_net_makeSocket(AF_INET6, type);
    struct sockaddr_in6 a6;
    int off = 0, e;
    *fatal = 0;
    if (s == FIRE_NET_INVALID) return FIRE_NET_INVALID;
    setsockopt(FIRE_NET_FD(s), IPPROTO_IPV6, IPV6_V6ONLY, (const char*)&off, sizeof off);
#if !defined(_WIN32)
    if (type == SOCK_STREAM) { int on = 1; setsockopt(FIRE_NET_FD(s), SOL_SOCKET, SO_REUSEADDR, (const char*)&on, sizeof on); }
#endif
    memset(&a6, 0, sizeof a6);
    a6.sin6_family = AF_INET6;
    a6.sin6_addr = in6addr_any;
    a6.sin6_port = htons((uint16_t)port);
    if (bind(FIRE_NET_FD(s), (struct sockaddr*)&a6, sizeof a6) == 0 && (type != SOCK_STREAM || listen(FIRE_NET_FD(s), listenBacklog) == 0)) return s;
    e = fire_net_lastError();
    fire_net_close(s);
    if (fire_net_mapError(e) == FIRE_NET_E_ADDRESS_IN_USE || fire_net_mapError(e) == FIRE_NET_E_PERMISSION) {
        char what[64];
        snprintf(what, sizeof what, type == SOCK_STREAM ? "listen on port %d" : "bind to port %d", port);
        fire_net_failErr(st, e, what);
        *fatal = 1;
    }
    return FIRE_NET_INVALID;
}

static int fire_net_tcpListen(const char* host, int port, int backlog, FireNetSock* out, FireNetStatus* st) {
    struct addrinfo* head;
    struct addrinfo* ai;
    FireNetStatus last;
    *out = FIRE_NET_INVALID;
    if (host[0] == 0) {
        int fatal;
        FireNetSock s = fire_net_dualStack(SOCK_STREAM, port, backlog, &fatal, st);
        if (s != FIRE_NET_INVALID) { *out = s; return 1; }
        if (fatal) return 0;
    }
    if (!fire_net_lookup(host, port, SOCK_STREAM, 1, &head, st)) return 0;
    fire_net_fail(&last, FIRE_NET_E_OTHER, "No address to listen on.");
    for (ai = head; ai != NULL; ai = ai->ai_next) {
        FireNetSock s = fire_net_makeSocket(ai->ai_family, ai->ai_socktype);
        if (s == FIRE_NET_INVALID) { fire_net_failErr(&last, fire_net_lastError(), "socket"); continue; }
#if !defined(_WIN32)
        { int on = 1; setsockopt(FIRE_NET_FD(s), SOL_SOCKET, SO_REUSEADDR, (const char*)&on, sizeof on); }   /* (on Windows SO_REUSEADDR would allow stealing a port) */
#endif
        if (bind(FIRE_NET_FD(s), ai->ai_addr, (int)ai->ai_addrlen) != 0 || listen(FIRE_NET_FD(s), backlog) != 0) {
            char what[300];
            fire_net_where(what, sizeof what, "listen on", host, port);
            fire_net_failErr(&last, fire_net_lastError(), what);
            fire_net_close(s);
            continue;
        }
        freeaddrinfo(head);
        *out = s;
        return 1;
    }
    freeaddrinfo(head);
    *st = last;
    return 0;
}

static int fire_net_acceptOne(FireNetSock listener, int64_t timeoutMs, FireNetSock* out, FireNetStatus* st) {
    struct sockaddr_storage ss;
    socklen_t len = sizeof ss;
    fire_net_fd s;
    FireNetSock sock;
    int r, w;
    *out = FIRE_NET_INVALID;
    if (!fire_net_select(listener, 1, 0, timeoutMs, &r, &w, st)) return 0;
    if (!r) return fire_net_fail(st, FIRE_NET_E_TIMED_OUT, "No connection arrived in time.");
    s = accept(FIRE_NET_FD(listener), (struct sockaddr*)&ss, &len);
#if defined(_WIN32)
    if (s == INVALID_SOCKET) {
#else
    if (s < 0) {
#endif
        int e = fire_net_lastError();
        if (fire_net_wouldBlock(e)) return fire_net_fail(st, FIRE_NET_E_TIMED_OUT, "No connection arrived in time.");
        return fire_net_failErr(st, e, "accept");
    }
    sock = (FireNetSock)s;
    if (!fire_net_setNonBlocking(sock)) { int e = fire_net_lastError(); fire_net_close(sock); return fire_net_failErr(st, e, "accept"); }
    fire_net_noSigPipe(sock);
    *out = sock;
    return 1;
}

/* The time left until `deadline` (< 0: no limit); 0 means it is over. */
static int64_t fire_net_left(int64_t deadline) {
    int64_t left;
    if (deadline < 0) return -1;
    left = deadline - fire_net_nowMillis();
    return left > 0 ? left : 0;
}

static int fire_net_sendBytes(FireNetSock s, const uint8_t* data, int count, int64_t timeoutMs, int* sent, FireNetStatus* st) {
    int64_t deadline = timeoutMs >= 0 ? fire_net_nowMillis() + timeoutMs : -1;
    *sent = 0;
    if (count <= 0) return 1;
    for (;;) {
        int n = (int)send(FIRE_NET_FD(s), (const char*)data, count, FIRE_NET_SEND_FLAGS), e, r, w;
        int64_t left;
        if (n >= 0) { *sent = n; return 1; }
        e = fire_net_lastError();
        if (!fire_net_wouldBlock(e)) return fire_net_failErr(st, e, "send");
        left = fire_net_left(deadline);
        if (deadline >= 0 && left <= 0) return fire_net_fail(st, FIRE_NET_E_TIMED_OUT, "Sending timed out.");
        if (!fire_net_select(s, 0, 1, left, &r, &w, st)) return 0;
    }
}

static int fire_net_recvBytes(FireNetSock s, uint8_t* data, int count, int64_t timeoutMs, int* got, FireNetStatus* st) {
    int64_t deadline = timeoutMs >= 0 ? fire_net_nowMillis() + timeoutMs : -1;
    *got = 0;
    if (count <= 0) return 1;
    for (;;) {
        int n = (int)recv(FIRE_NET_FD(s), (char*)data, count, 0), e, r, w;
        int64_t left;
        if (n >= 0) { *got = n; return 1; }
        e = fire_net_lastError();
        if (!fire_net_wouldBlock(e)) return fire_net_failErr(st, e, "recv");
        left = fire_net_left(deadline);
        if (deadline >= 0 && left <= 0) return fire_net_fail(st, FIRE_NET_E_TIMED_OUT, "Nothing was received in time.");
        if (!fire_net_select(s, 1, 0, left, &r, &w, st)) return 0;
    }
}

static int fire_net_udpOpen(const char* host, int port, FireNetSock* out, FireNetStatus* st) {
    struct addrinfo* head;
    struct addrinfo* ai;
    FireNetStatus last;
    *out = FIRE_NET_INVALID;
    if (host[0] == 0) {
        int fatal;
        FireNetSock s = fire_net_dualStack(SOCK_DGRAM, port, 0, &fatal, st);
        if (s != FIRE_NET_INVALID) { *out = s; return 1; }
        if (fatal) return 0;
    }
    if (!fire_net_lookup(host, port, SOCK_DGRAM, 1, &head, st)) return 0;
    fire_net_fail(&last, FIRE_NET_E_OTHER, "No address to bind to.");
    for (ai = head; ai != NULL; ai = ai->ai_next) {
        FireNetSock s = fire_net_makeSocket(ai->ai_family, ai->ai_socktype);
        if (s == FIRE_NET_INVALID) { fire_net_failErr(&last, fire_net_lastError(), "socket"); continue; }
        if (bind(FIRE_NET_FD(s), ai->ai_addr, (int)ai->ai_addrlen) != 0) {
            char what[300];
            fire_net_where(what, sizeof what, "bind to", host, port);
            fire_net_failErr(&last, fire_net_lastError(), what);
            fire_net_close(s);
            continue;
        }
        freeaddrinfo(head);
        *out = s;
        return 1;
    }
    freeaddrinfo(head);
    *st = last;
    return 0;
}

static int fire_net_sendDatagram(FireNetSock s, const uint8_t* data, int count, const char* host, int port, int* sent, FireNetStatus* st) {
    struct addrinfo* head;
    struct addrinfo* ai;
    struct sockaddr_storage local;
    socklen_t llen = sizeof local;
    int family = AF_INET;
    FireNetStatus last;
    *sent = 0;
    if (!fire_net_lookup(host, port, SOCK_DGRAM, 0, &head, st)) return 0;
    /* the socket has one family: use the first address of it (a dual-stack socket takes IPv4 as ::ffff:a.b.c.d) */
    if (getsockname(FIRE_NET_FD(s), (struct sockaddr*)&local, &llen) == 0) family = local.ss_family;
    fire_net_fail(&last, FIRE_NET_E_UNREACHABLE, "No address of '%s' fits the socket.", host);
    for (ai = head; ai != NULL; ai = ai->ai_next) {
        struct sockaddr_storage to;
        socklen_t tlen;
        int n;
        if (ai->ai_family == family) { memcpy(&to, ai->ai_addr, ai->ai_addrlen); tlen = (socklen_t)ai->ai_addrlen; }
        else if (family == AF_INET6 && ai->ai_family == AF_INET) {
            struct sockaddr_in6 m;
            uint8_t* b = (uint8_t*)&m.sin6_addr;
            memset(&m, 0, sizeof m);
            m.sin6_family = AF_INET6;
            m.sin6_port = ((struct sockaddr_in*)ai->ai_addr)->sin_port;
            b[10] = b[11] = 0xFF;
            memcpy(b + 12, &((struct sockaddr_in*)ai->ai_addr)->sin_addr, 4);
            memcpy(&to, &m, sizeof m);
            tlen = sizeof m;
        } else continue;
        n = (int)sendto(FIRE_NET_FD(s), (const char*)data, count, FIRE_NET_SEND_FLAGS, (struct sockaddr*)&to, (int)tlen);
        if (n >= 0) { freeaddrinfo(head); *sent = n; return 1; }
        {
            char what[300];
            snprintf(what, sizeof what, "sendto %s:%d", host, port);
            fire_net_failErr(&last, fire_net_lastError(), what);
        }
    }
    freeaddrinfo(head);
    *st = last;
    return 0;
}

static int fire_net_recvDatagram(FireNetSock s, uint8_t* data, int count, int64_t timeoutMs, int* got, char* fromHost, size_t size, int* fromPort, FireNetStatus* st) {
    int64_t deadline = timeoutMs >= 0 ? fire_net_nowMillis() + timeoutMs : -1;
    *got = 0;
    for (;;) {
        struct sockaddr_storage from;
        socklen_t flen = sizeof from;
        int n = (int)recvfrom(FIRE_NET_FD(s), (char*)data, count, 0, (struct sockaddr*)&from, &flen), e, r, w;
        int64_t left;
        if (n >= 0) { *got = n; fire_net_describe((struct sockaddr*)&from, fromHost, size, fromPort); return 1; }
        e = fire_net_lastError();
#if defined(_WIN32)
        if (e == WSAEMSGSIZE) { *got = count; fire_net_describe((struct sockaddr*)&from, fromHost, size, fromPort); return 1; }   /* the datagram was longer: the rest is lost, like everywhere */
        if (e == WSAECONNRESET) continue;   /* an earlier send was answered with ICMP "port unreachable": not an error of this receive */
#endif
        if (!fire_net_wouldBlock(e)) return fire_net_failErr(st, e, "recvfrom");
        left = fire_net_left(deadline);
        if (deadline >= 0 && left <= 0) return fire_net_fail(st, FIRE_NET_E_TIMED_OUT, "No datagram arrived in time.");
        if (!fire_net_select(s, 1, 0, left, &r, &w, st)) return 0;
    }
}

static int fire_net_waitFor(FireNetSock s, int wantRead, int wantWrite, int64_t timeoutMs, int* readable, int* writable, FireNetStatus* st) {
    return fire_net_select(s, wantRead, wantWrite, timeoutMs, readable, writable, st);
}

static int fire_net_available(FireNetSock s, int* bytes, FireNetStatus* st) {
#if defined(_WIN32)
    u_long n = 0;
    *bytes = 0;
    if (ioctlsocket(FIRE_NET_FD(s), FIONREAD, &n) != 0) return fire_net_failErr(st, fire_net_lastError(), "ioctl");
    *bytes = (int)n;
#else
    int n = 0;
    *bytes = 0;
    if (ioctl(FIRE_NET_FD(s), FIONREAD, &n) != 0) return fire_net_failErr(st, fire_net_lastError(), "ioctl");
    *bytes = n;
#endif
    return 1;
}

static int fire_net_localAddress(FireNetSock s, char* host, size_t size, int* port, FireNetStatus* st) {
    struct sockaddr_storage ss;
    socklen_t len = sizeof ss;
    if (getsockname(FIRE_NET_FD(s), (struct sockaddr*)&ss, &len) != 0) return fire_net_failErr(st, fire_net_lastError(), "getsockname");
    fire_net_describe((struct sockaddr*)&ss, host, size, port);
    return 1;
}

static int fire_net_peerAddress(FireNetSock s, char* host, size_t size, int* port, FireNetStatus* st) {
    struct sockaddr_storage ss;
    socklen_t len = sizeof ss;
    if (getpeername(FIRE_NET_FD(s), (struct sockaddr*)&ss, &len) != 0) return fire_net_failErr(st, fire_net_lastError(), "getpeername");
    fire_net_describe((struct sockaddr*)&ss, host, size, port);
    return 1;
}

static int fire_net_setOption(FireNetSock s, int option, int value, FireNetStatus* st) {
    int level = SOL_SOCKET, name = 0;
    switch (option) {
        case 1: level = IPPROTO_TCP; name = TCP_NODELAY; break;
        case 2: name = SO_KEEPALIVE; break;
        case 3: name = SO_BROADCAST; break;
        case 4: name = SO_REUSEADDR; break;
        default: return fire_net_fail(st, FIRE_NET_E_INVALID_ARGUMENT, "Unknown socket option %d.", option);
    }
    if (setsockopt(FIRE_NET_FD(s), level, name, (const char*)&value, sizeof value) != 0) return fire_net_failErr(st, fire_net_lastError(), "setsockopt");
    return 1;
}

static int fire_net_shutdown(FireNetSock s, int how, FireNetStatus* st) {
#if defined(_WIN32)
    int h = how == 0 ? SD_RECEIVE : how == 1 ? SD_SEND : SD_BOTH;
#else
    int h = how == 0 ? SHUT_RD : how == 1 ? SHUT_WR : SHUT_RDWR;
#endif
    if (shutdown(FIRE_NET_FD(s), h) != 0) return fire_net_failErr(st, fire_net_lastError(), "shutdown");
    return 1;
}

#endif
