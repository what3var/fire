/* fire native platform layer, audio part in C (the HAL of the audio bridge): what the implementations share - the error codes of the language, the status of an
 * operation, a list of names. It compiles as C++ as well (the circuits package, C++, plays and records through it).
 *
 * A platform package offers `fire_audio.h` (FIRE_PLATFORM_AUDIO_C_HEADER points to it) with, all names in UTF-8:
 *   enum FIRE_AUDIO_E_*            the codes of the error slot (the same numbers as in the prelude: Audio.AudioException.code)
 *   FireAudioStatus { int code; char message[176]; }     code 0 = ok
 *   FireAudioOutput                an opened output (opaque)
 *   int  fire_audio_supported(void)                     0: there is no sound here (the list of devices is empty)
 *   int  fire_audio_devices(FireAudioList* names, st)    the devices of this machine besides "sim" ("default", "pulse", "alsa" on Linux, "default" on Windows, "pwm" on an ESP32)
 *   int  fire_audio_open(name, int rate, int channels, FireAudioOutput** out, st)    16 bit signed samples, little endian, interleaved; FIRE_AUDIO_E_NOT_FOUND for a
 *                                  device that is not there
 *   int  fire_audio_write(FireAudioOutput*, const uint8_t* data, int bytes, int* accepted, st)   takes as many whole frames as fit into the buffer of the output and
 *                                  returns at once (never blocks)
 *   int64_t fire_audio_queued(FireAudioOutput*)         the bytes that were accepted and have not been played yet
 *   void fire_audio_stop(FireAudioOutput*)              throws the queued sound away (the output stays open)
 *   void fire_audio_close(FireAudioOutput*)             closes the output; what is queued is not played any more (the bridge waits for the end before, if the program wants that)
 * and the input side: an opened input captures 16 bit signed samples (little endian, interleaved) into its buffer; `read` takes what was captured (whole frames) and
 * returns at once, `available` says how much waits. A buffer that is not read in time loses its oldest sound.
 *   FireAudioInput                 an opened input (opaque)
 *   int  fire_audio_inputSupported(void)
 *   int  fire_audio_inputDevices(FireAudioList* names, st)
 *   int  fire_audio_openInput(name, int rate, int channels, FireAudioInput** in, st)
 *   int  fire_audio_read(FireAudioInput*, uint8_t* data, int bytes, int* got, st)
 *   int64_t fire_audio_available(FireAudioInput*)
 *   void fire_audio_closeInput(FireAudioInput*)
 * The functions that return int give 1 when done and 0 on an error (the status says why). Nothing waits: the bridge asks `queued` and the fire code sleeps in
 * between, so a program can always be aborted.
 *
 * The implementations: fire_audio_linux.h (PulseAudio or ALSA, loaded at run time), fire_audio_win32.h (winmm), fire_audio_coreaudio.h (Audio Queue Services: macOS),
 * fire_audio_web.h (Web Audio: the browser), fire_audio_sdl.h (SDL2: anywhere with FIRE_AUDIO_SDL), fire_audio_esp32.h (PWM on a GPIO pin), fire_audio_none.h (no sound
 * output: the list of devices is empty and every call fails with "not supported"; the simulated device `sim` of the bridge works everywhere). A board with sound of its
 * own provides its own `fire_audio.h`. */
#ifndef FIRE_AUDIO_C_H
#define FIRE_AUDIO_C_H

#include <stdarg.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* The error codes of the language (the same numbers as in the prelude of the audio package: Audio.AudioException.code). */
enum { FIRE_AUDIO_E_NONE = 0, FIRE_AUDIO_E_INVALID_ARGUMENT = 1, FIRE_AUDIO_E_INVALID_HANDLE = 2, FIRE_AUDIO_E_NOT_FOUND = 3, FIRE_AUDIO_E_BUSY = 4,
       FIRE_AUDIO_E_PERMISSION = 5, FIRE_AUDIO_E_UNSUPPORTED = 6, FIRE_AUDIO_E_OTHER = 7 };

typedef struct FireAudioStatus { int code; char message[176]; } FireAudioStatus;

typedef struct FireAudioOutput FireAudioOutput;
typedef struct FireAudioInput FireAudioInput;

/* Records a failure (printf format): always 0. */
static int fire_audio_fail(FireAudioStatus* st, int code, const char* format, ...) {
    va_list args;
    st->code = code;
    va_start(args, format);
    vsnprintf(st->message, sizeof st->message, format, args);
    va_end(args);
    return 0;
}
/* Records a success: always 1. */
static int fire_audio_success(FireAudioStatus* st) {
    st->code = 0;
    st->message[0] = 0;
    return 1;
}

/* ---- a list of names ---- */
typedef struct FireAudioList { char** items; size_t count, cap; } FireAudioList;
static void fire_audio_listAdd(FireAudioList* l, const char* s) {
    size_t n = strlen(s);
    char* copy;
    if (l->count == l->cap) {
        size_t cap = l->cap ? l->cap * 2 : 8;
        char** grown = (char**)realloc(l->items, sizeof(char*) * cap);
        if (grown == NULL) return;
        l->items = grown;
        l->cap = cap;
    }
    copy = (char*)malloc(n + 1);
    if (copy == NULL) return;
    memcpy(copy, s, n + 1);
    l->items[l->count++] = copy;
}
static void fire_audio_listClear(FireAudioList* l) {
    size_t i;
    for (i = 0; i < l->count; i++) free(l->items[i]);
    l->count = 0;
}
static void fire_audio_listFree(FireAudioList* l) {
    fire_audio_listClear(l);
    free(l->items);
    l->items = NULL;
    l->cap = 0;
}

/* Whether `s` is a non-empty string of decimal digits (a device given by its number). */
static int fire_audio_isNumber(const char* s) {
    if (*s == 0) return 0;
    for (; *s; s++) if (*s < '0' || *s > '9') return 0;
    return 1;
}

#endif
