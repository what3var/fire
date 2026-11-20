/* native/forge/build/tools.c - the commands of the builder's tools (Phase D 2, commit de45e7e4): `forge-c pkglib` (the libraries of packages for the VM,
 * build/pkglib.c) and the measures that FrontendDump of the C# forge prints the same way - `pkglibplan <import>`, `bootimage <exe>` (AfterLink of bios16),
 * `peres <file.fire>` (the PE resources), `toolchaintest <toolchain>` (TestToolchain). Returns -1 when argv is none of them. */
static int nbToolsMain(int argc, char** argv, const char* prelude, const char* storeRoot) {
    FcCtx ctx;
    FcCtx* c = &ctx;
    FcBuf out = {0};
    int result = 0;
    if (argc < 2) return -1;
    if (streq0(argv[1], "pkglib")) return nlMain(argc, argv, storeRoot);
    if (argc != 3 || !(streq0(argv[1], "pkglibplan") || streq0(argv[1], "bootimage") || streq0(argv[1], "peres") || streq0(argv[1], "toolchaintest"))) return -1;
    fcCtxInit(c);
    fcBufStr(&out, "");
    FC_TRY(c) {
        if (streq0(argv[1], "pkglibplan")) nlDumpPlan(c, argv[2], storeRoot, &out);
        else if (streq0(argv[1], "bootimage")) {
            const char* line = nbAfterLink(c, nbBuiltIn(c, "watcom"), fgTargetNamed("bios16"), argv[2]);
            fcOut8(&out, line ? line : "none");
            fcBufChar(&out, '\n');
        }
        else if (streq0(argv[1], "peres")) nbDumpPeResources(c, argv[2], prelude, storeRoot, &out);
        else nbDumpToolchainTest(c, argv[2], &out);
    } FC_CATCH(c) {
        FcList* diags = fcDiagnosticsOf(c, &c->err, NULL);
        int i;
        out.len = 0;
        fcBufStr(&out, "error\n");
        for (i = 0; i < diags->count; i++) fcDiagLogLine(&out, (const FcDiagnostic*)diags->items[i]);
        result = 1;
    } FC_END_TRY;
    fwrite(out.p, 1, out.len, stdout);
    fcBufFree(&out);
    fcCtxFree(c);
    return result;
}
