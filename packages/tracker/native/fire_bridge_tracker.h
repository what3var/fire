/* fire native bridge "tracker" (C): the natives behind `#import "tracker"` (docs/audio/tracker.md): a player for ProTracker modules (.mod, 4 to 32 channels) - the music of
 * the Amiga demos and games, made of short recorded samples instead of the synthesizer voices that MIDI leaves to the sound card (which is why a module sounds the same
 * everywhere). The fire side (Tracker.Song, Tracker.Player - fire source) is the same in the VM and in a native build; this file is what its `__Tracker...` functions do.
 *
 * This file is the one implementation, in C99 ("language": "c"): the native build puts it into the generated program and the virtual machine runs it in a shared
 * library built from it (native/abi/fire_pkg_abi.h). It has two parts: the replayer + mixer (`FireTrkSong`, plain C that knows nothing of fire - tested on its own
 * against other players, define FIRE_TRACKER_ENGINE_ONLY to get only that part) and the natives that wrap it. The mixer renders 16 bit samples into a buffer; playing
 * them is the job of the `audio` package.
 *
 * What it does like ProTracker 2.3: the period table with the 16 finetunes; the effects 0 arpeggio, 1/2 slides, 3 tone portamento, 4 vibrato, 5/6 the combinations
 * with volume slide, 7 tremolo, 9 sample offset, A volume slide, B position jump, C volume, D pattern break, E1/E2 fine slides, E3 glissando, E4/E7 waveforms, E5
 * finetune, E6 pattern loop, E9 retrigger, EA/EB fine volume, EC note cut, ED note delay, EE pattern delay, F speed/tempo (E0 filter, E8 and 8xx panning, EF funk
 * repeat do nothing). Channels 0 and 3 (and so on) play left, 1 and 2 right, like the Amiga; the stereo separation is adjustable. Samples are played with linear
 * interpolation (the Amiga did not interpolate: the option can switch it off), volume changes and cut-off samples are smoothed over about 1.5 ms so that nothing
 * clicks. All arithmetic is integer, so the VM and a native build render the same samples. */
#ifndef FIRE_BRIDGE_TRACKER_H
#define FIRE_BRIDGE_TRACKER_H

#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

enum { FIRE_TRK_E_NONE = 0, FIRE_TRK_E_INVALID_ARGUMENT = 1, FIRE_TRK_E_INVALID_HANDLE = 2, FIRE_TRK_E_NOT_FOUND = 3, FIRE_TRK_E_BUSY = 4, FIRE_TRK_E_PERMISSION = 5,
       FIRE_TRK_E_UNSUPPORTED = 6, FIRE_TRK_E_OTHER = 7, FIRE_TRK_E_BAD_FORMAT = 8 };

#define FIRE_TRK_ROWS 64
#define FIRE_TRK_MAX_CHANNELS 32

/* The periods of the notes C-1 .. B-3 for the 16 finetunes (0..7 are +0..+7, 8..15 are -8..-1), as in ProTracker. */
static const int16_t fire_trk_periods[16][36] = {
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
static const uint8_t fire_trk_vibratoTable[32] = {
    0x00, 0x18, 0x31, 0x4A, 0x61, 0x78, 0x8D, 0xA1, 0xB4, 0xC5, 0xD4, 0xE0, 0xEB, 0xF4, 0xFA, 0xFD,
    0xFF, 0xFD, 0xFA, 0xF4, 0xEB, 0xE0, 0xD4, 0xC5, 0xB4, 0xA1, 0x8D, 0x78, 0x61, 0x4A, 0x31, 0x18};

#define FIRE_TRK_PAULA_CLOCK ((int64_t)3546895)   /* the PAL Amiga: 7093789.2 Hz / 2 = the rate at which a sample byte is played is this divided by the period */

typedef struct FireTrkSample {
    char name[23];
    int length;             /* bytes */
    int finetune;           /* 0..15 as stored */
    int volume;             /* 0..64 */
    int loopStart;          /* bytes */
    int loopLength;         /* bytes; 0 when the sample does not loop */
    int8_t* data;
} FireTrkSample;

typedef struct FireTrkCell {
    uint8_t sample;
    uint16_t period;
    uint8_t effect;
    uint8_t param;
} FireTrkCell;

typedef struct FireTrkModule {
    char title[21];
    int channels;
    int songLength;
    int restart;
    uint8_t orders[128];
    int patterns;
    FireTrkCell* cells;     /* patterns * 64 rows * channels */
    FireTrkSample samples[31];
} FireTrkModule;

static const FireTrkCell* fire_trk_cell(const FireTrkModule* m, int pattern, int row, int channel) {
    return &m->cells[((size_t)pattern * FIRE_TRK_ROWS + (size_t)row) * (size_t)m->channels + (size_t)channel];
}

static void fire_trk_freeModule(FireTrkModule* m) {
    int i;
    if (m == NULL) return;
    for (i = 0; i < 31; i++) free(m->samples[i].data);
    free(m->cells);
    free(m);
}

/* `n` bytes of text from the file: printable ASCII, the rest a space, up to the first zero byte, without the spaces at the end (`out` holds n + 1 bytes). */
static void fire_trk_cleanText(const uint8_t* p, size_t n, char* out) {
    size_t i, len = 0;
    for (i = 0; i < n && p[i]; i++) out[len++] = p[i] >= 32 && p[i] < 127 ? (char)p[i] : ' ';
    while (len > 0 && out[len - 1] == ' ') len--;
    out[len] = 0;
}

/* The number of channels a module with this signature has, 0 if it is not one we know. */
static int fire_trk_channelsOfSignature(const uint8_t* s) {
    if (memcmp(s, "M.K.", 4) == 0 || memcmp(s, "M!K!", 4) == 0 || memcmp(s, "FLT4", 4) == 0 || memcmp(s, "4CHN", 4) == 0) return 4;
    if (memcmp(s, "6CHN", 4) == 0) return 6;
    if (memcmp(s, "8CHN", 4) == 0 || memcmp(s, "CD81", 4) == 0 || memcmp(s, "OCTA", 4) == 0) return 8;
    if (s[0] >= '1' && s[0] <= '9' && s[1] == 'C' && s[2] == 'H' && s[3] == 'N') return s[0] - '0';
    if (s[0] >= '1' && s[0] <= '9' && s[1] >= '0' && s[1] <= '9' && ((s[2] == 'C' && s[3] == 'H') || (s[2] == 'C' && s[3] == 'N'))) {
        int n = (s[0] - '0') * 10 + (s[1] - '0');
        return n >= 10 && n <= FIRE_TRK_MAX_CHANNELS ? n : 0;
    }
    return 0;
}

/* Reads a module. Returns it (malloc; fire_trk_freeModule), or NULL and says why in `error` (the code in `code`). */
static FireTrkModule* fire_trk_parseModule(const uint8_t* d, size_t size, const char** error, int* code) {
    FireTrkModule* m;
    int channels, i, maxAll = 0, maxSong = 0, patterns;
    size_t patternBytes, pos, k;
    const uint8_t* p;
    *code = FIRE_TRK_E_BAD_FORMAT;
    if (size < 1084) { *error = "This is not a module: the file is too short."; return NULL; }
    channels = fire_trk_channelsOfSignature(d + 1080);
    if (channels == 0) {
        if (memcmp(d + 1080, "FLT8", 4) == 0) { *error = "FLT8 (Startrekker 8 channel) modules are not supported."; *code = FIRE_TRK_E_UNSUPPORTED; return NULL; }
        *error = "This is not a ProTracker module (the signature at byte 1080 is not one of M.K., 4CHN, 6CHN, 8CHN, xxCH ...).";
        return NULL;
    }
    m = (FireTrkModule*)calloc(1, sizeof(FireTrkModule));
    if (m == NULL) { *error = "Out of memory."; *code = FIRE_TRK_E_OTHER; return NULL; }
    m->channels = channels;
    fire_trk_cleanText(d, 20, m->title);
    for (i = 0; i < 31; i++) {
        const uint8_t* h = d + 20 + i * 30;
        FireTrkSample* s = &m->samples[i];
        int loopLen;
        fire_trk_cleanText(h, 22, s->name);
        s->length = ((h[22] << 8) | h[23]) * 2;
        s->finetune = h[24] & 15;
        s->volume = h[25] > 64 ? 64 : h[25];
        s->loopStart = ((h[26] << 8) | h[27]) * 2;
        loopLen = ((h[28] << 8) | h[29]) * 2;
        s->loopLength = loopLen > 2 ? loopLen : 0;
    }
    m->songLength = d[950];
    m->restart = d[951];
    memcpy(m->orders, d + 952, 128);
    if (m->songLength < 1 || m->songLength > 128) m->songLength = m->songLength == 0 ? 1 : 128;
    for (i = 0; i < 128; i++) { if (m->orders[i] > maxAll) maxAll = m->orders[i]; if (i < m->songLength && m->orders[i] > maxSong) maxSong = m->orders[i]; }
    patternBytes = (size_t)FIRE_TRK_ROWS * (size_t)channels * 4;
    patterns = maxAll + 1;
    if (1084 + (size_t)patterns * patternBytes > size) patterns = maxSong + 1;   /* (a table with leftovers behind the song) */
    if (1084 + (size_t)patterns * patternBytes > size) { fire_trk_freeModule(m); *error = "The module is truncated (its patterns are not all there)."; return NULL; }
    m->patterns = patterns;
    m->cells = (FireTrkCell*)calloc((size_t)patterns * FIRE_TRK_ROWS * (size_t)channels, sizeof(FireTrkCell));
    if (m->cells == NULL) { fire_trk_freeModule(m); *error = "Out of memory."; *code = FIRE_TRK_E_OTHER; return NULL; }
    p = d + 1084;
    for (k = 0; k < (size_t)patterns * FIRE_TRK_ROWS * (size_t)channels; k++, p += 4) {
        FireTrkCell* c = &m->cells[k];
        c->sample = (uint8_t)((p[0] & 0xF0) | (p[2] >> 4));
        c->period = (uint16_t)(((p[0] & 0x0F) << 8) | p[1]);
        c->effect = p[2] & 0x0F;
        c->param = p[3];
        if (c->sample > 31) c->sample = 0;
    }
    pos = 1084 + (size_t)patterns * patternBytes;
    for (i = 0; i < 31; i++) {
        FireTrkSample* s = &m->samples[i];
        size_t avail, n;
        if (s->length <= 2) { s->length = 0; s->loopLength = 0; continue; }
        s->data = (int8_t*)calloc((size_t)s->length, 1);
        if (s->data == NULL) { fire_trk_freeModule(m); *error = "Out of memory."; *code = FIRE_TRK_E_OTHER; return NULL; }
        avail = pos < size ? size - pos : 0;
        n = avail < (size_t)s->length ? avail : (size_t)s->length;   /* (a truncated last sample is played as far as it goes) */
        if (n) memcpy(s->data, d + pos, n);
        pos += (size_t)s->length;
        if (s->loopLength > 0) {
            if (s->loopStart >= s->length) s->loopLength = 0;
            else if (s->loopStart + s->loopLength > s->length) s->loopLength = s->length - s->loopStart;
            if (s->loopLength <= 2) s->loopLength = 0;
        }
    }
    *code = FIRE_TRK_E_NONE;
    return m;
}

typedef struct FireTrkChannel {
    int sample;                 /* 1..31, 0: none yet */
    int period;                 /* the period of the note (without vibrato and arpeggio) */
    int mixPeriod;              /* what is played this tick */
    int portaTarget;
    int portaSpeed;
    int volume;                 /* 0..64 */
    int mixVolume;              /* with tremolo */
    int finetune;
    int vibratoSpeed, vibratoDepth, vibratoPos, vibratoWave;
    int tremoloSpeed, tremoloDepth, tremoloPos, tremoloWave;
    int sampleOffset;
    int loopRow, loopCount;
    int glissando;
    int effect, param;
    int pendingNote;            /* ED: the note waits */
    int pendingPeriod;
    int active;
    uint64_t pos;               /* 32.32 fixed point, in sample bytes */
    /* mixer */
    int32_t curVol;             /* volume * 256, moves towards the wanted volume */
    int32_t lastValue;          /* the last value this channel gave (for the fade out of a cut sample) */
    int32_t fade;               /* the value that fades out ... */
    int fadeLeft;               /* ... and the samples it still has */
} FireTrkChannel;

typedef struct FireTrkSong {
    const FireTrkModule* module;
    int rate;
    int loop;
    int separation;             /* 0 (mono) .. 100 (the hard panning of the Amiga) */
    int interpolate;
    int gain;                   /* percent */
    uint32_t muted;             /* bit n: channel n is silent */

    /* position */
    int order, row, tick;
    int speed, tempo;
    int finished;
    int repeatRow;              /* the row is repeated by a pattern delay (the notes are not read again) */
    int patternDelay;
    int jump;                   /* B, D or E6 asked for a jump */
    int jumpOrder, jumpRow;
    int jumpOrderSet;
    int loopJump;
    uint8_t visited[128 * FIRE_TRK_ROWS];
    FireTrkChannel ch[FIRE_TRK_MAX_CHANNELS];
    int64_t tickRemainder;
    int tickFrames;             /* the frames of the tick that is being played */
    int tickLeft;
} FireTrkSong;

static void fire_trk_restart(FireTrkSong* s) {
    const FireTrkModule* m = s->module;
    int i;
    s->order = 0; s->row = 0; s->tick = 0; s->speed = 6; s->tempo = 125;
    s->finished = 0; s->repeatRow = 0; s->patternDelay = 0; s->jump = 0; s->jumpOrderSet = 0; s->loopJump = 0; s->jumpRow = 0;
    memset(s->visited, 0, sizeof s->visited);
    memset(s->ch, 0, sizeof s->ch);
    for (i = 0; i < m->channels; i++) s->ch[i].finetune = 0;
    s->tickRemainder = 0; s->tickFrames = 0; s->tickLeft = 0;
    s->visited[0] = 1;
}

/* A song of the module at the sample rate (malloc; it does not own the module). */
static FireTrkSong* fire_trk_newSong(const FireTrkModule* m, int sampleRate) {
    FireTrkSong* s = (FireTrkSong*)calloc(1, sizeof(FireTrkSong));
    if (s == NULL) return NULL;
    s->module = m;
    s->rate = sampleRate;
    s->loop = 0;
    s->separation = 50;
    s->interpolate = 1;
    s->gain = 100;
    s->muted = 0;
    s->speed = 6;
    s->tempo = 125;
    fire_trk_restart(s);
    return s;
}

/* ---- the replayer ---------------------------------------------------------------------------------------------------------------------------- */
static int fire_trk_noteIndex(int period) {
    int i = 0;
    while (i < 35 && period < fire_trk_periods[0][i]) i++;   /* (the table falls: the first note that is not higher) */
    return i;
}
static int fire_trk_fineOf(const FireTrkChannel* c) { return c->finetune & 15; }

static int fire_trk_rampLength(const FireTrkSong* s) { int n = s->rate / 700; return n < 8 ? 8 : n; }

static void fire_trk_trigger(FireTrkSong* s, FireTrkChannel* c, int period, int withOffset) {
    c->period = fire_trk_periods[fire_trk_fineOf(c)][fire_trk_noteIndex(period)];
    c->pos = 0;
    if (c->sample > 0 && c->sample <= 31 && s->module->samples[c->sample - 1].length > 0) {
        c->active = 1;
        if (withOffset) c->pos = (uint64_t)((uint64_t)c->sampleOffset << 8) << 32;
    } else c->active = 0;
    if (c->active && (c->pos >> 32) >= (uint64_t)s->module->samples[c->sample - 1].length) {
        const FireTrkSample* smp = &s->module->samples[c->sample - 1];
        if (smp->loopLength == 0) c->active = 0;   /* an offset behind the end of a sample that does not loop: silence */
        else c->pos = (uint64_t)smp->loopStart << 32;
    }
    if ((c->vibratoWave & 4) == 0) c->vibratoPos = 0;
    if ((c->tremoloWave & 4) == 0) c->tremoloPos = 0;
    c->fade = c->lastValue;      /* the sample that was playing fades out instead of being cut */
    c->fadeLeft = fire_trk_rampLength(s);
    c->curVol = 0;               /* the new one fades in */
}

static void fire_trk_tickZeroEffect(FireTrkSong* s, FireTrkChannel* c, int index, int x, int y) {
    (void)index;
    switch (c->effect) {
        case 3: if (c->param) c->portaSpeed = c->param; break;
        case 4:
            if (x) c->vibratoSpeed = x;
            if (y) c->vibratoDepth = y;
            break;
        case 7:
            if (x) c->tremoloSpeed = x;
            if (y) c->tremoloDepth = y;
            break;
        case 0xB: s->jump = 1; s->jumpOrder = c->param; s->jumpOrderSet = 1; break;
        case 0xC: c->volume = c->param > 64 ? 64 : c->param; break;
        case 0xD: {
            int target = (c->param >> 4) * 10 + (c->param & 15);
            s->jump = 1;
            s->jumpRow = target > 63 ? 0 : target;   /* (without a B on the same row the song goes on with the next order) */
            break;
        }
        case 0xF:
            if (c->param == 0) s->finished = 1;   /* F00 stops the song */
            else if (c->param < 32) s->speed = c->param;
            else s->tempo = c->param;
            break;
        case 0xE:
            switch (x) {
                case 0x1: c->period -= y; if (c->period < 113) c->period = 113; break;
                case 0x2: c->period += y; if (c->period > 856) c->period = 856; break;
                case 0x3: c->glissando = y != 0; break;
                case 0x4: c->vibratoWave = y & 7; break;
                case 0x5: c->finetune = y; break;
                case 0x6:
                    if (y == 0) c->loopRow = s->row;
                    else {
                        if (c->loopCount == 0) c->loopCount = y;
                        else if (--c->loopCount == 0) break;
                        s->jump = 1;
                        s->jumpOrder = s->order;
                        s->jumpOrderSet = 1;
                        s->jumpRow = c->loopRow;
                        s->loopJump = 1;
                    }
                    break;
                case 0x7: c->tremoloWave = y & 7; break;
                case 0xA: c->volume += y; if (c->volume > 64) c->volume = 64; break;
                case 0xB: c->volume -= y; if (c->volume < 0) c->volume = 0; break;
                case 0xC: if (y == 0) c->volume = 0; break;
                case 0xE: if (s->patternDelay == 0 && !s->repeatRow) s->patternDelay = y; break;
                default: break;
            }
            break;
        default: break;
    }
}

static void fire_trk_readRow(FireTrkSong* s) {
    const FireTrkModule* m = s->module;
    int pattern = m->orders[s->order];
    int i;
    for (i = 0; i < m->channels; i++) {
        FireTrkChannel* c = &s->ch[i];
        const FireTrkCell* cell = fire_trk_cell(m, pattern, s->row, i);
        int x, y;
        if (cell->sample) {
            const FireTrkSample* smp = &m->samples[cell->sample - 1];
            c->sample = cell->sample;
            c->volume = smp->volume;
            c->finetune = smp->finetune;
        }
        c->effect = cell->effect;
        c->param = cell->param;
        x = cell->param >> 4;
        y = cell->param & 15;
        c->pendingNote = 0;
        if (cell->effect == 9 && cell->param) c->sampleOffset = cell->param;
        if (cell->effect == 0xE && x == 0x5) c->finetune = y;   /* (ProTracker sets the finetune before it looks up the period of the note on this row) */
        if (cell->period) {
            int adjusted = fire_trk_periods[fire_trk_fineOf(c)][fire_trk_noteIndex(cell->period)];
            if (cell->effect == 3 || cell->effect == 5) c->portaTarget = adjusted;
            else if (cell->effect == 0xE && x == 0xD && y > 0) { c->pendingNote = 1; c->pendingPeriod = cell->period; }
            else fire_trk_trigger(s, c, cell->period, cell->effect == 9);
        }
        fire_trk_tickZeroEffect(s, c, i, x, y);
    }
}

static int fire_trk_waveValue(int wave, int pos) {
    switch (wave & 3) {
        case 0: return fire_trk_vibratoTable[pos & 31];
        case 1: { int t = (pos & 31) * 8; return (pos & 32) ? 255 - t : t; }
        default: return 255;
    }
}

static void fire_trk_slideVolume(FireTrkChannel* c) {
    int x = c->param >> 4, y = c->param & 15;
    if (x) { c->volume += x; if (c->volume > 64) c->volume = 64; }
    else { c->volume -= y; if (c->volume < 0) c->volume = 0; }
}
static void fire_trk_tonePortamento(FireTrkChannel* c) {
    if (c->portaTarget == 0) return;
    if (c->period > c->portaTarget) { c->period -= c->portaSpeed; if (c->period <= c->portaTarget) { c->period = c->portaTarget; c->portaTarget = 0; } }
    else if (c->period < c->portaTarget) { c->period += c->portaSpeed; if (c->period >= c->portaTarget) { c->period = c->portaTarget; c->portaTarget = 0; } }
}
static int fire_trk_vibratoDelta(FireTrkChannel* c) {
    int amp = fire_trk_waveValue(c->vibratoWave, c->vibratoPos);
    int delta = (amp * c->vibratoDepth) >> 7;
    int signedDelta = (c->vibratoPos & 32) ? -delta : delta;
    c->vibratoPos = (c->vibratoPos + c->vibratoSpeed) & 63;
    return signedDelta;
}

/* The effects that work on every tick but the first of a row. */
static void fire_trk_continuous(FireTrkSong* s, FireTrkChannel* c) {
    int x = c->param >> 4, y = c->param & 15;
    switch (c->effect) {
        case 0:
            if (c->param) {
                int step = s->tick % 3;
                int offset = step == 0 ? 0 : step == 1 ? x : y;
                int i = fire_trk_noteIndex(c->period) + offset;
                c->mixPeriod = fire_trk_periods[fire_trk_fineOf(c)][i > 35 ? 35 : i];
            }
            break;
        case 1: c->period -= c->param; if (c->period < 113) c->period = 113; break;
        case 2: c->period += c->param; if (c->period > 856) c->period = 856; break;
        case 3: fire_trk_tonePortamento(c); break;
        case 4: c->mixPeriod = c->period + fire_trk_vibratoDelta(c); break;
        case 5: fire_trk_tonePortamento(c); fire_trk_slideVolume(c); break;
        case 6: c->mixPeriod = c->period + fire_trk_vibratoDelta(c); fire_trk_slideVolume(c); break;
        case 7: {
            int amp = fire_trk_waveValue(c->tremoloWave, c->tremoloPos);
            int delta = (amp * c->tremoloDepth) >> 6;
            int v = c->volume + ((c->tremoloPos & 32) ? -delta : delta);
            c->mixVolume = v < 0 ? 0 : v > 64 ? 64 : v;
            c->tremoloPos = (c->tremoloPos + c->tremoloSpeed) & 63;
            break;
        }
        case 0xA: fire_trk_slideVolume(c); break;
        case 0xE:
            if (x == 0x9 && y > 0 && s->tick % y == 0) { c->pos = 0; c->active = c->sample > 0 && s->module->samples[c->sample - 1].length > 0; c->fade = c->lastValue; c->fadeLeft = fire_trk_rampLength(s); c->curVol = 0; }
            else if (x == 0xC && s->tick == y) c->volume = 0;
            else if (x == 0xD && c->pendingNote && s->tick == y) { c->pendingNote = 0; fire_trk_trigger(s, c, c->pendingPeriod, 0); }
            break;
        default: break;
    }
}

/* Moves to the next tick, and at the end of a row to the next row. */
static void fire_trk_advance(FireTrkSong* s) {
    const FireTrkModule* m = s->module;
    int jumped, newOrder, newRow, wasLoopJump;
    s->tick++;
    if (s->tick < s->speed) return;
    s->tick = 0;
    if (s->patternDelay > 0) { s->patternDelay--; s->repeatRow = 1; return; }
    s->repeatRow = 0;
    jumped = s->jump;
    newOrder = s->order;
    newRow = s->row + 1;
    if (s->jump) {
        newOrder = s->jumpOrderSet ? s->jumpOrder : s->order + 1;
        newRow = s->jumpRow;
    }
    wasLoopJump = s->loopJump;
    s->jump = 0; s->jumpOrderSet = 0; s->loopJump = 0; s->jumpRow = 0;
    if (newRow >= FIRE_TRK_ROWS) { newRow = 0; newOrder++; }
    if (newOrder >= m->songLength) {
        if (s->loop) { newOrder = m->restart < m->songLength ? m->restart : 0; newRow = jumped ? newRow : 0; }
        else { s->finished = 1; return; }
    }
    if (!wasLoopJump && !s->loop) {
        size_t at = (size_t)newOrder * FIRE_TRK_ROWS + (size_t)newRow;
        if (jumped && s->visited[at]) { s->finished = 1; return; }   /* the song jumps back to where it has been: it would play for ever */
    }
    s->order = newOrder;
    s->row = newRow;
    s->visited[(size_t)s->order * FIRE_TRK_ROWS + (size_t)s->row] = 1;
}

/* Runs one tick of the replayer: after it the channels hold what is played for the next `tickFrames` samples. */
static void fire_trk_processTick(FireTrkSong* s) {
    const FireTrkModule* m = s->module;
    int64_t num, den;
    int i;
    for (i = 0; i < m->channels; i++) { s->ch[i].mixPeriod = 0; s->ch[i].mixVolume = -1; }   /* (what the effects of this tick do not change is the plain period and volume) */
    if (s->tick == 0 && !s->repeatRow) fire_trk_readRow(s);
    if (s->tick > 0 || s->repeatRow) for (i = 0; i < m->channels; i++) fire_trk_continuous(s, &s->ch[i]);
    for (i = 0; i < m->channels; i++) {
        FireTrkChannel* c = &s->ch[i];
        if (c->mixPeriod == 0) {
            c->mixPeriod = c->period;
            if (c->glissando && (c->effect == 3 || c->effect == 5)) c->mixPeriod = fire_trk_periods[fire_trk_fineOf(c)][fire_trk_noteIndex(c->period)];   /* glissando: the portamento steps from note to note */
        }
        if (c->mixVolume < 0) c->mixVolume = c->volume;
    }
    /* the length of the tick: rate * 2.5 / tempo samples, with the remainder carried over (so the tempo is exact over time) */
    num = (int64_t)s->rate * 5 + s->tickRemainder;
    den = 2 * (int64_t)s->tempo;
    s->tickFrames = (int)(num / den);
    s->tickRemainder = num % den;
    s->tickLeft = s->tickFrames;
    fire_trk_advance(s);
}

/* ---- the mixer ------------------------------------------------------------------------------------------------------------------------------- */
/* Mixes `frames` frames of the current tick into `out` (stereo: 2 values per frame; mono: 1). Returns after the tick or the frames. */
static int fire_trk_mixInto(FireTrkSong* s, int16_t* out, int frames, int outChannels) {
    const FireTrkModule* m = s->module;
    int n = frames < s->tickLeft ? frames : s->tickLeft;
    int perSide = m->channels / 2 > 0 ? m->channels / 2 : 1;
    int own = 100 + s->separation, other = 100 - s->separation;   /* of 200 */
    int32_t wantedVolume[FIRE_TRK_MAX_CHANNELS];
    uint64_t step[FIRE_TRK_MAX_CHANNELS];
    int32_t rampStep;
    int i, f;
    for (i = 0; i < m->channels; i++) {
        FireTrkChannel* c = &s->ch[i];
        int period = c->mixPeriod < 40 ? 40 : c->mixPeriod;
        wantedVolume[i] = c->mixVolume * 256;
        step[i] = c->active ? (uint64_t)(((uint64_t)FIRE_TRK_PAULA_CLOCK << 32) / ((uint64_t)period * (uint64_t)s->rate)) : 0;
    }
    rampStep = (int32_t)((64 * 256) / fire_trk_rampLength(s)) + 1;
    for (f = 0; f < n; f++) {
        int64_t left = 0, right = 0, l, r;
        for (i = 0; i < m->channels; i++) {
            FireTrkChannel* c = &s->ch[i];
            int32_t value = 0;
            int onLeft;
            if (c->active && !(s->muted & (1u << i))) {
                const FireTrkSample* smp = &m->samples[c->sample - 1];
                size_t idx = (size_t)(c->pos >> 32);
                int32_t a, b, v;
                size_t next;
                if (idx >= (size_t)smp->length) { c->active = 0; c->lastValue = 0; continue; }   /* (a new sample number without a note: the position may be behind the end of the other sample) */
                a = smp->data[idx];
                next = idx + 1;
                if (smp->loopLength > 0 && next >= (size_t)(smp->loopStart + smp->loopLength)) next = (size_t)smp->loopStart;
                if (next >= (size_t)smp->length) b = a; else b = smp->data[next];
                if (s->interpolate) v = a * 65536 + (b - a) * (int32_t)((c->pos >> 16) & 0xFFFF);
                else v = a * 65536;
                /* the volume moves towards the wanted one in a few samples */
                if (c->curVol < wantedVolume[i]) { c->curVol += rampStep; if (c->curVol > wantedVolume[i]) c->curVol = wantedVolume[i]; }
                else if (c->curVol > wantedVolume[i]) { c->curVol -= rampStep; if (c->curVol < wantedVolume[i]) c->curVol = wantedVolume[i]; }
                value = (int32_t)(((int64_t)v * c->curVol) >> 24);
                /* advance */
                c->pos += step[i];
                if (smp->loopLength > 0) {
                    uint64_t end = (uint64_t)(smp->loopStart + smp->loopLength) << 32;
                    if (c->pos >= end) c->pos = ((uint64_t)smp->loopStart << 32) + (c->pos - end) % ((uint64_t)smp->loopLength << 32);
                } else if ((c->pos >> 32) >= (uint64_t)smp->length) c->active = 0;
            } else if (c->curVol != 0) c->curVol = 0;
            c->lastValue = value;
            if (c->fadeLeft > 0) {   /* the end of the sample that was cut */
                value += (int32_t)((int64_t)c->fade * c->fadeLeft / fire_trk_rampLength(s));
                c->fadeLeft--;
            }
            onLeft = (i & 3) == 0 || (i & 3) == 3;
            left += onLeft ? (int64_t)value * own : (int64_t)value * other;
            right += onLeft ? (int64_t)value * other : (int64_t)value * own;
        }
        /* scale: one channel at full volume and full sample is 8192; the channels of a side add up to at most 32767 */
        l = left * 4 * s->gain / ((int64_t)200 * perSide * 100);
        r = right * 4 * s->gain / ((int64_t)200 * perSide * 100);
        if (l > 32767) l = 32767; else if (l < -32768) l = -32768;
        if (r > 32767) r = 32767; else if (r < -32768) r = -32768;
        if (outChannels == 2) { out[2 * f] = (int16_t)l; out[2 * f + 1] = (int16_t)r; }
        else out[f] = (int16_t)((l + r) / 2);
    }
    s->tickLeft -= n;
    return n;
}

/* Renders up to `frames` frames; fewer only at the end of the song. Interleaved 16 bit samples (`outChannels` 1 or 2). */
static int fire_trk_render(FireTrkSong* s, int16_t* out, int frames, int outChannels) {
    int done = 0;
    while (done < frames) {
        if (s->tickLeft == 0) {
            if (s->finished) break;
            fire_trk_processTick(s);
            if (s->tickLeft == 0) continue;   /* (a tick of no length: a very slow rate with a fast tempo) */
        }
        done += fire_trk_mixInto(s, out + (size_t)done * (size_t)outChannels, frames - done, outChannels);
    }
    return done;
}

/* Runs the replayer without sound until `order`/`row` is reached (the song keeps its speed, tempo and volumes), or to the end. Returns whether it got there. */
static int fire_trk_seek(FireTrkSong* s, int toOrder, int toRow) {
    int wasLoop = s->loop;
    int reached = 1;
    int guard = 0;
    fire_trk_restart(s);
    s->loop = 0;   /* (the dry run ends when the song would start over: a position that is never played is not found for ever) */
    while (!(s->order == toOrder && s->row == toRow && s->tick == 0)) {
        if (s->finished || ++guard > 20000000) { reached = 0; break; }
        fire_trk_processTick(s);
        s->tickLeft = 0;
    }
    s->loop = wasLoop;
    if (reached) {
        int i;
        s->finished = 0;
        for (i = 0; i < s->module->channels; i++) { s->ch[i].curVol = s->ch[i].mixVolume * 256; s->ch[i].fadeLeft = 0; }
    }
    return reached;
}

/* The length of the song in frames at the current rate (a dry run; 0 if it never ends: a module that loops by itself is counted until it jumps back);
 * -1 when the memory for the dry run is gone. */
static int64_t fire_trk_lengthFrames(const FireTrkSong* s) {
    FireTrkSong* copy = (FireTrkSong*)malloc(sizeof(FireTrkSong));
    int64_t total = 0;
    int guard = 0;
    if (copy == NULL) return -1;
    memcpy(copy, s, sizeof(FireTrkSong));
    fire_trk_restart(copy);
    copy->loop = 0;
    while (!copy->finished && ++guard < 10000000) {
        fire_trk_processTick(copy);
        total += copy->tickFrames;
        copy->tickLeft = 0;
        if (total > (int64_t)copy->rate * 7200) break;   /* two hours: not a song */
    }
    free(copy);
    return total;
}

#ifndef FIRE_TRACKER_ENGINE_ONLY

static int64_t fire_trkb_fail(int code, const char* message) {
    g_ioError.code = code;
    snprintf(g_ioError.message, sizeof g_ioError.message, "%s", message);
    return -1;
}
static void fire_trkb_ok(void) { g_ioError.code = 0; g_ioError.message[0] = 0; }

static Value fire_trkb_str8(const char* text, OwnList* list) {
    size_t n = strlen(text), i;
    Str* s = allocStr((uint32_t)n, list);
    for (i = 0; i < n; i++) strChars(s)[i] = (fire_char16)(uint8_t)text[i];
    return StrV(s);
}

/* The songs; the handle is the index (0 is not used, handles are not used again). Each song owns its module. */
static FireTrkSong** g_trackerSongs = NULL;
static size_t g_trackerSongCount = 1, g_trackerSongCap = 0;

static void fire_trkb_release(FireTrkSong* s) {
    if (s == NULL) return;
    fire_trk_freeModule((FireTrkModule*)s->module);
    free(s);
}

/* The program has ended (the library for the VM stays loaded for the next one): drops the songs. */
static void fire_tracker_reset(void) {
    size_t i;
    for (i = 1; i < g_trackerSongCount; i++) { fire_trkb_release(g_trackerSongs[i]); g_trackerSongs[i] = NULL; }
}

static FireTrkSong* fire_trkb_find(Value h) {
    int64_t i = h.i;
    if (i > 0 && (size_t)i < g_trackerSongCount && g_trackerSongs[i] != NULL) return g_trackerSongs[i];
    fire_trkb_fail(FIRE_TRK_E_INVALID_HANDLE, "Invalid or closed song handle.");
    return NULL;
}

static Value fire_tracker_LastError(void) { return Int(g_ioError.code); }
static Value fire_tracker_LastErrorMessage(OwnList* list) { return fire_trkb_str8(g_ioError.message, list); }
static Value fire_tracker_OpenCount(void) {
    int64_t n = 0;
    size_t i;
    for (i = 1; i < g_trackerSongCount; i++) if (g_trackerSongs[i]) n++;
    return Int(n);
}

/* Reads a module from `count` bytes of the buffer; the handle of the song, -1 on an error (BadFormat: it is not a module). */
static Value fire_tracker_Load(Value buffer, Value offset, Value count, Value rate) {
    int64_t length;
    FireTrkModule* module;
    FireTrkSong* song;
    const char* error = "";
    int code = 0;
    FireBytes in;
    if (!bytesArg(buffer, "Tracker.Song.Load", &in)) return Undef();
    length = in.length;
    if (offset.i < 0 || count.i < 0 || offset.i > length || count.i > length - offset.i) return Int(fire_trkb_fail(FIRE_TRK_E_INVALID_ARGUMENT, "offset/count are outside of the buffer."));
    if (rate.i < 8000 || rate.i > 192000) return Int(fire_trkb_fail(FIRE_TRK_E_INVALID_ARGUMENT, "The rate must be between 8000 and 192000 Hz."));
    module = fire_trk_parseModule(in.data + offset.i, (size_t)count.i, &error, &code);
    if (module == NULL) return Int(fire_trkb_fail(code, error));
    if (g_trackerSongCount >= g_trackerSongCap) {
        size_t cap = g_trackerSongCap ? g_trackerSongCap * 2 : 16;
        FireTrkSong** grown = (FireTrkSong**)realloc(g_trackerSongs, sizeof(FireTrkSong*) * cap);
        if (grown == NULL) { fire_trk_freeModule(module); return Int(fire_trkb_fail(FIRE_TRK_E_OTHER, "Out of memory.")); }
        if (g_trackerSongCap == 0) grown[0] = NULL;
        g_trackerSongs = grown;
        g_trackerSongCap = cap;
    }
    song = fire_trk_newSong(module, (int)rate.i);
    if (song == NULL) { fire_trk_freeModule(module); return Int(fire_trkb_fail(FIRE_TRK_E_OTHER, "Out of memory.")); }
    g_trackerSongs[g_trackerSongCount] = song;
    fire_trkb_ok();
    return Int((int64_t)g_trackerSongCount++);
}

static Value fire_tracker_Close(Value h) {
    int64_t i = h.i;
    if (i <= 0 || (size_t)i >= g_trackerSongCount || g_trackerSongs[i] == NULL) { fire_trkb_fail(FIRE_TRK_E_INVALID_HANDLE, "Invalid or closed song handle."); return Bool(false); }
    fire_trkb_release(g_trackerSongs[i]);
    g_trackerSongs[i] = NULL;
    fire_trkb_ok();
    return Bool(true);
}

/* Renders up to `frames` frames of 16 bit samples (channels 1 or 2) into the buffer from `offset`; the number rendered (less than asked only at the end of the song),
 * -1 on an error. */
static Value fire_tracker_Render(Value h, Value buffer, Value offset, Value frames, Value channels) {
    FireTrkSong* s;
    int64_t frameBytes, length;
    int16_t* out;
    int n;
    Buf* b = bufferArg(buffer, "Tracker.Song.Render");
    if (b == NULL) return Undef();
    s = fire_trkb_find(h);
    if (s == NULL) return Int(-1);
    if (channels.i < 1 || channels.i > 2) return Int(fire_trkb_fail(FIRE_TRK_E_INVALID_ARGUMENT, "Mono (1) or stereo (2) channels."));
    frameBytes = 2 * channels.i;
    length = b->length;
    if (offset.i < 0 || frames.i < 0 || offset.i > length || frames.i * frameBytes > length - offset.i) {
        char text[176];
        snprintf(text, sizeof text, "offset/frames are outside of the buffer (length %lld).", (long long)length);
        return Int(fire_trkb_fail(FIRE_TRK_E_INVALID_ARGUMENT, text));
    }
    out = (int16_t*)(void*)(b->data + offset.i);
    /* (the samples are little endian in the buffer: a big endian host would swap them here; there is none we build for) */
    n = fire_trk_render(s, out, (int)frames.i, (int)channels.i);
    fire_trkb_ok();
    return Int(n);
}

/* What the song knows: 0 channels, 1 orders (song length), 2 patterns, 3 order now, 4 row now, 5 speed, 6 tempo, 7 finished, 8 length in milliseconds (a dry run),
 * 9 rate, 10 loop, 11 separation, 12 interpolation, 13 gain, 14 muted channels (bit mask), 15 title length. */
static Value fire_tracker_Get(Value h, Value what) {
    FireTrkSong* s = fire_trkb_find(h);
    if (s == NULL) return Int(-1);
    fire_trkb_ok();
    switch (what.i) {
        case 0: return Int(s->module->channels);
        case 1: return Int(s->module->songLength);
        case 2: return Int(s->module->patterns);
        case 3: return Int(s->order);
        case 4: return Int(s->row);
        case 5: return Int(s->speed);
        case 6: return Int(s->tempo);
        case 7: return Int(s->finished ? 1 : 0);
        case 8: {
            int64_t frames = fire_trk_lengthFrames(s);
            if (frames < 0) return Int(fire_trkb_fail(FIRE_TRK_E_OTHER, "Out of memory."));
            return Int(frames * 1000 / s->rate);
        }
        case 9: return Int(s->rate);
        case 10: return Int(s->loop ? 1 : 0);
        case 11: return Int(s->separation);
        case 12: return Int(s->interpolate ? 1 : 0);
        case 13: return Int(s->gain);
        case 14: return Int((int64_t)s->muted);
        default: return Int(fire_trkb_fail(FIRE_TRK_E_INVALID_ARGUMENT, "Unknown information."));
    }
}

/* Changes a setting: 0 loop (0/1), 1 separation (0..100), 2 interpolation (0/1), 3 gain (percent), 4 muted channels (bit mask), 5 rate. */
static Value fire_tracker_Set(Value h, Value what, Value value) {
    FireTrkSong* s = fire_trkb_find(h);
    if (s == NULL) return Bool(false);
    switch (what.i) {
        case 0: s->loop = value.i != 0; break;
        case 1:
            if (value.i < 0 || value.i > 100) { fire_trkb_fail(FIRE_TRK_E_INVALID_ARGUMENT, "The stereo separation is between 0 and 100 percent."); return Bool(false); }
            s->separation = (int)value.i;
            break;
        case 2: s->interpolate = value.i != 0; break;
        case 3:
            if (value.i < 0 || value.i > 400) { fire_trkb_fail(FIRE_TRK_E_INVALID_ARGUMENT, "The gain is between 0 and 400 percent."); return Bool(false); }
            s->gain = (int)value.i;
            break;
        case 4: s->muted = (uint32_t)value.i; break;
        case 5:
            if (value.i < 8000 || value.i > 192000) { fire_trkb_fail(FIRE_TRK_E_INVALID_ARGUMENT, "The rate must be between 8000 and 192000 Hz."); return Bool(false); }
            s->rate = (int)value.i;
            break;
        default: fire_trkb_fail(FIRE_TRK_E_INVALID_ARGUMENT, "Unknown setting."); return Bool(false);
    }
    fire_trkb_ok();
    return Bool(true);
}

/* Text: what 0 the title, 1 the name of the sample `index` (0..30). */
static Value fire_tracker_Text(Value h, Value what, Value index, OwnList* list) {
    FireTrkSong* s = fire_trkb_find(h);
    if (s == NULL) return Undef();
    fire_trkb_ok();
    if (what.i == 0) return fire_trkb_str8(s->module->title, list);
    if (what.i == 1 && index.i >= 0 && index.i < 31) return fire_trkb_str8(s->module->samples[index.i].name, list);
    fire_trkb_fail(FIRE_TRK_E_INVALID_ARGUMENT, "Unknown text or sample number.");
    return Undef();
}

/* About a sample (0..30): 0 length in bytes, 1 finetune (-8..7), 2 volume, 3 loop start (bytes), 4 loop length (bytes, 0: no loop). */
static Value fire_tracker_SampleInfo(Value h, Value index, Value what) {
    FireTrkSong* s = fire_trkb_find(h);
    const FireTrkSample* smp;
    if (s == NULL) return Int(-1);
    if (index.i < 0 || index.i > 30) return Int(fire_trkb_fail(FIRE_TRK_E_INVALID_ARGUMENT, "Sample numbers are 0..30."));
    smp = &s->module->samples[index.i];
    fire_trkb_ok();
    switch (what.i) {
        case 0: return Int(smp->length);
        case 1: return Int(smp->finetune >= 8 ? smp->finetune - 16 : smp->finetune);
        case 2: return Int(smp->volume);
        case 3: return Int(smp->loopStart);
        case 4: return Int(smp->loopLength);
        default: return Int(fire_trkb_fail(FIRE_TRK_E_INVALID_ARGUMENT, "Unknown information."));
    }
}

/* Starts again at the beginning. */
static Value fire_tracker_Restart(Value h) {
    FireTrkSong* s = fire_trkb_find(h);
    if (s == NULL) return Bool(false);
    fire_trk_restart(s);
    fire_trkb_ok();
    return Bool(true);
}

/* Goes to a position (silently: speed, tempo and volumes are those of the song at that point). False if the song never gets there. */
static Value fire_tracker_Seek(Value h, Value order, Value row) {
    FireTrkSong* s = fire_trkb_find(h);
    int reached;
    if (s == NULL) return Bool(false);
    if (order.i < 0 || order.i >= s->module->songLength || row.i < 0 || row.i >= FIRE_TRK_ROWS) { fire_trkb_fail(FIRE_TRK_E_INVALID_ARGUMENT, "Order or row outside of the song."); return Bool(false); }
    reached = fire_trk_seek(s, (int)order.i, (int)row.i);
    if (!reached) fire_trk_restart(s);
    fire_trkb_ok();
    return Bool(reached != 0);
}

/* A cell of the pattern played at `order`: what 0 sample, 1 period, 2 effect, 3 parameter; the channel's state: what 4 volume, 5 period, 6 sample, 7 playing (a sample
 * is sounding). */
static Value fire_tracker_PatternCell(Value h, Value order, Value row, Value channel, Value what) {
    FireTrkSong* s = fire_trkb_find(h);
    const FireTrkModule* m;
    const FireTrkCell* cell;
    if (s == NULL) return Int(-1);
    m = s->module;
    if (channel.i < 0 || channel.i >= m->channels) return Int(fire_trkb_fail(FIRE_TRK_E_INVALID_ARGUMENT, "Channel outside of the song."));
    fire_trkb_ok();
    if (what.i >= 4) {
        const FireTrkChannel* c = &s->ch[channel.i];
        switch (what.i) {
            case 4: return Int(c->mixVolume < 0 ? c->volume : c->mixVolume);
            case 5: return Int(c->mixPeriod ? c->mixPeriod : c->period);
            case 6: return Int(c->sample);
            case 7: return Int(c->active ? 1 : 0);
            default: return Int(fire_trkb_fail(FIRE_TRK_E_INVALID_ARGUMENT, "Unknown information."));
        }
    }
    if (order.i < 0 || order.i >= m->songLength || row.i < 0 || row.i >= FIRE_TRK_ROWS) return Int(fire_trkb_fail(FIRE_TRK_E_INVALID_ARGUMENT, "Order or row outside of the song."));
    cell = fire_trk_cell(m, m->orders[order.i], (int)row.i, (int)channel.i);
    switch (what.i) {
        case 0: return Int(cell->sample);
        case 1: return Int(cell->period);
        case 2: return Int(cell->effect);
        case 3: return Int(cell->param);
        default: return Int(fire_trkb_fail(FIRE_TRK_E_INVALID_ARGUMENT, "Unknown information."));
    }
}

#endif  /* FIRE_TRACKER_ENGINE_ONLY */

#endif
