/* fire audio package: audio part in C on macOS: Audio Queue Services of AudioToolbox (a C interface - no SDL, no Objective-C). The interface is described in
 * fire_audio_c.h; the program links the framework AudioToolbox (`framework:AudioToolbox` in the audio package's link libraries for macOS).
 *
 * An output is an audio queue with three buffers of about 20 ms that its own thread (the queue's) fills from a ring of half a second: `write` puts into the ring and
 * never waits, `queued` is what the ring and the buffers on their way still hold. An input is an audio queue that records into buffers its thread copies into a ring
 * of a second; `read` takes from it (the oldest sound is lost when nobody reads). The samples are 16 bit signed, interleaved; the queue converts for the device.
 * The device is the system's default ("default"); the types of AudioToolbox are declared here (no headers of Apple). */
#ifndef FIRE_AUDIO_COREAUDIO_H
#define FIRE_AUDIO_COREAUDIO_H
#if !defined(FIRE_AUDIO_DESKTOP_H) || defined(FIRE_AUDIO_USE_COREAUDIO)   /* (with fire_audio_desktop.h in the build: when it chose this one) */

#include <pthread.h>
#include <time.h>
#include "fire_audio_c.h"

typedef struct FireCaFormat { double sampleRate; uint32_t formatId, formatFlags, bytesPerPacket, framesPerPacket, bytesPerFrame, channelsPerFrame, bitsPerChannel, reserved; } FireCaFormat;
typedef struct FireCaBuffer { const uint32_t capacity; void* const data; uint32_t size; void* user; const uint32_t packetCapacity; void* const packets; uint32_t packetCount; } FireCaBuffer;
typedef void* FireCaQueue;
extern int32_t AudioQueueNewOutput(const FireCaFormat*, void (*)(void*, FireCaQueue, FireCaBuffer*), void*, void*, void*, uint32_t, FireCaQueue*);
extern int32_t AudioQueueNewInput(const FireCaFormat*, void (*)(void*, FireCaQueue, FireCaBuffer*, const void*, uint32_t, const void*), void*, void*, void*, uint32_t, FireCaQueue*);
extern int32_t AudioQueueAllocateBuffer(FireCaQueue, uint32_t, FireCaBuffer**);
extern int32_t AudioQueueEnqueueBuffer(FireCaQueue, FireCaBuffer*, uint32_t, const void*);
extern int32_t AudioQueueStart(FireCaQueue, const void*);
extern int32_t AudioQueueStop(FireCaQueue, unsigned char);
extern int32_t AudioQueueDispose(FireCaQueue, unsigned char);

/* ---- a ring of bytes (between the program and the queue's thread) ---- */
typedef struct FireCaRing { uint8_t* data; size_t cap, head, fill; pthread_mutex_t m; } FireCaRing;
static int fire_ca_ringInit(FireCaRing* r, size_t cap) { r->data = (uint8_t*)malloc(cap); r->cap = cap; r->head = r->fill = 0; pthread_mutex_init(&r->m, NULL); return r->data != NULL; }
static void fire_ca_ringFree(FireCaRing* r) { free(r->data); r->data = NULL; pthread_mutex_destroy(&r->m); }
/* puts at most `n` bytes (`drop`: makes room by losing the oldest); returns how many */
static size_t fire_ca_ringPut(FireCaRing* r, const uint8_t* src, size_t n, int drop) {
    size_t k;
    pthread_mutex_lock(&r->m);
    if (drop && n > r->cap - r->fill) { size_t lose = n - (r->cap - r->fill); if (lose > r->fill) lose = r->fill; r->head = (r->head + lose) % r->cap; r->fill -= lose; }
    if (n > r->cap - r->fill) n = r->cap - r->fill;
    for (k = 0; k < n; k++) r->data[(r->head + r->fill + k) % r->cap] = src[k];
    r->fill += n;
    pthread_mutex_unlock(&r->m);
    return n;
}
static size_t fire_ca_ringTake(FireCaRing* r, uint8_t* dst, size_t n) {
    size_t k;
    pthread_mutex_lock(&r->m);
    if (n > r->fill) n = r->fill;
    for (k = 0; k < n; k++) dst[k] = r->data[(r->head + k) % r->cap];
    r->head = (r->head + n) % r->cap;
    r->fill -= n;
    pthread_mutex_unlock(&r->m);
    return n;
}
static size_t fire_ca_ringFill(FireCaRing* r) { size_t n; pthread_mutex_lock(&r->m); n = r->fill; pthread_mutex_unlock(&r->m); return n; }
static void fire_ca_ringClear(FireCaRing* r) { pthread_mutex_lock(&r->m); r->head = r->fill = 0; pthread_mutex_unlock(&r->m); }

static void fire_ca_sleepMs(int ms) { struct timespec t; t.tv_sec = ms / 1000; t.tv_nsec = (long)(ms % 1000) * 1000000L; nanosleep(&t, NULL); }
static FireCaFormat fire_ca_format(int rate, int channels) {
    FireCaFormat f;
    memset(&f, 0, sizeof f);
    f.sampleRate = rate;
    f.formatId = 0x6C70636Du;   /* 'lpcm' */
    f.formatFlags = 4u | 8u;    /* signed integer, packed (little endian) */
    f.bytesPerPacket = f.bytesPerFrame = (uint32_t)(2 * channels);
    f.framesPerPacket = 1;
    f.channelsPerFrame = (uint32_t)channels;
    f.bitsPerChannel = 16;
    return f;
}

/* ---- output ---- */
struct FireAudioOutput {
    FireCaQueue queue;
    FireCaRing ring;
    FireCaBuffer* buffers[3];
    int frameBytes, bufferBytes, discard;
    volatile long inFlight;   /* the bytes in the buffers that are on their way */
};
static void fire_ca_fill(void* user, FireCaQueue q, FireCaBuffer* b) {
    FireAudioOutput* o = (FireAudioOutput*)user;
    size_t got;
    __sync_fetch_and_sub(&o->inFlight, (long)(uintptr_t)b->user);   /* (what this buffer carried is played now) */
    got = fire_ca_ringTake(&o->ring, (uint8_t*)b->data, (size_t)o->bufferBytes);
    if (got < (size_t)o->bufferBytes) memset((uint8_t*)b->data + got, 0, (size_t)o->bufferBytes - got);   /* (silence while nothing is there) */
    b->size = (uint32_t)o->bufferBytes;
    __sync_fetch_and_add(&o->inFlight, (long)got);
    b->user = (void*)(uintptr_t)got;
    AudioQueueEnqueueBuffer(q, b, 0, NULL);
}

static int fire_audio_supported(void) { return 1; }
static int fire_audio_devices(FireAudioList* names, FireAudioStatus* st) { fire_audio_listClear(names); fire_audio_listAdd(names, "default"); return fire_audio_success(st); }
static int fire_audio_open(const char* name, int rate, int channels, FireAudioOutput** out, FireAudioStatus* st) {
    FireCaFormat f = fire_ca_format(rate, channels);
    FireAudioOutput* o;
    int k;
    *out = NULL;
    if (strcmp(name, "default") != 0 && strcmp(name, "0") != 0) return fire_audio_fail(st, FIRE_AUDIO_E_NOT_FOUND, "There is no sound device '%s' (\"default\").", name);
    o = (FireAudioOutput*)calloc(1, sizeof *o);
    if (o == NULL) return fire_audio_fail(st, FIRE_AUDIO_E_OTHER, "Out of memory.");
    o->frameBytes = 2 * channels;
    o->bufferBytes = (rate / 50) * o->frameBytes;   /* 20 ms */
    if (!fire_ca_ringInit(&o->ring, (size_t)(rate / 2) * (size_t)o->frameBytes)) { free(o); return fire_audio_fail(st, FIRE_AUDIO_E_OTHER, "Out of memory."); }
    if (AudioQueueNewOutput(&f, fire_ca_fill, o, NULL, NULL, 0, &o->queue) != 0) { fire_ca_ringFree(&o->ring); free(o); return fire_audio_fail(st, FIRE_AUDIO_E_UNSUPPORTED, "Audio queue: the output could not be opened."); }
    for (k = 0; k < 3; k++) {
        if (AudioQueueAllocateBuffer(o->queue, (uint32_t)o->bufferBytes, &o->buffers[k]) != 0) continue;
        memset(o->buffers[k]->data, 0, (size_t)o->bufferBytes);
        o->buffers[k]->size = (uint32_t)o->bufferBytes;
        AudioQueueEnqueueBuffer(o->queue, o->buffers[k], 0, NULL);
    }
    AudioQueueStart(o->queue, NULL);
    *out = o;
    return fire_audio_success(st);
}
static int fire_audio_write(FireAudioOutput* o, const uint8_t* data, int bytes, int* accepted, FireAudioStatus* st) {
    size_t n = (size_t)bytes - (size_t)bytes % (size_t)o->frameBytes, room;
    o->discard = 0;
    pthread_mutex_lock(&o->ring.m);
    room = o->ring.cap - o->ring.fill;
    pthread_mutex_unlock(&o->ring.m);
    if (n > room) n = room - room % (size_t)o->frameBytes;
    *accepted = (int)fire_ca_ringPut(&o->ring, data, n, 0);
    return fire_audio_success(st);
}
static int64_t fire_audio_queued(FireAudioOutput* o) { long f = o->inFlight; return (int64_t)fire_ca_ringFill(&o->ring) + (f > 0 ? f : 0); }
static void fire_audio_stop(FireAudioOutput* o) { o->discard = 1; fire_ca_ringClear(&o->ring); }
static void fire_audio_close(FireAudioOutput* o) {
    if (!o) return;
    if (!o->discard) { int waited; for (waited = 0; waited < 3000 && fire_audio_queued(o) > 0; waited += 5) fire_ca_sleepMs(5); }
    AudioQueueStop(o->queue, 1);
    AudioQueueDispose(o->queue, 1);
    fire_ca_ringFree(&o->ring);
    free(o);
}

/* ---- input ---- */
struct FireAudioInput {
    FireCaQueue queue;
    FireCaRing ring;
    int frameBytes;
};
static void fire_ca_record(void* user, FireCaQueue q, FireCaBuffer* b, const void* time, uint32_t packets, const void* descriptions) {
    FireAudioInput* in = (FireAudioInput*)user;
    (void)time; (void)packets; (void)descriptions;
    fire_ca_ringPut(&in->ring, (const uint8_t*)b->data, b->size, 1);
    AudioQueueEnqueueBuffer(q, b, 0, NULL);
}
static int fire_audio_inputSupported(void) { return 1; }
static int fire_audio_inputDevices(FireAudioList* names, FireAudioStatus* st) { fire_audio_listClear(names); fire_audio_listAdd(names, "default"); return fire_audio_success(st); }
static int fire_audio_openInput(const char* name, int rate, int channels, FireAudioInput** inOut, FireAudioStatus* st) {
    FireCaFormat f = fire_ca_format(rate, channels);
    FireAudioInput* in;
    int k, size;
    *inOut = NULL;
    if (strcmp(name, "default") != 0 && strcmp(name, "0") != 0) return fire_audio_fail(st, FIRE_AUDIO_E_NOT_FOUND, "There is no sound input '%s' (\"default\").", name);
    in = (FireAudioInput*)calloc(1, sizeof *in);
    if (in == NULL) return fire_audio_fail(st, FIRE_AUDIO_E_OTHER, "Out of memory.");
    in->frameBytes = 2 * channels;
    if (!fire_ca_ringInit(&in->ring, (size_t)rate * (size_t)in->frameBytes)) { free(in); return fire_audio_fail(st, FIRE_AUDIO_E_OTHER, "Out of memory."); }
    if (AudioQueueNewInput(&f, fire_ca_record, in, NULL, NULL, 0, &in->queue) != 0) {
        fire_ca_ringFree(&in->ring); free(in);
        return fire_audio_fail(st, FIRE_AUDIO_E_PERMISSION, "Audio queue: the input could not be opened (is the microphone allowed for this program?).");
    }
    size = (rate / 50) * in->frameBytes;
    for (k = 0; k < 3; k++) { FireCaBuffer* b; if (AudioQueueAllocateBuffer(in->queue, (uint32_t)size, &b) == 0) AudioQueueEnqueueBuffer(in->queue, b, 0, NULL); }
    AudioQueueStart(in->queue, NULL);
    *inOut = in;
    return fire_audio_success(st);
}
static int fire_audio_read(FireAudioInput* in, uint8_t* data, int bytes, int* got, FireAudioStatus* st) {
    size_t n = (size_t)bytes - (size_t)bytes % (size_t)in->frameBytes, have = fire_ca_ringFill(&in->ring);
    if (n > have) n = have - have % (size_t)in->frameBytes;
    *got = (int)fire_ca_ringTake(&in->ring, data, n);
    return fire_audio_success(st);
}
static int64_t fire_audio_available(FireAudioInput* in) { return (int64_t)fire_ca_ringFill(&in->ring); }
static void fire_audio_closeInput(FireAudioInput* in) {
    if (!in) return;
    AudioQueueStop(in->queue, 1);
    AudioQueueDispose(in->queue, 1);
    fire_ca_ringFree(&in->ring);
    free(in);
}

#endif
#endif
