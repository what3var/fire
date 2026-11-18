/* fire ai: a small language model engine in plain C (no library) - docs/ai/README.md.
 *
 * It reads a model in the GGUF format (the file format of llama.cpp) and runs it on the CPU:
 *   - architectures: llama-like decoders - qwen2 (Qwen 2 / 2.5, also the Coder models) and llama (Llama, SmolLM, TinyLlama): RMS norm, rotary positions,
 *     grouped-query attention (with the biases of q, k and v when the model has them), SwiGLU feed forward;
 *   - weights: F32, F16, Q8_0 and Q4_0 (the activations are quantized to Q8_0 for the products, as llama.cpp does: the same numbers to a few digits);
 *   - the tokenizer of the file: byte-level BPE (gpt2 style, the pre-tokenizer of qwen2 / llama 3) or SentencePiece-like (llama: "▁" for the space);
 *   - a key/value cache that keeps what was computed: a prompt that starts like the one before only computes the new part;
 *   - sampling (temperature, top-k, top-p, min-p, repetition penalty, a seed) and the log-probability of given continuations (multiple choice);
 *   - threads for the matrix products (a pool that stays).
 *
 * Nothing here knows fire: fire_bridge_ai.h puts it behind the natives. Everything is `static` (one translation unit, like the other engines of fire).
 */
#ifndef FIRE_AI_ENGINE_H
#define FIRE_AI_ENGINE_H

#include <stdint.h>
#include <stddef.h>
#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <math.h>
#include <time.h>

#if !defined(FIRE_AI_NO_THREADS) && !defined(__EMSCRIPTEN__) && !defined(ESP_PLATFORM) && (defined(_WIN32) || defined(__unix__) || defined(__APPLE__))
#define FAI_THREADS 1
#endif
#if defined(_WIN32)
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#elif defined(__unix__) || defined(__APPLE__)
#include <pthread.h>
#include <unistd.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <fcntl.h>
#define FAI_MMAP 1
#endif

/* ---- errors ------------------------------------------------------------------------------------------------------------------------------------- */
typedef struct FaiError { char text[256]; } FaiError;
static int fai_fail(FaiError* e, const char* fmt, const char* a) {
    if (e) snprintf(e->text, sizeof e->text, fmt, a ? a : "");
    return 0;
}

/* ---- half floats ------------------------------------------------------------------------------------------------------------------------------- */
static float fai_half(uint16_t h) {
    uint32_t sign = (uint32_t)(h & 0x8000) << 16, exp = (h >> 10) & 0x1F, man = h & 0x3FF, bits;
    float f;
    if (exp == 0) {
        if (man == 0) bits = sign;
        else {   /* subnormal */
            float v = (float)man * (1.0f / 16777216.0f);
            return sign ? -v : v;
        }
    }
    else if (exp == 31) bits = sign | 0x7F800000u | (man << 13);
    else bits = sign | ((exp + 112) << 23) | (man << 13);
    memcpy(&f, &bits, 4);
    return f;
}
static uint16_t fai_toHalf(float f) {
    uint32_t x, sign, exp, man;
    memcpy(&x, &f, 4);
    sign = (x >> 16) & 0x8000; exp = (x >> 23) & 0xFF; man = x & 0x7FFFFF;
    if (exp == 0xFF) return (uint16_t)(sign | 0x7C00 | (man ? 0x200 : 0));
    if (exp > 142) return (uint16_t)(sign | 0x7C00);
    if (exp < 113) {
        if (exp < 103) return (uint16_t)sign;
        man |= 0x800000;
        {
            uint32_t shift = 126 - exp, r = man >> shift, rem = man & ((1u << shift) - 1), half = 1u << (shift - 1);
            if (rem > half || (rem == half && (r & 1))) r++;
            return (uint16_t)(sign | r);
        }
    }
    {
        uint32_t r = ((exp - 112) << 10) | (man >> 13), rem = man & 0x1FFF;
        if (rem > 0x1000 || (rem == 0x1000 && (r & 1))) r++;
        return (uint16_t)(sign | r);
    }
}

/* ---- tensors ----------------------------------------------------------------------------------------------------------------------------------- */
enum { FAI_F32 = 0, FAI_F16 = 1, FAI_Q4_0 = 2, FAI_Q4_1 = 3, FAI_Q8_0 = 8, FAI_Q4_K = 12, FAI_Q5_K = 13, FAI_Q6_K = 14 };
#define FAI_QK 32
typedef struct FaiQ8 { uint16_t d; int8_t q[FAI_QK]; } FaiQ8;     /* 34 bytes */
typedef struct FaiQ4 { uint16_t d; uint8_t q[FAI_QK / 2]; } FaiQ4; /* 18 bytes */
#define FAI_QKK 256
/* Q6_K: 256 values of 6 bits in 16 groups with a scale each (the low 4 bits, the high 2 bits, the scales, the scale of the block) - 210 bytes */
typedef struct FaiQ41 { uint16_t d, m; uint8_t q[FAI_QK / 2]; } FaiQ41;    /* 20 bytes: x = d * q + m */
/* Q4_K / Q5_K: 8 groups of 32 with a 6-bit scale and minimum each (packed into 12 bytes), x = d * scale * q - dmin * min */
typedef struct FaiQ4K { uint16_t d, dmin; uint8_t sc[12]; uint8_t qs[FAI_QKK / 2]; } FaiQ4K;                       /* 144 bytes */
typedef struct FaiQ5K { uint16_t d, dmin; uint8_t sc[12]; uint8_t qh[FAI_QKK / 8]; uint8_t qs[FAI_QKK / 2]; } FaiQ5K; /* 176 bytes */
typedef struct FaiQ6K { uint8_t ql[FAI_QKK / 2]; uint8_t qh[FAI_QKK / 4]; int8_t sc[FAI_QKK / 16]; uint16_t d; } FaiQ6K;

typedef struct FaiTensor {
    uint8_t* owned;  /* the weights converted to Q8_0 at loading (freed with the model), else NULL */
    char name[96];
    int type;
    int64_t ne[4];   /* ne[0]: the length of a row (the inputs), ne[1]: the rows (the outputs) */
    uint64_t offset;
    const uint8_t* data;
} FaiTensor;

static size_t fai_rowBytes(int type, int64_t n) {
    switch (type) {
        case FAI_F32: return (size_t)n * 4;
        case FAI_F16: return (size_t)n * 2;
        case FAI_Q8_0: return (size_t)(n / FAI_QK) * sizeof(FaiQ8);
        case FAI_Q4_0: return (size_t)(n / FAI_QK) * sizeof(FaiQ4);
        case FAI_Q6_K: return (size_t)(n / FAI_QKK) * sizeof(FaiQ6K);
        case FAI_Q4_1: return (size_t)(n / FAI_QK) * sizeof(FaiQ41);
        case FAI_Q4_K: return (size_t)(n / FAI_QKK) * sizeof(FaiQ4K);
        case FAI_Q5_K: return (size_t)(n / FAI_QKK) * sizeof(FaiQ5K);
        default: return 0;
    }
}

static void fai_scaleMin(int j, const uint8_t* q, int* d, int* m) {
    if (j < 4) { *d = q[j] & 63; *m = q[j + 4] & 63; }
    else { *d = (q[j + 4] & 0xF) | ((q[j - 4] >> 6) << 4); *m = (q[j + 4] >> 4) | ((q[j] >> 6) << 4); }
}
/* The 256 values of a Q4_K or Q5_K block as small integers 0..31, and the scale and minimum of each group of 32. */
static void fai_unpackK(int type, const uint8_t* block, uint8_t v[FAI_QKK], int sc[8], int mn[8], float* d, float* dmin) {
    int j, l, g;
    if (type == FAI_Q4_K) {
        const FaiQ4K* q = (const FaiQ4K*)block;
        const uint8_t* qs = q->qs;
        *d = fai_half(q->d); *dmin = fai_half(q->dmin);
        for (g = 0; g < 8; g++) fai_scaleMin(g, q->sc, &sc[g], &mn[g]);
        for (j = 0; j < 4; j++, qs += 32)
            for (l = 0; l < 32; l++) { v[j * 64 + l] = qs[l] & 0x0F; v[j * 64 + 32 + l] = qs[l] >> 4; }
    }
    else {
        const FaiQ5K* q = (const FaiQ5K*)block;
        const uint8_t* qs = q->qs;
        int u1 = 1, u2 = 2;
        *d = fai_half(q->d); *dmin = fai_half(q->dmin);
        for (g = 0; g < 8; g++) fai_scaleMin(g, q->sc, &sc[g], &mn[g]);
        for (j = 0; j < 4; j++, qs += 32, u1 <<= 2, u2 <<= 2)
            for (l = 0; l < 32; l++) {
                v[j * 64 + l] = (uint8_t)((qs[l] & 0x0F) + ((q->qh[l] & u1) ? 16 : 0));
                v[j * 64 + 32 + l] = (uint8_t)((qs[l] >> 4) + ((q->qh[l] & u2) ? 16 : 0));
            }
    }
}

/* One row of a tensor as floats. */
static void fai_dequantRow(const FaiTensor* t, int64_t row, float* out) {
    int64_t n = t->ne[0], i, b;
    const uint8_t* p = t->data + fai_rowBytes(t->type, n) * (size_t)row;
    switch (t->type) {
        case FAI_F32: memcpy(out, p, (size_t)n * 4); break;
        case FAI_F16: for (i = 0; i < n; i++) { uint16_t h; memcpy(&h, p + i * 2, 2); out[i] = fai_half(h); } break;
        case FAI_Q8_0:
            for (b = 0; b < n / FAI_QK; b++) {
                const FaiQ8* q = (const FaiQ8*)p + b;
                float d = fai_half(q->d);
                for (i = 0; i < FAI_QK; i++) out[b * FAI_QK + i] = d * (float)q->q[i];
            }
            break;
        case FAI_Q4_0:
            for (b = 0; b < n / FAI_QK; b++) {
                const FaiQ4* q = (const FaiQ4*)p + b;
                float d = fai_half(q->d);
                for (i = 0; i < FAI_QK / 2; i++) {
                    out[b * FAI_QK + i] = d * (float)((q->q[i] & 0x0F) - 8);
                    out[b * FAI_QK + i + FAI_QK / 2] = d * (float)((q->q[i] >> 4) - 8);
                }
            }
            break;
        case FAI_Q4_1:
            for (b = 0; b < n / FAI_QK; b++) {
                const FaiQ41* q = (const FaiQ41*)p + b;
                float d = fai_half(q->d), mm = fai_half(q->m);
                for (i = 0; i < FAI_QK / 2; i++) { out[b * FAI_QK + i] = d * (float)(q->q[i] & 0x0F) + mm; out[b * FAI_QK + i + FAI_QK / 2] = d * (float)(q->q[i] >> 4) + mm; }
            }
            break;
        case FAI_Q4_K: case FAI_Q5_K:
            for (b = 0; b < n / FAI_QKK; b++) {
                uint8_t v[FAI_QKK];
                int sc[8], mn[8], g, l;
                float d, dmin;
                fai_unpackK(t->type, p + fai_rowBytes(t->type, FAI_QKK) * (size_t)b, v, sc, mn, &d, &dmin);
                for (g = 0; g < 8; g++) for (l = 0; l < 32; l++) out[b * FAI_QKK + g * 32 + l] = d * sc[g] * (float)v[g * 32 + l] - dmin * mn[g];
            }
            break;
        case FAI_Q6_K:
            for (b = 0; b < n / FAI_QKK; b++) {
                const FaiQ6K* q = (const FaiQ6K*)p + b;
                float d = fai_half(q->d), *y = out + b * FAI_QKK;
                const uint8_t *ql = q->ql, *qh = q->qh;
                const int8_t* sc = q->sc;
                int half;
                for (half = 0; half < 2; half++) {
                    int l;
                    for (l = 0; l < 32; l++) {
                        int is = l / 16;
                        y[l] = d * sc[is] * (float)(((ql[l] & 0x0F) | (((qh[l] >> 0) & 3) << 4)) - 32);
                        y[l + 32] = d * sc[is + 2] * (float)(((ql[l + 32] & 0x0F) | (((qh[l] >> 2) & 3) << 4)) - 32);
                        y[l + 64] = d * sc[is + 4] * (float)(((ql[l] >> 4) | (((qh[l] >> 4) & 3) << 4)) - 32);
                        y[l + 96] = d * sc[is + 6] * (float)(((ql[l + 32] >> 4) | (((qh[l] >> 6) & 3) << 4)) - 32);
                    }
                    y += 128; ql += 64; qh += 32; sc += 8;
                }
            }
            break;
    }
}

/* x (n floats, n a multiple of 32) as Q8_0 blocks. */
static void fai_quantQ8(const float* x, int64_t n, FaiQ8* out) {
    int64_t b, i;
    for (b = 0; b < n / FAI_QK; b++) {
        const float* v = x + b * FAI_QK;
        float amax = 0, d, id;
        for (i = 0; i < FAI_QK; i++) { float a = fabsf(v[i]); if (a > amax) amax = a; }
        d = amax / 127.0f;
        id = d != 0 ? 1.0f / d : 0.0f;
        out[b].d = fai_toHalf(d);
        for (i = 0; i < FAI_QK; i++) out[b].q[i] = (int8_t)lrintf(v[i] * id);
    }
}

/* The scales of the blocks are half floats: a table of all 65536 makes them cheap. */
static float* g_faiHalf;
static void fai_halfTable(void) {
    uint32_t i;
    float* t;
    if (g_faiHalf) return;
    t = (float*)malloc(65536 * sizeof(float));
    if (t == NULL) return;
    for (i = 0; i < 65536; i++) t[i] = fai_half((uint16_t)i);
    g_faiHalf = t;
}
#define FAI_H(h) (g_faiHalf ? g_faiHalf[(h)] : fai_half(h))

/* The product of one row of a quantized weight with the quantized x: plain C, and AVX2 / NEON where the processor has it (chosen once, at the start). */
static float fai_dotQ8_c(const FaiQ8* w, const FaiQ8* x, int64_t blocks) {
    float sum = 0;
    int64_t b;
    int i;
    for (b = 0; b < blocks; b++) {
        int32_t s = 0;
        for (i = 0; i < FAI_QK; i++) s += (int32_t)w[b].q[i] * (int32_t)x[b].q[i];
        sum += (float)s * FAI_H(w[b].d) * FAI_H(x[b].d);
    }
    return sum;
}
static float fai_dotQ4_c(const FaiQ4* w, const FaiQ8* x, int64_t blocks) {
    float sum = 0;
    int64_t b;
    int i;
    for (b = 0; b < blocks; b++) {
        int32_t s = 0;
        const uint8_t* q = w[b].q;
        const int8_t* y = x[b].q;
        for (i = 0; i < FAI_QK / 2; i++) s += ((int32_t)(q[i] & 0x0F) - 8) * y[i] + ((int32_t)(q[i] >> 4) - 8) * y[i + FAI_QK / 2];
        sum += (float)s * FAI_H(w[b].d) * FAI_H(x[b].d);
    }
    return sum;
}

#if (defined(__GNUC__) || defined(__clang__)) && (defined(__x86_64__) || defined(__i386__)) && !defined(FIRE_AI_NO_SIMD)
#define FAI_AVX2 1
#include <immintrin.h>
#define FAI_AVX2_FN __attribute__((target("avx2,fma")))
FAI_AVX2_FN static inline __m256 fai_avxBlock(__m256i w, __m256i x, float scale, __m256 acc) {
    /* |w| * (x with the sign of w): unsigned times signed bytes, summed in pairs, then in fours */
    __m256i ax = _mm256_sign_epi8(w, w), sy = _mm256_sign_epi8(x, w);
    __m256i p16 = _mm256_maddubs_epi16(ax, sy);
    __m256i p32 = _mm256_madd_epi16(p16, _mm256_set1_epi16(1));
    return _mm256_fmadd_ps(_mm256_cvtepi32_ps(p32), _mm256_set1_ps(scale), acc);
}
FAI_AVX2_FN static float fai_hsum(__m256 v) {
    __m128 lo = _mm256_castps256_ps128(v), hi = _mm256_extractf128_ps(v, 1);
    lo = _mm_add_ps(lo, hi);
    lo = _mm_add_ps(lo, _mm_movehl_ps(lo, lo));
    lo = _mm_add_ss(lo, _mm_shuffle_ps(lo, lo, 1));
    return _mm_cvtss_f32(lo);
}
FAI_AVX2_FN static float fai_dotQ8_avx2(const FaiQ8* w, const FaiQ8* x, int64_t blocks) {
    __m256 acc = _mm256_setzero_ps();
    int64_t b;
    for (b = 0; b < blocks; b++) {
        __m256i wv = _mm256_loadu_si256((const __m256i*)w[b].q), xv = _mm256_loadu_si256((const __m256i*)x[b].q);
        acc = fai_avxBlock(wv, xv, g_faiHalf[w[b].d] * g_faiHalf[x[b].d], acc);
    }
    return fai_hsum(acc);
}
FAI_AVX2_FN static float fai_dotQ4_avx2(const FaiQ4* w, const FaiQ8* x, int64_t blocks) {
    __m256 acc = _mm256_setzero_ps();
    const __m128i low = _mm_set1_epi8(0x0F);
    const __m256i eight = _mm256_set1_epi8(8);
    int64_t b;
    for (b = 0; b < blocks; b++) {
        __m128i raw = _mm_loadu_si128((const __m128i*)w[b].q);
        __m128i lo = _mm_and_si128(raw, low), hi = _mm_and_si128(_mm_srli_epi16(raw, 4), low);
        __m256i wv = _mm256_sub_epi8(_mm256_set_m128i(hi, lo), eight);
        __m256i xv = _mm256_loadu_si256((const __m256i*)x[b].q);
        acc = fai_avxBlock(wv, xv, g_faiHalf[w[b].d] * g_faiHalf[x[b].d], acc);
    }
    return fai_hsum(acc);
}
#endif

#if defined(__aarch64__) && defined(__ARM_NEON) && !defined(FIRE_AI_NO_SIMD)
#define FAI_NEON 1
#include <arm_neon.h>
static inline int32x4_t fai_neonDot(int8x16_t a, int8x16_t b, int32x4_t acc) {
    int16x8_t lo = vmull_s8(vget_low_s8(a), vget_low_s8(b)), hi = vmull_s8(vget_high_s8(a), vget_high_s8(b));
    return vpadalq_s16(vpadalq_s16(acc, lo), hi);
}
static float fai_dotQ8_neon(const FaiQ8* w, const FaiQ8* x, int64_t blocks) {
    float32x4_t acc = vdupq_n_f32(0);
    int64_t b;
    for (b = 0; b < blocks; b++) {
        int32x4_t s = vdupq_n_s32(0);
        s = fai_neonDot(vld1q_s8(w[b].q), vld1q_s8(x[b].q), s);
        s = fai_neonDot(vld1q_s8(w[b].q + 16), vld1q_s8(x[b].q + 16), s);
        acc = vmlaq_n_f32(acc, vcvtq_f32_s32(s), g_faiHalf[w[b].d] * g_faiHalf[x[b].d]);
    }
    return vaddvq_f32(acc);
}
static float fai_dotQ4_neon(const FaiQ4* w, const FaiQ8* x, int64_t blocks) {
    float32x4_t acc = vdupq_n_f32(0);
    const uint8x16_t low = vdupq_n_u8(0x0F);
    const int8x16_t eight = vdupq_n_s8(8);
    int64_t b;
    for (b = 0; b < blocks; b++) {
        uint8x16_t raw = vld1q_u8(w[b].q);
        int8x16_t lo = vsubq_s8(vreinterpretq_s8_u8(vandq_u8(raw, low)), eight), hi = vsubq_s8(vreinterpretq_s8_u8(vshrq_n_u8(raw, 4)), eight);
        int32x4_t s = vdupq_n_s32(0);
        s = fai_neonDot(lo, vld1q_s8(x[b].q), s);
        s = fai_neonDot(hi, vld1q_s8(x[b].q + 16), s);
        acc = vmlaq_n_f32(acc, vcvtq_f32_s32(s), g_faiHalf[w[b].d] * g_faiHalf[x[b].d]);
    }
    return vaddvq_f32(acc);
}
#endif

static float (*fai_dotQ8)(const FaiQ8*, const FaiQ8*, int64_t) = fai_dotQ8_c;
static float (*fai_dotQ4)(const FaiQ4*, const FaiQ8*, int64_t) = fai_dotQ4_c;
static const char* g_faiKernels = "C";
/* Picks the kernels (once). `simd` 0: plain C (the tests compare both). */
static void fai_kernels(int simd) {
    fai_halfTable();
    fai_dotQ8 = fai_dotQ8_c; fai_dotQ4 = fai_dotQ4_c; g_faiKernels = "C";
    if (!simd || g_faiHalf == NULL) return;
#ifdef FAI_AVX2
    __builtin_cpu_init();
    if (__builtin_cpu_supports("avx2") && __builtin_cpu_supports("fma")) { fai_dotQ8 = fai_dotQ8_avx2; fai_dotQ4 = fai_dotQ4_avx2; g_faiKernels = "AVX2"; }
#endif
#ifdef FAI_NEON
    fai_dotQ8 = fai_dotQ8_neon; fai_dotQ4 = fai_dotQ4_neon; g_faiKernels = "NEON";
#endif
}
/* A row of Q6_K with the x in Q8_0 blocks: each group of 16 lies in one block of x, so the products are integers per group, scaled by the group,
 * the block of the weights and the block of x. */
static float fai_dotQ6K(const FaiQ6K* w, const FaiQ8* x, int64_t blocks) {
    float sum = 0;
    int64_t b;
    for (b = 0; b < blocks; b++) {
        const FaiQ6K* q = &w[b];
        const uint8_t *ql = q->ql, *qh = q->qh;
        const int8_t* sc = q->sc;
        int8_t v[FAI_QKK];
        int half, l, g;
        float d = FAI_H(q->d), blockSum = 0;
        for (half = 0; half < 2; half++) {
            int8_t* y = v + half * 128;
            for (l = 0; l < 32; l++) {
                y[l] = (int8_t)(((ql[l] & 0x0F) | (((qh[l] >> 0) & 3) << 4)) - 32);
                y[l + 32] = (int8_t)(((ql[l + 32] & 0x0F) | (((qh[l] >> 2) & 3) << 4)) - 32);
                y[l + 64] = (int8_t)(((ql[l] >> 4) | (((qh[l] >> 4) & 3) << 4)) - 32);
                y[l + 96] = (int8_t)(((ql[l + 32] >> 4) | (((qh[l] >> 6) & 3) << 4)) - 32);
            }
            ql += 64; qh += 32;
        }
        for (g = 0; g < 16; g++) {
            const FaiQ8* xb = &x[b * 8 + g / 2];
            const int8_t* xq = xb->q + (g & 1) * 16;
            const int8_t* wq = v + g * 16;
            int32_t s32 = 0;
            for (l = 0; l < 16; l++) s32 += (int32_t)wq[l] * xq[l];
            blockSum += (float)(s32 * sc[g]) * FAI_H(xb->d);
        }
        sum += d * blockSum;
    }
    return sum;
}
static float fai_dotQ41(const FaiQ41* w, const FaiQ8* x, int64_t blocks) {
    float sum = 0;
    int64_t b;
    int i;
    for (b = 0; b < blocks; b++) {
        int32_t s32 = 0, sx = 0;
        for (i = 0; i < FAI_QK / 2; i++) {
            s32 += (int32_t)(w[b].q[i] & 0x0F) * x[b].q[i] + (int32_t)(w[b].q[i] >> 4) * x[b].q[i + FAI_QK / 2];
            sx += x[b].q[i] + x[b].q[i + FAI_QK / 2];
        }
        sum += FAI_H(x[b].d) * (FAI_H(w[b].d) * (float)s32 + FAI_H(w[b].m) * (float)sx);
    }
    return sum;
}
/* Q4_K / Q5_K with x in Q8_0: each group of 32 is one block of x. */
static float fai_dotK(int type, const uint8_t* row, const FaiQ8* x, int64_t blocks) {
    float sum = 0;
    int64_t b;
    size_t bb = fai_rowBytes(type, FAI_QKK);
    for (b = 0; b < blocks; b++) {
        uint8_t v[FAI_QKK];
        int sc[8], mn[8], g, l;
        float d, dmin, acc = 0;
        fai_unpackK(type, row + bb * (size_t)b, v, sc, mn, &d, &dmin);
        for (g = 0; g < 8; g++) {
            const FaiQ8* xb = &x[b * 8 + g];
            int32_t s32 = 0, sx = 0;
            for (l = 0; l < 32; l++) { s32 += (int32_t)v[g * 32 + l] * xb->q[l]; sx += xb->q[l]; }
            acc += FAI_H(xb->d) * (d * (float)(sc[g] * s32) - dmin * (float)(mn[g] * sx));
        }
        sum += acc;
    }
    return sum;
}
static float fai_dotF(const float* w, const float* x, int64_t n) { float s = 0; int64_t i; for (i = 0; i < n; i++) s += w[i] * x[i]; return s; }
static float fai_dotH(const uint8_t* w, const float* x, int64_t n) {
    float s = 0; int64_t i;
    for (i = 0; i < n; i++) { uint16_t h; memcpy(&h, w + i * 2, 2); s += fai_half(h) * x[i]; }
    return s;
}

/* ---- threads: a pool that stays (the matrix products of one token are too short to start threads for each) ---------------------------------- */
typedef void (*FaiJob)(void* arg, int64_t from, int64_t to);
#define FAI_MAX_THREADS 32
typedef struct FaiPool {
    int count;                 /* the threads besides the caller */
    volatile int generation;   /* counts the jobs */
    volatile int quit;
    FaiJob job; void* arg; int64_t total;
    volatile int pending;      /* the workers not done with the job */
#ifdef FAI_THREADS
#ifdef _WIN32
    HANDLE th[FAI_MAX_THREADS];
    CRITICAL_SECTION lock; CONDITION_VARIABLE wake, done;
#else
    pthread_t th[FAI_MAX_THREADS];
    pthread_mutex_t lock; pthread_cond_t wake, done;
#endif
#endif
} FaiPool;

typedef struct FaiWorker { FaiPool* pool; int index; } FaiWorker;

static void fai_part(FaiPool* p, int index, int parts, int64_t* from, int64_t* to) {
    int64_t step = p->total / parts, rest = p->total % parts;
    *from = index * step + (index < rest ? index : rest);
    *to = *from + step + (index < rest ? 1 : 0);
}

#ifdef FAI_THREADS
#ifdef _WIN32
static DWORD WINAPI fai_workerMain(LPVOID a)
#else
static void* fai_workerMain(void* a)
#endif
{
    /* (the block from fai_poolStart belongs to the worker: it keeps what it needs and frees it) */
    FaiPool* p = ((FaiWorker*)a)->pool;
    int index = ((FaiWorker*)a)->index;
    int seen = 0;
    free(a);
    for (;;) {
        int64_t from, to;
#ifdef _WIN32
        EnterCriticalSection(&p->lock);
        while (p->generation == seen && !p->quit) SleepConditionVariableCS(&p->wake, &p->lock, INFINITE);
        seen = p->generation;
        LeaveCriticalSection(&p->lock);
#else
        pthread_mutex_lock(&p->lock);
        while (p->generation == seen && !p->quit) pthread_cond_wait(&p->wake, &p->lock);
        seen = p->generation;
        pthread_mutex_unlock(&p->lock);
#endif
        if (p->quit) break;
        fai_part(p, index + 1, p->count + 1, &from, &to);
        if (from < to) p->job(p->arg, from, to);
#ifdef _WIN32
        EnterCriticalSection(&p->lock);
        if (--p->pending == 0) WakeConditionVariable(&p->done);
        LeaveCriticalSection(&p->lock);
#else
        pthread_mutex_lock(&p->lock);
        if (--p->pending == 0) pthread_cond_signal(&p->done);
        pthread_mutex_unlock(&p->lock);
#endif
    }
#ifdef _WIN32
    return 0;
#else
    return NULL;
#endif
}
#endif

static int fai_hardwareThreads(void) {
#if defined(FAI_THREADS) && defined(_WIN32)
    SYSTEM_INFO si; GetSystemInfo(&si); return (int)si.dwNumberOfProcessors;
#elif defined(FAI_THREADS)
    long n = sysconf(_SC_NPROCESSORS_ONLN); return n > 0 ? (int)n : 1;
#else
    return 1;
#endif
}

/* `threads` <= 0: one fewer than the machine has (the program that shows the answer, and the system, keep a core; 8 at most). */
static void fai_poolStart(FaiPool* p, int threads) {
    memset(p, 0, sizeof *p);
    if (threads <= 0) {
        threads = fai_hardwareThreads();
        if (threads > 2) threads--;
        if (threads > 8) threads = 8;
    }
    if (threads > FAI_MAX_THREADS) threads = FAI_MAX_THREADS;
#ifdef FAI_THREADS
    {
        int i;
#ifdef _WIN32
        InitializeCriticalSection(&p->lock); InitializeConditionVariable(&p->wake); InitializeConditionVariable(&p->done);
#else
        pthread_mutex_init(&p->lock, NULL); pthread_cond_init(&p->wake, NULL); pthread_cond_init(&p->done, NULL);
#endif
        for (i = 0; i < threads - 1; i++) {
            FaiWorker* w = (FaiWorker*)malloc(sizeof(FaiWorker));
            if (w == NULL) break;
            w->pool = p; w->index = p->count;
#ifdef _WIN32
            p->th[p->count] = CreateThread(NULL, 0, fai_workerMain, w, 0, NULL);
            if (p->th[p->count] == NULL) { free(w); break; }
#else
            if (pthread_create(&p->th[p->count], NULL, fai_workerMain, w) != 0) { free(w); break; }
#endif
            p->count++;
        }
    }
#else
    (void)threads;
#endif
}

static void fai_poolStop(FaiPool* p) {
#ifdef FAI_THREADS
    int i;
#ifdef _WIN32
    EnterCriticalSection(&p->lock); p->quit = 1; WakeAllConditionVariable(&p->wake); LeaveCriticalSection(&p->lock);
    for (i = 0; i < p->count; i++) { WaitForSingleObject(p->th[i], INFINITE); CloseHandle(p->th[i]); }
    DeleteCriticalSection(&p->lock);
#else
    pthread_mutex_lock(&p->lock); p->quit = 1; pthread_cond_broadcast(&p->wake); pthread_mutex_unlock(&p->lock);
    for (i = 0; i < p->count; i++) pthread_join(p->th[i], NULL);
    pthread_mutex_destroy(&p->lock); pthread_cond_destroy(&p->wake); pthread_cond_destroy(&p->done);
#endif
#endif
    p->count = 0;
}

/* job(arg, from, to) over 0..total, split between the threads and the caller. */
static void fai_run(FaiPool* p, int64_t total, FaiJob job, void* arg) {
    int64_t from, to;
    if (p->count == 0 || total < 64) { job(arg, 0, total); return; }
#ifdef FAI_THREADS
    p->job = job; p->arg = arg; p->total = total;
#ifdef _WIN32
    EnterCriticalSection(&p->lock); p->pending = p->count; p->generation++; WakeAllConditionVariable(&p->wake); LeaveCriticalSection(&p->lock);
#else
    pthread_mutex_lock(&p->lock); p->pending = p->count; p->generation++; pthread_cond_broadcast(&p->wake); pthread_mutex_unlock(&p->lock);
#endif
    fai_part(p, 0, p->count + 1, &from, &to);
    job(arg, from, to);
#ifdef _WIN32
    EnterCriticalSection(&p->lock); while (p->pending > 0) SleepConditionVariableCS(&p->done, &p->lock, INFINITE); LeaveCriticalSection(&p->lock);
#else
    pthread_mutex_lock(&p->lock); while (p->pending > 0) pthread_cond_wait(&p->done, &p->lock); pthread_mutex_unlock(&p->lock);
#endif
#endif
}

/* ---- the matrix product: out[r] = W[r] . x for the rows r (x already quantized when W is) -------------------------------------------------------- */
typedef struct FaiMatJob { const FaiTensor* w; const float* x; const FaiQ8* xq; float* out; const float* bias; int count; int64_t xStride, xqStride, outStride; } FaiMatJob;
static void fai_matJob(void* a, int64_t from, int64_t to) {
    FaiMatJob* j = (FaiMatJob*)a;
    const FaiTensor* w = j->w;
    int64_t n = w->ne[0], r;
    size_t rb = fai_rowBytes(w->type, n);
    int b;
    for (r = from; r < to; r++) {
        const uint8_t* row = w->data + rb * (size_t)r;
        float bias = j->bias ? j->bias[r] : 0.0f;
        for (b = 0; b < j->count; b++) {
            float v;
            switch (w->type) {
                case FAI_Q8_0: v = fai_dotQ8((const FaiQ8*)row, j->xq + b * j->xqStride, n / FAI_QK); break;
                case FAI_Q4_0: v = fai_dotQ4((const FaiQ4*)row, j->xq + b * j->xqStride, n / FAI_QK); break;
                case FAI_Q6_K: v = fai_dotQ6K((const FaiQ6K*)row, j->xq + b * j->xqStride, n / FAI_QKK); break;
                case FAI_Q4_1: v = fai_dotQ41((const FaiQ41*)row, j->xq + b * j->xqStride, n / FAI_QK); break;
                case FAI_Q4_K: case FAI_Q5_K: v = fai_dotK(w->type, row, j->xq + b * j->xqStride, n / FAI_QKK); break;
                case FAI_F16: v = fai_dotH(row, j->x + b * j->xStride, n); break;
                default: v = fai_dotF((const float*)row, j->x + b * j->xStride, n); break;
            }
            j->out[b * j->outStride + r] = v + bias;
        }
    }
}

/* ---- the GGUF file -------------------------------------------------------------------------------------------------------------------------------- */
enum { GG_U8 = 0, GG_I8, GG_U16, GG_I16, GG_U32, GG_I32, GG_F32, GG_BOOL, GG_STR, GG_ARR, GG_U64, GG_I64, GG_F64 };

typedef struct FaiFile {
    const uint8_t* base; size_t size;
#ifdef _WIN32
    HANDLE file, map;
#endif
    int mapped;
} FaiFile;

typedef struct FaiReader { const uint8_t* p; const uint8_t* end; int bad; } FaiReader;
static int fai_need(FaiReader* r, size_t n) { if (r->bad || (size_t)(r->end - r->p) < n) { r->bad = 1; return 0; } return 1; }
static uint32_t fai_u32(FaiReader* r) { uint32_t v = 0; if (fai_need(r, 4)) { memcpy(&v, r->p, 4); r->p += 4; } return v; }
static uint64_t fai_u64(FaiReader* r) { uint64_t v = 0; if (fai_need(r, 8)) { memcpy(&v, r->p, 8); r->p += 8; } return v; }
/* A string of the file: its bytes (not 0-terminated) and length. */
static const char* fai_str(FaiReader* r, uint64_t* len) {
    const char* s;
    *len = fai_u64(r);
    if (!fai_need(r, (size_t)*len)) { *len = 0; return ""; }
    s = (const char*)r->p; r->p += *len;
    return s;
}
static size_t fai_scalarSize(uint32_t t) {
    switch (t) {
        case GG_U8: case GG_I8: case GG_BOOL: return 1;
        case GG_U16: case GG_I16: return 2;
        case GG_U32: case GG_I32: case GG_F32: return 4;
        case GG_U64: case GG_I64: case GG_F64: return 8;
        default: return 0;
    }
}
static void fai_skipValue(FaiReader* r, uint32_t t) {
    if (t == GG_STR) { uint64_t n; fai_str(r, &n); }
    else if (t == GG_ARR) {
        uint32_t et = fai_u32(r);
        uint64_t n = fai_u64(r), i;
        if (et == GG_STR || et == GG_ARR) for (i = 0; i < n && !r->bad; i++) fai_skipValue(r, et);
        else if (fai_need(r, (size_t)(n * fai_scalarSize(et)))) r->p += n * fai_scalarSize(et);
    }
    else { size_t s = fai_scalarSize(t); if (s == 0) r->bad = 1; else if (fai_need(r, s)) r->p += s; }
}
static double fai_number(FaiReader* r, uint32_t t) {
    double v = 0;
    switch (t) {
        case GG_U8: if (fai_need(r, 1)) v = *r->p++; break;
        case GG_I8: if (fai_need(r, 1)) v = (int8_t)*r->p++; break;
        case GG_BOOL: if (fai_need(r, 1)) v = *r->p++ != 0; break;
        case GG_U16: { uint16_t x = 0; if (fai_need(r, 2)) { memcpy(&x, r->p, 2); r->p += 2; } v = x; break; }
        case GG_I16: { int16_t x = 0; if (fai_need(r, 2)) { memcpy(&x, r->p, 2); r->p += 2; } v = x; break; }
        case GG_U32: v = fai_u32(r); break;
        case GG_I32: v = (int32_t)fai_u32(r); break;
        case GG_F32: { float x = 0; if (fai_need(r, 4)) { memcpy(&x, r->p, 4); r->p += 4; } v = x; break; }
        case GG_U64: v = (double)fai_u64(r); break;
        case GG_I64: v = (double)(int64_t)fai_u64(r); break;
        case GG_F64: { double x = 0; if (fai_need(r, 8)) { memcpy(&x, r->p, 8); r->p += 8; } v = x; break; }
        default: fai_skipValue(r, t); break;
    }
    return v;
}

static int fai_open(FaiFile* f, const char* path, FaiError* e) {
    memset(f, 0, sizeof *f);
#if defined(_WIN32)
    {
        int n = MultiByteToWideChar(CP_UTF8, 0, path, -1, NULL, 0);
        wchar_t* w = (wchar_t*)malloc(sizeof(wchar_t) * (size_t)(n > 0 ? n : 1));
        LARGE_INTEGER size;
        if (w == NULL) return fai_fail(e, "Out of memory.", NULL);
        MultiByteToWideChar(CP_UTF8, 0, path, -1, w, n);
        f->file = CreateFileW(w, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
        free(w);
        if (f->file == INVALID_HANDLE_VALUE) return fai_fail(e, "The model file '%s' cannot be opened.", path);
        if (!GetFileSizeEx(f->file, &size)) { CloseHandle(f->file); return fai_fail(e, "The model file '%s' cannot be read.", path); }
        f->map = CreateFileMappingW(f->file, NULL, PAGE_READONLY, 0, 0, NULL);
        if (f->map == NULL) { CloseHandle(f->file); return fai_fail(e, "The model file '%s' cannot be mapped.", path); }
        f->base = (const uint8_t*)MapViewOfFile(f->map, FILE_MAP_READ, 0, 0, 0);
        if (f->base == NULL) { CloseHandle(f->map); CloseHandle(f->file); return fai_fail(e, "The model file '%s' cannot be mapped.", path); }
        f->size = (size_t)size.QuadPart;
        f->mapped = 1;
        return 1;
    }
#else
#ifdef FAI_MMAP
    {
        int fd = open(path, O_RDONLY);
        struct stat st;
        if (fd >= 0 && fstat(fd, &st) == 0 && st.st_size > 0) {
            void* m = mmap(NULL, (size_t)st.st_size, PROT_READ, MAP_PRIVATE, fd, 0);
            close(fd);
            if (m != MAP_FAILED) { f->base = (const uint8_t*)m; f->size = (size_t)st.st_size; f->mapped = 1; return 1; }
        }
        else if (fd >= 0) close(fd);
    }
#endif
    {
        FILE* fp = fopen(path, "rb");
        long n;
        uint8_t* data;
        if (fp == NULL) return fai_fail(e, "The model file '%s' cannot be opened.", path);
        fseek(fp, 0, SEEK_END); n = ftell(fp); fseek(fp, 0, SEEK_SET);
        if (n <= 0) { fclose(fp); return fai_fail(e, "The model file '%s' is empty.", path); }
        data = (uint8_t*)malloc((size_t)n);
        if (data == NULL) { fclose(fp); return fai_fail(e, "Out of memory: the model does not fit.", NULL); }
        if (fread(data, 1, (size_t)n, fp) != (size_t)n) { free(data); fclose(fp); return fai_fail(e, "The model file '%s' cannot be read.", path); }
        fclose(fp);
        f->base = data; f->size = (size_t)n;
        return 1;
    }
#endif
}
static void fai_close(FaiFile* f) {
    if (f->base == NULL) return;
#if defined(_WIN32)
    UnmapViewOfFile(f->base); CloseHandle(f->map); CloseHandle(f->file);
#else
#ifdef FAI_MMAP
    if (f->mapped) munmap((void*)f->base, f->size); else
#endif
    free((void*)f->base);
#endif
    f->base = NULL;
}

/* ---- the vocabulary ----------------------------------------------------------------------------------------------------------------------------- */
enum { FAI_TOK_NORMAL = 1, FAI_TOK_UNKNOWN = 2, FAI_TOK_CONTROL = 3, FAI_TOK_USER = 4, FAI_TOK_UNUSED = 5, FAI_TOK_BYTE = 6 };
enum { FAI_VOCAB_BPE = 0, FAI_VOCAB_SPM = 1 };

typedef struct FaiStr { const char* p; uint32_t n; } FaiStr;

/* A hash table from a byte string to a number. */
typedef struct FaiMap { uint32_t* slots; uint32_t cap; const FaiStr* keys; } FaiMap;   /* slots: index + 1, 0 empty */
static uint32_t fai_hash(const char* p, size_t n) { uint32_t h = 2166136261u; size_t i; for (i = 0; i < n; i++) { h ^= (uint8_t)p[i]; h *= 16777619u; } return h; }
static int fai_mapBuild(FaiMap* m, const FaiStr* keys, uint32_t count) {
    uint32_t cap = 1, i;
    while (cap < count * 2 + 1) cap <<= 1;
    m->slots = (uint32_t*)calloc(cap, sizeof(uint32_t));
    if (m->slots == NULL) return 0;
    m->cap = cap; m->keys = keys;
    for (i = 0; i < count; i++) {
        uint32_t s = fai_hash(keys[i].p, keys[i].n) & (cap - 1);
        while (m->slots[s] != 0) {
            const FaiStr* k = &keys[m->slots[s] - 1];
            if (k->n == keys[i].n && memcmp(k->p, keys[i].p, k->n) == 0) break;   /* the first one stays */
            s = (s + 1) & (cap - 1);
        }
        if (m->slots[s] == 0) m->slots[s] = i + 1;
    }
    return 1;
}
static int64_t fai_mapFind(const FaiMap* m, const char* p, size_t n) {
    uint32_t s;
    if (m->slots == NULL) return -1;
    s = fai_hash(p, n) & (m->cap - 1);
    while (m->slots[s] != 0) {
        const FaiStr* k = &m->keys[m->slots[s] - 1];
        if (k->n == n && memcmp(k->p, p, n) == 0) return (int64_t)m->slots[s] - 1;
        s = (s + 1) & (m->cap - 1);
    }
    return -1;
}

typedef struct FaiVocab {
    int kind;
    uint32_t count;
    FaiStr* tokens;          /* in the file */
    int32_t* types;
    float* scores;           /* SPM */
    FaiMap tokenIds;
    uint32_t mergeCount;
    FaiStr* merges;          /* "a b" in the file */
    FaiMap mergeRanks;
    int32_t bos, eos, eot, unk, pad;
    int addBos;
    int32_t* special;        /* the control and user tokens, longest first (they are matched in the text) */
    uint32_t specialCount;
    int32_t byteTokens[256]; /* SPM: <0xNN>; BPE: the token of each byte's character */
} FaiVocab;

/* ---- the model ---------------------------------------------------------------------------------------------------------------------------------- */
enum { FAI_ARCH_QWEN2 = 0, FAI_ARCH_LLAMA, FAI_ARCH_QWEN3, FAI_ARCH_QWEN35, FAI_ARCH_LFM3 };
/* What a layer mixes the tokens with: attention over all positions, attention over the last `swa` positions, a gated delta net (a state per head that is
 * updated by each token: Qwen 3.5) or a short convolution over the last few tokens (LFM). */
enum { FAI_MIX_ATTN = 0, FAI_MIX_SWA, FAI_MIX_DELTA, FAI_MIX_CONV };
#define FAI_MAX_LAYERS 256

typedef struct FaiLayer {
    int mix;
    int nHeadKv;
    const FaiTensor *attnNorm, *wq, *wk, *wv, *wo, *bq, *bk, *bv, *qNorm, *kNorm, *attnGate;
    const FaiTensor *postAttnNorm, *ffnNorm, *postFfnNorm, *gate, *up, *down;
    /* the delta net */
    const FaiTensor *ssmQkv, *ssmConv, *ssmDt, *ssmA, *ssmBeta, *ssmAlpha, *ssmNorm, *ssmOut;
    /* the short convolution */
    const FaiTensor *convW, *convIn, *convOut;
} FaiLayer;

typedef struct FaiModel {
    FaiFile file;
    char arch[32], name[128], pre[32];
    int archId;
    char* chatTemplate;
    int nLayer, nEmbd, nHead, nHeadKv, nFf, headDim, nRot, ctxTrain;
    int headKvOf[FAI_MAX_LAYERS]; int headKvArray;
    int swaOf[FAI_MAX_LAYERS]; int swaArray, swa;
    float eps, ropeBase, ropeBaseSwa;
    int ropeNeox;              /* the halves of a head are rotated together (qwen, lfm); llama: pairs of neighbours */
    /* the delta net (qwen35): conv kernel, the size of a head (k and v), the heads of k (groups) and of v, the inner size; every `fullEvery`th layer attends */
    int ssmConvK, ssmState, ssmGroups, ssmVHeads, ssmInner, fullEvery;
    int convL;                 /* lfm: the length of the short convolution */
    uint64_t tensorCount;
    FaiTensor* tensors;
    const FaiTensor *tokEmbd, *outNorm, *output, *ropeFreqs;
    FaiLayer* layers;
    FaiVocab vocab;
} FaiModel;

static const FaiTensor* fai_tensor(const FaiModel* m, const char* name) {
    uint64_t i;
    for (i = 0; i < m->tensorCount; i++) if (strcmp(m->tensors[i].name, name) == 0) return &m->tensors[i];
    return NULL;
}
static int fai_keyIs(const char* k, uint64_t kn, const char* prefix, const char* rest) {
    size_t a = strlen(prefix), b = strlen(rest);
    return kn == a + b && memcmp(k, prefix, a) == 0 && memcmp(k + a, rest, b) == 0;
}
static int fai_keyEq(const char* k, uint64_t kn, const char* s) { return kn == strlen(s) && memcmp(k, s, kn) == 0; }

static int fai_cmpSpecial(const void* a, const void* b, const FaiVocab* v) {
    int32_t x = *(const int32_t*)a, y = *(const int32_t*)b;
    return (int)v->tokens[y].n - (int)v->tokens[x].n;
}
static FaiVocab* g_faiSortVocab;
static int fai_cmpSpecialQ(const void* a, const void* b) { return fai_cmpSpecial(a, b, g_faiSortVocab); }

static void fai_modelFree(FaiModel* m) {
    uint64_t i;
    if (m == NULL) return;
    if (m->tensors) for (i = 0; i < m->tensorCount; i++) free(m->tensors[i].owned);
    free(m->tensors); free(m->layers); free(m->chatTemplate);
    free(m->vocab.tokens); free(m->vocab.types); free(m->vocab.scores); free(m->vocab.merges); free(m->vocab.special);
    free(m->vocab.tokenIds.slots); free(m->vocab.mergeRanks.slots);
    fai_close(&m->file);
    free(m);
}

/* The model in the file `path`, or NULL (the reason in `e`). */
static FaiModel* fai_modelLoad(const char* path, FaiError* e) {
    FaiModel* m = (FaiModel*)calloc(1, sizeof(FaiModel));
    FaiReader r;
    uint32_t version;
    uint64_t kvCount, i, alignment = 32;
    char key[160];
    size_t dataStart;
    if (m == NULL) { fai_fail(e, "Out of memory.", NULL); return NULL; }
    if (g_faiHalf == NULL) fai_kernels(1);
    m->vocab.bos = m->vocab.eos = m->vocab.eot = m->vocab.unk = m->vocab.pad = -1;
    m->ropeBase = 10000.0f; m->eps = 1e-5f;
    if (!fai_open(&m->file, path, e)) { free(m); return NULL; }
    r.p = m->file.base; r.end = m->file.base + m->file.size; r.bad = 0;
    if (m->file.size < 24 || memcmp(r.p, "GGUF", 4) != 0) { fai_fail(e, "'%s' is not a GGUF model file.", path); fai_modelFree(m); return NULL; }
    r.p += 4;
    version = fai_u32(&r);
    if (version < 2 || version > 3) { fai_fail(e, "The GGUF version of '%s' is not supported (2 and 3 are).", path); fai_modelFree(m); return NULL; }
    m->tensorCount = fai_u64(&r);
    kvCount = fai_u64(&r);
    for (i = 0; i < kvCount && !r.bad; i++) {
        uint64_t kn, j;
        const char* k = fai_str(&r, &kn);
        uint32_t t = fai_u32(&r);
        const char* arch = m->arch;
        if (fai_keyEq(k, kn, "general.architecture") && t == GG_STR) { uint64_t n; const char* s = fai_str(&r, &n); if (n >= sizeof m->arch) n = sizeof m->arch - 1; memcpy(m->arch, s, n); m->arch[n] = 0; continue; }
        if (fai_keyEq(k, kn, "general.name") && t == GG_STR) { uint64_t n; const char* s = fai_str(&r, &n); if (n >= sizeof m->name) n = sizeof m->name - 1; memcpy(m->name, s, n); m->name[n] = 0; continue; }
        if (fai_keyEq(k, kn, "general.alignment")) { alignment = (uint64_t)fai_number(&r, t); continue; }
        if (fai_keyEq(k, kn, "tokenizer.chat_template") && t == GG_STR) {
            uint64_t n; const char* s = fai_str(&r, &n);
            m->chatTemplate = (char*)malloc((size_t)n + 1);
            if (m->chatTemplate) { memcpy(m->chatTemplate, s, (size_t)n); m->chatTemplate[n] = 0; }
            continue;
        }
        if (fai_keyEq(k, kn, "tokenizer.ggml.model") && t == GG_STR) { uint64_t n; const char* s = fai_str(&r, &n); m->vocab.kind = (n == 5 && memcmp(s, "llama", 5) == 0) ? FAI_VOCAB_SPM : FAI_VOCAB_BPE; continue; }
        if (fai_keyEq(k, kn, "tokenizer.ggml.tokens") && t == GG_ARR) {
            uint32_t et = fai_u32(&r);
            uint64_t n = fai_u64(&r);
            if (et != GG_STR) { r.bad = 1; break; }
            m->vocab.count = (uint32_t)n;
            m->vocab.tokens = (FaiStr*)calloc((size_t)n + 1, sizeof(FaiStr));
            if (m->vocab.tokens == NULL) { fai_fail(e, "Out of memory.", NULL); fai_modelFree(m); return NULL; }
            for (j = 0; j < n && !r.bad; j++) { uint64_t sn; m->vocab.tokens[j].p = fai_str(&r, &sn); m->vocab.tokens[j].n = (uint32_t)sn; }
            continue;
        }
        if (fai_keyEq(k, kn, "tokenizer.ggml.merges") && t == GG_ARR) {
            uint32_t et = fai_u32(&r);
            uint64_t n = fai_u64(&r);
            if (et != GG_STR) { r.bad = 1; break; }
            m->vocab.mergeCount = (uint32_t)n;
            m->vocab.merges = (FaiStr*)calloc((size_t)n + 1, sizeof(FaiStr));
            if (m->vocab.merges == NULL) { fai_fail(e, "Out of memory.", NULL); fai_modelFree(m); return NULL; }
            for (j = 0; j < n && !r.bad; j++) { uint64_t sn; m->vocab.merges[j].p = fai_str(&r, &sn); m->vocab.merges[j].n = (uint32_t)sn; }
            continue;
        }
        if ((fai_keyEq(k, kn, "tokenizer.ggml.token_type") || fai_keyEq(k, kn, "tokenizer.ggml.scores")) && t == GG_ARR) {
            int types = k[kn - 1] == 'e';   /* token_type */
            uint32_t et = fai_u32(&r);
            uint64_t n = fai_u64(&r);
            if (types) { m->vocab.types = (int32_t*)calloc((size_t)n + 1, sizeof(int32_t)); }
            else { m->vocab.scores = (float*)calloc((size_t)n + 1, sizeof(float)); }
            if ((types && m->vocab.types == NULL) || (!types && m->vocab.scores == NULL)) { fai_fail(e, "Out of memory.", NULL); fai_modelFree(m); return NULL; }
            for (j = 0; j < n && !r.bad; j++) {
                double v = fai_number(&r, et);
                if (types) m->vocab.types[j] = (int32_t)v; else m->vocab.scores[j] = (float)v;
            }
            continue;
        }
        if (fai_keyEq(k, kn, "tokenizer.ggml.bos_token_id")) { m->vocab.bos = (int32_t)fai_number(&r, t); continue; }
        if (fai_keyEq(k, kn, "tokenizer.ggml.eos_token_id")) { m->vocab.eos = (int32_t)fai_number(&r, t); continue; }
        if (fai_keyEq(k, kn, "tokenizer.ggml.eot_token_id")) { m->vocab.eot = (int32_t)fai_number(&r, t); continue; }
        if (fai_keyEq(k, kn, "tokenizer.ggml.unknown_token_id")) { m->vocab.unk = (int32_t)fai_number(&r, t); continue; }
        if (fai_keyEq(k, kn, "tokenizer.ggml.padding_token_id")) { m->vocab.pad = (int32_t)fai_number(&r, t); continue; }
        if (fai_keyEq(k, kn, "tokenizer.ggml.add_bos_token")) { m->vocab.addBos = fai_number(&r, t) != 0; continue; }
        if (fai_keyEq(k, kn, "tokenizer.ggml.pre") && t == GG_STR) { uint64_t n; const char* s = fai_str(&r, &n); if (n >= sizeof m->pre) n = sizeof m->pre - 1; memcpy(m->pre, s, n); m->pre[n] = 0; continue; }
        if (arch[0] && kn > strlen(arch) && memcmp(k, arch, strlen(arch)) == 0 && t == GG_ARR &&
            (fai_keyIs(k, kn, arch, ".attention.head_count_kv") || fai_keyIs(k, kn, arch, ".attention.sliding_window_pattern"))) {
            /* a value per layer */
            int kvs = fai_keyIs(k, kn, arch, ".attention.head_count_kv");
            uint32_t et = fai_u32(&r);
            uint64_t n = fai_u64(&r);
            for (j = 0; j < n && !r.bad; j++) {
                int v = (int)fai_number(&r, et);
                if (j < FAI_MAX_LAYERS) { if (kvs) m->headKvOf[j] = v; else m->swaOf[j] = v; }
            }
            if (kvs) m->headKvArray = 1; else m->swaArray = 1;
            continue;
        }
        if (arch[0] && kn > strlen(arch) && memcmp(k, arch, strlen(arch)) == 0 && t != GG_ARR && t != GG_STR) {
            double v = fai_number(&r, t);
            if (fai_keyIs(k, kn, arch, ".block_count")) m->nLayer = (int)v;
            else if (fai_keyIs(k, kn, arch, ".embedding_length")) m->nEmbd = (int)v;
            else if (fai_keyIs(k, kn, arch, ".feed_forward_length")) m->nFf = (int)v;
            else if (fai_keyIs(k, kn, arch, ".attention.head_count")) m->nHead = (int)v;
            else if (fai_keyIs(k, kn, arch, ".attention.head_count_kv")) m->nHeadKv = (int)v;
            else if (fai_keyIs(k, kn, arch, ".attention.key_length")) m->headDim = (int)v;
            else if (fai_keyIs(k, kn, arch, ".context_length")) m->ctxTrain = (int)v;
            else if (fai_keyIs(k, kn, arch, ".rope.freq_base")) m->ropeBase = (float)v;
            else if (fai_keyIs(k, kn, arch, ".rope.freq_base_swa")) m->ropeBaseSwa = (float)v;
            else if (fai_keyIs(k, kn, arch, ".attention.layer_norm_rms_epsilon")) m->eps = (float)v;
            else if (fai_keyIs(k, kn, arch, ".rope.dimension_count")) m->nRot = (int)v;
            else if (fai_keyIs(k, kn, arch, ".attention.sliding_window")) m->swa = (int)v;
            else if (fai_keyIs(k, kn, arch, ".ssm.conv_kernel")) m->ssmConvK = (int)v;
            else if (fai_keyIs(k, kn, arch, ".ssm.state_size")) m->ssmState = (int)v;
            else if (fai_keyIs(k, kn, arch, ".ssm.group_count")) m->ssmGroups = (int)v;
            else if (fai_keyIs(k, kn, arch, ".ssm.time_step_rank")) m->ssmVHeads = (int)v;
            else if (fai_keyIs(k, kn, arch, ".ssm.inner_size")) m->ssmInner = (int)v;
            else if (fai_keyIs(k, kn, arch, ".full_attention_interval")) m->fullEvery = (int)v;
            else if (fai_keyIs(k, kn, arch, ".shortconv.l_cache")) m->convL = (int)v;
            continue;
        }
        (void)key;
        fai_skipValue(&r, t);
    }
    if (r.bad) { fai_fail(e, "The model file '%s' is broken (its header).", path); fai_modelFree(m); return NULL; }
    m->ropeNeox = 1;
    if (strcmp(m->arch, "qwen2") == 0) m->archId = FAI_ARCH_QWEN2;
    else if (strcmp(m->arch, "llama") == 0) { m->archId = FAI_ARCH_LLAMA; m->ropeNeox = 0; }
    else if (strcmp(m->arch, "qwen3") == 0) m->archId = FAI_ARCH_QWEN3;
    else if (strcmp(m->arch, "qwen35") == 0) m->archId = FAI_ARCH_QWEN35;
    else if (strcmp(m->arch, "lfm3") == 0) m->archId = FAI_ARCH_LFM3;
    else { fai_fail(e, "The architecture '%s' is not supported (qwen2, qwen3, qwen35, llama and lfm3 are).", m->arch); fai_modelFree(m); return NULL; }
    if (m->nLayer > FAI_MAX_LAYERS) { fai_fail(e, "The model file '%s' has too many layers.", path); fai_modelFree(m); return NULL; }
    if (m->nHeadKv == 0 && !m->headKvArray) m->nHeadKv = m->nHead;
    if (m->headDim == 0 && m->nHead > 0) m->headDim = m->nEmbd / m->nHead;
    if (m->nRot == 0 || m->nRot > m->headDim) m->nRot = m->headDim;
    if (m->ropeBaseSwa == 0) m->ropeBaseSwa = m->ropeBase;
    if (m->fullEvery <= 0) m->fullEvery = 4;
    if (m->nLayer <= 0 || m->nEmbd <= 0 || m->nHead <= 0 || m->nFf <= 0 || m->headDim <= 0 || (m->headDim & 1)) {
        fai_fail(e, "The model file '%s' lacks the sizes of the model.", path); fai_modelFree(m); return NULL;
    }
    if (m->vocab.tokens == NULL) { fai_fail(e, "The model file '%s' has no tokenizer.", path); fai_modelFree(m); return NULL; }

    /* the tensors */
    m->tensors = (FaiTensor*)calloc((size_t)m->tensorCount + 1, sizeof(FaiTensor));
    if (m->tensors == NULL) { fai_fail(e, "Out of memory.", NULL); fai_modelFree(m); return NULL; }
    for (i = 0; i < m->tensorCount && !r.bad; i++) {
        FaiTensor* t = &m->tensors[i];
        uint64_t nn, d;
        const char* name = fai_str(&r, &nn);
        uint32_t dims = fai_u32(&r);
        if (nn >= sizeof t->name) nn = sizeof t->name - 1;
        memcpy(t->name, name, (size_t)nn); t->name[nn] = 0;
        t->ne[0] = t->ne[1] = t->ne[2] = t->ne[3] = 1;
        if (dims > 4) { r.bad = 1; break; }
        for (d = 0; d < dims; d++) t->ne[d] = (int64_t)fai_u64(&r);
        t->type = (int)fai_u32(&r);
        t->offset = fai_u64(&r);
    }
    if (r.bad) { fai_fail(e, "The model file '%s' is broken (its tensors).", path); fai_modelFree(m); return NULL; }
    if (alignment == 0) alignment = 32;
    dataStart = (size_t)(r.p - m->file.base);
    dataStart = (dataStart + alignment - 1) / alignment * alignment;
    for (i = 0; i < m->tensorCount; i++) {
        FaiTensor* t = &m->tensors[i];
        size_t bytes = fai_rowBytes(t->type, t->ne[0]) * (size_t)(t->ne[1] * t->ne[2] * t->ne[3]);
        if (fai_rowBytes(t->type, FAI_QKK) == 0) {
            char text[160];
            snprintf(text, sizeof text, "%s (type %d)", t->name, t->type);
            fai_fail(e, "The weights %s are of a kind that is not supported (F32, F16, Q8_0, Q4_0, Q4_1, Q4_K, Q5_K and Q6_K are: take a Q8_0, Q4_0 or Q4_K_M file).", text);
            fai_modelFree(m); return NULL;
        }
        if (t->type != FAI_F32 && t->type != FAI_F16 && t->ne[0] % (t->type >= FAI_Q4_K ? FAI_QKK : FAI_QK) != 0) { fai_fail(e, "The tensor %s has an odd size.", t->name); fai_modelFree(m); return NULL; }
        if (dataStart + t->offset + bytes > m->file.size) { fai_fail(e, "The model file '%s' is cut off.", path); fai_modelFree(m); return NULL; }
        t->data = m->file.base + dataStart + t->offset;
    }
    /* weights of the other quantizations (Q4_1, Q4_K, Q5_K, Q6_K: in the mixed files) become Q8_0 once: the fast products are those of Q8_0 and Q4_0 */
    for (i = 0; i < m->tensorCount; i++) {
        FaiTensor* t = &m->tensors[i];
        int64_t rows = t->ne[1] * t->ne[2] * t->ne[3], r;
        size_t rb;
        float* tmp;
        if (t->type != FAI_Q4_1 && t->type != FAI_Q4_K && t->type != FAI_Q5_K && t->type != FAI_Q6_K) continue;
        rb = fai_rowBytes(FAI_Q8_0, t->ne[0]);
        t->owned = (uint8_t*)malloc(rb * (size_t)rows);
        tmp = (float*)malloc((size_t)t->ne[0] * sizeof(float));
        if (t->owned == NULL || tmp == NULL) { free(tmp); fai_fail(e, "Out of memory: the model does not fit.", NULL); fai_modelFree(m); return NULL; }
        for (r = 0; r < rows; r++) { fai_dequantRow(t, r, tmp); fai_quantQ8(tmp, t->ne[0], (FaiQ8*)(t->owned + rb * (size_t)r)); }
        free(tmp);
        t->data = t->owned;
        t->type = FAI_Q8_0;
    }
    m->tokEmbd = fai_tensor(m, "token_embd.weight");
    m->outNorm = fai_tensor(m, "output_norm.weight");
    m->output = fai_tensor(m, "output.weight");
    m->ropeFreqs = fai_tensor(m, "rope_freqs.weight");
    if (m->output == NULL) m->output = m->tokEmbd;   /* tied */
    m->layers = (FaiLayer*)calloc((size_t)m->nLayer, sizeof(FaiLayer));
    if (m->layers == NULL || m->tokEmbd == NULL || m->outNorm == NULL) { fai_fail(e, "The model file '%s' lacks weights.", path); fai_modelFree(m); return NULL; }
    for (i = 0; i < (uint64_t)m->nLayer; i++) {
        FaiLayer* L = &m->layers[i];
        char n[96];
        int arch = m->archId;
        L->nHeadKv = m->headKvArray ? m->headKvOf[i] : m->nHeadKv;
        if (arch == FAI_ARCH_QWEN35) L->mix = (i + 1) % (uint64_t)m->fullEvery != 0 ? FAI_MIX_DELTA : FAI_MIX_ATTN;
        else if (arch == FAI_ARCH_LFM3) L->mix = L->nHeadKv == 0 ? FAI_MIX_CONV : (m->swaArray && m->swaOf[i] ? FAI_MIX_SWA : FAI_MIX_ATTN);
        else L->mix = FAI_MIX_ATTN;
#define FAI_T(field, part, need) snprintf(n, sizeof n, "blk.%d.%s", (int)i, part); L->field = fai_tensor(m, n); if ((need) && L->field == NULL) { fai_fail(e, "The model lacks the weights %s.", n); fai_modelFree(m); return NULL; }
        FAI_T(attnNorm, "attn_norm.weight", 1)
        FAI_T(gate, "ffn_gate.weight", 1) FAI_T(up, "ffn_up.weight", 1) FAI_T(down, "ffn_down.weight", 1)
        if (arch == FAI_ARCH_QWEN35) { FAI_T(ffnNorm, "post_attention_norm.weight", 1) }   /* (named post attention, it is the norm before the feed forward) */
        else { FAI_T(ffnNorm, "ffn_norm.weight", 1) }
        if (arch == FAI_ARCH_LFM3) { FAI_T(postAttnNorm, "post_attention_norm.weight", 0) FAI_T(postFfnNorm, "post_ffw_norm.weight", 0) }
        if (L->mix == FAI_MIX_ATTN || L->mix == FAI_MIX_SWA) {
            FAI_T(wq, "attn_q.weight", 1) FAI_T(wk, "attn_k.weight", 1) FAI_T(wv, "attn_v.weight", 1) FAI_T(wo, "attn_output.weight", 1)
            FAI_T(bq, "attn_q.bias", 0) FAI_T(bk, "attn_k.bias", 0) FAI_T(bv, "attn_v.bias", 0)
            FAI_T(qNorm, "attn_q_norm.weight", arch == FAI_ARCH_QWEN3 || arch == FAI_ARCH_QWEN35 || arch == FAI_ARCH_LFM3)
            FAI_T(kNorm, "attn_k_norm.weight", arch == FAI_ARCH_QWEN3 || arch == FAI_ARCH_QWEN35 || arch == FAI_ARCH_LFM3)
            if (arch == FAI_ARCH_LFM3) { FAI_T(attnGate, "attn_gate.weight", 1) }
            if (L->nHeadKv <= 0 || m->nHead % L->nHeadKv != 0) { fai_fail(e, "The model file '%s' has an odd number of heads.", path); fai_modelFree(m); return NULL; }
        }
        else if (L->mix == FAI_MIX_DELTA) {
            FAI_T(ssmQkv, "attn_qkv.weight", 1) FAI_T(attnGate, "attn_gate.weight", 1) FAI_T(ssmConv, "ssm_conv1d.weight", 1) FAI_T(ssmDt, "ssm_dt.bias", 1)
            FAI_T(ssmA, "ssm_a", 1) FAI_T(ssmBeta, "ssm_beta.weight", 1) FAI_T(ssmAlpha, "ssm_alpha.weight", 1) FAI_T(ssmNorm, "ssm_norm.weight", 1) FAI_T(ssmOut, "ssm_out.weight", 1)
            if (m->ssmState <= 0 || m->ssmGroups <= 0 || m->ssmVHeads <= 0 || m->ssmVHeads % m->ssmGroups != 0 || m->ssmConvK < 2 || m->ssmInner != m->ssmState * m->ssmVHeads ||
                L->ssmConv->type != FAI_F32 || L->ssmConv->ne[0] != m->ssmConvK) {
                fai_fail(e, "The model file '%s' has a delta net of sizes that are not supported.", path); fai_modelFree(m); return NULL;
            }
        }
        else {
            FAI_T(convW, "shortconv.conv.weight", 1) FAI_T(convIn, "shortconv.in_proj.weight", 1) FAI_T(convOut, "shortconv.out_proj.weight", 1)
            if (m->convL < 2 || L->convW->type != FAI_F32 || L->convW->ne[0] != m->convL) { fai_fail(e, "The model file '%s' has a convolution that is not supported.", path); fai_modelFree(m); return NULL; }
        }
#undef FAI_T
    }

    /* the vocabulary */
    {
        FaiVocab* v = &m->vocab;
        uint32_t s = 0, k;
        if (!fai_mapBuild(&v->tokenIds, v->tokens, v->count)) { fai_fail(e, "Out of memory.", NULL); fai_modelFree(m); return NULL; }
        if (v->mergeCount > 0 && !fai_mapBuild(&v->mergeRanks, v->merges, v->mergeCount)) { fai_fail(e, "Out of memory.", NULL); fai_modelFree(m); return NULL; }
        v->special = (int32_t*)calloc((size_t)v->count + 1, sizeof(int32_t));
        if (v->special == NULL) { fai_fail(e, "Out of memory.", NULL); fai_modelFree(m); return NULL; }
        for (k = 0; k < v->count; k++) {
            int32_t ty = v->types ? v->types[k] : FAI_TOK_NORMAL;
            if ((ty == FAI_TOK_CONTROL || ty == FAI_TOK_USER) && v->tokens[k].n > 0) v->special[s++] = (int32_t)k;
        }
        v->specialCount = s;
        g_faiSortVocab = v;
        qsort(v->special, s, sizeof(int32_t), fai_cmpSpecialQ);
        for (k = 0; k < 256; k++) v->byteTokens[k] = -1;
        if (v->kind == FAI_VOCAB_SPM) {
            for (k = 0; k < v->count; k++) {
                const FaiStr* t = &v->tokens[k];
                if (t->n == 6 && t->p[0] == '<' && t->p[1] == '0' && t->p[2] == 'x' && t->p[5] == '>') {
                    unsigned b = 0;
                    if (sscanf(t->p + 3, "%2x", &b) == 1) v->byteTokens[b & 255] = (int32_t)k;
                }
            }
        }
        if (v->eot < 0) {   /* the end of a turn of ChatML */
            int64_t id = fai_mapFind(&v->tokenIds, "<|im_end|>", 10);
            if (id >= 0) v->eot = (int32_t)id;
        }
    }
    return m;
}

/* ---- the byte-level BPE (gpt2): every byte is a character, the printable ones themselves, the others moved past 255 ----------------------------- */
static uint32_t g_faiByteChar[256];
static int16_t g_faiCharByte[512];   /* the inverse for the characters below 512 */
static int g_faiBytesReady;
static void fai_bytesInit(void) {
    int b, n = 0;
    if (g_faiBytesReady) return;
    for (b = 0; b < 512; b++) g_faiCharByte[b] = -1;
    for (b = 0; b < 256; b++) {
        int printable = (b >= '!' && b <= '~') || (b >= 0xA1 && b <= 0xAC) || (b >= 0xAE && b <= 0xFF);
        g_faiByteChar[b] = printable ? (uint32_t)b : (uint32_t)(256 + n++);
        g_faiCharByte[g_faiByteChar[b]] = (int16_t)b;
    }
    g_faiBytesReady = 1;
}
static size_t fai_utf8Put(char* out, uint32_t c) {
    if (c < 0x80) { out[0] = (char)c; return 1; }
    if (c < 0x800) { out[0] = (char)(0xC0 | (c >> 6)); out[1] = (char)(0x80 | (c & 0x3F)); return 2; }
    if (c < 0x10000) { out[0] = (char)(0xE0 | (c >> 12)); out[1] = (char)(0x80 | ((c >> 6) & 0x3F)); out[2] = (char)(0x80 | (c & 0x3F)); return 3; }
    out[0] = (char)(0xF0 | (c >> 18)); out[1] = (char)(0x80 | ((c >> 12) & 0x3F)); out[2] = (char)(0x80 | ((c >> 6) & 0x3F)); out[3] = (char)(0x80 | (c & 0x3F)); return 4;
}
/* The code point at p (n bytes left) and its length in bytes (a broken sequence: the byte itself as one). */
static uint32_t fai_utf8Get(const uint8_t* p, size_t n, size_t* len) {
    uint32_t c = p[0];
    if (c < 0x80 || n < 2) { *len = 1; return c; }
    if ((c & 0xE0) == 0xC0 && (p[1] & 0xC0) == 0x80) { *len = 2; return ((c & 0x1F) << 6) | (p[1] & 0x3F); }
    if ((c & 0xF0) == 0xE0 && n >= 3 && (p[1] & 0xC0) == 0x80 && (p[2] & 0xC0) == 0x80) { *len = 3; return ((c & 0x0F) << 12) | ((uint32_t)(p[1] & 0x3F) << 6) | (p[2] & 0x3F); }
    if ((c & 0xF8) == 0xF0 && n >= 4 && (p[1] & 0xC0) == 0x80 && (p[2] & 0xC0) == 0x80 && (p[3] & 0xC0) == 0x80) {
        *len = 4; return ((c & 0x07) << 18) | ((uint32_t)(p[1] & 0x3F) << 12) | ((uint32_t)(p[2] & 0x3F) << 6) | (p[3] & 0x3F);
    }
    *len = 1; return c;
}

/* The classes of the pre-tokenizer (\p{L}, \p{N}, \s), close enough for the scripts in use: letters are what is not a digit, space or punctuation. */
static int fai_isSpace(uint32_t c) { return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == 0x0B || c == 0x0C || c == 0x85 || c == 0xA0 || c == 0x1680 || (c >= 0x2000 && c <= 0x200A) || c == 0x2028 || c == 0x2029 || c == 0x202F || c == 0x205F || c == 0x3000; }
static int fai_isDigit(uint32_t c) {
    return (c >= '0' && c <= '9') || (c >= 0x0660 && c <= 0x0669) || (c >= 0x06F0 && c <= 0x06F9) || (c >= 0x0966 && c <= 0x096F) || (c >= 0xFF10 && c <= 0xFF19) ||
           c == 0xB2 || c == 0xB3 || c == 0xB9 || (c >= 0xBC && c <= 0xBE) || (c >= 0x2070 && c <= 0x2079 && c != 0x2071 && c != 0x2072 && c != 0x2073) || (c >= 0x2080 && c <= 0x2089) ||
           (c >= 0x2150 && c <= 0x2189) || (c >= 0x2460 && c <= 0x249B) || (c >= 0x2776 && c <= 0x2793) || (c >= 0x3007 && c == 0x3007);
}
static int g_faiMarksAreLetters;   /* qwen35: [\\p{L}\\p{M}]+ (set by the tokenizer before it splits) */
static int fai_isLetter(uint32_t c) {
    if (c < 0x80) return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
    if (fai_isSpace(c) || fai_isDigit(c)) return 0;
    if (c < 0xC0) return c == 0xAA || c == 0xB5 || c == 0xBA;
    if (c == 0xD7 || c == 0xF7) return 0;
    if (c >= 0x2000 && c <= 0x2BFF) return (c >= 0x2C00) || (c >= 0x2090 && c <= 0x209C) || c == 0x2071 || c == 0x207F || c == 0x2102 || c == 0x2107 || (c >= 0x210A && c <= 0x2113) || c == 0x2115 || (c >= 0x2119 && c <= 0x211D) || c == 0x2124 || c == 0x2126 || c == 0x2128 || (c >= 0x212A && c <= 0x212D) || (c >= 0x212F && c <= 0x2139);
    if (c >= 0x3000 && c <= 0x303F) return c == 0x3005 || c == 0x3006 || (c >= 0x3031 && c <= 0x3035) || c == 0x303B || c == 0x303C;
    if (c >= 0xFF00 && c <= 0xFF20) return 0;
    if ((c >= 0xFF3B && c <= 0xFF40) || (c >= 0xFF5B && c <= 0xFF65)) return 0;
    if ((c >= 0x0300 && c <= 0x036F) || (c >= 0x0483 && c <= 0x0489)) return g_faiMarksAreLetters;   /* combining marks (\p{M}) */
    if (c >= 0x1F000 && c <= 0x1FFFF) return 0;   /* emoji and symbols */
    if ((c >= 0xFE00 && c <= 0xFE0F) || c == 0x200D) return g_faiMarksAreLetters && c != 0x200D;
    if (c == 0x037E || c == 0x0387 || (c >= 0x055A && c <= 0x055F) || c == 0x0589 || c == 0x05BE || c == 0x05C0 || c == 0x05C3 || c == 0x05C6 || c == 0x05F3 || c == 0x05F4 ||
        c == 0x060C || c == 0x061B || c == 0x061F || c == 0x06D4 || c == 0x0964 || c == 0x0965 || c == 0x0E4F || c == 0x0E5A || c == 0x0E5B) return 0;
    return 1;
}

/* The pieces of the qwen2 / llama 3 pre-tokenizer:
 *   (?i:'s|'t|'re|'ve|'m|'ll|'d) | [^\r\n\p{L}\p{N}]?\p{L}+ | \p{N} | ?[^\s\p{L}\p{N}]+[\r\n]* | \s*[\r\n]+ | \s+(?!\S) | \s+
 * (llama 3 takes up to three digits in a row for \p{N}: `digits`). Calls piece(arg, start, end) for each. */
typedef void (*FaiPieceFn)(void* arg, size_t from, size_t to);
static void fai_pretokenize(const uint8_t* s, size_t n, int digits, FaiPieceFn piece, void* arg) {
    size_t i = 0;
    while (i < n) {
        size_t l0, l1 = 0, j;
        uint32_t c = fai_utf8Get(s + i, n - i, &l0), d = 0;
        if (i + l0 < n) d = fai_utf8Get(s + i + l0, n - i - l0, &l1);
        /* contractions */
        if (c == '\'' && i + 1 < n) {
            uint8_t a = (uint8_t)(s[i + 1] | 0x20), b = i + 2 < n ? (uint8_t)(s[i + 2] | 0x20) : 0;
            size_t len = 0;
            if (a == 's' || a == 't' || a == 'm' || a == 'd') len = 2;
            else if ((a == 'r' && b == 'e') || (a == 'v' && b == 'e') || (a == 'l' && b == 'l')) len = 3;
            if (len) { piece(arg, i, i + len); i += len; continue; }
        }
        /* [^\r\n\p{L}\p{N}]?\p{L}+ */
        if (fai_isLetter(c) || (c != '\r' && c != '\n' && !fai_isDigit(c) && l1 && fai_isLetter(d))) {
            j = fai_isLetter(c) ? i + l0 : i + l0 + l1;
            while (j < n) { size_t l; uint32_t e = fai_utf8Get(s + j, n - j, &l); if (!fai_isLetter(e)) break; j += l; }
            piece(arg, i, j); i = j; continue;
        }
        /* \p{N} (one, or up to `digits`) */
        if (fai_isDigit(c)) {
            int k = 1;
            j = i + l0;
            while (k < digits && j < n) { size_t l; uint32_t e = fai_utf8Get(s + j, n - j, &l); if (!fai_isDigit(e)) break; j += l; k++; }
            piece(arg, i, j); i = j; continue;
        }
        /*  ?[^\s\p{L}\p{N}]+[\r\n]* */
        {
            size_t st = i, l;
            uint32_t e = c;
            int haveSpace = 0;
            if (c == ' ' && l1 && !fai_isSpace(d) && !fai_isLetter(d) && !fai_isDigit(d)) { st = i + l0; e = d; haveSpace = 1; }
            if (!fai_isSpace(e) && !fai_isLetter(e) && !fai_isDigit(e)) {
                j = st;
                while (j < n) { uint32_t x = fai_utf8Get(s + j, n - j, &l); if (fai_isSpace(x) || fai_isLetter(x) || fai_isDigit(x)) break; j += l; }
                while (j < n && (s[j] == '\r' || s[j] == '\n')) j++;
                (void)haveSpace;
                piece(arg, i, j); i = j; continue;
            }
        }
        /* white space: \s*[\r\n]+ | \s+(?!\S) | \s+ */
        {
            size_t l, end = i, lastNl = (size_t)-1, lastStart = i;
            while (end < n) {
                uint32_t x = fai_utf8Get(s + end, n - end, &l);
                if (!fai_isSpace(x)) break;
                if (x == '\r' || x == '\n') lastNl = end + l;
                lastStart = end;
                end += l;
            }
            if (lastNl != (size_t)-1) {   /* \s*[\r\n]+ : up to the last line break of the run */
                piece(arg, i, lastNl); i = lastNl; continue;
            }
            if (end < n && end - i > 1) {   /* \s+(?!\S): leave the last space to the next piece */
                piece(arg, i, lastStart); i = lastStart; continue;
            }
            if (end == i) end = i + l0;
            piece(arg, i, end); i = end;
        }
    }
}

/* A growing list of tokens. */
typedef struct FaiTokens { int32_t* t; size_t n, cap; } FaiTokens;
static int fai_push(FaiTokens* v, int32_t t) {
    if (v->n == v->cap) {
        size_t cap = v->cap ? v->cap * 2 : 64;
        int32_t* g = (int32_t*)realloc(v->t, cap * sizeof(int32_t));
        if (g == NULL) return 0;
        v->t = g; v->cap = cap;
    }
    v->t[v->n++] = t;
    return 1;
}

/* BPE of one piece (already in the byte characters): the pair of the lowest rank is merged until no pair is in the merges. */
typedef struct FaiSym { const char* p; uint32_t n; } FaiSym;
static int fai_bpeWord(const FaiVocab* v, const char* w, size_t n, int ignoreMerges, FaiTokens* out) {
    FaiSym small[64];
    FaiSym* sym = n <= 64 ? small : (FaiSym*)malloc(n * sizeof(FaiSym));
    size_t count = 0, i;
    char buf[512];
    int64_t whole;
    if (sym == NULL) return 0;
    whole = ignoreMerges ? fai_mapFind(&v->tokenIds, w, n) : -1;
    if (whole >= 0) { if (sym != small) free(sym); return fai_push(out, (int32_t)whole); }
    for (i = 0; i < n;) {
        size_t l;
        fai_utf8Get((const uint8_t*)w + i, n - i, &l);
        sym[count].p = w + i; sym[count].n = (uint32_t)l; count++;
        i += l;
    }
    for (;;) {
        int64_t best = -1;
        size_t at = 0;
        for (i = 0; i + 1 < count; i++) {
            size_t len = sym[i].n + 1 + sym[i + 1].n;
            int64_t rank;
            if (len > sizeof buf) continue;
            memcpy(buf, sym[i].p, sym[i].n); buf[sym[i].n] = ' '; memcpy(buf + sym[i].n + 1, sym[i + 1].p, sym[i + 1].n);
            rank = fai_mapFind(&v->mergeRanks, buf, len);
            if (rank >= 0 && (best < 0 || rank < best)) { best = rank; at = i; }
        }
        if (best < 0) break;
        sym[at].n += sym[at + 1].n;   /* the two are next to each other in w */
        memmove(&sym[at + 1], &sym[at + 2], (count - at - 2) * sizeof(FaiSym));
        count--;
    }
    for (i = 0; i < count; i++) {
        int64_t id = fai_mapFind(&v->tokenIds, sym[i].p, sym[i].n);
        if (id >= 0) { if (!fai_push(out, (int32_t)id)) { if (sym != small) free(sym); return 0; } }
        else {   /* not in the vocabulary: its characters one by one */
            size_t k;
            for (k = 0; k < sym[i].n;) {
                size_t l;
                fai_utf8Get((const uint8_t*)sym[i].p + k, sym[i].n - k, &l);
                id = fai_mapFind(&v->tokenIds, sym[i].p + k, l);
                if (id >= 0 && !fai_push(out, (int32_t)id)) { if (sym != small) free(sym); return 0; }
                k += l;
            }
        }
    }
    if (sym != small) free(sym);
    return 1;
}

typedef struct FaiBpeCtx { const FaiVocab* v; const uint8_t* s; FaiTokens* out; int ok, ignoreMerges; } FaiBpeCtx;
static void fai_bpePiece(void* a, size_t from, size_t to) {
    FaiBpeCtx* c = (FaiBpeCtx*)a;
    char small[256];
    size_t cap = (to - from) * 2 + 1, n = 0, i;
    char* w = cap <= sizeof small ? small : (char*)malloc(cap);
    if (w == NULL) { c->ok = 0; return; }
    for (i = from; i < to; i++) n += fai_utf8Put(w + n, g_faiByteChar[c->s[i]]);
    if (!fai_bpeWord(c->v, w, n, c->ignoreMerges, c->out)) c->ok = 0;
    if (w != small) free(w);
}

/* SentencePiece-like: the spaces become "▁" (and one in front), then the pieces of the best scores are merged (as llama.cpp's llm_tokenizer_spm). */
static int fai_spmText(const FaiVocab* v, const uint8_t* s, size_t n, int first, FaiTokens* out) {
    size_t cap = n * 3 + 4, len = 0, i, count = 0;
    char* w = (char*)malloc(cap);
    FaiSym* sym;
    char buf[512];
    if (w == NULL) return 0;
    if (first) { memcpy(w, "\xE2\x96\x81", 3); len = 3; }
    for (i = 0; i < n; i++) { if (s[i] == ' ') { memcpy(w + len, "\xE2\x96\x81", 3); len += 3; } else w[len++] = (char)s[i]; }
    sym = (FaiSym*)malloc((len + 1) * sizeof(FaiSym));
    if (sym == NULL) { free(w); return 0; }
    for (i = 0; i < len;) { size_t l; fai_utf8Get((const uint8_t*)w + i, len - i, &l); sym[count].p = w + i; sym[count].n = (uint32_t)l; count++; i += l; }
    for (;;) {
        float best = -1e30f;
        int64_t at = -1;
        for (i = 0; i + 1 < count; i++) {
            size_t l = sym[i].n + sym[i + 1].n;
            int64_t id;
            if (l > sizeof buf) continue;
            memcpy(buf, sym[i].p, sym[i].n); memcpy(buf + sym[i].n, sym[i + 1].p, sym[i + 1].n);
            id = fai_mapFind(&v->tokenIds, buf, l);
            if (id >= 0) { float sc = v->scores ? v->scores[id] : 0.0f; if (sc > best) { best = sc; at = (int64_t)i; } }
        }
        if (at < 0) break;
        sym[at].n += sym[at + 1].n;
        memmove(&sym[at + 1], &sym[at + 2], (count - (size_t)at - 2) * sizeof(FaiSym));
        count--;
    }
    for (i = 0; i < count; i++) {
        int64_t id = fai_mapFind(&v->tokenIds, sym[i].p, sym[i].n);
        if (id >= 0) fai_push(out, (int32_t)id);
        else {
            uint32_t k;
            for (k = 0; k < sym[i].n; k++) { int32_t b = v->byteTokens[(uint8_t)sym[i].p[k]]; if (b >= 0) fai_push(out, b); else if (v->unk >= 0) fai_push(out, v->unk); }
        }
    }
    free(sym); free(w);
    return 1;
}

/* The tokens of `text` (UTF-8). `special`: the control tokens written in the text (<|im_start|> ...) are those tokens; else they are text. `bos`: the start token first
 * (when the model wants one). */
static int fai_tokenize(const FaiModel* m, const char* text, size_t n, int special, int bos, FaiTokens* out) {
    const FaiVocab* v = &m->vocab;
    const uint8_t* s = (const uint8_t*)text;
    size_t i = 0, start = 0;
    int first = 1;
    fai_bytesInit();
    if (bos && v->addBos && v->bos >= 0 && !fai_push(out, v->bos)) return 0;
    while (start <= n) {
        size_t next = n, len = 0;
        int32_t id = -1;
        if (special) {
            for (i = start; i < n && id < 0; i++) {
                uint32_t k;
                if (s[i] != '<' && s[i] != '[') continue;
                for (k = 0; k < v->specialCount; k++) {
                    const FaiStr* t = &v->tokens[v->special[k]];
                    if (t->n <= n - i && memcmp(s + i, t->p, t->n) == 0) { id = v->special[k]; next = i; len = t->n; break; }
                }
            }
        }
        if (next > start) {
            if (v->kind == FAI_VOCAB_BPE) {
                FaiBpeCtx c;
                /* the pre-tokenizer of the file: llama 3 and lfm take up to 3 digits in a row and the whole word when it is a token; qwen 3.5 counts the
                 * combining marks as letters */
                int llama3 = strcmp(m->pre, "llama-bpe") == 0 || strcmp(m->pre, "llama3") == 0 || strcmp(m->pre, "lfm2") == 0 || strcmp(m->pre, "smollm") == 0 ||
                             (m->pre[0] == 0 && m->archId == FAI_ARCH_LLAMA);
                c.v = v; c.s = s + start; c.out = out; c.ok = 1; c.ignoreMerges = llama3;
                g_faiMarksAreLetters = strcmp(m->pre, "qwen35") == 0;
                fai_pretokenize(s + start, next - start, llama3 ? 3 : 1, fai_bpePiece, &c);
                if (!c.ok) return 0;
            }
            else if (!fai_spmText(v, s + start, next - start, first, out)) return 0;
            first = 0;
        }
        if (id < 0) break;
        if (!fai_push(out, id)) return 0;
        start = next + len;
    }
    return 1;
}

/* The bytes of a token (a control token: none, unless `special`). Returns their number; `out` holds at least 4 * the token's length + 8. */
static size_t fai_tokenBytes(const FaiModel* m, int32_t id, int special, char* out) {
    const FaiVocab* v = &m->vocab;
    const FaiStr* t;
    size_t n = 0, i;
    if (id < 0 || (uint32_t)id >= v->count) return 0;
    t = &v->tokens[id];
    if (v->types && (v->types[id] == FAI_TOK_CONTROL || v->types[id] == FAI_TOK_UNUSED)) {
        if (!special) return 0;
        memcpy(out, t->p, t->n); return t->n;
    }
    if (v->kind == FAI_VOCAB_SPM) {
        if (v->types && v->types[id] == FAI_TOK_BYTE && t->n == 6) { unsigned b = 0; sscanf(t->p + 3, "%2x", &b); out[0] = (char)b; return 1; }
        for (i = 0; i < t->n;) {
            if (i + 3 <= t->n && memcmp(t->p + i, "\xE2\x96\x81", 3) == 0) { out[n++] = ' '; i += 3; }
            else out[n++] = t->p[i++];
        }
        return n;
    }
    if (v->types && v->types[id] == FAI_TOK_USER) { memcpy(out, t->p, t->n); return t->n; }
    fai_bytesInit();
    for (i = 0; i < t->n;) {
        size_t l;
        uint32_t c = fai_utf8Get((const uint8_t*)t->p + i, t->n - i, &l);
        if (c < 512 && g_faiCharByte[c] >= 0) out[n++] = (char)g_faiCharByte[c];
        else n += fai_utf8Put(out + n, c);
        i += l;
    }
    return n;
}

/* The tokens of a prompt go through the layers together, this many at a time: each row of weights is read once for all of them. */
#define FAI_BATCH 16

/* ---- a session: the cache of keys and values, the states of the recurrent layers, the buffers ---------------------------------------------------------
 * The attention layers keep the keys and values of every position: going back to an earlier position is free. The delta net and the convolution keep a
 * state that every token changes: going back needs a copy of it, the checkpoint (taken after a prompt: a choice of options and the next question start
 * from there). */
typedef struct FaiSession {
    const FaiModel* m;
    FaiPool* pool;
    int ctx;              /* the positions of the cache */
    float* kv;            /* the keys and values of the attention layers: [layer: kvOff][pos][kvDim] keys, then values */
    size_t* kvOff;        /* per layer: where its keys start in kv (its values follow after ctx * kvDim) */
    float* rs;            /* the recurrent states: per delta / conv layer its convolution window, then (delta) the state of each head */
    size_t* rsOff;
    size_t rsSize;
    float* checkpoint;    /* a copy of rs ... */
    float* checkpointLogits;
    int checkpointPos;    /* ... after this many tokens (-1: none) */
    float* keep;          /* a second copy of rs after the first keepPos tokens (the instructions every prompt starts with: they stay computed when what
                             follows them changes, e.g. older turns of a chat are left out) */
    int keepPos;          /* 0: none */
    int keepValid;        /* keep holds the state after cached[0 .. keepPos) */
    int reused;           /* the tokens of the last fai_feed that came from the cache */
    int32_t* cached;      /* the tokens whose keys, values and states are in the cache */
    int cachedCount;
    /* the buffers of one step (FAI_BATCH tokens each) */
    float *x, *xb, *xb2, *q, *k, *v, *g, *ao, *att, *hb, *hb2, *mix, *z, *logits, *ropeCos, *ropeSin, *ropeCosSwa, *ropeSinSwa;
    int qWidth, mixWidth;
    FaiQ8* xq;            /* FAI_BATCH rows of the largest width */
    int xqStride;         /* blocks per row of xq */
    int logitsValid;      /* the logits belong to the last cached token */
    uint64_t rng;
} FaiSession;

static void fai_sessionFree(FaiSession* s) {
    if (s == NULL) return;
    free(s->kv); free(s->kvOff); free(s->rs); free(s->rsOff); free(s->checkpoint); free(s->checkpointLogits); free(s->keep); free(s->cached);
    free(s->x); free(s->xb); free(s->xb2); free(s->q); free(s->k); free(s->v); free(s->g); free(s->ao); free(s->att); free(s->hb); free(s->hb2); free(s->mix); free(s->z);
    free(s->logits); free(s->ropeCos); free(s->ropeSin); free(s->ropeCosSwa); free(s->ropeSinSwa); free(s->xq);
    free(s);
}

static int fai_imax(int a, int b) { return a > b ? a : b; }

/* The cosines and sines of the rotary positions: angle = pos * base^(-2i / nRot) / factor_i. */
static int fai_ropeTable(const FaiModel* m, int ctx, float base, const FaiTensor* factors, float** cosOut, float** sinOut) {
    int half = m->nRot / 2, p, i;
    float *c = (float*)malloc((size_t)ctx * (size_t)half * sizeof(float)), *sn = (float*)malloc((size_t)ctx * (size_t)half * sizeof(float));
    const float* f = factors && factors->type == FAI_F32 && factors->ne[0] >= half ? (const float*)factors->data : NULL;
    if (c == NULL || sn == NULL) { free(c); free(sn); return 0; }
    for (p = 0; p < ctx; p++)
        for (i = 0; i < half; i++) {
            double theta = (double)p * pow((double)base, -2.0 * i / (double)m->nRot) / (f ? (double)f[i] : 1.0);
            c[(size_t)p * half + i] = (float)cos(theta); sn[(size_t)p * half + i] = (float)sin(theta);
        }
    *cosOut = c; *sinOut = sn;
    return 1;
}

static FaiSession* fai_sessionNew(const FaiModel* m, FaiPool* pool, int ctx, FaiError* e) {
    FaiSession* s = (FaiSession*)calloc(1, sizeof(FaiSession));
    int hd = m->headDim, qd = m->nHead * hd, l, maxKv = 0, maxDim, anySwa = 0, anyFull = 0;
    size_t kvTotal = 0, rsTotal = 0, B = FAI_BATCH;
    int convDim = m->ssmGroups * m->ssmState * 2 + m->ssmInner;
    if (s == NULL) { fai_fail(e, "Out of memory.", NULL); return NULL; }
    if (ctx <= 0) ctx = 2048;
    if (m->ctxTrain > 0 && ctx > m->ctxTrain) ctx = m->ctxTrain;
    s->m = m; s->pool = pool; s->ctx = ctx; s->rng = 0x9E3779B97F4A7C15ull; s->checkpointPos = -1;
    s->kvOff = (size_t*)calloc((size_t)m->nLayer, sizeof(size_t));
    s->rsOff = (size_t*)calloc((size_t)m->nLayer, sizeof(size_t));
    if (!s->kvOff || !s->rsOff) { fai_sessionFree(s); fai_fail(e, "Out of memory.", NULL); return NULL; }
    for (l = 0; l < m->nLayer; l++) {
        const FaiLayer* L = &m->layers[l];
        if (L->mix == FAI_MIX_ATTN || L->mix == FAI_MIX_SWA) {
            int kvDim = L->nHeadKv * hd;
            s->kvOff[l] = kvTotal;
            kvTotal += 2 * (size_t)ctx * (size_t)kvDim;
            maxKv = fai_imax(maxKv, kvDim);
            if (L->mix == FAI_MIX_SWA) anySwa = 1; else anyFull = 1;
        }
        else if (L->mix == FAI_MIX_DELTA) {
            s->rsOff[l] = rsTotal;
            rsTotal += (size_t)(m->ssmConvK - 1) * (size_t)convDim + (size_t)m->ssmVHeads * (size_t)m->ssmState * (size_t)m->ssmState;
        }
        else {
            s->rsOff[l] = rsTotal;
            rsTotal += (size_t)(m->convL - 1) * (size_t)m->nEmbd;
        }
    }
    s->rsSize = rsTotal;
    s->qWidth = m->archId == FAI_ARCH_QWEN35 ? 2 * qd : qd;   /* qwen35: the query and its gate per head */
    s->mixWidth = fai_imax(fai_imax(convDim, 3 * m->nEmbd), m->ssmInner);
    maxDim = fai_imax(fai_imax(fai_imax(m->nEmbd, m->nFf), qd), m->ssmInner);
    s->kv = kvTotal ? (float*)malloc(kvTotal * sizeof(float)) : NULL;
    s->rs = rsTotal ? (float*)calloc(rsTotal, sizeof(float)) : NULL;
    s->checkpoint = rsTotal ? (float*)malloc(rsTotal * sizeof(float)) : NULL;
    s->keep = rsTotal ? (float*)malloc(rsTotal * sizeof(float)) : NULL;
    s->cached = (int32_t*)malloc((size_t)ctx * sizeof(int32_t));
    s->x = (float*)calloc(B * (size_t)m->nEmbd, sizeof(float)); s->xb = (float*)calloc(B * (size_t)m->nEmbd, sizeof(float)); s->xb2 = (float*)calloc(B * (size_t)m->nEmbd, sizeof(float));
    s->q = (float*)calloc(B * (size_t)s->qWidth, sizeof(float)); s->g = (float*)calloc(B * (size_t)fai_imax(qd, m->nHead), sizeof(float));
    s->k = (float*)calloc(B * (size_t)fai_imax(maxKv, 1), sizeof(float)); s->v = (float*)calloc(B * (size_t)fai_imax(maxKv, 1), sizeof(float));
    s->ao = (float*)calloc(B * (size_t)fai_imax(qd, m->ssmInner), sizeof(float));
    s->att = (float*)calloc((size_t)m->nHead * (size_t)ctx, sizeof(float));
    s->hb = (float*)calloc(B * (size_t)m->nFf, sizeof(float)); s->hb2 = (float*)calloc(B * (size_t)m->nFf, sizeof(float));
    s->mix = (float*)calloc(B * (size_t)s->mixWidth, sizeof(float)); s->z = (float*)calloc(B * (size_t)fai_imax(m->ssmInner, 1), sizeof(float));
    s->logits = (float*)calloc((size_t)m->output->ne[1], sizeof(float));
    s->checkpointLogits = (float*)calloc((size_t)m->output->ne[1], sizeof(float));
    s->xqStride = maxDim / FAI_QK + 1;
    s->xq = (FaiQ8*)calloc(B * (size_t)s->xqStride, sizeof(FaiQ8));
    if ((kvTotal && !s->kv) || (rsTotal && (!s->rs || !s->checkpoint || !s->keep)) || !s->cached || !s->x || !s->xb || !s->xb2 || !s->q || !s->g || !s->k || !s->v || !s->ao || !s->att ||
        !s->hb || !s->hb2 || !s->mix || !s->z || !s->logits || !s->checkpointLogits || !s->xq) {
        fai_sessionFree(s); fai_fail(e, "Out of memory: the context does not fit (a smaller one needs less).", NULL); return NULL;
    }
    if ((anyFull || m->archId != FAI_ARCH_LFM3) && !fai_ropeTable(m, ctx, m->ropeBase, m->archId == FAI_ARCH_LFM3 ? m->ropeFreqs : NULL, &s->ropeCos, &s->ropeSin)) {
        fai_sessionFree(s); fai_fail(e, "Out of memory.", NULL); return NULL;
    }
    if (anySwa && !fai_ropeTable(m, ctx, m->ropeBaseSwa, NULL, &s->ropeCosSwa, &s->ropeSinSwa)) { fai_sessionFree(s); fai_fail(e, "Out of memory.", NULL); return NULL; }
    return s;
}

static void fai_rmsnorm(float* out, const float* x, const FaiTensor* w, int n, float eps) {
    double ss = 0;
    int i;
    float scale;
    const float* g = (const float*)w->data;
    for (i = 0; i < n; i++) ss += (double)x[i] * x[i];
    scale = (float)(1.0 / sqrt(ss / n + eps));
    for (i = 0; i < n; i++) out[i] = x[i] * scale * g[i];
}

/* out[b] = W x[b] (+ the bias) for the `count` rows of x (`xStride` / `outStride` floats apart); the quantized x is made here when W needs it. */
static void fai_matmulN(FaiSession* s, const FaiTensor* w, const float* x, int64_t xStride, float* out, int64_t outStride, const FaiTensor* bias, int count) {
    FaiMatJob j;
    int b;
    if (w->type != FAI_F32 && w->type != FAI_F16)
        for (b = 0; b < count; b++) fai_quantQ8(x + b * xStride, w->ne[0], s->xq + b * s->xqStride);
    j.w = w; j.x = x; j.xq = s->xq; j.out = out; j.bias = bias ? (const float*)bias->data : NULL;
    j.count = count; j.xStride = xStride; j.xqStride = s->xqStride; j.outStride = outStride;
    fai_run(s->pool, w->ne[1], fai_matJob, &j);
}

/* The rotary positions of `heads` heads (the first nRot values of each). */
static void fai_rope(const FaiSession* s, float* vec, int heads, int pos, int swa) {
    const FaiModel* m = s->m;
    int hd = m->headDim, half = m->nRot / 2, h, i;
    const float* c = (swa ? s->ropeCosSwa : s->ropeCos) + (size_t)pos * half;
    const float* sn = (swa ? s->ropeSinSwa : s->ropeSin) + (size_t)pos * half;
    for (h = 0; h < heads; h++) {
        float* v = vec + h * hd;
        if (m->ropeNeox)
            for (i = 0; i < half; i++) { float a = v[i], b = v[i + half]; v[i] = a * c[i] - b * sn[i]; v[i + half] = a * sn[i] + b * c[i]; }
        else
            for (i = 0; i < half; i++) { float a = v[2 * i], b = v[2 * i + 1]; v[2 * i] = a * c[i] - b * sn[i]; v[2 * i + 1] = a * sn[i] + b * c[i]; }
    }
}

typedef struct FaiAttJob { FaiSession* s; int layer, pos; const float* q; float* out; } FaiAttJob;
static void fai_attJob(void* a, int64_t from, int64_t to) {
    FaiAttJob* j = (FaiAttJob*)a;
    FaiSession* s = j->s;
    const FaiModel* m = s->m;
    const FaiLayer* L = &m->layers[j->layer];
    int hd = m->headDim, kvDim = L->nHeadKv * hd, group = m->nHead / L->nHeadKv, t, first = 0;
    float scale = 1.0f / sqrtf((float)hd);
    const float* keys = s->kv + s->kvOff[j->layer];
    const float* values = keys + (size_t)s->ctx * (size_t)kvDim;
    int64_t h;
    if (L->mix == FAI_MIX_SWA && m->swa > 0 && j->pos - m->swa + 1 > 0) first = j->pos - m->swa + 1;
    for (h = from; h < to; h++) {
        const float* q = j->q + h * hd;
        float* att = s->att + h * s->ctx;
        float* out = j->out + h * hd;
        int kvh = (int)h / group, i;
        float maxv = -1e30f, sum = 0;
        for (t = first; t <= j->pos; t++) {
            const float* k = keys + (size_t)t * kvDim + (size_t)kvh * hd;
            float d = 0;
            for (i = 0; i < hd; i++) d += q[i] * k[i];
            d *= scale; att[t] = d;
            if (d > maxv) maxv = d;
        }
        for (t = first; t <= j->pos; t++) { att[t] = expf(att[t] - maxv); sum += att[t]; }
        for (i = 0; i < hd; i++) out[i] = 0;
        for (t = first; t <= j->pos; t++) {
            const float* v = values + (size_t)t * kvDim + (size_t)kvh * hd;
            float w = att[t] / sum;
            for (i = 0; i < hd; i++) out[i] += w * v[i];
        }
    }
}

static float fai_sigmoid(float x) { return 1.0f / (1.0f + expf(-x)); }
static float fai_silu(float x) { return x / (1.0f + expf(-x)); }
static float fai_softplus(float x) { return x > 20.0f ? x : log1pf(expf(x)); }

/* The attention of a layer for `count` tokens from `pos`: s->xb (normed) in, s->xb2 out. */
static void fai_attention(FaiSession* s, int l, int count, int pos) {
    const FaiModel* m = s->m;
    const FaiLayer* L = &m->layers[l];
    int n = m->nEmbd, hd = m->headDim, qd = m->nHead * hd, kvDim = L->nHeadKv * hd, qw = s->qWidth, b, h, i;
    int swa = L->mix == FAI_MIX_SWA;
    float* keys = s->kv + s->kvOff[l];
    float* values = keys + (size_t)s->ctx * (size_t)kvDim;
    fai_matmulN(s, L->wq, s->xb, n, s->q, qw, L->bq, count);
    fai_matmulN(s, L->wk, s->xb, n, s->k, kvDim, L->bk, count);
    fai_matmulN(s, L->wv, s->xb, n, s->v, kvDim, L->bv, count);
    if (m->archId == FAI_ARCH_LFM3) fai_matmulN(s, L->attnGate, s->xb, n, s->g, m->nHead, NULL, count);
    for (b = 0; b < count; b++) {
        float* q = s->q + (size_t)b * qw;
        float* k = s->k + (size_t)b * kvDim;
        size_t at = (size_t)(pos + b) * (size_t)kvDim;
        if (m->archId == FAI_ARCH_QWEN35) {
            /* per head: hd values of the query, then hd of its gate - the queries together in front, the gates into g */
            float* g = s->g + (size_t)b * qd;
            for (h = 0; h < m->nHead; h++) memcpy(g + h * hd, q + h * 2 * hd + hd, (size_t)hd * sizeof(float));
            for (h = 0; h < m->nHead; h++) memmove(q + h * hd, q + h * 2 * hd, (size_t)hd * sizeof(float));
        }
        if (L->qNorm) for (h = 0; h < m->nHead; h++) fai_rmsnorm(q + h * hd, q + h * hd, L->qNorm, hd, m->eps);
        if (L->kNorm) for (h = 0; h < L->nHeadKv; h++) fai_rmsnorm(k + h * hd, k + h * hd, L->kNorm, hd, m->eps);
        fai_rope(s, q, m->nHead, pos + b, swa);
        fai_rope(s, k, L->nHeadKv, pos + b, swa);
        memcpy(keys + at, k, (size_t)kvDim * sizeof(float));
        memcpy(values + at, s->v + (size_t)b * kvDim, (size_t)kvDim * sizeof(float));
    }
    for (b = 0; b < count; b++) {
        FaiAttJob aj;
        float* out = s->ao + (size_t)b * qd;
        aj.s = s; aj.layer = l; aj.pos = pos + b; aj.q = s->q + (size_t)b * qw; aj.out = out;
        fai_run(s->pool, m->nHead, fai_attJob, &aj);
        if (m->archId == FAI_ARCH_QWEN35) { const float* g = s->g + (size_t)b * qd; for (i = 0; i < qd; i++) out[i] *= fai_sigmoid(g[i]); }
        else if (m->archId == FAI_ARCH_LFM3) {
            const float* g = s->g + (size_t)b * m->nHead;
            for (h = 0; h < m->nHead; h++) { float gs = fai_sigmoid(g[h]); for (i = 0; i < hd; i++) out[h * hd + i] *= gs; }
        }
    }
    fai_matmulN(s, L->wo, s->ao, qd, s->xb2, n, NULL, count);
}

/* A causal convolution over the last `width` inputs of each channel: `window` holds the width - 1 before (oldest first), `w` the weights of each
 * channel (oldest first). The window moves on by the input. */
static void fai_convStep(float* window, const float* w, const float* in, float* out, int channels, int width) {
    int c, i, kk = width - 1;
    for (c = 0; c < channels; c++) {
        float* win = window + (size_t)c * kk;
        const float* wc = w + (size_t)c * width;
        float sum = in[c] * wc[kk];
        for (i = 0; i < kk; i++) sum += win[i] * wc[i];
        for (i = 0; i + 1 < kk; i++) win[i] = win[i + 1];
        win[kk - 1] = in[c];
        out[c] = sum;
    }
}

/* The gated delta net of a layer (Qwen 3.5): s->xb in, s->xb2 out. The tokens one after the other through the convolution and the states of the heads. */
typedef struct FaiDeltaJob { FaiSession* s; float* state; const float *q, *k, *v, *gate, *beta; float* out; } FaiDeltaJob;
static void fai_deltaJob(void* a, int64_t from, int64_t to) {
    FaiDeltaJob* j = (FaiDeltaJob*)a;
    const FaiModel* m = j->s->m;
    int S = m->ssmState, i, r;
    float scale = 1.0f / sqrtf((float)S);
    int64_t h;
    for (h = from; h < to; h++) {
        /* the state of the head, stored by rows j: M[j][i] = S[i][j] */
        float* M = j->state + (size_t)h * S * S;
        int kh = (int)(h % m->ssmGroups);
        const float *k = j->k + (size_t)kh * S, *q = j->q + (size_t)kh * S, *v = j->v + (size_t)h * S;
        float decay = expf(j->gate[h]), beta = j->beta[h];
        float* o = j->out + (size_t)h * S;
        for (r = 0; r < S; r++) {
            float* row = M + (size_t)r * S;
            float dot = 0, delta;
            for (i = 0; i < S; i++) { row[i] *= decay; dot += row[i] * k[i]; }
            delta = (v[r] - dot) * beta;
            dot = 0;
            for (i = 0; i < S; i++) { row[i] += delta * k[i]; dot += row[i] * q[i]; }
            o[r] = dot * scale;
        }
    }
}
static void fai_delta(FaiSession* s, int l, int count) {
    const FaiModel* m = s->m;
    const FaiLayer* L = &m->layers[l];
    int n = m->nEmbd, S = m->ssmState, Hk = m->ssmGroups, Hv = m->ssmVHeads, keyDim = Hk * S, valueDim = Hv * S, convDim = 2 * keyDim + valueDim, mw = s->mixWidth, b, h, i;
    float* window = s->rs + s->rsOff[l];
    float* state = window + (size_t)(m->ssmConvK - 1) * (size_t)convDim;
    float conv[16384];
    float* cbuf = convDim <= (int)(sizeof conv / sizeof conv[0]) ? conv : (float*)malloc((size_t)convDim * sizeof(float));
    float gate[256], beta[256];
    if (cbuf == NULL || Hv > 256) return;
    fai_matmulN(s, L->ssmQkv, s->xb, n, s->mix, mw, NULL, count);
    fai_matmulN(s, L->attnGate, s->xb, n, s->z, valueDim, NULL, count);
    fai_matmulN(s, L->ssmBeta, s->xb, n, s->q, Hv, NULL, count);      /* (q and k are free here: beta and alpha of each token) */
    fai_matmulN(s, L->ssmAlpha, s->xb, n, s->k, Hv, NULL, count);
    for (b = 0; b < count; b++) {
        float* o = s->ao + (size_t)b * valueDim;
        const float* z = s->z + (size_t)b * valueDim;
        FaiDeltaJob dj;
        fai_convStep(window, (const float*)L->ssmConv->data, s->mix + (size_t)b * mw, cbuf, convDim, m->ssmConvK);
        for (i = 0; i < convDim; i++) cbuf[i] = fai_silu(cbuf[i]);
        /* q and k of each key head scaled to length 1 */
        for (h = 0; h < 2 * Hk; h++) {
            float* x = cbuf + (size_t)h * S;
            double ss = 0;
            float inv;
            for (i = 0; i < S; i++) ss += (double)x[i] * x[i];
            inv = (float)(1.0 / sqrt(ss + m->eps));
            for (i = 0; i < S; i++) x[i] *= inv;
        }
        for (h = 0; h < Hv; h++) {
            beta[h] = fai_sigmoid(s->q[(size_t)b * Hv + h]);
            gate[h] = fai_softplus(s->k[(size_t)b * Hv + h] + ((const float*)L->ssmDt->data)[h]) * ((const float*)L->ssmA->data)[h];
        }
        dj.s = s; dj.state = state; dj.q = cbuf; dj.k = cbuf + keyDim; dj.v = cbuf + 2 * keyDim; dj.gate = gate; dj.beta = beta; dj.out = o;
        fai_run(s->pool, Hv, fai_deltaJob, &dj);
        /* the gated norm of each head: rms norm (the weights of a head) times silu(z) */
        for (h = 0; h < Hv; h++) {
            fai_rmsnorm(o + h * S, o + h * S, L->ssmNorm, S, m->eps);
            for (i = 0; i < S; i++) o[h * S + i] *= fai_silu(z[h * S + i]);
        }
    }
    if (cbuf != conv) free(cbuf);
    fai_matmulN(s, L->ssmOut, s->ao, valueDim, s->xb2, n, NULL, count);
}

/* The short convolution of a layer (LFM): b, c, x = in_proj(xb); y = c * conv(b * x) over the last convL tokens; out_proj(y). */
static void fai_shortConv(FaiSession* s, int l, int count) {
    const FaiModel* m = s->m;
    const FaiLayer* L = &m->layers[l];
    int n = m->nEmbd, mw = s->mixWidth, b, i;
    float* window = s->rs + s->rsOff[l];
    fai_matmulN(s, L->convIn, s->xb, n, s->mix, mw, NULL, count);
    for (b = 0; b < count; b++) {
        float* bcx = s->mix + (size_t)b * mw;
        float* y = s->ao + (size_t)b * n;
        for (i = 0; i < n; i++) bcx[i] *= bcx[2 * n + i];             /* b * x (into the place of b) */
        fai_convStep(window, (const float*)L->convW->data, bcx, y, n, m->convL);
        for (i = 0; i < n; i++) y[i] *= bcx[n + i];                    /* times c */
    }
    fai_matmulN(s, L->convOut, s->ao, n, s->xb2, n, NULL, count);
}

/* `count` tokens (FAI_BATCH at most) at the positions pos .. pos + count - 1: their keys, values and states go into the cache. `wantLogits`: the scores of
 * the token after the last one are computed (the end of a prompt, a generated token) - the others skip the largest product. */
static void fai_forward(FaiSession* s, const int32_t* tokens, int count, int pos, int wantLogits) {
    const FaiModel* m = s->m;
    int n = m->nEmbd, ff = m->nFf, l, i, b;
    for (b = 0; b < count; b++) fai_dequantRow(m->tokEmbd, tokens[b], s->x + (size_t)b * n);
    for (l = 0; l < m->nLayer; l++) {
        const FaiLayer* L = &m->layers[l];
        for (b = 0; b < count; b++) fai_rmsnorm(s->xb + (size_t)b * n, s->x + (size_t)b * n, L->attnNorm, n, m->eps);
        if (L->mix == FAI_MIX_DELTA) fai_delta(s, l, count);
        else if (L->mix == FAI_MIX_CONV) fai_shortConv(s, l, count);
        else fai_attention(s, l, count, pos);
        if (L->postAttnNorm) for (b = 0; b < count; b++) fai_rmsnorm(s->xb2 + (size_t)b * n, s->xb2 + (size_t)b * n, L->postAttnNorm, n, m->eps);
        for (i = 0; i < n * count; i++) s->x[i] += s->xb2[i];
        for (b = 0; b < count; b++) fai_rmsnorm(s->xb + (size_t)b * n, s->x + (size_t)b * n, L->ffnNorm, n, m->eps);
        fai_matmulN(s, L->gate, s->xb, n, s->hb, ff, NULL, count);
        fai_matmulN(s, L->up, s->xb, n, s->hb2, ff, NULL, count);
        for (i = 0; i < ff * count; i++) s->hb[i] = fai_silu(s->hb[i]) * s->hb2[i];
        fai_matmulN(s, L->down, s->hb, ff, s->xb2, n, NULL, count);
        if (L->postFfnNorm) for (b = 0; b < count; b++) fai_rmsnorm(s->xb2 + (size_t)b * n, s->xb2 + (size_t)b * n, L->postFfnNorm, n, m->eps);
        for (i = 0; i < n * count; i++) s->x[i] += s->xb2[i];
    }
    if (wantLogits) {
        fai_rmsnorm(s->xb, s->x + (size_t)(count - 1) * n, m->outNorm, n, m->eps);
        fai_matmulN(s, m->output, s->xb, n, s->logits, 0, NULL, 1);
    }
    s->logitsValid = wantLogits;
}

/* Remembers the recurrent states and the logits as they are now (after cachedCount tokens). */
static void fai_checkpoint(FaiSession* s) {
    if (s->rsSize == 0 || !s->logitsValid) return;
    memcpy(s->checkpoint, s->rs, s->rsSize * sizeof(float));
    memcpy(s->checkpointLogits, s->logits, (size_t)s->m->output->ne[1] * sizeof(float));
    s->checkpointPos = s->cachedCount;
}

/* Brings the cache to `tokens`: what it holds of their start stays, the rest is computed; afterwards the logits are those after the last token.
 * `stop`: when it becomes non-zero (another thread), the work stops (returns 0). 0 also when they do not fit into the context. While it works, cachedCount
 * is the number of tokens done (another thread may read it for a progress bar); `reused` says how many came from the cache. */
static int fai_feed(FaiSession* s, const int32_t* tokens, int count, volatile int* stop) {
    int same = 0, i;
    s->reused = 0;
    if (count > s->ctx || count <= 0) return 0;
    while (same < s->cachedCount && same < count && s->cached[same] == tokens[same]) same++;
    if (same == count && same == s->cachedCount && s->logitsValid) { s->reused = count; return 1; }
    if (s->rsSize > 0) {
        /* the recurrent states cannot go back: to the checkpoint (when it is a start of the tokens), else to the kept start, else to the beginning */
        if (same < s->cachedCount || (same == count && !s->logitsValid)) {
            int atCheckpoint = s->checkpointPos >= 0 && s->checkpointPos <= same && s->checkpointPos <= s->cachedCount;
            int atKeep = s->keepValid && s->keepPos > 0 && s->keepPos <= same && s->keepPos < count;
            if (atCheckpoint && (!atKeep || s->checkpointPos >= s->keepPos)) {
                memcpy(s->rs, s->checkpoint, s->rsSize * sizeof(float));
                memcpy(s->logits, s->checkpointLogits, (size_t)s->m->output->ne[1] * sizeof(float));
                same = s->checkpointPos;
                s->cachedCount = same;
                s->logitsValid = 1;
                if (same == count) { s->reused = count; return 1; }
            }
            else if (atKeep) {
                memcpy(s->rs, s->keep, s->rsSize * sizeof(float));
                same = s->keepPos;
                s->cachedCount = same;
                s->logitsValid = 0;
                if (s->checkpointPos > same) s->checkpointPos = -1;
            }
            else { memset(s->rs, 0, s->rsSize * sizeof(float)); same = 0; s->checkpointPos = -1; }
        }
    }
    else if (same == count) same--;   /* the logits of the last one are needed again */
    s->reused = same;
    s->cachedCount = same;
    if (same < s->keepPos) s->keepValid = 0;
    for (i = same; i < count;) {
        int b = count - i < FAI_BATCH ? count - i : FAI_BATCH;
        /* (a batch ends where the kept start ends: the state there is copied) */
        if (s->rsSize > 0 && i < s->keepPos && i + b > s->keepPos) b = s->keepPos - i;
        if (stop && *stop) {
            s->cachedCount = i; s->logitsValid = 0;
            if (s->rsSize > 0 && s->checkpointPos > i) s->checkpointPos = -1;
            return 0;
        }
        fai_forward(s, tokens + i, b, i, i + b == count);
        memcpy(s->cached + i, tokens + i, (size_t)b * sizeof(int32_t));
        i += b;
        s->cachedCount = i;
        if (s->rsSize > 0 && i == s->keepPos) { memcpy(s->keep, s->rs, s->rsSize * sizeof(float)); s->keepValid = 1; }
    }
    s->cachedCount = count;
    return 1;
}

/* The first `count` tokens of the prompts to come are the same (instructions): with recurrent layers their state is kept, so a prompt whose later part
 * changed does not compute them again (attention layers keep every position anyway). */
static void fai_keep(FaiSession* s, int count) {
    if (count < 0 || count >= s->ctx) count = 0;
    if (count != s->keepPos) { s->keepPos = count; s->keepValid = 0; }
}

/* Forgets what the cache holds: the next prompt is computed from its start. */
static void fai_forget(FaiSession* s) {
    s->cachedCount = 0; s->logitsValid = 0; s->checkpointPos = -1; s->keepValid = 0;
    if (s->rsSize > 0) memset(s->rs, 0, s->rsSize * sizeof(float));
}

/* ---- sampling ----------------------------------------------------------------------------------------------------------------------------------------- */
typedef struct FaiSampling {
    float temperature;    /* 0: always the most likely token */
    int topK;             /* 0: all */
    float topP;           /* 1: all */
    float minP;           /* 0: all */
    float repeatPenalty;  /* 1: none */
    int repeatLast;       /* the tokens it looks back at */
    uint64_t seed;        /* 0: from the clock */
} FaiSampling;

static uint32_t fai_random(FaiSession* s) {
    uint64_t x = s->rng;
    x ^= x >> 12; x ^= x << 25; x ^= x >> 27;
    s->rng = x;
    return (uint32_t)((x * 0x2545F4914F6CDD1Dull) >> 32);
}

typedef struct FaiCand { int32_t id; float p; } FaiCand;
static int fai_candDesc(const void* a, const void* b) { float x = ((const FaiCand*)a)->p, y = ((const FaiCand*)b)->p; return x < y ? 1 : x > y ? -1 : 0; }

/* The next token from the logits. `recent`: the tokens before (for the penalty). */
static int32_t fai_sample(FaiSession* s, const FaiSampling* o, const int32_t* recent, int recentCount) {
    int vocab = (int)s->m->output->ne[1], i, n;
    float* lg = s->logits;
    FaiCand* c;
    float maxv, sum, acc, r;
    int32_t chosen;
    if (o->repeatPenalty > 0 && o->repeatPenalty != 1.0f) {
        int from = o->repeatLast > 0 && recentCount > o->repeatLast ? recentCount - o->repeatLast : 0;
        for (i = from; i < recentCount; i++) {
            int32_t t = recent[i];
            if (t < 0 || t >= vocab) continue;
            /* each token once: mark it by a NaN-free flip */
            if (lg[t] > 0) lg[t] /= o->repeatPenalty; else lg[t] *= o->repeatPenalty;
        }
    }
    if (o->temperature <= 0) {
        int best = 0;
        for (i = 1; i < vocab; i++) if (lg[i] > lg[best]) best = i;
        return best;
    }
    n = o->topK > 0 && o->topK < vocab ? o->topK : vocab;
    c = (FaiCand*)malloc((size_t)vocab * sizeof(FaiCand));
    if (c == NULL) { int best = 0; for (i = 1; i < vocab; i++) if (lg[i] > lg[best]) best = i; return best; }
    for (i = 0; i < vocab; i++) { c[i].id = i; c[i].p = lg[i] / o->temperature; }
    if (n < vocab) {
        /* the n largest to the front (a partial selection), then sorted */
        int lo = 0, hi = vocab - 1, k = n;
        while (lo < hi) {
            float pivot = c[(lo + hi) / 2].p;
            int a = lo, b = hi;
            while (a <= b) {
                while (c[a].p > pivot) a++;
                while (c[b].p < pivot) b--;
                if (a <= b) { FaiCand t = c[a]; c[a] = c[b]; c[b] = t; a++; b--; }
            }
            if (k - 1 <= b) hi = b; else if (k - 1 >= a) lo = a; else break;
        }
    }
    qsort(c, (size_t)n, sizeof(FaiCand), fai_candDesc);
    maxv = c[0].p; sum = 0;
    for (i = 0; i < n; i++) { c[i].p = expf(c[i].p - maxv); sum += c[i].p; }
    for (i = 0; i < n; i++) c[i].p /= sum;
    if (o->minP > 0) { int keep = 1; while (keep < n && c[keep].p >= o->minP * c[0].p) keep++; n = keep; }
    if (o->topP > 0 && o->topP < 1) { int keep = 0; acc = 0; while (keep < n) { acc += c[keep].p; keep++; if (acc >= o->topP) break; } n = keep; }
    sum = 0;
    for (i = 0; i < n; i++) sum += c[i].p;
    r = (float)fai_random(s) / 4294967296.0f * sum;
    acc = 0; chosen = c[n - 1].id;
    for (i = 0; i < n; i++) { acc += c[i].p; if (r < acc) { chosen = c[i].id; break; } }
    free(c);
    return chosen;
}

/* The log-probability of `token` after the logits as they are. */
static double fai_logProb(const FaiSession* s, int32_t token) {
    int vocab = (int)s->m->output->ne[1], i;
    float maxv = s->logits[0];
    double sum = 0;
    for (i = 1; i < vocab; i++) if (s->logits[i] > maxv) maxv = s->logits[i];
    for (i = 0; i < vocab; i++) sum += exp((double)(s->logits[i] - maxv));
    return (double)(s->logits[token] - maxv) - log(sum);
}

static int fai_isEnd(const FaiModel* m, int32_t t) {
    const FaiVocab* v = &m->vocab;
    const FaiStr* s;
    if (t == v->eos || t == v->eot) return 1;
    if (t < 0 || (uint32_t)t >= v->count) return 1;
    s = &v->tokens[t];
    return (s->n == 13 && memcmp(s->p, "<|endoftext|>", 13) == 0) || (s->n == 10 && memcmp(s->p, "<|im_end|>", 10) == 0) || (s->n == 10 && memcmp(s->p, "<|eot_id|>", 10) == 0);
}

#endif
