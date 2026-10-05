using fire.Bytecode;
using fire.Runtime;
using fire.Terminal;
using fire.Values;
using System;
using System.Collections.Generic;
using System.Net.WebSockets;

namespace fire.Terminal.Bridge
{
    /// <summary>
    /// Die Brücke zwischen fire und der Grafik-API (siehe docs/
    /// CONSOLE.md): registriert FramebufferManager/ConsoleManager
    /// als native Funktionen (über NativeRegistry.
    /// RegisterGroup, jeweils mit eigenem Namens-Präfix) und liefert dazu
    /// passenden fire-Quelltext (<see cref="PreludeSource"/>), der
    /// diese nativen Funktionen hinter gewöhnlichen Klassen
    /// (Framebuffer/Console/Slicer) versteckt - das Fenster (`Window`) liegt getrennt in fire.Windows.Bridge (`#import "windows"`) - Skript-Code sieht nie eine
    /// rohe ID, nur normale Objekte mit normalen Methoden.
    ///
    /// Bewusst "grob" gehalten (siehe Anfrage) - nicht jede Manager-Methode
    /// ist abgebildet, nur eine repräsentative Auswahl (Erzeugen/Zerstören
    /// plus die gängigsten Operationen je Ressourcenart). Weitere Methoden
    /// lassen sich nach demselben Muster ergänzen: native Funktion in der
    /// passenden Build*Functions-Methode registrieren, passende fire-
    /// Methode in PreludeSource hinzufügen, die `this.id` automatisch
    /// mitgibt.
    /// </summary>
    public static class GraphicsBridge
    {
        public const string FramebufferPrefix = "__GRPHFb";
        public const string ConsolePrefix = "__GRPHCon";
        public const string SlicerPrefix = "__GRPHSlc";

        /// <summary>Ungültige/fehlgeschlagene Erzeugung - IdManager vergibt
        /// echte IDs immer ab 1 aufwärts (siehe dort), -1 ist deshalb als
        /// Sentinel sicher von jeder echten ID unterscheidbar. Die
        /// Create-Wrapper unten fangen JEDE Exception aus dem jeweiligen
        /// Manager ab und liefern diesen Sentinel statt die Exception roh
        /// durch die VM durchschlagen zu lassen - PreludeSource prüft
        /// darauf und wirft dafür eine echte, per `try`/`catch` fangbare
        /// HandleUnavailableException (siehe dort).</summary>
        public const int InvalidHandle = -1;

        /// <summary>`readFile`: wie `Framebuffer.FromFile` an die Bytes einer Datei kommt (der Host entscheidet, was ein Skript lesen darf, siehe IoPolicy) -
        /// ohne Angabe wird die Datei einfach gelesen.</summary>
        public static void RegisterAll(
            NativeRegistry natives, FramebufferManager framebuffers, ConsoleManager consoles, Func<string, byte[]>? readFile = null)
        {
            natives.RegisterGroup(FramebufferPrefix, BuildFramebufferFunctions(framebuffers, readFile));
            natives.RegisterGroup(ConsolePrefix, BuildConsoleFunctions(consoles));
            natives.RegisterGroup(SlicerPrefix, BuildSlicerFunctions(framebuffers));
        }

        public static void RegisterStubs(
                    NativeRegistry natives)
        {
            natives.RegisterGroup(FramebufferPrefix, BuildFramebufferFunctionStubs());
            natives.RegisterGroup(ConsolePrefix, BuildConsoleFunctionStubs());
            natives.RegisterGroup(SlicerPrefix, new Dictionary<string, NativeFunction> { ["Slice"] = args => Value.MakeUndefined() /*STUB*/ });
        }

        private static Dictionary<string, NativeFunction> BuildFramebufferFunctionStubs()
        {
            return new Dictionary<string, NativeFunction>
            {
                ["Create"] = args => Value.MakeUndefined() /*STUB*/,
                ["Destroy"] = args => Value.MakeUndefined() /*STUB*/,
                ["Width"] = args => Value.MakeUndefined() /*STUB*/,
                ["Height"] = args => Value.MakeUndefined() /*STUB*/,
                ["ReadByte"] = args => Value.MakeUndefined() /*STUB*/,
                ["WriteByte"] = args => Value.MakeUndefined() /*STUB*/,
                // WICHTIG: neue Funktionen immer ANS ENDE, in BuildFramebufferFunctions in derselben Reihenfolge (Index = Position).
                ["Mode"] = args => Value.MakeUndefined() /*STUB*/,
                ["ByteCount"] = args => Value.MakeUndefined() /*STUB*/,
                ["ReadBytes"] = args => Value.MakeUndefined() /*STUB*/,
                ["WriteBytes"] = args => Value.MakeUndefined() /*STUB*/,
                ["GetPaletteColor"] = args => Value.MakeUndefined() /*STUB*/,
                ["SetPaletteColor"] = args => Value.MakeUndefined() /*STUB*/,
                ["ReadPalette"] = args => Value.MakeUndefined() /*STUB*/,
                ["WritePalette"] = args => Value.MakeUndefined() /*STUB*/,
                ["LoadImage"] = args => Value.MakeUndefined() /*STUB*/,
                ["LoadFile"] = args => Value.MakeUndefined() /*STUB*/,
                ["FromPixels"] = args => Value.MakeUndefined() /*STUB*/,
                ["LastError"] = args => Value.MakeUndefined() /*STUB*/,
                ["GetTransparentIndex"] = args => Value.MakeUndefined() /*STUB*/,
                ["SetTransparentIndex"] = args => Value.MakeUndefined() /*STUB*/,
                ["ToMask"] = args => Value.MakeUndefined() /*STUB*/,
            };
        }

        /// <summary>Der Fehlertext der letzten fehlgeschlagenen Bild-Funktion dieses Threads (siehe LastError).</summary>
        [ThreadStatic] private static string? t_lastError;

        private static int I(Value v) => (int)v.AsInt();

        /// <summary>Eine Zahl als double (ein Skript darf `3` statt `3.0` übergeben).</summary>
        private static double D(Value v) => v.Kind == ValueKind.Float ? v.AsFloat() : v.AsInt();

        /// <summary>-1 (oder weniger) = "wie das Bild", sonst der Farbmodus.</summary>
        private static ColorMode? ModeArg(Value v)
        {
            long m = v.AsInt();
            return m < 0 ? null : (ColorMode)m;
        }

        /// <summary>Führt eine Bild-Funktion aus, die einen Framebuffer anlegt: der Fehlertext geht an LastError, das Ergebnis ist dann InvalidHandle
        /// (die Prelude wirft daraus eine fangbare ImageException).</summary>
        private static Value LoadGuarded(Func<int> action)
        {
            t_lastError = null;
            try { return Value.MakeInt(action()); }
            catch (Exception ex)
            {
                t_lastError = ex.Message;
                return Value.MakeInt(InvalidHandle);
            }
        }

        /// <summary>Führt eine Aktion aus, die an unpassenden Argumenten scheitern darf (Größe, Bereich): der Fehlertext geht an LastError, das Ergebnis ist false.</summary>
        private static bool Succeeded(Action action)
        {
            t_lastError = null;
            try { action(); return true; }
            catch (Exception ex) when (ex is ArgumentException)
            {
                t_lastError = ex.Message.Split('\n')[0].Replace(" (Parameter 'data')", "").Replace(" (Parameter 'index')", "");
                return false;
            }
        }

        private static byte[] DefaultReadFile(string path) => System.IO.File.ReadAllBytes(System.IO.Path.GetFullPath(path));

        private static Dictionary<string, NativeFunction> BuildFramebufferFunctions(FramebufferManager mgr, Func<string, byte[]>? readFile)
        {
            var reader = readFile ?? DefaultReadFile;
            return new Dictionary<string, NativeFunction>
            {
                ["Create"] = args =>
                {
                    try { return Value.MakeInt(mgr.CreateFramebuffer(I(args[0]), I(args[1]), args.Length > 2 ? (ColorMode)I(args[2]) : ColorMode.Rgba)); }
                    catch { return Value.MakeInt(InvalidHandle); }
                },
                ["Destroy"] = args => Value.MakeBool(mgr.DestroyFramebuffer(I(args[0]))),
                ["Width"] = args => Value.MakeInt(mgr.GetWidth(I(args[0]))),
                ["Height"] = args => Value.MakeInt(mgr.GetHeight(I(args[0]))),
                ["ReadByte"] = args => Value.MakeInt(mgr.ReadByte(I(args[0]), I(args[1]))),
                ["WriteByte"] = args =>
                {
                    mgr.WriteByte(I(args[0]), I(args[1]), (byte)args[2].AsInt());
                    return Value.MakeUndefined();
                },
                // WICHTIG: neue Funktionen immer ANS ENDE, in BuildFramebufferFunctionStubs in derselben Reihenfolge (Index = Position).
                ["Mode"] = args => Value.MakeInt((int)mgr.GetMode(I(args[0]))),
                ["ByteCount"] = args => Value.MakeInt(mgr.GetByteCount(I(args[0]))),
                ["ReadBytes"] = args => Value.MakeBuffer(new ByteBuffer(mgr.ReadBytes(I(args[0])), ByteConversions.HostByteOrder)),
                // false bei unpassenden Daten (Grund: LastError), die Prelude macht daraus eine GraphicsException
                ["WriteBytes"] = args => Value.MakeBool(Succeeded(() => mgr.WriteBytes(I(args[0]), args[1].AsBuffer().Bytes))),
                // Palette-Farben sind VORZEICHENLOSE Werte (r + g*256 + b*65536 + a*16777216), anders als GetPixel; -1 bei ungültigem Index
                ["GetPaletteColor"] = args =>
                {
                    long color = -1;
                    return Succeeded(() => color = (uint)mgr.GetPaletteColor(I(args[0]), I(args[1]))) ? Value.MakeInt(color) : Value.MakeInt(-1);
                },
                ["SetPaletteColor"] = args => Value.MakeBool(Succeeded(() => mgr.SetPaletteColor(I(args[0]), I(args[1]), I(args[2])))),
                ["ReadPalette"] = args => Value.MakeBuffer(new ByteBuffer(mgr.ReadPalette(I(args[0]), args[1].AsBool()), ByteConversions.HostByteOrder)),
                ["WritePalette"] = args => Value.MakeBool(Succeeded(() => mgr.WritePalette(I(args[0]), args[1].AsBuffer().Bytes))),
                // Bilder: liefern die ID des neuen Framebuffers oder InvalidHandle (Grund: LastError)
                ["LoadImage"] = args => LoadGuarded(() => mgr.LoadImage(args[0].AsBuffer().Bytes, ModeArg(args[1]))),
                ["LoadFile"] = args => LoadGuarded(() => mgr.LoadImage(reader(args[0].AsString()), ModeArg(args[1]))),
                ["FromPixels"] = args => LoadGuarded(() => mgr.CreateFromPixels(I(args[0]), I(args[1]), args[2].AsBuffer().Bytes, (ColorMode)I(args[3]),
                    args.Length > 4 && args[4].Kind == ValueKind.Buffer ? args[4].AsBuffer().Bytes : null)),
                ["LastError"] = args => Value.MakeString(t_lastError ?? string.Empty),
                ["GetTransparentIndex"] = args => Value.MakeInt(mgr.GetTransparentIndex(I(args[0]))),
                ["SetTransparentIndex"] = args =>
                {
                    mgr.SetTransparentIndex(I(args[0]), I(args[1]));
                    return Value.MakeUndefined();
                },
                ["ToMask"] = args => LoadGuarded(() => mgr.CreateMask(I(args[0]), I(args[1]), args[2].AsBool(), I(args[3]))),
            };
        }

        /// <summary>Die Bahnen des ImageSlicer für fire: ein Array je Bahn `[art, geschlossen, punkte]` (art 0 = Fill, 1 = Outline; punkte = flaches Array
        /// `[x0, y0, x1, y1, ...]` in mm). Bei ungültigen Argumenten -1 (Grund: LastError); die Prelude macht daraus `Slicer.Slice`.</summary>
        private static Dictionary<string, NativeFunction> BuildSlicerFunctions(FramebufferManager mgr)
        {
            return new Dictionary<string, NativeFunction>
            {
                ["Slice"] = args =>
                {
                    t_lastError = null;
                    try
                    {
                        double tolerance = D(args[5]);
                        var paths = mgr.Slice(I(args[0]), D(args[1]), D(args[2]), D(args[3]), (FillStrategy)I(args[4]),
                            tolerance < 0 ? double.NaN : tolerance, args[6].AsBool());
                        var result = new ScriptArray(paths.Count);
                        for (int i = 0; i < paths.Count; i++)
                        {
                            var path = paths[i];
                            var points = new ScriptArray(path.Points.Count * 2);
                            for (int j = 0; j < path.Points.Count; j++)
                            {
                                points.Items[2 * j] = Value.MakeFloat(path.Points[j].X);
                                points.Items[2 * j + 1] = Value.MakeFloat(path.Points[j].Y);
                            }
                            var entry = new ScriptArray(3);
                            entry.Items[0] = Value.MakeInt((int)path.Kind);
                            entry.Items[1] = Value.MakeBool(path.Closed);
                            entry.Items[2] = Value.MakeArray(points);
                            result.Items[i] = Value.MakeArray(entry);
                        }
                        return Value.MakeArray(result);
                    }
                    catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
                    {
                        t_lastError = ex.Message.Split('\n')[0];
                        return Value.MakeInt(InvalidHandle);
                    }
                },
            };
        }

        private static Dictionary<string, NativeFunction> BuildConsoleFunctionStubs()
        {
            return new Dictionary<string, NativeFunction>
            {
                ["Create"] = args => Value.MakeUndefined() /*STUB*/,
                ["Destroy"] = args => Value.MakeUndefined() /*STUB*/,
                ["Print"] = args => Value.MakeUndefined() /*STUB*/,
                ["Locate"] = args => Value.MakeUndefined() /*STUB*/,
                ["Clear"] = args => Value.MakeUndefined() /*STUB*/,
                ["SetColor"] = args => Value.MakeUndefined() /*STUB*/,
                ["SetPixel"] = args => Value.MakeUndefined() /*STUB*/,
                ["GetPixel"] = args => Value.MakeUndefined() /*STUB*/,
                ["FillRect"] = args => Value.MakeUndefined() /*STUB*/,
                ["DrawRect"] = args => Value.MakeUndefined() /*STUB*/,
                ["DrawLine"] = args => Value.MakeUndefined() /*STUB*/,
                ["DrawText"] = args => Value.MakeUndefined() /*STUB*/,
                ["CellWidth"] = args => Value.MakeUndefined() /*STUB*/,
                ["CellHeight"] = args => Value.MakeUndefined() /*STUB*/,
                // WICHTIG: neue Funktionen immer ANS ENDE, in BuildConsoleFunctions in derselben Reihenfolge (Index = Position).
                ["GetPixelIndex"] = args => Value.MakeUndefined() /*STUB*/,
                ["DrawCircle"] = args => Value.MakeUndefined() /*STUB*/,
                ["FillCircle"] = args => Value.MakeUndefined() /*STUB*/,
                ["DrawEllipse"] = args => Value.MakeUndefined() /*STUB*/,
                ["FillEllipse"] = args => Value.MakeUndefined() /*STUB*/,
                ["DrawTriangle"] = args => Value.MakeUndefined() /*STUB*/,
                ["FillTriangle"] = args => Value.MakeUndefined() /*STUB*/,
                ["DrawPolygon"] = args => Value.MakeUndefined() /*STUB*/,
                ["FillPolygon"] = args => Value.MakeUndefined() /*STUB*/,
                ["FloodFill"] = args => Value.MakeUndefined() /*STUB*/,
                ["FloodFillBorder"] = args => Value.MakeUndefined() /*STUB*/,
                ["Blit"] = args => Value.MakeUndefined() /*STUB*/,
            };
        }

        private static Dictionary<string, NativeFunction> BuildConsoleFunctions(ConsoleManager mgr)
        {
            return new Dictionary<string, NativeFunction>
            {
                ["Create"] = args =>
                {
                    try { return Value.MakeInt(mgr.CreateConsole((int)args[0].AsInt())); }
                    catch { return Value.MakeInt(InvalidHandle); }
                },
                ["Destroy"] = args => Value.MakeBool(mgr.DestroyConsole((int)args[0].AsInt())),
                ["Print"] = args =>
                {
                    mgr.Print((int)args[0].AsInt(), args[1].AsString());
                    return Value.MakeUndefined();
                },
                ["Locate"] = args =>
                {
                    mgr.Locate((int)args[0].AsInt(), (int)args[1].AsInt(), (int)args[2].AsInt());
                    return Value.MakeUndefined();
                },
                ["Clear"] = args =>
                {
                    mgr.Clear((int)args[0].AsInt());
                    return Value.MakeUndefined();
                },
                ["SetColor"] = args =>
                {
                    mgr.SetColor((int)args[0].AsInt(), (int)args[1].AsInt(), (int)args[2].AsInt());
                    return Value.MakeUndefined();
                },
                ["SetPixel"] = args =>
                {
                    mgr.SetPixel((int)args[0].AsInt(), (int)args[1].AsInt(), (int)args[2].AsInt(), (int)args[3].AsInt());
                    return Value.MakeUndefined();
                },
                ["GetPixel"] = args => Value.MakeInt(mgr.GetPixel((int)args[0].AsInt(), (int)args[1].AsInt(), (int)args[2].AsInt())),
                // Die folgenden nehmen ROHE Farbwerte (R im niedrigsten Byte, Alpha im höchsten), keine Palette-Indizes.
                ["FillRect"] = args =>
                {
                    mgr.FillRect((int)args[0].AsInt(), (int)args[1].AsInt(), (int)args[2].AsInt(), (int)args[3].AsInt(), (int)args[4].AsInt(), (int)args[5].AsInt());
                    return Value.MakeUndefined();
                },
                ["DrawRect"] = args =>
                {
                    mgr.DrawRect((int)args[0].AsInt(), (int)args[1].AsInt(), (int)args[2].AsInt(), (int)args[3].AsInt(), (int)args[4].AsInt(), (int)args[5].AsInt());
                    return Value.MakeUndefined();
                },
                ["DrawLine"] = args =>
                {
                    mgr.DrawLine((int)args[0].AsInt(), (int)args[1].AsInt(), (int)args[2].AsInt(), (int)args[3].AsInt(), (int)args[4].AsInt(), (int)args[5].AsInt());
                    return Value.MakeUndefined();
                },
                ["DrawText"] = args =>
                {
                    mgr.DrawText((int)args[0].AsInt(), (int)args[1].AsInt(), (int)args[2].AsInt(), args[3].AsString(), (int)args[4].AsInt(), (int)args[5].AsInt());
                    return Value.MakeUndefined();
                },
                ["CellWidth"] = args => Value.MakeInt(mgr.GetCellWidth((int)args[0].AsInt())),
                ["CellHeight"] = args => Value.MakeInt(mgr.GetCellHeight((int)args[0].AsInt())),
                // WICHTIG: neue Funktionen immer ANS ENDE, in BuildConsoleFunctionStubs in derselben Reihenfolge (Index = Position).
                // Farben: eine Zahl 0-255 ist ein Palette-Index, jede andere ein direkter Wert (siehe Paint.FromArgument).
                ["GetPixelIndex"] = args => Value.MakeInt(mgr.GetPixelIndex(I(args[0]), I(args[1]), I(args[2]))),
                ["DrawCircle"] = args =>
                {
                    mgr.DrawCircle(I(args[0]), I(args[1]), I(args[2]), I(args[3]), I(args[4]));
                    return Value.MakeUndefined();
                },
                ["FillCircle"] = args =>
                {
                    mgr.FillCircle(I(args[0]), I(args[1]), I(args[2]), I(args[3]), I(args[4]));
                    return Value.MakeUndefined();
                },
                ["DrawEllipse"] = args =>
                {
                    mgr.DrawEllipse(I(args[0]), I(args[1]), I(args[2]), I(args[3]), I(args[4]), I(args[5]));
                    return Value.MakeUndefined();
                },
                ["FillEllipse"] = args =>
                {
                    mgr.FillEllipse(I(args[0]), I(args[1]), I(args[2]), I(args[3]), I(args[4]), I(args[5]));
                    return Value.MakeUndefined();
                },
                ["DrawTriangle"] = args =>
                {
                    mgr.DrawTriangle(I(args[0]), I(args[1]), I(args[2]), I(args[3]), I(args[4]), I(args[5]), I(args[6]), I(args[7]));
                    return Value.MakeUndefined();
                },
                ["FillTriangle"] = args =>
                {
                    mgr.FillTriangle(I(args[0]), I(args[1]), I(args[2]), I(args[3]), I(args[4]), I(args[5]), I(args[6]), I(args[7]));
                    return Value.MakeUndefined();
                },
                ["DrawPolygon"] = args =>
                {
                    mgr.DrawPolygon(I(args[0]), ReadPoints(args[1]), I(args[2]), args[3].AsBool());
                    return Value.MakeUndefined();
                },
                ["FillPolygon"] = args =>
                {
                    mgr.FillPolygon(I(args[0]), ReadPoints(args[1]), I(args[2]));
                    return Value.MakeUndefined();
                },
                ["FloodFill"] = args =>
                {
                    mgr.FloodFill(I(args[0]), I(args[1]), I(args[2]), I(args[3]));
                    return Value.MakeUndefined();
                },
                ["FloodFillBorder"] = args =>
                {
                    mgr.FloodFillBorder(I(args[0]), I(args[1]), I(args[2]), I(args[3]), I(args[4]));
                    return Value.MakeUndefined();
                },
                ["Blit"] = args =>
                {
                    mgr.Blit(I(args[0]), I(args[1]), I(args[2]), I(args[3]), I(args[4]), I(args[5]), I(args[6]), I(args[7]), I(args[8]), I(args[9]), I(args[10]), I(args[11]));
                    return Value.MakeUndefined();
                },
            };
        }

        /// <summary>Die Punkte eines Polygons aus einem Skript-Array `[x0, y0, x1, y1, ...]` (Kommazahlen werden abgeschnitten; ein ungerades letztes Element zählt nicht).</summary>
        private static int[] ReadPoints(Value array)
        {
            var items = array.AsArray().Items;
            var points = new int[items.Length - items.Length % 2];
            for (int i = 0; i < points.Length; i++)
                points[i] = items[i].Kind == ValueKind.Float ? (int)items[i].AsFloat() : (int)items[i].AsInt();
            return points;
        }

        /// <summary>fire-Quelltext, der die per <see cref="RegisterAll"/>
        /// registrierten nativen Funktionen hinter drei gewöhnlichen Klassen
        /// versteckt - VOR das eigentliche Nutzer-Skript zu setzen (analog
        /// zu Standard.Prelude.Source, siehe Parser.ParseWithPrelude für das
        /// allgemeine Muster). Jede Klasse merkt sich ihre ID als Feld und
        /// gibt sie bei jeder Methode automatisch als erstes Argument an die
        /// zugehörige native Funktion mit - Skript-Code selbst sieht nie
        /// eine rohe ID.</summary>
        public const string PreludeSource = """
            class HandleUnavailableException : Exception {
                string message

                construct(string message) {
                    this.message = message
                }
            }

            // Wie ein Framebuffer seine Pixel speichert: Rgba = 4 Byte je Pixel (jede Farbe direkt), Palette = 1 Byte je Pixel (Index in eine
            // 256-Farben-Palette; ein Palette-Eintrag ändern färbt alle Pixel mit diesem Index um).
            enum ColorMode {
                Rgba = 0,
                Palette = 1
            }

            // Wie Console.Blit mit durchsichtigen Pixeln umgeht: Copy kopiert alles, Transparent lässt durchsichtige Pixel der Quelle aus (RGBA: Alpha 0,
            // Palette: der Farbschlüssel bzw. der TransparentIndex des Bildes), Blend mischt halbdurchsichtige RGBA-Pixel nach ihrem Alpha-Wert.
            enum BlitMode {
                Copy = 0,
                Transparent = 1,
                Blend = 2
            }

            // Ein Grafik-Aufruf mit unpassenden Daten (z.B. ein Puffer falscher Größe, ein Palette-Index außerhalb von 0-255).
            class GraphicsException : Exception {
                string message

                construct(string message) {
                    this.message = message
                }
            }

            // Ein Bild konnte nicht geladen werden (unbekanntes Format, beschädigt, Datei nicht lesbar oder nicht erlaubt).
            class ImageException : GraphicsException {
                construct(string message) : base(message) { }
            }

            class Framebuffer {
                int id

                // mode: ColorMode.Rgba (Vorgabe) oder ColorMode.Palette. (mode -2 ist intern: ein leerer Rahmen für die Fabrikmethoden unten.)
                construct(int width, int height, int mode = 0) {
                    if (mode == -2) {
                        this.id = 0
                    } else {
                        this.id = __GRPHFbCreate(width, height, mode)
                        if (this.id == -1) {
                            throw new HandleUnavailableException("The framebuffer could not be created.")
                        }
                    }
                }

                destruct() {
                    if (this.id > 0) {
                        __GRPHFbDestroy(this.id)
                    }
                }

                // Ein Bild laden: PNG, BMP oder GIF - als Inhalt einer Datei (FromImage: ein byte-Puffer mit den Bytes der Datei) oder von einem Pfad
                // (FromFile; der Host entscheidet, was gelesen werden darf). Der Framebuffer hat die Größe des Bildes. mode -1 (Vorgabe): wie das Bild -
                // ein Bild mit Palette (PNG mit Palette, GIF, BMP bis 8 Bit) wird ein Palette-Framebuffer mit der Palette der Datei, alles andere
                // RGBA; ColorMode.Rgba / ColorMode.Palette erzwingt einen Modus (ein RGBA-Bild wird dann auf die Standard-Palette abgebildet).
                // Fehler: ImageException.
                static Framebuffer FromImage(data, int mode = -1) {
                    var fb = new Framebuffer(0, 0, -2)
                    fb.id = __GRPHFbLoadImage(data, mode)
                    if (fb.id < 0) {
                        throw new ImageException(__GRPHFbLastError())
                    }
                    return fb
                }

                static Framebuffer FromFile(string path, int mode = -1) {
                    var fb = new Framebuffer(0, 0, -2)
                    fb.id = __GRPHFbLoadFile(path, mode)
                    if (fb.id < 0) {
                        throw new ImageException(__GRPHFbLastError())
                    }
                    return fb
                }

                // Rohe Pixel aus einem byte-Puffer, zeilenweise von oben nach unten: ColorMode.Rgba width*height*4 Byte (R, G, B, A je Pixel),
                // ColorMode.Palette width*height Byte (ein Index je Pixel) und optional eine Palette (768 Byte RGB oder 1024 Byte RGBA).
                static Framebuffer FromPixels(int width, int height, pixels, int mode = 0, palette = undefined) {
                    var fb = new Framebuffer(0, 0, -2)
                    fb.id = __GRPHFbFromPixels(width, height, pixels, mode, palette)
                    if (fb.id < 0) {
                        throw new ImageException(__GRPHFbLastError())
                    }
                    return fb
                }

                int Width() { return __GRPHFbWidth(this.id) }
                int Height() { return __GRPHFbHeight(this.id) }
                int Mode() { return __GRPHFbMode(this.id) }

                // Rohdaten: RGBA 4 Byte je Pixel (R, G, B, A), Palette 1 Byte je Pixel (der Index); ByteCount() ist die Größe.
                int ByteCount() { return __GRPHFbByteCount(this.id) }
                int ReadByte(int offset) { return __GRPHFbReadByte(this.id, offset) }
                WriteByte(int offset, int value) { __GRPHFbWriteByte(this.id, offset, value) }
                ReadBytes() { return __GRPHFbReadBytes(this.id) }
                WriteBytes(data) {
                    if (!__GRPHFbWriteBytes(this.id, data)) {
                        throw new GraphicsException(__GRPHFbLastError())
                    }
                }

                // Die 256-Farben-Palette (bei einem Palette-Framebuffer ist sie die Farbtabelle des Bildes, bei einem RGBA-Framebuffer löst sie Palette-Indizes
                // auf, die man als Farbe angibt). Farben sind r + g*256 + b*65536 + a*16777216 (Alpha 255 = deckend).
                int GetPaletteColor(int index) {
                    var color = __GRPHFbGetPaletteColor(this.id, index)
                    if (color < 0) {
                        throw new GraphicsException(__GRPHFbLastError())
                    }
                    return color
                }
                SetPaletteColor(int index, int color) {
                    if (!__GRPHFbSetPaletteColor(this.id, index, color)) {
                        throw new GraphicsException(__GRPHFbLastError())
                    }
                }
                SetPaletteRgb(int index, int r, int g, int b) { this.SetPaletteColor(index, r + g * 256 + b * 65536 + 255 * 16777216) }
                // 768 Byte (R, G, B je Eintrag) oder mit withAlpha 1024 Byte (R, G, B, A)
                ReadPalette(bool withAlpha = false) { return __GRPHFbReadPalette(this.id, withAlpha) }
                WritePalette(data) {
                    if (!__GRPHFbWritePalette(this.id, data)) {
                        throw new GraphicsException(__GRPHFbLastError())
                    }
                }

                // Die Maske dieses Bildes als NEUER Palette-Framebuffer gleicher Größe: Index 1 (weiß) = dieses Pixel soll ausgefräst werden (siehe Slicer),
                // Index 0 (schwarz) nicht. Pixel mit geringerer Deckkraft als alphaThreshold zählen nie; sonst entscheidet die Helligkeit gegen threshold
                // (0-255): darkIsRemoved = dunkle Pixel werden ausgefräst, false = helle.
                Framebuffer ToMask(int threshold = 128, bool darkIsRemoved = true, int alphaThreshold = 128) {
                    var mask = new Framebuffer(0, 0, -2)
                    mask.id = __GRPHFbToMask(this.id, threshold, darkIsRemoved, alphaThreshold)
                    if (mask.id < 0) {
                        throw new GraphicsException(__GRPHFbLastError())
                    }
                    return mask
                }

                // Palette-Framebuffer: der Index, der in einem Bild als durchsichtig gilt (GIF/PNG), -1 = keiner. Console.Blit mit BlitMode.Transparent überspringt ihn.
                int TransparentIndex {
                    get { return __GRPHFbGetTransparentIndex(this.id) }
                    set { __GRPHFbSetTransparentIndex(this.id, value) }
                }
            }

            // Art einer Werkzeugbahn: Fill = Ausräumen der Fläche, Outline = Schlichtkontur am Rand.
            enum PathKind {
                Fill = 0,
                Outline = 1
            }

            // Wie das Innere einer Fläche ausgeräumt wird: Contour = konzentrische, konturparallele Bahnen (von innen nach außen), ZigZag = waagerechte
            // Zickzack-Bahnen plus Randkontur, OutlineOnly = nur die Randkontur.
            enum FillStrategy {
                Contour = 0,
                ZigZag = 1,
                OutlineOnly = 2
            }

            // Eine Werkzeugbahn (Linienzug der Werkzeugmitte) in Millimetern.
            class ToolPath {
                int kind          // PathKind
                bool closed       // true: der letzte Punkt ist mit dem ersten verbunden
                points            // flaches Array [x0, y0, x1, y1, ...]

                construct(int kind, bool closed, points) {
                    this.kind = kind
                    this.closed = closed
                    this.points = points
                }

                int Count() { return this.points.length / 2 }
                float X(int index) { return this.points[index * 2] }
                float Y(int index) { return this.points[index * 2 + 1] }
            }

            // Zerlegt eine Maske (siehe Framebuffer.ToMask) in Werkzeugbahnen mit fester Linienstärke: Euklidische Distanztransformation, die Werkzeugmitte
            // darf nur dort liegen, wo der Abstand zum Rand mindestens der Linienradius ist; die Isolinie dort ist die Randkontur (subpixelgenau), das Innere
            // wird mit Bahnen im Abstand StepOver gefüllt. Alle Koordinaten in Millimetern, die Mitte des Werkzeugs.
            class Slicer {
                float lineWidth        // Linienstärke bzw. Werkzeugdurchmesser in mm
                float pixelSize        // Kantenlänge eines Pixels der Maske in mm (z.B. 25.4 / dpi)
                float overlap = 0.5    // Überlappung benachbarter Bahnen als Anteil der Linienstärke (0 bis 0.95); ab 0.5 bleiben bei Contour keine Restinseln
                int strategy = 0       // FillStrategy (Vorgabe Contour)
                float simplifyTolerance = -1.0   // Toleranz der Punktreduktion in mm; negativ = automatisch (1/4 Pixel), 0 = keine
                bool flipY = true      // true: Y zeigt nach oben (Maschinenkoordinaten), false: wie im Bild nach unten

                construct(float lineWidth, float pixelSize) {
                    if (lineWidth <= 0 || pixelSize <= 0) {
                        throw new GraphicsException("Line thickness and pixel size must be greater than 0.")
                    }
                    this.lineWidth = lineWidth
                    this.pixelSize = pixelSize
                }

                // Abstand zwischen benachbarten Bahnen in mm
                float StepOver { get { return this.lineWidth * (1.0 - this.overlap) } }

                // Liefert eine List von ToolPath (leer, wenn keine Stelle breit genug für die Linienstärke ist). Die Maske: ein Framebuffer, dessen gesetzte Pixel
                // (Palette: Index ungleich 0, RGBA: sichtbar und nicht schwarz) ausgefräst werden - meist von Framebuffer.ToMask.
                Slice(Framebuffer mask) {
                    if (this.overlap < 0 || this.overlap > 0.95) {
                        throw new GraphicsException("The overlap must be between 0 and 0.95.")
                    }
                    var raw = __GRPHSlcSlice(mask.id, this.lineWidth, this.pixelSize, this.overlap, this.strategy, this.simplifyTolerance, this.flipY)
                    if (raw is of int) {
                        throw new GraphicsException(__GRPHFbLastError())
                    }
                    var paths = new List()
                    for (var i = 0; i < raw.length; i = i + 1) {
                        var entry = raw[i]
                        paths.Add(new ToolPath(entry[0], entry[1], entry[2]))
                    }
                    return paths
                }
            }

            class Console {
                int id

                construct(Framebuffer framebuffer) {
                    this.id = __GRPHConCreate(framebuffer.id)
                    if (this.id == -1) {
                        throw new HandleUnavailableException("The console could not be created.")
                    }
                }

                destruct() {
                    __GRPHConDestroy(this.id)
                }

                Print(string text) { __GRPHConPrint(this.id, text) }
                Locate(int row, int column) { __GRPHConLocate(this.id, row, column) }
                Clear() { __GRPHConClear(this.id) }
                SetColor(int foreground, int background) { __GRPHConSetColor(this.id, foreground, background) }
                SetPixel(int x, int y, int color) { __GRPHConSetPixel(this.id, x, y, color) }
                int GetPixel(int x, int y) { return __GRPHConGetPixel(this.id, x, y) }

                // Farben: eine Zahl von 0 bis 255 ist ein Palette-Index, jede andere ein direkter Wert r + g*256 + b*65536 + a*16777216
                // (a = 255 deckend; siehe UI.Color.Rgb). Positionen/Größen in PIXELN.
                FillRect(int x, int y, int w, int h, int color) { __GRPHConFillRect(this.id, x, y, w, h, color) }
                DrawRect(int x, int y, int w, int h, int color) { __GRPHConDrawRect(this.id, x, y, w, h, color) }
                DrawLine(int x0, int y0, int x1, int y1, int color) { __GRPHConDrawLine(this.id, x0, y0, x1, y1, color) }
                // background 0 (Alpha 0) = transparent: nur die Zeichen-Pixel werden geschrieben
                DrawText(int x, int y, string text, int color, int background) { __GRPHConDrawText(this.id, x, y, text, color, background) }
                int CellWidth() { return __GRPHConCellWidth(this.id) }
                int CellHeight() { return __GRPHConCellHeight(this.id) }

                // Farben aller Zeichenfunktionen: eine Zahl von 0 bis 255 ist ein Index der Palette des Framebuffers, jede andere ein direkter Wert
                // (r + g*256 + b*65536 + a*16777216). In einem Palette-Framebuffer wird ein direkter Wert auf den nächsten Palette-Eintrag abgebildet.
                // Alles wird am Rand des Framebuffers beschnitten.

                // Der Palette-Index des Pixels (RGBA-Framebuffer: der Eintrag, der seiner Farbe am nächsten kommt)
                int GetPixelIndex(int x, int y) { return __GRPHConGetPixelIndex(this.id, x, y) }

                // Kreis um (cx, cy) mit Radius r, Ellipse mit den Halbachsen rx (waagerecht) und ry (senkrecht); Fill... füllt die Fläche samt Rand
                DrawCircle(int cx, int cy, int r, int color) { __GRPHConDrawCircle(this.id, cx, cy, r, color) }
                FillCircle(int cx, int cy, int r, int color) { __GRPHConFillCircle(this.id, cx, cy, r, color) }
                DrawEllipse(int cx, int cy, int rx, int ry, int color) { __GRPHConDrawEllipse(this.id, cx, cy, rx, ry, color) }
                FillEllipse(int cx, int cy, int rx, int ry, int color) { __GRPHConFillEllipse(this.id, cx, cy, rx, ry, color) }

                DrawTriangle(int x0, int y0, int x1, int y1, int x2, int y2, int color) { __GRPHConDrawTriangle(this.id, x0, y0, x1, y1, x2, y2, color) }
                FillTriangle(int x0, int y0, int x1, int y1, int x2, int y2, int color) { __GRPHConFillTriangle(this.id, x0, y0, x1, y1, x2, y2, color) }

                // points: ein Array [x0, y0, x1, y1, ...]. DrawPolygon verbindet den letzten mit dem ersten Punkt (closed = false: nur der Linienzug);
                // FillPolygon füllt nach der Even-Odd-Regel (sich überschneidende Teile bleiben leer).
                DrawPolygon(points, int color, bool closed = true) { __GRPHConDrawPolygon(this.id, points, color, closed) }
                FillPolygon(points, int color) { __GRPHConFillPolygon(this.id, points, color) }

                // Füllt die zusammenhängende Fläche mit der Farbe des Pixels (x, y) mit `color`. FloodFillBorder füllt stattdessen bis zu Pixeln der
                // Farbe `border` (wie PAINT in QBasic).
                FloodFill(int x, int y, int color) { __GRPHConFloodFill(this.id, x, y, color) }
                FloodFillBorder(int x, int y, int color, int border) { __GRPHConFloodFillBorder(this.id, x, y, color, border) }

                // Kopiert einen anderen Framebuffer (z.B. ein geladenes Bild) hierher, auch zwischen den Farbmodi (siehe BlitMode). Blit: das ganze Bild mit
                // der linken oberen Ecke bei (x, y); BlitRegion: der Ausschnitt (sx, sy, sw, sh) nach (dx, dy); BlitScaled: dazu auf die Größe dw x dh gebracht
                // (nächster Nachbar; eine NEGATIVE Breite/Höhe spiegelt). `key`: bei einem Palette-Bild der durchsichtige Index (-1 = sein TransparentIndex).
                Blit(Framebuffer source, int x, int y, int mode = 0, int key = -1) {
                    __GRPHConBlit(this.id, source.id, 0, 0, source.Width(), source.Height(), x, y, source.Width(), source.Height(), mode, key)
                }
                BlitRegion(Framebuffer source, int sx, int sy, int sw, int sh, int dx, int dy, int mode = 0, int key = -1) {
                    __GRPHConBlit(this.id, source.id, sx, sy, sw, sh, dx, dy, sw, sh, mode, key)
                }
                BlitScaled(Framebuffer source, int sx, int sy, int sw, int sh, int dx, int dy, int dw, int dh, int mode = 0, int key = -1) {
                    __GRPHConBlit(this.id, source.id, sx, sy, sw, sh, dx, dy, dw, dh, mode, key)
                }
            }
            """;
    }
}
