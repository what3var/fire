# Streams and text (`#import "io"`)

The library `io` (package `fire-io`, namespace `IO`) is the base of everything that reads and writes bytes: files ([`filesystem`](filesystem.md)),
network connections ([`net`](../networking/README.md)), processes ([`process`](process.md)), compression ([`compression`](compression.md)), USB.
They are all an `IO.Stream`, so the same code reads from any of them.

```fire
#import "io"

var memory = new IO.MemoryStream()
var writer = new IO.TextWriter(memory, true)       // true: leave the stream open when the writer closes
writer.WriteLine("first line")
writer.WriteLine("second line")
writer.Close()

memory.Seek(0)
var reader = new IO.TextReader(memory)
foreach (line in reader) { print(line) }           // first line, second line
print(memory.Length)                               // 23
```

## Streams

| Member | Meaning |
| --- | --- |
| `Read(buffer, offset, count)`, `Read(buffer)` | reads up to `count` bytes into a [byte buffer](../language/07-arrays-and-lists.md#byte-buffers); returns how many (0: the end) |
| `Write(buffer, offset, count)`, `Write(buffer)` | writes bytes |
| `ReadByte()` (-1 at the end), `WriteByte(value)`, `ReadBytes(count)`, `ReadAll()` | single bytes, a new buffer |
| `CopyTo(target, bufferSize)` | copies the rest of the stream into another one |
| `Seek(offset, origin)`, `Position`, `Length` | position (`IO.SeekOrigin.Begin`, `Current`, `End`), only where `CanSeek` |
| `CanRead`, `CanWrite`, `CanSeek` | what the stream can do |
| `Flush()`, `Close()` | the destructor closes, too |

`IO.IStream` is the interface of the four basic methods (`Read`, `Write`, `Flush`, `Close`); `Net.IConnection` extends it. A class of your own becomes a
stream by deriving from `IO.Stream` and overriding `Read`/`Write` (and `CanRead`/`CanWrite`).

`IO.MemoryStream()` is a stream in memory (`new IO.MemoryStream(buffer)` reads a buffer); `ToBuffer()` gives what was written.

## Text

| Class | Meaning |
| --- | --- |
| `IO.TextReader(source, leaveOpen = false)` | reads UTF-8 text from a stream: `ReadLine()` (`undefined` at the end), `ReadLines()`, `ReadAll()`, `EndOfStream`, `foreach (line in reader)` |
| `IO.TextWriter(target, leaveOpen = false)` | writes text as UTF-8: `Write(value)`, `WriteLine(value)`, `Flush()` |
| `IO.Utf8` | `GetBytes(text)`, `GetString(buffer, offset, count)` - text and bytes (`"text".ToBytes()` is ASCII only) |
| `IO.Stdio` | the standard streams: `In()`, `Out()`, `Err()` (as streams), `Write`, `WriteLine`, `ErrorWrite`, `ErrorLine`, `Flush` |

Lines end with `\n`, `\r\n` or `\r`; a reader gives them without the ending.

```fire
#import "io"

IO.Stdio.WriteLine("to standard output")
IO.Stdio.ErrorLine("to standard error")
var input = new IO.TextReader(IO.Stdio.In())
var line = input.ReadLine()                        // what the user types (undefined: no more input)
```

## Errors

`IO.IOException` (with a `code`) and its subclasses `FileNotFoundException`, `DirectoryNotFoundException`, `PermissionException`,
`FileExistsException`, `StreamClosedException`.

## The host decides

In spark and `forge` a program may read and write everything. A host that embeds fire can restrict it (`IoPolicy`: nothing, read only, some
folders) - a denied access is a `PermissionException` ([embedding](../../src/fire.Editor/Help/Embedding.md)).
