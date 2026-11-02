/* fire native platform layer, TLS part in C on OpenSSL (1.1.1 and 3.x): Linux, macOS (OpenSSL from Homebrew or MacPorts), Windows with OpenSSL installed. The
 * interface: fire_tls_c.h. */
#ifndef FIRE_TLS_OPENSSL_H
#define FIRE_TLS_OPENSSL_H

#include <openssl/err.h>
#include <openssl/pem.h>
#include <openssl/ssl.h>
#include <openssl/x509v3.h>

#include "fire_tls_c.h"

typedef struct FireTlsContext { SSL_CTX* ctx; } FireTlsContext;

typedef struct FireTlsSession {
    SSL* ssl;
    SSL_CTX* ownCtx;   /* a client session has a context of its own */
    FireNetSock fd;
    int client;
} FireTlsSession;

static int fire_tls_supported(void) { return 1; }

/* The errors OpenSSL has queued, as one text (at most about 120 characters). */
static void fire_tls_sslErrors(char* out, size_t size) {
    unsigned long e;
    size_t n = 0;
    out[0] = 0;
    while ((e = ERR_get_error()) != 0) {
        char buf[256];
        ERR_error_string_n(e, buf, sizeof buf);
        n += (size_t)snprintf(out + n, n < size ? size - n : 0, "%s%s", n > 0 ? "; " : "", buf);
        if (n >= size || n > 120) break;
    }
    if (n >= size && size > 0) out[size - 1] = 0;
}

static int fire_tls_isIpLiteral(const char* host) {
    unsigned char buf[sizeof(struct in6_addr)];
    return inet_pton(AF_INET, host, buf) == 1 || inet_pton(AF_INET6, host, buf) == 1;
}

static void fire_tls_initOnce(void) {
    static volatile int done = 0;
    if (done) return;
#if OPENSSL_VERSION_NUMBER < 0x10100000L
    SSL_library_init();
    SSL_load_error_strings();
#else
    OPENSSL_init_ssl(0, NULL);
#endif
    done = 1;
}

static X509* fire_tls_readCert(BIO* bio) { return PEM_read_bio_X509(bio, NULL, NULL, NULL); }

/* Waits for what OpenSSL asks for; 0 when the time is up (status TimedOut) or the wait failed. */
static int fire_tls_wait(FireTlsSession* s, int sslError, int64_t deadline, FireNetStatus* st) {
    int64_t left = deadline < 0 ? -1 : deadline - fire_net_nowMillis();
    int r, w;
    if (deadline >= 0 && left < 0) left = 0;
    if (!fire_net_waitFor(s->fd, sslError == SSL_ERROR_WANT_READ, sslError == SSL_ERROR_WANT_WRITE, left, &r, &w, st)) return 0;
    if (!r && !w) return fire_net_fail(st, FIRE_NET_E_TIMED_OUT, "The TLS operation timed out.");
    return 1;
}

/* A failed SSL call as a status: always 0. */
static int fire_tls_sslFailure(SSL* ssl, int ret, const char* what, FireNetStatus* st) {
    int err = SSL_get_error(ssl, ret);
    char detail[200];
    switch (err) {
        case SSL_ERROR_ZERO_RETURN: return fire_net_fail(st, FIRE_NET_E_CLOSED, "%s: the connection was closed.", what);
        case SSL_ERROR_SYSCALL: {
            int e = fire_net_lastError();
            fire_tls_sslErrors(detail, sizeof detail);
            if (e != 0) return fire_net_failErr(st, e, what);
            if (detail[0]) return fire_net_fail(st, FIRE_NET_E_CLOSED, "%s: the connection ended (%s).", what, detail);
            return fire_net_fail(st, FIRE_NET_E_CLOSED, "%s: the connection ended.", what);
        }
        case SSL_ERROR_SSL: {
            long verify = SSL_get_verify_result(ssl);
            fire_tls_sslErrors(detail, sizeof detail);
            if (verify != X509_V_OK) return fire_net_fail(st, FIRE_TLS_E_CERTIFICATE, "The certificate was not accepted: %s.", X509_verify_cert_error_string(verify));
            return fire_net_fail(st, FIRE_TLS_E_TLS, "%s: %s", what, detail[0] ? detail : "a TLS error");
        }
        default: return fire_net_fail(st, FIRE_TLS_E_TLS, "%s: TLS error %d", what, err);
    }
}

static void fire_tls_freeSession(FireTlsSession* s) {
    if (s == NULL) return;
    if (s->ssl) SSL_free(s->ssl);
    if (s->ownCtx) SSL_CTX_free(s->ownCtx);
    free(s);
}

static int fire_tls_handshakeStep(FireTlsSession* s, int* done, FireNetStatus* st) {
    int ret, err;
    *done = 0;
    ERR_clear_error();
    ret = s->client ? SSL_connect(s->ssl) : SSL_accept(s->ssl);
    if (ret == 1) { *done = 1; return 1; }
    err = SSL_get_error(s->ssl, ret);
    if (err == SSL_ERROR_WANT_READ || err == SSL_ERROR_WANT_WRITE) return 1;   /* not yet: the other side has to answer */
    return fire_tls_sslFailure(s->ssl, ret, s->client ? "TLS handshake" : "TLS accept", st);
}

/* A context with the settings of both sides (TLS 1.2 at least, partial writes; a peer that closes without close_notify ends the data, it is no error). */
static SSL_CTX* fire_tls_newCtx(const SSL_METHOD* method, FireNetStatus* st) {
    SSL_CTX* ctx;
    fire_tls_initOnce();
    ctx = SSL_CTX_new(method);
    if (ctx == NULL) {
        char why[200];
        fire_tls_sslErrors(why, sizeof why);
        fire_net_fail(st, FIRE_TLS_E_TLS, "TLS could not be started: %s", why);
        return NULL;
    }
    SSL_CTX_set_min_proto_version(ctx, TLS1_2_VERSION);
    SSL_CTX_set_mode(ctx, SSL_MODE_ENABLE_PARTIAL_WRITE | SSL_MODE_ACCEPT_MOVING_WRITE_BUFFER);
#ifdef SSL_OP_IGNORE_UNEXPECTED_EOF
    SSL_CTX_set_options(ctx, SSL_OP_IGNORE_UNEXPECTED_EOF);
#endif
    return ctx;
}

/* A certificate and its key from PEM text into the context (the further certificates of `certPem` are the chain when `chain`). */
static int fire_tls_useIdentity(SSL_CTX* ctx, const char* certPem, const char* keyPem, int check, int chain) {
    BIO* cb = BIO_new_mem_buf(certPem, (int)strlen(certPem));
    BIO* kb = BIO_new_mem_buf(keyPem, (int)strlen(keyPem));
    X509* cert = cb ? fire_tls_readCert(cb) : NULL;
    EVP_PKEY* key = kb ? PEM_read_bio_PrivateKey(kb, NULL, NULL, NULL) : NULL;
    int ok = cert && key && SSL_CTX_use_certificate(ctx, cert) == 1 && SSL_CTX_use_PrivateKey(ctx, key) == 1 && (!check || SSL_CTX_check_private_key(ctx) == 1);
    if (ok && chain) {
        for (;;) {
            X509* extra = fire_tls_readCert(cb);
            if (extra == NULL) break;
            if (SSL_CTX_add_extra_chain_cert(ctx, extra) != 1) { X509_free(extra); break; }
        }
    }
    ERR_clear_error();
    if (cert) X509_free(cert);
    if (key) EVP_PKEY_free(key);
    if (cb) BIO_free(cb);
    if (kb) BIO_free(kb);
    return ok;
}

static FireTlsSession* fire_tls_clientOpen(FireNetSock fd, const FireTlsOptions* opt, FireNetStatus* st) {
    FireTlsSession* s;
    SSL_CTX* ctx = fire_tls_newCtx(TLS_client_method(), st);
    if (ctx == NULL) return NULL;
    if (opt->verify) {
        int haveRoots = 0;
        SSL_CTX_set_verify(ctx, SSL_VERIFY_PEER, NULL);
        if (opt->caPem[0]) {
            BIO* bio = BIO_new_mem_buf(opt->caPem, (int)strlen(opt->caPem));
            X509_STORE* store = SSL_CTX_get_cert_store(ctx);
            while (bio) {
                X509* cert = fire_tls_readCert(bio);
                if (cert == NULL) break;
                X509_STORE_add_cert(store, cert);
                X509_free(cert);
                haveRoots = 1;
            }
            if (bio) BIO_free(bio);
            ERR_clear_error();
            if (!haveRoots) { SSL_CTX_free(ctx); fire_net_fail(st, FIRE_NET_E_INVALID_ARGUMENT, "The CA certificates (PEM) contain no certificate."); return NULL; }
        }
        if (opt->caFile[0]) {
            if (SSL_CTX_load_verify_locations(ctx, opt->caFile, NULL) != 1) {
                char why[200];
                fire_tls_sslErrors(why, sizeof why);
                SSL_CTX_free(ctx);
                if (why[0]) fire_net_fail(st, FIRE_NET_E_INVALID_ARGUMENT, "The CA file '%s' could not be read: %s", opt->caFile, why);
                else fire_net_fail(st, FIRE_NET_E_INVALID_ARGUMENT, "The CA file '%s' could not be read.", opt->caFile);
                return NULL;
            }
            haveRoots = 1;
        }
        if (!haveRoots) SSL_CTX_set_default_verify_paths(ctx);   /* the system's certificates */
    } else {
        SSL_CTX_set_verify(ctx, SSL_VERIFY_NONE, NULL);
    }
    if (opt->certPem[0] && opt->keyPem[0] && !fire_tls_useIdentity(ctx, opt->certPem, opt->keyPem, 0, 0)) {   /* a client certificate */
        SSL_CTX_free(ctx);
        fire_net_fail(st, FIRE_NET_E_INVALID_ARGUMENT, "The client certificate or its key is not valid.");
        return NULL;
    }
    s = (FireTlsSession*)calloc(1, sizeof(FireTlsSession));
    if (s == NULL) { SSL_CTX_free(ctx); fire_net_fail(st, FIRE_NET_E_OTHER, "Out of memory."); return NULL; }
    s->ownCtx = ctx;
    s->fd = fd;
    s->ssl = SSL_new(ctx);
    if (s->ssl == NULL) {
        char why[200];
        fire_tls_sslErrors(why, sizeof why);
        fire_tls_freeSession(s);
        fire_net_fail(st, FIRE_TLS_E_TLS, "TLS could not be started: %s", why);
        return NULL;
    }
    SSL_set_fd(s->ssl, (int)fd);
    if (opt->serverName[0]) {
        if (fire_tls_isIpLiteral(opt->serverName)) {
            if (opt->verify) X509_VERIFY_PARAM_set1_ip_asc(SSL_get0_param(s->ssl), opt->serverName);
        } else {
            SSL_set_tlsext_host_name(s->ssl, opt->serverName);
            if (opt->verify) SSL_set1_host(s->ssl, opt->serverName);
        }
    }
    s->client = 1;
    return s;
}

static FireTlsContext* fire_tls_serverContext(const FireTlsOptions* opt, FireNetStatus* st) {
    FireTlsContext* c;
    SSL_CTX* ctx = fire_tls_newCtx(TLS_server_method(), st);
    if (ctx == NULL) return NULL;
    if (!fire_tls_useIdentity(ctx, opt->certPem, opt->keyPem, 1, 1)) {
        SSL_CTX_free(ctx);
        fire_net_fail(st, FIRE_NET_E_INVALID_ARGUMENT, "The server certificate (PEM) or its private key is not valid, or they do not belong together.");
        return NULL;
    }
    c = (FireTlsContext*)calloc(1, sizeof(FireTlsContext));
    if (c == NULL) { SSL_CTX_free(ctx); fire_net_fail(st, FIRE_NET_E_OTHER, "Out of memory."); return NULL; }
    c->ctx = ctx;
    return c;
}

static void fire_tls_freeContext(FireTlsContext* c) {
    if (c == NULL) return;
    if (c->ctx) SSL_CTX_free(c->ctx);
    free(c);
}

static FireTlsSession* fire_tls_serverAccept(FireTlsContext* c, FireNetSock fd, FireNetStatus* st) {
    FireTlsSession* s = (FireTlsSession*)calloc(1, sizeof(FireTlsSession));
    if (s == NULL) { fire_net_fail(st, FIRE_NET_E_OTHER, "Out of memory."); return NULL; }
    s->fd = fd;
    s->ssl = SSL_new(c->ctx);
    if (s->ssl == NULL) {
        char why[200];
        fire_tls_sslErrors(why, sizeof why);
        fire_tls_freeSession(s);
        fire_net_fail(st, FIRE_TLS_E_TLS, "TLS could not be started: %s", why);
        return NULL;
    }
    SSL_set_fd(s->ssl, (int)fd);
    s->client = 0;
    return s;
}

static int fire_tls_readSome(FireTlsSession* s, uint8_t* data, int count, int64_t timeoutMs, int* got, FireNetStatus* st) {
    int64_t deadline = timeoutMs >= 0 ? fire_net_nowMillis() + timeoutMs : -1;
    *got = 0;
    if (count <= 0) return 1;
    for (;;) {
        int ret, err;
        ERR_clear_error();
        ret = SSL_read(s->ssl, data, count);
        if (ret > 0) { *got = ret; return 1; }
        err = SSL_get_error(s->ssl, ret);
        if (err == SSL_ERROR_ZERO_RETURN) return 1;
        if (err == SSL_ERROR_WANT_READ || err == SSL_ERROR_WANT_WRITE) {
            if (!fire_tls_wait(s, err, deadline, st)) return 0;
            continue;
        }
        if (err == SSL_ERROR_SYSCALL && fire_net_lastError() == 0) return 1;   /* the peer closed without close_notify */
        return fire_tls_sslFailure(s->ssl, ret, "TLS read", st);
    }
}

static int fire_tls_writeSome(FireTlsSession* s, const uint8_t* data, int count, int64_t timeoutMs, int* sent, FireNetStatus* st) {
    int64_t deadline = timeoutMs >= 0 ? fire_net_nowMillis() + timeoutMs : -1;
    *sent = 0;
    if (count <= 0) return 1;
    for (;;) {
        int ret, err;
        ERR_clear_error();
        ret = SSL_write(s->ssl, data, count);
        if (ret > 0) { *sent = ret; return 1; }
        err = SSL_get_error(s->ssl, ret);
        if (err == SSL_ERROR_WANT_READ || err == SSL_ERROR_WANT_WRITE) {
            if (!fire_tls_wait(s, err, deadline, st)) return 0;
            continue;
        }
        return fire_tls_sslFailure(s->ssl, ret, "TLS write", st);
    }
}

static int fire_tls_pending(FireTlsSession* s) { return SSL_pending(s->ssl); }

static void fire_tls_info(FireTlsSession* s, char* protocol, size_t psize, char* cipher, size_t csize) {
    const char* p = SSL_get_version(s->ssl);
    const char* c = SSL_get_cipher_name(s->ssl);
    snprintf(protocol, psize, "%s", p ? p : "");
    snprintf(cipher, csize, "%s", c ? c : "");
}

static void fire_tls_closeSession(FireTlsSession* s) {
    if (s == NULL) return;
    if (s->ssl && !(SSL_get_shutdown(s->ssl) & SSL_SENT_SHUTDOWN)) {
        ERR_clear_error();
        SSL_shutdown(s->ssl);   /* close_notify, best effort (the socket is non-blocking: no waiting for the answer) */
    }
    fire_tls_freeSession(s);
}

#endif
