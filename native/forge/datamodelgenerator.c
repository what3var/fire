/* native/forge/datamodelgenerator.c - port of src/fire.UI.Markup/DataModelGenerator.cs (commit 7a594769): a data model (`.fdata`: JSON, json.c)
 * as fire - the classes of its entities with the attributes of the mapping of the package `data`, its enums and records, `Model` and `Context`.
 * (DataModelDocument.cs is the designer's model in spark; the generator does not use it - not ported.) Where C# would end with an exception of
 * System.Text.Json (TryGetInt32 or GetString on a value of another kind), forge-c reports an internal error. */

#define FM_DATA_CURRENT_FORMAT 1

static const char* const fmDataKeywords[] = {
    "class", "var", "int", "float", "bool", "string", "char", "any", "undefined", "true", "false", "if", "else", "while", "for", "foreach", "in", "of", "from", "is",
    "return", "new", "delete", "namespace", "using", "func", "on", "off", "copy", "take", "replace", "try", "catch", "finally", "throw", "break", "continue", "switch", "case",
    "default", "static", "construct", "destruct", "fire", "sync", "unit", "enum", "interface", "extern", "native", "byte", "word", "do", "readonly", "flat", "override",
    "record", "actor", "this", "base", "where", "to", "lambda", "await", "process", "taking", "handover", "defined", "trust", "terminate", "leave", "print",
    NULL
};
static const char* const fmDataScalars[] = { "int", "float", "bool", "string", "char", "bytes", "DateTime", NULL };

typedef struct FmField {
    const char* name; const char* type; const char* column; bool key, generated, required; int length; bool unique, index, version, json;
    const char* comment; int line; const char* check; const char* dbType;
} FmField;
typedef struct FmEntity { const char* name; const char* table; bool record; const char* set; const char* comment; FcList* fields; int line; const char* view; FcList* checks; } FmEntity;
typedef struct FmTypeDecl { const char* name; FcList* fields; const char* comment; int line; } FmTypeDecl;
typedef struct FmEnumDecl { const char* name; FcList* values; const char* comment; int line; } FmEnumDecl;
typedef struct FmRelation {
    const char* kind; const char* from; const char* to; const char* key; const char* navigation; const char* inverse; const char* joinTable; const char* fromColumn; const char* toColumn; int line;
} FmRelation;

/* IsName */
static bool fmDataIsName(const char* name) {
    const unsigned char* p = (const unsigned char*)name;
    uint16_t u;
    if (!*p) return false;
    u = fc8Next(&p);
    if (!fcIsLetter(u) && u != '_') return false;
    while (*p) { u = fc8Next(&p); if (!fcIsLetterOrDigit(u) && u != '_') return false; }
    return !fmInSet(fmDataKeywords, name);
}
/* Lower: the first character in lower case */
static const char* fmDataLower(FcCtx* c, const char* name) {
    const unsigned char* p = (const unsigned char*)name;
    FcBuf b = {0};
    if (!*p) return name;
    fcBufStr(&b, "");
    fcBufUnit(&b, fmLower(fc8Next(&p)));
    fcBufStr(&b, (const char*)p);
    return fcBufDone(c, &b);
}
/* Snake */
static const char* fmDataSnake(FcCtx* c, const char* name) {
    int n, i;
    uint16_t* u = fmUnits(c, name, &n);
    FcBuf b = {0};
    fcBufStr(&b, "");
    for (i = 0; i < n; i++) {
        if (fmIsUpper(u[i]) && i > 0 && (fmIsLower(u[i - 1]) || fcIsDigit(u[i - 1]))) fcBufChar(&b, '_');
        fcBufUnit(&b, fmLower(u[i]));
    }
    return fcBufDone(c, &b);
}
/* Plural */
static const char* fmDataPlural(FcCtx* c, const char* name) {
    size_t n = strlen(name);
    if (fcEndsWith(name, "y") && n > 1 && !strchr("aeiou", name[n - 2])) return fcFmt(c, "%.*sies", (int)(n - 1), name);
    if (fcEndsWith(name, "s") || fcEndsWith(name, "x") || fcEndsWith(name, "ch") || fcEndsWith(name, "sh")) return fcFmt(c, "%ses", name);
    return fcFmt(c, "%ss", name);
}
static const char* fmDataTableName(FcCtx* c, const char* entity, const char* naming) {
    if (strcmp(naming, "plural") == 0) return fmDataPlural(c, entity);
    if (strcmp(naming, "snake") == 0) return fmDataSnake(c, entity);
    if (strcmp(naming, "snake_plural") == 0) return fmDataSnake(c, fmDataPlural(c, entity));
    return entity;
}
/* Lit: a text as a fire string literal */
static const char* fmDataLit(FcCtx* c, const char* text) {
    return fcFmt(c, "\"%s\"", fmReplace(c, fmReplace(c, fmReplace(c, text, "\\", "\\\\"), "\"", "\\\""), "\n", "\\n"));
}
static bool fmDataBool(const FjValue* e, const char* name) { const FjValue* v = fjProp(e, name); return v && v->kind == FJK_TRUE; }

/* (a TryGetInt32 or GetString of System.Text.Json on a value of another kind: an InvalidOperationException in C#) */
static _Noreturn void fmDataKindError(FcCtx* c, const char* wanted, const FjValue* v) {
    static const char* const kinds[] = { "Object", "Array", "String", "Number", "True", "False", "Null" };
    fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt(c, "Unhandled exception. System.InvalidOperationException: The requested operation requires an element of type '%s', but the target element has type '%s'.", wanted, kinds[v->kind]));
}
/* f.TryGetInt32(out v) on a property that is there */
static bool fmDataTryInt(FcCtx* c, const FjValue* v, int* out) {
    if (v->kind != FJK_NUMBER) fmDataKindError(c, "Number", v);
    return fjTryGetInt32(v, out);
}

/* LineIndex: the line of every object by its path ("entities[1].fields[0]") */
typedef struct FmLineIndex { FcList* paths; FcList* lines; } FmLineIndex;
static void fmLineIndexSet(FcCtx* c, FmLineIndex* m, const char* path, int line) {
    int i;
    for (i = 0; i < m->paths->count; i++) if (strcmp((const char*)m->paths->items[i], path) == 0) { m->lines->items[i] = (void*)(intptr_t)line; return; }
    fcListAdd(c, m->paths, (void*)path);
    fcListAdd(c, m->lines, (void*)(intptr_t)line);
}
static int fmLineIndexGet(const FmLineIndex* m, const char* path) {
    int i;
    for (i = 0; i < m->paths->count; i++) if (strcmp((const char*)m->paths->items[i], path) == 0) return (int)(intptr_t)m->lines->items[i];
    return 0;
}
typedef struct FmPathFrame { const char* path; bool array; int index; } FmPathFrame;
static FmLineIndex* fmLineIndex(FcCtx* c, const char* text, FcList* tokens) {
    FmLineIndex* m = (FmLineIndex*)fcAlloc(c, sizeof *m);
    size_t n;
    const unsigned char* bytes = fjBytes(c, text, &n);
    FcList* stack = fcListNew(c);
    const char* prop = "";
    int i;
    m->paths = fcListNew(c);
    m->lines = fcListNew(c);
    for (i = 0; i < tokens->count; i++) {
        FjToken* t = (FjToken*)tokens->items[i];
        FmPathFrame* top = stack->count ? (FmPathFrame*)stack->items[stack->count - 1] : NULL;
        switch (t->type) {
            case FJT_PROPERTY: prop = t->name; break;
            case FJT_START_OBJECT:
            case FJT_START_ARRAY: {
                const char* path = !top ? "" : top->array ? fcFmt(c, "%s[%d]", top->path, top->index) : !top->path[0] ? prop : fcFmt(c, "%s.%s", top->path, prop);
                FmPathFrame* f = (FmPathFrame*)fcAlloc(c, sizeof *f);
                if (t->type == FJT_START_OBJECT) {
                    size_t k;
                    int line = 1;
                    for (k = 0; k < t->start && k < n; k++) if (bytes[k] == '\n') line++;
                    fmLineIndexSet(c, m, path, line);
                }
                f->path = path;
                f->array = t->type == FJT_START_ARRAY;
                fcListAdd(c, stack, f);
                break;
            }
            case FJT_END_OBJECT:
            case FJT_END_ARRAY:
                stack->count--;
                if (stack->count && ((FmPathFrame*)stack->items[stack->count - 1])->array) ((FmPathFrame*)stack->items[stack->count - 1])->index++;
                break;
            default:
                if (top && top->array) top->index++;
                break;
        }
    }
    return m;
}

/* Items: the objects of an array of the owner, with their lines and paths */
typedef struct FmItem { const FjValue* item; int line; const char* path; } FmItem;
static FcList* fmDataItems(FcCtx* c, const FjValue* owner, const char* name, const FmLineIndex* lines, const char* ownerPath) {
    FcList* out = fcListNew(c);
    const FjValue* array = fjProp(owner, name);
    int i;
    if (!array || array->kind != FJK_ARRAY) return out;
    for (i = 0; i < array->items->count; i++) {
        const FjValue* item = (const FjValue*)array->items->items[i];
        const char* path = fcFmt(c, "%s[%d]", !ownerPath[0] ? name : fcFmt(c, "%s.%s", ownerPath, name), i);
        if (item->kind == FJK_OBJECT) {
            FmItem* it = (FmItem*)fcAlloc(c, sizeof *it);
            it->item = item;
            it->line = fmLineIndexGet(lines, path);
            it->path = path;
            fcListAdd(c, out, it);
        }
    }
    return out;
}

/* ReadFields */
static FcList* fmDataReadFields(FcCtx* c, const FjValue* owner, const FmLineIndex* lines, const char* ownerPath) {
    FcList* list = fcListNew(c);
    FcList* items = fmDataItems(c, owner, "fields", lines, ownerPath);
    int i;
    for (i = 0; i < items->count; i++) {
        FmItem* it = (FmItem*)items->items[i];
        const FjValue* fd = it->item;
        FmField* f = (FmField*)fcAlloc(c, sizeof *f);
        const FjValue* l = fjProp(fd, "length");
        const FjValue* g = fjProp(fd, "generated");
        const FjValue* dt = fjProp(fd, "dbType");
        int lv;
        f->name = fmJsonStr(fd, "name"); if (!f->name) f->name = "";
        f->type = fmJsonStr(fd, "type"); if (!f->type) f->type = "string";
        f->length = l && fmDataTryInt(c, l, &lv) ? lv : 0;
        f->key = fmDataBool(fd, "key");
        f->generated = !g || g->kind != FJK_FALSE;
        f->check = fmJsonStr(fd, "check");
        if (f->check && fmTrim(c, f->check)[0] == 0) f->check = NULL;
        /* ("dbType": "jsonb" for every database, or { "postgres": "jsonb", "mysql": "JSON", "*": "TEXT" } - [DbType "postgres: jsonb; mysql: JSON; *: TEXT"]) */
        f->dbType = NULL;
        if (dt) {
            if (dt->kind == FJK_STRING && fmTrim(c, dt->text)[0]) f->dbType = fmTrim(c, dt->text);
            else if (dt->kind == FJK_OBJECT) {
                FcBuf b = {0};
                int k, parts = 0;
                fcBufStr(&b, "");
                for (k = 0; k < dt->items->count; k++) {
                    const FjValue* p = (const FjValue*)dt->items->items[k];
                    if (p->kind != FJK_STRING) continue;
                    if (parts++) fcBufStr(&b, "; ");
                    fcBufFmt(&b, "%s: %s", (const char*)dt->names->items[k], p->text);
                }
                if (parts > 0) f->dbType = fcBufDone(c, &b); else fcBufFree(&b);
            }
        }
        f->column = fmJsonStr(fd, "column");
        f->required = fmDataBool(fd, "required");
        f->unique = fmDataBool(fd, "unique");
        f->index = fmDataBool(fd, "index");
        f->version = fmDataBool(fd, "version");
        f->json = fmDataBool(fd, "json");
        f->comment = fmJsonStr(fd, "comment");
        f->line = it->line;
        fcListAdd(c, list, f);
    }
    return list;
}

/* FieldLine */
static const char* fmDataFieldLine(FcCtx* c, const FmField* fd, const char* columnNaming, bool isEntity) {
    FcBuf sb = {0};
    const char* column = fd->column ? fd->column : strcmp(columnNaming, "snake") == 0 && strcmp(fmDataSnake(c, fd->name), fd->name) != 0 ? fmDataSnake(c, fd->name) : NULL;
    fcBufStr(&sb, "");
    if (isEntity && fd->key) fcBufStr(&sb, fd->generated || strcmp(fd->type, "int") != 0 ? "[Key] " : "[Key \"manual\"] ");
    if (isEntity && column && strcmp(column, fd->name) != 0) fcBufFmt(&sb, "[Column \"%s\"] ", column);
    if (isEntity && fd->required && !fd->key) fcBufStr(&sb, "[Required] ");
    if (fd->length > 0) fcBufFmt(&sb, "[Length %d] ", fd->length);
    if (isEntity && fd->unique) fcBufStr(&sb, "[Unique] ");
    if (isEntity && fd->index && !fd->unique) fcBufStr(&sb, "[Index] ");
    if (isEntity && fd->version) fcBufStr(&sb, "[Version] ");
    if (fd->json) fcBufStr(&sb, "[Json] ");
    if (isEntity && fd->check) fcBufFmt(&sb, "[Check %s] ", fmDataLit(c, fd->check));
    if (isEntity && fd->dbType) fcBufFmt(&sb, "[DbType %s] ", fmDataLit(c, fd->dbType));
    if (strcmp(fd->type, "bytes") == 0) fcBufFmt(&sb, "byte %s[]", fd->name);
    else fcBufFmt(&sb, "%s %s", fd->type, fd->name);
    return fcBufDone(c, &sb);
}

/* Comment */
static void fmDataComment(FcCtx* c, FcBuf* sb, const char* comment, const char* indent) {
    if (fmIsBlank(comment)) return;
    fcBufFmt(sb, "%s/// <summary>%s</summary>\n", indent, fmReplace(c, fmReplace(c, fmReplace(c, comment, "\n", " "), "<", "&lt;"), ">", "&gt;"));
}

static FmEntity* fmDataEntity(FcList* entities, const char* name) {
    int i;
    for (i = 0; i < entities->count; i++) if (strcmp(((FmEntity*)entities->items[i])->name, name) == 0) return (FmEntity*)entities->items[i];
    return NULL;
}
static bool fmDataHasField(const FmEntity* e, const char* name) {
    int i;
    for (i = 0; i < e->fields->count; i++) if (strcmp(((FmField*)e->fields->items[i])->name, name) == 0) return true;
    return false;
}

typedef struct FmDataGen {
    FcCtx* c;
    FcList* errors;
    FcList* entities;
    FcList* extraNames;   /* the entities (first declaration of a name) */
    FcList* extra;        /* FcList* of lines per name */
    FcList* added;        /* FcList* of field names per name */
} FmDataGen;

static int fmDataGroupIndex(FmDataGen* g, const char* entity) {
    int i;
    for (i = 0; i < g->extraNames->count; i++) if (strcmp((const char*)g->extraNames->items[i], entity) == 0) return i;
    return -1;
}
/* Add: a field a relation adds */
static void fmDataAdd(FmDataGen* g, const char* entity, const char* name, const char* line, int at) {
    int k = fmDataGroupIndex(g, entity);
    FcList* added = (FcList*)g->added->items[k];
    if (fcListHasStr(added, name)) { fcListAdd(g->c, g->errors, fmDiag(g->c, at, fcFmt(g->c, "'%s' has a field '%s' already - give the relation other names.", entity, name))); return; }
    fcListAdd(g->c, added, (void*)name);
    fcListAdd(g->c, (FcList*)g->extra->items[k], (void*)line);
}
/* KeyType */
static const char* fmDataKeyType(FmDataGen* g, const char* entity) {
    FmEntity* e = fmDataEntity(g->entities, entity);
    int i;
    for (i = 0; i < e->fields->count; i++) if (((FmField*)e->fields->items[i])->key) return ((FmField*)e->fields->items[i])->type;
    for (i = 0; i < e->fields->count; i++) if (strcmp(((FmField*)e->fields->items[i])->name, "id") == 0) return ((FmField*)e->fields->items[i])->type;
    return "int";
}

static void fmDataCheckFields(FmDataGen* g, const char* owner, FcList* fields, bool needsKey, int line, FcList* enumNames, FcList* recordNames) {
    FcCtx* c = g->c;
    FcList* seen = fcListNew(c);
    int i, keys = 0;
    bool anyId = false;
    for (i = 0; i < fields->count; i++) {
        FmField* fd = (FmField*)fields->items[i];
        bool known = fmInSet(fmDataScalars, fd->type) || fcListHasStr(enumNames, fd->type) || fcListHasStr(recordNames, fd->type);
        if (!fmDataIsName(fd->name)) fcListAdd(c, g->errors, fmDiag(c, fd->line, fcFmt(c, "The field '%s' of '%s' needs a name that fire allows.", fd->name, owner)));
        else if (fcListHasStr(seen, fd->name)) fcListAdd(c, g->errors, fmDiag(c, fd->line, fcFmt(c, "'%s' has the field '%s' twice.", owner, fd->name)));
        else fcListAdd(c, seen, (void*)fd->name);
        if (!known) fcListAdd(c, g->errors, fmDiag(c, fd->line, fcFmt(c, "The field '%s.%s' has the type '%s' - known are int, float, bool, string, char, bytes, DateTime, the enums and the types of the model.", owner, fd->name, fd->type)));
        if (fcListHasStr(recordNames, fd->type) && !fd->json) fcListAdd(c, g->errors, fmDiag(c, fd->line, fcFmt(c, "The field '%s.%s' of the type '%s' is stored as JSON: give it \"json\": true.", owner, fd->name, fd->type)));
        if (fd->key && !(strcmp(fd->type, "int") == 0 || strcmp(fd->type, "string") == 0)) fcListAdd(c, g->errors, fmDiag(c, fd->line, fcFmt(c, "The key '%s.%s' is an int or a string, not '%s'.", owner, fd->name, fd->type)));
        if (fd->version && strcmp(fd->type, "int") != 0) fcListAdd(c, g->errors, fmDiag(c, fd->line, fcFmt(c, "The version field '%s.%s' is an int.", owner, fd->name)));
        if (fd->length < 0) fcListAdd(c, g->errors, fmDiag(c, fd->line, fcFmt(c, "The length of '%s.%s' is not negative.", owner, fd->name)));
        if (fd->key) keys++;
        if (strcmp(fd->name, "id") == 0) anyId = true;
    }
    if (needsKey && keys > 1) fcListAdd(c, g->errors, fmDiag(c, line, fcFmt(c, "'%s' has more than one key (the mapping has one key field).", owner)));
    if (needsKey && keys == 0 && !anyId) fcListAdd(c, g->errors, fmDiag(c, line, fcFmt(c, "The entity '%s' has no key: a field with \"key\": true (or a field 'id').", owner)));
}

/* Generate */
static const char* fmDataGenerate(FcCtx* c, const char* text, const char* path) {
    FmDataGen gen, *g = &gen;
    FjError jerr;
    FcList* tokens = fcListNew(c);
    FjValue* root = fjParse(c, text, &jerr, tokens);
    FmLineIndex* lines;
    int format = FM_DATA_CURRENT_FORMAT, i, k;
    const char* ns;
    const char* tableNaming = "name";
    const char* columnNaming = "field";
    const FjValue* f;
    const FjValue* naming;
    FcList* enums = fcListNew(c);
    FcList* types = fcListNew(c);
    FcList* relations = fcListNew(c);
    FcList* items;
    FcList* typeNames = fcListNew(c);
    FcList* enumNames = fcListNew(c);
    FcList* recordNames = fcListNew(c);
    FcList* entityNames = fcListNew(c);
    FcList* tables = fcListNew(c);
    FcList* ordered;
    FcList* sets;
    FcBuf sb = {0};
    memset(g, 0, sizeof *g);
    g->c = c;
    g->errors = fcListNew(c);
    g->entities = fcListNew(c);
    if (!root) fjThrowInvalid(c, &jerr);
    if (root->kind != FJK_OBJECT) fmThrow1(c, 1, "A .fdata file is a JSON object.");
    lines = fmLineIndex(c, text, tokens);
    if ((f = fjProp(root, "format")) != NULL) { int fv; if (fmDataTryInt(c, f, &fv)) format = fv; }
    if (format > FM_DATA_CURRENT_FORMAT) fcListAdd(c, g->errors, fmDiag(c, 1, fcFmt(c, "The format %d is newer than this forge knows (%d).", format, FM_DATA_CURRENT_FORMAT)));
    ns = fmJsonStr(root, "namespace");
    if (!ns) ns = fmFileNameWithoutExtension(c, path);
    if (!fmDataIsName(fmReplace(c, ns, ".", ""))) fcListAdd(c, g->errors, fmDiag(c, 1, fcFmt(c, "'%s' is no name for a namespace.", ns)));
    if ((naming = fjProp(root, "naming")) != NULL && naming->kind == FJK_OBJECT) {
        tableNaming = fmJsonStr(naming, "tables"); if (!tableNaming) tableNaming = "name";
        columnNaming = fmJsonStr(naming, "columns"); if (!columnNaming) columnNaming = "field";
        if (strcmp(tableNaming, "name") != 0 && strcmp(tableNaming, "plural") != 0 && strcmp(tableNaming, "snake") != 0 && strcmp(tableNaming, "snake_plural") != 0)
            fcListAdd(c, g->errors, fmDiag(c, 1, fcFmt(c, "naming.tables is name, plural, snake or snake_plural, not '%s'.", tableNaming)));
        if (strcmp(columnNaming, "field") != 0 && strcmp(columnNaming, "snake") != 0) fcListAdd(c, g->errors, fmDiag(c, 1, fcFmt(c, "naming.columns is field or snake, not '%s'.", columnNaming)));
    }

    items = fmDataItems(c, root, "enums", lines, "");
    for (i = 0; i < items->count; i++) {
        FmItem* it = (FmItem*)items->items[i];
        const FjValue* e = it->item;
        const char* name = fmJsonStr(e, "name");
        const FjValue* vs = fjProp(e, "values");
        FmEnumDecl* en = (FmEnumDecl*)fcAlloc(c, sizeof *en);
        FcList* values = fcListNew(c);
        if (!name) name = "";
        if (vs && vs->kind == FJK_ARRAY)
            for (k = 0; k < vs->items->count; k++) {
                const FjValue* v = (const FjValue*)vs->items->items[k];
                if (v->kind != FJK_STRING && v->kind != FJK_NULL) fmDataKindError(c, "String", v);
                fcListAdd(c, values, (void*)(v->kind == FJK_STRING ? v->text : ""));
            }
        if (!fmDataIsName(name)) fcListAdd(c, g->errors, fmDiag(c, it->line, fcFmt(c, "The enum '%s' needs a name that fire allows.", name)));
        if (values->count == 0) fcListAdd(c, g->errors, fmDiag(c, it->line, fcFmt(c, "The enum '%s' has no values.", name)));
        for (k = 0; k < values->count; k++) if (!fmDataIsName((const char*)values->items[k])) fcListAdd(c, g->errors, fmDiag(c, it->line, fcFmt(c, "'%s' is no name for a value of the enum '%s'.", (const char*)values->items[k], name)));
        en->name = name;
        en->values = values;
        en->comment = fmJsonStr(e, "comment");
        en->line = it->line;
        fcListAdd(c, enums, en);
    }
    items = fmDataItems(c, root, "types", lines, "");
    for (i = 0; i < items->count; i++) {
        FmItem* it = (FmItem*)items->items[i];
        FmTypeDecl* t = (FmTypeDecl*)fcAlloc(c, sizeof *t);
        t->name = fmJsonStr(it->item, "name"); if (!t->name) t->name = "";
        t->fields = fmDataReadFields(c, it->item, lines, it->path);
        t->comment = fmJsonStr(it->item, "comment");
        t->line = it->line;
        fcListAdd(c, types, t);
    }
    items = fmDataItems(c, root, "entities", lines, "");
    for (i = 0; i < items->count; i++) {
        FmItem* it = (FmItem*)items->items[i];
        const FjValue* e = it->item;
        FmEntity* en = (FmEntity*)fcAlloc(c, sizeof *en);
        const FjValue* cs = fjProp(e, "checks");
        const char* table;
        en->name = fmJsonStr(e, "name"); if (!en->name) en->name = "";
        table = fmJsonStr(e, "table");
        en->table = table ? table : fmDataTableName(c, en->name, tableNaming);
        en->checks = fcListNew(c);
        if (cs && cs->kind == FJK_ARRAY)
            for (k = 0; k < cs->items->count; k++) {
                const FjValue* cv = (const FjValue*)cs->items->items[k];
                if (cv->kind == FJK_STRING && fmTrim(c, cv->text)[0]) fcListAdd(c, en->checks, (void*)cv->text);
            }
        en->view = fmJsonStr(e, "view");
        if (en->view && fmTrim(c, en->view)[0] == 0) en->view = NULL;
        en->record = fmDataBool(e, "record");
        en->set = fmJsonStr(e, "set");
        en->comment = fmJsonStr(e, "comment");
        en->fields = fmDataReadFields(c, e, lines, it->path);
        en->line = it->line;
        fcListAdd(c, g->entities, en);
    }
    items = fmDataItems(c, root, "relations", lines, "");
    for (i = 0; i < items->count; i++) {
        FmItem* it = (FmItem*)items->items[i];
        const FjValue* r = it->item;
        FmRelation* rel = (FmRelation*)fcAlloc(c, sizeof *rel);
        rel->kind = fmJsonStr(r, "kind"); if (!rel->kind) rel->kind = "";
        rel->from = fmJsonStr(r, "from"); if (!rel->from) rel->from = fmJsonStr(r, "one"); if (!rel->from) rel->from = "";
        rel->to = fmJsonStr(r, "to"); if (!rel->to) rel->to = fmJsonStr(r, "many"); if (!rel->to) rel->to = "";
        rel->key = fmJsonStr(r, "foreignKey");
        rel->navigation = fmJsonStr(r, "navigation");
        rel->inverse = fmJsonStr(r, "inverse");
        rel->joinTable = fmJsonStr(r, "joinTable");
        rel->fromColumn = fmJsonStr(r, "fromColumn");
        rel->toColumn = fmJsonStr(r, "toColumn");
        rel->line = it->line;
        fcListAdd(c, relations, rel);
    }

    /* ---- the checks ---- */
    {
        struct { FcList* list; const char* what; } groups[3] = { { enums, "enum" }, { types, "type" }, { g->entities, "entity" } };
        int gi;
        for (gi = 0; gi < 3; gi++)
            for (i = 0; i < groups[gi].list->count; i++) {
                void* x = groups[gi].list->items[i];
                const char* name = gi == 0 ? ((FmEnumDecl*)x)->name : gi == 1 ? ((FmTypeDecl*)x)->name : ((FmEntity*)x)->name;
                int line = gi == 0 ? ((FmEnumDecl*)x)->line : gi == 1 ? ((FmTypeDecl*)x)->line : ((FmEntity*)x)->line;
                if (!fmDataIsName(name)) fcListAdd(c, g->errors, fmDiag(c, line, fcFmt(c, "The %s '%s' needs a name that fire allows (letters, digits, _; not a keyword).", groups[gi].what, name)));
                else if (fcListHasStr(typeNames, name)) fcListAdd(c, g->errors, fmDiag(c, line, fcFmt(c, "'%s' is declared twice.", name)));
                else fcListAdd(c, typeNames, (void*)name);
            }
    }
    for (i = 0; i < enums->count; i++) fcListAdd(c, enumNames, (void*)((FmEnumDecl*)enums->items[i])->name);
    for (i = 0; i < types->count; i++) fcListAdd(c, recordNames, (void*)((FmTypeDecl*)types->items[i])->name);
    for (i = 0; i < g->entities->count; i++) fcListAdd(c, entityNames, (void*)((FmEntity*)g->entities->items[i])->name);
    for (i = 0; i < types->count; i++) { FmTypeDecl* t = (FmTypeDecl*)types->items[i]; fmDataCheckFields(g, t->name, t->fields, false, t->line, enumNames, recordNames); }
    for (i = 0; i < g->entities->count; i++) { FmEntity* e = (FmEntity*)g->entities->items[i]; fmDataCheckFields(g, e->name, e->fields, !e->view, e->line, enumNames, recordNames); }
    for (i = 0; i < g->entities->count; i++) {
        FmEntity* e = (FmEntity*)g->entities->items[i];
        bool fieldCheck = false;
        for (k = 0; k < e->fields->count; k++) if (((FmField*)e->fields->items[k])->check) fieldCheck = true;
        if (e->view && (e->checks->count > 0 || fieldCheck)) fcListAdd(c, g->errors, fmDiag(c, e->line, fcFmt(c, "The view '%s' has no checks (its query decides its rows).", e->name)));
    }
    for (i = 0; i < g->entities->count; i++) {
        FmEntity* e = (FmEntity*)g->entities->items[i];
        bool dup = false;
        for (k = 0; k < tables->count; k++) if (fmIEq((const char*)tables->items[k], e->table)) dup = true;
        if (dup) fcListAdd(c, g->errors, fmDiag(c, e->line, fcFmt(c, "Two entities have the table '%s'.", e->table)));
        else fcListAdd(c, tables, (void*)e->table);
    }

    /* ---- the relations: the fields they add to both sides ---- */
    /* (an entity declared twice is reported above: its first declaration counts here) */
    g->extraNames = fcListNew(c);
    g->extra = fcListNew(c);
    g->added = fcListNew(c);
    for (i = 0; i < g->entities->count; i++) {
        FmEntity* e = (FmEntity*)g->entities->items[i];
        FcList* names;
        if (fmDataGroupIndex(g, e->name) >= 0) continue;
        fcListAdd(c, g->extraNames, (void*)e->name);
        fcListAdd(c, g->extra, fcListNew(c));
        names = fcListNew(c);
        for (k = 0; k < e->fields->count; k++) if (!fcListHasStr(names, ((FmField*)e->fields->items[k])->name)) fcListAdd(c, names, (void*)((FmField*)e->fields->items[k])->name);
        fcListAdd(c, g->added, names);
    }
    for (i = 0; i < relations->count; i++) {
        FmRelation* r = (FmRelation*)relations->items[i];
        if (!fcListHasStr(entityNames, r->from) || !fcListHasStr(entityNames, r->to)) {
            fcListAdd(c, g->errors, fmDiag(c, r->line, fcFmt(c, "The relation of '%s' and '%s' needs two entities of the model.", r->from, r->to)));
            continue;
        }
        if (strcmp(r->kind, "one-to-one") == 0 || strcmp(r->kind, "one-to-many") == 0) {
            /* (one From has one To: the foreign key is a field of To, unique; one From has many To: the foreign key is a field of To) */
            bool one = strcmp(r->kind, "one-to-one") == 0;
            const char* fk = r->key ? r->key : fcFmt(c, "%sId", fmDataLower(c, r->from));
            FmEntity* to;
            if (!fmDataIsName(fk)) { fcListAdd(c, g->errors, fmDiag(c, r->line, fcFmt(c, "'%s' is no name for a foreign key.", fk))); continue; }
            to = fmDataEntity(g->entities, r->to);
            if (!fmDataHasField(to, fk)) {
                const char* column = strcmp(columnNaming, "snake") == 0 && strcmp(fmDataSnake(c, fk), fk) != 0 ? fcFmt(c, "[Column \"%s\"] ", fmDataSnake(c, fk)) : "";
                fmDataAdd(g, r->to, fk, fcFmt(c, "%s%s %s %s", column, one ? "[Unique]" : "[Index]", fmDataKeyType(g, r->from), fk), r->line);
            }
            if (r->navigation) fmDataAdd(g, r->from, r->navigation, one ? fcFmt(c, "[HasOne \"%s\"] %s %s", fk, r->to, r->navigation) : fcFmt(c, "[HasMany \"%s\"] List<%s> %s", fk, r->to, r->navigation), r->line);
            if (r->inverse) fmDataAdd(g, r->to, r->inverse, fcFmt(c, "[BelongsTo \"%s\"] %s %s", fk, r->from, r->inverse), r->line);
            if (!r->navigation && !r->inverse) fcListAdd(c, g->errors, fmDiag(c, r->line, fcFmt(c, "The relation of '%s' and '%s' needs a navigation field on one side at least (\"navigation\" or \"inverse\").", r->from, r->to)));
        } else if (strcmp(r->kind, "many-to-many") == 0) {
            const char* join = r->joinTable ? r->joinTable : fcFmt(c, "%s_%s", fmDataLower(c, r->from), fmDataLower(c, r->to));
            const char* a = r->fromColumn ? r->fromColumn : fcFmt(c, "%sId", fmDataLower(c, r->from));
            const char* b = r->toColumn ? r->toColumn : fcFmt(c, "%sId", fmDataLower(c, r->to));
            if (!r->navigation) { fcListAdd(c, g->errors, fmDiag(c, r->line, fcFmt(c, "The many-to-many relation of '%s' and '%s' needs \"navigation\" (the list in '%s').", r->from, r->to, r->from))); continue; }
            fmDataAdd(g, r->from, r->navigation, fcFmt(c, "[ManyToMany \"%s\", \"%s\", \"%s\"] List<%s> %s", join, a, b, r->to, r->navigation), r->line);
            if (r->inverse) fmDataAdd(g, r->to, r->inverse, fcFmt(c, "[ManyToMany \"%s\", \"%s\", \"%s\"] List<%s> %s", join, b, a, r->from, r->inverse), r->line);
        } else fcListAdd(c, g->errors, fmDiag(c, r->line, fcFmt(c, "The kind of a relation is one-to-one, one-to-many or many-to-many, not '%s'.", r->kind)));
    }
    if (g->errors->count > 0) fmThrow(c, g->errors);

    /* ---- the source ---- */
    fcBufStr(&sb, "");
    fcBufFmt(&sb, "// Generated by forge from %s (the data model, docs/data/designer.md) - not edited by hand.\n", fcBaseName(path));
    fcBufStr(&sb, "#import \"data\"\n\n");
    fcBufFmt(&sb, "namespace %s {\n", ns);
    for (i = 0; i < enums->count; i++) {
        FmEnumDecl* e = (FmEnumDecl*)enums->items[i];
        fmDataComment(c, &sb, e->comment, "    ");
        fcBufFmt(&sb, "    enum %s { ", e->name);
        for (k = 0; k < e->values->count; k++) { if (k) fcBufStr(&sb, ", "); fcBufStr(&sb, (const char*)e->values->items[k]); }
        fcBufStr(&sb, " }\n\n");
    }
    for (i = 0; i < types->count; i++) {
        FmTypeDecl* t = (FmTypeDecl*)types->items[i];
        fmDataComment(c, &sb, t->comment, "    ");
        fcBufFmt(&sb, "    record %s {\n", t->name);
        for (k = 0; k < t->fields->count; k++) fcBufFmt(&sb, "        %s\n", fmDataFieldLine(c, (FmField*)t->fields->items[k], columnNaming, false));
        /* (a record is written in its constructor: one parameter per field) */
        fcBufStr(&sb, "        construct(");
        for (k = 0; k < t->fields->count; k++) {
            FmField* fd = (FmField*)t->fields->items[k];
            if (k) fcBufStr(&sb, ", ");
            if (strcmp(fd->type, "bytes") == 0) fcBufFmt(&sb, "byte %s[]", fd->name); else fcBufFmt(&sb, "%s %s", fd->type, fd->name);
        }
        fcBufStr(&sb, ") {\n");
        for (k = 0; k < t->fields->count; k++) { FmField* fd = (FmField*)t->fields->items[k]; fcBufFmt(&sb, "            this.%s = %s\n", fd->name, fd->name); }
        fcBufStr(&sb, "        }\n    }\n\n");
    }
    for (i = 0; i < g->entities->count; i++) {
        FmEntity* e = (FmEntity*)g->entities->items[i];
        FcList* extra = (FcList*)g->extra->items[fmDataGroupIndex(g, e->name)];
        fmDataComment(c, &sb, e->comment, "    ");
        fcBufFmt(&sb, "    [Table \"%s\"]\n", e->table);
        if (e->view) fcBufFmt(&sb, "    [View %s]\n", fmDataLit(c, e->view));
        if (e->checks->count > 0) {
            const char* check;
            if (e->checks->count == 1) check = (const char*)e->checks->items[0];
            else {
                FcBuf cb = {0};
                fcBufStr(&cb, "");
                for (k = 0; k < e->checks->count; k++) { if (k) fcBufStr(&cb, " AND "); fcBufFmt(&cb, "(%s)", (const char*)e->checks->items[k]); }
                check = fcBufDone(c, &cb);
            }
            fcBufFmt(&sb, "    [Check %s]\n", fmDataLit(c, check));
        }
        /* (a class writes itself: p.Save(db), p.Delete(db), p.Reload(db) - Data.Entity) */
        fcBufFmt(&sb, "%s%s%s", e->record ? "    record " : "    class ", e->name, e->record ? " {\n" : " : Data.Entity {\n");
        for (k = 0; k < e->fields->count; k++) {
            FmField* fd = (FmField*)e->fields->items[k];
            if (fd->comment) fcBufFmt(&sb, "        // %s\n", fmReplace(c, fd->comment, "\n", " "));
            fcBufFmt(&sb, "        %s\n", fmDataFieldLine(c, fd, columnNaming, true));
        }
        for (k = 0; k < extra->count; k++) fcBufFmt(&sb, "        %s\n", (const char*)extra->items[k]);
        fcBufStr(&sb, "        construct() { }\n    }\n\n");
    }
    /* Model: what the model has */
    fcBufFmt(&sb, "    /// <summary>The data model %s: its entities and their tables.</summary>\n", ns);
    fcBufStr(&sb, "    class Model {\n");
    /* (the views after the tables: their queries name them) */
    ordered = fcListNew(c);
    for (i = 0; i < g->entities->count; i++) if (!((FmEntity*)g->entities->items[i])->view) fcListAdd(c, ordered, g->entities->items[i]);
    for (i = 0; i < g->entities->count; i++) if (((FmEntity*)g->entities->items[i])->view) fcListAdd(c, ordered, g->entities->items[i]);
    fcBufStr(&sb, "        static any Types() { return [");
    for (i = 0; i < ordered->count; i++) { if (i) fcBufStr(&sb, ", "); fcBufFmt(&sb, "\"%s.%s\"", ns, ((FmEntity*)ordered->items[i])->name); }
    fcBufStr(&sb, "] }\n");
    fcBufStr(&sb, "        static any Tables() { return [");
    for (i = 0; i < ordered->count; i++) { if (i) fcBufStr(&sb, ", "); fcBufFmt(&sb, "\"%s\"", ((FmEntity*)ordered->items[i])->table); }
    fcBufStr(&sb, "] }\n");
    fcBufStr(&sb, "    }\n\n");
    /* Context: the database through the model */
    fcBufFmt(&sb, "    /// <summary>A database through the model %s: a query per entity, sessions, the schema.</summary>\n", ns);
    fcBufStr(&sb, "    class Context {\n        Data.Connection db\n        construct(Data.Connection db) { this.db = db }\n");
    fcBufStr(&sb, "        Data.Connection Connection { get { return this.db } }\n");
    sets = fcListNew(c);
    fcListAdd(c, sets, (void*)"Connection"); fcListAdd(c, sets, (void*)"Session"); fcListAdd(c, sets, (void*)"Ensure");
    fcListAdd(c, sets, (void*)"Migrate"); fcListAdd(c, sets, (void*)"Script"); fcListAdd(c, sets, (void*)"db");
    for (i = 0; i < g->entities->count; i++) {
        FmEntity* e = (FmEntity*)g->entities->items[i];
        const char* set = e->set ? e->set : fmDataPlural(c, e->name);
        if (!fmDataIsName(set) || fcListHasStr(sets, set)) {
            fcBufFree(&sb);
            fmThrow1(c, e->line, fcFmt(c, "The query of '%s' in the Context would be '%s': give the entity another \"set\".", e->name, set));
        }
        fcListAdd(c, sets, (void*)set);
        fcBufFmt(&sb, "        Data.Query<%s> %s { get { return this.db.From<%s>() } }\n", e->name, set, e->name);
    }
    fcBufStr(&sb, "        Data.Session Session() { return this.db.Session() }\n");
    fcBufStr(&sb, "        // (the tables of the model: made when they are not there, missing columns and indexes added - never dropped)\n");
    fcBufStr(&sb, "        undefined Ensure() {\n");
    for (i = 0; i < ordered->count; i++) fcBufFmt(&sb, "            this.db.Ensure(\"%s.%s\")\n", ns, ((FmEntity*)ordered->items[i])->name);
    fcBufStr(&sb, "        }\n");
    fcBufStr(&sb, "        int Migrate() { return this.db.Migrate() }\n");
    fcBufStr(&sb, "        // (the SQL that makes the tables of the model in an empty database, in the dialect of the connection)\n");
    fcBufFmt(&sb, "        string Script() { return this.db.CreateScriptOf(%s.Model.Types()) }\n    }\n}\n", ns);
    return fcBufDone(c, &sb);
}
