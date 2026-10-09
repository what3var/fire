// fire native platform package "std": the events a display delivers (the shape plat::disp::Window::pump fills). The numbers are those of `EventType` in the windows prelude.
#pragma once
#include <string>
#include <vector>

namespace fire { namespace plat { namespace disp {

enum EventKind { EV_UNKNOWN = 0, EV_CLOSE = 1, EV_CLOSE_REQUEST = 2, EV_TEXT_INPUT = 3, EV_RESIZE = 4, EV_TOUCH_DOWN = 16, EV_TOUCH_MOVE = 17, EV_TOUCH_UP = 18, EV_MOUSE_DOWN = 8, EV_MOUSE_MOVE = 9, EV_MOUSE_MOVE_REL = 10, EV_MOUSE_UP = 11, EV_MOUSE_SCROLL = 12, EV_KEY_DOWN = 24, EV_KEY_UP = 25,
                 EV_JOY_AXIS = 32, EV_JOY_BUTTON_DOWN = 33, EV_JOY_BUTTON_UP = 34, EV_JOY_HAT = 35, EV_JOY_ADDED = 36, EV_JOY_REMOVED = 37 };

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
    int width = 0, height = 0;                     // resize: the new size of the window
    long long finger = 0;                          // touch: which finger; x, y are in framebuffer pixels
    float pressure = 0;                            // touch: 0 to 1
    int joystick = 0, index = 0;                   // joystick: the device, and the axis / button / hat
    float value = 0;                               // joystick axis: -1 to 1; hat: a bit mask (1 up, 2 right, 4 down, 8 left)
};

}}}
