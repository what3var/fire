/* native/forge/conditional.c - port of src/fire.Compiler/Conditional.cs (commit 8bfb232a): ConditionalSymbols (For, Evaluate), InlineCScan,
 * ConditionalState. Texts of a line are UTF-16 units (FcSlice). The symbols are case-insensitive: they are kept in lower case. */

typedef struct FcSlice { const uint16_t* p; int n; } FcSlice;

static FcSlice fcSliceTrim(FcSlice s) {
    while (s.n > 0 && fcIsWhiteSpace(s.p[0])) { s.p++; s.n--; }
    while (s.n > 0 && fcIsWhiteSpace(s.p[s.n - 1])) s.n--;
    return s;
}
static FcSlice fcSliceTrimStart(FcSlice s) {
    while (s.n > 0 && fcIsWhiteSpace(s.p[0])) { s.p++; s.n--; }
    return s;
}
static const char* fcSliceText(FcCtx* c, FcSlice s) { return fc8FromU16(c, s.p, s.n); }

static const char* fcLower(FcCtx* c, const char* s) {
    char* d = (char*)fcStr(c, s);
    char* p;
    for (p = d; *p; p++) if (*p >= 'A' && *p <= 'Z') *p = (char)(*p - 'A' + 'a');
    return d;
}

/* the set of symbols (case-insensitive) */
static int fcSymbolIndex(FcCtx* c, FcList* symbols, const char* name) {
    const char* low = fcLower(c, name);
    int i;
    for (i = 0; i < symbols->count; i++) if (strcmp((const char*)symbols->items[i], low) == 0) return i;
    return -1;
}
bool fcSymbolsContains(FcCtx* c, FcList* symbols, const char* name) { return fcSymbolIndex(c, symbols, name) >= 0; }
void fcSymbolsAdd(FcCtx* c, FcList* symbols, const char* name) {
    if (fcSymbolIndex(c, symbols, name) < 0) fcListAdd(c, symbols, (void*)fcLower(c, name));
}
static void fcSymbolsRemove(FcCtx* c, FcList* symbols, const char* name) {
    int i = fcSymbolIndex(c, symbols, name);
    if (i >= 0) fcListRemoveAt(symbols, i);
}

/* ConditionalSymbols.For(null, "vm"): the symbols of a VM run on this machine (TargetProfile.Host: 64-bit int and float, the word of 64 bits) */
void fcSymbolsForHost(FcCtx* c, FcList* symbols) {
#if defined(_WIN32)
    fcSymbolsAdd(c, symbols, "windows");
#elif defined(__APPLE__)
    fcSymbolsAdd(c, symbols, "macos");
    fcSymbolsAdd(c, symbols, "posix");
#else
    fcSymbolsAdd(c, symbols, "linux");
    fcSymbolsAdd(c, symbols, "posix");
#endif
    fcSymbolsAdd(c, symbols, "vm");
    fcSymbolsAdd(c, symbols, "word64");
}

static bool fcIsNameStart(uint16_t c) { return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_'; }
static bool fcIsNamePart(uint16_t c) { return fcIsNameStart(c) || (c >= '0' && c <= '9'); }

/* `^[A-Za-z_][A-Za-z0-9_]*$` */
static bool fcNameOnly(FcSlice s) {
    int i;
    if (s.n == 0 || !fcIsNameStart(s.p[0])) return false;
    for (i = 1; i < s.n; i++) if (!fcIsNamePart(s.p[i])) return false;
    return true;
}

typedef struct FcEval { FcCtx* ctx; const char** tokens; int count, i; FcList* symbols; int line; const char* trimmed; } FcEval;

_Noreturn static void fcPreError(FcCtx* c, const char* message) { fcThrow(c, FC_ERR_PRE, 0, 0, NULL, message); }

static bool fcEvalOr(FcEval* e);
static bool fcEvalPrimary(FcEval* e) {
    const char* t;
    if (e->i >= e->count) fcPreError(e->ctx, fcFmt(e->ctx, "'#if' expression '%s' ends too early (line %d).", e->trimmed, e->line));
    t = e->tokens[e->i++];
    if (strcmp(t, "(") == 0) {
        bool v = fcEvalOr(e);
        if (e->i >= e->count || strcmp(e->tokens[e->i], ")") != 0)
            fcPreError(e->ctx, fcFmt(e->ctx, "'#if' expression '%s': ')' is missing (line %d).", e->trimmed, e->line));
        e->i++;
        return v;
    }
    if (strcmp(t, ")") == 0 || strcmp(t, "&&") == 0 || strcmp(t, "||") == 0)
        fcPreError(e->ctx, fcFmt(e->ctx, "'#if' expression '%s': unexpected '%s' (line %d).", e->trimmed, t, e->line));
    if (strcmp(t, "true") == 0) return true;
    if (strcmp(t, "false") == 0) return false;
    return fcSymbolsContains(e->ctx, e->symbols, t);
}
static bool fcEvalNot(FcEval* e) {
    if (e->i < e->count && strcmp(e->tokens[e->i], "!") == 0) { e->i++; return !fcEvalNot(e); }
    return fcEvalPrimary(e);
}
static bool fcEvalAnd(FcEval* e) {
    bool v = fcEvalNot(e);
    while (e->i < e->count && strcmp(e->tokens[e->i], "&&") == 0) { bool r; e->i++; r = fcEvalNot(e); v = v && r; }
    return v;
}
static bool fcEvalOr(FcEval* e) {
    bool v = fcEvalAnd(e);
    while (e->i < e->count && strcmp(e->tokens[e->i], "||") == 0) { bool r; e->i++; r = fcEvalAnd(e); v = v || r; }
    return v;
}

/* ConditionalSymbols.Evaluate */
bool fcEvaluateCondition(FcCtx* c, FcSlice expression, FcList* symbols, int line) {
    FcEval e;
    const char** tokens = (const char**)fcAlloc(c, sizeof(char*) * (size_t)(expression.n + 1));
    int count = 0, pos = 0;
    while (pos < expression.n) {
        uint16_t ch = expression.p[pos];
        if (fcIsWhiteSpace(ch)) { pos++; continue; }
        if (fcIsNameStart(ch)) {
            int start = pos;
            while (pos < expression.n && fcIsNamePart(expression.p[pos])) pos++;
            tokens[count++] = fc8FromU16(c, expression.p + start, pos - start);
            continue;
        }
        if (ch == '&' && pos + 1 < expression.n && expression.p[pos + 1] == '&') { tokens[count++] = "&&"; pos += 2; continue; }
        if (ch == '|' && pos + 1 < expression.n && expression.p[pos + 1] == '|') { tokens[count++] = "||"; pos += 2; continue; }
        if (ch == '!') { tokens[count++] = "!"; pos++; continue; }
        if (ch == '(') { tokens[count++] = "("; pos++; continue; }
        if (ch == ')') { tokens[count++] = ")"; pos++; continue; }
        fcPreError(c, fcFmt(c, "'#if' expression '%s': unexpected character '%s' (line %d).", fcSliceText(c, fcSliceTrim(expression)),
            fc8FromU16(c, expression.p + pos, 1), line));
    }
    if (count == 0) fcPreError(c, fcFmt(c, "'#if' expects a condition (line %d).", line));
    e.ctx = c; e.tokens = tokens; e.count = count; e.i = 0; e.symbols = symbols; e.line = line;
    e.trimmed = fcSliceText(c, fcSliceTrim(expression));
    {
        bool result = fcEvalOr(&e);
        if (e.i < count) fcPreError(c, fcFmt(c, "'#if' expression '%s': unexpected '%s' (line %d).", e.trimmed, tokens[e.i], line));
        return result;
    }
}

/* ---- InlineCScan ---- */
typedef struct FcInlineCScan { bool armed, blockComment; int parens, depth, waitLines; } FcInlineCScan;

static bool fcInlineInBody(const FcInlineCScan* s) { return s->depth > 0; }

/* `^\s*(?:(?:public|private|protected|static)\s+)*inline\s+c\s+[A-Za-z_]`: the length of the match, -1 for none */
static int fcInlineStart(FcSlice line) {
    static const char* const mods[] = { "public", "private", "protected", "static" };
    int i = 0, k;
    while (i < line.n && fcIsWhiteSpace(line.p[i])) i++;
    for (;;) {
        bool any = false;
        for (k = 0; k < 4; k++) {
            int len = (int)strlen(mods[k]), j;
            if (i + len > line.n) continue;
            for (j = 0; j < len; j++) if (line.p[i + j] != (uint8_t)mods[k][j]) break;
            if (j < len || i + len >= line.n || !fcIsWhiteSpace(line.p[i + len])) continue;
            i += len;
            while (i < line.n && fcIsWhiteSpace(line.p[i])) i++;
            any = true;
            break;
        }
        if (!any) break;
    }
    {
        static const char inl[] = "inline";
        int j;
        if (i + 6 > line.n) return -1;
        for (j = 0; j < 6; j++) if (line.p[i + j] != (uint8_t)inl[j]) return -1;
        i += 6;
        if (i >= line.n || !fcIsWhiteSpace(line.p[i])) return -1;
        while (i < line.n && fcIsWhiteSpace(line.p[i])) i++;
        if (i >= line.n || line.p[i] != 'c') return -1;
        i++;
        if (i >= line.n || !fcIsWhiteSpace(line.p[i])) return -1;
        while (i < line.n && fcIsWhiteSpace(line.p[i])) i++;
        if (i >= line.n || !fcIsNameStart(line.p[i])) return -1;
        return i + 1;
    }
}

static void fcInlineFeed(FcInlineCScan* s, FcSlice line) {
    int i = 0;
    if (!s->armed && s->depth == 0) {
        int m = fcInlineStart(line);
        if (m < 0) return;
        s->armed = true;
        s->parens = 0;
        s->waitLines = 0;
        i = m - 1;
    } else if (s->armed && s->parens <= 0 && s->waitLines++ > 0 && fcSliceTrim(line).n > 0) {
        FcSlice t = fcSliceTrimStart(line);
        if (!(t.n > 0 && t.p[0] == '{')) { s->armed = false; return; }
    }
    for (; i < line.n; i++) {
        uint16_t ch = line.p[i];
        bool slash2 = ch == '/' && i + 1 < line.n && line.p[i + 1] == '/';
        if (s->blockComment) { if (ch == '*' && i + 1 < line.n && line.p[i + 1] == '/') { s->blockComment = false; i++; } continue; }
        if (s->depth == 0) {
            if (ch == '(') s->parens++;
            else if (ch == ')') s->parens--;
            else if (ch == '=' && s->parens <= 0) { s->armed = false; return; }
            else if (ch == '{' && s->parens <= 0) { s->armed = false; s->depth = 1; }
            else if (ch == ';' || slash2) { if (s->parens <= 0) { s->armed = false; return; } }
            continue;
        }
        if (slash2) return;
        if (ch == '/' && i + 1 < line.n && line.p[i + 1] == '*') { s->blockComment = true; i++; continue; }
        if (ch == '"' || ch == '\'') {
            for (i++; i < line.n && line.p[i] != ch; i++) if (line.p[i] == '\\') i++;
            continue;
        }
        if (ch == '{') s->depth++;
        else if (ch == '}' && --s->depth == 0) return;
    }
}

/* ---- ConditionalState ---- */
typedef struct FcCondFrame { bool parentActive, taken, active, sawElse; int line; } FcCondFrame;
typedef struct FcCondState { FcCondFrame* frames; int count, cap; } FcCondState;

static bool fcCondActive(const FcCondState* s) { return s->count == 0 || s->frames[s->count - 1].active; }

static void fcCondPush(FcCondState* s, bool condition, int line) {
    bool parent = fcCondActive(s), v = parent && condition;
    if (s->count == s->cap) {
        s->cap = s->cap ? s->cap * 2 : 8;
        s->frames = (FcCondFrame*)realloc(s->frames, sizeof(FcCondFrame) * (size_t)s->cap);
        if (!s->frames) { fbOutOfMemory(); return; }
    }
    s->frames[s->count].parentActive = parent;
    s->frames[s->count].taken = v;
    s->frames[s->count].active = v;
    s->frames[s->count].sawElse = false;
    s->frames[s->count].line = line;
    s->count++;
}

static bool fcCondNameCondition(FcCtx* c, FcSlice argument, const char* directive, FcList* symbols, int line) {
    if (!fcNameOnly(argument)) fcPreError(c, fcFmt(c, "'#%s' expects a name (line %d).", directive, line));
    return fcSymbolsContains(c, symbols, fcSliceText(c, argument));
}

/* ConditionalState.TryHandle (throwOnError: true - the preprocessor) */
static bool fcCondTryHandle(FcCtx* c, FcCondState* s, const char* name, FcSlice argument, int line, FcList* symbols) {
    const char* low = fcLower(c, name);
    argument = fcSliceTrim(argument);
    if (strcmp(low, "if") == 0) { bool v = fcCondActive(s) && fcEvaluateCondition(c, argument, symbols, line); fcCondPush(s, v, line); return true; }
    if (strcmp(low, "ifdef") == 0) { bool v = fcCondActive(s) && fcCondNameCondition(c, argument, "ifdef", symbols, line); fcCondPush(s, v, line); return true; }
    if (strcmp(low, "ifndef") == 0) { bool v = fcCondActive(s) && !fcCondNameCondition(c, argument, "ifndef", symbols, line); fcCondPush(s, v, line); return true; }
    if (strcmp(low, "elif") == 0) {
        FcCondFrame* f;
        if (s->count == 0) fcPreError(c, fcFmt(c, "'#elif' without '#if' (line %d).", line));
        f = &s->frames[s->count - 1];
        if (f->sawElse) fcPreError(c, fcFmt(c, "'#elif' after '#else' (line %d).", line));
        if (f->parentActive && !f->taken) {
            bool v = fcEvaluateCondition(c, argument, symbols, line);
            f = &s->frames[s->count - 1];
            f->active = v; f->taken = v;
        } else f->active = false;
        return true;
    }
    if (strcmp(low, "else") == 0) {
        FcCondFrame* f;
        if (s->count == 0) fcPreError(c, fcFmt(c, "'#else' without '#if' (line %d).", line));
        f = &s->frames[s->count - 1];
        if (f->sawElse) fcPreError(c, fcFmt(c, "A second '#else' for the '#if' of line %d (line %d).", f->line, line));
        f->sawElse = true;
        f->active = f->parentActive && !f->taken;
        f->taken = true;
        return true;
    }
    if (strcmp(low, "endif") == 0) {
        if (s->count == 0) fcPreError(c, fcFmt(c, "'#endif' without '#if' (line %d).", line));
        s->count--;
        return true;
    }
    if (strcmp(low, "define") == 0 || strcmp(low, "undef") == 0) {
        const char* arg;
        if (!fcCondActive(s)) return true;
        if (!fcNameOnly(argument)) fcPreError(c, fcFmt(c, "'#%s' expects a name (line %d).", name, line));
        arg = fcSliceText(c, argument);
        if (strcmp(low, "define") == 0) fcSymbolsAdd(c, symbols, arg); else fcSymbolsRemove(c, symbols, arg);
        return true;
    }
    if (strcmp(low, "error") == 0) {
        if (fcCondActive(s)) fcPreError(c, fcFmt(c, "#error %s (line %d)", fcSliceText(c, argument), line));
        return true;
    }
    return false;
}

static void fcCondEnsureClosed(FcCtx* c, FcCondState* s) {
    if (s->count > 0) fcPreError(c, fcFmt(c, "The '#if' of line %d has no '#endif'.", s->frames[s->count - 1].line));
}
