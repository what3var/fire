/* The host's own functions of the library of graphics and windows for the VM (PackageLibrary.GenerateGraphicsWrapper; after the bridges and fire_pkg_wrapper.h): what the
 * host of the VM - the runtime, the editor - does with the framebuffers and windows of a program besides calling its natives. Every function takes the lock of the
 * library's calls (fire_pkg_wrapper.h), so it can be called from any thread.
 *
 *   fire_gfx_host_pixels(id, out, capacity, &w, &h)   the pixels of framebuffer `id` (R,G,B,A per 32-bit word; a palette framebuffer resolved), copied into `out` when
 *                                                    capacity words are enough; returns width*height (0: no such framebuffer)
 *   fire_gfx_host_attach(id, present, ctx)           the windows that show framebuffer `id` call `present` instead (scene3d DirectDrawing: fire_host.win_attach); 1 attached
 *   fire_gfx_host_detach(id, ctx)                    takes that presenter away again (only the one of `ctx`)
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

/* (called with the lock held: fire_pkg_reset) */
static void fire_gfx_host_reset(void) {
    FireGfxTables* t = &g_fireGfxTables;
    size_t i;
    if (g_fireWinReady) {
        fire_winb_atExit();
        g_fireWinCount = 1;
        g_fireWinMgrCount = 1;
    }
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
