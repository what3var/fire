// fire native platform package "std": a window on the desktop through SDL2 (link with -lSDL2; the generated program asks for it with a `// fire-link: SDL2` line).
// What it shows is the same as the VM's window (src/fire.Terminal.Sdl): the framebuffer as a streaming texture (R,G,B,A in memory = ABGR8888 on little endian),
// stretched to the window, which can be resized; positions come back in framebuffer pixels; closing the window ends `Tick`.
#pragma once
#if defined(__has_include)
#  if __has_include(<SDL2/SDL.h>)
#    include <SDL2/SDL.h>
#  else
#    include <SDL.h>
#  endif
#else
#  include <SDL.h>
#endif
#include <algorithm>
#include <cstring>
#include "fire_display_event.hpp"

namespace fire { namespace plat { namespace disp {

constexpr bool available = true;

class Window {
public:
    ~Window() { close(); }

    bool open(const std::string& title, int width, int height, bool vsync) {
        vsync_ = vsync;
        winW_ = width; winH_ = height;
        if (SDL_WasInit(SDL_INIT_VIDEO) == 0 && SDL_InitSubSystem(SDL_INIT_VIDEO) != 0) return false;
        ownsVideo_ = true;
        window_ = SDL_CreateWindow(title.c_str(), SDL_WINDOWPOS_UNDEFINED, SDL_WINDOWPOS_UNDEFINED, width, height, SDL_WINDOW_RESIZABLE);
        if (!window_) { close(); return false; }
        renderer_ = SDL_CreateRenderer(window_, -1, vsync ? SDL_RENDERER_PRESENTVSYNC : 0);
        if (!renderer_) renderer_ = SDL_CreateRenderer(window_, -1, SDL_RENDERER_SOFTWARE);
        if (!renderer_) { close(); return false; }
        SDL_StartTextInput();
        windowId_ = SDL_GetWindowID(window_);
        // joysticks are an extra: without a driver the window opens all the same (the devices that are plugged in report themselves as SDL_JOYDEVICEADDED)
        if (SDL_WasInit(SDL_INIT_JOYSTICK) == 0 && SDL_InitSubSystem(SDL_INIT_JOYSTICK) == 0) ownsJoystick_ = true;
        SDL_SetHint(SDL_HINT_TOUCH_MOUSE_EVENTS, touchMouse_ ? "1" : "0");
#if defined(FIRE_DISPLAY_SELFTEST) || defined(FIRE_DISPLAY_SELFTEST_RESIZE) || defined(FIRE_DISPLAY_SELFTEST_INPUT)
        selftest();
#endif
        return true;
    }

    void setVSync(bool v) { vsync_ = v; if (renderer_) SDL_RenderSetVSync(renderer_, v ? 1 : 0); }
    bool vsync() const { return vsync_; }
    /// true (the default): a finger also makes mouse events (as SDL does itself); false: only the touch events.
    void setTouchMouse(bool v) { touchMouse_ = v; SDL_SetHint(SDL_HINT_TOUCH_MOUSE_EVENTS, v ? "1" : "0"); }
    bool touchMouse() const { return touchMouse_; }

    /// Takes the pending events of this window. False once the window was closed.
    bool pump(std::vector<Event>& out, int fbWidth, int fbHeight) {
        fbWidth_ = fbWidth; fbHeight_ = fbHeight;
        SDL_Event ev;
        while (SDL_PollEvent(&ev)) {
            Event e;
            switch (ev.type) {
                case SDL_QUIT: e.type = EV_CLOSE; out.push_back(e); quit_ = true; break;
                case SDL_WINDOWEVENT:
                    if (ev.window.event == SDL_WINDOWEVENT_CLOSE) { e.type = EV_CLOSE_REQUEST; out.push_back(e); }
                    else if (ev.window.event == SDL_WINDOWEVENT_SIZE_CHANGED && ev.window.windowID == windowId_ && (ev.window.data1 != winW_ || ev.window.data2 != winH_)) {
                        winW_ = ev.window.data1; winH_ = ev.window.data2;
                        e.type = EV_RESIZE; e.width = winW_; e.height = winH_; out.push_back(e); }
                    break;
                case SDL_KEYDOWN: case SDL_KEYUP:
                    e.type = ev.type == SDL_KEYDOWN ? EV_KEY_DOWN : EV_KEY_UP;
                    e.keyCode = (int)ev.key.keysym.sym; e.scanCode = (int)ev.key.keysym.scancode; e.modifier = (int)ev.key.keysym.mod; e.repeat = ev.key.repeat != 0;
                    out.push_back(e);
                    break;
                case SDL_MOUSEBUTTONDOWN: case SDL_MOUSEBUTTONUP:
                    e.type = ev.type == SDL_MOUSEBUTTONDOWN ? EV_MOUSE_DOWN : EV_MOUSE_UP;
                    e.button = ev.button.button;
                    toFramebuffer((float)ev.button.x, (float)ev.button.y, e.x, e.y);
                    out.push_back(e);
                    break;
                case SDL_MOUSEMOTION: {
                    e.type = EV_MOUSE_MOVE; e.buttonState = (int)ev.motion.state;
                    toFramebuffer((float)ev.motion.x, (float)ev.motion.y, e.x, e.y);
                    out.push_back(e);
                    Event r;
                    r.type = EV_MOUSE_MOVE_REL; r.buttonState = (int)ev.motion.state;
                    float ox, oy, mx, my;
                    toFramebuffer(0, 0, ox, oy); toFramebuffer((float)ev.motion.xrel, (float)ev.motion.yrel, mx, my);
                    r.xrel = mx - ox; r.yrel = my - oy;
                    out.push_back(r);
                    break;
                }
                case SDL_MOUSEWHEEL: {
                    e.type = EV_MOUSE_SCROLL; e.scrollX = ev.wheel.preciseX; e.scrollY = ev.wheel.preciseY;
                    int mx = 0, my = 0;
                    SDL_GetMouseState(&mx, &my);
                    toFramebuffer((float)mx, (float)my, e.x, e.y);
                    out.push_back(e);
                    break;
                }
                case SDL_FINGERDOWN: case SDL_FINGERMOTION: case SDL_FINGERUP:
                    // SDL reports the position normalized (0 to 1 over the window): in framebuffer pixels it is simply x * width
                    e.type = ev.type == SDL_FINGERDOWN ? EV_TOUCH_DOWN : ev.type == SDL_FINGERUP ? EV_TOUCH_UP : EV_TOUCH_MOVE;
                    e.finger = (long long)ev.tfinger.fingerId; e.x = ev.tfinger.x * (float)fbWidth_; e.y = ev.tfinger.y * (float)fbHeight_; e.pressure = ev.tfinger.pressure;
                    out.push_back(e);
                    break;
                case SDL_JOYDEVICEADDED: {
                    SDL_Joystick* js = SDL_JoystickOpen(ev.jdevice.which);
                    e.type = EV_JOY_ADDED;
                    if (js) { e.joystick = (int)SDL_JoystickInstanceID(js); joysticks_.push_back(js); } else e.joystick = ev.jdevice.which;
                    out.push_back(e);
                    break;
                }
                case SDL_JOYDEVICEREMOVED:
                    for (size_t k = 0; k < joysticks_.size(); k++)
                        if ((int)SDL_JoystickInstanceID(joysticks_[k]) == (int)ev.jdevice.which) { SDL_JoystickClose(joysticks_[k]); joysticks_.erase(joysticks_.begin() + (long)k); break; }
                    e.type = EV_JOY_REMOVED; e.joystick = (int)ev.jdevice.which;
                    out.push_back(e);
                    break;
                case SDL_JOYAXISMOTION:
                    e.type = EV_JOY_AXIS; e.joystick = (int)ev.jaxis.which; e.index = ev.jaxis.axis;
                    e.value = std::max(-1.0f, (float)ev.jaxis.value / 32767.0f);   // 16 bit, -32768 to 32767 -> -1 to 1
                    out.push_back(e);
                    break;
                case SDL_JOYBUTTONDOWN: case SDL_JOYBUTTONUP:
                    e.type = ev.type == SDL_JOYBUTTONDOWN ? EV_JOY_BUTTON_DOWN : EV_JOY_BUTTON_UP; e.joystick = (int)ev.jbutton.which; e.index = ev.jbutton.button;
                    out.push_back(e);
                    break;
                case SDL_JOYHATMOTION:
                    e.type = EV_JOY_HAT; e.joystick = (int)ev.jhat.which; e.index = ev.jhat.hat; e.value = (float)ev.jhat.value;
                    out.push_back(e);
                    break;
                case SDL_TEXTINPUT: e.type = EV_TEXT_INPUT; e.text = ev.text.text; out.push_back(e); break;
                default: break;
            }
        }
        return !quit_;
    }

    /// Shows the pixels (R,G,B,A per 32-bit word, width*height), stretched to the window.
    void present(const uint32_t* rgba, int width, int height) {
        if (!renderer_) return;
        if (!texture_ || texW_ != width || texH_ != height) {
            if (texture_) SDL_DestroyTexture(texture_);
            texture_ = SDL_CreateTexture(renderer_, SDL_PIXELFORMAT_ABGR8888, SDL_TEXTUREACCESS_STREAMING, width, height);
            texW_ = width; texH_ = height;
        }
        if (!texture_) return;
        SDL_UpdateTexture(texture_, nullptr, rgba, width * (int)sizeof(uint32_t));
        SDL_RenderClear(renderer_);
        SDL_RenderCopy(renderer_, texture_, nullptr, nullptr);
        SDL_RenderPresent(renderer_);
    }

    void close() {
        if (texture_) { SDL_DestroyTexture(texture_); texture_ = nullptr; }
        for (SDL_Joystick* js : joysticks_) SDL_JoystickClose(js);
        joysticks_.clear();
        if (ownsJoystick_) { SDL_QuitSubSystem(SDL_INIT_JOYSTICK); ownsJoystick_ = false; }
        if (renderer_) { SDL_DestroyRenderer(renderer_); renderer_ = nullptr; }
        if (window_) { SDL_DestroyWindow(window_); window_ = nullptr; }
        if (ownsVideo_) { SDL_QuitSubSystem(SDL_INIT_VIDEO); ownsVideo_ = false; }
    }

private:
#if defined(FIRE_DISPLAY_SELFTEST) || defined(FIRE_DISPLAY_SELFTEST_RESIZE) || defined(FIRE_DISPLAY_SELFTEST_INPUT)
    /// The tests (and nobody else) push a fixed series of events when a window opens: keys, a click, motion, the wheel, text and the close.
    void selftest() {
        SDL_Event e;
#ifdef FIRE_DISPLAY_SELFTEST_RESIZE
        // (only for the test of Window.AutoResize: the window is pulled to 90x70)
        SDL_zero(e); e.type = SDL_WINDOWEVENT; e.window.event = SDL_WINDOWEVENT_SIZE_CHANGED; e.window.windowID = windowId_; e.window.data1 = 90; e.window.data2 = 70; SDL_PushEvent(&e);
        return;
#endif
#ifdef FIRE_DISPLAY_SELFTEST_INPUT
        // (only for the test of touch and joystick: a finger touches, moves and lifts; a joystick moves an axis, presses a button, moves the hat, is plugged in and removed)
        SDL_zero(e); e.type = SDL_FINGERDOWN; e.tfinger.fingerId = 5; e.tfinger.x = 0.5f; e.tfinger.y = 0.25f; e.tfinger.pressure = 1.0f; SDL_PushEvent(&e);
        SDL_zero(e); e.type = SDL_FINGERMOTION; e.tfinger.fingerId = 5; e.tfinger.x = 0.75f; e.tfinger.y = 0.5f; e.tfinger.pressure = 0.5f; SDL_PushEvent(&e);
        SDL_zero(e); e.type = SDL_FINGERUP; e.tfinger.fingerId = 5; e.tfinger.x = 0.75f; e.tfinger.y = 0.5f; e.tfinger.pressure = 0.0f; SDL_PushEvent(&e);
        SDL_zero(e); e.type = SDL_JOYAXISMOTION; e.jaxis.which = 7; e.jaxis.axis = 1; e.jaxis.value = -32768; SDL_PushEvent(&e);
        SDL_zero(e); e.type = SDL_JOYBUTTONDOWN; e.jbutton.which = 7; e.jbutton.button = 3; e.jbutton.state = SDL_PRESSED; SDL_PushEvent(&e);
        SDL_zero(e); e.type = SDL_JOYBUTTONUP; e.jbutton.which = 7; e.jbutton.button = 3; e.jbutton.state = SDL_RELEASED; SDL_PushEvent(&e);
        SDL_zero(e); e.type = SDL_JOYHATMOTION; e.jhat.which = 7; e.jhat.hat = 0; e.jhat.value = SDL_HAT_UP | SDL_HAT_RIGHT; SDL_PushEvent(&e);
        SDL_zero(e); e.type = SDL_JOYDEVICEREMOVED; e.jdevice.which = 7; SDL_PushEvent(&e);
        return;
#endif
        SDL_zero(e); e.type = SDL_KEYDOWN; e.key.keysym.sym = 'a'; e.key.keysym.scancode = SDL_SCANCODE_A; e.key.keysym.mod = KMOD_LSHIFT; e.key.repeat = 0; SDL_PushEvent(&e);
        SDL_zero(e); e.type = SDL_KEYUP; e.key.keysym.sym = 'a'; e.key.keysym.scancode = SDL_SCANCODE_A; e.key.keysym.mod = 0; SDL_PushEvent(&e);
        SDL_zero(e); e.type = SDL_MOUSEBUTTONDOWN; e.button.button = SDL_BUTTON_LEFT; e.button.x = 32; e.button.y = 24; SDL_PushEvent(&e);
        SDL_zero(e); e.type = SDL_MOUSEBUTTONUP; e.button.button = SDL_BUTTON_LEFT; e.button.x = 32; e.button.y = 24; SDL_PushEvent(&e);
        SDL_zero(e); e.type = SDL_MOUSEMOTION; e.motion.x = 10; e.motion.y = 12; e.motion.xrel = 3; e.motion.yrel = -2; e.motion.state = 1; SDL_PushEvent(&e);
        SDL_zero(e); e.type = SDL_MOUSEWHEEL; e.wheel.preciseX = 0; e.wheel.preciseY = 1.5f; SDL_PushEvent(&e);
        SDL_zero(e); e.type = SDL_TEXTINPUT; std::strncpy(e.text.text, "\xC3\xA9x", sizeof e.text.text - 1); SDL_PushEvent(&e);
        SDL_zero(e); e.type = SDL_WINDOWEVENT; e.window.event = SDL_WINDOWEVENT_CLOSE; SDL_PushEvent(&e);
        SDL_zero(e); e.type = SDL_QUIT; SDL_PushEvent(&e);
    }
#endif
    void toFramebuffer(float x, float y, float& ox, float& oy) const {
        int w = 0, h = 0;
        if (window_) SDL_GetWindowSize(window_, &w, &h);
        if (w <= 0 || h <= 0 || fbWidth_ <= 0) { ox = x; oy = y; return; }
        ox = x * (float)fbWidth_ / (float)w; oy = y * (float)fbHeight_ / (float)h;
    }

    SDL_Window* window_ = nullptr;
    SDL_Renderer* renderer_ = nullptr;
    SDL_Texture* texture_ = nullptr;
    int winW_ = 0, winH_ = 0, texW_ = 0, texH_ = 0, fbWidth_ = 0, fbHeight_ = 0;
    Uint32 windowId_ = 0;
    bool vsync_ = true, quit_ = false, ownsVideo_ = false, touchMouse_ = true, ownsJoystick_ = false;
    std::vector<SDL_Joystick*> joysticks_;
};

}}}
