// fire native bridge "tls": the natives behind `#import "tls"` (docs/NETWORK.md) - TLS on top of a TCP socket of the net package: a client handshake (certificate and host name
// verified), a server with a certificate, reading and writing the encrypted stream. The fire side (Net.TlsStream, Net.TlsServer, ... - fire source) is the same in the VM and in a native build.
//
// A TLS session is an integer handle in a table of this bridge; the socket is the operating system's (the number that `__NetNativeHandle` of the net package gives): the session only reads
// and writes it, the net package keeps owning it (and closes it). The library for the VM is a different library from the one of the net package, so the two share nothing but that number.
// The TLS implementation comes from the platform package (FIRE_PLATFORM_TLS_HEADER: plat::tls, OpenSSL or mbedTLS, see platform/std/fire_tls_openssl.hpp).
// Errors are reported like those of the net bridge: -1/false/undefined and the code and message of this thread (`__TlsLastError`, `__TlsLastErrorMessage`); the codes are those of net, plus
// 14 (a TLS error: handshake failed, protocol error) and 15 (the certificate was not accepted).
#pragma once

#include <cstring>
#include FIRE_PLATFORM_TLS_HEADER
#include <string>
#include <vector>

namespace fire {
namespace tls {

enum Err { InvalidArgument = 1, InvalidHandle = 2, Unsupported = 9 };

#if defined(FIRE_TLS_STRUCT)   // (a board without `thread_local`: the slot hangs on the task)
#define FIRE_TLS_ERROR g_ioError
#else
struct ErrorSlot { int32_t code; char message[176]; };
inline thread_local ErrorSlot t_tlsError = {0, {0}};
#define FIRE_TLS_ERROR ::fire::tls::t_tlsError
#endif

inline int64_t fail(int code, const std::string& message) {
    FIRE_TLS_ERROR.code = code;
    size_t n = message.size() < sizeof FIRE_TLS_ERROR.message - 1 ? message.size() : sizeof FIRE_TLS_ERROR.message - 1;
    std::memcpy(FIRE_TLS_ERROR.message, message.data(), n);
    FIRE_TLS_ERROR.message[n] = 0;
    return -1;
}
inline void ok() { FIRE_TLS_ERROR.code = 0; FIRE_TLS_ERROR.message[0] = 0; }
inline int64_t fail(const plat::net::Status& s) { return fail(s.code, s.message); }

inline std::string toUtf8(Value v) {
    const Str* s = strOf(v);
    std::string out;
    out.reserve(s->length);
    for (uint32_t i = 0; i < s->length; i++) {
        uint32_t c = s->data[i];
        if (c >= 0xD800 && c <= 0xDBFF && i + 1 < s->length && s->data[i + 1] >= 0xDC00 && s->data[i + 1] <= 0xDFFF) { c = 0x10000 + ((c - 0xD800) << 10) + (s->data[i + 1] - 0xDC00); i++; }
        else if (c >= 0xD800 && c <= 0xDFFF) c = 0xFFFD;
        if (c < 0x80) out.push_back((char)c);
        else if (c < 0x800) { out.push_back((char)(0xC0 | (c >> 6))); out.push_back((char)(0x80 | (c & 0x3F))); }
        else if (c < 0x10000) { out.push_back((char)(0xE0 | (c >> 12))); out.push_back((char)(0x80 | ((c >> 6) & 0x3F))); out.push_back((char)(0x80 | (c & 0x3F))); }
        else { out.push_back((char)(0xF0 | (c >> 18))); out.push_back((char)(0x80 | ((c >> 12) & 0x3F))); out.push_back((char)(0x80 | ((c >> 6) & 0x3F))); out.push_back((char)(0x80 | (c & 0x3F))); }
    }
    return out;
}
inline Value str8(const std::string& text, OwnList* list) {
    Str* s = allocStr((uint32_t)text.size(), list);
    for (size_t i = 0; i < text.size(); i++) strChars(s)[i] = (char16_t)(uint8_t)text[i];
    return StrV(s);
}

// ---- the sessions and contexts ----------------------------------------------------------------------------------------------------------
struct Tables {
    std::vector<plat::tls::Session*> sessions{1, nullptr};
    std::vector<plat::tls::Context*> contexts{1, nullptr};
    ~Tables() {
        for (auto* s : sessions) if (s) plat::tls::closeSession(s);
        for (auto* c : contexts) if (c) plat::tls::freeContext(c);
    }
};
inline Tables& tables() { static Tables t; return t; }

/// The program has ended (the library for the VM stays loaded for the next one): everything is closed.
inline void reset() {
    Tables& t = tables();
    for (size_t i = 1; i < t.sessions.size(); i++) if (t.sessions[i]) { plat::tls::closeSession(t.sessions[i]); t.sessions[i] = nullptr; }
    for (size_t i = 1; i < t.contexts.size(); i++) if (t.contexts[i]) { plat::tls::freeContext(t.contexts[i]); t.contexts[i] = nullptr; }
}

inline plat::tls::Session* findSession(Value h) {
    int64_t i = h.i;
    if (i > 0 && (size_t)i < tables().sessions.size() && tables().sessions[(size_t)i]) return tables().sessions[(size_t)i];
    fail(InvalidHandle, "Invalid or already closed TLS session.");
    return nullptr;
}
inline bool checkRange(Value bufferValue, int64_t offset, int64_t count) {
    int64_t length = bufOf(bufferValue)->length;
    if (offset < 0 || count < 0 || offset > length || count > length - offset) {
        fail(InvalidArgument, "offset/count (" + std::to_string(offset) + "/" + std::to_string(count) + ") are outside of the buffer (length " + std::to_string(length) + ").");
        return false;
    }
    return true;
}

// ---- natives ----------------------------------------------------------------------------------------------------------------------------
inline Value LastError() { return Int(FIRE_TLS_ERROR.code); }
inline Value LastErrorMessage(OwnList* list) { return str8(FIRE_TLS_ERROR.message, list); }
/// 1 if this build has TLS, else 0.
inline Value Supported() { return Int(plat::tls::supported() ? 1 : 0); }

/// A client session over the socket `fd`: the host name for SNI and the check of the certificate, `verify`, a file and/or PEM text with the CA certificates (neither: the system's), and
/// a client certificate with its key (empty: none). Nothing is exchanged yet: `Handshake` does that, step by step. Returns the session, -1 on an error.
inline Value Open(Value fd, Value serverName, Value verify, Value caFile, Value caPem, Value certPem, Value keyPem) {
    plat::tls::Options opt;
    opt.verify = verify.i != 0;
    opt.serverName = toUtf8(serverName);
    opt.caFile = toUtf8(caFile);
    opt.caPem = toUtf8(caPem);
    opt.certPem = toUtf8(certPem);
    opt.keyPem = toUtf8(keyPem);
    plat::tls::Session* session = nullptr;
    plat::net::Status st = plat::tls::clientOpen((plat::net::Sock)fd.i, opt, session);
    if (!st.ok()) return Int(fail(st));
    tables().sessions.push_back(session);
    ok();
    return Int((int64_t)tables().sessions.size() - 1);
}

inline Value ServerContext(Value certPem, Value keyPem) {
    plat::tls::Options opt;
    opt.certPem = toUtf8(certPem);
    opt.keyPem = toUtf8(keyPem);
    plat::tls::Context* ctx = nullptr;
    plat::net::Status st = plat::tls::serverContext(opt, ctx);
    if (!st.ok()) return Int(fail(st));
    tables().contexts.push_back(ctx);
    ok();
    return Int((int64_t)tables().contexts.size() - 1);
}

inline Value FreeContext(Value h) {
    int64_t i = h.i;
    if (i <= 0 || (size_t)i >= tables().contexts.size() || !tables().contexts[(size_t)i]) { fail(InvalidHandle, "Invalid or already freed TLS context."); return Bool(false); }
    plat::tls::freeContext(tables().contexts[(size_t)i]);
    tables().contexts[(size_t)i] = nullptr;
    ok();
    return Bool(true);
}

/// A server session for the connection `fd` (the handshake is done by `Handshake`).
inline Value Accept(Value ctx, Value fd) {
    int64_t i = ctx.i;
    if (i <= 0 || (size_t)i >= tables().contexts.size() || !tables().contexts[(size_t)i]) return Int(fail(InvalidHandle, "Invalid or already freed TLS context."));
    plat::tls::Session* session = nullptr;
    plat::net::Status st = plat::tls::serverAccept(tables().contexts[(size_t)i], (plat::net::Sock)fd.i, session);
    if (!st.ok()) return Int(fail(st));
    tables().sessions.push_back(session);
    ok();
    return Int((int64_t)tables().sessions.size() - 1);
}

/// Does what can be done of the handshake without waiting: 1 it is complete, 0 not yet (call again in a moment), -1 it failed. The handshake of a client sends the first message in the
/// first call; the waiting for the other side's answers is the caller's business (the natives never wait, so that other threads and `terminate` are not held up).
inline Value Handshake(Value h) {
    plat::tls::Session* s = findSession(h);
    if (!s) return Int(-1);
    bool done = false;
    plat::net::Status st = plat::tls::handshakeStep(s, done);
    if (!st.ok()) return Int(fail(st));
    ok();
    return Int(done ? 1 : 0);
}

/// Up to `count` bytes (the number, 0: the other side has ended the connection), -1 on an error (code 4: nothing came in time).
inline Value Read(Value h, Value buffer, Value offset, Value count, Value timeoutMs) {
    if (!leafAlive(buffer)) return destroyedError(buffer);
    if (!checkRange(buffer, offset.i, count.i)) return Int(-1);
    plat::tls::Session* s = findSession(h);
    if (!s) return Int(-1);
    int got = 0;
    plat::net::Status st = plat::tls::readSome(s, bufOf(buffer)->bytes() + offset.i, (int)count.i, timeoutMs.i, got);
    if (!st.ok()) return Int(fail(st));
    ok();
    return Int(got);
}

/// Sends up to `count` bytes; the number sent, -1 on an error.
inline Value Write(Value h, Value buffer, Value offset, Value count, Value timeoutMs) {
    if (!leafAlive(buffer)) return destroyedError(buffer);
    if (!checkRange(buffer, offset.i, count.i)) return Int(-1);
    plat::tls::Session* s = findSession(h);
    if (!s) return Int(-1);
    int sent = 0;
    plat::net::Status st = plat::tls::writeSome(s, bufOf(buffer)->bytes() + offset.i, (int)count.i, timeoutMs.i, sent);
    if (!st.ok()) return Int(fail(st));
    ok();
    return Int(sent);
}

/// Decrypted bytes that can be read without touching the socket.
inline Value Pending(Value h) {
    plat::tls::Session* s = findSession(h);
    if (!s) return Int(-1);
    ok();
    return Int(plat::tls::pending(s));
}

/// "protocol cipher", e.g. "TLSv1.3 TLS_AES_256_GCM_SHA384".
inline Value Info(Value h, OwnList* list) {
    plat::tls::Session* s = findSession(h);
    if (!s) return Undef();
    std::string protocol, cipher;
    plat::tls::info(s, protocol, cipher);
    ok();
    return str8(protocol + " " + cipher, list);
}

/// Ends the session (close_notify) and frees it; the socket stays open.
inline Value Close(Value h) {
    int64_t i = h.i;
    if (i <= 0 || (size_t)i >= tables().sessions.size() || !tables().sessions[(size_t)i]) { fail(InvalidHandle, "Invalid or already closed TLS session."); return Bool(false); }
    plat::tls::closeSession(tables().sessions[(size_t)i]);
    tables().sessions[(size_t)i] = nullptr;
    ok();
    return Bool(true);
}

}  // namespace tls
}  // namespace fire
