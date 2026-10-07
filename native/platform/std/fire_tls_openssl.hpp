// fire native platform layer, TLS part on OpenSSL (1.1.1 and 3.x): Linux, macOS (OpenSSL from Homebrew or MacPorts), Windows with OpenSSL installed. A platform package offers
// `fire_tls.hpp` (the generated file defines FIRE_PLATFORM_TLS_HEADER for it) that provides, in fire::plat::tls (the sockets are those of plat::net):
//
//   struct Options { bool verify; std::string caFile, caPem, serverName, certPem, keyPem; }
//   struct Session;  struct Context;                                (opaque)
//   bool supported()
//   Status clientOpen(Sock fd, const Options&, Session*& out)                               a client session over the socket (SNI, certificate and host name checked unless !verify); nothing is sent yet
//   Status handshakeStep(Session*, bool& done)                                              does what can be done without waiting: done when the handshake is complete (call again until then)
//   Status serverContext(const Options& (certPem, keyPem), Context*& out) / freeContext(Context*)
//   Status serverAccept(Context*, Sock fd, Session*& out)                                  a server session over the connection; the handshake runs with handshakeStep
//   Status readSome(Session*, uint8_t*, int count, int64_t timeoutMs, int& got)             got 0: the other side ended the connection (close_notify or a plain close)
//   Status writeSome(Session*, const uint8_t*, int count, int64_t timeoutMs, int& sent)
//   int pending(Session*)                                                                   decrypted bytes that read without touching the socket
//   void info(Session*, std::string& protocol, std::string& cipher)
//   void closeSession(Session*)                                                             close_notify (best effort), then everything is freed (the socket stays open)
//
// The socket is non-blocking and belongs to plat::net; the session only reads and writes it. Waiting is done with plat::net::waitFor, never longer than the timeout.
#pragma once

#include <openssl/err.h>
#include <openssl/pem.h>
#include <openssl/ssl.h>
#include <openssl/x509v3.h>

#include <cstring>
#include <string>

#include FIRE_PLATFORM_NET_HEADER

namespace fire {
namespace plat {
namespace tls {

using net::Status;
using net::Sock;
using net::fail;
using net::success;

enum TlsErr { E_Tls = 14, E_Certificate = 15 };

struct Options {
    bool verify = true;
    std::string caFile, caPem, serverName, certPem, keyPem;
};

struct Context {
    SSL_CTX* ctx = nullptr;
};

struct Session {
    SSL* ssl = nullptr;
    SSL_CTX* ownCtx = nullptr;   // a client session has a context of its own
    Sock fd = net::kInvalid;
    bool client = true;
};

inline bool supported() { return true; }

inline std::string lastOpenSslError() {
    std::string text;
    unsigned long e;
    while ((e = ERR_get_error()) != 0) {
        char buf[256];
        ERR_error_string_n(e, buf, sizeof buf);
        if (!text.empty()) text += "; ";
        text += buf;
        if (text.size() > 120) break;
    }
    return text;
}

inline bool isIpLiteral(const std::string& host) {
    unsigned char buf[sizeof(struct in6_addr)];
    return inet_pton(AF_INET, host.c_str(), buf) == 1 || inet_pton(AF_INET6, host.c_str(), buf) == 1;
}

inline void initOnce() {
    static bool done = [] {
#if OPENSSL_VERSION_NUMBER < 0x10100000L
        SSL_library_init();
        SSL_load_error_strings();
#else
        OPENSSL_init_ssl(0, nullptr);
#endif
        return true;
    }();
    (void)done;
}

inline X509* readCert(BIO* bio) { return PEM_read_bio_X509(bio, nullptr, nullptr, nullptr); }

/// Waits for what OpenSSL asks for; false when the time is up (status TimedOut) or the wait failed.
inline bool waitFor(Session* s, int sslError, int64_t deadline, Status& status) {
    int64_t left = deadline < 0 ? -1 : deadline - net::nowMillis();
    if (deadline >= 0 && left < 0) left = 0;
    bool r, w;
    Status st = net::waitFor(s->fd, sslError == SSL_ERROR_WANT_READ, sslError == SSL_ERROR_WANT_WRITE, left, r, w);
    if (!st.ok()) { status = st; return false; }
    if (!r && !w) { status = fail(net::E_TimedOut, "The TLS operation timed out."); return false; }
    return true;
}

/// A failed SSL call as a status.
inline Status sslFailure(SSL* ssl, int ret, const char* what) {
    int err = SSL_get_error(ssl, ret);
    switch (err) {
        case SSL_ERROR_ZERO_RETURN: return fail(net::E_Closed, std::string(what) + ": the connection was closed.");
        case SSL_ERROR_SYSCALL: {
            int e = net::lastError();
            std::string detail = lastOpenSslError();
            if (e != 0) return net::failErrno(e, what);
            return fail(net::E_Closed, std::string(what) + ": the connection ended" + (detail.empty() ? "." : " (" + detail + ")."));
        }
        case SSL_ERROR_SSL: {
            long verify = SSL_get_verify_result(ssl);
            std::string detail = lastOpenSslError();
            if (verify != X509_V_OK) return fail(E_Certificate, std::string("The certificate was not accepted: ") + X509_verify_cert_error_string(verify) + ".");
            return fail(E_Tls, std::string(what) + ": " + (detail.empty() ? "a TLS error" : detail));
        }
        default: return fail(E_Tls, std::string(what) + ": TLS error " + std::to_string(err));
    }
}

inline void freeSession(Session* s) {
    if (!s) return;
    if (s->ssl) SSL_free(s->ssl);
    if (s->ownCtx) SSL_CTX_free(s->ownCtx);
    delete s;
}

inline Status handshakeStep(Session* s, bool& done) {
    done = false;
    ERR_clear_error();
    int ret = s->client ? SSL_connect(s->ssl) : SSL_accept(s->ssl);
    if (ret == 1) { done = true; return success(); }
    int err = SSL_get_error(s->ssl, ret);
    if (err == SSL_ERROR_WANT_READ || err == SSL_ERROR_WANT_WRITE) return success();   // not yet: the other side has to answer
    return sslFailure(s->ssl, ret, s->client ? "TLS handshake" : "TLS accept");
}

inline Status clientOpen(Sock fd, const Options& opt, Session*& out) {
    out = nullptr;
    initOnce();
    SSL_CTX* ctx = SSL_CTX_new(TLS_client_method());
    if (!ctx) return fail(E_Tls, "TLS could not be started: " + lastOpenSslError());
    SSL_CTX_set_min_proto_version(ctx, TLS1_2_VERSION);
    SSL_CTX_set_mode(ctx, SSL_MODE_ENABLE_PARTIAL_WRITE | SSL_MODE_ACCEPT_MOVING_WRITE_BUFFER);
#ifdef SSL_OP_IGNORE_UNEXPECTED_EOF
    SSL_CTX_set_options(ctx, SSL_OP_IGNORE_UNEXPECTED_EOF);   // a server that closes without close_notify (HTTP/1.0 style) ends the data, it is no error
#endif
    if (opt.verify) {
        SSL_CTX_set_verify(ctx, SSL_VERIFY_PEER, nullptr);
        bool haveRoots = false;
        if (!opt.caPem.empty()) {
            BIO* bio = BIO_new_mem_buf(opt.caPem.data(), (int)opt.caPem.size());
            X509_STORE* store = SSL_CTX_get_cert_store(ctx);
            while (bio) {
                X509* cert = readCert(bio);
                if (!cert) break;
                X509_STORE_add_cert(store, cert);
                X509_free(cert);
                haveRoots = true;
            }
            if (bio) BIO_free(bio);
            ERR_clear_error();
            if (!haveRoots) { SSL_CTX_free(ctx); return fail(net::E_InvalidArgument, "The CA certificates (PEM) contain no certificate."); }
        }
        if (!opt.caFile.empty()) {
            if (SSL_CTX_load_verify_locations(ctx, opt.caFile.c_str(), nullptr) != 1) {
                std::string why = lastOpenSslError();
                SSL_CTX_free(ctx);
                return fail(net::E_InvalidArgument, "The CA file '" + opt.caFile + "' could not be read" + (why.empty() ? "." : ": " + why));
            }
            haveRoots = true;
        }
        if (!haveRoots) SSL_CTX_set_default_verify_paths(ctx);   // the system's certificates
    } else {
        SSL_CTX_set_verify(ctx, SSL_VERIFY_NONE, nullptr);
    }
    if (!opt.certPem.empty() && !opt.keyPem.empty()) {
        // a client certificate
        BIO* cb = BIO_new_mem_buf(opt.certPem.data(), (int)opt.certPem.size());
        BIO* kb = BIO_new_mem_buf(opt.keyPem.data(), (int)opt.keyPem.size());
        X509* cert = cb ? readCert(cb) : nullptr;
        EVP_PKEY* key = kb ? PEM_read_bio_PrivateKey(kb, nullptr, nullptr, nullptr) : nullptr;
        bool ok = cert && key && SSL_CTX_use_certificate(ctx, cert) == 1 && SSL_CTX_use_PrivateKey(ctx, key) == 1;
        if (cert) X509_free(cert);
        if (key) EVP_PKEY_free(key);
        if (cb) BIO_free(cb);
        if (kb) BIO_free(kb);
        if (!ok) { SSL_CTX_free(ctx); return fail(net::E_InvalidArgument, "The client certificate or its key is not valid."); }
    }
    Session* s = new Session();
    s->ownCtx = ctx;
    s->fd = fd;
    s->ssl = SSL_new(ctx);
    if (!s->ssl) { std::string why = lastOpenSslError(); freeSession(s); return fail(E_Tls, "TLS could not be started: " + why); }
    SSL_set_fd(s->ssl, (int)fd);
    if (!opt.serverName.empty()) {
        if (isIpLiteral(opt.serverName)) {
            if (opt.verify) X509_VERIFY_PARAM_set1_ip_asc(SSL_get0_param(s->ssl), opt.serverName.c_str());
        } else {
            SSL_set_tlsext_host_name(s->ssl, opt.serverName.c_str());
            if (opt.verify) SSL_set1_host(s->ssl, opt.serverName.c_str());
        }
    }
    s->client = true;
    out = s;
    return success();
}

inline Status serverContext(const Options& opt, Context*& out) {
    out = nullptr;
    initOnce();
    SSL_CTX* ctx = SSL_CTX_new(TLS_server_method());
    if (!ctx) return fail(E_Tls, "TLS could not be started: " + lastOpenSslError());
    SSL_CTX_set_min_proto_version(ctx, TLS1_2_VERSION);
    SSL_CTX_set_mode(ctx, SSL_MODE_ENABLE_PARTIAL_WRITE | SSL_MODE_ACCEPT_MOVING_WRITE_BUFFER);
#ifdef SSL_OP_IGNORE_UNEXPECTED_EOF
    SSL_CTX_set_options(ctx, SSL_OP_IGNORE_UNEXPECTED_EOF);
#endif
    BIO* cb = BIO_new_mem_buf(opt.certPem.data(), (int)opt.certPem.size());
    BIO* kb = BIO_new_mem_buf(opt.keyPem.data(), (int)opt.keyPem.size());
    X509* cert = cb ? readCert(cb) : nullptr;
    EVP_PKEY* key = kb ? PEM_read_bio_PrivateKey(kb, nullptr, nullptr, nullptr) : nullptr;
    bool ok = cert && key && SSL_CTX_use_certificate(ctx, cert) == 1 && SSL_CTX_use_PrivateKey(ctx, key) == 1 && SSL_CTX_check_private_key(ctx) == 1;
    if (ok) {
        // the certificates after the first one are the chain the clients get
        for (;;) {
            X509* extra = readCert(cb);
            if (!extra) break;
            if (SSL_CTX_add_extra_chain_cert(ctx, extra) != 1) { X509_free(extra); break; }
        }
    }
    ERR_clear_error();
    if (cert) X509_free(cert);
    if (key) EVP_PKEY_free(key);
    if (cb) BIO_free(cb);
    if (kb) BIO_free(kb);
    if (!ok) { SSL_CTX_free(ctx); return fail(net::E_InvalidArgument, "The server certificate (PEM) or its private key is not valid, or they do not belong together."); }
    out = new Context();
    out->ctx = ctx;
    return success();
}

inline void freeContext(Context* c) {
    if (!c) return;
    if (c->ctx) SSL_CTX_free(c->ctx);
    delete c;
}

inline Status serverAccept(Context* c, Sock fd, Session*& out) {
    out = nullptr;
    Session* s = new Session();
    s->fd = fd;
    s->ssl = SSL_new(c->ctx);
    if (!s->ssl) { std::string why = lastOpenSslError(); freeSession(s); return fail(E_Tls, "TLS could not be started: " + why); }
    SSL_set_fd(s->ssl, (int)fd);
    s->client = false;
    out = s;
    return success();
}

inline Status readSome(Session* s, uint8_t* data, int count, int64_t timeoutMs, int& got) {
    got = 0;
    if (count <= 0) return success();
    int64_t deadline = timeoutMs >= 0 ? net::nowMillis() + timeoutMs : -1;
    for (;;) {
        ERR_clear_error();
        int ret = SSL_read(s->ssl, data, count);
        if (ret > 0) { got = ret; return success(); }
        int err = SSL_get_error(s->ssl, ret);
        if (err == SSL_ERROR_ZERO_RETURN) { got = 0; return success(); }
        if (err == SSL_ERROR_WANT_READ || err == SSL_ERROR_WANT_WRITE) {
            Status st;
            if (!waitFor(s, err, deadline, st)) return st;
            continue;
        }
        if (err == SSL_ERROR_SYSCALL && net::lastError() == 0) { got = 0; return success(); }   // the peer closed without close_notify
        return sslFailure(s->ssl, ret, "TLS read");
    }
}

inline Status writeSome(Session* s, const uint8_t* data, int count, int64_t timeoutMs, int& sent) {
    sent = 0;
    if (count <= 0) return success();
    int64_t deadline = timeoutMs >= 0 ? net::nowMillis() + timeoutMs : -1;
    for (;;) {
        ERR_clear_error();
        int ret = SSL_write(s->ssl, data, count);
        if (ret > 0) { sent = ret; return success(); }
        int err = SSL_get_error(s->ssl, ret);
        if (err == SSL_ERROR_WANT_READ || err == SSL_ERROR_WANT_WRITE) {
            Status st;
            if (!waitFor(s, err, deadline, st)) return st;
            continue;
        }
        return sslFailure(s->ssl, ret, "TLS write");
    }
}

inline int pending(Session* s) { return SSL_pending(s->ssl); }

inline void info(Session* s, std::string& protocol, std::string& cipher) {
    const char* p = SSL_get_version(s->ssl);
    const char* c = SSL_get_cipher_name(s->ssl);
    protocol = p ? p : "";
    cipher = c ? c : "";
}

inline void closeSession(Session* s) {
    if (!s) return;
    if (s->ssl && !(SSL_get_shutdown(s->ssl) & SSL_SENT_SHUTDOWN)) {
        ERR_clear_error();
        SSL_shutdown(s->ssl);   // close_notify, best effort (the socket is non-blocking: no waiting for the answer)
    }
    freeSession(s);
}

}  // namespace tls
}  // namespace plat
}  // namespace fire
