/* fire native bridge "audio" (C): the natives behind `#import "audio"` (docs/audio/README.md): sound output - 16 bit signed samples, mono or stereo, any rate the device
 * takes. The fire side (Audio.Output, Audio.Sound, Audio.Sim - fire source) is the same in the VM and in a native build; this file is what its `__Audio...` functions do.
 *
 * This file is the one implementation of the audio natives, in C99 ("language": "c"): the native build puts it into the generated program, and the virtual machine
 * runs it in a shared library built from it (native/abi/fire_pkg_abi.h). The devices come with the package (fire_audio_*; one file per platform, platformSources of audio.fireproj, see
 * fire_audio_c.h): PulseAudio or ALSA on Linux, winmm on Windows, PWM on a GPIO pin of an ESP32. Where there is no sound hardware the list of devices is
 * empty - but one device is always there:
 *
 *   "sim"   a simulated device that records what is played to it (the bytes, the rate and the channels of the last opened output): for tests, and to try a program on
 *           a machine without sound. It plays at once - unless the program holds it (`SimHold`): then it takes up to 4096 bytes and keeps them, as a device does that
 *           is slow to play.
 *
 * An output is an integer handle in a table of this bridge (handles are not used again). Every function reports an error the same way: a result of -1/false/undefined
 * and the code and message of this thread (`__AudioLastError`, `__AudioLastErrorMessage`, the slot of the io bridge), from which the fire code throws a typed exception.
 * Nothing in here waits: `Write` takes what fits and returns the number of bytes taken, `Queued` says what is still to be played, and the fire code sleeps in between -
 * so a program can always be aborted. */
#ifndef FIRE_BRIDGE_AUDIO_H
#define FIRE_BRIDGE_AUDIO_H

#include <math.h>
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "fire_audio_c.h"

enum { FIRE_AUDIOB_NONE = 0, FIRE_AUDIOB_INVALID_ARGUMENT = 1, FIRE_AUDIOB_INVALID_HANDLE = 2, FIRE_AUDIOB_NOT_FOUND = 3, FIRE_AUDIOB_BUSY = 4,
       FIRE_AUDIOB_PERMISSION = 5, FIRE_AUDIOB_UNSUPPORTED = 6, FIRE_AUDIOB_OTHER = 7 };

static int64_t fire_audiob_fail(int code, const char* message) {
    g_ioError.code = code;
    snprintf(g_ioError.message, sizeof g_ioError.message, "%s", message);
    return -1;
}
static int64_t fire_audiob_failStatus(const FireAudioStatus* s) { return fire_audiob_fail(s->code, s->message); }
static void fire_audiob_ok(void) { g_ioError.code = 0; g_ioError.message[0] = 0; }

/* ---- text ------------------------------------------------------------------------------------------------------------------------------- */
/* A fire string as UTF-8 (malloc; the caller frees it; NULL when out of memory); a lone surrogate becomes U+FFFD. */
static char* fire_audiob_utf8(Value v) {
    const Str* s;
    char* out;
    size_t n = 0;
    uint32_t i;
    if (v.kind != K_String) { out = (char*)malloc(1); if (out) out[0] = 0; return out; }
    s = strOf(v);
    out = (char*)malloc((size_t)s->length * 3 + 1);
    if (out == NULL) return NULL;
    for (i = 0; i < s->length; i++) {
        uint32_t c = s->data[i];
        if (c >= 0xD800 && c <= 0xDBFF && i + 1 < s->length && s->data[i + 1] >= 0xDC00 && s->data[i + 1] <= 0xDFFF) { c = 0x10000 + ((c - 0xD800) << 10) + (s->data[i + 1] - 0xDC00); i++; }
        else if (c >= 0xD800 && c <= 0xDFFF) c = 0xFFFD;
        if (c < 0x80) out[n++] = (char)c;
        else if (c < 0x800) { out[n++] = (char)(0xC0 | (c >> 6)); out[n++] = (char)(0x80 | (c & 0x3F)); }
        else if (c < 0x10000) { out[n++] = (char)(0xE0 | (c >> 12)); out[n++] = (char)(0x80 | ((c >> 6) & 0x3F)); out[n++] = (char)(0x80 | (c & 0x3F)); }
        else { out[n++] = (char)(0xF0 | (c >> 18)); out[n++] = (char)(0x80 | ((c >> 12) & 0x3F)); out[n++] = (char)(0x80 | ((c >> 6) & 0x3F)); out[n++] = (char)(0x80 | (c & 0x3F)); }
    }
    out[n] = 0;
    return out;
}
/* (device names and messages are ASCII or UTF-8 from the system; a byte above 127 becomes the Latin-1 character of the same number) */
static Value fire_audiob_str8(const char* text, OwnList* list) {
    size_t n = strlen(text), i;
    Str* s = allocStr((uint32_t)n, list);
    for (i = 0; i < n; i++) strChars(s)[i] = (fire_char16)(uint8_t)text[i];
    return StrV(s);
}

/* ---- bytes that grow -------------------------------------------------------------------------------------------------------------------- */
typedef struct FireAudiobBytes { uint8_t* data; size_t size, cap; } FireAudiobBytes;
/* Appends `n` bytes: 1, 0 when the memory is gone (nothing is appended then). */
static int fire_audiob_append(FireAudiobBytes* b, const uint8_t* bytes, size_t n) {
    if (n == 0) return 1;
    if (b->size + n > b->cap) {
        size_t cap = b->cap ? b->cap : 256;
        uint8_t* grown;
        while (cap < b->size + n) cap *= 2;
        grown = (uint8_t*)realloc(b->data, cap);
        if (grown == NULL) return 0;
        b->data = grown;
        b->cap = cap;
    }
    memcpy(b->data + b->size, bytes, n);
    b->size += n;
    return 1;
}
static void fire_audiob_bytesFree(FireAudiobBytes* b) { free(b->data); b->data = NULL; b->size = b->cap = 0; }

/* ---- the simulated device --------------------------------------------------------------------------------------------------------------- */
enum { FIRE_AUDIOB_SIM_HOLD_CAPACITY = 4096, FIRE_AUDIOB_SIM_RECORD_LIMIT = 16 * 1024 * 1024 };
typedef struct FireAudiobSim {
    int hold;
    int rate;
    int channels;
    int64_t played;             /* the bytes that were played (also those beyond the recording limit) */
    FireAudiobBytes data;       /* what was played (up to FIRE_AUDIOB_SIM_RECORD_LIMIT) */
    FireAudiobBytes pending;    /* what waits while the device is held */
    FireAudiobBytes input;      /* what the simulated input records next (Feed) */
    size_t inputAt;
    int64_t recorded;           /* the bytes the simulated input handed over */
} FireAudiobSim;
static FireAudiobSim g_audioSim;

static void fire_audiob_simPlay(const uint8_t* bytes, size_t n) {
    FireAudiobSim* s = &g_audioSim;
    size_t room = s->data.size < (size_t)FIRE_AUDIOB_SIM_RECORD_LIMIT ? (size_t)FIRE_AUDIOB_SIM_RECORD_LIMIT - s->data.size : 0;
    s->played += (int64_t)n;
    fire_audiob_append(&s->data, bytes, n < room ? n : room);
}
static void fire_audiob_simRelease(void) {
    FireAudiobSim* s = &g_audioSim;
    if (s->pending.size > 0) fire_audiob_simPlay(s->pending.data, s->pending.size);
    s->pending.size = 0;
}
/* Forgets everything (the device as it is at the start). */
static void fire_audiob_simClear(void) {
    fire_audiob_bytesFree(&g_audioSim.data);
    fire_audiob_bytesFree(&g_audioSim.pending);
    fire_audiob_bytesFree(&g_audioSim.input);
    memset(&g_audioSim, 0, sizeof g_audioSim);
}

/* ---- the outputs ------------------------------------------------------------------------------------------------------------------------ */
typedef struct FireAudiobOutput {
    int isSim;
    FireAudioOutput* out;
    int rate;
    int channels;
    int frameBytes;
    int volume;                 /* percent */
    FireAudiobBytes scaled;     /* (the samples with the volume applied) */
} FireAudiobOutput;
static void fire_audiob_releaseOutput(FireAudiobOutput* o) {
    if (o == NULL) return;
    if (o->out) fire_audio_close(o->out);
    fire_audiob_bytesFree(&o->scaled);
    free(o);
}

/* ---- the inputs ------------------------------------------------------------------------------------------------------------------------- */
typedef struct FireAudiobInput {
    int isSim;
    FireAudioInput* in;
    int rate;
    int channels;
    int frameBytes;
} FireAudiobInput;
static void fire_audiob_releaseInput(FireAudiobInput* i) {
    if (i == NULL) return;
    if (i->in) fire_audio_closeInput(i->in);
    free(i);
}

/* The open outputs and inputs; the handle is the index (0 is not used). */
static FireAudiobOutput** g_audioOutputs = NULL;
static size_t g_audioOutputCount = 1, g_audioOutputCap = 0;
static FireAudiobInput** g_audioInputs = NULL;
static size_t g_audioInputCount = 1, g_audioInputCap = 0;

/* Makes room for one more output / input in its table: 1, 0 when the memory is gone. */
static int fire_audiob_roomForOutput(void) {
    if (g_audioOutputCount >= g_audioOutputCap) {
        size_t cap = g_audioOutputCap ? g_audioOutputCap * 2 : 16;
        FireAudiobOutput** grown = (FireAudiobOutput**)realloc(g_audioOutputs, sizeof(FireAudiobOutput*) * cap);
        if (grown == NULL) return 0;
        if (g_audioOutputCap == 0) grown[0] = NULL;
        g_audioOutputs = grown;
        g_audioOutputCap = cap;
    }
    return 1;
}
static int fire_audiob_roomForInput(void) {
    if (g_audioInputCount >= g_audioInputCap) {
        size_t cap = g_audioInputCap ? g_audioInputCap * 2 : 16;
        FireAudiobInput** grown = (FireAudiobInput**)realloc(g_audioInputs, sizeof(FireAudiobInput*) * cap);
        if (grown == NULL) return 0;
        if (g_audioInputCap == 0) grown[0] = NULL;
        g_audioInputs = grown;
        g_audioInputCap = cap;
    }
    return 1;
}

/* The program has ended (the library for the VM stays loaded for the next one): closes the outputs and inputs and clears the simulated device. */
static void fire_audio_reset(void) {
    size_t i;
    for (i = 1; i < g_audioOutputCount; i++) {
        fire_audiob_releaseOutput(g_audioOutputs[i]);
        g_audioOutputs[i] = NULL;
    }
    for (i = 1; i < g_audioInputCount; i++) {
        fire_audiob_releaseInput(g_audioInputs[i]);
        g_audioInputs[i] = NULL;
    }
    fire_audiob_simClear();
}

static FireAudiobOutput* fire_audiob_find(Value h) {
    int64_t i = h.i;
    if (i > 0 && (size_t)i < g_audioOutputCount && g_audioOutputs[i] != NULL) return g_audioOutputs[i];
    fire_audiob_fail(FIRE_AUDIOB_INVALID_HANDLE, "Invalid or already closed output handle.");
    return NULL;
}
static int fire_audiob_checkRange(int64_t length, int64_t offset, int64_t count) {
    if (offset < 0 || count < 0 || offset > length || count > length - offset) {
        char text[176];
        snprintf(text, sizeof text, "offset/count (%lld/%lld) are outside of the buffer (length %lld).", (long long)offset, (long long)count, (long long)length);
        fire_audiob_fail(FIRE_AUDIOB_INVALID_ARGUMENT, text);
        return 0;
    }
    return 1;
}
/* A list of names as a fire array: "sim" first, then `hardware`. */
static Value fire_audiob_names(const FireAudioList* hardware, OwnList* list) {
    Arr* a = allocArr((uint32_t)(hardware->count + 1), list);
    size_t i;
    arrItems(a)[0] = fire_audiob_str8("sim", list);
    retain(arrItems(a)[0]);
    for (i = 0; i < hardware->count; i++) { arrItems(a)[i + 1] = fire_audiob_str8(hardware->items[i], list); retain(arrItems(a)[i + 1]); }
    return ArrV(a);
}

/* ---- natives ---------------------------------------------------------------------------------------------------------------------------- */
static Value fire_audio_LastError(void) { return Int(g_ioError.code); }
static Value fire_audio_LastErrorMessage(OwnList* list) { return fire_audiob_str8(g_ioError.message, list); }
static Value fire_audio_OpenCount(void) {
    int64_t n = 0;
    size_t i;
    for (i = 1; i < g_audioOutputCount; i++) if (g_audioOutputs[i]) n++;
    return Int(n);
}
/* 1 if this machine has a sound output (the simulated device is there anyway), else 0. */
static Value fire_audio_Supported(void) {
    FireAudioList hardware;
    FireAudioStatus st;
    int yes;
    memset(&hardware, 0, sizeof hardware);
    fire_audiob_ok();
    yes = fire_audio_supported() && fire_audio_devices(&hardware, &st) && hardware.count > 0;
    fire_audio_listFree(&hardware);
    return Int(yes ? 1 : 0);
}

/* The devices: "sim" first, then those of the hardware. */
static Value fire_audio_Devices(OwnList* list) {
    FireAudioList hardware;
    FireAudioStatus st;
    Value r;
    memset(&hardware, 0, sizeof hardware);
    if (!fire_audio_devices(&hardware, &st)) { fire_audio_listFree(&hardware); fire_audiob_failStatus(&st); return Undef(); }
    fire_audiob_ok();
    r = fire_audiob_names(&hardware, list);
    fire_audio_listFree(&hardware);
    return r;
}

static Value fire_audio_Open(Value name, Value rate, Value channels) {
    char* n;
    FireAudiobOutput* o;
    if (rate.i < 1000 || rate.i > 192000) return Int(fire_audiob_fail(FIRE_AUDIOB_INVALID_ARGUMENT, "The rate must be between 1000 and 192000 Hz."));
    if (channels.i < 1 || channels.i > 2) return Int(fire_audiob_fail(FIRE_AUDIOB_INVALID_ARGUMENT, "Mono (1) or stereo (2) channels."));
    n = fire_audiob_utf8(name);
    if (n == NULL) return Int(fire_audiob_fail(FIRE_AUDIOB_OTHER, "Out of memory."));
    o = (FireAudiobOutput*)calloc(1, sizeof(FireAudiobOutput));
    if (o == NULL || !fire_audiob_roomForOutput()) {
        free(o);
        free(n);
        return Int(fire_audiob_fail(FIRE_AUDIOB_OTHER, "Out of memory."));
    }
    o->rate = (int)rate.i;
    o->channels = (int)channels.i;
    o->frameBytes = 2 * o->channels;
    o->volume = 100;
    if (strcmp(n, "sim") == 0) {
        o->isSim = 1;
        g_audioSim.rate = o->rate;
        g_audioSim.channels = o->channels;
    } else {
        FireAudioStatus st;
        if (!fire_audio_open(n, o->rate, o->channels, &o->out, &st)) { free(o); free(n); return Int(fire_audiob_failStatus(&st)); }
    }
    free(n);
    g_audioOutputs[g_audioOutputCount] = o;
    fire_audiob_ok();
    return Int((int64_t)g_audioOutputCount++);
}

/* Plays `count` bytes (16 bit samples, whole frames) of the buffer: takes what fits and returns the number of bytes taken (0: full, try again later), -1 on an error. */
static Value fire_audio_Write(Value h, Value buffer, Value offset, Value count) {
    FireAudiobOutput* o;
    const uint8_t* bytes;
    int64_t taken = 0;
    FireBytes in;
    if (!bytesArg(buffer, "Audio.Output.Write", &in)) return Undef();
    o = fire_audiob_find(h);
    if (o == NULL) return Int(-1);
    if (!fire_audiob_checkRange(in.length, offset.i, count.i)) return Int(-1);
    if (count.i % o->frameBytes != 0) {
        char text[176];
        snprintf(text, sizeof text, "The sound is made of frames of %d bytes (%d channel(s) of 16 bits); %lld bytes are not a whole number of them.", o->frameBytes, o->channels, (long long)count.i);
        return Int(fire_audiob_fail(FIRE_AUDIOB_INVALID_ARGUMENT, text));
    }
    if (count.i == 0) { fire_audiob_ok(); return Int(0); }
    bytes = in.data + offset.i;   /* (what is not played at once is copied: held bytes, the device's queue) */
    if (o->volume != 100) {
        int64_t i;
        if ((size_t)count.i > o->scaled.cap) {
            uint8_t* grown = (uint8_t*)realloc(o->scaled.data, (size_t)count.i);
            if (grown == NULL) return Int(fire_audiob_fail(FIRE_AUDIOB_OTHER, "Out of memory."));
            o->scaled.data = grown;
            o->scaled.cap = (size_t)count.i;
        }
        o->scaled.size = (size_t)count.i;
        for (i = 0; i + 1 < count.i; i += 2) {
            int16_t s = (int16_t)((uint16_t)bytes[i] | ((uint16_t)bytes[i + 1] << 8));
            int32_t v = (int32_t)s * o->volume / 100;
            o->scaled.data[(size_t)i] = (uint8_t)(v & 0xFF);
            o->scaled.data[(size_t)i + 1] = (uint8_t)((v >> 8) & 0xFF);
        }
        bytes = o->scaled.data;
    }
    if (o->isSim) {
        FireAudiobSim* s = &g_audioSim;
        if (!s->hold) { fire_audiob_simPlay(bytes, (size_t)count.i); taken = count.i; }
        else {
            size_t room = s->pending.size < (size_t)FIRE_AUDIOB_SIM_HOLD_CAPACITY ? (size_t)FIRE_AUDIOB_SIM_HOLD_CAPACITY - s->pending.size : 0;
            size_t n;
            room -= room % (size_t)o->frameBytes;
            n = (size_t)count.i < room ? (size_t)count.i : room;
            if (!fire_audiob_append(&s->pending, bytes, n)) n = 0;
            taken = (int64_t)n;
        }
    } else {
        int accepted = 0;
        FireAudioStatus st;
        if (!fire_audio_write(o->out, bytes, (int)(count.i > 0x7FFFFFFF ? 0x7FFFFFFF : count.i), &accepted, &st)) return Int(fire_audiob_failStatus(&st));
        taken = accepted;
    }
    fire_audiob_ok();
    return Int(taken);
}

/* The bytes that were taken and are not played yet; -1 on an error. */
static Value fire_audio_Queued(Value h) {
    FireAudiobOutput* o = fire_audiob_find(h);
    if (o == NULL) return Int(-1);
    fire_audiob_ok();
    if (o->isSim) return Int((int64_t)g_audioSim.pending.size);
    return Int(fire_audio_queued(o->out));
}

/* Throws the queued sound away (the output stays open). */
static Value fire_audio_Stop(Value h) {
    FireAudiobOutput* o = fire_audiob_find(h);
    if (o == NULL) return Bool(false);
    if (o->isSim) g_audioSim.pending.size = 0;
    else fire_audio_stop(o->out);
    fire_audiob_ok();
    return Bool(true);
}

/* The volume in percent (0 .. 100); it is applied to the samples that are written afterwards. */
static Value fire_audio_SetVolume(Value h, Value percent) {
    FireAudiobOutput* o = fire_audiob_find(h);
    if (o == NULL) return Bool(false);
    if (percent.i < 0 || percent.i > 100) { fire_audiob_fail(FIRE_AUDIOB_INVALID_ARGUMENT, "The volume is between 0 and 100 percent."); return Bool(false); }
    o->volume = (int)percent.i;
    fire_audiob_ok();
    return Bool(true);
}

/* Closes the output; what was taken is played to its end first (unless `Stop` was called). */
static Value fire_audio_Close(Value h) {
    int64_t i = h.i;
    FireAudiobOutput* o;
    if (i <= 0 || (size_t)i >= g_audioOutputCount || g_audioOutputs[i] == NULL) { fire_audiob_fail(FIRE_AUDIOB_INVALID_HANDLE, "Invalid or already closed output handle."); return Bool(false); }
    o = g_audioOutputs[i];
    if (o->isSim) fire_audiob_simRelease();
    fire_audiob_releaseOutput(o);
    g_audioOutputs[i] = NULL;
    fire_audiob_ok();
    return Bool(true);
}

/* ---- inputs ----------------------------------------------------------------------------------------------------------------------------- */
/* 1 if this machine has a sound input (the simulated one is there anyway), else 0. */
static Value fire_audio_InSupported(void) {
    FireAudioList hardware;
    FireAudioStatus st;
    int yes;
    memset(&hardware, 0, sizeof hardware);
    fire_audiob_ok();
    yes = fire_audio_inputSupported() && fire_audio_inputDevices(&hardware, &st) && hardware.count > 0;
    fire_audio_listFree(&hardware);
    return Int(yes ? 1 : 0);
}

/* The inputs: "sim" first, then those of the hardware. */
static Value fire_audio_InDevices(OwnList* list) {
    FireAudioList hardware;
    FireAudioStatus st;
    Value r;
    memset(&hardware, 0, sizeof hardware);
    if (!fire_audio_inputDevices(&hardware, &st)) { fire_audio_listFree(&hardware); fire_audiob_failStatus(&st); return Undef(); }
    fire_audiob_ok();
    r = fire_audiob_names(&hardware, list);
    fire_audio_listFree(&hardware);
    return r;
}

static Value fire_audio_InOpen(Value name, Value rate, Value channels) {
    char* n;
    FireAudiobInput* i;
    if (rate.i < 1000 || rate.i > 192000) return Int(fire_audiob_fail(FIRE_AUDIOB_INVALID_ARGUMENT, "The rate must be between 1000 and 192000 Hz."));
    if (channels.i < 1 || channels.i > 2) return Int(fire_audiob_fail(FIRE_AUDIOB_INVALID_ARGUMENT, "Mono (1) or stereo (2) channels."));
    n = fire_audiob_utf8(name);
    if (n == NULL) return Int(fire_audiob_fail(FIRE_AUDIOB_OTHER, "Out of memory."));
    i = (FireAudiobInput*)calloc(1, sizeof(FireAudiobInput));
    if (i == NULL || !fire_audiob_roomForInput()) {
        free(i);
        free(n);
        return Int(fire_audiob_fail(FIRE_AUDIOB_OTHER, "Out of memory."));
    }
    i->rate = (int)rate.i;
    i->channels = (int)channels.i;
    i->frameBytes = 2 * i->channels;
    if (strcmp(n, "sim") == 0) i->isSim = 1;
    else {
        FireAudioStatus st;
        if (!fire_audio_openInput(n, i->rate, i->channels, &i->in, &st)) { free(i); free(n); return Int(fire_audiob_failStatus(&st)); }
    }
    free(n);
    g_audioInputs[g_audioInputCount] = i;
    fire_audiob_ok();
    return Int((int64_t)g_audioInputCount++);
}

static FireAudiobInput* fire_audiob_findInput(Value h) {
    int64_t i = h.i;
    if (i > 0 && (size_t)i < g_audioInputCount && g_audioInputs[i] != NULL) return g_audioInputs[i];
    fire_audiob_fail(FIRE_AUDIOB_INVALID_HANDLE, "Invalid or already closed input handle.");
    return NULL;
}

/* Takes up to `count` bytes (whole frames) of what the input recorded into the buffer from `offset`; the number of bytes (0: nothing yet), -1 on an error. */
static Value fire_audio_InRead(Value h, Value buffer, Value offset, Value count) {
    FireAudiobInput* i;
    int64_t want;
    uint8_t* bytes;
    int got = 0;
    Buf* b = bufferArg(buffer, "Audio.Input.Read");
    if (b == NULL) return Undef();
    i = fire_audiob_findInput(h);
    if (i == NULL) return Int(-1);
    if (!fire_audiob_checkRange(b->length, offset.i, count.i)) return Int(-1);
    want = count.i - count.i % i->frameBytes;
    bytes = b->data + offset.i;
    if (i->isSim) {
        FireAudiobSim* s = &g_audioSim;
        size_t left = s->input.size - s->inputAt;
        size_t n;
        left -= left % (size_t)i->frameBytes;
        n = (size_t)want < left ? (size_t)want : left;
        if (n > 0) memcpy(bytes, s->input.data + s->inputAt, n);
        s->inputAt += n;
        if (s->inputAt == s->input.size) { s->input.size = 0; s->inputAt = 0; }
        s->recorded += (int64_t)n;
        got = (int)n;
    } else {
        FireAudioStatus st;
        if (!fire_audio_read(i->in, bytes, (int)(want > 0x7FFFFFFF ? 0x7FFFFFFF : want), &got, &st)) return Int(fire_audiob_failStatus(&st));
    }
    fire_audiob_ok();
    return Int(got);
}

/* The bytes recorded and not read yet; -1 on an error. */
static Value fire_audio_InAvailable(Value h) {
    FireAudiobInput* i = fire_audiob_findInput(h);
    if (i == NULL) return Int(-1);
    fire_audiob_ok();
    if (i->isSim) return Int((int64_t)(g_audioSim.input.size - g_audioSim.inputAt));
    return Int(fire_audio_available(i->in));
}

static Value fire_audio_InClose(Value h) {
    int64_t i = h.i;
    if (i <= 0 || (size_t)i >= g_audioInputCount || g_audioInputs[i] == NULL) { fire_audiob_fail(FIRE_AUDIOB_INVALID_HANDLE, "Invalid or already closed input handle."); return Bool(false); }
    fire_audiob_releaseInput(g_audioInputs[i]);
    g_audioInputs[i] = NULL;
    fire_audiob_ok();
    return Bool(true);
}

/* ---- generated waves -------------------------------------------------------------------------------------------------------------------- */
/* Fills `frames` frames of the buffer (from `offset`, in the channels and the rate given) with a wave: kind 0 sine, 1 square, 2 triangle, 3 saw, 4 noise; `freq` the
 * frequency in Hz (an integer or a float, at most half the rate); `volume` in percent. `phase` is where the wave continues (0 at the start; the result of the last call
 * makes the next buffer join without a click; for noise it is the state of the generator). Returns the phase to go on with, -1 on an error. */
static Value fire_audio_Wave(Value buffer, Value offset, Value frames, Value kind, Value freq, Value volume, Value rate, Value channels, Value phase) {
    double hz, amplitude;
    const double twoPi = 6.283185307179586;
    int frameBytes;
    uint8_t* bytes;
    uint32_t p, increment;
    int64_t f;
    Buf* b = bufferArg(buffer, "Audio.Wave");
    if (b == NULL) return Undef();
    if (kind.i < 0 || kind.i > 4) return Int(fire_audiob_fail(FIRE_AUDIOB_INVALID_ARGUMENT, "Unknown wave (0 sine, 1 square, 2 triangle, 3 saw, 4 noise)."));
    hz = freq.kind == K_Float ? (double)freq.f : (double)freq.i;
    if (rate.i < 1000 || rate.i > 192000 || channels.i < 1 || channels.i > 2 || volume.i < 0 || volume.i > 100 || frames.i < 0)
        return Int(fire_audiob_fail(FIRE_AUDIOB_INVALID_ARGUMENT, "Invalid rate, channels, volume or number of frames."));
    if (!(hz >= 0.0) || hz > (double)rate.i / 2.0) {
        char text[176];
        snprintf(text, sizeof text, "The frequency must be between 0 and half of the rate (%lld Hz).", (long long)(rate.i / 2));
        return Int(fire_audiob_fail(FIRE_AUDIOB_INVALID_ARGUMENT, text));
    }
    frameBytes = (int)(2 * channels.i);
    if (!fire_audiob_checkRange(b->length, offset.i, frames.i * frameBytes)) return Int(-1);
    bytes = b->data + offset.i;
    p = (uint32_t)phase.i;
    increment = (uint32_t)(hz / (double)rate.i * 4294967296.0);
    amplitude = 32767.0 * (double)volume.i / 100.0;
    for (f = 0; f < frames.i; f++) {
        double v;   /* -1 .. +1 */
        int32_t s;
        int c;
        switch (kind.i) {
            case 0: v = sin(twoPi * ((double)p / 4294967296.0)); break;
            case 1: v = p < 0x80000000u ? 1.0 : -1.0; break;
            case 2: { double t = (double)p / 4294967296.0; v = t < 0.5 ? 4.0 * t - 1.0 : 3.0 - 4.0 * t; break; }
            case 3: v = 2.0 * ((double)p / 4294967296.0) - 1.0; break;
            default: {
                if (p == 0) p = 0x12345678u;
                p ^= p << 13; p ^= p >> 17; p ^= p << 5;   /* xorshift32 */
                v = (double)(int32_t)p / 2147483648.0;
                break;
            }
        }
        s = (int32_t)(v * amplitude);
        for (c = 0; c < channels.i; c++) {
            bytes[f * frameBytes + 2 * c] = (uint8_t)(s & 0xFF);
            bytes[f * frameBytes + 2 * c + 1] = (uint8_t)((s >> 8) & 0xFF);
        }
        if (kind.i != 4) p += increment;
    }
    fire_audiob_ok();
    return Int((int64_t)p);
}

/* ---- the simulated device from the outside ---------------------------------------------------------------------------------------------- */
/* Forgets everything that was played and what the simulated input had; the device plays again (not held). Open outputs and inputs stay open. */
static Value fire_audio_SimReset(void) {
    fire_audiob_simClear();
    fire_audiob_ok();
    return Bool(true);
}
/* Holds the device (true): it takes at most 4096 bytes and keeps them; releases it (false): the kept bytes are played. */
static Value fire_audio_SimHold(Value hold) {
    g_audioSim.hold = hold.i != 0;
    if (!g_audioSim.hold) fire_audiob_simRelease();
    fire_audiob_ok();
    return Bool(true);
}
/* Gives the simulated input sound to record: `count` bytes (16 bit samples in the channels of the input that reads them) from `offset` of the buffer. */
static Value fire_audio_SimFeed(Value buffer, Value offset, Value count) {
    const uint8_t* bytes;
    FireBytes in;
    if (!bytesArg(buffer, "Audio.Sim.Feed", &in)) return Undef();
    if (!fire_audiob_checkRange(in.length, offset.i, count.i)) return Int(-1);
    bytes = in.data + offset.i;   /* (appended as a copy) */
    if (!fire_audiob_append(&g_audioSim.input, bytes, (size_t)count.i)) return Int(fire_audiob_fail(FIRE_AUDIOB_OTHER, "Out of memory."));
    fire_audiob_ok();
    return Int(count.i);
}
/* What the device knows: 0 the bytes played, 1 the rate of the last opened output, 2 its channels, 3 the bytes that wait (held), 4 the bytes the simulated
 * input still has, 5 the bytes it handed over. */
static Value fire_audio_SimInfo(Value what) {
    fire_audiob_ok();
    switch (what.i) {
        case 0: return Int(g_audioSim.played);
        case 1: return Int(g_audioSim.rate);
        case 2: return Int(g_audioSim.channels);
        case 3: return Int((int64_t)g_audioSim.pending.size);
        case 4: return Int((int64_t)(g_audioSim.input.size - g_audioSim.inputAt));
        case 5: return Int(g_audioSim.recorded);
        default: fire_audiob_fail(FIRE_AUDIOB_INVALID_ARGUMENT, "Unknown information."); return Int(-1);
    }
}
/* The bytes that were played (a buffer; at most 16 MB are recorded). */
static Value fire_audio_SimData(OwnList* list) {
    Buf* b = allocBuf((uint32_t)g_audioSim.data.size, list);
    if (g_audioSim.data.size > 0) memcpy(b->data, g_audioSim.data.data, g_audioSim.data.size);
    fire_audiob_ok();
    return BufV(b);
}

#endif
