/* fire native bridge "data.sqlite" (C): the natives behind `#import "data.sqlite"` (docs/data/README.md) - SQLite (the amalgamation, sqlite3.c, is built with
 * the package: no library of the system).
 *
 * A connection and a statement are integer handles in one table of this bridge (index 0 unused, handles not used again); what a program leaves open is
 * finalized and closed when it ends. Every function reports an error the same way: -1/false/undefined and the code and message of this thread
 * (`__SqlLastError`, `__SqlLastErrorMessage`, the slot of the io bridge): the extended result code of SQLite, or one of ours above 100000.
 * Texts go to SQLite and come back as UTF-16 (prepare16, bind_text16, column_text16): no conversion. A call that may wait (open, step, exec: the disk, a lock
 * of another connection) lets go of the package meanwhile (gilRelease), so other fire threads go on. */
#ifndef FIRE_BRIDGE_SQLITE_H
#define FIRE_BRIDGE_SQLITE_H

#include "fire_sqlite_config.h"
#undef Bool                                 /* (the runtime's Bool again: fire_sqlite_config.h renamed SQLite's for the amalgamation) */
#if defined(__GNUC__) || defined(__clang__)
#pragma GCC diagnostic pop
#elif defined(_MSC_VER)
#pragma warning(pop)
#endif

enum { FIRE_SQLB_HANDLE = 100001, FIRE_SQLB_ARGUMENT = 100002, FIRE_SQLB_MEMORY = 100003, FIRE_SQLB_DENIED = 100004 };

static Value fire_sqlb_fail(int code, const char* message) {
    g_ioError.code = code;
    snprintf(g_ioError.message, sizeof g_ioError.message, "%s", message);
    return Int(-1);
}
static void fire_sqlb_ok(void) { g_ioError.code = 0; g_ioError.message[0] = 0; }
/* (the error of a connection: its extended code and its message) */
static Value fire_sqlb_failDb(sqlite3* db, int rc) {
    const char* m = db != NULL ? sqlite3_errmsg(db) : sqlite3_errstr(rc);
    return fire_sqlb_fail(db != NULL ? sqlite3_extended_errcode(db) : rc, m != NULL ? m : "SQLite error.");
}

/* ---- the table of connections (kind 1) and statements (kind 2) ---- */
typedef struct FireSqlEntry { int kind; void* p; int64_t owner; } FireSqlEntry;
static FireSqlEntry* g_sqlItems = NULL;
static size_t g_sqlCount = 1, g_sqlCap = 0;

static void fire_sqlb_release(size_t i) {
    FireSqlEntry* e = &g_sqlItems[i];
    if (e->p == NULL) return;
    if (e->kind == 2) sqlite3_finalize((sqlite3_stmt*)e->p);
    else {
        size_t k;
        for (k = 1; k < g_sqlCount; k++) if (g_sqlItems[k].kind == 2 && g_sqlItems[k].owner == (int64_t)i && g_sqlItems[k].p != NULL) fire_sqlb_release(k);
        sqlite3_close_v2((sqlite3*)e->p);
    }
    e->p = NULL;
}
/* The program has ended (the library for the VM stays loaded for the next one): finalizes and closes what it left open. */
static void fire_sqlite_reset(void) {
    size_t i;
    for (i = 1; i < g_sqlCount; i++) if (g_sqlItems[i].kind == 2) fire_sqlb_release(i);
    for (i = 1; i < g_sqlCount; i++) fire_sqlb_release(i);
}
static Value fire_sqlb_add(int kind, void* p, int64_t owner) {
    if (g_sqlCount >= g_sqlCap) {
        size_t cap = g_sqlCap ? g_sqlCap * 2 : 16;
        FireSqlEntry* grown = (FireSqlEntry*)realloc(g_sqlItems, sizeof(FireSqlEntry) * cap);
        if (grown == NULL) {
            if (kind == 2) sqlite3_finalize((sqlite3_stmt*)p); else sqlite3_close_v2((sqlite3*)p);
            return fire_sqlb_fail(FIRE_SQLB_MEMORY, "Out of memory.");
        }
        g_sqlItems = grown;
        g_sqlCap = cap;
    }
    g_sqlItems[g_sqlCount].kind = kind;
    g_sqlItems[g_sqlCount].p = p;
    g_sqlItems[g_sqlCount].owner = owner;
    fire_sqlb_ok();
    return Int((int64_t)g_sqlCount++);
}
static void* fire_sqlb_find(Value h, int kind) {
    int64_t i = h.kind == K_Int ? h.i : 0;
    if (i > 0 && (size_t)i < g_sqlCount && g_sqlItems[i].p != NULL && g_sqlItems[i].kind == kind) return g_sqlItems[i].p;
    fire_sqlb_fail(FIRE_SQLB_HANDLE, kind == 1 ? "Invalid or already closed SQLite connection." : "Invalid or already finalized SQLite statement.");
    return NULL;
}
static sqlite3* fire_sqlb_db(Value h) { return (sqlite3*)fire_sqlb_find(h, 1); }
static sqlite3_stmt* fire_sqlb_stmt(Value h) { return (sqlite3_stmt*)fire_sqlb_find(h, 2); }

/* ---- the host: which files a script may open (the same question as the io package asks: io_allow of the VM's host, FIRE_IO_POLICY natively) ---- */
static int fire_sqlb_allowed(const char* full, int access, char* reason, size_t size) {
#ifdef FIRE_LIBRARY
    const fire_host* host = libraryHost();
    if (!host || !host->io_allow) return 1;
    reason[0] = 0;
    return host->io_allow(full, access, reason, (int)size) ? 1 : 0;
#elif defined(FIRE_IO_POLICY)
    return FIRE_IO_POLICY(full, access, reason, size);
#else
    (void)full; (void)access; (void)reason; (void)size;
    return 1;
#endif
}
/* A fire string as UTF-8 (malloc; NULL when out of memory). */
static char* fire_sqlb_utf8(Value v) {
    const Str* s;
    char* out;
    size_t n = 0;
    uint32_t i;
    if (v.kind != K_String) { out = (char*)malloc(1); if (out) out[0] = 0; return out; }
    s = strOf(v);
    out = (char*)malloc((size_t)s->length * 3 + 1);
    if (out == NULL) return NULL;
    for (i = 0; i < s->length; i++) {
        uint32_t c = s->data[i];
        if (c >= 0xD800 && c <= 0xDBFF && i + 1 < s->length && s->data[i + 1] >= 0xDC00 && s->data[i + 1] <= 0xDFFF) { c = 0x10000 + ((c - 0xD800) << 10) + (s->data[i + 1] - 0xDC00); i++; }
        else if (c >= 0xD800 && c <= 0xDFFF) c = 0xFFFD;
        if (c < 0x80) out[n++] = (char)c;
        else if (c < 0x800) { out[n++] = (char)(0xC0 | (c >> 6)); out[n++] = (char)(0x80 | (c & 0x3F)); }
        else if (c < 0x10000) { out[n++] = (char)(0xE0 | (c >> 12)); out[n++] = (char)(0x80 | ((c >> 6) & 0x3F)); out[n++] = (char)(0x80 | (c & 0x3F)); }
        else { out[n++] = (char)(0xF0 | (c >> 18)); out[n++] = (char)(0x80 | ((c >> 12) & 0x3F)); out[n++] = (char)(0x80 | ((c >> 6) & 0x3F)); out[n++] = (char)(0x80 | (c & 0x3F)); }
    }
    out[n] = 0;
    return out;
}
static Value fire_sqlb_str8(const char* text, OwnList* list) {
    if (text == NULL) text = "";
    return newStrFromUtf8((const uint8_t*)text, strlen(text), list);
}

/* ---- natives ---------------------------------------------------------------------------------------------------------------------------- */
static Value fire_sqlite_LastError(void) { return Int(g_ioError.code); }
static Value fire_sqlite_LastErrorMessage(OwnList* list) { return fire_sqlb_str8(g_ioError.message, list); }
static Value fire_sqlite_Version(OwnList* list) { return fire_sqlb_str8(sqlite3_libversion(), list); }
static Value fire_sqlite_OpenCount(void) {
    int64_t n = 0;
    size_t i;
    for (i = 1; i < g_sqlCount; i++) if (g_sqlItems[i].kind == 1 && g_sqlItems[i].p != NULL) n++;
    return Int(n);
}

/* Opens a database: `path` a file (asked of the host: read, and write unless read-only), ":memory:" or "" (a temporary one); `flags` 1 read-only,
 * 2 do not create; `timeout` the milliseconds a statement waits for a lock of another connection. The handle, or -1. */
static Value fire_sqlite_Open(Value path, Value flags, Value timeout) {
    char* raw = fire_sqlb_utf8(path);
    char* full = NULL;
    const char* name;
    sqlite3* db = NULL;
    int open, rc;
    int64_t f = flags.kind == K_Int ? flags.i : 0;
    if (raw == NULL) return fire_sqlb_fail(FIRE_SQLB_MEMORY, "Out of memory.");
    name = raw;
    if (raw[0] != 0 && strcmp(raw, ":memory:") != 0 && strncmp(raw, "file:", 5) != 0) {
        char reason[256];
        full = fire_fs_fullPath(raw);
        if (full == NULL) { free(raw); return fire_sqlb_fail(FIRE_SQLB_MEMORY, "Out of memory."); }
        reason[0] = 0;
        if (!fire_sqlb_allowed(full, (f & 1) ? 1 : 3, reason, sizeof reason)) {
            char text[400];
            if (reason[0]) snprintf(text, sizeof text, "%s", reason);
            else snprintf(text, sizeof text, "Access to '%s' is not allowed.", full);
            free(full);
            free(raw);
            return fire_sqlb_fail(FIRE_SQLB_DENIED, text);
        }
        name = full;
    } else if (strncmp(raw, "file:", 5) == 0) {
        free(raw);
        return fire_sqlb_fail(FIRE_SQLB_ARGUMENT, "A 'file:' URI is not supported: give the path of the file.");
    }
    open = (f & 1) ? SQLITE_OPEN_READONLY : ((f & 2) ? SQLITE_OPEN_READWRITE : (SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE));
    open |= SQLITE_OPEN_EXRESCODE;
    gilRelease();
    rc = sqlite3_open_v2(name, &db, open, NULL);
    gilAcquire();
    free(full);
    free(raw);
    if (rc != SQLITE_OK) {
        Value fail = fire_sqlb_failDb(db, rc);
        if (db != NULL) sqlite3_close_v2(db);
        return fail;
    }
    sqlite3_extended_result_codes(db, 1);
    sqlite3_busy_timeout(db, timeout.kind == K_Int ? (int)timeout.i : 5000);
    return fire_sqlb_add(1, db, 0);
}

static Value fire_sqlite_Close(Value h) {
    if (fire_sqlb_db(h) == NULL) return Bool(false);
    fire_sqlb_release((size_t)h.i);
    fire_sqlb_ok();
    return Bool(true);
}

/* Runs SQL without parameters and without rows (several statements, `;` between them). */
static Value fire_sqlite_Exec(Value h, Value sql) {
    sqlite3* db = fire_sqlb_db(h);
    char* text;
    int rc;
    if (db == NULL) return Bool(false);
    text = fire_sqlb_utf8(sql);
    if (text == NULL) { fire_sqlb_fail(FIRE_SQLB_MEMORY, "Out of memory."); return Bool(false); }
    gilRelease();
    rc = sqlite3_exec(db, text, NULL, NULL, NULL);
    gilAcquire();
    free(text);
    if (rc != SQLITE_OK) { fire_sqlb_failDb(db, rc); return Bool(false); }
    fire_sqlb_ok();
    return Bool(true);
}

/* A prepared statement of the connection (the first statement of `sql`; what follows it is an error); its handle, or -1. */
static Value fire_sqlite_Prepare(Value h, Value sql) {
    sqlite3* db = fire_sqlb_db(h);
    sqlite3_stmt* st = NULL;
    const void* tail = NULL;
    const Str* s;
    int rc;
    if (db == NULL) return Int(-1);
    if (sql.kind != K_String) return fire_sqlb_fail(FIRE_SQLB_ARGUMENT, "The SQL must be a text.");
    s = strOf(sql);
    rc = sqlite3_prepare16_v3(db, s->data, (int)(s->length * 2), SQLITE_PREPARE_PERSISTENT, &st, &tail);
    if (rc != SQLITE_OK) return fire_sqlb_failDb(db, rc);
    if (st == NULL) return fire_sqlb_fail(FIRE_SQLB_ARGUMENT, "The SQL is empty.");
    if (tail != NULL) {
        const fire_char16* t = (const fire_char16*)tail;
        const fire_char16* end = s->data + s->length;
        while (t < end && (*t == ' ' || *t == '\t' || *t == '\r' || *t == '\n' || *t == ';')) t++;
        if (t < end) {
            sqlite3_finalize(st);
            return fire_sqlb_fail(FIRE_SQLB_ARGUMENT, "Only one statement at a time can have parameters or rows: use Execute without parameters for several.");
        }
    }
    return fire_sqlb_add(2, st, h.i);
}

/* Binds the values of `args` (an array) to ?1, ?2, ... after resetting the statement: a number, a text, a bool (0/1), undefined (NULL), bytes (a BLOB). */
static Value fire_sqlite_Bind(Value h, Value args) {
    sqlite3_stmt* st = fire_sqlb_stmt(h);
    uint32_t i, n;
    int want;
    if (st == NULL) return Bool(false);
    sqlite3_reset(st);
    sqlite3_clear_bindings(st);
    if (args.kind == K_Undefined) { fire_sqlb_ok(); return Bool(true); }
    if (args.kind != K_Array) { fire_sqlb_fail(FIRE_SQLB_ARGUMENT, "The parameters must be an array ([a, b])."); return Bool(false); }
    n = arrOf(args)->length;
    want = sqlite3_bind_parameter_count(st);
    if ((int)n != want) {
        char text[160];
        snprintf(text, sizeof text, "The statement has %d parameter(s), %u value(s) were given.", want, n);
        fire_sqlb_fail(FIRE_SQLB_ARGUMENT, text);
        return Bool(false);
    }
    for (i = 0; i < n; i++) {
        Value v = arrItems(arrOf(args))[i];
        int rc, at = (int)i + 1;
        switch (v.kind) {
            case K_Int: rc = sqlite3_bind_int64(st, at, v.i); break;
            case K_Bool: rc = sqlite3_bind_int64(st, at, v.i ? 1 : 0); break;
            case K_Float: rc = sqlite3_bind_double(st, at, (double)toR(v)); break;
            case K_Undefined: rc = sqlite3_bind_null(st, at); break;
            case K_String: rc = sqlite3_bind_text16(st, at, strOf(v)->data, (int)(strOf(v)->length * 2), SQLITE_TRANSIENT); break;
            case K_Char: { fire_char16 c = (fire_char16)v.i; rc = sqlite3_bind_text16(st, at, &c, 2, SQLITE_TRANSIENT); break; }
            case K_Buffer: case K_Array: {
                FireBytes b;
                if (!bytesArg(v, "Data.Sqlite (a parameter)", &b)) return Bool(false);
                rc = sqlite3_bind_blob(st, at, b.data, (int)b.length, SQLITE_TRANSIENT);
                break;
            }
            default: {
                char text[120];
                snprintf(text, sizeof text, "Parameter %u cannot go to the database (a number, text, bool, bytes or undefined).", i + 1);
                fire_sqlb_fail(FIRE_SQLB_ARGUMENT, text);
                return Bool(false);
            }
        }
        if (rc != SQLITE_OK) { fire_sqlb_failDb(sqlite3_db_handle(st), rc); return Bool(false); }
    }
    fire_sqlb_ok();
    return Bool(true);
}

/* The next step: 1 a row is there, 0 the statement is done, -1 an error. */
static Value fire_sqlite_Step(Value h) {
    sqlite3_stmt* st = fire_sqlb_stmt(h);
    int rc;
    if (st == NULL) return Int(-1);
    gilRelease();
    rc = sqlite3_step(st);
    gilAcquire();
    if (rc == SQLITE_ROW) { fire_sqlb_ok(); return Int(1); }
    if (rc == SQLITE_DONE) { fire_sqlb_ok(); return Int(0); }
    fire_sqlb_failDb(sqlite3_db_handle(st), rc);
    sqlite3_reset(st);
    return Int(-1);
}

/* The values of the current row (an array): INTEGER an int, REAL a float, TEXT a text, NULL undefined; a BLOB is `true` there (its bytes: __SqlBlob). */
static Value fire_sqlite_Row(Value h, OwnList* list) {
    sqlite3_stmt* st = fire_sqlb_stmt(h);
    int n, i;
    Arr* a;
    if (st == NULL) return Undef();
    n = sqlite3_column_count(st);
    a = allocArr((uint32_t)n, list);
    for (i = 0; i < n; i++) {
        Value v;
        switch (sqlite3_column_type(st, i)) {
            case SQLITE_INTEGER: v = Int(sqlite3_column_int64(st, i)); break;
            case SQLITE_FLOAT: v = Float((Real)sqlite3_column_double(st, i)); break;
            case SQLITE_TEXT: {
                const fire_char16* t = (const fire_char16*)sqlite3_column_text16(st, i);
                int bytes = sqlite3_column_bytes16(st, i);
                v = newStrFrom(t, (uint32_t)(bytes / 2), list);
                retain(v);
                break;
            }
            case SQLITE_BLOB: v = Bool(true); break;
            default: v = Undef(); break;
        }
        arrItems(a)[i] = v;
    }
    fire_sqlb_ok();
    return ArrV(a);
}

/* The bytes of a BLOB column of the current row. */
static Value fire_sqlite_Blob(Value h, Value column, OwnList* list) {
    sqlite3_stmt* st = fire_sqlb_stmt(h);
    const void* data;
    int bytes;
    Buf* b;
    if (st == NULL) return Undef();
    if (column.kind != K_Int || column.i < 0 || column.i >= sqlite3_column_count(st)) { fire_sqlb_fail(FIRE_SQLB_ARGUMENT, "No such column."); return Undef(); }
    data = sqlite3_column_blob(st, (int)column.i);
    bytes = sqlite3_column_bytes(st, (int)column.i);
    b = allocBuf((uint32_t)bytes, list);
    if (bytes > 0 && data != NULL) memcpy(bufBytes(b), data, (size_t)bytes);
    fire_sqlb_ok();
    return BufV(b);
}

/* The names of the columns (an array of texts). */
static Value fire_sqlite_Names(Value h, OwnList* list) {
    sqlite3_stmt* st = fire_sqlb_stmt(h);
    int n, i;
    Arr* a;
    if (st == NULL) return Undef();
    n = sqlite3_column_count(st);
    a = allocArr((uint32_t)n, list);
    for (i = 0; i < n; i++) {
        static const fire_char16 empty[1] = {0};
        const fire_char16* t = (const fire_char16*)sqlite3_column_name16(st, i);
        uint32_t len = 0;
        if (t == NULL) t = empty;
        while (t[len] != 0) len++;
        arrItems(a)[i] = newStrFrom(t, len, list);
        retain(arrItems(a)[i]);
    }
    fire_sqlb_ok();
    return ArrV(a);
}

static Value fire_sqlite_Reset(Value h) {
    sqlite3_stmt* st = fire_sqlb_stmt(h);
    if (st == NULL) return Bool(false);
    sqlite3_reset(st);
    sqlite3_clear_bindings(st);
    fire_sqlb_ok();
    return Bool(true);
}

static Value fire_sqlite_Finalize(Value h) {
    if (fire_sqlb_stmt(h) == NULL) return Bool(false);
    fire_sqlb_release((size_t)h.i);
    fire_sqlb_ok();
    return Bool(true);
}

/* The rows the last INSERT, UPDATE or DELETE of the connection changed. */
static Value fire_sqlite_Changes(Value h) {
    sqlite3* db = fire_sqlb_db(h);
    if (db == NULL) return Int(-1);
    fire_sqlb_ok();
    return Int((int64_t)sqlite3_changes64(db));
}

/* The rowid of the last INSERT of the connection. */
static Value fire_sqlite_LastId(Value h) {
    sqlite3* db = fire_sqlb_db(h);
    if (db == NULL) return Int(-1);
    fire_sqlb_ok();
    return Int((int64_t)sqlite3_last_insert_rowid(db));
}

/* Is the connection outside a transaction (autocommit)? */
static Value fire_sqlite_AutoCommit(Value h) {
    sqlite3* db = fire_sqlb_db(h);
    if (db == NULL) return Bool(false);
    fire_sqlb_ok();
    return Bool(sqlite3_get_autocommit(db) != 0);
}

#endif
