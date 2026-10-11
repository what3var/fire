// fire native platform layer, audio part for a platform without sound output (a board without a package of its own, a Linux whose sound libraries are not installed is NOT this case: the
// Linux part finds that out when it is asked): the list of devices is empty and every call fails with "not supported". The simulated device `sim` of the bridge works everywhere. A platform package
// offers `fire_audio.hpp` (the generated file defines FIRE_PLATFORM_AUDIO_HEADER for it) that provides, in fire::plat::audio:
//
//   enum Err                       the codes of the error slot (the same numbers as in the prelude: Audio.AudioException.code)
//   struct Status { int code; std::string message; }     code 0 = ok
//   struct Output;                 an opened output (opaque)
//   bool supported()               false: there is no sound here (the list of devices is empty)
//   Status devices(std::vector<std::string>& names)       the devices of this machine besides "sim" ("default", "pulse", "alsa" on Linux, "default" on Windows, "pwm" on an ESP32)
//   Status open(name, int rate, int channels, Output*& out)     16 bit signed samples, little endian, interleaved; NotFound for a device that is not there
//   Status write(Output*, const uint8_t* data, int bytes, int& accepted)   takes as many whole frames as fit into the buffer of the output and returns at once (never blocks)
//   int64_t queued(Output*)        the bytes that were accepted and have not been played yet
//   void stop(Output*)             throws the queued sound away (the output stays open)
//   void close(Output*)            closes the output; what is queued is not played any more (the bridge waits for the end before, if the program wants that)
// Nothing waits: the bridge asks `queued` and the fire code sleeps in between, so a program can always be aborted.
#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace fire {
namespace plat {
namespace audio {

enum Err { E_None = 0, E_InvalidArgument = 1, E_InvalidHandle = 2, E_NotFound = 3, E_Busy = 4, E_Permission = 5, E_Unsupported = 6, E_Other = 7 };

struct Status {
    int code = 0;
    std::string message;
    bool ok() const { return code == 0; }
};

inline Status fail(int code, const std::string& message) { Status s; s.code = code; s.message = message; return s; }
inline Status success() { return Status(); }

struct Output {};

inline bool supported() { return false; }
inline Status none() { return fail(E_Unsupported, "This platform has no sound output (the simulated device \"sim\" works everywhere)."); }

inline Status devices(std::vector<std::string>& names) { names.clear(); return success(); }
inline Status open(const std::string&, int, int, Output*& out) { out = nullptr; return fail(E_NotFound, "There is no such sound device on this platform."); }
inline Status write(Output*, const uint8_t*, int, int& accepted) { accepted = 0; return none(); }
inline int64_t queued(Output*) { return 0; }
inline void stop(Output*) {}
inline void close(Output*) {}

}  // namespace audio
}  // namespace plat
}  // namespace fire
