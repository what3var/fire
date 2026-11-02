/* fire native bridge "io" (C): the natives behind `#import "io"` (SPEC 8.11) - streams (files, memory, the console), the file system and paths, UTF-8 text
 * (src/fire.IO.Bridge). The fire side (IO.FileStream, IO.File, IO.Path, ... - fire source) is the same as in the VM; this file is what its `__IO...` functions do.
 *
 * This file is the one implementation of the io natives, in C99 ("language": "c"): the native build puts it into the generated program, and the virtual machine
 * runs it in a shared library built from it (native/abi/fire_pkg_abi.h). The file system comes from the platform layer (FIRE_PLATFORM_FS_C_HEADER: fire_fs_*,
 * see platform/std/fire_fs_c.h). Every function reports an error the same way: a result of -1/false/undefined and the error code and message of this thread
 * (`__IOLastError`, `__IOLastErrorMessage`), from which the fire code throws a typed exception.
 *
 * What the host decides (the policy for paths and where the console goes) differs by build:
 *   - a native build: everything is allowed unless the target defines FIRE_IO_POLICY (a function `int(const char* fullPath, int access, char* reason, size_t
 *     size)`, 1 allowed; access bits 1 Read, 2 Write, 4 Delete, 8 List; define it before the includes in the target configuration); the console is C stdio;
 *   - in the library for the VM: the host (the editor, the runtime) is asked through the callbacks of the ABI (`fire_host`): `io_allow` for every path,
 *     `std_read`/`std_write`/`std_flush` for the console. Reading standard input waits until some input or the end of it is there. */
#ifndef FIRE_BRIDGE_IO_H
#define FIRE_BRIDGE_IO_H

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include FIRE_PLATFORM_FS_C_HEADER

enum { FIRE_IO_NONE = 0, FIRE_IO_INVALID_ARGUMENT = 1, FIRE_IO_INVALID_HANDLE = 2, FIRE_IO_FILE_NOT_FOUND = 3, FIRE_IO_DIRECTORY_NOT_FOUND = 4, FIRE_IO_PERMISSION = 5,
       FIRE_IO_DENIED = 6, FIRE_IO_ALREADY_EXISTS = 7, FIRE_IO_NOT_SUPPORTED = 8, FIRE_IO_OTHER = 9 };
enum { FIRE_IO_A_READ = 1, FIRE_IO_A_WRITE = 2, FIRE_IO_A_DELETE = 4, FIRE_IO_A_LIST = 8 };
#define FIRE_IO_END_OF_STREAM ((int64_t)-2)

static int64_t fire_io_fail(int code, const char* message) {
    g_ioError.code = code;
    snprintf(g_ioError.message, sizeof g_ioError.message, "%s", message);
    return -1;
}
static int64_t fire_io_failStatus(const FireFsStatus* s) { return fire_io_fail(s->code, s->message); }
static void fire_io_ok(void) { g_ioError.code = 0; g_ioError.message[0] = 0; }

/* ---- text: UTF-16 (fire strings) <-> UTF-8 ------------------------------------------------------------------------------------------------- */
/* A fire string as UTF-8 (malloc; the caller frees it); a lone surrogate becomes U+FFFD. */
static char* fire_io_utf8(Value v, size_t* length) {
    const Str* s;
    char* out;
    size_t n = 0;
    uint32_t i;
    if (v.kind != K_String) { out = (char*)malloc(1); if (out) out[0] = 0; if (length) *length = 0; return out; }
    s = strOf(v);
    out = (char*)malloc((size_t)s->length * 3 + 1);
    if (out == NULL) return NULL;
    for (i = 0; i < s->length; i++) {
        uint32_t c = s->data[i];
        if (c >= 0xD800 && c <= 0xDBFF && i + 1 < s->length && s->data[i + 1] >= 0xDC00 && s->data[i + 1] <= 0xDFFF) { c = 0x10000 + ((c - 0xD800) << 10) + (s->data[i + 1] - 0xDC00); i++; }
        else if (c >= 0xD800 && c <= 0xDFFF) c = 0xFFFD;
        if (c < 0x80) out[n++] = (char)c;
        else if (c < 0x800) { out[n++] = (char)(0xC0 | (c >> 6)); out[n++] = (char)(0x80 | (c & 0x3F)); }
        else if (c < 0x10000) { out[n++] = (char)(0xE0 | (c >> 12)); out[n++] = (char)(0x80 | ((c >> 6) & 0x3F)); out[n++] = (char)(0x80 | (c & 0x3F)); }
        else { out[n++] = (char)(0xF0 | (c >> 18)); out[n++] = (char)(0x80 | ((c >> 12) & 0x3F)); out[n++] = (char)(0x80 | ((c >> 6) & 0x3F)); out[n++] = (char)(0x80 | (c & 0x3F)); }
    }
    out[n] = 0;
    if (length) *length = n;
    return out;
}

/* Decodes UTF-8 into `out` (room for n code units; NULL: only count); every maximal invalid sequence becomes one U+FFFD (like .NET). The number of code units. */
static size_t fire_io_decode(const uint8_t* p, size_t n, fire_char16* out) {
    size_t i = 0, count = 0;
    while (i < n) {
        uint8_t b = p[i];
        uint32_t cp = 0;
        size_t len = 0, k;
        uint8_t lo = 0x80, hi = 0xBF;
        if (b < 0x80) { if (out) out[count] = b; count++; i++; continue; }
        if (b >= 0xC2 && b <= 0xDF) { len = 2; cp = b & 0x1F; }
        else if (b >= 0xE0 && b <= 0xEF) { len = 3; cp = b & 0x0F; if (b == 0xE0) lo = 0xA0; if (b == 0xED) hi = 0x9F; }
        else if (b >= 0xF0 && b <= 0xF4) { len = 4; cp = b & 0x07; if (b == 0xF0) lo = 0x90; if (b == 0xF4) hi = 0x8F; }
        else { if (out) out[count] = 0xFFFD; count++; i++; continue; }
        for (k = 1; k < len; k++) {
            uint8_t c;
            if (i + k >= n) break;
            c = p[i + k];
            if (c < (k == 1 ? lo : 0x80) || c > (k == 1 ? hi : 0xBF)) break;
            cp = (cp << 6) | (c & 0x3F);
        }
        if (k < len) { if (out) out[count] = 0xFFFD; count++; i += k; continue; }
        i += len;
        if (cp >= 0x10000) {
            cp -= 0x10000;
            if (out) { out[count] = (fire_char16)(0xD800 + (cp >> 10)); out[count + 1] = (fire_char16)(0xDC00 + (cp & 0x3FF)); }
            count += 2;
        } else { if (out) out[count] = (fire_char16)cp; count++; }
    }
    return count;
}

/* A string from UTF-8 (with the decoding above); `skipBom`: a byte order mark at the start is dropped. */
static Value fire_io_str8n(const uint8_t* p, size_t n, int skipBom, OwnList* list) {
    size_t count = fire_io_decode(p, n, NULL);
    Str* s = allocStr((uint32_t)count, list);
    fire_io_decode(p, n, strChars(s));
    if (skipBom && count > 0 && strChars(s)[0] == 0xFEFF) {
        Str* rest = allocStr((uint32_t)(count - 1), list);
        memcpy(strChars(rest), strChars(s) + 1, (count - 1) * sizeof(fire_char16));
        return StrV(rest);
    }
    return StrV(s);
}
static Value fire_io_str8(const char* text, OwnList* list) { return fire_io_str8n((const uint8_t*)text, strlen(text), 0, list); }
static Value fire_io_strArray(const FireFsList* items, OwnList* list) {
    Arr* a = allocArr((uint32_t)items->count, list);
    size_t i;
    for (i = 0; i < items->count; i++) { arrItems(a)[i] = fire_io_str8(items->items[i], list); retain(arrItems(a)[i]); }
    return ArrV(a);
}

/* ---- streams ----------------------------------------------------------------------------------------------------------------------------- */
enum { FIRE_IO_FILE = 0, FIRE_IO_MEMORY = 1, FIRE_IO_STD = 2 };
typedef struct FireStream {
    int kind;
    FILE* file;
    uint8_t* mem;
    int64_t memSize, memCap;
    int64_t pos;
    int canRead, canWrite, canSeek, permanent;
    int stdKind;
    int lastOp;   /* a file: 1 after a read, 2 after a write (the C library wants a seek between them) */
} FireStream;

/* The open streams; the handle is the index (0 is not used), handles are not used again. What the script left open is closed when the program ends. */
static FireStream** g_ioStreams = NULL;
static size_t g_ioStreamCount = 1, g_ioStreamCap = 0;
static int g_ioStdHandles[3] = {0, 0, 0};

static void fire_io_freeStream(FireStream* s) {
    if (s->kind == FIRE_IO_FILE && s->file) fclose(s->file);
    free(s->mem);
    free(s);
}

/* The program has ended (the library for the VM stays loaded for the next one): closes what it left open. The standard streams stay. */
static void fire_io_reset(void) {
    size_t i;
    for (i = 1; i < g_ioStreamCount; i++) {
        FireStream* s = g_ioStreams[i];
        if (s == NULL || s->permanent) continue;
        fire_io_freeStream(s);
        g_ioStreams[i] = NULL;
    }
}

static int64_t fire_io_add(FireStream* s) {
    if (g_ioStreamCount >= g_ioStreamCap) {
        size_t cap = g_ioStreamCap ? g_ioStreamCap * 2 : 16;
        FireStream** grown = (FireStream**)realloc(g_ioStreams, sizeof(FireStream*) * cap);
        if (grown == NULL) { fire_io_freeStream(s); return fire_io_fail(FIRE_IO_OTHER, "Out of memory."); }
        if (g_ioStreamCap == 0) grown[0] = NULL;
        g_ioStreams = grown;
        g_ioStreamCap = cap;
    }
    g_ioStreams[g_ioStreamCount] = s;
    return (int64_t)g_ioStreamCount++;
}
static FireStream* fire_io_newStream(int kind) {
    FireStream* s = (FireStream*)calloc(1, sizeof(FireStream));
    if (s != NULL) s->kind = kind;
    return s;
}
static FireStream* fire_io_find(Value h) {
    int64_t i = h.i;
    if (i > 0 && (size_t)i < g_ioStreamCount && g_ioStreams[i] != NULL) return g_ioStreams[i];
    fire_io_fail(FIRE_IO_INVALID_HANDLE, "Invalid or already closed stream handle.");
    return NULL;
}
static int fire_io_checkRange(Value bufferValue, int64_t offset, int64_t count) {
    int64_t length = bufOf(bufferValue)->length;
    if (offset < 0 || count < 0 || offset > length || count > length - offset) {
        char text[176];
        snprintf(text, sizeof text, "offset/count (%lld/%lld) are outside of the buffer (length %lld).", (long long)offset, (long long)count, (long long)length);
        fire_io_fail(FIRE_IO_INVALID_ARGUMENT, text);
        return 0;
    }
    return 1;
}

/* ---- the host: the policy for paths and the console ------------------------------------------------------------------------------------------ */
/* Is `access` on the full path allowed? A native build: yes, unless the target defines FIRE_IO_POLICY. The library for the VM asks the host. */
static int fire_io_allowed(const char* full, int access, char* reason, size_t size) {
#ifdef FIRE_LIBRARY
    const fire_host* host = libraryHost();
    if (!host || !host->io_allow) return 1;
    reason[0] = 0;
    return host->io_allow(full, access, reason, (int)size) ? 1 : 0;
#elif defined(FIRE_IO_POLICY)
    return FIRE_IO_POLICY(full, access, reason, size);
#else
    (void)full; (void)access; (void)reason; (void)size;
    return 1;
#endif
}

/* The console (standard input, output and error). A native build: C stdio. The library for the VM: the callbacks of the host (the editor shows the output in its
 * own window). */
#ifdef FIRE_LIBRARY
typedef struct FireIoInput { uint8_t buf[512]; int pos, len, end; } FireIoInput;
static FireIoInput g_ioInput = {{0}, 0, 0, 0};
static int fire_io_fill(void) {
    const fire_host* host;
    int n;
    if (g_ioInput.pos < g_ioInput.len) return 1;
    if (g_ioInput.end) return 0;
    host = libraryHost();
    n = host && host->std_read ? host->std_read(0, g_ioInput.buf, (int)sizeof g_ioInput.buf) : 0;
    if (n <= 0) { g_ioInput.end = 1; return 0; }
    g_ioInput.pos = 0; g_ioInput.len = n;
    return 1;
}
static int fire_io_getc(void) { return fire_io_fill() ? g_ioInput.buf[g_ioInput.pos++] : -1; }
static void fire_io_ungetc(int c) { (void)c; g_ioInput.pos--; }   /* only right after a getc */
/* Up to `count` bytes; 0 at the end of the input. */
static int64_t fire_io_consoleRead(uint8_t* out, int64_t count) {
    int64_t n;
    if (!fire_io_fill()) return 0;
    n = g_ioInput.len - g_ioInput.pos < count ? g_ioInput.len - g_ioInput.pos : count;
    memcpy(out, g_ioInput.buf + g_ioInput.pos, (size_t)n);
    g_ioInput.pos += (int)n;
    return n;
}
static int fire_io_consoleWrite(int kind, const uint8_t* data, int64_t count) {
    const fire_host* host = libraryHost();
    return !host || !host->std_write || count == 0 || host->std_write(kind, data, (int)count) >= 0;
}
static void fire_io_consoleFlush(int kind) { const fire_host* host = libraryHost(); if (host && host->std_flush) host->std_flush(kind); }
#else
static int fire_io_getc(void) { return getc(stdin); }
static void fire_io_ungetc(int c) { ungetc(c, stdin); }
static int64_t fire_io_consoleRead(uint8_t* out, int64_t count) { return (int64_t)fread(out, 1, (size_t)count, stdin); }
static int fire_io_consoleWrite(int kind, const uint8_t* data, int64_t count) {
    FILE* f = kind == 1 ? stdout : stderr;
    if (count > 0 && fwrite(data, 1, (size_t)count, f) != (size_t)count) { clearerr(f); return 0; }
    return 1;
}
static void fire_io_consoleFlush(int kind) { fflush(kind == 1 ? stdout : stderr); }
#endif

/* Is the path allowed for this program? Returns the full path (malloc) or NULL (the error is recorded). */
static char* fire_io_authorize(Value path, int access) {
    char* raw = fire_io_utf8(path, NULL);
    char* full;
    char reason[256];
    size_t i;
    int blank = 1;
    if (raw == NULL) { fire_io_fail(FIRE_IO_OTHER, "Out of memory."); return NULL; }
    for (i = 0; raw[i]; i++) if (raw[i] != ' ' && raw[i] != '\t' && raw[i] != '\n' && raw[i] != '\r') { blank = 0; break; }
    if (blank) { free(raw); fire_io_fail(FIRE_IO_INVALID_ARGUMENT, "The path is empty."); return NULL; }
    full = fire_fs_fullPath(raw);
    free(raw);
    if (full == NULL) { fire_io_fail(FIRE_IO_OTHER, "Out of memory."); return NULL; }
    reason[0] = 0;
    if (!fire_io_allowed(full, access, reason, sizeof reason)) {
        char text[176];
        if (reason[0]) snprintf(text, sizeof text, "%s", reason);
        else snprintf(text, sizeof text, "Access to '%s' is not allowed.", full);
        free(full);
        fire_io_fail(FIRE_IO_DENIED, text);
        return NULL;
    }
    return full;
}

static void fire_io_prepare(FireStream* s, int op) {
    if (s->kind == FIRE_IO_FILE && s->lastOp != 0 && s->lastOp != op) fire_fs_seek(s->file, 0, SEEK_CUR);
    if (s->kind == FIRE_IO_FILE) s->lastOp = op;
}

/* ---- the natives (called as fire_io_Name; the fire name is __IOName) ------------------------------------------------------------------------- */
static Value fire_io_LastError(void) { return Int(g_ioError.code); }
static Value fire_io_LastErrorMessage(OwnList* list) { return fire_io_str8(g_ioError.message, list); }
static Value fire_io_OpenCount(void) {
    int64_t n = 0;
    size_t i;
    for (i = 1; i < g_ioStreamCount; i++) if (g_ioStreams[i] != NULL && !g_ioStreams[i]->permanent) n++;
    return Int(n);
}

static Value fire_io_FileOpen(Value path, Value modeV, Value accessV) {
    int64_t mode = modeV.i, access = accessV.i;
    char text[176];
    char* full;
    FireFsStatus status;
    FILE* f;
    FireStream* s;
    int needed;
    if (mode < 0 || mode > 4) { snprintf(text, sizeof text, "Invalid FileMode %lld.", (long long)mode); return Int(fire_io_fail(FIRE_IO_INVALID_ARGUMENT, text)); }
    if (access < 0 || access > 2) { snprintf(text, sizeof text, "Invalid FileAccess %lld.", (long long)access); return Int(fire_io_fail(FIRE_IO_INVALID_ARGUMENT, text)); }
    if (mode == 4 && access != 1) return Int(fire_io_fail(FIRE_IO_INVALID_ARGUMENT, "FileMode.Append verlangt FileAccess.Write."));
    if ((mode == 1 || mode == 2) && access == 0) return Int(fire_io_fail(FIRE_IO_INVALID_ARGUMENT, "Creating a file requires write access."));
    needed = access == 0 ? FIRE_IO_A_READ : access == 1 ? FIRE_IO_A_WRITE : (FIRE_IO_A_READ | FIRE_IO_A_WRITE);
    full = fire_io_authorize(path, needed);
    if (full == NULL) return Int(-1);
    memset(&status, 0, sizeof status);
    f = fire_fs_open(full, (int)mode, (int)access, &status);
    free(full);
    if (f == NULL) return Int(fire_io_failStatus(&status));
    s = fire_io_newStream(FIRE_IO_FILE);
    if (s == NULL) { fclose(f); return Int(fire_io_fail(FIRE_IO_OTHER, "Out of memory.")); }
    s->file = f;
    s->canRead = access != 1;
    s->canWrite = access != 0;
    s->canSeek = 1;
    fire_io_ok();
    return Int(fire_io_add(s));
}
static Value fire_io_MemNew(void) {
    FireStream* s = fire_io_newStream(FIRE_IO_MEMORY);
    if (s == NULL) return Int(fire_io_fail(FIRE_IO_OTHER, "Out of memory."));
    s->canRead = s->canWrite = s->canSeek = 1;
    fire_io_ok();
    return Int(fire_io_add(s));
}
static int fire_io_memReserve(FireStream* s, int64_t size) {
    if (size > s->memCap) {
        int64_t cap = s->memCap ? s->memCap : 256;
        uint8_t* grown;
        while (cap < size) cap *= 2;
        grown = (uint8_t*)realloc(s->mem, (size_t)cap);
        if (grown == NULL) return 0;
        s->mem = grown;
        s->memCap = cap;
    }
    return 1;
}
static Value fire_io_MemFromBuffer(Value buffer) {
    FireStream* s;
    Buf* b;
    if (!leafAlive(buffer)) return destroyedError(buffer);
    s = fire_io_newStream(FIRE_IO_MEMORY);
    if (s == NULL) return Int(fire_io_fail(FIRE_IO_OTHER, "Out of memory."));
    s->canRead = s->canWrite = s->canSeek = 1;
    b = bufOf(buffer);
    if (b->length > 0) {
        if (!fire_io_memReserve(s, b->length)) { fire_io_freeStream(s); return Int(fire_io_fail(FIRE_IO_OTHER, "Out of memory.")); }
        memcpy(s->mem, b->data, b->length);
        s->memSize = b->length;
    }
    fire_io_ok();
    return Int(fire_io_add(s));
}
static Value fire_io_Close(Value h) {
    FireStream* s = fire_io_find(h);
    int good = 1;
    if (s == NULL) return Bool(false);
    if (s->permanent) { fire_io_ok(); return Bool(true); }
    if (s->kind == FIRE_IO_FILE && fclose(s->file) != 0) { fire_io_fail(FIRE_IO_OTHER, "The stream could not be closed."); good = 0; }
    s->file = NULL;
    g_ioStreams[h.i] = NULL;
    fire_io_freeStream(s);
    if (good) fire_io_ok();
    return Bool(good);
}

static int64_t fire_io_memRead(FireStream* s, uint8_t* out, int64_t count) {
    int64_t n = s->pos >= s->memSize ? 0 : (count < s->memSize - s->pos ? count : s->memSize - s->pos);
    if (n > 0) memcpy(out, s->mem + s->pos, (size_t)n);
    s->pos += n;
    return n;
}
static int fire_io_memWrite(FireStream* s, const uint8_t* data, int64_t count) {
    int64_t end = s->pos + count;
    if (end > s->memSize) {
        if (!fire_io_memReserve(s, end)) return 0;
        memset(s->mem + s->memSize, 0, (size_t)(end - s->memSize));
        s->memSize = end;
    }
    if (count > 0) memcpy(s->mem + s->pos, data, (size_t)count);
    s->pos = end;
    return 1;
}
/* Reads up to `count` bytes. -1: an error is recorded. */
static int64_t fire_io_readBytes(FireStream* s, uint8_t* out, int64_t count) {
    size_t n;
    if (!s->canRead) return fire_io_fail(FIRE_IO_NOT_SUPPORTED, "The stream is not readable.");
    if (count == 0) return 0;
    if (s->kind == FIRE_IO_MEMORY) return fire_io_memRead(s, out, count);
    if (s->kind == FIRE_IO_STD) { fire_io_consoleFlush(1); return fire_io_consoleRead(out, count); }
    fire_io_prepare(s, 1);
    n = fread(out, 1, (size_t)count, s->file);
    if (n < (size_t)count && ferror(s->file)) { clearerr(s->file); return fire_io_fail(FIRE_IO_OTHER, "The stream could not be read."); }
    return (int64_t)n;
}
static int64_t fire_io_writeBytes(FireStream* s, const uint8_t* data, int64_t count) {
    if (!s->canWrite) return fire_io_fail(FIRE_IO_NOT_SUPPORTED, "The stream is not writable.");
    if (s->kind == FIRE_IO_MEMORY) return fire_io_memWrite(s, data, count) ? count : fire_io_fail(FIRE_IO_OTHER, "Out of memory.");
    if (s->kind == FIRE_IO_STD) return fire_io_consoleWrite(s->stdKind, data, count) ? count : fire_io_fail(FIRE_IO_OTHER, "The stream could not be written.");
    fire_io_prepare(s, 2);
    if (count > 0 && fwrite(data, 1, (size_t)count, s->file) != (size_t)count) { clearerr(s->file); return fire_io_fail(FIRE_IO_OTHER, "The stream could not be written."); }
    return count;
}

static Value fire_io_Read(Value h, Value buffer, Value offset, Value count) {
    FireStream* s;
    int64_t n;
    if (!leafAlive(buffer)) return destroyedError(buffer);
    if (!fire_io_checkRange(buffer, offset.i, count.i)) return Int(-1);
    s = fire_io_find(h);
    if (s == NULL) return Int(-1);
    n = fire_io_readBytes(s, bufOf(buffer)->data + offset.i, count.i);
    if (n >= 0) fire_io_ok();
    return Int(n);
}
static Value fire_io_Write(Value h, Value buffer, Value offset, Value count) {
    FireStream* s;
    int64_t n;
    if (!leafAlive(buffer)) return destroyedError(buffer);
    if (!fire_io_checkRange(buffer, offset.i, count.i)) return Int(-1);
    s = fire_io_find(h);
    if (s == NULL) return Int(-1);
    n = fire_io_writeBytes(s, bufOf(buffer)->data + offset.i, count.i);
    if (n >= 0) fire_io_ok();
    return Int(n);
}
static Value fire_io_ReadByte(Value h) {
    FireStream* s = fire_io_find(h);
    uint8_t b;
    int64_t n;
    if (s == NULL) return Int(-1);
    n = fire_io_readBytes(s, &b, 1);
    if (n < 0) return Int(-1);
    fire_io_ok();
    return Int(n == 0 ? FIRE_IO_END_OF_STREAM : b);
}
static Value fire_io_WriteByte(Value h, Value value) {
    FireStream* s = fire_io_find(h);
    uint8_t b = (uint8_t)(value.i & 0xFF);
    int64_t n;
    if (s == NULL) return Int(-1);
    n = fire_io_writeBytes(s, &b, 1);
    if (n >= 0) fire_io_ok();
    return Int(n);
}
static Value fire_io_ReadRest(Value h, OwnList* list) {
    FireStream* s = fire_io_find(h);
    uint8_t* all = NULL;
    size_t size = 0, cap = 0;
    uint8_t chunk[1024];
    Buf* b;
    if (s == NULL) return Undef();
    if (!s->canRead) { fire_io_fail(FIRE_IO_NOT_SUPPORTED, "The stream is not readable."); return Undef(); }
    for (;;) {
        int64_t n = fire_io_readBytes(s, chunk, sizeof chunk);
        if (n < 0) { free(all); return Undef(); }
        if (n == 0) break;
        if (size + (size_t)n > cap) {
            size_t c = cap ? cap * 2 : 4096;
            uint8_t* grown;
            while (c < size + (size_t)n) c *= 2;
            grown = (uint8_t*)realloc(all, c);
            if (grown == NULL) { free(all); fire_io_fail(FIRE_IO_OTHER, "Out of memory."); return Undef(); }
            all = grown;
            cap = c;
        }
        memcpy(all + size, chunk, (size_t)n);
        size += (size_t)n;
    }
    b = allocBuf((uint32_t)size, list);
    if (size > 0) memcpy(b->data, all, size);
    free(all);
    fire_io_ok();
    return BufV(b);
}
static Value fire_io_Flush(Value h) {
    FireStream* s = fire_io_find(h);
    if (s == NULL) return Bool(false);
    if (s->kind == FIRE_IO_FILE) fflush(s->file);
    else if (s->kind == FIRE_IO_STD && s->stdKind != 0) fire_io_consoleFlush(s->stdKind);
    fire_io_ok();
    return Bool(true);
}

static int64_t fire_io_length(FireStream* s) {
    int64_t here, end;
    if (s->kind == FIRE_IO_MEMORY) return s->memSize;
    here = fire_fs_tell(s->file);
    fire_fs_seek(s->file, 0, SEEK_END);
    end = fire_fs_tell(s->file);
    fire_fs_seek(s->file, here, SEEK_SET);
    return end;
}
static Value fire_io_Seek(Value h, Value offsetV, Value originV) {
    FireStream* s = fire_io_find(h);
    int64_t origin, target, here, base;
    if (s == NULL) return Int(-1);
    if (!s->canSeek) return Int(fire_io_fail(FIRE_IO_NOT_SUPPORTED, "The stream does not support seeking."));
    origin = originV.i;
    target = offsetV.i;
    if (origin < 0 || origin > 2) {
        char text[96];
        snprintf(text, sizeof text, "Invalid seek origin %lld.", (long long)origin);
        return Int(fire_io_fail(FIRE_IO_INVALID_ARGUMENT, text));
    }
    here = s->kind == FIRE_IO_MEMORY ? s->pos : fire_fs_tell(s->file);
    base = origin == 0 ? 0 : origin == 1 ? here : fire_io_length(s);
    if (base + target < 0) return Int(fire_io_fail(FIRE_IO_INVALID_ARGUMENT, "The position would be before the start of the stream."));
    if (s->kind == FIRE_IO_MEMORY) { s->pos = base + target; fire_io_ok(); return Int(s->pos); }
    s->lastOp = 0;
    if (!fire_fs_seek(s->file, base + target, SEEK_SET)) return Int(fire_io_fail(FIRE_IO_OTHER, "The stream could not be positioned."));
    fire_io_ok();
    return Int(base + target);
}
static Value fire_io_Position(Value h) {
    FireStream* s = fire_io_find(h);
    if (s == NULL) return Int(-1);
    if (!s->canSeek) return Int(fire_io_fail(FIRE_IO_NOT_SUPPORTED, "The stream does not support a position."));
    fire_io_ok();
    return Int(s->kind == FIRE_IO_MEMORY ? s->pos : fire_fs_tell(s->file));
}
static Value fire_io_Length(Value h) {
    FireStream* s = fire_io_find(h);
    if (s == NULL) return Int(-1);
    if (!s->canSeek) return Int(fire_io_fail(FIRE_IO_NOT_SUPPORTED, "The stream does not know its length."));
    fire_io_ok();
    return Int(fire_io_length(s));
}
static Value fire_io_SetLength(Value h, Value lengthV) {
    FireStream* s = fire_io_find(h);
    if (s == NULL) return Bool(false);
    if (lengthV.i < 0) { fire_io_fail(FIRE_IO_INVALID_ARGUMENT, "The length must not be negative."); return Bool(false); }
    if (!s->canSeek || !s->canWrite) { fire_io_fail(FIRE_IO_NOT_SUPPORTED, "The length of this stream cannot be changed."); return Bool(false); }
    if (s->kind == FIRE_IO_MEMORY) {
        if (lengthV.i > s->memSize) {
            if (!fire_io_memReserve(s, lengthV.i)) { fire_io_fail(FIRE_IO_OTHER, "Out of memory."); return Bool(false); }
            memset(s->mem + s->memSize, 0, (size_t)(lengthV.i - s->memSize));
        }
        s->memSize = lengthV.i;
        if (s->pos > lengthV.i) s->pos = lengthV.i;
    } else {
        int64_t here;
        s->lastOp = 0;
        here = fire_fs_tell(s->file);
        if (!fire_fs_truncate(s->file, lengthV.i)) { fire_io_fail(FIRE_IO_OTHER, "The length of the file could not be changed."); return Bool(false); }
        if (here > lengthV.i) fire_fs_seek(s->file, lengthV.i, SEEK_SET);
    }
    fire_io_ok();
    return Bool(true);
}
static FireStream* fire_io_peek(Value h) { int64_t i = h.i; return i > 0 && (size_t)i < g_ioStreamCount ? g_ioStreams[i] : NULL; }
static Value fire_io_CanRead(Value h) { FireStream* s = fire_io_peek(h); return Bool(s != NULL && s->canRead); }
static Value fire_io_CanWrite(Value h) { FireStream* s = fire_io_peek(h); return Bool(s != NULL && s->canWrite); }
static Value fire_io_CanSeek(Value h) { FireStream* s = fire_io_peek(h); return Bool(s != NULL && s->canSeek); }
static Value fire_io_MemToBuffer(Value h, OwnList* list) {
    FireStream* s = fire_io_find(h);
    Buf* b;
    if (s == NULL) return Undef();
    if (s->kind != FIRE_IO_MEMORY) { fire_io_fail(FIRE_IO_NOT_SUPPORTED, "Only a MemoryStream can be read as a buffer."); return Undef(); }
    b = allocBuf((uint32_t)s->memSize, list);
    if (s->memSize > 0) memcpy(b->data, s->mem, (size_t)s->memSize);
    fire_io_ok();
    return BufV(b);
}

/* ---- files and directories --------------------------------------------------------------------------------------------------------------- */
static Value fire_io_FileExists(Value path) {
    char* full = fire_io_authorize(path, FIRE_IO_A_READ);
    int isDir = 0, there;
    if (full == NULL) return Int(-1);
    there = fire_fs_exists(full, &isDir) && !isDir;
    free(full);
    fire_io_ok();
    return Int(there ? 1 : 0);
}
static Value fire_io_FileSize(Value path) {
    char* full = fire_io_authorize(path, FIRE_IO_A_READ);
    int64_t size = 0;
    FireFsStatus st;
    int ok;
    if (full == NULL) return Int(-1);
    ok = fire_fs_fileSize(full, &size, &st);
    free(full);
    if (!ok) return Int(fire_io_failStatus(&st));
    fire_io_ok();
    return Int(size);
}
static Value fire_io_FileTime(Value path) {
    char* full = fire_io_authorize(path, FIRE_IO_A_READ);
    int isDir = 0, ok;
    int64_t seconds = 0;
    FireFsStatus st;
    if (full == NULL) return Undef();
    if (!fire_fs_exists(full, &isDir) || isDir) {
        char text[176];
        snprintf(text, sizeof text, "The file '%s' does not exist.", full);
        free(full);
        fire_io_fail(FIRE_IO_FILE_NOT_FOUND, text);
        return Undef();
    }
    ok = fire_fs_fileTime(full, &seconds, &st);
    free(full);
    if (!ok) { fire_io_failStatus(&st); return Undef(); }
    fire_io_ok();
    return Int(seconds);   /* the prelude gives it the unit `s` (units do not cross the package ABI) */
}
static Value fire_io_FileDelete(Value path) {
    char* full = fire_io_authorize(path, FIRE_IO_A_DELETE);
    FireFsStatus st;
    int ok;
    if (full == NULL) return Bool(false);
    ok = fire_fs_removeFile(full, &st);
    free(full);
    if (!ok) { fire_io_failStatus(&st); return Bool(false); }
    fire_io_ok();
    return Bool(true);
}
/* copy (move = 0) or move a file; the target first, like the VM */
static Value fire_io_copyOrMove(Value source, Value target, Value overwrite, int move) {
    char* to = fire_io_authorize(target, FIRE_IO_A_WRITE);
    char* from;
    int isDir = 0, ok;
    FireFsStatus st;
    if (to == NULL) return Bool(false);
    from = fire_io_authorize(source, move ? (FIRE_IO_A_READ | FIRE_IO_A_DELETE) : FIRE_IO_A_READ);
    if (from == NULL) { free(to); return Bool(false); }
    if (!overwrite.i && fire_fs_exists(to, &isDir)) {
        char text[176];
        snprintf(text, sizeof text, "The target file '%s' already exists.", to);
        free(to); free(from);
        fire_io_fail(FIRE_IO_ALREADY_EXISTS, text);
        return Bool(false);
    }
    ok = move ? fire_fs_moveFile(from, to, overwrite.i != 0, &st) : fire_fs_copyFile(from, to, overwrite.i != 0, &st);
    free(to); free(from);
    if (!ok) { fire_io_failStatus(&st); return Bool(false); }
    fire_io_ok();
    return Bool(true);
}
static Value fire_io_FileCopy(Value source, Value target, Value overwrite) { return fire_io_copyOrMove(source, target, overwrite, 0); }
static Value fire_io_FileMove(Value source, Value target, Value overwrite) { return fire_io_copyOrMove(source, target, overwrite, 1); }
static Value fire_io_DirExists(Value path) {
    char* full = fire_io_authorize(path, FIRE_IO_A_READ);
    int isDir = 0, there;
    if (full == NULL) return Int(-1);
    there = fire_fs_exists(full, &isDir) && isDir;
    free(full);
    fire_io_ok();
    return Int(there ? 1 : 0);
}
static Value fire_io_DirCreate(Value path) {
    char* full = fire_io_authorize(path, FIRE_IO_A_WRITE);
    FireFsStatus st;
    int ok;
    if (full == NULL) return Bool(false);
    ok = fire_fs_makeDirs(full, &st);
    free(full);
    if (!ok) { fire_io_failStatus(&st); return Bool(false); }
    fire_io_ok();
    return Bool(true);
}
static Value fire_io_DirDelete(Value path, Value recursive) {
    char* full = fire_io_authorize(path, FIRE_IO_A_DELETE);
    FireFsStatus st;
    int ok;
    if (full == NULL) return Bool(false);
    ok = fire_fs_removeDir(full, recursive.i != 0, &st);
    free(full);
    if (!ok) { fire_io_failStatus(&st); return Bool(false); }
    fire_io_ok();
    return Bool(true);
}
static Value fire_io_DirList(Value path, Value pattern, Value recursive, Value kind, OwnList* list) {
    char* full = fire_io_authorize(path, FIRE_IO_A_LIST);
    char* pat;
    FireFsList entries = {NULL, 0, 0};
    FireFsStatus st;
    int ok;
    Value result;
    if (full == NULL) return Undef();
    pat = fire_io_utf8(pattern, NULL);
    ok = pat != NULL && fire_fs_list(full, pat, recursive.i != 0, kind.i != 0, &entries, &st);
    free(full); free(pat);
    if (!ok) { fire_fs_listFree(&entries); fire_io_failStatus(&st); return Undef(); }
    fire_io_ok();
    result = fire_io_strArray(&entries, list);
    fire_fs_listFree(&entries);
    return result;
}
static Value fire_io_takeText(char* text, OwnList* list) {
    Value v = fire_io_str8(text ? text : "", list);
    free(text);
    return v;
}
static Value fire_io_CurrentDir(OwnList* list) { fire_io_ok(); return fire_io_takeText(fire_fs_currentDir(), list); }
/* The time of the last change of a file or a directory (unix seconds; undefined on an error). */
static Value fire_io_EntryTime(Value path) {
    char* full = fire_io_authorize(path, FIRE_IO_A_READ);
    int64_t seconds = 0;
    FireFsStatus st;
    int ok;
    if (full == NULL) return Undef();
    ok = fire_fs_entryTime(full, &seconds, &st);
    free(full);
    if (!ok) { fire_io_failStatus(&st); return Undef(); }
    fire_io_ok();
    return Int(seconds);
}
static Value fire_io_DirMove(Value source, Value target) {
    char* to = fire_io_authorize(target, FIRE_IO_A_WRITE);
    char* from;
    FireFsStatus st;
    int ok;
    if (to == NULL) return Bool(false);
    from = fire_io_authorize(source, FIRE_IO_A_READ | FIRE_IO_A_DELETE);
    if (from == NULL) { free(to); return Bool(false); }
    ok = fire_fs_moveDir(from, to, &st);
    free(to); free(from);
    if (!ok) { fire_io_failStatus(&st); return Bool(false); }
    fire_io_ok();
    return Bool(true);
}
static Value fire_io_SetCurrentDir(Value path) {
    char* full = fire_io_authorize(path, FIRE_IO_A_LIST);
    FireFsStatus st;
    int ok;
    if (full == NULL) return Bool(false);
    ok = fire_fs_setCurrentDir(full, &st);
    free(full);
    if (!ok) { fire_io_failStatus(&st); return Bool(false); }
    fire_io_ok();
    return Bool(true);
}
/* The drives (Windows) or the root. */
static Value fire_io_Roots(OwnList* list) {
    FireFsList roots = {NULL, 0, 0};
    Value v;
    fire_fs_roots(&roots);
    fire_io_ok();
    v = fire_io_strArray(&roots, list);
    fire_fs_listFree(&roots);
    return v;
}
static Value fire_io_HomeDir(OwnList* list) { fire_io_ok(); return fire_io_takeText(fire_fs_homeDir(), list); }

/* ---- paths (text only, no file access) --------------------------------------------------------------------------------------------------- */
static int fire_io_rooted(const char* p) {
#ifdef _WIN32
    return (p[0] && fire_fs_isSep(p[0])) || (p[0] && p[1] == ':');
#else
    return p[0] == '/';
#endif
}
static const char* fire_io_fileName(const char* p) {
    size_t i = strlen(p);
    while (i > 0 && !fire_fs_isSep(p[i - 1])) i--;
    return p + i;
}
static Value fire_io_PathCombine(Value a, Value b, OwnList* list) {
    size_t xn, yn;
    char* x = fire_io_utf8(a, &xn);
    char* y = fire_io_utf8(b, &yn);
    Value v;
    fire_io_ok();
    if (x == NULL || y == NULL) { free(x); free(y); return fire_io_str8("", list); }
    if (fire_io_rooted(y) || xn == 0) v = fire_io_str8(y, list);
    else if (yn == 0) v = fire_io_str8(x, list);
    else {
        char* joined = (char*)malloc(xn + yn + 2);
        if (joined == NULL) v = fire_io_str8(y, list);
        else {
            memcpy(joined, x, xn);
            if (!fire_fs_isSep(x[xn - 1])) joined[xn++] = fire_fs_separator()[0];
            memcpy(joined + xn, y, yn + 1);
            v = fire_io_str8(joined, list);
            free(joined);
        }
    }
    free(x); free(y);
    return v;
}
static Value fire_io_PathFileName(Value p, OwnList* list) {
    char* s = fire_io_utf8(p, NULL);
    Value v = fire_io_str8(s ? fire_io_fileName(s) : "", list);
    free(s);
    fire_io_ok();
    return v;
}
/* the name without (stem) or with only (extension) its last `.xyz` */
static Value fire_io_nameParts(Value p, int extension, OwnList* list) {
    char* s = fire_io_utf8(p, NULL);
    const char* name;
    const char* dot;
    Value v;
    fire_io_ok();
    if (s == NULL) return fire_io_str8("", list);
    name = fire_io_fileName(s);
    dot = strrchr(name, '.');
    if (extension) v = fire_io_str8(dot == NULL || dot[1] == 0 ? "" : dot, list);
    else v = fire_io_str8n((const uint8_t*)name, dot == NULL ? strlen(name) : (size_t)(dot - name), 0, list);
    free(s);
    return v;
}
static Value fire_io_PathStem(Value p, OwnList* list) { return fire_io_nameParts(p, 0, list); }
static Value fire_io_PathExtension(Value p, OwnList* list) { return fire_io_nameParts(p, 1, list); }
static Value fire_io_PathParent(Value pv, OwnList* list) {
    size_t n, root = 0, end;
    char* p = fire_io_utf8(pv, &n);
    Value v;
    fire_io_ok();
    if (p == NULL) return fire_io_str8("", list);
#ifdef _WIN32
    if (n >= 3 && p[1] == ':' && fire_fs_isSep(p[2])) root = 3; else if (n >= 2 && p[1] == ':') root = 2; else if (n > 0 && fire_fs_isSep(p[0])) root = 1;
#else
    if (n > 0 && p[0] == '/') root = 1;
#endif
    if (n <= root) { free(p); return fire_io_str8("", list); }
    end = n;
    while (end > root && !fire_fs_isSep(p[--end])) {}
    while (end > root && fire_fs_isSep(p[end - 1])) end--;
    v = fire_io_str8n((const uint8_t*)p, end, 0, list);
    free(p);
    return v;
}
static Value fire_io_PathFull(Value p, OwnList* list) {
    size_t n;
    char* raw = fire_io_utf8(p, &n);
    if (raw == NULL || n == 0) { free(raw); fire_io_fail(FIRE_IO_INVALID_ARGUMENT, "The path is empty."); return Undef(); }
    fire_io_ok();
    {
        char* full = fire_fs_fullPath(raw);
        free(raw);
        return fire_io_takeText(full, list);
    }
}
static Value fire_io_PathTemp(OwnList* list) { fire_io_ok(); return fire_io_takeText(fire_fs_tempDir(), list); }
static Value fire_io_PathSeparator(OwnList* list) { return fire_io_str8(fire_fs_separator(), list); }
static Value fire_io_PathIsRooted(Value p) {
    char* s = fire_io_utf8(p, NULL);
    int r = s != NULL && fire_io_rooted(s);
    free(s);
    return Bool(r != 0);
}

/* ---- the console --------------------------------------------------------------------------------------------------------------------------- */
static Value fire_io_StdHandle(Value kind) {
    int k;
    if (kind.i < 0 || kind.i > 2) {
        char text[96];
        snprintf(text, sizeof text, "Invalid standard stream %lld.", (long long)kind.i);
        return Int(fire_io_fail(FIRE_IO_INVALID_ARGUMENT, text));
    }
    k = (int)kind.i;
    if (g_ioStdHandles[k] == 0) {
        FireStream* s = fire_io_newStream(FIRE_IO_STD);
        int64_t h;
        if (s == NULL) return Int(fire_io_fail(FIRE_IO_OTHER, "Out of memory."));
        s->stdKind = k;
        s->permanent = 1;
        s->canRead = k == 0;
        s->canWrite = k != 0;
        h = fire_io_add(s);
        if (h < 0) return Int(-1);
        g_ioStdHandles[k] = (int)h;
    }
    fire_io_ok();
    return Int(g_ioStdHandles[k]);
}
static Value fire_io_stdWrite(int64_t kind, const char* bytes, size_t n, int flush) {
    if (kind != 1 && kind != 2) {
        char text[96];
        snprintf(text, sizeof text, "Invalid output stream %lld.", (long long)kind);
        fire_io_fail(FIRE_IO_INVALID_ARGUMENT, text);
        return Bool(false);
    }
    if (n > 0 && !fire_io_consoleWrite((int)kind, (const uint8_t*)bytes, (int64_t)n)) { fire_io_fail(FIRE_IO_OTHER, "The console could not be written."); return Bool(false); }
    if (flush) fire_io_consoleFlush((int)kind);
    fire_io_ok();
    return Bool(true);
}
static Value fire_io_StdWrite(Value kind, Value text) {
    size_t n;
    char* bytes = fire_io_utf8(text, &n);
    Value r = fire_io_stdWrite(kind.i, bytes ? bytes : "", bytes ? n : 0, 0);
    free(bytes);
    return r;
}
static Value fire_io_StdFlush(Value kind) { return fire_io_stdWrite(kind.i, "", 0, 1); }

/* a growing byte text (malloc) */
typedef struct FireIoBytes { uint8_t* data; size_t size, cap; } FireIoBytes;
static void fire_io_bytesPut(FireIoBytes* b, const uint8_t* p, size_t n) {
    if (b->size + n > b->cap) {
        size_t cap = b->cap ? b->cap * 2 : 256;
        uint8_t* grown;
        while (cap < b->size + n) cap *= 2;
        grown = (uint8_t*)realloc(b->data, cap);
        if (grown == NULL) return;
        b->data = grown;
        b->cap = cap;
    }
    memcpy(b->data + b->size, p, n);
    b->size += n;
}

/* A line of the standard input without the line break; undefined at the end of the input. */
static Value fire_io_StdReadLine(OwnList* list) {
    FireIoBytes line = {NULL, 0, 0};
    Value v;
    int c;
    fire_io_consoleFlush(1);
    c = fire_io_getc();
    if (c < 0) { fire_io_ok(); return Undef(); }
    while (c >= 0 && c != '\n' && c != '\r') { uint8_t b = (uint8_t)c; fire_io_bytesPut(&line, &b, 1); c = fire_io_getc(); }
    if (c == '\r') {   /* a line break of two characters (\r\n): the second one belongs to it */
        int next = fire_io_getc();
        if (next >= 0 && next != '\n') fire_io_ungetc(next);
    }
    fire_io_ok();
    v = fire_io_str8n(line.data ? line.data : (const uint8_t*)"", line.size, 0, list);
    free(line.data);
    return v;
}
static Value fire_io_StdReadAll(OwnList* list) {
    FireIoBytes all = {NULL, 0, 0};
    uint8_t chunk[512];
    int64_t n;
    Value v;
    fire_io_consoleFlush(1);
    while ((n = fire_io_consoleRead(chunk, (int64_t)sizeof chunk)) > 0) fire_io_bytesPut(&all, chunk, (size_t)n);
    fire_io_ok();
    v = fire_io_str8n(all.data ? all.data : (const uint8_t*)"", all.size, 0, list);
    free(all.data);
    return v;
}

/* ---- buffers and text ------------------------------------------------------------------------------------------------------------------------ */
static Value fire_io_BufferIndexOf(Value buffer, Value offset, Value count, Value value) {
    const uint8_t* start;
    const void* hit;
    if (!leafAlive(buffer)) return destroyedError(buffer);
    if (!fire_io_checkRange(buffer, offset.i, count.i)) return Int(-1);
    start = bufOf(buffer)->data + offset.i;
    hit = count.i > 0 ? memchr(start, (int)(value.i & 0xFF), (size_t)count.i) : NULL;
    fire_io_ok();
    return Int(hit ? offset.i + ((const uint8_t*)hit - start) : -1);
}
static Value fire_io_Utf8Encode(Value text, OwnList* list) {
    size_t n = 0;
    char* bytes = fire_io_utf8(text, &n);
    Buf* b = allocBuf((uint32_t)(bytes ? n : 0), list);
    if (bytes && n > 0) memcpy(b->data, bytes, n);
    free(bytes);
    return BufV(b);
}
static Value fire_io_Utf8Decode(Value buffer, Value offset, Value count, OwnList* list) {
    if (!leafAlive(buffer)) return destroyedError(buffer);
    if (!fire_io_checkRange(buffer, offset.i, count.i)) return Undef();
    fire_io_ok();
    return fire_io_str8n(bufOf(buffer)->data + offset.i, (size_t)count.i, 1, list);   /* (a byte order mark at the start is dropped) */
}
/* Lines (\n, \r\n, \r); a line break at the end does not make an empty last line. */
static Value fire_io_SplitLines(Value text, OwnList* list) {
    const Str* s = strOf(text);
    uint32_t i, start = 0, lines = 0, k = 0;
    int open = 0;
    Arr* a;
    /* first count, then make them */
    for (i = 0; i < s->length; i++) {
        fire_char16 c = s->data[i];
        if (c == '\n' || c == '\r') { if (c == '\r' && i + 1 < s->length && s->data[i + 1] == '\n') i++; lines++; open = 0; }
        else open = 1;
    }
    if (open) lines++;
    a = allocArr(lines, list);
    for (i = 0; i < s->length; i++) {
        fire_char16 c = s->data[i];
        if (c == '\n' || c == '\r') {
            Str* line = allocStr(i - start, list);
            if (i > start) memcpy(strChars(line), s->data + start, (i - start) * sizeof(fire_char16));
            arrItems(a)[k] = StrV(line); retain(arrItems(a)[k]); k++;
            if (c == '\r' && i + 1 < s->length && s->data[i + 1] == '\n') i++;
            start = i + 1;
        }
    }
    if (open) {
        Str* line = allocStr(s->length - start, list);
        if (s->length > start) memcpy(strChars(line), s->data + start, (s->length - start) * sizeof(fire_char16));
        arrItems(a)[k] = StrV(line); retain(arrItems(a)[k]);
    }
    fire_io_ok();
    return ArrV(a);
}

#endif
