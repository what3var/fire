/* fire native bridge "time" (C): the natives behind `#import "time"` (SPEC 8.15) - `Sleep` and what DateTime and TimeSpan are written with
 * (src/fire/Standard/TimePrelude.cs).
 *
 * This file is the one implementation of the time functions, in C99 ("language": "c"): the native build puts it into the generated program, and the virtual
 * machine runs it in a shared library built from it (native/abi/fire_pkg_abi.h). It uses the runtime and, from the platform layer,
 *   fire_plat_unixMicros()     the wall clock: microseconds since 1970-01-01 UTC
 * and the C library for the time zone (localtime_r; define FIRE_NO_LOCALTIME on a board without a time zone database: local time is then UTC).
 * `Sleep` is only compiled in the native build (it uses the runtime's sleepTicks); the VM waits by itself.
 * Time is counted in ticks of 100 ns; a DateTime counts from 0001-01-01 (like .NET). DateTime.Parse reads a subset of the formats .NET knows: ISO dates and
 * times (`2024-03-15 14:30:00`, `2024-03-15T14:30:00.5Z`, `+02:00`), `M/d/yyyy`, month names (`March 15, 2024`, `March 2024`, `Fri, 15 Mar 2024 14:30:00 GMT`),
 * `yyyy-MM`, `3:45 PM`. */
#ifndef FIRE_BRIDGE_TIME_H
#define FIRE_BRIDGE_TIME_H

#include <ctype.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#define FIRE_TICKS_PER_SECOND ((int64_t)10000000)
#define FIRE_TICKS_PER_DAY ((int64_t)864000000000LL)
#define FIRE_TICKS_UNIX_EPOCH ((int64_t)621355968000000000LL)   /* 1970-01-01 */
#define FIRE_TICKS_MAX ((int64_t)3155378975999999999LL)         /* 9999-12-31 23:59:59.9999999 */

/* A failed time function: a TimeException (the prelude class), or the end of the program when the program has no exceptions. */
static Value fire_tm_fail(const char* text) { return fireError("TimeException", text); }

static Value fire_tm_ascii(const char* text, size_t n, OwnList* list) {
    Str* s = allocStr((uint32_t)n, list);
    widenAscii(text, (uint32_t)n, strChars(s));
    return StrV(s);
}

static double fire_tm_num(Value v) { return v.kind == K_Float ? (double)v.f : v.kind == K_Int ? (double)v.i : 0.0; }
static int64_t fire_tm_round(double x) { return (int64_t)nearbyint(x); }   /* half to even, like Math.Round */

/* ---- Sleep and units --------------------------------------------------------------------------------------------------------------------
 * Only in the native build: in the virtual machine `Sleep` and `__time_unit_ticks` are run by the VM itself (the `host` functions of the package: a wait has to be
 * abortable, and the units of a value do not cross the ABI), the library has no part of them. */
#ifndef FIRE_LIBRARY
/* A value with a unit of time (`500ms`, `2s`) in ticks, otherwise undefined. */
static Value fire_tm_unitTicks(Value v) {
    double perSecond;
    if (!(v.kind == K_Int || v.kind == K_Float) || unitIsUnitless(v.unit)) return Undef();
    if (!unitOfTime(v.unit, &perSecond)) {
        char text[160];
        snprintf(text, sizeof text, "'%s' is not a unit of time.", g_ud[v.unit].name);
        return fire_tm_fail(text);
    }
    return Int(fire_tm_round(fire_tm_num(v) * perSecond * 1e7));
}

/* `Sleep(time)`: the runtime puts the thread to sleep (not deaf: terminate ends it, the main program keeps serving the queue). */
static Value fire_tm_sleep(Value a) {
    int64_t ticks;
    char text[160];
    if (timeTicksOf(a, &ticks)) { sleepTicks(ticks); return Undef(); }
    if (a.kind == K_Class) snprintf(text, sizeof text, "Sleep expects a TimeSpan, got: %s.", className(asObj(a)->cls));
    else if (a.kind == K_Int || a.kind == K_Float) snprintf(text, sizeof text, "'%s' is not a unit of time.", g_ud[a.unit].name);
    else snprintf(text, sizeof text, "Sleep expects a TimeSpan, a time value or milliseconds, got: %s.", kindName(a));
    return fire_tm_fail(text);
}
#endif

/* ---- the calendar (proleptic Gregorian, like .NET) ------------------------------------------------------------------------------------- */
/* Days since 0001-01-01 of a date. */
static int64_t fire_tm_days(int64_t y, int64_t m, int64_t d) {
    int64_t era, yoe, doy, doe;
    y -= m <= 2;
    era = (y >= 0 ? y : y - 399) / 400;
    yoe = y - era * 400;
    doy = (153 * (m + (m > 2 ? -3 : 9)) + 2) / 5 + d - 1;
    doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    return era * 146097 + doe - 719468 + 719162;   /* days since 1970-01-01, shifted to 0001-01-01 */
}
static void fire_tm_civil(int64_t days, int64_t* y, int64_t* m, int64_t* d) {
    int64_t z = days - 719162 + 719468;
    int64_t era = (z >= 0 ? z : z - 146096) / 146097;
    int64_t doe = z - era * 146097;
    int64_t yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
    int64_t doy, mp;
    *y = yoe + era * 400;
    doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    mp = (5 * doy + 2) / 153;
    *d = doy - (153 * mp + 2) / 5 + 1;
    *m = mp < 10 ? mp + 3 : mp - 9;
    *y += *m <= 2;
}
static int fire_tm_leap(int64_t y) { return (y % 4 == 0 && y % 100 != 0) || y % 400 == 0; }
static int64_t fire_tm_monthDays(int64_t y, int64_t m) {
    static const int days[] = {31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31};
    return m == 2 && fire_tm_leap(y) ? 29 : days[m - 1];
}

typedef struct FireTmParts { int64_t year, month, day, hour, minute, second, tick, weekday, dayOfYear; } FireTmParts;
static FireTmParts fire_tm_split(int64_t ticks) {
    FireTmParts p;
    int64_t days = ticks / FIRE_TICKS_PER_DAY, rest = ticks % FIRE_TICKS_PER_DAY;
    fire_tm_civil(days, &p.year, &p.month, &p.day);
    p.hour = rest / (3600 * FIRE_TICKS_PER_SECOND);
    p.minute = rest / (60 * FIRE_TICKS_PER_SECOND) % 60;
    p.second = rest / FIRE_TICKS_PER_SECOND % 60;
    p.tick = rest % FIRE_TICKS_PER_SECOND;   /* 100 ns */
    p.weekday = (days + 1) % 7;              /* 0001-01-01 was a Monday; 0 = Sunday */
    p.dayOfYear = days - fire_tm_days(p.year, 1, 1) + 1;
    return p;
}

/* ---- the natives ----------------------------------------------------------------------------------------------------------------------- */
static Value fire_tm_now(void) { return Int(FIRE_TICKS_UNIX_EPOCH + fire_plat_unixMicros() * 10); }

/* The offset of the local time zone to UTC at a point in time (UTC ticks), in ticks. */
static Value fire_tm_localOffset(Value utc) {
#ifdef FIRE_NO_LOCALTIME
    (void)utc;
    return Int(0);
#else
    int64_t ticks = utc.i < 0 ? 0 : utc.i > FIRE_TICKS_MAX ? FIRE_TICKS_MAX : utc.i;
    int64_t unixSeconds = (ticks - FIRE_TICKS_UNIX_EPOCH) / FIRE_TICKS_PER_SECOND;
    int64_t local;
    time_t t;
    struct tm lt;
    if (ticks < FIRE_TICKS_UNIX_EPOCH && (ticks - FIRE_TICKS_UNIX_EPOCH) % FIRE_TICKS_PER_SECOND != 0) unixSeconds--;
    t = (time_t)unixSeconds;
    if ((int64_t)t != unixSeconds) return Int(0);
#ifdef _WIN32
    if (localtime_s(&lt, &t) != 0) return Int(0);
#elif defined(__WATCOMC__)
    if (!_localtime(&t, &lt)) return Int(0);   /* (Open Watcom: no localtime_r - an undeclared one returned an int and cut the far pointer) */
#else
    if (!localtime_r(&t, &lt)) return Int(0);
#endif
    local = (fire_tm_days(lt.tm_year + 1900, lt.tm_mon + 1, lt.tm_mday) - 719162) * 86400 + lt.tm_hour * 3600 + lt.tm_min * 60 + lt.tm_sec;
    return Int((local - unixSeconds) * FIRE_TICKS_PER_SECOND);
#endif
}

static Value fire_tm_outOfRange(int64_t ticks) {
    char text[96];
    snprintf(text, sizeof text, "Point in time out of range (%lld ticks).", (long long)ticks);
    return fire_tm_fail(text);
}

/* [year, month, day, hour, minute, second, millisecond, weekday (0 = Sunday), day of the year] */
static Value fire_tm_parts(Value ticks, OwnList* list) {
    FireTmParts p;
    Arr* a;
    int64_t v[9];
    int i;
    if (ticks.i < 0 || ticks.i > FIRE_TICKS_MAX) return fire_tm_outOfRange(ticks.i);
    p = fire_tm_split(ticks.i);
    a = allocArr(9, list);
    v[0] = p.year; v[1] = p.month; v[2] = p.day; v[3] = p.hour; v[4] = p.minute; v[5] = p.second; v[6] = p.tick / TICKS_PER_MS; v[7] = p.weekday; v[8] = p.dayOfYear;
    for (i = 0; i < 9; i++) arrItems(a)[i] = Int(v[i]);
    return ArrV(a);
}

static Value fire_tm_make(Value y, Value mo, Value d, Value h, Value mi, Value s, Value ms) {
    if (y.i < 1 || y.i > 9999 || mo.i < 1 || mo.i > 12 || d.i < 1 || d.i > fire_tm_monthDays(y.i, mo.i) || h.i < 0 || h.i > 23 || mi.i < 0 || mi.i > 59 || s.i < 0 || s.i > 59 || ms.i < 0 || ms.i > 999) {
        char text[160];
        snprintf(text, sizeof text, "Invalid date/time: %lld-%lld-%lld %lld:%lld:%lld.%lld", (long long)y.i, (long long)mo.i, (long long)d.i, (long long)h.i, (long long)mi.i, (long long)s.i, (long long)ms.i);
        return fire_tm_fail(text);
    }
    return Int(fire_tm_days(y.i, mo.i, d.i) * FIRE_TICKS_PER_DAY + (h.i * 3600 + mi.i * 60 + s.i) * FIRE_TICKS_PER_SECOND + ms.i * TICKS_PER_MS);
}

static Value fire_tm_daysInMonth(Value year, Value month) {
    if (year.i < 1 || year.i > 9999 || month.i < 1 || month.i > 12) {
        char text[96];
        snprintf(text, sizeof text, "Invalid month %lld-%lld.", (long long)year.i, (long long)month.i);
        return fire_tm_fail(text);
    }
    return Int(fire_tm_monthDays(year.i, month.i));
}

static Value fire_tm_addMonths(Value ticks, Value months) {
    FireTmParts p;
    int64_t total, y, m, d;
    if (ticks.i < 0 || ticks.i > FIRE_TICKS_MAX) return fire_tm_fail("The result is outside the valid time range.");
    p = fire_tm_split(ticks.i);
    total = p.year * 12 + (p.month - 1) + months.i;
    y = total >= 0 ? total / 12 : -((-total + 11) / 12);
    m = total - y * 12 + 1;
    if (y < 1 || y > 9999) return fire_tm_fail("The result is outside the valid time range.");
    d = p.day < fire_tm_monthDays(y, m) ? p.day : fire_tm_monthDays(y, m);
    return Int(fire_tm_days(y, m, d) * FIRE_TICKS_PER_DAY + ticks.i % FIRE_TICKS_PER_DAY);
}

static Value fire_tm_toTicks(Value a, Value b) { return Int(fire_tm_round(fire_tm_num(a) * fire_tm_num(b))); }

/* `[-][d.]hh:mm:ss[.fffffff]` */
static Value fire_tm_spanText(Value ticks, OwnList* list) {
    uint64_t t = ticks.i < 0 ? (uint64_t)0 - (uint64_t)ticks.i : (uint64_t)ticks.i;
    uint64_t days = t / (uint64_t)FIRE_TICKS_PER_DAY, rest = t % (uint64_t)FIRE_TICKS_PER_DAY, frac;
    char buf[64];
    int n = 0;
    if (ticks.i < 0) buf[n++] = '-';
    if (days) n += snprintf(buf + n, sizeof buf - n, "%llu.", (unsigned long long)days);
    n += snprintf(buf + n, sizeof buf - n, "%02llu:%02llu:%02llu", (unsigned long long)(rest / (3600ULL * 10000000)), (unsigned long long)(rest / (60ULL * 10000000) % 60), (unsigned long long)(rest / 10000000 % 60));
    frac = rest % 10000000;
    if (frac) n += snprintf(buf + n, sizeof buf - n, ".%07llu", (unsigned long long)frac);
    return fire_tm_ascii(buf, (size_t)n, list);
}

/* ---- formatting (the .NET format strings, invariant culture) --------------------------------------------------------------------------- */
static const char* const FIRE_TM_MONTHS[] = {"January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"};
static const char* const FIRE_TM_DAYS[] = {"Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"};

static const char* fire_tm_standardFormat(fire_char16 c) {
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
        default: return NULL;
    }
}

/* a growing UTF-16 text (malloc) */
typedef struct FireTmText { fire_char16* data; size_t length, cap; } FireTmText;
static void fire_tm_putc(FireTmText* t, fire_char16 c) {
    if (t->length == t->cap) {
        size_t cap = t->cap ? t->cap * 2 : 64;
        fire_char16* grown = (fire_char16*)realloc(t->data, cap * sizeof(fire_char16));
        if (grown == NULL) return;
        t->data = grown;
        t->cap = cap;
    }
    t->data[t->length++] = c;
}
static void fire_tm_puts(FireTmText* t, const char* s, size_t n) { size_t i; for (i = 0; i < n && s[i]; i++) fire_tm_putc(t, (fire_char16)(unsigned char)s[i]); }
static void fire_tm_number(FireTmText* t, int64_t v, int width) {
    char buf[32];
    int n = snprintf(buf, sizeof buf, "%0*lld", width, (long long)v);
    fire_tm_puts(t, buf, (size_t)n);
}

/* Formats a point in time with a custom format string (`yyyy-MM-dd HH:mm`). 0: the format is not valid. */
static int fire_tm_custom(int64_t ticks, const fire_char16* fmt, size_t n, FireTmText* out) {
    FireTmParts p = fire_tm_split(ticks);
    size_t i = 0, k;
    while (i < n) {
        fire_char16 c = fmt[i];
        k = 1;
        if (c == 'd' || c == 'f' || c == 'F' || c == 'g' || c == 'h' || c == 'H' || c == 'K' || c == 'm' || c == 'M' || c == 's' || c == 't' || c == 'y' || c == 'z')
            while (i + k < n && fmt[i + k] == c) k++;
        switch (c) {
            case 'd':
                if (k == 1) fire_tm_number(out, p.day, 1);
                else if (k == 2) fire_tm_number(out, p.day, 2);
                else if (k == 3) fire_tm_puts(out, FIRE_TM_DAYS[p.weekday], 3);
                else fire_tm_puts(out, FIRE_TM_DAYS[p.weekday], 64);
                i += k;
                break;
            case 'f': case 'F': {
                char buf[16];
                size_t len = k;
                if (k > 7) return 0;
                snprintf(buf, sizeof buf, "%07lld", (long long)p.tick);
                if (c == 'F') {
                    while (len > 0 && buf[len - 1] == '0') len--;
                    if (len == 0 && out->length > 0 && out->data[out->length - 1] == '.') out->length--;
                }
                fire_tm_puts(out, buf, len);
                i += k;
                break;
            }
            case 'g': fire_tm_puts(out, "A.D.", 4); i += k; break;
            case 'h': fire_tm_number(out, p.hour % 12 == 0 ? 12 : p.hour % 12, k > 2 ? 2 : (int)k); i += k; break;
            case 'H': fire_tm_number(out, p.hour, k > 2 ? 2 : (int)k); i += k; break;
            case 'K': i += k; break;   /* the kind of the time is not known here (unspecified): nothing */
            case 'm': fire_tm_number(out, p.minute, k > 2 ? 2 : (int)k); i += k; break;
            case 'M':
                if (k <= 2) fire_tm_number(out, p.month, (int)k);
                else if (k == 3) fire_tm_puts(out, FIRE_TM_MONTHS[p.month - 1], 3);
                else fire_tm_puts(out, FIRE_TM_MONTHS[p.month - 1], 64);
                i += k;
                break;
            case 's': fire_tm_number(out, p.second, k > 2 ? 2 : (int)k); i += k; break;
            case 't':
                if (k == 1) fire_tm_puts(out, p.hour < 12 ? "A" : "P", 1);
                else fire_tm_puts(out, p.hour < 12 ? "AM" : "PM", 2);
                i += k;
                break;
            case 'y': fire_tm_number(out, k <= 2 ? p.year % 100 : p.year, (int)k); i += k; break;
            case 'z': {
                int64_t offset = fire_tm_localOffset(Int(ticks)).i / FIRE_TICKS_PER_SECOND;   /* the time is read as local time: the offset at that moment */
                int64_t a = offset < 0 ? -offset : offset;
                fire_tm_puts(out, offset < 0 ? "-" : "+", 1);
                fire_tm_number(out, a / 3600, k == 1 ? 1 : 2);
                if (k >= 3) { fire_tm_puts(out, ":", 1); fire_tm_number(out, a / 60 % 60, 2); }
                i += k;
                break;
            }
            case ':': fire_tm_putc(out, ':'); i++; break;
            case '/': fire_tm_putc(out, '/'); i++; break;
            case '\'': case '"': {
                size_t close = i + 1;
                while (close < n && fmt[close] != c) { fire_tm_putc(out, fmt[close]); close++; }
                if (close >= n) return 0;
                i = close + 1;
                break;
            }
            case '%':
                if (i + 1 >= n || fmt[i + 1] == '%') return 0;
                if (!fire_tm_custom(ticks, &fmt[i + 1], 1, out)) return 0;   /* one specifier on its own */
                i += 2;
                break;
            case '\\':
                if (i + 1 >= n) return 0;
                fire_tm_putc(out, fmt[i + 1]);
                i += 2;
                break;
            default: fire_tm_putc(out, c); i++; break;
        }
    }
    return 1;
}

/* Formats with a standard format (`d`, `o`, `G`, ...: a single letter) or a custom one. 0: the format is not valid. */
static int fire_tm_formatText(int64_t ticks, const fire_char16* fmt, size_t n, FireTmText* out) {
    fire_char16 standard16[64];
    const char* standard;
    if (n == 0) { static const fire_char16 g[] = {'G'}; fmt = g; n = 1; }
    if (n == 1) {
        standard = fire_tm_standardFormat(fmt[0]);
        if (standard) {
            size_t k;
            for (k = 0; standard[k] && k < 63; k++) standard16[k] = (fire_char16)(unsigned char)standard[k];
            return fire_tm_custom(ticks, standard16, k, out);
        }
        if ((fmt[0] >= 'a' && fmt[0] <= 'z') || (fmt[0] >= 'A' && fmt[0] <= 'Z') || fmt[0] == '%' || fmt[0] == '\\') return 0;   /* an unknown standard format */
    }
    return fire_tm_custom(ticks, fmt, n, out);
}

static Value fire_tm_format(Value ticks, Value format, OwnList* list) {
    const Str* f;
    FireTmText out = {NULL, 0, 0};
    Str* s;
    if (ticks.i < 0 || ticks.i > FIRE_TICKS_MAX) return fire_tm_outOfRange(ticks.i);
    f = strOf(format);
    if (!fire_tm_formatText(ticks.i, f->data, f->length, &out)) {
        char text[176];
        size_t n = 0, i;
        free(out.data);
        n += (size_t)snprintf(text, sizeof text, "Invalid time format '");
        for (i = 0; i < f->length && n + 4 < sizeof text; i++) text[n++] = f->data[i] < 128 ? (char)f->data[i] : '?';
        snprintf(text + n, sizeof text - n, "'.");
        return fire_tm_fail(text);
    }
    s = allocStr((uint32_t)out.length, list);
    if (out.length > 0) memcpy(strChars(s), out.data, out.length * sizeof(fire_char16));
    free(out.data);
    return StrV(s);
}

/* ---- parsing ----------------------------------------------------------------------------------------------------------------------------- */
enum { FIRE_TM_NUM, FIRE_TM_WORD, FIRE_TM_SYM };
typedef struct FireTmTok { int kind; int64_t value; int digits; char text[16]; } FireTmTok;
#define FIRE_TM_MAX_TOKENS 64

static int fire_tm_eqNoCase(const char* a, const char* b, size_t n) {
    size_t i;
    if (strlen(a) != n) return 0;
    for (i = 0; i < n; i++) if (tolower((unsigned char)a[i]) != tolower((unsigned char)b[i])) return 0;
    return 1;
}
static int fire_tm_is(const char* a, const char* b) { return fire_tm_eqNoCase(a, b, strlen(b)); }

/* DateTime.Parse (invariant culture, assumed UTC): a subset of what .NET reads (see the top of the file). 0: not a date. */
static int fire_tm_parseText(const Str* str, int64_t* ticksOut) {
    FireTmTok t[FIRE_TM_MAX_TOKENS];
    int used[FIRE_TM_MAX_TOKENS];
    size_t numbers[FIRE_TM_MAX_TOKENS];
    size_t count = 0, numberCount = 0, k;
    uint32_t i;
    int64_t hour = 0, minute = 0, second = 0, fraction = 0, offset = 0;   /* fraction in ticks */
    int hasTime = 0, pm = 0, am = 0, anyDate = 0;
    int64_t year = 1, month = 1, day = 1, monthName = 0, result;
#define FIRE_TM_SYMAT(k, c) ((k) < count && t[k].kind == FIRE_TM_SYM && t[k].text[0] == (c))
#define FIRE_TM_NUMAT(k) ((k) < count && t[k].kind == FIRE_TM_NUM)
    for (i = 0; i < str->length;) {
        fire_char16 c = str->data[i];
        if (count == FIRE_TM_MAX_TOKENS) return 0;
        memset(&t[count], 0, sizeof t[count]);
        if (c == ' ' || c == '\t') { i++; continue; }
        if (c >= '0' && c <= '9') {
            int64_t v = 0; int digits = 0;
            while (i < str->length && str->data[i] >= '0' && str->data[i] <= '9') { if (digits < 15) v = v * 10 + (str->data[i] - '0'); digits++; i++; }
            t[count].kind = FIRE_TM_NUM; t[count].value = v; t[count].digits = digits; count++;
        } else if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) {
            size_t n = 0;
            t[count].kind = FIRE_TM_WORD;
            while (i < str->length && ((str->data[i] >= 'a' && str->data[i] <= 'z') || (str->data[i] >= 'A' && str->data[i] <= 'Z'))) {
                if (n + 1 < sizeof t[count].text) t[count].text[n++] = (char)str->data[i];
                else return 0;   /* (no word of a date is that long) */
                i++;
            }
            count++;
        } else if (c < 128) { t[count].kind = FIRE_TM_SYM; t[count].text[0] = (char)c; count++; i++; }
        else return 0;
    }
    memset(used, 0, sizeof used);
    /* the time: N:N[:N[.digits]] */
    for (k = 0; k + 2 < count && !hasTime; k++) {
        if (FIRE_TM_NUMAT(k) && FIRE_TM_SYMAT(k + 1, ':') && FIRE_TM_NUMAT(k + 2) && !used[k]) {
            size_t e = k + 3;
            hasTime = 1;
            hour = t[k].value; minute = t[k + 2].value;
            used[k] = used[k + 1] = used[k + 2] = 1;
            if (FIRE_TM_SYMAT(e, ':') && FIRE_TM_NUMAT(e + 1)) {
                second = t[e + 1].value; used[e] = used[e + 1] = 1; e += 2;
                if (FIRE_TM_SYMAT(e, '.') && FIRE_TM_NUMAT(e + 1)) {
                    int64_t f = t[e + 1].value; int d = t[e + 1].digits, z;
                    if (d > 7) { for (z = 7; z < d; z++) f /= 10; d = 7; }
                    for (z = d; z < 7; z++) f *= 10;
                    fraction = f; used[e] = used[e + 1] = 1; e += 2;
                }
            }
        }
    }
    /* AM/PM, GMT/UTC/Z, an offset after the time */
    for (k = 0; k < count; k++) {
        if (t[k].kind != FIRE_TM_WORD) continue;
        if (fire_tm_is(t[k].text, "AM")) { am = 1; used[k] = 1; }
        else if (fire_tm_is(t[k].text, "PM")) { pm = 1; used[k] = 1; }
        else if (fire_tm_is(t[k].text, "Z") || fire_tm_is(t[k].text, "GMT") || fire_tm_is(t[k].text, "UTC")) used[k] = 1;
        else if (fire_tm_is(t[k].text, "T") && k > 0 && FIRE_TM_NUMAT(k - 1) && FIRE_TM_NUMAT(k + 1)) used[k] = 1;   /* the ISO separator */
    }
    if (hasTime) {
        /* the sign of an offset follows the time */
        size_t after = 0;
        for (k = 0; k < count; k++) if (used[k]) after = k + 1;
        for (k = after; k + 1 < count; k++) {
            if ((FIRE_TM_SYMAT(k, '+') || FIRE_TM_SYMAT(k, '-')) && FIRE_TM_NUMAT(k + 1) && !used[k + 1]) {
                int64_t hh, mm = 0;
                size_t e = k + 2;
                if (t[k + 1].digits == 4) { hh = t[k + 1].value / 100; mm = t[k + 1].value % 100; }
                else { hh = t[k + 1].value; if (FIRE_TM_SYMAT(e, ':') && FIRE_TM_NUMAT(e + 1)) { mm = t[e + 1].value; used[e] = used[e + 1] = 1; } }
                offset = (hh * 3600 + mm * 60) * FIRE_TICKS_PER_SECOND * (t[k].text[0] == '-' ? -1 : 1);
                used[k] = used[k + 1] = 1;
                break;
            }
        }
        if (second > 59 || minute > 59) return 0;
        if (am || pm) { if (hour < 1 || hour > 12) return 0; hour = hour % 12 + (pm ? 12 : 0); }
        if (hour > 23) return 0;
    } else if (am || pm) return 0;
    /* the date */
    for (k = 0; k < count; k++) {
        if (used[k]) continue;
        if (t[k].kind == FIRE_TM_WORD) {
            int matched = 0, m, d;
            size_t len = strlen(t[k].text);
            for (m = 0; m < 12 && !matched; m++)
                if (fire_tm_is(t[k].text, FIRE_TM_MONTHS[m]) || (len == 3 && fire_tm_eqNoCase(t[k].text, FIRE_TM_MONTHS[m], 3))) { monthName = m + 1; matched = 1; }
            for (d = 0; d < 7 && !matched; d++)
                if (fire_tm_is(t[k].text, FIRE_TM_DAYS[d]) || (len == 3 && fire_tm_eqNoCase(t[k].text, FIRE_TM_DAYS[d], 3))) matched = 1;   /* a day name is not checked */
            if (!matched) return 0;
            used[k] = 1;
        } else if (t[k].kind == FIRE_TM_NUM) numbers[numberCount++] = k;
    }
#define FIRE_TM_YEAR2(y, digits) ((digits) <= 2 ? ((y) <= 49 ? 2000 + (y) : 1900 + (y)) : (y))
    if (monthName) {
        month = monthName;
        if (numberCount == 2) {
            size_t a = numbers[0], b = numbers[1];
            if (t[a].digits >= 3) { year = t[a].value; day = t[b].value; }
            else { day = t[a].value; year = FIRE_TM_YEAR2(t[b].value, t[b].digits); }
        } else if (numberCount == 1 && t[numbers[0]].digits >= 3) {   /* `March 2024`: the first of the month */
            year = t[numbers[0]].value;
        } else if (numberCount == 1) {
            time_t now = (time_t)(fire_plat_unixMicros() / 1000000);
            day = t[numbers[0]].value;
            year = 1970 + (int64_t)(now / 31556952);
        } else return 0;
        anyDate = 1;
    } else if (numberCount == 3) {
        size_t a = numbers[0], b = numbers[1], c = numbers[2];
        if (t[a].digits >= 3) { year = t[a].value; month = t[b].value; day = t[c].value; }
        else { month = t[a].value; day = t[b].value; year = FIRE_TM_YEAR2(t[c].value, t[c].digits); }
        anyDate = 1;
    } else if (numberCount == 2 && t[numbers[0]].digits >= 3) {   /* `2024-03`: the first of the month */
        year = t[numbers[0]].value; month = t[numbers[1]].value;
        anyDate = 1;
    } else if (numberCount > 0) return 0;
#undef FIRE_TM_YEAR2
#undef FIRE_TM_SYMAT
#undef FIRE_TM_NUMAT
    if (!hasTime && !anyDate) return 0;
    if (year < 1 || year > 9999 || month < 1 || month > 12 || day < 1 || day > fire_tm_monthDays(year, month)) return 0;
    result = fire_tm_days(year, month, day) * FIRE_TICKS_PER_DAY + (hour * 3600 + minute * 60 + second) * FIRE_TICKS_PER_SECOND + fraction - offset;
    if (result < 0 || result > FIRE_TICKS_MAX) return 0;
    *ticksOut = result;
    return 1;
}

static Value fire_tm_parse(Value text) {
    int64_t ticks;
    return fire_tm_parseText(strOf(text), &ticks) ? Int(ticks) : Undef();
}

#endif
