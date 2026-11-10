/* fire net package: network part in C for a platform without a network: every call fails with "not supported" (`fire_net_supported()` is 0). A board
 * package of your own provides `fire_net.h` (see fire_net_c.h for the list) with its own network stack; the lwIP of the ESP32 is covered by fire_net_sockets.h. */
#ifndef FIRE_NET_NONE_H
#define FIRE_NET_NONE_H

#include "fire_net_c.h"

typedef struct FireNetConnect { int unused; } FireNetConnect;

static int fire_net_supported(void) { return 0; }
static int fire_net_randomBytes(uint8_t* p, size_t n) { (void)p; (void)n; return 0; }
static int fire_net_none(FireNetStatus* st) { return fire_net_fail(st, FIRE_NET_E_UNSUPPORTED, "This platform has no network."); }

static int fire_net_resolve(const char* host, FireNetList* addresses, FireNetStatus* st) { (void)host; (void)addresses; return fire_net_none(st); }
static FireNetConnect* fire_net_connectBegin(const char* host, int port, FireNetStatus* st) { (void)host; (void)port; fire_net_none(st); return NULL; }
static int fire_net_connectStep(FireNetConnect* c, int* done, FireNetStatus* st) { (void)c; *done = 0; return fire_net_none(st); }
static FireNetSock fire_net_takeConnected(FireNetConnect* c) { (void)c; return FIRE_NET_INVALID; }
static void fire_net_freeConnect(FireNetConnect* c) { (void)c; }
static int fire_net_tcpListen(const char* host, int port, int backlog, FireNetSock* out, FireNetStatus* st) { (void)host; (void)port; (void)backlog; *out = FIRE_NET_INVALID; return fire_net_none(st); }
static int fire_net_acceptOne(FireNetSock l, int64_t timeoutMs, FireNetSock* out, FireNetStatus* st) { (void)l; (void)timeoutMs; *out = FIRE_NET_INVALID; return fire_net_none(st); }
static int fire_net_sendBytes(FireNetSock s, const uint8_t* data, int count, int64_t timeoutMs, int* sent, FireNetStatus* st) {
    (void)s; (void)data; (void)count; (void)timeoutMs; *sent = 0; return fire_net_none(st);
}
static int fire_net_recvBytes(FireNetSock s, uint8_t* data, int count, int64_t timeoutMs, int* got, FireNetStatus* st) {
    (void)s; (void)data; (void)count; (void)timeoutMs; *got = 0; return fire_net_none(st);
}
static int fire_net_udpOpen(const char* host, int port, FireNetSock* out, FireNetStatus* st) { (void)host; (void)port; *out = FIRE_NET_INVALID; return fire_net_none(st); }
static int fire_net_sendDatagram(FireNetSock s, const uint8_t* data, int count, const char* host, int port, int* sent, FireNetStatus* st) {
    (void)s; (void)data; (void)count; (void)host; (void)port; *sent = 0; return fire_net_none(st);
}
static int fire_net_recvDatagram(FireNetSock s, uint8_t* data, int count, int64_t timeoutMs, int* got, char* fromHost, size_t size, int* fromPort, FireNetStatus* st) {
    (void)s; (void)data; (void)count; (void)timeoutMs; (void)fromHost; (void)size; *got = 0; *fromPort = 0; return fire_net_none(st);
}
static int fire_net_waitFor(FireNetSock s, int wantRead, int wantWrite, int64_t timeoutMs, int* readable, int* writable, FireNetStatus* st) {
    (void)s; (void)wantRead; (void)wantWrite; (void)timeoutMs; *readable = *writable = 0; return fire_net_none(st);
}
static int fire_net_available(FireNetSock s, int* bytes, FireNetStatus* st) { (void)s; *bytes = 0; return fire_net_none(st); }
static int fire_net_localAddress(FireNetSock s, char* host, size_t size, int* port, FireNetStatus* st) { (void)s; (void)size; host[0] = 0; *port = 0; return fire_net_none(st); }
static int fire_net_peerAddress(FireNetSock s, char* host, size_t size, int* port, FireNetStatus* st) { (void)s; (void)size; host[0] = 0; *port = 0; return fire_net_none(st); }
static int fire_net_setOption(FireNetSock s, int option, int value, FireNetStatus* st) { (void)s; (void)option; (void)value; return fire_net_none(st); }
static int fire_net_shutdown(FireNetSock s, int how, FireNetStatus* st) { (void)s; (void)how; return fire_net_none(st); }
static void fire_net_close(FireNetSock s) { (void)s; }

#endif
