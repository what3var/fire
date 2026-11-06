/* fire audio package: audio part in C on SDL2: the audio subsystem of SDL2 in its queue mode (SDL_QueueAudio). This is the backend of macOS (SDL2 is also what
 * the windows of the `windows` package use there: `brew install sdl2`), and any other platform can choose it with FIRE_AUDIO_SDL instead of its own (PulseAudio/ALSA on
 * Linux, winmm on Windows). The interface is described in fire_audio_c.h.
 *
 * SDL converts the samples to what the device wants (rate, channels), so any rate and mono/stereo work. The queue of SDL is unbounded; `write` keeps it to about half a
 * second, so that the program is not far ahead of the sound and `Stop` is quick. The device list is what SDL reports ("default" is the system's default device). */
#ifndef FIRE_AUDIO_SDL_H
#define FIRE_AUDIO_SDL_H

#include "fire_audio_c.h"

#ifndef SDL_MAIN_HANDLED
#define SDL_MAIN_HANDLED
#endif
#if defined(__TINYC__) && !defined(SDL_DISABLE_IMMINTRIN_H)
#define SDL_DISABLE_IMMINTRIN_H 1   /* (TinyCC has no intrinsics headers) */
#endif
#include <SDL.h>

struct FireAudioOutput {
    SDL_AudioDeviceID device;
    int frameBytes;
    int bytesPerSecond;
    int bufferMs;       /* what the device holds besides the queue (SDL's buffer) */
    int discard;
};

static int g_fireAudioSdlUsers = 0;

/* Starts the audio subsystem of SDL (once; every output counts as a user). NULL if it worked, else SDL's reason. */
static const char* fire_audio_startSdl(void) {
    if (g_fireAudioSdlUsers == 0) {
        SDL_SetMainReady();
        if (SDL_InitSubSystem(SDL_INIT_AUDIO) != 0) return SDL_GetError();
    }
    g_fireAudioSdlUsers++;
    return NULL;
}
static void fire_audio_stopSdl(void) {
    if (g_fireAudioSdlUsers > 0 && --g_fireAudioSdlUsers == 0) SDL_QuitSubSystem(SDL_INIT_AUDIO);
}

static int fire_audio_supported(void) {
    if (fire_audio_startSdl() != NULL) return 0;
    fire_audio_stopSdl();
    return 1;
}

static int fire_audio_devices(FireAudioList* names, FireAudioStatus* st) {
    int count, i;
    fire_audio_listClear(names);
    if (fire_audio_startSdl() != NULL) return fire_audio_success(st);   /* no audio subsystem: no devices */
    fire_audio_listAdd(names, "default");
    count = SDL_GetNumAudioDevices(0);
    for (i = 0; i < count; i++) {
        const char* n = SDL_GetAudioDeviceName(i, 0);
        if (n && *n) fire_audio_listAdd(names, n);
    }
    fire_audio_stopSdl();
    return fire_audio_success(st);
}

static int fire_audio_open(const char* name, int rate, int channels, FireAudioOutput** out, FireAudioStatus* st) {
    const char* err;
    const char* wanted = NULL;   /* (SDL's own string for the device; NULL is the default device) */
    SDL_AudioSpec want, have;
    SDL_AudioDeviceID dev;
    FireAudioOutput* o;
    *out = NULL;
    err = fire_audio_startSdl();
    if (err != NULL) return fire_audio_fail(st, FIRE_AUDIO_E_UNSUPPORTED, "SDL2 audio: %s", err);
    if (strcmp(name, "default") != 0) {
        int count = SDL_GetNumAudioDevices(0), i;
        for (i = 0; i < count && !wanted; i++) {
            const char* n = SDL_GetAudioDeviceName(i, 0);
            if (n && strcmp(name, n) == 0) wanted = n;
        }
        if (!wanted && fire_audio_isNumber(name) && strlen(name) <= 9 && atoi(name) < count) wanted = SDL_GetAudioDeviceName(atoi(name), 0);
        if (!wanted) { fire_audio_stopSdl(); return fire_audio_fail(st, FIRE_AUDIO_E_NOT_FOUND, "There is no sound device '%s' (\"default\" or one of the names that Audio.Board.Devices() lists).", name); }
    }
    SDL_zero(want);
    want.freq = rate;
    want.format = AUDIO_S16LSB;
    want.channels = (Uint8)channels;
    want.samples = (Uint16)(rate <= 24000 ? 1024 : rate <= 48000 ? 2048 : 4096);
    want.callback = NULL;   /* queue mode */
    dev = SDL_OpenAudioDevice(wanted, 0, &want, &have, 0);   /* (0: SDL converts to what the device takes) */
    if (dev == 0) {
        char reason[160];
        snprintf(reason, sizeof reason, "%s", SDL_GetError());
        fire_audio_stopSdl();
        return fire_audio_fail(st, FIRE_AUDIO_E_OTHER, "SDL2 audio: %s", reason);
    }
    o = (FireAudioOutput*)calloc(1, sizeof(FireAudioOutput));
    if (o == NULL) { SDL_CloseAudioDevice(dev); fire_audio_stopSdl(); return fire_audio_fail(st, FIRE_AUDIO_E_OTHER, "Out of memory."); }
    o->device = dev;
    o->frameBytes = 2 * channels;
    o->bytesPerSecond = rate * o->frameBytes;
    o->bufferMs = (int)((long long)have.samples * 1000 / (have.freq > 0 ? have.freq : rate));
    SDL_PauseAudioDevice(dev, 0);
    *out = o;
    return fire_audio_success(st);
}

static int fire_audio_write(FireAudioOutput* o, const uint8_t* data, int bytes, int* accepted, FireAudioStatus* st) {
    Uint32 queued, capacity;
    uint32_t n;
    *accepted = 0;
    o->discard = 0;
    queued = SDL_GetQueuedAudioSize(o->device);
    capacity = (Uint32)(o->bytesPerSecond / 2);
    if (queued >= capacity) return fire_audio_success(st);
    n = (uint32_t)bytes < capacity - queued ? (uint32_t)bytes : capacity - queued;
    n -= n % (uint32_t)o->frameBytes;
    if (n == 0) return fire_audio_success(st);
    if (SDL_QueueAudio(o->device, data, n) != 0) return fire_audio_fail(st, FIRE_AUDIO_E_OTHER, "SDL2 audio: %s", SDL_GetError());
    *accepted = (int)n;
    return fire_audio_success(st);
}

static int64_t fire_audio_queued(FireAudioOutput* o) { return (int64_t)SDL_GetQueuedAudioSize(o->device); }

static void fire_audio_stop(FireAudioOutput* o) {
    o->discard = 1;
    SDL_ClearQueuedAudio(o->device);
}

static void fire_audio_close(FireAudioOutput* o) {
    if (!o) return;
    if (!o->discard) {
        /* plays what was accepted: the queue, then what the device itself still holds (at most the queue plus one buffer; a second is the limit) */
        int waited;
        for (waited = 0; waited < 3000 && SDL_GetQueuedAudioSize(o->device) > 0; waited += 5) SDL_Delay(5);
        SDL_Delay((Uint32)o->bufferMs);
    }
    SDL_CloseAudioDevice(o->device);
    fire_audio_stopSdl();
    free(o);
}


/* ---- sound input ---------------------------------------------------------------------------------------------------------------------- */
/* Capture with SDL2's queue mode (SDL_DequeueAudio): SDL collects what the device records, `read` takes it. */
struct FireAudioInput {
    SDL_AudioDeviceID device;
    int frameBytes;
    int bytesPerSecond;
};

static int fire_audio_inputSupported(void) {
    int any;
    if (fire_audio_startSdl() != NULL) return 0;
    any = SDL_GetNumAudioDevices(1) > 0;
    fire_audio_stopSdl();
    return any;
}

static int fire_audio_inputDevices(FireAudioList* names, FireAudioStatus* st) {
    int count, i;
    fire_audio_listClear(names);
    if (fire_audio_startSdl() != NULL) return fire_audio_success(st);
    count = SDL_GetNumAudioDevices(1);
    if (count > 0) fire_audio_listAdd(names, "default");
    for (i = 0; i < count; i++) {
        const char* n = SDL_GetAudioDeviceName(i, 1);
        if (n && *n) fire_audio_listAdd(names, n);
    }
    fire_audio_stopSdl();
    return fire_audio_success(st);
}

static int fire_audio_openInput(const char* name, int rate, int channels, FireAudioInput** in, FireAudioStatus* st) {
    const char* err;
    const char* wanted = NULL;
    SDL_AudioSpec want, have;
    SDL_AudioDeviceID dev;
    FireAudioInput* i;
    *in = NULL;
    err = fire_audio_startSdl();
    if (err != NULL) return fire_audio_fail(st, FIRE_AUDIO_E_UNSUPPORTED, "SDL2 audio: %s", err);
    if (strcmp(name, "default") != 0) {
        int count = SDL_GetNumAudioDevices(1), k;
        for (k = 0; k < count && !wanted; k++) {
            const char* n = SDL_GetAudioDeviceName(k, 1);
            if (n && strcmp(name, n) == 0) wanted = n;
        }
        if (!wanted) { fire_audio_stopSdl(); return fire_audio_fail(st, FIRE_AUDIO_E_NOT_FOUND, "There is no sound input '%s' (\"default\" or one of the names that Audio.Board.Inputs() lists).", name); }
    }
    SDL_zero(want);
    want.freq = rate;
    want.format = AUDIO_S16LSB;
    want.channels = (Uint8)channels;
    want.samples = (Uint16)(rate <= 24000 ? 256 : 512);
    want.callback = NULL;   /* queue mode */
    dev = SDL_OpenAudioDevice(wanted, 1, &want, &have, 0);
    if (dev == 0) {
        char reason[160];
        snprintf(reason, sizeof reason, "%s", SDL_GetError());
        fire_audio_stopSdl();
        return fire_audio_fail(st, FIRE_AUDIO_E_OTHER, "SDL2 audio: %s", reason);
    }
    i = (FireAudioInput*)calloc(1, sizeof(FireAudioInput));
    if (i == NULL) { SDL_CloseAudioDevice(dev); fire_audio_stopSdl(); return fire_audio_fail(st, FIRE_AUDIO_E_OTHER, "Out of memory."); }
    i->device = dev;
    i->frameBytes = 2 * channels;
    i->bytesPerSecond = rate * i->frameBytes;
    SDL_PauseAudioDevice(dev, 0);
    *in = i;
    return fire_audio_success(st);
}

static int fire_audio_read(FireAudioInput* in, uint8_t* data, int bytes, int* got, FireAudioStatus* st) {
    int n = bytes - bytes % in->frameBytes;
    *got = n > 0 ? (int)SDL_DequeueAudio(in->device, data, (Uint32)n) : 0;
    *got -= *got % in->frameBytes;
    return fire_audio_success(st);
}

static int64_t fire_audio_available(FireAudioInput* in) { return (int64_t)SDL_GetQueuedAudioSize(in->device); }

static void fire_audio_closeInput(FireAudioInput* in) {
    if (!in) return;
    SDL_CloseAudioDevice(in->device);
    fire_audio_stopSdl();
    free(in);
}

#endif
