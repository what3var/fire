/* native/forge/tools/setup.c - the toolchains forge downloads (Phase D 5, commit 044bb27b): src/fire.Native/ToolchainSetup.cs (InstallW64devkit, FindDownloadUrl,
 * SaveMachineToolchain), WatcomSetup (Install, Unpack, FindRoot), TinyccSetup.cs (Install, Verify, Unpack, FindRoot) and DownloadTransfer.cs (Fetch: the progress
 * lines, the check of the length). The same sources, the same checksum (TinyCC), the same folders (Toolchain/ next to forge-c: w64devkit/, watcom/, tcc/,
 * toolchain.json). The download goes through the system, not a TLS of forge-c's own: on Windows WinHTTP (loaded when it is needed), on POSIX http:// over a
 * socket (with http_proxy) and https:// with the program curl (or wget) of the machine. The archives: a zip is unpacked by forge-c (the reader of .fpk,
 * project/fpk.c), the 7z of w64devkit unpacks itself (it is a program for Windows, as in C#).
 *
 *   forge-c toolchain-setup w64devkit|watcom|tcc [--archive <file>]   (what spark's setup does - SetupSteps; --archive: unpack this file instead of downloading)
 *
 * ToolchainProvider.Require (build/provider.c) offers it on the command line when no compiler is found, as ConsoleToolchainPrompt does. */

#define TS_USER_AGENT "fire-toolchain-setup"
#define TS_W64_API "https://api.github.com/repos/skeeto/w64devkit/releases/latest"
#define TS_W64_FALLBACK "https://github.com/skeeto/w64devkit/releases/download/v2.0.0/w64devkit-x64-2.0.0.7z.exe"
#define TS_WATCOM_URL "http://openwatcom.org/ftp/source/ow_portable_v2_stable.zip"
#define TS_TCC_VERSION "0.9.27"
#define TS_TCC_SHA_WIN64 "34a721949a2583fdff725312da092fa0f5f1f284b702e6f811c6954714faabb2"
#define TS_TCC_SHA_WIN32 "02e2bfe8c272a549b15e4bfa4507bd7e05304692af1761db6c1e8e88af675651"

/* where the lines of the work go (ToolchainProvider.Log / the log of the setup) */
typedef void (*TsLog)(const char* line);
static void tsLogf(TsLog log, FcCtx* c, const char* fmt, ...) {
    va_list ap;
    FcBuf b = {0};
    char tmp[1];
    int n;
    if (!log) return;
    va_start(ap, fmt);
    n = vsnprintf(tmp, 0, fmt, ap);
    va_end(ap);
    if (n < 0) return;
    b.p = (char*)fcAlloc(c, (size_t)n + 1);
    va_start(ap, fmt);
    vsnprintf(b.p, (size_t)n + 1, fmt, ap);
    va_end(ap);
    log(b.p);
}

/* the machine: CanInstall of the three (w64devkit and TinyCC: Windows; Open Watcom: Windows or Linux on x64) */
static bool tsIsWindows(void) {
#ifdef _WIN32
    return true;
#else
    return false;
#endif
}
static bool tsIsX64(void) {
#if defined(__x86_64__) || defined(_M_X64) || defined(__aarch64__) || defined(_M_ARM64)
    return true;
#else
    return false;
#endif
}
static bool tsW64CanInstall(void) {
#if defined(_WIN32) && (defined(__x86_64__) || defined(_M_X64))
    return true;
#else
    return false;
#endif
}
static bool tsWatcomCanInstall(void) {
#if (defined(__x86_64__) || defined(_M_X64)) && (defined(_WIN32) || defined(__linux__))
    return true;
#else
    return false;
#endif
}

/* ---- DownloadTransfer.Fetch: the bytes into a file (replaced), "Downloading... n% (m MB)" every 5 %; NULL, or why it failed ---- */
typedef struct TsProgress { TsLog log; FcCtx* c; long long done, total; int lastPercent; } TsProgress;
static void tsProgress(TsProgress* p, long long n) {
    int percent;
    p->done += n;
    percent = p->total > 0 ? (int)(p->done * 100 / p->total) : -1;
    if (p->log && percent != p->lastPercent && (percent % 5 == 0 || percent < 0)) {
        p->lastPercent = percent;
        if (p->total > 0) tsLogf(p->log, p->c, "Downloading... %d%% (%lld MB)", percent, p->done / 1048576);
        else if (n > 0 && (p->done / 1048576) != ((p->done - n) / 1048576)) tsLogf(p->log, p->c, "Downloading... %lld MB", p->done / 1048576);
    }
}
static const char* tsIncomplete(FcCtx* c, long long done, long long total) {
    return fcFmt(c, "The download is incomplete (%lld of %lld bytes). Check the connection (or a proxy / virus scanner) and try again.", done, total);
}
/* the text of an HTTP status (HttpRequestException of EnsureSuccessStatusCode) */
static const char* tsStatusError(FcCtx* c, int status, const char* reason) {
    return fcFmt(c, "Response status code does not indicate success: %d (%s).", status, reason && *reason ? reason : "");
}

#ifdef _WIN32
/* WinHTTP, loaded on first use (no library to link) */
typedef void* HINTERNET_;
typedef struct TsWinHttp {
    bool loaded;
    void* (WINAPI* Open)(LPCWSTR, DWORD, LPCWSTR, LPCWSTR, DWORD);
    void* (WINAPI* Connect)(void*, LPCWSTR, WORD, DWORD);
    void* (WINAPI* OpenRequest)(void*, LPCWSTR, LPCWSTR, LPCWSTR, LPCWSTR, LPCWSTR*, DWORD);
    BOOL (WINAPI* SendRequest)(void*, LPCWSTR, DWORD, void*, DWORD, DWORD, DWORD_PTR);
    BOOL (WINAPI* ReceiveResponse)(void*, void*);
    BOOL (WINAPI* QueryHeaders)(void*, DWORD, LPCWSTR, void*, DWORD*, DWORD*);
    BOOL (WINAPI* ReadData)(void*, void*, DWORD, DWORD*);
    BOOL (WINAPI* CloseHandle)(void*);
    BOOL (WINAPI* CrackUrl)(LPCWSTR, DWORD, DWORD, void*);
} TsWinHttp;
static TsWinHttp tsHttp;
typedef struct TsUrlComponents {
    DWORD dwStructSize; LPWSTR lpszScheme; DWORD dwSchemeLength; int nScheme; LPWSTR lpszHostName; DWORD dwHostNameLength; WORD nPort;
    LPWSTR lpszUserName; DWORD dwUserNameLength; LPWSTR lpszPassword; DWORD dwPasswordLength; LPWSTR lpszUrlPath; DWORD dwUrlPathLength;
    LPWSTR lpszExtraInfo; DWORD dwExtraInfoLength;
} TsUrlComponents;   /* (URL_COMPONENTS of winhttp.h) */
static bool tsLoadWinHttp(void) {
    HMODULE m;
    if (tsHttp.loaded) return tsHttp.Open != NULL;
    tsHttp.loaded = true;
    if (!(m = LoadLibraryA("winhttp.dll"))) return false;
#define TS_GET(field, name) do { FARPROC fp_ = GetProcAddress(m, name); memcpy(&tsHttp.field, &fp_, sizeof fp_); } while (0)
    TS_GET(Open, "WinHttpOpen"); TS_GET(Connect, "WinHttpConnect"); TS_GET(OpenRequest, "WinHttpOpenRequest"); TS_GET(SendRequest, "WinHttpSendRequest");
    TS_GET(ReceiveResponse, "WinHttpReceiveResponse"); TS_GET(QueryHeaders, "WinHttpQueryHeaders"); TS_GET(ReadData, "WinHttpReadData");
    TS_GET(CloseHandle, "WinHttpCloseHandle"); TS_GET(CrackUrl, "WinHttpCrackUrl");
#undef TS_GET
    if (!tsHttp.Open || !tsHttp.Connect || !tsHttp.OpenRequest || !tsHttp.SendRequest || !tsHttp.ReceiveResponse || !tsHttp.QueryHeaders || !tsHttp.ReadData || !tsHttp.CloseHandle || !tsHttp.CrackUrl) tsHttp.Open = NULL;
    return tsHttp.Open != NULL;
}
static wchar_t* tsWide(const char* s) {
    int n = MultiByteToWideChar(CP_UTF8, 0, s, -1, NULL, 0);
    wchar_t* w = (wchar_t*)malloc(sizeof(wchar_t) * (size_t)(n > 0 ? n : 1));
    if (!w) fbOutOfMemory();
    if (n <= 0) w[0] = 0; else MultiByteToWideChar(CP_UTF8, 0, s, -1, w, n);
    return w;
}
/* GET url: the body into f (or into body), the progress to log */
static const char* tsFetchWin(FcCtx* c, const char* url, FILE* f, FcBuf* body, TsLog log) {
    wchar_t* wurl = tsWide(url);
    wchar_t host[256], path[4096];
    TsUrlComponents uc;
    void* session = NULL;
    void* connection = NULL;
    void* request = NULL;
    const char* error = NULL;
    DWORD status = 0, size = sizeof status, length = 0, got = 0;
    TsProgress p;
    char buf[65536];
    memset(&uc, 0, sizeof uc);
    uc.dwStructSize = sizeof uc;
    uc.lpszHostName = host; uc.dwHostNameLength = 256;
    uc.lpszUrlPath = path; uc.dwUrlPathLength = 4096;
    uc.dwSchemeLength = (DWORD)-1; uc.dwExtraInfoLength = (DWORD)-1;
    memset(&p, 0, sizeof p);
    p.log = log; p.c = c; p.lastPercent = -1;
    if (!tsLoadWinHttp()) { free(wurl); return "WinHTTP (winhttp.dll) is not available on this machine."; }
    if (!tsHttp.CrackUrl(wurl, 0, 0, &uc)) { free(wurl); return fcFmt(c, "'%s' is no URL.", url); }
    {
        /* (the proxy of the system - WINHTTP_ACCESS_TYPE_DEFAULT_PROXY -, or the one of http_proxy/https_proxy) */
        const char* proxy = uc.nScheme == 2 ? getenv("https_proxy") : getenv("http_proxy");
        if (!proxy || !*proxy) proxy = uc.nScheme == 2 ? getenv("HTTPS_PROXY") : getenv("HTTP_PROXY");
        if (proxy && *proxy) {
            const char* p = fcStartsWith(proxy, "http://") ? proxy + 7 : proxy;
            size_t n = strlen(p);
            wchar_t* wproxy;
            while (n > 0 && p[n - 1] == '/') n--;
            wproxy = tsWide(fcStrN(c, p, n));
            session = tsHttp.Open(L"" TS_USER_AGENT, 3 /* WINHTTP_ACCESS_TYPE_NAMED_PROXY */, wproxy, L"<local>", 0);
            free(wproxy);
        } else session = tsHttp.Open(L"" TS_USER_AGENT, 0 /* WINHTTP_ACCESS_TYPE_DEFAULT_PROXY */, NULL, NULL, 0);
    }
    if (session) connection = tsHttp.Connect(session, host, uc.nPort, 0);
    if (connection) request = tsHttp.OpenRequest(connection, L"GET", path, NULL, NULL, NULL, uc.nScheme == 2 /* INTERNET_SCHEME_HTTPS */ ? 0x00800000 /* WINHTTP_FLAG_SECURE */ : 0);
    if (!request || !tsHttp.SendRequest(request, NULL, 0, NULL, 0, 0, 0) || !tsHttp.ReceiveResponse(request, NULL)) {
        error = fcFmt(c, "The request to '%s' failed (error %lu).", url, (unsigned long)GetLastError());
        goto done;
    }
    tsHttp.QueryHeaders(request, 19 /* WINHTTP_QUERY_STATUS_CODE */ | 0x20000000 /* WINHTTP_QUERY_FLAG_NUMBER */, NULL, &status, &size, NULL);
    if (status < 200 || status > 299) { error = tsStatusError(c, (int)status, status == 404 ? "Not Found" : ""); goto done; }
    size = sizeof length;
    if (tsHttp.QueryHeaders(request, 5 /* WINHTTP_QUERY_CONTENT_LENGTH */ | 0x20000000, NULL, &length, &size, NULL)) p.total = length;
    tsProgress(&p, 0);
    while (tsHttp.ReadData(request, buf, sizeof buf, &got) && got > 0) {
        if (f && fwrite(buf, 1, got, f) != got) { error = "The download could not be written."; goto done; }
        if (body) fcBufPut(body, buf, got);
        tsProgress(&p, got);
    }
    if (p.total > 0 && p.done != p.total) error = tsIncomplete(c, p.done, p.total);
done:
    if (request) tsHttp.CloseHandle(request);
    if (connection) tsHttp.CloseHandle(connection);
    if (session) tsHttp.CloseHandle(session);
    free(wurl);
    return error;
}
#else
#include <netdb.h>
#include <sys/socket.h>
/* http://host[:port]/path into its parts */
static bool tsSplitUrl(FcCtx* c, const char* url, const char** host, const char** port, const char** path) {
    const char* p = url + 7;
    const char* slash;
    const char* colon;
    if (!fcStartsWith(url, "http://")) return false;
    slash = strchr(p, '/');
    if (!slash) slash = p + strlen(p);
    colon = memchr(p, ':', (size_t)(slash - p));
    *host = fcStrN(c, p, (size_t)((colon ? colon : slash) - p));
    *port = colon ? fcStrN(c, colon + 1, (size_t)(slash - colon - 1)) : "80";
    *path = *slash ? slash : "/";
    return **host != 0;
}
static int tsConnect(const char* host, const char* port) {
    struct addrinfo hints, *res, *a;
    int fd = -1;
    memset(&hints, 0, sizeof hints);
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_STREAM;
    if (getaddrinfo(host, port, &hints, &res) != 0) return -1;
    for (a = res; a; a = a->ai_next) {
        fd = socket(a->ai_family, a->ai_socktype, a->ai_protocol);
        if (fd < 0) continue;
        if (connect(fd, a->ai_addr, a->ai_addrlen) == 0) break;
        close(fd);
        fd = -1;
    }
    freeaddrinfo(res);
    return fd;
}
/* GET of http:// over a socket (HTTP/1.0: no chunks), through http_proxy when it is set; redirects followed (10 at most) */
static const char* tsFetchHttp(FcCtx* c, const char* url, FILE* f, FcBuf* body, TsLog log) {
    int redirects;
    for (redirects = 0; redirects < 10; redirects++) {
        const char* host;
        const char* port;
        const char* path;
        const char* proxy = getenv("http_proxy");
        const char* target;
        FcBuf head = {0};
        char buf[65536];
        int fd, status = 0;
        ssize_t n;
        size_t end = 0;
        TsProgress p;
        const char* location = NULL;
        const char* reason = "";
        if (!proxy || !*proxy) proxy = getenv("HTTP_PROXY");
        if (!tsSplitUrl(c, url, &host, &port, &path)) return fcFmt(c, "'%s' is no URL.", url);
        target = path;
        if (proxy && *proxy) {
            const char* ph;
            const char* pp;
            const char* ppath;
            if (!tsSplitUrl(c, fcStartsWith(proxy, "http://") ? proxy : fcFmt(c, "http://%s", proxy), &ph, &pp, &ppath)) return fcFmt(c, "The proxy '%s' is no URL.", proxy);
            fd = tsConnect(ph, pp);
            target = url;
            if (fd < 0) return fcFmt(c, "The proxy %s:%s could not be reached.", ph, pp);
        } else if ((fd = tsConnect(host, port)) < 0) return fcFmt(c, "%s:%s could not be reached.", host, port);
        fcBufStr(&head, "");
        fcBufFmt(&head, "GET %s HTTP/1.0\r\nHost: %s%s%s\r\nUser-Agent: " TS_USER_AGENT "\r\nAccept: */*\r\nConnection: close\r\n\r\n", target, host, streq0(port, "80") ? "" : ":", streq0(port, "80") ? "" : port);
        if (write(fd, head.p, head.len) != (ssize_t)head.len) { close(fd); fcBufFree(&head); return "The request could not be sent."; }
        head.len = 0;
        /* the head of the answer */
        while ((n = read(fd, buf, sizeof buf)) > 0) {
            char* sep;
            fcBufPut(&head, buf, (size_t)n);
            if ((sep = strstr(head.p, "\r\n\r\n")) != NULL) { end = (size_t)(sep - head.p) + 4; break; }
            if (head.len > 65536) break;
        }
        if (!end) { close(fd); fcBufFree(&head); return "The answer of the server is no HTTP."; }
        memset(&p, 0, sizeof p);
        p.log = log; p.c = c; p.lastPercent = -1;
        {
            char* line = head.p;
            char* eol;
            int k = 0;
            for (; line < head.p + end && (eol = strstr(line, "\r\n")) != NULL; line = eol + 2, k++) {
                *eol = 0;
                if (k == 0) {
                    const char* sp = strchr(line, ' ');
                    status = sp ? atoi(sp + 1) : 0;
                    if (sp && (sp = strchr(sp + 1, ' ')) != NULL) reason = fcStr(c, sp + 1);
                } else if (fcAsciiIEq(fcStrN(c, line, strlen(line) < 15 ? strlen(line) : 15), "content-length:")) p.total = atoll(line + 15);
                else if (strlen(line) > 9 && fcAsciiIEq(fcStrN(c, line, 9), "location:")) location = fgTrim(c, line + 9);
            }
        }
        if (status >= 300 && status < 400 && location) {
            close(fd);
            fcBufFree(&head);
            url = location[0] == '/' ? fcFmt(c, "http://%s%s%s%s", host, streq0(port, "80") ? "" : ":", streq0(port, "80") ? "" : port, location) : location;
            if (fcStartsWith(url, "https://")) return fcFmt(c, "redirect: %s", url);
            continue;
        }
        if (status < 200 || status > 299) { close(fd); fcBufFree(&head); return tsStatusError(c, status, reason); }
        tsProgress(&p, 0);
        if (head.len > end) {
            if (f) fwrite(head.p + end, 1, head.len - end, f);
            if (body) fcBufPut(body, head.p + end, head.len - end);
            tsProgress(&p, (long long)(head.len - end));
        }
        fcBufFree(&head);
        while ((n = read(fd, buf, sizeof buf)) > 0) {
            if (f && fwrite(buf, 1, (size_t)n, f) != (size_t)n) { close(fd); return "The download could not be written."; }
            if (body) fcBufPut(body, buf, (size_t)n);
            tsProgress(&p, n);
        }
        close(fd);
        if (p.total > 0 && p.done != p.total) return tsIncomplete(c, p.done, p.total);
        return NULL;
    }
    return "Too many redirects.";
}
/* https:// with curl or wget of the machine (no TLS in forge-c: the system's) */
static const char* tsFetchTool(FcCtx* c, const char* url, const char* file, TsLog log) {
    const char* curl = nbFindProgram(c, "curl");
    const char* wget = curl ? NULL : nbFindProgram(c, "wget");
    NbRunResult r;
    (void)log;
    if (!curl && !wget) return fcFmt(c, "Downloading %s needs curl or wget on this machine (forge-c has no TLS of its own).", url);
    r = curl ? nbRunCore(curl, fcFmt(c, "-fsSL -A " TS_USER_AGENT " -o \"%s\" \"%s\"", file, url), NULL)
             : nbRunCore(wget, fcFmt(c, "-q -U " TS_USER_AGENT " -O \"%s\" \"%s\"", file, url), NULL);
    if (!r.ok) {
        const char* text = r.text ? fgTrim(c, fcStrN(c, r.text, r.len)) : "";
        free(r.text);
        return fcFmt(c, "%s could not download %s%s%s", curl ? "curl" : "wget", url, *text ? ": " : ".", text);
    }
    free(r.text);
    return NULL;
}
#endif
/* Fetch: url into file; NULL or the error */
static const char* tsFetch(FcCtx* c, const char* url, const char* file, TsLog log) {
    FILE* f = fopen(file, "wb");
    const char* error;
    if (!f) return fcFmt(c, "Could not write '%s'.", file);
#ifdef _WIN32
    error = tsFetchWin(c, url, f, NULL, log);
    fclose(f);
#else
    if (fcStartsWith(url, "http://")) {
        error = tsFetchHttp(c, url, f, NULL, log);
        fclose(f);
        if (error && fcStartsWith(error, "redirect: ")) error = tsFetchTool(c, error + 10, file, log);
    } else { fclose(f); error = tsFetchTool(c, url, file, log); }
#endif
    return error;
}
/* a small answer into memory (the API of GitHub); NULL: none */
static const char* tsFetchText(FcCtx* c, const char* url) {
#ifdef _WIN32
    FcBuf b = {0};
    const char* error;
    fcBufStr(&b, "");
    error = tsFetchWin(c, url, NULL, &b, NULL);
    if (error) { fcBufFree(&b); return NULL; }
    return fcBufDone(c, &b);
#else
    const char* file = fcFmt(c, "%sfire-download-%s.json", nbTempPath(c), nbGuid(c));
    const char* text = NULL;
    if (!tsFetch(c, url, file, NULL)) text = fgReadText(c, file);
    remove(file);
    return text;
#endif
}

/* ---- the archives ---- */
/* a zip into a folder (ZipFile.ExtractToDirectory, overwriting): NULL or why not; the names may not leave the folder */
static const char* tsUnzip(FcCtx* c, const char* zipFile, const char* folder) {
    PkZip zip;
    const char* root;
    int i;
    nbMkdirs(c, folder);
    root = fcFmt(c, "%s/", fcFullPath(c, ".", folder));
    if (!pkZipOpen(c, zipFile, &zip)) { pkZipFree(&zip); return "End of Central Directory record could not be found."; }
    for (i = 0; i < zip.entries->count; i++) {
        const PkZipEntry* e = (const PkZipEntry*)zip.entries->items[i];
        const char* target = fcFullPath(c, folder, fgReplace(c, e->name, "\\", "/"));
        uint8_t* data;
        size_t n = 0;
        if (!fcStartsWith(fcFmt(c, "%s/", target), root)) { pkZipFree(&zip); return "Extracting Zip entry would have resulted in a file outside the specified destination directory."; }
        if (fcEndsWith(e->name, "/")) { nbMkdirs(c, target); continue; }
        nbMkdirs(c, fcDirectoryName(c, target));
        if (!(data = pkZipRead(&zip, e, &n))) { pkZipFree(&zip); return fcFmt(c, "The archive entry '%s' is damaged or uses an unsupported compression method.", e->name); }
        if (!nbWriteBytes(c, target, data, n)) { free(data); pkZipFree(&zip); return fcFmt(c, "Could not write '%s'.", target); }
        free(data);
#ifndef _WIN32
        if ((e->versionMadeBy >> 8) == 3 && ((e->external >> 16) & 0111)) chmod(target, (mode_t)((e->external >> 16) & 07777));   /* (a zip of Unix: the mode) */
#endif
    }
    pkZipFree(&zip);
    return NULL;
}
static FcList* tsSubFolders(FcCtx* c, const char* dir) {
    FcList* r = fcListNew(c);
    DIR* d = opendir(dir);
    struct dirent* e;
    if (!d) return r;
    while ((e = readdir(d)) != NULL) {
        const char* full;
        if (streq0(e->d_name, ".") || streq0(e->d_name, "..")) continue;
        full = nbCombine(c, dir, fcStr(c, e->d_name));
        if (nbDirExists(full)) fcListAdd(c, r, (void*)full);
    }
    closedir(d);
    if (r->count > 1) qsort(r->items, (size_t)r->count, sizeof(void*), fcCmpOrdinal);
    return r;
}
/* Directory.Move: a rename, else a copy and a delete */
static bool tsMove(FcCtx* c, const char* from, const char* to) {
    if (rename(from, to) == 0) return true;
    nbCopyDirectory(c, from, to);
    if (!nbDirExists(to)) return false;
    nbDeleteTree(c, from);
    return true;
}

/* ---- ToolchainSetup.SaveMachineToolchain: toolchain.json with the toolchain (System.Text.Json, null left out, indented), its build/publish kept ---- */
static void tsSaveMachineToolchain(FcCtx* c, const char* gpp) {
    const char* file = nbCombine(c, nbToolchainRoot(c), "toolchain.json");
    FcJson* old = nbFileExists(file) ? nbReadJson(c, file) : NULL;
    FcBuf b = {0};
    static const char* const keep[2] = { "build", "publish" };
    int i;
    nbMkdirs(c, nbToolchainRoot(c));
    fcBufStr(&b, "{\n  \"Extends\": \"gcc\",\n  \"Kind\": \"gcc\",\n  \"Compiler\": ");
    fjEscape(&b, gpp, false);
    fcBufStr(&b, ",\n  \"Std\": \"c\\u002B\\u002B17\",\n  \"Optimization\": \"-O2\",\n  \"Args\": [\n    \"-Wall\",\n    \"-Wextra\"\n  ]");
    for (i = 0; i < 2; i++) {
        FcJson* v = old && old->kind == FJ_OBJ ? fcJsonGet(old, keep[i]) : NULL;
        if (!v) continue;
        fcBufFmt(&b, ",\n  \"%s\": ", keep[i]);
        if (v->kind == FJ_STR) fjEscape(&b, v->s, false);
        else if (v->kind == FJ_BOOL) fcBufStr(&b, v->b ? "true" : "false");
        else if (v->kind == FJ_NUM) fcBufFmt(&b, "%.17g", v->n);
        else fcBufStr(&b, "null");
    }
    fcBufStr(&b, "\n}");
    nbWriteText(c, file, fcBufDone(c, &b));
}

/* ---- w64devkit (ToolchainSetup.InstallW64devkit, FindDownloadUrl) ---- */
static const char* tsW64Url(FcCtx* c) {
    const char* text = tsFetchText(c, TS_W64_API);
    FcJsonReader j;
    FcJson* doc;
    FcJson* assets;
    int i;
    if (!text) return TS_W64_FALLBACK;
    j.c = c; j.p = text; j.bad = false;
    doc = fcJsonValue(&j);
    if (j.bad || !doc || doc->kind != FJ_OBJ || !(assets = fcJsonGet(doc, "assets")) || assets->kind != FJ_ARR) return TS_W64_FALLBACK;
    for (i = 0; i < assets->items->count; i++) {
        FcJson* a = (FcJson*)assets->items->items[i];
        const char* name = a->kind == FJ_OBJ ? fcJsonStr(a, "name") : NULL;
        const char* url = a->kind == FJ_OBJ ? fcJsonStr(a, "browser_download_url") : NULL;
        if (name && url && strlen(name) > 21 && fcAsciiIEq(fcStrN(c, name, 14), "w64devkit-x64-") && nbEndsWithI(name, ".7z.exe")) return url;
    }
    return TS_W64_FALLBACK;
}
/* the compiler, or NULL with *error (InvalidOperationException) */
static const char* tsInstallW64devkit(FcCtx* c, const char* archive, TsLog log, const char** error) {
    const char* root = nbToolchainRoot(c);
    const char* dir = nbCombine(c, root, "w64devkit");
    const char* temp = archive ? archive : fcFmt(c, "%sfire-w64devkit-%s.7z.exe", nbTempPath(c), nbGuid(c));
    const char* gpp;
    const char* failure = NULL;
    NbRunResult run;
    if (!tsW64CanInstall()) { *error = "The portable toolchain (w64devkit) can only be downloaded on Windows (x64). Install a C++ compiler with your system's package manager."; return NULL; }
    nbMkdirs(c, root);
    if (!archive) {
        const char* url = tsW64Url(c);
        tsLogf(log, c, "Downloading %s ...", url);
        failure = tsFetch(c, url, temp, log);
    }
    if (!failure) {
        tsLogf(log, c, "Unpacking to %s ...", root);
        if (nbDirExists(dir)) nbDeleteTree(c, dir);
        run = nbRunCore(temp, fcFmt(c, "-y -o\"%s\"", root), NULL);
        free(run.text);
        if (!run.ok) failure = "Unpacking the toolchain failed.";
    }
    if (!archive) remove(temp);
    if (failure) { *error = fcFmt(c, "Installing the toolchain failed: %s", failure); return NULL; }
    gpp = nbCombine(c, nbCombine(c, dir, "bin"), "g++.exe");
    if (!nbFileExists(gpp)) { *error = fcFmt(c, "The toolchain was unpacked, but '%s' is not there.", gpp); return NULL; }
    tsSaveMachineToolchain(c, gpp);
    tsLogf(log, c, "The toolchain is ready: %s", gpp);
    return gpp;
}

/* ---- Open Watcom (WatcomSetup.Install, Unpack, FindRoot) ---- */
static bool tsWatcomRoot1(FcCtx* c, const char* dir) {
    static const char* const bins[4] = { "binnt64", "binl64", "binnt", "binl" };
    int i;
    if (!nbDirExists(nbCombine(c, dir, "h"))) return false;
    for (i = 0; i < 4; i++) if (nbDirExists(nbCombine(c, dir, bins[i]))) return true;
    return false;
}
static const char* tsWatcomFindRoot(FcCtx* c, const char* dir) {
    int depth, i;
    for (depth = 0; depth < 3; depth++) {
        FcList* subs;
        if (tsWatcomRoot1(c, dir)) return dir;
        subs = tsSubFolders(c, dir);
        for (i = 0; i < subs->count; i++) if (tsWatcomRoot1(c, (const char*)subs->items[i])) return (const char*)subs->items[i];
        if (subs->count != 1) return NULL;
        dir = (const char*)subs->items[0];
    }
    return NULL;
}
/* WatcomSetup.Unpack: the folder of the archive with h/ and the compilers becomes Toolchain/watcom; NULL with *error */
static const char* tsWatcomUnpack(FcCtx* c, const char* zip, TsLog log, const char** error) {
    const char* directory = nbCombine(c, nbToolchainRoot(c), "watcom");
    const char* temp = nbCombine(c, nbDirName(c, fcFullPath(c, ".", directory)), fcFmt(c, ".watcom-unpack-%s", nbGuid(c)));
    const char* failure;
    const char* root;
    const char* owcc;
    int i;
    nbMkdirs(c, temp);
    tsLogf(log, c, "Unpacking to %s ...", directory);
    if ((failure = tsUnzip(c, zip, temp)) != NULL) { nbDeleteTree(c, temp); *error = fcFmt(c, "The archive of Open Watcom is damaged: %s", failure); return NULL; }
    if (!(root = tsWatcomFindRoot(c, temp))) { nbDeleteTree(c, temp); *error = "The archive holds no Open Watcom (no folder with h and binnt64/binnt/binl64/binl)."; return NULL; }
    if (nbDirExists(directory)) nbDeleteTree(c, directory);
    if (!tsMove(c, root, directory)) { nbDeleteTree(c, temp); *error = fcFmt(c, "Could not move '%s' to '%s'.", root, directory); return NULL; }
#ifndef _WIN32
    for (i = 0; i < 2; i++) {
        const char* bin = nbCombine(c, directory, i == 0 ? "binl64" : "binl");
        DIR* d = opendir(bin);
        struct dirent* e;
        if (!d) continue;
        while ((e = readdir(d)) != NULL) {
            const char* f = nbCombine(c, bin, fcStr(c, e->d_name));
            struct stat st;
            if (nbFileExists(f) && stat(f, &st) == 0) chmod(f, st.st_mode | S_IXUSR | S_IXGRP | S_IXOTH);
        }
        closedir(d);
    }
#else
    (void)i;
#endif
    if (nbDirExists(temp)) nbDeleteTree(c, temp);
    owcc = nbWatcomCompilerIn(c, directory, "owcc");
    if (!owcc) { *error = fcFmt(c, "Open Watcom was unpacked to '%s', but owcc is in none of %s, %s.", directory, nbWatcomFolders[0], nbWatcomFolders[1]); return NULL; }
    tsLogf(log, c, "Compiler: %s", owcc);
    tsLogf(log, c, "Open Watcom is ready: %s", directory);
    return directory;
}
static const char* tsInstallWatcom(FcCtx* c, const char* archive, TsLog log, const char** error) {
    const char* zip = archive ? archive : fcFmt(c, "%sfire-watcom-%s.zip", nbTempPath(c), nbGuid(c));
    const char* failure;
    const char* done;
    if (!tsWatcomCanInstall()) { *error = "The portable Open Watcom can only be downloaded on Windows or Linux (x64). Install Open Watcom v2 yourself and set WATCOM."; return NULL; }
    nbMkdirs(c, nbToolchainRoot(c));
    if (!archive) {
        tsLogf(log, c, "Downloading %s ...", TS_WATCOM_URL);
        if ((failure = tsFetch(c, TS_WATCOM_URL, zip, log)) != NULL) { remove(zip); *error = fcFmt(c, "Installing Open Watcom failed: %s", failure); return NULL; }
    }
    done = tsWatcomUnpack(c, zip, log, error);
    if (!archive) remove(zip);
    return done;
}

/* ---- TinyCC (TinyccSetup.Install, Verify, Unpack, FindRoot) ---- */
/* Verify: the SHA-256 of the file against the pinned one; NULL or the message of the InvalidDataException */
static const char* tsVerify(FcCtx* c, const char* file, const char* expected) {
    size_t n = 0;
    uint8_t* data = fbReadFile(file, &n);
    FcSha256 h;
    const char* sum;
    if (!data) return fcFmt(c, "Could not find file '%s'.", file);
    fcShaInit(&h);
    fcShaUpdate(&h, data, n);
    free(data);
    sum = fcShaHex(c, &h);
    if (!fcAsciiIEq(sum, expected)) return fcFmt(c, "The download is not the expected file (its SHA-256 is %s, expected %s).", sum, expected);
    return NULL;
}
static const char* tsTccFindRoot(FcCtx* c, const char* dir) {
    FcList* subs;
    int i, k;
    if (nbFileExists(nbCombine(c, dir, "tcc.exe"))) return dir;
    subs = tsSubFolders(c, dir);
    for (i = 0; i < subs->count; i++) {
        const char* sub = (const char*)subs->items[i];
        FcList* inner;
        if (nbFileExists(nbCombine(c, sub, "tcc.exe"))) return sub;
        inner = tsSubFolders(c, sub);
        for (k = 0; k < inner->count; k++) if (nbFileExists(nbCombine(c, (const char*)inner->items[k], "tcc.exe"))) return (const char*)inner->items[k];
    }
    return NULL;
}
static const char* tsTccUnpack(FcCtx* c, const char* zip, TsLog log, const char** error) {
    const char* target = nbCombine(c, nbToolchainRoot(c), "tcc");
    const char* temp = nbCombine(c, nbDirName(c, fcFullPath(c, ".", target)), fcFmt(c, ".tcc-unpack-%s", nbGuid(c)));
    const char* failure;
    const char* root;
    const char* compiler;
    tsLogf(log, c, "Unpacking to %s ...", target);
    if ((failure = tsUnzip(c, zip, temp)) != NULL) { if (nbDirExists(temp)) nbDeleteTree(c, temp); *error = fcFmt(c, "Installing TinyCC failed: %s", failure); return NULL; }
    if (!(root = tsTccFindRoot(c, temp))) { nbDeleteTree(c, temp); *error = "The archive holds no tcc.exe."; return NULL; }
    if (nbDirExists(target)) nbDeleteTree(c, target);
    if (!tsMove(c, root, target)) { nbDeleteTree(c, temp); *error = fcFmt(c, "Could not move '%s' to '%s'.", root, target); return NULL; }
    if (nbDirExists(temp)) nbDeleteTree(c, temp);
    compiler = nbCombine(c, target, "tcc.exe");
    tsLogf(log, c, "TinyCC is ready: %s", compiler);
    return compiler;
}
static const char* tsInstallTinycc(FcCtx* c, const char* archive, TsLog log, const char** error) {
    const char* url = tsIsX64() ? "https://download.savannah.gnu.org/releases/tinycc/tcc-" TS_TCC_VERSION "-win64-bin.zip" : "https://download.savannah.gnu.org/releases/tinycc/tcc-" TS_TCC_VERSION "-win32-bin.zip";
    const char* sha = tsIsX64() ? TS_TCC_SHA_WIN64 : TS_TCC_SHA_WIN32;
    const char* zip = archive ? archive : fcFmt(c, "%sfire-tcc-%s.zip", nbTempPath(c), nbGuid(c));
    const char* failure = NULL;
    const char* done;
    /* (CanInstall: Windows, and the hash of its zip pinned; --archive checks and unpacks a zip on any machine - the test of the setup) */
    if (!tsIsWindows() && !archive) { *error = "The portable TinyCC is only for Windows. Install tcc with the package manager (e.g. sudo apt install tcc)."; return NULL; }
    nbMkdirs(c, nbToolchainRoot(c));
    if (!archive) {
        tsLogf(log, c, "Downloading %s ...", url);
        failure = tsFetch(c, url, zip, log);
    }
    if (!failure) {
        tsLogf(log, c, "Checking the SHA-256 ...");
        failure = tsVerify(c, zip, sha);
    }
    if (failure) { if (!archive) remove(zip); *error = fcFmt(c, "Installing TinyCC failed: %s", failure); return NULL; }
    done = tsTccUnpack(c, zip, log, error);
    if (!archive) remove(zip);
    return done;
}

/* ---- ToolchainProvider: Install for the request (w64devkit, or Open Watcom for its targets); false: it did not work (the log says why) ---- */
static void tsPrintLine(const char* line) { FcBuf u = {0}; fcBufStr(&u, ""); fcOut8(&u, line); printf("%s\n", u.p); fflush(stdout); fcBufFree(&u); }
static bool tsProvide(FcCtx* c, bool watcom) {
    const char* error = NULL;
    const char* done = watcom ? tsInstallWatcom(c, NULL, tsPrintLine, &error) : tsInstallW64devkit(c, NULL, tsPrintLine, &error);
    if (!done) tsPrintLine(error);
    return done != NULL;
}

/* `forge-c toolchain-setup w64devkit|watcom|tcc [--archive <file>]`: what spark's setup does (SetupSteps: TinyCC, w64devkit, Open Watcom) - the lines of the work on the
 * output, the error on the error output (1) */
static int tsMain(int argc, char** argv) {
    FcCtx ctx;
    FcCtx* c = &ctx;
    const char* which = argc >= 3 ? argv[2] : NULL;
    const char* archive = NULL;
    const char* error = NULL;
    const char* done = NULL;
    int i;
    for (i = 3; i < argc; i++) if (streq0(argv[i], "--archive") && i + 1 < argc) archive = argv[++i];
    if (!which || !(streq0(which, "w64devkit") || streq0(which, "watcom") || streq0(which, "tcc"))) {
        fprintf(stderr, "usage: forge-c toolchain-setup w64devkit|watcom|tcc [--archive <file>]\n");
        return 2;
    }
    fcCtxInit(c);
    if (archive && !nbFileExists(archive)) { fprintf(stderr, "Could not find file '%s'.\n", archive); fcCtxFree(c); return 1; }
    if (streq0(which, "w64devkit")) done = tsInstallW64devkit(c, archive, tsPrintLine, &error);
    else if (streq0(which, "watcom")) done = tsInstallWatcom(c, archive ? fcFullPath(c, ".", archive) : NULL, tsPrintLine, &error);
    else done = tsInstallTinycc(c, archive ? fcFullPath(c, ".", archive) : NULL, tsPrintLine, &error);
    if (!done) { FcBuf u = {0}; fcBufStr(&u, ""); fcOut8(&u, error); fprintf(stderr, "%s\n", u.p); fcBufFree(&u); }
    fcCtxFree(c);
    return done ? 0 : 1;
}
