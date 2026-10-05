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

/// Real -> text like .NET Core 3.0+ `double.ToString()` / `float.ToString()`: shortest round-trip digits, positional
/// notation for 1E-4 <= |x| < 1E17 (double) or 1E9 (float), otherwise `d.dddE+XX`.
template <class T>
inline int formatFloating(T v, char* out) {
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
    if (exp10 >= (sizeof(T) == 4 ? 9 : 17) || exp10 < -4) {
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
inline int formatReal(Real v, char* out) { return formatFloating<Real>(v, out); }

// ---------------------------------------------------------------------------------------------------------------------
// Units (SPEC 3): a unit is a vector of exponents over the base symbols of the program (`g_dimNames`: m, s, apples, ...) and a scale
// factor to the base. `Value::unit` is the index into a table that grows at run time - `mm * mm` or `m / s` make new entries - and
// is interned (same dimensions, scale and display text = same index). Two indices can still mean equal units (`unitEq`).
// ---------------------------------------------------------------------------------------------------------------------
#ifndef FIRE_NDIMS
#define FIRE_NDIMS 1
#endif
/// The base symbols, in the order of the dimension vectors (sorted like the VM prints them). Defined by the generated code.
extern const char* const g_dimNames[];

struct UnitInit { int32_t dims[FIRE_NDIMS]; double scale; const char* display; };
struct UnitDef {
    int32_t dims[FIRE_NDIMS];
    double scale;
    const char* display;   // the symbol of a named unit (`mm`), null for a derived one
    const char* name;      // Unit.ToString()
};
inline UnitDef* g_ud = nullptr;
inline uint32_t g_udCount = 0, g_udCap = 0;

inline bool unitDimsZero(const int32_t* dims) {
    for (int i = 0; i < FIRE_NDIMS; i++) if (dims[i]) return false;
    return true;
}
/// Unit.IsUnitless: no dimensions (whatever the scale).
inline bool unitIsUnitless(uint32_t u) { return unitDimsZero(g_ud[u].dims); }
/// The text printed after a number: nothing for a unit without dimensions.
inline const char* unitSuffix(uint32_t u) { return unitIsUnitless(u) ? "" : g_ud[u].name; }

/// Unit.ToString(): `m/s^2`, `(m*s)`, a symbol if the unit has one, `(×scale)` when the scale is not 1.
inline char* unitBuildName(const int32_t* dims, double scale, const char* display) {
    char buf[384];
    size_t n = 0;
    auto put = [&](const char* text) { for (; *text && n + 1 < sizeof buf; text++) buf[n++] = *text; };
    if (unitDimsZero(dims)) put("unitless");
    else if (display) put(display);
    else {
        int numerators = 0, denominators = 0;
        char tmp[16];
        for (int i = 0; i < FIRE_NDIMS; i++) {
            if (dims[i] > 0) { if (numerators++) put("*"); put(g_dimNames[i]); if (dims[i] != 1) { std::snprintf(tmp, sizeof tmp, "^%d", (int)dims[i]); put(tmp); } }
        }
        if (!numerators) put("1");
        for (int i = 0; i < FIRE_NDIMS; i++) if (dims[i] < 0) denominators++;
        if (denominators) {
            put("/");
            if (denominators > 1) put("(");
            int k = 0;
            for (int i = 0; i < FIRE_NDIMS; i++)
                if (dims[i] < 0) { if (k++) put("*"); put(g_dimNames[i]); if (dims[i] != -1) { std::snprintf(tmp, sizeof tmp, "^%d", (int)-dims[i]); put(tmp); } }
            if (denominators > 1) put(")");
        }
        if (std::fabs(scale - 1.0) > 1e-12) {
            char num[48];
            put("(\xC3\x97");
            int len = formatFloating<double>(scale, num);
            num[len] = 0;
            put(num);
            put(")");
        }
    }
    char* out = static_cast<char*>(std::malloc(n + 1));
    if (!out) { std::fputs("fire runtime error: Out of memory.\n", stderr); std::exit(1); }
    std::memcpy(out, buf, n);
    out[n] = 0;
    return out;
}

inline void unitsInit(const UnitInit* init, uint32_t count) {
    g_udCap = count + 32;
    g_ud = static_cast<UnitDef*>(std::malloc(sizeof(UnitDef) * g_udCap));
    if (!g_ud) { std::fputs("fire runtime error: Out of memory.\n", stderr); std::exit(1); }
    for (uint32_t i = 0; i < count; i++) {
        std::memcpy(g_ud[i].dims, init[i].dims, sizeof g_ud[i].dims);
        g_ud[i].scale = init[i].scale;
        g_ud[i].display = init[i].display;
        g_ud[i].name = unitBuildName(init[i].dims, init[i].scale, init[i].display);
    }
    g_udCount = count;
}

/// The index of the unit with these dimensions, scale and display text (made when it is new).
inline uint32_t unitIntern(const int32_t* dims, double scale, const char* display) {
    for (uint32_t i = 0; i < g_udCount; i++) {
        const UnitDef& d = g_ud[i];
        if (d.scale == scale && std::memcmp(d.dims, dims, sizeof d.dims) == 0 && ((d.display == nullptr) == (display == nullptr)) && (!display || std::strcmp(d.display, display) == 0)) return i;
    }
    if (g_udCount == g_udCap) {
        g_udCap *= 2;
        g_ud = static_cast<UnitDef*>(std::realloc(g_ud, sizeof(UnitDef) * g_udCap));
        if (!g_ud) { std::fputs("fire runtime error: Out of memory.\n", stderr); std::exit(1); }
    }
    UnitDef& d = g_ud[g_udCount];
    std::memcpy(d.dims, dims, sizeof d.dims);
    d.scale = scale;
    d.display = display;
    d.name = unitBuildName(dims, scale, display);
    return g_udCount++;
}

inline bool unitCompatible(uint32_t a, uint32_t b) { return a == b || std::memcmp(g_ud[a].dims, g_ud[b].dims, sizeof g_ud[a].dims) == 0; }
/// Unit.Equals: same dimensions and the scales differ by less than 1e-12.
inline bool unitEq(uint32_t a, uint32_t b) { return a == b || (unitCompatible(a, b) && std::fabs(g_ud[a].scale - g_ud[b].scale) < 1e-12); }

[[noreturn]] FIRE_COLD inline void unitConflict(uint32_t from, uint32_t to) {
    std::fflush(stdout);
    std::fprintf(stderr, "Unhandled exception. fire.Values.UnitMismatchException: Incompatible units: '%s' cannot be converted to '%s'.\n", g_ud[from].name, g_ud[to].name);
    std::exit(1);
}
/// Unit.ConversionFactorTo
inline double unitFactor(uint32_t from, uint32_t to) {
    if (!unitCompatible(from, to)) unitConflict(from, to);
    return g_ud[from].scale / g_ud[to].scale;
}

/// Unit.Multiply / Unit.Divide: a unit without dimensions leaves the other one as it is; `mm * mm` is `mm^2`.
inline uint32_t unitMul(uint32_t a, uint32_t b) {
    if (unitIsUnitless(b)) return a;
    if (unitIsUnitless(a)) return b;
    const UnitDef& x = g_ud[a];
    const UnitDef& y = g_ud[b];
    int32_t dims[FIRE_NDIMS];
    for (int i = 0; i < FIRE_NDIMS; i++) dims[i] = x.dims[i] + y.dims[i];
    const char* display = nullptr;
    if (x.display && y.display && std::strcmp(x.display, y.display) == 0) {
        size_t l = std::strlen(x.display);
        char* square = static_cast<char*>(std::malloc(l + 3));
        if (!square) { std::fputs("fire runtime error: Out of memory.\n", stderr); std::exit(1); }
        std::memcpy(square, x.display, l);
        std::memcpy(square + l, "^2", 3);
        display = square;
    }
    double scale = x.scale * y.scale;
    uint32_t id = unitIntern(dims, scale, display);
    if (display && g_ud[id].display != display) std::free(const_cast<char*>(display));   // an equal unit existed
    return id;
}
inline uint32_t unitDiv(uint32_t a, uint32_t b) {
    if (unitIsUnitless(b)) return a;
    const UnitDef& x = g_ud[a];
    const UnitDef& y = g_ud[b];
    int32_t dims[FIRE_NDIMS];
    for (int i = 0; i < FIRE_NDIMS; i++) dims[i] = x.dims[i] - y.dims[i];
    return unitIntern(dims, x.scale / y.scale, nullptr);
}


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
    uint32_t nreq;     // parameters without a default value (they come first)
    Value on;
    Value (*fn)(Value lam, const Value* args);
    Value (*dflt)(Value lam, uint32_t index);   // the default value of parameter `index` (null: the lambda has none)
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
    uint8_t little;    // the byte order the buffer is tagged with (metadata only: the bytes never change by themselves)
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
// Arithmetic. Fast path: both numeric with the same unit. Different units (conversion, unit algebra: SPEC 3) go through the slow paths.
// ---------------------------------------------------------------------------------------------------------------------
// NOTE: every function takes its Values BY VALUE, never by reference (and the slow paths only the operand kinds). A
// reference to a local that reaches a call that is not inlined makes the local "escape", and the C++ compiler then
// keeps it - and, since the generated code reuses its stack temporaries s0..sN, all of them - in memory for the whole
// function instead of in registers. That alone made generated loops ~10x slower.
[[noreturn]] FIRE_COLD inline void opFailed(const char* op) {
    std::fflush(stdout);
    std::fprintf(stderr, "fire runtime error: operator '%s' is not supported for these operands in the native backend yet\n", op);
    std::exit(1);
}

struct VPair { Value a, b; };

/// Value.AlignUnits: operands of the same dimension but a different scale (`500mm + 2m`) are converted implicitly - two ints stay ints
/// (the finer unit is the target while the converted value does not overflow, else the coarser one and the fraction is cut off), a float
/// operand makes the right one a float in the unit of the left.
FIRE_COLD inline VPair alignUnits(Value a, Value b) {
    uint32_t ua = a.unit, ub = b.unit;
    if (unitEq(ua, ub)) return {a, b};
    if (!unitCompatible(ua, ub)) unitConflict(ua, ub);
    if (a.kind == K_Int && b.kind == K_Int) {
        bool aIsFine = g_ud[ua].scale <= g_ud[ub].scale;
        Value& fine = aIsFine ? a : b;
        Value& coarse = aIsFine ? b : a;
        uint32_t uFine = aIsFine ? ua : ub, uCoarse = aIsFine ? ub : ua;
        double up = (double)coarse.i * unitFactor(uCoarse, uFine);
        if (up >= -9.2e18 && up <= 9.2e18) coarse = Int((int64_t)up, uFine);
        else fine = Int((int64_t)((double)fine.i * unitFactor(uFine, uCoarse)), uCoarse);
        return {a, b};
    }
    double factor = unitFactor(ub, ua);
    b = Float((Real)((b.kind == K_Int ? (double)b.i : (double)b.f) * factor), ua);
    return {a, b};
}

FIRE_COLD inline Value addUnits(Value a, Value b) {
    VPair p = alignUnits(a, b);
    if (p.a.kind == K_Int && p.b.kind == K_Int) return Int((int64_t)((uint64_t)p.a.i + (uint64_t)p.b.i), p.a.unit);
    return Float(toR(p.a) + toR(p.b), p.a.unit);
}
FIRE_COLD inline Value subUnits(Value a, Value b) {
    VPair p = alignUnits(a, b);
    if (p.a.kind == K_Int && p.b.kind == K_Int) return Int((int64_t)((uint64_t)p.a.i - (uint64_t)p.b.i), p.a.unit);
    return Float(toR(p.a) - toR(p.b), p.a.unit);
}
FIRE_COLD inline Value modUnits(Value a, Value b) {
    VPair p = alignUnits(a, b);
    if (p.a.kind == K_Int && p.b.kind == K_Int) {
        if (FIRE_UNLIKELY(p.b.i == 0)) fatal("Division by zero.");
        if (FIRE_UNLIKELY(p.b.i == -1)) return Int(0, p.a.unit);
        return Int(p.a.i % p.b.i, p.a.unit);
    }
    return Float(std::fmod(toR(p.a), toR(p.b)), p.a.unit);
}
FIRE_COLD inline Value mulUnits(Value a, Value b) {
    uint32_t u = unitMul(a.unit, b.unit);
    if (a.kind == K_Int && b.kind == K_Int) return Int((int64_t)((uint64_t)a.i * (uint64_t)b.i), u);
    return Float(toR(a) * toR(b), u);
}
FIRE_COLD inline Value divUnits(Value a, Value b) {
    uint32_t u = unitDiv(a.unit, b.unit);
    if (a.kind == K_Int && b.kind == K_Int) {
        if (FIRE_UNLIKELY(b.i == 0)) fatal("Division by zero.");
        if (FIRE_UNLIKELY(b.i == -1)) return Int((int64_t)(0 - (uint64_t)a.i), u);
        return Int(a.i / b.i, u);
    }
    return Float(toR(a) / toR(b), u);
}

inline Value add(Value a, Value b) {
    if (FIRE_LIKELY(a.unit == b.unit)) {
        if (a.kind == K_Int && b.kind == K_Int) return Int((int64_t)((uint64_t)a.i + (uint64_t)b.i), a.unit);
        if (isNumeric(a) && isNumeric(b)) return Float(toR(a) + toR(b), a.unit);
    } else if (isNumeric(a) && isNumeric(b)) return addUnits(a, b);
    opFailed("+");
}
inline Value sub(Value a, Value b) {
    if (FIRE_LIKELY(a.unit == b.unit)) {
        if (a.kind == K_Int && b.kind == K_Int) return Int((int64_t)((uint64_t)a.i - (uint64_t)b.i), a.unit);
        if (isNumeric(a) && isNumeric(b)) return Float(toR(a) - toR(b), a.unit);
    } else if (isNumeric(a) && isNumeric(b)) return subUnits(a, b);
    opFailed("-");
}
inline Value mul(Value a, Value b) {
    if (FIRE_LIKELY(a.unit == 0 && b.unit == 0)) {
        if (a.kind == K_Int && b.kind == K_Int) return Int((int64_t)((uint64_t)a.i * (uint64_t)b.i));
        if (isNumeric(a) && isNumeric(b)) return Float(toR(a) * toR(b));
    } else if (isNumeric(a) && isNumeric(b)) return mulUnits(a, b);
    opFailed("*");
}
inline Value divide(Value a, Value b) {
    if (FIRE_LIKELY(a.unit == 0 && b.unit == 0)) {
        if (a.kind == K_Int && b.kind == K_Int) {
            if (FIRE_UNLIKELY(b.i == 0)) fatal("Division by zero.");
            if (FIRE_UNLIKELY(b.i == -1)) return Int((int64_t)(0 - (uint64_t)a.i));
            return Int(a.i / b.i);
        }
        if (isNumeric(a) && isNumeric(b)) return Float(toR(a) / toR(b));
    } else if (isNumeric(a) && isNumeric(b)) return divUnits(a, b);
    opFailed("/");
}
inline Value modulo(Value a, Value b) {
    if (FIRE_LIKELY(a.unit == b.unit)) {
        if (a.kind == K_Int && b.kind == K_Int) {
            if (FIRE_UNLIKELY(b.i == 0)) fatal("Division by zero.");
            if (FIRE_UNLIKELY(b.i == -1)) return Int(0, a.unit);
            return Int(a.i % b.i, a.unit);
        }
        if (isNumeric(a) && isNumeric(b)) return Float(std::fmod(toR(a), toR(b)), a.unit);
    } else if (isNumeric(a) && isNumeric(b)) return modUnits(a, b);
    opFailed("%");
}
/// `a ^ b` (power; `#` is the bitwise xor): ints multiply, a float or a negative exponent uses pow. The units must be equal.
inline Value power(Value a, Value b) {
    if (FIRE_UNLIKELY(!isNumeric(a) || !isNumeric(b))) opFailed("^");
    if (FIRE_UNLIKELY(!unitEq(a.unit, b.unit))) unitConflict(a.unit, b.unit);
    bool useFloat = a.kind == K_Float || b.kind == K_Float || (b.kind == K_Int && b.i < 0);
    if (useFloat) return Float((Real)std::pow((double)toR(a), (double)toR(b)), a.unit);
    uint64_t result = 1, base = (uint64_t)a.i;
    for (int64_t i = 0; i < b.i; i++) result *= base;
    return Int((int64_t)result, a.unit);
}
/// The unit a value carries (only numbers and `undefined` can have one).
inline uint32_t unitOf(Value v) { return (v.kind == K_Int || v.kind == K_Float || v.kind == K_Undefined) ? v.unit : 0; }

/// `value:unit` - converts a number to another unit of the same dimension (Value.CoerceUnit); ints are rounded half to even.
inline Value coerceUnit(Value v, uint32_t target) {
    if (FIRE_UNLIKELY(!(v.kind == K_Int || v.kind == K_Float || v.kind == K_Undefined))) fatal("A value of this type carries no unit and cannot be converted.");
    if (unitEq(v.unit, target)) return v;
    double factor = unitFactor(v.unit, target);
    if (v.kind == K_Int) return Int((int64_t)std::nearbyint((double)v.i * factor), target);
    if (v.kind == K_Float) return Float((Real)((double)v.f * factor), target);
    return Undef(target);
}
/// `value!type` - int and float into each other, `undefined` into a number (Value.CoerceType).
inline Value coerceType(Value v, uint8_t target) {
    if (v.kind == target) return v;
    if (v.kind == K_Int && target == K_Float) return Float((Real)v.i, v.unit);
    if (v.kind == K_Float && target == K_Int) return Int((int64_t)std::nearbyint((double)v.f), v.unit);
    if (v.kind == K_Undefined && target == K_Int) return Int(0, v.unit);
    if (v.kind == K_Undefined && target == K_Float) return Float(0, v.unit);
    fatal("A value of this type cannot be coerced to the requested type.");
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
        if (FIRE_UNLIKELY(!unitEq(a.unit, b.unit))) unitConflict(a.unit, b.unit);                  \
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
inline int compareNumeric(Value a, Value b) {
    if (a.kind == K_Int && b.kind == K_Int) return a.i < b.i ? -1 : (a.i > b.i ? 1 : 0);
    Real x = toR(a), y = toR(b);
    if (x < y) return -1;
    if (x > y) return 1;
    if (x == y) return 0;
    if (std::isnan(x)) return std::isnan(y) ? 0 : -1;
    return 1;
}
inline int compare(Value a, Value b) {
    if (FIRE_LIKELY(a.unit == b.unit)) {
        if (isNumeric(a) && isNumeric(b)) return compareNumeric(a, b);
    } else if (isNumeric(a) && isNumeric(b)) { VPair p = alignUnits(a, b); return compareNumeric(p.a, p.b); }
    opFailed("<");
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

/// A value that came out of a function (a default argument), taken over by `list` like the result of a call.
inline Value adoptV(Value v, OwnList* list) {
    adopt(v, list);
    return v;
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
    l->nreq = nparams;
    l->on = Undef();
    l->fn = fn;
    l->dflt = nullptr;
    registerTemp(l, list);
    return l;
}

/// The `this` of a lambda body: its `on` target.
inline Value lamOn(Value lam) { return lamOf(lam)->on; }
inline Value lamCapture(Value lam, uint32_t index) { return lamOf(lam)->caps()[index]; }

/// `callee(args...)`
FIRE_COLD inline Value callLamDefaults(Value callee, int argc, const Value* args, OwnList* list) {
    Lam* l = lamOf(callee);
    if (!l->dflt || (uint32_t)argc > l->nparams || (uint32_t)argc < l->nreq || l->nparams > 16) fatal("A lambda was called with the wrong number of arguments.");
    Value full[16];
    for (int i = 0; i < argc; i++) full[i] = args[i];
    for (uint32_t i = (uint32_t)argc; i < l->nparams; i++) full[i] = adoptV(l->dflt(callee, i), list);
    return l->fn(callee, full);
}
inline Value callLam(Value callee, int argc, const Value* args, OwnList* list) {
    if (FIRE_UNLIKELY(callee.kind != K_Lambda)) fatal("Call of a value that is not a lambda.");
    Lam* l = lamOf(callee);
    if (FIRE_UNLIKELY(l->nparams != (uint32_t)argc)) return callLamDefaults(callee, argc, args, list);
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
    if (FIRE_UNLIKELY(!unitEq(actual, unit))) unitMismatch(v, expectedText);
}

/// The byte order of the machine, found out at run time (a 16-bit 1 starts with its low byte in little-endian memory).
inline bool hostLittle() { uint16_t one = 1; return *reinterpret_cast<uint8_t*>(&one) == 1; }

inline Buf* allocBuf(uint32_t length, OwnList* list) {
    Buf* b = static_cast<Buf*>(std::malloc(sizeof(Buf) + length));
    if (FIRE_UNLIKELY(!b)) allocFailed();
    b->okind = O_Buffer;
    b->flags = 0;
    b->length = length;
    b->little = hostLittle() ? 1 : 0;
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
        case K_Undefined: setPiece(out, "undefined"); if (v.unit && !unitIsUnitless(v.unit)) { out.buf[out.n++] = u':'; } break;
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
    if (v.unit && (v.kind == K_Int || v.kind == K_Float || v.kind == K_Undefined) && !unitIsUnitless(v.unit))
        out.n += utf8ToUtf16(g_ud[v.unit].name, out.buf + out.n, 80 - out.n);
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
    if (isNumeric(a) && isNumeric(b)) return addUnits(a, b);
    opFailed("+");
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
    if (letter == 'E') {
        // scientific: `1.234560E+004` (six decimals by default, an exponent of at least three digits, `e` in lower case for `e`)
        if (v.kind != K_Int && v.kind != K_Float) fatal("The format specifier expects a number.");
        double d = (double)toR(v);
        int prec = hasNumber ? (int)number : (spec->length == 1 ? 6 : -1);
        if (prec < 0) fatal("Unknown format specifier.");
        if (std::isnan(d)) n = std::snprintf(text, sizeof text, "NaN");
        else if (std::isinf(d)) n = std::snprintf(text, sizeof text, d < 0 ? "-Infinity" : "Infinity");
        else {
            char raw[400];
            int rn = std::snprintf(raw, sizeof raw, first == 'E' ? "%.*E" : "%.*e", prec, d);
            if (rn < 0 || rn >= (int)sizeof raw - 4) fatal("The formatted number is too long.");
            char* e = raw;
            while (*e && *e != 'E' && *e != 'e') e++;
            n = (int)(e - raw);
            std::memcpy(text, raw, (size_t)n);
            text[n++] = *e;
            text[n++] = e[1];                      // the sign
            int digits = (int)std::strlen(e + 2);
            for (int i = digits; i < 3; i++) text[n++] = '0';
            std::memcpy(text + n, e + 2, (size_t)digits);
            n += digits;
        }
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
    uint32_t an = actualUnit ? utf8ToUtf16(g_ud[actualUnit].name, actualText, 80) : 0;
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

/// Generated: builds an AccessDeniedException (message) owned by the global scope.
Value makeAccessError(Value message);

/// A private or protected member used from where it may not be (SPEC 5.7): an AccessDeniedException, or the end of the program when there are no exceptions.
inline Value accessDenied(const char* text) {
#ifdef FIRE_EXCEPTIONS
    OwnList unused = {nullptr, nullptr, 0, nullptr, nullptr};
    uint32_t n = (uint32_t)std::strlen(text);
    Str* msg = allocStr(n, &unused);
    widenAscii(text, n, strChars(msg));
    return throwValue(makeAccessError(StrV(msg)));
#else
    fatal(text);
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

// ---- The conversions of strings, chars, bytes and buffers (SPEC 8.10) --------------------------------------------------
inline void checkCharWidth(Value width) {
    if (FIRE_UNLIKELY(width.kind != K_Int || width.i < 1 || width.i > 4)) fatal("Invalid character width (allowed: 1-4 bytes per character).");
}
inline void writeCodeUnit(uint8_t* dest, uint32_t value, int width, bool little) {
    for (int b = 0; b < width; b++) dest[b] = (uint8_t)((value >> (little ? b * 8 : (width - 1 - b) * 8)) & 0xFF);
}
inline uint32_t readCodeUnit(const uint8_t* src, int width, bool little) {
    uint32_t v = 0;
    for (int b = 0; b < width; b++) v |= (uint32_t)src[b] << (little ? b * 8 : (width - 1 - b) * 8);
    return v & 0xFFFF;   // a char is one 16-bit code unit
}
/// `string.ToBytes()`: one byte per character (the lowest byte).
inline Value strToBytes(Value text, OwnList* list) {
    const Str* s = strOf(text);
    Buf* b = allocBuf(s->length, list);
    for (uint32_t i = 0; i < s->length; i++) b->bytes()[i] = (uint8_t)s->data[i];
    return BufV(b);
}
/// `string.ToUnicode(width)`
inline Value strToUnicode(Value text, Value width, OwnList* list) {
    checkCharWidth(width);
    const Str* s = strOf(text);
    Buf* b = allocBuf(s->length * (uint32_t)width.i, list);
    for (uint32_t i = 0; i < s->length; i++) writeCodeUnit(b->bytes() + i * (uint32_t)width.i, s->data[i], (int)width.i, b->little);
    return BufV(b);
}
/// `char.ToUnicode(width)`
inline Value charToUnicode(Value c, Value width, OwnList* list) {
    checkCharWidth(width);
    Buf* b = allocBuf((uint32_t)width.i, list);
    writeCodeUnit(b->bytes(), (uint32_t)c.i, (int)width.i, b->little);
    return BufV(b);
}
inline Value charToByte(Value c) { return Int((uint8_t)c.i); }
inline Value byteToChar(Value v) { return Char((uint8_t)v.i); }
/// `buffer.ToString()`: one character per byte.
inline Value bufToString(Value v, OwnList* list) {
    if (!ownedOf(v)) return destroyedError(v);
    Buf* b = bufOf(v);
    Str* r = allocStr(b->length, list);
    char16_t* d = strChars(r);
    for (uint32_t i = 0; i < b->length; i++) d[i] = b->bytes()[i];
    return StrV(r);
}
/// `buffer.ToUnicode(width)`
inline Value bufToUnicode(Value v, Value width, OwnList* list) {
    if (!ownedOf(v)) return destroyedError(v);
    checkCharWidth(width);
    Buf* b = bufOf(v);
    if (FIRE_UNLIKELY(b->length % (uint32_t)width.i != 0)) fatal("The buffer size is not a multiple of the character width.");
    uint32_t count = b->length / (uint32_t)width.i;
    Str* r = allocStr(count, list);
    char16_t* d = strChars(r);
    for (uint32_t i = 0; i < count; i++) d[i] = (char16_t)readCodeUnit(b->bytes() + i * (uint32_t)width.i, (int)width.i, b->little);
    return StrV(r);
}
/// `buffer.ToUnicodeChar(width)`: the first `width` bytes as one character.
inline Value bufToUnicodeChar(Value v, Value width) {
    if (!ownedOf(v)) return destroyedError(v);
    checkCharWidth(width);
    Buf* b = bufOf(v);
    if (FIRE_UNLIKELY(b->length < (uint32_t)width.i)) fatal("The buffer is too small for the character width.");
    return Char(readCodeUnit(b->bytes(), (int)width.i, b->little));
}
/// `buffer.ToLittleEndian()` / `ToBigEndian()`: a copy, mirrored as one block when the order differs.
inline Value bufToEndian(Value v, bool little, OwnList* list) {
    if (!ownedOf(v)) return destroyedError(v);
    Buf* b = bufOf(v);
    Buf* c = allocBuf(b->length, list);
    if ((b->little != 0) == little) std::memcpy(c->bytes(), b->bytes(), b->length);
    else for (uint32_t i = 0; i < b->length; i++) c->bytes()[i] = b->bytes()[b->length - 1 - i];
    c->little = little ? 1 : 0;
    return BufV(c);
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

/// `object is from owner` / `object is under owner` (SPEC 6): the owner of the object is that object - directly, or anywhere up the chain.
inline bool isFrom(Value operand, Value owner, bool transitive) {
    if (FIRE_UNLIKELY(operand.kind != K_Class || owner.kind != K_Class)) fatal("'is from'/'is under' expects objects.");
    Owned* target = asObj(owner);
    for (OwnList* l = asObj(operand)->owner; l; l = l->holder->owner) {
        if (l->holder == target) return true;
        if (!transitive || !l->holder) return false;
    }
    return false;
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

// ---------------------------------------------------------------------------------------------------------------------
// `flat x` and `copy x` (SPEC 2.4): no constructor runs, the copy belongs to `owner` like any new value.
// ---------------------------------------------------------------------------------------------------------------------
inline Value flatCopy(Value v, OwnList* owner) {
    switch (v.kind) {
        case K_Class: {
            Obj* src = asObj(v);
            Value c = newObject(src->cls, src->nfields, owner);
            Obj* dst = asObj(c);
            for (uint32_t i = 0; i < src->nfields; i++) { dst->fields()[i] = src->fields()[i]; retain(dst->fields()[i]); }
            return c;
        }
        case K_Array: {
            if (!ownedOf(v)) return destroyedError(v);
            Arr* src = arrOf(v);
            Arr* dst = allocArr(src->length, owner);
            for (uint32_t i = 0; i < src->length; i++) { dst->items()[i] = src->items()[i]; retain(dst->items()[i]); }
            return ArrV(dst);
        }
        case K_Buffer: {
            if (!ownedOf(v)) return destroyedError(v);
            Buf* src = bufOf(v);
            Buf* dst = allocBuf(src->length, owner);
            std::memcpy(dst->bytes(), src->bytes(), src->length);
            return BufV(dst);
        }
        default: return v;
    }
}

/// The identity table of a deep copy: original -> copy (open addressing on the address).
struct CopyMap {
    const void** from = nullptr;
    Value* to = nullptr;
    uint32_t cap = 0, count = 0;
    ~CopyMap() { std::free(from); std::free(to); }
    static uint32_t hashOf(const void* p) { return (uint32_t)(((uintptr_t)p >> 4) * 2654435761u); }
    void grow() {
        uint32_t newCap = cap ? cap * 2 : 32;
        const void** nf = static_cast<const void**>(std::calloc(newCap, sizeof(void*)));
        Value* nt = static_cast<Value*>(std::malloc(newCap * sizeof(Value)));
        if (!nf || !nt) allocFailed();
        for (uint32_t i = 0; i < cap; i++)
            if (from[i]) { uint32_t k = hashOf(from[i]) & (newCap - 1); while (nf[k]) k = (k + 1) & (newCap - 1); nf[k] = from[i]; nt[k] = to[i]; }
        std::free(from); std::free(to);
        from = nf; to = nt; cap = newCap;
    }
    Value* find(const void* key) {
        if (!cap) return nullptr;
        uint32_t k = hashOf(key) & (cap - 1);
        while (from[k]) { if (from[k] == key) return &to[k]; k = (k + 1) & (cap - 1); }
        return nullptr;
    }
    void put(const void* key, Value value) {
        if ((count + 1) * 2 > cap) grow();
        uint32_t k = hashOf(key) & (cap - 1);
        while (from[k]) k = (k + 1) & (cap - 1);
        from[k] = key; to[k] = value; count++;
    }
};

struct DeepCopier {
    OwnList* owner;
    CopyMap objects;      // object -> copy (the objects found, which are copied)
    CopyMap containers;   // array/buffer -> copy
    Obj** order = nullptr;
    uint32_t orderCount = 0, orderCap = 0;
    const void** seen = nullptr;   // arrays visited by the discovery
    uint32_t seenCount = 0, seenCap = 0;
    Value* pending = nullptr;
    uint32_t pendingCount = 0, pendingCap = 0;

    explicit DeepCopier(OwnList* o) : owner(o) {}
    ~DeepCopier() { std::free(order); std::free(seen); std::free(pending); }

    template <class T> static void push(T*& items, uint32_t& count, uint32_t& cap, T item) {
        if (count == cap) {
            cap = cap ? cap * 2 : 16;
            items = static_cast<T*>(std::realloc(items, cap * sizeof(T)));
            if (!items) allocFailed();
        }
        items[count++] = item;
    }

    bool seenArray(const void* a) {
        for (uint32_t i = 0; i < seenCount; i++) if (seen[i] == a) return true;
        return false;
    }
    /// Phase 1: everything reachable through fields and array elements (objects in discovery order).
    void discover(Value start) {
        push(pending, pendingCount, pendingCap, start);
        while (pendingCount) {
            Value v = pending[--pendingCount];
            if (v.kind == K_Class) {
                Obj* o = asObj(v);
                if (objects.find(o)) continue;
                objects.put(o, Undef());   // marks it as found; the copy is made in phase 2
                push(order, orderCount, orderCap, o);
                for (uint32_t i = 0; i < o->nfields; i++) push(pending, pendingCount, pendingCap, o->fields()[i]);
            } else if (v.kind == K_Array && ownedOf(v)) {
                Arr* a = arrOf(v);
                if (seenArray(a)) continue;
                push(seen, seenCount, seenCap, static_cast<const void*>(a));
                for (uint32_t i = 0; i < a->length; i++) push(pending, pendingCount, pendingCap, a->items()[i]);
            }
        }
    }
    /// Phase 2: the copy of an object belongs to the copy of its owner when that is copied too, else to `owner`.
    Value materialize(Obj* o) {
        Value* done = objects.find(o);
        if (done && done->kind == K_Class) return *done;
        OwnList* target = owner;
        if (o->owner && o->owner->holder && objects.find(o->owner->holder)) {
            Value holderCopy = materialize(static_cast<Obj*>(o->owner->holder));
            target = &asObj(holderCopy)->owned;
        }
        Value c = newObject(o->cls, o->nfields, target);
        *objects.find(o) = c;   // replaces the "found" marker
        return c;
    }
    /// What a value becomes in the copy.
    Value map(Value v) {
        switch (v.kind) {
            case K_Class: { Value* c = objects.find(asObj(v)); return c && c->kind == K_Class ? *c : v; }
            case K_Array: {
                if (!ownedOf(v)) return v;
                Arr* src = arrOf(v);
                if (Value* c = containers.find(src)) return *c;
                Arr* dst = allocArr(src->length, owner);
                Value r = ArrV(dst);
                containers.put(src, r);   // before filling: an array may contain itself
                for (uint32_t i = 0; i < src->length; i++) { dst->items()[i] = map(src->items()[i]); retain(dst->items()[i]); }
                return r;
            }
            case K_Buffer: {
                if (!ownedOf(v)) return v;
                Buf* src = bufOf(v);
                if (Value* c = containers.find(src)) return *c;
                Buf* dst = allocBuf(src->length, owner);
                std::memcpy(dst->bytes(), src->bytes(), src->length);
                Value r = BufV(dst);
                containers.put(src, r);
                return r;
            }
            default: return v;
        }
    }
    Value run(Value root) {
        discover(root);
        for (uint32_t i = 0; i < orderCount; i++) materialize(order[i]);
        for (uint32_t i = 0; i < orderCount; i++) {
            Obj* src = order[i];
            Obj* dst = asObj(*objects.find(src));
            for (uint32_t f = 0; f < src->nfields; f++) { dst->fields()[f] = map(src->fields()[f]); retain(dst->fields()[f]); }
        }
        return map(root);
    }
};

/// `copy x`: the copy of everything reachable from x, once each (cycles included); what belonged to a copied object belongs to its copy.
inline Value deepCopy(Value v, OwnList* owner) {
    if (v.kind == K_Class || ((v.kind == K_Array || v.kind == K_Buffer) && ownedOf(v))) { DeepCopier c(owner); return c.run(v); }
    if (v.kind == K_Array || v.kind == K_Buffer) return destroyedError(v);
    return v;
}

inline Value copyValue(Value v, bool deep, OwnList* owner) { return deep ? deepCopy(v, owner) : flatCopy(v, owner); }

/// `obj.field = copy x`: the copy belongs to the object (when it is being destroyed already, the copy goes with it at once).
inline Value copyOwned(Value target, Value v, bool deep) {
    Obj* t = asObj(target);
    if (t->flags & 1) {
        OwnList scratch = {nullptr, nullptr, 0, nullptr, nullptr};
        Value r = copyValue(v, deep, &scratch);
        destroyList(&scratch);
        return r;
    }
    return copyValue(v, deep, &t->owned);
}

/// A copy-prefixed argument (`f(copy x)`): the copy travels in the argument list of the call and dies with it unless the callee keeps it.
inline Value copyArg(Value v, bool deep, OwnList* from, OwnList* al) {
    al->parent = from;
    return copyValue(v, deep, al);
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
