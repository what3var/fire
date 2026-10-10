// fire native platform layer, audio part for the hosted platforms: PulseAudio or ALSA on Linux (loaded at run time), winmm on Windows, SDL2 on macOS - and SDL2 everywhere with FIRE_AUDIO_SDL.
#pragma once

#if (defined(__APPLE__) || defined(FIRE_AUDIO_SDL)) && !defined(FIRE_NO_AUDIO)
#include "fire_audio_sdl.hpp"
#elif defined(_WIN32) && !defined(FIRE_NO_AUDIO)
#include "fire_audio_win32.hpp"
#elif defined(__linux__) && !defined(FIRE_NO_AUDIO)
#include "fire_audio_linux.hpp"
#else
#include "fire_audio_none.hpp"
#endif
