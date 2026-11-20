/* native/forge/tools/testdriver.c - the driver of `forge test` (Phase D 4, commit b833e71b): src/fire.Compiler/TestDriver.cs (Generate, EmitCase, Escape, Quote),
 * TestFilter.cs and the linker's part (Linker.CompileAndLink with `TestDriver` set: the statements at the top level of the program's files are left out, the
 * driver is the last source, "<tests>"). The driver is fire source: every class with a `[Test]` method gets one object; `[PrepareTesting]` once, for every test
 * (and every set of `[Parameters]`) `[PrepareTest]`, the test and `[CleanTest]`, at the end `[CleanTesting]`; it reports with lines that start with
 * TD_MARKER (tools/testrunner.c reads them). */

#define TD_MARKER "##fire-test##"

/* TestFilter: patterns separated by ',' or ';', each `Class.Method` or `Class`, '*' any text (Regex.Escape, then \* -> .*: a match of the whole key) */
typedef struct TdFilter { FcList* patterns; } TdFilter;
#if defined(__GNUC__) || defined(__clang__)
__attribute__((noinline))   /* (called next to a setjmp) */
#endif
static TdFilter* tdFilterParse(FcCtx* c, const char* text) {
    TdFilter* f = (TdFilter*)fcAlloc(c, sizeof *f);
    const char* p = text;
    f->patterns = fcListNew(c);
    for (;;) {
        const char* end = p;
        const char *a, *b;
        while (*end && *end != ',' && *end != ';') end++;
        a = p; b = end;
        /* (string.Trim: white space of .NET) */
        while (a < b && (*a == ' ' || (*a >= 0x09 && *a <= 0x0D))) a++;
        while (b > a && (b[-1] == ' ' || (b[-1] >= 0x09 && b[-1] <= 0x0D))) b--;
        if (b > a) fcListAdd(c, f->patterns, (void*)fcStrN(c, a, (size_t)(b - a)));
        if (!*end) break;
        p = end + 1;
    }
    return f;
}
static bool tdWildcard(const char* pat, const char* s) {
    if (!*pat) return !*s;
    if (*pat == '*') { do { if (tdWildcard(pat + 1, s)) return true; } while (*s++); return false; }
    return *s == *pat && tdWildcard(pat + 1, s + 1);
}
/* Includes: the key `Class.Method` or the class matches a pattern */
static bool tdFilterIncludes(FcCtx* c, const TdFilter* f, const char* cls, const char* method) {
    const char* key = fcFmt(c, "%s.%s", cls, method);
    int i;
    for (i = 0; i < f->patterns->count; i++) {
        const char* p = (const char*)f->patterns->items[i];
        if (tdWildcard(p, key) || tdWildcard(p, cls)) return true;
    }
    return false;
}

static bool tdHas(const FcMethodDecl* m, const char* attribute) {
    int i;
    if (!m->attributes) return false;
    for (i = 0; i < m->attributes->count; i++) if (strcmp(((const FcAttributeDecl*)m->attributes->items[i])->name, attribute) == 0) return true;
    return false;
}
static const FcAttributeDecl* tdFirst(const FcMethodDecl* m, const char* attribute) {
    int i;
    for (i = 0; m->attributes && i < m->attributes->count; i++) if (strcmp(((const FcAttributeDecl*)m->attributes->items[i])->name, attribute) == 0) return (const FcAttributeDecl*)m->attributes->items[i];
    return NULL;
}
static int tdParamCount(const FcMethodDecl* m) { return m->params ? m->params->count : 0; }
static bool tdKnown(const char* name) {
    return strcmp(name, "Test") == 0 || strcmp(name, "Parameters") == 0 || strcmp(name, "PrepareTest") == 0 || strcmp(name, "CleanTest") == 0
        || strcmp(name, "PrepareTesting") == 0 || strcmp(name, "CleanTesting") == 0;
}
/* a string literal argument of an attribute, else NULL */
static const char* tdStringArg(const FcAttributeDecl* a, int i) {
    const FcExpr* e;
    if (!a->args || a->args->count <= i) return NULL;
    e = (const FcExpr*)a->args->items[i];
    if (e->kind != EK_LiteralExpr || ((const FcLiteralExpr*)e)->value.kind != VK_String) return NULL;
    return ((const FcLiteralExpr*)e)->value.s;
}

/* Escape: text inside a fire string literal (and without the field separator of the protocol) */
static const char* tdEscape(FcCtx* c, const char* text) {
    FcBuf b = {0};
    const char* p;
    fcBufStr(&b, "");
    for (p = text; *p; p++) {
        if (*p == '\\') fcBufStr(&b, "\\\\");
        else if (*p == '"') fcBufStr(&b, "\\\"");
        else if (*p == '\n') fcBufChar(&b, ' ');
        else if (*p == '\r') continue;
        else if (*p == '|') fcBufChar(&b, '/');
        else fcBufChar(&b, *p);
    }
    return fcBufDone(c, &b);
}
static const char* tdQuote(FcCtx* c, const char* text) { return fcFmt(c, "\"%s\"", tdEscape(c, text)); }

typedef const char* (*TdFileOf)(FcCtx* c, void* state, int source);

static const char* tdCall(FcCtx* c, const FcClassDecl* cls, const FcMethodDecl* m, const char* args) { return fcFmt(c, "%s.%s(%s)", m->isStatic ? cls->name : "o", m->name, args); }

static void tdEmitCase(FcCtx* c, FcBuf* sb, const char* indent, const FcClassDecl* cls, const FcMethodDecl* test, const char* nameExpr, const char* description,
                       FcList* prepare, const char* call, FcList* clean) {
    int i;
    fcBufFmt(sb, "%s__Tests.Say(\"S|%s|%s|\" + (%s).Replace(\"|\", \"/\") + \"|%s\")\n", indent, tdEscape(c, cls->name), tdEscape(c, test->name), nameExpr, tdEscape(c, description));
    fcBufFmt(sb, "%stry {\n", indent);
    for (i = 0; i < prepare->count; i++) fcBufFmt(sb, "%s    %s\n", indent, tdCall(c, cls, (const FcMethodDecl*)prepare->items[i], ""));
    fcBufFmt(sb, "%s    %s\n", indent, call);
    fcBufFmt(sb, "%s    __Tests.Say(\"P\")\n", indent);
    fcBufFmt(sb, "%s} catch (e) { __Tests.Failed(e) }\n", indent);
    for (i = 0; i < clean->count; i++) fcBufFmt(sb, "%stry { %s } catch (e) { __Tests.Say(\"C|\" + __Tests.Describe(e)) }\n", indent, tdCall(c, cls, (const FcMethodDecl*)clean->items[i], ""));
}

/* Generate: the driver for the classes of `program` from source `firstUserSource` on; what is wrong with an attribute goes to `problems` (with file and line);
 * `testCount`: the tests found (a set of parameters counts once); `filter`: only these tests run (NULL: all) */
static const char* tdGenerate(FcCtx* c, FcList* program, int firstUserSource, TdFileOf fileOf, void* state, FcList* problems, int* testCount, const TdFilter* filter) {
    FcBuf sb = {0};
    FcList* runs = fcListNew(c);
    int classIndex = 0, si, i, k;
    *testCount = 0;
    fcBufStr(&sb, "class __Tests {\n");
    fcBufStr(&sb, "    static undefined Say(string text) { print(\"" TD_MARKER "\" + text.Replace(\"\\r\", \"\").Replace(\"\\n\", \"\\\\n\")) }\n");
    fcBufStr(&sb, "    static string Describe(any e) {\n");
    fcBufStr(&sb, "        if (e is of Exception) { if (e.message == undefined) { return \"\" } return \"\" + e.message }\n");
    fcBufStr(&sb, "        return \"\" + e\n");
    fcBufStr(&sb, "    }\n");
    fcBufStr(&sb, "    static undefined Failed(any e) {\n");
    fcBufStr(&sb, "        if (e is of Assertion) { __Tests.Say(\"F|\" + __Tests.Describe(e)) } else { __Tests.Say(\"E|\" + __Tests.Describe(e)) }\n");
    fcBufStr(&sb, "    }\n");
    for (si = 0; si < program->count; si++) {
        const FcStmt* s = (const FcStmt*)program->items[si];
        const FcClassDecl* cls;
        FcList *methods, *tests, *prepare, *clean;
        const char* file;
        const char* run;
        bool any = false;
        if (s->kind != SK_ClassDecl || s->source < firstUserSource) continue;
        cls = (const FcClassDecl*)s;
        methods = fcListNew(c);
        for (i = 0; cls->members && i < cls->members->count; i++) if (((const FcStmt*)cls->members->items[i])->kind == SK_MethodDecl) fcListAdd(c, methods, cls->members->items[i]);
        for (i = 0; i < methods->count && !any; i++) {
            const FcMethodDecl* m = (const FcMethodDecl*)methods->items[i];
            if (tdHas(m, "Test") && (!filter || tdFilterIncludes(c, filter, cls->name, m->name))) any = true;
        }
        if (filter && !any) continue;
        file = fileOf(c, state, cls->h.source);
        if (!file) file = "?";
#define TD_WHERE(line) fcFmt(c, "%s:%d", file, (line))
        for (i = 0; i < methods->count; i++) {
            const FcMethodDecl* m = (const FcMethodDecl*)methods->items[i];
            for (k = 0; m->attributes && k < m->attributes->count; k++) {
                const FcAttributeDecl* a = (const FcAttributeDecl*)m->attributes->items[k];
                if (tdKnown(a->name) && strcmp(a->name, "Test") != 0 && strcmp(a->name, "Parameters") != 0 && tdParamCount(m) > 0)
                    fcListAdd(c, problems, (void*)fcFmt(c, "%s: [%s] needs a method without parameters ('%s' has %d).", TD_WHERE(a->line), a->name, m->name, tdParamCount(m)));
            }
        }
        for (i = 0; i < methods->count; i++) {
            const FcMethodDecl* m = (const FcMethodDecl*)methods->items[i];
            if (tdHas(m, "Parameters") && !tdHas(m, "Test")) fcListAdd(c, problems, (void*)fcFmt(c, "%s: '%s.%s' has [Parameters] but no [Test].", TD_WHERE(m->h.line), cls->name, m->name));
        }
        tests = fcListNew(c);
        for (i = 0; i < methods->count; i++) {
            const FcMethodDecl* m = (const FcMethodDecl*)methods->items[i];
            if (tdHas(m, "Test") && (!filter || tdFilterIncludes(c, filter, cls->name, m->name))) fcListAdd(c, tests, (void*)m);
        }
        if (tests->count == 0) continue;
        if (cls->typeParams && cls->typeParams->count > 0) {
            fcListAdd(c, problems, (void*)fcFmt(c, "%s: the test class '%s' is generic - a test class needs a plain `new %s()`.", TD_WHERE(cls->h.line), cls->name, cls->name));
            continue;
        }
        prepare = fcListNew(c);
        clean = fcListNew(c);
        for (i = 0; i < methods->count; i++) {
            const FcMethodDecl* m = (const FcMethodDecl*)methods->items[i];
            if (tdHas(m, "PrepareTest") && tdParamCount(m) == 0) fcListAdd(c, prepare, (void*)m);
            if (tdHas(m, "CleanTest") && tdParamCount(m) == 0) fcListAdd(c, clean, (void*)m);
        }
        run = fcFmt(c, "Class%d", classIndex++);
        fcListAdd(c, runs, (void*)run);
        fcBufFmt(&sb, "    static undefined %s() {\n", run);
        fcBufFmt(&sb, "        try { __Tests.%sBody(new %s()) } catch (e) { __Tests.Say(\"X|%s|construct|\" + __Tests.Describe(e)) }\n", run, cls->name, tdEscape(c, cls->name));
        fcBufStr(&sb, "    }\n");
        fcBufFmt(&sb, "    static undefined %sBody(%s o) {\n", run, cls->name);
        for (i = 0; i < methods->count; i++) {
            const FcMethodDecl* m = (const FcMethodDecl*)methods->items[i];
            if (tdHas(m, "PrepareTesting") && tdParamCount(m) == 0)
                fcBufFmt(&sb, "        try { %s } catch (e) { __Tests.Say(\"X|%s|PrepareTesting|\" + __Tests.Describe(e)); return }\n", tdCall(c, cls, m, ""), tdEscape(c, cls->name));
        }
        for (i = 0; i < tests->count; i++) {
            const FcMethodDecl* test = (const FcMethodDecl*)tests->items[i];
            const FcAttributeDecl* testAttr = tdFirst(test, "Test");
            const char* title = tdStringArg(testAttr, 0);
            const char* description = tdStringArg(testAttr, 1);
            FcList* sets = fcListNew(c);
            int argCount = testAttr->args ? testAttr->args->count : 0, q;
            if (!title) title = test->name;
            if (!description) description = "";
            if (argCount > 2) fcListAdd(c, problems, (void*)fcFmt(c, "%s: [Test] takes a title and a description, not %d arguments.", TD_WHERE(testAttr->line), argCount));
            for (k = 0; k < test->attributes->count; k++) if (strcmp(((const FcAttributeDecl*)test->attributes->items[k])->name, "Parameters") == 0) fcListAdd(c, sets, test->attributes->items[k]);
            if (sets->count == 0) {
                if (tdParamCount(test) > 0) {
                    fcListAdd(c, problems, (void*)fcFmt(c, "%s: the test '%s.%s' takes %d parameters - give them with [Parameters ...].", TD_WHERE(test->h.line), cls->name, test->name, tdParamCount(test)));
                    continue;
                }
                (*testCount)++;
                tdEmitCase(c, &sb, "        ", cls, test, tdQuote(c, title), description, prepare, tdCall(c, cls, test, ""), clean);
                continue;
            }
            for (q = 0; q < sets->count; q++) {
                const FcAttributeDecl* set = (const FcAttributeDecl*)sets->items[q];
                int n = set->args ? set->args->count : 0, depth = 0, j;
                FcBuf indent = {0}, values = {0}, shown = {0};
                const char* name;
                if (n != tdParamCount(test)) {
                    fcListAdd(c, problems, (void*)fcFmt(c, "%s: [Parameters] gives %d values, '%s' takes %d.", TD_WHERE(set->line), n, test->name, tdParamCount(test)));
                    continue;
                }
                (*testCount)++;
                /* a range (`1..5`) runs the test for each of its numbers; with several ranges for each combination */
                fcBufStr(&indent, "        ");
                fcBufStr(&values, "");
                fcBufStr(&shown, "");
                for (j = 0; j < n; j++) {
                    const char* text = (const char*)set->argTexts->items[j];
                    if (j > 0) { fcBufStr(&values, ", "); fcBufStr(&shown, " + \", \" + "); }
                    if (((const FcExpr*)set->args->items[j])->kind == EK_RangeExpr) {
                        fcBufFmt(&sb, "%sforeach (p%d in %s) {\n", indent.p, j, text);
                        fcBufStr(&indent, "    ");
                        depth++;
                        fcBufFmt(&values, "p%d", j);
                        fcBufFmt(&shown, "\"\" + p%d", j);
                    } else {
                        fcBufStr(&values, text);
                        fcBufStr(&shown, tdQuote(c, text));
                    }
                }
                name = fcFmt(c, "%s + %s + \")\"", tdQuote(c, fcFmt(c, "%s(", title)), shown.p);
                tdEmitCase(c, &sb, indent.p, cls, test, name, description, prepare, tdCall(c, cls, test, values.p), clean);
                for (j = 0; j < depth; j++) { indent.len -= 4; indent.p[indent.len] = 0; fcBufFmt(&sb, "%s}\n", indent.p); }
                fcBufFree(&indent); fcBufFree(&values); fcBufFree(&shown);
            }
        }
        for (i = 0; i < methods->count; i++) {
            const FcMethodDecl* m = (const FcMethodDecl*)methods->items[i];
            if (tdHas(m, "CleanTesting") && tdParamCount(m) == 0)
                fcBufFmt(&sb, "        try { %s } catch (e) { __Tests.Say(\"X|%s|CleanTesting|\" + __Tests.Describe(e)) }\n", tdCall(c, cls, m, ""), tdEscape(c, cls->name));
        }
        fcBufStr(&sb, "    }\n");
#undef TD_WHERE
    }
    fcBufStr(&sb, "}\n");
    for (i = 0; i < runs->count; i++) fcBufFmt(&sb, "__Tests.%s()\n", (const char*)runs->items[i]);
    fcBufStr(&sb, "__Tests.Say(\"D\")\n");
    return fcBufDone(c, &sb);
}

/* the linker's part (Linker.CompileAndLink with TestDriver set; called after ParseMultiple of every way the C linker goes): the statements at the top level of
 * the program's files are left out (IsDeclaration), the driver is parsed as the last source "<tests>" (Parser.ParseSource) */
bool fcTestDriverApply(FcCtx* c, FcList* program, FcList* processed, int firstUserSource) {
    FcList* kept;
    const char* text;
    FcProcessed* driver;
    FcU16 u = {0};
    uint16_t* copy;
    FcTokens tokens;
    FcList* stmts;
    int i;
    if (!c->testDriver) return false;
    kept = fcListNew(c);
    for (i = 0; i < program->count; i++) {
        const FcStmt* s = (const FcStmt*)program->items[i];
        if (s->source >= firstUserSource && !pjIsDeclaration(s)) continue;
        fcListAdd(c, kept, (void*)s);
    }
    program->count = 0;
    for (i = 0; i < kept->count; i++) fcListAdd(c, program, kept->items[i]);
    text = c->testDriver(c, c->testDriverState, program, firstUserSource);
    fcU16FromFc8(text, &u);
    copy = (uint16_t*)fcAlloc(c, sizeof(uint16_t) * (size_t)(u.len + 1));
    if (u.len) memcpy(copy, u.p, sizeof(uint16_t) * (size_t)u.len);
    driver = (FcProcessed*)fcAlloc(c, sizeof *driver);
    memset(driver, 0, sizeof *driver);
    driver->source = copy;
    driver->length = u.len;
    driver->usings = fcListNew(c);
    driver->name = "<tests>";
    fcU16Free(&u);
    fcListAdd(c, processed, driver);
    tokens = fcTokenize(c, driver->source, driver->length);
    FindNamespaces(c, &tokens, &c->declaredNamespaces);
    stmts = ParseProgram(ForSource(c, tokens, driver, processed->count - 1));
    for (i = 0; i < stmts->count; i++) fcListAdd(c, program, stmts->items[i]);
    return true;
}
