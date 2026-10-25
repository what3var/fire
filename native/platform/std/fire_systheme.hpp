// fire native platform package "std": what the desktop says about how programs should look (`SystemTheme.Current()` of `#import "windows"`, used by the UI
// library by default): the scale factor of the display, dark or light mode, the accent colour and the font of the interface. Windows reads the registry and
// the system metrics, Linux the GNOME settings (gsettings) or KDE's kdeglobals, macOS `defaults`, the browser its media queries. What cannot be found keeps
// its default; FIRE_SYSTEM_THEME=off switches the query off (the defaults: scale 1, light, the accent 0x0078D7, no font).
// A board without a desktop (fire_display_none.hpp) reports `embedded`: the UI then uses its fast built-in font, whole-number scaling and a dark violet theme.
#pragma once
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#ifdef _WIN32
#  ifndef WIN32_LEAN_AND_MEAN
#    define WIN32_LEAN_AND_MEAN
#  endif
#  include <windows.h>
#endif
#ifdef __EMSCRIPTEN__
#  include <emscripten.h>
#endif

namespace fire { namespace plat { namespace disp {

struct SystemTheme {
    bool embedded = false;    // a board without a desktop
    double scale = 1.0;       // logical pixels -> pixels of the screen
    int dark = 0;             // 1: dark mode
    int accent = 0x0078D7;    // 0xRRGGBB
    std::string fonts;        // candidates for the font of the interface, the best first, separated by ';' ("" = none known)
    int fontSize = 0;         // the height of the letters in logical pixels (0 = not known)
};

inline bool systemThemeOff() {
    const char* v = std::getenv("FIRE_SYSTEM_THEME");
    return v && (std::strcmp(v, "off") == 0 || std::strcmp(v, "0") == 0 || std::strcmp(v, "false") == 0);
}

inline std::string themeTrim(std::string s) {
    size_t a = 0, b = s.size();
    while (a < b && (s[a] == ' ' || s[a] == '\t' || s[a] == '\n' || s[a] == '\r' || s[a] == '\'' || s[a] == '"')) a++;
    while (b > a && (s[b - 1] == ' ' || s[b - 1] == '\t' || s[b - 1] == '\n' || s[b - 1] == '\r' || s[b - 1] == '\'' || s[b - 1] == '"')) b--;
    return s.substr(a, b - a);
}

/// "Cantarell 11" -> ("Cantarell", 11 points); "Noto Sans,10,-1,5,50,..." (KDE) the same.
inline void themeFontSpec(const std::string& spec, std::string& family, double& points) {
    std::string s = themeTrim(spec);
    size_t comma = s.find(',');
    if (comma != std::string::npos) {
        family = s.substr(0, comma);
        points = std::atof(s.c_str() + comma + 1);
        return;
    }
    size_t space = s.find_last_of(' ');
    if (space != std::string::npos && std::atof(s.c_str() + space + 1) > 0) { family = s.substr(0, space); points = std::atof(s.c_str() + space + 1); }
    else { family = s; points = 0; }
}

#if !defined(_WIN32) && !defined(__EMSCRIPTEN__)
/// The output of a command (its first line), "" when it fails.
inline std::string themeRun(const char* command) {
    std::FILE* p = popen(command, "r");
    if (!p) return "";
    char buf[512];
    std::string out;
    while (std::fgets(buf, sizeof buf, p)) out += buf;
    pclose(p);
    size_t nl = out.find('\n');
    if (nl != std::string::npos) out.resize(nl);
    return themeTrim(out);
}

/// A key of a section of an INI-like file (KDE's kdeglobals), "" when it is not there.
inline std::string themeIni(const std::string& path, const char* section, const char* key) {
    std::FILE* f = std::fopen(path.c_str(), "r");
    if (!f) return "";
    char line[512];
    std::string current, result;
    size_t keyLen = std::strlen(key);
    while (std::fgets(line, sizeof line, f)) {
        std::string l = themeTrim(line);
        if (!l.empty() && l[0] == '[') { current = l; continue; }
        if (current == std::string("[") + section + "]" && l.compare(0, keyLen, key) == 0 && l.size() > keyLen && l[keyLen] == '=') { result = l.substr(keyLen + 1); break; }
    }
    std::fclose(f);
    return result;
}
#endif

/// The accent colour of GNOME (47 and later) by its name.
inline int themeGnomeAccent(const std::string& name) {
    static const struct { const char* name; int rgb; } colors[] = {
        {"blue", 0x3584E4}, {"teal", 0x2190A4}, {"green", 0x3A944A}, {"yellow", 0xC88800}, {"orange", 0xED5B00},
        {"red", 0xE62D42}, {"pink", 0xD56199}, {"purple", 0x9141AC}, {"slate", 0x6F8396},
    };
    for (auto& c : colors) if (name == c.name) return c.rgb;
    return -1;
}

inline SystemTheme systemTheme() {
    SystemTheme t;
    if (systemThemeOff()) return t;
#if defined(_WIN32)
    // DPI: the program draws in the pixels of the screen itself (otherwise Windows stretches the whole window and it becomes blurry)
    HMODULE user32 = GetModuleHandleW(L"user32.dll");
    if (user32) {
        typedef BOOL (WINAPI *SetCtx)(HANDLE);
        typedef UINT (WINAPI *GetDpi)(void);
        if (SetCtx setCtx = (SetCtx)(void*)GetProcAddress(user32, "SetProcessDpiAwarenessContext")) setCtx((HANDLE)-4);   // per monitor v2
        else if (FARPROC aware = GetProcAddress(user32, "SetProcessDPIAware")) ((BOOL (WINAPI *)(void))(void*)aware)();
        // (Windows 10 and later; before that the scale stays 1 - GetDeviceCaps would need gdi32, which a program does not link by default)
        if (GetDpi getDpi = (GetDpi)(void*)GetProcAddress(user32, "GetDpiForSystem")) { UINT dpi = getDpi(); if (dpi >= 48) t.scale = dpi / 96.0; }
    }
    DWORD value = 0, size = sizeof value;
    if (RegGetValueW(HKEY_CURRENT_USER, L"Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize", L"AppsUseLightTheme", RRF_RT_REG_DWORD, nullptr, &value, &size) == ERROR_SUCCESS)
        t.dark = value == 0 ? 1 : 0;
    size = sizeof value;
    if (RegGetValueW(HKEY_CURRENT_USER, L"Software\\Microsoft\\Windows\\DWM", L"AccentColor", RRF_RT_REG_DWORD, nullptr, &value, &size) == ERROR_SUCCESS)
        t.accent = (int)(((value & 0xFF) << 16) | (value & 0xFF00) | ((value >> 16) & 0xFF));   // stored as 0xAABBGGRR
    NONCLIENTMETRICSW metrics;
    std::memset(&metrics, 0, sizeof metrics);
    metrics.cbSize = sizeof metrics;
    if (SystemParametersInfoW(SPI_GETNONCLIENTMETRICS, sizeof metrics, &metrics, 0)) {
        char face[LF_FACESIZE * 3];
        int n = WideCharToMultiByte(CP_UTF8, 0, metrics.lfMessageFont.lfFaceName, -1, face, sizeof face, nullptr, nullptr);
        if (n > 1) t.fonts = std::string(face) + ";Segoe UI;Tahoma;Arial";
        int height = metrics.lfMessageFont.lfHeight < 0 ? -metrics.lfMessageFont.lfHeight : metrics.lfMessageFont.lfHeight;
        if (height > 0) t.fontSize = (int)(height / t.scale + 0.5);
    }
#elif defined(__EMSCRIPTEN__)
    t.dark = EM_ASM_INT({ return (typeof window !== 'undefined' && window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches) ? 1 : 0; });
#elif defined(__APPLE__)
    // the window is scaled by macOS itself (points): the scale stays 1
    if (themeRun("defaults read -g AppleInterfaceStyle 2>/dev/null") == "Dark") t.dark = 1;
    std::string accent = themeRun("defaults read -g AppleAccentColor 2>/dev/null");
    if (!accent.empty()) {
        static const int colors[] = {0x8C8C8C, 0xFF5257, 0xF7821B, 0xFFC600, 0x62BA46, 0x007AFF, 0xA550A7, 0xF74F9E};   // -1 (graphite) .. 6
        int i = std::atoi(accent.c_str()) + 1;
        if (i >= 0 && i < 8) t.accent = colors[i];
    } else t.accent = 0x007AFF;
    t.fonts = "SF Pro Text;SF Pro;SFNS;Helvetica Neue;Helvetica;Arial";
    t.fontSize = 13;
#else
    // Linux and the other Unix desktops: GNOME (gsettings), else KDE (kdeglobals); the scale factors of GDK and Qt in the environment win
    const char* home = std::getenv("HOME");
    std::string kde = home ? std::string(home) + "/.config/kdeglobals" : "";
    std::string scheme = themeRun("gsettings get org.gnome.desktop.interface color-scheme 2>/dev/null");
    std::string gtkTheme = themeRun("gsettings get org.gnome.desktop.interface gtk-theme 2>/dev/null");
    bool gnome = !scheme.empty() || !gtkTheme.empty();
    if (gnome) {
        if (scheme == "prefer-dark" || gtkTheme.find("dark") != std::string::npos || gtkTheme.find("Dark") != std::string::npos) t.dark = 1;
        int accent = themeGnomeAccent(themeRun("gsettings get org.gnome.desktop.interface accent-color 2>/dev/null"));
        if (accent >= 0) t.accent = accent;
        else t.accent = 0x3584E4;
        std::string family; double points = 0;
        themeFontSpec(themeRun("gsettings get org.gnome.desktop.interface font-name 2>/dev/null"), family, points);
        if (!family.empty()) t.fonts = family + ";Cantarell;Noto Sans;DejaVu Sans;Liberation Sans";
        if (points > 0) t.fontSize = (int)(points * 96.0 / 72.0 + 0.5);
        double factor = std::atof(themeRun("gsettings get org.gnome.desktop.interface scaling-factor 2>/dev/null | awk '{print $NF}'").c_str());
        double text = std::atof(themeRun("gsettings get org.gnome.desktop.interface text-scaling-factor 2>/dev/null").c_str());
        if (factor >= 1) t.scale = factor;
        if (text > 0.5 && text < 4) t.scale *= text;
    } else if (!kde.empty()) {
        std::string colors = themeIni(kde, "General", "ColorScheme");
        if (colors.find("Dark") != std::string::npos || colors.find("dark") != std::string::npos) t.dark = 1;
        std::string accent = themeIni(kde, "General", "AccentColor");
        int r, g, b;
        if (std::sscanf(accent.c_str(), "%d,%d,%d", &r, &g, &b) == 3) t.accent = (r << 16) | (g << 8) | b;
        std::string family; double points = 0;
        themeFontSpec(themeIni(kde, "General", "font"), family, points);
        if (!family.empty()) t.fonts = family + ";Noto Sans;DejaVu Sans;Liberation Sans";
        if (points > 0) t.fontSize = (int)(points * 96.0 / 72.0 + 0.5);
    }
    if (const char* gdk = std::getenv("GDK_SCALE")) { double s = std::atof(gdk); if (s >= 1) t.scale = s; }
    if (const char* qt = std::getenv("QT_SCALE_FACTOR")) { double s = std::atof(qt); if (s >= 0.5) t.scale = s; }
#endif
    if (t.scale < 0.5 || t.scale > 8) t.scale = 1.0;
    return t;
}

}}}
