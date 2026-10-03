using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AvalonDock.Layout;
using AvalonDock.Layout.Serialization;
using fire.Compiler;
using fire.Compiler.Assembly;
using fire.Utilities;
using Microsoft.Win32;

namespace fire.Editor
{
    /// <summary>Das Editor-Hauptfenster: MEHRERE Dokumente in Tabs
    /// (AvalonDock-Dokumentbereich) - fire-Skripte (ScriptEditorControl) und
    /// Markdown-Dokumente (MarkdownEditorControl) nebeneinander, beliebig
    /// viele, jeweils in einem eigenen Tab.
    ///
    /// Alle Funktionen (Ausführen/Debuggen, Haltepunkte, Fehlerliste,
    /// Buildeinstellungen, Speichern, Statuszeile) wirken NUR auf das
    /// AKTIVE Dokument; ebenso gehört nur der Text des aktiven Skripts zur
    /// Quellen-Sammlung des Compilers - sollen mehrere Dateien zusammen
    /// übersetzt werden, bindet man sie per #include ein (relative Pfade
    /// beziehen sich dabei auf den Ordner der aktiven Datei).
    ///
    /// Dieses Fenster ist nur die ORCHESTRIERUNG (Datei-Menü, Ausführen,
    /// Hotkeys, Ausgabe-Fenster); die Controls (ScriptEditorControl,
    /// MarkdownEditorControl, DebuggerPanelControl) kennen weder einander
    /// noch DebugSession/Kompilieren.</summary>
    public partial class MainWindow : Window
    {
        private readonly DebugSession _session = new();
        private readonly EditorDeviceService _devices = new();
        private DebuggerPanels _debugger = null!;

        private AssemblyInfo _scriptAssemblyInfo;

        // Ausgabe-Warteschlange (siehe OnScriptOutput-Doku) - thread-sicher,
        // da JEDER Thread (Main oder ein Fire-Thread) gleichzeitig
        // hineinschreiben kann; _outputFlushTimer holt sie regelmäßig,
        // GEBÜNDELT auf dem UI-Thread ab, statt pro print() einzeln zu
        // aktualisieren.
        private readonly ConcurrentQueue<string> _pendingOutput = new();
        private readonly DispatcherTimer _outputFlushTimer;

        // Verhindert überlappende Schritt-Anfragen auf demselben Thread
        // (siehe DebugThreadContext.RequestStep-Doku: fire-and-forget, ein
        // zweiter Aufruf während der erste noch läuft könnte sonst dessen
        // Ergebnis überschreiben) - gesetzt beim Anfordern, zurückgesetzt
        // sobald ThreadPaused für den betroffenen Thread feuert.
        private bool _isBusy;

        // Andockbare Bereiche (AvalonDock), nach ContentId (siehe MainWindow.xaml). Nach dem Laden eines Layouts
        // ersetzt AvalonDock die Layout-Elemente durch neue - deshalb hier nie die XAML-Objekte selbst merken,
        // sondern nach jedem Laden neu einsammeln (siehe DeserializeLayout).
        private Dictionary<string, LayoutContent> _panels = new();

        // Das Layout aus dem XAML (Vorgabe) - für "Layout zurücksetzen" und als Rückfall, falls ein gespeichertes
        // Layout nicht geladen werden kann.
        private string _defaultLayout = "";
        private bool _layoutLoaded;

        private List<ErrorListItem> _errors = new();

        // -----------------------------------------------------------
        // Dokumente (Tabs)
        // -----------------------------------------------------------

        /// <summary>Ein geöffneter Tab. `Layout` wird nach jedem Laden eines
        /// Layouts ausgetauscht (siehe DeserializeLayout) - die View
        /// (Editor-Control) bleibt dieselbe.</summary>
        private enum DocumentKind
        {
            /// <summary>Ein fire-Skript.</summary>
            Script,
            /// <summary>Ein Markdown-Dokument.</summary>
            Markdown,
            /// <summary>Ein Paketprotokoll (live aufgezeichnet oder aus einer .fplog-Datei geladen).</summary>
            PacketLog,
        }

        private sealed class OpenDocument
        {
            public required string Id { get; init; }
            public required IDocumentView View { get; init; }
            public required DocumentKind Kind { get; init; }
            public required int Number { get; init; }

            /// <summary>Name, solange es keine Datei gibt (statt "Unbenannt N"), z.B. "Pakete serial:COM3".</summary>
            public string? UntitledName { get; init; }
            public LayoutDocument Layout { get; set; } = null!;

            public bool IsMarkdown => Kind == DocumentKind.Markdown;
            public ScriptEditorControl? Script => View as ScriptEditorControl;
            public MarkdownEditorControl? Markdown => View as MarkdownEditorControl;
            public PacketTraceControl? Trace => View as PacketTraceControl;

            /// <summary>Dateiname bzw. "Unbenannt N" (Markdown: mit .md).</summary>
            public string DisplayName => View.FilePath != null
                ? Path.GetFileName(View.FilePath)
                : UntitledName ?? (IsMarkdown ? $"Unbenannt {Number}.md" : $"Unbenannt {Number}");
        }

        private readonly List<OpenDocument> _documents = new();
        private int _documentCounter;

        // Das zuletzt aktive Dokument - die Quelle für "das aktuelle Dokument" (siehe ActiveDocument).
        private OpenDocument? _active;

        // Das Skript, das gerade kompiliert/im Debugger angehalten ist (bleibt es, auch wenn man den Tab wechselt).
        private OpenDocument? _debugDocument;

        private OpenDocument? ActiveDocument
        {
            get
            {
                if (_active != null && _documents.Contains(_active)) return _active;
                return _documents.FirstOrDefault(d => d.Layout.IsSelected) ?? _documents.FirstOrDefault();
            }
        }

        /// <summary>Das aktive Dokument, falls es ein fire-Skript ist.</summary>
        private ScriptEditorControl? ActiveScript => ActiveDocument?.Script;

        private static DocumentKind KindOfPath(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".md" or ".markdown" or ".mdown") return DocumentKind.Markdown;
            if (ext == fire.Device.Manager.DeviceManager.PacketLog.FileExtension) return DocumentKind.PacketLog;
            return DocumentKind.Script;
        }

        public MainWindow()
        {
            InitializeComponent();

            // Die Geräte-Übersicht startet eingeklappt (am rechten Rand ausgeblendet) - vor dem Sichern des Standard-Layouts.
            if (DockManager.Layout.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(a => a.ContentId == "devices") is { } devicesPane)
                devicesPane.ToggleAutoHide();

            CollectPanels();
            _defaultLayout = SerializeLayout();
            Loaded += (_, _) => LoadLayout();

            // Bewusst ein explizit registrierter Handler (siehe unten,
            // Window_PreviewKeyDown) statt eines OnPreviewKeyDown-Overrides -
            // UND mit handledEventsToo:true, damit er auch dann noch feuert,
            // wenn irgendein Kind-Element (oder eine spätere WPF-interne
            // Verarbeitung, siehe Window_PreviewKeyDown-Doku zu F10) das
            // Ereignis bereits als Handled markiert hat. Ein einfacher
            // Override/normal registrierter Handler hätte das NICHT
            // garantiert.
            AddHandler(PreviewKeyDownEvent, new KeyEventHandler(Window_PreviewKeyDown), handledEventsToo: true);

            _debugger = new DebuggerPanels(DebugThreadsPanel, DebugScopePanel, DebugStackPanel);
            _debugger.AttachSession(_session);
            _debugger.ThreadSelected += OnThreadSelected;

            // Der geteilte DeviceManager des Editors: alle Skripte benutzen ihn gemeinsam (siehe EditorDeviceService).
            _session.DeviceManager = _devices.Manager;
            DevicesPanel.Attach(_devices);
            DevicesPanel.StatusMessage += UpdateStatus;
            DevicesPanel.OpenTraceRequested += OpenTrace;
            _devices.Manager.DevicesChanged += () => Dispatcher.BeginInvoke(new Action(RefreshDefaultDeviceCombo));
            _devices.Manager.DefaultChanged += () => Dispatcher.BeginInvoke(new Action(RefreshDefaultDeviceCombo));
            mnuLoopback.IsChecked = _devices.LoopbackEnabled;
            RefreshDefaultDeviceCombo();

            _session.OutputWritten += OnScriptOutput;
            // WICHTIG: InvokeAsync (nicht-blockierend), NICHT Invoke -
            // dieser Handler kann vom EIGENEN Hintergrund-Thread eines
            // gerade entstehenden/pausierenden Fire-Threads aus feuern,
            // während der UI-Thread SELBST synchron in einem "Bis Ende
            // durchlaufen" des Main-Threads steckt (z.B. RunToCompletion),
            // das seinerseits auf eine Nachricht von GENAU DIESEM
            // Fire-Thread wartet (etwa über `process`) - ein blockierendes
            // Dispatcher.Invoke hier würde in diesem Fall zu einem echten
            // Deadlock führen (Fire-Thread wartet auf den UI-Thread, der
            // UI-Thread wartet transitiv auf den Fire-Thread).
            _session.ThreadAdded += ctx => Dispatcher.InvokeAsync(() => _debugger.Refresh(BreakpointDescriptions()));
            _session.ThreadPaused += ctx => Dispatcher.InvokeAsync(() =>
            {
                // Nur wenn der GERADE ANGEZEIGTE (aktive) Thread betroffen
                // ist, muss die Detailanzeige (Hervorhebung/Scope/Stack)
                // neu aufgebaut werden - ein anderer, automatisch
                // weiterlaufender Fire-Thread, der gerade z.B. einen
                // Haltepunkt erreicht, aktualisiert erstmal nur seinen
                // eigenen Eintrag in der Threads-Liste.
                _debugger.Refresh(BreakpointDescriptions());
                if (ReferenceEquals(ctx, _session.ActiveThread))
                {
                    _isBusy = false;
                    AfterStep(!ctx.IsFinished);
                }
            });

            // Läuft DURCHGEHEND - Ausgabe kann jederzeit, auch mitten in
            // einem langen Lauf, anfallen und soll zeitnah sichtbar werden,
            // nur eben gebündelt statt einzeln (siehe OnScriptOutput-Doku).
            _outputFlushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            _outputFlushTimer.Tick += (_, _) => FlushPendingOutput();
            _outputFlushTimer.Start();

            UpdateExecutionModeSelection(_session);

            _scriptAssemblyInfo = new AssemblyInfo();

            // Nach dem Laden des Layouts (das registriert sich oben zuerst): per Kommandozeile übergebene
            // Dateien öffnen, sonst ein leeres Willkommens-Skript.
            Loaded += (_, _) =>
            {
                foreach (var arg in Environment.GetCommandLineArgs().Skip(1))
                    if (File.Exists(arg)) OpenFile(arg);
                _ = _devices.RefreshAsync(fastScan: true); // Geräte auflisten (schnell); die Verfügbarkeitsprüfung macht "Suchen"
                if (_documents.Count == 0)
                    NewScript("// Willkommen im fire-Editor\nprint(\"Hallo, Welt!\")\n");
            };

            UpdateStatus("Bereit.");
        }

        private void UpdateExecutionModeSelection(DebugSession session)
        {
            mnuRunDebug.IsChecked = session.ExecutionMode == Runtime.VmExecutionMode.Debug;
            mnuRunRelease.IsChecked = session.ExecutionMode == Runtime.VmExecutionMode.Release;
            mnuRunPerformance.IsChecked = session.ExecutionMode == Runtime.VmExecutionMode.Performance;

            _updatingModeUi = true;
            cmbMode.SelectedIndex = session.ExecutionMode switch
            {
                Runtime.VmExecutionMode.Debug => 0,
                Runtime.VmExecutionMode.Release => 1,
                _ => 2,
            };
            _updatingModeUi = false;
        }

        // Verhindert, dass das programmatische Setzen der Auswahl (UpdateExecutionModeSelection) den Modus erneut setzt.
        private bool _updatingModeUi;

        private void cmbMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingModeUi || cmbMode.SelectedItem is not ComboBoxItem { Tag: string tag }) return;

            _session.ExecutionMode = Enum.Parse<Runtime.VmExecutionMode>(tag);
            UpdateExecutionModeSelection(_session);
        }

        /// <summary>Die Haltepunkte des Skripts `doc` sind bloße Zeilennummern, hier
        /// mit dem aktuell gültigen Quell-Index verknüpft (siehe
        /// DebugSession.FirstUserSourceIndex), bevor sie an die Datei-bewusste
        /// DebugSession-API gehen. Vor dem ersten erfolgreichen Compile() ist
        /// FirstUserSourceIndex noch 0 - dann wird 1 angenommen (Prelude liegt
        /// immer bei 0, der erste eigene Quelltext normalerweise bei 1). Es gibt
        /// immer nur EIN Skript als Quelle, daher reicht dieser eine Index.</summary>
        private HashSet<(int SourceIndex, int Line)> BreakpointLocations(OpenDocument? doc) =>
            (doc?.Script?.Breakpoints ?? (IReadOnlySet<int>)new HashSet<int>())
                .Select(l => (_session.FirstUserSourceIndex == 0 ? 1 : _session.FirstUserSourceIndex, l))
                .ToHashSet();

        /// <summary>Für DebuggerPanelControl.Refresh - die (numerisch sortierten) Haltepunkt-Zeilen des
        /// Skripts im Debugger, sonst des aktiven Skripts.</summary>
        private List<string> BreakpointDescriptions() =>
            (_debugDocument ?? ActiveDocument)?.Script?.Breakpoints.OrderBy(l => l).Select(l => l.ToString()).ToList()
            ?? new List<string>();

        private void UpdateErrorPanel()
        {
            var doc = ActiveDocument;
            var diagnostics = doc?.Script?.Diagnostics ?? (IReadOnlyList<Diagnostic>)Array.Empty<Diagnostic>();
            string file = doc?.DisplayName ?? "";
            var items = diagnostics.Select(d => new ErrorListItem("Fehler", d.Message, file, d.Line)).ToList();

            Dispatcher.Invoke(() =>
            {
                _errors = items;
                ApplyErrorFilter();
            });
        }

        /// <summary>Zeigt die Fehlerliste gemäß dem Filterknopf (Fehler ein/aus) und hält eine vom Nutzer gewählte
        /// Sortierung über die ständigen Neuberechnungen hinweg.</summary>
        private void ApplyErrorFilter()
        {
            int count = _errors.Count;
            ErrorCountText.Text = count == 1 ? "1 Fehler" : $"{count} Fehler";
            if (_panels.TryGetValue("errors", out var panel))
                panel.Title = count == 0 ? "Fehlerliste" : $"Fehlerliste ({count})";

            var sorts = ErrorGrid.Columns
                .Where(c => c.SortDirection != null)
                .Select(c => (Path: c.SortMemberPath, Direction: c.SortDirection!.Value))
                .ToList();

            var shown = ErrorFilterButton.IsChecked == true ? _errors : new List<ErrorListItem>();
            ErrorGrid.ItemsSource = shown;

            var view = CollectionViewSource.GetDefaultView(shown);
            view.SortDescriptions.Clear();
            foreach (var (path, direction) in sorts)
                if (!string.IsNullOrEmpty(path)) view.SortDescriptions.Add(new SortDescription(path, direction));
        }

        private void ErrorFilter_Click(object sender, RoutedEventArgs e) => ApplyErrorFilter();

        private void ErrorGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Nur ein Doppelklick auf eine ZEILE springt (nicht einer auf die Spaltenüberschrift).
            var element = e.OriginalSource as DependencyObject;
            while (element != null && element is not DataGridRow)
                element = element is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(element)
                    : LogicalTreeHelper.GetParent(element);
            if (element == null || ErrorGrid.SelectedItem is not ErrorListItem item) return;

            if (ActiveScript is not { } script) return;
            script.ScrollToLine(item.Line);
            script.SetCaretByLineColumn(Math.Max(0, item.Line - 1), 0);
            script.Focus();
        }

        /// <summary>Die kleine Symbolleiste der Fehlerliste braucht den "Überlauf"-Pfeil nicht.</summary>
        private void ToolBar_HideOverflow(object sender, RoutedEventArgs e)
        {
            if (sender is not ToolBar toolBar) return;
            if (toolBar.Template.FindName("OverflowGrid", toolBar) is FrameworkElement overflow)
                overflow.Visibility = Visibility.Collapsed;
            if (toolBar.Template.FindName("MainPanelBorder", toolBar) is FrameworkElement border)
                border.Margin = new Thickness(0);
        }

        // -----------------------------------------------------------
        // Andockbare Bereiche (AvalonDock): Ansicht-Menü und gespeichertes Layout
        // -----------------------------------------------------------

        private static string LayoutFilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "fire", "editor-layout.xml");

        /// <summary>Sammelt die Layout-Elemente des aktuellen Layouts nach ContentId (nur sichtbare; ausgeblendete
        /// kennt AvalonDock hier nicht - nach dem Laden eines Layouts liefert die Callback-Variante in
        /// <see cref="DeserializeLayout"/> alle).</summary>
        private void CollectPanels()
        {
            _panels = DockManager.Layout.Descendents().OfType<LayoutContent>()
                .Where(c => !string.IsNullOrEmpty(c.ContentId))
                .ToDictionary(c => c.ContentId!);
        }

        private string SerializeLayout()
        {
            var writer = new StringWriter();
            new XmlLayoutSerializer(DockManager).Serialize(writer);
            return writer.ToString();
        }

        /// <summary>Die Inhalte der Bereiche, nach ContentId - im gespeicherten Layout steht nur die ContentId, nicht der
        /// Inhalt.</summary>
        private Dictionary<string, object> PanelContents() => new()
        {
            ["output"] = OutputBox,
            ["errors"] = ErrorPanelContent,
            ["threads"] = DebugThreadsPanel,
            ["scope"] = DebugScopePanel,
            ["stack"] = DebugStackPanel,
            ["devices"] = DevicesPanel,
        };

        private void DeserializeLayout(TextReader reader)
        {
            var contents = PanelContents();
            var panels = new Dictionary<string, LayoutContent>();
            var documentsById = _documents.ToDictionary(d => "doc:" + d.Id);
            var restored = new HashSet<OpenDocument>();

            // Die Inhalte hängen noch an den bisherigen Layout-Elementen; ein Element kann nur einen Besitzer haben.
            foreach (var panel in _panels.Values) panel.Content = null;
            foreach (var doc in _documents) doc.Layout.Content = null;

            var serializer = new XmlLayoutSerializer(DockManager);
            serializer.LayoutSerializationCallback += (_, args) =>
            {
                string? id = args.Model.ContentId;
                if (id != null && contents.TryGetValue(id, out var content))
                {
                    args.Content = content;
                    panels[id] = args.Model;
                }
                else if (id != null && documentsById.TryGetValue(id, out var doc) && args.Model is LayoutDocument layoutDocument)
                {
                    // Ein offener Tab behält seinen Platz im Layout.
                    args.Content = doc.View;
                    AttachLayout(doc, layoutDocument);
                    restored.Add(doc);
                }
                else
                {
                    args.Cancel = true; // ein Bereich/Tab, den es nicht mehr gibt (z.B. aus einer früheren Sitzung)
                }
            };
            serializer.Deserialize(reader);
            _panels = panels;

            // Tabs, die das Layout nicht kennt (z.B. beim Zurücksetzen auf das Standard-Layout), kommen in den Dokumentbereich.
            foreach (var doc in _documents.Where(d => !restored.Contains(d)).ToList())
            {
                var layoutDocument = new LayoutDocument { ContentId = "doc:" + doc.Id, Content = doc.View };
                AttachLayout(doc, layoutDocument);
                GetDocumentPane().Children.Add(layoutDocument);
            }
            foreach (var doc in _documents) UpdateTitle(doc);
            if (ActiveDocument is { } active) { active.Layout.IsSelected = true; }
            RefreshActiveUi();
        }

        /// <summary>Der Bereich, in dem die Dokument-Tabs liegen - legt ihn an, falls ein (altes/beschädigtes) Layout keinen hat.</summary>
        private LayoutDocumentPane GetDocumentPane()
        {
            var pane = DockManager.Layout.Descendents().OfType<LayoutDocumentPane>().FirstOrDefault();
            if (pane != null) return pane;
            pane = new LayoutDocumentPane();
            DockManager.Layout.RootPanel.Children.Insert(0, pane);
            return pane;
        }

        private void LoadLayout()
        {
            if (_layoutLoaded) return;
            _layoutLoaded = true;
            if (!File.Exists(LayoutFilePath)) return;

            try
            {
                using var reader = new StreamReader(LayoutFilePath);
                DeserializeLayout(reader);
                // Ein Layout aus einer älteren Version (ohne die neuen Bereiche) oder ohne Dokumentbereich ist unbrauchbar: Standard.
                if (!DockManager.Layout.Descendents().OfType<LayoutDocumentPane>().Any() || !PanelContents().Keys.All(_panels.ContainsKey))
                    RestoreDefaultLayout();
            }
            catch (Exception ex)
            {
                // Ein beschädigtes oder zu altes Layout darf den Editor nicht unbenutzbar machen.
                System.Diagnostics.Debug.WriteLine(ex);
                RestoreDefaultLayout();
            }
        }

        private void SaveLayout()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LayoutFilePath)!);
                new XmlLayoutSerializer(DockManager).Serialize(LayoutFilePath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex); // nicht speicherbar (z.B. schreibgeschütztes Profil): kein Grund, das Schließen zu stören
            }
        }

        private void RestoreDefaultLayout()
        {
            try
            {
                DeserializeLayout(new StringReader(_defaultLayout));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
                UpdateStatus("Das Layout konnte nicht zurückgesetzt werden.");
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);
            if (e.Cancel) return;

            // Ungespeicherte Tabs: jeweils nachfragen; "Abbrechen" hält das Schließen des Fensters an.
            foreach (var doc in _documents.ToList())
            {
                if (!ConfirmClose(doc)) { e.Cancel = true; return; }
            }
            SaveLayout();
            _devices.Shutdown(); // trennt die Geräte; nur der Besitzer darf den geteilten Manager abbauen
        }

        private void ResetLayout_Click(object sender, RoutedEventArgs e)
        {
            RestoreDefaultLayout();
            try { File.Delete(LayoutFilePath); } catch (IOException) { }
        }

        private void ViewMenu_SubmenuOpened(object sender, RoutedEventArgs e)
        {
            foreach (var item in mnuView.Items.OfType<MenuItem>())
                if (item.Tag is string id && _panels.TryGetValue(id, out var panel))
                    item.IsChecked = panel is not LayoutAnchorable anchorable || anchorable.IsVisible;
        }

        private void ViewPanel_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: string id } || !_panels.TryGetValue(id, out var panel)) return;
            if (panel is not LayoutAnchorable anchorable) return;

            if (anchorable.IsAutoHidden)
            {
                // Eingeklappt (am Rand ausgeblendet): das Menü holt den Bereich heraus und dockt ihn an.
                anchorable.ToggleAutoHide();
                anchorable.IsActive = true;
            }
            else if (anchorable.IsVisible)
            {
                anchorable.Hide();
            }
            else
            {
                anchorable.Show();
                anchorable.IsActive = true;
            }
        }

        private void OnThreadSelected(DebugThreadContext chosen)
        {
            // Ein evtl. noch ausstehender Schritt des BISHERIGEN aktiven
            // Threads würde _isBusy sonst für immer gesetzt lassen (sein
            // ThreadPaused-Ereignis feuert später mit einem ctx, der nicht
            // mehr der aktive ist) - beim Wechsel des Threads deshalb immer
            // zurücksetzen, damit die Schritt-Knöpfe nicht dauerhaft
            // gesperrt bleiben.
            _isBusy = false;
            ShowDebugLine(chosen.IsFinished ? null : chosen.Vm.CurrentLine);
            _debugger.Refresh(BreakpointDescriptions());
            UpdateStatus(chosen.IsFinished
                ? $"{chosen.Name}: beendet."
                : $"{chosen.Name}: angehalten in Zeile {chosen.Vm.CurrentLine}.");
        }

        private void UpdateStatus(string text) => StatusText.Text = text;

        // -----------------------------------------------------------
        // Ausführen / Debuggen
        // -----------------------------------------------------------

        private void OnScriptOutput(string text)
        {
            // Bewusst NUR einreihen, KEINE UI-Interaktion hier - diese
            // Methode kann sehr oft und sehr schnell hintereinander aus
            // einem Fire-Thread heraus feuern (z.B. in einer Schleife mit
            // hunderten print()-Aufrufen). _outputFlushTimer (siehe
            // Konstruktor) holt die ganze Warteschlange stattdessen
            // gebündelt, in festen Abständen, in EINEM AppendText-Aufruf ab.
            _pendingOutput.Enqueue(text);
        }

        private void FlushPendingOutput()
        {
            if (_pendingOutput.IsEmpty) return;

            var batch = new System.Text.StringBuilder();
            while (_pendingOutput.TryDequeue(out var line))
                batch.Append(line).Append(Environment.NewLine);

            OutputBox.AppendText(batch.ToString());
            OutputBox.ScrollToEnd();
        }

        /// <summary>Zeigt die angehaltene Zeile im Tab des Skripts, das gerade im Debugger ist (und holt den Tab nach vorn);
        /// null = Hervorhebung entfernen.</summary>
        private void ShowDebugLine(int? line)
        {
            var script = _debugDocument?.Script;
            if (script == null) return;
            script.HighlightedLine = line;
            if (line != null)
            {
                _debugDocument!.Layout.IsSelected = true;
                script.ScrollToLine(line.Value);
            }
        }

        private void Run_Click(object sender, RoutedEventArgs e) => CompileAndPrepare(null);

        private void CompileAndPrepare(string? filename)
        {
            // Kompiliert wird NUR das aktive Dokument (mehrere Dateien: per #include einbinden).
            var doc = ActiveDocument;
            if (doc?.Script is not { } script)
            {
                UpdateStatus(doc == null ? "Kein Dokument geöffnet." : "Das aktive Dokument ist kein Skript - zum Ausführen einen Skript-Tab wählen.");
                return;
            }

            OutputBox.Clear();
            while (_pendingOutput.TryDequeue(out _)) { } // Reste eines evtl. noch nicht abgeflossenen vorigen Laufs verwerfen
            ShowDebugLine(null);
            _debugDocument = doc;
            _isBusy = false;
            string source = script.GetText();
            _session.UpdateBreakpoints(BreakpointLocations(doc));

            if (!_session.Compile(new[] { source }, filename, script.BaseDirectory))
            {
                UpdateStatus($"Kompilierfehler: {_session.CompileError}");
                MessageBox.Show(_session.CompileError, "Kompilierfehler",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            UpdateStatus("Kompiliert - bereit für Einzelschritt/Weiter/Bis Ende.");
            _debugger.Refresh(BreakpointDescriptions());
        }

        private void Step_Click(object sender, RoutedEventArgs e)
        {
            if (_session.Vm == null) CompileAndPrepare(null);
            if (_session.Vm == null || _isBusy) return;
            BeginStep();
            _session.StepLine();
        }

        private void StepInto_Click(object sender, RoutedEventArgs e)
        {
            if (_session.Vm == null) CompileAndPrepare(null);
            if (_session.Vm == null || _isBusy) return;
            BeginStep();
            _session.StepInto();
        }

        private void StepOut_Click(object sender, RoutedEventArgs e)
        {
            if (_session.Vm == null || _isBusy) return; // "Funktion verlassen" ohne laufende Funktion ergibt keinen Sinn
            BeginStep();
            _session.StepOut();
        }

        private void Continue_Click(object sender, RoutedEventArgs e)
        {
            if (_session.Vm == null) CompileAndPrepare(null);
            if (_session.Vm == null || _isBusy) return;
            BeginStep();
            _session.Continue(BreakpointLocations(_debugDocument));
        }

        private void RunToEnd_Click(object sender, RoutedEventArgs e)
        {
            if (_session.Vm == null) CompileAndPrepare(null);
            if (_session.Vm == null || _isBusy) return;
            BeginStep();
            _session.RunToCompletion();
        }

        /// <summary>Alle Schritt-Anfragen sind FIRE-AND-FORGET (siehe
        /// DebugThreadContext - jede VM läuft auf ihrem EIGENEN Hintergrund-
        /// Thread, damit die UI während der Ausführung reaktionsfähig
        /// bleibt) - hier nur die sofortige Rückmeldung "es läuft", die
        /// eigentliche Aktualisierung kommt asynchron über das ThreadPaused-
        /// Event (siehe Konstruktor).</summary>
        private void BeginStep()
        {
            _isBusy = true;
            UpdateStatus("Läuft...");
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            _session.Reset();
            _isBusy = false;
            ShowDebugLine(null);
            _debugger.Refresh(BreakpointDescriptions());
            UpdateStatus("Gestoppt.");
        }

        private void AfterStep(bool more)
        {
            FlushPendingOutput(); // sofort sichtbar, nicht erst beim nächsten Timer-Tick
            if (!more)
            {
                ShowDebugLine(null);
                UpdateStatus(_session.RuntimeError != null
                    ? $"Laufzeitfehler: {_session.RuntimeError}"
                    : "Programm beendet.");
            }
            else
            {
                int line = _session.Vm!.CurrentLine;
                ShowDebugLine(line);
                UpdateStatus($"Angehalten in Zeile {line}.");
            }
            _debugger.Refresh(BreakpointDescriptions());
        }

        private void ToggleBreakpoint_Click(object sender, RoutedEventArgs e)
        {
            if (ActiveScript is { } script) script.ToggleBreakpointAtCaret();
            else UpdateStatus("Haltepunkte gibt es nur in Skript-Tabs.");
        }

        // -----------------------------------------------------------
        // Datei-Menü
        // -----------------------------------------------------------

        private const string ScriptFilter = "fire-Dateien (*.script;*.fi;*.fic)|*.script;*.fi;*.fic";
        private const string MarkdownFilter = "Markdown (*.md;*.markdown)|*.md;*.markdown";
        private const string PacketLogFilter = "Paketprotokolle (*.fplog)|*.fplog";

        private static string SafeFileName(string name) =>
            string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '-' : c));

        private void New_Click(object sender, RoutedEventArgs e) => NewScript("");

        private void NewMarkdown_Click(object sender, RoutedEventArgs e) => NewMarkdown("");

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Multiselect = true,
                Filter = "Alle Dokumente|*.script;*.fi;*.fic;*.md;*.markdown;*.fplog|" + ScriptFilter + "|" + MarkdownFilter + "|" + PacketLogFilter + "|Alle Dateien (*.*)|*.*",
            };
            if (dlg.ShowDialog() != true) return;
            foreach (var file in dlg.FileNames) OpenFile(file);
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (ActiveDocument is { } doc) Save(doc);
        }

        private void SaveAs_Click(object sender, RoutedEventArgs e)
        {
            if (ActiveDocument is { } doc) SaveAs(doc);
        }

        private void SaveAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var doc in _documents.Where(d => d.View.IsModified).ToList())
                if (!Save(doc)) return;
        }

        private void CloseDocument_Click(object sender, RoutedEventArgs e) => ActiveDocument?.Layout.Close();

        // -----------------------------------------------------------
        // Bearbeiten-Menü (wirkt auf das aktive Dokument)
        // -----------------------------------------------------------

        private void EditMenu_SubmenuOpened(object sender, RoutedEventArgs e)
        {
            var view = ActiveDocument?.View;
            mnuUndo.IsEnabled = view?.CanUndo == true;
            mnuRedo.IsEnabled = view?.CanRedo == true;
            mnuCut.IsEnabled = mnuCopy.IsEnabled = mnuDelete.IsEnabled = view?.HasSelection == true;
            mnuPaste.IsEnabled = view != null && EditorCommands.ClipboardHasText();
            mnuGoToDefinition.IsEnabled = ActiveScript?.CanGoToDefinition() == true;
            mnuToggleComment.IsEnabled = ActiveScript != null;
        }

        private void Undo_Click(object sender, RoutedEventArgs e) => ActiveDocument?.View.Undo();
        private void Redo_Click(object sender, RoutedEventArgs e) => ActiveDocument?.View.Redo();
        private void Cut_Click(object sender, RoutedEventArgs e) => ActiveDocument?.View.Cut();
        private void Copy_Click(object sender, RoutedEventArgs e) => ActiveDocument?.View.Copy();
        private void Paste_Click(object sender, RoutedEventArgs e) => ActiveDocument?.View.Paste();
        private void Delete_Click(object sender, RoutedEventArgs e) => ActiveDocument?.View.Delete();
        private void SelectAll_Click(object sender, RoutedEventArgs e) => ActiveDocument?.View.SelectAll();
        private void Find_Click(object sender, RoutedEventArgs e) => ActiveDocument?.View.Find();
        private void FindNext_Click(object sender, RoutedEventArgs e) => ActiveDocument?.View.FindNext();
        private void FindPrevious_Click(object sender, RoutedEventArgs e) => ActiveDocument?.View.FindPrevious();

        private void GoToDefinition_Click(object sender, RoutedEventArgs e)
        {
            if (ActiveScript is { } script) script.GoToDefinition();
            else UpdateStatus("Zu Definition springen gibt es nur in Skript-Tabs.");
        }

        private void ToggleComment_Click(object sender, RoutedEventArgs e) => ActiveScript?.ToggleComment();

        private void GoToLine_Click(object sender, RoutedEventArgs e)
        {
            if (ActiveDocument is not { } doc) return;
            if (PromptLine(doc.View.GetCaretLine(), doc.View.LineCount) is { } line) doc.View.GoToLine(line);
        }

        /// <summary>Kleiner Eingabedialog "Gehe zu Zeile" (null = abgebrochen/ungültig).</summary>
        private int? PromptLine(int current, int max)
        {
            var box = new TextBox { Text = current.ToString(), MinWidth = 220, Margin = new Thickness(0, 4, 0, 10) };
            var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 70, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = "Abbrechen", IsCancel = true, MinWidth = 70 };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(new TextBlock { Text = $"Zeilennummer (1 - {max}):" });
            panel.Children.Add(box);
            panel.Children.Add(buttons);

            var dialog = new Window
            {
                Title = "Gehe zu Zeile",
                Content = panel,
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ShowInTaskbar = false,
            };
            int? result = null;
            ok.Click += (_, _) =>
            {
                if (int.TryParse(box.Text.Trim(), out int n)) { result = n; dialog.DialogResult = true; }
                else box.SelectAll();
            };
            dialog.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
            dialog.ShowDialog();
            return result;
        }

        private void ToggleMarkdownPreview_Click(object sender, RoutedEventArgs e) => ActiveDocument?.Markdown?.TogglePreview();

        // -----------------------------------------------------------
        // Geräte (Menü, Standardgerät-Auswahl, Paketverfolgung)
        // -----------------------------------------------------------

        private void DevicesSearch_Click(object sender, RoutedEventArgs e) => _ = DevicesPanel.SearchAsync();
        private void DevicesConnect_Click(object sender, RoutedEventArgs e) => _ = DevicesPanel.ConnectSelectedAsync();
        private void DevicesDisconnect_Click(object sender, RoutedEventArgs e) => _ = DevicesPanel.DisconnectSelectedAsync();
        private void DevicesSetDefault_Click(object sender, RoutedEventArgs e) => DevicesPanel.SetSelectedAsDefault();
        private void DevicesClearDefault_Click(object sender, RoutedEventArgs e) => DevicesPanel.ClearDefault();
        private void DevicesTrace_Click(object sender, RoutedEventArgs e) => DevicesPanel.OpenTraceForSelected();

        private void DevicesLoopback_Click(object sender, RoutedEventArgs e)
        {
            _devices.LoopbackEnabled = mnuLoopback.IsChecked;
            UpdateStatus(_devices.LoopbackEnabled ? "Simuliertes Gerät 'loopback:echo' aktiv." : "Simuliertes Gerät entfernt.");
        }

        // Verhindert, dass das programmatische Füllen der Auswahl das Standardgerät erneut setzt.
        private bool _updatingDefaultUi;
        private const string NoDefaultText = "(keins)";

        /// <summary>Füllt die Standardgerät-Auswahl der Symbolleiste: "(keins)", alle gefundenen Geräte und - falls es nicht
        /// (mehr) gefunden wird - das gewählte Standardgerät.</summary>
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

        private void cmbDefaultDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingDefaultUi || cmbDefaultDevice.SelectedItem is not string chosen) return;
            _devices.Manager.DefaultIdentifier = chosen == NoDefaultText ? null : chosen;
        }

        /// <summary>Öffnet die Paketverfolgung des Geräts `identifier` in einem Tab (eine bereits offene wird nur nach vorn geholt).</summary>
        private void OpenTrace(string identifier)
        {
            var existing = _documents.FirstOrDefault(d => d.Trace is { } t && t.DeviceIdentifier == identifier);
            if (existing != null)
            {
                Activate(existing);
                return;
            }

            var doc = CreateDocument(DocumentKind.PacketLog, "", null, untitledName: $"Pakete {identifier}");
            doc.Trace!.Attach(_devices.Manager, identifier);
            UpdateStatus($"Paketverfolgung für {identifier} gestartet.");
        }

        // -----------------------------------------------------------
        // Dokumente anlegen/öffnen/speichern/schließen
        // -----------------------------------------------------------

        private OpenDocument NewScript(string text) => CreateDocument(DocumentKind.Script, text, null);

        private OpenDocument NewMarkdown(string text) => CreateDocument(DocumentKind.Markdown, text, null);

        /// <summary>Öffnet eine Datei in einem neuen Tab (Skript oder Markdown nach Endung) - ist sie schon offen, wird
        /// nur dorthin gewechselt.</summary>
        private OpenDocument? OpenFile(string path)
        {
            string full = Path.GetFullPath(path);
            var existing = _documents.FirstOrDefault(d => d.View.FilePath != null &&
                string.Equals(Path.GetFullPath(d.View.FilePath), full, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                Activate(existing);
                return existing;
            }

            string text;
            try { text = File.ReadAllText(full); }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Öffnen fehlgeschlagen", MessageBoxButton.OK, MessageBoxImage.Error);
                return null;
            }

            // Ein noch unberührtes, leeres "Unbenannt"-Dokument (z.B. das Willkommens-Skript) wird dabei ersetzt.
            var pristine = _documents.Count == 1 && _documents[0].Kind != DocumentKind.PacketLog
                && _documents[0].View.FilePath == null && !_documents[0].View.IsModified
                ? _documents[0] : null;

            var doc = CreateDocument(KindOfPath(full), text, full);
            if (pristine != null) pristine.Layout.Close();
            UpdateStatus($"Geöffnet: {full}");
            return doc;
        }

        private OpenDocument CreateDocument(DocumentKind kind, string text, string? path, string? untitledName = null)
        {
            IDocumentView view = kind switch
            {
                DocumentKind.Markdown => new MarkdownEditorControl(),
                DocumentKind.PacketLog => new PacketTraceControl(),
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
                if (ReferenceEquals(ActiveDocument, doc)) CaretText.Text = $"Zeile {line}";
            };

            if (doc.Markdown is { } md)
                md.OpenFileRequested += p => OpenFile(p);

            if (doc.Script is { } script)
            {
                // Strg+Klick auf ein #include bzw. ein Symbol aus einer eingebundenen Datei: Datei in einem Tab öffnen.
                script.OpenFileRequested += (path, line) =>
                {
                    if (OpenFile(path) is { } target)
                        // Erst nach dem ersten Layout des (evtl. neuen) Tabs, sonst kann der Editor noch nicht scrollen.
                        Dispatcher.BeginInvoke(new Action(() => target.View.GoToLine(line)), DispatcherPriority.Loaded);
                };
                script.DiagnosticsChanged += () =>
                {
                    if (ReferenceEquals(ActiveDocument, doc)) UpdateErrorPanel();
                };
                script.BreakpointsChanged += () =>
                {
                    if (ReferenceEquals(doc, _debugDocument)) _session.UpdateBreakpoints(BreakpointLocations(doc));
                    _debugger.Refresh(BreakpointDescriptions());
                };
            }

            var layout = new LayoutDocument { ContentId = "doc:" + doc.Id, Content = view };
            AttachLayout(doc, layout);
            _documents.Add(doc);
            GetDocumentPane().Children.Add(layout);
            UpdateTitle(doc);
            Activate(doc);
            return doc;
        }

        /// <summary>Verbindet ein (neues) AvalonDock-Dokument mit dem Tab-Modell und hängt die Ereignisse ein.</summary>
        private void AttachLayout(OpenDocument doc, LayoutDocument layout)
        {
            doc.Layout = layout;
            layout.Closing += (_, e) =>
            {
                if (ReferenceEquals(doc.Layout, layout) && !ConfirmClose(doc)) e.Cancel = true;
            };
            layout.Closed += (_, _) =>
            {
                if (ReferenceEquals(doc.Layout, layout)) OnDocumentClosed(doc);
            };
            layout.IsActiveChanged += (_, _) =>
            {
                if (layout.IsActive && ReferenceEquals(doc.Layout, layout) && _documents.Contains(doc) && !ReferenceEquals(_active, doc))
                {
                    _active = doc;
                    RefreshActiveUi();
                }
            };
        }

        private void Activate(OpenDocument doc)
        {
            doc.Layout.IsSelected = true;
            doc.Layout.IsActive = true;
            _active = doc;
            RefreshActiveUi();
            doc.View.FocusEditor();
        }

        private void OnDocumentClosed(OpenDocument doc)
        {
            doc.Trace?.Detach();
            _documents.Remove(doc);
            if (ReferenceEquals(_active, doc)) _active = null;
            if (ReferenceEquals(_debugDocument, doc))
            {
                // Der Tab des laufenden Programms wurde geschlossen: den Lauf beenden.
                _session.Reset();
                _isBusy = false;
                _debugDocument = null;
            }
            RefreshActiveUi();
        }

        /// <summary>Tab-Titel: Name, bei ungespeicherten Änderungen mit Stern; der Tooltip zeigt den vollen Pfad.</summary>
        private void UpdateTitle(OpenDocument doc)
        {
            doc.Layout.Title = doc.DisplayName + (doc.View.IsModified ? "*" : "");
            doc.Layout.ToolTip = doc.View.FilePath ?? doc.DisplayName;
            if (ReferenceEquals(doc, ActiveDocument)) Title = WindowTitle(doc);
        }

        private static string WindowTitle(OpenDocument? doc) =>
            doc == null ? "fire Editor" : $"{doc.DisplayName}{(doc.View.IsModified ? "*" : "")} - fire Editor";

        /// <summary>Aktualisiert alles, was vom aktiven Dokument abhängt: Fenstertitel, Zeilenanzeige, Fehlerliste, Haltepunkte im Debugger-Panel.</summary>
        private void RefreshActiveUi()
        {
            var doc = ActiveDocument;
            Title = WindowTitle(doc);
            CaretText.Text = doc == null ? "" : $"Zeile {doc.View.GetCaretLine()}";
            UpdateErrorPanel();
            _debugger.Refresh(BreakpointDescriptions());
        }

        /// <summary>Fragt bei ungespeicherten Änderungen nach (Speichern/Verwerfen/Abbrechen). false = Schließen abbrechen.</summary>
        private bool ConfirmClose(OpenDocument doc)
        {
            if (!doc.View.IsModified) return true;
            var answer = MessageBox.Show(this, $"Änderungen an „{doc.DisplayName}“ speichern?", "fire Editor",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            return answer switch
            {
                MessageBoxResult.Yes => Save(doc),
                MessageBoxResult.No => true,
                _ => false,
            };
        }

        /// <summary>Speichert ein Dokument (ohne Pfad: "Speichern unter"). false = nicht gespeichert (abgebrochen/Fehler).</summary>
        private bool Save(OpenDocument doc) => doc.View.FilePath == null ? SaveAs(doc) : WriteDocument(doc, doc.View.FilePath);

        private bool SaveAs(OpenDocument doc)
        {
            var dlg = new SaveFileDialog
            {
                Filter = doc.Kind switch
                {
                    DocumentKind.Markdown => MarkdownFilter + "|Alle Dateien (*.*)|*.*",
                    DocumentKind.PacketLog => PacketLogFilter + "|Alle Dateien (*.*)|*.*",
                    _ => "fire-Dateien (*.script)|*.script|" + ScriptFilter + "|Alle Dateien (*.*)|*.*",
                },
                FileName = doc.View.FilePath ?? (doc.Kind == DocumentKind.PacketLog ? SafeFileName(doc.DisplayName) : ""),
                DefaultExt = doc.Kind switch { DocumentKind.Markdown => ".md", DocumentKind.PacketLog => fire.Device.Manager.DeviceManager.PacketLog.FileExtension, _ => ".script" },
                AddExtension = true,
            };
            if (dlg.ShowDialog() != true) return false;
            return WriteDocument(doc, dlg.FileName);
        }

        private bool WriteDocument(OpenDocument doc, string path)
        {
            try { File.WriteAllText(path, doc.View.GetText()); }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Speichern fehlgeschlagen", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            doc.View.FilePath = path;
            doc.View.MarkSaved();
            UpdateTitle(doc);
            UpdateStatus($"Gespeichert: {path}");
            return true;
        }

        // Dateien aus dem Explorer auf das Fenster ziehen: jede in einem eigenen Tab öffnen.
        private void Window_PreviewDragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
        }

        private void Window_PreviewDrop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
            foreach (var file in files.Where(File.Exists)) OpenFile(file);
            e.Handled = true;
        }

        private void Exit_Click(object sender, RoutedEventArgs e) => Close();

        /// <summary>Bewusst ein explizit registrierter Handler (siehe
        /// Konstruktor: `AddHandler(..., handledEventsToo: true)`) statt
        /// eines `OnPreviewKeyDown`-Overrides UND bewusst PreviewKeyDown
        /// (Tunneling, von der Wurzel zum fokussierten Element) statt
        /// KeyDown (Bubbling) - zwei unabhängige Gründe, warum die
        /// Hotkeys vorher nicht ankamen:
        ///
        /// 1. WPF behandelt F10 bei vorhandenem `Menu`-Element speziell
        ///    (aktiviert die Tastatur-Navigation des Menüs, klassisches
        ///    Windows-Verhalten) - Preview statt Bubbling läuft VOR dieser
        ///    eingebauten Behandlung.
        /// 2. WICHTIGER, der eigentliche Grund: F10 ist (wie Alt) unter
        ///    Windows eine "System-Taste" (WM_SYSKEYDOWN) - WPF liefert
        ///    dafür `e.Key == Key.System`, die TATSÄCHLICHE Taste steht in
        ///    `e.SystemKey`, NICHT in `e.Key` selbst! Ein Vergleich gegen
        ///    `e.Key == Key.F10` (wie vorher) matcht deshalb NIE, unabhängig
        ///    von Tunneling/Bubbling oder Handled-Status - das war der
        ///    eigentliche Bug, nicht (nur) die Event-Phase.
        ///
        /// `handledEventsToo: true` beim Registrieren macht das zusätzlich
        /// robust gegen JEDEN Fall, in dem irgendein Kind-Element das
        /// Ereignis bereits als Handled markiert hätte.</summary>
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Bei System-Tasten (F10, Alt+...) steht die eigentliche Taste in
            // SystemKey, e.Key ist dann nur Key.System (siehe Doku oben).
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;

            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

            switch (key)
            {
                case Key.F5 when ctrl:
                    RunToEnd_Click(this, e); e.Handled = true; break;
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
                case Key.V when ctrl && shift && ActiveDocument?.Markdown != null:
                    ToggleMarkdownPreview_Click(this, e); e.Handled = true; break;
            }
        }

        private void BuildSettings_Click(object sender, RoutedEventArgs e)
        {
            if (ActiveScript is not { } script)
            {
                UpdateStatus("Buildeinstellungen gibt es nur für Skript-Tabs.");
                return;
            }

            var buildSettings = new AssemblyInfoDialog();

            var source = script.GetText();

            var model = Linker.ExtractAssemblyInfo(new[] { source });

            buildSettings.DataContext = model;

            if (buildSettings.ShowDialog() == true)
            {
                model.CopyTo(_scriptAssemblyInfo);

                var directives = new List<(string, string?)>();

                if (model.Subsystem == Utilities.SubsystemType.GUI)
                {
                    directives.Add(("noconsole", ""));
                }
                else
                {
                    directives.Add(("noconsole", null));
                }

                if (model.ExecutionMode == Runtime.VmExecutionMode.Debug)
                {
                    directives.Add(("debug", ""));
                    directives.Add(("performance", null));
                } 
                else if (model.ExecutionMode == Runtime.VmExecutionMode.Performance)
                {
                    directives.Add(("debug", null));
                    directives.Add(("performance", ""));
                }
                else
                {
                    directives.Add(("debug", null));
                    directives.Add(("performance", null));
                }

                directives.Add(("name", model.ProductName));
                directives.Add(("codename", model.InternalName));
                directives.Add(("description", model.FileDescription));
                directives.Add(("author", model.CompanyName));
                directives.Add(("comments", model.Comments));

                directives.Add(("icon", model.IconPath));

                directives.Add(("version", model.ProductVersion));

                directives.Add(("fileversion", model.FileVersion));

                var formattedDirectives = new List<(string, string?)>();

                foreach (var directive in directives)
                {
                    if (!string.IsNullOrEmpty(directive.Item2))
                    {
                        formattedDirectives.Add((directive.Item1, $"\"{ValueUtils.EscapeString(directive.Item2)}\""));
                        continue;
                    }

                    formattedDirectives.Add(directive);
                }

                EnsureScriptHasDirectives(formattedDirectives);
            }
        }

        private void mnuRunDebug_Click(object sender, RoutedEventArgs e)
        {
            _session.ExecutionMode = Runtime.VmExecutionMode.Debug;
            UpdateExecutionModeSelection(_session);
        }

        private void mnuRunRelease_Click(object sender, RoutedEventArgs e)
        {
            _session.ExecutionMode = Runtime.VmExecutionMode.Release;
            UpdateExecutionModeSelection(_session);
        }

        private void mnuRunPerformance_Click(object sender, RoutedEventArgs e)
        {
            _session.ExecutionMode = Runtime.VmExecutionMode.Performance;
            UpdateExecutionModeSelection(_session);
        }

        private void EnsureScriptHasDirectives(IEnumerable<(string, string?)> directives)
        {
            if (ActiveScript is not { } script) return;
            var text = script.GetText();

            var textNew = new StringBuilder();
            var directivesAfter = directives.ToList();

            foreach (var line in text.AsSpan().EnumerateLines())
            {
                var match = false;
                foreach (var dir in directivesAfter.ToList())
                {
                    if (line.StartsWith($"#{dir.Item1}"))
                    {
                        match = true;
                        if (dir.Item2 != null)
                        {
                            if (dir.Item2.Length == 0)
                                textNew.AppendLine($"#{dir.Item1}");
                            else 
                                textNew.AppendLine($"#{dir.Item1} {dir.Item2}");
                        }
                        directivesAfter.Remove(dir);
                        break;
                    }
                } 
                if (!match)
                {
                    textNew.AppendLine(line.ToString());
                }
            }

            foreach (var dir in directivesAfter.Reverse<(string, string?)>())
            {
                if (dir.Item2 != null)
                {
                    if (dir.Item2.Length == 0)
                        textNew.Insert(0, $"#{dir.Item1}{Environment.NewLine}");
                    else
                        textNew.Insert(0, $"#{dir.Item1} {dir.Item2}{Environment.NewLine}");
                }
            }

            script.SetText(textNew.ToString());
        }

        private void Build_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog { Filter = "Ausführbare Dateien (*.exe)|*.exe|Alle Dateien (*.*)|*.*" };
            if (dlg.ShowDialog() != true) return;

            var filename = dlg.FileName;

            CompileAndPrepare(filename);
        }
    }
}
