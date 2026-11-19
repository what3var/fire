/* The host's own functions of the library of graphics and windows for the VM (PackageLibrary.GenerateGraphicsWrapper; after the bridges and fire_pkg_wrapper.h): what the
 * host of the VM - the runtime, the editor - does with the framebuffers and windows of a program besides calling its natives. Every function takes the lock of the
 * library's calls (fire_pkg_wrapper.h), so it can be called from any thread.
 *
 *   fire_gfx_host_pixels(id, out, capacity, &w, &h)   the pixels of framebuffer `id` (R,G,B,A per 32-bit word; a palette framebuffer resolved), copied into `out` when
 *                                                    capacity words are enough; returns width*height (0: no such framebuffer)
 *   fire_gfx_host_attach(id, present, ctx)           the windows that show framebuffer `id` call `present` instead (scene3d DirectDrawing: fire_host.win_attach); 1 attached
 *   fire_gfx_host_detach(id, ctx)                    takes that presenter away again (only the one of `ctx`)
 *   fire_gfx_host_display(host)                      the host drives the windows of the program (FireDisplayHost, fire_display_headless.h: its events, its pictures and its
 *                                                    pointer; NULL: windows without a screen) - the editor's live view and design view; before the first window opens.
 *                                                    A library is loaded once per process: a host that drives one program this way loads a copy of its own
 *                                                    (GraphicsLibrary.Instance), so that the windows of another program still go to the desktop. 1: done, 0: this library has no such display
 *   fire_gfx_host_reset()                            a program has ended (fire_pkg_reset): its windows close, the managers' threads stop, its framebuffers, renderers,
 *                                                    brushes and pens go - the next program's ids start at 1 again, as in the C# implementation */
#ifndef FIRE_GFX_HOST_H
#define FIRE_GFX_HOST_H

FIRE_PKG_EXPORT int fire_gfx_host_pixels(int id, uint32_t* out, int capacity, int* width, int* height) {
    FireGfxTables* t;
    FireGfxFramebuffer* fb;
    int n = 0;
    fire_pkgLock();
    t = fire_gfxb_tables();
    fb = id > 0 && (size_t)id < t->fbCount ? t->framebuffers[(size_t)id] : NULL;
    if (fb != NULL) {
        n = (int)fire_gfx_fbPixelCount(fb);
        if (width) *width = fb->width;
        if (height) *height = fb->height;
        if (out != NULL && capacity >= n) fire_gfx_fbResolveTo(fb, out);
    }
    fire_pkgUnlock();
    return n;
}

FIRE_PKG_EXPORT int fire_gfx_host_attach(int id, fire_present_fn present, void* ctx) {
    int ok;
    fire_pkgLock();
    ok = fire_gfx_attachPresenter(id, (void (*)(void))present, ctx);
    fire_pkgUnlock();
    return ok;
}

FIRE_PKG_EXPORT void fire_gfx_host_detach(int id, void* ctx) {
    fire_pkgLock();
    fire_gfx_detachPresenter(id, ctx);
    fire_pkgUnlock();
}

FIRE_PKG_EXPORT int fire_gfx_host_display(const void* host) {   /* (a FireDisplayHost) */
#ifdef FIRE_DISPLAY_DESKTOP_H
    fire_pkgLock();
    fire_display_host((const FireDisplayHost*)host);
    fire_pkgUnlock();
    return 1;
#else
    (void)host;
    return 0;
#endif
}

/* ---- the display of a host in another process (FIRE_DISPLAY_SHM=<file>): spark's live view of a program that runs in `fire` (docs/ROADMAP_C_PORT.md, phase B
 * step 9; docs/spec/VM_C.md, "Programme im Fenster der IDE"). The host made the file (its layout below, little-endian) and maps it too: the program's pictures
 * go into it (a sequence number that is odd while a picture is written - the host copies when it is even and the same before and after), its mouse pointer,
 * and the host's events come from a ring in it (the host writes a slot, then counts `eventWrite` up; the program counts `eventRead`). No pipe, no wait: the
 * host looks at the sequence number about every 8 ms; a picture with vsync waits for the next 1/60 s like the vertical blank.
 *
 *   0  "FIRESHM1"   8 int32 version (1)   12 int32 header size (4096)   16 int32 slots of the ring   20 int32 size of a slot (128)   24 int64 bytes for a picture
 *  32 int64 eventWrite   40 int64 eventRead   48 int64 frameSeq   56 int32 width   60 int32 height   64 int32 window   68 int32 cursor   72 int32 pid of the program
 *  76 int32 pictures dropped (too big)
 *  header size: the ring, slot k at header + k * slot size: int32 type, button, buttons, keyCode, modifiers, width, height, joystick, index; float x, y, dx, dy,
 *  value, pressure; int32 (0); int64 finger; char text[32] (UTF-8, zero terminated) - the events of fire_display_headless.h (FireDisplayHostEvent, without data)
 *  header size + slots * slot size: the picture, width * height words R,G,B,A
 * (POSIX: mmap of the file; Windows: not yet - the variable is ignored there.) */
#if defined(FIRE_DISPLAY_DESKTOP_H) && !defined(_WIN32)
#include <sys/mman.h>
#include <fcntl.h>
#include <unistd.h>
#include <time.h>
typedef struct FireShmDisplay { unsigned char* base; size_t size; int64_t lastPresent; } FireShmDisplay;
static FireShmDisplay g_fireShm;
#define FIRE_SHM_I32(off) ((volatile int32_t*)(g_fireShm.base + (off)))
#define FIRE_SHM_I64(off) ((int64_t*)(g_fireShm.base + (off)))
static int64_t fire_shm_nowMs(void) { struct timespec ts; clock_gettime(CLOCK_MONOTONIC, &ts); return (int64_t)ts.tv_sec * 1000 + ts.tv_nsec / 1000000; }

static int32_t fire_shm_next(void* ctx, int32_t window, FireDisplayHostEvent* out) {
    int64_t r, w;
    const unsigned char* s;
    int32_t hs = *FIRE_SHM_I32(12), slots = *FIRE_SHM_I32(16), slotSize = *FIRE_SHM_I32(20);
    (void)ctx; (void)window;
    r = __atomic_load_n(FIRE_SHM_I64(40), __ATOMIC_ACQUIRE);
    w = __atomic_load_n(FIRE_SHM_I64(32), __ATOMIC_ACQUIRE);
    if (r >= w || slots <= 0) return 0;
    s = g_fireShm.base + hs + (size_t)(r % slots) * (size_t)slotSize;
    memset(out, 0, sizeof *out);
    memcpy(&out->type, s, 4); memcpy(&out->button, s + 4, 4); memcpy(&out->buttons, s + 8, 4); memcpy(&out->keyCode, s + 12, 4); memcpy(&out->modifiers, s + 16, 4);
    memcpy(&out->width, s + 20, 4); memcpy(&out->height, s + 24, 4); memcpy(&out->joystick, s + 28, 4); memcpy(&out->index, s + 32, 4);
    memcpy(&out->x, s + 36, 4); memcpy(&out->y, s + 40, 4); memcpy(&out->dx, s + 44, 4); memcpy(&out->dy, s + 48, 4); memcpy(&out->value, s + 52, 4); memcpy(&out->pressure, s + 56, 4);
    memcpy(&out->finger, s + 64, 8); memcpy(out->text, s + 72, 32);
    out->text[31] = 0;
    __atomic_store_n(FIRE_SHM_I64(40), r + 1, __ATOMIC_RELEASE);
    return 1;
}

static void fire_shm_present(void* ctx, int32_t window, const uint32_t* rgba, int32_t width, int32_t height, int32_t vsync) {
    int64_t seq, cap = *FIRE_SHM_I64(24), now;
    size_t off = (size_t)*FIRE_SHM_I32(12) + (size_t)*FIRE_SHM_I32(16) * (size_t)*FIRE_SHM_I32(20);
    size_t bytes = (size_t)width * (size_t)height * 4;
    (void)ctx;
    if ((int64_t)bytes > cap || off + bytes > g_fireShm.size) { (*FIRE_SHM_I32(76))++; return; }
    seq = __atomic_load_n(FIRE_SHM_I64(48), __ATOMIC_RELAXED);
    __atomic_store_n(FIRE_SHM_I64(48), seq | 1, __ATOMIC_RELEASE);   /* (odd: being written) */
    __atomic_thread_fence(__ATOMIC_SEQ_CST);
    memcpy(g_fireShm.base + off, rgba, bytes);
    *FIRE_SHM_I32(56) = width; *FIRE_SHM_I32(60) = height; *FIRE_SHM_I32(64) = window;
    __atomic_store_n(FIRE_SHM_I64(48), (seq | 1) + 1, __ATOMIC_RELEASE);
    if (vsync) {
        int64_t wait;
        now = fire_shm_nowMs();
        wait = 16 - (now - g_fireShm.lastPresent);
        if (wait > 0 && wait <= 16) { struct timespec ts; ts.tv_sec = 0; ts.tv_nsec = (long)wait * 1000000L; nanosleep(&ts, NULL); }
    }
    g_fireShm.lastPresent = fire_shm_nowMs();
}

static void fire_shm_cursor(void* ctx, int32_t window, int32_t cursor) { (void)ctx; (void)window; *FIRE_SHM_I32(68) = cursor; }

/* FIRE_DISPLAY_SHM names the file of a host: the windows of the program are the host's from now on (fire_pkg_set_host, before the first window). */
static void fire_gfx_host_fromEnvironment(void) {
    const char* path = getenv("FIRE_DISPLAY_SHM");
    int fd;
    off_t size;
    void* base;
    FireDisplayHost host;
    if (path == NULL || *path == 0 || g_fireShm.base != NULL) return;
    fd = open(path, O_RDWR);
    if (fd < 0) return;
    size = lseek(fd, 0, SEEK_END);
    if (size < 4096) { close(fd); return; }
    base = mmap(NULL, (size_t)size, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
    close(fd);
    if (base == MAP_FAILED) return;
    if (memcmp(base, "FIRESHM1", 8) != 0) { munmap(base, (size_t)size); return; }
    g_fireShm.base = (unsigned char*)base;
    g_fireShm.size = (size_t)size;
    *FIRE_SHM_I32(72) = (int32_t)getpid();
    memset(&host, 0, sizeof host);
    host.size = (int32_t)sizeof host;
    host.next_event = fire_shm_next;
    host.present = fire_shm_present;
    host.cursor = fire_shm_cursor;
    fire_display_host(&host);
}
#else
static void fire_gfx_host_fromEnvironment(void) {}
#endif

/* (called with the lock held: fire_pkg_reset) */
static void fire_gfx_host_reset(void) {
    FireGfxTables* t = &g_fireGfxTables;
    size_t i;
    if (g_fireWinReady) {
        fire_winb_atExit();
        g_fireWinCount = 1;
        g_fireWinMgrCount = 1;
    }
#ifdef FIRE_DISPLAY_DESKTOP_H
    /* the program's own clipboard (a display without a desktop) goes with it; the desktop's stays */
    fire_display_memClear(fire_display_memClipboard());
#endif
    if (!t->ready) return;
    for (i = 1; i < t->rndCount; i++) if (t->renderers[i]) { fire_gfx_rendererDelete(t->renderers[i]); t->renderers[i] = NULL; }
    for (i = 1; i < t->fbCount; i++) if (t->framebuffers[i]) { fire_gfx_fbRelease(t->framebuffers[i]); t->framebuffers[i] = NULL; }
    for (i = 1; i < t->bshCount; i++) { free(t->brushes[i]); t->brushes[i] = NULL; }
    for (i = 1; i < t->penCount; i++) { free(t->pens[i]); t->pens[i] = NULL; }
    t->rndCount = t->fbCount = t->bshCount = t->penCount = 1;
    /* the fonts the program added (the built-in ones, 1 and 2, stay), and what it looked for on the system and did not find */
    for (i = 3; i < t->fonts.count; i++) fire_gfx_fontTableDestroy(&t->fonts, (int64_t)i);
    if (t->fonts.count > 3) t->fonts.count = 3;
    for (i = 0; i < t->fonts.missCount; i++) free(t->fonts.systemMisses[i]);
    t->fonts.missCount = 0;
}

#endif
