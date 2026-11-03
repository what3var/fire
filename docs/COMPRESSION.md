# Compression and Zip (`#import "compression"`)

The package `fire-compression` compresses with Deflate - as raw data, with the zlib framing or as gzip - and reads and writes Zip archives. The Deflate is the
package's own C (no zlib, no library): the compressed bytes are the same in the virtual machine, in a native build, with TinyCC and on a board, and the
inflater checks everything a broken or hostile file can get wrong.

```
#import "compression"

var packed = Compression.GZip.Compress(bytes)            // or a string (UTF-8); level 0 .. 9, 6 by default
var text = Compression.GZip.DecompressText(packed)

var z = new Compression.GZipStream(IO.File.Create("log.gz"), Compression.Mode.Compress)
var w = new IO.TextWriter(z)
w.WriteLine("...")
w.Close()                                                 // finishes the gzip data and closes the file

var a = Zip.Archive.Open("files.zip")
foreach (e in a.Entries) { print(e.FullName + " " + e.Length) }
print(a.GetEntry("readme.txt").ReadAllText())
a.ExtractTo("out")
a.Close()

var n = Zip.Archive.Create("new.zip")
n.Add("hello.txt", "Hallo")
n.AddFile("pics/cat.png", "cat.png")
n.Close()
Zip.Archive.CreateFromDirectory("folder", "folder.zip")
```

## Compression

| Member | Meaning |
|---|---|
| `Compression.Deflate`, `Compression.ZLib`, `Compression.GZip` | `static byte[] Compress(data, level = 6)` (`data`: bytes or a string as UTF-8), `static byte[] Decompress(bytes)`, `static string DecompressText(bytes)` |
| `Compression.DeflateStream(inner, mode, level = 6, leaveOpen = false, format = Raw)` | an `IO.Stream`: `Mode.Compress` compresses what is written into `inner`, `Mode.Decompress` decompresses what it reads from `inner`; `Flush()` (compressing) is a sync flush - a reader can decompress everything written so far; `Close()` finishes the data and closes `inner` unless `leaveOpen` |
| `Compression.GZipStream`, `Compression.ZLibStream` | the same with the gzip / zlib framing (`inner`, `mode`, `level`, `leaveOpen`) |
| `Compression.Format` | `Raw`, `ZLib`, `GZip` |
| `Compression.InvalidDataException` | (an `IO.IOException`) broken, incomplete or foreign data: a wrong check sum, a code that does not exist, a distance too far back, data that ends too early |

Levels: 0 stores (no compression), 1-3 are fast (greedy matching), 4-9 look further (lazy matching; 9 is the smallest); per block the cheapest of a dynamic,
the fixed and a stored encoding is chosen. The result is about the size of zlib's at the same level. Reading gzip also takes several members one after the
other (as `cat a.gz b.gz` makes them) and skips the optional header fields (file name, comment, extra field, header CRC). An empty input decompresses to
nothing; zlib data with a preset dictionary is not supported.

## Zip

| Member | Meaning |
|---|---|
| `Zip.Archive.Open(path)`, `Zip.Archive.Read(stream, leaveOpen = false)`, `Zip.Archive.FromBytes(bytes)` | an archive to read (the stream must be able to seek) |
| `archive.Entries`, `archive.Count`, `archive.GetEntry(fullName)`, `archive.Comment` | the entries in the order of the archive; `GetEntry` gives `undefined` for a name that is not there |
| `archive.ExtractTo(folder, overwrite = false)`, `Zip.Archive.ExtractToDirectory(zipPath, folder, overwrite = false)` | extracts everything; every name is checked first: a name that would land outside of `folder` (`../x`, `/etc/x`, `C:\x`) throws a `ZipException` and nothing is written |
| `Zip.Archive.Create(path)`, `Zip.Archive.Write(stream, leaveOpen = false)` | an archive to write (the stream need not seek) |
| `archive.Add(name, data, level = 6, time = now)` | an entry from bytes or a string (UTF-8); stored instead of deflated when that is not smaller |
| `archive.AddFile(name, path, level = 6)`, `archive.AddDirectory(name)` | a file of the disk (with its time of last change), an empty folder entry (`name/`) |
| `archive.CreateEntry(name, level = 6, time = now, zip64 = false)` | a stream to write an entry piece by piece (Deflate, with a data descriptor); close it before the next entry. It may grow beyond 4 GB; `zip64` announces that in the local header already (see below) |
| `archive.Close()` | writing: the central directory and the end record (Zip64 end records when needed); then the stream is closed (unless `leaveOpen`) |
| `Zip.Archive.CreateFromDirectory(folder, zipPath, level = 6, includeBaseDirectory = false)` | a whole folder (empty subfolders as folder entries) |
| `Zip.Entry` | `FullName` ("docs/readme.txt"; folders end with "/"), `Name` ("readme.txt"), `Length`, `CompressedLength`, `Crc32`, `CompressionMethod` (`Zip.Method.Stored`/`Deflated`), `IsDirectory`, `IsEncrypted`, `LastWriteTime` (local `DateTime`), `Comment`; `Open()` (a stream: at its end the CRC-32 and the length are checked), `ReadAllBytes()`, `ReadAllText()`, `ExtractToFile(path, overwrite = false)` |
| `Zip.ZipException` | (an `IO.IOException`) not a Zip archive, a name outside of the target folder, an encrypted entry or another method than stored/Deflate |

Reading understands Zip64 (more than 65535 entries, archives and entries above 4 GB), entries with data descriptors (written by streaming tools) and names
in UTF-8 - with or without the flag - or in code page 437. Writing uses UTF-8 names (flagged), UNIX file modes and the DOS time of the entry.

### Zip64 when writing

Zip64 is written only where a number does not fit, so an archive below 4 GB with fewer than 65535 entries has exactly the bytes it had before Zip64 writing:

* **Central directory:** an uncompressed size, compressed size or local header offset of 4 GB or more (0xFFFFFFFF or more) is written as 0xFFFFFFFF, and the
  Zip64 extra field (id 1) holds the real numbers, 8 bytes each, in that order (only the ones that did not fit); such an entry needs version 4.5.
* **End:** 65535 entries or more, a central directory at or beyond 4 GB or one of 4 GB or more add the Zip64 end of central directory record and its locator
  before the classic end record, whose fields then say 0xFFFF / 0xFFFFFFFF.
* **`Add`** (sizes known before the data): an entry of 4 GB or more gets a local header with version 4.5, 0xFFFFFFFF in both size fields and the Zip64 field with
  both sizes.
* **Streamed entries** (`CreateEntry`, and `AddFile` for files above 16 MB): the sizes follow the data in the data descriptor. Without `zip64` the local header is
  the classic one; if the entry still reaches 4 GB, its descriptor gets 8-byte sizes and the central directory the Zip64 field (readers that use the central
  directory - Python's `zipfile`, `unzip`, 7-Zip, .NET, this package - read it; a reader that only walks the local headers would have to guess the descriptor
  size, as Java's `ZipInputStream` does from the bytes it read). With `zip64 = true` the local header carries the Zip64 field (sizes 0, as the APPNOTE asks
  for an entry whose size is not known) and the descriptor always has 8-byte sizes, so every reader knows its layout. `AddFile` sets it by itself for a file
  that could reach 4 GB compressed.

`Zip.Archive.TestZip64Limits(size, count)` lowers the limits (Zip64 from `size` bytes and `count` entries on) so that tests take these paths with a few bytes;
`TestZip64Limits(0, 0)` restores 4 GB / 65535.

## Implementation

**Memory.** The compressor keeps a 32 KB window (about 240 KB of state, like zlib). On a small board (ESP-IDF) it takes a 4 KB window (about 40 KB); the output is still
ordinary Deflate, only a little larger. `FIRE_DEF_WBITS` (9 .. 15) chooses the window. Decompressing always needs the 32 KB window of the format.

`packages/compression/native/fire_cmp_deflate.h` is the engine (C99): the inflater is a state machine that stops at a symbol boundary when its input runs out
(so a stream can feed it in pieces of any size), decodes Huffman codes through a 10-bit table and checks every code, length, distance and check sum; the
deflater keeps a 32 KB window with hash chains (the design of zlib, our own code) and writes length-limited Huffman codes. `fire_bridge_compression.h` puts it
behind natives (a handle table; what a program leaves open is freed when it ends) and adds the byte work of the Zip code (little-endian fields, copies,
CRC-32, the search for the end record). The Zip format itself is fire code in the prelude.

Checked: round trips through Python's `zlib` for every level, framing and piece size, both ways (1504 cases); 1500 damaged streams under AddressSanitizer/UBSan
(no crash, an error every time the data is wrong); archives of Python's `zipfile` (Zip64, data descriptors, code page 437) read, archives written by fire
accepted by `zipfile` and `unzip -t`; the same output in the VM, with gcc, with TinyCC and for Windows (MinGW, Wine). Zip64 writing: with lowered limits in the
tests of the package and a native case (VM == native), and once for real (VM and native) - an archive of 4.3 GB with a sparse 5 GiB file through `AddFile`
(Zip64 local header, 24-byte descriptor), a streamed stored entry of 4.3 GB without `zip64` (8-byte descriptor, Zip64 field in the central directory), a small
entry and the central directory behind 4 GB (Zip64 end records); read back by this package, by `zipfile` (every byte, CRC checked) and by `unzip -t`.
