/* fire audio package: audio part in C for the hosted platforms: PulseAudio or ALSA on Linux (loaded at run time), winmm on Windows, Audio Queue Services on macOS,
 * Web Audio in the browser - and SDL2 everywhere with FIRE_AUDIO_SDL; nothing with FIRE_NO_AUDIO. */
#ifndef FIRE_AUDIO_DESKTOP_H
#define FIRE_AUDIO_DESKTOP_H

#if defined(FIRE_AUDIO_SDL) && !defined(FIRE_NO_AUDIO)
#define FIRE_AUDIO_USE_SDL 1
#elif defined(__EMSCRIPTEN__) && !defined(FIRE_NO_AUDIO)
#define FIRE_AUDIO_USE_WEB 1
#elif defined(__APPLE__) && !defined(FIRE_NO_AUDIO)
#define FIRE_AUDIO_USE_COREAUDIO 1
#elif defined(_WIN32) && !defined(FIRE_NO_AUDIO)
#define FIRE_AUDIO_USE_WIN32 1
#elif defined(__linux__) && !defined(FIRE_NO_AUDIO)
#define FIRE_AUDIO_USE_LINUX 1
#else
#define FIRE_AUDIO_USE_NONE 1
#endif

#endif
