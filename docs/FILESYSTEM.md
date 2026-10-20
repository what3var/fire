# Files, directories and paths (`#import "filesystem"`)

The standard library `filesystem` (package `fire-filesystem`, project `packages/filesystem`) is the file system of fire. Its classes are named and shaped like
`System.IO` in .NET and live in the namespace `IO`, next to the streams of `io` (which it imports): `IO.File`, `IO.Directory`, `IO.Path` and `IO.FileStream`.
`io` alone has the streams (`IO.Stream`, `IO.MemoryStream`, `IO.TextReader`, `IO.TextWriter`, `IO.Utf8`, `IO.Stdio`) and no file access.

```fire
#import "filesystem"

var folder = IO.Path.Combine(IO.Path.GetTempPath(), "notes")
IO.Directory.CreateDirectory(folder)
var file = IO.Path.Combine(folder, "today.txt")
IO.File.WriteAllText(file, "first line\n")
IO.File.AppendAllLines(file, ["second line"])
foreach (line in IO.File.ReadAllLines(file)) { print(line) }
print(IO.Path.GetFileNameWithoutExtension(file) + " - " + IO.File.Size(file) + " bytes, " + IO.File.GetLastWriteTime(file))
foreach (path in IO.Directory.GetFiles(folder, "*.txt", IO.SearchOption.AllDirectories)) { print(path) }
IO.Directory.Delete(folder, true)
```

The natives are those of the IO bridge (`packages/io/native/fire_bridge_io.hpp`): a `FileStream` is a handle in the same table as every other stream. Every access
goes through the host's `IoPolicy` (docs/SPEC.md 8.11): a denied access is an `IO.PermissionException`.

## `IO.Path`

Text only - nothing is read from the disk (except `GetFullPath`, which uses the current directory). On Windows both `\` and `/` separate.

| Member | |
|---|---|
| `Combine(a, b[, c[, d]])` | joins the parts; an absolute part discards everything before it |
| `GetFileName(p)`, `GetFileNameWithoutExtension(p)`, `GetExtension(p)` | `"a/b.txt"` → `"b.txt"`, `"b"`, `".txt"` |
| `GetDirectoryName(p)` | `"a/b/c.txt"` → `"a/b"` (`""` for a bare name or a root) |
| `HasExtension(p)`, `ChangeExtension(p, ext)` | `ChangeExtension("a.txt", ".md")` → `"a.md"`; `undefined` or `""` removes it |
| `GetFullPath(p)` | absolute and normalised (`.` and `..` resolved) |
| `GetRelativePath(from, to)` | `GetRelativePath("/a/b", "/a/c/d.txt")` → `"../c/d.txt"` |
| `GetPathRoot(p)`, `IsPathRooted(p)`, `IsPathFullyQualified(p)` | `"/"` or `"C:\"` |
| `GetTempPath()`, `GetTempFileName()` | the folder for temporary files; a new empty file in it |
| `DirectorySeparatorChar`, `AltDirectorySeparatorChar` | `"/"` or `"\"` |

## `IO.File`

`Exists`, `Size` (bytes), `GetLastWriteTime` / `GetLastWriteTimeUtc` (a `DateTime` of `time`), `Delete` (a missing file is not an error), `Copy(source, target,
overwrite = false)`, `Move(source, target, overwrite = false)`; whole files: `ReadAllText`, `WriteAllText`, `AppendAllText`, `ReadAllLines` (a `List`), `WriteAllLines`,
`AppendAllLines` (a `List` or an array), `ReadAllBytes`, `WriteAllBytes`, `AppendAllBytes`; streams: `Open(path, mode, access)`, `OpenRead`, `OpenWrite`, `Create`
(an `IO.FileStream`), `OpenText` (an `IO.TextReader`), `CreateText`, `AppendText` (an `IO.TextWriter`). Text is UTF-8.

## `IO.Directory`

`Exists`, `CreateDirectory` (also the folders in between), `Delete(path, recursive = false)`, `Move(source, target)`; lists of full paths, sorted:
`GetFiles(path, pattern = "*", searchOption)`, `GetDirectories(...)`, `GetFileSystemEntries(...)` (folders first) - `pattern` with `*` and `?`,
`searchOption` `IO.SearchOption.TopDirectoryOnly` (default) or `IO.SearchOption.AllDirectories` (or `true`); `GetParent(path)` (undefined for a root),
`GetDirectoryRoot`, `GetLogicalDrives()` (`C:\`, `D:\`, ... on Windows, `/` elsewhere), `GetLastWriteTime`, `GetCurrentDirectory`, `SetCurrentDirectory`,
`GetHomeDirectory()` (the user's folder: `USERPROFILE` / `HOME`).

## `IO.FileStream`

`new IO.FileStream(path, mode = IO.FileMode.Open, access)`: an `IO.Stream` on a file (`Read`, `Write`, `ReadByte`, `Seek`, `Position`, `Length`, `Flush`, `Close`, ...;
docs/SPEC.md 8.11). `IO.FileMode`: `Open`, `Create`, `CreateNew`, `OpenOrCreate`, `Append`; `IO.FileAccess`: `Read`, `Write`, `ReadWrite` (without one: `Open` reads,
`Append` writes, the rest does both). Its destructor closes it.

## Errors

`IO.FileNotFoundException`, `IO.DirectoryNotFoundException`, `IO.FileExistsException`, `IO.PermissionException` (the system or the host's policy), otherwise
`IO.IOException` - they come from `io`.

## From `io` to `filesystem`

Until now `io` had the file classes, with other names. A program that uses files adds `#import "filesystem"` (it brings `io` along) and uses the .NET names:

| before | now |
|---|---|
| `IO.Path.FileName`, `Stem`, `Extension`, `Parent`, `FullPath` | `GetFileName`, `GetFileNameWithoutExtension`, `GetExtension`, `GetDirectoryName`, `GetFullPath` |
| `IO.Path.Temp()`, `Separator()`, `IsRooted` | `GetTempPath()`, `DirectorySeparatorChar`, `IsPathRooted` |
| `IO.Directory.Create`, `Current()`; `GetFiles(path, pattern, recursive)` | `CreateDirectory`, `GetCurrentDirectory()`; `recursive` is still accepted (or `IO.SearchOption.AllDirectories`) |
| `IO.File.ModifiedTime(p)` (seconds with unit `s`) | `GetLastWriteTime(p)` (a `DateTime`) |
| `new IO.TextReader(path)`, `new IO.TextWriter(path[, append])` | `IO.File.OpenText(path)`, `IO.File.CreateText(path)` / `AppendText(path)` - the readers and writers take streams |

Tests: `packages/filesystem/testing` (`forge test packages/filesystem/filesystem.fireproj`, also with `--engine native`).
