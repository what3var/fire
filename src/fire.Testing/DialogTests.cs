using fire.Bytecode;
using fire.Compiler;
using fire.Parsing;
using fire.Resolving;
using fire.Runtime;
using fire.Values;

/// <summary>The standard library `dialogs` (docs/DIALOGS.md) in the virtual machine: open, save and folder dialogs and message boxes on a folder with files. The windows are
/// fakes that play a script of input (text, keys) one step per cycle - the dialogs run their own loop on the program's thread, like on a real screen.</summary>
static class DialogTests
{
    private static int _failures;

    private static void Check(string title, bool ok, string? detail = null)
    {
        if (!ok) _failures++;
        Console.WriteLine(ok ? $"OK: Dialoge: {title}" : $"FEHLER: Dialoge: {title}" + (detail == null ? "" : $"\n{detail}"));
    }

    /// <summary>A window that plays its script: one step per pump ("text:abc" types, "key:13" presses a key, "wait" does nothing). When the script is done it waits
    /// a while and then closes (a dialog that never ends would otherwise hang the suite).</summary>
    private sealed class ScriptedWindow : fire.Terminal.IFramebufferRenderer
    {
        private readonly Queue<string> _steps;
        private int _idle;
        public string Title = "";
        public bool Disposed;
        public bool VSync { get; set; } = true;
        public bool TouchMouse { get; set; } = true;

        public ScriptedWindow(IEnumerable<string> steps) { _steps = new Queue<string>(steps); }

        public void Initialize(string title, int initialWidth, int initialHeight, int internalHandle) { Title = title; }
        // FIRE_DIALOG_SHOTS=<folder>: the last frame of every window is saved there as a PNG (to look at the layout)
        private static readonly string? ShotFolder = Environment.GetEnvironmentVariable("FIRE_DIALOG_SHOTS");
        private static int _shots;
        private uint[]? _lastFrame;
        private int _lastWidth, _lastHeight;

        public void Present(fire.Terminal.Framebuffer framebuffer)
        {
            if (ShotFolder == null) return;
            _lastFrame = (uint[])framebuffer.Pixels.Clone();
            _lastWidth = framebuffer.Width;
            _lastHeight = framebuffer.Height;
        }

        public void Dispose()
        {
            Disposed = true;
            if (ShotFolder == null || _lastFrame == null) return;
            Directory.CreateDirectory(ShotFolder);
            var image = fire.Terminal.ImageData.CreateTruecolor(_lastWidth, _lastHeight, _lastFrame, "PNG");
            File.WriteAllBytes(Path.Combine(ShotFolder, $"{++_shots:00}-{Title.Replace(' ', '_')}.png"), fire.Terminal.ImageEncoder.EncodePng(image));
        }

        public fire.Terminal.WindowPumpResult PumpEvents()
        {
            var events = new List<fire.Terminal.Event.IEvent>();
            if (_steps.Count > 0)
            {
                string step = _steps.Dequeue();
                if (step.StartsWith("text:", StringComparison.Ordinal))
                    events.Add(new fire.Terminal.Event.TextEvent { Type = fire.Terminal.Event.EventType.TextInput, Text = step.Substring(5) });
                else if (step.StartsWith("key:", StringComparison.Ordinal))
                {
                    int code = int.Parse(step.Substring(4));
                    events.Add(new fire.Terminal.Event.KeyEvent { Type = fire.Terminal.Event.EventType.KeyDown, KeyCode = code });
                    events.Add(new fire.Terminal.Event.KeyEvent { Type = fire.Terminal.Event.EventType.KeyUp, KeyCode = code });
                }
            }
            else _idle++;
            return new fire.Terminal.WindowPumpResult { StillOpen = _idle < 300, Events = events.ToArray() };
        }
    }

    private static string StdPrelude(string import) => fire.Package.Manager.PackageStore.Default.FindImport(import)!.ReadPrelude()!;

    /// <summary>Runs `script` with the dialogs; `windows[i]` is the script of the i-th window that opens. Returns the printed lines and the window titles.</summary>
    private static (List<string> Lines, List<string> Titles) Run(string script, params string[][] windows)
    {
        var lines = new List<string>();
        var fakes = new List<ScriptedWindow>();
        var imports = new[] { "graphics", "windows", "reflection", "ui", "io", "time", "filesystem", "dialogs" };
        var sources = new[] { fire.Standard.Prelude.Source }.Concat(imports.Select(StdPrelude)).Append(script);
        var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var program = Parser.ParseMultiple(sources.Select(src => Preprocessor.Process(src, Directory.GetCurrentDirectory(), included)).ToList());
        var natives = new NativeRegistry();
        natives.Register("print", args => { lines.Add(args[0].ToString()); return Value.MakeUndefined(); });
        natives.RegisterBaseTypeNatives();

        var fbManager = new fire.Terminal.FramebufferManager();
        var conManager = new fire.Terminal.RendererManager(fbManager, new fire.Terminal.IntegratedGlyphFont());
        var winManager = new fire.Terminal.Windows.WindowManager(fbManager, (l, v) => { }, () =>
        {
            var fake = new ScriptedWindow(fakes.Count < windows.Length ? windows[fakes.Count] : Array.Empty<string>());
            fakes.Add(fake);
            return fake;
        });
        fire.Terminal.Bridge.GraphicsBridge.RegisterAll(natives, fbManager, conManager);
        fire.Windows.Bridge.WindowsBridge.RegisterAll(natives, winManager);
        ReflectionNatives.Register(natives);
        var packages = new[] { "io", "time" }.Select(i => fire.Package.Manager.PackageStore.Default.FindImport(i)!).ToList();
        var (names, libraries) = PackageImports.NativesOf(packages.Select(i => i.Key));
        var paths = packages.ToDictionary(i => PackageLibrary.FileNameFor(i), i => PackageLibrary.Ensure(i));
        PackageNativeBinding.Register(natives, names, libraries, file => paths.TryGetValue(file, out var path) ? path : null);

        using (PackageHost.Begin(null, null))
        {
            var resolved = Resolver.Resolve(program, natives.Names);
            var compiled = fire.Compiler.Compiler.Compile(program, resolved, natives);
            var vm = new VM(compiled.TopLevel, new Scope(null, isGlobal: true), natives, compiled.Classes, isMainThreadVm: true, executionMode: VmExecutionMode.Debug);
            vm.Run();
            if (vm.UnhandledException != null) lines.Add("UNHANDLED: " + new UncaughtScriptException(vm.UnhandledException).Message);
        }
        winManager.StopManagers();
        return (lines, fakes.Select(f => f.Title).ToList());
    }

    public static void Run()
    {
        Console.WriteLine("=== Dialoge ===");
        string root = Path.Combine(Path.GetTempPath(), "fire-dialogs-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            File.WriteAllText(Path.Combine(root, "a.txt"), "a");
            File.WriteAllText(Path.Combine(root, "b.md"), "b");
            File.WriteAllText(Path.Combine(root, "sub", "c.txt"), "c");
            string folder = root.Replace("\\", "/");
            string sep = Path.DirectorySeparatorChar.ToString();
            const string Enter = "key:13", Escape = "key:27", Tab = "key:9";

            var (lines, titles) = Run($$"""
                var d = new Dialogs.OpenFileDialog()
                d.InitialDirectory = "{{folder}}"
                print(d.ShowDialog())
                print(d.FileName)
                """, new[] { "text:b.md", Enter });
            Check("Oeffnen: Name tippen, Enter waehlt die Datei", lines.Count == 2 && lines[0] == "1" && lines[1] == Path.Combine(root, "b.md") && titles.SequenceEqual(new[] { "Open" }),
                string.Join(" | ", lines) + " / " + string.Join(",", titles));

            (lines, _) = Run($$"""
                var d = new Dialogs.OpenFileDialog()
                d.InitialDirectory = "{{folder}}"
                d.Filter = "Text|*.txt|All files|*.*"
                d.Title = "Pick"
                print(d.ShowDialog())
                print(d.FileName)
                print(d.FilterIndex)
                """, new[] { "text:sub", Enter, "text:c.txt", Enter });
            Check("Oeffnen: ein Ordnername oeffnet den Ordner, dann die Datei darin", lines.Count == 3 && lines[0] == "1" && lines[1] == Path.Combine(root, "sub", "c.txt") && lines[2] == "1", string.Join(" | ", lines));

            (lines, _) = Run($$"""
                var d = new Dialogs.OpenFileDialog()
                d.InitialDirectory = "{{folder}}"
                print(d.ShowDialog())
                print(d.FileName == "")
                """, new[] { "text:nope.txt", Enter, Escape });
            Check("Oeffnen: eine Datei, die es nicht gibt, beendet den Dialog nicht; Escape bricht ab", lines.SequenceEqual(new[] { "2", "True" }), string.Join(" | ", lines));

            const string Down = "key:1073741905", Backspace = "key:8";
            (lines, _) = Run($$"""
                var d = new Dialogs.OpenFileDialog()
                d.InitialDirectory = "{{folder}}"
                print(d.ShowDialog())
                print(d.FileName)
                """, new[] { Tab, Tab, Tab, Tab, Tab, Down, Enter, Backspace, Down, Enter, Down, Enter });
            Check("Liste: Tab zur Liste, Pfeil und Enter oeffnen den Ordner, Ruecktaste geht hinauf, Enter auf einer Datei waehlt sie",
                lines.Count == 2 && lines[0] == "1" && lines[1] == Path.Combine(root, "sub", "c.txt"), string.Join(" | ", lines));

            (lines, _) = Run($$"""
                print(Dialogs.OpenFileDialog.Show("", "", "{{folder}}") == undefined)
                print(Dialogs.OpenFileDialog.Show("Markdown|*.md", "", "{{folder}}"))
                """, new[] { Escape }, new[] { "text:b.md", Enter });
            Check("Oeffnen: die schnelle Form liefert den Pfad oder undefined", lines.Count == 2 && lines[0] == "True" && lines[1] == Path.Combine(root, "b.md"), string.Join(" | ", lines));

            (lines, _) = Run($$"""
                var d = new Dialogs.SaveFileDialog()
                d.InitialDirectory = "{{folder}}"
                d.Filter = "Text|*.txt|All files|*.*"
                print(d.ShowDialog())
                print(d.FileName)
                """, new[] { "text:new", Enter });
            Check("Speichern: ein neuer Name bekommt die Endung des Dateityps", lines.Count == 2 && lines[0] == "1" && lines[1] == Path.Combine(root, "new.txt"), string.Join(" | ", lines));

            (lines, titles) = Run($$"""
                var d = new Dialogs.SaveFileDialog()
                d.InitialDirectory = "{{folder}}"
                print(d.ShowDialog())
                print(d.FileName)
                """, new[] { "text:a.txt", Enter }, new[] { Enter });
            Check("Speichern: eine vorhandene Datei erst nach Rueckfrage (Ja)", lines.Count == 2 && lines[0] == "1" && lines[1] == Path.Combine(root, "a.txt") && titles.SequenceEqual(new[] { "Save As", "Confirm Save As" }),
                string.Join(" | ", lines) + " / " + string.Join(",", titles));

            (lines, _) = Run($$"""
                var d = new Dialogs.SaveFileDialog()
                d.InitialDirectory = "{{folder}}"
                print(d.ShowDialog())
                """, new[] { "text:a.txt", Enter, Escape }, new[] { Escape });
            Check("Speichern: Nein bei der Rueckfrage laesst den Dialog offen", lines.SequenceEqual(new[] { "2" }), string.Join(" | ", lines));

            (lines, _) = Run($$"""
                var d = new Dialogs.FolderBrowserDialog()
                d.SelectedPath = "{{folder}}"
                d.Description = "Where?"
                print(d.ShowDialog())
                print(d.SelectedPath)
                print(Dialogs.FolderBrowserDialog.Show("", "{{folder}}"))
                """, new[] { "text:sub", Enter }, new[] { Enter });
            Check("Ordner: ein Name waehlt den Unterordner, leer der offene Ordner", lines.Count == 3 && lines[0] == "1" && lines[1] == Path.Combine(root, "sub") && lines[2] == root,
                string.Join(" | ", lines));

            (lines, titles) = Run("""
                print(Dialogs.MessageBox.Show("Hello\nWorld", "Note"))
                print(Dialogs.MessageBox.Show("Save?", "Editor", Dialogs.MessageBoxButtons.YesNoCancel))
                print(Dialogs.MessageBox.Show("Save?", "Editor", Dialogs.MessageBoxButtons.YesNo))
                print(Dialogs.MessageBox.Show("Go on?", "", Dialogs.MessageBoxButtons.OKCancel))
                """, new[] { Enter }, new[] { Tab, Enter }, new[] { Escape }, new[] { Escape });
            Check("Meldung: OK, Tab zum zweiten Knopf, Escape ergibt Nein bzw. Abbrechen", lines.SequenceEqual(new[] { "1", "4", "4", "2" }) && titles.SequenceEqual(new[] { "Note", "Editor", "Editor", "Message" }),
                string.Join(" | ", lines) + " / " + string.Join(",", titles));
        }
        catch (Exception ex) { Check("ohne Ausnahme durchgelaufen", false, ex.ToString()); }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
        Console.WriteLine(_failures == 0 ? "Alle Dialog-Pruefungen bestanden." : $"FEHLER: {_failures} Dialog-Pruefung(en) fehlgeschlagen.");
    }
}
