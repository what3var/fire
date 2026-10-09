// fire native platform layer, TLS part for a platform without TLS: every call fails with "not supported" (`supported()` is false). Select it with FIRE_NO_TLS in the target configuration
// (`defines`). The interface is the one of fire_tls_openssl.hpp.
#pragma once

#include <cstdint>
#include <string>

#include FIRE_PLATFORM_NET_HEADER

namespace fire {
namespace plat {
namespace tls {

using net::Status;
using net::Sock;

enum TlsErr { E_Tls = 14, E_Certificate = 15 };

struct Options {
    bool verify = true;
    std::string caFile, caPem, serverName, certPem, keyPem;
};
struct Context {};
struct Session {};

inline bool supported() { return false; }
inline Status none() { Status s; s.code = net::E_Unsupported; s.message = "This platform (or this build) has no TLS."; return s; }

inline Status clientOpen(Sock, const Options&, Session*& out) { out = nullptr; return none(); }
inline Status handshakeStep(Session*, bool& done) { done = false; return none(); }
inline Status serverContext(const Options&, Context*& out) { out = nullptr; return none(); }
inline void freeContext(Context*) {}
inline Status serverAccept(Context*, Sock, Session*& out) { out = nullptr; return none(); }
inline Status readSome(Session*, uint8_t*, int, int64_t, int&) { return none(); }
inline Status writeSome(Session*, const uint8_t*, int, int64_t, int&) { return none(); }
inline int pending(Session*) { return 0; }
inline void info(Session*, std::string& protocol, std::string& cipher) { protocol.clear(); cipher.clear(); }
inline void closeSession(Session*) {}

}  // namespace tls
}  // namespace plat
}  // namespace fire
