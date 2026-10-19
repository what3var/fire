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
    /// fire source (the prelude of the standard package) that
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

    }
}
