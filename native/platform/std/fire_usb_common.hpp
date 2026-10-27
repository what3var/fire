// fire native platform layer, USB part: the types every backend shares. A platform package offers `fire_usb.hpp` (the generated file defines FIRE_PLATFORM_USB_HEADER for it) that provides, in fire::plat::usb:
//
//   enum Err                       the codes of the error slot (the same numbers as in the prelude: Usb.UsbException.code)
//   struct Status { int code; std::string message; }     code 0 = ok
//   struct DeviceInfo              id (what openDevice takes), vendor, product, class, bus, address, manufacturer, product name, serial number
//   struct EndpointInfo            interface, alternate setting, address (bit 7: IN), type (0 control, 1 isochronous, 2 bulk, 3 interrupt), max packet size, interface class
//   struct Device;                 an opened device (opaque)
//   bool supported()               false: there is no USB access here (the list is empty)
//   Status devices(std::vector<DeviceInfo>&)
//   Status openDevice(id, Device*&)
//   Status endpoints(Device*, std::vector<EndpointInfo>&)
//   Status claim(Device*, int interface) / release(Device*, int interface) / setAlternate(Device*, int interface, int alternate)
//   Status control(Device*, requestType, request, value, index, uint8_t* data, int length, int timeoutMs, int& transferred)
//   Status transfer(Device*, int type, int endpoint, uint8_t* data, int length, int timeoutMs, int& transferred)   type 2 bulk, 3 interrupt; the direction is bit 7 of the endpoint
//   Status clearHalt(Device*, int endpoint)
//   void closeDevice(Device*)
// A timeout of 0 waits without a limit. Transfers are done in the call.
#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace fire {
namespace plat {
namespace usb {

enum Err { E_None = 0, E_InvalidArgument = 1, E_InvalidHandle = 2, E_NotFound = 3, E_Busy = 4, E_Permission = 5, E_Unsupported = 6, E_Other = 7, E_Timeout = 8, E_Stall = 9,
           E_Disconnected = 10, E_Overflow = 11 };

struct Status {
    int code = 0;
    std::string message;
    bool ok() const { return code == 0; }
};

inline Status fail(int code, const std::string& message) { Status s; s.code = code; s.message = message; return s; }
inline Status success() { return Status(); }

struct DeviceInfo {
    std::string id, manufacturer, product, serial;
    int vendorId = 0, productId = 0, deviceClass = 0, bus = 0, address = 0;
};
struct EndpointInfo {
    int interfaceNumber = 0, alternate = 0, address = 0, type = 0, maxPacket = 0, interfaceClass = 0;
};

}  // namespace usb
}  // namespace plat
}  // namespace fire
