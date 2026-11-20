/* native/forge/build/bootimage.c - the boot image of the targets without an operating system (Phase D 2, commit de45e7e4): src/fire.Native/Toolchains/Fat12Image.cs
 * (a FAT12 floppy of 1.44 MB: the boot sector, two FATs, the root directory, FIRE.EXE first and in one piece, then the files and folders of `disk`),
 * Toolchains/BiosBootSectors.cs (the boot sector of bios16, assembled from native/platform/bios16/fire_bios16_boot.asm) and WatcomToolchain.AfterLink
 * (after the link of a bios16 program: <name>.img next to it). `forge-c bootimage <exe>` is the measure (FrontendDump.DumpBootImage of the C# forge):
 * AfterLink on a file of one's own - Open Watcom is not needed for it. */

static const uint8_t nbBios16BootSector[512] = {
    0xEB, 0x3C, 0x90, 0x46, 0x49, 0x52, 0x45, 0x20, 0x20, 0x20, 0x20, 0x00, 0x02, 0x01, 0x01, 0x00, 0x02, 0xE0, 0x00, 0x40, 0x0B, 0xF0, 0x09,
    0x00, 0x12, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x29, 0x45, 0x52, 0x49, 0x46, 0x4E, 0x4F, 0x20,
    0x4E, 0x41, 0x4D, 0x45, 0x20, 0x20, 0x20, 0x20, 0x46, 0x41, 0x54, 0x31, 0x32, 0x20, 0x20, 0x20, 0xFA, 0x31, 0xC0, 0x8E, 0xD8, 0x8E, 0xD0,
    0xBC, 0x00, 0x7C, 0xFB, 0xFC, 0x88, 0x16, 0x24, 0x7C, 0xB8, 0x50, 0x00, 0x8E, 0xC0, 0xB8, 0x13, 0x00, 0xE8, 0xF1, 0x00, 0x31, 0xFF, 0xBE,
    0x89, 0x7D, 0xB9, 0x0B, 0x00, 0x57, 0xF3, 0xA6, 0x5F, 0x74, 0x0E, 0x83, 0xC7, 0x20, 0x81, 0xFF, 0x00, 0x02, 0x72, 0xEB, 0xB0, 0x46, 0xE9,
    0x0B, 0x01, 0x26, 0x8B, 0x45, 0x1A, 0x83, 0xC0, 0x1F, 0xA3, 0x96, 0x7D, 0xB8, 0x00, 0x08, 0x8E, 0xC0, 0xA1, 0x96, 0x7D, 0xE8, 0xC0, 0x00,
    0x26, 0x8B, 0x0E, 0x04, 0x00, 0x89, 0x0E, 0x94, 0x7D, 0xB8, 0x00, 0x08, 0xA3, 0x98, 0x7D, 0xFF, 0x0E, 0x94, 0x7D, 0x74, 0x16, 0x83, 0x06,
    0x98, 0x7D, 0x20, 0xFF, 0x06, 0x96, 0x7D, 0xA1, 0x98, 0x7D, 0x8E, 0xC0, 0xA1, 0x96, 0x7D, 0xE8, 0x97, 0x00, 0xEB, 0xE4, 0xB8, 0x00, 0x08,
    0x8E, 0xC0, 0x26, 0x8B, 0x1E, 0x08, 0x00, 0x81, 0xC3, 0x00, 0x08, 0x89, 0x1E, 0x9A, 0x7D, 0x26, 0xA1, 0x04, 0x00, 0xB1, 0x05, 0xD3, 0xE0,
    0x05, 0x00, 0x08, 0x26, 0x8B, 0x16, 0x0A, 0x00, 0x09, 0xD2, 0x74, 0x13, 0x06, 0x8E, 0xC0, 0x31, 0xFF, 0x50, 0x31, 0xC0, 0xB9, 0x08, 0x00,
    0xF3, 0xAB, 0x58, 0x07, 0x40, 0x4A, 0xEB, 0xE9, 0xA3, 0x9C, 0x7D, 0x26, 0x8B, 0x0E, 0x06, 0x00, 0x26, 0x8B, 0x36, 0x18, 0x00, 0xE3, 0x1B,
    0x26, 0x8B, 0x3C, 0x26, 0x8B, 0x44, 0x02, 0x03, 0x06, 0x9A, 0x7D, 0x06, 0x8E, 0xC0, 0xA1, 0x9A, 0x7D, 0x26, 0x01, 0x05, 0x07, 0x83, 0xC6,
    0x04, 0x49, 0xEB, 0xE3, 0x26, 0xA1, 0x14, 0x00, 0xA3, 0x9E, 0x7D, 0x26, 0xA1, 0x16, 0x00, 0x03, 0x06, 0x9A, 0x7D, 0xA3, 0xA0, 0x7D, 0x26,
    0xA1, 0x0E, 0x00, 0x03, 0x06, 0x9A, 0x7D, 0x26, 0x8B, 0x1E, 0x10, 0x00, 0x8A, 0x16, 0x24, 0x7C, 0x8B, 0x2E, 0x9C, 0x7D, 0xFA, 0x8E, 0xD0,
    0x89, 0xDC, 0xFB, 0x2E, 0xFF, 0x2E, 0x9E, 0x7D, 0x50, 0x31, 0xD2, 0xF7, 0x36, 0x18, 0x7C, 0x42, 0x88, 0xD1, 0x31, 0xD2, 0xF7, 0x36, 0x1A,
    0x7C, 0x88, 0xC5, 0xD0, 0xCC, 0xD0, 0xCC, 0x80, 0xE4, 0xC0, 0x08, 0xE1, 0x88, 0xD6, 0x8A, 0x16, 0x24, 0x7C, 0xBE, 0x03, 0x00, 0xB8, 0x01,
    0x02, 0x31, 0xDB, 0xCD, 0x13, 0x73, 0x10, 0x31, 0xC0, 0xCD, 0x13, 0x4E, 0x75, 0xF0, 0xB0, 0x45, 0xB4, 0x0E, 0xCD, 0x10, 0xF4, 0xEB, 0xFD,
    0x58, 0xC3, 0x46, 0x49, 0x52, 0x45, 0x20, 0x20, 0x20, 0x20, 0x45, 0x58, 0x45, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x55, 0xAA,
};

/* ---- Fat12Image ---- */
#define NB_FAT_SECTOR_BYTES 512
#define NB_FAT_SECTORS 2880
#define NB_FAT_FAT_SECTORS 9
#define NB_FAT_ROOT_ENTRIES 224
#define NB_FAT_ROOT_SECTOR (1 + 2 * NB_FAT_FAT_SECTORS)
#define NB_FAT_DATA_SECTOR (NB_FAT_ROOT_SECTOR + NB_FAT_ROOT_ENTRIES * 32 / NB_FAT_SECTOR_BYTES)
#define NB_FAT_CLUSTERS (NB_FAT_SECTORS - NB_FAT_DATA_SECTOR)
/* A file or folder on the disk: a folder has children, a file data. time: the local time (DateTime of .NET without a kind). */
typedef struct NbFatEntry {
    const char* name;
    const uint8_t* data; int size; bool isFolder;
    FcList* children;           /* NbFatEntry* */
    struct tm time;
    int cluster;
} NbFatEntry;
typedef struct NbFat { uint8_t* image; int fat[NB_FAT_CLUSTERS + 2]; int next; const char* error; } NbFat;
static struct tm nbFatTime(int year, int month, int day) { struct tm t; memset(&t, 0, sizeof t); t.tm_year = year - 1900; t.tm_mon = month - 1; t.tm_mday = day; return t; }
static struct tm nbLocalTime(time_t at) { struct tm t; localtime_r(&at, &t); return t; }

/* ShortName: the name as the 11 characters of a directory entry ("FIRE    EXE"), NULL when it is no 8.3 name */
static const char* nbFatShortName(FcCtx* c, const char* name) {
    static const char allowed[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!#$%&'()-@^_`{}~";
    const char* dot = strrchr(name, '.');
    size_t stem = dot ? (size_t)(dot - name) : strlen(name), ext = dot ? strlen(dot + 1) : 0;
    const char* p;
    if (stem == 0 || stem > 8 || ext > 3 || (dot && ext == 0)) return NULL;
    for (p = name; *p; p++) if (p != dot && !strchr(allowed, *p)) return NULL;
    return fcFmt(c, "%-8.*s%-3s", (int)stem, name, dot ? dot + 1 : "");
}
static void nbFatPut16(uint8_t* p, unsigned v) { p[0] = (uint8_t)v; p[1] = (uint8_t)(v >> 8); }
static void nbFatPut32(uint8_t* p, unsigned v) { p[0] = (uint8_t)v; p[1] = (uint8_t)(v >> 8); p[2] = (uint8_t)(v >> 16); p[3] = (uint8_t)(v >> 24); }
static void nbFatWriteEntryRaw(uint8_t* dir, int at, const char* name11, uint8_t attributes, int cluster, int size, const struct tm* time) {
    int year = time->tm_year + 1900;
    unsigned t, d;
    memcpy(dir + at, name11, 11);
    dir[at + 11] = attributes;   /* (files: read-only - the mini-DOS does not write) */
    if (year < 1980) year = 1980;
    if (year > 2107) year = 2107;
    t = ((unsigned)time->tm_hour << 11) | ((unsigned)time->tm_min << 5) | ((unsigned)time->tm_sec / 2);
    d = ((unsigned)(year - 1980) << 9) | ((unsigned)(time->tm_mon + 1) << 5) | (unsigned)time->tm_mday;
    nbFatPut16(dir + at + 14, t & 0xFFFF);
    nbFatPut16(dir + at + 16, d & 0xFFFF);
    nbFatPut16(dir + at + 18, d & 0xFFFF);
    nbFatPut16(dir + at + 22, t & 0xFFFF);
    nbFatPut16(dir + at + 24, d & 0xFFFF);
    nbFatPut16(dir + at + 26, (unsigned)cluster & 0xFFFF);
    nbFatPut32(dir + at + 28, (unsigned)size);
}
static void nbFatWriteEntry(FcCtx* c, uint8_t* dir, int at, const NbFatEntry* e) {
    nbFatWriteEntryRaw(dir, at, nbFatShortName(c, e->name), e->isFolder ? 0x10 : 0x01, e->cluster, e->isFolder ? 0 : e->size, &e->time);
}
static const char* nbFatCheckNames(FcCtx* c, FcList* entries, const char* folder) {
    FcDict* seen = fcDictNew(c, FC_KEY_STR);
    int i;
    for (i = 0; i < entries->count; i++) {
        NbFatEntry* e = (NbFatEntry*)entries->items[i];
        const char* inner;
        if (!nbFatShortName(c, e->name)) return fcFmt(c, "'%s%s' is no name of DOS (8.3: up to 8 letters, a dot, up to 3 letters; no spaces).", folder, e->name);
        if (!fcSetAdd(seen, e->name)) return fcFmt(c, "'%s%s' is on the disk twice (FIRE.EXE is the program).", folder, e->name);
        if (e->isFolder && (inner = nbFatCheckNames(c, e->children, fcFmt(c, "%s%s\\", folder, e->name))) != NULL) return inner;
    }
    return NULL;
}
static int nbFatAllocate(FcCtx* c, NbFat* f, int bytes, const char* what) {
    int count = (bytes + NB_FAT_SECTOR_BYTES - 1) / NB_FAT_SECTOR_BYTES, first, i;
    if (count < 1) count = 1;
    if (f->next + count > NB_FAT_CLUSTERS + 2) { if (!f->error) f->error = fcFmt(c, "The floppy (1.44 MB) is full: %s does not fit any more.", what); return 0; }
    first = f->next;
    for (i = 0; i < count; i++) f->fat[f->next + i] = i == count - 1 ? 0xFFF : f->next + i + 1;
    f->next += count;
    return first;
}
static void nbFatWriteCluster(NbFat* f, int cluster, const uint8_t* data, int n) { memcpy(f->image + (size_t)(NB_FAT_DATA_SECTOR + cluster - 2) * NB_FAT_SECTOR_BYTES, data, (size_t)n); }
/* FIRE.EXE first, in one piece; then the rest, a folder before its contents */
static void nbFatPlace(FcCtx* c, NbFat* f, NbFatEntry* e) {
    int i;
    if (f->error) return;
    if (e->isFolder) {
        e->cluster = nbFatAllocate(c, f, (e->children->count + 2) * 32, fcFmt(c, "the folder %s", e->name));
        for (i = 0; i < e->children->count; i++) nbFatPlace(c, f, (NbFatEntry*)e->children->items[i]);
    } else if (e->size > 0) e->cluster = nbFatAllocate(c, f, e->size, e->name);
}
/* the contents: files, then the directories (their entries name the clusters of their contents) */
static void nbFatFill(FcCtx* c, NbFat* f, NbFatEntry* e, int parentCluster) {
    uint8_t* dir;
    int i, n;
    if (!e->isFolder) { if (e->size > 0) nbFatWriteCluster(f, e->cluster, e->data, e->size); return; }
    n = (e->children->count + 2) * 32;
    dir = (uint8_t*)calloc(1, (size_t)n);
    nbFatWriteEntryRaw(dir, 0, ".          ", 0x10, e->cluster, 0, &e->time);
    nbFatWriteEntryRaw(dir, 32, "..         ", 0x10, parentCluster, 0, &e->time);
    for (i = 0; i < e->children->count; i++) nbFatWriteEntry(c, dir, (i + 2) * 32, (NbFatEntry*)e->children->items[i]);
    nbFatWriteCluster(f, e->cluster, dir, n);
    free(dir);
    for (i = 0; i < e->children->count; i++) nbFatFill(c, f, (NbFatEntry*)e->children->items[i], e->cluster);
}
/* Create: the image (malloc'd, NB_FAT_SECTORS * 512 bytes) of bootSector, FIRE.EXE and files; NULL with *error the problem (a name that is not 8.3, more than fits) */
static uint8_t* nbFat12Create(FcCtx* c, const uint8_t* bootSector, const uint8_t* program, int programSize, FcList* files, const char** error) {
    NbFat* f = (NbFat*)calloc(1, sizeof *f);
    NbFatEntry* program83 = (NbFatEntry*)fcAlloc(c, sizeof *program83);
    FcList* root = fcListNew(c);
    uint8_t* fatBytes;
    uint8_t* image;
    int i;
    f->image = (uint8_t*)calloc(NB_FAT_SECTORS, NB_FAT_SECTOR_BYTES);
    memcpy(f->image, bootSector, NB_FAT_SECTOR_BYTES);
    f->fat[0] = 0xFF0; f->fat[1] = 0xFFF;
    f->next = 2;
    program83->name = "FIRE.EXE"; program83->data = program; program83->size = programSize; program83->time = nbLocalTime(time(NULL));
    fcListAdd(c, root, program83);
    for (i = 0; i < files->count; i++) fcListAdd(c, root, files->items[i]);
    if ((f->error = nbFatCheckNames(c, root, "")) == NULL && root->count > NB_FAT_ROOT_ENTRIES)
        f->error = fcFmt(c, "The root directory of the floppy holds %d entries, not %d.", NB_FAT_ROOT_ENTRIES, root->count);
    for (i = 0; i < root->count && !f->error; i++) nbFatPlace(c, f, (NbFatEntry*)root->items[i]);
    if (f->error) { *error = f->error; free(f->image); free(f); return NULL; }
    for (i = 0; i < root->count; i++) nbFatFill(c, f, (NbFatEntry*)root->items[i], 0);
    for (i = 0; i < root->count; i++) nbFatWriteEntry(c, f->image, NB_FAT_ROOT_SECTOR * NB_FAT_SECTOR_BYTES + i * 32, (NbFatEntry*)root->items[i]);
    /* the two FATs: 12 bits per cluster, two entries in three bytes */
    fatBytes = (uint8_t*)calloc(NB_FAT_FAT_SECTORS, NB_FAT_SECTOR_BYTES);
    for (i = 0; i < NB_FAT_CLUSTERS + 2; i++) {
        int at = i * 3 / 2, v = f->fat[i];
        if (at + 1 >= NB_FAT_FAT_SECTORS * NB_FAT_SECTOR_BYTES) break;
        if ((i & 1) == 0) { fatBytes[at] = (uint8_t)v; fatBytes[at + 1] = (uint8_t)((fatBytes[at + 1] & 0xF0) | ((v >> 8) & 0x0F)); }
        else { fatBytes[at] = (uint8_t)((fatBytes[at] & 0x0F) | ((v << 4) & 0xF0)); fatBytes[at + 1] = (uint8_t)(v >> 4); }
    }
    memcpy(f->image + NB_FAT_SECTOR_BYTES, fatBytes, NB_FAT_FAT_SECTORS * NB_FAT_SECTOR_BYTES);
    memcpy(f->image + (1 + NB_FAT_FAT_SECTORS) * NB_FAT_SECTOR_BYTES, fatBytes, NB_FAT_FAT_SECTORS * NB_FAT_SECTOR_BYTES);
    free(fatBytes);
    image = f->image;
    free(f);
    return image;
}
/* FromFolder: the files and folders of a folder of this machine as entries (names in upper case - ASCII; folders first, each by its name ignoring case) */
static FcList* nbFat12FromFolder(FcCtx* c, const char* folder) {
    FcList* list = fcListNew(c);
    FcList* dirs = fcListNew(c);
    FcList* files = fcListNew(c);
    DIR* d = opendir(folder);
    struct dirent* e;
    int pass, i;
    if (!d) return list;
    while ((e = readdir(d)) != NULL) {
        const char* full;
        if (streq0(e->d_name, ".") || streq0(e->d_name, "..")) continue;
        full = nbCombine(c, folder, fcStr(c, e->d_name));
        if (nbDirExists(full)) fcListAdd(c, dirs, (void*)full);
        else if (nbFileExists(full)) fcListAdd(c, files, (void*)full);
    }
    closedir(d);
    if (dirs->count > 1) qsort(dirs->items, (size_t)dirs->count, sizeof(void*), fcCmpIgnoreCase);
    if (files->count > 1) qsort(files->items, (size_t)files->count, sizeof(void*), fcCmpIgnoreCase);
    for (pass = 0; pass < 2; pass++) {
        FcList* src = pass == 0 ? dirs : files;
        for (i = 0; i < src->count; i++) {
            const char* path = (const char*)src->items[i];
            NbFatEntry* x = (NbFatEntry*)fcAlloc(c, sizeof *x);
            struct stat st;
            char* upper = (char*)fcStr(c, nbFileName(path));
            char* p;
            for (p = upper; *p; p++) if (*p >= 'a' && *p <= 'z') *p = (char)(*p - 32);
            x->name = upper;
            x->time = stat(path, &st) == 0 ? nbLocalTime(st.st_mtime) : nbFatTime(1601, 1, 1);
            if (pass == 0) { x->isFolder = true; x->children = nbFat12FromFolder(c, path); }
            else {
                size_t n = 0;
                uint8_t* data = fbReadFile(path, &n);
                uint8_t* copy = (uint8_t*)fcAlloc(c, n + 1);
                if (data) memcpy(copy, data, n);
                free(data);
                x->data = copy; x->size = (int)n;
            }
            fcListAdd(c, list, x);
        }
    }
    return list;
}
/* WatcomToolchain.AfterLink: a bios16 program gets its disk image next to it (<name>.img: the boot sector, FIRE.EXE, the files and folders of `disk` next to the
 * program). The line of the log; NULL for every other platform. */
static const char* nbAfterLink(FcCtx* c, const NbToolchain* toolchain, const FgTarget* target, const char* exe) {
    const char* image;
    const char* disk;
    FcList* files;
    uint8_t* program;
    uint8_t* img;
    size_t n = 0;
    const char* error = NULL;
    if (!nbIsWatcom(nbKind(toolchain)) || !streq0(target->platform, "bios16")) return NULL;
    image = nbChangeExtension(c, exe, ".img");
    disk = nbCombine(c, nbDirName(c, fcFullPath(c, ".", exe)), "disk");
    files = nbDirExists(disk) ? nbFat12FromFolder(c, disk) : fcListNew(c);
    program = fbReadFile(exe, &n);
    if (!program) return fcFmt(c, "boot image: not written - the program '%s' cannot be read", exe);
    img = nbFat12Create(c, nbBios16BootSector, program, (int)n, files, &error);
    free(program);
    if (!img) return fcFmt(c, "boot image: not written - %s", error);
    nbWriteBytes(c, image, img, (size_t)NB_FAT_SECTORS * NB_FAT_SECTOR_BYTES);
    free(img);
    return fcFmt(c, "boot image: %s (a FAT12 floppy of 1.44 MB: FIRE.EXE, %d bytes%s)", image, (int)n, files->count > 0 ? fcFmt(c, ", and %d entries of %s", files->count, disk) : "");
}
