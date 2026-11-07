/* fire native platform package "bios16" (ROADMAP 13.5): the minimal C library of a program without an operating system, in real mode. The C library of Open
 * Watcom stays (strings, formatting, math, setjmp, its stdio); what it would ask DOS for comes from here:
 *  - the console: the screen through the BIOS (int 10h) and the first serial port (COM1, 115200 8N1) for output, the keyboard (int 16h) and COM1 for input;
 *  - the heap: the conventional memory between the end of the program (BP from the boot sector) and the top the BIOS reports (int 12h) - malloc/calloc/realloc/
 *    free and halloc/hfree (blocks over 64 KB) -, and a near heap of 8 KB in DGROUP for the C library's own structures (stdio, arguments);
 *  - int 21h: the services the C library uses (console, version, time and date of the BIOS clock, vectors, the end of the program); everything else fails as
 *    "invalid function" - there are no files;
 *  - the end: port 0xF4 (QEMU's debug exit) with the exit code, then the machine waits with a message on the screen.
 * Every definition here is the program's own (no `static`): the linker takes them instead of the ones of the C library. The startup code (fire_bios16_start.asm)
 * calls fire_bios_cmain. Only the translation unit of the program includes this file. */
#ifndef FIRE_BIOS16_LIBC_H
#define FIRE_BIOS16_LIBC_H

#include <stddef.h>
#include <stdlib.h>
#include <string.h>
#include <dos.h>
#include <i86.h>
#include <conio.h>
#include <bios.h>

#ifndef FIRE_BIOS_SERIAL
#define FIRE_BIOS_SERIAL 0x3F8   /* (the console also on COM1; 0: the screen only) */
#endif

/* ---- the console ------------------------------------------------------------------------------------------------------------ */
static int fire_bios_serialReady;
static void fire_bios_serialInit(void) {
#if FIRE_BIOS_SERIAL
    outp(FIRE_BIOS_SERIAL + 1, 0);       /* no interrupts */
    outp(FIRE_BIOS_SERIAL + 3, 0x80);    /* the divisor: 1 = 115200 baud */
    outp(FIRE_BIOS_SERIAL + 0, 1);
    outp(FIRE_BIOS_SERIAL + 1, 0);
    outp(FIRE_BIOS_SERIAL + 3, 3);       /* 8N1 */
    outp(FIRE_BIOS_SERIAL + 2, 0xC7);    /* FIFO */
    outp(FIRE_BIOS_SERIAL + 4, 3);
    fire_bios_serialReady = 1;
#endif
}
static void fire_bios_serialPut(char c) {
#if FIRE_BIOS_SERIAL
    unsigned spin = 0;
    if (!fire_bios_serialReady) return;
    while (!(inp(FIRE_BIOS_SERIAL + 5) & 0x20)) { if (++spin == 0) { fire_bios_serialReady = 0; return; } }   /* (no UART there: stop asking) */
    outp(FIRE_BIOS_SERIAL, c);
#else
    (void)c;
#endif
}
static void fire_bios_screenPut(char c) {
    union REGS r;
    r.h.ah = 0x0E;
    r.h.al = (unsigned char)c;
    r.x.bx = 7;
    int86(0x10, &r, &r);
}
void fire_bios_putc(char c) {
    if (c == '\n') { fire_bios_serialPut('\r'); fire_bios_screenPut('\r'); }
    fire_bios_serialPut(c);
    fire_bios_screenPut(c);
}
/* A character from the keyboard or COM1, whichever comes first (waits). */
int fire_bios_getc(void) {
    for (;;) {
#if FIRE_BIOS_SERIAL
        if (fire_bios_serialReady && (inp(FIRE_BIOS_SERIAL + 5) & 1)) return inp(FIRE_BIOS_SERIAL);
#endif
        if (_bios_keybrd(_KEYBRD_READY)) return _bios_keybrd(_KEYBRD_READ) & 0xFF;
    }
}

/* ---- the end ------------------------------------------------------------------------------------------------------------------ */
void fire_bios_exit(int code) {
    static const char text[] = "\r\nThe program has ended. The machine can be switched off.\r\n";
    const char* p;
    outp(0xF4, code & 0xFF);   /* (QEMU with -device isa-debug-exit ends here with the code) */
    for (p = text; *p; p++) fire_bios_screenPut(*p);
    for (;;) { _disable(); }
}
/* (a fatal message of the C library: the text, then the end) */
void fire_bios_exitmsg(const char __far* message, int code) {
    while (*message) fire_bios_putc(*message++);
    fire_bios_exit(code);
}

/* ---- the heap ----------------------------------------------------------------------------------------------------------------- */
/* A block is a paragraph of header (its size in paragraphs, the next free block) and the paragraphs behind it; free blocks are a list by address and are
 * joined with their neighbours when one is freed. */
extern unsigned fire_bios_heapseg;   /* (fire_bios16_start.asm: the first free paragraph behind the program) */
typedef struct FireBiosBlk { unsigned long paras; unsigned next; unsigned magic; } FireBiosBlk;
#define FIRE_BIOS_BLK(seg) ((FireBiosBlk __far*)MK_FP((seg), 0))
#define FIRE_BIOS_FREE 0xF4EEu
#define FIRE_BIOS_USED 0x05EDu
static unsigned fire_bios_freeList;
static int fire_bios_heapReady;

static void fire_bios_heapInit(void) {
    union REGS r;
    unsigned top;
    int86(0x12, &r, &r);
    top = (unsigned)r.x.ax * 64u;   /* KB -> paragraphs (below the extended BIOS data area) */
    fire_bios_freeList = fire_bios_heapseg;
    FIRE_BIOS_BLK(fire_bios_freeList)->paras = (unsigned long)(top - fire_bios_heapseg);
    FIRE_BIOS_BLK(fire_bios_freeList)->next = 0;
    FIRE_BIOS_BLK(fire_bios_freeList)->magic = FIRE_BIOS_FREE;
    fire_bios_heapReady = 1;
}

static void __far* fire_bios_alloc(unsigned long bytes) {
    unsigned long need = (bytes + 15ul) / 16ul + 1ul;
    unsigned prev = 0, cur;
    if (!fire_bios_heapReady) fire_bios_heapInit();
    for (cur = fire_bios_freeList; cur != 0; prev = cur, cur = FIRE_BIOS_BLK(cur)->next) {
        FireBiosBlk __far* b = FIRE_BIOS_BLK(cur);
        if (b->paras < need) continue;
        if (b->paras > need + 1ul) {   /* split: the rest stays free */
            unsigned rest = cur + (unsigned)need;
            FIRE_BIOS_BLK(rest)->paras = b->paras - need;
            FIRE_BIOS_BLK(rest)->next = b->next;
            FIRE_BIOS_BLK(rest)->magic = FIRE_BIOS_FREE;
            b->paras = need;
            if (prev) FIRE_BIOS_BLK(prev)->next = rest; else fire_bios_freeList = rest;
        } else if (prev) FIRE_BIOS_BLK(prev)->next = b->next;
        else fire_bios_freeList = b->next;
        b->magic = FIRE_BIOS_USED;
        b->next = 0;
        return MK_FP(cur + 1, 0);
    }
    return NULL;
}

static unsigned fire_bios_blockOf(const void __far* p) { return FP_SEG(p) + (FP_OFF(p) >> 4) - 1; }

static void fire_bios_free(void __far* p) {
    unsigned seg, prev = 0, cur;
    if (p == NULL) return;
    seg = fire_bios_blockOf(p);
    if (FIRE_BIOS_BLK(seg)->magic != FIRE_BIOS_USED) return;   /* (not a block of this heap) */
    FIRE_BIOS_BLK(seg)->magic = FIRE_BIOS_FREE;
    for (cur = fire_bios_freeList; cur != 0 && cur < seg; prev = cur, cur = FIRE_BIOS_BLK(cur)->next) {}
    FIRE_BIOS_BLK(seg)->next = cur;
    if (prev) FIRE_BIOS_BLK(prev)->next = seg; else fire_bios_freeList = seg;
    if (cur && seg + FIRE_BIOS_BLK(seg)->paras == cur) { FIRE_BIOS_BLK(seg)->paras += FIRE_BIOS_BLK(cur)->paras; FIRE_BIOS_BLK(seg)->next = FIRE_BIOS_BLK(cur)->next; }
    if (prev && prev + FIRE_BIOS_BLK(prev)->paras == seg) { FIRE_BIOS_BLK(prev)->paras += FIRE_BIOS_BLK(seg)->paras; FIRE_BIOS_BLK(prev)->next = FIRE_BIOS_BLK(seg)->next; }
}

void __far* malloc(size_t n) { return fire_bios_alloc(n ? n : 1); }
void __far* _fmalloc(size_t n) { return fire_bios_alloc(n ? n : 1); }
void free(void __far* p) { fire_bios_free(p); }
void _ffree(void __far* p) { fire_bios_free(p); }
void __far* calloc(size_t n, size_t size) {
    unsigned long bytes = (unsigned long)n * size;
    void __far* p = bytes <= 0xFFF0ul ? fire_bios_alloc(bytes ? bytes : 1) : NULL;
    if (p) _fmemset(p, 0, (size_t)bytes);
    return p;
}
void __far* _fcalloc(size_t n, size_t size) { return calloc(n, size); }
void __far* realloc(void __far* p, size_t n) {
    void __far* q;
    unsigned long have;
    if (p == NULL) return malloc(n);
    if (n == 0) { free(p); return NULL; }
    have = (FIRE_BIOS_BLK(fire_bios_blockOf(p))->paras - 1ul) * 16ul;
    if (have >= n) return p;
    q = fire_bios_alloc(n);
    if (q) { _fmemcpy(q, p, (size_t)have); fire_bios_free(p); }
    return q;
}
void __far* _frealloc(void __far* p, size_t n) { return realloc(p, n); }
size_t _msize(void __far* p) { return p ? (size_t)((FIRE_BIOS_BLK(fire_bios_blockOf(p))->paras - 1ul) * 16ul) : 0; }
void __huge* halloc(long n, size_t size) {
    unsigned long bytes = (unsigned long)n * size, i;
    char __huge* p = (char __huge*)fire_bios_alloc(bytes);
    if (p) for (i = 0; i < bytes; i++) p[i] = 0;
    return (void __huge*)p;
}
void hfree(void __huge* p) { fire_bios_free((void __far*)p); }

/* The near heap (_nmalloc: stdio's structures, the arguments, the copy of the environment): a fixed area in DGROUP; a block is a word of size (bit 0: used). */
#ifndef FIRE_BIOS_NEAR_HEAP
#define FIRE_BIOS_NEAR_HEAP 8192u
#endif
static unsigned char __near fire_bios_near[FIRE_BIOS_NEAR_HEAP];
static int fire_bios_nearReady;
void __near* _nmalloc(size_t n) {
    unsigned need = (unsigned)((n + 3u) & ~1u), at = 0;
    if (!fire_bios_nearReady) { *(unsigned __near*)fire_bios_near = FIRE_BIOS_NEAR_HEAP; fire_bios_nearReady = 1; }
    if (n > FIRE_BIOS_NEAR_HEAP - 2u) return NULL;
    while (at < FIRE_BIOS_NEAR_HEAP) {
        unsigned __near* h = (unsigned __near*)(fire_bios_near + at);
        unsigned size = *h & ~1u;
        if (!(*h & 1u)) {
            while (at + size < FIRE_BIOS_NEAR_HEAP && !(*(unsigned __near*)(fire_bios_near + at + size) & 1u)) size += *(unsigned __near*)(fire_bios_near + at + size);
            *h = size;
            if (size >= need) {
                if (size - need >= 4u) { *(unsigned __near*)(fire_bios_near + at + need) = size - need; size = need; }
                *h = size | 1u;
                return (void __near*)(h + 1);
            }
        }
        at += size;
    }
    return NULL;
}
void _nfree(void __near* p) { if (p) ((unsigned __near*)p)[-1] &= ~1u; }
void __near* _ncalloc(size_t n, size_t size) { void __near* p = _nmalloc(n * size); if (p) memset(p, 0, n * size); return p; }
void __near* _nrealloc(void __near* p, size_t n) {
    void __near* q;
    unsigned have;
    if (p == NULL) return _nmalloc(n);
    have = (((unsigned __near*)p)[-1] & ~1u) - 2u;
    if (have >= n) return p;
    q = _nmalloc(n);
    if (q) { memcpy(q, p, have); _nfree(p); }
    return q;
}

/* ---- int 21h: the services of DOS that the C library uses ------------------------------------------------------------------------- */
/* (the frame fire_bios16_start.asm hands over: the caller's registers; bit 0 of `flags` is the carry of the result) */
typedef struct FireBiosRegs { unsigned flags, ax, bx, cx, dx, si, di, bp, ds, es; } FireBiosRegs;
static unsigned char fire_bios_noDbcs[4];   /* (an empty table of DBCS lead bytes) */
static unsigned fire_bios_bcd(unsigned char b) { return (b >> 4) * 10u + (b & 15u); }
static void fire_bios_fail(FireBiosRegs __far* r, unsigned code) { r->ax = code; r->flags |= 1; }

void __far fire_bios_int21(FireBiosRegs __far* r) {
    unsigned char ah = (unsigned char)(r->ax >> 8), al = (unsigned char)r->ax;
    union REGS q;
    r->flags &= ~1u;
    switch (ah) {
    case 0x30: r->ax = 0x0005; r->bx = 0; r->cx = 0; return;                       /* the version: 5.0 */
    case 0x44:                                                                     /* IOCTL: handles 0-2 are the console */
        if (al == 0x00 && r->bx <= 2) { r->dx = r->bx == 0 ? 0x80D1 : 0x80D2; return; }
        fire_bios_fail(r, 6); return;
    case 0x40: {                                                                   /* write to the console */
        const char __far* p = (const char __far*)MK_FP(r->ds, r->dx);
        unsigned i;
        if (r->bx != 1 && r->bx != 2) { fire_bios_fail(r, 6); return; }
        for (i = 0; i < r->cx; i++) fire_bios_putc(p[i]);
        r->ax = r->cx;
        return;
    }
    case 0x3F: {                                                                   /* read a line from the console */
        char __far* p = (char __far*)MK_FP(r->ds, r->dx);
        unsigned n = 0;
        if (r->bx != 0) { fire_bios_fail(r, 6); return; }
        while (n < r->cx) {
            int c = fire_bios_getc();
            if (c == '\r') c = '\n';
            if (c == 8) { if (n > 0) { n--; fire_bios_putc(8); fire_bios_putc(' '); fire_bios_putc(8); } continue; }
            p[n++] = (char)c;
            fire_bios_putc((char)c);
            if (c == '\n') break;
        }
        r->ax = n;
        return;
    }
    case 0x01: { int c = fire_bios_getc(); fire_bios_putc((char)c); r->ax = (r->ax & 0xFF00) | (unsigned char)c; return; }
    case 0x07: case 0x08: r->ax = (r->ax & 0xFF00) | (unsigned char)fire_bios_getc(); return;
    case 0x0B: r->ax = (r->ax & 0xFF00) | (_bios_keybrd(_KEYBRD_READY) ? 0xFF : 0); return;
    case 0x02: fire_bios_putc((char)r->dx); return;
    case 0x09: { const char __far* p = (const char __far*)MK_FP(r->ds, r->dx); while (*p != '$') fire_bios_putc(*p++); return; }
    case 0x2C: {                                                                   /* the time: the BIOS tick count (18.2 per second) since midnight */
        unsigned long ticks = *(unsigned long __far*)MK_FP(0x40, 0x6C);
        unsigned long hundredths = (unsigned long)(((unsigned long long)ticks * 6553600ull) / 1193180ull);   /* (1193180 / 65536 ticks a second) */
        r->cx = (unsigned)((hundredths / 360000ul) << 8) | (unsigned)(hundredths / 6000ul % 60ul);
        r->dx = (unsigned)((hundredths / 100ul % 60ul) << 8) | (unsigned)(hundredths % 100ul);
        return;
    }
    case 0x2A:                                                                     /* the date: the clock of the CMOS */
        q.h.ah = 0x04;
        int86(0x1A, &q, &q);
        r->cx = fire_bios_bcd(q.h.ch) * 100u + fire_bios_bcd(q.h.cl);
        r->dx = (fire_bios_bcd(q.h.dh) << 8) | fire_bios_bcd(q.h.dl);
        r->ax &= 0xFF00;
        return;
    case 0x25: { unsigned __far* v = (unsigned __far*)MK_FP(0, al * 4u); _disable(); v[0] = r->dx; v[1] = r->ds; _enable(); return; }
    case 0x35: { unsigned __far* v = (unsigned __far*)MK_FP(0, al * 4u); r->bx = v[0]; r->es = v[1]; return; }
    case 0x4C: fire_bios_exit(al); return;
    case 0x19: r->ax &= 0xFF00; return;                                            /* the current drive: A */
    case 0x63:                                                                     /* the table of DBCS lead bytes: empty */
        if (al == 0) { r->ds = FP_SEG(fire_bios_noDbcs); r->si = FP_OFF(fire_bios_noDbcs); r->ax &= 0xFF00; return; }
        fire_bios_fail(r, 1); return;
    case 0x66: if (al == 1) { r->bx = 437; r->dx = 437; return; } fire_bios_fail(r, 1); return;   /* the code page: 437 */
    case 0x38: {                                                                   /* the country: the USA, as a DOS without COUNTRY= */
        unsigned char __far* p = (unsigned char __far*)MK_FP(r->ds, r->dx);
        unsigned i;
        if (al != 0) { fire_bios_fail(r, 2); return; }
        for (i = 0; i < 34; i++) p[i] = 0;
        p[2] = '$'; p[4] = ','; p[6] = '.'; p[8] = '/'; p[10] = ':'; p[22] = ',';
        r->bx = 1;
        return;
    }
    default: fire_bios_fail(r, 1); return;                                         /* "invalid function" (files, memory of DOS, ...) */
    }
}

/* ---- the start ------------------------------------------------------------------------------------------------------------------ */
extern int main(int argc, char** argv);
void __far fire_bios_cmain(void) {
    fire_bios_serialInit();
    exit(main(__argc, __argv));
}

#endif
