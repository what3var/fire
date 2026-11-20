/* native/forge/serve_markup.c - the completion of markup files (.fxml) of `forge serve`: src/fire.UI.Markup/MarkupCompletion.cs (commit 1c549476:
 * Suggest, Rank, ElementsIn, AttributesFor, Describe, Values, TargetOf, ValuesOf, Prefixes, ExtensionValues, DocumentInfo), method for method, on the
 * markup schema in C (markupschema.c, Phase C step 5: FmSchema, fmFindDef, fmFindTarget, the root and state properties). A small scanner reads the
 * text up to the caret (it is being typed: not well formed); what the document declares (keys, names, handlers, prefixes) is read with patterns.
 * Included by serve.c when markupschema.c is part of the program (FM_P defined). */

typedef enum SmKind { MK_ELEMENT, MK_ATTRIBUTE, MK_VALUE, MK_EXTENSION, MK_NAMESPACE } SmKind;
static const char* const smKindNames[] = { "element", "attribute", "value", "extension", "namespace" };
typedef struct SmItem { const char* text; SmKind kind; const char* detail; int caretBack; const char* display; } SmItem;

typedef struct SmOpen { const char* tag; FcList* attrNames; FcList* attrValues; } SmOpen;   /* attributes: names compared ignoring case, the last value wins */

typedef struct SmInfo {
    FcList* styles; FcList* templates; FcList* dataTemplates; FcList* views; FcList* converters; FcList* names; FcList* handlers;
    FcList* prefixKeys; FcList* prefixValues;
    const char* ownNamespace;
} SmInfo;

typedef struct SmCtx { FcCtx* t; FcCtx* sc; const uint16_t* s; int n; } SmCtx;   /* t: the request's scratch, sc: the context of the schema */

static const char* smLabel(const SmItem* i) { return i->display ? i->display : i->text; }
static SmItem* smItem(FcCtx* t, const char* text, SmKind kind, const char* detail, int caretBack, const char* display) {
    SmItem* i = (SmItem*)fcAlloc(t, sizeof *i);
    i->text = text; i->kind = kind; i->detail = detail; i->caretBack = caretBack; i->display = display;
    return i;
}
static const char* smSub(SmCtx* m, int from, int to) { return fc8FromU16(m->t, m->s + from, to - from); }
static bool smNameChar(uint16_t c) { return fcIsLetterOrDigit(c) || c == '_' || c == '.' || c == ':' || c == '-'; }
static bool smStarts(SmCtx* m, int at, const char* what) { int i; for (i = 0; what[i]; i++) if (at + i >= m->n || m->s[at + i] != (uint16_t)what[i]) return false; return true; }

static SmOpen* smOpenNew(FcCtx* t) { SmOpen* o = (SmOpen*)fcAlloc(t, sizeof *o); o->tag = ""; o->attrNames = fcListNew(t); o->attrValues = fcListNew(t); return o; }
static const char* smAttr(const SmOpen* o, const char* name) {
    int i;
    for (i = 0; i < o->attrNames->count; i++) if (fmIEq((const char*)o->attrNames->items[i], name)) return (const char*)o->attrValues->items[i];
    return NULL;
}
static void smSetAttr(FcCtx* t, SmOpen* o, const char* name, const char* value) {
    int i;
    for (i = 0; i < o->attrNames->count; i++) if (fmIEq((const char*)o->attrNames->items[i], name)) { o->attrValues->items[i] = (void*)value; return; }
    fcListAdd(t, o->attrNames, (void*)name);
    fcListAdd(t, o->attrValues, (void*)value);
}

/* Rank: what starts with the typed text first, then what contains it */
static FcList* smRank(FcCtx* t, FcList* items, const char* typed) {
    FcList* r;
    int i;
    if (!typed[0]) return items;
    r = fcListNew(t);
    for (i = 0; i < items->count; i++) if (svStartsWithIC(smLabel((SmItem*)items->items[i]), typed)) fcListAdd(t, r, items->items[i]);
    for (i = 0; i < items->count; i++) { const char* l = smLabel((SmItem*)items->items[i]); if (!svStartsWithIC(l, typed) && svContainsIC(l, typed)) fcListAdd(t, r, items->items[i]); }
    return r;
}

/* ---- DocumentInfo (the patterns) ---- */
static bool smWordAt(SmCtx* m, int at, int* end) {   /* [A-Za-z_]\w* */
    int i = at;
    if (i >= m->n || !((m->s[i] >= 'A' && m->s[i] <= 'Z') || (m->s[i] >= 'a' && m->s[i] <= 'z') || m->s[i] == '_')) return false;
    while (i < m->n && (fcIsLetterOrDigit(m->s[i]) || m->s[i] == '_')) i++;
    *end = i;
    return true;
}
static bool smBoundaryBefore(SmCtx* m, int at) { return at == 0 || !(fcIsLetterOrDigit(m->s[at - 1]) || m->s[at - 1] == '_'); }
static bool smIEqAt(SmCtx* m, int at, const char* w) { int i; for (i = 0; w[i]; i++) if (at + i >= m->n || svFold(m->s[at + i]) != svFold((unsigned char)w[i])) return false; return true; }
/* `\s*=\s*["']` at `at`: the position after the quote, -1 */
static int smEqualsQuote(SmCtx* m, int at) {
    while (at < m->n && fcIsWhiteSpace(m->s[at])) at++;
    if (at >= m->n || m->s[at] != '=') return -1;
    at++;
    while (at < m->n && fcIsWhiteSpace(m->s[at])) at++;
    if (at >= m->n || (m->s[at] != '"' && m->s[at] != '\'')) return -1;
    return at + 1;
}
static void smAddOnce(FcCtx* t, FcList* l, const char* v) { if (!fcListHasStr(l, v)) fcListAdd(t, l, (void*)v); }

static SmInfo* smReadInfo(SmCtx* m) {
    FcCtx* t = m->t;
    SmInfo* info = (SmInfo*)fcAlloc(t, sizeof *info);
    static const char* const keyed[] = { "Style", "ControlTemplate", "DataTemplate", "CollectionView", "Converter" };
    int i, k;
    info->styles = fcListNew(t); info->templates = fcListNew(t); info->dataTemplates = fcListNew(t); info->views = fcListNew(t);
    info->converters = fcListNew(t); info->names = fcListNew(t); info->handlers = fcListNew(t); info->prefixKeys = fcListNew(t); info->prefixValues = fcListNew(t);
    /* Keyed: <(Style|...)\b[^>]*?\bkey\s*=\s*["']([A-Za-z_]\w*)["'] (ignore case) */
    for (i = 0; i < m->n; i++) {
        int which = -1, p, q, e;
        if (m->s[i] != '<') continue;
        for (k = 0; k < 5 && which < 0; k++) {
            int len = (int)strlen(keyed[k]);
            if (smIEqAt(m, i + 1, keyed[k]) && !(i + 1 + len < m->n && (fcIsLetterOrDigit(m->s[i + 1 + len]) || m->s[i + 1 + len] == '_'))) which = k;
        }
        if (which < 0) continue;
        for (p = i + 1; p < m->n && m->s[p] != '>'; p++) {
            if (!smBoundaryBefore(m, p) || !smIEqAt(m, p, "key") || (p + 3 < m->n && (fcIsLetterOrDigit(m->s[p + 3]) || m->s[p + 3] == '_'))) continue;
            q = smEqualsQuote(m, p + 3);
            if (q < 0 || !smWordAt(m, q, &e) || e >= m->n || (m->s[e] != '"' && m->s[e] != '\'')) continue;
            {
                /* (the C# switches on the group as written: another case than the schema's is a converter) */
                const char* written = smSub(m, i + 1, i + 1 + (int)strlen(keyed[which]));
                FcList* l;
                if (strcmp(written, keyed[which]) != 0) which = 4;
                l = which == 0 ? info->styles : which == 1 ? info->templates : which == 2 ? info->dataTemplates : which == 3 ? info->views : info->converters;
                smAddOnce(t, l, smSub(m, q, e));
            }
            break;
        }
    }
    for (i = 0; i < m->n; i++) {
        int q, e;
        /* Name: \bname\s*=\s*["']([A-Za-z_]\w*)["'] (ignore case) */
        if (smBoundaryBefore(m, i) && smIEqAt(m, i, "name") && !(i + 4 < m->n && (fcIsLetterOrDigit(m->s[i + 4]) || m->s[i + 4] == '_'))
            && (q = smEqualsQuote(m, i + 4)) >= 0 && smWordAt(m, q, &e) && e < m->n && (m->s[e] == '"' || m->s[e] == '\''))
            smAddOnce(t, info->names, smSub(m, q, e));
        /* Handler: \b[oO]n[A-Z]\w*\s*=\s*["']([A-Za-z_]\w*)["'] */
        if (smBoundaryBefore(m, i) && i + 2 < m->n && (m->s[i] == 'o' || m->s[i] == 'O') && m->s[i + 1] == 'n' && m->s[i + 2] >= 'A' && m->s[i + 2] <= 'Z') {
            int w = i + 3;
            while (w < m->n && (fcIsLetterOrDigit(m->s[w]) || m->s[w] == '_')) w++;
            if ((q = smEqualsQuote(m, w)) >= 0 && smWordAt(m, q, &e) && e < m->n && (m->s[e] == '"' || m->s[e] == '\'')) smAddOnce(t, info->handlers, smSub(m, q, e));
        }
        /* Prefix: \bnamespace:([A-Za-z_]\w*)\s*=\s*["']([^"']*)["'];  Own: \bnamespace\s*=\s*["']([^"']*)["'] (the first) */
        if (smBoundaryBefore(m, i) && smStarts(m, i, "namespace")) {
            int a = i + 9;
            if (a < m->n && m->s[a] == ':' && smWordAt(m, a + 1, &e) && (q = smEqualsQuote(m, e)) >= 0) {
                int v = q;
                while (v < m->n && m->s[v] != '"' && m->s[v] != '\'') v++;
                if (v < m->n) {
                    const char* key = smSub(m, a + 1, e);
                    const char* value = smSub(m, q, v);
                    int k2;
                    for (k2 = 0; k2 < info->prefixKeys->count; k2++) if (strcmp((const char*)info->prefixKeys->items[k2], key) == 0) { info->prefixValues->items[k2] = (void*)value; break; }
                    if (k2 == info->prefixKeys->count) { fcListAdd(t, info->prefixKeys, (void*)key); fcListAdd(t, info->prefixValues, (void*)value); }
                }
            } else if (!info->ownNamespace && (q = smEqualsQuote(m, a)) >= 0) {
                int v = q;
                while (v < m->n && m->s[v] != '"' && m->s[v] != '\'') v++;
                if (v < m->n) info->ownNamespace = smSub(m, q, v);
            }
        }
    }
    return info;
}

/* ---- elements ---- */
static void smInterfaceElements(SmCtx* m, FcList* out) {
    FmSchema* s = fmSchema(m->sc);
    int i;
    for (i = 0; i < s->elements->count; i++) {
        FmElementDef* d = (FmElementDef*)s->elements->items[i];
        if (fmIsPart(d)) continue;
        fcListAdd(m->t, out, smItem(m->t, d->tag, MK_ELEMENT, d->cls && d->cls[0] ? d->cls : "part", 0, NULL));
    }
}

static FcList* smElementsIn(SmCtx* m, SmOpen* parent) {
    FcCtx* t = m->t;
    FcList* r = fcListNew(t);
    static const char* const resourceKinds[] = { "Converter", "Style", "ControlTemplate", "DataTemplate", "CollectionView" };
    const char* tag;
    const FmElementDef* def;
    int i;
    if (!parent) {
        fcListAdd(t, r, smItem(t, "Window", MK_ELEMENT, "a window of its own", 0, NULL));
        fcListAdd(t, r, smItem(t, "View", MK_ELEMENT, "a panel that a window attaches", 0, NULL));
        return r;
    }
    tag = parent->tag;
    if (fmIEq(tag, "Window") || fmIEq(tag, "View")) { fcListAdd(t, r, smItem(t, "Resources", MK_ELEMENT, "styles, templates, converters, views", 0, NULL)); smInterfaceElements(m, r); }
    else if (fmIEq(tag, "Resources")) { for (i = 0; i < 5; i++) fcListAdd(t, r, smItem(t, resourceKinds[i], MK_ELEMENT, "resource", 0, NULL)); }
    else if (fmIEq(tag, "Style")) { fcListAdd(t, r, smItem(t, "Setter", MK_ELEMENT, "a value", 0, NULL)); fcListAdd(t, r, smItem(t, "Trigger", MK_ELEMENT, "values while a condition holds", 0, NULL)); }
    else if (fmIEq(tag, "Trigger")) { fcListAdd(t, r, smItem(t, "Condition", MK_ELEMENT, "another condition", 0, NULL)); fcListAdd(t, r, smItem(t, "Setter", MK_ELEMENT, "a value", 0, NULL)); }
    else if (fmIEq(tag, "ControlTemplate")) { fcListAdd(t, r, smItem(t, "Trigger", MK_ELEMENT, "values while a condition holds", 0, NULL)); smInterfaceElements(m, r); }
    else if (fmIEq(tag, "DataTemplate")) smInterfaceElements(m, r);
    else if ((def = fmFindDef(m->sc, tag)) != NULL) {
        if (def->children == CM_Parts) { for (i = 0; def->partTags && def->partTags[i]; i++) fcListAdd(t, r, smItem(t, def->partTags[i], MK_ELEMENT, fcFmt(t, "part of %s", def->tag), 0, NULL)); }
        else if (def->children == CM_Elements || def->children == CM_Single) smInterfaceElements(m, r);
    }
    return r;
}

/* ---- attributes ---- */
static const char* smDescribe(FcCtx* t, const FmPropDef* p) {
    static const char* const kinds[] = { "int", "number", "bool", "text", "color", "orientation", "pen", "thickness", "enum", "image", "geometry", "styleref", "templateref", "datatemplateref", "viewref", "collection", "code" };
    switch (p->kind) {
        case PK_Enum: {
            FcBuf b = {0};
            int i;
            fcBufStr(&b, "");
            for (i = 0; p->choices && p->choices[i]; i++) fcBufFmt(&b, "%s%s", i ? " | " : "", p->choices[i]);
            return fcBufDone(t, &b);
        }
        case PK_Number: return "number: 0.5, 100";
        case PK_Color: return "colour: #RRGGBB";
        case PK_Pen: return "pen: #RRGGBB or #RRGGBB,width";
        case PK_Thickness: return "4 | 4,2 | 1,2,3,4";
        case PK_Orientation: return "Horizontal | Vertical";
        case PK_StyleRef: return "the key of a Style";
        case PK_TemplateRef: return "the key of a ControlTemplate";
        case PK_DataTemplateRef: return "the key of a DataTemplate";
        case PK_ViewRef: return "the key of a CollectionView";
        case PK_Collection: return "a list: {Binding ...}";
        case PK_Code: return "{Expr ...}";
        case PK_Image: return "the path of an image";
        case PK_Geometry: return "a symbol (Up, Home, ...) or path data";
        default: return kinds[p->kind];
    }
}

typedef struct SmAttrInfo { const char* name; const char* detail; } SmAttrInfo;
static void smAddAttr(FcCtx* t, FcList* l, const char* name, const char* detail) { SmAttrInfo* a = (SmAttrInfo*)fcAlloc(t, sizeof *a); a->name = name; a->detail = detail; fcListAdd(t, l, a); }

/* AttributesFor */
static FcList* smAttributesFor(SmCtx* m, const char* tag, FcList* stack) {
    FcCtx* t = m->t;
    FcList* items = fcListNew(t);
    bool inTemplate = false;
    const FmElementDef* def;
    int i;
    for (i = 0; i < stack->count; i++) if (fmIEq(((SmOpen*)stack->items[i])->tag, "ControlTemplate")) inTemplate = true;
    if (fmIEq(tag, "Window") || fmIEq(tag, "View")) {
        smAddAttr(t, items, "Class", "the class of the code-behind");
        smAddAttr(t, items, "Base", "the name of the generated base class");
        smAddAttr(t, items, "namespace", "the namespace of the markup (local:)");
        if (fmIEq(tag, "Window")) smAddAttr(t, items, "Title", "the title of the window");
        smAddAttr(t, items, "Width", "pixels");
        smAddAttr(t, items, "Height", "pixels");
        if (fmIEq(tag, "Window"))
            for (i = 0; i < (int)(sizeof fmRootProperties / sizeof fmRootProperties[0]); i++) smAddAttr(t, items, fmRootProperties[i].name, fcFmt(t, "the UI root: %s", smDescribe(t, &fmRootProperties[i])));
        return items;
    }
    if (fmIEq(tag, "CollectionView")) {
        FmElementDef* cv = fmSchema(m->sc)->collectionView;
        smAddAttr(t, items, "Key", NULL);
        for (i = 0; i < cv->properties->count; i++) { const FmPropDef* p = (const FmPropDef*)cv->properties->items[i]; smAddAttr(t, items, p->name, smDescribe(t, p)); }
        return items;
    }
    {
        const char* fixed[4] = { NULL, NULL, NULL, NULL };
        bool known = true;
        if (fmIEq(tag, "converter")) { fixed[0] = "Key"; fixed[1] = "Type"; }
        else if (fmIEq(tag, "style")) { fixed[0] = "Key"; fixed[1] = "Target"; fixed[2] = "BasedOn"; }
        else if (fmIEq(tag, "setter")) { fixed[0] = "Property"; fixed[1] = "Value"; if (inTemplate) fixed[2] = "Target"; }
        else if (fmIEq(tag, "trigger") || fmIEq(tag, "condition")) { fixed[0] = "Property"; fixed[1] = "Value"; if (inTemplate) fixed[2] = "Source"; }
        else if (fmIEq(tag, "controltemplate")) { fixed[0] = "Key"; fixed[1] = "Target"; }
        else if (fmIEq(tag, "datatemplate")) fixed[0] = "Key";
        else if (fmIEq(tag, "resources")) { }
        else known = false;
        if (known) { for (i = 0; fixed[i]; i++) smAddAttr(t, items, fixed[i], NULL); return items; }
    }
    if ((def = fmFindDef(m->sc, tag)) == NULL) return items;
    if (def->cls && def->cls[0]) smAddAttr(t, items, "Name", "a field of the generated class");
    for (i = 0; i < def->properties->count; i++) { const FmPropDef* p = (const FmPropDef*)def->properties->items[i]; smAddAttr(t, items, p->name, smDescribe(t, p)); }
    for (i = 0; i < def->events->count; i++) {
        const FmEventDef* e = (const FmEventDef*)def->events->items[i];
        FcBuf b = {0};
        int a, n = fmArgCount(e);
        fcBufStr(&b, "event: the name of a method");
        if (n > 0) { fcBufStr(&b, " (sender, "); for (a = 0; a < n; a++) fcBufFmt(&b, "%s%s", a ? ", " : "", e->args[a]); fcBufChar(&b, ')'); }
        else fcBufStr(&b, " (sender)");
        smAddAttr(t, items, e->name, fcBufDone(t, &b));
    }
    return items;
}

static FcList* smAttributesOf(SmCtx* m, SmOpen* tag, FcList* stack) {
    FcCtx* t = m->t;
    FcList* infos = smAttributesFor(m, tag->tag, stack);
    FcList* items = fcListNew(t);
    int i;
    for (i = 0; i < infos->count; i++) {
        SmAttrInfo* a = (SmAttrInfo*)infos->items[i];
        if (smAttr(tag, a->name)) continue;
        fcListAdd(t, items, smItem(t, fcFmt(t, "%s=\"\"", a->name), MK_ATTRIBUTE, a->detail, 1, a->name));
        if (strcmp(a->name, "namespace") == 0) fcListAdd(t, items, smItem(t, "namespace:", MK_NAMESPACE, "binds another namespace to a prefix: namespace:m=\"App.Models\"", 0, "namespace:prefix"));
    }
    return items;
}

/* ---- values ---- */
static FcList* smPrefixes(SmCtx* m, SmInfo* info) {
    FcCtx* t = m->t;
    FcList* r = fcListNew(t);
    int i;
    fcListAdd(t, r, smItem(t, "local:", MK_NAMESPACE, info->ownNamespace ? info->ownNamespace : "the namespace of the markup", 0, NULL));
    for (i = 0; i < info->prefixKeys->count; i++) fcListAdd(t, r, smItem(t, fcFmt(t, "%s:", (const char*)info->prefixKeys->items[i]), MK_NAMESPACE, (const char*)info->prefixValues->items[i], 0, NULL));
    return r;
}
static void smWords(FcCtx* t, FcList* out, const char* const* words, int n, const char* detail) { int i; for (i = 0; n < 0 ? words[i] != NULL : i < n; i++) fcListAdd(t, out, smItem(t, words[i], MK_VALUE, detail, 0, NULL)); }
static void smWordList(FcCtx* t, FcList* out, FcList* words, const char* detail) { int i; for (i = 0; i < words->count; i++) fcListAdd(t, out, smItem(t, (const char*)words->items[i], MK_VALUE, detail, 0, NULL)); }

static FcList* smValuesOf(SmCtx* m, const FmPropDef* p, SmInfo* info) {
    FcCtx* t = m->t;
    FcList* r = fcListNew(t);
    static const char* const bools[] = { "true", "false" };
    static const char* const orient[] = { "Horizontal", "Vertical" };
    static const char* const colors[] = { "#000000", "#FFFFFF" };
    bool refKind = p->kind == PK_StyleRef || p->kind == PK_TemplateRef || p->kind == PK_DataTemplateRef || p->kind == PK_ViewRef;
    switch (p->kind) {
        case PK_Bool: smWords(t, r, bools, 2, NULL); break;
        case PK_Orientation: smWords(t, r, orient, 2, NULL); break;
        case PK_Enum: if (p->choices) smWords(t, r, p->choices, -1, NULL); break;
        case PK_StyleRef: smWordList(t, r, info->styles, "style"); break;
        case PK_TemplateRef: smWordList(t, r, info->templates, "control template"); break;
        case PK_DataTemplateRef: smWordList(t, r, info->dataTemplates, "data template"); break;
        case PK_ViewRef: smWordList(t, r, info->views, "collection view"); break;
        case PK_Color: smWords(t, r, colors, 2, "colour"); break;
        case PK_Geometry: smWords(t, r, fmIconNames, -1, "symbol"); break;
        default: break;
    }
    if (refKind) return r;
    fcListAdd(t, r, smItem(t, "{Binding }", MK_EXTENSION, "the value follows the data context", 1, NULL));
    fcListAdd(t, r, smItem(t, "{Enum }", MK_EXTENSION, "an enum member of your script", 1, NULL));
    fcListAdd(t, r, smItem(t, "{Static }", MK_EXTENSION, "a constant of your script", 1, NULL));
    fcListAdd(t, r, smItem(t, "{Expr }", MK_EXTENSION, "any fire expression", 1, NULL));
    fcListAdd(t, r, smItem(t, "[]", MK_EXTENSION, "a new object: [Type Arg1, Arg2]", 1, "[Type ...]"));
    return r;
}

static const FmElementDef* smTargetOf(SmCtx* m, FcList* stack) {
    int i;
    for (i = stack->count - 1; i >= 0; i--) {
        SmOpen* o = (SmOpen*)stack->items[i];
        const char* target;
        if ((fmIEq(o->tag, "Style") || fmIEq(o->tag, "ControlTemplate")) && (target = smAttr(o, "target")) != NULL) return fmFindTarget(m->sc, target);
    }
    return NULL;
}

typedef struct SmResult { int replaceStart; FcList* items; } SmResult;
static SmResult smResult(int start, FcList* items) { SmResult r; r.replaceStart = start; r.items = items; return r; }

static SmResult smExtensionValues(SmCtx* m, FcList* stack, SmInfo* info, const char* inside, int insideStart) {
    FcCtx* t = m->t;
    static const char* const extensions[] = { "Binding", "TemplateBinding", "Enum", "Static", "Expr" };
    const char* space = inside;
    const char* keyword;
    const char* rest;
    int restStart, i;
    while (*space && *space != ' ' && *space != '\t') space++;
    if (!*space) {
        FcList* items = fcListNew(t);
        for (i = 0; i < 5; i++) fcListAdd(t, items, smItem(t, fcFmt(t, "%s ", extensions[i]), MK_EXTENSION, NULL, 0, extensions[i]));
        return smResult(insideStart, smRank(t, items, inside));
    }
    keyword = fcStrN(t, inside, (size_t)(space - inside));
    restStart = insideStart + fc8Units(keyword) + 1;
    rest = space + 1;
    if (fmIEq(keyword, "binding")) {
        const char* comma = strrchr(rest, ',');
        const char* part = comma ? comma + 1 : rest;
        const char* equals;
        int partStart;
        static const char* const options[] = { "Mode=", "Converter=", "ElementName=", "Path=" };
        FcList* items;
        while (*part == ' ') part++;
        partStart = fc8Units(fcStrN(t, rest, (size_t)(part - rest)));
        equals = strchr(part, '=');
        if (equals) {
            const char* option = fcStrN(t, part, (size_t)(equals - part));
            const char* value = equals + 1;
            FcList* values = fcListNew(t);
            const char* a = option;
            const char* b = option + strlen(option);
            while (*a && fcIsWhiteSpace((uint16_t)(unsigned char)*a)) a++;
            while (b > a && fcIsWhiteSpace((uint16_t)(unsigned char)b[-1])) b--;
            option = fcStrN(t, a, (size_t)(b - a));
            if (fmIEq(option, "mode")) { static const char* const modes[] = { "OneWay", "TwoWay", "OneTime" }; smWords(t, values, modes, 3, NULL); }
            else if (fmIEq(option, "converter")) {
                FcList* keys = fcListNew(t);
                for (i = 0; i < info->converters->count; i++) smAddOnce(t, keys, (const char*)info->converters->items[i]);
                for (i = 0; fmBuiltInConverters[i]; i += 2) smAddOnce(t, keys, fmBuiltInConverters[i]);
                smWordList(t, values, keys, "converter");
            } else if (fmIEq(option, "elementname")) smWordList(t, values, info->names, "element");
            return smResult(restStart + partStart + fc8Units(fcStrN(t, part, (size_t)(equals - part))) + 1, smRank(t, values, value));
        }
        items = fcListNew(t);
        for (i = 0; i < 4; i++) fcListAdd(t, items, smItem(t, options[i], MK_EXTENSION, "binding option", 0, NULL));
        return smResult(restStart + partStart, partStart == 0 && !part[0] ? items : smRank(t, items, part));
    }
    if (fmIEq(keyword, "templatebinding")) {
        const FmElementDef* target = NULL;
        FcList* props = fcListNew(t);
        for (i = stack->count - 1; i >= 0; i--) {
            SmOpen* o = (SmOpen*)stack->items[i];
            if (fmIEq(o->tag, "ControlTemplate")) { const char* name = smAttr(o, "target"); if (name) target = fmFindTarget(m->sc, name); break; }
        }
        if (target) for (i = 0; i < target->properties->count; i++) { const FmPropDef* p = (const FmPropDef*)target->properties->items[i]; fcListAdd(t, props, smItem(t, p->name, MK_VALUE, smDescribe(t, p), 0, NULL)); }
        for (i = 0; i < (int)(sizeof fmStateProperties / sizeof fmStateProperties[0]); i++) fcListAdd(t, props, smItem(t, fmStateProperties[i].name, MK_VALUE, smDescribe(t, &fmStateProperties[i]), 0, NULL));
        return smResult(restStart, smRank(t, props, rest));
    }
    if (fmIEq(keyword, "enum") || fmIEq(keyword, "static"))
        return strchr(rest, ':') || strchr(rest, '.') ? smResult(0, fcListNew(t)) : smResult(restStart, smRank(t, smPrefixes(m, info), rest));
    return smResult(0, fcListNew(t));
}

static SmResult smValues(SmCtx* m, SmOpen* tag, const char* attribute, FcList* stack, SmInfo* info, int valueStart, int offset) {
    FcCtx* t = m->t;
    const char* typed = smSub(m, valueStart, offset);
    const char* brace = strrchr(typed, '{');
    const FmElementDef* target;
    const FmPropDef* property = NULL;
    FcList* items = fcListNew(t);
    int i;
    if (brace && !strchr(brace, '}')) return smExtensionValues(m, stack, info, brace + 1, valueStart + fc8Units(fcStrN(t, typed, (size_t)(brace - typed))) + 1);
    if (typed[0] == '[' && !strchr(typed, ' ')) return smResult(valueStart + 1, smRank(t, smPrefixes(m, info), typed + 1));
    target = smTargetOf(m, stack);
    if (fmIEq(tag->tag, "converter") && fmIEq(attribute, "type")) items = smPrefixes(m, info);
    else if ((fmIEq(tag->tag, "style") || fmIEq(tag->tag, "controltemplate")) && fmIEq(attribute, "target")) {
        FmSchema* s = fmSchema(m->sc);
        for (i = 0; i < s->elements->count; i++) { FmElementDef* d = (FmElementDef*)s->elements->items[i]; if (!fmIsPart(d)) fcListAdd(t, items, smItem(t, d->tag, MK_VALUE, NULL, 0, NULL)); }
        fcListAdd(t, items, smItem(t, "Element", MK_VALUE, NULL, 0, NULL));
    } else if (fmIEq(tag->tag, "style") && fmIEq(attribute, "basedon")) smWordList(t, items, info->styles, "style");
    else if ((fmIEq(tag->tag, "setter") || fmIEq(tag->tag, "trigger") || fmIEq(tag->tag, "condition")) && fmIEq(attribute, "property")) {
        if (target) {
            for (i = 0; i < target->properties->count; i++) { const FmPropDef* p = (const FmPropDef*)target->properties->items[i]; fcListAdd(t, items, smItem(t, p->name, MK_VALUE, smDescribe(t, p), 0, NULL)); }
            if (!fmIEq(tag->tag, "setter")) for (i = 0; i < (int)(sizeof fmStateProperties / sizeof fmStateProperties[0]); i++) fcListAdd(t, items, smItem(t, fmStateProperties[i].name, MK_VALUE, "state", 0, NULL));
        }
    } else if ((fmIEq(tag->tag, "setter") || fmIEq(tag->tag, "trigger") || fmIEq(tag->tag, "condition")) && fmIEq(attribute, "value")) {
        const char* propertyName = smAttr(tag, "property");
        property = !propertyName ? NULL : (target ? fmProperty(target, propertyName) : NULL);
        if (propertyName && !property) property = fmStateProperty(propertyName);
    } else if (fmIEq(tag->tag, "window") && fmRootProperty(attribute)) property = fmRootProperty(attribute);
    else {
        const FmElementDef* def = fmIEq(tag->tag, "collectionview") ? fmSchema(m->sc)->collectionView : fmFindDef(m->sc, tag->tag);
        if (def && fmEvent(def, attribute)) smWordList(t, items, info->handlers, "handler");
        else property = def ? fmProperty(def, attribute) : NULL;
    }
    if (property) items = smValuesOf(m, property, info);
    return smResult(valueStart, smRank(t, items, typed));
}

typedef enum SmState { SS_TEXT, SS_TAGNAME, SS_CLOSETAGNAME, SS_INTAG, SS_ATTRNAME, SS_AFTERATTRNAME, SS_AFTEREQUALS, SS_VALUE } SmState;

/* MarkupCompletion.Suggest */
static SmResult smSuggest(SmCtx* m, int offset) {
    FcCtx* t = m->t;
    FcList* stack = fcListNew(t);
    SmState state = SS_TEXT;
    SmOpen* tag = NULL;
    int wordStart = 0, valueStart = 0, i = 0;
    const char* attribute = "";
    uint16_t quote = '"';
    SmInfo* info;
    SmOpen* parent;
    if (offset < 0) offset = 0;
    if (offset > m->n) offset = m->n;
    while (i < offset) {
        uint16_t c = m->s[i];
        switch (state) {
            case SS_TEXT:
                if (c == '<') {
                    if (smStarts(m, i, "<!--")) {
                        int end = -1, k;
                        for (k = i + 4; k + 2 < m->n; k++) if (m->s[k] == '-' && m->s[k + 1] == '-' && m->s[k + 2] == '>') { end = k; break; }
                        if (end < 0 || end + 3 > offset) return smResult(0, fcListNew(t));
                        i = end + 3;
                        continue;
                    }
                    if (smStarts(m, i, "<?") || smStarts(m, i, "<!")) {
                        int end = -1, k;
                        for (k = i; k < m->n; k++) if (m->s[k] == '>') { end = k; break; }
                        if (end < 0 || end + 1 > offset) return smResult(0, fcListNew(t));
                        i = end + 1;
                        continue;
                    }
                    if (i + 1 < m->n && m->s[i + 1] == '/') { state = SS_CLOSETAGNAME; wordStart = i + 2; i += 2; continue; }
                    state = SS_TAGNAME;
                    tag = smOpenNew(t);
                    wordStart = i + 1;
                }
                break;
            case SS_CLOSETAGNAME:
                if (c == '>') {
                    const char* name = smSub(m, wordStart, i);
                    const char* a = name;
                    const char* b = name + strlen(name);
                    int at = -1, k;
                    while (*a && fcIsWhiteSpace((uint16_t)(unsigned char)*a)) a++;
                    while (b > a && fcIsWhiteSpace((uint16_t)(unsigned char)b[-1])) b--;
                    name = fcStrN(t, a, (size_t)(b - a));
                    for (k = stack->count - 1; k >= 0; k--) if (strcmp(((SmOpen*)stack->items[k])->tag, name) == 0) { at = k; break; }
                    if (at >= 0) stack->count = at;
                    state = SS_TEXT;
                }
                break;
            case SS_TAGNAME:
                if (fcIsWhiteSpace(c) || c == '/' || c == '>') { tag->tag = smSub(m, wordStart, i); state = SS_INTAG; continue; }
                break;
            case SS_INTAG:
                if (c == '>') {
                    bool selfClosing = i > 0 && m->s[i - 1] == '/';
                    if (!selfClosing) fcListAdd(t, stack, tag);
                    state = SS_TEXT;
                } else if (smNameChar(c)) { state = SS_ATTRNAME; wordStart = i; }
                break;
            case SS_ATTRNAME:
                if (!smNameChar(c)) { attribute = smSub(m, wordStart, i); state = SS_AFTERATTRNAME; continue; }
                break;
            case SS_AFTERATTRNAME:
                if (c == '=') state = SS_AFTEREQUALS;
                else if (!fcIsWhiteSpace(c)) { state = SS_INTAG; continue; }
                break;
            case SS_AFTEREQUALS:
                if (c == '"' || c == '\'') { quote = c; state = SS_VALUE; valueStart = i + 1; }
                else if (!fcIsWhiteSpace(c)) { state = SS_INTAG; continue; }
                break;
            case SS_VALUE:
                if (c == quote) { smSetAttr(t, tag, attribute, smSub(m, valueStart, i)); state = SS_INTAG; }
                break;
        }
        i++;
    }
    info = smReadInfo(m);
    parent = stack->count > 0 ? (SmOpen*)stack->items[stack->count - 1] : NULL;
    switch (state) {
        case SS_TAGNAME: return smResult(wordStart, smRank(t, smElementsIn(m, parent), smSub(m, wordStart, offset)));
        case SS_CLOSETAGNAME: {
            FcList* items = fcListNew(t);
            if (!parent) return smResult(0, items);
            fcListAdd(t, items, smItem(t, fcFmt(t, "%s>", parent->tag), MK_ELEMENT, fcFmt(t, "closes <%s>", parent->tag), 0, NULL));
            return smResult(wordStart, items);
        }
        case SS_INTAG: return smResult(offset, smAttributesOf(m, tag, stack));
        case SS_ATTRNAME: return smResult(wordStart, smRank(t, smAttributesOf(m, tag, stack), smSub(m, wordStart, offset)));
        case SS_VALUE: return smValues(m, tag, attribute, stack, info, valueStart, offset);
        default: return smResult(0, fcListNew(t));
    }
}

static void svMarkupCompletion(SvServer* S, SvDoc* d, int offset, FcBuf* b) {
    SmCtx m;
    SmResult r;
    int i;
    m.t = S->lang.tmp;
    m.sc = &S->lang.pctx;
    m.s = d->text->p;
    m.n = d->text->len;
    r = smSuggest(&m, offset);
    fcBufFmt(b, "{\"replaceStart\":%d,\"items\":[", r.replaceStart);
    for (i = 0; i < r.items->count; i++) {
        SmItem* it = (SmItem*)r.items->items[i];
        if (i) fcBufChar(b, ',');
        fcBufStr(b, "{\"text\":");
        svJsonEsc(b, it->text);
        fcBufFmt(b, ",\"kind\":\"%s\",\"caretBack\":%d", smKindNames[it->kind], it->caretBack);
        if (it->detail) { fcBufStr(b, ",\"detail\":"); svJsonEsc(b, it->detail); }
        if (it->display) { fcBufStr(b, ",\"display\":"); svJsonEsc(b, it->display); }
        fcBufChar(b, '}');
    }
    fcBufStr(b, "]}");
}
