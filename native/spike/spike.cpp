// AOT spike: hand-written C++ the way the generator would emit it, for three of the fire benchmarks.
//   generic: every variable is a tagged `Value` (what you get without any type knowledge)
//   typed:   variables are plain int64/double (what type inference would yield for `var i = 0`)
// Build: g++ -O2 -std=c++17 spike.cpp -o spike && ./spike
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cmath>

enum Kind : uint8_t { K_Bool, K_Int, K_Float, K_Char, K_String, K_Class, K_Lambda, K_Pointer, K_Array, K_Buffer, K_Undefined };

struct Value {
    uint8_t kind; uint8_t width; uint16_t reserved; uint32_t unit;   // unit: index into the program's unit table, 0 = unitless
    union { int64_t i; double f; void* p; };
};
static_assert(sizeof(Value) == 16, "Value must stay 16 bytes");

static inline Value mkInt(int64_t v, uint32_t unit = 0) { Value r; r.kind = K_Int; r.width = 0; r.reserved = 0; r.unit = unit; r.i = v; return r; }
static inline Value mkFloat(double v, uint32_t unit = 0) { Value r; r.kind = K_Float; r.width = 0; r.reserved = 0; r.unit = unit; r.f = v; return r; }
static inline Value mkBool(bool v) { Value r; r.kind = K_Bool; r.width = 0; r.reserved = 0; r.unit = 0; r.i = v; return r; }

[[noreturn]] __attribute__((noinline)) static void slowPath(const char* op) { std::fprintf(stderr, "slow path: %s\n", op); std::abort(); }

static inline double toD(const Value& v) { return v.kind == K_Float ? v.f : (double)v.i; }

#define ARITH(NAME, OP, SYM)                                                                       \
    static inline Value NAME(const Value& a, const Value& b) {                                     \
        if (a.unit == b.unit) {                                                                    \
            if (a.kind == K_Int && b.kind == K_Int) return mkInt(a.i OP b.i, a.unit);              \
            if ((a.kind == K_Float || a.kind == K_Int) && (b.kind == K_Float || b.kind == K_Int))  \
                return mkFloat(toD(a) OP toD(b), a.unit);                                          \
        }                                                                                          \
        slowPath(SYM);                                                                             \
    }
ARITH(add, +, "+")
ARITH(sub, -, "-")
static inline Value mul(const Value& a, const Value& b) {
    if (a.unit == 0 && b.unit == 0) {
        if (a.kind == K_Int && b.kind == K_Int) return mkInt(a.i * b.i);
        if ((a.kind == K_Float || a.kind == K_Int) && (b.kind == K_Float || b.kind == K_Int)) return mkFloat(toD(a) * toD(b));
    }
    slowPath("*");
}
static inline Value div_(const Value& a, const Value& b) {
    if (a.unit == 0 && b.unit == 0) {
        if (a.kind == K_Int && b.kind == K_Int) return mkInt(a.i / b.i);
        if ((a.kind == K_Float || a.kind == K_Int) && (b.kind == K_Float || b.kind == K_Int)) return mkFloat(toD(a) / toD(b));
    }
    slowPath("/");
}
static inline Value mod(const Value& a, const Value& b) {
    if (a.unit == b.unit && a.kind == K_Int && b.kind == K_Int) return mkInt(a.i % b.i, a.unit);
    slowPath("%");
}
static inline bool lt(const Value& a, const Value& b) {
    if (a.unit == b.unit) {
        if (a.kind == K_Int && b.kind == K_Int) return a.i < b.i;
        if ((a.kind == K_Float || a.kind == K_Int) && (b.kind == K_Float || b.kind == K_Int)) return toD(a) < toD(b);
    }
    slowPath("<");
}

// ---------------------------------------------------------------- generic
static int64_t loop_generic() {
    Value sum = mkInt(0);
    for (Value i = mkInt(0); lt(i, mkInt(1500000)); i = add(i, mkInt(1)))
        sum = add(sum, mod(i, mkInt(7)));
    return sum.i;
}
static double float_generic() {
    Value x = mkFloat(0.0);
    for (Value i = mkInt(0); lt(i, mkInt(600000)); i = add(i, mkInt(1)))
        x = sub(add(x, mul(i, mkFloat(0.5))), div_(x, mkFloat(3.0)));
    return x.f;
}
static Value fib_generic(Value n) {
    if (lt(n, mkInt(2))) return n;
    return add(fib_generic(sub(n, mkInt(1))), fib_generic(sub(n, mkInt(2))));
}

// ---------------------------------------------------------------- typed
static int64_t loop_typed() {
    int64_t sum = 0;
    for (int64_t i = 0; i < 1500000; i = i + 1) sum = sum + i % 7;
    return sum;
}
static double float_typed() {
    double x = 0.0;
    for (int64_t i = 0; i < 600000; i = i + 1) x = x + (double)i * 0.5 - x / 3.0;
    return x;
}
static int64_t fib_typed(int64_t n) {
    if (n < 2) return n;
    return fib_typed(n - 1) + fib_typed(n - 2);
}

template <class F> static double timeIt(const char* name, int reps, F f) {
    volatile double sink = 0;
    auto t0 = std::chrono::steady_clock::now();
    for (int r = 0; r < reps; r++) sink = sink + (double)f();
    auto t1 = std::chrono::steady_clock::now();
    double ms = std::chrono::duration<double, std::milli>(t1 - t0).count() / reps;
    std::printf("%-14s %9.3f ms   result %.1f\n", name, ms, (double)sink / reps);
    return ms;
}

int main() {
    std::printf("fire VM (Performance mode, this machine): loop 158.4 ms, float 99.2 ms, fib 28.6 ms\n\n");
    timeIt("loop  generic", 20, [] { return loop_generic(); });
    timeIt("loop  typed", 20, [] { return loop_typed(); });
    timeIt("float generic", 20, [] { return float_generic(); });
    timeIt("float typed", 20, [] { return float_typed(); });
    timeIt("fib   generic", 20, [] { return fib_generic(mkInt(23)).i; });
    timeIt("fib   typed", 20, [] { return fib_typed(23); });
}
