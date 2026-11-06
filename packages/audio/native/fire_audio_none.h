/* fire audio package: audio part in C for a platform without sound output (a board without a package of its own; a Linux whose sound libraries are not
 * installed is NOT this case: the Linux part finds that out when it is asked): the list of devices is empty and every call fails with "not supported". The simulated
 * device `sim` of the bridge works everywhere. The interface is described in fire_audio_c.h. */
#ifndef FIRE_AUDIO_NONE_H
#define FIRE_AUDIO_NONE_H
#if !defined(FIRE_AUDIO_DESKTOP_H) || defined(FIRE_AUDIO_USE_NONE)   /* (with fire_audio_desktop.h in the build: when it chose this one) */

#include "fire_audio_c.h"

struct FireAudioOutput { int unused; };

static int fire_audio_supported(void) { return 0; }
static int fire_audio_none(FireAudioStatus* st) { return fire_audio_fail(st, FIRE_AUDIO_E_UNSUPPORTED, "This platform has no sound output (the simulated device \"sim\" works everywhere)."); }

static int fire_audio_devices(FireAudioList* names, FireAudioStatus* st) { fire_audio_listClear(names); return fire_audio_success(st); }
static int fire_audio_open(const char* name, int rate, int channels, FireAudioOutput** out, FireAudioStatus* st) {
    (void)name; (void)rate; (void)channels;
    *out = NULL;
    return fire_audio_fail(st, FIRE_AUDIO_E_NOT_FOUND, "There is no such sound device on this platform.");
}
static int fire_audio_write(FireAudioOutput* o, const uint8_t* data, int bytes, int* accepted, FireAudioStatus* st) { (void)o; (void)data; (void)bytes; *accepted = 0; return fire_audio_none(st); }
static int64_t fire_audio_queued(FireAudioOutput* o) { (void)o; return 0; }
static void fire_audio_stop(FireAudioOutput* o) { (void)o; }
static void fire_audio_close(FireAudioOutput* o) { (void)o; }


/* ---- sound input ---------------------------------------------------------------------------------------------------------------------- */
/* The input side of the interface: an opened input captures 16 bit signed samples (little endian, interleaved) into its buffer; `read` takes what was captured (whole
 * frames) and returns at once, `available` says how much waits. A buffer that is not read in time loses its oldest sound. */
struct FireAudioInput { int unused; };
static int fire_audio_inputSupported(void) { return 0; }
static int fire_audio_inputDevices(FireAudioList* names, FireAudioStatus* st) { fire_audio_listClear(names); return fire_audio_success(st); }
static int fire_audio_openInput(const char* name, int rate, int channels, FireAudioInput** in, FireAudioStatus* st) {
    (void)name; (void)rate; (void)channels;
    *in = NULL;
    return fire_audio_fail(st, FIRE_AUDIO_E_NOT_FOUND, "This platform has no sound input (the simulated device \"sim\" works everywhere).");
}
static int fire_audio_read(FireAudioInput* in, uint8_t* data, int bytes, int* got, FireAudioStatus* st) {
    (void)in; (void)data; (void)bytes;
    *got = 0;
    return fire_audio_fail(st, FIRE_AUDIO_E_UNSUPPORTED, "This platform has no sound input.");
}
static int64_t fire_audio_available(FireAudioInput* in) { (void)in; return 0; }
static void fire_audio_closeInput(FireAudioInput* in) { (void)in; }

#endif
#endif
