// fire native platform layer, audio part on SDL2: the audio subsystem of SDL2 in its queue mode (SDL_QueueAudio). This is the backend of macOS (SDL2 is also what the windows of the `windows`
// package use there: `brew install sdl2`), and any other platform can choose it with FIRE_AUDIO_SDL instead of its own (PulseAudio/ALSA on Linux, winmm on Windows). The interface is the one of
// fire_audio_none.hpp.
//
// SDL converts the samples to what the device wants (rate, channels), so any rate and mono/stereo work. The queue of SDL is unbounded; `write` keeps it to about half a second, so that the
// program is not far ahead of the sound and `Stop` is quick. The device list is what SDL reports ("default" is the system's default device).
#pragma once

#ifndef SDL_MAIN_HANDLED
#define SDL_MAIN_HANDLED
#endif
#include <SDL.h>

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

struct Output {
    SDL_AudioDeviceID device = 0;
    int frameBytes = 4;
    int bytesPerSecond = 0;
    int bufferMs = 0;       // what the device holds besides the queue (SDL's buffer)
    bool discard = false;
};

inline int& users() { static int count = 0; return count; }

/// Starts the audio subsystem of SDL (once; every output counts as a user). An empty string if it worked, else SDL's reason.
inline std::string startSdl() {
    if (users() == 0) {
        SDL_SetMainReady();
        if (SDL_InitSubSystem(SDL_INIT_AUDIO) != 0) return SDL_GetError();
    }
    users()++;
    return "";
}
inline void stopSdl() {
    if (users() > 0 && --users() == 0) SDL_QuitSubSystem(SDL_INIT_AUDIO);
}

inline bool supported() {
    std::string err = startSdl();
    if (!err.empty()) return false;
    stopSdl();
    return true;
}

inline Status devices(std::vector<std::string>& names) {
    names.clear();
    std::string err = startSdl();
    if (!err.empty()) return success();   // no audio subsystem: no devices
    names.push_back("default");
    int count = SDL_GetNumAudioDevices(0);
    for (int i = 0; i < count; i++) {
        const char* n = SDL_GetAudioDeviceName(i, 0);
        if (n && *n) names.push_back(n);
    }
    stopSdl();
    return success();
}

inline Status open(const std::string& name, int rate, int channels, Output*& out) {
    out = nullptr;
    std::string err = startSdl();
    if (!err.empty()) return fail(E_Unsupported, "SDL2 audio: " + err);
    const char* wanted = nullptr;   // (SDL's own string for the device; NULL is the default device)
    if (name != "default") {
        int count = SDL_GetNumAudioDevices(0);
        for (int i = 0; i < count && !wanted; i++) {
            const char* n = SDL_GetAudioDeviceName(i, 0);
            if (n && name == n) wanted = n;
        }
        if (!wanted && !name.empty() && name.find_first_not_of("0123456789") == std::string::npos && std::stoi(name) < count) wanted = SDL_GetAudioDeviceName(std::stoi(name), 0);
        if (!wanted) { stopSdl(); return fail(E_NotFound, "There is no sound device '" + name + "' (\"default\" or one of the names that Audio.Board.Devices() lists)."); }
    }
    SDL_AudioSpec want;
    SDL_zero(want);
    want.freq = rate;
    want.format = AUDIO_S16LSB;
    want.channels = (Uint8)channels;
    want.samples = (Uint16)(rate <= 24000 ? 1024 : rate <= 48000 ? 2048 : 4096);
    want.callback = nullptr;   // queue mode
    SDL_AudioSpec have;
    SDL_AudioDeviceID dev = SDL_OpenAudioDevice(wanted, 0, &want, &have, 0);   // (0: SDL converts to what the device takes)
    if (dev == 0) { std::string reason = SDL_GetError(); stopSdl(); return fail(E_Other, "SDL2 audio: " + reason); }
    Output* o = new Output();
    o->device = dev;
    o->frameBytes = 2 * channels;
    o->bytesPerSecond = rate * o->frameBytes;
    o->bufferMs = (int)((long long)have.samples * 1000 / (have.freq > 0 ? have.freq : rate));
    SDL_PauseAudioDevice(dev, 0);
    out = o;
    return success();
}

inline Status write(Output* o, const uint8_t* data, int bytes, int& accepted) {
    accepted = 0;
    o->discard = false;
    Uint32 queued = SDL_GetQueuedAudioSize(o->device);
    Uint32 capacity = (Uint32)(o->bytesPerSecond / 2);
    if (queued >= capacity) return success();
    uint32_t n = (uint32_t)bytes < capacity - queued ? (uint32_t)bytes : capacity - queued;
    n -= n % (uint32_t)o->frameBytes;
    if (n == 0) return success();
    if (SDL_QueueAudio(o->device, data, n) != 0) return fail(E_Other, std::string("SDL2 audio: ") + SDL_GetError());
    accepted = (int)n;
    return success();
}

inline int64_t queued(Output* o) { return (int64_t)SDL_GetQueuedAudioSize(o->device); }

inline void stop(Output* o) {
    o->discard = true;
    SDL_ClearQueuedAudio(o->device);
}

inline void close(Output* o) {
    if (!o) return;
    if (!o->discard) {
        // plays what was accepted: the queue, then what the device itself still holds (at most the queue plus one buffer; a second is the limit)
        for (int waited = 0; waited < 3000 && SDL_GetQueuedAudioSize(o->device) > 0; waited += 5) SDL_Delay(5);
        SDL_Delay((Uint32)o->bufferMs);
    }
    SDL_CloseAudioDevice(o->device);
    stopSdl();
    delete o;
}

}  // namespace audio
}  // namespace plat
}  // namespace fire
