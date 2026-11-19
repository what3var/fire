/* native/forge/serve_complete.c - the completion of `forge serve`: src/fire.Editor/Language/CompletionEngine.cs (commit 1c549476), method for method
 * (L3). The rules (the C# tests: src/fire.Testing/CompletionTests.cs, now over the protocol):
 *   - after `x.`: the type of `x` is derived (serve_types.c) and only its members offered - inherited ones too, without what the code at the cursor may
 *     not use (`private`: only in the declaring class, `protected`: also in its derived classes), at a class only the static members, at an object only
 *     the instance members (at `this.` both); an unknown type: the accessible instance members of the program's own classes (those of the libraries
 *     only when the program has none that fit what was typed, and only their API);
 *   - a name being typed: keywords, type keywords, the types reachable here without qualification, the top-level namespaces, the parameters and
 *     variables visible here, the members of the class around (in a static method only the static ones), named arguments `name: ` at the start of an
 *     argument;
 *   - the score (CompareKeywords): what starts with the typed text first, exactly the typed case before, the shorter the name the better; the base
 *     scores: parameters and variables 0.25, members of the class 0.2 (less with every base), the own types 0.1, keywords 0.05, the library types 0;
 *     a member of a library whose name starts small (`cw`, `templateRoot`) 0.1 lower - the API before the internals;
 *   - never: constructors, operators, `__` internals, the helpers of the markup generator (`fx...` in a generated class), the collective classes `$string`.
 * The list is deduplicated (text and kind, the best score), sorted by text ignoring case, then by score (best first, stable) - as spark shows it. */

typedef enum SvItemKind { CK_KEYWORD, CK_TYPEKEYWORD, CK_CLASS, CK_ENUM, CK_ENUMMEMBER, CK_NAMESPACE, CK_FIELD, CK_METHOD, CK_PROPERTY, CK_VARIABLE, CK_PARAMETER } SvItemKind;
static const char* const svItemKindNames[] = { "keyword", "typeKeyword", "class", "enum", "enumMember", "namespace", "field", "method", "property", "variable", "parameter" };

typedef struct SvItem { const char* text; SvItemKind kind; float score; const char* detail; const char* doc; int order; } SvItem;

static const char* const svKeywords[] = {
    "var", "func", "class", "construct", "destruct", "return",
    "if", "else", "while", "do", "for", "foreach", "in", "on", "new", "base", "this",
    "try", "catch", "finally", "throw", "is", "of", "from", "under",
    "extern", "unsafe", "interface", "readonly", "enum", "override",
    "public", "private", "protected",
    "namespace",
    "with", "extends", "switch", "case", "default", "break", "continue", "where",
    "fire", "await", "dedicated", "until", "taking", "handover", "sync", "flat", "copy", "take", "replace", "leave", "terminate", "actor", "process", "operator", "defined", "trust",
    "true", "false", "undefined", "and", "or",
    "unit", "prefixed", "unprefixed", "coerce",
};
static const char* const svTypeKeywords[] = { "bool", "int", "float", "char", "string", "byte", "word", "any" };

#define SV_OWN_TYPE_SCORE 0.1f

static float svCompareKeywords(const char* current, const char* candidate, float baseScore) {
    float matchOffset = 0.0f, exactCase;
    int cl, nl;
    if (!current || !candidate) return 0.0f;
    if (svStartsWithIC(candidate, current)) matchOffset = 0.35f;
    else if (!svContainsIC(candidate, current)) return 0.0f;
    cl = fc8Units(current);
    nl = fc8Units(candidate);
    exactCase = cl > 0 && fcStartsWith(candidate, current) ? 0.05f : 0.0f;
    return baseScore + matchOffset + exactCase + ((1.0f - matchOffset - baseScore) * (float)cl / (float)nl);
}
static bool svMatchesPrefix(const char* candidate, const char* prefix) { return !prefix[0] || svStartsWithIC(candidate, prefix); }

typedef struct SvCompletion { SvLang* S; SvIndex* x; FcCtx* t; FcList* items; const uint16_t* src; int len; } SvCompletion;

static void svAdd(SvCompletion* q, const char* text, SvItemKind kind, float score, const char* detail, const char* doc) {
    SvItem* it = (SvItem*)fcAlloc(q->t, sizeof *it);
    it->text = text; it->kind = kind; it->score = score; it->detail = detail; it->doc = doc;
    it->order = q->items->count;
    fcListAdd(q->t, q->items, it);
}

static bool svIsVisible(SvIndex* x, const SvMember* m, const char* fromClass) {
    switch (m->access) {
        case SA_PRIVATE: return fromClass && strcmp(fromClass, m->owner) == 0;
        case SA_PROTECTED: return fromClass && (strcmp(fromClass, m->owner) == 0 || svDerivesFrom(x, fromClass, m->owner));
        default: return true;
    }
}
static bool svIsGeneratorHelper(const char* name) {
    return strlen(name) > 2 && name[0] == 'f' && name[1] == 'x' && ((name[2] >= 'A' && name[2] <= 'Z') || name[2] == '_');
}
static bool svIsListable(SvIndex* x, const SvMember* m) {
    SvClass* owner;
    if (m->kind == SM_CONSTRUCTOR || fcStartsWith(m->name, "operator") || fcStartsWith(m->name, "__")) return false;
    return !(svIsGeneratorHelper(m->name) && (owner = svClassGet(x, m->owner)) != NULL && owner->isGenerated);
}
static bool svIsLower(const char* name) {
    /* char.IsLower of the first UTF-16 unit (ASCII and the Latin-1 small letters) */
    unsigned char ch = (unsigned char)name[0];
    return (ch >= 'a' && ch <= 'z') || (ch == 0xC3 && (unsigned char)name[1] >= 0x9F && (unsigned char)name[1] <= 0xBF && (unsigned char)name[1] != 0xB7);
}
static bool svIsLibraryInternal(SvIndex* x, const SvMember* m) {
    SvClass* owner;
    return m->name[0] && (svIsLower(m->name) || m->name[0] == '_') && (owner = svClassGet(x, m->owner)) != NULL && svClassFromPrelude(owner);
}
static float svMemberBaseScore(SvIndex* x, const SvMember* m, int depth) {
    float score = 0.3f - 0.05f * (float)depth;
    if (score < 0.1f) score = 0.1f;
    return svIsLibraryInternal(x, m) ? score - 0.1f : score;
}

static void svAddMemberItem(SvCompletion* q, const SvMember* m, const char* prefix, float baseScore, bool showOwner) {
    FcCtx* t = q->t;
    const char* type = m->typeName ? fcFmt(t, "%s%s", m->typeName, m->typeIsArray ? "[]" : "") : "";
    const char* owner = showOwner ? fcFmt(t, "  [%s]", m->owner) : "";
    const char* modifiers = fcFmt(t, "%s%s", m->isStatic ? "static " : "", m->access == SA_PRIVATE ? "private " : m->access == SA_PROTECTED ? "protected " : "");
    SvItemKind kind = m->kind == SM_METHOD ? CK_METHOD : m->kind == SM_PROPERTY ? CK_PROPERTY : CK_FIELD;
    const char* detail;
    if (m->kind == SM_METHOD) detail = fcFmt(t, "%s(%s)%s%s%s", modifiers, m->signature ? m->signature : "", type[0] ? " \xE2\x86\x92 " : "", type, owner);
    else if (m->kind == SM_PROPERTY) detail = fcFmt(t, "%sProperty%s%s%s", modifiers, type[0] ? " : " : "", type, owner);
    else detail = fcFmt(t, "%sField%s%s%s", modifiers, type[0] ? " : " : "", type, owner);
    svAdd(q, m->name, kind, svCompareKeywords(prefix, m->name, baseScore), detail, svDocAt(m->source, t, m->declLine));
}

/* Dedupe: per (text, kind) the best score (the first of equal ones), ordered by text ignoring case (stable) */
static int svCmpItemText(const void* a, const void* b) {
    const SvItem* p = *(const SvItem* const*)a;
    const SvItem* r = *(const SvItem* const*)b;
    int c = svCmpIC(p->text, r->text);
    return c ? c : p->order - r->order;
}
static int svCmpItemScore(const void* a, const void* b) {
    const SvItem* p = *(const SvItem* const*)a;
    const SvItem* r = *(const SvItem* const*)b;
    if (p->score != r->score) return p->score > r->score ? -1 : 1;
    return p->order - r->order;
}
static void svDedupe(SvCompletion* q) {
    FcDict best;
    FcList* out = fcListNew(q->t);
    int i;
    fcDictInit(q->t, &best, FC_KEY_STR);
    for (i = 0; i < q->items->count; i++) {
        SvItem* it = (SvItem*)q->items->items[i];
        const char* key = fcFmt(q->t, "%d\x01%s", (int)it->kind, it->text);
        void** slot = fcDictRef(&best, key);
        if (!slot) { fcDictAdd(&best, key, it); fcListAdd(q->t, out, it); }
        else if (((SvItem*)*slot)->score < it->score) {
            int k;
            for (k = 0; k < out->count; k++) if (out->items[k] == *slot) { it->order = ((SvItem*)*slot)->order; out->items[k] = it; break; }
            *slot = it;
        }
    }
    /* GroupBy keeps the order of the groups' first elements */
    for (i = 0; i < out->count; i++) ((SvItem*)out->items[i])->order = i;
    if (out->count > 1) qsort(out->items, (size_t)out->count, sizeof(void*), svCmpItemText);
    q->items = out;
}

static int svLineStart(const uint16_t* s, int offset) { int j = offset; while (j > 0 && s[j - 1] != '\n') j--; return j; }

/* IsAfterNew */
static bool svIsAfterNew(const uint16_t* s, int start) {
    int j = start, wordEnd;
    while (j >= 0 && (s[j] == ' ' || s[j] == '\t')) j--;
    if (j == start) return false;
    wordEnd = j;
    while (j >= 0 && svIsWord(s[j])) j--;
    return wordEnd - j == 3 && s[j + 1] == 'n' && s[j + 2] == 'e' && s[j + 3] == 'w';
}
static bool svIsQualifiedNameAfterNew(const uint16_t* s, int dotOffset) {
    int j = dotOffset - 1;
    while (j >= 0 && (svIsWord(s[j]) || s[j] == '.')) j--;
    return svIsAfterNew(s, j);
}

static void svAddVisibleTypes(SvCompletion* q, int offset, const char* prefix, bool includeInterfaces, bool includeEnums) {
    SvIndex* x = q->x;
    FcList* context = svContextAt(x, offset);
    FC_DICT_EACH(&x->classes, e) {
        SvClass* k = (SvClass*)e->value;
        const char* detail;
        if (k->name[0] == '$' || fcStartsWith(k->simpleName, "__")) continue;
        if ((k->isInterface && !includeInterfaces) || !svMatchesPrefix(k->simpleName, prefix)) continue;
        { const char* r = svResolveClassKey(x, k->simpleName, context, false); if (!r || strcmp(r, (const char*)e->key) != 0) continue; }
        detail = k->ns[0] ? fcFmt(q->t, "%s in %s", k->isInterface ? "Interface" : "Class", k->ns) : (k->isInterface ? "Interface" : "Class");
        svAdd(q, k->simpleName, CK_CLASS, svCompareKeywords(prefix, k->simpleName, svClassFromPrelude(k) ? 0.0f : SV_OWN_TYPE_SCORE), detail, svDocAt(k->source, q->t, k->declLine));
    }
    if (!includeEnums) return;
    FC_DICT_EACH(&x->enumMembers, e) {
        const char* key = (const char*)e->key;
        const char* simple = svAfterLastDot(key);
        const char* r;
        if (!svMatchesPrefix(simple, prefix) || !(r = svResolveEnumKey(x, simple, context)) || strcmp(r, key) != 0) continue;
        svAdd(q, simple, CK_ENUM, svCompareKeywords(prefix, simple, fcDictHas(&x->enumPreludes, key) ? 0.0f : SV_OWN_TYPE_SCORE),
            svHasChar(key, '.') ? fcFmt(q->t, "Enum in %s", fcStrN(q->t, key, (size_t)(simple - 1 - key))) : "Enum", NULL);
    }
}

static void svAddTopLevelNamespaces(SvCompletion* q, const char* prefix) {
    FcList* ms = svMembersOfNamespace(q->x, "");
    int i;
    for (i = 0; i < ms->count; i++) {
        SvNsMember* m = (SvNsMember*)ms->items[i];
        if (m->kind == SN_NAMESPACE && svMatchesPrefix(m->name, prefix)) svAdd(q, m->name, CK_NAMESPACE, svCompareKeywords(prefix, m->name, 0.0f), "Namespace", NULL);
    }
}

static void svAddFallbackMembers(SvCompletion* q, const char* prefix, const char* fromClass, bool fromLibraries) {
    SvIndex* x = q->x;
    int i;
    FC_DICT_EACH(&x->classes, e) {
        SvClass* k = (SvClass*)e->value;
        if (svClassFromPrelude(k) != fromLibraries || k->name[0] == '$' || fcStartsWith(k->simpleName, "__")) continue;
        for (i = 0; i < k->members->count; i++) {
            SvMember* m = (SvMember*)k->members->items[i];
            if (svIsListable(x, m) && !m->isStatic && svMatchesPrefix(m->name, prefix) && svIsVisible(x, m, fromClass) && !svIsLibraryInternal(x, m))
                svAddMemberItem(q, m, prefix, 0.0f, true);
        }
    }
}

static void svGetMemberSuggestions(SvCompletion* q, int offset, int dotOffset, const char* prefix) {
    SvIndex* x = q->x;
    SvType receiver = svResolveReceiver(x, dotOffset);
    const char* fromClass = svEnclosingClassAt(x, offset);
    int i;
    switch (receiver.kind) {
        case TK_NAMESPACE: {
            bool afterNew = svIsQualifiedNameAfterNew(q->src, dotOffset);
            FcList* ms = svMembersOfNamespace(x, receiver.name);
            for (i = 0; i < ms->count; i++) {
                SvNsMember* m = (SvNsMember*)ms->items[i];
                SvItemKind kind;
                const char* detail;
                if (!svMatchesPrefix(m->name, prefix)) continue;
                if (afterNew && (m->kind == SN_INTERFACE || m->kind == SN_ENUM)) continue;
                switch (m->kind) {
                    case SN_NAMESPACE: kind = CK_NAMESPACE; detail = fcFmt(q->t, "Namespace %s", m->fullName); break;
                    case SN_INTERFACE: kind = CK_CLASS; detail = "Interface"; break;
                    case SN_ENUM: kind = CK_ENUM; detail = "Enum"; break;
                    default: kind = CK_CLASS; detail = "Class"; break;
                }
                svAdd(q, m->name, kind, svCompareKeywords(prefix, m->name, 0.3f), detail, NULL);
            }
            break;
        }
        case TK_ENUM: {
            FcList* members = (FcList*)fcDictGet(&x->enumMembers, receiver.name);
            if (members)
                for (i = 0; i < members->count; i++) {
                    const char* name = (const char*)members->items[i];
                    if (svMatchesPrefix(name, prefix)) svAdd(q, name, CK_ENUMMEMBER, svCompareKeywords(prefix, name, 0.3f), fcFmt(q->t, "Enum %s", receiver.name), NULL);
                }
            break;
        }
        case TK_INSTANCE: case TK_STATIC: {
            FcList* all = svMembersOfWithDepth(x, receiver.name);
            for (i = 0; i < all->count; i++) {
                SvMemberAt* at = (SvMemberAt*)all->items[i];
                SvMember* m = at->m;
                bool staticOk;
                if (!svIsListable(x, m) || !svMatchesPrefix(m->name, prefix) || !svIsVisible(x, m, fromClass)) continue;
                staticOk = receiver.kind == TK_STATIC ? m->isStatic : (!m->isStatic || receiver.viaThis);
                if (!staticOk) continue;
                svAddMemberItem(q, m, prefix, svMemberBaseScore(x, m, at->depth), at->depth > 0);
            }
            break;
        }
        case TK_PRIMITIVE: case TK_ARRAY: {
            int n;
            const SvBuiltin* b = svBuiltinsFor(receiver, &n);
            const char* ext;
            for (i = 0; i < n; i++) {
                const char* type;
                const char* detail;
                if (!svMatchesPrefix(b[i].name, prefix)) continue;
                type = b[i].returnType ? (strcmp(b[i].returnType, "buffer") == 0 ? "byte[]" : b[i].returnType) : "";
                detail = b[i].isProperty ? fcFmt(q->t, "Property : %s", type) : fcFmt(q->t, "(%s)%s%s", b[i].signature, type[0] ? " \xE2\x86\x92 " : "", type);
                svAdd(q, b[i].name, b[i].isProperty ? CK_PROPERTY : CK_METHOD, svCompareKeywords(prefix, b[i].name, 0.3f), detail, NULL);
            }
            if ((ext = svExtensionClassOf(receiver)) != NULL && fcDictHas(&x->classes, ext)) {
                FcList* all = svMembersOfWithDepth(x, ext);
                for (i = 0; i < all->count; i++) {
                    SvMember* m = ((SvMemberAt*)all->items[i])->m;
                    if (m->kind == SM_METHOD && svIsListable(x, m) && svMatchesPrefix(m->name, prefix) && svIsVisible(x, m, fromClass)) svAddMemberItem(q, m, prefix, 0.3f, false);
                }
            }
            break;
        }
        default:
            svAddFallbackMembers(q, prefix, fromClass, false);
            if (q->items->count == 0 && prefix[0]) svAddFallbackMembers(q, prefix, fromClass, true);
            break;
    }
}

static void svGetIdentifierSuggestions(SvCompletion* q, int offset, const char* prefix) {
    SvIndex* x = q->x;
    FcList* ps;
    FcList* vars;
    const char* enclosing;
    size_t i;
    int k;
    for (i = 0; i < sizeof svKeywords / sizeof svKeywords[0]; i++)
        if (svMatchesPrefix(svKeywords[i], prefix)) svAdd(q, svKeywords[i], CK_KEYWORD, svCompareKeywords(prefix, svKeywords[i], 0.05f), NULL, NULL);
    for (i = 0; i < sizeof svTypeKeywords / sizeof svTypeKeywords[0]; i++)
        if (svMatchesPrefix(svTypeKeywords[i], prefix)) svAdd(q, svTypeKeywords[i], CK_TYPEKEYWORD, svCompareKeywords(prefix, svTypeKeywords[i], 0.05f), NULL, NULL);
    svAddVisibleTypes(q, offset, prefix, true, true);
    svAddTopLevelNamespaces(q, prefix);
    ps = svEnclosingFunctionParams(x, offset);
    for (k = 0; k < ps->count; k++) {
        const char* name = ((SvParam*)ps->items[k])->name;
        if (svMatchesPrefix(name, prefix)) svAdd(q, name, CK_PARAMETER, svCompareKeywords(prefix, name, 0.25f), NULL, NULL);
    }
    vars = svLocalVarsBeforeCursor(x, !prefix[0] ? offset : offset - fc8Units(prefix));
    for (k = 0; k < vars->count; k++) {
        const char* name = (const char*)vars->items[k];
        if (svMatchesPrefix(name, prefix)) svAdd(q, name, CK_VARIABLE, svCompareKeywords(prefix, name, 0.25f), NULL, NULL);
    }
    enclosing = svEnclosingClassAt(x, offset);
    if (enclosing) {
        bool staticOnly = svIsInStaticMethod(x, offset);
        FcList* all = svMembersOfWithDepth(x, enclosing);
        for (k = 0; k < all->count; k++) {
            SvMemberAt* at = (SvMemberAt*)all->items[k];
            SvMember* m = at->m;
            float base;
            if (!(svIsListable(x, m) && svMatchesPrefix(m->name, prefix) && svIsVisible(x, m, enclosing) && (!staticOnly || m->isStatic))) continue;
            base = 0.2f - 0.03f * (float)at->depth;
            if (base < 0.1f) base = 0.1f;
            svAddMemberItem(q, m, prefix, base - (svIsLibraryInternal(x, m) ? 0.1f : 0.0f), at->depth > 0);
        }
    }
}

/* `(?:^|,)\s*([A-Za-z_][A-Za-z0-9_]*):\s` over the arguments written so far */
static void svGivenNames(SvCompletion* q, const uint16_t* s, int from, int to, FcDict* given) {
    int i = from;
    while (i <= to) {
        if (i == from || s[i - 1] == ',') {
            int p = i, a;
            while (p < to && fcIsWhiteSpace(s[p])) p++;
            a = p;
            if (p < to && ((s[p] >= 'A' && s[p] <= 'Z') || (s[p] >= 'a' && s[p] <= 'z') || s[p] == '_')) {
                p++;
                while (p < to && ((s[p] >= 'A' && s[p] <= 'Z') || (s[p] >= 'a' && s[p] <= 'z') || (s[p] >= '0' && s[p] <= '9') || s[p] == '_')) p++;
                if (p + 1 < to + 1 && p < to && s[p] == ':' && p + 1 < to && fcIsWhiteSpace(s[p + 1])) fcSetAdd(given, fc8FromU16(q->t, s + a, p - a));
            }
        }
        i++;
    }
}

static void svAddNamedArgumentSuggestions(SvCompletion* q, int beforeWord, const char* prefix) {
    const uint16_t* s = q->src;
    SvIndex* x = q->x;
    int k = beforeWord, depth = 0, open = -1, j, nameEnd, nameStart, i;
    FcDict given;
    FcList* members = fcListNew(q->t);
    const char* callee;
    const char* fromClass;
    FcDict names;
    while (k >= 0 && (s[k] == ' ' || s[k] == '\t')) k--;
    if (k < 0 || (s[k] != '(' && s[k] != ',')) return;
    for (j = k; j >= 0 && j > k - 4000; j--) {
        uint16_t ch = s[j];
        if (ch == ')' || ch == ']' || ch == '}') depth++;
        else if (ch == '(' || ch == '[' || ch == '{') {
            if (depth == 0) { if (ch == '(') open = j; break; }
            depth--;
        }
    }
    if (open < 0) return;
    fcDictInit(q->t, &given, FC_KEY_STR);
    { int to = beforeWord + 1 > open + 1 ? beforeWord + 1 : open + 1; svGivenNames(q, s, open + 1, to, &given); }
    nameEnd = open - 1;
    while (nameEnd >= 0 && (s[nameEnd] == ' ' || s[nameEnd] == '\t')) nameEnd--;
    nameStart = nameEnd;
    while (nameStart >= 0 && (svIsWord(s[nameStart]) || s[nameStart] == '.')) nameStart--;
    callee = fc8FromU16(q->t, s + nameStart + 1, nameEnd - nameStart);
    if (!callee[0]) return;
    if (svIsAfterNew(s, nameStart)) {
        const char* cls = svTryFindClass(x, callee, open);
        FcList* all;
        if (!cls) return;
        all = svMembersOfWithDepth(x, cls);
        for (i = 0; i < all->count; i++) { SvMemberAt* at = (SvMemberAt*)all->items[i]; if (at->depth == 0 && at->m->kind == SM_CONSTRUCTOR) fcListAdd(q->t, members, at->m); }
    } else {
        const char* dot = strrchr(callee, '.');
        const char* method = dot ? dot + 1 : callee;
        const char* cls = NULL;
        FcList* all;
        if (dot) {
            /* the offset of that dot in the source: nameStart + 1 + (units before the dot) */
            int dotUnits = fc8Units(fcStrN(q->t, callee, (size_t)(dot - callee)));
            SvType receiver = svResolveReceiver(x, nameStart + 1 + dotUnits);
            if (receiver.kind == TK_INSTANCE || receiver.kind == TK_STATIC) cls = receiver.name;
        } else cls = svEnclosingClassAt(x, open);
        if (!cls) return;
        all = svMembersOfWithDepth(x, cls);
        for (i = 0; i < all->count; i++) { SvMember* m = ((SvMemberAt*)all->items[i])->m; if (m->kind == SM_METHOD && strcmp(m->name, method) == 0) fcListAdd(q->t, members, m); }
    }
    fromClass = svEnclosingClassAt(x, open);
    fcDictInit(q->t, &names, FC_KEY_STR);
    for (i = 0; i < members->count; i++) {
        SvMember* m = (SvMember*)members->items[i];
        int p;
        if (!svIsVisible(x, m, fromClass)) continue;
        for (p = 0; p < m->paramNames->count; p++) {
            const char* name = (const char*)m->paramNames->items[p];
            if (!fcSetAdd(&names, name)) continue;
            if (!fcDictHas(&given, name) && svMatchesPrefix(name, prefix))
                svAdd(q, fcFmt(q->t, "%s: ", name), CK_PARAMETER, svCompareKeywords(prefix, name, 0.4f), "named argument", NULL);
        }
    }
}

/* `#using` typed so far: ^[ \t]*#using[ \t]+([A-Za-z0-9_.]*)$ (ignore case) on the text of the line before the cursor; NULL: no such line */
static const char* svUsingTyped(SvCompletion* q, int lineStart, int offset) {
    const uint16_t* s = q->src;
    int p = lineStart, a, i;
    while (p < offset && svIsSpaceTab(s[p])) p++;
    if (p + 6 > offset || s[p] != '#' || svFold(s[p + 1]) != 'U' || svFold(s[p + 2]) != 'S' || svFold(s[p + 3]) != 'I' || svFold(s[p + 4]) != 'N' || svFold(s[p + 5]) != 'G') return NULL;
    p += 6;
    if (p >= offset || !svIsSpaceTab(s[p])) return NULL;
    while (p < offset && svIsSpaceTab(s[p])) p++;
    a = p;
    for (i = p; i < offset; i++) if (!(svIsWord(s[i]) && s[i] < 128) && s[i] != '.') return NULL;
    return fc8FromU16(q->t, s + a, offset - a);
}

static void svGetUsingSuggestions(SvCompletion* q, const char* typed) {
    const char* dot = strrchr(typed, '.');
    const char* parent = dot ? fcStrN(q->t, typed, (size_t)(dot - typed)) : "";
    const char* prefix = dot ? dot + 1 : typed;
    FcList* ms = svMembersOfNamespace(q->x, parent);
    int i;
    for (i = 0; i < ms->count; i++) {
        SvNsMember* m = (SvNsMember*)ms->items[i];
        if (m->kind == SN_NAMESPACE && svMatchesPrefix(m->name, prefix)) svAdd(q, m->name, CK_NAMESPACE, svCompareKeywords(prefix, m->name, 0.3f), fcFmt(q->t, "Namespace %s", m->fullName), NULL);
    }
}

/* GetSuggestions; the items best first; *replaceStart: the start of the word at the cursor */
static FcList* svGetSuggestions(SvLang* S, SvIndex* x, int offset, int* replaceStart) {
    SvCompletion q;
    const uint16_t* s = x->src;
    int i, start;
    const char* typed;
    q.S = S; q.x = x; q.t = S->tmp; q.items = fcListNew(S->tmp); q.src = s; q.len = x->srcLen;
    *replaceStart = offset;
    if (offset < 0 || offset > x->srcLen) return q.items;
    i = offset - 1;
    start = i;
    while (start >= 0 && svIsWord(s[start])) start--;
    *replaceStart = start + 1;
    if ((typed = svUsingTyped(&q, svLineStart(s, offset), offset)) != NULL) svGetUsingSuggestions(&q, typed);
    else if (i >= 0 && s[i] == '.') svGetMemberSuggestions(&q, offset, i, "");
    else if (start >= 0 && s[start] == '.') svGetMemberSuggestions(&q, offset, start, fc8FromU16(q.t, s + start + 1, offset - start - 1));
    else {
        const char* idPrefix = start + 1 <= offset ? fc8FromU16(q.t, s + start + 1, offset - start - 1) : "";
        if (svIsAfterNew(s, start)) {
            svAddVisibleTypes(&q, offset, idPrefix, false, false);
            svAddTopLevelNamespaces(&q, idPrefix);
        } else {
            svGetIdentifierSuggestions(&q, offset, idPrefix);
            svDedupe(&q);   /* (the C# deduplicates the identifiers, then appends the named arguments) */
            svAddNamedArgumentSuggestions(&q, start, idPrefix);
            for (i = 0; i < q.items->count; i++) ((SvItem*)q.items->items[i])->order = i;
            if (q.items->count > 1) qsort(q.items->items, (size_t)q.items->count, sizeof(void*), svCmpItemScore);
            return q.items;
        }
    }
    svDedupe(&q);
    for (i = 0; i < q.items->count; i++) ((SvItem*)q.items->items[i])->order = i;
    if (q.items->count > 1) qsort(q.items->items, (size_t)q.items->count, sizeof(void*), svCmpItemScore);
    return q.items;
}
