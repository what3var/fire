# Data: databases

`#import "data"` gives the namespace `Data`: connections to databases, SQL with bound parameters, rows as objects, transactions. The drivers are
packages of their own; today there are **SQLite** (`#import "data.sqlite"`), **MySQL/MariaDB** (`#import "data.mysql"`), **SQL Server**
(`#import "data.sqlserver"`) and the **in-memory database** (`#import "data.memory"`); all import `data`. What comes next - migrations and the designer - is planned in [ROADMAP_DATA.md](../ROADMAP_DATA.md).

```fire
#import "data.sqlite"
record Person {
    int id
    string name
    int age
}
var db = new Data.Connection("sqlite:app.db")
db.Execute("create table if not exists person(id integer primary key, name text not null, age integer)")
db.Execute("insert into person(name, age) values (?, ?)", ["Ada", 36])
print(db.LastInsertId)                                              // 1
foreach (row in db.Query("select name, age from person where age > ?", [18])) { print(row["name"] + " " + row.Int("age")) }
List people = db.QueryList("Person", "select * from person order by name")
Person ada = db.QueryOne("Person", "select * from person where id = ?", [1])
print(db.Scalar("select count(*) from person"))
db.Transaction(t => { t.Execute("update person set age = age + 1") })
db.Close()
```

## Connecting

`new Data.Connection(url)` - or `new Data.Connection(url, new Data.Credentials(user, password))` for a database with a server - takes the scheme of the
URL (`sqlite:`) to find the driver; its package must be imported (else a `Data.ConnectionException` says which one). The rest of the URL is the driver's.

| URL (SQLite) | |
|---|---|
| `sqlite:app.db` | the file (made when it is not there); a relative path is relative to the current directory |
| `sqlite:app.db?mode=ro` | read-only; `mode=rw`: read and write, but do not make the file |
| `sqlite:app.db?timeout=2000` | how long a statement waits for a lock of another connection (ms; default 5000), then `Data.BusyException` |
| `sqlite::memory:` | a database in memory, its own for this connection |

The file is asked of the host like a file of `io` (the editor's and the runtime's policy for scripts). SQLite (3.54.1) is built into the package - no
library of the system is needed, the same engine on every machine; foreign keys are on.

A connection belongs to the thread that made it, like any object: another thread gets it with `handover` (`fire handover db { ... }`), never two at
once. Several threads that work on the same file at the same time each open a connection of their own; SQLite locks the file for them.

## Running SQL

| | |
|---|---|
| `Execute(sql, [args])` | a statement that changes something (INSERT, UPDATE, DELETE, CREATE ...): the number of rows it changed. Without `args` the text may hold several statements (`;` between them). |
| `Query(sql, [args])` | the rows one by one: `foreach (row in db.Query(...))` - the statement is read while the loop goes and ends with it (or with `Close()` on the reader) |
| `Rows(sql, [args])` | all rows at once (`List<Data.Row>`) |
| `Scalar(sql, [args])` | the first value of the first row (undefined: no row or NULL) |
| `QueryList(typeName, sql, [args])` | the rows as objects of a class or record (a `List`) |
| `QueryOne(typeName, sql, [args])` | the first row as such an object (undefined: no row) |
| `LastInsertId` | the key the last INSERT of the connection generated |

Values only go as **parameters**: every `?` in the SQL takes the next value of the array - a number, a text, a bool (1/0), `undefined` (NULL) or bytes
(a `byte[]` or an array of 0..255: a BLOB). There is no call that puts a value into the SQL text, so a value can never change the statement. A wrong number
of values is an `ArgumentException`.

### Rows

A `Data.Row` has its values by the name of the column (`row["name"]`, the case of the letters does not count) or by position (`row.Get(0)`), and
converts: `Int`, `Float`, `Text`, `Bool`, `Bytes`, `IsNull`; `Count`, `Name(i)`, `Names()`, `Has(name)`. A value comes as SQLite has it: an INTEGER is an
`int`, a REAL a `float`, TEXT a `string`, a BLOB a `byte[]`, NULL `undefined`.

### Rows as objects

`QueryList("Person", ...)` makes an object of the class or record `Person` per row: a column goes to the field of the same name (the case of the letters and
`_` do not count - `full_name` fills `fullName`) and is converted to the field's type (`bool` from 1/0, `string` from a number, ...); a column without a
field is left out, a field without a column (or a NULL) stays undefined. The objects are made without their constructors (like `Json.Deserialize`), so
records work as well; they belong to the caller.

The type can be the type argument as well: `List<Person> people = db.QueryList<Person>(sql, args)`, `Person p = db.QueryOne<Person>(sql, args)`.

## Mapping: objects and tables

A class or record maps to a table by convention, attributes change it:

```fire
enum Level { Low, Mid, High }
record Address { string city; string zip; construct(string c, string z) { this.city = c; this.zip = z } }
[Table "people"]
class Person {
    [Key] int id                                    // the key; an int key is generated by the database
    [Column "full_name"] [Required] [Length 100] string name
    [Unique] string email
    int age
    DateTime born                                   // stored as ISO text (sortable)
    Level level                                     // stored by the name of the member
    [Json] Address address                          // a record or class as JSON text
    [Ignore] string shown                           // not a column
    List orders                                     // not a column (the relations come later)
    construct(string name, string email) { this.name = name; this.email = email }
}
var db = new Data.Connection("sqlite:shop.db")
db.Ensure<Person>()                                 // CREATE TABLE, missing columns, indexes - never drops
var p = new Person("Ada", "ada@x.org")
db.Insert(p)                                        // p.id is set (the result is the key as well)
p.age = 37
db.Update(p)                                        // every column by the key
db.Save(p)                                          // insert when new (no key yet), else update
Person same = db.Find<Person>(p.id)                 // or db.Find("Person", p.id)
List<Person> all = db.All<Person>()
db.Delete(p)
```

| | |
|---|---|
| table | the name of the class (without its namespace); `[Table "name"]` |
| key | the field with `[Key]`, else a field `id`; an `int` key is generated (`[Key "manual"]`: not); a text key is the program's |
| column | every field of a type the database stores: `int`, `float`, `string`, `char`, `bool` (1/0), `byte data[]` (BLOB), `DateTime` (ISO text), an enum (its name); a class or record only with `[Json]`; lists and other classes are left out |
| `[Column "name"]` | another name for the column |
| `[Required]`, `[Length n]` | NOT NULL; `VARCHAR(n)` |
| `[Unique]`, `[Index]` | a (unique) index on the column (`ix_<table>_<column>`) |
| `[Ignore]` | not a column |

- `Insert(obj)` writes a new row; a generated key goes into the key field of a class (a record keeps its fields - the key is the result).
- `Update(obj)` writes every column by the key (the number of rows: 0 when there is no row), `Delete(obj)` deletes by the key, `Save(obj)` inserts or
  updates.
- `Find(typeName, key)` and `All(typeName)` read objects; `QueryList`/`QueryOne` use the same mapping, so a renamed column fills its field.
- `Ensure(typeName)` makes the table, adds the columns it lacks and the indexes - for tests, tools and small programs; migrations come later.
- A class with `[Table]` is kept by a native build (stripping) - its fields are read by reflection. A class without it works as well, as long as the
  program uses it elsewhere.

## Transactions

```fire
db.Transaction(t => {                 // Commit at the end, Rollback when it throws (the exception goes on)
    t.Execute("update account set balance = balance - ? where id = ?", [10, 1])
    t.Execute("update account set balance = balance + ? where id = ?", [10, 2])
})
db.Begin()                            // the same by hand
db.Execute(...)
db.Commit()                           // or db.Rollback()
```

The lambda gets the connection (`t`), so it needs no capture. A transaction inside another one is a savepoint: its `Rollback` goes back to where it began,
the outer one goes on. `InTransaction` tells whether one is open.

## Waiting and threads

Every call can be **awaited** - `await` runs it as a task of its own while the caller waits, so other tasks go on:

```fire
List people = await db.QueryList("Person", "select * from person")
any both = await [db.Scalar("select count(*) from person"), other.Scalar("select max(id) from person")]   // two connections at once
```

A call that waits for the disk or for a lock of another connection gives its worker to other tasks meanwhile (natively the package lets go of its lock
while SQLite works). Two calls on the same connection at the same time are not possible - one connection, one statement at a time.

## Pools and servers

A connection belongs to one task at a time. Many tasks - a server's requests, workers - share the connections of a **pool**:

```fire
var pool = new Data.Pool("sqlite:shop.db", undefined, 10)      // at most 10 open at once (url, credentials, max)
var c = pool.Take()                     // a connection, moved to this task (opened when none is idle; waits when all are in use)
c.Execute(...)
pool.Give(take c)                       // back: the pool's again
int n = pool.Use(c => c.Scalar("select count(*) from person"))   // take, run, give back - also when it throws
pool.Close()                            // at the end: closes the idle ones (those in use when they come back)
```

- `Take(limit)` waits at most `limit` (default 10 s; a time value) for a connection that comes back - then a `TimeoutException`. `Idle` and `Open`
  count the connections.
- A connection that comes back with an open transaction is rolled back; a closed one is forgotten (a new one may be opened instead).
- The pool can go to any task and thread (its connections live in an actor of its own); a connection goes only with `Take` and `Give` (moved, never copied).

**For an Http.AppServer** (`#import "http.data"`): the link `Http.Database` gives every request a connection of the pool - taken when the request first
asks for it, given back when the request ends (also when it throws; an open transaction is rolled back then):

```fire
var server = new Http.AppServer("0.0.0.0", 8080)
server.PushTo(new Http.Sessions()).PushTo(new Http.Database(pool)).PushTo(new Http.Rpc("/rpc")).PushTo(take routes)
routes.Map("GET", "/count", func (Http.Context ctx) => "" + Http.Database.Of(ctx).Scalar("select count(*) from person"))

[Remote]
class People {                           // an Http.Rpc class of lifetime "request" gets the connection of the request
    Data.Connection db
    construct(Data.Connection db) { this.db = db }
    [Remote] List<Person> Adults() { return this.db.From<Person>().Where(p => p.age >= 18).ToList() }
}
```

`Http.Database.Of(ctx)` is the same connection for the whole request. Http.Rpc gives a constructor parameter of a class of lifetime "request" what the
items of the request offer - an item of that type, or what an item with a method `Provide(typeName)` gives (the scope of `Http.Database` gives the
connection); the services of `new Http.Rpc(prefix, services)` come first.

## Errors

| Exception | When |
|---|---|
| `Data.ConstraintException` | a constraint was broken: UNIQUE, PRIMARY KEY, FOREIGN KEY, NOT NULL, CHECK |
| `Data.BusyException` | another connection held the lock longer than the timeout |
| `Data.ConnectionException` | the database cannot be opened (no such file or folder, not a database, the host does not allow it), no driver for the scheme |
| `Data.StateException` | the connection is closed; Commit or Rollback without Begin |
| `Data.DataException` | every other error of the database (a syntax error, no such table, ...) - the base of the others |
| `ArgumentException` | a wrong number of parameters, a value that cannot be a parameter, several statements with parameters |

`Code` is the driver's code of the error (for SQLite the extended result code: 1555 is a broken primary key, 2067 a broken UNIQUE), `message` its text.

## Queries with expression trees

`db.From<Person>()` is a query of the objects of a mapped class; its conditions and keys are lambdas that the compiler hands over as **expression trees**
(docs/language/12-lambdas.md), and the query turns them into the SQL of the database when it runs:

```fire
var min = 18
List<Person> adults = db.From<Person>()
    .Where(p => p.age >= taking min && p.city != undefined)
    .OrderBy(p => p.name).ThenByDescending(p => p.age)
    .Skip(20).Take(10)
    .ToList()
Person oldest = db.From<Person>().OrderByDescending(p => p.age).First()
int n = db.From<Person>().Where(p => p.name.StartsWith("A")).Count()
int gone = db.From<Person>().Where(p => p.age < 18).Delete()
print(db.From<Person>().Where(p => p.age >= taking min).ToSql())     // SELECT * FROM "people" WHERE ("age" >= ?) [18]
List<Person> london = await db.From<Person>().Where(p => p.city == "London").ToList()
```

- `Where` (several: all must hold), `OrderBy`/`OrderByDescending`, `ThenBy`/`ThenByDescending`, `Skip`, `Take`; nothing runs before an end: `ToList`,
  `First` (a `Data.DataException` when there is no row), `FirstOrDefault` (undefined), `Count`, `Any`, `Delete`, `ToSql` (the SQL and its parameters).
- What the lambda takes from the code around it is written with `taking` (`taking min`, like in every lambda: a copy, made with the lambda) - a value
  that does not depend on the object (a variable, a field, the result of a call) is a **parameter** (`?`), its value when the query was written, never text
  in the SQL. A value compared with a column is converted like the mapping does (a `DateTime` to its ISO text, an enum to its name).
- The database runs: the columns (`p.age` - the field's column, also a renamed one), `== != < <= > >=`, `&& || !`, `+ - * / %` (`+` of texts joins them),
  `?? `, `x ? a : b`, `== undefined` (`IS NULL`), of a text `StartsWith`, `EndsWith`, `Contains` (case-sensitive, like fire), `ToLower`, `ToUpper`, `Length`,
  and an array or a `List` of the program that `Contains` a column (`IN (...)`: `var wanted = ["Ada", "Linus"]; db.From<Person>().Where(p => (taking wanted).Contains(p.name))`).
- The columns of what a [BelongsTo] field points to (`o.customer.name`, `o.customer.country.name` - as many hops as needed), in conditions and in
  `OrderBy`: a subquery per hop, nested, in the database (nothing is loaded). `o.customer == undefined` is its foreign key `IS NULL`. A list (a [HasMany]
  or [ManyToMany] field) is reached with `Any`/`All` (below), not by a member.
- Anything else - a method of your own, a member of a list, `is of` - is a `Data.NotTranslatableException` that names the part of the lambda.
- The same query runs on every driver; the in-memory database gets the same SQL.

## Sessions: a unit of work

`db.Session()` keeps the objects it reads and gets, and `Save()` writes what changed since - in one transaction:

```fire
var s = db.Session()
Person p = s.Find<Person>(7)             // read and kept (a second Find gives the same object - one object per row)
p.age = 37                               // a change: Save finds it by comparing with what was read
s.Add(new Person("Grace", "grace@x.org"))  // a new object: the session owns it, Save inserts it
s.Remove(s.Find<Person>(3))              // Save deletes it
List<Person> london = s.From<Person>().Where(p => p.city == "London").ToList()   // queries of the session: its objects
s.Save()                                 // INSERT, UPDATE (only the changed columns), DELETE - all or nothing
await s.Save()                           // the same, awaited
```

- **One object per row:** `Find`, `From<T>()...ToList()`, `First` and `All<T>()` of a session give the object the session has for a row, however often it is
  read; the session owns them (they live as long as it does). `Count`, `HasChanges`, `IsChanged(obj)` tell what it holds.
- **Save** writes the added objects (their generated keys go into them), then the changed ones (an UPDATE of the columns whose values differ from what was
  read or saved last), then the removed ones; the number of rows written. When anything fails - a constraint, a conflict - nothing is written (Rollback),
  the exception goes on, and the generated keys of that Save are undefined again; the session keeps its objects, so the program can fix them and save again.
- **Optimistic concurrency:** an `int` field with `[Version]` is counted by every Save that writes the row (1 for a new one) and checked: when somebody
  else changed or deleted the row since it was read, the UPDATE/DELETE finds no row and Save throws a `Data.ConcurrencyException` (nothing written).
- `Attach(obj)` takes an object the session does not know but whose row is there (it came over RPC, from another thread - `s.Attach(take p)`): its changes
  from then on are written; an object without a key is added instead. `Detach(obj)` gives an object out of the session (`Person p = s.Detach(p)`): it
  belongs to the caller, its changes are not written any more.
- A key does not change (Save says so); delete the object and add a new one.
- Without a session the per-object calls stay (`db.Insert`, `db.Update`, `db.Save`, `db.Delete`): for a record, a single write, a tool.

## Relations

Navigation fields connect the classes; the foreign key stays an ordinary field (a column):

```fire
class Customer {
    [Key] int id
    string name
    [HasMany "customerId"] List<Order> orders                         // the orders whose customerId is this key
}
class Order {
    [Key] int id
    int customerId
    float total
    [BelongsTo "customerId"] Customer customer                        // the customer whose key is customerId
}
class Article {
    [Key] int id
    [ManyToMany "article_tag", "articleId", "tagId"] List<Tag> tags   // the pairs in the join table article_tag
}
```

- **Loading:** `Include(c => c.orders)` (also `o => o.customer`, `a => a.tags`) loads a navigation field with the objects of a query - one query more
  per Include, not one per object; `s.Load(obj, "orders")` loads it later for objects of a session. Without a session the loaded objects belong to the
  object they hang on: the children to its list, a [BelongsTo] object is a copy of its own for each one. In a session they are the session's - one object
  per row: two orders of one customer point to the same `Customer`, and a change of it is written once.
- **Conditions:** `Where(c => c.orders.Any(o => o.total > taking limit))`, `c.orders.Any()`, `c.orders.All(o => ...)`, `a.tags.Any(t => t.label == "x")` - a
  subquery (`EXISTS`) in the database, nothing is loaded for it.
- **Writing (sessions):** an object added to a list of an object of the session (`ada.orders.Add(new Order(...))`) is inserted by Save with the parent's key
  in its foreign key (the parent first when it is new); an object set in a [BelongsTo] field is inserted first when it is new, and its key goes into the
  foreign key. Moving a child from one list to another is an update of its foreign key; taking it out of a list does not delete it (`Remove` does).
- **New or there?** An object the session reaches through a navigation field without knowing it is new without a key (or with 0 for a generated
  one) and there with a generated key. With a key of the program - a text, `[Key "manual"]` - the database is asked (one `SELECT count(*)` by the key
  at Save): new when the table has no row with it. One that is there is not inserted; in a [HasMany] list it is read from then on, so its foreign key is
  written when it moved into the list; in a [ManyToMany] list only the pair is written.
  The pairs of a [ManyToMany] list are written from the difference to what was loaded or saved last (a list that was never loaded is compared with the
  rows that are there); removing an object removes its pairs.
- `Ensure` makes the join table of a [ManyToMany] field (its two columns and an index).

## MySQL and MariaDB

`#import "data.mysql"`, `new Data.Connection("mysql://db.local:3306/shop", new Data.Credentials("app", password))`. The driver is written in fire on
`net` and `tls` - no client library: the wire protocol 4.1, prepared statements in the binary protocol (the parameters never become text in the SQL),
`Execute` without parameters as one text (several statements separated by `;` work there).

| URL (MySQL) | |
|---|---|
| `mysql://host/shop` | port 3306, the database `shop`; `mysql://user@host:3307/shop`: the user in the URL when no credentials are given |
| `?tls=prefer` | the default: TLS when the server offers it, without checking the certificate |
| `?tls=require` | TLS or no connection (`Data.ConnectionException`); `tls=verify`: the certificate is checked (with the system's authorities or `&ca=file.pem`); `tls=off`: never |
| `&key=server.pem` | the public RSA key of the server, for logins without TLS (below) |
| `&fetchkey=true` | without TLS and without `key`: the server may send its RSA key |
| `&timeout=10000` | connecting and waiting for an answer, in ms |

The logins `mysql_native_password`, `caching_sha2_password` (MySQL 8's default) and `sha256_password` are understood, and the switch of the server
to another plugin. `caching_sha2_password` sends only a hash once the server has the user in its cache; the first login after a start or
`FLUSH PRIVILEGES` (and every login with `sha256_password`) sends the password itself: over TLS as text, without TLS encrypted with the server's RSA
key (OAEP; the RSA is written in fire). That key comes from `key=file.pem` (MySQL keeps it as `public_key.pem` in its data directory) or, with
`fetchkey=true`, from the server - only where nobody can pose as the server, because a false server could send its own key. Without TLS, key and
permission the login is a `Data.ConnectionException` (2061) that says so.

Rows are read from the socket one at a time while `Next`/`foreach` asks for them - a big result is never in memory as a whole. When another command
runs on the connection while a reader still has rows (an `Execute` in the `foreach`, a second reader), the rest of the first result is read and kept
for its reader, so both go on as with SQLite. A reader that is left early (`break`, `Close`) throws its remaining rows away. A `CALL` of a procedure
gives the rows of its first result; the other results are read and dropped.

Values: integers (also UNSIGNED) are `int`, DOUBLE/FLOAT `float`, DECIMAL a `float` (an `int` without a fraction), texts `string` (utf8mb4), binary
columns `byte[]`, DATETIME/DATE/TIMESTAMP/TIME ISO text (the mapping makes a `DateTime` of it), NULL `undefined`. Errors: duplicate keys, foreign keys,
NOT NULL are `Data.ConstraintException`, lock waits and deadlocks `Data.BusyException`, a wrong login or database `Data.ConnectionException` - with
the server's error number in `Code`. The dialect makes `BIGINT AUTO_INCREMENT` keys, `VARCHAR(255)` for strings with a key or an index (`[Length n]`:
`VARCHAR(n)`), `LONGTEXT`/`LONGBLOB` otherwise, and compares texts in `StartsWith`/`Contains`/`EndsWith` byte by byte (MySQL's collations ignore case,
fire's methods do not).

The tests run against a server when `FIRE_TEST_MYSQL` names an empty database (`mysql://127.0.0.1:3306/test`, several separated by spaces - e.g.
a MariaDB and a MySQL 8; with `FIRE_TEST_MYSQL_USER` and `FIRE_TEST_MYSQL_PASSWORD`; its tables are dropped): the SQL of MySQL, streaming, procedures,
TLS, the RSA encryption against a known ciphertext, on MySQL 8 the logins without TLS (when the user may make users: CREATE USER, RELOAD), then the
mapping, queries, sessions and relations of the in-memory case.

## SQL Server

`#import "data.sqlserver"`, `new Data.Connection("sqlserver://db.local:1433/shop", new Data.Credentials("app", password))`. The driver is written in fire on
`net` and `tls` (TDS 7.4, no client library): a SQL login, SQL batches for `Execute` without parameters (several statements at once), `sp_executesql` with
typed parameters for the rest - the `?` of the SQL become `@p0`, `@p1`, ... (not inside texts, `[names]` or comments).

| URL (SQL Server) | |
|---|---|
| `sqlserver://host/shop` | port 1433, the database `shop` |
| `?encrypt=mandatory` | the default: everything encrypted - TLS inside TDS (its handshake in the PRELOGIN packets), the certificate checked |
| `&trust=true` | accept the certificate of the server unchecked (a server with a certificate of its own, as SQL Server makes one); `&ca=file.pem`: the authorities to trust |
| `?encrypt=optional` | only the login encrypted when the server asks for no more (what old clients did: the data goes in the clear) |
| `?encrypt=strict` | TDS 8.0: TLS first, then TDS inside it - SQL Server 2022 with a certificate configured for it, Azure SQL |
| `?encrypt=off` | no TLS at all, only where the server allows it and nobody can listen (the password is only obfuscated) |
| `&mars=true` | Multiple Active Result Sets: several results open at once (SMP sessions on the one connection) - a reader in a reader or an `Execute` in a `foreach` runs in a session of its own instead of keeping the rest of the rows in memory |
| `sqlserver://host\SQLEXPRESS/shop`, `?instance=SQLEXPRESS` | a named instance: its port comes from the SQL Server Browser (UDP 1434, `&browser=port` for another one); with `host:port` the port is used as it is |
| `&timeout=10000` | connecting and waiting for an answer, in ms |

Values: the integer types are `int`, `bit` is `bool`, `float`/`real` are `float`, `decimal`/`numeric` a `float` (an `int` without a fraction),
`money` a `float`, texts (`nvarchar`, `varchar` with its code page, `nchar`, `xml`) `string`, binary types `byte[]`, `uniqueidentifier` its text,
`date`/`time`/`datetime`/`datetime2` ISO text, `datetimeoffset` the local time with its offset (`2026-11-13 10:00:00 +02:00`), `sql_variant` its value,
NULL `undefined`. Parameters: `int` as `bigint`, `bool` as `bit`, `float` as `float`, texts as `nvarchar(4000)` (longer: `nvarchar(max)`), bytes as
`varbinary`. Big values go over several packets and as PLP. Rows are read from the socket one at a time as with MySQL (a command in between keeps
the rest for its reader). A generated key comes from `SCOPE_IDENTITY()` in the same request as the `INSERT`; the transaction descriptor of the server
goes with every request, and the transactions go as requests of the transaction manager of TDS (begin, commit, rollback, savepoints - with
MARS a transaction begun in a batch may not outlive it), so `Transaction`, savepoints and the session work as elsewhere. With MARS each session has
its own stream of packets and its own window (the SMP flow control); a session that is free again serves the next request.

Errors: duplicate keys (2627, 2601), foreign keys and checks (547), NOT NULL (515) are `Data.ConstraintException`, deadlocks and lock time-outs
`Data.BusyException`, a wrong login or database (18456, 4060) `Data.ConnectionException` - the server's error number in `Code`. The dialect makes
`BIGINT IDENTITY` keys, `NVARCHAR(450)` for strings with a key or an index, `NVARCHAR(MAX)`/`VARBINARY(MAX)` otherwise, `BIT` for `bool`; a unique
index leaves NULL out (`WHERE c IS NOT NULL`, as the other databases do); paging is `OFFSET ... FETCH` (with `ORDER BY (SELECT NULL)` when the query
has no order); a boolean column alone as a condition becomes `c = 1`; `StartsWith`/`Contains`/`EndsWith` compare with a binary collation (the default
collations ignore case, fire does not).

The tests run against a server when `FIRE_TEST_SQLSERVER` names an empty database (`sqlserver://127.0.0.1:1433/test?trust=true`, with
`FIRE_TEST_SQLSERVER_USER` and `FIRE_TEST_SQLSERVER_PASSWORD`; its tables are dropped). Tried against SQL Server 2022 (16.0) on Linux. Not built yet:
Windows logins (Kerberos/NTLM), table-valued parameters; `encrypt=strict` is written to
the protocol but could not be tried (the server on Linux did not accept a certificate for it).

## Data models

A data model (`Shop.fdata`, JSON - entities, fields, enums, records, relations, the diagram of the designer) is compiled into the classes with these
attributes, `Shop.Model` and `Shop.Context` (`ctx.Customers` is `db.From<Customer>()`, `ctx.Session()`, `ctx.Ensure()`, `ctx.Script()`): see
[data models](designer.md). `db.CreateScript("Person")` gives the SQL that makes the table of a class (and its join tables and indexes) in an empty
database without running it, `db.CreateScriptOf(types)` that of several (each table once).

## Migrations

A migration is a class of the program with `[Migration n, "title"]` and `Up(Data.Migrator m)` (and `Down` to undo it). `db.Migrate()` runs the ones that
have not run on the database, in the order of their numbers, each in a transaction; the table `_fire_migrations` keeps which ran.
`db.MigrateTo(n)` goes up to `n` or down (with the `Down` methods, the newest first); `db.Applied()` gives the numbers that ran.

```fire
[Migration 2, "email"]
class AddEmail {
    undefined Up(Data.Migrator m) {
        m.AddColumn("people", m.Column("email", "string", 200))
        m.CreateIndex("ix_people_email", "people", "email", true)
        m.Sql("update people set email = ? where email is null", [""])
    }
    undefined Down(Data.Migrator m) {
        m.DropIndex("ix_people_email", "people")
        m.DropColumn("people", "email")
    }
}
db.Migrate()
```

The steps of `Data.Migrator` are in the words of no database - the dialect of the connection makes the SQL, so one migration runs on SQLite, MySQL,
SQL Server and the in-memory database alike: `CreateTable(table, [m.Key("id"), m.Column("name", "string", 100, true), ...])` (a column: name, kind -
`int`, `float`, `bool`, `string`, `bytes`, `time`, `enum`, `json` -, length, required; `m.Key(name, kind = "int", generated = true)`), `DropTable`,
`AddColumn`, `DropColumn`, `RenameColumn`, `CreateIndex(name, table, column, unique)`, `DropIndex`, `Sql(text, args)` for the rest, and
`CreateTableFor("Person")` (what `Ensure` makes from the mapping as it is now - for the first migration of a new program; later ones should name their
columns, so that they still do the same when the class has changed). A migration that throws is rolled back and nothing of it is recorded; on MySQL and
MariaDB a change of a table (CREATE, ALTER, DROP) cannot be rolled back - it commits on its own -, so there the steps before the failure stay.

`db.Schema()` reads the tables as the database has them: `Data.SchemaTable` (`name`, `columns` of `Data.SchemaColumn` - `name`, `type` as the
database names it, `nullable`, `key` -, `indexes` of `Data.SchemaIndex` - `name`, `column`, `unique`).

### forge data

```
forge data status  [program] --url <url>               which migrations of the program ran on the database
forge data migrate [program] --url <url> [--to N]      runs the missing ones, or goes up/down to N
forge data diff    [program] --url <url> [--title T] [-o file]   the migration that the database lacks
forge data import  --url <url> [--namespace NS] [-o file]        classes for the tables of a database
```

The program is files, a project or a solution (`-p` for one of its projects), or the project in the current folder; its classes and migrations run in the
VM, its top level does not (as with `forge test`). `--user` and `--password` (or the environment variable `FIRE_DATA_PASSWORD`) for a server; the driver
comes from the scheme of the URL. `diff` compares the mapping of the classes with `[Table]` with the database - after the migrations of the program, so
they must have run (else it says so) - and writes the next migration as fire code: new tables, new columns, the indexes of `[Unique]` and `[Index]`
columns, the join tables of `[ManyToMany]` fields, and a `Down` that undoes it in the reverse order. It never drops anything: what only the database
has is named in a comment; a new `[Required]` column is added as one that may be NULL, with a note (the rows that are there have no value). `import`
writes a class per table with `[Table]`, `[Key]`, `[Required]`, `[Length]`, `[Unique]`/`[Index]` and `[Column]` where a name is no fire name - the start
of a mapping to edit.

## The in-memory database

`#import "data.memory"`, `new Data.Connection("memory:")`: a database in the memory of the program, its own for the connection - written in fire, no
file, nothing to install. It is for tests (the same code as with a database on disk, fast, empty at every start) and for small programs. It understands
a subset of SQL, the part that programs and the mapping use, with SQLite's rules for values, NULL and errors:

| | |
|---|---|
| schema | `CREATE TABLE [IF NOT EXISTS] t (c TYPE [PRIMARY KEY] [NOT NULL] [UNIQUE] [DEFAULT v], ..., [PRIMARY KEY (c)], [UNIQUE (c)])`, `DROP TABLE [IF EXISTS]`, `ALTER TABLE t ADD [COLUMN] ...`, `ALTER TABLE t DROP [COLUMN] c`, `ALTER TABLE t RENAME [COLUMN] a TO b`, `CREATE [UNIQUE] INDEX [IF NOT EXISTS] i ON t (c)`, `DROP INDEX` |
| rows | `INSERT INTO t [(c, ...)] VALUES (...)[, (...)]`, `INSERT ... DEFAULT VALUES`, `UPDATE t SET c = e, ... [WHERE e]`, `DELETE FROM t [WHERE e]` |
| queries | `SELECT [DISTINCT] * \| e [AS name], ... [FROM t] [WHERE e] [ORDER BY e [ASC\|DESC], ...] [LIMIT n [OFFSET m]]` - one table |
| expressions | literals, `?`, columns, `+ - * / %`, `\|\|`, `= != <> < <= > >=`, `AND OR NOT`, `IS [NOT] NULL`, `[NOT] LIKE`, `[NOT] IN (...)`, `[NOT] BETWEEN`, `lower upper length abs trim substr coalesce ifnull` |
| aggregates | `count(*)`, `count(e)`, `count(DISTINCT e)`, `sum`, `total`, `min`, `max`, `avg` - over all matching rows (no GROUP BY yet) |
| transactions | `BEGIN`, `COMMIT`, `ROLLBACK`, `SAVEPOINT s`, `RELEASE s`, `ROLLBACK TO s` - so `Transaction(...)` and nested ones work |

An `INTEGER PRIMARY KEY` is generated like SQLite's rowid; PRIMARY KEY, UNIQUE (also a unique index) and NOT NULL are checked
(`Data.ConstraintException` with SQLite's codes). What is not in the subset - joins, GROUP BY, subqueries - is a `Data.DataException` ("syntax error").
The table `_columns` (`table_name`, `name`, `type`, `pk`, `notnull`) describes the tables (what `Ensure` asks), `_indexes` (`name`, `table_name`,
`column_name`, `is_unique`) their indexes. Queries of the ORM with expression trees
(next steps) run on the rows directly, without SQL.

## Writing a driver

A driver is a package with a class that has `[Driver "scheme"]` and `[Keep]` and implements `Data.IDriver` (`Open(target, credentials)` gives a
`Data.IDriverConnection`: `Prepare`, `Exec`, `Changes`, `LastId`, `Dialect`, `Close`; a prepared `Data.IStatement`: `Bind`, `Next`, `Values`, `Names`, `Reset`,
`Close`). `Data.Connection` does the rest - the reader, the mapping to objects, the transactions (with the SQL of the driver's `Data.Dialect`). The SQLite
driver (`packages/data.sqlite`) is the example.
