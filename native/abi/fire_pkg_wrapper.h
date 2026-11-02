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

/* A native of the package as the wrapper calls it: the arguments, and the list of the scope that owns what the call allocates. */
typedef struct FirePkgFn { const char* name; int arity; Value (*call)(const Value* args, OwnList* list); } FirePkgFn;

/* ---- one call at a time ---- */
#if defined(_WIN32)
static SRWLOCK g_pkgLock = SRWLOCK_INIT;
static void fire_pkgLock(void) { AcquireSRWLockExclusive(&g_pkgLock); }
static void fire_pkgUnlock(void) { ReleaseSRWLockExclusive(&g_pkgLock); }
typedef DWORD fire_pkgThread;
static fire_pkgThread fire_pkgSelf(void) { return GetCurrentThreadId(); }
static int fire_pkgSame(fire_pkgThread a, fire_pkgThread b) { return a == b; }
#else
static pthread_mutex_t g_pkgLock = PTHREAD_MUTEX_INITIALIZER;
static void fire_pkgLock(void) { pthread_mutex_lock(&g_pkgLock); }
static void fire_pkgUnlock(void) { pthread_mutex_unlock(&g_pkgLock); }
typedef pthread_t fire_pkgThread;
static fire_pkgThread fire_pkgSelf(void) { return pthread_self(); }
static int fire_pkgSame(fire_pkgThread a, fire_pkgThread b) { return pthread_equal(a, b); }
#endif

/* ---- memory for results: lives until the next call of the same thread (the caller copies it) ---- */
typedef struct FirePkgArena { fire_pkgThread thread; int used; void** blocks; size_t count, cap; } FirePkgArena;
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
            copy = (uint8_t*)fire_pkgAlloc(arena, b->length);
            if (b->length > 0) memcpy(copy, b->data, b->length);
            out->kind = FIRE_BUFFER; out->length = (int32_t)b->length; out->v.p = copy;
            return;
        }
        default: break;
    }
    fatal("A package function returned a value that cannot go to the virtual machine (an object, lambda or pointer): numbers, text, buffers and arrays can.");
}

static int fire_pkgCallEntry(const FirePkgFn* entries, int count, int index, const fire_val* args, int argc, fire_val* result, char* error, int errorSize) {
    jmp_buf jump;
    OwnList list;
    Value values[17];
    FirePkgArena* arena;
    volatile int status = 0;
    if (index < 0 || index >= count) { if (error && errorSize > 0) snprintf(error, (size_t)errorSize, "unknown function index"); return 1; }
    if (argc != entries[index].arity || argc > 16) { if (error && errorSize > 0) snprintf(error, (size_t)errorSize, "wrong number of arguments"); return 1; }
    fire_pkgLock();
    arena = fire_pkgArena();
    fire_pkgArenaClear(arena);
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
    g_libraryJump = NULL;
    leave(&list);
    fire_pkgUnlock();
    return status;
}

#endif
