/* native/forge/xml.c - what the markup generators use of System.Xml (XDocument.Load/Parse with LoadOptions.SetLineInfo, XElement.ToString with
 * SaveOptions.DisableFormatting): src/fire.UI.Markup/MarkupParser.cs and FalstadImport.cs (commit 7a594769) read their XML with it. Not a port of
 * .NET's reader (that is not C# of this repository) but its behaviour for what a markup file holds: elements, attributes with namespaces,
 * text, CDATA, comments, processing instructions, a doctype (skipped), the five entities and character references; newlines normalized
 * (\r\n and \r to \n), attribute values normalized (\t \n to a space), text that is only white space dropped (no PreserveWhitespace); the line
 * and position of an element (its name) and an attribute; XmlException as a message with " Line n, position m." and its line. */

typedef enum FcXKind { FCX_ELEMENT, FCX_TEXT, FCX_CDATA, FCX_COMMENT, FCX_PI } FcXKind;

typedef struct FcXAttr {
    const char* name;     /* as written (prefix:local) */
    const char* prefix;   /* "" without one */
    const char* local;
    const char* ns;       /* the namespace URI ("" none) */
    const char* value;
    bool nsDecl;          /* xmlns / xmlns:p (XAttribute.IsNamespaceDeclaration) */
    int line, pos;
} FcXAttr;

typedef struct FcXNode FcXNode;
struct FcXNode {
    FcXKind kind;
    FcXNode* parent;
    int index;            /* in the nodes of the parent */
    const char* text;     /* text, CDATA, comment; the data of a processing instruction */
    const char* target;   /* a processing instruction */
    const char* name;     /* an element: as written */
    const char* prefix;
    const char* local;
    const char* ns;
    int line, pos;
    FcList* attrs;        /* FcXAttr* */
    FcList* nodes;        /* FcXNode* */
    bool empty;           /* <a/> (XElement.IsEmpty) */
};

typedef struct FcXmlError { const char* message; int line, pos; } FcXmlError;

typedef struct FcXReader {
    FcCtx* c;
    const uint16_t* s;
    int n, i;
    int line, lineStart;  /* the index where the current line starts */
    FcList* nsStack;      /* pairs prefix, uri (const char*) */
    FcList* open;         /* the elements whose content is read (outermost first) */
    jmp_buf fail;
    FcXmlError error;
} FcXReader;

#define FCX_XML_NS "http://www.w3.org/XML/1998/namespace"
#define FCX_XMLNS_NS "http://www.w3.org/2000/xmlns/"

static int fcxPos(FcXReader* r, int at) { return at - r->lineStart + 1; }

static _Noreturn void fcxFailAt(FcXReader* r, int line, int pos, const char* message) {
    r->error.message = fcFmt(r->c, "%s Line %d, position %d.", message, line, pos);
    r->error.line = line;
    r->error.pos = pos;
    longjmp(r->fail, 1);
}
static _Noreturn void fcxFail(FcXReader* r, const char* message) { fcxFailAt(r, r->line, fcxPos(r, r->i), message); }

/* the character as XmlException shows it: the character and its hexadecimal value */
static const char* fcxCharText(FcCtx* c, uint16_t u) {
    FcBuf b = {0};
    fcBufStr(&b, "");
    fcBufUnit(&b, u);
    return fcBufDone(c, &b);
}

static bool fcxIsSpace(uint16_t u) { return u == ' ' || u == '\t' || u == '\n' || u == '\r'; }
static bool fcxIsNameStart(uint16_t u) { return fcIsLetter(u) || u == '_' || u == ':' || (u >= 0xC0 && u != 0xD7 && u != 0xF7 && !(u >= 0x2000 && u <= 0x206F)); }
static bool fcxIsNameChar(uint16_t u) { return fcxIsNameStart(u) || fcIsDigit(u) || u == '-' || u == '.' || u == 0xB7; }

/* advance over one character, counting lines (\r\n, \r and \n are each one line break) */
static void fcxAdvance(FcXReader* r) {
    uint16_t u = r->s[r->i];
    r->i++;
    if (u == '\n') { r->line++; r->lineStart = r->i; }
    else if (u == '\r') {
        if (r->i < r->n && r->s[r->i] == '\n') r->i++;
        r->line++;
        r->lineStart = r->i;
    }
}
static bool fcxAt(FcXReader* r, const char* lit) {
    int k;
    for (k = 0; lit[k]; k++) if (r->i + k >= r->n || r->s[r->i + k] != (uint16_t)(unsigned char)lit[k]) return false;
    return true;
}
static void fcxSkipSpace(FcXReader* r) { while (r->i < r->n && fcxIsSpace(r->s[r->i])) fcxAdvance(r); }

static const char* fcxName(FcXReader* r) {
    int start = r->i;
    if (r->i >= r->n) fcxFail(r, "Unexpected end of file has occurred.");
    if (!fcxIsNameStart(r->s[r->i]) || r->s[r->i] == ':') {
        uint16_t u = r->s[r->i];
        fcxFail(r, fcFmt(r->c, "Name cannot begin with the '%s' character, hexadecimal value 0x%02X.", fcxCharText(r->c, u), u));
    }
    while (r->i < r->n && fcxIsNameChar(r->s[r->i])) r->i++;
    return fc8FromU16(r->c, r->s + start, r->i - start);
}

/* an entity or a character reference after '&' (the reader stands on '&'); appends the character(s) */
static void fcxReference(FcXReader* r, FcBuf* out) {
    int start = r->i;
    r->i++;
    if (r->i < r->n && r->s[r->i] == '#') {
        uint32_t v = 0;
        bool hex = false, any = false;
        r->i++;
        if (r->i < r->n && r->s[r->i] == 'x') { hex = true; r->i++; }
        while (r->i < r->n && r->s[r->i] != ';') {
            uint16_t u = r->s[r->i];
            int d = u >= '0' && u <= '9' ? u - '0' : hex && u >= 'a' && u <= 'f' ? u - 'a' + 10 : hex && u >= 'A' && u <= 'F' ? u - 'A' + 10 : -1;
            if (d < 0) {
                fcxFail(r, hex ? "Invalid syntax for a hexadecimal numeric entity reference." : "Invalid syntax for a decimal numeric entity reference.");
            }
            v = v * (hex ? 16 : 10) + (uint32_t)d;
            if (v > 0x10FFFF) v = 0x110000;
            any = true;
            r->i++;
        }
        if (!any || r->i >= r->n) fcxFail(r, hex ? "Invalid syntax for a hexadecimal numeric entity reference." : "Invalid syntax for a decimal numeric entity reference.");
        r->i++;
        if (v > 0x10FFFF || (v < 0x20 && v != 9 && v != 10 && v != 13) || (v >= 0xD800 && v <= 0xDFFF) || v == 0xFFFE || v == 0xFFFF) {
            int line = r->line, pos = fcxPos(r, start + 2);
            fcxFailAt(r, line, pos, fcFmt(r->c, "Invalid character in the given encoding."));
        }
        if (v >= 0x10000) { fcBufUnit(out, (uint16_t)(0xD800 + ((v - 0x10000) >> 10))); fcBufUnit(out, (uint16_t)(0xDC00 + ((v - 0x10000) & 0x3FF))); }
        else fcBufUnit(out, (uint16_t)v);
        return;
    }
    {
        int nameStart = r->i;
        const char* name;
        if (r->i >= r->n || !fcxIsNameStart(r->s[r->i])) { r->i = start + 1; fcxFail(r, "An error occurred while parsing EntityName."); }
        while (r->i < r->n && fcxIsNameChar(r->s[r->i])) r->i++;
        if (r->i >= r->n || r->s[r->i] != ';') fcxFail(r, "An error occurred while parsing EntityName.");
        name = fc8FromU16(r->c, r->s + nameStart, r->i - nameStart);
        r->i++;
        if (strcmp(name, "lt") == 0) fcBufChar(out, '<');
        else if (strcmp(name, "gt") == 0) fcBufChar(out, '>');
        else if (strcmp(name, "amp") == 0) fcBufChar(out, '&');
        else if (strcmp(name, "quot") == 0) fcBufChar(out, '"');
        else if (strcmp(name, "apos") == 0) fcBufChar(out, '\'');
        else {
            r->i = nameStart;
            fcxFail(r, fcFmt(r->c, "Reference to undeclared entity '%s'.", name));
        }
    }
}

static const char* fcxLookupNs(FcXReader* r, const char* prefix) {
    int k;
    if (strcmp(prefix, "xml") == 0) return FCX_XML_NS;
    if (strcmp(prefix, "xmlns") == 0) return FCX_XMLNS_NS;
    for (k = r->nsStack->count - 2; k >= 0; k -= 2)
        if (strcmp((const char*)r->nsStack->items[k], prefix) == 0) return (const char*)r->nsStack->items[k + 1];
    return prefix[0] ? NULL : "";
}

static void fcxSplitName(FcCtx* c, const char* name, const char** prefix, const char** local) {
    const char* colon = strchr(name, ':');
    if (!colon) { *prefix = ""; *local = name; return; }
    *prefix = fcStrN(c, name, (size_t)(colon - name));
    *local = colon + 1;
}

static void fcxAddNode(FcXReader* r, FcXNode* parent, FcXNode* node) {
    node->parent = parent;
    node->index = parent->nodes->count;
    fcListAdd(r->c, parent->nodes, node);
}

static void fcxComment(FcXReader* r, FcXNode* parent) {
    int start;
    r->i += 4;
    start = r->i;
    {
        FcBuf b = {0};
        fcBufStr(&b, "");
        while (r->i < r->n && !fcxAt(r, "-->")) {
            if (fcxAt(r, "--")) { r->i += 2; fcxFail(r, "An XML comment cannot contain '--', and '-' cannot be the last character."); }
            if (r->s[r->i] == '\r') { fcBufChar(&b, '\n'); fcxAdvance(r); continue; }
            fcBufUnit(&b, r->s[r->i]);
            fcxAdvance(r);
        }
        if (r->i >= r->n) { fcBufFree(&b); fcxFail(r, "Unexpected end of file while parsing Comment has occurred."); }
        r->i += 3;
        if (parent) {
            FcXNode* node = (FcXNode*)fcAlloc(r->c, sizeof *node);
            node->kind = FCX_COMMENT;
            node->text = fcBufDone(r->c, &b);
            fcxAddNode(r, parent, node);
        } else fcBufFree(&b);
    }
    (void)start;
}

static void fcxPi(FcXReader* r, FcXNode* parent) {
    const char* target;
    FcBuf b = {0};
    r->i += 2;
    target = fcxName(r);
    fcBufStr(&b, "");
    fcxSkipSpace(r);
    while (r->i < r->n && !fcxAt(r, "?>")) {
        if (r->s[r->i] == '\r') { fcBufChar(&b, '\n'); fcxAdvance(r); continue; }
        fcBufUnit(&b, r->s[r->i]);
        fcxAdvance(r);
    }
    if (r->i >= r->n) { fcBufFree(&b); fcxFail(r, "Unexpected end of file while parsing PI has occurred."); }
    r->i += 2;
    if (parent) {
        FcXNode* node = (FcXNode*)fcAlloc(r->c, sizeof *node);
        node->kind = FCX_PI;
        node->target = target;
        node->text = fcBufDone(r->c, &b);
        fcxAddNode(r, parent, node);
    } else fcBufFree(&b);
}

/* an attribute value in quotes (the reader stands on the quote) */
static const char* fcxAttrValue(FcXReader* r) {
    uint16_t quote = r->s[r->i];
    FcBuf b = {0};
    fcBufStr(&b, "");
    r->i++;
    for (;;) {
        uint16_t u;
        if (r->i >= r->n) { fcBufFree(&b); fcxFail(r, "Unexpected end of file has occurred."); }
        u = r->s[r->i];
        if (u == quote) { r->i++; break; }
        if (u == '<') { fcBufFree(&b); fcxFail(r, fcFmt(r->c, "'<', hexadecimal value 0x3C, is an invalid attribute character.")); }
        if (u == '&') { fcxReference(r, &b); continue; }
        if (u == '\r' || u == '\n' || u == '\t') { fcBufChar(&b, ' '); fcxAdvance(r); continue; }
        fcBufUnit(&b, u);
        r->i++;
    }
    return fcBufDone(r->c, &b);
}

static void fcxContent(FcXReader* r, FcXNode* element);

/* an element (the reader stands on '<') */
static FcXNode* fcxElement(FcXReader* r) {
    FcCtx* c = r->c;
    FcXNode* e = (FcXNode*)fcAlloc(c, sizeof *e);
    int nsMark = r->nsStack->count;
    int k, m;
    e->kind = FCX_ELEMENT;
    r->i++;
    e->line = r->line;
    e->pos = fcxPos(r, r->i);
    e->name = fcxName(r);
    e->attrs = fcListNew(c);
    e->nodes = fcListNew(c);
    for (;;) {
        bool space = r->i < r->n && fcxIsSpace(r->s[r->i]);
        fcxSkipSpace(r);
        if (r->i >= r->n) fcxFail(r, "Unexpected end of file has occurred.");
        if (r->s[r->i] == '>') { r->i++; break; }
        if (r->s[r->i] == '/') {
            r->i++;
            if (r->i >= r->n || r->s[r->i] != '>') {
                if (r->i >= r->n) fcxFail(r, "Unexpected end of file has occurred.");
                fcxFail(r, fcFmt(c, "'%s' is an unexpected token. The expected token is '>'.", fcxCharText(c, r->s[r->i])));
            }
            r->i++;
            e->empty = true;
            break;
        }
        if (!space) {
            fcxFail(r, fcFmt(c, "'%s' is an unexpected token. Expecting white space.", fcxCharText(c, r->s[r->i])));
        }
        {
            FcXAttr* a = (FcXAttr*)fcAlloc(c, sizeof *a);
            a->line = r->line;
            a->pos = fcxPos(r, r->i);
            a->name = fcxName(r);
            for (k = 0; k < e->attrs->count; k++)
                if (strcmp(((FcXAttr*)e->attrs->items[k])->name, a->name) == 0)
                    fcxFailAt(r, a->line, a->pos, fcFmt(c, "'%s' is a duplicate attribute name.", a->name));
            fcxSkipSpace(r);
            if (r->i >= r->n) fcxFail(r, "Unexpected end of file has occurred.");
            if (r->s[r->i] != '=') fcxFail(r, fcFmt(c, "'%s' is an unexpected token. The expected token is '='.", fcxCharText(c, r->s[r->i])));
            r->i++;
            fcxSkipSpace(r);
            if (r->i >= r->n) fcxFail(r, "Unexpected end of file has occurred.");
            if (r->s[r->i] != '"' && r->s[r->i] != '\'')
                fcxFail(r, fcFmt(c, "'%s' is an unexpected token. The expected token is '\"' or '''.", fcxCharText(c, r->s[r->i])));
            a->value = fcxAttrValue(r);
            fcListAdd(c, e->attrs, a);
            fcxSplitName(c, a->name, &a->prefix, &a->local);
            if (strcmp(a->name, "xmlns") == 0) { a->nsDecl = true; fcListAdd(c, r->nsStack, (void*)""); fcListAdd(c, r->nsStack, (void*)a->value); }
            else if (strcmp(a->prefix, "xmlns") == 0) { a->nsDecl = true; fcListAdd(c, r->nsStack, (void*)a->local); fcListAdd(c, r->nsStack, (void*)a->value); }
        }
    }
    /* the namespaces of the element and its attributes */
    fcxSplitName(c, e->name, &e->prefix, &e->local);
    e->ns = fcxLookupNs(r, e->prefix);
    if (!e->ns) fcxFailAt(r, e->line, e->pos, fcFmt(c, "'%s' is an undeclared prefix.", e->prefix));
    for (k = 0; k < e->attrs->count; k++) {
        FcXAttr* a = (FcXAttr*)e->attrs->items[k];
        if (strcmp(a->name, "xmlns") == 0) { a->ns = FCX_XMLNS_NS; continue; }   /* (XNamespace.Xmlns of "xmlns" itself: its name is in no namespace) */
        if (!a->prefix[0]) { a->ns = ""; continue; }
        a->ns = fcxLookupNs(r, a->prefix);
        if (!a->ns) fcxFailAt(r, a->line, a->pos, fcFmt(c, "'%s' is an undeclared prefix.", a->prefix));
        for (m = 0; m < k; m++) {
            FcXAttr* b = (FcXAttr*)e->attrs->items[m];
            if (b->ns && strcmp(b->ns, a->ns) == 0 && strcmp(b->local, a->local) == 0 && a->ns[0])
                fcxFailAt(r, a->line, a->pos, fcFmt(c, "'%s' is a duplicate attribute name.", a->name));
        }
    }
    for (k = 0; k < e->attrs->count; k++) {
        FcXAttr* a = (FcXAttr*)e->attrs->items[k];
        if (strcmp(a->name, "xmlns") == 0) a->ns = "";
    }
    if (!e->empty) {
        fcListAdd(c, r->open, e);
        fcxContent(r, e);
        r->open->count--;
    }
    r->nsStack->count = nsMark;
    return e;
}

/* the content of an element up to its end tag */
static void fcxContent(FcXReader* r, FcXNode* e) {
    FcCtx* c = r->c;
    for (;;) {
        if (r->i >= r->n) {
            /* the elements that are not closed: this one and those around it */
            FcBuf b = {0};
            int k;
            fcBufStr(&b, "");
            for (k = 0; k < r->open->count; k++) { if (k) fcBufStr(&b, ", "); fcBufStr(&b, ((FcXNode*)r->open->items[k])->name); }
            fcxFail(r, fcFmt(c, "Unexpected end of file has occurred. The following elements are not closed: %s.", fcBufDone(c, &b)));
        }
        if (r->s[r->i] == '<') {
            if (fcxAt(r, "</")) {
                int line, pos;
                const char* name;
                r->i += 2;
                line = r->line;
                pos = fcxPos(r, r->i);
                name = fcxName(r);
                if (strcmp(name, e->name) != 0) {
                    fcxFailAt(r, line, pos, fcFmt(c, "The '%s' start tag on line %d position %d does not match the end tag of '%s'.", e->name, e->line, e->pos, name));
                }
                fcxSkipSpace(r);
                if (r->i >= r->n) fcxFail(r, "Unexpected end of file has occurred.");
                if (r->s[r->i] != '>') fcxFail(r, fcFmt(c, "'%s' is an unexpected token. The expected token is '>'.", fcxCharText(c, r->s[r->i])));
                r->i++;
                return;
            }
            if (fcxAt(r, "<!--")) { fcxComment(r, e); continue; }
            if (fcxAt(r, "<![CDATA[")) {
                FcBuf b = {0};
                FcXNode* node;
                fcBufStr(&b, "");
                r->i += 9;
                while (r->i < r->n && !fcxAt(r, "]]>")) {
                    if (r->s[r->i] == '\r') { fcBufChar(&b, '\n'); fcxAdvance(r); continue; }
                    fcBufUnit(&b, r->s[r->i]);
                    fcxAdvance(r);
                }
                if (r->i >= r->n) { fcBufFree(&b); fcxFail(r, "Unexpected end of file while parsing CDATA has occurred."); }
                r->i += 3;
                node = (FcXNode*)fcAlloc(c, sizeof *node);
                node->kind = FCX_CDATA;
                node->text = fcBufDone(c, &b);
                fcxAddNode(r, e, node);
                continue;
            }
            if (fcxAt(r, "<?")) { fcxPi(r, e); continue; }
            if (fcxAt(r, "<!")) { r->i += 2; fcxFail(r, "Unexpected character in content."); }
            fcxAddNode(r, e, fcxElement(r));
            continue;
        }
        {
            /* text up to the next markup; only white space: dropped (XmlReaderSettings.IgnoreWhitespace) */
            FcBuf b = {0};
            bool onlySpace = true;
            fcBufStr(&b, "");
            while (r->i < r->n && r->s[r->i] != '<') {
                uint16_t u = r->s[r->i];
                if (u == '&') { fcxReference(r, &b); onlySpace = false; continue; }
                if (u == '\r') { fcBufChar(&b, '\n'); fcxAdvance(r); continue; }
                if (!fcxIsSpace(u)) onlySpace = false;
                if (u == ']' && fcxAt(r, "]]>")) fcxFail(r, "']]>' is not allowed in character data.");
                fcBufUnit(&b, u);
                fcxAdvance(r);
            }
            if (onlySpace) { fcBufFree(&b); continue; }
            {
                FcXNode* last = e->nodes->count ? (FcXNode*)e->nodes->items[e->nodes->count - 1] : NULL;
                if (last && last->kind == FCX_TEXT) {
                    /* (XContainer.AddStringSkipNotify: text after text is one text) */
                    FcBuf j = {0};
                    fcBufStr(&j, last->text);
                    fcBufPut(&j, b.p, b.len);
                    fcBufFree(&b);
                    last->text = fcBufDone(c, &j);
                } else {
                    FcXNode* node = (FcXNode*)fcAlloc(c, sizeof *node);
                    node->kind = FCX_TEXT;
                    node->text = fcBufDone(c, &b);
                    fcxAddNode(r, e, node);
                }
            }
        }
    }
}

/* skips a doctype (the reader stands on "<!DOCTYPE") */
static void fcxDoctype(FcXReader* r) {
    int depth = 0;
    uint16_t quote = 0;
    r->i += 9;
    while (r->i < r->n) {
        uint16_t u = r->s[r->i];
        if (quote) { if (u == quote) quote = 0; }
        else if (u == '"' || u == '\'') quote = u;
        else if (u == '[') depth++;
        else if (u == ']') depth--;
        else if (u == '>' && depth <= 0) { r->i++; return; }
        fcxAdvance(r);
    }
    fcxFail(r, "Unexpected end of file while parsing DOCTYPE has occurred.");
}

/* XDocument.Load(reader, LoadOptions.SetLineInfo): the root element, or NULL and the XmlException in `error` */
static FcXNode* fcXmlParse(FcCtx* c, const uint16_t* s, int n, FcXmlError* error) {
    FcXReader* r = (FcXReader*)fcAlloc(c, sizeof *r);
    FcXNode* volatile root = NULL;
    r->c = c;
    r->s = s;
    r->n = n;
    r->line = 1;
    r->nsStack = fcListNew(c);
    r->open = fcListNew(c);
    if (setjmp(r->fail) != 0) {
        *error = r->error;
        return NULL;
    }
    if (fcxAt(r, "<?xml") && r->i + 5 < r->n && fcxIsSpace(r->s[r->i + 5])) fcxPi(r, NULL);
    for (;;) {
        fcxSkipSpace(r);
        if (r->i >= r->n) break;
        if (r->s[r->i] != '<') {
            if (root) fcxFail(r, "Data at the root level is invalid.");
            fcxFail(r, "Data at the root level is invalid.");
        }
        if (fcxAt(r, "<!--")) { fcxComment(r, NULL); continue; }
        if (fcxAt(r, "<?")) {
            if (fcxAt(r, "<?xml") && r->i + 5 < r->n && (fcxIsSpace(r->s[r->i + 5]) || r->s[r->i + 5] == '?')) {
                r->i += 2;
                fcxFail(r, "Unexpected XML declaration. The XML declaration must be the first node in the document, and no white space characters are allowed to appear before it.");
            }
            fcxPi(r, NULL);
            continue;
        }
        if (fcxAt(r, "<!DOCTYPE")) { fcxDoctype(r); continue; }
        if (fcxAt(r, "</")) { r->i += 2; fcxFail(r, "Unexpected end tag."); }
        if (fcxAt(r, "<!")) { r->i += 2; fcxFail(r, "Data at the root level is invalid."); }
        if (root) { r->i++; fcxFail(r, "There are multiple root elements."); }
        root = fcxElement(r);
    }
    if (!root) fcxFail(r, "Root element is missing.");
    return root;
}

/* XContainer.Elements(): the child elements */
static FC_LATER int fcxElementCount(const FcXNode* e) {
    int k, n = 0;
    for (k = 0; k < e->nodes->count; k++) if (((FcXNode*)e->nodes->items[k])->kind == FCX_ELEMENT) n++;
    return n;
}
/* XElement.Attribute(name) of an attribute in no namespace; NULL without one */
static const char* fcxAttr(const FcXNode* e, const char* name) {
    int k;
    for (k = 0; k < e->attrs->count; k++) {
        const FcXAttr* a = (const FcXAttr*)e->attrs->items[k];
        if (!a->nsDecl && a->ns[0] == 0 && strcmp(a->local, name) == 0) return a->value;
    }
    return NULL;
}

/* XElement.ToString(SaveOptions.DisableFormatting) */
static void fcxEscText(FcBuf* b, const char* s, bool attribute) {
    for (; *s; s++) {
        switch (*s) {
            case '&': fcBufStr(b, "&amp;"); break;
            case '<': fcBufStr(b, "&lt;"); break;
            case '>': fcBufStr(b, "&gt;"); break;
            case '"': if (attribute) fcBufStr(b, "&quot;"); else fcBufChar(b, '"'); break;
            case '\n': if (attribute) fcBufStr(b, "&#xA;"); else fcBufChar(b, '\n'); break;
            case '\r': fcBufStr(b, "&#xD;"); break;
            case '\t': if (attribute) fcBufStr(b, "&#x9;"); else fcBufChar(b, '\t'); break;
            default: fcBufChar(b, *s);
        }
    }
}
static void fcxWrite(FcBuf* b, const FcXNode* x) {
    int k;
    switch (x->kind) {
        case FCX_TEXT: fcxEscText(b, x->text, false); return;
        case FCX_CDATA: fcBufStr(b, "<![CDATA["); fcBufStr(b, x->text); fcBufStr(b, "]]>"); return;
        case FCX_COMMENT: fcBufStr(b, "<!--"); fcBufStr(b, x->text); fcBufStr(b, "-->"); return;
        case FCX_PI: fcBufStr(b, "<?"); fcBufStr(b, x->target); if (x->text[0]) { fcBufChar(b, ' '); fcBufStr(b, x->text); } fcBufStr(b, "?>"); return;
        case FCX_ELEMENT: break;
    }
    fcBufChar(b, '<');
    fcBufStr(b, x->name);
    for (k = 0; k < x->attrs->count; k++) {
        const FcXAttr* a = (const FcXAttr*)x->attrs->items[k];
        fcBufChar(b, ' ');
        fcBufStr(b, a->name);
        fcBufStr(b, "=\"");
        fcxEscText(b, a->value, true);
        fcBufChar(b, '"');
    }
    if (x->empty) { fcBufStr(b, " />"); return; }
    fcBufChar(b, '>');
    for (k = 0; k < x->nodes->count; k++) fcxWrite(b, (const FcXNode*)x->nodes->items[k]);
    fcBufStr(b, "</");
    fcBufStr(b, x->name);
    fcBufChar(b, '>');
}
