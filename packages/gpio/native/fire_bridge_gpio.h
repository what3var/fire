/* fire native bridge "gpio" (C): the natives behind `#import "gpio"` (docs/networking/README.md, "Hardware"): digital pins - read, write, pull resistors, edge events. The
 * fire side (Gpio.Pin, Gpio.Board, Gpio.Sim - fire source) is the same in the VM and in a native build; this file is what its `__Gpio...` functions do.
 *
 * This file is the one implementation of the gpio natives, in C99 ("language": "c"): the native build puts it into the generated program, and the virtual
 * machine runs it in a shared library built from it (native/abi/fire_pkg_abi.h). The pins come from the platform layer (FIRE_PLATFORM_GPIO_C_HEADER: fire_gpio_*,
 * see platform/std/fire_gpio_c.h): the GPIO character device on a Linux board (Raspberry Pi, ...), ESP-IDF on an ESP32. Where there is no hardware the list of
 * chips is empty - but one chip is always there:
 *
 *   "sim"   32 simulated lines (0..31) that need no hardware: write an output, read it back, connect two lines with a wire (`SimWire`), drive an input from
 *           outside (`SimDrive`, as a button would), pull resistors and edge events behave as on the real thing. For tests, and to develop a program on a PC that
 *           later runs on the board.
 *
 * A pin is an integer handle in a table of this bridge (handles are not used again). Opening a pin claims nothing; the first `Configure` does (a second handle to
 * the same line then fails with Busy). Every function reports an error the same way: a result of -1/false/undefined and the code and message of this thread
 * (`__GpioLastError`, `__GpioLastErrorMessage`, the slot of the io bridge), from which the fire code throws a typed exception. Nothing waits: edges are collected
 * (the sim here, the driver or an interrupt on a board) and `PollEdge` hands them out one by one, the fire code sleeps between the questions. */
#ifndef FIRE_BRIDGE_GPIO_H
#define FIRE_BRIDGE_GPIO_H

#include <stdarg.h>
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include FIRE_PLATFORM_GPIO_C_HEADER

/* (the codes are those of the platform layer: FIRE_GPIO_E_*, the same numbers as Gpio.GpioException.code) */

static int64_t fire_gpiob_fail(int code, const char* message) {
    g_ioError.code = code;
    snprintf(g_ioError.message, sizeof g_ioError.message, "%s", message);
    return -1;
}
static int64_t fire_gpiob_failf(int code, const char* format, ...) {
    char text[176];
    va_list args;
    va_start(args, format);
    vsnprintf(text, sizeof text, format, args);
    va_end(args);
    return fire_gpiob_fail(code, text);
}
static void fire_gpiob_ok(void) { g_ioError.code = 0; g_ioError.message[0] = 0; }
static int64_t fire_gpiob_failStatus(const FireGpioStatus* s) { return fire_gpiob_fail(s->code, s->message); }

/* ---- text ---------------------------------------------------------------------------------------------------------------------------------- */
/* A fire string as UTF-8 (malloc; the caller frees it; NULL when out of memory); a lone surrogate becomes U+FFFD. */
static char* fire_gpiob_utf8(Value v) {
    const Str* s;
    char* out;
    size_t n = 0;
    uint32_t i;
    if (v.kind != K_String) { out = (char*)malloc(1); if (out) out[0] = 0; return out; }
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
    return out;
}
/* (names and messages are ASCII or UTF-8 from the system; a byte above 127 becomes the Latin-1 character of the same number) */
static Value fire_gpiob_str8(const char* text, OwnList* list) {
    size_t n = strlen(text), i;
    Str* s = allocStr((uint32_t)n, list);
    for (i = 0; i < n; i++) strChars(s)[i] = (fire_char16)(uint8_t)text[i];
    return StrV(s);
}

/* ---- the simulated chip ---------------------------------------------------------------------------------------------------------------------- */
enum { FIRE_GPIOB_SIM_LINES = 32, FIRE_GPIOB_EVENT_QUEUE = 16 };
enum { FIRE_GPIOB_D_INPUT = 0, FIRE_GPIOB_D_OUTPUT = 1 };
enum { FIRE_GPIOB_P_NONE = 0, FIRE_GPIOB_P_UP = 1, FIRE_GPIOB_P_DOWN = 2 };
enum { FIRE_GPIOB_E_NONE = 0, FIRE_GPIOB_E_RISING = 1, FIRE_GPIOB_E_FALLING = 2, FIRE_GPIOB_E_BOTH = 3 };

typedef struct FireGpioSimLine {
    int claimed;
    int configured;
    int direction;
    int pull;
    int edge;
    int out;            /* the level an output drives */
    int drive;          /* the level somebody outside drives (-1 nobody) */
    int level;          /* the level on the line now (what a read gives) */
    int events[FIRE_GPIOB_EVENT_QUEUE];
    int64_t times[FIRE_GPIOB_EVENT_QUEUE];
    int head, count;
} FireGpioSimLine;
typedef struct FireGpioSim {
    FireGpioSimLine line[FIRE_GPIOB_SIM_LINES];
    unsigned char wire[FIRE_GPIOB_SIM_LINES][FIRE_GPIOB_SIM_LINES];
} FireGpioSim;

static FireGpioSim g_gpioSim;
static int g_gpioSimReady = 0;

/* The simulated chip as it starts: nothing open, no wires, nobody drives. */
static void fire_gpiob_simInit(FireGpioSim* s) {
    int i;
    memset(s, 0, sizeof *s);
    for (i = 0; i < FIRE_GPIOB_SIM_LINES; i++) s->line[i].drive = -1;
}
static FireGpioSim* fire_gpiob_sim(void) {
    if (!g_gpioSimReady) { fire_gpiob_simInit(&g_gpioSim); g_gpioSimReady = 1; }
    return &g_gpioSim;
}

/* The lines that are connected to `start` by wires (including itself). */
static int fire_gpiob_simNet(const FireGpioSim* s, int start, int* in) {
    int stack[FIRE_GPIOB_SIM_LINES], sp = 0, n = 0, i, b;
    for (i = 0; i < FIRE_GPIOB_SIM_LINES; i++) in[i] = 0;
    in[start] = 1;
    stack[sp++] = start;
    while (sp > 0) {
        int a = stack[--sp];
        n++;
        for (b = 0; b < FIRE_GPIOB_SIM_LINES; b++)
            if (s->wire[a][b] && !in[b]) { in[b] = 1; stack[sp++] = b; }
    }
    return n;
}
/* The level of the net `in`: an output wins (when two outputs disagree, low wins - as a pulled-down open line would), else the outside drive, else the pulls,
 * else low. */
static int fire_gpiob_simLevelOf(const FireGpioSim* s, const int* in) {
    int anyOut = 0, high = 0, anyDrive = 0, driveHigh = 0, up = 0, down = 0, i;
    for (i = 0; i < FIRE_GPIOB_SIM_LINES; i++) {
        const FireGpioSimLine* l;
        if (!in[i]) continue;
        l = &s->line[i];
        if (l->drive >= 0) { anyDrive = 1; if (l->drive) driveHigh = 1; }
        if (l->configured && l->direction == FIRE_GPIOB_D_OUTPUT) { anyOut = 1; if (l->out) high = 1; }
        else if (l->configured) { if (l->pull == FIRE_GPIOB_P_UP) up = 1; else if (l->pull == FIRE_GPIOB_P_DOWN) down = 1; }
    }
    if (anyOut) {
        for (i = 0; i < FIRE_GPIOB_SIM_LINES; i++)
            if (in[i] && s->line[i].configured && s->line[i].direction == FIRE_GPIOB_D_OUTPUT && !s->line[i].out) return 0;
        return high ? 1 : 0;
    }
    if (anyDrive) return driveHigh ? 1 : 0;
    if (up && !down) return 1;
    return 0;
}
/* Computes the level of every line again; every input that watches for the change gets an event. */
static void fire_gpiob_simSettle(FireGpioSim* s) {
    int64_t now = fire_gpio_nowMicros();
    int in[FIRE_GPIOB_SIM_LINES];
    int i;
    for (i = 0; i < FIRE_GPIOB_SIM_LINES; i++) {
        FireGpioSimLine* l;
        int lv, kind, at;
        fire_gpiob_simNet(s, i, in);
        lv = fire_gpiob_simLevelOf(s, in);
        l = &s->line[i];
        if (lv == l->level) continue;
        l->level = lv;
        if (!l->configured || l->direction != FIRE_GPIOB_D_INPUT) continue;
        kind = lv ? FIRE_GPIOB_E_RISING : FIRE_GPIOB_E_FALLING;
        if (!(l->edge & kind)) continue;
        if (l->count == FIRE_GPIOB_EVENT_QUEUE) { l->head = (l->head + 1) % FIRE_GPIOB_EVENT_QUEUE; l->count--; }   /* the oldest is lost when nobody asks */
        at = (l->head + l->count) % FIRE_GPIOB_EVENT_QUEUE;
        l->events[at] = kind;
        l->times[at] = now;
        l->count++;
    }
}

/* ---- the pins -------------------------------------------------------------------------------------------------------------------------------- */
typedef struct FireGpioPin {
    int isSim;
    int simLine;
    FireGpioLine* line;
    char* chip;
    int number;
    int configured;
    int direction;
    int64_t lastMicros;
} FireGpioPin;

static void fire_gpiob_release(FireGpioPin* p) {
    if (p == NULL) return;
    if (p->isSim) {
        FireGpioSimLine* l = &fire_gpiob_sim()->line[p->simLine];
        if (p->configured) {
            l->claimed = 0;
            l->configured = 0;
            l->count = 0;
            fire_gpiob_simSettle(fire_gpiob_sim());
        }
    } else if (p->line) fire_gpio_closeLine(p->line);
    free(p->chip);
    free(p);
}

/* The open pins; the handle is the index (0 is not used). */
static FireGpioPin** g_gpioPins = NULL;
static size_t g_gpioPinCount = 1, g_gpioPinCap = 0;

/* The program has ended (the library for the VM stays loaded for the next one): gives the pins back and resets the simulated chip. */
static void fire_gpio_reset(void) {
    size_t i;
    for (i = 1; i < g_gpioPinCount; i++) {
        fire_gpiob_release(g_gpioPins[i]);
        g_gpioPins[i] = NULL;
    }
    fire_gpiob_simInit(&g_gpioSim);
    g_gpioSimReady = 1;
}

/* Puts a pin into the table: the handle (-1: out of memory, the pin is released). */
static int64_t fire_gpiob_add(FireGpioPin* p) {
    if (g_gpioPinCount >= g_gpioPinCap) {
        size_t cap = g_gpioPinCap ? g_gpioPinCap * 2 : 16;
        FireGpioPin** grown = (FireGpioPin**)realloc(g_gpioPins, sizeof(FireGpioPin*) * cap);
        if (grown == NULL) { fire_gpiob_release(p); return fire_gpiob_fail(FIRE_GPIO_E_OTHER, "Out of memory."); }
        if (g_gpioPinCap == 0) grown[0] = NULL;
        g_gpioPins = grown;
        g_gpioPinCap = cap;
    }
    g_gpioPins[g_gpioPinCount] = p;
    return (int64_t)g_gpioPinCount++;
}
static FireGpioPin* fire_gpiob_find(Value h) {
    int64_t i = h.i;
    if (i > 0 && (size_t)i < g_gpioPinCount && g_gpioPins[i] != NULL) return g_gpioPins[i];
    fire_gpiob_fail(FIRE_GPIO_E_INVALID_HANDLE, "Invalid or already closed pin handle.");
    return NULL;
}
static FireGpioPin* fire_gpiob_findConfigured(Value h) {
    FireGpioPin* p = fire_gpiob_find(h);
    if (p == NULL) return NULL;
    if (!p->configured) { fire_gpiob_fail(FIRE_GPIO_E_INVALID_ARGUMENT, "The pin is not set up yet (call Input or Output first)."); return NULL; }
    return p;
}
/* "sim" first, then the chips of the hardware: `hardware` is filled (the caller frees it); 0 with the error set when the platform cannot list them. */
static int fire_gpiob_hardware(FireGpioList* hardware) {
    FireGpioStatus st;
    hardware->items = NULL;
    hardware->count = hardware->cap = 0;
    if (!fire_gpio_chips(hardware, &st)) { fire_gpio_listFree(hardware); fire_gpiob_failStatus(&st); return 0; }
    return 1;
}

/* ---- natives --------------------------------------------------------------------------------------------------------------------------------- */
static Value fire_gpio_LastError(void) { return Int(g_ioError.code); }
static Value fire_gpio_LastErrorMessage(OwnList* list) { return fire_gpiob_str8(g_ioError.message, list); }
static Value fire_gpio_OpenCount(void) {
    int64_t n = 0;
    size_t i;
    for (i = 1; i < g_gpioPinCount; i++) if (g_gpioPins[i] != NULL) n++;
    return Int(n);
}
/* 1 if this machine has GPIO hardware (a driver for it and at least one chip; the simulated chip is there anyway), else 0. */
static Value fire_gpio_Supported(void) {
    FireGpioList hardware = { NULL, 0, 0 };
    FireGpioStatus st;
    int yes = 0;
    fire_gpiob_ok();
    if (fire_gpio_supported()) yes = fire_gpio_chips(&hardware, &st) && hardware.count > 0;
    fire_gpio_listFree(&hardware);
    return Int(yes ? 1 : 0);
}

/* The chips: "sim" first, then those of the hardware. */
static Value fire_gpio_Chips(OwnList* list) {
    FireGpioList hardware;
    Arr* a;
    size_t i;
    if (!fire_gpiob_hardware(&hardware)) return Undef();
    fire_gpiob_ok();
    a = allocArr((uint32_t)(hardware.count + 1), list);
    arrItems(a)[0] = fire_gpiob_str8("sim", list);
    retain(arrItems(a)[0]);
    for (i = 0; i < hardware.count; i++) { arrItems(a)[i + 1] = fire_gpiob_str8(hardware.items[i], list); retain(arrItems(a)[i + 1]); }
    fire_gpio_listFree(&hardware);
    return ArrV(a);
}

static Value fire_gpio_Open(Value chip, Value number) {
    FireGpioPin* p;
    char* name = fire_gpiob_utf8(chip);
    if (name == NULL) return Int(fire_gpiob_fail(FIRE_GPIO_E_OTHER, "Out of memory."));
    if (number.i < 0 || number.i > 100000) { free(name); return Int(fire_gpiob_failf(FIRE_GPIO_E_INVALID_ARGUMENT, "Invalid line number %lld.", (long long)number.i)); }
    p = (FireGpioPin*)calloc(1, sizeof(FireGpioPin));
    if (p == NULL) { free(name); return Int(fire_gpiob_fail(FIRE_GPIO_E_OTHER, "Out of memory.")); }
    p->chip = name;
    p->number = (int)number.i;
    if (strcmp(name, "sim") == 0) {
        if (number.i >= FIRE_GPIOB_SIM_LINES) {
            fire_gpiob_release(p);
            return Int(fire_gpiob_failf(FIRE_GPIO_E_NOT_FOUND, "The simulated chip has the lines 0..%d, not %lld.", FIRE_GPIOB_SIM_LINES - 1, (long long)number.i));
        }
        p->isSim = 1;
        p->simLine = (int)number.i;
    } else {
        FireGpioStatus st;
        p->line = fire_gpio_openLine(name, (int)number.i, &st);
        if (p->line == NULL) { fire_gpiob_release(p); return Int(fire_gpiob_failStatus(&st)); }
    }
    {
        int64_t handle = fire_gpiob_add(p);
        if (handle < 0) return Int(-1);
        fire_gpiob_ok();
        return Int(handle);
    }
}

/* Sets the pin up: direction 0 input / 1 output, pull 0 none / 1 up / 2 down, edge 0 none / 1 rising / 2 falling / 3 both (inputs), value: the level an output
 * starts with. true, or false on an error. */
static Value fire_gpio_Configure(Value h, Value direction, Value pull, Value edge, Value value) {
    FireGpioPin* p = fire_gpiob_find(h);
    int v;
    if (p == NULL) return Bool(false);
    if (direction.i < 0 || direction.i > 1 || pull.i < 0 || pull.i > 2 || edge.i < 0 || edge.i > 3) { fire_gpiob_fail(FIRE_GPIO_E_INVALID_ARGUMENT, "Invalid direction, pull or edge."); return Bool(false); }
    v = value.i != 0 ? 1 : 0;
    if (p->isSim) {
        FireGpioSimLine* l = &fire_gpiob_sim()->line[p->simLine];
        if (!p->configured && l->claimed) { fire_gpiob_failf(FIRE_GPIO_E_BUSY, "Line %d of the chip sim is already in use.", p->simLine); return Bool(false); }
        if (!p->configured) { l->claimed = 1; l->count = 0; }
        l->configured = 1;
        l->direction = (int)direction.i;
        l->pull = (int)pull.i;
        l->edge = direction.i == FIRE_GPIOB_D_INPUT ? (int)edge.i : 0;
        l->out = v;
        fire_gpiob_simSettle(fire_gpiob_sim());
        if (direction.i == FIRE_GPIOB_D_INPUT) l->count = 0;   /* what happened while setting up is not an edge */
    } else {
        FireGpioStatus st;
        if (!fire_gpio_configure(p->line, (int)direction.i, (int)pull.i, (int)edge.i, v, &st)) { fire_gpiob_failStatus(&st); return Bool(false); }
    }
    p->configured = 1;
    p->direction = (int)direction.i;
    fire_gpiob_ok();
    return Bool(true);
}

/* The level of the pin: 0 or 1; -1 on an error. */
static Value fire_gpio_Read(Value h) {
    FireGpioPin* p = fire_gpiob_findConfigured(h);
    FireGpioStatus st;
    int v = 0;
    if (p == NULL) return Int(-1);
    if (p->isSim) { fire_gpiob_ok(); return Int(fire_gpiob_sim()->line[p->simLine].level); }
    if (!fire_gpio_read(p->line, &v, &st)) return Int(fire_gpiob_failStatus(&st));
    fire_gpiob_ok();
    return Int(v ? 1 : 0);
}

static Value fire_gpio_Write(Value h, Value value) {
    FireGpioPin* p = fire_gpiob_findConfigured(h);
    int v;
    if (p == NULL) return Bool(false);
    if (p->direction != FIRE_GPIOB_D_OUTPUT) { fire_gpiob_fail(FIRE_GPIO_E_INVALID_ARGUMENT, "The pin is an input: it cannot be written."); return Bool(false); }
    v = value.i != 0 ? 1 : 0;
    if (p->isSim) {
        fire_gpiob_sim()->line[p->simLine].out = v;
        fire_gpiob_simSettle(fire_gpiob_sim());
    } else {
        FireGpioStatus st;
        if (!fire_gpio_write(p->line, v, &st)) { fire_gpiob_failStatus(&st); return Bool(false); }
    }
    fire_gpiob_ok();
    return Bool(true);
}

/* The next edge since the last question, without waiting: 0 none, 1 rising, 2 falling; -1 on an error. `EdgeTime` then says when it happened. */
static Value fire_gpio_PollEdge(Value h) {
    FireGpioPin* p = fire_gpiob_findConfigured(h);
    int event = 0;
    int64_t micros = 0;
    if (p == NULL) return Int(-1);
    if (p->direction != FIRE_GPIOB_D_INPUT) return Int(fire_gpiob_fail(FIRE_GPIO_E_INVALID_ARGUMENT, "The pin is an output: it has no edges."));
    if (p->isSim) {
        FireGpioSimLine* l = &fire_gpiob_sim()->line[p->simLine];
        if (l->count > 0) {
            event = l->events[l->head];
            micros = l->times[l->head];
            l->head = (l->head + 1) % FIRE_GPIOB_EVENT_QUEUE;
            l->count--;
        }
    } else {
        FireGpioStatus st;
        if (!fire_gpio_pollEdge(p->line, &event, &micros, &st)) return Int(fire_gpiob_failStatus(&st));
    }
    if (event != 0) p->lastMicros = micros;
    fire_gpiob_ok();
    return Int(event);
}

/* When the last edge that `PollEdge` handed out happened (microseconds of a clock that only goes forward). */
static Value fire_gpio_EdgeTime(Value h) {
    FireGpioPin* p = fire_gpiob_find(h);
    if (p == NULL) return Int(-1);
    fire_gpiob_ok();
    return Int(p->lastMicros);
}

static Value fire_gpio_Close(Value h) {
    int64_t i = h.i;
    if (i <= 0 || (size_t)i >= g_gpioPinCount || g_gpioPins[i] == NULL) { fire_gpiob_fail(FIRE_GPIO_E_INVALID_HANDLE, "Invalid or already closed pin handle."); return Bool(false); }
    fire_gpiob_release(g_gpioPins[i]);
    g_gpioPins[i] = NULL;
    fire_gpiob_ok();
    return Bool(true);
}

/* ---- the simulated chip from the outside ----------------------------------------------------------------------------------------------------- */
static int fire_gpiob_simLine(int64_t n) {
    if (n < 0 || n >= FIRE_GPIOB_SIM_LINES) { fire_gpiob_failf(FIRE_GPIO_E_NOT_FOUND, "The simulated chip has the lines 0..%d, not %lld.", FIRE_GPIOB_SIM_LINES - 1, (long long)n); return 0; }
    return 1;
}
/* Connects two lines of the simulated chip by a wire (what one drives, the other sees). */
static Value fire_gpio_SimWire(Value a, Value b) {
    FireGpioSim* s;
    if (!fire_gpiob_simLine(a.i) || !fire_gpiob_simLine(b.i)) return Bool(false);
    if (a.i == b.i) { fire_gpiob_fail(FIRE_GPIO_E_INVALID_ARGUMENT, "A line cannot be wired to itself."); return Bool(false); }
    s = fire_gpiob_sim();
    s->wire[a.i][b.i] = s->wire[b.i][a.i] = 1;
    fire_gpiob_simSettle(s);
    fire_gpiob_ok();
    return Bool(true);
}
static Value fire_gpio_SimUnwire(Value a, Value b) {
    FireGpioSim* s;
    if (!fire_gpiob_simLine(a.i) || !fire_gpiob_simLine(b.i)) return Bool(false);
    s = fire_gpiob_sim();
    s->wire[a.i][b.i] = s->wire[b.i][a.i] = 0;
    fire_gpiob_simSettle(s);
    fire_gpiob_ok();
    return Bool(true);
}
/* Drives a line of the simulated chip from outside (a button, a sensor): 0 or 1, -1 lets go. */
static Value fire_gpio_SimDrive(Value line, Value value) {
    FireGpioSim* s;
    if (!fire_gpiob_simLine(line.i)) return Bool(false);
    s = fire_gpiob_sim();
    s->line[line.i].drive = value.i < 0 ? -1 : (value.i != 0 ? 1 : 0);
    fire_gpiob_simSettle(s);
    fire_gpiob_ok();
    return Bool(true);
}
/* The level on a line of the simulated chip, whoever drives it (it can be looked at without opening the line). */
static Value fire_gpio_SimLevel(Value line) {
    if (!fire_gpiob_simLine(line.i)) return Int(-1);
    fire_gpiob_ok();
    return Int(fire_gpiob_sim()->line[line.i].level);
}
/* What the program drives on a line of the simulated chip: 0 or 1 while it is an output of the program, -1 while it is not (an input, or not open). */
static Value fire_gpio_SimOutput(Value line) {
    const FireGpioSimLine* l;
    if (!fire_gpiob_simLine(line.i)) return Int(-2);
    fire_gpiob_ok();
    l = &fire_gpiob_sim()->line[line.i];
    if (!l->configured || l->direction != FIRE_GPIOB_D_OUTPUT) return Int(-1);
    return Int(l->out ? 1 : 0);
}
/* Takes all wires and drives away (the pins that are open stay open). */
static Value fire_gpio_SimReset(void) {
    FireGpioSim* s = fire_gpiob_sim();
    int i, j;
    for (i = 0; i < FIRE_GPIOB_SIM_LINES; i++) {
        s->line[i].drive = -1;
        for (j = 0; j < FIRE_GPIOB_SIM_LINES; j++) s->wire[i][j] = 0;
    }
    fire_gpiob_simSettle(s);
    fire_gpiob_ok();
    return Bool(true);
}

#endif
