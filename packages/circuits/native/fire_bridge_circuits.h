/* fire native bridge "circuits" (C): the kernel behind `#import "circuits"` (docs/circuits/README.md). The fire side (Circuits.Circuit and its parts) compiles a circuit
 * into a plan the first time it steps after a change: the nodes and branches, the matrix of the parts that do not change from step to step, and tables of the parts
 * that this kernel steps itself (capacitors, inductors, sources). This file runs that plan:
 *
 *  * Compile: decides the order of the pivots once (partial pivoting on the numbers), then turns the elimination into a fixed list of operations on the
 *    entries that are not zero (with the fill-in). Solving a step walks only those entries: a chain of resistors and capacitors costs O(n) instead of O(n^2).
 *    Refactor runs the same list again on new numbers (non-linear parts, later stages).
 *  * Begin/Finish: one time step - the right side from the parts of the kernel (and what the fire side adds with RhsAdd for parts written in fire), the
 *    solution, the new state of the capacitors and inductors, the probes of the scopes. Run does many steps in one call.
 *  * Recorders keep what scopes record; they live apart from a plan, so a recording goes on when the circuit is compiled again.
 *
 * This file is the one implementation, in C99 ("language": "c"): the native build puts it into the generated program, and the virtual machine runs it in a shared
 * library built from it (native/abi/fire_pkg_abi.h). It does every computation in the same order as the C++ kernel it replaces, so the numbers are the same. */
#ifndef FIRE_BRIDGE_CIRCUITS_H
#define FIRE_BRIDGE_CIRCUITS_H

#include <ctype.h>
#include <math.h>
#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* The simulation in a thread of its own (the runner): on the hosted platforms (the library of the virtual machine, native programs for Linux, Windows, macOS).
 * Elsewhere (the browser, microcontrollers) the program steps the circuit itself, as before. */
#if !defined(CIR_NO_THREADS) && !defined(__EMSCRIPTEN__) && (defined(FIRE_LIBRARY) || defined(FIRE_HAL_POSIX) || defined(FIRE_HAL_WINDOWS))
#define CIR_THREADS 1
#ifdef _WIN32
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#else
#include <pthread.h>
#include <sched.h>
#include <time.h>
#endif
#endif

/* A stream can play to a sound device and record from one in the kernel itself (StreamPlay / StreamRecord): on the hosted platforms, with the devices of the
 * platform layer in C (fire_audio_*, as the package "audio"). Elsewhere only the simulated device "sim" is there. */
#if !defined(CIR_NO_AUDIO) && !defined(__EMSCRIPTEN__) && defined(FIRE_PLATFORM_AUDIO_C_HEADER) && (defined(FIRE_LIBRARY) || defined(FIRE_HAL_POSIX) || defined(FIRE_HAL_WINDOWS))
#define CIR_AUDIO 1
#include FIRE_PLATFORM_AUDIO_C_HEADER
#endif

/* the message of the last error is per thread, where the compiler has thread-local storage (TinyCC has none) */
#if defined(__TINYC__)
#define CIR_TLS
#elif defined(__GNUC__) || defined(__clang__)
#define CIR_TLS __thread
#elif defined(_MSC_VER)
#define CIR_TLS __declspec(thread)
#elif defined(__STDC_VERSION__) && __STDC_VERSION__ >= 201112L && !defined(__STDC_NO_THREADS__)
#define CIR_TLS _Thread_local
#else
#define CIR_TLS
#endif

/* ---- growing arrays: { items, count, capacity }. The kernel cannot go on without its memory: when there is none, the program ends with a message (as the C++
 * kernel did, whose vectors threw). ---- */
#define CIR_VEC(T) struct { T* d; size_t n, cap; }

static void* cir_growTo(void* data, size_t* cap, size_t need, size_t elem) {
    size_t c = *cap ? *cap : 4;
    void* grown;
    while (c < need) c *= 2;
    grown = realloc(data, c * elem);
    if (grown == NULL) { fputs("circuits: out of memory\n", stderr); abort(); }
    *cap = c;
    return grown;
}
#define CIR_RESERVE(v, need) do { if ((size_t)(need) > (v).cap) (v).d = cir_growTo((v).d, &(v).cap, (size_t)(need), sizeof *(v).d); } while (0)
#define CIR_PUSH(v, ...) do { CIR_RESERVE(v, (v).n + 1); (v).d[(v).n++] = (__VA_ARGS__); } while (0)
#define CIR_COPY(dst, src) do { CIR_RESERVE(dst, (src).n); if ((src).n) memcpy((dst).d, (src).d, (src).n * sizeof *(dst).d); (dst).n = (src).n; } while (0)
#define CIR_FREE(v) do { free((v).d); (v).d = NULL; (v).n = (v).cap = 0; } while (0)

typedef CIR_VEC(double) CirD;
typedef CIR_VEC(int) CirI;
typedef CIR_VEC(char) CirC;
typedef CIR_VEC(float) CirF;
typedef CIR_VEC(uint8_t) CirB;

/* n copies of x (std::vector::assign) */
static void cir_dAssign(CirD* v, size_t n, double x) { size_t i; CIR_RESERVE(*v, n); for (i = 0; i < n; i++) v->d[i] = x; v->n = n; }
static void cir_iAssign(CirI* v, size_t n, int x) { size_t i; CIR_RESERVE(*v, n); for (i = 0; i < n; i++) v->d[i] = x; v->n = n; }
static void cir_cAssign(CirC* v, size_t n, char x) { CIR_RESERVE(*v, n); if (n) memset(v->d, x, n); v->n = n; }
/* drops the first k items */
static void cir_dDropFront(CirD* v, size_t k) { if (k) { memmove(v->d, v->d + k, (v->n - k) * sizeof(double)); v->n -= k; } }

/* std::min / std::max (the first one when they are equal, as there) */
static double cir_min(double a, double b) { return b < a ? b : a; }
static double cir_max(double a, double b) { return a < b ? b : a; }
static size_t cir_minZ(size_t a, size_t b) { return b < a ? b : a; }
static int64_t cir_minL(int64_t a, int64_t b) { return b < a ? b : a; }
static int64_t cir_maxL(int64_t a, int64_t b) { return a < b ? b : a; }

/* ---- the lock of the kernel: every native takes it, and so does the thread of a running plan while it does a few steps - the program reads and changes plans and
 * recordings only between those. ---- */
#ifdef CIR_THREADS
#ifdef _WIN32
static SRWLOCK g_cirLock = SRWLOCK_INIT;
static void cir_lock(void) { AcquireSRWLockExclusive(&g_cirLock); }
static void cir_unlock(void) { ReleaseSRWLockExclusive(&g_cirLock); }
#else
static pthread_mutex_t g_cirLock = PTHREAD_MUTEX_INITIALIZER;
static void cir_lock(void) { pthread_mutex_lock(&g_cirLock); }
static void cir_unlock(void) { pthread_mutex_unlock(&g_cirLock); }
#endif
#else
static void cir_lock(void) {}
static void cir_unlock(void) {}
#endif
/* a native: the body under the lock */
#define CIR_LOCKED(call) do { Value r_; cir_lock(); r_ = (call); cir_unlock(); return r_; } while (0)

/* ---- the last error (per thread) ---- */
static CIR_TLS char* g_cirError;

/* Takes `message` (malloc) as the last error. */
static void cir_setErrorOwned(char* message) {
    free(g_cirError);
    g_cirError = message;
}
/* The last error, printf-style. */
static void cir_error(const char* format, ...) {
    va_list args;
    int n;
    char* s;
    va_start(args, format);
    n = vsnprintf(NULL, 0, format, args);
    va_end(args);
    if (n < 0) n = 0;
    s = (char*)malloc((size_t)n + 1);
    if (s == NULL) return;
    va_start(args, format);
    vsnprintf(s, (size_t)n + 1, format, args);
    va_end(args);
    cir_setErrorOwned(s);
}
/* A new text, printf-style (malloc; NULL when out of memory). */
static char* cir_format(const char* format, ...) {
    va_list args;
    int n;
    char* s;
    va_start(args, format);
    n = vsnprintf(NULL, 0, format, args);
    va_end(args);
    if (n < 0) n = 0;
    s = (char*)malloc((size_t)n + 1);
    if (s == NULL) return NULL;
    va_start(args, format);
    vsnprintf(s, (size_t)n + 1, format, args);
    va_end(args);
    return s;
}

/* A text of fire (UTF-16) as UTF-8 (the formulas and parameters are ASCII; other characters only show in messages). malloc; `length` gets its length. */
static char* cir_utf8(Value v, size_t* length) {
    const Str* s = strOf(v);
    uint32_t i;
    size_t n = 0;
    char* out = (char*)malloc((size_t)s->length * 3 + 1);
    if (out == NULL) { fputs("circuits: out of memory\n", stderr); abort(); }
    for (i = 0; i < s->length; i++) {
        uint32_t c = s->data[i];
        if (c < 0x80) out[n++] = (char)c;
        else if (c < 0x800) { out[n++] = (char)(0xC0 | (c >> 6)); out[n++] = (char)(0x80 | (c & 0x3F)); }
        else { out[n++] = (char)(0xE0 | (c >> 12)); out[n++] = (char)(0x80 | ((c >> 6) & 0x3F)); out[n++] = (char)(0x80 | (c & 0x3F)); }
    }
    out[n] = 0;
    if (length) *length = n;
    return out;
}

/* A fire string from bytes (each byte one character, as the C++ kernel made its messages). */
static Value cir_bytesStr(const char* text, OwnList* list) {
    size_t n = text ? strlen(text) : 0, i;
    Str* s = allocStr((uint32_t)n, list);
    for (i = 0; i < n; i++) strChars(s)[i] = (fire_char16)(uint8_t)text[i];
    return StrV(s);
}

static double cir_D(Value v) { return v.kind == K_Float ? (double)v.f : (double)v.i; }

/* ---- recorders: what a scope records - the times and one row of samples per channel; stops when `capacity` samples are there. A rolling recorder (made with
 * a negative capacity) drops the older half instead and goes on. ---- */
typedef CIR_VEC(CirD) CirDD;
typedef struct CirRecorder {
    int64_t capacity;
    int rolling;
    CirD times;
    CirDD channels;
} CirRecorder;

/* Is there room for one more sample (after dropping the older half of a full rolling recorder)? */
static int cir_recRoom(CirRecorder* r) {
    size_t drop, i;
    if ((int64_t)r->times.n < r->capacity) return 1;
    if (!r->rolling) return 0;
    drop = (size_t)(r->capacity / 2);
    cir_dDropFront(&r->times, cir_minZ(drop, r->times.n));
    for (i = 0; i < r->channels.n; i++) cir_dDropFront(&r->channels.d[i], cir_minZ(drop, r->channels.d[i].n));
    return 1;
}

static void cir_recFree(CirRecorder* r) {
    size_t i;
    if (r == NULL) return;
    CIR_FREE(r->times);
    for (i = 0; i < r->channels.n; i++) CIR_FREE(r->channels.d[i]);
    CIR_FREE(r->channels);
    free(r);
}

enum { CIR_PR_NODES = 0, CIR_PR_CAPACITOR = 1, CIR_PR_INDUCTOR = 2, CIR_PR_BRANCH = 3, CIR_PR_CONDUCTANCE = 4, CIR_PR_CURRENT_SOURCE = 5, CIR_PR_NONLINEAR = 6,
       CIR_PR_COUPLED = 7 };

typedef struct CirProbe { int rec, channel, kind, a, b; double g; } CirProbe;

/* WSweep + flags (1: logarithmic, 2: up and down again): WSweep .. WSweep + 3 */
enum { CIR_W_DC = 0, CIR_W_SINE = 1, CIR_W_SQUARE = 2, CIR_W_TRIANGLE = 3, CIR_W_SAWTOOTH = 4, CIR_W_NOISE = 5, CIR_W_SWEEP = 6 };

#define CIR_PI 3.14159265358979323846

typedef struct CirSource {
    int a, b, branch;       /* branch >= 0: a voltage source (row of its branch), else a current source from a through it to b */
    int wave;
    double p0, p1, p2, p3;  /* DC: level; sine: amplitude, frequency, offset, phase; square: high, frequency, low, duty */
    int fed;                /* driven by Process before: the next piece of sound goes on from its last sample */
    double lastSample;
    /* the waves with a state (noise, sweep): the value of the step, and the state of the sweep (frequency, phase, direction) */
    double held, freq, phase, dir;
    uint64_t seed;
    int started;
} CirSource;

static CirSource cir_source(int a, int b, int branch, int wave, double p0, double p1, double p2, double p3) {
    CirSource s;
    memset(&s, 0, sizeof s);
    s.a = a; s.b = b; s.branch = branch; s.wave = wave;
    s.p0 = p0; s.p1 = p1; s.p2 = p2; s.p3 = p3;
    s.dir = 1;
    s.seed = 0x9E3779B97F4A7C15ull;
    return s;
}

static double cir_sourceValue(const CirSource* s, double t) {
    const double pi = CIR_PI;
    switch (s->wave) {
        case CIR_W_SINE: return s->p2 + s->p0 * sin(2.0 * pi * s->p1 * t + s->p3);
        case CIR_W_SQUARE: {
            double cycles = t * s->p1;
            return cycles - floor(cycles) < s->p3 ? s->p0 : s->p2;
        }
        case CIR_W_TRIANGLE: case CIR_W_SAWTOOTH: {
            /* as Falstad: amplitude p0, frequency p1, offset p2, phase p3 */
            double w = fmod(2.0 * pi * s->p1 * t + s->p3, 2.0 * pi);
            if (w < 0) w += 2.0 * pi;
            if (s->wave == CIR_W_SAWTOOTH) return s->p2 + w * (s->p0 / pi) - s->p0;
            return s->p2 + (w < pi ? w * (2 / pi) - 1 : 1 - (w - pi) * (2 / pi)) * s->p0;
        }
        default: return s->wave >= CIR_W_NOISE ? s->held : s->p0;
    }
}

/* Before a step: the waves with a state move on (Falstad's SweepElm: amplitude p0 from p1 to p2 Hz in p3 seconds). */
static void cir_sourceAdvance(CirSource* s, double dt) {
    if (s->wave == CIR_W_NOISE) {
        double r;
        s->seed ^= s->seed << 13; s->seed ^= s->seed >> 7; s->seed ^= s->seed << 17;
        r = (double)(s->seed >> 11) / 9007199254740992.0;
        s->held = (r * 2 - 1) * s->p0 + s->p2;
    } else if (s->wave >= CIR_W_SWEEP) {
        int flags = s->wave - CIR_W_SWEEP;
        if (!s->started || s->freq < s->p1 || s->freq > s->p2) { s->freq = s->p1; s->phase = 0; s->dir = 1; s->started = 1; }
        s->held = sin(s->phase) * s->p0;
        s->phase += s->freq * 2 * 3.14159265358979323846 * dt;
        if (flags & 1) s->freq *= pow(s->p2 / s->p1, s->dir * dt / s->p3); else s->freq += s->dir * dt * (s->p2 - s->p1) / s->p3;
        if (s->freq >= s->p2 && s->dir == 1) {
            if (flags & 2) s->dir = -1; else s->freq = s->p1;
        }
        if (s->freq <= s->p1 && s->dir == -1) s->dir = 1;
    }
}

/* a capacitor (2C/dt) or an inductor (dt/2L), trapezoidal rule; `last` and `flips` watch it for ringing (see cir_damp) */
typedef struct CirReactive { int a, b; double geq, vPrev, iPrev, ieq; double last; int flips; } CirReactive;

/* The trapezoidal rule does not damp what is much faster than the time step: a capacitor shorted by a switch (or an inductor left open) keeps a current (a
 * voltage) that changes its sign every step and never dies away. A value that alternates two steps in a row cannot be a signal the step resolves, so it is
 * replaced by the mean of the last two values - the ringing is gone, and a slower signal is not touched. */
static void cir_damp(double* value, double* last, int* flips) {
    int flipped = (*value > 0 && *last < 0) || (*value < 0 && *last > 0);
    *flips = flipped ? *flips + 1 : 0;
    if (*flips >= 2) { *value = (*value + *last) / 2; *flips = 0; }
    *last = *value;
}

/* Two coupled inductors (a transformer): winding 1 from a1 to b1, winding 2 from a2 to b2. With L = [[L1, M], [M, L2]] the trapezoidal rule gives
 * i(n+1) = G v(n+1) + h, G = dt/2 L^-1, h = i(n) + G v(n) - a 2x2 conductance (with the cross terms as voltage-controlled currents) and two currents. */
typedef struct CirCoupled { int a1, b1, a2, b2; double g11, g12, g22, v1Prev, v2Prev, i1Prev, i2Prev, h1, h2; } CirCoupled;

/* A lossless transmission line (Bergeron's method): each end (a: the conductor, b: the return) is its impedance z with a current source that brings the wave
 * the other end sent `delay` ago. h holds per step and end V/z + I (I: the current into the line at its conductor). */
typedef struct CirLine {
    int a1, b1, a2, b2;
    double z;
    int steps;
    double* h1;
    double* h2;
    int ptr;
    double j1, j2, i1, i2;
} CirLine;

enum { CIR_NL_DIODE = 0, CIR_NL_NPN = 1, CIR_NL_PNP = 2, CIR_NL_NMOS = 3, CIR_NL_PMOS = 4, CIR_NL_OPAMP = 5, CIR_NL_FORMULA = 6,
       /* resistances that follow a state between the steps (Falstad's startIteration), and a switch driven by a voltage */
       CIR_NL_LAMP = 7, CIR_NL_FUSE = 8, CIR_NL_MEMRISTOR = 9, CIR_NL_SPARK_GAP = 10, CIR_NL_ANALOG_SWITCH = 11,
       /* the switch of a thyristor (anode to its inner node; gate node2, cathode aux) and of a triac (inner node to MT1; gate node2) */
       CIR_NL_SCR = 12, CIR_NL_TRIAC = 13,
       /* vacuum tubes (Circuits.VacuumDiode, Triode, Pentode): a diode after Child and Langmuir (plate, cathode), triode and pentode after Norman Koren's
        * models (plate, grid, cathode; plate, screen, grid, cathode) */
       CIR_NL_VACUUM_DIODE = 14, CIR_NL_TRIODE = 15, CIR_NL_PENTODE = 16 };
static int cir_isVarRes(int kind) { return (kind >= CIR_NL_LAMP && kind <= CIR_NL_SPARK_GAP) || kind == CIR_NL_SCR || kind == CIR_NL_TRIAC; }

#define CIR_VT 0.025852     /* the thermal voltage at 27 degrees Celsius */
#define CIR_GMIN 1e-12      /* a conductance across every junction (keeps the matrix regular when a junction is off) */

static double cir_expl(double x, double* derivative);

/* ---- formulas -----------------------------------------------------------------------------------------------------------------------------------------------
 * A part given by formulas for its currents (Circuits.Formula): `Is*(exp(V/(n*Vt))-1)`. The text is compiled once into a small stack program; evaluating it on
 * dual numbers gives the value and its derivatives by the controlling voltages at the same time - what Newton's method needs. */

/* A number with its derivatives by the two controlling voltages. */
typedef struct CirDual { double v, d1, d2; } CirDual;
static CirDual cir_dual(double v, double d1, double d2) { CirDual r; r.v = v; r.d1 = d1; r.d2 = d2; return r; }

enum { CIR_F_CONST, CIR_F_VAR, CIR_F_ADD, CIR_F_SUB, CIR_F_MUL, CIR_F_DIV, CIR_F_POW, CIR_F_NEG, CIR_F_CALL };
enum { CIR_FN_EXP, CIR_FN_LOG, CIR_FN_SQRT, CIR_FN_ABS, CIR_FN_SIN, CIR_FN_COS, CIR_FN_TANH, CIR_FN_MIN, CIR_FN_MAX, CIR_FN_IF, CIR_FN_POW, CIR_FN_ATAN,
       CIR_FN_LIMIT };
/* CONST: value; VAR: arg 0 V1, 1 V2, 2 t; CALL: arg = function, value = argument count */
typedef struct CirFInstr { int op; double value; int arg; } CirFInstr;

typedef struct CirFormula {
    CIR_VEC(CirFInstr) code;
    int exponential;     /* uses exp or pow: Newton's method moves its voltages carefully */
} CirFormula;

static void cir_formulaFree(CirFormula* f) {
    if (f == NULL) return;
    CIR_FREE(f->code);
    free(f);
}

typedef struct CirParam { char* name; double value; } CirParam;
typedef CIR_VEC(CirParam) CirParams;

static void cir_paramsFree(CirParams* p) {
    size_t i;
    for (i = 0; i < p->n; i++) free(p->d[i].name);
    CIR_FREE(*p);
}

typedef struct CirFComp {
    const char* text;
    size_t len;
    const CirParams* params;
    int threeTerminals;
    size_t pos;
    CirFormula out;
    char* error;        /* NULL: none */
} CirFComp;

static void cir_fcInit(CirFComp* c, const char* text, size_t len, const CirParams* params, int three) {
    memset(c, 0, sizeof *c);
    c->text = text; c->len = len; c->params = params; c->threeTerminals = three;
}
static void cir_fcFree(CirFComp* c) { CIR_FREE(c->out.code); free(c->error); c->error = NULL; }

static void cir_fcEmit(CirFComp* c, int op, double value, int arg) { CirFInstr in; in.op = op; in.value = value; in.arg = arg; CIR_PUSH(c->out.code, in); }
static void cir_fcSkip(CirFComp* c) { while (c->pos < c->len && (c->text[c->pos] == ' ' || c->text[c->pos] == '\t')) c->pos++; }
static int cir_fcEat(CirFComp* c, char ch) { cir_fcSkip(c); if (c->pos < c->len && c->text[c->pos] == ch) { c->pos++; return 1; } return 0; }
/* the first mistake, with where it is; takes `message` (malloc) */
static int cir_fcFailOwned(CirFComp* c, char* message) {
    if (c->error == NULL) c->error = cir_format("%s (at %lu of '%s')", message ? message : "", (unsigned long)(c->pos + 1), c->text);
    free(message);
    return 0;
}
static int cir_fcFail(CirFComp* c, const char* message) { return cir_fcFailOwned(c, cir_format("%s", message)); }

static int cir_fcExpression(CirFComp* c);
static int cir_fcUnary(CirFComp* c);

static int cir_fcPrimary(CirFComp* c) {
    char ch;
    cir_fcSkip(c);
    if (c->pos >= c->len) return cir_fcFail(c, "An expression is missing");
    ch = c->text[c->pos];
    if (ch == '(') { c->pos++; if (!cir_fcExpression(c)) return 0; if (!cir_fcEat(c, ')')) return cir_fcFail(c, "')' expected"); return 1; }
    if ((ch >= '0' && ch <= '9') || ch == '.') {
        const char* start = c->text + c->pos;
        char* end = NULL;
        double v = strtod(start, &end);
        if (end == start) return cir_fcFail(c, "A number expected");
        c->pos += (size_t)(end - start);
        cir_fcEmit(c, CIR_F_CONST, v, 0);
        return 1;
    }
    if (isalpha((unsigned char)ch) || ch == '_') {
        static const char* names[] = {"exp", "log", "sqrt", "abs", "sin", "cos", "tanh", "min", "max", "if", "pow", "atan", "limit"};
        static const int arity[] = {1, 1, 1, 1, 1, 1, 1, 2, 2, 3, 2, 1, 3};
        size_t start = c->pos, nameLen, k;
        char* name;
        int result;
        while (c->pos < c->len && (isalnum((unsigned char)c->text[c->pos]) || c->text[c->pos] == '_')) c->pos++;
        nameLen = c->pos - start;
        name = (char*)malloc(nameLen + 1);
        if (name == NULL) { fputs("circuits: out of memory\n", stderr); abort(); }
        memcpy(name, c->text + start, nameLen);
        name[nameLen] = 0;
        cir_fcSkip(c);
        if (c->pos < c->len && c->text[c->pos] == '(') {
            int fn = -1, i, count = 0;
            c->pos++;
            for (i = 0; i < 13; i++) if (strcmp(name, names[i]) == 0) fn = i;
            if (fn < 0) { result = cir_fcFailOwned(c, cir_format("Unknown function '%s'", name)); free(name); return result; }
            if (!cir_fcEat(c, ')')) {
                do { if (!cir_fcExpression(c)) { free(name); return 0; } count++; } while (cir_fcEat(c, ','));
                if (!cir_fcEat(c, ')')) { result = cir_fcFailOwned(c, cir_format("')' expected after the arguments of %s", name)); free(name); return result; }
            }
            if (count != arity[fn]) { result = cir_fcFailOwned(c, cir_format("%s takes %d argument(s)", name, arity[fn])); free(name); return result; }
            if (fn == CIR_FN_EXP || fn == CIR_FN_POW) c->out.exponential = 1;
            cir_fcEmit(c, CIR_F_CALL, (double)count, fn);
            free(name);
            return 1;
        }
        for (k = 0; k < c->params->n; k++)
            if (strcmp(c->params->d[k].name, name) == 0) { cir_fcEmit(c, CIR_F_CONST, c->params->d[k].value, 0); free(name); return 1; }
        result = 1;
        if (!c->threeTerminals && strcmp(name, "V") == 0) cir_fcEmit(c, CIR_F_VAR, 0, 0);
        else if (c->threeTerminals && strcmp(name, "V1") == 0) cir_fcEmit(c, CIR_F_VAR, 0, 0);
        else if (c->threeTerminals && strcmp(name, "V2") == 0) cir_fcEmit(c, CIR_F_VAR, 0, 1);
        else if (strcmp(name, "t") == 0) cir_fcEmit(c, CIR_F_VAR, 0, 2);
        else if (strcmp(name, "Vt") == 0) cir_fcEmit(c, CIR_F_CONST, CIR_VT, 0);
        else if (strcmp(name, "pi") == 0) cir_fcEmit(c, CIR_F_CONST, 3.14159265358979323846, 0);
        else if (strcmp(name, "e") == 0) cir_fcEmit(c, CIR_F_CONST, 2.718281828459045, 0);
        else result = cir_fcFailOwned(c, cir_format("Unknown name '%s'%s", name, c->threeTerminals ? " (the voltages are V1 and V2)" : " (the voltage is V)"));
        free(name);
        return result;
    }
    return cir_fcFailOwned(c, cir_format("Unexpected '%c'", ch));
}

static int cir_fcPower(CirFComp* c) {
    if (!cir_fcPrimary(c)) return 0;
    if (cir_fcEat(c, '^')) {   /* right-associative, binds tighter than a sign on its left: -2^2 is -4 */
        if (!cir_fcUnary(c)) return 0;
        cir_fcEmit(c, CIR_F_POW, 0, 0);
        c->out.exponential = 1;
    }
    return 1;
}

static int cir_fcUnary(CirFComp* c) {
    if (cir_fcEat(c, '-')) { if (!cir_fcUnary(c)) return 0; cir_fcEmit(c, CIR_F_NEG, 0, 0); return 1; }
    if (cir_fcEat(c, '+')) return cir_fcUnary(c);
    return cir_fcPower(c);
}

static int cir_fcTerm(CirFComp* c) {
    if (!cir_fcUnary(c)) return 0;
    for (;;) {
        if (cir_fcEat(c, '*')) { if (!cir_fcUnary(c)) return 0; cir_fcEmit(c, CIR_F_MUL, 0, 0); }
        else if (cir_fcEat(c, '/')) { if (!cir_fcUnary(c)) return 0; cir_fcEmit(c, CIR_F_DIV, 0, 0); }
        else return 1;
    }
}

static int cir_fcExpression(CirFComp* c) {
    if (!cir_fcTerm(c)) return 0;
    for (;;) {
        if (cir_fcEat(c, '+')) { if (!cir_fcTerm(c)) return 0; cir_fcEmit(c, CIR_F_ADD, 0, 0); }
        else if (cir_fcEat(c, '-')) { if (!cir_fcTerm(c)) return 0; cir_fcEmit(c, CIR_F_SUB, 0, 0); }
        else return 1;
    }
}

static int cir_isSep(char c) { return c == ' ' || c == '\t' || c == ',' || c == ';' || c == '\n'; }

/* "Is=1e-12 n=1.5" (also with commas or semicolons, and with the prefixes f p n u m k M G after a number) as names and values; 0 and a message (malloc) on a
 * mistake. */
static int cir_parseParameters(const char* text, size_t len, CirParams* out, char** error) {
    size_t i = 0;
    while (i < len) {
        size_t start, nameLen, k;
        char* name;
        const char* begin;
        char* end = NULL;
        double v;
        int replaced = 0;
        while (i < len && cir_isSep(text[i])) i++;
        if (i >= len) break;
        start = i;
        while (i < len && (isalnum((unsigned char)text[i]) || text[i] == '_')) i++;
        nameLen = i - start;
        while (i < len && text[i] == ' ') i++;
        if (nameLen == 0 || i >= len || text[i] != '=') { *error = cir_format("Parameters are written name=value: '%s'", text); return 0; }
        name = (char*)malloc(nameLen + 1);
        if (name == NULL) { fputs("circuits: out of memory\n", stderr); abort(); }
        memcpy(name, text + start, nameLen);
        name[nameLen] = 0;
        i++;
        while (i < len && text[i] == ' ') i++;
        begin = text + i;
        v = strtod(begin, &end);
        if (end == begin) { *error = cir_format("The value of '%s' is not a number", name); free(name); return 0; }
        i += (size_t)(end - begin);
        if (i < len && !cir_isSep(text[i])) {
            static const char prefixes[] = "fpnumkMG";
            static const double factors[] = {1e-15, 1e-12, 1e-9, 1e-6, 1e-3, 1e3, 1e6, 1e9};
            const char* f = strchr(prefixes, text[i]);
            if (!f || !*f) { *error = cir_format("Unknown suffix after the value of '%s'", name); free(name); return 0; }
            v *= factors[f - prefixes];
            i++;
            while (i < len && !cir_isSep(text[i])) i++;   /* a unit after the prefix (5mA) is ignored */
        }
        for (k = 0; k < out->n; k++) if (strcmp(out->d[k].name, name) == 0) { out->d[k].value = v; replaced = 1; }
        if (!replaced) { CirParam p; p.name = name; p.value = v; CIR_PUSH(*out, p); }
        else free(name);
    }
    return 1;
}

static CirDual cir_fcall(int fn, const CirDual* a) {
    switch (fn) {
        case CIR_FN_EXP: { double d, e = cir_expl(a[0].v, &d); return cir_dual(e, d * a[0].d1, d * a[0].d2); }
        case CIR_FN_LOG: { double v = a[0].v > 1e-300 ? a[0].v : 1e-300; return cir_dual(log(v), a[0].d1 / v, a[0].d2 / v); }
        case CIR_FN_SQRT: { double r = sqrt(a[0].v > 0 ? a[0].v : 0); double k = r > 0 ? 0.5 / r : 0; return cir_dual(r, k * a[0].d1, k * a[0].d2); }
        case CIR_FN_ABS: { double s = a[0].v < 0 ? -1 : 1; return cir_dual(fabs(a[0].v), s * a[0].d1, s * a[0].d2); }
        case CIR_FN_SIN: { double c = cos(a[0].v); return cir_dual(sin(a[0].v), c * a[0].d1, c * a[0].d2); }
        case CIR_FN_COS: { double s = -sin(a[0].v); return cir_dual(cos(a[0].v), s * a[0].d1, s * a[0].d2); }
        case CIR_FN_TANH: { double t = tanh(a[0].v), k = 1 - t * t; return cir_dual(t, k * a[0].d1, k * a[0].d2); }
        case CIR_FN_ATAN: { double k = 1 / (1 + a[0].v * a[0].v); return cir_dual(atan(a[0].v), k * a[0].d1, k * a[0].d2); }
        case CIR_FN_MIN: return a[0].v <= a[1].v ? a[0] : a[1];
        case CIR_FN_MAX: return a[0].v >= a[1].v ? a[0] : a[1];
        case CIR_FN_IF: return a[0].v > 0 ? a[1] : a[2];
        case CIR_FN_LIMIT: { if (a[0].v < a[1].v) return cir_dual(a[1].v, 0, 0); if (a[0].v > a[2].v) return cir_dual(a[2].v, 0, 0); return a[0]; }
        case CIR_FN_POW: default: {
            const CirDual* x = &a[0];
            const CirDual* y = &a[1];
            double v = pow(x->v, y->v);
            double dx = x->v != 0 ? y->v * pow(x->v, y->v - 1) : 0;
            double dy = x->v > 0 ? v * log(x->v) : 0;
            return cir_dual(v, dx * x->d1 + dy * y->d1, dx * x->d2 + dy * y->d2);
        }
    }
}

/* the places of the stack of evaluate (a program must not need more: cir_stackDepth) */
#define CIR_FSTACK 64

static CirDual cir_evaluate(const CirFormula* prog, double v1, double v2, double t) {
    CirDual stack[CIR_FSTACK];
    int sp = 0;
    size_t k;
    for (k = 0; k < prog->code.n; k++) {
        const CirFInstr* in = &prog->code.d[k];
        switch (in->op) {
            case CIR_F_CONST: stack[sp++] = cir_dual(in->value, 0, 0); break;
            case CIR_F_VAR:
                if (in->arg == 0) stack[sp++] = cir_dual(v1, 1, 0);
                else if (in->arg == 1) stack[sp++] = cir_dual(v2, 0, 1);
                else stack[sp++] = cir_dual(t, 0, 0);
                break;
            case CIR_F_NEG: stack[sp - 1] = cir_dual(-stack[sp - 1].v, -stack[sp - 1].d1, -stack[sp - 1].d2); break;
            case CIR_F_ADD: case CIR_F_SUB: case CIR_F_MUL: case CIR_F_DIV: case CIR_F_POW: {
                CirDual b = stack[--sp], a = stack[sp - 1], r;
                if (in->op == CIR_F_ADD) r = cir_dual(a.v + b.v, a.d1 + b.d1, a.d2 + b.d2);
                else if (in->op == CIR_F_SUB) r = cir_dual(a.v - b.v, a.d1 - b.d1, a.d2 - b.d2);
                else if (in->op == CIR_F_MUL) r = cir_dual(a.v * b.v, a.d1 * b.v + a.v * b.d1, a.d2 * b.v + a.v * b.d2);
                else if (in->op == CIR_F_DIV) { double q = b.v != 0 ? b.v : 1e-300; r = cir_dual(a.v / q, (a.d1 * q - a.v * b.d1) / (q * q), (a.d2 * q - a.v * b.d2) / (q * q)); }
                else { CirDual args[2]; args[0] = a; args[1] = b; r = cir_fcall(CIR_FN_POW, args); }
                stack[sp - 1] = r;
                break;
            }
            case CIR_F_CALL: {
                int n = (int)in->value;
                sp -= n;
                stack[sp] = cir_fcall(in->arg, stack + sp);
                sp++;
                break;
            }
        }
    }
    return sp > 0 ? stack[sp - 1] : cir_dual(0, 0, 0);
}

/* The deepest the stack of a program gets (it must fit the places of evaluate). */
static int cir_stackDepth(const CirFormula* prog) {
    int sp = 0, most = 0;
    size_t k;
    for (k = 0; k < prog->code.n; k++) {
        const CirFInstr* in = &prog->code.d[k];
        if (in->op == CIR_F_CONST || in->op == CIR_F_VAR) sp++;
        else if (in->op == CIR_F_NEG) {}
        else if (in->op == CIR_F_CALL) sp -= (int)in->value - 1;
        else sp--;
        if (sp > most) most = sp;
    }
    return most;
}

/* ---- parts that are not linear: a diode (nodes anode, cathode), a bipolar transistor (collector, base, emitter), a MOSFET (drain, gate, source), an operational
 * amplifier (in+, in-, out), a formula part, a tube, a resistance that follows a state. Every iteration of Newton's method linearizes it at the present guess: the
 * currents into its terminals and their derivatives by the node voltages. ---- */

/* the capacitances: a junction between the terminals a and b with the depletion capacitance of SPICE (cj0, vj, m; fc = 0.5) and a diffusion charge tt * (the
 * current of the junction); integrated with backward Euler. Diode: anode-cathode; bipolar: base-emitter (TF), base-collector; MOSFET: gate-source and gate-drain
 * (m = 0: linear) */
typedef struct CirJunction {
    int a, b;
    double cj0, vj, m, tt;
    double id, gd;            /* the current of the junction and its derivative (for the diffusion charge), set by the model */
    double qPrev, iPrev;      /* after the last step */
    int started;
    double v, q, c;           /* at the linearization of the last iteration */
    /* the constants of the forward branch of the depletion charge (they depend on vj and m only), made at the first use */
    double f1, f2, f3, fm, fvj;
} CirJunction;

typedef struct CirNonlinear {
    int kind;
    int node[4];
    int terminals;
    CirFormula* f1;           /* a formula part: the current into terminal 0 (and into terminal 1); owned by the part */
    CirFormula* f2;
    CirJunction junction[3];
    int junctions;
    double p[8];              /* diode: Is, n, breakdown voltage, current at the breakdown voltage; bipolar: Is, beta forward, beta reverse, -; MOSFET: threshold,
                               * beta (A/V^2), lambda, -; op amp: gain, lowest output, highest output, output resistance */
    double lim[2];            /* the junction voltages of the last iteration (limited) */
    double cur[4];            /* the currents into its terminals after the last step */
    int aux;                  /* a node a state reads besides its terminals (the cathode of a thyristor) */
    double st[2];             /* the state of a resistance that follows a state: lamp temperature / resistance, fuse heat / blown, memristor doped width /
                               * resistance, spark gap on; the analog switch: on */
    int limited;              /* the last linearization used a limited junction voltage (Newton's method must go on) */
    /* the linearization: I(V) = i0 + J (V - v0) */
    double i0[4], j[4][4], v0[4];
} CirNonlinear;

static void cir_nlInit(CirNonlinear* e) {
    int k;
    memset(e, 0, sizeof *e);
    for (k = 0; k < 3; k++) {
        e->junction[k].vj = 0.7;
        e->junction[k].m = 0.5;
        e->junction[k].f2 = 1;
        e->junction[k].fm = -1;
        e->junction[k].fvj = -1;
    }
    e->aux = -1;
}

/* The junction limit of SPICE (pnjlim): an exponential junction may not jump by many thermal voltages from one iteration to the next. */
static double cir_pnjlim(double vnew, double vold, double vt, double vcrit) {
    if (vnew > vcrit && fabs(vnew - vold) > 2 * vt) {
        if (vold > 0) {
            double arg = 1 + (vnew - vold) / vt;
            vnew = arg > 0 ? vold + vt * log(arg) : vcrit;
        } else vnew = vt * log(vnew / vt);
    }
    return vnew;
}

/* exp with a straight line above x = 80 (no overflow while Newton's method is still far away). */
static double cir_expl(double x, double* derivative) {
    double e;
    if (x > 80) { e = exp(80.0); *derivative = e; return e * (1 + (x - 80)); }
    e = exp(x);
    *derivative = e;
    return e;
}

/* The depletion charge of SPICE (cj0, vj, m, fc = 0.5) at the junction voltage v, and its capacitance. `j` keeps the constants of the forward branch. */
static void cir_depletion(CirJunction* j, double v, double* q, double* c) {
    const double fc = 0.5;
    const double cj0 = j->cj0, vj = j->vj;
    double m;
    if (cj0 == 0) { *q = 0; *c = 0; return; }
    m = j->m >= 0.999 ? 0.999 : j->m;
    if (v < fc * vj) {
        double r = 1 - v / vj, rm = pow(r, -m);   /* r^(1-m) = r * r^-m: one pow for both */
        *q = cj0 * vj * (1 - r * rm) / (1 - m);
        *c = cj0 * rm;
    } else {
        double v1;
        if (j->fm != m || j->fvj != vj) {
            j->f1 = vj * (1 - pow(1 - fc, 1 - m)) / (1 - m);
            j->f2 = pow(1 - fc, 1 + m);
            j->f3 = 1 - fc * (1 + m);
            j->fm = m; j->fvj = vj;
        }
        v1 = fc * vj;
        *q = cj0 * (j->f1 + (j->f3 * (v - v1) + m / (2 * vj) * (v * v - v1 * v1)) / j->f2);
        *c = cj0 / j->f2 * (j->f3 + m * v / vj);
    }
}

/* The polarity of a part for its junctions (1, or -1 for PNP and PMOS). */
static double cir_polarity(const CirNonlinear* e) { return e->kind == CIR_NL_PNP || e->kind == CIR_NL_PMOS ? -1.0 : 1.0; }

/* Adds the currents of the capacitances to the linearization: i = (q(v) - q before) / dt, di/dv = C(v) / dt - backward Euler: the charges of the junctions are
 * stiff (a stored charge leaves within nanoseconds), the trapezoidal rule would make them ring from step to step. */
static void cir_stampCharges(CirNonlinear* e, double dt) {
    double pol = cir_polarity(e);
    int k;
    for (k = 0; k < e->junctions; k++) {
        CirJunction* j = &e->junction[k];
        double v = pol * (e->v0[j->a] - e->v0[j->b]);
        double q, c, ic, gc;
        cir_depletion(j, v, &q, &c);
        q += j->tt * j->id;
        c += j->tt * j->gd;
        if (!j->started) { j->qPrev = q; j->iPrev = 0; j->started = 1; }
        j->v = v; j->q = q; j->c = c;
        ic = (q - j->qPrev) / dt; gc = c / dt;
        e->i0[j->a] += pol * ic; e->i0[j->b] -= pol * ic;
        e->j[j->a][j->a] += gc; e->j[j->a][j->b] -= gc; e->j[j->b][j->a] -= gc; e->j[j->b][j->b] += gc;
    }
}

/* the voltage of terminal t of a part (a terminal on no node: 0) */
static double cir_V(const CirNonlinear* e, const double* x, int t) { int n = e->node[t]; return n < 0 ? 0.0 : x[n]; }

/* After a step: the charges and currents of the capacitances at the voltages found (from the linearization of the last iteration). */
static void cir_commitCharges(CirNonlinear* e, const double* x, double dt) {
    double pol = cir_polarity(e);
    int k;
    for (k = 0; k < e->junctions; k++) {
        CirJunction* j = &e->junction[k];
        double v = pol * (cir_V(e, x, j->a) - cir_V(e, x, j->b));
        double q = j->q + j->c * (v - j->v);
        j->iPrev = (q - j->qPrev) / dt;
        j->qPrev = q;
    }
}

/* The resistance of a part that follows a state (LAMP ... SPARK_GAP, SCR, TRIAC) for the present step. */
static double cir_varResistance(const CirNonlinear* e) {
    switch (e->kind) {
        case CIR_NL_LAMP: return e->st[1];
        case CIR_NL_FUSE: return e->st[1] != 0 ? 1e9 : e->p[0];
        case CIR_NL_MEMRISTOR: return e->st[1];
        case CIR_NL_SPARK_GAP: return e->st[0] != 0 ? e->p[0] : e->p[1];
        case CIR_NL_SCR: return e->st[0] != 0 ? 0.0105 : 1e6;
        case CIR_NL_TRIAC: return e->st[0] != 0 ? 0.01 : 1e6;
        default: return 1.0;
    }
}

/* The resistance of a lamp at the temperature t (Falstad's LampElm, after http://www.intusoft.com/nlpdf/nl11.pdf): nominal power p0 at the voltage p1. */
static double cir_lampResistance(double nomPower, double nomVolts, double t) {
    double nomR = nomVolts * nomVolts / nomPower;
    if (t > 5390) t = 5390;
    return nomR * (1.26104 - 4.90662 * sqrt(17.1839 / t - 0.00318794) - 7.8569 / (t - 187.56));
}

/* Before a step: the state of a part that follows a state moves on with the current and voltage of the last step (Falstad's startIteration); `gate` is the voltage
 * of node2 against node1 (triac) or against aux (thyristor). */
static void cir_stepState(CirNonlinear* e, double v, double i, double dt, double gate) {
    if (e->kind == CIR_NL_SCR || e->kind == CIR_NL_TRIAC) {
        /* p: trigger current, holding current, gate resistance */
        double ig = gate / e->p[2];
        if (e->kind == CIR_NL_SCR) e->st[0] = ig / e->p[0] + i / e->p[1] > 1 ? 1 : 0;
        else {
            if (fabs(i) < e->p[1]) e->st[0] = 0;
            if (fabs(ig) > e->p[0]) e->st[0] = 1;
        }
        return;
    }
    if (e->kind == CIR_NL_LAMP) {
        double nomPower = e->p[0], warm = e->p[2], cool = e->p[3];
        double cap, capw, capc, cr;
        e->st[1] = cir_lampResistance(nomPower, e->p[1], e->st[0]);
        cap = 1.57e-4 * nomPower; capw = cap * warm / 0.4; capc = cap * cool / 0.4; cr = 2600 / nomPower;
        e->st[0] += v * i * dt / capw;
        e->st[0] -= dt * (e->st[0] - 300) / (capc * cr);
    } else if (e->kind == CIR_NL_FUSE) {
        double i2t = e->p[1];
        e->st[0] += i * i * dt;
        e->st[0] -= dt * i2t / 3;   /* it gives off its whole i2t in 3 seconds */
        if (e->st[0] < 0) e->st[0] = 0;
        if (e->st[0] > i2t) e->st[1] = 1;
    } else if (e->kind == CIR_NL_MEMRISTOR) {
        double ron = e->p[0], roff = e->p[1], width = e->p[2], mobility = e->p[3];
        double wd = e->st[0] / width;
        e->st[0] += dt * mobility * ron * i / width;
        if (e->st[0] < 0) e->st[0] = 0;
        if (e->st[0] > width) e->st[0] = width;
        e->st[1] = ron * wd + roff * (1 - wd);
    } else if (e->kind == CIR_NL_SPARK_GAP) {
        if (fabs(i) < e->p[3]) e->st[0] = 0;
        if (fabs(v) > e->p[2]) e->st[0] = 1;
    }
}

/* ln(1 + e^x) without overflow, and its derivative (the logistic function). */
static double cir_softplus(double x, double* derivative) {
    double e;
    if (x > 30) { *derivative = 1; return x; }
    if (x < -30) { e = exp(x); *derivative = e; return e; }
    e = exp(x);
    *derivative = e / (1 + e);
    return log1p(e);
}

/* The grid current of a tube once the grid is positive against the cathode: about Vgk / rgi (Koren's grid diode with its series resistance), starting smoothly at
 * 0 V; and its derivative. */
static double cir_gridCurrent(double vg, double rgi, double* derivative) {
    double d;
    if (vg <= 0 || rgi <= 0) { *derivative = 0; return 0; }
    d = vg + 0.5;
    *derivative = (vg * vg + vg) / (rgi * d * d);
    return vg * vg / (rgi * d);
}

/* Linearizes a vacuum tube. Diode: Ip = k * Vpk^ex (Child and Langmuir, ex = 1.5). Triode (Koren): E1 = Vpk / kp * ln(1 + exp(kp * (1/mu + Vgk / sqrt(kvb +
 * Vpk^2)))), Ip = 2 * E1^ex / kg1. Pentode (Koren): E1 = Vg2k / kp * ln(1 + exp(kp * (1/mu + Vg1k / Vg2k))), Ip = 2 * E1^ex / kg1 * atan(Vpk / kvb), the screen
 * current (Vg1k + Vg2k / mu)^ex / kg2. A positive grid draws current through rgi. */
static void cir_linearizeTube(CirNonlinear* e, const double* x) {
    if (e->kind == CIR_NL_VACUUM_DIODE) {
        double k = e->p[0], ex = e->p[1];
        double v = cir_V(e, x, 0) - cir_V(e, x, 1);
        double i = 0, g = 0;
        if (v > 0) { i = k * pow(v, ex); g = k * ex * pow(v, ex - 1); }
        i += CIR_GMIN * v; g += CIR_GMIN;
        e->i0[0] = i; e->i0[1] = -i;
        e->j[0][0] = g; e->j[0][1] = -g; e->j[1][0] = -g; e->j[1][1] = g;
        return;
    }
    if (e->kind == CIR_NL_TRIODE) {
        double mu = e->p[0], ex = e->p[1], kg1 = e->p[2], kp = e->p[3], kvb = e->p[4], rgi = e->p[5];
        double vp = cir_V(e, x, 0) - cir_V(e, x, 2), vg = cir_V(e, x, 1) - cir_V(e, x, 2);
        double s = sqrt(kvb + vp * vp);
        double sig, sp = cir_softplus(kp * (1 / mu + vg / s), &sig);
        double e1 = vp / kp * sp;
        double de1p = sp / kp - sig * vg * vp * vp / (s * s * s), de1g = vp * sig / s;
        double ip = 0, dip = 0, gp, gg, dig, ig;
        double r0[3], r1[3];
        int b;
        if (e1 > 0) { ip = 2 * pow(e1, ex) / kg1; dip = 2 * ex * pow(e1, ex - 1) / kg1; }
        gp = dip * de1p + CIR_GMIN; gg = dip * de1g;
        ip += CIR_GMIN * vp;
        ig = cir_gridCurrent(vg, rgi, &dig);
        ig += CIR_GMIN * vg; dig += CIR_GMIN;
        /* rows: plate, grid, cathode; columns: the node voltages of plate, grid, cathode */
        e->i0[0] = ip; e->i0[1] = ig; e->i0[2] = -(ip + ig);
        r0[0] = gp; r0[1] = gg; r0[2] = -(gp + gg);
        r1[0] = 0; r1[1] = dig; r1[2] = -dig;
        for (b = 0; b < 3; b++) { e->j[0][b] = r0[b]; e->j[1][b] = r1[b]; e->j[2][b] = -(r0[b] + r1[b]); }
        return;
    }
    {
        /* the pentode: plate 0, screen 1, grid 2, cathode 3 (the suppressor is joined to the cathode) */
        double mu = e->p[0], ex = e->p[1], kg1 = e->p[2], kg2 = e->p[3], kp = e->p[4], kvb = e->p[5], rgi = e->p[6];
        double vp = cir_V(e, x, 0) - cir_V(e, x, 3), vs = cir_V(e, x, 1) - cir_V(e, x, 3), vg = cir_V(e, x, 2) - cir_V(e, x, 3);
        double vsc = vs > 1e-3 ? vs : 1e-3;
        double sig, sp = cir_softplus(kp * (1 / mu + vg / vsc), &sig);
        double e1 = vsc / kp * sp;
        double de1s = vs > 1e-3 ? sp / kp - sig * vg / vsc : 0, de1g = sig;
        double a = 0, da = 0;
        double ip = 0, dpp = 0, dps = 0, dpg = 0;
        double w, is = 0, dsg = 0, dss = 0, dig, ig;
        double r0[4], r1[4], r2[4];
        int b;
        if (vp > 0) { double r = vp / kvb; a = atan(r); da = 1 / (kvb * (1 + r * r)); }
        if (e1 > 0) {
            double pw = pow(e1, ex), dpw = ex * pow(e1, ex - 1);
            ip = 2 * pw / kg1 * a;
            dpp = 2 * pw / kg1 * da;
            dps = 2 * dpw / kg1 * a * de1s;
            dpg = 2 * dpw / kg1 * a * de1g;
        }
        ip += CIR_GMIN * vp; dpp += CIR_GMIN;
        w = vg + vs / mu;
        if (w > 0) { is = pow(w, ex) / kg2; dsg = ex * pow(w, ex - 1) / kg2; dss = dsg / mu; }
        is += CIR_GMIN * vs; dss += CIR_GMIN;
        ig = cir_gridCurrent(vg, rgi, &dig);
        ig += CIR_GMIN * vg; dig += CIR_GMIN;
        e->i0[0] = ip; e->i0[1] = is; e->i0[2] = ig; e->i0[3] = -(ip + is + ig);
        r0[0] = dpp; r0[1] = dps; r0[2] = dpg; r0[3] = -(dpp + dps + dpg);
        r1[0] = 0; r1[1] = dss; r1[2] = dsg; r1[3] = -(dss + dsg);
        r2[0] = 0; r2[1] = 0; r2[2] = dig; r2[3] = -dig;
        for (b = 0; b < 4; b++) { e->j[0][b] = r0[b]; e->j[1][b] = r1[b]; e->j[2][b] = r2[b]; e->j[3][b] = -(r0[b] + r1[b] + r2[b]); }
    }
}

/* (marks a part whose junction voltage was limited: Newton's method must go on) */
static void cir_check(CirNonlinear* e, double wanted, double used) { if (fabs(wanted - used) > 1e-9 + 1e-6 * fabs(wanted)) e->limited = 1; }

/* Linearizes the part at the node voltages x (with limited junctions). */
static void cir_linearize(CirNonlinear* e, const double* x) {
    const int nt = e->terminals;
    int a, b;
    for (a = 0; a < nt; a++) { e->i0[a] = 0; for (b = 0; b < nt; b++) e->j[a][b] = 0; e->v0[a] = cir_V(e, x, a); }
    e->limited = 0;
    if (e->kind == CIR_NL_DIODE) {
        double is = e->p[0], nvt = e->p[1] * CIR_VT, bv = e->p[2];
        double vcrit = nvt * log(nvt / (1.4142135623730951 * is));
        double vd = cir_V(e, x, 0) - cir_V(e, x, 1);
        double vl = cir_pnjlim(vd, e->lim[0], nvt, vcrit);
        double d1, ex, i, g;
        if (bv > 0 && vd < -bv) {   /* in breakdown the same limit, seen from the other side */
            double vr = -(vd + bv), vrOld = -(e->lim[0] + bv);
            vl = -(cir_pnjlim(vr, vrOld, nvt, vcrit) + bv);
        }
        cir_check(e, vd, vl);
        e->lim[0] = vl;
        ex = cir_expl(vl / nvt, &d1);
        i = is * (ex - 1); g = is / nvt * d1;
        if (bv > 0) {
            /* the breakdown of SPICE: IBV flows at -BV and grows with the same slope as the forward current */
            double ibv = e->p[3] > 0 ? e->p[3] : 1e-3;
            double d2, ez = cir_expl(-(vl + bv) / nvt, &d2);
            i -= ibv * ez;
            g += ibv / nvt * d2;
        }
        if (e->junctions > 0) { e->junction[0].id = i; e->junction[0].gd = g; }
        i += CIR_GMIN * vl;
        g += CIR_GMIN;
        e->v0[0] = cir_V(e, x, 1) + vl;
        e->i0[0] = i; e->i0[1] = -i;
        e->j[0][0] = g; e->j[0][1] = -g; e->j[1][0] = -g; e->j[1][1] = g;
    } else if (e->kind == CIR_NL_NPN || e->kind == CIR_NL_PNP) {
        double pol = e->kind == CIR_NL_NPN ? 1.0 : -1.0;
        double is = e->p[0], bf = e->p[1], br = e->p[2];
        double vcrit = CIR_VT * log(CIR_VT / (1.4142135623730951 * is));
        double vbe = cir_pnjlim(pol * (cir_V(e, x, 1) - cir_V(e, x, 2)), e->lim[0], CIR_VT, vcrit);
        double vbc = cir_pnjlim(pol * (cir_V(e, x, 1) - cir_V(e, x, 0)), e->lim[1], CIR_VT, vcrit);
        double df, dr, ef, er, iF, iR, gf, gr, ic, ib, dicBe, dicBc, dibBe, dibBc;
        double jc[3], jb[3];
        cir_check(e, pol * (cir_V(e, x, 1) - cir_V(e, x, 2)), vbe);
        cir_check(e, pol * (cir_V(e, x, 1) - cir_V(e, x, 0)), vbc);
        e->lim[0] = vbe; e->lim[1] = vbc;
        ef = cir_expl(vbe / CIR_VT, &df); er = cir_expl(vbc / CIR_VT, &dr);
        iF = is * (ef - 1) + CIR_GMIN * vbe; iR = is * (er - 1) + CIR_GMIN * vbc;
        gf = is / CIR_VT * df + CIR_GMIN; gr = is / CIR_VT * dr + CIR_GMIN;
        ic = iF - iR - iR / br; ib = iF / bf + iR / br;
        if (e->junctions > 0) { e->junction[0].id = iF; e->junction[0].gd = gf; }
        if (e->junctions > 1) { e->junction[1].id = iR; e->junction[1].gd = gr; }
        dicBe = gf; dicBc = -gr * (1 + 1 / br); dibBe = gf / bf; dibBc = gr / br;
        /* currents into the terminals (collector, base, emitter) and their derivatives by the node voltages (the polarity cancels: pol * pol = 1) */
        e->i0[0] = pol * ic; e->i0[1] = pol * ib; e->i0[2] = -pol * (ic + ib);
        jc[0] = -dicBc; jc[1] = dicBe + dicBc; jc[2] = -dicBe;
        jb[0] = -dibBc; jb[1] = dibBe + dibBc; jb[2] = -dibBe;
        for (b = 0; b < 3; b++) { e->j[0][b] = jc[b]; e->j[1][b] = jb[b]; e->j[2][b] = -(jc[b] + jb[b]); }
        e->v0[1] = cir_V(e, x, 1);
        e->v0[2] = cir_V(e, x, 1) - pol * vbe;
        e->v0[0] = cir_V(e, x, 1) - pol * vbc;
    } else if (e->kind == CIR_NL_VACUUM_DIODE || e->kind == CIR_NL_TRIODE || e->kind == CIR_NL_PENTODE) {
        cir_linearizeTube(e, x);
    } else if (e->kind == CIR_NL_FORMULA) {
        int three = e->terminals == 3;
        int ref = three ? 2 : 1;
        double want1 = cir_V(e, x, 0) - cir_V(e, x, ref), want2 = three ? cir_V(e, x, 1) - cir_V(e, x, ref) : 0;
        double v1 = want1, v2 = want2;
        double t;
        CirDual i1;
        /* a formula with exp or pow: the voltages move at most 0.5 V per iteration (like the limits of the junctions) */
        if (e->f1->exponential || (e->f2 && e->f2->exponential)) {
            if (fabs(v1 - e->lim[0]) > 0.5) v1 = e->lim[0] + (v1 > e->lim[0] ? 0.5 : -0.5);
            if (three && fabs(v2 - e->lim[1]) > 0.5) v2 = e->lim[1] + (v2 > e->lim[1] ? 0.5 : -0.5);
        }
        cir_check(e, want1, v1);
        cir_check(e, want2, v2);
        e->lim[0] = v1; e->lim[1] = v2;
        t = e->p[0];   /* (the time of the step, set by the kernel before Newton's method) */
        i1 = cir_evaluate(e->f1, v1, v2, t);
        i1.v += CIR_GMIN * v1; i1.d1 += CIR_GMIN;
        if (!three) {
            e->i0[0] = i1.v; e->i0[1] = -i1.v;
            e->j[0][0] = i1.d1; e->j[0][1] = -i1.d1; e->j[1][0] = -i1.d1; e->j[1][1] = i1.d1;
            e->v0[0] = cir_V(e, x, 1) + v1;
        } else {
            CirDual i2 = e->f2 ? cir_evaluate(e->f2, v1, v2, t) : cir_dual(0, 0, 0);
            double r0[3], r1[3];
            i2.v += CIR_GMIN * v2; i2.d2 += CIR_GMIN;
            e->i0[0] = i1.v; e->i0[1] = i2.v; e->i0[2] = -(i1.v + i2.v);
            r0[0] = i1.d1; r0[1] = i1.d2; r0[2] = -(i1.d1 + i1.d2);
            r1[0] = i2.d1; r1[1] = i2.d2; r1[2] = -(i2.d1 + i2.d2);
            for (b = 0; b < 3; b++) { e->j[0][b] = r0[b]; e->j[1][b] = r1[b]; e->j[2][b] = -(r0[b] + r1[b]); }
            e->v0[0] = cir_V(e, x, 2) + v1; e->v0[1] = cir_V(e, x, 2) + v2;
        }
    } else if (cir_isVarRes(e->kind) || e->kind == CIR_NL_ANALOG_SWITCH) {
        double g, v;
        if (e->kind == CIR_NL_ANALOG_SWITCH) {
            /* on above the threshold (below it when inverted), read anew in every iteration (as Falstad does) */
            int on = cir_V(e, x, 2) >= e->p[2];
            if (e->p[3] != 0) on = !on;
            e->st[0] = on ? 1 : 0;
            g = 1.0 / (on ? e->p[0] : e->p[1]);
        } else g = 1.0 / cir_varResistance(e);
        v = cir_V(e, x, 0) - cir_V(e, x, 1);
        e->i0[0] = g * v; e->i0[1] = -g * v;
        e->j[0][0] = g; e->j[0][1] = -g; e->j[1][0] = -g; e->j[1][1] = g;
    } else if (e->kind == CIR_NL_OPAMP) {
        /* the output follows mid + swing * tanh(gain * (in+ - in-) / swing) through its output resistance: linear with a high gain, limited at the rails */
        double gain = e->p[0], lo = e->p[1], hi = e->p[2], rout = e->p[3];
        double mid = (hi + lo) / 2, swing = (hi - lo) / 2;
        double vd = cir_V(e, x, 0) - cir_V(e, x, 1);
        double t = tanh(gain * vd / swing);
        double target = mid + swing * t;
        double slope = gain * (1 - t * t);
        double g = 1.0 / rout;
        /* current into the output terminal: (Vout - target) / rout */
        e->i0[2] = (cir_V(e, x, 2) - target) * g;
        e->j[2][0] = -slope * g; e->j[2][1] = slope * g; e->j[2][2] = g;
    } else {
        double pol = e->kind == CIR_NL_NMOS ? 1.0 : -1.0;
        double vth = e->p[0], beta = e->p[1], lambda = e->p[2];
        /* drain and source may change places (the device is symmetric): d and s are the terminals that act as drain and source now */
        int d = 0, s = 2;
        double vgs, vds, vgsOld, vgsWanted, vov, id = 0, gm = 0, gds = 0;
        if (pol * (cir_V(e, x, 0) - cir_V(e, x, 2)) < 0) { d = 2; s = 0; }
        vgs = pol * (cir_V(e, x, 1) - cir_V(e, x, s)); vds = pol * (cir_V(e, x, d) - cir_V(e, x, s));
        /* limits in the manner of fetlim: the gate voltage moves at most 0.5 V around the threshold per iteration */
        vgsOld = e->lim[0]; vgsWanted = vgs;
        if (fabs(vgs - vgsOld) > 0.5 && (vgs > vth || vgsOld > vth)) vgs = vgsOld + (vgs > vgsOld ? 0.5 : -0.5);
        cir_check(e, vgsWanted, vgs);
        e->lim[0] = vgs;
        vov = vgs - vth;
        if (vov > 0) {
            if (vds < vov) {
                id = beta * (vov * vds - vds * vds / 2) * (1 + lambda * vds);
                gm = beta * vds * (1 + lambda * vds);
                gds = beta * (vov - vds) * (1 + lambda * vds) + beta * (vov * vds - vds * vds / 2) * lambda;
            } else {
                id = beta / 2 * vov * vov * (1 + lambda * vds);
                gm = beta * vov * (1 + lambda * vds);
                gds = beta / 2 * vov * vov * lambda;
            }
        }
        id += CIR_GMIN * vds;
        gds += CIR_GMIN;
        /* terminals: 0 drain, 1 gate, 2 source of the part; d/s are the ones acting now */
        e->i0[d] = pol * id; e->i0[s] = -pol * id;
        e->j[d][1] = gm; e->j[d][d] = gds; e->j[d][s] = -(gm + gds);
        e->j[s][1] = -gm; e->j[s][d] = -gds; e->j[s][s] = gm + gds;
        e->v0[s] = cir_V(e, x, s);
        e->v0[1] = cir_V(e, x, s) + pol * vgs;
    }
}

/* ---- logic parts ---- */
enum { CIR_G_AND = 0, CIR_G_OR = 1, CIR_G_XOR = 2, CIR_G_NAND = 3, CIR_G_NOR = 4, CIR_G_XNOR = 5, CIR_G_NOT = 6, CIR_G_BUFFER = 7, CIR_G_DFLIPFLOP = 8,
       CIR_G_COMPARATOR = 9, CIR_G_SRLATCH = 10, CIR_G_JKFLIPFLOP = 11, CIR_G_SCHMITT = 12, CIR_G_INV_SCHMITT = 13 };

/* A logic part: it reads its inputs after every step (high above `threshold`) and drives its outputs for the next one through `rout` (high: `high` volts, low:
 * 0 V). A D flip-flop has the inputs D and clock and the outputs Q and not Q; it takes D on the rising edge of the clock. A comparator is high while its first
 * input is above its second (analog). An SR latch has the inputs S, R and (optional) a reset that is active low; R wins over S. A JK flip-flop has the inputs J,
 * clock, K and (optional) a reset that is active high: on the edge of the clock (rising, or falling with `falling`) J and K both high toggle it, J sets it, K
 * clears it. A Schmitt trigger goes high above `threshold` and low below `low` (in between it keeps its state); the inverting one the other way round. */
typedef struct CirGate {
    int kind;
    CirI inputs;
    int out, outNot;
    double high, threshold, g;
    double low;            /* the lower threshold of a Schmitt trigger */
    int falling;           /* a JK flip-flop that takes the falling edge */
    int state, clock;
    int primed;            /* a flip-flop has seen its clock once (the first step only notes it: no edge at power-on) */
} CirGate;

static double cir_gateVolts(const CirGate* g, const double* x, size_t i) { int n = g->inputs.d[i]; return n < 0 ? 0.0 : x[n]; }
static int cir_gateIn(const CirGate* g, const double* x, size_t i) { return cir_gateVolts(g, x, i) > g->threshold; }

static int cir_gateEval(CirGate* g, const double* x) {
    size_t i, n = g->inputs.n;
    int r;
    if (g->kind == CIR_G_COMPARATOR) return n > 1 && cir_gateVolts(g, x, 0) > cir_gateVolts(g, x, 1);
    if (g->kind == CIR_G_SRLATCH) {
        int reset = n > 2 && !cir_gateIn(g, x, 2);
        if (reset || (n > 1 && cir_gateIn(g, x, 1))) g->state = 0;
        else if (cir_gateIn(g, x, 0)) g->state = 1;
        return g->state;
    }
    if (g->kind == CIR_G_JKFLIPFLOP) {
        int c = n > 1 && cir_gateIn(g, x, 1);
        int edge = g->falling ? (!c && g->clock) : (c && !g->clock);
        if (g->primed && edge) {
            int j = cir_gateIn(g, x, 0), k = n > 2 && cir_gateIn(g, x, 2);
            if (j && k) g->state = !g->state;
            else if (j) g->state = 1;
            else if (k) g->state = 0;
        }
        g->primed = 1;
        g->clock = c;
        if (n > 3 && cir_gateIn(g, x, 3)) g->state = 0;
        return g->state;
    }
    if (g->kind == CIR_G_SCHMITT || g->kind == CIR_G_INV_SCHMITT) {
        /* `clock` keeps whether the input was last seen above the upper threshold */
        double v = cir_gateVolts(g, x, 0);
        if (v > g->threshold) g->clock = 1;
        else if (v < g->low) g->clock = 0;
        return g->kind == CIR_G_SCHMITT ? g->clock : !g->clock;
    }
    if (g->kind == CIR_G_DFLIPFLOP) {
        int c = n > 1 && cir_gateIn(g, x, 1);
        if (g->primed && c && !g->clock) g->state = cir_gateIn(g, x, 0);
        g->primed = 1;
        g->clock = c;
        return g->state;
    }
    r = g->kind == CIR_G_AND || g->kind == CIR_G_NAND || g->kind == CIR_G_XNOR;
    if (g->kind == CIR_G_XOR || g->kind == CIR_G_XNOR) { r = 0; for (i = 0; i < n; i++) r ^= cir_gateIn(g, x, i); if (g->kind == CIR_G_XNOR) r = !r; return r; }
    if (g->kind == CIR_G_NOT || g->kind == CIR_G_BUFFER) { int v = n > 0 && cir_gateIn(g, x, 0); return g->kind == CIR_G_NOT ? !v : v; }
    for (i = 0; i < n; i++) {
        if (g->kind == CIR_G_AND || g->kind == CIR_G_NAND) r = r && cir_gateIn(g, x, i); else r = r || cir_gateIn(g, x, i);
    }
    if (g->kind == CIR_G_NAND || g->kind == CIR_G_NOR) r = !r;
    return r;
}

/* ---- chips: logic and mixed-signal ICs after Falstad's ChipElm ---- */
enum { CIR_C_TFLIPFLOP = 1, CIR_C_LATCH = 2, CIR_C_RING_COUNTER = 3, CIR_C_MUX = 4, CIR_C_DEMUX = 5, CIR_C_HALF_ADDER = 6, CIR_C_FULL_ADDER = 7, CIR_C_SIPO = 8,
       CIR_C_PISO = 9, CIR_C_ADC = 10, CIR_C_DAC = 11, CIR_C_SEVEN_SEG_DECODER = 12, CIR_C_MONOSTABLE = 13, CIR_C_PHASE_COMP = 14, CIR_C_VCO = 15 };
enum { CIR_PIN_IN = 0, CIR_PIN_OUT = 1, CIR_PIN_CURRENT = 2 };

/* A chip: pins in the order of Falstad's chip (inputs, outputs that drive a level through `1/g` like the gates, and outputs that drive a current), its logic run
 * once per step from the voltages of the step before (cir_chipExecute, as in Falstad's startIteration). `bits` and `flags` say how it is made. */
typedef struct CirChip {
    int kind, bits, flags;
    CirI node;
    CirC mode;
    CirC val;            /* the logic value of each pin (inputs: read; outputs: driven) */
    CirD level;          /* what an output drives this step: volts (PIN_OUT) or amperes (PIN_CURRENT) */
    CirD st;             /* the state of the chip (data of a shift register, times of a monostable, ...) */
    double high, threshold, g;
    double p[4];
    int lastClock, lastLoad;
} CirChip;

static void cir_chipInit(CirChip* c) {
    memset(c, 0, sizeof *c);
    c->high = 5; c->threshold = 2.5; c->g = 0.1;
}
static void cir_chipFree(CirChip* c) { CIR_FREE(c->node); CIR_FREE(c->mode); CIR_FREE(c->val); CIR_FREE(c->level); CIR_FREE(c->st); }

static int cir_chipIn(const CirChip* c, size_t i) { return c->val.d[i] != 0; }
static void cir_chipSet(CirChip* c, size_t i, int v) { c->val.d[i] = v ? 1 : 0; }
static double cir_chipVolts(const CirChip* c, const double* x, size_t i) { int n = c->node.d[i]; return n < 0 ? 0.0 : x[n]; }

static void cir_chipExecute(CirChip* c, const double* x, double time) {
    size_t i;
    const size_t n = (size_t)c->bits;
    for (i = 0; i < c->node.n; i++) {
        if (c->mode.d[i] == CIR_PIN_IN) c->val.d[i] = cir_chipVolts(c, x, i) > c->threshold ? 1 : 0;
        c->level.d[i] = NAN;
    }
    switch (c->kind) {
        case CIR_C_TFLIPFLOP: {
            /* T, Q, /Q, clock [, R [, S]] - flags 1: reset, 2: set */
            int reset = (c->flags & 1) || (c->flags & 2);
            int clk = cir_chipIn(c, 3);
            if (clk && !c->lastClock && cir_chipIn(c, 0)) cir_chipSet(c, 1, !cir_chipIn(c, 1));
            if ((c->flags & 2) && cir_chipIn(c, 5)) cir_chipSet(c, 1, 1);
            if (reset && cir_chipIn(c, 4)) cir_chipSet(c, 1, 0);
            cir_chipSet(c, 2, !cir_chipIn(c, 1));
            c->lastClock = clk;
            break;
        }
        case CIR_C_LATCH: {
            /* I0..In-1, O0..On-1, load [, R] [, S] - flags 1: level (not edge) triggered, 2: reset, 4: set, 8: reset active low */
            size_t load = 2 * n, k = load + 1;
            size_t reset = (c->flags & 2) ? k++ : 0, setp = (c->flags & 4) ? k++ : 0;
            int ld = cir_chipIn(c, load);
            if ((c->flags & 4) && cir_chipIn(c, setp)) { for (i = 0; i < n; i++) cir_chipSet(c, n + i, 1); }
            else if ((c->flags & 2) && cir_chipIn(c, reset) != ((c->flags & 8) != 0)) { for (i = 0; i < n; i++) cir_chipSet(c, n + i, 0); }
            else if (ld && ((c->flags & 1) || !c->lastLoad)) { for (i = 0; i < n; i++) cir_chipSet(c, n + i, cir_chipIn(c, i)); }
            c->lastLoad = ld;
            break;
        }
        case CIR_C_RING_COUNTER: {
            /* clock, R, Q0..Qn-1 [, CE] - flags 1: clock inhibit (CE, active low), 2: reset active high */
            int running = !((c->flags & 1) && cir_chipIn(c, n + 2));
            int resetActive;
            size_t k;
            i = 0;
            while (i < n && !cir_chipIn(c, i + 2)) i++;
            if (cir_chipIn(c, 0) && !c->lastClock && running) {
                if (i < n) cir_chipSet(c, 2 + i++, 0);
                i %= n;
                cir_chipSet(c, 2 + i, 1);
            }
            resetActive = (c->flags & 2) ? cir_chipIn(c, 1) : !cir_chipIn(c, 1);
            if (resetActive || i == n) {
                for (k = 1; k < n; k++) cir_chipSet(c, k + 2, 0);
                cir_chipSet(c, 2, 1);
            }
            c->lastClock = cir_chipIn(c, 0);
            break;
        }
        case CIR_C_MUX: {
            /* I0..I(2^s - 1), S0..Ss-1, Q [, /Q] [, strobe] - bits = s; flags 1: inverted output, 2: strobe */
            size_t count = (size_t)1 << n, sel = 0, out, strobe;
            int v;
            for (i = 0; i < n; i++) if (cir_chipIn(c, count + i)) sel |= (size_t)1 << i;
            out = count + n;
            v = cir_chipIn(c, sel);
            strobe = out + 1 + ((c->flags & 1) ? 1 : 0);
            if ((c->flags & 2) && cir_chipIn(c, strobe)) v = 0;
            cir_chipSet(c, out, v);
            if (c->flags & 1) cir_chipSet(c, out + 1, !v);
            break;
        }
        case CIR_C_DEMUX: {
            /* Q0..Q(2^s - 1), S0..Ss-1, input - flags 1: inverted outputs */
            size_t count = (size_t)1 << n, sel = 0;
            int idle = (c->flags & 1) != 0;
            for (i = 0; i < n; i++) if (cir_chipIn(c, count + i)) sel |= (size_t)1 << i;
            for (i = 0; i < count; i++) cir_chipSet(c, i, idle);
            cir_chipSet(c, sel, cir_chipIn(c, count + n));
            break;
        }
        case CIR_C_HALF_ADDER:
            cir_chipSet(c, 0, cir_chipIn(c, 2) != cir_chipIn(c, 3));
            cir_chipSet(c, 1, cir_chipIn(c, 2) && cir_chipIn(c, 3));
            break;
        case CIR_C_FULL_ADDER: {
            /* A0..An-1, B0..Bn-1, S0..Sn-1, carry in, carry out */
            int carry = cir_chipIn(c, 3 * n) ? 1 : 0;
            for (i = 0; i < n; i++) {
                int v = (cir_chipIn(c, i) ? 1 : 0) + (cir_chipIn(c, i + n) ? 1 : 0) + carry;
                carry = v > 1 ? 1 : 0;
                cir_chipSet(c, i + 2 * n, (v & 1) == 1);
            }
            cir_chipSet(c, 3 * n + 1, carry == 1);
            break;
        }
        case CIR_C_SIPO: {
            /* D, clock, Q0..Qn-1 */
            int clk = cir_chipIn(c, 1);
            if (clk != c->lastClock) {
                c->lastClock = clk;
                if (clk && n > 0) {
                    for (i = n - 1; i >= 1; i--) cir_chipSet(c, 2 + i, cir_chipIn(c, 2 + i - 1));
                    cir_chipSet(c, 2, cir_chipIn(c, 0));
                }
            }
            break;
        }
        case CIR_C_PISO: {
            /* load, clock, Q [, serial in], D(n-1)..D0 - flags 1: Falstad's new behavior (with serial in); st: n data bits, then the index */
            int modern = (c->flags & 1) != 0;
            size_t data = modern ? 4 : 3;
            double* index;
            if (c->st.n < n + 1) cir_dAssign(&c->st, n + 1, 0.0);
            index = &c->st.d[n];
            if (cir_chipIn(c, 0) != c->lastLoad) {
                c->lastLoad = cir_chipIn(c, 0);
                if (c->lastLoad && n > 0) {
                    if (modern) { cir_chipSet(c, 2, cir_chipIn(c, data)); *index = 0; } else *index = -1;
                    for (i = 0; i < n; i++) c->st.d[i] = cir_chipIn(c, data + i) ? 1 : 0;
                }
            }
            if (cir_chipIn(c, 1) != c->lastClock) {
                c->lastClock = cir_chipIn(c, 1);
                if (c->lastClock && n > 0) {
                    if (*index >= 0) c->st.d[(size_t)*index] = (modern && cir_chipIn(c, 3)) ? 1 : 0;
                    *index += 1;
                    if (*index >= (double)n) *index = 0;
                    cir_chipSet(c, 2, c->st.d[(size_t)*index] != 0);
                }
            }
            break;
        }
        case CIR_C_ADC: {
            /* D0..Dn-1, In, V+ (the reference) */
            double imax = (double)(((int64_t)1 << n) - 1), ref = cir_chipVolts(c, x, n + 1);
            int64_t v = ref != 0 ? (int64_t)trunc(imax * cir_chipVolts(c, x, n) / ref) : 0;
            if (v < 0) v = 0;
            if (v > (int64_t)imax) v = (int64_t)imax;
            for (i = 0; i < n; i++) cir_chipSet(c, i, (int)((v >> i) & 1));
            break;
        }
        case CIR_C_DAC: {
            /* D0..Dn-1, O (analog), V+ (the reference) */
            int64_t v = 0;
            double imax;
            for (i = 0; i < n; i++) if (cir_chipIn(c, i)) v |= (int64_t)1 << i;
            imax = (double)(((int64_t)1 << n) - 1);
            c->level.d[n] = imax > 0 ? (double)v * cir_chipVolts(c, x, n + 1) / imax : 0;
            break;
        }
        case CIR_C_SEVEN_SEG_DECODER: {
            /* a..g, I3, I2, I1, I0 [, BI] - flags 1: blanking input (active low), 2: blank on F */
            static const char sym[16][7] = {
                {1,1,1,1,1,1,0}, {0,1,1,0,0,0,0}, {1,1,0,1,1,0,1}, {1,1,1,1,0,0,1}, {0,1,1,0,0,1,1}, {1,0,1,1,0,1,1}, {1,0,1,1,1,1,1}, {1,1,1,0,0,0,0},
                {1,1,1,1,1,1,1}, {1,1,1,1,0,1,1}, {1,1,1,0,1,1,1}, {0,0,1,1,1,1,1}, {1,0,0,1,1,1,0}, {0,1,1,1,1,0,1}, {1,0,0,1,1,1,1}, {1,0,0,0,1,1,1} };
            int v = (cir_chipIn(c, 7) ? 8 : 0) + (cir_chipIn(c, 8) ? 4 : 0) + (cir_chipIn(c, 9) ? 2 : 0) + (cir_chipIn(c, 10) ? 1 : 0);
            int on = !((c->flags & 1) && !cir_chipIn(c, 11)) && !(v == 15 && (c->flags & 2));
            for (i = 0; i < 7; i++) cir_chipSet(c, i, on && sym[v][i]);
            break;
        }
        case CIR_C_MONOSTABLE: {
            /* trigger, Q, /Q - p0 the time; flags 1: inverted trigger, 2: retriggerable; st: triggered, the time of the edge */
            int trig;
            if (c->st.n < 2) cir_dAssign(&c->st, 2, 0.0);
            trig = cir_chipIn(c, 0) != ((c->flags & 1) != 0);
            if (trig && !c->lastClock && ((c->flags & 2) || c->st.d[0] == 0)) {
                c->st.d[1] = time;
                c->st.d[0] = 1;
                cir_chipSet(c, 1, 1);
                cir_chipSet(c, 2, 0);
            }
            if (c->st.d[0] != 0 && time > c->st.d[1] + c->p[0]) {
                cir_chipSet(c, 1, 0);
                cir_chipSet(c, 2, 1);
                c->st.d[0] = 0;
            }
            c->lastClock = trig;
            break;
        }
        case CIR_C_PHASE_COMP: {
            /* I1, I2, the level, enable (the output is open while enable is low) - st: ff1, ff2, the last I1, the last I2 */
            int v1, v2;
            if (c->st.n < 4) cir_dAssign(&c->st, 4, 0.0);
            v1 = cir_chipVolts(c, x, 0) > c->threshold; v2 = cir_chipVolts(c, x, 1) > c->threshold;
            if (v1 && c->st.d[2] == 0) c->st.d[0] = 1;
            if (v2 && c->st.d[3] == 0) c->st.d[1] = 1;
            if (c->st.d[0] != 0 && c->st.d[1] != 0) c->st.d[0] = c->st.d[1] = 0;
            c->st.d[2] = v1 ? 1 : 0;
            c->st.d[3] = v2 ? 1 : 0;
            cir_chipSet(c, 2, c->st.d[0] != 0);
            cir_chipSet(c, 3, c->st.d[0] != 0 || c->st.d[1] != 0);
            break;
        }
        case CIR_C_VCO: {
            /* Vi, Vo, C1, C2, R1 (held at Vi), R2 (held at the high level): the currents of R1 and R2 charge the capacitor between C1 and C2 one way or the
             * other; Vo flips when the capacitor reaches 90 % or 10 % of the high level */
            double vc = cir_chipVolts(c, x, 3) - cir_chipVolts(c, x, 2);
            int vo = cir_chipIn(c, 1);
            int dir = vo ? -1 : 1;
            double i1, i2, cur;
            if (!vo && vc > c->high * 0.9) { vo = 1; dir = -1; }
            if (vo && vc < c->high * 0.1) { vo = 0; dir = 1; }
            cir_chipSet(c, 1, vo);
            c->level.d[4] = cir_chipVolts(c, x, 0);
            c->level.d[5] = c->high;
            i1 = c->g * (cir_chipVolts(c, x, 0) - cir_chipVolts(c, x, 4)); i2 = c->g * (c->high - cir_chipVolts(c, x, 5));
            cur = i1 + i2;
            c->level.d[2] = -dir * cur;
            c->level.d[3] = dir * cur;
            break;
        }
        default: break;
    }
    for (i = 0; i < c->node.n; i++)
        if (isnan(c->level.d[i])) c->level.d[i] = c->mode.d[i] == CIR_PIN_OUT ? (c->val.d[i] ? c->high : 0.0) : 0.0;
}

/* ---- the state of a plan between two steps: what a step changes, so that a step that does not converge can be done again in smaller steps ---- */
/* what a step changes in a part that is not linear (the rest of it - kind, nodes, parameters, the formulas - stays): kept at the start of a step for a retry, small
 * enough to copy every step */
typedef struct CirNlSaved {
    double lim[2], cur[4], st[2];
    double qPrev[3], iPrev[3];
    int started[3];
} CirNlSaved;

/* what a step changes in a chip */
typedef struct CirChipSaved { CirC val; CirD level; CirD st; int lastClock, lastLoad; } CirChipSaved;

typedef CIR_VEC(CirReactive) CirReactives;
typedef CIR_VEC(CirCoupled) CirCoupleds;
typedef CIR_VEC(CirSource) CirSources;
typedef CIR_VEC(CirNlSaved) CirNlSaveds;
typedef CIR_VEC(CirChipSaved) CirChipSaveds;

typedef struct CirSnapshot {
    double time;
    CirD x, xPrev;
    CirReactives capacitors, inductors;
    CirCoupleds coupled;
    CirSources sources;
    CirC gates;              /* per gate: state, clock, primed */
    CirNlSaveds nonlinear;
    CirChipSaveds chips;
} CirSnapshot;

static void cir_snapshotFree(CirSnapshot* s) {
    size_t i;
    CIR_FREE(s->x); CIR_FREE(s->xPrev); CIR_FREE(s->capacitors); CIR_FREE(s->inductors); CIR_FREE(s->coupled); CIR_FREE(s->sources); CIR_FREE(s->gates);
    CIR_FREE(s->nonlinear);
    for (i = 0; i < s->chips.n; i++) { CIR_FREE(s->chips.d[i].val); CIR_FREE(s->chips.d[i].level); CIR_FREE(s->chips.d[i].st); }
    CIR_FREE(s->chips);
}

/* ---- streams ----
 * A stream of samples at a fixed rate between the circuit and the program (or a sound device): a ring of volts. As an output (a tap) the plan writes the voltage
 * between two nodes into it at every point of its grid k / rate - interpolated between the steps around it, also when a step was done again in smaller steps; as an
 * input (a feed) it drives a source, the voltage interpolated between the samples. It lives apart from a plan (like a recorder), so the stream goes on when the
 * circuit is compiled again. */
typedef struct CirDevice {
    int sim;                    /* the simulated device: plays at once and keeps what it played, records what the program gave it (SimInput) */
#ifdef CIR_AUDIO
    FireAudioOutput* out;
    FireAudioInput* in;
#endif
    int rate, channels;
    double fullScale;           /* the volts of a full-scale sample */
    double latency;             /* seconds of sound kept between the circuit and the device */
    int primed;                 /* (an output starts when the latency is filled) */
    CirB pending;               /* bytes the device did not take yet */
    int64_t frames;             /* frames played to / recorded from the device */
    CirF simData;               /* what the simulated output played (up to 16 M samples) */
    CirB simInput;              /* what the simulated input records next */
    size_t simInputAt;
} CirDevice;

static CirDevice* cir_deviceNew(void) {
    CirDevice* d = (CirDevice*)calloc(1, sizeof(CirDevice));
    if (d == NULL) { fputs("circuits: out of memory\n", stderr); abort(); }
    d->rate = 44100; d->channels = 1; d->fullScale = 1.0; d->latency = 0.05;
    return d;
}

static void cir_deviceFree(CirDevice* d) {
    if (d == NULL) return;
#ifdef CIR_AUDIO
    if (d->out) { fire_audio_close(d->out); d->out = NULL; }
    if (d->in) { fire_audio_closeInput(d->in); d->in = NULL; }
#endif
    CIR_FREE(d->pending); CIR_FREE(d->simData); CIR_FREE(d->simInput);
    free(d);
}

typedef struct CirStream {
    float* ring;
    size_t ringSize;
    size_t head, count;
    double rate;
    int64_t overruns, underruns;
    /* as a tap: the next sample index and the step before (time, voltage) */
    int64_t nextIndex;
    double prevT, prevV;
    int havePrev;
    /* as a feed: the two samples around the time of the step (index of the first) */
    int64_t feedIndex;
    double feedA, feedB;
    /* a sound device the stream plays to (an output of the circuit) or records from (an input), or none */
    CirDevice* playTo;
    CirDevice* recordFrom;
} CirStream;

static void cir_streamFree(CirStream* st) {
    if (st == NULL) return;
    cir_deviceFree(st->playTo);
    cir_deviceFree(st->recordFrom);
    free(st->ring);
    free(st);
}

static int cir_streamPush(CirStream* st, float v) {
    if (st->ringSize == 0) return 0;
    if (st->count == st->ringSize) { st->head = (st->head + 1) % st->ringSize; st->count--; st->overruns++; }
    st->ring[(st->head + st->count) % st->ringSize] = v;
    st->count++;
    return 1;
}

static int cir_streamPop(CirStream* st, float* v) {
    if (st->count == 0) return 0;
    *v = st->ring[st->head];
    st->head = (st->head + 1) % st->ringSize;
    st->count--;
    return 1;
}

typedef struct CirTap { int stream, a, b; } CirTap;
typedef struct CirFeed { int stream, source; } CirFeed;

/* a pair (row, value) that the runner adds or sets in every step */
typedef struct CirHold { int row; double value; } CirHold;
typedef CIR_VEC(CirHold) CirHolds;

struct CirRunner;

typedef struct CirPlan {
    int nodes, size;
    /* the thread that simulates the plan in real time (StartRunner), or none */
    struct CirRunner* runner;
    double dt, time;
    CirD g;                       /* the matrix of the parts that stay the same (size x size, row major) */
    CirC pattern;                 /* the entries that can be other than zero (also where only the non-linear parts write) */
    CirD m;                       /* the factorized matrix (rows in pivot order) */
    CirI perm, rowOf;             /* perm[i]: the row of g that is row i of m; rowOf: the other way round */
    CirI entries;                 /* the indices of m that the elimination touches (to load the numbers of a new iteration) */
    CirD rhsBase, xOld;
    CIR_VEC(CirNonlinear) nonlinear;
    CIR_VEC(CirGate) gates;
    CIR_VEC(CirChip) chips;
    int64_t iterations, unconverged;
    int maxIterations;
    /* a step whose Newton's method does not converge is done again in steps of half the time step, down to dt / 2^maxHalvings */
    int maxHalvings;
    int64_t retried;
    int retrying;
    CirSnapshot stepStart, subStart;
    CirD rhsBegun, gFull;
    CirI group;                   /* nodes joined by conductances and voltage sources (0: ground, node i: i + 1) */
    /* the elimination as a list: factor (index of m[r][k], index of m[k][k]) and update (m[r][c] -= m[r][k] * m[k][c]) */
    CirI factorOps, updateOps;
    /* the triangles for solving: per row the entries left (L) and right (U) of the diagonal, as (column, index of m) */
    CirI lStart, lCol, lIdx, uStart, uCol, uIdx, diag;
    CirD rhs, x, y;
    /* the solution of the step before (x is that of the last step): the first iteration of a step starts from x + (x - xPrev) */
    CirD xPrev;
    /* a step may end after one iteration when the parts confirm their linearization (cir_linearizationHolds); how many did */
    int oneIteration;
    int64_t shortcuts;
    CirD limSave;
    /* the matrix of the parts that stay the same in the layout of m (rows in pivot order), made again when g or the order changes (gDirty) */
    CirD mLinear;
    int gDirty;
    CirReactives capacitors, inductors;
    CirCoupleds coupled;
    CIR_VEC(CirLine) lines;
    CirSources sources;
    CIR_VEC(CirProbe) probes;
    CIR_VEC(CirTap) taps;
    CIR_VEC(CirFeed) feeds;
    int compiled;
    /* (scratch of the steps: the matrix for choosing the pivots again, what parts in fire added during a retry) */
    CirD scratch, extra;
    CirC nz;
} CirPlan;

static int cir_find(CirPlan* p, int i) { int* group = p->group.d; while (group[i] != i) { group[i] = group[group[i]]; i = group[i]; } return i; }
static void cir_join(CirPlan* p, int i, int j) { int a = cir_find(p, i + 1), b = cir_find(p, j + 1); if (a != b) p->group.d[a] = b; }
static void cir_add(CirPlan* p, int r, int c, double v) {
    if (r >= 0 && c >= 0) { p->g.d[(size_t)r * p->size + c] += v; p->pattern.d[(size_t)r * p->size + c] = 1; p->gDirty = 1; }
}
static void cir_mark(CirPlan* p, int r, int c) { if (r >= 0 && c >= 0) p->pattern.d[(size_t)r * p->size + c] = 1; }
static void cir_conductance(CirPlan* p, int i, int j, double v) {
    cir_add(p, i, i, v); cir_add(p, j, j, v); cir_add(p, i, j, -v); cir_add(p, j, i, -v);
    if (v != 0) cir_join(p, i, j);
}
static double cir_node(const CirPlan* p, int i) { return i < 0 ? 0.0 : p->x.d[i]; }

static void cir_planFree(CirPlan* p) {
    size_t i;
    if (p == NULL) return;
    CIR_FREE(p->g); CIR_FREE(p->pattern); CIR_FREE(p->m); CIR_FREE(p->perm); CIR_FREE(p->rowOf); CIR_FREE(p->entries); CIR_FREE(p->rhsBase); CIR_FREE(p->xOld);
    for (i = 0; i < p->nonlinear.n; i++) { cir_formulaFree(p->nonlinear.d[i].f1); cir_formulaFree(p->nonlinear.d[i].f2); }
    CIR_FREE(p->nonlinear);
    for (i = 0; i < p->gates.n; i++) CIR_FREE(p->gates.d[i].inputs);
    CIR_FREE(p->gates);
    for (i = 0; i < p->chips.n; i++) cir_chipFree(&p->chips.d[i]);
    CIR_FREE(p->chips);
    cir_snapshotFree(&p->stepStart); cir_snapshotFree(&p->subStart);
    CIR_FREE(p->rhsBegun); CIR_FREE(p->gFull); CIR_FREE(p->group); CIR_FREE(p->factorOps); CIR_FREE(p->updateOps);
    CIR_FREE(p->lStart); CIR_FREE(p->lCol); CIR_FREE(p->lIdx); CIR_FREE(p->uStart); CIR_FREE(p->uCol); CIR_FREE(p->uIdx); CIR_FREE(p->diag);
    CIR_FREE(p->rhs); CIR_FREE(p->x); CIR_FREE(p->y); CIR_FREE(p->xPrev); CIR_FREE(p->limSave); CIR_FREE(p->mLinear);
    CIR_FREE(p->capacitors); CIR_FREE(p->inductors); CIR_FREE(p->coupled);
    for (i = 0; i < p->lines.n; i++) { free(p->lines.d[i].h1); free(p->lines.d[i].h2); }
    CIR_FREE(p->lines);
    CIR_FREE(p->sources); CIR_FREE(p->probes); CIR_FREE(p->taps); CIR_FREE(p->feeds);
    CIR_FREE(p->scratch); CIR_FREE(p->extra); CIR_FREE(p->nz);
    free(p);
}

/* ---- the tables: slot 0 is never used (handle 0 is "none"); handles are not used again ---- */
static CIR_VEC(CirPlan*) g_cirPlans;
static CIR_VEC(CirRecorder*) g_cirRecorders;
static CIR_VEC(CirStream*) g_cirStreams;

static void cir_stopRunner(CirPlan* p);

/* The program has ended (the library of the VM stays loaded for the next one): drops the plans, recordings and streams. */
static void fire_cir_reset(void) {
    size_t i, n;
    cir_lock();
    n = g_cirPlans.n;
    cir_unlock();
    for (i = 1; i < n; i++) {
        CirPlan* p;
        cir_lock(); p = g_cirPlans.d[i]; cir_unlock();
        cir_stopRunner(p);
    }
    cir_lock();
    for (i = 1; i < g_cirPlans.n; i++) { cir_planFree(g_cirPlans.d[i]); g_cirPlans.d[i] = NULL; }
    for (i = 1; i < g_cirRecorders.n; i++) { cir_recFree(g_cirRecorders.d[i]); g_cirRecorders.d[i] = NULL; }
    for (i = 1; i < g_cirStreams.n; i++) { cir_streamFree(g_cirStreams.d[i]); g_cirStreams.d[i] = NULL; }
    cir_unlock();
}

static CirStream* cir_stream(int64_t i) { return i > 0 && (size_t)i < g_cirStreams.n ? g_cirStreams.d[i] : NULL; }
static CirPlan* cir_plan(Value h) { int64_t i = h.i; return i > 0 && (size_t)i < g_cirPlans.n ? g_cirPlans.d[i] : NULL; }
static CirRecorder* cir_recorder(Value h) { int64_t i = h.i; return i > 0 && (size_t)i < g_cirRecorders.n ? g_cirRecorders.d[i] : NULL; }

/* ---- streams and sound devices ---- */
/* Plays what a stream holds to its device: as much as the device takes now (16 bit samples; nothing waits). */
static void cir_pumpOut(CirStream* st) {
    CirDevice* d = st->playTo;
    double k;
    int round;
    if (!d->primed) {
        if ((double)st->count < d->latency * st->rate) return;
        d->primed = 1;
    }
    k = d->fullScale != 0 ? 32767.0 / d->fullScale : 32767.0;
    for (round = 0; round < 64; round++) {
        int frameBytes, taken = 0;
        if (d->pending.n == 0) {
            size_t n = cir_minZ(st->count, 1024), i;
            if (n == 0) return;
            CIR_RESERVE(d->pending, n * 2 * (size_t)d->channels);
            d->pending.n = n * 2 * (size_t)d->channels;
            for (i = 0; i < n; i++) {
                float v = 0;
                double y;
                int s16, c;
                cir_streamPop(st, &v);
                y = v * k;
                if (y > 32767) y = 32767; else if (y < -32768) y = -32768;
                s16 = (int)lround(y);
                for (c = 0; c < d->channels; c++) {
                    d->pending.d[(i * (size_t)d->channels + (size_t)c) * 2] = (uint8_t)(s16 & 255);
                    d->pending.d[(i * (size_t)d->channels + (size_t)c) * 2 + 1] = (uint8_t)((s16 >> 8) & 255);
                }
            }
        }
        frameBytes = 2 * d->channels;
        if (d->sim) {
            size_t i;
            taken = (int)d->pending.n;
            for (i = 0; i + 1 < d->pending.n && d->simData.n < ((size_t)16 << 20); i += (size_t)frameBytes)
                CIR_PUSH(d->simData, (float)((int16_t)(uint16_t)(d->pending.d[i] | (d->pending.d[i + 1] << 8)) / k));
        }
#ifdef CIR_AUDIO
        else if (d->out) {
            FireAudioStatus status;
            if (!fire_audio_write(d->out, d->pending.d, (int)d->pending.n, &taken, &status)) taken = 0;
        }
#endif
        if (taken <= 0) return;
        d->frames += taken / frameBytes;
        memmove(d->pending.d, d->pending.d + taken, d->pending.n - (size_t)taken);
        d->pending.n -= (size_t)taken;
        if (d->pending.n != 0) return;   /* the device is full */
    }
}

/* Takes what the device recorded into a stream (mixed to one channel, a full-scale sample is fullScale volts). */
static void cir_pumpIn(CirStream* st) {
    CirDevice* d = st->recordFrom;
    const int frameBytes = 2 * d->channels;
    const double k = d->fullScale / 32768.0;
    uint8_t buffer[4096];
    int round;
    for (round = 0; round < 64; round++) {
        /* no more than fits into the stream (a device without a clock of its own, as ALSA's "null", would deliver without end) */
        size_t room = (st->ringSize - st->count) * (size_t)frameBytes;
        size_t want = cir_minZ(room, sizeof buffer - sizeof buffer % (size_t)frameBytes);
        int got = 0, f;
        if (want == 0) return;
        if (d->sim) {
            size_t left = d->simInput.n - d->simInputAt;
            got = (int)cir_minZ(left - left % (size_t)frameBytes, want);
            if (got > 0) memcpy(buffer, d->simInput.d + d->simInputAt, (size_t)got);
            d->simInputAt += (size_t)got;
            if (d->simInputAt == d->simInput.n) { d->simInput.n = 0; d->simInputAt = 0; }
        }
#ifdef CIR_AUDIO
        else if (d->in) {
            FireAudioStatus status;
            if (!fire_audio_read(d->in, buffer, (int)want, &got, &status)) got = 0;
        }
#endif
        if (got <= 0) return;
        for (f = 0; f + frameBytes <= got; f += frameBytes) {
            double sum = 0;
            int c;
            for (c = 0; c < d->channels; c++) sum += (int16_t)(uint16_t)(buffer[f + 2 * c] | (buffer[f + 2 * c + 1] << 8));
            cir_streamPush(st, (float)(sum / d->channels * k));
        }
        d->frames += got / frameBytes;
    }
}

static void cir_pumpStream(CirStream* st) {
    if (!st) return;
    if (st->playTo) cir_pumpOut(st);
    if (st->recordFrom) cir_pumpIn(st);
}

/* How far the streams of a plan that go to or come from a sound device (not the simulated one) are off the latency they keep, in seconds: above zero the circuit
 * is behind the clock of the device (an output runs dry, an input fills up), below zero ahead. 0 without such a stream. */
static double cir_deviceLag(CirStream* st, int* any) {
    double lag = 0;
    if (!st) return 0;
#ifdef CIR_AUDIO
    if (st->playTo && st->playTo->out && st->playTo->primed) {
        CirDevice* d = st->playTo;
        double fill = ((double)fire_audio_queued(d->out) / (2.0 * d->channels) + (double)d->pending.n / (2.0 * d->channels)) / d->rate + (double)st->count / st->rate;
        lag += d->latency - fill;
        *any = 1;
    }
    if (st->recordFrom && st->recordFrom->in) {
        lag += (double)st->count / st->rate - st->recordFrom->latency;
        *any = 1;
    }
#else
    (void)any;
#endif
    return lag;
}

/* ---- plans ---- */

static Value cir_New(Value nodes, Value branches, Value dt) {
    CirPlan* p;
    int i;
    if (nodes.i < 0 || branches.i < 0 || cir_D(dt) <= 0) return Int(0);
    p = (CirPlan*)calloc(1, sizeof(CirPlan));
    if (p == NULL) { fputs("circuits: out of memory\n", stderr); abort(); }
    p->dt = 1e-5;
    p->maxIterations = 100;
    p->maxHalvings = 6;
    p->oneIteration = 1;
    p->gDirty = 1;
    p->nodes = (int)nodes.i;
    p->size = (int)(nodes.i + branches.i);
    p->dt = cir_D(dt);
    cir_dAssign(&p->g, (size_t)p->size * p->size, 0.0);
    cir_cAssign(&p->pattern, (size_t)p->size * p->size, 0);
    cir_iAssign(&p->group, (size_t)p->nodes + 1, 0);
    for (i = 0; i <= p->nodes; i++) p->group.d[i] = i;
    cir_dAssign(&p->rhs, (size_t)p->size, 0.0);
    cir_dAssign(&p->x, (size_t)p->size, 0.0);
    cir_dAssign(&p->y, (size_t)p->size, 0.0);
    if (g_cirPlans.n == 0) CIR_PUSH(g_cirPlans, NULL);
    CIR_PUSH(g_cirPlans, p);
    return Int((int64_t)g_cirPlans.n - 1);
}
static Value fire_cir_New(Value nodes, Value branches, Value dt) { CIR_LOCKED(cir_New(nodes, branches, dt)); }

static Value fire_cir_Free(Value h) {
    CirPlan* p;
    int64_t i = h.i;
    cir_lock(); p = cir_plan(h); cir_unlock();
    cir_stopRunner(p);   /* (outside the lock: the thread finishes its steps under it) */
    cir_lock();
    if (i > 0 && (size_t)i < g_cirPlans.n) { cir_planFree(g_cirPlans.d[i]); g_cirPlans.d[i] = NULL; }
    cir_unlock();
    return Undef();
}

static Value cir_Conductance(Value h, Value i, Value j, Value v) {
    CirPlan* p = cir_plan(h);
    if (p) cir_conductance(p, (int)i.i, (int)j.i, cir_D(v));
    return Undef();
}
static Value fire_cir_Conductance(Value h, Value i, Value j, Value v) { CIR_LOCKED(cir_Conductance(h, i, j, v)); }

/* The entries of a voltage source between the nodes plus and minus with the branch row k. */
static Value cir_VoltageBranch(Value h, Value k, Value plus, Value minus) {
    CirPlan* p = cir_plan(h);
    int r, i, j;
    if (!p) return Undef();
    r = (int)k.i; i = (int)plus.i; j = (int)minus.i;
    cir_add(p, i, r, 1.0); cir_add(p, j, r, -1.0); cir_add(p, r, i, 1.0); cir_add(p, r, j, -1.0);
    cir_join(p, i, j);
    return Undef();
}
static Value fire_cir_VoltageBranch(Value h, Value k, Value plus, Value minus) { CIR_LOCKED(cir_VoltageBranch(h, k, plus, minus)); }

/* A capacitor (kind 0, value in farads, state: its voltage) or an inductor (kind 1, henries, state: its current); returns its index. */
static Value cir_AddReactive(Value h, Value kind, Value a, Value b, Value value, Value vPrev, Value iPrev) {
    CirPlan* p = cir_plan(h);
    CirReactive r;
    CirReactives* list;
    if (!p) return Int(-1);
    memset(&r, 0, sizeof r);
    r.a = (int)a.i; r.b = (int)b.i; r.vPrev = cir_D(vPrev); r.iPrev = cir_D(iPrev);
    r.geq = kind.i == 0 ? 2.0 * cir_D(value) / p->dt : p->dt / (2.0 * cir_D(value));
    cir_conductance(p, r.a, r.b, r.geq);
    list = kind.i == 0 ? &p->capacitors : &p->inductors;
    CIR_PUSH(*list, r);
    return Int((int64_t)list->n - 1);
}
static Value fire_cir_AddReactive(Value h, Value kind, Value a, Value b, Value value, Value vPrev, Value iPrev) {
    CIR_LOCKED(cir_AddReactive(h, kind, a, b, value, vPrev, iPrev));
}

/* Two coupled inductors (a transformer, see CirCoupled): l1, l2 in henries, k the coupling (below 1), with their state at the start; returns its index. */
static Value cir_AddCoupled(Value h, Value a1, Value b1, Value a2, Value b2, Value l1, Value l2, Value k, Value i1, Value i2, Value v1, Value v2) {
    CirPlan* p = cir_plan(h);
    double L1, L2, kk, m, det, f;
    CirCoupled c;
    if (!p) return Int(-1);
    L1 = cir_D(l1); L2 = cir_D(l2); kk = cir_D(k);
    if (L1 <= 0 || L2 <= 0) { cir_error("A transformer needs inductances above zero."); return Int(-1); }
    if (kk < 0) kk = 0;
    if (kk > 0.999999) kk = 0.999999;   /* (k = 1 has no inverse) */
    m = kk * sqrt(L1 * L2); det = L1 * L2 - m * m; f = p->dt / 2.0 / det;
    c.a1 = (int)a1.i; c.b1 = (int)b1.i; c.a2 = (int)a2.i; c.b2 = (int)b2.i;
    c.g11 = f * L2; c.g12 = -f * m; c.g22 = f * L1;
    c.v1Prev = cir_D(v1); c.v2Prev = cir_D(v2); c.i1Prev = cir_D(i1); c.i2Prev = cir_D(i2);
    c.h1 = 0; c.h2 = 0;
    cir_conductance(p, c.a1, c.b1, c.g11);
    cir_conductance(p, c.a2, c.b2, c.g22);
    /* the current of each winding depends on the voltage of the other */
    cir_add(p, c.a1, c.a2, c.g12); cir_add(p, c.a1, c.b2, -c.g12); cir_add(p, c.b1, c.a2, -c.g12); cir_add(p, c.b1, c.b2, c.g12);
    cir_add(p, c.a2, c.a1, c.g12); cir_add(p, c.a2, c.b1, -c.g12); cir_add(p, c.b2, c.a1, -c.g12); cir_add(p, c.b2, c.b1, c.g12);
    CIR_PUSH(p->coupled, c);
    return Int((int64_t)p->coupled.n - 1);
}
static Value fire_cir_AddCoupled(Value h, Value a1, Value b1, Value a2, Value b2, Value l1, Value l2, Value k, Value i1, Value i2, Value v1, Value v2) {
    CIR_LOCKED(cir_AddCoupled(h, a1, b1, a2, b2, l1, l2, k, i1, i2, v1, v2));
}

/* What a logic part has beyond AddGate: the lower threshold of a Schmitt trigger, the falling edge of a JK flip-flop. */
static Value cir_GateExtra(Value h, Value index, Value low, Value falling) {
    CirPlan* p = cir_plan(h);
    CirGate* g;
    if (!p || index.i < 0 || (size_t)index.i >= p->gates.n) return Bool(false);
    g = &p->gates.d[index.i];
    g->low = cir_D(low);
    g->falling = falling.i != 0;
    return Bool(true);
}
static Value fire_cir_GateExtra(Value h, Value index, Value low, Value falling) { CIR_LOCKED(cir_GateExtra(h, index, low, falling)); }

/* A source: branch >= 0 for a voltage source (its entries come with VoltageBranch), -1 for a current source from a through it to b; wave 0 DC, 1 sine, 2 square. */
static Value cir_AddSource(Value h, Value a, Value b, Value branch, Value wave, Value p0, Value p1, Value p2, Value p3) {
    CirPlan* p = cir_plan(h);
    if (!p) return Int(-1);
    CIR_PUSH(p->sources, cir_source((int)a.i, (int)b.i, (int)branch.i, (int)wave.i, cir_D(p0), cir_D(p1), cir_D(p2), cir_D(p3)));
    return Int((int64_t)p->sources.n - 1);
}
static Value fire_cir_AddSource(Value h, Value a, Value b, Value branch, Value wave, Value p0, Value p1, Value p2, Value p3) {
    CIR_LOCKED(cir_AddSource(h, a, b, branch, wave, p0, p1, p2, p3));
}

/* New values for a source (Level, Amplitude, ...): no new plan needed. */
static Value cir_SetSource(Value h, Value index, Value p0, Value p1, Value p2, Value p3) {
    CirPlan* p = cir_plan(h);
    CirSource* s;
    if (!p || index.i < 0 || (size_t)index.i >= p->sources.n) return Bool(false);
    s = &p->sources.d[index.i];
    s->p0 = cir_D(p0); s->p1 = cir_D(p1); s->p2 = cir_D(p2); s->p3 = cir_D(p3);
    return Bool(true);
}
static Value fire_cir_SetSource(Value h, Value index, Value p0, Value p1, Value p2, Value p3) { CIR_LOCKED(cir_SetSource(h, index, p0, p1, p2, p3)); }

/* A part that is not linear (CIR_NL_*) on the nodes n0, n1, n2 with its numbers; returns its index. */
static Value cir_AddNonlinear(Value h, Value kind, Value n0, Value n1, Value n2, Value p0, Value p1, Value p2, Value p3) {
    CirPlan* p = cir_plan(h);
    CirNonlinear e;
    int two, reads, a, b;
    if (!p) return Int(-1);
    cir_nlInit(&e);
    e.kind = (int)kind.i;
    two = kind.i == CIR_NL_DIODE || cir_isVarRes((int)kind.i);
    reads = kind.i == CIR_NL_SCR || kind.i == CIR_NL_TRIAC;   /* two terminals, and the gate it only reads */
    e.node[0] = (int)n0.i; e.node[1] = (int)n1.i; e.node[2] = two && !reads ? -1 : (int)n2.i;
    e.terminals = two ? 2 : 3;
    if (kind.i == CIR_NL_OPAMP && (cir_D(p0) <= 0 || cir_D(p2) <= cir_D(p1) || cir_D(p3) <= 0)) {
        cir_error("An op amp needs a gain, rails and an output resistance above zero.");
        return Int(-1);
    }
    e.p[0] = cir_D(p0); e.p[1] = cir_D(p1); e.p[2] = cir_D(p2); e.p[3] = cir_D(p3);
    if (e.kind == CIR_NL_NMOS || e.kind == CIR_NL_PMOS) e.lim[0] = 0;
    if (e.kind == CIR_NL_LAMP) { e.st[0] = 300; e.st[1] = cir_lampResistance(e.p[0], e.p[1], 300); }
    if (e.kind == CIR_NL_MEMRISTOR) e.st[1] = e.p[1];
    for (a = 0; a < e.terminals; a++)
        for (b = 0; b < e.terminals; b++) cir_mark(p, e.node[a], e.node[b]);
    /* (the junctions join their nodes for the tiny conductance to ground: through a junction a node is not hanging in the air; the output of an op amp is tied to
     * ground through its output stage, its inputs are not) */
    if (e.kind == CIR_NL_OPAMP) cir_join(p, e.node[2], -1);
    else if (e.kind == CIR_NL_ANALOG_SWITCH) cir_join(p, e.node[0], e.node[1]);
    else for (a = 1; a < e.terminals; a++) cir_join(p, e.node[0], e.node[a]);
    CIR_PUSH(p->nonlinear, e);
    return Int((int64_t)p->nonlinear.n - 1);
}
static Value fire_cir_AddNonlinear(Value h, Value kind, Value n0, Value n1, Value n2, Value p0, Value p1, Value p2, Value p3) {
    CIR_LOCKED(cir_AddNonlinear(h, kind, n0, n1, n2, p0, p1, p2, p3));
}

/* a parameter of a tube by its name (any case), else `fallback` */
static double cir_tubeParam(const CirParams* params, const char* name, double fallback) {
    size_t k;
    for (k = 0; k < params->n; k++) {
        const char* a = params->d[k].name;
        const char* b = name;
        while (*a && *b && (char)tolower((unsigned char)*a) == *b) { a++; b++; }
        if (*a == 0 && *b == 0) return params->d[k].value;
    }
    return fallback;
}
static void cir_tubeCap(CirNonlinear* e, int a, int b, double c) {
    CirJunction* j;
    if (c <= 0) return;
    j = &e->junction[e->junctions++];
    j->a = a; j->b = b; j->cj0 = c; j->vj = 1; j->m = 0; j->tt = 0;
}

/* A vacuum tube (VACUUM_DIODE, TRIODE, PENTODE) on its nodes (diode: plate, cathode; triode: plate, grid, cathode; pentode: plate, screen, grid, cathode - unused
 * ones -2) with its parameters as text ("mu=100 ex=1.4 kg1=1060 ..."): k ex for a diode; mu ex kg1 kp kvb rgi for a triode; mu ex kg1 kg2 kp kvb rgi for a pentode;
 * and the capacitances between the electrodes (diode cpk; triode ccg cgp ccp; pentode cin cgp cout). Returns its index, -1 with LastError. */
static Value cir_AddTube(Value h, Value kind, Value n0, Value n1, Value n2, Value n3, Value parameters) {
    CirPlan* p = cir_plan(h);
    CirParams params;
    char* error = NULL;
    char* text;
    size_t len;
    CirNonlinear e;
    int nodes[4], count, a, b;
    if (!p) return Int(-1);
    memset(&params, 0, sizeof params);
    text = cir_utf8(parameters, &len);
    if (!cir_parseParameters(text, len, &params, &error)) { free(text); cir_paramsFree(&params); cir_setErrorOwned(error); return Int(-1); }
    free(text);
    cir_nlInit(&e);
    e.kind = (int)kind.i;
    nodes[0] = (int)n0.i; nodes[1] = (int)n1.i; nodes[2] = (int)n2.i; nodes[3] = (int)n3.i;
    if (e.kind == CIR_NL_VACUUM_DIODE) {
        e.terminals = 2;
        e.p[0] = cir_tubeParam(&params, "k", 0.001); e.p[1] = cir_tubeParam(&params, "ex", 1.5);
        cir_tubeCap(&e, 0, 1, cir_tubeParam(&params, "cpk", 0));
    } else if (e.kind == CIR_NL_TRIODE) {
        e.terminals = 3;
        e.p[0] = cir_tubeParam(&params, "mu", 100); e.p[1] = cir_tubeParam(&params, "ex", 1.4); e.p[2] = cir_tubeParam(&params, "kg1", 1060);
        e.p[3] = cir_tubeParam(&params, "kp", 600); e.p[4] = cir_tubeParam(&params, "kvb", 300); e.p[5] = cir_tubeParam(&params, "rgi", 2000);
        cir_tubeCap(&e, 1, 2, cir_tubeParam(&params, "ccg", 0)); cir_tubeCap(&e, 1, 0, cir_tubeParam(&params, "cgp", 0)); cir_tubeCap(&e, 0, 2, cir_tubeParam(&params, "ccp", 0));
    } else if (e.kind == CIR_NL_PENTODE) {
        e.terminals = 4;
        e.p[0] = cir_tubeParam(&params, "mu", 11); e.p[1] = cir_tubeParam(&params, "ex", 1.35); e.p[2] = cir_tubeParam(&params, "kg1", 650);
        e.p[3] = cir_tubeParam(&params, "kg2", 4200); e.p[4] = cir_tubeParam(&params, "kp", 60); e.p[5] = cir_tubeParam(&params, "kvb", 24);
        e.p[6] = cir_tubeParam(&params, "rgi", 1000);
        cir_tubeCap(&e, 2, 3, cir_tubeParam(&params, "cin", 0)); cir_tubeCap(&e, 2, 0, cir_tubeParam(&params, "cgp", 0)); cir_tubeCap(&e, 0, 3, cir_tubeParam(&params, "cout", 0));
    } else { cir_paramsFree(&params); cir_error("Not a kind of tube."); return Int(-1); }
    cir_paramsFree(&params);
    count = e.kind == CIR_NL_VACUUM_DIODE ? 2 : e.kind == CIR_NL_TRIODE ? 6 : 7;
    for (a = 0; a < count; a++)
        if (!(e.p[a] > 0)) { cir_error("The parameters of a tube must be above zero."); return Int(-1); }
    for (a = 0; a < e.terminals; a++) e.node[a] = nodes[a];
    for (a = e.terminals; a < 4; a++) e.node[a] = -1;
    for (a = 0; a < e.terminals; a++)
        for (b = 0; b < e.terminals; b++) cir_mark(p, e.node[a], e.node[b]);
    for (a = 1; a < e.terminals; a++) cir_join(p, e.node[0], e.node[a]);
    CIR_PUSH(p->nonlinear, e);
    return Int((int64_t)p->nonlinear.n - 1);
}
static Value fire_cir_AddTube(Value h, Value kind, Value n0, Value n1, Value n2, Value n3, Value parameters) {
    CIR_LOCKED(cir_AddTube(h, kind, n0, n1, n2, n3, parameters));
}

/* A logic part (CIR_G_*) with its inputs (an array of nodes), its outputs, the high level, the threshold and the output resistance; returns its index. */
static Value cir_AddGate(Value h, Value kind, Value inputs, Value out, Value outNot, Value high, Value threshold, Value rout) {
    CirPlan* p = cir_plan(h);
    CirGate gate;
    Arr* a;
    uint32_t i;
    if (!p || inputs.kind != K_Array || cir_D(rout) <= 0) return Int(-1);
    memset(&gate, 0, sizeof gate);
    gate.kind = (int)kind.i;
    a = arrOf(inputs);
    for (i = 0; i < a->length; i++) CIR_PUSH(gate.inputs, (int)arrItems(a)[i].i);
    gate.out = (int)out.i;
    gate.outNot = (int)outNot.i;
    gate.high = cir_D(high);
    gate.threshold = cir_D(threshold);
    gate.g = 1.0 / cir_D(rout);
    cir_conductance(p, gate.out, -1, gate.g);
    if (gate.outNot >= 0) cir_conductance(p, gate.outNot, -1, gate.g);
    CIR_PUSH(p->gates, gate);
    return Int((int64_t)p->gates.n - 1);
}
static Value fire_cir_AddGate(Value h, Value kind, Value inputs, Value out, Value outNot, Value high, Value threshold, Value rout) {
    CIR_LOCKED(cir_AddGate(h, kind, inputs, out, outNot, high, threshold, rout));
}

/* A chip (see CirChip): its kind, the nodes of its pins in Falstad's order and how each is used (0 input, 1 output of a level, 2 output of a current), the high
 * level, the threshold of the inputs, the resistance of the outputs, bits, flags and two parameters; returns its index. */
static Value cir_AddChip(Value h, Value kind, Value nodes, Value modes, Value high, Value threshold, Value rout, Value bits, Value flags, Value p0, Value p1) {
    CirPlan* p = cir_plan(h);
    Arr* a;
    Arr* m;
    CirChip c;
    uint32_t i;
    if (!p || nodes.kind != K_Array || modes.kind != K_Array || cir_D(rout) <= 0) return Int(-1);
    a = arrOf(nodes);
    m = arrOf(modes);
    if (a->length != m->length) return Int(-1);
    cir_chipInit(&c);
    c.kind = (int)kind.i;
    c.bits = (int)bits.i;
    c.flags = (int)flags.i;
    c.high = cir_D(high);
    c.threshold = cir_D(threshold);
    c.g = 1.0 / cir_D(rout);
    c.p[0] = cir_D(p0);
    c.p[1] = cir_D(p1);
    for (i = 0; i < a->length; i++) {
        CIR_PUSH(c.node, (int)arrItems(a)[i].i);
        CIR_PUSH(c.mode, (char)arrItems(m)[i].i);
        if (c.mode.d[c.mode.n - 1] == CIR_PIN_OUT) cir_conductance(p, c.node.d[c.node.n - 1], -1, c.g);
    }
    cir_cAssign(&c.val, c.node.n, 0);
    cir_dAssign(&c.level, c.node.n, 0.0);
    CIR_PUSH(p->chips, c);
    return Int((int64_t)p->chips.n - 1);
}
static Value fire_cir_AddChip(Value h, Value kind, Value nodes, Value modes, Value high, Value threshold, Value rout, Value bits, Value flags, Value p0, Value p1) {
    CIR_LOCKED(cir_AddChip(h, kind, nodes, modes, high, threshold, rout, bits, flags, p0, p1));
}

/* Reads a chip: what 0 the logic value of pin k, 1 the state value k, 2 the number of state values, 3 the level (volts or amperes) pin k drives, 4 the last clock,
 * 5 the last load. */
static Value cir_ChipGet(Value h, Value index, Value what, Value k) {
    CirPlan* p = cir_plan(h);
    const CirChip* c;
    size_t i;
    if (!p || index.i < 0 || (size_t)index.i >= p->chips.n) return Float(0);
    c = &p->chips.d[index.i];
    i = (size_t)k.i;
    switch (what.i) {
        case 0: return Float(i < c->val.n && c->val.d[i] ? 1 : 0);
        case 1: return Float(i < c->st.n ? (Real)c->st.d[i] : 0);
        case 2: return Float((Real)c->st.n);
        case 3: return Float(i < c->level.n ? (Real)c->level.d[i] : 0);
        case 4: return Float(c->lastClock ? 1 : 0);
        case 5: return Float(c->lastLoad ? 1 : 0);
        default: return Float(0);
    }
}
static Value fire_cir_ChipGet(Value h, Value index, Value what, Value k) { CIR_LOCKED(cir_ChipGet(h, index, what, k)); }

/* Sets what ChipGet reads (0 the logic value of pin k, 1 the state value k, 4 the last clock, 5 the last load): the state a chip had before the circuit was
 * compiled again. */
static Value cir_ChipSet(Value h, Value index, Value what, Value k, Value value) {
    CirPlan* p = cir_plan(h);
    CirChip* c;
    size_t i;
    double v;
    if (!p || index.i < 0 || (size_t)index.i >= p->chips.n) return Bool(false);
    c = &p->chips.d[index.i];
    i = (size_t)k.i;
    v = cir_D(value);
    if (what.i == 0 && i < c->val.n) c->val.d[i] = v != 0 ? 1 : 0;
    else if (what.i == 1) {
        if (i >= c->st.n) { size_t old = c->st.n, q; CIR_RESERVE(c->st, i + 1); for (q = old; q < i + 1; q++) c->st.d[q] = 0.0; c->st.n = i + 1; }
        c->st.d[i] = v;
    }
    else if (what.i == 4) c->lastClock = v != 0;
    else if (what.i == 5) c->lastLoad = v != 0;
    else return Bool(false);
    return Bool(true);
}
static Value fire_cir_ChipSet(Value h, Value index, Value what, Value k, Value value) { CIR_LOCKED(cir_ChipSet(h, index, what, k, value)); }

/* The output of a logic part (true: high). */
static Value cir_GateState(Value h, Value index) {
    CirPlan* p = cir_plan(h);
    if (!p || index.i < 0 || (size_t)index.i >= p->gates.n) return Bool(false);
    return Bool(p->gates.d[index.i].state != 0);
}
static Value fire_cir_GateState(Value h, Value index) { CIR_LOCKED(cir_GateState(h, index)); }

static void cir_capsAdd(CirNonlinear* e, int ta, int tb, double cj, double transit, double mm, double vj) {
    CirJunction* j;
    if (cj <= 0 && transit <= 0) return;
    j = &e->junction[e->junctions++];
    j->a = ta; j->b = tb; j->cj0 = cj; j->vj = vj > 0 ? vj : 0.7; j->m = mm; j->tt = transit;
}

/* The capacitances of a part that is not linear: cjA for the diode, the base-emitter junction or gate-source; cjB for base-collector or gate-drain; vj and m of the
 * depletion (m = 0: linear, the MOSFETs); tt the transit time of the diode or the forward one of the bipolar transistor, tr the reverse one (the diffusion charges;
 * tr makes the storage time of a saturated transistor). */
static Value cir_SetCaps(Value h, Value index, Value cjA, Value cjB, Value vj, Value m, Value tt, Value tr) {
    CirPlan* p = cir_plan(h);
    CirNonlinear* e;
    double a, b, t;
    if (!p || index.i < 0 || (size_t)index.i >= p->nonlinear.n) return Bool(false);
    e = &p->nonlinear.d[index.i];
    a = cir_D(cjA); b = cir_D(cjB); t = cir_D(tt);
    e->junctions = 0;
    if (e->kind == CIR_NL_DIODE) cir_capsAdd(e, 0, 1, a, t, cir_D(m), cir_D(vj));
    else if (e->kind == CIR_NL_NPN || e->kind == CIR_NL_PNP) { cir_capsAdd(e, 1, 2, a, t, cir_D(m), cir_D(vj)); cir_capsAdd(e, 1, 0, b, cir_D(tr), cir_D(m), cir_D(vj)); }
    else if (e->kind == CIR_NL_NMOS || e->kind == CIR_NL_PMOS) { cir_capsAdd(e, 1, 2, a, 0, 0, cir_D(vj)); cir_capsAdd(e, 1, 0, b, 0, 0, cir_D(vj)); }
    return Bool(true);
}
static Value fire_cir_SetCaps(Value h, Value index, Value cjA, Value cjB, Value vj, Value m, Value tt, Value tr) {
    CIR_LOCKED(cir_SetCaps(h, index, cjA, cjB, vj, m, tt, tr));
}

/* compiles one formula of a part; 0 with LastError */
static int cir_compileFormula(const char* text, size_t len, const CirParams* params, int three, CirFormula** into) {
    CirFComp c;
    int ok;
    cir_fcInit(&c, text, len, params, three);
    ok = cir_fcExpression(&c);
    cir_fcSkip(&c);
    if (ok && c.pos < c.len) ok = cir_fcFail(&c, "Unexpected text");
    if (!ok) { cir_setErrorOwned(c.error); c.error = NULL; cir_fcFree(&c); return 0; }
    if (cir_stackDepth(&c.out) > 60) { cir_error("The formula is too deeply nested: '%s'", text); cir_fcFree(&c); return 0; }
    *into = (CirFormula*)malloc(sizeof(CirFormula));
    if (*into == NULL) { fputs("circuits: out of memory\n", stderr); abort(); }
    **into = c.out;
    free(c.error);
    return 1;
}

/* A part given by formulas: the current into its first terminal as a function of V (two terminals: V = V(a) - V(b)), or the currents into the first and the second
 * terminal as functions of V1 and V2 (three terminals: against the third); `parameters`: "name=value ...". Returns its index, -1 with LastError. */
static Value cir_AddFormula(Value h, Value n0, Value n1, Value n2, Value current1, Value current2, Value parameters) {
    CirPlan* p = cir_plan(h);
    CirParams params;
    char* error = NULL;
    char* text;
    size_t len;
    int three, a, b;
    CirNonlinear e;
    if (!p) return Int(-1);
    memset(&params, 0, sizeof params);
    text = cir_utf8(parameters, &len);
    if (!cir_parseParameters(text, len, &params, &error)) { free(text); cir_paramsFree(&params); cir_setErrorOwned(error); return Int(-1); }
    free(text);
    three = n2.i != -2;
    cir_nlInit(&e);
    e.kind = CIR_NL_FORMULA;
    e.terminals = three ? 3 : 2;
    e.node[0] = (int)n0.i; e.node[1] = (int)n1.i; e.node[2] = three ? (int)n2.i : -1;
    text = cir_utf8(current1, &len);
    if (!cir_compileFormula(text, len, &params, three, &e.f1)) { free(text); cir_paramsFree(&params); return Int(-1); }
    free(text);
    if (three) {
        text = cir_utf8(current2, &len);
        if (len != 0 && !cir_compileFormula(text, len, &params, three, &e.f2)) { free(text); cir_paramsFree(&params); cir_formulaFree(e.f1); return Int(-1); }
        free(text);
    }
    cir_paramsFree(&params);
    for (a = 0; a < e.terminals; a++)
        for (b = 0; b < e.terminals; b++) cir_mark(p, e.node[a], e.node[b]);
    for (a = 1; a < e.terminals; a++) cir_join(p, e.node[0], e.node[a]);
    CIR_PUSH(p->nonlinear, e);
    return Int((int64_t)p->nonlinear.n - 1);
}
static Value fire_cir_AddFormula(Value h, Value n0, Value n1, Value n2, Value current1, Value current2, Value parameters) {
    CIR_LOCKED(cir_AddFormula(h, n0, n1, n2, current1, current2, parameters));
}

/* Checks a formula without a circuit: "" when it compiles, else the message. */
static Value cir_CheckFormula(Value text, Value parameters, Value threeTerminals, OwnList* list) {
    CirParams params;
    char* error = NULL;
    size_t plen, tlen;
    char* ptext = cir_utf8(parameters, &plen);
    char* t = cir_utf8(text, &tlen);
    Value result;
    memset(&params, 0, sizeof params);
    if (cir_parseParameters(ptext, plen, &params, &error)) {
        CirFComp c;
        int ok;
        cir_fcInit(&c, t, tlen, &params, threeTerminals.i != 0);
        ok = cir_fcExpression(&c);
        cir_fcSkip(&c);
        if (ok && c.pos < tlen) cir_fcFail(&c, "Unexpected text");
        error = c.error;
        c.error = NULL;
        cir_fcFree(&c);
    }
    result = cir_bytesStr(error ? error : "", list);
    free(error); free(ptext); free(t);
    cir_paramsFree(&params);
    return result;
}
static Value fire_cir_CheckFormula(Value text, Value parameters, Value threeTerminals, OwnList* list) { CIR_LOCKED(cir_CheckFormula(text, parameters, threeTerminals, list)); }

/* The value of a formula (two terminals: at V = v1; three: at V1, V2) - for tests and plots of a model. */
static Value cir_EvalFormula(Value text, Value parameters, Value v1, Value v2) {
    CirParams params;
    char* error = NULL;
    size_t plen, tlen;
    char* ptext = cir_utf8(parameters, &plen);
    char* t = cir_utf8(text, &tlen);
    Value result = Float(0);
    memset(&params, 0, sizeof params);
    if (cir_parseParameters(ptext, plen, &params, &error)) {
        int three = strstr(t, "V1") != NULL || strstr(t, "V2") != NULL;
        CirFComp c;
        cir_fcInit(&c, t, tlen, &params, three);
        /* (a program deeper than the stack of evaluate is not evaluated) */
        if (cir_fcExpression(&c) && cir_stackDepth(&c.out) <= CIR_FSTACK) result = Float((Real)cir_evaluate(&c.out, cir_D(v1), cir_D(v2), 0).v);
        cir_fcFree(&c);
    }
    free(error); free(ptext); free(t);
    cir_paramsFree(&params);
    return result;
}
static Value fire_cir_EvalFormula(Value text, Value parameters, Value v1, Value v2) { CIR_LOCKED(cir_EvalFormula(text, parameters, v1, v2)); }

/* Sets the state of a part that follows a state (what 4 or 5: st[0], st[1]) - its state from the plan before, or a reset; what 6: the node it reads besides its
 * terminals (aux). */
static Value cir_NlSet(Value h, Value index, Value what, Value value) {
    CirPlan* p = cir_plan(h);
    CirNonlinear* e;
    if (!p || index.i < 0 || (size_t)index.i >= p->nonlinear.n || what.i < 4 || what.i > 6) return Bool(false);
    e = &p->nonlinear.d[index.i];
    if (what.i == 6) e->aux = (int)cir_D(value); else e->st[what.i - 4] = cir_D(value);
    return Bool(true);
}
static Value fire_cir_NlSet(Value h, Value index, Value what, Value value) { CIR_LOCKED(cir_NlSet(h, index, what, value)); }

/* Adds `value` to the entry (row, col) of the matrix of the parts that stay the same (-1: ground, nothing) - the controlled sources of fire use it. */
static Value cir_Entry(Value h, Value row, Value col, Value value) {
    CirPlan* p = cir_plan(h);
    if (p && row.i < p->size && col.i < p->size) cir_add(p, (int)row.i, (int)col.i, cir_D(value));
    return Undef();
}
static Value fire_cir_Entry(Value h, Value row, Value col, Value value) { CIR_LOCKED(cir_Entry(h, row, col, value)); }

/* The currents into the terminals of a part that is not linear after the last step (what: terminal 0..2; 7: terminal 3), the voltage between its first two
 * terminals (what 3 and the rest), the state of a part that follows one (what 4 and 5). */
static Value cir_NlState(Value h, Value index, Value what) {
    CirPlan* p = cir_plan(h);
    const CirNonlinear* e;
    if (!p || index.i < 0 || (size_t)index.i >= p->nonlinear.n) return Float(0);
    e = &p->nonlinear.d[index.i];
    if (what.i >= 0 && what.i < 3) return Float((Real)e->cur[what.i]);
    if (what.i == 4 || what.i == 5) return Float((Real)e->st[what.i - 4]);
    if (what.i == 7) return Float((Real)e->cur[3]);
    return Float((Real)(cir_node(p, e->node[0]) - cir_node(p, e->node[1])));
}
static Value fire_cir_NlState(Value h, Value index, Value what) { CIR_LOCKED(cir_NlState(h, index, what)); }

/* Runs the list of operations on m (the list keeps the order of the elimination: each factor is followed by the updates that use it). */
static void cir_eliminate(CirPlan* p) {
    double* m = p->m.d;
    const int* f = p->factorOps.d;
    const int* u = p->updateOps.d;
    const size_t fn = p->factorOps.n, un = p->updateOps.n;
    size_t ui = 0, fi;
    for (fi = 0; fi < fn; fi += 2) {
        const int rk = f[fi];
        double factor;
        m[rk] /= m[f[fi + 1]];
        factor = m[rk];
        while (ui < un && u[ui + 1] == rk) { m[u[ui]] -= factor * m[u[ui + 2]]; ui += 3; }
    }
}

/* The order of the pivots for the matrix `a` (rows as in g; it is changed): Gaussian elimination with partial pivoting; then the elimination in that order as a
 * list of operations on the entries of the pattern (and the ones that fill in), and the triangles for solving. */
static int cir_order(CirPlan* p, double* a) {
    const int n = p->size;
    int i, k, r, c;
    char* nz;
    CirI rowCols;
    /* (the order is taken into p->perm only when every pivot was found) */
    CirI perm;
    memset(&perm, 0, sizeof perm);
    cir_iAssign(&perm, (size_t)n, 0);
    for (i = 0; i < n; i++) perm.d[i] = i;
    for (k = 0; k < n; k++) {
        int best = k;
        double bestValue = fabs(a[(size_t)k * n + k]);
        double d;
        for (r = k + 1; r < n; r++) {
            double v = fabs(a[(size_t)r * n + k]);
            if (v > bestValue) { best = r; bestValue = v; }
        }
        if (bestValue == 0.0) { CIR_FREE(perm); return 0; }
        if (best != k) {
            int t;
            for (c = 0; c < n; c++) { double s = a[(size_t)k * n + c]; a[(size_t)k * n + c] = a[(size_t)best * n + c]; a[(size_t)best * n + c] = s; }
            t = perm.d[k]; perm.d[k] = perm.d[best]; perm.d[best] = t;
        }
        d = a[(size_t)k * n + k];
        for (r = k + 1; r < n; r++) {
            double f = a[(size_t)r * n + k];
            if (f == 0.0) continue;
            f /= d;
            a[(size_t)r * n + k] = f;
            for (c = k + 1; c < n; c++) a[(size_t)r * n + c] -= f * a[(size_t)k * n + c];
        }
    }
    CIR_FREE(p->perm);
    p->perm = perm;
    p->gDirty = 1;
    cir_iAssign(&p->rowOf, (size_t)n, 0);
    for (i = 0; i < n; i++) p->rowOf.d[p->perm.d[i]] = i;
    cir_cAssign(&p->nz, (size_t)n * n, 0);
    nz = p->nz.d;
    for (i = 0; i < n; i++)
        for (c = 0; c < n; c++) nz[(size_t)i * n + c] = p->pattern.d[(size_t)p->perm.d[i] * n + c];
    p->factorOps.n = 0;
    p->updateOps.n = 0;
    memset(&rowCols, 0, sizeof rowCols);
    for (k = 0; k < n; k++) {
        rowCols.n = 0;
        for (c = k + 1; c < n; c++) if (nz[(size_t)k * n + c]) CIR_PUSH(rowCols, c);
        for (r = k + 1; r < n; r++) {
            size_t q;
            if (!nz[(size_t)r * n + k]) continue;
            CIR_PUSH(p->factorOps, r * n + k);
            CIR_PUSH(p->factorOps, k * n + k);
            CIR_RESERVE(p->updateOps, p->updateOps.n + rowCols.n * 3);
            for (q = 0; q < rowCols.n; q++) {
                c = rowCols.d[q];
                nz[(size_t)r * n + c] = 1;
                p->updateOps.d[p->updateOps.n++] = r * n + c;
                p->updateOps.d[p->updateOps.n++] = r * n + k;
                p->updateOps.d[p->updateOps.n++] = k * n + c;
            }
        }
    }
    CIR_FREE(rowCols);
    cir_iAssign(&p->lStart, (size_t)n + 1, 0); cir_iAssign(&p->uStart, (size_t)n + 1, 0);
    p->lCol.n = 0; p->lIdx.n = 0; p->uCol.n = 0; p->uIdx.n = 0; p->entries.n = 0;
    cir_iAssign(&p->diag, (size_t)n, 0);
    for (i = 0; i < n; i++) {
        p->lStart.d[i] = (int)p->lCol.n;
        p->uStart.d[i] = (int)p->uCol.n;
        for (c = 0; c < n; c++) {
            if (!nz[(size_t)i * n + c] && c != i) continue;
            CIR_PUSH(p->entries, i * n + c);
            if (c < i) { CIR_PUSH(p->lCol, c); CIR_PUSH(p->lIdx, i * n + c); }
            else if (c > i) { CIR_PUSH(p->uCol, c); CIR_PUSH(p->uIdx, i * n + c); }
        }
        p->diag.d[i] = i * n + i;
    }
    p->lStart.d[n] = (int)p->lCol.n;
    p->uStart.d[n] = (int)p->uCol.n;
    cir_dAssign(&p->m, (size_t)n * n, 0.0);
    return 1;
}

/* Loads the numbers of the parts that stay the same into m (rows in pivot order); only the entries of the pattern. */
static void cir_load(CirPlan* p) {
    const int n = p->size;
    double* m = p->m.d;
    const double* lin;
    size_t k;
    if (p->gDirty || p->mLinear.n != p->m.n) {
        cir_dAssign(&p->mLinear, p->m.n, 0.0);
        for (k = 0; k < p->entries.n; k++) {
            int idx = p->entries.d[k];
            int i = idx / n, c = idx % n;
            p->mLinear.d[idx] = p->g.d[(size_t)p->perm.d[i] * n + c];
        }
        p->gDirty = 0;
    }
    lin = p->mLinear.d;
    for (k = 0; k < p->entries.n; k++) { int idx = p->entries.d[k]; m[idx] = lin[idx]; }
}

static int cir_pivotsOk(CirPlan* p) {
    size_t k;
    for (k = 0; k < p->diag.n; k++) {
        double v = p->m.d[p->diag.d[k]];
        if (!(fabs(v) > 1e-300) || !isfinite(v)) return 0;
    }
    return 1;
}

/* Writes the linearization of the parts that are not linear into m (via rowOf) - or, with `dense`, into a matrix with the rows of g - and into rhs. */
static void cir_stampNonlinear(CirPlan* p, double* dense) {
    const int n = p->size;
    size_t k;
    for (k = 0; k < p->nonlinear.n; k++) {
        CirNonlinear* e = &p->nonlinear.d[k];
        int a, b;
        cir_linearize(e, p->x.d);
        if (e->junctions > 0) cir_stampCharges(e, p->dt);
        for (a = 0; a < e->terminals; a++) {
            int r = e->node[a];
            double constant;
            if (r < 0) continue;
            constant = e->i0[a];
            for (b = 0; b < e->terminals; b++) {
                int c;
                constant -= e->j[a][b] * e->v0[b];
                c = e->node[b];
                if (c < 0) continue;
                if (dense) dense[(size_t)r * n + c] += e->j[a][b];
                else p->m.d[(size_t)p->rowOf.d[r] * n + c] += e->j[a][b];
            }
            p->rhs.d[r] -= constant;
        }
    }
}

/* Makes the plan: a tiny conductance for nodes without a path to ground, the order of the pivots, the list of operations, the factorization. True, or false with
 * LastError when the circuit cannot be solved. */
static Value cir_Compile(Value h, Value time) {
    CirPlan* p = cir_plan(h);
    const char* unsolvable = "The circuit cannot be solved (a loop of voltage sources, or a source without a path).";
    int n, ground, i;
    size_t k, q;
    double* a;
    if (!p) { cir_error("Invalid plan."); return Bool(false); }
    p->time = cir_D(time);
    n = p->size;
    ground = cir_find(p, 0);
    /* a node with no path to ground gets a tiny conductance to it - relative to what it has (next to a closed switch of 1 MS, 1e-12 S would vanish) */
    for (i = 0; i < p->nodes; i++)
        if (cir_find(p, i + 1) != ground) cir_add(p, i, i, cir_max(1e-12, fabs(p->g.d[(size_t)i * n + i]) * 1e-9));
    for (i = 0; i < n; i++) p->pattern.d[(size_t)i * n + i] = 1;
    /* the pivots are chosen with the parts that are not linear standing in as conductances of 1 mS between their terminals and from each terminal to ground
     * (without the ground ties, an op amp whose input is only fed by a current source would make the stand-in singular) */
    CIR_COPY(p->scratch, p->g);
    a = p->scratch.d;
    for (k = 0; k < p->nonlinear.n; k++) {
        const CirNonlinear* e = &p->nonlinear.d[k];
        int x, y;
        for (x = 0; x < e->terminals; x++)
            for (y = 0; y < e->terminals; y++) {
                int r = e->node[x], c = e->node[y];
                if (r >= 0 && c >= 0) a[(size_t)r * n + c] += x == y ? 1e-3 * e->terminals : -1e-3;
            }
    }
    if (!cir_order(p, a)) { cir_error("%s", unsolvable); return Bool(false); }
    cir_dAssign(&p->rhsBase, (size_t)n, 0.0);
    cir_dAssign(&p->xOld, (size_t)n, 0.0);
    /* the outputs of the logic parts start at their level (a counter must not see the outputs come up as edges at power-on); the voltages the circuit had before a
     * change are set after this (SetNode) */
    for (k = 0; k < p->gates.n; k++) {
        const CirGate* gate = &p->gates.d[k];
        int node = gate->state ? gate->out : gate->outNot;
        if (node >= 0) p->x.d[node] = gate->high;
    }
    for (k = 0; k < p->chips.n; k++) {
        const CirChip* c = &p->chips.d[k];
        for (q = 0; q < c->node.n; q++)
            if (c->mode.d[q] == CIR_PIN_OUT && c->val.d[q] && c->node.d[q] >= 0) p->x.d[c->node.d[q]] = c->high;
    }
    if (p->nonlinear.n == 0) {
        cir_load(p);
        cir_eliminate(p);
        if (!cir_pivotsOk(p)) { cir_error("%s", unsolvable); return Bool(false); }
    }
    p->xPrev.n = 0;
    p->compiled = 1;
    return Bool(true);
}
static Value fire_cir_Compile(Value h, Value time) { CIR_LOCKED(cir_Compile(h, time)); }

static void cir_solve(CirPlan* p) {
    const int n = p->size;
    const double* m = p->m.d;
    double* y = p->y.d;
    double* x = p->x.d;
    int i, e;
    for (i = 0; i < n; i++) {
        double s = p->rhs.d[p->perm.d[i]];
        for (e = p->lStart.d[i]; e < p->lStart.d[i + 1]; e++) s -= m[p->lIdx.d[e]] * y[p->lCol.d[e]];
        y[i] = s;
    }
    for (i = n - 1; i >= 0; i--) {
        double s = y[i];
        for (e = p->uStart.d[i]; e < p->uStart.d[i + 1]; e++) s -= m[p->uIdx.d[e]] * x[p->uCol.d[e]];
        x[i] = s / m[p->diag.d[i]];
    }
}

static int cir_canRetry(const CirPlan* p) { return p->nonlinear.n != 0 && p->lines.n == 0 && p->maxHalvings > 0 && !p->retrying; }

static void cir_save(const CirPlan* p, CirSnapshot* s) {
    size_t i;
    int k;
    s->time = p->time;
    CIR_COPY(s->x, p->x);
    CIR_COPY(s->capacitors, p->capacitors);
    CIR_COPY(s->inductors, p->inductors);
    CIR_COPY(s->coupled, p->coupled);
    CIR_COPY(s->sources, p->sources);
    CIR_RESERVE(s->gates, p->gates.n * 3);
    s->gates.n = p->gates.n * 3;
    for (i = 0; i < p->gates.n; i++) {
        s->gates.d[i * 3] = (char)p->gates.d[i].state; s->gates.d[i * 3 + 1] = (char)p->gates.d[i].clock; s->gates.d[i * 3 + 2] = (char)p->gates.d[i].primed;
    }
    CIR_RESERVE(s->nonlinear, p->nonlinear.n);
    s->nonlinear.n = p->nonlinear.n;
    for (i = 0; i < p->nonlinear.n; i++) {
        const CirNonlinear* e = &p->nonlinear.d[i];
        CirNlSaved* v = &s->nonlinear.d[i];
        v->lim[0] = e->lim[0]; v->lim[1] = e->lim[1];
        for (k = 0; k < 4; k++) v->cur[k] = e->cur[k];
        v->st[0] = e->st[0]; v->st[1] = e->st[1];
        for (k = 0; k < e->junctions; k++) { v->qPrev[k] = e->junction[k].qPrev; v->iPrev[k] = e->junction[k].iPrev; v->started[k] = e->junction[k].started; }
    }
    CIR_COPY(s->xPrev, p->xPrev);
    if (p->chips.n != 0) {
        while (s->chips.n < p->chips.n) { CirChipSaved z; memset(&z, 0, sizeof z); CIR_PUSH(s->chips, z); }
        for (i = 0; i < p->chips.n; i++) {
            const CirChip* c = &p->chips.d[i];
            CirChipSaved* v = &s->chips.d[i];
            CIR_COPY(v->val, c->val);
            CIR_COPY(v->level, c->level);
            CIR_COPY(v->st, c->st);
            v->lastClock = c->lastClock;
            v->lastLoad = c->lastLoad;
        }
    }
}

static void cir_restore(CirPlan* p, const CirSnapshot* s) {
    size_t i;
    int k;
    p->time = s->time;
    CIR_COPY(p->x, s->x);
    CIR_COPY(p->capacitors, s->capacitors);
    CIR_COPY(p->inductors, s->inductors);
    CIR_COPY(p->coupled, s->coupled);
    CIR_COPY(p->sources, s->sources);
    for (i = 0; i < p->gates.n; i++) {
        p->gates.d[i].state = s->gates.d[i * 3] != 0; p->gates.d[i].clock = s->gates.d[i * 3 + 1] != 0; p->gates.d[i].primed = s->gates.d[i * 3 + 2] != 0;
    }
    for (i = 0; i < p->nonlinear.n && i < s->nonlinear.n; i++) {
        CirNonlinear* e = &p->nonlinear.d[i];
        const CirNlSaved* v = &s->nonlinear.d[i];
        e->lim[0] = v->lim[0]; e->lim[1] = v->lim[1];
        for (k = 0; k < 4; k++) e->cur[k] = v->cur[k];
        e->st[0] = v->st[0]; e->st[1] = v->st[1];
        for (k = 0; k < e->junctions; k++) { e->junction[k].qPrev = v->qPrev[k]; e->junction[k].iPrev = v->iPrev[k]; e->junction[k].started = v->started[k]; }
    }
    CIR_COPY(p->xPrev, s->xPrev);
    if (p->chips.n != 0) {
        for (i = 0; i < p->chips.n && i < s->chips.n; i++) {
            CirChip* c = &p->chips.d[i];
            const CirChipSaved* v = &s->chips.d[i];
            CIR_COPY(c->val, v->val);
            CIR_COPY(c->level, v->level);
            CIR_COPY(c->st, v->st);
            c->lastClock = v->lastClock;
            c->lastLoad = v->lastLoad;
        }
    }
}

static void cir_cond(CirPlan* p, int i, int j, double v) { cir_add(p, i, i, v); cir_add(p, j, j, v); cir_add(p, i, j, -v); cir_add(p, j, i, -v); }

/* Sets the time step to h for the steps of a retry: the conductances of the capacitors and inductors (made for `full`, as in stepStart) change with it. */
static void cir_setDt(CirPlan* p, double h, double full) {
    double f;
    size_t i;
    if (p->dt == h) return;
    p->dt = h;
    CIR_COPY(p->g, p->gFull);
    p->gDirty = 1;
    f = full / h;
    for (i = 0; i < p->capacitors.n; i++) {
        double g0 = p->stepStart.capacitors.d[i].geq;
        p->capacitors.d[i].geq = g0 * f;
        cir_cond(p, p->capacitors.d[i].a, p->capacitors.d[i].b, g0 * f - g0);
    }
    for (i = 0; i < p->inductors.n; i++) {
        double g0 = p->stepStart.inductors.d[i].geq;
        p->inductors.d[i].geq = g0 / f;
        cir_cond(p, p->inductors.d[i].a, p->inductors.d[i].b, g0 / f - g0);
    }
    for (i = 0; i < p->coupled.n; i++) {
        const CirCoupled* c0 = &p->stepStart.coupled.d[i];
        CirCoupled* c = &p->coupled.d[i];
        double d;
        c->g11 = c0->g11 / f; c->g12 = c0->g12 / f; c->g22 = c0->g22 / f;
        cir_cond(p, c->a1, c->b1, c->g11 - c0->g11);
        cir_cond(p, c->a2, c->b2, c->g22 - c0->g22);
        d = c->g12 - c0->g12;
        cir_add(p, c->a1, c->a2, d); cir_add(p, c->a1, c->b2, -d); cir_add(p, c->b1, c->a2, -d); cir_add(p, c->b1, c->b2, d);
        cir_add(p, c->a2, c->a1, d); cir_add(p, c->a2, c->b1, -d); cir_add(p, c->b2, c->a1, -d); cir_add(p, c->b2, c->b1, d);
    }
}

static void cir_into(CirPlan* p, int node, double amps) { if (node >= 0) p->rhs.d[node] += amps; }

static void cir_begin(CirPlan* p) {
    double t;
    size_t k, i;
    if (cir_canRetry(p)) cir_save(p, &p->stepStart);
    for (k = 0; k < p->rhs.n; k++) p->rhs.d[k] = 0.0;
    t = p->time + p->dt;
    for (k = 0; k < p->capacitors.n; k++) {
        CirReactive* c = &p->capacitors.d[k];
        c->ieq = c->geq * c->vPrev + c->iPrev; cir_into(p, c->a, c->ieq); cir_into(p, c->b, -c->ieq);
    }
    for (k = 0; k < p->inductors.n; k++) {
        CirReactive* l = &p->inductors.d[k];
        l->ieq = l->iPrev + l->geq * l->vPrev; cir_into(p, l->a, -l->ieq); cir_into(p, l->b, l->ieq);
    }
    for (k = 0; k < p->coupled.n; k++) {
        CirCoupled* c = &p->coupled.d[k];
        c->h1 = c->i1Prev + c->g11 * c->v1Prev + c->g12 * c->v2Prev;
        c->h2 = c->i2Prev + c->g12 * c->v1Prev + c->g22 * c->v2Prev;
        cir_into(p, c->a1, -c->h1); cir_into(p, c->b1, c->h1); cir_into(p, c->a2, -c->h2); cir_into(p, c->b2, c->h2);
    }
    for (k = 0; k < p->lines.n; k++) {
        CirLine* l = &p->lines.d[k];
        l->j1 = l->h2[l->ptr]; l->j2 = l->h1[l->ptr];
        cir_into(p, l->a1, l->j1); cir_into(p, l->b1, -l->j1); cir_into(p, l->a2, l->j2); cir_into(p, l->b2, -l->j2);
    }
    /* the sources fed by streams: the voltage at the end of the step, between the two samples around it (a missing sample: the last one holds) */
    for (k = 0; k < p->feeds.n; k++) {
        const CirFeed* f = &p->feeds.d[k];
        CirStream* st = cir_stream(f->stream);
        double at, frac;
        CirSource* src;
        if (!st || f->source < 0 || (size_t)f->source >= p->sources.n) continue;
        at = t * st->rate;
        if (st->feedIndex < 0) {
            float v;
            st->feedIndex = (int64_t)floor(at);
            st->feedA = cir_streamPop(st, &v) ? v : st->feedA;
            st->feedB = cir_streamPop(st, &v) ? v : st->feedA;
        }
        while ((double)(st->feedIndex + 1) <= at) {
            float v;
            st->feedIndex++;
            st->feedA = st->feedB;
            if (cir_streamPop(st, &v)) st->feedB = v; else st->underruns++;
        }
        frac = at - (double)st->feedIndex;
        if (frac < 0) frac = 0; else if (frac > 1) frac = 1;
        src = &p->sources.d[f->source];
        src->wave = CIR_W_DC;
        src->p0 = st->feedA + (st->feedB - st->feedA) * frac;
    }
    for (k = 0; k < p->sources.n; k++) {
        CirSource* s = &p->sources.d[k];
        double v;
        if (s->wave >= CIR_W_NOISE) cir_sourceAdvance(s, p->dt);
        v = cir_sourceValue(s, t);
        if (s->branch >= 0) p->rhs.d[s->branch] = v;
        else { cir_into(p, s->a, -v); cir_into(p, s->b, v); }
    }
    for (k = 0; k < p->gates.n; k++) {
        CirGate* gate = &p->gates.d[k];
        int high = cir_gateEval(gate, p->x.d);
        if (gate->kind != CIR_G_DFLIPFLOP && gate->kind != CIR_G_SRLATCH && gate->kind != CIR_G_JKFLIPFLOP) gate->state = high;
        if (high) cir_into(p, gate->out, gate->high * gate->g);
        else if (gate->outNot >= 0) cir_into(p, gate->outNot, gate->high * gate->g);
    }
    for (k = 0; k < p->chips.n; k++) {
        CirChip* c = &p->chips.d[k];
        cir_chipExecute(c, p->x.d, p->time);
        for (i = 0; i < c->node.n; i++) {
            if (c->mode.d[i] == CIR_PIN_OUT) cir_into(p, c->node.d[i], c->level.d[i] * c->g);
            else if (c->mode.d[i] == CIR_PIN_CURRENT) cir_into(p, c->node.d[i], c->level.d[i]);
        }
    }
    /* (what parts in fire add between Begin and Finish is the difference to this) */
    if (cir_canRetry(p)) CIR_COPY(p->rhsBegun, p->rhs);
}

static double cir_probeValue(CirPlan* p, const CirProbe* pr) {
    switch (pr->kind) {
        case CIR_PR_NODES: return cir_node(p, pr->a) - cir_node(p, pr->b);
        case CIR_PR_CAPACITOR: return p->capacitors.d[pr->a].iPrev;
        case CIR_PR_INDUCTOR: return p->inductors.d[pr->a].iPrev;
        case CIR_PR_BRANCH: return -p->x.d[pr->a];
        case CIR_PR_CONDUCTANCE: return (cir_node(p, pr->a) - cir_node(p, pr->b)) * pr->g;
        case CIR_PR_CURRENT_SOURCE: return cir_sourceValue(&p->sources.d[pr->a], p->time);
        case CIR_PR_NONLINEAR: return p->nonlinear.d[pr->a].cur[pr->b];
        case CIR_PR_COUPLED: return pr->b == 0 ? p->coupled.d[pr->a].i1Prev : p->coupled.d[pr->a].i2Prev;
        default: return 0.0;
    }
}

/* the currents into the terminals of a part at the node voltages found, from its linearization */
static double cir_nlCurrent(const CirPlan* p, const CirNonlinear* e, int a) {
    double c = e->i0[a];
    int b;
    for (b = 0; b < e->terminals; b++) c += e->j[a][b] * (cir_node(p, e->node[b]) - e->v0[b]);
    return c;
}

/* After the first iteration of a step (it started from the predicted voltages): does the linearization still hold at the solution? Each part that is not linear is
 * linearized again there; when its currents are those its old linearization predicted (within 1e-4 and 1 nA), the solution is that of the non-linear equations, and
 * the second iteration - load, factorize, solve only to confirm it - is saved. The convergence check of SPICE on the currents of the devices. When it fails, the
 * limits of the junctions are as before (the next iteration limits as it would have). */
static int cir_linearizationHolds(CirPlan* p) {
    int holds = 1;
    double* saved = p->limSave.d;
    size_t k;
    for (k = 0; k < p->nonlinear.n; k++) {
        CirNonlinear* e = &p->nonlinear.d[k];
        double want[4];
        int a;
        saved[2 * k] = e->lim[0]; saved[2 * k + 1] = e->lim[1];
        if (!holds) continue;
        for (a = 0; a < e->terminals; a++) want[a] = cir_nlCurrent(p, e, a);
        cir_linearize(e, p->x.d);
        if (e->junctions > 0) cir_stampCharges(e, p->dt);
        if (e->limited) { holds = 0; continue; }
        for (a = 0; a < e->terminals; a++) {
            double d = fabs(e->i0[a] - want[a]);
            if (!(d <= 1e-9 + 1e-4 * cir_max(fabs(e->i0[a]), fabs(want[a])))) { holds = 0; break; }
        }
    }
    if (!holds)
        for (k = 0; k < p->nonlinear.n; k++) { p->nonlinear.d[k].lim[0] = saved[2 * k]; p->nonlinear.d[k].lim[1] = saved[2 * k + 1]; }
    return holds;
}

/* Newton's method for a step with parts that are not linear: load, linearize, factorize, solve - until the node voltages stop moving. */
static int cir_newton(CirPlan* p) {
    const int n = p->size;
    size_t k;
    int predicted, converged = 0, it, i;
    CIR_COPY(p->rhsBase, p->rhs);
    for (k = 0; k < p->nonlinear.n; k++) {
        CirNonlinear* e = &p->nonlinear.d[k];
        if (e->kind == CIR_NL_FORMULA) e->p[0] = p->time + p->dt;
        else if (cir_isVarRes(e->kind)) {
            double gate = e->kind == CIR_NL_SCR ? cir_node(p, e->node[2]) - cir_node(p, e->aux) : e->kind == CIR_NL_TRIAC ? cir_node(p, e->node[2]) - cir_node(p, e->node[1]) : 0;
            cir_stepState(e, cir_node(p, e->node[0]) - cir_node(p, e->node[1]), e->cur[0], p->dt, gate);
        }
    }
    /* the first iteration starts where the step before points to (x + (x - x before)): Newton's method needs fewer iterations while the voltages move (sound); not in
     * the half steps of a retry (another time step) and not right after the plan was made or a node was set */
    predicted = !p->retrying && p->xPrev.n == (size_t)n;
    if (predicted) {
        double* x = p->x.d;
        double* xp = p->xPrev.d;
        for (i = 0; i < n; i++) { double now = x[i]; x[i] = now + (now - xp[i]); xp[i] = now; }
    } else CIR_COPY(p->xPrev, p->x);
    CIR_RESERVE(p->limSave, p->nonlinear.n * 2);
    p->limSave.n = p->nonlinear.n * 2;
    for (it = 0; it < p->maxIterations && !converged; it++) {
        p->iterations++;
        CIR_COPY(p->rhs, p->rhsBase);
        cir_load(p);
        cir_stampNonlinear(p, NULL);
        cir_eliminate(p);
        if (!cir_pivotsOk(p)) {
            /* the order of the pivots does not fit the numbers of this iteration: choose it again for them */
            CIR_COPY(p->scratch, p->g);
            CIR_COPY(p->rhs, p->rhsBase);
            cir_stampNonlinear(p, p->scratch.d);
            if (!cir_order(p, p->scratch.d)) return 0;
            CIR_COPY(p->rhs, p->rhsBase);
            cir_load(p);
            cir_stampNonlinear(p, NULL);
            cir_eliminate(p);
        }
        CIR_COPY(p->xOld, p->x);
        cir_solve(p);
        /* from a good prediction one iteration is often enough: the parts tell whether their linearization still holds */
        if (it == 0 && predicted && p->oneIteration) {
            int anyLimited = 0;
            for (k = 0; k < p->nonlinear.n; k++) if (p->nonlinear.d[k].limited) anyLimited = 1;
            if (!anyLimited && cir_linearizationHolds(p)) { converged = 1; p->shortcuts++; break; }
        }
        converged = it > 0;
        for (k = 0; k < p->nonlinear.n; k++) if (p->nonlinear.d[k].limited) converged = 0;
        for (i = 0; i < n && converged; i++) {
            double dx = fabs(p->x.d[i] - p->xOld.d[i]);
            if (!(dx <= 1e-6 + 1e-4 * fabs(p->x.d[i]))) converged = 0;
        }
    }
    CIR_COPY(p->rhs, p->rhsBase);
    for (k = 0; k < p->nonlinear.n; k++) {
        CirNonlinear* e = &p->nonlinear.d[k];
        int a;
        if (e->junctions > 0) cir_commitCharges(e, p->x.d, p->dt);
        /* the currents at the solution (the linearization of the last iteration, at the node voltages found) */
        for (a = 0; a < e->terminals; a++) e->cur[a] = cir_nlCurrent(p, e, a);
    }
    return converged;
}

/* After the solution of a step: the capacitors, inductors and lines take their new state, the time moves on. */
static void cir_settle(CirPlan* p) {
    size_t k;
    for (k = 0; k < p->capacitors.n; k++) {
        CirReactive* c = &p->capacitors.d[k];
        double v = cir_node(p, c->a) - cir_node(p, c->b);
        c->iPrev = c->geq * v - c->ieq; c->vPrev = v; cir_damp(&c->iPrev, &c->last, &c->flips);
    }
    for (k = 0; k < p->inductors.n; k++) {
        CirReactive* l = &p->inductors.d[k];
        double v = cir_node(p, l->a) - cir_node(p, l->b);
        l->iPrev = l->geq * v + l->ieq; l->vPrev = v; cir_damp(&l->vPrev, &l->last, &l->flips);
    }
    for (k = 0; k < p->coupled.n; k++) {
        CirCoupled* c = &p->coupled.d[k];
        double v1 = cir_node(p, c->a1) - cir_node(p, c->b1), v2 = cir_node(p, c->a2) - cir_node(p, c->b2);
        c->i1Prev = c->g11 * v1 + c->g12 * v2 + c->h1;
        c->i2Prev = c->g12 * v1 + c->g22 * v2 + c->h2;
        c->v1Prev = v1;
        c->v2Prev = v2;
    }
    for (k = 0; k < p->lines.n; k++) {
        CirLine* l = &p->lines.d[k];
        double v1 = cir_node(p, l->a1) - cir_node(p, l->b1), v2 = cir_node(p, l->a2) - cir_node(p, l->b2);
        l->i1 = v1 / l->z - l->j1; l->i2 = v2 / l->z - l->j2;
        l->h1[l->ptr] = v1 / l->z + l->i1; l->h2[l->ptr] = v2 / l->z + l->i2;
        l->ptr = (l->ptr + 1) % l->steps;
    }
    p->time += p->dt;
}

/* The step did not converge: back to its start and again in steps of half the time step (halved again where one does not converge, and doubled again up to dt / 2
 * after one that did). What parts in fire added to the right side stays for all of them. */
static void cir_substeps(CirPlan* p) {
    const int n = p->size;
    double full, end, minStep, h;
    int i;
    p->retried++;
    cir_dAssign(&p->extra, (size_t)n, 0.0);
    for (i = 0; i < n; i++) p->extra.d[i] = p->rhsBase.d[i] - p->rhsBegun.d[i];
    full = p->dt; end = p->stepStart.time + full; minStep = full / (double)(1 << p->maxHalvings);
    cir_restore(p, &p->stepStart);
    CIR_COPY(p->gFull, p->g);
    p->retrying = 1;
    h = full / 2;
    while (p->time < end - full * 1e-9) {
        double step = cir_min(h, end - p->time);
        int ok;
        cir_setDt(p, step, full);
        cir_save(p, &p->subStart);
        cir_begin(p);
        for (i = 0; i < n; i++) p->rhs.d[i] += p->extra.d[i];
        ok = cir_newton(p);
        if (!ok && step > minStep * 1.5) { cir_restore(p, &p->subStart); h = step / 2; continue; }
        if (!ok) p->unconverged++;
        cir_settle(p);
        if (ok) h = cir_min(full / 2, h * 2);
    }
    cir_setDt(p, full, full);
    CIR_COPY(p->g, p->gFull);
    p->gDirty = 1;
    p->time = end;
    p->retrying = 0;
}

static void cir_finish(CirPlan* p) {
    int done = 0, full = 0;
    CirRecorder* last = NULL;
    size_t k;
    if (p->nonlinear.n == 0) cir_solve(p);
    else if (!cir_newton(p)) {
        if (cir_canRetry(p)) { cir_substeps(p); done = 1; }
        else p->unconverged++;
    }
    if (!done) cir_settle(p);
    for (k = 0; k < p->probes.n; k++) {
        const CirProbe* pr = &p->probes.d[k];
        CirRecorder* r = (size_t)pr->rec < g_cirRecorders.n ? g_cirRecorders.d[pr->rec] : NULL;
        if (!r) continue;
        if (r != last) {
            last = r;
            full = !cir_recRoom(r);
            if (!full) CIR_PUSH(r->times, p->time);
        }
        if (!full && (size_t)pr->channel < r->channels.n) CIR_PUSH(r->channels.d[pr->channel], cir_probeValue(p, pr));
    }
    /* the taps: a sample at every point k / rate of their grid that this step passed, between the voltages before and after it */
    for (k = 0; k < p->taps.n; k++) {
        const CirTap* tp = &p->taps.d[k];
        CirStream* st = cir_stream(tp->stream);
        double t1, v1;
        if (!st) continue;
        t1 = p->time; v1 = cir_node(p, tp->a) - cir_node(p, tp->b);
        if (!st->havePrev || st->nextIndex < 0) {
            st->nextIndex = (int64_t)ceil(t1 * st->rate - 1e-9);
            st->prevT = t1; st->prevV = v1; st->havePrev = 1;
        }
        while ((double)st->nextIndex / st->rate <= t1 + 1e-12) {
            double tk = (double)st->nextIndex / st->rate;
            double frac = t1 > st->prevT ? (tk - st->prevT) / (t1 - st->prevT) : 1.0;
            if (frac < 0) frac = 0;
            cir_streamPush(st, (float)(st->prevV + (v1 - st->prevV) * frac));
            st->nextIndex++;
        }
        st->prevT = t1; st->prevV = v1;
    }
}

/* ---- the runner: a plan simulated in real time by a thread of its own ----
 * The thread keeps the time of the plan at anchorSim + (clock - anchorWall) * speed: it does the steps that are due, a few at a time under the kernel lock, and
 * sleeps when it is ahead. The parts written in fire are not called by it: what they added to the right side the last time the program serviced the circuit
 * (HoldBegin ... HoldEnd) is added again in every step until the next service - the simulation runs on with the last values until new ones come. After a pause, or
 * when the computer cannot keep up, at most a quarter of a second of the clock is made up (Lost tells how much was given up). */
typedef struct CirRunner {
    double speed;
    double anchorWall, anchorSim;
    CirHolds holdAdd, holdSet, capAdd, capSet;
    int capturing;
    int64_t steps;
    double lost;                        /* simulated seconds given up (behind by more than a quarter of a second) */
    double share, load;                 /* the part of the wanted time it managed, the part of the clock it computed (over the last half second) */
    double windowWall, windowWant, windowDone, windowBusy;
    int quit;                           /* (read and written under the kernel lock) */
#ifdef CIR_THREADS
#ifdef _WIN32
    HANDLE thread;
#else
    pthread_t thread;
#endif
    int started;
#endif
} CirRunner;

static void cir_runnerFree(CirRunner* r) {
    CIR_FREE(r->holdAdd); CIR_FREE(r->holdSet); CIR_FREE(r->capAdd); CIR_FREE(r->capSet);
    free(r);
}

static double cir_wallSeconds(void) {
#ifdef CIR_THREADS
#ifdef _WIN32
    LARGE_INTEGER f, c;
    QueryPerformanceFrequency(&f);
    QueryPerformanceCounter(&c);
    return (double)c.QuadPart / (double)f.QuadPart;
#else
    struct timespec t;
    clock_gettime(CLOCK_MONOTONIC, &t);
    return (double)t.tv_sec + (double)t.tv_nsec * 1e-9;
#endif
#else
    return 0;
#endif
}

/* One step with what the parts in fire added at their last service. */
static void cir_stepHeld(CirPlan* p) {
    CirRunner* r;
    size_t k;
    cir_begin(p);
    r = p->runner;
    if (r) {
        for (k = 0; k < r->holdSet.n; k++) p->rhs.d[r->holdSet.d[k].row] = r->holdSet.d[k].value;
        for (k = 0; k < r->holdAdd.n; k++) p->rhs.d[r->holdAdd.d[k].row] += r->holdAdd.d[k].value;
    }
    cir_finish(p);
}

#ifdef CIR_THREADS
static void cir_sleepSeconds(double s) {
#ifdef _WIN32
    DWORD ms = (DWORD)ceil(s * 1000.0);
    Sleep(ms > 0 ? ms : 1);
#else
    struct timespec t;
    t.tv_sec = (time_t)s;
    t.tv_nsec = (long)((s - (double)t.tv_sec) * 1e9);
    while (nanosleep(&t, &t) != 0) {}
#endif
}

static void cir_yield(void) {
#ifdef _WIN32
    SwitchToThread();
#else
    sched_yield();
#endif
}

static void cir_runnerLoop(CirPlan* p) {
    CirRunner* r = p->runner;
    double lastWall = cir_wallSeconds();
    for (;;) {
        double sleepFor = 0;
        cir_lock();
        if (r->quit) { cir_unlock(); break; }
        {
            double wall = cir_wallSeconds();
            double target = r->anchorSim + (wall - r->anchorWall) * r->speed;
            double maxBehind = 0.25 * r->speed;
            int devices = 0;
            double lag = 0, t0, now;
            int64_t need, ran = 0;
            size_t k;
            if (target - p->time > maxBehind) {
                double give = target - p->time - maxBehind;
                r->lost += give;
                r->anchorSim -= give;
                target -= give;
            }
            /* the sound devices: what the circuit made since goes to them, what they recorded comes in; their clock leads the way at full speed (the clock of a
             * sound card is not quite that of the computer: without this an output would run dry or overflow after a while) */
            for (k = 0; k < p->taps.n; k++) { CirStream* st = cir_stream(p->taps.d[k].stream); cir_pumpStream(st); lag += cir_deviceLag(st, &devices); }
            for (k = 0; k < p->feeds.n; k++) { CirStream* st = cir_stream(p->feeds.d[k].stream); cir_pumpStream(st); lag += cir_deviceLag(st, &devices); }
            if (devices && fabs(r->speed - 1.0) < 1e-9) {
                double nudge = cir_max(-0.05, cir_min(0.05, lag)) * 0.5 * cir_min(0.1, wall - lastWall);
                r->anchorSim += nudge;
                target += nudge;
            }
            need = (int64_t)floor((target - p->time) / p->dt + 1e-9);
            t0 = wall;
            while (need > 0 && p->compiled) {
                cir_stepHeld(p);
                need--; ran++;
                if ((ran & 31) == 0 && cir_wallSeconds() - t0 > 0.001) break;   /* let the program in now and then */
            }
            r->steps += ran;
            now = cir_wallSeconds();
            r->windowWant += (now - lastWall) * r->speed;
            r->windowDone += (double)ran * p->dt;
            r->windowBusy += now - t0;
            if (now - r->windowWall >= 0.5) {
                double span = now - r->windowWall;
                r->share = r->windowWant > 0 ? cir_min(1.0, r->windowDone / r->windowWant) : 1.0;
                if (r->share > 0.995) r->share = 1.0;
                r->load = span > 0 ? cir_min(1.0, r->windowBusy / span) : 0.0;
                r->windowWall = now; r->windowWant = r->windowDone = r->windowBusy = 0;
            }
            lastWall = now;
            if (need <= 0) {
                /* ahead: until the next step is due, at most 2 ms (a change of the speed or the plan is seen soon) */
                double next = (p->time + p->dt - target) / r->speed;
                sleepFor = cir_min(0.002, cir_max(0.0, next));
            }
        }
        cir_unlock();
        if (sleepFor > 0) cir_sleepSeconds(sleepFor);
        else cir_yield();
    }
}

#ifdef _WIN32
static DWORD WINAPI cir_runnerMain(LPVOID p) { cir_runnerLoop((CirPlan*)p); return 0; }
#else
static void* cir_runnerMain(void* p) { cir_runnerLoop((CirPlan*)p); return NULL; }
#endif
#endif

/* Stops the thread of a plan (not under the kernel lock: the thread needs it to finish its steps). */
static void cir_stopRunner(CirPlan* p) {
    CirRunner* r;
    if (!p) return;
    cir_lock();
    r = p->runner;
    if (r) r->quit = 1;
    cir_unlock();
    if (!r) return;
#ifdef CIR_THREADS
    if (r->started) {
#ifdef _WIN32
        WaitForSingleObject(r->thread, INFINITE);
        CloseHandle(r->thread);
#else
        pthread_join(r->thread, NULL);
#endif
    }
#endif
    cir_lock();
    p->runner = NULL;
    cir_runnerFree(r);
    cir_unlock();
}

static Value cir_Begin(Value h) { CirPlan* p = cir_plan(h); if (p && p->compiled) cir_begin(p); return Undef(); }
static Value fire_cir_Begin(Value h) { CIR_LOCKED(cir_Begin(h)); }

static Value cir_RhsAdd(Value h, Value row, Value amps) {
    CirPlan* p = cir_plan(h);
    if (!p || row.i < 0 || row.i >= p->size) return Undef();
    if (p->runner && p->runner->capturing) { CirHold e; e.row = (int)row.i; e.value = cir_D(amps); CIR_PUSH(p->runner->capAdd, e); }
    else p->rhs.d[row.i] += cir_D(amps);
    return Undef();
}
static Value fire_cir_RhsAdd(Value h, Value row, Value amps) { CIR_LOCKED(cir_RhsAdd(h, row, amps)); }

/* Sets the right side of a row (a branch of a voltage source written in fire). */
static Value cir_RhsSet(Value h, Value row, Value value) {
    CirPlan* p = cir_plan(h);
    if (!p || row.i < 0 || row.i >= p->size) return Undef();
    if (p->runner && p->runner->capturing) { CirHold e; e.row = (int)row.i; e.value = cir_D(value); CIR_PUSH(p->runner->capSet, e); }
    else p->rhs.d[row.i] = cir_D(value);
    return Undef();
}
static Value fire_cir_RhsSet(Value h, Value row, Value value) { CIR_LOCKED(cir_RhsSet(h, row, value)); }

/* The end of a step (after Begin and what parts in fire added): solves it; returns the time after the step. */
static Value cir_Finish(Value h) { CirPlan* p = cir_plan(h); if (p && p->compiled) cir_finish(p); return Float(p ? (Real)p->time : (Real)0); }
static Value fire_cir_Finish(Value h) { CIR_LOCKED(cir_Finish(h)); }

/* a sample of Process: frame i of the input (the last one after the end), its channels mixed */
static double cir_pcmSample(const uint8_t* src, int64_t count, int ch, int64_t i) {
    double sum = 0;
    int c;
    if (i >= count) i = count - 1;
    for (c = 0; c < ch; c++) {
        const uint8_t* b = src + (i * ch + c) * 2;
        sum += (int16_t)(uint16_t)(b[0] | (b[1] << 8)) / 32768.0;
    }
    return sum / ch;
}

/* Sends sound through the plan: `frames` frames of 16 bit samples (little endian, `channels` interleaved; stereo is mixed to mono) from `inOffset` of the buffer
 * `in` drive the source `source` (a sample of 1.0 is `amplitude` volts, linear in between), `oversample` steps per sample (the time step of the plan must be
 * 1 / (rate * oversample)); after each sample the voltage between the nodes `out` and `ref`, divided by `scale`, goes as a 16 bit sample into the buffer `outBuf`
 * from `outOffset` (clipped at full scale). Returns the number of samples clipped, -1 on an error. */
static Value cir_Process(Value h, Value source, Value in, Value inOffset, Value frames, Value channels, Value amplitude, Value oversample, Value out, Value ref,
                         Value scale, Value outBuf, Value outOffset) {
    CirPlan* p = cir_plan(h);
    int ch, n, outNode, refNode;
    int64_t count, clipped = 0, i;
    const uint8_t* src;
    uint8_t* dst;
    CirSource* s;
    double amp, k, previous;
    if (!p || !p->compiled || source.i < 0 || (size_t)source.i >= p->sources.n) { cir_error("The circuit is not compiled or the input is not in it."); return Int(-1); }
    if (!leafAlive(in) || !leafAlive(outBuf)) return destroyedError(in);
    ch = (int)channels.i; n = (int)oversample.i;
    count = frames.i;
    if (ch < 1 || ch > 2 || n < 1 || count < 0) { cir_error("channels 1 or 2, oversample 1 or more."); return Int(-1); }
    if (inOffset.i < 0 || inOffset.i + count * ch * 2 > (int64_t)bufOf(in)->length || outOffset.i < 0 || outOffset.i + count * 2 > (int64_t)bufOf(outBuf)->length) {
        cir_error("The frames do not fit into the buffers.");
        return Int(-1);
    }
    src = bufBytes(bufOf(in)) + inOffset.i;
    dst = bufBytes(bufOf(outBuf)) + outOffset.i;
    s = &p->sources.d[source.i];
    amp = cir_D(amplitude); k = cir_D(scale) != 0 ? 1.0 / cir_D(scale) : 1.0;
    outNode = (int)out.i; refNode = (int)ref.i;
    previous = s->fed ? s->lastSample : count > 0 ? cir_pcmSample(src, count, ch, 0) : 0;
    for (i = 0; i < count; i++) {
        double next = cir_pcmSample(src, count, ch, i), y;
        int step, v;
        for (step = 1; step <= n; step++) {
            s->wave = CIR_W_DC;
            s->p0 = amp * (previous + (next - previous) * step / n);
            cir_begin(p);
            cir_finish(p);
        }
        previous = next;
        s->fed = 1;
        s->lastSample = next;
        y = (cir_node(p, outNode) - cir_node(p, refNode)) * k;
        if (y > 1) { y = 1; clipped++; } else if (y < -1) { y = -1; clipped++; }
        v = (int)lround(y * 32767);
        dst[i * 2] = (uint8_t)(v & 255);
        dst[i * 2 + 1] = (uint8_t)((v >> 8) & 255);
    }
    return Int(clipped);
}
static Value fire_cir_Process(Value h, Value source, Value in, Value inOffset, Value frames, Value channels, Value amplitude, Value oversample, Value out, Value ref,
                              Value scale, Value outBuf, Value outOffset) {
    CIR_LOCKED(cir_Process(h, source, in, inOffset, frames, channels, amplitude, oversample, out, ref, scale, outBuf, outOffset));
}

/* `steps` time steps of a plan without parts written in fire. */
static Value cir_Run(Value h, Value steps) {
    CirPlan* p = cir_plan(h);
    int64_t i;
    if (!p || !p->compiled) return Bool(false);
    for (i = 0; i < steps.i; i++) { cir_begin(p); cir_finish(p); }
    return Bool(true);
}
static Value fire_cir_Run(Value h, Value steps) { CIR_LOCKED(cir_Run(h, steps)); }

static Value cir_Time(Value h) { CirPlan* p = cir_plan(h); return Float(p ? (Real)p->time : (Real)0); }
static Value fire_cir_Time(Value h) { CIR_LOCKED(cir_Time(h)); }
static Value cir_Node(Value h, Value i) { CirPlan* p = cir_plan(h); return Float(p && i.i < p->size ? (Real)cir_node(p, (int)i.i) : (Real)0); }
static Value fire_cir_Node(Value h, Value i) { CIR_LOCKED(cir_Node(h, i)); }

/* Sets an unknown before the first step (the node voltages of the plan before, when the circuit is compiled again). */
static Value cir_SetNode(Value h, Value i, Value v) {
    CirPlan* p = cir_plan(h);
    if (p && i.i >= 0 && i.i < p->size) { p->x.d[i.i] = cir_D(v); p->xPrev.n = 0; }
    return Undef();
}
static Value fire_cir_SetNode(Value h, Value i, Value v) { CIR_LOCKED(cir_SetNode(h, i, v)); }

static Value cir_Unknown(Value h, Value k) { CirPlan* p = cir_plan(h); return Float(p && k.i >= 0 && k.i < p->size ? (Real)p->x.d[k.i] : (Real)0); }
static Value fire_cir_Unknown(Value h, Value k) { CIR_LOCKED(cir_Unknown(h, k)); }

/* The state of a capacitor (kind 0) or inductor (kind 1): what 0 its voltage, 1 its current; of a transformer (kind 2): what 0 and 1 voltage and current of the
 * first winding, 2 and 3 of the second; of a transmission line (kind 3): what 0 and 1 the currents into its ends. */
static Value cir_State(Value h, Value kind, Value index, Value what) {
    CirPlan* p = cir_plan(h);
    const CirReactives* list;
    const CirReactive* r;
    if (!p) return Float(0);
    if (kind.i == 3) {
        const CirLine* l;
        if (index.i < 0 || (size_t)index.i >= p->lines.n) return Float(0);
        l = &p->lines.d[index.i];
        return Float((Real)(what.i == 0 ? l->i1 : l->i2));
    }
    if (kind.i == 2) {
        const CirCoupled* c;
        double v[4];
        if (index.i < 0 || (size_t)index.i >= p->coupled.n) return Float(0);
        c = &p->coupled.d[index.i];
        v[0] = c->v1Prev; v[1] = c->i1Prev; v[2] = c->v2Prev; v[3] = c->i2Prev;
        return Float((Real)(what.i >= 0 && what.i < 4 ? v[what.i] : 0));
    }
    list = kind.i == 0 ? &p->capacitors : &p->inductors;
    if (index.i < 0 || (size_t)index.i >= list->n) return Float(0);
    r = &list->d[index.i];
    return Float((Real)(what.i == 0 ? r->vPrev : r->iPrev));
}
static Value fire_cir_State(Value h, Value kind, Value index, Value what) { CIR_LOCKED(cir_State(h, kind, index, what)); }

/* A transmission line between the ends (a1, b1) and (a2, b2): impedance z (ohms), delay in seconds (at least one step). Returns its index. */
static Value cir_AddLine(Value h, Value a1, Value b1, Value a2, Value b2, Value z, Value delay) {
    CirPlan* p = cir_plan(h);
    double n;
    CirLine l;
    if (!p) return Int(-1);
    if (cir_D(z) <= 0) { cir_error("A transmission line needs an impedance above zero."); return Int(-1); }
    n = round(cir_D(delay) / p->dt);
    if (n > 10000000) { cir_error("The delay of the transmission line is too long for the time step."); return Int(-1); }
    memset(&l, 0, sizeof l);
    l.a1 = (int)a1.i; l.b1 = (int)b1.i; l.a2 = (int)a2.i; l.b2 = (int)b2.i;
    l.z = cir_D(z);
    l.steps = n < 1 ? 1 : (int)n;
    l.h1 = (double*)calloc((size_t)l.steps, sizeof(double));
    l.h2 = (double*)calloc((size_t)l.steps, sizeof(double));
    if (l.h1 == NULL || l.h2 == NULL) { fputs("circuits: out of memory\n", stderr); abort(); }
    cir_conductance(p, l.a1, l.b1, 1.0 / l.z);
    cir_conductance(p, l.a2, l.b2, 1.0 / l.z);
    CIR_PUSH(p->lines, l);
    return Int((int64_t)p->lines.n - 1);
}
static Value fire_cir_AddLine(Value h, Value a1, Value b1, Value a2, Value b2, Value z, Value delay) { CIR_LOCKED(cir_AddLine(h, a1, b1, a2, b2, z, delay)); }

/* The value of a source at the time of the plan. */
static Value cir_SourceValue(Value h, Value index) {
    CirPlan* p = cir_plan(h);
    if (!p || index.i < 0 || (size_t)index.i >= p->sources.n) return Float(0);
    return Float((Real)cir_sourceValue(&p->sources.d[index.i], p->time));
}
static Value fire_cir_SourceValue(Value h, Value index) { CIR_LOCKED(cir_SourceValue(h, index)); }

/* A probe of a scope: kind (CIR_PR_*), a, b, g; it records into channel `channel` of the recorder. */
static Value cir_AddProbe(Value h, Value rec, Value channel, Value kind, Value a, Value b, Value g) {
    CirPlan* p = cir_plan(h);
    CirProbe pr;
    if (!p) return Bool(false);
    pr.rec = (int)rec.i; pr.channel = (int)channel.i; pr.kind = (int)kind.i; pr.a = (int)a.i; pr.b = (int)b.i; pr.g = cir_D(g);
    CIR_PUSH(p->probes, pr);
    return Bool(true);
}
static Value fire_cir_AddProbe(Value h, Value rec, Value channel, Value kind, Value a, Value b, Value g) { CIR_LOCKED(cir_AddProbe(h, rec, channel, kind, a, b, g)); }

static Value cir_LastError(OwnList* list) { return cir_bytesStr(g_cirError ? g_cirError : "", list); }
static Value fire_cir_LastError(OwnList* list) { CIR_LOCKED(cir_LastError(list)); }

/* Statistics of a plan for tests and the editor: 0 size, 1 entries of L and U, 2 update operations, 3 capacitors + inductors + transformers, 4 sources,
 * 6 parts that are not linear + logic parts, 7 iterations, 8 steps that did not converge, 9 steps done again, 10 steps of one iteration. */
static Value cir_Info(Value h, Value what) {
    CirPlan* p = cir_plan(h);
    if (!p) return Int(-1);
    switch (what.i) {
        case 0: return Int(p->size);
        case 1: return Int((int64_t)(p->lCol.n + p->uCol.n + (size_t)p->size));
        case 2: return Int((int64_t)p->updateOps.n / 3);
        case 3: return Int((int64_t)(p->capacitors.n + p->inductors.n + p->coupled.n));
        case 4: return Int((int64_t)p->sources.n);
        case 6: return Int((int64_t)(p->nonlinear.n + p->gates.n));
        case 7: return Int(p->iterations);
        case 8: return Int(p->unconverged);
        case 9: return Int(p->retried);
        case 10: return Int(p->shortcuts);
        default: return Int(-1);
    }
}
static Value fire_cir_Info(Value h, Value what) { CIR_LOCKED(cir_Info(h, what)); }

/* Starts the thread that simulates the plan in real time at `speed` (simulated seconds per second), from the time it has now - or, when it runs, gives it the speed
 * and starts its clock anew. False where there are no threads (the program steps the plan itself). */
static Value cir_StartRunner(Value h, Value speed) {
#ifdef CIR_THREADS
    CirPlan* p = cir_plan(h);
    CirRunner* r;
    int fresh;
    if (!p || !p->compiled || cir_D(speed) <= 0) return Bool(false);
    fresh = p->runner == NULL;
    if (fresh) {
        p->runner = (CirRunner*)calloc(1, sizeof(CirRunner));
        if (p->runner == NULL) return Bool(false);
        p->runner->speed = 1.0;
        p->runner->share = 1.0;
    }
    r = p->runner;
    r->speed = cir_D(speed);
    r->anchorWall = cir_wallSeconds();
    r->anchorSim = p->time;
    r->windowWall = r->anchorWall;
    r->windowWant = r->windowDone = r->windowBusy = 0;
    if (fresh) {
#ifdef _WIN32
        r->thread = CreateThread(NULL, 0, cir_runnerMain, p, 0, NULL);
        r->started = r->thread != NULL;
#else
        r->started = pthread_create(&r->thread, NULL, cir_runnerMain, p) == 0;
#endif
        if (!r->started) { p->runner = NULL; cir_runnerFree(r); return Bool(false); }
    }
    return Bool(true);
#else
    (void)h; (void)speed;
    return Bool(false);
#endif
}
static Value fire_cir_StartRunner(Value h, Value speed) { CIR_LOCKED(cir_StartRunner(h, speed)); }

/* Stops the thread of the plan (the plan stays where it is). */
static Value fire_cir_StopRunner(Value h) {
    CirPlan* p;
    cir_lock(); p = cir_plan(h); cir_unlock();
    cir_stopRunner(p);
    return Undef();
}

/* The thread of a plan: 0 runs (1/0), 1 the share of the wanted time it manages, 2 steps done, 3 the share of the clock it computes, 4 simulated seconds given up
 * because it fell behind by more than a quarter of a second. */
static Value cir_RunnerInfo(Value h, Value what) {
    CirPlan* p = cir_plan(h);
    CirRunner* r = p ? p->runner : NULL;
    switch (what.i) {
        case 0: return Int(r ? 1 : 0);
        case 1: return Float((Real)(r ? r->share : 0.0));
        case 2: return Int(r ? r->steps : 0);
        case 3: return Float((Real)(r ? r->load : 0.0));
        case 4: return Float((Real)(r ? r->lost : 0.0));
        default: return Int(-1);
    }
}
static Value fire_cir_RunnerInfo(Value h, Value what) { CIR_LOCKED(cir_RunnerInfo(h, what)); }

/* The parts written in fire are serviced: what they add to the right side from here (RhsAdd, RhsSet) ... */
static Value cir_HoldBegin(Value h) {
    CirPlan* p = cir_plan(h);
    if (p && p->runner) { p->runner->capturing = 1; p->runner->capAdd.n = 0; p->runner->capSet.n = 0; }
    return Undef();
}
static Value fire_cir_HoldBegin(Value h) { CIR_LOCKED(cir_HoldBegin(h)); }

/* ... until here is what the thread adds in every step from now on. */
static Value cir_HoldEnd(Value h) {
    CirPlan* p = cir_plan(h);
    if (p && p->runner) {
        CirRunner* r = p->runner;
        CirHolds t;
        r->capturing = 0;
        t = r->holdAdd; r->holdAdd = r->capAdd; r->capAdd = t;
        t = r->holdSet; r->holdSet = r->capSet; r->capSet = t;
    }
    return Undef();
}
static Value fire_cir_HoldEnd(Value h) { CIR_LOCKED(cir_HoldEnd(h)); }

/* ---- signal streams ---- */

/* A new stream of `capacity` samples at `rate` per second; its handle. */
static Value cir_StreamNew(Value rate, Value capacity) {
    CirStream* st;
    if (cir_D(rate) <= 0 || capacity.i < 1) return Int(0);
    st = (CirStream*)calloc(1, sizeof(CirStream));
    if (st == NULL) { fputs("circuits: out of memory\n", stderr); abort(); }
    st->rate = cir_D(rate);
    st->nextIndex = -1;
    st->feedIndex = -1;
    st->ringSize = (size_t)capacity.i;
    st->ring = (float*)calloc(st->ringSize, sizeof(float));
    if (st->ring == NULL) { free(st); return Int(0); }
    if (g_cirStreams.n == 0) CIR_PUSH(g_cirStreams, NULL);
    CIR_PUSH(g_cirStreams, st);
    return Int((int64_t)g_cirStreams.n - 1);
}
static Value fire_cir_StreamNew(Value rate, Value capacity) { CIR_LOCKED(cir_StreamNew(rate, capacity)); }

static Value cir_StreamFree(Value h) {
    if (h.i > 0 && (size_t)h.i < g_cirStreams.n) { cir_streamFree(g_cirStreams.d[h.i]); g_cirStreams.d[h.i] = NULL; }
    return Undef();
}
static Value fire_cir_StreamFree(Value h) { CIR_LOCKED(cir_StreamFree(h)); }

/* Writes volts into a stream (an input of the circuit): as many as fit; how many were taken. */
static Value cir_StreamWrite(Value h, Value values) {
    CirStream* st = cir_stream(h.i);
    Arr* a;
    uint32_t i;
    int64_t n = 0;
    if (!st || values.kind != K_Array) return Int(0);
    a = arrOf(values);
    for (i = 0; i < a->length && st->count < st->ringSize; i++, n++) cir_streamPush(st, (float)cir_D(arrItems(a)[i]));
    return Int(n);
}
static Value fire_cir_StreamWrite(Value h, Value values) { CIR_LOCKED(cir_StreamWrite(h, values)); }

/* Takes up to `max` samples (volts) out of a stream (an output of the circuit). */
static Value cir_StreamRead(Value h, Value max, OwnList* list) {
    CirStream* st = cir_stream(h.i);
    size_t n = st ? cir_minZ(st->count, (size_t)cir_maxL(0, max.i)) : 0, i;
    Arr* a = allocArr((uint32_t)n, list);
    for (i = 0; i < n; i++) { float v = 0; cir_streamPop(st, &v); arrItems(a)[i] = Float((Real)v); }
    return ArrV(a);
}
static Value fire_cir_StreamRead(Value h, Value max, OwnList* list) { CIR_LOCKED(cir_StreamRead(h, max, list)); }

/* Writes 16 bit samples (little endian, `channels` interleaved and mixed to one) from `offset` of a buffer into a stream: a full-scale sample is `fullScale` volts.
 * How many frames were taken (as many as fit). */
static Value cir_StreamWritePcm(Value h, Value buf, Value offset, Value frames, Value channels, Value fullScale) {
    CirStream* st = cir_stream(h.i);
    int ch;
    int64_t n, done;
    const uint8_t* src;
    double k;
    if (!st) return Int(0);
    if (!leafAlive(buf)) return destroyedError(buf);
    ch = (int)cir_maxL(1, channels.i);
    n = frames.i;
    if (offset.i < 0 || n < 0 || offset.i + n * ch * 2 > (int64_t)bufOf(buf)->length) { cir_error("The frames do not fit into the buffer."); return Int(-1); }
    src = bufBytes(bufOf(buf)) + offset.i;
    k = cir_D(fullScale) / 32768.0;
    for (done = 0; done < n && st->count < st->ringSize; done++) {
        double sum = 0;
        int c;
        for (c = 0; c < ch; c++) { const uint8_t* b = src + (done * ch + c) * 2; sum += (int16_t)(uint16_t)(b[0] | (b[1] << 8)); }
        cir_streamPush(st, (float)(sum / ch * k));
    }
    return Int(done);
}
static Value fire_cir_StreamWritePcm(Value h, Value buf, Value offset, Value frames, Value channels, Value fullScale) {
    CIR_LOCKED(cir_StreamWritePcm(h, buf, offset, frames, channels, fullScale));
}

/* Takes up to `maxFrames` samples out of a stream as 16 bit mono samples into a buffer from `offset` (full scale at `fullScale` volts, clipped there); how many
 * frames were written. */
static Value cir_StreamReadPcm(Value h, Value buf, Value offset, Value maxFrames, Value fullScale) {
    CirStream* st = cir_stream(h.i);
    int64_t room, n, i;
    uint8_t* dst;
    double k;
    if (!st) return Int(0);
    if (!leafAlive(buf)) return destroyedError(buf);
    room = ((int64_t)bufOf(buf)->length - offset.i) / 2;
    n = cir_minL(cir_minL(maxFrames.i, room), (int64_t)st->count);
    if (offset.i < 0 || n < 0) return Int(0);
    dst = bufBytes(bufOf(buf)) + offset.i;
    k = cir_D(fullScale) != 0 ? 32767.0 / cir_D(fullScale) : 32767.0;
    for (i = 0; i < n; i++) {
        float v = 0;
        double y;
        int s16;
        cir_streamPop(st, &v);
        y = v * k;
        if (y > 32767) y = 32767; else if (y < -32768) y = -32768;
        s16 = (int)lround(y);
        dst[i * 2] = (uint8_t)(s16 & 255);
        dst[i * 2 + 1] = (uint8_t)((s16 >> 8) & 255);
    }
    return Int(n);
}
static Value fire_cir_StreamReadPcm(Value h, Value buf, Value offset, Value maxFrames, Value fullScale) {
    CIR_LOCKED(cir_StreamReadPcm(h, buf, offset, maxFrames, fullScale));
}

/* A stream: 0 samples in it, 1 capacity, 2 samples lost because it was full, 3 samples missing when a source needed one, 4 rate. */
static Value cir_StreamInfo(Value h, Value what) {
    CirStream* st = cir_stream(h.i);
    if (!st) return Int(-1);
    switch (what.i) {
        case 0: return Int((int64_t)st->count);
        case 1: return Int((int64_t)st->ringSize);
        case 2: return Int(st->overruns);
        case 3: return Int(st->underruns);
        case 4: return Float((Real)st->rate);
        default: return Int(-1);
    }
}
static Value fire_cir_StreamInfo(Value h, Value what) { CIR_LOCKED(cir_StreamInfo(h, what)); }

/* Empties a stream (and its position on the grid starts anew). */
static Value cir_StreamClear(Value h) {
    CirStream* st = cir_stream(h.i);
    if (st) {
        st->head = st->count = 0; st->nextIndex = -1; st->havePrev = 0; st->feedIndex = -1;
        if (st->playTo) { st->playTo->primed = 0; st->playTo->pending.n = 0; }
    }
    return Undef();
}
static Value fire_cir_StreamClear(Value h) { CIR_LOCKED(cir_StreamClear(h)); }

/* The plan writes the voltage between the nodes a and b (-1: ground) into the stream at the points of its grid. */
static Value cir_AddTap(Value h, Value st, Value a, Value b) {
    CirPlan* p = cir_plan(h);
    CirTap t;
    if (!p || !cir_stream(st.i)) return Bool(false);
    t.stream = (int)st.i; t.a = (int)a.i; t.b = (int)b.i;
    CIR_PUSH(p->taps, t);
    return Bool(true);
}
static Value fire_cir_AddTap(Value h, Value st, Value a, Value b) { CIR_LOCKED(cir_AddTap(h, st, a, b)); }

/* The stream drives the source `source` of the plan (its voltage, interpolated between the samples). */
static Value cir_AddFeed(Value h, Value st, Value source) {
    CirPlan* p = cir_plan(h);
    CirFeed f;
    if (!p || !cir_stream(st.i)) return Bool(false);
    f.stream = (int)st.i; f.source = (int)source.i;
    CIR_PUSH(p->feeds, f);
    return Bool(true);
}
static Value fire_cir_AddFeed(Value h, Value st, Value source) { CIR_LOCKED(cir_AddFeed(h, st, source)); }

/* A device for a stream (output or input): "sim" or a device of the platform, NULL with LastError. */
static CirDevice* cir_openDevice(CirStream* st, Value device, Value fullScale, Value latency, int input) {
    char* name = cir_utf8(device, NULL);
    CirDevice* d = cir_deviceNew();
    d->rate = (int)lround(st->rate);
    d->fullScale = cir_D(fullScale) > 0 ? cir_D(fullScale) : 1.0;
    d->latency = cir_max(0.005, cir_D(latency));
    if (strcmp(name, "sim") == 0) d->sim = 1;
    else {
#ifdef CIR_AUDIO
        FireAudioStatus status, second;
        int ok;
        if (d->rate < 1000 || d->rate > 192000) { cir_error("A sound device takes rates between 1000 and 192000 Hz."); cir_deviceFree(d); free(name); return NULL; }
        ok = input ? fire_audio_openInput(name, d->rate, 1, &d->in, &status) : fire_audio_open(name, d->rate, 1, &d->out, &status);
        if (!ok) {
            d->channels = 2;   /* (some devices play only stereo) */
            ok = input ? fire_audio_openInput(name, d->rate, 2, &d->in, &second) : fire_audio_open(name, d->rate, 2, &d->out, &second);
            if (!ok) { cir_error("%s", status.message); cir_deviceFree(d); free(name); return NULL; }
        }
#else
        (void)input;
        cir_error("There are no sound devices here (only \"sim\").");
        cir_deviceFree(d);
        free(name);
        return NULL;
#endif
    }
    free(name);
    return d;
}

/* Plays the stream (an output of the circuit) to a sound device: "default" (or another name of the platform, as for Audio.Output) or "sim"; a sample of `fullScale`
 * volts is full scale; `latency` seconds of sound are kept ahead (at least 5 ms). The thread of the circuit (or Pump) hands the samples over. 1 if it plays, 0 with
 * the reason in LastError. */
static Value cir_StreamPlay(Value h, Value device, Value fullScale, Value latency) {
    CirStream* st = cir_stream(h.i);
    CirDevice* d;
    if (!st) { cir_error("The stream is closed."); return Int(0); }
    d = cir_openDevice(st, device, fullScale, latency, 0);
    if (d == NULL) return Int(0);
    cir_deviceFree(st->playTo);
    st->playTo = d;
    return Int(1);
}
static Value fire_cir_StreamPlay(Value h, Value device, Value fullScale, Value latency) { CIR_LOCKED(cir_StreamPlay(h, device, fullScale, latency)); }

/* Records from a sound device into the stream (an input of the circuit): as StreamPlay; the channels are mixed to one. */
static Value cir_StreamRecord(Value h, Value device, Value fullScale, Value latency) {
    CirStream* st = cir_stream(h.i);
    CirDevice* d;
    if (!st) { cir_error("The stream is closed."); return Int(0); }
    d = cir_openDevice(st, device, fullScale, latency, 1);
    if (d == NULL) return Int(0);
    cir_deviceFree(st->recordFrom);
    st->recordFrom = d;
    return Int(1);
}
static Value fire_cir_StreamRecord(Value h, Value device, Value fullScale, Value latency) { CIR_LOCKED(cir_StreamRecord(h, device, fullScale, latency)); }

/* Closes the devices of a stream (what: 1 the output, 2 the input, 3 both). */
static Value cir_StreamDetach(Value h, Value what) {
    CirStream* st = cir_stream(h.i);
    if (st && (what.i & 1)) { cir_deviceFree(st->playTo); st->playTo = NULL; }
    if (st && (what.i & 2)) { cir_deviceFree(st->recordFrom); st->recordFrom = NULL; }
    return Undef();
}
static Value fire_cir_StreamDetach(Value h, Value what) { CIR_LOCKED(cir_StreamDetach(h, what)); }

/* Hands sound between the streams and their devices now (the thread of a circuit does it itself; a program that steps the circuit calls this). */
static Value cir_StreamPump(void) {
    size_t i;
    for (i = 0; i < g_cirStreams.n; i++) cir_pumpStream(g_cirStreams.d[i]);
    return Undef();
}
static Value fire_cir_StreamPump(void) { CIR_LOCKED(cir_StreamPump()); }

/* The device of a stream: 0 frames played, 1 frames recorded, 2 bytes queued in the output device, 3 plays (1/0), 4 records (1/0), 5 its channels. */
static Value cir_StreamDeviceInfo(Value h, Value what) {
    CirStream* st = cir_stream(h.i);
    CirDevice* o;
    CirDevice* i;
    if (!st) return Int(-1);
    o = st->playTo;
    i = st->recordFrom;
    switch (what.i) {
        case 0: return Int(o ? o->frames : 0);
        case 1: return Int(i ? i->frames : 0);
        case 2:
#ifdef CIR_AUDIO
            if (o && o->out) return Int(fire_audio_queued(o->out));
#endif
            return Int(0);
        case 3: return Int(o ? 1 : 0);
        case 4: return Int(i ? 1 : 0);
        case 5: return Int(o ? o->channels : i ? i->channels : 0);
        default: return Int(-1);
    }
}
static Value fire_cir_StreamDeviceInfo(Value h, Value what) { CIR_LOCKED(cir_StreamDeviceInfo(h, what)); }

/* What the simulated output of a stream played (volts), and forgets it. */
static Value cir_StreamSimPlayed(Value h, OwnList* list) {
    CirStream* st = cir_stream(h.i);
    CirDevice* d = st ? st->playTo : NULL;
    size_t n = d && d->sim ? d->simData.n : 0, i;
    Arr* a = allocArr((uint32_t)n, list);
    for (i = 0; i < n; i++) arrItems(a)[i] = Float((Real)d->simData.d[i]);
    if (d) d->simData.n = 0;
    return ArrV(a);
}
static Value fire_cir_StreamSimPlayed(Value h, OwnList* list) { CIR_LOCKED(cir_StreamSimPlayed(h, list)); }

/* Gives the simulated input of a stream sound to record: `count` bytes of 16 bit samples (its channels: mono) from `offset` of a buffer. */
static Value cir_StreamSimInput(Value h, Value buf, Value offset, Value count) {
    CirStream* st = cir_stream(h.i);
    CirDevice* d = st ? st->recordFrom : NULL;
    const uint8_t* src;
    if (!d || !d->sim) { cir_error("The stream does not record from \"sim\"."); return Int(-1); }
    if (!leafAlive(buf)) return destroyedError(buf);
    if (offset.i < 0 || count.i < 0 || offset.i + count.i > (int64_t)bufOf(buf)->length) { cir_error("offset/count are outside of the buffer."); return Int(-1); }
    src = bufBytes(bufOf(buf)) + offset.i;
    CIR_RESERVE(d->simInput, d->simInput.n + (size_t)count.i);
    if (count.i > 0) memcpy(d->simInput.d + d->simInput.n, src, (size_t)count.i);
    d->simInput.n += (size_t)count.i;
    return Int(count.i);
}
static Value fire_cir_StreamSimInput(Value h, Value buf, Value offset, Value count) { CIR_LOCKED(cir_StreamSimInput(h, buf, offset, count)); }

/* ---- recorders ---- */

static Value cir_RecNew(Value capacity) {
    CirRecorder* r = (CirRecorder*)calloc(1, sizeof(CirRecorder));
    if (r == NULL) { fputs("circuits: out of memory\n", stderr); abort(); }
    r->rolling = capacity.i < 0;
    r->capacity = capacity.i > 0 ? capacity.i : capacity.i < -1 ? -capacity.i : 100000;
    if (g_cirRecorders.n == 0) CIR_PUSH(g_cirRecorders, NULL);
    CIR_PUSH(g_cirRecorders, r);
    return Int((int64_t)g_cirRecorders.n - 1);
}
static Value fire_cir_RecNew(Value capacity) { CIR_LOCKED(cir_RecNew(capacity)); }

static Value cir_RecFree(Value h) {
    int64_t i = h.i;
    if (i > 0 && (size_t)i < g_cirRecorders.n) { cir_recFree(g_cirRecorders.d[i]); g_cirRecorders.d[i] = NULL; }
    return Undef();
}
static Value fire_cir_RecFree(Value h) { CIR_LOCKED(cir_RecFree(h)); }

static Value cir_RecChannel(Value h) {
    CirRecorder* r = cir_recorder(h);
    CirD empty;
    if (!r) return Int(-1);
    memset(&empty, 0, sizeof empty);
    CIR_PUSH(r->channels, empty);
    return Int((int64_t)r->channels.n - 1);
}
static Value fire_cir_RecChannel(Value h) { CIR_LOCKED(cir_RecChannel(h)); }

/* A sample that the fire side records (a channel of a part written in fire), and the time of a step that only such channels record. */
static Value cir_RecPut(Value h, Value channel, Value v) {
    CirRecorder* r = cir_recorder(h);
    if (r && channel.i >= 0 && (size_t)channel.i < r->channels.n) {
        CirD* c = &r->channels.d[channel.i];
        if (c->n < r->times.n) CIR_PUSH(*c, cir_D(v));
    }
    return Undef();
}
static Value fire_cir_RecPut(Value h, Value channel, Value v) { CIR_LOCKED(cir_RecPut(h, channel, v)); }

static Value cir_RecTime(Value h, Value t) {
    CirRecorder* r = cir_recorder(h);
    if (r && cir_recRoom(r)) CIR_PUSH(r->times, cir_D(t));
    return Undef();
}
static Value fire_cir_RecTime(Value h, Value t) { CIR_LOCKED(cir_RecTime(h, t)); }

static Value cir_RecCount(Value h) { CirRecorder* r = cir_recorder(h); return Int(r ? (int64_t)r->times.n : 0); }
static Value fire_cir_RecCount(Value h) { CIR_LOCKED(cir_RecCount(h)); }

static Value cir_RecClear(Value h) {
    CirRecorder* r = cir_recorder(h);
    size_t i;
    if (r) { r->times.n = 0; for (i = 0; i < r->channels.n; i++) r->channels.d[i].n = 0; }
    return Undef();
}
static Value fire_cir_RecClear(Value h) { CIR_LOCKED(cir_RecClear(h)); }

static Value cir_RecLast(Value h, Value channel) {
    CirRecorder* r = cir_recorder(h);
    const CirD* c;
    if (!r || channel.i < 0 || (size_t)channel.i >= r->channels.n || r->channels.d[channel.i].n == 0) return Float(0);
    c = &r->channels.d[channel.i];
    return Float((Real)c->d[c->n - 1]);
}
static Value fire_cir_RecLast(Value h, Value channel) { CIR_LOCKED(cir_RecLast(h, channel)); }

/* The samples of a channel (-1: the times) as a float array. */
static Value cir_RecSamples(Value h, Value channel, OwnList* list) {
    CirRecorder* r = cir_recorder(h);
    const double* src = NULL;
    size_t n = 0, i;
    Arr* a;
    if (r && channel.i < 0) { src = r->times.d; n = r->times.n; }
    else if (r && (size_t)channel.i < r->channels.n) { src = r->channels.d[channel.i].d; n = r->channels.d[channel.i].n; }
    a = allocArr((uint32_t)n, list);
    for (i = 0; i < n; i++) arrItems(a)[i] = Float((Real)src[i]);
    return ArrV(a);
}
static Value fire_cir_RecSamples(Value h, Value channel, OwnList* list) { CIR_LOCKED(cir_RecSamples(h, channel, list)); }

/* sample i of channel ys (times channel xs when there is one: the power) */
static double cir_traceValue(const double* ys, const double* xs, size_t i) { return xs ? ys[i] * xs[i] : ys[i]; }

/* The trace of a scope over `columns` columns of the screen. Channel y (times channel x when x >= 0: the power v * i). `span` <= 0: the last `columns` samples, one
 * per column; else a window of `span` seconds - with a trigger (1 rising, 2 falling through `level`) it starts at the latest crossing that leaves room for the whole
 * window, without one (or when there is no crossing) it ends now. Result: [first column with data, value of column 0, ..., of column columns-1]. */
static Value cir_RecTrace(Value h, Value chY, Value chX, Value spanV, Value columnsV, Value triggerV, Value levelV, OwnList* list) {
    CirRecorder* r = cir_recorder(h);
    int64_t columns = cir_maxL(1, cir_minL(16384, columnsV.i));
    Arr* out = allocArr((uint32_t)(columns + 1), list);
    Value* o = arrItems(out);
    const double* ys;
    const double* xs = NULL;
    const double* times;
    size_t count, idx;
    double span, end, start, level;
    int64_t trigger, c, first;
    o[0] = Float((Real)columns);
    for (c = 0; c < columns; c++) o[c + 1] = Float(0);
    if (!r || chY.i < 0 || (size_t)chY.i >= r->channels.n) return ArrV(out);
    ys = r->channels.d[chY.i].d;
    count = cir_minZ(r->channels.d[chY.i].n, r->times.n);
    if (chX.i >= 0 && (size_t)chX.i < r->channels.n) { xs = r->channels.d[chX.i].d; count = cir_minZ(count, r->channels.d[chX.i].n); }
    if (count == 0) return ArrV(out);
    span = cir_D(spanV);
    if (span <= 0) {
        size_t n = cir_minZ(count, (size_t)columns), k;
        first = columns - (int64_t)n;
        o[0] = Float((Real)first);
        for (k = 0; k < n; k++) o[first + (int64_t)k + 1] = Float((Real)cir_traceValue(ys, xs, count - n + k));
        return ArrV(out);
    }
    times = r->times.d;
    end = times[count - 1];
    start = end - span;
    trigger = triggerV.i;
    level = cir_D(levelV);
    if (trigger == 1 || trigger == 2) {
        /* the latest crossing at or before end - span */
        size_t i;
        for (i = count - 1; i > 0; i--) {
            double a, b;
            int hit;
            if (times[i] > end - span) continue;
            a = cir_traceValue(ys, xs, i - 1); b = cir_traceValue(ys, xs, i);
            hit = trigger == 1 ? (a < level && b >= level) : (a > level && b <= level);
            if (hit) {
                double f = b != a ? (level - a) / (b - a) : 0.0;
                start = times[i - 1] + (times[i] - times[i - 1]) * f;
                break;
            }
        }
    }
    /* each column: the sample at its time (linear between the samples) */
    idx = 0;
    while (idx + 1 < count && times[idx + 1] < start) idx++;
    first = columns;
    for (c = 0; c < columns; c++) {
        double tc = start + span * (double)c / (double)cir_maxL(1, columns - 1);
        double v;
        if (tc < times[0] || tc > end + 1e-15) continue;
        while (idx + 1 < count && times[idx + 1] < tc) idx++;
        if (idx + 1 < count && times[idx + 1] > times[idx]) {
            double f = (tc - times[idx]) / (times[idx + 1] - times[idx]);
            f = cir_max(0.0, cir_min(1.0, f));
            v = cir_traceValue(ys, xs, idx) + (cir_traceValue(ys, xs, idx + 1) - cir_traceValue(ys, xs, idx)) * f;
        } else v = cir_traceValue(ys, xs, idx);
        o[c + 1] = Float((Real)v);
        if (c < first) first = c;
    }
    o[0] = Float((Real)first);
    return ArrV(out);
}
static Value fire_cir_RecTrace(Value h, Value chY, Value chX, Value spanV, Value columnsV, Value triggerV, Value levelV, OwnList* list) {
    CIR_LOCKED(cir_RecTrace(h, chY, chX, spanV, columnsV, triggerV, levelV, list));
}

/* The scale of a trace of RecTrace: [lo, hi] - with `range` > 0 -range .. range, otherwise -m .. m with m the largest |value| from the first column with data (at
 * least ±1 for a flat trace). */
static Value cir_TraceRange(Value traceV, Value rangeV, OwnList* list) {
    Arr* trace = arrOf(traceV);
    int64_t columns = trace->length > 0 ? (int64_t)trace->length - 1 : 0;
    int64_t first = trace->length > 0 ? cir_maxL(0, cir_minL(columns, (int64_t)cir_D(arrItems(trace)[0]))) : 0;
    double lo = 0, hi = 0, range = cir_D(rangeV);
    Arr* out;
    if (range > 0) { lo = -range; hi = range; }
    else {
        int64_t c;
        double big;
        for (c = first; c < columns; c++) {
            double v = cir_D(arrItems(trace)[c + 1]);
            if (v < lo) lo = v;
            if (v > hi) hi = v;
        }
        if (hi - lo < 1e-9) { hi += 1.0; lo -= 1.0; }
        big = cir_max(-lo, hi);
        lo = -big; hi = big;
    }
    out = allocArr(2, list);
    arrItems(out)[0] = Float((Real)lo);
    arrItems(out)[1] = Float((Real)hi);
    return ArrV(out);
}
static Value fire_cir_TraceRange(Value traceV, Value rangeV, OwnList* list) { CIR_LOCKED(cir_TraceRange(traceV, rangeV, list)); }

/* A trace of RecTrace as the points of a path [x, y, x, y, ...] in a box at (x0, y0) of height h scaled to lo .. hi: column c at x0 + 1 + c, 0 in the middle - what
 * the view did in a loop of the script per column, in one call. */
static Value cir_TracePoints(Value traceV, Value x0V, Value y0V, Value hV, Value loV, Value hiV, OwnList* list) {
    Arr* trace = arrOf(traceV);
    int64_t columns = trace->length > 0 ? (int64_t)trace->length - 1 : 0;
    int64_t first = trace->length > 0 ? cir_maxL(0, cir_minL(columns, (int64_t)cir_D(arrItems(trace)[0]))) : 0;
    int64_t n = columns - first, c;
    Arr* out = allocArr((uint32_t)(2 * n), list);
    Value* o = arrItems(out);
    double x0 = cir_D(x0V), y0 = cir_D(y0V), h = cir_D(hV), span = cir_D(hiV) - cir_D(loV);
    if (span == 0) span = 1;
    for (c = first; c < columns; c++) {
        int64_t k = c - first;
        o[k * 2] = Float((Real)(x0 + 1.0 + (double)c));
        o[k * 2 + 1] = Float((Real)(y0 + h / 2.0 - (cir_D(arrItems(trace)[c + 1]) / span * 2.0) * (h / 2.0 - 2.0)));
    }
    return ArrV(out);
}
static Value fire_cir_TracePoints(Value traceV, Value x0V, Value y0V, Value hV, Value loV, Value hiV, OwnList* list) {
    CIR_LOCKED(cir_TracePoints(traceV, x0V, y0V, hV, loV, hiV, list));
}

/* The last pairs of two channels for an XY display: [x0, y0, x1, y1, ...] of at most `count` samples (the newest last). */
static Value cir_RecPairs(Value h, Value chX, Value chY, Value countV, OwnList* list) {
    CirRecorder* r = cir_recorder(h);
    const CirD* xs;
    const CirD* ys;
    size_t n, take, k;
    Arr* out;
    if (!r || chX.i < 0 || chY.i < 0 || (size_t)chX.i >= r->channels.n || (size_t)chY.i >= r->channels.n) return ArrV(allocArr(0, list));
    xs = &r->channels.d[chX.i];
    ys = &r->channels.d[chY.i];
    n = cir_minZ(xs->n, ys->n);
    take = cir_minZ(n, (size_t)cir_maxL(0, countV.i));
    out = allocArr((uint32_t)(take * 2), list);
    for (k = 0; k < take; k++) {
        arrItems(out)[k * 2] = Float((Real)xs->d[n - take + k]);
        arrItems(out)[k * 2 + 1] = Float((Real)ys->d[n - take + k]);
    }
    return ArrV(out);
}
static Value fire_cir_RecPairs(Value h, Value chX, Value chY, Value countV, OwnList* list) { CIR_LOCKED(cir_RecPairs(h, chX, chY, countV, list)); }

/* Measurements of a channel (times channel x when x >= 0: the power v * i) over what the recorder holds, like the info of a scope in Falstad's simulator: [count,
 * min, max, average, rms, frequency (0: none found), duration in seconds]. Average and rms over whole periods when there are two rising crossings of the middle
 * (with a hysteresis of a tenth of peak to peak), otherwise over all samples. */
static Value cir_RecStats(Value h, Value chY, Value chX, OwnList* list) {
    CirRecorder* r = cir_recorder(h);
    Arr* out = allocArr(7, list);
    Value* o = arrItems(out);
    const double* y;
    const double* x = NULL;
    size_t n, i, firstI = 0, lastI = 0, a = 0, b, rises = 0;
    double lo, hi, mid, band, firstRise = 0, lastRise = 0, freq = 0, sum = 0, sq = 0, cnt;
    for (i = 0; i < 7; i++) o[i] = Float(0);
    if (!r || chY.i < 0 || (size_t)chY.i >= r->channels.n) return ArrV(out);
    y = r->channels.d[chY.i].d;
    n = cir_minZ(r->channels.d[chY.i].n, r->times.n);
    if (chX.i >= 0 && (size_t)chX.i < r->channels.n) { x = r->channels.d[chX.i].d; n = cir_minZ(n, r->channels.d[chX.i].n); }
    if (n == 0) return ArrV(out);
    lo = cir_traceValue(y, x, 0); hi = lo;
    for (i = 1; i < n; i++) { double v = cir_traceValue(y, x, i); lo = cir_min(lo, v); hi = cir_max(hi, v); }
    /* rising crossings of the middle (linear between the samples), with hysteresis against noise */
    mid = (lo + hi) / 2; band = (hi - lo) / 10;
    if (hi - lo > 1e-12) {
        int below = cir_traceValue(y, x, 0) < mid;
        for (i = 1; i < n; i++) {
            double v = cir_traceValue(y, x, i);
            if (v < mid - band) below = 1;
            else if (below && v >= mid) {
                double pv = cir_traceValue(y, x, i - 1), f = v != pv ? (mid - pv) / (v - pv) : 0;
                double t = r->times.d[i - 1] + f * (r->times.d[i] - r->times.d[i - 1]);
                if (rises == 0) firstRise = t;
                lastRise = t;
                rises++;
                if (rises == 1) firstI = i;
                lastI = i;
                below = 0;
            }
        }
    }
    b = n;
    if (rises >= 2 && lastRise > firstRise) {
        freq = (double)(rises - 1) / (lastRise - firstRise);
        a = firstI; b = lastI;
    }
    for (i = a; i < b; i++) { double v = cir_traceValue(y, x, i); sum += v; sq += v * v; }
    cnt = (double)(b - a);
    o[0] = Float((Real)n);
    o[1] = Float((Real)lo);
    o[2] = Float((Real)hi);
    o[3] = Float((Real)(sum / cnt));
    o[4] = Float((Real)sqrt(sq / cnt));
    o[5] = Float((Real)freq);
    o[6] = Float((Real)(r->times.d[n - 1] - r->times.d[0]));
    return ArrV(out);
}
static Value fire_cir_RecStats(Value h, Value chY, Value chX, OwnList* list) { CIR_LOCKED(cir_RecStats(h, chY, chX, list)); }

/* The spectrum of a channel for an FFT analyzer: the last `size` samples (a power of two, 16 .. 8192), taken at the rate 2 * fmax (so the bins reach up to fmax;
 * fmax <= 0 or a coarser simulation: the rate of the simulation), the mean taken away, a Hann window, then the FFT. Result: [bin width in Hz, amplitude of bin 1,
 * ..., amplitude of bin size/2] (amplitudes of a sine as its peak value); empty if there are too few samples. */
static Value cir_RecSpectrum(Value h, Value channel, Value sizeV, Value fmaxV, OwnList* list) {
    CirRecorder* r = cir_recorder(h);
    const double* v = NULL;
    const double* times;
    size_t count = 0, n = 16, look, idx, valid = 0, k, half, i, j, len;
    double simDt, fmax, dt, end, mean = 0, wsum = 0, scale;
    const double pi = 3.14159265358979323846;
    double* re;
    double* im;
    double* x;
    char* has;
    Arr* out;
    if (r && channel.i >= 0 && (size_t)channel.i < r->channels.n) { v = r->channels.d[channel.i].d; count = cir_minZ(r->channels.d[channel.i].n, r->times.n); }
    while (n < (size_t)cir_maxL(16, cir_minL(8192, sizeV.i))) n <<= 1;
    if (count < 16) return ArrV(allocArr(0, list));
    times = r->times.d;
    /* the step of the simulation (over the last samples) and the rate of the analysis */
    look = cir_minZ(count - 1, 64);
    simDt = (times[count - 1] - times[count - 1 - look]) / (double)look;
    if (!(simDt > 0)) return ArrV(allocArr(0, list));
    fmax = cir_D(fmaxV);
    dt = simDt;
    if (fmax > 0 && 1.0 / (2.0 * fmax) > simDt) dt = 1.0 / (2.0 * fmax);
    /* resample the last n points (linear between the recorded ones); before the first sample: none */
    re = (double*)calloc(n, sizeof(double));
    im = (double*)calloc(n, sizeof(double));
    x = (double*)calloc(n, sizeof(double));
    has = (char*)calloc(n, 1);
    if (!re || !im || !x || !has) { free(re); free(im); free(x); free(has); return ArrV(allocArr(0, list)); }
    end = times[count - 1];
    idx = count - 1;
    for (k = n; k-- > 0;) {
        double tk = end - (double)(n - 1 - k) * dt;
        double y;
        while (idx > 0 && times[idx - 1] >= tk) idx--;
        if (idx == 0 && times[0] > tk) break;
        if (times[idx] <= tk || idx == 0) y = v[idx];
        else {
            double t0 = times[idx - 1], t1 = times[idx];
            double a = t1 > t0 ? (tk - t0) / (t1 - t0) : 1.0;
            y = v[idx - 1] + (v[idx] - v[idx - 1]) * a;
        }
        x[k] = y;
        has[k] = 1;
        mean += y;
        valid++;
    }
    if (valid < 16) { free(re); free(im); free(x); free(has); return ArrV(allocArr(0, list)); }
    mean /= (double)valid;
    for (k = 0; k < n; k++) {
        double w = 0.5 - 0.5 * cos(2.0 * pi * (double)k / (double)(n - 1));
        wsum += w;
        re[k] = has[k] ? (x[k] - mean) * w : 0.0;
    }
    /* iterative radix-2 FFT */
    for (i = 1, j = 0; i < n; i++) {
        size_t bit = n >> 1;
        for (; j & bit; bit >>= 1) j ^= bit;
        j ^= bit;
        if (i < j) { double t = re[i]; re[i] = re[j]; re[j] = t; t = im[i]; im[i] = im[j]; im[j] = t; }
    }
    for (len = 2; len <= n; len <<= 1) {
        double ang = -2.0 * pi / (double)len;
        double wr = cos(ang), wi = sin(ang);
        size_t s;
        for (s = 0; s < n; s += len) {
            double cr = 1, ci = 0;
            for (k = 0; k < len / 2; k++) {
                size_t a = s + k, b = s + k + len / 2;
                double br = re[b] * cr - im[b] * ci, bi = re[b] * ci + im[b] * cr;
                double ncr;
                re[b] = re[a] - br; im[b] = im[a] - bi;
                re[a] += br; im[a] += bi;
                ncr = cr * wr - ci * wi;
                ci = cr * wi + ci * wr;
                cr = ncr;
            }
        }
    }
    half = n / 2;
    out = allocArr((uint32_t)(half + 1), list);
    arrItems(out)[0] = Float((Real)(1.0 / (dt * (double)n)));
    scale = wsum > 0 ? 2.0 / wsum : 0.0;
    for (k = 1; k <= half; k++) arrItems(out)[k] = Float((Real)(sqrt(re[k] * re[k] + im[k] * im[k]) * scale));
    free(re); free(im); free(x); free(has);
    return ArrV(out);
}
static Value fire_cir_RecSpectrum(Value h, Value channel, Value sizeV, Value fmaxV, OwnList* list) { CIR_LOCKED(cir_RecSpectrum(h, channel, sizeV, fmaxV, list)); }

#endif /* FIRE_BRIDGE_CIRCUITS_H */
