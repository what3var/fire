/* native/forge/build/provider.c - a toolchain when one is needed (Phase D 2, commit de45e7e4): src/fire.Native/ToolchainSetup.cs ToolchainProvider.Require
 * (with RequireWatcom) and ToolchainRequest.Message, src/fire.Compiler/ConsoleToolchainPrompt.cs (the question on the command line) and
 * src/fire.Compiler/NativeBuilder.cs TestToolchain. The host of forge-c is the command line: it asks only when somebody is there (input and output a
 * terminal) - and then like the C# forge: "Fix automatically? [F]ix / [C]ancel (f): " where fire can download the toolchain (w64devkit on Windows x64, Open Watcom
 * on Windows or Linux x64 - Phase D 5, tools/setup.c), else the text alone and the answer "cancel". */
static bool tsW64CanInstall(void);
static bool tsWatcomCanInstall(void);
static bool tsProvide(FcCtx* c, bool watcom);   /* (tools/setup.c: the download, its lines on the output) */

/* ToolchainRequest.Message (CanInstall of ToolchainSetup is Windows only; of WatcomSetup Windows or Linux on x64) */
static const char* nbToolchainRequestMessage(FcCtx* c, const char* reason, bool watcom) {
    bool canInstall = watcom ? tsWatcomCanInstall() : tsW64CanInstall();
    if (watcom)
        return fcFmt(c, "%s\n\nThis target is built with Open Watcom v2, and it was not found on this machine (on the PATH, in %%WATCOM%%, in Toolchain\\watcom or /opt/watcom).\n\n%s", reason,
            canInstall ? fcFmt(c, "fire can download the portable Open Watcom (about 150 MB) and keep it in '%s'. Nothing is installed on the system; it is only used by fire.", nbCombine(c, nbToolchainRoot(c), "watcom"))
                       : "Install Open Watcom v2 (open-watcom.github.io) and set WATCOM to its folder.");
    return fcFmt(c, "%s\n\nA C++ toolchain (compiler) is needed for that, and none was found on this machine.\n\n%s", reason,
        canInstall ? fcFmt(c, "fire can download the portable w64devkit (about 80 MB, a GCC for Windows) and keep it in '%s'. Nothing is installed on the system; it is only used by fire.", nbCombine(c, nbToolchainRoot(c), "w64devkit"))
                   : "Install a C++ compiler with the package manager of your system (Linux: g++ or clang++, macOS: `xcode-select --install`), or name the compiler yourself.");
}
/* ConsoleToolchainPrompt: the question, only when somebody is there to answer (FIRE_TOOLCHAIN_ANSWER: the answer of a test, without a terminal); true: install */
static bool nbToolchainAsk(FcCtx* c, const char* reason, bool watcom) {
    FcBuf o = {0};
    char line[256];
    const char* given = getenv("FIRE_TOOLCHAIN_ANSWER");
    const char* answer;
    bool canInstall = watcom ? tsWatcomCanInstall() : tsW64CanInstall();
    if (!(given && *given) && (!isatty(0) || !isatty(1))) return false;
    fcBufStr(&o, "\n");
    fcOut8(&o, nbToolchainRequestMessage(c, reason, watcom));
    fcBufStr(&o, "\n\n");
    if (canInstall) fcBufStr(&o, "Fix automatically? [F]ix / [C]ancel (f): ");
    fwrite(o.p, 1, o.len, stdout);
    fflush(stdout);
    fcBufFree(&o);
    if (!canInstall) return false;
    if (given && *given) answer = given;
    else answer = fgets(line, sizeof line, stdin) ? line : "c";
    answer = fcAsciiLower(c, fgTrim(c, answer));
    if (given && *given) printf("%s\n", answer);
    return streq0(answer, "") || streq0(answer, "f") || streq0(answer, "fix") || streq0(answer, "y") || streq0(answer, "yes") || streq0(answer, "j") || streq0(answer, "ja") || streq0(answer, "b");
}
/* Require: wanted if it is there, else any toolchain that is found; else the user is asked (install / cancel) until there is one - NULL: there is none */
static NbToolchain* nbToolchainRequire(FcCtx* c, NbToolchain* wanted, const char* reason) {
    NbToolchain* found;
    int attempt;
    bool watcom = fcAsciiIEq(nbKind(wanted), "watcom");
    for (attempt = 0; attempt < 5; attempt++) {
        if (nbFind(c, wanted)) return wanted;
        /* (RequireWatcom: no other compiler stands in for Open Watcom) */
        if (!watcom && (found = nbDetect(c)) != NULL) return found;
        if (!nbToolchainAsk(c, reason, watcom)) return NULL;
        if (!tsProvide(c, watcom)) return NULL;
        if (watcom && !nbFind(c, wanted)) {
            /* (installed, but the build does not find it: where it looked) */
            printf("Open Watcom was installed to '%s', but '%s' was not found on the PATH or in its folders.\n", nbCombine(c, nbToolchainRoot(c), "watcom"), wanted->compiler ? wanted->compiler : "owcc");
            return NULL;
        }
    }
    return NULL;
}

/* TestToolchain: compiles a small C++ program (with the standard library and threads) for this machine and runs it; the log says what ran and what came out */
typedef struct NbTestResult { bool ok; const char* output; const char* log; } NbTestResult;
static NbTestResult nbTestToolchain(FcCtx* c, NbToolchain* toolchain) {
    NbTestResult r = { false, "", "" };
    FcBuf log = {0};
    const char* work;
    const char* cpp;
    const char* exe;
    NbCommand* k;
    NbRunResult run;
    if (streq0(nbKind(toolchain), "files")) { r.ok = true; r.log = "The toolchain 'files' only writes the sources; there is nothing to test."; return r; }
    work = fcFmt(c, "%sfire-toolchain-test-%s", nbTempPath(c), nbGuid(c));
    nbMkdirs(c, work);
    cpp = nbCombine(c, work, "test.cpp");
    nbWriteText(c, cpp, "#include <cstdio>\n#include <cmath>\n#include <string>\n#include <thread>\nint main() { std::string s = \"toolchain \"; std::thread t([&] { s += \"ok\"; }); t.join(); std::printf(\"%s %g\\n\", s.c_str(), std::sqrt(16.0)); return 0; }\n");
    exe = nbCombine(c, work, "test");
    r.output = exe;
    k = nbCompilerCommand(c, toolchain, fgHostTarget(), cpp, work, exe, false, NULL, false, "c++", false, NULL, NULL, NULL, false);
    if (!streq0(nbKind(toolchain), "custom") && !nbFind(c, toolchain)) {
        r.log = fcFmt(c, "The compiler '%s' was not found.", nbEffectiveCompiler(toolchain));
        nbDeleteTree(c, work);
        return r;
    }
    fcBufStr(&log, "");
    fcBufFmt(&log, "%s %s\n", k->exe, k->args);
    run = nbRun(k->exe, k->args, work);
    nbAppendRun(&log, &run);
    if (run.ok && nbFileExists(exe)) {
        NbRunResult ran = nbRun(exe, "", work);
        bool says = ran.text && strstr(fcStrN(c, ran.text, ran.len), "toolchain ok 4") != NULL;
        r.ok = ran.ok && says;
        nbAppendRun(&log, &ran);
    }
    r.log = fcBufDone(c, &log);
    nbDeleteTree(c, work);
    return r;
}
/* `toolchaintest <name>`: TestToolchain of a built-in toolchain or one of fire.native.json in this folder (the measure: FrontendDump.DumpToolchainTest) */
static void nbDumpToolchainTest(FcCtx* c, const char* name, FcBuf* out) {
    NbConfig* config = nbFileExists("fire.native.json") ? nbConfigLoad(c, "fire.native.json") : nbConfigNew(c);
    NbToolchain* t = nbResolveToolchain(c, config, fgHostTarget(), name, -1);
    NbTestResult r = nbTestToolchain(c, t);
    const char* work = *r.output ? nbDirName(c, r.output) : NULL;
    fcBufFmt(out, "%s\n", r.ok ? "ok" : "failed");
    fcOut8(out, work ? fgReplace(c, r.log, work, "<work>") : r.log);
    fcBufChar(out, '\n');
}
