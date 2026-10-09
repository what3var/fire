// fire native platform package "std": no display (a board without a screen, or one whose package you write yourself). A window cannot be created - `new Window(...)` throws a
// HandleUnavailableException. A board package of your own provides fire::plat::disp::Window (see fire_display_sdl.hpp for the members) and puts the pixels on its screen.
#pragma once
#include "fire_display_event.hpp"

namespace fire { namespace plat { namespace disp {

constexpr bool available = false;

class Window {
public:
    bool open(const std::string&, int, int, bool) { return false; }
    void setVSync(bool v) { vsync_ = v; }
    bool vsync() const { return vsync_; }
    void setTouchMouse(bool v) { touchMouse_ = v; }
    bool touchMouse() const { return touchMouse_; }
    bool pump(std::vector<Event>&, int, int) { return false; }
    void present(const uint32_t*, int, int) {}
private:
    bool vsync_ = true, touchMouse_ = true;
};

}}}
