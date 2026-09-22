using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
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

        public MainWindow()
        {
            InitializeComponent();

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

            EditorControl.ResetTo("// Willkommen im fire-Editor\nprint(\"Hallo, Welt!\")\n", null);
            UpdateStatus("Bereit.");
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
            ErrorList.ItemsSource = diagnostics.Select(d => d.ToString()).ToList();
            ErrorPanelHeader.Text = diagnostics.Count == 0
                ? "Fehler (keine)"
                : $"Fehler ({diagnostics.Count})";
        }

        private void ErrorList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var diagnostics = EditorControl.Diagnostics;
            if (ErrorList.SelectedIndex < 0 || ErrorList.SelectedIndex >= diagnostics.Count) return;
            int line = diagnostics[ErrorList.SelectedIndex].Line;
            EditorControl.ScrollToLine(line);
            EditorControl.SetCaretByLineColumn(Math.Max(0, line - 1), 0);
            EditorControl.Focus();
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

        private void Run_Click(object sender, RoutedEventArgs e) => CompileAndPrepare();

        private void CompileAndPrepare()
        {
            OutputBox.Clear();
            while (_pendingOutput.TryDequeue(out _)) { } // Reste eines evtl. noch nicht abgeflossenen vorigen Laufs verwerfen
            EditorControl.HighlightedLine = null;
            _isBusy = false;
            string source = EditorControl.GetText();
            _session.UpdateBreakpoints(BreakpointLocations());

            if (!_session.Compile(new[] { source }))
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
            if (_session.Vm == null) CompileAndPrepare();
            if (_session.Vm == null || _isBusy) return;
            BeginStep();
            _session.StepLine();
        }

        private void StepInto_Click(object sender, RoutedEventArgs e)
        {
            if (_session.Vm == null) CompileAndPrepare();
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
            if (_session.Vm == null) CompileAndPrepare();
            if (_session.Vm == null || _isBusy) return;
            BeginStep();
            _session.Continue(BreakpointLocations());
        }

        private void RunToEnd_Click(object sender, RoutedEventArgs e)
        {
            if (_session.Vm == null) CompileAndPrepare();
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
            var dlg = new OpenFileDialog { Filter = "fire-Dateien (*.script)|*.script|Alle Dateien (*.*)|*.*" };
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
    }
}
