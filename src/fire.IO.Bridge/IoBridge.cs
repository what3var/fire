using System.Collections.Concurrent;
using fire.Bytecode;
using fire.Values;

namespace fire.IO.Bridge
{
    /// <summary>Fehlercodes der nativen IO-Funktionen (`__IOLastError()`) - die
    /// Zahlenwerte sind Teil der Schnittstelle zum fire-Prelude
    /// (IoBridge.PreludeSource, `IOErrors.Throw`) und dort dieselben.</summary>
    public enum IoError
    {
        None = 0,
        InvalidArgument = 1,
        InvalidHandle = 2,
        FileNotFound = 3,
        DirectoryNotFound = 4,
        Permission = 5,        // das Betriebssystem verweigert den Zugriff
        Denied = 6,            // die IoPolicy des Hosts verweigert den Zugriff
        AlreadyExists = 7,
        NotSupported = 8,      // z.B. Schreiben auf einen Lese-Stream
        Other = 9,
    }

    /// <summary>
    /// Die Brücke zwischen fire und System.IO - registriert Streams als native
    /// Funktionen (Präfix `__IO`, über NativeRegistry.RegisterGroup) und liefert
    /// dazu passenden fire-Quelltext (<see cref="PreludeSource"/>,
    /// `namespace IO`). Dasselbe Muster wie Terminal.Bridge.GraphicsBridge/
    /// Device.Bridge.DeviceBridge: fire sieht nur eine Klasse mit einem
    /// `int handle`, dahinter liegt hier ein .NET-Stream in einer
    /// nebenläufigkeitssicheren Tabelle.
    ///
    /// Fehler: jede Funktion liefert bei einem Fehler `-1` (bzw. `false`/
    /// `undefined`, siehe jeweils) und merkt sich Code und Meldung pro THREAD
    /// (`__IOLastError()`/`__IOLastErrorMessage()`) - der Prelude wirft daraus
    /// eine typisierte, fangbare Exception. Native Funktionen selbst werfen
    /// bei einem Fehler des Betriebssystems nie.
    ///
    /// Sicherheit: Pfade werden vor dem Öffnen über die IoPolicy des Hosts
    /// geprüft (siehe dort).
    /// </summary>
    public static partial class IoBridge
    {
        public const string Prefix = "__IO";

        /// <summary>Ungültiges Handle / Fehler - siehe GraphicsBridge.InvalidHandle
        /// für dieselbe Konvention.</summary>
        public const int Failed = -1;

        /// <summary>Rückgabe von `__IOReadByte` am Ende des Streams (ein Fehler
        /// ist dort `-1`).</summary>
        public const int EndOfStream = -2;

        /// <summary>Nur die NAMEN der Funktionen (ohne Wirkung) - reicht fürs
        /// Kompilieren, siehe ImportedPreludes.</summary>
        public static void RegisterStubs(NativeRegistry natives)
        {
            var stubs = new Dictionary<string, NativeFunction>();
            foreach (var name in new IoHost(IoPolicy.DenyAll, IoStdio.SystemConsole).BuildFunctions().Keys)
                stubs[name] = args => Value.MakeUndefined();
            natives.RegisterGroup(Prefix, stubs);
        }

        /// <summary>Die echten Funktionen. `policy`: was Skripte anfassen dürfen
        /// (Vorgabe: alles, siehe IoPolicy.AllowAll). Jeder Aufruf erzeugt eine
        /// EIGENE Handle-Tabelle - Sessions teilen sich keine offenen Streams.
        ///
        /// Der Rückgabewert schließt beim `Dispose()` alle noch offenen Streams dieser Registrierung (ausgenommen die
        /// Standardstreams des Hosts) - das Sicherheitsnetz des Hosts für Streams, die ein Skript nie geschlossen hat und
        /// deren Destruktor nicht (mehr) lief. Normalerweise schließt der Destruktor von `IO.FileStream` & Co. sie schon.</summary>
        public static IDisposable RegisterAll(NativeRegistry natives, IoPolicy? policy = null, IoStdio? stdio = null)
        {
            var host = new IoHost(policy ?? IoPolicy.AllowAll, stdio ?? IoStdio.SystemConsole);
            natives.RegisterGroup(Prefix, host.BuildFunctions());
            return host;
        }

        /// <summary>Die offenen Streams EINER Registrierung - für Tests/Diagnose
        /// (fire: `__IOOpenCount()`).</summary>
        internal sealed partial class IoHost : IDisposable
        {
            /// <summary>Schließt alle noch offenen, nicht-permanenten Streams (siehe RegisterAll).</summary>
            public void Dispose()
            {
                foreach (var handle in _streams.Keys.ToArray())
                {
                    if (!_streams.TryGetValue(handle, out var entry) || entry.Permanent) continue;
                    if (!_streams.TryRemove(handle, out entry)) continue;
                    try { lock (entry.Lock) entry.Stream.Dispose(); }
                    catch { /* beim Aufräumen ist ein Fehler beim Schließen egal */ }
                }
            }

            private sealed class StreamEntry
            {
                public required Stream Stream { get; init; }
                public readonly object Lock = new();

                /// <summary>Standardein-/-ausgabe/-fehler: gehören dem Host, ein
                /// `Close()` aus dem Skript schließt sie nicht.</summary>
                public bool Permanent { get; init; }
            }

            private readonly IoPolicy _policy;
            private readonly IoStdio _stdio;
            private readonly Stream?[] _stdStreams = new Stream?[3];
            private readonly int[] _stdHandles = new int[3];
            private StreamReader? _stdReader;
            private readonly object _stdLock = new();
            private readonly ConcurrentDictionary<int, StreamEntry> _streams = new();
            private int _nextHandle;

            // Der letzte Fehler dieses THREADS (native Funktionen laufen auf dem
            // VM-Thread des Aufrufers, auch mehrere fire-Threads gleichzeitig).
            [ThreadStatic] private static IoError _lastError;
            [ThreadStatic] private static string? _lastMessage;

            public IoHost(IoPolicy policy, IoStdio stdio)
            {
                _policy = policy;
                _stdio = stdio;
            }

            private static long Fail(IoError error, string message)
            {
                _lastError = error;
                _lastMessage = message;
                return Failed;
            }

            private static void Succeed()
            {
                _lastError = IoError.None;
                _lastMessage = null;
            }

            /// <summary>Übersetzt eine .NET-Exception in Code + Meldung.</summary>
            private static long FailFrom(Exception ex) => ex switch
            {
                FileNotFoundException => Fail(IoError.FileNotFound, ex.Message),
                DirectoryNotFoundException => Fail(IoError.DirectoryNotFound, ex.Message),
                UnauthorizedAccessException => Fail(IoError.Permission, ex.Message),
                ArgumentException or NotSupportedException => Fail(IoError.InvalidArgument, ex.Message),
                ObjectDisposedException => Fail(IoError.InvalidHandle, "The stream is already closed."),
                IOException => Fail(IoError.Other, ex.Message),
                _ => Fail(IoError.Other, ex.Message),
            };

            private int Register(Stream stream)
            {
                int handle = Interlocked.Increment(ref _nextHandle);
                _streams[handle] = new StreamEntry { Stream = stream };
                return handle;
            }

            private bool TryGet(Value handleArg, out StreamEntry entry)
            {
                if (_streams.TryGetValue((int)handleArg.AsInt(), out var found))
                {
                    entry = found;
                    return true;
                }
                Fail(IoError.InvalidHandle, "Invalid or already closed stream handle.");
                entry = null!;
                return false;
            }

            /// <summary>Prüft `offset`/`count` gegen die Länge des Puffers.</summary>
            private static bool CheckRange(ByteBuffer buffer, long offset, long count)
            {
                if (offset < 0 || count < 0 || offset > buffer.Length || count > buffer.Length - offset)
                {
                    Fail(IoError.InvalidArgument, $"offset/count ({offset}/{count}) are outside of the buffer (length {buffer.Length}).");
                    return false;
                }
                return true;
            }

            /// <summary>Führt `action` unter der Sperre des Streams aus und fängt
            /// .NET-Fehler (siehe FailFrom).</summary>
            private long Locked(Value handleArg, Func<Stream, long> action)
            {
                if (!TryGet(handleArg, out var entry)) return Failed;
                try
                {
                    lock (entry.Lock)
                    {
                        long result = action(entry.Stream);
                        if (result != Failed) Succeed();
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    return FailFrom(ex);
                }
            }

            /// <summary>Alle nativen Funktionen: Streams (hier) und Dateisystem/Text
            /// (IoFileSystem.cs).</summary>
            public Dictionary<string, NativeFunction> BuildFunctions()
            {
                var all = BuildStreamFunctions();
                foreach (var (name, function) in BuildFileSystemFunctions())
                    all[name] = function;
                return all;
            }

            private Dictionary<string, NativeFunction> BuildStreamFunctions() => new()
            {
                // ---- Fehler ----
                ["LastError"] = args => Value.MakeInt((long)_lastError),
                ["LastErrorMessage"] = args => Value.MakeString(_lastMessage ?? string.Empty),
                ["OpenCount"] = args => Value.MakeInt(_streams.Values.Count(e => !e.Permanent)),

                // ---- Öffnen ----
                // mode: 0 Open (muss existieren), 1 Create (anlegen/leeren),
                // 2 CreateNew (muss neu sein), 3 OpenOrCreate, 4 Append.
                // access: 0 Read, 1 Write, 2 ReadWrite.
                ["FileOpen"] = args => Value.MakeInt(OpenFile(args[0].AsString(), args[1].AsInt(), args[2].AsInt())),
                ["MemNew"] = args =>
                {
                    Succeed();
                    return Value.MakeInt(Register(new MemoryStream()));
                },
                ["MemFromBuffer"] = args =>
                {
                    Succeed();
                    var ms = new MemoryStream();
                    var source = args[0].AsBuffer();
                    ms.Write(source.Bytes, 0, source.Length);
                    ms.Position = 0;
                    return Value.MakeInt(Register(ms));
                },
                ["Close"] = args =>
                {
                    if (_streams.TryGetValue((int)args[0].AsInt(), out var permanent) && permanent.Permanent)
                    {
                        Succeed(); // Standardstreams gehören dem Host
                        return Value.MakeBool(true);
                    }
                    if (!_streams.TryRemove((int)args[0].AsInt(), out var entry))
                    {
                        Fail(IoError.InvalidHandle, "Invalid or already closed stream handle.");
                        return Value.MakeBool(false);
                    }
                    try
                    {
                        lock (entry.Lock) entry.Stream.Dispose();
                        Succeed();
                        return Value.MakeBool(true);
                    }
                    catch (Exception ex)
                    {
                        FailFrom(ex);
                        return Value.MakeBool(false);
                    }
                },

                // ---- Lesen/Schreiben ----
                ["Read"] = args =>
                {
                    var buffer = args[1].AsBuffer();
                    long offset = args[2].AsInt(), count = args[3].AsInt();
                    if (!CheckRange(buffer, offset, count)) return Value.MakeInt(Failed);
                    return Value.MakeInt(Locked(args[0], s =>
                    {
                        if (!s.CanRead) return Fail(IoError.NotSupported, "The stream is not readable.");
                        return s.Read(buffer.Bytes, (int)offset, (int)count);
                    }));
                },
                ["Write"] = args =>
                {
                    var buffer = args[1].AsBuffer();
                    long offset = args[2].AsInt(), count = args[3].AsInt();
                    if (!CheckRange(buffer, offset, count)) return Value.MakeInt(Failed);
                    return Value.MakeInt(Locked(args[0], s =>
                    {
                        if (!s.CanWrite) return Fail(IoError.NotSupported, "The stream is not writable.");
                        s.Write(buffer.Bytes, (int)offset, (int)count);
                        return count;
                    }));
                },
                ["ReadByte"] = args => Value.MakeInt(Locked(args[0], s =>
                {
                    if (!s.CanRead) return Fail(IoError.NotSupported, "The stream is not readable.");
                    int b = s.ReadByte();
                    return b < 0 ? EndOfStream : b;
                })),
                ["WriteByte"] = args => Value.MakeInt(Locked(args[0], s =>
                {
                    if (!s.CanWrite) return Fail(IoError.NotSupported, "The stream is not writable.");
                    s.WriteByte((byte)(args[1].AsInt() & 0xFF));
                    return 1;
                })),
                // Den Rest des Streams ab der aktuellen Position als neuen Puffer.
                ["ReadRest"] = args =>
                {
                    if (!TryGet(args[0], out var entry)) return Value.MakeUndefined();
                    try
                    {
                        lock (entry.Lock)
                        {
                            if (!entry.Stream.CanRead)
                            {
                                Fail(IoError.NotSupported, "The stream is not readable.");
                                return Value.MakeUndefined();
                            }
                            using var copy = new MemoryStream();
                            entry.Stream.CopyTo(copy);
                            Succeed();
                            return Value.MakeBuffer(new ByteBuffer(copy.ToArray(), ByteConversions.HostByteOrder));
                        }
                    }
                    catch (Exception ex)
                    {
                        FailFrom(ex);
                        return Value.MakeUndefined();
                    }
                },
                ["Flush"] = args => Value.MakeBool(Locked(args[0], s =>
                {
                    s.Flush();
                    return 1;
                }) != Failed),

                // ---- Position ----
                ["Seek"] = args => Value.MakeInt(Locked(args[0], s =>
                {
                    if (!s.CanSeek) return Fail(IoError.NotSupported, "The stream does not support seeking.");
                    long origin = args[2].AsInt();
                    if (origin < 0 || origin > 2) return Fail(IoError.InvalidArgument, $"Invalid seek origin {origin}.");
                    long target = args[1].AsInt();
                    // Vor den Anfang zu springen ist ein Fehler des Aufrufers.
                    long basePos = origin == 0 ? 0 : origin == 1 ? s.Position : s.Length;
                    if (basePos + target < 0) return Fail(IoError.InvalidArgument, "The position would be before the start of the stream.");
                    return s.Seek(target, (SeekOrigin)origin);
                })),
                ["Position"] = args => Value.MakeInt(Locked(args[0], s =>
                    s.CanSeek ? s.Position : Fail(IoError.NotSupported, "The stream does not support a position."))),
                ["Length"] = args => Value.MakeInt(Locked(args[0], s =>
                    s.CanSeek ? s.Length : Fail(IoError.NotSupported, "The stream does not know its length."))),
                ["SetLength"] = args => Value.MakeBool(Locked(args[0], s =>
                {
                    long length = args[1].AsInt();
                    if (length < 0) return Fail(IoError.InvalidArgument, "The length must not be negative.");
                    if (!s.CanSeek || !s.CanWrite) return Fail(IoError.NotSupported, "The length of this stream cannot be changed.");
                    s.SetLength(length);
                    return 1;
                }) != Failed),

                // ---- Fähigkeiten ----
                ["CanRead"] = args => Value.MakeBool(_streams.TryGetValue((int)args[0].AsInt(), out var e) && e.Stream.CanRead),
                ["CanWrite"] = args => Value.MakeBool(_streams.TryGetValue((int)args[0].AsInt(), out var e) && e.Stream.CanWrite),
                ["CanSeek"] = args => Value.MakeBool(_streams.TryGetValue((int)args[0].AsInt(), out var e) && e.Stream.CanSeek),

                // ---- MemoryStream ----
                ["MemToBuffer"] = args =>
                {
                    if (!TryGet(args[0], out var entry)) return Value.MakeUndefined();
                    if (entry.Stream is not MemoryStream ms)
                    {
                        Fail(IoError.NotSupported, "Only a MemoryStream can be read as a buffer.");
                        return Value.MakeUndefined();
                    }
                    lock (entry.Lock)
                    {
                        Succeed();
                        return Value.MakeBuffer(new ByteBuffer(ms.ToArray(), ByteConversions.HostByteOrder));
                    }
                },
            };

            private long OpenFile(string path, long mode, long access)
            {
                try
                {
                    if (mode < 0 || mode > 4) return Fail(IoError.InvalidArgument, $"Invalid FileMode {mode}.");
                    if (access < 0 || access > 2) return Fail(IoError.InvalidArgument, $"Invalid FileAccess {access}.");
                    if (mode == 4 && access != 1) return Fail(IoError.InvalidArgument, "FileMode.Append verlangt FileAccess.Write.");
                    if ((mode == 1 || mode == 2) && access == 0)
                        return Fail(IoError.InvalidArgument, "Creating a file requires write access.");

                    var needed = access == 0 ? IoAccess.Read : access == 1 ? IoAccess.Write : IoAccess.Read | IoAccess.Write;
                    if (!Authorize(path, needed, out string fullPath)) return Failed;

                    if (mode == 2 && File.Exists(fullPath))
                        return Fail(IoError.AlreadyExists, $"The file '{fullPath}' already exists.");

                    var fileMode = mode switch
                    {
                        0 => FileMode.Open,
                        1 => FileMode.Create,
                        2 => FileMode.CreateNew,
                        3 => FileMode.OpenOrCreate,
                        _ => FileMode.Append,
                    };
                    var fileAccess = access switch { 0 => FileAccess.Read, 1 => FileAccess.Write, _ => FileAccess.ReadWrite };
                    var stream = new FileStream(fullPath, fileMode, fileAccess, FileShare.Read);
                    Succeed();
                    return Register(stream);
                }
                catch (Exception ex)
                {
                    return FailFrom(ex);
                }
            }
        }
    }
}
