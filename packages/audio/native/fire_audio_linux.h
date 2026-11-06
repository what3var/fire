/* fire audio package: audio part in C on Linux: PulseAudio (the "simple" API) or ALSA - whichever is there. Neither library is needed to BUILD a program (no
 * headers, no link library): they are loaded with dlopen when the first output is opened or the list of devices is asked for, and only the few functions used here
 * are looked up. So one binary runs on a machine with PulseAudio or PipeWire (which speaks the Pulse protocol), on one with plain ALSA, and on one with neither (then
 * every call says "not supported").
 *
 * Names: "default" takes PulseAudio when a server answers and ALSA otherwise; "pulse" / "pulse:<sink>" and "alsa" / "alsa:<pcm>" (for example "alsa:plughw:1,0") name
 * the backend. The interface is described in fire_audio_c.h. (Link: dl, and pthread where the C library does not have it.)
 *
 * ALSA is used in non-blocking mode: `write` takes what fits into the buffer of the device (about half a second) and returns. PulseAudio's simple API only blocks, so
 * each Pulse output has a thread that plays a ring buffer (`write` fills the ring and returns); all calls into the simple API are made by that thread. */
#ifndef FIRE_AUDIO_LINUX_H
#define FIRE_AUDIO_LINUX_H

#include "fire_audio_c.h"

#include <dlfcn.h>
#include <pthread.h>
#include <time.h>

/* ---- the libraries ---------------------------------------------------------------------------------------------------------------------- */
typedef struct FireAudioPaSampleSpec { int format; uint32_t rate; uint8_t channels; } FireAudioPaSampleSpec;   /* pa_sample_spec (PA_SAMPLE_S16LE = 3) */
typedef struct FireAudioPaBufferAttr { uint32_t maxlength, tlength, prebuf, minreq, fragsize; } FireAudioPaBufferAttr;   /* pa_buffer_attr ((uint32_t)-1: the default) */

static void fire_audio_quietHandler(const char* file, int line, const char* function, int err, const char* format, ...) { (void)file; (void)line; (void)function; (void)err; (void)format; }

typedef struct FireAudioLibs {
    int triedAlsa, triedPulse;
    void* alsa;
    void* pulse;
    void* pulseCore;   /* libpulse: pa_strerror */
    /* ALSA */
    int (*snd_pcm_open)(void**, const char*, int, int);
    int (*snd_pcm_close)(void*);
    int (*snd_pcm_set_params)(void*, int, int, unsigned, unsigned, int, unsigned);
    long (*snd_pcm_writei)(void*, const void*, unsigned long);
    int (*snd_pcm_delay)(void*, long*);
    int (*snd_pcm_recover)(void*, int, int);
    int (*snd_pcm_drop)(void*);
    int (*snd_pcm_drain)(void*);
    int (*snd_pcm_prepare)(void*);
    int (*snd_pcm_start)(void*);
    int (*snd_pcm_state)(void*);
    int (*snd_pcm_nonblock)(void*, int);
    const char* (*snd_strerror)(int);
    long (*snd_pcm_readi)(void*, void*, unsigned long);   /* (capture; looked up, not required for output) */
    long (*snd_pcm_avail)(void*);
    void (*snd_lib_error_set_handler)(void (*)(const char*, int, const char*, int, const char*, ...));
    /* PulseAudio simple API */
    void* (*pa_simple_new)(const char*, const char*, int, const char*, const char*, const FireAudioPaSampleSpec*, const void*, const FireAudioPaBufferAttr*, int*);
    int (*pa_simple_write)(void*, const void*, size_t, int*);
    int (*pa_simple_drain)(void*, int*);
    int (*pa_simple_flush)(void*, int*);
    uint64_t (*pa_simple_get_latency)(void*, int*);
    void (*pa_simple_free)(void*);
    const char* (*pa_strerror)(int);
    int (*pa_simple_read)(void*, void*, size_t, int*);     /* (capture; looked up, not required for output) */
} FireAudioLibs;

static FireAudioLibs g_fireAudioLibs;   /* (the libraries are loaded once, by whoever asks first) */
static pthread_mutex_t g_fireAudioLibsLoading = PTHREAD_MUTEX_INITIALIZER;

/* Looks up `name` in `lib` into the function pointer at `fn`: 1 if it is there. */
static int fire_audio_bind(void* lib, const char* name, void* fn) {
    void* p = dlsym(lib, name);
    memcpy(fn, &p, sizeof p);
    return p != NULL;
}

static int fire_audio_haveAlsa(void) {
    FireAudioLibs* l = &g_fireAudioLibs;
    pthread_mutex_lock(&g_fireAudioLibsLoading);
    if (!l->triedAlsa) {
        l->triedAlsa = 1;
        l->alsa = dlopen("libasound.so.2", RTLD_LAZY | RTLD_LOCAL);
        if (!l->alsa) l->alsa = dlopen("libasound.so", RTLD_LAZY | RTLD_LOCAL);
        if (l->alsa) {
            void* a = l->alsa;
            int all = fire_audio_bind(a, "snd_pcm_open", &l->snd_pcm_open) && fire_audio_bind(a, "snd_pcm_close", &l->snd_pcm_close) &&
                      fire_audio_bind(a, "snd_pcm_set_params", &l->snd_pcm_set_params) && fire_audio_bind(a, "snd_pcm_writei", &l->snd_pcm_writei) &&
                      fire_audio_bind(a, "snd_pcm_delay", &l->snd_pcm_delay) && fire_audio_bind(a, "snd_pcm_recover", &l->snd_pcm_recover) &&
                      fire_audio_bind(a, "snd_pcm_drop", &l->snd_pcm_drop) && fire_audio_bind(a, "snd_pcm_drain", &l->snd_pcm_drain) &&
                      fire_audio_bind(a, "snd_pcm_prepare", &l->snd_pcm_prepare) && fire_audio_bind(a, "snd_pcm_start", &l->snd_pcm_start) &&
                      fire_audio_bind(a, "snd_pcm_state", &l->snd_pcm_state) && fire_audio_bind(a, "snd_pcm_nonblock", &l->snd_pcm_nonblock) &&
                      fire_audio_bind(a, "snd_strerror", &l->snd_strerror);
            if (!all) { dlclose(a); l->alsa = NULL; }
            else {
                fire_audio_bind(a, "snd_pcm_readi", &l->snd_pcm_readi);
                fire_audio_bind(a, "snd_pcm_avail", &l->snd_pcm_avail);
                if (fire_audio_bind(a, "snd_lib_error_set_handler", &l->snd_lib_error_set_handler)) l->snd_lib_error_set_handler(fire_audio_quietHandler);   /* (ALSA would print its complaints to stderr; we report the error ourselves) */
            }
        }
    }
    pthread_mutex_unlock(&g_fireAudioLibsLoading);
    return l->alsa != NULL;
}
static int fire_audio_havePulse(void) {
    FireAudioLibs* l = &g_fireAudioLibs;
    pthread_mutex_lock(&g_fireAudioLibsLoading);
    if (!l->triedPulse) {
        l->triedPulse = 1;
        l->pulse = dlopen("libpulse-simple.so.0", RTLD_LAZY | RTLD_LOCAL);
        if (l->pulse) {
            void* p = l->pulse;
            int all = fire_audio_bind(p, "pa_simple_new", &l->pa_simple_new) && fire_audio_bind(p, "pa_simple_write", &l->pa_simple_write) &&
                      fire_audio_bind(p, "pa_simple_drain", &l->pa_simple_drain) && fire_audio_bind(p, "pa_simple_flush", &l->pa_simple_flush) &&
                      fire_audio_bind(p, "pa_simple_get_latency", &l->pa_simple_get_latency) && fire_audio_bind(p, "pa_simple_free", &l->pa_simple_free);
            if (!all) { dlclose(p); l->pulse = NULL; }
            else {
                fire_audio_bind(p, "pa_simple_read", &l->pa_simple_read);
                l->pulseCore = dlopen("libpulse.so.0", RTLD_LAZY | RTLD_LOCAL);
                if (l->pulseCore) fire_audio_bind(l->pulseCore, "pa_strerror", &l->pa_strerror);
            }
        }
    }
    pthread_mutex_unlock(&g_fireAudioLibsLoading);
    return l->pulse != NULL;
}
/* The text of a PulseAudio error code. */
static void fire_audio_paError(int code, char* text, size_t size) {
    if (g_fireAudioLibs.pa_strerror) snprintf(text, size, "%s", g_fireAudioLibs.pa_strerror(code));
    else snprintf(text, size, "error %d", code);
}

/* A monotonic clock in seconds. */
static double fire_audio_seconds(void) {
    struct timespec t;
    clock_gettime(CLOCK_MONOTONIC, &t);
    return (double)t.tv_sec + (double)t.tv_nsec / 1e9;
}

/* ---- the outputs ------------------------------------------------------------------------------------------------------------------------ */
struct FireAudioOutput {
    int kind;                   /* 1 ALSA, 2 PulseAudio */
    int frameBytes;
    int bytesPerSecond;
    /* ALSA */
    void* pcm;
    int discard;                /* stop() was called after the last write: close() does not wait for the rest */
    /* PulseAudio */
    void* pa;
    pthread_t worker;
    int hasWorker;
    pthread_mutex_t mutex;
    pthread_cond_t wake;
    uint8_t* ring;              /* the sound that waits for the thread */
    size_t ringCapacity;
    size_t ringHead;            /* where the next byte is read */
    size_t ringSize;            /* bytes in the ring */
    size_t inFlight;            /* the bytes the thread is writing to the server right now */
    int64_t latencyBytes;       /* what the server holds, measured after the last write ... */
    double latencyStamp;        /* ... at this time */
    int quit;
    int flush;
    char failure[176];          /* the worker saw an error */
};

static int fire_audio_alsaOpen(const char* pcmName, int rate, int channels, FireAudioOutput** out, FireAudioStatus* st) {
    FireAudioLibs* l = &g_fireAudioLibs;
    void* pcm = NULL;
    int err;
    FireAudioOutput* o;
    if (!fire_audio_haveAlsa()) return fire_audio_fail(st, FIRE_AUDIO_E_UNSUPPORTED, "ALSA is not installed (libasound.so.2 was not found).");
    err = l->snd_pcm_open(&pcm, pcmName, 0 /* SND_PCM_STREAM_PLAYBACK */, 1 /* SND_PCM_NONBLOCK */);
    if (err < 0) {
        int code = (err == -2 || err == -19) ? FIRE_AUDIO_E_NOT_FOUND : (err == -16) ? FIRE_AUDIO_E_BUSY : (err == -13 || err == -1) ? FIRE_AUDIO_E_PERMISSION : FIRE_AUDIO_E_OTHER;
        return fire_audio_fail(st, code, "ALSA '%s': %s", pcmName, l->snd_strerror(err));
    }
    err = l->snd_pcm_set_params(pcm, 2 /* SND_PCM_FORMAT_S16_LE */, 3 /* SND_PCM_ACCESS_RW_INTERLEAVED */, (unsigned)channels, (unsigned)rate, 1, 500000);
    if (err < 0) {
        l->snd_pcm_close(pcm);
        return fire_audio_fail(st, err == -22 ? FIRE_AUDIO_E_UNSUPPORTED : FIRE_AUDIO_E_OTHER, "ALSA '%s' cannot play %d Hz, %d channel(s): %s", pcmName, rate, channels, l->snd_strerror(err));
    }
    o = (FireAudioOutput*)calloc(1, sizeof(FireAudioOutput));
    if (o == NULL) { l->snd_pcm_close(pcm); return fire_audio_fail(st, FIRE_AUDIO_E_OTHER, "Out of memory."); }
    o->kind = 1;
    o->pcm = pcm;
    o->frameBytes = 2 * channels;
    o->bytesPerSecond = o->frameBytes * rate;
    *out = o;
    return fire_audio_success(st);
}

static void* fire_audio_pulseThread(void* arg) {
    FireAudioOutput* o = (FireAudioOutput*)arg;
    FireAudioLibs* l = &g_fireAudioLibs;
    uint8_t* chunk;
    size_t chunkBytes = (size_t)(o->bytesPerSecond / 50);   /* 20 ms */
    chunkBytes -= chunkBytes % (size_t)o->frameBytes;
    if (chunkBytes < (size_t)o->frameBytes) chunkBytes = (size_t)o->frameBytes;
    chunk = (uint8_t*)malloc(chunkBytes);
    pthread_mutex_lock(&o->mutex);
    if (chunk == NULL) { snprintf(o->failure, sizeof o->failure, "%s", "PulseAudio: out of memory"); o->ringSize = 0; o->quit = 1; }
    for (;;) {
        size_t n, i;
        int e = 0, e2 = 0, r;
        uint64_t latency = 0;
        while (!(o->quit || o->flush || o->ringSize > 0)) pthread_cond_wait(&o->wake, &o->mutex);
        if (o->flush) {
            o->ringSize = 0;
            o->ringHead = 0;
            o->flush = 0;
            pthread_mutex_unlock(&o->mutex);
            l->pa_simple_flush(o->pa, &e);
            pthread_mutex_lock(&o->mutex);
            o->latencyBytes = 0;
            pthread_cond_broadcast(&o->wake);
            continue;
        }
        if (o->ringSize == 0) {   /* quit, and everything is written */
            if (!o->discard) {
                pthread_mutex_unlock(&o->mutex);
                l->pa_simple_drain(o->pa, &e);   /* the server plays what it holds */
                pthread_mutex_lock(&o->mutex);
            }
            break;
        }
        n = o->ringSize < chunkBytes ? o->ringSize : chunkBytes;
        for (i = 0; i < n; i++) chunk[i] = o->ring[(o->ringHead + i) % o->ringCapacity];
        o->ringHead = (o->ringHead + n) % o->ringCapacity;
        o->ringSize -= n;
        o->inFlight = n;
        pthread_mutex_unlock(&o->mutex);
        r = l->pa_simple_write(o->pa, chunk, n, &e);
        if (r >= 0) latency = l->pa_simple_get_latency(o->pa, &e2);
        pthread_mutex_lock(&o->mutex);
        o->inFlight = 0;
        if (r < 0) {
            char text[128];
            fire_audio_paError(e, text, sizeof text);
            snprintf(o->failure, sizeof o->failure, "PulseAudio: %s", text);
            o->ringSize = 0;
            o->quit = 1;
        } else {
            o->latencyBytes = (int64_t)(latency * (uint64_t)o->bytesPerSecond / 1000000ull);
            o->latencyStamp = fire_audio_seconds();
        }
    }
    pthread_mutex_unlock(&o->mutex);
    free(chunk);
    return NULL;
}

/* The mutex and the condition variable of a Pulse output or input (the condition waits on the monotonic clock). */
static void fire_audio_initSync(pthread_mutex_t* m, pthread_cond_t* c) {
    pthread_condattr_t attr;
    pthread_mutex_init(m, NULL);
    pthread_condattr_init(&attr);
    pthread_condattr_setclock(&attr, CLOCK_MONOTONIC);
    pthread_cond_init(c, &attr);
    pthread_condattr_destroy(&attr);
}

static int fire_audio_pulseOpen(const char* sink, int rate, int channels, FireAudioOutput** out, FireAudioStatus* st) {
    FireAudioLibs* l = &g_fireAudioLibs;
    FireAudioPaSampleSpec spec;
    FireAudioPaBufferAttr attr;
    int e = 0;
    void* pa;
    FireAudioOutput* o;
    if (!fire_audio_havePulse()) return fire_audio_fail(st, FIRE_AUDIO_E_UNSUPPORTED, "PulseAudio is not installed (libpulse-simple.so.0 was not found).");
    spec.format = 3;   /* PA_SAMPLE_S16LE */
    spec.rate = (uint32_t)rate;
    spec.channels = (uint8_t)channels;
    /* a small buffer on the server (a fifth of a second), and playback starts with the first bytes: the default would wait for two seconds of sound before it begins */
    attr.maxlength = (uint32_t)-1;
    attr.tlength = (uint32_t)(rate * channels * 2 / 5);
    attr.prebuf = 0;
    attr.minreq = (uint32_t)-1;
    attr.fragsize = (uint32_t)-1;
    pa = l->pa_simple_new(NULL, "fire", 1 /* PA_STREAM_PLAYBACK */, sink[0] == 0 ? NULL : sink, "fire sound", &spec, NULL, &attr, &e);
    if (!pa) {
        char text[128];
        fire_audio_paError(e, text, sizeof text);
        return fire_audio_fail(st, e == 1 ? FIRE_AUDIO_E_PERMISSION : (e == 5 || e == 6) ? FIRE_AUDIO_E_NOT_FOUND : FIRE_AUDIO_E_OTHER, "PulseAudio: %s", text);
    }
    o = (FireAudioOutput*)calloc(1, sizeof(FireAudioOutput));
    if (o != NULL) {
        o->kind = 2;
        o->pa = pa;
        o->frameBytes = 2 * channels;
        o->bytesPerSecond = o->frameBytes * rate;
        o->ringCapacity = (size_t)(o->bytesPerSecond / 2) - (size_t)(o->bytesPerSecond / 2) % (size_t)o->frameBytes + (size_t)o->frameBytes;   /* half a second */
        o->ring = (uint8_t*)malloc(o->ringCapacity);
    }
    if (o == NULL || o->ring == NULL) {
        if (o) free(o);
        l->pa_simple_free(pa);
        return fire_audio_fail(st, FIRE_AUDIO_E_OTHER, "Out of memory.");
    }
    o->latencyStamp = fire_audio_seconds();
    fire_audio_initSync(&o->mutex, &o->wake);
    if (pthread_create(&o->worker, NULL, fire_audio_pulseThread, o) != 0) {
        pthread_cond_destroy(&o->wake);
        pthread_mutex_destroy(&o->mutex);
        free(o->ring);
        free(o);
        l->pa_simple_free(pa);
        return fire_audio_fail(st, FIRE_AUDIO_E_OTHER, "PulseAudio: the thread of the output could not be started.");
    }
    o->hasWorker = 1;
    *out = o;
    return fire_audio_success(st);
}

static int fire_audio_supported(void) { return fire_audio_haveAlsa() || fire_audio_havePulse(); }

static int fire_audio_devices(FireAudioList* names, FireAudioStatus* st) {
    int a = fire_audio_haveAlsa(), p = fire_audio_havePulse();
    fire_audio_listClear(names);
    if (a || p) fire_audio_listAdd(names, "default");
    if (p) fire_audio_listAdd(names, "pulse");
    if (a) fire_audio_listAdd(names, "alsa");
    return fire_audio_success(st);
}

static int fire_audio_open(const char* name, int rate, int channels, FireAudioOutput** out, FireAudioStatus* st) {
    *out = NULL;
    if (strcmp(name, "default") == 0) {
        FireAudioStatus pulse, alsa;
        if (fire_audio_havePulse()) fire_audio_pulseOpen("", rate, channels, out, &pulse);
        else fire_audio_fail(&pulse, FIRE_AUDIO_E_UNSUPPORTED, "PulseAudio is not installed.");
        if (pulse.code == 0) { *st = pulse; return 1; }
        fire_audio_alsaOpen("default", rate, channels, out, &alsa);
        if (alsa.code == 0) { *st = alsa; return 1; }
        if (!fire_audio_haveAlsa() && !fire_audio_havePulse()) return fire_audio_fail(st, FIRE_AUDIO_E_UNSUPPORTED, "No sound system found: install PulseAudio (libpulse) or ALSA (libasound2).");
        *st = alsa.code == FIRE_AUDIO_E_UNSUPPORTED ? pulse : alsa;
        return 0;
    }
    if (strcmp(name, "pulse") == 0 || strncmp(name, "pulse:", 6) == 0) return fire_audio_pulseOpen(strcmp(name, "pulse") == 0 ? "" : name + 6, rate, channels, out, st);
    if (strcmp(name, "alsa") == 0) return fire_audio_alsaOpen("default", rate, channels, out, st);
    if (strncmp(name, "alsa:", 5) == 0) return fire_audio_alsaOpen(name + 5, rate, channels, out, st);
    return fire_audio_fail(st, FIRE_AUDIO_E_NOT_FOUND, "There is no sound device '%s' (\"default\", \"pulse\", \"alsa\", \"alsa:<pcm>\", \"pulse:<sink>\").", name);
}

static int fire_audio_write(FireAudioOutput* o, const uint8_t* data, int bytes, int* accepted, FireAudioStatus* st) {
    FireAudioLibs* l = &g_fireAudioLibs;
    size_t space, n, i;
    *accepted = 0;
    if (o->kind == 1) {
        long frames = bytes / o->frameBytes;
        long r;
        o->discard = 0;
        if (frames <= 0) return fire_audio_success(st);
        r = l->snd_pcm_writei(o->pcm, data, (unsigned long)frames);
        if (r == -11 /* EAGAIN */) return fire_audio_success(st);
        if (r < 0) {
            int rec = l->snd_pcm_recover(o->pcm, (int)r, 1);   /* an underrun is fixed here; the next write plays again */
            if (rec < 0) return fire_audio_fail(st, FIRE_AUDIO_E_OTHER, "ALSA: %s", l->snd_strerror(rec));
            r = l->snd_pcm_writei(o->pcm, data, (unsigned long)frames);
            if (r == -11) return fire_audio_success(st);
            if (r < 0) return fire_audio_fail(st, FIRE_AUDIO_E_OTHER, "ALSA: %s", l->snd_strerror((int)r));
        }
        if (l->snd_pcm_state(o->pcm) == 2 /* SND_PCM_STATE_PREPARED */) l->snd_pcm_start(o->pcm);   /* (the device would wait for a full buffer before it starts) */
        *accepted = (int)(r * o->frameBytes);
        return fire_audio_success(st);
    }
    pthread_mutex_lock(&o->mutex);
    if (o->failure[0]) {
        fire_audio_fail(st, FIRE_AUDIO_E_OTHER, "%s", o->failure);
        pthread_mutex_unlock(&o->mutex);
        return 0;
    }
    o->discard = 0;
    space = o->ringCapacity - (size_t)o->frameBytes - o->ringSize;
    n = (size_t)bytes < space ? (size_t)bytes : space;
    n -= n % (size_t)o->frameBytes;
    for (i = 0; i < n; i++) o->ring[(o->ringHead + o->ringSize + i) % o->ringCapacity] = data[i];
    o->ringSize += n;
    *accepted = (int)n;
    if (n > 0) pthread_cond_broadcast(&o->wake);
    pthread_mutex_unlock(&o->mutex);
    return fire_audio_success(st);
}

static int64_t fire_audio_queued(FireAudioOutput* o) {
    FireAudioLibs* l = &g_fireAudioLibs;
    int64_t held = 0, total;
    if (o->kind == 1) {
        long delay = 0;
        if (l->snd_pcm_delay(o->pcm, &delay) < 0 || delay < 0) return 0;   /* an underrun or a stopped device: nothing is waiting */
        return (int64_t)delay * o->frameBytes;
    }
    pthread_mutex_lock(&o->mutex);
    if (o->latencyBytes > 0) {
        double elapsed = fire_audio_seconds() - o->latencyStamp;
        held = o->latencyBytes - (int64_t)(elapsed * o->bytesPerSecond);
        if (held < 0) held = 0;
    }
    total = (int64_t)o->ringSize + (int64_t)o->inFlight + held;
    pthread_mutex_unlock(&o->mutex);
    return total;
}

static void fire_audio_stop(FireAudioOutput* o) {
    FireAudioLibs* l = &g_fireAudioLibs;
    struct timespec deadline;
    if (o->kind == 1) {
        o->discard = 1;
        l->snd_pcm_drop(o->pcm);
        l->snd_pcm_prepare(o->pcm);
        return;
    }
    clock_gettime(CLOCK_MONOTONIC, &deadline);
    deadline.tv_nsec += 500 * 1000000L;
    if (deadline.tv_nsec >= 1000000000L) { deadline.tv_sec++; deadline.tv_nsec -= 1000000000L; }
    pthread_mutex_lock(&o->mutex);
    o->discard = 1;
    o->flush = 1;
    pthread_cond_broadcast(&o->wake);
    while (o->flush) if (pthread_cond_timedwait(&o->wake, &o->mutex, &deadline) != 0) break;
    pthread_mutex_unlock(&o->mutex);
}

static void fire_audio_close(FireAudioOutput* o) {
    FireAudioLibs* l = &g_fireAudioLibs;
    if (!o) return;
    if (o->kind == 1) {
        if (o->discard) l->snd_pcm_drop(o->pcm);
        else { l->snd_pcm_nonblock(o->pcm, 0); l->snd_pcm_drain(o->pcm); }   /* plays what was accepted */
        l->snd_pcm_close(o->pcm);
    } else {
        pthread_mutex_lock(&o->mutex);
        o->quit = 1;
        pthread_cond_broadcast(&o->wake);
        pthread_mutex_unlock(&o->mutex);
        if (o->hasWorker) pthread_join(o->worker, NULL);
        l->pa_simple_free(o->pa);
        pthread_cond_destroy(&o->wake);
        pthread_mutex_destroy(&o->mutex);
        free(o->ring);
    }
    free(o);
}


/* ---- sound input ---------------------------------------------------------------------------------------------------------------------- */
/* Capture: ALSA in non-blocking mode (`read` takes what the device has), or PulseAudio's simple API, whose blocking read runs in a thread of the input that fills a ring
 * (a quarter of a second; the oldest sound is given up when nobody reads). Names as for outputs: "default", "pulse" / "pulse:<source>", "alsa" / "alsa:<pcm>". */
struct FireAudioInput {
    int kind;                   /* 1 ALSA, 2 PulseAudio */
    int frameBytes;
    int bytesPerSecond;
    void* pcm;
    void* pa;
    pthread_t worker;
    int hasWorker;
    pthread_mutex_t mutex;
    uint8_t* ring;
    size_t capacity;
    size_t head, size;
    int quit;
    char failure[176];
};

static int fire_audio_inputSupported(void) { return (fire_audio_haveAlsa() && g_fireAudioLibs.snd_pcm_readi) || (fire_audio_havePulse() && g_fireAudioLibs.pa_simple_read); }

static int fire_audio_inputDevices(FireAudioList* names, FireAudioStatus* st) {
    int a = fire_audio_haveAlsa() && g_fireAudioLibs.snd_pcm_readi, p = fire_audio_havePulse() && g_fireAudioLibs.pa_simple_read;
    fire_audio_listClear(names);
    if (a || p) fire_audio_listAdd(names, "default");
    if (p) fire_audio_listAdd(names, "pulse");
    if (a) fire_audio_listAdd(names, "alsa");
    return fire_audio_success(st);
}

static int fire_audio_alsaOpenInput(const char* pcmName, int rate, int channels, FireAudioInput** in, FireAudioStatus* st) {
    FireAudioLibs* l = &g_fireAudioLibs;
    void* pcm = NULL;
    int err;
    FireAudioInput* i;
    if (!fire_audio_haveAlsa() || !l->snd_pcm_readi) return fire_audio_fail(st, FIRE_AUDIO_E_UNSUPPORTED, "ALSA is not installed (libasound.so.2 was not found).");
    err = l->snd_pcm_open(&pcm, pcmName, 1 /* SND_PCM_STREAM_CAPTURE */, 1 /* SND_PCM_NONBLOCK */);
    if (err < 0) {
        int code = (err == -2 || err == -19) ? FIRE_AUDIO_E_NOT_FOUND : (err == -16) ? FIRE_AUDIO_E_BUSY : (err == -13 || err == -1) ? FIRE_AUDIO_E_PERMISSION : FIRE_AUDIO_E_OTHER;
        return fire_audio_fail(st, code, "ALSA '%s': %s", pcmName, l->snd_strerror(err));
    }
    err = l->snd_pcm_set_params(pcm, 2 /* SND_PCM_FORMAT_S16_LE */, 3 /* SND_PCM_ACCESS_RW_INTERLEAVED */, (unsigned)channels, (unsigned)rate, 1, 100000);
    if (err < 0) {
        l->snd_pcm_close(pcm);
        return fire_audio_fail(st, err == -22 ? FIRE_AUDIO_E_UNSUPPORTED : FIRE_AUDIO_E_OTHER, "ALSA '%s' cannot record %d Hz, %d channel(s): %s", pcmName, rate, channels, l->snd_strerror(err));
    }
    l->snd_pcm_start(pcm);
    i = (FireAudioInput*)calloc(1, sizeof(FireAudioInput));
    if (i == NULL) { l->snd_pcm_close(pcm); return fire_audio_fail(st, FIRE_AUDIO_E_OTHER, "Out of memory."); }
    i->kind = 1;
    i->pcm = pcm;
    i->frameBytes = 2 * channels;
    i->bytesPerSecond = i->frameBytes * rate;
    *in = i;
    return fire_audio_success(st);
}

static void* fire_audio_pulseInputThread(void* arg) {
    FireAudioInput* in = (FireAudioInput*)arg;
    FireAudioLibs* l = &g_fireAudioLibs;
    size_t chunkBytes = (size_t)(in->bytesPerSecond / 100);   /* 10 ms */
    uint8_t* chunk;
    chunkBytes -= chunkBytes % (size_t)in->frameBytes;
    if (chunkBytes < (size_t)in->frameBytes) chunkBytes = (size_t)in->frameBytes;
    chunk = (uint8_t*)calloc(1, chunkBytes);
    if (chunk == NULL) {
        pthread_mutex_lock(&in->mutex);
        snprintf(in->failure, sizeof in->failure, "%s", "PulseAudio: out of memory");
        pthread_mutex_unlock(&in->mutex);
        return NULL;
    }
    for (;;) {
        int e = 0, r, quit;
        size_t k;
        pthread_mutex_lock(&in->mutex);
        quit = in->quit;
        pthread_mutex_unlock(&in->mutex);
        if (quit) break;
        r = l->pa_simple_read(in->pa, chunk, chunkBytes, &e);
        pthread_mutex_lock(&in->mutex);
        if (r < 0) {
            char text[128];
            fire_audio_paError(e, text, sizeof text);
            snprintf(in->failure, sizeof in->failure, "PulseAudio: %s", text);
            pthread_mutex_unlock(&in->mutex);
            break;
        }
        for (k = 0; k < chunkBytes; k++) {
            if (in->size == in->capacity) { in->head = (in->head + 1) % in->capacity; in->size--; }
            in->ring[(in->head + in->size) % in->capacity] = chunk[k];
            in->size++;
        }
        pthread_mutex_unlock(&in->mutex);
    }
    free(chunk);
    return NULL;
}

static int fire_audio_pulseOpenInput(const char* source, int rate, int channels, FireAudioInput** in, FireAudioStatus* st) {
    FireAudioLibs* l = &g_fireAudioLibs;
    FireAudioPaSampleSpec spec;
    FireAudioPaBufferAttr attr;
    int e = 0;
    void* pa;
    FireAudioInput* i;
    if (!fire_audio_havePulse() || !l->pa_simple_read) return fire_audio_fail(st, FIRE_AUDIO_E_UNSUPPORTED, "PulseAudio is not installed (libpulse-simple.so.0 was not found).");
    spec.format = 3;   /* PA_SAMPLE_S16LE */
    spec.rate = (uint32_t)rate;
    spec.channels = (uint8_t)channels;
    /* fragments of 10 ms: the sound comes in small pieces (the default would hand it over in large ones, late) */
    attr.maxlength = (uint32_t)-1;
    attr.tlength = (uint32_t)-1;
    attr.prebuf = (uint32_t)-1;
    attr.minreq = (uint32_t)-1;
    attr.fragsize = (uint32_t)(rate * channels * 2 / 100);
    pa = l->pa_simple_new(NULL, "fire", 2 /* PA_STREAM_RECORD */, source[0] == 0 ? NULL : source, "fire input", &spec, NULL, &attr, &e);
    if (!pa) {
        char text[128];
        fire_audio_paError(e, text, sizeof text);
        return fire_audio_fail(st, e == 1 ? FIRE_AUDIO_E_PERMISSION : (e == 5 || e == 6) ? FIRE_AUDIO_E_NOT_FOUND : FIRE_AUDIO_E_OTHER, "PulseAudio: %s", text);
    }
    i = (FireAudioInput*)calloc(1, sizeof(FireAudioInput));
    if (i != NULL) {
        i->kind = 2;
        i->pa = pa;
        i->frameBytes = 2 * channels;
        i->bytesPerSecond = i->frameBytes * rate;
        i->capacity = (size_t)(i->bytesPerSecond / 4) - (size_t)(i->bytesPerSecond / 4) % (size_t)i->frameBytes + (size_t)i->frameBytes;   /* a quarter of a second */
        i->ring = (uint8_t*)malloc(i->capacity);
    }
    if (i == NULL || i->ring == NULL) {
        if (i) free(i);
        l->pa_simple_free(pa);
        return fire_audio_fail(st, FIRE_AUDIO_E_OTHER, "Out of memory.");
    }
    pthread_mutex_init(&i->mutex, NULL);
    if (pthread_create(&i->worker, NULL, fire_audio_pulseInputThread, i) != 0) {
        pthread_mutex_destroy(&i->mutex);
        free(i->ring);
        free(i);
        l->pa_simple_free(pa);
        return fire_audio_fail(st, FIRE_AUDIO_E_OTHER, "PulseAudio: the thread of the input could not be started.");
    }
    i->hasWorker = 1;
    *in = i;
    return fire_audio_success(st);
}

static int fire_audio_openInput(const char* name, int rate, int channels, FireAudioInput** in, FireAudioStatus* st) {
    *in = NULL;
    if (strcmp(name, "default") == 0) {
        if (fire_audio_havePulse() && g_fireAudioLibs.pa_simple_read) {
            if (fire_audio_pulseOpenInput("", rate, channels, in, st)) return 1;
        }
        return fire_audio_alsaOpenInput("default", rate, channels, in, st);
    }
    if (strcmp(name, "pulse") == 0 || strncmp(name, "pulse:", 6) == 0) return fire_audio_pulseOpenInput(strcmp(name, "pulse") == 0 ? "" : name + 6, rate, channels, in, st);
    if (strcmp(name, "alsa") == 0) return fire_audio_alsaOpenInput("default", rate, channels, in, st);
    if (strncmp(name, "alsa:", 5) == 0) return fire_audio_alsaOpenInput(name + 5, rate, channels, in, st);
    return fire_audio_fail(st, FIRE_AUDIO_E_NOT_FOUND, "There is no sound input '%s' (\"default\", \"pulse\", \"alsa\", \"alsa:<pcm>\", \"pulse:<source>\").", name);
}

static int fire_audio_read(FireAudioInput* in, uint8_t* data, int bytes, int* got, FireAudioStatus* st) {
    FireAudioLibs* l = &g_fireAudioLibs;
    size_t n, k;
    *got = 0;
    if (in->kind == 1) {
        long frames = bytes / in->frameBytes;
        long r;
        if (frames <= 0) return fire_audio_success(st);
        r = l->snd_pcm_readi(in->pcm, data, (unsigned long)frames);
        if (r == -11 /* EAGAIN */) return fire_audio_success(st);
        if (r < 0) {
            int rec = l->snd_pcm_recover(in->pcm, (int)r, 1);   /* an overrun: the oldest sound is gone, capture goes on */
            if (rec < 0) return fire_audio_fail(st, FIRE_AUDIO_E_OTHER, "ALSA: %s", l->snd_strerror(rec));
            l->snd_pcm_start(in->pcm);
            return fire_audio_success(st);
        }
        *got = (int)(r * in->frameBytes);
        return fire_audio_success(st);
    }
    pthread_mutex_lock(&in->mutex);
    if (in->failure[0]) {
        fire_audio_fail(st, FIRE_AUDIO_E_OTHER, "%s", in->failure);
        pthread_mutex_unlock(&in->mutex);
        return 0;
    }
    n = in->size < (size_t)bytes ? in->size : (size_t)bytes;
    n -= n % (size_t)in->frameBytes;
    for (k = 0; k < n; k++) data[k] = in->ring[(in->head + k) % in->capacity];
    in->head = (in->head + n) % in->capacity;
    in->size -= n;
    *got = (int)n;
    pthread_mutex_unlock(&in->mutex);
    return fire_audio_success(st);
}

static int64_t fire_audio_available(FireAudioInput* in) {
    int64_t size;
    if (in->kind == 1) {
        long frames = g_fireAudioLibs.snd_pcm_avail ? g_fireAudioLibs.snd_pcm_avail(in->pcm) : 0;
        return frames > 0 ? (int64_t)frames * in->frameBytes : 0;
    }
    pthread_mutex_lock(&in->mutex);
    size = (int64_t)in->size;
    pthread_mutex_unlock(&in->mutex);
    return size;
}

static void fire_audio_closeInput(FireAudioInput* in) {
    FireAudioLibs* l = &g_fireAudioLibs;
    if (!in) return;
    if (in->kind == 1) l->snd_pcm_close(in->pcm);
    else {
        pthread_mutex_lock(&in->mutex);
        in->quit = 1;
        pthread_mutex_unlock(&in->mutex);
        if (in->hasWorker) pthread_join(in->worker, NULL);   /* (its read returns within 10 ms) */
        l->pa_simple_free(in->pa);
        pthread_mutex_destroy(&in->mutex);
        free(in->ring);
    }
    free(in);
}

#endif
