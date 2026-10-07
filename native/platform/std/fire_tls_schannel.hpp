// fire native platform layer, TLS part on SChannel: the TLS of Windows (SSPI, secur32.dll). Nothing has to be installed, the certificates are those of the Windows certificate store (the same trust as
// the browsers of the machine and .NET). It is the default of the `windows` platform (FIRE_TLS_OPENSSL selects OpenSSL there, see fire_tls_desktop.hpp). The interface is the one of fire_tls_openssl.hpp.
//
// How it works:
//   - The handshake is done with InitializeSecurityContext/AcceptSecurityContext in steps: `handshakeStep` sends what is waiting, reads what is there (the socket is non-blocking), feeds it to SChannel and
//     returns when it has to wait for the other side - the same model as the OpenSSL backend.
//   - Records are made by EncryptMessage/DecryptMessage on buffers of this file; an incomplete record waits in the session until the rest has come.
//   - The certificate of a server is checked by this file after the handshake (SChannel is told not to): the chain is built with CertGetCertificateChain (the system's roots, or - when CA certificates
//     are given - only those) and judged by CertVerifyCertificateChainPolicy (CERT_CHAIN_POLICY_SSL: dates, chain, purpose and the host name). An IP address as host name is compared with the IP
//     entries of the certificate here (the policy does not do that). No revocation checks (as with the OpenSSL backend).
//   - A server certificate with its private key comes as PEM text: the certificates are put in a memory store (the chain the clients get), an RSA key is imported into a key container of the CryptoAPI
//     (named for this process, deleted when the context is freed), an ECDSA key (P-256, P-384, P-521) into a CNG key. PEM keys of the kinds "PRIVATE KEY" (PKCS#8), "RSA PRIVATE KEY" and "EC PRIVATE KEY"
//     (unencrypted) are understood. The same works for the certificate a client shows.
//   - TLS 1.2 and 1.3 (1.3 where the Windows version has it: from Windows 11 and Server 2022; elsewhere SChannel agrees on 1.2).
// Link libraries: ws2_32 secur32 crypt32 ncrypt (the package brings them).
#pragma once

#ifndef SECURITY_WIN32
#define SECURITY_WIN32
#endif
#include <windows.h>
#include <wincrypt.h>
#include <ncrypt.h>
#include <security.h>
#include <schnlsp.h>
#include <sspi.h>

#include <algorithm>
#include <cstddef>
#include <cstring>
#include <string>
#include <vector>

#include FIRE_PLATFORM_NET_HEADER

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

/// A certificate (with the chain behind it) and its private key: where the key lives depends on its kind.
struct KeyedCert {
    PCCERT_CONTEXT cert = nullptr;
    HCERTSTORE store = nullptr;
    HCRYPTPROV prov = 0;                    // an RSA key: the container of the CryptoAPI (named, deleted when freed)
    std::wstring container;
    NCRYPT_KEY_HANDLE nkey = 0;             // an ECDSA key: the CNG key
};

struct Context {
    CredHandle cred;
    bool haveCred = false;
    KeyedCert keys;
};

struct Session {
    Sock fd = net::kInvalid;
    bool client = true;
    bool verify = true;
    std::string serverName;
    CredHandle cred;
    bool haveCred = false;
    bool ownCred = false;
    const CredHandle* credPtr = nullptr;      // the credentials in use (own, or those of the server context)
    CtxtHandle ctx;
    bool haveCtx = false;
    bool first = true;                         // the first call of the handshake (a client sends the hello without input)
    bool handshakeDone = false;
    bool needMore = false;                     // the handshake waits for bytes of the other side
    bool needMoreEnc = false;                  // the buffered records are not complete
    bool renegotiating = false;                // a handshake message after the handshake (TLS 1.3) is being handled
    int credRetries = 0;
    bool eof = false;
    bool verified = false;
    std::vector<uint8_t> enc;                  // received, not yet decrypted
    std::vector<uint8_t> out;                  // encrypted, not yet sent
    std::vector<uint8_t> plain;                // decrypted, not yet given out
    size_t plainPos = 0;
    SecPkgContext_StreamSizes sizes;
    bool haveSizes = false;
    HCERTSTORE roots = nullptr;                // the CA certificates that were given (client)
    HCERTCHAINENGINE engine = nullptr;
    KeyedCert clientKeys;
};

inline bool supported() { return true; }

// ---- small helpers ----------------------------------------------------------------------------------------------------------------------
inline std::string hex32(unsigned long v) {
    char t[16];
    std::snprintf(t, sizeof t, "0x%08lX", v);
    return t;
}
inline std::wstring wide(const std::string& s) {
    if (s.empty()) return std::wstring();
    int n = MultiByteToWideChar(CP_UTF8, 0, s.data(), (int)s.size(), nullptr, 0);
    std::wstring w((size_t)n, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.data(), (int)s.size(), &w[0], n);
    return w;
}
inline bool isIpLiteral(const std::string& host, std::vector<uint8_t>& bytes) {
    unsigned char buf[16];
    if (inet_pton(AF_INET, host.c_str(), buf) == 1) { bytes.assign(buf, buf + 4); return true; }
    if (inet_pton(AF_INET6, host.c_str(), buf) == 1) { bytes.assign(buf, buf + 16); return true; }
    return false;
}
inline std::string readFile(const std::string& path, bool& ok) {
    ok = false;
    HANDLE h = CreateFileW(wide(path).c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return std::string();
    std::string text;
    char buf[8192];
    DWORD got = 0;
    while (ReadFile(h, buf, sizeof buf, &got, nullptr) && got > 0) text.append(buf, got);
    CloseHandle(h);
    ok = true;
    return text;
}

/// The certificate-chain error of the policy as text.
inline std::string chainErrorText(unsigned long e) {
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
        default: return "error " + hex32(e);
    }
}

// ---- PEM and DER -------------------------------------------------------------------------------------------------------------------------
struct PemBlock {
    std::string label;
    std::vector<uint8_t> der;
};
inline std::vector<PemBlock> pemBlocks(const std::string& text) {
    std::vector<PemBlock> blocks;
    size_t pos = 0;
    for (;;) {
        size_t b = text.find("-----BEGIN ", pos);
        if (b == std::string::npos) break;
        size_t le = text.find("-----", b + 11);
        if (le == std::string::npos) break;
        std::string label = text.substr(b + 11, le - (b + 11));
        size_t bodyStart = le + 5;
        std::string endMark = "-----END " + label + "-----";
        size_t e = text.find(endMark, bodyStart);
        if (e == std::string::npos) break;
        std::string body = text.substr(bodyStart, e - bodyStart);
        DWORD size = 0;
        if (CryptStringToBinaryA(body.c_str(), (DWORD)body.size(), CRYPT_STRING_BASE64, nullptr, &size, nullptr, nullptr) && size > 0) {
            PemBlock block;
            block.label = label;
            block.der.resize(size);
            if (CryptStringToBinaryA(body.c_str(), (DWORD)body.size(), CRYPT_STRING_BASE64, block.der.data(), &size, nullptr, nullptr)) {
                block.der.resize(size);
                blocks.push_back(std::move(block));
            }
        }
        pos = e + endMark.size();
    }
    return blocks;
}

/// One DER element: its tag, its content and where the next one starts.
struct Der {
    uint8_t tag = 0;
    const uint8_t* value = nullptr;
    size_t length = 0;
    const uint8_t* next = nullptr;
};
inline bool derRead(const uint8_t* p, const uint8_t* end, Der& d) {
    if (end - p < 2) return false;
    d.tag = p[0];
    size_t len = p[1];
    const uint8_t* v = p + 2;
    if (len & 0x80) {
        size_t n = len & 0x7F;
        if (n == 0 || n > 4 || (size_t)(end - v) < n) return false;
        len = 0;
        for (size_t i = 0; i < n; i++) len = (len << 8) | v[i];
        v += n;
    }
    if ((size_t)(end - v) < len) return false;
    d.value = v;
    d.length = len;
    d.next = v + len;
    return true;
}
inline bool oidIs(const Der& d, const uint8_t* bytes, size_t n) { return d.tag == 0x06 && d.length == n && std::memcmp(d.value, bytes, n) == 0; }
static const uint8_t kOidRsa[] = {0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x01, 0x01};
static const uint8_t kOidEc[] = {0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x02, 0x01};
static const uint8_t kOidP256[] = {0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x03, 0x01, 0x07};
static const uint8_t kOidP384[] = {0x2B, 0x81, 0x04, 0x00, 0x22};
static const uint8_t kOidP521[] = {0x2B, 0x81, 0x04, 0x00, 0x23};

// ---- certificates with keys ---------------------------------------------------------------------------------------------------------------
inline void freeKeyedCert(KeyedCert& k) {
    if (k.cert) CertFreeCertificateContext(k.cert);
    if (k.store) CertCloseStore(k.store, 0);
    if (k.prov) CryptReleaseContext(k.prov, 0);
    if (!k.container.empty()) {
        HCRYPTPROV p = 0;
        CryptAcquireContextW(&p, k.container.c_str(), MS_ENH_RSA_AES_PROV_W, PROV_RSA_AES, CRYPT_DELETEKEYSET);   // (deletes the container of the imported key)
    }
    if (k.nkey) NCryptFreeObject(k.nkey);
    k = KeyedCert();
}

inline Status importRsaKey(KeyedCert& k, const uint8_t* pkcs1, size_t length) {
    BYTE* blob = nullptr;
    DWORD blobSize = 0;
    if (!CryptDecodeObjectEx(X509_ASN_ENCODING | PKCS_7_ASN_ENCODING, PKCS_RSA_PRIVATE_KEY, pkcs1, (DWORD)length, CRYPT_DECODE_ALLOC_FLAG, nullptr, &blob, &blobSize))
        return fail(net::E_InvalidArgument, "The RSA private key could not be read (error " + hex32(GetLastError()) + ").");
    static LONG counter = 0;
    wchar_t name[96];
    swprintf(name, 96, L"fire-tls-%lu-%lu-%ld", (unsigned long)GetCurrentProcessId(), (unsigned long)GetTickCount(), (long)InterlockedIncrement(&counter));
    HCRYPTPROV prov = 0;
    if (!CryptAcquireContextW(&prov, name, MS_ENH_RSA_AES_PROV_W, PROV_RSA_AES, CRYPT_NEWKEYSET)) {
        LocalFree(blob);
        return fail(net::E_Other, "A key container could not be made (error " + hex32(GetLastError()) + ").");
    }
    HCRYPTKEY key = 0;
    BOOL imported = CryptImportKey(prov, blob, blobSize, 0, 0, &key);
    DWORD importError = GetLastError();
    LocalFree(blob);
    if (!imported) {
        CryptReleaseContext(prov, 0);
        HCRYPTPROV p = 0;
        CryptAcquireContextW(&p, name, MS_ENH_RSA_AES_PROV_W, PROV_RSA_AES, CRYPT_DELETEKEYSET);
        return fail(net::E_InvalidArgument, "The RSA private key could not be imported (error " + hex32(importError) + ").");
    }
    CryptDestroyKey(key);
    k.prov = prov;
    k.container = name;
    CRYPT_KEY_PROV_INFO info;
    std::memset(&info, 0, sizeof info);
    info.pwszContainerName = const_cast<LPWSTR>(k.container.c_str());
    info.pwszProvName = const_cast<LPWSTR>(MS_ENH_RSA_AES_PROV_W);
    info.dwProvType = PROV_RSA_AES;
    info.dwKeySpec = AT_KEYEXCHANGE;
    if (!CertSetCertificateContextProperty(k.cert, CERT_KEY_PROV_INFO_PROP_ID, 0, &info))
        return fail(net::E_Other, "The key could not be connected to the certificate (error " + hex32(GetLastError()) + ").");
    return success();
}

inline Status importEcKey(KeyedCert& k, const uint8_t* sec1, size_t length, const uint8_t* oidFromHeader, size_t oidLength) {
    // ECPrivateKey ::= SEQUENCE { INTEGER 1, OCTET STRING privateKey, [0] OID curve OPTIONAL, [1] BIT STRING publicKey OPTIONAL }
    Der seq;
    if (!derRead(sec1, sec1 + length, seq) || seq.tag != 0x30) return fail(net::E_InvalidArgument, "The EC private key could not be read.");
    const uint8_t* p = seq.value;
    const uint8_t* end = seq.value + seq.length;
    Der version, priv;
    if (!derRead(p, end, version) || version.tag != 0x02 || !derRead(version.next, end, priv) || priv.tag != 0x04) return fail(net::E_InvalidArgument, "The EC private key could not be read.");
    std::vector<uint8_t> curve(oidFromHeader, oidFromHeader + oidLength);
    std::vector<uint8_t> pub;
    p = priv.next;
    while (p < end) {
        Der item;
        if (!derRead(p, end, item)) break;
        if (item.tag == 0xA0) {
            Der oid;
            if (derRead(item.value, item.value + item.length, oid) && oid.tag == 0x06) curve.assign(oid.value, oid.value + oid.length);
        } else if (item.tag == 0xA1) {
            Der bits;
            if (derRead(item.value, item.value + item.length, bits) && bits.tag == 0x03 && bits.length > 1) pub.assign(bits.value + 1, bits.value + bits.length);
        }
        p = item.next;
    }
    ULONG magic = 0, size = 0;
    if (curve.size() == sizeof kOidP256 && std::memcmp(curve.data(), kOidP256, curve.size()) == 0) { magic = 0x32534345; size = 32; }          // ECS2
    else if (curve.size() == sizeof kOidP384 && std::memcmp(curve.data(), kOidP384, curve.size()) == 0) { magic = 0x34534345; size = 48; }   // ECS4
    else if (curve.size() == sizeof kOidP521 && std::memcmp(curve.data(), kOidP521, curve.size()) == 0) { magic = 0x36534345; size = 66; }   // ECS6
    else return fail(net::E_InvalidArgument, "The curve of the EC private key is not supported (P-256, P-384 and P-521 are).");
    if (pub.size() != 1 + 2 * (size_t)size || pub[0] != 0x04) return fail(net::E_InvalidArgument, "The EC private key has no public key (an uncompressed point is needed).");
    if (priv.length > size) return fail(net::E_InvalidArgument, "The EC private key has the wrong size.");
    std::vector<uint8_t> blob(8 + 3 * (size_t)size, 0);
    std::memcpy(blob.data(), &magic, 4);
    std::memcpy(blob.data() + 4, &size, 4);
    std::memcpy(blob.data() + 8, pub.data() + 1, 2 * (size_t)size);
    std::memcpy(blob.data() + 8 + 2 * (size_t)size + (size - priv.length), priv.value, priv.length);
    NCRYPT_PROV_HANDLE provider = 0;
    SECURITY_STATUS ss = NCryptOpenStorageProvider(&provider, MS_KEY_STORAGE_PROVIDER, 0);
    if (ss != ERROR_SUCCESS) return fail(net::E_Other, "The key storage of Windows could not be opened (error " + hex32((unsigned long)ss) + ").");
    NCRYPT_KEY_HANDLE key = 0;
    ss = NCryptImportKey(provider, 0, BCRYPT_ECCPRIVATE_BLOB, nullptr, &key, blob.data(), (DWORD)blob.size(), 0);
    NCryptFreeObject(provider);
    if (ss != ERROR_SUCCESS) return fail(net::E_InvalidArgument, "The EC private key could not be imported (error " + hex32((unsigned long)ss) + ").");
    k.nkey = key;
    if (!CertSetCertificateContextProperty(k.cert, CERT_NCRYPT_KEY_HANDLE_PROP_ID, CERT_SET_PROPERTY_INHIBIT_PERSIST_FLAG, &key))
        return fail(net::E_Other, "The key could not be connected to the certificate (error " + hex32(GetLastError()) + ").");
    return success();
}

/// Makes a certificate context (its store holds the whole chain of the PEM text) and connects the private key to it.
inline Status makeKeyedCert(const std::string& certPem, const std::string& keyPem, KeyedCert& out) {
    out = KeyedCert();
    std::vector<PemBlock> certs = pemBlocks(certPem);
    std::vector<const PemBlock*> chain;
    for (const PemBlock& b : certs) if (b.label == "CERTIFICATE" || b.label == "TRUSTED CERTIFICATE" || b.label == "X509 CERTIFICATE") chain.push_back(&b);
    if (chain.empty()) return fail(net::E_InvalidArgument, "The certificate (PEM) contains no certificate.");
    out.store = CertOpenStore(CERT_STORE_PROV_MEMORY, 0, 0, 0, nullptr);
    if (!out.store) return fail(net::E_Other, "A certificate store could not be made.");
    for (size_t i = 0; i < chain.size(); i++) {
        PCCERT_CONTEXT added = nullptr;
        if (!CertAddEncodedCertificateToStore(out.store, X509_ASN_ENCODING, chain[i]->der.data(), (DWORD)chain[i]->der.size(), CERT_STORE_ADD_ALWAYS, i == 0 ? &added : nullptr)) {
            freeKeyedCert(out);
            return fail(net::E_InvalidArgument, "A certificate (PEM) could not be read.");
        }
        if (i == 0) out.cert = added;
    }
    std::vector<PemBlock> keys = pemBlocks(keyPem);
    const PemBlock* keyBlock = nullptr;
    for (const PemBlock& b : keys) if (b.label == "PRIVATE KEY" || b.label == "RSA PRIVATE KEY" || b.label == "EC PRIVATE KEY") { keyBlock = &b; break; }
    if (!keyBlock) {
        freeKeyedCert(out);
        return fail(net::E_InvalidArgument, "The private key (PEM) is missing or encrypted (supported: PRIVATE KEY, RSA PRIVATE KEY and EC PRIVATE KEY without a password).");
    }
    Status st;
    const std::vector<uint8_t>& der = keyBlock->der;
    if (keyBlock->label == "RSA PRIVATE KEY") st = importRsaKey(out, der.data(), der.size());
    else if (keyBlock->label == "EC PRIVATE KEY") st = importEcKey(out, der.data(), der.size(), nullptr, 0);
    else {
        // PrivateKeyInfo ::= SEQUENCE { INTEGER 0, SEQUENCE { OID algorithm, parameters }, OCTET STRING key }
        Der seq, version, alg, key;
        bool ok = derRead(der.data(), der.data() + der.size(), seq) && seq.tag == 0x30 && derRead(seq.value, seq.value + seq.length, version) && version.tag == 0x02 &&
                  derRead(version.next, seq.value + seq.length, alg) && alg.tag == 0x30 && derRead(alg.next, seq.value + seq.length, key) && key.tag == 0x04;
        Der oid, params;
        ok = ok && derRead(alg.value, alg.value + alg.length, oid) && oid.tag == 0x06;
        if (!ok) st = fail(net::E_InvalidArgument, "The private key (PKCS#8) could not be read.");
        else if (oidIs(oid, kOidRsa, sizeof kOidRsa)) st = importRsaKey(out, key.value, key.length);
        else if (oidIs(oid, kOidEc, sizeof kOidEc)) {
            const uint8_t* curve = nullptr;
            size_t curveLength = 0;
            if (derRead(oid.next, alg.value + alg.length, params) && params.tag == 0x06) { curve = params.value; curveLength = params.length; }
            st = importEcKey(out, key.value, key.length, curve, curveLength);
        } else st = fail(net::E_InvalidArgument, "The kind of the private key is not supported (RSA and EC are).");
    }
    if (!st.ok()) freeKeyedCert(out);
    return st;
}

// ---- credentials ---------------------------------------------------------------------------------------------------------------------------
inline Status makeCredentials(bool client, const KeyedCert* keys, CredHandle& cred) {
    SCHANNEL_CRED sc;
    std::memset(&sc, 0, sizeof sc);
    sc.dwVersion = SCHANNEL_CRED_VERSION;
    PCCERT_CONTEXT certs[1];
    if (keys && keys->cert) {
        certs[0] = keys->cert;
        sc.cCreds = 1;
        sc.paCred = certs;
    }
    // the certificate of the server is checked by this file, not by SChannel (it has to know the CA certificates that the program gives)
    sc.dwFlags = SCH_USE_STRONG_CRYPTO | (client ? (SCH_CRED_NO_DEFAULT_CREDS | SCH_CRED_MANUAL_CRED_VALIDATION | SCH_CRED_NO_SERVERNAME_CHECK) : SCH_CRED_NO_SYSTEM_MAPPER);
    if (keys && keys->cert && client) sc.dwFlags &= ~(DWORD)SCH_CRED_NO_DEFAULT_CREDS;
    TimeStamp expiry;
    DWORD protocols[2] = {client ? (DWORD)(SP_PROT_TLS1_2_CLIENT | SP_PROT_TLS1_3_CLIENT) : (DWORD)(SP_PROT_TLS1_2_SERVER | SP_PROT_TLS1_3_SERVER), client ? (DWORD)SP_PROT_TLS1_2_CLIENT : (DWORD)SP_PROT_TLS1_2_SERVER};
    SECURITY_STATUS ss = SEC_E_INTERNAL_ERROR;
    for (int attempt = 0; attempt < 2; attempt++) {   // (a Windows without TLS 1.3 may not like the flag: then TLS 1.2 alone)
        sc.grbitEnabledProtocols = protocols[attempt];
        ss = AcquireCredentialsHandleA(nullptr, const_cast<SEC_CHAR*>(UNISP_NAME_A), client ? SECPKG_CRED_OUTBOUND : SECPKG_CRED_INBOUND, nullptr, &sc, nullptr, nullptr, &cred, &expiry);
        if (ss == SEC_E_OK) return success();
    }
    return fail(E_Tls, std::string("TLS could not be started (AcquireCredentialsHandle ") + hex32((unsigned long)ss) + ").");
}

// ---- sessions ------------------------------------------------------------------------------------------------------------------------------
inline void freeSession(Session* s) {
    if (!s) return;
    if (s->haveCtx) DeleteSecurityContext(&s->ctx);
    if (s->haveCred && s->ownCred) FreeCredentialsHandle(&s->cred);
    if (s->engine) CertFreeCertificateChainEngine(s->engine);
    if (s->roots) CertCloseStore(s->roots, 0);
    freeKeyedCert(s->clientKeys);
    delete s;
}

inline Status addRoots(HCERTSTORE store, const std::string& pem, int& count) {
    for (const PemBlock& b : pemBlocks(pem)) {
        if (b.label != "CERTIFICATE" && b.label != "TRUSTED CERTIFICATE" && b.label != "X509 CERTIFICATE") continue;
        if (!CertAddEncodedCertificateToStore(store, X509_ASN_ENCODING, b.der.data(), (DWORD)b.der.size(), CERT_STORE_ADD_ALWAYS, nullptr))
            return fail(net::E_InvalidArgument, "A CA certificate could not be read (error " + hex32(GetLastError()) + ").");
        count++;
    }
    return success();
}

inline Status clientOpen(Sock fd, const Options& opt, Session*& out) {
    out = nullptr;
    Session* s = new Session();
    std::memset(&s->cred, 0, sizeof s->cred);
    std::memset(&s->ctx, 0, sizeof s->ctx);
    std::memset(&s->sizes, 0, sizeof s->sizes);
    s->fd = fd;
    s->client = true;
    s->verify = opt.verify;
    s->serverName = opt.serverName;
    if (opt.verify && (!opt.caPem.empty() || !opt.caFile.empty())) {
        s->roots = CertOpenStore(CERT_STORE_PROV_MEMORY, 0, 0, 0, nullptr);
        if (!s->roots) { freeSession(s); return fail(net::E_Other, "A certificate store could not be made."); }
        int count = 0;
        Status st = success();
        if (!opt.caPem.empty()) st = addRoots(s->roots, opt.caPem, count);
        if (st.ok() && !opt.caFile.empty()) {
            bool ok;
            std::string text = readFile(opt.caFile, ok);
            if (!ok) st = fail(net::E_InvalidArgument, "The CA file '" + opt.caFile + "' could not be read.");
            else st = addRoots(s->roots, text, count);
        }
        if (st.ok() && count == 0) st = fail(net::E_InvalidArgument, "The CA certificates contain no certificate.");
        if (!st.ok()) { freeSession(s); return st; }
        // (the structure with all of its fields, whatever the headers know: Windows 8 and newer have the last one, Windows 7 does not take it into account)
        struct EngineConfig {
            DWORD cbSize;
            HCERTSTORE hRestrictedRoot, hRestrictedTrust, hRestrictedOther;
            DWORD cAdditionalStore;
            HCERTSTORE* rghAdditionalStore;
            DWORD dwFlags, dwUrlRetrievalTimeout, MaximumCachedCertificates, CycleDetectionModulus;
            HCERTSTORE hExclusiveRoot, hExclusiveTrustedPeople;
            DWORD dwExclusiveFlags;
        } cfg;
        std::memset(&cfg, 0, sizeof cfg);
        cfg.hExclusiveRoot = s->roots;   // only these authorities are trusted
        BOOL made = FALSE;
        DWORD sizes[2] = {(DWORD)sizeof cfg, (DWORD)offsetof(EngineConfig, dwExclusiveFlags)};
        for (int attempt = 0; attempt < 2 && !made; attempt++) {
            cfg.cbSize = sizes[attempt];
            made = CertCreateCertificateChainEngine(reinterpret_cast<PCERT_CHAIN_ENGINE_CONFIG>(&cfg), &s->engine);
        }
        if (!made) {
            DWORD e = GetLastError();
            freeSession(s);
            return fail(net::E_Other, "The certificate checking could not be set up (error " + hex32(e) + ").");
        }
    }
    if (!opt.certPem.empty() && !opt.keyPem.empty()) {
        Status st = makeKeyedCert(opt.certPem, opt.keyPem, s->clientKeys);
        if (!st.ok()) { freeSession(s); return st; }
    }
    Status st = makeCredentials(true, s->clientKeys.cert ? &s->clientKeys : nullptr, s->cred);
    if (!st.ok()) { freeSession(s); return st; }
    s->haveCred = true;
    s->ownCred = true;
    s->credPtr = &s->cred;
    s->first = true;
    out = s;
    return success();
}

inline Status serverContext(const Options& opt, Context*& out) {
    out = nullptr;
    Context* c = new Context();
    std::memset(&c->cred, 0, sizeof c->cred);
    Status st = makeKeyedCert(opt.certPem, opt.keyPem, c->keys);
    if (!st.ok()) { delete c; return st; }
    st = makeCredentials(false, &c->keys, c->cred);
    if (!st.ok()) { freeKeyedCert(c->keys); delete c; return st; }
    c->haveCred = true;
    out = c;
    return success();
}

inline void freeContext(Context* c) {
    if (!c) return;
    if (c->haveCred) FreeCredentialsHandle(&c->cred);
    freeKeyedCert(c->keys);
    delete c;
}

inline Status serverAccept(Context* c, Sock fd, Session*& out) {
    out = nullptr;
    Session* s = new Session();
    std::memset(&s->cred, 0, sizeof s->cred);
    std::memset(&s->ctx, 0, sizeof s->ctx);
    std::memset(&s->sizes, 0, sizeof s->sizes);
    s->fd = fd;
    s->client = false;
    s->credPtr = &c->cred;
    s->first = true;
    s->needMore = true;   // a server waits for the hello
    out = s;
    return success();
}

/// Sends what is waiting in `out`; true when it is all gone. With a timeout of 0 nothing waits (the rest stays for the next time).
inline Status flushOut(Session* s, int64_t timeoutMs, bool& empty) {
    empty = s->out.empty();
    while (!s->out.empty()) {
        int sent = 0;
        Status st = net::sendBytes(s->fd, s->out.data(), (int)std::min<size_t>(s->out.size(), 1 << 20), timeoutMs, sent);
        if (!st.ok()) return st.code == net::E_TimedOut ? success() : st;
        s->out.erase(s->out.begin(), s->out.begin() + sent);
        if (sent == 0) break;
    }
    empty = s->out.empty();
    return success();
}

inline Status secFail(SECURITY_STATUS ss, const char* what) {
    unsigned long code = (unsigned long)ss;
    if (ss == SEC_E_UNTRUSTED_ROOT || ss == (SECURITY_STATUS)CERT_E_UNTRUSTEDROOT || ss == (SECURITY_STATUS)CERT_E_EXPIRED || ss == SEC_E_CERT_EXPIRED || ss == (SECURITY_STATUS)CERT_E_CN_NO_MATCH ||
        ss == SEC_E_WRONG_PRINCIPAL || ss == SEC_E_CERT_UNKNOWN)
        return fail(E_Certificate, std::string("The certificate was not accepted: ") + chainErrorText(code) + ".");
    if (ss == SEC_E_ALGORITHM_MISMATCH) return fail(E_Tls, std::string(what) + ": no common protocol or cipher (" + hex32(code) + ").");
    return fail(E_Tls, std::string(what) + ": error " + hex32(code) + ".");
}

/// Keeps the bytes that SChannel did not use ("extra") and drops the rest of the input.
inline void keepExtra(std::vector<uint8_t>& enc, const SecBuffer* buffers, int count) {
    for (int i = 0; i < count; i++) {
        if (buffers[i].BufferType == SECBUFFER_EXTRA && buffers[i].cbBuffer > 0 && buffers[i].cbBuffer <= enc.size()) {
            enc.erase(enc.begin(), enc.end() - buffers[i].cbBuffer);
            return;
        }
    }
    enc.clear();
}

/// The certificate of the server, checked (the chain, the dates, the purpose and the name).
inline Status verifyServer(Session* s) {
    PCCERT_CONTEXT remote = nullptr;
    SECURITY_STATUS ss = QueryContextAttributes(&s->ctx, SECPKG_ATTR_REMOTE_CERT_CONTEXT, &remote);
    if (ss != SEC_E_OK || !remote) return fail(E_Certificate, "The server sent no certificate.");
    Status result = success();
    PCCERT_CHAIN_CONTEXT chain = nullptr;
    CERT_CHAIN_PARA para;
    std::memset(&para, 0, sizeof para);
    para.cbSize = sizeof para;
    if (!CertGetCertificateChain(s->engine, remote, nullptr, remote->hCertStore, &para, 0, nullptr, &chain)) {
        result = fail(E_Certificate, "The certificate chain could not be built (error " + hex32(GetLastError()) + ").");
    } else {
        std::vector<uint8_t> ip;
        bool ipHost = !s->serverName.empty() && isIpLiteral(s->serverName, ip);
        std::wstring name = wide(s->serverName);
        HTTPSPolicyCallbackData ssl;
        std::memset(&ssl, 0, sizeof ssl);
        ssl.cbStruct = sizeof ssl;
        ssl.dwAuthType = AUTHTYPE_SERVER;
        ssl.fdwChecks = ipHost || s->serverName.empty() ? SECURITY_FLAG_IGNORE_CERT_CN_INVALID : 0;   // (an IP address is compared below)
        ssl.pwszServerName = s->serverName.empty() ? nullptr : const_cast<wchar_t*>(name.c_str());
        CERT_CHAIN_POLICY_PARA policy;
        std::memset(&policy, 0, sizeof policy);
        policy.cbSize = sizeof policy;
        policy.pvExtraPolicyPara = &ssl;
        CERT_CHAIN_POLICY_STATUS status;
        std::memset(&status, 0, sizeof status);
        status.cbSize = sizeof status;
        if (!CertVerifyCertificateChainPolicy(CERT_CHAIN_POLICY_SSL, chain, &policy, &status)) {
            result = fail(E_Certificate, "The certificate could not be checked (error " + hex32(GetLastError()) + ").");
        } else if (status.dwError != 0) {
            result = fail(E_Certificate, "The certificate was not accepted: " + chainErrorText(status.dwError) + ".");
        } else if (ipHost) {
            bool match = false;
            PCERT_EXTENSION ext = CertFindExtension(szOID_SUBJECT_ALT_NAME2, remote->pCertInfo->cExtension, remote->pCertInfo->rgExtension);
            if (ext) {
                CERT_ALT_NAME_INFO* names = nullptr;
                DWORD size = 0;
                if (CryptDecodeObjectEx(X509_ASN_ENCODING, X509_ALTERNATE_NAME, ext->Value.pbData, ext->Value.cbData, CRYPT_DECODE_ALLOC_FLAG, nullptr, &names, &size)) {
                    for (DWORD i = 0; i < names->cAltEntry; i++) {
                        const CERT_ALT_NAME_ENTRY& e = names->rgAltEntry[i];
                        if (e.dwAltNameChoice == CERT_ALT_NAME_IP_ADDRESS && e.IPAddress.cbData == ip.size() && std::memcmp(e.IPAddress.pbData, ip.data(), ip.size()) == 0) match = true;
                    }
                    LocalFree(names);
                }
            }
            if (!match) result = fail(E_Certificate, "The certificate was not accepted: it is not valid for the address " + s->serverName + ".");
        }
        CertFreeCertificateChain(chain);
    }
    CertFreeCertificateContext(remote);
    return result;
}

inline Status handshakeStep(Session* s, bool& done) {
    done = false;
    for (;;) {
        bool empty = true;
        Status st = flushOut(s, 0, empty);
        if (!st.ok()) return st;
        if (!empty) return success();   // the other side has to take what was sent first
        if (s->handshakeDone) {
            if (s->renegotiating) { s->renegotiating = false; done = true; return success(); }
            if (!s->haveSizes) {
                SECURITY_STATUS qs = QueryContextAttributes(&s->ctx, SECPKG_ATTR_STREAM_SIZES, &s->sizes);
                if (qs != SEC_E_OK) return fail(E_Tls, "TLS: the record sizes could not be asked (" + hex32((unsigned long)qs) + ").");
                s->haveSizes = true;
            }
            if (s->client && s->verify && !s->verified) {
                Status v = verifyServer(s);
                if (!v.ok()) return v;
                s->verified = true;
            }
            done = true;
            return success();
        }
        if (s->needMore || (!s->first && s->enc.empty())) {
            uint8_t tmp[16384];
            int got = 0;
            st = net::recvBytes(s->fd, tmp, sizeof tmp, 0, got);
            if (!st.ok()) return st.code == net::E_TimedOut ? success() : st;
            if (got == 0) return fail(net::E_Closed, "TLS handshake: the connection was closed by the other side.");
            s->enc.insert(s->enc.end(), tmp, tmp + got);
            s->needMore = false;
        }
        SecBuffer inBuffers[2];
        inBuffers[0].pvBuffer = s->enc.empty() ? nullptr : s->enc.data();
        inBuffers[0].cbBuffer = (ULONG)s->enc.size();
        inBuffers[0].BufferType = SECBUFFER_TOKEN;
        inBuffers[1].pvBuffer = nullptr;
        inBuffers[1].cbBuffer = 0;
        inBuffers[1].BufferType = SECBUFFER_EMPTY;
        SecBufferDesc inDesc = {SECBUFFER_VERSION, 2, inBuffers};
        SecBuffer outBuffer;
        outBuffer.pvBuffer = nullptr;
        outBuffer.cbBuffer = 0;
        outBuffer.BufferType = SECBUFFER_TOKEN;
        SecBufferDesc outDesc = {SECBUFFER_VERSION, 1, &outBuffer};
        ULONG attributes = 0;
        TimeStamp expiry;
        SECURITY_STATUS ss;
        if (s->client) {
            ULONG flags = ISC_REQ_SEQUENCE_DETECT | ISC_REQ_REPLAY_DETECT | ISC_REQ_CONFIDENTIALITY | ISC_REQ_ALLOCATE_MEMORY | ISC_REQ_STREAM | ISC_REQ_EXTENDED_ERROR;
            if (s->clientKeys.cert) flags |= ISC_REQ_USE_SUPPLIED_CREDS;
            bool hello = s->first && !s->renegotiating;
            ss = InitializeSecurityContextA(const_cast<PCredHandle>(s->credPtr), s->haveCtx ? &s->ctx : nullptr, s->serverName.empty() ? nullptr : const_cast<SEC_CHAR*>(s->serverName.c_str()), flags, 0, 0,
                                            hello ? nullptr : &inDesc, 0, &s->ctx, &outDesc, &attributes, &expiry);
        } else {
            ULONG flags = ASC_REQ_SEQUENCE_DETECT | ASC_REQ_REPLAY_DETECT | ASC_REQ_CONFIDENTIALITY | ASC_REQ_ALLOCATE_MEMORY | ASC_REQ_STREAM | ASC_REQ_EXTENDED_ERROR;
            ss = AcceptSecurityContext(const_cast<PCredHandle>(s->credPtr), s->haveCtx ? &s->ctx : nullptr, &inDesc, flags, 0, &s->ctx, &outDesc, &attributes, &expiry);
        }
        if (ss != SEC_E_INCOMPLETE_MESSAGE && ss != SEC_E_INVALID_HANDLE && ss != SEC_E_INSUFFICIENT_MEMORY && ss != SEC_E_INTERNAL_ERROR) s->haveCtx = true;
        s->first = false;
        if (outBuffer.pvBuffer && outBuffer.cbBuffer > 0) s->out.insert(s->out.end(), (uint8_t*)outBuffer.pvBuffer, (uint8_t*)outBuffer.pvBuffer + outBuffer.cbBuffer);
        if (outBuffer.pvBuffer) FreeContextBuffer(outBuffer.pvBuffer);
        if (ss == SEC_E_OK) {
            keepExtra(s->enc, inBuffers, 2);
            s->handshakeDone = true;
            continue;
        }
        if (ss == SEC_I_CONTINUE_NEEDED) {
            keepExtra(s->enc, inBuffers, 2);
            s->needMore = s->enc.empty();
            continue;
        }
        if (ss == SEC_E_INCOMPLETE_MESSAGE) { s->needMore = true; continue; }
        if (ss == SEC_I_INCOMPLETE_CREDENTIALS) {
            // the server asks for a certificate: with one it was given, without we go on with none
            if (++s->credRetries > 2) return fail(E_Tls, "TLS handshake: the server wants a client certificate.");
            continue;
        }
        // an alert may be waiting in `out`: send it, the error is what counts
        bool ignored;
        flushOut(s, 0, ignored);
        return secFail(ss, s->client ? "TLS handshake" : "TLS accept");
    }
}

inline Status waitSocket(Session* s, bool read, int64_t deadline) {
    int64_t left = deadline < 0 ? -1 : deadline - net::nowMillis();
    if (deadline >= 0 && left < 0) left = 0;
    bool r, w;
    Status st = net::waitFor(s->fd, read, !read, left, r, w);
    if (!st.ok()) return st;
    if (!r && !w) return fail(net::E_TimedOut, "The TLS operation timed out.");
    return success();
}

inline Status readSome(Session* s, uint8_t* data, int count, int64_t timeoutMs, int& got) {
    got = 0;
    if (count <= 0) return success();
    int64_t deadline = timeoutMs >= 0 ? net::nowMillis() + timeoutMs : -1;
    for (;;) {
        if (s->plainPos < s->plain.size()) {
            size_t n = std::min<size_t>((size_t)count, s->plain.size() - s->plainPos);
            std::memcpy(data, s->plain.data() + s->plainPos, n);
            s->plainPos += n;
            if (s->plainPos == s->plain.size()) { s->plain.clear(); s->plainPos = 0; }
            got = (int)n;
            return success();
        }
        if (s->eof) return success();
        if (s->renegotiating || !s->handshakeDone) {
            bool done = false;
            Status st = handshakeStep(s, done);
            if (!st.ok()) return st;
            if (!done) {
                st = waitSocket(s, s->out.empty(), deadline);
                if (!st.ok()) return st;
            }
            continue;
        }
        if (!s->enc.empty() && !s->needMoreEnc) {
            SecBuffer b[4];
            b[0].pvBuffer = s->enc.data();
            b[0].cbBuffer = (ULONG)s->enc.size();
            b[0].BufferType = SECBUFFER_DATA;
            for (int i = 1; i < 4; i++) { b[i].pvBuffer = nullptr; b[i].cbBuffer = 0; b[i].BufferType = SECBUFFER_EMPTY; }
            SecBufferDesc desc = {SECBUFFER_VERSION, 4, b};
            SECURITY_STATUS ss = DecryptMessage(&s->ctx, &desc, 0, nullptr);
            if (ss == SEC_E_OK) {
                for (int i = 1; i < 4; i++)
                    if (b[i].BufferType == SECBUFFER_DATA && b[i].cbBuffer > 0) s->plain.assign((uint8_t*)b[i].pvBuffer, (uint8_t*)b[i].pvBuffer + b[i].cbBuffer);
                s->plainPos = 0;
                keepExtra(s->enc, b, 4);
                continue;
            }
            if (ss == SEC_E_INCOMPLETE_MESSAGE) { s->needMoreEnc = true; continue; }
            if (ss == SEC_I_CONTEXT_EXPIRED) { s->eof = true; s->enc.clear(); return success(); }
            if (ss == SEC_I_RENEGOTIATE) {
                keepExtra(s->enc, b, 4);   // the rest is the handshake message that follows
                s->renegotiating = true;
                s->handshakeDone = false;
                s->needMore = s->enc.empty();
                continue;
            }
            return secFail(ss, "TLS read");
        }
        uint8_t tmp[16384];
        int n = 0;
        int64_t left = deadline < 0 ? -1 : deadline - net::nowMillis();
        if (deadline >= 0 && left < 0) left = 0;
        Status st = net::recvBytes(s->fd, tmp, sizeof tmp, left, n);
        if (!st.ok()) return st;
        if (n == 0) {
            if (!s->enc.empty()) return fail(net::E_Closed, "TLS read: the connection ended in the middle of a record.");
            s->eof = true;   // closed without close_notify: the data ends (as with the OpenSSL backend)
            return success();
        }
        s->enc.insert(s->enc.end(), tmp, tmp + n);
        s->needMoreEnc = false;
    }
}

inline Status writeSome(Session* s, const uint8_t* data, int count, int64_t timeoutMs, int& sent) {
    sent = 0;
    if (count <= 0) return success();
    if (!s->handshakeDone || s->renegotiating) return fail(E_Tls, "TLS write: the handshake is not complete.");
    int64_t deadline = timeoutMs >= 0 ? net::nowMillis() + timeoutMs : -1;
    // what an earlier call could not send goes first
    bool empty = true;
    for (;;) {
        int64_t left = deadline < 0 ? -1 : deadline - net::nowMillis();
        if (deadline >= 0 && left < 0) left = 0;
        Status st = flushOut(s, left, empty);
        if (!st.ok()) return st;
        if (empty) break;
        if (deadline >= 0 && net::nowMillis() >= deadline) return fail(net::E_TimedOut, "Sending timed out.");
    }
    size_t chunk = std::min<size_t>((size_t)count, s->sizes.cbMaximumMessage);
    std::vector<uint8_t> buf(s->sizes.cbHeader + chunk + s->sizes.cbTrailer);
    std::memcpy(buf.data() + s->sizes.cbHeader, data, chunk);
    SecBuffer b[4];
    b[0].pvBuffer = buf.data();
    b[0].cbBuffer = s->sizes.cbHeader;
    b[0].BufferType = SECBUFFER_STREAM_HEADER;
    b[1].pvBuffer = buf.data() + s->sizes.cbHeader;
    b[1].cbBuffer = (ULONG)chunk;
    b[1].BufferType = SECBUFFER_DATA;
    b[2].pvBuffer = buf.data() + s->sizes.cbHeader + chunk;
    b[2].cbBuffer = s->sizes.cbTrailer;
    b[2].BufferType = SECBUFFER_STREAM_TRAILER;
    b[3].pvBuffer = nullptr;
    b[3].cbBuffer = 0;
    b[3].BufferType = SECBUFFER_EMPTY;
    SecBufferDesc desc = {SECBUFFER_VERSION, 4, b};
    SECURITY_STATUS ss = EncryptMessage(&s->ctx, 0, &desc, 0);
    if (ss != SEC_E_OK) return secFail(ss, "TLS write");
    s->out.assign(buf.begin(), buf.begin() + b[0].cbBuffer + b[1].cbBuffer + b[2].cbBuffer);
    // the record is ours now (the data is taken); what the socket does not take within the time goes out with the next call or when the session closes
    int64_t left = deadline < 0 ? -1 : deadline - net::nowMillis();
    if (deadline >= 0 && left < 0) left = 0;
    Status st = flushOut(s, left, empty);
    if (!st.ok()) return st;
    sent = (int)chunk;
    return success();
}

inline int pending(Session* s) { return (int)(s->plain.size() - s->plainPos); }

inline void info(Session* s, std::string& protocol, std::string& cipher) {
    protocol.clear();
    cipher.clear();
    if (!s->haveCtx) return;
    SecPkgContext_ConnectionInfo ci;
    std::memset(&ci, 0, sizeof ci);
    if (QueryContextAttributes(&s->ctx, SECPKG_ATTR_CONNECTION_INFO, &ci) != SEC_E_OK) return;
    DWORD p = ci.dwProtocol;
    if (p & (SP_PROT_TLS1_3_CLIENT | SP_PROT_TLS1_3_SERVER)) protocol = "TLSv1.3";
    else if (p & (SP_PROT_TLS1_2_CLIENT | SP_PROT_TLS1_2_SERVER)) protocol = "TLSv1.2";
    else if (p & 0x00000300) protocol = "TLSv1.1";
    else if (p & 0x000000C0) protocol = "TLSv1.0";
    else protocol = "TLS";
    std::string name;
    switch (ci.aiCipher) {
        case CALG_AES_128: name = "AES-128"; break;
        case CALG_AES_192: name = "AES-192"; break;
        case CALG_AES_256: name = "AES-256"; break;
        case CALG_3DES: name = "3DES"; break;
        case CALG_RC4: name = "RC4"; break;
        default: name = "cipher " + hex32(ci.aiCipher); break;
    }
    cipher = name;
#if defined(SECPKG_ATTR_CIPHER_INFO)
    SecPkgContext_CipherInfo suite;
    std::memset(&suite, 0, sizeof suite);
    suite.dwVersion = SECPKGCONTEXT_CIPHERINFO_V1;
    if (QueryContextAttributes(&s->ctx, SECPKG_ATTR_CIPHER_INFO, &suite) == SEC_E_OK && suite.szCipherSuite[0]) {
        char narrow[128] = {0};
        WideCharToMultiByte(CP_UTF8, 0, suite.szCipherSuite, -1, narrow, (int)sizeof narrow - 1, nullptr, nullptr);
        cipher = narrow;   // the name of the suite: TLS_AES_256_GCM_SHA384
    }
#endif
}

inline void closeSession(Session* s) {
    if (!s) return;
    if (s->haveCtx && s->handshakeDone && !s->eof) {
        // close_notify, best effort (the socket does not wait)
        DWORD shutdown = SCHANNEL_SHUTDOWN;
        SecBuffer token;
        token.pvBuffer = &shutdown;
        token.cbBuffer = sizeof shutdown;
        token.BufferType = SECBUFFER_TOKEN;
        SecBufferDesc tokenDesc = {SECBUFFER_VERSION, 1, &token};
        if (ApplyControlToken(&s->ctx, &tokenDesc) == SEC_E_OK) {
            SecBuffer outBuffer;
            outBuffer.pvBuffer = nullptr;
            outBuffer.cbBuffer = 0;
            outBuffer.BufferType = SECBUFFER_TOKEN;
            SecBufferDesc outDesc = {SECBUFFER_VERSION, 1, &outBuffer};
            ULONG attributes = 0;
            TimeStamp expiry;
            SECURITY_STATUS ss;
            if (s->client)
                ss = InitializeSecurityContextA(const_cast<PCredHandle>(s->credPtr), &s->ctx, nullptr, ISC_REQ_SEQUENCE_DETECT | ISC_REQ_REPLAY_DETECT | ISC_REQ_CONFIDENTIALITY | ISC_REQ_ALLOCATE_MEMORY | ISC_REQ_STREAM, 0, 0,
                                                nullptr, 0, &s->ctx, &outDesc, &attributes, &expiry);
            else
                ss = AcceptSecurityContext(const_cast<PCredHandle>(s->credPtr), &s->ctx, nullptr, ASC_REQ_SEQUENCE_DETECT | ASC_REQ_REPLAY_DETECT | ASC_REQ_CONFIDENTIALITY | ASC_REQ_ALLOCATE_MEMORY | ASC_REQ_STREAM, 0,
                                           &s->ctx, &outDesc, &attributes, &expiry);
            if ((ss == SEC_E_OK || ss == SEC_I_CONTEXT_EXPIRED) && outBuffer.pvBuffer && outBuffer.cbBuffer > 0) {
                s->out.insert(s->out.end(), (uint8_t*)outBuffer.pvBuffer, (uint8_t*)outBuffer.pvBuffer + outBuffer.cbBuffer);
                bool ignored;
                flushOut(s, 0, ignored);
            }
            if (outBuffer.pvBuffer) FreeContextBuffer(outBuffer.pvBuffer);
        }
    }
    freeSession(s);
}

}  // namespace tls
}  // namespace plat
}  // namespace fire
