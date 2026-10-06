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
    /// CONSOLE.md): registriert FramebufferManager/RendererManager
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
        public const string RendererPrefix = "__GRPHRnd";
        public const string BrushPrefix = "__GRPHBsh";
        public const string PenPrefix = "__GRPHPen";
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
            NativeRegistry natives, FramebufferManager framebuffers, RendererManager renderers, Func<string, byte[]>? readFile = null)
        {
            natives.RegisterGroup(FramebufferPrefix, BuildFramebufferFunctions(framebuffers, readFile));
            natives.RegisterGroup(RendererPrefix, BuildRendererFunctions(renderers));
            natives.RegisterGroup(BrushPrefix, BuildBrushFunctions(renderers));
            natives.RegisterGroup(PenPrefix, BuildPenFunctions(renderers));
            natives.RegisterGroup(SlicerPrefix, BuildSlicerFunctions(framebuffers));
        }

        public static void RegisterStubs(
                    NativeRegistry natives)
        {
            natives.RegisterGroup(FramebufferPrefix, BuildFramebufferFunctionStubs());
            natives.RegisterGroup(RendererPrefix, StubsOf(RendererFunctionNames));
            natives.RegisterGroup(BrushPrefix, StubsOf(BrushFunctionNames));
            natives.RegisterGroup(PenPrefix, StubsOf(PenFunctionNames));
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

        // Die Namen der Funktionen jeder Gruppe in ihrer Reihenfolge (Index = Position): die Stubs (nur für den Compiler, der die Natives kennen muss) und die echten Funktionen
        // müssen übereinstimmen - deshalb gibt es sie nur hier und BuildXFunctions prüft sie.
        private static readonly string[] RendererFunctionNames =
        {
            "Create", "Destroy", "Print", "Locate", "Clear", "ClearTo", "SetColor", "SetPixel", "GetPixel", "GetPixelIndex", "CellWidth", "CellHeight",
            "GetAlphaBlending", "SetAlphaBlending", "DrawText",
            "FillRect", "Fill", "FillCircle", "FillEllipse", "FillTriangle", "FillPolygon", "FloodFill", "FloodFillBorder",
            "DrawPoint", "DrawLine", "DrawPath", "DrawRect", "DrawCircle", "DrawEllipse", "DrawTriangle", "DrawPolygon",
            "Blit", "SetClip", "ResetClip",
        };

        private static readonly string[] BrushFunctionNames = { "CreateSolid", "Destroy", "GetColor", "SetColor" };

        private static readonly string[] PenFunctionNames = { "Create", "Destroy", "GetColor", "SetColor", "GetWidth", "SetWidth", "GetShape", "SetShape" };

        private static Dictionary<string, NativeFunction> StubsOf(string[] names)
        {
            var stubs = new Dictionary<string, NativeFunction>();
            foreach (var name in names) stubs[name] = args => Value.MakeUndefined() /*STUB*/;
            return stubs;
        }

        /// <summary>Stellt sicher, dass die echten Funktionen genau die Namen (und die Reihenfolge) der Stubs haben.</summary>
        private static Dictionary<string, NativeFunction> Ordered(string[] names, Dictionary<string, NativeFunction> functions)
        {
            var ordered = new Dictionary<string, NativeFunction>();
            foreach (var name in names) ordered[name] = functions[name];
            if (functions.Count != names.Length) throw new InvalidOperationException("The natives and their stubs differ.");
            return ordered;
        }

        private static Value Nothing(Action action)
        {
            action();
            return Value.MakeUndefined();
        }

        private static Dictionary<string, NativeFunction> BuildRendererFunctions(RendererManager mgr)
        {
            return Ordered(RendererFunctionNames, new Dictionary<string, NativeFunction>
            {
                ["Create"] = args =>
                {
                    try { return Value.MakeInt(mgr.CreateRenderer(I(args[0]))); }
                    catch { return Value.MakeInt(InvalidHandle); }
                },
                ["Destroy"] = args => Value.MakeBool(mgr.DestroyRenderer(I(args[0]))),
                ["Print"] = args => Nothing(() => mgr.Print(I(args[0]), args[1].AsString())),
                ["Locate"] = args => Nothing(() => mgr.Locate(I(args[0]), I(args[1]), I(args[2]))),
                ["Clear"] = args => Nothing(() => mgr.Clear(I(args[0]))),
                // eine Zahl 0-255 ist ein Palette-Index, jede andere ein direkter Wert (siehe Paint.FromArgument)
                ["ClearTo"] = args => Nothing(() => mgr.ClearTo(I(args[0]), I(args[1]))),
                ["SetColor"] = args => Nothing(() => mgr.SetColor(I(args[0]), I(args[1]), I(args[2]))),
                ["SetPixel"] = args => Nothing(() => mgr.SetPixel(I(args[0]), I(args[1]), I(args[2]), I(args[3]))),
                ["GetPixel"] = args => Value.MakeInt(mgr.GetPixel(I(args[0]), I(args[1]), I(args[2]))),
                ["GetPixelIndex"] = args => Value.MakeInt(mgr.GetPixelIndex(I(args[0]), I(args[1]), I(args[2]))),
                ["CellWidth"] = args => Value.MakeInt(mgr.GetCellWidth(I(args[0]))),
                ["CellHeight"] = args => Value.MakeInt(mgr.GetCellHeight(I(args[0]))),
                ["GetAlphaBlending"] = args => Value.MakeBool(mgr.GetAlphaBlending(I(args[0]))),
                ["SetAlphaBlending"] = args => Nothing(() => mgr.SetAlphaBlending(I(args[0]), args[1].AsBool())),
                // Pinsel und Stifte kommen als ID; beim Text ist 0 als Hintergrund "kein Hintergrund"
                ["DrawText"] = args => Nothing(() => mgr.DrawText(I(args[0]), I(args[1]), I(args[2]), args[3].AsString(), I(args[4]), I(args[5]))),
                ["FillRect"] = args => Nothing(() => mgr.FillRect(I(args[0]), I(args[1]), I(args[2]), I(args[3]), I(args[4]), I(args[5]))),
                ["Fill"] = args => Nothing(() => mgr.Fill(I(args[0]), I(args[1]))),
                ["FillCircle"] = args => Nothing(() => mgr.FillCircle(I(args[0]), I(args[1]), I(args[2]), I(args[3]), I(args[4]))),
                ["FillEllipse"] = args => Nothing(() => mgr.FillEllipse(I(args[0]), I(args[1]), I(args[2]), I(args[3]), I(args[4]), I(args[5]))),
                ["FillTriangle"] = args => Nothing(() => mgr.FillTriangle(I(args[0]), I(args[1]), I(args[2]), I(args[3]), I(args[4]), I(args[5]), I(args[6]), I(args[7]))),
                ["FillPolygon"] = args => Nothing(() => mgr.FillPolygon(I(args[0]), ReadPoints(args[1]), I(args[2]))),
                ["FloodFill"] = args => Nothing(() => mgr.FloodFill(I(args[0]), I(args[1]), I(args[2]), I(args[3]))),
                ["FloodFillBorder"] = args => Nothing(() => mgr.FloodFillBorder(I(args[0]), I(args[1]), I(args[2]), I(args[3]), I(args[4]))),
                ["DrawPoint"] = args => Nothing(() => mgr.DrawPoint(I(args[0]), I(args[1]), I(args[2]), I(args[3]))),
                ["DrawLine"] = args => Nothing(() => mgr.DrawLine(I(args[0]), I(args[1]), I(args[2]), I(args[3]), I(args[4]), I(args[5]))),
                ["DrawPath"] = args => Nothing(() => mgr.DrawPath(I(args[0]), ReadPoints(args[1]), I(args[2]), args[3].AsBool())),
                ["DrawRect"] = args => Nothing(() => mgr.DrawRect(I(args[0]), I(args[1]), I(args[2]), I(args[3]), I(args[4]), I(args[5]))),
                ["DrawCircle"] = args => Nothing(() => mgr.DrawCircle(I(args[0]), I(args[1]), I(args[2]), I(args[3]), I(args[4]))),
                ["DrawEllipse"] = args => Nothing(() => mgr.DrawEllipse(I(args[0]), I(args[1]), I(args[2]), I(args[3]), I(args[4]), I(args[5]))),
                ["DrawTriangle"] = args => Nothing(() => mgr.DrawTriangle(I(args[0]), I(args[1]), I(args[2]), I(args[3]), I(args[4]), I(args[5]), I(args[6]), I(args[7]))),
                ["DrawPolygon"] = args => Nothing(() => mgr.DrawPolygon(I(args[0]), ReadPoints(args[1]), I(args[2]), args[3].AsBool())),
                ["SetClip"] = args => Nothing(() => mgr.SetClip(I(args[0]), I(args[1]), I(args[2]), I(args[3]), I(args[4]))),
                ["ResetClip"] = args => Nothing(() => mgr.ResetClip(I(args[0]))),
                ["Blit"] = args => Nothing(() => mgr.Blit(I(args[0]), I(args[1]), I(args[2]), I(args[3]), I(args[4]), I(args[5]), I(args[6]), I(args[7]), I(args[8]), I(args[9]), I(args[10]), I(args[11]))),
            });
        }

        private static Dictionary<string, NativeFunction> BuildBrushFunctions(RendererManager mgr)
        {
            return Ordered(BrushFunctionNames, new Dictionary<string, NativeFunction>
            {
                ["CreateSolid"] = args =>
                {
                    try { return Value.MakeInt(mgr.CreateSolidBrush(I(args[0]))); }
                    catch { return Value.MakeInt(InvalidHandle); }
                },
                ["Destroy"] = args => Value.MakeBool(mgr.DestroyBrush(I(args[0]))),
                ["GetColor"] = args => Value.MakeInt((uint)mgr.GetBrushColor(I(args[0]))),
                ["SetColor"] = args => Nothing(() => mgr.SetBrushColor(I(args[0]), I(args[1]))),
            });
        }

        private static Dictionary<string, NativeFunction> BuildPenFunctions(RendererManager mgr)
        {
            return Ordered(PenFunctionNames, new Dictionary<string, NativeFunction>
            {
                ["Create"] = args =>
                {
                    try { return Value.MakeInt(mgr.CreatePen(I(args[0]), I(args[1]), I(args[2]))); }
                    catch { return Value.MakeInt(InvalidHandle); }
                },
                ["Destroy"] = args => Value.MakeBool(mgr.DestroyPen(I(args[0]))),
                ["GetColor"] = args => Value.MakeInt((uint)mgr.GetPenColor(I(args[0]))),
                ["SetColor"] = args => Nothing(() => mgr.SetPenColor(I(args[0]), I(args[1]))),
                ["GetWidth"] = args => Value.MakeInt(mgr.GetPenWidth(I(args[0]))),
                ["SetWidth"] = args => Nothing(() => mgr.SetPenWidth(I(args[0]), I(args[1]))),
                ["GetShape"] = args => Value.MakeInt(mgr.GetPenShape(I(args[0]))),
                ["SetShape"] = args => Nothing(() => mgr.SetPenShape(I(args[0]), I(args[1]))),
            });
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

                // Ein Bild aus einer eingebetteten Ressource (`Framebuffer.FromResource(new Resource("images/logo.png"))`): die Datei steckt im Programm.
                static Framebuffer FromResource(resource, int mode = -1) {
                    return Framebuffer.FromImage(resource.Bytes(), mode)
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
                        var path = new ToolPath(entry[0], entry[1], entry[2])
                        path.TakeTo(paths)   // (the paths belong to the list, not to the loop body)
                        paths.Add(path)
                    }
                    return paths
                }
            }

            // Die Form der Stiftspitze: Round = Kreisscheibe mit dem Durchmesser der Breite, Square = Quadrat der Breite.
            enum PenShape {
                Round = 0,
                Square = 1
            }

            // Ein Pinsel sagt, wie eine Fläche gefüllt wird (Renderer.FillRect, FillCircle, FloodFill, ... und der Text-Vordergrund). Die Farbe ist eine Zahl: 0 bis 255 ist ein Index der
            // Palette des Framebuffers, jede andere ein direkter Wert r + g*256 + b*65536 + a*16777216 (a = 255 deckend; siehe UI.Color.Rgb). Ein Alpha unter 255 wird gemischt
            // (Renderer.AlphaBlending); in einem Palette-Framebuffer wird eine Farbe ab Alpha 128 kopiert und eine darunter nicht gezeichnet.
            class Brush {
                int id

                construct(int id) {
                    this.id = id
                }

                destruct() {
                    if (this.id > 0) {
                        __GRPHBshDestroy(this.id)
                    }
                }
            }

            // Ein einfarbiger Pinsel.
            class SolidBrush : Brush {
                construct(int color) : base(__GRPHBshCreateSolid(color)) {
                    if (this.id == -1) {
                        throw new HandleUnavailableException("The brush could not be created.")
                    }
                }

                // Die Farbe (eine Änderung gilt für alles, was danach mit diesem Pinsel gezeichnet wird)
                int Color {
                    get { return __GRPHBshGetColor(this.id) }
                    set { __GRPHBshSetColor(this.id, value) }
                }
            }

            // Ein Stift zeichnet Punkte, Linien und Pfade (und damit die Umrisse der Formen) mit einer Spitze von `width` Pixeln; Farbe wie beim Pinsel. Die Spitze wird einmal
            // vorgerendert und an jedem Pixel der Linie kopiert bzw. gemischt; width 1 zeichnet genau die Pixel einer einfachen Linie.
            class Pen {
                int id

                construct(int color, int width = 1, int shape = 0) {
                    this.id = __GRPHPenCreate(color, width, shape)
                    if (this.id == -1) {
                        throw new HandleUnavailableException("The pen could not be created.")
                    }
                }

                destruct() {
                    if (this.id > 0) {
                        __GRPHPenDestroy(this.id)
                    }
                }

                int Color {
                    get { return __GRPHPenGetColor(this.id) }
                    set { __GRPHPenSetColor(this.id, value) }
                }

                // 1 bis 512 (größere Werte werden begrenzt)
                int Width {
                    get { return __GRPHPenGetWidth(this.id) }
                    set { __GRPHPenSetWidth(this.id, value) }
                }

                // PenShape.Round oder PenShape.Square
                int Shape {
                    get { return __GRPHPenGetShape(this.id) }
                    set { __GRPHPenSetShape(this.id, value) }
                }
            }

            // Der Renderer zeichnet in einen Framebuffer: Terminal-Text (Print, Locate, SetColor) und Grafik in PIXEL-Koordinaten. Füllungen nehmen einen Brush, Zeichnen einen Pen.
            // Alles wird am Rand des Framebuffers beschnitten.
            class Renderer {
                int id

                construct(Framebuffer framebuffer) {
                    this.id = __GRPHRndCreate(framebuffer.id)
                    if (this.id == -1) {
                        throw new HandleUnavailableException("The renderer could not be created.")
                    }
                }

                destruct() {
                    __GRPHRndDestroy(this.id)
                }

                // Alpha-Blending (Vorgabe: an): eine Farbe mit Alpha unter 255 wird mit dem gemischt, was schon da ist - nur in einem RGBA-Framebuffer. Aus: jede Farbe wird samt Alpha
                // kopiert. Im Palette-Framebuffer wird eine Farbe ab Alpha 128 kopiert und eine darunter nicht gezeichnet; aus: immer kopiert.
                bool AlphaBlending {
                    get { return __GRPHRndGetAlphaBlending(this.id) }
                    set { __GRPHRndSetAlphaBlending(this.id, value) }
                }

                // Beschränkt das Zeichnen (Füllungen, Formen, Text, Blit) auf das Rechteck (x, y, w, h) des Framebuffers; ResetClip hebt es auf (wieder der ganze Framebuffer).
                // Es gilt nicht für Clear/ClearTo.
                SetClip(int x, int y, int w, int h) { __GRPHRndSetClip(this.id, x, y, w, h) }
                ResetClip() { __GRPHRndResetClip(this.id) }

                // ---- Terminal ----
                Print(string text) { __GRPHRndPrint(this.id, text) }
                Locate(int row, int column) { __GRPHRndLocate(this.id, row, column) }
                // Löscht den ganzen Framebuffer mit der Hintergrundfarbe von SetColor und setzt den Cursor auf (0, 0)
                Clear() { __GRPHRndClear(this.id) }
                // Setzt den ganzen Framebuffer auf eine Farbe (ohne Mischen, auch mit Alpha)
                ClearTo(int color) { __GRPHRndClearTo(this.id, color) }
                SetColor(int foreground, int background) { __GRPHRndSetColor(this.id, foreground, background) }

                // ---- Pixel ----
                // Ein Pixel in der Farbe (Zahl wie beim Pinsel; gemischt, wenn sie halbdurchsichtig ist und AlphaBlending an)
                SetPixel(int x, int y, int color) { __GRPHRndSetPixel(this.id, x, y, color) }
                // Die Farbe des Pixels als 32-Bit-Zahl MIT Vorzeichen (deckende Farben sind negativ)
                int GetPixel(int x, int y) { return __GRPHRndGetPixel(this.id, x, y) }
                // Der Palette-Index des Pixels (RGBA-Framebuffer: der Eintrag, der seiner Farbe am nächsten kommt)
                int GetPixelIndex(int x, int y) { return __GRPHRndGetPixelIndex(this.id, x, y) }
                int CellWidth() { return __GRPHRndCellWidth(this.id) }
                int CellHeight() { return __GRPHRndCellHeight(this.id) }

                // Text an Pixel-Koordinaten: die Zeichen-Pixel mit `foreground`, mit `background` die ganze Zelle darunter (ohne: nur die Zeichen-Pixel)
                DrawText(int x, int y, string text, Brush foreground, Brush background = undefined) {
                    var back = 0
                    if (background != undefined) { back = background.id }
                    __GRPHRndDrawText(this.id, x, y, text, foreground.id, back)
                }

                // ---- Füllungen (Pinsel) ----
                FillRect(int x, int y, int w, int h, Brush brush) { __GRPHRndFillRect(this.id, x, y, w, h, brush.id) }
                // Füllt den ganzen Framebuffer (gemischt, wenn AlphaBlending an; ClearTo setzt ohne Mischen)
                Fill(Brush brush) { __GRPHRndFill(this.id, brush.id) }
                // Kreis um (cx, cy) mit Radius r, Ellipse mit den Halbachsen rx (waagerecht) und ry (senkrecht), samt Rand
                FillCircle(int cx, int cy, int r, Brush brush) { __GRPHRndFillCircle(this.id, cx, cy, r, brush.id) }
                FillEllipse(int cx, int cy, int rx, int ry, Brush brush) { __GRPHRndFillEllipse(this.id, cx, cy, rx, ry, brush.id) }
                FillTriangle(int x0, int y0, int x1, int y1, int x2, int y2, Brush brush) { __GRPHRndFillTriangle(this.id, x0, y0, x1, y1, x2, y2, brush.id) }
                // points: ein Array [x0, y0, x1, y1, ...]; gefüllt nach der Even-Odd-Regel (sich überschneidende Teile bleiben leer)
                FillPolygon(points, Brush brush) { __GRPHRndFillPolygon(this.id, points, brush.id) }
                // Füllt die zusammenhängende Fläche mit der Farbe des Pixels (x, y). Mit `border` füllt FloodFill stattdessen bis zu Pixeln dieser Farbe (wie PAINT in QBasic).
                FloodFill(int x, int y, Brush brush) { __GRPHRndFloodFill(this.id, x, y, brush.id) }
                FloodFillBorder(int x, int y, Brush brush, int border) { __GRPHRndFloodFillBorder(this.id, x, y, brush.id, border) }

                // ---- Zeichnen (Stift) ----
                DrawPoint(int x, int y, Pen pen) { __GRPHRndDrawPoint(this.id, x, y, pen.id) }
                DrawLine(int x0, int y0, int x1, int y1, Pen pen) { __GRPHRndDrawLine(this.id, x0, y0, x1, y1, pen.id) }
                // Ein Pfad durch die Punkte [x0, y0, x1, y1, ...]; closed verbindet den letzten mit dem ersten
                DrawPath(points, Pen pen, bool closed = false) { __GRPHRndDrawPath(this.id, points, pen.id, closed) }
                // Umrisse: Pfade aus den Pixeln der Form
                DrawRect(int x, int y, int w, int h, Pen pen) { __GRPHRndDrawRect(this.id, x, y, w, h, pen.id) }
                DrawCircle(int cx, int cy, int r, Pen pen) { __GRPHRndDrawCircle(this.id, cx, cy, r, pen.id) }
                DrawEllipse(int cx, int cy, int rx, int ry, Pen pen) { __GRPHRndDrawEllipse(this.id, cx, cy, rx, ry, pen.id) }
                DrawTriangle(int x0, int y0, int x1, int y1, int x2, int y2, Pen pen) { __GRPHRndDrawTriangle(this.id, x0, y0, x1, y1, x2, y2, pen.id) }
                DrawPolygon(points, Pen pen, bool closed = true) { __GRPHRndDrawPolygon(this.id, points, pen.id, closed) }

                // ---- Kopieren ----
                // Kopiert einen anderen Framebuffer (z.B. ein geladenes Bild) hierher, auch zwischen den Farbmodi (siehe BlitMode; Blend mischt nur bei AlphaBlending). Blit: das ganze Bild
                // mit der linken oberen Ecke bei (x, y); BlitRegion: der Ausschnitt (sx, sy, sw, sh) nach (dx, dy); BlitScaled: dazu auf die Größe dw x dh gebracht (nächster Nachbar;
                // eine NEGATIVE Breite/Höhe spiegelt). `key`: bei einem Palette-Bild der durchsichtige Index (-1 = sein TransparentIndex).
                Blit(Framebuffer source, int x, int y, int mode = 0, int key = -1) {
                    __GRPHRndBlit(this.id, source.id, 0, 0, source.Width(), source.Height(), x, y, source.Width(), source.Height(), mode, key)
                }
                BlitRegion(Framebuffer source, int sx, int sy, int sw, int sh, int dx, int dy, int mode = 0, int key = -1) {
                    __GRPHRndBlit(this.id, source.id, sx, sy, sw, sh, dx, dy, sw, sh, mode, key)
                }
                BlitScaled(Framebuffer source, int sx, int sy, int sw, int sh, int dx, int dy, int dw, int dh, int mode = 0, int key = -1) {
                    __GRPHRndBlit(this.id, source.id, sx, sy, sw, sh, dx, dy, dw, dh, mode, key)
                }
            }
            """;
    }
}
