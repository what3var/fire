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
    /// <summary>Das ursprüngliche Einzeldatei-Editor-Fenster - bewusst
    /// erhalten (siehe SPEC/Projektauftrag "alten Editor bestehen lassen"),
    /// nutzt intern aber dieselben geteilten Controls wie das neue Tabbed-
    /// Projekt-Fenster (siehe ScriptEditorControl/DebuggerPanelControl-
    /// Klassendoku), statt eigene Editing-/Debugger-Anzeige-Logik zu
    /// pflegen - dieses Fenster selbst ist dadurch nur noch die
    /// ORCHESTRIERUNG (Datei-Menü, Kompilieren/Ausführen/Schritt-Buttons,
    /// Hotkeys, Ausgabe-Fenster), keine der beiden Controls kennt
    /// irgendetwas vom jeweils anderen oder von DebugSession/Kompilieren
    /// selbst.</summary>
    public partial class MainWindow : Window
    {
        private readonly DebugSession _session = new();

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

        public MainWindow()
        {
            InitializeComponent();

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

            DebuggerPanel.AttachSession(_session);
            DebuggerPanel.ThreadSelected += OnThreadSelected;

            EditorControl.CaretLineChanged += line => CaretText.Text = $"Zeile {line}";
            EditorControl.DiagnosticsChanged += UpdateErrorPanel;
            EditorControl.BreakpointsChanged += () =>
            {
                _session.UpdateBreakpoints(BreakpointLocations());
                DebuggerPanel.Refresh(BreakpointDescriptions());
            };

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
            _session.ThreadAdded += ctx => Dispatcher.InvokeAsync(() => DebuggerPanel.Refresh(BreakpointDescriptions()));
            _session.ThreadPaused += ctx => Dispatcher.InvokeAsync(() =>
            {
                // Nur wenn der GERADE ANGEZEIGTE (aktive) Thread betroffen
                // ist, muss die Detailanzeige (Hervorhebung/Scope/Stack)
                // neu aufgebaut werden - ein anderer, automatisch
                // weiterlaufender Fire-Thread, der gerade z.B. einen
                // Haltepunkt erreicht, aktualisiert erstmal nur seinen
                // eigenen Eintrag in der Threads-Liste.
                DebuggerPanel.Refresh(BreakpointDescriptions());
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

            EditorControl.ResetTo("// Willkommen im fire-Editor\nprint(\"Hallo, Welt!\")\n", null);
            _scriptAssemblyInfo = new AssemblyInfo();
            
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

        /// <summary>Übergangslösung, solange dieses Fenster immer nur EINE
        /// einzige Datei debuggt (siehe geplantes Projekt-Fenster für den
        /// Mehrdatei-Fall): die Haltepunkte des EditorControls sind bloße
        /// Zeilennummern, werden hier mit dem aktuell gültigen Quell-Index
        /// verknüpft (siehe DebugSession.FirstUserSourceIndex), bevor sie
        /// an die Datei-bewusste DebugSession-API gehen. Vor dem ersten
        /// erfolgreichen Compile() ist FirstUserSourceIndex noch 0 - dann
        /// wird 1 angenommen (Prelude liegt immer bei 0, der erste eigene
        /// Quelltext normalerweise bei 1).</summary>
        private HashSet<(int SourceIndex, int Line)> BreakpointLocations() =>
            EditorControl.Breakpoints
                .Select(l => (_session.FirstUserSourceIndex == 0 ? 1 : _session.FirstUserSourceIndex, l))
                .ToHashSet();

        /// <summary>Für DebuggerPanelControl.Refresh - nur EINE Datei, daher
        /// reichen die nackten (aber numerisch sortierten) Zeilennummern als
        /// Text.</summary>
        private List<string> BreakpointDescriptions() =>
            EditorControl.Breakpoints.OrderBy(l => l).Select(l => l.ToString()).ToList();

        private void UpdateErrorPanel()
        {
            var diagnostics = EditorControl.Diagnostics;
            string file = EditorControl.FilePath == null ? "(unbenannt)" : Path.GetFileName(EditorControl.FilePath);
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

            EditorControl.ScrollToLine(item.Line);
            EditorControl.SetCaretByLineColumn(Math.Max(0, item.Line - 1), 0);
            EditorControl.Focus();
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
            ["editor"] = EditorControl,
            ["output"] = OutputBox,
            ["errors"] = ErrorPanelContent,
            ["debugger"] = DebuggerPanel,
        };

        private void DeserializeLayout(TextReader reader)
        {
            var contents = PanelContents();
            var panels = new Dictionary<string, LayoutContent>();

            // Die Inhalte hängen noch an den bisherigen Layout-Elementen; ein Element kann nur einen Besitzer haben.
            foreach (var panel in _panels.Values) panel.Content = null;

            var serializer = new XmlLayoutSerializer(DockManager);
            serializer.LayoutSerializationCallback += (_, args) =>
            {
                if (args.Model.ContentId != null && contents.TryGetValue(args.Model.ContentId, out var content))
                {
                    args.Content = content;
                    panels[args.Model.ContentId] = args.Model;
                }
                else
                {
                    args.Cancel = true; // ein Bereich, den es in dieser Version nicht mehr gibt
                }
            };
            serializer.Deserialize(reader);
            _panels = panels;
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
            if (!e.Cancel) SaveLayout();
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

            if (anchorable.IsVisible)
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
            EditorControl.HighlightedLine = chosen.IsFinished ? null : chosen.Vm.CurrentLine;
            if (EditorControl.HighlightedLine != null) EditorControl.ScrollToLine(EditorControl.HighlightedLine.Value);
            DebuggerPanel.Refresh(BreakpointDescriptions());
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

        private void Run_Click(object sender, RoutedEventArgs e) => CompileAndPrepare(null);

        private void CompileAndPrepare(string? filename)
        {
            OutputBox.Clear();
            while (_pendingOutput.TryDequeue(out _)) { } // Reste eines evtl. noch nicht abgeflossenen vorigen Laufs verwerfen
            EditorControl.HighlightedLine = null;
            _isBusy = false;
            string source = EditorControl.GetText();
            _session.UpdateBreakpoints(BreakpointLocations());

            if (!_session.Compile(new[] { source }, filename))
            {
                UpdateStatus($"Kompilierfehler: {_session.CompileError}");
                MessageBox.Show(_session.CompileError, "Kompilierfehler",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            UpdateStatus("Kompiliert - bereit für Einzelschritt/Weiter/Bis Ende.");
            DebuggerPanel.Refresh(BreakpointDescriptions());
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
            _session.Continue(BreakpointLocations());
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
            EditorControl.HighlightedLine = null;
            DebuggerPanel.Refresh(BreakpointDescriptions());
            UpdateStatus("Gestoppt.");
        }

        private void AfterStep(bool more)
        {
            FlushPendingOutput(); // sofort sichtbar, nicht erst beim nächsten Timer-Tick
            if (!more)
            {
                EditorControl.HighlightedLine = null;
                UpdateStatus(_session.RuntimeError != null
                    ? $"Laufzeitfehler: {_session.RuntimeError}"
                    : "Programm beendet.");
            }
            else
            {
                EditorControl.HighlightedLine = _session.Vm!.CurrentLine;
                EditorControl.ScrollToLine(EditorControl.HighlightedLine.Value);
                UpdateStatus($"Angehalten in Zeile {EditorControl.HighlightedLine}.");
            }
            DebuggerPanel.Refresh(BreakpointDescriptions());
        }

        private void ToggleBreakpoint_Click(object sender, RoutedEventArgs e) => EditorControl.ToggleBreakpointAtCaret();

        // -----------------------------------------------------------
        // Datei-Menü
        // -----------------------------------------------------------

        private void New_Click(object sender, RoutedEventArgs e)
        {
            EditorControl.ResetTo(string.Empty, null);
            Stop_Click(sender, e);
            UpdateStatus("Neue Datei.");
        }

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "fire-Dateien (*.script;*.fi;*.fic)|*.script;*.fi;*.fic|Alle Dateien (*.*)|*.*" };
            if (dlg.ShowDialog() != true) return;

            EditorControl.ResetTo(File.ReadAllText(dlg.FileName), dlg.FileName);
            Stop_Click(sender, e);
            UpdateStatus($"Geöffnet: {dlg.FileName}");
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (EditorControl.FilePath == null)
            {
                SaveAs_Click(sender, e);
                return;
            }
            File.WriteAllText(EditorControl.FilePath, EditorControl.GetText());
            UpdateStatus($"Gespeichert: {EditorControl.FilePath}");
        }

        private void SaveAs_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog { Filter = "fire-Dateien (*.script)|*.script|Alle Dateien (*.*)|*.*" };
            if (dlg.ShowDialog() != true) return;

            EditorControl.FilePath = dlg.FileName;
            File.WriteAllText(dlg.FileName, EditorControl.GetText());
            UpdateStatus($"Gespeichert: {dlg.FileName}");
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
                case Key.N when ctrl:
                    New_Click(this, e); e.Handled = true; break;
                case Key.O when ctrl:
                    Open_Click(this, e); e.Handled = true; break;
                case Key.S when ctrl:
                    Save_Click(this, e); e.Handled = true; break;
            }
        }

        private void BuildSettings_Click(object sender, RoutedEventArgs e)
        {
            var buildSettings = new AssemblyInfoDialog();

            var source = EditorControl.GetText();

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
            var text = EditorControl.GetText();

            var textNew = new StringBuilder();
            var directivesAfter = directives.ToList();

            foreach (var line in text.EnumerateLines())
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

            EditorControl.SetText(textNew.ToString());
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
