# Roadmap: databases and ORM (`Data`)

Status: concept. The expression trees it builds on have their own page: [ROADMAP_EXPRESSIONS.md](ROADMAP_EXPRESSIONS.md) (prepared in
parallel).

## Goals

- One way to reach a database from fire, in the VM and natively, on the desktop, on a server and in tests.
- Drivers: **SQLite**, an **in-memory database**, **MySQL/MariaDB**, **SQL Server** - in this order; PostgreSQL later with the same interface.
- Raw SQL (always possible, always with bound parameters) and an ORM on top: queries as **expression trees**
  (`db.From<Person>().Where(p => p.age > 18)`), units of work per **session**, and per object where that is simpler.
- Every operation can be **awaited**.
- A **designer** in spark: a model (entities, fields, keys, relations, indexes) from which the database, the classes and the mapping are made.

## Packages and namespace

| Package (`#import`) | Contents |
|---|---|
| `data` | the namespace `Data`: interfaces of the drivers, connection, pool, mapping, query builder, session, migrations, the SQL dialects |
| `data.sqlite` | SQLite (the amalgamation as the native part of the package: one C file, no server - VM and native, also small targets) |
| `data.memory` | the in-memory database, written in fire |
| `data.mysql` | MySQL/MariaDB: the wire protocol in fire (`Net.TcpClient`, TLS) - no client library |
| `data.sqlserver` | SQL Server: TDS in fire (`Net.TcpClient`, TLS) - no client library |

A program imports `data` and the drivers it uses; `Data.Open(url)` finds the driver by the scheme of the URL (`sqlite:app.db`, `memory:`,
`mysql://user@host/db`, `sqlserver://host/db`). The password does not belong in the URL: `Data.Open(url, new Data.Credentials(user, password))`
or the environment.

## 1. Drivers

```fire
interface Data.IDriver {
    string Scheme { get }
    Data.IConnection Open(string url, Data.Credentials credentials)
    Data.Dialect Dialect { get }
}
```

- **SQLite:** prepared statements, bound parameters, `sqlite3_step` row by row. Calls into SQLite that wait (I/O, `busy_timeout`) mark the worker as
  blocked (`workerBlockEnter`/`BlockEnter`), so other tasks go on. `:memory:` works too, but it is SQLite and not `data.memory`.
- **In-memory (`data.memory`):** tables as lists of rows in a home actor (several connections see the same data), keys, unique indexes,
  transactions (a copy of the touched tables, thrown away by a rollback). It runs the ORM's expression trees itself: no SQL text in between.
  Raw SQL there is a `NotSupportedException` at first; a small SQL subset can come later. Use: tests (fast, the same code as with a real database),
  caches, small programs. `Snapshot(path)`/`Load(path)` write and read it as JSON.
- **MySQL/MariaDB:** handshake v10, `caching_sha2_password` and `mysql_native_password` (with TLS; RSA key exchange without TLS later), the binary
  protocol for prepared statements, streaming result sets.
- **SQL Server:** TDS 7.4: PRELOGIN, TLS **inside** TDS (the TLS handshake runs in TDS packets - `tls` needs a stream that is not a socket: to be
  checked first, it is the biggest risk), LOGIN7 (SQL login; Windows/Kerberos later), RPC `sp_executesql` with typed parameters, token stream
  (COLMETADATA, ROW, NBCROW, DONE), MARS later.

### The dialect

Everything that differs between the databases is in a `Data.Dialect` of the driver - the query builder and the migrations only ask it:

| | SQLite | MySQL | SQL Server | memory |
|---|---|---|---|---|
| quoting | `"name"` | `` `name` `` | `[name]` | - |
| parameters | `?` | `?` | `@p0` | values |
| paging | `LIMIT n OFFSET m` | `LIMIT m, n` | `OFFSET m ROWS FETCH NEXT n ROWS ONLY` (needs ORDER BY) | - |
| generated key | `INTEGER PRIMARY KEY` + `RETURNING` | `AUTO_INCREMENT` + `LAST_INSERT_ID()` | `IDENTITY` + `OUTPUT INSERTED` | counter |
| types | `INTEGER`, `REAL`, `TEXT`, `BLOB` | `BIGINT`, `DOUBLE`, `VARCHAR(n)`/`TEXT`, `DATETIME(6)` ... | `BIGINT`, `FLOAT`, `NVARCHAR(n)`, `DATETIME2` ... | fire values |
| text functions | `lower`, `instr`, `||` | `LOWER`, `LOCATE`, `CONCAT` | `LOWER`, `CHARINDEX`, `+` | fire |

## 2. Connection: raw SQL

```fire
#import "data"
#import "data.sqlite"
var db = Data.Open("sqlite:app.db")
db.Execute("create table person(id integer primary key, name text, age integer)")
db.Execute("insert into person(name, age) values (?, ?)", ["Ada", 36])
foreach (row in db.Query("select name, age from person where age > ?", [18])) { print(row["name"] + " " + row.Int("age")) }
Person p = db.QueryOne<Person>("select * from person where id = ?", [1])     // a row as an object (by the names of the columns)
List<Person> all = db.QueryList<Person>("select * from person")
db.Transaction(() => {                                                      // commit at the end, rollback on an exception
    db.Execute("update person set age = age + 1")
})
```

- Values only as parameters; there is no call that puts a value into the SQL text. The parameter marker is always `?` in fire - the driver turns
  it into its own (`@p0`).
- `Query` streams (an `IEnumerable` of `Data.Row`: by name or index, `Int/Float/Text/Bool/Time/Bytes`, `IsNull`); the reader is closed by `foreach`
  ending or by `Close()`. `QueryList`, `QueryOne`, `Scalar` read everything at once.
- Prepared statements are cached per connection (by their text).
- A connection belongs to the thread that opened it, like any object: it goes to another one only with `take`/`handover`. Two tasks never use
  the same connection at once.
- Errors: `Data.DataException` with the code and the message of the database; `Data.ConstraintException` (unique, foreign key, not null),
  `Data.DeadlockException`, `Data.ConnectionException`, `TimeoutException` for a command timeout.

### Awaitable

Every call can be awaited, and the language gives most of it: `await` runs a call as a task of its own - the caller waits, its objects are lent:

```fire
List<Person> adults = await db.QueryList<Person>("select * from person where age > ?", [18])
var [people, orders] = await [a.QueryList<Person>("select * from person"), b.QueryList<Order>("select * from orders")]   // two connections at once
```

- The drivers never block a worker while they wait: the network drivers wait in the poller (like Http), SQLite and the in-memory database mark the
  worker as blocked. So `await` and `fire` scale on all cores without a thread per query.
- Two awaited calls on **the same** connection at once are refused (`InvalidOperationException`): one connection, one statement at a time.
- The pool (below) is an actor: `await pool.Take()`.

## 3. Pool

```fire
var pool = new Data.Pool("mysql://app@db.local/shop", credentials, 16)   // at most 16 connections
var c = await pool.Take(5s)           // a connection, taken over (TimeoutException after 5 s)
...
pool.Give(take c)                     // back - a broken one is closed instead
pool.Use(c => { ... })                // takes, runs, gives back (also when it throws)
```

- `actor Data.Pool` owns the idle connections; `Take` hands one over with `take`. A connection that comes back with an open transaction is rolled back.
- For servers: the router `Data.PerRequest(pool)` (`http`) puts a connection into `ctx.Item("data")` for the request and gives it back at its end;
  `Http.Rpc` gives it to a parameter of type `Data.IConnection` (DI like the session).

## 4. Mapping

Classes and records become tables by convention, attributes change it:

```fire
[Table "people"]
class Person {
    [Key auto] int id
    [Column "full_name", Length 100] string name
    int age
    [Unique] string email
    [Ignore] string shown
    float height in "m"
    [HasMany "personId"] List<Order> orders
}
record Order {
    [Key auto] int id
    int personId
    [BelongsTo "personId"] Person owner
    float total
    DateTime at
}
```

- Without attributes: the table is the name of the class, the key the field `id` (an `int` key is generated), every field is a column of the same name.
- `[Table]` and `[Entity]` imply `[Keep]` (like `[Remote]`, `[Page]`): stripping does not remove what only reflection reaches.
- Types: `int`, `float`, `string` (`[Length n]`, else the dialect's long text), `bool`, `DateTime`, `byte[]`, enums (by name; `[AsNumber]`), units (stored in the
  base unit, the unit is in the schema), `undefined` is NULL (`[Required]` makes it NOT NULL), a record without `[Table]` as a JSON column.
- The mapping is read once per class (`Data.Model.Of<Person>()`), cached, and can be built in code instead of attributes (`model.Entity<Person>().Key(p => p.id)`) -
  also what the designer generates.
- Records are the natural rows for reading (immutable, `==` by fields, lent over channels); classes are entities that change.

## 5. Queries: expression trees

```fire
var adults = await db.From<Person>()
    .Where(p => p.age >= 18 && p.name.StartsWith(prefix))     // an expression tree: becomes SQL with `prefix` as a parameter
    .OrderBy(p => p.name)
    .Skip(20).Take(10)
    .ToList()
var names = db.From<Person>().Select(p => new PersonName(p.id, p.name)).ToList()    // projection to a record
var big = db.From<Order>().Where(o => o.total > 100).Include(o => o.owner).ToList()
int n = db.From<Person>().Count(p => p.age < 18)
var byAge = db.From<Person>().GroupBy(p => p.age).Select(g => new AgeCount(g.Key, g.Count())).ToList()
var withOrders = db.From<Person>().Where(p => p.orders.Any(o => o.total > 100)).ToList()   // EXISTS (subquery)
```

- `From<T>()` gives a `Data.Query<T>`: `Where`, `OrderBy[Descending]`, `ThenBy`, `Skip`, `Take`, `Select`, `Include`, `GroupBy`, `Join`, `Distinct`, and the
  ends `ToList`, `First[OrDefault]`, `Single`, `Count`, `Any`, `Sum/Min/Max/Average`, `Delete()` (`DELETE ... WHERE`), `Update(p => new Person { ... })`
  later. Like a LINQ query it runs only at its end; every end can be awaited.
- The lambdas are **expression trees** (`lambda expression<T>`, see [ROADMAP_EXPRESSIONS.md](ROADMAP_EXPRESSIONS.md)): the compiler gives the method
  the tree instead of code. The query builder turns the tree into the dialect's SQL; `data.memory` evaluates the same tree on its rows. A captured value
  (`prefix`) is a parameter, never text. What cannot be translated (a call of an own method) is an error - at compile time where the compiler knows
  it, else `Data.NotTranslatableException` with the part of the expression.
- `ToSql()` shows the SQL and the parameters of a query (for logs and tests).
- Raw SQL and queries mix: `db.FromSql<Person>("select * from people where ...", [args]).OrderBy(p => p.name)`.

## 6. Writing: per session and per object

**Per session (unit of work)** - the main way:

```fire
var s = db.Session()
Person p = s.Find<Person>(7)          // from the identity map, else loaded
p.age = 37                            // a change: found at Save by comparing with the snapshot (like the properties of the Pages)
s.Add(new Person("Grace", 85))
s.Remove(s.Find<Person>(3))
s.Save()                              // one transaction: INSERT, UPDATE (only the changed columns), DELETE in the order of the foreign keys
await s.Save()                        // the same, awaited
```

- The session owns what it loaded and what was added (ownership: `s.Add(new ...)` or `s.Add(take x)`); one object per row (identity map).
- Its queries: `s.From<Person>()...` - what they bring back is tracked.
- `Save` writes the generated keys back into the objects, sets the foreign keys of added children (`p.orders.Add(o)`), and checks a
  `[Version]` field (optimistic concurrency: `Data.ConcurrencyException` when someone else changed the row).
- `s.Detach(p)` hands an object out of the session (`take`), `s.Attach(p)` takes one in (for an object that came over RPC or from another thread).

**Per object** (where a session is too much):

```fire
db.Insert(new Person("Ada", 36))
db.Update(p)                          // all columns by key
db.Delete(p)
db.Save(p)                            // insert or update by the key
p.Save(db)                            // the same as a method: for classes with [Entity]/[Table] (an extension that the package adds)
```

Records have only these (they do not change, so a session has nothing to track): `db.Update(copy r with { age = 37 })` once the language has `with`,
until then a new record.

## 7. Schema and migrations

- `db.Ensure<Person>()` / `db.Ensure(model)`: makes missing tables, columns and indexes (never drops) - for tests, prototypes, `data.memory`.
- Migrations: classes with `[Migration 3, "add email"]` and `Up(Data.Migrator m)` (`m.AddColumn<Person>(p => p.email)`, `m.Sql(...)`), optionally `Down`.
  `db.Migrate()` runs the missing ones in order; the done ones are in the table `_fire_migrations`.
- `forge data diff` compares the model (classes or designer file) with a database and writes the migration that is missing; `forge data apply` runs them;
  `forge data import <url>` reads a database into a model (reverse engineering).

## 8. The designer (spark)

A data model is a file of the project like a block model: `*.fdata` (JSON, readable in a diff), *File > New > Data Model*. spark opens it in the
**data designer**; forge turns it into code at build time.

**In the model:**

- entities: name, table, the fields (name, type, length, precision, nullable, default, unit, key, generated, unique), comments;
- relations: one-to-many, one-to-one, many-to-many (with its join table), the navigation fields on both sides, on delete (cascade, set null, restrict);
- indexes, check constraints, views (as SQL), enums;
- naming rules (tables plural/singular, `snake_case`/`PascalCase` for columns), per database the type mapping where the default does not fit;
- the target databases (one model, several dialects).

**In spark** (like the circuit editor: a canvas in the middle, toolbox, properties):

- the diagram: entities as boxes with their fields, relations as lines with their cardinality; drag to place, groups/colours, several diagrams of one
  model for big ones;
- the properties panel for the selected entity, field or relation; a grid editor of the fields;
- **from the database:** connect (a connection of the project's settings, the password from the system's key store), import tables, views and
  relations into the model, later compare and update;
- **to the database:** *Generate Database* shows the DDL for the chosen dialect and runs it, or *Generate Migration* writes the difference to the last
  state as a migration class;
- **preview:** the generated fire code and the DDL beside the diagram, updated while editing;
- checks while editing: a relation without a key, a name that is a keyword of a dialect, a type a dialect does not have.

**What forge generates** from `Shop.fdata` (a file in the build output, like the code of an FXML file - not edited by hand):

- the classes or records of the entities with their attributes, the navigation fields, an enum per enum;
- `Shop.Model` (the mapping in code: what `Data.Model.Of<T>()` would read, without reflection at run time - also for strong stripping);
- `Shop.Context`: `new Shop.Context(db)` with `ctx.People` (= `From<Person>()`), `ctx.Session()`, `ctx.Ensure()`, `ctx.Migrate()`;
- the DDL per target dialect as a resource, and the migrations that `Generate Migration` wrote (those are source and checked in).

Own methods for the generated classes come in an extension (`extension Person { string Display() { ... } }`), so that the generated file stays generated.

## Order

| Step | What | Depends on |
|---|---|---|
| 1 | `data` and `data.sqlite`: connection, parameters, `Query`/`Execute`, rows to records and classes, transactions, errors; awaitable - **built** ([docs/data](data/README.md)) | - |
| 2 | Mapping (attributes, convention, `Data.Model`), `Ensure`, `Insert/Update/Delete/Save` per object - **built** (`Data.EntityMap`; `p.Save(db)` as a method and the model in code still open) | 1 |
| 3 | `data.memory` with the raw API (rows, keys, transactions) - **built**, with a subset of SQL (decided by the user: the same code as with a real database) | 2 |
| 4 | Query builder on expression trees: SQL for SQLite, evaluation in `data.memory`; `ToSql` - **built** (`Data.Query<T>`, `Data.SqlWriter`; data.memory runs the same SQL; `Select`, `Include`, `GroupBy`, `Join` still open) | 2, expressions step 1-3 |
| 5 | Session: identity map, snapshots, Save in one transaction, generated keys, `[Version]` - **built** (`Data.Session`, `Data.ConcurrencyException`) | 2, 4 |
| 6 | Relations: navigation fields, `Include`, `Any`/subqueries, join tables - **built** ([HasMany], [BelongsTo], [ManyToMany], Include/Load in sessions, Any/All as EXISTS, writing through lists, also without a session) | 4, 5 |
| 7 | Pool, `Data.PerRequest`, Rpc DI - **built** (`Data.Pool` with an actor home; `Http.Database` in the package `http.data`; Http.Rpc takes constructor parameters from the items of the request) | 1 |
| 8 | `data.mysql` (wire protocol, TLS, prepared statements) - **built** (in fire on `net`/`tls`; native and caching_sha2 logins, the binary protocol; RSA without TLS and streaming of big results still open) | 1, 7 |
| 9 | `data.sqlserver` (TDS, TLS in TDS, `sp_executesql`) | 1, 7 |
| 10 | Migrations, `forge data diff/apply/import` | 2, a driver with schema reading |
| 11 | Designer: the `.fdata` format and forge's generator (classes, Model, Context, DDL) | 2, 10 |
| 12 | Designer in spark: diagram, properties, import from a database, generate database/migration, preview | 11 |
| 13 | PostgreSQL | 1, 7 |

Tests: every driver runs the same suite (`Data.Tests.Run(url)`: types, NULL, parameters, transactions, errors, queries, session) - SQLite and memory in the
normal tests (VM == native), MySQL and SQL Server against a server when one is configured (`FIRE_TEST_MYSQL`, `FIRE_TEST_SQLSERVER`).

## Open

- ~~The type argument of a generic method at run time~~ - `Type.Of<T>()` is there: `QueryList<T>`, `QueryOne<T>`, `Find<T>`, `All<T>`, `Ensure<T>` are built.
- `data.memory`: one database per connection; a database shared by several connections (and threads) by name needs a home actor - later.
- The connection is a class (`new Data.Connection(url)`), not a function `Data.Open(url)`: a namespace has no functions of its own.

- TLS inside TDS (SQL Server): can `tls` run over a stream that is not a socket?
- `with` for records (`copy r with { age = 37 }`) - a language question, helpful for writing records.
- Change notifications (SQLite update hook, SQL Server query notifications) for live UI lists - later, with the bindings of the UI library.
