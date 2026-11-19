/* native/forge/util.c: the context, texts, lists, exceptions (forge.h). No C# counterpart of its own: what .NET gives the C# code
 * (string, List, StringBuilder, char.IsLetter, File.ReadAllText, exceptions). */
#include "unicode.inc"

void fcBufPut(FcBuf* b, const char* s, size_t n) {
    if (b->len + n + 1 > b->cap) {
        size_t cap = b->cap ? b->cap * 2 : 256;
        while (cap < b->len + n + 1) cap *= 2;
        b->p = (char*)realloc(b->p, cap);
        if (!b->p) { fbOutOfMemory(); return; }
        b->cap = cap;
    }
    if (n) memcpy(b->p + b->len, s, n);
    b->len += n;
    b->p[b->len] = 0;
}
void fcBufStr(FcBuf* b, const char* s) { fcBufPut(b, s, strlen(s)); }
void fcBufChar(FcBuf* b, char c) { fcBufPut(b, &c, 1); }
void fcBufFmt(FcBuf* b, const char* fmt, ...) {
    char small[256];
    va_list ap;
    int n;
    va_start(ap, fmt);
    n = vsnprintf(small, sizeof small, fmt, ap);
    va_end(ap);
    if (n < 0) return;
    if ((size_t)n < sizeof small) { fcBufPut(b, small, (size_t)n); return; }
    {
        char* big = (char*)malloc((size_t)n + 1);
        if (!big) { fbOutOfMemory(); return; }
        va_start(ap, fmt);
        vsnprintf(big, (size_t)n + 1, fmt, ap);
        va_end(ap);
        fcBufPut(b, big, (size_t)n);
        free(big);
    }
}
void fcBufUnit(FcBuf* b, uint16_t c) {
    char t[3];
    if (c == 0) { t[0] = (char)0xC0; t[1] = (char)0x80; fcBufPut(b, t, 2); }
    else if (c < 0x80) fcBufChar(b, (char)c);
    else if (c < 0x800) { t[0] = (char)(0xC0 | (c >> 6)); t[1] = (char)(0x80 | (c & 0x3F)); fcBufPut(b, t, 2); }
    else { t[0] = (char)(0xE0 | (c >> 12)); t[1] = (char)(0x80 | ((c >> 6) & 0x3F)); t[2] = (char)(0x80 | (c & 0x3F)); fcBufPut(b, t, 3); }
}
void fcBufFree(FcBuf* b) { free(b->p); b->p = NULL; b->len = b->cap = 0; }

void fcU16Put(FcU16* b, const uint16_t* s, int n) {
    if (b->len + n + 1 > b->cap) {
        int cap = b->cap ? b->cap * 2 : 256;
        while (cap < b->len + n + 1) cap *= 2;
        b->p = (uint16_t*)realloc(b->p, (size_t)cap * sizeof(uint16_t));
        if (!b->p) { fbOutOfMemory(); return; }
        b->cap = cap;
    }
    if (n) memcpy(b->p + b->len, s, (size_t)n * sizeof(uint16_t));
    b->len += n;
    b->p[b->len] = 0;
}
void fcU16Unit(FcU16* b, uint16_t c) { fcU16Put(b, &c, 1); }
void fcU16Free(FcU16* b) { free(b->p); b->p = NULL; b->len = b->cap = 0; }

void fcCtxInit(FcCtx* c) {
    memset(c, 0, sizeof *c);
    c->symbols = fcListNew(c);
    c->diags = fcListNew(c);
    c->declared = fcListNew(c);
    c->customUnits = fcListNew(c);
}
void fcCtxFree(FcCtx* c) {
    fbMapFree(&c->customTable);
    fbMapFree(&c->declaredNamespaces);
    fbArenaFree(&c->arena);
}

void* fcAlloc(FcCtx* c, size_t n) { return fbAlloc(&c->arena, n); }
const char* fcStrN(FcCtx* c, const char* s, size_t n) { return fbStrDup(&c->arena, s, n); }
const char* fcStr(FcCtx* c, const char* s) { return s ? fbStrDup(&c->arena, s, strlen(s)) : NULL; }
const char* fcFmt(FcCtx* c, const char* fmt, ...) {
    FcBuf b = {0};
    va_list ap;
    int n;
    va_start(ap, fmt);
    n = vsnprintf(NULL, 0, fmt, ap);
    va_end(ap);
    if (n < 0) return "";
    {
        char* d = (char*)fcAlloc(c, (size_t)n + 1);
        va_start(ap, fmt);
        vsnprintf(d, (size_t)n + 1, fmt, ap);
        va_end(ap);
        (void)b;
        return d;
    }
}
const char* fcBufDone(FcCtx* c, FcBuf* b) {
    const char* s = fcStrN(c, b->p ? b->p : "", b->len);
    fcBufFree(b);
    return s;
}

FcList* fcListNew(FcCtx* c) { return (FcList*)fcAlloc(c, sizeof(FcList)); }
void fcListAdd(FcCtx* c, FcList* l, void* item) { fcListInsert(c, l, l->count, item); }
void fcListInsert(FcCtx* c, FcList* l, int at, void* item) {
    if (l->count == l->cap) {
        int cap = l->cap ? l->cap * 2 : 4;
        void** items = (void**)fcAlloc(c, (size_t)cap * sizeof(void*));
        if (l->count) memcpy(items, l->items, (size_t)l->count * sizeof(void*));
        l->items = items;
        l->cap = cap;
    }
    memmove(l->items + at + 1, l->items + at, (size_t)(l->count - at) * sizeof(void*));
    l->items[at] = item;
    l->count++;
}
void fcListRemoveAt(FcList* l, int at) {
    memmove(l->items + at, l->items + at + 1, (size_t)(l->count - at - 1) * sizeof(void*));
    l->count--;
}
FcList* fcListCopy(FcCtx* c, const FcList* l) {
    FcList* r = fcListNew(c);
    int i;
    if (!l) return r;
    for (i = 0; i < l->count; i++) fcListAdd(c, r, l->items[i]);
    return r;
}
bool fcListHasStr(const FcList* l, const char* s) {
    int i;
    if (!l) return false;
    for (i = 0; i < l->count; i++) if (strcmp((const char*)l->items[i], s) == 0) return true;
    return false;
}

/* ---- texts ---- */
const char* fc8FromU16(FcCtx* c, const uint16_t* u, int n) {
    FcBuf b = {0};
    int i;
    fcBufPut(&b, "", 0);
    for (i = 0; i < n; i++) fcBufUnit(&b, u[i]);
    return fcBufDone(c, &b);
}
/* the next unit of an fc8 text */
static uint16_t fc8Next(const unsigned char** s) {
    const unsigned char* p = *s;
    uint16_t u;
    if (p[0] < 0x80) { u = p[0]; *s = p + 1; }
    else if ((p[0] & 0xE0) == 0xC0 && p[1]) { u = (uint16_t)(((p[0] & 0x1F) << 6) | (p[1] & 0x3F)); *s = p + 2; }
    else if ((p[0] & 0xF0) == 0xE0 && p[1] && p[2]) { u = (uint16_t)(((p[0] & 0x0F) << 12) | ((p[1] & 0x3F) << 6) | (p[2] & 0x3F)); *s = p + 3; }
    else { u = 0xFFFD; *s = p + 1; }
    return u;
}
int fcU16FromFc8(const char* s, FcU16* out) {
    const unsigned char* p = (const unsigned char*)s;
    int n = 0;
    while (*p) { fcU16Unit(out, fc8Next(&p)); n++; }
    return n;
}
int fc8Units(const char* s) {
    const unsigned char* p = (const unsigned char*)s;
    int n = 0;
    while (*p) { fc8Next(&p); n++; }
    return n;
}
static void fcPutCodePoint(FcBuf* out, uint32_t cp) {
    char t[4];
    if (cp < 0x80) fcBufChar(out, (char)cp);
    else if (cp < 0x800) { t[0] = (char)(0xC0 | (cp >> 6)); t[1] = (char)(0x80 | (cp & 0x3F)); fcBufPut(out, t, 2); }
    else if (cp < 0x10000) { t[0] = (char)(0xE0 | (cp >> 12)); t[1] = (char)(0x80 | ((cp >> 6) & 0x3F)); t[2] = (char)(0x80 | (cp & 0x3F)); fcBufPut(out, t, 3); }
    else { t[0] = (char)(0xF0 | (cp >> 18)); t[1] = (char)(0x80 | ((cp >> 12) & 0x3F)); t[2] = (char)(0x80 | ((cp >> 6) & 0x3F)); t[3] = (char)(0x80 | (cp & 0x3F)); fcBufPut(out, t, 4); }
}
void fcOut8(FcBuf* out, const char* s) {
    const unsigned char* p = (const unsigned char*)s;
    while (*p) {
        uint16_t u = fc8Next(&p);
        if (u >= 0xD800 && u <= 0xDBFF) {
            const unsigned char* q = p;
            uint16_t lo = *q ? fc8Next(&q) : 0;
            if (lo >= 0xDC00 && lo <= 0xDFFF) { fcPutCodePoint(out, 0x10000u + (((uint32_t)u - 0xD800u) << 10) + (lo - 0xDC00u)); p = q; continue; }
            fcPutCodePoint(out, 0xFFFD);
            continue;
        }
        if (u >= 0xDC00 && u <= 0xDFFF) { fcPutCodePoint(out, 0xFFFD); continue; }
        fcPutCodePoint(out, u);
    }
}
void fcEsc(FcBuf* out, const char* s) {
    const unsigned char* p = (const unsigned char*)s;
    fcBufChar(out, '"');
    while (*p) {
        uint16_t u = fc8Next(&p);
        if (u == '\\') fcBufStr(out, "\\\\");
        else if (u == '"') fcBufStr(out, "\\\"");
        else if (u >= 0x20 && u < 0x7F) fcBufChar(out, (char)u);
        else fcBufFmt(out, "\\u%04X", u);
    }
    fcBufChar(out, '"');
}
bool fcStartsWith(const char* s, const char* prefix) { return strncmp(s, prefix, strlen(prefix)) == 0; }
bool fcEndsWith(const char* s, const char* suffix) {
    size_t n = strlen(s), m = strlen(suffix);
    return n >= m && memcmp(s + n - m, suffix, m) == 0;
}

static bool fcInRanges(const uint16_t (*r)[2], int n, uint16_t c) {
    int lo = 0, hi = n - 1;
    while (lo <= hi) {
        int mid = (lo + hi) / 2;
        if (c < r[mid][0]) hi = mid - 1;
        else if (c > r[mid][1]) lo = mid + 1;
        else return true;
    }
    return false;
}
bool fcIsLetter(uint16_t c) {
    if (c < 0x80) return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
    return fcInRanges(fcLetterRanges, (int)(sizeof fcLetterRanges / sizeof fcLetterRanges[0]), c);
}
bool fcIsDigit(uint16_t c) {
    if (c < 0x80) return c >= '0' && c <= '9';
    return fcInRanges(fcDigitRanges, (int)(sizeof fcDigitRanges / sizeof fcDigitRanges[0]), c);
}
bool fcIsLetterOrDigit(uint16_t c) { return fcIsLetter(c) || fcIsDigit(c); }
bool fcIsWhiteSpace(uint16_t c) {
    return c == ' ' || (c >= 0x09 && c <= 0x0D) || c == 0x85 || c == 0xA0 || c == 0x1680 || (c >= 0x2000 && c <= 0x200A)
        || c == 0x2028 || c == 0x2029 || c == 0x202F || c == 0x205F || c == 0x3000;
}

bool fcReadText(const char* path, FcU16* out) {
    size_t n = 0, i = 0;
    uint8_t* b = fbReadFile(path, &n);
    if (!b) return false;
    fcU16Put(out, NULL, 0);
    if (n >= 2 && ((b[0] == 0xFF && b[1] == 0xFE) || (b[0] == 0xFE && b[1] == 0xFF))) {
        bool le = b[0] == 0xFF;
        for (i = 2; i + 1 < n; i += 2) fcU16Unit(out, le ? (uint16_t)(b[i] | (b[i + 1] << 8)) : (uint16_t)((b[i] << 8) | b[i + 1]));
        free(b);
        return true;
    }
    if (n >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) i = 3;
    while (i < n) {
        uint8_t c = b[i];
        uint32_t cp;
        int len, k;
        if (c < 0x80) { fcU16Unit(out, c); i++; continue; }
        if ((c & 0xE0) == 0xC0 && c >= 0xC2) { len = 2; cp = c & 0x1F; }
        else if ((c & 0xF0) == 0xE0) { len = 3; cp = c & 0x0F; }
        else if ((c & 0xF8) == 0xF0 && c <= 0xF4) { len = 4; cp = c & 0x07; }
        else { fcU16Unit(out, 0xFFFD); i++; continue; }
        for (k = 1; k < len; k++) {
            if (i + (size_t)k >= n || (b[i + k] & 0xC0) != 0x80) break;
            cp = (cp << 6) | (b[i + k] & 0x3F);
        }
        if (k < len || (len == 3 && (cp < 0x800 || (cp >= 0xD800 && cp <= 0xDFFF))) || (len == 4 && (cp < 0x10000 || cp > 0x10FFFF))) {
            fcU16Unit(out, 0xFFFD);
            i += (size_t)(k > 1 ? k : 1);
            continue;
        }
        if (cp >= 0x10000) { cp -= 0x10000; fcU16Unit(out, (uint16_t)(0xD800 + (cp >> 10))); fcU16Unit(out, (uint16_t)(0xDC00 + (cp & 0x3FF))); }
        else fcU16Unit(out, (uint16_t)cp);
        i += (size_t)len;
    }
    free(b);
    return true;
}

/* ---- exceptions ---- */
void fcRethrow(FcCtx* c) {
    if (!c->tryTop) { fprintf(stderr, "forge-c: an exception without a handler: %s\n", c->err.message ? c->err.message : "?"); abort(); }
    longjmp(c->tryTop->jb, 1);
}
void fcThrow(FcCtx* c, FcErrKind kind, int line, int column, const char* file, const char* message) {
    c->err.kind = kind;
    c->err.line = line;
    c->err.column = column;
    c->err.file = file;
    c->err.message = message;
    fcRethrow(c);
}
void fcThrowF(FcCtx* c, FcErrKind kind, int line, int column, const char* file, const char* fmt, ...) {
    FcBuf b = {0};
    va_list ap;
    char* big;
    int n;
    va_start(ap, fmt);
    n = vsnprintf(NULL, 0, fmt, ap);
    va_end(ap);
    big = (char*)fcAlloc(c, (size_t)(n > 0 ? n : 0) + 1);
    va_start(ap, fmt);
    vsnprintf(big, (size_t)(n > 0 ? n : 0) + 1, fmt, ap);
    va_end(ap);
    (void)b;
    fcThrow(c, kind, line, column, file, big);
}
