/* fire native platform package "std": what the desktop says about how programs should look (`SystemTheme.Current()` of `#import "windows"`, used by the UI
 * library by default): the scale factor of the display, dark or light mode, the accent colour and the font of the interface. Windows reads the registry and
 * the system metrics, Linux the GNOME settings (gsettings) or KDE's kdeglobals, macOS `defaults`, the browser its media queries. What cannot be found keeps
 * its default; FIRE_SYSTEM_THEME=off switches the query off (the defaults: scale 1, light, the accent 0x0078D7, no font).
 * A board without a desktop (fire_display_none.h) reports `embedded`: the UI then uses its fast built-in font, whole-number scaling and a dark violet theme. */
#ifndef FIRE_SYSTHEME_H
#define FIRE_SYSTHEME_H

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#ifdef _WIN32
#  ifndef WIN32_LEAN_AND_MEAN
#    define WIN32_LEAN_AND_MEAN
#  endif
#  include <windows.h>
#endif
#ifdef __EMSCRIPTEN__
#  include <emscripten.h>
#endif
#include "fire_display_c.h"

static int fire_theme_off(void) {
    const char* v = getenv("FIRE_SYSTEM_THEME");
    return v && (strcmp(v, "off") == 0 || strcmp(v, "0") == 0 || strcmp(v, "false") == 0);
}

/* Trims blanks and quotes at both ends, in place. */
static char* fire_theme_trim(char* s) {
    size_t a = 0, b = strlen(s);
    while (a < b && (s[a] == ' ' || s[a] == '\t' || s[a] == '\n' || s[a] == '\r' || s[a] == '\'' || s[a] == '"')) a++;
    while (b > a && (s[b - 1] == ' ' || s[b - 1] == '\t' || s[b - 1] == '\n' || s[b - 1] == '\r' || s[b - 1] == '\'' || s[b - 1] == '"')) b--;
    memmove(s, s + a, b - a);
    s[b - a] = 0;
    return s;
}

/* "Cantarell 11" -> ("Cantarell", 11 points); "Noto Sans,10,-1,5,50,..." (KDE) the same. `family` has room for `size` bytes. */
static void fire_theme_fontSpec(const char* spec, char* family, size_t size, double* points) {
    char s[512];
    char* comma;
    char* space;
    snprintf(s, sizeof s, "%s", spec);
    fire_theme_trim(s);
    comma = strchr(s, ',');
    if (comma != NULL) {
        *comma = 0;
        snprintf(family, size, "%s", s);
        *points = atof(comma + 1);
        return;
    }
    space = strrchr(s, ' ');
    if (space != NULL && atof(space + 1) > 0) { *points = atof(space + 1); *space = 0; snprintf(family, size, "%s", s); }
    else { snprintf(family, size, "%s", s); *points = 0; }
}

/* "family;a;b" into the fonts of the theme. */
static void fire_theme_fonts(FireSystemTheme* t, const char* family, const char* rest) { snprintf(t->fonts, sizeof t->fonts, "%s%s", family, rest); }

#if !defined(_WIN32) && !defined(__EMSCRIPTEN__)
/* The output of a command (its first line, trimmed) into `out` (`size` bytes), "" when it fails. */
static char* fire_theme_run(const char* command, char* out, size_t size) {
    FILE* p = popen(command, "r");
    char buf[512];
    size_t n = 0;
    out[0] = 0;
    if (!p) return out;
    while (fgets(buf, sizeof buf, p)) {
        size_t k = strlen(buf);
        if (n + k >= size) k = size - 1 - n;
        memcpy(out + n, buf, k);
        n += k;
        out[n] = 0;
    }
    pclose(p);
    {
        char* nl = strchr(out, '\n');
        if (nl != NULL) *nl = 0;
    }
    return fire_theme_trim(out);
}

/* A key of a section of an INI-like file (KDE's kdeglobals) into `out`, "" when it is not there. */
static char* fire_theme_ini(const char* path, const char* section, const char* key, char* out, size_t size) {
    FILE* f = fopen(path, "r");
    char line[512], current[512], wanted[512];
    size_t keyLen = strlen(key);
    out[0] = 0;
    if (!f) return out;
    current[0] = 0;
    snprintf(wanted, sizeof wanted, "[%s]", section);
    while (fgets(line, sizeof line, f)) {
        char* l = fire_theme_trim(line);
        size_t n = strlen(l);
        if (n > 0 && l[0] == '[') { snprintf(current, sizeof current, "%s", l); continue; }
        if (strcmp(current, wanted) == 0 && strncmp(l, key, keyLen) == 0 && n > keyLen && l[keyLen] == '=') { snprintf(out, size, "%s", l + keyLen + 1); break; }
    }
    fclose(f);
    return out;
}
#endif

/* The accent colour of GNOME (47 and later) by its name. */
static int fire_theme_gnomeAccent(const char* name) {
    static const struct { const char* name; int rgb; } colors[] = {
        {"blue", 0x3584E4}, {"teal", 0x2190A4}, {"green", 0x3A944A}, {"yellow", 0xC88800}, {"orange", 0xED5B00},
        {"red", 0xE62D42}, {"pink", 0xD56199}, {"purple", 0x9141AC}, {"slate", 0x6F8396},
    };
    size_t i;
    for (i = 0; i < sizeof colors / sizeof colors[0]; i++) if (strcmp(name, colors[i].name) == 0) return colors[i].rgb;
    return -1;
}

static void fire_display_systemTheme(FireSystemTheme* t) {
    fire_display_themeDefaults(t);
    if (fire_theme_off()) return;
#if defined(_WIN32)
    {
        /* DPI: the program draws in the pixels of the screen itself (otherwise Windows stretches the whole window and it becomes blurry) */
        typedef BOOL (WINAPI *SetCtx)(HANDLE);
        typedef UINT (WINAPI *GetDpi)(void);
        HMODULE user32 = GetModuleHandleW(L"user32.dll");
        DWORD value = 0, size = sizeof value;
        NONCLIENTMETRICSW metrics;
        if (user32) {
            SetCtx setCtx = (SetCtx)(void*)GetProcAddress(user32, "SetProcessDpiAwarenessContext");
            GetDpi getDpi;
            if (setCtx) setCtx((HANDLE)-4);   /* per monitor v2 */
            else {
                FARPROC aware = GetProcAddress(user32, "SetProcessDPIAware");
                if (aware) ((BOOL (WINAPI *)(void))(void*)aware)();
            }
            /* (Windows 10 and later; before that the scale stays 1 - GetDeviceCaps would need gdi32, which a program does not link by default) */
            getDpi = (GetDpi)(void*)GetProcAddress(user32, "GetDpiForSystem");
            if (getDpi) { UINT dpi = getDpi(); if (dpi >= 48) t->scale = dpi / 96.0; }
        }
        if (RegGetValueW(HKEY_CURRENT_USER, L"Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize", L"AppsUseLightTheme", RRF_RT_REG_DWORD, NULL, &value, &size) == ERROR_SUCCESS)
            t->dark = value == 0 ? 1 : 0;
        size = sizeof value;
        if (RegGetValueW(HKEY_CURRENT_USER, L"Software\\Microsoft\\Windows\\DWM", L"AccentColor", RRF_RT_REG_DWORD, NULL, &value, &size) == ERROR_SUCCESS)
            t->accent = (int)(((value & 0xFF) << 16) | (value & 0xFF00) | ((value >> 16) & 0xFF));   /* stored as 0xAABBGGRR */
        memset(&metrics, 0, sizeof metrics);
        metrics.cbSize = sizeof metrics;
        if (SystemParametersInfoW(SPI_GETNONCLIENTMETRICS, sizeof metrics, &metrics, 0)) {
            char face[LF_FACESIZE * 3];
            int n = WideCharToMultiByte(CP_UTF8, 0, metrics.lfMessageFont.lfFaceName, -1, face, sizeof face, NULL, NULL);
            int height = metrics.lfMessageFont.lfHeight < 0 ? -metrics.lfMessageFont.lfHeight : metrics.lfMessageFont.lfHeight;
            if (n > 1) fire_theme_fonts(t, face, ";Segoe UI;Tahoma;Arial");
            if (height > 0) t->fontSize = (int)(height / t->scale + 0.5);
        }
    }
#elif defined(__EMSCRIPTEN__)
    t->dark = EM_ASM_INT({ return (typeof window !== 'undefined' && window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches) ? 1 : 0; });
#elif defined(__APPLE__)
    {
        char out[512];
        /* the window is scaled by macOS itself (points): the scale stays 1 */
        if (strcmp(fire_theme_run("defaults read -g AppleInterfaceStyle 2>/dev/null", out, sizeof out), "Dark") == 0) t->dark = 1;
        fire_theme_run("defaults read -g AppleAccentColor 2>/dev/null", out, sizeof out);
        if (out[0]) {
            static const int colors[] = {0x8C8C8C, 0xFF5257, 0xF7821B, 0xFFC600, 0x62BA46, 0x007AFF, 0xA550A7, 0xF74F9E};   /* -1 (graphite) .. 6 */
            int i = atoi(out) + 1;
            if (i >= 0 && i < 8) t->accent = colors[i];
        } else t->accent = 0x007AFF;
        snprintf(t->fonts, sizeof t->fonts, "%s", "SF Pro Text;SF Pro;SFNS;Helvetica Neue;Helvetica;Arial");
        t->fontSize = 13;
    }
#else
    {
        /* Linux and the other Unix desktops: GNOME (gsettings), else KDE (kdeglobals); the scale factors of GDK and Qt in the environment win */
        const char* home = getenv("HOME");
        char kde[1024], scheme[512], gtkTheme[512], out[512], family[512];
        const char* gdk;
        const char* qt;
        double points = 0;
        int gnome;
        if (home) snprintf(kde, sizeof kde, "%s/.config/kdeglobals", home); else kde[0] = 0;
        fire_theme_run("gsettings get org.gnome.desktop.interface color-scheme 2>/dev/null", scheme, sizeof scheme);
        fire_theme_run("gsettings get org.gnome.desktop.interface gtk-theme 2>/dev/null", gtkTheme, sizeof gtkTheme);
        gnome = scheme[0] != 0 || gtkTheme[0] != 0;
        if (gnome) {
            int accent;
            double factor, text;
            if (strcmp(scheme, "prefer-dark") == 0 || strstr(gtkTheme, "dark") != NULL || strstr(gtkTheme, "Dark") != NULL) t->dark = 1;
            accent = fire_theme_gnomeAccent(fire_theme_run("gsettings get org.gnome.desktop.interface accent-color 2>/dev/null", out, sizeof out));
            if (accent >= 0) t->accent = accent;
            else t->accent = 0x3584E4;
            fire_theme_fontSpec(fire_theme_run("gsettings get org.gnome.desktop.interface font-name 2>/dev/null", out, sizeof out), family, sizeof family, &points);
            if (family[0]) fire_theme_fonts(t, family, ";Cantarell;Noto Sans;DejaVu Sans;Liberation Sans");
            if (points > 0) t->fontSize = (int)(points * 96.0 / 72.0 + 0.5);
            factor = atof(fire_theme_run("gsettings get org.gnome.desktop.interface scaling-factor 2>/dev/null | awk '{print $NF}'", out, sizeof out));
            text = atof(fire_theme_run("gsettings get org.gnome.desktop.interface text-scaling-factor 2>/dev/null", out, sizeof out));
            if (factor >= 1) t->scale = factor;
            if (text > 0.5 && text < 4) t->scale *= text;
        } else if (kde[0]) {
            int r, g, b;
            fire_theme_ini(kde, "General", "ColorScheme", out, sizeof out);
            if (strstr(out, "Dark") != NULL || strstr(out, "dark") != NULL) t->dark = 1;
            fire_theme_ini(kde, "General", "AccentColor", out, sizeof out);
            if (sscanf(out, "%d,%d,%d", &r, &g, &b) == 3) t->accent = (r << 16) | (g << 8) | b;
            fire_theme_fontSpec(fire_theme_ini(kde, "General", "font", out, sizeof out), family, sizeof family, &points);
            if (family[0]) fire_theme_fonts(t, family, ";Noto Sans;DejaVu Sans;Liberation Sans");
            if (points > 0) t->fontSize = (int)(points * 96.0 / 72.0 + 0.5);
        }
        if ((gdk = getenv("GDK_SCALE")) != NULL) { double s = atof(gdk); if (s >= 1) t->scale = s; }
        if ((qt = getenv("QT_SCALE_FACTOR")) != NULL) { double s = atof(qt); if (s >= 0.5) t->scale = s; }
    }
#endif
    if (t->scale < 0.5 || t->scale > 8) t->scale = 1.0;
}

#endif
