/* native/forge/project/fpk.c - the package file `.fpk` (Phase D 3, commit de45e7e4): src/fire.Package.Manager/Fpk.cs (Forge, ReadManifest, Extract) and
 * ProjectBuilder.PackLibrary of src/fire.Compiler/ProjectBuilder.cs. A package is a zip as System.IO.Compression's ZipArchive writes it: the entries in
 * the order they are added, no data descriptors, made by Unix (3) with version 2.0, the mode of the file in the external attributes (package.json: a
 * regular file 0644), Deflate (CompressionLevel.Optimal) - with the deflater of packages/compression at level 6, not zlib-ng: the compressed bytes
 * differ from those of the C# forge, everything else (names, order, CRC, sizes, attributes) is the same; the times are those of the files and of now,
 * as there. The reader takes any zip with Stored and Deflate entries. */

#include "../../../packages/compression/native/fire_cmp_deflate.h"

/* ---- zip ---- */
typedef struct PkZipOut { FILE* f; long offset; FcList* central; FcCtx* c; int count; } PkZipOut;
typedef struct PkCentral { const char* name; uint16_t method, version, flags, time, date; uint32_t crc, csize, usize, external, offset; } PkCentral;
static void pkPut16(FcBuf* b, unsigned v) { fcBufChar(b, (char)(v & 0xFF)); fcBufChar(b, (char)((v >> 8) & 0xFF)); }
static void pkPut32(FcBuf* b, uint32_t v) { pkPut16(b, v & 0xFFFF); pkPut16(b, v >> 16); }
/* the DOS time of a time (local time, as ZipArchiveEntry.LastWriteTime) */
static void pkDosTime(time_t t, uint16_t* dosTime, uint16_t* dosDate) {
    struct tm tmv;
    localtime_r(&t, &tmv);
    if (tmv.tm_year < 80) { tmv.tm_year = 80; tmv.tm_mon = 0; tmv.tm_mday = 1; tmv.tm_hour = tmv.tm_min = tmv.tm_sec = 0; }
    *dosTime = (uint16_t)(tmv.tm_hour << 11 | tmv.tm_min << 5 | tmv.tm_sec / 2);
    *dosDate = (uint16_t)((tmv.tm_year - 80) << 9 | (tmv.tm_mon + 1) << 5 | tmv.tm_mday);
}
static bool pkNonAscii(const char* s) { for (; *s; s++) if ((unsigned char)*s >= 0x80) return true; return false; }
/* one entry: the bytes compressed with Deflate (an empty one stored, as .NET does) */
static bool pkZipAdd(PkZipOut* z, const char* name, const uint8_t* data, size_t n, time_t mtime, uint32_t mode) {
    FcCtx* c = z->c;
    PkCentral* e = (PkCentral*)fcAlloc(c, sizeof *e);
    FcBuf h = {0};
    uint8_t* packed = NULL;
    size_t packedLen = 0, cap = 0;
    const char* utf8;
    FcBuf nb = {0};
    fcBufStr(&nb, "");
    fcOut8(&nb, name);
    utf8 = fcBufDone(c, &nb);
    e->name = utf8;
    e->crc = fire_cmp_crc(0, data, n);
    e->usize = (uint32_t)n;
    if (n == 0) { e->method = 0; e->version = 20; }   /* (stored, the version as for Deflate - .NET's) */
    else {
        FireDeflate* d = fire_def_new(FIRE_CMP_RAW, 6);
        uint8_t buf[65536];
        size_t got;
        if (!d) return false;
        fire_def_write(d, data, n);
        fire_def_flush(d, 1);
        while ((got = fire_def_read(d, buf, sizeof buf)) > 0) {
            if (packedLen + got > cap) { cap = (packedLen + got) * 2; packed = (uint8_t*)realloc(packed, cap); }
            memcpy(packed + packedLen, buf, got);
            packedLen += got;
        }
        fire_def_free(d);
        e->method = 8; e->version = 20;
    }
    e->csize = (uint32_t)packedLen;
    e->flags = pkNonAscii(utf8) ? 0x800 : 0;
    pkDosTime(mtime, &e->time, &e->date);
    e->external = mode << 16;
    e->offset = (uint32_t)z->offset;
    fcBufStr(&h, "");
    pkPut32(&h, 0x04034b50); pkPut16(&h, e->version); pkPut16(&h, e->flags); pkPut16(&h, e->method); pkPut16(&h, e->time); pkPut16(&h, e->date);
    pkPut32(&h, e->crc); pkPut32(&h, e->csize); pkPut32(&h, e->usize); pkPut16(&h, (unsigned)strlen(utf8)); pkPut16(&h, 0);
    fcBufStr(&h, utf8);
    fwrite(h.p, 1, h.len, z->f);
    if (packedLen) fwrite(packed, 1, packedLen, z->f);
    z->offset += (long)(h.len + packedLen);
    fcBufFree(&h);
    free(packed);
    fcListAdd(c, z->central, e);
    return true;
}
static bool pkZipClose(PkZipOut* z) {
    FcBuf h = {0};
    int i;
    long start = z->offset;
    fcBufStr(&h, "");
    for (i = 0; i < z->central->count; i++) {
        const PkCentral* e = (const PkCentral*)z->central->items[i];
        pkPut32(&h, 0x02014b50); pkPut16(&h, 0x0300 | e->version); pkPut16(&h, e->version); pkPut16(&h, e->flags); pkPut16(&h, e->method); pkPut16(&h, e->time); pkPut16(&h, e->date);
        pkPut32(&h, e->crc); pkPut32(&h, e->csize); pkPut32(&h, e->usize); pkPut16(&h, (unsigned)strlen(e->name)); pkPut16(&h, 0); pkPut16(&h, 0); pkPut16(&h, 0); pkPut16(&h, 0);
        pkPut32(&h, e->external); pkPut32(&h, e->offset);
        fcBufStr(&h, e->name);
    }
    {
        uint32_t cdSize = (uint32_t)h.len;
        pkPut32(&h, 0x06054b50); pkPut16(&h, 0); pkPut16(&h, 0); pkPut16(&h, (unsigned)z->central->count); pkPut16(&h, (unsigned)z->central->count);
        pkPut32(&h, cdSize);
    }
    pkPut32(&h, (uint32_t)start); pkPut16(&h, 0);
    fwrite(h.p, 1, h.len, z->f);
    fcBufFree(&h);
    return fclose(z->f) == 0;
}

/* a zip read into memory: its entries */
typedef struct PkZipEntry { const char* name; uint16_t method, flags, versionMadeBy, versionNeeded; uint32_t crc, csize, usize, external, localOffset; } PkZipEntry;
typedef struct PkZip { uint8_t* data; size_t size; FcList* entries; } PkZip;
static uint32_t pkGet16(const uint8_t* p) { return (uint32_t)p[0] | (uint32_t)p[1] << 8; }
static uint32_t pkGet32(const uint8_t* p) { return pkGet16(p) | pkGet16(p + 2) << 16; }
/* false: not a zip */
static bool pkZipOpen(FcCtx* c, const char* path, PkZip* z) {
    size_t i, at, count, k;
    memset(z, 0, sizeof *z);
    z->data = fbReadFile(path, &z->size);
    z->entries = fcListNew(c);
    if (!z->data || z->size < 22) return false;
    for (i = z->size - 22 + 1; i-- > 0;) { if (pkGet32(z->data + i) == 0x06054b50) break; if (z->size - i > 22 + 65535) return false; }
    if (pkGet32(z->data + i) != 0x06054b50) return false;
    count = pkGet16(z->data + i + 10);
    at = pkGet32(z->data + i + 16);
    for (k = 0; k < count; k++) {
        PkZipEntry* e;
        size_t nameLen, extraLen, commentLen;
        if (at + 46 > z->size || pkGet32(z->data + at) != 0x02014b50) return false;
        e = (PkZipEntry*)fcAlloc(c, sizeof *e);
        e->versionMadeBy = (uint16_t)pkGet16(z->data + at + 4);
        e->versionNeeded = (uint16_t)pkGet16(z->data + at + 6);
        e->flags = (uint16_t)pkGet16(z->data + at + 8);
        e->method = (uint16_t)pkGet16(z->data + at + 10);
        e->crc = pkGet32(z->data + at + 16);
        e->csize = pkGet32(z->data + at + 20);
        e->usize = pkGet32(z->data + at + 24);
        nameLen = pkGet16(z->data + at + 28); extraLen = pkGet16(z->data + at + 30); commentLen = pkGet16(z->data + at + 32);
        e->external = pkGet32(z->data + at + 38);
        e->localOffset = pkGet32(z->data + at + 42);
        if (at + 46 + nameLen > z->size) return false;
        e->name = fcStrN(c, (const char*)z->data + at + 46, nameLen);
        fcListAdd(c, z->entries, e);
        at += 46 + nameLen + extraLen + commentLen;
    }
    return true;
}
static void pkZipFree(PkZip* z) { free(z->data); z->data = NULL; }
/* the bytes of an entry (NULL: broken); n gets the size */
static uint8_t* pkZipRead(const PkZip* z, const PkZipEntry* e, size_t* n) {
    size_t at = e->localOffset, start;
    uint8_t* out;
    if (at + 30 > z->size || pkGet32(z->data + at) != 0x04034b50) return NULL;
    start = at + 30 + pkGet16(z->data + at + 26) + pkGet16(z->data + at + 28);
    if (start + e->csize > z->size) return NULL;
    out = (uint8_t*)malloc(e->usize + 1);
    if (!out) return NULL;
    if (e->method == 0) { if (e->csize != e->usize) { free(out); return NULL; } memcpy(out, z->data + start, e->usize); }
    else if (e->method == 8) {
        FireInflate* inf = fire_inf_new(FIRE_CMP_RAW);
        long got;
        size_t total = 0;
        fire_inf_feed(inf, z->data + start, e->csize);
        inf->ended = 1;
        while (total < e->usize && (got = fire_inf_run(inf, out + total, e->usize - total)) > 0) total += (size_t)got;
        fire_inf_free(inf);
        if (total != e->usize) { free(out); return NULL; }
    } else { free(out); return NULL; }
    if (fire_cmp_crc(0, out, e->usize) != e->crc) { free(out); return NULL; }
    *n = e->usize;
    return out;
}
static const PkZipEntry* pkZipFind(const PkZip* z, const char* name) {
    int i;
    for (i = 0; i < z->entries->count; i++) if (streq0(((const PkZipEntry*)z->entries->items[i])->name, name)) return (const PkZipEntry*)z->entries->items[i];
    return NULL;
}

/* ---- Fpk.Forge ---- */
typedef struct PkForge { FcCtx* c; const char* baseDir; } PkForge;
static const char* pkAbsolute(void* state, const char* path) { PkForge* f = (PkForge*)state; return pjFull(f->c, path, f->baseDir); }
static bool pkAbsoluteExists(void* state, const char* path) { return nbFileExists(pkAbsolute(state, path)); }
static const char* pkAbsoluteRead(void* state, const char* path) { const char* a = pkAbsolute(state, path); return nbFileExists(a) ? pjReadAllText(((PkForge*)state)->c, a) : NULL; }
static PkManifest* pkManifestLoad(FcCtx* c, const char* path) {
    const char* text = pjReadAllText(c, path);
    if (!text) pkThrow(c, fcFmt(c, "Cannot read '%s': Could not find file '%s'.", path, path));
    return pkManifestParse(c, text);
}
typedef struct PkPlace { FcCtx* c; PkForge* forge; FcDict* used /* lower */; FcDict* byFile /* lower abs -> entry */; FcList* sources; FcList* entries; const char* importName; } PkPlace;
static const char* pkPlace(void* state, const char* source) {
    PkPlace* p = (PkPlace*)state;
    FcCtx* c = p->c;
    const char* abs = pkAbsolute(p->forge, source);
    const char* done = (const char*)fcDictGet(p->byFile, pjLower(c, abs));
    const char* file;
    const char* entry;
    int k;
    if (done) return done;
    file = pjFileName(abs);
    entry = fcFmt(c, "%s/%s", p->importName, file);
    for (k = 2; !fcSetAdd(p->used, pjLower(c, entry)); k++) entry = fcFmt(c, "%s/%d_%s", p->importName, k, file);
    fcDictPut(p->byFile, pjLower(c, abs), (void*)entry);
    fcListAdd(c, p->sources, (void*)abs);
    fcListAdd(c, p->entries, (void*)entry);
    return entry;
}
static void pkMapNative(FcCtx* c, PkNative* n, const char* (*map)(void* state, const char* path), void* state) {
    int i, k;
    n->m.sources = pkSrcMap(c, n->m.sources, map, state);
    for (i = 0; i < n->m.platformSources->count; i++) ((FgKeyed*)n->m.platformSources->items[i])->value = pkSrcMap(c, (FgSrcList*)((FgKeyed*)n->m.platformSources->items[i])->value, map, state);
    for (i = 0; i < n->libraries->count; i++) ((FgKeyed*)n->libraries->items[i])->value = (void*)map(state, (const char*)((FgKeyed*)n->libraries->items[i])->value);
    for (k = 0; k < n->m.headers->count; k++) n->m.headers->items[k] = (void*)map(state, (const char*)n->m.headers->items[k]);
}
static int pkCmpOrdinalQ(const void* a, const void* b) { return strcmp(*(const char* const*)a, *(const char* const*)b); }
/* the files below a folder (full paths, all levels) */
static void pkFilesBelow(FcCtx* c, const char* dir, FcList* out) {
    FcList* files = fcListNew(c);
    FcList* folders = fcListNew(c);
    int i;
    pjEnumerate(c, dir, files, folders);
    for (i = 0; i < files->count; i++) fcListAdd(c, out, (void*)nbCombine(c, dir, (const char*)files->items[i]));
    for (i = 0; i < folders->count; i++) pkFilesBelow(c, nbCombine(c, dir, (const char*)folders->items[i]), out);
}
typedef struct PkForgeResult { const char* packagePath; const char* jsonCopyPath; PkManifest* manifest; } PkForgeResult;
static PkForgeResult pkForge(FcCtx* c, const char* forgeJsonPath, const char* outputDirectory) {
    const char* forgeFile = pjFull(c, forgeJsonPath, ".");
    PkForge f;
    PkManifest* manifest;
    PkManifest* forgeCopy;
    PkManifest* packaged;
    FcList* problems;
    const char* templatesDir = NULL;
    PkPlace place;
    FcList* documents;
    const char* outDir;
    const char* baseName;
    const char* fpk;
    const char* jsonCopy;
    PkZipOut zip;
    PkForgeResult result;
    int i, k;
    if (!nbFileExists(forgeFile)) pkThrow(c, fcFmt(c, "'%s' does not exist.", forgeJsonPath));
    f.c = c; f.baseDir = fcDirectoryName(c, forgeFile);
    manifest = pkManifestLoad(c, forgeFile);
    problems = pkValidate(c, manifest, pkAbsoluteExists, &f);
    if (problems->count == 0)
        for (i = 0; i < manifest->imports->count; i++) {
            const PkImport* imp = (const PkImport*)manifest->imports->items[i];
            FcList* order;
            if (!imp->native) continue;
            order = pkCheckAllPlatforms(c, imp->native, pkAbsoluteRead, &f);
            for (k = 0; k < order->count; k++) fcListAdd(c, problems, (void*)fcFmt(c, "Import '%s': %s", imp->name, (const char*)order->items[k]));
        }
    if (problems->count > 0) pkThrow(c, fcFmt(c, "The package description has problems:\n  %s", fgJoin(c, problems, "\n  ")));
    /* the forge copy: the same file with every path absolute */
    forgeCopy = pkManifestParse(c, pkManifestToJson(c, manifest));
    for (i = 0; i < forgeCopy->imports->count; i++) {
        PkImport* imp = (PkImport*)forgeCopy->imports->items[i];
        if (!pjIsWhite(imp->prelude)) imp->prelude = pkAbsolute(&f, imp->prelude);
        if (imp->resourceValues) for (k = 0; k < imp->resourceValues->count; k++) imp->resourceValues->items[k] = (void*)pkAbsolute(&f, (const char*)imp->resourceValues->items[k]);
        if (imp->native) pkMapNative(c, imp->native, pkAbsolute, &f);
    }
    /* the templates */
    if (!pjIsWhite(manifest->templates)) {
        templatesDir = pkAbsolute(&f, manifest->templates);
        if (!nbDirExists(templatesDir)) pkThrow(c, fcFmt(c, "The templates folder '%s' does not exist.", manifest->templates));
    } else if (nbDirExists(nbCombine(c, f.baseDir, "templates"))) templatesDir = nbCombine(c, f.baseDir, "templates");
    /* the package: every file below a folder of its import */
    packaged = pkManifestParse(c, pkManifestToJson(c, manifest));
    memset(&place, 0, sizeof place);
    place.c = c; place.forge = &f; place.used = fcDictNew(c, FC_KEY_STR); place.byFile = fcDictNew(c, FC_KEY_STR); place.sources = fcListNew(c); place.entries = fcListNew(c);
    fcSetAdd(place.used, "package.json");
    for (i = 0; i < packaged->imports->count; i++) {
        PkImport* imp = (PkImport*)packaged->imports->items[i];
        place.importName = imp->name;
        if (!pjIsWhite(imp->prelude)) imp->prelude = pkPlace(&place, imp->prelude);
        if (imp->resourceKeys && imp->resourceKeys->count > 0) {
            const char* slash = strrchr(imp->prelude, '/');
            const char* folder = slash ? fcStrN(c, imp->prelude, (size_t)(slash - imp->prelude + 1)) : "";
            for (k = 0; k < imp->resourceKeys->count; k++) {
                const char* name = (const char*)imp->resourceKeys->items[k];
                const char* entry = fcFmt(c, "%s%s", folder, pjReplaceChar(c, name, '\\', '/'));
                if (!fcSetAdd(place.used, pjLower(c, entry))) pkThrow(c, fcFmt(c, "The resource '%s' of '%s' collides with another file of the package ('%s').", name, imp->name, entry));
                fcListAdd(c, place.sources, (void*)pkAbsolute(&f, (const char*)imp->resourceValues->items[k]));
                fcListAdd(c, place.entries, (void*)entry);
                imp->resourceValues->items[k] = (void*)entry;
            }
        }
        if (imp->native) pkMapNative(c, imp->native, pkPlace, &place);
    }
    /* the documents */
    documents = fcListNew(c);
    if (manifest->documents && manifest->documents->count > 0) { for (i = 0; i < manifest->documents->count; i++) fcListAdd(c, documents, (void*)pkAbsolute(&f, (const char*)manifest->documents->items[i])); }
    else {
        FcList* files = fcListNew(c);
        pjEnumerate(c, f.baseDir, files, NULL);
        for (i = 0; i < files->count; i++) if (pkIsDocumentName(c, (const char*)files->items[i])) fcListAdd(c, documents, (void*)nbCombine(c, f.baseDir, (const char*)files->items[i]));
        if (documents->count > 1) qsort(documents->items, (size_t)documents->count, sizeof(void*), pkCmpOrdinalQ);
    }
    if (documents->count > 0) {
        forgeCopy->documents = documents;
        packaged->documents = fcListNew(c);
        for (i = 0; i < documents->count; i++) {
            const char* entry = pjFileName((const char*)documents->items[i]);
            if (!fcSetAdd(place.used, pjLower(c, entry))) pkThrow(c, fcFmt(c, "The document '%s' has the name of another file of the package ('%s').", (const char*)documents->items[i], entry));
            fcListAdd(c, place.sources, documents->items[i]);
            fcListAdd(c, place.entries, (void*)entry);
            fcListAdd(c, packaged->documents, (void*)entry);
        }
    }
    if (templatesDir) {
        FcList* files = fcListNew(c);
        forgeCopy->templates = templatesDir;
        packaged->templates = "templates";
        pkFilesBelow(c, templatesDir, files);
        if (files->count > 1) qsort(files->items, (size_t)files->count, sizeof(void*), pkCmpOrdinalQ);
        for (i = 0; i < files->count; i++) { fcListAdd(c, place.sources, files->items[i]); fcListAdd(c, place.entries, (void*)fcFmt(c, "templates/%s", pjRelative(c, templatesDir, (const char*)files->items[i]))); }
    }
    outDir = pjFull(c, outputDirectory ? outputDirectory : nbCombine(c, f.baseDir, "build"), ".");
    nbMkdirs(c, outDir);
    baseName = fcFmt(c, "%s-%s", manifest->name, manifest->version);
    fpk = nbCombine(c, outDir, fcFmt(c, "%s.fpk", baseName));
    if (nbFileExists(fpk)) unlink(fpk);
    memset(&zip, 0, sizeof zip);
    zip.c = c;
    zip.central = fcListNew(c);
    zip.f = fopen(fpk, "wb");
    if (!zip.f) pkThrow(c, fcFmt(c, "Cannot write '%s'.", fpk));
    {
        FcBuf json = {0};
        fcBufStr(&json, "");
        fcOut8(&json, pkManifestToJson(c, packaged));
        pkZipAdd(&zip, "package.json", (const uint8_t*)json.p, json.len, time(NULL), 0100644);
        fcBufFree(&json);
    }
    for (i = 0; i < place.sources->count; i++) {
        const char* source = (const char*)place.sources->items[i];
        size_t n = 0;
        uint8_t* data = fbReadFile(source, &n);
        struct stat st;
        if (!data && !nbFileExists(source)) { fclose(zip.f); unlink(fpk); pkThrow(c, fcFmt(c, "Could not find file '%s'.", source)); }
        stat(source, &st);
        pkZipAdd(&zip, (const char*)place.entries->items[i], data ? data : (const uint8_t*)"", n, st.st_mtime, (uint32_t)st.st_mode & 0xFFFF);
        free(data);
    }
    pkZipClose(&zip);
    nbMkdirs(c, nbCombine(c, outDir, "json"));
    jsonCopy = nbCombine(c, nbCombine(c, outDir, "json"), fcFmt(c, "%s.json", baseName));
    nbWriteText(c, jsonCopy, pkManifestToJson(c, forgeCopy));
    result.packagePath = fpk;
    result.jsonCopyPath = jsonCopy;
    result.manifest = packaged;
    return result;
}

/* ---- Fpk.ReadManifest / Extract ---- */
typedef struct PkInZip { const PkZip* zip; FcCtx* c; } PkInZip;
static bool pkInZip(void* state, const char* path) { PkInZip* z = (PkInZip*)state; return pkZipFind(z->zip, pjReplaceChar(z->c, path, '\\', '/')) != NULL; }
static PkManifest* pkReadManifestOf(FcCtx* c, const PkZip* zip, const char* fpkPath) {
    const PkZipEntry* entry = pkZipFind(zip, "package.json");
    uint8_t* data;
    size_t n = 0;
    PkManifest* m;
    FcList* problems;
    PkInZip in = { zip, c };
    FcU16 text = {0};
    const char* json;
    if (!entry) pkThrow(c, fcFmt(c, "'%s' has no package.json in its root.", fpkPath));
    data = pkZipRead(zip, entry, &n);
    if (!data) pkThrow(c, fcFmt(c, "'%s' is not a package file (.fpk).", fpkPath));
    {
        /* (StreamReader with UTF-8: a byte order mark is skipped) */
        fcDecodeText(data, n, &text);
    }
    free(data);
    json = fc8FromU16(c, text.p, text.len);
    fcU16Free(&text);
    m = pkManifestParse(c, json);
    problems = pkValidate(c, m, pkInZip, &in);
    if (problems->count > 0) pkThrow(c, fcFmt(c, "The package '%s' has problems:\n  %s", fpkPath, fgJoin(c, problems, "\n  ")));
    return m;
}
static PkManifest* pkReadManifest(FcCtx* c, const char* fpkPath) {
    PkZip zip;
    PkManifest* volatile m = NULL;
    if (!nbFileExists(fpkPath)) pkThrow(c, fcFmt(c, "Cannot read '%s': Could not find file '%s'.", fpkPath, pjFull(c, fpkPath, ".")));
    if (!pkZipOpen(c, fpkPath, &zip)) { pkZipFree(&zip); pkThrow(c, fcFmt(c, "'%s' is not a package file (.fpk).", fpkPath)); }
    FC_TRY(c) {
        m = pkReadManifestOf(c, &zip, fpkPath);
    } FC_CATCH(c) {
        pkZipFree(&zip);
        fcRethrow(c);
    } FC_END_TRY;
    pkZipFree(&zip);
    return m;
}
static FC_LATER PkManifest* pkExtract(FcCtx* c, const char* fpkPath, const char* directory) {
    PkZip zip;
    PkManifest* m;
    const char* root = fcFmt(c, "%s/", pjFull(c, directory, "."));
    int i;
    if (!pkZipOpen(c, fpkPath, &zip)) { pkZipFree(&zip); pkThrow(c, fcFmt(c, "'%s' is not a package file (.fpk).", fpkPath)); }
    m = pkReadManifestOf(c, &zip, fpkPath);
    for (i = 0; i < zip.entries->count; i++) {
        const PkZipEntry* e = (const PkZipEntry*)zip.entries->items[i];
        const char* target;
        uint8_t* data;
        size_t n = 0;
        if (fcEndsWith(e->name, "/")) continue;
        target = pjFull(c, e->name, directory);
        if (!fcStartsWith(target, root)) { pkZipFree(&zip); pkThrow(c, fcFmt(c, "'%s' contains a path that leaves the package folder: %s", fpkPath, e->name)); }
        nbMkdirs(c, fcDirectoryName(c, target));
        data = pkZipRead(&zip, e, &n);
        if (!data) { pkZipFree(&zip); pkThrow(c, fcFmt(c, "'%s' is not a package file (.fpk).", fpkPath)); }
        nbWriteBytes(c, target, data, n);
        free(data);
    }
    pkZipFree(&zip);
    return m;
}
