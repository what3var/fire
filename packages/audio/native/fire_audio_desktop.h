/* fire audio package: audio part in C for the hosted platforms: PulseAudio or ALSA on Linux (loaded at run time), winmm on Windows, Audio Queue Services on macOS,
 * Web Audio in the browser; nothing with FIRE_NO_AUDIO. */
#ifndef FIRE_AUDIO_DESKTOP_H
#define FIRE_AUDIO_DESKTOP_H

#if defined(__EMSCRIPTEN__) && !defined(FIRE_NO_AUDIO)
#include "fire_audio_web.h"
#elif defined(__APPLE__) && !defined(FIRE_NO_AUDIO)
#include "fire_audio_coreaudio.h"
#elif defined(_WIN32) && !defined(FIRE_NO_AUDIO)
#include "fire_audio_win32.h"
#elif defined(__linux__) && !defined(FIRE_NO_AUDIO)
#include "fire_audio_linux.h"
#else
#include "fire_audio_none.h"
#endif

#endif
