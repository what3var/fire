/* fire native platform package "bios16", target bios16-dos (ROADMAP 13.5): the mini-DOS - more of int 21h on top of the minimal C library (fire_bios16_libc.h),
 * so that what calls DOS runs without one (the packages io/filesystem, dos):
 *  - files, read only, from the boot floppy (FAT12, as Fat12Image writes it; drive A:): open, read, seek, close, the time of a file, attributes, find first/next,
 *    the current directory, the free space; writing, creating, deleting and renaming fail with "access denied" (5);
 *  - the memory of DOS (48h, 49h, 4Ah): blocks of the heap of the platform, the segment of their first paragraph;
 *  - ctrl-break (33h), the drive (0Eh, 19h), extended open (6Ch) for reading.
 * The geometry and the drive come from the BIOS parameter block of the boot sector, which stays at 0000:7C00. Included by fire_bios16_libc.h (FIRE_BIOS_MINIDOS). */
#ifndef FIRE_BIOS16_DOS_H
#define FIRE_BIOS16_DOS_H

#define FIRE_DOS_FILES 16                 /* open files at once (handles 5 to 20) */
#define FIRE_DOS_EOC 0xFF8u               /* (a cluster from here on ends a chain) */

typedef struct FireDosGeo { unsigned spt, heads, fatSec, fatCount, rootSec, rootEntries, dataSec, clusters; unsigned char drive; } FireDosGeo;
static FireDosGeo fire_dos_geo;
static unsigned char __far* fire_dos_fat;   /* the first FAT, read once */
static unsigned char __far* fire_dos_buf;   /* one sector */
static unsigned fire_dos_bufLba = 0xFFFFu;
static unsigned fire_dos_cwd;                /* the cluster of the current directory (0: the root) */
static char fire_dos_cwdText[68];           /* its path without drive and leading backslash ("GAMES\LEVELS") */
static unsigned char __far* fire_dos_dta;

typedef struct FireDosFile { unsigned char used; unsigned first; unsigned long size, pos; unsigned cluster, clusterIndex; unsigned time, date; unsigned char attr; } FireDosFile;
static FireDosFile fire_dos_files[FIRE_DOS_FILES];

/* A directory entry where it was found: its 32 bytes, and the place for find next. */
typedef struct FireDosHit { unsigned char e[32]; unsigned dir, index; } FireDosHit;

static int fire_dos_init(void) {
    const unsigned char __far* b = (const unsigned char __far*)MK_FP(0, 0x7C00);
    if (fire_dos_buf) return 1;
    fire_dos_geo.spt = *(const unsigned __far*)(b + 24);
    fire_dos_geo.heads = *(const unsigned __far*)(b + 26);
    fire_dos_geo.fatSec = *(const unsigned __far*)(b + 22);
    fire_dos_geo.fatCount = b[16];
    fire_dos_geo.rootEntries = *(const unsigned __far*)(b + 17);
    fire_dos_geo.rootSec = *(const unsigned __far*)(b + 14) + fire_dos_geo.fatCount * fire_dos_geo.fatSec;
    fire_dos_geo.dataSec = fire_dos_geo.rootSec + fire_dos_geo.rootEntries / 16u;
    fire_dos_geo.clusters = *(const unsigned __far*)(b + 19) - fire_dos_geo.dataSec;
    fire_dos_geo.drive = b[36];
    if (*(const unsigned __far*)(b + 11) != 512 || b[13] != 1 || fire_dos_geo.spt == 0 || fire_dos_geo.heads == 0) return 0;   /* (only what Fat12Image writes) */
    {
        /* the sector buffer: the floppy's DMA cannot cross a boundary of 64 KB - of 1 KB, the half that does not cross one */
        unsigned char __far* two = (unsigned char __far*)fire_bios_alloc(1024);
        unsigned long at;
        fire_dos_fat = (unsigned char __far*)fire_bios_alloc((unsigned long)fire_dos_geo.fatSec * 512ul);
        if (!two || !fire_dos_fat) return 0;
        at = ((unsigned long)FP_SEG(two) << 4) + FP_OFF(two);
        fire_dos_buf = (at & 0xFFFFul) > 0xFE00ul ? (unsigned char __far*)MK_FP((unsigned)((at + 512ul) >> 4), (unsigned)((at + 512ul) & 15ul)) : two;
    }
    return 1;
}

/* Reads the sector `lba` to `to` (512 bytes; a buffer that does not cross a boundary of 64 KB - fire_dos_buf); 0 on a disk error. */
static int fire_dos_read(unsigned lba, unsigned char __far* to) {
    union REGS r;
    struct SREGS s;
    unsigned track = lba / fire_dos_geo.spt, cyl = track / fire_dos_geo.heads;
    int tries;
    for (tries = 0; tries < 3; tries++) {
        r.x.ax = 0x0201;
        r.h.ch = (unsigned char)cyl;
        r.h.cl = (unsigned char)((lba % fire_dos_geo.spt + 1) | ((cyl >> 2) & 0xC0));
        r.h.dh = (unsigned char)(track % fire_dos_geo.heads);
        r.h.dl = fire_dos_geo.drive;
        r.x.bx = FP_OFF(to);
        s.es = FP_SEG(to);
        int86x(0x13, &r, &r, &s);
        if (!r.x.cflag) return 1;
        r.h.ah = 0;
        r.h.dl = fire_dos_geo.drive;
        int86(0x13, &r, &r);
    }
    return 0;
}
static const unsigned char __far* fire_dos_sector(unsigned lba) {
    if (fire_dos_bufLba != lba) { if (!fire_dos_read(lba, fire_dos_buf)) { fire_dos_bufLba = 0xFFFFu; return NULL; } fire_dos_bufLba = lba; }
    return fire_dos_buf;
}
static int fire_dos_loadFat(void) {
    static int loaded;
    unsigned i;
    if (loaded) return 1;
    for (i = 0; i < fire_dos_geo.fatSec; i++) {
        if (!fire_dos_read(1 + i, fire_dos_buf)) return 0;
        _fmemcpy(fire_dos_fat + i * 512u, fire_dos_buf, 512);
    }
    fire_dos_bufLba = 0xFFFFu;
    return loaded = 1;
}
static unsigned fire_dos_next(unsigned cluster) {
    unsigned at = cluster + cluster / 2u, v = fire_dos_fat[at] | ((unsigned)fire_dos_fat[at + 1] << 8);
    return (cluster & 1) ? v >> 4 : v & 0xFFFu;
}

/* The `index`-th entry of the directory `dir` (0: the root) into hit->e; 0 past its end. */
static int fire_dos_entry(unsigned dir, unsigned index, FireDosHit* hit) {
    const unsigned char __far* s;
    unsigned lba;
    if (dir == 0) {
        if (index >= fire_dos_geo.rootEntries) return 0;
        lba = fire_dos_geo.rootSec + index / 16u;
    } else {
        unsigned c = dir, n = index / 16u;
        while (n-- > 0) { c = fire_dos_next(c); if (c < 2 || c >= FIRE_DOS_EOC) return 0; }
        lba = fire_dos_geo.dataSec + c - 2;
    }
    if ((s = fire_dos_sector(lba)) == NULL) return 0;
    _fmemcpy(hit->e, s + (index % 16u) * 32u, 32);
    hit->dir = dir;
    hit->index = index;
    return hit->e[0] != 0;
}

/* "name.ext" as the 11 characters of an entry (with ? from *); 0 when it is no 8.3 name. */
static int fire_dos_name11(const char __far* p, unsigned len, char out[11]) {
    unsigned i = 0, k = 0;
    _fmemset(out, ' ', 11);
    if (len == 1 && p[0] == '.') { out[0] = '.'; return 1; }
    if (len == 2 && p[0] == '.' && p[1] == '.') { out[0] = out[1] = '.'; return 1; }
    for (; i < len && p[i] != '.'; i++) {
        char c = p[i];
        if (c == '*') { while (k < 8) out[k++] = '?'; continue; }
        if (k >= 8) return 0;
        out[k++] = (char)(c >= 'a' && c <= 'z' ? c - 32 : c);
    }
    if (i < len) {
        for (i++, k = 8; i < len; i++) {
            char c = p[i];
            if (c == '*') { while (k < 11) out[k++] = '?'; continue; }
            if (k >= 11 || c == '.') return 0;
            out[k++] = (char)(c >= 'a' && c <= 'z' ? c - 32 : c);
        }
    }
    return k > 0 || out[0] != ' ';
}
static int fire_dos_match(const unsigned char* e, const char pattern[11]) {
    unsigned i;
    for (i = 0; i < 11; i++) if (pattern[i] != '?' && pattern[i] != (char)e[i]) return 0;
    return 1;
}
/* Looks the name up in the directory `dir`; the entry in `hit`. */
static int fire_dos_lookup(unsigned dir, const char name[11], FireDosHit* hit) {
    unsigned i;
    if (dir == 0 && name[0] == '.') { _fmemset(hit->e, 0, 32); hit->e[11] = 0x10; return 1; }   /* (the root has no . and ..: itself) */
    for (i = 0; fire_dos_entry(dir, i, hit); i++)
        if (hit->e[0] != 0xE5 && !(hit->e[11] & 0x08) && fire_dos_match(hit->e, name)) return 1;
    return 0;
}

/* Walks a path of DOS ("A:\GAMES\LEVEL.DAT", "levels/one.dat", "..\x"): the directory that holds the last part, the last part as 11 characters (empty:
 * the path names a directory itself, its cluster in *dirOut). 0: the path does not exist (DOS error 3), or it names another drive (15). */
static unsigned fire_dos_walk(const char __far* path, unsigned* dirOut, char last[11]) {
    unsigned dir = fire_dos_cwd;
    const char __far* p = path;
    FireDosHit hit;
    if (p[0] && p[1] == ':') { if ((p[0] | 32) != 'a') return 15; p += 2; }
    if (*p == '\\' || *p == '/') { dir = 0; while (*p == '\\' || *p == '/') p++; }
    _fmemset(last, ' ', 11);
    for (;;) {
        const char __far* end = p;
        char part[11];
        unsigned len;
        while (*end && *end != '\\' && *end != '/') end++;
        len = (unsigned)(end - p);
        if (len == 0) { *dirOut = dir; return 0; }
        if (!fire_dos_name11(p, len, part)) return 3;
        while (*end == '\\' || *end == '/') end++;
        if (*end == 0) { _fmemcpy(last, part, 11); *dirOut = dir; return 0; }
        if (!fire_dos_lookup(dir, part, &hit) || !(hit.e[11] & 0x10)) return 3;
        dir = *(unsigned*)(hit.e + 26);
        p = end;
    }
}
/* The entry a path names (a file or a directory; the root: an entry with the directory flag and cluster 0). */
static unsigned fire_dos_find(const char __far* path, FireDosHit* hit) {
    unsigned dir, err;
    char last[11];
    if ((err = fire_dos_walk(path, &dir, last)) != 0) return err;
    if (last[0] == ' ') { _fmemset(hit->e, 0, 32); hit->e[11] = 0x10; *(unsigned*)(hit->e + 26) = dir; return 0; }
    return fire_dos_lookup(dir, last, hit) ? 0 : 2;
}

/* ---- the services ---- */
static void fire_dos_ok(FireBiosRegs __far* r) { r->flags &= ~1u; }
static FireDosFile* fire_dos_file(unsigned handle) {
    return handle >= 5 && handle < 5 + FIRE_DOS_FILES && fire_dos_files[handle - 5].used ? &fire_dos_files[handle - 5] : NULL;
}
static unsigned fire_dos_open(const char __far* path, unsigned mode, unsigned* handle) {
    FireDosHit hit;
    unsigned err, i;
    if ((mode & 7) != 0) return 5;                                                /* (writing: the disk is read only) */
    if ((err = fire_dos_find(path, &hit)) != 0) return err;
    if (hit.e[11] & 0x18) return 5;                                               /* (a directory or the label) */
    for (i = 0; i < FIRE_DOS_FILES && fire_dos_files[i].used; i++) {}
    if (i == FIRE_DOS_FILES) return 4;                                            /* (too many open files) */
    fire_dos_files[i].used = 1;
    fire_dos_files[i].first = *(unsigned*)(hit.e + 26);
    fire_dos_files[i].size = *(unsigned long*)(hit.e + 28);
    fire_dos_files[i].pos = 0;
    fire_dos_files[i].cluster = fire_dos_files[i].first;
    fire_dos_files[i].clusterIndex = 0;
    fire_dos_files[i].time = *(unsigned*)(hit.e + 22);
    fire_dos_files[i].date = *(unsigned*)(hit.e + 24);
    fire_dos_files[i].attr = hit.e[11];
    *handle = i + 5;
    return 0;
}
static unsigned fire_dos_readFile(FireDosFile* f, unsigned char __far* to, unsigned count, unsigned* got) {
    unsigned done = 0;
    while (done < count && f->pos < f->size) {
        unsigned want = (unsigned)(f->pos / 512ul), off = (unsigned)(f->pos % 512ul), n = 512u - off;
        const unsigned char __far* s;
        if (want < f->clusterIndex) { f->cluster = f->first; f->clusterIndex = 0; }
        while (f->clusterIndex < want) { f->cluster = fire_dos_next(f->cluster); f->clusterIndex++; if (f->cluster < 2 || f->cluster >= FIRE_DOS_EOC) { *got = done; return 30; } }
        if (n > count - done) n = count - done;
        if ((unsigned long)n > f->size - f->pos) n = (unsigned)(f->size - f->pos);
        if ((s = fire_dos_sector(fire_dos_geo.dataSec + f->cluster - 2)) == NULL) { *got = done; return 30; }
        _fmemcpy(to + done, s + off, n);
        done += n;
        f->pos += n;
    }
    *got = done;
    return 0;
}
/* the DTA of find first/next: 0-1 the directory, 2-3 the next index, 4-14 the pattern, 15 the attributes asked for; 21 attributes, 22 time, 24 date, 26 size,
 * 30 the name ("NAME.EXT", 0-terminated) */
static unsigned fire_dos_findNext(unsigned char __far* dta) {
    FireDosHit hit;
    unsigned dir = *(unsigned __far*)dta, i = *(unsigned __far*)(dta + 2);
    char pattern[11];
    unsigned char want = dta[15];
    _fmemcpy(pattern, dta + 4, 11);
    for (; fire_dos_entry(dir, i, &hit); i++) {
        unsigned k, n = 0;
        unsigned char a = hit.e[11];
        if (hit.e[0] == 0xE5 || (a & 0x08) || ((a & 0x16) & ~want) || !fire_dos_match(hit.e, pattern)) continue;
        *(unsigned __far*)(dta + 2) = i + 1;
        dta[21] = a;
        *(unsigned __far*)(dta + 22) = *(unsigned*)(hit.e + 22);
        *(unsigned __far*)(dta + 24) = *(unsigned*)(hit.e + 24);
        *(unsigned long __far*)(dta + 26) = *(unsigned long*)(hit.e + 28);
        for (k = 0; k < 8 && hit.e[k] != ' '; k++) dta[30 + n++] = hit.e[k];
        if (hit.e[8] != ' ') { dta[30 + n++] = '.'; for (k = 8; k < 11 && hit.e[k] != ' '; k++) dta[30 + n++] = hit.e[k]; }
        dta[30 + n] = 0;
        return 0;
    }
    *(unsigned __far*)(dta + 2) = i;
    return 18;                                                                    /* (no more files) */
}
static unsigned long fire_dos_largest(void) {
    unsigned long best = 0;
    unsigned cur;
    if (!fire_bios_heapReady) fire_bios_heapInit();
    for (cur = fire_bios_freeList; cur != 0; cur = FIRE_BIOS_BLK(cur)->next) if (FIRE_BIOS_BLK(cur)->paras > best) best = FIRE_BIOS_BLK(cur)->paras;
    return best > 1ul ? best - 1ul : 0ul;
}
/* 4Ah: a block of DOS memory smaller (the rest goes back) or larger (when the block behind it is free and big enough). */
static int fire_dos_resize(unsigned seg, unsigned paras, unsigned* largest) {
    unsigned blk = seg - 1, behind;
    unsigned long have, need = (unsigned long)paras + 1ul;
    if (FIRE_BIOS_BLK(blk)->magic != FIRE_BIOS_USED) return 9;
    have = FIRE_BIOS_BLK(blk)->paras;
    if (need + 1ul < have) {
        unsigned rest = blk + (unsigned)need;
        FIRE_BIOS_BLK(rest)->paras = have - need;
        FIRE_BIOS_BLK(rest)->magic = FIRE_BIOS_USED;
        FIRE_BIOS_BLK(blk)->paras = need;
        fire_bios_free(MK_FP(rest + 1, 0));
        return 0;
    }
    if (need <= have) return 0;
    behind = blk + (unsigned)have;
    {
        unsigned prev = 0, cur;
        for (cur = fire_bios_freeList; cur != 0 && cur != behind; prev = cur, cur = FIRE_BIOS_BLK(cur)->next) {}
        if (cur == behind && have + FIRE_BIOS_BLK(cur)->paras >= need) {
            unsigned long total = have + FIRE_BIOS_BLK(cur)->paras;
            unsigned next = FIRE_BIOS_BLK(cur)->next;
            if (total > need + 1ul) {
                unsigned rest = blk + (unsigned)need;
                FIRE_BIOS_BLK(rest)->paras = total - need;
                FIRE_BIOS_BLK(rest)->next = next;
                FIRE_BIOS_BLK(rest)->magic = FIRE_BIOS_FREE;
                next = rest;
                FIRE_BIOS_BLK(blk)->paras = need;
            } else FIRE_BIOS_BLK(blk)->paras = total;
            if (prev) FIRE_BIOS_BLK(prev)->next = next; else fire_bios_freeList = next;
            return 0;
        }
        *largest = (unsigned)(have - 1ul + (cur == behind ? FIRE_BIOS_BLK(cur)->paras : 0ul));
    }
    return 8;
}

/* The services of the mini-DOS: 1 when it answered (the carry and AX as DOS sets them), 0 for the minimal library's own. */
static int fire_bios_dos(FireBiosRegs __far* r, unsigned char ah, unsigned char al) {
    unsigned err, handle;
    FireDosFile* f;
    switch (ah) {
    case 0x48: {                                                                  /* the memory of DOS */
        void __far* p = r->bx ? fire_bios_alloc((unsigned long)r->bx * 16ul) : NULL;
        if (p == NULL) { unsigned long l = fire_dos_largest(); r->bx = l > 0xFFFFul ? 0xFFFFu : (unsigned)l; fire_bios_fail(r, 8); return 1; }
        r->ax = FP_SEG(p) + (FP_OFF(p) >> 4);
        return 1;
    }
    case 0x49:
        if (FIRE_BIOS_BLK(r->es - 1)->magic != FIRE_BIOS_USED) { fire_bios_fail(r, 9); return 1; }
        fire_bios_free(MK_FP(r->es, 0));
        return 1;
    case 0x4A: { unsigned largest = 0; if ((err = fire_dos_resize(r->es, r->bx, &largest)) != 0) { r->bx = largest; fire_bios_fail(r, err); } return 1; }
    case 0x33: if (al == 0) r->dx &= 0xFF00; return 1;                            /* ctrl-break: off */
    case 0x0E: r->ax = (r->ax & 0xFF00) | 1; return 1;                            /* one drive: A: */
    case 0x1A: fire_dos_dta = (unsigned char __far*)MK_FP(r->ds, r->dx); return 1;
    case 0x2F: if (!fire_dos_dta) fire_dos_dta = (unsigned char __far*)MK_FP(_psp, 0x80); r->es = FP_SEG(fire_dos_dta); r->bx = FP_OFF(fire_dos_dta); return 1;
    case 0x3C: case 0x41: case 0x56: case 0x39: case 0x3A: case 0x5A: case 0x5B: fire_bios_fail(r, 5); return 1;   /* (writing) */
    }
    if (!fire_dos_init() || !fire_dos_loadFat()) {
        if (ah == 0x3D || ah == 0x6C || ah == 0x4E || ah == 0x43 || ah == 0x3B || ah == 0x36) { fire_bios_fail(r, 21); return 1; }   /* (the drive is not ready) */
        return 0;
    }
    switch (ah) {
    case 0x3D:                                                                    /* open */
        if ((err = fire_dos_open((const char __far*)MK_FP(r->ds, r->dx), al, &handle)) != 0) { fire_bios_fail(r, err); return 1; }
        r->ax = handle;
        return 1;
    case 0x6C:                                                                    /* extended open: only "open an existing file" for reading */
        if ((r->dx & 0xF0) != 0 && (r->dx & 0x0F) != 1) { fire_bios_fail(r, 5); return 1; }
        if ((err = fire_dos_open((const char __far*)MK_FP(r->ds, r->si), r->bx & 0xFF, &handle)) != 0) { fire_bios_fail(r, err); return 1; }
        r->ax = handle;
        r->cx = 1;
        return 1;
    case 0x3E: if ((f = fire_dos_file(r->bx)) == NULL) { fire_bios_fail(r, 6); return 1; } f->used = 0; return 1;
    case 0x3F:
        if ((f = fire_dos_file(r->bx)) == NULL) return 0;                         /* (the console: the minimal library) */
        if ((err = fire_dos_readFile(f, (unsigned char __far*)MK_FP(r->ds, r->dx), r->cx, &handle)) != 0) { fire_bios_fail(r, err); return 1; }
        r->ax = handle;
        return 1;
    case 0x40: if (fire_dos_file(r->bx) != NULL) { fire_bios_fail(r, 5); return 1; } return 0;
    case 0x42: {
        long base, to;
        if ((f = fire_dos_file(r->bx)) == NULL) { fire_bios_fail(r, 6); return 1; }
        base = al == 0 ? 0L : al == 1 ? (long)f->pos : al == 2 ? (long)f->size : -1L;
        if (al > 2) { fire_bios_fail(r, 1); return 1; }
        to = base + (long)(((unsigned long)r->cx << 16) | r->dx);
        if (to < 0) { fire_bios_fail(r, 0x19); return 1; }
        f->pos = (unsigned long)to;
        r->ax = (unsigned)(f->pos & 0xFFFFu);
        r->dx = (unsigned)(f->pos >> 16);
        return 1;
    }
    case 0x44:
        if (al == 0x00 && fire_dos_file(r->bx) != NULL) { r->dx = 0x0000; return 1; }   /* a file on drive A: */
        return 0;
    case 0x57:
        if ((f = fire_dos_file(r->bx)) == NULL) { fire_bios_fail(r, 6); return 1; }
        if (al != 0) { fire_bios_fail(r, 5); return 1; }
        r->cx = f->time;
        r->dx = f->date;
        return 1;
    case 0x43: {                                                                  /* the attributes of a file */
        FireDosHit hit;
        if (al != 0) { fire_bios_fail(r, 5); return 1; }
        if ((err = fire_dos_find((const char __far*)MK_FP(r->ds, r->dx), &hit)) != 0) { fire_bios_fail(r, err); return 1; }
        r->cx = hit.e[11];
        return 1;
    }
    case 0x4E: {                                                                  /* find first */
        unsigned dir;
        char last[11];
        unsigned char __far* dta;
        if (!fire_dos_dta) fire_dos_dta = (unsigned char __far*)MK_FP(_psp, 0x80);
        dta = fire_dos_dta;
        if ((err = fire_dos_walk((const char __far*)MK_FP(r->ds, r->dx), &dir, last)) != 0) { fire_bios_fail(r, err); return 1; }
        if (last[0] == ' ') { fire_bios_fail(r, 3); return 1; }
        *(unsigned __far*)dta = dir;
        *(unsigned __far*)(dta + 2) = 0;
        _fmemcpy(dta + 4, last, 11);
        dta[15] = (unsigned char)r->cx;
        if ((err = fire_dos_findNext(dta)) != 0) { fire_bios_fail(r, err == 18 ? 2 : err); return 1; }
        return 1;
    }
    case 0x4F: {
        unsigned char __far* dta = fire_dos_dta ? fire_dos_dta : (unsigned char __far*)MK_FP(_psp, 0x80);
        if ((err = fire_dos_findNext(dta)) != 0) { fire_bios_fail(r, err); return 1; }
        return 1;
    }
    case 0x3B: {                                                                  /* change the directory */
        FireDosHit hit;
        const char __far* path = (const char __far*)MK_FP(r->ds, r->dx);
        const char __far* p = path;
        char text[68];
        unsigned n = 0;
        if ((err = fire_dos_find(path, &hit)) != 0) { fire_bios_fail(r, err == 2 ? 3 : err); return 1; }
        if (!(hit.e[11] & 0x10)) { fire_bios_fail(r, 3); return 1; }
        fire_dos_cwd = *(unsigned*)(hit.e + 26);
        /* the text: from the root, or the current one with the parts of the path added (".." takes one off) */
        if (p[0] && p[1] == ':') p += 2;
        if (*p == '\\' || *p == '/') text[0] = 0; else _fstrcpy(text, fire_dos_cwdText);
        n = (unsigned)strlen(text);
        while (*p) {
            const char __far* end;
            unsigned len;
            while (*p == '\\' || *p == '/') p++;
            for (end = p; *end && *end != '\\' && *end != '/'; end++) {}
            len = (unsigned)(end - p);
            if (len == 2 && p[0] == '.' && p[1] == '.') { while (n > 0 && text[n - 1] != '\\') n--; if (n > 0) n--; text[n] = 0; }
            else if (len > 0 && !(len == 1 && p[0] == '.') && n + len + 2 < sizeof text) {
                unsigned k;
                if (n > 0) text[n++] = '\\';
                for (k = 0; k < len; k++) text[n++] = (char)(p[k] >= 'a' && p[k] <= 'z' ? p[k] - 32 : p[k]);
                text[n] = 0;
            }
            p = end;
        }
        strcpy(fire_dos_cwdText, text);
        if (fire_dos_cwd == 0) fire_dos_cwdText[0] = 0;
        return 1;
    }
    case 0x47:                                                                    /* the current directory */
        if ((r->dx & 0xFF) > 1) { fire_bios_fail(r, 15); return 1; }
        _fstrcpy((char __far*)MK_FP(r->ds, r->si), fire_dos_cwdText);
        r->ax = 0x0100;
        return 1;
    case 0x36: {                                                                  /* the free space: none (read only) */
        if ((r->dx & 0xFF) > 1) { r->ax = 0xFFFF; return 1; }
        r->ax = 1;
        r->bx = 0;
        r->cx = 512;
        r->dx = fire_dos_geo.clusters;
        return 1;
    }
    }
    return 0;
}

#endif
