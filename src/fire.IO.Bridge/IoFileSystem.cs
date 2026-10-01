using System.Text;
using fire.Bytecode;
using fire.Values;

namespace fire.IO.Bridge
{
    public static partial class IoBridge
    {
        internal sealed partial class IoHost
        {
            /// <summary>Normalisiert `path` (`Path.GetFullPath`, also ohne `..`) und
            /// fragt die IoPolicy des Hosts - der EINZIGE Weg, auf dem ein Skript-Pfad
            /// zu einem echten Pfad wird. `false` (Fehler ist gemerkt), wenn der Pfad
            /// ungültig ist oder die Richtlinie ihn ablehnt.</summary>
            private bool Authorize(string path, IoAccess access, out string fullPath)
            {
                fullPath = string.Empty;
                try
                {
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        Fail(IoError.InvalidArgument, "Der Pfad ist leer.");
                        return false;
                    }
                    fullPath = Path.GetFullPath(path);
                }
                catch (Exception ex)
                {
                    FailFrom(ex);
                    return false;
                }

                if (!_policy.IsAllowed(fullPath, access, out var reason))
                {
                    Fail(IoError.Denied, reason ?? $"Zugriff auf '{fullPath}' ist nicht erlaubt.");
                    return false;
                }
                return true;
            }

            /// <summary>Prüft `path` gegen die Richtlinie und führt `action` mit dem
            /// vollständigen Pfad aus; .NET-Fehler werden zu einem gemerkten Fehler
            /// (siehe FailFrom) und `failure` als Ergebnis.</summary>
            private Value OnPath(string path, IoAccess access, Func<string, Value> action, Value failure)
            {
                if (!Authorize(path, access, out var fullPath)) return failure;
                try
                {
                    var result = action(fullPath);
                    if (!IsFailure(result, failure)) Succeed();
                    return result;
                }
                catch (Exception ex)
                {
                    FailFrom(ex);
                    return failure;
                }
            }

            // `action` meldet einen Fehler selbst (Fail(...)) und liefert dann `failure`.
            private static bool IsFailure(Value result, Value failure) =>
                result.Kind == failure.Kind && result.Kind switch
                {
                    ValueKind.Bool => result.AsBool() == failure.AsBool(),
                    ValueKind.Int => result.AsInt() == failure.AsInt(),
                    ValueKind.Undefined => true,
                    _ => false,
                };

            private static Value Bool(bool value) => Value.MakeBool(value);
            private static readonly Value BoolFailure = Value.MakeBool(false);
            private static readonly Value IntFailure = Value.MakeInt(Failed);
            private static readonly Value NoValue = Value.MakeUndefined();

            private static Value StringArray(IEnumerable<string> items)
            {
                var list = items.ToList();
                var array = new ScriptArray(list.Count);
                for (int i = 0; i < list.Count; i++)
                    array.Items[i] = Value.MakeString(list[i]);
                return Value.MakeArray(array);
            }

            private static Value TextResult(Func<string> action)
            {
                try
                {
                    string text = action();
                    Succeed();
                    return Value.MakeString(text);
                }
                catch (Exception ex)
                {
                    FailFrom(ex);
                    return NoValue;
                }
            }

            private Stream GetStdStream(int kind) => _stdStreams[kind] ??= kind switch
            {
                0 => _stdio.OpenInput(),
                1 => _stdio.OpenOutput(),
                _ => _stdio.OpenError(),
            };

            private StreamReader StdReader() => _stdReader ??= new StreamReader(GetStdStream(0), new UTF8Encoding(false));

            private Value StdWrite(long kind, string text, bool flush)
            {
                if (kind != 1 && kind != 2) return BoolFailureWith(IoError.InvalidArgument, $"Ungültiger Ausgabestream {kind}.");
                try
                {
                    lock (_stdLock)
                    {
                        var stream = GetStdStream((int)kind);
                        if (text.Length > 0)
                        {
                            var bytes = new UTF8Encoding(false).GetBytes(text);
                            stream.Write(bytes, 0, bytes.Length);
                        }
                        if (flush) stream.Flush();
                        Succeed();
                        return Bool(true);
                    }
                }
                catch (Exception ex)
                {
                    FailFrom(ex);
                    return BoolFailure;
                }
            }

            private static Value BoolFailureWith(IoError error, string message)
            {
                Fail(error, message);
                return BoolFailure;
            }

            private Dictionary<string, NativeFunction> BuildFileSystemFunctions() => new()
            {
                // ---- Dateien ----
                ["FileExists"] = args => OnPath(args[0].AsString(), IoAccess.Read,
                    full => Value.MakeInt(File.Exists(full) ? 1 : 0), IntFailure),
                ["FileSize"] = args => OnPath(args[0].AsString(), IoAccess.Read,
                    full => Value.MakeInt(new FileInfo(full).Length), IntFailure),
                // Änderungszeit in Sekunden seit 1970 (UTC), mit der Einheit `s`.
                ["FileTime"] = args => OnPath(args[0].AsString(), IoAccess.Read, full =>
                {
                    if (!File.Exists(full)) throw new FileNotFoundException($"Die Datei '{full}' existiert nicht.");
                    var seconds = new DateTimeOffset(File.GetLastWriteTimeUtc(full)).ToUnixTimeSeconds();
                    return Value.MakeInt(seconds, Values.Unit.Parse("s"));
                }, NoValue),
                // Eine fehlende Datei ist kein Fehler (wie File.Delete in .NET).
                ["FileDelete"] = args => OnPath(args[0].AsString(), IoAccess.Delete, full =>
                {
                    File.Delete(full);
                    return Bool(true);
                }, BoolFailure),
                ["FileCopy"] = args =>
                {
                    if (!Authorize(args[1].AsString(), IoAccess.Write, out var target)) return BoolFailure;
                    bool overwrite = args[2].AsBool();
                    return OnPath(args[0].AsString(), IoAccess.Read, source =>
                    {
                        if (!overwrite && File.Exists(target))
                        {
                            Fail(IoError.AlreadyExists, $"Die Zieldatei '{target}' existiert bereits.");
                            return BoolFailure;
                        }
                        File.Copy(source, target, overwrite);
                        return Bool(true);
                    }, BoolFailure);
                },
                ["FileMove"] = args =>
                {
                    if (!Authorize(args[1].AsString(), IoAccess.Write, out var target)) return BoolFailure;
                    bool overwrite = args[2].AsBool();
                    return OnPath(args[0].AsString(), IoAccess.Read | IoAccess.Delete, source =>
                    {
                        if (!overwrite && File.Exists(target))
                        {
                            Fail(IoError.AlreadyExists, $"Die Zieldatei '{target}' existiert bereits.");
                            return BoolFailure;
                        }
                        File.Move(source, target, overwrite);
                        return Bool(true);
                    }, BoolFailure);
                },

                // ---- Verzeichnisse ----
                ["DirExists"] = args => OnPath(args[0].AsString(), IoAccess.Read,
                    full => Value.MakeInt(Directory.Exists(full) ? 1 : 0), IntFailure),
                // Legt auch fehlende Zwischenverzeichnisse an; ein vorhandenes ist kein Fehler.
                ["DirCreate"] = args => OnPath(args[0].AsString(), IoAccess.Write, full =>
                {
                    Directory.CreateDirectory(full);
                    return Bool(true);
                }, BoolFailure),
                ["DirDelete"] = args => OnPath(args[0].AsString(), IoAccess.Delete, full =>
                {
                    Directory.Delete(full, args[1].AsBool());
                    return Bool(true);
                }, BoolFailure),
                // kind: 0 Dateien, 1 Verzeichnisse. Vollständige Pfade, sortiert.
                ["DirList"] = args => OnPath(args[0].AsString(), IoAccess.List, full =>
                {
                    string pattern = args[1].AsString();
                    var option = args[2].AsBool() ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                    var entries = args[3].AsInt() == 0
                        ? Directory.EnumerateFiles(full, pattern, option)
                        : Directory.EnumerateDirectories(full, pattern, option);
                    return StringArray(entries.OrderBy(e => e, StringComparer.Ordinal));
                }, NoValue),
                ["CurrentDir"] = args => TextResult(() => Directory.GetCurrentDirectory()),

                // ---- Pfade (reine Textverarbeitung, ohne Dateizugriff - keine Richtlinie) ----
                ["PathCombine"] = args => TextResult(() => Path.Combine(args[0].AsString(), args[1].AsString())),
                ["PathFileName"] = args => TextResult(() => Path.GetFileName(args[0].AsString())),
                ["PathStem"] = args => TextResult(() => Path.GetFileNameWithoutExtension(args[0].AsString())),
                ["PathExtension"] = args => TextResult(() => Path.GetExtension(args[0].AsString())),
                ["PathParent"] = args => TextResult(() => Path.GetDirectoryName(args[0].AsString()) ?? string.Empty),
                ["PathFull"] = args => TextResult(() => Path.GetFullPath(args[0].AsString())),
                ["PathTemp"] = args => TextResult(Path.GetTempPath),
                ["PathSeparator"] = args => Value.MakeString(Path.DirectorySeparatorChar.ToString()),
                ["PathIsRooted"] = args => Bool(Path.IsPathRooted(args[0].AsString())),

                // ---- Standardein-/-ausgabe (Ziel bestimmt der Host, siehe IoStdio) ----
                // kind: 0 Eingabe, 1 Ausgabe, 2 Fehler. Immer dasselbe Handle je kind.
                ["StdHandle"] = args =>
                {
                    long kind = args[0].AsInt();
                    if (kind < 0 || kind > 2)
                    {
                        Fail(IoError.InvalidArgument, $"Ungültiger Standardstream {kind}.");
                        return IntFailure;
                    }
                    try
                    {
                        lock (_stdLock)
                        {
                            if (_stdHandles[kind] == 0)
                            {
                                int handle = Interlocked.Increment(ref _nextHandle);
                                _streams[handle] = new StreamEntry { Stream = GetStdStream((int)kind), Permanent = true };
                                _stdHandles[kind] = handle;
                            }
                            Succeed();
                            return Value.MakeInt(_stdHandles[kind]);
                        }
                    }
                    catch (Exception ex)
                    {
                        FailFrom(ex);
                        return IntFailure;
                    }
                },
                ["StdWrite"] = args => StdWrite(args[0].AsInt(), args[1].AsString(), flush: false),
                ["StdFlush"] = args => StdWrite(args[0].AsInt(), string.Empty, flush: true),
                // Eine Zeile Standardeingabe (ohne Umbruch), `undefined` am Ende. Liest gepuffert -
                // nicht mit rohen Lesezugriffen auf IO.Stdio.In() mischen.
                ["StdReadLine"] = args =>
                {
                    try
                    {
                        lock (_stdLock)
                        {
                            string? line = StdReader().ReadLine();
                            Succeed();
                            return line == null ? NoValue : Value.MakeString(line);
                        }
                    }
                    catch (Exception ex)
                    {
                        FailFrom(ex);
                        return NoValue;
                    }
                },
                ["StdReadAll"] = args => TextResult(() =>
                {
                    lock (_stdLock) return StdReader().ReadToEnd();
                }),

                // ---- Puffer ----
                // Index (absolut) des ersten Bytes `value` in buffer[offset .. offset+count), sonst -1.
                // Schnelle Suche für Zeilentrenner (TextReader) - ein Byte-für-Byte-Lauf in fire
                // wäre dafür viel zu langsam.
                ["BufferIndexOf"] = args =>
                {
                    var buffer = args[0].AsBuffer();
                    long offset = args[1].AsInt(), count = args[2].AsInt();
                    if (!CheckRange(buffer, offset, count)) return IntFailure;
                    int index = Array.IndexOf(buffer.Bytes, (byte)(args[3].AsInt() & 0xFF), (int)offset, (int)count);
                    Succeed();
                    return Value.MakeInt(index);
                },

                // ---- Text (UTF-8) ----
                ["Utf8Encode"] = args => Value.MakeBuffer(
                    new ByteBuffer(new UTF8Encoding(false).GetBytes(args[0].AsString()), ByteConversions.HostByteOrder)),
                // Ein Byte-Order-Mark am Anfang wird entfernt, ungültige Folgen werden
                // zu U+FFFD (kein Fehler).
                ["Utf8Decode"] = args =>
                {
                    var buffer = args[0].AsBuffer();
                    long offset = args[1].AsInt(), count = args[2].AsInt();
                    if (!CheckRange(buffer, offset, count)) return NoValue;
                    Succeed();
                    string text = new UTF8Encoding(false, false).GetString(buffer.Bytes, (int)offset, (int)count);
                    return Value.MakeString(text.Length > 0 && text[0] == '﻿' ? text.Substring(1) : text);
                },
                // Zerlegt in Zeilen (\n, \r\n, \r); ein abschließender Zeilenumbruch
                // erzeugt keine leere letzte Zeile (wie File.ReadAllLines in .NET).
                ["SplitLines"] = args =>
                {
                    Succeed();
                    var lines = new List<string>();
                    using var reader = new StringReader(args[0].AsString());
                    string? line;
                    while ((line = reader.ReadLine()) != null) lines.Add(line);
                    return StringArray(lines);
                },
            };
        }
    }
}
