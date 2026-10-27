using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using fire.Compiler;
using fire.UI.Markup;

namespace fire.Editor
{
    /// <summary>
    /// The editor of a circuit (`.biscuit`, docs/CIRCUITS.md). The schematic itself is the CircuitView of the package `circuits.ui` - the same control a program
    /// shows - running live in a fire program of its own (<see cref="LiveSession"/>): it draws, simulates, and is edited right there (select, move, stretch, wires,
    /// rotate, delete, undo). Around it, in Avalonia: the palette of parts (a click places the next one in the view), the properties of the selected element,
    /// the tools and the import of Falstad circuits. The text of the file is what the view reports after every change.
    /// </summary>
    public sealed class CircuitEditorControl : UserControl, IDocumentView, IPropertyHost
    {
        // ---- the picture of the program -----------------------------------------------------------------------------------------------------------
        private sealed class Surface : Control
        {
            private WriteableBitmap? _bitmap;
            private uint[] _pixels = Array.Empty<uint>();
            public LiveSession? Session { get; set; }
            public long Frame = -1;

            /// <summary>Pixels of the screen per unit of Avalonia: the program draws at the full resolution, the picture is shown 1:1 (no stretching).</summary>
            public double Scale => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;

            /// <summary>The size of the area in pixels of the screen.</summary>
            public (int W, int H) PixelSize(Size size) => ((int)Math.Max(1, Math.Round(size.Width * Scale)), (int)Math.Max(1, Math.Round(size.Height * Scale)));

            public Surface()
            {
                Focusable = true;
                ClipToBounds = true;
            }

            public void TakeFrame()
            {
                if (Session == null) return;
                long frame = Session.CopyFrame(ref _pixels, out int w, out int h);
                if (frame == Frame || w <= 0 || h <= 0) return;
                Frame = frame;
                if (_bitmap == null || _bitmap.PixelSize.Width != w || _bitmap.PixelSize.Height != h)
                {
                    _bitmap?.Dispose();
                    _bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
                }
                using (var fb = _bitmap.Lock())
                {
                    var bytes = new byte[w * 4];
                    for (int y = 0; y < h; y++)
                    {
                        Buffer.BlockCopy(_pixels, y * w * 4, bytes, 0, w * 4);
                        System.Runtime.InteropServices.Marshal.Copy(bytes, 0, fb.Address + y * fb.RowBytes, w * 4);
                    }
                }
                InvalidateVisual();
            }

            public override void Render(DrawingContext context)
            {
                context.FillRectangle(new SolidColorBrush(Color.FromRgb(24, 24, 28)), new Rect(Bounds.Size));
                if (_bitmap != null)
                    using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
                        context.DrawImage(_bitmap, new Rect(0, 0, _bitmap.PixelSize.Width / Scale, _bitmap.PixelSize.Height / Scale));
            }

            private static int Button(PointerUpdateKind kind) => kind switch
            {
                PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => 1,
                PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => 2,
                PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => 3,
                _ => 1,
            };

            protected override void OnPointerPressed(PointerPressedEventArgs e)
            {
                base.OnPointerPressed(e);
                Focus();
                var p = e.GetCurrentPoint(this);
                e.Pointer.Capture(this);
                double k = Scale;
                Session?.MouseMove(p.Position.X * k, p.Position.Y * k, 0);
                Session?.MouseButton(Button(p.Properties.PointerUpdateKind), true, p.Position.X * k, p.Position.Y * k);
                e.Handled = true;
            }

            protected override void OnPointerReleased(PointerReleasedEventArgs e)
            {
                base.OnPointerReleased(e);
                var p = e.GetCurrentPoint(this);
                e.Pointer.Capture(null);
                Session?.MouseButton(Button(p.Properties.PointerUpdateKind), false, p.Position.X * Scale, p.Position.Y * Scale);
                e.Handled = true;
            }

            protected override void OnPointerMoved(PointerEventArgs e)
            {
                base.OnPointerMoved(e);
                var p = e.GetCurrentPoint(this);
                int buttons = (p.Properties.IsLeftButtonPressed ? 1 : 0) | (p.Properties.IsMiddleButtonPressed ? 2 : 0) | (p.Properties.IsRightButtonPressed ? 4 : 0);
                Session?.MouseMove(p.Position.X * Scale, p.Position.Y * Scale, buttons);
            }

            protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
            {
                base.OnPointerWheelChanged(e);
                var p = e.GetPosition(this);
                Session?.Wheel(e.Delta.X, e.Delta.Y, p.X * Scale, p.Y * Scale);
                e.Handled = true;
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e);
                if (KeyCode(e.Key) is { } code)
                {
                    Session?.Key(code, Modifiers(e.KeyModifiers), true);
                    e.Handled = true;
                }
            }

            protected override void OnKeyUp(KeyEventArgs e)
            {
                base.OnKeyUp(e);
                if (KeyCode(e.Key) is { } code) Session?.Key(code, Modifiers(e.KeyModifiers), false);
            }

            protected override void OnTextInput(TextInputEventArgs e)
            {
                base.OnTextInput(e);
                if (!string.IsNullOrEmpty(e.Text)) Session?.Text(e.Text);
            }

            protected override void OnSizeChanged(SizeChangedEventArgs e)
            {
                base.OnSizeChanged(e);
                var (w, h) = PixelSize(e.NewSize);
                Session?.Resize(w, h);
            }

            /// <summary>The key as SDL names it (letters: their lower case character; see UI.Keys); null for keys the view does not use.</summary>
            internal static int? KeyCode(Key key)
            {
                if (key >= Key.A && key <= Key.Z) return 'a' + (key - Key.A);
                if (key >= Key.D0 && key <= Key.D9) return '0' + (key - Key.D0);
                return key switch
                {
                    Key.Delete => 127, Key.Back => 8, Key.Escape => 27, Key.Enter => 13, Key.Space => 32, Key.Tab => 9,
                    Key.Left => 1073741904, Key.Right => 1073741903, Key.Up => 1073741906, Key.Down => 1073741905,
                    Key.Home => 1073741898, Key.End => 1073741901, Key.PageUp => 1073741899, Key.PageDown => 1073741902,
                    _ => null,
                };
            }

            internal static int Modifiers(KeyModifiers m) =>
                ((m & KeyModifiers.Shift) != 0 ? 1 : 0) | ((m & (KeyModifiers.Control | KeyModifiers.Meta)) != 0 ? 64 : 0) | ((m & KeyModifiers.Alt) != 0 ? 256 : 0);
        }

        // ---- the control ----------------------------------------------------------------------------------------------------------------------
        private readonly Surface _surface = new();
        private readonly StackPanel _palette = new() { Spacing = 2, Margin = new Thickness(6) };
        private readonly TextBlock _status = new() { Foreground = EditorTheme.TextDim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly ToggleButton _run = new() { Content = "Pause", IsChecked = true };
        private readonly ComboBox _speed = new() { Width = 120 };
        private readonly List<ToggleButton> _placeButtons = new();
        private ToggleButton? _pointerButton;
        private LiveSession? _session;
        private string _text = "";
        private string _savedText = "";
        private bool _canUndo, _canRedo;
        private bool _framePending;
        // the next text the view reports is the file as it was read (not a change): it becomes the saved state
        private bool _baselinePending;
        private string? _pendingLoad;
        // the catalog of the package: category, type, properties (name, kind, default)
        private readonly List<(string Category, string Type, string Properties)> _catalog = new();
        private readonly Dictionary<string, List<string>> _models = new(StringComparer.OrdinalIgnoreCase);
        // the circuit itself as the view reported it (@circuit): class, namespace, time step and the parts that can be operated (index|kind|name|value|range)
        private string[] _circuit = { "", "", "10us" };
        private readonly List<string[]> _positions = new();
        // the ports and the device bindings (@ports): index|type|name|key|value
        private readonly List<string[]> _ports = new();
        public event Action? PortsChanged;
        // the selected element as the view reported it
        private int _selectedIndex = -1;
        private string _selectedType = "";

        public string? FilePath { get; set; }
        public bool IsReadOnly => false;
        public bool IsModified => _text != _savedText;
        public event Action? ModifiedChanged;
        public event Action<int>? CaretLineChanged { add { } remove { } }

        public CircuitEditorControl()
        {
            Button Tool(string text, string tip, Action click)
            {
                var b = new Button { Content = text };
                b.Classes.Add("tool");
                ToolTip.SetTip(b, tip);
                b.Click += (_, _) => { click(); _surface.Focus(); };
                return b;
            }

            _run.Classes.Add("tool");
            ToolTip.SetTip(_run, "Run or pause the simulation (Space in the view)");
            _run.Click += (_, _) => { Send("run\t" + (_run.IsChecked == true ? "1" : "0")); _run.Content = _run.IsChecked == true ? "Pause" : "Run"; _surface.Focus(); };
            foreach (var s in new[] { "1", "5", "20", "100", "500" }) _speed.Items.Add(s + " steps");
            _speed.SelectedIndex = 2;
            ToolTip.SetTip(_speed, "Time steps of the circuit per picture (the speed of the simulation)");
            _speed.SelectionChanged += (_, _) => { if (_speed.SelectedItem is string s) Send("steps\t" + s.Split(' ')[0]); };

            var import = Tool("Import Falstad...", "A circuit of Paul Falstad's simulator (File > Export As Text) from a file or from the clipboard", () => { });
            var importMenu = new ContextMenu();
            var fromFile = new MenuItem { Header = "From file..." };
            fromFile.Click += async (_, _) => await ImportFromFile();
            var fromClipboard = new MenuItem { Header = "From clipboard" };
            fromClipboard.Click += async (_, _) => await ImportFromClipboard();
            importMenu.Items.Add(fromFile);
            importMenu.Items.Add(fromClipboard);
            import.ContextMenu = importMenu;
            import.Click += (_, _) => importMenu.Open(import);

            var samples = Tool("Samples", "Example circuits to try out and to start from", () => { });
            var samplesMenu = new ContextMenu();
            foreach (var sample in CircuitSamples.All)
            {
                var item = new MenuItem { Header = sample.Title };
                ToolTip.SetTip(item, sample.Description);
                item.Click += async (_, _) => await LoadSample(sample);
                samplesMenu.Items.Add(item);
            }
            samples.ContextMenu = samplesMenu;
            samples.Click += (_, _) => samplesMenu.Open(samples);

            var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(4, 2) };
            foreach (var c in new Control[]
            {
                _run, Tool("Reset", "Back to time 0", () => Send("reset")), _speed, Separator(),
                Tool("Undo", "Undo (Ctrl+Z)", Undo), Tool("Redo", "Redo (Ctrl+Y)", Redo), Separator(),
                Tool("Rotate", "Turn the selection by 90 degrees (R)", () => Send("rotate")), Tool("Flip", "Mirror the selection (F)", () => Send("flip")),
                Tool("Duplicate", "Copy the selection (Ctrl+D)", () => Send("duplicate")), Tool("Delete", "Remove the selection (Del)", () => Send("delete")), Separator(),
                Tool("Wire", "Draw wires (or drag from a pin; W)", () => Send("place\tWire")), Tool("Fit", "Show the whole schematic", () => Send("fit")), Separator(),
                Tool("Circuit...", "The name of the generated class, the time step of the simulation, the ports of the circuit and the parts bound to devices", () => _ = ShowCircuitDialog()),
                import, samples, _status,
            }) bar.Children.Add(c);

            var paletteScroll = new ScrollViewer { Content = _palette, Width = 150 };
            var paletteBorder = new Border { Child = paletteScroll, BorderBrush = EditorTheme.Border, BorderThickness = new Thickness(0, 0, 1, 0) };

            var dock = new DockPanel();
            DockPanel.SetDock(bar, Avalonia.Controls.Dock.Top);
            DockPanel.SetDock(paletteBorder, Avalonia.Controls.Dock.Left);
            dock.Children.Add(bar);
            dock.Children.Add(paletteBorder);
            dock.Children.Add(_surface);
            Content = dock;
            ShowProperties(new List<string>());
            _palette.Children.Add(new TextBlock { Text = "Starting the simulation...", Foreground = EditorTheme.TextDim, TextWrapping = TextWrapping.Wrap });
        }

        private static Control Separator() => new Border { Width = 1, Background = EditorTheme.Border, Margin = new Thickness(6, 2) };

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            if (_session == null) StartSession(_text);
            // a window moved to a screen with another scaling: the program draws at the new one
            if (TopLevel.GetTopLevel(this) is TopLevel top) top.ScalingChanged += OnScalingChanged;
        }

        private void OnScalingChanged(object? sender, EventArgs e)
        {
            if (_session == null) return;
            _session.Send("scaling\t" + _surface.Scale.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var (w, h) = _surface.PixelSize(_surface.Bounds.Size);
            _session.Resize(w, h);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            if (e.Root is TopLevel top) top.ScalingChanged -= OnScalingChanged;
            // a tab that is moved stays alive; the session ends when the document is closed (the control is not attached again)
            Dispatcher.UIThread.Post(() => { if (!IsAttachedToVisualTree() && _session != null) { _session.Stop(); _session = null; _surface.Session = null; } }, DispatcherPriority.Background);
        }

        private bool IsAttachedToVisualTree() => TopLevel.GetTopLevel(this) != null;

        // ---- the program -------------------------------------------------------------------------------------------------------------------------
        /// <summary>The fire program that runs the view: the schematic, the view filling the window, the reports to the editor and the commands from it.</summary>
        public static string HostScript(string biscuit, int width, int height, double scaling = 1.0) => $$"""
            #import "circuits.ui"
            extern string __HostCommand()

            class SparkHost {
                static string Selected(UI.CircuitView view) {
                    var e = view.SelectedElement
                    if (e == undefined) { return "@select" }
                    var text = "@select\t" + view.Schematic.Elements().IndexOf(e) + "\t" + e.type + "\t" + e.name + "\t" + view.Selection().count + "\t" + e.Error
                    for (int k = 0; k < e.keys.count; k++) { text = text + "\t" + e.keys[k] + "=" + e.values[k] }
                    return text
                }

                // the circuit itself: class, namespace, time step, then a field for each part that can be operated (UI.CircuitView.Positions)
                static string Circuit(UI.CircuitView view) {
                    var text = "@circuit\t" + view.CircuitProperty("class") + "\t" + view.CircuitProperty("namespace") + "\t" + view.CircuitProperty("timeStep")
                    foreach (line in view.Positions()) { text = text + "\t" + line }
                    return text
                }

                static string Ports(UI.CircuitView view) {
                    var text = "@ports"
                    foreach (line in view.Ports()) { text = text + "\t" + line }
                    return text
                }

                static string State(UI.CircuitView view) {
                    return "@state\t" + view.running + "\t" + view.CanUndo + "\t" + view.CanRedo
                }

                static Circuits.Element At(UI.CircuitView view, string index) {
                    var i = index.ToInt()
                    var list = view.Schematic.Elements()
                    if (i < 0 || i >= list.count) { return undefined }
                    return list[i]
                }

                static undefined Do(UI.CircuitView view, Circuits.Circuit circuit, string command) {
                    var p = command.Split("\t")
                    var c = p[0]
                    if (c == "place") { view.BeginPlace(p[1]) }
                    else if (c == "cancel") { view.CancelPlace() }
                    else if (c == "delete") { view.DeleteSelection() }
                    else if (c == "rotate") { view.RotateSelection() }
                    else if (c == "flip") { view.FlipSelection() }
                    else if (c == "duplicate") { view.DuplicateSelection() }
                    else if (c == "undo") { view.Undo() }
                    else if (c == "redo") { view.Redo() }
                    else if (c == "selectall") { view.SelectAll() }
                    else if (c == "fit") { view.FitToView() }
                    else if (c == "run") { view.running = p[1] == "1" }
                    else if (c == "reset") { circuit.Reset() }
                    else if (c == "steps") { view.stepsPerFrame = p[1].ToInt() }
                    else if (c == "load") { view.LoadText(command.Substring(5)) }
                    else if (c == "select") { view.Select(SparkHost.At(view, p[1]), false) }
                    else if (c == "set") {
                        var e = SparkHost.At(view, p[1])
                        if (e != undefined) { view.SetProperty(e, p[2], command.Substring(p[0].Length + p[1].Length + p[2].Length + 3)) }
                    }
                    else if (c == "circuit") {
                        if (!view.SetCircuitProperty(p[1], command.Substring(p[0].Length + p[1].Length + 2))) { print("@circuiterror\t" + p[1]) }
                        print(SparkHost.Circuit(view))
                    }
                    else if (c == "operate") { view.Operate(p[1].ToInt(), p[2]) }
                    else if (c == "rename") {
                        var e = SparkHost.At(view, p[1])
                        if (e != undefined) { view.RenameElement(e, p[2]) }
                    }
                    print(SparkHost.State(view))
                }
            }

            var fb = new Framebuffer({{Math.Max(width, 64)}}, {{Math.Max(height, 64)}})
            var win = new Window(fb, "circuit")
            var ui = new UI.Root(fb, win)
            // the scaling of the screen: the view draws at its full resolution (spark shows the picture 1:1)
            ui.Scaling = {{scaling.ToString(System.Globalization.CultureInfo.InvariantCulture)}}
            var circuit = new Circuits.Circuit("schematic")
            var view = new UI.CircuitView(circuit, 0, 0, fb.Width(), fb.Height())
            ui.Add(view)
            ui.onResize = func (int w, int h) => {
                view.Width = w
                view.Height = h
            }
            try { circuit.LoadSchematic({{BiscuitGenerator.Quote(biscuit)}}) } catch (Exception ex) { print("@error\t" + ex.message) }
            view.OnChange = func (v) => {
                print("@text\t" + circuit.Schematic.ToText())
                print(SparkHost.Circuit(view))
                print(SparkHost.Ports(view))
                print(SparkHost.Selected(view))
                print(SparkHost.State(view))
            }
            view.OnSelect = func (v) => { print(SparkHost.Selected(view)) }
            view.OnEdit = func (e) => { print("@edit") }
            print("@catalog\t" + "\n".Join(Circuits.Symbols.Catalog()))
            foreach (kind in ["D", "NPN", "PNP", "NMOS", "PMOS", "OPAMP", "FORMULA"]) { print("@models\t" + kind + "\t" + ",".Join(Circuits.Models.Names(kind))) }
            print("@text\t" + circuit.Schematic.ToText())
            print(SparkHost.Circuit(view))
            print(SparkHost.Ports(view))
            ui.Update()
            view.FitToView()
            print(SparkHost.State(view))
            var placing = ""
            while (ui.Tick()) {
                // the tool of the view (Escape, a placed element): the palette follows
                if (view.PlaceType != placing) {
                    placing = view.PlaceType
                    print("@place\t" + placing)
                }
                var command = __HostCommand()
                while (command != "") {
                    if (command.StartsWith("scaling\t")) {
                        var factor = command.Substring(8).TryToFloat()
                        if (factor != undefined) { ui.Scaling = factor }
                    }
                    SparkHost.Do(view, circuit, command)
                    command = __HostCommand()
                    ui.UpdateCursor(ui.mouseX, ui.mouseY)
                }
            }
            """;

        /// <summary>The Avalonia pointer for a cursor of the program (UI.Cursor / SDL numbering).</summary>
        internal static Cursor PointerFor(int cursor) => new(cursor switch
        {
            1 => StandardCursorType.Ibeam,
            2 => StandardCursorType.Wait,
            3 => StandardCursorType.Cross,
            4 => StandardCursorType.AppStarting,
            5 => StandardCursorType.TopLeftCorner,
            6 => StandardCursorType.TopRightCorner,
            7 => StandardCursorType.SizeWestEast,
            8 => StandardCursorType.SizeNorthSouth,
            9 => StandardCursorType.SizeAll,
            10 => StandardCursorType.No,
            11 => StandardCursorType.Hand,
            _ => StandardCursorType.Arrow,
        });

        private void StartSession(string text)
        {
            _session?.Stop();
            _baselinePending = true;
            var session = new LiveSession();
            _session = session;
            _surface.Session = session;
            _surface.Frame = -1;
            session.Message += line => Dispatcher.UIThread.Post(() => { if (ReferenceEquals(session, _session)) OnMessage(line); });
            session.FrameReady += () =>
            {
                if (_framePending) return;
                _framePending = true;
                Dispatcher.UIThread.Post(() => { _framePending = false; if (ReferenceEquals(session, _session)) _surface.TakeFrame(); }, DispatcherPriority.Render);
            };
            session.CursorChanged += cursor => Dispatcher.UIThread.Post(() => { if (ReferenceEquals(session, _session)) _surface.Cursor = PointerFor(cursor); });
            session.Ended += error => Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(session, _session)) return;
                _status.Text = error == null ? "The simulation ended." : "The simulation failed: " + error;
            });
            int w = (int)Math.Max(64, _surface.Bounds.Width), h = (int)Math.Max(64, _surface.Bounds.Height);
            if (_surface.Bounds.Width < 2) { w = 800; h = 500; }
            string script = HostScript(text, w, h, _surface.Scale);
            string? basePath = FilePath != null ? Path.GetDirectoryName(Path.GetFullPath(FilePath)) : null;
            _status.Text = "Starting...";
            Task.Run(() =>
            {
                try
                {
                    session.Start(script, basePath);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!ReferenceEquals(session, _session)) return;
                        _status.Text = "";
                        if (_surface.Bounds.Width >= 2) { var (pw, ph) = _surface.PixelSize(_surface.Bounds.Size); session.Resize(pw, ph); }
                        if (_pendingLoad != null) { session.Send("load\t" + _pendingLoad); _pendingLoad = null; }
                        session.Send("fit");
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.UIThread.Post(() => { if (ReferenceEquals(session, _session)) _status.Text = "The circuit editor could not start: " + CompileErrors.Describe(ex); });
                }
            });
        }

        private void Send(string command) => _session?.Send(command);

        private void OnMessage(string line)
        {
            int tab = line.IndexOf('\t');
            string kind = tab < 0 ? line : line.Substring(0, tab);
            string rest = tab < 0 ? "" : line.Substring(tab + 1);
            switch (kind)
            {
                case "@text":
                    bool was = IsModified;
                    _text = rest;
                    if (_baselinePending) { _savedText = _text; _baselinePending = false; }   // the file as the view writes it: unchanged
                    if (IsModified != was) ModifiedChanged?.Invoke();
                    break;
                case "@select":
                    ShowProperties(rest == "" ? new List<string>() : rest.Split('\t').ToList());
                    break;
                case "@circuit":
                {
                    var p = rest.Split('\t');
                    if (p.Length >= 3)
                    {
                        _circuit = new[] { p[0], p[1], p[2] };
                        _positions.Clear();
                        foreach (var item in p.Skip(3)) { var f = item.Split('|'); if (f.Length >= 5) _positions.Add(f); }
                        if (_selectedIndex < 0) ShowProperties(new List<string>());
                    }
                    break;
                }
                case "@ports":
                    _ports.Clear();
                    foreach (var item in rest.Split('\t', StringSplitOptions.RemoveEmptyEntries)) { var f = item.Split('|'); if (f.Length >= 5) _ports.Add(f); }
                    PortsChanged?.Invoke();
                    break;
                case "@circuiterror":
                    _status.Text = rest == "timeStep" ? "The time step must be a time like 10us (more than 0, at most 1s)." : "That value is not valid.";
                    break;
                case "@state":
                {
                    var p = rest.Split('\t');
                    if (p.Length >= 3)
                    {
                        _run.IsChecked = p[0] == "True";
                        _run.Content = _run.IsChecked == true ? "Pause" : "Run";
                        _canUndo = p[1] == "True";
                        _canRedo = p[2] == "True";
                    }
                    break;
                }
                case "@catalog":
                    _catalog.Clear();
                    foreach (var entry in rest.Split('\n'))
                    {
                        var p = entry.Split('|');
                        if (p.Length == 3) _catalog.Add((p[0], p[1], p[2]));
                    }
                    BuildPalette();
                    break;
                case "@models":
                {
                    var p = rest.Split('\t');
                    if (p.Length == 2) _models[p[0]] = p[1].Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
                    break;
                }
                case "@place":
                    foreach (var b in _placeButtons) b.IsChecked = (b.Tag as string) == rest;
                    if (_pointerButton != null) _pointerButton.IsChecked = rest == "";
                    break;
                case "@error":
                    _status.Text = "The schematic could not be read: " + rest;
                    break;
                case "@edit":
                    PropertiesChanged?.Invoke(true);
                    break;
            }
        }

        // ---- the palette ---------------------------------------------------------------------------------------------------------------------------
        private void BuildPalette()
        {
            _palette.Children.Clear();
            _placeButtons.Clear();
            // the pointer: no tool, select and move (like Escape)
            var pointer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            pointer.Children.Add(CircuitIcons.Create("Pointer", EditorTheme.Text));
            pointer.Children.Add(new TextBlock { Text = "Pointer", VerticalAlignment = VerticalAlignment.Center });
            _pointerButton = new ToggleButton { Content = pointer, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, IsChecked = true };
            _pointerButton.Classes.Add("tool");
            ToolTip.SetTip(_pointerButton, "Select, move and stretch elements, draw wires from pins (Esc)");
            _pointerButton.Click += (_, _) =>
            {
                _pointerButton.IsChecked = true;
                foreach (var other in _placeButtons) other.IsChecked = false;
                Send("cancel");
                _surface.Focus();
            };
            _palette.Children.Add(_pointerButton);
            foreach (var group in _catalog.GroupBy(c => c.Category))
            {
                _palette.Children.Add(new TextBlock { Text = group.Key, Foreground = EditorTheme.TextDim, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 2) });
                foreach (var (_, type, _) in group)
                {
                    var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    content.Children.Add(CircuitIcons.Create(type, EditorTheme.Text));
                    content.Children.Add(new TextBlock { Text = Spaced(type), VerticalAlignment = VerticalAlignment.Center });
                    var b = new ToggleButton { Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Tag = type };
                    b.Classes.Add("tool");
                    ToolTip.SetTip(b, $"Place a {Spaced(type).ToLowerInvariant()}: click in the schematic, or drag for its length and direction");
                    b.Click += (_, _) =>
                    {
                        bool on = b.IsChecked == true;
                        foreach (var other in _placeButtons) if (!ReferenceEquals(other, b)) other.IsChecked = false;
                        if (_pointerButton != null) _pointerButton.IsChecked = !on;
                        Send(on ? "place\t" + type : "cancel");
                        _surface.Focus();
                    };
                    _placeButtons.Add(b);
                    _palette.Children.Add(b);
                }
            }
        }

        // "CurrentSource" is "Current Source"
        private static string Spaced(string type)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < type.Length; i++)
            {
                if (i > 0 && char.IsUpper(type[i]) && char.IsLower(type[i - 1])) sb.Append(' ');
                sb.Append(type[i]);
            }
            return sb.ToString();
        }

        // ---- the properties (shown in the tool window "Properties", see PropertiesPanelControl) --------------------------------------------------------
        public PropertySet? Properties { get; private set; }
        public event Action<bool>? PropertiesChanged;

        private const string Hint =
            "Place parts from the palette on the left. Drag from a pin to draw a wire. Drag elements to move them, or their ends to stretch them. " +
            "Click switches and logic inputs to flip them.\n\nR rotates, F flips, Del deletes, Ctrl+Z undoes, Space pauses, the wheel zooms, the middle button pans.";

        private void ShowProperties(List<string> report)
        {
            if (report.Count < 5)
            {
                _selectedIndex = -1;
                _selectedType = "";
                Properties = CircuitProperties();
                PropertiesChanged?.Invoke(false);
                return;
            }
            int.TryParse(report[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _selectedIndex);
            _selectedType = report[1];
            string name = report[2];
            int count = int.TryParse(report[3], out int c) ? c : 1;
            string error = report[4];
            var values = new Dictionary<string, string>();
            foreach (var kv in report.Skip(5))
            {
                int eq = kv.IndexOf('=');
                if (eq > 0) values[kv.Substring(0, eq)] = kv.Substring(eq + 1);
            }

            var entries = new List<PropertyEntry>
            {
                new("name", PropertyEditKind.Text, name) { Tip = "The name of the element: the class generated from the file gets a field of this name" },
            };
            var entry = _catalog.FirstOrDefault(x => x.Type == _selectedType);
            if (entry.Properties is { Length: > 0 } props)
            {
                foreach (var prop in props.Split(';'))
                {
                    int colon = prop.IndexOf(':');
                    int eq = prop.LastIndexOf('=');
                    if (colon < 0 || eq < colon) continue;
                    string key = prop.Substring(0, colon);
                    string kind = prop.Substring(colon + 1, eq - colon - 1);
                    string value = values.TryGetValue(key, out var v) ? v : prop.Substring(eq + 1);
                    entries.Add(Entry(key, kind, value));
                }
            }
            int index = _selectedIndex;
            Properties = new PropertySet
            {
                Title = Spaced(_selectedType) + (count > 1 ? $"  (+{count - 1} more selected)" : ""),
                Error = error == "" ? null : error,
                Entries = entries,
                Set = (key, value) => Send(key == "name" ? $"rename\t{index}\t{value.Trim()}" : $"set\t{index}\t{key}\t{value}"),
            };
            PropertiesChanged?.Invoke(false);
        }

        /// <summary>Nothing selected: the circuit itself - its class and time step, and a control for each potentiometer (group), switch, push button,
        /// selector switch and logic input.</summary>
        private PropertySet CircuitProperties()
        {
            var entries = new List<PropertyEntry>
            {
                new("class", PropertyEditKind.Text, _circuit[0]) { Category = "Circuit", IsSet = _circuit[0] != "", CanClear = true,
                    Tip = "The name of the class generated from the file (empty: the name of the file)" },
                new("namespace", PropertyEditKind.Text, _circuit[1]) { Category = "Circuit", IsSet = _circuit[1] != "", CanClear = true,
                    Tip = "The namespace of the generated class (empty: none)" },
                new("timeStep", PropertyEditKind.Text, _circuit[2]) { Category = "Circuit",
                    Tip = "The time step of the simulation, like 10us: smaller is more exact and slower (a step that does not converge is halved by itself)" },
            };
            var targets = new Dictionary<string, (string Index, string Kind)>();
            foreach (var p in _positions)
            {
                string name = p[2] != "" ? p[2] : "#" + p[0];
                if (targets.ContainsKey(name)) name += " (" + p[0] + ")";
                targets[name] = (p[0], p[1]);
                entries.Add(p[1] switch
                {
                    "slider" => new PropertyEntry(name, PropertyEditKind.Slider, p[3]) { Category = "Positions", Tip = "The position of the potentiometer (or of its group), 0 .. 1" },
                    "button" => new PropertyEntry(name, PropertyEditKind.Momentary, p[3]) { Category = "Positions", Tip = "A push button: closed while it is held" },
                    "choice" => new PropertyEntry(name, PropertyEditKind.Choice, p[3])
                    {
                        Category = "Positions", Tip = "The throw the selector switch is at",
                        Choices = Enumerable.Range(0, int.TryParse(p[4], out int n) && n > 0 ? n : 2).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToList(),
                    },
                    _ => new PropertyEntry(name, PropertyEditKind.Bool, p[3]) { Category = "Positions", Tip = p[1] == "logic" ? "The logic input high (on) or low" : "The switch closed (on) or open" },
                });
            }
            return new PropertySet
            {
                Title = "Circuit",
                Hint = Hint,
                Entries = entries,
                Set = (key, value) =>
                {
                    if (key is "class" or "namespace" or "timeStep") Send($"circuit\t{key}\t{value}");
                    else if (targets.TryGetValue(key, out var target)) Send($"operate\t{target.Index}\t{value}");
                },
            };
        }

        /// <summary>The dialog of the circuit: class, namespace and time step, the ports (Pin elements) and the parts bound to devices.</summary>
        private async Task ShowCircuitDialog()
        {
            var owner = Dialogs.WindowOf(this);
            if (owner == null) return;
            var dialog = new CircuitDialog(_circuit, _ports, (key, value) => Send($"circuit\t{key}\t{value}"), (index, key, value) => Send(key == "name" ? $"rename\t{index}\t{value}" : $"set\t{index}\t{key}\t{value}"));
            void Update() => dialog.Refresh(_circuit, _ports);
            PortsChanged += Update;
            try { await dialog.ShowDialog(owner); }
            finally { PortsChanged -= Update; }
        }

        private PropertyEntry Entry(string key, string kind, string value)
        {
            if (kind == "bool") return new PropertyEntry(key, PropertyEditKind.Bool, value);
            if (kind.StartsWith("choice:") || kind.StartsWith("model:"))
            {
                var options = new List<string>();
                if (kind.StartsWith("choice:")) options.AddRange(kind.Substring(7).Split('/'));
                else foreach (var k in kind.Substring(6).Split('/')) if (_models.TryGetValue(k, out var names)) options.AddRange(names);
                return new PropertyEntry(key, PropertyEditKind.Choice, value) { Choices = options };
            }
            string unit = kind is "text" or "int" ? "" : kind == "Ohm" ? "Ω" : kind;
            return new PropertyEntry(key, PropertyEditKind.Text, value)
            {
                Unit = unit,
                Tip = kind == "text" ? "Text" : "A number with an SI prefix (4.7k, 10u, 2.2M) or an expression of the parameters of the circuit",
            };
        }

        // ---- import ------------------------------------------------------------------------------------------------------------------------------
        private async Task ImportFromFile()
        {
            var top = TopLevel.GetTopLevel(this);
            if (top == null) return;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import a circuit of Falstad's simulator",
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("Falstad circuit") { Patterns = new[] { "*.txt", "*.circuitjs.txt", "*" } } },
            });
            if (files.Count == 0) return;
            string? path = files[0].TryGetLocalPath();
            if (path == null) return;
            await Import(await File.ReadAllTextAsync(path));
        }

        private async Task ImportFromClipboard()
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null) return;
            string? text = await clipboard.TryGetTextAsync();
            if (string.IsNullOrWhiteSpace(text)) { await Dialogs.Message(Dialogs.WindowOf(this), "The clipboard holds no text. In Falstad's simulator: File > Export As Text, then copy the text.", "Import"); return; }
            await Import(text);
        }

        private async Task Import(string falstad)
        {
            var result = FalstadImport.Convert(falstad);
            if (result.Elements == 0) { await Dialogs.Message(Dialogs.WindowOf(this), "The text holds no circuit of Falstad's simulator.", "Import"); return; }
            if (!await ConfirmReplace("Import")) return;
            Send("load\t" + result.Biscuit);
            Send("fit");
            if (result.Warnings.Count > 0)
                await Dialogs.Message(Dialogs.WindowOf(this), $"{result.Elements} elements imported. Not everything could be taken over:\n\n" + string.Join("\n", result.Warnings.Take(20)) +
                    (result.Warnings.Count > 20 ? $"\n... and {result.Warnings.Count - 20} more" : ""), "Import");
        }

        // ---- samples -----------------------------------------------------------------------------------------------------------------------------
        private async Task LoadSample(CircuitSamples.Sample sample)
        {
            if (!await ConfirmReplace("Samples")) return;
            Send("load\t" + sample.Text);
            Send("fit");
        }

        /// <summary>Asks before the drawn circuit is replaced (true at once when nothing is drawn; the replacement can be undone).</summary>
        private async Task<bool> ConfirmReplace(string title)
        {
            if (!HasElements(_text)) return true;
            var answer = await Dialogs.Ask(Dialogs.WindowOf(this), "The circuit that is drawn will be replaced (Undo brings it back). Replace it?", title,
                ("Replace", Dialogs.Answer.Yes), ("Cancel", Dialogs.Answer.Cancel));
            return answer == Dialogs.Answer.Yes;
        }

        /// <summary>Does the text of a schematic hold an element?</summary>
        internal static bool HasElements(string text)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(text);
                return doc.RootElement.TryGetProperty("elements", out var elements) && elements.ValueKind == System.Text.Json.JsonValueKind.Array && elements.GetArrayLength() > 0;
            }
            catch (System.Text.Json.JsonException) { return !string.IsNullOrWhiteSpace(text); }
        }

        // ---- IDocumentView -------------------------------------------------------------------------------------------------------------------------
        public string GetText() => _text;

        public void ResetTo(string text, string? filePath)
        {
            FilePath = filePath;
            _text = text;
            _savedText = text;
            _baselinePending = true;
            if (_session != null && _session.IsRunning) Send("load\t" + text);
            else if (IsAttachedToVisualTree()) StartSession(text);
            ModifiedChanged?.Invoke();
        }

        public void MarkSaved()
        {
            _savedText = _text;
            ModifiedChanged?.Invoke();
        }

        public int GetCaretLine() => 1;
        public void FocusEditor() => _surface.Focus();
        public bool CanUndo => _canUndo;
        public bool CanRedo => _canRedo;
        public bool HasSelection => _selectedIndex >= 0;
        public void Undo() => Send("undo");
        public void Redo() => Send("redo");
        public void Cut() { }
        public void Copy() { }
        public void Paste() { }
        public void Delete() => Send("delete");
        public void SelectAll() => Send("selectall");
        public void Find() { }
        public void FindNext() { }
        public void FindPrevious() { }
        public int LineCount => 0;
        public void GoToLine(int line) { }
    }
}
