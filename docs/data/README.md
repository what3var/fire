# Data: databases

`#import "data"` gives the namespace `Data`: connections to databases, SQL with bound parameters, rows as objects, transactions. The drivers are
packages of their own; today there are **SQLite** (`#import "data.sqlite"`) and the **in-memory database** (`#import "data.memory"`); both import `data`.
What comes next - MySQL, SQL Server, the ORM with expression trees, sessions, migrations and the designer - is planned in [ROADMAP_DATA.md](../ROADMAP_DATA.md).

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
- Anything else - a method of your own, a member of a member, `is of` - is a `Data.NotTranslatableException` that names the part of the lambda.
- The same query runs on every driver; the in-memory database gets the same SQL.

## The in-memory database

`#import "data.memory"`, `new Data.Connection("memory:")`: a database in the memory of the program, its own for the connection - written in fire, no
file, nothing to install. It is for tests (the same code as with a database on disk, fast, empty at every start) and for small programs. It understands
a subset of SQL, the part that programs and the mapping use, with SQLite's rules for values, NULL and errors:

| | |
|---|---|
| schema | `CREATE TABLE [IF NOT EXISTS] t (c TYPE [PRIMARY KEY] [NOT NULL] [UNIQUE] [DEFAULT v], ..., [PRIMARY KEY (c)], [UNIQUE (c)])`, `DROP TABLE [IF EXISTS]`, `ALTER TABLE t ADD [COLUMN] ...`, `CREATE [UNIQUE] INDEX [IF NOT EXISTS] i ON t (c)`, `DROP INDEX` |
| rows | `INSERT INTO t [(c, ...)] VALUES (...)[, (...)]`, `INSERT ... DEFAULT VALUES`, `UPDATE t SET c = e, ... [WHERE e]`, `DELETE FROM t [WHERE e]` |
| queries | `SELECT [DISTINCT] * \| e [AS name], ... [FROM t] [WHERE e] [ORDER BY e [ASC\|DESC], ...] [LIMIT n [OFFSET m]]` - one table |
| expressions | literals, `?`, columns, `+ - * / %`, `\|\|`, `= != <> < <= > >=`, `AND OR NOT`, `IS [NOT] NULL`, `[NOT] LIKE`, `[NOT] IN (...)`, `[NOT] BETWEEN`, `lower upper length abs trim substr coalesce ifnull` |
| aggregates | `count(*)`, `count(e)`, `count(DISTINCT e)`, `sum`, `total`, `min`, `max`, `avg` - over all matching rows (no GROUP BY yet) |
| transactions | `BEGIN`, `COMMIT`, `ROLLBACK`, `SAVEPOINT s`, `RELEASE s`, `ROLLBACK TO s` - so `Transaction(...)` and nested ones work |

An `INTEGER PRIMARY KEY` is generated like SQLite's rowid; PRIMARY KEY, UNIQUE (also a unique index) and NOT NULL are checked
(`Data.ConstraintException` with SQLite's codes). What is not in the subset - joins, GROUP BY, subqueries - is a `Data.DataException` ("syntax error").
The table `_columns` (`table_name`, `name`, `type`, `pk`, `notnull`) describes the tables (what `Ensure` asks). Queries of the ORM with expression trees
(next steps) run on the rows directly, without SQL.

## Writing a driver

A driver is a package with a class that has `[Driver "scheme"]` and `[Keep]` and implements `Data.IDriver` (`Open(target, credentials)` gives a
`Data.IDriverConnection`: `Prepare`, `Exec`, `Changes`, `LastId`, `Dialect`, `Close`; a prepared `Data.IStatement`: `Bind`, `Next`, `Values`, `Names`, `Reset`,
`Close`). `Data.Connection` does the rest - the reader, the mapping to objects, the transactions (with the SQL of the driver's `Data.Dialect`). The SQLite
driver (`packages/data.sqlite`) is the example.
