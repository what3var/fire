// fire native bridge "windows": the natives behind `#import "windows"` - a window that shows a framebuffer of the graphics bridge, and the events of that window.
// The fire side (Window, EventType - fire source) is the same as in the VM; this file is what its `__GRPHWin...` functions do. Putting the pixels on the screen and collecting the
// events is the display of the platform (FIRE_PLATFORM_DISPLAY_HEADER: fire::plat::disp::Window; SDL2 on the desktop, see platform/std/fire_display_sdl.hpp).
//
// Included by the generated file after fire_bridge_graphics.hpp when the program imports "windows". As in the VM, `Tick` collects the events, shows the framebuffer and then
// runs the callbacks (registered lambdas) on the calling thread with the real global variables; an exception that a callback does not catch is reported and ends only the callback.
#pragma once

#include FIRE_PLATFORM_DISPLAY_HEADER

namespace fire {
namespace win {

struct Callback { int type; Value lam; };

struct WindowState {
    plat::disp::Window display;
    gfx::Framebuffer* fb = nullptr;
    std::vector<Callback> callbacks;
    std::vector<plat::disp::Event> queue;   // the events for NextEvent (only once EnableEvents was called)
    size_t queueHead = 0;
    bool queueOn = false;
    bool autoResize = false;   // a resize of the window resizes the framebuffer (Window.AutoResize)
    ~WindowState() {
        for (Callback& c : callbacks) release(c.lam);
        if (fb) gfx::fbRelease(fb);
    }
};

struct Windows {
    std::vector<WindowState*> list{1, nullptr};
    ~Windows() { for (WindowState* w : list) delete w; }
};
inline Windows& windows() { static Windows w; return w; }
constexpr size_t MaxQueuedEvents = 4096;

inline WindowState* windowOf(Value id) {
    int64_t i = (int32_t)id.i;
    Windows& w = windows();
    if (i > 0 && (size_t)i < w.list.size() && w.list[(size_t)i]) return w.list[(size_t)i];
    fatal(("No resource with ID " + std::to_string(i) + " (unknown or already destroyed).").c_str());
}

/// A string argument as UTF-8.
inline std::string toUtf8(Value v) {
    const Str* s = strOf(v);
    const char16_t* c = strChars(const_cast<Str*>(s));
    std::string out;
    for (uint32_t i = 0; i < s->length; i++) {
        uint32_t u = c[i];
        if (u >= 0xD800 && u < 0xDC00 && i + 1 < s->length && c[i + 1] >= 0xDC00 && c[i + 1] < 0xE000) { u = 0x10000 + ((u - 0xD800) << 10) + (c[i + 1] - 0xDC00); i++; }
        if (u < 0x80) out += (char)u;
        else if (u < 0x800) { out += (char)(0xC0 | (u >> 6)); out += (char)(0x80 | (u & 0x3F)); }
        else if (u < 0x10000) { out += (char)(0xE0 | (u >> 12)); out += (char)(0x80 | ((u >> 6) & 0x3F)); out += (char)(0x80 | (u & 0x3F)); }
        else { out += (char)(0xF0 | (u >> 18)); out += (char)(0x80 | ((u >> 12) & 0x3F)); out += (char)(0x80 | ((u >> 6) & 0x3F)); out += (char)(0x80 | (u & 0x3F)); }
    }
    return out;
}
inline Value utf8Str(const std::string& text, OwnList* list) {
    std::u16string w;
    for (size_t i = 0; i < text.size();) {
        unsigned char b = (unsigned char)text[i];
        uint32_t u; size_t n;
        if (b < 0x80) { u = b; n = 1; } else if ((b >> 5) == 6) { u = b & 0x1F; n = 2; } else if ((b >> 4) == 14) { u = b & 0x0F; n = 3; } else if ((b >> 3) == 30) { u = b & 0x07; n = 4; } else { u = 0xFFFD; n = 1; }
        if (i + n > text.size()) { u = 0xFFFD; n = 1; } else for (size_t k = 1; k < n; k++) u = (u << 6) | ((unsigned char)text[i + k] & 0x3F);
        i += n;
        if (u >= 0x10000) { u -= 0x10000; w += (char16_t)(0xD800 + (u >> 10)); w += (char16_t)(0xDC00 + (u & 0x3FF)); } else w += (char16_t)u;
    }
    Str* s = allocStr((uint32_t)w.size(), list);
    if (!w.empty()) std::memcpy(strChars(s), w.data(), w.size() * sizeof(char16_t));
    return StrV(s);
}

/// The number of parameters a callback for this event type must have (EventCallback.CheckParameters).
inline uint32_t callbackParams(int type) {
    switch (type) {
        case plat::disp::EV_KEY_DOWN: case plat::disp::EV_KEY_UP: case plat::disp::EV_MOUSE_SCROLL: return 4;
        case plat::disp::EV_MOUSE_MOVE_REL: case plat::disp::EV_MOUSE_MOVE: case plat::disp::EV_MOUSE_UP: case plat::disp::EV_MOUSE_DOWN: return 3;
        case plat::disp::EV_TEXT_INPUT: return 1;
        case plat::disp::EV_RESIZE: return 2;
        default: return 0;
    }
}

/// Runs one callback like a nested lambda: the real globals; an exception ends only the callback.
inline void runCallback(Value lam, int argc, const Value* args) {
    Handler* savedHandlers = g_handlers;
    g_handlers = nullptr;
    OwnList scratch = {nullptr, nullptr, poolMark(), 0, nullptr, nullptr};
    callLam(lam, argc, args, &scratch);
    if (g_unwind.active) {
        Value ex = g_unwind.value;
        clearUnwind();
        std::fprintf(stderr, "(unhandled exception in the callback: %s)\n", ex.kind == K_Class ? className(asObj(ex)->cls) : "exception");
    }
    g_handlers = savedHandlers;
    leave(&scratch);
}

inline void dispatch(WindowState* w, const plat::disp::Event& e) {
    using namespace plat::disp;
    std::vector<Callback> mine;   // a callback may register or unregister callbacks
    for (const Callback& c : w->callbacks) if (c.type == e.type) { mine.push_back(c); retain(c.lam); }
    for (const Callback& c : mine) {
        OwnList scratch = {nullptr, nullptr, poolMark(), 0, nullptr, nullptr};
        Value a[4];
        int n = 0;
        switch (e.type) {
            case EV_KEY_DOWN: case EV_KEY_UP: a[0] = Int(e.keyCode); a[1] = Int(e.scanCode); a[2] = Int(e.modifier); a[3] = Bool(e.repeat); n = 4; break;
            case EV_MOUSE_DOWN: case EV_MOUSE_UP: a[0] = Int(e.button); a[1] = Float(e.x); a[2] = Float(e.y); n = 3; break;
            case EV_MOUSE_MOVE: a[0] = Float(e.x); a[1] = Float(e.y); a[2] = Int(e.buttonState); n = 3; break;
            case EV_MOUSE_MOVE_REL: a[0] = Float(e.xrel); a[1] = Float(e.yrel); a[2] = Int(e.buttonState); n = 3; break;
            case EV_MOUSE_SCROLL: a[0] = Float(e.scrollX); a[1] = Float(e.scrollY); a[2] = Float(e.x); a[3] = Float(e.y); n = 4; break;
            case EV_TEXT_INPUT: a[0] = utf8Str(e.text, &scratch); n = 1; break;
            case EV_RESIZE: a[0] = Int(e.width); a[1] = Int(e.height); n = 2; break;
            default: break;
        }
        runCallback(c.lam, n, a);
        leave(&scratch);
        release(c.lam);
    }
}

inline Value WinCreate(Value fbId, Value title) {
    gfx::Framebuffer* fb = gfx::fbOf(fbId);
    WindowState* w = new WindowState();
    if (!w->display.open(toUtf8(title), fb->width, fb->height, true)) { delete w; return Int(-1); }
    w->fb = fb;
    gfx::fbRetain(fb);
    Windows& all = windows();
    all.list.push_back(w);
    return Int((int64_t)all.list.size() - 1);
}
inline Value WinDestroy(Value id) {
    int64_t i = (int32_t)id.i;
    Windows& all = windows();
    if (i > 0 && (size_t)i < all.list.size() && all.list[(size_t)i]) { delete all.list[(size_t)i]; all.list[(size_t)i] = nullptr; return Bool(true); }
    return Bool(false);
}
inline Value WinTick(Value id) {
    WindowState* w = windowOf(id);
    std::vector<plat::disp::Event> events;
    bool open = w->display.pump(events, w->fb->width, w->fb->height);
    if (w->autoResize)
        for (const plat::disp::Event& e : events)
            if (e.type == plat::disp::EV_RESIZE && gfx::Framebuffer::validSize(e.width, e.height)) w->fb->resize(e.width, e.height);
    std::vector<uint32_t> pixels(w->fb->pixelCount());
    w->fb->resolveTo(pixels.data());
    w->display.present(pixels.data(), w->fb->width, w->fb->height);
    if (w->queueOn) for (const plat::disp::Event& e : events) if (w->queue.size() - w->queueHead < MaxQueuedEvents) w->queue.push_back(e);
    int64_t i = (int32_t)id.i;
    for (const plat::disp::Event& e : events) {
        dispatch(w, e);
        if (!windows().list[(size_t)i]) break;   // a callback destroyed the window
    }
    return Bool(open);
}
inline Value WinEnableEvents(Value id) { windowOf(id)->queueOn = true; return Bool(true); }
inline Value WinNextEvent(Value id, OwnList* list) {
    WindowState* w = windowOf(id);
    using namespace plat::disp;
    if (!w->queueOn || w->queueHead >= w->queue.size()) return Undef();
    Event e = w->queue[w->queueHead++];
    if (w->queueHead == w->queue.size()) { w->queue.clear(); w->queueHead = 0; }
    auto px = [](float v) { return Int((int64_t)std::floor(v)); };
    Value items[5];
    uint32_t n = 0;
    items[n++] = Int(e.type);
    switch (e.type) {
        case EV_KEY_DOWN: case EV_KEY_UP: items[n++] = Int(e.keyCode); items[n++] = Int(e.scanCode); items[n++] = Int(e.modifier); items[n++] = Bool(e.repeat); break;
        case EV_MOUSE_DOWN: case EV_MOUSE_UP: items[n++] = Int(e.button); items[n++] = px(e.x); items[n++] = px(e.y); break;
        case EV_MOUSE_MOVE: items[n++] = px(e.x); items[n++] = px(e.y); items[n++] = Int(e.buttonState); break;
        case EV_MOUSE_MOVE_REL: items[n++] = px(e.xrel); items[n++] = px(e.yrel); items[n++] = Int(e.buttonState); break;
        case EV_MOUSE_SCROLL: items[n++] = Float(e.scrollX); items[n++] = Float(e.scrollY); items[n++] = px(e.x); items[n++] = px(e.y); break;
        case EV_TEXT_INPUT: items[n++] = utf8Str(e.text, list); break;
        case EV_RESIZE: items[n++] = Int(e.width); items[n++] = Int(e.height); break;
        default: break;
    }
    Arr* a = allocArr(n, list);
    for (uint32_t k = 0; k < n; k++) { a->items()[k] = items[k]; retain(items[k]); }
    return ArrV(a);
}
inline Value WinRegisterEvent(Value id, Value type, Value lam) {
    WindowState* w = windowOf(id);
    if (lam.kind != K_Lambda) return Bool(false);
    int t = (int32_t)type.i;
    if (lamOf(lam)->nparams != callbackParams(t)) return Bool(false);
    retain(lam);
    w->callbacks.push_back({t, lam});
    return Bool(true);
}
inline Value WinSetVSync(Value id, Value on) { windowOf(id)->display.setVSync(on.i != 0); return Undef(); }
inline Value WinGetVSync(Value id) { return Bool(windowOf(id)->display.vsync()); }
inline Value WinSetAutoResize(Value id, Value on) { windowOf(id)->autoResize = on.i != 0; return Undef(); }
inline Value WinGetAutoResize(Value id) { return Bool(windowOf(id)->autoResize); }

}  // namespace win
}  // namespace fire
