/* fire native bridge "windows" (C): the natives behind `#import "windows"` - a window that shows a framebuffer of the graphics bridge, and the events of that window.
 * The fire side (Window, EventType - fire source) is the same as in the VM; this file is what its `__GRPHWin...` functions do (the generator calls `__GRPHWinName` as
 * fire_win_WinName). Putting the pixels on the screen and collecting the events is the display of the platform (FIRE_PLATFORM_DISPLAY_C_HEADER: FireDisplayWindow and the
 * fire_display_* functions; SDL3 or SDL2 on the desktop, see platform/std/fire_display_sdl3.h and fire_display_sdl.h).
 *
 * C99, part of the generated file after fire_bridge_graphics.h when the program imports "windows". As in the VM, `Tick` collects the events, shows the framebuffer and then
 * runs the callbacks (registered lambdas) on the calling thread with the real global variables; an exception that a callback does not catch is reported and ends only the callback.
 * When the program ends (atexit), the threads of the window managers stop and the windows close - what the static destructors of the C++ bridge did. */
#ifndef FIRE_BRIDGE_WINDOWS_H
#define FIRE_BRIDGE_WINDOWS_H

#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include FIRE_PLATFORM_DISPLAY_C_HEADER

typedef struct FireWinCallback { int type; Value lam; } FireWinCallback;

/* A window manager's thread needs the fire threads (the GIL, host services; the generator switches them on when the program can start one), not the
 * browser. Where windows may only live on the main thread (macOS; FIRE_WINDOW_COOPERATIVE elsewhere, for tests) the thread only gives the beat: it asks the
 * program for a cycle about 60 times a second, and the program opens, pumps and shows the windows itself (FIRE_WINDOW_COOP). */
#if defined(FIRE_THREADS) && !defined(__EMSCRIPTEN__)
#define FIRE_WINDOW_THREADS 1
#if defined(__APPLE__) || defined(FIRE_WINDOW_COOPERATIVE)
#define FIRE_WINDOW_COOP 1
#endif
typedef fire_mutex FireWinMutex;
static void fire_winb_mutexInit(FireWinMutex* m) { fire_mutex_init(m); }
static void fire_winb_lock(FireWinMutex* m) { fire_mutex_lock(m); }
static void fire_winb_unlock(FireWinMutex* m) { fire_mutex_unlock(m); }
#else
typedef struct FireWinMutex { int unused; } FireWinMutex;
static void fire_winb_mutexInit(FireWinMutex* m) { m->unused = 0; }
static void fire_winb_lock(FireWinMutex* m) { (void)m; }
static void fire_winb_unlock(FireWinMutex* m) { (void)m; }
#endif

/* What the program and the thread of a window manager share of one window. The OS window (display) belongs to the thread that opens and pumps it: the
 * program's thread (Tick, a manager that runs there) or the manager's thread (Window.Show) - SDL serves a window only on the thread that created it. */
typedef struct FireWinShared {
    FireWinMutex m;
    FireDisplayWindow display;
    char* title;
    int width, height;            /* the size it opens with */
    int opened, failed;
    /* between the program and the manager's thread (under m) */
    FireDisplayEvents inbox;      /* the events the thread fetched */
    int closed;                   /* the thread saw the window close */
    uint32_t* front;              /* the frame the program published last */
    int frontW, frontH;
    int frontNew;
    int vsyncReq, touchReq;       /* settings for the thread */
    int sizeReqW, sizeReqH;       /* a new size of the window for the thread (0: none) */
    int cursorReq;                /* a new cursor for the thread (-1: none) */
    int release;                  /* the window is gone: the thread closes it and frees this */
    /* the thread's own */
    uint32_t* shown;
    int shownW, shownH;
} FireWinShared;

static FireWinShared* fire_winb_sharedNew(const char* title, int width, int height) {
    FireWinShared* s = (FireWinShared*)calloc(1, sizeof(FireWinShared));
    if (s == NULL) fatal("Out of memory.");
    fire_winb_mutexInit(&s->m);
    fire_display_init(&s->display);
    s->title = fire_gfx_strdup(title ? title : "");
    s->width = width; s->height = height;
    s->vsyncReq = -1; s->touchReq = -1;
    s->cursorReq = -1;
    return s;
}
/* Frees it (and closes the OS window). */
static void fire_winb_sharedDelete(FireWinShared* s) {
    if (s == NULL) return;
    fire_display_close(&s->display);
    fire_display_eventsFree(&s->inbox);
    free(s->front);
    free(s->shown);
    free(s->title);
    free(s);
}

struct FireWinMgr;

typedef struct FireWinState {
    FireWinShared* sh;
    FireGfxFramebuffer* fb;
    struct FireWinMgr* mgr;
    int vsync;
    FireWinCallback* callbacks; size_t callbackCount, callbackCap;
    FireDisplayEvents queue;   /* the events for NextEvent (only once EnableEvents was called) */
    size_t queueHead;
    int queueOn;
    int touchMouse;            /* Window.TouchMouse */
    int autoResize;            /* a resize of the window resizes the framebuffer (Window.AutoResize) */
    int blocked;               /* a modal window of its manager is open: its input is dropped (Window.Show(true)) */
    int autoClose;             /* a close request (the frame's button) closes the window (Window.AutoClose) */
    int closeRequested;
} FireWinState;

static void fire_winb_stateDelete(FireWinState* w) {
    size_t i;
    if (w == NULL) return;
    for (i = 0; i < w->callbackCount; i++) release(w->callbacks[i].lam);
    free(w->callbacks);
    fire_display_eventsFree(&w->queue);
    if (w->fb) fire_gfx_fbRelease(w->fb);
    fire_winb_sharedDelete(w->sh);
    free(w);
}

#define FIRE_WIN_MAX_QUEUED_EVENTS 4096

/* ---------------------------------------------------------------------------------------------------------------------------------------------
 * Window managers (fire's WindowManager, docs/UI.md): a manager cycles the windows attached to it - on the program's thread (Tick/Run), or, once started,
 * with a thread of its own that opens the OS windows, fetches their events and shows the last frame the program published, about 60 times a second. The
 * program's side of a cycle (callbacks, the queue, drawing) still runs on the program's thread: the thread asks the main program (hostRequest) to run the
 * manager's service lambda at its next safe point.
 * --------------------------------------------------------------------------------------------------------------------------------------------- */
typedef struct FireWinMgr {
    FireWinMutex m;
    FireWinShared** shown; size_t shownCount, shownCap;      /* the windows its thread shows (under m) */
    FireWinState** attached; size_t attachedCount, attachedCap;   /* the program's view */
    fire_atomic32 stop;
    fire_atomic32 pending;     /* a cycle was asked for and has not run yet */
    Value service;
#ifdef FIRE_WINDOW_THREADS
    fire_thread* thread;
    fire_mutex cycleM;
    fire_cond cycleCv;
    uint64_t cycles;
#endif
} FireWinMgr;

#ifdef FIRE_WINDOW_THREADS
static int fire_winb_started(const FireWinMgr* m) { return m->thread != NULL; }
#ifdef FIRE_WINDOW_COOP
static int fire_winb_ownsWindows(const FireWinMgr* m) { (void)m; return 0; }
#else
static int fire_winb_ownsWindows(const FireWinMgr* m) { return m->thread != NULL; }
#endif
#else
static int fire_winb_started(const FireWinMgr* m) { (void)m; return 0; }
static int fire_winb_ownsWindows(const FireWinMgr* m) { (void)m; return 0; }
#endif

/* The windows and the managers by id (index 0 is not used, ids are not used again). The managers are made first and destroyed last: the windows stop their threads when they go. */
static FireWinState** g_fireWinList; static size_t g_fireWinCount, g_fireWinCap;
static FireWinMgr** g_fireWinMgrs; static size_t g_fireWinMgrCount, g_fireWinMgrCap;
static int g_fireWinReady;

static void fire_winb_atExit(void);
static void fire_winb_ready(void) {
    if (g_fireWinReady) return;
    g_fireWinReady = 1;
    if (!fire_gfx_grow((void**)&g_fireWinList, &g_fireWinCap, 0, sizeof(FireWinState*)) || !fire_gfx_grow((void**)&g_fireWinMgrs, &g_fireWinMgrCap, 0, sizeof(FireWinMgr*))) fatal("Out of memory.");
    g_fireWinList[0] = NULL; g_fireWinCount = 1;
    g_fireWinMgrs[0] = NULL; g_fireWinMgrCount = 1;
    atexit(fire_winb_atExit);
}

FIRE_NORETURN static void fire_winb_noResource(int64_t i) {
    char text[96];
    snprintf(text, sizeof text, "No resource with ID %lld (unknown or already destroyed).", (long long)i);
    fatal(text);
}

static FireWinState* fire_winb_windowOf(Value id) {
    int64_t i = (int32_t)id.i;
    fire_winb_ready();
    if (i > 0 && (size_t)i < g_fireWinCount && g_fireWinList[(size_t)i]) return g_fireWinList[(size_t)i];
    fire_winb_noResource(i);
    return NULL;
}

/* A string argument as UTF-8 (malloc; the caller frees it). */
static char* fire_winb_toUtf8(Value v) {
    const Str* s = strOf(v);
    const fire_char16* c = s->data;
    char* out = (char*)malloc((size_t)s->length * 3 + 1);
    size_t n = 0;
    uint32_t i;
    if (out == NULL) fatal("Out of memory.");
    for (i = 0; i < s->length; i++) {
        uint32_t u = c[i];
        if (u >= 0xD800 && u < 0xDC00 && i + 1 < s->length && c[i + 1] >= 0xDC00 && c[i + 1] < 0xE000) { u = 0x10000 + ((u - 0xD800) << 10) + (c[i + 1] - 0xDC00); i++; }
        if (u < 0x80) out[n++] = (char)u;
        else if (u < 0x800) { out[n++] = (char)(0xC0 | (u >> 6)); out[n++] = (char)(0x80 | (u & 0x3F)); }
        else if (u < 0x10000) { out[n++] = (char)(0xE0 | (u >> 12)); out[n++] = (char)(0x80 | ((u >> 6) & 0x3F)); out[n++] = (char)(0x80 | (u & 0x3F)); }
        else { out[n++] = (char)(0xF0 | (u >> 18)); out[n++] = (char)(0x80 | ((u >> 12) & 0x3F)); out[n++] = (char)(0x80 | ((u >> 6) & 0x3F)); out[n++] = (char)(0x80 | (u & 0x3F)); }
    }
    out[n] = 0;
    return out;
}
static Value fire_winb_utf8Str(const char* text, OwnList* list) {
    size_t len = strlen(text), i = 0, count = 0;
    fire_char16* w = (fire_char16*)malloc(sizeof(fire_char16) * (len * 2 + 1));
    Str* s;
    if (w == NULL) fatal("Out of memory.");
    while (i < len) {
        unsigned char b = (unsigned char)text[i];
        uint32_t u;
        size_t n, k;
        if (b < 0x80) { u = b; n = 1; } else if ((b >> 5) == 6) { u = b & 0x1F; n = 2; } else if ((b >> 4) == 14) { u = b & 0x0F; n = 3; } else if ((b >> 3) == 30) { u = b & 0x07; n = 4; } else { u = 0xFFFD; n = 1; }
        if (i + n > len) { u = 0xFFFD; n = 1; } else for (k = 1; k < n; k++) u = (u << 6) | ((unsigned char)text[i + k] & 0x3F);
        i += n;
        if (u >= 0x10000) { u -= 0x10000; w[count++] = (fire_char16)(0xD800 + (u >> 10)); w[count++] = (fire_char16)(0xDC00 + (u & 0x3FF)); } else w[count++] = (fire_char16)u;
    }
    s = allocStr((uint32_t)count, list);
    if (count) memcpy(strChars(s), w, count * sizeof(fire_char16));
    free(w);
    return StrV(s);
}

/* The paths of a drop (one per line in `data`) as an array of strings. */
static Value fire_winb_paths(const char* data, OwnList* list) {
    size_t count = 0, i = 0;
    const char* p;
    Arr* a;
    if (data == NULL || data[0] == 0) return ArrV(allocArr(0, list));
    for (p = data; *p; p++) if (*p == '\n') count++;
    count++;
    a = allocArr((uint32_t)count, list);
    p = data;
    while (i < count) {
        const char* end = strchr(p, '\n');
        size_t n = end ? (size_t)(end - p) : strlen(p);
        char* line = (char*)malloc(n + 1);
        if (line == NULL) fatal("Out of memory.");
        memcpy(line, p, n);
        line[n] = 0;
        arrItems(a)[i] = fire_winb_utf8Str(line, list);
        retain(arrItems(a)[i]);
        free(line);
        i++;
        p = end ? end + 1 : p + n;
    }
    return ArrV(a);
}

/* The number of parameters a callback for this event type must have (EventCallback.CheckParameters). */
static uint32_t fire_winb_callbackParams(int type) {
    switch (type) {
        case FIRE_EV_KEY_DOWN: case FIRE_EV_KEY_UP: case FIRE_EV_MOUSE_SCROLL: return 4;
        case FIRE_EV_MOUSE_MOVE_REL: case FIRE_EV_MOUSE_MOVE: case FIRE_EV_MOUSE_UP: case FIRE_EV_MOUSE_DOWN: return 3;
        case FIRE_EV_TEXT_INPUT: return 1;
        case FIRE_EV_RESIZE: case FIRE_EV_JOY_BUTTON_DOWN: case FIRE_EV_JOY_BUTTON_UP: return 2;
        case FIRE_EV_JOY_AXIS: case FIRE_EV_JOY_HAT: return 3;
        case FIRE_EV_TOUCH_DOWN: case FIRE_EV_TOUCH_MOVE: case FIRE_EV_TOUCH_UP: return 4;
        case FIRE_EV_JOY_ADDED: case FIRE_EV_JOY_REMOVED: return 1;
        case FIRE_EV_DROP_FILES: case FIRE_EV_DROP_TEXT: return 3;
        case FIRE_EV_DRAG_HOVER: return 2;
        case FIRE_EV_DRAG_LEAVE: return 0;
        default: return 0;
    }
}

/* Runs one callback like a nested lambda: the real globals; an exception ends only the callback. */
static void fire_winb_runCallback(Value lam, int argc, const Value* args) {
    Handler* savedHandlers = g_handlers;
    OwnList scratch;
    g_handlers = NULL;
    ownListInit(&scratch, poolMark(), NULL);
    callLam(lam, argc, args, &scratch);
    if (g_unwind.active) {
        Value ex = g_unwind.value;
        clearUnwind();
        fprintf(stderr, "(unhandled exception in the callback: %s)\n", ex.kind == K_Class ? className(asObj(ex)->cls) : "exception");
    }
    g_handlers = savedHandlers;
    leave(&scratch);
}

static void fire_winb_dispatch(FireWinState* w, const FireDisplayEvent* e) {
    FireWinCallback* mine;   /* a callback may register or unregister callbacks */
    size_t count = 0, i;
    if (w->callbackCount == 0) return;
    mine = (FireWinCallback*)malloc(sizeof(FireWinCallback) * w->callbackCount);
    if (mine == NULL) fatal("Out of memory.");
    for (i = 0; i < w->callbackCount; i++) if (w->callbacks[i].type == e->type) { mine[count++] = w->callbacks[i]; retain(w->callbacks[i].lam); }
    for (i = 0; i < count; i++) {
        OwnList scratch;
        Value a[4];
        int n = 0;
        ownListInit(&scratch, poolMark(), NULL);
        switch (e->type) {
            case FIRE_EV_KEY_DOWN: case FIRE_EV_KEY_UP: a[0] = Int(e->keyCode); a[1] = Int(e->scanCode); a[2] = Int(e->modifier); a[3] = Bool(e->repeat != 0); n = 4; break;
            case FIRE_EV_MOUSE_DOWN: case FIRE_EV_MOUSE_UP: a[0] = Int(e->button); a[1] = Float(e->x); a[2] = Float(e->y); n = 3; break;
            case FIRE_EV_MOUSE_MOVE: a[0] = Float(e->x); a[1] = Float(e->y); a[2] = Int(e->buttonState); n = 3; break;
            case FIRE_EV_MOUSE_MOVE_REL: a[0] = Float(e->xrel); a[1] = Float(e->yrel); a[2] = Int(e->buttonState); n = 3; break;
            case FIRE_EV_MOUSE_SCROLL: a[0] = Float(e->scrollX); a[1] = Float(e->scrollY); a[2] = Float(e->x); a[3] = Float(e->y); n = 4; break;
            case FIRE_EV_TEXT_INPUT: a[0] = fire_winb_utf8Str(e->text, &scratch); n = 1; break;
            case FIRE_EV_RESIZE: a[0] = Int(e->width); a[1] = Int(e->height); n = 2; break;
            case FIRE_EV_TOUCH_DOWN: case FIRE_EV_TOUCH_MOVE: case FIRE_EV_TOUCH_UP: a[0] = Int(e->finger); a[1] = Float(e->x); a[2] = Float(e->y); a[3] = Float(e->pressure); n = 4; break;
            case FIRE_EV_JOY_AXIS: a[0] = Int(e->joystick); a[1] = Int(e->index); a[2] = Float(e->value); n = 3; break;
            case FIRE_EV_JOY_HAT: a[0] = Int(e->joystick); a[1] = Int(e->index); a[2] = Int((int64_t)e->value); n = 3; break;
            case FIRE_EV_JOY_BUTTON_DOWN: case FIRE_EV_JOY_BUTTON_UP: a[0] = Int(e->joystick); a[1] = Int(e->index); n = 2; break;
            case FIRE_EV_JOY_ADDED: case FIRE_EV_JOY_REMOVED: a[0] = Int(e->joystick); n = 1; break;
            case FIRE_EV_DROP_FILES: a[0] = fire_winb_paths(e->data, &scratch); a[1] = Float(e->x); a[2] = Float(e->y); n = 3; break;
            case FIRE_EV_DROP_TEXT: a[0] = fire_winb_utf8Str(e->data ? e->data : "", &scratch); a[1] = Float(e->x); a[2] = Float(e->y); n = 3; break;
            case FIRE_EV_DRAG_HOVER: a[0] = Float(e->x); a[1] = Float(e->y); n = 2; break;
            default: break;
        }
        fire_winb_runCallback(mine[i].lam, n, a);
        leave(&scratch);
        release(mine[i].lam);
    }
    free(mine);
}

static FireWinMgr* fire_winb_mgrOf(Value id) {
    int64_t i = (int32_t)id.i;
    fire_winb_ready();
    if (i > 0 && (size_t)i < g_fireWinMgrCount && g_fireWinMgrs[(size_t)i]) return g_fireWinMgrs[(size_t)i];
    {
        char text[64];
        snprintf(text, sizeof text, "No window manager with ID %lld.", (long long)i);
        fatal(text);
    }
    return NULL;
}

/* Does a manager's thread own the OS window (it opens and pumps it)? */
static int fire_winb_threaded(const FireWinState* w) { return w->mgr && fire_winb_ownsWindows(w->mgr); }

/* The events that a blocked window still gets: a resize and the end of the program. */
static void fire_winb_dropBlocked(FireWinState* w, FireDisplayEvents* events) {
    size_t i, k = 0;
    if (!w->blocked) {
        /* AutoClose: the request closes the window (its handlers still hear of it) */
        if (w->autoClose)
            for (i = 0; i < events->count; i++) if (events->items[i].type == FIRE_EV_CLOSE_REQUEST) w->closeRequested = 1;
        return;
    }
    for (i = 0; i < events->count; i++) {
        if (events->items[i].type == FIRE_EV_RESIZE || events->items[i].type == FIRE_EV_CLOSE) events->items[k++] = events->items[i];
        else { free(events->items[i].data); events->items[i].data = NULL; }
    }
    events->count = k;
}

/* Opens the OS window on the calling thread (once); 0 when it cannot be opened (no display). */
static int fire_winb_ensureOpen(FireWinShared* s, int vsync) {
    if (s->opened) return !s->failed;
    s->opened = 1;
    if (!fire_display_open(&s->display, s->title, s->width, s->height, vsync)) s->failed = 1;
    return !s->failed;
}

/* (a pointer list: remove every `item`) */
static void fire_winb_remove(void** items, size_t* count, const void* item) {
    size_t i, k = 0;
    for (i = 0; i < *count; i++) if (items[i] != item) items[k++] = items[i];
    *count = k;
}
static void fire_winb_add(void*** items, size_t* count, size_t* cap, void* item) {
    if (!fire_gfx_grow((void**)items, cap, *count, sizeof(void*))) fatal("Out of memory.");
    (*items)[(*count)++] = item;
}

static void fire_winb_stopThread(FireWinMgr* m) {
#ifdef FIRE_WINDOW_THREADS
    if (!m->thread) return;
    fire_atomic_store(&m->stop, 1);
    fire_thread_join(m->thread);
    m->thread = NULL;
#else
    (void)m;
#endif
}

/* The end of the program: the threads of the managers end first (they show windows of the list), then the windows close, then the managers go. */
static void fire_winb_atExit(void) {
    size_t i;
    for (i = 0; i < g_fireWinMgrCount; i++) if (g_fireWinMgrs[i]) fire_winb_stopThread(g_fireWinMgrs[i]);
    for (i = 0; i < g_fireWinCount; i++) { fire_winb_stateDelete(g_fireWinList[i]); g_fireWinList[i] = NULL; }
    for (i = 0; i < g_fireWinMgrCount; i++) {
        FireWinMgr* m = g_fireWinMgrs[i];
        if (!m) continue;
        fire_winb_stopThread(m);
        if (m->service.kind == K_Lambda) release(m->service);
        free(m->shown);
        free(m->attached);
        free(m);
        g_fireWinMgrs[i] = NULL;
    }
}

#ifdef FIRE_WINDOW_THREADS
/* The thread of a cooperative manager (FIRE_WINDOW_COOP): only the beat - the program opens, pumps and shows the windows at its safe points. */
static void fire_winb_beatThread(void* arg) {
    FireWinMgr* m = (FireWinMgr*)arg;
    while (!fire_atomic_load(&m->stop)) {
        fire_mutex_lock(&m->cycleM);
        m->cycles++;
        fire_cond_notifyAll(&m->cycleCv);
        fire_mutex_unlock(&m->cycleM);
        if (fire_atomic_exchange(&m->pending, 1) == 0) hostRequest();
        fire_plat_sleepMs(16);
    }
}

static void fire_winb_pumpThread(void* arg) {
    FireWinMgr* m = (FireWinMgr*)arg;
    FireDisplayEvents events = {NULL, 0, 0};
    FireWinShared** list = NULL;
    size_t listCount = 0, listCap = 0, k;
    while (!fire_atomic_load(&m->stop)) {
        int64_t start = fire_plat_nowMs(), rest;
        fire_winb_lock(&m->m);
        listCount = 0;
        for (k = 0; k < m->shownCount; k++) fire_winb_add((void***)&list, &listCount, &listCap, m->shown[k]);
        fire_winb_unlock(&m->m);
        for (k = 0; k < listCount; k++) {
            FireWinShared* s = list[k];
            int w, h, open;
            size_t e;
            fire_winb_lock(&s->m);
            if (s->release) {
                fire_winb_unlock(&s->m);
                fire_winb_lock(&m->m);
                fire_winb_remove((void**)m->shown, &m->shownCount, s);
                fire_winb_unlock(&m->m);
                fire_winb_sharedDelete(s);   /* closes the OS window here, on its thread */
                continue;
            }
            if (s->closed) { fire_winb_unlock(&s->m); continue; }
            if (!fire_winb_ensureOpen(s, s->vsyncReq != 0)) { s->closed = 1; fire_winb_unlock(&s->m); continue; }
            if (s->vsyncReq >= 0) { fire_display_setVSync(&s->display, s->vsyncReq != 0); s->vsyncReq = -1; }
            if (s->touchReq >= 0) { fire_display_setTouchMouse(&s->display, s->touchReq != 0); s->touchReq = -1; }
            if (s->sizeReqW > 0) { fire_display_setSize(&s->display, s->sizeReqW, s->sizeReqH); s->sizeReqW = s->sizeReqH = 0; }
            if (s->cursorReq >= 0) { fire_display_setCursor(&s->display, s->cursorReq); s->cursorReq = -1; }
            fire_display_eventsClear(&events);
            w = s->frontW > 0 ? s->frontW : s->width; h = s->frontH > 0 ? s->frontH : s->height;
            open = fire_display_pump(&s->display, &events, w, h);
            for (e = 0; e < events.count; e++) if (s->inbox.count < FIRE_WIN_MAX_QUEUED_EVENTS) fire_display_push(&s->inbox, &events.items[e]);
            if (!open) s->closed = 1;
            if (s->frontNew) {
                uint32_t* t = s->shown;
                s->shown = s->front; s->front = t;
                s->shownW = s->frontW; s->shownH = s->frontH; s->frontNew = 0;
            }
            fire_winb_unlock(&s->m);
            /* (the program publishes into `front` meanwhile; what is shown is the thread's own copy) */
            if (s->shown != NULL) fire_display_present(&s->display, s->shown, s->shownW, s->shownH);
        }
        fire_mutex_lock(&m->cycleM);
        m->cycles++;
        fire_cond_notifyAll(&m->cycleCv);
        fire_mutex_unlock(&m->cycleM);
        if (fire_atomic_exchange(&m->pending, 1) == 0) hostRequest();
        /* about 60 cycles a second: with VSync the presenting has already waited for the screen, otherwise the rest of the period */
        rest = 16 - (fire_plat_nowMs() - start);
        if (rest > 0) fire_plat_sleepMs(rest);
    }
    fire_winb_lock(&m->m);
    listCount = 0;
    for (k = 0; k < m->shownCount; k++) fire_winb_add((void***)&list, &listCount, &listCap, m->shown[k]);
    m->shownCount = 0;
    fire_winb_unlock(&m->m);
    for (k = 0; k < listCount; k++) {
        FireWinShared* s = list[k];
        int gone;
        fire_winb_lock(&s->m);
        gone = s->release;
        if (!gone) fire_display_close(&s->display);   /* (the program frees it with its window) */
        fire_winb_unlock(&s->m);
        if (gone) fire_winb_sharedDelete(s);
    }
    free(list);
    fire_display_eventsFree(&events);
    fire_display_threadDone();
}

/* The main program at a safe point: the cycles that the threads of the managers asked for (each runs the manager's Tick). */
static void fire_winb_serveManagers(void) {
    static int busy = 0;
    size_t i;
    if (busy) { fire_atomic_store(&g_hostPending, 1); return; }   /* a cycle is running: the next safe point after it */
    busy = 1;
    for (i = 0; i < g_fireWinMgrCount; i++) {
        FireWinMgr* m = g_fireWinMgrs[i];
        if (m && fire_winb_started(m) && fire_atomic_load(&m->pending) && m->service.kind == K_Lambda) fire_winb_runCallback(m->service, 0, NULL);
    }
    busy = 0;
}
#endif

static Value fire_win_WinMgrCreate(void) {
    FireWinMgr* m;
    fire_winb_ready();
    m = (FireWinMgr*)calloc(1, sizeof(FireWinMgr));
    if (m == NULL) fatal("Out of memory.");
    fire_winb_mutexInit(&m->m);
    m->service = Undef();
#ifdef FIRE_WINDOW_THREADS
    fire_mutex_init(&m->cycleM);
    fire_cond_init(&m->cycleCv);
#endif
    fire_winb_add((void***)&g_fireWinMgrs, &g_fireWinMgrCount, &g_fireWinMgrCap, m);
    return Int((int64_t)g_fireWinMgrCount - 1);
}

static void fire_winb_detachFrom(FireWinState* w) {
    FireWinMgr* m = w->mgr;
    if (!m) return;
    fire_winb_remove((void**)m->attached, &m->attachedCount, w);
    if (!fire_winb_ownsWindows(m)) {
#ifdef FIRE_WINDOW_THREADS
        if (fire_winb_started(m)) hostHold(-1);
#endif
    } else {
        /* the OS window belongs to the manager's thread: it closes it there; the window starts afresh (unopened) on whatever cycles it next */
        FireWinShared* fresh = fire_winb_sharedNew(w->sh->title, w->fb->width, w->fb->height);
        fire_winb_lock(&w->sh->m);
        w->sh->release = 1;
        fire_winb_unlock(&w->sh->m);
        w->sh = fresh;
#ifdef FIRE_WINDOW_THREADS
        hostHold(-1);
#endif
    }
    w->mgr = NULL;
}

static Value fire_win_WinMgrAttach(Value mgrId, Value winId) {
    FireWinMgr* m = fire_winb_mgrOf(mgrId);
    FireWinState* w = fire_winb_windowOf(winId);
    if (w->mgr == m) return Bool(true);
    fire_winb_detachFrom(w);
    w->mgr = m;
    fire_winb_add((void***)&m->attached, &m->attachedCount, &m->attachedCap, w);
#ifdef FIRE_WINDOW_THREADS
    if (fire_winb_started(m) && !fire_winb_ownsWindows(m)) hostHold(1);
#endif
    if (fire_winb_ownsWindows(m)) {
        fire_winb_lock(&m->m);
        fire_winb_add((void***)&m->shown, &m->shownCount, &m->shownCap, w->sh);
        fire_winb_unlock(&m->m);
#ifdef FIRE_WINDOW_THREADS
        hostHold(1);
#endif
    }
    return Bool(true);
}

static Value fire_win_WinMgrDetach(Value mgrId, Value winId) {
    FireWinMgr* m = fire_winb_mgrOf(mgrId);
    FireWinState* w = fire_winb_windowOf(winId);
    if (w->mgr != m) return Bool(false);
    fire_winb_detachFrom(w);
    return Bool(true);
}

/* Starts the manager's thread; `service` is the fire lambda the program runs for each cycle. false where there is no thread for windows. */
static Value fire_win_WinMgrStart(Value mgrId, Value service) {
    FireWinMgr* m = fire_winb_mgrOf(mgrId);
#ifdef FIRE_WINDOW_THREADS
    static int registered = 0;
    if (fire_winb_started(m)) return Bool(false);
    if (service.kind != K_Lambda) return Bool(false);
    if (!registered) { registered = 1; hostAddService(fire_winb_serveManagers); }
    retain(service);
    m->service = service;
#ifdef FIRE_WINDOW_COOP
    hostHold((int32_t)m->attachedCount);
    m->thread = fire_thread_start(fire_winb_beatThread, m, 0);
    return Bool(true);
#else
    {
        size_t i;
        fire_winb_lock(&m->m);
        for (i = 0; i < m->attachedCount; i++) {
            FireWinState* w = m->attached[i];
            /* a window that this thread opened already cannot move to the manager's thread: it starts afresh there */
            if (w->sh->opened) {
                FireWinShared* fresh = fire_winb_sharedNew(w->sh->title, w->fb->width, w->fb->height);
                fire_winb_sharedDelete(w->sh);
                w->sh = fresh;
            }
            w->sh->vsyncReq = w->vsync ? 1 : 0;
            w->sh->touchReq = w->touchMouse ? 1 : 0;
            fire_winb_add((void***)&m->shown, &m->shownCount, &m->shownCap, w->sh);
        }
        fire_winb_unlock(&m->m);
        hostHold((int32_t)m->attachedCount);
        m->thread = fire_thread_start(fire_winb_pumpThread, m, 0);
        return Bool(true);
    }
#endif
#else
    (void)m; (void)service;
    return Bool(false);
#endif
}

/* Waits (on the program's thread, without the GIL) up to `ms` for the next cycle of the manager's thread. */
static Value fire_win_WinMgrWait(Value mgrId, Value ms) {
    FireWinMgr* m = fire_winb_mgrOf(mgrId);
    int64_t wait = ms.i > 0 ? ms.i : 1;
#ifdef FIRE_WINDOW_THREADS
    if (fire_winb_started(m)) {
        uint64_t seen;
        gilRelease();
        fire_mutex_lock(&m->cycleM);
        seen = m->cycles;
        while (m->cycles == seen) if (!fire_cond_waitFor(&m->cycleCv, &m->cycleM, wait)) break;
        fire_mutex_unlock(&m->cycleM);
        gilAcquire();
        return Undef();
    }
#endif
    (void)m;
    fire_plat_sleepMs(wait);
    return Undef();
}

static Value fire_win_WinCreate(Value fbId, Value title) {
    FireGfxFramebuffer* fb = fire_gfxb_fbOf(fbId);
    FireWinState* w = (FireWinState*)calloc(1, sizeof(FireWinState));
    char* text;
    if (w == NULL) fatal("Out of memory.");
    w->vsync = 1;
    w->touchMouse = 1;
    w->autoClose = 1;
    text = fire_winb_toUtf8(title);
    w->sh = fire_winb_sharedNew(text, fb->width, fb->height);
    free(text);
    w->fb = fb;
    fire_gfx_fbRetain(fb);
    fire_winb_ready();
    fire_winb_add((void***)&g_fireWinList, &g_fireWinCount, &g_fireWinCap, w);
    return Int((int64_t)g_fireWinCount - 1);
}
static Value fire_win_WinDestroy(Value id) {
    int64_t i = (int32_t)id.i;
    fire_winb_ready();
    if (i > 0 && (size_t)i < g_fireWinCount && g_fireWinList[(size_t)i]) {
        FireWinState* w = g_fireWinList[(size_t)i];
        if (fire_winb_threaded(w)) {
            /* the manager's thread closes the OS window and frees what it shares */
            fire_winb_detachFrom(w);   /* (gives w a fresh, unopened one and marks the old one for release) */
        } else if (w->mgr) fire_winb_detachFrom(w);
        fire_winb_stateDelete(w);
        g_fireWinList[(size_t)i] = NULL;
        return Bool(true);
    }
    return Bool(false);
}

/* (the events of a cycle for the queue of NextEvent) */
static void fire_winb_enqueue(FireWinState* w, const FireDisplayEvents* events) {
    size_t i;
    if (!w->queueOn) return;
    for (i = 0; i < events->count; i++) if (w->queue.count - w->queueHead < FIRE_WIN_MAX_QUEUED_EVENTS) fire_display_push(&w->queue, &events->items[i]);
}

/* The events of a cycle go to the queue (EnableEvents) and to the callbacks; with AutoResize a resize gives the framebuffer the new size. */
static void fire_winb_deliver(Value id, FireWinState* w, FireDisplayEvents* events) {
    int64_t i = (int32_t)id.i;
    size_t k;
    if (w->autoResize)
        for (k = 0; k < events->count; k++)
            if (events->items[k].type == FIRE_EV_RESIZE && fire_gfx_validSize(events->items[k].width, events->items[k].height)) fire_gfx_fbResize(w->fb, events->items[k].width, events->items[k].height);
    fire_winb_enqueue(w, events);
    for (k = 0; k < events->count; k++) {
        fire_winb_dispatch(w, &events->items[k]);
        if (!g_fireWinList[(size_t)i]) break;   /* a callback destroyed the window */
    }
}

/* The first half of a cycle (WindowManager): the events of the window - from its manager's thread, or fetched here when nobody else shows it. */
static Value fire_win_WinPump(Value id) {
    FireWinState* w = fire_winb_windowOf(id);
    FireDisplayEvents events = {NULL, 0, 0};
    int open;
    if (fire_winb_threaded(w)) {
        FireDisplayEvents taken;
        fire_atomic_store(&w->mgr->pending, 0);   /* the program runs a cycle: the next one may be asked for */
        fire_winb_lock(&w->sh->m);
        taken = w->sh->inbox;
        w->sh->inbox = events;
        events = taken;
        open = !w->sh->closed;
        fire_winb_unlock(&w->sh->m);
    } else {
        if (w->mgr && fire_winb_started(w->mgr)) fire_atomic_store(&w->mgr->pending, 0);   /* (a cooperative manager: the same) */
        if (!fire_winb_ensureOpen(w->sh, w->vsync)) return Bool(false);
        open = fire_display_pump(&w->sh->display, &events, w->fb->width, w->fb->height);
    }
    fire_winb_dropBlocked(w, &events);
    fire_winb_deliver(id, w, &events);
    fire_display_eventsFree(&events);
    /* (a callback destroyed the window: its close request went with it, as in the VM) */
    if (!g_fireWinList[(size_t)(int32_t)id.i]) return Bool(open != 0);
    return Bool(open && !w->closeRequested);
}

/* The pixels of the framebuffer as colors (malloc). */
static uint32_t* fire_winb_resolve(const FireGfxFramebuffer* fb) {
    size_t n = fire_gfx_fbPixelCount(fb);
    uint32_t* pixels = (uint32_t*)malloc(sizeof(uint32_t) * (n ? n : 1));
    if (pixels == NULL) fatal("Out of memory.");
    fire_gfx_fbResolveTo(fb, pixels);
    return pixels;
}

/* The second half of a cycle: shows the framebuffer - here, or (a started manager) hands a copy to the manager's thread. */
static Value fire_win_WinPresent(Value id) {
    FireWinState* w = fire_winb_windowOf(id);
    uint32_t* pixels;
    if (fire_winb_threaded(w)) {
        uint32_t* old;
        pixels = fire_winb_resolve(w->fb);
        fire_winb_lock(&w->sh->m);
        old = w->sh->front;
        w->sh->front = pixels;
        w->sh->frontW = w->fb->width; w->sh->frontH = w->fb->height;
        w->sh->frontNew = 1;
        fire_winb_unlock(&w->sh->m);
        free(old);
        return Undef();
    }
    if (!fire_winb_ensureOpen(w->sh, w->vsync)) return Undef();
    pixels = fire_winb_resolve(w->fb);
    fire_display_present(&w->sh->display, pixels, w->fb->width, w->fb->height);
    free(pixels);
    return Undef();
}

/* One cycle of this window alone (Window.Tick): events, frame, then the callbacks. */
static Value fire_win_WinTick(Value id) {
    FireWinState* w = fire_winb_windowOf(id);
    FireDisplayEvents events = {NULL, 0, 0};
    int open;
    int64_t i;
    size_t k;
    uint32_t* pixels;
    if (fire_winb_threaded(w)) { Value o = fire_win_WinPump(id); if (g_fireWinList[(size_t)(int32_t)id.i]) fire_win_WinPresent(id); return o; }
    if (!fire_winb_ensureOpen(w->sh, w->vsync)) return Bool(false);
    open = fire_display_pump(&w->sh->display, &events, w->fb->width, w->fb->height);
    if (w->autoResize)
        for (k = 0; k < events.count; k++)
            if (events.items[k].type == FIRE_EV_RESIZE && fire_gfx_validSize(events.items[k].width, events.items[k].height)) fire_gfx_fbResize(w->fb, events.items[k].width, events.items[k].height);
    fire_winb_dropBlocked(w, &events);
    pixels = fire_winb_resolve(w->fb);
    fire_display_present(&w->sh->display, pixels, w->fb->width, w->fb->height);
    free(pixels);
    fire_winb_enqueue(w, &events);
    i = (int32_t)id.i;
    for (k = 0; k < events.count; k++) {
        fire_winb_dispatch(w, &events.items[k]);
        if (!g_fireWinList[(size_t)i]) break;   /* a callback destroyed the window */
    }
    fire_display_eventsFree(&events);
    if (!g_fireWinList[(size_t)i]) return Bool(open != 0);   /* (a callback destroyed the window: its close request went with it, as in the VM) */
    return Bool(open && !w->closeRequested);
}
static Value fire_win_WinEnableEvents(Value id) { fire_winb_windowOf(id)->queueOn = 1; return Bool(true); }
static Value fire_winb_px(float v) { return Int((int64_t)floor(v)); }
static Value fire_win_WinNextEvent(Value id, OwnList* list) {
    FireWinState* w = fire_winb_windowOf(id);
    FireDisplayEvent e;
    Value items[5];
    uint32_t n = 0, k;
    Arr* a;
    if (!w->queueOn || w->queueHead >= w->queue.count) return Undef();
    e = w->queue.items[w->queueHead];
    w->queue.items[w->queueHead++].data = NULL;   /* (the event's text is this function's now) */
    if (w->queueHead == w->queue.count) { w->queue.count = 0; w->queueHead = 0; }
    items[n++] = Int(e.type);
    switch (e.type) {
        case FIRE_EV_KEY_DOWN: case FIRE_EV_KEY_UP: items[n++] = Int(e.keyCode); items[n++] = Int(e.scanCode); items[n++] = Int(e.modifier); items[n++] = Bool(e.repeat != 0); break;
        case FIRE_EV_MOUSE_DOWN: case FIRE_EV_MOUSE_UP: items[n++] = Int(e.button); items[n++] = fire_winb_px(e.x); items[n++] = fire_winb_px(e.y); break;
        case FIRE_EV_MOUSE_MOVE: items[n++] = fire_winb_px(e.x); items[n++] = fire_winb_px(e.y); items[n++] = Int(e.buttonState); break;
        case FIRE_EV_MOUSE_MOVE_REL: items[n++] = fire_winb_px(e.xrel); items[n++] = fire_winb_px(e.yrel); items[n++] = Int(e.buttonState); break;
        case FIRE_EV_MOUSE_SCROLL: items[n++] = Float(e.scrollX); items[n++] = Float(e.scrollY); items[n++] = fire_winb_px(e.x); items[n++] = fire_winb_px(e.y); break;
        case FIRE_EV_TEXT_INPUT: items[n++] = fire_winb_utf8Str(e.text, list); break;
        case FIRE_EV_RESIZE: items[n++] = Int(e.width); items[n++] = Int(e.height); break;
        case FIRE_EV_TOUCH_DOWN: case FIRE_EV_TOUCH_MOVE: case FIRE_EV_TOUCH_UP: items[n++] = Int(e.finger); items[n++] = fire_winb_px(e.x); items[n++] = fire_winb_px(e.y); items[n++] = Float(e.pressure); break;
        case FIRE_EV_JOY_AXIS: items[n++] = Int(e.joystick); items[n++] = Int(e.index); items[n++] = Float(e.value); break;
        case FIRE_EV_JOY_HAT: items[n++] = Int(e.joystick); items[n++] = Int(e.index); items[n++] = Int((int64_t)e.value); break;
        case FIRE_EV_JOY_BUTTON_DOWN: case FIRE_EV_JOY_BUTTON_UP: items[n++] = Int(e.joystick); items[n++] = Int(e.index); break;
        case FIRE_EV_JOY_ADDED: case FIRE_EV_JOY_REMOVED: items[n++] = Int(e.joystick); break;
        case FIRE_EV_DROP_FILES: items[n++] = fire_winb_paths(e.data, list); items[n++] = fire_winb_px(e.x); items[n++] = fire_winb_px(e.y); break;
        case FIRE_EV_DROP_TEXT: items[n++] = fire_winb_utf8Str(e.data ? e.data : "", list); items[n++] = fire_winb_px(e.x); items[n++] = fire_winb_px(e.y); break;
        case FIRE_EV_DRAG_HOVER: items[n++] = fire_winb_px(e.x); items[n++] = fire_winb_px(e.y); break;
        default: break;
    }
    free(e.data);
    a = allocArr(n, list);
    for (k = 0; k < n; k++) { arrItems(a)[k] = items[k]; retain(items[k]); }
    return ArrV(a);
}
/* ---- the clipboard (text) of the desktop - Clipboard of the prelude ---- */
static Value fire_win_WinClipboardSetText(Value text) {
    char* utf8;
    int ok;
    if (text.kind != K_String) return Bool(false);
    utf8 = fire_winb_toUtf8(text);
    if (utf8 == NULL) fatal("Out of memory.");
    ok = fire_display_clipboardSet(utf8);
    free(utf8);
    return Bool(ok != 0);
}
/* The text on the clipboard, undefined when there is none. */
static Value fire_win_WinClipboardGetText(OwnList* list) {
    char* text = fire_display_clipboardGet();
    Value v;
    if (text == NULL) return Undef();
    v = fire_winb_utf8Str(text, list);
    free(text);
    return v;
}
static Value fire_win_WinClipboardHasText(void) { return Bool(fire_display_clipboardHas() != 0); }

/* ---- pictures and files on the clipboard: Clipboard.GetImage/SetImage/HasImage, GetFiles/SetFiles/HasFiles ---- */
/* The picture on the clipboard: the bytes of its PNG or BMP file (the prelude decodes them), undefined when there is none. */
static Value fire_win_WinClipboardGetImage(OwnList* list) {
    size_t size = 0;
    unsigned char* image = fire_display_clipboardGetImage(&size);
    Buf* b;
    if (image == NULL || size == 0 || size > 0x7FFFFFFFu) { free(image); return Undef(); }
    b = allocBuf((uint32_t)size, list);
    memcpy(bufBytes(b), image, size);
    free(image);
    return BufV(b);
}
/* Puts a picture there (`png`: the PNG file that Framebuffer.ToPng made); the desktops that want a BMP (Windows: CF_DIB) get one made from it too. */
static Value fire_win_WinClipboardSetImage(Value png) {
    Buf* b;
    FireGfxImage image;
    char err[FIRE_GFX_ERR_SIZE];
    uint8_t* bmp = NULL;
    size_t bmpSize = 0;
    int ok;
    if (png.kind != K_Buffer) return Bool(false);
    if (!leafAlive(png)) return destroyedError(png);
    b = bufOf(png);
    fire_gfx_imageInit(&image);
    err[0] = 0;
    if (!fire_gfx_decodeImage(bufBytes(b), b->length, &image, err)) { fire_gfx_imageFree(&image); return Bool(false); }
    {
        size_t n = fire_gfx_imageCount(&image), i;
        uint32_t* rgba = (uint32_t*)calloc(n + 1, sizeof(uint32_t));
        if (rgba == NULL) { fire_gfx_imageFree(&image); return Bool(false); }
        if (image.indexed) { for (i = 0; i < n; i++) rgba[i] = image.indices[i] == image.transparentIndex ? image.palette[image.indices[i]] & 0x00FFFFFFu : image.palette[image.indices[i]]; }
        else if (n) memcpy(rgba, image.pixels, n * sizeof(uint32_t));
        bmp = fire_gfx_encodeBmp(rgba, image.width, image.height, &bmpSize);
        free(rgba);
    }
    fire_gfx_imageFree(&image);
    ok = fire_display_clipboardSetImage(bufBytes(b), b->length, bmp, bmpSize);
    free(bmp);
    return Bool(ok != 0);
}
static Value fire_win_WinClipboardHasImage(void) { return Bool(fire_display_clipboardHasImage() != 0); }
/* The paths on the clipboard (string[]; empty: none). */
static Value fire_win_WinClipboardGetFiles(OwnList* list) {
    char* paths = fire_display_clipboardGetFiles();
    Value v = fire_winb_paths(paths, list);
    free(paths);
    return v;
}
/* Puts paths there (string[]; the prelude makes an array of a List). */
static Value fire_win_WinClipboardSetFiles(Value paths) {
    Arr* a;
    char* all = NULL;
    size_t total = 0, used = 0;
    uint32_t i;
    int ok;
    if (paths.kind != K_Array) return Bool(false);
    if (!leafAlive(paths)) return destroyedError(paths);
    a = arrOf(paths);
    for (i = 0; i < a->length; i++) {
        Value item = arrItems(a)[i];
        char* utf8;
        size_t n;
        char* grown;
        if (item.kind != K_String) continue;
        utf8 = fire_winb_toUtf8(item);
        n = strlen(utf8);
        if (n == 0 || strchr(utf8, '\n') != NULL) { free(utf8); continue; }
        grown = (char*)realloc(all, total + n + 2);
        if (grown == NULL) { free(utf8); free(all); fatal("Out of memory."); }
        all = grown;
        total += n + 2;
        if (used > 0) all[used++] = '\n';
        memcpy(all + used, utf8, n + 1);
        used += n;
        free(utf8);
    }
    if (all == NULL) return Bool(false);
    ok = fire_display_clipboardSetFiles(all);
    free(all);
    return Bool(ok != 0);
}
static Value fire_win_WinClipboardHasFiles(void) { return Bool(fire_display_clipboardHasFiles() != 0); }

static Value fire_win_WinRegisterEvent(Value id, Value type, Value lam) {
    FireWinState* w = fire_winb_windowOf(id);
    int t;
    if (lam.kind != K_Lambda) return Bool(false);
    t = (int32_t)type.i;
    if (lamOf(lam)->nparams != fire_winb_callbackParams(t)) return Bool(false);
    if (!fire_gfx_grow((void**)&w->callbacks, &w->callbackCap, w->callbackCount, sizeof(FireWinCallback))) fatal("Out of memory.");
    retain(lam);
    w->callbacks[w->callbackCount].type = t;
    w->callbacks[w->callbackCount].lam = lam;
    w->callbackCount++;
    return Bool(true);
}
static Value fire_win_WinSetVSync(Value id, Value on) {
    FireWinState* w = fire_winb_windowOf(id);
    w->vsync = on.i != 0;
    if (fire_winb_threaded(w)) { fire_winb_lock(&w->sh->m); w->sh->vsyncReq = w->vsync ? 1 : 0; fire_winb_unlock(&w->sh->m); }
    else fire_display_setVSync(&w->sh->display, w->vsync);
    return Undef();
}
static Value fire_win_WinGetVSync(Value id) { return Bool(fire_winb_windowOf(id)->vsync != 0); }
static Value fire_win_WinSetTouchMouse(Value id, Value on) {
    FireWinState* w = fire_winb_windowOf(id);
    w->touchMouse = on.i != 0;
    if (fire_winb_threaded(w)) { fire_winb_lock(&w->sh->m); w->sh->touchReq = w->touchMouse ? 1 : 0; fire_winb_unlock(&w->sh->m); }
    else fire_display_setTouchMouse(&w->sh->display, w->touchMouse);
    return Undef();
}
static Value fire_win_WinGetTouchMouse(Value id) { return Bool(fire_winb_windowOf(id)->touchMouse != 0); }
static Value fire_win_WinSetAutoResize(Value id, Value on) { fire_winb_windowOf(id)->autoResize = on.i != 0; return Undef(); }
static Value fire_win_WinGetAutoResize(Value id) { return Bool(fire_winb_windowOf(id)->autoResize != 0); }
static Value fire_win_WinSetAutoClose(Value id, Value on) { fire_winb_windowOf(id)->autoClose = on.i != 0; return Undef(); }
static Value fire_win_WinGetAutoClose(Value id) { return Bool(fire_winb_windowOf(id)->autoClose != 0); }
static Value fire_win_WinSetBlocked(Value id, Value on) { fire_winb_windowOf(id)->blocked = on.i != 0; return Undef(); }
/* Window.SetSize: the size of the window in pixels of the screen (before it opens: the size it opens with). */
static Value fire_win_WinSetSize(Value id, Value width, Value height) {
    FireWinState* w = fire_winb_windowOf(id);
    int wd = (int)width.i, ht = (int)height.i;
    FireWinShared* s;
    if (wd <= 0 || ht <= 0) return Bool(false);
    s = w->sh;
    if (fire_winb_threaded(w)) {
        fire_winb_lock(&s->m);
        if (!s->opened) { s->width = wd; s->height = ht; } else { s->sizeReqW = wd; s->sizeReqH = ht; }
        fire_winb_unlock(&s->m);
    } else if (!s->opened) { s->width = wd; s->height = ht; }
    else fire_display_setSize(&s->display, wd, ht);
    return Bool(true);
}
/* Window.Cursor: the system cursor over the window (SDL numbering; the thread of a managed window sets it at its next cycle). */
static Value fire_win_WinSetCursor(Value id, Value cursor) {
    FireWinState* w = fire_winb_windowOf(id);
    FireWinShared* s = w->sh;
    int c = (int)cursor.i;
    if (fire_winb_threaded(w)) {
        fire_winb_lock(&s->m);
        s->cursorReq = c;
        fire_winb_unlock(&s->m);
    } else if (s->opened) fire_display_setCursor(&s->display, c);
    return Undef();
}
/* SystemTheme.Current(): [embedded, scale, dark, accent 0xRRGGBB, font candidates "a;b;c", font size in logical pixels] (see fire_systheme.h). */
static Value fire_win_WinSystemTheme(OwnList* list) {
    FireSystemTheme t;
    Arr* a;
    Value items[6];
    int k;
    fire_display_systemTheme(&t);
    a = allocArr(6, list);
    items[0] = Bool(t.embedded != 0); items[1] = Float((Real)t.scale); items[2] = Bool(t.dark != 0); items[3] = Int(t.accent); items[4] = fire_winb_utf8Str(t.fonts, list); items[5] = Int(t.fontSize);
    for (k = 0; k < 6; k++) { arrItems(a)[k] = items[k]; retain(items[k]); }
    return ArrV(a);
}
/* The program runs the cycle the manager's thread asked for: the next one may be asked for (also when that cycle does nothing, see RunModal). */
static Value fire_win_WinMgrServed(Value mgrId) { fire_atomic_store(&fire_winb_mgrOf(mgrId)->pending, 0); return Undef(); }

#endif
