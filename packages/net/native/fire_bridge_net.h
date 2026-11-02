/* fire native bridge "net" (C): the natives behind `#import "net"` (docs/NETWORK.md) - TCP connections and listeners, UDP sockets, name resolution. The fire side
 * (Net.TcpClient, Net.TcpListener, Net.UdpSocket, Net.Dns, ... - fire source) is the same in the VM and in a native build; this file is what its `__Net...`
 * functions do.
 *
 * This file is the one implementation of the net natives, in C99 ("language": "c"): the native build puts it into the generated program, and the virtual machine
 * runs it in a shared library built from it (native/abi/fire_pkg_abi.h). The sockets come from the platform layer (FIRE_PLATFORM_NET_C_HEADER: fire_net_*, see
 * platform/std/fire_net_c.h). A socket is an integer handle in a table of this bridge (like the streams of io); handles are not used again. Every function
 * reports an error the same way: a result of -1/false/undefined and the code and message of this thread (`__NetLastError`, `__NetLastErrorMessage`, the slot of
 * the io bridge), from which the fire code throws a typed exception.
 *
 * Time limits: every call that waits takes a timeout in milliseconds (< 0: no limit) and never blocks longer. The fire code asks with a timeout of 0 and sleeps a
 * moment between the attempts (it does not hold the library while it waits), so a program stays abortable (`terminate`, threads, the main queue) while it waits
 * for the network.
 *
 * What the host decides (which hosts and ports a script may talk to) differs by build:
 *   - a native build: everything is allowed unless the target defines FIRE_NET_POLICY (a function `int(const char* host, int port, int access, char* reason,
 *     size_t size)`, 1 allowed; access bits 1 Connect (also: send a datagram), 2 Listen (also: bind a UDP socket), 4 Resolve; define it before the includes in
 *     the target configuration);
 *   - in the library for the VM: the host (the editor, the runtime) is asked through the callback `net_allow` of the ABI (`fire_host`). */
#ifndef FIRE_BRIDGE_NET_H
#define FIRE_BRIDGE_NET_H

#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include FIRE_PLATFORM_NET_C_HEADER

enum { FIRE_NET_A_CONNECT = 1, FIRE_NET_A_LISTEN = 2, FIRE_NET_A_RESOLVE = 4 };
enum { FIRE_NET_K_TCP = 1, FIRE_NET_K_LISTENER = 2, FIRE_NET_K_UDP = 3, FIRE_NET_K_CONNECTING = 4 };

static int64_t fire_net_error(int code, const char* message) {
    g_ioError.code = code;
    snprintf(g_ioError.message, sizeof g_ioError.message, "%s", message);
    return -1;
}
static int64_t fire_net_errorStatus(const FireNetStatus* s) { return fire_net_error(s->code, s->message); }
static void fire_net_ok(void) { g_ioError.code = 0; g_ioError.message[0] = 0; }

/* ---- text ---------------------------------------------------------------------------------------------------------------------------------- */
/* A fire string as UTF-8 (malloc; the caller frees it; NULL when out of memory); a lone surrogate becomes U+FFFD. */
static char* fire_net_utf8(Value v) {
    const Str* s;
    char* out;
    size_t n = 0;
    uint32_t i;
    if (v.kind != K_String) { out = (char*)malloc(1); if (out) out[0] = 0; return out; }
    s = strOf(v);
    out = (char*)malloc((size_t)s->length * 3 + 1);
    if (out == NULL) return NULL;
    for (i = 0; i < s->length; i++) {
        uint32_t c = s->data[i];
        if (c >= 0xD800 && c <= 0xDBFF && i + 1 < s->length && s->data[i + 1] >= 0xDC00 && s->data[i + 1] <= 0xDFFF) { c = 0x10000 + ((c - 0xD800) << 10) + (s->data[i + 1] - 0xDC00); i++; }
        else if (c >= 0xD800 && c <= 0xDFFF) c = 0xFFFD;
        if (c < 0x80) out[n++] = (char)c;
        else if (c < 0x800) { out[n++] = (char)(0xC0 | (c >> 6)); out[n++] = (char)(0x80 | (c & 0x3F)); }
        else if (c < 0x10000) { out[n++] = (char)(0xE0 | (c >> 12)); out[n++] = (char)(0x80 | ((c >> 6) & 0x3F)); out[n++] = (char)(0x80 | (c & 0x3F)); }
        else { out[n++] = (char)(0xF0 | (c >> 18)); out[n++] = (char)(0x80 | ((c >> 12) & 0x3F)); out[n++] = (char)(0x80 | ((c >> 6) & 0x3F)); out[n++] = (char)(0x80 | (c & 0x3F)); }
    }
    out[n] = 0;
    return out;
}
/* (host names and messages are ASCII or UTF-8 from the system; a byte above 127 becomes the Latin-1 character of the same number) */
static Value fire_net_str8(const char* text, OwnList* list) {
    size_t n = strlen(text), i;
    Str* s = allocStr((uint32_t)n, list);
    for (i = 0; i < n; i++) strChars(s)[i] = (fire_char16)(uint8_t)text[i];
    return StrV(s);
}

/* ---- the sockets ----------------------------------------------------------------------------------------------------------------------------- */
typedef struct FireNetSocket {
    FireNetSock fd;
    int kind;
    char peerHost[96];
    int peerPort;
    FireNetConnect* connecting;   /* FIRE_NET_K_CONNECTING: the attempts that are going on */
} FireNetSocket;

static void fire_net_release(FireNetSocket* s) {
    if (s == NULL) return;
    fire_net_close(s->fd);
    if (s->connecting) fire_net_freeConnect(s->connecting);
    free(s);
}

/* The open sockets; the handle is the index (0 is not used). What the script left open is closed when the program ends (the safety net of the VM's host). */
static FireNetSocket** g_netSockets = NULL;
static size_t g_netSocketCount = 1, g_netSocketCap = 0;

/* The program has ended (the library for the VM stays loaded for the next one): closes what it left open. */
static void fire_net_reset(void) {
    size_t i;
    for (i = 1; i < g_netSocketCount; i++) {
        fire_net_release(g_netSockets[i]);
        g_netSockets[i] = NULL;
    }
}

static FireNetSocket* fire_net_newSocket(int kind) {
    FireNetSocket* s = (FireNetSocket*)calloc(1, sizeof(FireNetSocket));
    if (s == NULL) return NULL;
    s->fd = FIRE_NET_INVALID;
    s->kind = kind;
    return s;
}
static int64_t fire_net_add(FireNetSocket* s) {
    if (g_netSocketCount >= g_netSocketCap) {
        size_t cap = g_netSocketCap ? g_netSocketCap * 2 : 16;
        FireNetSocket** grown = (FireNetSocket**)realloc(g_netSockets, sizeof(FireNetSocket*) * cap);
        if (grown == NULL) { fire_net_release(s); return fire_net_error(FIRE_NET_E_OTHER, "Out of memory."); }
        if (g_netSocketCap == 0) grown[0] = NULL;
        g_netSockets = grown;
        g_netSocketCap = cap;
    }
    g_netSockets[g_netSocketCount] = s;
    return (int64_t)g_netSocketCount++;
}
/* A new socket of `kind` around `fd`, in the table: the handle (-1: out of memory, `fd` is closed). */
static int64_t fire_net_addFd(FireNetSock fd, int kind) {
    FireNetSocket* s = fire_net_newSocket(kind);
    if (s == NULL) { fire_net_close(fd); return fire_net_error(FIRE_NET_E_OTHER, "Out of memory."); }
    s->fd = fd;
    return fire_net_add(s);
}
static FireNetSocket* fire_net_find(Value h) {
    int64_t i = h.i;
    if (i > 0 && (size_t)i < g_netSocketCount && g_netSockets[i] != NULL) return g_netSockets[i];
    fire_net_error(FIRE_NET_E_INVALID_HANDLE, "Invalid or already closed socket handle.");
    return NULL;
}
static FireNetSocket* fire_net_findKind(Value h, int kind, const char* what) {
    FireNetSocket* s = fire_net_find(h);
    char text[96];
    if (s == NULL) return NULL;
    if (s->kind == kind) return s;
    snprintf(text, sizeof text, "This socket is not %s.", what);
    fire_net_error(FIRE_NET_E_INVALID_ARGUMENT, text);
    return NULL;
}
static int fire_net_checkRange(Value bufferValue, int64_t offset, int64_t count) {
    int64_t length = bufOf(bufferValue)->length;
    if (offset < 0 || count < 0 || offset > length || count > length - offset) {
        char text[176];
        snprintf(text, sizeof text, "offset/count (%lld/%lld) are outside of the buffer (length %lld).", (long long)offset, (long long)count, (long long)length);
        fire_net_error(FIRE_NET_E_INVALID_ARGUMENT, text);
        return 0;
    }
    return 1;
}
static int fire_net_checkPort(int64_t port, int allowZero) {
    if (port < (allowZero ? 0 : 1) || port > 65535) {
        char text[96];
        snprintf(text, sizeof text, "Invalid port %lld (1..65535%s).", (long long)port, allowZero ? ", 0 = any" : "");
        fire_net_error(FIRE_NET_E_INVALID_ARGUMENT, text);
        return 0;
    }
    return 1;
}
/* The bytes of a buffer from `offset` (the range is checked). */
static uint8_t* fire_net_bytes(Value buffer, int64_t offset) { return bufOf(buffer)->data + offset; }

/* ---- the host: which hosts and ports a script may talk to ------------------------------------------------------------------------------------ */
static int fire_net_allowed(const char* host, int port, int access, char* reason, size_t size) {
#if defined(FIRE_LIBRARY)
    const fire_host* h = libraryHost();
    if (h == NULL || h->size < (int32_t)(offsetof(fire_host, net_allow) + sizeof(h->net_allow)) || h->net_allow == NULL) return 1;
    return h->net_allow(host, port, access, reason, (int)size) ? 1 : 0;
#elif defined(FIRE_NET_POLICY)
    return FIRE_NET_POLICY(host, port, access, reason, size);
#else
    (void)host; (void)port; (void)access; (void)reason; (void)size;
    return 1;
#endif
}
static int fire_net_authorize(const char* host, int port, int access) {
    char reason[256];
    char text[176];
    reason[0] = 0;
    if (fire_net_allowed(host, port, access, reason, sizeof reason)) return 1;
    if (reason[0]) { fire_net_error(FIRE_NET_E_DENIED, reason); return 0; }
    if (port > 0) snprintf(text, sizeof text, "Network access to '%s:%d' is not allowed.", host[0] ? host : "*", port);
    else snprintf(text, sizeof text, "Network access to '%s' is not allowed.", host[0] ? host : "*");
    fire_net_error(FIRE_NET_E_DENIED, text);
    return 0;
}
/* The host of a call as UTF-8 (malloc), NULL with the error set when it is empty (and `mustHave`) or the memory is gone. */
static char* fire_net_host(Value host, int mustHave) {
    char* h = fire_net_utf8(host);
    if (h == NULL) { fire_net_error(FIRE_NET_E_OTHER, "Out of memory."); return NULL; }
    if (mustHave && h[0] == 0) { free(h); fire_net_error(FIRE_NET_E_INVALID_ARGUMENT, "The host is empty."); return NULL; }
    return h;
}

/* ---- natives --------------------------------------------------------------------------------------------------------------------------------- */
static Value fire_net_LastError(void) { return Int(g_ioError.code); }
static Value fire_net_LastErrorMessage(OwnList* list) { return fire_net_str8(g_ioError.message, list); }
static Value fire_net_OpenCount(void) {
    int64_t n = 0;
    size_t i;
    for (i = 1; i < g_netSocketCount; i++) if (g_netSockets[i] != NULL) n++;
    return Int(n);
}
/* The number the operating system gives the socket (for the tls package, which works on the socket itself); -1 on an error. Whoever uses it must not close
 * it: the socket belongs to this bridge. */
static Value fire_net_NativeHandle(Value h) {
    FireNetSocket* s = fire_net_find(h);
    if (s == NULL) return Int(-1);
    fire_net_ok();
    return Int((int64_t)s->fd);
}
/* 1 if the platform has a network, else 0. */
static Value fire_net_Supported(void) { return Int(fire_net_supported() ? 1 : 0); }

/* Starts a connection to host:port (the name is resolved now, which can take a moment; the connecting itself does not wait): the result is a handle that
 * `ConnectStep` makes a connection; -1 on an error. */
static Value fire_net_ConnectBegin(Value host, Value port) {
    FireNetStatus st;
    FireNetConnect* c;
    FireNetSocket* s;
    char* h;
    if (!fire_net_checkPort(port.i, 0)) return Int(-1);
    h = fire_net_host(host, 1);
    if (h == NULL) return Int(-1);
    if (!fire_net_authorize(h, (int)port.i, FIRE_NET_A_CONNECT)) { free(h); return Int(-1); }
    c = fire_net_connectBegin(h, (int)port.i, &st);
    free(h);
    if (c == NULL) return Int(fire_net_errorStatus(&st));
    s = fire_net_newSocket(FIRE_NET_K_CONNECTING);
    if (s == NULL) { fire_net_freeConnect(c); return Int(fire_net_error(FIRE_NET_E_OTHER, "Out of memory.")); }
    s->connecting = c;
    fire_net_ok();
    return Int(fire_net_add(s));
}

/* Asks without waiting: 1 the connection is made (the handle is a connection now), 0 not yet, -1 no address could be connected (the handle stays until it is
 * closed). */
static Value fire_net_ConnectStep(Value h) {
    FireNetSocket* s = fire_net_findKind(h, FIRE_NET_K_CONNECTING, "being connected");
    FireNetStatus st;
    int done = 0;
    if (s == NULL) return Int(-1);
    if (!fire_net_connectStep(s->connecting, &done, &st)) return Int(fire_net_errorStatus(&st));
    fire_net_ok();
    if (!done) return Int(0);
    s->fd = fire_net_takeConnected(s->connecting);
    fire_net_freeConnect(s->connecting);
    s->connecting = NULL;
    s->kind = FIRE_NET_K_TCP;
    if (!fire_net_peerAddress(s->fd, s->peerHost, sizeof s->peerHost, &s->peerPort, &st)) { s->peerHost[0] = 0; s->peerPort = 0; }
    return Int(1);
}

static Value fire_net_TcpListen(Value host, Value port, Value backlog) {
    FireNetStatus st;
    FireNetSock fd;
    char* h;
    int done;
    if (!fire_net_checkPort(port.i, 1)) return Int(-1);
    h = fire_net_host(host, 0);
    if (h == NULL) return Int(-1);
    if (!fire_net_authorize(h, (int)port.i, FIRE_NET_A_LISTEN)) { free(h); return Int(-1); }
    done = fire_net_tcpListen(h, (int)port.i, backlog.i > 0 ? (int)backlog.i : 16, &fd, &st);
    free(h);
    if (!done) return Int(fire_net_errorStatus(&st));
    fire_net_ok();
    return Int(fire_net_addFd(fd, FIRE_NET_K_LISTENER));
}

static Value fire_net_Accept(Value h, Value timeoutMs) {
    FireNetSocket* l = fire_net_findKind(h, FIRE_NET_K_LISTENER, "a listener");
    FireNetStatus st;
    FireNetSock fd;
    int64_t handle;
    if (l == NULL) return Int(-1);
    if (!fire_net_acceptOne(l->fd, timeoutMs.i, &fd, &st)) return Int(fire_net_errorStatus(&st));
    fire_net_ok();
    handle = fire_net_addFd(fd, FIRE_NET_K_TCP);
    if (handle > 0) {
        FireNetSocket* s = g_netSockets[handle];
        if (!fire_net_peerAddress(fd, s->peerHost, sizeof s->peerHost, &s->peerPort, &st)) { s->peerHost[0] = 0; s->peerPort = 0; }
    }
    return Int(handle);
}

/* Sends up to `count` bytes (waiting up to the timeout until the system takes at least one); the number sent, -1 on an error. */
static Value fire_net_Send(Value h, Value buffer, Value offset, Value count, Value timeoutMs) {
    FireNetSocket* s;
    FireNetStatus st;
    int sent = 0;
    if (!leafAlive(buffer)) return destroyedError(buffer);
    if (!fire_net_checkRange(buffer, offset.i, count.i)) return Int(-1);
    s = fire_net_findKind(h, FIRE_NET_K_TCP, "a connection");
    if (s == NULL) return Int(-1);
    if (!fire_net_sendBytes(s->fd, fire_net_bytes(buffer, offset.i), (int)count.i, timeoutMs.i, &sent, &st)) return Int(fire_net_errorStatus(&st));
    fire_net_ok();
    return Int(sent);
}

/* Receives up to `count` bytes: the number (0: the other side closed the connection), -1 on an error (TimedOut: nothing came in time). */
static Value fire_net_Recv(Value h, Value buffer, Value offset, Value count, Value timeoutMs) {
    FireNetSocket* s;
    FireNetStatus st;
    int got = 0;
    if (!leafAlive(buffer)) return destroyedError(buffer);
    if (!fire_net_checkRange(buffer, offset.i, count.i)) return Int(-1);
    s = fire_net_findKind(h, FIRE_NET_K_TCP, "a connection");
    if (s == NULL) return Int(-1);
    if (!fire_net_recvBytes(s->fd, fire_net_bytes(buffer, offset.i), (int)count.i, timeoutMs.i, &got, &st)) return Int(fire_net_errorStatus(&st));
    fire_net_ok();
    return Int(got);
}

static Value fire_net_UdpOpen(Value host, Value port) {
    FireNetStatus st;
    FireNetSock fd;
    char* h;
    int done;
    if (!fire_net_checkPort(port.i, 1)) return Int(-1);
    h = fire_net_host(host, 0);
    if (h == NULL) return Int(-1);
    if (!fire_net_authorize(h, (int)port.i, FIRE_NET_A_LISTEN)) { free(h); return Int(-1); }
    done = fire_net_udpOpen(h, (int)port.i, &fd, &st);
    free(h);
    if (!done) return Int(fire_net_errorStatus(&st));
    fire_net_ok();
    return Int(fire_net_addFd(fd, FIRE_NET_K_UDP));
}

static Value fire_net_SendTo(Value h, Value buffer, Value offset, Value count, Value host, Value port) {
    FireNetSocket* s;
    FireNetStatus st;
    char* to;
    int sent = 0, done;
    if (!leafAlive(buffer)) return destroyedError(buffer);
    if (!fire_net_checkRange(buffer, offset.i, count.i)) return Int(-1);
    if (!fire_net_checkPort(port.i, 0)) return Int(-1);
    s = fire_net_findKind(h, FIRE_NET_K_UDP, "a datagram socket");
    if (s == NULL) return Int(-1);
    to = fire_net_host(host, 1);
    if (to == NULL) return Int(-1);
    if (!fire_net_authorize(to, (int)port.i, FIRE_NET_A_CONNECT)) { free(to); return Int(-1); }
    done = fire_net_sendDatagram(s->fd, fire_net_bytes(buffer, offset.i), (int)count.i, to, (int)port.i, &sent, &st);
    free(to);
    if (!done) return Int(fire_net_errorStatus(&st));
    fire_net_ok();
    return Int(sent);
}

/* Receives one datagram (a longer one is cut to `count`); the sender is then `PeerHost`/`PeerPort` of the socket. */
static Value fire_net_RecvFrom(Value h, Value buffer, Value offset, Value count, Value timeoutMs) {
    FireNetSocket* s;
    FireNetStatus st;
    char from[96];
    int got = 0, fromPort = 0;
    if (!leafAlive(buffer)) return destroyedError(buffer);
    if (!fire_net_checkRange(buffer, offset.i, count.i)) return Int(-1);
    s = fire_net_findKind(h, FIRE_NET_K_UDP, "a datagram socket");
    if (s == NULL) return Int(-1);
    if (!fire_net_recvDatagram(s->fd, fire_net_bytes(buffer, offset.i), (int)count.i, timeoutMs.i, &got, from, sizeof from, &fromPort, &st)) return Int(fire_net_errorStatus(&st));
    snprintf(s->peerHost, sizeof s->peerHost, "%s", from);
    s->peerPort = fromPort;
    fire_net_ok();
    return Int(got);
}

static Value fire_net_PeerHost(Value h, OwnList* list) {
    FireNetSocket* s = fire_net_find(h);
    if (s == NULL) return Undef();
    fire_net_ok();
    return fire_net_str8(s->peerHost, list);
}
static Value fire_net_PeerPort(Value h) {
    FireNetSocket* s = fire_net_find(h);
    if (s == NULL) return Int(-1);
    fire_net_ok();
    return Int(s->peerPort);
}
static Value fire_net_LocalHost(Value h, OwnList* list) {
    FireNetSocket* s = fire_net_find(h);
    FireNetStatus st;
    char host[96];
    int port;
    if (s == NULL) return Undef();
    if (!fire_net_localAddress(s->fd, host, sizeof host, &port, &st)) { fire_net_errorStatus(&st); return Undef(); }
    fire_net_ok();
    return fire_net_str8(host, list);
}
static Value fire_net_LocalPort(Value h) {
    FireNetSocket* s = fire_net_find(h);
    FireNetStatus st;
    char host[96];
    int port;
    if (s == NULL) return Int(-1);
    if (!fire_net_localAddress(s->fd, host, sizeof host, &port, &st)) return Int(fire_net_errorStatus(&st));
    fire_net_ok();
    return Int(port);
}

/* Bytes that can be read without waiting (a datagram socket: the size of the next datagram); -1 on an error. */
static Value fire_net_Available(Value h) {
    FireNetSocket* s = fire_net_find(h);
    FireNetStatus st;
    int n = 0;
    if (s == NULL) return Int(-1);
    if (!fire_net_available(s->fd, &n, &st)) return Int(fire_net_errorStatus(&st));
    fire_net_ok();
    return Int(n);
}

/* Waits up to the timeout: the result has bit 1 if the socket can be read (a listener: a connection is waiting; a connection: data or the end came in), bit 2 if
 * it can be written. */
static Value fire_net_Poll(Value h, Value wantRead, Value wantWrite, Value timeoutMs) {
    FireNetSocket* s = fire_net_find(h);
    FireNetStatus st;
    int r = 0, w = 0;
    if (s == NULL) return Int(-1);
    if (!fire_net_waitFor(s->fd, wantRead.i != 0, wantWrite.i != 0, timeoutMs.i, &r, &w, &st)) return Int(fire_net_errorStatus(&st));
    fire_net_ok();
    return Int((r ? 1 : 0) | (w ? 2 : 0));
}

static Value fire_net_SetOption(Value h, Value option, Value value) {
    FireNetSocket* s = fire_net_find(h);
    FireNetStatus st;
    if (s == NULL) return Bool(false);
    if (!fire_net_setOption(s->fd, (int)option.i, (int)value.i, &st)) { fire_net_errorStatus(&st); return Bool(false); }
    fire_net_ok();
    return Bool(true);
}

static Value fire_net_Shutdown(Value h, Value how) {
    FireNetSocket* s = fire_net_findKind(h, FIRE_NET_K_TCP, "a connection");
    FireNetStatus st;
    if (s == NULL) return Bool(false);
    if (how.i < 0 || how.i > 2) { fire_net_error(FIRE_NET_E_INVALID_ARGUMENT, "Invalid shutdown direction."); return Bool(false); }
    if (!fire_net_shutdown(s->fd, (int)how.i, &st)) { fire_net_errorStatus(&st); return Bool(false); }
    fire_net_ok();
    return Bool(true);
}

static Value fire_net_Close(Value h) {
    int64_t i = h.i;
    if (i <= 0 || (size_t)i >= g_netSocketCount || g_netSockets[i] == NULL) { fire_net_error(FIRE_NET_E_INVALID_HANDLE, "Invalid or already closed socket handle."); return Bool(false); }
    fire_net_release(g_netSockets[i]);
    g_netSockets[i] = NULL;
    fire_net_ok();
    return Bool(true);
}

/* The addresses of a name (IPv4 and IPv6, as text); a literal address gives itself. */
static Value fire_net_Resolve(Value host, OwnList* list) {
    FireNetStatus st;
    FireNetList addresses = { NULL, 0, 0 };
    Arr* a;
    size_t i;
    char* h = fire_net_host(host, 1);
    if (h == NULL) return Undef();
    if (!fire_net_authorize(h, 0, FIRE_NET_A_RESOLVE)) { free(h); return Undef(); }
    if (!fire_net_resolve(h, &addresses, &st)) { free(h); fire_net_listFree(&addresses); fire_net_errorStatus(&st); return Undef(); }
    free(h);
    fire_net_ok();
    a = allocArr((uint32_t)addresses.count, list);
    for (i = 0; i < addresses.count; i++) { arrItems(a)[i] = fire_net_str8(addresses.items[i], list); retain(arrItems(a)[i]); }
    fire_net_listFree(&addresses);
    return ArrV(a);
}

#endif
