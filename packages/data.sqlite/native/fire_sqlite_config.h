/* fire native bridge "data.sqlite": the build options of SQLite (before the amalgamation, sqlite3.c - SQLite 3.54.1, public domain, unchanged).
 * One file for the whole engine; no extensions loaded from libraries (no libdl), foreign keys on by default, no double-quoted string literals. */
#ifndef FIRE_SQLITE_CONFIG_H
#define FIRE_SQLITE_CONFIG_H

#define SQLITE_THREADSAFE 1                 /* (serialized: the engine locks itself - fire threads may use connections of their own at once) */
#define SQLITE_DEFAULT_FOREIGN_KEYS 1
#define SQLITE_DQS 0
#define SQLITE_OMIT_LOAD_EXTENSION 1
#define SQLITE_OMIT_DEPRECATED 1
#define SQLITE_DEFAULT_MEMSTATUS 0
#define SQLITE_LIKE_DOESNT_MATCH_BLOBS 1
#define SQLITE_ENABLE_FTS5 1
#define SQLITE_ENABLE_JSON1 1
#define SQLITE_ENABLE_RTREE 1
#define SQLITE_USE_URI 1
#define HAVE_MREMAP 0                       /* (the system headers come before the amalgamation, without its _GNU_SOURCE: no mremap) */

/* SQLite's own names that the runtime has as well: renamed for the amalgamation only (fire_bridge_sqlite.h takes them back) */
#define Bool fire_sqlite_Bool

#endif
