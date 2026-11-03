/* fire native platform package "std": the numbers of keys, modifiers, mouse buttons and cursors that a display reports (part of the HAL, fire_display_c.h).
 *
 * The numbers are those programs already know (UI.Keys, the `windows` prelude): they were SDL3's, and they stay - every display of fire translates what its window
 * system says into them, so that a program sees the same key on every platform:
 *   scan code  the position of the key on the keyboard, as the USB HID usage table numbers it (A = 4, Enter = 40, Right = 79, Left Ctrl = 224, ...);
 *   key code   what the key means on the keyboard layout of the user: the character it types without shift (lower case: 'a', '1', '-'; 13 Enter, 27 Escape, 8 Backspace,
 *              9 Tab, 32 Space, 127 Delete), and for keys without a character the scan code with bit 30 set (FIRE_KEY_FROM_SCAN: Right = 0x4000004F);
 *   modifiers  bits: 0x1/0x2 left/right Shift, 0x40/0x80 Ctrl, 0x100/0x200 Alt, 0x400/0x800 Cmd/Windows key, 0x1000 Num Lock, 0x2000 Caps Lock, 0x4000 AltGr, 0x8000 Scroll Lock;
 *   buttons    1 left, 2 middle, 3 right, 4 and 5 the side buttons; the state of the buttons is a mask of bit (button - 1);
 *   cursors    0 arrow, 1 text, 2 wait, 3 crosshair, 4 progress, 5 NW-SE, 6 NE-SW, 7 W-E, 8 N-S, 9 move, 10 not allowed, 11 hand.
 *
 * The tables translate what the window systems give: Linux (evdev key codes: X11 key code - 8, Wayland's key), Windows (scan codes of set 1 with the extended bit) and
 * macOS (virtual key codes of the keyboard, kVK_...). The Web display translates KeyboardEvent.code in its script. */
#ifndef FIRE_DISPLAY_KEYS_H
#define FIRE_DISPLAY_KEYS_H

#include <stdint.h>

enum {
    FIRE_SC_UNKNOWN = 0, FIRE_SC_A = 4, FIRE_SC_Z = 29, FIRE_SC_1 = 30, FIRE_SC_0 = 39, FIRE_SC_RETURN = 40, FIRE_SC_ESCAPE = 41, FIRE_SC_BACKSPACE = 42, FIRE_SC_TAB = 43,
    FIRE_SC_SPACE = 44, FIRE_SC_MINUS = 45, FIRE_SC_EQUALS = 46, FIRE_SC_LEFTBRACKET = 47, FIRE_SC_RIGHTBRACKET = 48, FIRE_SC_BACKSLASH = 49, FIRE_SC_NONUSHASH = 50,
    FIRE_SC_SEMICOLON = 51, FIRE_SC_APOSTROPHE = 52, FIRE_SC_GRAVE = 53, FIRE_SC_COMMA = 54, FIRE_SC_PERIOD = 55, FIRE_SC_SLASH = 56, FIRE_SC_CAPSLOCK = 57,
    FIRE_SC_F1 = 58, FIRE_SC_F12 = 69, FIRE_SC_PRINTSCREEN = 70, FIRE_SC_SCROLLLOCK = 71, FIRE_SC_PAUSE = 72, FIRE_SC_INSERT = 73, FIRE_SC_HOME = 74, FIRE_SC_PAGEUP = 75,
    FIRE_SC_DELETE = 76, FIRE_SC_END = 77, FIRE_SC_PAGEDOWN = 78, FIRE_SC_RIGHT = 79, FIRE_SC_LEFT = 80, FIRE_SC_DOWN = 81, FIRE_SC_UP = 82, FIRE_SC_NUMLOCK = 83,
    FIRE_SC_KP_DIVIDE = 84, FIRE_SC_KP_MULTIPLY = 85, FIRE_SC_KP_MINUS = 86, FIRE_SC_KP_PLUS = 87, FIRE_SC_KP_ENTER = 88, FIRE_SC_KP_1 = 89, FIRE_SC_KP_9 = 97, FIRE_SC_KP_0 = 98,
    FIRE_SC_KP_PERIOD = 99, FIRE_SC_NONUSBACKSLASH = 100, FIRE_SC_APPLICATION = 101, FIRE_SC_POWER = 102, FIRE_SC_KP_EQUALS = 103, FIRE_SC_F13 = 104, FIRE_SC_F24 = 115,
    FIRE_SC_HELP = 117, FIRE_SC_MENU = 118, FIRE_SC_MUTE = 127, FIRE_SC_VOLUMEUP = 128, FIRE_SC_VOLUMEDOWN = 129, FIRE_SC_KP_COMMA = 133, FIRE_SC_INTERNATIONAL1 = 135,
    FIRE_SC_INTERNATIONAL3 = 137, FIRE_SC_LANG1 = 144, FIRE_SC_LANG2 = 145, FIRE_SC_KP_CLEAR = 156,
    FIRE_SC_LCTRL = 224, FIRE_SC_LSHIFT = 225, FIRE_SC_LALT = 226, FIRE_SC_LGUI = 227, FIRE_SC_RCTRL = 228, FIRE_SC_RSHIFT = 229, FIRE_SC_RALT = 230, FIRE_SC_RGUI = 231
};

enum {
    FIRE_MOD_LSHIFT = 0x0001, FIRE_MOD_RSHIFT = 0x0002, FIRE_MOD_LCTRL = 0x0040, FIRE_MOD_RCTRL = 0x0080, FIRE_MOD_LALT = 0x0100, FIRE_MOD_RALT = 0x0200,
    FIRE_MOD_LGUI = 0x0400, FIRE_MOD_RGUI = 0x0800, FIRE_MOD_NUM = 0x1000, FIRE_MOD_CAPS = 0x2000, FIRE_MOD_MODE = 0x4000, FIRE_MOD_SCROLL = 0x8000
};

enum { FIRE_BUTTON_LEFT = 1, FIRE_BUTTON_MIDDLE = 2, FIRE_BUTTON_RIGHT = 3, FIRE_BUTTON_X1 = 4, FIRE_BUTTON_X2 = 5 };

enum {
    FIRE_CURSOR_ARROW = 0, FIRE_CURSOR_TEXT = 1, FIRE_CURSOR_WAIT = 2, FIRE_CURSOR_CROSSHAIR = 3, FIRE_CURSOR_PROGRESS = 4, FIRE_CURSOR_NWSE = 5, FIRE_CURSOR_NESW = 6,
    FIRE_CURSOR_EW = 7, FIRE_CURSOR_NS = 8, FIRE_CURSOR_MOVE = 9, FIRE_CURSOR_NOT_ALLOWED = 10, FIRE_CURSOR_HAND = 11, FIRE_CURSOR_COUNT = 12
};

#define FIRE_KEY_FROM_SCAN(sc) ((int)(0x40000000u | (unsigned)(sc)))

/* The key code of a key that types no character (or whose character is not known): the scan code with bit 30; the few keys with a control character keep it. */
static int fire_key_fromScan(int sc) {
    switch (sc) {
        case FIRE_SC_RETURN: return 13;
        case FIRE_SC_ESCAPE: return 27;
        case FIRE_SC_BACKSPACE: return 8;
        case FIRE_SC_TAB: return 9;
        case FIRE_SC_SPACE: return 32;
        case FIRE_SC_DELETE: return 127;
        case FIRE_SC_UNKNOWN: return 0;
        default: break;
    }
    /* the keys of the main block with a character on a US layout (what a display without a layout of its own reports) */
    if (sc >= FIRE_SC_A && sc <= FIRE_SC_Z) return 'a' + (sc - FIRE_SC_A);
    if (sc >= FIRE_SC_1 && sc < FIRE_SC_0) return '1' + (sc - FIRE_SC_1);
    if (sc == FIRE_SC_0) return '0';
    switch (sc) {
        case FIRE_SC_MINUS: return '-'; case FIRE_SC_EQUALS: return '='; case FIRE_SC_LEFTBRACKET: return '['; case FIRE_SC_RIGHTBRACKET: return ']';
        case FIRE_SC_BACKSLASH: return '\\'; case FIRE_SC_NONUSHASH: return '#'; case FIRE_SC_SEMICOLON: return ';'; case FIRE_SC_APOSTROPHE: return '\'';
        case FIRE_SC_GRAVE: return '`'; case FIRE_SC_COMMA: return ','; case FIRE_SC_PERIOD: return '.'; case FIRE_SC_SLASH: return '/';
        case FIRE_SC_NONUSBACKSLASH: return '<';
        default: return FIRE_KEY_FROM_SCAN(sc);
    }
}

/* The key code from the character the layout gives the key without shift (`unshifted`, a code point; 0: none) - only keys of the main block take a character (the
 * number block, the function keys and the arrows keep their own code even when the layout names a character, as in SDL). */
static int fire_key_code(int sc, uint32_t unshifted) {
    int mainBlock = (sc >= FIRE_SC_A && sc <= FIRE_SC_0) || (sc >= FIRE_SC_MINUS && sc <= FIRE_SC_SLASH) || sc == FIRE_SC_NONUSBACKSLASH;
    if (mainBlock && unshifted >= 32 && unshifted != 127) {
        if (unshifted >= 'A' && unshifted <= 'Z') unshifted += 'a' - 'A';
        else if (unshifted >= 0xC0 && unshifted <= 0xDE && unshifted != 0xD7) unshifted += 0x20;   /* (Latin-1 capitals: Ä -> ä) */
        return (int)unshifted;
    }
    return fire_key_fromScan(sc);
}

/* The modifier bit of a modifier key (0: none). */
static int fire_key_modBit(int sc) {
    switch (sc) {
        case FIRE_SC_LSHIFT: return FIRE_MOD_LSHIFT; case FIRE_SC_RSHIFT: return FIRE_MOD_RSHIFT;
        case FIRE_SC_LCTRL: return FIRE_MOD_LCTRL; case FIRE_SC_RCTRL: return FIRE_MOD_RCTRL;
        case FIRE_SC_LALT: return FIRE_MOD_LALT; case FIRE_SC_RALT: return FIRE_MOD_RALT;
        case FIRE_SC_LGUI: return FIRE_MOD_LGUI; case FIRE_SC_RGUI: return FIRE_MOD_RGUI;
        default: return 0;
    }
}

/* ---- Linux: evdev key codes (linux/input-event-codes.h) -> scan codes ---- */
static int fire_key_fromEvdev(unsigned code) {
    static const unsigned char t[128] = {
        /*   0 */ 0, FIRE_SC_ESCAPE, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, FIRE_SC_MINUS, FIRE_SC_EQUALS, FIRE_SC_BACKSPACE, FIRE_SC_TAB,
        /*  16 */ 20, 26, 8, 21, 23, 28, 24, 12, 18, 19, FIRE_SC_LEFTBRACKET, FIRE_SC_RIGHTBRACKET, FIRE_SC_RETURN, FIRE_SC_LCTRL, 4, 22,
        /*  32 */ 7, 9, 10, 11, 13, 14, 15, FIRE_SC_SEMICOLON, FIRE_SC_APOSTROPHE, FIRE_SC_GRAVE, FIRE_SC_LSHIFT, FIRE_SC_BACKSLASH, 29, 27, 6, 25,
        /*  48 */ 5, 17, 16, FIRE_SC_COMMA, FIRE_SC_PERIOD, FIRE_SC_SLASH, FIRE_SC_RSHIFT, FIRE_SC_KP_MULTIPLY, FIRE_SC_LALT, FIRE_SC_SPACE, FIRE_SC_CAPSLOCK, 58, 59, 60, 61, 62,
        /*  64 */ 63, 64, 65, 66, 67, FIRE_SC_NUMLOCK, FIRE_SC_SCROLLLOCK, 95, 96, 97, FIRE_SC_KP_MINUS, 92, 93, 94, FIRE_SC_KP_PLUS, 89,
        /*  80 */ 90, 91, FIRE_SC_KP_0, FIRE_SC_KP_PERIOD, 0, FIRE_SC_LANG2, FIRE_SC_NONUSBACKSLASH, 68, 69, FIRE_SC_INTERNATIONAL1, 0, 0, 0, 0, 0, 0,
        /*  96 */ FIRE_SC_KP_ENTER, FIRE_SC_RCTRL, FIRE_SC_KP_DIVIDE, FIRE_SC_PRINTSCREEN, FIRE_SC_RALT, 0, FIRE_SC_HOME, FIRE_SC_UP, FIRE_SC_PAGEUP, FIRE_SC_LEFT, FIRE_SC_RIGHT,
                  FIRE_SC_END, FIRE_SC_DOWN, FIRE_SC_PAGEDOWN, FIRE_SC_INSERT, FIRE_SC_DELETE,
        /* 112 */ 0, FIRE_SC_MUTE, FIRE_SC_VOLUMEDOWN, FIRE_SC_VOLUMEUP, FIRE_SC_POWER, FIRE_SC_KP_EQUALS, 0, FIRE_SC_PAUSE, 0, FIRE_SC_KP_COMMA, FIRE_SC_LANG1, FIRE_SC_LANG2,
                  FIRE_SC_INTERNATIONAL3, FIRE_SC_LGUI, FIRE_SC_RGUI, FIRE_SC_APPLICATION
    };
    if (code < 128) return t[code];
    if (code >= 183 && code <= 194) return FIRE_SC_F13 + (int)(code - 183);
    if (code == 138) return FIRE_SC_HELP;
    if (code == 139) return FIRE_SC_MENU;
    return FIRE_SC_UNKNOWN;
}

/* ---- Windows: scan codes of set 1 (bits 16-23 of a key message's lParam; `extended`: bit 24) -> scan codes ---- */
static int fire_key_fromWin32(unsigned sc, int extended) {
    static const unsigned char t[128] = {
        /* 0x00 */ 0, FIRE_SC_ESCAPE, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, FIRE_SC_MINUS, FIRE_SC_EQUALS, FIRE_SC_BACKSPACE, FIRE_SC_TAB,
        /* 0x10 */ 20, 26, 8, 21, 23, 28, 24, 12, 18, 19, FIRE_SC_LEFTBRACKET, FIRE_SC_RIGHTBRACKET, FIRE_SC_RETURN, FIRE_SC_LCTRL, 4, 22,
        /* 0x20 */ 7, 9, 10, 11, 13, 14, 15, FIRE_SC_SEMICOLON, FIRE_SC_APOSTROPHE, FIRE_SC_GRAVE, FIRE_SC_LSHIFT, FIRE_SC_BACKSLASH, 29, 27, 6, 25,
        /* 0x30 */ 5, 17, 16, FIRE_SC_COMMA, FIRE_SC_PERIOD, FIRE_SC_SLASH, FIRE_SC_RSHIFT, FIRE_SC_KP_MULTIPLY, FIRE_SC_LALT, FIRE_SC_SPACE, FIRE_SC_CAPSLOCK, 58, 59, 60, 61, 62,
        /* 0x40 */ 63, 64, 65, 66, 67, FIRE_SC_PAUSE, FIRE_SC_SCROLLLOCK, 95, 96, 97, FIRE_SC_KP_MINUS, 92, 93, 94, FIRE_SC_KP_PLUS, 89,
        /* 0x50 */ 90, 91, FIRE_SC_KP_0, FIRE_SC_KP_PERIOD, 0, 0, FIRE_SC_NONUSBACKSLASH, 68, 69, FIRE_SC_KP_EQUALS, 0, 0, 0, 0, 0, 0,
        /* 0x60 */ 0, 0, 0, 0, 104, 105, 106, 107, 108, 109, 110, 111, 112, 113, 114, 0,
        /* 0x70 */ FIRE_SC_INTERNATIONAL3 - 1, 0, 0, FIRE_SC_INTERNATIONAL1, 0, 0, 115, 0, 0, FIRE_SC_INTERNATIONAL3 + 1, 0, FIRE_SC_INTERNATIONAL3 + 2, 0, FIRE_SC_INTERNATIONAL3, 0, 0
    };
    if (extended) {
        switch (sc) {
            case 0x1C: return FIRE_SC_KP_ENTER;
            case 0x1D: return FIRE_SC_RCTRL;
            case 0x35: return FIRE_SC_KP_DIVIDE;
            case 0x37: return FIRE_SC_PRINTSCREEN;
            case 0x38: return FIRE_SC_RALT;
            case 0x45: return FIRE_SC_NUMLOCK;
            case 0x47: return FIRE_SC_HOME;
            case 0x48: return FIRE_SC_UP;
            case 0x49: return FIRE_SC_PAGEUP;
            case 0x4B: return FIRE_SC_LEFT;
            case 0x4D: return FIRE_SC_RIGHT;
            case 0x4F: return FIRE_SC_END;
            case 0x50: return FIRE_SC_DOWN;
            case 0x51: return FIRE_SC_PAGEDOWN;
            case 0x52: return FIRE_SC_INSERT;
            case 0x53: return FIRE_SC_DELETE;
            case 0x5B: return FIRE_SC_LGUI;
            case 0x5C: return FIRE_SC_RGUI;
            case 0x5D: return FIRE_SC_APPLICATION;
            case 0x20: return FIRE_SC_MUTE;
            case 0x2E: return FIRE_SC_VOLUMEDOWN;
            case 0x30: return FIRE_SC_VOLUMEUP;
            default: return FIRE_SC_UNKNOWN;
        }
    }
    return sc < 128 ? t[sc] : FIRE_SC_UNKNOWN;
}

/* ---- macOS: virtual key codes (kVK_..., the position on the keyboard) -> scan codes ---- */
static int fire_key_fromMac(unsigned vk) {
    static const unsigned char t[128] = {
        /* 0x00 */ 4, 22, 7, 9, 11, 10, 29, 27, 6, 25, FIRE_SC_NONUSBACKSLASH, 5, 20, 26, 8, 21,
        /* 0x10 */ 28, 23, 30, 31, 32, 33, 35, 34, FIRE_SC_EQUALS, 38, 36, FIRE_SC_MINUS, 37, 39, FIRE_SC_RIGHTBRACKET, 18,
        /* 0x20 */ 24, FIRE_SC_LEFTBRACKET, 12, 19, FIRE_SC_RETURN, 15, 13, FIRE_SC_APOSTROPHE, 14, FIRE_SC_SEMICOLON, FIRE_SC_BACKSLASH, FIRE_SC_COMMA, FIRE_SC_SLASH, 17, 16, FIRE_SC_PERIOD,
        /* 0x30 */ FIRE_SC_TAB, FIRE_SC_SPACE, FIRE_SC_GRAVE, FIRE_SC_BACKSPACE, 0, FIRE_SC_ESCAPE, FIRE_SC_RGUI, FIRE_SC_LGUI, FIRE_SC_LSHIFT, FIRE_SC_CAPSLOCK, FIRE_SC_LALT, FIRE_SC_LCTRL,
                   FIRE_SC_RSHIFT, FIRE_SC_RALT, FIRE_SC_RCTRL, 0,
        /* 0x40 */ 108, FIRE_SC_KP_PERIOD, 0, FIRE_SC_KP_MULTIPLY, 0, FIRE_SC_KP_PLUS, 0, FIRE_SC_KP_CLEAR, FIRE_SC_VOLUMEUP, FIRE_SC_VOLUMEDOWN, FIRE_SC_MUTE, FIRE_SC_KP_DIVIDE,
                   FIRE_SC_KP_ENTER, 0, FIRE_SC_KP_MINUS, 109,
        /* 0x50 */ 110, FIRE_SC_KP_EQUALS, FIRE_SC_KP_0, 89, 90, 91, 92, 93, 94, 95, 111, 96, 97, 0, 0, 0,
        /* 0x60 */ 62, 63, 64, 60, 65, 66, 0, 68, 0, 104, 107, 105, 0, 67, 0, 69,
        /* 0x70 */ 0, 106, FIRE_SC_INSERT, FIRE_SC_HOME, FIRE_SC_PAGEUP, FIRE_SC_DELETE, 61, FIRE_SC_END, 59, FIRE_SC_PAGEDOWN, 58, FIRE_SC_LEFT, FIRE_SC_RIGHT, FIRE_SC_DOWN, FIRE_SC_UP, 0
    };
    return vk < 128 ? t[vk] : FIRE_SC_UNKNOWN;
}

#endif
