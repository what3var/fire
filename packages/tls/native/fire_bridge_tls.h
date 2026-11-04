/* fire native bridge "tls" (C): the natives behind `#import "tls"` (docs/networking/README.md) - TLS on top of a TCP socket of the net package: a client handshake
 * (certificate and host name verified), a server with a certificate, reading and writing the encrypted stream. The fire side (Net.TlsStream, Net.TlsServer, ...
 * - fire source) is the same in the VM and in a native build.
 *
 * This file is the one implementation of the tls natives, in C99 ("language": "c"). A TLS session is an integer handle in a table of this bridge; the socket is
 * the operating system's (the number that `__NetNativeHandle` of the net package gives): the session only reads and writes it, the net package keeps owning it
 * (and closes it). The library for the VM is a different library from the one of the net package, so the two share nothing but that number. The TLS
 * implementation comes from the platform layer (FIRE_PLATFORM_TLS_C_HEADER: fire_tls_*, OpenSSL, SChannel or mbedTLS, see platform/std/fire_tls_c.h).
 * Errors are reported like those of the net bridge: -1/false/undefined and the code and message of this thread (`__TlsLastError`, `__TlsLastErrorMessage`, the
 * slot of the io bridge, read right after the call); the codes are those of net, plus 14 (a TLS error: handshake failed, protocol error) and 15 (the
 * certificate was not accepted). */
#ifndef FIRE_BRIDGE_TLS_H
#define FIRE_BRIDGE_TLS_H

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include FIRE_PLATFORM_TLS_C_HEADER

static int64_t fire_tlsb_fail(int code, const char* message) {
    g_ioError.code = code;
    snprintf(g_ioError.message, sizeof g_ioError.message, "%s", message);
    return -1;
}
static int64_t fire_tlsb_failStatus(const FireNetStatus* s) { return fire_tlsb_fail(s->code, s->message); }
static void fire_tlsb_ok(void) { g_ioError.code = 0; g_ioError.message[0] = 0; }

/* A fire string as UTF-8 (malloc; the caller frees it; NULL when out of memory); not a string: "". */
static char* fire_tlsb_utf8(Value v) {
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
static Value fire_tlsb_str8(const char* text, OwnList* list) {
    size_t n = strlen(text), i;
    Str* s = allocStr((uint32_t)n, list);
    for (i = 0; i < n; i++) strChars(s)[i] = (fire_char16)(uint8_t)text[i];
    return StrV(s);
}

/* ---- the sessions and contexts (the handle is the index; 0 is not used, handles are not used again) ------------------------------------------- */
typedef struct FireTlsTable { void** items; size_t count, cap; } FireTlsTable;
static FireTlsTable g_tlsSessions = { NULL, 1, 0 };
static FireTlsTable g_tlsContexts = { NULL, 1, 0 };

static int64_t fire_tlsb_add(FireTlsTable* t, void* item) {
    if (t->count >= t->cap) {
        size_t cap = t->cap ? t->cap * 2 : 16;
        void** grown = (void**)realloc(t->items, sizeof(void*) * cap);
        if (grown == NULL) return -1;
        if (t->cap == 0) grown[0] = NULL;
        t->items = grown;
        t->cap = cap;
    }
    t->items[t->count] = item;
    return (int64_t)t->count++;
}
static void* fire_tlsb_at(FireTlsTable* t, Value h) {
    int64_t i = h.i;
    return i > 0 && (size_t)i < t->count ? t->items[i] : NULL;
}

/* The program has ended (the library for the VM stays loaded for the next one): everything is closed. */
static void fire_tls_reset(void) {
    size_t i;
    for (i = 1; i < g_tlsSessions.count; i++) if (g_tlsSessions.items[i]) { fire_tls_closeSession((FireTlsSession*)g_tlsSessions.items[i]); g_tlsSessions.items[i] = NULL; }
    for (i = 1; i < g_tlsContexts.count; i++) if (g_tlsContexts.items[i]) { fire_tls_freeContext((FireTlsContext*)g_tlsContexts.items[i]); g_tlsContexts.items[i] = NULL; }
}

static FireTlsSession* fire_tlsb_session(Value h) {
    FireTlsSession* s = (FireTlsSession*)fire_tlsb_at(&g_tlsSessions, h);
    if (s == NULL) fire_tlsb_fail(FIRE_NET_E_INVALID_HANDLE, "Invalid or already closed TLS session.");
    return s;
}
static int fire_tlsb_checkRange(Value bufferValue, int64_t offset, int64_t count) {
    int64_t length = bufOf(bufferValue)->length;
    if (offset < 0 || count < 0 || offset > length || count > length - offset) {
        char text[176];
        snprintf(text, sizeof text, "offset/count (%lld/%lld) are outside of the buffer (length %lld).", (long long)offset, (long long)count, (long long)length);
        fire_tlsb_fail(FIRE_NET_E_INVALID_ARGUMENT, text);
        return 0;
    }
    return 1;
}
/* A new session in the table: the handle, or -1 (out of memory: the session is closed). */
static int64_t fire_tlsb_addSession(FireTlsSession* s) {
    int64_t h = fire_tlsb_add(&g_tlsSessions, s);
    if (h < 0) { fire_tls_closeSession(s); return fire_tlsb_fail(FIRE_NET_E_OTHER, "Out of memory."); }
    fire_tlsb_ok();
    return h;
}

/* ---- natives --------------------------------------------------------------------------------------------------------------------------------- */
static Value fire_tls_LastError(void) { return Int(g_ioError.code); }
static Value fire_tls_LastErrorMessage(OwnList* list) { return fire_tlsb_str8(g_ioError.message, list); }
/* 1 if this build has TLS, else 0. */
static Value fire_tls_Supported(void) { return Int(fire_tls_supported() ? 1 : 0); }

/* The texts of the options (UTF-8, malloc'd; NULL ones are "" for the platform layer). */
typedef struct FireTlsTexts { char* items[5]; } FireTlsTexts;
static void fire_tlsb_freeTexts(FireTlsTexts* t) { int i; for (i = 0; i < 5; i++) free(t->items[i]); }
static int fire_tlsb_texts(FireTlsTexts* t, Value a, Value b, Value c, Value d, Value e) {
    Value v[5];
    int i, ok = 1;
    v[0] = a; v[1] = b; v[2] = c; v[3] = d; v[4] = e;
    for (i = 0; i < 5; i++) { t->items[i] = fire_tlsb_utf8(v[i]); if (t->items[i] == NULL) ok = 0; }
    if (!ok) { fire_tlsb_freeTexts(t); fire_tlsb_fail(FIRE_NET_E_OTHER, "Out of memory."); }
    return ok;
}

/* A client session over the socket `fd`: the host name for SNI and the check of the certificate, `verify`, a file and/or PEM text with the CA certificates
 * (neither: the system's), and a client certificate with its key (empty: none). Nothing is exchanged yet: `Handshake` does that, step by step. Returns the
 * session, -1 on an error. */
static Value fire_tls_Open(Value fd, Value serverName, Value verify, Value caFile, Value caPem, Value certPem, Value keyPem) {
    FireTlsTexts t;
    FireTlsOptions opt;
    FireNetStatus st;
    FireTlsSession* session;
    if (!fire_tlsb_texts(&t, serverName, caFile, caPem, certPem, keyPem)) return Int(-1);
    opt.verify = verify.i != 0;
    opt.serverName = t.items[0];
    opt.caFile = t.items[1];
    opt.caPem = t.items[2];
    opt.certPem = t.items[3];
    opt.keyPem = t.items[4];
    session = fire_tls_clientOpen((FireNetSock)fd.i, &opt, &st);
    fire_tlsb_freeTexts(&t);
    if (session == NULL) return Int(fire_tlsb_failStatus(&st));
    return Int(fire_tlsb_addSession(session));
}

static Value fire_tls_ServerContext(Value certPem, Value keyPem) {
    FireTlsTexts t;
    FireTlsOptions opt;
    FireNetStatus st;
    FireTlsContext* ctx;
    int64_t h;
    if (!fire_tlsb_texts(&t, certPem, keyPem, Undef(), Undef(), Undef())) return Int(-1);
    opt.verify = 0;
    opt.certPem = t.items[0];
    opt.keyPem = t.items[1];
    opt.serverName = opt.caFile = opt.caPem = t.items[2];
    ctx = fire_tls_serverContext(&opt, &st);
    fire_tlsb_freeTexts(&t);
    if (ctx == NULL) return Int(fire_tlsb_failStatus(&st));
    h = fire_tlsb_add(&g_tlsContexts, ctx);
    if (h < 0) { fire_tls_freeContext(ctx); return Int(fire_tlsb_fail(FIRE_NET_E_OTHER, "Out of memory.")); }
    fire_tlsb_ok();
    return Int(h);
}

static Value fire_tls_FreeContext(Value h) {
    FireTlsContext* c = (FireTlsContext*)fire_tlsb_at(&g_tlsContexts, h);
    if (c == NULL) { fire_tlsb_fail(FIRE_NET_E_INVALID_HANDLE, "Invalid or already freed TLS context."); return Bool(false); }
    fire_tls_freeContext(c);
    g_tlsContexts.items[h.i] = NULL;
    fire_tlsb_ok();
    return Bool(true);
}

/* A server session for the connection `fd` (the handshake is done by `Handshake`). */
static Value fire_tls_Accept(Value ctx, Value fd) {
    FireTlsContext* c = (FireTlsContext*)fire_tlsb_at(&g_tlsContexts, ctx);
    FireNetStatus st;
    FireTlsSession* session;
    if (c == NULL) return Int(fire_tlsb_fail(FIRE_NET_E_INVALID_HANDLE, "Invalid or already freed TLS context."));
    session = fire_tls_serverAccept(c, (FireNetSock)fd.i, &st);
    if (session == NULL) return Int(fire_tlsb_failStatus(&st));
    return Int(fire_tlsb_addSession(session));
}

/* Does what can be done of the handshake without waiting: 1 it is complete, 0 not yet (call again in a moment), -1 it failed. The handshake of a client sends
 * the first message in the first call; the waiting for the other side's answers is the caller's business (the natives never wait, so that other threads and
 * `terminate` are not held up). */
static Value fire_tls_Handshake(Value h) {
    FireTlsSession* s = fire_tlsb_session(h);
    FireNetStatus st;
    int done = 0;
    if (s == NULL) return Int(-1);
    if (!fire_tls_handshakeStep(s, &done, &st)) return Int(fire_tlsb_failStatus(&st));
    fire_tlsb_ok();
    return Int(done ? 1 : 0);
}

/* Up to `count` bytes (the number, 0: the other side has ended the connection), -1 on an error (code 4: nothing came in time). */
static Value fire_tls_Read(Value h, Value buffer, Value offset, Value count, Value timeoutMs) {
    FireTlsSession* s;
    FireNetStatus st;
    int got = 0;
    if (!leafAlive(buffer)) return destroyedError(buffer);
    if (!fire_tlsb_checkRange(buffer, offset.i, count.i)) return Int(-1);
    s = fire_tlsb_session(h);
    if (s == NULL) return Int(-1);
    if (!fire_tls_readSome(s, bufOf(buffer)->data + offset.i, (int)count.i, timeoutMs.i, &got, &st)) return Int(fire_tlsb_failStatus(&st));
    fire_tlsb_ok();
    return Int(got);
}

/* Sends up to `count` bytes; the number sent, -1 on an error. */
static Value fire_tls_Write(Value h, Value buffer, Value offset, Value count, Value timeoutMs) {
    FireTlsSession* s;
    FireNetStatus st;
    int sent = 0;
    if (!leafAlive(buffer)) return destroyedError(buffer);
    if (!fire_tlsb_checkRange(buffer, offset.i, count.i)) return Int(-1);
    s = fire_tlsb_session(h);
    if (s == NULL) return Int(-1);
    if (!fire_tls_writeSome(s, bufOf(buffer)->data + offset.i, (int)count.i, timeoutMs.i, &sent, &st)) return Int(fire_tlsb_failStatus(&st));
    fire_tlsb_ok();
    return Int(sent);
}

/* Decrypted bytes that can be read without touching the socket. */
static Value fire_tls_Pending(Value h) {
    FireTlsSession* s = fire_tlsb_session(h);
    if (s == NULL) return Int(-1);
    fire_tlsb_ok();
    return Int(fire_tls_pending(s));
}

/* "protocol cipher", e.g. "TLSv1.3 TLS_AES_256_GCM_SHA384". */
static Value fire_tls_Info(Value h, OwnList* list) {
    FireTlsSession* s = fire_tlsb_session(h);
    char protocol[64], cipher[128], text[200];
    if (s == NULL) return Undef();
    fire_tls_info(s, protocol, sizeof protocol, cipher, sizeof cipher);
    snprintf(text, sizeof text, "%s %s", protocol, cipher);
    fire_tlsb_ok();
    return fire_tlsb_str8(text, list);
}

/* Ends the session (close_notify) and frees it; the socket stays open. */
static Value fire_tls_Close(Value h) {
    FireTlsSession* s = (FireTlsSession*)fire_tlsb_at(&g_tlsSessions, h);
    if (s == NULL) { fire_tlsb_fail(FIRE_NET_E_INVALID_HANDLE, "Invalid or already closed TLS session."); return Bool(false); }
    fire_tls_closeSession(s);
    g_tlsSessions.items[h.i] = NULL;
    fire_tlsb_ok();
    return Bool(true);
}

#endif
