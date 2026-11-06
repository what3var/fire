/* fire native runtime: the debugger of a debug build (docs/spec/NATIVE_DEBUG_PROTOCOL.md, docs/spec/NATIVE_BACKEND.md "Debug-Builds"). Included by fire_rt.h when the
 * program is generated in the mode `debug` (FIRE_DEBUG).
 *
 * The generated code keeps a shadow stack: every fire function has a frame (DbgFrame) on it with its function, the addresses of its variables, `this`, and
 * the position it is at (source, line, scope state), which the code updates where a line begins. While a debugger is attached (g_dbgArmed), the line hook
 * decides whether the thread stops there (breakpoint, step, pause), exactly like the stepping loops of the VM. A stopped thread waits (without the GIL) until
 * the debugger lets it go on.
 *
 * The agent (FIRE_DEBUG_AGENT: the desktop - posix, windows -, FreeRTOS and ESP32) is a thread (a task) of its own that talks to spark: over TCP on the
 * desktop, framed over the console UART on an ESP32, framed over stdio for tests and FreeRTOS boards (see "the transport" below), one JSON message per line. It never runs
 * fire code to show a value (no ToString()), never touches a reference count and never allocates on the fire heap; what changes the program (`set`) is done by
 * the stopped thread itself. */
#ifndef FIRE_DEBUG_H
#define FIRE_DEBUG_H

/* ---- the tables the generator writes ---- */
typedef struct DbgVar { const char* name; uint16_t index; } DbgVar;   /* a variable of a scope: its name and its slot in DbgFrame.vars */
/* A level of a scope chain (VM.ScopeLevel): its variables (the slots declared so far), whether it is the top level of a function (1), and the level around it
 * (-1: none - the global scope follows). A scope state of a function is the index of its innermost level. The outermost level of a fire body (functionTop 2)
 * holds the thread's own variables of its top level: they are globals of the thread, not a scope of the chain. */
typedef struct DbgLevel { int32_t parent; uint8_t functionTop; uint16_t count; const DbgVar* vars; } DbgLevel;
typedef struct DbgFunc { const char* name; const DbgLevel* levels; } DbgFunc;
typedef struct DbgGlobal { const char* name; Value* slot; } DbgGlobal;          /* by global slot (slot null: no variable) */
typedef struct DbgSource { const char* file; const char* hash; } DbgSource;     /* by source index (null: the prelude, no file) */
typedef struct DbgClass { const char* name; uint32_t nfields; const char* const* fields; } DbgClass;
typedef struct DbgProgram {
    const DbgSource* sources; int32_t nsources; int32_t firstUser;
    const DbgGlobal* globals; int32_t nglobals;
    int32_t entrySrc, entryLine;   /* the position of the first instruction of the program (where the main thread waits at the start) */
    const char* target;
} DbgProgram;

/* The frame of a running fire function (on the C stack of its wrapper). `vars` points to the addresses of its variables, `thisSlot` to its `this` (null: none). */
struct DbgFrame {
    DbgFrame* prev;
    const DbgFunc* fn;
    Value* const* vars;
    Value* thisSlot;   /* (not `self`: the generated code has a variable of that name) */
    int32_t src, line, lv;
    uint32_t depth;
    uint8_t hidden;   /* called the way the VM runs it nested (a getter, a destructor, ...): no stops inside */
};

extern FIRE_RT_DATA volatile int g_dbgArmed;     /* a debugger is attached: the hooks look at the position */
extern FIRE_RT_DATA int32_t g_dbgGlobalCount;    /* the global variables declared so far (VM.DebugGlobals) */
/* The program's standard input comes from the agent (a framed transport owns the input, docs/spec/NATIVE_DEBUG_PROTOCOL.md "Transport"): IO reads it with
 * fire_dbgStdinRead (waits for input or its end, without the GIL; 0 at the end) and gives a byte back with fire_dbgStdinUnget. */
extern FIRE_RT_DATA volatile int g_dbgStdin;
FIRE_RT_FN int64_t fire_dbgStdinRead(uint8_t* out, int64_t count);
FIRE_RT_FN void fire_dbgStdinUnget(int c);

/* (generated) the class `cls` for the debugger: its name and the names of its fields, base fields first */
FIRE_HOST_EXTERN const DbgClass* fire_dbgClass(uint32_t cls);
/* (generated) `set` of field `index` of an object: through the setter of the field, as the class itself would assign it */
FIRE_HOST_EXTERN void fire_dbgSetField(Value o, uint32_t index, Value x);

FIRE_RT_FN void fire_dbgLine(DbgFrame* f);
FIRE_RT_FN void fire_dbgRet(DbgFrame* caller);
FIRE_RT_FN void fire_dbgStart(const DbgProgram* program);
/* A plugin is registered (fireRegisterPlugin, before its static initialisers): its sources get the next indexes after the program's (p->srcMap), the debugger hears
 * of them (event `sources`) and the thread waits until it has sent its breakpoints (at most FIRE_DEBUG_PLUGIN_WAIT_MS). */
#ifdef FIRE_MODULES
FIRE_RT_FN void fire_dbgPlugin(const FirePlugin* p);
#endif

FIRE_FN void fire_dbgEnter(DbgFrame* f, const DbgFunc* fn, int hidden) {
    DbgFrame* prev = g_dbgTop;
    f->prev = prev;
    f->fn = fn;
    f->vars = NULL;
    f->thisSlot = NULL;
    f->src = 0; f->line = 0; f->lv = -1;
    f->depth = prev ? prev->depth + 1 : 0;
    f->hidden = (uint8_t)hidden;
    if (hidden) g_dbgNest++;
    g_dbgTop = f;
}
FIRE_FN void fire_dbgLeave(DbgFrame* f) {
    g_dbgTop = f->prev;
    if (f->hidden) g_dbgNest--;
    else if (FIRE_UNLIKELY(g_dbgArmed) && f->prev) fire_dbgRet(f->prev);   /* back in the caller: its position is a new one (like the VM after Return) */
}
/* A line begins (`src`, `line`, scope state `lv`). */
#define FIRE_DBG_L(s, l, v) do { DF->src = (s); DF->line = (l); DF->lv = (v); if (FIRE_UNLIKELY(g_dbgArmed)) fire_dbgLine(DF); } while (0)
/* Code that the VM runs nested, called from the runtime (ToString() for a text, the constructor of an exception the runtime makes). */
#define FIRE_DBG_NESTED(...) do { g_dbgNest++; __VA_ARGS__; g_dbgNest--; } while (0)

#if defined(FIRE_RT_IMPLEMENTATION) && !defined(__cplusplus)
volatile int g_dbgArmed = 0;
int32_t g_dbgGlobalCount = 0;
volatile int g_dbgStdin = 0;

#ifndef FIRE_DEBUG_AGENT
/* No agent on this platform (yet): the program runs without a debugger. */
void fire_dbgLine(DbgFrame* f) { (void)f; }
void fire_dbgRet(DbgFrame* caller) { (void)caller; }
void fire_dbgStart(const DbgProgram* program) { (void)program; }
#ifdef FIRE_MODULES
void fire_dbgPlugin(const FirePlugin* p) { (void)p; }
#endif
void fire_dbgExited(int code) { (void)code; }
int fire_dbgUnhandled(Value exception) { (void)exception; return 0; }
void fire_dbgThreadBegin(void) {}
void fire_dbgThreadEnd(void) {}
int64_t fire_dbgStdinRead(uint8_t* out, int64_t count) { (void)out; (void)count; return 0; }
void fire_dbgStdinUnget(int c) { (void)c; }
#else

/* ---- the system: threads, a lock with a condition (also with a timeout), and the transport (docs/spec/NATIVE_DEBUG_PROTOCOL.md "Transport") ----
 * Three transports behind one small interface (open, send a message, receive bytes, close):
 *   tcp    FIRE_DEBUG=tcp:HOST:PORT;token=T - the desktop: the program connects to spark; one JSON message per line.
 *   stdio  FIRE_DEBUG=stdio[;token=T][;wait=MS] on the desktop, or FIRE_DEBUG_TRANSPORT_STDIO (a FreeRTOS board, the simulator native/sim): framed
 *          messages on stdout between the output of the program, framed commands on stdin; the other lines of stdin are the program's standard input.
 *   uart   FIRE_DEBUG_TRANSPORT_UART (ESP32, the generator defines it in a debug build): the same framing on the console UART, which also carries the output
 *          of the program and the log of ESP-IDF; the agent owns its receiving side and passes what is not a frame on to the program's standard input.
 * A frame is one line: \x1E "FD:" <json> "*" <CRC-16 of the json, 4 hex digits> "\n" (the JSON is pure ASCII). Over a framed transport the program waits
 * FIRE_DEBUG_WAIT_MS (3 s) after `hello` for the first command; without one it runs without a debugger. */
#ifndef FIRE_DEBUG_WAIT_MS
#define FIRE_DEBUG_WAIT_MS 3000
#endif
#if defined(_WIN32)
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <io.h>
#define DBG_HAS_TCP 1
typedef SRWLOCK dbg_mutex;
typedef CONDITION_VARIABLE dbg_cond;
typedef SOCKET dbg_socket;
#define DBG_NO_SOCKET INVALID_SOCKET
static void dbgMutexInit(dbg_mutex* m) { InitializeSRWLock(m); }
static void dbgLock(dbg_mutex* m) { AcquireSRWLockExclusive(m); }
static void dbgUnlock(dbg_mutex* m) { ReleaseSRWLockExclusive(m); }
static void dbgCondInit(dbg_cond* c) { InitializeConditionVariable(c); }
static void dbgWait(dbg_cond* c, dbg_mutex* m) { SleepConditionVariableSRW(c, m, INFINITE, 0); }
/* Waits at most `ms`; 0 when the time ran out. */
static int dbgWaitMs(dbg_cond* c, dbg_mutex* m, int ms) { return SleepConditionVariableSRW(c, m, (DWORD)ms, 0) ? 1 : 0; }
static void dbgBroadcast(dbg_cond* c) { WakeAllConditionVariable(c); }
static DWORD WINAPI dbgThreadTrampoline(LPVOID arg);
static bool dbgSpawn(void (*fn)(void)) {
    HANDLE h = CreateThread(NULL, 0, dbgThreadTrampoline, (LPVOID)fn, 0, NULL);
    if (!h) return false;
    CloseHandle(h);
    return true;
}
static DWORD WINAPI dbgThreadTrampoline(LPVOID arg) { ((void (*)(void))arg)(); return 0; }
static int dbgSockSend(dbg_socket s, const char* p, size_t n) { return send(s, p, (int)n, 0); }
static int dbgSockRecv(dbg_socket s, char* p, size_t n) { return recv(s, p, (int)n, 0); }
static void dbgCloseSocket(dbg_socket s) { shutdown(s, SD_BOTH); closesocket(s); }
static int dbgPid(void) { return (int)GetCurrentProcessId(); }
static int dbgStdinRaw(char* p, size_t n) { return _read(0, p, (unsigned)n); }
/* A frame onto stdout in one piece: the lock of the stream keeps the prints of the program out of it. */
static int dbgStdoutWrite(const char* p, size_t n) {
    int ok;
    _lock_file(stdout);
    ok = fwrite(p, 1, n, stdout) == n && fflush(stdout) == 0;
    _unlock_file(stdout);
    return ok;
}
#elif defined(FIRE_PLATFORM_FREERTOS_H)
/* FreeRTOS (ESP-IDF, a board, the simulator): a mutex, a condition out of binary semaphores (like fire_cond of the platform package), the agent is a task */
#include <unistd.h>
#ifndef FIRE_DEBUG_STACK_BYTES
#define FIRE_DEBUG_STACK_BYTES 8192
#endif
#ifndef FIRE_DEBUG_PRIORITY
#define FIRE_DEBUG_PRIORITY (tskIDLE_PRIORITY + 2)
#endif
typedef SemaphoreHandle_t dbg_mutex;
typedef struct DbgCondWaiter { SemaphoreHandle_t sem; struct DbgCondWaiter* next; int queued; } DbgCondWaiter;
typedef struct { DbgCondWaiter* head; } dbg_cond;
static void dbgMutexInit(dbg_mutex* m) { *m = xSemaphoreCreateMutex(); }
static void dbgLock(dbg_mutex* m) { xSemaphoreTake(*m, portMAX_DELAY); }
static void dbgUnlock(dbg_mutex* m) { xSemaphoreGive(*m); }
static void dbgCondInit(dbg_cond* c) { c->head = NULL; }
static int dbgCondBlock(dbg_cond* c, dbg_mutex* m, TickType_t ticks) {
    DbgCondWaiter w;
    int signalled;
    w.sem = xSemaphoreCreateBinary();
    w.next = c->head;
    w.queued = 1;
    c->head = &w;
    dbgUnlock(m);
    signalled = xSemaphoreTake(w.sem, ticks) == pdTRUE;
    dbgLock(m);
    if (!signalled) {
        if (w.queued) {   /* timed out and nobody gave it: leave the queue */
            DbgCondWaiter** at;
            for (at = &c->head; *at; at = &(*at)->next)
                if (*at == &w) { *at = w.next; break; }
        } else signalled = xSemaphoreTake(w.sem, 0) == pdTRUE;
    }
    vSemaphoreDelete(w.sem);
    return signalled;
}
static void dbgWait(dbg_cond* c, dbg_mutex* m) { dbgCondBlock(c, m, portMAX_DELAY); }
static int dbgWaitMs(dbg_cond* c, dbg_mutex* m, int ms) { TickType_t t = pdMS_TO_TICKS(ms); return dbgCondBlock(c, m, t ? t : 1); }
static void dbgBroadcast(dbg_cond* c) {
    DbgCondWaiter* w = c->head;
    while (w) {
        DbgCondWaiter* next = w->next;
        w->queued = 0;
        xSemaphoreGive(w->sem);
        w = next;
    }
    c->head = NULL;
}
static void dbgTaskBody(void* arg) {
    void (*fn)(void);
    memcpy(&fn, &arg, sizeof fn);
    fn();
    vTaskDelete(NULL);
}
static bool dbgSpawn(void (*fn)(void)) {
    void* arg;
    uint32_t depth = (uint32_t)FIRE_DEBUG_STACK_BYTES;
#ifndef FIRE_FREERTOS_STACK_BYTES
    depth /= (uint32_t)sizeof(StackType_t);
#endif
    memcpy(&arg, &fn, sizeof arg);
    return xTaskCreate(dbgTaskBody, "fire_dbg", depth, arg, FIRE_DEBUG_PRIORITY, NULL) == pdPASS;
}
static int dbgPid(void) { return 0; }
static int dbgStdinRaw(char* p, size_t n) { return (int)read(fileno(stdin), p, n); }
static int dbgStdoutWrite(const char* p, size_t n) {
    size_t off = 0;
    flockfile(stdout);
    fflush(stdout);   /* (what the program printed before; then the frame in one write: a write of the console is not split) */
    while (off < n) {
        ssize_t w = write(fileno(stdout), p + off, n - off);
        if (w <= 0) break;
        off += (size_t)w;
    }
    funlockfile(stdout);
    return off == n;
}
#ifdef FIRE_DEBUG_TRANSPORT_UART
/* ESP-IDF: the console UART. The agent installs its driver (the console then writes through it as well) and reads it; nobody else reads the console. */
#include "driver/uart.h"
#include "driver/uart_vfs.h"
#include "esp_system.h"
#ifndef FIRE_DEBUG_UART
#ifdef CONFIG_ESP_CONSOLE_UART_NUM
#define FIRE_DEBUG_UART CONFIG_ESP_CONSOLE_UART_NUM
#else
#define FIRE_DEBUG_UART 0
#endif
#endif
static bool dbgUartOpen(void) {
    fflush(stdout);
    if (!uart_is_driver_installed(FIRE_DEBUG_UART) && uart_driver_install(FIRE_DEBUG_UART, 2048, 0, 0, NULL, 0) != ESP_OK) return false;
    uart_vfs_dev_use_driver(FIRE_DEBUG_UART);
    return true;
}
static int dbgUartRecv(char* p, size_t n) {
    for (;;) {
        int r = uart_read_bytes(FIRE_DEBUG_UART, p, (uint32_t)n, pdMS_TO_TICKS(20));
        if (r != 0) return r;
    }
}
#endif
#else
#include <pthread.h>
#include <arpa/inet.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <sys/socket.h>
#include <sys/time.h>
#include <sys/types.h>
#include <time.h>
#include <unistd.h>
#define DBG_HAS_TCP 1
typedef pthread_mutex_t dbg_mutex;
typedef pthread_cond_t dbg_cond;
typedef int dbg_socket;
#define DBG_NO_SOCKET (-1)
static void dbgMutexInit(dbg_mutex* m) { pthread_mutex_init(m, NULL); }
static void dbgLock(dbg_mutex* m) { pthread_mutex_lock(m); }
static void dbgUnlock(dbg_mutex* m) { pthread_mutex_unlock(m); }
static void dbgCondInit(dbg_cond* c) { pthread_cond_init(c, NULL); }
static void dbgWait(dbg_cond* c, dbg_mutex* m) { pthread_cond_wait(c, m); }
static int dbgWaitMs(dbg_cond* c, dbg_mutex* m, int ms) {
    struct timespec until;
    struct timeval now;
    gettimeofday(&now, NULL);   /* (the clock of a condition with default attributes) */
    until.tv_sec = now.tv_sec + ms / 1000;
    until.tv_nsec = (long)now.tv_usec * 1000L + (long)(ms % 1000) * 1000000L;
    if (until.tv_nsec >= 1000000000L) { until.tv_sec++; until.tv_nsec -= 1000000000L; }
    return pthread_cond_timedwait(c, m, &until) == 0;
}
static void dbgBroadcast(dbg_cond* c) { pthread_cond_broadcast(c); }
static void* dbgThreadTrampoline(void* arg) { void (*fn)(void); memcpy(&fn, &arg, sizeof fn); fn(); return NULL; }
static bool dbgSpawn(void (*fn)(void)) {
    pthread_t t;
    void* arg;
    memcpy(&arg, &fn, sizeof arg);
    if (pthread_create(&t, NULL, dbgThreadTrampoline, arg) != 0) return false;
    pthread_detach(t);
    return true;
}
static int dbgSockSend(dbg_socket s, const char* p, size_t n) {
#ifdef MSG_NOSIGNAL
    return (int)send(s, p, n, MSG_NOSIGNAL);
#else
    return (int)send(s, p, n, 0);
#endif
}
static int dbgSockRecv(dbg_socket s, char* p, size_t n) { return (int)recv(s, p, n, 0); }
static void dbgCloseSocket(dbg_socket s) { shutdown(s, SHUT_RDWR); close(s); }
static int dbgPid(void) { return (int)getpid(); }
static int dbgStdinRaw(char* p, size_t n) { return (int)read(0, p, n); }
static int dbgStdoutWrite(const char* p, size_t n) {
    size_t off = 0;
    flockfile(stdout);
    fflush(stdout);
    while (off < n) {
        ssize_t w = write(fileno(stdout), p + off, n - off);
        if (w <= 0) break;
        off += (size_t)w;
    }
    funlockfile(stdout);
    return off == n;
}
#endif

/* the transport in use */
enum { DBG_T_NONE, DBG_T_TCP, DBG_T_STDIO, DBG_T_UART };
static int g_dbgTransport = DBG_T_NONE;
#ifdef DBG_HAS_TCP
static dbg_socket g_dbgSock = DBG_NO_SOCKET;
#endif
/* CRC-16/CCITT-FALSE (polynomial 0x1021, start 0xFFFF, no reflection, no final xor) of a frame's JSON */
static unsigned dbgCrc16(const char* p, size_t n) {
    unsigned crc = 0xFFFF;
    size_t i;
    int k;
    for (i = 0; i < n; i++) {
        crc ^= (unsigned)(unsigned char)p[i] << 8;
        for (k = 0; k < 8; k++) crc = (crc & 0x8000) ? ((crc << 1) ^ 0x1021) & 0xFFFF : (crc << 1) & 0xFFFF;
    }
    return crc;
}
/* Sends one message (`n` bytes of JSON, no line break). 0: the debugger is gone. */
static int dbgTransportSend(const char* p, size_t n) {
    switch (g_dbgTransport) {
#ifdef DBG_HAS_TCP
        case DBG_T_TCP: {
            size_t off = 0;
            if (g_dbgSock == DBG_NO_SOCKET) return 0;
            while (off < n) {
                int w = dbgSockSend(g_dbgSock, p + off, n - off);
                if (w <= 0) return 0;
                off += (size_t)w;
            }
            return dbgSockSend(g_dbgSock, "\n", 1) == 1;
        }
#endif
        case DBG_T_STDIO:
        case DBG_T_UART: {
            char* frame = (char*)malloc(n + 16);
            int ok;
            if (!frame) return 1;
            frame[0] = 0x1E; frame[1] = 'F'; frame[2] = 'D'; frame[3] = ':';
            memcpy(frame + 4, p, n);
            snprintf(frame + 4 + n, 12, "*%04x\n", dbgCrc16(p, n));
            ok = dbgStdoutWrite(frame, n + 10);
            free(frame);
            return ok;
        }
        default: return 0;
    }
}
/* Reads what came (lines, framed or not); <= 0: the end. */
static int dbgTransportRecv(char* p, size_t n) {
    switch (g_dbgTransport) {
#ifdef DBG_HAS_TCP
        case DBG_T_TCP: return g_dbgSock == DBG_NO_SOCKET ? 0 : dbgSockRecv(g_dbgSock, p, n);
#endif
        case DBG_T_STDIO: return dbgStdinRaw(p, n);
#ifdef FIRE_DEBUG_TRANSPORT_UART
        case DBG_T_UART: return dbgUartRecv(p, n);
#endif
        default: return 0;
    }
}
/* The end of the connection: a socket is closed; stdout and the UART stay what they are. */
static void dbgTransportClose(void) {
#ifdef DBG_HAS_TCP
    if (g_dbgTransport == DBG_T_TCP && g_dbgSock != DBG_NO_SOCKET) { dbgCloseSocket(g_dbgSock); g_dbgSock = DBG_NO_SOCKET; }
#endif
    g_dbgTransport = DBG_T_NONE;
}

/* ---- the state ---- */
enum { DBG_RUNNING, DBG_PAUSED };
enum { DBG_RUN, DBG_END, DBG_INTO, DBG_OVER, DBG_OUT };

typedef struct DbgTask DbgTask;
struct DbgThread {
    DbgThread* next;
    int id;
    char name[32];
    DbgFrame** topp;            /* &g_dbgTop of the thread */
    int state, mode;
    int32_t startLine; uint32_t startDepth;
    int32_t lastSrc, lastLine; int fresh;   /* RUN: the position last seen (a breakpoint counts when it changes); fresh: none seen yet */
    volatile int pauseReq;
    const char* reason;
    int atEntry;                /* waits before the first instruction (no frame yet) */
    Value exc; int hasExc;      /* stopped at an unhandled exception */
    int resumeExc;              /* `resume`: ignore it */
    const void* declined;       /* the exception the user let go on (its rethrows do not stop again) */
    DbgTask* task;              /* `set`: work the stopped thread does itself */
};

typedef struct DbgBuf { char* p; size_t n, cap; } DbgBuf;

struct DbgTask {
    const char* ref;
    const char* literal;
    DbgBuf* out;                /* the answer (a value, or an error text) */
    int ok, done;
};

static dbg_mutex g_dbgM;
static dbg_cond g_dbgCv;
static const DbgProgram* g_dbgProg;
static DbgThread* g_dbgThreads;
static DbgThread g_dbgMain;
static int g_dbgNextId = 1, g_dbgFireCount = 0;
static int g_dbgConnected = 0;
/* A framed transport: the debugger has sent its first command (before that the main thread waits at most FIRE_DEBUG_WAIT_MS); it did not come in time. */
static int g_dbgClaimed = 0, g_dbgGaveUp = 0;
static int64_t g_dbgWaitUntil = 0;
static int g_dbgWaitMs = FIRE_DEBUG_WAIT_MS;
/* The program's standard input while the agent owns the input (a framed transport): what came that was no frame, read by IO (fire_dbgStdinRead). */
typedef struct DbgInput { char* p; size_t n, cap, pos; int eof; } DbgInput;
static DbgInput g_dbgIn;
/* the breakpoints: an immutable list (source, line pairs), replaced as a whole (the old ones are kept: a hook may still read them) */
typedef struct DbgBps { int32_t n; struct DbgBps* older; int32_t* pairs; } DbgBps;
static DbgBps* volatile g_dbgBps;
static int g_dbgBpGen = 0;   /* (counts the `breakpoints` commands) */
#ifdef FIRE_MODULES
/* the plugins with sources (fire_dbgPlugin): their sources come after the program's, their classes are shown with their own tables */
static const FirePlugin* g_dbgPlugins[64];
static int g_dbgPluginCount = 0;
static int32_t g_dbgPluginSources = 0;
#ifndef FIRE_DEBUG_PLUGIN_WAIT_MS
#define FIRE_DEBUG_PLUGIN_WAIT_MS 2000
#endif
#endif
/* The class of an object for the debugger: a plugin's from its table, the program's from fire_dbgClass. */
static const DbgClass* dbgClassOf(uint32_t cls) {
#ifdef FIRE_MODULES
    int i;
    for (i = 0; i < g_dbgPluginCount; i++) {
        const FirePlugin* p = g_dbgPlugins[i];
        if (p->dbgClass && cls >= *p->base && cls < *p->base + p->nclasses) return (const DbgClass*)p->dbgClass(cls - *p->base);
    }
#endif
    return fire_dbgClass(cls);
}

/* ---- JSON out ---- */
static void dbgPutN(DbgBuf* b, const char* s, size_t n) {
    if (b->n + n + 1 > b->cap) {
        size_t cap = b->cap ? b->cap * 2 : 1024;
        char* p;
        while (cap < b->n + n + 1) cap *= 2;
        p = (char*)realloc(b->p, cap);
        if (!p) return;
        b->p = p; b->cap = cap;
    }
    memcpy(b->p + b->n, s, n);
    b->n += n;
    b->p[b->n] = 0;
}
static void dbgPut(DbgBuf* b, const char* s) { dbgPutN(b, s, strlen(s)); }
static void dbgPutI(DbgBuf* b, long long v) { char t[32]; snprintf(t, sizeof t, "%lld", v); dbgPut(b, t); }
/* A JSON string from UTF-8 (C text: names, files): what is not printable ASCII becomes \u of its UTF-16 units. */
static void dbgPutS(DbgBuf* b, const char* s) {
    fire_char16 units[2];
    dbgPut(b, "\"");
    while (s && *s) {
        unsigned c = (unsigned char)*s;
        if (c >= 0x20 && c < 0x7F && c != '"' && c != '\\') { dbgPutN(b, s, 1); s++; continue; }
        if (c < 0x80) {
            char t[8];
            if (c == '"' || c == '\\') { t[0] = '\\'; t[1] = (char)c; dbgPutN(b, t, 2); }
            else { snprintf(t, sizeof t, "\\u%04x", c); dbgPut(b, t); }
            s++;
            continue;
        }
        {
            /* one code point of UTF-8 */
            char one[5] = {0};
            int len = (c >> 5) == 6 ? 2 : (c >> 4) == 14 ? 3 : (c >> 3) == 30 ? 4 : 1, k, n;
            for (k = 0; k < len && s[k]; k++) one[k] = s[k];
            s += k;
            n = (int)utf8ToUtf16(one, units, 4);
            for (k = 0; k < n; k++) { char t[8]; snprintf(t, sizeof t, "\\u%04x", (unsigned)units[k]); dbgPut(b, t); }
        }
    }
    dbgPut(b, "\"");
}
/* A JSON string from UTF-16 (a fire string), at most `max` units (then "..."). Returns whether it was cut. */
static int dbgPutS16(DbgBuf* b, const fire_char16* p, uint32_t n, uint32_t max) {
    uint32_t i;
    int cut = n > max;
    if (cut) n = max;
    dbgPut(b, "\"");
    for (i = 0; i < n; i++) {
        unsigned c = p[i];
        char t[8];
        if (c >= 0x20 && c < 0x7F && c != '"' && c != '\\') { t[0] = (char)c; dbgPutN(b, t, 1); }
        else if (c == '"' || c == '\\') { t[0] = '\\'; t[1] = (char)c; dbgPutN(b, t, 2); }
        else { snprintf(t, sizeof t, "\\u%04x", c); dbgPut(b, t); }
    }
    if (cut) dbgPut(b, "...");
    dbgPut(b, "\"");
    return cut;
}

/* Sends a message (with the lock held); a failed write means the debugger is gone. */
static void dbgSendLine(DbgBuf* b) {
    if (g_dbgTransport == DBG_T_NONE || !b->p) return;
    if (!dbgTransportSend(b->p, b->n)) g_dbgArmed = 0;
}

/* ---- JSON in: a flat object of the command, read on demand ---- */
static const char* dbgSkipWs(const char* p) { while (*p == ' ' || *p == '\t' || *p == '\r' || *p == '\n') p++; return p; }
static const char* dbgSkipValue(const char* p) {
    p = dbgSkipWs(p);
    if (*p == '"') {
        for (p++; *p && *p != '"'; p++) if (*p == '\\' && p[1]) p++;
        return *p ? p + 1 : p;
    }
    if (*p == '[' || *p == '{') {
        int depth = 0;
        for (; *p; p++) {
            if (*p == '"') { p = dbgSkipValue(p) - 1; continue; }
            if (*p == '[' || *p == '{') depth++;
            else if (*p == ']' || *p == '}') { if (--depth == 0) return p + 1; }
        }
        return p;
    }
    while (*p && *p != ',' && *p != '}' && *p != ']' && *p != ' ') p++;
    return p;
}
/* The value of `key` in the object `json` (its first character), null when it is not there. */
static const char* dbgFind(const char* json, const char* key) {
    const char* p = dbgSkipWs(json);
    size_t klen = strlen(key);
    if (*p != '{') return NULL;
    p++;
    for (;;) {
        const char* k;
        p = dbgSkipWs(p);
        if (*p != '"') return NULL;
        k = p + 1;
        p = dbgSkipValue(p);
        {
            int match = (size_t)(p - 1 - k) == klen && strncmp(k, key, klen) == 0;
            p = dbgSkipWs(p);
            if (*p != ':') return NULL;
            p = dbgSkipWs(p + 1);
            if (match) return p;
        }
        p = dbgSkipWs(dbgSkipValue(p));
        if (*p != ',') return NULL;
        p++;
    }
}
static long long dbgInt(const char* json, const char* key, long long def) {
    const char* v = dbgFind(json, key);
    if (!v || !(*v == '-' || (*v >= '0' && *v <= '9'))) return def;
    return strtoll(v, NULL, 10);
}
static int dbgBool(const char* json, const char* key, int def) {
    const char* v = dbgFind(json, key);
    if (!v) return def;
    if (strncmp(v, "true", 4) == 0) return 1;
    if (strncmp(v, "false", 5) == 0) return 0;
    return def;
}
/* A JSON string as UTF-8 into `out` (escapes decoded). false when the key is missing or no string. */
static bool dbgStr(const char* json, const char* key, char* out, size_t cap) {
    const char* v = dbgFind(json, key);
    size_t n = 0;
    if (!v || *v != '"' || cap == 0) return false;
    for (v++; *v && *v != '"' && n + 4 < cap; v++) {
        if (*v != '\\') { out[n++] = *v; continue; }
        v++;
        switch (*v) {
            case 'n': out[n++] = '\n'; break;
            case 't': out[n++] = '\t'; break;
            case 'r': out[n++] = '\r'; break;
            case 'b': out[n++] = '\b'; break;
            case 'f': out[n++] = '\f'; break;
            case 'u': {
                unsigned cp = 0;
                int k;
                for (k = 1; k <= 4 && v[k]; k++) { char c = v[k]; cp = cp * 16 + (unsigned)(c >= 'a' ? c - 'a' + 10 : c >= 'A' ? c - 'A' + 10 : c - '0'); }
                v += 4;
                if (cp >= 0xD800 && cp < 0xDC00 && v[1] == '\\' && v[2] == 'u') {
                    unsigned lo = 0;
                    for (k = 3; k <= 6 && v[k]; k++) { char c = v[k]; lo = lo * 16 + (unsigned)(c >= 'a' ? c - 'a' + 10 : c >= 'A' ? c - 'A' + 10 : c - '0'); }
                    if (lo >= 0xDC00 && lo < 0xE000) { cp = 0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00); v += 6; }
                }
                if (cp < 0x80) out[n++] = (char)cp;
                else if (cp < 0x800) { out[n++] = (char)(0xC0 | (cp >> 6)); out[n++] = (char)(0x80 | (cp & 0x3F)); }
                else if (cp < 0x10000) { out[n++] = (char)(0xE0 | (cp >> 12)); out[n++] = (char)(0x80 | ((cp >> 6) & 0x3F)); out[n++] = (char)(0x80 | (cp & 0x3F)); }
                else { out[n++] = (char)(0xF0 | (cp >> 18)); out[n++] = (char)(0x80 | ((cp >> 12) & 0x3F)); out[n++] = (char)(0x80 | ((cp >> 6) & 0x3F)); out[n++] = (char)(0x80 | (cp & 0x3F)); }
                break;
            }
            default: if (*v) out[n++] = *v; else v--; break;
        }
    }
    out[n] = 0;
    return true;
}

/* ---- threads ---- */
static DbgThread* dbgThreadById(int id) {
    DbgThread* t;
    for (t = g_dbgThreads; t; t = t->next) if (t->id == id) return t;
    return NULL;
}
static void dbgPosition(DbgThread* t, int32_t* src, int32_t* line, uint32_t* depth) {
    DbgFrame* top = *t->topp;
    if (top && !t->atEntry) { *src = top->src; *line = top->line; *depth = top->depth; }
    else { *src = g_dbgProg->entrySrc; *line = g_dbgProg->entryLine; *depth = 0; }
}
static void dbgPutExc(DbgBuf* b, Value e) {
    Obj* o = asObj(e);
    int32_t index = excMessageIndex(o->cls);
    dbgPut(b, "{\"class\":");
    dbgPutS(b, className(o->cls));
    if (index >= 0 && (uint32_t)index < o->nfields && objFields(o)[index].kind == K_String) {
        const Str* s = strOf(objFields(o)[index]);
        dbgPut(b, ",\"message\":");
        dbgPutS16(b, s->data, s->length, 4000);
    }
    dbgPut(b, "}");
}
/* `stopped` (with the lock held). */
static void dbgSendStopped(DbgThread* t) {
    DbgBuf b = {NULL, 0, 0};
    int32_t src, line; uint32_t depth;
    dbgPosition(t, &src, &line, &depth);
    dbgPut(&b, "{\"event\":\"stopped\",\"thread\":"); dbgPutI(&b, t->id);
    dbgPut(&b, ",\"reason\":"); dbgPutS(&b, t->reason);
    dbgPut(&b, ",\"source\":"); dbgPutI(&b, src);
    dbgPut(&b, ",\"line\":"); dbgPutI(&b, line);
    dbgPut(&b, ",\"depth\":"); dbgPutI(&b, depth);
    if (t->hasExc) { dbgPut(&b, ",\"exception\":"); dbgPutExc(&b, t->exc); }
    dbgPut(&b, "}");
    dbgSendLine(&b);
    free(b.p);
}

static void dbgRunTask(DbgThread* t);

/* The thread stops and waits until the debugger lets it go on. `gil`: it holds the GIL (released while it waits, so that the other threads run). */
static void dbgPark(DbgThread* t, const char* reason, int gil) {
    dbgLock(&g_dbgM);
    if (!g_dbgArmed) { dbgUnlock(&g_dbgM); return; }
    t->state = DBG_PAUSED;
    t->reason = reason;
    t->pauseReq = 0;
    dbgSendStopped(t);
#ifdef FIRE_THREADS
    if (gil) gilRelease();
#else
    (void)gil;
#endif
    while (t->state == DBG_PAUSED && g_dbgArmed) {
        if (!g_dbgClaimed) {
            /* a framed transport, the main thread at its entry: the debugger has FIRE_DEBUG_WAIT_MS for its first command, else the program runs without it */
            int64_t left = g_dbgWaitUntil - fire_plat_nowMs();
            if (left > 0) dbgWaitMs(&g_dbgCv, &g_dbgM, (int)left);
            if (!g_dbgClaimed && g_dbgWaitUntil - fire_plat_nowMs() <= 0) {
                g_dbgGaveUp = 1;
                g_dbgArmed = 0;
                fprintf(stderr, "fire debug: no debugger within %d ms - running without one\n", g_dbgWaitMs);
            }
            continue;
        }
        if (t->task) {
            DbgTask* task = t->task;
            dbgUnlock(&g_dbgM);
#ifdef FIRE_THREADS
            gilAcquire();
#endif
            dbgRunTask(t);
#ifdef FIRE_THREADS
            gilRelease();
#endif
            dbgLock(&g_dbgM);
            task->done = 1;
            t->task = NULL;
            dbgBroadcast(&g_dbgCv);
            continue;
        }
        dbgWait(&g_dbgCv, &g_dbgM);
    }
    t->state = DBG_RUNNING;
    dbgUnlock(&g_dbgM);
#ifdef FIRE_THREADS
    if (gil) gilAcquire();
#endif
}

static bool dbgBreakpointAt(int32_t src, int32_t line) {
    DbgBps* bps = g_dbgBps;
    int32_t i;
    if (!bps) return false;
    for (i = 0; i < bps->n; i++) if (bps->pairs[2 * i] == src && bps->pairs[2 * i + 1] == line) return true;
    return false;
}

void fire_dbgLine(DbgFrame* f) {
    DbgThread* t = g_dbgSelf;
    const char* reason = NULL;
    if (!t || g_dbgNest || !g_dbgArmed) return;
    if (t->pauseReq) reason = "pause";
    else switch (t->mode) {
        case DBG_RUN:
            if (t->fresh || f->src != t->lastSrc || f->line != t->lastLine) {
                int fresh = t->fresh;
                t->fresh = 0;
                t->lastSrc = f->src; t->lastLine = f->line;
                if (!fresh && dbgBreakpointAt(f->src, f->line)) reason = "breakpoint";
            }
            break;
        case DBG_INTO: if (f->line != t->startLine || f->depth != t->startDepth) reason = "step"; break;
        case DBG_OVER: if (f->depth <= t->startDepth && f->line != t->startLine) reason = "step"; break;
        case DBG_OUT: if (f->depth < t->startDepth) reason = "step"; break;
        default: break;
    }
    if (reason) dbgPark(t, reason, 1);
}
void fire_dbgRet(DbgFrame* caller) {
    if (g_unwind.active) return;   /* unwinding (an exception, a catch block leaving): the VM goes straight to the handler */
    fire_dbgLine(caller);
}

/* An exception that no catch of this thread takes (fire_rt.h knows that): stop at the throw. 1 = the debugger ignores it (`resume`). */
int fire_dbgUnhandled(Value exception) {
    DbgThread* t = g_dbgSelf;
    int resumed;
    if (!t || !g_dbgArmed || g_dbgNest || exception.p == t->declined) return 0;
    t->exc = exception; t->hasExc = 1; t->resumeExc = 0;
    dbgPark(t, "unhandled", 1);
    resumed = t->resumeExc;
    t->hasExc = 0; t->resumeExc = 0;
    if (!resumed) t->declined = exception.p;
    return resumed;
}

/* ---- values ---- */
static const char* dbgKindName(Value v) {
    switch (v.kind) {
        case K_Bool: return "bool";
        case K_Int: return "int";
        case K_Word: return "word";
        case K_Float: return "float";
        case K_Char: return "char";
        case K_String: return "string";
        case K_Class: return "object";
        case K_Lambda: return "lambda";
        case K_PtrVar: case K_PtrByte: case K_PtrElem: return "pointer";
        case K_Array: return "array";
        case K_Buffer: return "buffer";
        default: return "undefined";
    }
}
static bool dbgAlive(Value v) {
    if (v.kind != K_Class && v.kind != K_Array && v.kind != K_Buffer) return true;
    if (!v.p) return false;
    return leafAlive(v);
}
/* One value (V of the protocol): name, kind, display, ref. */
static void dbgPutValue(DbgBuf* b, const char* name, Value v, const char* ref) {
    dbgPut(b, "{\"name\":"); dbgPutS(b, name);
    dbgPut(b, ",\"kind\":"); dbgPutS(b, dbgKindName(v));
    if (v.kind == K_Class) {
        if (!dbgAlive(v)) dbgPut(b, ",\"display\":\"<destroyed>\"");
        else {
            Obj* o = objAt(v);
            dbgPut(b, ",\"class\":"); dbgPutS(b, className(o->cls));
            dbgPut(b, ",\"display\":"); dbgPutS(b, className(o->cls));
            if (o->nfields > 0) dbgPut(b, ",\"expandable\":true");
        }
    }
    else if (v.kind == K_Array) {
        if (!dbgAlive(v)) dbgPut(b, ",\"display\":\"<destroyed>\"");
        else {
            char t[40];
            uint32_t n = arrOf(v)->length;
            snprintf(t, sizeof t, "Array[%u]", (unsigned)n);
            dbgPut(b, ",\"length\":"); dbgPutI(b, n);
            dbgPut(b, ",\"display\":"); dbgPutS(b, t);
            if (n > 0) dbgPut(b, ",\"expandable\":true");
        }
    }
    else if (v.kind == K_String) {
        const Str* s = strOf(v);
        dbgPut(b, ",\"display\":");
        if (dbgPutS16(b, s->data, s->length, 4000)) dbgPut(b, ",\"truncated\":true");
    }
    else if (v.kind == K_Buffer && !dbgAlive(v)) dbgPut(b, ",\"display\":\"<destroyed>\"");
    else {
        Piece piece;
        pieceOf(v, &piece, NULL);   /* (not an object: no fire code, no allocation) */
        dbgPut(b, ",\"display\":");
        dbgPutS16(b, piece.p, piece.n, 4000);
    }
    dbgPut(b, ",\"ref\":"); dbgPutS(b, ref);
    dbgPut(b, "}");
}

/* The level `depth` of the scope chain of a frame (0 = the innermost), null when there is none. */
static const DbgLevel* dbgLevel(DbgFrame* f, int depth) {
    int32_t lv;
    if (!f || !f->fn || !f->fn->levels) return NULL;
    lv = f->lv;
    while (lv >= 0 && depth > 0) { lv = f->fn->levels[lv].parent; depth--; }
    return lv >= 0 && f->fn->levels[lv].functionTop != 2 ? &f->fn->levels[lv] : NULL;
}
/* The bottom frame of a thread and the level of its own globals (a fire body), null when it has none. */
static const DbgLevel* dbgThreadGlobals(DbgThread* t, DbgFrame** bottom) {
    DbgFrame* f = t->atEntry ? NULL : *t->topp;
    int32_t lv;
    if (!f) return NULL;
    while (f->prev) f = f->prev;
    *bottom = f;
    if (!f->fn || !f->fn->levels || f->lv < 0) return NULL;
    for (lv = f->lv; f->fn->levels[lv].parent >= 0; lv = f->fn->levels[lv].parent) {}
    return f->fn->levels[lv].functionTop == 2 ? &f->fn->levels[lv] : NULL;
}

/* Resolves a ref (docs/spec/NATIVE_DEBUG_PROTOCOL.md "Werte"): the slot of the value, or null with a reason. `thread` is the thread a `l:`/`t:` ref names. */
static Value* dbgResolveField(const char* ref, const char** error, DbgThread** thread, Value* object, long* field);
static Value* dbgResolve(const char* ref, const char** error, DbgThread** thread) {
    Value object;
    long field;
    return dbgResolveField(ref, error, thread, &object, &field);
}
/* Like dbgResolve; when the last step is a field of an object, that object and the index of the field (else `field` is -1). */
static Value* dbgResolveField(const char* ref, const char** error, DbgThread** thread, Value* object, long* field) {
    Value* slot = NULL;
    const char* p = ref;
    *thread = NULL;
    *field = -1;
    if (p[0] == 'g' && p[1] == ':') {
        long n = strtol(p + 2, (char**)&p, 10);
        if (n < 0 || n >= g_dbgProg->nglobals || n >= g_dbgGlobalCount || !g_dbgProg->globals[n].slot) { *error = "no such global"; return NULL; }
        slot = g_dbgProg->globals[n].slot;
    }
    else if (p[0] == 'T' && p[1] == ':') {
        long id = strtol(p + 2, (char**)&p, 10), index;
        DbgThread* t = dbgThreadById((int)id);
        DbgFrame* bottom = NULL;
        const DbgLevel* level;
        if (!t || t->state != DBG_PAUSED) { *error = "the thread is not paused"; return NULL; }
        *thread = t;
        if (*p != ':') { *error = "bad ref"; return NULL; }
        index = strtol(p + 1, (char**)&p, 10);
        level = dbgThreadGlobals(t, &bottom);
        if (!level || index < 0 || index >= level->count || !bottom->vars || level->vars[index].index == 0xFFFF) { *error = "no such variable"; return NULL; }
        slot = bottom->vars[level->vars[index].index];
    }
    else if ((p[0] == 'l' || p[0] == 't') && p[1] == ':') {
        char kind = p[0];
        long id = strtol(p + 2, (char**)&p, 10);
        DbgThread* t = dbgThreadById((int)id);
        DbgFrame* f;
        if (!t || t->state != DBG_PAUSED) { *error = "the thread is not paused"; return NULL; }
        *thread = t;
        f = t->atEntry ? NULL : *t->topp;
        if (kind == 't') {
            if (!f || !f->thisSlot) { *error = "no this"; return NULL; }
            slot = f->thisSlot;
        }
        else {
            long depth, index;
            const DbgLevel* level;
            if (*p != ':') { *error = "bad ref"; return NULL; }
            depth = strtol(p + 1, (char**)&p, 10);
            if (*p != ':') { *error = "bad ref"; return NULL; }
            index = strtol(p + 1, (char**)&p, 10);
            level = dbgLevel(f, (int)depth);
            if (!level || index < 0 || index >= level->count || !f->vars || level->vars[index].index == 0xFFFF) { *error = "no such variable"; return NULL; }
            slot = f->vars[level->vars[index].index];
        }
    }
    else { *error = "bad ref"; return NULL; }
    while (*p == '/') {
        long k = strtol(p + 1, (char**)&p, 10);
        Value v = *slot;
        if (!dbgAlive(v)) { *error = "the value is gone"; return NULL; }
        *field = -1;
        if (v.kind == K_Class && k >= 0 && (uint32_t)k < objAt(v)->nfields) { slot = &objFields(objAt(v))[k]; *object = v; *field = k; }
        else if (v.kind == K_Array && k >= 0 && (uint32_t)k < arrOf(v)->length) slot = &arrItems(arrOf(v))[k];
        else { *error = "the value is gone"; return NULL; }
    }
    if (*p) { *error = "bad ref"; return NULL; }
    return slot;
}

/* ---- setting a value (`set`): done by the stopped thread itself, like an assignment of the program ---- */
static bool dbgParseLiteral(const char* text, Value old, Value* out, OwnList* list) {
    const char* p = text;
    while (*p == ' ') p++;
    if (strcmp(p, "true") == 0 || strcmp(p, "True") == 0) { *out = Bool(true); return true; }
    if (strcmp(p, "false") == 0 || strcmp(p, "False") == 0) { *out = Bool(false); return true; }
    if (strcmp(p, "undefined") == 0) { *out = Undef(); return true; }
    if (*p == '"') {
        /* a string literal: \" \\ \n \t */
        size_t n = strlen(p), i, k = 0;
        char* utf8 = (char*)malloc(n + 1);
        fire_char16* units;
        uint32_t count;
        Value s;
        if (!utf8) return false;
        for (i = 1; i < n && p[i] != '"'; i++) {
            if (p[i] == '\\' && i + 1 < n) {
                i++;
                utf8[k++] = p[i] == 'n' ? '\n' : p[i] == 't' ? '\t' : p[i];
            }
            else utf8[k++] = p[i];
        }
        utf8[k] = 0;
        units = (fire_char16*)malloc((k + 2) * sizeof(fire_char16));
        if (!units) { free(utf8); return false; }
        count = utf8ToUtf16(utf8, units, (uint32_t)k + 2);
        s = newStrFrom(units, count, list);
        free(units);
        free(utf8);
        *out = s;
        return true;
    }
    {
        char* end;
        uint32_t unit = (old.kind == K_Int || old.kind == K_Float || old.kind == K_Undefined) ? old.unit : 0;
        long long i = strtoll(p, &end, 10);
        if (end != p && *end == 0) { *out = Int((int64_t)i, unit); return true; }
        {
            double d = strtod(p, &end);
            if (end != p && *end == 0) { *out = Float((Real)d, unit); return true; }
        }
    }
    return false;
}
/* The catch block of `set`: an exception of a setter (the declared kind of a field, a probe) is the answer. */
static Value g_dbgSetExc;
static void dbgSetCatch(void* ctx, uint32_t ci, Value e) { (void)ci; g_dbgSetExc = e; clearPending(e); exitJump((Handler*)ctx, 0); }
static void dbgRunTask(DbgThread* t) {
    static const int32_t anything[] = {-1};
    static const HandlerInfo catchAll = {1, anything, 0, NULL};
    DbgTask* task = t->task;
    const char* error = NULL;
    DbgThread* owner;
    Value object;
    long field;
    Value* slot = dbgResolveField(task->ref, &error, &owner, &object, &field);
    Value nv, old;
    if (!slot) { dbgPutS(task->out, error); return; }
    old = *slot;
    if (!dbgParseLiteral(task->literal, old, &nv, g_globalOwn)) { dbgPutS(task->out, "not a literal (a number, true, false, undefined or a string in quotes)"); return; }
    if (field >= 0) {
        /* a field: its setter, under a catch block of our own (an exception does not reach the program) */
        Handler h;
        h.info = &catchAll; h.fn = dbgSetCatch; h.ctx = &h;
        pushHandler(&h);
        g_dbgNest++;
        fire_dbgSetField(object, (uint32_t)field, nv);
        g_dbgNest--;
        if (g_handlers == &h) popHandler();
        if (g_unwind.active) {
            Obj* o = g_dbgSetExc.kind == K_Class ? asObj(g_dbgSetExc) : NULL;
            int32_t index = o ? excMessageIndex(o->cls) : -1;
            clearUnwind();
            if (o && index >= 0 && objFields(o)[index].kind == K_String) dbgPutS16(task->out, strOf(objFields(o)[index])->data, strOf(objFields(o)[index])->length, 4000);
            else dbgPutS(task->out, "the setter threw an exception");
            return;
        }
        nv = *slot;
    }
    else {
        retain(nv);
        *slot = nv;
        release(old);
    }
    dbgPutValue(task->out, "", nv, task->ref);
    task->ok = 1;
}

/* ---- commands ---- */
static void dbgReplyHead(DbgBuf* b, long long id, int ok) {
    dbgPut(b, "{\"re\":"); dbgPutI(b, id);
    dbgPut(b, ok ? ",\"ok\":true" : ",\"ok\":false");
}
static void dbgReplyError(long long id, const char* error) {
    DbgBuf b = {NULL, 0, 0};
    dbgReplyHead(&b, id, 0);
    dbgPut(&b, ",\"error\":"); dbgPutS(&b, error);
    dbgPut(&b, "}");
    dbgSendLine(&b);
    free(b.p);
}

/* Lets a stopped thread go on in a mode (with the lock held). */
static void dbgGo(DbgThread* t, int mode) {
    int32_t src, line; uint32_t depth;
    dbgPosition(t, &src, &line, &depth);
    t->mode = mode;
    t->startLine = line; t->startDepth = depth;
    t->lastSrc = src; t->lastLine = line; t->fresh = 0;
    t->atEntry = 0;
    t->state = DBG_RUNNING;
}

static void dbgWriteFrames(DbgBuf* b, DbgThread* t) {
    DbgFrame* f;
    int first = 1;
    dbgPut(b, ",\"frames\":[");
    if (t->atEntry || !*t->topp) {
        dbgPut(b, "{\"function\":\"<main>\",\"source\":"); dbgPutI(b, g_dbgProg->entrySrc);
        dbgPut(b, ",\"line\":"); dbgPutI(b, g_dbgProg->entryLine); dbgPut(b, "}");
    }
    else
        for (f = *t->topp; f; f = f->prev) {
            if (!first) dbgPut(b, ",");
            first = 0;
            dbgPut(b, "{\"function\":"); dbgPutS(b, f->fn ? f->fn->name : "?");
            dbgPut(b, ",\"source\":"); dbgPutI(b, f->src);
            dbgPut(b, ",\"line\":"); dbgPutI(b, f->line);
            dbgPut(b, "}");
        }
    dbgPut(b, "]");
}

static void dbgWriteScopes(DbgBuf* b, DbgThread* t) {
    DbgFrame* f = t->atEntry ? NULL : *t->topp;
    int depth;
    dbgPut(b, ",\"scopes\":[");
    for (depth = 0; ; depth++) {
        const DbgLevel* level = dbgLevel(f, depth);
        uint16_t i;
        if (!level) break;
        if (depth) dbgPut(b, ",");
        dbgPut(b, "{\"depth\":"); dbgPutI(b, depth);
        dbgPut(b, level->functionTop ? ",\"functionTop\":true" : ",\"functionTop\":false");
        dbgPut(b, ",\"variables\":[");
        for (i = 0; i < level->count; i++) {
            char ref[64];
            snprintf(ref, sizeof ref, "l:%d:%d:%u", t->id, depth, (unsigned)i);
            if (i) dbgPut(b, ",");
            dbgPutValue(b, level->vars[i].name, f->vars && level->vars[i].index != 0xFFFF ? *f->vars[level->vars[i].index] : Undef(), ref);
        }
        dbgPut(b, "]}");
    }
    dbgPut(b, "]");
}

static void dbgWriteGlobals(DbgBuf* b, DbgThread* thread) {
    int32_t i;
    int any = 0;
    dbgPut(b, ",\"variables\":[");
    if (thread) {
        /* the variables of the top level of a fire thread (its own globals), first */
        DbgFrame* bottom = NULL;
        const DbgLevel* level = dbgThreadGlobals(thread, &bottom);
        uint16_t k;
        for (k = 0; level && k < level->count; k++) {
            char ref[48];
            snprintf(ref, sizeof ref, "T:%d:%u", thread->id, (unsigned)k);
            if (any++) dbgPut(b, ",");
            dbgPutValue(b, level->vars[k].name, bottom->vars && level->vars[k].index != 0xFFFF ? *bottom->vars[level->vars[k].index] : Undef(), ref);
        }
    }
    for (i = 0; i < g_dbgGlobalCount && i < g_dbgProg->nglobals; i++) {
        char ref[32], name[32];
        const DbgGlobal* g = &g_dbgProg->globals[i];
        snprintf(ref, sizeof ref, "g:%d", (int)i);
        if (!g->name) snprintf(name, sizeof name, "(global %d)", (int)i);
        if (any++) dbgPut(b, ",");
        dbgPutValue(b, g->name ? g->name : name, g->slot ? *g->slot : Undef(), ref);
    }
    dbgPut(b, "]");
}

static void dbgWriteChildren(DbgBuf* b, Value v, const char* ref) {
    char child[256];
    uint32_t i;
    dbgPut(b, ",\"children\":[");
    if (v.kind == K_Class) {
        Obj* o = objAt(v);
        const DbgClass* cls = dbgClassOf(o->cls);
        for (i = 0; i < o->nfields; i++) {
            char name[32];
            if (i) dbgPut(b, ",");
            snprintf(child, sizeof child, "%s/%u", ref, (unsigned)i);
            if (!cls || i >= cls->nfields) snprintf(name, sizeof name, "(field %u)", (unsigned)i);
            dbgPutValue(b, cls && i < cls->nfields ? cls->fields[i] : name, objFields(o)[i], child);
        }
    }
    else if (v.kind == K_Array) {
        Arr* a = arrOf(v);
        for (i = 0; i < a->length; i++) {
            char name[24];
            if (i) dbgPut(b, ",");
            snprintf(child, sizeof child, "%s/%u", ref, (unsigned)i);
            snprintf(name, sizeof name, "[%u]", (unsigned)i);
            dbgPutValue(b, name, arrItems(a)[i], child);
        }
    }
    dbgPut(b, "]");
}

static void dbgCommand(const char* json) {
    char cmd[32];
    long long id = dbgInt(json, "id", -1);
    int threadId = (int)dbgInt(json, "thread", 0);
    DbgBuf b = {NULL, 0, 0};
    bool reads;
    if (!dbgStr(json, "cmd", cmd, sizeof cmd)) { dbgLock(&g_dbgM); dbgReplyError(id, "no cmd"); dbgUnlock(&g_dbgM); return; }
    /* the commands that read values hold the GIL (no other thread changes them meanwhile; the agent itself never runs fire code) */
    reads = strcmp(cmd, "scopes") == 0 || strcmp(cmd, "globals") == 0 || strcmp(cmd, "this") == 0 || strcmp(cmd, "expand") == 0;
    (void)reads;
#ifdef FIRE_THREADS
    if (reads) gilAcquire();
#endif
    dbgLock(&g_dbgM);
    if (strcmp(cmd, "breakpoints") == 0) {
        const char* p = dbgFind(json, "breakpoints");
        DbgBps* bps = (DbgBps*)calloc(1, sizeof(DbgBps));
        int32_t cap = 16;
        if (bps) bps->pairs = (int32_t*)malloc((size_t)cap * 2 * sizeof(int32_t));
        if (!bps || !bps->pairs) { free(bps); dbgReplyError(id, "out of memory"); goto done; }
        if (p && *p == '[') {
            p++;
            for (;;) {
                long s, l;
                p = dbgSkipWs(p);
                if (*p != '[') break;
                s = strtol(dbgSkipWs(p + 1), (char**)&p, 10);
                p = dbgSkipWs(p);
                if (*p == ',') p++;
                l = strtol(dbgSkipWs(p), (char**)&p, 10);
                p = dbgSkipWs(p);
                if (*p == ']') p++;
                if (bps->n == cap) {
                    int32_t* more = (int32_t*)realloc(bps->pairs, (size_t)cap * 4 * sizeof(int32_t));
                    if (!more) break;
                    bps->pairs = more; cap *= 2;
                }
                bps->pairs[2 * bps->n] = (int32_t)s; bps->pairs[2 * bps->n + 1] = (int32_t)l; bps->n++;
                p = dbgSkipWs(p);
                if (*p == ',') p++;
            }
        }
        bps->older = g_dbgBps;
        g_dbgBps = bps;
        g_dbgBpGen++;
        dbgBroadcast(&g_dbgCv);   /* (a thread that registers a plugin waits for them: fire_dbgPlugin) */
        dbgReplyHead(&b, id, 1); dbgPut(&b, "}"); dbgSendLine(&b);
    }
    else if (strcmp(cmd, "continue") == 0 || strcmp(cmd, "step") == 0) {
        int mode = DBG_RUN;
        DbgThread* t;
        if (strcmp(cmd, "step") == 0) {
            char kind[16];
            if (!dbgStr(json, "kind", kind, sizeof kind)) kind[0] = 0;
            mode = strcmp(kind, "into") == 0 ? DBG_INTO : strcmp(kind, "out") == 0 ? DBG_OUT : strcmp(kind, "line") == 0 ? DBG_OVER : -1;
            if (mode < 0) { dbgReplyError(id, "kind must be line, into or out"); goto done; }
        }
        else if (!dbgBool(json, "breakpoints", 1)) mode = DBG_END;
        if (threadId == 0 && (mode == DBG_RUN || mode == DBG_END)) {   /* continue without a thread: all that are paused */
            for (t = g_dbgThreads; t; t = t->next) if (t->state == DBG_PAUSED) dbgGo(t, mode);
            dbgReplyHead(&b, id, 1); dbgPut(&b, "}"); dbgSendLine(&b);
            dbgBroadcast(&g_dbgCv);
            goto done;
        }
        t = dbgThreadById(threadId);
        if (!t || t->state != DBG_PAUSED) { dbgReplyError(id, "the thread is not paused"); goto done; }
        if (mode == DBG_OUT) {
            int32_t src, line; uint32_t depth;
            dbgPosition(t, &src, &line, &depth);
            if (depth == 0) { dbgReplyError(id, "step out at depth 0"); goto done; }
        }
        dbgGo(t, mode);
        dbgReplyHead(&b, id, 1); dbgPut(&b, "}"); dbgSendLine(&b);
        dbgBroadcast(&g_dbgCv);
    }
    else if (strcmp(cmd, "pause") == 0) {
        DbgThread* t;
        for (t = g_dbgThreads; t; t = t->next) if ((threadId == 0 || t->id == threadId) && t->state == DBG_RUNNING) t->pauseReq = 1;
        dbgReplyHead(&b, id, 1); dbgPut(&b, "}"); dbgSendLine(&b);
    }
    else if (strcmp(cmd, "threads") == 0) {
        DbgThread* t;
        dbgReplyHead(&b, id, 1);
        dbgPut(&b, ",\"threads\":[");
        for (t = g_dbgThreads; t; t = t->next) {
            dbgPut(&b, "{\"id\":"); dbgPutI(&b, t->id);
            dbgPut(&b, ",\"name\":"); dbgPutS(&b, t->name);
            if (t->state == DBG_PAUSED) {
                int32_t src, line; uint32_t depth;
                dbgPosition(t, &src, &line, &depth);
                dbgPut(&b, ",\"state\":\"paused\",\"reason\":"); dbgPutS(&b, t->reason);
                dbgPut(&b, ",\"source\":"); dbgPutI(&b, src);
                dbgPut(&b, ",\"line\":"); dbgPutI(&b, line);
                dbgPut(&b, ",\"depth\":"); dbgPutI(&b, depth);
            }
            else dbgPut(&b, ",\"state\":\"running\"");
            dbgPut(&b, t->next ? "}," : "}");
        }
        dbgPut(&b, "]}");
        dbgSendLine(&b);
    }
    else if (strcmp(cmd, "trace") == 0 || strcmp(cmd, "scopes") == 0 || strcmp(cmd, "this") == 0) {
        DbgThread* t = dbgThreadById(threadId);
        if (!t || t->state != DBG_PAUSED) { dbgReplyError(id, "the thread is not paused"); goto done; }
        dbgReplyHead(&b, id, 1);
        if (cmd[0] == 't' && cmd[1] == 'r') {
            int32_t src, line; uint32_t depth;
            dbgPosition(t, &src, &line, &depth);
            dbgPut(&b, ",\"depth\":"); dbgPutI(&b, depth);
            dbgWriteFrames(&b, t);
        }
        else if (cmd[0] == 's') dbgWriteScopes(&b, t);
        else {
            DbgFrame* f = t->atEntry ? NULL : *t->topp;
            dbgPut(&b, ",\"value\":");
            if (f && f->thisSlot && f->thisSlot->kind != K_Undefined) {
                char ref[32];
                snprintf(ref, sizeof ref, "t:%d", t->id);
                dbgPutValue(&b, "this", *f->thisSlot, ref);
            }
            else dbgPut(&b, "null");
        }
        dbgPut(&b, "}");
        dbgSendLine(&b);
    }
    else if (strcmp(cmd, "globals") == 0) {
#ifndef FIRE_THREADS
        if (g_dbgMain.state != DBG_PAUSED) { dbgReplyError(id, "the program is running"); goto done; }
#endif
        DbgThread* t = threadId ? dbgThreadById(threadId) : NULL;
        if (threadId && (!t || t->state != DBG_PAUSED)) { dbgReplyError(id, "the thread is not paused"); goto done; }
        dbgReplyHead(&b, id, 1);
        dbgWriteGlobals(&b, t);
        dbgPut(&b, "}");
        dbgSendLine(&b);
    }
    else if (strcmp(cmd, "expand") == 0) {
        char ref[200];
        const char* error = "bad ref";
        DbgThread* owner;
        Value* slot = dbgStr(json, "ref", ref, sizeof ref) ? dbgResolve(ref, &error, &owner) : NULL;
#ifndef FIRE_THREADS
        if (slot && !owner && g_dbgMain.state != DBG_PAUSED) { slot = NULL; error = "the program is running"; }
#endif
        if (!slot) { dbgReplyError(id, error); goto done; }
        if (!dbgAlive(*slot)) { dbgReplyError(id, "the value is gone"); goto done; }
        dbgReplyHead(&b, id, 1);
        dbgWriteChildren(&b, *slot, ref);
        dbgPut(&b, "}");
        dbgSendLine(&b);
    }
    else if (strcmp(cmd, "set") == 0) {
        static char ref[200], literal[4096];
        DbgTask task;
        DbgBuf out = {NULL, 0, 0};
        DbgThread* t = NULL;
        if (!dbgStr(json, "ref", ref, sizeof ref) || !dbgStr(json, "value", literal, sizeof literal)) { dbgReplyError(id, "set needs ref and value"); goto done; }
        if (threadId) t = dbgThreadById(threadId);
        else for (t = g_dbgThreads; t && t->state != DBG_PAUSED; t = t->next) {}
        if (!t || t->state != DBG_PAUSED) { dbgReplyError(id, "the thread is not paused"); goto done; }
        task.ref = ref; task.literal = literal; task.out = &out; task.ok = 0; task.done = 0;
        t->task = &task;
        dbgBroadcast(&g_dbgCv);
        while (!task.done && g_dbgArmed) dbgWait(&g_dbgCv, &g_dbgM);
        dbgReplyHead(&b, id, task.ok);
        dbgPut(&b, task.ok ? ",\"value\":" : ",\"error\":");
        if (out.p) dbgPut(&b, out.p); else dbgPut(&b, "\"\"");
        dbgPut(&b, "}");
        dbgSendLine(&b);
        free(out.p);
    }
    else if (strcmp(cmd, "resume") == 0) {
        DbgThread* t = dbgThreadById(threadId);
        if (!t || t->state != DBG_PAUSED || !t->hasExc) { dbgReplyError(id, "the thread is not stopped at an unhandled exception"); goto done; }
        t->resumeExc = 1;
        t->hasExc = 0;
        t->reason = "step";
        dbgReplyHead(&b, id, 1); dbgPut(&b, "}"); dbgSendLine(&b);
    }
    else if (strcmp(cmd, "terminate") == 0) {
        int code = (int)dbgInt(json, "code", 1);
        dbgReplyHead(&b, id, 1); dbgPut(&b, "}"); dbgSendLine(&b);
        free(b.p); b.p = NULL; b.n = 0; b.cap = 0;
        dbgPut(&b, "{\"event\":\"exited\",\"code\":"); dbgPutI(&b, code); dbgPut(&b, "}");
        dbgSendLine(&b);
        dbgTransportClose();
        fflush(stdout);
        fflush(stderr);
#if defined(ESP_PLATFORM) && defined(FIRE_DEBUG_TRANSPORT_UART)
        (void)code;
        esp_restart();   /* (a board has no process to end: it starts again - and runs without the debugger once the wait for it is over) */
#else
        _Exit(code);
#endif
    }
    else if (strcmp(cmd, "detach") == 0) {
        dbgReplyHead(&b, id, 1); dbgPut(&b, "}"); dbgSendLine(&b);
        g_dbgArmed = 0;
        dbgBroadcast(&g_dbgCv);
    }
    else dbgReplyError(id, "unknown command");
done:
    dbgUnlock(&g_dbgM);
#ifdef FIRE_THREADS
    if (reads) gilRelease();
#endif
    free(b.p);
}

/* ---- the program's standard input over a framed transport (the agent reads it; IO reads from here) ---- */
static void dbgInputPut(const char* p, size_t n, int eof) {
    if (n > 0) {
        if (g_dbgIn.pos > 0 && g_dbgIn.pos == g_dbgIn.n) { g_dbgIn.pos = 0; g_dbgIn.n = 0; }   /* (all read: start at the front again) */
        if (g_dbgIn.n + n > g_dbgIn.cap) {
            size_t cap = g_dbgIn.cap ? g_dbgIn.cap * 2 : 512;
            char* q;
            while (cap < g_dbgIn.n + n) cap *= 2;
            q = (char*)realloc(g_dbgIn.p, cap);
            if (!q) return;
            g_dbgIn.p = q; g_dbgIn.cap = cap;
        }
        memcpy(g_dbgIn.p + g_dbgIn.n, p, n);
        g_dbgIn.n += n;
    }
    if (eof) g_dbgIn.eof = 1;
    dbgBroadcast(&g_dbgCv);
}
int64_t fire_dbgStdinRead(uint8_t* out, int64_t count) {
    int64_t n;
    if (count <= 0) return 0;
#ifdef FIRE_THREADS
    gilRelease();   /* (waiting for input: the other threads run) */
#endif
    dbgLock(&g_dbgM);
    while (g_dbgIn.pos == g_dbgIn.n && !g_dbgIn.eof) dbgWait(&g_dbgCv, &g_dbgM);
    n = (int64_t)(g_dbgIn.n - g_dbgIn.pos);
    if (n > count) n = count;
    if (n > 0) memcpy(out, g_dbgIn.p + g_dbgIn.pos, (size_t)n);
    g_dbgIn.pos += (size_t)n;
    dbgUnlock(&g_dbgM);
#ifdef FIRE_THREADS
    gilAcquire();
#endif
    return n;
}
void fire_dbgStdinUnget(int c) {
    dbgLock(&g_dbgM);
    if (c >= 0 && g_dbgIn.pos > 0) g_dbgIn.p[--g_dbgIn.pos] = (char)c;
    dbgUnlock(&g_dbgM);
}

/* A frame that came: `FD:<json>*<crc>` (after the \x1E). A garbled one (no prefix, a wrong CRC) is dropped. */
static void dbgFrame(char* line, size_t n) {
    char* star;
    unsigned crc;
    if (n < 9 || memcmp(line, "FD:", 3) != 0) return;
    line[n] = 0;
    star = strrchr(line, '*');
    if (!star || strlen(star + 1) != 4) return;
    crc = (unsigned)strtoul(star + 1, NULL, 16);
    *star = 0;
    if (crc != dbgCrc16(line + 3, (size_t)(star - line - 3))) return;
    dbgLock(&g_dbgM);
    if (g_dbgGaveUp) { dbgUnlock(&g_dbgM); return; }   /* (too late: the program runs without a debugger) */
    if (!g_dbgClaimed) { g_dbgClaimed = 1; dbgBroadcast(&g_dbgCv); }
    dbgUnlock(&g_dbgM);
    dbgCommand(line + 3);
}

/* The thread of the agent: reads the commands (and, framed, the program's standard input). The end of the connection detaches: the program runs on
 * without a debugger. */
static void dbgAgentMain(void) {
    char chunk[512];
    DbgBuf line = {NULL, 0, 0};
    int framed = g_dbgTransport != DBG_T_TCP;
    int inFrame = 0, lineStart = 1;
    for (;;) {
        int n = dbgTransportRecv(chunk, sizeof chunk);
        int i, start = 0;
        if (n <= 0) break;
        if (!framed) {
            for (i = 0; i < n; i++)
                if (chunk[i] == '\n') {
                    dbgPutN(&line, chunk + start, (size_t)(i - start));
                    if (line.n > 0 && line.p) dbgCommand(line.p);
                    line.n = 0;
                    if (line.p) line.p[0] = 0;
                    start = i + 1;
                }
            if (start < n) dbgPutN(&line, chunk + start, (size_t)(n - start));
            continue;
        }
        /* framed: a line that begins with \x1E is a frame; every other byte is the program's input, passed on at once */
        {
            int plain = -1;   /* the start of the run of input bytes in this chunk */
            for (i = 0; i < n; i++) {
                char c = chunk[i];
                if (inFrame) {
                    if (c == '\n') {
                        inFrame = 0;
                        lineStart = 1;
                        if (line.p && line.n < 8192) dbgFrame(line.p, line.n);
                        line.n = 0;
                    }
                    else if (c != '\r') dbgPutN(&line, &c, 1);
                    continue;
                }
                if (lineStart && c == 0x1E) {
                    if (plain >= 0) { dbgLock(&g_dbgM); dbgInputPut(chunk + plain, (size_t)(i - plain), 0); dbgUnlock(&g_dbgM); plain = -1; }
                    inFrame = 1;
                    line.n = 0;
                    continue;
                }
                if (plain < 0) plain = i;
                lineStart = c == '\n';
            }
            if (plain >= 0) { dbgLock(&g_dbgM); dbgInputPut(chunk + plain, (size_t)(n - plain), 0); dbgUnlock(&g_dbgM); }
        }
    }
    free(line.p);
    dbgLock(&g_dbgM);
    g_dbgArmed = 0;
    if (framed) dbgInputPut(NULL, 0, 1);   /* (the end of the input) */
    dbgBroadcast(&g_dbgCv);
    dbgUnlock(&g_dbgM);
}

/* Registers the calling thread (the lock held). */
static void dbgRegister(DbgThread* t, const char* name) {
    t->id = g_dbgNextId++;
    snprintf(t->name, sizeof t->name, "%s", name);
    t->topp = &g_dbgTop;
    t->state = DBG_RUNNING;
    t->mode = DBG_RUN;
    t->fresh = 1;
    t->next = NULL;
    if (!g_dbgThreads) g_dbgThreads = t;
    else { DbgThread* last = g_dbgThreads; while (last->next) last = last->next; last->next = t; }
    g_dbgSelf = t;
}

#ifdef DBG_HAS_TCP
/* `tcp:HOST:PORT`: connects to the debugger. false: no connection (the program runs without one). */
static bool dbgTcpOpen(const char* p) {
    char host[64] = "127.0.0.1";
    int port;
    struct sockaddr_in addr;
    const char* colon = strrchr(p, ':');
    const char* semi = strchr(p, ';');
    if (!colon || (semi && colon > semi)) { fprintf(stderr, "fire debug: FIRE_DEBUG must be tcp:HOST:PORT;token=T\n"); return false; }
    if ((size_t)(colon - p) < sizeof host) { memcpy(host, p, (size_t)(colon - p)); host[colon - p] = 0; }
    port = atoi(colon + 1);
#if defined(_WIN32)
    {
        WSADATA wsa;
        if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) { fprintf(stderr, "fire debug: cannot start Winsock\n"); return false; }
    }
#endif
    g_dbgSock = socket(AF_INET, SOCK_STREAM, 0);
    memset(&addr, 0, sizeof addr);
    addr.sin_family = AF_INET;
    addr.sin_port = htons((unsigned short)port);
    if (g_dbgSock == DBG_NO_SOCKET || inet_pton(AF_INET, host, &addr.sin_addr) != 1 || connect(g_dbgSock, (struct sockaddr*)&addr, sizeof addr) != 0) {
        fprintf(stderr, "fire debug: cannot connect to %s:%d - running without a debugger\n", host, port);
        if (g_dbgSock != DBG_NO_SOCKET) dbgCloseSocket(g_dbgSock);
        g_dbgSock = DBG_NO_SOCKET;
        return false;
    }
    {
        int one = 1;
        setsockopt(g_dbgSock, IPPROTO_TCP, TCP_NODELAY, (const char*)&one, sizeof one);
    }
    g_dbgTransport = DBG_T_TCP;
    return true;
}
#endif

#if !defined(FIRE_DEBUG_TRANSPORT_UART) && !defined(FIRE_DEBUG_TRANSPORT_STDIO)
/* The value of `;key=` in the FIRE_DEBUG text (up to the next `;`), into `out`. */
static bool dbgOption(const char* spec, const char* key, char* out, size_t cap) {
    size_t klen = strlen(key);
    const char* p = spec;
    while ((p = strchr(p, ';')) != NULL) {
        p++;
        if (strncmp(p, key, klen) == 0 && p[klen] == '=') {
            size_t n = 0;
            p += klen + 1;
            while (p[n] && p[n] != ';' && n + 1 < cap) { out[n] = p[n]; n++; }
            out[n] = 0;
            return true;
        }
    }
    return false;
}
#endif

/* Starts the agent: opens the transport (FIRE_DEBUG on the desktop, the one the build chose on a board), says hello and waits at the entry of the main
 * thread (a framed transport: at most FIRE_DEBUG_WAIT_MS for the first command). */
void fire_dbgStart(const DbgProgram* program) {
    char token[128] = "";
    int i;
    DbgBuf b = {NULL, 0, 0};
    g_dbgProg = program;
#if defined(FIRE_DEBUG_TRANSPORT_UART)
    if (!dbgUartOpen()) { fprintf(stderr, "fire debug: cannot open the console UART - running without a debugger\n"); return; }
    g_dbgTransport = DBG_T_UART;
#elif defined(FIRE_DEBUG_TRANSPORT_STDIO)
    g_dbgTransport = DBG_T_STDIO;
#else
    {
        const char* spec = getenv("FIRE_DEBUG");
        if (!spec) return;
        if (strncmp(spec, "tcp:", 4) == 0) {
#ifdef DBG_HAS_TCP
            if (!dbgTcpOpen(spec + 4)) return;
#else
            return;
#endif
        }
        else if (strncmp(spec, "stdio", 5) == 0 && (spec[5] == 0 || spec[5] == ';')) {
            char wait[16];
            g_dbgTransport = DBG_T_STDIO;
            if (dbgOption(spec, "wait", wait, sizeof wait)) g_dbgWaitMs = atoi(wait);
        }
        else { fprintf(stderr, "fire debug: FIRE_DEBUG must be tcp:HOST:PORT;token=T or stdio[;token=T][;wait=MS]\n"); return; }
        dbgOption(spec, "token", token, sizeof token);
    }
#endif
    dbgMutexInit(&g_dbgM);
    dbgCondInit(&g_dbgCv);
    dbgLock(&g_dbgM);
    g_dbgConnected = 1;
    g_dbgArmed = 1;
    g_dbgClaimed = g_dbgTransport == DBG_T_TCP || g_dbgWaitMs <= 0;   /* (over TCP the debugger is there: it accepted the connection) */
    if (g_dbgTransport != DBG_T_TCP) g_dbgStdin = 1;   /* (the agent owns the input: IO reads what it passes on) */
    dbgRegister(&g_dbgMain, "Main");
    g_dbgMain.atEntry = 1;
    dbgPut(&b, "{\"event\":\"hello\",\"protocol\":1,\"token\":"); dbgPutS(&b, token);
    dbgPut(&b, ",\"engine\":\"native\",\"pid\":"); dbgPutI(&b, dbgPid());
    dbgPut(&b, ",\"target\":"); dbgPutS(&b, program->target);
    dbgPut(&b, ",\"mode\":\"debug\",\"transport\":"); dbgPutS(&b, g_dbgTransport == DBG_T_TCP ? "tcp" : g_dbgTransport == DBG_T_UART ? "uart" : "stdio");
    dbgPut(&b, ",\"firstUserSource\":"); dbgPutI(&b, program->firstUser);
    dbgPut(&b, ",\"mainThread\":"); dbgPutI(&b, g_dbgMain.id);
    dbgPut(&b, ",\"sources\":[");
    for (i = 0; i < program->nsources; i++) {
        if (i) dbgPut(&b, ",");
        dbgPut(&b, "{\"index\":"); dbgPutI(&b, i);
        dbgPut(&b, ",\"file\":"); if (program->sources[i].file) dbgPutS(&b, program->sources[i].file); else dbgPut(&b, "null");
        dbgPut(&b, ",\"hash\":"); if (program->sources[i].hash) dbgPutS(&b, program->sources[i].hash); else dbgPut(&b, "null");
        dbgPut(&b, "}");
    }
    dbgPut(&b, "]}");
    dbgSendLine(&b);
    free(b.p);
    g_dbgWaitUntil = fire_plat_nowMs() + g_dbgWaitMs;
    dbgUnlock(&g_dbgM);
    if (!dbgSpawn(dbgAgentMain)) { g_dbgArmed = 0; g_dbgStdin = 0; fprintf(stderr, "fire debug: cannot start the agent thread\n"); return; }
    dbgPark(&g_dbgMain, "entry", 0);
}

/* The program ends (`exitNow`, the end of main): the debugger hears it. */
void fire_dbgExited(int code) {
    DbgBuf b = {NULL, 0, 0};
    if (!g_dbgConnected) return;
    dbgLock(&g_dbgM);
    if (g_dbgTransport != DBG_T_NONE && !g_dbgGaveUp) {
        dbgPut(&b, "{\"event\":\"exited\",\"code\":"); dbgPutI(&b, code); dbgPut(&b, "}");
        dbgSendLine(&b);
        dbgTransportClose();
    }
    g_dbgArmed = 0;
    dbgUnlock(&g_dbgM);
    free(b.p);
}

/* A fire thread begins: it registers and runs on (`continue` with the breakpoints, like a thread of the VM in spark). */
void fire_dbgThreadBegin(void) {
    DbgThread* t;
    DbgBuf b = {NULL, 0, 0};
    char name[32];
    if (!g_dbgConnected || !g_dbgArmed) return;
    t = (DbgThread*)calloc(1, sizeof(DbgThread));
    if (!t) return;
    dbgLock(&g_dbgM);
    snprintf(name, sizeof name, "Fire #%d", ++g_dbgFireCount);
    dbgRegister(t, name);
    dbgPut(&b, "{\"event\":\"thread\",\"thread\":"); dbgPutI(&b, t->id);
    dbgPut(&b, ",\"name\":"); dbgPutS(&b, t->name); dbgPut(&b, "}");
    dbgSendLine(&b);
    dbgUnlock(&g_dbgM);
    free(b.p);
}
void fire_dbgThreadEnd(void) {
    DbgThread* t = g_dbgSelf;
    DbgBuf b = {NULL, 0, 0};
    if (!t || t == &g_dbgMain) return;
    dbgLock(&g_dbgM);
    if (g_dbgThreads == t) g_dbgThreads = t->next;
    else { DbgThread* q = g_dbgThreads; while (q && q->next != t) q = q->next; if (q) q->next = t->next; }
    dbgPut(&b, "{\"event\":\"threadExited\",\"thread\":"); dbgPutI(&b, t->id); dbgPut(&b, "}");
    dbgSendLine(&b);
    dbgUnlock(&g_dbgM);
    g_dbgSelf = NULL;
    free(t);
    free(b.p);
}

#ifdef FIRE_MODULES
/* A plugin is registered: its sources get the next indexes after the program's (and the other plugins'), its classes their tables; a debugger hears of them
 * (`sources`) and this thread waits until it has sent its breakpoints again (they may be in the plugin's files) - at most FIRE_DEBUG_PLUGIN_WAIT_MS. */
void fire_dbgPlugin(const FirePlugin* p) {
    DbgBuf b = {NULL, 0, 0};
    uint32_t i;
    int32_t first;
    int locked = g_dbgConnected;
    if (!p->nsources && !p->dbgClass) return;
    if (locked) dbgLock(&g_dbgM);
    if (g_dbgPluginCount < (int)(sizeof g_dbgPlugins / sizeof g_dbgPlugins[0])) g_dbgPlugins[g_dbgPluginCount++] = p;
    first = (g_dbgProg ? g_dbgProg->nsources : 0) + g_dbgPluginSources;
    if (p->srcMap) for (i = 0; i < p->nsources; i++) p->srcMap[i] = first + (int32_t)i;
    g_dbgPluginSources += (int32_t)p->nsources;
    if (locked && g_dbgArmed && g_dbgTransport != DBG_T_NONE && !g_dbgGaveUp && p->nsources) {
        int gen = g_dbgBpGen;
        int64_t until = fire_plat_nowMs() + FIRE_DEBUG_PLUGIN_WAIT_MS;
        dbgPut(&b, "{\"event\":\"sources\",\"plugin\":"); dbgPutS(&b, p->name);
        dbgPut(&b, ",\"sources\":[");
        for (i = 0; i < p->nsources; i++) {
            if (i) dbgPut(&b, ",");
            dbgPut(&b, "{\"index\":"); dbgPutI(&b, first + (int32_t)i);
            dbgPut(&b, ",\"file\":"); if (p->srcFiles && p->srcFiles[i]) dbgPutS(&b, p->srcFiles[i]); else dbgPut(&b, "null");
            dbgPut(&b, ",\"hash\":"); if (p->srcHashes && p->srcHashes[i]) dbgPutS(&b, p->srcHashes[i]); else dbgPut(&b, "null");
            dbgPut(&b, "}");
        }
        dbgPut(&b, "]}");
        dbgSendLine(&b);
        /* (the agent thread answers without the GIL: waiting here with it is no deadlock) */
        while (g_dbgBpGen == gen && g_dbgArmed && g_dbgTransport != DBG_T_NONE) {
            int64_t left = until - fire_plat_nowMs();
            if (left <= 0) break;
            dbgWaitMs(&g_dbgCv, &g_dbgM, (int)left);
        }
    }
    if (locked) dbgUnlock(&g_dbgM);
    free(b.p);
}
#endif
#endif  /* FIRE_DEBUG_AGENT */
#endif  /* FIRE_RT_IMPLEMENTATION */

#endif  /* FIRE_DEBUG_H */
