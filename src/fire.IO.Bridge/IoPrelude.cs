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
        /// bleibt also nicht offen.
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
                        get { throw new IOException("Dieser Stream kennt keine Position.", 8) }
                        set { throw new IOException("Dieser Stream unterstützt kein Positionieren.", 8) }
                    }

                    int Length {
                        get { throw new IOException("Dieser Stream kennt seine Länge nicht.", 8) }
                        set { throw new IOException("Die Länge dieses Streams lässt sich nicht ändern.", 8) }
                    }

                    int Read(buffer, offset, count) {
                        throw new IOException("Dieser Stream ist nicht lesbar.", 8)
                    }

                    int Write(buffer, offset, count) {
                        throw new IOException("Dieser Stream ist nicht beschreibbar.", 8)
                    }

                    int Seek(int offset, int origin = IO.SeekOrigin.Begin) {
                        throw new IOException("Dieser Stream unterstützt kein Positionieren.", 8)
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
                    destruct() { this.Close() }

                    bool IsClosed { get { return this.closed } }

                    bool CanRead { get { return !this.closed && __IOCanRead(this.handle) } }
                    bool CanWrite { get { return !this.closed && __IOCanWrite(this.handle) } }
                    bool CanSeek { get { return !this.closed && __IOCanSeek(this.handle) } }

                    // Wirft, wenn der Stream schon geschlossen ist.
                    Check() {
                        if (this.closed) { throw new StreamClosedException("Der Stream ist geschlossen.") }
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

                    // Alle Zeilen als Array von Strings.
                    static ReadAllLines(string path) {
                        return __IOSplitLines(IO.File.ReadAllText(path))
                    }

                    // Jede Zeile des Arrays, jeweils mit "\n" abgeschlossen.
                    static WriteAllLines(string path, lines) {
                        var stream = new IO.FileStream(path, IO.FileMode.Create)
                        for (var i = 0; i < lines.length; i++) {
                            stream.Write(IO.Utf8.GetBytes(lines[i] + "\n"))
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

                    // Vollständige Pfade der Dateien, sortiert. pattern: z.B. "*.txt".
                    static GetFiles(string path, string pattern = "*", bool recursive = false) {
                        var list = __IODirList(path, pattern, recursive, 0)
                        if (list == undefined) { IO.IOErrors.Throw() }
                        return list
                    }

                    // Vollständige Pfade der Unterverzeichnisse, sortiert.
                    static GetDirectories(string path, string pattern = "*", bool recursive = false) {
                        var list = __IODirList(path, pattern, recursive, 1)
                        if (list == undefined) { IO.IOErrors.Throw() }
                        return list
                    }

                    // Das aktuelle Arbeitsverzeichnis.
                    static string Current() { return __IOCurrentDir() }
                }
            }
            """;
    }
}
