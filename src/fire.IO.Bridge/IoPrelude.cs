namespace fire.IO.Bridge
{
    public static partial class IoBridge
    {
        /// <summary>
        /// Der fire-Quelltext zur Brücke (analog zu GraphicsBridge.PreludeSource/
        /// DeviceBridge.PreludeSource) - VOR das eigentliche Nutzer-Skript zu
        /// setzen, wenn es `#import "io"` gibt. Alles liegt in `namespace IO`
        /// (`IO.FileStream`, `IO.File`, `IO.Path`, ...), damit kein Nutzer-
        /// Klassenname wie `File` oder `Stream` damit kollidiert.
        ///
        /// Aufbau: `IStream` ist die kleinste Schnittstelle (Read/Write/Flush/
        /// Close) - wer einen EIGENEN Stream schreibt, implementiert sie oder
        /// (bequemer) leitet von `Stream` ab, der alles Übrige (ReadByte,
        /// ReadBytes, ReadAll, CopyTo, ...) auf Read/Write aufbaut. `NativeStream`
        /// ist die gemeinsame Basis von `FileStream`/`MemoryStream`, hinter der
        /// ein natives Handle liegt (siehe IoBridge); sein `destruct()` schließt
        /// das Handle, wenn der Besitzer-Scope endet - ein vergessenes Close()
        /// bleibt also nicht offen. Ein Destruktor wirft nie: schlägt das Schließen
        /// fehl (z.B. weil der Stream, in den ein TextWriter noch leeren will,
        /// schon von jemand anderem geschlossen wurde), wird der IO-Fehler dort
        /// verschluckt.
        ///
        /// Konventionen: Lesefunktionen liefern die Anzahl gelesener Bytes
        /// (0 = Ende des Streams), `ReadByte` liefert -1 am Ende. Fehler sind
        /// fangbare Exceptions (`IO.IOException` und Ableitungen, siehe
        /// IOErrors.Throw). Enum-Werte werden vollqualifiziert geschrieben
        /// (`IO.FileMode.Create`) - ein Enum in einem Namespace ist nur so
        /// erreichbar.
        /// </summary>
        public const string PreludeSource = """
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

                // Das Betriebssystem (5) ODER die Richtlinie des Hosts (6) verweigert den Zugriff.
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
                    // Wirft die zum letzten Fehler einer nativen IO-Funktion passende Exception.
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

                    // Gibt ein gültiges Handle zurück, wirft bei -1 den Fehler.
                    static int Handle(int handle) {
                        if (handle < 0) { Throw() }
                        return handle
                    }

                    // Der Standard-FileAccess zu einem FileMode, wenn keiner angegeben ist (-1).
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

                // Basisklasse für eigene Streams: Read/Write (und was sonst
                // unterstützt wird) überschreiben, der Rest baut darauf auf.
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

                    // Ein Byte (0..255), oder -1 am Ende des Streams.
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

                    // Bis zu `count` Bytes als neuer Puffer (kürzer, wenn der Stream vorher endet).
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

                    // Alles bis zum Ende des Streams als neuer Puffer.
                    ReadAll() {
                        var all = new MemoryStream()
                        this.CopyTo(all)
                        var result = all.ToBuffer()
                        all.Close()
                        return result
                    }

                    // Kopiert alles bis zum Ende dieses Streams in `target`.
                    CopyTo(target, int bufferSize = 4096) {
                        var chunk = new byte[bufferSize]
                        while (true) {
                            var n = this.Read(chunk, 0, bufferSize)
                            if (n <= 0) { break }
                            target.Write(chunk, 0, n)
                        }
                    }
                }

                // Gemeinsame Basis von FileStream und MemoryStream: hinter ihnen liegt
                // ein natives Handle. destruct() schließt es, wenn der Besitzer endet.
                class NativeStream : Stream {
                    int handle
                    bool closed

                    construct(int handle) {
                        this.handle = handle
                        this.closed = false
                    }

                    // Die Unterklassen übergeben erst -1 und öffnen danach im eigenen
                    // Konstruktor-Body: scheitert das Öffnen (z.B. Datei nicht gefunden),
                    // ist dieses Objekt trotzdem vollständig aufgebaut - sein destruct()
                    // findet dann handle == -1 und schließt nichts. (Scheitert ein
                    // Konstruktor schon BEVOR der der Basisklasse lief, haben die Felder
                    // nur den Standardwert `false` - darauf ist kein Verlass.)
                    destruct() { try { this.Close() } catch (IO.IOException e) { } }

                    bool IsClosed { get { return this.closed } }

                    bool CanRead { get { return !this.closed && __IOCanRead(this.handle) } }
                    bool CanWrite { get { return !this.closed && __IOCanWrite(this.handle) } }
                    bool CanSeek { get { return !this.closed && __IOCanSeek(this.handle) } }

                    // Wirft, wenn der Stream schon geschlossen ist.
                    Check() {
                        if (this.closed) { throw new StreamClosedException("The stream is closed.") }
                    }

                    // Schließt das Handle; ein weiteres Close() ist wirkungslos.
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

                    // Neue Position (vom Anfang), `origin`: IO.SeekOrigin.
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

                // Eine Datei. `mode`: IO.FileMode, `access`: IO.FileAccess (ohne Angabe
                // je nach mode: Open -> Read, Append -> Write, sonst ReadWrite).
                class FileStream : NativeStream {
                    string name

                    construct(string path, int mode = IO.FileMode.Open, int access = -1) : base(-1) {
                        this.name = path
                        this.handle = IO.IOErrors.Handle(__IOFileOpen(path, mode, IO.IOErrors.AccessFor(mode, access)))
                    }

                    // Der Pfad, wie beim Öffnen angegeben.
                    string Name { get { return this.name } }
                }

                // Ein Stream im Arbeitsspeicher (wächst beim Schreiben). Mit einem
                // Puffer: beginnt mit einer KOPIE seines Inhalts, Position 0.
                class MemoryStream : NativeStream {
                    construct() : base(-1) {
                        this.handle = IO.IOErrors.Handle(__IOMemNew())
                    }

                    construct(buffer) : base(-1) {
                        this.handle = IO.IOErrors.Handle(__IOMemFromBuffer(buffer))
                    }

                    // Der gesamte Inhalt (unabhängig von der Position) als neuer Puffer.
                    ToBuffer() {
                        this.Check()
                        var all = __IOMemToBuffer(this.handle)
                        if (all == undefined) { IO.IOErrors.Throw() }
                        return all
                    }
                }

                // UTF-8 <-> Puffer. (`string.ToBytes()` ist nur ASCII.)
                class Utf8 {
                    // Der Text als UTF-8 (ohne Byte-Order-Mark) in einem neuen Puffer.
                    static GetBytes(string text) { return __IOUtf8Encode(text) }

                    // Ein Byte-Order-Mark am Anfang wird entfernt, ungültige Folgen
                    // werden zu U+FFFD. Ohne count: bis zum Ende des Puffers.
                    static string GetString(buffer, int offset = 0, int count = -1) {
                        if (count == -1) { count = buffer.length - offset }
                        var text = __IOUtf8Decode(buffer, offset, count)
                        if (text == undefined) { IO.IOErrors.Throw() }
                        return text
                    }
                }

                // Reine Textverarbeitung auf Pfaden (kein Dateizugriff). Wie überall in
                // dieser Sprache mit dem Namespace: IO.Path.Combine(...).
                class Path {
                    // Das Zeichen, das Verzeichnisse trennt ("/" oder "\").
                    static string Separator() { return __IOPathSeparator() }

                    // Fügt Pfadteile zusammen; ein absoluter Teil verwirft alles davor
                    // (wie Path.Combine in .NET).
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

                    // "dir/name.txt" -> ".txt" ("" ohne Endung)
                    static string Extension(string path) { return IO.Path.Text(__IOPathExtension(path)) }

                    // "dir/sub/name.txt" -> "dir/sub" ("" bei einem reinen Namen)
                    static string Parent(string path) { return IO.Path.Text(__IOPathParent(path)) }

                    // Der absolute, normalisierte Pfad (ohne "..").
                    static string FullPath(string path) { return IO.Path.Text(__IOPathFull(path)) }

                    // Das Verzeichnis für temporäre Dateien.
                    static string Temp() { return IO.Path.Text(__IOPathTemp()) }

                    static bool IsRooted(string path) { return __IOPathIsRooted(path) }

                    static string Text(value) {
                        if (value == undefined) { IO.IOErrors.Throw() }
                        return value
                    }
                }

                // Dateien als Ganzes. Jeder Zugriff geht durch die Richtlinie des Hosts;
                // Fehler sind IO.IOException und Ableitungen. Text ist UTF-8, Zeilen
                // werden mit "\n" geschrieben und mit \n, \r\n oder \r gelesen.
                class File {
                    static bool Exists(string path) {
                        var r = __IOFileExists(path)
                        if (r < 0) { IO.IOErrors.Throw() }
                        return r == 1
                    }

                    // Größe in Bytes.
                    static int Size(string path) {
                        var n = __IOFileSize(path)
                        if (n < 0) { IO.IOErrors.Throw() }
                        return n
                    }

                    // Zeitpunkt der letzten Änderung: Sekunden seit 1970 (UTC), mit der Einheit s.
                    static ModifiedTime(string path) {
                        var t = __IOFileTime(path)
                        if (t == undefined) { IO.IOErrors.Throw() }
                        return t
                    }

                    // Eine fehlende Datei ist kein Fehler.
                    static Delete(string path) {
                        if (!__IOFileDelete(path)) { IO.IOErrors.Throw() }
                    }

                    static Copy(string source, string target, bool overwrite = false) {
                        if (!__IOFileCopy(source, target, overwrite)) { IO.IOErrors.Throw() }
                    }

                    static Move(string source, string target, bool overwrite = false) {
                        if (!__IOFileMove(source, target, overwrite)) { IO.IOErrors.Throw() }
                    }

                    // Ein TextReader/TextWriter auf der Datei (siehe dort); ohne append wird überschrieben.
                    static OpenText(string path) { return new IO.TextReader(path) }
                    static CreateText(string path) { return new IO.TextWriter(path) }
                    static AppendText(string path) { return new IO.TextWriter(path, true) }

                    static ReadAllBytes(string path) {
                        var stream = new IO.FileStream(path)
                        var data = stream.ReadAll()
                        stream.Close()
                        return data
                    }

                    // Legt die Datei an bzw. überschreibt sie.
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

                    // Alle Zeilen als List von Strings (`foreach (zeile in ...)`, `.count`, `[i]`).
                    static ReadAllLines(string path) {
                        return new List(__IOSplitLines(IO.File.ReadAllText(path)))
                    }

                    // Jede Zeile einer List (oder eines Arrays), jeweils mit "\n" abgeschlossen.
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

                    // Legt auch fehlende Zwischenverzeichnisse an; ein vorhandenes ist kein Fehler.
                    static Create(string path) {
                        if (!__IODirCreate(path)) { IO.IOErrors.Throw() }
                    }

                    // Ein nicht leeres Verzeichnis nur mit recursive = true.
                    static Delete(string path, bool recursive = false) {
                        if (!__IODirDelete(path, recursive)) { IO.IOErrors.Throw() }
                    }

                    // Vollständige Pfade der Dateien als sortierte List. pattern: z.B. "*.txt".
                    static GetFiles(string path, string pattern = "*", bool recursive = false) {
                        var list = __IODirList(path, pattern, recursive, 0)
                        if (list == undefined) { IO.IOErrors.Throw() }
                        return new List(list)
                    }

                    // Vollständige Pfade der Unterverzeichnisse als sortierte List.
                    static GetDirectories(string path, string pattern = "*", bool recursive = false) {
                        var list = __IODirList(path, pattern, recursive, 1)
                        if (list == undefined) { IO.IOErrors.Throw() }
                        return new List(list)
                    }

                    // Das aktuelle Arbeitsverzeichnis.
                    static string Current() { return __IOCurrentDir() }
                }

                // Liest UTF-8-Text zeilenweise von einem Stream. `new IO.TextReader(pfad)`
                // öffnet die Datei selbst; `new IO.TextReader(stream)` liest von einem
                // vorhandenen Stream und SCHLIESST ihn mit, außer leaveOpen ist true.
                // Zeilen enden mit "\n" oder "\r\n" (das "\r" gehört nicht zur Zeile).
                //
                //     var reader = new IO.TextReader("notizen.txt")
                //     foreach (zeile in reader) { print(zeile) }
                //
                // ReadLine() liefert undefined am Ende des Streams.
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
                        // Alle Felder zuerst belegen: scheitert das Öffnen unten (Datei nicht
                        // gefunden), räumt destruct() ein sonst halb aufgebautes Objekt auf.
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
                        }
                    }

                    destruct() { try { this.Close() } catch (IO.IOException e) { } }

                    bool IsClosed { get { return this.closed } }

                    // true, wenn kein weiteres Zeichen mehr kommt (liest dafür ggf. vor).
                    bool EndOfStream {
                        get {
                            this.Check()
                            return !this.Fill() && this.pending.Length == 0
                        }
                    }

                    Check() {
                        if (this.closed) { throw new StreamClosedException("The reader is closed.") }
                    }

                    // Sorgt dafür, dass chunk[pos..len) Daten enthält; false am Ende des Streams.
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

                    // Die gesammelten Bytes als Zeile (ohne abschließendes "\r"), Sammler leeren.
                    TakeLine() {
                        var bytes = this.pending.ToBuffer()
                        this.pending.Length = 0
                        var count = bytes.length
                        if (count > 0 && bytes[count - 1] == 13) { count = count - 1 }
                        return IO.Utf8.GetString(bytes, 0, count)
                    }

                    // Die nächste Zeile, oder undefined am Ende.
                    ReadLine() {
                        this.Check()
                        while (this.Fill()) {
                            var nl = __IOBufferIndexOf(this.chunk, this.pos, this.len - this.pos, 10)
                            if (nl >= 0) {
                                if (this.pending.Length == 0) {
                                    // Ganze Zeile im Puffer - direkt dekodieren.
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

                    // Der gesamte Rest als ein String.
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

                    // Alle übrigen Zeilen als List.
                    ReadLines() {
                        var lines = new List()
                        var line = this.ReadLine()
                        while (line != undefined) {
                            lines.Add(line)
                            line = this.ReadLine()
                        }
                        return lines
                    }

                    // `foreach (zeile in reader)` liest Zeile für Zeile.
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

                // Schreibt UTF-8-Text (ohne Byte-Order-Mark) in einen Stream. `new
                // IO.TextWriter(pfad[, append])` öffnet die Datei selbst (überschreibt,
                // mit append = true hängt an); `new IO.TextWriter(stream[, leaveOpen])`
                // schreibt in einen vorhandenen Stream und SCHLIESST ihn mit, außer
                // leaveOpen ist true. Zeilen enden mit "\n".
                class TextWriter {
                    var target
                    bool ownsTarget
                    bool closed

                    construct(dest, bool flag = false) {
                        // Alle Felder zuerst belegen (siehe TextReader).
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
                        }
                    }

                    destruct() { try { this.Close() } catch (IO.IOException e) { } }

                    bool IsClosed { get { return this.closed } }

                    Check() {
                        if (this.closed) { throw new StreamClosedException("The writer is closed.") }
                    }

                    // Schreibt den Wert als Text (Zahlen usw. werden umgewandelt).
                    Write(value) {
                        this.Check()
                        this.target.Write(IO.Utf8.GetBytes("" + value))
                    }

                    // Wie Write, hängt zusätzlich einen Zeilenumbruch an.
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

                // Standardeingabe/-ausgabe/-fehler als Stream (In() nur lesbar, Out()/Err()
                // nur schreibbar). Wohin sie führen, entscheidet der Host (Konsole, im
                // Editor das Ausgabefenster). Close() ändert nichts - die Streams gehören
                // dem Host und bleiben offen.
                class StdStream : NativeStream {
                    construct(int kind) : base(-1) {
                        this.handle = IO.IOErrors.Handle(__IOStdHandle(kind))
                    }

                    Close() { }
                }

                // Bequemer Zugriff auf Standardeingabe/-ausgabe/-fehler:
                //
                //     IO.Stdio.WriteLine("Hallo")
                //     var name = IO.Stdio.ReadLine()          // undefined am Ende der Eingabe
                //     var out = new IO.TextWriter(IO.Stdio.Out(), true)
                //
                // Ausgabe geht immer als UTF-8. ReadLine/ReadAll lesen gepuffert - nicht mit
                // rohen Lesezugriffen auf In() mischen. Eine unvollständige Ausgabezeile
                // erscheint, wenn im Editor der Zeilenumbruch kommt oder Flush() aufgerufen
                // wird.
                class Stdio {
                    static In() { return new IO.StdStream(0) }
                    static Out() { return new IO.StdStream(1) }
                    static Err() { return new IO.StdStream(2) }

                    // Schreibt den Wert als Text auf die Standardausgabe.
                    static Write(value) {
                        if (!__IOStdWrite(1, "" + value)) { IO.IOErrors.Throw() }
                    }

                    static WriteLine(value = "") {
                        if (!__IOStdWrite(1, "" + value + "\n")) { IO.IOErrors.Throw() }
                    }

                    // Dasselbe auf den Standardfehler.
                    static ErrorWrite(value) {
                        if (!__IOStdWrite(2, "" + value)) { IO.IOErrors.Throw() }
                    }

                    static ErrorLine(value = "") {
                        if (!__IOStdWrite(2, "" + value + "\n")) { IO.IOErrors.Throw() }
                    }

                    // Gibt gepufferte Ausgabe (auch eine unvollständige Zeile) aus.
                    static Flush() {
                        __IOStdFlush(1)
                        __IOStdFlush(2)
                    }

                    // Die nächste Zeile der Standardeingabe, oder undefined am Ende.
                    static ReadLine() {
                        var line = __IOStdReadLine()
                        if (line == undefined && __IOLastError() != 0) { IO.IOErrors.Throw() }
                        return line
                    }

                    // Alles, was noch an Standardeingabe kommt, als ein String.
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
