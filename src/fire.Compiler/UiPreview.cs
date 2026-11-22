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
        /// <summary>Where it was drawn: "c" (`fire`) or "dotnet" (the C# VM of this process).</summary>
        public string Engine { get; init; } = "";
        /// <summary>Milliseconds: compiling the script here, running it.</summary>
        public double CompileMs { get; init; }
        public double RunMs { get; init; }
    }

    /// <summary>The design view of the editor (docs/ui/markup.md): draws the interface of a markup document with the library `ui` itself - the same code that runs in the program - into a framebuffer behind a
    /// window that shows nothing, and reports where the elements are. No handlers run and no code of the program (see <see cref="FireUiGenerator.GeneratePreview"/>).
    /// The script runs in `fire` (the VM in C, <see cref="CVmScript"/>; docs/spec/VM_C.md, "spark auf der C-VM"): its window is the host's display file
    /// (FIRE_DISPLAY_SHM), one cycle of the window without its handlers (`Window.Idle`) after the dump hands the picture over, the places of the elements come as `print`.
    /// With FIRE_SPARK_VM=dotnet (or without a `fire`) in the C# VM of this process as before.</summary>
    public static class UiPreview
    {
        /// <param name="sourcePath">The file of the markup: files that the markup embeds (`Image source="..."`) are looked for next to it.</param>
        public static UiPreviewResult Render(MarkupDocument document, string? sourcePath = null)
        {
            string script;
            try { script = FireUiGenerator.GeneratePreview(document, sourcePath ?? ""); }
            catch (MarkupException ex) { return new UiPreviewResult { Error = ex.Message }; }
            string? basePath = string.IsNullOrEmpty(sourcePath) ? null : Path.GetDirectoryName(Path.GetFullPath(sourcePath));
            return CVmScript.Available ? RenderInFire(script, basePath) : RenderHere(script, basePath);
        }

        /// <summary>The places of the elements from what the script printed (`@N X Y W H`).</summary>
        private static List<PreviewRect> Rects(IEnumerable<string> output)
        {
            var rects = new List<PreviewRect>();
            foreach (string line in output)
            {
                var parts = line.Split(' ');
                if (parts.Length == 5 && parts[0].StartsWith('@'))
                    rects.Add(new PreviewRect(int.Parse(parts[0].AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture),
                        int.Parse(parts[1], CultureInfo.InvariantCulture), int.Parse(parts[2], CultureInfo.InvariantCulture),
                        int.Parse(parts[3], CultureInfo.InvariantCulture), int.Parse(parts[4], CultureInfo.InvariantCulture)));
            }
            return rects;
        }

        /// <summary>In `fire`: the picture is what the window of the script showed on the display file - half a cycle of its window (`Window.Idle`: its events - there are
        /// none -, its frame; no onCycle) after `FxDump`, so the library's own way of showing a frame hands it over: no capture of a framebuffer by its number
        /// (the plan of VM_C.md) is needed.</summary>
        private static UiPreviewResult RenderInFire(string script, string? basePath)
        {
            try
            {
                using var display = HostDisplayFile.Create();
                var run = CVmScript.Run(script + "fxP.window.Idle()\n", basePath, display, timeoutMs: 20000);
                if (run.Failure is { } failure) return new UiPreviewResult { Error = failure, Engine = "c", CompileMs = run.CompileMs, RunMs = run.RunMs };
                var pixels = Array.Empty<uint>();
                if (display.TryCopyFrame(ref pixels, out int width, out int height, out _) == null)
                    return new UiPreviewResult { Error = "The design view could not draw the interface (" + string.Join(" | ", run.Printed.Take(3)) + ").", Engine = "c" };
                return new UiPreviewResult { Ok = true, Width = width, Height = height, Pixels = pixels, Rects = Rects(run.Printed), Engine = "c", CompileMs = run.CompileMs, RunMs = run.RunMs };
            }
            catch (Exception ex)
            {
                return new UiPreviewResult { Error = CompileErrors.Describe(ex), Engine = "c" };
            }
        }

        /// <summary>In the C# VM of this process (FIRE_SPARK_VM=dotnet): the framebuffer is copied the moment the script prints `@fb ID`.</summary>
        private static UiPreviewResult RenderHere(string script, string? basePath)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
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
                }, basePath: basePath, hostWindows: new HostWindows(null));   // (a window without a screen: no events, it stays open)
                double compileMs = clock.Elapsed.TotalMilliseconds;
                clock.Restart();
                try { session.Run(); }
                finally { session.CloseHostResources(); }
                double runMs = clock.Elapsed.TotalMilliseconds;
                if (session.VirtualMachine?.UnhandledException != null)
                    return new UiPreviewResult { Error = new UncaughtScriptException(session.VirtualMachine.UnhandledException).Message, Engine = "dotnet" };
                if (pixels == null) return new UiPreviewResult { Error = "The design view could not draw the interface (" + string.Join(" | ", output.Take(3)) + ").", Engine = "dotnet" };
                return new UiPreviewResult { Ok = true, Width = width, Height = height, Pixels = pixels, Rects = Rects(output), Engine = "dotnet", CompileMs = compileMs, RunMs = runMs };
            }
            catch (Exception ex)
            {
                return new UiPreviewResult { Error = CompileErrors.Describe(ex), Engine = "dotnet" };
            }
        }
    }
}
