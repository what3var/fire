// fire native platform layer, audio part on Linux: PulseAudio (the "simple" API) or ALSA - whichever is there. Neither library is needed to BUILD a program (no headers, no link library): they are
// loaded with dlopen when the first output is opened or the list of devices is asked for, and only the few functions used here are looked up. So one binary runs on a machine with PulseAudio
// or PipeWire (which speaks the Pulse protocol), on one with plain ALSA, and on one with neither (then every call says "not supported").
//
// Names: "default" takes PulseAudio when a server answers and ALSA otherwise; "pulse" / "pulse:<sink>" and "alsa" / "alsa:<pcm>" (for example "alsa:plughw:1,0") name the backend.
// The interface is the one of fire_audio_none.hpp.
//
// ALSA is used in non-blocking mode: `write` takes what fits into the buffer of the device (about half a second) and returns. PulseAudio's simple API only blocks, so each Pulse output has a
// thread that plays a ring buffer (`write` fills the ring and returns); all calls into the simple API are made by that thread.
#pragma once

#include <dlfcn.h>

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <string>
#include <thread>
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

// ---- the libraries ----------------------------------------------------------------------------------------------------------------------
struct PaSampleSpec { int format; uint32_t rate; uint8_t channels; };   // pa_sample_spec (PA_SAMPLE_S16LE = 3)
struct PaBufferAttr { uint32_t maxlength, tlength, prebuf, minreq, fragsize; };   // pa_buffer_attr ((uint32_t)-1: the default)

inline void quietHandler(const char*, int, const char*, int, const char*, ...) {}

struct Libs {
    std::mutex loading;   // (the libraries are loaded once, by whoever asks first)
    bool triedAlsa = false, triedPulse = false;
    void* alsa = nullptr;
    void* pulse = nullptr;
    void* pulseCore = nullptr;   // libpulse: pa_strerror
    // ALSA
    int (*snd_pcm_open)(void**, const char*, int, int) = nullptr;
    int (*snd_pcm_close)(void*) = nullptr;
    int (*snd_pcm_set_params)(void*, int, int, unsigned, unsigned, int, unsigned) = nullptr;
    long (*snd_pcm_writei)(void*, const void*, unsigned long) = nullptr;
    int (*snd_pcm_delay)(void*, long*) = nullptr;
    int (*snd_pcm_recover)(void*, int, int) = nullptr;
    int (*snd_pcm_drop)(void*) = nullptr;
    int (*snd_pcm_drain)(void*) = nullptr;
    int (*snd_pcm_prepare)(void*) = nullptr;
    int (*snd_pcm_start)(void*) = nullptr;
    int (*snd_pcm_state)(void*) = nullptr;
    int (*snd_pcm_nonblock)(void*, int) = nullptr;
    const char* (*snd_strerror)(int) = nullptr;
    void (*snd_lib_error_set_handler)(void (*)(const char*, int, const char*, int, const char*, ...)) = nullptr;
    // PulseAudio simple API
    void* (*pa_simple_new)(const char*, const char*, int, const char*, const char*, const PaSampleSpec*, const void*, const PaBufferAttr*, int*) = nullptr;
    int (*pa_simple_write)(void*, const void*, size_t, int*) = nullptr;
    int (*pa_simple_drain)(void*, int*) = nullptr;
    int (*pa_simple_flush)(void*, int*) = nullptr;
    uint64_t (*pa_simple_get_latency)(void*, int*) = nullptr;
    void (*pa_simple_free)(void*) = nullptr;
    const char* (*pa_strerror)(int) = nullptr;

    template <typename F> static bool bind(void* lib, const char* name, F& fn) { fn = reinterpret_cast<F>(dlsym(lib, name)); return fn != nullptr; }

    bool haveAlsa() {
        std::lock_guard<std::mutex> guard(loading);
        if (!triedAlsa) {
            triedAlsa = true;
            alsa = dlopen("libasound.so.2", RTLD_LAZY | RTLD_LOCAL);
            if (!alsa) alsa = dlopen("libasound.so", RTLD_LAZY | RTLD_LOCAL);
            if (alsa) {
                bool all = bind(alsa, "snd_pcm_open", snd_pcm_open) && bind(alsa, "snd_pcm_close", snd_pcm_close) && bind(alsa, "snd_pcm_set_params", snd_pcm_set_params) &&
                           bind(alsa, "snd_pcm_writei", snd_pcm_writei) && bind(alsa, "snd_pcm_delay", snd_pcm_delay) && bind(alsa, "snd_pcm_recover", snd_pcm_recover) &&
                           bind(alsa, "snd_pcm_drop", snd_pcm_drop) && bind(alsa, "snd_pcm_drain", snd_pcm_drain) && bind(alsa, "snd_pcm_prepare", snd_pcm_prepare) &&
                           bind(alsa, "snd_pcm_start", snd_pcm_start) && bind(alsa, "snd_pcm_state", snd_pcm_state) && bind(alsa, "snd_pcm_nonblock", snd_pcm_nonblock) &&
                           bind(alsa, "snd_strerror", snd_strerror);
                if (!all) { dlclose(alsa); alsa = nullptr; }
                else if (bind(alsa, "snd_lib_error_set_handler", snd_lib_error_set_handler)) snd_lib_error_set_handler(quietHandler);   // (ALSA would print its complaints to stderr; we report the error ourselves)
            }
        }
        return alsa != nullptr;
    }
    bool havePulse() {
        std::lock_guard<std::mutex> guard(loading);
        if (!triedPulse) {
            triedPulse = true;
            pulse = dlopen("libpulse-simple.so.0", RTLD_LAZY | RTLD_LOCAL);
            if (pulse) {
                bool all = bind(pulse, "pa_simple_new", pa_simple_new) && bind(pulse, "pa_simple_write", pa_simple_write) && bind(pulse, "pa_simple_drain", pa_simple_drain) &&
                           bind(pulse, "pa_simple_flush", pa_simple_flush) && bind(pulse, "pa_simple_get_latency", pa_simple_get_latency) && bind(pulse, "pa_simple_free", pa_simple_free);
                if (!all) { dlclose(pulse); pulse = nullptr; }
                else {
                    pulseCore = dlopen("libpulse.so.0", RTLD_LAZY | RTLD_LOCAL);
                    if (pulseCore) bind(pulseCore, "pa_strerror", pa_strerror);
                }
            }
        }
        return pulse != nullptr;
    }
    std::string paError(int code) {
        if (pa_strerror) return pa_strerror(code);
        return "error " + std::to_string(code);
    }
};
inline Libs& libs() { static Libs l; return l; }

// ---- the outputs ------------------------------------------------------------------------------------------------------------------------
struct Output {
    int kind = 0;               // 1 ALSA, 2 PulseAudio
    int frameBytes = 4;
    int bytesPerSecond = 0;
    // ALSA
    void* pcm = nullptr;
    bool discard = false;       // stop() was called after the last write: close() does not wait for the rest
    // PulseAudio
    void* pa = nullptr;
    std::thread worker;
    std::mutex mutex;
    std::condition_variable wake;
    std::vector<uint8_t> ring;  // the sound that waits for the thread
    size_t ringHead = 0;        // where the next byte is read
    size_t ringSize = 0;        // bytes in the ring
    size_t inFlight = 0;        // the bytes the thread is writing to the server right now
    int64_t latencyBytes = 0;   // what the server holds, measured after the last write ...
    std::chrono::steady_clock::time_point latencyStamp;   // ... at this time
    bool quit = false;
    bool flush = false;
    std::string failure;        // the worker saw an error
};

inline Status alsaOpen(const std::string& pcmName, int rate, int channels, Output*& out) {
    Libs& l = libs();
    if (!l.haveAlsa()) return fail(E_Unsupported, "ALSA is not installed (libasound.so.2 was not found).");
    void* pcm = nullptr;
    int err = l.snd_pcm_open(&pcm, pcmName.c_str(), 0 /* SND_PCM_STREAM_PLAYBACK */, 1 /* SND_PCM_NONBLOCK */);
    if (err < 0) {
        int code = (err == -2 || err == -19) ? E_NotFound : (err == -16) ? E_Busy : (err == -13 || err == -1) ? E_Permission : E_Other;
        return fail(code, "ALSA '" + pcmName + "': " + l.snd_strerror(err));
    }
    err = l.snd_pcm_set_params(pcm, 2 /* SND_PCM_FORMAT_S16_LE */, 3 /* SND_PCM_ACCESS_RW_INTERLEAVED */, (unsigned)channels, (unsigned)rate, 1, 500000);
    if (err < 0) {
        l.snd_pcm_close(pcm);
        return fail(err == -22 ? E_Unsupported : E_Other, "ALSA '" + pcmName + "' cannot play " + std::to_string(rate) + " Hz, " + std::to_string(channels) + " channel(s): " + l.snd_strerror(err));
    }
    Output* o = new Output();
    o->kind = 1;
    o->pcm = pcm;
    o->frameBytes = 2 * channels;
    o->bytesPerSecond = o->frameBytes * rate;
    out = o;
    return success();
}

inline void pulseThread(Output* o) {
    Libs& l = libs();
    std::vector<uint8_t> chunk;
    size_t chunkBytes = (size_t)(o->bytesPerSecond / 50);   // 20 ms
    chunkBytes -= chunkBytes % (size_t)o->frameBytes;
    if (chunkBytes < (size_t)o->frameBytes) chunkBytes = (size_t)o->frameBytes;
    chunk.resize(chunkBytes);
    std::unique_lock<std::mutex> lock(o->mutex);
    for (;;) {
        o->wake.wait(lock, [&] { return o->quit || o->flush || o->ringSize > 0; });
        if (o->flush) {
            o->ringSize = 0;
            o->ringHead = 0;
            o->flush = false;
            lock.unlock();
            int e = 0;
            l.pa_simple_flush(o->pa, &e);
            lock.lock();
            o->latencyBytes = 0;
            o->wake.notify_all();
            continue;
        }
        if (o->ringSize == 0) {   // quit, and everything is written
            if (!o->discard) {
                lock.unlock();
                int e = 0;
                l.pa_simple_drain(o->pa, &e);   // the server plays what it holds
                lock.lock();
            }
            break;
        }
        size_t n = o->ringSize < chunkBytes ? o->ringSize : chunkBytes;
        for (size_t i = 0; i < n; i++) chunk[i] = o->ring[(o->ringHead + i) % o->ring.size()];
        o->ringHead = (o->ringHead + n) % o->ring.size();
        o->ringSize -= n;
        o->inFlight = n;
        lock.unlock();
        int e = 0;
        int r = l.pa_simple_write(o->pa, chunk.data(), n, &e);
        uint64_t latency = 0;
        int e2 = 0;
        if (r >= 0) latency = l.pa_simple_get_latency(o->pa, &e2);
        lock.lock();
        o->inFlight = 0;
        if (r < 0) {
            o->failure = "PulseAudio: " + l.paError(e);
            o->ringSize = 0;
            o->quit = true;
        } else {
            o->latencyBytes = (int64_t)(latency * (uint64_t)o->bytesPerSecond / 1000000ull);
            o->latencyStamp = std::chrono::steady_clock::now();
        }
    }
}

inline Status pulseOpen(const std::string& sink, int rate, int channels, Output*& out) {
    Libs& l = libs();
    if (!l.havePulse()) return fail(E_Unsupported, "PulseAudio is not installed (libpulse-simple.so.0 was not found).");
    PaSampleSpec spec;
    spec.format = 3;   // PA_SAMPLE_S16LE
    spec.rate = (uint32_t)rate;
    spec.channels = (uint8_t)channels;
    int e = 0;
    // a small buffer on the server (a fifth of a second), and playback starts with the first bytes: the default would wait for two seconds of sound before it begins
    PaBufferAttr attr;
    attr.maxlength = (uint32_t)-1;
    attr.tlength = (uint32_t)(rate * channels * 2 / 5);
    attr.prebuf = 0;
    attr.minreq = (uint32_t)-1;
    attr.fragsize = (uint32_t)-1;
    void* pa = l.pa_simple_new(nullptr, "fire", 1 /* PA_STREAM_PLAYBACK */, sink.empty() ? nullptr : sink.c_str(), "fire sound", &spec, nullptr, &attr, &e);
    if (!pa) return fail(e == 1 ? E_Permission : (e == 5 || e == 6) ? E_NotFound : E_Other, "PulseAudio: " + l.paError(e));
    Output* o = new Output();
    o->kind = 2;
    o->pa = pa;
    o->frameBytes = 2 * channels;
    o->bytesPerSecond = o->frameBytes * rate;
    o->ring.resize((size_t)(o->bytesPerSecond / 2) - (size_t)(o->bytesPerSecond / 2) % (size_t)o->frameBytes + (size_t)o->frameBytes);   // half a second
    o->latencyStamp = std::chrono::steady_clock::now();
    o->worker = std::thread(pulseThread, o);
    out = o;
    return success();
}

inline bool supported() { return libs().haveAlsa() || libs().havePulse(); }

inline Status devices(std::vector<std::string>& names) {
    names.clear();
    bool a = libs().haveAlsa(), p = libs().havePulse();
    if (a || p) names.push_back("default");
    if (p) names.push_back("pulse");
    if (a) names.push_back("alsa");
    return success();
}

inline Status open(const std::string& name, int rate, int channels, Output*& out) {
    out = nullptr;
    if (name == "default") {
        Status pulse = libs().havePulse() ? pulseOpen("", rate, channels, out) : fail(E_Unsupported, "PulseAudio is not installed.");
        if (pulse.ok()) return pulse;
        Status alsa = alsaOpen("default", rate, channels, out);
        if (alsa.ok()) return alsa;
        if (!libs().haveAlsa() && !libs().havePulse()) return fail(E_Unsupported, "No sound system found: install PulseAudio (libpulse) or ALSA (libasound2).");
        return alsa.code == E_Unsupported ? pulse : alsa;
    }
    if (name == "pulse" || name.compare(0, 6, "pulse:") == 0) return pulseOpen(name == "pulse" ? "" : name.substr(6), rate, channels, out);
    if (name == "alsa") return alsaOpen("default", rate, channels, out);
    if (name.compare(0, 5, "alsa:") == 0) return alsaOpen(name.substr(5), rate, channels, out);
    return fail(E_NotFound, "There is no sound device '" + name + "' (\"default\", \"pulse\", \"alsa\", \"alsa:<pcm>\", \"pulse:<sink>\").");
}

inline Status write(Output* o, const uint8_t* data, int bytes, int& accepted) {
    accepted = 0;
    Libs& l = libs();
    if (o->kind == 1) {
        o->discard = false;
        long frames = bytes / o->frameBytes;
        if (frames <= 0) return success();
        long n = l.snd_pcm_writei(o->pcm, data, (unsigned long)frames);
        if (n == -11 /* EAGAIN */) return success();
        if (n < 0) {
            int r = l.snd_pcm_recover(o->pcm, (int)n, 1);   // an underrun is fixed here; the next write plays again
            if (r < 0) return fail(E_Other, std::string("ALSA: ") + l.snd_strerror(r));
            n = l.snd_pcm_writei(o->pcm, data, (unsigned long)frames);
            if (n == -11) return success();
            if (n < 0) return fail(E_Other, std::string("ALSA: ") + l.snd_strerror((int)n));
        }
        if (l.snd_pcm_state(o->pcm) == 2 /* SND_PCM_STATE_PREPARED */) l.snd_pcm_start(o->pcm);   // (the device would wait for a full buffer before it starts)
        accepted = (int)(n * o->frameBytes);
        return success();
    }
    std::lock_guard<std::mutex> lock(o->mutex);
    if (!o->failure.empty()) return fail(E_Other, o->failure);
    o->discard = false;
    size_t space = o->ring.size() - o->frameBytes - o->ringSize;
    size_t n = (size_t)bytes < space ? (size_t)bytes : space;
    n -= n % (size_t)o->frameBytes;
    for (size_t i = 0; i < n; i++) o->ring[(o->ringHead + o->ringSize + i) % o->ring.size()] = data[i];
    o->ringSize += n;
    accepted = (int)n;
    if (n > 0) o->wake.notify_all();
    return success();
}

inline int64_t queued(Output* o) {
    Libs& l = libs();
    if (o->kind == 1) {
        long delay = 0;
        if (l.snd_pcm_delay(o->pcm, &delay) < 0 || delay < 0) return 0;   // an underrun or a stopped device: nothing is waiting
        return (int64_t)delay * o->frameBytes;
    }
    std::lock_guard<std::mutex> lock(o->mutex);
    int64_t held = 0;
    if (o->latencyBytes > 0) {
        double elapsed = std::chrono::duration<double>(std::chrono::steady_clock::now() - o->latencyStamp).count();
        held = o->latencyBytes - (int64_t)(elapsed * o->bytesPerSecond);
        if (held < 0) held = 0;
    }
    return (int64_t)o->ringSize + (int64_t)o->inFlight + held;
}

inline void stop(Output* o) {
    Libs& l = libs();
    if (o->kind == 1) {
        o->discard = true;
        l.snd_pcm_drop(o->pcm);
        l.snd_pcm_prepare(o->pcm);
        return;
    }
    std::unique_lock<std::mutex> lock(o->mutex);
    o->discard = true;
    o->flush = true;
    o->wake.notify_all();
    o->wake.wait_for(lock, std::chrono::milliseconds(500), [&] { return !o->flush; });
}

inline void close(Output* o) {
    if (!o) return;
    Libs& l = libs();
    if (o->kind == 1) {
        if (o->discard) l.snd_pcm_drop(o->pcm);
        else { l.snd_pcm_nonblock(o->pcm, 0); l.snd_pcm_drain(o->pcm); }   // plays what was accepted
        l.snd_pcm_close(o->pcm);
    } else {
        {
            std::lock_guard<std::mutex> lock(o->mutex);
            o->quit = true;
            o->wake.notify_all();
        }
        if (o->worker.joinable()) o->worker.join();
        l.pa_simple_free(o->pa);
    }
    delete o;
}

}  // namespace audio
}  // namespace plat
}  // namespace fire
