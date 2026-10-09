// fire native platform layer, SPI part for a platform without SPI (Windows, macOS, a board without a package of its own, a Linux without the spidev headers): the list of devices is empty and every call fails
// with "not supported". The simulated device `sim` of the bridge works everywhere. A platform package offers `fire_spi.hpp` (the generated file defines FIRE_PLATFORM_SPI_HEADER for it) that provides, in
// fire::plat::spi:
//
//   enum Err                       the codes of the error slot (the same numbers as in the prelude: Spi.SpiException.code)
//   struct Status { int code; std::string message; }     code 0 = ok
//   struct Device;                 an opened device: a bus with one chip select (opaque)
//   bool supported()               false: there is no SPI here (the list of devices is empty)
//   Status devices(std::vector<std::string>& names)       the devices of this machine ("spidev0.0" on Linux, "spi-2" on the ESP32)
//   Status openDevice(name, int mode, int speedHz, bool lsbFirst, Device*& out)    mode 0..3 (clock polarity and phase); NotFound for a device that is not there
//   Status configure(Device*, int mode, int speedHz, bool lsbFirst)                 changes the setup of an opened device
//   Status transfer(Device*, const uint8_t* out, uint8_t* in, int n)               n bytes full duplex with the chip select held low in between; out == nullptr sends zeros, in == nullptr drops what comes in
//   void closeDevice(Device*)
// A transfer is done in the call (n bytes at the speed of the bus; a megabyte at 1 MHz takes 8 seconds - cut long transfers into pieces).
#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace fire {
namespace plat {
namespace spi {

enum Err { E_None = 0, E_InvalidArgument = 1, E_InvalidHandle = 2, E_NotFound = 3, E_Busy = 4, E_Permission = 5, E_Unsupported = 6, E_Other = 7, E_Timeout = 8 };

struct Status {
    int code = 0;
    std::string message;
    bool ok() const { return code == 0; }
};

inline Status fail(int code, const std::string& message) { Status s; s.code = code; s.message = message; return s; }
inline Status success() { return Status(); }

struct Device {};

inline bool supported() { return false; }
inline Status none() { return fail(E_Unsupported, "This platform has no SPI (the simulated device \"sim\" works everywhere)."); }

inline Status devices(std::vector<std::string>& names) { names.clear(); return success(); }
inline Status openDevice(const std::string&, int, int, bool, Device*& out) { out = nullptr; return fail(E_NotFound, "There is no such SPI device on this platform."); }
inline Status configure(Device*, int, int, bool) { return none(); }
inline Status transfer(Device*, const uint8_t*, uint8_t*, int) { return none(); }
inline void closeDevice(Device*) {}

}  // namespace spi
}  // namespace plat
}  // namespace fire
