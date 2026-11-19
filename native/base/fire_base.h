/* native/base: what the tools in C (the VM, later forge) need besides the runtime (docs/ROADMAP_C_PORT.md, "Aufbau im Repository", L3):
 *
 *   FbArena      memory of one translation or one loaded program - many small allocations, freed at once (no single free)
 *   fbIntern     names: every text once in an arena, so that a name compares by its pointer
 *   FbMap        an ordered map text -> pointer: lookups by hash, iteration in the order of insertion (the C# code's Dictionary)
 *   fbReadFile   a whole file into memory
 *
 * C11, no global state (everything hangs on a context the caller owns, L6), errors as results - an allocation that fails calls the handler of the
 * caller (`fbOutOfMemory`, which the program that includes this file defines). Header-only: `#define FIRE_BASE_IMPLEMENTATION` in one file. */
#ifndef FIRE_BASE_H
#define FIRE_BASE_H

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* ---- arena ---- */
typedef struct FbArenaBlock { struct FbArenaBlock* next; size_t used, cap; } FbArenaBlock;
typedef struct FbArena { FbArenaBlock* head; size_t total; } FbArena;

void* fbAlloc(FbArena* a, size_t n);                 /* zeroed, 16-byte aligned */
char* fbStrDup(FbArena* a, const char* s, size_t n); /* a copy with a 0 behind it */
void fbArenaFree(FbArena* a);

/* ---- interned names ---- */
typedef struct FbInterns { FbArena* arena; const char** tab; uint32_t cap, count; } FbInterns;
uint32_t fbHash(const char* s, size_t n);
const char* fbIntern(FbInterns* t, const char* s, size_t n);   /* the one copy of the text (made when it is new) */
const char* fbInternFind(const FbInterns* t, const char* s, size_t n);   /* NULL when it is not there */

/* ---- ordered map: interned (or any stable) key text -> value; the order of insertion is kept ---- */
typedef struct FbMapEntry { const char* key; void* value; uint32_t hash; } FbMapEntry;
typedef struct FbMap { FbMapEntry* entries; uint32_t count, capEntries; int32_t* index; uint32_t capIndex; } FbMap;
void* fbMapGet(const FbMap* m, const char* key);
bool fbMapHas(const FbMap* m, const char* key);
void fbMapPut(FbMap* m, const char* key, void* value);           /* a key that is there keeps its place */
void fbMapFree(FbMap* m);
#define FB_MAP_EACH(m, e) for (FbMapEntry* e = (m)->entries; e && e < (m)->entries + (m)->count; e++)

/* ---- files ---- */
uint8_t* fbReadFile(const char* path, size_t* len);   /* malloc'd, NULL when it cannot be read */

/* the program that includes this file says what happens when memory runs out */
void fbOutOfMemory(void);

#ifdef FIRE_BASE_IMPLEMENTATION

void* fbAlloc(FbArena* a, size_t n) {
    FbArenaBlock* b = a->head;
    char* base;
    void* p;
    n = (n + 15u) & ~(size_t)15u;
    if (!b || b->used + n > b->cap) {
        size_t cap = n > 65536u ? n : 65536u;
        b = (FbArenaBlock*)malloc(sizeof(FbArenaBlock) + 16 + cap);
        if (!b) { fbOutOfMemory(); return NULL; }
        b->next = a->head; b->used = 0; b->cap = cap;
        a->head = b;
        a->total += cap;
    }
    base = (char*)(b + 1);
    base += (16 - ((uintptr_t)base & 15u)) & 15u;
    p = base + b->used;
    b->used += n;
    memset(p, 0, n);
    return p;
}
char* fbStrDup(FbArena* a, const char* s, size_t n) {
    char* d = (char*)fbAlloc(a, n + 1);
    if (n) memcpy(d, s, n);
    d[n] = 0;
    return d;
}
void fbArenaFree(FbArena* a) {
    FbArenaBlock* b = a->head;
    while (b) { FbArenaBlock* n = b->next; free(b); b = n; }
    a->head = NULL;
    a->total = 0;
}

uint32_t fbHash(const char* s, size_t n) {
    uint32_t h = 2166136261u;
    size_t i;
    for (i = 0; i < n; i++) { h ^= (uint8_t)s[i]; h *= 16777619u; }
    return h;
}
static bool fbSame(const char* e, const char* s, size_t n) { return strncmp(e, s, n) == 0 && e[n] == 0; }
const char* fbInternFind(const FbInterns* t, const char* s, size_t n) {
    uint32_t i;
    if (!t->cap) return NULL;
    for (i = fbHash(s, n) & (t->cap - 1); t->tab[i]; i = (i + 1) & (t->cap - 1))
        if (fbSame(t->tab[i], s, n)) return t->tab[i];
    return NULL;
}
const char* fbIntern(FbInterns* t, const char* s, size_t n) {
    uint32_t i;
    if (t->count * 2 + 2 > t->cap) {
        uint32_t cap = t->cap ? t->cap * 2 : 1024, k;
        const char** tab = (const char**)fbAlloc(t->arena, cap * sizeof(char*));
        for (k = 0; k < t->cap; k++) {
            const char* e = t->tab[k];
            if (!e) continue;
            for (i = fbHash(e, strlen(e)) & (cap - 1); tab[i]; i = (i + 1) & (cap - 1)) { }
            tab[i] = e;
        }
        t->tab = tab;
        t->cap = cap;
    }
    for (i = fbHash(s, n) & (t->cap - 1); t->tab[i]; i = (i + 1) & (t->cap - 1))
        if (fbSame(t->tab[i], s, n)) return t->tab[i];
    t->tab[i] = fbStrDup(t->arena, s, n);
    t->count++;
    return t->tab[i];
}

static int32_t fbMapSlot(const FbMap* m, const char* key, uint32_t h) {
    uint32_t i;
    if (!m->capIndex) return -1;
    for (i = h & (m->capIndex - 1); m->index[i] >= 0; i = (i + 1) & (m->capIndex - 1)) {
        const FbMapEntry* e = &m->entries[m->index[i]];
        if (e->hash == h && (e->key == key || strcmp(e->key, key) == 0)) return m->index[i];
    }
    return -1;
}
void* fbMapGet(const FbMap* m, const char* key) {
    int32_t at = key ? fbMapSlot(m, key, fbHash(key, strlen(key))) : -1;
    return at < 0 ? NULL : m->entries[at].value;
}
bool fbMapHas(const FbMap* m, const char* key) { return key && fbMapSlot(m, key, fbHash(key, strlen(key))) >= 0; }
void fbMapPut(FbMap* m, const char* key, void* value) {
    uint32_t h = fbHash(key, strlen(key)), i;
    int32_t at = fbMapSlot(m, key, h);
    if (at >= 0) { m->entries[at].value = value; return; }
    if (m->count == m->capEntries) {
        m->capEntries = m->capEntries ? m->capEntries * 2 : 16;
        m->entries = (FbMapEntry*)realloc(m->entries, m->capEntries * sizeof(FbMapEntry));
        if (!m->entries) { fbOutOfMemory(); return; }
    }
    if ((m->count + 1) * 2 > m->capIndex) {
        uint32_t cap = m->capIndex ? m->capIndex * 2 : 32, k;
        free(m->index);
        m->index = (int32_t*)malloc(cap * sizeof(int32_t));
        if (!m->index) { fbOutOfMemory(); return; }
        for (k = 0; k < cap; k++) m->index[k] = -1;
        m->capIndex = cap;
        for (k = 0; k < m->count; k++) {
            for (i = m->entries[k].hash & (cap - 1); m->index[i] >= 0; i = (i + 1) & (cap - 1)) { }
            m->index[i] = (int32_t)k;
        }
    }
    m->entries[m->count].key = key; m->entries[m->count].value = value; m->entries[m->count].hash = h;
    for (i = h & (m->capIndex - 1); m->index[i] >= 0; i = (i + 1) & (m->capIndex - 1)) { }
    m->index[i] = (int32_t)m->count++;
}
void fbMapFree(FbMap* m) { free(m->entries); free(m->index); memset(m, 0, sizeof *m); }

uint8_t* fbReadFile(const char* path, size_t* len) {
    FILE* f = fopen(path, "rb");
    uint8_t* b;
    long n;
    if (!f) return NULL;
    if (fseek(f, 0, SEEK_END) != 0 || (n = ftell(f)) < 0 || fseek(f, 0, SEEK_SET) != 0) { fclose(f); return NULL; }
    b = (uint8_t*)malloc(n > 0 ? (size_t)n : 1);
    if (!b || fread(b, 1, (size_t)n, f) != (size_t)n) { fclose(f); free(b); return NULL; }
    fclose(f);
    *len = (size_t)n;
    return b;
}

#endif
#endif
