// fire native runtime for natives written in C++ - the bridges of graphics and windows, the natives of packages.
//
// The program is C (generated, it includes fire_rt.h with FIRE_RT_IMPLEMENTATION: the global state of the runtime lives there). The natives in C++ are a file of
// their own (fire_natives.cpp) that includes this header: fire_rt.h compiled as C++ (its functions in namespace fire, with C linkage, so that both files share
// one runtime), the C++ platform layer (namespace fire::plat: the clock, threads, and what the bridges add - files, sockets, the display, ...) and the few
// C++ conveniences the natives were written with. The program calls the natives through functions with C linkage that the generator writes at the end
// of the file (`fire_nat_N`).
#pragma once

#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cmath>

#include "fire_rt.h"

// the C++ platform layer of the target (fire::plat): the core (clock, sleep, threads) - the bridges include the parts they need (FIRE_PLATFORM_FS_HEADER, ...)
#ifdef FIRE_PLATFORM_CPP_HEADER
#include FIRE_PLATFORM_CPP_HEADER
#endif

namespace fire {

// ---- the functions of fire_rt.h that give a result through a pointer, as the C++ runtime had them (a reference)
inline bool timeObjTicks(Value v, int64_t& ticks) { return timeObjTicks(v, &ticks); }
inline bool unitOfTime(uint32_t unit, double& factor) { return unitOfTime(unit, &factor); }

}  // namespace fire
