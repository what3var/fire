/* native/forge/project/projectfiles.c - which files a project consists of (Phase D 3, commit de45e7e4): src/fire.Project/ProjectFiles.cs (PatternToRegex,
 * Walk, Resolve, Folders, ContentFiles, Expand, Relative - not HiddenEntries, which only the explorer of spark shows), ProjectFolders.cs (ProjectFolderMap
 * with StateOf, Normalize, Matching, Scan; GitIgnore - not GitignoreSync and ProjectFolderEdits, which change files) and Notes.cs (ProjectNotes.Folder).
 * The regular expressions that a pattern becomes (`*` -> [^/]*, `**` -> .*, `**` / -> (?:.* /)?, `?` -> [^/], `{a,b}` -> (?:a|b), the sets of a .gitignore)
 * are matched by a small backtracking matcher over the same tree; on Linux, as there, case-sensitively. */

/* ---- the patterns as regular expressions ---- */
enum { PG_LIT, PG_ONE, PG_STAR, PG_ANY, PG_DIRS, PG_SET, PG_GROUP };
typedef struct PgNode { int kind; uint16_t ch; const char* set; bool negate; FcList* alts; /* PG_GROUP: FcList* of FcList* (sequences of PgNode*) */ } PgNode;
typedef struct PgCont { const FcList* seq; int at; const struct PgCont* next; } PgCont;

static PgNode* pgNode(FcCtx* c, int kind) { PgNode* n = (PgNode*)fcAlloc(c, sizeof *n); n->kind = kind; return n; }
/* the length in bytes of the UTF-8 sequence at s (one UTF-16 unit of fc8; '.' and [^/] take one) */
static int pgCharLen(const char* s) {
    const unsigned char* p = (const unsigned char*)s;
    fc8Next(&p);
    return (int)((const char*)p - s);
}
static bool pgSetHas(const char* set, uint16_t u) {
    const unsigned char* p = (const unsigned char*)set;
    while (*p) {
        uint16_t a = fc8Next(&p);
        if (*p == '-' && p[1]) {
            uint16_t b;
            p++;
            b = fc8Next(&p);
            if (u >= a && u <= b) return true;
            continue;
        }
        if (a == u) return true;
    }
    return false;
}
static bool pgMatch(const FcList* seq, int at, const char* s, const PgCont* k) {
    const PgNode* n;
    if (at == seq->count) return k ? pgMatch(k->seq, k->at, s, k->next) : *s == 0;
    n = (const PgNode*)seq->items[at];
    switch (n->kind) {
        case PG_LIT: {
            const unsigned char* p = (const unsigned char*)s;
            if (!*s || fc8Next(&p) != n->ch) return false;
            return pgMatch(seq, at + 1, (const char*)p, k);
        }
        case PG_ONE: if (!*s || *s == '/') return false; return pgMatch(seq, at + 1, s + pgCharLen(s), k);
        case PG_SET: {
            const unsigned char* p = (const unsigned char*)s;
            uint16_t u;
            if (!*s) return false;
            u = fc8Next(&p);
            if (u == '\n' && n->negate) return pgSetHas(n->set, u) == !n->negate && pgMatch(seq, at + 1, (const char*)p, k);
            if (pgSetHas(n->set, u) == n->negate) return false;
            return pgMatch(seq, at + 1, (const char*)p, k);
        }
        case PG_STAR: case PG_ANY: {
            /* greedy: the longest run first */
            const char* ends[4096];
            int count = 0;
            const char* p = s;
            ends[count++] = p;
            while (*p && (n->kind == PG_ANY ? *p != '\n' : *p != '/')) {
                p += pgCharLen(p);
                if (count < 4096) ends[count++] = p;
            }
            while (count-- > 0) if (pgMatch(seq, at + 1, ends[count], k)) return true;
            return false;
        }
        case PG_DIRS: {
            /* (?:.*\/)?: a part up to a '/' (the last one first), else nothing */
            const char* p;
            const char* last = NULL;
            for (p = s; *p && *p != '\n'; p++) if (*p == '/') last = p;
            while (last) {
                const char* q;
                if (pgMatch(seq, at + 1, last + 1, k)) return true;
                for (q = last - 1, last = NULL; q >= s; q--) if (*q == '/') { last = q; break; }
            }
            return pgMatch(seq, at + 1, s, k);
        }
        case PG_GROUP: {
            PgCont next = { seq, at + 1, k };
            int i;
            for (i = 0; i < n->alts->count; i++) if (pgMatch((const FcList*)n->alts->items[i], 0, s, &next)) return true;
            return false;
        }
    }
    return false;
}
static void pgAddLit(FcCtx* c, FcList* seq, const char** p) {
    const unsigned char* q = (const unsigned char*)*p;
    PgNode* n = pgNode(c, PG_LIT);
    n->ch = fc8Next(&q);
    *p = (const char*)q;
    fcListAdd(c, seq, n);
}
/* ProjectFiles.PatternToRegex */
static FcList* pgCompileGlob(FcCtx* c, const char* pattern) {
    const char* p = pjReplaceChar(c, pattern, '\\', '/');
    FcList* stack[256];   /* the open groups: the group's node list of alternatives is the list below */
    PgNode* groups[256];
    int depth = 0;
    FcList* seq = fcListNew(c);
    while (fcStartsWith(p, "./")) p += 2;
    while (*p) {
        if (*p == '*') {
            if (p[1] == '*') {
                p += 2;
                if (*p == '/') { p++; fcListAdd(c, seq, pgNode(c, PG_DIRS)); }
                else fcListAdd(c, seq, pgNode(c, PG_ANY));
            } else { p++; fcListAdd(c, seq, pgNode(c, PG_STAR)); }
        }
        else if (*p == '?') { p++; fcListAdd(c, seq, pgNode(c, PG_ONE)); }
        else if (*p == '{' && depth < 256) {
            PgNode* g = pgNode(c, PG_GROUP);
            g->alts = fcListNew(c);
            fcListAdd(c, seq, g);
            stack[depth] = seq;
            groups[depth++] = g;
            seq = fcListNew(c);
            fcListAdd(c, g->alts, seq);
            p++;
        }
        else if (*p == '}' && depth > 0) { seq = stack[--depth]; p++; }
        else if (*p == ',' && depth > 0) { seq = fcListNew(c); fcListAdd(c, groups[depth - 1]->alts, seq); p++; }
        else pgAddLit(c, seq, &p);
    }
    /* (an unclosed `{`: .NET cannot make the expression - here the rest is the group) */
    while (depth > 0) seq = stack[--depth];
    return seq;
}
static bool pjPatternMatch(FcCtx* c, const char* pattern, const char* rel) { return pgMatch(pgCompileGlob(c, pattern), 0, rel, NULL); }

/* GitIgnore.ToRegex */
static FcList* pgCompileGitignore(FcCtx* c, const char* pattern, bool anchored) {
    FcList* seq = fcListNew(c);
    const char* p = pattern;
    if (!anchored) fcListAdd(c, seq, pgNode(c, PG_DIRS));
    while (*p) {
        if (*p == '*') {
            if (p[1] == '*') {
                p += 2;
                if (*p == '/') { p++; fcListAdd(c, seq, pgNode(c, PG_DIRS)); }
                else fcListAdd(c, seq, pgNode(c, PG_ANY));
            } else { p++; fcListAdd(c, seq, pgNode(c, PG_STAR)); }
        }
        else if (*p == '?') { p++; fcListAdd(c, seq, pgNode(c, PG_ONE)); }
        else if (*p == '[') {
            const char* end = p[1] ? strchr(p + 2, ']') : NULL;
            PgNode* n;
            const char* set;
            if (!end) { p++; { PgNode* l = pgNode(c, PG_LIT); l->ch = '['; fcListAdd(c, seq, l); } continue; }
            set = fcStrN(c, p + 1, (size_t)(end - p - 1));
            n = pgNode(c, PG_SET);
            if (*set == '!') { n->negate = true; set++; }
            else if (*set == '^') { n->negate = true; set++; }
            n->set = set;
            fcListAdd(c, seq, n);
            p = end + 1;
        }
        else if (*p == '\\' && p[1]) { p++; pgAddLit(c, seq, &p); }
        else pgAddLit(c, seq, &p);
    }
    return seq;
}

/* ---- the folders and files below a folder ---- */
static bool pjIsDirectory(const char* path) { struct stat st; return stat(path, &st) == 0 && S_ISDIR(st.st_mode); }
/* Directory.EnumerateFiles / EnumerateDirectories (the names, in the order of the directory) */
static void pjEnumerate(FcCtx* c, const char* dir, FcList* files, FcList* folders) {
    DIR* d = opendir(dir);
    struct dirent* e;
    if (!d) return;
    while ((e = readdir(d)) != NULL) {
        const char* full;
        if (streq0(e->d_name, ".") || streq0(e->d_name, "..")) continue;
        full = nbCombine(c, dir, e->d_name);
        if (pjIsDirectory(full)) { if (folders) fcListAdd(c, folders, (void*)fcStr(c, e->d_name)); }
        else if (files) fcListAdd(c, files, (void*)fcStr(c, e->d_name));
    }
    closedir(d);
}
/* Directory.EnumerateFiles(dir, "*" + ext).Any() (case-sensitive on Linux) */
static bool pjHasFileWith(FcCtx* c, const char* dir, const char* ext) {
    FcList* files = fcListNew(c);
    int i;
    pjEnumerate(c, dir, files, NULL);
    for (i = 0; i < files->count; i++) if (fcEndsWith((const char*)files->items[i], ext)) return true;
    return false;
}
#define PJ_PUBLISH_MARKER ".firepublish"
static bool pjIsPublishFolder(FcCtx* c, const char* folder) { return nbFileExists(nbCombine(c, folder, PJ_PUBLISH_MARKER)); }
static const char* const pjSkippedFolders[] = { "bin", "obj", ".git", ".vs", "node_modules" };
static bool pjIsHiddenName(const char* name) {
    size_t i;
    for (i = 0; i < 5; i++) if (pjIEq(name, pjSkippedFolders[i])) return true;
    return name[0] == '.';
}

/* ProjectFiles.Walk: every file below `directory` (relative, `/`) without the folders of builds and of other projects */
static void pjWalkInto(FcCtx* c, const char* dir, const char* rel, FcList* found) {
    FcList* files = fcListNew(c);
    FcList* folders = fcListNew(c);
    int i;
    pjEnumerate(c, dir, files, folders);
    for (i = 0; i < files->count; i++) fcListAdd(c, found, (void*)fcFmt(c, "%s%s", rel, (const char*)files->items[i]));
    for (i = 0; i < folders->count; i++) {
        const char* name = (const char*)folders->items[i];
        const char* folder = nbCombine(c, dir, name);
        if (pjIsHiddenName(name)) continue;
        if (pjHasFileWith(c, folder, ".fireproj")) continue;
        if (pjIsPublishFolder(c, folder)) continue;
        pjWalkInto(c, folder, fcFmt(c, "%s%s/", rel, name), found);
    }
}
static FcList* pjWalk(FcCtx* c, const char* directory) { FcList* found = fcListNew(c); pjWalkInto(c, directory, "", found); return found; }

/* ---- GitIgnore ---- */
typedef struct PjGitRule { const char* baseDir; FcList* regex; bool negate; bool directoryOnly; const char* line; } PjGitRule;
typedef struct PjGitIgnore { FcList* rules; } PjGitIgnore;
static const char* pjTrimEndSep(FcCtx* c, const char* p) {
    size_t n = strlen(p);
    while (n > 1 && p[n - 1] == '/') n--;
    return fcStrN(c, p, n);
}
static const char* pjParentDir(FcCtx* c, const char* d) {   /* Path.GetDirectoryName of a full path (NULL for the root) */
    if (fcIsRootDir(d)) return NULL;
    return fcDirectoryName(c, d);
}
static const char* pjRepositoryRoot(FcCtx* c, const char* path) {
    const char* d;
    for (d = pjFull(c, path, "."); d; d = pjParentDir(c, d)) {
        const char* git = nbCombine(c, d, ".git");
        if (nbDirExists(git) || nbFileExists(git)) return d;
    }
    return NULL;
}
static FcList* pjFoldersAbove(FcCtx* c, const char* projectDir) {
    const char* dir = pjTrimEndSep(c, pjFull(c, projectDir, "."));
    const char* root = pjRepositoryRoot(c, dir);
    FcList* chain = fcListNew(c);
    FcList* r = fcListNew(c);
    const char* d;
    int i;
    for (d = dir; d; d = pjParentDir(c, d)) {
        fcListAdd(c, chain, (void*)d);
        if (!root || streq0(d, root)) break;
    }
    for (i = chain->count - 1; i >= 0; i--) fcListAdd(c, r, chain->items[i]);
    return r;
}
static PjGitIgnore* pjGitWith(FcCtx* c, const PjGitIgnore* g, const char* baseDir, const char* text) {
    PjGitIgnore* r = (PjGitIgnore*)fcAlloc(c, sizeof *r);
    FcList* lines = fgSplit(c, fgReplace(c, text, "\r\n", "\n"), '\n');
    int i;
    r->rules = fcListCopy(c, g->rules);
    for (i = 0; i < lines->count; i++) {
        const char* raw = (const char*)lines->items[i];
        size_t n = strlen(raw);
        const char* line;
        const char* p;
        bool negate, dirOnly, anchored;
        PjGitRule* rule;
        /* TrimEnd: the white space at the end */
        while (n > 0 && (raw[n - 1] == ' ' || raw[n - 1] == '\t' || raw[n - 1] == '\r' || raw[n - 1] == '\n' || raw[n - 1] == '\f' || raw[n - 1] == '\v')) n--;
        line = fcStrN(c, raw, n);
        if (!*line || line[0] == '#') continue;
        p = line;
        negate = *p == '!';
        if (negate) p++;
        if (fcStartsWith(p, "\\#") || fcStartsWith(p, "\\!")) p++;
        dirOnly = fcEndsWith(p, "/");
        n = strlen(p);
        while (n > 0 && p[n - 1] == '/') n--;
        p = fcStrN(c, p, n);
        if (!*p) continue;
        anchored = strchr(p, '/') != NULL;
        while (*p == '/') p++;
        if (!*p) continue;
        rule = (PjGitRule*)fcAlloc(c, sizeof *rule);
        rule->baseDir = pjTrimEndSep(c, pjFull(c, baseDir, "."));
        rule->regex = pgCompileGitignore(c, p, anchored);
        rule->negate = negate;
        rule->directoryOnly = dirOnly;
        rule->line = line;
        fcListAdd(c, r->rules, rule);
    }
    return r;
}
static PjGitIgnore* pjGitWithFolder(FcCtx* c, PjGitIgnore* g, const char* folder) {
    const char* file = nbCombine(c, folder, ".gitignore");
    const char* text;
    if (!nbFileExists(file) || !(text = pjReadAllText(c, file))) return g;
    return pjGitWith(c, g, folder, text);
}
static PjGitIgnore* pjGitForProject(FcCtx* c, const char* projectDir) {
    PjGitIgnore* g = (PjGitIgnore*)fcAlloc(c, sizeof *g);
    FcList* above = pjFoldersAbove(c, projectDir);
    int i;
    g->rules = fcListNew(c);
    for (i = 0; i < above->count; i++) g = pjGitWithFolder(c, g, (const char*)above->items[i]);
    return g;
}
/* GitIgnore.Match: the line that ignores the path (NULL: none) */
static const char* pjGitMatch(FcCtx* c, const PjGitIgnore* g, const char* fullPath, bool isDirectory) {
    const char* full = pjTrimEndSep(c, pjFull(c, fullPath, "."));
    const char* decided = NULL;
    int i;
    for (i = 0; i < g->rules->count; i++) {
        const PjGitRule* r = (const PjGitRule*)g->rules->items[i];
        const char* rel;
        size_t n = strlen(r->baseDir);
        if (r->directoryOnly && !isDirectory) continue;
        if (!(strncmp(full, r->baseDir, n) == 0 && full[n] == '/')) continue;
        rel = full + n + 1;
        if (pgMatch(r->regex, 0, rel, NULL)) decided = r->negate ? NULL : r->line;
    }
    return decided;
}

/* ---- ProjectFolderMap ---- */
enum { PJ_FS_INCLUDED, PJ_FS_UNDECIDED, PJ_FS_EXCLUDED };
enum { PJ_FR_ROOT, PJ_FR_LISTED, PJ_FR_INSIDELISTED, PJ_FR_CODE, PJ_FR_NOTES, PJ_FR_NONE, PJ_FR_GITIGNORE, PJ_FR_HIDDEN };
static const char* const pjFolderStates[] = { "included", "undecided", "excluded" };
static const char* const pjFolderReasons[] = { "Root", "Listed", "InsideListed", "Code", "Notes", "None", "Gitignore", "Hidden" };
typedef struct PjFolderInfo { const char* path; int state; int reason; const char* entry; } PjFolderInfo;
typedef struct PjFolderMap { const char* directory; FcDict* folders; FcList* order; FcList* files; } PjFolderMap;
static PjFolderInfo* pjFolderInfo(FcCtx* c, const char* path, int state, int reason, const char* entry) {
    PjFolderInfo* f = (PjFolderInfo*)fcAlloc(c, sizeof *f);
    f->path = path; f->state = state; f->reason = reason; f->entry = entry;
    return f;
}
static PjFolderMap* pjFolderMapEmpty(FcCtx* c, const char* directory) {
    PjFolderMap* m = (PjFolderMap*)fcAlloc(c, sizeof *m);
    m->directory = directory; m->folders = fcDictNew(c, FC_KEY_STR); m->order = fcListNew(c); m->files = fcListNew(c);
    return m;
}
static const char* pjFolderNormalize(FcCtx* c, const char* entry) {
    const char* p = pjReplaceChar(c, pjTrim(c, entry), '\\', '/');
    size_t n;
    while (fcStartsWith(p, "./")) p += 2;
    while (*p == '/') p++;
    n = strlen(p);
    while (n > 0 && p[n - 1] == '/') n--;
    p = fcStrN(c, p, n);
    return streq0(p, ".") ? "" : p;
}
static const char* pjFolderMatching(FcCtx* c, const FcList* entries, const char* rel) {
    int i;
    if (!entries) return NULL;
    for (i = 0; i < entries->count; i++) {
        const char* e = (const char*)entries->items[i];
        const char* n = pjFolderNormalize(c, e ? e : "");
        if (!*n) continue;
        if (pjIsPattern(n) ? pjPatternMatch(c, n, rel) : streq0(n, rel)) return e;
    }
    return NULL;
}
static PjFolderInfo* pjFolderStateOf(FcCtx* c, const PjFolderMap* m, const char* relFolder) {
    const char* rel = pjFolderNormalize(c, relFolder);
    FcList* parts;
    const char* path = "";
    PjFolderInfo* known;
    int i;
    if (!*rel) return pjFolderInfo(c, "", PJ_FS_INCLUDED, PJ_FR_ROOT, NULL);
    if (streq0(rel, "..") || fcStartsWith(rel, "../") || rel[0] == '/') return pjFolderInfo(c, rel, PJ_FS_INCLUDED, PJ_FR_LISTED, NULL);
    if ((known = (PjFolderInfo*)fcDictGet(m->folders, rel)) != NULL) return known;
    parts = fgSplit(c, rel, '/');
    for (i = 0; i < parts->count; i++) {
        const char* part = (const char*)parts->items[i];
        PjFolderInfo* f;
        path = i == 0 ? part : fcFmt(c, "%s/%s", path, part);
        if (pjIsHiddenName(part)) return pjFolderInfo(c, rel, PJ_FS_EXCLUDED, streq0(path, rel) ? PJ_FR_HIDDEN : PJ_FR_INSIDELISTED, path);
        if ((f = (PjFolderInfo*)fcDictGet(m->folders, path)) != NULL && f->state == PJ_FS_EXCLUDED)
            return streq0(path, rel) ? f : pjFolderInfo(c, rel, PJ_FS_EXCLUDED, PJ_FR_INSIDELISTED, f->entry ? f->entry : path);
    }
    return pjFolderInfo(c, rel, PJ_FS_UNDECIDED, PJ_FR_NONE, NULL);
}
static int pjFolderStateOfFile(FcCtx* c, const PjFolderMap* m, const char* relFile) {
    const char* rel = pjFolderNormalize(c, relFile);
    const char* slash = strrchr(rel, '/');
    return slash ? pjFolderStateOf(c, m, fcStrN(c, rel, (size_t)(slash - rel)))->state : PJ_FS_INCLUDED;
}
static const char* const pjNativeExtensions[] = { ".h", ".hpp", ".hh", ".c", ".cc", ".cpp", ".cxx" };
static bool pjIsCode(FcCtx* c, const PjProject* p, const char* name) {
    size_t i;
    if (pjIsSourceFile(c, name)) return true;
    if (p->native) for (i = 0; i < 7; i++) if (pjIEq(pjExtension(c, name), pjNativeExtensions[i])) return true;
    return false;
}
/* ProjectNotes.Folder */
static const char* pjNotesFolder(FcCtx* c, const char* ownerFile, const char* setting) {
    const char* dir = fcDirectoryName(c, pjFull(c, ownerFile, "."));
    return pjFull(c, pjCombine(c, dir, pjIsWhite(setting) ? "notes" : pjTrim(c, setting)), ".");
}
typedef struct PjScan { FcCtx* c; const PjProject* p; PjFolderMap* map; const char* notes; } PjScan;
static bool pjScanVisit(PjScan* s, const char* full, const char* rel, PjGitIgnore* rules, bool insideIncluded) {
    FcCtx* c = s->c;
    bool code = false;
    FcList* files = fcListNew(c);
    FcList* subs = fcListNew(c);
    int i;
    DIR* d = opendir(full);
    if (!d) return false;
    closedir(d);
    pjEnumerate(c, full, files, subs);
    for (i = 0; i < files->count; i++) {
        const char* name = (const char*)files->items[i];
        fcListAdd(c, s->map->files, (void*)(*rel ? fcFmt(c, "%s/%s", rel, name) : name));
        code |= pjIsCode(c, s->p, name);
    }
    if (rules && *rel) rules = pjGitWithFolder(c, rules, full);
    pjStableSort(subs, pjCmpIgnoreCaseQ);
    for (i = 0; i < subs->count; i++) {
        const char* name = (const char*)subs->items[i];
        const char* sub = nbCombine(c, full, name);
        const char* subRel;
        const char* excluded;
        const char* line;
        const char* listed;
        bool below;
        PjFolderInfo* info;
        if (pjIsHiddenName(name)) continue;
        if (pjHasFileWith(c, sub, ".fireproj") || pjIsPublishFolder(c, sub)) continue;
        subRel = *rel ? fcFmt(c, "%s/%s", rel, name) : name;
        fcListAdd(c, s->map->order, (void*)subRel);
        if ((excluded = pjFolderMatching(c, s->p->excludedDirectories, subRel)) != NULL) {
            fcDictPut(s->map->folders, subRel, pjFolderInfo(c, subRel, PJ_FS_EXCLUDED, PJ_FR_LISTED, excluded));
            continue;
        }
        if (rules && (line = pjGitMatch(c, rules, sub, true)) != NULL) {
            fcDictPut(s->map->folders, subRel, pjFolderInfo(c, subRel, PJ_FS_EXCLUDED, PJ_FR_GITIGNORE, line));
            continue;
        }
        listed = pjFolderMatching(c, s->p->includedDirectories, subRel);
        fcDictPut(s->map->folders, subRel, NULL);   /* (the place in the order; set below, after its contents) */
        below = pjScanVisit(s, sub, subRel, rules, insideIncluded || listed);
        if (listed) info = pjFolderInfo(c, subRel, PJ_FS_INCLUDED, PJ_FR_LISTED, listed);
        else if (insideIncluded) info = pjFolderInfo(c, subRel, PJ_FS_INCLUDED, PJ_FR_INSIDELISTED, NULL);
        else if (below) info = pjFolderInfo(c, subRel, PJ_FS_INCLUDED, PJ_FR_CODE, NULL);
        else if (streq0(subRel, s->notes)) info = pjFolderInfo(c, subRel, PJ_FS_INCLUDED, PJ_FR_NOTES, NULL);
        else info = pjFolderInfo(c, subRel, PJ_FS_UNDECIDED, PJ_FR_NONE, NULL);
        fcDictPut(s->map->folders, subRel, info);
        code |= below || listed;
    }
    return code;
}
static PjFolderMap* pjFolderMapScan(FcCtx* c, const PjProject* p) {
    PjScan s;
    if (!p->directory) return pjFolderMapEmpty(c, "");
    s.c = c; s.p = p; s.map = pjFolderMapEmpty(c, p->directory);
    s.notes = pjRelative(c, p->directory, pjNotesFolder(c, p->filePath, p->notes));
    pjScanVisit(&s, p->directory, "", p->syncGitignore != 0 ? pjGitForProject(c, p->directory) : NULL, false);
    return s.map;
}

/* ---- ProjectFiles.Resolve ---- */
static bool pjMarkupFirst(const char* r) { return pjIsMarkupFile(r); }
static int pjCmpResolveOrder(const void* a, const void* b) {
    const char* x = *(const char* const*)a;
    const char* y = *(const char* const*)b;
    int rx = pjMarkupFirst(x) ? 0 : 1, ry = pjMarkupFirst(y) ? 0 : 1;
    if (rx != ry) return rx - ry;
    return pjCmpPathOrder(a, b);
}
static bool pjResolveExcluded(FcCtx* c, const PjProject* p, FcList* excludes, PjFolderMap** map, const char* rel) {
    int i;
    for (i = 0; i < excludes->count; i++) if (pgMatch((const FcList*)excludes->items[i], 0, rel, NULL)) return true;
    if (p->buildActionKeys && p->buildActionKeys->count > 0 && pjActionOf(c, p, rel) != PJ_ACT_BUILD) return true;
    if (!*map) *map = pjFolderMapScan(c, p);
    return pjFolderStateOfFile(c, *map, rel) == PJ_FS_EXCLUDED;
}
static FcList* pjResolveFiles(FcCtx* c, const PjProject* p, FcList* problems, bool tests, PjFolderMap* folders) {
    FcList* result = fcListNew(c);
    FcDict* seen = fcDictNew(c, FC_KEY_STR);
    PjFolderMap* map = folders;
    FcList* patterns = p->files;
    FcList* excludes = fcListNew(c);
    FcList* walked = NULL;
    int i, k;
    const char* dir = p->directory;
    if (!dir) return result;
    if (patterns->count == 0) { patterns = fcListNew(c); fcListAdd(c, patterns, (void*)pjDefaultFiles[0]); }
    for (i = 0; i < p->exclude->count; i++) fcListAdd(c, excludes, pgCompileGlob(c, (const char*)p->exclude->items[i]));
    for (i = 0; i < patterns->count; i++) {
        const char* entry = (const char*)patterns->items[i];
        if (pjIsPattern(entry)) {
            FcList* regex = pgCompileGlob(c, entry);
            FcList* matches = fcListNew(c);
            if (!walked) { if (!map) map = pjFolderMapScan(c, p); walked = map->files; }
            for (k = 0; k < walked->count; k++) if (pgMatch(regex, 0, (const char*)walked->items[k], NULL)) fcListAdd(c, matches, walked->items[k]);
            pjStableSort(matches, pjCmpResolveOrder);
            for (k = 0; k < matches->count; k++) {
                const char* rel = (const char*)matches->items[k];
                const char* full;
                if (pjResolveExcluded(c, p, excludes, &map, rel)) continue;
                if (p->files->count == 0 && strlen(rel) >= 10 && pjCmpIgnoreCase(fcStrN(c, rel, 10), "templates/") == 0) continue;
                if (p->files->count == 0 && !tests && strlen(rel) >= 8 && pjCmpIgnoreCase(fcStrN(c, rel, 8), "testing/") == 0) continue;
                full = pjFull(c, rel, dir);
                if (fcSetAdd(seen, full)) fcListAdd(c, result, (void*)full);
            }
        } else {
            const char* rel = pjReplaceChar(c, entry, '\\', '/');
            const char* full = pjFull(c, rel, dir);
            if (!nbFileExists(full)) { if (problems) fcListAdd(c, problems, (void*)fcFmt(c, "The file '%s' of the project '%s' does not exist.", entry, p->name)); continue; }
            if (pjResolveExcluded(c, p, excludes, &map, rel)) continue;
            if (fcSetAdd(seen, full)) fcListAdd(c, result, (void*)full);
        }
    }
    if (p->type == PJ_EXE && p->entry) {
        const char* entryFull = pjFull(c, pjReplaceChar(c, p->entry, '\\', '/'), dir);
        int at = -1;
        for (i = 0; i < result->count; i++) if (streq0((const char*)result->items[i], entryFull)) { at = i; break; }
        if (at < 0) { if (problems) fcListAdd(c, problems, (void*)fcFmt(c, "The entry file '%s' of the project '%s' is not one of its files.", p->entry, p->name)); }
        else { fcListRemoveAt(result, at); fcListAdd(c, result, (void*)entryFull); }
    }
    return result;
}
/* ProjectFiles.Folders(map): the folders that are not excluded */
static FcList* pjFolders(FcCtx* c, const PjFolderMap* m) {
    FcList* r = fcListNew(c);
    int i;
    for (i = 0; i < m->order->count && r->count < 2000; i++) {
        const PjFolderInfo* f = (const PjFolderInfo*)fcDictGet(m->folders, m->order->items[i]);
        if (f && f->state != PJ_FS_EXCLUDED) fcListAdd(c, r, (void*)f->path);
    }
    return r;
}
/* ProjectFiles.ContentFiles(map, compiled) */
static FcList* pjContentFiles(FcCtx* c, const PjFolderMap* m, const FcList* compiled) {
    FcDict* set = fcDictNew(c, FC_KEY_STR);
    FcList* sorted = fcListCopy(c, m->files);
    FcList* r = fcListNew(c);
    int i;
    for (i = 0; i < compiled->count; i++) fcSetAdd(set, compiled->items[i]);
    pjStableSort(sorted, pjCmpPathOrder);
    for (i = 0; i < sorted->count; i++) {
        const char* rel = (const char*)sorted->items[i];
        const char* name = pjFileName(rel);
        const char* ext;
        const char* full;
        if (name[0] == '.') continue;
        ext = pjExtension(c, name);
        if (pjIEq(ext, ".fireproj") || pjIEq(ext, ".firesln")) continue;
        full = pjFull(c, rel, m->directory);
        if (fcDictHas(set, full)) continue;
        fcListAdd(c, r, (void*)full);
        if (r->count >= 5000) break;
    }
    return r;
}
/* ProjectFiles.Expand with a rank (`rank` NULL: 0 for all); `entryOf` gets the index of the entry of each file */
typedef int (*PjRank)(FcCtx* c, const char* rel);
static PjRank pjExpandRank;
static FcCtx* pjExpandCtx;
static int pjCmpExpandOrder(const void* a, const void* b) {
    int rx = pjExpandRank ? pjExpandRank(pjExpandCtx, *(const char* const*)a) : 0;
    int ry = pjExpandRank ? pjExpandRank(pjExpandCtx, *(const char* const*)b) : 0;
    if (rx != ry) return rx - ry;
    return pjCmpPathOrder(a, b);
}
static FcList* pjExpand(FcCtx* c, const char* directory, const FcList* patterns, FcList* problems, PjRank rank, FcList* entryOf) {
    FcList* result = fcListNew(c);
    FcDict* seen = fcDictNew(c, FC_KEY_STR);
    FcList* walked = NULL;
    int i, k;
    for (i = 0; i < patterns->count; i++) {
        const char* entry = (const char*)patterns->items[i];
        if (pjIsPattern(entry)) {
            FcList* regex = pgCompileGlob(c, entry);
            FcList* matches = fcListNew(c);
            if (!walked) walked = pjWalk(c, directory);
            for (k = 0; k < walked->count; k++) if (pgMatch(regex, 0, (const char*)walked->items[k], NULL)) fcListAdd(c, matches, walked->items[k]);
            pjExpandRank = rank; pjExpandCtx = c;
            pjStableSort(matches, pjCmpExpandOrder);
            pjExpandRank = NULL;
            for (k = 0; k < matches->count; k++) {
                const char* full = pjFull(c, (const char*)matches->items[k], directory);
                if (fcSetAdd(seen, full)) { fcListAdd(c, result, (void*)full); if (entryOf) fcListAdd(c, entryOf, FC_IVAL(i)); }
            }
        } else {
            const char* full = pjFull(c, pjReplaceChar(c, entry, '\\', '/'), directory);
            if (!nbFileExists(full)) { if (problems) fcListAdd(c, problems, (void*)fcFmt(c, "The file '%s' does not exist.", entry)); continue; }
            if (fcSetAdd(seen, full)) { fcListAdd(c, result, (void*)full); if (entryOf) fcListAdd(c, entryOf, FC_IVAL(i)); }
        }
    }
    return result;
}
