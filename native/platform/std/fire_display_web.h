/* fire native platform package "std": a window in the browser (fire_dweb_*, one of the displays of fire_display_desktop.h) - without SDL: a <canvas> of the page,
 * driven by a few functions of JavaScript in this file (EM_JS of Emscripten).
 *
 * The first window takes the canvas the page has for the program (Module.canvas - the canvas of Emscripten's page -, else the element with the id "fire"); a page
 * without one, and every further window, gets a canvas of its own at the end of the page. The size of a window is the size of its canvas on the page (CSS pixels);
 * the framebuffer is put into the canvas as it is (putImageData: R,G,B,A in memory is what the canvas takes) and the page stretches it. A page that sizes the canvas
 * itself (CSS) resizes the window: FIRE_EV_RESIZE.
 * Input: keys (KeyboardEvent.code -> the scan code; the key code from the layout's character), text and compositions through an invisible text field over the
 * canvas (it has the keyboard while the canvas has the focus; input methods, dead keys and the paste of the browser work there; compositions as
 * FIRE_EV_TEXT_EDITING), the mouse, the wheel and fingers (pointer events), files dropped onto the canvas (written to /tmp/drop/ of the program's file system,
 * the paths reported) and text. The clipboard is the program's own; what the user pastes (Ctrl+V) goes into it, what the program copies also goes to the browser's
 * (navigator.clipboard, when the page may).
 * Behind the window: the canvas is transparent where the framebuffer is (the page shows through); BLUR blurs the page behind it (backdrop-filter).
 * The browser draws only when the program hands the thread back: pumping and showing do (emscripten_sleep; VSync waits until 1/60 s since the last frame). */
#ifndef FIRE_DISPLAY_WEB_H
#define FIRE_DISPLAY_WEB_H

#include <emscripten.h>
#include <stdlib.h>
#include <string.h>
#include "fire_display_common.h"

/* the pages's side: a list of windows (canvas, its 2D context, the text field, the events not yet taken) */
EM_JS(int, fire_dwebjs_create, (int width, int height, const char* title), {
    var M = Module;
    if (!M.fireWins) M.fireWins = [];
    var codes = { KeyA:4,KeyB:5,KeyC:6,KeyD:7,KeyE:8,KeyF:9,KeyG:10,KeyH:11,KeyI:12,KeyJ:13,KeyK:14,KeyL:15,KeyM:16,KeyN:17,KeyO:18,KeyP:19,KeyQ:20,KeyR:21,KeyS:22,
        KeyT:23,KeyU:24,KeyV:25,KeyW:26,KeyX:27,KeyY:28,KeyZ:29,Digit1:30,Digit2:31,Digit3:32,Digit4:33,Digit5:34,Digit6:35,Digit7:36,Digit8:37,Digit9:38,Digit0:39,
        Enter:40,Escape:41,Backspace:42,Tab:43,Space:44,Minus:45,Equal:46,BracketLeft:47,BracketRight:48,Backslash:49,Semicolon:51,Quote:52,Backquote:53,Comma:54,
        Period:55,Slash:56,CapsLock:57,F1:58,F2:59,F3:60,F4:61,F5:62,F6:63,F7:64,F8:65,F9:66,F10:67,F11:68,F12:69,PrintScreen:70,ScrollLock:71,Pause:72,Insert:73,
        Home:74,PageUp:75,Delete:76,End:77,PageDown:78,ArrowRight:79,ArrowLeft:80,ArrowDown:81,ArrowUp:82,NumLock:83,NumpadDivide:84,NumpadMultiply:85,
        NumpadSubtract:86,NumpadAdd:87,NumpadEnter:88,Numpad1:89,Numpad2:90,Numpad3:91,Numpad4:92,Numpad5:93,Numpad6:94,Numpad7:95,Numpad8:96,Numpad9:97,Numpad0:98,
        NumpadDecimal:99,IntlBackslash:100,ContextMenu:101,NumpadEqual:103,F13:104,F14:105,F15:106,F16:107,F17:108,F18:109,F19:110,F20:111,F21:112,F22:113,F23:114,
        F24:115,ControlLeft:224,ShiftLeft:225,AltLeft:226,MetaLeft:227,ControlRight:228,ShiftRight:229,AltRight:230,MetaRight:231 };
    var w = { events: [], buttons: 0, mods: 0, touchMouse: 1, composing: false };
    var taken = M.fireWins.some(function (x) { return x && x.ownCanvas === false; });
    var canvas = null;
    if (!taken) canvas = M.canvas || document.getElementById("fire");
    w.ownCanvas = !canvas;
    if (!canvas) { canvas = document.createElement("canvas"); document.body.appendChild(canvas); }
    w.canvas = canvas;
    canvas.width = width; canvas.height = height;
    canvas.style.width = width + "px"; canvas.style.height = height + "px";
    canvas.style.imageRendering = "auto";
    canvas.style.touchAction = "none";
    canvas.tabIndex = 0;
    w.ctx = canvas.getContext("2d");
    if (!w.ownCanvas || M.fireWins.length == 0) document.title = UTF8ToString(title);
    /* the text field: invisible, over the canvas at the program's text cursor */
    var ta = document.createElement("textarea");
    ta.setAttribute("autocomplete", "off"); ta.setAttribute("autocorrect", "off"); ta.setAttribute("autocapitalize", "off"); ta.setAttribute("spellcheck", "false");
    ta.style.cssText = "position:absolute;opacity:0;width:1px;height:1em;padding:0;border:0;margin:0;resize:none;overflow:hidden;outline:none;z-index:-1;left:0;top:0";
    document.body.appendChild(ta);
    w.ta = ta;
    function push(t, i, f, s) { w.events.push({ t: t, i: i || [], f: f || [], s: s || "" }); }
    w.push = push;
    function modBit(code) { return ({ ShiftLeft: 1, ShiftRight: 2, ControlLeft: 64, ControlRight: 128, AltLeft: 256, AltRight: 512, MetaLeft: 1024, MetaRight: 2048 })[code] || 0; }
    function mods(e) {
        var m = w.mods;
        if (!e.shiftKey) m &= ~3; else if (!(m & 3)) m |= 1;
        if (!e.ctrlKey) m &= ~192; else if (!(m & 192)) m |= 64;
        if (!e.altKey) m &= ~768; else if (!(m & 768)) m |= 256;
        if (!e.metaKey) m &= ~3072; else if (!(m & 3072)) m |= 1024;
        if (e.getModifierState && e.getModifierState("CapsLock")) m |= 8192;
        if (e.getModifierState && e.getModifierState("NumLock")) m |= 4096;
        if (e.getModifierState && e.getModifierState("AltGraph")) m |= 16384;
        return m;
    }
    function key(e, down) {
        var sc = codes[e.code] || 0, bit = modBit(e.code), ch = 0;
        if (bit) { if (down) w.mods |= bit; else w.mods &= ~bit; }
        /* (the layout's character without shift: the key's own for letters, else only when shift is up) */
        if (e.key && e.key.length == 1 && (!e.shiftKey || e.key.toLowerCase() != e.key.toUpperCase())) ch = e.key.toLowerCase().codePointAt(0);
        push(down ? 24 : 25, [sc, ch, mods(e) | (down ? bit : 0), e.repeat ? 1 : 0]);
    }
    ta.addEventListener("keydown", function (e) {
        if (e.isComposing || e.keyCode == 229) return;
        key(e, true);
        /* the browser's own meaning of a key goes, except for what the text field needs (characters, the paste and copy of the browser) */
        var c = e.code;
        if (c == "Tab" || c == "Backspace" || c == "Enter" || c.indexOf("Arrow") == 0 || c == "PageUp" || c == "PageDown" || c == "Home" || c == "End" || /^F[0-9]+$/.test(c)
            || ((e.ctrlKey || e.metaKey) && !(e.key == "v" || e.key == "V" || e.key == "c" || e.key == "x"))) e.preventDefault();
    });
    ta.addEventListener("keyup", function (e) { if (!e.isComposing) key(e, false); });
    ta.addEventListener("input", function (e) {
        if (e.isComposing || e.inputType == "insertCompositionText") return;
        if (e.data) push(3, [], [], e.data);
        ta.value = "";
    });
    ta.addEventListener("compositionstart", function () { w.composing = true; });
    ta.addEventListener("compositionupdate", function (e) { push(5, [(e.data || "").length], [], e.data || ""); });
    ta.addEventListener("compositionend", function (e) { w.composing = false; push(5, [0], [], ""); if (e.data) push(3, [], [], e.data); ta.value = ""; });
    ta.addEventListener("paste", function (e) { var t = e.clipboardData && e.clipboardData.getData("text/plain"); if (t) push(1000, [], [], t); });
    ta.addEventListener("focus", function () { push(20); });
    ta.addEventListener("blur", function () { w.mods = 0; push(21); });
    function pos(e) { var r = canvas.getBoundingClientRect(); return [e.clientX - r.left, e.clientY - r.top]; }
    canvas.addEventListener("pointerdown", function (e) {
        var p = pos(e);
        ta.focus({ preventScroll: true });
        canvas.setPointerCapture && canvas.setPointerCapture(e.pointerId);
        if (e.pointerType == "touch") { push(16, [e.pointerId], [p[0], p[1], e.pressure || 1]); if (!w.touchMouse || !e.isPrimary) { e.preventDefault(); return; } }
        var b = [1, 2, 3, 4, 5][e.button] || 1;
        w.buttons |= 1 << (b - 1);
        push(8, [b], p);
        e.preventDefault();
    });
    canvas.addEventListener("pointerup", function (e) {
        var p = pos(e);
        if (e.pointerType == "touch") { push(18, [e.pointerId], [p[0], p[1], 0]); if (!w.touchMouse || !e.isPrimary) return; }
        var b = [1, 2, 3, 4, 5][e.button] || 1;
        w.buttons &= ~(1 << (b - 1));
        push(11, [b], p);
    });
    canvas.addEventListener("pointermove", function (e) {
        var p = pos(e);
        if (e.pointerType == "touch") { push(17, [e.pointerId], [p[0], p[1], e.pressure || 1]); if (!w.touchMouse || !e.isPrimary) return; }
        push(9, [], [p[0], p[1], e.movementX || 0, e.movementY || 0]);
    });
    canvas.addEventListener("pointerenter", function () { push(13); });
    canvas.addEventListener("pointerleave", function () { push(14); });
    canvas.addEventListener("contextmenu", function (e) { e.preventDefault(); });
    canvas.addEventListener("wheel", function (e) {
        var p = pos(e), k = e.deltaMode == 1 ? 1 / 3 : e.deltaMode == 2 ? 1 : 1 / 100;
        push(12, [], [p[0], p[1], e.deltaX * k, -e.deltaY * k]);
        e.preventDefault();
    }, { passive: false });
    canvas.addEventListener("dragover", function (e) { e.preventDefault(); var p = pos(e); push(42, [], p); });
    canvas.addEventListener("dragleave", function () { push(43); });
    canvas.addEventListener("drop", function (e) {
        e.preventDefault();
        var p = pos(e), dt = e.dataTransfer, files = dt && dt.files ? Array.prototype.slice.call(dt.files) : [];
        if (files.length == 0) { var t = dt ? dt.getData("text/plain") : ""; push(41, [], p, t || ""); return; }
        /* the files into the program's file system (/tmp/drop/), then the paths */
        var left = files.length, paths = [];
        try { FS.mkdirTree("/tmp/drop"); } catch (x) {}
        files.forEach(function (f) {
            f.arrayBuffer().then(function (buf) {
                var path = "/tmp/drop/" + f.name.split("/").join("_").split(String.fromCharCode(92)).join("_");
                try { FS.writeFile(path, new Uint8Array(buf)); paths.push(path); } catch (x) {}
                if (--left == 0) push(40, [], p, paths.join("\n"));
            }, function () { if (--left == 0) push(40, [], p, paths.join("\n")); });
        });
    });
    var id = M.fireWins.length;
    for (var k = 0; k < M.fireWins.length; k++) if (!M.fireWins[k]) { id = k; break; }
    M.fireWins[id] = w;
    return id;
});
EM_JS(void, fire_dwebjs_destroy, (int id), {
    var w = Module.fireWins && Module.fireWins[id];
    if (!w) return;
    if (w.ta && w.ta.parentNode) w.ta.parentNode.removeChild(w.ta);
    if (w.ownCanvas && w.canvas.parentNode) w.canvas.parentNode.removeChild(w.canvas);
    Module.fireWins[id] = null;
});
/* (only the area x, y, aw, ah of it - the canvas keeps the rest; a canvas of another size is cleared: all of it) */
EM_JS(void, fire_dwebjs_put, (int id, const void* pixels, int width, int height, int x, int y, int aw, int ah), {
    var w = Module.fireWins[id];
    if (!w) return;
    if (w.canvas.width != width || w.canvas.height != height) { w.canvas.width = width; w.canvas.height = height; x = 0; y = 0; aw = width; ah = height; }
    if (aw <= 0 || ah <= 0) return;
    w.ctx.putImageData(new ImageData(new Uint8ClampedArray(HEAPU8.buffer, pixels, width * height * 4), width, height), 0, 0, x, y, aw, ah);
});
EM_JS(void, fire_dwebjs_setCssSize, (int id, int width, int height), {
    var w = Module.fireWins[id];
    if (w) { w.canvas.style.width = width + "px"; w.canvas.style.height = height + "px"; }
});
/* The size of the canvas on the page (CSS pixels): out[0], out[1]. */
EM_JS(void, fire_dwebjs_cssSize, (int id, int* out), {
    var w = Module.fireWins[id];
    if (!w) return;
    var r = w.canvas.getBoundingClientRect();
    HEAP32[out >> 2] = Math.round(r.width); HEAP32[(out >> 2) + 1] = Math.round(r.height);
});
/* The next event: its type (0: none), up to 4 numbers into ints, up to 4 into nums, its text (UTF-8) into text (cap bytes). */
EM_JS(int, fire_dwebjs_next, (int id, int* ints, double* nums, char* text, int cap), {
    var w = Module.fireWins[id];
    if (!w || w.events.length == 0) return 0;
    var e = w.events.shift(), k;
    for (k = 0; k < 4; k++) { HEAP32[(ints >> 2) + k] = e.i[k] || 0; HEAPF64[(nums >> 3) + k] = e.f[k] || 0; }
    stringToUTF8(e.s || "", text, cap);
    return e.t;
});
EM_JS(int, fire_dwebjs_textLength, (int id), {
    var w = Module.fireWins[id];
    return w && w.events.length ? lengthBytesUTF8(w.events[0].s || "") + 1 : 0;
});
EM_JS(void, fire_dwebjs_setCursor, (int id, int c), {
    var w = Module.fireWins[id];
    var names = ["default", "text", "wait", "crosshair", "progress", "nwse-resize", "nesw-resize", "ew-resize", "ns-resize", "move", "not-allowed", "pointer"];
    if (w) w.canvas.style.cursor = names[c] || "default";
});
EM_JS(void, fire_dwebjs_setTextRect, (int id, int x, int y, int width, int height), {
    var w = Module.fireWins[id];
    if (!w) return;
    var r = w.canvas.getBoundingClientRect();
    w.ta.style.left = (r.left + window.scrollX + x) + "px"; w.ta.style.top = (r.top + window.scrollY + y) + "px";
    w.ta.style.height = Math.max(height, 8) + "px"; w.ta.style.fontSize = Math.max(height * 0.8, 8) + "px";
});
EM_JS(int, fire_dwebjs_setBackdrop, (int id, int kind), {
    var w = Module.fireWins[id];
    if (!w) return 0;
    w.canvas.style.background = kind > 0 ? "transparent" : "#000";
    var blur = kind >= 2 ? "blur(12px) saturate(1.2)" : "";
    w.canvas.style.backdropFilter = blur; w.canvas.style.webkitBackdropFilter = blur;
    return kind >= 2 ? 2 : kind;
});
EM_JS(void, fire_dwebjs_setTouchMouse, (int id, int v), { var w = Module.fireWins[id]; if (w) w.touchMouse = v; });
EM_JS(double, fire_dwebjs_scale, (void), { return window.devicePixelRatio || 1; });
EM_JS(void, fire_dwebjs_copy, (const char* text), {
    try { if (navigator.clipboard && navigator.clipboard.writeText) navigator.clipboard.writeText(UTF8ToString(text)).catch(function () {}); } catch (x) {}
});

typedef struct FireDwebWindow {
    int id, open;
    int winW, winH;
    int vsync, touchMouse;
    double lastFrame, scale;
    FireDisplayQueue queue;
} FireDwebWindow;

static int fire_dweb_available(void) { return 1; }
static void fire_dweb_threadDone(void) {}
static void fire_dweb_init(FireDwebWindow* w) { memset(w, 0, sizeof *w); w->vsync = 1; w->touchMouse = 1; w->id = -1; w->scale = 1.0; }

static int fire_dweb_open(FireDwebWindow* w, const char* title, int width, int height, int vsync) {
    if (width <= 0 || height <= 0) return 0;
    fire_display_queueInit(&w->queue);
    w->id = fire_dwebjs_create(width, height, title ? title : "");
    if (w->id < 0) return 0;
    w->open = 1; w->vsync = vsync; w->winW = width; w->winH = height;
    w->scale = fire_dwebjs_scale();
#ifdef FIRE_DISPLAY_HAS_SELFTEST
    fire_display_selftest(&w->queue, width, height);
#endif
    return 1;
}
static void fire_dweb_close(FireDwebWindow* w) {
    if (w->open) fire_dwebjs_destroy(w->id);
    w->open = 0;
    fire_display_queueFree(&w->queue);
}
static void fire_dweb_setVSync(FireDwebWindow* w, int v) { w->vsync = v; }
static void fire_dweb_setTouchMouse(FireDwebWindow* w, int v) { w->touchMouse = v; if (w->open) fire_dwebjs_setTouchMouse(w->id, v); }
static void fire_dweb_setSize(FireDwebWindow* w, int width, int height) { if (!w->open) return; w->winW = width; w->winH = height; fire_dwebjs_setCssSize(w->id, width, height); }
static void fire_dweb_setCursor(FireDwebWindow* w, int c) { if (w->open) fire_dwebjs_setCursor(w->id, c); }
static void fire_dweb_setTextInputRect(FireDwebWindow* w, int x, int y, int width, int height) { if (w->open) fire_dwebjs_setTextRect(w->id, x, y, width, height); }
static int fire_dweb_setBackdrop(FireDwebWindow* w, int kind) { return w->open ? fire_dwebjs_setBackdrop(w->id, kind) : FIRE_BACKDROP_NONE; }
static double fire_dweb_scale(FireDwebWindow* w) { (void)w; return fire_dwebjs_scale(); }

static int fire_dweb_pump(FireDwebWindow* w, FireDisplayEvents* out, int fbWidth, int fbHeight) {
    int ints[4], type, size[2];
    double nums[4];
    double s;
    if (!w->open) return 0;
    emscripten_sleep(0);   /* (the browser delivers events only while the program is not running) */
    for (;;) {
        int need = fire_dwebjs_textLength(w->id);
        char small[256];
        char* text = need > (int)sizeof small ? (char*)malloc((size_t)need) : small;
        if (text == NULL) break;
        type = fire_dwebjs_next(w->id, ints, nums, text, need > (int)sizeof small ? need : (int)sizeof small);
        if (type == 0) { if (text != small) free(text); break; }
        switch (type) {
            case FIRE_EV_KEY_DOWN: case FIRE_EV_KEY_UP: fire_display_queueKey(&w->queue, type == FIRE_EV_KEY_DOWN, fire_key_code(ints[0], (uint32_t)ints[1]), ints[0], ints[2], ints[3]); break;
            case FIRE_EV_TEXT_INPUT: fire_display_queueText(&w->queue, text, strlen(text)); break;
            case FIRE_EV_TEXT_EDITING: fire_display_queueEditing(&w->queue, text, strlen(text), ints[0], 0); break;
            case FIRE_EV_MOUSE_DOWN: case FIRE_EV_MOUSE_UP: fire_display_queueButton(&w->queue, type == FIRE_EV_MOUSE_DOWN, ints[0], (float)nums[0], (float)nums[1]); break;
            case FIRE_EV_MOUSE_MOVE: fire_display_queueMotion2(&w->queue, (float)nums[0], (float)nums[1], 1, (float)nums[2], (float)nums[3]); break;
            case FIRE_EV_MOUSE_SCROLL: fire_display_queueScroll(&w->queue, (float)nums[2], (float)nums[3], (float)nums[0], (float)nums[1]); break;
            case FIRE_EV_TOUCH_DOWN: case FIRE_EV_TOUCH_MOVE: case FIRE_EV_TOUCH_UP: fire_display_queueTouch(&w->queue, type, ints[0], (float)nums[0], (float)nums[1], (float)nums[2]); break;
            case FIRE_EV_DRAG_HOVER: fire_display_dropHover(&w->queue, (float)nums[0], (float)nums[1]); break;
            case FIRE_EV_DRAG_LEAVE: fire_display_dropLeave(&w->queue); break;
            case FIRE_EV_DROP_FILES:
                /* (no file could be written - a program without a file system: the drag just leaves) */
                if (text[0]) { fire_display_dropFile(&w->queue, text, strlen(text)); fire_display_dropDone(&w->queue, (float)nums[0], (float)nums[1]); } else fire_display_dropLeave(&w->queue);
                break;
            case FIRE_EV_DROP_TEXT: fire_display_dropText(&w->queue, text, strlen(text)); fire_display_dropDone(&w->queue, (float)nums[0], (float)nums[1]); break;
            case 1000: fire_display_memSetText(text); break;   /* (the user pasted: the program's clipboard has it) */
            default: fire_display_queueSimple(&w->queue, type); break;   /* focus, the mouse coming and going */
        }
        if (text != small) free(text);
    }
    /* the page resized the canvas (CSS), or the program's size is still on its way */
    fire_dwebjs_cssSize(w->id, size);
    if (size[0] > 0 && size[1] > 0 && (size[0] != w->winW || size[1] != w->winH)) { w->winW = size[0]; w->winH = size[1]; fire_display_queueResize(&w->queue, w->winW, w->winH); }
    s = fire_dwebjs_scale();
    if (s != w->scale) { w->scale = s; fire_display_queueScale(&w->queue, s); }
    fire_display_queueTake(&w->queue, out, w->winW, w->winH, fbWidth, fbHeight);
    return !w->queue.quit;
}

/* Shows the frame; only (ax, ay, aw, ah) changed since the last one: only it goes into the canvas. */
static void fire_dweb_presentArea(FireDwebWindow* w, const uint32_t* rgba, int width, int height, int ax, int ay, int aw, int ah) {
    int x0, y0, x1, y1;
    if (!w->open || rgba == NULL || width <= 0 || height <= 0) return;
    x0 = ax < 0 ? 0 : ax > width ? width : ax; x1 = ax + aw < 0 ? 0 : ax + aw > width ? width : ax + aw;
    y0 = ay < 0 ? 0 : ay > height ? height : ay; y1 = ay + ah < 0 ? 0 : ay + ah > height ? height : ay + ah;
    fire_dwebjs_put(w->id, rgba, width, height, x0, y0, x1 - x0, y1 - y0);
    if (w->vsync) {
        double now = emscripten_get_now(), next = w->lastFrame + 1000.0 / 60.0;
        emscripten_sleep(w->lastFrame > 0 && now < next ? (unsigned)(next - now) : 0);   /* (the page draws it while the thread is handed back) */
        w->lastFrame = emscripten_get_now();
    } else emscripten_sleep(0);
}

static void fire_dweb_present(FireDwebWindow* w, const uint32_t* rgba, int width, int height) { fire_dweb_presentArea(w, rgba, width, height, 0, 0, width, height); }

static int fire_dweb_native(FireDwebWindow* w, fire_native_window* out) { (void)w; (void)out; return 0; }
static void fire_dweb_restore(FireDwebWindow* w) { (void)w; }
static int fire_dweb_startDrag(FireDwebWindow* w, const char* text, const char* files) { (void)w; (void)text; (void)files; return 0; }
static int fire_dweb_canStartDrag(FireDwebWindow* w) { (void)w; return 0; }

/* the program's own clipboard; text it copies also goes to the browser's */
static int fire_dweb_clipboardSet(const char* text) { fire_dwebjs_copy(text); return fire_display_memSetText(text); }
static char* fire_dweb_clipboardGet(void) { return fire_display_memGetText(); }
static int fire_dweb_clipboardHas(void) { return fire_display_memHasText(); }
static unsigned char* fire_dweb_clipboardGetImage(size_t* size) { return fire_display_memGetImage(size); }
static int fire_dweb_clipboardSetImage(const unsigned char* png, size_t pngSize, const unsigned char* bmp, size_t bmpSize) { (void)bmp; (void)bmpSize; return fire_display_memSetImage(png, pngSize); }
static int fire_dweb_clipboardHasImage(void) { return fire_display_memHasImage(); }
static char* fire_dweb_clipboardGetFiles(void) { return fire_display_memGetFiles(); }
static int fire_dweb_clipboardSetFiles(const char* paths) { return fire_display_memSetFiles(paths); }
static int fire_dweb_clipboardHasFiles(void) { return fire_display_memHasFiles(); }

#endif
