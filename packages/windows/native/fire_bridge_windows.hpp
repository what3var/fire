// fire native bridge "windows": the natives behind `#import "windows"` - a window that shows a framebuffer of the graphics bridge, and the events of that window.
// The fire side (Window, EventType - fire source) is the same as in the VM; this file is what its `__GRPHWin...` functions do. Putting the pixels on the screen and collecting the
// events is the display of the platform (FIRE_PLATFORM_DISPLAY_HEADER: fire::plat::disp::Window; SDL2 on the desktop, see platform/std/fire_display_sdl.hpp).
//
// Included by the generated file after fire_bridge_graphics.hpp when the program imports "windows". As in the VM, `Tick` collects the events, shows the framebuffer and then
// runs the callbacks (registered lambdas) on the calling thread with the real global variables; an exception that a callback does not catch is reported and ends only the callback.
#pragma once

#include FIRE_PLATFORM_DISPLAY_HEADER
#include <algorithm>
#include <atomic>

namespace fire {
namespace win {

struct Callback { int type; Value lam; };

// A window manager's thread needs the fire threads (the GIL, host services; the generator switches them on when the program can start one), not the
// browser. Where windows may only live on the main thread (macOS; FIRE_WINDOW_COOPERATIVE elsewhere, for tests) the thread only gives the beat: it asks the
// program for a cycle about 60 times a second, and the program opens, pumps and shows the windows itself (FIRE_WINDOW_COOP).
#if defined(FIRE_THREADS) && !defined(__EMSCRIPTEN__)
#define FIRE_WINDOW_THREADS 1
#if defined(__APPLE__) || defined(FIRE_WINDOW_COOPERATIVE)
#define FIRE_WINDOW_COOP 1
#endif
using WinMutex = plat::Mutex;
#else
struct WinMutex { void lock() {} void unlock() {} };
#endif

// What the program and the thread of a window manager share of one window. The OS window (display) belongs to the thread that opens and pumps it: the
// program's thread (Tick, a manager that runs there) or the manager's thread (Window.Show) - SDL serves a window only on the thread that created it.
struct Shared {
    WinMutex m;
    plat::disp::Window display;
    std::string title;
    int width = 0, height = 0;        // the size it opens with
    bool opened = false, failed = false;
    // between the program and the manager's thread (under m)
    std::vector<plat::disp::Event> inbox;   // the events the thread fetched
    bool closed = false;              // the thread saw the window close
    std::vector<uint32_t> front;      // the frame the program published last
    int frontW = 0, frontH = 0;
    bool frontNew = false;
    int vsyncReq = -1, touchReq = -1; // settings for the thread
    int sizeReqW = 0, sizeReqH = 0;
    int cursorReq = -1;               // a new cursor for the thread (-1: none)   // a new size of the window for the thread (0: none)
    bool release = false;             // the window is gone: the thread closes it and frees this
    // the thread's own
    std::vector<uint32_t> shown;
    int shownW = 0, shownH = 0;
};

struct Mgr;

struct WindowState {
    Shared* sh = nullptr;
    gfx::Framebuffer* fb = nullptr;
    Mgr* mgr = nullptr;
    bool vsync = true;
    std::vector<Callback> callbacks;
    std::vector<plat::disp::Event> queue;   // the events for NextEvent (only once EnableEvents was called)
    size_t queueHead = 0;
    bool queueOn = false;
    bool touchMouse = true;    // Window.TouchMouse
    bool autoResize = false;   // a resize of the window resizes the framebuffer (Window.AutoResize)
    bool blocked = false;      // a modal window of its manager is open: its input is dropped (Window.Show(true))
    bool autoClose = true;     // a close request (the frame's button) closes the window (Window.AutoClose)
    bool closeRequested = false;
    ~WindowState() {
        for (Callback& c : callbacks) release(c.lam);
        if (fb) gfx::fbRelease(fb);
        delete sh;
    }
};

struct Windows {
    std::vector<WindowState*> list{1, nullptr};
    ~Windows();
};
inline void managersFirst();
// (the managers are made first, so they are destroyed last: the windows stop their threads when they go)
inline Windows& windows() { managersFirst(); static Windows w; return w; }
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
        case plat::disp::EV_RESIZE: case plat::disp::EV_JOY_BUTTON_DOWN: case plat::disp::EV_JOY_BUTTON_UP: return 2;
        case plat::disp::EV_JOY_AXIS: case plat::disp::EV_JOY_HAT: return 3;
        case plat::disp::EV_TOUCH_DOWN: case plat::disp::EV_TOUCH_MOVE: case plat::disp::EV_TOUCH_UP: return 4;
        case plat::disp::EV_JOY_ADDED: case plat::disp::EV_JOY_REMOVED: return 1;
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
            case EV_TOUCH_DOWN: case EV_TOUCH_MOVE: case EV_TOUCH_UP: a[0] = Int(e.finger); a[1] = Float(e.x); a[2] = Float(e.y); a[3] = Float(e.pressure); n = 4; break;
            case EV_JOY_AXIS: a[0] = Int(e.joystick); a[1] = Int(e.index); a[2] = Float(e.value); n = 3; break;
            case EV_JOY_HAT: a[0] = Int(e.joystick); a[1] = Int(e.index); a[2] = Int((int64_t)e.value); n = 3; break;
            case EV_JOY_BUTTON_DOWN: case EV_JOY_BUTTON_UP: a[0] = Int(e.joystick); a[1] = Int(e.index); n = 2; break;
            case EV_JOY_ADDED: case EV_JOY_REMOVED: a[0] = Int(e.joystick); n = 1; break;
            default: break;
        }
        runCallback(c.lam, n, a);
        leave(&scratch);
        release(c.lam);
    }
}

// ---------------------------------------------------------------------------------------------------------------------------------------------
// Window managers (fire's WindowManager, docs/UI.md): a manager cycles the windows attached to it - on the program's thread (Tick/Run), or, once started,
// with a thread of its own that opens the OS windows, fetches their events and shows the last frame the program published, about 60 times a second. The
// program's side of a cycle (callbacks, the queue, drawing) still runs on the program's thread: the thread asks the main program (hostRequest) to run the
// manager's service lambda at its next safe point.
// ---------------------------------------------------------------------------------------------------------------------------------------------
struct Mgr {
    WinMutex m;
    std::vector<Shared*> shown;           // the windows its thread shows (under m)
    std::vector<WindowState*> attached;   // the program's view
    std::atomic<bool> stop{false};
    std::atomic<uint32_t> pending{0};     // a cycle was asked for and has not run yet
    Value service = {K_Undefined, 0, 0, 0, {0}};
#ifdef FIRE_WINDOW_THREADS
    plat::Thread* thread = nullptr;
    plat::Mutex cycleM;
    plat::CondVar cycleCv;
    uint64_t cycles = 0;
    bool started() const { return thread != nullptr; }
#ifdef FIRE_WINDOW_COOP
    bool ownsWindows() const { return false; }
#else
    bool ownsWindows() const { return thread != nullptr; }
#endif
#else
    bool started() const { return false; }
    bool ownsWindows() const { return false; }
#endif
};

struct Managers {
    std::vector<Mgr*> list{1, nullptr};
    ~Managers();
};
inline Managers& managers() { static Managers m; return m; }
inline void managersFirst() { managers(); }

inline Mgr* mgrOf(Value id) {
    int64_t i = (int32_t)id.i;
    Managers& all = managers();
    if (i > 0 && (size_t)i < all.list.size() && all.list[(size_t)i]) return all.list[(size_t)i];
    fatal(("No window manager with ID " + std::to_string(i) + ".").c_str());
}

/// Does a manager's thread own the OS window (it opens and pumps it)?
inline bool threaded(WindowState* w) { return w->mgr && w->mgr->ownsWindows(); }

/// The events that a blocked window still gets: a resize and the end of the program.
inline void dropBlocked(WindowState* w, std::vector<plat::disp::Event>& events) {
    if (!w->blocked) {
        // AutoClose: the request closes the window (its handlers still hear of it)
        if (w->autoClose)
            for (const plat::disp::Event& e : events) if (e.type == plat::disp::EV_CLOSE_REQUEST) w->closeRequested = true;
        return;
    }
    events.erase(std::remove_if(events.begin(), events.end(), [](const plat::disp::Event& e) { return e.type != plat::disp::EV_RESIZE && e.type != plat::disp::EV_CLOSE; }), events.end());
}

/// Opens the OS window on the calling thread (once); false when it cannot be opened (no display).
inline bool ensureOpen(Shared* s, bool vsync) {
    if (s->opened) return !s->failed;
    s->opened = true;
    if (!s->display.open(s->title, s->width, s->height, vsync)) s->failed = true;
    return !s->failed;
}

inline void stopThread(Mgr* m);

inline Windows::~Windows() {
    // the threads of the managers end first: they show windows of this list
    for (Mgr* m : managers().list) if (m) stopThread(m);
    for (WindowState* w : list) delete w;
}

inline Managers::~Managers() {
    for (Mgr* m : list) if (m) { stopThread(m); if (m->service.kind == K_Lambda) release(m->service); delete m; }
}

#ifdef FIRE_WINDOW_THREADS
/// The thread of a cooperative manager (FIRE_WINDOW_COOP): only the beat - the program opens, pumps and shows the windows at its safe points.
inline void beatThread(void* arg) {
    Mgr* m = static_cast<Mgr*>(arg);
    while (!m->stop.load(std::memory_order_relaxed)) {
        m->cycleM.lock();
        m->cycles++;
        m->cycleCv.notifyAll();
        m->cycleM.unlock();
        if (m->pending.exchange(1, std::memory_order_relaxed) == 0) hostRequest();
        plat::sleepMs(16);
    }
}

inline void pumpThread(void* arg) {
    Mgr* m = static_cast<Mgr*>(arg);
    std::vector<plat::disp::Event> events;
    while (!m->stop.load(std::memory_order_relaxed)) {
        int64_t start = plat::nowMs();
        m->m.lock();
        std::vector<Shared*> list = m->shown;
        m->m.unlock();
        for (Shared* s : list) {
            s->m.lock();
            if (s->release) {
                s->m.unlock();
                m->m.lock();
                m->shown.erase(std::remove(m->shown.begin(), m->shown.end(), s), m->shown.end());
                m->m.unlock();
                delete s;   // closes the OS window here, on its thread
                continue;
            }
            if (s->closed) { s->m.unlock(); continue; }
            if (!ensureOpen(s, s->vsyncReq != 0)) { s->closed = true; s->m.unlock(); continue; }
            if (s->vsyncReq >= 0) { s->display.setVSync(s->vsyncReq != 0); s->vsyncReq = -1; }
            if (s->touchReq >= 0) { s->display.setTouchMouse(s->touchReq != 0); s->touchReq = -1; }
            if (s->sizeReqW > 0) { s->display.setSize(s->sizeReqW, s->sizeReqH); s->sizeReqW = s->sizeReqH = 0; }
            if (s->cursorReq >= 0) { s->display.setCursor(s->cursorReq); s->cursorReq = -1; }
            events.clear();
            int w = s->frontW > 0 ? s->frontW : s->width, h = s->frontH > 0 ? s->frontH : s->height;
            bool open = s->display.pump(events, w, h);
            for (const plat::disp::Event& e : events) if (s->inbox.size() < MaxQueuedEvents) s->inbox.push_back(e);
            if (!open) s->closed = true;
            if (s->frontNew) { s->shown.swap(s->front); s->shownW = s->frontW; s->shownH = s->frontH; s->frontNew = false; }
            s->m.unlock();
            // (the program publishes into `front` meanwhile; what is shown is the thread's own copy)
            if (!s->shown.empty()) s->display.present(s->shown.data(), s->shownW, s->shownH);
        }
        m->cycleM.lock();
        m->cycles++;
        m->cycleCv.notifyAll();
        m->cycleM.unlock();
        if (m->pending.exchange(1, std::memory_order_relaxed) == 0) hostRequest();
        // about 60 cycles a second: with VSync the presenting has already waited for the screen, otherwise the rest of the period
        int64_t rest = 16 - (plat::nowMs() - start);
        if (rest > 0) plat::sleepMs(rest);
    }
    m->m.lock();
    std::vector<Shared*> left;
    left.swap(m->shown);
    m->m.unlock();
    for (Shared* s : left) {
        s->m.lock();
        bool gone = s->release;
        if (!gone) s->display.close();   // (the program frees it with its window)
        s->m.unlock();
        if (gone) delete s;
    }
    plat::disp::threadDone();
}

/// The main program at a safe point: the cycles that the threads of the managers asked for (each runs the manager's Tick).
inline void serveManagers() {
    static bool busy = false;
    if (busy) { g_hostPending.store(1, std::memory_order_relaxed); return; }   // a cycle is running: the next safe point after it
    busy = true;
    for (Mgr* m : managers().list)
        if (m && m->started() && m->pending.load(std::memory_order_relaxed) && m->service.kind == K_Lambda) runCallback(m->service, 0, nullptr);
    busy = false;
}
#endif

inline void stopThread(Mgr* m) {
#ifdef FIRE_WINDOW_THREADS
    if (!m->thread) return;
    m->stop.store(true);
    plat::threadJoin(m->thread);
    m->thread = nullptr;
#else
    (void)m;
#endif
}

inline Value WinMgrCreate() {
    Managers& all = managers();
    all.list.push_back(new Mgr());
    return Int((int64_t)all.list.size() - 1);
}

inline void detachFrom(WindowState* w) {
    Mgr* m = w->mgr;
    if (!m) return;
    m->attached.erase(std::remove(m->attached.begin(), m->attached.end(), w), m->attached.end());
    if (!m->ownsWindows()) {
#ifdef FIRE_WINDOW_THREADS
        if (m->started()) hostHold(-1);
#endif
    } else {
        // the OS window belongs to the manager's thread: it closes it there; the window starts afresh (unopened) on whatever cycles it next
        Shared* fresh = new Shared();
        fresh->title = w->sh->title;
        fresh->width = w->fb->width; fresh->height = w->fb->height;
        w->sh->m.lock();
        w->sh->release = true;
        w->sh->m.unlock();
        w->sh = fresh;
#ifdef FIRE_WINDOW_THREADS
        hostHold(-1);
#endif
    }
    w->mgr = nullptr;
}

inline Value WinMgrAttach(Value mgrId, Value winId) {
    Mgr* m = mgrOf(mgrId);
    WindowState* w = windowOf(winId);
    if (w->mgr == m) return Bool(true);
    detachFrom(w);
    w->mgr = m;
    m->attached.push_back(w);
#ifdef FIRE_WINDOW_THREADS
    if (m->started() && !m->ownsWindows()) hostHold(1);
#endif
    if (m->ownsWindows()) {
        m->m.lock();
        m->shown.push_back(w->sh);
        m->m.unlock();
#ifdef FIRE_WINDOW_THREADS
        hostHold(1);
#endif
    }
    return Bool(true);
}

inline Value WinMgrDetach(Value mgrId, Value winId) {
    Mgr* m = mgrOf(mgrId);
    WindowState* w = windowOf(winId);
    if (w->mgr != m) return Bool(false);
    detachFrom(w);
    return Bool(true);
}

/// Starts the manager's thread; `service` is the fire lambda the program runs for each cycle. false where there is no thread for windows.
inline Value WinMgrStart(Value mgrId, Value service) {
    Mgr* m = mgrOf(mgrId);
#ifdef FIRE_WINDOW_THREADS
    if (m->started()) return Bool(false);
    if (service.kind != K_Lambda) return Bool(false);
    static bool registered = false;
    if (!registered) { registered = true; g_hostServices.push_back(serveManagers); }
    retain(service);
    m->service = service;
#ifdef FIRE_WINDOW_COOP
    hostHold((int32_t)m->attached.size());
    m->thread = plat::threadStart(beatThread, m, 0);
    return Bool(true);
#endif
    m->m.lock();
    for (WindowState* w : m->attached) {
        // a window that this thread opened already cannot move to the manager's thread: it starts afresh there
        if (w->sh->opened) {
            Shared* fresh = new Shared();
            fresh->title = w->sh->title;
            fresh->width = w->fb->width; fresh->height = w->fb->height;
            delete w->sh;
            w->sh = fresh;
        }
        w->sh->vsyncReq = w->vsync ? 1 : 0;
        w->sh->touchReq = w->touchMouse ? 1 : 0;
        m->shown.push_back(w->sh);
    }
    m->m.unlock();
    hostHold((int32_t)m->attached.size());
    m->thread = plat::threadStart(pumpThread, m, 0);
    return Bool(true);
#else
    (void)m; (void)service;
    return Bool(false);
#endif
}

/// Waits (on the program's thread, without the GIL) up to `ms` for the next cycle of the manager's thread.
inline Value WinMgrWait(Value mgrId, Value ms) {
    Mgr* m = mgrOf(mgrId);
    int64_t wait = ms.i > 0 ? ms.i : 1;
#ifdef FIRE_WINDOW_THREADS
    if (m->started()) {
        gilRelease();
        m->cycleM.lock();
        uint64_t seen = m->cycles;
        while (m->cycles == seen) if (!m->cycleCv.waitFor(m->cycleM, wait)) break;
        m->cycleM.unlock();
        gilAcquire();
        return Undef();
    }
#endif
    (void)m;
    plat::sleepMs(wait);
    return Undef();
}

inline Value WinCreate(Value fbId, Value title) {
    gfx::Framebuffer* fb = gfx::fbOf(fbId);
    WindowState* w = new WindowState();
    w->sh = new Shared();
    w->sh->title = toUtf8(title);
    w->sh->width = fb->width; w->sh->height = fb->height;
    w->fb = fb;
    gfx::fbRetain(fb);
    Windows& all = windows();
    all.list.push_back(w);
    return Int((int64_t)all.list.size() - 1);
}
inline Value WinDestroy(Value id) {
    int64_t i = (int32_t)id.i;
    Windows& all = windows();
    if (i > 0 && (size_t)i < all.list.size() && all.list[(size_t)i]) {
        WindowState* w = all.list[(size_t)i];
        if (threaded(w)) {
            // the manager's thread closes the OS window and frees what it shares
            Shared* s = w->sh;
            detachFrom(w);   // (gives w a fresh, unopened one and marks the old one for release)
            (void)s;
        } else if (w->mgr) detachFrom(w);
        delete w;
        all.list[(size_t)i] = nullptr;
        return Bool(true);
    }
    return Bool(false);
}

/// The events of a cycle go to the queue (EnableEvents) and to the callbacks; with AutoResize a resize gives the framebuffer the new size.
inline void deliver(Value id, WindowState* w, std::vector<plat::disp::Event>& events) {
    if (w->autoResize)
        for (const plat::disp::Event& e : events)
            if (e.type == plat::disp::EV_RESIZE && gfx::Framebuffer::validSize(e.width, e.height)) w->fb->resize(e.width, e.height);
    if (w->queueOn) for (const plat::disp::Event& e : events) if (w->queue.size() - w->queueHead < MaxQueuedEvents) w->queue.push_back(e);
    int64_t i = (int32_t)id.i;
    for (const plat::disp::Event& e : events) {
        dispatch(w, e);
        if (!windows().list[(size_t)i]) break;   // a callback destroyed the window
    }
}

/// The first half of a cycle (WindowManager): the events of the window - from its manager's thread, or fetched here when nobody else shows it.
inline Value WinPump(Value id) {
    WindowState* w = windowOf(id);
    std::vector<plat::disp::Event> events;
    bool open;
    if (threaded(w)) {
        w->mgr->pending.store(0, std::memory_order_relaxed);   // the program runs a cycle: the next one may be asked for
        w->sh->m.lock();
        events.swap(w->sh->inbox);
        open = !w->sh->closed;
        w->sh->m.unlock();
    } else {
        if (w->mgr && w->mgr->started()) w->mgr->pending.store(0, std::memory_order_relaxed);   // (a cooperative manager: the same)
        if (!ensureOpen(w->sh, w->vsync)) return Bool(false);
        open = w->sh->display.pump(events, w->fb->width, w->fb->height);
    }
    dropBlocked(w, events);
    deliver(id, w, events);
    return Bool(open && !w->closeRequested);
}

/// The second half of a cycle: shows the framebuffer - here, or (a started manager) hands a copy to the manager's thread.
inline Value WinPresent(Value id) {
    WindowState* w = windowOf(id);
    if (threaded(w)) {
        std::vector<uint32_t> pixels(w->fb->pixelCount());
        w->fb->resolveTo(pixels.data());
        w->sh->m.lock();
        w->sh->front.swap(pixels);
        w->sh->frontW = w->fb->width; w->sh->frontH = w->fb->height;
        w->sh->frontNew = true;
        w->sh->m.unlock();
        return Undef();
    }
    if (!ensureOpen(w->sh, w->vsync)) return Undef();
    std::vector<uint32_t> pixels(w->fb->pixelCount());
    w->fb->resolveTo(pixels.data());
    w->sh->display.present(pixels.data(), w->fb->width, w->fb->height);
    return Undef();
}

/// One cycle of this window alone (Window.Tick): events, frame, then the callbacks.
inline Value WinTick(Value id) {
    WindowState* w = windowOf(id);
    if (threaded(w)) { Value open = WinPump(id); if (windows().list[(size_t)(int32_t)id.i]) WinPresent(id); return open; }
    if (!ensureOpen(w->sh, w->vsync)) return Bool(false);
    std::vector<plat::disp::Event> events;
    bool open = w->sh->display.pump(events, w->fb->width, w->fb->height);
    if (w->autoResize)
        for (const plat::disp::Event& e : events)
            if (e.type == plat::disp::EV_RESIZE && gfx::Framebuffer::validSize(e.width, e.height)) w->fb->resize(e.width, e.height);
    dropBlocked(w, events);
    std::vector<uint32_t> pixels(w->fb->pixelCount());
    w->fb->resolveTo(pixels.data());
    w->sh->display.present(pixels.data(), w->fb->width, w->fb->height);
    if (w->queueOn) for (const plat::disp::Event& e : events) if (w->queue.size() - w->queueHead < MaxQueuedEvents) w->queue.push_back(e);
    int64_t i = (int32_t)id.i;
    for (const plat::disp::Event& e : events) {
        dispatch(w, e);
        if (!windows().list[(size_t)i]) break;   // a callback destroyed the window
    }
    return Bool(open && !w->closeRequested);
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
        case EV_TOUCH_DOWN: case EV_TOUCH_MOVE: case EV_TOUCH_UP: items[n++] = Int(e.finger); items[n++] = px(e.x); items[n++] = px(e.y); items[n++] = Float(e.pressure); break;
        case EV_JOY_AXIS: items[n++] = Int(e.joystick); items[n++] = Int(e.index); items[n++] = Float(e.value); break;
        case EV_JOY_HAT: items[n++] = Int(e.joystick); items[n++] = Int(e.index); items[n++] = Int((int64_t)e.value); break;
        case EV_JOY_BUTTON_DOWN: case EV_JOY_BUTTON_UP: items[n++] = Int(e.joystick); items[n++] = Int(e.index); break;
        case EV_JOY_ADDED: case EV_JOY_REMOVED: items[n++] = Int(e.joystick); break;
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
inline Value WinSetVSync(Value id, Value on) {
    WindowState* w = windowOf(id);
    w->vsync = on.i != 0;
    if (threaded(w)) { w->sh->m.lock(); w->sh->vsyncReq = w->vsync ? 1 : 0; w->sh->m.unlock(); }
    else w->sh->display.setVSync(w->vsync);
    return Undef();
}
inline Value WinGetVSync(Value id) { return Bool(windowOf(id)->vsync); }
inline Value WinSetTouchMouse(Value id, Value on) {
    WindowState* w = windowOf(id);
    w->touchMouse = on.i != 0;
    if (threaded(w)) { w->sh->m.lock(); w->sh->touchReq = w->touchMouse ? 1 : 0; w->sh->m.unlock(); }
    else w->sh->display.setTouchMouse(w->touchMouse);
    return Undef();
}
inline Value WinGetTouchMouse(Value id) { return Bool(windowOf(id)->touchMouse); }
inline Value WinSetAutoResize(Value id, Value on) { windowOf(id)->autoResize = on.i != 0; return Undef(); }
inline Value WinGetAutoResize(Value id) { return Bool(windowOf(id)->autoResize); }
inline Value WinSetAutoClose(Value id, Value on) { windowOf(id)->autoClose = on.i != 0; return Undef(); }
inline Value WinGetAutoClose(Value id) { return Bool(windowOf(id)->autoClose); }
inline Value WinSetBlocked(Value id, Value on) { windowOf(id)->blocked = on.i != 0; return Undef(); }
/// Window.SetSize: the size of the window in pixels of the screen (before it opens: the size it opens with).
inline Value WinSetSize(Value id, Value width, Value height) {
    WindowState* w = windowOf(id);
    int wd = (int)width.i, ht = (int)height.i;
    if (wd <= 0 || ht <= 0) return Bool(false);
    Shared* s = w->sh;
    if (threaded(w)) {
        s->m.lock();
        if (!s->opened) { s->width = wd; s->height = ht; } else { s->sizeReqW = wd; s->sizeReqH = ht; }
        s->m.unlock();
    } else if (!s->opened) { s->width = wd; s->height = ht; }
    else s->display.setSize(wd, ht);
    return Bool(true);
}
/// Window.Cursor: the system cursor over the window (SDL numbering; the thread of a managed window sets it at its next cycle).
inline Value WinSetCursor(Value id, Value cursor) {
    WindowState* w = windowOf(id);
    Shared* s = w->sh;
    int c = (int)cursor.i;
    if (threaded(w)) {
        s->m.lock();
        s->cursorReq = c;
        s->m.unlock();
    } else if (s->opened) s->display.setCursor(c);
    return Undef();
}
/// SystemTheme.Current(): [embedded, scale, dark, accent 0xRRGGBB, font candidates "a;b;c", font size in logical pixels] (see fire_systheme.hpp).
inline Value WinSystemTheme(OwnList* list) {
    plat::disp::SystemTheme t = plat::disp::systemTheme();
    Arr* a = allocArr(6, list);
    Value items[6] = { Bool(t.embedded), Float((Real)t.scale), Bool(t.dark != 0), Int(t.accent), utf8Str(t.fonts.c_str(), list), Int(t.fontSize) };
    for (int k = 0; k < 6; k++) { a->items()[k] = items[k]; retain(items[k]); }
    return ArrV(a);
}
/// The program runs the cycle the manager's thread asked for: the next one may be asked for (also when that cycle does nothing, see RunModal).
inline Value WinMgrServed(Value mgrId) { mgrOf(mgrId)->pending.store(0, std::memory_order_relaxed); return Undef(); }

}  // namespace win
}  // namespace fire
