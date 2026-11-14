/* fire tls package: TLS part in C for a platform without TLS: every call fails with "not supported" (`fire_tls_supported()` is 0). Select it with
 * FIRE_NO_TLS in the target configuration (`defines`). The interface: fire_tls_c.h. */
#ifndef FIRE_TLS_NONE_H
#define FIRE_TLS_NONE_H

#include "fire_tls_c.h"

typedef struct FireTlsContext { int unused; } FireTlsContext;
typedef struct FireTlsSession { int unused; } FireTlsSession;

static int fire_tls_supported(void) { return 0; }
static int fire_tls_none(FireNetStatus* st) { return fire_net_fail(st, FIRE_NET_E_UNSUPPORTED, "This platform (or this build) has no TLS."); }

static FireTlsSession* fire_tls_clientOpen(FireNetSock fd, const FireTlsOptions* opt, FireNetStatus* st) { (void)fd; (void)opt; fire_tls_none(st); return NULL; }
static int fire_tls_handshakeStep(FireTlsSession* s, int* done, FireNetStatus* st) { (void)s; *done = 0; return fire_tls_none(st); }
static FireTlsContext* fire_tls_serverContext(const FireTlsOptions* opt, FireNetStatus* st) { (void)opt; fire_tls_none(st); return NULL; }
static void fire_tls_freeContext(FireTlsContext* c) { (void)c; }
static FireTlsSession* fire_tls_serverAccept(FireTlsContext* c, FireNetSock fd, FireNetStatus* st) { (void)c; (void)fd; fire_tls_none(st); return NULL; }
static int fire_tls_readSome(FireTlsSession* s, uint8_t* data, int count, int64_t timeoutMs, int* got, FireNetStatus* st) {
    (void)s; (void)data; (void)count; (void)timeoutMs; *got = 0; return fire_tls_none(st);
}
static int fire_tls_writeSome(FireTlsSession* s, const uint8_t* data, int count, int64_t timeoutMs, int* sent, FireNetStatus* st) {
    (void)s; (void)data; (void)count; (void)timeoutMs; *sent = 0; return fire_tls_none(st);
}
static int fire_tls_pending(FireTlsSession* s) { (void)s; return 0; }
static void fire_tls_info(FireTlsSession* s, char* protocol, size_t psize, char* cipher, size_t csize) { (void)s; (void)psize; (void)csize; protocol[0] = 0; cipher[0] = 0; }
static void fire_tls_closeSession(FireTlsSession* s) { (void)s; }

/* TLS over buffers (fire_tls_c.h: fire_tls_clientOpenMem ...): not here yet - opening fails with a status that says so. */
static FireTlsSession* fire_tls_clientOpenMem(const FireTlsOptions* opt, FireNetStatus* st) {
    (void)opt;
    fire_net_fail(st, FIRE_NET_E_UNSUPPORTED, "TLS over a buffer (Tls.Engine) is not supported by this TLS library yet (it is with OpenSSL).");
    return NULL;
}
static int fire_tls_memFeed(FireTlsSession* s, const uint8_t* data, int count, FireNetStatus* st) { (void)s; (void)data; (void)count; return fire_net_fail(st, FIRE_NET_E_UNSUPPORTED, "TLS over a buffer is not supported."); }
static int fire_tls_memDrain(FireTlsSession* s, uint8_t* out, int max) { (void)s; (void)out; (void)max; return 0; }
static int fire_tls_memWaiting(FireTlsSession* s) { (void)s; return 0; }
static int fire_tls_memRead(FireTlsSession* s, uint8_t* data, int count, int* got, FireNetStatus* st) { (void)s; (void)data; (void)count; *got = 0; return fire_net_fail(st, FIRE_NET_E_UNSUPPORTED, "TLS over a buffer is not supported."); }
static int fire_tls_memWrite(FireTlsSession* s, const uint8_t* data, int count, int* sent, FireNetStatus* st) { (void)s; (void)data; (void)count; *sent = 0; return fire_net_fail(st, FIRE_NET_E_UNSUPPORTED, "TLS over a buffer is not supported."); }

#endif
