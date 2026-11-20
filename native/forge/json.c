/* native/forge/json.c - what the markup generators use of System.Text.Json: JsonDocument.Parse with AllowTrailingCommas and comments skipped (the
 * reader's errors as JsonReaderException's message, with its LineNumber and BytePositionInLine), the start of every token (Utf8JsonReader.
 * TokenStartIndex: DataModelGenerator.LineIndex), TryGetProperty (the last property of a name wins, as JsonDocument), TryGetInt32, TryGetDouble,
 * and JsonSerializer.Serialize of an element (compact, JavaScriptEncoder.UnsafeRelaxedJsonEscaping) and of a string (the default encoder).
 * src/fire.UI.Markup/BiscuitGenerator.cs and DataModelGenerator.cs (commit 7a594769) read their files with it. Not a port of .NET's reader
 * (not C# of this repository) but its behaviour. The JSON is read as UTF-8 (JsonDocument.Parse(string) transcodes); strings are fc8. */

typedef enum FjKind { FJK_OBJECT, FJK_ARRAY, FJK_STRING, FJK_NUMBER, FJK_TRUE, FJK_FALSE, FJK_NULL } FjKind;

typedef struct FjValue FjValue;
struct FjValue {
    FjKind kind;
    const char* text;     /* a string: its value (fc8); a number: as written */
    FcList* names;        /* an object: the names of the properties (fc8), parallel to `items` */
    FcList* items;        /* an object: the values; an array: the elements */
    size_t start;         /* the byte where the token starts */
};

typedef struct FjError { const char* message; int64_t line; int64_t pos; } FjError;

typedef struct FjReader {
    FcCtx* c;
    const unsigned char* s;
    size_t n, i;
    int64_t line;         /* 0-based, as the reader counts */
    size_t lineStart;     /* the byte after the last \n */
    int depth;
    jmp_buf fail;
    FjError error;
    FcList* tokens;       /* (DataModelGenerator.LineIndex) the tokens in order: FjToken* */
} FjReader;

/* a token as Utf8JsonReader.Read gives them, for LineIndex */
typedef enum FjTokenType { FJT_START_OBJECT, FJT_END_OBJECT, FJT_START_ARRAY, FJT_END_ARRAY, FJT_PROPERTY, FJT_VALUE } FjTokenType;
typedef struct FjToken { FjTokenType type; size_t start; const char* name; } FjToken;

static const char* fjPrintable(FcCtx* c, unsigned char b) { return b >= 0x20 && b < 0x7F ? fcFmt(c, "%c", b) : fcFmt(c, "0x%02X", b); }

static _Noreturn void fjFailAt(FjReader* r, size_t at, const char* message) {
    size_t k;
    int64_t line = 0;
    size_t ls = 0;
    for (k = 0; k < at && k < r->n; k++) if (r->s[k] == '\n') { line++; ls = k + 1; }
    r->error.message = fcFmt(r->c, "%s LineNumber: %" PRId64 " | BytePositionInLine: %" PRId64 ".", message, line, (int64_t)(at - ls));
    r->error.line = line;
    r->error.pos = (int64_t)(at - ls);
    longjmp(r->fail, 1);
}

static void fjToken(FjReader* r, FjTokenType type, size_t start, const char* name) {
    FjToken* t;
    if (!r->tokens) return;
    t = (FjToken*)fcAlloc(r->c, sizeof *t);
    t->type = type;
    t->start = start;
    t->name = name;
    fcListAdd(r->c, r->tokens, t);
}

/* white space and comments (CommentHandling.Skip) */
static void fjSkip(FjReader* r) {
    for (;;) {
        while (r->i < r->n && (r->s[r->i] == ' ' || r->s[r->i] == '\t' || r->s[r->i] == '\n' || r->s[r->i] == '\r')) r->i++;
        if (r->i < r->n && r->s[r->i] == '/') {
            size_t at = r->i;
            if (r->i + 1 >= r->n) fjFailAt(r, at, "'/' is an invalid start of a value.");
            if (r->s[r->i + 1] == '/') {
                r->i += 2;
                while (r->i < r->n && r->s[r->i] != '\n' && r->s[r->i] != '\r') r->i++;
                continue;
            }
            if (r->s[r->i + 1] == '*') {
                size_t k = r->i + 2;
                while (k + 1 < r->n && !(r->s[k] == '*' && r->s[k + 1] == '/')) k++;
                if (k + 1 >= r->n) fjFailAt(r, at, "Expected end of comment, but instead reached end of data.");
                r->i = k + 2;
                continue;
            }
            fjFailAt(r, at, "'/' is an invalid start of a value.");
        }
        return;
    }
}

static int fjHex(unsigned char b) { return b >= '0' && b <= '9' ? b - '0' : b >= 'a' && b <= 'f' ? b - 'a' + 10 : b >= 'A' && b <= 'F' ? b - 'A' + 10 : -1; }

/* a string (the reader stands on '"'): its value as fc8 */
static const char* fjString(FjReader* r) {
    FcBuf b = {0};
    fcBufStr(&b, "");
    r->i++;
    for (;;) {
        unsigned char ch;
        if (r->i >= r->n) { fcBufFree(&b); fjFailAt(r, r->n, "Expected end of string, but instead reached end of data."); }
        ch = r->s[r->i];
        if (ch == '"') { r->i++; break; }
        if (ch < 0x20) { fcBufFree(&b); fjFailAt(r, r->i, fcFmt(r->c, "'%s' is invalid within a JSON string. The string should be correctly escaped.", fjPrintable(r->c, ch))); }
        if (ch == '\\') {
            unsigned char e;
            if (r->i + 1 >= r->n) { fcBufFree(&b); fjFailAt(r, r->n, "Expected end of string, but instead reached end of data."); }
            e = r->s[r->i + 1];
            r->i += 2;
            switch (e) {
                case '"': fcBufChar(&b, '"'); break;
                case '\\': fcBufChar(&b, '\\'); break;
                case '/': fcBufChar(&b, '/'); break;
                case 'b': fcBufChar(&b, '\b'); break;
                case 'f': fcBufChar(&b, '\f'); break;
                case 'n': fcBufChar(&b, '\n'); break;
                case 'r': fcBufChar(&b, '\r'); break;
                case 't': fcBufChar(&b, '\t'); break;
                case 'u': {
                    int k, v = 0;
                    for (k = 0; k < 4; k++) {
                        int h;
                        if (r->i + (size_t)k >= r->n) { fcBufFree(&b); fjFailAt(r, r->n, "Expected end of string, but instead reached end of data."); }
                        h = fjHex(r->s[r->i + (size_t)k]);
                        if (h < 0) { fcBufFree(&b); fjFailAt(r, r->i + (size_t)k, fcFmt(r->c, "'%s' is not a hex digit following '\\u' within a JSON string. The string should be correctly escaped.", fjPrintable(r->c, r->s[r->i + (size_t)k]))); }
                        v = v * 16 + h;
                    }
                    r->i += 4;
                    fcBufUnit(&b, (uint16_t)v);
                    break;
                }
                default:
                    fcBufFree(&b);
                    fjFailAt(r, r->i - 1, fcFmt(r->c, "'%s' is an invalid escapable character within a JSON string. The string should be correctly escaped.", fjPrintable(r->c, e)));
            }
            continue;
        }
        if (ch < 0x80) { fcBufChar(&b, (char)ch); r->i++; continue; }
        {
            /* a UTF-8 sequence: as UTF-16 units (fc8) */
            uint32_t cp;
            int len = (ch & 0xE0) == 0xC0 ? 2 : (ch & 0xF0) == 0xE0 ? 3 : 4;
            cp = len == 2 ? (ch & 0x1Fu) : len == 3 ? (ch & 0x0Fu) : (ch & 0x07u);
            {
                int k;
                for (k = 1; k < len && r->i + (size_t)k < r->n; k++) cp = (cp << 6) | (r->s[r->i + (size_t)k] & 0x3Fu);
            }
            r->i += (size_t)len;
            if (cp >= 0x10000) { fcBufUnit(&b, (uint16_t)(0xD800 + ((cp - 0x10000) >> 10))); fcBufUnit(&b, (uint16_t)(0xDC00 + ((cp - 0x10000) & 0x3FF))); }
            else fcBufUnit(&b, (uint16_t)cp);
        }
    }
    return fcBufDone(r->c, &b);
}

static bool fjDelimiter(unsigned char b) { return b == ',' || b == '}' || b == ']' || b == ' ' || b == '\n' || b == '\r' || b == '\t' || b == '/'; }

/* a number (the reader stands on '-' or a digit) */
static const char* fjNumber(FjReader* r) {
    size_t start = r->i;
    if (r->s[r->i] == '-') {
        r->i++;
        if (r->i >= r->n) fjFailAt(r, r->n, "Expected a digit ('0'-'9'), but instead reached end of data.");
        if (!(r->s[r->i] >= '0' && r->s[r->i] <= '9'))
            fjFailAt(r, r->i, fcFmt(r->c, "'%s' is invalid within a number, immediately after a sign character ('+' or '-'). Expected a digit ('0'-'9').", fjPrintable(r->c, r->s[r->i])));
    }
    if (r->s[r->i] == '0') {
        r->i++;
        if (r->i < r->n && r->s[r->i] >= '0' && r->s[r->i] <= '9') fjFailAt(r, r->i, fcFmt(r->c, "Invalid leading zero before '%s'.", fjPrintable(r->c, r->s[r->i])));
    } else while (r->i < r->n && r->s[r->i] >= '0' && r->s[r->i] <= '9') r->i++;
    if (r->i < r->n && r->s[r->i] == '.') {
        r->i++;
        if (r->i >= r->n) fjFailAt(r, r->n, "Expected a digit ('0'-'9'), but instead reached end of data.");
        if (!(r->s[r->i] >= '0' && r->s[r->i] <= '9'))
            fjFailAt(r, r->i, fcFmt(r->c, "'%s' is invalid within a number, immediately after a decimal point ('.'). Expected a digit ('0'-'9').", fjPrintable(r->c, r->s[r->i])));
        while (r->i < r->n && r->s[r->i] >= '0' && r->s[r->i] <= '9') r->i++;
    }
    if (r->i < r->n && (r->s[r->i] == 'e' || r->s[r->i] == 'E')) {
        r->i++;
        if (r->i < r->n && (r->s[r->i] == '+' || r->s[r->i] == '-')) r->i++;
        if (r->i >= r->n) fjFailAt(r, r->n, "Expected a digit ('0'-'9'), but instead reached end of data.");
        if (!(r->s[r->i] >= '0' && r->s[r->i] <= '9'))
            fjFailAt(r, r->i, fcFmt(r->c, "'%s' is invalid within a number, immediately after a sign character ('+' or '-'). Expected a digit ('0'-'9').", fjPrintable(r->c, r->s[r->i])));
        while (r->i < r->n && r->s[r->i] >= '0' && r->s[r->i] <= '9') r->i++;
    }
    if (r->i < r->n && !fjDelimiter(r->s[r->i]))
        fjFailAt(r, r->i, fcFmt(r->c, "'%s' is an invalid end of a number. Expected a delimiter.", fjPrintable(r->c, r->s[r->i])));
    /* (a number that ends with the data inside an object or array: the reader cannot know it ended) */
    if (r->i >= r->n && r->depth > 0)
        fjFailAt(r, r->n, fcFmt(r->c, "'%s' is an invalid end of a number. Expected a delimiter.", fjPrintable(r->c, r->s[r->n - 1])));
    return fcStrN(r->c, (const char*)r->s + start, r->i - start);
}

/* a literal: a mismatch shows the rest of the data from its start (CheckLiteral) */
static void fjLiteral(FjReader* r, const char* lit, const char* what) {
    size_t k, len = strlen(lit);
    for (k = 0; k < len; k++)
        if (r->i + k >= r->n || r->s[r->i + k] != (unsigned char)lit[k]) {
            FcBuf b = {0};
            fcBufStr(&b, "'");
            fcBufPut(&b, (const char*)r->s + r->i, r->n - r->i);
            fcBufFmt(&b, "' is an invalid JSON literal. Expected the literal '%s'.", what);
            fjFailAt(r, r->i + k, fcBufDone(r->c, &b));
        }
    r->i += len;
}

static FjValue* fjValue(FjReader* r);

/* past a ',': the data must go on */
static void fjAfterComma(FjReader* r) {
    size_t at = r->i;
    r->i++;
    if (r->i >= r->n) fjFailAt(r, at, "Expected start of a property name or value, but instead reached end of data.");
    fjSkip(r);
    if (r->i >= r->n) fjFailAt(r, r->n, "Expected start of a property name or value, but instead reached end of data.");
}

static FjValue* fjNew(FjReader* r, FjKind kind) {
    FjValue* v = (FjValue*)fcAlloc(r->c, sizeof *v);
    v->kind = kind;
    v->start = r->i;
    return v;
}

static FjValue* fjValue(FjReader* r) {
    FjValue* v;
    unsigned char ch;
    fjSkip(r);
    if (r->i >= r->n) fjFailAt(r, r->n, r->depth == 0 ? "The input does not contain any JSON tokens. Expected the input to start with a valid JSON token, when isFinalBlock is true."
        : "Expected depth to be zero at the end of the JSON payload. There is an open JSON object or array that should be closed.");
    ch = r->s[r->i];
    if (ch == '{') {
        v = fjNew(r, FJK_OBJECT);
        v->names = fcListNew(r->c);
        v->items = fcListNew(r->c);
        fjToken(r, FJT_START_OBJECT, r->i, NULL);
        r->i++;
        r->depth++;
        if (r->depth > 64) fjFailAt(r, r->i - 1, "The maximum configured depth of 64 has been exceeded. Cannot read next JSON object.");
        for (;;) {
            const char* name;
            size_t at;
            fjSkip(r);
            if (r->i >= r->n) fjFailAt(r, r->n, "Expected depth to be zero at the end of the JSON payload. There is an open JSON object or array that should be closed.");
            if (r->s[r->i] == '}') { fjToken(r, FJT_END_OBJECT, r->i, NULL); r->i++; break; }
            if (r->s[r->i] != '"')
                fjFailAt(r, r->i, fcFmt(r->c, "'%s' is an invalid start of a property name. Expected a '\"'.", fjPrintable(r->c, r->s[r->i])));
            at = r->i;
            name = fjString(r);
            fjToken(r, FJT_PROPERTY, at, name);
            fjSkip(r);
            if (r->i >= r->n) fjFailAt(r, r->n, "Expected a value, but instead reached end of data.");
            if (r->s[r->i] != ':')
                fjFailAt(r, r->i, fcFmt(r->c, "'%s' is invalid after a property name. Expected a ':'.", fjPrintable(r->c, r->s[r->i])));
            r->i++;
            fcListAdd(r->c, v->names, (void*)name);
            fcListAdd(r->c, v->items, fjValue(r));
            fjSkip(r);
            if (r->i >= r->n) fjFailAt(r, r->n, "Expected depth to be zero at the end of the JSON payload. There is an open JSON object or array that should be closed.");
            if (r->s[r->i] == ',') { fjAfterComma(r); continue; }
            if (r->s[r->i] == '}') continue;
            if (r->s[r->i] == ']') fjFailAt(r, r->i, "']' is invalid without a matching open.");
            fjFailAt(r, r->i, fcFmt(r->c, "'%s' is invalid after a value. Expected either ',', '}', or ']'.", fjPrintable(r->c, r->s[r->i])));
        }
        r->depth--;
        return v;
    }
    if (ch == '[') {
        v = fjNew(r, FJK_ARRAY);
        v->items = fcListNew(r->c);
        fjToken(r, FJT_START_ARRAY, r->i, NULL);
        r->i++;
        r->depth++;
        if (r->depth > 64) fjFailAt(r, r->i - 1, "The maximum configured depth of 64 has been exceeded. Cannot read next JSON array.");
        for (;;) {
            fjSkip(r);
            if (r->i >= r->n) fjFailAt(r, r->n, "Expected depth to be zero at the end of the JSON payload. There is an open JSON object or array that should be closed.");
            if (r->s[r->i] == ']') { fjToken(r, FJT_END_ARRAY, r->i, NULL); r->i++; break; }
            fcListAdd(r->c, v->items, fjValue(r));
            fjSkip(r);
            if (r->i >= r->n) fjFailAt(r, r->n, "Expected depth to be zero at the end of the JSON payload. There is an open JSON object or array that should be closed.");
            if (r->s[r->i] == ',') { fjAfterComma(r); continue; }
            if (r->s[r->i] == ']') continue;
            if (r->s[r->i] == '}') fjFailAt(r, r->i, "'}' is invalid without a matching open.");
            fjFailAt(r, r->i, fcFmt(r->c, "'%s' is invalid after a value. Expected either ',', '}', or ']'.", fjPrintable(r->c, r->s[r->i])));
        }
        r->depth--;
        return v;
    }
    if (ch == '"') { v = fjNew(r, FJK_STRING); fjToken(r, FJT_VALUE, r->i, NULL); v->text = fjString(r); return v; }
    if (ch == '-' || (ch >= '0' && ch <= '9')) { v = fjNew(r, FJK_NUMBER); fjToken(r, FJT_VALUE, r->i, NULL); v->text = fjNumber(r); return v; }
    if (ch == 't') { v = fjNew(r, FJK_TRUE); fjToken(r, FJT_VALUE, r->i, NULL); fjLiteral(r, "true", "true"); return v; }
    if (ch == 'f') { v = fjNew(r, FJK_FALSE); fjToken(r, FJT_VALUE, r->i, NULL); fjLiteral(r, "false", "false"); return v; }
    if (ch == 'n') { v = fjNew(r, FJK_NULL); fjToken(r, FJT_VALUE, r->i, NULL); fjLiteral(r, "null", "null"); return v; }
    fjFailAt(r, r->i, fcFmt(r->c, "'%s' is an invalid start of a value.", fjPrintable(r->c, ch)));
}

/* the text (fc8) as UTF-8 bytes (a lone surrogate: U+FFFD) */
static const unsigned char* fjBytes(FcCtx* c, const char* text, size_t* n) {
    FcBuf b = {0};
    fcBufStr(&b, "");
    fcOut8(&b, text);
    *n = b.len;
    return (const unsigned char*)fcBufDone(c, &b);
}

/* JsonDocument.Parse: the root, or NULL and the JsonReaderException in `error`; `tokens` (may be NULL) gets the tokens in order */
static FjValue* fjParse(FcCtx* c, const char* text, FjError* error, FcList* tokens) {
    FjReader* r = (FjReader*)fcAlloc(c, sizeof *r);
    FjValue* volatile root;
    r->c = c;
    r->s = fjBytes(c, text, &r->n);
    r->tokens = tokens;
    if (setjmp(r->fail) != 0) {
        *error = r->error;
        return NULL;
    }
    /* (a UTF-8 BOM at the start is skipped) */
    if (r->n >= 3 && r->s[0] == 0xEF && r->s[1] == 0xBB && r->s[2] == 0xBF) r->i = 3;
    root = fjValue(r);
    fjSkip(r);
    if (r->i < r->n)
        fjFailAt(r, r->i, fcFmt(c, "'%s' is invalid after a single JSON value. Expected end of data.", fjPrintable(c, r->s[r->i])));
    return root;
}

/* JsonElement.TryGetProperty (the last property of the name) */
static FjValue* fjProp(const FjValue* o, const char* name) {
    int i;
    if (!o || o->kind != FJK_OBJECT) return NULL;
    for (i = o->names->count - 1; i >= 0; i--) if (strcmp((const char*)o->names->items[i], name) == 0) return (FjValue*)o->items->items[i];
    return NULL;
}
/* JsonElement.TryGetInt32 (a number without fraction or exponent in the range of int; .NET also takes 1.0 and 1e2? no: only what Utf8Parser reads as an int) */
static bool fjTryGetInt32(const FjValue* v, int* out) {
    const char* p;
    int64_t x = 0;
    bool neg = false;
    if (!v || v->kind != FJK_NUMBER) return false;
    p = v->text;
    if (*p == '-') { neg = true; p++; }
    while (*p >= '0' && *p <= '9') { x = x * 10 + (*p - '0'); if (x > 2147483648LL) return false; p++; }
    if (*p) return false;
    if (neg) x = -x;
    if (x > 2147483647LL || x < -2147483648LL) return false;
    *out = (int)x;
    return true;
}
static FC_LATER bool fjTryGetDouble(const FjValue* v, double* out) {
    if (!v || v->kind != FJK_NUMBER) return false;
    *out = strtod(v->text, NULL);
    return isfinite(*out);
}

/* the JSON escaping of a text: `relaxed` (UnsafeRelaxedJsonEscaping) keeps '"'-free HTML characters and non-ASCII; the default encoder escapes
 * those as \uXXXX (and '"' as ") */
static void fjEscape(FcBuf* b, const char* s, bool relaxed) {
    const unsigned char* p = (const unsigned char*)s;
    fcBufChar(b, '"');
    while (*p) {
        uint16_t u = fc8Next(&p);
        switch (u) {
            case '\b': fcBufStr(b, "\\b"); continue;
            case '\t': fcBufStr(b, "\\t"); continue;
            case '\n': fcBufStr(b, "\\n"); continue;
            case '\f': fcBufStr(b, "\\f"); continue;
            case '\r': fcBufStr(b, "\\r"); continue;
            case '\\': fcBufStr(b, "\\\\"); continue;
            case '"': fcBufStr(b, relaxed ? "\\\"" : "\\u0022"); continue;
            default: break;
        }
        if (u < 0x20 || (u >= 0x7F && u <= 0x9F) || u == 0x2028 || u == 0x2029 || (u >= 0xD800 && u <= 0xDFFF) || (u >= 0xE000 && u <= 0xF8FF) || u == 0xFFFE || u == 0xFFFF)
            fcBufFmt(b, "\\u%04X", u);
        else if (!relaxed && (u >= 0x80 || u == '<' || u == '>' || u == '&' || u == '\'' || u == '+' || u == '`'))
            fcBufFmt(b, "\\u%04X", u);
        else fcBufUnit(b, u);
    }
    fcBufChar(b, '"');
}

/* JsonSerializer.Serialize(element, { WriteIndented = false, Encoder = UnsafeRelaxedJsonEscaping }) */
static void fjWrite(FcBuf* b, const FjValue* v) {
    int i;
    switch (v->kind) {
        case FJK_OBJECT:
            fcBufChar(b, '{');
            for (i = 0; i < v->items->count; i++) {
                if (i) fcBufChar(b, ',');
                fjEscape(b, (const char*)v->names->items[i], true);
                fcBufChar(b, ':');
                fjWrite(b, (const FjValue*)v->items->items[i]);
            }
            fcBufChar(b, '}');
            return;
        case FJK_ARRAY:
            fcBufChar(b, '[');
            for (i = 0; i < v->items->count; i++) { if (i) fcBufChar(b, ','); fjWrite(b, (const FjValue*)v->items->items[i]); }
            fcBufChar(b, ']');
            return;
        case FJK_STRING: fjEscape(b, v->text, true); return;
        case FJK_NUMBER: fcBufStr(b, v->text); return;
        case FJK_TRUE: fcBufStr(b, "true"); return;
        case FJK_FALSE: fcBufStr(b, "false"); return;
        case FJK_NULL: fcBufStr(b, "null"); return;
    }
}

/* "The file is no valid JSON: " + ex.Message.Split(" Path:")[0], at the line ex.LineNumber + 1 */
static _Noreturn void fjThrowInvalid(FcCtx* c, const FjError* e) {
    const char* m = e->message;
    const char* path = strstr(m, " Path:");
    if (path) m = fcStrN(c, m, (size_t)(path - m));
    fmThrow1(c, (int)(e->line + 1), fcFmt(c, "The file is no valid JSON: %s", m));
}
