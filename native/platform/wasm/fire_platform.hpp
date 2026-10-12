// fire native platform package "wasm": a program that runs in the browser (WebAssembly, built with Emscripten). Single-threaded: the clock and the exit are the standard ones, but
// `Sleep` hands the thread back to the browser (Emscripten's ASYNCIFY), so that the page can draw and deliver events while the program waits. Fire threads do not exist here.
#pragma once
#include <emscripten.h>
#define FIRE_PLAT_SLEEP_CUSTOM 1
#include "../std/fire_platform_std.hpp"

namespace fire {
namespace plat {
inline void sleepMs(int64_t ms) { emscripten_sleep(ms < 0 ? 0 : (unsigned)ms); }
}  // namespace plat
}  // namespace fire
