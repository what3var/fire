// fire native platform layer, GPIO part for a platform without GPIO pins (Windows, macOS, a board without a package of its own, a Linux without the GPIO character device): the list of
// chips is empty and every call fails with "not supported". The simulated chip `sim` of the bridge works everywhere. A platform package offers `fire_gpio.hpp` (the generated file defines
// FIRE_PLATFORM_GPIO_HEADER for it) that provides, in fire::plat::gpio:
//
//   enum Err                       the codes of the error slot (the same numbers as in the prelude: Gpio.GpioException.code)
//   struct Status { int code; std::string message; }     code 0 = ok
//   struct Line;                   an opened pin (opaque)
//   bool supported()               false: there is no GPIO here (the chips list is empty)
//   Status chips(std::vector<std::string>& names)         the chips of this machine/board ("gpiochip0" on Linux, "gpio" on the ESP32)
//   Status openLine(chip, int line, Line*& out)           claims nothing yet (the first configure does); NotFound for a chip or line that is not there
//   Status configure(Line*, int direction, int pull, int edge, int value)   direction 0 input, 1 output; pull 0 none, 1 up, 2 down; edge 0 none, 1 rising, 2 falling, 3 both (inputs); value: the
//                                  level an output starts with. Can be called again to change the setup.
//   Status read(Line*, int& value) / write(Line*, int value)
//   Status pollEdge(Line*, int& event, int64_t& micros)   without waiting: event 0 nothing, 1 rising, 2 falling; micros: when (a clock of the system)
//   void closeLine(Line*)                                 gives the pin back
#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace fire {
namespace plat {
namespace gpio {

enum Err { E_None = 0, E_InvalidArgument = 1, E_InvalidHandle = 2, E_NotFound = 3, E_Busy = 4, E_Permission = 5, E_Unsupported = 6, E_Other = 7 };

struct Status {
    int code = 0;
    std::string message;
    bool ok() const { return code == 0; }
};

inline Status fail(int code, const std::string& message) { Status s; s.code = code; s.message = message; return s; }
inline Status success() { return Status(); }

struct Line {};

inline bool supported() { return false; }
inline Status none() { return fail(E_Unsupported, "This platform has no GPIO pins (the simulated chip \"sim\" works everywhere)."); }

inline Status chips(std::vector<std::string>& names) { names.clear(); return success(); }
inline Status openLine(const std::string&, int, Line*& out) { out = nullptr; return fail(E_NotFound, "There is no such GPIO chip on this platform."); }
inline Status configure(Line*, int, int, int, int) { return none(); }
inline Status read(Line*, int&) { return none(); }
inline Status write(Line*, int) { return none(); }
inline Status pollEdge(Line*, int&, int64_t&) { return none(); }
inline void closeLine(Line*) {}

}  // namespace gpio
}  // namespace plat
}  // namespace fire
