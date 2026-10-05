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

/// Immutable string (UTF-8). Constants live in static storage.
struct StrObj {
    uint32_t length;
    const char* data;
};

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
inline Value Str(const StrObj* s) { Value r; r.kind = K_String; r.width = 0; r.reserved = 0; r.unit = 0; r.p = s; return r; }
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
            auto x = (const StrObj*)a.p, y = (const StrObj*)b.p;
            return x->length == y->length && std::memcmp(x->data, y->data, x->length) == 0;
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

inline void writeValue(Value v, std::FILE* f) {
    char buf[64];
    switch (v.kind) {
        case K_Bool: std::fputs(v.i ? "True" : "False", f); break;
        case K_Int: {
            auto r = std::to_chars(buf, buf + sizeof buf, v.i);
            std::fwrite(buf, 1, (size_t)(r.ptr - buf), f);
            if (v.unit) std::fputs(g_unitNames[v.unit], f);
            break;
        }
        case K_Float: {
            int n = formatReal(v.f, buf);
            std::fwrite(buf, 1, (size_t)n, f);
            if (v.unit) std::fputs(g_unitNames[v.unit], f);
            break;
        }
        case K_Char: {
            uint32_t c = (uint32_t)v.i;
            if (c < 0x80) std::fputc((int)c, f);
            else if (c < 0x800) { std::fputc((int)(0xC0 | (c >> 6)), f); std::fputc((int)(0x80 | (c & 0x3F)), f); }
            else { std::fputc((int)(0xE0 | (c >> 12)), f); std::fputc((int)(0x80 | ((c >> 6) & 0x3F)), f); std::fputc((int)(0x80 | (c & 0x3F)), f); }
            break;
        }
        case K_String: { auto s = (const StrObj*)v.p; std::fwrite(s->data, 1, s->length, f); break; }
        case K_Undefined: std::fputs("undefined", f); if (v.unit) { std::fputc(':', f); std::fputs(g_unitNames[v.unit], f); } break;
        case K_Class: std::fputs("<object>", f); break;
        case K_Lambda: std::fputs("<lambda>", f); break;
        case K_Pointer: std::fputs("<pointer>", f); break;
        case K_Array: std::fputs("<array>", f); break;
        case K_Buffer: std::fputs("<buffer>", f); break;
    }
}

/// `print(value)`: writes the value and a line break.
inline Value print(Value v) {
    writeValue(v, stdout);
    std::fputc('\n', stdout);
    return Undef();
}

}  // namespace fire
