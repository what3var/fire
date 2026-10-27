// fire native platform layer, USB part for a platform without USB host access (a board, the browser): the list of devices is empty and every call fails with "not supported". The simulated
// device "sim" of the bridge works everywhere. The interface is described in fire_usb_common.hpp.
#pragma once

#include "fire_usb_common.hpp"

namespace fire {
namespace plat {
namespace usb {

struct Device {};

inline bool supported() { return false; }
inline Status none() { return fail(E_Unsupported, "This platform has no USB access (the simulated device \"sim\" works everywhere)."); }

inline Status devices(std::vector<DeviceInfo>& out) { out.clear(); return success(); }
inline Status openDevice(const std::string&, Device*& out) { out = nullptr; return fail(E_NotFound, "There is no such USB device on this platform."); }
inline Status endpoints(Device*, std::vector<EndpointInfo>& out) { out.clear(); return none(); }
inline Status claim(Device*, int) { return none(); }
inline Status release(Device*, int) { return none(); }
inline Status setAlternate(Device*, int, int) { return none(); }
inline Status control(Device*, int, int, int, int, uint8_t*, int, int, int& transferred) { transferred = 0; return none(); }
inline Status transfer(Device*, int, int, uint8_t*, int, int, int& transferred) { transferred = 0; return none(); }
inline Status clearHalt(Device*, int) { return none(); }
inline void closeDevice(Device*) {}
}  // namespace usb
}  // namespace plat
}  // namespace fire
