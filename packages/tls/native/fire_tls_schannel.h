/* fire tls package: TLS part in C on SChannel: the TLS of Windows (SSPI, secur32.dll). Nothing has to be installed, the certificates are those of the
 * Windows certificate store (the same trust as the browsers of the machine and .NET). It is the default of the `windows` platform (FIRE_TLS_OPENSSL selects OpenSSL
 * there, see fire_tls_desktop.h). The interface: fire_tls_c.h.
 *
 * How it works:
 *   - The handshake is done with InitializeSecurityContext/AcceptSecurityContext in steps: `handshakeStep` sends what is waiting, reads what is there (the socket
 *     is non-blocking), feeds it to SChannel and returns when it has to wait for the other side - the same model as the OpenSSL backend.
 *   - Records are made by EncryptMessage/DecryptMessage on buffers of this file; an incomplete record waits in the session until the rest has come.
 *   - The certificate of a server is checked by this file after the handshake (SChannel is told not to): the chain is built with CertGetCertificateChain (the
 *     system's roots, or - when CA certificates are given - only those) and judged by CertVerifyCertificateChainPolicy (CERT_CHAIN_POLICY_SSL: dates, chain,
 *     purpose and the host name). An IP address as host name is compared with the IP entries of the certificate here (the policy does not do that). No
 *     revocation checks (as with the OpenSSL backend).
 *   - A server certificate with its private key comes as PEM text: the certificates are put in a memory store (the chain the clients get), an RSA key is imported
 *     into a key container of the CryptoAPI (named for this process, deleted when the context is freed), an ECDSA key (P-256, P-384, P-521) into a CNG key. PEM
 *     keys of the kinds "PRIVATE KEY" (PKCS#8), "RSA PRIVATE KEY" and "EC PRIVATE KEY" (unencrypted) are understood. The same works for the certificate a client
 *     shows.
 *   - TLS 1.2 and 1.3 (1.3 where the Windows version has it: from Windows 11 and Server 2022; elsewhere SChannel agrees on 1.2).
 * Link libraries: ws2_32 secur32 crypt32 ncrypt (the package brings them). */
#ifndef FIRE_TLS_SCHANNEL_H
#define FIRE_TLS_SCHANNEL_H

#ifndef SECURITY_WIN32
#define SECURITY_WIN32
#endif

#include "fire_tls_c.h"   /* (first: the network header brings winsock2.h before windows.h) */

#include <stddef.h>
#include <wchar.h>
#include <windows.h>
#include <wincrypt.h>
#include <ncrypt.h>
#include <security.h>
#include <schnlsp.h>
#include <sspi.h>

#ifndef SP_PROT_TLS1_2_CLIENT
#define SP_PROT_TLS1_2_CLIENT 0x00000800
#endif
#ifndef SP_PROT_TLS1_3_CLIENT
#define SP_PROT_TLS1_3_CLIENT 0x00002000
#endif
#ifndef SP_PROT_TLS1_2_SERVER
#define SP_PROT_TLS1_2_SERVER 0x00000400
#endif
#ifndef SP_PROT_TLS1_3_SERVER
#define SP_PROT_TLS1_3_SERVER 0x00001000
#endif
#ifndef SECURITY_FLAG_IGNORE_CERT_CN_INVALID
#define SECURITY_FLAG_IGNORE_CERT_CN_INVALID 0x00001000
#endif
#ifndef SCH_USE_STRONG_CRYPTO
#define SCH_USE_STRONG_CRYPTO 0x00400000
#endif
#ifndef SCH_CRED_NO_SERVERNAME_CHECK
#define SCH_CRED_NO_SERVERNAME_CHECK 0x00000004
#endif
#ifndef SCH_CRED_MANUAL_CRED_VALIDATION
#define SCH_CRED_MANUAL_CRED_VALIDATION 0x00000008
#endif
#ifndef SCH_CRED_NO_DEFAULT_CREDS
#define SCH_CRED_NO_DEFAULT_CREDS 0x00000010
#endif
#ifndef SCH_CRED_NO_SYSTEM_MAPPER
#define SCH_CRED_NO_SYSTEM_MAPPER 0x00000002
#endif
#ifndef CERT_SET_PROPERTY_INHIBIT_PERSIST_FLAG
#define CERT_SET_PROPERTY_INHIBIT_PERSIST_FLAG 0x40000000
#endif
#ifndef CERT_NCRYPT_KEY_HANDLE_PROP_ID
#define CERT_NCRYPT_KEY_HANDLE_PROP_ID 78
#endif

/* A growing buffer of bytes. */
typedef struct FireTlsBytes { uint8_t* data; size_t size, cap; } FireTlsBytes;

/* A certificate (with the chain behind it) and its private key: where the key lives depends on its kind. */
typedef struct FireTlsSchKeys {
    PCCERT_CONTEXT cert;
    HCERTSTORE store;
    HCRYPTPROV prov;             /* an RSA key: the container of the CryptoAPI (named, deleted when freed) */
    wchar_t container[96];       /* ("" = none) */
    NCRYPT_KEY_HANDLE nkey;      /* an ECDSA key: the CNG key */
} FireTlsSchKeys;

typedef struct FireTlsContext {
    CredHandle cred;
    int haveCred;
    FireTlsSchKeys keys;
} FireTlsContext;

typedef struct FireTlsSession {
    FireNetSock fd;
    int client;
    int verify;
    char* serverName;            /* (a client's, "" = none) */
    CredHandle cred;
    int haveCred;
    int ownCred;
    CredHandle* credPtr;         /* the credentials in use (own, or those of the server context) */
    CtxtHandle ctx;
    int haveCtx;
    int first;                   /* the first call of the handshake (a client sends the hello without input) */
    int handshakeDone;
    int needMore;                /* the handshake waits for bytes of the other side */
    int needMoreEnc;             /* the buffered records are not complete */
    int renegotiating;           /* a handshake message after the handshake (TLS 1.3) is being handled */
    int credRetries;
    int eof;
    int verified;
    FireTlsBytes enc;            /* received, not yet decrypted */
    FireTlsBytes out;            /* encrypted, not yet sent */
    FireTlsBytes plain;          /* decrypted, not yet given out */
    size_t plainPos;
    SecPkgContext_StreamSizes sizes;
    int haveSizes;
    HCERTSTORE roots;            /* the CA certificates that were given (client) */
    HCERTCHAINENGINE engine;
    FireTlsSchKeys clientKeys;
} FireTlsSession;

static int fire_tls_supported(void) { return 1; }

/* ---- small helpers ---------------------------------------------------------------------------------------------------------------------------------------- */
static int fire_tls_oom(FireNetStatus* st) { return fire_net_fail(st, FIRE_NET_E_OTHER, "Out of memory."); }

static int fire_tls_bytesReserve(FireTlsBytes* b, size_t need) {
    size_t cap;
    uint8_t* grown;
    if (need <= b->cap) return 1;
    cap = b->cap ? b->cap : 4096;
    while (cap < need) {
        if (cap > ((size_t)-1) / 2) { cap = need; break; }
        cap *= 2;
    }
    grown = (uint8_t*)realloc(b->data, cap);
    if (grown == NULL) return 0;
    b->data = grown;
    b->cap = cap;
    return 1;
}
static int fire_tls_bytesAppend(FireTlsBytes* b, const void* data, size_t n) {
    if (n == 0) return 1;
    if (!fire_tls_bytesReserve(b, b->size + n)) return 0;
    memcpy(b->data + b->size, data, n);
    b->size += n;
    return 1;
}
/* Removes the first `n` bytes. */
static void fire_tls_bytesDrop(FireTlsBytes* b, size_t n) {
    if (n >= b->size) { b->size = 0; return; }
    memmove(b->data, b->data + n, b->size - n);
    b->size -= n;
}
static void fire_tls_bytesFree(FireTlsBytes* b) {
    free(b->data);
    b->data = NULL;
    b->size = b->cap = 0;
}

/* UTF-8 to UTF-16 (malloc'd, free it); NULL when the memory is out. */
static wchar_t* fire_tls_schWide(const char* s) {
    int n = MultiByteToWideChar(CP_UTF8, 0, s, -1, NULL, 0);
    wchar_t* w = (wchar_t*)malloc(sizeof(wchar_t) * (size_t)(n > 0 ? n : 1));
    if (w == NULL) return NULL;
    if (n <= 0 || MultiByteToWideChar(CP_UTF8, 0, s, -1, w, n) <= 0) w[0] = 0;
    return w;
}

static char* fire_tls_schCopy(const char* s) {
    size_t n = strlen(s);
    char* copy = (char*)malloc(n + 1);
    if (copy != NULL) memcpy(copy, s, n + 1);
    return copy;
}

/* An IP address as text: its bytes (4 or 16) in `bytes`, their number in `*length`. */
static int fire_tls_schIsIpLiteral(const char* host, uint8_t bytes[16], size_t* length) {
    *length = 0;
    if (inet_pton(AF_INET, host, bytes) == 1) { *length = 4; return 1; }
    if (inet_pton(AF_INET6, host, bytes) == 1) { *length = 16; return 1; }
    return 0;
}

/* The whole file as text (malloc'd, with a terminating zero): 1 done, 0 it could not be read, -1 the memory is out. */
static int fire_tls_schReadFile(const char* path, char** text) {
    FireTlsBytes b = {NULL, 0, 0};
    wchar_t* wpath;
    HANDLE h;
    char buf[8192];
    DWORD got = 0;
    *text = NULL;
    wpath = fire_tls_schWide(path);
    if (wpath == NULL) return -1;
    h = CreateFileW(wpath, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    free(wpath);
    if (h == INVALID_HANDLE_VALUE) return 0;
    while (ReadFile(h, buf, sizeof buf, &got, NULL) && got > 0) {
        if (!fire_tls_bytesAppend(&b, buf, got)) { CloseHandle(h); fire_tls_bytesFree(&b); return -1; }
    }
    CloseHandle(h);
    if (!fire_tls_bytesAppend(&b, "", 1)) { fire_tls_bytesFree(&b); return -1; }
    *text = (char*)b.data;
    return 1;
}

/* The certificate-chain error of the policy as text (`buf` holds the text of an unknown one). */
static const char* fire_tls_schChainErrorText(unsigned long e, char* buf, size_t size) {
    switch (e) {
        case (unsigned long)CERT_E_UNTRUSTEDROOT: return "the certificate is not signed by a trusted authority";
        case (unsigned long)CERT_E_CHAINING: return "the certificate chain is not complete or not trusted";
        case (unsigned long)CERT_E_EXPIRED: return "the certificate has expired or is not valid yet";
        case (unsigned long)CERT_E_CN_NO_MATCH: return "the certificate is not for this host name";
        case (unsigned long)CERT_E_WRONG_USAGE: return "the certificate is not for this purpose";
        case (unsigned long)CERT_E_UNTRUSTEDTESTROOT: return "the certificate is signed by a test authority";
        case (unsigned long)CERT_E_REVOKED: return "the certificate has been revoked";
        case (unsigned long)TRUST_E_CERT_SIGNATURE: return "the signature of the certificate is not valid";
        case (unsigned long)CERT_E_ROLE: return "the certificate may not be an authority";
        default: snprintf(buf, size, "error 0x%08lX", e); return buf;
    }
}

/* ---- PEM and DER ------------------------------------------------------------------------------------------------------------------------------------------ */
typedef struct FireTlsPemBlock {
    const char* label;           /* (in the PEM text) */
    size_t labelLength;
    uint8_t* der;
    DWORD size;
} FireTlsPemBlock;
typedef struct FireTlsPemList { FireTlsPemBlock* items; size_t count, cap; } FireTlsPemList;

static void fire_tls_schPemFree(FireTlsPemList* l) {
    size_t i;
    for (i = 0; i < l->count; i++) free(l->items[i].der);
    free(l->items);
    l->items = NULL;
    l->count = l->cap = 0;
}

/* The blocks of a PEM text (those that cannot be decoded are left out): 0 when the memory is out. */
static int fire_tls_schPemBlocks(const char* text, FireTlsPemList* list) {
    const char* pos = text;
    list->items = NULL;
    list->count = list->cap = 0;
    for (;;) {
        const char* b = strstr(pos, "-----BEGIN ");
        const char* le;
        const char* label;
        const char* bodyStart;
        const char* e;
        size_t labelLength;
        DWORD size = 0;
        if (b == NULL) break;
        le = strstr(b + 11, "-----");
        if (le == NULL) break;
        label = b + 11;
        labelLength = (size_t)(le - label);
        bodyStart = le + 5;
        for (e = bodyStart;; e++) {   /* "-----END <label>-----" */
            e = strstr(e, "-----END ");
            if (e == NULL) break;
            if (strncmp(e + 9, label, labelLength) == 0 && strncmp(e + 9 + labelLength, "-----", 5) == 0) break;
        }
        if (e == NULL) break;
        if (CryptStringToBinaryA(bodyStart, (DWORD)(e - bodyStart), CRYPT_STRING_BASE64, NULL, &size, NULL, NULL) && size > 0) {
            FireTlsPemBlock block;
            block.label = label;
            block.labelLength = labelLength;
            block.size = size;
            block.der = (uint8_t*)malloc(size);
            if (block.der == NULL) { fire_tls_schPemFree(list); return 0; }
            if (CryptStringToBinaryA(bodyStart, (DWORD)(e - bodyStart), CRYPT_STRING_BASE64, block.der, &block.size, NULL, NULL)) {
                if (list->count == list->cap) {
                    size_t cap = list->cap ? list->cap * 2 : 4;
                    FireTlsPemBlock* grown = (FireTlsPemBlock*)realloc(list->items, sizeof(FireTlsPemBlock) * cap);
                    if (grown == NULL) { free(block.der); fire_tls_schPemFree(list); return 0; }
                    list->items = grown;
                    list->cap = cap;
                }
                list->items[list->count++] = block;
            } else {
                free(block.der);
            }
        }
        pos = e + 9 + labelLength + 5;
    }
    return 1;
}

static int fire_tls_schPemIs(const FireTlsPemBlock* b, const char* label) { return b->labelLength == strlen(label) && memcmp(b->label, label, b->labelLength) == 0; }
static int fire_tls_schPemIsCert(const FireTlsPemBlock* b) {
    return fire_tls_schPemIs(b, "CERTIFICATE") || fire_tls_schPemIs(b, "TRUSTED CERTIFICATE") || fire_tls_schPemIs(b, "X509 CERTIFICATE");
}

/* One DER element: its tag, its content and where the next one starts. */
typedef struct FireTlsDer {
    uint8_t tag;
    const uint8_t* value;
    size_t length;
    const uint8_t* next;
} FireTlsDer;
static int fire_tls_schDerRead(const uint8_t* p, const uint8_t* end, FireTlsDer* d) {
    size_t len;
    const uint8_t* v;
    if (end - p < 2) return 0;
    d->tag = p[0];
    len = p[1];
    v = p + 2;
    if (len & 0x80) {
        size_t n = len & 0x7F, i;
        if (n == 0 || n > 4 || (size_t)(end - v) < n) return 0;
        len = 0;
        for (i = 0; i < n; i++) len = (len << 8) | v[i];
        v += n;
    }
    if ((size_t)(end - v) < len) return 0;
    d->value = v;
    d->length = len;
    d->next = v + len;
    return 1;
}
static int fire_tls_schOidIs(const FireTlsDer* d, const uint8_t* bytes, size_t n) { return d->tag == 0x06 && d->length == n && memcmp(d->value, bytes, n) == 0; }
static int fire_tls_schBytesAre(const uint8_t* a, size_t n, const uint8_t* b, size_t m) { return a != NULL && n == m && memcmp(a, b, n) == 0; }
static const uint8_t fire_tls_schOidRsa[] = {0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x01, 0x01};
static const uint8_t fire_tls_schOidEc[] = {0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x02, 0x01};
static const uint8_t fire_tls_schOidP256[] = {0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x03, 0x01, 0x07};
static const uint8_t fire_tls_schOidP384[] = {0x2B, 0x81, 0x04, 0x00, 0x22};
static const uint8_t fire_tls_schOidP521[] = {0x2B, 0x81, 0x04, 0x00, 0x23};

/* ---- certificates with keys ------------------------------------------------------------------------------------------------------------------------------- */
static void fire_tls_schFreeKeys(FireTlsSchKeys* k) {
    if (k->cert) CertFreeCertificateContext(k->cert);
    if (k->store) CertCloseStore(k->store, 0);
    if (k->prov) CryptReleaseContext(k->prov, 0);
    if (k->container[0]) {
        HCRYPTPROV p = 0;
        CryptAcquireContextW(&p, k->container, MS_ENH_RSA_AES_PROV_W, PROV_RSA_AES, CRYPT_DELETEKEYSET);   /* (deletes the container of the imported key) */
    }
    if (k->nkey) NCryptFreeObject(k->nkey);
    memset(k, 0, sizeof *k);
}

static int fire_tls_schImportRsaKey(FireTlsSchKeys* k, const uint8_t* pkcs1, size_t length, FireNetStatus* st) {
    static LONG counter = 0;
    BYTE* blob = NULL;
    DWORD blobSize = 0;
    wchar_t name[96];
    HCRYPTPROV prov = 0;
    HCRYPTKEY key = 0;
    BOOL imported;
    DWORD importError;
    CRYPT_KEY_PROV_INFO info;
    if (!CryptDecodeObjectEx(X509_ASN_ENCODING | PKCS_7_ASN_ENCODING, PKCS_RSA_PRIVATE_KEY, pkcs1, (DWORD)length, CRYPT_DECODE_ALLOC_FLAG, NULL, &blob, &blobSize))
        return fire_net_fail(st, FIRE_NET_E_INVALID_ARGUMENT, "The RSA private key could not be read (error 0x%08lX).", (unsigned long)GetLastError());
    swprintf(name, 96, L"fire-tls-%lu-%lu-%ld", (unsigned long)GetCurrentProcessId(), (unsigned long)GetTickCount(), (long)InterlockedIncrement(&counter));
    if (!CryptAcquireContextW(&prov, name, MS_ENH_RSA_AES_PROV_W, PROV_RSA_AES, CRYPT_NEWKEYSET)) {
        DWORD e = GetLastError();
        LocalFree(blob);
        return fire_net_fail(st, FIRE_NET_E_OTHER, "A key container could not be made (error 0x%08lX).", (unsigned long)e);
    }
    imported = CryptImportKey(prov, blob, blobSize, 0, 0, &key);
    importError = GetLastError();
    LocalFree(blob);
    if (!imported) {
        HCRYPTPROV p = 0;
        CryptReleaseContext(prov, 0);
        CryptAcquireContextW(&p, name, MS_ENH_RSA_AES_PROV_W, PROV_RSA_AES, CRYPT_DELETEKEYSET);
        return fire_net_fail(st, FIRE_NET_E_INVALID_ARGUMENT, "The RSA private key could not be imported (error 0x%08lX).", (unsigned long)importError);
    }
    CryptDestroyKey(key);
    k->prov = prov;
    memcpy(k->container, name, sizeof name);
    memset(&info, 0, sizeof info);
    info.pwszContainerName = k->container;
    info.pwszProvName = (LPWSTR)MS_ENH_RSA_AES_PROV_W;
    info.dwProvType = PROV_RSA_AES;
    info.dwKeySpec = AT_KEYEXCHANGE;
    if (!CertSetCertificateContextProperty(k->cert, CERT_KEY_PROV_INFO_PROP_ID, 0, &info))
        return fire_net_fail(st, FIRE_NET_E_OTHER, "The key could not be connected to the certificate (error 0x%08lX).", (unsigned long)GetLastError());
    return 1;
}

static int fire_tls_schImportEcKey(FireTlsSchKeys* k, const uint8_t* sec1, size_t length, const uint8_t* oidFromHeader, size_t oidLength, FireNetStatus* st) {
    /* ECPrivateKey ::= SEQUENCE { INTEGER 1, OCTET STRING privateKey, [0] OID curve OPTIONAL, [1] BIT STRING publicKey OPTIONAL } */
    FireTlsDer seq, version, priv;
    const uint8_t* p;
    const uint8_t* end;
    const uint8_t* curve = oidFromHeader;
    size_t curveLength = oidLength;
    const uint8_t* pub = NULL;
    size_t pubLength = 0;
    ULONG magic = 0, size = 0;
    uint8_t blob[8 + 3 * 66];
    DWORD blobSize;
    NCRYPT_PROV_HANDLE provider = 0;
    NCRYPT_KEY_HANDLE key = 0;
    SECURITY_STATUS ss;
    if (!fire_tls_schDerRead(sec1, sec1 + length, &seq) || seq.tag != 0x30) return fire_net_fail(st, FIRE_NET_E_INVALID_ARGUMENT, "The EC private key could not be read.");
    p = seq.value;
    end = seq.value + seq.length;
    if (!fire_tls_schDerRead(p, end, &version) || version.tag != 0x02 || !fire_tls_schDerRead(version.next, end, &priv) || priv.tag != 0x04)
        return fire_net_fail(st, FIRE_NET_E_INVALID_ARGUMENT, "The EC private key could not be read.");
    p = priv.next;
    while (p < end) {
        FireTlsDer item;
        if (!fire_tls_schDerRead(p, end, &item)) break;
        if (item.tag == 0xA0) {
            FireTlsDer oid;
            if (fire_tls_schDerRead(item.value, item.value + item.length, &oid) && oid.tag == 0x06) { curve = oid.value; curveLength = oid.length; }
        } else if (item.tag == 0xA1) {
            FireTlsDer bits;
            if (fire_tls_schDerRead(item.value, item.value + item.length, &bits) && bits.tag == 0x03 && bits.length > 1) { pub = bits.value + 1; pubLength = bits.length - 1; }
        }
        p = item.next;
    }
    if (fire_tls_schBytesAre(curve, curveLength, fire_tls_schOidP256, sizeof fire_tls_schOidP256)) { magic = 0x32534345; size = 32; }          /* ECS2 */
    else if (fire_tls_schBytesAre(curve, curveLength, fire_tls_schOidP384, sizeof fire_tls_schOidP384)) { magic = 0x34534345; size = 48; }   /* ECS4 */
    else if (fire_tls_schBytesAre(curve, curveLength, fire_tls_schOidP521, sizeof fire_tls_schOidP521)) { magic = 0x36534345; size = 66; }   /* ECS6 */
    else return fire_net_fail(st, FIRE_NET_E_INVALID_ARGUMENT, "The curve of the EC private key is not supported (P-256, P-384 and P-521 are).");
    if (pubLength != 1 + 2 * (size_t)size || pub[0] != 0x04) return fire_net_fail(st, FIRE_NET_E_INVALID_ARGUMENT, "The EC private key has no public key (an uncompressed point is needed).");
    if (priv.length > size) return fire_net_fail(st, FIRE_NET_E_INVALID_ARGUMENT, "The EC private key has the wrong size.");
    blobSize = (DWORD)(8 + 3 * (size_t)size);
    memset(blob, 0, sizeof blob);
    memcpy(blob, &magic, 4);
    memcpy(blob + 4, &size, 4);
    memcpy(blob + 8, pub + 1, 2 * (size_t)size);
    memcpy(blob + 8 + 2 * (size_t)size + (size - priv.length), priv.value, priv.length);
    ss = NCryptOpenStorageProvider(&provider, MS_KEY_STORAGE_PROVIDER, 0);
    if (ss != ERROR_SUCCESS) return fire_net_fail(st, FIRE_NET_E_OTHER, "The key storage of Windows could not be opened (error 0x%08lX).", (unsigned long)ss);
    ss = NCryptImportKey(provider, 0, BCRYPT_ECCPRIVATE_BLOB, NULL, &key, blob, blobSize, 0);
    NCryptFreeObject(provider);
    if (ss != ERROR_SUCCESS) return fire_net_fail(st, FIRE_NET_E_INVALID_ARGUMENT, "The EC private key could not be imported (error 0x%08lX).", (unsigned long)ss);
    k->nkey = key;
    if (!CertSetCertificateContextProperty(k->cert, CERT_NCRYPT_KEY_HANDLE_PROP_ID, CERT_SET_PROPERTY_INHIBIT_PERSIST_FLAG, &key))
        return fire_net_fail(st, FIRE_NET_E_OTHER, "The key could not be connected to the certificate (error 0x%08lX).", (unsigned long)GetLastError());
    return 1;
}

/* The private key of a PEM block into `out` (its certificate is there already). */
static int fire_tls_schImportKey(FireTlsSchKeys* out, const FireTlsPemBlock* keyBlock, FireNetStatus* st) {
    const uint8_t* der = keyBlock->der;
    size_t size = keyBlock->size;
    if (fire_tls_schPemIs(keyBlock, "RSA PRIVATE KEY")) return fire_tls_schImportRsaKey(out, der, size, st);
    if (fire_tls_schPemIs(keyBlock, "EC PRIVATE KEY")) return fire_tls_schImportEcKey(out, der, size, NULL, 0, st);
    {
        /* PrivateKeyInfo ::= SEQUENCE { INTEGER 0, SEQUENCE { OID algorithm, parameters }, OCTET STRING key } */
        FireTlsDer seq, version, alg, key, oid, params;
        int ok = fire_tls_schDerRead(der, der + size, &seq) && seq.tag == 0x30 && fire_tls_schDerRead(seq.value, seq.value + seq.length, &version) && version.tag == 0x02 &&
                 fire_tls_schDerRead(version.next, seq.value + seq.length, &alg) && alg.tag == 0x30 && fire_tls_schDerRead(alg.next, seq.value + seq.length, &key) && key.tag == 0x04;
        ok = ok && fire_tls_schDerRead(alg.value, alg.value + alg.length, &oid) && oid.tag == 0x06;
        if (!ok) return fire_net_fail(st, FIRE_NET_E_INVALID_ARGUMENT, "The private key (PKCS#8) could not be read.");
        if (fire_tls_schOidIs(&oid, fire_tls_schOidRsa, sizeof fire_tls_schOidRsa)) return fire_tls_schImportRsaKey(out, key.value, key.length, st);
        if (fire_tls_schOidIs(&oid, fire_tls_schOidEc, sizeof fire_tls_schOidEc)) {
            const uint8_t* curve = NULL;
            size_t curveLength = 0;
            if (fire_tls_schDerRead(oid.next, alg.value + alg.length, &params) && params.tag == 0x06) { curve = params.value; curveLength = params.length; }
            return fire_tls_schImportEcKey(out, key.value, key.length, curve, curveLength, st);
        }
        return fire_net_fail(st, FIRE_NET_E_INVALID_ARGUMENT, "The kind of the private key is not supported (RSA and EC are).");
    }
}

/* Makes a certificate context (its store holds the whole chain of the PEM text) and connects the private key to it. */
static int fire_tls_schKeyedCert(const char* certPem, const char* keyPem, FireTlsSchKeys* out, FireNetStatus* st) {
    FireTlsPemList certs, keys;
    const FireTlsPemBlock* keyBlock = NULL;
    size_t i, chain = 0;
    int ok;
    memset(out, 0, sizeof *out);
    if (!fire_tls_schPemBlocks(certPem, &certs)) return fire_tls_oom(st);
    for (i = 0; i < certs.count; i++) if (fire_tls_schPemIsCert(&certs.items[i])) chain++;
    if (chain == 0) { fire_tls_schPemFree(&certs); return fire_net_fail(st, FIRE_NET_E_INVALID_ARGUMENT, "The certificate (PEM) contains no certificate."); }
    out->store = CertOpenStore(CERT_STORE_PROV_MEMORY, 0, 0, 0, NULL);
    if (!out->store) { fire_tls_schPemFree(&certs); return fire_net_fail(st, FIRE_NET_E_OTHER, "A certificate store could not be made."); }
    for (i = 0; i < certs.count; i++) {
        const FireTlsPemBlock* b = &certs.items[i];
        PCCERT_CONTEXT added = NULL;
        int firstOne = out->cert == NULL;
        if (!fire_tls_schPemIsCert(b)) continue;
        if (!CertAddEncodedCertificateToStore(out->store, X509_ASN_ENCODING, b->der, b->size, CERT_STORE_ADD_ALWAYS, firstOne ? &added : NULL)) {
            fire_tls_schPemFree(&certs);
            fire_tls_schFreeKeys(out);
            return fire_net_fail(st, FIRE_NET_E_INVALID_ARGUMENT, "A certificate (PEM) could not be read.");
        }
        if (firstOne) out->cert = added;
    }
    fire_tls_schPemFree(&certs);
    if (!fire_tls_schPemBlocks(keyPem, &keys)) { fire_tls_schFreeKeys(out); return fire_tls_oom(st); }
    for (i = 0; i < keys.count && keyBlock == NULL; i++) {
        const FireTlsPemBlock* b = &keys.items[i];
        if (fire_tls_schPemIs(b, "PRIVATE KEY") || fire_tls_schPemIs(b, "RSA PRIVATE KEY") || fire_tls_schPemIs(b, "EC PRIVATE KEY")) keyBlock = b;
    }
    if (keyBlock == NULL) {
        fire_tls_schPemFree(&keys);
        fire_tls_schFreeKeys(out);
        return fire_net_fail(st, FIRE_NET_E_INVALID_ARGUMENT, "The private key (PEM) is missing or encrypted (supported: PRIVATE KEY, RSA PRIVATE KEY and EC PRIVATE KEY without a password).");
    }
    ok = fire_tls_schImportKey(out, keyBlock, st);
    fire_tls_schPemFree(&keys);
    if (!ok) fire_tls_schFreeKeys(out);
    return ok;
}

/* ---- credentials ------------------------------------------------------------------------------------------------------------------------------------------ */
static int fire_tls_schCredentials(int client, const FireTlsSchKeys* keys, CredHandle* cred, FireNetStatus* st) {
    SCHANNEL_CRED sc;
    PCCERT_CONTEXT certs[1];
    TimeStamp expiry;
    DWORD protocols[2];
    SECURITY_STATUS ss = SEC_E_INTERNAL_ERROR;
    int attempt;
    memset(&sc, 0, sizeof sc);
    sc.dwVersion = SCHANNEL_CRED_VERSION;
    if (keys && keys->cert) {
        certs[0] = keys->cert;
        sc.cCreds = 1;
        sc.paCred = certs;
    }
    /* the certificate of the server is checked by this file, not by SChannel (it has to know the CA certificates that the program gives) */
    sc.dwFlags = SCH_USE_STRONG_CRYPTO | (client ? (SCH_CRED_NO_DEFAULT_CREDS | SCH_CRED_MANUAL_CRED_VALIDATION | SCH_CRED_NO_SERVERNAME_CHECK) : SCH_CRED_NO_SYSTEM_MAPPER);
    if (keys && keys->cert && client) sc.dwFlags &= ~(DWORD)SCH_CRED_NO_DEFAULT_CREDS;
    protocols[0] = client ? (DWORD)(SP_PROT_TLS1_2_CLIENT | SP_PROT_TLS1_3_CLIENT) : (DWORD)(SP_PROT_TLS1_2_SERVER | SP_PROT_TLS1_3_SERVER);
    protocols[1] = client ? (DWORD)SP_PROT_TLS1_2_CLIENT : (DWORD)SP_PROT_TLS1_2_SERVER;
    for (attempt = 0; attempt < 2; attempt++) {   /* (a Windows without TLS 1.3 may not like the flag: then TLS 1.2 alone) */
        sc.grbitEnabledProtocols = protocols[attempt];
        ss = AcquireCredentialsHandleA(NULL, (SEC_CHAR*)UNISP_NAME_A, client ? SECPKG_CRED_OUTBOUND : SECPKG_CRED_INBOUND, NULL, &sc, NULL, NULL, cred, &expiry);
        if (ss == SEC_E_OK) return 1;
    }
    return fire_net_fail(st, FIRE_TLS_E_TLS, "TLS could not be started (AcquireCredentialsHandle 0x%08lX).", (unsigned long)ss);
}

/* ---- sessions --------------------------------------------------------------------------------------------------------------------------------------------- */
static void fire_tls_freeSession(FireTlsSession* s) {
    if (s == NULL) return;
    if (s->haveCtx) DeleteSecurityContext(&s->ctx);
    if (s->haveCred && s->ownCred) FreeCredentialsHandle(&s->cred);
    if (s->engine) CertFreeCertificateChainEngine(s->engine);
    if (s->roots) CertCloseStore(s->roots, 0);
    fire_tls_schFreeKeys(&s->clientKeys);
    fire_tls_bytesFree(&s->enc);
    fire_tls_bytesFree(&s->out);
    fire_tls_bytesFree(&s->plain);
    free(s->serverName);
    free(s);
}

static FireTlsSession* fire_tls_newSession(FireNetSock fd) {
    FireTlsSession* s = (FireTlsSession*)calloc(1, sizeof(FireTlsSession));
    if (s == NULL) return NULL;
    s->fd = fd;
    s->client = 1;
    s->verify = 1;
    s->first = 1;
    return s;
}

static int fire_tls_schAddRoots(HCERTSTORE store, const char* pem, int* count, FireNetStatus* st) {
    FireTlsPemList blocks;
    size_t i;
    if (!fire_tls_schPemBlocks(pem, &blocks)) return fire_tls_oom(st);
    for (i = 0; i < blocks.count; i++) {
        const FireTlsPemBlock* b = &blocks.items[i];
        if (!fire_tls_schPemIsCert(b)) continue;
        if (!CertAddEncodedCertificateToStore(store, X509_ASN_ENCODING, b->der, b->size, CERT_STORE_ADD_ALWAYS, NULL)) {
            DWORD e = GetLastError();
            fire_tls_schPemFree(&blocks);
            return fire_net_fail(st, FIRE_NET_E_INVALID_ARGUMENT, "A CA certificate could not be read (error 0x%08lX).", (unsigned long)e);
        }
        (*count)++;
    }
    fire_tls_schPemFree(&blocks);
    return 1;
}

/* The CA certificates that were given, as the only trusted authorities of the session. */
static int fire_tls_schSetupRoots(FireTlsSession* s, const FireTlsOptions* opt, FireNetStatus* st) {
    /* (the structure with all of its fields, whatever the headers know: Windows 8 and newer have the last one, Windows 7 does not take it into account) */
    typedef struct FireTlsSchEngineConfig {
        DWORD cbSize;
        HCERTSTORE hRestrictedRoot, hRestrictedTrust, hRestrictedOther;
        DWORD cAdditionalStore;
        HCERTSTORE* rghAdditionalStore;
        DWORD dwFlags, dwUrlRetrievalTimeout, MaximumCachedCertificates, CycleDetectionModulus;
        HCERTSTORE hExclusiveRoot, hExclusiveTrustedPeople;
        DWORD dwExclusiveFlags;
    } FireTlsSchEngineConfig;
    FireTlsSchEngineConfig cfg;
    DWORD sizes[2];
    BOOL made = FALSE;
    int count = 0, attempt;
    s->roots = CertOpenStore(CERT_STORE_PROV_MEMORY, 0, 0, 0, NULL);
    if (!s->roots) return fire_net_fail(st, FIRE_NET_E_OTHER, "A certificate store could not be made.");
    if (opt->caPem[0] && !fire_tls_schAddRoots(s->roots, opt->caPem, &count, st)) return 0;
    if (opt->caFile[0]) {
        char* text = NULL;
        int r = fire_tls_schReadFile(opt->caFile, &text), ok;
        if (r < 0) return fire_tls_oom(st);
        if (r == 0) return fire_net_fail(st, FIRE_NET_E_INVALID_ARGUMENT, "The CA file '%s' could not be read.", opt->caFile);
        ok = fire_tls_schAddRoots(s->roots, text, &count, st);
        free(text);
        if (!ok) return 0;
    }
    if (count == 0) return fire_net_fail(st, FIRE_NET_E_INVALID_ARGUMENT, "The CA certificates contain no certificate.");
    memset(&cfg, 0, sizeof cfg);
    cfg.hExclusiveRoot = s->roots;   /* only these authorities are trusted */
    sizes[0] = (DWORD)sizeof cfg;
    sizes[1] = (DWORD)offsetof(FireTlsSchEngineConfig, dwExclusiveFlags);
    for (attempt = 0; attempt < 2 && !made; attempt++) {
        cfg.cbSize = sizes[attempt];
        made = CertCreateCertificateChainEngine((PCERT_CHAIN_ENGINE_CONFIG)(void*)&cfg, &s->engine);
    }
    if (!made) return fire_net_fail(st, FIRE_NET_E_OTHER, "The certificate checking could not be set up (error 0x%08lX).", (unsigned long)GetLastError());
    return 1;
}

static FireTlsSession* fire_tls_clientOpen(FireNetSock fd, const FireTlsOptions* opt, FireNetStatus* st) {
    FireTlsSession* s = fire_tls_newSession(fd);
    if (s == NULL || (s->serverName = fire_tls_schCopy(opt->serverName)) == NULL) { fire_tls_freeSession(s); fire_tls_oom(st); return NULL; }
    s->client = 1;
    s->verify = opt->verify != 0;
    if (opt->verify && (opt->caPem[0] || opt->caFile[0]) && !fire_tls_schSetupRoots(s, opt, st)) { fire_tls_freeSession(s); return NULL; }
    if (opt->certPem[0] && opt->keyPem[0] && !fire_tls_schKeyedCert(opt->certPem, opt->keyPem, &s->clientKeys, st)) { fire_tls_freeSession(s); return NULL; }
    if (!fire_tls_schCredentials(1, s->clientKeys.cert ? &s->clientKeys : NULL, &s->cred, st)) { fire_tls_freeSession(s); return NULL; }
    s->haveCred = 1;
    s->ownCred = 1;
    s->credPtr = &s->cred;
    s->first = 1;
    return s;
}

static FireTlsContext* fire_tls_serverContext(const FireTlsOptions* opt, FireNetStatus* st) {
    FireTlsContext* c = (FireTlsContext*)calloc(1, sizeof(FireTlsContext));
    if (c == NULL) { fire_tls_oom(st); return NULL; }
    if (!fire_tls_schKeyedCert(opt->certPem, opt->keyPem, &c->keys, st)) { free(c); return NULL; }
    if (!fire_tls_schCredentials(0, &c->keys, &c->cred, st)) { fire_tls_schFreeKeys(&c->keys); free(c); return NULL; }
    c->haveCred = 1;
    return c;
}

static void fire_tls_freeContext(FireTlsContext* c) {
    if (c == NULL) return;
    if (c->haveCred) FreeCredentialsHandle(&c->cred);
    fire_tls_schFreeKeys(&c->keys);
    free(c);
}

static FireTlsSession* fire_tls_serverAccept(FireTlsContext* c, FireNetSock fd, FireNetStatus* st) {
    FireTlsSession* s = fire_tls_newSession(fd);
    if (s == NULL) { fire_tls_oom(st); return NULL; }
    s->client = 0;
    s->credPtr = &c->cred;
    s->first = 1;
    s->needMore = 1;   /* a server waits for the hello */
    return s;
}

/* Sends what is waiting in `out`; *empty when it is all gone. With a timeout of 0 nothing waits (the rest stays for the next time). */
static int fire_tls_flushOut(FireTlsSession* s, int64_t timeoutMs, int* empty, FireNetStatus* st) {
    *empty = s->out.size == 0;
    while (s->out.size > 0) {
        int sent = 0;
        FireNetStatus ss;
        if (!fire_net_sendBytes(s->fd, s->out.data, (int)(s->out.size < ((size_t)1 << 20) ? s->out.size : ((size_t)1 << 20)), timeoutMs, &sent, &ss)) {
            if (ss.code == FIRE_NET_E_TIMED_OUT) return 1;
            *st = ss;
            return 0;
        }
        fire_tls_bytesDrop(&s->out, (size_t)sent);
        if (sent == 0) break;
    }
    *empty = s->out.size == 0;
    return 1;
}

/* A failed call of SChannel as a status: always 0. */
static int fire_tls_schSecFail(SECURITY_STATUS ss, const char* what, FireNetStatus* st) {
    unsigned long code = (unsigned long)ss;
    char buf[32];
    if (ss == SEC_E_UNTRUSTED_ROOT || ss == (SECURITY_STATUS)CERT_E_UNTRUSTEDROOT || ss == (SECURITY_STATUS)CERT_E_EXPIRED || ss == SEC_E_CERT_EXPIRED ||
        ss == (SECURITY_STATUS)CERT_E_CN_NO_MATCH || ss == SEC_E_WRONG_PRINCIPAL || ss == SEC_E_CERT_UNKNOWN)
        return fire_net_fail(st, FIRE_TLS_E_CERTIFICATE, "The certificate was not accepted: %s.", fire_tls_schChainErrorText(code, buf, sizeof buf));
    if (ss == SEC_E_ALGORITHM_MISMATCH) return fire_net_fail(st, FIRE_TLS_E_TLS, "%s: no common protocol or cipher (0x%08lX).", what, code);
    return fire_net_fail(st, FIRE_TLS_E_TLS, "%s: error 0x%08lX.", what, code);
}

/* Keeps the bytes that SChannel did not use ("extra") and drops the rest of the input. */
static void fire_tls_schKeepExtra(FireTlsBytes* enc, const SecBuffer* buffers, int count) {
    int i;
    for (i = 0; i < count; i++) {
        if (buffers[i].BufferType == SECBUFFER_EXTRA && buffers[i].cbBuffer > 0 && buffers[i].cbBuffer <= enc->size) {
            fire_tls_bytesDrop(enc, enc->size - buffers[i].cbBuffer);
            return;
        }
    }
    enc->size = 0;
}

/* Whether the certificate has the IP address among its alternative names. */
static int fire_tls_schHasIp(PCCERT_CONTEXT remote, const uint8_t* ip, size_t ipLength) {
    int match = 0;
    PCERT_EXTENSION ext = CertFindExtension(szOID_SUBJECT_ALT_NAME2, remote->pCertInfo->cExtension, remote->pCertInfo->rgExtension);
    if (ext) {
        CERT_ALT_NAME_INFO* names = NULL;
        DWORD size = 0, i;
        if (CryptDecodeObjectEx(X509_ASN_ENCODING, X509_ALTERNATE_NAME, ext->Value.pbData, ext->Value.cbData, CRYPT_DECODE_ALLOC_FLAG, NULL, &names, &size)) {
            for (i = 0; i < names->cAltEntry; i++) {
                const CERT_ALT_NAME_ENTRY* e = &names->rgAltEntry[i];
                if (e->dwAltNameChoice == CERT_ALT_NAME_IP_ADDRESS && e->IPAddress.cbData == ipLength && memcmp(e->IPAddress.pbData, ip, ipLength) == 0) match = 1;
            }
            LocalFree(names);
        }
    }
    return match;
}

/* The certificate of the server, checked (the chain, the dates, the purpose and the name). */
static int fire_tls_schVerifyServer(FireTlsSession* s, FireNetStatus* st) {
    PCCERT_CONTEXT remote = NULL;
    PCCERT_CHAIN_CONTEXT chain = NULL;
    CERT_CHAIN_PARA para;
    int result = 1;
    SECURITY_STATUS ss = QueryContextAttributes(&s->ctx, SECPKG_ATTR_REMOTE_CERT_CONTEXT, &remote);
    if (ss != SEC_E_OK || !remote) return fire_net_fail(st, FIRE_TLS_E_CERTIFICATE, "The server sent no certificate.");
    memset(&para, 0, sizeof para);
    para.cbSize = sizeof para;
    if (!CertGetCertificateChain(s->engine, remote, NULL, remote->hCertStore, &para, 0, NULL, &chain)) {
        result = fire_net_fail(st, FIRE_TLS_E_CERTIFICATE, "The certificate chain could not be built (error 0x%08lX).", (unsigned long)GetLastError());
    } else {
        uint8_t ip[16];
        size_t ipLength = 0;
        int haveName = s->serverName[0] != 0;
        int ipHost = haveName && fire_tls_schIsIpLiteral(s->serverName, ip, &ipLength);
        wchar_t* name = haveName ? fire_tls_schWide(s->serverName) : NULL;
        HTTPSPolicyCallbackData ssl;
        CERT_CHAIN_POLICY_PARA policy;
        CERT_CHAIN_POLICY_STATUS status;
        if (haveName && name == NULL) {
            result = fire_tls_oom(st);
        } else {
            memset(&ssl, 0, sizeof ssl);
            ssl.cbStruct = sizeof ssl;
            ssl.dwAuthType = AUTHTYPE_SERVER;
            ssl.fdwChecks = ipHost || !haveName ? SECURITY_FLAG_IGNORE_CERT_CN_INVALID : 0;   /* (an IP address is compared below) */
            ssl.pwszServerName = name;
            memset(&policy, 0, sizeof policy);
            policy.cbSize = sizeof policy;
            policy.pvExtraPolicyPara = &ssl;
            memset(&status, 0, sizeof status);
            status.cbSize = sizeof status;
            if (!CertVerifyCertificateChainPolicy(CERT_CHAIN_POLICY_SSL, chain, &policy, &status)) {
                result = fire_net_fail(st, FIRE_TLS_E_CERTIFICATE, "The certificate could not be checked (error 0x%08lX).", (unsigned long)GetLastError());
            } else if (status.dwError != 0) {
                char buf[32];
                result = fire_net_fail(st, FIRE_TLS_E_CERTIFICATE, "The certificate was not accepted: %s.", fire_tls_schChainErrorText((unsigned long)status.dwError, buf, sizeof buf));
            } else if (ipHost && !fire_tls_schHasIp(remote, ip, ipLength)) {
                result = fire_net_fail(st, FIRE_TLS_E_CERTIFICATE, "The certificate was not accepted: it is not valid for the address %s.", s->serverName);
            }
        }
        free(name);
        CertFreeCertificateChain(chain);
    }
    CertFreeCertificateContext(remote);
    return result;
}

static int fire_tls_handshakeStep(FireTlsSession* s, int* done, FireNetStatus* st) {
    *done = 0;
    for (;;) {
        int empty = 1;
        SecBuffer inBuffers[2];
        SecBufferDesc inDesc;
        SecBuffer outBuffer;
        SecBufferDesc outDesc;
        ULONG attributes = 0;
        TimeStamp expiry;
        SECURITY_STATUS ss;
        if (!fire_tls_flushOut(s, 0, &empty, st)) return 0;
        if (!empty) return 1;   /* the other side has to take what was sent first */
        if (s->handshakeDone) {
            if (s->renegotiating) { s->renegotiating = 0; *done = 1; return 1; }
            if (!s->haveSizes) {
                SECURITY_STATUS qs = QueryContextAttributes(&s->ctx, SECPKG_ATTR_STREAM_SIZES, &s->sizes);
                if (qs != SEC_E_OK) return fire_net_fail(st, FIRE_TLS_E_TLS, "TLS: the record sizes could not be asked (0x%08lX).", (unsigned long)qs);
                s->haveSizes = 1;
            }
            if (s->client && s->verify && !s->verified) {
                if (!fire_tls_schVerifyServer(s, st)) return 0;
                s->verified = 1;
            }
            *done = 1;
            return 1;
        }
        if (s->needMore || (!s->first && s->enc.size == 0)) {
            int got = 0;
            FireNetStatus rs;
            if (!fire_tls_bytesReserve(&s->enc, s->enc.size + 16384)) return fire_tls_oom(st);
            if (!fire_net_recvBytes(s->fd, s->enc.data + s->enc.size, 16384, 0, &got, &rs)) {
                if (rs.code == FIRE_NET_E_TIMED_OUT) return 1;
                *st = rs;
                return 0;
            }
            if (got == 0) return fire_net_fail(st, FIRE_NET_E_CLOSED, "TLS handshake: the connection was closed by the other side.");
            s->enc.size += (size_t)got;
            s->needMore = 0;
        }
        inBuffers[0].pvBuffer = s->enc.size == 0 ? NULL : s->enc.data;
        inBuffers[0].cbBuffer = (ULONG)s->enc.size;
        inBuffers[0].BufferType = SECBUFFER_TOKEN;
        inBuffers[1].pvBuffer = NULL;
        inBuffers[1].cbBuffer = 0;
        inBuffers[1].BufferType = SECBUFFER_EMPTY;
        inDesc.ulVersion = SECBUFFER_VERSION;
        inDesc.cBuffers = 2;
        inDesc.pBuffers = inBuffers;
        outBuffer.pvBuffer = NULL;
        outBuffer.cbBuffer = 0;
        outBuffer.BufferType = SECBUFFER_TOKEN;
        outDesc.ulVersion = SECBUFFER_VERSION;
        outDesc.cBuffers = 1;
        outDesc.pBuffers = &outBuffer;
        if (s->client) {
            ULONG flags = ISC_REQ_SEQUENCE_DETECT | ISC_REQ_REPLAY_DETECT | ISC_REQ_CONFIDENTIALITY | ISC_REQ_ALLOCATE_MEMORY | ISC_REQ_STREAM | ISC_REQ_EXTENDED_ERROR;
            int hello = s->first && !s->renegotiating;
            if (s->clientKeys.cert) flags |= ISC_REQ_USE_SUPPLIED_CREDS;
            ss = InitializeSecurityContextA(s->credPtr, s->haveCtx ? &s->ctx : NULL, s->serverName[0] ? (SEC_CHAR*)s->serverName : NULL, flags, 0, 0,
                                            hello ? NULL : &inDesc, 0, &s->ctx, &outDesc, &attributes, &expiry);
        } else {
            ULONG flags = ASC_REQ_SEQUENCE_DETECT | ASC_REQ_REPLAY_DETECT | ASC_REQ_CONFIDENTIALITY | ASC_REQ_ALLOCATE_MEMORY | ASC_REQ_STREAM | ASC_REQ_EXTENDED_ERROR;
            ss = AcceptSecurityContext(s->credPtr, s->haveCtx ? &s->ctx : NULL, &inDesc, flags, 0, &s->ctx, &outDesc, &attributes, &expiry);
        }
        if (ss != SEC_E_INCOMPLETE_MESSAGE && ss != SEC_E_INVALID_HANDLE && ss != SEC_E_INSUFFICIENT_MEMORY && ss != SEC_E_INTERNAL_ERROR) s->haveCtx = 1;
        s->first = 0;
        if (outBuffer.pvBuffer && outBuffer.cbBuffer > 0 && !fire_tls_bytesAppend(&s->out, outBuffer.pvBuffer, outBuffer.cbBuffer)) {
            FreeContextBuffer(outBuffer.pvBuffer);
            return fire_tls_oom(st);
        }
        if (outBuffer.pvBuffer) FreeContextBuffer(outBuffer.pvBuffer);
        if (ss == SEC_E_OK) {
            fire_tls_schKeepExtra(&s->enc, inBuffers, 2);
            s->handshakeDone = 1;
            continue;
        }
        if (ss == SEC_I_CONTINUE_NEEDED) {
            fire_tls_schKeepExtra(&s->enc, inBuffers, 2);
            s->needMore = s->enc.size == 0;
            continue;
        }
        if (ss == SEC_E_INCOMPLETE_MESSAGE) { s->needMore = 1; continue; }
        if (ss == SEC_I_INCOMPLETE_CREDENTIALS) {
            /* the server asks for a certificate: with one it was given, without we go on with none */
            if (++s->credRetries > 2) return fire_net_fail(st, FIRE_TLS_E_TLS, "TLS handshake: the server wants a client certificate.");
            continue;
        }
        {
            /* an alert may be waiting in `out`: send it, the error is what counts */
            int ignored;
            FireNetStatus ignoredSt;
            fire_tls_flushOut(s, 0, &ignored, &ignoredSt);
            return fire_tls_schSecFail(ss, s->client ? "TLS handshake" : "TLS accept", st);
        }
    }
}

/* Waits until the socket can be read (or written); 0 when the time is up (status TimedOut) or the wait failed. */
static int fire_tls_wait(FireTlsSession* s, int read, int64_t deadline, FireNetStatus* st) {
    int64_t left = deadline < 0 ? -1 : deadline - fire_net_nowMillis();
    int r, w;
    if (deadline >= 0 && left < 0) left = 0;
    if (!fire_net_waitFor(s->fd, read, !read, left, &r, &w, st)) return 0;
    if (!r && !w) return fire_net_fail(st, FIRE_NET_E_TIMED_OUT, "The TLS operation timed out.");
    return 1;
}

static int fire_tls_readSome(FireTlsSession* s, uint8_t* data, int count, int64_t timeoutMs, int* got, FireNetStatus* st) {
    int64_t deadline;
    *got = 0;
    if (count <= 0) return 1;
    deadline = timeoutMs >= 0 ? fire_net_nowMillis() + timeoutMs : -1;
    for (;;) {
        int n = 0;
        int64_t left;
        if (s->plainPos < s->plain.size) {
            size_t take = s->plain.size - s->plainPos;
            if (take > (size_t)count) take = (size_t)count;
            memcpy(data, s->plain.data + s->plainPos, take);
            s->plainPos += take;
            if (s->plainPos == s->plain.size) { s->plain.size = 0; s->plainPos = 0; }
            *got = (int)take;
            return 1;
        }
        if (s->eof) return 1;
        if (s->renegotiating || !s->handshakeDone) {
            int done = 0;
            if (!fire_tls_handshakeStep(s, &done, st)) return 0;
            if (!done && !fire_tls_wait(s, s->out.size == 0, deadline, st)) return 0;
            continue;
        }
        if (s->enc.size > 0 && !s->needMoreEnc) {
            SecBuffer b[4];
            SecBufferDesc desc;
            SECURITY_STATUS ss;
            int i;
            b[0].pvBuffer = s->enc.data;
            b[0].cbBuffer = (ULONG)s->enc.size;
            b[0].BufferType = SECBUFFER_DATA;
            for (i = 1; i < 4; i++) { b[i].pvBuffer = NULL; b[i].cbBuffer = 0; b[i].BufferType = SECBUFFER_EMPTY; }
            desc.ulVersion = SECBUFFER_VERSION;
            desc.cBuffers = 4;
            desc.pBuffers = b;
            ss = DecryptMessage(&s->ctx, &desc, 0, NULL);
            if (ss == SEC_E_OK) {
                for (i = 1; i < 4; i++) {
                    if (b[i].BufferType == SECBUFFER_DATA && b[i].cbBuffer > 0) {
                        s->plain.size = 0;
                        if (!fire_tls_bytesAppend(&s->plain, b[i].pvBuffer, b[i].cbBuffer)) return fire_tls_oom(st);
                    }
                }
                s->plainPos = 0;
                fire_tls_schKeepExtra(&s->enc, b, 4);
                continue;
            }
            if (ss == SEC_E_INCOMPLETE_MESSAGE) { s->needMoreEnc = 1; continue; }
            if (ss == SEC_I_CONTEXT_EXPIRED) { s->eof = 1; s->enc.size = 0; return 1; }
            if (ss == SEC_I_RENEGOTIATE) {
                fire_tls_schKeepExtra(&s->enc, b, 4);   /* the rest is the handshake message that follows */
                s->renegotiating = 1;
                s->handshakeDone = 0;
                s->needMore = s->enc.size == 0;
                continue;
            }
            return fire_tls_schSecFail(ss, "TLS read", st);
        }
        left = deadline < 0 ? -1 : deadline - fire_net_nowMillis();
        if (deadline >= 0 && left < 0) left = 0;
        if (!fire_tls_bytesReserve(&s->enc, s->enc.size + 16384)) return fire_tls_oom(st);
        if (!fire_net_recvBytes(s->fd, s->enc.data + s->enc.size, 16384, left, &n, st)) return 0;
        if (n == 0) {
            if (s->enc.size > 0) return fire_net_fail(st, FIRE_NET_E_CLOSED, "TLS read: the connection ended in the middle of a record.");
            s->eof = 1;   /* closed without close_notify: the data ends (as with the OpenSSL backend) */
            return 1;
        }
        s->enc.size += (size_t)n;
        s->needMoreEnc = 0;
    }
}

static int fire_tls_writeSome(FireTlsSession* s, const uint8_t* data, int count, int64_t timeoutMs, int* sent, FireNetStatus* st) {
    int64_t deadline, left;
    int empty = 1;
    size_t chunk;
    SecBuffer b[4];
    SecBufferDesc desc;
    SECURITY_STATUS ss;
    *sent = 0;
    if (count <= 0) return 1;
    if (!s->handshakeDone || s->renegotiating) return fire_net_fail(st, FIRE_TLS_E_TLS, "TLS write: the handshake is not complete.");
    deadline = timeoutMs >= 0 ? fire_net_nowMillis() + timeoutMs : -1;
    /* what an earlier call could not send goes first */
    for (;;) {
        left = deadline < 0 ? -1 : deadline - fire_net_nowMillis();
        if (deadline >= 0 && left < 0) left = 0;
        if (!fire_tls_flushOut(s, left, &empty, st)) return 0;
        if (empty) break;
        if (deadline >= 0 && fire_net_nowMillis() >= deadline) return fire_net_fail(st, FIRE_NET_E_TIMED_OUT, "Sending timed out.");
    }
    chunk = (size_t)count < (size_t)s->sizes.cbMaximumMessage ? (size_t)count : (size_t)s->sizes.cbMaximumMessage;
    /* (the record is made in `out`, which is empty now) */
    if (!fire_tls_bytesReserve(&s->out, (size_t)s->sizes.cbHeader + chunk + (size_t)s->sizes.cbTrailer)) return fire_tls_oom(st);
    memcpy(s->out.data + s->sizes.cbHeader, data, chunk);
    b[0].pvBuffer = s->out.data;
    b[0].cbBuffer = s->sizes.cbHeader;
    b[0].BufferType = SECBUFFER_STREAM_HEADER;
    b[1].pvBuffer = s->out.data + s->sizes.cbHeader;
    b[1].cbBuffer = (ULONG)chunk;
    b[1].BufferType = SECBUFFER_DATA;
    b[2].pvBuffer = s->out.data + s->sizes.cbHeader + chunk;
    b[2].cbBuffer = s->sizes.cbTrailer;
    b[2].BufferType = SECBUFFER_STREAM_TRAILER;
    b[3].pvBuffer = NULL;
    b[3].cbBuffer = 0;
    b[3].BufferType = SECBUFFER_EMPTY;
    desc.ulVersion = SECBUFFER_VERSION;
    desc.cBuffers = 4;
    desc.pBuffers = b;
    ss = EncryptMessage(&s->ctx, 0, &desc, 0);
    if (ss != SEC_E_OK) return fire_tls_schSecFail(ss, "TLS write", st);
    s->out.size = (size_t)b[0].cbBuffer + b[1].cbBuffer + b[2].cbBuffer;
    /* the record is ours now (the data is taken); what the socket does not take within the time goes out with the next call or when the session closes */
    left = deadline < 0 ? -1 : deadline - fire_net_nowMillis();
    if (deadline >= 0 && left < 0) left = 0;
    if (!fire_tls_flushOut(s, left, &empty, st)) return 0;
    *sent = (int)chunk;
    return 1;
}

static int fire_tls_pending(FireTlsSession* s) { return (int)(s->plain.size - s->plainPos); }

static void fire_tls_info(FireTlsSession* s, char* protocol, size_t psize, char* cipher, size_t csize) {
    SecPkgContext_ConnectionInfo ci;
    DWORD p;
    const char* version;
    if (psize > 0) protocol[0] = 0;
    if (csize > 0) cipher[0] = 0;
    if (!s->haveCtx) return;
    memset(&ci, 0, sizeof ci);
    if (QueryContextAttributes(&s->ctx, SECPKG_ATTR_CONNECTION_INFO, &ci) != SEC_E_OK) return;
    p = ci.dwProtocol;
    if (p & (SP_PROT_TLS1_3_CLIENT | SP_PROT_TLS1_3_SERVER)) version = "TLSv1.3";
    else if (p & (SP_PROT_TLS1_2_CLIENT | SP_PROT_TLS1_2_SERVER)) version = "TLSv1.2";
    else if (p & 0x00000300) version = "TLSv1.1";
    else if (p & 0x000000C0) version = "TLSv1.0";
    else version = "TLS";
    snprintf(protocol, psize, "%s", version);
    switch (ci.aiCipher) {
        case CALG_AES_128: snprintf(cipher, csize, "%s", "AES-128"); break;
        case CALG_AES_192: snprintf(cipher, csize, "%s", "AES-192"); break;
        case CALG_AES_256: snprintf(cipher, csize, "%s", "AES-256"); break;
        case CALG_3DES: snprintf(cipher, csize, "%s", "3DES"); break;
        case CALG_RC4: snprintf(cipher, csize, "%s", "RC4"); break;
        default: snprintf(cipher, csize, "cipher 0x%08lX", (unsigned long)ci.aiCipher); break;
    }
#if defined(SECPKG_ATTR_CIPHER_INFO)
    {
        SecPkgContext_CipherInfo suite;
        memset(&suite, 0, sizeof suite);
        suite.dwVersion = SECPKGCONTEXT_CIPHERINFO_V1;
        if (QueryContextAttributes(&s->ctx, SECPKG_ATTR_CIPHER_INFO, &suite) == SEC_E_OK && suite.szCipherSuite[0]) {
            char narrow[128] = {0};
            WideCharToMultiByte(CP_UTF8, 0, suite.szCipherSuite, -1, narrow, (int)sizeof narrow - 1, NULL, NULL);
            snprintf(cipher, csize, "%s", narrow);   /* the name of the suite: TLS_AES_256_GCM_SHA384 */
        }
    }
#endif
}

static void fire_tls_closeSession(FireTlsSession* s) {
    if (s == NULL) return;
    if (s->haveCtx && s->handshakeDone && !s->eof) {
        /* close_notify, best effort (the socket does not wait) */
        DWORD shutdown = SCHANNEL_SHUTDOWN;
        SecBuffer token;
        SecBufferDesc tokenDesc;
        token.pvBuffer = &shutdown;
        token.cbBuffer = sizeof shutdown;
        token.BufferType = SECBUFFER_TOKEN;
        tokenDesc.ulVersion = SECBUFFER_VERSION;
        tokenDesc.cBuffers = 1;
        tokenDesc.pBuffers = &token;
        if (ApplyControlToken(&s->ctx, &tokenDesc) == SEC_E_OK) {
            SecBuffer outBuffer;
            SecBufferDesc outDesc;
            ULONG attributes = 0;
            TimeStamp expiry;
            SECURITY_STATUS ss;
            outBuffer.pvBuffer = NULL;
            outBuffer.cbBuffer = 0;
            outBuffer.BufferType = SECBUFFER_TOKEN;
            outDesc.ulVersion = SECBUFFER_VERSION;
            outDesc.cBuffers = 1;
            outDesc.pBuffers = &outBuffer;
            if (s->client)
                ss = InitializeSecurityContextA(s->credPtr, &s->ctx, NULL, ISC_REQ_SEQUENCE_DETECT | ISC_REQ_REPLAY_DETECT | ISC_REQ_CONFIDENTIALITY | ISC_REQ_ALLOCATE_MEMORY | ISC_REQ_STREAM, 0, 0,
                                                NULL, 0, &s->ctx, &outDesc, &attributes, &expiry);
            else
                ss = AcceptSecurityContext(s->credPtr, &s->ctx, NULL, ASC_REQ_SEQUENCE_DETECT | ASC_REQ_REPLAY_DETECT | ASC_REQ_CONFIDENTIALITY | ASC_REQ_ALLOCATE_MEMORY | ASC_REQ_STREAM, 0,
                                           &s->ctx, &outDesc, &attributes, &expiry);
            if ((ss == SEC_E_OK || ss == SEC_I_CONTEXT_EXPIRED) && outBuffer.pvBuffer && outBuffer.cbBuffer > 0 && fire_tls_bytesAppend(&s->out, outBuffer.pvBuffer, outBuffer.cbBuffer)) {
                int ignored;
                FireNetStatus ignoredSt;
                fire_tls_flushOut(s, 0, &ignored, &ignoredSt);
            }
            if (outBuffer.pvBuffer) FreeContextBuffer(outBuffer.pvBuffer);
        }
    }
    fire_tls_freeSession(s);
}

#endif
