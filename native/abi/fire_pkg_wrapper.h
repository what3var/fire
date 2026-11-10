/* The C side of the package ABI (native/abi/fire_pkg_abi.h) for a package in C ("language": "c"): marshals fire_val <-> Value and exports the entry points.
 * Included by the wrapper that the compiler generates around the natives of a package (after fire_rt.h in library mode and after the sources of the package).
 * The counterpart of fire_pkg_wrapper.hpp, without C++: errors come back with longjmp (fire_rt.h, fireLibraryJump), and the memory of the results is kept per
 * thread in a small table (no thread-local storage - TinyCC has none). Plain C99. */
#ifndef FIRE_PKG_WRAPPER_H
#define FIRE_PKG_WRAPPER_H

#include "fire_pkg_abi.h"
#include <setjmp.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#if defined(_WIN32)
#define FIRE_PKG_EXPORT __declspec(dllexport)
#else
#include <pthread.h>
#define FIRE_PKG_EXPORT __attribute__((visibility("default")))
#endif

/* A package library has no classes of its own (objects cannot cross the boundary): nothing to run. */
void runDestructors(Obj* o) { (void)o; }
/* ... nor their names (an exception of the program that a callback threw: the VM reports it; a DLL of Windows must have every symbol it uses). */
const char* className(uint32_t cls) { (void)cls; return "?"; }
/* ... and so no TimeSpan either (the prelude hands the bridge its ticks). */
bool timeObjTicks(Value v, int64_t* ticks) { (void)v; (void)ticks; return false; }

/* A native of the package as the wrapper calls it: the arguments, and the list of the scope that owns what the call allocates. */
typedef struct FirePkgFn { const char* name; int arity; Value (*call)(const Value* args, OwnList* list); } FirePkgFn;

/* ---- one call at a time - but re-entrant on the thread that holds it: a native that calls a lambda back (a window's event) may be called again by it ---- */
#if defined(_WIN32)
static SRWLOCK g_pkgMutex = SRWLOCK_INIT;
static void fire_pkgMutexLock(void) { AcquireSRWLockExclusive(&g_pkgMutex); }
static void fire_pkgMutexUnlock(void) { ReleaseSRWLockExclusive(&g_pkgMutex); }
typedef DWORD fire_pkgThread;
static fire_pkgThread fire_pkgSelf(void) { return GetCurrentThreadId(); }
static int fire_pkgSame(fire_pkgThread a, fire_pkgThread b) { return a == b; }
#else
static pthread_mutex_t g_pkgMutex = PTHREAD_MUTEX_INITIALIZER;
static void fire_pkgMutexLock(void) { pthread_mutex_lock(&g_pkgMutex); }
static void fire_pkgMutexUnlock(void) { pthread_mutex_unlock(&g_pkgMutex); }
typedef pthread_t fire_pkgThread;
static fire_pkgThread fire_pkgSelf(void) { return pthread_self(); }
static int fire_pkgSame(fire_pkgThread a, fire_pkgThread b) { return pthread_equal(a, b); }
#endif
static volatile int g_pkgDepth = 0;      /* how often the holder took it */
static fire_pkgThread g_pkgOwner;
void fire_pkgLock(void) {
    if (g_pkgDepth > 0 && fire_pkgSame(g_pkgOwner, fire_pkgSelf())) { g_pkgDepth++; return; }
    fire_pkgMutexLock();
    g_pkgOwner = fire_pkgSelf();
    g_pkgDepth = 1;
}
void fire_pkgUnlock(void) {
    if (--g_pkgDepth == 0) fire_pkgMutexUnlock();
}

/* ---- per thread of the caller: the memory for results (lives until the next call of the same thread; the caller copies it) and the last errors of the
 * bridges (`g_ioError` & co. are plain globals in a library without FIRE_THREADS: they are swapped in and out around each call, which holds the lock) ---- */
typedef struct FirePkgArena { fire_pkgThread thread; int used; void** blocks; size_t count, cap; BridgeError io, device, gfx; } FirePkgArena;
static FirePkgArena* g_pkgArenas = NULL;
static size_t g_pkgArenaCount = 0, g_pkgArenaCap = 0;

static FirePkgArena* fire_pkgArena(void) {
    fire_pkgThread self = fire_pkgSelf();
    size_t i;
    for (i = 0; i < g_pkgArenaCount; i++) if (g_pkgArenas[i].used && fire_pkgSame(g_pkgArenas[i].thread, self)) return &g_pkgArenas[i];
    if (g_pkgArenaCount == g_pkgArenaCap) {
        size_t cap = g_pkgArenaCap ? g_pkgArenaCap * 2 : 8;
        FirePkgArena* grown = (FirePkgArena*)realloc(g_pkgArenas, sizeof(FirePkgArena) * cap);
        if (grown == NULL) allocFailed();
        g_pkgArenas = grown;
        g_pkgArenaCap = cap;
    }
    memset(&g_pkgArenas[g_pkgArenaCount], 0, sizeof(FirePkgArena));
    g_pkgArenas[g_pkgArenaCount].thread = self;
    g_pkgArenas[g_pkgArenaCount].used = 1;
    return &g_pkgArenas[g_pkgArenaCount++];
}
static void fire_pkgArenaClear(FirePkgArena* a) {
    size_t i;
    for (i = 0; i < a->count; i++) free(a->blocks[i]);
    a->count = 0;
}
static void* fire_pkgAlloc(FirePkgArena* a, size_t bytes) {
    void* p;
    if (a->count == a->cap) {
        size_t cap = a->cap ? a->cap * 2 : 16;
        void** grown = (void**)realloc(a->blocks, sizeof(void*) * cap);
        if (grown == NULL) allocFailed();
        a->blocks = grown;
        a->cap = cap;
    }
    p = malloc(bytes ? bytes : 1);
    if (p == NULL) allocFailed();
    a->blocks[a->count++] = p;
    return p;
}

/* ---- fire_val <-> Value ---- */
static void fire_pkgFromRt(FirePkgArena* arena, Value v, fire_val* out);
/* The body of a lambda of the VM (FIRE_LAMBDA): the arguments go to the host, which runs it - at once on the VM's thread, else at its next safe point.
 * (From any thread: the memory of the arguments is an arena of its own, freed when the host has copied them.) */
static Value fire_pkgHostLambda(Value lam, const Value* args) {
    FirePkgArena own;
    fire_val vals[16];
    uint32_t i, n = lamOf(lam)->nparams;
    if (g_libraryHost == NULL || (size_t)g_libraryHost->size < offsetof(fire_host, call_lambda) + sizeof g_libraryHost->call_lambda || g_libraryHost->call_lambda == NULL) return Undef();
    memset(&own, 0, sizeof own);
    if (n > 16) n = 16;
    for (i = 0; i < n; i++) fire_pkgFromRt(&own, args[i], &vals[i]);
    g_libraryHost->call_lambda(lamCaps(lamOf(lam))[0].i, vals, (int32_t)n);
    for (i = 0; i < own.count; i++) free(own.blocks[i]);
    free(own.blocks);
    return Undef();
}
static Value fire_pkgToRt(const fire_val* in, OwnList* list) {
    switch (in->kind) {
        case FIRE_UNDEFINED: return Undef();
        case FIRE_BOOL: return Bool(in->v.i != 0);
        case FIRE_INT: return Int(in->v.i);
        case FIRE_FLOAT: return Float((Real)in->v.f);
        case FIRE_CHAR: return Char((uint32_t)in->v.i);
        case FIRE_STRING: {
            Str* s = allocStr((uint32_t)in->length, list);
            if (in->length > 0) memcpy(strChars(s), in->v.p, (size_t)in->length * sizeof(fire_char16));
            return StrV(s);
        }
        case FIRE_ARRAY: {
            Arr* a = allocArr((uint32_t)in->length, list);
            const fire_val* items = (const fire_val*)in->v.p;
            int32_t i;
            for (i = 0; i < in->length; i++) {
                Value v = fire_pkgToRt(&items[i], list);
                arrItems(a)[i] = v;
                retain(v);
            }
            return ArrV(a);
        }
        case FIRE_BUFFER: {
            /* by reference: the native reads and writes the bytes of the caller in place (the VM pinned them for the call) */
            Buf* b = allocBufExternal((uint8_t*)in->v.p, (uint32_t)in->length, list);
            return BufV(b);
        }
        case FIRE_WORDS: {
            /* a buffer of machine words, by reference like a byte buffer (the library is built for the machine the VM runs on: 64-bit words) */
            Buf* b = allocBufExternal((uint8_t*)in->v.p, (uint32_t)in->length, list);
            if (sizeof(FireWord) != 8) fatal("A word buffer of the virtual machine needs a package library with 64-bit words.");
            b->elem = (uint8_t)sizeof(FireWord);
            return BufV(b);
        }
        case FIRE_WORD: return WordV((uint64_t)in->v.i);
        case FIRE_LAMBDA: {
            /* a lambda of the VM: a lambda here whose body asks the host to call it (its handle is its one capture) */
            Lam* l = allocLam((uint32_t)(in->length < 0 ? 0 : in->length), 1, fire_pkgHostLambda, list);
            lamCaps(l)[0] = Int(in->v.i);
            return LamV(l);
        }
    }
    fatal("An argument of a package function has an unknown kind.");
}

static void fire_pkgFromRt(FirePkgArena* arena, Value v, fire_val* out) {
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
            fire_char16* copy = (fire_char16*)fire_pkgAlloc(arena, (size_t)s->length * sizeof(fire_char16));
            if (s->length > 0) memcpy(copy, s->data, (size_t)s->length * sizeof(fire_char16));
            out->kind = FIRE_STRING; out->length = (int32_t)s->length; out->v.p = copy;
            return;
        }
        case K_Array: {
            Arr* a;
            fire_val* items;
            uint32_t i;
            if (!ownedOf(v)) fatal("A package function returned an array that is destroyed.");
            a = arrOf(v);
            items = (fire_val*)fire_pkgAlloc(arena, (size_t)a->length * sizeof(fire_val));
            for (i = 0; i < a->length; i++) fire_pkgFromRt(arena, arrItems(a)[i], &items[i]);
            out->kind = FIRE_ARRAY; out->length = (int32_t)a->length; out->v.p = items;
            return;
        }
        case K_Buffer: {
            Buf* b;
            uint8_t* copy;
            if (!ownedOf(v)) fatal("A package function returned a buffer that is destroyed.");
            b = bufOf(v);
            copy = (uint8_t*)fire_pkgAlloc(arena, bufSize(b));
            if (b->length > 0) memcpy(copy, b->data, bufSize(b));
            out->kind = b->elem != 1 ? FIRE_WORDS : FIRE_BUFFER; out->length = (int32_t)b->length; out->v.p = copy;
            return;
        }
        case K_Word: out->kind = FIRE_WORD; out->v.i = (int64_t)(uint64_t)v.w; return;
        default: break;
    }
    fatal("A package function returned a value that cannot go to the virtual machine (an object, lambda or pointer): numbers, text, buffers and arrays can.");
}

static int fire_pkgCallEntry(const FirePkgFn* entries, int count, int index, const fire_val* args, int argc, fire_val* result, char* error, int errorSize) {
    jmp_buf jump;
    jmp_buf* outerJump;
    OwnList list;
    Value values[17];
    FirePkgArena* arena;
    volatile int status = 0;
    int nested;
    if (index < 0 || index >= count) { if (error && errorSize > 0) snprintf(error, (size_t)errorSize, "unknown function index"); return 1; }
    if (argc != entries[index].arity || argc > 16) { if (error && errorSize > 0) snprintf(error, (size_t)errorSize, "wrong number of arguments"); return 1; }
    fire_pkgLock();
    /* (a call from a lambda that a native of this library called back: the outer call keeps its error slots and its jump target) */
    nested = g_pkgDepth > 1;
    outerJump = g_libraryJump;
    arena = fire_pkgArena();
    fire_pkgArenaClear(arena);
    if (!nested) {
        g_ioError = arena->io;
        g_deviceError = arena->device;
        g_gfxError = arena->gfx;
    }
    ownListInit(&list, poolMark(), NULL);
    g_libraryJump = &jump;
    if (setjmp(jump) == 0) {
        int i;
        for (i = 0; i < argc; i++) values[i] = fire_pkgToRt(&args[i], &list);
        values[argc] = Undef();
        fire_pkgFromRt(arena, entries[index].call(values, &list), result);
    } else {
        /* an error of the runtime (1) or an exception of the program (2): "Class\ntext" */
        if (error && errorSize > 0) {
            if (g_libraryError.kind == 2) snprintf(error, (size_t)errorSize, "%s\n%s", g_libraryError.cls, g_libraryError.text);
            else snprintf(error, (size_t)errorSize, "%s", g_libraryError.text);
        }
        status = g_libraryError.kind == 2 ? 2 : 1;
    }
    g_libraryJump = outerJump;
    leave(&list);
    if (!nested) {
        arena->io = g_ioError;
        arena->device = g_deviceError;
        arena->gfx = g_gfxError;
    }
    fire_pkgUnlock();
    return status;
}

/* fire_pkg_service: what the library asked the VM for (hostRequest), on the VM's thread - its services run like a call (the lock, a jump target for an error). */
FIRE_MAYBE_UNUSED static void fire_pkgService(void) {   /* (only the library of graphics and windows exports it: fire_pkg_service) */
    jmp_buf jump;
    jmp_buf* outerJump;
    fire_pkgLock();
    outerJump = g_libraryJump;
    g_libraryJump = &jump;
    if (setjmp(jump) == 0) hostServe();
    else fprintf(stderr, "(fire package library: %s)\n", g_libraryError.text);
    g_libraryJump = outerJump;
    fire_pkgUnlock();
}

/* fire_pkg_idle: a turn of a wait of the VM on this thread (Sleep, await, the end of the program) - the windows this thread pumps itself read what the
 * system has for them meanwhile (fire_rt.h FIRE_RT_IDLE_HOOKS, as the waits of a native build do). */
FIRE_MAYBE_UNUSED static void fire_pkgIdle(void) {   /* (only the library of graphics and windows exports it: fire_pkg_idle) */
    jmp_buf jump;
    jmp_buf* outerJump;
    if (!fire_atomic_load(&g_idleOn)) return;
    fire_pkgLock();
    outerJump = g_libraryJump;
    g_libraryJump = &jump;
    if (setjmp(jump) == 0) g_idlePump();
    else fprintf(stderr, "(fire package library: %s)\n", g_libraryError.text);
    g_libraryJump = outerJump;
    fire_pkgUnlock();
}

#endif
