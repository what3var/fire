/* fire native platform layer, TLS part in C on mbedTLS (2.28 and 3.x): the ESP32 (mbedTLS comes with ESP-IDF), or any system with mbedTLS (define FIRE_TLS_MBEDTLS
 * to choose it on a desktop; link mbedtls, mbedx509 and mbedcrypto). The interface: fire_tls_c.h.
 *
 * Certificates: a client verifies against `caPem` / `caFile` (the file needs MBEDTLS_FS_IO) or, without one of them, against the store that the board provides:
 * define FIRE_TLS_CA_ATTACH(conf) in the target configuration (on the ESP32 `esp_crt_bundle_attach` of ESP-IDF's certificate bundle:
 * `#define FIRE_TLS_CA_ATTACH(conf) esp_crt_bundle_attach(conf)` and include "esp_crt_bundle.h"). Without any CA the handshake is refused with a message that
 * says so (set `verify` to false only for tests). */
#ifndef FIRE_TLS_MBEDTLS_H
#define FIRE_TLS_MBEDTLS_H

#include <mbedtls/ctr_drbg.h>
#include <mbedtls/entropy.h>
#include <mbedtls/error.h>
#include <mbedtls/pk.h>
#include <mbedtls/ssl.h>
#include <mbedtls/version.h>
#include <mbedtls/x509_crt.h>

#include "fire_tls_c.h"

/* What a server (or a client with a certificate) needs for every connection: kept as long as the connections live. */
typedef struct FireTlsContext {
    mbedtls_ssl_config conf;
    mbedtls_x509_crt cert;
    mbedtls_pk_context key;
    mbedtls_entropy_context entropy;
    mbedtls_ctr_drbg_context drbg;
} FireTlsContext;

typedef struct FireTlsSession {
    mbedtls_ssl_context ssl;
    FireTlsContext* shared;   /* the server's context (not owned) */
    FireTlsContext* own;      /* a client session has a context of its own */
    mbedtls_x509_crt ca;
    FireNetSock fd;
    int eof;
} FireTlsSession;

static int fire_tls_supported(void) { return 1; }

static FireTlsContext* fire_tls_newContext(void) {
    FireTlsContext* c = (FireTlsContext*)calloc(1, sizeof(FireTlsContext));
    if (c == NULL) return NULL;
    mbedtls_ssl_config_init(&c->conf);
    mbedtls_x509_crt_init(&c->cert);
    mbedtls_pk_init(&c->key);
    mbedtls_entropy_init(&c->entropy);
    mbedtls_ctr_drbg_init(&c->drbg);
    return c;
}
static void fire_tls_freeContext(FireTlsContext* c) {
    if (c == NULL) return;
    mbedtls_ssl_config_free(&c->conf);
    mbedtls_x509_crt_free(&c->cert);
    mbedtls_pk_free(&c->key);
    mbedtls_ctr_drbg_free(&c->drbg);
    mbedtls_entropy_free(&c->entropy);
    free(c);
}
static FireTlsSession* fire_tls_newSession(FireNetSock fd) {
    FireTlsSession* s = (FireTlsSession*)calloc(1, sizeof(FireTlsSession));
    if (s == NULL) return NULL;
    mbedtls_ssl_init(&s->ssl);
    mbedtls_x509_crt_init(&s->ca);
    s->fd = fd;
    return s;
}
static void fire_tls_freeSession(FireTlsSession* s) {
    if (s == NULL) return;
    mbedtls_ssl_free(&s->ssl);
    mbedtls_x509_crt_free(&s->ca);
    fire_tls_freeContext(s->own);
    free(s);
}

static void fire_tls_describe(int code, char* out, size_t size) { mbedtls_strerror(code, out, size); }
/* "<what>: <text of mbedTLS>" with `code`: always 0. */
static int fire_tls_failMbed(FireNetStatus* st, int code, const char* what, int ret) {
    char text[160];
    fire_tls_describe(ret, text, sizeof text);
    return fire_net_fail(st, code, "%s%s", what, text);
}

/* the socket callbacks of mbedTLS: non-blocking, so "nothing now" is WANT_READ / WANT_WRITE */
static int fire_tls_sendCb(void* ctx, const unsigned char* buf, size_t len) {
    FireTlsSession* s = (FireTlsSession*)ctx;
    FireNetStatus st;
    int sent = 0;
    if (fire_net_sendBytes(s->fd, buf, (int)len, 0, &sent, &st)) return sent;
    if (st.code == FIRE_NET_E_TIMED_OUT) return MBEDTLS_ERR_SSL_WANT_WRITE;
    return MBEDTLS_ERR_SSL_INTERNAL_ERROR;
}
static int fire_tls_recvCb(void* ctx, unsigned char* buf, size_t len) {
    FireTlsSession* s = (FireTlsSession*)ctx;
    FireNetStatus st;
    int got = 0;
    if (fire_net_recvBytes(s->fd, buf, (int)len, 0, &got, &st)) { if (got == 0) { s->eof = 1; return 0; } return got; }
    if (st.code == FIRE_NET_E_TIMED_OUT) return MBEDTLS_ERR_SSL_WANT_READ;
    return MBEDTLS_ERR_SSL_INTERNAL_ERROR;
}

static int fire_tls_wait(FireTlsSession* s, int ret, int64_t deadline, FireNetStatus* st) {
    int64_t left = deadline < 0 ? -1 : deadline - fire_net_nowMillis();
    int r, w;
    if (deadline >= 0 && left < 0) left = 0;
    if (!fire_net_waitFor(s->fd, ret == MBEDTLS_ERR_SSL_WANT_READ, ret == MBEDTLS_ERR_SSL_WANT_WRITE, left, &r, &w, st)) return 0;
    if (!r && !w) return fire_net_fail(st, FIRE_NET_E_TIMED_OUT, "The TLS operation timed out.");
    return 1;
}

static int fire_tls_seedRandom(FireTlsContext* c, FireNetStatus* st) {
    const char* pers = "fire-tls";
    int ret = mbedtls_ctr_drbg_seed(&c->drbg, mbedtls_entropy_func, &c->entropy, (const unsigned char*)pers, strlen(pers));
    if (ret != 0) return fire_tls_failMbed(st, FIRE_TLS_E_TLS, "TLS could not be started: ", ret);
    mbedtls_ssl_conf_rng(&c->conf, mbedtls_ctr_drbg_random, &c->drbg);
    return 1;
}

static void fire_tls_setMinVersion(mbedtls_ssl_config* conf) {
#if MBEDTLS_VERSION_MAJOR >= 3
    mbedtls_ssl_conf_min_tls_version(conf, MBEDTLS_SSL_VERSION_TLS1_2);
#else
    mbedtls_ssl_conf_min_version(conf, MBEDTLS_SSL_MAJOR_VERSION_3, MBEDTLS_SSL_MINOR_VERSION_3);
#endif
}

/* (mbedTLS wants the terminating zero of PEM data in the length) */
static size_t fire_tls_pemLength(const char* pem) { return strlen(pem) + 1; }

static int fire_tls_parseKey(mbedtls_pk_context* key, const char* pem, FireTlsContext* c) {
#if MBEDTLS_VERSION_MAJOR >= 3
    return mbedtls_pk_parse_key(key, (const unsigned char*)pem, fire_tls_pemLength(pem), NULL, 0, mbedtls_ctr_drbg_random, &c->drbg);
#else
    (void)c;
    return mbedtls_pk_parse_key(key, (const unsigned char*)pem, fire_tls_pemLength(pem), NULL, 0);
#endif
}

static int fire_tls_loadIdentity(FireTlsContext* c, const FireTlsOptions* opt, FireNetStatus* st) {
    int ret = mbedtls_x509_crt_parse(&c->cert, (const unsigned char*)opt->certPem, fire_tls_pemLength(opt->certPem));
    if (ret != 0) return fire_tls_failMbed(st, FIRE_NET_E_INVALID_ARGUMENT, "The certificate (PEM) is not valid: ", ret);
    ret = fire_tls_parseKey(&c->key, opt->keyPem, c);
    if (ret != 0) return fire_tls_failMbed(st, FIRE_NET_E_INVALID_ARGUMENT, "The private key (PEM) is not valid: ", ret);
    ret = mbedtls_ssl_conf_own_cert(&c->conf, &c->cert, &c->key);
    if (ret != 0) return fire_tls_failMbed(st, FIRE_NET_E_INVALID_ARGUMENT, "The certificate and the key cannot be used: ", ret);
    return 1;
}

static int fire_tls_certificateFailure(FireTlsSession* s, int ret, const char* what, FireNetStatus* st) {
    if (ret == MBEDTLS_ERR_X509_CERT_VERIFY_FAILED) {
        uint32_t flags = mbedtls_ssl_get_verify_result(&s->ssl);
        char buf[512];
        size_t n;
        buf[0] = 0;
        if (flags != 0 && flags != 0xFFFFFFFFu) mbedtls_x509_crt_verify_info(buf, sizeof buf, "", flags);
        n = strlen(buf);
        while (n > 0 && (buf[n - 1] == '\n' || buf[n - 1] == ' ')) buf[--n] = 0;
        if (n == 0) return fire_net_fail(st, FIRE_TLS_E_CERTIFICATE, "The certificate was not accepted.");
        return fire_net_fail(st, FIRE_TLS_E_CERTIFICATE, "The certificate was not accepted: %s.", buf);
    }
    {
        char prefix[64];
        snprintf(prefix, sizeof prefix, "%s: ", what);
        return fire_tls_failMbed(st, FIRE_TLS_E_TLS, prefix, ret);
    }
}

static int fire_tls_handshakeStep(FireTlsSession* s, int* done, FireNetStatus* st) {
    int ret = mbedtls_ssl_handshake(&s->ssl);
    *done = 0;
    if (ret == 0) { *done = 1; return 1; }
    if (ret == MBEDTLS_ERR_SSL_WANT_READ || ret == MBEDTLS_ERR_SSL_WANT_WRITE) return 1;   /* not yet: the other side has to answer */
    if (s->eof) return fire_net_fail(st, FIRE_NET_E_CLOSED, "TLS handshake: the connection was closed.");
    return fire_tls_certificateFailure(s, ret, "TLS handshake", st);
}

/* The CA certificates of the client: the given ones, or the store of the board / the bundle of the system. */
static int fire_tls_loadRoots(FireTlsSession* s, FireTlsContext* c, const FireTlsOptions* opt, FireNetStatus* st) {
    int haveRoots = 0, ret;
    if (opt->caPem[0]) {
        ret = mbedtls_x509_crt_parse(&s->ca, (const unsigned char*)opt->caPem, fire_tls_pemLength(opt->caPem));
        if (ret < 0) return fire_tls_failMbed(st, FIRE_NET_E_INVALID_ARGUMENT, "The CA certificates (PEM) are not valid: ", ret);
        haveRoots = 1;
    }
    if (opt->caFile[0]) {
#if defined(MBEDTLS_FS_IO)
        ret = mbedtls_x509_crt_parse_file(&s->ca, opt->caFile);
        if (ret < 0) {
            char text[160];
            fire_tls_describe(ret, text, sizeof text);
            return fire_net_fail(st, FIRE_NET_E_INVALID_ARGUMENT, "The CA file '%s' could not be read: %s", opt->caFile, text);
        }
        haveRoots = 1;
#else
        return fire_net_fail(st, FIRE_NET_E_UNSUPPORTED, "This build of mbedTLS cannot read files: give the CA certificates as PEM text.");
#endif
    }
    mbedtls_ssl_conf_authmode(&c->conf, MBEDTLS_SSL_VERIFY_REQUIRED);
    if (haveRoots) { mbedtls_ssl_conf_ca_chain(&c->conf, &s->ca, NULL); return 1; }
#if defined(FIRE_TLS_CA_ATTACH)
    ret = FIRE_TLS_CA_ATTACH(&c->conf);
    if (ret != 0) return fire_tls_failMbed(st, FIRE_TLS_E_TLS, "The certificate bundle could not be attached: ", ret);
    return 1;
#else
    {
        /* a desktop system: the bundle of the distribution */
        int loaded = 0;
#if defined(MBEDTLS_FS_IO)
        static const char* const bundles[] = { "/etc/ssl/certs/ca-certificates.crt", "/etc/pki/tls/certs/ca-bundle.crt", "/etc/ssl/cert.pem", "/etc/ssl/ca-bundle.pem" };
        size_t i;
        for (i = 0; i < sizeof bundles / sizeof bundles[0] && !loaded; i++) if (mbedtls_x509_crt_parse_file(&s->ca, bundles[i]) >= 0) loaded = 1;
#endif
        if (!loaded)
            return fire_net_fail(st, FIRE_NET_E_INVALID_ARGUMENT, "There are no CA certificates to verify the server with: give caPem (or caFile), or define FIRE_TLS_CA_ATTACH for the store of the board.");
        mbedtls_ssl_conf_ca_chain(&c->conf, &s->ca, NULL);
        return 1;
    }
#endif
}

static FireTlsSession* fire_tls_clientOpen(FireNetSock fd, const FireTlsOptions* opt, FireNetStatus* st) {
    FireTlsSession* s = fire_tls_newSession(fd);
    FireTlsContext* c;
    int ret;
    if (s == NULL || (s->own = fire_tls_newContext()) == NULL) { fire_tls_freeSession(s); fire_net_fail(st, FIRE_NET_E_OTHER, "Out of memory."); return NULL; }
    c = s->own;
    ret = mbedtls_ssl_config_defaults(&c->conf, MBEDTLS_SSL_IS_CLIENT, MBEDTLS_SSL_TRANSPORT_STREAM, MBEDTLS_SSL_PRESET_DEFAULT);
    if (ret != 0) { fire_tls_freeSession(s); fire_tls_failMbed(st, FIRE_TLS_E_TLS, "TLS could not be started: ", ret); return NULL; }
    if (!fire_tls_seedRandom(c, st)) { fire_tls_freeSession(s); return NULL; }
    fire_tls_setMinVersion(&c->conf);
    if (opt->verify) {
        if (!fire_tls_loadRoots(s, c, opt, st)) { fire_tls_freeSession(s); return NULL; }
    } else {
        mbedtls_ssl_conf_authmode(&c->conf, MBEDTLS_SSL_VERIFY_NONE);
    }
    if (opt->certPem[0] && opt->keyPem[0] && !fire_tls_loadIdentity(c, opt, st)) { fire_tls_freeSession(s); return NULL; }
    ret = mbedtls_ssl_setup(&s->ssl, &c->conf);
    if (ret != 0) { fire_tls_freeSession(s); fire_tls_failMbed(st, FIRE_TLS_E_TLS, "TLS could not be started: ", ret); return NULL; }
    if (opt->serverName[0]) mbedtls_ssl_set_hostname(&s->ssl, opt->serverName);
    mbedtls_ssl_set_bio(&s->ssl, s, fire_tls_sendCb, fire_tls_recvCb, NULL);
    return s;
}

static FireTlsContext* fire_tls_serverContext(const FireTlsOptions* opt, FireNetStatus* st) {
    FireTlsContext* c = fire_tls_newContext();
    int ret;
    if (c == NULL) { fire_net_fail(st, FIRE_NET_E_OTHER, "Out of memory."); return NULL; }
    ret = mbedtls_ssl_config_defaults(&c->conf, MBEDTLS_SSL_IS_SERVER, MBEDTLS_SSL_TRANSPORT_STREAM, MBEDTLS_SSL_PRESET_DEFAULT);
    if (ret != 0) { fire_tls_freeContext(c); fire_tls_failMbed(st, FIRE_TLS_E_TLS, "TLS could not be started: ", ret); return NULL; }
    if (!fire_tls_seedRandom(c, st)) { fire_tls_freeContext(c); return NULL; }
    fire_tls_setMinVersion(&c->conf);
    mbedtls_ssl_conf_authmode(&c->conf, MBEDTLS_SSL_VERIFY_NONE);
    if (!fire_tls_loadIdentity(c, opt, st)) { fire_tls_freeContext(c); return NULL; }
    return c;
}

static FireTlsSession* fire_tls_serverAccept(FireTlsContext* c, FireNetSock fd, FireNetStatus* st) {
    FireTlsSession* s = fire_tls_newSession(fd);
    int ret;
    if (s == NULL) { fire_net_fail(st, FIRE_NET_E_OTHER, "Out of memory."); return NULL; }
    s->shared = c;
    ret = mbedtls_ssl_setup(&s->ssl, &c->conf);
    if (ret != 0) { fire_tls_freeSession(s); fire_tls_failMbed(st, FIRE_TLS_E_TLS, "TLS could not be started: ", ret); return NULL; }
    mbedtls_ssl_set_bio(&s->ssl, s, fire_tls_sendCb, fire_tls_recvCb, NULL);
    return s;
}

static int fire_tls_readSome(FireTlsSession* s, uint8_t* data, int count, int64_t timeoutMs, int* got, FireNetStatus* st) {
    int64_t deadline = timeoutMs >= 0 ? fire_net_nowMillis() + timeoutMs : -1;
    *got = 0;
    if (count <= 0) return 1;
    for (;;) {
        int ret = mbedtls_ssl_read(&s->ssl, data, (size_t)count);
        if (ret > 0) { *got = ret; return 1; }
        if (ret == 0 || ret == MBEDTLS_ERR_SSL_PEER_CLOSE_NOTIFY) return 1;
        if (ret == MBEDTLS_ERR_SSL_WANT_READ || ret == MBEDTLS_ERR_SSL_WANT_WRITE) {
            if (!fire_tls_wait(s, ret, deadline, st)) return 0;
            continue;
        }
        if (s->eof) return 1;   /* the peer closed without close_notify */
        return fire_tls_failMbed(st, FIRE_TLS_E_TLS, "TLS read: ", ret);
    }
}

static int fire_tls_writeSome(FireTlsSession* s, const uint8_t* data, int count, int64_t timeoutMs, int* sent, FireNetStatus* st) {
    int64_t deadline = timeoutMs >= 0 ? fire_net_nowMillis() + timeoutMs : -1;
    *sent = 0;
    if (count <= 0) return 1;
    for (;;) {
        int ret = mbedtls_ssl_write(&s->ssl, data, (size_t)count);
        if (ret > 0) { *sent = ret; return 1; }
        if (ret == MBEDTLS_ERR_SSL_WANT_READ || ret == MBEDTLS_ERR_SSL_WANT_WRITE) {
            if (!fire_tls_wait(s, ret, deadline, st)) return 0;
            continue;
        }
        return fire_tls_failMbed(st, ret == MBEDTLS_ERR_SSL_PEER_CLOSE_NOTIFY ? FIRE_NET_E_CLOSED : FIRE_TLS_E_TLS, "TLS write: ", ret);
    }
}

static int fire_tls_pending(FireTlsSession* s) { return (int)mbedtls_ssl_get_bytes_avail(&s->ssl); }

static void fire_tls_info(FireTlsSession* s, char* protocol, size_t psize, char* cipher, size_t csize) {
    const char* p = mbedtls_ssl_get_version(&s->ssl);
    const char* c = mbedtls_ssl_get_ciphersuite(&s->ssl);
    snprintf(protocol, psize, "%s", p ? p : "");
    snprintf(cipher, csize, "%s", c ? c : "");
}

static void fire_tls_closeSession(FireTlsSession* s) {
    if (s == NULL) return;
    mbedtls_ssl_close_notify(&s->ssl);   /* best effort: the socket is non-blocking */
    fire_tls_freeSession(s);
}

#endif
