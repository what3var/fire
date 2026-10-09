namespace fire.Standard
{
    public static class IoPrelude
    {
        /// <summary>
        /// The fire source for `#import "io"` (the natives are C++, see native/bridges/fire_bridge_io.hpp) - to be placed BEFORE the actual user script
        /// if there is an `#import "io"`. Everything lives in `namespace IO`
        /// (`IO.FileStream`, `IO.File`, `IO.Path`, ...), so that no user
        /// class name such as `File` or `Stream` collides with it.
        ///
        /// Structure: `IStream` is the smallest interface (Read/Write/Flush/
        /// Close) - whoever writes an OWN stream implements it or
        /// (more conveniently) derives from `Stream`, which builds everything else (ReadByte,
        /// ReadBytes, ReadAll, CopyTo, ...) on Read/Write. `NativeStream`
        /// is the common base of `FileStream`/`MemoryStream`, behind which
        /// lies a native handle (the natives: native/bridges/fire_bridge_io.hpp); its `destruct()` closes
        /// the handle when the owner scope ends - a forgotten Close()
        /// therefore does not stay open. A destructor never throws: if closing
        /// fails (e.g. because the stream a TextWriter still wants to flush into
        /// was already closed by someone else), the IO error is
        /// swallowed there.
        ///
        /// Conventions: read functions return the number of bytes read
        /// (0 = end of stream), `ReadByte` returns -1 at the end. Errors are
        /// catchable exceptions (`IO.IOException` and derivatives, see
        /// IOErrors.Throw). Enum values are written fully qualified
        /// (`IO.FileMode.Create`) - an enum in a namespace is reachable only
        /// that way.
        /// </summary>
        public const string Source = """
            namespace IO {
                enum FileMode { Open, Create, CreateNew, OpenOrCreate, Append }
                enum FileAccess { Read, Write, ReadWrite }
                enum SeekOrigin { Begin, Current, End }

                class IOException : Exception {
                    string message
                    int code

                    construct(string message, int code = 9) {
                        this.message = message
                        this.code = code
                    }
                }

                class FileNotFoundException : IOException {
                    construct(string message) : base(message, 3) { }
                }

                class DirectoryNotFoundException : IOException {
                    construct(string message) : base(message, 4) { }
                }

                // Either the operating system (5) OR the host's policy (6) denies access.
                class PermissionException : IOException {
                    construct(string message, int code = 5) : base(message, code) { }
                }

                class FileExistsException : IOException {
                    construct(string message) : base(message, 7) { }
                }

                class StreamClosedException : IOException {
                    construct(string message) : base(message, 2) { }
                }

                class IOErrors {
                    // Throws the exception matching the last error of a native IO function.
                    static Throw() {
                        var code = __IOLastError()
                        var message = __IOLastErrorMessage()
                        if (code == 2) { throw new StreamClosedException(message) }
                        if (code == 3) { throw new FileNotFoundException(message) }
                        if (code == 4) { throw new DirectoryNotFoundException(message) }
                        if (code == 5 || code == 6) { throw new PermissionException(message, code) }
                        if (code == 7) { throw new FileExistsException(message) }
                        throw new IOException(message, code)
                    }

                    // Returns a valid handle, throws the error on -1.
                    static int Handle(int handle) {
                        if (handle < 0) { Throw() }
                        return handle
                    }

                    // The default FileAccess for a FileMode when none is given (-1).
                    static int AccessFor(int mode, int access) {
                        if (access != -1) { return access }
                        if (mode == IO.FileMode.Open) { return IO.FileAccess.Read }
                        if (mode == IO.FileMode.Append) { return IO.FileAccess.Write }
                        return IO.FileAccess.ReadWrite
                    }
                }

                interface IStream {
                    int Read(buffer, offset, count)
                    int Write(buffer, offset, count)
                    Flush()
                    Close()
                }

                // Base class for own streams: override Read/Write (and whatever else is
                // supported), the rest builds on that.
                class Stream : IStream {
                    bool CanRead { get { return false } }
                    bool CanWrite { get { return false } }
                    bool CanSeek { get { return false } }

                    int Position {
                        get { throw new IOException("This stream has no position.", 8) }
                        set { throw new IOException("This stream does not support seeking.", 8) }
                    }

                    int Length {
                        get { throw new IOException("This stream does not know its length.", 8) }
                        set { throw new IOException("The length of this stream cannot be changed.", 8) }
                    }

                    int Read(buffer, offset, count) {
                        throw new IOException("This stream is not readable.", 8)
                    }

                    int Write(buffer, offset, count) {
                        throw new IOException("This stream is not writable.", 8)
                    }

                    int Seek(int offset, int origin = IO.SeekOrigin.Begin) {
                        throw new IOException("This stream does not support seeking.", 8)
                    }

                    Flush() { }
                    Close() { }

                    int Read(buffer) { return this.Read(buffer, 0, buffer.length) }
                    int Write(buffer) { return this.Write(buffer, 0, buffer.length) }

                    // A byte (0..255), or -1 at the end of the stream.
                    int ReadByte() {
                        var one = new byte[1]
                        var n = this.Read(one, 0, 1)
                        if (n <= 0) { return -1 }
                        return one[0]
                    }

                    WriteByte(int value) {
                        var one = new byte[1]
                        one[0] = value
                        this.Write(one, 0, 1)
                    }

                    // Up to `count` bytes as a new buffer (shorter if the stream ends earlier).
                    ReadBytes(int count) {
                        var data = new byte[count]
                        var total = 0
                        while (total < count) {
                            var n = this.Read(data, total, count - total)
                            if (n <= 0) { break }
                            total = total + n
                        }
                        if (total == count) { return data }
                        var shorter = new byte[total]
                        for (var i = 0; i < total; i++) { shorter[i] = data[i] }
                        return shorter
                    }

                    // Everything up to the end of the stream as a new buffer.
                    ReadAll() {
                        var all = new MemoryStream()
                        this.CopyTo(all)
                        var result = all.ToBuffer()
                        all.Close()
                        return result
                    }

                    // Copies everything up to the end of this stream into `target`.
                    CopyTo(target, int bufferSize = 4096) {
                        var chunk = new byte[bufferSize]
                        while (true) {
                            var n = this.Read(chunk, 0, bufferSize)
                            if (n <= 0) { break }
                            target.Write(chunk, 0, n)
                        }
                    }
                }

                // Common base of FileStream and MemoryStream: behind them lies
                // a native handle. destruct() closes it when the owner ends.
                class NativeStream : Stream {
                    int handle
                    bool closed

                    construct(int handle) {
                        this.handle = handle
                        this.closed = false
                    }

                    // The subclasses first pass -1 and then open in their own
                    // constructor body: if opening fails (e.g. file not found),
                    // this object is nevertheless fully built - its destruct()
                    // then finds handle == -1 and closes nothing. (If a
                    // constructor fails already BEFORE the one of the base class ran, the fields
                    // only have the default value `false` - that cannot be relied on.)
                    destruct() { try { this.Close() } catch (IO.IOException e) { } }

                    bool IsClosed { get { return this.closed } }

                    bool CanRead { get { return !this.closed && __IOCanRead(this.handle) } }
                    bool CanWrite { get { return !this.closed && __IOCanWrite(this.handle) } }
                    bool CanSeek { get { return !this.closed && __IOCanSeek(this.handle) } }

                    // Throws if the stream is already closed.
                    Check() {
                        if (this.closed) { throw new StreamClosedException("The stream is closed.") }
                    }

                    // Closes the handle; a further Close() has no effect.
                    Close() {
                        if (this.closed) { return }
                        this.closed = true
                        if (this.handle >= 0) { __IOClose(this.handle) }
                    }

                    int Position {
                        get {
                            this.Check()
                            var p = __IOPosition(this.handle)
                            if (p < 0) { IO.IOErrors.Throw() }
                            return p
                        }
                        set {
                            this.Check()
                            if (__IOSeek(this.handle, value, IO.SeekOrigin.Begin) < 0) { IO.IOErrors.Throw() }
                        }
                    }

                    int Length {
                        get {
                            this.Check()
                            var n = __IOLength(this.handle)
                            if (n < 0) { IO.IOErrors.Throw() }
                            return n
                        }
                        set {
                            this.Check()
                            if (!__IOSetLength(this.handle, value)) { IO.IOErrors.Throw() }
                        }
                    }

                    // New position (from the start), `origin`: IO.SeekOrigin.
                    int Seek(int offset, int origin = IO.SeekOrigin.Begin) {
                        this.Check()
                        var p = __IOSeek(this.handle, offset, origin)
                        if (p < 0) { IO.IOErrors.Throw() }
                        return p
                    }

                    int Read(buffer, offset, count) {
                        this.Check()
                        var n = __IORead(this.handle, buffer, offset, count)
                        if (n < 0) { IO.IOErrors.Throw() }
                        return n
                    }

                    int Write(buffer, offset, count) {
                        this.Check()
                        var n = __IOWrite(this.handle, buffer, offset, count)
                        if (n < 0) { IO.IOErrors.Throw() }
                        return n
                    }

                    int ReadByte() {
                        this.Check()
                        var b = __IOReadByte(this.handle)
                        if (b == -1) { IO.IOErrors.Throw() }
                        if (b == -2) { return -1 }
                        return b
                    }

                    WriteByte(int value) {
                        this.Check()
                        if (__IOWriteByte(this.handle, value) < 0) { IO.IOErrors.Throw() }
                    }

                    ReadAll() {
                        this.Check()
                        var all = __IOReadRest(this.handle)
                        if (all == undefined) { IO.IOErrors.Throw() }
                        return all
                    }

                    Flush() {
                        this.Check()
                        if (!__IOFlush(this.handle)) { IO.IOErrors.Throw() }
                    }
                }

                // A file. `mode`: IO.FileMode, `access`: IO.FileAccess (if not given,
                // depending on mode: Open -> Read, Append -> Write, otherwise ReadWrite).
                class FileStream : NativeStream {
                    string name

                    construct(string path, int mode = IO.FileMode.Open, int access = -1) : base(-1) {
                        this.name = path
                        this.handle = IO.IOErrors.Handle(__IOFileOpen(path, mode, IO.IOErrors.AccessFor(mode, access)))
                    }

                    // The path, as given on opening.
                    string Name { get { return this.name } }
                }

                // A stream in memory (grows on writing). With a
                // buffer: starts with a COPY of its content, position 0.
                class MemoryStream : NativeStream {
                    construct() : base(-1) {
                        this.handle = IO.IOErrors.Handle(__IOMemNew())
                    }

                    construct(buffer) : base(-1) {
                        this.handle = IO.IOErrors.Handle(__IOMemFromBuffer(buffer))
                    }

                    // The entire content (regardless of position) as a new buffer.
                    ToBuffer() {
                        this.Check()
                        var all = __IOMemToBuffer(this.handle)
                        if (all == undefined) { IO.IOErrors.Throw() }
                        return all
                    }
                }

                // UTF-8 <-> buffer. (`string.ToBytes()` is ASCII only.)
                class Utf8 {
                    // The text as UTF-8 (without byte-order mark) in a new buffer.
                    static GetBytes(string text) { return __IOUtf8Encode(text) }

                    // A byte-order mark at the start is removed, invalid sequences
                    // become U+FFFD. Without count: up to the end of the buffer.
                    static string GetString(buffer, int offset = 0, int count = -1) {
                        if (count == -1) { count = buffer.length - offset }
                        var text = __IOUtf8Decode(buffer, offset, count)
                        if (text == undefined) { IO.IOErrors.Throw() }
                        return text
                    }
                }

                // Pure text processing on paths (no file access). As everywhere in
                // this language with the namespace: IO.Path.Combine(...).
                class Path {
                    // The character that separates directories ("/" or "\").
                    static string Separator() { return __IOPathSeparator() }

                    // Joins path parts; an absolute part discards everything before it
                    // (like Path.Combine in .NET).
                    static string Combine(string a, string b) {
                        var r = __IOPathCombine(a, b)
                        if (r == undefined) { IO.IOErrors.Throw() }
                        return r
                    }

                    static string Combine(string a, string b, string c) {
                        return IO.Path.Combine(IO.Path.Combine(a, b), c)
                    }

                    // "dir/name.txt" -> "name.txt"
                    static string FileName(string path) { return IO.Path.Text(__IOPathFileName(path)) }

                    // "dir/name.txt" -> "name"
                    static string Stem(string path) { return IO.Path.Text(__IOPathStem(path)) }

                    // "dir/name.txt" -> ".txt" ("" without extension)
                    static string Extension(string path) { return IO.Path.Text(__IOPathExtension(path)) }

                    // "dir/sub/name.txt" -> "dir/sub" ("" for a bare name)
                    static string Parent(string path) { return IO.Path.Text(__IOPathParent(path)) }

                    // The absolute, normalised path (without "..").
                    static string FullPath(string path) { return IO.Path.Text(__IOPathFull(path)) }

                    // The directory for temporary files.
                    static string Temp() { return IO.Path.Text(__IOPathTemp()) }

                    static bool IsRooted(string path) { return __IOPathIsRooted(path) }

                    static string Text(value) {
                        if (value == undefined) { IO.IOErrors.Throw() }
                        return value
                    }
                }

                // Files as a whole. Every access goes through the host's policy;
                // errors are IO.IOException and derivatives. Text is UTF-8, lines
                // are written with "\n" and read with \n, \r\n or \r.
                class File {
                    static bool Exists(string path) {
                        var r = __IOFileExists(path)
                        if (r < 0) { IO.IOErrors.Throw() }
                        return r == 1
                    }

                    // Size in bytes.
                    static int Size(string path) {
                        var n = __IOFileSize(path)
                        if (n < 0) { IO.IOErrors.Throw() }
                        return n
                    }

                    // Time of the last modification: seconds since 1970 (UTC), with the unit s.
                    static ModifiedTime(string path) {
                        var t = __IOFileTime(path)
                        if (t == undefined) { IO.IOErrors.Throw() }
                        return t * 1s   // the native gives plain seconds (units do not cross the package ABI)
                    }

                    // A missing file is not an error.
                    static Delete(string path) {
                        if (!__IOFileDelete(path)) { IO.IOErrors.Throw() }
                    }

                    static Copy(string source, string target, bool overwrite = false) {
                        if (!__IOFileCopy(source, target, overwrite)) { IO.IOErrors.Throw() }
                    }

                    static Move(string source, string target, bool overwrite = false) {
                        if (!__IOFileMove(source, target, overwrite)) { IO.IOErrors.Throw() }
                    }

                    // A TextReader/TextWriter on the file (see there); without append it is overwritten.
                    static OpenText(string path) { return new IO.TextReader(path) }
                    static CreateText(string path) { return new IO.TextWriter(path) }
                    static AppendText(string path) { return new IO.TextWriter(path, true) }

                    static ReadAllBytes(string path) {
                        var stream = new IO.FileStream(path)
                        var data = stream.ReadAll()
                        stream.Close()
                        return data
                    }

                    // Creates the file or overwrites it.
                    static WriteAllBytes(string path, buffer) {
                        var stream = new IO.FileStream(path, IO.FileMode.Create)
                        stream.Write(buffer)
                        stream.Close()
                    }

                    static AppendAllBytes(string path, buffer) {
                        var stream = new IO.FileStream(path, IO.FileMode.Append)
                        stream.Write(buffer)
                        stream.Close()
                    }

                    static string ReadAllText(string path) {
                        return IO.Utf8.GetString(IO.File.ReadAllBytes(path))
                    }

                    static WriteAllText(string path, string text) {
                        IO.File.WriteAllBytes(path, IO.Utf8.GetBytes(text))
                    }

                    static AppendAllText(string path, string text) {
                        IO.File.AppendAllBytes(path, IO.Utf8.GetBytes(text))
                    }

                    // All lines as a List of strings (`foreach (line in ...)`, `.count`, `[i]`).
                    static ReadAllLines(string path) {
                        return new List(__IOSplitLines(IO.File.ReadAllText(path)))
                    }

                    // Each line of a List (or an array), each terminated with "\n".
                    static WriteAllLines(string path, lines) {
                        var stream = new IO.FileStream(path, IO.FileMode.Create)
                        if (lines is of List) {
                            foreach (line in lines) {
                                stream.Write(IO.Utf8.GetBytes(line + "\n"))
                            }
                        } else {
                            for (var i = 0; i < lines.length; i++) {
                                stream.Write(IO.Utf8.GetBytes(lines[i] + "\n"))
                            }
                        }
                        stream.Close()
                    }
                }

                class Directory {
                    static bool Exists(string path) {
                        var r = __IODirExists(path)
                        if (r < 0) { IO.IOErrors.Throw() }
                        return r == 1
                    }

                    // Also creates missing intermediate directories; an existing one is not an error.
                    static Create(string path) {
                        if (!__IODirCreate(path)) { IO.IOErrors.Throw() }
                    }

                    // A non-empty directory only with recursive = true.
                    static Delete(string path, bool recursive = false) {
                        if (!__IODirDelete(path, recursive)) { IO.IOErrors.Throw() }
                    }

                    // Full paths of the files as a sorted List. pattern: e.g. "*.txt".
                    static GetFiles(string path, string pattern = "*", bool recursive = false) {
                        var list = __IODirList(path, pattern, recursive, 0)
                        if (list == undefined) { IO.IOErrors.Throw() }
                        return new List(list)
                    }

                    // Full paths of the subdirectories as a sorted List.
                    static GetDirectories(string path, string pattern = "*", bool recursive = false) {
                        var list = __IODirList(path, pattern, recursive, 1)
                        if (list == undefined) { IO.IOErrors.Throw() }
                        return new List(list)
                    }

                    // The current working directory.
                    static string Current() { return __IOCurrentDir() }
                }

                // Reads UTF-8 text line by line from a stream. `new IO.TextReader(path)`
                // opens the file itself; `new IO.TextReader(stream)` reads from an
                // existing stream and CLOSES it too, unless leaveOpen is true.
                // Lines end with "\n" or "\r\n" (the "\r" does not belong to the line).
                //
                //     var reader = new IO.TextReader("notes.txt")
                //     foreach (line in reader) { print(line) }
                //
                // ReadLine() returns undefined at the end of the stream.
                class TextReader {
                    var source
                    bool ownsSource
                    var chunk
                    int pos
                    int len
                    bool eof
                    bool closed
                    var pending

                    construct(source, bool leaveOpen = false) {
                        // Occupy all fields first: if opening below fails (file not
                        // found), destruct() cleans up an otherwise half-built object.
                        this.source = undefined
                        this.ownsSource = false
                        this.closed = false
                        this.eof = false
                        this.pos = 0
                        this.len = 0
                        this.chunk = new byte[4096]
                        this.pending = new IO.MemoryStream()
                        if (source is of string) {
                            this.source = new IO.FileStream(source)
                            this.ownsSource = true
                        } else {
                            this.source = source
                            this.ownsSource = !leaveOpen
                            try source.TakeTo(this)   // (a stream that is only the result of a call passed on - `new IO.TextReader(IO.File.Open(...))` - belongs to the reader; one that belongs to somebody else stays)
                        }
                    }

                    destruct() { try { this.Close() } catch (IO.IOException e) { } }

                    bool IsClosed { get { return this.closed } }

                    // true if no further character comes (reads ahead for that if necessary).
                    bool EndOfStream {
                        get {
                            this.Check()
                            return !this.Fill() && this.pending.Length == 0
                        }
                    }

                    Check() {
                        if (this.closed) { throw new StreamClosedException("The reader is closed.") }
                    }

                    // Ensures that chunk[pos..len) contains data; false at the end of the stream.
                    Fill() {
                        if (this.pos < this.len) { return true }
                        if (this.eof) { return false }
                        var n = this.source.Read(this.chunk, 0, 4096)
                        if (n <= 0) {
                            this.eof = true
                            this.pos = 0
                            this.len = 0
                            return false
                        }
                        this.pos = 0
                        this.len = n
                        return true
                    }

                    // The collected bytes as a line (without a trailing "\r"), empty the collector.
                    TakeLine() {
                        var bytes = this.pending.ToBuffer()
                        this.pending.Length = 0
                        var count = bytes.length
                        if (count > 0 && bytes[count - 1] == 13) { count = count - 1 }
                        return IO.Utf8.GetString(bytes, 0, count)
                    }

                    // The next line, or undefined at the end.
                    ReadLine() {
                        this.Check()
                        while (this.Fill()) {
                            var nl = __IOBufferIndexOf(this.chunk, this.pos, this.len - this.pos, 10)
                            if (nl >= 0) {
                                if (this.pending.Length == 0) {
                                    // Whole line in the buffer - decode directly.
                                    var count = nl - this.pos
                                    if (count > 0 && this.chunk[nl - 1] == 13) { count = count - 1 }
                                    var text = IO.Utf8.GetString(this.chunk, this.pos, count)
                                    this.pos = nl + 1
                                    return text
                                }
                                this.pending.Write(this.chunk, this.pos, nl - this.pos)
                                this.pos = nl + 1
                                return this.TakeLine()
                            }
                            this.pending.Write(this.chunk, this.pos, this.len - this.pos)
                            this.pos = this.len
                        }
                        if (this.pending.Length == 0) { return undefined }
                        return this.TakeLine()
                    }

                    // The entire rest as one string.
                    string ReadAll() {
                        this.Check()
                        while (this.Fill()) {
                            this.pending.Write(this.chunk, this.pos, this.len - this.pos)
                            this.pos = this.len
                        }
                        var bytes = this.pending.ToBuffer()
                        this.pending.Length = 0
                        return IO.Utf8.GetString(bytes)
                    }

                    // All remaining lines as a List.
                    ReadLines() {
                        var lines = new List()
                        var line = this.ReadLine()
                        while (line != undefined) {
                            lines.Add(line)
                            line = this.ReadLine()
                        }
                        return lines
                    }

                    // `foreach (line in reader)` reads line by line.
                    GetEnumerator() { return new IO.LineEnumerator(this) }

                    Close() {
                        if (this.closed == undefined || this.closed) { return }
                        this.closed = true
                        if (this.ownsSource) { this.source.Close() }
                        if (this.pending != undefined) { this.pending.Close() }
                    }
                }

                class LineEnumerator {
                    var reader
                    var line

                    construct(reader) {
                        this.reader = reader
                        this.line = undefined
                    }

                    MoveNext() {
                        this.line = this.reader.ReadLine()
                        return this.line != undefined
                    }

                    GetCurrent() { return this.line }
                }

                // Writes UTF-8 text (without byte-order mark) to a stream. `new
                // IO.TextWriter(path[, append])` opens the file itself (overwrites,
                // with append = true it appends); `new IO.TextWriter(stream[, leaveOpen])`
                // writes to an existing stream and CLOSES it too, unless
                // leaveOpen is true. Lines end with "\n".
                class TextWriter {
                    var target
                    bool ownsTarget
                    bool closed

                    construct(dest, bool flag = false) {
                        // Occupy all fields first (see TextReader).
                        this.target = undefined
                        this.ownsTarget = false
                        this.closed = false
                        if (dest is of string) {
                            var mode = IO.FileMode.Create
                            if (flag) { mode = IO.FileMode.Append }
                            this.target = new IO.FileStream(dest, mode)
                            this.ownsTarget = true
                        } else {
                            this.target = dest
                            this.ownsTarget = !flag
                            try dest.TakeTo(this)   // (see TextReader)
                        }
                    }

                    destruct() { try { this.Close() } catch (IO.IOException e) { } }

                    bool IsClosed { get { return this.closed } }

                    Check() {
                        if (this.closed) { throw new StreamClosedException("The writer is closed.") }
                    }

                    // Writes the value as text (numbers etc. are converted).
                    Write(value) {
                        this.Check()
                        this.target.Write(IO.Utf8.GetBytes("" + value))
                    }

                    // Like Write, additionally appends a line break.
                    WriteLine(value = "") {
                        this.Check()
                        this.target.Write(IO.Utf8.GetBytes("" + value + "\n"))
                    }

                    Flush() {
                        this.Check()
                        this.target.Flush()
                    }

                    Close() {
                        if (this.closed == undefined || this.closed) { return }
                        this.closed = true
                        if (this.target == undefined) { return }
                        this.target.Flush()
                        if (this.ownsTarget) { this.target.Close() }
                    }
                }

                // Standard input/output/error as a stream (In() readable only, Out()/Err()
                // writable only). Where they lead is decided by the host (console, in the
                // editor the output window). Close() changes nothing - the streams belong
                // to the host and stay open.
                class StdStream : NativeStream {
                    construct(int kind) : base(-1) {
                        this.handle = IO.IOErrors.Handle(__IOStdHandle(kind))
                    }

                    Close() { }
                }

                // Convenient access to standard input/output/error:
                //
                //     IO.Stdio.WriteLine("Hello")
                //     var name = IO.Stdio.ReadLine()          // undefined at the end of the input
                //     var out = new IO.TextWriter(IO.Stdio.Out(), true)
                //
                // Output always goes out as UTF-8. ReadLine/ReadAll read buffered - do not mix with
                // raw reads on In(). An incomplete output line
                // appears when the line break comes in the editor or Flush() is
                // called.
                class Stdio {
                    static In() { return new IO.StdStream(0) }
                    static Out() { return new IO.StdStream(1) }
                    static Err() { return new IO.StdStream(2) }

                    // Writes the value as text to standard output.
                    static Write(value) {
                        if (!__IOStdWrite(1, "" + value)) { IO.IOErrors.Throw() }
                    }

                    static WriteLine(value = "") {
                        if (!__IOStdWrite(1, "" + value + "\n")) { IO.IOErrors.Throw() }
                    }

                    // The same to standard error.
                    static ErrorWrite(value) {
                        if (!__IOStdWrite(2, "" + value)) { IO.IOErrors.Throw() }
                    }

                    static ErrorLine(value = "") {
                        if (!__IOStdWrite(2, "" + value + "\n")) { IO.IOErrors.Throw() }
                    }

                    // Outputs buffered output (also an incomplete line).
                    static Flush() {
                        __IOStdFlush(1)
                        __IOStdFlush(2)
                    }

                    // The next line of standard input, or undefined at the end.
                    static ReadLine() {
                        var line = __IOStdReadLine()
                        if (line == undefined && __IOLastError() != 0) { IO.IOErrors.Throw() }
                        return line
                    }

                    // Everything still coming on standard input, as one string.
                    static string ReadAll() {
                        var text = __IOStdReadAll()
                        if (text == undefined) { IO.IOErrors.Throw() }
                        return text
                    }
                }
            }
            """;
    }
}
