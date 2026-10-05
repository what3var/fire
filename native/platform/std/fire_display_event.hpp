// fire native platform package "std": the events a display delivers (the shape plat::disp::Window::pump fills). The numbers are those of `EventType` in the windows prelude.
#pragma once
#include <string>
#include <vector>

namespace fire { namespace plat { namespace disp {

enum EventKind { EV_UNKNOWN = 0, EV_CLOSE = 1, EV_CLOSE_REQUEST = 2, EV_TEXT_INPUT = 3, EV_MOUSE_DOWN = 8, EV_MOUSE_MOVE = 9, EV_MOUSE_MOVE_REL = 10, EV_MOUSE_UP = 11, EV_MOUSE_SCROLL = 12, EV_KEY_DOWN = 24, EV_KEY_UP = 25 };

/// One event; which fields count depends on `type` (positions are in framebuffer pixels: the window may be scaled).
struct Event {
    int type = EV_UNKNOWN;
    int keyCode = 0, scanCode = 0, modifier = 0;   // keys
    bool repeat = false;
    int button = 0;                                // mouse buttons
    int buttonState = 0;                           // mouse motion: the buttons that are down
    float x = 0, y = 0, xrel = 0, yrel = 0;        // mouse position / movement
    float scrollX = 0, scrollY = 0;
    std::string text;                              // text input (UTF-8)
};

}}}
