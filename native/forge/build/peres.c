/* native/forge/build/peres.c - the version info and the icon of a Windows program (Phase D 2, commit de45e7e4): src/fire/Utilities/PeResourceEditor.cs (the
 * resources it writes - VS_VERSIONINFO, the RT_ICON images and their RT_GROUP_ICON of an .ico file -, SetSubsystem), PeVersionInfo, src/fire.Compiler/Assembly/
 * AssemblyInfo.cs (ToVersionInfo) and NativeBuilder.ApplyPeMetadata. The C# forge writes the resources with the Win32 resource API (UpdateResource) and only on
 * Windows; forge-c builds on POSIX only, so ApplyPeMetadata leaves the program alone as the C# forge does there - what is ported is the content of the
 * resources, measured with `forge-c peres <file.fire>` against FrontendDump.DumpPeResources. */

/* PeVersionInfo (the fields ToVersionInfo sets; OriginalFilename and LegalCopyright stay null in a native build) */
typedef struct NbPeVersionInfo {
    int fileVersion[4], productVersion[4];
    int subsystem;              /* SubsystemType: 2 GUI, 3 Console */
    const char* companyName; const char* fileDescription; const char* productName; const char* legalCopyright; const char* originalFilename; const char* internalName; const char* comments;
} NbPeVersionInfo;
/* AssemblyInfo.ToVersionInfo */
static NbPeVersionInfo nbPeVersionInfoOf(const FcAsmInfo* a) {
    NbPeVersionInfo v;
    int k;
    bool fileSet = false;
    memset(&v, 0, sizeof v);
    for (k = 0; k < 4; k++) { v.productVersion[k] = a->productVersion[k]; if (a->fileVersion[k] != 0) fileSet = true; }
    for (k = 0; k < 4; k++) v.fileVersion[k] = fileSet ? a->fileVersion[k] : a->productVersion[k];
    v.subsystem = a->gui ? 2 : 3;
    v.comments = a->comments; v.companyName = a->companyName; v.fileDescription = a->fileDescription; v.internalName = a->internalName; v.productName = a->productName;
    return v;
}

/* a growing little-endian buffer with positions (BinaryWriter over a MemoryStream) */
typedef struct NbPeW { uint8_t* p; size_t len, cap; } NbPeW;
static void nbPeByte(NbPeW* w, unsigned v) { if (w->len == w->cap) { w->cap = w->cap ? w->cap * 2 : 256; w->p = (uint8_t*)realloc(w->p, w->cap); } w->p[w->len++] = (uint8_t)v; }
static void nbPe16(NbPeW* w, unsigned v) { nbPeByte(w, v & 0xFF); nbPeByte(w, (v >> 8) & 0xFF); }
static void nbPe32(NbPeW* w, uint32_t v) { nbPe16(w, v & 0xFFFF); nbPe16(w, v >> 16); }
static void nbPePatch16(NbPeW* w, size_t at, unsigned v) { w->p[at] = (uint8_t)v; w->p[at + 1] = (uint8_t)(v >> 8); }
static void nbPeAlign4(NbPeW* w) { while (w->len % 4 != 0) nbPeByte(w, 0); }
/* WriteUnicodeZ: the text as UTF-16 (little endian) and a 0 */
static void nbPeUnicodeZ(NbPeW* w, const char* fc8) {
    const unsigned char* p = (const unsigned char*)fc8;
    while (*p) nbPe16(w, fc8Next(&p));
    nbPe16(w, 0);
}
static size_t nbPeBeginBlock(NbPeW* w, const char* key, bool valueIsText) {
    size_t at = w->len;
    nbPe16(w, 0);                       /* wLength - placeholder, see EndBlock */
    nbPe16(w, 0);                       /* wValueLength */
    nbPe16(w, valueIsText ? 1 : 0);     /* wType */
    nbPeUnicodeZ(w, key);
    nbPeAlign4(w);
    return at;
}
static void nbPeEndBlock(NbPeW* w, size_t at) { nbPePatch16(w, at, (unsigned)((w->len - at) & 0xFFFF)); nbPeAlign4(w); }
static void nbPeStringEntry(NbPeW* w, const char* key, const char* value) {
    size_t at = w->len;
    nbPe16(w, 0);
    nbPe16(w, (unsigned)(fc8Units(value) + 1));   /* wValueLength in WCHARs with the 0 */
    nbPe16(w, 1);
    nbPeUnicodeZ(w, key);
    nbPeAlign4(w);
    nbPeUnicodeZ(w, value);
    nbPeAlign4(w);
    nbPePatch16(w, at, (unsigned)((w->len - at) & 0xFFFF));
}
/* PackVersion: two of the four numbers as 16 bits each (a negative one is 0) */
static uint32_t nbPePack(const int* v, bool high) {
    int a = high ? v[0] : v[2], b = high ? v[1] : v[3];
    return ((uint32_t)(uint16_t)(a > 0 ? a : 0) << 16) | (uint16_t)(b > 0 ? b : 0);
}
/* Version.ToString() of the four numbers */
static const char* nbPeVersionText(FcCtx* c, const int* v) { return fcFmt(c, "%d.%d.%d.%d", v[0], v[1], v[2], v[3]); }
/* BuildVersionInfoResource: VS_VERSIONINFO with VS_FIXEDFILEINFO, one StringTable (040904B0) and the Translation (malloc'd bytes in *out) */
static size_t nbPeBuildVersionInfo(FcCtx* c, const NbPeVersionInfo* info, uint8_t** out) {
    NbPeW w = { NULL, 0, 0 };
    size_t root = nbPeBeginBlock(&w, "VS_VERSION_INFO", false), sfi, table, vfi, tr;
    const char* keys[9];
    const char* values[9];
    int i, n = 0;
    nbPe32(&w, 0xFEEF04BDu);                          /* dwSignature */
    nbPe32(&w, 0x00010000u);                          /* dwStrucVersion (1.0) */
    nbPe32(&w, nbPePack(info->fileVersion, true));
    nbPe32(&w, nbPePack(info->fileVersion, false));
    nbPe32(&w, nbPePack(info->productVersion, true));
    nbPe32(&w, nbPePack(info->productVersion, false));
    nbPe32(&w, 0);                                    /* dwFileFlagsMask */
    nbPe32(&w, 0);                                    /* dwFileFlags */
    nbPe32(&w, 0x00000004u);                          /* dwFileOS = VOS_NT_WINDOWS32 */
    nbPe32(&w, 0x00000001u);                          /* dwFileType = VFT_APP */
    nbPe32(&w, (uint32_t)info->subsystem);            /* dwFileSubtype */
    nbPe32(&w, 0);                                    /* dwFileDateMS */
    nbPe32(&w, 0);                                    /* dwFileDateLS */
    nbPePatch16(&w, root + 2, 13 * 4);
    nbPeAlign4(&w);
    sfi = nbPeBeginBlock(&w, "StringFileInfo", true);
    table = nbPeBeginBlock(&w, "040904B0", true);
    /* EnumerateStrings */
    keys[n] = "CompanyName"; values[n++] = info->companyName;
    keys[n] = "FileDescription"; values[n++] = info->fileDescription;
    keys[n] = "FileVersion"; values[n++] = nbPeVersionText(c, info->fileVersion);
    keys[n] = "InternalName"; values[n++] = info->internalName;
    keys[n] = "LegalCopyright"; values[n++] = info->legalCopyright;
    keys[n] = "OriginalFilename"; values[n++] = info->originalFilename;
    keys[n] = "ProductName"; values[n++] = info->productName;
    keys[n] = "ProductVersion"; values[n++] = nbPeVersionText(c, info->productVersion);
    if (info->comments && *info->comments) { keys[n] = "Comments"; values[n++] = info->comments; }
    for (i = 0; i < n; i++) nbPeStringEntry(&w, keys[i], values[i] ? values[i] : "");
    nbPeEndBlock(&w, table);
    nbPeEndBlock(&w, sfi);
    vfi = nbPeBeginBlock(&w, "VarFileInfo", false);
    tr = nbPeBeginBlock(&w, "Translation", false);
    nbPe16(&w, 0x0409);
    nbPe16(&w, 0x04B0);
    nbPePatch16(&w, tr + 2, 4);
    nbPeEndBlock(&w, tr);
    nbPeEndBlock(&w, vfi);
    nbPeEndBlock(&w, root);
    *out = w.p;
    return w.len;
}

/* ReadIconImages: the images of an .ico file (each becomes an RT_ICON resource); NULL with *error: no icon file (InvalidDataException) */
typedef struct NbPeIcon { uint8_t width, height, colorCount; uint16_t planes, bitCount; const uint8_t* data; uint32_t size; } NbPeIcon;
static FcList* nbPeReadIconImages(FcCtx* c, const char* icoPath, const char** error) {
    size_t n = 0;
    uint8_t* f = fbReadFile(icoPath, &n);
    FcList* images;
    unsigned type, count, i;
    if (!f) { *error = fcFmt(c, "Could not find file '%s'.", icoPath); return NULL; }
    if (n < 6) { free(f); *error = "Unable to read beyond the end of the stream."; return NULL; }
    type = (unsigned)(f[2] | (f[3] << 8));
    if (type != 1) { free(f); *error = fcFmt(c, "'%s' is not a valid .ico file (type %u, expected 1).", icoPath, type); return NULL; }
    count = (unsigned)(f[4] | (f[5] << 8));
    images = fcListNew(c);
    for (i = 0; i < count; i++) {
        const uint8_t* e = f + 6 + i * 16;
        NbPeIcon* x;
        uint32_t offset, avail;
        uint8_t* copy;
        if (6 + (size_t)(i + 1) * 16 > n) { free(f); *error = "Unable to read beyond the end of the stream."; return NULL; }
        x = (NbPeIcon*)fcAlloc(c, sizeof *x);
        x->width = e[0]; x->height = e[1]; x->colorCount = e[2];
        x->planes = (uint16_t)(e[4] | (e[5] << 8)); x->bitCount = (uint16_t)(e[6] | (e[7] << 8));
        x->size = (uint32_t)e[8] | ((uint32_t)e[9] << 8) | ((uint32_t)e[10] << 16) | ((uint32_t)e[11] << 24);
        offset = (uint32_t)e[12] | ((uint32_t)e[13] << 8) | ((uint32_t)e[14] << 16) | ((uint32_t)e[15] << 24);
        /* (BinaryReader.ReadBytes: what is there, up to the size) */
        avail = offset < n ? (uint32_t)(n - offset) : 0;
        if (x->size > avail) x->size = avail;
        copy = (uint8_t*)fcAlloc(c, x->size + 1);
        if (x->size) memcpy(copy, f + offset, x->size);
        x->data = copy;
        fcListAdd(c, images, x);
    }
    free(f);
    return images;
}
/* BuildGroupIconResource: the directory of the images with the ids 1..N in place of their offsets */
static size_t nbPeBuildGroupIcon(FcList* images, uint8_t** out) {
    NbPeW w = { NULL, 0, 0 };
    int i;
    nbPe16(&w, 0);
    nbPe16(&w, 1);
    nbPe16(&w, (unsigned)images->count);
    for (i = 0; i < images->count; i++) {
        const NbPeIcon* img = (const NbPeIcon*)images->items[i];
        nbPeByte(&w, img->width); nbPeByte(&w, img->height); nbPeByte(&w, img->colorCount); nbPeByte(&w, 0);
        nbPe16(&w, img->planes); nbPe16(&w, img->bitCount);
        nbPe32(&w, img->size);
        nbPe16(&w, (unsigned)(i + 1));
    }
    *out = w.p;
    return w.len;
}
/* SetSubsystem: the field Subsystem of the optional header (offset 68 in PE32 and PE32+) - plain bytes, any OS; a file that is no PE file is left alone */
static void nbPeSetSubsystem(const char* exePath, int subsystem) {
    FILE* f = fopen(exePath, "r+b");
    uint8_t head[0x40], sig[4], v[2];
    long size;
    int32_t pe;
    if (!f) return;
    fseek(f, 0, SEEK_END);
    size = ftell(f);
    fseek(f, 0, SEEK_SET);
    if (fread(head, 1, sizeof head, f) != sizeof head || head[0] != 'M' || head[1] != 'Z') { fclose(f); return; }
    pe = (int32_t)((uint32_t)head[0x3C] | ((uint32_t)head[0x3D] << 8) | ((uint32_t)head[0x3E] << 16) | ((uint32_t)head[0x3F] << 24));
    if (pe <= 0 || size < (long)pe + 24 + 70) { fclose(f); return; }
    fseek(f, pe, SEEK_SET);
    if (fread(sig, 1, 4, f) != 4 || sig[0] != 'P' || sig[1] != 'E') { fclose(f); return; }
    fseek(f, pe + 24 + 68, SEEK_SET);
    v[0] = (uint8_t)subsystem; v[1] = (uint8_t)(subsystem >> 8);
    fwrite(v, 1, 2, f);
    fclose(f);
}
/* NativeBuilder.ApplyPeMetadata: version info and icon into a Windows program the toolchain made - with the Win32 resource API, so only on Windows (any other
 * platform leaves the file alone, as the C# forge does). forge-c builds on POSIX only: nothing to do. */
static void nbApplyPeMetadata(FcCtx* c, const char* exe, const FcAsmInfo* info, const FgTarget* target, FcBuf* log) {
    (void)c; (void)exe; (void)info; (void)log;
#ifdef _WIN32
#error "forge-c: the PE resources on Windows (UpdateResource) are not ported"
#endif
    if (!streq0(target->name, "windows")) return;
}

/* `peres <file.fire>`: the PE resources a native build for Windows writes for a source (its #noconsole, #name, ... #icon): the subsystem, VS_VERSIONINFO and,
 * with an icon, its images and the group, each as its length and hex (FrontendDump.DumpPeResources of the C# forge) */
static void nbHexLine(FcBuf* out, const char* what, const uint8_t* p, size_t n) {
    size_t i;
    fcBufFmt(out, "%s %d ", what, (int)n);
    for (i = 0; i < n; i++) fcBufFmt(out, "%02x", p[i]);
    fcBufChar(out, '\n');
}
static void nbDumpPeResources(FcCtx* c, const char* path, const char* prelude, const char* storeRoot, FcBuf* out) {
    FgOptions o;
    FcNatives* registry;
    FcLinked* linked;
    NbPeVersionInfo v;
    uint8_t* data;
    size_t n;
    memset(&o, 0, sizeof o);
    o.target = fgTargetNamed("windows");
    o.mode = -1;
    o.optimize = -1;
    linked = fgLinkNativeKind(c, path, prelude, storeRoot, fcStrictTag(path), &o, &registry, "gcc");
    v = nbPeVersionInfoOf(linked->asmInfo);
    fcBufFmt(out, "subsystem %d\n", v.subsystem);
    n = nbPeBuildVersionInfo(c, &v, &data);
    nbHexLine(out, "version", data, n);
    free(data);
    if (linked->asmInfo->iconPath && *linked->asmInfo->iconPath) {
        const char* error = NULL;
        FcList* images;
        int i;
        fcBufStr(out, "icon ");
        fcOut8(out, linked->asmInfo->iconPath);
        fcBufChar(out, '\n');
        if (!nbFileExists(linked->asmInfo->iconPath)) { fcBufStr(out, "icon not found\n"); return; }
        if (!(images = nbPeReadIconImages(c, linked->asmInfo->iconPath, &error))) { fcBufStr(out, "error "); fcOut8(out, error); fcBufChar(out, '\n'); return; }
        for (i = 0; i < images->count; i++) nbHexLine(out, fcFmt(c, "image %d", i + 1), ((NbPeIcon*)images->items[i])->data, ((NbPeIcon*)images->items[i])->size);
        n = nbPeBuildGroupIcon(images, &data);
        nbHexLine(out, "group", data, n);
        free(data);
    }
}
