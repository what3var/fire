# Data: databases

`#import "data"` gives the namespace `Data`: connections to databases, SQL with bound parameters, rows as objects, transactions. The drivers are
packages of their own; today there is **SQLite** (`#import "data.sqlite"`, which imports `data`). What comes next - the in-memory database, MySQL,
SQL Server, the ORM with expression trees, sessions, migrations and the designer - is planned in [ROADMAP_DATA.md](../ROADMAP_DATA.md).

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

(The type is given by its name for now; `db.QueryList<Person>(...)` comes when the compiler gives a generic method its type argument at run time. The
attributes of the ORM - tables, keys, column names - come with step 2 of the roadmap.)

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

## Writing a driver

A driver is a package with a class that has `[Driver "scheme"]` and `[Keep]` and implements `Data.IDriver` (`Open(target, credentials)` gives a
`Data.IDriverConnection`: `Prepare`, `Exec`, `Changes`, `LastId`, `Dialect`, `Close`; a prepared `Data.IStatement`: `Bind`, `Next`, `Values`, `Names`, `Reset`,
`Close`). `Data.Connection` does the rest - the reader, the mapping to objects, the transactions (with the SQL of the driver's `Data.Dialect`). The SQLite
driver (`packages/data.sqlite`) is the example.
