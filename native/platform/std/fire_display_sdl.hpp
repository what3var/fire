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
#include <cstring>
#include "fire_display_event.hpp"

namespace fire { namespace plat { namespace disp {

constexpr bool available = true;

class Window {
public:
    ~Window() { close(); }

    bool open(const std::string& title, int width, int height, bool vsync) {
        vsync_ = vsync;
        if (SDL_WasInit(SDL_INIT_VIDEO) == 0 && SDL_InitSubSystem(SDL_INIT_VIDEO) != 0) return false;
        ownsVideo_ = true;
        window_ = SDL_CreateWindow(title.c_str(), SDL_WINDOWPOS_UNDEFINED, SDL_WINDOWPOS_UNDEFINED, width, height, SDL_WINDOW_RESIZABLE);
        if (!window_) { close(); return false; }
        renderer_ = SDL_CreateRenderer(window_, -1, vsync ? SDL_RENDERER_PRESENTVSYNC : 0);
        if (!renderer_) renderer_ = SDL_CreateRenderer(window_, -1, SDL_RENDERER_SOFTWARE);
        if (!renderer_) { close(); return false; }
        SDL_StartTextInput();
        windowId_ = SDL_GetWindowID(window_);
#ifdef FIRE_DISPLAY_SELFTEST
        selftest();
#endif
        return true;
    }

    void setVSync(bool v) { vsync_ = v; if (renderer_) SDL_RenderSetVSync(renderer_, v ? 1 : 0); }
    bool vsync() const { return vsync_; }

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
        if (renderer_) { SDL_DestroyRenderer(renderer_); renderer_ = nullptr; }
        if (window_) { SDL_DestroyWindow(window_); window_ = nullptr; }
        if (ownsVideo_) { SDL_QuitSubSystem(SDL_INIT_VIDEO); ownsVideo_ = false; }
    }

private:
#ifdef FIRE_DISPLAY_SELFTEST
    /// The tests (and nobody else) push a fixed series of events when a window opens: keys, a click, motion, the wheel, text and the close.
    void selftest() {
        SDL_Event e;
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
    int texW_ = 0, texH_ = 0, fbWidth_ = 0, fbHeight_ = 0;
    Uint32 windowId_ = 0;
    bool vsync_ = true, quit_ = false, ownsVideo_ = false;
};

}}}
