/* fire native platform layer, network part in C (the HAL of the net bridge): what the implementations share - the error codes of the language, the status of an
 * operation, a list of addresses.
 *
 * A platform package offers `fire_net.h` (FIRE_PLATFORM_NET_C_HEADER points to it) with, all host names and addresses in UTF-8:
 *   typedef intptr_t FireNetSock; FIRE_NET_INVALID
 *   int  fire_net_supported(void)                               0: every call fails with FIRE_NET_E_UNSUPPORTED (a board without a network)
 *   int  fire_net_resolve(host, FireNetList*, FireNetStatus*)
 *   FireNetConnect* fire_net_connectBegin(host, port, FireNetStatus*) / int fire_net_connectStep(c, int* done, st) / FireNetSock fire_net_takeConnected(c)
 *        / void fire_net_freeConnect(c)                         a connection that is made without waiting
 *   int  fire_net_tcpListen(host, port, backlog, FireNetSock*, st)  host "" = every interface; port 0 = a free port (ask localAddress)
 *   int  fire_net_acceptOne(listener, timeoutMs, FireNetSock*, st)
 *   int  fire_net_sendBytes(sock, data, count, timeoutMs, int* sent, st)   waits (up to the timeout) until at least one byte can be sent
 *   int  fire_net_recvBytes(sock, data, count, timeoutMs, int* got, st)    got 0: the other side closed; FIRE_NET_E_TIMED_OUT when nothing came in time
 *   int  fire_net_udpOpen(host, port, FireNetSock*, st)          bound to host:port ("" and 0: any address, a free port)
 *   int  fire_net_sendDatagram(sock, data, count, host, port, int* sent, st)
 *   int  fire_net_recvDatagram(sock, data, count, timeoutMs, int* got, char* fromHost, size_t size, int* fromPort, st)
 *   int  fire_net_waitFor(sock, wantRead, wantWrite, timeoutMs, int* readable, int* writable, st)
 *   int  fire_net_available(sock, int* bytes, st)
 *   int  fire_net_localAddress / fire_net_peerAddress(sock, char* host, size_t size, int* port, st)
 *   int  fire_net_setOption(sock, option, value, st)    option: 1 NoDelay, 2 KeepAlive, 3 Broadcast, 4 ReuseAddress
 *   int  fire_net_shutdown(sock, how, st)                how: 0 receive, 1 send, 2 both
 *   void fire_net_close(sock)
 * The functions that return int give 1 when done and 0 on an error (the status says why). A board with a network stack of its own provides its own `fire_net.h`. */
#ifndef FIRE_NET_C_H
#define FIRE_NET_C_H

#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* The error codes of the language (the same numbers as in the prelude of the net package: Net.NetException.code). */
enum { FIRE_NET_E_NONE = 0, FIRE_NET_E_INVALID_ARGUMENT = 1, FIRE_NET_E_INVALID_HANDLE = 2, FIRE_NET_E_REFUSED = 3, FIRE_NET_E_TIMED_OUT = 4, FIRE_NET_E_UNREACHABLE = 5,
       FIRE_NET_E_ADDRESS_IN_USE = 6, FIRE_NET_E_CLOSED = 7, FIRE_NET_E_DENIED = 8, FIRE_NET_E_UNSUPPORTED = 9, FIRE_NET_E_RESOLVE_FAILED = 10, FIRE_NET_E_OTHER = 11,
       FIRE_NET_E_NOT_CONNECTED = 12, FIRE_NET_E_PERMISSION = 13 };

typedef struct FireNetStatus { int code; char message[176]; } FireNetStatus;

typedef intptr_t FireNetSock;
#define FIRE_NET_INVALID ((FireNetSock)-1)

/* Records a failure (printf format): always 0. */
static int fire_net_fail(FireNetStatus* st, int code, const char* format, ...) {
    va_list args;
    st->code = code;
    va_start(args, format);
    vsnprintf(st->message, sizeof st->message, format, args);
    va_end(args);
    return 0;
}

/* Copies a text into `out` (room for `size` bytes), cut when it is longer. */
static void fire_net_copy(char* out, size_t size, const char* text) {
    size_t n = strlen(text);
    if (size == 0) return;
    if (n >= size) n = size - 1;
    memcpy(out, text, n);
    out[n] = 0;
}

/* ---- a list of addresses (text) ---- */
typedef struct FireNetList { char** items; size_t count, cap; } FireNetList;
static void fire_net_listAdd(FireNetList* l, const char* s) {
    size_t n = strlen(s);
    char* copy;
    if (l->count == l->cap) {
        size_t cap = l->cap ? l->cap * 2 : 8;
        char** grown = (char**)realloc(l->items, sizeof(char*) * cap);
        if (grown == NULL) return;
        l->items = grown;
        l->cap = cap;
    }
    copy = (char*)malloc(n + 1);
    if (copy == NULL) return;
    memcpy(copy, s, n + 1);
    l->items[l->count++] = copy;
}
static void fire_net_listFree(FireNetList* l) {
    size_t i;
    for (i = 0; i < l->count; i++) free(l->items[i]);
    free(l->items);
    l->items = NULL;
    l->count = l->cap = 0;
}
static int fire_net_listHas(const FireNetList* l, const char* s) {
    size_t i;
    for (i = 0; i < l->count; i++) if (strcmp(l->items[i], s) == 0) return 1;
    return 0;
}

#endif
