// fire native bridge "io": the natives behind `#import "io"` (SPEC 8.11) - streams (files, memory, the console), the file system and paths, UTF-8 text
// (src/fire.IO.Bridge). The fire side (IO.FileStream, IO.File, IO.Path, ... - fire source) is the same as in the VM; this file is what its `__IO...` functions do.
//
// Included by the generated file (after fire_rt.hpp) when the program imports "io". The file system comes from the platform package (FIRE_PLATFORM_FS_HEADER:
// plat::fs, see platform/std/fire_fs_std.hpp), the console is C stdio. Every function reports an error like the VM's: a result of -1/false/undefined and the
// error code and message of this thread (`__IOLastError`, `__IOLastErrorMessage`), from which the fire code throws a typed exception.
//
// Differences to the VM: a program can restrict what it may touch with FIRE_IO_POLICY (a function `bool(const std::string& fullPath, int access, std::string& reason)`,
// access bits 1 Read, 2 Write, 4 Delete, 8 List; define it before the includes in the target configuration) - the default is: everything; reading standard input waits
// until the requested number of bytes or the end of the input.
#pragma once

#include <cstring>
#include FIRE_PLATFORM_FS_HEADER
#include <string>
#include <vector>

#ifndef FIRE_UNIT_SECONDS
#error "fire bridge io: the generated file defines FIRE_UNIT_SECONDS (the index of the unit `s`)"
#endif

namespace fire {
namespace io {

enum Err { None = 0, InvalidArgument = 1, InvalidHandle = 2, FileNotFound = 3, DirectoryNotFound = 4, Permission = 5, Denied = 6, AlreadyExists = 7, NotSupported = 8, Other = 9 };
enum Access { A_Read = 1, A_Write = 2, A_Delete = 4, A_List = 8 };
constexpr int64_t END_OF_STREAM = -2;

inline int64_t fail(int code, const std::string& message) {
    g_ioError.code = code;
    size_t n = message.size() < sizeof g_ioError.message - 1 ? message.size() : sizeof g_ioError.message - 1;
    std::memcpy(g_ioError.message, message.data(), n);
    g_ioError.message[n] = 0;
    return -1;
}
inline void ok() { g_ioError.code = 0; g_ioError.message[0] = 0; }
inline int64_t fail(const plat::fs::Status& s) { return fail(s.code, s.message); }

// ---- text: UTF-16 (fire strings) <-> UTF-8 ----------------------------------------------------------------------------------------------
inline std::string toUtf8(const char16_t* s, uint32_t n) {
    std::string out;
    out.reserve(n);
    for (uint32_t i = 0; i < n; i++) {
        uint32_t c = s[i];
        if (c >= 0xD800 && c <= 0xDBFF && i + 1 < n && s[i + 1] >= 0xDC00 && s[i + 1] <= 0xDFFF) { c = 0x10000 + ((c - 0xD800) << 10) + (s[i + 1] - 0xDC00); i++; }
        else if (c >= 0xD800 && c <= 0xDFFF) c = 0xFFFD;   // a lone surrogate
        if (c < 0x80) out.push_back((char)c);
        else if (c < 0x800) { out.push_back((char)(0xC0 | (c >> 6))); out.push_back((char)(0x80 | (c & 0x3F))); }
        else if (c < 0x10000) { out.push_back((char)(0xE0 | (c >> 12))); out.push_back((char)(0x80 | ((c >> 6) & 0x3F))); out.push_back((char)(0x80 | (c & 0x3F))); }
        else { out.push_back((char)(0xF0 | (c >> 18))); out.push_back((char)(0x80 | ((c >> 12) & 0x3F))); out.push_back((char)(0x80 | ((c >> 6) & 0x3F))); out.push_back((char)(0x80 | (c & 0x3F))); }
    }
    return out;
}
inline std::string toUtf8(Value v) { const Str* s = strOf(v); return toUtf8(s->data, s->length); }

/// Decodes UTF-8; every maximal invalid sequence becomes one U+FFFD (like .NET).
inline std::u16string fromUtf8(const uint8_t* p, size_t n) {
    std::u16string out;
    out.reserve(n);
    auto cont = [&](size_t k, uint8_t lo, uint8_t hi) { return k < n && p[k] >= lo && p[k] <= hi; };
    size_t i = 0;
    while (i < n) {
        uint8_t b = p[i];
        if (b < 0x80) { out.push_back(b); i++; continue; }
        uint32_t cp = 0;
        size_t len = 0;
        uint8_t lo = 0x80, hi = 0xBF;
        if (b >= 0xC2 && b <= 0xDF) { len = 2; cp = b & 0x1F; }
        else if (b >= 0xE0 && b <= 0xEF) { len = 3; cp = b & 0x0F; if (b == 0xE0) lo = 0xA0; if (b == 0xED) hi = 0x9F; }
        else if (b >= 0xF0 && b <= 0xF4) { len = 4; cp = b & 0x07; if (b == 0xF0) lo = 0x90; if (b == 0xF4) hi = 0x8F; }
        else { out.push_back(0xFFFD); i++; continue; }
        size_t k = 1;
        for (; k < len; k++) {
            if (!cont(i + k, k == 1 ? lo : 0x80, k == 1 ? hi : 0xBF)) break;
            cp = (cp << 6) | (p[i + k] & 0x3F);
        }
        if (k < len) { out.push_back(0xFFFD); i += k; continue; }
        i += len;
        if (cp >= 0x10000) { cp -= 0x10000; out.push_back((char16_t)(0xD800 + (cp >> 10))); out.push_back((char16_t)(0xDC00 + (cp & 0x3FF))); }
        else out.push_back((char16_t)cp);
    }
    return out;
}
inline std::u16string fromUtf8(const std::string& s) { return fromUtf8(reinterpret_cast<const uint8_t*>(s.data()), s.size()); }

inline Value str(const std::u16string& text, OwnList* list) {
    Str* s = allocStr((uint32_t)text.size(), list);
    if (!text.empty()) std::memcpy(strChars(s), text.data(), text.size() * sizeof(char16_t));
    return StrV(s);
}
inline Value str8(const std::string& text, OwnList* list) { return str(fromUtf8(text), list); }
inline Value strArray(const std::vector<std::string>& items, OwnList* list) {
    Arr* a = allocArr((uint32_t)items.size(), list);
    for (size_t i = 0; i < items.size(); i++) { a->items()[i] = str8(items[i], list); retain(a->items()[i]); }
    return ArrV(a);
}

// ---- streams ----------------------------------------------------------------------------------------------------------------------------
struct Stream {
    enum Kind { File, Memory, Std } kind = File;
    std::FILE* file = nullptr;
    std::vector<uint8_t> mem;
    int64_t pos = 0;
    bool canRead = false, canWrite = false, canSeek = false, permanent = false;
    int stdKind = 0;
    int lastOp = 0;   // a file: 1 after a read, 2 after a write (the C library wants a seek between them)
};
/// The open streams; the handle is the index, handles are not used again. What the script left open is closed when the program ends (the safety net of the VM's host).
struct StreamTable {
    std::vector<Stream*> items{1, nullptr};
    ~StreamTable() {
        for (Stream* s : items) {
            if (!s) continue;
            if (s->kind == Stream::File && s->file) std::fclose(s->file);
            delete s;
        }
    }
};
inline std::vector<Stream*>& streams() { static StreamTable table; return table.items; }
inline int g_stdHandles[3] = {0, 0, 0};

inline int add(Stream* s) { streams().push_back(s); return (int)streams().size() - 1; }
inline Stream* find(Value h) {
    int64_t i = h.i;
    if (i > 0 && (size_t)i < streams().size() && streams()[(size_t)i]) return streams()[(size_t)i];
    fail(InvalidHandle, "Invalid or already closed stream handle.");
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

/// Is the path allowed for this program? Returns the full path.
inline bool authorize(Value path, int access, std::string& full) {
    std::string raw = toUtf8(path);
    bool blank = true;
    for (char c : raw) if (c != ' ' && c != '\t' && c != '\n' && c != '\r') { blank = false; break; }
    if (blank) { fail(InvalidArgument, "The path is empty."); return false; }
    full = plat::fs::fullPath(raw);
#ifdef FIRE_IO_POLICY
    std::string reason;
    if (!FIRE_IO_POLICY(full, access, reason)) { fail(Denied, reason.empty() ? "Access to '" + full + "' is not allowed." : reason); return false; }
#else
    (void)access;
#endif
    return true;
}

inline void prepare(Stream* s, int op) {
    if (s->kind == Stream::File && s->lastOp != 0 && s->lastOp != op) plat::fs::seek(s->file, 0, SEEK_CUR);
    if (s->kind == Stream::File) s->lastOp = op;
}

inline std::FILE* stdFile(const Stream* s) { return s->stdKind == 0 ? stdin : s->stdKind == 1 ? stdout : stderr; }

// ---- the natives (called from the generated code as `io::Name`, the name is the part after `__IO`) ----------------------------------------------

inline Value LastError() { return Int(g_ioError.code); }
inline Value LastErrorMessage(OwnList* list) { return str8(g_ioError.message, list); }
inline Value OpenCount() {
    int64_t n = 0;
    for (Stream* s : streams()) if (s && !s->permanent) n++;
    return Int(n);
}

inline Value FileOpen(Value path, Value modeV, Value accessV) {
    int64_t mode = modeV.i, access = accessV.i;
    if (mode < 0 || mode > 4) return Int(fail(InvalidArgument, "Invalid FileMode " + std::to_string(mode) + "."));
    if (access < 0 || access > 2) return Int(fail(InvalidArgument, "Invalid FileAccess " + std::to_string(access) + "."));
    if (mode == 4 && access != 1) return Int(fail(InvalidArgument, "FileMode.Append verlangt FileAccess.Write."));
    if ((mode == 1 || mode == 2) && access == 0) return Int(fail(InvalidArgument, "Creating a file requires write access."));
    std::string full;
    int needed = access == 0 ? A_Read : access == 1 ? A_Write : (A_Read | A_Write);
    if (!authorize(path, needed, full)) return Int(-1);
    plat::fs::Status status;
    std::FILE* f = plat::fs::open(full, (int)mode, (int)access, status);
    if (!f) return Int(fail(status));
    Stream* s = new Stream();
    s->kind = Stream::File;
    s->file = f;
    s->canRead = access != 1;
    s->canWrite = access != 0;
    s->canSeek = true;
    ok();
    return Int(add(s));
}
inline Value MemNew() {
    Stream* s = new Stream();
    s->kind = Stream::Memory;
    s->canRead = s->canWrite = s->canSeek = true;
    ok();
    return Int(add(s));
}
inline Value MemFromBuffer(Value buffer) {
    if (!leafAlive(buffer)) return destroyedError(buffer);
    Stream* s = new Stream();
    s->kind = Stream::Memory;
    s->canRead = s->canWrite = s->canSeek = true;
    Buf* b = bufOf(buffer);
    s->mem.assign(b->bytes(), b->bytes() + b->length);
    ok();
    return Int(add(s));
}
inline Value Close(Value h) {
    Stream* s = find(h);
    if (!s) return Bool(false);
    if (s->permanent) { ok(); return Bool(true); }
    bool good = true;
    if (s->kind == Stream::File && std::fclose(s->file) != 0) { fail(Other, "The stream could not be closed."); good = false; }
    streams()[(size_t)h.i] = nullptr;
    delete s;
    if (good) ok();
    return Bool(good);
}

inline int64_t memRead(Stream* s, uint8_t* out, int64_t count) {
    int64_t size = (int64_t)s->mem.size();
    int64_t n = s->pos >= size ? 0 : (count < size - s->pos ? count : size - s->pos);
    if (n > 0) std::memcpy(out, s->mem.data() + s->pos, (size_t)n);
    s->pos += n;
    return n;
}
inline void memWrite(Stream* s, const uint8_t* data, int64_t count) {
    int64_t end = s->pos + count;
    if (end > (int64_t)s->mem.size()) s->mem.resize((size_t)end, 0);
    if (count > 0) std::memcpy(s->mem.data() + s->pos, data, (size_t)count);
    s->pos = end;
}
/// Reads up to `count` bytes. -1: an error is recorded.
inline int64_t readBytes(Stream* s, uint8_t* out, int64_t count) {
    if (!s->canRead) return fail(NotSupported, "The stream is not readable.");
    if (count == 0) return 0;
    if (s->kind == Stream::Memory) return memRead(s, out, count);
    if (s->kind == Stream::File) prepare(s, 1);
    std::FILE* f = s->kind == Stream::File ? s->file : stdFile(s);
    if (s->kind == Stream::Std) std::fflush(stdout);
    size_t n = std::fread(out, 1, (size_t)count, f);
    if (n < (size_t)count && std::ferror(f)) { std::clearerr(f); return fail(Other, "The stream could not be read."); }
    return (int64_t)n;
}
inline int64_t writeBytes(Stream* s, const uint8_t* data, int64_t count) {
    if (!s->canWrite) return fail(NotSupported, "The stream is not writable.");
    if (s->kind == Stream::Memory) { memWrite(s, data, count); return count; }
    if (s->kind == Stream::File) prepare(s, 2);
    std::FILE* f = s->kind == Stream::File ? s->file : stdFile(s);
    if (count > 0 && std::fwrite(data, 1, (size_t)count, f) != (size_t)count) { std::clearerr(f); return fail(Other, "The stream could not be written."); }
    return count;
}

inline Value Read(Value h, Value buffer, Value offset, Value count) {
    if (!leafAlive(buffer)) return destroyedError(buffer);
    if (!checkRange(buffer, offset.i, count.i)) return Int(-1);
    Stream* s = find(h);
    if (!s) return Int(-1);
    int64_t n = readBytes(s, bufOf(buffer)->bytes() + offset.i, count.i);
    if (n >= 0) ok();
    return Int(n);
}
inline Value Write(Value h, Value buffer, Value offset, Value count) {
    if (!leafAlive(buffer)) return destroyedError(buffer);
    if (!checkRange(buffer, offset.i, count.i)) return Int(-1);
    Stream* s = find(h);
    if (!s) return Int(-1);
    int64_t n = writeBytes(s, bufOf(buffer)->bytes() + offset.i, count.i);
    if (n >= 0) ok();
    return Int(n);
}
inline Value ReadByte(Value h) {
    Stream* s = find(h);
    if (!s) return Int(-1);
    uint8_t b;
    int64_t n = readBytes(s, &b, 1);
    if (n < 0) return Int(-1);
    ok();
    return Int(n == 0 ? END_OF_STREAM : b);
}
inline Value WriteByte(Value h, Value value) {
    Stream* s = find(h);
    if (!s) return Int(-1);
    uint8_t b = (uint8_t)(value.i & 0xFF);
    int64_t n = writeBytes(s, &b, 1);
    if (n >= 0) ok();
    return Int(n);
}
inline Value ReadRest(Value h, OwnList* list) {
    Stream* s = find(h);
    if (!s) return Undef();
    if (!s->canRead) { fail(NotSupported, "The stream is not readable."); return Undef(); }
    std::vector<uint8_t> all;
    uint8_t chunk[1024];
    while (true) {
        int64_t n = readBytes(s, chunk, sizeof chunk);
        if (n < 0) return Undef();
        if (n == 0) break;
        all.insert(all.end(), chunk, chunk + n);
    }
    Buf* b = allocBuf((uint32_t)all.size(), list);
    if (!all.empty()) std::memcpy(b->bytes(), all.data(), all.size());
    ok();
    return BufV(b);
}
inline Value Flush(Value h) {
    Stream* s = find(h);
    if (!s) return Bool(false);
    if (s->kind == Stream::File) std::fflush(s->file);
    else if (s->kind == Stream::Std && s->stdKind != 0) std::fflush(stdFile(s));
    ok();
    return Bool(true);
}

inline int64_t length(Stream* s) {
    if (s->kind == Stream::Memory) return (int64_t)s->mem.size();
    int64_t here = plat::fs::tell(s->file);
    plat::fs::seek(s->file, 0, SEEK_END);
    int64_t end = plat::fs::tell(s->file);
    plat::fs::seek(s->file, here, SEEK_SET);
    return end;
}
inline Value Seek(Value h, Value offsetV, Value originV) {
    Stream* s = find(h);
    if (!s) return Int(-1);
    if (!s->canSeek) return Int(fail(NotSupported, "The stream does not support seeking."));
    int64_t origin = originV.i, target = offsetV.i;
    if (origin < 0 || origin > 2) return Int(fail(InvalidArgument, "Invalid seek origin " + std::to_string(origin) + "."));
    int64_t here = s->kind == Stream::Memory ? s->pos : plat::fs::tell(s->file);
    int64_t base = origin == 0 ? 0 : origin == 1 ? here : length(s);
    if (base + target < 0) return Int(fail(InvalidArgument, "The position would be before the start of the stream."));
    if (s->kind == Stream::Memory) { s->pos = base + target; ok(); return Int(s->pos); }
    s->lastOp = 0;
    if (!plat::fs::seek(s->file, base + target, SEEK_SET)) return Int(fail(Other, "The stream could not be positioned."));
    ok();
    return Int(base + target);
}
inline Value Position(Value h) {
    Stream* s = find(h);
    if (!s) return Int(-1);
    if (!s->canSeek) return Int(fail(NotSupported, "The stream does not support a position."));
    ok();
    return Int(s->kind == Stream::Memory ? s->pos : plat::fs::tell(s->file));
}
inline Value Length(Value h) {
    Stream* s = find(h);
    if (!s) return Int(-1);
    if (!s->canSeek) return Int(fail(NotSupported, "The stream does not know its length."));
    ok();
    return Int(length(s));
}
inline Value SetLength(Value h, Value lengthV) {
    Stream* s = find(h);
    if (!s) return Bool(false);
    if (lengthV.i < 0) { fail(InvalidArgument, "The length must not be negative."); return Bool(false); }
    if (!s->canSeek || !s->canWrite) { fail(NotSupported, "The length of this stream cannot be changed."); return Bool(false); }
    if (s->kind == Stream::Memory) { s->mem.resize((size_t)lengthV.i, 0); if (s->pos > lengthV.i) s->pos = lengthV.i; }
    else {
        s->lastOp = 0;
        int64_t here = plat::fs::tell(s->file);
        if (!plat::fs::truncate(s->file, lengthV.i)) { fail(Other, "The length of the file could not be changed."); return Bool(false); }
        if (here > lengthV.i) plat::fs::seek(s->file, lengthV.i, SEEK_SET);
    }
    ok();
    return Bool(true);
}
inline Value CanRead(Value h) { int64_t i = h.i; return Bool(i > 0 && (size_t)i < streams().size() && streams()[(size_t)i] && streams()[(size_t)i]->canRead); }
inline Value CanWrite(Value h) { int64_t i = h.i; return Bool(i > 0 && (size_t)i < streams().size() && streams()[(size_t)i] && streams()[(size_t)i]->canWrite); }
inline Value CanSeek(Value h) { int64_t i = h.i; return Bool(i > 0 && (size_t)i < streams().size() && streams()[(size_t)i] && streams()[(size_t)i]->canSeek); }
inline Value MemToBuffer(Value h, OwnList* list) {
    Stream* s = find(h);
    if (!s) return Undef();
    if (s->kind != Stream::Memory) { fail(NotSupported, "Only a MemoryStream can be read as a buffer."); return Undef(); }
    Buf* b = allocBuf((uint32_t)s->mem.size(), list);
    if (!s->mem.empty()) std::memcpy(b->bytes(), s->mem.data(), s->mem.size());
    ok();
    return BufV(b);
}

// ---- files and directories --------------------------------------------------------------------------------------------------------------
inline Value FileExists(Value path) {
    std::string full;
    if (!authorize(path, A_Read, full)) return Int(-1);
    bool isDir = false;
    bool there = plat::fs::exists(full, isDir) && !isDir;
    ok();
    return Int(there ? 1 : 0);
}
inline Value FileSize(Value path) {
    std::string full;
    if (!authorize(path, A_Read, full)) return Int(-1);
    int64_t size = 0;
    plat::fs::Status s = plat::fs::fileSize(full, size);
    if (!s.ok()) return Int(fail(s));
    ok();
    return Int(size);
}
inline Value FileTime(Value path) {
    std::string full;
    if (!authorize(path, A_Read, full)) return Undef();
    bool isDir = false;
    if (!plat::fs::exists(full, isDir) || isDir) { fail(FileNotFound, "The file '" + full + "' does not exist."); return Undef(); }
    int64_t seconds = 0;
    plat::fs::Status s = plat::fs::fileTime(full, seconds);
    if (!s.ok()) { fail(s); return Undef(); }
    ok();
    return Int(seconds, FIRE_UNIT_SECONDS);
}
inline Value FileDelete(Value path) {
    std::string full;
    if (!authorize(path, A_Delete, full)) return Bool(false);
    plat::fs::Status s = plat::fs::removeFile(full);
    if (!s.ok()) { fail(s); return Bool(false); }
    ok();
    return Bool(true);
}
inline Value FileCopy(Value source, Value target, Value overwrite) {
    std::string to, from;
    if (!authorize(target, A_Write, to)) return Bool(false);
    if (!authorize(source, A_Read, from)) return Bool(false);
    bool isDir = false;
    if (!overwrite.i && plat::fs::exists(to, isDir)) { fail(AlreadyExists, "The target file '" + to + "' already exists."); return Bool(false); }
    plat::fs::Status s = plat::fs::copyFile(from, to, overwrite.i != 0);
    if (!s.ok()) { fail(s); return Bool(false); }
    ok();
    return Bool(true);
}
inline Value FileMove(Value source, Value target, Value overwrite) {
    std::string to, from;
    if (!authorize(target, A_Write, to)) return Bool(false);
    if (!authorize(source, A_Read | A_Delete, from)) return Bool(false);
    bool isDir = false;
    if (!overwrite.i && plat::fs::exists(to, isDir)) { fail(AlreadyExists, "The target file '" + to + "' already exists."); return Bool(false); }
    plat::fs::Status s = plat::fs::moveFile(from, to, overwrite.i != 0);
    if (!s.ok()) { fail(s); return Bool(false); }
    ok();
    return Bool(true);
}
inline Value DirExists(Value path) {
    std::string full;
    if (!authorize(path, A_Read, full)) return Int(-1);
    bool isDir = false;
    bool there = plat::fs::exists(full, isDir) && isDir;
    ok();
    return Int(there ? 1 : 0);
}
inline Value DirCreate(Value path) {
    std::string full;
    if (!authorize(path, A_Write, full)) return Bool(false);
    plat::fs::Status s = plat::fs::makeDirs(full);
    if (!s.ok()) { fail(s); return Bool(false); }
    ok();
    return Bool(true);
}
inline Value DirDelete(Value path, Value recursive) {
    std::string full;
    if (!authorize(path, A_Delete, full)) return Bool(false);
    plat::fs::Status s = plat::fs::removeDir(full, recursive.i != 0);
    if (!s.ok()) { fail(s); return Bool(false); }
    ok();
    return Bool(true);
}
inline Value DirList(Value path, Value pattern, Value recursive, Value kind, OwnList* list) {
    std::string full;
    if (!authorize(path, A_List, full)) return Undef();
    std::vector<std::string> entries;
    plat::fs::Status s = plat::fs::list(full, toUtf8(pattern), recursive.i != 0, kind.i != 0, entries);
    if (!s.ok()) { fail(s); return Undef(); }
    ok();
    return strArray(entries, list);
}
inline Value CurrentDir(OwnList* list) { ok(); return str8(plat::fs::currentDir(), list); }

// ---- paths (text only, no file access) ----------------------------------------------------------------------------------------------------
inline bool isSep(char c) {
#ifdef _WIN32
    return c == '/' || c == '\\';
#else
    return c == '/';
#endif
}
inline bool rooted(const std::string& p) {
#ifdef _WIN32
    return (!p.empty() && isSep(p[0])) || (p.size() >= 2 && p[1] == ':');
#else
    return !p.empty() && p[0] == '/';
#endif
}
inline std::string fileName(const std::string& p) {
    size_t i = p.size();
    while (i > 0 && !isSep(p[i - 1])) i--;
    return p.substr(i);
}
inline Value PathCombine(Value a, Value b, OwnList* list) {
    std::string x = toUtf8(a), y = toUtf8(b);
    ok();
    if (rooted(y) || x.empty()) return str8(y, list);
    if (y.empty()) return str8(x, list);
    if (isSep(x.back())) return str8(x + y, list);
    return str8(x + plat::fs::separator() + y, list);
}
inline Value PathFileName(Value p, OwnList* list) { ok(); return str8(fileName(toUtf8(p)), list); }
inline Value PathStem(Value p, OwnList* list) {
    std::string name = fileName(toUtf8(p));
    size_t dot = name.find_last_of('.');
    ok();
    return str8(dot == std::string::npos ? name : name.substr(0, dot), list);
}
inline Value PathExtension(Value p, OwnList* list) {
    std::string name = fileName(toUtf8(p));
    size_t dot = name.find_last_of('.');
    ok();
    return str8(dot == std::string::npos || dot + 1 == name.size() ? std::string() : name.substr(dot), list);
}
inline Value PathParent(Value pv, OwnList* list) {
    std::string p = toUtf8(pv);
    size_t root = 0;
#ifdef _WIN32
    if (p.size() >= 3 && p[1] == ':' && isSep(p[2])) root = 3; else if (p.size() >= 2 && p[1] == ':') root = 2; else if (!p.empty() && isSep(p[0])) root = 1;
#else
    if (!p.empty() && p[0] == '/') root = 1;
#endif
    ok();
    if (p.size() <= root) return str8("", list);
    size_t end = p.size();
    while (end > root && !isSep(p[--end])) {}
    while (end > root && isSep(p[end - 1])) end--;
    return str8(p.substr(0, end), list);
}
inline Value PathFull(Value p, OwnList* list) {
    std::string raw = toUtf8(p);
    if (raw.empty()) { fail(InvalidArgument, "The path is empty."); return Undef(); }
    ok();
    return str8(plat::fs::fullPath(raw), list);
}
inline Value PathTemp(OwnList* list) { ok(); return str8(plat::fs::tempDir(), list); }
inline Value PathSeparator(OwnList* list) { return str8(plat::fs::separator(), list); }
inline Value PathIsRooted(Value p) { return Bool(rooted(toUtf8(p))); }

// ---- the console ------------------------------------------------------------------------------------------------------------------------------
inline Value StdHandle(Value kind) {
    if (kind.i < 0 || kind.i > 2) return Int(fail(InvalidArgument, "Invalid standard stream " + std::to_string(kind.i) + "."));
    int k = (int)kind.i;
    if (g_stdHandles[k] == 0) {
        Stream* s = new Stream();
        s->kind = Stream::Std;
        s->stdKind = k;
        s->permanent = true;
        s->canRead = k == 0;
        s->canWrite = k != 0;
        g_stdHandles[k] = add(s);
    }
    ok();
    return Int(g_stdHandles[k]);
}
inline Value stdWrite(int64_t kind, const std::string& bytes, bool flush) {
    if (kind != 1 && kind != 2) { fail(InvalidArgument, "Invalid output stream " + std::to_string(kind) + "."); return Bool(false); }
    std::FILE* f = kind == 1 ? stdout : stderr;
    if (!bytes.empty() && std::fwrite(bytes.data(), 1, bytes.size(), f) != bytes.size()) { std::clearerr(f); fail(Other, "The console could not be written."); return Bool(false); }
    if (flush) std::fflush(f);
    ok();
    return Bool(true);
}
inline Value StdWrite(Value kind, Value text) { return stdWrite(kind.i, toUtf8(text), false); }
inline Value StdFlush(Value kind) { return stdWrite(kind.i, std::string(), true); }
/// A line of the standard input without the line break; undefined at the end of the input.
inline Value StdReadLine(OwnList* list) {
    std::fflush(stdout);
    std::string line;
    int c = std::getc(stdin);
    if (c == EOF) { ok(); return Undef(); }
    while (c != EOF && c != '\n' && c != '\r') { line.push_back((char)c); c = std::getc(stdin); }
    if (c == '\r') { int next = std::getc(stdin); if (next != '\n' && next != EOF) std::ungetc(next, stdin); }
    ok();
    return str8(line, list);
}
inline Value StdReadAll(OwnList* list) {
    std::fflush(stdout);
    std::string all;
    char chunk[512];
    size_t n;
    while ((n = std::fread(chunk, 1, sizeof chunk, stdin)) > 0) all.append(chunk, n);
    ok();
    return str8(all, list);
}

// ---- buffers and text -----------------------------------------------------------------------------------------------------------------------
inline Value BufferIndexOf(Value buffer, Value offset, Value count, Value value) {
    if (!leafAlive(buffer)) return destroyedError(buffer);
    if (!checkRange(buffer, offset.i, count.i)) return Int(-1);
    const uint8_t* start = bufOf(buffer)->bytes() + offset.i;
    const void* hit = count.i > 0 ? std::memchr(start, (int)(value.i & 0xFF), (size_t)count.i) : nullptr;
    ok();
    return Int(hit ? offset.i + (static_cast<const uint8_t*>(hit) - start) : -1);
}
inline Value Utf8Encode(Value text, OwnList* list) {
    std::string bytes = toUtf8(text);
    Buf* b = allocBuf((uint32_t)bytes.size(), list);
    if (!bytes.empty()) std::memcpy(b->bytes(), bytes.data(), bytes.size());
    return BufV(b);
}
inline Value Utf8Decode(Value buffer, Value offset, Value count, OwnList* list) {
    if (!leafAlive(buffer)) return destroyedError(buffer);
    if (!checkRange(buffer, offset.i, count.i)) return Undef();
    ok();
    std::u16string text = fromUtf8(bufOf(buffer)->bytes() + offset.i, (size_t)count.i);
    if (!text.empty() && text[0] == 0xFEFF) text.erase(0, 1);   // a byte order mark at the start
    return str(text, list);
}
/// Lines (\n, \r\n, \r); a line break at the end does not make an empty last line.
inline Value SplitLines(Value text, OwnList* list) {
    const Str* s = strOf(text);
    std::vector<std::u16string> lines;
    std::u16string current;
    bool open = false;
    for (uint32_t i = 0; i < s->length; i++) {
        char16_t c = s->data[i];
        if (c == '\n' || c == '\r') {
            if (c == '\r' && i + 1 < s->length && s->data[i + 1] == '\n') i++;
            lines.push_back(current);
            current.clear();
            open = false;
        } else { current.push_back(c); open = true; }
    }
    if (open) lines.push_back(current);
    ok();
    Arr* a = allocArr((uint32_t)lines.size(), list);
    for (size_t i = 0; i < lines.size(); i++) { a->items()[i] = str(lines[i], list); retain(a->items()[i]); }
    return ArrV(a);
}

}  // namespace io
}  // namespace fire
