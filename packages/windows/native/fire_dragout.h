/* fire windows package: part of the display: dragging text and files out of a window into other programs (Window.StartDrag, fire_display_startDrag of
 * the displays fire_display_win32.h, fire_display_x11.h and fire_display_wayland.h), written here per platform, for the OS window of the display:
 *
 *   Windows  fire_dragout_win32(hwnd, text, files): OLE's DoDragDrop with an IDataObject of this file (CF_UNICODETEXT for the text, CF_HDROP for the files) and an
 *            IDropSource, in C (the COM objects are structs with their tables of functions). ole32.dll is loaded when the first drag starts (no link library);
 *            OleInitialize on the calling thread. DoDragDrop is modal: it pumps the messages of the thread until the user lets go.
 *   X11      fire_dragout_x11(display, window, text, files): the source side of the XDND protocol (version 5): XdndEnter/Position/Leave/Drop to the window under
 *            the pointer that has XdndAware (or its XdndProxy), XdndStatus and XdndFinished back, the selection XdndSelection owned by the window and its
 *            SelectionRequests answered with text/uri-list, text/plain;charset=utf-8, UTF8_STRING, text/plain (and TARGETS). libX11 is loaded with dlopen (no
 *            headers, no link library): a build or a run without X11 (Wayland only, a board) simply has no drag. It runs on the thread that pumps the window,
 *            with the connection of the display: nobody else reads the events of the connection meanwhile - the loop takes
 *            only the XDND messages and the selection requests out of the queue (everything else stays there for the display). The pointer stays grabbed by the
 *            press that started the drag (the implicit grab of the button: a grab of our own could fail while another grab holds it); the loop asks the
 *            server where the pointer is and which buttons are held (XQueryPointer) until the button is let go - Escape cancels.
 *   Wayland  fire_dragout_wayland(display, surface, text, files): a wl_data_source and start_drag on the display's wl_display, with a queue, a seat and a
 *            pointer of its own (the pointer learns the serial of the press that a drag needs; fire_dragout_wlPump after every pump of the display). libwayland-client is loaded with dlopen, the few signatures are declared below (no headers).
 *   others   (macOS, the browser, boards) no drag: 0.
 *
 * `files`: the paths, one per line (UTF-8); `text`: UTF-8. The result: 0 nothing happened (no target, refused, cancelled), 1 the other program copied, 2 it moved. */
#ifndef FIRE_DRAGOUT_H
#define FIRE_DRAGOUT_H

#include <stdlib.h>
#include <string.h>
#include "fire_display_c.h"

enum { FIRE_DRAG_NONE = 0, FIRE_DRAG_COPY = 1, FIRE_DRAG_MOVE = 2 };

#ifdef _WIN32
/* ======================================================================================================================================================
 * Windows: OLE drag & drop
 * ====================================================================================================================================================== */
#ifndef WIN32_LEAN_AND_MEAN
#  define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

/* (the few OLE types and numbers this needs - of our own, so that only windows.h is required: objidl.h is not there everywhere, e.g. TinyCC) */
typedef struct FireOleGuid { unsigned long d1; unsigned short d2, d3; unsigned char d4[8]; } FireOleGuid;
typedef struct FireOleFormat { WORD cfFormat; void* ptd; DWORD dwAspect; LONG lindex; DWORD tymed; } FireOleFormat;   /* FORMATETC */
typedef struct FireOleMedium { DWORD tymed; HGLOBAL hGlobal; void* pUnkForRelease; } FireOleMedium;                     /* STGMEDIUM (the union is one handle) */
#define FIRE_OLE_TYMED_HGLOBAL 1u
#define FIRE_OLE_ASPECT_CONTENT 1u
#define FIRE_OLE_DATADIR_GET 1u
#define FIRE_OLE_EFFECT_COPY 1u
#define FIRE_OLE_EFFECT_MOVE 2u
#define FIRE_OLE_EFFECT_LINK 4u
#define FIRE_OLE_S_OK ((HRESULT)0)
#define FIRE_OLE_S_FALSE ((HRESULT)1)
#define FIRE_OLE_DROP ((HRESULT)0x00040100L)                 /* DRAGDROP_S_DROP */
#define FIRE_OLE_CANCEL ((HRESULT)0x00040101L)               /* DRAGDROP_S_CANCEL */
#define FIRE_OLE_DEFAULT_CURSORS ((HRESULT)0x00040102L)      /* DRAGDROP_S_USEDEFAULTCURSORS */
#define FIRE_OLE_NOINTERFACE ((HRESULT)0x80004002L)
#define FIRE_OLE_NOTIMPL ((HRESULT)0x80004001L)
#define FIRE_OLE_INVALIDARG ((HRESULT)0x80070057L)
#define FIRE_OLE_OUTOFMEMORY ((HRESULT)0x8007000EL)
#define FIRE_OLE_FORMATETC ((HRESULT)0x80040064L)            /* DV_E_FORMATETC */
#define FIRE_OLE_TYMED ((HRESULT)0x80040069L)                /* DV_E_TYMED */
#define FIRE_OLE_NO_ADVISE ((HRESULT)0x80040003L)            /* OLE_E_ADVISENOTSUPPORTED */

static const FireOleGuid fire_ole_IUnknown = {0x00000000, 0x0000, 0x0000, {0xC0, 0, 0, 0, 0, 0, 0, 0x46}};
static const FireOleGuid fire_ole_IDataObject = {0x0000010E, 0x0000, 0x0000, {0xC0, 0, 0, 0, 0, 0, 0, 0x46}};
static const FireOleGuid fire_ole_IDropSource = {0x00000121, 0x0000, 0x0000, {0xC0, 0, 0, 0, 0, 0, 0, 0x46}};
static const FireOleGuid fire_ole_IEnumFORMATETC = {0x00000103, 0x0000, 0x0000, {0xC0, 0, 0, 0, 0, 0, 0, 0x46}};
static int fire_ole_is(const FireOleGuid* a, const FireOleGuid* b) { return a != NULL && memcmp(a, b, sizeof *b) == 0; }

/* -- IEnumFORMATETC: the formats of the data -- */
struct FireOleEnum;
typedef struct FireOleEnumVtbl {
    HRESULT (WINAPI *QueryInterface)(struct FireOleEnum*, const FireOleGuid*, void**);
    ULONG (WINAPI *AddRef)(struct FireOleEnum*);
    ULONG (WINAPI *Release)(struct FireOleEnum*);
    HRESULT (WINAPI *Next)(struct FireOleEnum*, ULONG, FireOleFormat*, ULONG*);
    HRESULT (WINAPI *Skip)(struct FireOleEnum*, ULONG);
    HRESULT (WINAPI *Reset)(struct FireOleEnum*);
    HRESULT (WINAPI *Clone)(struct FireOleEnum*, struct FireOleEnum**);
} FireOleEnumVtbl;
typedef struct FireOleEnum { const FireOleEnumVtbl* vtbl; LONG refs; WORD formats[2]; ULONG count, pos; } FireOleEnum;

static FireOleEnum* fire_ole_newEnum(const WORD* formats, ULONG count, ULONG pos);
static HRESULT WINAPI fire_ole_enumQuery(FireOleEnum* self, const FireOleGuid* iid, void** out) {
    if (out == NULL) return FIRE_OLE_INVALIDARG;
    *out = NULL;
    if (!fire_ole_is(iid, &fire_ole_IUnknown) && !fire_ole_is(iid, &fire_ole_IEnumFORMATETC)) return FIRE_OLE_NOINTERFACE;
    InterlockedIncrement(&self->refs);
    *out = self;
    return FIRE_OLE_S_OK;
}
static ULONG WINAPI fire_ole_enumAddRef(FireOleEnum* self) { return (ULONG)InterlockedIncrement(&self->refs); }
static ULONG WINAPI fire_ole_enumRelease(FireOleEnum* self) {
    LONG left = InterlockedDecrement(&self->refs);
    if (left == 0) free(self);
    return (ULONG)left;
}
static HRESULT WINAPI fire_ole_enumNext(FireOleEnum* self, ULONG wanted, FireOleFormat* out, ULONG* fetched) {
    ULONG n = 0;
    if (out == NULL || (wanted > 1 && fetched == NULL)) return FIRE_OLE_INVALIDARG;
    while (n < wanted && self->pos < self->count) {
        out[n].cfFormat = self->formats[self->pos++];
        out[n].ptd = NULL; out[n].dwAspect = FIRE_OLE_ASPECT_CONTENT; out[n].lindex = -1; out[n].tymed = FIRE_OLE_TYMED_HGLOBAL;
        n++;
    }
    if (fetched != NULL) *fetched = n;
    return n == wanted ? FIRE_OLE_S_OK : FIRE_OLE_S_FALSE;
}
static HRESULT WINAPI fire_ole_enumSkip(FireOleEnum* self, ULONG n) {
    self->pos = self->pos + n > self->count ? self->count : self->pos + n;
    return self->pos < self->count ? FIRE_OLE_S_OK : FIRE_OLE_S_FALSE;
}
static HRESULT WINAPI fire_ole_enumReset(FireOleEnum* self) { self->pos = 0; return FIRE_OLE_S_OK; }
static HRESULT WINAPI fire_ole_enumClone(FireOleEnum* self, FireOleEnum** out) {
    if (out == NULL) return FIRE_OLE_INVALIDARG;
    *out = fire_ole_newEnum(self->formats, self->count, self->pos);
    return *out != NULL ? FIRE_OLE_S_OK : FIRE_OLE_OUTOFMEMORY;
}
static const FireOleEnumVtbl fire_ole_enumVtbl = {fire_ole_enumQuery, fire_ole_enumAddRef, fire_ole_enumRelease, fire_ole_enumNext, fire_ole_enumSkip, fire_ole_enumReset, fire_ole_enumClone};
static FireOleEnum* fire_ole_newEnum(const WORD* formats, ULONG count, ULONG pos) {
    FireOleEnum* e = (FireOleEnum*)calloc(1, sizeof(FireOleEnum));
    ULONG i;
    if (e == NULL) return NULL;
    e->vtbl = &fire_ole_enumVtbl;
    e->refs = 1;
    for (i = 0; i < count && i < 2; i++) e->formats[i] = formats[i];
    e->count = i;
    e->pos = pos > i ? i : pos;
    return e;
}

/* -- IDataObject: the text (CF_UNICODETEXT) and the files (CF_HDROP), made anew for every GetData (the receiver frees the memory) -- */
struct FireOleData;
typedef struct FireOleDataVtbl {
    HRESULT (WINAPI *QueryInterface)(struct FireOleData*, const FireOleGuid*, void**);
    ULONG (WINAPI *AddRef)(struct FireOleData*);
    ULONG (WINAPI *Release)(struct FireOleData*);
    HRESULT (WINAPI *GetData)(struct FireOleData*, const FireOleFormat*, FireOleMedium*);
    HRESULT (WINAPI *GetDataHere)(struct FireOleData*, const FireOleFormat*, FireOleMedium*);
    HRESULT (WINAPI *QueryGetData)(struct FireOleData*, const FireOleFormat*);
    HRESULT (WINAPI *GetCanonicalFormatEtc)(struct FireOleData*, const FireOleFormat*, FireOleFormat*);
    HRESULT (WINAPI *SetData)(struct FireOleData*, const FireOleFormat*, FireOleMedium*, BOOL);
    HRESULT (WINAPI *EnumFormatEtc)(struct FireOleData*, DWORD, FireOleEnum**);
    HRESULT (WINAPI *DAdvise)(struct FireOleData*, const FireOleFormat*, DWORD, void*, DWORD*);
    HRESULT (WINAPI *DUnadvise)(struct FireOleData*, DWORD);
    HRESULT (WINAPI *EnumDAdvise)(struct FireOleData*, void**);
} FireOleDataVtbl;
typedef struct FireOleData {
    const FireOleDataVtbl* vtbl;
    LONG refs;
    WORD formats[2];
    ULONG formatCount;
    wchar_t* text; size_t textChars;      /* without the terminating 0 */
    wchar_t* files; size_t filesChars;    /* "a\0b\0\0": all of it, the two zeros at the end included */
} FireOleData;

static int fire_ole_offers(const FireOleData* self, const FireOleFormat* f) {
    ULONG i;
    if (f == NULL || (f->tymed & FIRE_OLE_TYMED_HGLOBAL) == 0 || (f->dwAspect != 0 && f->dwAspect != FIRE_OLE_ASPECT_CONTENT)) return 0;
    for (i = 0; i < self->formatCount; i++) if (self->formats[i] == f->cfFormat) return 1;
    return 0;
}
static HRESULT WINAPI fire_ole_dataQuery(FireOleData* self, const FireOleGuid* iid, void** out) {
    if (out == NULL) return FIRE_OLE_INVALIDARG;
    *out = NULL;
    if (!fire_ole_is(iid, &fire_ole_IUnknown) && !fire_ole_is(iid, &fire_ole_IDataObject)) return FIRE_OLE_NOINTERFACE;
    InterlockedIncrement(&self->refs);
    *out = self;
    return FIRE_OLE_S_OK;
}
static ULONG WINAPI fire_ole_dataAddRef(FireOleData* self) { return (ULONG)InterlockedIncrement(&self->refs); }
static ULONG WINAPI fire_ole_dataRelease(FireOleData* self) {
    LONG left = InterlockedDecrement(&self->refs);
    if (left == 0) { free(self->text); free(self->files); free(self); }
    return (ULONG)left;
}
static HRESULT WINAPI fire_ole_dataGet(FireOleData* self, const FireOleFormat* f, FireOleMedium* m) {
    HGLOBAL h = NULL;
    unsigned char* p;
    if (f == NULL || m == NULL) return FIRE_OLE_INVALIDARG;
    if ((f->tymed & FIRE_OLE_TYMED_HGLOBAL) == 0) return FIRE_OLE_TYMED;
    if (!fire_ole_offers(self, f)) return FIRE_OLE_FORMATETC;
    if (f->cfFormat == CF_UNICODETEXT) {
        h = GlobalAlloc(GMEM_MOVEABLE, (self->textChars + 1) * sizeof(wchar_t));
        if (h == NULL || (p = (unsigned char*)GlobalLock(h)) == NULL) { if (h) GlobalFree(h); return FIRE_OLE_OUTOFMEMORY; }
        memcpy(p, self->text, (self->textChars + 1) * sizeof(wchar_t));
        GlobalUnlock(h);
    } else {
        /* DROPFILES (pFiles, pt, fNC, fWide: 20 bytes), then the wide paths */
        DWORD head[5] = {20, 0, 0, 0, 1};
        h = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, sizeof head + self->filesChars * sizeof(wchar_t));
        if (h == NULL || (p = (unsigned char*)GlobalLock(h)) == NULL) { if (h) GlobalFree(h); return FIRE_OLE_OUTOFMEMORY; }
        memcpy(p, head, sizeof head);
        memcpy(p + sizeof head, self->files, self->filesChars * sizeof(wchar_t));
        GlobalUnlock(h);
    }
    m->tymed = FIRE_OLE_TYMED_HGLOBAL;
    m->hGlobal = h;
    m->pUnkForRelease = NULL;
    return FIRE_OLE_S_OK;
}
static HRESULT WINAPI fire_ole_dataGetHere(FireOleData* self, const FireOleFormat* f, FireOleMedium* m) { (void)self; (void)f; (void)m; return FIRE_OLE_NOTIMPL; }
static HRESULT WINAPI fire_ole_dataQueryGet(FireOleData* self, const FireOleFormat* f) { return fire_ole_offers(self, f) ? FIRE_OLE_S_OK : FIRE_OLE_FORMATETC; }
static HRESULT WINAPI fire_ole_dataCanonical(FireOleData* self, const FireOleFormat* in, FireOleFormat* out) {
    (void)self; (void)in;
    if (out != NULL) out->ptd = NULL;
    return FIRE_OLE_NOTIMPL;
}
static HRESULT WINAPI fire_ole_dataSet(FireOleData* self, const FireOleFormat* f, FireOleMedium* m, BOOL release) { (void)self; (void)f; (void)m; (void)release; return FIRE_OLE_NOTIMPL; }
static HRESULT WINAPI fire_ole_dataEnum(FireOleData* self, DWORD direction, FireOleEnum** out) {
    if (out == NULL) return FIRE_OLE_INVALIDARG;
    *out = NULL;
    if (direction != FIRE_OLE_DATADIR_GET) return FIRE_OLE_NOTIMPL;
    *out = fire_ole_newEnum(self->formats, self->formatCount, 0);
    return *out != NULL ? FIRE_OLE_S_OK : FIRE_OLE_OUTOFMEMORY;
}
static HRESULT WINAPI fire_ole_dataAdvise(FireOleData* self, const FireOleFormat* f, DWORD flags, void* sink, DWORD* connection) { (void)self; (void)f; (void)flags; (void)sink; (void)connection; return FIRE_OLE_NO_ADVISE; }
static HRESULT WINAPI fire_ole_dataUnadvise(FireOleData* self, DWORD connection) { (void)self; (void)connection; return FIRE_OLE_NO_ADVISE; }
static HRESULT WINAPI fire_ole_dataEnumAdvise(FireOleData* self, void** out) { (void)self; if (out) *out = NULL; return FIRE_OLE_NO_ADVISE; }
static const FireOleDataVtbl fire_ole_dataVtbl = {fire_ole_dataQuery, fire_ole_dataAddRef, fire_ole_dataRelease, fire_ole_dataGet, fire_ole_dataGetHere, fire_ole_dataQueryGet,
                                                  fire_ole_dataCanonical, fire_ole_dataSet, fire_ole_dataEnum, fire_ole_dataAdvise, fire_ole_dataUnadvise, fire_ole_dataEnumAdvise};

/* -- IDropSource: the drop happens when no mouse button is held any more, Escape cancels -- */
struct FireOleSource;
typedef struct FireOleSourceVtbl {
    HRESULT (WINAPI *QueryInterface)(struct FireOleSource*, const FireOleGuid*, void**);
    ULONG (WINAPI *AddRef)(struct FireOleSource*);
    ULONG (WINAPI *Release)(struct FireOleSource*);
    HRESULT (WINAPI *QueryContinueDrag)(struct FireOleSource*, BOOL, DWORD);
    HRESULT (WINAPI *GiveFeedback)(struct FireOleSource*, DWORD);
} FireOleSourceVtbl;
typedef struct FireOleSource { const FireOleSourceVtbl* vtbl; LONG refs; } FireOleSource;

static HRESULT WINAPI fire_ole_sourceQuery(FireOleSource* self, const FireOleGuid* iid, void** out) {
    if (out == NULL) return FIRE_OLE_INVALIDARG;
    *out = NULL;
    if (!fire_ole_is(iid, &fire_ole_IUnknown) && !fire_ole_is(iid, &fire_ole_IDropSource)) return FIRE_OLE_NOINTERFACE;
    InterlockedIncrement(&self->refs);
    *out = self;
    return FIRE_OLE_S_OK;
}
static ULONG WINAPI fire_ole_sourceAddRef(FireOleSource* self) { return (ULONG)InterlockedIncrement(&self->refs); }
static ULONG WINAPI fire_ole_sourceRelease(FireOleSource* self) {
    LONG left = InterlockedDecrement(&self->refs);
    if (left == 0) free(self);
    return (ULONG)left;
}
static HRESULT WINAPI fire_ole_sourceContinue(FireOleSource* self, BOOL escape, DWORD keys) {
    (void)self;
    if (escape) return FIRE_OLE_CANCEL;
    if ((keys & (MK_LBUTTON | MK_RBUTTON | MK_MBUTTON)) == 0) return FIRE_OLE_DROP;
    return FIRE_OLE_S_OK;
}
static HRESULT WINAPI fire_ole_sourceFeedback(FireOleSource* self, DWORD effect) { (void)self; (void)effect; return FIRE_OLE_DEFAULT_CURSORS; }
static const FireOleSourceVtbl fire_ole_sourceVtbl = {fire_ole_sourceQuery, fire_ole_sourceAddRef, fire_ole_sourceRelease, fire_ole_sourceContinue, fire_ole_sourceFeedback};

/* UTF-8 -> UTF-16 (malloc, terminated; `chars`: without the 0). */
static wchar_t* fire_ole_wide(const char* s, size_t* chars) {
    int n = MultiByteToWideChar(CP_UTF8, 0, s, -1, NULL, 0);
    wchar_t* w;
    *chars = 0;
    if (n <= 0) return NULL;
    w = (wchar_t*)malloc(sizeof(wchar_t) * (size_t)n);
    if (w == NULL) return NULL;
    MultiByteToWideChar(CP_UTF8, 0, s, -1, w, n);
    *chars = (size_t)n - 1;
    return w;
}

/* The paths (one per line) as the list of CF_HDROP: each path ends with a 0, the list with another (malloc; `chars`: all of it). */
static wchar_t* fire_ole_fileList(const char* paths, size_t* chars) {
    size_t cap = 16, used = 0;
    wchar_t* list = (wchar_t*)malloc(sizeof(wchar_t) * cap);
    const char* p = paths;
    *chars = 0;
    if (list == NULL) return NULL;
    while (*p) {
        const char* end = strchr(p, '\n');
        size_t len = end ? (size_t)(end - p) : strlen(p);
        if (len > 0 && len < 0x7FFFFFF) {
            int n = MultiByteToWideChar(CP_UTF8, 0, p, (int)len, NULL, 0);
            if (n > 0) {
                if (used + (size_t)n + 2 > cap) {
                    wchar_t* grown;
                    while (used + (size_t)n + 2 > cap) cap *= 2;
                    grown = (wchar_t*)realloc(list, sizeof(wchar_t) * cap);
                    if (grown == NULL) { free(list); return NULL; }
                    list = grown;
                }
                MultiByteToWideChar(CP_UTF8, 0, p, (int)len, list + used, n);
                used += (size_t)n;
                list[used++] = 0;
            }
        }
        p = end ? end + 1 : p + len;
    }
    if (used == 0) { free(list); return NULL; }
    list[used++] = 0;
    *chars = used;
    return list;
}

typedef HRESULT (WINAPI *FireOleInitialize)(void*);
typedef void (WINAPI *FireOleUninitialize)(void);
typedef HRESULT (WINAPI *FireOleDoDragDrop)(void*, void*, DWORD, DWORD*);

/* Drags the text and/or the files out of the window (`hwnd`: the window the drag starts from - DoDragDrop does not need it, the drag belongs to the thread)
 * until the user lets go; 0 / 1 copy / 2 move. A drag needs a mouse button that is held: without one there is none (0). */
static int fire_dragout_win32(HWND hwnd, const char* text, const char* files) {
    static HMODULE ole = NULL;
    static FireOleInitialize init = NULL;
    static FireOleUninitialize uninit = NULL;
    static FireOleDoDragDrop doDrag = NULL;
    FireOleData* data;
    FireOleSource* source;
    DWORD effect = 0;
    HRESULT hr;
    int result = FIRE_DRAG_NONE;
    (void)hwnd;
    if ((text == NULL || text[0] == 0) && (files == NULL || files[0] == 0)) return FIRE_DRAG_NONE;
    if (ole == NULL) {
        ole = LoadLibraryA("ole32.dll");
        if (ole != NULL) {
            init = (FireOleInitialize)(void*)GetProcAddress(ole, "OleInitialize");
            uninit = (FireOleUninitialize)(void*)GetProcAddress(ole, "OleUninitialize");
            doDrag = (FireOleDoDragDrop)(void*)GetProcAddress(ole, "DoDragDrop");
        }
    }
    if (init == NULL || uninit == NULL || doDrag == NULL) return FIRE_DRAG_NONE;
    if ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0 && (GetAsyncKeyState(VK_RBUTTON) & 0x8000) == 0 && (GetAsyncKeyState(VK_MBUTTON) & 0x8000) == 0) return FIRE_DRAG_NONE;
    hr = init(NULL);   /* (S_FALSE: the thread has it already - also counted, and given back below) */
    if (FAILED(hr)) return FIRE_DRAG_NONE;
    data = (FireOleData*)calloc(1, sizeof(FireOleData));
    source = (FireOleSource*)calloc(1, sizeof(FireOleSource));
    if (data != NULL && source != NULL) {
        data->vtbl = &fire_ole_dataVtbl;
        data->refs = 1;
        source->vtbl = &fire_ole_sourceVtbl;
        source->refs = 1;
        if (text != NULL && text[0] != 0 && (data->text = fire_ole_wide(text, &data->textChars)) != NULL) data->formats[data->formatCount++] = CF_UNICODETEXT;
        if (files != NULL && files[0] != 0 && (data->files = fire_ole_fileList(files, &data->filesChars)) != NULL) data->formats[data->formatCount++] = CF_HDROP;
        if (data->formatCount > 0) {
            hr = doDrag(data, source, FIRE_OLE_EFFECT_COPY | FIRE_OLE_EFFECT_MOVE, &effect);
            if (hr == FIRE_OLE_DROP) result = (effect & FIRE_OLE_EFFECT_MOVE) ? FIRE_DRAG_MOVE : (effect & (FIRE_OLE_EFFECT_COPY | FIRE_OLE_EFFECT_LINK)) ? FIRE_DRAG_COPY : FIRE_DRAG_NONE;
        }
    }
    if (data != NULL) { if (data->vtbl) fire_ole_dataRelease(data); else free(data); }
    if (source != NULL) { if (source->vtbl) fire_ole_sourceRelease(source); else free(source); }
    uninit();
    return result;
}
#endif /* _WIN32 */

#if !defined(_WIN32) && !defined(__APPLE__) && !defined(__EMSCRIPTEN__) && !defined(__ANDROID__)
/* ======================================================================================================================================================
 * X11: the source side of XDND
 * ====================================================================================================================================================== */
#define FIRE_DRAGOUT_X11 1
#include <dlfcn.h>
#include <poll.h>
#include <time.h>

/* (the few types of Xlib this needs, laid out as in <X11/Xlib.h> - of our own, so that no X11 headers are needed to build) */
typedef unsigned long FireXId;   /* Window, Atom, Time */
typedef struct FireXClient { int type; unsigned long serial; int send_event; void* display; FireXId window; FireXId message_type; int format; union { char b[20]; short s[10]; long l[5]; } data; } FireXClient;
typedef struct FireXSelRequest { int type; unsigned long serial; int send_event; void* display; FireXId owner, requestor, selection, target, property, time; } FireXSelRequest;
typedef struct FireXSelNotify { int type; unsigned long serial; int send_event; void* display; FireXId requestor, selection, target, property, time; } FireXSelNotify;
typedef struct FireXProperty { int type; unsigned long serial; int send_event; void* display; FireXId window, atom, time; int state; } FireXProperty;
/* (XFixesSelectionNotifyEvent: the notice that the owner of the clipboard changed) */
typedef struct FireXFixesNotify { int type; unsigned long serial; int send_event; void* display; FireXId window; int subtype; FireXId owner, selection, time, selectionTime; } FireXFixesNotify;
typedef union FireXEvent { int type; FireXClient xclient; FireXSelRequest xselectionrequest; FireXSelNotify xselection; FireXProperty xproperty; FireXFixesNotify xfixes; long pad[24]; } FireXEvent;
enum { FIRE_X_PROPERTY_NOTIFY = 28, FIRE_X_SELECTION_REQUEST = 30, FIRE_X_SELECTION_NOTIFY = 31, FIRE_X_CLIENT_MESSAGE = 33 };
#define FIRE_X_ATOM 4ul           /* XA_ATOM */
#define FIRE_X_WINDOW 33ul        /* XA_WINDOW */
#define FIRE_X_BUTTONS (0x1Fu << 8)   /* Button1Mask .. Button5Mask */
#define FIRE_X_SHIFT 1u

typedef int (*FireXErrorHandler)(void*, void*);
typedef struct FireX11 {
    int tried;
    void* lib;
    FireXId (*InternAtom)(void*, const char*, int);
    int (*GetWindowProperty)(void*, FireXId, FireXId, long, long, int, FireXId, FireXId*, int*, unsigned long*, unsigned long*, unsigned char**);
    int (*Free)(void*);
    int (*SendEvent)(void*, FireXId, int, long, FireXEvent*);
    int (*Flush)(void*);
    int (*Sync)(void*, int);
    int (*ChangeProperty)(void*, FireXId, FireXId, FireXId, int, int, const unsigned char*, int);
    int (*DeleteProperty)(void*, FireXId, FireXId);
    int (*SetSelectionOwner)(void*, FireXId, FireXId, FireXId);
    int (*QueryPointer)(void*, FireXId, FireXId*, FireXId*, int*, int*, int*, int*, unsigned int*);
    int (*TranslateCoordinates)(void*, FireXId, FireXId, int, int, int*, int*, FireXId*);
    int (*CheckIfEvent)(void*, FireXEvent*, int (*)(void*, FireXEvent*, char*), char*);
    int (*ConnectionNumber)(void*);
    FireXId (*DefaultRootWindow)(void*);
    unsigned char (*KeysymToKeycode)(void*, FireXId);
    int (*QueryKeymap)(void*, char*);
    FireXErrorHandler (*SetErrorHandler)(FireXErrorHandler);
    FireXId (*GetSelectionOwner)(void*, FireXId);
    int (*QueryExtension)(void*, const char*, int*, int*, int*);
} FireX11;

static int fire_x11_bind(FireX11* x, const char* name, void* fn) {
    void* p = dlsym(x->lib, name);
    memcpy(fn, &p, sizeof p);
    return p != NULL;
}
/* libX11, loaded once (NULL: not there). */
static FireX11* fire_x11_load(void) {
    static FireX11 x;
    if (!x.tried) {
        x.tried = 1;
        x.lib = dlopen("libX11.so.6", RTLD_LAZY | RTLD_LOCAL);
        if (x.lib == NULL) x.lib = dlopen("libX11.so", RTLD_LAZY | RTLD_LOCAL);
        if (x.lib != NULL
            && !(fire_x11_bind(&x, "XInternAtom", &x.InternAtom) && fire_x11_bind(&x, "XGetWindowProperty", &x.GetWindowProperty) && fire_x11_bind(&x, "XFree", &x.Free)
                 && fire_x11_bind(&x, "XSendEvent", &x.SendEvent) && fire_x11_bind(&x, "XFlush", &x.Flush) && fire_x11_bind(&x, "XSync", &x.Sync)
                 && fire_x11_bind(&x, "XChangeProperty", &x.ChangeProperty)
                 && fire_x11_bind(&x, "XDeleteProperty", &x.DeleteProperty) && fire_x11_bind(&x, "XSetSelectionOwner", &x.SetSelectionOwner)
                 && fire_x11_bind(&x, "XQueryPointer", &x.QueryPointer) && fire_x11_bind(&x, "XTranslateCoordinates", &x.TranslateCoordinates)
                 && fire_x11_bind(&x, "XCheckIfEvent", &x.CheckIfEvent) && fire_x11_bind(&x, "XConnectionNumber", &x.ConnectionNumber)
                 && fire_x11_bind(&x, "XDefaultRootWindow", &x.DefaultRootWindow) && fire_x11_bind(&x, "XKeysymToKeycode", &x.KeysymToKeycode)
                 && fire_x11_bind(&x, "XQueryKeymap", &x.QueryKeymap) && fire_x11_bind(&x, "XSetErrorHandler", &x.SetErrorHandler)
                 && fire_x11_bind(&x, "XGetSelectionOwner", &x.GetSelectionOwner) && fire_x11_bind(&x, "XQueryExtension", &x.QueryExtension))) {
            dlclose(x.lib);
            x.lib = NULL;
        }
    }
    return x.lib != NULL ? &x : NULL;
}

/* (during the drag an error of the server - a window that went away under the pointer - is ignored instead of ending the program) */
static int fire_xdrag_ignoreError(void* display, void* error) { (void)display; (void)error; return 0; }

static long long fire_xdrag_nowMs(void) {
    struct timespec t;
    clock_gettime(CLOCK_MONOTONIC, &t);
    return (long long)t.tv_sec * 1000 + t.tv_nsec / 1000000;
}

typedef struct FireXDrag {
    FireX11* x;
    void* dpy;
    FireXId src, root;
    FireXId aSelection, aAware, aProxy, aEnter, aPosition, aStatus, aLeave, aDrop, aFinished, aTypeList, aCopy, aMove, aTargets, aUriList, aUtf8Mime, aUtf8String, aTextPlain;
    FireXId types[4];
    int typeCount;
    const char* text;
    char* uris;
    FireXId target, targetProxy;   /* the window under the pointer that takes drops (0: none), and where its messages go */
    int version;
    int waiting;                   /* a position went out and its status has not come back */
    int pendingPos, px, py;        /* the pointer moved meanwhile: the position to send when the status comes */
    FireXId want;                  /* the action asked for: copy, move with Shift */
    int accepted;
    FireXId action;                /* what the target said it would do */
    int releasing, dropped, done, result;
    long long releaseAt, dropAt;
    unsigned long time0;           /* the time of the server when the drag started (0: not known - CurrentTime) */
    long long ms0;
} FireXDrag;

static FireXId fire_xdrag_time(const FireXDrag* d) { return d->time0 == 0 ? 0 : (FireXId)(d->time0 + (unsigned long)(fire_xdrag_nowMs() - d->ms0)); }

/* The events the loop takes out of the queue: the answers of the target to our window, the requests for the selection - nothing else. */
static int fire_xdrag_ours(void* dpy, FireXEvent* e, char* arg) {
    const FireXDrag* d = (const FireXDrag*)(void*)arg;
    (void)dpy;
    if (e->type == FIRE_X_CLIENT_MESSAGE) return e->xclient.window == d->src && (e->xclient.message_type == d->aStatus || e->xclient.message_type == d->aFinished);
    if (e->type == FIRE_X_SELECTION_REQUEST) return e->xselectionrequest.selection == d->aSelection && e->xselectionrequest.owner == d->src;
    return 0;
}
static int fire_xdrag_stamp(void* dpy, FireXEvent* e, char* arg) {
    const FireXDrag* d = (const FireXDrag*)(void*)arg;
    (void)dpy;
    return e->type == FIRE_X_PROPERTY_NOTIFY && e->xproperty.window == d->src && e->xproperty.atom == d->aTypeList;
}

/* A property of 32-bit items (atoms, windows): the first one (0: none). */
static FireXId fire_xdrag_prop32(FireXDrag* d, FireXId w, FireXId prop, FireXId type) {
    FireXId actual = 0, value = 0;
    int format = 0;
    unsigned long n = 0, after = 0;
    unsigned char* data = NULL;
    if (d->x->GetWindowProperty(d->dpy, w, prop, 0, 1, 0, type, &actual, &format, &n, &after, &data) == 0 && data != NULL && actual == type && format == 32 && n >= 1)
        value = ((const unsigned long*)(const void*)data)[0];   /* (format 32 comes as longs) */
    if (data != NULL) d->x->Free(data);
    return value;
}

/* The window under the pointer that takes XDND (version 3 or later), and its version; 0 over nothing, over the window itself, or over one that takes nothing. */
static FireXId fire_xdrag_targetAt(FireXDrag* d, int x, int y, int* version) {
    FireXId w = d->root, child = 0;
    int tx, ty, depth;
    *version = 0;
    for (depth = 0; depth < 16; depth++) {
        FireXId aware;
        if (!d->x->TranslateCoordinates(d->dpy, d->root, w, x, y, &tx, &ty, &child) || child == 0) return 0;
        w = child;
        if (w == d->src) return 0;
        aware = fire_xdrag_prop32(d, w, d->aAware, FIRE_X_ATOM);
        if (aware != 0) {
            if (aware < 3) return 0;
            *version = aware > 5 ? 5 : (int)aware;
            return w;
        }
    }
    return 0;
}

static void fire_xdrag_send(FireXDrag* d, FireXId message, long l1, long l2, long l3, long l4) {
    FireXEvent e;
    memset(&e, 0, sizeof e);
    e.xclient.type = FIRE_X_CLIENT_MESSAGE;
    e.xclient.display = d->dpy;
    e.xclient.window = d->target;
    e.xclient.message_type = message;
    e.xclient.format = 32;
    e.xclient.data.l[0] = (long)d->src;
    e.xclient.data.l[1] = l1; e.xclient.data.l[2] = l2; e.xclient.data.l[3] = l3; e.xclient.data.l[4] = l4;
    d->x->SendEvent(d->dpy, d->targetProxy != 0 ? d->targetProxy : d->target, 0, 0, &e);
}
static void fire_xdrag_position(FireXDrag* d, int x, int y) {
    fire_xdrag_send(d, d->aPosition, 0, ((long)(x & 0xFFFF) << 16) | (long)(y & 0xFFFF), (long)fire_xdrag_time(d), (long)d->want);
    d->waiting = 1;
    d->pendingPos = 0;
}

/* The pointer is at (x, y) of the screen: the target under it hears Enter (and the one it left Leave), then where it is. */
static void fire_xdrag_move(FireXDrag* d, int x, int y, unsigned int mask) {
    int version = 0;
    FireXId t = fire_xdrag_targetAt(d, x, y, &version);
    d->want = (mask & FIRE_X_SHIFT) ? d->aMove : d->aCopy;
    if (t != d->target) {
        if (d->target != 0) fire_xdrag_send(d, d->aLeave, 0, 0, 0, 0);
        d->target = t;
        d->version = version;
        d->waiting = 0; d->pendingPos = 0; d->accepted = 0; d->action = 0;
        d->targetProxy = 0;
        if (t != 0) {
            FireXId proxy = fire_xdrag_prop32(d, t, d->aProxy, FIRE_X_WINDOW);
            /* a proxy counts when it names itself (the rule of XDND) */
            if (proxy != 0 && fire_xdrag_prop32(d, proxy, d->aProxy, FIRE_X_WINDOW) == proxy) d->targetProxy = proxy;
            fire_xdrag_send(d, d->aEnter, ((long)d->version << 24) | (d->typeCount > 3 ? 1 : 0), (long)d->types[0], d->typeCount > 1 ? (long)d->types[1] : 0,
                            d->typeCount > 2 ? (long)d->types[2] : 0);
        }
    }
    if (d->target == 0) return;
    if (d->waiting) { d->pendingPos = 1; d->px = x; d->py = y; return; }   /* (one position at a time: the next when the status came) */
    fire_xdrag_position(d, x, y);
}

/* The button was let go (and the status of the last position is in, or did not come in time): the target gets the drop if it said it takes it. */
static void fire_xdrag_decide(FireXDrag* d) {
    d->releasing = 0;
    if (d->target == 0) { d->done = 1; return; }
    if (d->accepted) {
        fire_xdrag_send(d, d->aDrop, 0, (long)fire_xdrag_time(d), 0, 0);
        d->dropped = 1;
        d->dropAt = fire_xdrag_nowMs();
    } else {
        fire_xdrag_send(d, d->aLeave, 0, 0, 0, 0);
        d->done = 1;
    }
}

static int fire_xdrag_effect(const FireXDrag* d, FireXId action) { return action == d->aMove ? FIRE_DRAG_MOVE : FIRE_DRAG_COPY; }

/* Another program asks for the data (the target after the drop): the text or the paths in the kind it asks for, or the list of kinds (TARGETS). */
static void fire_xdrag_answer(FireXDrag* d, const FireXSelRequest* r) {
    FireXEvent reply;
    FireXId prop = r->property != 0 ? r->property : r->target;   /* (an old client names no property: the target is used) */
    int ok = 0;
    if (r->target == d->aTargets) {
        unsigned long atoms[5];
        int n = 0, i;
        atoms[n++] = d->aTargets;
        for (i = 0; i < d->typeCount; i++) atoms[n++] = d->types[i];
        d->x->ChangeProperty(d->dpy, r->requestor, prop, FIRE_X_ATOM, 32, 0, (const unsigned char*)(const void*)atoms, n);
        ok = 1;
    } else if (r->target == d->aUriList && d->uris != NULL) {
        d->x->ChangeProperty(d->dpy, r->requestor, prop, r->target, 8, 0, (const unsigned char*)d->uris, (int)strlen(d->uris));
        ok = 1;
    } else if ((r->target == d->aUtf8Mime || r->target == d->aUtf8String || r->target == d->aTextPlain) && d->text != NULL && d->text[0] != 0) {
        d->x->ChangeProperty(d->dpy, r->requestor, prop, r->target, 8, 0, (const unsigned char*)d->text, (int)strlen(d->text));
        ok = 1;
    }
    memset(&reply, 0, sizeof reply);
    reply.xselection.type = FIRE_X_SELECTION_NOTIFY;
    reply.xselection.display = d->dpy;
    reply.xselection.requestor = r->requestor;
    reply.xselection.selection = r->selection;
    reply.xselection.target = r->target;
    reply.xselection.property = ok ? prop : 0;
    reply.xselection.time = r->time;
    d->x->SendEvent(d->dpy, r->requestor, 0, 0, &reply);
}

static void fire_xdrag_handle(FireXDrag* d, const FireXEvent* e) {
    if (e->type == FIRE_X_SELECTION_REQUEST) { fire_xdrag_answer(d, &e->xselectionrequest); return; }
    if (e->type != FIRE_X_CLIENT_MESSAGE || (FireXId)e->xclient.data.l[0] != d->target || d->target == 0) return;
    if (e->xclient.message_type == d->aStatus && !d->dropped) {
        d->waiting = 0;
        d->accepted = (e->xclient.data.l[1] & 1) != 0;
        d->action = d->accepted ? ((FireXId)e->xclient.data.l[4] != 0 ? (FireXId)e->xclient.data.l[4] : d->aCopy) : 0;
        if (d->pendingPos) fire_xdrag_position(d, d->px, d->py);   /* (also after the release: the drop lands where the pointer was let go) */
    } else if (e->xclient.message_type == d->aFinished && d->dropped) {
        /* version 5 says whether it worked and what it did; before that a finish is a success with the action of the last status */
        int ok = d->version >= 5 ? (e->xclient.data.l[1] & 1) != 0 : 1;
        FireXId done = d->version >= 5 && (FireXId)e->xclient.data.l[2] != 0 ? (FireXId)e->xclient.data.l[2] : d->action;
        d->result = ok ? fire_xdrag_effect(d, done) : FIRE_DRAG_NONE;
        d->done = 1;
    }
}

/* Drags the text and/or the files out of `window` (on `display`, the connection of the display - it pumps no events meanwhile) until the user lets go;
 * 0 / 1 copy / 2 move. A drag needs a mouse button that is held: without one there is none (0). */
static int fire_dragout_x11(void* display, unsigned long window, const char* text, const char* files) {
    FireX11* x = fire_x11_load();
    FireXDrag d;
    FireXErrorHandler oldHandler;
    FireXId rootRet = 0, childRet = 0;
    int rx = 0, ry = 0, wx = 0, wy = 0, lastX, lastY, fd;
    unsigned int mask = 0;
    unsigned char escape;
    long long start;
    FireXEvent ev;
    if (x == NULL || display == NULL || window == 0) return FIRE_DRAG_NONE;
    if ((text == NULL || text[0] == 0) && (files == NULL || files[0] == 0)) return FIRE_DRAG_NONE;
    memset(&d, 0, sizeof d);
    d.x = x; d.dpy = display; d.src = (FireXId)window; d.text = text;
    d.root = x->DefaultRootWindow(display);
    if (!x->QueryPointer(display, d.root, &rootRet, &childRet, &rx, &ry, &wx, &wy, &mask) || (mask & FIRE_X_BUTTONS) == 0) return FIRE_DRAG_NONE;
    d.aSelection = x->InternAtom(display, "XdndSelection", 0);
    d.aAware = x->InternAtom(display, "XdndAware", 0);
    d.aProxy = x->InternAtom(display, "XdndProxy", 0);
    d.aEnter = x->InternAtom(display, "XdndEnter", 0);
    d.aPosition = x->InternAtom(display, "XdndPosition", 0);
    d.aStatus = x->InternAtom(display, "XdndStatus", 0);
    d.aLeave = x->InternAtom(display, "XdndLeave", 0);
    d.aDrop = x->InternAtom(display, "XdndDrop", 0);
    d.aFinished = x->InternAtom(display, "XdndFinished", 0);
    d.aTypeList = x->InternAtom(display, "XdndTypeList", 0);
    d.aCopy = x->InternAtom(display, "XdndActionCopy", 0);
    d.aMove = x->InternAtom(display, "XdndActionMove", 0);
    d.aTargets = x->InternAtom(display, "TARGETS", 0);
    d.aUriList = x->InternAtom(display, "text/uri-list", 0);
    d.aUtf8Mime = x->InternAtom(display, "text/plain;charset=utf-8", 0);
    d.aUtf8String = x->InternAtom(display, "UTF8_STRING", 0);
    d.aTextPlain = x->InternAtom(display, "text/plain", 0);
    if (files != NULL && files[0] != 0) {
        d.uris = fire_display_uriListFromPaths(files);
        if (d.uris != NULL && d.uris[0] != 0) d.types[d.typeCount++] = d.aUriList;
    }
    if (text != NULL && text[0] != 0) { d.types[d.typeCount++] = d.aUtf8Mime; d.types[d.typeCount++] = d.aUtf8String; d.types[d.typeCount++] = d.aTextPlain; }
    if (d.typeCount == 0) { free(d.uris); return FIRE_DRAG_NONE; }
    escape = x->KeysymToKeycode(display, 0xFF1B);   /* XK_Escape */
    fd = x->ConnectionNumber(display);
    oldHandler = x->SetErrorHandler(fire_xdrag_ignoreError);
    /* the kinds (all of them, for a target that wants more than three) - and the change of the property gives the time of the server for the selection */
    x->ChangeProperty(display, d.src, d.aTypeList, FIRE_X_ATOM, 32, 0, (const unsigned char*)(const void*)d.types, d.typeCount);
    x->Flush(display);
    start = fire_xdrag_nowMs();
    while (fire_xdrag_nowMs() - start < 200) {
        if (x->CheckIfEvent(display, &ev, fire_xdrag_stamp, (char*)(void*)&d)) { d.time0 = ev.xproperty.time; break; }
        { struct pollfd p; p.fd = fd; p.events = POLLIN; p.revents = 0; poll(&p, 1, 5); }
    }
    d.ms0 = fire_xdrag_nowMs();
    x->SetSelectionOwner(display, d.aSelection, d.src, fire_xdrag_time(&d));
    lastX = rx; lastY = ry;
    fire_xdrag_move(&d, rx, ry, mask);
    x->Flush(display);
    while (!d.done) {
        long long now;
        while (!d.done && x->CheckIfEvent(display, &ev, fire_xdrag_ours, (char*)(void*)&d)) fire_xdrag_handle(&d, &ev);
        if (d.done) break;
        now = fire_xdrag_nowMs();
        if (!d.dropped && !d.releasing) {
            char keys[32];
            if (!x->QueryPointer(display, d.root, &rootRet, &childRet, &rx, &ry, &wx, &wy, &mask)) mask = 0;
            memset(keys, 0, sizeof keys);
            x->QueryKeymap(display, keys);
            if (escape != 0 && (keys[escape / 8] & (1 << (escape % 8))) != 0) {
                if (d.target != 0) fire_xdrag_send(&d, d.aLeave, 0, 0, 0, 0);
                d.result = FIRE_DRAG_NONE;
                break;
            }
            if (rx != lastX || ry != lastY) { lastX = rx; lastY = ry; fire_xdrag_move(&d, rx, ry, mask); }
            if ((mask & FIRE_X_BUTTONS) == 0) { d.releasing = 1; d.releaseAt = now; }
        }
        /* let go: as soon as the status of the last position is in (half a second at most) */
        if (d.releasing && ((!d.waiting && !d.pendingPos) || now - d.releaseAt > 500)) fire_xdrag_decide(&d);
        /* dropped: the target fetches the data and says it is finished (a target that does not, after 5 s: what its status said) */
        if (d.dropped && !d.done && now - d.dropAt > 5000) { d.result = d.accepted ? fire_xdrag_effect(&d, d.action) : FIRE_DRAG_NONE; break; }
        if (d.done) break;
        x->Flush(display);
        { struct pollfd p; p.fd = fd; p.events = POLLIN; p.revents = 0; poll(&p, 1, 10); }
    }
    x->SetSelectionOwner(display, d.aSelection, 0, fire_xdrag_time(&d));
    x->DeleteProperty(display, d.src, d.aTypeList);
    x->Sync(display, 0);   /* (the errors of the drag come in while it still ignores them; the events stay in the queue) */
    x->SetErrorHandler(oldHandler);
    free(d.uris);
    return d.result;
}

/* ======================================================================================================================================================
 * Wayland: the source side of wl_data_device (start_drag with a wl_data_source), on the display's own connection
 * ====================================================================================================================================================== */
/* A drag on Wayland needs the serial of the button press that holds the pointer (the implicit grab). So this part listens itself:
 * on the display's wl_display it makes an event queue of its own, binds wl_seat and wl_data_device_manager (version 3:
 * the actions) through a registry on that queue and asks the seat for a wl_pointer of its own - the compositor tells every wl_pointer of the client about the
 * button presses, so this one learns the serials. The display reads the events of the connection into both queues when it pumps and dispatches fire's queue
 * right after (fire_dragout_wlPump). The drag itself: a wl_data_source offering text/uri-list and/or text/plain;charset=utf-8
 * (also UTF8_STRING, text/plain), the actions copy and move, a wl_data_device of the seat made for the drag, start_drag with the origin surface (the window's) and the serial of the press; then fire's queue is dispatched until the source says dnd_finished (the last
 * `action`: copy 1, move 2) or cancelled (0) - the `send` requests are answered by writing to the descriptor. The compositor takes the pointer for the drag
 * (the display gets a leave and raises its buttons). libwayland-client is loaded with dlopen (the display has loaded it already: the same library, the same interfaces); the
 * few signatures it needs are declared here (no headers). A release that reaches fire's pointer during the loop means the compositor did not take the drag. */
#ifdef FIRE_DRAGOUT_WANT_WAYLAND   /* (fire_display_desktop.h asks for it when it has the Wayland display) */
#define FIRE_DRAGOUT_WAYLAND 1
#include <fcntl.h>
#include <signal.h>
#include <stdint.h>
#include <unistd.h>
#include <errno.h>

typedef void (*FireWlFn)(void);
typedef struct FireWlLib {
    int tried;
    void* lib;
    const void *iRegistry, *iSeat, *iPointer, *iDdm, *iDataDevice, *iDataSource;
    void* (*marshalConstructor)(void*, uint32_t, const void*, ...);
    void* (*marshalConstructorVersioned)(void*, uint32_t, const void*, uint32_t, ...);
    void (*marshal)(void*, uint32_t, ...);
    int (*addListener)(void*, FireWlFn*, void*);
    void (*destroy)(void*);
    void* (*createWrapper)(void*);
    void (*wrapperDestroy)(void*);
    void (*setQueue)(void*, void*);
    void* (*createQueue)(void*);
    void (*queueDestroy)(void*);
    int (*dispatchQueuePending)(void*, void*);
    int (*roundtripQueue)(void*, void*);
    int (*prepareReadQueue)(void*, void*);
    int (*readEvents)(void*);
    void (*cancelRead)(void*);
    int (*flush)(void*);
    int (*getFd)(void*);
} FireWlLib;

static int fire_wl_bind(FireWlLib* l, const char* name, void* fn) {
    void* p = dlsym(l->lib, name);
    memcpy(fn, &p, sizeof p);
    return p != NULL;
}
/* libwayland-client, loaded once (NULL: not there). */
static FireWlLib* fire_wl_load(void) {
    static FireWlLib l;
    if (!l.tried) {
        l.tried = 1;
        l.lib = dlopen("libwayland-client.so.0", RTLD_LAZY | RTLD_LOCAL);
        if (l.lib == NULL) l.lib = dlopen("libwayland-client.so", RTLD_LAZY | RTLD_LOCAL);
        if (l.lib != NULL) {
            l.iRegistry = dlsym(l.lib, "wl_registry_interface");
            l.iSeat = dlsym(l.lib, "wl_seat_interface");
            l.iPointer = dlsym(l.lib, "wl_pointer_interface");
            l.iDdm = dlsym(l.lib, "wl_data_device_manager_interface");
            l.iDataDevice = dlsym(l.lib, "wl_data_device_interface");
            l.iDataSource = dlsym(l.lib, "wl_data_source_interface");
            if (!(l.iRegistry && l.iSeat && l.iPointer && l.iDdm && l.iDataDevice && l.iDataSource
                  && fire_wl_bind(&l, "wl_proxy_marshal_constructor", &l.marshalConstructor)
                  && fire_wl_bind(&l, "wl_proxy_marshal_constructor_versioned", &l.marshalConstructorVersioned)
                  && fire_wl_bind(&l, "wl_proxy_marshal", &l.marshal) && fire_wl_bind(&l, "wl_proxy_add_listener", &l.addListener)
                  && fire_wl_bind(&l, "wl_proxy_destroy", &l.destroy) && fire_wl_bind(&l, "wl_proxy_create_wrapper", &l.createWrapper)
                  && fire_wl_bind(&l, "wl_proxy_wrapper_destroy", &l.wrapperDestroy) && fire_wl_bind(&l, "wl_proxy_set_queue", &l.setQueue)
                  && fire_wl_bind(&l, "wl_display_create_queue", &l.createQueue) && fire_wl_bind(&l, "wl_event_queue_destroy", &l.queueDestroy) && fire_wl_bind(&l, "wl_display_dispatch_queue_pending", &l.dispatchQueuePending)
                  && fire_wl_bind(&l, "wl_display_roundtrip_queue", &l.roundtripQueue) && fire_wl_bind(&l, "wl_display_prepare_read_queue", &l.prepareReadQueue)
                  && fire_wl_bind(&l, "wl_display_read_events", &l.readEvents) && fire_wl_bind(&l, "wl_display_cancel_read", &l.cancelRead)
                  && fire_wl_bind(&l, "wl_display_flush", &l.flush) && fire_wl_bind(&l, "wl_display_get_fd", &l.getFd))) {
                dlclose(l.lib);
                l.lib = NULL;
            }
        }
    }
    return l.lib != NULL ? &l : NULL;
}
/* (struct wl_interface begins with its name and its version) */
static const char* fire_wl_name(const void* iface) { return *(const char* const*)iface; }
static uint32_t fire_wl_version(const void* iface) { return (uint32_t)*(const int*)((const char*)iface + sizeof(const char*)); }

/* What fire keeps of the connection (one: the display has one wl_display); made once by fire_dragout_wlWatch. */
typedef struct FireWlState {
    FireWlLib* l;
    void* display;
    void* queue;
    void* registry;
    void* seat;
    uint32_t seatName, seatVersion;
    void* ddm;
    uint32_t ddmVersion;
    void* pointer;
    uint32_t pressSerial;   /* the serial of the last press of a button */
    int held;               /* buttons that are held */
    int released;           /* (during a drag: a button was let go - the compositor did not take the drag) */
    int failed;
} FireWlState;
static FireWlState fire_wl;

static void fire_wl_pointerEnter(void* data, void* p, uint32_t serial, void* surface, int32_t x, int32_t y) { (void)data; (void)p; (void)serial; (void)surface; (void)x; (void)y; }
static void fire_wl_pointerLeave(void* data, void* p, uint32_t serial, void* surface) { (void)data; (void)p; (void)serial; (void)surface; }
static void fire_wl_pointerMotion(void* data, void* p, uint32_t time, int32_t x, int32_t y) { (void)data; (void)p; (void)time; (void)x; (void)y; }
static void fire_wl_pointerButton(void* data, void* p, uint32_t serial, uint32_t time, uint32_t button, uint32_t state) {
    FireWlState* s = (FireWlState*)data;
    (void)p; (void)time; (void)button;
    if (state == 1) { s->pressSerial = serial; s->held++; }
    else { if (s->held > 0) s->held--; s->released = 1; }
}
static void fire_wl_pointerAxis(void* data, void* p, uint32_t time, uint32_t axis, int32_t value) { (void)data; (void)p; (void)time; (void)axis; (void)value; }
static void fire_wl_pointerFrame(void* data, void* p) { (void)data; (void)p; }
static void fire_wl_pointerAxisSource(void* data, void* p, uint32_t source) { (void)data; (void)p; (void)source; }
static void fire_wl_pointerAxisStop(void* data, void* p, uint32_t time, uint32_t axis) { (void)data; (void)p; (void)time; (void)axis; }
static void fire_wl_pointerAxisDiscrete(void* data, void* p, uint32_t axis, int32_t discrete) { (void)data; (void)p; (void)axis; (void)discrete; }
static const struct {
    void (*enter)(void*, void*, uint32_t, void*, int32_t, int32_t);
    void (*leave)(void*, void*, uint32_t, void*);
    void (*motion)(void*, void*, uint32_t, int32_t, int32_t);
    void (*button)(void*, void*, uint32_t, uint32_t, uint32_t, uint32_t);
    void (*axis)(void*, void*, uint32_t, uint32_t, int32_t);
    void (*frame)(void*, void*);
    void (*axisSource)(void*, void*, uint32_t);
    void (*axisStop)(void*, void*, uint32_t, uint32_t);
    void (*axisDiscrete)(void*, void*, uint32_t, int32_t);
} fire_wl_pointerListener = { fire_wl_pointerEnter, fire_wl_pointerLeave, fire_wl_pointerMotion, fire_wl_pointerButton, fire_wl_pointerAxis,
                              fire_wl_pointerFrame, fire_wl_pointerAxisSource, fire_wl_pointerAxisStop, fire_wl_pointerAxisDiscrete };

static void fire_wl_seatCapabilities(void* data, void* seat, uint32_t caps) {
    FireWlState* s = (FireWlState*)data;
    if ((caps & 1u) && s->pointer == NULL) {   /* WL_SEAT_CAPABILITY_POINTER */
        s->pointer = s->l->marshalConstructor(seat, 0, s->l->iPointer, NULL);   /* wl_seat.get_pointer */
        if (s->pointer != NULL) s->l->addListener(s->pointer, (FireWlFn*)(void*)&fire_wl_pointerListener, s);
    } else if (!(caps & 1u) && s->pointer != NULL) {
        if (s->seatVersion >= 3) s->l->marshal(s->pointer, 1);   /* wl_pointer.release */
        s->l->destroy(s->pointer);
        s->pointer = NULL;
        s->held = 0;
    }
}
static void fire_wl_seatName(void* data, void* seat, const char* name) { (void)data; (void)seat; (void)name; }
static const struct { void (*capabilities)(void*, void*, uint32_t); void (*name)(void*, void*, const char*); } fire_wl_seatListener = { fire_wl_seatCapabilities, fire_wl_seatName };

static void fire_wl_global(void* data, void* registry, uint32_t name, const char* iface, uint32_t version) {
    FireWlState* s = (FireWlState*)data;
    if (strcmp(iface, "wl_seat") == 0 && s->seat == NULL) {
        /* (up to version 5: the events of the pointer that the listener above knows) */
        uint32_t v = version < 5 ? version : 5;
        if (v > fire_wl_version(s->l->iSeat)) v = fire_wl_version(s->l->iSeat);
        s->seat = s->l->marshalConstructorVersioned(registry, 0, s->l->iSeat, v, name, fire_wl_name(s->l->iSeat), v, NULL);   /* wl_registry.bind */
        s->seatName = name; s->seatVersion = v;
        if (s->seat != NULL) s->l->addListener(s->seat, (FireWlFn*)(void*)&fire_wl_seatListener, s);
    } else if (strcmp(iface, "wl_data_device_manager") == 0 && s->ddm == NULL && version >= 3 && fire_wl_version(s->l->iDdm) >= 3) {
        s->ddm = s->l->marshalConstructorVersioned(registry, 0, s->l->iDdm, 3, name, fire_wl_name(s->l->iDdm), (uint32_t)3, NULL);
        s->ddmVersion = 3;
    }
}
static void fire_wl_globalRemove(void* data, void* registry, uint32_t name) {
    FireWlState* s = (FireWlState*)data;
    (void)registry;
    if (s->seat != NULL && name == s->seatName) {
        /* (the seat went away: its pointer with it; a later drag finds no seat) */
        if (s->pointer != NULL) { s->l->destroy(s->pointer); s->pointer = NULL; }
        s->l->destroy(s->seat);
        s->seat = NULL;
        s->held = 0;
    }
}
static const struct { void (*global)(void*, void*, uint32_t, const char*, uint32_t); void (*remove)(void*, void*, uint32_t); } fire_wl_registryListener = { fire_wl_global, fire_wl_globalRemove };

/* Starts listening on `display` (the display's wl_display) the first time; then dispatches fire's queue (what the display has read for it). The display calls
 * it after it pumped, so that the press that starts a drag is known. */
static void fire_dragout_wlPump(void* display) {
    FireWlState* s = &fire_wl;
    if (display == NULL || s->failed) return;
    if (s->display == NULL) {
        void* wrapper;
        s->l = fire_wl_load();
        if (s->l == NULL) { s->failed = 1; return; }
        s->display = display;
        s->queue = s->l->createQueue(display);
        wrapper = s->queue ? s->l->createWrapper(display) : NULL;
        if (wrapper == NULL) { s->failed = 1; return; }
        s->l->setQueue(wrapper, s->queue);
        s->registry = s->l->marshalConstructor(wrapper, 1, s->l->iRegistry, NULL);   /* wl_display.get_registry (on fire's queue) */
        s->l->wrapperDestroy(wrapper);
        if (s->registry == NULL) { s->failed = 1; return; }
        s->l->addListener(s->registry, (FireWlFn*)(void*)&fire_wl_registryListener, s);
        /* the globals, then the capabilities of the seat (and the pointer) */
        s->l->roundtripQueue(display, s->queue);
        s->l->roundtripQueue(display, s->queue);
        return;
    }
    if (display == s->display) s->l->dispatchQueuePending(display, s->queue);
}

/* The last window of the display closes (it may close its wl_display right after): everything fire made on the connection is given back while it is still
 * there; the next window starts afresh. */
static void fire_dragout_wlForget(void) {
    FireWlState* s = &fire_wl;
    FireWlLib* l = s->l;
    if (l != NULL && s->display != NULL && !s->failed) {
        if (s->pointer != NULL) { if (s->seatVersion >= 3) l->marshal(s->pointer, 1); l->destroy(s->pointer); }   /* wl_pointer.release */
        if (s->seat != NULL) { if (s->seatVersion >= 5) l->marshal(s->seat, 3); l->destroy(s->seat); }             /* wl_seat.release */
        if (s->ddm != NULL) l->destroy(s->ddm);
        if (s->registry != NULL) l->destroy(s->registry);
        l->flush(s->display);
    }
    if (l != NULL && s->queue != NULL) l->queueDestroy(s->queue);
    memset(s, 0, sizeof *s);
    s->l = l;
}

/* (one wait for the connection: up to `ms`, then what came in is dispatched) */
static void fire_wl_wait(FireWlState* s, int ms) {
    struct pollfd p;
    while (s->l->prepareReadQueue(s->display, s->queue) != 0) s->l->dispatchQueuePending(s->display, s->queue);
    s->l->flush(s->display);
    p.fd = s->l->getFd(s->display); p.events = POLLIN; p.revents = 0;
    if (poll(&p, 1, ms) > 0 && (p.revents & POLLIN)) s->l->readEvents(s->display);
    else s->l->cancelRead(s->display);
    s->l->dispatchQueuePending(s->display, s->queue);
}

typedef struct FireWlDrag {
    FireWlLib* l;
    const char* text;
    char* uris;
    uint32_t action;
    int accepted, dropped, done, result;
    void** offers;          /* (what the compositor offers the data device of the drag - over our own window: destroyed at the end) */
    size_t offerCount, offerCap;
} FireWlDrag;

static void fire_wl_deviceOffer(void* data, void* dev, void* offer) {
    FireWlDrag* d = (FireWlDrag*)data;
    (void)dev;
    if (offer == NULL) return;
    if (d->offerCount == d->offerCap) {
        size_t cap = d->offerCap ? d->offerCap * 2 : 4;
        void** grown = (void**)realloc(d->offers, cap * sizeof *grown);
        if (grown == NULL) { d->l->marshal(offer, 2); d->l->destroy(offer); return; }   /* (wl_data_offer.destroy) */
        d->offers = grown; d->offerCap = cap;
    }
    d->offers[d->offerCount++] = offer;
}
static void fire_wl_deviceEnter(void* data, void* dev, uint32_t serial, void* surface, int32_t x, int32_t y, void* offer) { (void)data; (void)dev; (void)serial; (void)surface; (void)x; (void)y; (void)offer; }
static void fire_wl_deviceLeave(void* data, void* dev) { (void)data; (void)dev; }
static void fire_wl_deviceMotion(void* data, void* dev, uint32_t time, int32_t x, int32_t y) { (void)data; (void)dev; (void)time; (void)x; (void)y; }
static void fire_wl_deviceDrop(void* data, void* dev) { (void)data; (void)dev; }
static void fire_wl_deviceSelection(void* data, void* dev, void* offer) { (void)data; (void)dev; (void)offer; }
static const struct {
    void (*dataOffer)(void*, void*, void*);
    void (*enter)(void*, void*, uint32_t, void*, int32_t, int32_t, void*);
    void (*leave)(void*, void*);
    void (*motion)(void*, void*, uint32_t, int32_t, int32_t);
    void (*drop)(void*, void*);
    void (*selection)(void*, void*, void*);
} fire_wl_deviceListener = { fire_wl_deviceOffer, fire_wl_deviceEnter, fire_wl_deviceLeave, fire_wl_deviceMotion, fire_wl_deviceDrop, fire_wl_deviceSelection };

static void fire_wl_sourceTarget(void* data, void* src, const char* mime) { FireWlDrag* d = (FireWlDrag*)data; (void)src; d->accepted = mime != NULL; }
/* The target wants the data: written to the descriptor (not blocking for more than 5 s per piece; a closed pipe is no signal that ends the program). */
static void fire_wl_sourceSend(void* data, void* src, const char* mime, int32_t fd) {
    FireWlDrag* d = (FireWlDrag*)data;
    const char* bytes = NULL;
    size_t n = 0, done = 0;
    struct sigaction ignore, old;
    (void)src;
    if (strcmp(mime, "text/uri-list") == 0) bytes = d->uris;
    else if (d->text != NULL) bytes = d->text;
    if (bytes != NULL) n = strlen(bytes);
    memset(&ignore, 0, sizeof ignore);
    ignore.sa_handler = SIG_IGN;
    sigemptyset(&ignore.sa_mask);
    sigaction(SIGPIPE, &ignore, &old);
    fcntl(fd, F_SETFL, fcntl(fd, F_GETFL) | O_NONBLOCK);
    while (done < n) {
        ssize_t w = write(fd, bytes + done, n - done);
        if (w > 0) { done += (size_t)w; continue; }
        if (w < 0 && (errno == EAGAIN || errno == EWOULDBLOCK)) {
            struct pollfd p;
            p.fd = fd; p.events = POLLOUT; p.revents = 0;
            if (poll(&p, 1, 5000) > 0) continue;
        } else if (w < 0 && errno == EINTR) continue;
        break;
    }
    sigaction(SIGPIPE, &old, NULL);
    close(fd);
}
static void fire_wl_sourceCancelled(void* data, void* src) { FireWlDrag* d = (FireWlDrag*)data; (void)src; d->result = FIRE_DRAG_NONE; d->done = 1; }
static void fire_wl_sourceDropPerformed(void* data, void* src) { FireWlDrag* d = (FireWlDrag*)data; (void)src; d->dropped = 1; }
static void fire_wl_sourceFinished(void* data, void* src) {
    FireWlDrag* d = (FireWlDrag*)data;
    (void)src;
    d->result = d->action == 2u ? FIRE_DRAG_MOVE : FIRE_DRAG_COPY;   /* WL_DATA_DEVICE_MANAGER_DND_ACTION_MOVE */
    d->done = 1;
}
static void fire_wl_sourceAction(void* data, void* src, uint32_t action) { FireWlDrag* d = (FireWlDrag*)data; (void)src; d->action = action; }
static const struct {
    void (*target)(void*, void*, const char*);
    void (*send)(void*, void*, const char*, int32_t);
    void (*cancelled)(void*, void*);
    void (*dropPerformed)(void*, void*);
    void (*finished)(void*, void*);
    void (*action)(void*, void*, uint32_t);
} fire_wl_sourceListener = { fire_wl_sourceTarget, fire_wl_sourceSend, fire_wl_sourceCancelled, fire_wl_sourceDropPerformed, fire_wl_sourceFinished, fire_wl_sourceAction };

/* Whether a drag can start on `display` (Wayland: fire listens on it and has the seat, its pointer and the data device manager). */
static int fire_dragout_wlReady(void* display) {
    return display != NULL && !fire_wl.failed && fire_wl.display == display && fire_wl.seat != NULL && fire_wl.pointer != NULL && fire_wl.ddm != NULL;
}

/* Drags the text and/or the files out of the window with `surface` (on `display`, the display's) until the user lets go;
 * 0 / 1 copy / 2 move. Without a button that is held (or before fire listens on the connection) there is no drag. */
static int fire_dragout_wayland(void* display, void* surface, const char* text, const char* files) {
    FireWlState* s = &fire_wl;
    FireWlDrag d;
    void *source, *device;
    long long start, dropAt = 0;
    size_t i;
    if (display == NULL || surface == NULL) return FIRE_DRAG_NONE;
    if ((text == NULL || text[0] == 0) && (files == NULL || files[0] == 0)) return FIRE_DRAG_NONE;
    fire_dragout_wlPump(display);
    if (!fire_dragout_wlReady(display) || s->held <= 0) return FIRE_DRAG_NONE;
    memset(&d, 0, sizeof d);
    d.l = s->l;
    d.text = text != NULL && text[0] != 0 ? text : NULL;
    if (files != NULL && files[0] != 0) {
        d.uris = fire_display_uriListFromPaths(files);
        if (d.uris != NULL && d.uris[0] == 0) { free(d.uris); d.uris = NULL; }
    }
    if (d.uris == NULL && d.text == NULL) return FIRE_DRAG_NONE;
    source = s->l->marshalConstructor(s->ddm, 0, s->l->iDataSource, NULL);              /* wl_data_device_manager.create_data_source */
    device = s->l->marshalConstructor(s->ddm, 1, s->l->iDataDevice, NULL, s->seat);    /* wl_data_device_manager.get_data_device */
    if (source == NULL || device == NULL) {
        if (source != NULL) { s->l->marshal(source, 1); s->l->destroy(source); }
        if (device != NULL) { s->l->marshal(device, 2); s->l->destroy(device); }
        free(d.uris);
        return FIRE_DRAG_NONE;
    }
    s->l->addListener(source, (FireWlFn*)(void*)&fire_wl_sourceListener, &d);
    s->l->addListener(device, (FireWlFn*)(void*)&fire_wl_deviceListener, &d);
    if (d.uris != NULL) s->l->marshal(source, 0, "text/uri-list");                    /* wl_data_source.offer */
    if (d.text != NULL) {
        s->l->marshal(source, 0, "text/plain;charset=utf-8");
        s->l->marshal(source, 0, "UTF8_STRING");
        s->l->marshal(source, 0, "text/plain");
    }
    s->l->marshal(source, 2, (uint32_t)(1u | 2u));                                     /* wl_data_source.set_actions: copy, move */
    s->released = 0;
    s->l->marshal(device, 0, source, surface, (void*)NULL, s->pressSerial);           /* wl_data_device.start_drag */
    s->l->flush(display);
    start = fire_xdrag_nowMs();
    while (!d.done) {
        long long now;
        fire_wl_wait(s, 50);
        if (d.done) break;
        now = fire_xdrag_nowMs();
        /* a release on fire's pointer: the compositor kept the pointer for the window - no drag (one that never answers ends after 10 minutes) */
        if (s->released && !d.dropped) { d.result = FIRE_DRAG_NONE; break; }
        if (now - start > 600000) { d.result = FIRE_DRAG_NONE; break; }
        /* dropped: the target fetches the data and says it is finished (one that does not, after 5 s: what it took and the last action) */
        if (d.dropped && dropAt == 0) dropAt = now;
        if (dropAt != 0 && now - dropAt > 5000) { d.result = !d.accepted ? FIRE_DRAG_NONE : d.action == 2u ? FIRE_DRAG_MOVE : FIRE_DRAG_COPY; break; }
    }
    s->l->marshal(source, 1);   /* wl_data_source.destroy */
    s->l->destroy(source);
    s->l->marshal(device, 2);   /* wl_data_device.release */
    s->l->destroy(device);
    for (i = 0; i < d.offerCount; i++) { s->l->marshal(d.offers[i], 2); s->l->destroy(d.offers[i]); }   /* wl_data_offer.destroy */
    s->l->flush(display);
    s->held = 0;
    free(d.offers);
    free(d.uris);
    return d.result;
}
#endif /* FIRE_DRAGOUT_WANT_WAYLAND */
#endif /* X11, Wayland */

#endif
