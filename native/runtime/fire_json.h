/* JSON (docs/JSON.md, `#import "json"`) for native programs, in C: the tables of the program's classes that the engine reads (the program defines them and the
 * functions declared below: generated, after this file) and the engine itself (FIRE_JSON_IMPLEMENTATION). The program includes this file after fire_rt.h. */
#ifndef FIRE_JSON_H
#define FIRE_JSON_H

#include <errno.h>

FIRE_BEGIN

/* A field as JSON sees it (JsonMethods.JsonField): the name in the JSON text, the slot in the object, the declared type (`int`, `Point`, `List<Point>`, `$0`, "" for
 * an untyped one) and the unit it is declared with (0: none). */
typedef struct JsonFieldInfo { const char* name; uint32_t slot; const char* type; uint32_t unit; } JsonFieldInfo;
/* A class: its name (as the program knows it, `Box`1` for a generic one whose plain name is taken), the plain name, its fields, `flags` (1 actor, 2 has
 * OnDeserialized(), 4 the List of the prelude, 8 JsonObject, 16 not serializable: `[NotSerializable]` or `"serializable": false` in its package) and the number of its
 * type parameters. */
typedef struct JsonClassInfo { const char* name; const char* plain; uint32_t nfields; const JsonFieldInfo* fields; uint32_t njson; uint8_t flags; uint8_t ntparams; } JsonClassInfo;
/* The slots of the classes JSON fills itself (-1: not in the program). */
typedef struct JsonSpecial { int32_t listCls, listItems, listCount; int32_t objCls, objKeys, objValues, objCount; } JsonSpecial;

/* Generated (the program): the class with this id (null: none), the id of a class name (-1: none), the text of a type argument (null: not known), the type
 * argument of a text (null: none), the special slots. */
const JsonClassInfo* jsonClassAt(uint32_t cls);
int32_t jsonClassNamed(const char* name);
/* A class of the program that is not serializable and not in the tables (the native build left it out): its name, null for any other name. */
const char* jsonExcludedNamed(const char* name);
const char* jsonTArgText(const TArg* t);
const TArg* jsonTArgFor(const char* text);
const JsonSpecial* jsonSpecial(void);

/* The engine (below, in the program): `__JsonCall(id, ...)` - 1 Serialize(value, flags), 2 Deserialize(text[, typeName]) -> [value, objects with
 * OnDeserialized], 3 Parse(text). `callType` is the type argument of the generic method that calls it (`Json.Deserialize<Person>`), null when there is none. */
Value fire_jsonCall(int argc, Value op, Value a1, Value a2, const char* callType, OwnList* list);

/* ==== The engine: the same as fire.Standard.JsonMethods of the VM - the same text, the same errors. In the program (C) that defines FIRE_JSON_IMPLEMENTATION
 * before it includes this file. Errors are not exceptions while it works: the first one is kept in the context, every step returns early once there is one,
 * and the end of the call throws it (a JsonException, or what the error says). Everything the engine makes for itself - texts, lists, the parsed tree - comes
 * from the arena of the call and is freed at its end. ==== */
#ifdef FIRE_JSON_IMPLEMENTATION

enum { JSON_FLAG_ACTOR = 1, JSON_FLAG_HOOK = 2, JSON_FLAG_LIST = 4, JSON_FLAG_OBJECT = 8, JSON_FLAG_EXCLUDED = 16 };

/* What a class that is not serializable says (the same text as the VM, JsonMethods.NotSerializableReason). */
#define JSON_NOT_SERIALIZABLE "it is not serializable ([NotSerializable] on it or a base class, or \"serializable\": false in its package)."

typedef struct JsCtx {
    bool failed;
    const char* message;
    const char* errorClass;
    void** blocks;      /* the arena: every block that was allocated (malloc), freed at the end */
    size_t nblocks, capBlocks;
} JsCtx;

static void* jsAlloc(JsCtx* J, size_t n) {
    void* p = malloc(n ? n : 1);
    if (FIRE_UNLIKELY(!p)) allocFailed();
    if (J->nblocks == J->capBlocks) {
        size_t cap = J->capBlocks ? J->capBlocks * 2 : 64;
        void** grown = (void**)realloc(J->blocks, cap * sizeof(void*));
        if (FIRE_UNLIKELY(!grown)) allocFailed();
        J->blocks = grown;
        J->capBlocks = cap;
    }
    J->blocks[J->nblocks++] = p;
    return p;
}
static void jsFreeAll(JsCtx* J) {
    size_t i;
    for (i = 0; i < J->nblocks; i++) free(J->blocks[i]);
    free(J->blocks);
    J->blocks = NULL;
    J->nblocks = J->capBlocks = 0;
}
/* A list that grows (from the arena): `*items` with `*n` of `*cap` elements of `size` bytes; room for one more. */
static void jsGrow(JsCtx* J, void** items, size_t n, size_t* cap, size_t size) {
    size_t newCap;
    void* grown;
    if (n < *cap) return;
    newCap = *cap ? *cap * 2 : 8;
    grown = jsAlloc(J, newCap * size);
    if (n) memcpy(grown, *items, n * size);
    *items = grown;
    *cap = newCap;
}

/* ---- texts (UTF-8, from the arena) ---- */
static const char* jsDup(JsCtx* J, const char* s, size_t n) {
    char* r = (char*)jsAlloc(J, n + 1);
    if (n) memcpy(r, s, n);
    r[n] = 0;
    return r;
}
static const char* jsCat(JsCtx* J, const char* a, const char* b) {
    size_t na = strlen(a), nb = strlen(b);
    char* r = (char*)jsAlloc(J, na + nb + 1);
    memcpy(r, a, na);
    memcpy(r + na, b, nb + 1);
    return r;
}
static const char* jsCat3(JsCtx* J, const char* a, const char* b, const char* c) { return jsCat(J, jsCat(J, a, b), c); }
static const char* jsCat5(JsCtx* J, const char* a, const char* b, const char* c, const char* d, const char* e) { return jsCat(J, jsCat(J, jsCat3(J, a, b, c), d), e); }
static bool jsEq(const char* a, const char* b) { return strcmp(a, b) == 0; }
static bool jsEndsWith(const char* s, const char* tail) { size_t n = strlen(tail), m = strlen(s); return m >= n && memcmp(s + m - n, tail, n) == 0; }

static void jsFail(JsCtx* J, const char* text, const char* cls) {
    if (J->failed) return;
    J->failed = true;
    J->message = text;
    J->errorClass = cls;
}

/* UTF-16 <-> UTF-8 */
typedef struct JsU16 { fire_char16* p; size_t n, cap; } JsU16;
typedef struct JsBytes { uint8_t* p; size_t n, cap; } JsBytes;
typedef struct JsStrs { const char** p; size_t n, cap; } JsStrs;

static void jsPush16(JsCtx* J, JsU16* out, fire_char16 c) {
    jsGrow(J, (void**)&out->p, out->n, &out->cap, sizeof(fire_char16));
    out->p[out->n++] = c;
}
static void jsAscii(JsCtx* J, JsU16* out, const char* text) { for (; *text; text++) jsPush16(J, out, (fire_char16)(unsigned char)*text); }
static void jsPushStr(JsCtx* J, JsStrs* list, const char* s) {
    jsGrow(J, (void**)&list->p, list->n, &list->cap, sizeof(const char*));
    list->p[list->n++] = s;
}

static size_t jsUtf8Put(char* out, uint32_t c) {
    if (c < 0x80) { out[0] = (char)c; return 1; }
    if (c < 0x800) { out[0] = (char)(0xC0 | (c >> 6)); out[1] = (char)(0x80 | (c & 0x3F)); return 2; }
    if (c < 0x10000) { out[0] = (char)(0xE0 | (c >> 12)); out[1] = (char)(0x80 | ((c >> 6) & 0x3F)); out[2] = (char)(0x80 | (c & 0x3F)); return 3; }
    out[0] = (char)(0xF0 | (c >> 18)); out[1] = (char)(0x80 | ((c >> 12) & 0x3F)); out[2] = (char)(0x80 | ((c >> 6) & 0x3F)); out[3] = (char)(0x80 | (c & 0x3F));
    return 4;
}
static const char* jsUtf8(JsCtx* J, const fire_char16* p, size_t n) {
    char* out = (char*)jsAlloc(J, n * 3 + 1);
    size_t k = 0, i;
    for (i = 0; i < n; i++) {
        uint32_t c = p[i];
        if (c >= 0xD800 && c < 0xDC00 && i + 1 < n && p[i + 1] >= 0xDC00 && p[i + 1] < 0xE000) { c = 0x10000 + ((c - 0xD800) << 10) + (uint32_t)(p[i + 1] - 0xDC00); i++; }
        k += jsUtf8Put(out + k, c);
    }
    out[k] = 0;
    return out;
}
static const char* jsUtf8Char(JsCtx* J, uint32_t c) { char text[5]; size_t n = jsUtf8Put(text, c); return jsDup(J, text, n); }
static JsU16 jsUtf16(JsCtx* J, const char* s) {
    JsU16 out = {NULL, 0, 0};
    size_t i = 0, len = strlen(s);
    while (i < len) {
        uint8_t c = (uint8_t)s[i];
        uint32_t cp;
        int extra, k;
        if (c < 0x80) { cp = c; extra = 0; }
        else if ((c & 0xE0) == 0xC0) { cp = c & 0x1F; extra = 1; }
        else if ((c & 0xF0) == 0xE0) { cp = c & 0x0F; extra = 2; }
        else { cp = c & 0x07; extra = 3; }
        i++;
        for (k = 0; k < extra && i < len; k++, i++) cp = (cp << 6) | ((uint8_t)s[i] & 0x3F);
        if (cp >= 0x10000) { cp -= 0x10000; jsPush16(J, &out, (fire_char16)(0xD800 + (cp >> 10))); jsPush16(J, &out, (fire_char16)(0xDC00 + (cp & 0x3FF))); }
        else jsPush16(J, &out, (fire_char16)cp);
    }
    return out;
}
static bool js16IsAscii(const fire_char16* p, size_t n, const char* ascii) {
    size_t m = strlen(ascii), i;
    if (n != m) return false;
    for (i = 0; i < n; i++) if (p[i] != (fire_char16)(unsigned char)ascii[i]) return false;
    return true;
}

/* ---- type names ---- */
static const char* jsTrim(JsCtx* J, const char* s, size_t a, size_t b) {
    while (a < b && s[a] == ' ') a++;
    while (b > a && s[b - 1] == ' ') b--;
    return jsDup(J, s + a, b - a);
}
/* `List<Point>` -> ("List", ["Point"]), `Point` -> ("Point", []). */
static const char* jsSplitGeneric(JsCtx* J, const char* type, JsStrs* args) {
    const char* lt = strchr(type, '<');
    size_t len = strlen(type), start, i;
    int depth = 0;
    if (!lt || len == 0 || type[len - 1] != '>') return type;
    start = (size_t)(lt - type) + 1;
    for (i = start; i + 1 < len; i++) {
        char ch = type[i];
        if (ch == '<') depth++;
        else if (ch == '>') depth--;
        else if (ch == ',' && depth == 0) { if (args) jsPushStr(J, args, jsTrim(J, type, start, i)); start = i + 1; }
    }
    if (args) jsPushStr(J, args, jsTrim(J, type, start, len - 1));
    return jsTrim(J, type, 0, (size_t)(lt - type));
}
/* GenericClassNames.PlainName: `Box`1` -> `Box`. */
static const char* jsPlainName(JsCtx* J, const char* name) {
    const char* tick = strchr(name, '`');
    return tick ? jsDup(J, name, (size_t)(tick - name)) : name;
}
static const char* jsPlain(JsCtx* J, const char* type) { return jsPlainName(J, jsSplitGeneric(J, type, NULL)); }
static const char* jsElementType(JsCtx* J, const char* type) {
    JsStrs args = {NULL, 0, 0};
    if (jsEndsWith(type, "[]")) return jsDup(J, type, strlen(type) - 2);
    if (jsEq(jsSplitGeneric(J, type, &args), "List") && args.n == 1) return args.p[0];
    return "";
}
/* The text of a type argument as the VM keeps it ("" when not known). */
static const char* jsTArgName(const TArg* t) {
    const char* text;
    if (!t) return "";
    text = jsonTArgText(t);
    return text ? text : (t->display ? t->display : "");
}
/* The type arguments of an object (none when it has none or one is not known). */
static JsStrs jsTypeArgs(JsCtx* J, Obj* o, const JsonClassInfo* c) {
    JsStrs result = {NULL, 0, 0};
    uint32_t k;
    if (!o->targs || !c || c->ntparams == 0) return result;
    for (k = 0; k < c->ntparams; k++) jsPushStr(J, &result, jsTArgName(o->targs[k]));
    return result;
}
/* The type of a slot with a type parameter of the class (`$0`) replaced by the object's type argument. */
static const char* jsSubstitute(JsCtx* J, const char* type, Obj* o, const JsonClassInfo* c) {
    size_t len = strlen(type), end = 1;
    uint32_t k;
    const char* arg;
    if (len < 2 || type[0] != '$') return type;
    while (end < len && type[end] >= '0' && type[end] <= '9') end++;
    k = (uint32_t)atoi(jsDup(J, type + 1, end - 1));
    arg = o && o->targs && c && k < c->ntparams ? jsTArgName(o->targs[k]) : "";
    return arg[0] == 0 ? "" : jsCat(J, arg, type + end);
}

static const char* const kJsB64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
static const char* jsBase64(JsCtx* J, const uint8_t* p, uint32_t n) {
    char* out = (char*)jsAlloc(J, (size_t)(n + 2) / 3 * 4 + 1);
    size_t k = 0;
    uint32_t i;
    for (i = 0; i < n; i += 3) {
        uint32_t b = (uint32_t)p[i] << 16 | (i + 1 < n ? (uint32_t)p[i + 1] << 8 : 0) | (i + 2 < n ? p[i + 2] : 0);
        out[k++] = kJsB64[(b >> 18) & 63];
        out[k++] = kJsB64[(b >> 12) & 63];
        out[k++] = i + 1 < n ? kJsB64[(b >> 6) & 63] : '=';
        out[k++] = i + 2 < n ? kJsB64[b & 63] : '=';
    }
    out[k] = 0;
    return out;
}
/* Base64 like Convert.FromBase64String (spaces are skipped); false when the text is not base64. */
static bool jsFromBase64(JsCtx* J, const fire_char16* s, size_t n, JsBytes* out) {
    uint32_t acc = 0;
    int bits = 0, chars = 0, pad = 0;
    size_t i;
    for (i = 0; i < n; i++) {
        fire_char16 ch = s[i];
        int v;
        if (ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n') continue;
        if (ch >= 'A' && ch <= 'Z') v = ch - 'A';
        else if (ch >= 'a' && ch <= 'z') v = ch - 'a' + 26;
        else if (ch >= '0' && ch <= '9') v = ch - '0' + 52;
        else if (ch == '+') v = 62;
        else if (ch == '/') v = 63;
        else if (ch == '=') { pad++; chars++; continue; }
        else return false;
        if (pad > 0) return false;
        chars++;
        acc = (acc << 6) | (uint32_t)v;
        bits += 6;
        if (bits >= 8) {
            bits -= 8;
            jsGrow(J, (void**)&out->p, out->n, &out->cap, 1);
            out->p[out->n++] = (uint8_t)((acc >> bits) & 0xFF);
        }
    }
    return chars % 4 == 0 && pad <= 2;
}

/* A float as JSON: the shortest text that reads back as the same number, always with a `.` or an exponent (JsonMethods.FormatFloat). */
static void jsFloat(JsCtx* J, JsU16* out, double d) {
    char text[48];
    int n = formatFloating(d, false, text);
    text[n] = 0;
    jsAscii(J, out, text);
    if (!strchr(text, '.') && !strchr(text, 'E') && !strchr(text, 'e')) jsAscii(J, out, ".0");
}
static void jsString(JsCtx* J, JsU16* out, const fire_char16* p, size_t n) {
    size_t i;
    jsPush16(J, out, '"');
    for (i = 0; i < n; i++) {
        fire_char16 c = p[i];
        switch (c) {
            case '"': jsAscii(J, out, "\\\""); break;
            case '\\': jsAscii(J, out, "\\\\"); break;
            case '\n': jsAscii(J, out, "\\n"); break;
            case '\r': jsAscii(J, out, "\\r"); break;
            case '\t': jsAscii(J, out, "\\t"); break;
            case '\b': jsAscii(J, out, "\\b"); break;
            case '\f': jsAscii(J, out, "\\f"); break;
            default:
                if (c < 0x20) { char esc[8]; snprintf(esc, sizeof esc, "\\u%04x", (unsigned)c); jsAscii(J, out, esc); }
                else jsPush16(J, out, c);
        }
    }
    jsPush16(J, out, '"');
}
static void jsStringUtf8(JsCtx* J, JsU16* out, const char* utf8) { JsU16 s = jsUtf16(J, utf8); jsString(J, out, s.p, s.n); }

/* ---- a map of pointers (the writer: how often an object is reached, its id, whether it is written, whether its owner is in the graph) ---- */
typedef struct JsSeen { const void* key; int seen; int id; uint8_t written; uint8_t ownerInGraph; } JsSeen;
typedef struct JsSeenMap { JsSeen* items; size_t count, cap; } JsSeenMap;
static size_t jsHashPtr(const void* p) { uintptr_t x = (uintptr_t)p; x ^= x >> 33; x *= (uintptr_t)0xff51afd7ed558ccdULL; x ^= x >> 33; return (size_t)x; }
static JsSeen* jsSeenFind(JsSeenMap* m, const void* key) {
    size_t i;
    if (!m->cap) return NULL;
    for (i = jsHashPtr(key) & (m->cap - 1); m->items[i].key; i = (i + 1) & (m->cap - 1))
        if (m->items[i].key == key) return &m->items[i];
    return NULL;
}
static JsSeen* jsSeenGet(JsCtx* J, JsSeenMap* m, const void* key) {
    JsSeen* e = jsSeenFind(m, key);
    size_t i;
    if (e) return e;
    if ((m->count + 1) * 2 > m->cap) {
        size_t cap = m->cap ? m->cap * 2 : 64, k;
        JsSeen* old = m->items;
        size_t oldCap = m->cap;
        m->items = (JsSeen*)jsAlloc(J, cap * sizeof(JsSeen));
        memset(m->items, 0, cap * sizeof(JsSeen));
        m->cap = cap;
        for (k = 0; k < oldCap; k++)
            if (old[k].key) {
                for (i = jsHashPtr(old[k].key) & (cap - 1); m->items[i].key; i = (i + 1) & (cap - 1)) { }
                m->items[i] = old[k];
            }
    }
    for (i = jsHashPtr(key) & (m->cap - 1); m->items[i].key; i = (i + 1) & (m->cap - 1)) { }
    m->items[i].key = key;
    m->count++;
    return &m->items[i];
}

/* ---- writing ---- */
typedef struct JsWriter {
    JsCtx* J;
    bool indent, typeNames;
    JsU16 out;
    JsSeenMap map;
    const void** order;   /* (the order of discovery: the ids follow it, like the VM) */
    size_t norder, capOrder;
    int depth;
} JsWriter;
typedef struct JsPending { Value v; const void* container; uint8_t containerKind; } JsPending;

/* Does the container (an object or an array) own the item? */
static bool jsOwns(const void* container, uint8_t containerKind, const Owned* item) {
    if (!item->owner) return false;
    if (containerKind == K_Class) return item->owner == &((Obj*)container)->owned;
    if (containerKind == K_Array) { const Arr* a = (const Arr*)container; return a->parts && item->owner == a->parts; }
    return false;
}
static int jsIdOf(JsWriter* w, const void* key) { JsSeen* e = jsSeenFind(&w->map, key); return e ? e->id : 0; }

static void jsDiscover(JsWriter* w, Value start) {
    JsCtx* J = w->J;
    JsPending* stack = NULL;
    size_t n = 0, cap = 0, i;
    int next = 1;
    jsGrow(J, (void**)&stack, n, &cap, sizeof(JsPending));
    stack[n].v = start; stack[n].container = NULL; stack[n].containerKind = K_Undefined; n++;
    while (n > 0) {
        JsPending p = stack[--n];
        Value v = p.v;
        const void* key;
        JsSeen* e;
        if (v.kind != K_Class && v.kind != K_Array && v.kind != K_Buffer) continue;
        if (!leafAlive(v)) continue;
        key = v.p;
        e = jsSeenGet(J, &w->map, key);
        if (p.container && jsOwns(p.container, p.containerKind, (const Owned*)key)) e->ownerInGraph = 1;
        if (e->seen > 0) { e->seen++; continue; }
        e->seen = 1;
        jsGrow(J, (void**)&w->order, w->norder, &w->capOrder, sizeof(const void*));
        w->order[w->norder++] = key;
        if (v.kind == K_Class) {
            Obj* o = objAt(v);
            const JsonClassInfo* c = jsonClassAt(o->cls);
            int64_t k;
            if (!c) continue;
            if (c->flags & JSON_FLAG_LIST) {
                const JsonSpecial* sp = jsonSpecial();
                Value items = objFields(o)[sp->listItems], count = objFields(o)[sp->listCount];
                if (items.kind == K_Array && leafAlive(items)) {
                    Arr* a = arrOf(items);
                    int64_t m = count.kind == K_Int ? count.i : 0;
                    if (m > (int64_t)a->length) m = a->length;
                    for (k = m - 1; k >= 0; k--) {
                        jsGrow(J, (void**)&stack, n, &cap, sizeof(JsPending));
                        stack[n].v = arrItems(a)[k]; stack[n].container = a; stack[n].containerKind = K_Array; n++;
                    }
                }
                continue;
            }
            for (k = (int64_t)c->njson - 1; k >= 0; k--) {
                jsGrow(J, (void**)&stack, n, &cap, sizeof(JsPending));
                stack[n].v = objFields(o)[c->fields[k].slot]; stack[n].container = o; stack[n].containerKind = K_Class; n++;
            }
        } else if (v.kind == K_Array) {
            Arr* a = arrOf(v);
            int64_t k;
            for (k = (int64_t)a->length - 1; k >= 0; k--) {
                jsGrow(J, (void**)&stack, n, &cap, sizeof(JsPending));
                stack[n].v = arrItems(a)[k]; stack[n].container = a; stack[n].containerKind = K_Array; n++;
            }
        }
    }
    for (i = 0; i < w->norder; i++) {
        JsSeen* e = jsSeenFind(&w->map, w->order[i]);
        if (e->seen > 1) e->id = next++;
    }
}

static void jsNewLine(JsWriter* w) {
    int i;
    if (!w->indent) return;
    jsPush16(w->J, &w->out, '\n');
    for (i = 0; i < w->depth * 2; i++) jsPush16(w->J, &w->out, ' ');
}
static void jsKey16(JsWriter* w, const fire_char16* p, size_t n, bool* first) {
    if (!*first) jsPush16(w->J, &w->out, ',');
    *first = false;
    jsNewLine(w);
    jsString(w->J, &w->out, p, n);
    jsAscii(w->J, &w->out, w->indent ? ": " : ":");
}
static void jsKey(JsWriter* w, const char* name, bool* first) { JsU16 s = jsUtf16(w->J, name); jsKey16(w, s.p, s.n, first); }
static void jsNumber(JsWriter* w, int64_t i) { char text[32]; snprintf(text, sizeof text, "%lld", (long long)i); jsAscii(w->J, &w->out, text); }
static void jsRef(JsWriter* w, int id) { jsAscii(w->J, &w->out, "{\"$ref\":"); jsNumber(w, id); jsPush16(w->J, &w->out, '}'); }

static bool jsCanDefine(JsWriter* w, const void* key, const void* container, uint8_t containerKind) {
    JsSeen* e = jsSeenFind(&w->map, key);
    if (e && e->written) return false;
    if (e && e->ownerInGraph && container && !jsOwns(container, containerKind, (const Owned*)key)) return false;
    return true;
}
static void jsMarkWritten(JsWriter* w, const void* key) { jsSeenGet(w->J, &w->map, key)->written = 1; }

static void jsWriteNumber(JsWriter* w, Value v, uint32_t fieldUnit) {
    bool plain = v.unit == 0 || unitIsUnitless(v.unit) || (fieldUnit != 0 && unitEq(v.unit, fieldUnit));
    if (!plain) jsAscii(w->J, &w->out, "{\"$value\":");
    if (v.kind == K_Int) jsNumber(w, v.i);
    else {
        double d = (double)v.f;
        if (isnan(d)) jsAscii(w->J, &w->out, "\"NaN\"");
        else if (isinf(d)) jsAscii(w->J, &w->out, d > 0 ? "\"Infinity\"" : "\"-Infinity\"");
        else jsFloat(w->J, &w->out, d);
    }
    if (!plain) {
        jsAscii(w->J, &w->out, ",\"$unit\":");
        jsStringUtf8(w->J, &w->out, g_ud[v.unit].name);
        jsPush16(w->J, &w->out, '}');
    }
}

static void jsWriteValue(JsWriter* w, Value v, const char* type, const void* container, uint8_t containerKind, bool isRoot, uint32_t unit);

static void jsWriteItems(JsWriter* w, const Value* items, int64_t count, const char* elementType, Arr* container) {
    int64_t i;
    jsPush16(w->J, &w->out, '[');
    w->depth++;
    for (i = 0; i < count && !w->J->failed; i++) {
        Value item = items[i];
        if (i > 0) jsPush16(w->J, &w->out, ',');
        jsNewLine(w);
        if (item.kind == K_Lambda || isPtr(item)) { jsAscii(w->J, &w->out, "null"); continue; }
        jsWriteValue(w, item, elementType, container, K_Array, false, 0);
    }
    w->depth--;
    if (count > 0) jsNewLine(w);
    jsPush16(w->J, &w->out, ']');
}

static void jsWriteArray(JsWriter* w, Arr* a, const char* elementType, const void* container, uint8_t containerKind) {
    int id = jsIdOf(w, a);
    if (id) {
        if (!jsCanDefine(w, a, container, containerKind)) { jsRef(w, id); return; }
        jsMarkWritten(w, a);
        jsAscii(w->J, &w->out, "{\"$id\":");
        jsNumber(w, id);
        jsAscii(w->J, &w->out, w->indent ? ", \"$values\": " : ",\"$values\":");
        jsWriteItems(w, arrItems(a), a->length, elementType, a);
        jsPush16(w->J, &w->out, '}');
        return;
    }
    jsWriteItems(w, arrItems(a), a->length, elementType, a);
}

static const char* jsTypeName(JsWriter* w, Obj* o, const JsonClassInfo* c) {
    JsStrs args = jsTypeArgs(w->J, o, c);
    const char* joined = "";
    size_t i;
    if (args.n == 0) return c->plain;
    for (i = 0; i < args.n; i++) {
        if (args.p[i][0] == 0) return c->plain;
        joined = i ? jsCat3(w->J, joined, ",", args.p[i]) : args.p[i];
    }
    return jsCat5(w->J, c->plain, "<", joined, ">", "");
}

static void jsWriteObject(JsWriter* w, Obj* o, const char* type, const void* container, uint8_t containerKind, bool isRoot) {
    JsCtx* J = w->J;
    const JsonClassInfo* c = jsonClassAt(o->cls);
    const JsonSpecial* sp = jsonSpecial();
    JsStrs typeArgs = {NULL, 0, 0};
    bool shared, withType, first = true;
    int id;
    if (!c) { jsAscii(J, &w->out, "null"); return; }
    if ((c->flags & JSON_FLAG_ACTOR) || (o->h.flags & 8)) { jsFail(J, jsCat3(J, "An actor ('", c->name, "') cannot be written to JSON."), "JsonException"); return; }
    if (c->flags & JSON_FLAG_EXCLUDED) { jsFail(J, jsCat3(J, "The class '", c->name, "' cannot be written to JSON: " JSON_NOT_SERIALIZABLE), "ArgumentException"); return; }
    id = jsIdOf(w, o);
    shared = id != 0;
    if (shared && !(isRoot || jsCanDefine(w, o, container, containerKind))) { jsRef(w, id); return; }
    if (shared) jsMarkWritten(w, o);
    jsSplitGeneric(J, type, &typeArgs);
    withType = w->typeNames && (isRoot || type[0] == 0 || !jsEq(jsPlain(J, type), c->plain) || (typeArgs.n == 0 && o->targs && c->ntparams > 0));
    if (c->flags & JSON_FLAG_OBJECT) withType = false;

    if (c->flags & JSON_FLAG_LIST) {
        Value items = objFields(o)[sp->listItems], countV = objFields(o)[sp->listCount];
        int64_t count = countV.kind == K_Int ? countV.i : 0;
        JsStrs own = jsTypeArgs(J, o, c);
        const char* elementType = own.n == 1 ? own.p[0] : jsElementType(J, type);
        Arr* a = items.kind == K_Array && leafAlive(items) ? arrOf(items) : NULL;
        bool firstL = true;
        if (!a) count = 0;
        else if (count > (int64_t)a->length) count = a->length;
        if (!shared && !withType) { jsWriteItems(w, a ? arrItems(a) : NULL, count, elementType, a); return; }
        jsPush16(J, &w->out, '{');
        w->depth++;
        if (withType) { jsKey(w, "$type", &firstL); jsStringUtf8(J, &w->out, jsTypeName(w, o, c)); }
        if (shared) { jsKey(w, "$id", &firstL); jsNumber(w, id); }
        jsKey(w, "$values", &firstL);
        jsWriteItems(w, a ? arrItems(a) : NULL, count, elementType, a);
        w->depth--;
        jsNewLine(w);
        jsPush16(J, &w->out, '}');
        return;
    }

    jsPush16(J, &w->out, '{');
    w->depth++;
    if (withType) { jsKey(w, "$type", &first); jsStringUtf8(J, &w->out, jsTypeName(w, o, c)); }
    if (shared) { jsKey(w, "$id", &first); jsNumber(w, id); }
    if (c->flags & JSON_FLAG_OBJECT) {
        Value keys = objFields(o)[sp->objKeys], values = objFields(o)[sp->objValues], countV = objFields(o)[sp->objCount];
        int64_t n = countV.kind == K_Int ? countV.i : 0, i;
        if (keys.kind == K_Array && values.kind == K_Array && leafAlive(keys) && leafAlive(values)) {
            Arr* ka = arrOf(keys);
            Arr* va = arrOf(values);
            for (i = 0; i < n && i < (int64_t)ka->length && i < (int64_t)va->length && !J->failed; i++) {
                Value k = arrItems(ka)[i];
                if (k.kind == K_String) { const Str* s = strOf(k); jsKey16(w, s->data, s->length, &first); }
                else jsKey(w, "", &first);
                jsWriteValue(w, arrItems(va)[i], "", va, K_Array, false, 0);
            }
        }
    } else {
        uint32_t i;
        for (i = 0; i < c->njson && !J->failed; i++) {
            const JsonFieldInfo* f = &c->fields[i];
            Value value = objFields(o)[f->slot];
            if (value.kind == K_Lambda || isPtr(value)) continue;
            jsKey(w, f->name, &first);
            jsWriteValue(w, value, jsSubstitute(J, f->type, o, c), o, K_Class, false, f->unit);
        }
    }
    w->depth--;
    if (!first) jsNewLine(w);
    jsPush16(J, &w->out, '}');
}

static void jsWriteValue(JsWriter* w, Value v, const char* type, const void* container, uint8_t containerKind, bool isRoot, uint32_t unit) {
    JsCtx* J = w->J;
    if (J->failed) return;
    switch (v.kind) {
        case K_Undefined: jsAscii(J, &w->out, "null"); return;
        case K_Bool: jsAscii(J, &w->out, v.i ? "true" : "false"); return;
        case K_Int: case K_Float: jsWriteNumber(w, v, unit); return;
        case K_String: { const Str* s = strOf(v); jsString(J, &w->out, s->data, s->length); return; }
        case K_Char: {
            fire_char16 c = (fire_char16)v.i;
            if (jsEq(type, "char")) jsString(J, &w->out, &c, 1);
            else { jsAscii(J, &w->out, "{\"$char\":"); jsString(J, &w->out, &c, 1); jsPush16(J, &w->out, '}'); }
            return;
        }
        case K_Buffer: {
            Buf* b;
            int id;
            JsSeen* e;
            const char* b64;
            if (!leafAlive(v)) { jsAscii(J, &w->out, "null"); return; }
            b = bufOf(v);
            id = jsIdOf(w, b);
            e = jsSeenFind(&w->map, b);
            if (id && e && e->written) { jsRef(w, id); return; }
            if (id) jsMarkWritten(w, b);
            b64 = jsBase64(J, bufBytes(b), b->length);
            if ((jsEq(type, "byte[]") || jsEq(type, "buffer")) && !id) { jsStringUtf8(J, &w->out, b64); return; }
            jsPush16(J, &w->out, '{');
            if (id) { jsAscii(J, &w->out, "\"$id\":"); jsNumber(w, id); jsPush16(J, &w->out, ','); }
            jsAscii(J, &w->out, "\"$buffer\":");
            jsStringUtf8(J, &w->out, b64);
            jsPush16(J, &w->out, '}');
            return;
        }
        case K_Array:
            if (!leafAlive(v)) { jsAscii(J, &w->out, "null"); return; }
            jsWriteArray(w, arrOf(v), jsElementType(J, type), container, containerKind);
            return;
        case K_Class:
            if (!leafAlive(v)) { jsAscii(J, &w->out, "null"); return; }
            jsWriteObject(w, objAt(v), type, container, containerKind, isRoot);
            return;
        default:
            jsAscii(J, &w->out, "null");   /* a lambda or pointer in an array (a field of that kind is left out) */
            return;
    }
}

/* ---- parsing (the same parser and messages as JsonMethods.JsonParser) ---- */
enum { JN_NULL, JN_BOOL, JN_NUMBER, JN_STRING, JN_ARRAY, JN_OBJECT };
typedef struct JsNode JsNode;
typedef struct JsMember { const fire_char16* key; size_t klen; JsNode* node; } JsMember;
struct JsNode {
    uint8_t kind;
    bool b, isInt;
    const char* num;
    const fire_char16* str;
    size_t slen;
    JsNode** items;
    size_t nitems, capItems;
    JsMember* members;
    size_t nmembers, capMembers;
};
static JsNode* jsNodeGet(JsNode* node, const char* key) {
    size_t i;
    for (i = 0; i < node->nmembers; i++) if (js16IsAscii(node->members[i].key, node->members[i].klen, key)) return node->members[i].node;
    return NULL;
}

typedef struct JsParser { JsCtx* J; const fire_char16* text; size_t n, pos; } JsParser;

static JsNode* jsMake(JsParser* ps, uint8_t kind) {
    JsNode* node = (JsNode*)jsAlloc(ps->J, sizeof(JsNode));
    memset(node, 0, sizeof *node);
    node->kind = kind;
    return node;
}
static JsNode* jsParseFail(JsParser* ps, size_t at, const char* what) {
    int line = 1, column = 1;
    size_t i;
    char where[64];
    for (i = 0; i < at && i < ps->n; i++) { if (ps->text[i] == '\n') { line++; column = 1; } else column++; }
    snprintf(where, sizeof where, " (line %d, column %d).", line, column);
    jsFail(ps->J, jsCat(ps->J, what, where), "JsonException");
    return NULL;
}
static void jsSkipSpace(JsParser* ps) { while (ps->pos < ps->n && (ps->text[ps->pos] == ' ' || ps->text[ps->pos] == '\t' || ps->text[ps->pos] == '\n' || ps->text[ps->pos] == '\r')) ps->pos++; }
static bool jsMatches(JsParser* ps, const char* word) {
    size_t len = strlen(word), i;
    if (ps->pos + len > ps->n) return false;
    for (i = 0; i < len; i++) if (ps->text[ps->pos + i] != (fire_char16)word[i]) return false;
    return true;
}
static bool jsDigit(fire_char16 c) { return c >= '0' && c <= '9'; }

static bool jsParseString(JsParser* ps, JsU16* out) {
    size_t start = ps->pos;
    ps->pos++;   /* '"' */
    for (;;) {
        fire_char16 c, e;
        if (ps->pos >= ps->n) { jsParseFail(ps, start, "Unterminated string"); return false; }
        c = ps->text[ps->pos++];
        if (c == '"') return true;
        if (c != '\\') { jsPush16(ps->J, out, c); continue; }
        if (ps->pos >= ps->n) { jsParseFail(ps, start, "Unterminated string"); return false; }
        e = ps->text[ps->pos++];
        switch (e) {
            case '"': jsPush16(ps->J, out, '"'); break;
            case '\\': jsPush16(ps->J, out, '\\'); break;
            case '/': jsPush16(ps->J, out, '/'); break;
            case 'n': jsPush16(ps->J, out, '\n'); break;
            case 'r': jsPush16(ps->J, out, '\r'); break;
            case 't': jsPush16(ps->J, out, '\t'); break;
            case 'b': jsPush16(ps->J, out, '\b'); break;
            case 'f': jsPush16(ps->J, out, '\f'); break;
            case 'u': {
                uint32_t code = 0;
                bool ok = ps->pos + 4 <= ps->n;
                size_t i;
                for (i = 0; ok && i < 4; i++) {
                    fire_char16 h = ps->text[ps->pos + i];
                    int v = h >= '0' && h <= '9' ? h - '0' : h >= 'a' && h <= 'f' ? h - 'a' + 10 : h >= 'A' && h <= 'F' ? h - 'A' + 10 : -1;
                    if (v < 0) ok = false; else code = code * 16 + (uint32_t)v;
                }
                if (!ok) { jsParseFail(ps, ps->pos, "Invalid \\u escape"); return false; }
                jsPush16(ps->J, out, (fire_char16)code);
                ps->pos += 4;
                break;
            }
            default:
                jsParseFail(ps, ps->pos - 1, jsCat3(ps->J, "Invalid escape '\\", jsUtf8Char(ps->J, e), "'"));
                return false;
        }
    }
}

static JsNode* jsParseValue(JsParser* ps, int depth) {
    fire_char16 c;
    if (depth > 512) return jsParseFail(ps, ps->pos, "JSON nested too deeply");
    jsSkipSpace(ps);
    if (ps->pos >= ps->n) return jsParseFail(ps, ps->pos, "Unexpected end of the JSON text");
    c = ps->text[ps->pos];
    if (c == '{') {
        JsNode* obj;
        ps->pos++;
        obj = jsMake(ps, JN_OBJECT);
        jsSkipSpace(ps);
        if (ps->pos < ps->n && ps->text[ps->pos] == '}') { ps->pos++; return obj; }
        for (;;) {
            JsU16 key = {NULL, 0, 0};
            JsNode* item;
            jsSkipSpace(ps);
            if (ps->pos >= ps->n || ps->text[ps->pos] != '"') return jsParseFail(ps, ps->pos, "Expected a name in quotes");
            if (!jsParseString(ps, &key)) return NULL;
            jsSkipSpace(ps);
            if (ps->pos >= ps->n || ps->text[ps->pos] != ':') return jsParseFail(ps, ps->pos, "Expected ':' after a name");
            ps->pos++;
            item = jsParseValue(ps, depth + 1);
            if (!item) return NULL;
            jsGrow(ps->J, (void**)&obj->members, obj->nmembers, &obj->capMembers, sizeof(JsMember));
            obj->members[obj->nmembers].key = key.p;
            obj->members[obj->nmembers].klen = key.n;
            obj->members[obj->nmembers].node = item;
            obj->nmembers++;
            jsSkipSpace(ps);
            if (ps->pos < ps->n && ps->text[ps->pos] == ',') { ps->pos++; continue; }
            if (ps->pos < ps->n && ps->text[ps->pos] == '}') { ps->pos++; return obj; }
            return jsParseFail(ps, ps->pos, "Expected ',' or '}' in an object");
        }
    }
    if (c == '[') {
        JsNode* array;
        ps->pos++;
        array = jsMake(ps, JN_ARRAY);
        jsSkipSpace(ps);
        if (ps->pos < ps->n && ps->text[ps->pos] == ']') { ps->pos++; return array; }
        for (;;) {
            JsNode* item = jsParseValue(ps, depth + 1);
            if (!item) return NULL;
            jsGrow(ps->J, (void**)&array->items, array->nitems, &array->capItems, sizeof(JsNode*));
            array->items[array->nitems++] = item;
            jsSkipSpace(ps);
            if (ps->pos < ps->n && ps->text[ps->pos] == ',') { ps->pos++; continue; }
            if (ps->pos < ps->n && ps->text[ps->pos] == ']') { ps->pos++; return array; }
            return jsParseFail(ps, ps->pos, "Expected ',' or ']' in an array");
        }
    }
    if (c == '"') {
        JsNode* s = jsMake(ps, JN_STRING);
        JsU16 text = {NULL, 0, 0};
        if (!jsParseString(ps, &text)) return NULL;
        s->str = text.p;
        s->slen = text.n;
        return s;
    }
    if (c == 't' && jsMatches(ps, "true")) { JsNode* b; ps->pos += 4; b = jsMake(ps, JN_BOOL); b->b = true; return b; }
    if (c == 'f' && jsMatches(ps, "false")) { ps->pos += 5; return jsMake(ps, JN_BOOL); }
    if (c == 'n' && jsMatches(ps, "null")) { ps->pos += 4; return jsMake(ps, JN_NULL); }
    if (c == '-' || jsDigit(c)) {
        size_t start = ps->pos, i;
        bool isInt = true;
        char* number;
        JsNode* node;
        if (ps->text[ps->pos] == '-') ps->pos++;
        while (ps->pos < ps->n && jsDigit(ps->text[ps->pos])) ps->pos++;
        if (ps->pos < ps->n && ps->text[ps->pos] == '.') { isInt = false; ps->pos++; while (ps->pos < ps->n && jsDigit(ps->text[ps->pos])) ps->pos++; }
        if (ps->pos < ps->n && (ps->text[ps->pos] == 'e' || ps->text[ps->pos] == 'E')) {
            isInt = false;
            ps->pos++;
            if (ps->pos < ps->n && (ps->text[ps->pos] == '+' || ps->text[ps->pos] == '-')) ps->pos++;
            while (ps->pos < ps->n && jsDigit(ps->text[ps->pos])) ps->pos++;
        }
        number = (char*)jsAlloc(ps->J, ps->pos - start + 1);
        for (i = start; i < ps->pos; i++) number[i - start] = (char)ps->text[i];
        number[ps->pos - start] = 0;
        if (jsEq(number, "-") || number[0] == 0) return jsParseFail(ps, start, "Invalid number");
        node = jsMake(ps, JN_NUMBER);
        node->num = number;
        node->isInt = isInt;
        return node;
    }
    return jsParseFail(ps, ps->pos, jsCat3(ps->J, "Unexpected character '", jsUtf8Char(ps->J, c), "'"));
}

static JsNode* jsParse(JsParser* ps) {
    JsNode* node = jsParseValue(ps, 0);
    if (!node) return NULL;
    jsSkipSpace(ps);
    if (ps->pos < ps->n) return jsParseFail(ps, ps->pos, "Unexpected text after the JSON value");
    return node;
}

/* ---- reading: the parsed tree into fire values ---- */
typedef struct JsFixup { Value container; uint32_t slot; int64_t id; } JsFixup;
typedef struct JsSlot { Value container; uint32_t index; bool has; } JsSlot;
typedef struct JsIdEntry { int64_t id; Value value; bool used; } JsIdEntry;
typedef struct JsReader {
    JsCtx* J;
    OwnList* scope;   /* the temporaries (strings) and the owner of the root */
    bool plain;
    JsIdEntry* byId;
    size_t nids, capIds;
    JsFixup* fixups;
    size_t nfixups, capFixups;
    Value* hooks;
    size_t nhooks, capHooks;
} JsReader;

static size_t jsHashId(int64_t id) { uint64_t x = (uint64_t)id; x ^= x >> 33; x *= 0xff51afd7ed558ccdULL; x ^= x >> 33; return (size_t)x; }
static JsIdEntry* jsIdFind(JsReader* r, int64_t id) {
    size_t i;
    if (!r->capIds) return NULL;
    for (i = jsHashId(id) & (r->capIds - 1); r->byId[i].used; i = (i + 1) & (r->capIds - 1))
        if (r->byId[i].id == id) return &r->byId[i];
    return NULL;
}
static void jsIdSet(JsReader* r, int64_t id, Value v) {
    JsIdEntry* e = jsIdFind(r, id);
    size_t i;
    if (e) { e->value = v; return; }
    if ((r->nids + 1) * 2 > r->capIds) {
        size_t cap = r->capIds ? r->capIds * 2 : 32, k;
        JsIdEntry* old = r->byId;
        size_t oldCap = r->capIds;
        r->byId = (JsIdEntry*)jsAlloc(r->J, cap * sizeof(JsIdEntry));
        memset(r->byId, 0, cap * sizeof(JsIdEntry));
        r->capIds = cap;
        for (k = 0; k < oldCap; k++)
            if (old[k].used) {
                for (i = jsHashId(old[k].id) & (cap - 1); r->byId[i].used; i = (i + 1) & (cap - 1)) { }
                r->byId[i] = old[k];
            }
    }
    for (i = jsHashId(id) & (r->capIds - 1); r->byId[i].used; i = (i + 1) & (r->capIds - 1)) { }
    r->byId[i].used = true;
    r->byId[i].id = id;
    r->byId[i].value = v;
    r->nids++;
}

static Value jsReadFail(JsReader* r, const char* text) { jsFail(r->J, text, "JsonException"); return Undef(); }

/* The list that owns what is made inside a container: an object's own list, an array's parts. */
static OwnList* jsOwnerIn(Value container) {
    Arr* a;
    if (container.kind == K_Class) return &objAt(container)->owned;
    a = arrOf(container);
    if (!a->parts) a->parts = newPartsList(&a->h);
    return a->parts;
}
static void jsStore(Value container, uint32_t slot, Value v) {
    Value* target = container.kind == K_Class ? &objFields(objAt(container))[slot] : &arrItems(arrOf(container))[slot];
    release(*target);
    *target = v;
    retain(v);
}

static const char* jsDescribe(JsCtx* J, const char* type) {
    if (type[0] == 0) return "a value";
    if (jsEndsWith(type, "[]")) return "an array";
    return jsCat3(J, "'", type, "'");
}
static Value jsMismatch(JsReader* r, const char* path, const char* type, const char* found) {
    return jsReadFail(r, jsCat(r->J, jsCat5(r->J, path, ": expected ", jsDescribe(r->J, type), ", found ", found), "."));
}

static bool jsParseLong(const char* text, int64_t* out) {
    char* end = NULL;
    long long v;
    if (!text[0]) return false;
    errno = 0;
    v = strtoll(text, &end, 10);
    if (errno != 0 || !end || *end) return false;
    *out = v;
    return true;
}

static Value jsBuild(JsReader* r, JsNode* node, const char* type, OwnList* owner, const char* path, uint32_t unit, JsSlot slot);

static Value jsReadNumber(JsReader* r, const char* text, bool isInt, const char* type, uint32_t unit, const char* path) {
    static const char* const ints[] = {"int", "byte", "long", "short", "int8", "int16", "int32", "int64", "uint8", "uint16", "uint32", "uint64"};
    static const char* const floats[] = {"float", "double", "float32", "float64"};
    size_t k;
    int64_t i;
    for (k = 0; k < sizeof ints / sizeof ints[0]; k++)
        if (jsEq(type, ints[k])) {
            double d;
            if (isInt && jsParseLong(text, &i)) return fire_IntU(i, unit);
            d = strtod(text, NULL);
            if (d == floor(d) && fabs(d) < 9.2e18) return fire_IntU((int64_t)d, unit);
            return jsReadFail(r, jsCat3(r->J, path, ": expected a whole number, found ", jsCat(r->J, text, ".")));
        }
    for (k = 0; k < sizeof floats / sizeof floats[0]; k++) if (jsEq(type, floats[k])) return fire_FloatU((Real)strtod(text, NULL), unit);
    if (type[0] == 0 || jsEq(type, "any")) {
        if (isInt && jsParseLong(text, &i)) return fire_IntU(i, unit);
        return fire_FloatU((Real)strtod(text, NULL), unit);
    }
    return jsMismatch(r, path, type, text);
}

static Value jsNewString(JsReader* r, const fire_char16* s, size_t n) { return newStrFrom(s, (uint32_t)n, r->scope); }

static Value jsFromString(JsReader* r, const fire_char16* s, size_t n, const char* type, OwnList* owner, const char* path, uint32_t unit) {
    if (type[0] == 0 || jsEq(type, "any") || jsEq(type, "string")) return jsNewString(r, s, n);
    if (jsEq(type, "char")) {
        if (n == 1) return Char(s[0]);
        return jsReadFail(r, jsCat5(r->J, path, ": expected one character, found \"", jsUtf8(r->J, s, n), "\".", ""));
    }
    if (jsEq(type, "float") || jsEq(type, "double") || jsEq(type, "float32") || jsEq(type, "float64")) {
        if (js16IsAscii(s, n, "NaN")) return fire_FloatU((Real)NAN, unit);
        if (js16IsAscii(s, n, "Infinity")) return fire_FloatU((Real)INFINITY, unit);
        if (js16IsAscii(s, n, "-Infinity")) return fire_FloatU((Real)-INFINITY, unit);
    }
    if (jsEq(type, "byte[]") || jsEq(type, "buffer")) {
        JsBytes bytes = {NULL, 0, 0};
        Buf* b;
        if (!jsFromBase64(r->J, s, n, &bytes)) return jsReadFail(r, jsCat(r->J, path, ": expected base64 bytes."));
        b = allocBuf((uint32_t)bytes.n, owner);
        b->little = 1;
        if (bytes.n) memcpy(bufBytes(b), bytes.p, bytes.n);
        return BufV(b);
    }
    return jsMismatch(r, path, type, "a string");
}

static const char* jsIndexPath(JsCtx* J, const char* path, size_t i) {
    char index[32];
    snprintf(index, sizeof index, "[%llu]", (unsigned long long)i);
    return jsCat(J, path, index);
}

/* The TArgs for these type argument texts (null when none is known). */
static TArgs jsTypeArgsFor(JsCtx* J, const char* const* args, size_t n) {
    const TArg** found;
    bool any = false;
    size_t i;
    if (n == 0) return NULL;
    found = (const TArg**)jsAlloc(J, n * sizeof(const TArg*));
    for (i = 0; i < n; i++) { found[i] = jsonTArgFor(args[i]); any = any || found[i]; }
    return any ? internTArgsN(found, (uint32_t)n) : NULL;
}

static Value jsBuildList(JsReader* r, JsNode* a, const char* type, OwnList* owner, const char* path, bool hasId, int64_t id) {
    const JsonSpecial* sp = jsonSpecial();
    const JsonClassInfo* c;
    Value list, itemsV;
    JsStrs args = {NULL, 0, 0};
    const char* element;
    size_t n, i;
    Arr* items;
    if (sp->listCls < 0) return jsReadFail(r, jsCat(r->J, path, ": the class 'List' is not known."));
    c = jsonClassAt((uint32_t)sp->listCls);
    list = newObject((uint32_t)sp->listCls, c->nfields, owner);
    jsSplitGeneric(r->J, type, &args);
    element = args.n == 1 ? args.p[0] : "";
    if (element[0]) objAt(list)->targs = jsTypeArgsFor(r->J, &element, 1);
    if (hasId) jsIdSet(r, id, list);
    n = a->nitems;
    items = allocArr((uint32_t)(n > 8 ? n : 8), &objAt(list)->owned);
    itemsV = ArrV(items);
    for (i = 0; i < n && !r->J->failed; i++) {
        JsSlot s;
        Value item;
        s.container = itemsV; s.index = (uint32_t)i; s.has = true;
        item = jsBuild(r, a->items[i], element, jsOwnerIn(itemsV), jsIndexPath(r->J, path, i), 0, s);
        jsStore(itemsV, (uint32_t)i, item);
    }
    jsStore(list, (uint32_t)sp->listItems, itemsV);
    jsStore(list, (uint32_t)sp->listCount, fire_IntU((int64_t)n, 0));
    return list;
}

static Value jsBuildArray(JsReader* r, JsNode* a, const char* type, OwnList* owner, const char* path, bool hasId, int64_t id) {
    JsCtx* J = r->J;
    if (jsEndsWith(type, "[]") || type[0] == 0 || jsEq(type, "any") || jsEq(type, "class")) {
        const char* element = jsEndsWith(type, "[]") ? jsDup(J, type, strlen(type) - 2) : "";
        Arr* arr;
        Value value;
        size_t i;
        if (jsEq(element, "byte") && !r->plain) {
            /* a byte array written as numbers: a buffer */
            uint8_t* bytes = (uint8_t*)jsAlloc(J, a->nitems);
            Buf* buf;
            Value bv;
            for (i = 0; i < a->nitems; i++) {
                JsNode* item = a->items[i];
                int64_t b;
                if (item->kind != JN_NUMBER || !jsParseLong(item->num, &b) || b < 0 || b > 255) return jsReadFail(r, jsCat(J, jsIndexPath(J, path, i), ": expected a byte."));
                bytes[i] = (uint8_t)b;
            }
            buf = allocBuf((uint32_t)a->nitems, owner);
            buf->little = 1;
            if (a->nitems) memcpy(bufBytes(buf), bytes, a->nitems);
            bv = BufV(buf);
            if (hasId) jsIdSet(r, id, bv);
            return bv;
        }
        arr = allocArr((uint32_t)a->nitems, owner);
        value = ArrV(arr);
        if (hasId) jsIdSet(r, id, value);
        for (i = 0; i < a->nitems && !J->failed; i++) {
            JsSlot s;
            Value item;
            s.container = value; s.index = (uint32_t)i; s.has = true;
            item = jsBuild(r, a->items[i], element, jsOwnerIn(value), jsIndexPath(J, path, i), 0, s);
            jsStore(value, (uint32_t)i, item);
        }
        return value;
    }
    if (jsEq(jsPlain(J, type), "List") && !r->plain) return jsBuildList(r, a, type, owner, path, hasId, id);
    return jsMismatch(r, path, type, "an array");
}

static bool jsIdOfNode(JsReader* r, JsNode* node, const char* path, int64_t* id) {
    if (node->kind == JN_NUMBER && jsParseLong(node->num, id)) return true;
    jsReadFail(r, jsCat(r->J, path, ": an id must be a whole number."));
    return false;
}

/* A class name of JSON (`Point`, `Box<int>`) as the program knows it (GenericClassNames.Mangle when the plain name is taken). */
static int32_t jsClassFor(JsCtx* J, const char* name, JsStrs* args) {
    const char* plainName = jsSplitGeneric(J, name, args);
    if (args->n > 0) {
        char count[24];
        int32_t generic;
        snprintf(count, sizeof count, "`%llu", (unsigned long long)args->n);
        generic = jsonClassNamed(jsCat(J, plainName, count));
        if (generic >= 0) return generic;
    }
    return jsonClassNamed(plainName);
}
/* The same lookup for the classes that are not serializable and not in the tables (jsonExcludedNamed): the name of the class, or null. */
static const char* jsExcludedFor(JsCtx* J, const char* name) {
    JsStrs args = {NULL, 0, 0};
    const char* plainName = jsSplitGeneric(J, name, &args);
    if (args.n > 0) {
        char count[24];
        const char* generic;
        snprintf(count, sizeof count, "`%llu", (unsigned long long)args.n);
        generic = jsonExcludedNamed(jsCat(J, plainName, count));
        if (generic) return generic;
    }
    return jsonExcludedNamed(plainName);
}
static bool jsSameIgnoreCase(const char* a, const char* b) {
    size_t i, n = strlen(a);
    if (n != strlen(b)) return false;
    for (i = 0; i < n; i++) {
        char x = a[i], y = b[i];
        if (x >= 'A' && x <= 'Z') x = (char)(x - 'A' + 'a');
        if (y >= 'A' && y <= 'Z') y = (char)(y - 'A' + 'a');
        if (x != y) return false;
    }
    return true;
}

static Value jsBuildInstance(JsReader* r, JsNode* o, const char* type, OwnList* owner, const char* path, bool hasId, int64_t id) {
    JsCtx* J = r->J;
    JsStrs args = {NULL, 0, 0};
    int32_t cls = jsClassFor(J, type, &args);
    const JsonClassInfo* c;
    Value obj;
    Obj* o2;
    size_t m;
    if (cls < 0) {
        const char* excluded = jsExcludedFor(J, type);
        if (excluded) { jsFail(J, jsCat5(J, path, ": the class '", excluded, "' cannot be read from JSON: ", JSON_NOT_SERIALIZABLE), "ArgumentException"); return Undef(); }
        return jsReadFail(r, jsCat5(J, path, ": unknown class '", type, "'.", ""));
    }
    c = jsonClassAt((uint32_t)cls);
    if (c->flags & JSON_FLAG_ACTOR) return jsReadFail(r, jsCat5(J, path, ": an actor ('", c->name, "') cannot be read from JSON.", ""));
    if (c->flags & JSON_FLAG_EXCLUDED) { jsFail(J, jsCat5(J, path, ": the class '", c->name, "' cannot be read from JSON: ", JSON_NOT_SERIALIZABLE), "ArgumentException"); return Undef(); }
    obj = newObject((uint32_t)cls, c->nfields, owner);
    o2 = objAt(obj);
    if (args.n > 0) o2->targs = jsTypeArgsFor(J, args.p, args.n);
    if (hasId) jsIdSet(r, id, obj);
    for (m = 0; m < o->nmembers; m++) {
        JsMember* member = &o->members[m];
        const JsonFieldInfo* field = NULL;
        const char* key;
        uint32_t i;
        JsSlot s;
        Value v;
        if (J->failed) break;
        if (member->klen > 0 && member->key[0] == '$') continue;
        key = jsUtf8(J, member->key, member->klen);
        for (i = 0; i < c->njson && !field; i++) if (jsEq(key, c->fields[i].name)) field = &c->fields[i];
        for (i = 0; i < c->njson && !field; i++) if (jsSameIgnoreCase(key, c->fields[i].name)) field = &c->fields[i];
        if (!field) continue;   /* (a member the class does not have: left out) */
        s.container = obj; s.index = field->slot; s.has = true;
        v = jsBuild(r, member->node, jsSubstitute(J, field->type, o2, c), &o2->owned, jsCat3(J, path, ".", key), field->unit, s);
        jsStore(obj, field->slot, v);
    }
    if (c->flags & JSON_FLAG_HOOK) {
        jsGrow(J, (void**)&r->hooks, r->nhooks, &r->capHooks, sizeof(Value));
        r->hooks[r->nhooks++] = obj;
    }
    return obj;
}

static Value jsBuildJsonObject(JsReader* r, JsNode* o, OwnList* owner, const char* path, bool hasId, int64_t id) {
    JsCtx* J = r->J;
    const JsonSpecial* sp = jsonSpecial();
    const JsonClassInfo* c;
    Value obj, keys, values;
    JsMember** members;
    size_t n = 0, i;
    uint32_t size;
    OwnList* own;
    if (sp->objCls < 0) return jsReadFail(r, jsCat(J, path, ": an object without '$type' needs a class to read into (or the class JsonObject of `#import \"json\"`)."));
    c = jsonClassAt((uint32_t)sp->objCls);
    obj = newObject((uint32_t)sp->objCls, c->nfields, owner);
    if (hasId) jsIdSet(r, id, obj);
    members = (JsMember**)jsAlloc(J, (o->nmembers ? o->nmembers : 1) * sizeof(JsMember*));
    for (i = 0; i < o->nmembers; i++) if (r->plain || o->members[i].klen == 0 || o->members[i].key[0] != '$') members[n++] = &o->members[i];
    size = (uint32_t)(n > 4 ? n : 4);
    own = &objAt(obj)->owned;
    keys = ArrV(allocArr(size, own));
    values = ArrV(allocArr(size, own));
    for (i = 0; i < n && !J->failed; i++) {
        JsSlot s;
        Value v;
        jsStore(keys, (uint32_t)i, jsNewString(r, members[i]->key, members[i]->klen));
        s.container = values; s.index = (uint32_t)i; s.has = true;
        v = jsBuild(r, members[i]->node, "", jsOwnerIn(values), jsCat3(J, path, ".", jsUtf8(J, members[i]->key, members[i]->klen)), 0, s);
        jsStore(values, (uint32_t)i, v);
    }
    jsStore(obj, (uint32_t)sp->objKeys, keys);
    jsStore(obj, (uint32_t)sp->objValues, values);
    jsStore(obj, (uint32_t)sp->objCount, fire_IntU((int64_t)n, 0));
    return obj;
}

static Value jsBuildObject(JsReader* r, JsNode* o, const char* type, OwnList* owner, const char* path, uint32_t unit, JsSlot slot) {
    JsCtx* J = r->J;
    JsNode *refNode, *idNode, *numberNode, *ch, *b64, *t, *values;
    bool hasId = false;
    int64_t id = 0;
    const char* declared;
    if (r->plain) return jsBuildJsonObject(r, o, owner, path, false, 0);
    if ((refNode = jsNodeGet(o, "$ref")) != NULL) {
        int64_t refId;
        JsIdEntry* e;
        if (!jsIdOfNode(r, refNode, path, &refId)) return Undef();
        e = jsIdFind(r, refId);
        if (e) return e->value;
        if (!slot.has) return jsReadFail(r, jsCat(J, path, ": '$ref' to an object that is not written out before."));
        jsGrow(J, (void**)&r->fixups, r->nfixups, &r->capFixups, sizeof(JsFixup));
        r->fixups[r->nfixups].container = slot.container;
        r->fixups[r->nfixups].slot = slot.index;
        r->fixups[r->nfixups].id = refId;
        r->nfixups++;
        return Undef();
    }
    if ((idNode = jsNodeGet(o, "$id")) != NULL) { if (!jsIdOfNode(r, idNode, path, &id)) return Undef(); hasId = true; }
    if ((numberNode = jsNodeGet(o, "$value")) != NULL) {
        uint32_t withUnit = unit;
        JsNode* u = jsNodeGet(o, "$unit");
        if (u && u->kind == JN_STRING) {
            const char* name = jsUtf8(J, u->str, u->slen);
            bool found = false;
            uint32_t i;
            for (i = 0; i < g_udCount; i++)
                if (strcmp(g_ud[i].name, name) == 0) { withUnit = i; found = true; break; }
            if (!found) return jsReadFail(r, jsCat5(J, path, ": unknown unit '", name, "'.", ""));
        }
        if (numberNode->kind != JN_NUMBER) return jsReadFail(r, jsCat(J, path, ": '$value' is no number."));
        return jsReadNumber(r, numberNode->num, numberNode->isInt, type, withUnit, path);
    }
    ch = jsNodeGet(o, "$char");
    if (ch && ch->kind == JN_STRING) {
        if (ch->slen == 1) return Char(ch->str[0]);
        return jsReadFail(r, jsCat(J, path, ": '$char' must be one character."));
    }
    b64 = jsNodeGet(o, "$buffer");
    if (b64 && b64->kind == JN_STRING) {
        Value v = jsFromString(r, b64->str, b64->slen, "byte[]", owner, path, 0);
        if (hasId && !J->failed) jsIdSet(r, id, v);
        return v;
    }
    t = jsNodeGet(o, "$type");
    declared = t && t->kind == JN_STRING ? jsUtf8(J, t->str, t->slen) : type;
    values = jsNodeGet(o, "$values");
    if (values && values->kind == JN_ARRAY)
        return jsBuildArray(r, values, declared[0] && jsEq(jsPlain(J, declared), "List") ? declared : type, owner, path, hasId, id);
    if (declared[0] == 0 || jsEq(declared, "any") || jsEq(declared, "class") || jsEq(declared, "JsonObject")) return jsBuildJsonObject(r, o, owner, path, hasId, id);
    return jsBuildInstance(r, o, declared, owner, path, hasId, id);
}

static Value jsBuild(JsReader* r, JsNode* node, const char* type, OwnList* owner, const char* path, uint32_t unit, JsSlot slot) {
    const char* bare;
    if (r->J->failed) return Undef();
    bare = type[0] == '#' ? type + 1 : type;
    switch (node->kind) {
        case JN_NULL: return Undef();
        case JN_BOOL:
            if (bare[0] == 0 || jsEq(bare, "bool") || jsEq(bare, "any")) return Bool(node->b);
            return jsMismatch(r, path, bare, "true/false");
        case JN_NUMBER: return jsReadNumber(r, node->num, node->isInt, bare, unit, path);
        case JN_STRING: return jsFromString(r, node->str, node->slen, bare, owner, path, unit);
        case JN_ARRAY: return jsBuildArray(r, node, bare, owner, path, false, 0);
        case JN_OBJECT: return jsBuildObject(r, node, bare, owner, path, unit, slot);
        default: return Undef();
    }
}

static Value jsRead(JsReader* r, JsNode* root, const char* type, bool plainMode) {
    JsSlot none;
    Value value;
    size_t i;
    none.container = Undef(); none.index = 0; none.has = false;
    r->plain = plainMode;
    value = jsBuild(r, root, type, r->scope, "$", 0, none);
    if (r->J->failed) return Undef();
    for (i = 0; i < r->nfixups; i++) {
        JsFixup* f = &r->fixups[i];
        JsIdEntry* e = jsIdFind(r, f->id);
        if (!e) {
            char text[96];
            snprintf(text, sizeof text, "'$ref': %lld names no object of the JSON text.", (long long)f->id);
            return jsReadFail(r, jsDup(r->J, text, strlen(text)));
        }
        jsStore(f->container, f->slot, e->value);
    }
    return value;
}

Value fire_jsonCall(int argc, Value op, Value a1, Value a2, const char* callType, OwnList* list) {
    JsCtx ctx;
    Value result = Undef();
    int64_t id = op.kind == K_Int ? op.i : 0;
    memset(&ctx, 0, sizeof ctx);
    if (id == 1) {
        JsWriter writer;
        int flags = argc > 2 && a2.kind == K_Int ? (int)a2.i : 0;
        memset(&writer, 0, sizeof writer);
        writer.J = &ctx;
        writer.indent = (flags & 1) != 0;
        writer.typeNames = (flags & 2) == 0;
        jsDiscover(&writer, a1);
        jsWriteValue(&writer, a1, "", NULL, K_Undefined, true, 0);
        if (!ctx.failed) result = newStrFrom(writer.out.p, (uint32_t)writer.out.n, list);
    } else if ((id == 2 || id == 3) && a1.kind == K_String) {
        const Str* s = strOf(a1);
        JsParser parser;
        JsNode* root;
        parser.J = &ctx; parser.text = s->data; parser.n = s->length; parser.pos = 0;
        root = jsParse(&parser);
        if (root) {
            const char* type = "";
            JsReader reader;
            Value value;
            if (id == 2) {
                if (argc > 2 && a2.kind == K_String) type = jsUtf8(&ctx, strOf(a2)->data, strOf(a2)->length);
                else if (callType) type = callType;
            }
            memset(&reader, 0, sizeof reader);
            reader.J = &ctx;
            reader.scope = list;
            value = jsRead(&reader, root, type, id == 3);
            if (!ctx.failed) {
                if (id == 3) result = value;
                else {
                    Arr* pair = allocArr(2, list);
                    Arr* hooks = allocArr((uint32_t)reader.nhooks, list);
                    size_t i;
                    arrItems(pair)[0] = value;
                    retain(value);
                    for (i = 0; i < reader.nhooks; i++) arrItems(hooks)[i] = reader.hooks[i];
                    arrItems(pair)[1] = ArrV(hooks);
                    result = ArrV(pair);
                }
            }
        }
    } else jsFail(&ctx, id == 2 || id == 3 ? "JSON text expected." : "Unknown JSON function.", "ArgumentException");
    if (ctx.failed) {
        /* (the texts live in the arena: copied before it is freed) */
        char cls[96], message[1024];
        snprintf(cls, sizeof cls, "%s", ctx.errorClass ? ctx.errorClass : "JsonException");
        snprintf(message, sizeof message, "%s", ctx.message ? ctx.message : "");
        jsFreeAll(&ctx);
        return fireError(cls, message);
    }
    jsFreeAll(&ctx);
    return result;
}

#endif /* FIRE_JSON_IMPLEMENTATION */

FIRE_END

#endif
