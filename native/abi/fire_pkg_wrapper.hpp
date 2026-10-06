// The C++ side of the package ABI (native/abi/fire_pkg_abi.h): marshals fire_val <-> Value and exports the entry points. Included by the wrapper that the compiler generates
// around the natives of a package (after fire_rt.hpp, which is compiled with FIRE_LIBRARY, and after the sources of the package).
#pragma once

#include "fire_pkg_abi.h"
#include <cstdio>
#include <cstring>
#include <exception>
#include <memory>
#include <mutex>
#include <vector>

#if defined(_WIN32)
#define FIRE_PKG_EXPORT extern "C" __declspec(dllexport)
#else
#define FIRE_PKG_EXPORT extern "C" __attribute__((visibility("default")))
#endif

namespace fire {

/// A package library has no classes of its own (objects cannot cross the boundary): nothing to run.
void runDestructors(Obj*) {}

namespace pkgabi {

/// A native of the package as the wrapper calls it: the arguments, and the list of the scope that owns what the call allocates.
struct FnEntry { const char* name; int arity; Value (*call)(const Value* args, OwnList* list); };

/// Memory for results: lives until the next call of the thread.
class Arena {
public:
    void* alloc(size_t bytes) {
        blocks_.emplace_back(new char[bytes ? bytes : 1]);
        return blocks_.back().get();
    }
    void clear() { blocks_.clear(); }
private:
    std::vector<std::unique_ptr<char[]>> blocks_;
};

inline Arena& arena() { static thread_local Arena a; return a; }
inline std::mutex& callLock() { static std::mutex m; return m; }

inline Value toRt(const fire_val& in, OwnList* list) {
    switch (in.kind) {
        case FIRE_UNDEFINED: return Undef();
        case FIRE_BOOL: return Bool(in.v.i != 0);
        case FIRE_INT: return Int(in.v.i);
        case FIRE_FLOAT: return Float((Real)in.v.f);
        case FIRE_CHAR: return Char((uint32_t)in.v.i);
        case FIRE_STRING: {
            Str* s = allocStr((uint32_t)in.length, list);
            if (in.length > 0) std::memcpy(strChars(s), in.v.p, (size_t)in.length * sizeof(char16_t));
            return StrV(s);
        }
        case FIRE_ARRAY: {
            Arr* a = allocArr((uint32_t)in.length, list);
            const fire_val* items = static_cast<const fire_val*>(in.v.p);
            for (int32_t i = 0; i < in.length; i++) {
                Value v = toRt(items[i], list);
                a->items()[i] = v;
                retain(v);
            }
            return ArrV(a);
        }
        case FIRE_BUFFER: {
            // by reference: the native reads and writes the bytes of the caller in place (the VM pinned them for the call)
            Buf* b = allocBufExternal(const_cast<uint8_t*>(static_cast<const uint8_t*>(in.v.p)), (uint32_t)in.length, list);
            return BufV(b);
        }
    }
    fatal("An argument of a package function has an unknown kind.");
}

inline void fromRt(Value v, fire_val* out) {
    out->length = 0;
    out->v.i = 0;
    switch (v.kind) {
        case K_Undefined: out->kind = FIRE_UNDEFINED; return;
        case K_Bool: out->kind = FIRE_BOOL; out->v.i = v.i ? 1 : 0; return;
        case K_Int: out->kind = FIRE_INT; out->v.i = v.i; return;
        case K_Float: out->kind = FIRE_FLOAT; out->v.f = (double)v.f; return;
        case K_Char: out->kind = FIRE_CHAR; out->v.i = v.i; return;
        case K_String: {
            const Str* s = strOf(v);
            char16_t* copy = static_cast<char16_t*>(arena().alloc((size_t)s->length * sizeof(char16_t)));
            if (s->length > 0) std::memcpy(copy, s->data, (size_t)s->length * sizeof(char16_t));
            out->kind = FIRE_STRING; out->length = (int32_t)s->length; out->v.p = copy;
            return;
        }
        case K_Array: {
            if (!ownedOf(v)) fatal("A package function returned an array that is destroyed.");
            Arr* a = arrOf(v);
            fire_val* items = static_cast<fire_val*>(arena().alloc((size_t)a->length * sizeof(fire_val)));
            for (uint32_t i = 0; i < a->length; i++) fromRt(a->items()[i], &items[i]);
            out->kind = FIRE_ARRAY; out->length = (int32_t)a->length; out->v.p = items;
            return;
        }
        case K_Buffer: {
            if (!ownedOf(v)) fatal("A package function returned a buffer that is destroyed.");
            Buf* b = bufOf(v);
            uint8_t* copy = static_cast<uint8_t*>(arena().alloc(b->length));
            if (b->length > 0) std::memcpy(copy, b->bytes(), b->length);
            out->kind = FIRE_BUFFER; out->length = (int32_t)b->length; out->v.p = copy;
            return;
        }
        default: break;
    }
    fatal("A package function returned a value that cannot go to the virtual machine (an object, lambda or pointer): numbers, text, buffers and arrays can.");
}

inline void setError(char* error, int size, const char* text) {
    if (!error || size <= 0) return;
    std::snprintf(error, (size_t)size, "%s", text);
}

inline int callEntry(const FnEntry* entries, int count, int index, const fire_val* args, int argc, fire_val* result, char* error, int errorSize) {
    if (index < 0 || index >= count) { setError(error, errorSize, "unknown function index"); return 1; }
    const FnEntry& entry = entries[index];
    if (argc != entry.arity) { setError(error, errorSize, "wrong number of arguments"); return 1; }
    std::lock_guard<std::mutex> lock(callLock());
    arena().clear();
    OwnList list = {nullptr, nullptr, poolMark(), 0, nullptr, nullptr};
    int status = 0;
    try {
        std::vector<Value> values((size_t)argc + 1);
        for (int i = 0; i < argc; i++) values[(size_t)i] = toRt(args[i], &list);
        Value r = entry.call(values.data(), &list);
        fromRt(r, result);
    } catch (const FireClassError& e) {
        if (error && errorSize > 0) std::snprintf(error, (size_t)errorSize, "%s\n%s", e.cls, e.text);
        status = 2;
    } catch (const FireFatalError& e) {
        setError(error, errorSize, e.text);
        status = 1;
    } catch (const std::exception& e) {
        setError(error, errorSize, e.what());
        status = 1;
    }
    leave(&list);
    return status;
}

}  // namespace pkgabi
}  // namespace fire
