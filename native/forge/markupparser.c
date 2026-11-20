/* native/forge/markupparser.c - port of src/fire.UI.Markup/MarkupParser.cs (commit 7a594769): reads a markup file (XML, xml.c) into an FmDocument,
 * collecting the mistakes with their lines. The namespaces of the markup that is read ([ThreadStatic] s_scope in C#) travel as FmScope (L6). */

#define FM_NAMESPACE_PREFIX_URI "urn:fire:namespace"

/* NamespaceScope */
typedef struct FmScope {
    const char* own;
    FcList* boundPrefixes;   /* Dictionary<string, string> Bound: keys and values in insertion order */
    FcList* boundValues;
} FmScope;

static void fmScopeBind(FcCtx* c, FmScope* s, const char* prefix, const char* ns) {
    int i;
    for (i = 0; i < s->boundPrefixes->count; i++) if (strcmp((const char*)s->boundPrefixes->items[i], prefix) == 0) { s->boundValues->items[i] = (void*)ns; return; }
    fcListAdd(c, s->boundPrefixes, (void*)prefix);
    fcListAdd(c, s->boundValues, (void*)ns);
}

/* \w of .NET's Regex (letters, digits, the connector punctuation; marks are rare in a tag) */
static bool fmIsWordUnit(uint16_t u) { return fcIsLetterOrDigit(u) || u == '_'; }

/* DeclareNamespacePrefix: `namespace:prefix="..."` needs the prefix `namespace` declared for XML */
static void fmDeclareNamespacePrefix(FcCtx* c, const uint16_t* s, int n, const uint16_t** out, int* outN) {
    int i, k;
    bool hasDecl = false, uses = false;
    static const char decl[] = "xmlns:namespace", use[] = "namespace:";
    *out = s;
    *outN = n;
    for (i = 0; i < n && !hasDecl; i++) {
        for (k = 0; decl[k] && i + k < n && s[i + k] == (uint16_t)decl[k]; k++) {}
        if (!decl[k]) hasDecl = true;
    }
    for (i = 0; i < n && !uses; i++) {
        for (k = 0; use[k] && i + k < n && s[i + k] == (uint16_t)use[k]; k++) {}
        if (!use[k]) uses = true;
    }
    if (hasDecl || !uses) return;
    /* RootTag: <(?![?!/])[A-Za-z_][\w.\-]* */
    for (i = 0; i < n; i++) {
        int j;
        if (s[i] != '<' || i + 1 >= n) continue;
        if (s[i + 1] == '?' || s[i + 1] == '!' || s[i + 1] == '/') continue;
        if (!((s[i + 1] >= 'A' && s[i + 1] <= 'Z') || (s[i + 1] >= 'a' && s[i + 1] <= 'z') || s[i + 1] == '_')) continue;
        j = i + 2;
        while (j < n && (fmIsWordUnit(s[j]) || s[j] == '.' || s[j] == '-')) j++;
        {
            static const char ins[] = " xmlns:namespace=\"" FM_NAMESPACE_PREFIX_URI "\"";
            int m = (int)strlen(ins);
            uint16_t* r = (uint16_t*)fcAlloc(c, sizeof(uint16_t) * (size_t)(n + m + 1));
            memcpy(r, s, sizeof(uint16_t) * (size_t)j);
            for (k = 0; k < m; k++) r[j + k] = (uint16_t)ins[k];
            memcpy(r + j + m, s + j, sizeof(uint16_t) * (size_t)(n - j));
            *out = r;
            *outN = n + m;
        }
        return;
    }
}

/* QualifyName */
static const char* fmQualifyName(FcCtx* c, FmScope* scope, const char* name, int line, FcList* errors) {
    const char* colon = strchr(name, ':');
    const char* prefix;
    const char* rest;
    int i;
    if (!colon) return name;
    prefix = fcStrN(c, name, (size_t)(colon - name));
    rest = colon + 1;
    if (strcmp(prefix, "local") == 0) return !scope || !scope->own ? rest : fcFmt(c, "%s.%s", scope->own, rest);
    if (scope) for (i = 0; i < scope->boundPrefixes->count; i++)
        if (strcmp((const char*)scope->boundPrefixes->items[i], prefix) == 0) return fcFmt(c, "%s.%s", (const char*)scope->boundValues->items[i], rest);
    fcListAdd(c, errors, fmDiag(c, line, fcFmt(c, "Unknown namespace prefix '%s' (bind it on the root element: namespace:%s=\"Name.Space\"; 'local' is the namespace of the markup).", prefix, prefix)));
    return NULL;
}

static int fmLineOf(const FcXNode* x) { return x->line; }

/* the attributes of an element that are no namespace declarations */
static FcList* fmXAttrs(FcCtx* c, const FcXNode* e) {
    FcList* l = fcListNew(c);
    int i;
    for (i = 0; i < e->attrs->count; i++) if (!((FcXAttr*)e->attrs->items[i])->nsDecl) fcListAdd(c, l, e->attrs->items[i]);
    return l;
}
static FcList* fmXElements(FcCtx* c, const FcXNode* e) {
    FcList* l = fcListNew(c);
    int i;
    for (i = 0; i < e->nodes->count; i++) if (((FcXNode*)e->nodes->items[i])->kind == FCX_ELEMENT) fcListAdd(c, l, e->nodes->items[i]);
    return l;
}

/* int.TryParse(text, out n) (NumberStyles.Integer, the invariant culture): white space around, a sign, digits */
static bool fmNumWhite(char ch) { return ch == ' ' || (ch >= 0x09 && ch <= 0x0D); }   /* (the white space of .NET's number parsing) */
static bool fmTryParseInt(const char* text, int* out) {
    const char* p = text;
    bool neg = false, any = false;
    int64_t v = 0;
    while (fmNumWhite(*p)) p++;
    if (*p == '+' || *p == '-') { neg = *p == '-'; p++; }
    while (*p >= '0' && *p <= '9') {
        v = v * 10 + (*p - '0');
        if (v > 2147483648LL) return false;
        p++;
        any = true;
    }
    if (!any) return false;
    while (fmNumWhite(*p)) p++;
    if (*p) return false;
    if (neg) v = -v;
    if (v > 2147483647LL) return false;
    *out = (int)v;
    return true;
}

static int fmReadSize(FcCtx* c, const FcXAttr* attribute, int fallback, FcList* errors) {
    int n;
    if (fmTryParseInt(attribute->value, &n) && n > 0) return n;
    fcListAdd(c, errors, fmDiag(c, attribute->line, fcFmt(c, "'%s' must be a positive whole number, not '%s'.", attribute->local, attribute->value)));
    return fallback;
}

static FmValue* fmReadValue(FcCtx* c, FmScope* scope, const char* raw, int line, FcList* errors);
static bool fmIsTypeName(const char* text);
static void fmReadElement(FcCtx* c, FmScope* scope, const FcXNode* node, FcList* into, FcList* errors, const FmElementDef* parent);
static void fmValidate(FcCtx* c, FmDocument* document);

/* Attr: the value of an attribute, whatever the case of its name; NULL if it is missing */
static const char* fmXAttr(const FcXNode* node, const char* name) {
    int i;
    for (i = 0; i < node->attrs->count; i++) {
        const FcXAttr* a = (const FcXAttr*)node->attrs->items[i];
        if (!a->nsDecl && a->ns[0] == 0 && fmIEq(a->local, name)) return a->value;
    }
    return NULL;
}

static const char* fmKeyOf(FcCtx* c, const FcXNode* node, const char* what, FcList* errors, bool required) {
    const char* key = fmXAttr(node, "Key");
    if (fmIsBlank(key)) {
        if (required) fcListAdd(c, errors, fmDiag(c, fmLineOf(node), fcFmt(c, "A '%s' needs Key=\"...\".", what)));
        return NULL;
    }
    if (!fmIsIdentifier(key)) {
        fcListAdd(c, errors, fmDiag(c, fmLineOf(node), fcFmt(c, "'%s' is not a valid key.", key)));
        return NULL;
    }
    return key;
}

static void fmCheckAttributes(FcCtx* c, const FcXNode* node, const char* const* known, int knownCount, FcList* errors) {
    int i, k;
    for (i = 0; i < node->attrs->count; i++) {
        const FcXAttr* a = (const FcXAttr*)node->attrs->items[i];
        bool found = false;
        if (a->nsDecl) continue;
        for (k = 0; k < knownCount; k++) if (fmIEq(known[k], a->local)) found = true;
        if (!found) {
            FcBuf b = {0};
            fcBufStr(&b, "");
            for (k = 0; k < knownCount; k++) { if (k) fcBufStr(&b, ", "); fcBufStr(&b, known[k]); }
            fcListAdd(c, errors, fmDiag(c, a->line, fcFmt(c, "'%s' has no attribute '%s' (known: %s).", node->local, a->local, fcBufDone(c, &b))));
        }
    }
}

static const char* fmPropertyNames(FcCtx* c, const FmElementDef* d, bool withState) {
    FcBuf b = {0};
    int i;
    size_t k;
    fcBufStr(&b, "");
    for (i = 0; i < d->properties->count; i++) { if (i) fcBufStr(&b, ", "); fcBufStr(&b, ((const FmPropDef*)d->properties->items[i])->name); }
    if (withState)
        for (k = 0; k < sizeof fmStateProperties / sizeof fmStateProperties[0]; k++) { if (i || k) fcBufStr(&b, ", "); fcBufStr(&b, fmStateProperties[k].name); }
    return fcBufDone(c, &b);
}

/* ReadSetter (template: whether it is in a ControlTemplate) */
static FmSetter* fmReadSetter(FcCtx* c, FmScope* scope, const FcXNode* node, const FmElementDef* target, bool template, FcList* errors) {
    static const char* const known2[] = { "Property", "Value" }, * const known3[] = { "Property", "Value", "Target" };
    const char* property;
    const char* value;
    const char* part;
    FmValue* parsed;
    FmSetter* s;
    fmCheckAttributes(c, node, template ? known3 : known2, template ? 3 : 2, errors);
    property = fmXAttr(node, "Property");
    value = fmXAttr(node, "Value");
    part = fmXAttr(node, "Target");
    if (fmIsBlank(property) || !value) {
        fcListAdd(c, errors, fmDiag(c, fmLineOf(node), "A 'Setter' needs Property=\"...\" and Value=\"...\"."));
        return NULL;
    }
    if (!template && !fmProperty(target, property)) {
        fcListAdd(c, errors, fmDiag(c, fmLineOf(node), fcFmt(c, "'%s' has no property '%s' (known: %s).", target->tag, property, fmPropertyNames(c, target, false))));
        return NULL;
    }
    parsed = fmReadValue(c, scope, value, fmLineOf(node), errors);
    if (!parsed) return NULL;
    if (parsed->kind == FM_BINDING || parsed->kind == FM_TEMPLATEBINDING) {
        fcListAdd(c, errors, fmDiag(c, fmLineOf(node), "A 'Setter' takes a plain value, not a binding."));
        return NULL;
    }
    s = (FmSetter*)fcAlloc(c, sizeof *s);
    s->property = property;
    s->value = parsed;
    s->target = part;
    s->line = fmLineOf(node);
    return s;
}

/* ReadTrigger: Condition() of the node and its <Condition> children */
static bool fmTriggerCondition(FcCtx* c, FmScope* scope, const FcXNode* from, const FmElementDef* target, FmTrigger* trigger, FcList* errors) {
    const char* property = fmXAttr(from, "Property");
    const char* value = fmXAttr(from, "Value");
    const char* source = fmXAttr(from, "Source");
    FmValue* parsed;
    FmCondition* cond;
    if (fmIsBlank(property) || !value) {
        fcListAdd(c, errors, fmDiag(c, fmLineOf(from), "A trigger condition needs Property=\"...\" and Value=\"...\"."));
        return false;
    }
    if (!source && !fmProperty(target, property) && !fmStateProperty(property)) {
        fcListAdd(c, errors, fmDiag(c, fmLineOf(from), fcFmt(c, "'%s' has no property '%s' to test (known: %s).", target->tag, property, fmPropertyNames(c, target, true))));
        return false;
    }
    parsed = fmReadValue(c, scope, value, fmLineOf(from), errors);
    if (!parsed || parsed->kind == FM_BINDING || parsed->kind == FM_TEMPLATEBINDING) {
        if (parsed) fcListAdd(c, errors, fmDiag(c, fmLineOf(from), "A trigger condition compares with a plain value, not a binding."));
        return false;
    }
    cond = (FmCondition*)fcAlloc(c, sizeof *cond);
    cond->property = property;
    cond->value = parsed;
    cond->source = source;
    cond->line = fmLineOf(from);
    fcListAdd(c, trigger->conditions, cond);
    return true;
}

static FmTrigger* fmReadTrigger(FcCtx* c, FmScope* scope, const FcXNode* node, const FmElementDef* target, bool template, FcList* errors) {
    static const char* const known2[] = { "Property", "Value" }, * const known3[] = { "Property", "Value", "Source" };
    FmTrigger* trigger = (FmTrigger*)fcAlloc(c, sizeof *trigger);
    FcList* children;
    bool ok = true;
    int i;
    fmCheckAttributes(c, node, template ? known3 : known2, template ? 3 : 2, errors);
    trigger->line = fmLineOf(node);
    trigger->conditions = fcListNew(c);
    trigger->setters = fcListNew(c);
    if (!fmTriggerCondition(c, scope, node, target, trigger, errors)) ok = false;
    children = fmXElements(c, node);
    for (i = 0; i < children->count; i++) {
        const FcXNode* child = (const FcXNode*)children->items[i];
        if (fmIEq(child->local, "Condition")) { if (!fmTriggerCondition(c, scope, child, target, trigger, errors)) ok = false; }
        else if (fmIEq(child->local, "Setter")) {
            FmSetter* setter = fmReadSetter(c, scope, child, target, template, errors);
            if (setter) fcListAdd(c, trigger->setters, setter);
        }
        else fcListAdd(c, errors, fmDiag(c, fmLineOf(child), fcFmt(c, "A 'Trigger' contains 'Condition' and 'Setter', not '%s'.", child->local)));
    }
    return ok ? trigger : NULL;
}

static void fmReadStyle(FcCtx* c, FmScope* scope, const FcXNode* node, FmDocument* document, FcList* errors) {
    static const char* const known[] = { "Key", "Target", "BasedOn" };
    const char* key;
    const char* targetName;
    const FmElementDef* target;
    FmStyle* style;
    FcList* children;
    int i;
    fmCheckAttributes(c, node, known, 3, errors);
    key = fmKeyOf(c, node, "Style", errors, false);
    targetName = fmXAttr(node, "Target");
    target = targetName ? fmFindTarget(c, targetName) : NULL;
    if (!target) {
        fcListAdd(c, errors, fmDiag(c, fmLineOf(node), !targetName ? "A 'Style' needs Target=\"...\" (the kind of element it is for, e.g. Button)."
            : fcFmt(c, "Unknown style target '%s' (an element of the markup, or Element).", targetName)));
        return;
    }
    style = (FmStyle*)fcAlloc(c, sizeof *style);
    style->key = key;
    style->target = target->tag;
    style->basedOn = fmXAttr(node, "BasedOn");
    style->line = fmLineOf(node);
    style->setters = fcListNew(c);
    style->triggers = fcListNew(c);
    children = fmXElements(c, node);
    for (i = 0; i < children->count; i++) {
        const FcXNode* child = (const FcXNode*)children->items[i];
        if (fmIEq(child->local, "Setter")) {
            FmSetter* setter = fmReadSetter(c, scope, child, target, false, errors);
            if (setter) fcListAdd(c, style->setters, setter);
        } else if (fmIEq(child->local, "Trigger")) {
            FmTrigger* trigger = fmReadTrigger(c, scope, child, target, false, errors);
            if (trigger) fcListAdd(c, style->triggers, trigger);
        } else fcListAdd(c, errors, fmDiag(c, fmLineOf(child), fcFmt(c, "A 'Style' contains 'Setter' and 'Trigger', not '%s'.", child->local)));
    }
    fcListAdd(c, document->styles, style);
}

static void fmReadTemplate(FcCtx* c, FmScope* scope, const FcXNode* node, FmDocument* document, FcList* errors) {
    static const char* const known[] = { "Key", "Target" };
    const char* key;
    const char* targetName;
    const FmElementDef* target;
    FmTemplate* template;
    FcList* roots;
    FcList* children;
    int i;
    fmCheckAttributes(c, node, known, 2, errors);
    key = fmKeyOf(c, node, "ControlTemplate", errors, true);
    targetName = fmXAttr(node, "Target");
    target = targetName ? fmFindTarget(c, targetName) : NULL;
    if (!key) return;
    if (!target) {
        fcListAdd(c, errors, fmDiag(c, fmLineOf(node), !targetName ? "A 'ControlTemplate' needs Target=\"...\" (the kind of control it is for, e.g. Button)."
            : fcFmt(c, "Unknown template target '%s'.", targetName)));
        return;
    }
    template = (FmTemplate*)fcAlloc(c, sizeof *template);
    template->key = key;
    template->target = target->tag;
    template->line = fmLineOf(node);
    template->triggers = fcListNew(c);
    roots = fcListNew(c);
    children = fmXElements(c, node);
    for (i = 0; i < children->count; i++) {
        const FcXNode* child = (const FcXNode*)children->items[i];
        if (fmIEq(child->local, "Trigger")) {
            FmTrigger* trigger = fmReadTrigger(c, scope, child, target, true, errors);
            if (trigger) fcListAdd(c, template->triggers, trigger);
        } else fmReadElement(c, scope, child, roots, errors, NULL);
    }
    if (roots->count != 1) fcListAdd(c, errors, fmDiag(c, fmLineOf(node), "A 'ControlTemplate' contains exactly one element: the root of the parts."));
    else template->root = (FmElement*)roots->items[0];
    fcListAdd(c, document->templates, template);
}

static void fmReadDataTemplate(FcCtx* c, FmScope* scope, const FcXNode* node, FmDocument* document, FcList* errors) {
    static const char* const known[] = { "Key" };
    const char* key;
    FmDataTemplate* template;
    FcList* roots;
    FcList* children;
    int i;
    fmCheckAttributes(c, node, known, 1, errors);
    key = fmKeyOf(c, node, "DataTemplate", errors, true);
    if (!key) return;
    template = (FmDataTemplate*)fcAlloc(c, sizeof *template);
    template->key = key;
    template->line = fmLineOf(node);
    roots = fcListNew(c);
    children = fmXElements(c, node);
    for (i = 0; i < children->count; i++) fmReadElement(c, scope, (const FcXNode*)children->items[i], roots, errors, NULL);
    if (roots->count != 1) fcListAdd(c, errors, fmDiag(c, fmLineOf(node), "A 'DataTemplate' contains exactly one element: what is shown for one item."));
    else template->root = (FmElement*)roots->items[0];
    fcListAdd(c, document->dataTemplates, template);
}

static void fmReadView(FcCtx* c, FmScope* scope, const FcXNode* node, FmDocument* document, FcList* errors) {
    const FmElementDef* def = fmSchema(c)->collectionView;
    const char* known[8];
    int nk = 0, i;
    const char* key;
    FmElement* element;
    FmView* view;
    FcList* children;
    for (i = 0; i < def->properties->count; i++) known[nk++] = ((const FmPropDef*)def->properties->items[i])->name;
    known[nk++] = "Key";
    fmCheckAttributes(c, node, known, nk, errors);
    key = fmKeyOf(c, node, "CollectionView", errors, true);
    if (!key) return;
    children = fmXElements(c, node);
    if (children->count > 0) fcListAdd(c, errors, fmDiag(c, fmLineOf((const FcXNode*)children->items[0]), "A 'CollectionView' contains no elements."));
    element = fmElement(c, def->tag, fmLineOf(node));
    for (i = 0; i < node->attrs->count; i++) {
        const FcXAttr* a = (const FcXAttr*)node->attrs->items[i];
        const FmPropDef* property;
        FmValue* value;
        if (a->nsDecl || fmIEq(a->local, "Key")) continue;
        property = fmProperty(def, a->local);
        if (!property) continue;   /* reported by CheckAttributes */
        value = fmReadValue(c, scope, a->value, a->line, errors);
        if (value) fcListAdd(c, element->attributes, fmAttr(c, property->name, value, a->line));
    }
    view = (FmView*)fcAlloc(c, sizeof *view);
    view->key = key;
    view->line = fmLineOf(node);
    view->element = element;
    fcListAdd(c, document->views, view);
}

static void fmReadResources(FcCtx* c, FmScope* scope, const FcXNode* resources, FmDocument* document, FcList* errors) {
    FcList* items = fmXElements(c, resources);
    int i, k;
    for (i = 0; i < items->count; i++) {
        const FcXNode* item = (const FcXNode*)items->items[i];
        const char* kind = item->local;
        const char* key;
        const char* type;
        bool twice = false;
        if (fmIEq(kind, "Style")) { fmReadStyle(c, scope, item, document, errors); continue; }
        if (fmIEq(kind, "ControlTemplate")) { fmReadTemplate(c, scope, item, document, errors); continue; }
        if (fmIEq(kind, "DataTemplate")) { fmReadDataTemplate(c, scope, item, document, errors); continue; }
        if (fmIEq(kind, "CollectionView")) { fmReadView(c, scope, item, document, errors); continue; }
        if (!fmIEq(kind, "Converter")) {
            fcListAdd(c, errors, fmDiag(c, fmLineOf(item), fcFmt(c, "Unknown resource '%s' (known: Converter, Style, ControlTemplate, DataTemplate, CollectionView).", item->local)));
            continue;
        }
        key = fmXAttr(item, "Key");
        type = fmXAttr(item, "Type");
        if (fmIsBlank(key) || fmIsBlank(type)) {
            fcListAdd(c, errors, fmDiag(c, fmLineOf(item), "A 'Converter' needs Key=\"...\" (the name bindings use) and Type=\"...\" (the class)."));
            continue;
        }
        for (k = 0; k < document->converters->count; k++) if (strcmp(((FmConverter*)document->converters->items[k])->key, key) == 0) twice = true;
        if (!fmIsIdentifier(key)) fcListAdd(c, errors, fmDiag(c, fmLineOf(item), fcFmt(c, "'%s' is not a valid converter key.", key)));
        else if (!fmIsTypeName(type)) fcListAdd(c, errors, fmDiag(c, fmLineOf(item), fcFmt(c, "'%s' is not a valid class name.", type)));
        else if (twice) fcListAdd(c, errors, fmDiag(c, fmLineOf(item), fcFmt(c, "The converter '%s' is declared twice.", key)));
        else {
            const char* qualified = fmQualifyName(c, scope, type, fmLineOf(item), errors);
            if (qualified) {
                FmConverter* conv = (FmConverter*)fcAlloc(c, sizeof *conv);
                conv->key = key;
                conv->type = qualified;
                conv->line = fmLineOf(item);
                fcListAdd(c, document->converters, conv);
            }
        }
    }
}

/* Regex.Replace(text, @"\s+", " ") */
static const char* fmFoldSpace(FcCtx* c, const char* s) {
    FcBuf b = {0};
    const unsigned char* p = (const unsigned char*)s;
    bool inSpace = false;
    fcBufStr(&b, "");
    while (*p) {
        uint16_t u = fc8Next(&p);
        if (fcIsWhiteSpace(u)) { if (!inSpace) fcBufChar(&b, ' '); inSpace = true; continue; }
        inSpace = false;
        fcBufUnit(&b, u);
    }
    return fcBufDone(c, &b);
}

static const char* fmJoinTags(FcCtx* c, const char* const* tags, const char* sep) {
    FcBuf b = {0};
    int i;
    fcBufStr(&b, "");
    for (i = 0; tags && tags[i]; i++) { if (i) fcBufStr(&b, sep); fcBufFmt(&b, "'%s'", tags[i]); }
    return fcBufDone(c, &b);
}

static void fmReadElement(FcCtx* c, FmScope* scope, const FcXNode* node, FcList* into, FcList* errors, const FmElementDef* parent) {
    const FmElementDef* def = fmFindDef(c, node->local);
    FmElement* element;
    FcList* attrs;
    FcList* children;
    int i, k;
    if (!def) {
        FmSchema* s = fmSchema(c);
        FcBuf b = {0};
        bool first = true;
        fcBufStr(&b, "");
        for (i = 0; i < s->elements->count; i++) {
            const FmElementDef* e = (const FmElementDef*)s->elements->items[i];
            if (fmIsPart(e)) continue;
            if (!first) fcBufStr(&b, ", ");
            fcBufStr(&b, e->tag);
            first = false;
        }
        fcListAdd(c, errors, fmDiag(c, fmLineOf(node), fcFmt(c, "Unknown element '%s' (known: %s).", node->local, fcBufDone(c, &b))));
        return;
    }
    if (fmIsPart(def)) {
        bool inside = false;
        if (parent) for (k = 0; def->partOf[k]; k++) if (strcmp(def->partOf[k], parent->tag) == 0) inside = true;
        if (!inside) {
            fcListAdd(c, errors, fmDiag(c, fmLineOf(node), fcFmt(c, "'%s' can only be inside %s.", def->tag, fmJoinTags(c, def->partOf, " or "))));
            return;
        }
    }
    if (!fmIsPart(def) && parent && parent->children == CM_Parts) {
        fcListAdd(c, errors, fmDiag(c, fmLineOf(node), fcFmt(c, "'%s' can only contain %s, not '%s'.", parent->tag, fmJoinTags(c, parent->partTags, ", "), def->tag)));
        return;
    }

    element = fmElement(c, def->tag, fmLineOf(node));
    attrs = fmXAttrs(c, node);
    for (i = 0; i < attrs->count; i++) {
        const FcXAttr* attribute = (const FcXAttr*)attrs->items[i];
        const char* name = attribute->local;
        int line = attribute->line;
        const FmEventDef* eventDef;
        const FmPropDef* property;
        if (fmIEq(name, "name")) {
            if (!fmIsIdentifier(attribute->value)) fcListAdd(c, errors, fmDiag(c, line, fcFmt(c, "'%s' is not a valid name.", attribute->value)));
            else element->name = attribute->value;
        } else if ((eventDef = fmEvent(def, name)) != NULL) {
            if (!fmIsIdentifier(attribute->value)) fcListAdd(c, errors, fmDiag(c, line, fcFmt(c, "'%s' is not a valid method name for '%s'.", attribute->value, eventDef->name)));
            else {
                FmHandler* h = (FmHandler*)fcAlloc(c, sizeof *h);
                h->event = eventDef->name;
                h->handler = attribute->value;
                h->line = line;
                fcListAdd(c, element->handlers, h);
            }
        } else if ((property = fmProperty(def, name)) != NULL) {
            FmValue* value = fmReadValue(c, scope, attribute->value, line, errors);
            if (value) fcListAdd(c, element->attributes, fmAttr(c, property->name, value, line));
        } else {
            const char* renamed = fmRenamedAttribute(name);
            if (renamed && fmProperty(def, renamed)) fcListAdd(c, errors, fmDiag(c, line, fcFmt(c, "'%s' is now called '%s'.", name, renamed)));
            else {
                FcBuf b = {0};
                fcBufStr(&b, "");
                for (k = 0; k < def->properties->count; k++) { fcBufStr(&b, ((const FmPropDef*)def->properties->items[k])->name); fcBufStr(&b, ", "); }
                for (k = 0; k < def->events->count; k++) { fcBufStr(&b, ((const FmEventDef*)def->events->items[k])->name); fcBufStr(&b, ", "); }
                fcBufStr(&b, "Name");
                fcListAdd(c, errors, fmDiag(c, line, fcFmt(c, "'%s' has no property or event '%s' (known: %s).", def->tag, name, fcBufDone(c, &b))));
            }
        }
    }

    /* a FlowPanel: its text and its elements in their order - every piece of text between the elements is a Run (spaces folded to one) */
    if (strcmp(def->tag, "FlowPanel") == 0) {
        for (i = 0; i < node->nodes->count; i++) {
            const FcXNode* part = (const FcXNode*)node->nodes->items[i];
            if (part->kind == FCX_TEXT || part->kind == FCX_CDATA) {
                const char* folded = fmFoldSpace(c, part->text);
                FmElement* run;
                if (folded[0] == 0 || (strcmp(folded, " ") == 0 && (part->index == 0 || part->index == node->nodes->count - 1))) continue;
                run = fmElement(c, "Run", fmLineOf(node));
                fcListAdd(c, run->attributes, fmAttr(c, "Text", fmValue(c, FM_LITERAL, folded), fmLineOf(node)));
                fcListAdd(c, element->children, run);
            } else if (part->kind == FCX_ELEMENT) fmReadElement(c, scope, part, element->children, errors, def);
        }
        fcListAdd(c, into, element);
        return;
    }

    /* the text between the tags is the text of the element: <Button>OK</Button> */
    {
        FcBuf b = {0};
        const char* content;
        fcBufStr(&b, "");
        for (i = 0; i < node->nodes->count; i++) {
            const FcXNode* part = (const FcXNode*)node->nodes->items[i];
            if (part->kind == FCX_TEXT || part->kind == FCX_CDATA) fcBufStr(&b, part->text);
        }
        content = fmTrim(c, fcBufDone(c, &b));
        if (content[0]) {
            if (!fmProperty(def, "Text")) fcListAdd(c, errors, fmDiag(c, fmLineOf(node), fcFmt(c, "'%s' cannot have text content.", def->tag)));
            else if (fmFind(element, "Text")) fcListAdd(c, errors, fmDiag(c, fmLineOf(node), "The text is given twice (attribute and content)."));
            else {
                FmValue* v = fmReadValue(c, scope, fmFoldSpace(c, content), fmLineOf(node), errors);
                fcListAdd(c, element->attributes, fmAttr(c, "Text", v ? v : fmValue(c, FM_LITERAL, content), fmLineOf(node)));
            }
        }
    }

    children = fmXElements(c, node);
    for (i = 0; i < children->count; i++) {
        const FcXNode* child = (const FcXNode*)children->items[i];
        if (def->children == CM_None) {
            fcListAdd(c, errors, fmDiag(c, fmLineOf(child), fcFmt(c, "'%s' cannot contain elements.", def->tag)));
            continue;
        }
        if (def->children == CM_Single && element->children->count >= 1) {
            fcListAdd(c, errors, fmDiag(c, fmLineOf(child), fcFmt(c, "'%s' contains only one element.", def->tag)));
            continue;
        }
        fmReadElement(c, scope, child, element->children, errors, def);
    }
    fcListAdd(c, into, element);
}

/* ---- the patterns ---- */

/* ObjectStart: ^\[\s*([A-Za-z_][A-Za-z0-9_]*:)?[A-Za-z_][A-Za-z0-9_.]*(\s|\]) */
static bool fmObjectStart(const char* text) {
    const unsigned char* p = (const unsigned char*)text;
    const unsigned char* q;
    int attempt;
    if (*p != '[') return false;
    p++;
    for (;;) { q = p; if (!*q || !fcIsWhiteSpace(fc8Next(&q))) break; p = q; }
    for (attempt = 0; attempt < 2; attempt++) {
        const char* s = (const char*)p;
        if (attempt == 0) {
            const char* e = fmMatchIdent(s);
            if (!e || *e != ':') continue;
            s = e + 1;
        }
        if (!fmAsciiLetter(*s)) continue;
        s++;
        while (fmAsciiAlnum(*s) || *s == '.') s++;
        if (*s == ']') return true;
        q = (const unsigned char*)s;
        if (*q && fcIsWhiteSpace(fc8Next(&q))) return true;
    }
    return false;
}
/* TypeNamePattern: ^([A-Za-z_][A-Za-z0-9_]*:)?[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$ */
static bool fmIsTypeName(const char* text) {
    const char* e = fmMatchIdent(text);
    if (e && *e == ':' && fmIsDottedName(e + 1)) return true;
    return fmIsDottedName(text);
}
/* NumberArgument: ^-?(\d+(\.\d+)?|\.\d+)([eE][+-]?\d+)?[A-Za-z]*$ (\d: the digits of Unicode) */
static const unsigned char* fmDigits(const unsigned char* p) {
    const unsigned char* q = p;
    const unsigned char* last = NULL;
    while (*q) { const unsigned char* at = q; if (!fcIsDigit(fc8Next(&q))) { q = at; break; } last = q; }
    return last;
}
static bool fmNumberArgument(const char* text) {
    const unsigned char* p = (const unsigned char*)text;
    const unsigned char* d;
    const unsigned char* rest;
    int attempt;
    if (*p == '-') p++;
    if (*p == '.') { d = fmDigits(p + 1); if (!d) return false; p = d; }
    else {
        d = fmDigits(p);
        if (!d) return false;
        p = d;
        if (*p == '.' && (d = fmDigits(p + 1)) != NULL) p = d;
    }
    for (attempt = 0; attempt < 2; attempt++) {
        rest = p;
        if (attempt == 0) {
            const unsigned char* e = rest;
            if (*e != 'e' && *e != 'E') continue;
            e++;
            if (*e == '+' || *e == '-') e++;
            d = fmDigits(e);
            if (!d) continue;
            rest = d;
        }
        while (fmAsciiLetter((char)*rest) && *rest != '_') rest++;
        if (fmRegexEnd((const char*)rest)) return true;
    }
    return false;
}

/* IsLibraryClass */
static bool fmIsLibraryClass(const char* name) {
    return fcStartsWith(name, "UI.") || strcmp(name, "SolidBrush") == 0 || strcmp(name, "Pen") == 0 || strcmp(name, "Font") == 0 || strcmp(name, "Fonts") == 0;
}

static const char* fmUiQuote(FcCtx* c, const char* text);
static bool fmTryParseColor(const char* text, int* r, int* g, int* b);

/* SplitArguments */
static FcList* fmSplitArguments(FcCtx* c, const char* text, int line, FcList* errors) {
    FcList* result = fcListNew(c);
    FcBuf current = {0};
    int depth = 0;
    char quote = 0;
    size_t i, n = strlen(text);
    if (fmTrim(c, text)[0] == 0) return result;
    fcBufStr(&current, "");
    for (i = 0; i < n; i++) {
        char ch = text[i];
        if (quote) {
            fcBufChar(&current, ch);
            if (ch == quote) {
                if (i + 1 < n && text[i + 1] == quote) { fcBufChar(&current, text[++i]); continue; }
                quote = 0;
            }
            continue;
        }
        if (ch == '\'' || ch == '"') quote = ch;
        else if (ch == '[' || ch == '{') depth++;
        else if (ch == ']' || ch == '}') depth--;
        else if (ch == ',' && depth == 0) { fcListAdd(c, result, (void*)fcStrN(c, current.p, current.len)); current.len = 0; current.p[0] = 0; continue; }
        fcBufChar(&current, ch);
    }
    if (quote || depth != 0) {
        fcBufFree(&current);
        fcListAdd(c, errors, fmDiag(c, line, quote ? "A text in an object value is not closed (a quote is missing)." : "The brackets of an object value do not match."));
        return NULL;
    }
    fcListAdd(c, result, (void*)fcBufDone(c, &current));
    return result;
}

/* ReadObject: `[Type Arg1, Arg2]` */
static FmValue* fmReadObject(FcCtx* c, FmScope* scope, const char* text, int line, FcList* errors) {
    const char* inner;
    const unsigned char* p;
    const char* type;
    const char* qualified;
    FcList* arguments;
    FcBuf codes = {0};
    bool safe;
    int i;
    FmValue* v;
    inner = fmTrim(c, fcStrN(c, text + 1, strlen(text) - 2));
    p = (const unsigned char*)inner;
    for (;;) { const unsigned char* q = p; if (!*q || fcIsWhiteSpace(fc8Next(&q))) break; p = q; }
    type = fcStrN(c, inner, (size_t)((const char*)p - inner));
    if (!fmIsTypeName(type)) {
        fcListAdd(c, errors, fmDiag(c, line, fcFmt(c, "'[%s ...]' needs a class name, not '%s'.", type, type)));
        return NULL;
    }
    qualified = fmQualifyName(c, scope, type, line, errors);
    if (!qualified) return NULL;
    arguments = fmSplitArguments(c, (const char*)p, line, errors);
    if (!arguments) return NULL;
    safe = fmIsLibraryClass(qualified);
    fcBufStr(&codes, "");
    for (i = 0; i < arguments->count; i++) {
        const char* a = fmTrim(c, (const char*)arguments->items[i]);
        size_t n = strlen(a);
        int r, g, b;
        if (i) fcBufStr(&codes, ", ");
        if (n == 0) {
            fcBufFree(&codes);
            fcListAdd(c, errors, fmDiag(c, line, fcFmt(c, "An argument of '[%s ...]' is empty.", type)));
            return NULL;
        }
        if (n >= 2 && (a[0] == '\'' || a[0] == '"') && a[n - 1] == a[0]) {
            char q[2] = { a[0], 0 }, qq[3] = { a[0], a[0], 0 };
            fcBufStr(&codes, fmUiQuote(c, fmReplace(c, fcStrN(c, a + 1, n - 2), qq, q)));
        }
        else if (fmNumberArgument(a) || strcmp(a, "true") == 0 || strcmp(a, "false") == 0 || strcmp(a, "undefined") == 0) fcBufStr(&codes, a);
        else if (a[0] == '#' && fmTryParseColor(a, &r, &g, &b)) fcBufFmt(&codes, "UI.Color.Rgb(%d, %d, %d)", r, g, b);
        else if (a[0] == '[' || a[0] == '{') {
            FmValue* value = fmReadValue(c, scope, a, line, errors);
            if (!value) { fcBufFree(&codes); return NULL; }
            if (value->kind == FM_OBJECT) { fcBufStr(&codes, value->text); safe = safe && value->previewSafe; }
            else if (value->kind == FM_EXPRESSION) { fcBufStr(&codes, value->text); safe = false; }
            else {
                fcBufFree(&codes);
                fcListAdd(c, errors, fmDiag(c, line, fcFmt(c, "'%s' cannot be an argument of '[%s ...]' (a binding is no value).", a, type)));
                return NULL;
            }
        } else {
            fcBufFree(&codes);
            fcListAdd(c, errors, fmDiag(c, line, fcFmt(c, "'%s' is no argument value: write a text in quotes ('%s'), a number, true/false, #RRGGBB, [Type ...] or {Static Type.Member}.", a, a)));
            return NULL;
        }
    }
    v = fmValue(c, FM_OBJECT, fcFmt(c, "new %s(%s)", qualified, fcBufDone(c, &codes)));
    v->typeName = qualified;
    v->previewSafe = safe;
    return v;
}

/* Enum.TryParse(value, true, out BindingMode) && Enum.IsDefined */
static bool fmParseBindingMode(FcCtx* c, const char* value, FmBindingMode* mode) {
    const char* t = fmTrim(c, value);
    int n, i;
    if (!t[0]) return false;
    if ((t[0] >= '0' && t[0] <= '9') || t[0] == '-' || t[0] == '+') {
        if (!fmTryParseInt(t, &n)) return false;
        if (n < 0 || n > 2) return false;
        *mode = (FmBindingMode)n;
        return true;
    }
    for (i = 0; i < 3; i++) if (fmIEq(t, fmBindingModeNames[i])) { *mode = (FmBindingMode)i; return true; }
    return false;
}

/* ReadBinding */
static FmValue* fmReadBinding(FcCtx* c, const char* arguments, int line, FcList* errors) {
    const char* path = NULL;
    const char* converter = NULL;
    const char* elementName = NULL;
    FmBindingMode mode = FM_ONEWAY;
    bool ok = true;
    int position = 0;
    const char* p = arguments;
    FmValue* v;
    for (;;) {
        const char* comma = strchr(p, ',');
        const char* part = fmTrim(c, comma ? fcStrN(c, p, (size_t)(comma - p)) : p);
        if (part[0]) {
            const char* equals = strchr(part, '=');
            const char* key = !equals ? (position == 0 ? "Path" : "") : fmTrim(c, fcStrN(c, part, (size_t)(equals - part)));
            const char* value = !equals ? part : fmTrim(c, equals + 1);
            const char* lower = fmToLower(c, key);
            position++;
            if (strcmp(lower, "path") == 0) path = value;
            else if (strcmp(lower, "converter") == 0) converter = value;
            else if (strcmp(lower, "elementname") == 0) elementName = value;
            else if (strcmp(lower, "mode") == 0) {
                if (!fmParseBindingMode(c, value, &mode)) {
                    fcListAdd(c, errors, fmDiag(c, line, fcFmt(c, "Unknown binding mode '%s' (known: OneWay, TwoWay, OneTime).", value)));
                    ok = false;
                }
            } else {
                fcListAdd(c, errors, fmDiag(c, line, fcFmt(c, "Unknown binding option '%s' (known: Path, Mode, Converter, ElementName).", key[0] == 0 ? part : key)));
                ok = false;
            }
        }
        if (!comma) break;
        p = comma + 1;
    }
    /* no path (or `.`): the item itself, which only a DataTemplate can bind to (checked in Validate) */
    if (!path || !path[0] || strcmp(path, ".") == 0) path = "";
    else if (!fmIsDottedName(path)) {
        fcListAdd(c, errors, fmDiag(c, line, fcFmt(c, "A binding needs a path like Name or Player.Hp, not '%s'.", path)));
        return NULL;
    }
    if (!ok) return NULL;
    v = fmValue(c, FM_BINDING, NULL);
    v->path = path;
    v->mode = mode;
    v->converter = converter;
    v->elementName = elementName;
    return v;
}

/* ReadValue: a plain value or a markup extension in braces */
static FmValue* fmReadValue(FcCtx* c, FmScope* scope, const char* raw, int line, FcList* errors) {
    const char* text = fmTrim(c, raw);
    size_t n = strlen(text);
    const char* inner;
    const char* keyword;
    const char* rest;
    const char* space;
    const char* lower;
    if (fcStartsWith(raw, "{}")) return fmValue(c, FM_LITERAL, raw + 2);
    if (fmObjectStart(text) && n > 0 && text[n - 1] == ']') return fmReadObject(c, scope, text, line, errors);
    if (!(n > 0 && text[0] == '{' && text[n - 1] == '}')) return fmValue(c, FM_LITERAL, raw);
    inner = fmTrim(c, n >= 2 ? fcStrN(c, text + 1, n - 2) : "");
    space = inner;
    while (*space && *space != ' ' && *space != '\t') space++;
    keyword = *space ? fcStrN(c, inner, (size_t)(space - inner)) : inner;
    rest = *space ? fmTrim(c, space + 1) : "";
    lower = fmToLower(c, keyword);
    if (strcmp(lower, "enum") == 0 || strcmp(lower, "static") == 0) {
        const char* member;
        if (!fmIsTypeName(rest) || !strchr(rest, '.')) {
            fcListAdd(c, errors, fmDiag(c, line, fcFmt(c, "'{%s ...}' needs a name like Type.Member, not '%s'.", keyword, rest)));
            return NULL;
        }
        member = fmQualifyName(c, scope, rest, line, errors);
        return member ? fmValue(c, FM_EXPRESSION, member) : NULL;
    }
    if (strcmp(lower, "expr") == 0) {
        FmValue* v;
        if (!rest[0]) {
            fcListAdd(c, errors, fmDiag(c, line, "'{Expr ...}' needs an expression."));
            return NULL;
        }
        v = fmValue(c, FM_EXPRESSION, rest);
        v->isRaw = true;
        return v;
    }
    if (strcmp(lower, "binding") == 0) return fmReadBinding(c, rest, line, errors);
    if (strcmp(lower, "templatebinding") == 0) {
        if (!fmIsIdentifier(rest)) {
            fcListAdd(c, errors, fmDiag(c, line, fcFmt(c, "'{TemplateBinding ...}' needs a property name, not '%s'.", rest)));
            return NULL;
        }
        return fmValue(c, FM_TEMPLATEBINDING, rest);
    }
    fcListAdd(c, errors, fmDiag(c, line, fcFmt(c, "Unknown markup extension '{%s ...}' (known: Binding, TemplateBinding, Enum, Static, Expr; use '{}' in front of a text that starts with a brace).", keyword)));
    return NULL;
}

/* Parse */
static FmDocument* fmParse(FcCtx* c, const uint16_t* source, int length) {
    FcList* errors = fcListNew(c);
    FmScope* scope = (FmScope*)fcAlloc(c, sizeof *scope);
    const uint16_t* text;
    int n, i;
    FcXmlError xerr;
    FcXNode* root;
    FmRootKind kind;
    const char* className = NULL;
    const char* baseName = NULL;
    const char* title = NULL;
    int width = 640, height = 480;
    FcList* rootAttributes = fcListNew(c);
    FcList* attrs;
    FcList* children;
    FmDocument* document;
    scope->boundPrefixes = fcListNew(c);
    scope->boundValues = fcListNew(c);
    fmDeclareNamespacePrefix(c, source, length, &text, &n);
    root = fcXmlParse(c, text, n, &xerr);
    if (!root) {
        FmDocument* broken = fmDocument(c);
        broken->kind = FM_WINDOW;
        fcListAdd(c, broken->diagnostics, fmDiag(c, xerr.line, xerr.message));
        return broken;
    }
    kind = fmIEq(root->local, "View") ? FM_VIEW : FM_WINDOW;
    if (!fmIEq(root->local, "Window") && kind != FM_VIEW)
        fcListAdd(c, errors, fmDiag(c, fmLineOf(root), fcFmt(c, "The root element must be 'Window' or 'View', not '%s'.", root->local)));

    attrs = fmXAttrs(c, root);
    for (i = 0; i < attrs->count; i++) {
        const FcXAttr* attribute = (const FcXAttr*)attrs->items[i];
        const char* name = attribute->local;
        int line = attribute->line;
        const char* lower;
        const FmPropDef* rootProperty;
        if (strcmp(attribute->ns, FM_NAMESPACE_PREFIX_URI) == 0) {
            /* namespace:prefix="Name.Space": binds another namespace */
            const char* bound = fmTrim(c, attribute->value);
            if (strcmp(name, "local") == 0) fcListAdd(c, errors, fmDiag(c, line, "The prefix 'local' is the namespace of the markup itself (namespace=\"...\"); bind another one under another prefix."));
            else if (!fmIsDottedName(bound)) fcListAdd(c, errors, fmDiag(c, line, fcFmt(c, "'%s' is not a valid namespace name.", bound)));
            else fmScopeBind(c, scope, name, bound);
            continue;
        }
        if (attribute->ns[0]) {
            fcListAdd(c, errors, fmDiag(c, line, fcFmt(c, "Unknown attribute '%s' of '%s' in the XML namespace '%s'.", attribute->local, root->local, attribute->ns)));
            continue;
        }
        lower = fmToLower(c, name);
        if (strcmp(lower, "namespace") == 0) {
            if (!fmIsDottedName(fmTrim(c, attribute->value))) fcListAdd(c, errors, fmDiag(c, line, fcFmt(c, "'%s' is not a valid namespace name.", attribute->value)));
            else scope->own = fmTrim(c, attribute->value);
        } else if (strcmp(lower, "class") == 0) className = attribute->value;
        else if (strcmp(lower, "base") == 0) baseName = attribute->value;
        else if (strcmp(lower, "title") == 0 && kind == FM_WINDOW) title = attribute->value;
        else if (strcmp(lower, "width") == 0) width = fmReadSize(c, attribute, width, errors);
        else if (strcmp(lower, "height") == 0) height = fmReadSize(c, attribute, height, errors);
        else if (kind == FM_WINDOW && (rootProperty = fmRootProperty(name)) != NULL) {
            /* UIRoot.WindowBackdrop="Acrylic": a property of the UI root of the window */
            FmValue* rootValue = fmReadValue(c, scope, attribute->value, line, errors);
            if (rootValue && (rootValue->kind == FM_BINDING || rootValue->kind == FM_TEMPLATEBINDING))
                fcListAdd(c, errors, fmDiag(c, line, fcFmt(c, "'%s' cannot be bound.", rootProperty->name)));
            else if (rootValue) fcListAdd(c, rootAttributes, fmAttr(c, rootProperty->name, rootValue, line));
        } else {
            FcBuf b = {0};
            size_t k;
            fcBufStr(&b, "");
            if (kind == FM_WINDOW) {
                fcBufStr(&b, ", ");
                for (k = 0; k < sizeof fmRootProperties / sizeof fmRootProperties[0]; k++) { if (k) fcBufStr(&b, ", "); fcBufStr(&b, fmRootProperties[k].name); }
            }
            fcListAdd(c, errors, fmDiag(c, line, fcFmt(c, "Unknown attribute '%s' of '%s' (known: Class, Base, namespace, namespace:prefix, %sWidth, Height%s).",
                name, root->local, kind == FM_WINDOW ? "Title, " : "", fcBufDone(c, &b))));
        }
    }

    if (fmIsBlank(className)) {
        fcListAdd(c, errors, fmDiag(c, fmLineOf(root), fcFmt(c, "'%s' needs the attribute Class=\"...\": the name of the class of the code-behind.", root->local)));
        className = "Ui";
    } else if (!fmIsIdentifier(className))
        fcListAdd(c, errors, fmDiag(c, fmLineOf(root), fcFmt(c, "'%s' is not a valid class name.", className)));

    if (fmIsBlank(baseName)) baseName = fcFmt(c, "%sBase", className);
    else if (!fmIsIdentifier(baseName))
        fcListAdd(c, errors, fmDiag(c, fmLineOf(root), fcFmt(c, "'%s' is not a valid class name.", baseName)));

    document = fmDocument(c);
    document->kind = kind;
    document->className = className;
    document->baseName = baseName;
    document->title = title ? title : className;
    document->width = width;
    document->height = height;
    document->ns = scope->own;
    for (i = 0; i < scope->boundPrefixes->count; i++) { fcListAdd(c, document->nsPrefixes, scope->boundPrefixes->items[i]); fcListAdd(c, document->nsValues, scope->boundValues->items[i]); }
    for (i = 0; i < rootAttributes->count; i++) fcListAdd(c, document->rootAttributes, rootAttributes->items[i]);

    children = fmXElements(c, root);
    for (i = 0; i < children->count; i++) {
        const FcXNode* child = (const FcXNode*)children->items[i];
        if (fmIEq(child->local, "Resources")) fmReadResources(c, scope, child, document, errors);
        else fmReadElement(c, scope, child, document->children, errors, NULL);
    }

    for (i = 0; i < errors->count; i++) fcListAdd(c, document->diagnostics, errors->items[i]);
    fmValidate(c, document);
    return document;
}

/* ---- ValidateResources and Validate ---- */

static bool fmHasConverter(const FmDocument* d, const char* key) {
    int i;
    for (i = 0; i < d->converters->count; i++) if (strcmp(((FmConverter*)d->converters->items[i])->key, key) == 0) return true;
    return false;
}
static void fmCheckConverter(FcCtx* c, FmDocument* document, const FmValue* binding, int line) {
    if (binding->converter && !fmHasConverter(document, binding->converter) && !fmBuiltInConverter(binding->converter))
        fcListAdd(c, document->diagnostics, fmDiag(c, line,
            fcFmt(c, "Unknown converter '%s' (declare it in <Resources>, or use one of: %s).", binding->converter, fmBuiltInConverterKeys)));
}
static FmStyle* fmStyleByKey(const FmDocument* d, const char* key) {
    int i;
    for (i = 0; i < d->styles->count; i++) { FmStyle* s = (FmStyle*)d->styles->items[i]; if (s->key && key && strcmp(s->key, key) == 0) return s; }
    return NULL;
}
static void fmCheckReference(FcCtx* c, FmDocument* document, const FmPropDef* property, const char* text, int line) {
    const char* key = fmTrim(c, text);
    int i;
    bool found;
    if (!property) return;
    if (property->kind == PK_StyleRef && !fmStyleByKey(document, key))
        fcListAdd(c, document->diagnostics, fmDiag(c, line, fcFmt(c, "Unknown style '%s' (give a <Style Key=\"...\"> in the Resources).", key)));
    if (property->kind == PK_TemplateRef) {
        found = false;
        for (i = 0; i < document->templates->count; i++) if (strcmp(((FmTemplate*)document->templates->items[i])->key, key) == 0) found = true;
        if (!found) fcListAdd(c, document->diagnostics, fmDiag(c, line, fcFmt(c, "Unknown template '%s' (give a <ControlTemplate Key=\"...\"> in the Resources).", key)));
    }
    if (property->kind == PK_DataTemplateRef) {
        found = false;
        for (i = 0; i < document->dataTemplates->count; i++) if (strcmp(((FmDataTemplate*)document->dataTemplates->items[i])->key, key) == 0) found = true;
        if (!found) fcListAdd(c, document->diagnostics, fmDiag(c, line, fcFmt(c, "Unknown data template '%s' (give a <DataTemplate Key=\"...\"> in the Resources).", key)));
    }
    if (property->kind == PK_ViewRef) {
        found = false;
        for (i = 0; i < document->views->count; i++) if (strcmp(((FmView*)document->views->items[i])->key, key) == 0) found = true;
        if (!found) fcListAdd(c, document->diagnostics, fmDiag(c, line, fcFmt(c, "Unknown view '%s' (give a <CollectionView Key=\"...\"> in the Resources).", key)));
    }
}
static void fmCheckValueKind(FcCtx* c, FmDocument* document, const FmPropDef* property, const FmValue* value, int line) {
    if (!property) return;
    if (property->kind == PK_Collection && value->kind == FM_LITERAL)
        fcListAdd(c, document->diagnostics, fmDiag(c, line, fcFmt(c, "'%s' is a list: give {Binding ...} or {Expr ...}.", property->name)));
    if (property->kind == PK_Code && value->kind != FM_EXPRESSION && value->kind != FM_OBJECT)
        fcListAdd(c, document->diagnostics, fmDiag(c, line, fcFmt(c, "'%s' is code: give {Expr ...} or [Type ...].", property->name)));
}

/* new Stack<MarkupElement>(new[] { root }) popped, children pushed: the order of that walk */
static FcList* fmStackWalk(FcCtx* c, FmElement* root) {
    FcList* stack = fcListNew(c);
    FcList* out = fcListNew(c);
    int i;
    fcListAdd(c, stack, root);
    while (stack->count > 0) {
        FmElement* e = (FmElement*)stack->items[--stack->count];
        fcListAdd(c, out, e);
        for (i = 0; i < e->children->count; i++) fcListAdd(c, stack, e->children->items[i]);
    }
    return out;
}

static void fmValidateResources(FcCtx* c, FmDocument* document) {
    FcList* d = document->diagnostics;
    FcList* keys = fcListNew(c);
    FcList* implicitTargets = fcListNew(c);
    FcList* all;
    int i, k, m;
    for (i = 0; i < document->views->count; i++) {
        FmView* view = (FmView*)document->views->items[i];
        for (k = 0; k < view->element->attributes->count; k++) {
            FmAttr* attribute = (FmAttr*)view->element->attributes->items[k];
            if (attribute->value->kind != FM_BINDING) continue;
            if (attribute->value->path[0] == 0) fcListAdd(c, d, fmDiag(c, attribute->line, "A binding needs a path like Name or Player.Hp."));
            fmCheckConverter(c, document, attribute->value, attribute->line);
        }
    }
    {
        /* the keys of styles, templates, data templates and views share one namespace */
        struct { FcList* list; int kind; } groups[4] = { { document->styles, 0 }, { document->templates, 1 }, { document->dataTemplates, 2 }, { document->views, 3 } };
        int g;
        for (g = 0; g < 4; g++)
            for (i = 0; i < groups[g].list->count; i++) {
                void* x = groups[g].list->items[i];
                const char* key = g == 0 ? ((FmStyle*)x)->key : g == 1 ? ((FmTemplate*)x)->key : g == 2 ? ((FmDataTemplate*)x)->key : ((FmView*)x)->key;
                int line = g == 0 ? ((FmStyle*)x)->line : g == 1 ? ((FmTemplate*)x)->line : g == 2 ? ((FmDataTemplate*)x)->line : ((FmView*)x)->line;
                if (!key) continue;
                if (fcListHasStr(keys, key)) fcListAdd(c, d, fmDiag(c, line, fcFmt(c, "The key '%s' of a style or template is used twice.", key)));
                else fcListAdd(c, keys, (void*)key);
            }
    }
    for (i = 0; i < document->styles->count; i++) {
        FmStyle* style = (FmStyle*)document->styles->items[i];
        if (!style->key) {
            if (fcListHasStr(implicitTargets, style->target)) fcListAdd(c, d, fmDiag(c, style->line, fcFmt(c, "There are two styles without a key for '%s'.", style->target)));
            else fcListAdd(c, implicitTargets, (void*)style->target);
        }
        if (style->basedOn) {
            FmStyle* based = fmStyleByKey(document, style->basedOn);
            if (!based) fcListAdd(c, d, fmDiag(c, style->line, fcFmt(c, "The style 'BasedOn=\"%s\"' does not exist (it needs a key).", style->basedOn)));
            else {
                /* a cycle of basedOn */
                FcList* seen = fcListNew(c);
                FmStyle* next;
                fcListAdd(c, seen, style);
                for (next = based; next; next = next->basedOn ? fmStyleByKey(document, next->basedOn) : NULL) {
                    bool dup = false;
                    for (k = 0; k < seen->count; k++) if (seen->items[k] == next) dup = true;
                    if (dup) { fcListAdd(c, d, fmDiag(c, style->line, fcFmt(c, "The styles '%s' and '%s' are based on each other.", style->key ? style->key : "", style->basedOn))); break; }
                    fcListAdd(c, seen, next);
                }
            }
        }
    }

    /* references from the attributes */
    all = fmAllElements(c, document);
    {
        FcList* t = fmTemplateElements(c, document);
        FcList* dt = fmDataTemplateElements(c, document);
        for (i = 0; i < t->count; i++) fcListAdd(c, all, t->items[i]);
        for (i = 0; i < dt->count; i++) fcListAdd(c, all, dt->items[i]);
    }
    for (i = 0; i < all->count; i++) {
        FmElement* element = (FmElement*)all->items[i];
        const FmElementDef* def = fmFindDef(c, element->tag);
        if (!def) continue;
        for (k = 0; k < element->attributes->count; k++) {
            FmAttr* attribute = (FmAttr*)element->attributes->items[k];
            const FmPropDef* property = fmProperty(def, attribute->name);
            if (attribute->value->kind == FM_LITERAL) fmCheckReference(c, document, property, attribute->value->text, attribute->line);
            fmCheckValueKind(c, document, property, attribute->value, attribute->line);
        }
        if (fmFind(element, "view") && fmFind(element, "itemsSource"))
            fcListAdd(c, d, fmDiag(c, element->line, fcFmt(c, "'%s' shows a 'View' or an 'ItemsSource', not both.", element->tag)));
    }
    for (i = 0; i < document->views->count; i++) {
        FmView* view = (FmView*)document->views->items[i];
        for (k = 0; k < view->element->attributes->count; k++) {
            FmAttr* attribute = (FmAttr*)view->element->attributes->items[k];
            const FmPropDef* property = fmProperty(fmSchema(c)->collectionView, attribute->name);
            fmCheckValueKind(c, document, property, attribute->value, attribute->line);
            if (attribute->value->kind == FM_BINDING && attribute->value->mode == FM_TWOWAY)
                fcListAdd(c, d, fmDiag(c, attribute->line, "The source of a 'CollectionView' is read only (Mode=OneWay or OneTime)."));
            if (fmIEq(attribute->name, "SortBy") && attribute->value->kind == FM_LITERAL && !fmIsIdentifier(fmTrim(c, attribute->value->text)))
                fcListAdd(c, d, fmDiag(c, attribute->line, fcFmt(c, "'SortBy' needs the name of a property, not '%s'.", attribute->value->text)));
            if (fmIEq(attribute->name, "Descending") && attribute->value->kind != FM_LITERAL)
                fcListAdd(c, d, fmDiag(c, attribute->line, "'Descending' needs true or false."));
        }
    }
    for (i = 0; i < document->styles->count; i++) {
        FmStyle* style = (FmStyle*)document->styles->items[i];
        FcList* setters = fcListCopy(c, style->setters);
        for (k = 0; k < style->triggers->count; k++) {
            FmTrigger* t = (FmTrigger*)style->triggers->items[k];
            for (m = 0; m < t->setters->count; m++) fcListAdd(c, setters, t->setters->items[m]);
        }
        for (k = 0; k < setters->count; k++) {
            FmSetter* setter = (FmSetter*)setters->items[k];
            const FmElementDef* def = fmFindTarget(c, style->target);
            const FmPropDef* property = def ? fmProperty(def, setter->property) : NULL;
            if (setter->value->kind == FM_LITERAL) fmCheckReference(c, document, property, setter->value->text, setter->line);
            fmCheckValueKind(c, document, property, setter->value, setter->line);
        }
    }

    /* inside a template */
    for (i = 0; i < document->templates->count; i++) {
        FmTemplate* template = (FmTemplate*)document->templates->items[i];
        const FmElementDef* target;
        FcList* partNames;
        FcList* partElements;
        FcList* walk;
        if (!template->root) continue;
        target = fmFindTarget(c, template->target);
        partNames = fcListNew(c);
        partElements = fcListNew(c);
        walk = fmStackWalk(c, template->root);
        for (k = 0; k < walk->count; k++) {
            FmElement* e = (FmElement*)walk->items[k];
            if (e->name) {
                if (fcListHasStr(partNames, e->name)) fcListAdd(c, d, fmDiag(c, e->line, fcFmt(c, "The part name '%s' is used twice in the template '%s'.", e->name, template->key)));
                else { fcListAdd(c, partNames, (void*)e->name); fcListAdd(c, partElements, e); }
            }
            if (e->handlers->count > 0) fcListAdd(c, d, fmDiag(c, e->line, "Event handlers are not available inside a ControlTemplate (react to the control itself)."));
            for (m = 0; m < e->attributes->count; m++) {
                FmAttr* attribute = (FmAttr*)e->attributes->items[m];
                if (attribute->value->kind == FM_BINDING)
                    fcListAdd(c, d, fmDiag(c, attribute->line, "'{Binding ...}' is not available inside a ControlTemplate; use '{TemplateBinding Property}'."));
                if (attribute->value->kind == FM_TEMPLATEBINDING && !fmProperty(target, attribute->value->text) && !fmStateProperty(attribute->value->text))
                    fcListAdd(c, d, fmDiag(c, attribute->line, fcFmt(c, "'%s' has no property '%s' to bind to.", template->target, attribute->value->text)));
            }
        }
        for (k = 0; k < template->triggers->count; k++) {
            FmTrigger* trigger = (FmTrigger*)template->triggers->items[k];
            for (m = 0; m < trigger->conditions->count; m++) {
                FmCondition* cond = (FmCondition*)trigger->conditions->items[m];
                if (cond->source && !fcListHasStr(partNames, cond->source))
                    fcListAdd(c, d, fmDiag(c, cond->line, fcFmt(c, "The template '%s' has no part '%s'.", template->key, cond->source)));
            }
            for (m = 0; m < trigger->setters->count; m++) {
                FmSetter* setter = (FmSetter*)trigger->setters->items[m];
                if (!setter->target) {
                    if (!fmProperty(target, setter->property))
                        fcListAdd(c, d, fmDiag(c, setter->line, fcFmt(c, "'%s' has no property '%s'.", template->target, setter->property)));
                } else {
                    int at = -1, q;
                    for (q = 0; q < partNames->count; q++) if (strcmp((const char*)partNames->items[q], setter->target) == 0) at = q;
                    if (at < 0) fcListAdd(c, d, fmDiag(c, setter->line, fcFmt(c, "The template '%s' has no part '%s'.", template->key, setter->target)));
                    else {
                        FmElement* part = (FmElement*)partElements->items[at];
                        const FmElementDef* pd = fmFindDef(c, part->tag);
                        if (!pd || !fmProperty(pd, setter->property))
                            fcListAdd(c, d, fmDiag(c, setter->line, fcFmt(c, "'%s' has no property '%s'.", part->tag, setter->property)));
                    }
                }
            }
        }
    }
    /* inside a data template: bindings go to the item; nothing that needs a name or a handler */
    for (i = 0; i < document->dataTemplates->count; i++) {
        FmDataTemplate* template = (FmDataTemplate*)document->dataTemplates->items[i];
        FcList* walk;
        if (!template->root) continue;
        walk = fmStackWalk(c, template->root);
        for (k = 0; k < walk->count; k++) {
            FmElement* e = (FmElement*)walk->items[k];
            if (e->name) fcListAdd(c, d, fmDiag(c, e->line, "Elements inside a DataTemplate have no name (there is one set of elements per item)."));
            if (e->handlers->count > 0) fcListAdd(c, d, fmDiag(c, e->line, "Event handlers are not available inside a DataTemplate (the rows only show data)."));
            for (m = 0; m < e->attributes->count; m++) {
                FmAttr* attribute = (FmAttr*)e->attributes->items[m];
                const FmValue* binding = attribute->value;
                if (binding->kind == FM_TEMPLATEBINDING)
                    fcListAdd(c, d, fmDiag(c, attribute->line, "'{TemplateBinding ...}' is only for the elements inside a ControlTemplate."));
                if (binding->kind != FM_BINDING) continue;
                if (binding->elementName)
                    fcListAdd(c, d, fmDiag(c, attribute->line, "'ElementName' is not available inside a DataTemplate: a binding goes to the item."));
                if (binding->path[0] == 0 && binding->mode == FM_TWOWAY)
                    fcListAdd(c, d, fmDiag(c, attribute->line, "A binding to the item itself is read only (Mode=OneWay or OneTime)."));
                fmCheckConverter(c, document, binding, attribute->line);
            }
        }
    }
    /* a trigger of a style cannot name parts */
    for (i = 0; i < document->styles->count; i++) {
        FmStyle* style = (FmStyle*)document->styles->items[i];
        for (k = 0; k < style->triggers->count; k++) {
            FmTrigger* trigger = (FmTrigger*)style->triggers->items[k];
            for (m = 0; m < trigger->setters->count; m++) {
                FmSetter* setter = (FmSetter*)trigger->setters->items[m];
                if (setter->target) fcListAdd(c, d, fmDiag(c, setter->line, "'Target' is only for the setters of a ControlTemplate."));
            }
        }
    }
}

/* Validate: unique names, converters and elements that bindings name */
static void fmValidate(FcCtx* c, FmDocument* document) {
    FcList* d = document->diagnostics;
    FcList* names = fcListNew(c);
    FcList* all = fmAllElements(c, document);
    int i, k;
    for (i = 0; i < all->count; i++) {
        FmElement* element = (FmElement*)all->items[i];
        if (!element->name) continue;
        if (fmIsReserved(element->name) || fcStartsWith(element->name, "fx"))
            fcListAdd(c, d, fmDiag(c, element->line, fcFmt(c, "The name '%s' is used by the generated class.", element->name)));
        else if (fcListHasStr(names, element->name)) fcListAdd(c, d, fmDiag(c, element->line, fcFmt(c, "The name '%s' is used twice.", element->name)));
        else fcListAdd(c, names, (void*)element->name);
    }
    for (i = 0; i < all->count; i++) {
        FmElement* element = (FmElement*)all->items[i];
        for (k = 0; k < element->handlers->count; k++) {
            FmHandler* h = (FmHandler*)element->handlers->items[k];
            if (fcListHasStr(names, h->handler) || fmIsReserved(h->handler))
                fcListAdd(c, d, fmDiag(c, h->line, fcFmt(c, "The method name '%s' is already used.", h->handler)));
        }
    }

    fmValidateResources(c, document);

    for (i = 0; i < document->views->count; i++) {
        FmView* view = (FmView*)document->views->items[i];
        for (k = 0; k < view->element->attributes->count; k++) {
            FmAttr* attribute = (FmAttr*)view->element->attributes->items[k];
            if (attribute->value->kind == FM_BINDING && attribute->value->elementName && !fcListHasStr(names, attribute->value->elementName))
                fcListAdd(c, d, fmDiag(c, attribute->line, fcFmt(c, "The binding names the element '%s', which does not exist (give it Name=\"...\").", attribute->value->elementName)));
        }
    }

    for (i = 0; i < all->count; i++) {
        FmElement* element = (FmElement*)all->items[i];
        for (k = 0; k < element->attributes->count; k++) {
            FmAttr* attribute = (FmAttr*)element->attributes->items[k];
            const FmValue* binding = attribute->value;
            if (binding->kind == FM_TEMPLATEBINDING)
                fcListAdd(c, d, fmDiag(c, attribute->line, "'{TemplateBinding ...}' is only for the elements inside a ControlTemplate."));
            if (binding->kind != FM_BINDING) continue;
            if (binding->path[0] == 0)
                fcListAdd(c, d, fmDiag(c, attribute->line, "A binding needs a path like Name or Player.Hp (a binding without a path is for the elements of a DataTemplate)."));
            if (binding->elementName && !fcListHasStr(names, binding->elementName))
                fcListAdd(c, d, fmDiag(c, attribute->line, fcFmt(c, "The binding names the element '%s', which does not exist (give it Name=\"...\").", binding->elementName)));
            if (binding->converter && !fmHasConverter(document, binding->converter) && !fmBuiltInConverter(binding->converter))
                fcListAdd(c, d, fmDiag(c, attribute->line,
                    fcFmt(c, "Unknown converter '%s' (declare it in <Resources>, or use one of: %s).", binding->converter, fmBuiltInConverterKeys)));
        }
    }
}
