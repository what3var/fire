/* native/forge/model/command.c - where forge uses the model converter (Phase D 4, commit 6c1b0f0e): a resource "x.gltf" / "x.glb" / "x.fblocks" /
 * "x.fmesh" becomes fire's model file when it is added to the program (ResourceTable.Add of src/fire.Compiler/Preprocessor.cs - the program carries
 * what scene3d reads), and `forge model <file> [-o <file.f3d>] [--fps n]` (src/fire.Compiler/Program.cs). The converted bytes hang on the context
 * (`modelResources`) until the linker takes the resources (fcResourceRead). */

typedef struct MdResource { const char* path; const uint8_t* data; size_t n; struct MdResource* next; } MdResource;

/* ResourceTable.Add for a new resource: a model is converted now - an exception the table catches is the PreprocessorException of the line */
void fcResourceAdded(FcCtx* c, const char* fullPath, const char* name) {
    const char *type, *message;
    size_t n = 0;
    const uint8_t* data;
    MdResource* r;
    if (!mdIsModelSource(fullPath)) return;
    data = mdConvertFile(c, fullPath, MD_FPS, &n, &type, &message);
    if (!data) {
        if (mdCaught(type)) fcPreError(c, fcFmt(c, "The model '%s' cannot be converted: %s", name, message));
        /* (one it does not catch: what FrontendDump prints for an exception that is no compile error, `internal: <type>: <message>`) */
        fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt(c, "unhandled: %s: %s", strrchr(type, '.') ? strrchr(type, '.') + 1 : type, message));
    }
    r = (MdResource*)fcAlloc(c, sizeof *r);
    r->path = fullPath;
    r->data = data;
    r->n = n;
    r->next = (MdResource*)c->modelResources;
    c->modelResources = r;
}

/* the bytes of a resource (malloc'd; NULL when it cannot be read): the model file of a model, else the file */
uint8_t* fcResourceRead(FcCtx* c, const char* fullPath, size_t* n) {
    const MdResource* r;
    for (r = (const MdResource*)c->modelResources; r; r = r->next)
        if (strcmp(r->path, fullPath) == 0) {
            uint8_t* copy = (uint8_t*)malloc(r->n + 1);
            if (!copy) return NULL;
            if (r->n) memcpy(copy, r->data, r->n);
            *n = r->n;
            return copy;
        }
    return fbReadFile(fullPath, n);
}

/* int.TryParse (NumberStyles.Integer: white space around it, a sign) */
static bool mdTryParseInt(const char* s, int* out) {
    int64_t v = 0;
    bool neg = false, any = false;
    while (*s == ' ' || (*s >= 0x09 && *s <= 0x0D)) s++;
    if (*s == '-' || *s == '+') { neg = *s == '-'; s++; }
    while (*s >= '0' && *s <= '9') { v = v * 10 + (*s - '0'); if (v > 2147483648LL) return false; s++; any = true; }
    while (*s == ' ' || (*s >= 0x09 && *s <= 0x0D)) s++;
    if (*s || !any) return false;
    if (neg) v = -v;
    if (v > 2147483647LL || v < -2147483648LL) return false;
    *out = (int)v;
    return true;
}

/* Path.ChangeExtension(path, ".f3d") */
static const char* mdChangeExtension(FcCtx* c, const char* path) {
    const char* dot = strrchr(path, '.');
    const char* slash = strrchr(path, '/');
    if (!dot || (slash && dot < slash)) return fcFmt(c, "%s.f3d", path);
    return fcFmt(c, "%.*s.f3d", (int)(dot - path), path);
}

/* `model <file.gltf|file.glb|file.fblocks|file.fmesh> [-o <file.f3d>] [--fps n]` (args: argv[1] is "model") */
static int mdModelCommand(int argc, char** argv) {
    FcCtx ctx;
    int o = -1, f = -1, i, fps = MD_FPS, n, result = 0;
    const char *type, *message, *output;
    size_t size = 0;
    const uint8_t* data;
    fcCtxInit(&ctx);
    for (i = argc - 1; i >= 1; i--) { if (strcmp(argv[i], "-o") == 0) o = i - 1; if (strcmp(argv[i], "--fps") == 0) f = i - 1; }
    /* (the indices into args, which starts at "model") */
    if (f >= 0 && f + 1 < argc - 1 && mdTryParseInt(argv[f + 2], &n) && n > 0 && n <= 240) fps = n;
    data = mdConvertFile(&ctx, argv[2], fps, &size, &type, &message);
    if (!data) {
        if (mdCaught(type)) { fprintf(stderr, "%s: %s\n", argv[2], message); result = 1; }
        else { fprintf(stderr, "Unhandled exception. %s: %s\n", type, message); result = 134; }
        fcCtxFree(&ctx);
        return result;
    }
    output = o >= 0 && o + 1 < argc - 1 ? argv[o + 2] : mdChangeExtension(&ctx, argv[2]);
    {
        FILE* w = fopen(output, "wb");
        if (!w || fwrite(data, 1, size, w) != size) {
            const char* full = fcFullPath(&ctx, ".", output);
            if (w) fclose(w);
            fprintf(stderr, "%s: Could not find a part of the path '%s'.\n", argv[2], full);
            fcCtxFree(&ctx);
            return 1;
        }
        fclose(w);
    }
    printf("%s (%zu bytes)\n", output, size);
    fcCtxFree(&ctx);
    return 0;
}
