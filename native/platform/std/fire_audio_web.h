/* fire native platform layer, audio part in C in the browser: Web Audio, driven by a few functions of JavaScript in this file (EM_JS of Emscripten) - no SDL. The
 * interface is described in fire_audio_c.h.
 *
 * An output is an AudioContext: every `write` becomes an AudioBuffer (16 bit samples to floats, channel by channel) played right after what was written before;
 * `queued` is the time that is still planned, in bytes - at most half a second. A browser lets sound start only after the user did something on the page: the
 * context waits for the first click or key (what is written meanwhile is planned and plays then). An input asks for the microphone (the browser asks the user)
 * and collects what it records in a list that `read` takes from (at most a second; the oldest is lost). The device is "default". */
#ifndef FIRE_AUDIO_WEB_H
#define FIRE_AUDIO_WEB_H

#include <emscripten.h>
#include "fire_audio_c.h"

EM_JS(int, fire_audiojs_supported, (void), { return (typeof AudioContext !== "undefined" || typeof webkitAudioContext !== "undefined") ? 1 : 0; });
EM_JS(int, fire_audiojs_open, (int rate, int channels), {
    var C = typeof AudioContext !== "undefined" ? AudioContext : webkitAudioContext;
    var ctx;
    try { ctx = new C({ sampleRate: rate }); } catch (e) { try { ctx = new C(); } catch (e2) { return -1; } }
    if (!Module.fireAudio) Module.fireAudio = [];
    var o = { ctx: ctx, rate: rate, channels: channels, next: 0, sources: [] };
    /* (the browser starts the sound only after the user did something on the page) */
    var resume = function () { if (ctx.state === "suspended") ctx.resume(); };
    document.addEventListener("pointerdown", resume, true);
    document.addEventListener("keydown", resume, true);
    o.resume = resume;
    resume();
    var id = Module.fireAudio.length;
    Module.fireAudio.push(o);
    return id;
});
/* Plans `frames` frames (16 bit, interleaved, at `data`) after what was planned before. */
EM_JS(void, fire_audiojs_write, (int id, const void* data, int frames), {
    var o = Module.fireAudio[id];
    if (!o || frames <= 0) return;
    var b = o.ctx.createBuffer(o.channels, frames, o.rate), c, k;
    var src = new Int16Array(HEAPU8.buffer, data, frames * o.channels);
    for (c = 0; c < o.channels; c++) {
        var ch = b.getChannelData(c);
        for (k = 0; k < frames; k++) ch[k] = src[k * o.channels + c] / 32768;
    }
    var s = o.ctx.createBufferSource();
    s.buffer = b;
    s.connect(o.ctx.destination);
    var at = Math.max(o.next, o.ctx.currentTime);
    s.start(at);
    o.next = at + frames / o.rate;
    o.sources.push(s);
    s.onended = function () { var i = o.sources.indexOf(s); if (i >= 0) o.sources.splice(i, 1); };
});
/* The planned time that is not played yet, in frames. */
EM_JS(double, fire_audiojs_queuedFrames, (int id), {
    var o = Module.fireAudio[id];
    return o ? Math.max(0, o.next - o.ctx.currentTime) * o.rate : 0;
});
EM_JS(void, fire_audiojs_stop, (int id), {
    var o = Module.fireAudio[id];
    if (!o) return;
    o.sources.forEach(function (s) { try { s.stop(); } catch (e) {} });
    o.sources = [];
    o.next = o.ctx.currentTime;
});
EM_JS(void, fire_audiojs_close, (int id), {
    var o = Module.fireAudio[id];
    if (!o) return;
    document.removeEventListener("pointerdown", o.resume, true);
    document.removeEventListener("keydown", o.resume, true);
    try { o.ctx.close(); } catch (e) {}
    Module.fireAudio[id] = null;
});
/* ---- input: the microphone through getUserMedia and a ScriptProcessor (no worklet: the program has no threads in the browser) ---- */
EM_JS(int, fire_audiojs_inputSupported, (void), { return navigator.mediaDevices && navigator.mediaDevices.getUserMedia ? 1 : 0; });
EM_JS(int, fire_audiojs_openInput, (int rate, int channels), {
    var C = typeof AudioContext !== "undefined" ? AudioContext : webkitAudioContext;
    var ctx;
    try { ctx = new C({ sampleRate: rate }); } catch (e) { return -1; }
    if (!Module.fireAudio) Module.fireAudio = [];
    var i = { ctx: ctx, channels: channels, chunks: [], frames: 0, max: rate };
    navigator.mediaDevices.getUserMedia({ audio: { channelCount: channels } }).then(function (stream) {
        var source = ctx.createMediaStreamSource(stream), proc = ctx.createScriptProcessor(4096, channels, channels);
        proc.onaudioprocess = function (e) {
            var n = e.inputBuffer.length, out = new Int16Array(n * channels), c, k;
            for (c = 0; c < channels; c++) {
                var d = e.inputBuffer.getChannelData(Math.min(c, e.inputBuffer.numberOfChannels - 1));
                for (k = 0; k < n; k++) out[k * channels + c] = Math.max(-32768, Math.min(32767, Math.round(d[k] * 32767)));
            }
            i.chunks.push(out); i.frames += n;
            while (i.frames > i.max && i.chunks.length > 1) i.frames -= i.chunks.shift().length / channels;
        };
        source.connect(proc); proc.connect(ctx.destination);
        i.stream = stream;
    }, function () { i.denied = true; });
    var id = Module.fireAudio.length;
    Module.fireAudio.push(i);
    return id;
});
EM_JS(int, fire_audiojs_availableFrames, (int id), { var i = Module.fireAudio[id]; return i ? i.frames : 0; });
/* Takes up to `frames` frames into `data`: how many it took. */
EM_JS(int, fire_audiojs_read, (int id, void* data, int frames), {
    var i = Module.fireAudio[id];
    if (!i) return 0;
    var dst = new Int16Array(HEAPU8.buffer, data, frames * i.channels), took = 0;
    while (took < frames && i.chunks.length > 0) {
        var c = i.chunks[0], n = Math.min(c.length / i.channels, frames - took);
        dst.set(c.subarray(0, n * i.channels), took * i.channels);
        took += n; i.frames -= n;
        if (n * i.channels < c.length) i.chunks[0] = c.subarray(n * i.channels); else i.chunks.shift();
    }
    return took;
});
EM_JS(void, fire_audiojs_closeInput, (int id), {
    var i = Module.fireAudio[id];
    if (!i) return;
    if (i.stream) i.stream.getTracks().forEach(function (t) { t.stop(); });
    try { i.ctx.close(); } catch (e) {}
    Module.fireAudio[id] = null;
});

struct FireAudioOutput { int id, frameBytes, rate, discard; };
struct FireAudioInput { int id, frameBytes; };

static int fire_audio_supported(void) { return fire_audiojs_supported(); }
static int fire_audio_devices(FireAudioList* names, FireAudioStatus* st) { fire_audio_listClear(names); if (fire_audiojs_supported()) fire_audio_listAdd(names, "default"); return fire_audio_success(st); }
static int fire_audio_open(const char* name, int rate, int channels, FireAudioOutput** out, FireAudioStatus* st) {
    FireAudioOutput* o;
    int id;
    *out = NULL;
    if (strcmp(name, "default") != 0 && strcmp(name, "0") != 0) return fire_audio_fail(st, FIRE_AUDIO_E_NOT_FOUND, "There is no sound device '%s' (\"default\").", name);
    id = fire_audiojs_open(rate, channels);
    if (id < 0) return fire_audio_fail(st, FIRE_AUDIO_E_UNSUPPORTED, "Web Audio: the browser has no sound output.");
    o = (FireAudioOutput*)calloc(1, sizeof *o);
    if (o == NULL) { fire_audiojs_close(id); return fire_audio_fail(st, FIRE_AUDIO_E_OTHER, "Out of memory."); }
    o->id = id; o->frameBytes = 2 * channels; o->rate = rate;
    *out = o;
    return fire_audio_success(st);
}
static int64_t fire_audio_queued(FireAudioOutput* o) { return (int64_t)fire_audiojs_queuedFrames(o->id) * o->frameBytes; }
static int fire_audio_write(FireAudioOutput* o, const uint8_t* data, int bytes, int* accepted, FireAudioStatus* st) {
    int64_t capacity = (int64_t)(o->rate / 2) * o->frameBytes, room = capacity - fire_audio_queued(o);
    int frames;
    o->discard = 0;
    *accepted = 0;
    if (room <= 0) return fire_audio_success(st);
    frames = (int)((bytes < room ? bytes : room) / o->frameBytes);
    if (frames > 0) { fire_audiojs_write(o->id, data, frames); *accepted = frames * o->frameBytes; }
    return fire_audio_success(st);
}
static void fire_audio_stop(FireAudioOutput* o) { o->discard = 1; fire_audiojs_stop(o->id); }
static void fire_audio_close(FireAudioOutput* o) {
    if (!o) return;
    if (!o->discard) { int waited; for (waited = 0; waited < 3000 && fire_audio_queued(o) > 0; waited += 10) emscripten_sleep(10); }
    fire_audiojs_close(o->id);
    free(o);
}

static int fire_audio_inputSupported(void) { return fire_audiojs_inputSupported(); }
static int fire_audio_inputDevices(FireAudioList* names, FireAudioStatus* st) { fire_audio_listClear(names); if (fire_audiojs_inputSupported()) fire_audio_listAdd(names, "default"); return fire_audio_success(st); }
static int fire_audio_openInput(const char* name, int rate, int channels, FireAudioInput** inOut, FireAudioStatus* st) {
    FireAudioInput* in;
    int id;
    *inOut = NULL;
    if (strcmp(name, "default") != 0 && strcmp(name, "0") != 0) return fire_audio_fail(st, FIRE_AUDIO_E_NOT_FOUND, "There is no sound input '%s' (\"default\").", name);
    if (!fire_audiojs_inputSupported()) return fire_audio_fail(st, FIRE_AUDIO_E_UNSUPPORTED, "The browser offers no microphone here (it needs a secure page: https or localhost).");
    id = fire_audiojs_openInput(rate, channels);
    if (id < 0) return fire_audio_fail(st, FIRE_AUDIO_E_UNSUPPORTED, "Web Audio: the input could not be opened.");
    in = (FireAudioInput*)calloc(1, sizeof *in);
    if (in == NULL) { fire_audiojs_closeInput(id); return fire_audio_fail(st, FIRE_AUDIO_E_OTHER, "Out of memory."); }
    in->id = id; in->frameBytes = 2 * channels;
    *inOut = in;
    return fire_audio_success(st);
}
static int fire_audio_read(FireAudioInput* in, uint8_t* data, int bytes, int* got, FireAudioStatus* st) {
    *got = fire_audiojs_read(in->id, data, bytes / in->frameBytes) * in->frameBytes;
    return fire_audio_success(st);
}
static int64_t fire_audio_available(FireAudioInput* in) { return (int64_t)fire_audiojs_availableFrames(in->id) * in->frameBytes; }
static void fire_audio_closeInput(FireAudioInput* in) { if (!in) return; fire_audiojs_closeInput(in->id); free(in); }

#endif
