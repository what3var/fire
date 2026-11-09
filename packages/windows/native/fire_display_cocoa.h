/* fire native platform package "std": a window on macOS (fire_dns_*, one of the displays of fire_display_desktop.h) - without Objective-C: AppKit is
 * driven from C through the Objective-C runtime (objc_msgSend, cast to the exact type of every call - the calling convention of arm64 does not allow variadic
 * calls), the classes this needs (the view, the window's delegate, the application's delegate) are made at run time. No header of Apple is included: the few
 * types and functions are declared here. The program links the frameworks Cocoa and QuartzCore (`// fire-link: framework:...`).
 *
 * AppKit wants its windows on the main thread: the windows bridge opens and pumps them there (FIRE_WINDOW_COOP on macOS). Pumping takes the events of the
 * application (they go to the windows they belong to). Sizes and positions are points (macOS scales a window for a Retina screen itself; the framebuffer is
 * stretched over the view by its layer); fire_dns_scale says the backing scale, FIRE_EV_SCALE reports a change. What the window shows is the framebuffer as the
 * contents of the view's layer (a CGImage of a copy, premultiplied B,G,R,A). VSync: a frame waits until 1/60 s since the last one passed.
 * Input: keys (the scan code from the key code of the keyboard, the key code from the layout - charactersIgnoringModifiers), text and compositions through
 * NSTextInputClient (insertText, setMarkedText: FIRE_EV_TEXT_EDITING; the candidates at the rectangle the program names), the mouse, the wheel (precise deltas of a
 * trackpad in notches), what other programs drop (files and text, with the position while it is dragged). The clipboard is the general pasteboard (text,
 * PNG - TIFF of other programs is converted -, file URLs).
 * Behind the window: NSVisualEffectView (blur, acrylic, Mica as materials of macOS) behind a transparent view. Dragging out of the window is not there yet (0). */
#ifndef FIRE_DISPLAY_COCOA_H
#define FIRE_DISPLAY_COCOA_H

#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include "fire_display_common.h"

/* ---- the Objective-C runtime and the few C functions of CoreGraphics/CoreFoundation ---- */
typedef struct objc_object* FireId;
typedef struct objc_selector* FireSel;
typedef struct objc_class* FireClass;
typedef struct FireNSPoint { double x, y; } FireNSPoint;
typedef struct FireNSSize { double width, height; } FireNSSize;
typedef struct FireNSRect { FireNSPoint origin; FireNSSize size; } FireNSRect;
typedef struct FireNSRange { unsigned long location, length; } FireNSRange;
extern FireClass objc_getClass(const char*);
extern FireClass object_getClass(FireId);
extern FireSel sel_registerName(const char*);
extern void objc_msgSend(void);
#if defined(__x86_64__)
extern void objc_msgSend_stret(void);
#endif
extern FireClass objc_allocateClassPair(FireClass, const char*, size_t);
extern void objc_registerClassPair(FireClass);
extern int class_addMethod(FireClass, FireSel, void (*)(void), const char*);
extern int class_addIvar(FireClass, const char*, size_t, uint8_t, const char*);
extern int class_addProtocol(FireClass, void*);
extern void* objc_getProtocol(const char*);
extern void* object_getIndexedIvars(FireId);
extern FireId object_getIvar(FireId, void*);
extern void* class_getInstanceVariable(FireClass, const char*);
extern void object_setIvar(FireId, void*, FireId);
extern void* objc_autoreleasePoolPush(void);
extern void objc_autoreleasePoolPop(void*);
extern void* CGColorSpaceCreateDeviceRGB(void);
extern void CGColorSpaceRelease(void*);
extern void* CGDataProviderCreateWithData(void*, const void*, size_t, void (*)(void*, const void*, size_t));
extern void CGDataProviderRelease(void*);
extern void* CGImageCreate(size_t, size_t, size_t, size_t, size_t, void*, uint32_t, void*, const double*, int, int);
extern void CGImageRelease(void*);

/* (objc_msgSend as a plain function pointer: every call casts it to its own type) */
static void (*fire_dns_msg(void))(void) { return objc_msgSend; }
#if defined(__x86_64__)
static void (*fire_dns_msgStret(void))(void) { return objc_msgSend_stret; }
#endif
/* A message to `obj`: R the result, P the parameter list (after id and SEL) in brackets, then the arguments. */
#define FIRE_NS(R, P, obj, name, ...) ((R (*) P)fire_dns_msg())((FireId)(obj), sel_registerName(name), __VA_ARGS__)
#define FIRE_NS0(R, obj, name) ((R (*)(FireId, FireSel))fire_dns_msg())((FireId)(obj), sel_registerName(name))
#define FIRE_CLS(name) ((FireId)objc_getClass(name))

static FireNSRect fire_dns_rect(FireId obj, const char* name) {
#if defined(__x86_64__)
    FireNSRect r;
    ((void (*)(FireNSRect*, FireId, FireSel))fire_dns_msgStret())(&r, obj, sel_registerName(name));
    return r;
#else
    return ((FireNSRect (*)(FireId, FireSel))fire_dns_msg())(obj, sel_registerName(name));
#endif
}
static FireNSRect fire_dns_rectArg(FireId obj, const char* name, FireNSRect arg, FireId other) {
#if defined(__x86_64__)
    FireNSRect r;
    ((void (*)(FireNSRect*, FireId, FireSel, FireNSRect, FireId))fire_dns_msgStret())(&r, obj, sel_registerName(name), arg, other);
    return r;
#else
    return ((FireNSRect (*)(FireId, FireSel, FireNSRect, FireId))fire_dns_msg())(obj, sel_registerName(name), arg, other);
#endif
}
static FireNSRect fire_dns_rectMake(double x, double y, double w, double h) { FireNSRect r; r.origin.x = x; r.origin.y = y; r.size.width = w; r.size.height = h; return r; }
static FireId fire_dns_str(const char* utf8) { return FIRE_NS(FireId, (FireId, FireSel, const char*), FIRE_CLS("NSString"), "stringWithUTF8String:", utf8 ? utf8 : ""); }
static const char* fire_dns_utf8(FireId s) { return s ? FIRE_NS0(const char*, s, "UTF8String") : NULL; }
/* (an NSString or an NSAttributedString: its text) */
static FireId fire_dns_plain(FireId s) {
    if (s && FIRE_NS(signed char, (FireId, FireSel, FireClass), s, "isKindOfClass:", objc_getClass("NSAttributedString"))) return FIRE_NS0(FireId, s, "string");
    return s;
}

/* ---- the window ---- */
typedef struct FireDnsWindow {
    FireId window, view, effect, delegate;
    int winW, winH;
    int vsync, touchMouse, cursor, closed, direct, backdrop;
    double scale, lastFrame;
    uint32_t* bgra; size_t bgraCap;
    int mods;
    int textX, textY, textW, textH;
    char* marked;
    FireDisplayQueue queue;
} FireDnsWindow;

static FireDnsWindow* fire_dns_of(FireId obj) {
    void* ivar = obj != NULL ? class_getInstanceVariable(object_getClass(obj), "fireWindow") : NULL;   /* (the isa of arm64 is not a plain pointer) */
    return ivar ? (FireDnsWindow*)(void*)object_getIvar(obj, ivar) : NULL;
}

static double fire_dns_now(void) { struct timespec t; clock_gettime(CLOCK_MONOTONIC, &t); return (double)t.tv_sec + (double)t.tv_nsec * 1e-9; }

/* The position of an event in the view (points from the top left). */
static void fire_dns_where(FireDnsWindow* w, FireId event, float* x, float* y) {
    FireNSPoint p = FIRE_NS0(FireNSPoint, event, "locationInWindow");
    p = FIRE_NS(FireNSPoint, (FireId, FireSel, FireNSPoint, FireId), w->view, "convertPoint:fromView:", p, (FireId)NULL);
    *x = (float)p.x; *y = (float)p.y;   /* (the view is flipped: y grows downwards) */
}

/* The modifiers of an event: the device-dependent bits tell left from right. */
static int fire_dns_mods(unsigned long flags) {
    int m = 0;
    if (flags & 0x2) m |= FIRE_MOD_LSHIFT;
    if (flags & 0x4) m |= FIRE_MOD_RSHIFT;
    if (flags & 0x1) m |= FIRE_MOD_LCTRL;
    if (flags & 0x2000) m |= FIRE_MOD_RCTRL;
    if (flags & 0x20) m |= FIRE_MOD_LALT;
    if (flags & 0x40) m |= FIRE_MOD_RALT;
    if (flags & 0x8) m |= FIRE_MOD_LGUI;
    if (flags & 0x10) m |= FIRE_MOD_RGUI;
    /* (a modifier without its device bit - an event made by a program: the left one) */
    if ((flags & (1ul << 17)) && !(m & (FIRE_MOD_LSHIFT | FIRE_MOD_RSHIFT))) m |= FIRE_MOD_LSHIFT;
    if ((flags & (1ul << 18)) && !(m & (FIRE_MOD_LCTRL | FIRE_MOD_RCTRL))) m |= FIRE_MOD_LCTRL;
    if ((flags & (1ul << 19)) && !(m & (FIRE_MOD_LALT | FIRE_MOD_RALT))) m |= FIRE_MOD_LALT;
    if ((flags & (1ul << 20)) && !(m & (FIRE_MOD_LGUI | FIRE_MOD_RGUI))) m |= FIRE_MOD_LGUI;
    if (flags & (1ul << 16)) m |= FIRE_MOD_CAPS;
    return m;
}

static void fire_dns_key(FireDnsWindow* w, FireId event, int down) {
    unsigned short code = FIRE_NS0(unsigned short, event, "keyCode");
    unsigned long flags = FIRE_NS0(unsigned long, event, "modifierFlags");
    int sc = fire_key_fromMac(code);
    uint32_t unshifted = 0;
    FireId chars = FIRE_NS0(FireId, event, "charactersIgnoringModifiers");
    if (chars && FIRE_NS0(unsigned long, chars, "length") > 0) {
        unsigned short c = FIRE_NS(unsigned short, (FireId, FireSel, unsigned long), chars, "characterAtIndex:", 0ul);
        if (c < 0xF700 || c > 0xF8FF) unshifted = c;   /* (the function keys are characters of the private area) */
    }
    fire_display_queueKey(&w->queue, down, fire_key_code(sc, unshifted), sc, fire_dns_mods(flags), down && FIRE_NS0(signed char, event, "isARepeat"));
}

/* ---- the methods of the view ---- */
static signed char fire_dns_yes(FireId self, FireSel cmd) { (void)self; (void)cmd; return 1; }
static signed char fire_dns_acceptsFirstMouse(FireId self, FireSel cmd, FireId e) { (void)self; (void)cmd; (void)e; return 1; }
static void fire_dns_keyDown(FireId self, FireSel cmd, FireId e) {
    FireDnsWindow* w = fire_dns_of(self);
    FireId list;
    (void)cmd;
    if (w == NULL) return;
    fire_dns_key(w, e, 1);
    /* the text (and compositions) through the input system of macOS: it calls insertText / setMarkedText back */
    list = FIRE_NS(FireId, (FireId, FireSel, FireId), FIRE_CLS("NSArray"), "arrayWithObject:", e);
    FIRE_NS(void, (FireId, FireSel, FireId), self, "interpretKeyEvents:", list);
}
static void fire_dns_keyUp(FireId self, FireSel cmd, FireId e) { FireDnsWindow* w = fire_dns_of(self); (void)cmd; if (w) fire_dns_key(w, e, 0); }
static void fire_dns_flagsChanged(FireId self, FireSel cmd, FireId e) {
    FireDnsWindow* w = fire_dns_of(self);
    unsigned short code;
    int sc, bit, now;
    (void)cmd;
    if (w == NULL) return;
    code = FIRE_NS0(unsigned short, e, "keyCode");
    sc = fire_key_fromMac(code);
    bit = fire_key_modBit(sc);
    now = fire_dns_mods(FIRE_NS0(unsigned long, e, "modifierFlags"));
    if (sc == FIRE_SC_CAPSLOCK) { fire_display_queueKey(&w->queue, 1, fire_key_fromScan(sc), sc, now, 0); fire_display_queueKey(&w->queue, 0, fire_key_fromScan(sc), sc, now, 0); return; }
    if (bit) fire_display_queueKey(&w->queue, (now & bit) != 0, fire_key_fromScan(sc), sc, now, 0);
}
static void fire_dns_mouse(FireId self, FireId e, int button, int kind) {
    FireDnsWindow* w = fire_dns_of(self);
    float x, y;
    if (w == NULL) return;
    fire_dns_where(w, e, &x, &y);
    if (kind == 0) fire_display_queueMotion(&w->queue, x, y);
    else fire_display_queueButton(&w->queue, kind == 1, button, x, y);
}
static void fire_dns_mouseDown(FireId self, FireSel cmd, FireId e) { (void)cmd; fire_dns_mouse(self, e, FIRE_BUTTON_LEFT, 1); }
static void fire_dns_mouseUp(FireId self, FireSel cmd, FireId e) { (void)cmd; fire_dns_mouse(self, e, FIRE_BUTTON_LEFT, 2); }
static void fire_dns_rightDown(FireId self, FireSel cmd, FireId e) { (void)cmd; fire_dns_mouse(self, e, FIRE_BUTTON_RIGHT, 1); }
static void fire_dns_rightUp(FireId self, FireSel cmd, FireId e) { (void)cmd; fire_dns_mouse(self, e, FIRE_BUTTON_RIGHT, 2); }
static int fire_dns_otherButton(FireId e) { long n = FIRE_NS0(long, e, "buttonNumber"); return n == 2 ? FIRE_BUTTON_MIDDLE : n == 3 ? FIRE_BUTTON_X1 : n == 4 ? FIRE_BUTTON_X2 : 0; }
static void fire_dns_otherDown(FireId self, FireSel cmd, FireId e) { (void)cmd; if (fire_dns_otherButton(e)) fire_dns_mouse(self, e, fire_dns_otherButton(e), 1); }
static void fire_dns_otherUp(FireId self, FireSel cmd, FireId e) { (void)cmd; if (fire_dns_otherButton(e)) fire_dns_mouse(self, e, fire_dns_otherButton(e), 2); }
static void fire_dns_moved(FireId self, FireSel cmd, FireId e) { (void)cmd; fire_dns_mouse(self, e, 0, 0); }
static void fire_dns_entered(FireId self, FireSel cmd, FireId e) { FireDnsWindow* w = fire_dns_of(self); (void)cmd; (void)e; if (w) fire_display_queueSimple(&w->queue, FIRE_EV_MOUSE_ENTER); }
static void fire_dns_exited(FireId self, FireSel cmd, FireId e) { FireDnsWindow* w = fire_dns_of(self); (void)cmd; (void)e; if (w) fire_display_queueSimple(&w->queue, FIRE_EV_MOUSE_LEAVE); }
static void fire_dns_scroll(FireId self, FireSel cmd, FireId e) {
    FireDnsWindow* w = fire_dns_of(self);
    double dx = FIRE_NS0(double, e, "scrollingDeltaX"), dy = FIRE_NS0(double, e, "scrollingDeltaY");
    float x, y;
    (void)cmd;
    if (w == NULL) return;
    if (FIRE_NS0(signed char, e, "hasPreciseScrollingDeltas")) { dx /= 10.0; dy /= 10.0; }   /* (a trackpad: points, about ten per notch) */
    fire_dns_where(w, e, &x, &y);
    fire_display_queueScroll(&w->queue, (float)-dx, (float)dy, x, y);
}
static void fire_dns_resetCursorRects(FireId self, FireSel cmd) {
    static const char* const names[FIRE_CURSOR_COUNT] = { "arrowCursor", "IBeamCursor", "arrowCursor", "crosshairCursor", "arrowCursor", "arrowCursor", "arrowCursor",
                                                          "resizeLeftRightCursor", "resizeUpDownCursor", "openHandCursor", "operationNotAllowedCursor", "pointingHandCursor" };
    FireDnsWindow* w = fire_dns_of(self);
    FireId cursor;
    (void)cmd;
    if (w == NULL) return;
    cursor = FIRE_NS0(FireId, FIRE_CLS("NSCursor"), names[w->cursor >= 0 && w->cursor < FIRE_CURSOR_COUNT ? w->cursor : 0]);
    FIRE_NS(void, (FireId, FireSel, FireNSRect, FireId), self, "addCursorRect:cursor:", fire_dns_rect(self, "bounds"), cursor);
}
/* NSTextInputClient */
static void fire_dns_insertText(FireId self, FireSel cmd, FireId text, FireNSRange range) {
    FireDnsWindow* w = fire_dns_of(self);
    const char* s = fire_dns_utf8(fire_dns_plain(text));
    (void)cmd; (void)range;
    if (w == NULL) return;
    if (w->marked != NULL) { free(w->marked); w->marked = NULL; fire_display_queueEditing(&w->queue, NULL, 0, 0, 0); }
    if (s != NULL) fire_display_queueText(&w->queue, s, strlen(s));
}
static void fire_dns_setMarkedText(FireId self, FireSel cmd, FireId text, FireNSRange selected, FireNSRange replace) {
    FireDnsWindow* w = fire_dns_of(self);
    const char* s = fire_dns_utf8(fire_dns_plain(text));
    (void)cmd; (void)replace;
    if (w == NULL) return;
    free(w->marked);
    w->marked = s != NULL && s[0] ? (char*)fire_display_copyBytes(s, strlen(s)) : NULL;
    fire_display_queueEditing(&w->queue, w->marked, w->marked ? strlen(w->marked) : 0, (int)selected.location, (int)selected.length);
}
static void fire_dns_unmarkText(FireId self, FireSel cmd) {
    FireDnsWindow* w = fire_dns_of(self);
    (void)cmd;
    if (w != NULL && w->marked != NULL) { free(w->marked); w->marked = NULL; fire_display_queueEditing(&w->queue, NULL, 0, 0, 0); }
}
static signed char fire_dns_hasMarkedText(FireId self, FireSel cmd) { FireDnsWindow* w = fire_dns_of(self); (void)cmd; return w != NULL && w->marked != NULL; }
static FireNSRange fire_dns_markedRange(FireId self, FireSel cmd) {
    FireDnsWindow* w = fire_dns_of(self);
    FireNSRange r;
    (void)cmd;
    r.location = w != NULL && w->marked != NULL ? 0 : (unsigned long)-1;   /* (NSNotFound) */
    r.length = w != NULL && w->marked != NULL ? strlen(w->marked) : 0;
    return r;
}
static FireNSRange fire_dns_selectedRange(FireId self, FireSel cmd) { FireNSRange r; (void)self; (void)cmd; r.location = 0; r.length = 0; return r; }
static FireId fire_dns_validAttributes(FireId self, FireSel cmd) { (void)self; (void)cmd; return FIRE_NS0(FireId, FIRE_CLS("NSArray"), "array"); }
static FireId fire_dns_attributedSubstring(FireId self, FireSel cmd, FireNSRange r, FireNSRange* actual) { (void)self; (void)cmd; (void)r; (void)actual; return NULL; }
static unsigned long fire_dns_characterIndex(FireId self, FireSel cmd, FireNSPoint p) { (void)self; (void)cmd; (void)p; return (unsigned long)-1; }
static FireNSRect fire_dns_firstRect(FireId self, FireSel cmd, FireNSRange r, FireNSRange* actual) {
    FireDnsWindow* w = fire_dns_of(self);
    FireNSRect inView, inWindow;
    (void)cmd; (void)r; (void)actual;
    if (w == NULL) return fire_dns_rectMake(0, 0, 0, 0);
    /* the text cursor of the program, on the screen (Cocoa's screen: y grows upwards) */
    inView = fire_dns_rectMake(w->textX, w->textY, w->textW > 0 ? w->textW : 1, w->textH > 0 ? w->textH : 16);
    inWindow = fire_dns_rectArg(w->view, "convertRect:toView:", inView, (FireId)NULL);
#if defined(__x86_64__)
    {
        FireNSRect out;
        ((void (*)(FireNSRect*, FireId, FireSel, FireNSRect))fire_dns_msgStret())(&out, w->window, sel_registerName("convertRectToScreen:"), inWindow);
        return out;
    }
#else
    return ((FireNSRect (*)(FireId, FireSel, FireNSRect))fire_dns_msg())(w->window, sel_registerName("convertRectToScreen:"), inWindow);
#endif
}
static void fire_dns_doCommand(FireId self, FireSel cmd, FireSel which) { (void)self; (void)cmd; (void)which; }   /* (no beep for keys without text) */
/* what other programs drop */
static unsigned long fire_dns_dragEntered(FireId self, FireSel cmd, FireId info) {
    FireDnsWindow* w = fire_dns_of(self);
    FireNSPoint p;
    (void)cmd;
    if (w == NULL) return 0;
    p = FIRE_NS0(FireNSPoint, info, "draggingLocation");
    p = FIRE_NS(FireNSPoint, (FireId, FireSel, FireNSPoint, FireId), w->view, "convertPoint:fromView:", p, (FireId)NULL);
    fire_display_dropHover(&w->queue, (float)p.x, (float)p.y);
    return 1;   /* NSDragOperationCopy */
}
static void fire_dns_dragExited(FireId self, FireSel cmd, FireId info) { FireDnsWindow* w = fire_dns_of(self); (void)cmd; (void)info; if (w) fire_display_dropLeave(&w->queue); }
static signed char fire_dns_performDrag(FireId self, FireSel cmd, FireId info) {
    FireDnsWindow* w = fire_dns_of(self);
    FireId board, urls, classes, text;
    FireNSPoint p;
    (void)cmd;
    if (w == NULL) return 0;
    board = FIRE_NS0(FireId, info, "draggingPasteboard");
    p = FIRE_NS0(FireNSPoint, info, "draggingLocation");
    p = FIRE_NS(FireNSPoint, (FireId, FireSel, FireNSPoint, FireId), w->view, "convertPoint:fromView:", p, (FireId)NULL);
    classes = FIRE_NS(FireId, (FireId, FireSel, FireId), FIRE_CLS("NSArray"), "arrayWithObject:", FIRE_CLS("NSURL"));
    urls = FIRE_NS(FireId, (FireId, FireSel, FireId, FireId), board, "readObjectsForClasses:options:", classes, (FireId)NULL);
    if (urls != NULL) {
        unsigned long n = FIRE_NS0(unsigned long, urls, "count"), i;
        for (i = 0; i < n; i++) {
            FireId url = FIRE_NS(FireId, (FireId, FireSel, unsigned long), urls, "objectAtIndex:", i);
            if (FIRE_NS0(signed char, url, "isFileURL")) {
                const char* path = fire_dns_utf8(FIRE_NS0(FireId, url, "path"));
                if (path) fire_display_dropFile(&w->queue, path, strlen(path));
            }
        }
    }
    if (w->queue.dropFiles == NULL) {
        text = FIRE_NS(FireId, (FireId, FireSel, FireId), board, "stringForType:", fire_dns_str("public.utf8-plain-text"));
        if (text != NULL) { const char* s = fire_dns_utf8(text); if (s) fire_display_dropText(&w->queue, s, strlen(s)); }
    }
    fire_display_dropDone(&w->queue, (float)p.x, (float)p.y);
    return 1;
}
/* the window's delegate */
static signed char fire_dns_shouldClose(FireId self, FireSel cmd, FireId sender) { FireDnsWindow* w = fire_dns_of(self); (void)cmd; (void)sender; if (w) fire_display_queueSimple(&w->queue, FIRE_EV_CLOSE_REQUEST); return 0; }
static void fire_dns_didResize(FireId self, FireSel cmd, FireId note) {
    FireDnsWindow* w = fire_dns_of(self);
    FireNSRect b;
    int cw, ch;
    (void)cmd; (void)note;
    if (w == NULL) return;
    b = fire_dns_rect(w->view, "bounds");
    cw = (int)(b.size.width + 0.5); ch = (int)(b.size.height + 0.5);
    if (cw > 0 && ch > 0 && (cw != w->winW || ch != w->winH)) { w->winW = cw; w->winH = ch; fire_display_queueResize(&w->queue, cw, ch); }
}
static void fire_dns_didChangeBacking(FireId self, FireSel cmd, FireId note) {
    FireDnsWindow* w = fire_dns_of(self);
    double s;
    (void)cmd; (void)note;
    if (w == NULL) return;
    s = FIRE_NS0(double, w->window, "backingScaleFactor");
    if (s != w->scale) { w->scale = s; fire_display_queueScale(&w->queue, s); }
}
static void fire_dns_didBecomeKey(FireId self, FireSel cmd, FireId note) { FireDnsWindow* w = fire_dns_of(self); (void)cmd; (void)note; if (w) fire_display_queueSimple(&w->queue, FIRE_EV_FOCUS_GAINED); }
static void fire_dns_didResignKey(FireId self, FireSel cmd, FireId note) { FireDnsWindow* w = fire_dns_of(self); (void)cmd; (void)note; if (w) fire_display_queueSimple(&w->queue, FIRE_EV_FOCUS_LOST); }

/* The classes (made once): FireView (the content), FireWindowDelegate. */
static FireClass fire_dns_viewClass, fire_dns_delegateClass;
static void fire_dns_method(FireClass c, const char* name, void (*fn)(void), const char* types) { class_addMethod(c, sel_registerName(name), fn, types); }
#define FIRE_DNS_FN(f) ((void (*)(void))(f))
static void fire_dns_classes(void) {
    if (fire_dns_viewClass != NULL) return;
    fire_dns_viewClass = objc_allocateClassPair(objc_getClass("NSView"), "FireView", 0);
    class_addIvar(fire_dns_viewClass, "fireWindow", sizeof(void*), sizeof(void*) == 8 ? 3 : 2, "^v");
    fire_dns_method(fire_dns_viewClass, "isFlipped", FIRE_DNS_FN(fire_dns_yes), "c@:");
    fire_dns_method(fire_dns_viewClass, "acceptsFirstResponder", FIRE_DNS_FN(fire_dns_yes), "c@:");
    fire_dns_method(fire_dns_viewClass, "acceptsFirstMouse:", FIRE_DNS_FN(fire_dns_acceptsFirstMouse), "c@:@");
    fire_dns_method(fire_dns_viewClass, "keyDown:", FIRE_DNS_FN(fire_dns_keyDown), "v@:@");
    fire_dns_method(fire_dns_viewClass, "keyUp:", FIRE_DNS_FN(fire_dns_keyUp), "v@:@");
    fire_dns_method(fire_dns_viewClass, "flagsChanged:", FIRE_DNS_FN(fire_dns_flagsChanged), "v@:@");
    fire_dns_method(fire_dns_viewClass, "mouseDown:", FIRE_DNS_FN(fire_dns_mouseDown), "v@:@");
    fire_dns_method(fire_dns_viewClass, "mouseUp:", FIRE_DNS_FN(fire_dns_mouseUp), "v@:@");
    fire_dns_method(fire_dns_viewClass, "rightMouseDown:", FIRE_DNS_FN(fire_dns_rightDown), "v@:@");
    fire_dns_method(fire_dns_viewClass, "rightMouseUp:", FIRE_DNS_FN(fire_dns_rightUp), "v@:@");
    fire_dns_method(fire_dns_viewClass, "otherMouseDown:", FIRE_DNS_FN(fire_dns_otherDown), "v@:@");
    fire_dns_method(fire_dns_viewClass, "otherMouseUp:", FIRE_DNS_FN(fire_dns_otherUp), "v@:@");
    fire_dns_method(fire_dns_viewClass, "mouseMoved:", FIRE_DNS_FN(fire_dns_moved), "v@:@");
    fire_dns_method(fire_dns_viewClass, "mouseDragged:", FIRE_DNS_FN(fire_dns_moved), "v@:@");
    fire_dns_method(fire_dns_viewClass, "rightMouseDragged:", FIRE_DNS_FN(fire_dns_moved), "v@:@");
    fire_dns_method(fire_dns_viewClass, "otherMouseDragged:", FIRE_DNS_FN(fire_dns_moved), "v@:@");
    fire_dns_method(fire_dns_viewClass, "mouseEntered:", FIRE_DNS_FN(fire_dns_entered), "v@:@");
    fire_dns_method(fire_dns_viewClass, "mouseExited:", FIRE_DNS_FN(fire_dns_exited), "v@:@");
    fire_dns_method(fire_dns_viewClass, "scrollWheel:", FIRE_DNS_FN(fire_dns_scroll), "v@:@");
    fire_dns_method(fire_dns_viewClass, "resetCursorRects", FIRE_DNS_FN(fire_dns_resetCursorRects), "v@:");
    fire_dns_method(fire_dns_viewClass, "insertText:replacementRange:", FIRE_DNS_FN(fire_dns_insertText), "v@:@{_NSRange=QQ}");
    fire_dns_method(fire_dns_viewClass, "setMarkedText:selectedRange:replacementRange:", FIRE_DNS_FN(fire_dns_setMarkedText), "v@:@{_NSRange=QQ}{_NSRange=QQ}");
    fire_dns_method(fire_dns_viewClass, "unmarkText", FIRE_DNS_FN(fire_dns_unmarkText), "v@:");
    fire_dns_method(fire_dns_viewClass, "hasMarkedText", FIRE_DNS_FN(fire_dns_hasMarkedText), "c@:");
    fire_dns_method(fire_dns_viewClass, "markedRange", FIRE_DNS_FN(fire_dns_markedRange), "{_NSRange=QQ}@:");
    fire_dns_method(fire_dns_viewClass, "selectedRange", FIRE_DNS_FN(fire_dns_selectedRange), "{_NSRange=QQ}@:");
    fire_dns_method(fire_dns_viewClass, "validAttributesForMarkedText", FIRE_DNS_FN(fire_dns_validAttributes), "@@:");
    fire_dns_method(fire_dns_viewClass, "attributedSubstringForProposedRange:actualRange:", FIRE_DNS_FN(fire_dns_attributedSubstring), "@@:{_NSRange=QQ}^{_NSRange=QQ}");
    fire_dns_method(fire_dns_viewClass, "characterIndexForPoint:", FIRE_DNS_FN(fire_dns_characterIndex), "Q@:{CGPoint=dd}");
    fire_dns_method(fire_dns_viewClass, "firstRectForCharacterRange:actualRange:", FIRE_DNS_FN(fire_dns_firstRect), "{CGRect={CGPoint=dd}{CGSize=dd}}@:{_NSRange=QQ}^{_NSRange=QQ}");
    fire_dns_method(fire_dns_viewClass, "doCommandBySelector:", FIRE_DNS_FN(fire_dns_doCommand), "v@::");
    fire_dns_method(fire_dns_viewClass, "draggingEntered:", FIRE_DNS_FN(fire_dns_dragEntered), "Q@:@");
    fire_dns_method(fire_dns_viewClass, "draggingUpdated:", FIRE_DNS_FN(fire_dns_dragEntered), "Q@:@");
    fire_dns_method(fire_dns_viewClass, "draggingExited:", FIRE_DNS_FN(fire_dns_dragExited), "v@:@");
    fire_dns_method(fire_dns_viewClass, "performDragOperation:", FIRE_DNS_FN(fire_dns_performDrag), "c@:@");
    { void* proto = objc_getProtocol("NSTextInputClient"); if (proto) class_addProtocol(fire_dns_viewClass, proto); }
    objc_registerClassPair(fire_dns_viewClass);
    fire_dns_delegateClass = objc_allocateClassPair(objc_getClass("NSObject"), "FireWindowDelegate", 0);
    class_addIvar(fire_dns_delegateClass, "fireWindow", sizeof(void*), sizeof(void*) == 8 ? 3 : 2, "^v");
    fire_dns_method(fire_dns_delegateClass, "windowShouldClose:", FIRE_DNS_FN(fire_dns_shouldClose), "c@:@");
    fire_dns_method(fire_dns_delegateClass, "windowDidResize:", FIRE_DNS_FN(fire_dns_didResize), "v@:@");
    fire_dns_method(fire_dns_delegateClass, "windowDidChangeBackingProperties:", FIRE_DNS_FN(fire_dns_didChangeBacking), "v@:@");
    fire_dns_method(fire_dns_delegateClass, "windowDidBecomeKey:", FIRE_DNS_FN(fire_dns_didBecomeKey), "v@:@");
    fire_dns_method(fire_dns_delegateClass, "windowDidResignKey:", FIRE_DNS_FN(fire_dns_didResignKey), "v@:@");
    objc_registerClassPair(fire_dns_delegateClass);
}
static void fire_dns_setIvar(FireId obj, FireDnsWindow* w) { void* ivar = obj != NULL ? class_getInstanceVariable(object_getClass(obj), "fireWindow") : NULL; if (ivar) object_setIvar(obj, ivar, (FireId)(void*)w); }

/* The application (once): a regular one with a menu bar (the menu of the application with Quit: Cmd+Q asks every window to close). */
static void fire_dns_app(void) {
    static int ready = 0;
    FireId app, menu, appItem, appMenu, quit;
    if (ready) return;
    ready = 1;
    app = FIRE_NS0(FireId, FIRE_CLS("NSApplication"), "sharedApplication");
    FIRE_NS(signed char, (FireId, FireSel, long), app, "setActivationPolicy:", 0L);   /* NSApplicationActivationPolicyRegular */
    menu = FIRE_NS0(FireId, FIRE_NS0(FireId, FIRE_CLS("NSMenu"), "alloc"), "init");
    appItem = FIRE_NS0(FireId, FIRE_NS0(FireId, FIRE_CLS("NSMenuItem"), "alloc"), "init");
    FIRE_NS(void, (FireId, FireSel, FireId), menu, "addItem:", appItem);
    appMenu = FIRE_NS0(FireId, FIRE_NS0(FireId, FIRE_CLS("NSMenu"), "alloc"), "init");
    quit = FIRE_NS(FireId, (FireId, FireSel, FireId, FireSel, FireId), FIRE_NS0(FireId, FIRE_CLS("NSMenuItem"), "alloc"), "initWithTitle:action:keyEquivalent:",
                   fire_dns_str("Quit"), sel_registerName("performClose:"), fire_dns_str("q"));
    FIRE_NS(void, (FireId, FireSel, FireId), appMenu, "addItem:", quit);
    FIRE_NS(void, (FireId, FireSel, FireId), appItem, "setSubmenu:", appMenu);
    FIRE_NS(void, (FireId, FireSel, FireId), app, "setMainMenu:", menu);
    FIRE_NS0(void, app, "finishLaunching");
    FIRE_NS(void, (FireId, FireSel, signed char), app, "activateIgnoringOtherApps:", (signed char)1);
}

static int fire_dns_available(void) { return 1; }
static void fire_dns_threadDone(void) {}
static void fire_dns_init(FireDnsWindow* w) { memset(w, 0, sizeof *w); w->vsync = 1; w->touchMouse = 1; w->scale = 1.0; }

static int fire_dns_open(FireDnsWindow* w, const char* title, int width, int height, int vsync) {
    void* pool;
    FireId area;
    if (width <= 0 || height <= 0) return 0;
    pool = objc_autoreleasePoolPush();
    fire_dns_app();
    fire_dns_classes();
    fire_display_queueInit(&w->queue);
    w->vsync = vsync;
    w->winW = width; w->winH = height;
    w->window = FIRE_NS(FireId, (FireId, FireSel, FireNSRect, unsigned long, unsigned long, signed char), FIRE_NS0(FireId, FIRE_CLS("NSWindow"), "alloc"),
                        "initWithContentRect:styleMask:backing:defer:", fire_dns_rectMake(0, 0, width, height), 15ul /* titled, closable, miniaturizable, resizable */, 2ul /* buffered */, (signed char)0);
    if (w->window == NULL) { objc_autoreleasePoolPop(pool); return 0; }
    FIRE_NS(void, (FireId, FireSel, signed char), w->window, "setReleasedWhenClosed:", (signed char)0);
    FIRE_NS(void, (FireId, FireSel, FireId), w->window, "setTitle:", fire_dns_str(title));
    w->view = FIRE_NS(FireId, (FireId, FireSel, FireNSRect), FIRE_NS0(FireId, (FireId)(void*)fire_dns_viewClass, "alloc"), "initWithFrame:", fire_dns_rectMake(0, 0, width, height));
    fire_dns_setIvar(w->view, w);
    FIRE_NS(void, (FireId, FireSel, signed char), w->view, "setWantsLayer:", (signed char)1);
    FIRE_NS(void, (FireId, FireSel, FireId), FIRE_NS0(FireId, w->view, "layer"), "setContentsGravity:", fire_dns_str("resize"));
    FIRE_NS(void, (FireId, FireSel, FireId), w->window, "setContentView:", w->view);
    FIRE_NS(signed char, (FireId, FireSel, FireId), w->window, "makeFirstResponder:", w->view);
    FIRE_NS(void, (FireId, FireSel, signed char), w->window, "setAcceptsMouseMovedEvents:", (signed char)1);
    /* the mouse coming in and leaving, and its motion (all of the view, however large it gets) */
    area = FIRE_NS(FireId, (FireId, FireSel, FireNSRect, unsigned long, FireId, FireId), FIRE_NS0(FireId, FIRE_CLS("NSTrackingArea"), "alloc"), "initWithRect:options:owner:userInfo:",
                   fire_dns_rectMake(0, 0, 0, 0), 0x1ul | 0x2ul | 0x80ul | 0x200ul, w->view, (FireId)NULL);
    FIRE_NS(void, (FireId, FireSel, FireId), w->view, "addTrackingArea:", area);
    {
        FireId items[2], types;
        items[0] = fire_dns_str("public.file-url"); items[1] = fire_dns_str("public.utf8-plain-text");
        types = FIRE_NS(FireId, (FireId, FireSel, const FireId*, unsigned long), FIRE_CLS("NSArray"), "arrayWithObjects:count:", items, 2ul);   /* (not the variadic one: arm64) */
        FIRE_NS(void, (FireId, FireSel, FireId), w->view, "registerForDraggedTypes:", types);
    }
    w->delegate = FIRE_NS0(FireId, FIRE_NS0(FireId, (FireId)(void*)fire_dns_delegateClass, "alloc"), "init");
    fire_dns_setIvar(w->delegate, w);
    FIRE_NS(void, (FireId, FireSel, FireId), w->window, "setDelegate:", w->delegate);
    FIRE_NS0(void, w->window, "center");
    FIRE_NS(void, (FireId, FireSel, FireId), w->window, "makeKeyAndOrderFront:", (FireId)NULL);
    w->scale = FIRE_NS0(double, w->window, "backingScaleFactor");
    objc_autoreleasePoolPop(pool);
#ifdef FIRE_DISPLAY_HAS_SELFTEST
    fire_display_selftest(&w->queue, width, height);
#endif
    return 1;
}

static void fire_dns_close(FireDnsWindow* w) {
    void* pool = objc_autoreleasePoolPush();
    if (w->window != NULL) {
        FIRE_NS(void, (FireId, FireSel, FireId), w->window, "setDelegate:", (FireId)NULL);
        fire_dns_setIvar(w->view, NULL);
        fire_dns_setIvar(w->delegate, NULL);
        FIRE_NS0(void, w->window, "close");
        FIRE_NS0(void, w->window, "release");
        FIRE_NS0(void, w->view, "release");
        FIRE_NS0(void, w->delegate, "release");
        if (w->effect) FIRE_NS0(void, w->effect, "release");
        w->window = w->view = w->delegate = w->effect = NULL;
    }
    objc_autoreleasePoolPop(pool);
    free(w->bgra); w->bgra = NULL; w->bgraCap = 0;
    free(w->marked); w->marked = NULL;
    fire_display_queueFree(&w->queue);
}

static void fire_dns_setVSync(FireDnsWindow* w, int v) { w->vsync = v; }
static void fire_dns_setTouchMouse(FireDnsWindow* w, int v) { w->touchMouse = v; }
static void fire_dns_setSize(FireDnsWindow* w, int width, int height) {
    FireNSSize s;
    if (w->window == NULL) return;
    w->winW = width; w->winH = height;
    s.width = width; s.height = height;
    FIRE_NS(void, (FireId, FireSel, FireNSSize), w->window, "setContentSize:", s);
}
static void fire_dns_setCursor(FireDnsWindow* w, int c) {
    w->cursor = c;
    if (w->window) FIRE_NS(void, (FireId, FireSel, FireId), w->window, "invalidateCursorRectsForView:", w->view);
}
static void fire_dns_setTextInputRect(FireDnsWindow* w, int x, int y, int width, int height) { w->textX = x; w->textY = y; w->textW = width; w->textH = height; }

static int fire_dns_setBackdrop(FireDnsWindow* w, int kind) {
    void* pool;
    FireId clear;
    if (w->window == NULL) return FIRE_BACKDROP_NONE;
    pool = objc_autoreleasePoolPush();
    clear = FIRE_NS0(FireId, FIRE_CLS("NSColor"), kind > FIRE_BACKDROP_NONE ? "clearColor" : "windowBackgroundColor");
    FIRE_NS(void, (FireId, FireSel, signed char), w->window, "setOpaque:", (signed char)(kind == FIRE_BACKDROP_NONE));
    FIRE_NS(void, (FireId, FireSel, FireId), w->window, "setBackgroundColor:", clear);
    FIRE_NS(void, (FireId, FireSel, signed char), FIRE_NS0(FireId, w->view, "layer"), "setOpaque:", (signed char)(kind == FIRE_BACKDROP_NONE));
    if (kind >= FIRE_BACKDROP_BLUR) {
        /* NSVisualEffectView behind the view: the materials of macOS (under the window's background, a sheet-like veil, the window's background) */
        long material = kind == FIRE_BACKDROP_BLUR ? 21L : kind == FIRE_BACKDROP_ACRYLIC ? 15L : 12L;
        if (w->effect == NULL) {
            FireNSRect b = fire_dns_rect(w->view, "frame");
            w->effect = FIRE_NS(FireId, (FireId, FireSel, FireNSRect), FIRE_NS0(FireId, FIRE_CLS("NSVisualEffectView"), "alloc"), "initWithFrame:", b);
            FIRE_NS(void, (FireId, FireSel, long), w->effect, "setBlendingMode:", 0L);   /* behind the window */
            FIRE_NS(void, (FireId, FireSel, long), w->effect, "setState:", 1L);          /* always active */
            FIRE_NS(void, (FireId, FireSel, unsigned long), w->effect, "setAutoresizingMask:", 18ul);   /* width and height follow */
            FIRE_NS0(void, w->view, "retain");
            FIRE_NS(void, (FireId, FireSel, FireId), w->window, "setContentView:", w->effect);
            FIRE_NS(void, (FireId, FireSel, unsigned long), w->view, "setAutoresizingMask:", 18ul);
            FIRE_NS(void, (FireId, FireSel, FireId), w->effect, "addSubview:", w->view);
            FIRE_NS(signed char, (FireId, FireSel, FireId), w->window, "makeFirstResponder:", w->view);
        }
        FIRE_NS(void, (FireId, FireSel, long), w->effect, "setMaterial:", material);
        w->backdrop = kind;
    } else {
        if (w->effect != NULL) {
            FIRE_NS0(void, w->view, "removeFromSuperview");
            FIRE_NS(void, (FireId, FireSel, FireId), w->window, "setContentView:", w->view);
            FIRE_NS0(void, w->view, "release");
            FIRE_NS0(void, w->effect, "release");
            w->effect = NULL;
            FIRE_NS(signed char, (FireId, FireSel, FireId), w->window, "makeFirstResponder:", w->view);
        }
        w->backdrop = kind;
    }
    objc_autoreleasePoolPop(pool);
    return w->backdrop;
}

static double fire_dns_scale(FireDnsWindow* w) { return w->scale; }

static int fire_dns_pump(FireDnsWindow* w, FireDisplayEvents* out, int fbWidth, int fbHeight) {
    void* pool;
    FireId app, past, mode, e;
    if (w->window == NULL) return 0;
    pool = objc_autoreleasePoolPush();
    app = FIRE_NS0(FireId, FIRE_CLS("NSApplication"), "sharedApplication");
    past = FIRE_NS0(FireId, FIRE_CLS("NSDate"), "distantPast");
    mode = fire_dns_str("kCFRunLoopDefaultMode");
    /* (the events of the application: each goes to its window, and the window's view puts what it means into its queue) */
    while ((e = FIRE_NS(FireId, (FireId, FireSel, unsigned long long, FireId, FireId, signed char), app, "nextEventMatchingMask:untilDate:inMode:dequeue:", ~0ull, past, mode, (signed char)1)) != NULL)
        FIRE_NS(void, (FireId, FireSel, FireId), app, "sendEvent:", e);
    FIRE_NS0(void, app, "updateWindows");
    objc_autoreleasePoolPop(pool);
    if (w->closed) fire_display_queueSimple(&w->queue, FIRE_EV_CLOSE);
    fire_display_queueTake(&w->queue, out, w->winW, w->winH, fbWidth, fbHeight);
    return !w->queue.quit;
}

/* Shows the pixels as the contents of the view's layer: a CGImage of a copy (premultiplied B,G,R,A - little endian, alpha first). */
static void fire_dns_releaseCopy(void* info, const void* data, size_t size) { (void)info; (void)size; free((void*)data); }
static void fire_dns_present(FireDnsWindow* w, const uint32_t* rgba, int width, int height) {
    void *pool, *space, *provider, *image;
    uint32_t* copy;
    size_t n, i;
    int opaque = w->backdrop == FIRE_BACKDROP_NONE;
    if (w->window == NULL || w->direct || rgba == NULL || width <= 0 || height <= 0) return;
    n = (size_t)width * (size_t)height;
    copy = (uint32_t*)malloc(n * 4);
    if (copy == NULL) return;
    for (i = 0; i < n; i++) {
        uint32_t v = rgba[i], a = opaque ? 255u : v >> 24;
        copy[i] = a == 255u ? (0xFF000000u | ((v & 0xFFu) << 16) | (v & 0xFF00u) | ((v >> 16) & 0xFFu))
                            : (a << 24) | (((v & 0xFFu) * a / 255u) << 16) | ((((v >> 8) & 0xFFu) * a / 255u) << 8) | (((v >> 16) & 0xFFu) * a / 255u);
    }
    pool = objc_autoreleasePoolPush();
    space = CGColorSpaceCreateDeviceRGB();
    provider = CGDataProviderCreateWithData(NULL, copy, n * 4, fire_dns_releaseCopy);
    image = CGImageCreate((size_t)width, (size_t)height, 8, 32, (size_t)width * 4, space, (opaque ? 6u /* NoneSkipFirst */ : 2u /* PremultipliedFirst */) | (2u << 12) /* ByteOrder32Little */,
                          provider, NULL, 0, 0 /* kCGRenderingIntentDefault */);
    if (image != NULL) {
        FIRE_NS(void, (FireId, FireSel, FireId), FIRE_NS0(FireId, w->view, "layer"), "setContents:", (FireId)image);
        CGImageRelease(image);
    }
    CGDataProviderRelease(provider);
    CGColorSpaceRelease(space);
    objc_autoreleasePoolPop(pool);
    if (w->vsync) {
        double now = fire_dns_now(), next = w->lastFrame + 1.0 / 60.0;
        if (w->lastFrame > 0 && now < next) { struct timespec s; double d = next - now; s.tv_sec = (time_t)d; s.tv_nsec = (long)((d - (double)s.tv_sec) * 1e9); nanosleep(&s, NULL); }
        w->lastFrame = fire_dns_now();
    }
}

/* A presenter (scene3d's Vulkan through MoltenVK) draws into a CAMetalLayer of the view. */
static int fire_dns_native(FireDnsWindow* w, fire_native_window* out) {
    FireId layer;
    if (w->window == NULL || objc_getClass("CAMetalLayer") == NULL) return 0;
    layer = FIRE_NS0(FireId, FIRE_CLS("CAMetalLayer"), "layer");
    FIRE_NS(void, (FireId, FireSel, FireId), w->view, "setLayer:", layer);
    FIRE_NS(void, (FireId, FireSel, double), layer, "setContentsScale:", w->scale);
    out->kind = FIRE_NATIVE_METAL;
    out->b = layer;
    out->width = (int)(w->winW * w->scale + 0.5); out->height = (int)(w->winH * w->scale + 0.5);
    w->direct = 1;
    return 1;
}
static void fire_dns_restore(FireDnsWindow* w) {
    if (!w->direct || w->view == NULL) return;
    FIRE_NS(void, (FireId, FireSel, FireId), w->view, "setLayer:", FIRE_NS0(FireId, FIRE_CLS("CALayer"), "layer"));
    FIRE_NS(void, (FireId, FireSel, FireId), FIRE_NS0(FireId, w->view, "layer"), "setContentsGravity:", fire_dns_str("resize"));
    w->direct = 0;
}

static int fire_dns_startDrag(FireDnsWindow* w, const char* text, const char* files) { (void)w; (void)text; (void)files; return 0; }
static int fire_dns_canStartDrag(FireDnsWindow* w) { (void)w; return 0; }

/* ---- the general pasteboard ---- */
static FireId fire_dns_board(void) { return FIRE_NS0(FireId, FIRE_CLS("NSPasteboard"), "generalPasteboard"); }
static int fire_dns_clipboardSet(const char* text) {
    void* pool = objc_autoreleasePoolPush();
    FireId b = fire_dns_board();
    signed char ok;
    FIRE_NS0(long, b, "clearContents");
    ok = FIRE_NS(signed char, (FireId, FireSel, FireId, FireId), b, "setString:forType:", fire_dns_str(text), fire_dns_str("public.utf8-plain-text"));
    objc_autoreleasePoolPop(pool);
    return ok != 0;
}
static char* fire_dns_clipboardGet(void) {
    void* pool = objc_autoreleasePoolPush();
    FireId s = FIRE_NS(FireId, (FireId, FireSel, FireId), fire_dns_board(), "stringForType:", fire_dns_str("public.utf8-plain-text"));
    const char* u = fire_dns_utf8(s);
    char* text = u != NULL && u[0] ? (char*)fire_display_copyBytes(u, strlen(u)) : NULL;
    objc_autoreleasePoolPop(pool);
    return text;
}
static int fire_dns_clipboardHas(void) { char* t = fire_dns_clipboardGet(); int has = t != NULL; free(t); return has; }
static unsigned char* fire_dns_bytes(FireId data, size_t* size) {
    unsigned long n;
    unsigned char* out;
    *size = 0;
    if (data == NULL) return NULL;
    n = FIRE_NS0(unsigned long, data, "length");
    out = n > 0 ? fire_display_copyBytes(FIRE_NS0(const void*, data, "bytes"), n) : NULL;
    if (out != NULL) *size = n;
    return out;
}
static unsigned char* fire_dns_clipboardGetImage(size_t* size) {
    void* pool = objc_autoreleasePoolPush();
    FireId b = fire_dns_board();
    FireId data = FIRE_NS(FireId, (FireId, FireSel, FireId), b, "dataForType:", fire_dns_str("public.png"));
    unsigned char* out;
    if (data == NULL) {
        /* what most programs put there is TIFF: as PNG */
        FireId tiff = FIRE_NS(FireId, (FireId, FireSel, FireId), b, "dataForType:", fire_dns_str("public.tiff"));
        FireId rep = tiff ? FIRE_NS(FireId, (FireId, FireSel, FireId), FIRE_CLS("NSBitmapImageRep"), "imageRepWithData:", tiff) : NULL;
        if (rep) data = FIRE_NS(FireId, (FireId, FireSel, unsigned long, FireId), rep, "representationUsingType:properties:", 4ul /* PNG */, FIRE_NS0(FireId, FIRE_CLS("NSDictionary"), "dictionary"));
    }
    out = fire_dns_bytes(data, size);
    objc_autoreleasePoolPop(pool);
    return out;
}
static int fire_dns_clipboardHasImage(void) { size_t n; unsigned char* i = fire_dns_clipboardGetImage(&n); int has = i != NULL; free(i); return has; }
static int fire_dns_clipboardSetImage(const unsigned char* png, size_t pngSize, const unsigned char* bmp, size_t bmpSize) {
    void* pool = objc_autoreleasePoolPush();
    FireId b = fire_dns_board();
    FireId data = FIRE_NS(FireId, (FireId, FireSel, const void*, unsigned long), FIRE_CLS("NSData"), "dataWithBytes:length:", png, (unsigned long)pngSize);
    signed char ok;
    (void)bmp; (void)bmpSize;
    FIRE_NS0(long, b, "clearContents");
    ok = FIRE_NS(signed char, (FireId, FireSel, FireId, FireId), b, "setData:forType:", data, fire_dns_str("public.png"));
    objc_autoreleasePoolPop(pool);
    return ok != 0;
}
static char* fire_dns_clipboardGetFiles(void) {
    void* pool = objc_autoreleasePoolPush();
    FireId classes = FIRE_NS(FireId, (FireId, FireSel, FireId), FIRE_CLS("NSArray"), "arrayWithObject:", FIRE_CLS("NSURL"));
    FireId urls = FIRE_NS(FireId, (FireId, FireSel, FireId, FireId), fire_dns_board(), "readObjectsForClasses:options:", classes, (FireId)NULL);
    char* paths = NULL;
    if (urls != NULL) {
        unsigned long n = FIRE_NS0(unsigned long, urls, "count"), i;
        for (i = 0; i < n; i++) {
            FireId url = FIRE_NS(FireId, (FireId, FireSel, unsigned long), urls, "objectAtIndex:", i);
            if (FIRE_NS0(signed char, url, "isFileURL")) { const char* p = fire_dns_utf8(FIRE_NS0(FireId, url, "path")); if (p) paths = fire_display_addLine(paths, p, strlen(p)); }
        }
    }
    objc_autoreleasePoolPop(pool);
    return paths;
}
static int fire_dns_clipboardHasFiles(void) { char* p = fire_dns_clipboardGetFiles(); int has = p != NULL; free(p); return has; }
static int fire_dns_clipboardSetFiles(const char* paths) {
    void* pool = objc_autoreleasePoolPush();
    FireId list = FIRE_NS0(FireId, FIRE_CLS("NSMutableArray"), "array");
    const char* p = paths;
    signed char ok;
    while (p && *p) {
        const char* end = strchr(p, '\n');
        size_t n = end ? (size_t)(end - p) : strlen(p);
        char* one = (char*)fire_display_copyBytes(p, n);
        if (one != NULL && n > 0) FIRE_NS(void, (FireId, FireSel, FireId), list, "addObject:", FIRE_NS(FireId, (FireId, FireSel, FireId), FIRE_CLS("NSURL"), "fileURLWithPath:", fire_dns_str(one)));
        free(one);
        p = end ? end + 1 : p + n;
    }
    FIRE_NS0(long, fire_dns_board(), "clearContents");
    ok = FIRE_NS(signed char, (FireId, FireSel, FireId), fire_dns_board(), "writeObjects:", list);
    objc_autoreleasePoolPop(pool);
    return ok != 0;
}

#endif
