/* fire compression engine (C99, no library): Deflate (RFC 1951) both ways, with the framings zlib (RFC 1950) and gzip (RFC 1952). Both directions are
 * streams: the inflater takes its input in pieces of any size (it stops at a symbol boundary when the input runs out and goes on when more comes), the
 * deflater takes data in pieces and gives the compressed bytes out as they are made. Nothing here knows the runtime; fire_bridge_compression.h puts it behind
 * natives. Errors are texts (`error`), never a crash: the inflater checks everything a broken or hostile stream can get wrong.
 *
 * The deflater follows the well-known design of zlib (hash chains over a 32 KB window, lazy matching from level 4, per block the cheapest of a dynamic, the
 * fixed and a stored encoding); the code is our own. Levels 0 (stored) .. 9. */
#ifndef FIRE_CMP_DEFLATE_H
#define FIRE_CMP_DEFLATE_H

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

enum { FIRE_CMP_RAW = 0, FIRE_CMP_ZLIB = 1, FIRE_CMP_GZIP = 2 };

#define FIRE_CMP_WSIZE 32768u
#define FIRE_CMP_WMASK (FIRE_CMP_WSIZE - 1)

/* ---- checksums ------------------------------------------------------------------------------------------------------------------------------------ */
static const uint32_t fire_cmp_crcTab[256] = {
    0x00000000u, 0x77073096u, 0xee0e612cu, 0x990951bau, 0x076dc419u, 0x706af48fu, 0xe963a535u, 0x9e6495a3u,
    0x0edb8832u, 0x79dcb8a4u, 0xe0d5e91eu, 0x97d2d988u, 0x09b64c2bu, 0x7eb17cbdu, 0xe7b82d07u, 0x90bf1d91u,
    0x1db71064u, 0x6ab020f2u, 0xf3b97148u, 0x84be41deu, 0x1adad47du, 0x6ddde4ebu, 0xf4d4b551u, 0x83d385c7u,
    0x136c9856u, 0x646ba8c0u, 0xfd62f97au, 0x8a65c9ecu, 0x14015c4fu, 0x63066cd9u, 0xfa0f3d63u, 0x8d080df5u,
    0x3b6e20c8u, 0x4c69105eu, 0xd56041e4u, 0xa2677172u, 0x3c03e4d1u, 0x4b04d447u, 0xd20d85fdu, 0xa50ab56bu,
    0x35b5a8fau, 0x42b2986cu, 0xdbbbc9d6u, 0xacbcf940u, 0x32d86ce3u, 0x45df5c75u, 0xdcd60dcfu, 0xabd13d59u,
    0x26d930acu, 0x51de003au, 0xc8d75180u, 0xbfd06116u, 0x21b4f4b5u, 0x56b3c423u, 0xcfba9599u, 0xb8bda50fu,
    0x2802b89eu, 0x5f058808u, 0xc60cd9b2u, 0xb10be924u, 0x2f6f7c87u, 0x58684c11u, 0xc1611dabu, 0xb6662d3du,
    0x76dc4190u, 0x01db7106u, 0x98d220bcu, 0xefd5102au, 0x71b18589u, 0x06b6b51fu, 0x9fbfe4a5u, 0xe8b8d433u,
    0x7807c9a2u, 0x0f00f934u, 0x9609a88eu, 0xe10e9818u, 0x7f6a0dbbu, 0x086d3d2du, 0x91646c97u, 0xe6635c01u,
    0x6b6b51f4u, 0x1c6c6162u, 0x856530d8u, 0xf262004eu, 0x6c0695edu, 0x1b01a57bu, 0x8208f4c1u, 0xf50fc457u,
    0x65b0d9c6u, 0x12b7e950u, 0x8bbeb8eau, 0xfcb9887cu, 0x62dd1ddfu, 0x15da2d49u, 0x8cd37cf3u, 0xfbd44c65u,
    0x4db26158u, 0x3ab551ceu, 0xa3bc0074u, 0xd4bb30e2u, 0x4adfa541u, 0x3dd895d7u, 0xa4d1c46du, 0xd3d6f4fbu,
    0x4369e96au, 0x346ed9fcu, 0xad678846u, 0xda60b8d0u, 0x44042d73u, 0x33031de5u, 0xaa0a4c5fu, 0xdd0d7cc9u,
    0x5005713cu, 0x270241aau, 0xbe0b1010u, 0xc90c2086u, 0x5768b525u, 0x206f85b3u, 0xb966d409u, 0xce61e49fu,
    0x5edef90eu, 0x29d9c998u, 0xb0d09822u, 0xc7d7a8b4u, 0x59b33d17u, 0x2eb40d81u, 0xb7bd5c3bu, 0xc0ba6cadu,
    0xedb88320u, 0x9abfb3b6u, 0x03b6e20cu, 0x74b1d29au, 0xead54739u, 0x9dd277afu, 0x04db2615u, 0x73dc1683u,
    0xe3630b12u, 0x94643b84u, 0x0d6d6a3eu, 0x7a6a5aa8u, 0xe40ecf0bu, 0x9309ff9du, 0x0a00ae27u, 0x7d079eb1u,
    0xf00f9344u, 0x8708a3d2u, 0x1e01f268u, 0x6906c2feu, 0xf762575du, 0x806567cbu, 0x196c3671u, 0x6e6b06e7u,
    0xfed41b76u, 0x89d32be0u, 0x10da7a5au, 0x67dd4accu, 0xf9b9df6fu, 0x8ebeeff9u, 0x17b7be43u, 0x60b08ed5u,
    0xd6d6a3e8u, 0xa1d1937eu, 0x38d8c2c4u, 0x4fdff252u, 0xd1bb67f1u, 0xa6bc5767u, 0x3fb506ddu, 0x48b2364bu,
    0xd80d2bdau, 0xaf0a1b4cu, 0x36034af6u, 0x41047a60u, 0xdf60efc3u, 0xa867df55u, 0x316e8eefu, 0x4669be79u,
    0xcb61b38cu, 0xbc66831au, 0x256fd2a0u, 0x5268e236u, 0xcc0c7795u, 0xbb0b4703u, 0x220216b9u, 0x5505262fu,
    0xc5ba3bbeu, 0xb2bd0b28u, 0x2bb45a92u, 0x5cb36a04u, 0xc2d7ffa7u, 0xb5d0cf31u, 0x2cd99e8bu, 0x5bdeae1du,
    0x9b64c2b0u, 0xec63f226u, 0x756aa39cu, 0x026d930au, 0x9c0906a9u, 0xeb0e363fu, 0x72076785u, 0x05005713u,
    0x95bf4a82u, 0xe2b87a14u, 0x7bb12baeu, 0x0cb61b38u, 0x92d28e9bu, 0xe5d5be0du, 0x7cdcefb7u, 0x0bdbdf21u,
    0x86d3d2d4u, 0xf1d4e242u, 0x68ddb3f8u, 0x1fda836eu, 0x81be16cdu, 0xf6b9265bu, 0x6fb077e1u, 0x18b74777u,
    0x88085ae6u, 0xff0f6a70u, 0x66063bcau, 0x11010b5cu, 0x8f659effu, 0xf862ae69u, 0x616bffd3u, 0x166ccf45u,
    0xa00ae278u, 0xd70dd2eeu, 0x4e048354u, 0x3903b3c2u, 0xa7672661u, 0xd06016f7u, 0x4969474du, 0x3e6e77dbu,
    0xaed16a4au, 0xd9d65adcu, 0x40df0b66u, 0x37d83bf0u, 0xa9bcae53u, 0xdebb9ec5u, 0x47b2cf7fu, 0x30b5ffe9u,
    0xbdbdf21cu, 0xcabac28au, 0x53b39330u, 0x24b4a3a6u, 0xbad03605u, 0xcdd70693u, 0x54de5729u, 0x23d967bfu,
    0xb3667a2eu, 0xc4614ab8u, 0x5d681b02u, 0x2a6f2b94u, 0xb40bbe37u, 0xc30c8ea1u, 0x5a05df1bu, 0x2d02ef8du };
/* CRC-32 of the bytes, continued from `crc` (0 to start). */
static uint32_t fire_cmp_crc(uint32_t crc, const uint8_t* p, size_t n) {
    const uint32_t* t = fire_cmp_crcTab;
    size_t i;
    crc ^= 0xFFFFFFFFu;
    for (i = 0; i < n; i++) crc = t[(crc ^ p[i]) & 0xFF] ^ (crc >> 8);
    return crc ^ 0xFFFFFFFFu;
}
/* Adler-32, continued from `adler` (1 to start). */
static uint32_t fire_cmp_adler(uint32_t adler, const uint8_t* p, size_t n) {
    uint32_t a = adler & 0xFFFF, b = adler >> 16;
    while (n > 0) {
        size_t chunk = n < 5552 ? n : 5552, i;
        for (i = 0; i < chunk; i++) { a += p[i]; b += a; }
        a %= 65521u;
        b %= 65521u;
        p += chunk;
        n -= chunk;
    }
    return (b << 16) | a;
}

/* ---- the tables of Deflate ------------------------------------------------------------------------------------------------------------------------ */
static const uint16_t fire_cmp_lenBase[29] = { 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258 };
static const uint8_t fire_cmp_lenExtra[29] = { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0 };
static const uint16_t fire_cmp_distBase[30] = { 1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145,
                                                8193, 12289, 16385, 24577 };
static const uint8_t fire_cmp_distExtra[30] = { 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13 };
static const uint8_t fire_cmp_clOrder[19] = { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };

/* ======================================================================================================================================================
 * Inflate
 * ====================================================================================================================================================== */
#define FIRE_CMP_FAST 10

/* A canonical Huffman code for decoding: the number of codes per length, the symbols in code order, and a table for the codes of up to FIRE_CMP_FAST bits
 * (entry: length << 9 | symbol, 0: the code is longer - decoded bit by bit). */
typedef struct FireCmpHuff {
    uint16_t count[16];
    uint16_t symbol[288];
    uint16_t fast[1 << FIRE_CMP_FAST];
} FireCmpHuff;

/* Builds the code from the lengths: 0 ok, -1 over-subscribed, -2 incomplete (only a single code of length 1 may be incomplete). */
static int fire_cmp_build(FireCmpHuff* h, const uint8_t* lengths, int n) {
    uint16_t offs[16], next[16];
    int len, sym, left = 1, used = 0;
    uint32_t code = 0;
    memset(h->count, 0, sizeof h->count);
    memset(h->fast, 0, sizeof h->fast);
    for (sym = 0; sym < n; sym++) h->count[lengths[sym]]++;
    used = n - h->count[0];
    for (len = 1; len < 16; len++) {
        left <<= 1;
        left -= h->count[len];
        if (left < 0) return -1;
    }
    offs[1] = 0;
    for (len = 1; len < 15; len++) offs[len + 1] = (uint16_t)(offs[len] + h->count[len]);
    for (sym = 0; sym < n; sym++) if (lengths[sym] != 0) h->symbol[offs[lengths[sym]]++] = (uint16_t)sym;
    /* the fast table: canonical codes, bit-reversed (Deflate sends Huffman codes from their highest bit, the bit buffer is read from the lowest) */
    next[0] = 0;
    h->count[0] = 0;
    for (len = 1; len < 16; len++) { code = (code + h->count[len - 1]) << 1; next[len] = (uint16_t)code; }
    for (sym = 0; sym < n; sym++) {
        int l = lengths[sym];
        uint32_t c, rev = 0;
        int b;
        if (l == 0) continue;
        c = next[l]++;
        if (l > FIRE_CMP_FAST) continue;
        for (b = 0; b < l; b++) rev |= ((c >> b) & 1u) << (l - 1 - b);
        for (; rev < (1u << FIRE_CMP_FAST); rev += 1u << l) h->fast[rev] = (uint16_t)((l << 9) | sym);
    }
    if (left > 0 && !(used == 1 && h->count[1] == 1)) return -2;
    return 0;
}

enum { FIRE_INF_HEADER, FIRE_INF_BLOCK, FIRE_INF_STORED, FIRE_INF_CODES, FIRE_INF_TRAILER, FIRE_INF_NEXT_MEMBER, FIRE_INF_DONE, FIRE_INF_ERROR };

typedef struct FireInflate {
    int format, state;
    /* the input that came and was not used yet; up to 8 bytes before `inPos` are kept (the bit buffer can give whole bytes back) */
    uint8_t* in;
    size_t inLen, inPos, inCap;
    uint64_t bits;
    int nbits;
    int ended;           /* no more input will come */
    int sawInput;        /* any input came at all (no input at all is empty data, not a broken stream) */
    /* the last 32 KB of output */
    uint8_t win[FIRE_CMP_WSIZE];
    uint32_t wpos, whave;
    int final;
    uint32_t storedLeft, copyLen, copyDist;
    FireCmpHuff lit, dist;
    /* the check of the framing */
    uint32_t check;
    uint64_t total;      /* output of this member (gzip: ISIZE) */
    size_t checkFrom;    /* the output of this call from here on is not in `check` yet */
    int members;         /* gzip members done */
    char error[160];
} FireInflate;

static FireInflate* fire_inf_new(int format) {
    FireInflate* z = (FireInflate*)calloc(1, sizeof(FireInflate));
    if (z == NULL) return NULL;
    z->format = format;
    z->state = FIRE_INF_HEADER;
    z->check = format == FIRE_CMP_ZLIB ? 1u : 0u;
    return z;
}
static void fire_inf_free(FireInflate* z) {
    if (z == NULL) return;
    free(z->in);
    free(z);
}
static int fire_inf_fail(FireInflate* z, const char* text) {
    snprintf(z->error, sizeof z->error, "%s", text);
    z->state = FIRE_INF_ERROR;
    return -1;
}

/* More input: copied (the bytes before the read position are dropped, except the last 8). 0: out of memory. */
static int fire_inf_feed(FireInflate* z, const uint8_t* p, size_t n) {
    size_t keep = z->inPos > 8 ? z->inPos - 8 : 0;
    if (keep > 0) {
        memmove(z->in, z->in + keep, z->inLen - keep);
        z->inLen -= keep;
        z->inPos -= keep;
    }
    if (z->inLen + n > z->inCap) {
        size_t cap = z->inCap ? z->inCap : 4096;
        uint8_t* grown;
        while (cap < z->inLen + n) cap *= 2;
        grown = (uint8_t*)realloc(z->in, cap);
        if (grown == NULL) return 0;
        z->in = grown;
        z->inCap = cap;
    }
    if (n > 0) { memcpy(z->in + z->inLen, p, n); z->sawInput = 1; }
    z->inLen += n;
    return 1;
}

/* Fills the bit buffer from the input as far as it goes (up to 56 bits). */
static void fire_inf_fill(FireInflate* z) {
    while (z->nbits <= 48 && z->inPos < z->inLen) {
        z->bits |= (uint64_t)z->in[z->inPos++] << z->nbits;
        z->nbits += 8;
    }
}
static int fire_inf_need(FireInflate* z, int n) {
    if (z->nbits < n) fire_inf_fill(z);
    return z->nbits >= n;
}
static uint32_t fire_inf_take(FireInflate* z, int n) {
    uint32_t v = (uint32_t)(z->bits & ((n == 32) ? 0xFFFFFFFFu : ((1u << n) - 1)));
    z->bits >>= n;
    z->nbits -= n;
    return v;
}
/* To the next byte boundary: the rest of the current byte is dropped, the whole bytes in the bit buffer go back to the input. */
static void fire_inf_align(FireInflate* z) {
    int drop = z->nbits & 7;
    z->bits >>= drop;
    z->nbits -= drop;
    z->inPos -= (size_t)(z->nbits / 8);
    z->bits = 0;
    z->nbits = 0;
}
static size_t fire_inf_avail(const FireInflate* z) { return z->inLen - z->inPos; }

/* Decodes a symbol: 1 done, 0 more input is needed (nothing taken), -1 no such code. */
static int fire_inf_decode(FireInflate* z, const FireCmpHuff* h, int* sym) {
    uint32_t e;
    int len, code = 0, first = 0, index = 0;
    fire_inf_fill(z);
    e = h->fast[z->bits & ((1u << FIRE_CMP_FAST) - 1)];
    if (e != 0 && (int)(e >> 9) <= z->nbits) {
        *sym = (int)(e & 511);
        z->bits >>= e >> 9;
        z->nbits -= (int)(e >> 9);
        return 1;
    }
    for (len = 1; len < 16; len++) {
        int count;
        if (len > z->nbits) return 0;
        code |= (int)((z->bits >> (len - 1)) & 1);
        count = h->count[len];
        if (code - count < first) {
            *sym = h->symbol[index + (code - first)];
            z->bits >>= len;
            z->nbits -= len;
            return 1;
        }
        index += count;
        first += count;
        first <<= 1;
        code <<= 1;
    }
    return -1;
}

static void fire_inf_fixed(FireInflate* z) {
    uint8_t l[288];
    int i;
    for (i = 0; i < 144; i++) l[i] = 8;
    for (; i < 256; i++) l[i] = 9;
    for (; i < 280; i++) l[i] = 7;
    for (; i < 288; i++) l[i] = 8;
    fire_cmp_build(&z->lit, l, 288);
    for (i = 0; i < 30; i++) l[i] = 5;
    fire_cmp_build(&z->dist, l, 30);
}

/* The tables of a dynamic block: 1 done, 0 more input is needed (nothing taken), -1 broken. */
static int fire_inf_dynamic(FireInflate* z) {
    uint8_t lengths[320], cl[19];
    FireCmpHuff clh;
    int nlen, ndist, ncode, i, index, r;
    if (!fire_inf_need(z, 14)) return 0;
    nlen = (int)fire_inf_take(z, 5) + 257;
    ndist = (int)fire_inf_take(z, 5) + 1;
    ncode = (int)fire_inf_take(z, 4) + 4;
    if (nlen > 286 || ndist > 30) return fire_inf_fail(z, "The compressed data is broken (too many codes in a block).");
    memset(cl, 0, sizeof cl);
    for (i = 0; i < ncode; i++) {
        if (!fire_inf_need(z, 3)) return 0;
        cl[fire_cmp_clOrder[i]] = (uint8_t)fire_inf_take(z, 3);
    }
    if (fire_cmp_build(&clh, cl, 19) != 0) return fire_inf_fail(z, "The compressed data is broken (the code of the code lengths).");
    index = 0;
    while (index < nlen + ndist) {
        int sym, len = 0, repeat;
        r = fire_inf_decode(z, &clh, &sym);
        if (r == 0) return 0;
        if (r < 0) return fire_inf_fail(z, "The compressed data is broken (a code length).");
        if (sym < 16) { lengths[index++] = (uint8_t)sym; continue; }
        if (sym == 16) {
            if (index == 0) return fire_inf_fail(z, "The compressed data is broken (a repeat without a length).");
            if (!fire_inf_need(z, 2)) return 0;
            len = lengths[index - 1];
            repeat = 3 + (int)fire_inf_take(z, 2);
        } else if (sym == 17) {
            if (!fire_inf_need(z, 3)) return 0;
            repeat = 3 + (int)fire_inf_take(z, 3);
        } else {
            if (!fire_inf_need(z, 7)) return 0;
            repeat = 11 + (int)fire_inf_take(z, 7);
        }
        if (index + repeat > nlen + ndist) return fire_inf_fail(z, "The compressed data is broken (too many code lengths).");
        while (repeat-- > 0) lengths[index++] = (uint8_t)len;
    }
    if (lengths[256] == 0) return fire_inf_fail(z, "The compressed data is broken (no end-of-block code).");
    if (fire_cmp_build(&z->lit, lengths, nlen) != 0) return fire_inf_fail(z, "The compressed data is broken (the literal/length code).");
    r = fire_cmp_build(&z->dist, lengths + nlen, ndist);
    if (r == -1) return fire_inf_fail(z, "The compressed data is broken (the distance code).");
    if (r == -2) {
        /* an incomplete distance code is allowed when it has a single code (or none: a block of literals) */
        int usedDist = ndist - z->dist.count[0];
        if (usedDist > 1) return fire_inf_fail(z, "The compressed data is broken (the distance code).");
    }
    return 1;
}

/* The gzip header at the read position (byte aligned): 1 done, 0 more input is needed (nothing taken), -1 broken. */
static int fire_inf_gzipHeader(FireInflate* z) {
    const uint8_t* p = z->in + z->inPos;
    size_t n = fire_inf_avail(z), at = 10;
    int flags;
    if ((n >= 1 && p[0] != 0x1F) || (n >= 2 && p[1] != 0x8B)) return fire_inf_fail(z, "This is not gzip data (the magic number is missing).");
    if (n < 10) return 0;
    if (p[2] != 8) return fire_inf_fail(z, "The gzip data uses an unknown compression method.");
    flags = p[3];
    if (flags & 0xE0) return fire_inf_fail(z, "The gzip header has unknown flags.");
    if (flags & 4) {   /* FEXTRA */
        size_t xlen;
        if (n < at + 2) return 0;
        xlen = (size_t)p[at] | ((size_t)p[at + 1] << 8);
        at += 2 + xlen;
        if (n < at) return 0;
    }
    if (flags & 8) { while (at < n && p[at] != 0) at++; if (at >= n) return 0; at++; }    /* FNAME */
    if (flags & 16) { while (at < n && p[at] != 0) at++; if (at >= n) return 0; at++; }   /* FCOMMENT */
    if (flags & 2) { at += 2; if (n < at) return 0; }                                      /* FHCRC */
    z->inPos += at;
    return 1;
}

/* Puts out a byte of the result. */
static void fire_inf_put(FireInflate* z, uint8_t* out, size_t* produced, uint8_t b) {
    out[(*produced)++] = b;
    z->win[z->wpos] = b;
    z->wpos = (z->wpos + 1) & FIRE_CMP_WMASK;
    if (z->whave < FIRE_CMP_WSIZE) z->whave++;
}

/* Adds the output since the last call of this to the check of the framing. */
static void fire_inf_sum(FireInflate* z, const uint8_t* out, size_t produced) {
    size_t n = produced - z->checkFrom;
    if (n == 0) return;
    if (z->format == FIRE_CMP_GZIP) z->check = fire_cmp_crc(z->check, out + z->checkFrom, n);
    else if (z->format == FIRE_CMP_ZLIB) z->check = fire_cmp_adler(z->check, out + z->checkFrom, n);
    z->total += n;
    z->checkFrom = produced;
}

/* Decompresses into `out` (room for `cap` bytes): the number of bytes (0: more input is needed, or the end - see `state`), -1 broken (`error`). */
static long fire_inf_run(FireInflate* z, uint8_t* out, size_t cap) {
    size_t produced = 0;
    int r;
    z->checkFrom = 0;
    while (produced < cap) {
        switch (z->state) {
            case FIRE_INF_HEADER:
                if (z->format == FIRE_CMP_ZLIB) {
                    uint32_t cmf, flg;
                    if (fire_inf_avail(z) < 2) goto needInput;
                    cmf = z->in[z->inPos];
                    flg = z->in[z->inPos + 1];
                    if ((cmf & 15) != 8 || (cmf >> 4) > 7) return fire_inf_fail(z, "This is not zlib data (unknown compression method or window).");
                    if (((cmf << 8) | flg) % 31 != 0) return fire_inf_fail(z, "This is not zlib data (the header check is wrong).");
                    if (flg & 32) return fire_inf_fail(z, "The zlib data needs a preset dictionary, which is not supported.");
                    z->inPos += 2;
                } else if (z->format == FIRE_CMP_GZIP) {
                    r = fire_inf_gzipHeader(z);
                    if (r < 0) return -1;
                    if (r == 0) goto needInput;
                }
                z->state = FIRE_INF_BLOCK;
                break;
            case FIRE_INF_BLOCK: {
                uint32_t type;
                uint64_t savedBits = z->bits;
                int savedN = z->nbits;
                size_t savedPos = z->inPos;
                if (!fire_inf_need(z, 3)) goto needInput;
                z->final = (int)fire_inf_take(z, 1);
                type = fire_inf_take(z, 2);
                if (type == 0) {
                    const uint8_t* p;
                    fire_inf_align(z);
                    if (fire_inf_avail(z) < 4) { z->inPos = savedPos; z->bits = savedBits; z->nbits = savedN; goto needInput; }
                    p = z->in + z->inPos;
                    if ((p[0] | (p[1] << 8)) != ((~(p[2] | (p[3] << 8))) & 0xFFFF)) return fire_inf_fail(z, "The compressed data is broken (the length of a stored block).");
                    z->storedLeft = (uint32_t)(p[0] | (p[1] << 8));
                    z->inPos += 4;
                    z->state = FIRE_INF_STORED;
                } else if (type == 1) {
                    fire_inf_fixed(z);
                    z->state = FIRE_INF_CODES;
                } else if (type == 2) {
                    r = fire_inf_dynamic(z);
                    if (r < 0) return -1;
                    if (r == 0) { z->inPos = savedPos; z->bits = savedBits; z->nbits = savedN; goto needInput; }
                    z->state = FIRE_INF_CODES;
                } else return fire_inf_fail(z, "The compressed data is broken (an unknown block type).");
                break;
            }
            case FIRE_INF_STORED: {
                size_t n = z->storedLeft, i;
                if (n == 0) { z->state = z->final ? FIRE_INF_TRAILER : FIRE_INF_BLOCK; break; }
                if (n > fire_inf_avail(z)) n = fire_inf_avail(z);
                if (n > cap - produced) n = cap - produced;
                if (n == 0) goto needInput;
                for (i = 0; i < n; i++) fire_inf_put(z, out, &produced, z->in[z->inPos + i]);
                z->inPos += n;
                z->storedLeft -= (uint32_t)n;
                break;
            }
            case FIRE_INF_CODES: {
                int sym;
                uint64_t savedBits;
                int savedN;
                size_t savedPos;
                if (z->copyLen > 0) {
                    while (z->copyLen > 0 && produced < cap) {
                        fire_inf_put(z, out, &produced, z->win[(z->wpos - z->copyDist) & FIRE_CMP_WMASK]);
                        z->copyLen--;
                    }
                    break;
                }
                savedBits = z->bits;
                savedN = z->nbits;
                savedPos = z->inPos;
                r = fire_inf_decode(z, &z->lit, &sym);
                if (r < 0) return fire_inf_fail(z, "The compressed data is broken (an invalid code).");
                if (r == 0) goto needInput;
                if (sym < 256) { fire_inf_put(z, out, &produced, (uint8_t)sym); break; }
                if (sym == 256) { z->state = z->final ? FIRE_INF_TRAILER : FIRE_INF_BLOCK; break; }
                sym -= 257;
                if (sym >= 29) return fire_inf_fail(z, "The compressed data is broken (an invalid length code).");
                {
                    uint32_t len, distance;
                    int dsym;
                    if (!fire_inf_need(z, fire_cmp_lenExtra[sym])) { z->inPos = savedPos; z->bits = savedBits; z->nbits = savedN; goto needInput; }
                    len = fire_cmp_lenBase[sym] + fire_inf_take(z, fire_cmp_lenExtra[sym]);
                    r = fire_inf_decode(z, &z->dist, &dsym);
                    if (r < 0) return fire_inf_fail(z, "The compressed data is broken (an invalid distance code).");
                    if (r == 0) { z->inPos = savedPos; z->bits = savedBits; z->nbits = savedN; goto needInput; }
                    if (dsym >= 30) return fire_inf_fail(z, "The compressed data is broken (an invalid distance code).");
                    if (!fire_inf_need(z, fire_cmp_distExtra[dsym])) { z->inPos = savedPos; z->bits = savedBits; z->nbits = savedN; goto needInput; }
                    distance = fire_cmp_distBase[dsym] + fire_inf_take(z, fire_cmp_distExtra[dsym]);
                    if (distance > z->whave) return fire_inf_fail(z, "The compressed data is broken (a distance too far back).");
                    z->copyLen = len;
                    z->copyDist = distance;
                }
                break;
            }
            case FIRE_INF_TRAILER: {
                const uint8_t* p;
                fire_inf_sum(z, out, produced);
                fire_inf_align(z);
                if (z->format == FIRE_CMP_ZLIB) {
                    uint32_t want;
                    if (fire_inf_avail(z) < 4) goto needInput;
                    p = z->in + z->inPos;
                    want = ((uint32_t)p[0] << 24) | ((uint32_t)p[1] << 16) | ((uint32_t)p[2] << 8) | p[3];
                    if (want != z->check) return fire_inf_fail(z, "The zlib data is broken (the Adler-32 check does not match).");
                    z->inPos += 4;
                    z->state = FIRE_INF_DONE;
                } else if (z->format == FIRE_CMP_GZIP) {
                    uint32_t crc, size;
                    if (fire_inf_avail(z) < 8) goto needInput;
                    p = z->in + z->inPos;
                    crc = (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
                    size = (uint32_t)p[4] | ((uint32_t)p[5] << 8) | ((uint32_t)p[6] << 16) | ((uint32_t)p[7] << 24);
                    if (crc != z->check) return fire_inf_fail(z, "The gzip data is broken (the CRC-32 check does not match).");
                    if (size != (uint32_t)z->total) return fire_inf_fail(z, "The gzip data is broken (the length does not match).");
                    z->inPos += 8;
                    z->members++;
                    z->state = FIRE_INF_NEXT_MEMBER;
                } else z->state = FIRE_INF_DONE;
                break;
            }
            case FIRE_INF_NEXT_MEMBER:
                /* gzip: another member may follow (like gunzip and .NET); anything else after a member ends the data */
                if (fire_inf_avail(z) < 2) {
                    if (z->ended) { z->state = FIRE_INF_DONE; break; }
                    goto needInput;
                }
                if (z->in[z->inPos] == 0x1F && z->in[z->inPos + 1] == 0x8B) {
                    z->state = FIRE_INF_HEADER;
                    z->check = 0;
                    z->total = 0;
                    z->wpos = 0;
                    z->whave = 0;
                } else z->state = FIRE_INF_DONE;
                break;
            case FIRE_INF_DONE:
                goto done;
            default:
                return -1;
        }
    }
    goto done;
needInput:
    if (z->ended && !z->sawInput && z->state == FIRE_INF_HEADER) { z->state = FIRE_INF_DONE; return 0; }
    if (z->ended) {
        fire_inf_sum(z, out, produced);
        if (produced > 0) return (long)produced;   /* (the error comes with the next call, which has nothing to give) */
        return fire_inf_fail(z, "The compressed data ends too early.");
    }
done:
    fire_inf_sum(z, out, produced);
    return (long)produced;
}

/* ======================================================================================================================================================
 * Deflate
 * ====================================================================================================================================================== */
#define FIRE_DEF_MIN_MATCH 3
#define FIRE_DEF_MAX_MATCH 258
#define FIRE_DEF_MIN_LOOKAHEAD (FIRE_DEF_MAX_MATCH + FIRE_DEF_MIN_MATCH + 1)
/* The window of the compressor: 32 KB (15 bits) like zlib, the state then takes about 240 KB in one block. A small board (ESP-IDF) takes 4 KB (12 bits, about 40 KB):
   the output stays valid Deflate (it only uses shorter distances), only a little less small. FIRE_DEF_WBITS (9..15) chooses it. */
#ifndef FIRE_DEF_WBITS
#if defined(ESP_PLATFORM)
#define FIRE_DEF_WBITS 12
#else
#define FIRE_DEF_WBITS 15
#endif
#endif
#define FIRE_DEF_WSIZE (1u << FIRE_DEF_WBITS)
#define FIRE_DEF_WMASK (FIRE_DEF_WSIZE - 1)
#define FIRE_DEF_MAX_DIST (FIRE_DEF_WSIZE - FIRE_DEF_MIN_LOOKAHEAD)
#define FIRE_DEF_HASH_BITS FIRE_DEF_WBITS
#define FIRE_DEF_HASH_SIZE (1u << FIRE_DEF_HASH_BITS)
#define FIRE_DEF_HASH_MASK (FIRE_DEF_HASH_SIZE - 1)
#define FIRE_DEF_HASH_SHIFT ((FIRE_DEF_HASH_BITS + FIRE_DEF_MIN_MATCH - 1) / FIRE_DEF_MIN_MATCH)
#define FIRE_DEF_SYMS (FIRE_DEF_WBITS >= 14 ? 16384u : 1u << (FIRE_DEF_WBITS))
#define FIRE_DEF_TOO_FAR 4096

typedef struct FireDeflateConfig { uint16_t good, lazy, nice, chain; } FireDeflateConfig;
/* (the same trade-offs per level as zlib) */
static const FireDeflateConfig fire_def_config[10] = {
    { 0, 0, 0, 0 }, { 4, 4, 8, 4 }, { 4, 5, 16, 8 }, { 4, 6, 32, 32 }, { 4, 4, 16, 16 }, { 8, 16, 32, 32 }, { 8, 16, 128, 128 }, { 8, 32, 128, 256 },
    { 32, 128, 258, 1024 }, { 32, 258, 258, 4096 } };

/* the work arrays of a Huffman code (fire_def_lengths; about 10 KB - too much for the stack of a small board, so they live in the compressor) */
typedef struct FireDefTree { uint32_t freq[320], weight[640]; int parent[640], order[320], depth[640]; } FireDefTree;

typedef struct FireDeflate {
    int format, level;
    uint8_t win[2 * FIRE_DEF_WSIZE + FIRE_DEF_MAX_MATCH + 8];
    uint16_t head[FIRE_DEF_HASH_SIZE];   /* 0: none (position 0 is never a match source - as in zlib) */
    uint16_t prev[FIRE_DEF_WSIZE];
    uint32_t insH, strstart, lookahead, matchStart, matchLength, prevLength, prevMatch;
    long blockStart;
    int matchAvailable;
    /* the symbols of the current block: a literal (dist 0) or a match (length - 3, distance) */
    uint8_t symLen[FIRE_DEF_SYMS];
    uint16_t symDist[FIRE_DEF_SYMS];
    uint32_t nsym;
    uint32_t freqL[286], freqD[30];
    FireDefTree tree;
    /* the output that was made and not taken yet */
    uint8_t* out;
    size_t outLen, outPos, outCap;
    uint64_t bitbuf;
    int bitcnt;
    int outOfMemory;
    uint32_t check;
    uint64_t total;
    int headerDone, finished, hashPrimed;
} FireDeflate;

static FireDeflate* fire_def_new(int format, int level) {
    FireDeflate* z = (FireDeflate*)calloc(1, sizeof(FireDeflate));
    if (z == NULL) return NULL;
    z->format = format;
    z->level = level < 0 ? 6 : level > 9 ? 9 : level;
    z->check = format == FIRE_CMP_ZLIB ? 1u : 0u;
    z->matchLength = z->prevLength = FIRE_DEF_MIN_MATCH - 1;
    return z;
}
static void fire_def_free(FireDeflate* z) {
    if (z == NULL) return;
    free(z->out);
    free(z);
}

static void fire_def_byte(FireDeflate* z, uint8_t b) {
    if (z->outLen == z->outCap) {
        size_t cap;
        uint8_t* grown;
        if (z->outPos > 0) {   /* the part that was taken can go */
            memmove(z->out, z->out + z->outPos, z->outLen - z->outPos);
            z->outLen -= z->outPos;
            z->outPos = 0;
        }
        if (z->outLen == z->outCap) {
            cap = z->outCap ? z->outCap * 2 : 16384;
            grown = (uint8_t*)realloc(z->out, cap);
            if (grown == NULL) { z->outOfMemory = 1; return; }
            z->out = grown;
            z->outCap = cap;
        }
    }
    z->out[z->outLen++] = b;
}
static void fire_def_bits(FireDeflate* z, uint32_t value, int n) {
    z->bitbuf |= (uint64_t)value << z->bitcnt;
    z->bitcnt += n;
    while (z->bitcnt >= 8) {
        fire_def_byte(z, (uint8_t)z->bitbuf);
        z->bitbuf >>= 8;
        z->bitcnt -= 8;
    }
}
static void fire_def_alignOut(FireDeflate* z) { if (z->bitcnt > 0) fire_def_bits(z, 0, 8 - z->bitcnt); }

static void fire_def_header(FireDeflate* z) {
    if (z->headerDone) return;
    z->headerDone = 1;
    if (z->format == FIRE_CMP_ZLIB) {
        int flevel = z->level <= 1 ? 0 : z->level <= 5 ? 1 : z->level == 6 ? 2 : 3;
        uint32_t h = (0x78u << 8) | ((uint32_t)flevel << 6);
        h += 31 - h % 31;
        fire_def_byte(z, (uint8_t)(h >> 8));
        fire_def_byte(z, (uint8_t)h);
    } else if (z->format == FIRE_CMP_GZIP) {
        static const uint8_t gz[10] = { 0x1F, 0x8B, 8, 0, 0, 0, 0, 0, 0, 255 };
        int i;
        for (i = 0; i < 10; i++) fire_def_byte(z, i == 8 ? (uint8_t)(z->level >= 9 ? 2 : z->level <= 1 ? 4 : 0) : gz[i]);
    }
}

/* ---- Huffman codes of limited length ---- */
/* The lengths of a Huffman code for `freq` (n symbols, no length above maxBits). At least two symbols get a code when any has a frequency (a single code
 * would be incomplete). */
static void fire_def_lengths(FireDefTree* t, const uint32_t* freqIn, int n, int maxBits, uint8_t* lengths) {
    uint32_t* freq = t->freq;
    uint32_t* weight = t->weight;
    int* parent = t->parent;
    int* order = t->order;
    int blCount[32];
    int nodes, leaves = 0, i, j, used = 0;
    memset(lengths, 0, (size_t)n);
    for (i = 0; i < n; i++) { freq[i] = freqIn[i]; if (freq[i]) used++; }
    if (used == 0) return;
    if (used == 1) { for (i = 0; i < n && used < 2; i++) if (freq[i] == 0) { freq[i] = 1; used++; } }
    /* the leaves, by frequency (then by symbol, so that the result is fixed) */
    for (i = 0; i < n; i++) if (freq[i]) order[leaves++] = i;
    for (i = 1; i < leaves; i++) {
        int s = order[i];
        j = i - 1;
        while (j >= 0 && (freq[order[j]] > freq[s] || (freq[order[j]] == freq[s] && order[j] > s))) { order[j + 1] = order[j]; j--; }
        order[j + 1] = s;
    }
    /* the tree (two queues: the sorted leaves and the inner nodes, which come in rising weight) */
    for (i = 0; i < leaves; i++) { weight[i] = freq[order[i]]; parent[i] = -1; }
    nodes = leaves;
    {
        int li = 0, ni = leaves;
        while (nodes - leaves < leaves - 1) {
            int pick[2], k;
            for (k = 0; k < 2; k++) {
                if (li < leaves && (ni >= nodes || weight[li] <= weight[ni])) pick[k] = li++;
                else pick[k] = ni++;
            }
            weight[nodes] = weight[pick[0]] + weight[pick[1]];
            parent[nodes] = -1;
            parent[pick[0]] = nodes;
            parent[pick[1]] = nodes;
            nodes++;
        }
    }
    /* the depth of each leaf; the counts per length, with the lengths above maxBits brought down (the method of zlib) */
    memset(blCount, 0, sizeof blCount);
    {
        int* depth = t->depth;
        int overflow = 0;
        depth[nodes - 1] = 0;
        for (i = nodes - 2; i >= 0; i--) depth[i] = depth[parent[i]] + 1;
        for (i = 0; i < leaves; i++) {
            int d = depth[i];
            if (d > maxBits) { d = maxBits; overflow++; }
            blCount[d]++;
        }
        while (overflow > 0) {
            int bits = maxBits - 1;
            while (blCount[bits] == 0) bits--;
            blCount[bits]--;
            blCount[bits + 1] += 2;
            blCount[maxBits]--;
            overflow -= 2;
        }
    }
    /* the longest codes to the rarest symbols */
    {
        int len = maxBits;
        for (i = 0; i < leaves; i++) {
            while (len > 0 && blCount[len] == 0) len--;
            lengths[order[i]] = (uint8_t)len;
            blCount[len]--;
        }
    }
}

/* Canonical codes for the lengths, bit-reversed for writing. */
static void fire_def_codes(const uint8_t* lengths, int n, uint16_t* codes) {
    uint16_t blCount[16], next[16];
    int i, len;
    uint32_t code = 0;
    memset(blCount, 0, sizeof blCount);
    for (i = 0; i < n; i++) blCount[lengths[i]]++;
    blCount[0] = 0;
    for (len = 1; len < 16; len++) { code = (code + blCount[len - 1]) << 1; next[len] = (uint16_t)code; }
    for (i = 0; i < n; i++) {
        int l = lengths[i], b;
        uint32_t c, rev = 0;
        if (l == 0) { codes[i] = 0; continue; }
        c = next[l]++;
        for (b = 0; b < l; b++) rev |= ((c >> b) & 1u) << (l - 1 - b);
        codes[i] = (uint16_t)rev;
    }
}

static int fire_def_lenCode(uint32_t len) {   /* len: 3..258 */
    int c = 0;
    while (c < 28 && fire_cmp_lenBase[c + 1] <= len) c++;
    return c;
}
static int fire_def_distCode(uint32_t dist) {
    int c = 0;
    while (c < 29 && fire_cmp_distBase[c + 1] <= dist) c++;
    return c;
}

/* The run-length encoding of the code lengths (symbols 0..18 with their extra bits). */
static int fire_def_rle(const uint8_t* lengths, int n, uint8_t* syms, uint8_t* extra) {
    int i = 0, count = 0;
    while (i < n) {
        int len = lengths[i], run = 1;
        while (i + run < n && lengths[i + run] == len) run++;
        if (len == 0 && run >= 3) {
            int take = run > 138 ? 138 : run;
            if (take >= 11) { syms[count] = 18; extra[count++] = (uint8_t)(take - 11); }
            else { syms[count] = 17; extra[count++] = (uint8_t)(take - 3); }
            i += take;
        } else if (len != 0 && run >= 4) {
            syms[count] = (uint8_t)len; extra[count++] = 0;
            i++;
            run--;
            while (run >= 3) {
                int take = run > 6 ? 6 : run;
                syms[count] = 16; extra[count++] = (uint8_t)(take - 3);
                i += take;
                run -= take;
            }
        } else {
            syms[count] = (uint8_t)len; extra[count++] = 0;
            i++;
        }
    }
    return count;
}

/* Writes the block of the collected symbols (and the bytes from blockStart to strstart) in the cheapest of the three encodings. */
static void fire_def_block(FireDeflate* z, int last) {
    uint8_t litLen[286], distLen[30], clLen[19], fixedLit[288], fixedDist[30];
    uint16_t litCode[286], distCode[30], clCode[19];
    uint8_t rleSym[320], rleExtra[320];
    uint32_t clFreq[19];
    uint64_t dynamicBits, fixedBits, storedBits = 0;
    long storedLen = z->blockStart >= 0 ? (long)z->strstart - z->blockStart : -1;
    int nlen, ndist, ncl, nrle, i, useFixed;
    uint8_t all[316];
    z->freqL[256]++;
    fire_def_lengths(&z->tree, z->freqL, 286, 15, litLen);
    fire_def_lengths(&z->tree, z->freqD, 30, 15, distLen);
    for (nlen = 286; nlen > 257 && litLen[nlen - 1] == 0; nlen--) { }
    for (ndist = 30; ndist > 1 && distLen[ndist - 1] == 0; ndist--) { }
    memcpy(all, litLen, (size_t)nlen);
    memcpy(all + nlen, distLen, (size_t)ndist);
    nrle = fire_def_rle(all, nlen + ndist, rleSym, rleExtra);
    memset(clFreq, 0, sizeof clFreq);
    for (i = 0; i < nrle; i++) clFreq[rleSym[i]]++;
    fire_def_lengths(&z->tree, clFreq, 19, 7, clLen);
    for (ncl = 19; ncl > 4 && clLen[fire_cmp_clOrder[ncl - 1]] == 0; ncl--) { }
    /* the costs */
    for (i = 0; i < 288; i++) fixedLit[i] = (uint8_t)(i < 144 ? 8 : i < 256 ? 9 : i < 280 ? 7 : 8);
    for (i = 0; i < 30; i++) fixedDist[i] = 5;
    dynamicBits = 3 + 5 + 5 + 4 + 3 * (uint64_t)ncl;
    for (i = 0; i < nrle; i++) dynamicBits += clLen[rleSym[i]] + (rleSym[i] == 16 ? 2 : rleSym[i] == 17 ? 3 : rleSym[i] == 18 ? 7 : 0);
    fixedBits = 3;
    for (i = 0; i < 286; i++) {
        uint32_t extraBits = i >= 257 ? fire_cmp_lenExtra[i - 257] : 0;
        dynamicBits += (uint64_t)z->freqL[i] * (litLen[i] + extraBits);
        fixedBits += (uint64_t)z->freqL[i] * (fixedLit[i] + extraBits);
    }
    for (i = 0; i < 30; i++) {
        dynamicBits += (uint64_t)z->freqD[i] * (distLen[i] + fire_cmp_distExtra[i]);
        fixedBits += (uint64_t)z->freqD[i] * (fixedDist[i] + fire_cmp_distExtra[i]);
    }
    if (storedLen >= 0) {
        long blocks = storedLen / 65535 + 1;
        storedBits = (uint64_t)blocks * (3 + 7 + 32) + 8 * (uint64_t)storedLen;
    }
    if (storedLen >= 0 && (z->level == 0 || (storedBits <= dynamicBits && storedBits <= fixedBits))) {
        const uint8_t* p = z->win + z->blockStart;
        long left = storedLen;
        do {
            long n = left > 65535 ? 65535 : left;
            long k;
            fire_def_bits(z, (left - n == 0 && last) ? 1u : 0u, 1);
            fire_def_bits(z, 0, 2);
            fire_def_alignOut(z);
            fire_def_byte(z, (uint8_t)n); fire_def_byte(z, (uint8_t)(n >> 8));
            fire_def_byte(z, (uint8_t)~n); fire_def_byte(z, (uint8_t)(~n >> 8));
            for (k = 0; k < n; k++) fire_def_byte(z, p[k]);
            p += n;
            left -= n;
        } while (left > 0);
    } else {
        const uint8_t* lens;
        const uint8_t* dlens;
        uint16_t fixedLitCode[288], fixedDistCode[30];
        const uint16_t* lcodes;
        const uint16_t* dcodes;
        uint32_t s;
        useFixed = fixedBits <= dynamicBits;
        fire_def_bits(z, last ? 1u : 0u, 1);
        if (useFixed) {
            fire_def_bits(z, 1, 2);
            fire_def_codes(fixedLit, 288, fixedLitCode);
            fire_def_codes(fixedDist, 30, fixedDistCode);
            lens = fixedLit; dlens = fixedDist; lcodes = fixedLitCode; dcodes = fixedDistCode;
        } else {
            fire_def_bits(z, 2, 2);
            fire_def_codes(litLen, 286, litCode);
            fire_def_codes(distLen, 30, distCode);
            fire_def_codes(clLen, 19, clCode);
            fire_def_bits(z, (uint32_t)(nlen - 257), 5);
            fire_def_bits(z, (uint32_t)(ndist - 1), 5);
            fire_def_bits(z, (uint32_t)(ncl - 4), 4);
            for (i = 0; i < ncl; i++) fire_def_bits(z, clLen[fire_cmp_clOrder[i]], 3);
            for (i = 0; i < nrle; i++) {
                fire_def_bits(z, clCode[rleSym[i]], clLen[rleSym[i]]);
                if (rleSym[i] == 16) fire_def_bits(z, rleExtra[i], 2);
                else if (rleSym[i] == 17) fire_def_bits(z, rleExtra[i], 3);
                else if (rleSym[i] == 18) fire_def_bits(z, rleExtra[i], 7);
            }
            lens = litLen; dlens = distLen; lcodes = litCode; dcodes = distCode;
        }
        for (s = 0; s < z->nsym; s++) {
            if (z->symDist[s] == 0) {
                int lit = z->symLen[s];
                fire_def_bits(z, lcodes[lit], lens[lit]);
            } else {
                uint32_t len = (uint32_t)z->symLen[s] + FIRE_DEF_MIN_MATCH, dist = z->symDist[s];
                int lc = fire_def_lenCode(len), dc = fire_def_distCode(dist);
                fire_def_bits(z, lcodes[257 + lc], lens[257 + lc]);
                if (fire_cmp_lenExtra[lc]) fire_def_bits(z, len - fire_cmp_lenBase[lc], fire_cmp_lenExtra[lc]);
                fire_def_bits(z, dcodes[dc], dlens[dc]);
                if (fire_cmp_distExtra[dc]) fire_def_bits(z, dist - fire_cmp_distBase[dc], fire_cmp_distExtra[dc]);
            }
        }
        fire_def_bits(z, lcodes[256], lens[256]);
    }
    z->nsym = 0;
    memset(z->freqL, 0, sizeof z->freqL);
    memset(z->freqD, 0, sizeof z->freqD);
    z->blockStart = (long)z->strstart;
}

static int fire_def_tallyLit(FireDeflate* z, uint8_t c) {
    z->symLen[z->nsym] = c;
    z->symDist[z->nsym++] = 0;
    z->freqL[c]++;
    return z->nsym == FIRE_DEF_SYMS - 1;
}
static int fire_def_tallyMatch(FireDeflate* z, uint32_t dist, uint32_t len) {
    z->symLen[z->nsym] = (uint8_t)(len - FIRE_DEF_MIN_MATCH);
    z->symDist[z->nsym++] = (uint16_t)dist;
    z->freqL[257 + fire_def_lenCode(len)]++;
    z->freqD[fire_def_distCode(dist)]++;
    return z->nsym == FIRE_DEF_SYMS - 1;
}

static void fire_def_insert(FireDeflate* z, uint32_t pos, uint32_t* head) {
    z->insH = ((z->insH << FIRE_DEF_HASH_SHIFT) ^ z->win[pos + FIRE_DEF_MIN_MATCH - 1]) & FIRE_DEF_HASH_MASK;
    *head = z->head[z->insH];
    z->prev[pos & FIRE_DEF_WMASK] = (uint16_t)*head;
    z->head[z->insH] = (uint16_t)pos;
}

static uint32_t fire_def_longest(FireDeflate* z, uint32_t cur) {
    const FireDeflateConfig* cfg = &fire_def_config[z->level];
    uint32_t chain = cfg->chain, best = z->prevLength, nice = cfg->nice;
    uint32_t limit = z->strstart > FIRE_DEF_MAX_DIST ? z->strstart - FIRE_DEF_MAX_DIST : 0;
    const uint8_t* scan = z->win + z->strstart;
    if (z->prevLength >= cfg->good) chain >>= 2;
    if (nice > z->lookahead) nice = z->lookahead;
    do {
        const uint8_t* match = z->win + cur;
        uint32_t len;
        if (match[best] != scan[best] || match[best - 1] != scan[best - 1] || match[0] != scan[0] || match[1] != scan[1]) continue;
        len = 2;
        while (len < FIRE_DEF_MAX_MATCH && match[len] == scan[len]) len++;
        if (len > best) {
            z->matchStart = cur;
            best = len;
            if (len >= nice) break;
        }
    } while ((cur = z->prev[cur & FIRE_DEF_WMASK]) > limit && --chain != 0);
    return best <= z->lookahead ? best : z->lookahead;
}

/* Compresses what is in the window: without `flush` only as far as a full lookahead is there. */
static void fire_def_compress(FireDeflate* z, int flush) {
    const FireDeflateConfig* cfg = &fire_def_config[z->level];
    uint32_t hashHead = 0;
    if (z->level == 0) {
        /* stored: everything up to the end of the window goes into blocks of the stored kind (fire_def_block chooses it at level 0) */
        if (flush || z->lookahead >= FIRE_DEF_MIN_LOOKAHEAD) {
            uint32_t n = flush ? z->lookahead : z->lookahead - FIRE_DEF_MIN_LOOKAHEAD;
            z->strstart += n;
            z->lookahead -= n;
        }
        /* (the bytes of a block must still be in the window when it is written: a block before the window slides) */
        if ((long)z->strstart - z->blockStart >= (long)FIRE_DEF_WSIZE) fire_def_block(z, 0);
        return;
    }
    for (;;) {
        if (z->lookahead < FIRE_DEF_MIN_LOOKAHEAD) {
            if (!flush || z->lookahead == 0) break;
        }
        hashHead = 0;
        if (!z->hashPrimed && z->lookahead >= FIRE_DEF_MIN_MATCH) {
            /* the rolling hash starts with the first two bytes (it covers three: the third comes with the insertion) */
            z->insH = ((uint32_t)z->win[z->strstart] << FIRE_DEF_HASH_SHIFT ^ z->win[z->strstart + 1]) & FIRE_DEF_HASH_MASK;
            z->hashPrimed = 1;
        }
        if (z->lookahead >= FIRE_DEF_MIN_MATCH) fire_def_insert(z, z->strstart, &hashHead);
        if (z->level <= 3) {
            /* greedy */
            z->matchLength = FIRE_DEF_MIN_MATCH - 1;
            z->prevLength = FIRE_DEF_MIN_MATCH - 1;
            if (hashHead != 0 && z->strstart - hashHead <= FIRE_DEF_MAX_DIST) z->matchLength = fire_def_longest(z, hashHead);
            if (z->matchLength >= FIRE_DEF_MIN_MATCH) {
                int full = fire_def_tallyMatch(z, z->strstart - z->matchStart, z->matchLength);
                z->lookahead -= z->matchLength;
                if (z->matchLength <= cfg->lazy && z->lookahead >= FIRE_DEF_MIN_MATCH) {
                    z->matchLength--;
                    do { z->strstart++; fire_def_insert(z, z->strstart, &hashHead); } while (--z->matchLength != 0);
                    z->strstart++;
                } else {
                    z->strstart += z->matchLength;
                    z->matchLength = 0;
                    z->insH = z->win[z->strstart];
                    z->insH = ((z->insH << FIRE_DEF_HASH_SHIFT) ^ z->win[z->strstart + 1]) & FIRE_DEF_HASH_MASK;
                }
                if (full) fire_def_block(z, 0);
            } else {
                int full = fire_def_tallyLit(z, z->win[z->strstart]);
                z->lookahead--;
                z->strstart++;
                if (full) fire_def_block(z, 0);
            }
            continue;
        }
        /* lazy: a match is only taken when the next position has no longer one */
        z->prevLength = z->matchLength;
        z->prevMatch = z->matchStart;
        z->matchLength = FIRE_DEF_MIN_MATCH - 1;
        if (hashHead != 0 && z->prevLength < cfg->lazy && z->strstart - hashHead <= FIRE_DEF_MAX_DIST) {
            z->matchLength = fire_def_longest(z, hashHead);
            if (z->matchLength <= 5 && z->matchLength == FIRE_DEF_MIN_MATCH && z->strstart - z->matchStart > FIRE_DEF_TOO_FAR) z->matchLength = FIRE_DEF_MIN_MATCH - 1;
        }
        if (z->prevLength >= FIRE_DEF_MIN_MATCH && z->matchLength <= z->prevLength) {
            uint32_t maxInsert = z->strstart + z->lookahead - FIRE_DEF_MIN_MATCH;
            int full = fire_def_tallyMatch(z, z->strstart - 1 - z->prevMatch, z->prevLength);
            z->lookahead -= z->prevLength - 1;
            z->prevLength -= 2;
            do { if (++z->strstart <= maxInsert) fire_def_insert(z, z->strstart, &hashHead); } while (--z->prevLength != 0);
            z->matchAvailable = 0;
            z->matchLength = FIRE_DEF_MIN_MATCH - 1;
            z->strstart++;
            if (full) fire_def_block(z, 0);
        } else if (z->matchAvailable) {
            if (fire_def_tallyLit(z, z->win[z->strstart - 1])) fire_def_block(z, 0);
            z->strstart++;
            z->lookahead--;
        } else {
            z->matchAvailable = 1;
            z->strstart++;
            z->lookahead--;
        }
    }
    if (flush) {
        if (z->matchAvailable) fire_def_tallyLit(z, z->win[z->strstart - 1]);
        z->matchAvailable = 0;
        z->matchLength = FIRE_DEF_MIN_MATCH - 1;   /* (a match found before the flush must not be taken after it) */
    }
}

/* Moves the upper half of the window down when the position has gone far enough (the hash chains move with it). */
static void fire_def_slide(FireDeflate* z) {
    uint32_t i;
    if (z->strstart < FIRE_DEF_WSIZE + FIRE_DEF_MAX_DIST) return;
    memmove(z->win, z->win + FIRE_DEF_WSIZE, FIRE_DEF_WSIZE + FIRE_DEF_MAX_MATCH);
    z->matchStart = z->matchStart >= FIRE_DEF_WSIZE ? z->matchStart - FIRE_DEF_WSIZE : 0;
    z->prevMatch = z->prevMatch >= FIRE_DEF_WSIZE ? z->prevMatch - FIRE_DEF_WSIZE : 0;
    z->strstart -= FIRE_DEF_WSIZE;
    z->blockStart -= (long)FIRE_DEF_WSIZE;
    for (i = 0; i < FIRE_DEF_HASH_SIZE; i++) z->head[i] = (uint16_t)(z->head[i] >= FIRE_DEF_WSIZE ? z->head[i] - FIRE_DEF_WSIZE : 0);
    for (i = 0; i < FIRE_DEF_WSIZE; i++) z->prev[i] = (uint16_t)(z->prev[i] >= FIRE_DEF_WSIZE ? z->prev[i] - FIRE_DEF_WSIZE : 0);
}

/* Takes data to compress (all of it; the compressed bytes wait in `out`). */
static void fire_def_write(FireDeflate* z, const uint8_t* p, size_t n) {
    fire_def_header(z);
    if (z->format == FIRE_CMP_GZIP) z->check = fire_cmp_crc(z->check, p, n);
    else if (z->format == FIRE_CMP_ZLIB) z->check = fire_cmp_adler(z->check, p, n);
    z->total += n;
    while (n > 0) {
        uint32_t room, take;
        fire_def_slide(z);
        room = 2 * FIRE_DEF_WSIZE - (z->strstart + z->lookahead);
        if (room == 0) {
            fire_def_compress(z, 0);
            fire_def_slide(z);
            room = 2 * FIRE_DEF_WSIZE - (z->strstart + z->lookahead);
            if (room == 0) { fire_def_block(z, 0); continue; }   /* (cannot happen: the compressor leaves less than a lookahead) */
        }
        take = n < room ? (uint32_t)n : room;
        memcpy(z->win + z->strstart + z->lookahead, p, take);
        z->lookahead += take;
        p += take;
        n -= take;
        if (z->lookahead >= FIRE_DEF_MIN_LOOKAHEAD) fire_def_compress(z, 0);
    }
}

/* Everything so far goes out: `final` ends the stream (the last block and the trailer of the framing), otherwise a sync flush (an empty stored block, so
 * that a reader can decompress everything written up to here). */
static void fire_def_flush(FireDeflate* z, int final) {
    if (z->finished) return;
    fire_def_header(z);
    fire_def_slide(z);
    fire_def_compress(z, 1);
    if (final) {
        fire_def_block(z, 1);
        fire_def_alignOut(z);
        if (z->format == FIRE_CMP_ZLIB) {
            fire_def_byte(z, (uint8_t)(z->check >> 24)); fire_def_byte(z, (uint8_t)(z->check >> 16));
            fire_def_byte(z, (uint8_t)(z->check >> 8)); fire_def_byte(z, (uint8_t)z->check);
        } else if (z->format == FIRE_CMP_GZIP) {
            int i;
            for (i = 0; i < 4; i++) fire_def_byte(z, (uint8_t)(z->check >> (8 * i)));
            for (i = 0; i < 4; i++) fire_def_byte(z, (uint8_t)(z->total >> (8 * i)));
        }
        z->finished = 1;
    } else {
        if (z->nsym > 0 || (z->blockStart >= 0 && (long)z->strstart > z->blockStart)) fire_def_block(z, 0);
        fire_def_bits(z, 0, 3);
        fire_def_alignOut(z);
        fire_def_byte(z, 0); fire_def_byte(z, 0); fire_def_byte(z, 0xFF); fire_def_byte(z, 0xFF);
    }
}

/* Takes up to `cap` compressed bytes. */
static size_t fire_def_read(FireDeflate* z, uint8_t* out, size_t cap) {
    size_t n = z->outLen - z->outPos;
    if (n > cap) n = cap;
    if (n == 0) return 0;
    memcpy(out, z->out + z->outPos, n);
    z->outPos += n;
    if (z->outPos == z->outLen) z->outPos = z->outLen = 0;
    return n;
}

#endif
