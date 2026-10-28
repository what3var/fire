// fire native bridge "audio": the natives behind `#import "audio"` (docs/AUDIO.md): sound output - 16 bit signed samples, mono or stereo, any rate the device takes. The fire side (Audio.Output,
// Audio.Sound, Audio.Sim - fire source) is the same in the VM and in a native build; this file is what its `__Audio...` functions do.
//
// This file is the one implementation of the audio natives: the native build includes it (the package "audio" brings it as its C++ source) and the virtual machine runs it in a shared library built
// from it (native/abi/fire_pkg_abi.h). The devices come from the platform package (FIRE_PLATFORM_AUDIO_HEADER: plat::audio, see platform/std/fire_audio_none.hpp): PulseAudio or ALSA on Linux, winmm on
// Windows, PWM on a GPIO pin of an ESP32. Where there is no sound hardware the list of devices is empty - but one device is always there:
//
//   "sim"   a simulated device that records what is played to it (the bytes, the rate and the channels of the last opened output): for tests, and to try a program on a machine without sound.
//           It plays at once - unless the program holds it (`SimHold`): then it takes up to 4096 bytes and keeps them, as a device does that is slow to play.
//
// An output is an integer handle in a table of this bridge. Every function reports an error the same way: a result of -1/false/undefined and the code and message of this thread
// (`__AudioLastError`, `__AudioLastErrorMessage`), from which the fire code throws a typed exception. Nothing in here waits: `Write` takes what fits and returns the number of bytes taken,
// `Queued` says what is still to be played, and the fire code sleeps in between - so a program can always be aborted.
#pragma once

#include <cmath>
#include <cstddef>
#include <cstdio>
#include <cstring>
#include FIRE_PLATFORM_AUDIO_HEADER
#include <string>
#include <vector>

namespace fire {
namespace audio {

enum Err { None = 0, InvalidArgument = 1, InvalidHandle = 2, NotFound = 3, Busy = 4, Permission = 5, Unsupported = 6, Other = 7 };

#if defined(FIRE_TLS_STRUCT)
#define FIRE_AUDIO_ERROR g_ioError
#else
struct ErrorSlot { int32_t code; char message[176]; };
inline thread_local ErrorSlot t_audioError = {0, {0}};
#define FIRE_AUDIO_ERROR ::fire::audio::t_audioError
#endif

inline int64_t fail(int code, const std::string& message) {
    FIRE_AUDIO_ERROR.code = code;
    size_t n = message.size() < sizeof FIRE_AUDIO_ERROR.message - 1 ? message.size() : sizeof FIRE_AUDIO_ERROR.message - 1;
    std::memcpy(FIRE_AUDIO_ERROR.message, message.data(), n);
    FIRE_AUDIO_ERROR.message[n] = 0;
    return -1;
}
inline void ok() { FIRE_AUDIO_ERROR.code = 0; FIRE_AUDIO_ERROR.message[0] = 0; }
inline int64_t fail(const plat::audio::Status& s) { return fail(s.code, s.message); }

// ---- text -------------------------------------------------------------------------------------------------------------------------------
inline std::string toUtf8(Value v) {
    const Str* s = strOf(v);
    std::string out;
    out.reserve(s->length);
    for (uint32_t i = 0; i < s->length; i++) {
        uint32_t c = s->data[i];
        if (c >= 0xD800 && c <= 0xDBFF && i + 1 < s->length && s->data[i + 1] >= 0xDC00 && s->data[i + 1] <= 0xDFFF) { c = 0x10000 + ((c - 0xD800) << 10) + (s->data[i + 1] - 0xDC00); i++; }
        else if (c >= 0xD800 && c <= 0xDFFF) c = 0xFFFD;
        if (c < 0x80) out.push_back((char)c);
        else if (c < 0x800) { out.push_back((char)(0xC0 | (c >> 6))); out.push_back((char)(0x80 | (c & 0x3F))); }
        else if (c < 0x10000) { out.push_back((char)(0xE0 | (c >> 12))); out.push_back((char)(0x80 | ((c >> 6) & 0x3F))); out.push_back((char)(0x80 | (c & 0x3F))); }
        else { out.push_back((char)(0xF0 | (c >> 18))); out.push_back((char)(0x80 | ((c >> 12) & 0x3F))); out.push_back((char)(0x80 | ((c >> 6) & 0x3F))); out.push_back((char)(0x80 | (c & 0x3F))); }
    }
    return out;
}
inline Value str8(const std::string& text, OwnList* list) {
    Str* s = allocStr((uint32_t)text.size(), list);
    for (size_t i = 0; i < text.size(); i++) strChars(s)[i] = (char16_t)(uint8_t)text[i];
    return StrV(s);
}

// ---- the simulated device ---------------------------------------------------------------------------------------------------------------
enum { SimHoldCapacity = 4096, SimRecordLimit = 16 * 1024 * 1024 };
struct Sim {
    bool hold = false;
    int rate = 0;
    int channels = 0;
    int64_t played = 0;             // the bytes that were played (also those beyond the recording limit)
    std::vector<uint8_t> data;      // what was played (up to SimRecordLimit)
    std::vector<uint8_t> pending;   // what waits while the device is held
    std::vector<uint8_t> input;     // what the simulated input records next (Feed)
    size_t inputAt = 0;
    int64_t recorded = 0;           // the bytes the simulated input handed over

    void play(const uint8_t* bytes, size_t n) {
        played += (int64_t)n;
        size_t room = data.size() < (size_t)SimRecordLimit ? (size_t)SimRecordLimit - data.size() : 0;
        data.insert(data.end(), bytes, bytes + (n < room ? n : room));
    }
    void release() {
        if (!pending.empty()) play(pending.data(), pending.size());
        pending.clear();
    }
};
inline Sim& sim() { static Sim s; return s; }

// ---- the outputs ------------------------------------------------------------------------------------------------------------------------
struct Output {
    bool isSim = false;
    plat::audio::Output* out = nullptr;
    std::string name;
    int rate = 0;
    int channels = 0;
    int frameBytes = 0;
    int volume = 100;               // percent
    std::vector<uint8_t> scaled;    // (the samples with the volume applied)
};
inline void release(Output* o) {
    if (!o) return;
    if (o->out) plat::audio::close(o->out);
    delete o;
}
struct OutputTable {
    std::vector<Output*> items{1, nullptr};
    ~OutputTable() { for (Output* o : items) release(o); }
};
inline std::vector<Output*>& outputs() { static OutputTable table; return table.items; }

// ---- the inputs -------------------------------------------------------------------------------------------------------------------------
struct Input {
    bool isSim = false;
    plat::audio::Input* in = nullptr;
    int rate = 0;
    int channels = 0;
    int frameBytes = 0;
};
inline void release(Input* i) {
    if (!i) return;
    if (i->in) plat::audio::closeInput(i->in);
    delete i;
}
struct InputTable {
    std::vector<Input*> items{1, nullptr};
    ~InputTable() { for (Input* i : items) release(i); }
};
inline std::vector<Input*>& inputs() { static InputTable table; return table.items; }

/// The program has ended (the library for the VM stays loaded for the next one): closes the outputs and inputs and clears the simulated device.
inline void reset() {
    std::vector<Output*>& all = outputs();
    for (size_t i = 1; i < all.size(); i++) {
        release(all[i]);
        all[i] = nullptr;
    }
    std::vector<Input*>& ins = inputs();
    for (size_t i = 1; i < ins.size(); i++) {
        release(ins[i]);
        ins[i] = nullptr;
    }
    sim() = Sim();
}

inline Output* find(Value h) {
    int64_t i = h.i;
    if (i > 0 && (size_t)i < outputs().size() && outputs()[(size_t)i]) return outputs()[(size_t)i];
    fail(InvalidHandle, "Invalid or already closed output handle.");
    return nullptr;
}
inline bool checkRange(Value bufferValue, int64_t offset, int64_t count) {
    int64_t length = bufOf(bufferValue)->length;
    if (offset < 0 || count < 0 || offset > length || count > length - offset) {
        fail(InvalidArgument, "offset/count (" + std::to_string(offset) + "/" + std::to_string(count) + ") are outside of the buffer (length " + std::to_string(length) + ").");
        return false;
    }
    return true;
}

// ---- natives ----------------------------------------------------------------------------------------------------------------------------
inline Value LastError() { return Int(FIRE_AUDIO_ERROR.code); }
inline Value LastErrorMessage(OwnList* list) { return str8(FIRE_AUDIO_ERROR.message, list); }
inline Value OpenCount() {
    int64_t n = 0;
    for (Output* o : outputs()) if (o) n++;
    return Int(n);
}
/// 1 if this machine has a sound output (the simulated device is there anyway), else 0.
inline Value Supported() {
    std::vector<std::string> hardware;
    ok();
    return Int(plat::audio::supported() && plat::audio::devices(hardware).ok() && !hardware.empty() ? 1 : 0);
}

/// The devices: "sim" first, then those of the hardware.
inline Value Devices(OwnList* list) {
    std::vector<std::string> names{"sim"};
    std::vector<std::string> hardware;
    plat::audio::Status st = plat::audio::devices(hardware);
    if (!st.ok()) { fail(st); return Undef(); }
    for (const std::string& n : hardware) names.push_back(n);
    ok();
    Arr* a = allocArr((uint32_t)names.size(), list);
    for (size_t i = 0; i < names.size(); i++) { a->items()[i] = str8(names[i], list); retain(a->items()[i]); }
    return ArrV(a);
}

inline Value Open(Value name, Value rate, Value channels) {
    std::string n = toUtf8(name);
    if (rate.i < 1000 || rate.i > 192000) return Int(fail(InvalidArgument, "The rate must be between 1000 and 192000 Hz."));
    if (channels.i < 1 || channels.i > 2) return Int(fail(InvalidArgument, "Mono (1) or stereo (2) channels."));
    Output* o = new Output();
    o->name = n;
    o->rate = (int)rate.i;
    o->channels = (int)channels.i;
    o->frameBytes = 2 * o->channels;
    if (n == "sim") {
        o->isSim = true;
        sim().rate = o->rate;
        sim().channels = o->channels;
    } else {
        plat::audio::Status st = plat::audio::open(n, o->rate, o->channels, o->out);
        if (!st.ok()) { delete o; return Int(fail(st)); }
    }
    outputs().push_back(o);
    ok();
    return Int((int64_t)outputs().size() - 1);
}

/// Plays `count` bytes (16 bit samples, whole frames) of the buffer: takes what fits and returns the number of bytes taken (0: full, try again later), -1 on an error.
inline Value Write(Value h, Value buffer, Value offset, Value count) {
    if (!leafAlive(buffer)) return destroyedError(buffer);
    Output* o = find(h);
    if (!o) return Int(-1);
    if (!checkRange(buffer, offset.i, count.i)) return Int(-1);
    if (count.i % o->frameBytes != 0) return Int(fail(InvalidArgument, "The sound is made of frames of " + std::to_string(o->frameBytes) + " bytes (" + std::to_string(o->channels) + " channel(s) of 16 bits); " + std::to_string(count.i) + " bytes are not a whole number of them."));
    if (count.i == 0) { ok(); return Int(0); }
    const uint8_t* bytes = bufOf(buffer)->bytes() + offset.i;
    if (o->volume != 100) {
        o->scaled.resize((size_t)count.i);
        for (int64_t i = 0; i + 1 < count.i; i += 2) {
            int16_t s = (int16_t)((uint16_t)bytes[i] | ((uint16_t)bytes[i + 1] << 8));
            int32_t v = (int32_t)s * o->volume / 100;
            o->scaled[(size_t)i] = (uint8_t)(v & 0xFF);
            o->scaled[(size_t)i + 1] = (uint8_t)((v >> 8) & 0xFF);
        }
        bytes = o->scaled.data();
    }
    int64_t taken = 0;
    if (o->isSim) {
        Sim& s = sim();
        if (!s.hold) s.play(bytes, (size_t)count.i), taken = count.i;
        else {
            size_t room = s.pending.size() < (size_t)SimHoldCapacity ? (size_t)SimHoldCapacity - s.pending.size() : 0;
            room -= room % (size_t)o->frameBytes;
            size_t n = (size_t)count.i < room ? (size_t)count.i : room;
            s.pending.insert(s.pending.end(), bytes, bytes + n);
            taken = (int64_t)n;
        }
    } else {
        int accepted = 0;
        plat::audio::Status st = plat::audio::write(o->out, bytes, (int)(count.i > 0x7FFFFFFF ? 0x7FFFFFFF : count.i), accepted);
        if (!st.ok()) return Int(fail(st));
        taken = accepted;
    }
    ok();
    return Int(taken);
}

/// The bytes that were taken and are not played yet; -1 on an error.
inline Value Queued(Value h) {
    Output* o = find(h);
    if (!o) return Int(-1);
    ok();
    if (o->isSim) return Int((int64_t)sim().pending.size());
    return Int(plat::audio::queued(o->out));
}

/// Throws the queued sound away (the output stays open).
inline Value Stop(Value h) {
    Output* o = find(h);
    if (!o) return Bool(false);
    if (o->isSim) sim().pending.clear();
    else plat::audio::stop(o->out);
    ok();
    return Bool(true);
}

/// The volume in percent (0 .. 100); it is applied to the samples that are written afterwards.
inline Value SetVolume(Value h, Value percent) {
    Output* o = find(h);
    if (!o) return Bool(false);
    if (percent.i < 0 || percent.i > 100) { fail(InvalidArgument, "The volume is between 0 and 100 percent."); return Bool(false); }
    o->volume = (int)percent.i;
    ok();
    return Bool(true);
}

/// Closes the output; what was taken is played to its end first (unless `Stop` was called).
inline Value Close(Value h) {
    int64_t i = h.i;
    if (i <= 0 || (size_t)i >= outputs().size() || !outputs()[(size_t)i]) { fail(InvalidHandle, "Invalid or already closed output handle."); return Bool(false); }
    Output* o = outputs()[(size_t)i];
    if (o->isSim) sim().release();
    release(o);
    outputs()[(size_t)i] = nullptr;
    ok();
    return Bool(true);
}

// ---- inputs -----------------------------------------------------------------------------------------------------------------------------
/// 1 if this machine has a sound input (the simulated one is there anyway), else 0.
inline Value InSupported() {
    std::vector<std::string> hardware;
    ok();
    return Int(plat::audio::inputSupported() && plat::audio::inputDevices(hardware).ok() && !hardware.empty() ? 1 : 0);
}

/// The inputs: "sim" first, then those of the hardware.
inline Value InDevices(OwnList* list) {
    std::vector<std::string> names{"sim"};
    std::vector<std::string> hardware;
    plat::audio::Status st = plat::audio::inputDevices(hardware);
    if (!st.ok()) { fail(st); return Undef(); }
    for (const std::string& n : hardware) names.push_back(n);
    ok();
    Arr* a = allocArr((uint32_t)names.size(), list);
    for (size_t i = 0; i < names.size(); i++) { a->items()[i] = str8(names[i], list); retain(a->items()[i]); }
    return ArrV(a);
}

inline Value InOpen(Value name, Value rate, Value channels) {
    std::string n = toUtf8(name);
    if (rate.i < 1000 || rate.i > 192000) return Int(fail(InvalidArgument, "The rate must be between 1000 and 192000 Hz."));
    if (channels.i < 1 || channels.i > 2) return Int(fail(InvalidArgument, "Mono (1) or stereo (2) channels."));
    Input* i = new Input();
    i->rate = (int)rate.i;
    i->channels = (int)channels.i;
    i->frameBytes = 2 * i->channels;
    if (n == "sim") i->isSim = true;
    else {
        plat::audio::Status st = plat::audio::openInput(n, i->rate, i->channels, i->in);
        if (!st.ok()) { delete i; return Int(fail(st)); }
    }
    inputs().push_back(i);
    ok();
    return Int((int64_t)inputs().size() - 1);
}

inline Input* findInput(Value h) {
    int64_t i = h.i;
    if (i > 0 && (size_t)i < inputs().size() && inputs()[(size_t)i]) return inputs()[(size_t)i];
    fail(InvalidHandle, "Invalid or already closed input handle.");
    return nullptr;
}

/// Takes up to `count` bytes (whole frames) of what the input recorded into the buffer from `offset`; the number of bytes (0: nothing yet), -1 on an error.
inline Value InRead(Value h, Value buffer, Value offset, Value count) {
    if (!leafAlive(buffer)) return destroyedError(buffer);
    Input* i = findInput(h);
    if (!i) return Int(-1);
    if (!checkRange(buffer, offset.i, count.i)) return Int(-1);
    int64_t want = count.i - count.i % i->frameBytes;
    uint8_t* bytes = bufOf(buffer)->bytes() + offset.i;
    int got = 0;
    if (i->isSim) {
        Sim& s = sim();
        size_t left = s.input.size() - s.inputAt;
        left -= left % (size_t)i->frameBytes;
        size_t n = (size_t)want < left ? (size_t)want : left;
        if (n > 0) std::memcpy(bytes, s.input.data() + s.inputAt, n);
        s.inputAt += n;
        if (s.inputAt == s.input.size()) { s.input.clear(); s.inputAt = 0; }
        s.recorded += (int64_t)n;
        got = (int)n;
    } else {
        plat::audio::Status st = plat::audio::read(i->in, bytes, (int)(want > 0x7FFFFFFF ? 0x7FFFFFFF : want), got);
        if (!st.ok()) return Int(fail(st));
    }
    ok();
    return Int(got);
}

/// The bytes recorded and not read yet; -1 on an error.
inline Value InAvailable(Value h) {
    Input* i = findInput(h);
    if (!i) return Int(-1);
    ok();
    if (i->isSim) return Int((int64_t)(sim().input.size() - sim().inputAt));
    return Int(plat::audio::available(i->in));
}

inline Value InClose(Value h) {
    int64_t i = h.i;
    if (i <= 0 || (size_t)i >= inputs().size() || !inputs()[(size_t)i]) { fail(InvalidHandle, "Invalid or already closed input handle."); return Bool(false); }
    release(inputs()[(size_t)i]);
    inputs()[(size_t)i] = nullptr;
    ok();
    return Bool(true);
}

// ---- generated waves --------------------------------------------------------------------------------------------------------------------
/// Fills `frames` frames of the buffer (from `offset`, in the channels and the rate given) with a wave: kind 0 sine, 1 square, 2 triangle, 3 saw, 4 noise; `freq` the frequency in Hz (an integer
/// or a float, at most half the rate); `volume` in percent. `phase` is where the wave continues (0 at the start; the result of the last call makes the next buffer join without a click; for noise it is the state
/// of the generator). Returns the phase to go on with, -1 on an error.
inline Value Wave(Value buffer, Value offset, Value frames, Value kind, Value freq, Value volume, Value rate, Value channels, Value phase) {
    if (!leafAlive(buffer)) return destroyedError(buffer);
    if (kind.i < 0 || kind.i > 4) return Int(fail(InvalidArgument, "Unknown wave (0 sine, 1 square, 2 triangle, 3 saw, 4 noise)."));
    const double hz = freq.kind == K_Float ? (double)freq.f : (double)freq.i;
    if (rate.i < 1000 || rate.i > 192000 || channels.i < 1 || channels.i > 2 || volume.i < 0 || volume.i > 100 || frames.i < 0)
        return Int(fail(InvalidArgument, "Invalid rate, channels, volume or number of frames."));
    if (!(hz >= 0.0) || hz > (double)rate.i / 2.0) return Int(fail(InvalidArgument, "The frequency must be between 0 and half of the rate (" + std::to_string(rate.i / 2) + " Hz)."));
    int frameBytes = (int)(2 * channels.i);
    if (!checkRange(buffer, offset.i, frames.i * frameBytes)) return Int(-1);
    uint8_t* bytes = bufOf(buffer)->bytes() + offset.i;
    uint32_t p = (uint32_t)phase.i;
    const uint32_t increment = (uint32_t)(hz / (double)rate.i * 4294967296.0);
    const double amplitude = 32767.0 * (double)volume.i / 100.0;
    const double twoPi = 6.283185307179586;
    for (int64_t f = 0; f < frames.i; f++) {
        double v;   // -1 .. +1
        switch (kind.i) {
            case 0: v = std::sin(twoPi * ((double)p / 4294967296.0)); break;
            case 1: v = p < 0x80000000u ? 1.0 : -1.0; break;
            case 2: { double t = (double)p / 4294967296.0; v = t < 0.5 ? 4.0 * t - 1.0 : 3.0 - 4.0 * t; break; }
            case 3: v = 2.0 * ((double)p / 4294967296.0) - 1.0; break;
            default: {
                if (p == 0) p = 0x12345678u;
                p ^= p << 13; p ^= p >> 17; p ^= p << 5;   // xorshift32
                v = (double)(int32_t)p / 2147483648.0;
                break;
            }
        }
        int32_t s = (int32_t)(v * amplitude);
        for (int c = 0; c < channels.i; c++) {
            bytes[f * frameBytes + 2 * c] = (uint8_t)(s & 0xFF);
            bytes[f * frameBytes + 2 * c + 1] = (uint8_t)((s >> 8) & 0xFF);
        }
        if (kind.i != 4) p += increment;
    }
    ok();
    return Int((int64_t)p);
}

// ---- the simulated device from the outside ----------------------------------------------------------------------------------------------
/// Forgets everything that was played and what the simulated input had; the device plays again (not held). Open outputs and inputs stay open.
inline Value SimReset() {
    sim() = Sim();
    ok();
    return Bool(true);
}
/// Holds the device (true): it takes at most 4096 bytes and keeps them; releases it (false): the kept bytes are played.
inline Value SimHold(Value hold) {
    sim().hold = hold.i != 0;
    if (!sim().hold) sim().release();
    ok();
    return Bool(true);
}
/// Gives the simulated input sound to record: `count` bytes (16 bit samples in the channels of the input that reads them) from `offset` of the buffer.
inline Value SimFeed(Value buffer, Value offset, Value count) {
    if (!leafAlive(buffer)) return destroyedError(buffer);
    if (!checkRange(buffer, offset.i, count.i)) return Int(-1);
    const uint8_t* bytes = bufOf(buffer)->bytes() + offset.i;
    sim().input.insert(sim().input.end(), bytes, bytes + count.i);
    ok();
    return Int(count.i);
}
/// What the device knows: 0 the bytes played, 1 the rate of the last opened output, 2 its channels, 3 the bytes that wait (held), 4 the bytes the simulated
/// input still has, 5 the bytes it handed over.
inline Value SimInfo(Value what) {
    ok();
    switch (what.i) {
        case 0: return Int(sim().played);
        case 1: return Int(sim().rate);
        case 2: return Int(sim().channels);
        case 3: return Int((int64_t)sim().pending.size());
        case 4: return Int((int64_t)(sim().input.size() - sim().inputAt));
        case 5: return Int(sim().recorded);
        default: fail(InvalidArgument, "Unknown information."); return Int(-1);
    }
}
/// The bytes that were played (a buffer; at most 16 MB are recorded).
inline Value SimData(OwnList* list) {
    Buf* b = allocBuf((uint32_t)sim().data.size(), list);
    if (!sim().data.empty()) std::memcpy(b->bytes(), sim().data.data(), sim().data.size());
    ok();
    return BufV(b);
}

}  // namespace audio
}  // namespace fire
