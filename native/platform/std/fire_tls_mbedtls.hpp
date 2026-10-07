// fire native platform layer, TLS part on mbedTLS (2.28 and 3.x): the ESP32 (mbedTLS comes with ESP-IDF), or any system with mbedTLS (define FIRE_TLS_MBEDTLS before the platform header
// to choose it on a desktop; link mbedtls, mbedx509 and mbedcrypto). The interface is the one of fire_tls_openssl.hpp (see there).
//
// Certificates: a client verifies against `Options::caPem` / `caFile` (the file needs MBEDTLS_FS_IO) or, without one of them, against the store that the board provides: define
// FIRE_TLS_CA_ATTACH(conf) in the target configuration (on the ESP32 `esp_crt_bundle_attach` of ESP-IDF's certificate bundle: `#define FIRE_TLS_CA_ATTACH(conf) esp_crt_bundle_attach(conf)` and
// include "esp_crt_bundle.h"). Without any CA the handshake is refused with a message that says so (set `verify` to false only for tests).
#pragma once

#include <mbedtls/ctr_drbg.h>
#include <mbedtls/entropy.h>
#include <mbedtls/error.h>
#include <mbedtls/pk.h>
#include <mbedtls/ssl.h>
#include <mbedtls/version.h>
#include <mbedtls/x509_crt.h>

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

/// What a server (or a client with a certificate) needs for every connection: kept as long as the connections live.
struct Context {
    mbedtls_ssl_config conf;
    mbedtls_x509_crt cert;
    mbedtls_pk_context key;
    mbedtls_entropy_context entropy;
    mbedtls_ctr_drbg_context drbg;
    Context() {
        mbedtls_ssl_config_init(&conf);
        mbedtls_x509_crt_init(&cert);
        mbedtls_pk_init(&key);
        mbedtls_entropy_init(&entropy);
        mbedtls_ctr_drbg_init(&drbg);
    }
    ~Context() {
        mbedtls_ssl_config_free(&conf);
        mbedtls_x509_crt_free(&cert);
        mbedtls_pk_free(&key);
        mbedtls_ctr_drbg_free(&drbg);
        mbedtls_entropy_free(&entropy);
    }
};

struct Session {
    mbedtls_ssl_context ssl;
    Context* shared = nullptr;     // the server's context (not owned)
    Context* own = nullptr;        // a client session has a context of its own
    mbedtls_x509_crt ca;
    Sock fd = net::kInvalid;
    bool eof = false;
    Session() { mbedtls_ssl_init(&ssl); mbedtls_x509_crt_init(&ca); }
    ~Session() {
        mbedtls_ssl_free(&ssl);
        mbedtls_x509_crt_free(&ca);
        delete own;
    }
};

inline bool supported() { return true; }

inline std::string describe(int code) {
    char buf[160];
    mbedtls_strerror(code, buf, sizeof buf);
    return buf;
}

// the socket callbacks of mbedTLS: non-blocking, so "nothing now" is WANT_READ / WANT_WRITE
inline int sendCb(void* ctx, const unsigned char* buf, size_t len) {
    Session* s = static_cast<Session*>(ctx);
    int sent = 0;
    Status st = net::sendBytes(s->fd, buf, (int)len, 0, sent);
    if (st.ok()) return sent;
    if (st.code == net::E_TimedOut) return MBEDTLS_ERR_SSL_WANT_WRITE;
    return MBEDTLS_ERR_SSL_INTERNAL_ERROR;
}
inline int recvCb(void* ctx, unsigned char* buf, size_t len) {
    Session* s = static_cast<Session*>(ctx);
    int got = 0;
    Status st = net::recvBytes(s->fd, buf, (int)len, 0, got);
    if (st.ok()) { if (got == 0) { s->eof = true; return 0; } return got; }
    if (st.code == net::E_TimedOut) return MBEDTLS_ERR_SSL_WANT_READ;
    return MBEDTLS_ERR_SSL_INTERNAL_ERROR;
}

inline bool waitFor(Session* s, int ret, int64_t deadline, Status& status) {
    int64_t left = deadline < 0 ? -1 : deadline - net::nowMillis();
    if (deadline >= 0 && left < 0) left = 0;
    bool r, w;
    Status st = net::waitFor(s->fd, ret == MBEDTLS_ERR_SSL_WANT_READ, ret == MBEDTLS_ERR_SSL_WANT_WRITE, left, r, w);
    if (!st.ok()) { status = st; return false; }
    if (!r && !w) { status = fail(net::E_TimedOut, "The TLS operation timed out."); return false; }
    return true;
}

inline Status seedRandom(Context* c) {
    const char* pers = "fire-tls";
    int ret = mbedtls_ctr_drbg_seed(&c->drbg, mbedtls_entropy_func, &c->entropy, (const unsigned char*)pers, std::strlen(pers));
    if (ret != 0) return fail(E_Tls, "TLS could not be started: " + describe(ret));
    mbedtls_ssl_conf_rng(&c->conf, mbedtls_ctr_drbg_random, &c->drbg);
    return success();
}

inline void setMinVersion(mbedtls_ssl_config* conf) {
#if MBEDTLS_VERSION_MAJOR >= 3
    mbedtls_ssl_conf_min_tls_version(conf, MBEDTLS_SSL_VERSION_TLS1_2);
#else
    mbedtls_ssl_conf_min_version(conf, MBEDTLS_SSL_MAJOR_VERSION_3, MBEDTLS_SSL_MINOR_VERSION_3);
#endif
}

inline int parseKey(mbedtls_pk_context* key, const std::string& pem, Context* c) {
    std::string text = pem;
    text.push_back('\0');   // (mbedTLS wants the terminating zero of PEM data in the length)
#if MBEDTLS_VERSION_MAJOR >= 3
    return mbedtls_pk_parse_key(key, (const unsigned char*)text.data(), text.size(), nullptr, 0, mbedtls_ctr_drbg_random, &c->drbg);
#else
    (void)c;
    return mbedtls_pk_parse_key(key, (const unsigned char*)text.data(), text.size(), nullptr, 0);
#endif
}

inline Status loadIdentity(Context* c, const Options& opt) {
    std::string text = opt.certPem;
    text.push_back('\0');
    int ret = mbedtls_x509_crt_parse(&c->cert, (const unsigned char*)text.data(), text.size());
    if (ret != 0) return fail(net::E_InvalidArgument, "The certificate (PEM) is not valid: " + describe(ret));
    ret = parseKey(&c->key, opt.keyPem, c);
    if (ret != 0) return fail(net::E_InvalidArgument, "The private key (PEM) is not valid: " + describe(ret));
    ret = mbedtls_ssl_conf_own_cert(&c->conf, &c->cert, &c->key);
    if (ret != 0) return fail(net::E_InvalidArgument, "The certificate and the key cannot be used: " + describe(ret));
    return success();
}

inline Status certificateFailure(Session* s, int ret, const char* what) {
    if (ret == MBEDTLS_ERR_X509_CERT_VERIFY_FAILED) {
        uint32_t flags = mbedtls_ssl_get_verify_result(&s->ssl);
        char buf[512];
        buf[0] = 0;
        if (flags != 0 && flags != 0xFFFFFFFFu) mbedtls_x509_crt_verify_info(buf, sizeof buf, "", flags);
        std::string text = buf;
        while (!text.empty() && (text.back() == '\n' || text.back() == ' ')) text.pop_back();
        return fail(E_Certificate, "The certificate was not accepted" + (text.empty() ? std::string(".") : ": " + text + "."));
    }
    return fail(E_Tls, std::string(what) + ": " + describe(ret));
}

inline Status handshakeStep(Session* s, bool& done) {
    done = false;
    int ret = mbedtls_ssl_handshake(&s->ssl);
    if (ret == 0) { done = true; return success(); }
    if (ret == MBEDTLS_ERR_SSL_WANT_READ || ret == MBEDTLS_ERR_SSL_WANT_WRITE) return success();   // not yet: the other side has to answer
    if (s->eof) return fail(net::E_Closed, "TLS handshake: the connection was closed.");
    return certificateFailure(s, ret, "TLS handshake");
}

inline Status clientOpen(Sock fd, const Options& opt, Session*& out) {
    out = nullptr;
    Session* s = new Session();
    s->fd = fd;
    s->own = new Context();
    Context* c = s->own;
    int ret = mbedtls_ssl_config_defaults(&c->conf, MBEDTLS_SSL_IS_CLIENT, MBEDTLS_SSL_TRANSPORT_STREAM, MBEDTLS_SSL_PRESET_DEFAULT);
    if (ret != 0) { delete s; return fail(E_Tls, "TLS could not be started: " + describe(ret)); }
    Status st = seedRandom(c);
    if (!st.ok()) { delete s; return st; }
    setMinVersion(&c->conf);
    if (opt.verify) {
        bool haveRoots = false;
        if (!opt.caPem.empty()) {
            std::string text = opt.caPem;
            text.push_back('\0');
            ret = mbedtls_x509_crt_parse(&s->ca, (const unsigned char*)text.data(), text.size());
            if (ret < 0) { delete s; return fail(net::E_InvalidArgument, "The CA certificates (PEM) are not valid: " + describe(ret)); }
            haveRoots = true;
        }
        if (!opt.caFile.empty()) {
#if defined(MBEDTLS_FS_IO)
            ret = mbedtls_x509_crt_parse_file(&s->ca, opt.caFile.c_str());
            if (ret < 0) { delete s; return fail(net::E_InvalidArgument, "The CA file '" + opt.caFile + "' could not be read: " + describe(ret)); }
            haveRoots = true;
#else
            delete s;
            return fail(net::E_Unsupported, "This build of mbedTLS cannot read files: give the CA certificates as PEM text.");
#endif
        }
        mbedtls_ssl_conf_authmode(&c->conf, MBEDTLS_SSL_VERIFY_REQUIRED);
        if (haveRoots) {
            mbedtls_ssl_conf_ca_chain(&c->conf, &s->ca, nullptr);
        } else {
#if defined(FIRE_TLS_CA_ATTACH)
            ret = FIRE_TLS_CA_ATTACH(&c->conf);
            if (ret != 0) { delete s; return fail(E_Tls, "The certificate bundle could not be attached: " + describe(ret)); }
#else
            // a desktop system: the bundle of the distribution
            bool loaded = false;
#if defined(MBEDTLS_FS_IO)
            static const char* const bundles[] = { "/etc/ssl/certs/ca-certificates.crt", "/etc/pki/tls/certs/ca-bundle.crt", "/etc/ssl/cert.pem", "/etc/ssl/ca-bundle.pem" };
            for (const char* file : bundles)
                if (mbedtls_x509_crt_parse_file(&s->ca, file) >= 0) { loaded = true; break; }
#endif
            if (!loaded) {
                delete s;
                return fail(net::E_InvalidArgument, "There are no CA certificates to verify the server with: give caPem (or caFile), or define FIRE_TLS_CA_ATTACH for the store of the board.");
            }
            mbedtls_ssl_conf_ca_chain(&c->conf, &s->ca, nullptr);
#endif
        }
    } else {
        mbedtls_ssl_conf_authmode(&c->conf, MBEDTLS_SSL_VERIFY_NONE);
    }
    if (!opt.certPem.empty() && !opt.keyPem.empty()) {
        st = loadIdentity(c, opt);
        if (!st.ok()) { delete s; return st; }
    }
    ret = mbedtls_ssl_setup(&s->ssl, &c->conf);
    if (ret != 0) { delete s; return fail(E_Tls, "TLS could not be started: " + describe(ret)); }
    if (!opt.serverName.empty()) mbedtls_ssl_set_hostname(&s->ssl, opt.serverName.c_str());
    mbedtls_ssl_set_bio(&s->ssl, s, sendCb, recvCb, nullptr);
    out = s;
    return success();
}

inline Status serverContext(const Options& opt, Context*& out) {
    out = nullptr;
    Context* c = new Context();
    int ret = mbedtls_ssl_config_defaults(&c->conf, MBEDTLS_SSL_IS_SERVER, MBEDTLS_SSL_TRANSPORT_STREAM, MBEDTLS_SSL_PRESET_DEFAULT);
    if (ret != 0) { delete c; return fail(E_Tls, "TLS could not be started: " + describe(ret)); }
    Status st = seedRandom(c);
    if (!st.ok()) { delete c; return st; }
    setMinVersion(&c->conf);
    mbedtls_ssl_conf_authmode(&c->conf, MBEDTLS_SSL_VERIFY_NONE);
    st = loadIdentity(c, opt);
    if (!st.ok()) { delete c; return st; }
    out = c;
    return success();
}

inline void freeContext(Context* c) { delete c; }

inline Status serverAccept(Context* c, Sock fd, Session*& out) {
    out = nullptr;
    Session* s = new Session();
    s->fd = fd;
    s->shared = c;
    int ret = mbedtls_ssl_setup(&s->ssl, &c->conf);
    if (ret != 0) { delete s; return fail(E_Tls, "TLS could not be started: " + describe(ret)); }
    mbedtls_ssl_set_bio(&s->ssl, s, sendCb, recvCb, nullptr);
    out = s;
    return success();
}

inline Status readSome(Session* s, uint8_t* data, int count, int64_t timeoutMs, int& got) {
    got = 0;
    if (count <= 0) return success();
    int64_t deadline = timeoutMs >= 0 ? net::nowMillis() + timeoutMs : -1;
    for (;;) {
        int ret = mbedtls_ssl_read(&s->ssl, data, (size_t)count);
        if (ret > 0) { got = ret; return success(); }
        if (ret == 0 || ret == MBEDTLS_ERR_SSL_PEER_CLOSE_NOTIFY) { got = 0; return success(); }
        if (ret == MBEDTLS_ERR_SSL_WANT_READ || ret == MBEDTLS_ERR_SSL_WANT_WRITE) {
            Status st;
            if (!waitFor(s, ret, deadline, st)) return st;
            continue;
        }
        if (s->eof) { got = 0; return success(); }   // the peer closed without close_notify
        return fail(E_Tls, "TLS read: " + describe(ret));
    }
}

inline Status writeSome(Session* s, const uint8_t* data, int count, int64_t timeoutMs, int& sent) {
    sent = 0;
    if (count <= 0) return success();
    int64_t deadline = timeoutMs >= 0 ? net::nowMillis() + timeoutMs : -1;
    for (;;) {
        int ret = mbedtls_ssl_write(&s->ssl, data, (size_t)count);
        if (ret > 0) { sent = ret; return success(); }
        if (ret == MBEDTLS_ERR_SSL_WANT_READ || ret == MBEDTLS_ERR_SSL_WANT_WRITE) {
            Status st;
            if (!waitFor(s, ret, deadline, st)) return st;
            continue;
        }
        return fail(ret == MBEDTLS_ERR_SSL_PEER_CLOSE_NOTIFY ? (int)net::E_Closed : (int)E_Tls, "TLS write: " + describe(ret));
    }
}

inline int pending(Session* s) { return (int)mbedtls_ssl_get_bytes_avail(&s->ssl); }

inline void info(Session* s, std::string& protocol, std::string& cipher) {
    const char* p = mbedtls_ssl_get_version(&s->ssl);
    const char* c = mbedtls_ssl_get_ciphersuite(&s->ssl);
    protocol = p ? p : "";
    cipher = c ? c : "";
}

inline void closeSession(Session* s) {
    if (!s) return;
    mbedtls_ssl_close_notify(&s->ssl);   // best effort: the socket is non-blocking
    delete s;
}

}  // namespace tls
}  // namespace plat
}  // namespace fire
