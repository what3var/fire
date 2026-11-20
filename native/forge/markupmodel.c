/* native/forge/markupmodel.c - port of src/fire.UI.Markup/MarkupModel.cs (commit 7a594769): MarkupDiagnostic, MarkupException (FC_ERR_MARKUP
 * with the diagnostics in `list`), the values of attributes, elements, the resources and MarkupDocument; plus the helpers of the markup port
 * that stand for .NET (OrdinalIgnoreCase, Trim, Regex `$`). Texts are fc8 (forge.h). */

/* MarkupDiagnostic */
typedef struct FmDiag { int line; const char* message; } FmDiag;

static FmDiag* fmDiag(FcCtx* c, int line, const char* message) {
    FmDiag* d = (FmDiag*)fcAlloc(c, sizeof *d);
    d->line = line;
    d->message = message;
    return d;
}
/* MarkupDiagnostic.ToString */
static const char* fmDiagText(FcCtx* c, const FmDiag* d) { return d->line > 0 ? fcFmt(c, "line %d: %s", d->line, d->message) : d->message; }

/* throw new MarkupException(diagnostics): the message is the diagnostics joined with "; " */
static _Noreturn void fmThrow(FcCtx* c, FcList* diagnostics) {
    FcBuf b = {0};
    int i;
    fcBufStr(&b, "");
    for (i = 0; i < diagnostics->count; i++) { if (i) fcBufStr(&b, "; "); fcBufStr(&b, fmDiagText(c, (const FmDiag*)diagnostics->items[i])); }
    c->err.kind = FC_ERR_MARKUP;
    c->err.message = fcBufDone(c, &b);
    c->err.line = 0;
    c->err.column = 0;
    c->err.file = NULL;
    c->err.list = diagnostics;
    fcRethrow(c);
}
static _Noreturn void fmThrow1(FcCtx* c, int line, const char* message) {
    FcList* l = fcListNew(c);
    fcListAdd(c, l, fmDiag(c, line, message));
    fmThrow(c, l);
}

/* MarkupRootKind, BindingMode */
typedef enum FmRootKind { FM_WINDOW, FM_VIEW } FmRootKind;
typedef enum FmBindingMode { FM_ONEWAY, FM_TWOWAY, FM_ONETIME } FmBindingMode;
static const char* const fmBindingModeNames[] = { "OneWay", "TwoWay", "OneTime" };

/* MarkupValue: LiteralValue (text), ExpressionValue (code, isRaw), ObjectValue (typeName, code, previewSafe), BindingValue (path, mode, converter,
 * elementName), TemplateBindingValue (ownerProperty, in `text`) */
typedef enum FmValueKind { FM_LITERAL, FM_EXPRESSION, FM_OBJECT, FM_BINDING, FM_TEMPLATEBINDING } FmValueKind;
typedef struct FmValue {
    FmValueKind kind;
    const char* text;
    bool isRaw;
    const char* typeName;
    bool previewSafe;
    const char* path;
    FmBindingMode mode;
    const char* converter;
    const char* elementName;
} FmValue;

static FmValue* fmValue(FcCtx* c, FmValueKind kind, const char* text) {
    FmValue* v = (FmValue*)fcAlloc(c, sizeof *v);
    v->kind = kind;
    v->text = text;
    return v;
}

/* MarkupAttribute */
typedef struct FmAttr { const char* name; FmValue* value; int line; } FmAttr;
static FmAttr* fmAttr(FcCtx* c, const char* name, FmValue* value, int line) {
    FmAttr* a = (FmAttr*)fcAlloc(c, sizeof *a);
    a->name = name;
    a->value = value;
    a->line = line;
    return a;
}
typedef struct FmHandler { const char* event; const char* handler; int line; } FmHandler;

/* MarkupElement */
typedef struct FmElement {
    const char* tag;
    int line;
    const char* name;
    FcList* attributes;   /* FmAttr* */
    FcList* handlers;     /* FmHandler* */
    FcList* children;     /* FmElement* */
} FmElement;

static FmElement* fmElement(FcCtx* c, const char* tag, int line) {
    FmElement* e = (FmElement*)fcAlloc(c, sizeof *e);
    e->tag = tag;
    e->line = line;
    e->attributes = fcListNew(c);
    e->handlers = fcListNew(c);
    e->children = fcListNew(c);
    return e;
}

/* ---- what .NET does for the markup ---- */

/* a UTF-16 unit in simple upper case (ASCII and Latin-1; what OrdinalIgnoreCase and ToUpperInvariant do with these) */
static uint16_t fmUpper(uint16_t u) {
    if (u >= 'a' && u <= 'z') return (uint16_t)(u - 32);
    if (u >= 0xE0 && u <= 0xFE && u != 0xF7) return (uint16_t)(u - 32);
    if (u == 0xFF) return 0x178;
    if (u == 0xB5) return 0x39C;
    return u;
}
static uint16_t fmLower(uint16_t u) {
    if (u >= 'A' && u <= 'Z') return (uint16_t)(u + 32);
    if (u >= 0xC0 && u <= 0xDE && u != 0xD7) return (uint16_t)(u + 32);
    if (u == 0x178) return 0xFF;
    return u;
}
static bool fmIsUpper(uint16_t u) { return (u >= 'A' && u <= 'Z') || (u >= 0xC0 && u <= 0xDE && u != 0xD7); }
static bool fmIsLower(uint16_t u) { return (u >= 'a' && u <= 'z') || (u >= 0xDF && u <= 0xFF && u != 0xF7) || u == 0xB5 || u == 0xAA || u == 0xBA; }

/* string.Equals(a, b, StringComparison.OrdinalIgnoreCase) */
static bool fmIEq(const char* a, const char* b) {
    const unsigned char* p = (const unsigned char*)a;
    const unsigned char* q = (const unsigned char*)b;
    if (!a || !b) return a == b;
    while (*p && *q) if (fmUpper(fc8Next(&p)) != fmUpper(fc8Next(&q))) return false;
    return !*p && !*q;
}
/* ToLowerInvariant / ToUpperInvariant */
static const char* fmToLower(FcCtx* c, const char* s) {
    FcBuf b = {0};
    const unsigned char* p = (const unsigned char*)s;
    fcBufStr(&b, "");
    while (*p) fcBufUnit(&b, fmLower(fc8Next(&p)));
    return fcBufDone(c, &b);
}
static const char* fmToUpper(FcCtx* c, const char* s) {
    FcBuf b = {0};
    const unsigned char* p = (const unsigned char*)s;
    fcBufStr(&b, "");
    while (*p) fcBufUnit(&b, fmUpper(fc8Next(&p)));
    return fcBufDone(c, &b);
}
/* string.Trim() (char.IsWhiteSpace) */
static const char* fmTrim(FcCtx* c, const char* s) {
    const unsigned char* p = (const unsigned char*)s;
    const unsigned char* start = NULL;
    const unsigned char* end = p;
    while (*p) {
        const unsigned char* at = p;
        uint16_t u = fc8Next(&p);
        if (!fcIsWhiteSpace(u)) { if (!start) start = at; end = p; }
    }
    if (!start) return "";
    return fcStrN(c, (const char*)start, (size_t)(end - start));
}
/* string.IsNullOrWhiteSpace */
static bool fmIsBlank(const char* s) {
    const unsigned char* p = (const unsigned char*)s;
    if (!s) return true;
    while (*p) if (!fcIsWhiteSpace(fc8Next(&p))) return false;
    return true;
}
/* string.Concat of a unit's fc8 */
static FC_LATER const char* fmUnitText(FcCtx* c, uint16_t u) {
    FcBuf b = {0};
    fcBufStr(&b, "");
    fcBufUnit(&b, u);
    return fcBufDone(c, &b);
}
/* the units of an fc8 text (in the arena) */
static uint16_t* fmUnits(FcCtx* c, const char* s, int* n) {
    FcU16 u = {0};
    uint16_t* r;
    *n = fcU16FromFc8(s, &u);
    r = (uint16_t*)fcAlloc(c, sizeof(uint16_t) * (size_t)(*n + 1));
    if (*n) memcpy(r, u.p, sizeof(uint16_t) * (size_t)*n);
    fcU16Free(&u);
    return r;
}
/* text.Replace(from, to) (ordinal) */
static const char* fmReplace(FcCtx* c, const char* s, const char* from, const char* to) {
    FcBuf b = {0};
    size_t n = strlen(from);
    const char* p = s;
    const char* q;
    if (!n || !strstr(s, from)) return s;
    fcBufStr(&b, "");
    while ((q = strstr(p, from)) != NULL) { fcBufPut(&b, p, (size_t)(q - p)); fcBufStr(&b, to); p = q + n; }
    fcBufStr(&b, p);
    return fcBufDone(c, &b);
}
/* the end of a Regex match with `$`: the end of the text, or a final \n */
static bool fmRegexEnd(const char* p) { return p[0] == 0 || (p[0] == '\n' && p[1] == 0); }

/* ConverterDeclaration, SetterDeclaration, TriggerDeclaration (a condition: FmCondition), StyleDeclaration, TemplateDeclaration,
 * DataTemplateDeclaration, ViewDeclaration */
typedef struct FmConverter { const char* key; const char* type; int line; } FmConverter;
typedef struct FmSetter { const char* property; FmValue* value; const char* target; int line; } FmSetter;
typedef struct FmCondition { const char* property; FmValue* value; const char* source; int line; } FmCondition;
typedef struct FmTrigger { int line; FcList* conditions; FcList* setters; } FmTrigger;
typedef struct FmStyle { const char* key; const char* target; const char* basedOn; int line; FcList* setters; FcList* triggers; } FmStyle;
typedef struct FmTemplate { const char* key; const char* target; int line; FmElement* root; FcList* triggers; } FmTemplate;
typedef struct FmDataTemplate { const char* key; int line; FmElement* root; } FmDataTemplate;
typedef struct FmView { const char* key; int line; FmElement* element; } FmView;

/* MarkupDocument */
typedef struct FmDocument {
    FmRootKind kind;
    const char* className;
    const char* baseName;
    const char* title;
    const char* ns;            /* NULL: none */
    FcList* nsPrefixes;        /* Namespaces: prefix -> namespace, in the order of the dictionary */
    FcList* nsValues;
    int width, height;
    FcList* rootAttributes;    /* FmAttr* */
    FcList* converters;
    FcList* styles;
    FcList* templates;
    FcList* dataTemplates;
    FcList* views;
    FcList* children;          /* FmElement* */
    FcList* diagnostics;       /* FmDiag* */
} FmDocument;

static FmDocument* fmDocument(FcCtx* c) {
    FmDocument* d = (FmDocument*)fcAlloc(c, sizeof *d);
    d->title = "fire";
    d->width = 640;
    d->height = 480;
    d->nsPrefixes = fcListNew(c);
    d->nsValues = fcListNew(c);
    d->rootAttributes = fcListNew(c);
    d->converters = fcListNew(c);
    d->styles = fcListNew(c);
    d->templates = fcListNew(c);
    d->dataTemplates = fcListNew(c);
    d->views = fcListNew(c);
    d->children = fcListNew(c);
    d->diagnostics = fcListNew(c);
    return d;
}

/* MarkupDocument.Walk: depth first, the children before the next sibling */
static void fmWalkInto(FcCtx* c, FcList* out, FmElement* e) {
    int i;
    fcListAdd(c, out, e);
    for (i = 0; i < e->children->count; i++) fmWalkInto(c, out, (FmElement*)e->children->items[i]);
}
/* AllElements(), TemplateElements(), DataTemplateElements() */
static FcList* fmAllElements(FcCtx* c, const FmDocument* d) {
    FcList* out = fcListNew(c);
    int i;
    for (i = 0; i < d->children->count; i++) fmWalkInto(c, out, (FmElement*)d->children->items[i]);
    return out;
}
static FcList* fmTemplateElements(FcCtx* c, const FmDocument* d) {
    FcList* out = fcListNew(c);
    int i;
    for (i = 0; i < d->templates->count; i++) { FmTemplate* t = (FmTemplate*)d->templates->items[i]; if (t->root) fmWalkInto(c, out, t->root); }
    return out;
}
static FcList* fmDataTemplateElements(FcCtx* c, const FmDocument* d) {
    FcList* out = fcListNew(c);
    int i;
    for (i = 0; i < d->dataTemplates->count; i++) { FmDataTemplate* t = (FmDataTemplate*)d->dataTemplates->items[i]; if (t->root) fmWalkInto(c, out, t->root); }
    return out;
}
/* MarkupElement.Find: the attribute of the name, whatever its case */
static FmAttr* fmFind(const FmElement* e, const char* name) {
    int i;
    for (i = 0; i < e->attributes->count; i++) if (fmIEq(((FmAttr*)e->attributes->items[i])->name, name)) return (FmAttr*)e->attributes->items[i];
    return NULL;
}
