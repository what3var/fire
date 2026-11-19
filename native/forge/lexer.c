/* native/forge/lexer.c - port of src/fire.Compiler/Lexer.cs (commit f18c976c), the token of src/fire/Lexing/Token.cs (commit f18c976c).
 * The source is UTF-16 units (columns count them, as in C#); lexemes and literal texts are fc8 (forge.h). */

static const char* const fcTokenTypeNames[] = {
#define FC_TT_NAME(name) #name,
    FC_TOKEN_TYPES(FC_TT_NAME)
#undef FC_TT_NAME
};
const char* fcTokenTypeName(FcTokenType t) { return t >= 0 && t < TT__COUNT ? fcTokenTypeNames[t] : "?"; }

typedef struct FcKeyword { const char* word; FcTokenType type; } FcKeyword;
static const FcKeyword fcKeywords[] = {
    {"var", TT_Var}, {"func", TT_Func}, {"class", TT_Class}, {"construct", TT_Construct}, {"destruct", TT_Destruct}, {"return", TT_Return},
    {"if", TT_If}, {"else", TT_Else}, {"while", TT_While}, {"do", TT_Do}, {"for", TT_For}, {"foreach", TT_Foreach}, {"in", TT_In}, {"on", TT_On},
    {"new", TT_New}, {"base", TT_Base}, {"this", TT_This}, {"try", TT_Try}, {"catch", TT_Catch}, {"finally", TT_Finally}, {"throw", TT_Throw},
    {"is", TT_Is}, {"of", TT_Of}, {"from", TT_From}, {"under", TT_Under}, {"extern", TT_Extern}, {"unsafe", TT_Unsafe}, {"interface", TT_Interface},
    {"readonly", TT_Readonly}, {"override", TT_Override}, {"static", TT_Static}, {"public", TT_Public}, {"private", TT_Private},
    {"protected", TT_Protected}, {"namespace", TT_Namespace}, {"enum", TT_Enum}, {"with", TT_With}, {"extends", TT_Extends},
    {"switch", TT_Switch}, {"case", TT_Case}, {"default", TT_Default}, {"break", TT_Break}, {"continue", TT_Continue}, {"where", TT_Where},
    {"fire", TT_Fire}, {"taking", TT_Taking}, {"handover", TT_Handover}, {"operator", TT_Operator}, {"sync", TT_Sync}, {"flat", TT_Flat},
    {"copy", TT_Copy}, {"take", TT_Take}, {"replace", TT_Replace}, {"leave", TT_Leave}, {"terminate", TT_Terminate}, {"actor", TT_Actor},
    {"process", TT_Process}, {"true", TT_True}, {"false", TT_False}, {"undefined", TT_Undefined}, {"bool", TT_KwBool}, {"int", TT_KwInt},
    {"float", TT_KwFloat}, {"char", TT_KwChar}, {"string", TT_KwString}, {"byte", TT_KwByte}, {"word", TT_KwWord}, {"and", TT_And}, {"or", TT_Or},
};

static bool fcKeywordType(const char* word, FcTokenType* type) {
    size_t i;
    for (i = 0; i < sizeof fcKeywords / sizeof fcKeywords[0]; i++)
        if (fcKeywords[i].word[0] == word[0] && strcmp(fcKeywords[i].word, word) == 0) { *type = fcKeywords[i].type; return true; }
    return false;
}
bool fcIsKeyword(const char* word) { FcTokenType t; return fcKeywordType(word, &t); }

typedef struct FcLexer {
    FcCtx* ctx;
    const uint16_t* src;
    int n;
    int pos, line, col;
    bool inlineC;
    int inlineParens;
} FcLexer;

static bool lxAtEnd(FcLexer* l) { return l->pos >= l->n; }
static uint16_t lxPeek(FcLexer* l) { return lxAtEnd(l) ? 0 : l->src[l->pos]; }
static uint16_t lxPeekNext(FcLexer* l) { return l->pos + 1 >= l->n ? 0 : l->src[l->pos + 1]; }
static uint16_t lxPeekAt(FcLexer* l, int offset) { return l->pos + offset >= l->n ? 0 : l->src[l->pos + offset]; }
static uint16_t lxAdvance(FcLexer* l) {
    uint16_t c = l->src[l->pos++];
    if (c == '\n') { l->line++; l->col = 1; }
    else l->col++;
    return c;
}
static bool lxMatch(FcLexer* l, uint16_t expected) {
    if (lxAtEnd(l) || l->src[l->pos] != expected) return false;
    lxAdvance(l);
    return true;
}
static const char* lxText(FcLexer* l, int start, int end) { return fc8FromU16(l->ctx, l->src + start, end - start); }

_Noreturn static void lxError(FcLexer* l, const char* message, int line, int col) { fcThrow(l->ctx, FC_ERR_LEX, line, col, NULL, message); }

static void lxAdd(FcCtx* c, FcTokens* ts, FcToken t) {
    if (ts->count == ts->cap) {
        int cap = ts->cap ? ts->cap * 2 : 256;
        FcToken* items = (FcToken*)fcAlloc(c, sizeof(FcToken) * (size_t)cap);
        if (ts->count) memcpy(items, ts->items, sizeof(FcToken) * (size_t)ts->count);
        ts->items = items;
        ts->cap = cap;
    }
    ts->items[ts->count++] = t;
}

static FcToken lxTok(FcTokenType type, const char* lexeme, int line, int col, bool nl) {
    FcToken t;
    memset(&t, 0, sizeof t);
    t.type = type; t.lexeme = lexeme; t.line = line; t.column = col; t.newlineBefore = nl;
    return t;
}

static bool lxIsHexDigit(uint16_t c) { return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'); }
static bool lxIsBinaryDigit(uint16_t c) { return c == '0' || c == '1'; }
static bool lxIsDecDigit(uint16_t c) { return fcIsDigit(c); }
static bool lxIsIdentifierStart(uint16_t c) { return fcIsLetter(c) || c == '_'; }
static bool lxIsIdentifierPart(uint16_t c) { return fcIsLetterOrDigit(c) || c == '_'; }
static bool lxIsUnitSuffixChar(uint16_t c) { return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == 0xB5 || c == 0x3BC; }

/* `inline c [Type] Name(params) { C }` (see Lexer.NoteInlineC) */
static void lxNoteInlineC(FcLexer* l, FcTokens* ts) {
    FcToken* t = &ts->items[ts->count - 1];
    FcToken* inl;
    if (l->inlineC) {
        if (l->inlineParens <= 0 && (t->newlineBefore || t->type == TT_Assign)) { l->inlineC = false; return; }
        if (t->type == TT_LParen) l->inlineParens++;
        else if (t->type == TT_RParen) l->inlineParens--;
        else if (l->inlineParens <= 0 && (t->type == TT_Semicolon || t->type == TT_RBrace)) l->inlineC = false;
        return;
    }
    if (ts->count < 2 || t->type != TT_Identifier || strcmp(t->lexeme, "c") != 0 || t->newlineBefore) return;
    inl = &ts->items[ts->count - 2];
    if (inl->type != TT_Identifier || strcmp(inl->lexeme, "inline") != 0) return;
    if (!(inl->newlineBefore || ts->count == 2)) {
        FcTokenType b = ts->items[ts->count - 3].type;
        if (!(b == TT_Semicolon || b == TT_RBrace || b == TT_RBracket || b == TT_LBrace || b == TT_Static || b == TT_Public || b == TT_Private || b == TT_Protected)) return;
    }
    l->inlineC = true;
    l->inlineParens = 0;
}

static FcToken lxReadInlineCode(FcLexer* l, int line, int col, bool nl) {
    int start = l->pos, depth = 0;
    while (!lxAtEnd(l)) {
        uint16_t c = lxPeek(l);
        if (c == '"' || c == '\'') {
            uint16_t quote = lxAdvance(l);
            while (!lxAtEnd(l) && lxPeek(l) != quote && lxPeek(l) != '\n') { if (lxAdvance(l) == '\\' && !lxAtEnd(l)) lxAdvance(l); }
            if (!lxAtEnd(l) && lxPeek(l) == quote) lxAdvance(l);
            continue;
        }
        if (c == '/' && lxPeekNext(l) == '/') { while (!lxAtEnd(l) && lxPeek(l) != '\n') lxAdvance(l); continue; }
        if (c == '/' && lxPeekNext(l) == '*') {
            lxAdvance(l); lxAdvance(l);
            while (!lxAtEnd(l) && !(lxPeek(l) == '*' && lxPeekNext(l) == '/')) lxAdvance(l);
            if (!lxAtEnd(l)) { lxAdvance(l); lxAdvance(l); }
            continue;
        }
        lxAdvance(l);
        if (c == '{') depth++;
        else if (c == '}' && --depth == 0) {
            FcToken t = lxTok(TT_InlineCode, lxText(l, start, l->pos), line, col, nl);
            t.literal = FC_LIT_STRING;
            t.stringValue = lxText(l, start + 1, l->pos - 1);
            return t;
        }
    }
    lxError(l, "The C body of an 'inline c' function is not closed ('}' missing)", line, col);
}

/* a standalone `_` at the end of a line (optionally followed by a line comment): the line goes on */
static bool lxTryConsumeLineContinuation(FcLexer* l) {
    int offset = 1, i;
    if (lxIsIdentifierPart(lxPeekNext(l))) return false;
    while (lxPeekAt(l, offset) == ' ' || lxPeekAt(l, offset) == '\t' || lxPeekAt(l, offset) == '\r') offset++;
    if (lxPeekAt(l, offset) == '/' && lxPeekAt(l, offset + 1) == '/') {
        offset += 2;
        while (lxPeekAt(l, offset) != '\n' && lxPeekAt(l, offset) != 0) offset++;
    }
    if (lxPeekAt(l, offset) != '\n' && lxPeekAt(l, offset) != 0) return false;
    for (i = 0; i < offset; i++) lxAdvance(l);
    if (lxPeek(l) == '\n') lxAdvance(l);
    return true;
}

static bool lxSkipWhitespaceAndComments(FcLexer* l) {
    bool sawNewline = false;
    while (!lxAtEnd(l)) {
        uint16_t c = lxPeek(l);
        if (c == ' ' || c == '\t' || c == '\r') lxAdvance(l);
        else if (c == '\n') { sawNewline = true; lxAdvance(l); }
        else if (c == '/' && lxPeekNext(l) == '/') { while (!lxAtEnd(l) && lxPeek(l) != '\n') lxAdvance(l); }
        else if (c == '/' && lxPeekNext(l) == '*') {
            lxAdvance(l); lxAdvance(l);
            while (!lxAtEnd(l) && !(lxPeek(l) == '*' && lxPeekNext(l) == '/')) {
                if (lxPeek(l) == '\n') sawNewline = true;
                lxAdvance(l);
            }
            if (lxAtEnd(l)) lxError(l, "Unterminated block comment", l->line, l->col);
            lxAdvance(l); lxAdvance(l);
        }
        else if (c == '_' && lxTryConsumeLineContinuation(l)) { }
        else break;
    }
    return sawNewline;
}

static void lxSkipDigits(FcLexer* l, bool (*isDigit)(uint16_t)) {
    while (!lxAtEnd(l) && (isDigit(lxPeek(l)) || (lxPeek(l) == '_' && l->pos > 0 && isDigit(l->src[l->pos - 1]) && isDigit(lxPeekNext(l)))))
        lxAdvance(l);
}

/* the text without `_`, ASCII (false when it has other characters) */
static bool lxNumberText(FcLexer* l, int start, int end, char* out, size_t cap) {
    size_t k = 0;
    int i;
    for (i = start; i < end; i++) {
        uint16_t c = l->src[i];
        if (c == '_') continue;
        if (c >= 0x80 || k + 1 >= cap) return false;
        out[k++] = (char)c;
    }
    out[k] = 0;
    return true;
}

static const char* lxReadUnitSuffix(FcLexer* l, int line, int col) {
    int unitStart = l->pos, powerStart;
    const char* unit;
    while (!lxAtEnd(l) && lxIsUnitSuffixChar(lxPeek(l))) lxAdvance(l);
    unit = lxText(l, unitStart, l->pos);
    if (l->pos == unitStart || lxAtEnd(l) || !fcIsDigit(lxPeek(l))) return unit;
    powerStart = l->pos;
    while (!lxAtEnd(l) && fcIsDigit(lxPeek(l))) lxAdvance(l);
    {
        const char* power = lxText(l, powerStart, l->pos);
        if (l->src[powerStart] == '0')
            lxError(l, fcFmt(l->ctx, "The power of a unit written as digits must be 1 or more ('%s%s')", unit, power), line, col);
        return fcFmt(l->ctx, "%s^%s", unit, power);
    }
}

static FcToken lxReadRadixNumber(FcLexer* l, int line, int col, bool nl, int start, int radix, bool (*isDigit)(uint16_t)) {
    int digitsStart;
    char digits[256];
    uint64_t value = 0;
    size_t i;
    FcToken t;
    const char* unit;
    lxAdvance(l); lxAdvance(l);
    digitsStart = l->pos;
    lxSkipDigits(l, isDigit);
    if (l->pos == digitsStart)
        lxError(l, fcFmt(l->ctx, "Expected at least one valid digit after '%s'", lxText(l, start, l->pos)), line, col);
    if (!lxNumberText(l, digitsStart, l->pos, digits, sizeof digits) || strlen(digits) > (radix == 16 ? 16u : 64u))
        fcThrow(l->ctx, FC_ERR_INTERNAL, line, col, NULL, "OverflowException: the literal does not fit into 64 bits (Convert.ToInt64)");
    for (i = 0; digits[i]; i++) {
        char d = digits[i];
        value = value * (uint64_t)radix + (uint64_t)(d >= 'a' ? d - 'a' + 10 : d >= 'A' ? d - 'A' + 10 : d - '0');
    }
    unit = lxReadUnitSuffix(l, line, col);
    t = lxTok(TT_IntLiteral, lxText(l, start, l->pos), line, col, nl);
    t.unitSuffix = unit;
    t.literal = FC_LIT_INT;
    t.intValue = (int64_t)value;
    return t;
}

static bool lxIsDotFollowerName(uint16_t c) { return fcIsLetterOrDigit(c) || c == '_' || c == '.' || c == '$'; }

static FcToken lxReadNumber(FcLexer* l, int line, int col, bool nl) {
    int start = l->pos;
    bool isFloat = false;
    char text[512];
    const char* unit;
    FcToken t;
    if (lxPeek(l) == '0' && (lxPeekNext(l) == 'x' || lxPeekNext(l) == 'X') && lxIsHexDigit(lxPeekAt(l, 2)))
        return lxReadRadixNumber(l, line, col, nl, start, 16, lxIsHexDigit);
    if (lxPeek(l) == '0' && (lxPeekNext(l) == 'b' || lxPeekNext(l) == 'B') && lxIsBinaryDigit(lxPeekAt(l, 2)))
        return lxReadRadixNumber(l, line, col, nl, start, 2, lxIsBinaryDigit);
    lxSkipDigits(l, lxIsDecDigit);
    if (!lxAtEnd(l) && lxPeek(l) == '.' && fcIsDigit(lxPeekNext(l))) {
        isFloat = true;
        lxAdvance(l);
        lxSkipDigits(l, lxIsDecDigit);
    } else if (!lxAtEnd(l) && lxPeek(l) == '.' && !lxIsDotFollowerName(lxPeekNext(l))) {
        isFloat = true;
        lxAdvance(l);
    }
    if (!lxAtEnd(l) && (lxPeek(l) == 'e' || lxPeek(l) == 'E')
        && (fcIsDigit(lxPeekNext(l)) || ((lxPeekNext(l) == '+' || lxPeekNext(l) == '-') && fcIsDigit(lxPeekAt(l, 2))))) {
        isFloat = true;
        lxAdvance(l);
        if (lxPeek(l) == '+' || lxPeek(l) == '-') lxAdvance(l);
        lxSkipDigits(l, lxIsDecDigit);
    }
    if (!lxNumberText(l, start, l->pos, text, sizeof text))
        fcThrow(l->ctx, FC_ERR_INTERNAL, line, col, NULL, "FormatException: a number with digits that are not ASCII");
    unit = lxReadUnitSuffix(l, line, col);
    t = lxTok(isFloat ? TT_FloatLiteral : TT_IntLiteral, lxText(l, start, l->pos), line, col, nl);
    t.unitSuffix = unit;
    if (isFloat) {
        t.literal = FC_LIT_FLOAT;
        t.floatValue = strtod(text, NULL);
    } else {
        /* up to 2^63-1 an int, up to 2^64-1 its 64 bits; beyond that an error */
        uint64_t v = 0;
        bool over = false;
        const char* p;
        for (p = text; *p; p++) {
            uint64_t d = (uint64_t)(*p - '0');
            if (v > (UINT64_MAX - d) / 10) { over = true; break; }
            v = v * 10 + d;
        }
        if (over) lxError(l, fcFmt(l->ctx, "The whole number '%s' does not fit into 64 bits", text), line, col);
        t.literal = FC_LIT_INT;
        t.intValue = (int64_t)v;
    }
    return t;
}

static FcToken lxReadIdentifierOrKeyword(FcLexer* l, int line, int col, bool nl) {
    int start = l->pos;
    const char* text;
    FcTokenType kw;
    while (!lxAtEnd(l) && lxIsIdentifierPart(lxPeek(l))) lxAdvance(l);
    text = lxText(l, start, l->pos);
    if (fcKeywordType(text, &kw)) return lxTok(kw, text, line, col, nl);
    return lxTok(TT_Identifier, text, line, col, nl);
}

static uint16_t lxUnescape(uint16_t c) {
    switch (c) {
        case 'n': return '\n';
        case 't': return '\t';
        case 'r': return '\r';
        case '\\': return '\\';
        case '\'': return '\'';
        case '"': return '"';
        case '0': return 0;
        default: return c;
    }
}

static FcToken lxReadString(FcLexer* l, int line, int col, bool nl) {
    FcBuf sb = {0};
    const char* value;
    FcToken t;
    fcBufStr(&sb, "");
    lxAdvance(l);
    while (!lxAtEnd(l) && lxPeek(l) != '"') {
        uint16_t c = lxAdvance(l);
        if (c == '\\') {
            if (lxAtEnd(l)) { fcBufFree(&sb); lxError(l, "Unterminated string literal", line, col); }
            fcBufUnit(&sb, lxUnescape(lxAdvance(l)));
        } else if (c == '\n') {
            fcBufFree(&sb);
            lxError(l, "String literals must not span multiple lines", line, col);
        } else fcBufUnit(&sb, c);
    }
    if (lxAtEnd(l)) { fcBufFree(&sb); lxError(l, "Unterminated string literal", line, col); }
    lxAdvance(l);
    value = fcBufDone(l->ctx, &sb);
    t = lxTok(TT_StringLiteral, value, line, col, nl);
    t.literal = FC_LIT_STRING;
    t.stringValue = value;
    return t;
}

static FcSegment* lxSegment(FcCtx* c, bool isExpression, const char* text, const char* format) {
    FcSegment* s = (FcSegment*)fcAlloc(c, sizeof *s);
    s->isExpression = isExpression; s->text = text; s->format = format;
    return s;
}

static FcSegment* lxReadInterpolationExprSegment(FcLexer* l, int line, int col) {
    FcBuf expr = {0};
    int depth = 0;
    fcBufStr(&expr, "");
    while (true) {
        uint16_t c;
        if (lxAtEnd(l)) { fcBufFree(&expr); lxError(l, "Unterminated expression in a format string", line, col); }
        c = lxPeek(l);
        if (c == '"') {
            fcBufUnit(&expr, c);
            lxAdvance(l);
            while (!lxAtEnd(l) && lxPeek(l) != '"') {
                if (lxPeek(l) == '\\') {
                    fcBufUnit(&expr, lxPeek(l));
                    lxAdvance(l);
                    if (!lxAtEnd(l)) { fcBufUnit(&expr, lxPeek(l)); lxAdvance(l); }
                    continue;
                }
                fcBufUnit(&expr, lxPeek(l));
                lxAdvance(l);
            }
            if (!lxAtEnd(l)) { fcBufUnit(&expr, lxPeek(l)); lxAdvance(l); }
            continue;
        }
        if (c == '(' || c == '[' || c == '{') { depth++; fcBufUnit(&expr, c); lxAdvance(l); continue; }
        if (c == ')' || c == ']') { depth--; fcBufUnit(&expr, c); lxAdvance(l); continue; }
        if (c == '}') {
            if (depth > 0) { depth--; fcBufUnit(&expr, c); lxAdvance(l); continue; }
            lxAdvance(l);
            return lxSegment(l->ctx, true, fcBufDone(l->ctx, &expr), NULL);
        }
        if (c == ':' && depth == 0) {
            FcBuf format = {0};
            fcBufStr(&format, "");
            lxAdvance(l);
            while (!lxAtEnd(l) && lxPeek(l) != '}') { fcBufUnit(&format, lxPeek(l)); lxAdvance(l); }
            if (lxAtEnd(l)) { fcBufFree(&expr); fcBufFree(&format); lxError(l, "Unterminated format specifier in a format string", line, col); }
            lxAdvance(l);
            {
                const char* e = fcBufDone(l->ctx, &expr);
                return lxSegment(l->ctx, true, e, fcBufDone(l->ctx, &format));
            }
        }
        if (c == '\n') { fcBufFree(&expr); lxError(l, "Format strings must not span multiple lines", line, col); }
        fcBufUnit(&expr, c);
        lxAdvance(l);
    }
}

static FcToken lxReadInterpolatedString(FcLexer* l, int line, int col, bool nl) {
    FcList* segments = fcListNew(l->ctx);
    FcBuf text = {0};
    FcToken t;
    lxAdvance(l);
    lxAdvance(l);
    while (true) {
        uint16_t c;
        if (lxAtEnd(l)) { fcBufFree(&text); lxError(l, "Unterminated format string", line, col); }
        c = lxPeek(l);
        if (c == '"') { lxAdvance(l); break; }
        if (c == '\n') { fcBufFree(&text); lxError(l, "Format strings must not span multiple lines", line, col); }
        if (c == '\\') {
            lxAdvance(l);
            if (lxAtEnd(l)) { fcBufFree(&text); lxError(l, "Unterminated format string", line, col); }
            fcBufUnit(&text, lxUnescape(lxAdvance(l)));
            continue;
        }
        if (c == '{' && lxPeekNext(l) == '{') { lxAdvance(l); lxAdvance(l); fcBufUnit(&text, '{'); continue; }
        if (c == '}' && lxPeekNext(l) == '}') { lxAdvance(l); lxAdvance(l); fcBufUnit(&text, '}'); continue; }
        if (c == '{') {
            if (text.len > 0) fcListAdd(l->ctx, segments, lxSegment(l->ctx, false, fcBufDone(l->ctx, &text), NULL));
            lxAdvance(l);
            fcListAdd(l->ctx, segments, lxReadInterpolationExprSegment(l, line, col));
            continue;
        }
        fcBufUnit(&text, c);
        lxAdvance(l);
    }
    if (text.len > 0) fcListAdd(l->ctx, segments, lxSegment(l->ctx, false, fcBufDone(l->ctx, &text), NULL));
    else fcBufFree(&text);
    t = lxTok(TT_InterpolatedStringLiteral, "$\"...\"", line, col, nl);
    t.literal = FC_LIT_SEGMENTS;
    t.segments = segments;
    return t;
}

static FcToken lxReadChar(FcLexer* l, int line, int col, bool nl) {
    uint16_t value, c;
    FcToken t;
    FcBuf b = {0};
    lxAdvance(l);
    if (lxAtEnd(l)) lxError(l, "Unterminated char literal", line, col);
    c = lxAdvance(l);
    if (c == '\\') {
        if (lxAtEnd(l)) lxError(l, "Unterminated char literal", line, col);
        value = lxUnescape(lxAdvance(l));
    } else value = c;
    if (lxAtEnd(l) || lxPeek(l) != '\'') lxError(l, "A char literal must contain exactly one character", line, col);
    lxAdvance(l);
    fcBufStr(&b, "");
    fcBufUnit(&b, value);
    t = lxTok(TT_CharLiteral, fcBufDone(l->ctx, &b), line, col, nl);
    t.literal = FC_LIT_CHAR;
    t.stringValue = t.lexeme;
    return t;
}

static FcToken lxReadOperatorOrPunctuation(FcLexer* l, int line, int col, bool nl) {
    uint16_t c = lxAdvance(l);
#define TOK(type, text) return lxTok(TT_##type, text, line, col, nl)
    switch (c) {
        case '+': if (lxMatch(l, '+')) TOK(PlusPlus, "++"); if (lxMatch(l, '=')) TOK(PlusAssign, "+="); TOK(Plus, "+");
        case '-': if (lxMatch(l, '-')) TOK(MinusMinus, "--"); if (lxMatch(l, '=')) TOK(MinusAssign, "-="); TOK(Minus, "-");
        case '*': if (lxMatch(l, '=')) TOK(StarAssign, "*="); TOK(Star, "*");
        case '%': if (lxMatch(l, '=')) TOK(PercentAssign, "%="); TOK(Percent, "%");
        case '.': if (lxMatch(l, '.')) TOK(DotDot, ".."); TOK(Dot, ".");
        case '~': TOK(Tilde, "~");
        case ',': TOK(Comma, ",");
        case ';': TOK(Semicolon, ";");
        case '(': TOK(LParen, "(");
        case ')': TOK(RParen, ")");
        case '{': TOK(LBrace, "{");
        case '}': TOK(RBrace, "}");
        case '[': TOK(LBracket, "[");
        case ']': TOK(RBracket, "]");
        case '#': if (lxMatch(l, '#')) TOK(NotEq, "##"); if (lxMatch(l, '=')) TOK(HashAssign, "#="); TOK(Hash, "#");
        case '/': if (lxMatch(l, '=')) TOK(SlashAssign, "/="); TOK(Slash, "/");
        case '?':
            if (lxPeek(l) == '.' && !fcIsDigit(lxPeekNext(l))) { lxAdvance(l); TOK(QuestionDot, "?."); }
            if (lxPeek(l) == '?' && lxPeekNext(l) == '.') { lxAdvance(l); lxAdvance(l); TOK(QuestionQuestionDot, "??."); }
            TOK(Question, "?");
        case ':': TOK(Colon, ":");
        case '!': if (lxMatch(l, '=')) TOK(NotEq, "!="); TOK(Bang, "!");
        case '=': if (lxMatch(l, '>')) TOK(Arrow, "=>"); if (lxMatch(l, '=')) TOK(Eq, "=="); TOK(Assign, "=");
        case '<':
            if (lxPeek(l) == '<' && lxPeekNext(l) == '=') { lxAdvance(l); lxAdvance(l); TOK(ShlAssign, "<<="); }
            if (lxMatch(l, '=')) TOK(LtEq, "<=");
            TOK(Lt, "<");
        case '>':
            if (lxPeek(l) == '>' && lxPeekNext(l) == '>' && lxPeekAt(l, 2) == '=') { lxAdvance(l); lxAdvance(l); lxAdvance(l); TOK(UShrAssign, ">>>="); }
            if (lxPeek(l) == '>' && lxPeekNext(l) == '=') { lxAdvance(l); lxAdvance(l); TOK(ShrAssign, ">>="); }
            if (lxMatch(l, '=')) TOK(GtEq, ">=");
            TOK(Gt, ">");
        case '&': if (lxMatch(l, '&')) TOK(And, "&&"); if (lxMatch(l, '=')) TOK(AmpAssign, "&="); TOK(Amp, "&");
        case '|': if (lxMatch(l, '|')) TOK(Or, "||"); if (lxMatch(l, '=')) TOK(PipeAssign, "|="); TOK(Pipe, "|");
        case '^': if (lxMatch(l, '=')) TOK(CaretAssign, "^="); TOK(Caret, "^");
        default: {
            FcBuf b = {0};
            fcBufStr(&b, "Unexpected character '");
            fcBufUnit(&b, c);
            fcBufStr(&b, "'");
            lxError(l, fcBufDone(l->ctx, &b), line, col);
        }
    }
#undef TOK
}

FcTokens fcTokenize(FcCtx* c, const uint16_t* source, int length) {
    FcLexer lx;
    FcLexer* l = &lx;
    FcTokens tokens = {0};
    bool pendingNewline = true;
    memset(&lx, 0, sizeof lx);
    lx.ctx = c; lx.src = source; lx.n = length; lx.line = 1; lx.col = 1;
    while (true) {
        int startLine, startCol, startPos;
        uint16_t ch;
        FcToken tok;
        pendingNewline |= lxSkipWhitespaceAndComments(l);
        if (lxAtEnd(l)) {
            lxAdd(c, &tokens, lxTok(TT_Eof, "", l->line, l->col, pendingNewline));
            break;
        }
        startLine = l->line; startCol = l->col; startPos = l->pos;
        ch = lxPeek(l);
        if (l->inlineC && ch == '{' && l->inlineParens == 0) {
            tok = lxReadInlineCode(l, startLine, startCol, pendingNewline);
            l->inlineC = false;
        }
        else if (fcIsDigit(ch)) tok = lxReadNumber(l, startLine, startCol, pendingNewline);
        else if (lxIsIdentifierStart(ch)) tok = lxReadIdentifierOrKeyword(l, startLine, startCol, pendingNewline);
        else if (ch == '$' && lxPeekNext(l) == '"') tok = lxReadInterpolatedString(l, startLine, startCol, pendingNewline);
        else if (ch == '"') tok = lxReadString(l, startLine, startCol, pendingNewline);
        else if (ch == '\'') tok = lxReadChar(l, startLine, startCol, pendingNewline);
        else tok = lxReadOperatorOrPunctuation(l, startLine, startCol, pendingNewline);
        tok.length = l->pos - startPos;
        lxAdd(c, &tokens, tok);
        pendingNewline = false;
        lxNoteInlineC(l, &tokens);
    }
    return tokens;
}

FcTokens fcTokenizeFc8(FcCtx* c, const char* source) {
    FcU16 u = {0};
    uint16_t* copy;
    int n = fcU16FromFc8(source, &u);
    copy = (uint16_t*)fcAlloc(c, sizeof(uint16_t) * (size_t)(n + 1));
    if (n) memcpy(copy, u.p, sizeof(uint16_t) * (size_t)n);
    fcU16Free(&u);
    return fcTokenize(c, copy, n);
}
