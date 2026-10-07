using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Dock.Model.Avalonia.Json;
using Dock.Model.Controls;
using Dock.Model.Core;
using fire.Compiler;
using fire.Compiler.Assembly;
using DockDocument = Dock.Model.Avalonia.Controls.Document;
using DockTool = Dock.Model.Avalonia.Controls.Tool;

namespace fire.Editor
{
    /// <summary>The main window of the editor: SEVERAL documents in tabs (Dock.Avalonia document area) - fire scripts (ScriptEditorControl),
    /// Markdown documents (MarkdownEditorControl) and packet traces next to each other, each in a tab of its own.
    ///
    /// All functions (run/debug, breakpoints, error list, build settings, save, status bar) act on the ACTIVE document only; likewise only the text of the active script
    /// belongs to the sources of the compiler - to translate several files together, include them with #include (relative paths refer to the folder of the active file).
    ///
    /// This window is only the ORCHESTRATION (file menu, run, hotkeys, output window); the controls (ScriptEditorControl, MarkdownEditorControl, the debugger panels)
    /// know neither each other nor DebugSession/compiling. It is split into several files: layout and documents (this one), run/debug (MainWindow.Run.cs),
    /// files (MainWindow.Files.cs), build and settings (MainWindow.Build.cs).</summary>
    public partial class MainWindow : Window
    {
        private readonly DebugSession _session = new();
        private readonly EditorDeviceService _devices = new();
        private readonly DebuggerPanels _debugger;

        private readonly OutputPanelControl _output = new();
        private readonly ErrorListPanelControl _errorList = new();
        private readonly ThreadsPanelControl _threadsPanel = new();
        private readonly ScopePanelControl _scopePanel = new();
        private readonly StackPanelControl _stackPanel = new();
        private readonly DevicesPanelControl _devicesPanel = new();
        private readonly SolutionExplorerControl _solutionPanel = new();

        private AssemblyInfo _scriptAssemblyInfo = new();

        // The documents (tabs) and the layout of the docking areas.
        private readonly SparkFactory _factory;
        private readonly Dictionary<string, DockTool> _tools = new();
        private string _defaultLayout = "";
        private bool _layoutLoaded;

        private static readonly Dictionary<string, string> PanelTitles = new()
        {
            ["output"] = "Output", ["errors"] = "Error List", ["threads"] = "Threads", ["scope"] = "Scope", ["stack"] = "Stack", ["devices"] = "Devices", ["solution"] = "Solution Explorer",
        };

        /// <summary>What kind of document a tab holds.</summary>
        private enum DocumentKind
        {
            /// <summary>A fire script.</summary>
            Script,
            /// <summary>A Markdown document.</summary>
            Markdown,
            /// <summary>A packet trace (recorded live or loaded from a .fplog file).</summary>
            PacketLog,
            /// <summary>The markup of a user interface (.fxml, docs/UI_MARKUP.md) with its design view.</summary>
            UiMarkup,
            /// <summary>A text file that is no fire code: the C++ of natives, notes, data.</summary>
            Text,
            /// <summary>A picture (a resource of a project), to look at.</summary>
            Image,
            /// <summary>A picture in the pixel editor (palette of 256 colours or true colour).</summary>
            Pixel,
            /// <summary>Any file as bytes in the hex editor.</summary>
            Hex,
        }

        /// <summary>An open tab. `Layout` is the docking element that shows the view (the view - the editor control - stays the same when the layout is rebuilt).</summary>
        private sealed class OpenDocument
        {
            public required string Id { get; init; }
            public required IDocumentView View { get; init; }
            public required DocumentKind Kind { get; init; }
            public required int Number { get; init; }

            /// <summary>Name as long as there is no file (instead of "Untitled N"), e.g. "Packets serial:COM3".</summary>
            public string? UntitledName { get; init; }
            public DockDocument Layout { get; set; } = null!;

            /// <summary>Files shown in this tab by following links in a read-only Markdown document (back/forward with Alt+Left/Right).</summary>
            public List<string> History { get; } = new();
            public int HistoryIndex { get; set; }

            public bool IsMarkdown => Kind == DocumentKind.Markdown;
            public ScriptEditorControl? Script => View as ScriptEditorControl;
            public MarkdownEditorControl? Markdown => View as MarkdownEditorControl;
            public PacketTraceControl? Trace => View as PacketTraceControl;
            public MarkupEditorControl? Design => View as MarkupEditorControl;

            /// <summary>File name or "Untitled N" (Markdown: with .md).</summary>
            public string DisplayName => View.FilePath != null
                ? Path.GetFileName(View.FilePath)
                : UntitledName ?? (IsMarkdown ? $"Untitled {Number}.md" : $"Untitled {Number}");
        }

        private readonly List<OpenDocument> _documents = new();
        private int _documentCounter;

        // The document that was active last - the source of "the current document" (see ActiveDocument).
        private OpenDocument? _active;

        // The documents whose unsaved changes were already asked about (closing them goes through).
        private readonly HashSet<OpenDocument> _closeApproved = new();

        private OpenDocument? ActiveDocument
        {
            get
            {
                if (_active != null && _documents.Contains(_active)) return _active;
                var selected = DocumentDock?.ActiveDockable;
                return _documents.FirstOrDefault(d => ReferenceEquals(d.Layout, selected)) ?? _documents.FirstOrDefault();
            }
        }

        /// <summary>The active document, if it is a fire script.</summary>
        private ScriptEditorControl? ActiveScript => ActiveDocument?.Script;

        private static readonly HashSet<string> ImageExtensions = new() { ".png", ".bmp", ".gif", ".jpg", ".jpeg" };
        private static readonly HashSet<string> TextExtensions = new() { ".txt", ".json", ".xml", ".csv", ".ini", ".cfg", ".yaml", ".yml", ".html", ".htm", ".css", ".js", ".log", ".tsv", ".toml", ".cmake", ".mk", ".sh", ".bat", ".cs" };

        private static readonly HashSet<string> ScriptExtensions = new() { ".script", ".fi", ".fic", ".fire", "" };

        /// <summary>Does the file start with bytes that no text has (a zero byte in the first 8 KB)? A file that cannot be read is not binary (opening it reports the problem).</summary>
        private static bool LooksBinary(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                var buffer = new byte[8192];
                int n = stream.Read(buffer, 0, buffer.Length);
                return buffer.AsSpan(0, n).Contains((byte)0);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }

        private static DocumentKind KindOfPath(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".md" or ".markdown" or ".mdown") return DocumentKind.Markdown;
            if (ext == fire.Device.Manager.DeviceManager.PacketLog.FileExtension) return DocumentKind.PacketLog;
            if (ext == ".fxml") return DocumentKind.UiMarkup;
            if (ImageExtensions.Contains(ext)) return DocumentKind.Image;
            if (TextExtensions.Contains(ext) || CppHighlighter.IsCppFile(path)) return DocumentKind.Text;
            if (!ScriptExtensions.Contains(ext) && LooksBinary(path)) return DocumentKind.Hex;   // data that is no text: bytes
            return DocumentKind.Script;
        }

        public MainWindow()
        {
            InitializeComponent();

            _factory = new SparkFactory(
                new()
                {
                    ("output", PanelTitles["output"], _output),
                    ("errors", PanelTitles["errors"], _errorList),
                    ("threads", PanelTitles["threads"], _threadsPanel),
                    ("scope", PanelTitles["scope"], _scopePanel),
                    ("stack", PanelTitles["stack"], _stackPanel),
                },
                new() { ("solution", PanelTitles["solution"], _solutionPanel), ("devices", PanelTitles["devices"], _devicesPanel) });
            var layout = _factory.CreateLayout();
            _factory.InitLayout(layout);
            DockHost.Factory = _factory;
            DockHost.Layout = layout;
            CollectTools();
            HookFactory();

            _strips = new ToolStripManager(this, DockArea, DockHint, trayTop, trayBottom, trayLeft, trayRight);
            _strips.Adopt();
            BuildToolbarsMenu();
            _defaultStrips = _strips.Save();

            RegisterToolchainPrompts();
            Opened += (_, _) => OnFirstShown();

            // The hotkeys: a handler that sees the key BEFORE the controls (tunnelling) and also when a control already handled it, so that F5/F10 reach us wherever the focus is.
            AddHandler(KeyDownEvent, Window_KeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
            // The mouse back/forward buttons navigate like Alt+Left/Right in read-only Markdown tabs.
            AddHandler(PointerPressedEvent, (_, e) =>
            {
                if (ActiveDocument is not { Markdown.IsReadOnly: true } d) return;
                var props = e.GetCurrentPoint(this).Properties;
                if (props.IsXButton1Pressed) { NavigateHistory(d, -1); e.Handled = true; }
                else if (props.IsXButton2Pressed) { NavigateHistory(d, +1); e.Handled = true; }
            }, RoutingStrategies.Tunnel);
            AddHandler(DragDrop.DragOverEvent, Window_DragOver);
            AddHandler(DragDrop.DropEvent, Window_Drop);

            InitProjects();

            _debugger = new DebuggerPanels(_threadsPanel, _scopePanel, _stackPanel);
            _debugger.AttachSession(_session);
            _debugger.ThreadSelected += OnThreadSelected;

            // The shared DeviceManager of the editor: all scripts use it together (see EditorDeviceService).
            _session.DeviceManager = _devices.Manager;
            _devicesPanel.Attach(_devices);
            _devicesPanel.StatusMessage += UpdateStatus;
            _devicesPanel.OpenTraceRequested += OpenTrace;
            _devices.Manager.DevicesChanged += () => Dispatcher.UIThread.Post(RefreshDefaultDeviceCombo);
            _devices.Manager.DefaultChanged += () => Dispatcher.UIThread.Post(RefreshDefaultDeviceCombo);
            mnuLoopback.IsChecked = _devices.LoopbackEnabled;
            RefreshDefaultDeviceCombo();

            _errorList.CountChanged += count =>
            {
                if (_tools.TryGetValue("errors", out var tool)) tool.Title = count == 0 ? PanelTitles["errors"] : $"{PanelTitles["errors"]} ({count})";
            };
            _errorList.ItemActivated += item =>
            {
                if (ActiveScript is not { } script) return;
                script.ScrollToLine(item.Line);
                script.SetCaretByLineColumn(Math.Max(0, item.Line - 1), 0);
                script.Focus();
            };

            _session.OutputWritten += OnScriptOutput;
            // IMPORTANT: Post (non-blocking), NOT Invoke - these handlers can fire from the OWN background thread of a fire thread that is just starting/pausing while the
            // UI thread ITSELF sits synchronously in a "run to the end" of the main thread (e.g. RunToCompletion) that in turn waits for a message from exactly this fire
            // thread (e.g. via `process`) - a blocking Invoke would deadlock then (the fire thread waits for the UI thread, the UI thread transitively for the fire thread).
            _session.ThreadAdded += ctx => Dispatcher.UIThread.Post(() => _debugger.Refresh(BreakpointDescriptions()));
            _session.ThreadPaused += ctx => Dispatcher.UIThread.Post(() =>
            {
                // Only when the thread that is SHOWN (the active one) is affected, the details (highlight/scope/stack) have to be rebuilt - another fire thread that continues
                // automatically and e.g. reaches a breakpoint just updates its own entry in the thread list.
                _debugger.Refresh(BreakpointDescriptions());
                if (ReferenceEquals(ctx, _session.ActiveThread))
                {
                    _isBusy = false;
                    AfterStep(!ctx.IsFinished);
                }
            });

            // Runs CONTINUOUSLY - output can arrive at any time, even in the middle of a long run, and should become visible soon, only bundled instead of one by one.
            _outputFlushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(80), DispatcherPriority.Normal, (_, _) => FlushPendingOutput());
            _outputFlushTimer.Start();

            UpdateExecutionModeSelection(_session);
            UpdateStatus("Ready.");
        }

        private void OnFirstShown()
        {
            // The solution explorer and the devices start collapsed (pinned to the right edge; the explorer opens when a project is opened) - before the default layout is saved.
            foreach (var id in new[] { "solution", "devices" })
                if (_tools.TryGetValue(id, out var collapsed))
                {
                    try { _factory.PinDockable(collapsed); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
                }
            _defaultLayout = SerializeLayout();
            LoadLayout();
            LoadToolStrips();

            // Files passed on the command line, else an empty welcome script.
            foreach (var arg in (Environment.GetCommandLineArgs()).Skip(1))
                if (File.Exists(arg)) OpenFile(arg);
            _ = _devices.RefreshAsync(fastScan: true); // list the devices (fast); the availability check is done by "Search"
            if (_documents.Count == 0)
                NewScript("// Welcome to the fire editor\nprint(\"Hello, world!\")\n");
        }

        private void UpdateStatus(string text) => StatusText.Text = text;

        // -----------------------------------------------------------
        // Docking areas: the View menu and the saved layout
        // -----------------------------------------------------------

        private static string LayoutFilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "fire", "editor-layout.json");

        private IRootDock? RootDock => DockHost.Layout as IRootDock;

        private IDock? DocumentDock => RootDock == null ? null : _factory.Find(RootDock, d => d.Id == SparkFactory.DocumentsId).OfType<IDock>().FirstOrDefault();

        /// <summary>Collects the tools of the current layout by id (after loading a layout the elements are new ones).</summary>
        private void CollectTools()
        {
            _tools.Clear();
            foreach (var tool in AllTools(RootDock))
                if (tool.Id != null) _tools[tool.Id] = tool;
        }

        /// <summary>All tools of a layout: the docked ones, the hidden ones and the ones pinned to an edge (a search through the visible dockables does not find the latter two).</summary>
        private IEnumerable<DockTool> AllTools(IRootDock? root)
        {
            if (root == null) yield break;
            var found = new HashSet<DockTool>();
            foreach (var tool in _factory.Find(root, _ => true).OfType<DockTool>()) if (found.Add(tool)) yield return tool;
            foreach (var list in new[] { root.HiddenDockables, root.LeftPinnedDockables, root.RightPinnedDockables, root.TopPinnedDockables, root.BottomPinnedDockables })
                if (list != null)
                    foreach (var tool in list.OfType<DockTool>()) if (found.Add(tool)) yield return tool;
        }

        private void HookFactory()
        {
            _factory.ActiveDockableChanged += (_, e) => OnDockableActivated(e.Dockable);
            _factory.FocusedDockableChanged += (_, e) => OnDockableActivated(e.Dockable);
            _factory.DockableClosing += (_, e) =>
            {
                if (e.Dockable is DockTool tool)
                {
                    // the close button of a tool window hides it (it comes back with the View menu)
                    e.Cancel = true;
                    Dispatcher.UIThread.Post(() => _factory.HideDockable(tool));
                }
                else if (FindDocument(e.Dockable) is { } doc && !_closeApproved.Contains(doc) && doc.View.IsModified)
                {
                    e.Cancel = true;
                    _ = ConfirmThenClose(doc);
                }
            };
            _factory.DockableClosed += (_, e) =>
            {
                if (FindDocument(e.Dockable) is { } doc) OnDocumentClosed(doc);
            };
        }

        private OpenDocument? FindDocument(IDockable? dockable) =>
            dockable == null ? null : _documents.FirstOrDefault(d => ReferenceEquals(d.Layout, dockable));

        private void OnDockableActivated(IDockable? dockable)
        {
            if (FindDocument(dockable) is { } doc && !ReferenceEquals(_active, doc))
            {
                _active = doc;
                RefreshActiveUi();
            }
        }

        private async Task ConfirmThenClose(OpenDocument doc)
        {
            if (!await ConfirmClose(doc)) return;
            _closeApproved.Add(doc);
            _factory.CloseDockable(doc.Layout);
        }

        private string SerializeLayout()
        {
            // the open documents belong to the session, not to the layout: they are taken out of the document area for a moment
            var dock = DocumentDock as Dock.Model.Avalonia.Controls.DocumentDock;
            var held = dock?.VisibleDockables?.ToList() ?? new List<IDockable>();
            var active = dock?.ActiveDockable;
            if (dock != null) foreach (var d in held) dock.VisibleDockables!.Remove(d);
            try { return new AvaloniaDockSerializer().Serialize(DockHost.Layout!); }
            finally
            {
                if (dock != null)
                {
                    foreach (var d in held) dock.VisibleDockables!.Add(d);
                    dock.ActiveDockable = active;
                }
            }
        }

        /// <summary>Replaces the layout by the one stored in `json` and puts the panels and the open tabs back into it.</summary>
        private void ApplyLayout(string json)
        {
            var layout = new AvaloniaDockSerializer().Deserialize<IRootDock?>(json) ?? throw new InvalidDataException("empty layout");

            // the panels get their content again by id (a layout stores only the ids), the documents go back into the document area
            var contents = new Dictionary<string, Control>
            {
                ["output"] = _output, ["errors"] = _errorList, ["threads"] = _threadsPanel, ["scope"] = _scopePanel, ["stack"] = _stackPanel, ["devices"] = _devicesPanel, ["solution"] = _solutionPanel,
            };
            MakeObservable(layout, new HashSet<IDockable>());
            _factory.InitLayout(layout);
            foreach (var tool in AllTools(layout).ToList())
            {
                if (tool.Id != null && contents.TryGetValue(tool.Id, out var content))
                {
                    tool.Content = content;
                    tool.Title = PanelTitles[tool.Id];
                }
            }
            DockHost.Layout = layout;

            var documentDock = DocumentDock as Dock.Model.Avalonia.Controls.DocumentDock
                ?? throw new InvalidDataException("the layout has no document area");
            foreach (var doc in _documents)
            {
                doc.Layout = NewDockDocument(doc);
                documentDock.AddDocument(doc.Layout);
            }
            CollectTools();
            if (!contents.Keys.All(_tools.ContainsKey)) throw new InvalidDataException("the layout is incomplete");
            if (ActiveDocument is { } active) Activate(active);
            RefreshActiveUi();
        }

        /// <summary>The serializer creates plain lists; the docking controls only follow changes of observable ones (a tab added later would not show up). Replaces every list of the tree.</summary>
        private static void MakeObservable(IDockable dockable, HashSet<IDockable> seen)
        {
            if (!seen.Add(dockable)) return;
            static ObservableCollection<IDockable>? Wrap(IList<IDockable>? list) =>
                list == null ? null : list is ObservableCollection<IDockable> o ? o : new ObservableCollection<IDockable>(list);

            if (dockable is IRootDock root)
            {
                root.HiddenDockables = Wrap(root.HiddenDockables);
                root.LeftPinnedDockables = Wrap(root.LeftPinnedDockables);
                root.RightPinnedDockables = Wrap(root.RightPinnedDockables);
                root.TopPinnedDockables = Wrap(root.TopPinnedDockables);
                root.BottomPinnedDockables = Wrap(root.BottomPinnedDockables);
                foreach (var pinned in new[] { root.HiddenDockables, root.LeftPinnedDockables, root.RightPinnedDockables, root.TopPinnedDockables, root.BottomPinnedDockables })
                    if (pinned != null) foreach (var d in pinned) MakeObservable(d, seen);
            }
            if (dockable is IDock dock)
            {
                dock.VisibleDockables = Wrap(dock.VisibleDockables);
                if (dock.VisibleDockables != null) foreach (var child in dock.VisibleDockables.ToList()) MakeObservable(child, seen);
            }
        }

        private void LoadLayout()
        {
            if (_layoutLoaded) return;
            _layoutLoaded = true;
            if (!File.Exists(LayoutFilePath)) return;
            try { ApplyLayout(File.ReadAllText(LayoutFilePath)); }
            catch (Exception ex)
            {
                // A damaged or too old layout must not make the editor unusable.
                System.Diagnostics.Debug.WriteLine(ex);
                RestoreDefaultLayout();
            }
        }

        private void SaveLayout()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LayoutFilePath)!);
                File.WriteAllText(LayoutFilePath, SerializeLayout());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex); // cannot be saved (e.g. a read-only profile): no reason to disturb the closing
            }
        }

        private void RestoreDefaultLayout()
        {
            try { ApplyLayout(_defaultLayout); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
                UpdateStatus("The layout could not be reset.");
            }
        }

        private bool _closeConfirmed;

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            base.OnClosing(e);
            if (e.Cancel) return;
            if (!_closeConfirmed && _documents.Any(d => d.View.IsModified))
            {
                // unsaved tabs: ask for each one; "Cancel" stops the closing of the window (the questions are asynchronous, so the closing is repeated afterwards)
                e.Cancel = true;
                _ = ConfirmAllThenClose();
                return;
            }
            SaveLayout();
            SaveToolStrips();
            _strips.CloseFloatingWindows();
            _devices.Shutdown(); // disconnects the devices; only the owner may take down the shared manager
        }

        private async Task ConfirmAllThenClose()
        {
            foreach (var doc in _documents.ToList())
                if (!await ConfirmClose(doc)) return;
            _closeConfirmed = true;
            Close();
        }

        private void ResetLayout_Click(object? sender, RoutedEventArgs e)
        {
            RestoreDefaultLayout();
            try { File.Delete(LayoutFilePath); } catch (IOException) { }
            ResetToolStrips();
        }

        // -----------------------------------------------------------
        // The tool strips: arrangement saved next to the layout of the docking areas, View > Toolbars
        // -----------------------------------------------------------

        private readonly ToolStripManager _strips;
        private readonly string _defaultStrips;

        private static string ToolStripsFilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "fire", "editor-toolbars.json");

        private void LoadToolStrips()
        {
            if (!File.Exists(ToolStripsFilePath)) return;
            try { _strips.Load(File.ReadAllText(ToolStripsFilePath)); }
            catch (Exception ex)
            {
                // a damaged file must not make the editor unusable
                System.Diagnostics.Debug.WriteLine(ex);
                ResetToolStrips();
            }
        }

        private void SaveToolStrips()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ToolStripsFilePath)!);
                File.WriteAllText(ToolStripsFilePath, _strips.Save());
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        }

        private void ResetToolStrips()
        {
            try { _strips.Load(_defaultStrips); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
            try { File.Delete(ToolStripsFilePath); } catch (IOException) { }
        }

        private void BuildToolbarsMenu()
        {
            foreach (var strip in _strips.Strips)
            {
                var item = new MenuItem { Header = strip.Title, Tag = strip.Id, ToggleType = MenuItemToggleType.CheckBox };
                item.Click += (_, _) => _strips.SetVisible(strip, !strip.IsVisible);
                mnuToolbars.Items.Add(item);
            }
            mnuToolbars.Items.Add(new Separator());
            var reset = new MenuItem { Header = "_Reset Toolbars" };
            reset.Click += (_, _) => ResetToolStrips();
            mnuToolbars.Items.Add(reset);
        }

        private void ToolbarsMenu_SubmenuOpened(object? sender, RoutedEventArgs e)
        {
            foreach (var item in mnuToolbars.Items.OfType<MenuItem>())
                if (item.Tag is string id && _strips.Find(id) is { } strip) item.IsChecked = strip.IsVisible;
        }

        private void ViewMenu_SubmenuOpened(object? sender, RoutedEventArgs e)
        {
            foreach (var item in mnuView.Items.OfType<MenuItem>())
                if (item.Tag is string id && _tools.TryGetValue(id, out var tool))
                    item.IsChecked = IsToolVisible(tool);
        }

        private bool IsToolVisible(DockTool tool) => RootDock?.HiddenDockables?.Contains(tool) != true;

        private void ViewPanel_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: string id } item || !_tools.TryGetValue(id, out var tool)) return;

            if (RootDock != null && _factory.IsDockablePinned(tool, RootDock))
            {
                // collapsed (pinned to the edge): the menu takes it out and docks it
                _factory.UnpinDockable(tool);
                _factory.SetActiveDockable(tool);
            }
            else if (IsToolVisible(tool)) _factory.HideDockable(tool);
            else
            {
                _factory.RestoreDockable(tool);
                _factory.SetActiveDockable(tool);
            }
            item.IsChecked = IsToolVisible(tool);
        }

        private void OnThreadSelected(DebugThreadContext chosen)
        {
            // A step still pending of the PREVIOUSLY active thread would leave _isBusy set forever (its ThreadPaused event fires later with a ctx that is no longer the active one) -
            // so always reset it when the thread changes, otherwise the step buttons stay locked.
            _isBusy = false;
            ShowDebugLocation(chosen.IsFinished ? null : chosen.Vm.CurrentLocation);
            _debugger.Refresh(BreakpointDescriptions());
            UpdateStatus(chosen.IsFinished
                ? $"{chosen.Name}: finished."
                : $"{chosen.Name}: paused at line {chosen.Vm.CurrentLine}.");
        }

        // -----------------------------------------------------------
        // Error list
        // -----------------------------------------------------------

        private void UpdateErrorPanel()
        {
            var doc = ActiveDocument;
            var diagnostics = doc?.Script?.Diagnostics ?? (IReadOnlyList<Diagnostic>)Array.Empty<Diagnostic>();
            string file = doc?.DisplayName ?? "";
            _errorList.SetItems(diagnostics.Select(d => new ErrorListItem("Error", d.Message, file, d.Line)).ToList());
        }

        // -----------------------------------------------------------
        // Documents: create, open, activate, close
        // -----------------------------------------------------------

        private OpenDocument NewScript(string text) => CreateDocument(DocumentKind.Script, text, null);

        private OpenDocument NewMarkdown(string text) => CreateDocument(DocumentKind.Markdown, text, null);

        private OpenDocument CreateDocument(DocumentKind kind, string text, string? path, string? untitledName = null,
            MarkdownViewMode mode = MarkdownViewMode.Edit)
        {
            IDocumentView view = kind switch
            {
                DocumentKind.Markdown => new MarkdownEditorControl { Mode = mode },
                DocumentKind.PacketLog => new PacketTraceControl(),
                DocumentKind.UiMarkup => new MarkupEditorControl(),
                DocumentKind.Text => new TextFileEditorControl(),
                DocumentKind.Image => new ImageViewerControl(),
                DocumentKind.Pixel => new PixelEditorControl(),
                DocumentKind.Hex => new HexEditorControl(),
                _ => new ScriptEditorControl(),
            };

            var doc = new OpenDocument
            {
                Id = Guid.NewGuid().ToString("N"),
                View = view,
                Kind = kind,
                Number = path == null ? ++_documentCounter : 0,
                UntitledName = untitledName,
            };

            view.ResetTo(text, path);
            view.ModifiedChanged += () => UpdateTitle(doc);
            view.CaretLineChanged += line =>
            {
                if (ReferenceEquals(ActiveDocument, doc)) CaretText.Text = $"Line {line}";
            };

            if (doc.Markdown is { } md)
                md.OpenFileRequested += (p, anchor, newTab) => HandleMarkdownLink(doc, p, anchor, newTab);

            if (doc.Design is { } design)
                design.ShowScriptRequested += () => ShowGeneratedScript(doc);

            if (view is ImageViewerControl viewer)
                viewer.EditRequested += () => { if (viewer.FilePath != null) OpenFile(viewer.FilePath, forceKind: DocumentKind.Pixel); };

            if (doc.Script is { } script)
            {
                AttachProjectSupport(script);
                // Ctrl+click on an #include or on a symbol of an included file: open the file in a tab.
                script.OpenFileRequested += (target, line) =>
                {
                    if (OpenFile(target) is { } opened)
                        // only after the first layout of the (possibly new) tab, otherwise the editor cannot scroll yet
                        Dispatcher.UIThread.Post(() => opened.View.GoToLine(line), DispatcherPriority.Loaded);
                };
                script.DiagnosticsChanged += () =>
                {
                    if (ReferenceEquals(ActiveDocument, doc)) UpdateErrorPanel();
                };
                script.BreakpointsChanged += () =>
                {
                    if (TakesPartInRun(doc)) _session.UpdateBreakpoints(BreakpointLocations());
                    _debugger.Refresh(BreakpointDescriptions());
                };
            }

            doc.Layout = NewDockDocument(doc);
            _documents.Add(doc);
            if (DocumentDock is Dock.Model.Avalonia.Controls.DocumentDock dock) dock.AddDocument(doc.Layout);
            UpdateTitle(doc);
            Activate(doc);
            return doc;
        }

        private static DockDocument NewDockDocument(OpenDocument doc) =>
            new() { Id = "doc:" + doc.Id, Title = doc.DisplayName + (doc.View.IsModified ? "*" : ""), Content = doc.View, CanClose = true, CanFloat = true };

        private void Activate(OpenDocument doc)
        {
            _active = doc;
            if (doc.Layout.Owner is IDock owner)
            {
                _factory.SetActiveDockable(doc.Layout);
                _factory.SetFocusedDockable(owner, doc.Layout);
            }
            RefreshActiveUi();
            Dispatcher.UIThread.Post(doc.View.FocusEditor, DispatcherPriority.Loaded);
        }

        private void OnDocumentClosed(OpenDocument doc)
        {
            if (ReferenceEquals(_highlightedDoc, doc)) _highlightedDoc = null;
            doc.Trace?.Detach();
            _documents.Remove(doc);
            _closeApproved.Remove(doc);
            if (ReferenceEquals(_active, doc)) _active = null;
            if (ReferenceEquals(_debugDocument, doc))
            {
                // The tab of the running program was closed: end the run.
                _session.Reset();
                _isBusy = false;
                _debugDocument = null;
            }
            RefreshActiveUi();
        }

        /// <summary>Tab title: the name, with a star for unsaved changes.</summary>
        private void UpdateTitle(OpenDocument doc)
        {
            doc.Layout.Title = doc.DisplayName + (doc.View.IsModified ? "*" : "");
            if (ReferenceEquals(doc, ActiveDocument)) UpdateWindowTitle(WindowTitle(doc));
        }

        private static string? WindowTitle(OpenDocument? doc) =>
            doc == null ? null : $"{doc.DisplayName}{(doc.View.IsModified ? "*" : "")}{(doc.View.IsReadOnly ? " (read-only)" : "")}";

        private void UpdateWindowTitle(string? subtitle) => Title = subtitle != null ? $"spark • {subtitle}" : "spark";

        /// <summary>Updates everything that depends on the active document: window title, line display, error list, breakpoints in the debugger panel.</summary>
        private void RefreshActiveUi()
        {
            var doc = ActiveDocument;
            UpdateWindowTitle(WindowTitle(doc));
            CaretText.Text = doc == null ? "" : $"Line {doc.View.GetCaretLine()}";
            UpdateErrorPanel();
            UpdateSaveCommands();
            RefreshProjectUi();
            _debugger.Refresh(BreakpointDescriptions());
        }

        /// <summary>Save, Save As and the Save tool bar button are disabled for read-only documents; Save All when nothing is saveable.</summary>
        private void UpdateSaveCommands()
        {
            bool canSave = ActiveDocument is { View.IsReadOnly: false };
            mnuSave.IsEnabled = mnuSaveAs.IsEnabled = btnSave.IsEnabled = canSave;
            mnuSaveAll.IsEnabled = _documents.Any(d => !d.View.IsReadOnly);
        }

        // -----------------------------------------------------------
        // Edit menu (acts on the active document)
        // -----------------------------------------------------------

        private async void EditMenu_SubmenuOpened(object? sender, RoutedEventArgs e)
        {
            var view = ActiveDocument?.View;
            mnuUndo.IsEnabled = view?.CanUndo == true;
            mnuRedo.IsEnabled = view?.CanRedo == true;
            mnuCopy.IsEnabled = view?.HasSelection == true;
            mnuCut.IsEnabled = mnuDelete.IsEnabled = view?.HasSelection == true && !view.IsReadOnly;
            mnuGoToDefinition.IsEnabled = ActiveScript?.CanGoToDefinition() == true;
            mnuToggleComment.IsEnabled = ActiveScript != null;
            mnuPaste.IsEnabled = view is { IsReadOnly: false } && await EditorCommands.ClipboardHasText(this);
        }

        private void Undo_Click(object? sender, RoutedEventArgs e) => ActiveDocument?.View.Undo();
        private void Redo_Click(object? sender, RoutedEventArgs e) => ActiveDocument?.View.Redo();
        private void Cut_Click(object? sender, RoutedEventArgs e) => ActiveDocument?.View.Cut();
        private void Copy_Click(object? sender, RoutedEventArgs e) => ActiveDocument?.View.Copy();
        private void Paste_Click(object? sender, RoutedEventArgs e) => ActiveDocument?.View.Paste();
        private void Delete_Click(object? sender, RoutedEventArgs e) => ActiveDocument?.View.Delete();
        private void SelectAll_Click(object? sender, RoutedEventArgs e) => ActiveDocument?.View.SelectAll();
        private void Find_Click(object? sender, RoutedEventArgs e) => ActiveDocument?.View.Find();
        private void FindNext_Click(object? sender, RoutedEventArgs e) => ActiveDocument?.View.FindNext();
        private void FindPrevious_Click(object? sender, RoutedEventArgs e) => ActiveDocument?.View.FindPrevious();

        private void GoToDefinition_Click(object? sender, RoutedEventArgs e)
        {
            if (ActiveScript is { } script) script.GoToDefinition();
            else UpdateStatus("Go to Definition is only available in script tabs.");
        }

        private void ToggleComment_Click(object? sender, RoutedEventArgs e) => ActiveScript?.ToggleComment();

        private async void GoToLine_Click(object? sender, RoutedEventArgs e)
        {
            if (ActiveDocument is not { } doc) return;
            string? text = await Dialogs.Input(this, $"Line number (1 - {doc.View.LineCount}):", "Go to Line", doc.View.GetCaretLine().ToString());
            if (int.TryParse(text?.Trim(), out int line)) doc.View.GoToLine(line);
        }

        private void ToggleMarkdownPreview_Click(object? sender, RoutedEventArgs e)
        {
            ActiveDocument?.Markdown?.TogglePreview();
            ActiveDocument?.Design?.TogglePreview();
        }

        /// <summary>Opens the script that is generated from a markup in a new tab (to look at; the script of a markup is never stored, `#include "x.fxml"` generates it on the fly).</summary>
        private void ShowGeneratedScript(OpenDocument markup)
        {
            if (markup.Design is not { } design) return;
            bool ok = design.TryGenerate(out string script);
            if (!ok)
            {
                UpdateStatus("The markup has mistakes: " + script);
                _ = Dialogs.Message(this, script, "UI markup");
                return;
            }
            string name = Path.GetFileNameWithoutExtension(markup.DisplayName) + ".generated";
            var generated = CreateDocument(DocumentKind.Script, script, null, untitledName: name);
            UpdateStatus($"Generated script of {markup.DisplayName}.");
        }

        // -----------------------------------------------------------
        // Devices (menu, default device selection, packet trace)
        // -----------------------------------------------------------

        private void DevicesSearch_Click(object? sender, RoutedEventArgs e) => _ = _devicesPanel.SearchAsync();
        private void DevicesConnect_Click(object? sender, RoutedEventArgs e) => _ = _devicesPanel.ConnectSelectedAsync();
        private void DevicesDisconnect_Click(object? sender, RoutedEventArgs e) => _ = _devicesPanel.DisconnectSelectedAsync();
        private void DevicesSetDefault_Click(object? sender, RoutedEventArgs e) => _devicesPanel.SetSelectedAsDefault();
        private void DevicesClearDefault_Click(object? sender, RoutedEventArgs e) => _devicesPanel.ClearDefault();
        private void DevicesTrace_Click(object? sender, RoutedEventArgs e) => _devicesPanel.OpenTraceForSelected();

        private void DevicesLoopback_Click(object? sender, RoutedEventArgs e)
        {
            _devices.LoopbackEnabled = mnuLoopback.IsChecked;
            UpdateStatus(_devices.LoopbackEnabled ? "Simulated device 'loopback:echo' enabled." : "Simulated device removed.");
        }

        // Prevents the programmatic filling of the selection from setting the default device again.
        private bool _updatingDefaultUi;
        private const string NoDefaultText = "(none)";

        /// <summary>Fills the default device selection of the tool bar: "(none)", all devices found and - if it is not (any more) found - the chosen default device.</summary>
        private void RefreshDefaultDeviceCombo()
        {
            _updatingDefaultUi = true;
            try
            {
                string? current = _devices.Manager.DefaultIdentifier;
                var items = new List<string> { NoDefaultText };
                items.AddRange(_devices.Manager.GetSlots().Select(s => s.Identifier));
                if (current != null && !items.Contains(current)) items.Add(current);
                cmbDefaultDevice.ItemsSource = items;
                cmbDefaultDevice.SelectedItem = current ?? NoDefaultText;
            }
            finally { _updatingDefaultUi = false; }
        }

        private void cmbDefaultDevice_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_updatingDefaultUi || cmbDefaultDevice.SelectedItem is not string chosen) return;
            _devices.Manager.DefaultIdentifier = chosen == NoDefaultText ? null : chosen;
        }

        /// <summary>Opens the packet trace of the device `identifier` in a tab (one that is already open is only brought to the front).</summary>
        private void OpenTrace(string identifier)
        {
            var existing = _documents.FirstOrDefault(d => d.Trace is { } t && t.DeviceIdentifier == identifier);
            if (existing != null)
            {
                Activate(existing);
                return;
            }

            var doc = CreateDocument(DocumentKind.PacketLog, "", null, untitledName: $"Packets {identifier}");
            doc.Trace!.Attach(_devices.Manager, identifier);
            UpdateStatus($"Packet trace for {identifier} started.");
        }

        // -----------------------------------------------------------
        // Hotkeys
        // -----------------------------------------------------------

        /// <summary>Deliberately registered as a tunnelling handler with handledEventsToo (see the constructor): the keys must reach us wherever the focus is, also when the
        /// text editor or a menu has already handled them (F10 would otherwise only activate the menu bar on some systems).</summary>
        private void Window_KeyDown(object? sender, KeyEventArgs e)
        {
            bool ctrl = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
            bool shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
            bool alt = (e.KeyModifiers & KeyModifiers.Alt) != 0;

            switch (e.Key)
            {
                case Key.Left when alt && ActiveDocument is { Markdown.IsReadOnly: true } mdDoc:
                    NavigateHistory(mdDoc, -1); e.Handled = true; break;
                case Key.Right when alt && ActiveDocument is { Markdown.IsReadOnly: true } mdDoc:
                    NavigateHistory(mdDoc, +1); e.Handled = true; break;
                case Key.F5 when ctrl:
                    Restart_Click(this, e); e.Handled = true; break;
                case Key.F5 when shift:
                    Stop_Click(this, e); e.Handled = true; break;
                case Key.F5:
                    Run_Click(this, e); e.Handled = true; break;
                case Key.F10:
                    Step_Click(this, e); e.Handled = true; break;
                case Key.F11 when shift:
                    StepOut_Click(this, e); e.Handled = true; break;
                case Key.F11:
                    StepInto_Click(this, e); e.Handled = true; break;
                case Key.F8:
                    Continue_Click(this, e); e.Handled = true; break;
                case Key.F9:
                    ToggleBreakpoint_Click(this, e); e.Handled = true; break;
                case Key.N when ctrl && shift:
                    NewMarkdown_Click(this, e); e.Handled = true; break;
                case Key.N when ctrl:
                    New_Click(this, e); e.Handled = true; break;
                case Key.O when ctrl:
                    Open_Click(this, e); e.Handled = true; break;
                case Key.S when ctrl && shift:
                    SaveAll_Click(this, e); e.Handled = true; break;
                case Key.S when ctrl:
                    Save_Click(this, e); e.Handled = true; break;
                case Key.G when ctrl:
                    GoToLine_Click(this, e); e.Handled = true; break;
                case Key.F12 when ActiveScript != null && !shift && !ctrl:
                    GoToDefinition_Click(this, e); e.Handled = true; break;
                case Key.W when ctrl:
                    CloseDocument_Click(this, e); e.Handled = true; break;
                case Key.V when ctrl && shift && (ActiveDocument?.Markdown != null || ActiveDocument?.Design != null):
                    ToggleMarkdownPreview_Click(this, e); e.Handled = true; break;
            }
        }

        // Drag files from the file manager onto the window: each one opens in a tab of its own.
        private void Window_DragOver(object? sender, DragEventArgs e)
        {
            if (e.DataTransfer.Formats.Contains(DataFormat.File))
            {
                e.DragEffects = DragDropEffects.Copy;
                e.Handled = true;
            }
        }

        private void Window_Drop(object? sender, DragEventArgs e)
        {
            var files = e.DataTransfer.GetItems(DataFormat.File);
            if (files == null) return;
            foreach (var path in files.Select(f => f.TryGetFile()?.TryGetLocalPath()).Where(p => p != null && File.Exists(p)))
                OpenFile(path!);   // (a project or a solution file opens the workspace)
            e.Handled = true;
        }

        private void Exit_Click(object? sender, RoutedEventArgs e) => Close();
    }
}
