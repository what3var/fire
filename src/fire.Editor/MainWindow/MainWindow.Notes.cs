using System;
using System.IO;
using System.Linq;
using Avalonia.Interactivity;
using Avalonia.Threading;
using fire.Projects;

namespace fire.Editor
{
    // The notes of projects and solutions (docs/NOTES.md): the Todo window shows `notes/todo.md` of the project of the active document (or of the solution) and writes
    // every change to the file - or, when the file is open as a document, keeps the document in step with it.
    public partial class MainWindow
    {
        private readonly TodoPanelControl _todoPanel = new();
        private DispatcherTimer? _todoTimer;
        // what the window shows now: a change from outside (the file, the document) is noticed by comparing with it
        private (string? Title, string? Path, string? Text, string? ReadOnly) _todoShown;

        private void InitNotes()
        {
            _todoPanel.Write = WriteTodo;
            _todoPanel.ScopeChanged += () => RefreshTodo(force: true);
            _todoPanel.OpenFileRequested += path => OpenFile(path);
            // a todo list ticked off in a project overview goes the same way
            OverviewContext.WriteTodo = WriteTodo;
            OverviewContext.OpenFile = path => OpenFile(path);
            // a change from outside (git pull, another editor, the document of the list) is seen within a second
            _todoTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RefreshTodo());
            _todoTimer.Start();
        }

        /// <summary>The project a path is in: the project whose folder holds it (the innermost one).</summary>
        private LoadedProject? ProjectByFolder(string path)
        {
            string full = Path.GetFullPath(path);
            return _workspace.Projects
                .Where(p => full.StartsWith(p.Directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.Directory.Length)
                .FirstOrDefault();
        }

        /// <summary>Whose list the window shows: the project of the active document (also a note of it, like todo.md), else the one selected in the explorer, else the startup
        /// project - or the solution. null: none (nothing open, or no solution file).</summary>
        private (string Title, string Path)? TodoTarget(out string? why)
        {
            why = null;
            if (!_workspace.IsOpen) { why = "Open a project or a solution to keep a todo list (notes/todo.md)."; return null; }
            if (_todoPanel.Scope == TodoScope.Solution)
            {
                if (_workspace.Solution is { FilePath: not null } solution) return ($"Solution: {solution.Name}", ProjectNotes.TodoPath(solution));
                why = "No solution is open: the todo list of a project is under 'Project'.";
                return null;
            }
            var project = (FullPathOf(ActiveDocument) is { } path ? ProjectByFolder(path) : null) ?? _solutionPanel.Selected?.Project ?? _workspace.Startup;
            if (project == null) { why = "No project is selected."; return null; }
            return ($"Project: {project.Name}", ProjectNotes.TodoPath(project.Project));
        }

        private OpenDocument? DocumentOfFile(string path)
        {
            string full = Path.GetFullPath(path);
            return _documents.FirstOrDefault(d => FullPathOf(d) is { } p && ProjectFiles.PathComparer.Equals(p, full));
        }

        /// <summary>Shows the list of the current target again if anything about it changed (`force`: in any case).</summary>
        private void RefreshTodo(bool force = false)
        {
            var target = TodoTarget(out string? why);
            string? text = null, readOnly = null;
            if (target is { } t)
            {
                if (DocumentOfFile(t.Path) is { } doc)
                {
                    text = doc.View.GetText();
                    if (doc.View.IsModified) readOnly = "todo.md has unsaved changes in its document: save it to change the list here.";
                }
                else
                {
                    try { text = File.Exists(t.Path) ? File.ReadAllText(t.Path) : null; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { text = null; readOnly = "todo.md cannot be read: " + ex.Message; }
                }
            }
            var shown = (target?.Title, target?.Path, text, readOnly);
            if (!force && shown == _todoShown) return;
            _todoShown = shown;
            _todoPanel.Show(target?.Title, target?.Path, text, readOnly, why);
        }

        /// <summary>Writes the list the window changed: into the file (the folder `notes/` is made when it is missing), and into its document if it is open (and unchanged).</summary>
        private bool WriteTodo(string path, string text)
        {
            try
            {
                bool created = !File.Exists(path);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text);
                if (DocumentOfFile(path) is { View.IsModified: false } doc) doc.View.ResetTo(text, doc.View.FilePath);
                _todoShown = (_todoShown.Title, path, text, null);
                if (created) { foreach (var project in _workspace.Projects) project.Refresh(); RefreshProjectUi(); }   // notes/todo.md shows up in the explorer
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _ = Dialogs.Message(this, $"The todo list cannot be written: {ex.Message}", "Todo");
                return false;
            }
        }

        private void TodoOpen_Click(object? sender, RoutedEventArgs e)
        {
            RevealTool("todo");
            RefreshTodo(force: true);
        }
    }
}
