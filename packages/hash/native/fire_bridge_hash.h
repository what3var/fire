/* fire native bridge "hash" (C): the natives behind `#import "hash"` (docs/stdlib/hash.md) - MD5, SHA-1, SHA-256, SHA-384, SHA-512, CRC-32 and Adler-32, in plain C99
 * without a library (the same results on every target, the ESP32 included).
 *
 * A hasher keeps its state in a byte buffer that belongs to the fire object (`__HashInit` makes it, `__HashUpdate` changes it in place, `__HashFinal` reads the
 * digest from it and starts it anew): no handle table, nothing to free, nothing left behind when a program ends. The buffer starts with a tag and the algorithm;
 * a buffer that is not such a state (or a broken one) is refused with an ArgumentException. */
#ifndef FIRE_BRIDGE_HASH_H
#define FIRE_BRIDGE_HASH_H

#include <stdint.h>
#include <stdio.h>
#include <string.h>

enum { FIRE_HASH_MD5 = 1, FIRE_HASH_SHA1 = 2, FIRE_HASH_SHA256 = 3, FIRE_HASH_SHA384 = 4, FIRE_HASH_SHA512 = 5, FIRE_HASH_CRC32 = 6, FIRE_HASH_ADLER32 = 7 };

/* ---- the algorithms ------------------------------------------------------------------------------------------------------------------------------- */
typedef struct FireHashCtx {
    uint32_t tag;        /* FIRE_HASH_TAG: this buffer is a hasher */
    uint32_t algo;
    uint64_t length;     /* bytes hashed so far (low part for SHA-384/512, whose length is 128 bit: the high part is `lengthHigh`) */
    uint64_t lengthHigh;
    uint32_t used;       /* bytes waiting in `block` */
    uint32_t pad;
    union { uint32_t w[8]; uint64_t d[8]; } h;
    uint8_t block[128];
} FireHashCtx;
#define FIRE_HASH_TAG 0x48534846u   /* "FHSH" */

static uint32_t fire_hash_rol32(uint32_t x, int n) { return (x << n) | (x >> (32 - n)); }
static uint32_t fire_hash_ror32(uint32_t x, int n) { return (x >> n) | (x << (32 - n)); }
static uint64_t fire_hash_ror64(uint64_t x, int n) { return (x >> n) | (x << (64 - n)); }
static uint32_t fire_hash_be32(const uint8_t* p) { return ((uint32_t)p[0] << 24) | ((uint32_t)p[1] << 16) | ((uint32_t)p[2] << 8) | (uint32_t)p[3]; }
static uint32_t fire_hash_le32(const uint8_t* p) { return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24); }
static uint64_t fire_hash_be64(const uint8_t* p) { return ((uint64_t)fire_hash_be32(p) << 32) | fire_hash_be32(p + 4); }

/* MD5 (RFC 1321) */
static const uint32_t fire_hash_md5K[64] = {
    0xd76aa478, 0xe8c7b756, 0x242070db, 0xc1bdceee, 0xf57c0faf, 0x4787c62a, 0xa8304613, 0xfd469501, 0x698098d8, 0x8b44f7af, 0xffff5bb1, 0x895cd7be,
    0x6b901122, 0xfd987193, 0xa679438e, 0x49b40821, 0xf61e2562, 0xc040b340, 0x265e5a51, 0xe9b6c7aa, 0xd62f105d, 0x02441453, 0xd8a1e681, 0xe7d3fbc8,
    0x21e1cde6, 0xc33707d6, 0xf4d50d87, 0x455a14ed, 0xa9e3e905, 0xfcefa3f8, 0x676f02d9, 0x8d2a4c8a, 0xfffa3942, 0x8771f681, 0x6d9d6122, 0xfde5380c,
    0xa4beea44, 0x4bdecfa9, 0xf6bb4b60, 0xbebfbc70, 0x289b7ec6, 0xeaa127fa, 0xd4ef3085, 0x04881d05, 0xd9d4d039, 0xe6db99e5, 0x1fa27cf8, 0xc4ac5665,
    0xf4292244, 0x432aff97, 0xab9423a7, 0xfc93a039, 0x655b59c3, 0x8f0ccc92, 0xffeff47d, 0x85845dd1, 0x6fa87e4f, 0xfe2ce6e0, 0xa3014314, 0x4e0811a1,
    0xf7537e82, 0xbd3af235, 0x2ad7d2bb, 0xeb86d391 };
static const uint8_t fire_hash_md5R[64] = { 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20,
    4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21 };
static void fire_hash_md5Block(uint32_t* h, const uint8_t* p) {
    uint32_t m[16], a = h[0], b = h[1], c = h[2], d = h[3];
    int i;
    for (i = 0; i < 16; i++) m[i] = fire_hash_le32(p + 4 * i);
    for (i = 0; i < 64; i++) {
        uint32_t f, t;
        int g;
        if (i < 16) { f = (b & c) | (~b & d); g = i; }
        else if (i < 32) { f = (d & b) | (~d & c); g = (5 * i + 1) & 15; }
        else if (i < 48) { f = b ^ c ^ d; g = (3 * i + 5) & 15; }
        else { f = c ^ (b | ~d); g = (7 * i) & 15; }
        t = d;
        d = c;
        c = b;
        b = b + fire_hash_rol32(a + f + fire_hash_md5K[i] + m[g], fire_hash_md5R[i]);
        a = t;
    }
    h[0] += a; h[1] += b; h[2] += c; h[3] += d;
}

/* SHA-1 (FIPS 180-4) */
static void fire_hash_sha1Block(uint32_t* h, const uint8_t* p) {
    uint32_t w[80], a = h[0], b = h[1], c = h[2], d = h[3], e = h[4];
    int i;
    for (i = 0; i < 16; i++) w[i] = fire_hash_be32(p + 4 * i);
    for (i = 16; i < 80; i++) w[i] = fire_hash_rol32(w[i - 3] ^ w[i - 8] ^ w[i - 14] ^ w[i - 16], 1);
    for (i = 0; i < 80; i++) {
        uint32_t f, k, t;
        if (i < 20) { f = (b & c) | (~b & d); k = 0x5A827999; }
        else if (i < 40) { f = b ^ c ^ d; k = 0x6ED9EBA1; }
        else if (i < 60) { f = (b & c) | (b & d) | (c & d); k = 0x8F1BBCDC; }
        else { f = b ^ c ^ d; k = 0xCA62C1D6; }
        t = fire_hash_rol32(a, 5) + f + e + k + w[i];
        e = d; d = c; c = fire_hash_rol32(b, 30); b = a; a = t;
    }
    h[0] += a; h[1] += b; h[2] += c; h[3] += d; h[4] += e;
}

/* SHA-256 */
static const uint32_t fire_hash_k256[64] = {
    0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5, 0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3,
    0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174, 0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
    0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967, 0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13,
    0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85, 0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
    0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3, 0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208,
    0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2 };
static void fire_hash_sha256Block(uint32_t* h, const uint8_t* p) {
    uint32_t w[64], s[8];
    int i;
    for (i = 0; i < 16; i++) w[i] = fire_hash_be32(p + 4 * i);
    for (i = 16; i < 64; i++) {
        uint32_t s0 = fire_hash_ror32(w[i - 15], 7) ^ fire_hash_ror32(w[i - 15], 18) ^ (w[i - 15] >> 3);
        uint32_t s1 = fire_hash_ror32(w[i - 2], 17) ^ fire_hash_ror32(w[i - 2], 19) ^ (w[i - 2] >> 10);
        w[i] = w[i - 16] + s0 + w[i - 7] + s1;
    }
    for (i = 0; i < 8; i++) s[i] = h[i];
    for (i = 0; i < 64; i++) {
        uint32_t S1 = fire_hash_ror32(s[4], 6) ^ fire_hash_ror32(s[4], 11) ^ fire_hash_ror32(s[4], 25);
        uint32_t ch = (s[4] & s[5]) ^ (~s[4] & s[6]);
        uint32_t t1 = s[7] + S1 + ch + fire_hash_k256[i] + w[i];
        uint32_t S0 = fire_hash_ror32(s[0], 2) ^ fire_hash_ror32(s[0], 13) ^ fire_hash_ror32(s[0], 22);
        uint32_t mj = (s[0] & s[1]) ^ (s[0] & s[2]) ^ (s[1] & s[2]);
        uint32_t t2 = S0 + mj;
        s[7] = s[6]; s[6] = s[5]; s[5] = s[4]; s[4] = s[3] + t1; s[3] = s[2]; s[2] = s[1]; s[1] = s[0]; s[0] = t1 + t2;
    }
    for (i = 0; i < 8; i++) h[i] += s[i];
}

/* SHA-512 (and SHA-384) */
static const uint64_t fire_hash_k512[80] = {
    0x428a2f98d728ae22ULL, 0x7137449123ef65cdULL, 0xb5c0fbcfec4d3b2fULL, 0xe9b5dba58189dbbcULL, 0x3956c25bf348b538ULL, 0x59f111f1b605d019ULL,
    0x923f82a4af194f9bULL, 0xab1c5ed5da6d8118ULL, 0xd807aa98a3030242ULL, 0x12835b0145706fbeULL, 0x243185be4ee4b28cULL, 0x550c7dc3d5ffb4e2ULL,
    0x72be5d74f27b896fULL, 0x80deb1fe3b1696b1ULL, 0x9bdc06a725c71235ULL, 0xc19bf174cf692694ULL, 0xe49b69c19ef14ad2ULL, 0xefbe4786384f25e3ULL,
    0x0fc19dc68b8cd5b5ULL, 0x240ca1cc77ac9c65ULL, 0x2de92c6f592b0275ULL, 0x4a7484aa6ea6e483ULL, 0x5cb0a9dcbd41fbd4ULL, 0x76f988da831153b5ULL,
    0x983e5152ee66dfabULL, 0xa831c66d2db43210ULL, 0xb00327c898fb213fULL, 0xbf597fc7beef0ee4ULL, 0xc6e00bf33da88fc2ULL, 0xd5a79147930aa725ULL,
    0x06ca6351e003826fULL, 0x142929670a0e6e70ULL, 0x27b70a8546d22ffcULL, 0x2e1b21385c26c926ULL, 0x4d2c6dfc5ac42aedULL, 0x53380d139d95b3dfULL,
    0x650a73548baf63deULL, 0x766a0abb3c77b2a8ULL, 0x81c2c92e47edaee6ULL, 0x92722c851482353bULL, 0xa2bfe8a14cf10364ULL, 0xa81a664bbc423001ULL,
    0xc24b8b70d0f89791ULL, 0xc76c51a30654be30ULL, 0xd192e819d6ef5218ULL, 0xd69906245565a910ULL, 0xf40e35855771202aULL, 0x106aa07032bbd1b8ULL,
    0x19a4c116b8d2d0c8ULL, 0x1e376c085141ab53ULL, 0x2748774cdf8eeb99ULL, 0x34b0bcb5e19b48a8ULL, 0x391c0cb3c5c95a63ULL, 0x4ed8aa4ae3418acbULL,
    0x5b9cca4f7763e373ULL, 0x682e6ff3d6b2b8a3ULL, 0x748f82ee5defb2fcULL, 0x78a5636f43172f60ULL, 0x84c87814a1f0ab72ULL, 0x8cc702081a6439ecULL,
    0x90befffa23631e28ULL, 0xa4506cebde82bde9ULL, 0xbef9a3f7b2c67915ULL, 0xc67178f2e372532bULL, 0xca273eceea26619cULL, 0xd186b8c721c0c207ULL,
    0xeada7dd6cde0eb1eULL, 0xf57d4f7fee6ed178ULL, 0x06f067aa72176fbaULL, 0x0a637dc5a2c898a6ULL, 0x113f9804bef90daeULL, 0x1b710b35131c471bULL,
    0x28db77f523047d84ULL, 0x32caab7b40c72493ULL, 0x3c9ebe0a15c9bebcULL, 0x431d67c49c100d4cULL, 0x4cc5d4becb3e42b6ULL, 0x597f299cfc657e2aULL,
    0x5fcb6fab3ad6faecULL, 0x6c44198c4a475817ULL };
static void fire_hash_sha512Block(uint64_t* h, const uint8_t* p) {
    uint64_t w[80], s[8];
    int i;
    for (i = 0; i < 16; i++) w[i] = fire_hash_be64(p + 8 * i);
    for (i = 16; i < 80; i++) {
        uint64_t s0 = fire_hash_ror64(w[i - 15], 1) ^ fire_hash_ror64(w[i - 15], 8) ^ (w[i - 15] >> 7);
        uint64_t s1 = fire_hash_ror64(w[i - 2], 19) ^ fire_hash_ror64(w[i - 2], 61) ^ (w[i - 2] >> 6);
        w[i] = w[i - 16] + s0 + w[i - 7] + s1;
    }
    for (i = 0; i < 8; i++) s[i] = h[i];
    for (i = 0; i < 80; i++) {
        uint64_t S1 = fire_hash_ror64(s[4], 14) ^ fire_hash_ror64(s[4], 18) ^ fire_hash_ror64(s[4], 41);
        uint64_t ch = (s[4] & s[5]) ^ (~s[4] & s[6]);
        uint64_t t1 = s[7] + S1 + ch + fire_hash_k512[i] + w[i];
        uint64_t S0 = fire_hash_ror64(s[0], 28) ^ fire_hash_ror64(s[0], 34) ^ fire_hash_ror64(s[0], 39);
        uint64_t mj = (s[0] & s[1]) ^ (s[0] & s[2]) ^ (s[1] & s[2]);
        uint64_t t2 = S0 + mj;
        s[7] = s[6]; s[6] = s[5]; s[5] = s[4]; s[4] = s[3] + t1; s[3] = s[2]; s[2] = s[1]; s[1] = s[0]; s[0] = t1 + t2;
    }
    for (i = 0; i < 8; i++) h[i] += s[i];
}

/* CRC-32 (IEEE 802.3, the one of Zip, gzip and PNG) */
static const uint32_t fire_hash_crcTable[256] = {
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

static int fire_hash_digestSize(uint32_t algo) {
    switch (algo) {
        case FIRE_HASH_MD5: return 16;
        case FIRE_HASH_SHA1: return 20;
        case FIRE_HASH_SHA256: return 32;
        case FIRE_HASH_SHA384: return 48;
        case FIRE_HASH_SHA512: return 64;
        case FIRE_HASH_CRC32: case FIRE_HASH_ADLER32: return 4;
        default: return 0;
    }
}
static int fire_hash_blockSize(uint32_t algo) { return algo == FIRE_HASH_SHA384 || algo == FIRE_HASH_SHA512 ? 128 : 64; }

static void fire_hash_start(FireHashCtx* c, uint32_t algo) {
    static const uint32_t sha256[8] = { 0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19 };
    static const uint64_t sha512[8] = { 0x6a09e667f3bcc908ULL, 0xbb67ae8584caa73bULL, 0x3c6ef372fe94f82bULL, 0xa54ff53a5f1d36f1ULL, 0x510e527fade682d1ULL,
                                        0x9b05688c2b3e6c1fULL, 0x1f83d9abfb41bd6bULL, 0x5be0cd19137e2179ULL };
    static const uint64_t sha384[8] = { 0xcbbb9d5dc1059ed8ULL, 0x629a292a367cd507ULL, 0x9159015a3070dd17ULL, 0x152fecd8f70e5939ULL, 0x67332667ffc00b31ULL,
                                        0x8eb44a8768581511ULL, 0xdb0c2e0d64f98fa7ULL, 0x47b5481dbefa4fa4ULL };
    memset(c, 0, sizeof *c);
    c->tag = FIRE_HASH_TAG;
    c->algo = algo;
    switch (algo) {
        case FIRE_HASH_MD5: c->h.w[0] = 0x67452301; c->h.w[1] = 0xefcdab89; c->h.w[2] = 0x98badcfe; c->h.w[3] = 0x10325476; break;
        case FIRE_HASH_SHA1: c->h.w[0] = 0x67452301; c->h.w[1] = 0xEFCDAB89; c->h.w[2] = 0x98BADCFE; c->h.w[3] = 0x10325476; c->h.w[4] = 0xC3D2E1F0; break;
        case FIRE_HASH_SHA256: memcpy(c->h.w, sha256, sizeof sha256); break;
        case FIRE_HASH_SHA384: memcpy(c->h.d, sha384, sizeof sha384); break;
        case FIRE_HASH_SHA512: memcpy(c->h.d, sha512, sizeof sha512); break;
        case FIRE_HASH_CRC32: c->h.w[0] = 0xFFFFFFFFu; break;
        case FIRE_HASH_ADLER32: c->h.w[0] = 1; c->h.w[1] = 0; break;
        default: break;
    }
}

static void fire_hash_block(FireHashCtx* c, const uint8_t* p) {
    switch (c->algo) {
        case FIRE_HASH_MD5: fire_hash_md5Block(c->h.w, p); break;
        case FIRE_HASH_SHA1: fire_hash_sha1Block(c->h.w, p); break;
        case FIRE_HASH_SHA256: fire_hash_sha256Block(c->h.w, p); break;
        default: fire_hash_sha512Block(c->h.d, p); break;
    }
}

static void fire_hash_update(FireHashCtx* c, const uint8_t* p, size_t n) {
    if (c->algo == FIRE_HASH_CRC32) {
        uint32_t crc = c->h.w[0];
        size_t i;
        for (i = 0; i < n; i++) crc = fire_hash_crcTable[(crc ^ p[i]) & 0xFF] ^ (crc >> 8);
        c->h.w[0] = crc;
        return;
    }
    if (c->algo == FIRE_HASH_ADLER32) {
        uint32_t a = c->h.w[0], b = c->h.w[1];
        while (n > 0) {
            size_t chunk = n < 5552 ? n : 5552, i;   /* (5552 bytes cannot overflow the 32-bit sums before the modulo) */
            for (i = 0; i < chunk; i++) { a += p[i]; b += a; }
            a %= 65521u;
            b %= 65521u;
            p += chunk;
            n -= chunk;
        }
        c->h.w[0] = a;
        c->h.w[1] = b;
        return;
    }
    {
        size_t bs = (size_t)fire_hash_blockSize(c->algo);
        uint64_t before = c->length;
        c->length += n;
        if (c->length < before) c->lengthHigh++;
        if (c->used > 0) {
            size_t take = bs - c->used < n ? bs - c->used : n;
            memcpy(c->block + c->used, p, take);
            c->used += (uint32_t)take;
            p += take;
            n -= take;
            if (c->used < bs) return;
            fire_hash_block(c, c->block);
            c->used = 0;
        }
        while (n >= bs) { fire_hash_block(c, p); p += bs; n -= bs; }
        if (n > 0) { memcpy(c->block, p, n); c->used = (uint32_t)n; }
    }
}

/* The digest into `out` (fire_hash_digestSize bytes); the context is spent afterwards. */
static void fire_hash_final(FireHashCtx* c, uint8_t* out) {
    int i, size = fire_hash_digestSize(c->algo);
    if (c->algo == FIRE_HASH_CRC32 || c->algo == FIRE_HASH_ADLER32) {
        uint32_t v = c->algo == FIRE_HASH_CRC32 ? c->h.w[0] ^ 0xFFFFFFFFu : (c->h.w[1] << 16) | c->h.w[0];
        out[0] = (uint8_t)(v >> 24); out[1] = (uint8_t)(v >> 16); out[2] = (uint8_t)(v >> 8); out[3] = (uint8_t)v;   /* (big endian, as they are written as hex) */
        return;
    }
    {
        size_t bs = (size_t)fire_hash_blockSize(c->algo), lenBytes = bs == 128 ? 16 : 8;
        uint64_t bitsLow = c->length << 3, bitsHigh = (c->lengthHigh << 3) | (c->length >> 61);
        c->block[c->used++] = 0x80;
        if (c->used > bs - lenBytes) {
            memset(c->block + c->used, 0, bs - c->used);
            fire_hash_block(c, c->block);
            c->used = 0;
        }
        memset(c->block + c->used, 0, bs - c->used);
        if (c->algo == FIRE_HASH_MD5) {
            for (i = 0; i < 8; i++) c->block[56 + i] = (uint8_t)(bitsLow >> (8 * i));
        } else {
            for (i = 0; i < 8; i++) c->block[bs - 1 - i] = (uint8_t)(bitsLow >> (8 * i));
            if (lenBytes == 16) for (i = 0; i < 8; i++) c->block[bs - 9 - i] = (uint8_t)(bitsHigh >> (8 * i));
        }
        fire_hash_block(c, c->block);
    }
    for (i = 0; i < size; i++) {
        if (c->algo == FIRE_HASH_MD5) out[i] = (uint8_t)(c->h.w[i / 4] >> (8 * (i % 4)));
        else if (c->algo == FIRE_HASH_SHA1 || c->algo == FIRE_HASH_SHA256) out[i] = (uint8_t)(c->h.w[i / 4] >> (24 - 8 * (i % 4)));
        else out[i] = (uint8_t)(c->h.d[i / 8] >> (56 - 8 * (i % 8)));
    }
}

#ifndef FIRE_HASH_ENGINE_ONLY   /* (the algorithms alone, e.g. for a test) */
/* ---- natives ----------------------------------------------------------------------------------------------------------------------------------- */
/* An ArgumentException (natively it is only marked as thrown: the caller returns right away): always 0. */
static int fire_hashb_argument(const char* text) {
    fireError("ArgumentException", text);
    return 0;
}

/* The hasher in a state buffer (checked: an ArgumentException for anything else, then 0). The state is copied out and back in (the bytes of a buffer need not
 * be aligned). */
static int fire_hashb_load(Value state, FireHashCtx* c) {
    Buf* b;
    if (state.kind != K_Buffer) return fire_hashb_argument("This is not the state of a hasher.");
    if (!leafAlive(state)) { destroyedError(state); return 0; }
    b = bufOf(state);
    if (b->length != sizeof(FireHashCtx)) return fire_hashb_argument("This is not the state of a hasher.");
    memcpy(c, b->data, sizeof *c);
    if (c->tag != FIRE_HASH_TAG || fire_hash_digestSize(c->algo) == 0 || c->used > 128) return fire_hashb_argument("This is not the state of a hasher.");
    return 1;
}
static void fire_hashb_store(Value state, const FireHashCtx* c) { memcpy(bufOf(state)->data, c, sizeof *c); }

/* A new hasher for `algo` (1 MD5, 2 SHA-1, 3 SHA-256, 4 SHA-384, 5 SHA-512, 6 CRC-32, 7 Adler-32): its state as a buffer. */
static Value fire_hash_Init(Value algo, OwnList* list) {
    FireHashCtx c;
    Buf* b;
    if (algo.kind != K_Int || algo.i < 1 || algo.i > 7) return fireError("ArgumentException", "Unknown hash algorithm.");
    fire_hash_start(&c, (uint32_t)algo.i);
    b = allocBuf((uint32_t)sizeof c, list);
    memcpy(b->data, &c, sizeof c);
    return BufV(b);
}

/* Hashes `count` bytes of `data` from `offset`. */
static Value fire_hash_Update(Value state, Value data, Value offset, Value count) {
    FireHashCtx c;
    int64_t length;
    if (!fire_hashb_load(state, &c)) return Undef();
    if (data.kind != K_Buffer) return fireError("ArgumentException", "The data to hash must be a byte buffer.");
    if (!leafAlive(data)) return destroyedError(data);
    length = bufOf(data)->length;
    if (offset.i < 0 || count.i < 0 || offset.i > length || count.i > length - offset.i) {
        char text[160];
        snprintf(text, sizeof text, "offset/count (%lld/%lld) are outside of the buffer (length %lld).", (long long)offset.i, (long long)count.i, (long long)length);
        return fireError("ArgumentException", text);
    }
    fire_hash_update(&c, bufOf(data)->data + offset.i, (size_t)count.i);
    fire_hashb_store(state, &c);
    return Bool(true);
}

/* Hashes the text as UTF-8 (a lone surrogate as U+FFFD, like IO.Utf8.GetBytes). */
static Value fire_hash_UpdateText(Value state, Value text) {
    FireHashCtx c;
    const Str* s;
    uint8_t chunk[256];
    size_t n = 0;
    uint32_t i;
    if (!fire_hashb_load(state, &c)) return Undef();
    if (text.kind != K_String) return fireError("ArgumentException", "The text to hash must be a string.");
    s = strOf(text);
    for (i = 0; i < s->length; i++) {
        uint32_t u = s->data[i];
        if (u >= 0xD800 && u <= 0xDBFF && i + 1 < s->length && s->data[i + 1] >= 0xDC00 && s->data[i + 1] <= 0xDFFF) { u = 0x10000 + ((u - 0xD800) << 10) + (s->data[i + 1] - 0xDC00); i++; }
        else if (u >= 0xD800 && u <= 0xDFFF) u = 0xFFFD;
        if (n > sizeof chunk - 4) { fire_hash_update(&c, chunk, n); n = 0; }
        if (u < 0x80) chunk[n++] = (uint8_t)u;
        else if (u < 0x800) { chunk[n++] = (uint8_t)(0xC0 | (u >> 6)); chunk[n++] = (uint8_t)(0x80 | (u & 0x3F)); }
        else if (u < 0x10000) { chunk[n++] = (uint8_t)(0xE0 | (u >> 12)); chunk[n++] = (uint8_t)(0x80 | ((u >> 6) & 0x3F)); chunk[n++] = (uint8_t)(0x80 | (u & 0x3F)); }
        else { chunk[n++] = (uint8_t)(0xF0 | (u >> 18)); chunk[n++] = (uint8_t)(0x80 | ((u >> 12) & 0x3F)); chunk[n++] = (uint8_t)(0x80 | ((u >> 6) & 0x3F)); chunk[n++] = (uint8_t)(0x80 | (u & 0x3F)); }
    }
    if (n > 0) fire_hash_update(&c, chunk, n);
    fire_hashb_store(state, &c);
    return Bool(true);
}

/* The digest (a new buffer); the hasher starts anew. */
static Value fire_hash_Final(Value state, OwnList* list) {
    FireHashCtx c;
    Buf* b;
    uint32_t algo;
    if (!fire_hashb_load(state, &c)) return Undef();
    algo = c.algo;
    b = allocBuf((uint32_t)fire_hash_digestSize(algo), list);
    fire_hash_final(&c, b->data);
    fire_hash_start(&c, algo);
    fire_hashb_store(state, &c);
    return BufV(b);
}

/* The bytes as lower-case hex digits. */
static Value fire_hash_ToHex(Value data, OwnList* list) {
    static const char digits[] = "0123456789abcdef";
    Buf* b;
    Str* s;
    uint32_t i;
    if (data.kind != K_Buffer) return fireError("ArgumentException", "Only a byte buffer can be written as hex.");
    if (!leafAlive(data)) return destroyedError(data);
    b = bufOf(data);
    s = allocStr(b->length * 2, list);
    for (i = 0; i < b->length; i++) {
        strChars(s)[2 * i] = (fire_char16)digits[b->data[i] >> 4];
        strChars(s)[2 * i + 1] = (fire_char16)digits[b->data[i] & 15];
    }
    return StrV(s);
}

static int fire_hashb_hexDigit(uint32_t c) {
    if (c >= '0' && c <= '9') return (int)(c - '0');
    if (c >= 'a' && c <= 'f') return (int)(c - 'a' + 10);
    if (c >= 'A' && c <= 'F') return (int)(c - 'A' + 10);
    return -1;
}
/* Hex digits (upper or lower case; spaces, colons and dashes between the bytes are skipped) as bytes: an ArgumentException for anything else. */
static Value fire_hash_FromHex(Value text, OwnList* list) {
    const Str* s;
    Buf* b;
    uint32_t i, n = 0;
    int high = -1;
    if (text.kind != K_String) return fireError("ArgumentException", "Hex digits must be a string.");
    s = strOf(text);
    for (i = 0; i < s->length; i++) {
        uint32_t c = s->data[i];
        if ((c == ' ' || c == ':' || c == '-') && high < 0) continue;
        if (fire_hashb_hexDigit(c) < 0) {
            char message[96];
            snprintf(message, sizeof message, "'%c' at %u is not a hex digit.", c < 127 && c >= 32 ? (char)c : '?', (unsigned)i);
            return fireError("ArgumentException", message);
        }
        if (high < 0) high = 0; else { high = -1; n++; }
    }
    if (high >= 0) return fireError("ArgumentException", "The hex digits have an odd number.");
    b = allocBuf(n, list);
    n = 0;
    for (i = 0; i < s->length; i++) {
        int d = fire_hashb_hexDigit(s->data[i]);
        if (d < 0) continue;
        if (high < 0) high = d;
        else { b->data[n++] = (uint8_t)((high << 4) | d); high = -1; }
    }
    return BufV(b);
}

/* Whether two buffers hold the same bytes, in a time that depends only on the length (for comparing digests and MACs). */
static Value fire_hash_Equal(Value a, Value b) {
    Buf* x;
    Buf* y;
    uint32_t i;
    uint8_t diff = 0;
    if (a.kind != K_Buffer || b.kind != K_Buffer) return fireError("ArgumentException", "Only byte buffers can be compared.");
    if (!leafAlive(a)) return destroyedError(a);
    if (!leafAlive(b)) return destroyedError(b);
    x = bufOf(a);
    y = bufOf(b);
    if (x->length != y->length) return Bool(false);
    for (i = 0; i < x->length; i++) diff |= (uint8_t)(x->data[i] ^ y->data[i]);
    return Bool(diff == 0);
}

#endif

#endif
