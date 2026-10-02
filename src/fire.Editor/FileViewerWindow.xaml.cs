using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace fire.Editor
{
    /// <summary>
    /// Schlankes, schreibgeschütztes Fenster zum Anzeigen EINER Datei oder
    /// eingebauten Prelude - für "zu Definition/Include springen" (siehe
    /// ScriptEditorControl.GoToDefinition), wenn das Ziel NICHT im bearbeiteten
    /// Dokument liegt. Wie der Editor selbst ein AvalonEdit-TextEditor
    /// (Hervorhebung, Zeilennummern, Suchen, Strg+Klick mit zuverlässigen
    /// Textpositionen); springt der Nutzer von HIER aus weiter, öffnet das ein
    /// weiteres Fenster bzw. bewegt das Ziel-Fenster - keine
    /// Rückwärtsnavigation/Verlauf (bewusste Vereinfachung).
    /// </summary>
    public partial class FileViewerWindow : Window
    {
        private string _filePath = "";
        private string? _preludeName;
        private string _source = "";

        private readonly HighlightingColorizer _colorizer = new();
        private readonly LineBackgroundRenderer _lineBackground = new();

        // Pro Prelude nur EIN Fenster: weitere Sprünge in dieselbe Prelude bewegen es nur.
        private static readonly Dictionary<string, FileViewerWindow> OpenPreludes = new();

        public FileViewerWindow()
        {
            InitializeComponent();
            Viewer.TextArea.TextView.LineTransformers.Add(_colorizer);
            Viewer.TextArea.TextView.BackgroundRenderers.Add(_lineBackground);
            ICSharpCode.AvalonEdit.Search.SearchPanel.Install(Viewer);
            Viewer.PreviewMouseLeftButtonDown += Viewer_PreviewMouseLeftButtonDown;
        }

        /// <summary>Zeigt die eingebaute Prelude `preludeName` (siehe
        /// ScriptSymbolIndex.PreludeSourceOf) zu `line` gescrollt - ein bereits
        /// offenes Fenster derselben Prelude wird wiederverwendet.</summary>
        public static void ShowPrelude(string preludeName, int line, Window? owner = null)
        {
            if (OpenPreludes.TryGetValue(preludeName, out var existing))
            {
                existing.JumpTo(line);
                if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
                existing.Activate();
                return;
            }

            string? source = ScriptSymbolIndex.PreludeSourceOf(preludeName);
            if (source == null) return;

            var viewer = new FileViewerWindow { Owner = owner };
            viewer.LoadSource(ScriptSymbolIndex.PreludeTitleOf(preludeName), source, line, preludeName);
            OpenPreludes[preludeName] = viewer;
            viewer.Closed += (_, _) => OpenPreludes.Remove(preludeName);
            viewer.Show();
        }

        /// <summary>Lädt `filePath` und zeigt es an, optional direkt zu
        /// `jumpToLine` (1-basiert) gescrollt und dezent hervorgehoben.
        /// Kann NICHT gelesen werden (Datei fehlt, kein Zugriff, ...) -
        /// zeigt eine Fehlermeldung als Inhalt an, statt das Fenster gar
        /// nicht erst zu öffnen (der Nutzer soll sehen, WAS schiefging, statt
        /// sich zu fragen, warum der Klick nichts getan hat).</summary>
        public void LoadFile(string filePath, int? jumpToLine = null)
        {
            _filePath = filePath;
            _preludeName = null;
            Title = $"Datei ansehen - {Path.GetFileName(filePath)}";
            PathText.Text = filePath;

            string source;
            try
            {
                source = File.ReadAllText(filePath);
            }
            catch (Exception ex)
            {
                source = $"// Datei konnte nicht geöffnet werden:\n// {filePath}\n// {ex.Message}";
            }

            DisplaySource(source, jumpToLine);
        }

        /// <summary>Wie LoadFile, aber für Quelltext OHNE echten Dateipfad
        /// (eine eingebaute Prelude) - `title` steht direkt im Fenstertitel/der
        /// Pfad-Zeile statt eines Dateinamens. `preludeName`: welche Prelude es
        /// ist (damit Strg+Klick auf Namen anderer Preludes funktioniert).</summary>
        public void LoadSource(string title, string source, int? jumpToLine = null, string? preludeName = null)
        {
            _filePath = "";
            _preludeName = preludeName;
            Title = title;
            PathText.Text = title;
            DisplaySource(source, jumpToLine);
        }

        private void DisplaySource(string source, int? jumpToLine)
        {
            _source = source;
            Viewer.Text = source;
            _colorizer.Spans = SyntaxHighlighter.Highlight(source);
            if (jumpToLine.HasValue)
                // Erst nach dem ersten Layout, sonst kennt der Editor die Zeilenhöhen noch nicht.
                Dispatcher.BeginInvoke(new Action(() => JumpTo(jumpToLine.Value)), DispatcherPriority.Loaded);
            else
                Viewer.TextArea.TextView.Redraw();
        }

        /// <summary>Hebt Zeile `line` hervor und scrollt hin.</summary>
        private void JumpTo(int line)
        {
            int target = Math.Max(1, Math.Min(line, Viewer.Document.LineCount));
            _lineBackground.HighlightedLine = target;
            Viewer.TextArea.Caret.Line = target;
            Viewer.TextArea.Caret.Column = 1;
            Viewer.ScrollToLine(target);
            Viewer.TextArea.TextView.Redraw();
        }

        private void Viewer_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Nur Strg+Klick navigiert (wie im Editor) - ein normaler Klick
            // muss weiterhin ganz gewöhnlich den Cursor setzen/Text markieren können.
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;

            var pos = Viewer.GetPositionFromPoint(e.GetPosition(Viewer));
            if (pos == null) return;

            int offset = Viewer.Document.GetOffset(pos.Value.Location);
            // Eine Erweiterungs-Prelude kennt die Klassen ihrer Abhängigkeiten (`ui` -> `graphics`) nur über `#import`.
            var index = _preludeName is null or ScriptSymbolIndex.StandardPreludeName
                ? ScriptSymbolIndex.Build(_source)
                : ScriptSymbolIndex.Build(_source, new[] { _preludeName });
            var target = NavigationEngine.TryResolve(_source, offset, index);
            if (target == null) return;

            e.Handled = true;

            if (target.PreludeName != null && target.PreludeName != _preludeName)
            {
                ShowPrelude(target.PreludeName, target.Line, this);
                return;
            }

            if (target.FilePath == null)
            {
                JumpTo(target.Line);
                return;
            }

            string? dir = Path.GetDirectoryName(_filePath);
            string resolved = Path.GetFullPath(Path.Combine(dir ?? ".", target.FilePath));
            var viewer = new FileViewerWindow { Owner = this };
            viewer.LoadFile(resolved, target.Line);
            viewer.Show();
        }
    }
}
