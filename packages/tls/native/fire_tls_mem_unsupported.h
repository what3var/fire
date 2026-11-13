/* fire tls package: TLS over buffers (fire_tls_c.h: fire_tls_clientOpenMem ...) for the TLS parts that do not have it yet (mbedTLS, SChannel, none):
 * opening fails with a status that says so. */
#ifndef FIRE_TLS_MEM_UNSUPPORTED_H
#define FIRE_TLS_MEM_UNSUPPORTED_H

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
