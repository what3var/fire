/* native/forge/fireuigenerator.c - port of src/fire.UI.Markup/FireUiGenerator.cs (commit 7a594769): an FmDocument as the fire script of the
 * interface (docs/ui/markup.md) - ONE class, the base class of the code-behind, with the elements, the handlers, the data bindings (probes, with the
 * undefined that a broken path passes on) and Run()/Attach(). GeneratePreview's flag ([ThreadStatic] s_preview) travels in FmGen (L6). Also the
 * number formats of .NET the markup port needs (int.TryParse, double.TryParse, the custom format "0.0###..."). */

/* ---- .NET's numbers ---- */

/* the digits of a double as .NET's custom formats see it: 15 significant digits (DoublePrecisionCustomFormat), trailing zeros dropped; scale: the
 * position of the decimal point */
static void fmDigits15(double a, char* digits, int* scale) {
    char buf[64];
    int k = 0, e, i;
    char* p;
    if (a == 0) { digits[0] = 0; *scale = 0; return; }
    snprintf(buf, sizeof buf, "%.14e", a);
    for (p = buf; *p && *p != 'e'; p++) if (*p >= '0' && *p <= '9') digits[k++] = *p;
    digits[k] = 0;
    e = atoi(p + 1);
    *scale = e + 1;
    for (i = k - 1; i >= 0 && digits[i] == '0'; i--) digits[i] = 0;
}
/* double.ToString("0.0##...") style: at least minFrac, at most maxFrac decimals (RoundNumber: half up on the 15 digits) */
static const char* fmFormatCustom(FcCtx* c, double v, int minFrac, int maxFrac) {
    char d[40];
    int scale, len, pos, i;
    FcBuf b = {0};
    if (isnan(v)) return "NaN";
    if (isinf(v)) return v > 0 ? "Infinity" : "-Infinity";
    fmDigits15(fabs(v), d, &scale);
    len = (int)strlen(d);
    pos = scale + maxFrac;
    i = 0;
    while (i < pos && i < len) i++;
    if (i == pos && pos >= 0 && pos < len && d[pos] >= '5') {
        while (i > 0 && d[i - 1] == '9') i--;
        if (i > 0) d[i - 1]++;
        else { scale++; d[0] = '1'; i = 1; }
    } else {
        if (pos < 0) i = 0;
        while (i > 0 && d[i - 1] == '0') i--;
    }
    if (i == 0) scale = 0;
    d[i] = 0;
    len = i;
    fcBufStr(&b, "");
    if (signbit(v)) fcBufChar(&b, '-');
    if (scale <= 0) fcBufChar(&b, '0');
    else for (i = 0; i < scale; i++) fcBufChar(&b, i < len ? d[i] : '0');
    {
        char frac[64];
        int n = 0;
        for (i = 0; i < maxFrac; i++) {
            int at = scale + i;
            frac[n++] = at >= 0 && at < len ? d[at] : '0';
        }
        while (n > minFrac && frac[n - 1] == '0') n--;
        if (n > 0) { fcBufChar(&b, '.'); fcBufPut(&b, frac, (size_t)n); }
    }
    return fcBufDone(c, &b);
}

/* int.TryParse(text, NumberStyles.AllowLeadingSign or None, CultureInfo.InvariantCulture) */
static bool fmParseIntStyle(const char* p, bool sign, int* out) {
    bool neg = false, any = false;
    int64_t v = 0;
    if (sign && (*p == '+' || *p == '-')) { neg = *p == '-'; p++; }
    while (*p >= '0' && *p <= '9') {
        v = v * 10 + (*p - '0');
        if (v > 2147483648LL) return false;
        p++;
        any = true;
    }
    if (!any || *p) return false;
    if (neg) v = -v;
    if (v > 2147483647LL) return false;
    *out = (int)v;
    return true;
}
/* int.TryParse(text, NumberStyles.HexNumber): white space around, hex digits, the bits of a uint */
static bool fmParseHex(const char* p, int* out) {
    uint64_t v = 0;
    bool any = false;
    while (fmNumWhite(*p)) p++;
    while (isxdigit((unsigned char)*p)) {
        int d = *p >= '0' && *p <= '9' ? *p - '0' : (*p | 0x20) - 'a' + 10;
        v = v * 16 + (uint64_t)d;
        if (v > 0xFFFFFFFFull) return false;
        p++;
        any = true;
    }
    if (!any) return false;
    while (fmNumWhite(*p)) p++;
    if (*p) return false;
    *out = (int)(uint32_t)v;
    return true;
}
/* double.TryParse(text, AllowLeadingSign | AllowDecimalPoint, invariant) - and the symbols .NET always accepts */
static bool fmParseNumber(const char* t, double* out) {
    const char* p = t;
    bool any = false;
    const char* s = t;
    if (*p == '+' || *p == '-') p++;
    while (*p >= '0' && *p <= '9') { p++; any = true; }
    if (*p == '.') { p++; while (*p >= '0' && *p <= '9') { p++; any = true; } }
    if (any && !*p) { *out = strtod(t, NULL); return true; }
    if (*s == '+' || *s == '-') s++;
    if (fmIEq(t, "Infinity") || fmIEq(t, "+Infinity") || strcmp(t, "\xE2\x88\x9E") == 0) { *out = INFINITY; return true; }
    if (fmIEq(t, "-Infinity") || strcmp(t, "-\xE2\x88\x9E") == 0) { *out = -INFINITY; return true; }
    if (fmIEq(s, "NaN")) { *out = NAN; return true; }
    return false;
}

/* ---- the generator ---- */

typedef struct FmInfo {
    FmElement* element;
    const FmElementDef* def;
    const char* local;
    const char* field;          /* NULL: none */
    const char* handlerField;
    bool inTemplate;
} FmInfo;

typedef struct FmBinding {
    int index;
    FmInfo* target;
    const FmPropDef* property;
    const FmValue* value;
    const char* source;
    const char* converterField;
} FmBinding;

typedef struct FmTemplatePartBinding { const char* part; const char* partProperty; const char* ownerProperty; } FmTemplatePartBinding;

typedef struct FmGen {
    FcCtx* c;
    bool preview;               /* s_preview */
    FcList* errors;             /* FmDiag* */
    FcList* infoKeys;           /* Dictionary<MarkupElement, Info>: keys and values in insertion order */
    FcList* infoValues;
    FcBuf text;                 /* Writer */
    int indent;
} FmGen;

static FmInfo* fmInfoOf(FmGen* g, const FmElement* e) {
    int i;
    for (i = 0; i < g->infoKeys->count; i++) if (g->infoKeys->items[i] == e) return (FmInfo*)g->infoValues->items[i];
    return NULL;
}
static void fmInfoSet(FmGen* g, FmElement* e, FmInfo* info) {
    int i;
    for (i = 0; i < g->infoKeys->count; i++) if (g->infoKeys->items[i] == e) { g->infoValues->items[i] = info; return; }
    fcListAdd(g->c, g->infoKeys, e);
    fcListAdd(g->c, g->infoValues, info);
}
static FmInfo* fmNewInfo(FmGen* g, FmElement* e, const FmElementDef* def, const char* local, const char* field) {
    FmInfo* info = (FmInfo*)fcAlloc(g->c, sizeof *info);
    info->element = e;
    info->def = def;
    info->local = local;
    info->field = field;
    return info;
}

/* Writer.Line */
static void fmLine(FmGen* g, const char* text) {
    int i;
    if (text[0]) { for (i = 0; i < g->indent * 4; i++) fcBufChar(&g->text, ' '); fcBufStr(&g->text, text); }
    fcBufChar(&g->text, '\n');
}
static void fmLineF(FmGen* g, const char* fmt, ...) {
    va_list ap;
    FcBuf b = {0};
    char tmp[1];
    int n;
    va_start(ap, fmt);
    n = vsnprintf(tmp, 1, fmt, ap);
    va_end(ap);
    b.p = (char*)malloc((size_t)n + 1);
    va_start(ap, fmt);
    vsnprintf(b.p, (size_t)n + 1, fmt, ap);
    va_end(ap);
    fmLine(g, b.p);
    free(b.p);
}

/* Quote: a fire string literal */
static const char* fmUiQuote(FcCtx* c, const char* text) {
    FcBuf b = {0};
    const char* p;
    fcBufChar(&b, '"');
    for (p = text; *p; p++) {
        switch (*p) {
            case '\\': fcBufStr(&b, "\\\\"); break;
            case '"': fcBufStr(&b, "\\\""); break;
            case '\n': fcBufStr(&b, "\\n"); break;
            case '\r': fcBufStr(&b, "\\r"); break;
            case '\t': fcBufStr(&b, "\\t"); break;
            default: fcBufChar(&b, *p);
        }
    }
    fcBufChar(&b, '"');
    return fcBufDone(c, &b);
}

/* TryParseColor: #RRGGBB (or #RGB) */
static bool fmTryParseColor(const char* text, int* r, int* g, int* b) {
    char hex[16];
    int rgb, n;
    *r = *g = *b = 0;
    if (text[0] != '#') return false;
    n = fc8Units(text + 1);
    if (n == 3 && strlen(text + 1) == 3) {
        int k;
        for (k = 0; k < 3; k++) { hex[2 * k] = text[1 + k]; hex[2 * k + 1] = text[1 + k]; }
        hex[6] = 0;
    } else if (n == 6 && strlen(text + 1) == 6) memcpy(hex, text + 1, 7);
    else return false;
    if (!fmParseHex(hex, &rgb)) return false;
    *r = (rgb >> 16) & 0xFF;
    *g = (rgb >> 8) & 0xFF;
    *b = rgb & 0xFF;
    return true;
}

static void fmError(FmGen* g, int line, const char* message) { fcListAdd(g->c, g->errors, fmDiag(g->c, line, message)); }

static const char* fmJoinChoices(FcCtx* c, const char* const* choices) {
    FcBuf b = {0};
    int i;
    fcBufStr(&b, "");
    for (i = 0; choices && choices[i]; i++) { if (i) fcBufStr(&b, ", "); fcBufStr(&b, choices[i]); }
    return fcBufDone(c, &b);
}

/* String.Split with TrimEntries (and RemoveEmptyEntries) on the given separators */
static FcList* fmSplitTrim(FcCtx* c, const char* s, const char* separators, bool removeEmpty) {
    FcList* l = fcListNew(c);
    const char* p = s;
    for (;;) {
        const char* q = p;
        const char* e;
        while (*q && !strchr(separators, *q)) q++;
        e = fmTrim(c, fcStrN(c, p, (size_t)(q - p)));
        if (!removeEmpty || e[0]) fcListAdd(c, l, (void*)e);
        if (!*q) break;
        p = q + 1;
    }
    return l;
}

/* Convert: the fire expression of a plain value, by the type of the property */
static const char* fmConvert(FmGen* g, const FmPropDef* property, const char* text, int line) {
    FcCtx* c = g->c;
    const char* trimmed = fmTrim(c, text);
    int n, r, gg, b, i;
    double d;
    switch (property->kind) {
        case PK_Text:
            return fmUiQuote(c, text);
        case PK_Int:
            if (fmParseIntStyle(trimmed, true, &n)) return fcFmt(c, "%d", n);
            if ((fcStartsWith(trimmed, "0x") || fcStartsWith(trimmed, "0X")) && fmParseHex(trimmed + 2, &n)) return fcFmt(c, "0x%X", (unsigned)n);
            fmError(g, line, fcFmt(c, "'%s' needs a whole number (or {Enum Type.Member}), not '%s'.", property->name, text));
            return NULL;
        case PK_Number:
            if (fmParseNumber(trimmed, &d)) return fmFormatCustom(c, d, 1, 16);
            fmError(g, line, fcFmt(c, "'%s' needs a number (0.5, -2, 100), not '%s'.", property->name, text));
            return NULL;
        case PK_Bool:
            if (fmIEq(trimmed, "true")) return "true";
            if (fmIEq(trimmed, "false")) return "false";
            fmError(g, line, fcFmt(c, "'%s' needs true or false, not '%s'.", property->name, text));
            return NULL;
        case PK_Orientation:
            if (fmIEq(trimmed, "Horizontal")) return "true";
            if (fmIEq(trimmed, "Vertical")) return "false";
            fmError(g, line, fcFmt(c, "'%s' needs Horizontal or Vertical, not '%s'.", property->name, text));
            return NULL;
        case PK_Color:
            if (fmTryParseColor(trimmed, &r, &gg, &b)) return fcFmt(c, "new SolidBrush(UI.Color.Rgb(%d, %d, %d))", r, gg, b);
            if (fmParseIntStyle(trimmed, true, &n)) return fcFmt(c, "new SolidBrush(%d)", n);
            fmError(g, line, fcFmt(c, "'%s' needs a colour like #RRGGBB (or a whole number or {Enum Type.Member}), not '%s'.", property->name, text));
            return NULL;
        case PK_Pen: {
            FcList* parts = fmSplitTrim(c, trimmed, ",", false);
            const char* colorCode = NULL;
            int width;
            const char* p0 = (const char*)parts->items[0];
            if (fmTryParseColor(p0, &r, &gg, &b)) colorCode = fcFmt(c, "UI.Color.Rgb(%d, %d, %d)", r, gg, b);
            else if (fmParseIntStyle(p0, true, &n)) colorCode = fcFmt(c, "%d", n);
            if (colorCode && parts->count == 1) return fcFmt(c, "new Pen(%s)", colorCode);
            if (colorCode && parts->count == 2 && fmParseIntStyle((const char*)parts->items[1], false, &width) && width > 0) return fcFmt(c, "new Pen(%s, %d)", colorCode, width);
            fmError(g, line, fcFmt(c, "'%s' needs a pen like #RRGGBB or #RRGGBB,3 (colour and width), not '%s'.", property->name, text));
            return NULL;
        }
        case PK_Thickness: {
            FcList* numbers = fmSplitTrim(c, trimmed, ", ", true);
            bool ok = numbers->count == 1 || numbers->count == 2 || numbers->count == 4;
            FcBuf o = {0};
            for (i = 0; ok && i < numbers->count; i++) if (!fmParseIntStyle((const char*)numbers->items[i], true, &n)) ok = false;
            if (ok) {
                fcBufStr(&o, "new UI.Thickness(");
                for (i = 0; i < numbers->count; i++) { fmParseIntStyle((const char*)numbers->items[i], true, &n); if (i) fcBufStr(&o, ", "); fcBufFmt(&o, "%d", n); }
                fcBufChar(&o, ')');
                return fcBufDone(c, &o);
            }
            fmError(g, line, fcFmt(c, "'%s' needs 1 number (all sides), 2 (horizontal, vertical) or 4 (left, top, right, bottom), not '%s'.", property->name, text));
            return NULL;
        }
        case PK_Enum:
            for (i = 0; property->choices && property->choices[i]; i++)
                if (fmIEq(property->choices[i], trimmed)) return fcFmt(c, "%s.%s", property->enumType, property->choices[i]);
            fmError(g, line, fcFmt(c, "'%s' needs one of %s, not '%s'.", property->name, fmJoinChoices(c, property->choices), text));
            return NULL;
        case PK_Image:
            if (!trimmed[0]) {
                fmError(g, line, fcFmt(c, "'%s' needs the path of an image file.", property->name));
                return NULL;
            }
            return fcFmt(c, "Framebuffer.FromResource(new Resource(%s))", fmUiQuote(c, trimmed));
        case PK_Geometry:
            if (!trimmed[0]) {
                fmError(g, line, fcFmt(c, "'%s' needs the name of a symbol (%s) or path data like M 8 1 L 15 8 L 1 8 Z.", property->name, fmJoinChoices(c, fmIconNames)));
                return NULL;
            }
            for (i = 0; fmIconNames[i]; i++) if (fmIEq(fmIconNames[i], trimmed)) return fcFmt(c, "UI.Icons.%s()", fmIconNames[i]);
            return fcFmt(c, "UI.Geometry.Parse(%s)", fmUiQuote(c, trimmed));
        case PK_StyleRef: return fcFmt(c, "this.fxStyle_%s", trimmed);
        case PK_TemplateRef: return fcFmt(c, "this.fxTemplate_%s", trimmed);
        case PK_DataTemplateRef: return fcFmt(c, "this.fxData_%s", trimmed);
        case PK_ViewRef: return fcFmt(c, "this.fxView_%s, false", trimmed);   /* the view belongs to the window, not to the list (SetView(view, own)) */
        case PK_Collection:
        case PK_Code:
            fmError(g, line, fcFmt(c, "'%s' needs {Binding ...} or {Expr ...}.", property->name));
            return NULL;
    }
    return NULL;
}

/* ValueCode: the fire expression of the value of an attribute (a literal, `{Enum}`, `{Expr}`), by the type of the property */
static const char* fmValueCode(FmGen* g, const FmPropDef* property, const FmValue* value, int line) {
    switch (value->kind) {
        case FM_EXPRESSION:
            if (g->preview) return NULL;   /* code of the program: the design view does not run it */
            if (property->kind == PK_Color && !value->isRaw) return fcFmt(g->c, "new SolidBrush(%s)", value->text);
            if (property->kind == PK_Pen && !value->isRaw) return fcFmt(g->c, "new Pen(%s)", value->text);
            return value->text;
        case FM_OBJECT:
            if (!value->previewSafe && g->preview) return NULL;
            return value->text;   /* a ready value (a brush, a pen, any object): no SolidBrush/Pen around it */
        case FM_LITERAL:
            return fmConvert(g, property, value->text, line);
        default:
            return NULL;
    }
}

static const char* fmReplacePlaceholder(FcCtx* c, const char* s, const char* placeholder, const char* value) { return fmReplace(c, s, placeholder, value); }

/* EmitElement */
static void fmEmitElement(FmGen* g, FmElement* element, const char* parent, const FmElementDef* parentDef, FcList* partBindings,
                          const FmElementDef* templateTarget, const char* dataItem) {
    FcCtx* c = g->c;
    FmInfo* info = fmInfoOf(g, element);
    const char* local = info->local;
    const char* partName = NULL;
    int i, k;

    /* a part without an object of its own (a list item, a column, a menu separator): only the call that adds it */
    if (info->def->cls[0] == 0) {
        const char* call = parentDef ? fmAddCallIn(info->def, parentDef->tag) : info->def->addCall;
        call = fmReplacePlaceholder(c, call, "{p}", parent ? parent : "");
        for (i = 0; i < info->def->properties->count; i++) {
            const FmPropDef* property = (const FmPropDef*)info->def->properties->items[i];
            const char* placeholder = fcFmt(c, "{%s}", property->name);
            FmAttr* attribute;
            const char* value = NULL;
            if (!strstr(call, placeholder)) continue;
            attribute = fmFind(element, property->name);
            if (attribute) value = fmValueCode(g, property, attribute->value, attribute->line);
            call = fmReplacePlaceholder(c, call, placeholder, value ? value : property->def ? property->def : "\"\"");
        }
        fmLine(g, call);
        return;
    }

    fmLineF(g, "var %s = %s", local, info->def->create);
    if (g->preview && !partBindings && !fmIsPart(info->def)) fmLineF(g, "this.fxAll.Add(%s)", local);

    if (partBindings) {
        bool bound = false;
        for (i = 0; i < element->attributes->count; i++) if (((FmAttr*)element->attributes->items[i])->value->kind == FM_TEMPLATEBINDING) bound = true;
        if (element->name || bound) {
            partName = element->name ? element->name : fcFmt(c, "fxPart%s", local + 1);
            fmLineF(g, "%s.Name = %s", local, fmUiQuote(c, partName));
        }
    }

    for (i = 0; i < element->attributes->count; i++) {
        FmAttr* attribute = (FmAttr*)element->attributes->items[i];
        const FmPropDef* property;
        const char* value;
        if (attribute->value->kind == FM_BINDING) {
            const FmValue* shown = attribute->value;
            const FmPropDef* textProperty = fmProperty(info->def, attribute->name);
            /* the design view shows the path of a bound text; the other bound properties keep their default */
            if (g->preview && textProperty && textProperty->kind == PK_Text && !textProperty->method)
                fmLineF(g, "%s.%s = %s", local, fmFieldName(textProperty), fmUiQuote(c, fcFmt(c, "\xE2\x80\xB9%s\xE2\x80\xBA", shown->path[0] == 0 ? "item" : shown->path)));
            else if (dataItem && !g->preview) {
                /* inside a data template: the property follows a property of the item (checked at every layout, see UI.Binding) */
                const FmPropDef* bound = fmProperty(info->def, attribute->name);
                if (bound->method) fmError(g, attribute->line, fcFmt(c, "'%s' of '%s' cannot be bound.", attribute->name, info->def->tag));
                else {
                    const char* converter = shown->converter ? fcFmt(c, "this.fxConv_%s", shown->converter) : "undefined";
                    fmLineF(g, "%s.Bind(%s, %s, %s, %s, %s, %s)", local, fmUiQuote(c, fmFieldName(bound)), dataItem, fmUiQuote(c, shown->path),
                        shown->mode == FM_TWOWAY ? "true" : "false", converter, shown->mode == FM_ONETIME ? "true" : "false");
                }
            }
            continue;   /* made by the binding */
        }
        property = fmProperty(info->def, attribute->name);
        if (attribute->value->kind == FM_TEMPLATEBINDING) {
            if (partBindings && partName) {
                const FmPropDef* owner = templateTarget ? fmProperty(templateTarget, attribute->value->text) : NULL;
                const FmPropDef* state = fmStateProperty(attribute->value->text);
                FmTemplatePartBinding* pb = (FmTemplatePartBinding*)fcAlloc(c, sizeof *pb);
                pb->part = partName;
                pb->partProperty = fmFieldName(property);
                pb->ownerProperty = owner ? fmFieldName(owner) : state ? fmFieldName(state) : attribute->value->text;
                fcListAdd(c, partBindings, pb);
            }
            continue;
        }
        if (g->preview && property->kind == PK_ViewRef) continue;   /* the design view shows sample rows instead */
        value = fmValueCode(g, property, attribute->value, attribute->line);
        if (!value) continue;
        if (property->method) fmLineF(g, "%s.%s(%s)", local, property->method, value);
        else fmLineF(g, "%s.%s = %s", local, fmFieldName(property), value);
    }

    /* the design view cannot run the data: a list that gets its rows from one shows three empty ones */
    if (g->preview && fmProperty(info->def, "itemsSource") && (fmFind(element, "itemsSource") || fmFind(element, "view"))) {
        bool items = false;
        for (i = 0; i < element->children->count; i++) if (strcmp(((FmElement*)element->children->items[i])->tag, "Item") == 0) items = true;
        if (!items) for (i = 0; i < 3; i++) fmLineF(g, "%s.Add(\"\")", local);
    }

    if (!g->preview && element->handlers->count > 0) {
        if (info->handlerField) fmLineF(g, "this.%s = %s", info->handlerField, local);
        else if (info->inTemplate && !info->field) { fmLineF(g, "var %sAt = this.fxTemplateEls.Count", local); fmLineF(g, "this.fxTemplateEls.Add(%s)", local); }
    }
    if (!g->preview)
        for (i = 0; i < element->handlers->count; i++) {
            FmHandler* h = (FmHandler*)element->handlers->items[i];
            const FmEventDef* ev = fmEvent(info->def, h->event);
            int nargs = fmArgCount(ev);
            FcBuf params = {0}, args = {0};
            const char* element0;
            fcBufStr(&params, "");
            fcBufStr(&args, "");
            for (k = 0; k < nargs; k++) {
                if (k) fcBufStr(&params, ", ");
                fcBufFmt(&params, "%s %s", fmArgType(ev, k), ev->args[k]);
                fcBufFmt(&args, ", %s", ev->args[k]);
            }
            /* (the handler finds its element through `this` - a field, or its place in the list of the template parts) */
            element0 = info->field ? fcFmt(c, "this.%s", info->field) : info->handlerField ? fcFmt(c, "this.%s", info->handlerField) : fcFmt(c, "this.fxTemplateEls[(taking %sAt)]", local);
            fmLineF(g, "%s.%s = func (%s) on this => { %s(%s%s) }", local, ev->name, fcBufDone(c, &params), h->handler, element0, fcBufDone(c, &args));
        }

    for (i = 0; i < element->children->count; i++) fmEmitElement(g, (FmElement*)element->children->items[i], local, info->def, partBindings, templateTarget, dataItem);
    if (parent) {
        /* a part knows how its container takes it; any other element is taken the way its container says (`Add`, `SetChild`, `SetContent`) */
        const char* add = fmIsPart(info->def) && parentDef ? fmAddCallIn(info->def, parentDef->tag) : parentDef ? parentDef->addCall : "{p}.Add({e})";
        fmLine(g, fmReplace(c, fmReplace(c, add, "{p}", parent), "{e}", local));
    }
    if (info->field) fmLineF(g, "this.%s = %s", info->field, local);
}

/* EmitResources: SetterCode */
static const char* fmSetterCode(FmGen* g, const FmSetter* setter, const FmElementDef* def, const char* owner) {
    FcCtx* c = g->c;
    const FmPropDef* property = fmProperty(def, setter->property);
    const char* value;
    if (!property) return "";
    if (property->method) {
        fmError(g, setter->line, fcFmt(c, "'%s' is set by a method and cannot be part of a style.", setter->property));
        return "";
    }
    value = fmValueCode(g, property, setter->value, setter->line);
    if (!value) return "";
    return fcFmt(c, "%s.Set(%s, %s%s)", owner, fmUiQuote(c, fmFieldName(property)), value, setter->target ? fcFmt(c, ", %s", fmUiQuote(c, setter->target)) : "");
}

/* a part of a template by its name (the dictionary `parts`: the last element of a name wins) */
static FmElement* fmPartOf(FcList* partNames, FcList* partElements, const char* name) {
    int i;
    if (!partNames || !name) return NULL;
    for (i = 0; i < partNames->count; i++) if (strcmp((const char*)partNames->items[i], name) == 0) return (FmElement*)partElements->items[i];
    return NULL;
}

static void fmEmitTriggers(FmGen* g, FcList* triggers, const FmElementDef* def, const char* owner, const char* addCall, FcList* partNames, FcList* partElements, int* counter) {
    FcCtx* c = g->c;
    int i, k;
    for (i = 0; i < triggers->count; i++) {
        FmTrigger* trigger = (FmTrigger*)triggers->items[i];
        const char* t = fcFmt(c, "tr%d", (*counter)++);
        for (k = 0; k < trigger->conditions->count; k++) {
            FmCondition* cond = (FmCondition*)trigger->conditions->items[k];
            FmElement* part = cond->source ? fmPartOf(partNames, partElements, cond->source) : NULL;
            const FmElementDef* sourceDef = part ? fmFindDef(c, part->tag) : (cond->source && partNames && !part ? def : def);
            const FmPropDef* property;
            const char* code1;
            if (cond->source && partNames && part) sourceDef = fmFindDef(c, part->tag);
            else sourceDef = def;
            property = sourceDef ? fmProperty(sourceDef, cond->property) : NULL;
            if (!property) property = fmStateProperty(cond->property);
            if (!property) continue;
            code1 = fmValueCode(g, property, cond->value, cond->line);
            if (!code1) continue;
            if (k == 0) fmLineF(g, "var %s = new UI.Trigger(%s, %s%s)", t, fmUiQuote(c, fmFieldName(property)), code1, cond->source ? fcFmt(c, ", %s", fmUiQuote(c, cond->source)) : "");
            else fmLineF(g, "%s.And(%s, %s%s)", t, fmUiQuote(c, fmFieldName(property)), code1, cond->source ? fcFmt(c, ", %s", fmUiQuote(c, cond->source)) : "");
        }
        for (k = 0; k < trigger->setters->count; k++) {
            FmSetter* setter = (FmSetter*)trigger->setters->items[k];
            FmElement* part = setter->target ? fmPartOf(partNames, partElements, setter->target) : NULL;
            const FmElementDef* setterDef = setter->target && part ? fmFindDef(c, part->tag) : def;
            const char* line;
            if (!setterDef) continue;
            line = fmSetterCode(g, setter, setterDef, t);
            if (line[0]) fmLine(g, line);
        }
        fmLineF(g, "%s.%s(%s)", owner, addCall, t);
    }
}

static void fmEmitResources(FmGen* g, FmDocument* document, const char* resources) {
    FcCtx* c = g->c;
    int counter = 0, i, k;
    FcList* ordered;
    FcList* pending;
    if (document->styles->count == 0 && document->templates->count == 0 && document->dataTemplates->count == 0 && document->views->count == 0) return;

    /* styles: the ones that others are based on first */
    ordered = fcListNew(c);
    pending = fcListCopy(c, document->styles);
    while (pending->count > 0) {
        FmStyle* ready = NULL;
        for (i = 0; i < pending->count && !ready; i++) {
            FmStyle* st = (FmStyle*)pending->items[i];
            bool basedReady = !st->basedOn;
            for (k = 0; k < ordered->count && !basedReady; k++) { FmStyle* o = (FmStyle*)ordered->items[k]; if (o->key && strcmp(o->key, st->basedOn) == 0) basedReady = true; }
            if (basedReady) ready = st;
        }
        if (!ready) ready = (FmStyle*)pending->items[0];
        fcListAdd(c, ordered, ready);
        for (i = 0; i < pending->count; i++) if (pending->items[i] == ready) { fcListRemoveAt(pending, i); break; }
    }
    for (i = 0; i < ordered->count; i++) {
        FmStyle* style = (FmStyle*)ordered->items[i];
        const FmElementDef* def = fmFindTarget(c, style->target);
        const char* v = fcFmt(c, "st%d", counter++);
        fmLineF(g, "var %s = new UI.Style(%s)", v, fmUiQuote(c, fmStyleTarget(def)));
        if (style->basedOn) fmLineF(g, "%s.basedOn = this.fxStyle_%s", v, style->basedOn);
        for (k = 0; k < style->setters->count; k++) {
            const char* line = fmSetterCode(g, (FmSetter*)style->setters->items[k], def, v);
            if (line[0]) fmLine(g, line);
        }
        fmEmitTriggers(g, style->triggers, def, v, "AddTrigger", NULL, NULL, &counter);
        if (style->key) {
            fmLineF(g, "%s.Set(%s, %s)", resources, fmUiQuote(c, style->key), v);
            fmLineF(g, "this.fxStyle_%s = %s", style->key, v);
        } else fmLineF(g, "%s.AddStyle(%s)", resources, v);
    }

    /* collection views and data templates (the views first: a list refers to them) */
    for (i = 0; i < document->views->count; i++) {
        FmView* view = (FmView*)document->views->items[i];
        const char* v = fcFmt(c, "vw%d", counter++);
        FmElement* element = view->element;
        FmAttr* sort = fmFind(element, "sortBy");
        FmAttr* desc = fmFind(element, "descending");
        const char* sortBy = sort && sort->value->kind == FM_LITERAL ? fmTrim(c, sort->value->text) : NULL;
        bool descending = desc && desc->value->kind == FM_LITERAL && fmIEq(fmTrim(c, desc->value->text), "true");
        static const char* const names[] = { "Filter", "Comparer", "Source" };
        fmLineF(g, "var %s = %s", v, fmSchema(c)->collectionView->create);
        if (desc && desc->value->kind == FM_LITERAL && !fmIEq(fmTrim(c, desc->value->text), "true") && !fmIEq(fmTrim(c, desc->value->text), "false"))
            fmError(g, desc->line, fcFmt(c, "'Descending' needs true or false, not '%s'.", desc->value->text));
        if (sortBy) fmLineF(g, "%s.SortBy(%s, %s)", v, fmUiQuote(c, sortBy), descending ? "true" : "false");
        else if (descending) fmLineF(g, "%s.Descending = true", v);
        for (k = 0; k < 3; k++) {
            FmAttr* attribute = fmFind(element, names[k]);
            const FmPropDef* property;
            const char* value;
            if (!attribute || attribute->value->kind == FM_BINDING) continue;   /* a binding makes the source */
            property = fmProperty(fmSchema(c)->collectionView, names[k]);
            value = fmValueCode(g, property, attribute->value, attribute->line);
            if (value) fmLineF(g, "%s.%s = %s", v, fmFieldName(property), value);
        }
        fmLineF(g, "%s.Set(%s, %s)", resources, fmUiQuote(c, view->key), v);
        fmLineF(g, "this.fxView_%s = %s", view->key, v);
    }
    for (i = 0; i < document->dataTemplates->count; i++) {
        FmDataTemplate* template = (FmDataTemplate*)document->dataTemplates->items[i];
        const char* v;
        if (!template->root) continue;
        v = fcFmt(c, "dt%d", counter++);
        fmLineF(g, "var %s = new UI.DataTemplate(func (any item) on this => {", v);
        g->indent++;
        fmEmitElement(g, template->root, NULL, NULL, NULL, NULL, "item");
        fmLineF(g, "return %s", fmInfoOf(g, template->root)->local);
        g->indent--;
        fmLine(g, "})");
        fmLineF(g, "%s.Set(%s, %s)", resources, fmUiQuote(c, template->key), v);
        fmLineF(g, "this.fxData_%s = %s", template->key, v);
    }

    for (i = 0; i < document->templates->count; i++) {
        FmTemplate* template = (FmTemplate*)document->templates->items[i];
        const FmElementDef* def;
        const char* v;
        FcList* bindings;
        FcList* partNames;
        FcList* partElements;
        FcList* stack;
        if (!template->root) continue;
        def = fmFindTarget(c, template->target);
        v = fcFmt(c, "tp%d", counter++);
        bindings = fcListNew(c);
        fmLineF(g, "var %s = new UI.ControlTemplate(func (UI.Element owner) on this => {", v);
        g->indent++;
        fmEmitElement(g, template->root, NULL, NULL, bindings, def, NULL);
        fmLineF(g, "return %s", fmInfoOf(g, template->root)->local);
        g->indent--;
        fmLine(g, "})");
        for (k = 0; k < bindings->count; k++) {
            FmTemplatePartBinding* pb = (FmTemplatePartBinding*)bindings->items[k];
            fmLineF(g, "%s.Bind(%s, %s, %s)", v, fmUiQuote(c, pb->part), fmUiQuote(c, pb->partProperty), fmUiQuote(c, pb->ownerProperty));
        }
        /* the parts by name (parts[e.Name] = e: the last one of a name wins) */
        partNames = fcListNew(c);
        partElements = fcListNew(c);
        stack = fcListNew(c);
        fcListAdd(c, stack, template->root);
        while (stack->count > 0) {
            FmElement* e = (FmElement*)stack->items[--stack->count];
            if (e->name) {
                bool found = false;
                for (k = 0; k < partNames->count; k++) if (strcmp((const char*)partNames->items[k], e->name) == 0) { partElements->items[k] = e; found = true; }
                if (!found) { fcListAdd(c, partNames, (void*)e->name); fcListAdd(c, partElements, e); }
            }
            for (k = 0; k < e->children->count; k++) fcListAdd(c, stack, e->children->items[k]);
        }
        fmEmitTriggers(g, template->triggers, def, v, "AddTrigger", partNames, partElements, &counter);
        fmLineF(g, "%s.Set(%s, %s)", resources, fmUiQuote(c, template->key), v);
        fmLineF(g, "this.fxTemplate_%s = %s", template->key, v);
    }
}

/* EmitBindings */
static void fmEmitBindings(FmGen* g, FcList* bindings) {
    FcCtx* c = g->c;
    int i, s;
    fmLine(g, "");
    fmLine(g, "// ---- data binding: the properties follow the data context (probes, SPEC 8.14) ----");
    fmLine(g, "// sets the object the bindings read from (and write to) and connects them; null disconnects");
    fmLine(g, "undefined SetDataContext(any context) {");
    fmLine(g, "    this.fxUnbindAll()");
    fmLine(g, "    this.dataContext = context");
    fmLine(g, "    // a context handed over (`SetDataContext(new Model())`, `SetDataContext(take model)`) belongs to this object from now on; a variable of the");
    fmLine(g, "    // caller (`SetDataContext(model)`) stays the caller's - this object only refers to it");
    fmLine(g, "    try context??.TakeTo(this)");
    fmLine(g, "    this.fxBindAll()");
    fmLine(g, "}");
    fmLine(g, "");
    fmLine(g, "undefined fxBindAll() {");
    g->indent++;
    for (i = 0; i < bindings->count; i++) fmLineF(g, "this.fxBind%d()", ((FmBinding*)bindings->items[i])->index);
    g->indent--;
    fmLine(g, "}");
    fmLine(g, "");
    fmLine(g, "undefined fxUnbindAll() {");
    g->indent++;
    for (i = 0; i < bindings->count; i++) fmLineF(g, "this.fxUnbind%d()", ((FmBinding*)bindings->items[i])->index);
    g->indent--;
    fmLine(g, "}");

    for (i = 0; i < bindings->count; i++) {
        FmBinding* b = (FmBinding*)bindings->items[i];
        FcList* path = fcListNew(c);
        const char* target = fcFmt(c, "this.%s", b->target->field);
        const char* fieldName = fmFieldName(b->property);
        int x = b->index;
        int n;
        {
            /* Segments: Path.Split('.') */
            const char* p = b->value->path;
            for (;;) {
                const char* dot = strchr(p, '.');
                fcListAdd(c, path, (void*)(dot ? fcStrN(c, p, (size_t)(dot - p)) : p));
                if (!dot) break;
                p = dot + 1;
            }
        }
        n = path->count;
        fmLine(g, "");
        fmLineF(g, "// %s.%s <- %s%s (%s)", b->target->def->tag, b->property->name, b->value->elementName ? fcFmt(c, "%s.", b->value->elementName) : "", b->value->path, fmBindingModeNames[b->value->mode]);

        /* unbind: silence every probe of the binding */
        fmLineF(g, "undefined fxUnbind%d() {", x);
        fmLineF(g, "    for (var i = 0; i < this.fxHandles%d.Count; i = i + 1) {", x);
        fmLineF(g, "        any h = this.fxHandles%d[i]", x);
        fmLine(g, "        silence h");
        fmLine(g, "    }");
        fmLineF(g, "    this.fxHandles%d = new List()", x);
        fmLine(g, "}");
        fmLine(g, "");

        /* bind: a probe on every step of the path (the last one updates the target, an earlier one connects the path again), and for two-way the probe on the target */
        fmLineF(g, "undefined fxBind%d() {", x);
        g->indent++;
        fmLineF(g, "this.fxUnbind%d()", x);
        if (b->value->mode != FM_ONETIME) {
            fmLineF(g, "any o0 = %s", b->source);
            for (s = 0; s < n; s++) {
                const char* handler = s == n - 1 ? fcFmt(c, "fxUpdate%d()", x) : fcFmt(c, "fxBind%d()", x);
                fmLineF(g, "if (o%d != undefined) {", s);
                g->indent++;
                fmLineF(g, "var f%d = func (any o, any v) on this => { %s }", s, handler);
                fmLineF(g, "any p%d = probe o%d.%s changed f%d", s, s, (const char*)path->items[s], s);
                fmLineF(g, "this.fxHandles%d.Add(p%d)", x, s);
                if (s < n - 1) fmLineF(g, "any o%d = o%d.%s", s + 1, s, (const char*)path->items[s]);
            }
            for (s = n - 1; s >= 0; s--) {
                g->indent--;
                fmLine(g, "}");
            }
            if (b->value->mode == FM_TWOWAY) {
                fmLineF(g, "var g = func (any o, any v) on this => { fxPush%d(v) }", x);
                fmLineF(g, "any pg = probe %s.%s changed g", target, fieldName);
                fmLineF(g, "this.fxHandles%d.Add(pg)", x);
            }
        }
        fmLineF(g, "this.fxUpdate%d()", x);
        g->indent--;
        fmLine(g, "}");
        fmLine(g, "");

        /* update: read the source, convert, write the target. An undefined on the way (no data context, nothing selected, a step that is undefined)
         * goes on as undefined: the converter gets it, and the property gets it - a text property as "", any other its default (the value before the binding) */
        fmLineF(g, "undefined fxUpdate%d() {", x);
        g->indent++;
        fmLineF(g, "if (this.fxBusy%d) { return }", x);
        if (b->property->kind != PK_Text) {
            fmLineF(g, "if (!this.fxDefaulted%d) {", x);
            fmLineF(g, "    this.fxDefault%d = %s.%s", x, target, fieldName);
            fmLineF(g, "    this.fxDefaulted%d = true", x);
            fmLine(g, "}");
        }
        fmLineF(g, "any v = %s", b->source);
        for (s = 0; s < n; s++) fmLineF(g, "v = v?.%s", (const char*)path->items[s]);
        if (b->converterField) fmLineF(g, "v = this.%s.Convert(v)", b->converterField);
        if (b->property->kind == PK_Text) fmLine(g, "if (v == undefined) { v = \"\" }");
        else fmLineF(g, "if (v == undefined) { v = this.fxDefault%d }", x);
        fmLineF(g, "this.fxBusy%d = true", x);
        fmLineF(g, "%s.%s = v", target, fieldName);
        fmLineF(g, "this.fxBusy%d = false", x);
        g->indent--;
        fmLine(g, "}");

        /* push: the other direction */
        if (b->value->mode == FM_TWOWAY) {
            fmLine(g, "");
            fmLineF(g, "undefined fxPush%d(any value) {", x);
            g->indent++;
            fmLineF(g, "if (this.fxBusy%d) { return }", x);
            fmLineF(g, "any o = %s", b->source);
            fmLine(g, "if (o == undefined) { return }");
            for (s = 0; s < n - 1; s++) {
                fmLineF(g, "o = o.%s", (const char*)path->items[s]);
                fmLine(g, "if (o == undefined) { return }");
            }
            if (b->converterField) fmLineF(g, "value = this.%s.ConvertBack(value)", b->converterField);
            fmLineF(g, "this.fxBusy%d = true", x);
            fmLineF(g, "o.%s = value", (const char*)path->items[n - 1]);
            fmLineF(g, "this.fxBusy%d = false", x);
            g->indent--;
            fmLine(g, "}");
        }
    }
}

/* Parameters(EventDef): `, int x, int y` */
static const char* fmParameters(FcCtx* c, const FmEventDef* e) {
    FcBuf b = {0};
    int k;
    fcBufStr(&b, "");
    for (k = 0; k < fmArgCount(e); k++) fcBufFmt(&b, ", %s %s", fmArgType(e, k), e->args[k]);
    return fcBufDone(c, &b);
}

/* the converters in use (key, type) */
typedef struct FmConvUse { FcList* keys; FcList* types; } FmConvUse;
static const char* fmConverterField(FcCtx* c, FmConvUse* conv, const char* key) {
    int i;
    bool known = false;
    const char* builtIn;
    for (i = 0; i < conv->keys->count; i++) if (strcmp((const char*)conv->keys->items[i], key) == 0) known = true;
    if (!known && (builtIn = fmBuiltInConverter(key)) != NULL) { fcListAdd(c, conv->keys, (void*)key); fcListAdd(c, conv->types, (void*)builtIn); }
    return fcFmt(c, "fxConv_%s", key);
}

/* Generate(document, sourceName, preview) */
static const char* fmUiGenerate(FcCtx* c, FmDocument* document, const char* sourceName, bool preview) {
    FmGen gen, *g = &gen;
    int counter = 0, i, k;
    FcList* all = fmAllElements(c, document);
    FcList* templateEls = fmTemplateElements(c, document);
    FcList* dataEls = fmDataTemplateElements(c, document);
    FmConvUse conv;
    FcList* bindings = fcListNew(c);
    FcList* bindSources;
    bool inNamespace;
    const char* file;
    const char* container;
    FcList* handlerNames;
    FcList* handlerDefs;
    bool anyInTemplate = false;
    memset(g, 0, sizeof *g);
    g->c = c;
    g->preview = preview;
    g->errors = fcListCopy(c, document->diagnostics);
    g->infoKeys = fcListNew(c);
    g->infoValues = fcListNew(c);
    fcBufStr(&g->text, "");
    for (i = 0; i < all->count; i++) {
        FmElement* element = (FmElement*)all->items[i];
        const FmElementDef* def = fmFindDef(c, element->tag);
        fmInfoSet(g, element, fmNewInfo(g, element, def, fcFmt(c, "e%d", counter++), def->cls[0] == 0 ? NULL : element->name));
    }
    /* the elements inside control templates have no fields (their names are the names of the parts) */
    for (i = 0; i < templateEls->count; i++) {
        FmElement* element = (FmElement*)templateEls->items[i];
        fmInfoSet(g, element, fmNewInfo(g, element, fmFindDef(c, element->tag), fcFmt(c, "e%d", counter++), NULL));
    }
    /* the elements inside data templates have none either (there is one set of elements per item) */
    for (i = 0; i < dataEls->count; i++) {
        FmElement* element = (FmElement*)dataEls->items[i];
        fmInfoSet(g, element, fmNewInfo(g, element, fmFindDef(c, element->tag), fcFmt(c, "e%d", counter++), NULL));
    }
    /* a collection view is held by a field `fxView_Key` (a binding of its `source` needs it) */
    for (i = 0; i < document->views->count; i++) {
        FmView* view = (FmView*)document->views->items[i];
        fmInfoSet(g, view->element, fmNewInfo(g, view->element, fmSchema(c)->collectionView, fcFmt(c, "vw%d", counter++), fcFmt(c, "fxView_%s", view->key)));
    }

    /* converters: the declared ones and the built-in ones that are used */
    conv.keys = fcListNew(c);
    conv.types = fcListNew(c);
    for (i = 0; i < document->converters->count; i++) {
        FmConverter* declared = (FmConverter*)document->converters->items[i];
        fcListAdd(c, conv.keys, (void*)declared->key);
        fcListAdd(c, conv.types, (void*)declared->type);
    }
    /* the converters that data templates use */
    for (i = 0; i < dataEls->count; i++) {
        FmElement* element = (FmElement*)dataEls->items[i];
        for (k = 0; k < element->attributes->count; k++) {
            FmAttr* attribute = (FmAttr*)element->attributes->items[k];
            if (attribute->value->kind == FM_BINDING && attribute->value->converter) fmConverterField(c, &conv, attribute->value->converter);
        }
    }
    bindSources = fcListNew(c);
    if (!preview) {
        for (i = 0; i < all->count; i++) fcListAdd(c, bindSources, all->items[i]);
        for (i = 0; i < document->views->count; i++) fcListAdd(c, bindSources, ((FmView*)document->views->items[i])->element);
    }
    for (i = 0; i < bindSources->count; i++) {
        FmElement* element = (FmElement*)bindSources->items[i];
        FmInfo* info = fmInfoOf(g, element);
        for (k = 0; k < element->attributes->count; k++) {
            FmAttr* attribute = (FmAttr*)element->attributes->items[k];
            const FmValue* value = attribute->value;
            const FmPropDef* property;
            const char* converterField;
            const char* source;
            FmBinding* b;
            if (value->kind != FM_BINDING) continue;
            property = fmProperty(info->def, attribute->name);
            if (property->method || info->def->cls[0] == 0) {
                fmError(g, attribute->line, fcFmt(c, "'%s' of '%s' cannot be bound.", attribute->name, info->def->tag));
                continue;
            }
            if (!info->field) info->field = fcFmt(c, "fxEl%s", info->local + 1);
            converterField = value->converter ? fmConverterField(c, &conv, value->converter) : NULL;
            source = "this.dataContext";
            if (value->elementName) {
                int m;
                for (m = 0; m < all->count; m++) {
                    FmElement* named = (FmElement*)all->items[m];
                    if (named->name && strcmp(named->name, value->elementName) == 0) { source = fcFmt(c, "this.%s", value->elementName); break; }
                }
            }
            b = (FmBinding*)fcAlloc(c, sizeof *b);
            b->index = bindings->count;
            b->target = info;
            b->property = property;
            b->value = value;
            b->source = source;
            b->converterField = converterField;
            fcListAdd(c, bindings, b);
        }
    }

    if (g->errors->count > 0) { fcBufFree(&g->text); fmThrow(c, g->errors); }

    file = !sourceName || !sourceName[0] ? "the markup" : fcBaseName(sourceName);
    fmLineF(g, "// Generated from %s - do not edit, change the markup. Derive from the class and override the methods named in on...=\"...\":", file);
    fmLineF(g, "//     class %s : %s { override undefined Save(any sender) { ... } }", document->className, document->baseName);
    fmLine(g, "#import \"ui\"");
    /* the namespaces of the markup: an unqualified type name is looked up in them (a name that two of them have must be written with its prefix) */
    for (i = 0; i < document->nsValues->count; i++) {
        bool dup = false;
        for (k = 0; k < i; k++) if (strcmp((const char*)document->nsValues->items[k], (const char*)document->nsValues->items[i]) == 0) dup = true;
        if (!dup) fmLineF(g, "#using %s", (const char*)document->nsValues->items[i]);
    }
    inNamespace = document->ns && !preview;
    if (inNamespace) fmLineF(g, "#using %s", document->ns);
    fmLine(g, "");
    if (inNamespace) {
        fmLineF(g, "namespace %s {", document->ns);
        g->indent++;
    }
    fmLineF(g, "class %s {", preview ? "FxPreview" : document->baseName);
    g->indent++;

    /* ---- fields ---- */
    if (document->kind == FM_WINDOW || preview) {
        fmLine(g, "Framebuffer framebuffer");
        fmLine(g, "Window window");
        fmLine(g, "UI.Root UIRoot");
    }
    if (document->kind == FM_VIEW) fmLine(g, "UI.Panel view");
    if (preview) fmLine(g, "List fxAll");
    for (i = 0; i < g->infoValues->count; i++) {
        FmInfo* info = (FmInfo*)g->infoValues->items[i];
        if (info->field) fmLineF(g, "%s %s", info->def->cls, info->field);
    }
    /* (the elements the handlers reach through `this`: a field each, the parts of templates in one list) */
    {
        FcList* inTemplates = fcListNew(c);
        for (i = 0; i < document->dataTemplates->count; i++) { FmDataTemplate* t = (FmDataTemplate*)document->dataTemplates->items[i]; if (t->root) fmWalkInto(c, inTemplates, t->root); }
        for (i = 0; i < document->templates->count; i++) { FmTemplate* t = (FmTemplate*)document->templates->items[i]; if (t->root) fmWalkInto(c, inTemplates, t->root); }
        for (i = 0; i < g->infoValues->count; i++) {
            FmInfo* info = (FmInfo*)g->infoValues->items[i];
            bool inside = false;
            if (info->field || info->def->cls[0] == 0 || info->element->handlers->count == 0) continue;
            for (k = 0; k < inTemplates->count; k++) if (inTemplates->items[k] == info->element) inside = true;
            if (inside) { info->inTemplate = true; continue; }
            info->handlerField = fcFmt(c, "fxEl_%s", info->local);
            fmLineF(g, "%s %s", info->def->cls, info->handlerField);
        }
        for (i = 0; i < g->infoValues->count; i++) if (((FmInfo*)g->infoValues->items[i])->inTemplate) anyInTemplate = true;
        if (!preview && anyInTemplate) fmLine(g, "List fxTemplateEls");
    }
    for (i = 0; i < document->styles->count; i++) { FmStyle* st = (FmStyle*)document->styles->items[i]; if (st->key) fmLineF(g, "UI.Style fxStyle_%s", st->key); }
    for (i = 0; i < document->templates->count; i++) fmLineF(g, "UI.ControlTemplate fxTemplate_%s", ((FmTemplate*)document->templates->items[i])->key);
    for (i = 0; i < document->dataTemplates->count; i++) fmLineF(g, "UI.DataTemplate fxData_%s", ((FmDataTemplate*)document->dataTemplates->items[i])->key);
    /* the design view does not run the program, so the classes of its converters do not exist there (and nothing uses them) */
    if (!preview) for (i = 0; i < conv.keys->count; i++) fmLineF(g, "%s fxConv_%s", (const char*)conv.types->items[i], (const char*)conv.keys->items[i]);
    if (bindings->count > 0) {
        fmLine(g, "// the data context of the bindings: set it with SetDataContext(object)");
        fmLine(g, "class dataContext");
        for (i = 0; i < bindings->count; i++) {
            FmBinding* b = (FmBinding*)bindings->items[i];
            fmLineF(g, "List fxHandles%d", b->index);
            fmLineF(g, "bool fxBusy%d = false", b->index);
            /* the value the property had before the binding first wrote it (the default of the control): it comes back when the path is broken */
            if (b->property->kind != PK_Text) {
                fmLineF(g, "any fxDefault%d", b->index);
                fmLineF(g, "bool fxDefaulted%d = false", b->index);
            }
        }
    }
    fmLine(g, "");

    /* ---- constructor ---- */
    fmLine(g, "construct() {");
    g->indent++;
    if (document->kind == FM_WINDOW || preview) {
        fmLineF(g, "this.framebuffer = new Framebuffer(%d, %d)", document->width, document->height);
        fmLineF(g, "this.window = new Window(this.framebuffer, %s)", fmUiQuote(c, document->title));
        fmLine(g, "this.UIRoot = new UI.Root(this.framebuffer, this.window)");
        /* UIRoot.WindowBackdrop="Acrylic", ...: the properties of the root (the design view draws no material of the desktop behind it) */
        for (i = 0; i < document->rootAttributes->count; i++) {
            FmAttr* attribute = (FmAttr*)document->rootAttributes->items[i];
            const FmPropDef* property = fmRootProperty(attribute->name);
            const char* value;
            if (preview && strcmp(fmFieldName(property), "WindowBackdrop") == 0) continue;
            value = fmValueCode(g, property, attribute->value, attribute->line);
            if (value) fmLineF(g, "this.UIRoot.%s = %s", fmFieldName(property), value);
        }
    }
    if (document->kind == FM_VIEW) {
        fmLineF(g, "this.view = new UI.Panel(0, 0, %d, %d)", document->width, document->height);
        if (preview) fmLine(g, "this.UIRoot.Add(this.view)");
    }
    if (preview) fmLine(g, "this.fxAll = new List()");
    if (!preview && anyInTemplate) fmLine(g, "this.fxTemplateEls = new List()");
    if (!preview) for (i = 0; i < conv.keys->count; i++) fmLineF(g, "this.fxConv_%s = new %s()", (const char*)conv.keys->items[i], (const char*)conv.types->items[i]);
    for (i = 0; i < bindings->count; i++) fmLineF(g, "this.fxHandles%d = new List()", ((FmBinding*)bindings->items[i])->index);

    container = document->kind == FM_WINDOW ? "this.UIRoot" : "this.view";
    fmEmitResources(g, document, document->kind == FM_WINDOW ? "this.UIRoot.resources" : "this.view.Resources()");
    for (i = 0; i < document->children->count; i++) fmEmitElement(g, (FmElement*)document->children->items[i], container, NULL, NULL, NULL, NULL);
    if (bindings->count > 0) fmLine(g, "this.fxBindAll()");
    if (document->kind == FM_WINDOW && !preview) fmLine(g, "this.UIRoot.onTick = func () on this => { this.OnTick() }");
    g->indent--;
    fmLine(g, "}");
    fmLine(g, "");

    /* ---- handlers (empty here: the derived class overrides them) ---- */
    handlerNames = fcListNew(c);
    handlerDefs = fcListNew(c);
    if (!preview)
        for (i = 0; i < all->count; i++) {
            FmElement* element = (FmElement*)all->items[i];
            for (k = 0; k < element->handlers->count; k++) {
                FmHandler* h = (FmHandler*)element->handlers->items[k];
                const FmEventDef* eventDef = fmEvent(fmInfoOf(g, element)->def, h->event);
                int at = -1, m;
                for (m = 0; m < handlerNames->count; m++) if (strcmp((const char*)handlerNames->items[m], h->handler) == 0) at = m;
                if (at >= 0 && strcmp(fmParameters(c, (const FmEventDef*)handlerDefs->items[at]), fmParameters(c, eventDef)) != 0) {
                    const FmEventDef* existing = (const FmEventDef*)handlerDefs->items[at];
                    FcBuf a = {0}, b2 = {0};
                    fcBufStr(&a, "");
                    fcBufStr(&b2, "");
                    for (m = 0; m < fmArgCount(existing); m++) fcBufFmt(&a, ", %s", existing->args[m]);
                    for (m = 0; m < fmArgCount(eventDef); m++) fcBufFmt(&b2, ", %s", eventDef->args[m]);
                    fmError(g, h->line, fcFmt(c, "The method '%s' handles events with different parameters (sender%s and sender%s).", h->handler, fcBufDone(c, &a), fcBufDone(c, &b2)));
                } else if (at >= 0) handlerDefs->items[at] = (void*)eventDef;
                else { fcListAdd(c, handlerNames, (void*)h->handler); fcListAdd(c, handlerDefs, (void*)eventDef); }
            }
        }
    for (i = 0; i < handlerNames->count; i++) {
        fmLine(g, "// the handler of an event in the markup - override it in the derived class");
        fmLineF(g, "undefined %s(any sender%s) { }", (const char*)handlerNames->items[i], fmParameters(c, (const FmEventDef*)handlerDefs->items[i]));
    }
    if (handlerNames->count > 0) fmLine(g, "");

    /* ---- run / attach ---- */
    if (preview) {
        fmLine(g, "// where every element ended up, for the design view");
        fmLine(g, "undefined FxDump() {");
        fmLine(g, "    print(\"@fb \" + this.framebuffer.id)");
        fmLine(g, "    for (var i = 0; i < this.fxAll.Count; i = i + 1) {");
        fmLine(g, "        var e = this.fxAll[i]");
        fmLine(g, "        print(\"@\" + i + \" \" + e.ax + \" \" + e.ay + \" \" + e.actualWidth + \" \" + e.actualHeight)");
        fmLine(g, "    }");
        fmLine(g, "}");
    } else if (document->kind == FM_WINDOW) {
        fmLine(g, "// called once per cycle of this window (in Run(), also when it is attached to another window) - override it for a loop of your own");
        fmLine(g, "undefined OnTick() { }");
        fmLine(g, "");
        fmLine(g, "// the start of the program: shows the window and runs its window manager on this thread until the windows are closed (if nobody runs it yet, else it is only shown); with `other` (another window of the markup, or a UI.Root) that window is opened as well");
        fmLine(g, "undefined Run(any other = undefined) {");
        fmLine(g, "    if (other != undefined) { this.Open(other) }");
        fmLine(g, "    this.UIRoot.Run()");
        fmLine(g, "}");
        fmLine(g, "");
        fmLine(g, "// shows the window on its window manager (started with a thread of its own if nobody runs it yet), the events are handled at the program's safe points (and at its end); `modal`: returns when the window is closed, the other windows take no input meanwhile");
        fmLine(g, "undefined Show(bool modal = false) { this.UIRoot.Show(modal) }");
        fmLine(g, "");
        fmLine(g, "// opens another window (another object of a window of the markup, or a UI.Root): this window's Run/Tick works it off from now on until its window is closed");
        fmLine(g, "undefined Open(any other) {");
        fmLine(g, "    any root = other");
        fmLine(g, "    if (!(other is of UI.Root)) {");
        fmLine(g, "        root = other.UIRoot");
        fmLine(g, "        other.TakeTo(this)");
        fmLine(g, "    }");
        fmLine(g, "    this.UIRoot.Attach(root)");
        fmLine(g, "}");
    } else {
        fmLine(g, "// adds the view to a container (UI.Root.content, a Panel or a Stack)");
        fmLine(g, "undefined Attach(any container) { container.Add(this.view) }");
    }

    if (bindings->count > 0) fmEmitBindings(g, bindings);

    g->indent--;
    fmLine(g, "}");
    if (inNamespace) {
        g->indent--;
        fmLine(g, "}");
    }

    if (g->errors->count > 0) { fcBufFree(&g->text); fmThrow(c, g->errors); }
    return fcBufDone(c, &g->text);
}

/* FireUiGenerator.Generate */
static const char* fmUiGenerateScript(FcCtx* c, FmDocument* document, const char* sourceName) { return fmUiGenerate(c, document, sourceName, false); }

/* GeneratePreview: a script that only draws the interface, for the design view of the editor */
static FC_LATER const char* fmUiGeneratePreview(FcCtx* c, FmDocument* document, const char* sourceName) {
    return fcFmt(c, "%s\nvar fxP = new FxPreview()\nfxP.UIRoot.Draw()\nfxP.UIRoot.Draw()\nfxP.FxDump()\n", fmUiGenerate(c, document, sourceName, true));
}
