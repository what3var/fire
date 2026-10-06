// fire native bridge "time": the natives behind `#import "time"` (SPEC 8.15) - `Sleep` and what DateTime and TimeSpan are written with (src/fire/Standard/TimePrelude.cs).
//
// This file is the one implementation of the time functions: the native build includes it (the package "time" brings it as its C++ source), and the virtual machine runs it in
// a shared library built from it (native/abi/fire_pkg_abi.h). It uses the runtime and, from the platform package,
//   plat::unixMicros()     the wall clock: microseconds since 1970-01-01 UTC
// and the C library for the time zone (localtime_r; define FIRE_NO_LOCALTIME on a board without a time zone database: local time is then UTC).
// `Sleep` is only compiled in the native build (it uses plat::sleepMs through the runtime's sleepTicks); the VM waits by itself.
// Time is counted in ticks of 100 ns; a DateTime counts from 0001-01-01 (like .NET). DateTime.Parse reads
// a subset of the formats .NET knows: ISO dates and times (`2024-03-15 14:30:00`, `2024-03-15T14:30:00.5Z`, `+02:00`), `M/d/yyyy`,
// month names (`March 15, 2024`, `March 2024`, `Fri, 15 Mar 2024 14:30:00 GMT`), `yyyy-MM`, `3:45 PM`.
#pragma once

#include <cctype>
#include <cmath>
#include <ctime>
#include <string>
#include <vector>

namespace fire {

constexpr int64_t TICKS_PER_SECOND = 10000000;
constexpr int64_t TICKS_PER_DAY = 864000000000LL;
constexpr int64_t TICKS_UNIX_EPOCH = 621355968000000000LL;   // 1970-01-01
constexpr int64_t TICKS_MAX = 3155378975999999999LL;         // 9999-12-31 23:59:59.9999999

/// A failed time function: a TimeException (the prelude class), or the end of the program when the program has no exceptions.
inline Value timeFail(const char* text) { return fireError("TimeException", text); }

inline Value tmString(const std::string& text, OwnList* list) {
    Str* s = allocStr((uint32_t)text.size(), list);
    widenAscii(text.data(), (uint32_t)text.size(), strChars(s));
    return StrV(s);
}

inline double tmNum(Value v) { return v.kind == K_Float ? (double)v.f : v.kind == K_Int ? (double)v.i : 0.0; }
inline int64_t tmRound(double x) { return (int64_t)std::nearbyint(x); }   // half to even, like Math.Round

// ---- Sleep and units -----------------------------------------------------------------------------------------------------------------------------------------
// Only in the native build: in the virtual machine `Sleep` and `__time_unit_ticks` are run by the VM itself (the `host` functions of the package: a wait has to be abortable, and
// the units of a value do not cross the ABI), the library has no part of them.
#ifndef FIRE_LIBRARY
/// A time as ticks, the way `Sleep` and the waiting functions take it: a TimeSpan, a number with a unit of time (`500ms`), or a number (milliseconds).
/// False when it was none of these (an exception is then unwinding).
inline bool timeToTicks(Value a, int64_t& ticks, const char* who) {
    char text[160];
    if (a.kind == K_Class) {
        if (timeObjTicks(a, ticks)) return true;
        std::snprintf(text, sizeof text, "%s expects a TimeSpan, got: %s.", who, className(asObj(a)->cls));
    } else if (a.kind == K_Int || a.kind == K_Float) {
        double number = tmNum(a);
        if (unitIsUnitless(a.unit)) { ticks = tmRound(number * (double)TICKS_PER_MS); return true; }
        double perSecond;
        if (unitOfTime(a.unit, perSecond)) { ticks = tmRound(number * perSecond * 1e7); return true; }
        std::snprintf(text, sizeof text, "'%s' is not a unit of time.", g_ud[a.unit].name);
    } else {
        static const char* const kinds[] = {"Bool", "Int", "Float", "Char", "String", "Class", "Lambda", "Pointer", "Array", "Buffer", "Undefined"};
        std::snprintf(text, sizeof text, "%s expects a TimeSpan, a time value or milliseconds, got: %s.", who, kinds[a.kind]);
    }
    timeFail(text);
    return false;
}

/// A value with a unit of time (`500ms`, `2s`) in ticks, otherwise undefined. (Units do not cross the package ABI: the VM runs this one itself, like Sleep.)
inline Value tm_unitTicks(Value v) {
    if (!(v.kind == K_Int || v.kind == K_Float) || unitIsUnitless(v.unit)) return Undef();
    double perSecond;
    if (!unitOfTime(v.unit, perSecond)) {
        char text[160];
        std::snprintf(text, sizeof text, "'%s' is not a unit of time.", g_ud[v.unit].name);
        return timeFail(text);
    }
    return Int(tmRound(tmNum(v) * perSecond * 1e7));
}

/// `Sleep(time)`: the runtime puts the thread to sleep (not deaf: terminate ends it, the main program keeps serving the queue).
inline Value sleepNative(Value a) {
    int64_t ticks;
    if (timeToTicks(a, ticks, "Sleep")) sleepTicks(ticks);
    return Undef();
}
#endif   // FIRE_LIBRARY

// ---- the calendar (proleptic Gregorian, like .NET) ----------------------------------------------------------------------------------
/// Days since 0001-01-01 of a date.
inline int64_t tmDays(int64_t y, int64_t m, int64_t d) {
    y -= m <= 2;
    int64_t era = (y >= 0 ? y : y - 399) / 400;
    int64_t yoe = y - era * 400;
    int64_t doy = (153 * (m + (m > 2 ? -3 : 9)) + 2) / 5 + d - 1;
    int64_t doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    return era * 146097 + doe - 719468 + 719162;   // days since 1970-01-01, shifted to 0001-01-01
}
inline void tmCivil(int64_t days, int64_t& y, int64_t& m, int64_t& d) {
    int64_t z = days - 719162 + 719468;
    int64_t era = (z >= 0 ? z : z - 146096) / 146097;
    int64_t doe = z - era * 146097;
    int64_t yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
    y = yoe + era * 400;
    int64_t doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    int64_t mp = (5 * doy + 2) / 153;
    d = doy - (153 * mp + 2) / 5 + 1;
    m = mp < 10 ? mp + 3 : mp - 9;
    y += m <= 2;
}
inline bool tmLeap(int64_t y) { return (y % 4 == 0 && y % 100 != 0) || y % 400 == 0; }
inline int64_t tmMonthDays(int64_t y, int64_t m) {
    static const int days[] = {31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31};
    return m == 2 && tmLeap(y) ? 29 : days[m - 1];
}

struct TmParts { int64_t year, month, day, hour, minute, second, tick, weekday, dayOfYear; };
inline TmParts tmSplit(int64_t ticks) {
    TmParts p;
    int64_t days = ticks / TICKS_PER_DAY, rest = ticks % TICKS_PER_DAY;
    tmCivil(days, p.year, p.month, p.day);
    p.hour = rest / (3600 * TICKS_PER_SECOND);
    p.minute = rest / (60 * TICKS_PER_SECOND) % 60;
    p.second = rest / TICKS_PER_SECOND % 60;
    p.tick = rest % TICKS_PER_SECOND;   // 100 ns
    p.weekday = (days + 1) % 7;         // 0001-01-01 was a Monday; 0 = Sunday
    p.dayOfYear = days - tmDays(p.year, 1, 1) + 1;
    return p;
}

// ---- the natives ----------------------------------------------------------------------------------------------------------------------
inline Value tm_now() {
    return Int(TICKS_UNIX_EPOCH + plat::unixMicros() * 10);
}

/// The offset of the local time zone to UTC at a point in time (UTC ticks), in ticks.
inline Value tm_localOffset(Value utc) {
#ifdef FIRE_NO_LOCALTIME
    (void)utc;
    return Int(0);
#else
    int64_t ticks = utc.i < 0 ? 0 : utc.i > TICKS_MAX ? TICKS_MAX : utc.i;
    int64_t unixSeconds = (ticks - TICKS_UNIX_EPOCH) / TICKS_PER_SECOND;
    if (ticks < TICKS_UNIX_EPOCH && (ticks - TICKS_UNIX_EPOCH) % TICKS_PER_SECOND != 0) unixSeconds--;
    std::time_t t = (std::time_t)unixSeconds;
    if ((int64_t)t != unixSeconds) return Int(0);
    std::tm lt;
#ifdef _WIN32
    if (localtime_s(&lt, &t) != 0) return Int(0);
#else
    if (!localtime_r(&t, &lt)) return Int(0);
#endif
    int64_t local = (tmDays(lt.tm_year + 1900, lt.tm_mon + 1, lt.tm_mday) - 719162) * 86400 + lt.tm_hour * 3600 + lt.tm_min * 60 + lt.tm_sec;
    return Int((local - unixSeconds) * TICKS_PER_SECOND);
#endif
}

/// [year, month, day, hour, minute, second, millisecond, weekday (0 = Sunday), day of the year]
inline Value tm_parts(Value ticks, OwnList* list) {
    if (ticks.i < 0 || ticks.i > TICKS_MAX) {
        char text[96];
        std::snprintf(text, sizeof text, "Point in time out of range (%lld ticks).", (long long)ticks.i);
        return timeFail(text);
    }
    TmParts p = tmSplit(ticks.i);
    Arr* a = allocArr(9, list);
    Value* it = a->items();
    int64_t v[9] = {p.year, p.month, p.day, p.hour, p.minute, p.second, p.tick / TICKS_PER_MS, p.weekday, p.dayOfYear};
    for (int i = 0; i < 9; i++) it[i] = Int(v[i]);
    return ArrV(a);
}

inline Value tm_make(Value y, Value mo, Value d, Value h, Value mi, Value s, Value ms) {
    if (y.i < 1 || y.i > 9999 || mo.i < 1 || mo.i > 12 || d.i < 1 || d.i > tmMonthDays(y.i, mo.i) || h.i < 0 || h.i > 23 || mi.i < 0 || mi.i > 59 || s.i < 0 || s.i > 59 || ms.i < 0 || ms.i > 999) {
        char text[160];
        std::snprintf(text, sizeof text, "Invalid date/time: %lld-%lld-%lld %lld:%lld:%lld.%lld", (long long)y.i, (long long)mo.i, (long long)d.i, (long long)h.i, (long long)mi.i, (long long)s.i, (long long)ms.i);
        return timeFail(text);
    }
    return Int(tmDays(y.i, mo.i, d.i) * TICKS_PER_DAY + (h.i * 3600 + mi.i * 60 + s.i) * TICKS_PER_SECOND + ms.i * TICKS_PER_MS);
}

inline Value tm_daysInMonth(Value year, Value month) {
    if (year.i < 1 || year.i > 9999 || month.i < 1 || month.i > 12) {
        char text[96];
        std::snprintf(text, sizeof text, "Invalid month %lld-%lld.", (long long)year.i, (long long)month.i);
        return timeFail(text);
    }
    return Int(tmMonthDays(year.i, month.i));
}

inline Value tm_addMonths(Value ticks, Value months) {
    if (ticks.i < 0 || ticks.i > TICKS_MAX) return timeFail("The result is outside the valid time range.");
    TmParts p = tmSplit(ticks.i);
    int64_t total = p.year * 12 + (p.month - 1) + months.i;
    int64_t y = total >= 0 ? total / 12 : -((-total + 11) / 12), m = total - y * 12 + 1;
    if (y < 1 || y > 9999) return timeFail("The result is outside the valid time range.");
    int64_t d = p.day < tmMonthDays(y, m) ? p.day : tmMonthDays(y, m);
    return Int(tmDays(y, m, d) * TICKS_PER_DAY + ticks.i % TICKS_PER_DAY);
}

inline Value tm_toTicks(Value a, Value b) { return Int(tmRound(tmNum(a) * tmNum(b))); }

/// `[-][d.]hh:mm:ss[.fffffff]`
inline Value tm_spanText(Value ticks, OwnList* list) {
    uint64_t t = ticks.i < 0 ? (uint64_t)0 - (uint64_t)ticks.i : (uint64_t)ticks.i;
    uint64_t days = t / (uint64_t)TICKS_PER_DAY, rest = t % (uint64_t)TICKS_PER_DAY;
    char buf[64];
    int n = 0;
    if (ticks.i < 0) buf[n++] = '-';
    if (days) n += std::snprintf(buf + n, sizeof buf - n, "%llu.", (unsigned long long)days);
    n += std::snprintf(buf + n, sizeof buf - n, "%02llu:%02llu:%02llu", (unsigned long long)(rest / (3600ULL * 10000000)), (unsigned long long)(rest / (60ULL * 10000000) % 60), (unsigned long long)(rest / 10000000 % 60));
    uint64_t frac = rest % 10000000;
    if (frac) n += std::snprintf(buf + n, sizeof buf - n, ".%07llu", (unsigned long long)frac);
    return tmString(std::string(buf, (size_t)n), list);
}

// ---- formatting (the .NET format strings, invariant culture) ----------------------------------------------------------------------
inline const char* const TM_MONTHS[] = {"January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"};
inline const char* const TM_DAYS[] = {"Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"};

inline const char* tmStandardFormat(char16_t c) {
    switch (c) {
        case 'd': return "MM/dd/yyyy";
        case 'D': return "dddd, dd MMMM yyyy";
        case 'f': return "dddd, dd MMMM yyyy HH:mm";
        case 'F': return "dddd, dd MMMM yyyy HH:mm:ss";
        case 'g': return "MM/dd/yyyy HH:mm";
        case 'G': return "MM/dd/yyyy HH:mm:ss";
        case 'm': case 'M': return "MMMM dd";
        case 'o': case 'O': return "yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fffffffK";
        case 'r': case 'R': return "ddd, dd MMM yyyy HH':'mm':'ss 'GMT'";
        case 's': return "yyyy'-'MM'-'dd'T'HH':'mm':'ss";
        case 't': return "HH:mm";
        case 'T': return "HH:mm:ss";
        case 'u': return "yyyy'-'MM'-'dd HH':'mm':'ss'Z'";
        case 'y': case 'Y': return "yyyy MMMM";
        default: return nullptr;
    }
}

/// Formats a point in time with a custom format string (`yyyy-MM-dd HH:mm`). False: the format is not valid.
inline bool tmCustom(int64_t ticks, const std::u16string& fmt, std::u16string& out) {
    TmParts p = tmSplit(ticks);
    auto put = [&](const std::string& s) { for (char c : s) out.push_back((char16_t)c); };
    auto number = [&](int64_t v, int width) {
        char buf[32];
        std::snprintf(buf, sizeof buf, "%0*lld", width, (long long)v);
        put(buf);
    };
    size_t i = 0, n = fmt.size();
    auto run = [&](char16_t c) { size_t k = 1; while (i + k < n && fmt[i + k] == c) k++; return k; };
    while (i < n) {
        char16_t c = fmt[i];
        size_t k;
        switch (c) {
            case 'd':
                k = run(c);
                if (k == 1) number(p.day, 1);
                else if (k == 2) number(p.day, 2);
                else if (k == 3) put(std::string(TM_DAYS[p.weekday]).substr(0, 3));
                else put(TM_DAYS[p.weekday]);
                i += k;
                break;
            case 'f': case 'F': {
                k = run(c);
                if (k > 7) return false;
                char buf[16];
                std::snprintf(buf, sizeof buf, "%07lld", (long long)p.tick);
                std::string digits(buf, k);
                if (c == 'F') {
                    while (!digits.empty() && digits.back() == '0') digits.pop_back();
                    if (digits.empty() && !out.empty() && out.back() == u'.') out.pop_back();
                }
                put(digits);
                i += k;
                break;
            }
            case 'g': k = run(c); put("A.D."); i += k; break;
            case 'h': k = run(c); number(p.hour % 12 == 0 ? 12 : p.hour % 12, k > 2 ? 2 : (int)k); i += k; break;
            case 'H': k = run(c); number(p.hour, k > 2 ? 2 : (int)k); i += k; break;
            case 'K': k = run(c); i += k; break;   // the kind of the time is not known here (unspecified): nothing
            case 'm': k = run(c); number(p.minute, k > 2 ? 2 : (int)k); i += k; break;
            case 'M':
                k = run(c);
                if (k <= 2) number(p.month, (int)k);
                else if (k == 3) put(std::string(TM_MONTHS[p.month - 1]).substr(0, 3));
                else put(TM_MONTHS[p.month - 1]);
                i += k;
                break;
            case 's': k = run(c); number(p.second, k > 2 ? 2 : (int)k); i += k; break;
            case 't':
                k = run(c);
                put(k == 1 ? std::string(p.hour < 12 ? "A" : "P") : std::string(p.hour < 12 ? "AM" : "PM"));
                i += k;
                break;
            case 'y':
                k = run(c);
                number(k <= 2 ? p.year % 100 : p.year, (int)k);
                i += k;
                break;
            case 'z': {
                k = run(c);
                int64_t offset = tm_localOffset(Int(ticks - 0)).i / TICKS_PER_SECOND;   // the time is read as local time: the offset at that moment
                put(offset < 0 ? "-" : "+");
                int64_t a = offset < 0 ? -offset : offset;
                number(a / 3600, k == 1 ? 1 : 2);
                if (k >= 3) { put(":"); number(a / 60 % 60, 2); }
                i += k;
                break;
            }
            case ':': out.push_back(u':'); i++; break;
            case '/': out.push_back(u'/'); i++; break;
            case '\'': case '"': {
                size_t close = i + 1;
                while (close < n && fmt[close] != c) { out.push_back(fmt[close]); close++; }
                if (close >= n) return false;
                i = close + 1;
                break;
            }
            case '%':
                if (i + 1 >= n || fmt[i + 1] == '%') return false;
                {
                    std::u16string part;
                    if (!tmCustom(ticks, std::u16string(1, fmt[i + 1]), part)) return false;   // one specifier on its own
                    out += part;
                }
                i += 2;
                break;
            case '\\':
                if (i + 1 >= n) return false;
                out.push_back(fmt[i + 1]);
                i += 2;
                break;
            default: out.push_back(c); i++; break;
        }
    }
    return true;
}

/// Formats with a standard format (`d`, `o`, `G`, ...: a single letter) or a custom one. False: the format is not valid.
inline bool tmFormat(int64_t ticks, std::u16string fmt, std::u16string& out) {
    if (fmt.empty()) fmt = u"G";
    if (fmt.size() == 1) {
        const char* standard = tmStandardFormat(fmt[0]);
        if (standard) fmt.assign(standard, standard + std::strlen(standard));
        else if ((fmt[0] >= 'a' && fmt[0] <= 'z') || (fmt[0] >= 'A' && fmt[0] <= 'Z') || fmt[0] == '%' || fmt[0] == '\\') return false;   // an unknown standard format
    }
    return tmCustom(ticks, fmt, out);
}

inline Value tm_format(Value ticks, Value format, OwnList* list) {
    if (ticks.i < 0 || ticks.i > TICKS_MAX) {
        char text[96];
        std::snprintf(text, sizeof text, "Point in time out of range (%lld ticks).", (long long)ticks.i);
        return timeFail(text);
    }
    const Str* f = strOf(format);
    std::u16string fmt(f->data, f->length), out;
    if (!tmFormat(ticks.i, fmt, out)) {
        std::string text = "Invalid time format '";
        for (char16_t c : fmt) text.push_back(c < 128 ? (char)c : '?');
        text += "'.";
        return timeFail(text.c_str());
    }
    Str* s = allocStr((uint32_t)out.size(), list);
    std::memcpy(strChars(s), out.data(), out.size() * sizeof(char16_t));
    return StrV(s);
}

// ---- parsing ---------------------------------------------------------------------------------------------------------------------------
struct TmTok { enum Kind { Num, Word, Sym } kind; int64_t value; int digits; std::string text; };

inline bool tmEqNoCase(const std::string& a, const char* b) {
    size_t n = std::strlen(b);
    if (a.size() != n) return false;
    for (size_t i = 0; i < n; i++) if (std::tolower((unsigned char)a[i]) != std::tolower((unsigned char)b[i])) return false;
    return true;
}

/// DateTime.Parse (invariant culture, assumed UTC): a subset of what .NET reads (see the top of the file). False: not a date.
inline bool tmParse(const Str* str, int64_t& ticks) {
    std::vector<TmTok> t;
    for (uint32_t i = 0; i < str->length;) {
        char16_t c = str->data[i];
        if (c == ' ' || c == '\t') { i++; continue; }
        if (c >= '0' && c <= '9') {
            int64_t v = 0; int digits = 0;
            while (i < str->length && str->data[i] >= '0' && str->data[i] <= '9') { if (digits < 15) v = v * 10 + (str->data[i] - '0'); digits++; i++; }
            t.push_back({TmTok::Num, v, digits, ""});
        } else if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) {
            std::string w;
            while (i < str->length && ((str->data[i] >= 'a' && str->data[i] <= 'z') || (str->data[i] >= 'A' && str->data[i] <= 'Z'))) w.push_back((char)str->data[i++]);
            t.push_back({TmTok::Word, 0, 0, w});
        } else if (c < 128) { t.push_back({TmTok::Sym, 0, 0, std::string(1, (char)c)}); i++; }
        else return false;
    }
    auto sym = [&](size_t k, char c) { return k < t.size() && t[k].kind == TmTok::Sym && t[k].text[0] == c; };
    auto num = [&](size_t k) { return k < t.size() && t[k].kind == TmTok::Num; };
    std::vector<bool> used(t.size(), false);
    int64_t hour = 0, minute = 0, second = 0, fraction = 0, offset = 0;   // fraction in ticks
    bool hasTime = false, pm = false, am = false;
    // the time: N:N[:N[.digits]]
    for (size_t k = 0; k + 2 < t.size() && !hasTime; k++) {
        if (num(k) && sym(k + 1, ':') && num(k + 2) && !used[k]) {
            hasTime = true;
            hour = t[k].value; minute = t[k + 2].value;
            used[k] = used[k + 1] = used[k + 2] = true;
            size_t e = k + 3;
            if (sym(e, ':') && num(e + 1)) {
                second = t[e + 1].value; used[e] = used[e + 1] = true; e += 2;
                if (sym(e, '.') && num(e + 1)) {
                    int64_t f = t[e + 1].value; int d = t[e + 1].digits;
                    if (d > 7) { for (int z = 7; z < d; z++) f /= 10; d = 7; }
                    for (int z = d; z < 7; z++) f *= 10;
                    fraction = f; used[e] = used[e + 1] = true; e += 2;
                }
            }
        }
    }
    // AM/PM, GMT/UTC/Z, an offset after the time
    bool hasOffset = false;
    for (size_t k = 0; k < t.size(); k++) {
        if (t[k].kind == TmTok::Word) {
            if (tmEqNoCase(t[k].text, "AM")) { am = true; used[k] = true; }
            else if (tmEqNoCase(t[k].text, "PM")) { pm = true; used[k] = true; }
            else if (tmEqNoCase(t[k].text, "Z") || tmEqNoCase(t[k].text, "GMT") || tmEqNoCase(t[k].text, "UTC")) { used[k] = true; hasOffset = true; }
            else if (tmEqNoCase(t[k].text, "T") && k > 0 && num(k - 1) && num(k + 1)) used[k] = true;   // the ISO separator
        }
    }
    if (hasTime) {
        // the sign of an offset follows the time
        size_t after = 0;
        for (size_t k = 0; k < t.size(); k++) if (used[k]) after = k + 1;
        for (size_t k = after; k + 1 < t.size(); k++) {
            if ((sym(k, '+') || sym(k, '-')) && num(k + 1) && !used[k + 1]) {
                int64_t hh, mm = 0;
                size_t e = k + 2;
                if (t[k + 1].digits == 4) { hh = t[k + 1].value / 100; mm = t[k + 1].value % 100; }
                else { hh = t[k + 1].value; if (sym(e, ':') && num(e + 1)) { mm = t[e + 1].value; used[e] = used[e + 1] = true; e += 2; } }
                offset = (hh * 3600 + mm * 60) * TICKS_PER_SECOND * (t[k].text[0] == '-' ? -1 : 1);
                used[k] = used[k + 1] = true; hasOffset = true;
                break;
            }
        }
        if (second > 59 || minute > 59) return false;
        if (am || pm) { if (hour < 1 || hour > 12) return false; hour = hour % 12 + (pm ? 12 : 0); }
        if (hour > 23) return false;
    } else if (am || pm) return false;
    (void)hasOffset;
    // the date
    int64_t year = 1, month = 1, day = 1;
    int64_t monthName = 0;
    std::vector<size_t> numbers;
    bool anyDate = false;
    for (size_t k = 0; k < t.size(); k++) {
        if (used[k]) continue;
        if (t[k].kind == TmTok::Word) {
            bool matched = false;
            for (int m = 0; m < 12 && !matched; m++)
                if (tmEqNoCase(t[k].text, TM_MONTHS[m]) || (t[k].text.size() == 3 && tmEqNoCase(t[k].text, std::string(TM_MONTHS[m]).substr(0, 3).c_str()))) { monthName = m + 1; matched = true; }
            for (int d = 0; d < 7 && !matched; d++)
                if (tmEqNoCase(t[k].text, TM_DAYS[d]) || (t[k].text.size() == 3 && tmEqNoCase(t[k].text, std::string(TM_DAYS[d]).substr(0, 3).c_str()))) matched = true;   // a day name is not checked
            if (!matched) return false;
            used[k] = true;
        } else if (t[k].kind == TmTok::Num) numbers.push_back(k);
    }
    auto twoDigitYear = [](int64_t y, int digits) { return digits <= 2 ? (y <= 49 ? 2000 + y : 1900 + y) : y; };
    if (monthName) {
        month = monthName;
        if (numbers.size() == 2) {
            size_t a = numbers[0], b = numbers[1];
            if (t[a].digits >= 3) { year = t[a].value; day = t[b].value; }
            else { day = t[a].value; year = twoDigitYear(t[b].value, t[b].digits); }
        } else if (numbers.size() == 1 && t[numbers[0]].digits >= 3) {   // `March 2024`: the first of the month
            year = t[numbers[0]].value;
        } else if (numbers.size() == 1) {
            day = t[numbers[0]].value;
            std::time_t now = (std::time_t)(plat::unixMicros() / 1000000);
            year = 1970 + (int64_t)(now / 31556952);
        } else return false;
        anyDate = true;
    } else if (numbers.size() == 3) {
        size_t a = numbers[0], b = numbers[1], c = numbers[2];
        if (t[a].digits >= 3) { year = t[a].value; month = t[b].value; day = t[c].value; }
        else { month = t[a].value; day = t[b].value; year = twoDigitYear(t[c].value, t[c].digits); }
        anyDate = true;
    } else if (numbers.size() == 2 && t[numbers[0]].digits >= 3) {   // `2024-03`: the first of the month
        year = t[numbers[0]].value; month = t[numbers[1]].value;
        anyDate = true;
    } else if (!numbers.empty()) return false;
    if (!hasTime && !anyDate) return false;
    if (year < 1 || year > 9999 || month < 1 || month > 12 || day < 1 || day > tmMonthDays(year, month)) return false;
    int64_t result = tmDays(year, month, day) * TICKS_PER_DAY + (hour * 3600 + minute * 60 + second) * TICKS_PER_SECOND + fraction - offset;
    if (result < 0 || result > TICKS_MAX) return false;
    ticks = result;
    return true;
}

inline Value tm_parse(Value text) {
    int64_t ticks;
    return tmParse(strOf(text), ticks) ? Int(ticks) : Undef();
}

}  // namespace fire
