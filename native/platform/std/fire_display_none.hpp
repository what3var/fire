// fire native platform package "std": no display (a board without a screen, or one whose package you write yourself). A window cannot be created - `new Window(...)` throws a
// HandleUnavailableException. A board package of your own provides fire::plat::disp::Window (see fire_display_sdl.hpp for the members) and puts the pixels on its screen.
#pragma once
#include "fire_display_event.hpp"

namespace fire { namespace plat { namespace disp {

constexpr bool available = false;

/// A board: no desktop to ask - the UI uses its embedded look (the fast built-in font, whole-number scaling, dark violet).
struct SystemTheme {
    bool embedded = true;
    double scale = 1.0;
    int dark = 1;
    int accent = 0x8854D0;
    std::string fonts;
    int fontSize = 0;
};
inline SystemTheme systemTheme() { return SystemTheme(); }

inline void threadDone() {}

class Window {
public:
    bool open(const std::string&, int, int, bool) { return false; }
    void setVSync(bool v) { vsync_ = v; }
    void setSize(int, int) {}
    void setCursor(int) {}
    bool vsync() const { return vsync_; }
    void setTouchMouse(bool v) { touchMouse_ = v; }
    bool touchMouse() const { return touchMouse_; }
    bool pump(std::vector<Event>&, int, int) { return false; }
    void present(const uint32_t*, int, int) {}
private:
    bool vsync_ = true, touchMouse_ = true;
};

}}}
