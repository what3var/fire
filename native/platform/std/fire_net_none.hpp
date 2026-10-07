// fire native platform layer, network part for a platform without a network: every call fails with "not supported" (`supported()` is false). A board package of your own
// provides plat::net (see fire_net_sockets.hpp for the list) with its own network stack; the lwIP of the ESP32 is covered by fire_net_sockets.hpp.
#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace fire {
namespace plat {
namespace net {

enum Err {
    E_None = 0, E_InvalidArgument = 1, E_InvalidHandle = 2, E_Refused = 3, E_TimedOut = 4, E_Unreachable = 5, E_AddressInUse = 6, E_Closed = 7,
    E_Denied = 8, E_Unsupported = 9, E_ResolveFailed = 10, E_Other = 11, E_NotConnected = 12, E_Permission = 13
};

struct Status {
    int code = 0;
    std::string message;
    bool ok() const { return code == 0; }
};

typedef intptr_t Sock;
constexpr Sock kInvalid = -1;

inline bool supported() { return false; }
inline Status none() { Status s; s.code = E_Unsupported; s.message = "This platform has no network."; return s; }

inline Status resolve(const std::string&, std::vector<std::string>&) { return none(); }
struct ConnectState {};
inline Status connectBegin(const std::string&, int, ConnectState*& out) { out = nullptr; return none(); }
inline Status connectStep(ConnectState&, bool& done) { done = false; return none(); }
inline Sock takeConnected(ConnectState&) { return kInvalid; }
inline void freeConnect(ConnectState*) {}
inline Status tcpListen(const std::string&, int, int, Sock&) { return none(); }
inline Status acceptOne(Sock, int64_t, Sock&) { return none(); }
inline Status sendBytes(Sock, const uint8_t*, int, int64_t, int&) { return none(); }
inline Status recvBytes(Sock, uint8_t*, int, int64_t, int&) { return none(); }
inline Status udpOpen(const std::string&, int, Sock&) { return none(); }
inline Status sendDatagram(Sock, const uint8_t*, int, const std::string&, int, int&) { return none(); }
inline Status recvDatagram(Sock, uint8_t*, int, int64_t, int&, std::string&, int&) { return none(); }
inline Status waitFor(Sock, bool, bool, int64_t, bool&, bool&) { return none(); }
inline Status available(Sock, int&) { return none(); }
inline Status localAddress(Sock, std::string&, int&) { return none(); }
inline Status peerAddress(Sock, std::string&, int&) { return none(); }
inline Status setOption(Sock, int, int) { return none(); }
inline Status shutdownSock(Sock, int) { return none(); }
inline void closeSock(Sock) {}

}  // namespace net
}  // namespace plat
}  // namespace fire
