// fire native bridge "tracker": the natives behind `#import "tracker"` (docs/TRACKER.md): a player for ProTracker modules (.mod, 4 to 32 channels) - the music of the Amiga demos and games, made of
// short recorded samples instead of the synthesizer voices that MIDI leaves to the sound card (which is why a module sounds the same everywhere). The fire side (Tracker.Song, Tracker.Player -
// fire source) is the same in the VM and in a native build; this file is what its `__Tracker...` functions do.
//
// This file is the one implementation: the native build includes it (the package "tracker" brings it as its C++ source) and the virtual machine runs it in a shared library built from it
// (native/abi/fire_pkg_abi.h). It has two parts: the replayer + mixer (`fire::tracker::Song`, plain C++ that knows nothing of fire - tested on its own against other players, define
// FIRE_TRACKER_ENGINE_ONLY to get only that part) and the natives that wrap it. The mixer renders 16 bit samples into a buffer; playing them is the job of the `audio` package.
//
// What it does like ProTracker 2.3: the period table with the 16 finetunes; the effects 0 arpeggio, 1/2 slides, 3 tone portamento, 4 vibrato, 5/6 the combinations with volume slide, 7 tremolo,
// 9 sample offset, A volume slide, B position jump, C volume, D pattern break, E1/E2 fine slides, E3 glissando, E4/E7 waveforms, E5 finetune, E6 pattern loop, E9 retrigger, EA/EB fine volume,
// EC note cut, ED note delay, EE pattern delay, F speed/tempo (E0 filter, E8 and 8xx panning, EF funk repeat do nothing). Channels 0 and 3 (and so on) play left, 1 and 2 right, like the Amiga;
// the stereo separation is adjustable. Samples are played with linear interpolation (the Amiga did not interpolate: the option can switch it off), volume changes and cut-off samples are smoothed over
// about 1.5 ms so that nothing clicks. All arithmetic is integer, so the VM and a native build render the same samples.
#pragma once

#include <cstddef>
#include <cstdint>
#include <cstring>
#include <memory>
#include <string>
#include <vector>

namespace fire {
namespace tracker {

enum Err { None = 0, InvalidArgument = 1, InvalidHandle = 2, NotFound = 3, Busy = 4, Permission = 5, Unsupported = 6, Other = 7, BadFormat = 8 };

constexpr int kRows = 64;
constexpr int kMaxChannels = 32;

// The periods of the notes C-1 .. B-3 for the 16 finetunes (0..7 are +0..+7, 8..15 are -8..-1), as in ProTracker.
static const int16_t kPeriods[16][36] = {
    {856,808,762,720,678,640,604,570,538,508,480,453,428,404,381,360,339,320,302,285,269,254,240,226,214,202,190,180,170,160,151,143,135,127,120,113},
    {850,802,757,715,674,637,601,567,535,505,477,450,425,401,379,357,337,318,300,284,268,253,239,225,213,201,189,179,169,159,150,142,134,126,119,113},
    {844,796,752,709,670,632,597,563,532,502,474,447,422,398,376,355,335,316,298,282,266,251,237,224,211,199,188,177,167,158,149,141,133,125,118,112},
    {838,791,746,704,665,628,592,559,528,498,470,444,419,395,373,352,332,314,296,280,264,249,235,222,209,198,187,176,166,157,148,140,132,125,118,111},
    {832,785,741,699,660,623,588,555,524,495,467,441,416,392,370,350,330,312,294,278,262,247,233,220,208,196,185,175,165,156,147,139,131,124,117,110},
    {826,779,736,694,655,619,584,551,520,491,463,437,413,390,368,347,328,309,292,276,260,245,232,219,206,195,184,174,164,155,146,138,130,123,116,109},
    {820,774,730,689,651,614,580,547,516,487,460,434,410,387,365,345,325,307,290,274,258,244,230,217,205,193,183,172,163,154,145,137,129,122,115,109},
    {814,768,725,684,646,610,575,543,513,484,457,431,407,384,363,342,323,305,288,272,256,242,228,216,204,192,181,171,161,152,144,136,128,121,114,108},
    {907,856,808,762,720,678,640,604,570,538,508,480,453,428,404,381,360,339,320,302,285,269,254,240,226,214,202,190,180,170,160,151,143,135,127,120},
    {900,850,802,757,715,675,636,601,567,535,505,477,450,425,401,379,357,337,318,300,284,268,253,238,225,212,200,189,179,169,159,150,142,134,126,119},
    {894,844,796,752,709,670,632,597,563,532,502,474,447,422,398,376,355,335,316,298,282,266,251,237,223,211,199,188,177,167,158,149,141,133,125,118},
    {887,838,791,746,704,665,628,592,559,528,498,470,444,419,395,373,352,332,314,296,280,264,249,235,222,209,198,187,176,166,157,148,140,132,125,118},
    {881,832,785,741,699,660,623,588,555,524,494,467,441,416,392,370,350,330,312,294,278,262,247,233,220,208,196,185,175,165,156,147,139,131,123,117},
    {875,826,779,736,694,655,619,584,551,520,491,463,437,413,390,368,347,328,309,292,276,260,245,232,219,206,195,184,174,164,155,146,138,130,123,116},
    {868,820,774,730,689,651,614,580,547,516,487,460,434,410,387,365,345,325,307,290,274,258,244,230,217,205,193,183,172,163,154,145,137,129,122,115},
    {862,814,768,725,684,646,610,575,543,513,484,457,431,407,384,363,342,323,305,288,272,256,242,228,216,203,192,181,171,161,152,144,136,128,121,114},
};
static const uint8_t kVibratoTable[32] = {
    0x00, 0x18, 0x31, 0x4A, 0x61, 0x78, 0x8D, 0xA1, 0xB4, 0xC5, 0xD4, 0xE0, 0xEB, 0xF4, 0xFA, 0xFD,
    0xFF, 0xFD, 0xFA, 0xF4, 0xEB, 0xE0, 0xD4, 0xC5, 0xB4, 0xA1, 0x8D, 0x78, 0x61, 0x4A, 0x31, 0x18};

constexpr int64_t kPaulaClock = 3546895;   // the PAL Amiga: 7093789.2 Hz / 2 = the rate at which a sample byte is played is this divided by the period

struct Sample {
    std::string name;
    int length = 0;          // bytes
    int finetune = 0;        // 0..15 as stored
    int volume = 0;          // 0..64
    int loopStart = 0;       // bytes
    int loopLength = 0;      // bytes; 0 when the sample does not loop
    std::vector<int8_t> data;
};

struct Cell {
    uint8_t sample = 0;
    uint16_t period = 0;
    uint8_t effect = 0;
    uint8_t param = 0;
};

struct Module {
    std::string title;
    int channels = 4;
    int songLength = 0;
    int restart = 0;
    uint8_t orders[128] = {0};
    int patterns = 0;
    std::vector<Cell> cells;   // patterns * 64 rows * channels
    Sample samples[31];

    const Cell& cell(int pattern, int row, int channel) const { return cells[((size_t)pattern * kRows + (size_t)row) * (size_t)channels + (size_t)channel]; }
};

inline std::string cleanText(const uint8_t* p, size_t n) {
    std::string s;
    for (size_t i = 0; i < n && p[i]; i++) s.push_back(p[i] >= 32 && p[i] < 127 ? (char)p[i] : ' ');
    while (!s.empty() && s.back() == ' ') s.pop_back();
    return s;
}

/// The number of channels a module with this signature has, 0 if it is not one we know.
inline int channelsOfSignature(const uint8_t* s) {
    auto is = [&](const char* t) { return std::memcmp(s, t, 4) == 0; };
    if (is("M.K.") || is("M!K!") || is("FLT4") || is("4CHN")) return 4;
    if (is("6CHN")) return 6;
    if (is("8CHN") || is("CD81") || is("OCTA")) return 8;
    if (s[0] >= '1' && s[0] <= '9' && s[1] == 'C' && s[2] == 'H' && s[3] == 'N') return s[0] - '0';
    if (s[0] >= '1' && s[0] <= '9' && s[1] >= '0' && s[1] <= '9' && ((s[2] == 'C' && s[3] == 'H') || (s[2] == 'C' && s[3] == 'N'))) {
        int n = (s[0] - '0') * 10 + (s[1] - '0');
        return n >= 10 && n <= kMaxChannels ? n : 0;
    }
    return 0;
}

/// Reads a module. Returns false and says why in `error` (the code in `code`).
inline bool parseModule(const uint8_t* d, size_t size, Module& m, std::string& error, int& code) {
    code = BadFormat;
    if (size < 1084) { error = "This is not a module: the file is too short."; return false; }
    int channels = channelsOfSignature(d + 1080);
    if (channels == 0) {
        if (std::memcmp(d + 1080, "FLT8", 4) == 0) { error = "FLT8 (Startrekker 8 channel) modules are not supported."; code = Unsupported; return false; }
        error = "This is not a ProTracker module (the signature at byte 1080 is not one of M.K., 4CHN, 6CHN, 8CHN, xxCH ...).";
        return false;
    }
    m = Module();
    m.channels = channels;
    m.title = cleanText(d, 20);
    for (int i = 0; i < 31; i++) {
        const uint8_t* h = d + 20 + i * 30;
        Sample& s = m.samples[i];
        s.name = cleanText(h, 22);
        s.length = ((h[22] << 8) | h[23]) * 2;
        s.finetune = h[24] & 15;
        s.volume = h[25] > 64 ? 64 : h[25];
        s.loopStart = ((h[26] << 8) | h[27]) * 2;
        int loopLen = ((h[28] << 8) | h[29]) * 2;
        s.loopLength = loopLen > 2 ? loopLen : 0;
    }
    m.songLength = d[950];
    m.restart = d[951];
    std::memcpy(m.orders, d + 952, 128);
    if (m.songLength < 1 || m.songLength > 128) m.songLength = m.songLength == 0 ? 1 : 128;
    int maxAll = 0, maxSong = 0;
    for (int i = 0; i < 128; i++) { if (m.orders[i] > maxAll) maxAll = m.orders[i]; if (i < m.songLength && m.orders[i] > maxSong) maxSong = m.orders[i]; }
    size_t patternBytes = (size_t)kRows * (size_t)channels * 4;
    int patterns = maxAll + 1;
    if (1084 + (size_t)patterns * patternBytes > size) patterns = maxSong + 1;   // (a table with leftovers behind the song)
    if (1084 + (size_t)patterns * patternBytes > size) { error = "The module is truncated (its patterns are not all there)."; return false; }
    m.patterns = patterns;
    m.cells.resize((size_t)patterns * kRows * (size_t)channels);
    const uint8_t* p = d + 1084;
    for (size_t i = 0; i < m.cells.size(); i++, p += 4) {
        Cell& c = m.cells[i];
        c.sample = (uint8_t)((p[0] & 0xF0) | (p[2] >> 4));
        c.period = (uint16_t)(((p[0] & 0x0F) << 8) | p[1]);
        c.effect = p[2] & 0x0F;
        c.param = p[3];
        if (c.sample > 31) c.sample = 0;
    }
    size_t pos = 1084 + (size_t)patterns * patternBytes;
    for (int i = 0; i < 31; i++) {
        Sample& s = m.samples[i];
        if (s.length <= 2) { s.length = 0; s.loopLength = 0; continue; }
        s.data.assign((size_t)s.length, 0);
        size_t avail = pos < size ? size - pos : 0;
        size_t n = avail < (size_t)s.length ? avail : (size_t)s.length;   // (a truncated last sample is played as far as it goes)
        if (n) std::memcpy(s.data.data(), d + pos, n);
        pos += (size_t)s.length;
        if (s.loopLength > 0) {
            if (s.loopStart >= s.length) s.loopLength = 0;
            else if (s.loopStart + s.loopLength > s.length) s.loopLength = s.length - s.loopStart;
            if (s.loopLength <= 2) s.loopLength = 0;
        }
    }
    code = None;
    return true;
}

struct Channel {
    int sample = 0;             // 1..31, 0: none yet
    int period = 0;             // the period of the note (without vibrato and arpeggio)
    int mixPeriod = 0;          // what is played this tick
    int portaTarget = 0;
    int portaSpeed = 0;
    int volume = 0;             // 0..64
    int mixVolume = 0;          // with tremolo
    int finetune = 0;
    int vibratoSpeed = 0, vibratoDepth = 0, vibratoPos = 0, vibratoWave = 0;
    int tremoloSpeed = 0, tremoloDepth = 0, tremoloPos = 0, tremoloWave = 0;
    int sampleOffset = 0;
    int loopRow = 0, loopCount = 0;
    bool glissando = false;
    int effect = 0, param = 0;
    bool pendingNote = false;   // ED: the note waits
    int pendingPeriod = 0;
    bool active = false;
    uint64_t pos = 0;           // 32.32 fixed point, in sample bytes
    // mixer
    int32_t curVol = 0;         // volume * 256, moves towards the wanted volume
    int32_t lastValue = 0;      // the last value this channel gave (for the fade out of a cut sample)
    int32_t fade = 0;           // the value that fades out ...
    int fadeLeft = 0;           // ... and the samples it still has
};

class Song {
public:
    std::shared_ptr<const Module> module;
    int rate = 44100;
    bool loop = false;
    int separation = 50;        // 0 (mono) .. 100 (the hard panning of the Amiga)
    bool interpolate = true;
    int gain = 100;             // percent
    uint32_t muted = 0;         // bit n: channel n is silent

    // position
    int order = 0, row = 0, tick = 0;
    int speed = 6, tempo = 125;
    bool finished = false;
    bool repeatRow = false;     // the row is repeated by a pattern delay (the notes are not read again)
    int patternDelay = 0;
    bool jump = false;          // B, D or E6 asked for a jump
    int jumpOrder = 0, jumpRow = 0;
    bool jumpOrderSet = false;
    std::vector<uint8_t> visited;
    Channel ch[kMaxChannels];
    int64_t tickRemainder = 0;
    int tickFrames = 0;         // the frames of the tick that is being played
    int tickLeft = 0;

    Song() {}
    explicit Song(std::shared_ptr<const Module> m, int sampleRate) : module(std::move(m)), rate(sampleRate) { restart(); }

    void restart() {
        const Module& m = *module;
        order = 0; row = 0; tick = 0; speed = 6; tempo = 125;
        finished = false; repeatRow = false; patternDelay = 0; jump = false; jumpOrderSet = false; loopJump = false; jumpRow = 0;
        visited.assign((size_t)128 * kRows, 0);
        for (int i = 0; i < kMaxChannels; i++) ch[i] = Channel();
        for (int i = 0; i < m.channels; i++) ch[i].finetune = 0;
        tickRemainder = 0; tickFrames = 0; tickLeft = 0;
        visited[0] = 1;
    }

    // ---- the replayer --------------------------------------------------------------------------------------------------------------------------
    static int noteIndex(int period) {
        int i = 0;
        while (i < 35 && period < kPeriods[0][i]) i++;   // (the table falls: the first note that is not higher)
        return i;
    }
    int fineOf(const Channel& c) const { return c.finetune & 15; }

    void trigger(Channel& c, int period, bool withOffset) {
        c.period = kPeriods[fineOf(c)][noteIndex(period)];
        c.pos = 0;
        if (c.sample > 0 && c.sample <= 31 && module->samples[c.sample - 1].length > 0) {
            c.active = true;
            if (withOffset) c.pos = (uint64_t)((uint64_t)c.sampleOffset << 8) << 32;
        } else c.active = false;
        if (c.active && (c.pos >> 32) >= (uint64_t)module->samples[c.sample - 1].length) {
            const Sample& s = module->samples[c.sample - 1];
            if (s.loopLength == 0) c.active = false;   // an offset behind the end of a sample that does not loop: silence
            else c.pos = (uint64_t)s.loopStart << 32;
        }
        if ((c.vibratoWave & 4) == 0) c.vibratoPos = 0;
        if ((c.tremoloWave & 4) == 0) c.tremoloPos = 0;
        c.fade = c.lastValue;      // the sample that was playing fades out instead of being cut
        c.fadeLeft = rampLength();
        c.curVol = 0;              // the new one fades in
    }

    int rampLength() const { int n = rate / 700; return n < 8 ? 8 : n; }

    void readRow() {
        const Module& m = *module;
        int pattern = m.orders[order];
        for (int i = 0; i < m.channels; i++) {
            Channel& c = ch[i];
            const Cell& cell = m.cell(pattern, row, i);
            if (cell.sample) {
                c.sample = cell.sample;
                const Sample& s = m.samples[cell.sample - 1];
                c.volume = s.volume;
                c.finetune = s.finetune;
            }
            c.effect = cell.effect;
            c.param = cell.param;
            int x = cell.param >> 4, y = cell.param & 15;
            c.pendingNote = false;
            if (cell.effect == 9 && cell.param) c.sampleOffset = cell.param;
            if (cell.effect == 0xE && x == 0x5) c.finetune = y;   // (ProTracker sets the finetune before it looks up the period of the note on this row)
            if (cell.period) {
                int adjusted = kPeriods[fineOf(c)][noteIndex(cell.period)];
                if (cell.effect == 3 || cell.effect == 5) c.portaTarget = adjusted;
                else if (cell.effect == 0xE && x == 0xD && y > 0) { c.pendingNote = true; c.pendingPeriod = cell.period; }
                else trigger(c, cell.period, cell.effect == 9);
            }
            tickZeroEffect(c, i, x, y);
        }
    }

    void tickZeroEffect(Channel& c, int index, int x, int y) {
        (void)index;
        switch (c.effect) {
            case 3: if (c.param) c.portaSpeed = c.param; break;
            case 4:
                if (x) c.vibratoSpeed = x;
                if (y) c.vibratoDepth = y;
                break;
            case 7:
                if (x) c.tremoloSpeed = x;
                if (y) c.tremoloDepth = y;
                break;
            case 0xB: jump = true; jumpOrder = c.param; jumpOrderSet = true; break;
            case 0xC: c.volume = c.param > 64 ? 64 : c.param; break;
            case 0xD: {
                int target = (c.param >> 4) * 10 + (c.param & 15);
                jump = true;
                jumpRow = target > 63 ? 0 : target;   // (without a B on the same row the song goes on with the next order)
                break;
            }
            case 0xF:
                if (c.param == 0) finished = true;   // F00 stops the song
                else if (c.param < 32) speed = c.param;
                else tempo = c.param;
                break;
            case 0xE:
                switch (x) {
                    case 0x1: c.period -= y; if (c.period < 113) c.period = 113; break;
                    case 0x2: c.period += y; if (c.period > 856) c.period = 856; break;
                    case 0x3: c.glissando = y != 0; break;
                    case 0x4: c.vibratoWave = y & 7; break;
                    case 0x5: c.finetune = y; break;
                    case 0x6:
                        if (y == 0) c.loopRow = row;
                        else {
                            if (c.loopCount == 0) c.loopCount = y;
                            else if (--c.loopCount == 0) break;
                            jump = true;
                            jumpOrder = order;
                            jumpOrderSet = true;
                            jumpRow = c.loopRow;
                            loopJump = true;
                        }
                        break;
                    case 0x7: c.tremoloWave = y & 7; break;
                    case 0xA: c.volume += y; if (c.volume > 64) c.volume = 64; break;
                    case 0xB: c.volume -= y; if (c.volume < 0) c.volume = 0; break;
                    case 0xC: if (y == 0) c.volume = 0; break;
                    case 0xE: if (patternDelay == 0 && !repeatRow) patternDelay = y; break;
                    default: break;
                }
                break;
            default: break;
        }
    }
    bool loopJump = false;

    static int waveValue(int wave, int pos) {
        switch (wave & 3) {
            case 0: return kVibratoTable[pos & 31];
            case 1: { int t = (pos & 31) * 8; return (pos & 32) ? 255 - t : t; }
            default: return 255;
        }
    }

    void slideVolume(Channel& c) {
        int x = c.param >> 4, y = c.param & 15;
        if (x) { c.volume += x; if (c.volume > 64) c.volume = 64; }
        else { c.volume -= y; if (c.volume < 0) c.volume = 0; }
    }
    void tonePortamento(Channel& c) {
        if (c.portaTarget == 0) return;
        if (c.period > c.portaTarget) { c.period -= c.portaSpeed; if (c.period <= c.portaTarget) { c.period = c.portaTarget; c.portaTarget = 0; } }
        else if (c.period < c.portaTarget) { c.period += c.portaSpeed; if (c.period >= c.portaTarget) { c.period = c.portaTarget; c.portaTarget = 0; } }
    }
    int vibratoDelta(Channel& c) {
        int amp = waveValue(c.vibratoWave, c.vibratoPos);
        int delta = (amp * c.vibratoDepth) >> 7;
        int signedDelta = (c.vibratoPos & 32) ? -delta : delta;
        c.vibratoPos = (c.vibratoPos + c.vibratoSpeed) & 63;
        return signedDelta;
    }

    /// The effects that work on every tick but the first of a row.
    void continuous(Channel& c) {
        int x = c.param >> 4, y = c.param & 15;
        switch (c.effect) {
            case 0:
                if (c.param) {
                    int step = tick % 3;
                    int offset = step == 0 ? 0 : step == 1 ? x : y;
                    int i = noteIndex(c.period) + offset;
                    c.mixPeriod = kPeriods[fineOf(c)][i > 35 ? 35 : i];
                }
                break;
            case 1: c.period -= c.param; if (c.period < 113) c.period = 113; break;
            case 2: c.period += c.param; if (c.period > 856) c.period = 856; break;
            case 3: tonePortamento(c); break;
            case 4: c.mixPeriod = c.period + vibratoDelta(c); break;
            case 5: tonePortamento(c); slideVolume(c); break;
            case 6: c.mixPeriod = c.period + vibratoDelta(c); slideVolume(c); break;
            case 7: {
                int amp = waveValue(c.tremoloWave, c.tremoloPos);
                int delta = (amp * c.tremoloDepth) >> 6;
                int v = c.volume + ((c.tremoloPos & 32) ? -delta : delta);
                c.mixVolume = v < 0 ? 0 : v > 64 ? 64 : v;
                c.tremoloPos = (c.tremoloPos + c.tremoloSpeed) & 63;
                break;
            }
            case 0xA: slideVolume(c); break;
            case 0xE:
                if (x == 0x9 && y > 0 && tick % y == 0) { c.pos = 0; c.active = c.sample > 0 && module->samples[c.sample - 1].length > 0; c.fade = c.lastValue; c.fadeLeft = rampLength(); c.curVol = 0; }
                else if (x == 0xC && tick == y) c.volume = 0;
                else if (x == 0xD && c.pendingNote && tick == y) { c.pendingNote = false; trigger(c, c.pendingPeriod, false); }
                break;
            default: break;
        }
    }

    /// Runs one tick of the replayer: after it the channels hold what is played for the next `tickFrames` samples.
    void processTick() {
        const Module& m = *module;
        for (int i = 0; i < m.channels; i++) { ch[i].mixPeriod = 0; ch[i].mixVolume = -1; }   // (what the effects of this tick do not change is the plain period and volume)
        if (tick == 0 && !repeatRow) readRow();
        if (tick > 0 || repeatRow) for (int i = 0; i < m.channels; i++) continuous(ch[i]);
        for (int i = 0; i < m.channels; i++) {
            Channel& c = ch[i];
            if (c.mixPeriod == 0) {
                c.mixPeriod = c.period;
                if (c.glissando && (c.effect == 3 || c.effect == 5)) c.mixPeriod = kPeriods[fineOf(c)][noteIndex(c.period)];   // glissando: the portamento steps from note to note
            }
            if (c.mixVolume < 0) c.mixVolume = c.volume;
        }
        // the length of the tick: rate * 2.5 / tempo samples, with the remainder carried over (so the tempo is exact over time)
        int64_t num = (int64_t)rate * 5 + tickRemainder;
        int64_t den = 2 * (int64_t)tempo;
        tickFrames = (int)(num / den);
        tickRemainder = num % den;
        tickLeft = tickFrames;
        advance();
    }

    /// Moves to the next tick, and at the end of a row to the next row.
    void advance() {
        tick++;
        if (tick < speed) return;
        tick = 0;
        if (patternDelay > 0) { patternDelay--; repeatRow = true; return; }
        repeatRow = false;
        const Module& m = *module;
        bool jumped = jump;
        int newOrder = order, newRow = row + 1;
        if (jump) {
            newOrder = jumpOrderSet ? jumpOrder : order + 1;
            newRow = jumpRow;
        }
        bool wasLoopJump = loopJump;
        jump = false; jumpOrderSet = false; loopJump = false; jumpRow = 0;
        if (newRow >= kRows) { newRow = 0; newOrder++; }
        if (newOrder >= m.songLength) {
            if (loop) { newOrder = m.restart < m.songLength ? m.restart : 0; newRow = jumped ? newRow : 0; }
            else { finished = true; return; }
        }
        if (!wasLoopJump && !loop) {
            size_t at = (size_t)newOrder * kRows + (size_t)newRow;
            if (jumped && visited[at]) { finished = true; return; }   // the song jumps back to where it has been: it would play for ever
        }
        order = newOrder;
        row = newRow;
        visited[(size_t)order * kRows + (size_t)row] = 1;
    }

    // ---- the mixer -----------------------------------------------------------------------------------------------------------------------------
    /// Mixes `frames` frames of the current tick into `out` (stereo: 2 values per frame; mono: 1). Returns after the tick or the frames.
    int mixInto(int16_t* out, int frames, int outChannels) {
        const Module& m = *module;
        int n = frames < tickLeft ? frames : tickLeft;
        int perSide = m.channels / 2 > 0 ? m.channels / 2 : 1;
        int own = 100 + separation, other = 100 - separation;   // of 200
        int32_t wantedVolume[kMaxChannels];
        uint64_t step[kMaxChannels];
        for (int i = 0; i < m.channels; i++) {
            Channel& c = ch[i];
            wantedVolume[i] = c.mixVolume * 256;
            int period = c.mixPeriod < 40 ? 40 : c.mixPeriod;
            step[i] = c.active ? (uint64_t)(((uint64_t)kPaulaClock << 32) / ((uint64_t)period * (uint64_t)rate)) : 0;
        }
        int32_t rampStep = (int32_t)((64 * 256) / rampLength()) + 1;
        for (int f = 0; f < n; f++) {
            int64_t left = 0, right = 0;
            for (int i = 0; i < m.channels; i++) {
                Channel& c = ch[i];
                int32_t value = 0;
                if (c.active && !(muted & (1u << i))) {
                    const Sample& s = m.samples[c.sample - 1];
                    size_t idx = (size_t)(c.pos >> 32);
                    if (idx >= (size_t)s.length) { c.active = false; c.lastValue = 0; continue; }   // (a new sample number without a note: the position may be behind the end of the other sample)
                    int32_t a = s.data[idx];
                    int32_t b;
                    size_t next = idx + 1;
                    if (s.loopLength > 0 && next >= (size_t)(s.loopStart + s.loopLength)) next = (size_t)s.loopStart;
                    if (next >= (size_t)s.length) b = a; else b = s.data[next];
                    int32_t v;
                    if (interpolate) v = a * 65536 + (b - a) * (int32_t)((c.pos >> 16) & 0xFFFF);
                    else v = a * 65536;
                    // the volume moves towards the wanted one in a few samples
                    if (c.curVol < wantedVolume[i]) { c.curVol += rampStep; if (c.curVol > wantedVolume[i]) c.curVol = wantedVolume[i]; }
                    else if (c.curVol > wantedVolume[i]) { c.curVol -= rampStep; if (c.curVol < wantedVolume[i]) c.curVol = wantedVolume[i]; }
                    value = (int32_t)(((int64_t)v * c.curVol) >> 24);
                    // advance
                    c.pos += step[i];
                    if (s.loopLength > 0) {
                        uint64_t end = (uint64_t)(s.loopStart + s.loopLength) << 32;
                        if (c.pos >= end) c.pos = ((uint64_t)s.loopStart << 32) + (c.pos - end) % ((uint64_t)s.loopLength << 32);
                    } else if ((c.pos >> 32) >= (uint64_t)s.length) c.active = false;
                } else if (c.curVol != 0) c.curVol = 0;
                c.lastValue = value;
                if (c.fadeLeft > 0) {   // the end of the sample that was cut
                    value += (int32_t)((int64_t)c.fade * c.fadeLeft / rampLength());
                    c.fadeLeft--;
                }
                bool onLeft = (i & 3) == 0 || (i & 3) == 3;
                left += onLeft ? (int64_t)value * own : (int64_t)value * other;
                right += onLeft ? (int64_t)value * other : (int64_t)value * own;
            }
            // scale: one channel at full volume and full sample is 8192; the channels of a side add up to at most 32767
            int64_t l = left * 4 * gain / ((int64_t)200 * perSide * 100);
            int64_t r = right * 4 * gain / ((int64_t)200 * perSide * 100);
            if (l > 32767) l = 32767; else if (l < -32768) l = -32768;
            if (r > 32767) r = 32767; else if (r < -32768) r = -32768;
            if (outChannels == 2) { out[2 * f] = (int16_t)l; out[2 * f + 1] = (int16_t)r; }
            else out[f] = (int16_t)((l + r) / 2);
        }
        tickLeft -= n;
        return n;
    }

    /// Renders up to `frames` frames; fewer only at the end of the song. Interleaved 16 bit samples (`outChannels` 1 or 2).
    int render(int16_t* out, int frames, int outChannels) {
        int done = 0;
        while (done < frames) {
            if (tickLeft == 0) {
                if (finished) break;
                processTick();
                if (tickLeft == 0) continue;   // (a tick of no length: a very slow rate with a fast tempo)
            }
            done += mixInto(out + (size_t)done * (size_t)outChannels, frames - done, outChannels);
        }
        return done;
    }

    /// Runs the replayer without sound until `order`/`row` is reached (the song keeps its speed, tempo and volumes), or to the end. Returns whether it got there.
    bool seek(int toOrder, int toRow) {
        bool wasLoop = loop;
        restart();
        loop = false;   // (the dry run ends when the song would start over: a position that is never played is not found for ever)
        bool reached = true;
        int guard = 0;
        while (!(order == toOrder && row == toRow && tick == 0)) {
            if (finished || ++guard > 20000000) { reached = false; break; }
            processTick();
            tickLeft = 0;
        }
        loop = wasLoop;
        if (reached) {
            finished = false;
            for (int i = 0; i < module->channels; i++) { ch[i].curVol = ch[i].mixVolume * 256; ch[i].fadeLeft = 0; }
        }
        return reached;
    }

    /// The length of the song in frames at the current rate (a dry run; 0 if it never ends: a module that loops by itself is counted until it jumps back).
    int64_t lengthFrames() const {
        Song copy(*this);
        copy.restart();
        copy.loop = false;
        int64_t total = 0;
        int guard = 0;
        while (!copy.finished && ++guard < 10000000) {
            copy.processTick();
            total += copy.tickFrames;
            copy.tickLeft = 0;
            if (total > (int64_t)rate * 7200) break;   // two hours: not a song
        }
        return total;
    }
};

}  // namespace tracker
}  // namespace fire

#ifndef FIRE_TRACKER_ENGINE_ONLY

namespace fire {
namespace tracker {

#if defined(FIRE_TLS_STRUCT)
#define FIRE_TRACKER_ERROR g_ioError
#else
struct ErrorSlot { int32_t code; char message[176]; };
inline thread_local ErrorSlot t_trackerError = {0, {0}};
#define FIRE_TRACKER_ERROR ::fire::tracker::t_trackerError
#endif

inline int64_t fail(int code, const std::string& message) {
    FIRE_TRACKER_ERROR.code = code;
    size_t n = message.size() < sizeof FIRE_TRACKER_ERROR.message - 1 ? message.size() : sizeof FIRE_TRACKER_ERROR.message - 1;
    std::memcpy(FIRE_TRACKER_ERROR.message, message.data(), n);
    FIRE_TRACKER_ERROR.message[n] = 0;
    return -1;
}
inline void ok() { FIRE_TRACKER_ERROR.code = 0; FIRE_TRACKER_ERROR.message[0] = 0; }

inline Value str8(const std::string& text, OwnList* list) {
    Str* s = allocStr((uint32_t)text.size(), list);
    for (size_t i = 0; i < text.size(); i++) strChars(s)[i] = (char16_t)(uint8_t)text[i];
    return StrV(s);
}

struct SongTable {
    std::vector<Song*> items{1, nullptr};
    ~SongTable() { for (Song* s : items) delete s; }
};
inline std::vector<Song*>& songs() { static SongTable table; return table.items; }

/// The program has ended (the library for the VM stays loaded for the next one): drops the songs.
inline void reset() {
    std::vector<Song*>& all = songs();
    for (size_t i = 1; i < all.size(); i++) { delete all[i]; all[i] = nullptr; }
}

inline Song* find(Value h) {
    int64_t i = h.i;
    if (i > 0 && (size_t)i < songs().size() && songs()[(size_t)i]) return songs()[(size_t)i];
    fail(InvalidHandle, "Invalid or closed song handle.");
    return nullptr;
}

inline Value LastError() { return Int(FIRE_TRACKER_ERROR.code); }
inline Value LastErrorMessage(OwnList* list) { return str8(FIRE_TRACKER_ERROR.message, list); }
inline Value OpenCount() {
    int64_t n = 0;
    for (Song* s : songs()) if (s) n++;
    return Int(n);
}

/// Reads a module from `count` bytes of the buffer; the handle of the song, -1 on an error (BadFormat: it is not a module).
inline Value Load(Value buffer, Value offset, Value count, Value rate) {
    if (!leafAlive(buffer)) return destroyedError(buffer);
    int64_t length = bufOf(buffer)->length;
    if (offset.i < 0 || count.i < 0 || offset.i > length || count.i > length - offset.i) return Int(fail(InvalidArgument, "offset/count are outside of the buffer."));
    if (rate.i < 8000 || rate.i > 192000) return Int(fail(InvalidArgument, "The rate must be between 8000 and 192000 Hz."));
    auto module = std::make_shared<Module>();
    std::string error;
    int code = 0;
    if (!parseModule(bufOf(buffer)->bytes() + offset.i, (size_t)count.i, *module, error, code)) return Int(fail(code, error));
    Song* song = new Song(module, (int)rate.i);
    songs().push_back(song);
    ok();
    return Int((int64_t)songs().size() - 1);
}

inline Value Close(Value h) {
    int64_t i = h.i;
    if (i <= 0 || (size_t)i >= songs().size() || !songs()[(size_t)i]) { fail(InvalidHandle, "Invalid or closed song handle."); return Bool(false); }
    delete songs()[(size_t)i];
    songs()[(size_t)i] = nullptr;
    ok();
    return Bool(true);
}

/// Renders up to `frames` frames of 16 bit samples (channels 1 or 2) into the buffer from `offset`; the number rendered (less than asked only at the end of the song), -1 on an error.
inline Value Render(Value h, Value buffer, Value offset, Value frames, Value channels) {
    if (!leafAlive(buffer)) return destroyedError(buffer);
    Song* s = find(h);
    if (!s) return Int(-1);
    if (channels.i < 1 || channels.i > 2) return Int(fail(InvalidArgument, "Mono (1) or stereo (2) channels."));
    int64_t frameBytes = 2 * channels.i;
    int64_t length = bufOf(buffer)->length;
    if (offset.i < 0 || frames.i < 0 || offset.i > length || frames.i * frameBytes > length - offset.i) return Int(fail(InvalidArgument, "offset/frames are outside of the buffer (length " + std::to_string(length) + ")."));
    int16_t* out = reinterpret_cast<int16_t*>(bufOf(buffer)->bytes() + offset.i);
    // (the samples are little endian in the buffer: a big endian host would swap them here; there is none we build for)
    int n = s->render(out, (int)frames.i, (int)channels.i);
    ok();
    return Int(n);
}

/// What the song knows: 0 channels, 1 orders (song length), 2 patterns, 3 order now, 4 row now, 5 speed, 6 tempo, 7 finished, 8 length in milliseconds (a dry run), 9 rate,
/// 10 loop, 11 separation, 12 interpolation, 13 gain, 14 muted channels (bit mask), 15 title length.
inline Value Get(Value h, Value what) {
    Song* s = find(h);
    if (!s) return Int(-1);
    ok();
    switch (what.i) {
        case 0: return Int(s->module->channels);
        case 1: return Int(s->module->songLength);
        case 2: return Int(s->module->patterns);
        case 3: return Int(s->order);
        case 4: return Int(s->row);
        case 5: return Int(s->speed);
        case 6: return Int(s->tempo);
        case 7: return Int(s->finished ? 1 : 0);
        case 8: return Int(s->lengthFrames() * 1000 / s->rate);
        case 9: return Int(s->rate);
        case 10: return Int(s->loop ? 1 : 0);
        case 11: return Int(s->separation);
        case 12: return Int(s->interpolate ? 1 : 0);
        case 13: return Int(s->gain);
        case 14: return Int((int64_t)s->muted);
        default: return Int(fail(InvalidArgument, "Unknown information."));
    }
}

/// Changes a setting: 0 loop (0/1), 1 separation (0..100), 2 interpolation (0/1), 3 gain (percent), 4 muted channels (bit mask), 5 rate.
inline Value Set(Value h, Value what, Value value) {
    Song* s = find(h);
    if (!s) return Bool(false);
    switch (what.i) {
        case 0: s->loop = value.i != 0; break;
        case 1:
            if (value.i < 0 || value.i > 100) { fail(InvalidArgument, "The stereo separation is between 0 and 100 percent."); return Bool(false); }
            s->separation = (int)value.i;
            break;
        case 2: s->interpolate = value.i != 0; break;
        case 3:
            if (value.i < 0 || value.i > 400) { fail(InvalidArgument, "The gain is between 0 and 400 percent."); return Bool(false); }
            s->gain = (int)value.i;
            break;
        case 4: s->muted = (uint32_t)value.i; break;
        case 5:
            if (value.i < 8000 || value.i > 192000) { fail(InvalidArgument, "The rate must be between 8000 and 192000 Hz."); return Bool(false); }
            s->rate = (int)value.i;
            break;
        default: fail(InvalidArgument, "Unknown setting."); return Bool(false);
    }
    ok();
    return Bool(true);
}

/// Text: what 0 the title, 1 the name of the sample `index` (0..30).
inline Value Text(Value h, Value what, Value index, OwnList* list) {
    Song* s = find(h);
    if (!s) return Undef();
    ok();
    if (what.i == 0) return str8(s->module->title, list);
    if (what.i == 1 && index.i >= 0 && index.i < 31) return str8(s->module->samples[index.i].name, list);
    fail(InvalidArgument, "Unknown text or sample number.");
    return Undef();
}

/// About a sample (0..30): 0 length in bytes, 1 finetune (-8..7), 2 volume, 3 loop start (bytes), 4 loop length (bytes, 0: no loop).
inline Value SampleInfo(Value h, Value index, Value what) {
    Song* s = find(h);
    if (!s) return Int(-1);
    if (index.i < 0 || index.i > 30) return Int(fail(InvalidArgument, "Sample numbers are 0..30."));
    const Sample& smp = s->module->samples[index.i];
    ok();
    switch (what.i) {
        case 0: return Int(smp.length);
        case 1: return Int(smp.finetune >= 8 ? smp.finetune - 16 : smp.finetune);
        case 2: return Int(smp.volume);
        case 3: return Int(smp.loopStart);
        case 4: return Int(smp.loopLength);
        default: return Int(fail(InvalidArgument, "Unknown information."));
    }
}

/// Starts again at the beginning.
inline Value Restart(Value h) {
    Song* s = find(h);
    if (!s) return Bool(false);
    s->restart();
    ok();
    return Bool(true);
}

/// Goes to a position (silently: speed, tempo and volumes are those of the song at that point). False if the song never gets there.
inline Value Seek(Value h, Value order, Value row) {
    Song* s = find(h);
    if (!s) return Bool(false);
    if (order.i < 0 || order.i >= s->module->songLength || row.i < 0 || row.i >= kRows) { fail(InvalidArgument, "Order or row outside of the song."); return Bool(false); }
    bool reached = s->seek((int)order.i, (int)row.i);
    if (!reached) s->restart();
    ok();
    return Bool(reached);
}

/// A cell of the pattern played at `order`: what 0 sample, 1 period, 2 effect, 3 parameter; the channel's state: what 4 volume, 5 period, 6 sample, 7 playing (a sample is sounding).
inline Value PatternCell(Value h, Value order, Value row, Value channel, Value what) {
    Song* s = find(h);
    if (!s) return Int(-1);
    const Module& m = *s->module;
    if (channel.i < 0 || channel.i >= m.channels) return Int(fail(InvalidArgument, "Channel outside of the song."));
    ok();
    if (what.i >= 4) {
        const Channel& c = s->ch[channel.i];
        switch (what.i) {
            case 4: return Int(c.mixVolume < 0 ? c.volume : c.mixVolume);
            case 5: return Int(c.mixPeriod ? c.mixPeriod : c.period);
            case 6: return Int(c.sample);
            case 7: return Int(c.active ? 1 : 0);
            default: return Int(fail(InvalidArgument, "Unknown information."));
        }
    }
    if (order.i < 0 || order.i >= m.songLength || row.i < 0 || row.i >= kRows) return Int(fail(InvalidArgument, "Order or row outside of the song."));
    const struct Cell& cell = m.cell(m.orders[order.i], (int)row.i, (int)channel.i);
    switch (what.i) {
        case 0: return Int(cell.sample);
        case 1: return Int(cell.period);
        case 2: return Int(cell.effect);
        case 3: return Int(cell.param);
        default: return Int(fail(InvalidArgument, "Unknown information."));
    }
}

}  // namespace tracker
}  // namespace fire

#endif  // FIRE_TRACKER_ENGINE_ONLY
