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
    /// The bridge between fire and the graphics API (see docs/
    /// CONSOLE.md): registers FramebufferManager/RendererManager
    /// as native functions (via NativeRegistry.
    /// RegisterGroup, each with its own name prefix) and supplies matching
    /// fire source (<see cref="PreludeSource"/>) that
    /// hides these native functions behind ordinary classes
    /// (Framebuffer/Console/Slicer) - the window (`Window`) lives separately in fire.Windows.Bridge (`#import "windows"`) - script code never sees a
    /// raw ID, only normal objects with normal methods.
    ///
    /// Deliberately kept "coarse" (see the request) - not every manager method
    /// is mapped, only a representative selection (create/destroy
    /// plus the most common operations per resource kind). Further methods
    /// can be added following the same pattern: register a native function in the
    /// matching Build*Functions method, add a matching fire
    /// method to PreludeSource, which passes `this.id` along
    /// automatically.
    /// </summary>
    public static class GraphicsBridge
    {
        public const string FramebufferPrefix = "__GRPHFb";
        public const string RendererPrefix = "__GRPHRnd";
        public const string BrushPrefix = "__GRPHBsh";
        public const string PenPrefix = "__GRPHPen";
        public const string SlicerPrefix = "__GRPHSlc";
        public const string FontPrefix = "__GRPHFnt";

        /// <summary>Invalid/failed creation - IdManager always assigns
        /// real IDs from 1 upwards (see there), so -1 can safely be told apart
        /// from any real ID as a sentinel. The
        /// Create wrappers below catch EVERY exception from the respective
        /// manager and return this sentinel instead of letting the exception
        /// break through the VM raw - PreludeSource checks
        /// for it and throws a real HandleUnavailableException,
        /// catchable with `try`/`catch`, in its place (see there).</summary>
        public const int InvalidHandle = -1;

        /// <summary>`readFile`: how `Framebuffer.FromFile` gets to the bytes of a file (the host decides what a script may read, see IoPolicy) -
        /// without it the file is simply read.</summary>
        public static void RegisterAll(
            NativeRegistry natives, FramebufferManager framebuffers, RendererManager renderers, Func<string, byte[]>? readFile = null)
        {
            natives.RegisterGroup(FramebufferPrefix, BuildFramebufferFunctions(framebuffers, readFile));
            natives.RegisterGroup(RendererPrefix, BuildRendererFunctions(renderers));
            natives.RegisterGroup(BrushPrefix, BuildBrushFunctions(renderers));
            natives.RegisterGroup(PenPrefix, BuildPenFunctions(renderers));
            natives.RegisterGroup(SlicerPrefix, BuildSlicerFunctions(framebuffers));
            natives.RegisterGroup(FontPrefix, BuildFontFunctions(renderers.Fonts));
        }

        public static void RegisterStubs(
                    NativeRegistry natives)
        {
            natives.RegisterGroup(FramebufferPrefix, BuildFramebufferFunctionStubs());
            natives.RegisterGroup(RendererPrefix, StubsOf(RendererFunctionNames));
            natives.RegisterGroup(BrushPrefix, StubsOf(BrushFunctionNames));
            natives.RegisterGroup(PenPrefix, StubsOf(PenFunctionNames));
            natives.RegisterGroup(SlicerPrefix, new Dictionary<string, NativeFunction> { ["Slice"] = args => Value.MakeUndefined() /*STUB*/ });
            natives.RegisterGroup(FontPrefix, StubsOf(FontFunctionNames));
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
                // IMPORTANT: always add new functions AT THE END, in BuildFramebufferFunctions in the same order (index = position).
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
                ["Resize"] = args => Value.MakeUndefined() /*STUB*/,
            };
        }

        /// <summary>The error text of the last failed image function of this thread (see LastError).</summary>
        [ThreadStatic] private static string? t_lastError;

        private static int I(Value v) => (int)v.AsInt();

        /// <summary>A number as a double (a script may pass `3` instead of `3.0`).</summary>
        private static double D(Value v) => v.Kind == ValueKind.Float ? v.AsFloat() : v.AsInt();

        /// <summary>-1 (or less) = "like the image", otherwise the colour mode.</summary>
        private static ColorMode? ModeArg(Value v)
        {
            long m = v.AsInt();
            return m < 0 ? null : (ColorMode)m;
        }

        /// <summary>Runs an image function that creates a framebuffer: the error text goes to LastError, the result is then InvalidHandle
        /// (the prelude turns that into a catchable ImageException).</summary>
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

        /// <summary>Runs an action that may fail on unsuitable arguments (size, range): the error text goes to LastError, the result is false.</summary>
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
                // IMPORTANT: always add new functions AT THE END, in BuildFramebufferFunctionStubs in the same order (index = position).
                ["Mode"] = args => Value.MakeInt((int)mgr.GetMode(I(args[0]))),
                ["ByteCount"] = args => Value.MakeInt(mgr.GetByteCount(I(args[0]))),
                ["ReadBytes"] = args => Value.MakeBuffer(new ByteBuffer(mgr.ReadBytes(I(args[0])), ByteConversions.HostByteOrder)),
                // false for unsuitable data (reason: LastError), the prelude turns that into a GraphicsException
                ["WriteBytes"] = args => Value.MakeBool(Succeeded(() => mgr.WriteBytes(I(args[0]), args[1].AsBuffer().Bytes))),
                // Palette colours are UNSIGNED values (r + g*256 + b*65536 + a*16777216), unlike GetPixel; -1 for an invalid index
                ["GetPaletteColor"] = args =>
                {
                    long color = -1;
                    return Succeeded(() => color = (uint)mgr.GetPaletteColor(I(args[0]), I(args[1]))) ? Value.MakeInt(color) : Value.MakeInt(-1);
                },
                ["SetPaletteColor"] = args => Value.MakeBool(Succeeded(() => mgr.SetPaletteColor(I(args[0]), I(args[1]), I(args[2])))),
                ["ReadPalette"] = args => Value.MakeBuffer(new ByteBuffer(mgr.ReadPalette(I(args[0]), args[1].AsBool()), ByteConversions.HostByteOrder)),
                ["WritePalette"] = args => Value.MakeBool(Succeeded(() => mgr.WritePalette(I(args[0]), args[1].AsBuffer().Bytes))),
                // Images: return the ID of the new framebuffer or InvalidHandle (reason: LastError)
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
                // false for an invalid size (the framebuffer stays as it was)
                ["Resize"] = args => Value.MakeBool(mgr.Resize(I(args[0]), I(args[1]), I(args[2]))),
            };
        }

        /// <summary>The paths of the ImageSlicer for fire: one array per path `[kind, closed, points]` (kind 0 = Fill, 1 = Outline; points = flat array
        /// `[x0, y0, x1, y1, ...]` in mm). -1 for invalid arguments (reason: LastError); the prelude turns that into `Slicer.Slice`.</summary>
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

        // The names of the functions of each group in their order (index = position): the stubs (only for the compiler, which has to know the natives) and the real functions
        // must match - that is why they exist only here and BuildXFunctions checks them.
        private static readonly string[] RendererFunctionNames =
        {
            "Create", "Destroy", "Print", "Locate", "Clear", "ClearTo", "SetColor", "SetPixel", "GetPixel", "GetPixelIndex", "CellWidth", "CellHeight",
            "GetAlphaBlending", "SetAlphaBlending", "DrawText",
            "FillRect", "Fill", "FillCircle", "FillEllipse", "FillTriangle", "FillPolygon", "FloodFill", "FloodFillBorder",
            "DrawPoint", "DrawLine", "DrawPath", "DrawRect", "DrawCircle", "DrawEllipse", "DrawTriangle", "DrawPolygon",
            "Blit", "SetClip", "ResetClip", "DrawTextFont",
        };

        private static readonly string[] FontFunctionNames = { "Load", "Add", "Open", "Destroy", "Name", "IsBitmap", "Ascent", "Height", "Measure" };

        private static readonly string[] BrushFunctionNames = { "CreateSolid", "Destroy", "GetColor", "SetColor" };

        private static readonly string[] PenFunctionNames = { "Create", "Destroy", "GetColor", "SetColor", "GetWidth", "SetWidth", "GetShape", "SetShape" };

        private static Dictionary<string, NativeFunction> StubsOf(string[] names)
        {
            var stubs = new Dictionary<string, NativeFunction>();
            foreach (var name in names) stubs[name] = args => Value.MakeUndefined() /*STUB*/;
            return stubs;
        }

        /// <summary>Makes sure that the real functions have exactly the names (and the order) of the stubs.</summary>
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
                // a number 0-255 is a palette index, any other is a direct value (see Paint.FromArgument)
                ["ClearTo"] = args => Nothing(() => mgr.ClearTo(I(args[0]), I(args[1]))),
                ["SetColor"] = args => Nothing(() => mgr.SetColor(I(args[0]), I(args[1]), I(args[2]))),
                ["SetPixel"] = args => Nothing(() => mgr.SetPixel(I(args[0]), I(args[1]), I(args[2]), I(args[3]))),
                ["GetPixel"] = args => Value.MakeInt(mgr.GetPixel(I(args[0]), I(args[1]), I(args[2]))),
                ["GetPixelIndex"] = args => Value.MakeInt(mgr.GetPixelIndex(I(args[0]), I(args[1]), I(args[2]))),
                ["CellWidth"] = args => Value.MakeInt(mgr.GetCellWidth(I(args[0]))),
                ["CellHeight"] = args => Value.MakeInt(mgr.GetCellHeight(I(args[0]))),
                ["GetAlphaBlending"] = args => Value.MakeBool(mgr.GetAlphaBlending(I(args[0]))),
                ["SetAlphaBlending"] = args => Nothing(() => mgr.SetAlphaBlending(I(args[0]), args[1].AsBool())),
                // Brushes and pens arrive as an ID; for text, 0 as the background means "no background"
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
                // text in a font: font ID 0 = the font of the renderer, size in pixels
                ["DrawTextFont"] = args => Nothing(() => mgr.DrawTextFont(I(args[0]), I(args[1]), I(args[2]), args[3].AsString(), I(args[4]), I(args[5]), I(args[6]), I(args[7]))),
                ["Blit"] = args => Nothing(() => mgr.Blit(I(args[0]), I(args[1]), I(args[2]), I(args[3]), I(args[4]), I(args[5]), I(args[6]), I(args[7]), I(args[8]), I(args[9]), I(args[10]), I(args[11]))),
            });
        }

        /// <summary>The fonts (see <see cref="FontManager"/>): IDs of fonts, 0 = the font of the renderer, -1 = none.</summary>
        private static Dictionary<string, NativeFunction> BuildFontFunctions(FontManager fonts)
        {
            return Ordered(FontFunctionNames, new Dictionary<string, NativeFunction>
            {
                ["Load"] = args => Value.MakeInt(fonts.Load(args[0].AsBuffer().Bytes)),
                ["Add"] = args => Value.MakeInt(fonts.Add(args[0].AsBuffer().Bytes, args[1].AsString())),
                ["Open"] = args => Value.MakeInt(fonts.Open(args[0].AsString())),
                ["Destroy"] = args => Value.MakeBool(fonts.Destroy(I(args[0]))),
                ["Name"] = args => Value.MakeString(fonts.Get(I(args[0])).Name),
                ["IsBitmap"] = args => Value.MakeBool(fonts.Get(I(args[0])).IsBitmap),
                ["Ascent"] = args => Value.MakeInt(fonts.Get(I(args[0])).Ascent(I(args[1]))),
                ["Height"] = args => Value.MakeInt(fonts.Get(I(args[0])).LineHeight(I(args[1]))),
                ["Measure"] = args => Value.MakeInt(fonts.Get(I(args[0])).Measure(args[2].AsString(), I(args[1]))),
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

        /// <summary>The points of a polygon from a script array `[x0, y0, x1, y1, ...]` (floating-point numbers are truncated; an odd last element does not count).</summary>
        private static int[] ReadPoints(Value array)
        {
            var items = array.AsArray().Items;
            var points = new int[items.Length - items.Length % 2];
            for (int i = 0; i < points.Length; i++)
                points[i] = items[i].Kind == ValueKind.Float ? (int)items[i].AsFloat() : (int)items[i].AsInt();
            return points;
        }

        /// <summary>fire source that hides the native functions
        /// registered via <see cref="RegisterAll"/>
        /// behind three ordinary classes - to be placed BEFORE the actual user script (analogous
        /// to Standard.Prelude.Source, see Parser.ParseWithPrelude for the
        /// general pattern). Each class remembers its ID as a field and
        /// passes it on automatically as the first argument with every method to the
        /// corresponding native function - script code itself never sees
        /// a raw ID.</summary>
        public const string PreludeSource = """
            class HandleUnavailableException : Exception {
                string message

                construct(string message) {
                    this.message = message
                }
            }

            // How a framebuffer stores its pixels: Rgba = 4 bytes per pixel (every colour directly), Palette = 1 byte per pixel (an index into a
            // 256-colour palette; changing a palette entry recolours all pixels with that index).
            enum ColorMode {
                Rgba = 0,
                Palette = 1
            }

            // How Console.Blit deals with transparent pixels: Copy copies everything, Transparent leaves out transparent pixels of the source (RGBA: alpha 0,
            // Palette: the colour key or the TransparentIndex of the image), Blend mixes semi-transparent RGBA pixels according to their alpha value.
            enum BlitMode {
                Copy = 0,
                Transparent = 1,
                Blend = 2
            }

            // A graphics call with unsuitable data (e.g. a buffer of the wrong size, a palette index outside 0-255).
            class GraphicsException : Exception {
                string message

                construct(string message) {
                    this.message = message
                }
            }

            // An image could not be loaded (unknown format, damaged, file not readable or not allowed).
            class ImageException : GraphicsException {
                construct(string message) : base(message) { }
            }

            class Framebuffer {
                int id

                // mode: ColorMode.Rgba (default) or ColorMode.Palette. (mode -2 is internal: an empty shell for the factory methods below.)
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

                // Load an image: PNG, BMP or GIF - as the content of a file (FromImage: a byte buffer with the bytes of the file) or from a path
                // (FromFile; the host decides what may be read). The framebuffer has the size of the image. mode -1 (default): like the image -
                // an image with a palette (PNG with palette, GIF, BMP up to 8 bit) becomes a palette framebuffer with the palette of the file, everything else
                // RGBA; ColorMode.Rgba / ColorMode.Palette forces a mode (an RGBA image is then mapped onto the default palette).
                // Error: ImageException.
                static Framebuffer FromImage(data, int mode = -1) {
                    var fb = new Framebuffer(0, 0, -2)
                    fb.id = __GRPHFbLoadImage(data, mode)
                    if (fb.id < 0) {
                        throw new ImageException(__GRPHFbLastError())
                    }
                    return fb
                }

                // An image from an embedded resource (`Framebuffer.FromResource(new Resource("images/logo.png"))`): the file is inside the program.
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

                // Raw pixels from a byte buffer, row by row from top to bottom: ColorMode.Rgba width*height*4 bytes (R, G, B, A per pixel),
                // ColorMode.Palette width*height bytes (one index per pixel) and optionally a palette (768 bytes RGB or 1024 bytes RGBA).
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

                // Brings the framebuffer to a new size: the content is kept at the top left, what is added is transparent (palette: index 0). Valid are both sides from 1 to 16384
                // and at most 64 million pixels; for any other size (for example 0 for a minimised window) it stays as it is, and the answer is false.
                // A console (renderer) on this framebuffer follows the new size; pointers to the pixels (natives) are no longer valid afterwards.
                bool Resize(int width, int height) { return __GRPHFbResize(this.id, width, height) }
                int Mode() { return __GRPHFbMode(this.id) }

                // Raw data: RGBA 4 bytes per pixel (R, G, B, A), palette 1 byte per pixel (the index); ByteCount() is the size.
                int ByteCount() { return __GRPHFbByteCount(this.id) }
                int ReadByte(int offset) { return __GRPHFbReadByte(this.id, offset) }
                WriteByte(int offset, int value) { __GRPHFbWriteByte(this.id, offset, value) }
                ReadBytes() { return __GRPHFbReadBytes(this.id) }
                WriteBytes(data) {
                    if (!__GRPHFbWriteBytes(this.id, data)) {
                        throw new GraphicsException(__GRPHFbLastError())
                    }
                }

                // The 256-colour palette (for a palette framebuffer it is the colour table of the image, for an RGBA framebuffer it resolves palette indices
                // that are given as a colour). Colours are r + g*256 + b*65536 + a*16777216 (alpha 255 = opaque).
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
                // 768 bytes (R, G, B per entry) or, with withAlpha, 1024 bytes (R, G, B, A)
                ReadPalette(bool withAlpha = false) { return __GRPHFbReadPalette(this.id, withAlpha) }
                WritePalette(data) {
                    if (!__GRPHFbWritePalette(this.id, data)) {
                        throw new GraphicsException(__GRPHFbLastError())
                    }
                }

                // The mask of this image as a NEW palette framebuffer of the same size: index 1 (white) = this pixel is to be milled out (see Slicer),
                // index 0 (black) is not. Pixels with a lower opacity than alphaThreshold never count; otherwise the brightness against threshold decides
                // (0-255): darkIsRemoved = dark pixels are milled out, false = bright ones.
                Framebuffer ToMask(int threshold = 128, bool darkIsRemoved = true, int alphaThreshold = 128) {
                    var mask = new Framebuffer(0, 0, -2)
                    mask.id = __GRPHFbToMask(this.id, threshold, darkIsRemoved, alphaThreshold)
                    if (mask.id < 0) {
                        throw new GraphicsException(__GRPHFbLastError())
                    }
                    return mask
                }

                // Palette framebuffer: the index that counts as transparent in an image (GIF/PNG), -1 = none. Console.Blit with BlitMode.Transparent skips it.
                int TransparentIndex {
                    get { return __GRPHFbGetTransparentIndex(this.id) }
                    set { __GRPHFbSetTransparentIndex(this.id, value) }
                }
            }

            // Kind of a tool path: Fill = clearing the area, Outline = finishing contour at the edge.
            enum PathKind {
                Fill = 0,
                Outline = 1
            }

            // How the inside of an area is cleared: Contour = concentric, contour-parallel paths (from the inside out), ZigZag = horizontal
            // zigzag paths plus edge contour, OutlineOnly = only the edge contour.
            enum FillStrategy {
                Contour = 0,
                ZigZag = 1,
                OutlineOnly = 2
            }

            // A tool path (polyline of the tool centre) in millimetres.
            class ToolPath {
                int kind          // PathKind
                bool closed       // true: the last point is connected to the first
                points            // flat array [x0, y0, x1, y1, ...]

                construct(int kind, bool closed, points) {
                    this.kind = kind
                    this.closed = closed
                    this.points = points
                }

                int Count() { return this.points.length / 2 }
                float X(int index) { return this.points[index * 2] }
                float Y(int index) { return this.points[index * 2 + 1] }
            }

            // Splits a mask (see Framebuffer.ToMask) into tool paths with a fixed line width: Euclidean distance transform, the tool centre
            // may only lie where the distance to the edge is at least the line radius; the isoline there is the edge contour (subpixel-accurate), the inside
            // is filled with paths at the distance StepOver. All coordinates in millimetres, the centre of the tool.
            class Slicer {
                float lineWidth        // Line width or tool diameter in mm
                float pixelSize        // Edge length of a pixel of the mask in mm (e.g. 25.4 / dpi)
                float overlap = 0.5    // Overlap of neighbouring paths as a fraction of the line width (0 to 0.95); from 0.5 on, Contour leaves no residual islands
                int strategy = 0       // FillStrategy (default Contour)
                float simplifyTolerance = -1.0   // Tolerance of the point reduction in mm; negative = automatic (1/4 pixel), 0 = none
                bool flipY = true      // true: Y points up (machine coordinates), false: down, as in the image

                construct(float lineWidth, float pixelSize) {
                    if (lineWidth <= 0 || pixelSize <= 0) {
                        throw new GraphicsException("Line thickness and pixel size must be greater than 0.")
                    }
                    this.lineWidth = lineWidth
                    this.pixelSize = pixelSize
                }

                // Distance between adjacent tracks in mm
                float StepOver { get { return this.lineWidth * (1.0 - this.overlap) } }

                // Returns a List of ToolPath (empty if no place is wide enough for the line width). The mask: a framebuffer whose set pixels
                // (palette: index not 0, RGBA: visible and not black) are milled out - usually from Framebuffer.ToMask.
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

            // The shape of the pen tip: Round = a disc with the diameter of the width, Square = a square of the width.
            enum PenShape {
                Round = 0,
                Square = 1
            }

            // A brush says how an area is filled (Renderer.FillRect, FillCircle, FloodFill, ... and the text foreground). The colour is a number: 0 to 255 is an index of the
            // framebuffer's palette, any other is a direct value r + g*256 + b*65536 + a*16777216 (a = 255 opaque; see UI.Color.Rgb). An alpha below 255 is blended
            // (Renderer.AlphaBlending); in a palette framebuffer a colour from alpha 128 up is copied and one below it is not drawn.
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

            // A single-colour brush.
            class SolidBrush : Brush {
                construct(int color) : base(__GRPHBshCreateSolid(color)) {
                    if (this.id == -1) {
                        throw new HandleUnavailableException("The brush could not be created.")
                    }
                }

                // The colour (a change applies to everything drawn with this brush afterwards)
                int Color {
                    get { return __GRPHBshGetColor(this.id) }
                    set { __GRPHBshSetColor(this.id, value) }
                }
            }

            // A pen draws points, lines and paths (and thus the outlines of the shapes) with a tip of `width` pixels; colour as with the brush. The tip is rendered once
            // beforehand and copied or blended at every pixel of the line; width 1 draws exactly the pixels of a simple line.
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

                // 1 to 512 (larger values are limited)
                int Width {
                    get { return __GRPHPenGetWidth(this.id) }
                    set { __GRPHPenSetWidth(this.id, value) }
                }

                // PenShape.Round or PenShape.Square
                int Shape {
                    get { return __GRPHPenGetShape(this.id) }
                    set { __GRPHPenSetShape(this.id, value) }
                }
            }

            // A font for text at pixel positions (Renderer.DrawText): the console font of the renderer (ID 0), one of the two built-in bitmap fonts ("8x14", "8x8") or a TrueType font
            // (.ttf). Get fonts with Fonts.Get / Fonts.Add - a Font is only a name for the font that the program holds; releasing it is not needed.
            class Font {
                int id

                construct(int id) {
                    this.id = id
                }

                // The name of the font (for TrueType its full name, "console" for the font of the renderer)
                string Name() {
                    if (this.id == 0) { return "console" }
                    return __GRPHFntName(this.id)
                }

                // Is it a bitmap font (fixed size, the size of the text is ignored)?
                bool IsBitmap() {
                    if (this.id == 0) { return true }
                    return __GRPHFntIsBitmap(this.id)
                }

                // Frees a font that was made with Fonts.FromBytes or Fonts.Add (the built-in fonts stay); later use of it is an error.
                Release() {
                    if (this.id > 0) {
                        __GRPHFntDestroy(this.id)
                    }
                }
            }

            // Finding fonts. A TrueType font comes from an embedded file (`Fonts.Add(new Resource("fonts/Roboto.ttf"))`), from bytes, or from the system.
            // Fonts.Get(name) looks the name up, in this order: "" or "console" is the font of the renderer; "8x14" and "8x8" the built-in bitmap fonts;
            // a font that was added with Fonts.Add (by its alias, the file name without folder and extension, its family name or its full name); a font installed on the system
            // (the font folders of the system and the folders in the environment variable FIRE_FONT_DIRS, separated by ';'; by file name, full name or family name);
            // otherwise the console font. Names are compared without case, spaces and punctuation ("DejaVu Sans" = "dejavusans" = "DejaVuSans").
            class Fonts {
                // Reads a TrueType font from an embedded file and makes it findable by name (see above); `alias` is an additional name. Error: GraphicsException if the data is no TrueType font
                // (outlines in the "glyf" format; OpenType fonts with PostScript outlines are not supported).
                static Font Add(resource, string alias = "") {
                    var name = alias
                    if (name == "") { name = resource.Name() }
                    var id = __GRPHFntAdd(resource.Bytes(), name)
                    if (id < 0) {
                        throw new GraphicsException("The file '" + resource.Name() + "' is not a TrueType font.")
                    }
                    return new Font(id)
                }

                // Reads a TrueType font from the bytes of a file; it has no name to look it up by.
                static Font FromBytes(data) {
                    var id = __GRPHFntLoad(data)
                    if (id < 0) {
                        throw new GraphicsException("The data is not a TrueType font.")
                    }
                    return new Font(id)
                }

                // The font with this name; the console font if there is none (so that text never fails because of a missing font).
                static Font Get(string name) {
                    var id = __GRPHFntOpen(name)
                    if (id < 0) { id = 0 }
                    return new Font(id)
                }

                // Is there a font with this name (other than the console font that Get falls back to)?
                static bool Has(string name) {
                    return __GRPHFntOpen(name) >= 0
                }

                // The font of the renderer.
                static Font Console() {
                    return new Font(0)
                }
            }

            // The renderer draws into a framebuffer: terminal text (Print, Locate, SetColor) and graphics in PIXEL coordinates. Fills take a Brush, drawing takes a Pen.
            // Everything is clipped at the edge of the framebuffer.
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

                // Alpha blending (default: on): a colour with alpha below 255 is blended with what is already there - only in an RGBA framebuffer. Off: every colour is copied including its alpha.
                // In a palette framebuffer a colour from alpha 128 up is copied and one below it is not drawn; off: always copied.
                bool AlphaBlending {
                    get { return __GRPHRndGetAlphaBlending(this.id) }
                    set { __GRPHRndSetAlphaBlending(this.id, value) }
                }

                // Restricts drawing (fills, shapes, text, blit) to the rectangle (x, y, w, h) of the framebuffer; ResetClip lifts it (the whole framebuffer again).
                // It does not apply to Clear/ClearTo.
                SetClip(int x, int y, int w, int h) { __GRPHRndSetClip(this.id, x, y, w, h) }
                ResetClip() { __GRPHRndResetClip(this.id) }

                // ---- Terminal ----
                Print(string text) { __GRPHRndPrint(this.id, text) }
                Locate(int row, int column) { __GRPHRndLocate(this.id, row, column) }
                // Clears the whole framebuffer with the background colour of SetColor and sets the cursor to (0, 0)
                Clear() { __GRPHRndClear(this.id) }
                // Sets the whole framebuffer to a colour (without blending, also with alpha)
                ClearTo(int color) { __GRPHRndClearTo(this.id, color) }
                SetColor(int foreground, int background) { __GRPHRndSetColor(this.id, foreground, background) }

                // ---- Pixel ----
                // A pixel in the colour (a number as with the brush; blended if it is semi-transparent and AlphaBlending is on)
                SetPixel(int x, int y, int color) { __GRPHRndSetPixel(this.id, x, y, color) }
                // The colour of the pixel as a SIGNED 32-bit number (opaque colours are negative)
                int GetPixel(int x, int y) { return __GRPHRndGetPixel(this.id, x, y) }
                // The palette index of the pixel (RGBA framebuffer: the entry that comes closest to its colour)
                int GetPixelIndex(int x, int y) { return __GRPHRndGetPixelIndex(this.id, x, y) }
                int CellWidth() { return __GRPHRndCellWidth(this.id) }
                int CellHeight() { return __GRPHRndCellHeight(this.id) }

                // Text at pixel coordinates: the character pixels with `foreground`, with `background` the whole cell (the line of text with a TrueType font) beneath (without: only the character pixels).
                // `font` (a Font, see Fonts.Get; without or the console font: the font of the renderer) and `size` (the height of the letters in pixels, 0 = 14; the two bitmap fonts ignore it) choose the font.
                DrawText(int x, int y, string text, Brush foreground, Brush background = undefined, font = undefined, int size = 0) {
                    var back = 0
                    if (background != undefined) { back = background.id }
                    var fontId = 0
                    if (font != undefined) { fontId = font.id }
                    if (fontId == 0) {
                        __GRPHRndDrawText(this.id, x, y, text, foreground.id, back)
                    } else {
                        __GRPHRndDrawTextFont(this.id, x, y, text, foreground.id, back, fontId, size)
                    }
                }

                // Width of `text` in pixels in the font (what DrawText would take); the console font is monospaced.
                int TextWidth(string text, font = undefined, int size = 0) {
                    var fontId = 0
                    if (font != undefined) { fontId = font.id }
                    if (fontId == 0) {
                        return text.Length * __GRPHRndCellWidth(this.id)
                    }
                    return __GRPHFntMeasure(fontId, size, text)
                }

                // Height of a line of text in pixels in the font.
                int TextHeight(font = undefined, int size = 0) {
                    var fontId = 0
                    if (font != undefined) { fontId = font.id }
                    if (fontId == 0) {
                        return __GRPHRndCellHeight(this.id)
                    }
                    return __GRPHFntHeight(fontId, size)
                }

                // Pixels from the top of a line of text to its baseline in the font (the console font: the height of a cell).
                int TextAscent(font = undefined, int size = 0) {
                    var fontId = 0
                    if (font != undefined) { fontId = font.id }
                    if (fontId == 0) {
                        return __GRPHRndCellHeight(this.id)
                    }
                    return __GRPHFntAscent(fontId, size)
                }

                // ---- Fills (brush) ----
                FillRect(int x, int y, int w, int h, Brush brush) { __GRPHRndFillRect(this.id, x, y, w, h, brush.id) }
                // Fills the whole framebuffer (blended if AlphaBlending is on; ClearTo sets without blending)
                Fill(Brush brush) { __GRPHRndFill(this.id, brush.id) }
                // Circle around (cx, cy) with radius r, ellipse with the semi-axes rx (horizontal) and ry (vertical), including the edge
                FillCircle(int cx, int cy, int r, Brush brush) { __GRPHRndFillCircle(this.id, cx, cy, r, brush.id) }
                FillEllipse(int cx, int cy, int rx, int ry, Brush brush) { __GRPHRndFillEllipse(this.id, cx, cy, rx, ry, brush.id) }
                FillTriangle(int x0, int y0, int x1, int y1, int x2, int y2, Brush brush) { __GRPHRndFillTriangle(this.id, x0, y0, x1, y1, x2, y2, brush.id) }
                // points: an array [x0, y0, x1, y1, ...]; filled by the even-odd rule (overlapping parts stay empty)
                FillPolygon(points, Brush brush) { __GRPHRndFillPolygon(this.id, points, brush.id) }
                // Fills the connected area with the colour of the pixel (x, y). With `border`, FloodFill instead fills up to pixels of that colour (like PAINT in QBasic).
                FloodFill(int x, int y, Brush brush) { __GRPHRndFloodFill(this.id, x, y, brush.id) }
                FloodFillBorder(int x, int y, Brush brush, int border) { __GRPHRndFloodFillBorder(this.id, x, y, brush.id, border) }

                // ---- Zeichnen (Stift) ----
                DrawPoint(int x, int y, Pen pen) { __GRPHRndDrawPoint(this.id, x, y, pen.id) }
                DrawLine(int x0, int y0, int x1, int y1, Pen pen) { __GRPHRndDrawLine(this.id, x0, y0, x1, y1, pen.id) }
                // A path through the points [x0, y0, x1, y1, ...]; closed connects the last to the first
                DrawPath(points, Pen pen, bool closed = false) { __GRPHRndDrawPath(this.id, points, pen.id, closed) }
                // Outlines: paths made from the pixels of the shape
                DrawRect(int x, int y, int w, int h, Pen pen) { __GRPHRndDrawRect(this.id, x, y, w, h, pen.id) }
                DrawCircle(int cx, int cy, int r, Pen pen) { __GRPHRndDrawCircle(this.id, cx, cy, r, pen.id) }
                DrawEllipse(int cx, int cy, int rx, int ry, Pen pen) { __GRPHRndDrawEllipse(this.id, cx, cy, rx, ry, pen.id) }
                DrawTriangle(int x0, int y0, int x1, int y1, int x2, int y2, Pen pen) { __GRPHRndDrawTriangle(this.id, x0, y0, x1, y1, x2, y2, pen.id) }
                DrawPolygon(points, Pen pen, bool closed = true) { __GRPHRndDrawPolygon(this.id, points, pen.id, closed) }

                // ---- Kopieren ----
                // Copies another framebuffer (e.g. a loaded image) here, also between the colour modes (see BlitMode; Blend only blends with AlphaBlending). Blit: the whole image
                // with its top left corner at (x, y); BlitRegion: the section (sx, sy, sw, sh) to (dx, dy); BlitScaled: additionally brought to the size dw x dh (nearest neighbour;
                // a NEGATIVE width/height mirrors). `key`: for a palette image the transparent index (-1 = its TransparentIndex).
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
