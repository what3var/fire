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
#define FIRE_UNUSED_LABEL __attribute__((unused))
#define FIRE_LIKELY(x) __builtin_expect(!!(x), 1)
#define FIRE_UNLIKELY(x) __builtin_expect(!!(x), 0)
#else
#define FIRE_COLD
#define FIRE_UNUSED_LABEL
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
// ---------------------------------------------------------------------------------------------------------------------
// The state of an exception that is unwinding the stack (see the exceptions section): every generated function checks the flag
// after a call that can throw and, if it is set, leaves its scopes and returns.
// ---------------------------------------------------------------------------------------------------------------------
enum UnwindKind : uint8_t { UW_NONE, UW_JUMP, UW_RETURN, UW_RETHROW, UW_RESUME };
struct Handler;
struct UnwindState {
    uint8_t active;
    uint8_t kind;
    Handler* target;      // the frame that is being unwound to
    Value value;          // the exception, the return value or the resume value
    uint32_t label;       // JUMP: the bytecode address to continue at
    uint64_t token;       // RESUME: which throw is resumed
    Value regs[4];        // JUMP: operand stack slots that the jump takes along (the completion of a finally block)
};
inline UnwindState g_unwind = {0, UW_NONE, nullptr, {}, 0, 0, {}};

/// A run-time error that the language reports as an exception (see the exceptions section): the exception is thrown, or - when
/// the program has no exceptions at all - it ends the program.
inline Value indexError(const char* what, int64_t index, int64_t length);
/// The use of a destroyed array or buffer (SPEC 2.5): a DestroyedException, or the end of the program when there are no exceptions.
inline Value destroyedError(Value leaf);

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
// Heap values. Two kinds of lifetime:
//  * strings (UTF-16, like the VM) and lambdas are values that are shared freely (`var t = s`, stored in fields and arrays, returned).
//    They are *counted*:
//      - a *storage location* (variable, parameter, field, array element, static) holds one count of what it contains;
//      - a stack temporary holds nothing: a fresh value is pushed on the *temporary pool* (count 1) and the scope that created it
//        releases everything above its mark when it is left (so a temporary lives until the end of its scope);
//      - `return` retains the value for the trip and the caller adopts it onto its pool (see `adopt`).
//    Constants live in static storage and are immortal (never counted).
//  * objects, arrays and byte buffers have exactly one *owner* (SPEC 2): a scope or another object. They are destroyed with it and
//    never counted (see the ownership section).
// ---------------------------------------------------------------------------------------------------------------------
constexpr uint32_t IMMORTAL = 0xFFFFFFFFu;
enum RefType : uint8_t { R_Str, R_Lam };

struct Ref {
    uint32_t rc;     // number of holders; IMMORTAL for constants
    uint8_t type;    // RefType
};

/// Immutable UTF-16 string. A heap string stores its characters right behind the header.
struct Str : Ref {
    uint32_t length;
    const char16_t* data;
};

/// A lambda value: the function, the values it captured when it was created (copies, SPEC 4.2.1) and the `on` target (`this`).
struct alignas(alignof(Value)) Lam : Ref {
    uint32_t nparams;
    uint32_t ncaps;
    Value on;
    Value (*fn)(Value lam, const Value* args);
    Value* caps() { return reinterpret_cast<Value*>(this + 1); }
};

// Owned values: the header they share. An owner is an OwnList (for a scope a local of the generated function, for an object the
// `owned` list in its header); it holds its members in creation order, which is the order they are destroyed in.
struct OwnList;
enum OwnedKind : uint8_t { O_Object, O_Array, O_Buffer };

struct Owned {
    Owned* prev;
    Owned* next;
    OwnList* owner;    // the list this value is in; null while it travels as a return value to its new owner
    uint32_t slot;     // arrays and buffers: the slot in the handle table (see below)
    uint16_t gen;      // ... and its generation
    uint8_t okind;     // OwnedKind
    uint8_t flags;     // bit 0: destroyed (or being destroyed)
};

struct OwnList {
    Owned* head;
    Owned* tail;
    uint32_t mark;     // scopes: height of the temporary pool when the scope was entered
    OwnList* parent;   // scopes: the enclosing scope (TakeUpwards; set only when the program uses it)
    Owned* holder;     // object lists: the object that owns the list (null for a scope)
};

/// Fixed-size array of Values (elements start as `undefined`). It owns the inner arrays that were made together with it
/// (`new int[3][4]`, `[[1, 2], [3]]`), not the elements that are assigned later.
struct alignas(alignof(Value)) Arr : Owned {
    uint32_t length;
    OwnList* parts;
    Value* items() { return reinterpret_cast<Value*>(this + 1); }
};

/// Byte buffer (`byte[]`).
struct Buf : Owned {
    uint32_t length;
    uint8_t* bytes() { return reinterpret_cast<uint8_t*>(this + 1); }
};

// ---- Handles. A destroyed array or buffer must not be used any more (SPEC 2.5): in the checked modes a Value of an array or
// buffer carries the slot of its header in a table of generations (`unit` = slot, `reserved` = generation); the slot of a destroyed
// value gets a new generation, so a stale Value is recognized before the freed memory is touched. With FIRE_UNCHECKED
// (`#performance`) there is no table and nothing is checked.
#ifndef FIRE_UNCHECKED
struct SlotTable {
    uint16_t* gen;
    uint32_t* freed;
    uint32_t count, cap, nfree, freedCap;
};
inline SlotTable g_slots = {nullptr, nullptr, 0, 0, 0, 0};

[[noreturn]] FIRE_COLD inline void outOfMemory() { fatal("Out of memory."); }

FIRE_COLD inline uint32_t slotGrow() {
    uint32_t cap = g_slots.cap ? g_slots.cap * 2 : 256;
    uint16_t* gen = static_cast<uint16_t*>(std::realloc(g_slots.gen, cap * sizeof(uint16_t)));
    if (!gen) outOfMemory();
    std::memset(gen + g_slots.cap, 0, (cap - g_slots.cap) * sizeof(uint16_t));
    g_slots.gen = gen;
    g_slots.cap = cap;
    return cap;
}
inline void slotAcquire(Owned* o) {
    uint32_t slot;
    if (g_slots.nfree) slot = g_slots.freed[--g_slots.nfree];
    else {
        if (FIRE_UNLIKELY(g_slots.count == g_slots.cap)) slotGrow();
        slot = g_slots.count++;
    }
    o->slot = slot;
    o->gen = g_slots.gen[slot];
}
inline void slotRelease(Owned* o) {
    uint32_t slot = o->slot;
    g_slots.gen[slot] = (uint16_t)(g_slots.gen[slot] + 1);
    if (g_slots.nfree == g_slots.freedCap) {
        uint32_t cap = g_slots.freedCap ? g_slots.freedCap * 2 : 256;
        uint32_t* freed = static_cast<uint32_t*>(std::realloc(g_slots.freed, cap * sizeof(uint32_t)));
        if (!freed) outOfMemory();
        g_slots.freed = freed;
        g_slots.freedCap = cap;
    }
    g_slots.freed[g_slots.nfree++] = slot;
}
#else
inline void slotAcquire(Owned* o) { o->slot = 0; o->gen = 0; }
inline void slotRelease(Owned*) {}
#endif

/// Is the array or buffer behind this value still alive?
inline bool leafAlive(Value v) {
#ifdef FIRE_UNCHECKED
    (void)v;
    return true;
#else
    return FIRE_LIKELY(g_slots.gen[v.unit] == v.reserved);
#endif
}

inline Value StrV(const Str* s) { Value r; r.kind = K_String; r.width = 0; r.reserved = 0; r.unit = 0; r.p = s; return r; }
inline Value ArrV(Arr* a) { Value r; r.kind = K_Array; r.width = 0; r.reserved = a->gen; r.unit = a->slot; r.p = a; return r; }
inline Value BufV(Buf* b) { Value r; r.kind = K_Buffer; r.width = 0; r.reserved = b->gen; r.unit = b->slot; r.p = b; return r; }

inline bool isRef(Value v) { return ((0x50u >> v.kind) & 1u) != 0; }  // String, Lambda (counted values)
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
// Objects and ownership (SPEC 2): every object, array and byte buffer has exactly one owner - a scope or another object - and is
// destroyed (for an object its destruct() runs, then everything it owns) when the owner is destroyed. An owner is an OwnList: for
// a scope a local of the generated function, for an object the `owned` list in its header. Destroyed values are freed.
//
// Differences to the VM: a reference to a destroyed *object* is not detected (the VM keeps destroyed objects readable); a destroyed
// array or buffer is (see the handles above).
// ---------------------------------------------------------------------------------------------------------------------
struct alignas(alignof(Value)) Obj : Owned {
    uint32_t cls;      // class id (assigned by the generator)
    uint32_t nfields;
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

inline void link(OwnList* list, Owned* o) {
    o->owner = list;
    o->prev = list->tail;
    o->next = nullptr;
    if (list->tail) list->tail->next = o; else list->head = o;
    list->tail = o;
}

inline void unlink(Owned* o) {
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
    o->okind = O_Object;
    o->slot = 0;
    o->gen = 0;
    o->cls = cls;
    o->flags = 0;
    o->owned.head = o->owned.tail = nullptr;
    o->owned.mark = 0;
    o->owned.parent = nullptr;
    o->owned.holder = o;
    o->nfields = fieldCount;
    Value* f = o->fields();
    for (uint32_t i = 0; i < fieldCount; i++) f[i] = Undef();
    link(owner, o);
    return ObjV(o);
}

inline void destroyList(OwnList* list);
inline void destroyLeaf(Owned* o);

inline void destroy(Obj* o) {
    if (o->flags & 1) return;
    o->flags |= 1;
    if (FIRE_UNLIKELY(g_unwind.active)) {
        // the scope is left because of an exception: the destructor runs as ordinary code, the exception keeps unwinding afterwards
        UnwindState saved = g_unwind;
        g_unwind.active = 0;
        runDestructors(o);
        g_unwind = saved;
    } else runDestructors(o);
    destroyList(&o->owned);
    Value* f = o->fields();
    for (uint32_t i = 0; i < o->nfields; i++) release(f[i]);
#ifndef FIRE_KEEP_DESTROYED
    std::free(o);
#endif
}

/// Destroys everything on the list, in creation order, and empties it.
inline void destroyList(OwnList* list) {
    Owned* o = list->head;
    list->head = list->tail = nullptr;
    while (o) {
        Owned* next = o->next;
        o->owner = nullptr;
        if (o->okind == O_Object) destroy(static_cast<Obj*>(o));
        else destroyLeaf(o);
        o = next;
    }
}

/// An array or buffer is destroyed: what it holds is released, the inner arrays it owns are destroyed, its handle expires.
inline void destroyLeaf(Owned* o) {
    if (o->flags & 1) return;
    o->flags |= 1;
    if (o->owner) unlink(o);
    if (o->okind == O_Array) {
        Arr* a = static_cast<Arr*>(o);
        Value* items = a->items();
        for (uint32_t i = 0; i < a->length; i++) release(items[i]);
        if (a->parts) { destroyList(a->parts); std::free(a->parts); }
    }
    slotRelease(o);
    std::free(o);
}

/// Leaving a scope: destroys what it owns and lets go of the strings/arrays it created.
inline void leave(OwnList* list) {
    if (list->head) destroyList(list);
    if (g_pool.top > list->mark) poolRelease(list->mark);
}

/// The header of an object, an array or a buffer (null for any other value, and for an array or buffer that is already destroyed).
inline Owned* ownedOf(Value v) {
    if (v.kind == K_Class) return static_cast<Owned*>(const_cast<void*>(v.p));
    if ((v.kind == K_Array || v.kind == K_Buffer) && leafAlive(v)) return static_cast<Owned*>(const_cast<void*>(v.p));
    return nullptr;
}

/// `return`: an object (array, buffer) returned from the scope that owns it does not die with it; it goes to the caller (SPEC 2.3).
inline void transferOut(Value v, OwnList* list) {
    Owned* o = ownedOf(v);
    if (o && o->owner == list) unlink(o);
}

/// The caller takes over what came back from a call: an object, array or buffer without an owner, or a string/lambda that the
/// callee retained for the trip (that count now belongs to the scope's pool).
inline void adopt(Value v, OwnList* list) {
    if (Owned* o = ownedOf(v)) {
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

/// A new array; it belongs to `list` (the innermost scope of the creator, or the object that it is assigned to directly).
inline Arr* allocArr(uint32_t length, OwnList* list) {
    Arr* a = static_cast<Arr*>(std::malloc(sizeof(Arr) + (size_t)length * sizeof(Value)));
    if (FIRE_UNLIKELY(!a)) allocFailed();
    a->okind = O_Array;
    a->flags = 0;
    a->length = length;
    a->parts = nullptr;
    Value* items = a->items();
    for (uint32_t i = 0; i < length; i++) items[i] = Undef();
    slotAcquire(a);
    link(list, a);
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

/// A value does not have the unit that its declaration demands (an UnitMismatchException; defined with the exceptions).
inline void unitMismatch(Value v, Value expectedText);

/// CheckUnit: the value must have exactly this unit.
inline void checkUnit(Value v, uint32_t unit, Value expectedText) {
    uint32_t actual = (v.kind == K_Int || v.kind == K_Float || v.kind == K_Undefined) ? v.unit : 0;
    if (FIRE_UNLIKELY(actual != unit)) unitMismatch(v, expectedText);
}

inline Buf* allocBuf(uint32_t length, OwnList* list) {
    Buf* b = static_cast<Buf*>(std::malloc(sizeof(Buf) + length));
    if (FIRE_UNLIKELY(!b)) allocFailed();
    b->okind = O_Buffer;
    b->flags = 0;
    b->length = length;
    std::memset(b->bytes(), 0, length);
    slotAcquire(b);
    link(list, b);
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
            if (userToString(v, list, &text)) {
                if (FIRE_UNLIKELY(g_unwind.active || text.kind != K_String)) { out.p = out.buf; out.n = 0; return; }   // ToString() threw
                out.p = strOf(text)->data; out.n = strOf(text)->length;
            }
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
            if (start < 0 || start > (int64_t)len) return indexError("String index", start, len);
            return Int(findFrom(s, len, v.p(), v.n, (uint32_t)start));
        }
        case 2: {  // LastIndexOf(value[, start])
            Chars v = charsOf(a0);
            int64_t start = argc == 2 ? a1.i : (int64_t)len - 1;
            if (len == 0) return Int(v.n == 0 ? 0 : -1);
            if (start < 0 || start >= (int64_t)len) return indexError("String index", start, len);
            if (v.n == 0) return Int(start + 1);
            for (int64_t i = start - (int64_t)v.n + 1; i >= 0; i--)
                if (std::memcmp(s + i, v.p(), v.n * sizeof(char16_t)) == 0) return Int(i);
            return Int(-1);
        }
        case 3: {  // Substring(start[, count])
            int64_t start = a0.i;
            if (start < 0 || start > (int64_t)len) return indexError("String index", start, len);
            int64_t count = argc == 2 ? a1.i : (int64_t)len - start;
            if (count < 0 || count > (int64_t)len - start) return indexError("String index", start + count, len);
            if (start == 0 && count == (int64_t)len) return text;
            return newStrFrom(s + start, (uint32_t)count, list);
        }
        case 4: {  // CharAt(index)
            if (a0.i < 0 || a0.i >= (int64_t)len) return indexError("String index", a0.i, len);
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
            if (width < 0 || width > 0x7FFFFFFF) return indexError("String index", width, len);
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
// Exceptions (SPEC 7): throw, try/catch/finally and resume.
//
// A throw does not unwind by itself. `throwValue` walks the handler stack from the top (the innermost active `try`):
//  * a handler with a matching catch clause runs that catch block *on top of the throw site* (a callback into the
//    function that owns the `try`), so the frames of the throw site are still alive and `resume(value)` can continue there;
//  * a handler without a match - or a finally-only handler - is not entered directly: the frames above its function are
//    unwound first (status flag `g_unwind`, every generated function cleans up its scopes and returns), and its function
//    then rethrows (after running its `finally`).
// When a catch block ends it leaves through `g_unwind` as well: jump to the code after the `try`, `return`, or resume.
// ---------------------------------------------------------------------------------------------------------------------

struct HandlerInfo {
    uint32_t ncatch;
    const int32_t* types;   // per catch clause: the type id for excMatches, -1 = catches everything
    uint8_t hasFinally;
};

struct Handler {
    Handler* prev;
    const HandlerInfo* info;
    void (*fn)(void* ctx, uint32_t catchIndex, Value exception);   // runs the catch block (generated)
    void* ctx;
    Handler* origin;      // the handler this one stands for (a finally-only handler stands for the one that is running its catch)
    uint8_t finallyOnly;
};


inline Handler* g_handlers = nullptr;
inline OwnList* g_globalOwn = nullptr;   // the global scope: thrown exceptions belong to it (SPEC 7.6)

/// Generated: does the exception match the catch type (`typeId` as stored in HandlerInfo::types)?
bool excMatches(Value exception, int32_t typeId);
/// Generated: the name of a class, for the report of an unhandled exception.
const char* className(uint32_t cls);
/// Generated: builds an IndexOutOfBoundsException (message, index, length) owned by the global scope.
Value makeIndexError(Value message, int64_t index, int64_t length);
/// Generated: builds a UnitMismatchException (message, expected unit, actual unit) owned by the global scope.
Value makeUnitError(Value message, Value expected, Value actual);

template <class L> void handlerTrampoline(void* ctx, uint32_t catchIndex, Value exception) { (*static_cast<L*>(ctx))(catchIndex, exception); }

inline void pushHandler(Handler* h) { h->prev = g_handlers; h->origin = h; h->finallyOnly = 0; g_handlers = h; }
inline void popHandler() { g_handlers = g_handlers->prev; }
inline void clearUnwind() { g_unwind.active = 0; g_unwind.kind = UW_NONE; g_unwind.target = nullptr; }
inline void unwindTo(uint8_t kind, Handler* target, Value value, uint32_t label) {
    g_unwind.active = 1; g_unwind.kind = kind; g_unwind.target = target ? target->origin : nullptr; g_unwind.value = value; g_unwind.label = label;
}
/// Leaving a catch block: continue at `label` in the function that owns the handler.
inline void exitJump(Handler* h, uint32_t label) { unwindTo(UW_JUMP, h, Undef(), label); }
/// `return` from inside a catch block: the function that owns the handler returns `value`.
inline void exitReturn(Handler* h, Value value) { unwindTo(UW_RETURN, h, value, 0); }

/// A throw whose catch block may still call resume (innermost record first).
struct Pending {
    Pending* prev;
    Value exception;
    uint64_t token;
    uint8_t resumable;
    uint8_t cleared;
};
inline Pending* g_pending = nullptr;
inline uint64_t g_throwToken = 0;

inline void takeGlobal(Obj* o) {
    if (o->owner) unlink(o);
    link(g_globalOwn, o);
}

FIRE_COLD inline void reportUnhandled(Value exception) {
    std::fflush(stdout);
    Obj* o = asObj(exception);
    std::fprintf(stderr, "Unhandled exception of class '%s'.\n", className(o->cls));
}

/// `throw exception`. Returns the resume value if the exception is resumed at this very point; otherwise it returns with
/// `g_unwind.active` set and the caller has to unwind (the generated code checks the flag after every call).
inline Value throwValue(Value exception, bool resumable = true) {
    if (FIRE_UNLIKELY(exception.kind != K_Class)) fatal("throw expects an exception object.");
    takeGlobal(asObj(exception));
    Handler* h = g_handlers;
    if (!h) {   // nothing catches it: the program ends here (like the VM, without unwinding the stack)
        reportUnhandled(exception);
        std::exit(1);
    }
    g_handlers = h->prev;
    if (!h->finallyOnly) {
        int found = -1;
        for (uint32_t i = 0; i < h->info->ncatch && found < 0; i++)
            if (h->info->types[i] < 0 || excMatches(exception, h->info->types[i])) found = (int)i;
        if (found >= 0) {
            Pending pending = {g_pending, exception, ++g_throwToken, (uint8_t)(resumable ? 1 : 0), 0};
            g_pending = &pending;
            Handler fin;
            if (h->info->hasFinally) {   // while the catch block runs, an exception that leaves it must still run the finally
                fin.prev = g_handlers; fin.info = h->info; fin.fn = nullptr; fin.ctx = nullptr; fin.origin = h->origin; fin.finallyOnly = 1;
                g_handlers = &fin;
            }
            Handler* saved = h->prev;
            h->fn(h->ctx, (uint32_t)found, exception);
            g_pending = pending.prev;
            if (g_handlers == &fin) g_handlers = fin.prev;   // the catch block was left with its finally still armed: it is not on the stack any more
            if (g_unwind.active && g_unwind.kind == UW_RESUME && g_unwind.token == pending.token) {
                Value v = g_unwind.value;
                clearUnwind();
                h->prev = saved;      // the resumed code is still inside the try block: its handler is armed again
                g_handlers = h;
                return v;
            }
            if (!g_unwind.active) fatal("internal error: a catch block ended without leaving.");
            return Undef();
        }
    }
    // no catch clause here: unwind to the function of this handler, which rethrows (after its finally)
    unwindTo(UW_RETHROW, h, exception, 0);
    return Undef();
}

/// `e.resume(value)`: continue at the throw that `e` belongs to, which then evaluates to `value`.
inline void resumeThrow(Value exception, Value value) {
    for (Pending* p = g_pending; p; p = p->prev)
        if (!p->cleared && p->exception.p == exception.p) {
            if (!p->resumable) fatal("resume() across a finally block or a rethrow is not supported by the native backend.");
            p->cleared = 1;
            retain(value);
            g_unwind.active = 1; g_unwind.kind = UW_RESUME; g_unwind.target = nullptr; g_unwind.value = value; g_unwind.token = p->token;
            return;
        }
    fatal("resume() was called, but this exception is not being handled right now.");
}

/// The end of a catch block that did not resume: the throw site is given up.
inline void clearPending(Value exception) {
    for (Pending* p = g_pending; p; p = p->prev)
        if (!p->cleared && p->exception.p == exception.p) { p->cleared = 1; return; }
}

/// Run-time errors the language reports as exceptions (index out of range).
inline Value indexError(const char* what, int64_t index, int64_t length) {
    char text[120];
    int n = std::snprintf(text, sizeof text, "%s %lld out of range (length %lld).", what, (long long)index, (long long)length);
#ifdef FIRE_EXCEPTIONS
    OwnList unused = {nullptr, nullptr, 0, nullptr, nullptr};
    Str* msg = allocStr((uint32_t)n, &unused);
    widenAscii(text, (uint32_t)n, strChars(msg));
    return throwValue(makeIndexError(StrV(msg), index, length));
#else
    (void)n;
    fatal(text);
#endif
}

inline void unitMismatch(Value v, Value expectedText) {
#ifdef FIRE_EXCEPTIONS
    uint32_t actualUnit = (v.kind == K_Int || v.kind == K_Float || v.kind == K_Undefined) ? v.unit : 0;
    char16_t actualText[96];
    uint32_t an = actualUnit ? utf8ToUtf16(g_unitNames[actualUnit], actualText, 80) : 0;
    if (!actualUnit) { const char* none = "(no unit)"; an = widenAscii(none, 9, actualText); }
    const Str* expected = strOf(expectedText);
    OwnList unused = {nullptr, nullptr, 0, nullptr, nullptr};
    Str* msg = allocStr(15 + expected->length + 7 + an + 1, &unused);
    char16_t* d = strChars(msg);
    uint32_t n = widenAscii("Expected unit '", 15, d);
    std::memcpy(d + n, expected->data, expected->length * sizeof(char16_t)); n += expected->length;
    n += widenAscii("', got: ", 8, d + n);
    std::memcpy(d + n, actualText, an * sizeof(char16_t)); n += an;
    d[n++] = u'.';
    msg->length = n;
    Value actual = newStrFrom(actualText, an, &unused);
    throwValue(makeUnitError(StrV(msg), expectedText, actual));
#else
    (void)v; (void)expectedText;
    fatal("Incompatible units.");
#endif
}

/// Generated: builds a DestroyedException (message) owned by the global scope.
Value makeDestroyedError(Value message);

inline Value destroyedError(Value leaf) {
    const char* text = leaf.kind == K_Buffer ? "Access to a destroyed buffer." : "Access to a destroyed array.";
#ifdef FIRE_EXCEPTIONS
    OwnList unused = {nullptr, nullptr, 0, nullptr, nullptr};
    uint32_t n = (uint32_t)std::strlen(text);
    Str* msg = allocStr(n, &unused);
    widenAscii(text, n, strChars(msg));
    return throwValue(makeDestroyedError(StrV(msg)));
#else
    fatal(text);
#endif
}

// ---------------------------------------------------------------------------------------------------------------------
// Arrays and byte buffers
// ---------------------------------------------------------------------------------------------------------------------

/// `a[i]` for arrays, buffers and strings (objects with a GetIndex method are handled by the generated code).
inline Value arrayGet(Value a, Value i) {
    if (FIRE_LIKELY(a.kind == K_Array && i.kind == K_Int)) {
        if (FIRE_UNLIKELY(!leafAlive(a))) return destroyedError(a);
        Arr* arr = arrOf(a);
        if (FIRE_UNLIKELY((uint64_t)i.i >= arr->length)) return indexError("Array index", i.i, arr->length);
        return arr->items()[i.i];
    }
    if (i.kind != K_Int) fatal("An index must be an int.");
    if (a.kind == K_Buffer) {
        if (FIRE_UNLIKELY(!leafAlive(a))) return destroyedError(a);
        Buf* b = bufOf(a);
        if ((uint64_t)i.i >= b->length) return indexError("Array index", i.i, b->length);
        return Int(b->bytes()[i.i]);
    }
    if (a.kind == K_String) {
        const Str* s = strOf(a);
        if ((uint64_t)i.i >= s->length) return indexError("String index", i.i, s->length);
        return Char(s->data[i.i]);
    }
    fatal("Index access ('[]') is not possible on this value.");
}

/// `a[i] = v`; the array holds a count of the new element and lets go of the old one.
inline void arraySet(Value a, Value i, Value v) {
    if (FIRE_LIKELY(a.kind == K_Array && i.kind == K_Int)) {
        if (FIRE_UNLIKELY(!leafAlive(a))) { destroyedError(a); return; }
        Arr* arr = arrOf(a);
        if (FIRE_UNLIKELY((uint64_t)i.i >= arr->length)) { indexError("Array index", i.i, arr->length); return; }
        Value old = arr->items()[i.i];
        arr->items()[i.i] = v;
        retain(v);
        release(old);
        return;
    }
    if (i.kind != K_Int) fatal("An index must be an int.");
    if (a.kind == K_Buffer) {
        if (v.kind != K_Int) fatal("Assigning to a byte buffer expects an int value.");
        if (FIRE_UNLIKELY(!leafAlive(a))) { destroyedError(a); return; }
        Buf* b = bufOf(a);
        if ((uint64_t)i.i >= b->length) { indexError("Array index", i.i, b->length); return; }
        b->bytes()[i.i] = (uint8_t)v.i;
        return;
    }
    if (a.kind == K_String) fatal("Strings are immutable.");
    fatal("Index access ('[]') is not possible on this value.");
}

// ---------------------------------------------------------------------------------------------------------------------
// `ref` parameters (SPEC 5.4.2): the argument is a pointer to the variable, the field or the element of the caller.
// A pointer is a K_Pointer value; `width` 0 points to a Value (variable, field, array element), 1 to a byte (buffer element).
// ---------------------------------------------------------------------------------------------------------------------
inline Value PtrV(Value* p) { Value r; r.kind = K_Pointer; r.width = 0; r.reserved = 0; r.unit = 0; r.p = p; return r; }
inline Value BytePtrV(uint8_t* p) { Value r; r.kind = K_Pointer; r.width = 1; r.reserved = 0; r.unit = 0; r.p = p; return r; }

inline Value ptrRead(Value p) {
    if (FIRE_UNLIKELY(p.kind != K_Pointer)) fatal("Dereference of a value that is not a pointer.");
    return p.width ? Int(*static_cast<const uint8_t*>(p.p)) : *static_cast<const Value*>(p.p);
}

/// `*p = v`: the storage holds a count of what it holds now.
inline void ptrWrite(Value p, Value v) {
    if (FIRE_UNLIKELY(p.kind != K_Pointer)) fatal("Assignment through a value that is not a pointer.");
    if (p.width) { *static_cast<uint8_t*>(const_cast<void*>(p.p)) = (uint8_t)v.i; return; }
    Value* target = static_cast<Value*>(const_cast<void*>(p.p));
    Value old = *target;
    *target = v;
    retain(v);
    release(old);
}

/// A call site that passes an address to a method of which not every implementation takes a `ref`: the others get the value.
inline Value derefArg(Value v) { return v.kind == K_Pointer ? ptrRead(v) : v; }

inline void requireRef(Value v, const char* name) {
    if (FIRE_UNLIKELY(v.kind != K_Pointer)) {
        std::fflush(stdout);
        std::fprintf(stderr, "fire runtime error: Parameter '%s' is declared 'ref': pass a variable, a field or an array element, not a value.\n", name);
        std::exit(1);
    }
}

/// `x[i]` as a `ref` argument.
inline Value addressOfIndex(Value a, Value i) {
    if (i.kind != K_Int) fatal("An index must be an int.");
    if (a.kind == K_Array) {
        if (FIRE_UNLIKELY(!leafAlive(a))) return destroyedError(a);
        Arr* arr = arrOf(a);
        if ((uint64_t)i.i >= arr->length) return indexError("Array index", i.i, arr->length);
        return PtrV(&arr->items()[i.i]);
    }
    if (a.kind == K_Buffer) {
        if (FIRE_UNLIKELY(!leafAlive(a))) return destroyedError(a);
        Buf* b = bufOf(a);
        if ((uint64_t)i.i >= b->length) return indexError("Array index", i.i, b->length);
        return BytePtrV(&b->bytes()[i.i]);
    }
    fatal("A 'ref' argument 'x[i]' expects an array or a byte buffer.");
}

inline Value lengthOf(Value v, bool* ok) {
    *ok = true;
    if (v.kind == K_Array) { if (FIRE_UNLIKELY(!leafAlive(v))) return destroyedError(v); return Int(arrOf(v)->length); }
    if (v.kind == K_String) return Int(strOf(v)->length);
    if (v.kind == K_Buffer) { if (FIRE_UNLIKELY(!leafAlive(v))) return destroyedError(v); return Int(bufOf(v)->length); }
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
// Ownership of arrays and buffers (SPEC 2.5): inner arrays, Take..., delete
// ---------------------------------------------------------------------------------------------------------------------
inline int64_t sizeArg(Value v) {
    if (FIRE_UNLIKELY(v.kind != K_Int)) fatal("An array size must be an int.");
    return v.i;
}

inline OwnList* newPartsList(Owned* holder) {
    OwnList* parts = static_cast<OwnList*>(std::malloc(sizeof(OwnList)));
    if (FIRE_UNLIKELY(!parts)) allocFailed();
    *parts = {nullptr, nullptr, 0, nullptr, holder};
    return parts;
}

inline void fillJagged(Arr* arr, const int64_t* sizes, int ranks, int level, OwnList* parts) {
    for (uint32_t i = 0; i < arr->length; i++) {
        Arr* inner = allocArr((uint32_t)sizes[level], parts);
        arr->items()[i] = ArrV(inner);
        if (level + 1 < ranks) fillJagged(inner, sizes, ranks, level + 1, parts);
    }
}

/// `new T[a][b]...`: the inner arrays belong to the outer one.
inline Value newJagged(const int64_t* sizes, int ranks, OwnList* list) {
    for (int i = 0; i < ranks; i++)
        if (sizes[i] < 0 || sizes[i] > 0x7FFFFFFF) fatal("An array size must be a non-negative int.");
    Arr* outer = allocArr((uint32_t)sizes[0], list);
    if (ranks > 1 && outer->length > 0) {
        outer->parts = newPartsList(outer);
        fillJagged(outer, sizes, ranks, 1, outer->parts);
    }
    return ArrV(outer);
}

/// An array that was made inside the expression of its outer array (`[[1, 2], [3]]`) belongs to it now.
inline void attachPart(Value outer, Value inner) {
    Owned* o = ownedOf(inner);
    if (!o) return;
    Arr* arr = arrOf(outer);
    if (o->owner) unlink(o);
    if (!arr->parts) arr->parts = newPartsList(arr);
    link(arr->parts, o);
}

/// Gives an object, array or buffer to the object `target` (TakeTo, a direct field assignment): when the target is already being
/// destroyed the value goes with it at once (SPEC 2.2); an object must not become an owner of itself or of its owners.
inline void takeToObject(Owned* o, Obj* target) {
    if (target->flags & 1) {
        if (o->owner) unlink(o);
        if (o->okind == O_Object) destroy(static_cast<Obj*>(o)); else destroyLeaf(o);
        return;
    }
    if (o->okind == O_Object)
        for (Owned* x = target;;) {
            if (x == o) fatal("TakeTo: cycle detected - the target object is already owned (directly or transitively) by this object.");
            OwnList* l = x->owner;
            if (!l || !l->holder) break;
            x = l->holder;
        }
    if (o->owner) unlink(o);
    link(&target->owned, o);
}

/// `obj.field = <fresh value>`: the value belongs to the object - when it is still in the hands of this function (it has no owner or
/// is in one of the function's scopes `lists`); something that belongs to others (an object, the caller) stays where it is (SPEC 2.1).
template <class... L>
inline Value ownValue(Value owner, Value v, L*... lists) {
    if (Owned* o = ownedOf(v))
        if (!o->owner || ((o->owner == lists) || ...)) takeToObject(o, asObj(owner));
    return v;
}

/// `x = value` with a variable of an outer scope: what belongs to the inner scope `from` moves to the function scope `to`
/// (SPEC 2.1) - never out of the function.
inline void hoistFrom(Value v, OwnList* from, OwnList* to) {
    Owned* o = ownedOf(v);
    if (o && o->owner == from) { unlink(o); link(to, o); }
}

/// `f(g())`: the result of a call passed straight on belongs to the callee, not to the caller (SPEC 2.1). The generated code lets the
/// value travel in the argument list `al` of the call and destroys what is left in it afterwards (the callee has moved, stored or
/// returned what it wants to keep).
inline void reownArg(Value v, OwnList* from, OwnList* al) {
    Owned* o = ownedOf(v);
    if (o && o->owner == from) { unlink(o); link(al, o); }
    al->parent = from;
}
inline void finishArgs(Value result, OwnList* al) {
    transferOut(result, al);
    if (al->head) destroyList(al);
}

enum OwnMethod { OM_Take, OM_TakeUpwards, OM_TakeGlobal, OM_TakeTo };

/// `x.Take()`, `x.TakeUpwards()`, `x.TakeGlobal()`, `x.TakeTo(obj)` on an object, an array or a buffer (SPEC 2.2).
inline void ownMethod(int method, Value self, Value arg, OwnList* here) {
    Owned* o = ownedOf(self);
    if (!o) fatal("Take... is only possible for an object, an array or a buffer that is not destroyed.");
    if (o->flags & 1) fatal("A destroyed value cannot change its owner.");
    switch (method) {
        case OM_Take:
            if (o->owner) unlink(o);
            link(here, o);
            break;
        case OM_TakeUpwards: {
            OwnList* current = o->owner;
            if (!current || current->holder) fatal("TakeUpwards is only valid if the current owner is a scope.");
            if (!current->parent) fatal("TakeUpwards: the current scope has no parent scope (already global).");
            unlink(o);
            link(current->parent, o);
            break;
        }
        case OM_TakeGlobal:
            if (o->owner) unlink(o);
            link(g_globalOwn, o);
            break;
        default:
            takeToObject(o, asObj(arg));
            break;
    }
}

/// `delete x`: destroys the object (destructor, everything it owns), array or buffer at once.
inline void deleteValue(Value v) {
    if (v.kind == K_Class) {
        Obj* o = static_cast<Obj*>(const_cast<void*>(v.p));
        if (o->owner) unlink(o);
        destroy(o);
    } else if (v.kind == K_Array || v.kind == K_Buffer) {
        if (Owned* o = ownedOf(v)) destroyLeaf(o);
    } else fatal("'delete' expects an object, an array or a buffer.");
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
    if (FIRE_UNLIKELY(g_unwind.active)) return Undef();   // ToString() threw
    writeUtf16(p.p, p.n, stdout);
    std::fputc('\n', stdout);
    return Undef();
}

}  // namespace fire
