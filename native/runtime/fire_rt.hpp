// fire native runtime - header-only core used by code that `fire.Native` generates from the bytecode.
//
// Design rules (see docs/NATIVE_BACKEND.md):
//  * Same semantics as the C# VM; the test suite runs programs on both and compares the output.
//  * Plain C++17, no exceptions, no RTTI (ESP32/FreeRTOS builds usually disable both).
//  * Every operation has an inline fast path that the C++ compiler can fold when the operand kinds are known
//    at compile time (they usually are: `var i = 0` is an Int constant), and a cold slow path for everything else.
#pragma once

#include <charconv>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>

#if defined(__GNUC__) || defined(__clang__)
#define FIRE_COLD __attribute__((noinline, cold))
#define FIRE_LIKELY(x) __builtin_expect(!!(x), 1)
#define FIRE_UNLIKELY(x) __builtin_expect(!!(x), 0)
#else
#define FIRE_COLD
#define FIRE_LIKELY(x) (x)
#define FIRE_UNLIKELY(x) (x)
#endif

// Precision of `float`: double by default, `float` when the program is generated with `#floatwidth 32` (the generator then
// defines FIRE_FLOAT32). The C# VM rounds every float result to 32 bits in that mode, so both give the same numbers.
#ifdef FIRE_FLOAT32
using Real = float;
#else
using Real = double;
#endif

namespace fire {

enum Kind : uint8_t { K_Bool, K_Int, K_Float, K_Char, K_String, K_Class, K_Lambda, K_Pointer, K_Array, K_Buffer, K_Undefined };

/// A runtime value: 16 bytes, trivially copyable. `unit` indexes the program's unit table (0 = unitless).
struct Value {
    uint8_t kind;
    uint8_t width;
    uint16_t reserved;
    uint32_t unit;
    union {
        int64_t i;
        Real f;
        const void* p;
    };
};
static_assert(sizeof(Value) == 16, "Value must stay 16 bytes");

/// Names of the units (symbol as printed after a number), index = Value::unit. Defined by the generated code.
extern const char* const g_unitNames[];

// ---------------------------------------------------------------------------------------------------------------------
// Construction
// ---------------------------------------------------------------------------------------------------------------------
inline Value Int(int64_t v, uint32_t unit = 0) { Value r; r.kind = K_Int; r.width = 0; r.reserved = 0; r.unit = unit; r.i = v; return r; }
inline Value Float(Real v, uint32_t unit = 0) { Value r; r.kind = K_Float; r.width = 0; r.reserved = 0; r.unit = unit; r.f = v; return r; }
inline Value Bool(bool v) { Value r; r.kind = K_Bool; r.width = 0; r.reserved = 0; r.unit = 0; r.i = v ? 1 : 0; return r; }
inline Value Char(uint32_t c) { Value r; r.kind = K_Char; r.width = 0; r.reserved = 0; r.unit = 0; r.i = c; return r; }
inline Value Undef(uint32_t unit = 0) { Value r; r.kind = K_Undefined; r.width = 0; r.reserved = 0; r.unit = unit; r.i = 0; return r; }

// ---------------------------------------------------------------------------------------------------------------------
// Errors. The C# VM turns most of these into catchable script exceptions; the native backend does not support
// `try`/`catch` yet, so a runtime error ends the program (exit code 1).
// ---------------------------------------------------------------------------------------------------------------------
[[noreturn]] FIRE_COLD inline void fatal(const char* message) {
    std::fflush(stdout);
    std::fprintf(stderr, "fire runtime error: %s\n", message);
    std::exit(1);
}

[[noreturn]] FIRE_COLD inline void unsupported(const char* what) {
    std::fflush(stdout);
    std::fprintf(stderr, "fire runtime error: '%s' is not supported by the native backend yet\n", what);
    std::exit(1);
}

inline bool isNumeric(Value v) { return v.kind == K_Int || v.kind == K_Float; }
inline Real toR(Value v) { return v.kind == K_Float ? v.f : (Real)v.i; }

// ---------------------------------------------------------------------------------------------------------------------
// Reference-counted heap values: strings (UTF-16, like the VM), arrays and byte buffers.
//
// The language has no garbage collector, but - unlike objects, which have exactly one owner - these values are shared freely
// (`var t = s`, stored in fields and arrays, returned). So they are counted:
//  * a *storage location* (variable, parameter, field, array element, static) holds one count of what it contains;
//  * a stack temporary holds nothing: a fresh value is pushed on the *temporary pool* (count 1) and the scope that created it
//    releases everything above its mark when it is left (so a temporary lives until the end of its scope);
//  * `return` retains the value for the trip and the caller adopts it onto its pool (see `adopt`).
// Constants live in static storage and are immortal (never counted).
// ---------------------------------------------------------------------------------------------------------------------
constexpr uint32_t IMMORTAL = 0xFFFFFFFFu;
enum RefType : uint8_t { R_Str, R_Arr, R_Buf, R_Lam };

struct Ref {
    uint32_t rc;     // number of holders; IMMORTAL for constants
    uint8_t type;    // RefType
};

/// Immutable UTF-16 string. A heap string stores its characters right behind the header.
struct Str : Ref {
    uint32_t length;
    const char16_t* data;
};

/// Fixed-size array of Values (elements start as `undefined`).
struct alignas(alignof(Value)) Arr : Ref {
    uint32_t length;
    Value* items() { return reinterpret_cast<Value*>(this + 1); }
};

/// A lambda value: the function, the values it captured when it was created (copies, SPEC 4.2.1) and the `on` target (`this`).
struct alignas(alignof(Value)) Lam : Ref {
    uint32_t nparams;
    uint32_t ncaps;
    Value on;
    Value (*fn)(Value lam, const Value* args);
    Value* caps() { return reinterpret_cast<Value*>(this + 1); }
};

/// Byte buffer (`byte[]`).
struct Buf : Ref {
    uint32_t length;
    uint8_t* bytes() { return reinterpret_cast<uint8_t*>(this + 1); }
};

inline Value StrV(const Str* s) { Value r; r.kind = K_String; r.width = 0; r.reserved = 0; r.unit = 0; r.p = s; return r; }
inline Value ArrV(Arr* a) { Value r; r.kind = K_Array; r.width = 0; r.reserved = 0; r.unit = 0; r.p = a; return r; }
inline Value BufV(Buf* b) { Value r; r.kind = K_Buffer; r.width = 0; r.reserved = 0; r.unit = 0; r.p = b; return r; }

inline bool isRef(Value v) { return ((0x350u >> v.kind) & 1u) != 0; }  // String, Lambda, Array, Buffer
inline Ref* refOf(Value v) { return static_cast<Ref*>(const_cast<void*>(v.p)); }
inline const Str* strOf(Value v) { return static_cast<const Str*>(v.p); }
inline Arr* arrOf(Value v) { return static_cast<Arr*>(const_cast<void*>(v.p)); }
inline Buf* bufOf(Value v) { return static_cast<Buf*>(const_cast<void*>(v.p)); }
inline Lam* lamOf(Value v) { return static_cast<Lam*>(const_cast<void*>(v.p)); }
inline Value LamV(Lam* l) { Value r; r.kind = K_Lambda; r.width = 0; r.reserved = 0; r.unit = 0; r.p = l; return r; }

inline void freeRef(Ref* r);

inline void retain(Value v) {
    if (isRef(v)) {
        Ref* r = refOf(v);
        if (r->rc != IMMORTAL) r->rc++;
    }
}
inline void release(Value v) {
    if (isRef(v)) {
        Ref* r = refOf(v);
        if (r->rc != IMMORTAL && --r->rc == 0) freeRef(r);
    }
}

/// The temporary pool: every entry holds one count of a reference value; a scope releases the entries above its mark when it
/// ends. (One pool per thread once fire threads are translated.)
struct Pool {
    Ref** items;
    uint32_t top;
    uint32_t cap;
};
inline Pool g_pool = {nullptr, 0, 0};

FIRE_COLD inline void poolGrow() {
    uint32_t cap = g_pool.cap ? g_pool.cap * 2 : 256;
    Ref** items = static_cast<Ref**>(std::realloc(g_pool.items, cap * sizeof(Ref*)));
    if (!items) fatal("Out of memory.");
    g_pool.items = items;
    g_pool.cap = cap;
}
inline void poolPush(Ref* r) {
    if (FIRE_UNLIKELY(g_pool.top == g_pool.cap)) poolGrow();
    g_pool.items[g_pool.top++] = r;
}
inline uint32_t poolMark() { return g_pool.top; }
inline void poolRelease(uint32_t mark) {
    while (g_pool.top > mark) {
        Ref* r = g_pool.items[--g_pool.top];
        if (--r->rc == 0) freeRef(r);
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Arithmetic. Fast path: both numeric with the same unit. Unit conversion/algebra is not ported yet (slow path).
// ---------------------------------------------------------------------------------------------------------------------
// NOTE: every function takes its Values BY VALUE, never by reference (and the slow paths only the operand kinds). A
// reference to a local that reaches a call that is not inlined makes the local "escape", and the C++ compiler then
// keeps it - and, since the generated code reuses its stack temporaries s0..sN, all of them - in memory for the whole
// function instead of in registers. That alone made generated loops ~10x slower.
[[noreturn]] FIRE_COLD inline void opFailed(const char* op, bool bothNumeric) {
    if (bothNumeric) unsupported("arithmetic on different units");
    std::fflush(stdout);
    std::fprintf(stderr, "fire runtime error: operator '%s' is not supported for these operands in the native backend yet\n", op);
    std::exit(1);
}

inline Value add(Value a, Value b) {
    if (FIRE_LIKELY(a.unit == b.unit)) {
        if (a.kind == K_Int && b.kind == K_Int) return Int((int64_t)((uint64_t)a.i + (uint64_t)b.i), a.unit);
        if (isNumeric(a) && isNumeric(b)) return Float(toR(a) + toR(b), a.unit);
    }
    opFailed("+", isNumeric(a) && isNumeric(b));
}
inline Value sub(Value a, Value b) {
    if (FIRE_LIKELY(a.unit == b.unit)) {
        if (a.kind == K_Int && b.kind == K_Int) return Int((int64_t)((uint64_t)a.i - (uint64_t)b.i), a.unit);
        if (isNumeric(a) && isNumeric(b)) return Float(toR(a) - toR(b), a.unit);
    }
    opFailed("-", isNumeric(a) && isNumeric(b));
}
inline Value mul(Value a, Value b) {
    if (FIRE_LIKELY(a.unit == 0 && b.unit == 0)) {
        if (a.kind == K_Int && b.kind == K_Int) return Int((int64_t)((uint64_t)a.i * (uint64_t)b.i));
        if (isNumeric(a) && isNumeric(b)) return Float(toR(a) * toR(b));
    }
    opFailed("*", isNumeric(a) && isNumeric(b));
}
inline Value divide(Value a, Value b) {
    if (FIRE_LIKELY(a.unit == 0 && b.unit == 0)) {
        if (a.kind == K_Int && b.kind == K_Int) {
            if (FIRE_UNLIKELY(b.i == 0)) fatal("Division by zero.");
            if (FIRE_UNLIKELY(b.i == -1)) return Int((int64_t)(0 - (uint64_t)a.i));
            return Int(a.i / b.i);
        }
        if (isNumeric(a) && isNumeric(b)) return Float(toR(a) / toR(b));
    }
    opFailed("/", isNumeric(a) && isNumeric(b));
}
inline Value modulo(Value a, Value b) {
    if (FIRE_LIKELY(a.unit == b.unit)) {
        if (a.kind == K_Int && b.kind == K_Int) {
            if (FIRE_UNLIKELY(b.i == 0)) fatal("Division by zero.");
            if (FIRE_UNLIKELY(b.i == -1)) return Int(0, a.unit);
            return Int(a.i % b.i, a.unit);
        }
        if (isNumeric(a) && isNumeric(b)) return Float(std::fmod(toR(a), toR(b)), a.unit);
    }
    opFailed("%", isNumeric(a) && isNumeric(b));
}
inline Value negate(Value v) {
    if (v.kind == K_Int) return Int((int64_t)(0 - (uint64_t)v.i), v.unit);
    if (v.kind == K_Float) return Float(-v.f, v.unit);
    fatal("Unary '-' expects a number.");
}
inline Value lnot(Value v) {
    if (FIRE_UNLIKELY(v.kind != K_Bool)) fatal("'!' (negation) expects bool.");
    return Bool(v.i == 0);
}

#define FIRE_BITOP(NAME, OP, SYM)                                                              \
    inline Value NAME(Value a, Value b) {                                        \
        if (FIRE_UNLIKELY(a.kind != K_Int || b.kind != K_Int)) fatal("'" SYM "' expects int."); \
        if (FIRE_UNLIKELY(a.unit != b.unit)) unsupported("bit operation on different units");  \
        return Int(a.i OP b.i, a.unit);                                                        \
    }
FIRE_BITOP(bitAnd, &, "&")
FIRE_BITOP(bitOr, |, "|")
FIRE_BITOP(bitXor, ^, "#")
#undef FIRE_BITOP
inline Value shl(Value a, Value b) {
    if (FIRE_UNLIKELY(a.kind != K_Int || b.kind != K_Int)) fatal("'<<' expects int.");
    return Int((int64_t)((uint64_t)a.i << (b.i & 63)), a.unit);
}
inline Value shr(Value a, Value b) {
    if (FIRE_UNLIKELY(a.kind != K_Int || b.kind != K_Int)) fatal("'>>' expects int.");
    return Int(a.i >> (b.i & 63), a.unit);
}
inline Value bitNot(Value v) {
    if (FIRE_UNLIKELY(v.kind != K_Int)) fatal("'~' expects int.");
    return Int(~v.i, v.unit);
}

// ---------------------------------------------------------------------------------------------------------------------
// Comparison
// ---------------------------------------------------------------------------------------------------------------------
/// -1, 0, 1 like Value.Compare (NaN sorts below everything, like double.CompareTo).
inline int compare(Value a, Value b) {
    if (FIRE_LIKELY(a.unit == b.unit)) {
        if (a.kind == K_Int && b.kind == K_Int) return a.i < b.i ? -1 : (a.i > b.i ? 1 : 0);
        if (isNumeric(a) && isNumeric(b)) {
            Real x = toR(a), y = toR(b);
            if (x < y) return -1;
            if (x > y) return 1;
            if (x == y) return 0;
            if (std::isnan(x)) return std::isnan(y) ? 0 : -1;
            return 1;
        }
    }
    opFailed("<", isNumeric(a) && isNumeric(b));
}
inline bool lt(Value a, Value b) {
    if (FIRE_LIKELY(a.unit == b.unit)) {
        if (a.kind == K_Int && b.kind == K_Int) return a.i < b.i;
        if (a.kind == K_Float && b.kind == K_Float) return a.f < b.f;
    }
    return compare(a, b) < 0;
}
inline bool le(Value a, Value b) {
    if (FIRE_LIKELY(a.unit == b.unit)) {
        if (a.kind == K_Int && b.kind == K_Int) return a.i <= b.i;
        if (a.kind == K_Float && b.kind == K_Float) return a.f <= b.f;
    }
    return compare(a, b) <= 0;
}
inline bool gt(Value a, Value b) {
    if (FIRE_LIKELY(a.unit == b.unit)) {
        if (a.kind == K_Int && b.kind == K_Int) return a.i > b.i;
        if (a.kind == K_Float && b.kind == K_Float) return a.f > b.f;
    }
    return compare(a, b) > 0;
}
inline bool ge(Value a, Value b) {
    if (FIRE_LIKELY(a.unit == b.unit)) {
        if (a.kind == K_Int && b.kind == K_Int) return a.i >= b.i;
        if (a.kind == K_Float && b.kind == K_Float) return a.f >= b.f;
    }
    return compare(a, b) >= 0;
}
/// Value.Equals: different kinds are never equal (1 != 1.0), units and widths are ignored.
inline bool eq(Value a, Value b) {
    if (a.kind != b.kind) return false;
    switch (a.kind) {
        case K_Int: case K_Bool: case K_Char: return a.i == b.i;
        case K_Float: return a.f == b.f || (std::isnan(a.f) && std::isnan(b.f));
        case K_Undefined: return true;
        case K_String: {
            auto x = strOf(a), y = strOf(b);
            return x == y || (x->length == y->length && std::memcmp(x->data, y->data, x->length * sizeof(char16_t)) == 0);
        }
        default: return a.p == b.p;
    }
}
inline bool truthy(Value v) {
    if (FIRE_UNLIKELY(v.kind != K_Bool)) fatal("A condition must be a bool.");
    return v.i != 0;
}

// ---------------------------------------------------------------------------------------------------------------------
// Output: formatting identical to Value.ToString() (invariant culture)
// ---------------------------------------------------------------------------------------------------------------------
/// Real -> text like .NET Core 3.0+ `double.ToString()` / `float.ToString()`: shortest round-trip digits, positional
/// notation for 1E-4 <= |x| < 1E17 (double) or 1E9 (float), otherwise `d.dddE+XX`.
inline int formatReal(Real v, char* out) {
    if (std::isnan(v)) { std::memcpy(out, "NaN", 3); return 3; }
    if (std::isinf(v)) { if (v < 0) { std::memcpy(out, "-Infinity", 9); return 9; } std::memcpy(out, "Infinity", 8); return 8; }
    if (v == 0) { if (std::signbit(v)) { out[0] = '-'; out[1] = '0'; return 2; } out[0] = '0'; return 1; }

    char sci[40];
    auto r = std::to_chars(sci, sci + sizeof sci, v, std::chars_format::scientific);
    *r.ptr = 0;
    // sci = [-]d[.ddd]e[+-]XX
    char digits[24] = {0}; int nd = 0; bool negative = false; const char* c = sci;
    if (*c == '-') { negative = true; c++; }
    for (; *c && *c != 'e'; c++) if (*c != '.') digits[nd++] = *c;
    int exp10 = std::atoi(c + 1);

    int n = 0;
    if (negative) out[n++] = '-';
    if (exp10 >= (sizeof(Real) == 4 ? 9 : 17) || exp10 < -4) {
        out[n++] = digits[0];
        if (nd > 1) { out[n++] = '.'; for (int k = 1; k < nd; k++) out[n++] = digits[k]; }
        out[n++] = 'E'; out[n++] = exp10 < 0 ? '-' : '+';
        int e = exp10 < 0 ? -exp10 : exp10;
        if (e < 10) out[n++] = '0';
        n += std::snprintf(out + n, 8, "%d", e);
        return n;
    }
    if (exp10 >= 0) {
        for (int k = 0; k <= exp10; k++) out[n++] = k < nd ? digits[k] : '0';
        if (nd > exp10 + 1) { out[n++] = '.'; for (int k = exp10 + 1; k < nd; k++) out[n++] = digits[k]; }
    } else {
        out[n++] = '0'; out[n++] = '.';
        for (int k = 0; k < -exp10 - 1; k++) out[n++] = '0';
        for (int k = 0; k < nd; k++) out[n++] = digits[k];
    }
    return n;
}

// ---------------------------------------------------------------------------------------------------------------------
// Objects and ownership (SPEC 2): every object has exactly one owner - a scope or another object - and is destroyed (its
// destruct() runs, then everything it owns) when the owner is destroyed. An owner is an OwnList: for a scope a local of the
// generated function, for an object the `owned` list in its header. Objects are freed when destroyed.
//
// Differences to the VM: a reference to a destroyed object is not detected (the VM reports an error); compile with
// -DFIRE_KEEP_DESTROYED to keep destroyed objects in memory and get the check.
// ---------------------------------------------------------------------------------------------------------------------
struct Obj;

/// Doubly linked list of the objects an owner owns, in creation order (the order in which they are destroyed).
struct OwnList {
    Obj* head;       // objects, in creation order
    Obj* tail;
    uint32_t mark;   // height of the temporary pool when the scope was entered
};

struct alignas(alignof(Value)) Obj {
    uint32_t cls;      // class id (assigned by the generator)
    uint32_t flags;    // bit 0: destroyed
    uint32_t nfields;
    OwnList* owner;    // the list this object is in; null while it travels as a return value to its new owner
    Obj* prev;
    Obj* next;
    OwnList owned;     // what this object owns
    Value* fields() { return reinterpret_cast<Value*>(this + 1); }
};

/// Runs the destructors of the object's class chain, derived class first (generated; empty if no class has one).
void runDestructors(Obj* o);

inline Value ObjV(Obj* o) { Value r; r.kind = K_Class; r.width = 0; r.reserved = 0; r.unit = 0; r.p = o; return r; }
inline Obj* asObj(Value v) {
    if (FIRE_UNLIKELY(v.kind != K_Class)) fatal("A member was accessed on something that is not an object.");
    return static_cast<Obj*>(const_cast<void*>(v.p));
}

inline void link(OwnList* list, Obj* o) {
    o->owner = list;
    o->prev = list->tail;
    o->next = nullptr;
    if (list->tail) list->tail->next = o; else list->head = o;
    list->tail = o;
}

inline void unlink(Obj* o) {
    OwnList* list = o->owner;
    if (!list) return;
    if (o->prev) o->prev->next = o->next; else list->head = o->next;
    if (o->next) o->next->prev = o->prev; else list->tail = o->prev;
    o->prev = o->next = nullptr;
    o->owner = nullptr;
}

FIRE_COLD inline void* allocFailed() { fatal("Out of memory."); }

/// `new`: the object belongs to `owner` (the innermost scope of the creator, or - for `field = new X()` - the object that gets it).
inline Value newObject(uint32_t cls, uint32_t fieldCount, OwnList* owner) {
    void* memory = std::malloc(sizeof(Obj) + fieldCount * sizeof(Value));
    if (FIRE_UNLIKELY(!memory)) allocFailed();
    Obj* o = static_cast<Obj*>(memory);
    o->cls = cls;
    o->flags = 0;
    o->owned.head = o->owned.tail = nullptr;
    o->owned.mark = 0;
    o->nfields = fieldCount;
    Value* f = o->fields();
    for (uint32_t i = 0; i < fieldCount; i++) f[i] = Undef();
    link(owner, o);
    return ObjV(o);
}

inline void destroyList(OwnList* list);

inline void destroy(Obj* o) {
    if (o->flags & 1) return;
    o->flags |= 1;
    runDestructors(o);
    destroyList(&o->owned);
    Value* f = o->fields();
    for (uint32_t i = 0; i < o->nfields; i++) release(f[i]);
#ifndef FIRE_KEEP_DESTROYED
    std::free(o);
#endif
}

/// Destroys everything on the list, in creation order, and empties it.
inline void destroyList(OwnList* list) {
    Obj* o = list->head;
    list->head = list->tail = nullptr;
    while (o) {
        Obj* next = o->next;
        o->owner = nullptr;
        destroy(o);
        o = next;
    }
}

/// Leaving a scope: destroys what it owns and lets go of the strings/arrays it created.
inline void leave(OwnList* list) {
    if (list->head) destroyList(list);
    if (g_pool.top > list->mark) poolRelease(list->mark);
}

/// `return`: an object returned from the scope that owns it does not die with it; it goes to the caller (SPEC 2.3).
inline void transferOut(Value v, OwnList* list) {
    if (v.kind == K_Class) {
        Obj* o = static_cast<Obj*>(const_cast<void*>(v.p));
        if (o->owner == list) unlink(o);
    }
}

/// The caller takes over what came back from a call: an object without an owner, or a string/array/buffer that the callee
/// retained for the trip (that count now belongs to the scope's list).
inline void adopt(Value v, OwnList* list) {
    if (v.kind == K_Class) {
        Obj* o = static_cast<Obj*>(const_cast<void*>(v.p));
        if (!o->owner) link(list, o);
    } else if (isRef(v)) {
        Ref* r = refOf(v);
        if (r->rc != IMMORTAL) poolPush(r);   // the count that came with the value now belongs to the pool
        (void)list;
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Memory of the reference-counted values
// ---------------------------------------------------------------------------------------------------------------------
inline void freeRef(Ref* r) {
    if (r->type == R_Lam) {
        Lam* l = static_cast<Lam*>(r);
        Value* caps = l->caps();
        for (uint32_t i = 0; i < l->ncaps; i++) release(caps[i]);
    } else if (r->type == R_Arr) {
        Arr* a = static_cast<Arr*>(r);
        Value* items = a->items();
        for (uint32_t i = 0; i < a->length; i++) release(items[i]);
    }
    std::free(r);
}

/// A fresh reference value is held by the temporary pool (count 1) until its scope ends.
inline void registerTemp(Ref* r, OwnList* list) {
    r->rc = 1;
    poolPush(r);
    (void)list;
}

inline Str* allocStr(uint32_t length, OwnList* list) {
    Str* s = static_cast<Str*>(std::malloc(sizeof(Str) + (size_t)(length + 1) * sizeof(char16_t)));
    if (FIRE_UNLIKELY(!s)) allocFailed();
    s->type = R_Str;
    s->length = length;
    char16_t* d = reinterpret_cast<char16_t*>(s + 1);
    d[length] = 0;
    s->data = d;
    registerTemp(s, list);
    return s;
}
inline char16_t* strChars(Str* s) { return const_cast<char16_t*>(s->data); }

inline Arr* allocArr(uint32_t length, OwnList* list) {
    Arr* a = static_cast<Arr*>(std::malloc(sizeof(Arr) + (size_t)length * sizeof(Value)));
    if (FIRE_UNLIKELY(!a)) allocFailed();
    a->type = R_Arr;
    a->length = length;
    Value* items = a->items();
    for (uint32_t i = 0; i < length; i++) items[i] = Undef();
    registerTemp(a, list);
    return a;
}

inline Lam* allocLam(uint32_t nparams, uint32_t ncaps, Value (*fn)(Value, const Value*), OwnList* list) {
    Lam* l = static_cast<Lam*>(std::malloc(sizeof(Lam) + (size_t)ncaps * sizeof(Value)));
    if (FIRE_UNLIKELY(!l)) allocFailed();
    l->type = R_Lam;
    l->nparams = nparams;
    l->ncaps = ncaps;
    l->on = Undef();
    l->fn = fn;
    registerTemp(l, list);
    return l;
}

/// The `this` of a lambda body: its `on` target.
inline Value lamOn(Value lam) { return lamOf(lam)->on; }
inline Value lamCapture(Value lam, uint32_t index) { return lamOf(lam)->caps()[index]; }

/// `callee(args...)`
inline Value callLam(Value callee, int argc, const Value* args) {
    if (FIRE_UNLIKELY(callee.kind != K_Lambda)) fatal("Call of a value that is not a lambda.");
    Lam* l = lamOf(callee);
    if (FIRE_UNLIKELY(l->nparams != (uint32_t)argc)) fatal("A lambda was called with the wrong number of arguments.");
    return l->fn(callee, args);
}

/// CheckLambdaSignature: a value assigned to a `lambda<...>` annotation must have that many parameters.
inline void checkLambda(Value v, int nparams) {
    if (FIRE_UNLIKELY(v.kind != K_Lambda || lamOf(v)->nparams != (uint32_t)nparams)) fatal("The lambda does not have the declared number of parameters.");
}

/// CheckUnit: the value must have exactly this unit.
inline void checkUnit(Value v, uint32_t unit) {
    uint32_t actual = (v.kind == K_Int || v.kind == K_Float || v.kind == K_Undefined) ? v.unit : 0;
    if (FIRE_UNLIKELY(actual != unit)) fatal("Incompatible units.");
}

inline Buf* allocBuf(uint32_t length, OwnList* list) {
    Buf* b = static_cast<Buf*>(std::malloc(sizeof(Buf) + length));
    if (FIRE_UNLIKELY(!b)) allocFailed();
    b->type = R_Buf;
    b->length = length;
    std::memset(b->bytes(), 0, length);
    registerTemp(b, list);
    return b;
}

// ---------------------------------------------------------------------------------------------------------------------
// Strings
// ---------------------------------------------------------------------------------------------------------------------
/// Generated: calls the ToString() method of the object's class if it has one (the result is a string value created in `list`).
bool userToString(Value object, OwnList* list, Value* result);

/// The characters of a value as `+` and `$"..."` append them (Value.ToString()).
struct Piece {
    const char16_t* p;
    uint32_t n;
    char16_t buf[80];
};

inline uint32_t widenAscii(const char* s, uint32_t n, char16_t* out) {
    for (uint32_t i = 0; i < n; i++) out[i] = (char16_t)(unsigned char)s[i];
    return n;
}

/// UTF-8 -> UTF-16 (unit symbols such as "µm"); returns the number of UTF-16 units written.
inline uint32_t utf8ToUtf16(const char* s, char16_t* out, uint32_t cap) {
    uint32_t n = 0;
    while (*s && n + 2 < cap) {
        unsigned c = (unsigned char)*s++;
        uint32_t cp;
        if (c < 0x80) cp = c;
        else if ((c >> 5) == 6 && *s) cp = ((c & 0x1F) << 6) | ((unsigned char)*s++ & 0x3F);
        else if ((c >> 4) == 14 && s[0] && s[1]) { cp = ((c & 0x0F) << 12) | (((unsigned char)s[0] & 0x3F) << 6) | ((unsigned char)s[1] & 0x3F); s += 2; }
        else if ((c >> 3) == 30 && s[0] && s[1] && s[2]) { cp = ((c & 0x07) << 18) | (((unsigned char)s[0] & 0x3F) << 12) | (((unsigned char)s[1] & 0x3F) << 6) | ((unsigned char)s[2] & 0x3F); s += 3; }
        else cp = 0xFFFD;
        if (cp >= 0x10000) { cp -= 0x10000; out[n++] = (char16_t)(0xD800 + (cp >> 10)); out[n++] = (char16_t)(0xDC00 + (cp & 0x3FF)); }
        else out[n++] = (char16_t)cp;
    }
    return n;
}

inline void setPiece(Piece& out, const char* ascii) {
    out.n = widenAscii(ascii, (uint32_t)std::strlen(ascii), out.buf);
    out.p = out.buf;
}

inline void pieceOf(Value v, Piece& out, OwnList* list) {
    char tmp[64];
    switch (v.kind) {
        case K_String: out.p = strOf(v)->data; out.n = strOf(v)->length; return;
        case K_Char: out.buf[0] = (char16_t)v.i; out.p = out.buf; out.n = 1; return;
        case K_Bool: setPiece(out, v.i ? "True" : "False"); return;
        case K_Int: {
            auto r = std::to_chars(tmp, tmp + sizeof tmp, v.i);
            out.n = widenAscii(tmp, (uint32_t)(r.ptr - tmp), out.buf);
            break;
        }
        case K_Float: out.n = widenAscii(tmp, (uint32_t)formatReal(v.f, tmp), out.buf); break;
        case K_Undefined: setPiece(out, "undefined"); if (v.unit) { out.buf[out.n++] = u':'; } break;
        case K_Class: {
            Value text;
            if (userToString(v, list, &text)) { out.p = strOf(text)->data; out.n = strOf(text)->length; }
            else setPiece(out, "<object>");
            return;
        }
        case K_Lambda: setPiece(out, "<lambda>"); return;
        case K_Pointer: setPiece(out, "<pointer>"); return;
        case K_Array: setPiece(out, "<array>"); return;
        case K_Buffer: {
            int n = std::snprintf(tmp, sizeof tmp, "<buffer %u bytes>", (unsigned)bufOf(v)->length);
            out.n = widenAscii(tmp, (uint32_t)n, out.buf);
            break;
        }
        default: setPiece(out, "?"); return;
    }
    out.p = out.buf;
    if (v.unit && (v.kind == K_Int || v.kind == K_Float || v.kind == K_Undefined))
        out.n += utf8ToUtf16(g_unitNames[v.unit], out.buf + out.n, 80 - out.n);
}

/// `a + b` when one side is a string: the other side is appended as its text.
inline Value concat(Value a, Value b, OwnList* list) {
    Piece pa, pb;
    pieceOf(a, pa, list);
    pieceOf(b, pb, list);
    Str* r = allocStr(pa.n + pb.n, list);
    char16_t* d = strChars(r);
    std::memcpy(d, pa.p, pa.n * sizeof(char16_t));
    std::memcpy(d + pa.n, pb.p, pb.n * sizeof(char16_t));
    return StrV(r);
}

/// The text of any value as a new string (`$"{x}"` without a format).
inline Value toStringValue(Value v, OwnList* list) {
    if (v.kind == K_String) return v;
    Piece p;
    pieceOf(v, p, list);
    Str* r = allocStr(p.n, list);
    std::memcpy(strChars(r), p.p, p.n * sizeof(char16_t));
    return StrV(r);
}

FIRE_COLD inline Value addSlow(Value a, Value b, OwnList* list) {
    if (a.kind == K_String || b.kind == K_String) return concat(a, b, list);
    opFailed("+", isNumeric(a) && isNumeric(b));
}

/// `+` for operands that may be strings (the generator uses `add` where both are known to be numbers).
inline Value addR(Value a, Value b, OwnList* list) {
    if (FIRE_LIKELY(a.unit == b.unit)) {
        if (a.kind == K_Int && b.kind == K_Int) return Int((int64_t)((uint64_t)a.i + (uint64_t)b.i), a.unit);
        if (isNumeric(a) && isNumeric(b)) return Float(toR(a) + toR(b), a.unit);
    }
    return addSlow(a, b, list);
}

// ---- Unicode helpers (Basic Latin, Latin-1, Greek and Cyrillic; everything else is left alone) -------------------------
inline bool isWhiteC(uint32_t c) {
    return c == 0x20 || (c >= 9 && c <= 13) || c == 0x85 || c == 0xA0 || c == 0x1680 || (c >= 0x2000 && c <= 0x200A) ||
           c == 0x2028 || c == 0x2029 || c == 0x202F || c == 0x205F || c == 0x3000;
}
inline uint32_t toUpperC(uint32_t c) {
    if (c >= 'a' && c <= 'z') return c - 32;
    if (c < 0xB5) return c;
    if (c == 0xB5) return 0x39C;
    if (c >= 0xE0 && c <= 0xFE && c != 0xF7) return c - 32;
    if (c == 0xFF) return 0x178;
    if (c >= 0x3B1 && c <= 0x3C9 && c != 0x3C2) return c - 32;
    if (c == 0x3C2) return 0x3A3;
    if (c == 0x3AC) return 0x386;
    if (c >= 0x3AD && c <= 0x3AF) return c - 37;
    if (c == 0x3CC) return 0x38C;
    if (c == 0x3CD || c == 0x3CE) return c - 63;
    if (c >= 0x430 && c <= 0x44F) return c - 32;
    if (c >= 0x450 && c <= 0x45F) return c - 80;
    return c;
}
inline uint32_t toLowerC(uint32_t c) {
    if (c >= 'A' && c <= 'Z') return c + 32;
    if (c < 0xC0) return c;
    if (c <= 0xDE && c != 0xD7) return c + 32;
    if (c >= 0x391 && c <= 0x3A9 && c != 0x3A2) return c + 32;
    if (c == 0x386) return 0x3AC;
    if (c >= 0x388 && c <= 0x38A) return c + 37;
    if (c == 0x38C) return 0x3CC;
    if (c == 0x38E || c == 0x38F) return c + 63;
    if (c >= 0x410 && c <= 0x42F) return c + 32;
    if (c >= 0x400 && c <= 0x40F) return c + 80;
    return c;
}
inline bool isLetterC(uint32_t c) {
    if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) return true;
    if (c < 0xAA) return false;
    if (c == 0xAA || c == 0xB5 || c == 0xBA) return true;
    if (c >= 0xC0 && c <= 0x24F) return c != 0xD7 && c != 0xF7;
    if (c >= 0x370 && c <= 0x3FF) return c != 0x37E && c != 0x387 && c != 0x375 && c != 0x384 && c != 0x385;
    return (c >= 0x400 && c <= 0x481) || (c >= 0x48A && c <= 0x52F);
}

// ---- The string methods of the prelude (`class extends string`), see StringMethods.cs ---------------------------------
struct Chars {
    const char16_t* sp;
    uint32_t n;
    char16_t one;
    bool isChar;
    /// The characters (a single char lives inside this struct, so take the pointer from the copy you keep).
    const char16_t* p() const { return isChar ? &one : sp; }
};
/// A `string` or `char` argument as characters (the VM's Text()).
inline Chars charsOf(Value v) {
    Chars c;
    c.sp = nullptr; c.one = 0; c.isChar = false; c.n = 0;
    if (v.kind == K_Char) { c.one = (char16_t)v.i; c.isChar = true; c.n = 1; }
    else if (v.kind == K_String) { c.sp = strOf(v)->data; c.n = strOf(v)->length; }
    else fatal("A string or char was expected.");
    return c;
}

inline int64_t findFrom(const char16_t* s, uint32_t len, const char16_t* v, uint32_t vn, uint32_t start) {
    if (vn == 0) return start;
    if (vn > len) return -1;
    for (uint32_t i = start; i + vn <= len; i++)
        if (std::memcmp(s + i, v, vn * sizeof(char16_t)) == 0) return i;
    return -1;
}

inline Value newStrFrom(const char16_t* p, uint32_t n, OwnList* list) {
    Str* r = allocStr(n, list);
    std::memcpy(strChars(r), p, n * sizeof(char16_t));
    return StrV(r);
}

/// `__StringCall(id, text, args...)`: the methods of `string`. `argc` counts the arguments after the text.
inline Value stringCall(int64_t method, Value text, int argc, Value a0, Value a1, OwnList* list) {
    if (text.kind != K_String) fatal("The string method was called on something that is not a string.");
    const char16_t* s = strOf(text)->data;
    uint32_t len = strOf(text)->length;
    switch (method) {
        case 1: {  // IndexOf(value[, start])
            Chars v = charsOf(a0);
            int64_t start = argc == 2 ? a1.i : 0;
            if (start < 0 || start > (int64_t)len) fatal("String index out of range.");
            return Int(findFrom(s, len, v.p(), v.n, (uint32_t)start));
        }
        case 2: {  // LastIndexOf(value[, start])
            Chars v = charsOf(a0);
            int64_t start = argc == 2 ? a1.i : (int64_t)len - 1;
            if (len == 0) return Int(v.n == 0 ? 0 : -1);
            if (start < 0 || start >= (int64_t)len) fatal("String index out of range.");
            if (v.n == 0) return Int(start + 1);
            for (int64_t i = start - (int64_t)v.n + 1; i >= 0; i--)
                if (std::memcmp(s + i, v.p(), v.n * sizeof(char16_t)) == 0) return Int(i);
            return Int(-1);
        }
        case 3: {  // Substring(start[, count])
            int64_t start = a0.i;
            if (start < 0 || start > (int64_t)len) fatal("String index out of range.");
            int64_t count = argc == 2 ? a1.i : (int64_t)len - start;
            if (count < 0 || count > (int64_t)len - start) fatal("String index out of range.");
            if (start == 0 && count == (int64_t)len) return text;
            return newStrFrom(s + start, (uint32_t)count, list);
        }
        case 4: {  // CharAt(index)
            if (a0.i < 0 || a0.i >= (int64_t)len) fatal("String index out of range.");
            return Char(s[a0.i]);
        }
        case 5: { Chars v = charsOf(a0); return Bool(findFrom(s, len, v.p(), v.n, 0) >= 0); }
        case 6: { Chars v = charsOf(a0); return Bool(v.n <= len && std::memcmp(s, v.p(), v.n * sizeof(char16_t)) == 0); }
        case 7: { Chars v = charsOf(a0); return Bool(v.n <= len && std::memcmp(s + len - v.n, v.p(), v.n * sizeof(char16_t)) == 0); }
        case 8: case 9: {  // ToUpper / ToLower
            Str* r = allocStr(len, list);
            char16_t* d = strChars(r);
            for (uint32_t i = 0; i < len; i++) d[i] = (char16_t)(method == 8 ? toUpperC(s[i]) : toLowerC(s[i]));
            return StrV(r);
        }
        case 10: case 11: case 12: {  // Trim / TrimStart / TrimEnd
            uint32_t b = 0, e = len;
            if (method != 12) while (b < e && isWhiteC(s[b])) b++;
            if (method != 11) while (e > b && isWhiteC(s[e - 1])) e--;
            if (b == 0 && e == len) return text;
            return newStrFrom(s + b, e - b, list);
        }
        case 13: {  // Replace(old, new): every occurrence, an empty `old` changes nothing
            Chars o = charsOf(a0), w = charsOf(a1);
            if (o.n == 0) return text;
            uint32_t count = 0;
            for (int64_t i = findFrom(s, len, o.p(), o.n, 0); i >= 0; i = findFrom(s, len, o.p(), o.n, (uint32_t)i + o.n)) count++;
            if (count == 0) return text;
            Str* r = allocStr(len - count * o.n + count * w.n, list);
            char16_t* d = strChars(r);
            uint32_t from = 0;
            for (int64_t i = findFrom(s, len, o.p(), o.n, 0); i >= 0; i = findFrom(s, len, o.p(), o.n, from)) {
                std::memcpy(d, s + from, (uint32_t)(i - from) * sizeof(char16_t)); d += (uint32_t)i - from;
                std::memcpy(d, w.p(), w.n * sizeof(char16_t)); d += w.n;
                from = (uint32_t)i + o.n;
            }
            std::memcpy(d, s + from, (len - from) * sizeof(char16_t));
            return StrV(r);
        }
        case 14: {  // Split(separator): an array of the parts, empty parts kept; an empty separator gives the whole string
            Chars sep = charsOf(a0);
            uint32_t parts = 1;
            if (sep.n > 0)
                for (int64_t i = findFrom(s, len, sep.p(), sep.n, 0); i >= 0; i = findFrom(s, len, sep.p(), sep.n, (uint32_t)i + sep.n)) parts++;
            Arr* arr = allocArr(parts, list);
            uint32_t from = 0, k = 0;
            if (sep.n > 0)
                for (int64_t i = findFrom(s, len, sep.p(), sep.n, 0); i >= 0; i = findFrom(s, len, sep.p(), sep.n, from)) {
                    Value part = newStrFrom(s + from, (uint32_t)i - from, list);
                    arr->items()[k++] = part; retain(part);
                    from = (uint32_t)i + sep.n;
                }
            Value last = (from == 0) ? text : newStrFrom(s + from, len - from, list);
            arr->items()[k] = last; retain(last);
            return ArrV(arr);
        }
        case 15: case 16: {  // PadLeft / PadRight(width[, fill])
            int64_t width = a0.i;
            if (width < 0 || width > 0x7FFFFFFF) fatal("String index out of range.");
            char16_t fill = u' ';
            if (argc == 2) { Chars f = charsOf(a1); if (f.n > 0) fill = f.p()[0]; }
            if (width <= (int64_t)len) return text;
            Str* r = allocStr((uint32_t)width, list);
            char16_t* d = strChars(r);
            uint32_t pad = (uint32_t)width - len;
            if (method == 15) { for (uint32_t i = 0; i < pad; i++) d[i] = fill; std::memcpy(d + pad, s, len * sizeof(char16_t)); }
            else { std::memcpy(d, s, len * sizeof(char16_t)); for (uint32_t i = 0; i < pad; i++) d[len + i] = fill; }
            return StrV(r);
        }
        default: fatal("Unknown string method.");
    }
}

/// `__CharCall(id, char)`: the methods of `char`.
inline Value charCall(int64_t method, Value ch, OwnList* list) {
    if (ch.kind != K_Char) fatal("The char method was called on something that is not a char.");
    uint32_t c = (uint32_t)ch.i;
    switch (method) {
        case 1: return Bool(c >= '0' && c <= '9');
        case 2: return Bool(isLetterC(c));
        case 3: return Bool(isLetterC(c) || (c >= '0' && c <= '9'));
        case 4: return Bool(isWhiteC(c));
        case 5: return Bool(toLowerC(c) != c);
        case 6: return Bool(toUpperC(c) != c || c == 0xDF);
        case 7: return Char(toUpperC(c));
        case 8: return Char(toLowerC(c));
        case 9: { char16_t u = (char16_t)c; return newStrFrom(&u, 1, list); }
        case 10: return Int(c);
        default: fatal("Unknown char method.");
    }
}

// ---- Number formats of `$"{x:F2}"` (Value.Format): X/x, D, B, F ------------------------------------------------------
inline Value formatValue(Value v, Value specV, OwnList* list) {
    const Str* spec = strOf(specV);
    if (spec->length == 0) return toStringValue(v, list);
    char text[400];
    int n = 0;
    uint32_t first = spec->data[0];
    int64_t number = -1;       // the digits after the letter
    bool hasNumber = false;
    {
        int64_t acc = 0;
        bool ok = spec->length > 1;
        for (uint32_t i = 1; i < spec->length; i++) {
            uint32_t d = spec->data[i];
            if (d < '0' || d > '9') { ok = false; break; }
            acc = acc * 10 + (d - '0');
            if (acc > 99) break;
        }
        if (ok) { hasNumber = true; number = acc; }
    }
    uint32_t letter = toUpperC(first);
    if (letter == 'X' || letter == 'D' || letter == 'B') {
        if (v.kind != K_Int) fatal("The format specifier expects an int.");
        uint64_t u = (uint64_t)v.i;
        char digits[80];
        int dn = 0;
        bool negative = false;
        if (letter == 'X') {
            const char* hex = first == 'X' ? "0123456789ABCDEF" : "0123456789abcdef";
            do { digits[dn++] = hex[u & 15]; u >>= 4; } while (u);
        } else if (letter == 'B') {
            do { digits[dn++] = (char)('0' + (u & 1)); u >>= 1; } while (u);
        } else {
            negative = v.i < 0;
            if (negative) u = 0 - u;
            do { digits[dn++] = (char)('0' + u % 10); u /= 10; } while (u);
        }
        int width = hasNumber ? (int)number : 0;
        if (negative) text[n++] = '-';
        for (int i = dn; i < width && n < 380; i++) text[n++] = '0';
        while (dn) text[n++] = digits[--dn];
        char16_t wide[400];
        uint32_t wn = widenAscii(text, (uint32_t)n, wide);
        return newStrFrom(wide, wn, list);
    }
    if (letter == 'F') {
        if (v.kind != K_Int && v.kind != K_Float) fatal("The format specifier expects a number.");
        double d = (double)toR(v);
        int prec = hasNumber ? (int)number : (spec->length == 1 ? 2 : -1);
        if (prec < 0) fatal("Unknown format specifier.");
        if (std::isnan(d)) n = std::snprintf(text, sizeof text, "NaN");
        else if (std::isinf(d)) n = std::snprintf(text, sizeof text, d < 0 ? "-Infinity" : "Infinity");
        else n = std::snprintf(text, sizeof text, "%.*f", prec, d);
        if (n < 0 || n >= (int)sizeof text) fatal("The formatted number is too long.");
        char16_t wide[400];
        uint32_t wn = widenAscii(text, (uint32_t)n, wide);
        return newStrFrom(wide, wn, list);
    }
    fatal("Unknown format specifier.");
}

// ---------------------------------------------------------------------------------------------------------------------
// Arrays and byte buffers
// ---------------------------------------------------------------------------------------------------------------------
FIRE_COLD inline void indexFailed(const char* what) { fatal(what); }

/// `a[i]` for arrays, buffers and strings (objects with a GetIndex method are handled by the generated code).
inline Value arrayGet(Value a, Value i) {
    if (FIRE_LIKELY(a.kind == K_Array && i.kind == K_Int)) {
        Arr* arr = arrOf(a);
        if (FIRE_UNLIKELY((uint64_t)i.i >= arr->length)) indexFailed("Array index out of range.");
        return arr->items()[i.i];
    }
    if (i.kind != K_Int) fatal("An index must be an int.");
    if (a.kind == K_Buffer) {
        Buf* b = bufOf(a);
        if ((uint64_t)i.i >= b->length) indexFailed("Buffer index out of range.");
        return Int(b->bytes()[i.i]);
    }
    if (a.kind == K_String) {
        const Str* s = strOf(a);
        if ((uint64_t)i.i >= s->length) indexFailed("String index out of range.");
        return Char(s->data[i.i]);
    }
    fatal("Index access ('[]') is not possible on this value.");
}

/// `a[i] = v`; the array holds a count of the new element and lets go of the old one.
inline void arraySet(Value a, Value i, Value v) {
    if (FIRE_LIKELY(a.kind == K_Array && i.kind == K_Int)) {
        Arr* arr = arrOf(a);
        if (FIRE_UNLIKELY((uint64_t)i.i >= arr->length)) indexFailed("Array index out of range.");
        Value old = arr->items()[i.i];
        arr->items()[i.i] = v;
        retain(v);
        release(old);
        return;
    }
    if (i.kind != K_Int) fatal("An index must be an int.");
    if (a.kind == K_Buffer) {
        if (v.kind != K_Int) fatal("Assigning to a byte buffer expects an int value.");
        Buf* b = bufOf(a);
        if ((uint64_t)i.i >= b->length) indexFailed("Buffer index out of range.");
        b->bytes()[i.i] = (uint8_t)v.i;
        return;
    }
    if (a.kind == K_String) fatal("Strings are immutable.");
    fatal("Index access ('[]') is not possible on this value.");
}

inline Value lengthOf(Value v, bool* ok) {
    *ok = true;
    if (v.kind == K_Array) return Int(arrOf(v)->length);
    if (v.kind == K_String) return Int(strOf(v)->length);
    if (v.kind == K_Buffer) return Int(bufOf(v)->length);
    *ok = false;
    return Undef();
}

inline Value newArray(Value size, OwnList* list) {
    if (size.kind != K_Int || size.i < 0 || size.i > 0x7FFFFFFF) fatal("An array size must be a non-negative int.");
    return ArrV(allocArr((uint32_t)size.i, list));
}

inline Value newBuffer(Value size, OwnList* list) {
    if (size.kind != K_Int || size.i < 0 || size.i > 0x7FFFFFFF) fatal("A buffer size must be a non-negative int.");
    return BufV(allocBuf((uint32_t)size.i, list));
}

// ---------------------------------------------------------------------------------------------------------------------
// Output
// ---------------------------------------------------------------------------------------------------------------------
/// Writes UTF-16 text as UTF-8 (an unpaired surrogate becomes U+FFFD, like the .NET console encoding).
inline void writeUtf16(const char16_t* s, uint32_t n, std::FILE* f) {
    char buf[256];
    size_t k = 0;
    for (uint32_t i = 0; i < n; i++) {
        uint32_t c = s[i];
        if (c >= 0xD800 && c <= 0xDBFF && i + 1 < n && s[i + 1] >= 0xDC00 && s[i + 1] <= 0xDFFF) {
            c = 0x10000 + ((c - 0xD800) << 10) + (s[i + 1] - 0xDC00);
            i++;
        } else if (c >= 0xD800 && c <= 0xDFFF) c = 0xFFFD;
        if (k + 4 > sizeof buf) { std::fwrite(buf, 1, k, f); k = 0; }
        if (c < 0x80) buf[k++] = (char)c;
        else if (c < 0x800) { buf[k++] = (char)(0xC0 | (c >> 6)); buf[k++] = (char)(0x80 | (c & 0x3F)); }
        else if (c < 0x10000) { buf[k++] = (char)(0xE0 | (c >> 12)); buf[k++] = (char)(0x80 | ((c >> 6) & 0x3F)); buf[k++] = (char)(0x80 | (c & 0x3F)); }
        else { buf[k++] = (char)(0xF0 | (c >> 18)); buf[k++] = (char)(0x80 | ((c >> 12) & 0x3F)); buf[k++] = (char)(0x80 | ((c >> 6) & 0x3F)); buf[k++] = (char)(0x80 | (c & 0x3F)); }
    }
    if (k) std::fwrite(buf, 1, k, f);
}

/// `print(value)`: writes the text of the value (Value.ToString(), or its ToString() method) and a line break.
inline Value print(Value v, OwnList* list) {
    Piece p;
    pieceOf(v, p, list);
    writeUtf16(p.p, p.n, stdout);
    std::fputc('\n', stdout);
    return Undef();
}

}  // namespace fire
