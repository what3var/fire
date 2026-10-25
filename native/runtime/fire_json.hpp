// JSON (docs/JSON.md, `#import "json"`) for native programs: the same as fire.Standard.JsonMethods of the VM - the same text, the same errors.
// The generator puts this file into the program when it calls `__JsonCall`; after it come the tables of the program's classes (kJson*) and the
// functions declared at the top (jsonClassAt, ...). Errors are not C++ exceptions (a board's compiler may have them switched off): the first one is
// kept in the context and every step returns early once there is one.
#include <cerrno>
#include <deque>
#include <string>
#include <unordered_map>
#include <utility>
#include <vector>

namespace fire {

/// A field as JSON sees it (JsonMethods.JsonField): the name in the JSON text, the slot in the object, the declared type (`int`, `Point`, `List<Point>`, `$0`, "" for
/// an untyped one) and the unit it is declared with (0: none).
struct JsonFieldInfo { const char* name; uint32_t slot; const char* type; uint32_t unit; };
/// A class: its name (as the program knows it, `Box`1` for a generic one whose plain name is taken), the plain name, its fields, `flags` (1 actor, 2 has
/// OnDeserialized(), 4 the List of the prelude, 8 JsonObject) and the number of its type parameters.
struct JsonClassInfo { const char* name; const char* plain; uint32_t nfields; const JsonFieldInfo* fields; uint32_t njson; uint8_t flags; uint8_t ntparams; };
/// The slots of the classes JSON fills itself (-1: not in the program).
struct JsonSpecial { int32_t listCls, listItems, listCount; int32_t objCls, objKeys, objValues, objCount; };

static const JsonClassInfo* jsonClassAt(uint32_t cls);
static int32_t jsonClassNamed(const char* name);
static const char* jsonTArgText(const TArg* t);
static const TArg* jsonTArgFor(const std::string& text);
static const JsonSpecial& jsonSpecial();

enum { JSON_FLAG_ACTOR = 1, JSON_FLAG_HOOK = 2, JSON_FLAG_LIST = 4, JSON_FLAG_OBJECT = 8 };

struct JsonCtx {
    bool failed = false;
    std::string message;
    std::string errorClass = "JsonException";
    void fail(const std::string& text, const char* cls = "JsonException") {
        if (failed) return;
        failed = true;
        message = text;
        errorClass = cls;
    }
};

// ---- text helpers -------------------------------------------------------------------------------------------------------
inline void jsonAppendUtf8(std::string& out, uint32_t c) {
    if (c < 0x80) out += (char)c;
    else if (c < 0x800) { out += (char)(0xC0 | (c >> 6)); out += (char)(0x80 | (c & 0x3F)); }
    else if (c < 0x10000) { out += (char)(0xE0 | (c >> 12)); out += (char)(0x80 | ((c >> 6) & 0x3F)); out += (char)(0x80 | (c & 0x3F)); }
    else { out += (char)(0xF0 | (c >> 18)); out += (char)(0x80 | ((c >> 12) & 0x3F)); out += (char)(0x80 | ((c >> 6) & 0x3F)); out += (char)(0x80 | (c & 0x3F)); }
}
inline std::string jsonUtf8(const char16_t* p, size_t n) {
    std::string out;
    for (size_t i = 0; i < n; i++) {
        uint32_t c = p[i];
        if (c >= 0xD800 && c < 0xDC00 && i + 1 < n && p[i + 1] >= 0xDC00 && p[i + 1] < 0xE000) { c = 0x10000 + ((c - 0xD800) << 10) + (p[i + 1] - 0xDC00); i++; }
        jsonAppendUtf8(out, c);
    }
    return out;
}
inline std::string jsonUtf8(const std::u16string& s) { return jsonUtf8(s.data(), s.size()); }
inline std::u16string jsonUtf16(const std::string& s) {
    std::u16string out;
    for (size_t i = 0; i < s.size();) {
        uint8_t c = (uint8_t)s[i];
        uint32_t cp;
        int extra;
        if (c < 0x80) { cp = c; extra = 0; }
        else if ((c & 0xE0) == 0xC0) { cp = c & 0x1F; extra = 1; }
        else if ((c & 0xF0) == 0xE0) { cp = c & 0x0F; extra = 2; }
        else { cp = c & 0x07; extra = 3; }
        i++;
        for (int k = 0; k < extra && i < s.size(); k++, i++) cp = (cp << 6) | ((uint8_t)s[i] & 0x3F);
        if (cp >= 0x10000) { cp -= 0x10000; out += (char16_t)(0xD800 + (cp >> 10)); out += (char16_t)(0xDC00 + (cp & 0x3FF)); }
        else out += (char16_t)cp;
    }
    return out;
}
inline void jsonAppendAscii(std::u16string& out, const char* text) { for (; *text; text++) out += (char16_t)(unsigned char)*text; }

/// `List<Point>` -> ("List", ["Point"]), `Point` -> ("Point", []).
inline std::string jsonSplitGeneric(const std::string& type, std::vector<std::string>* args) {
    size_t lt = type.find('<');
    if (lt == std::string::npos || type.empty() || type.back() != '>') return type;
    auto trim = [](std::string s) {
        size_t a = 0, b = s.size();
        while (a < b && s[a] == ' ') a++;
        while (b > a && s[b - 1] == ' ') b--;
        return s.substr(a, b - a);
    };
    int depth = 0;
    size_t start = lt + 1;
    for (size_t i = lt + 1; i + 1 < type.size(); i++) {
        char ch = type[i];
        if (ch == '<') depth++;
        else if (ch == '>') depth--;
        else if (ch == ',' && depth == 0) { if (args) args->push_back(trim(type.substr(start, i - start))); start = i + 1; }
    }
    if (args) args->push_back(trim(type.substr(start, type.size() - 1 - start)));
    return trim(type.substr(0, lt));
}
/// GenericClassNames.PlainName: `Box`1` -> `Box`.
inline std::string jsonPlainName(const std::string& name) {
    size_t tick = name.find('`');
    return tick == std::string::npos ? name : name.substr(0, tick);
}
inline std::string jsonPlain(const std::string& type) { return jsonPlainName(jsonSplitGeneric(type, nullptr)); }
inline bool jsonEndsWith(const std::string& s, const char* tail) { size_t n = std::strlen(tail); return s.size() >= n && s.compare(s.size() - n, n, tail) == 0; }
inline std::string jsonElementType(const std::string& type) {
    if (jsonEndsWith(type, "[]")) return type.substr(0, type.size() - 2);
    std::vector<std::string> args;
    if (jsonSplitGeneric(type, &args) == "List" && args.size() == 1) return args[0];
    return "";
}

/// The text of a type argument as the VM keeps it ("" when not known).
inline std::string jsonTArgName(const TArg* t) {
    if (!t) return "";
    const char* text = jsonTArgText(t);
    return text ? text : (t->display ? t->display : "");
}
/// The type arguments of an object (empty when it has none or one is not known).
inline std::vector<std::string> jsonTypeArgs(Obj* o, const JsonClassInfo* c) {
    std::vector<std::string> result;
    if (!o->targs || !c || c->ntparams == 0) return result;
    for (uint32_t k = 0; k < c->ntparams; k++) result.push_back(jsonTArgName(o->targs[k]));
    return result;
}
/// The type of a slot with a type parameter of the class (`$0`) replaced by the object's type argument.
inline std::string jsonSubstitute(const std::string& type, Obj* o, const JsonClassInfo* c) {
    if (type.size() < 2 || type[0] != '$') return type;
    size_t end = 1;
    while (end < type.size() && type[end] >= '0' && type[end] <= '9') end++;
    uint32_t k = (uint32_t)std::atoi(type.substr(1, end - 1).c_str());
    std::string arg = o && o->targs && c && k < c->ntparams ? jsonTArgName(o->targs[k]) : "";
    return arg.empty() ? "" : arg + type.substr(end);
}

inline const char* jsonB64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
inline std::string jsonBase64(const uint8_t* p, uint32_t n) {
    std::string out;
    for (uint32_t i = 0; i < n; i += 3) {
        uint32_t b = (uint32_t)p[i] << 16 | (i + 1 < n ? (uint32_t)p[i + 1] << 8 : 0) | (i + 2 < n ? p[i + 2] : 0);
        out += jsonB64[(b >> 18) & 63];
        out += jsonB64[(b >> 12) & 63];
        out += i + 1 < n ? jsonB64[(b >> 6) & 63] : '=';
        out += i + 2 < n ? jsonB64[b & 63] : '=';
    }
    return out;
}
/// Base64 like Convert.FromBase64String (spaces are skipped); false when the text is not base64.
inline bool jsonFromBase64(const std::u16string& s, std::vector<uint8_t>& out) {
    uint32_t acc = 0; int bits = 0, chars = 0, pad = 0;
    for (char16_t ch : s) {
        if (ch == u' ' || ch == u'\t' || ch == u'\r' || ch == u'\n') continue;
        int v;
        if (ch >= u'A' && ch <= u'Z') v = ch - u'A';
        else if (ch >= u'a' && ch <= u'z') v = ch - u'a' + 26;
        else if (ch >= u'0' && ch <= u'9') v = ch - u'0' + 52;
        else if (ch == u'+') v = 62;
        else if (ch == u'/') v = 63;
        else if (ch == u'=') { pad++; chars++; continue; }
        else return false;
        if (pad > 0) return false;
        chars++;
        acc = (acc << 6) | (uint32_t)v;
        bits += 6;
        if (bits >= 8) { bits -= 8; out.push_back((uint8_t)((acc >> bits) & 0xFF)); }
    }
    return chars % 4 == 0 && pad <= 2;
}

/// A float as JSON: the shortest text that reads back as the same number, always with a `.` or an exponent (JsonMethods.FormatFloat).
inline void jsonFloat(std::u16string& out, double d) {
    char text[48];
    int n = formatFloating<double>(d, text);
    text[n] = 0;
    jsonAppendAscii(out, text);
    if (!std::strchr(text, '.') && !std::strchr(text, 'E') && !std::strchr(text, 'e')) jsonAppendAscii(out, ".0");
}

inline void jsonString(std::u16string& out, const char16_t* p, size_t n) {
    out += u'"';
    for (size_t i = 0; i < n; i++) {
        char16_t c = p[i];
        switch (c) {
            case u'"': jsonAppendAscii(out, "\\\""); break;
            case u'\\': jsonAppendAscii(out, "\\\\"); break;
            case u'\n': jsonAppendAscii(out, "\\n"); break;
            case u'\r': jsonAppendAscii(out, "\\r"); break;
            case u'\t': jsonAppendAscii(out, "\\t"); break;
            case u'\b': jsonAppendAscii(out, "\\b"); break;
            case u'\f': jsonAppendAscii(out, "\\f"); break;
            default:
                if (c < 0x20) { char esc[8]; std::snprintf(esc, sizeof esc, "\\u%04x", (unsigned)c); jsonAppendAscii(out, esc); }
                else out += c;
        }
    }
    out += u'"';
}
inline void jsonString(std::u16string& out, const std::string& utf8) { std::u16string s = jsonUtf16(utf8); jsonString(out, s.data(), s.size()); }

// ---- writing --------------------------------------------------------------------------------------------------------------
struct JsonWriter {
    JsonCtx& ctx;
    bool indent, typeNames;
    std::u16string out;
    std::unordered_map<const void*, int> seen;
    std::vector<const void*> order;   // (the order of discovery: the ids follow it, like the VM)
    std::unordered_map<const void*, int> ids;
    std::unordered_map<const void*, bool> written;
    std::unordered_map<const void*, bool> ownerInGraph;
    int depth = 0;

    JsonWriter(JsonCtx& c, int flags) : ctx(c), indent((flags & 1) != 0), typeNames((flags & 2) == 0) {}

    static bool alive(Value v) { return leafAlive(v); }

    /// Does the container (an object or an array) own the item?
    static bool owns(const void* container, uint8_t containerKind, const Owned* item) {
        if (!item->owner) return false;
        if (containerKind == K_Class) return item->owner == &static_cast<Obj*>(const_cast<void*>(container))->owned;
        if (containerKind == K_Array) { const Arr* a = static_cast<const Arr*>(container); return a->parts && item->owner == a->parts; }
        return false;
    }

    struct Pending { Value v; const void* container; uint8_t containerKind; };

    void discover(Value start) {
        std::vector<Pending> stack;
        stack.push_back({start, nullptr, K_Undefined});
        while (!stack.empty()) {
            Pending p = stack.back();
            stack.pop_back();
            Value v = p.v;
            if (v.kind != K_Class && v.kind != K_Array && v.kind != K_Buffer) continue;
            if (!alive(v)) continue;
            const void* key = v.p;
            const Owned* item = static_cast<const Owned*>(key);
            if (p.container && owns(p.container, p.containerKind, item)) ownerInGraph[key] = true;
            auto it = seen.find(key);
            if (it != seen.end()) { it->second++; continue; }
            seen[key] = 1;
            order.push_back(key);
            if (v.kind == K_Class) {
                Obj* o = objAt(v);
                const JsonClassInfo* c = jsonClassAt(o->cls);
                if (!c) continue;
                if (c->flags & JSON_FLAG_LIST) {
                    const JsonSpecial& sp = jsonSpecial();
                    Value items = o->fields()[sp.listItems], count = o->fields()[sp.listCount];
                    if (items.kind == K_Array && alive(items)) {
                        Arr* a = arrOf(items);
                        int64_t n = count.kind == K_Int ? count.i : 0;
                        if (n > (int64_t)a->length) n = a->length;
                        for (int64_t i = n - 1; i >= 0; i--) stack.push_back({a->items()[i], a, K_Array});
                    }
                    continue;
                }
                for (int64_t i = (int64_t)c->njson - 1; i >= 0; i--) stack.push_back({o->fields()[c->fields[i].slot], o, K_Class});
            } else if (v.kind == K_Array) {
                Arr* a = arrOf(v);
                for (int64_t i = (int64_t)a->length - 1; i >= 0; i--) stack.push_back({a->items()[i], a, K_Array});
            }
        }
        int next = 1;
        for (const void* key : order) if (seen[key] > 1) ids[key] = next++;
    }

    void newLine() {
        if (!indent) return;
        out += u'\n';
        out.append((size_t)depth * 2, u' ');
    }
    void key(const std::string& name, bool& first) {
        if (!first) out += u',';
        first = false;
        newLine();
        jsonString(out, name);
        jsonAppendAscii(out, indent ? ": " : ":");
    }
    void key(const char16_t* p, size_t n, bool& first) {
        if (!first) out += u',';
        first = false;
        newLine();
        jsonString(out, p, n);
        jsonAppendAscii(out, indent ? ": " : ":");
    }
    void number(int64_t i) { char text[32]; std::snprintf(text, sizeof text, "%lld", (long long)i); jsonAppendAscii(out, text); }
    void ref(int id) { jsonAppendAscii(out, "{\"$ref\":"); number(id); out += u'}'; }

    bool canDefine(const void* key, const void* container, uint8_t containerKind) {
        if (written.count(key)) return false;
        if (ownerInGraph.count(key) && container && !owns(container, containerKind, static_cast<const Owned*>(key))) return false;
        return true;
    }

    void writeNumber(Value v, uint32_t fieldUnit) {
        bool plain = v.unit == 0 || unitIsUnitless(v.unit) || (fieldUnit != 0 && unitEq(v.unit, fieldUnit));
        if (!plain) jsonAppendAscii(out, "{\"$value\":");
        if (v.kind == K_Int) number(v.i);
        else {
            double d = (double)v.f;
            if (std::isnan(d)) jsonAppendAscii(out, "\"NaN\"");
            else if (std::isinf(d)) jsonAppendAscii(out, d > 0 ? "\"Infinity\"" : "\"-Infinity\"");
            else jsonFloat(out, d);
        }
        if (!plain) {
            jsonAppendAscii(out, ",\"$unit\":");
            jsonString(out, std::string(g_ud[v.unit].name));
            out += u'}';
        }
    }

    void writeValue(Value v, const std::string& type, const void* container, uint8_t containerKind, bool isRoot = false, uint32_t unit = 0) {
        if (ctx.failed) return;
        switch (v.kind) {
            case K_Undefined: jsonAppendAscii(out, "null"); return;
            case K_Bool: jsonAppendAscii(out, v.i ? "true" : "false"); return;
            case K_Int: case K_Float: writeNumber(v, unit); return;
            case K_String: { const Str* s = strOf(v); jsonString(out, s->data, s->length); return; }
            case K_Char: {
                char16_t c = (char16_t)v.i;
                if (type == "char") jsonString(out, &c, 1);
                else { jsonAppendAscii(out, "{\"$char\":"); jsonString(out, &c, 1); out += u'}'; }
                return;
            }
            case K_Buffer: {
                if (!alive(v)) { jsonAppendAscii(out, "null"); return; }
                Buf* b = bufOf(v);
                auto id = ids.find(b);
                if (id != ids.end() && written.count(b)) { ref(id->second); return; }
                if (id != ids.end()) written[b] = true;
                std::string b64 = jsonBase64(b->bytes(), b->length);
                if ((type == "byte[]" || type == "buffer") && id == ids.end()) { jsonString(out, b64); return; }
                out += u'{';
                if (id != ids.end()) { jsonAppendAscii(out, "\"$id\":"); number(id->second); out += u','; }
                jsonAppendAscii(out, "\"$buffer\":");
                jsonString(out, b64);
                out += u'}';
                return;
            }
            case K_Array:
                if (!alive(v)) { jsonAppendAscii(out, "null"); return; }
                writeArray(arrOf(v), jsonElementType(type), container, containerKind);
                return;
            case K_Class:
                if (!alive(v)) { jsonAppendAscii(out, "null"); return; }
                writeObject(objAt(v), type, container, containerKind, isRoot);
                return;
            default:
                jsonAppendAscii(out, "null");   // a lambda or pointer in an array (a field of that kind is left out)
                return;
        }
    }

    void writeArray(Arr* a, const std::string& elementType, const void* container, uint8_t containerKind) {
        auto id = ids.find(a);
        if (id != ids.end()) {
            if (!canDefine(a, container, containerKind)) { ref(id->second); return; }
            written[a] = true;
            jsonAppendAscii(out, "{\"$id\":");
            number(id->second);
            jsonAppendAscii(out, indent ? ", \"$values\": " : ",\"$values\":");
            writeItems(a->items(), a->length, elementType, a);
            out += u'}';
            return;
        }
        writeItems(a->items(), a->length, elementType, a);
    }

    void writeItems(const Value* items, int64_t count, const std::string& elementType, Arr* container) {
        out += u'[';
        depth++;
        for (int64_t i = 0; i < count && !ctx.failed; i++) {
            if (i > 0) out += u',';
            newLine();
            Value item = items[i];
            if (item.kind == K_Lambda || item.kind == K_Pointer) { jsonAppendAscii(out, "null"); continue; }
            writeValue(item, elementType, container, K_Array);
        }
        depth--;
        if (count > 0) newLine();
        out += u']';
    }

    std::string typeName(Obj* o, const JsonClassInfo* c) {
        std::string plain = c->plain;
        std::vector<std::string> args = jsonTypeArgs(o, c);
        if (args.empty()) return plain;
        std::string joined;
        for (size_t i = 0; i < args.size(); i++) {
            if (args[i].empty()) return plain;
            if (i) joined += ",";
            joined += args[i];
        }
        return plain + "<" + joined + ">";
    }

    void writeObject(Obj* o, const std::string& type, const void* container, uint8_t containerKind, bool isRoot) {
        const JsonClassInfo* c = jsonClassAt(o->cls);
        if (!c) { jsonAppendAscii(out, "null"); return; }
        if ((c->flags & JSON_FLAG_ACTOR) || (o->flags & 8)) { ctx.fail(std::string("An actor ('") + c->name + "') cannot be written to JSON."); return; }
        auto idIt = ids.find(o);
        bool shared = idIt != ids.end();
        int id = shared ? idIt->second : 0;
        if (shared && !(isRoot || canDefine(o, container, containerKind))) { ref(id); return; }
        if (shared) written[o] = true;
        std::vector<std::string> typeArgs;
        jsonSplitGeneric(type, &typeArgs);
        bool withType = typeNames && (isRoot || type.empty() || jsonPlain(type) != std::string(c->plain)
            || (typeArgs.empty() && o->targs && c->ntparams > 0));
        if (c->flags & JSON_FLAG_OBJECT) withType = false;
        const JsonSpecial& sp = jsonSpecial();

        if (c->flags & JSON_FLAG_LIST) {
            Value items = o->fields()[sp.listItems], countV = o->fields()[sp.listCount];
            int64_t count = countV.kind == K_Int ? countV.i : 0;
            std::vector<std::string> own = jsonTypeArgs(o, c);
            std::string elementType = own.size() == 1 ? own[0] : jsonElementType(type);
            Arr* a = items.kind == K_Array && alive(items) ? arrOf(items) : nullptr;
            if (!a) count = 0;
            else if (count > (int64_t)a->length) count = a->length;
            if (!shared && !withType) { writeItems(a ? a->items() : nullptr, count, elementType, a); return; }
            out += u'{';
            bool firstL = true;
            depth++;
            if (withType) { key("$type", firstL); jsonString(out, typeName(o, c)); }
            if (shared) { key("$id", firstL); number(id); }
            key("$values", firstL);
            writeItems(a ? a->items() : nullptr, count, elementType, a);
            depth--;
            newLine();
            out += u'}';
            return;
        }

        out += u'{';
        bool first = true;
        depth++;
        if (withType) { key("$type", first); jsonString(out, typeName(o, c)); }
        if (shared) { key("$id", first); number(id); }
        if (c->flags & JSON_FLAG_OBJECT) {
            Value keys = o->fields()[sp.objKeys], values = o->fields()[sp.objValues], countV = o->fields()[sp.objCount];
            int64_t n = countV.kind == K_Int ? countV.i : 0;
            if (keys.kind == K_Array && values.kind == K_Array && alive(keys) && alive(values)) {
                Arr* ka = arrOf(keys);
                Arr* va = arrOf(values);
                for (int64_t i = 0; i < n && i < (int64_t)ka->length && i < (int64_t)va->length && !ctx.failed; i++) {
                    Value k = ka->items()[i];
                    if (k.kind == K_String) { const Str* s = strOf(k); key(s->data, s->length, first); }
                    else key(std::string(""), first);
                    writeValue(va->items()[i], "", va, K_Array);
                }
            }
        } else {
            for (uint32_t i = 0; i < c->njson && !ctx.failed; i++) {
                const JsonFieldInfo& f = c->fields[i];
                Value value = o->fields()[f.slot];
                if (value.kind == K_Lambda || value.kind == K_Pointer) continue;
                key(std::string(f.name), first);
                writeValue(value, jsonSubstitute(f.type, o, c), o, K_Class, false, f.unit);
            }
        }
        depth--;
        if (!first) newLine();
        out += u'}';
    }
};

// ---- parsing (the same parser and messages as JsonMethods.JsonParser) ---------------------------------------------------------
struct JsonNode {
    enum Kind : uint8_t { Null, Bool, Number, String, Array, Object } kind = Null;
    bool b = false;
    bool isInt = false;
    std::string num;
    std::u16string str;
    std::vector<JsonNode*> items;
    std::vector<std::pair<std::u16string, JsonNode*>> members;
    JsonNode* get(const char* key) {
        std::u16string k = jsonUtf16(key);
        for (auto& m : members) if (m.first == k) return m.second;
        return nullptr;
    }
};

struct JsonParser {
    JsonCtx& ctx;
    const char16_t* text;
    size_t n;
    size_t pos = 0;
    std::deque<JsonNode> nodes;

    JsonParser(JsonCtx& c, const char16_t* t, size_t len) : ctx(c), text(t), n(len) {}

    JsonNode* make(JsonNode::Kind kind) { nodes.emplace_back(); nodes.back().kind = kind; return &nodes.back(); }

    JsonNode* fail(size_t at, const std::string& what) {
        int line = 1, column = 1;
        for (size_t i = 0; i < at && i < n; i++) { if (text[i] == u'\n') { line++; column = 1; } else column++; }
        char where[64];
        std::snprintf(where, sizeof where, " (line %d, column %d).", line, column);
        ctx.fail(what + where);
        return nullptr;
    }

    void skipSpace() { while (pos < n && (text[pos] == u' ' || text[pos] == u'\t' || text[pos] == u'\n' || text[pos] == u'\r')) pos++; }
    bool matches(const char* word) {
        size_t len = std::strlen(word);
        if (pos + len > n) return false;
        for (size_t i = 0; i < len; i++) if (text[pos + i] != (char16_t)word[i]) return false;
        return true;
    }
    static bool digit(char16_t c) { return c >= u'0' && c <= u'9'; }

    JsonNode* parse() {
        JsonNode* node = value(0);
        if (!node) return nullptr;
        skipSpace();
        if (pos < n) return fail(pos, "Unexpected text after the JSON value");
        return node;
    }

    JsonNode* value(int depth) {
        if (depth > 512) return fail(pos, "JSON nested too deeply");
        skipSpace();
        if (pos >= n) return fail(pos, "Unexpected end of the JSON text");
        char16_t c = text[pos];
        switch (c) {
            case u'{': {
                pos++;
                JsonNode* obj = make(JsonNode::Object);
                skipSpace();
                if (pos < n && text[pos] == u'}') { pos++; return obj; }
                while (true) {
                    skipSpace();
                    if (pos >= n || text[pos] != u'"') return fail(pos, "Expected a name in quotes");
                    std::u16string key;
                    if (!string(key)) return nullptr;
                    skipSpace();
                    if (pos >= n || text[pos] != u':') return fail(pos, "Expected ':' after a name");
                    pos++;
                    JsonNode* item = value(depth + 1);
                    if (!item) return nullptr;
                    obj->members.emplace_back(std::move(key), item);
                    skipSpace();
                    if (pos < n && text[pos] == u',') { pos++; continue; }
                    if (pos < n && text[pos] == u'}') { pos++; return obj; }
                    return fail(pos, "Expected ',' or '}' in an object");
                }
            }
            case u'[': {
                pos++;
                JsonNode* array = make(JsonNode::Array);
                skipSpace();
                if (pos < n && text[pos] == u']') { pos++; return array; }
                while (true) {
                    JsonNode* item = value(depth + 1);
                    if (!item) return nullptr;
                    array->items.push_back(item);
                    skipSpace();
                    if (pos < n && text[pos] == u',') { pos++; continue; }
                    if (pos < n && text[pos] == u']') { pos++; return array; }
                    return fail(pos, "Expected ',' or ']' in an array");
                }
            }
            case u'"': {
                JsonNode* s = make(JsonNode::String);
                if (!string(s->str)) return nullptr;
                return s;
            }
            default: break;
        }
        if (c == u't' && matches("true")) { pos += 4; JsonNode* b = make(JsonNode::Bool); b->b = true; return b; }
        if (c == u'f' && matches("false")) { pos += 5; return make(JsonNode::Bool); }
        if (c == u'n' && matches("null")) { pos += 4; return make(JsonNode::Null); }
        if (c == u'-' || digit(c)) {
            size_t start = pos;
            bool isInt = true;
            if (text[pos] == u'-') pos++;
            while (pos < n && digit(text[pos])) pos++;
            if (pos < n && text[pos] == u'.') { isInt = false; pos++; while (pos < n && digit(text[pos])) pos++; }
            if (pos < n && (text[pos] == u'e' || text[pos] == u'E')) {
                isInt = false;
                pos++;
                if (pos < n && (text[pos] == u'+' || text[pos] == u'-')) pos++;
                while (pos < n && digit(text[pos])) pos++;
            }
            std::string number;
            for (size_t i = start; i < pos; i++) number += (char)text[i];
            if (number == "-" || number.empty()) return fail(start, "Invalid number");
            JsonNode* node = make(JsonNode::Number);
            node->num = number;
            node->isInt = isInt;
            return node;
        }
        std::string what = "Unexpected character '";
        jsonAppendUtf8(what, c);
        return fail(pos, what + "'");
    }

    bool string(std::u16string& out) {
        size_t start = pos;
        pos++;   // '"'
        while (true) {
            if (pos >= n) { fail(start, "Unterminated string"); return false; }
            char16_t c = text[pos++];
            if (c == u'"') return true;
            if (c != u'\\') { out += c; continue; }
            if (pos >= n) { fail(start, "Unterminated string"); return false; }
            char16_t e = text[pos++];
            switch (e) {
                case u'"': out += u'"'; break;
                case u'\\': out += u'\\'; break;
                case u'/': out += u'/'; break;
                case u'n': out += u'\n'; break;
                case u'r': out += u'\r'; break;
                case u't': out += u'\t'; break;
                case u'b': out += u'\b'; break;
                case u'f': out += u'\f'; break;
                case u'u': {
                    uint32_t code = 0;
                    bool ok = pos + 4 <= n;
                    for (size_t i = 0; ok && i < 4; i++) {
                        char16_t h = text[pos + i];
                        int v = h >= u'0' && h <= u'9' ? h - u'0' : h >= u'a' && h <= u'f' ? h - u'a' + 10 : h >= u'A' && h <= u'F' ? h - u'A' + 10 : -1;
                        if (v < 0) ok = false; else code = code * 16 + (uint32_t)v;
                    }
                    if (!ok) { fail(pos, "Invalid \\u escape"); return false; }
                    out += (char16_t)code;
                    pos += 4;
                    break;
                }
                default: {
                    std::string what = "Invalid escape '\\";
                    jsonAppendUtf8(what, e);
                    fail(pos - 1, what + "'");
                    return false;
                }
            }
        }
    }
};

// ---- reading: the parsed tree into fire values --------------------------------------------------------------------------------
struct JsonReader {
    JsonCtx& ctx;
    OwnList* scope;   // the temporaries (strings) and the owner of the root
    bool plain = false;
    std::unordered_map<int64_t, Value> byId;
    struct Fixup { Value container; uint32_t slot; int64_t id; };
    std::vector<Fixup> fixups;
    std::vector<Value> hooks;

    JsonReader(JsonCtx& c, OwnList* list) : ctx(c), scope(list) {}

    struct Slot { Value container; uint32_t index; bool has; };

    Value fail(const std::string& text) { ctx.fail(text); return Undef(); }

    /// The list that owns what is made inside a container: an object's own list, an array's parts.
    static OwnList* ownerIn(Value container) {
        if (container.kind == K_Class) return &objAt(container)->owned;
        Arr* a = arrOf(container);
        if (!a->parts) a->parts = newPartsList(a);
        return a->parts;
    }
    static void store(Value container, uint32_t slot, Value v) {
        Value* target = container.kind == K_Class ? &objAt(container)->fields()[slot] : &arrOf(container)->items()[slot];
        release(*target);
        *target = v;
        retain(v);
    }

    Value run(JsonNode* root, const std::string& type, bool plainMode) {
        plain = plainMode;
        Value value = build(root, type, scope, "$", 0, Slot{Undef(), 0, false});
        if (ctx.failed) return Undef();
        for (const Fixup& f : fixups) {
            auto it = byId.find(f.id);
            if (it == byId.end()) {
                char text[96];
                std::snprintf(text, sizeof text, "'$ref': %lld names no object of the JSON text.", (long long)f.id);
                return fail(text);
            }
            store(f.container, f.slot, it->second);
        }
        return value;
    }

    static std::string describe(const std::string& type) {
        if (type.empty()) return "a value";
        if (jsonEndsWith(type, "[]")) return "an array";
        return "'" + type + "'";
    }
    Value mismatch(const std::string& path, const std::string& type, const std::string& found) {
        return fail(path + ": expected " + describe(type) + ", found " + found + ".");
    }

    Value build(JsonNode* node, const std::string& type, OwnList* owner, const std::string& path, uint32_t unit, Slot slot) {
        if (ctx.failed) return Undef();
        std::string bare = !type.empty() && type[0] == '#' ? type.substr(1) : type;
        switch (node->kind) {
            case JsonNode::Null: return Undef();
            case JsonNode::Bool:
                if (bare.empty() || bare == "bool" || bare == "any") return Bool(node->b);
                return mismatch(path, bare, "true/false");
            case JsonNode::Number: return number(node->num, node->isInt, bare, unit, path);
            case JsonNode::String: return fromString(node->str, bare, owner, path, unit);
            case JsonNode::Array: return buildArray(node, bare, owner, path, false, 0);
            case JsonNode::Object: return buildObject(node, bare, owner, path, unit, slot);
        }
        return Undef();
    }

    static bool parseLong(const std::string& text, int64_t& out) {
        if (text.empty()) return false;
        errno = 0;
        char* end = nullptr;
        long long v = std::strtoll(text.c_str(), &end, 10);
        if (errno != 0 || !end || *end) return false;
        out = v;
        return true;
    }
    static double parseDouble(const std::string& text) { return std::strtod(text.c_str(), nullptr); }

    Value number(const std::string& text, bool isInt, const std::string& type, uint32_t unit, const std::string& path) {
        static const char* const ints[] = {"int", "byte", "long", "short", "int8", "int16", "int32", "int64", "uint8", "uint16", "uint32", "uint64"};
        static const char* const floats[] = {"float", "double", "float32", "float64"};
        for (const char* t : ints)
            if (type == t) {
                int64_t i;
                if (isInt && parseLong(text, i)) return Int(i, unit);
                double d = parseDouble(text);
                if (d == std::floor(d) && std::fabs(d) < 9.2e18) return Int((int64_t)d, unit);
                return fail(path + ": expected a whole number, found " + text + ".");
            }
        for (const char* t : floats) if (type == t) return Float((Real)parseDouble(text), unit);
        if (type.empty() || type == "any") {
            int64_t l;
            if (isInt && parseLong(text, l)) return Int(l, unit);
            return Float((Real)parseDouble(text), unit);
        }
        return mismatch(path, type, text);
    }

    Value newString(const std::u16string& s) { return newStrFrom(s.data(), (uint32_t)s.size(), scope); }

    Value fromString(const std::u16string& s, const std::string& type, OwnList* owner, const std::string& path, uint32_t unit) {
        if (type.empty() || type == "any" || type == "string") return newString(s);
        if (type == "char") {
            if (s.size() == 1) return Char(s[0]);
            return fail(path + ": expected one character, found \"" + jsonUtf8(s) + "\".");
        }
        if (type == "float" || type == "double" || type == "float32" || type == "float64") {
            if (s == u"NaN") return Float((Real)NAN, unit);
            if (s == u"Infinity") return Float((Real)INFINITY, unit);
            if (s == u"-Infinity") return Float((Real)-INFINITY, unit);
        }
        if (type == "byte[]" || type == "buffer") {
            std::vector<uint8_t> bytes;
            if (!jsonFromBase64(s, bytes)) return fail(path + ": expected base64 bytes.");
            Buf* b = allocBuf((uint32_t)bytes.size(), owner);
            b->little = 1;
            if (!bytes.empty()) std::memcpy(b->bytes(), bytes.data(), bytes.size());
            return BufV(b);
        }
        return mismatch(path, type, "a string");
    }

    static std::string indexPath(const std::string& path, size_t i) { return path + "[" + std::to_string(i) + "]"; }

    Value buildArray(JsonNode* a, const std::string& type, OwnList* owner, const std::string& path, bool hasId, int64_t id) {
        if (jsonEndsWith(type, "[]") || type.empty() || type == "any" || type == "class") {
            std::string element = jsonEndsWith(type, "[]") ? type.substr(0, type.size() - 2) : "";
            if (element == "byte" && !plain) {
                // a byte array written as numbers: a buffer
                std::vector<uint8_t> bytes(a->items.size());
                for (size_t i = 0; i < bytes.size(); i++) {
                    JsonNode* item = a->items[i];
                    int64_t b;
                    if (item->kind != JsonNode::Number || !parseLong(item->num, b) || b < 0 || b > 255) return fail(indexPath(path, i) + ": expected a byte.");
                    bytes[i] = (uint8_t)b;
                }
                Buf* buf = allocBuf((uint32_t)bytes.size(), owner);
                buf->little = 1;
                if (!bytes.empty()) std::memcpy(buf->bytes(), bytes.data(), bytes.size());
                Value bv = BufV(buf);
                if (hasId) byId[id] = bv;
                return bv;
            }
            Arr* arr = allocArr((uint32_t)a->items.size(), owner);
            Value value = ArrV(arr);
            if (hasId) byId[id] = value;
            for (size_t i = 0; i < a->items.size() && !ctx.failed; i++) {
                Value item = build(a->items[i], element, ownerIn(value), indexPath(path, i), 0, Slot{value, (uint32_t)i, true});
                store(value, (uint32_t)i, item);
            }
            return value;
        }
        if (jsonPlain(type) == "List" && !plain) return buildList(a, type, owner, path, hasId, id);
        return mismatch(path, type, "an array");
    }

    /// The TArgs for these type argument texts (nullptr when none is known).
    static TArgs typeArgsFor(const std::vector<std::string>& args) {
        if (args.empty()) return nullptr;
        std::vector<const TArg*> found;
        bool any = false;
        for (const std::string& a : args) { const TArg* t = jsonTArgFor(a); found.push_back(t); any = any || t; }
        return any ? internTArgsN(found.data(), (uint32_t)found.size()) : nullptr;
    }

    Value buildList(JsonNode* a, const std::string& type, OwnList* owner, const std::string& path, bool hasId, int64_t id) {
        const JsonSpecial& sp = jsonSpecial();
        if (sp.listCls < 0) return fail(path + ": the class 'List' is not known.");
        const JsonClassInfo* c = jsonClassAt((uint32_t)sp.listCls);
        Value list = newObject((uint32_t)sp.listCls, c->nfields, owner);
        std::vector<std::string> args;
        jsonSplitGeneric(type, &args);
        std::string element = args.size() == 1 ? args[0] : "";
        if (!element.empty()) objAt(list)->targs = typeArgsFor({element});
        if (hasId) byId[id] = list;
        size_t n = a->items.size();
        Arr* items = allocArr((uint32_t)(n > 8 ? n : 8), &objAt(list)->owned);
        Value itemsV = ArrV(items);
        for (size_t i = 0; i < n && !ctx.failed; i++) {
            Value item = build(a->items[i], element, ownerIn(itemsV), indexPath(path, i), 0, Slot{itemsV, (uint32_t)i, true});
            store(itemsV, (uint32_t)i, item);
        }
        store(list, (uint32_t)sp.listItems, itemsV);
        store(list, (uint32_t)sp.listCount, Int((int64_t)n));
        return list;
    }

    bool idOf(JsonNode* node, const std::string& path, int64_t& id) {
        if (node->kind == JsonNode::Number && parseLong(node->num, id)) return true;
        fail(path + ": an id must be a whole number.");
        return false;
    }

    Value buildObject(JsonNode* o, const std::string& type, OwnList* owner, const std::string& path, uint32_t unit, Slot slot) {
        if (plain) return buildJsonObject(o, owner, path, false, 0);
        if (JsonNode* refNode = o->get("$ref")) {
            int64_t refId;
            if (!idOf(refNode, path, refId)) return Undef();
            auto it = byId.find(refId);
            if (it != byId.end()) return it->second;
            if (!slot.has) return fail(path + ": '$ref' to an object that is not written out before.");
            fixups.push_back({slot.container, slot.index, refId});
            return Undef();
        }
        bool hasId = false;
        int64_t id = 0;
        if (JsonNode* idNode = o->get("$id")) { if (!idOf(idNode, path, id)) return Undef(); hasId = true; }
        if (JsonNode* numberNode = o->get("$value")) {
            uint32_t withUnit = unit;
            JsonNode* u = o->get("$unit");
            if (u && u->kind == JsonNode::String) {
                std::string name = jsonUtf8(u->str);
                bool found = false;
                for (uint32_t i = 0; i < g_udCount; i++)
                    if (std::strcmp(g_ud[i].name, name.c_str()) == 0) { withUnit = i; found = true; break; }
                if (!found) return fail(path + ": unknown unit '" + name + "'.");
            }
            if (numberNode->kind != JsonNode::Number) return fail(path + ": '$value' is no number.");
            return number(numberNode->num, numberNode->isInt, type, withUnit, path);
        }
        JsonNode* ch = o->get("$char");
        if (ch && ch->kind == JsonNode::String) {
            if (ch->str.size() == 1) return Char(ch->str[0]);
            return fail(path + ": '$char' must be one character.");
        }
        JsonNode* b64 = o->get("$buffer");
        if (b64 && b64->kind == JsonNode::String) {
            Value v = fromString(b64->str, "byte[]", owner, path, 0);
            if (hasId && !ctx.failed) byId[id] = v;
            return v;
        }
        JsonNode* t = o->get("$type");
        std::string declared = t && t->kind == JsonNode::String ? jsonUtf8(t->str) : type;
        JsonNode* values = o->get("$values");
        if (values && values->kind == JsonNode::Array)
            return buildArray(values, !declared.empty() && jsonPlain(declared) == "List" ? declared : type, owner, path, hasId, id);
        if (declared.empty() || declared == "any" || declared == "class" || declared == "JsonObject") return buildJsonObject(o, owner, path, hasId, id);
        return buildInstance(o, declared, owner, path, hasId, id);
    }

    /// A class name of JSON (`Point`, `Box<int>`) as the program knows it (GenericClassNames.Mangle when the plain name is taken).
    static int32_t classFor(const std::string& name, std::vector<std::string>& args) {
        std::string plainName = jsonSplitGeneric(name, &args);
        if (!args.empty()) {
            std::string mangled = plainName + "`" + std::to_string(args.size());
            int32_t generic = jsonClassNamed(mangled.c_str());
            if (generic >= 0) return generic;
        }
        return jsonClassNamed(plainName.c_str());
    }

    static bool sameIgnoreCase(const std::string& a, const std::string& b) {
        if (a.size() != b.size()) return false;
        for (size_t i = 0; i < a.size(); i++) {
            char x = a[i], y = b[i];
            if (x >= 'A' && x <= 'Z') x = (char)(x - 'A' + 'a');
            if (y >= 'A' && y <= 'Z') y = (char)(y - 'A' + 'a');
            if (x != y) return false;
        }
        return true;
    }

    Value buildInstance(JsonNode* o, const std::string& type, OwnList* owner, const std::string& path, bool hasId, int64_t id) {
        std::vector<std::string> args;
        int32_t cls = classFor(type, args);
        if (cls < 0) return fail(path + ": unknown class '" + type + "'.");
        const JsonClassInfo* c = jsonClassAt((uint32_t)cls);
        if (c->flags & JSON_FLAG_ACTOR) return fail(path + ": an actor ('" + c->name + "') cannot be read from JSON.");
        Value obj = newObject((uint32_t)cls, c->nfields, owner);
        Obj* o2 = objAt(obj);
        if (!args.empty()) o2->targs = typeArgsFor(args);
        if (hasId) byId[id] = obj;
        for (auto& member : o->members) {
            if (ctx.failed) break;
            if (!member.first.empty() && member.first[0] == u'$') continue;
            std::string key = jsonUtf8(member.first);
            const JsonFieldInfo* field = nullptr;
            for (uint32_t i = 0; i < c->njson && !field; i++) if (key == c->fields[i].name) field = &c->fields[i];
            for (uint32_t i = 0; i < c->njson && !field; i++) if (sameIgnoreCase(key, c->fields[i].name)) field = &c->fields[i];
            if (!field) continue;   // (a member the class does not have: left out)
            Value v = build(member.second, jsonSubstitute(field->type, o2, c), &o2->owned, path + "." + key, field->unit, Slot{obj, field->slot, true});
            store(obj, field->slot, v);
        }
        if (c->flags & JSON_FLAG_HOOK) hooks.push_back(obj);
        return obj;
    }

    Value buildJsonObject(JsonNode* o, OwnList* owner, const std::string& path, bool hasId, int64_t id) {
        const JsonSpecial& sp = jsonSpecial();
        if (sp.objCls < 0) return fail(path + ": an object without '$type' needs a class to read into (or the class JsonObject of `#import \"json\"`).");
        const JsonClassInfo* c = jsonClassAt((uint32_t)sp.objCls);
        Value obj = newObject((uint32_t)sp.objCls, c->nfields, owner);
        if (hasId) byId[id] = obj;
        std::vector<std::pair<std::u16string, JsonNode*>*> members;
        for (auto& m : o->members) if (plain || m.first.empty() || m.first[0] != u'$') members.push_back(&m);
        uint32_t size = (uint32_t)(members.size() > 4 ? members.size() : 4);
        OwnList* own = &objAt(obj)->owned;
        Value keys = ArrV(allocArr(size, own));
        Value values = ArrV(allocArr(size, own));
        for (size_t i = 0; i < members.size() && !ctx.failed; i++) {
            store(keys, (uint32_t)i, newString(members[i]->first));
            Value v = build(members[i]->second, "", ownerIn(values), path + "." + jsonUtf8(members[i]->first), 0, Slot{values, (uint32_t)i, true});
            store(values, (uint32_t)i, v);
        }
        store(obj, (uint32_t)sp.objKeys, keys);
        store(obj, (uint32_t)sp.objValues, values);
        store(obj, (uint32_t)sp.objCount, Int((int64_t)members.size()));
        return obj;
    }
};

/// `__JsonCall(id, ...)`: 1 Serialize(value, flags), 2 Deserialize(text[, typeName]) -> [value, objects with OnDeserialized], 3 Parse(text).
/// `callType` is the type argument of the generic method that calls it (`Json.Deserialize<Person>`), null when there is none.
inline Value jsonCall(int argc, Value op, Value a1, Value a2, const char* callType, OwnList* list) {
    JsonCtx ctx;
    Value result = Undef();
    int64_t id = op.kind == K_Int ? op.i : 0;
    if (id == 1) {
        JsonWriter writer(ctx, argc > 2 && a2.kind == K_Int ? (int)a2.i : 0);
        writer.discover(a1);
        writer.writeValue(a1, "", nullptr, K_Undefined, true);
        if (!ctx.failed) result = newStrFrom(writer.out.data(), (uint32_t)writer.out.size(), list);
    } else if ((id == 2 || id == 3) && a1.kind == K_String) {
        const Str* s = strOf(a1);
        JsonParser parser(ctx, s->data, s->length);
        JsonNode* root = parser.parse();
        if (root) {
            std::string type;
            if (id == 2) {
                if (argc > 2 && a2.kind == K_String) type = jsonUtf8(strOf(a2)->data, strOf(a2)->length);
                else if (callType) type = callType;
            }
            JsonReader reader(ctx, list);
            Value value = reader.run(root, type, id == 3);
            if (!ctx.failed) {
                if (id == 3) result = value;
                else {
                    Arr* pair = allocArr(2, list);
                    pair->items()[0] = value;
                    retain(value);
                    Arr* hooks = allocArr((uint32_t)reader.hooks.size(), list);
                    for (size_t i = 0; i < reader.hooks.size(); i++) hooks->items()[i] = reader.hooks[i];
                    pair->items()[1] = ArrV(hooks);
                    result = ArrV(pair);
                }
            }
        }
    } else ctx.fail(id == 2 || id == 3 ? "JSON text expected." : "Unknown JSON function.", "ArgumentException");
    if (ctx.failed) return fireError(ctx.errorClass.c_str(), ctx.message.c_str());
    return result;
}

}  // namespace fire
