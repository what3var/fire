using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace fire.Editor
{
    /// <summary>
    /// Slim, read-only window for showing ONE file or
    /// built-in prelude - for "go to definition/include" (see
    /// ScriptEditorControl.GoToDefinition) when the target is NOT in the edited
    /// document. Like the editor itself an AvalonEdit TextEditor
    /// (highlighting, line numbers, search, Ctrl+click with reliable
    /// text positions); if the user jumps on from HERE, that opens a
    /// further window or moves the target window - no
    /// backward navigation/history (deliberate simplification).
    /// </summary>
    public partial class FileViewerWindow : Window
    {
        private string _filePath = "";
        private string? _preludeName;
        private string _source = "";

        private readonly HighlightingColorizer _colorizer = new();
        private readonly LineBackgroundRenderer _lineBackground = new();

        // Only ONE window per prelude: further jumps into the same prelude only move it.
        private static readonly Dictionary<string, FileViewerWindow> OpenPreludes = new();

        public FileViewerWindow()
        {
            InitializeComponent();
            Viewer.TextArea.TextView.LineTransformers.Add(_colorizer);
            Viewer.TextArea.TextView.BackgroundRenderers.Add(_lineBackground);
            EditorTheme.Apply(Viewer, AvaloniaEdit.Search.SearchPanel.Install(Viewer));
            Viewer.AddHandler(PointerPressedEvent, Viewer_PointerPressed, RoutingStrategies.Tunnel);
        }

        /// <summary>Shows the built-in prelude `preludeName` (see
        /// ScriptSymbolIndex.PreludeSourceOf) scrolled to `line` - an already
        /// open window of the same prelude is reused.</summary>
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

            var viewer = new FileViewerWindow();
            viewer.LoadSource(ScriptSymbolIndex.PreludeTitleOf(preludeName), source, line, preludeName);
            OpenPreludes[preludeName] = viewer;
            viewer.Closed += (_, _) => OpenPreludes.Remove(preludeName);
            if (owner != null) viewer.Show(owner); else viewer.Show();
        }

        /// <summary>Loads `filePath` and shows it, optionally scrolled directly to
        /// `jumpToLine` (1-based) and subtly highlighted.
        /// If it can NOT be read (file missing, no access, ...) -
        /// shows an error message as the content, instead of not opening
        /// the window at all (the user should see WHAT went wrong, instead of
        /// wondering why the click did nothing).</summary>
        public void LoadFile(string filePath, int? jumpToLine = null)
        {
            _filePath = filePath;
            _preludeName = null;
            Title = $"View File - {Path.GetFileName(filePath)}";
            PathText.Text = filePath;

            string source;
            try
            {
                source = File.ReadAllText(filePath);
            }
            catch (Exception ex)
            {
                source = $"// The file could not be opened:\n// {filePath}\n// {ex.Message}";
            }

            DisplaySource(source, jumpToLine);
        }

        /// <summary>Like LoadFile, but for source text WITHOUT a real file path
        /// (a built-in prelude) - `title` stands directly in the window title/the
        /// path line instead of a file name. `preludeName`: which prelude it
        /// is (so that Ctrl+click on names of other preludes works).</summary>
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
                // Only after the first layout, otherwise the editor does not yet know the line heights.
                Dispatcher.UIThread.Post(() => JumpTo(jumpToLine.Value), DispatcherPriority.Loaded);
            else
                Viewer.TextArea.TextView.Redraw();
        }

        /// <summary>Highlights line `line` and scrolls to it.</summary>
        private void JumpTo(int line)
        {
            int target = Math.Max(1, Math.Min(line, Viewer.Document.LineCount));
            _lineBackground.HighlightedLine = target;
            Viewer.TextArea.Caret.Line = target;
            Viewer.TextArea.Caret.Column = 1;
            Viewer.ScrollToLine(target);
            Viewer.TextArea.TextView.Redraw();
        }

        private void Viewer_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            // Only Ctrl+click navigates (as in the editor) - a normal click
            // must still be able to set the cursor/select text quite normally.
            if (!e.GetCurrentPoint(Viewer).Properties.IsLeftButtonPressed || (e.KeyModifiers & KeyModifiers.Control) == 0) return;

            var pos = Viewer.GetPositionFromPoint(e.GetPosition(Viewer));
            if (pos == null) return;

            int offset = Viewer.Document.GetOffset(pos.Value.Location);
            // An extension prelude knows the classes of its dependencies (`ui` -> `graphics`) only via `#import`.
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
            var viewer = new FileViewerWindow();
            viewer.LoadFile(resolved, target.Line);
            viewer.Show(this);
        }
    }
}
