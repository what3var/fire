/* fire native bridge "compression" (C): the natives behind `#import "compression"` (docs/COMPRESSION.md) - Deflate, zlib and gzip as streams and in one go,
 * and the byte work the Zip code of the prelude needs (little-endian fields, copies, CRC-32, finding the end record). The engine is fire_cmp_deflate.h (our own
 * Deflate, no library: the same bytes on every target).
 *
 * An (de)compressor is an integer handle in a table of this bridge (index 0 unused, handles not used again); what a program leaves open is freed when it ends.
 * Every function reports an error the same way: -1/false/undefined and the code and message of this thread (`__CmpLastError`, `__CmpLastErrorMessage`, the
 * slot of the io bridge): 1 broken data, 2 an invalid handle, 3 an invalid argument, 4 out of memory. */
#ifndef FIRE_BRIDGE_COMPRESSION_H
#define FIRE_BRIDGE_COMPRESSION_H

#include "fire_cmp_deflate.h"

enum { FIRE_CMPB_BROKEN = 1, FIRE_CMPB_HANDLE = 2, FIRE_CMPB_ARGUMENT = 3, FIRE_CMPB_MEMORY = 4 };

static int64_t fire_cmpb_fail(int code, const char* message) {
    g_ioError.code = code;
    snprintf(g_ioError.message, sizeof g_ioError.message, "%s", message);
    return -1;
}
static void fire_cmpb_ok(void) { g_ioError.code = 0; g_ioError.message[0] = 0; }

/* ---- the table of (de)compressors ---- */
typedef struct FireCmpEntry { int inflate; void* z; } FireCmpEntry;
static FireCmpEntry* g_cmpItems = NULL;
static size_t g_cmpCount = 1, g_cmpCap = 0;

static void fire_cmpb_release(FireCmpEntry* e) {
    if (e->z == NULL) return;
    if (e->inflate) fire_inf_free((FireInflate*)e->z); else fire_def_free((FireDeflate*)e->z);
    e->z = NULL;
}
/* The program has ended (the library for the VM stays loaded for the next one): frees what it left open. */
static void fire_compression_reset(void) {
    size_t i;
    for (i = 1; i < g_cmpCount; i++) fire_cmpb_release(&g_cmpItems[i]);
}
static int64_t fire_cmpb_add(int inflate, void* z) {
    if (z == NULL) return fire_cmpb_fail(FIRE_CMPB_MEMORY, "Out of memory.");
    if (g_cmpCount >= g_cmpCap) {
        size_t cap = g_cmpCap ? g_cmpCap * 2 : 16;
        FireCmpEntry* grown = (FireCmpEntry*)realloc(g_cmpItems, sizeof(FireCmpEntry) * cap);
        if (grown == NULL) {
            if (inflate) fire_inf_free((FireInflate*)z); else fire_def_free((FireDeflate*)z);
            return fire_cmpb_fail(FIRE_CMPB_MEMORY, "Out of memory.");
        }
        g_cmpItems = grown;
        g_cmpCap = cap;
    }
    g_cmpItems[g_cmpCount].inflate = inflate;
    g_cmpItems[g_cmpCount].z = z;
    fire_cmpb_ok();
    return (int64_t)g_cmpCount++;
}
static void* fire_cmpb_find(Value h, int inflate) {
    int64_t i = h.i;
    if (i > 0 && (size_t)i < g_cmpCount && g_cmpItems[i].z != NULL) {
        if (g_cmpItems[i].inflate == inflate) return g_cmpItems[i].z;
        fire_cmpb_fail(FIRE_CMPB_ARGUMENT, inflate ? "This stream compresses, it cannot decompress." : "This stream decompresses, it cannot compress.");
        return NULL;
    }
    fire_cmpb_fail(FIRE_CMPB_HANDLE, "Invalid or already closed compression stream.");
    return NULL;
}
/* The bytes `offset`..`offset + count` of a buffer: NULL with the error set when they are not inside it. */
static uint8_t* fire_cmpb_range(Value buffer, int64_t offset, int64_t count) {
    int64_t length;
    if (buffer.kind != K_Buffer) { fire_cmpb_fail(FIRE_CMPB_ARGUMENT, "A byte buffer is expected."); return NULL; }
    length = bufOf(buffer)->length;
    if (offset < 0 || count < 0 || offset > length || count > length - offset) {
        char text[160];
        snprintf(text, sizeof text, "offset/count (%lld/%lld) are outside of the buffer (length %lld).", (long long)offset, (long long)count, (long long)length);
        fire_cmpb_fail(FIRE_CMPB_ARGUMENT, text);
        return NULL;
    }
    return bufOf(buffer)->data + offset;
}
static int fire_cmpb_format(Value format) { return format.kind == K_Int && format.i >= 0 && format.i <= 2 ? (int)format.i : -1; }

/* ---- natives ---- */
static Value fire_compression_LastError(void) { return Int(g_ioError.code); }
static Value fire_compression_LastErrorMessage(OwnList* list) {
    size_t n = strlen(g_ioError.message), i;
    Str* s = allocStr((uint32_t)n, list);
    for (i = 0; i < n; i++) strChars(s)[i] = (fire_char16)(uint8_t)g_ioError.message[i];
    return StrV(s);
}
static Value fire_compression_OpenCount(void) {
    int64_t n = 0;
    size_t i;
    for (i = 1; i < g_cmpCount; i++) if (g_cmpItems[i].z != NULL) n++;
    return Int(n);
}

/* A decompressor for `format` (0 raw Deflate, 1 zlib, 2 gzip): its handle, -1 on an error. */
static Value fire_compression_InflateNew(Value format) {
    int f = fire_cmpb_format(format);
    if (f < 0) return Int(fire_cmpb_fail(FIRE_CMPB_ARGUMENT, "Unknown compression format."));
    return Int(fire_cmpb_add(1, fire_inf_new(f)));
}
/* Gives the decompressor `count` bytes of compressed data. */
static Value fire_compression_InflateFeed(Value h, Value buffer, Value offset, Value count) {
    FireInflate* z = (FireInflate*)fire_cmpb_find(h, 1);
    uint8_t* p;
    if (z == NULL) return Bool(false);
    if (buffer.kind == K_Buffer && !leafAlive(buffer)) return destroyedError(buffer);
    p = fire_cmpb_range(buffer, offset.i, count.i);
    if (p == NULL) return Bool(false);
    if (!fire_inf_feed(z, p, (size_t)count.i)) { fire_cmpb_fail(FIRE_CMPB_MEMORY, "Out of memory."); return Bool(false); }
    fire_cmpb_ok();
    return Bool(true);
}
/* No more compressed data will come (the end of the data must be there now). */
static Value fire_compression_InflateEnd(Value h) {
    FireInflate* z = (FireInflate*)fire_cmpb_find(h, 1);
    if (z == NULL) return Bool(false);
    z->ended = 1;
    fire_cmpb_ok();
    return Bool(true);
}
/* Decompresses into the buffer: the number of bytes (0: more input is needed, or the end - InflateDone), -1 on an error (broken data). */
static Value fire_compression_InflateRead(Value h, Value buffer, Value offset, Value count) {
    FireInflate* z = (FireInflate*)fire_cmpb_find(h, 1);
    uint8_t* p;
    long n;
    if (z == NULL) return Int(-1);
    if (buffer.kind == K_Buffer && !leafAlive(buffer)) return destroyedError(buffer);
    p = fire_cmpb_range(buffer, offset.i, count.i);
    if (p == NULL) return Int(-1);
    if (z->state == FIRE_INF_ERROR) return Int(fire_cmpb_fail(FIRE_CMPB_BROKEN, z->error));
    n = fire_inf_run(z, p, (size_t)count.i);
    if (n < 0) return Int(fire_cmpb_fail(FIRE_CMPB_BROKEN, z->error));
    fire_cmpb_ok();
    return Int(n);
}
/* Whether the end of the compressed data was reached. */
static Value fire_compression_InflateDone(Value h) {
    FireInflate* z = (FireInflate*)fire_cmpb_find(h, 1);
    if (z == NULL) return Bool(false);
    fire_cmpb_ok();
    return Bool(z->state == FIRE_INF_DONE);
}

/* A compressor for `format` with `level` (0 stored .. 9 smallest): its handle, -1 on an error. */
static Value fire_compression_DeflateNew(Value format, Value level) {
    int f = fire_cmpb_format(format);
    if (f < 0) return Int(fire_cmpb_fail(FIRE_CMPB_ARGUMENT, "Unknown compression format."));
    if (level.kind != K_Int || level.i < 0 || level.i > 9) return Int(fire_cmpb_fail(FIRE_CMPB_ARGUMENT, "The compression level must be 0 (none) .. 9 (smallest)."));
    return Int(fire_cmpb_add(0, fire_def_new(f, (int)level.i)));
}
/* Takes `count` bytes to compress (all of them). */
static Value fire_compression_DeflateWrite(Value h, Value buffer, Value offset, Value count) {
    FireDeflate* z = (FireDeflate*)fire_cmpb_find(h, 0);
    uint8_t* p;
    if (z == NULL) return Bool(false);
    if (z->finished) { fire_cmpb_fail(FIRE_CMPB_ARGUMENT, "The compressed stream is already finished."); return Bool(false); }
    if (buffer.kind == K_Buffer && !leafAlive(buffer)) return destroyedError(buffer);
    p = fire_cmpb_range(buffer, offset.i, count.i);
    if (p == NULL) return Bool(false);
    fire_def_write(z, p, (size_t)count.i);
    if (z->outOfMemory) { fire_cmpb_fail(FIRE_CMPB_MEMORY, "Out of memory."); return Bool(false); }
    fire_cmpb_ok();
    return Bool(true);
}
/* Everything written so far goes out: `final` ends the compressed data, otherwise a sync flush. */
static Value fire_compression_DeflateFlush(Value h, Value final) {
    FireDeflate* z = (FireDeflate*)fire_cmpb_find(h, 0);
    if (z == NULL) return Bool(false);
    fire_def_flush(z, final.kind == K_Bool ? final.i != 0 : final.i != 0);
    if (z->outOfMemory) { fire_cmpb_fail(FIRE_CMPB_MEMORY, "Out of memory."); return Bool(false); }
    fire_cmpb_ok();
    return Bool(true);
}
/* Takes up to `count` compressed bytes that are ready: the number (0: none now). */
static Value fire_compression_DeflateRead(Value h, Value buffer, Value offset, Value count) {
    FireDeflate* z = (FireDeflate*)fire_cmpb_find(h, 0);
    uint8_t* p;
    if (z == NULL) return Int(-1);
    if (buffer.kind == K_Buffer && !leafAlive(buffer)) return destroyedError(buffer);
    p = fire_cmpb_range(buffer, offset.i, count.i);
    if (p == NULL) return Int(-1);
    fire_cmpb_ok();
    return Int((int64_t)fire_def_read(z, p, (size_t)count.i));
}

static Value fire_compression_Close(Value h) {
    int64_t i = h.i;
    if (i <= 0 || (size_t)i >= g_cmpCount || g_cmpItems[i].z == NULL) { fire_cmpb_fail(FIRE_CMPB_HANDLE, "Invalid or already closed compression stream."); return Bool(false); }
    fire_cmpb_release(&g_cmpItems[i]);
    fire_cmpb_ok();
    return Bool(true);
}

/* All of `data` compressed in one go (a new buffer); undefined on an error. */
static Value fire_compression_Compress(Value data, Value format, Value level, OwnList* list) {
    int f = fire_cmpb_format(format);
    FireDeflate* z;
    uint8_t* p;
    Buf* out;
    size_t n;
    if (f < 0) { fire_cmpb_fail(FIRE_CMPB_ARGUMENT, "Unknown compression format."); return Undef(); }
    if (level.kind != K_Int || level.i < 0 || level.i > 9) { fire_cmpb_fail(FIRE_CMPB_ARGUMENT, "The compression level must be 0 (none) .. 9 (smallest)."); return Undef(); }
    if (data.kind == K_Buffer && !leafAlive(data)) return destroyedError(data);
    p = fire_cmpb_range(data, 0, data.kind == K_Buffer ? bufOf(data)->length : 0);
    if (p == NULL) return Undef();
    z = fire_def_new(f, (int)level.i);
    if (z == NULL) { fire_cmpb_fail(FIRE_CMPB_MEMORY, "Out of memory."); return Undef(); }
    fire_def_write(z, p, bufOf(data)->length);
    fire_def_flush(z, 1);
    if (z->outOfMemory) { fire_def_free(z); fire_cmpb_fail(FIRE_CMPB_MEMORY, "Out of memory."); return Undef(); }
    n = z->outLen - z->outPos;
    if (n > 0xFFFFFFF0u) { fire_def_free(z); fire_cmpb_fail(FIRE_CMPB_MEMORY, "The result is too large for a buffer."); return Undef(); }
    out = allocBuf((uint32_t)n, list);
    fire_def_read(z, out->data, n);
    fire_def_free(z);
    fire_cmpb_ok();
    return BufV(out);
}

/* All of `data` decompressed in one go (a new buffer); undefined on an error (broken or incomplete data). */
static Value fire_compression_Decompress(Value data, Value format, OwnList* list) {
    int f = fire_cmpb_format(format);
    FireInflate* z;
    uint8_t* p;
    uint8_t* all = NULL;
    size_t len = 0, cap = 0;
    Buf* out;
    if (f < 0) { fire_cmpb_fail(FIRE_CMPB_ARGUMENT, "Unknown compression format."); return Undef(); }
    if (data.kind == K_Buffer && !leafAlive(data)) return destroyedError(data);
    p = fire_cmpb_range(data, 0, data.kind == K_Buffer ? bufOf(data)->length : 0);
    if (p == NULL) return Undef();
    z = fire_inf_new(f);
    if (z == NULL || !fire_inf_feed(z, p, bufOf(data)->length)) { fire_inf_free(z); fire_cmpb_fail(FIRE_CMPB_MEMORY, "Out of memory."); return Undef(); }
    z->ended = 1;
    for (;;) {
        long n;
        if (cap - len < 65536) {
            size_t grow = cap ? cap * 2 : 65536 + (size_t)bufOf(data)->length * 3;
            uint8_t* bigger = (uint8_t*)realloc(all, grow);
            if (bigger == NULL || grow > 0xFFFFFFF0u) { free(bigger ? bigger : all); fire_inf_free(z); fire_cmpb_fail(FIRE_CMPB_MEMORY, "The result is too large for a buffer."); return Undef(); }
            all = bigger;
            cap = grow;
        }
        n = fire_inf_run(z, all + len, cap - len);
        if (n < 0) { free(all); fire_cmpb_fail(FIRE_CMPB_BROKEN, z->error); fire_inf_free(z); return Undef(); }
        len += (size_t)n;
        if (n == 0) break;
    }
    if (z->state != FIRE_INF_DONE) { free(all); fire_inf_free(z); fire_cmpb_fail(FIRE_CMPB_BROKEN, "The compressed data ends too early."); return Undef(); }
    fire_inf_free(z);
    out = allocBuf((uint32_t)len, list);
    if (len > 0) memcpy(out->data, all, len);
    free(all);
    fire_cmpb_ok();
    return BufV(out);
}

/* ---- bytes for the Zip code ---- */
/* CRC-32 of `count` bytes from `offset`, continued from `crc` (0 to start); -1 on an error. */
static Value fire_compression_Crc32(Value crc, Value buffer, Value offset, Value count) {
    uint8_t* p;
    if (buffer.kind == K_Buffer && !leafAlive(buffer)) return destroyedError(buffer);
    p = fire_cmpb_range(buffer, offset.i, count.i);
    if (p == NULL) return Int(-1);
    return Int((int64_t)fire_cmp_crc((uint32_t)crc.i, p, (size_t)count.i));
}
/* An unsigned little-endian number of `size` (1..8) bytes at `offset` (8 bytes: as a signed 64-bit number); -1 outside of the buffer. */
static Value fire_compression_GetLE(Value buffer, Value offset, Value size) {
    uint8_t* p;
    uint64_t v = 0;
    int i;
    if (size.i < 1 || size.i > 8) return Int(fire_cmpb_fail(FIRE_CMPB_ARGUMENT, "A field has 1 to 8 bytes."));
    if (buffer.kind == K_Buffer && !leafAlive(buffer)) return destroyedError(buffer);
    p = fire_cmpb_range(buffer, offset.i, size.i);
    if (p == NULL) return Int(-1);
    for (i = (int)size.i - 1; i >= 0; i--) v = (v << 8) | p[i];
    return Int((int64_t)v);
}
/* Writes `value` little-endian into `size` (1..8) bytes at `offset`. */
static Value fire_compression_PutLE(Value buffer, Value offset, Value size, Value value) {
    uint8_t* p;
    uint64_t v = (uint64_t)value.i;
    int i;
    if (size.i < 1 || size.i > 8) { fire_cmpb_fail(FIRE_CMPB_ARGUMENT, "A field has 1 to 8 bytes."); return Bool(false); }
    if (buffer.kind == K_Buffer && !leafAlive(buffer)) return destroyedError(buffer);
    p = fire_cmpb_range(buffer, offset.i, size.i);
    if (p == NULL) return Bool(false);
    for (i = 0; i < (int)size.i; i++) { p[i] = (uint8_t)v; v >>= 8; }
    return Bool(true);
}
/* Copies `count` bytes (the ranges may overlap). */
static Value fire_compression_Copy(Value source, Value sourceOffset, Value target, Value targetOffset, Value count) {
    uint8_t* from;
    uint8_t* to;
    if (source.kind == K_Buffer && !leafAlive(source)) return destroyedError(source);
    if (target.kind == K_Buffer && !leafAlive(target)) return destroyedError(target);
    from = fire_cmpb_range(source, sourceOffset.i, count.i);
    if (from == NULL) return Bool(false);
    to = fire_cmpb_range(target, targetOffset.i, count.i);
    if (to == NULL) return Bool(false);
    if (count.i > 0) memmove(to, from, (size_t)count.i);
    return Bool(true);
}
/* The last offset at which the 4-byte little-endian `signature` starts, -1 if there is none. */
static Value fire_compression_FindLast(Value buffer, Value signature) {
    Buf* b;
    int64_t i;
    uint32_t sig = (uint32_t)signature.i;
    if (buffer.kind != K_Buffer) return Int(fire_cmpb_fail(FIRE_CMPB_ARGUMENT, "A byte buffer is expected."));
    if (!leafAlive(buffer)) return destroyedError(buffer);
    b = bufOf(buffer);
    for (i = (int64_t)b->length - 4; i >= 0; i--) {
        const uint8_t* p = b->data + i;
        if (((uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24)) == sig) return Int(i);
    }
    return Int(-1);
}

#endif
