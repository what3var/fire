using fire.Bytecode;
using fire.Runtime;
using fire.Terminal;
using fire.Terminal.Event;
using fire.UI.Markup;
using fire.Values;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace fire.Compiler
{
    /// <summary>Where an element of the markup ended up in the picture (<paramref name="Index"/>: its place in <see cref="MarkupDocument.AllElements"/> without the parts that have no object).</summary>
    public sealed record PreviewRect(int Index, int X, int Y, int Width, int Height);

    /// <summary>The picture of an interface and where its elements are; or the reason there is none.</summary>
    public sealed class UiPreviewResult
    {
        public bool Ok { get; init; }
        public string? Error { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        /// <summary>One uint per pixel (R, G, B, A from the lowest byte), row by row.</summary>
        public uint[] Pixels { get; init; } = Array.Empty<uint>();
        public IReadOnlyList<PreviewRect> Rects { get; init; } = Array.Empty<PreviewRect>();
    }

    /// <summary>The design view of the editor (docs/ui/markup.md): draws the interface of a markup document with the library `ui` itself - the same code that runs in the program - into a framebuffer behind a
    /// window that shows nothing, and reports where the elements are. No handlers run and no code of the program (see <see cref="FireUiGenerator.GeneratePreview"/>).</summary>
    public static class UiPreview
    {
        /// <param name="sourcePath">The file of the markup: files that the markup embeds (`Image source="..."`) are looked for next to it.</param>
        public static UiPreviewResult Render(MarkupDocument document, string? sourcePath = null)
        {
            string script;
            try { script = FireUiGenerator.GeneratePreview(document, sourcePath ?? ""); }
            catch (MarkupException ex) { return new UiPreviewResult { Error = ex.Message }; }

            var output = new List<string>();
            RuntimeSession? session = null;
            uint[]? pixels = null;
            int width = 0, height = 0;
            try
            {
                session = RuntimeSession.Build(new[] { script }, VmExecutionMode.Release, args =>
                {
                    if (args.Length == 0) return Value.MakeUndefined();
                    string line = args[0].ToString();
                    output.Add(line);
                    // the picture is taken the moment the script reports it: when the program ends, its framebuffer is destroyed
                    if (line.StartsWith("@fb ", StringComparison.Ordinal) && int.TryParse(line.AsSpan(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
                        && session?.FramebufferPixels(id, out int w, out int h) is { } shot)
                    {
                        pixels = shot;
                        width = w;
                        height = h;
                    }
                    return Value.MakeUndefined();
                }, basePath: string.IsNullOrEmpty(sourcePath) ? null : Path.GetDirectoryName(Path.GetFullPath(sourcePath)), hostWindows: new HostWindows(null));   // (a window without a screen: no events, it stays open)
                try { session.Run(); }
                finally { session.CloseHostResources(); }
                if (session.VirtualMachine?.UnhandledException != null)
                    return new UiPreviewResult { Error = new UncaughtScriptException(session.VirtualMachine.UnhandledException).Message };

                var rects = new List<PreviewRect>();
                foreach (string line in output)
                {
                    var parts = line.Split(' ');
                    if (parts.Length == 5 && parts[0].StartsWith('@'))
                        rects.Add(new PreviewRect(int.Parse(parts[0].AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture),
                            int.Parse(parts[1], CultureInfo.InvariantCulture), int.Parse(parts[2], CultureInfo.InvariantCulture),
                            int.Parse(parts[3], CultureInfo.InvariantCulture), int.Parse(parts[4], CultureInfo.InvariantCulture)));
                }
                if (pixels == null) return new UiPreviewResult { Error = "The design view could not draw the interface (" + string.Join(" | ", output.Take(3)) + ")." };
                return new UiPreviewResult { Ok = true, Width = width, Height = height, Pixels = pixels, Rects = rects };
            }
            catch (Exception ex)
            {
                return new UiPreviewResult { Error = CompileErrors.Describe(ex) };
            }
        }
    }
}
