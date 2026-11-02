/* fire native platform layer, audio part in C on Windows: the waveOut API of winmm (every Windows has it; the system mixes and resamples). The device "default" is the
 * wave mapper (the default device of the user); the other names are those that Windows reports for the devices (or their numbers). The interface is described in
 * fire_audio_c.h. (Link: winmm.)
 *
 * The sound is handed over in blocks of 50 ms that the driver plays one after the other; `write` fills the free blocks and returns at once, a block is free again when
 * the driver sets its DONE flag. So nothing in here blocks and no thread or callback is needed. */
#ifndef FIRE_AUDIO_WIN32_H
#define FIRE_AUDIO_WIN32_H

#include "fire_audio_c.h"

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <mmsystem.h>

#define FIRE_AUDIO_BLOCKS 32

typedef struct FireAudioBlock {
    WAVEHDR header;
    uint8_t* data;
    int submitted;
} FireAudioBlock;

struct FireAudioOutput {
    HWAVEOUT handle;
    int frameBytes;
    int blockBytes;
    int bytesPerSecond;
    int discard;
    FireAudioBlock blocks[FIRE_AUDIO_BLOCKS];
    int next;   /* the block the next write fills (they are used in a ring) */
};

static int fire_audio_supported(void) { return waveOutGetNumDevs() > 0; }

/* The name of output `index` ("" if Windows does not say). */
static void fire_audio_deviceName(UINT index, char* name, size_t size) {
    WAVEOUTCAPSA caps;
    name[0] = 0;
    if (waveOutGetDevCapsA(index, &caps, sizeof caps) != MMSYSERR_NOERROR) return;
    snprintf(name, size, "%s", caps.szPname);
}

static int fire_audio_devices(FireAudioList* names, FireAudioStatus* st) {
    UINT count = waveOutGetNumDevs(), i;
    fire_audio_listClear(names);
    if (count == 0) return fire_audio_success(st);
    fire_audio_listAdd(names, "default");
    for (i = 0; i < count; i++) {
        char n[MAXPNAMELEN + 1];
        fire_audio_deviceName(i, n, sizeof n);
        if (n[0]) fire_audio_listAdd(names, n);
    }
    return fire_audio_success(st);
}

static void fire_audio_errorText(MMRESULT r, char* text, size_t size) {
    char buffer[MAXERRORLENGTH];
    memset(buffer, 0, sizeof buffer);
    if (waveOutGetErrorTextA(r, buffer, sizeof buffer) != MMSYSERR_NOERROR) snprintf(text, size, "error %d", (int)r);
    else snprintf(text, size, "%s", buffer);
}

static int fire_audio_isDone(const FireAudioBlock* b) { return !b->submitted || (b->header.dwFlags & WHDR_DONE) != 0; }

static void fire_audio_release(FireAudioOutput* o, FireAudioBlock* b) {
    if (b->submitted) {
        waveOutUnprepareHeader(o->handle, &b->header, sizeof(WAVEHDR));
        b->submitted = 0;
    }
}

/* The number of a device given by its name or its number (`count` devices; `nameOf` gives a name): 1 if found. */
static int fire_audio_findDevice(const char* name, UINT count, void (*nameOf)(UINT, char*, size_t), UINT* device) {
    UINT i;
    for (i = 0; i < count; i++) {
        char n[MAXPNAMELEN + 1];
        nameOf(i, n, sizeof n);
        if (strcmp(n, name) == 0) { *device = i; return 1; }
    }
    if (fire_audio_isNumber(name) && strlen(name) <= 9 && (UINT)atoi(name) < count) { *device = (UINT)atoi(name); return 1; }
    return 0;
}

static int fire_audio_open(const char* name, int rate, int channels, FireAudioOutput** out, FireAudioStatus* st) {
    UINT device = WAVE_MAPPER;
    WAVEFORMATEX format;
    HWAVEOUT handle = NULL;
    MMRESULT r;
    FireAudioOutput* o;
    int k;
    *out = NULL;
    if (strcmp(name, "default") != 0) {
        if (!fire_audio_findDevice(name, waveOutGetNumDevs(), fire_audio_deviceName, &device))
            return fire_audio_fail(st, FIRE_AUDIO_E_NOT_FOUND, "There is no sound device '%s' (\"default\" or one of the names that Audio.Board.Devices() lists).", name);
    }
    memset(&format, 0, sizeof format);
    format.wFormatTag = WAVE_FORMAT_PCM;
    format.nChannels = (WORD)channels;
    format.nSamplesPerSec = (DWORD)rate;
    format.wBitsPerSample = 16;
    format.nBlockAlign = (WORD)(channels * 2);
    format.nAvgBytesPerSec = (DWORD)(rate * channels * 2);
    r = waveOutOpen(&handle, device, &format, 0, 0, CALLBACK_NULL);
    if (r != MMSYSERR_NOERROR) {
        char text[MAXERRORLENGTH + 16];
        int code = (r == MMSYSERR_ALLOCATED) ? FIRE_AUDIO_E_BUSY : (r == MMSYSERR_BADDEVICEID || r == MMSYSERR_NODRIVER) ? FIRE_AUDIO_E_NOT_FOUND : (r == WAVERR_BADFORMAT) ? FIRE_AUDIO_E_UNSUPPORTED : FIRE_AUDIO_E_OTHER;
        fire_audio_errorText(r, text, sizeof text);
        return fire_audio_fail(st, code, "waveOut: %s", text);
    }
    o = (FireAudioOutput*)calloc(1, sizeof(FireAudioOutput));
    if (o == NULL) { waveOutClose(handle); return fire_audio_fail(st, FIRE_AUDIO_E_OTHER, "Out of memory."); }
    o->handle = handle;
    o->frameBytes = channels * 2;
    o->bytesPerSecond = rate * channels * 2;
    o->blockBytes = o->bytesPerSecond / 20;   /* 50 ms */
    o->blockBytes -= o->blockBytes % o->frameBytes;
    if (o->blockBytes < o->frameBytes) o->blockBytes = o->frameBytes;
    for (k = 0; k < FIRE_AUDIO_BLOCKS; k++) {
        o->blocks[k].data = (uint8_t*)malloc((size_t)o->blockBytes);
        if (o->blocks[k].data == NULL) {
            while (k-- > 0) free(o->blocks[k].data);
            free(o);
            waveOutClose(handle);
            return fire_audio_fail(st, FIRE_AUDIO_E_OTHER, "Out of memory.");
        }
    }
    *out = o;
    return fire_audio_success(st);
}

static int fire_audio_write(FireAudioOutput* o, const uint8_t* data, int bytes, int* accepted, FireAudioStatus* st) {
    *accepted = 0;
    o->discard = 0;
    bytes -= bytes % o->frameBytes;
    while (bytes > 0) {
        FireAudioBlock* b = &o->blocks[o->next];
        int n;
        MMRESULT r;
        if (!fire_audio_isDone(b)) break;   /* all blocks are queued: the rest has to wait */
        fire_audio_release(o, b);
        n = bytes < o->blockBytes ? bytes : o->blockBytes;
        memcpy(b->data, data + *accepted, (size_t)n);
        memset(&b->header, 0, sizeof b->header);
        b->header.lpData = (LPSTR)b->data;
        b->header.dwBufferLength = (DWORD)n;
        r = waveOutPrepareHeader(o->handle, &b->header, sizeof(WAVEHDR));
        if (r == MMSYSERR_NOERROR) r = waveOutWrite(o->handle, &b->header, sizeof(WAVEHDR));
        if (r != MMSYSERR_NOERROR) {
            char text[MAXERRORLENGTH + 16];
            if (b->header.dwFlags & WHDR_PREPARED) waveOutUnprepareHeader(o->handle, &b->header, sizeof(WAVEHDR));
            fire_audio_errorText(r, text, sizeof text);
            return fire_audio_fail(st, FIRE_AUDIO_E_OTHER, "waveOut: %s", text);
        }
        b->submitted = 1;
        o->next = (o->next + 1) % FIRE_AUDIO_BLOCKS;
        *accepted += n;
        bytes -= n;
    }
    return fire_audio_success(st);
}

static int64_t fire_audio_queued(FireAudioOutput* o) {
    int64_t total = 0;
    int k;
    for (k = 0; k < FIRE_AUDIO_BLOCKS; k++) if (!fire_audio_isDone(&o->blocks[k])) total += o->blocks[k].header.dwBufferLength;
    return total;
}

static void fire_audio_stop(FireAudioOutput* o) {
    o->discard = 1;
    waveOutReset(o->handle);   /* returns all blocks (DONE) at once */
}

static void fire_audio_close(FireAudioOutput* o) {
    int k;
    if (!o) return;
    if (!o->discard) {
        /* plays what was accepted (at most FIRE_AUDIO_BLOCKS * 50 ms) */
        int waited;
        for (waited = 0; waited < 2500 && fire_audio_queued(o) > 0; waited += 5) Sleep(5);
    }
    waveOutReset(o->handle);
    for (k = 0; k < FIRE_AUDIO_BLOCKS; k++) fire_audio_release(o, &o->blocks[k]);
    waveOutClose(o->handle);
    for (k = 0; k < FIRE_AUDIO_BLOCKS; k++) free(o->blocks[k].data);
    free(o);
}


/* ---- sound input ---------------------------------------------------------------------------------------------------------------------- */
/* Capture with waveIn: blocks of 20 ms are handed to the driver, which fills them one after the other; `read` takes the filled ones (and the part of one that was not
 * taken before) and hands them back to the driver. When nobody reads, the driver stops at the end of the free blocks and the newest sound is lost. */
#define FIRE_AUDIO_IN_BLOCKS 16

typedef struct FireAudioInBlock {
    WAVEHDR header;
    uint8_t* data;
    size_t size;
    int submitted;
} FireAudioInBlock;

struct FireAudioInput {
    HWAVEIN handle;
    int frameBytes;
    int blockBytes;
    int bytesPerSecond;
    FireAudioInBlock blocks[FIRE_AUDIO_IN_BLOCKS];
    int next;           /* the block that is read next */
    DWORD taken;        /* the bytes of it already read */
};

/* The name of input `index` ("" if Windows does not say). */
static void fire_audio_inputName(UINT index, char* name, size_t size) {
    WAVEINCAPSA caps;
    name[0] = 0;
    if (waveInGetDevCapsA(index, &caps, sizeof caps) != MMSYSERR_NOERROR) return;
    snprintf(name, size, "%s", caps.szPname);
}

static int fire_audio_inputSupported(void) { return waveInGetNumDevs() > 0; }

static int fire_audio_inputDevices(FireAudioList* names, FireAudioStatus* st) {
    UINT count = waveInGetNumDevs(), i;
    fire_audio_listClear(names);
    if (count == 0) return fire_audio_success(st);
    fire_audio_listAdd(names, "default");
    for (i = 0; i < count; i++) {
        char n[MAXPNAMELEN + 1];
        fire_audio_inputName(i, n, sizeof n);
        if (n[0]) fire_audio_listAdd(names, n);
    }
    return fire_audio_success(st);
}

static int fire_audio_submit(FireAudioInput* in, FireAudioInBlock* b) {
    memset(&b->header, 0, sizeof b->header);
    b->header.lpData = (LPSTR)b->data;
    b->header.dwBufferLength = (DWORD)b->size;
    if (waveInPrepareHeader(in->handle, &b->header, sizeof(WAVEHDR)) != MMSYSERR_NOERROR) return 0;
    if (waveInAddBuffer(in->handle, &b->header, sizeof(WAVEHDR)) != MMSYSERR_NOERROR) { waveInUnprepareHeader(in->handle, &b->header, sizeof(WAVEHDR)); return 0; }
    b->submitted = 1;
    return 1;
}

static int fire_audio_openInput(const char* name, int rate, int channels, FireAudioInput** in, FireAudioStatus* st) {
    UINT device = WAVE_MAPPER;
    WAVEFORMATEX format;
    HWAVEIN handle = NULL;
    MMRESULT r;
    FireAudioInput* i;
    int k;
    *in = NULL;
    if (strcmp(name, "default") != 0) {
        if (!fire_audio_findDevice(name, waveInGetNumDevs(), fire_audio_inputName, &device))
            return fire_audio_fail(st, FIRE_AUDIO_E_NOT_FOUND, "There is no sound input '%s' (\"default\" or one of the names that Audio.Board.Inputs() lists).", name);
    }
    memset(&format, 0, sizeof format);
    format.wFormatTag = WAVE_FORMAT_PCM;
    format.nChannels = (WORD)channels;
    format.nSamplesPerSec = (DWORD)rate;
    format.wBitsPerSample = 16;
    format.nBlockAlign = (WORD)(channels * 2);
    format.nAvgBytesPerSec = (DWORD)(rate * channels * 2);
    r = waveInOpen(&handle, device, &format, 0, 0, CALLBACK_NULL);
    if (r != MMSYSERR_NOERROR) {
        int code = (r == MMSYSERR_ALLOCATED) ? FIRE_AUDIO_E_BUSY : (r == MMSYSERR_BADDEVICEID || r == MMSYSERR_NODRIVER) ? FIRE_AUDIO_E_NOT_FOUND : (r == WAVERR_BADFORMAT) ? FIRE_AUDIO_E_UNSUPPORTED : FIRE_AUDIO_E_OTHER;
        return fire_audio_fail(st, code, "waveIn: error %d", (int)r);
    }
    i = (FireAudioInput*)calloc(1, sizeof(FireAudioInput));
    if (i == NULL) { waveInClose(handle); return fire_audio_fail(st, FIRE_AUDIO_E_OTHER, "Out of memory."); }
    i->handle = handle;
    i->frameBytes = channels * 2;
    i->bytesPerSecond = rate * channels * 2;
    i->blockBytes = i->bytesPerSecond / 50;   /* 20 ms */
    i->blockBytes -= i->blockBytes % i->frameBytes;
    if (i->blockBytes < i->frameBytes) i->blockBytes = i->frameBytes;
    for (k = 0; k < FIRE_AUDIO_IN_BLOCKS; k++) {
        FireAudioInBlock* b = &i->blocks[k];
        b->data = (uint8_t*)calloc(1, (size_t)i->blockBytes);
        if (b->data == NULL) {
            waveInReset(handle);
            while (k-- > 0) { if (i->blocks[k].submitted) waveInUnprepareHeader(handle, &i->blocks[k].header, sizeof(WAVEHDR)); free(i->blocks[k].data); }
            waveInClose(handle);
            free(i);
            return fire_audio_fail(st, FIRE_AUDIO_E_OTHER, "Out of memory.");
        }
        b->size = (size_t)i->blockBytes;
        fire_audio_submit(i, b);
    }
    waveInStart(handle);
    *in = i;
    return fire_audio_success(st);
}

static int fire_audio_read(FireAudioInput* in, uint8_t* data, int bytes, int* got, FireAudioStatus* st) {
    *got = 0;
    bytes -= bytes % in->frameBytes;
    while (*got < bytes) {
        FireAudioInBlock* b = &in->blocks[in->next];
        DWORD have, n;
        if (!b->submitted || (b->header.dwFlags & WHDR_DONE) == 0) break;   /* not filled yet */
        have = b->header.dwBytesRecorded - in->taken;
        n = have < (DWORD)(bytes - *got) ? have : (DWORD)(bytes - *got);
        memcpy(data + *got, b->data + in->taken, n);
        *got += (int)n;
        in->taken += n;
        if (in->taken >= b->header.dwBytesRecorded) {
            waveInUnprepareHeader(in->handle, &b->header, sizeof(WAVEHDR));
            b->submitted = 0;
            fire_audio_submit(in, b);
            in->taken = 0;
            in->next = (in->next + 1) % FIRE_AUDIO_IN_BLOCKS;
        }
    }
    return fire_audio_success(st);
}

static int64_t fire_audio_available(FireAudioInput* in) {
    int64_t total = 0;
    int k;
    for (k = 0; k < FIRE_AUDIO_IN_BLOCKS; k++) {
        FireAudioInBlock* b = &in->blocks[(in->next + k) % FIRE_AUDIO_IN_BLOCKS];
        if (!b->submitted || (b->header.dwFlags & WHDR_DONE) == 0) break;
        total += (int64_t)b->header.dwBytesRecorded - (k == 0 ? in->taken : 0);
    }
    return total;
}

static void fire_audio_closeInput(FireAudioInput* in) {
    int k;
    if (!in) return;
    waveInReset(in->handle);   /* returns all blocks */
    for (k = 0; k < FIRE_AUDIO_IN_BLOCKS; k++) if (in->blocks[k].submitted) waveInUnprepareHeader(in->handle, &in->blocks[k].header, sizeof(WAVEHDR));
    waveInClose(in->handle);
    for (k = 0; k < FIRE_AUDIO_IN_BLOCKS; k++) free(in->blocks[k].data);
    free(in);
}

#endif
