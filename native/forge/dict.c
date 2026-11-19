/* native/forge/dict.c - .NET's Dictionary<K,V> and HashSet<T> as the C# code of the compiler uses them (L3: the order of the output hangs on the order
 * of enumeration): entries in the order they were added; a removed entry is a hole that the next Add fills (the last one removed first) - exactly what
 * System.Collections.Generic does, so that an enumeration after Remove/Add gives the same order. Keys: texts (fc8, compared with strcmp), pointers
 * (ReferenceEqualityComparer), or a key with an equality of its own (a record's value equality). Memory from the arena of the context. */

typedef enum FcKeyKind { FC_KEY_STR, FC_KEY_PTR, FC_KEY_CUSTOM } FcKeyKind;
typedef struct FcDictEntry { const void* key; void* value; uint32_t hash; int next; } FcDictEntry;   /* next: < -1 free (-3 - next on the free list) */
typedef bool (*FcKeyEq)(const void* a, const void* b);
typedef uint32_t (*FcKeyHash)(const void* a);
typedef struct FcDict {
    FcCtx* ctx;
    FcKeyKind kind;
    FcKeyEq eq;
    FcKeyHash hash;
    FcDictEntry* entries;
    int used, cap;          /* entries[0..used) hold live and free ones */
    int freeList, freeCount;
    int32_t* index;         /* open addressing: entry index, -1 empty, -2 deleted */
    int indexCap, indexFill;
} FcDict;

#define FC_DICT_EACH(d, e) for (FcDictEntry* e = (d)->entries; e && e < (d)->entries + (d)->used; e++) if (e->next >= -1)

static void fcDictInit(FcCtx* c, FcDict* d, FcKeyKind kind) { memset(d, 0, sizeof *d); d->ctx = c; d->kind = kind; d->freeList = -1; }
static FcDict* fcDictNew(FcCtx* c, FcKeyKind kind) { FcDict* d = (FcDict*)fcAlloc(c, sizeof *d); fcDictInit(c, d, kind); return d; }
static FcDict* fcDictNewCustom(FcCtx* c, FcKeyEq eq, FcKeyHash hash) { FcDict* d = fcDictNew(c, FC_KEY_CUSTOM); d->eq = eq; d->hash = hash; return d; }
static int fcDictCount(const FcDict* d) { return d->used - d->freeCount; }

static uint32_t fcDictHashOf(const FcDict* d, const void* key) {
    uint32_t h;
    if (d->kind == FC_KEY_STR) h = fbHash((const char*)key, strlen((const char*)key));
    else if (d->kind == FC_KEY_PTR) { uintptr_t p = (uintptr_t)key; h = (uint32_t)(p ^ (p >> 17) ^ (p >> 33)) * 2654435761u; }
    else h = d->hash(key);
    return h ? h : 1;
}
static bool fcDictSame(const FcDict* d, const void* a, const void* b) {
    if (d->kind == FC_KEY_STR) return strcmp((const char*)a, (const char*)b) == 0;
    if (d->kind == FC_KEY_PTR) return a == b;
    return d->eq(a, b);
}
static int fcDictFind(const FcDict* d, const void* key, uint32_t h) {
    uint32_t mask, i;
    if (!d->indexCap) return -1;
    mask = (uint32_t)d->indexCap - 1;
    for (i = h & mask;; i = (i + 1) & mask) {
        int32_t at = d->index[i];
        if (at == -1) return -1;
        if (at >= 0 && d->entries[at].hash == h && fcDictSame(d, d->entries[at].key, key)) return (int)i;
    }
}
static void fcDictReindex(FcDict* d, int cap) {
    int i;
    uint32_t mask = (uint32_t)cap - 1;
    d->index = (int32_t*)fcAlloc(d->ctx, sizeof(int32_t) * (size_t)cap);
    memset(d->index, 0xff, sizeof(int32_t) * (size_t)cap);
    d->indexCap = cap;
    d->indexFill = 0;
    for (i = 0; i < d->used; i++) {
        uint32_t k;
        if (d->entries[i].next < -1) continue;
        for (k = d->entries[i].hash & mask; d->index[k] != -1; k = (k + 1) & mask) {}
        d->index[k] = i;
        d->indexFill++;
    }
}
static void** fcDictRef(const FcDict* d, const void* key) {
    int at = fcDictFind(d, key, fcDictHashOf(d, key));
    return at < 0 ? NULL : &d->entries[d->index[at]].value;
}
static bool fcDictHas(const FcDict* d, const void* key) { return key && fcDictFind(d, key, fcDictHashOf(d, key)) >= 0; }
static void* fcDictGet(const FcDict* d, const void* key) { void** v = key ? fcDictRef(d, key) : NULL; return v ? *v : NULL; }
/* TryGetValue */
static bool fcDictTry(const FcDict* d, const void* key, void** value) { void** v = key ? fcDictRef(d, key) : NULL; if (v && value) *value = *v; return v != NULL; }

/* d[key] = value: a key that is there keeps its place; true when it was new */
static bool fcDictPut(FcDict* d, const void* key, void* value) {
    uint32_t h = fcDictHashOf(d, key), mask, k;
    int at = fcDictFind(d, key, h), slot;
    if (at >= 0) { d->entries[d->index[at]].value = value; return false; }
    if (d->freeCount > 0) {
        slot = d->freeList;
        d->freeList = -3 - d->entries[slot].next;
        d->freeCount--;
    } else {
        if (d->used == d->cap) {
            int ncap = d->cap ? d->cap * 2 : 4;
            FcDictEntry* n = (FcDictEntry*)fcAlloc(d->ctx, sizeof(FcDictEntry) * (size_t)ncap);
            if (d->used) memcpy(n, d->entries, sizeof(FcDictEntry) * (size_t)d->used);
            d->entries = n;
            d->cap = ncap;
        }
        slot = d->used++;
    }
    d->entries[slot].key = key;
    d->entries[slot].value = value;
    d->entries[slot].hash = h;
    d->entries[slot].next = -1;
    if ((d->indexFill + 1) * 2 > d->indexCap) fcDictReindex(d, d->indexCap ? d->indexCap * 2 : 8);
    else {
        mask = (uint32_t)d->indexCap - 1;
        for (k = h & mask; d->index[k] >= 0; k = (k + 1) & mask) {}
        if (d->index[k] == -1) d->indexFill++;
        d->index[k] = slot;
        return true;
    }
    return true;
}
/* HashSet.Add / Dictionary.TryAdd: false when the key is there */
static bool fcDictAdd(FcDict* d, const void* key, void* value) {
    if (fcDictHas(d, key)) return false;
    fcDictPut(d, key, value);
    return true;
}
static bool fcSetAdd(FcDict* d, const void* key) { return fcDictAdd(d, key, (void*)1); }
static bool fcDictRemove(FcDict* d, const void* key) {
    int at = fcDictFind(d, key, fcDictHashOf(d, key)), slot;
    if (at < 0) return false;
    slot = d->index[at];
    d->index[at] = -2;
    d->entries[slot].key = NULL;
    d->entries[slot].value = NULL;
    d->entries[slot].next = -3 - d->freeList;
    d->freeList = slot;
    d->freeCount++;
    return true;
}
static void fcDictClear(FcDict* d) {
    d->used = 0; d->freeList = -1; d->freeCount = 0;
    if (d->indexCap) memset(d->index, 0xff, sizeof(int32_t) * (size_t)d->indexCap);
    d->indexFill = 0;
}
static FcDict* fcDictCopy(FcCtx* c, const FcDict* src) {
    FcDict* d = fcDictNew(c, src->kind);
    d->eq = src->eq; d->hash = src->hash;
    FC_DICT_EACH(src, e) fcDictPut(d, e->key, e->value);
    return d;
}
#define FC_IVAL(v) ((void*)(intptr_t)(v))
#define FC_VALI(v) ((int)(intptr_t)(v))
