/* fire native platform package "wasm": a program that runs in the browser (WebAssembly, built with Emscripten). One thread of the browser: the clock and the exit
 * are the POSIX ones, but `Sleep` hands the thread back to the browser (Emscripten's ASYNCIFY), so that the page can draw and deliver events while the program
 * waits. fire threads are tasks of fire's own scheduler (std/fire_sched.h) on the fibers of Emscripten (std/sched/fire_ctx_emscripten.h): switched at the
 * safepoints and while waiting; when every task waits, the browser gets the thread (emscripten_sleep). */
#ifndef FIRE_PLATFORM_H
#define FIRE_PLATFORM_H
#include <emscripten.h>
#define FIRE_PLAT_SLEEP_CUSTOM 1
#define FIRE_PLAT_NAME "wasm"
#ifdef FIRE_THREADS
#define FIRE_GREEN_THREADS 1
#define FIRE_SCHED_IDLE(ms) emscripten_sleep((unsigned)(ms))
#define FIRE_SCHED_CTX_HEADER "sched/fire_ctx_emscripten.h"
#endif
#include "../std/fire_platform_posix.h"
#ifndef FIRE_GREEN_THREADS
static inline void fire_plat_sleepMs(int64_t ms) { emscripten_sleep(ms < 0 ? 0 : (unsigned)ms); }
#endif
#endif
