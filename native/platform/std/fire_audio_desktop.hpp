// fire native platform layer, audio part for the hosted platforms: PulseAudio or ALSA on Linux (loaded at run time), winmm on Windows, nothing elsewhere (macOS).
#pragma once

#if defined(_WIN32) && !defined(FIRE_NO_AUDIO)
#include "fire_audio_win32.hpp"
#elif defined(__linux__) && !defined(FIRE_NO_AUDIO)
#include "fire_audio_linux.hpp"
#else
#include "fire_audio_none.hpp"
#endif
