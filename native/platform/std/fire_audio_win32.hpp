// fire native platform layer, audio part on Windows: the waveOut API of winmm (every Windows has it; the system mixes and resamples). The device "default" is the wave mapper (the default
// device of the user); the other names are those that Windows reports for the devices (or their numbers). The interface is the one of fire_audio_none.hpp. (Link: winmm.)
//
// The sound is handed over in blocks of 50 ms that the driver plays one after the other; `write` fills the free blocks and returns at once, a block is free again when the driver sets its
// DONE flag. So nothing in here blocks and no thread or callback is needed.
#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <mmsystem.h>

#include <cstdint>
#include <cstring>
#include <string>
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

constexpr int kBlocks = 32;

struct Block {
    WAVEHDR header;
    std::vector<uint8_t> data;
    bool submitted = false;
};

struct Output {
    HWAVEOUT handle = nullptr;
    int frameBytes = 4;
    int blockBytes = 0;
    int bytesPerSecond = 0;
    bool discard = false;
    Block blocks[kBlocks];
    int next = 0;   // the block the next write fills (they are used in a ring)
};

inline bool supported() { return waveOutGetNumDevs() > 0; }

inline std::string deviceName(UINT index) {
    WAVEOUTCAPSA caps;
    if (waveOutGetDevCapsA(index, &caps, sizeof caps) != MMSYSERR_NOERROR) return "";
    return caps.szPname;
}

inline Status devices(std::vector<std::string>& names) {
    names.clear();
    UINT count = waveOutGetNumDevs();
    if (count == 0) return success();
    names.push_back("default");
    for (UINT i = 0; i < count; i++) {
        std::string n = deviceName(i);
        if (!n.empty()) names.push_back(n);
    }
    return success();
}

inline std::string errorText(MMRESULT r) {
    char text[MAXERRORLENGTH] = {0};
    if (waveOutGetErrorTextA(r, text, sizeof text) != MMSYSERR_NOERROR) return "error " + std::to_string((int)r);
    return text;
}

inline bool isDone(Block& b) { return !b.submitted || (b.header.dwFlags & WHDR_DONE) != 0; }

inline void release(Output* o, Block& b) {
    if (b.submitted) {
        waveOutUnprepareHeader(o->handle, &b.header, sizeof(WAVEHDR));
        b.submitted = false;
    }
}

inline Status open(const std::string& name, int rate, int channels, Output*& out) {
    out = nullptr;
    UINT device = WAVE_MAPPER;
    if (name != "default") {
        UINT count = waveOutGetNumDevs();
        bool found = false;
        for (UINT i = 0; i < count && !found; i++) if (deviceName(i) == name) { device = i; found = true; }
        if (!found && !name.empty() && name.find_first_not_of("0123456789") == std::string::npos && (UINT)std::stoi(name) < count) { device = (UINT)std::stoi(name); found = true; }
        if (!found) return fail(E_NotFound, "There is no sound device '" + name + "' (\"default\" or one of the names that Audio.Board.Devices() lists).");
    }
    WAVEFORMATEX format;
    std::memset(&format, 0, sizeof format);
    format.wFormatTag = WAVE_FORMAT_PCM;
    format.nChannels = (WORD)channels;
    format.nSamplesPerSec = (DWORD)rate;
    format.wBitsPerSample = 16;
    format.nBlockAlign = (WORD)(channels * 2);
    format.nAvgBytesPerSec = (DWORD)(rate * channels * 2);
    HWAVEOUT handle = nullptr;
    MMRESULT r = waveOutOpen(&handle, device, &format, 0, 0, CALLBACK_NULL);
    if (r != MMSYSERR_NOERROR) {
        int code = (r == MMSYSERR_ALLOCATED) ? E_Busy : (r == MMSYSERR_BADDEVICEID || r == MMSYSERR_NODRIVER) ? E_NotFound : (r == WAVERR_BADFORMAT) ? E_Unsupported : E_Other;
        return fail(code, "waveOut: " + errorText(r));
    }
    Output* o = new Output();
    o->handle = handle;
    o->frameBytes = channels * 2;
    o->bytesPerSecond = rate * channels * 2;
    o->blockBytes = o->bytesPerSecond / 20;   // 50 ms
    o->blockBytes -= o->blockBytes % o->frameBytes;
    if (o->blockBytes < o->frameBytes) o->blockBytes = o->frameBytes;
    for (Block& b : o->blocks) { b.data.resize((size_t)o->blockBytes); std::memset(&b.header, 0, sizeof b.header); }
    out = o;
    return success();
}

inline Status write(Output* o, const uint8_t* data, int bytes, int& accepted) {
    accepted = 0;
    o->discard = false;
    bytes -= bytes % o->frameBytes;
    while (bytes > 0) {
        Block& b = o->blocks[o->next];
        if (!isDone(b)) break;   // all blocks are queued: the rest has to wait
        release(o, b);
        int n = bytes < o->blockBytes ? bytes : o->blockBytes;
        std::memcpy(b.data.data(), data + accepted, (size_t)n);
        std::memset(&b.header, 0, sizeof b.header);
        b.header.lpData = reinterpret_cast<LPSTR>(b.data.data());
        b.header.dwBufferLength = (DWORD)n;
        MMRESULT r = waveOutPrepareHeader(o->handle, &b.header, sizeof(WAVEHDR));
        if (r == MMSYSERR_NOERROR) r = waveOutWrite(o->handle, &b.header, sizeof(WAVEHDR));
        if (r != MMSYSERR_NOERROR) {
            if (r != MMSYSERR_NOERROR && (b.header.dwFlags & WHDR_PREPARED)) waveOutUnprepareHeader(o->handle, &b.header, sizeof(WAVEHDR));
            return fail(E_Other, "waveOut: " + errorText(r));
        }
        b.submitted = true;
        o->next = (o->next + 1) % kBlocks;
        accepted += n;
        bytes -= n;
    }
    return success();
}

inline int64_t queued(Output* o) {
    int64_t total = 0;
    for (Block& b : o->blocks) if (!isDone(b)) total += b.header.dwBufferLength;
    return total;
}

inline void stop(Output* o) {
    o->discard = true;
    waveOutReset(o->handle);   // returns all blocks (DONE) at once
}

inline void close(Output* o) {
    if (!o) return;
    if (!o->discard) {
        // plays what was accepted (at most kBlocks * 50 ms)
        for (int waited = 0; waited < 2500 && queued(o) > 0; waited += 5) Sleep(5);
    }
    waveOutReset(o->handle);
    for (Block& b : o->blocks) release(o, b);
    waveOutClose(o->handle);
    delete o;
}

}  // namespace audio
}  // namespace plat
}  // namespace fire
