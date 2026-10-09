// fire native platform layer, I2C part for a platform without I2C buses (Windows, macOS, a board without a package of its own, a Linux without the i2c-dev headers): the list of buses is empty and every
// call fails with "not supported". The simulated bus `sim` of the bridge works everywhere. A platform package offers `fire_i2c.hpp` (the generated file defines FIRE_PLATFORM_I2C_HEADER for it)
// that provides, in fire::plat::i2c:
//
//   enum Err                       the codes of the error slot (the same numbers as in the prelude: I2c.I2cException.code)
//   struct Status { int code; std::string message; }     code 0 = ok
//   struct Bus;                    an opened bus (opaque)
//   bool supported()               false: there is no I2C here (the list of buses is empty)
//   Status buses(std::vector<std::string>& names)         the buses of this machine ("i2c-1" on Linux, "i2c-0" on the ESP32)
//   Status openBus(name, int speedHz, Bus*& out)            NotFound for a bus that is not there
//   Status setSpeed(Bus*, int speedHz)                    where the software decides (the ESP32); on Linux the device tree does, the call is accepted and the value ignored
//   Status write(Bus*, int address, const uint8_t*, int n)            7-bit address; NoAck if nobody answers
//   Status read(Bus*, int address, uint8_t*, int n)
//   Status writeRead(Bus*, int address, const uint8_t* out, int nout, uint8_t* in, int nin)     with a repeated start in between (no stop)
//   Status probe(Bus*, int address, bool& present)        is somebody there
//   void closeBus(Bus*)
// Nothing waits long: a transfer ends when the bus is done with it or its time (a few 100 ms) runs out (Timeout).
#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace fire {
namespace plat {
namespace i2c {

enum Err { E_None = 0, E_InvalidArgument = 1, E_InvalidHandle = 2, E_NotFound = 3, E_Busy = 4, E_Permission = 5, E_Unsupported = 6, E_Other = 7, E_NoAck = 8, E_Timeout = 9 };

struct Status {
    int code = 0;
    std::string message;
    bool ok() const { return code == 0; }
};

inline Status fail(int code, const std::string& message) { Status s; s.code = code; s.message = message; return s; }
inline Status success() { return Status(); }

struct Bus {};

inline bool supported() { return false; }
inline Status none() { return fail(E_Unsupported, "This platform has no I2C buses (the simulated bus \"sim\" works everywhere)."); }

inline Status buses(std::vector<std::string>& names) { names.clear(); return success(); }
inline Status openBus(const std::string&, int, Bus*& out) { out = nullptr; return fail(E_NotFound, "There is no such I2C bus on this platform."); }
inline Status setSpeed(Bus*, int) { return none(); }
inline Status write(Bus*, int, const uint8_t*, int) { return none(); }
inline Status read(Bus*, int, uint8_t*, int) { return none(); }
inline Status writeRead(Bus*, int, const uint8_t*, int, uint8_t*, int) { return none(); }
inline Status probe(Bus*, int, bool&) { return none(); }
inline void closeBus(Bus*) {}

}  // namespace i2c
}  // namespace plat
}  // namespace fire
