using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using fire.Compiler;
using fire.Projects;

namespace fire.Editor
{
    // Projects and solutions (docs/tools/projects.md): the workspace, the solution explorer, the Project menu, what Start and Build act on.
    public partial class MainWindow
    {
        private static readonly FilePickerFileType AllFilesType = new("All files") { Patterns = new[] { "*" } };
        private static readonly FilePickerFileType ProjectFilesType = new("fire projects and solutions") { Patterns = new[] { "*" + FireProject.Extension, "*" + FireSolution.Extension } };
        private static readonly FilePickerFileType ProjectOnlyType = new("fire projects") { Patterns = new[] { "*" + FireProject.Extension } };
        private static readonly FilePickerFileType SolutionOnlyType = new("fire solutions") { Patterns = new[] { "*" + FireSolution.Extension } };

        /// <summary>What is open of solutions and projects. Single files are always possible next to it: only the active document decides what Start and Build act on.</summary>
        private readonly Workspace _workspace = new();

        // a command of the solution explorer (Start Debugging, Build, Clean of a project) acts on this project once, whatever the active document is
        private LoadedProject? _projectOverride;
        private bool _updatingStartProjectUi;

        private void InitProjects()
        {
            _workspace.Changed += () => Dispatcher.UIThread.Post(OnWorkspaceChanged);
            _solutionPanel.FileOpenRequested += path => OpenFile(path);
            _solutionPanel.CommandRequested += (command, node) => _ = OnSolutionCommand(command, node);
            _solutionPanel.SelectionChanged += OnExplorerSelection;
            // the switches of its tool bar are kept in the settings
            _solutionPanel.TrackActive = _settings.ExplorerTrackActive;
            _solutionPanel.ShowAllFiles = _settings.ExplorerShowAllFiles;
            _solutionPanel.OptionsChanged += () =>
            {
                _settings.ExplorerTrackActive = _solutionPanel.TrackActive;
                _settings.ExplorerShowAllFiles = _solutionPanel.ShowAllFiles;
                _settings.Save();
            };
            mnuCloseWorkspace.IsEnabled = false;
        }

        // -----------------------------------------------------------
        // The properties of the file selected in the explorer (tool window "Properties")
        // -----------------------------------------------------------

        private FilePropertiesHost? _fileProperties;
        private FilePropertiesHost FileProperties => _fileProperties ??= new FilePropertiesHost { SaveProject = p => _workspace.SaveProject(p), Focus = () => _solutionPanel.Focus() };
        // the document the property window last followed; while it stays active, a file selected in the explorer is shown instead of it
        private OpenDocument? _propertiesDocument;
        private bool _showFileProperties;

        /// <summary>The property window follows the active document (one with properties) - or the file selected in the explorer, until another document becomes active.</summary>
        private void AttachProperties(OpenDocument? doc)
        {
            var host = doc?.View as IPropertyHost;
            if (!ReferenceEquals(doc, _propertiesDocument))
            {
                _propertiesDocument = doc;
                _showFileProperties = host == null && FileProperties.HasFile;
            }
            _propertiesPanel.Attach(_showFileProperties ? FileProperties : host);
        }

        private void OnExplorerSelection(ExplorerNode? node)
        {
            if (node is not { Kind: ExplorerKind.File or ExplorerKind.Content, Path: { } path }) return;   // (a folder, a project: the window keeps what it shows)
            var others = SelectedFiles().Where(f => !ProjectFiles.PathComparer.Equals(f.Path, path)).ToList();
            FileProperties.Show(new FilePropertiesContext(path, node.Project) { Others = others });
            _showFileProperties = true;
            _propertiesPanel.Attach(FileProperties);
        }

        /// <summary>The files selected in the explorer (several with Ctrl or Shift), as file property contexts: the properties window shows what they have in common.</summary>
        private List<FilePropertiesContext> SelectedFiles() => _solutionPanel.SelectedNodes
            .Where(n => n is { Kind: ExplorerKind.File or ExplorerKind.Content, Path: not null, Project: not null, IsHidden: false })
            .Select(n => new FilePropertiesContext(n.Path!, n.Project)).ToList();

        /// <summary>The Build Action submenu of the explorer: the action for every selected file (docs/tools/projects.md, "Build actions"); the project files are saved.</summary>
        private void SetBuildAction(string name)
        {
            if (FileBuildActions.Parse(name) is not { } action) return;
            var files = SelectedFiles();
            if (files.Count == 0) return;
            string? problem = BuildActionFileProperties.Apply(files, action, p => _workspace.SaveProject(p));
            UpdateStatus(problem ?? $"Build action of {(files.Count == 1 ? Path.GetFileName(files[0].Path) : files.Count + " files")}: {FileBuildActions.Name(action)}.");
            RefreshFileProperties();
        }

        /// <summary>After the workspace changed: the file's project is found again (a reloaded workspace has new ones) and its rows are read again.</summary>
        private void RefreshFileProperties()
        {
            if (_fileProperties?.Properties == null || _fileProperties.Context is not { } file) return;
            var project = file.Project == null ? null : _workspace.AllProjects.FirstOrDefault(p => ProjectFiles.PathComparer.Equals(p.FilePath, file.Project.FilePath));
            if (!ReferenceEquals(project, file.Project)) _fileProperties.Show(new FilePropertiesContext(file.Path, project) { Others = SelectedFiles().Where(f => !ProjectFiles.PathComparer.Equals(f.Path, file.Path)).ToList() });
            else _fileProperties.Refresh();
        }

        // -----------------------------------------------------------
        // Which project does the active document belong to?
        // -----------------------------------------------------------

        private static string? FullPathOf(OpenDocument? doc) => doc?.View.FilePath is { } p ? Path.GetFullPath(p) : null;

        /// <summary>The project Start and Build act on: the project of the active document (a file of it, or of a library it references); for a document that is no script (or no document) the
        /// startup project of the solution; null for a single file that belongs to no project (it is built alone, as always).</summary>
        private LoadedProject? ContextProject()
        {
            if (_projectOverride != null) return _projectOverride;
            if (!_workspace.IsOpen) return null;
            var doc = ActiveDocument;
            if (doc?.Script != null)
                return FullPathOf(doc) is { } path ? _workspace.FindProjectOf(path) : null;
            return _workspace.Startup;
        }

        /// <summary>The project Start (F5, Ctrl+F5, the steps) runs: from every file of the solution - a file of any of its projects, a project file, the solution file, notes - the start
        /// project of the solution (without a solution the project); null for a single script that belongs to no project (it runs alone). See Workspace.StartProjectFor.</summary>
        private LoadedProject? StartProject()
        {
            if (_projectOverride != null) return _projectOverride;
            var doc = ActiveDocument;
            return _workspace.StartProjectFor(FullPathOf(doc), doc?.Script != null);
        }

        /// <summary>The project a Project menu command concerns: the one selected in the explorer when it has the focus's selection, else the context project, else the startup project.</summary>
        private LoadedProject? CommandProject(ExplorerNode? node = null) => node?.Project ?? _solutionPanel.Selected?.Project ?? ContextProject() ?? _workspace.Startup;

        /// <summary>The text of an open document (also an unsaved one) for the build plan.</summary>
        private string? OpenText(string path)
        {
            string full = Path.GetFullPath(path);
            var doc = _documents.FirstOrDefault(d => d.Script != null && FullPathOf(d) is { } p && ProjectFiles.PathComparer.Equals(p, full));
            return doc?.Script?.GetText();
        }

        private BuildPlan CreatePlan(LoadedProject project) => BuildPlan.Create(_workspace, project, OpenText);

        /// <summary>The symbols of `#if` that the settings of the project and the solution add (the branches that are not taken are greyed out).</summary>
        private IReadOnlyList<string>? ProjectDefinesOf(ScriptEditorControl script)
        {
            if (!_workspace.IsOpen || script.FilePath == null) return null;
            var project = _workspace.FindProjectOf(script.FilePath);
            if (project == null) return null;
            var merged = ProjectSettings.Merge(project.Project.Settings, _workspace.Solution?.Settings);
            return merged.Defines;
        }

        /// <summary>The texts of the open scripts (also unsaved ones) - taken on the UI thread for a check that runs in the background.</summary>
        private Dictionary<string, string> OpenTexts()
        {
            var texts = new Dictionary<string, string>(ProjectFiles.PathComparer);
            foreach (var doc in _documents)
                if (doc.Script != null && FullPathOf(doc) is { } path) texts[path] = doc.Script.GetText();
            return texts;
        }

        /// <summary>The live diagnostics of a document: in a project the other files and the imported libraries are known, else the file alone as always. Called on the UI thread when the
        /// check starts: it takes the texts of the open documents; the plan (which reads the other files from the disk) and the analysis run in the background. The result is remembered:
        /// the same texts are not analysed twice (LiveDiagnostics.Remembered).</summary>
        private Func<CancellationToken, List<Diagnostic>> AnalyzeDocumentJob(ScriptEditorControl script, string source)
        {
            string? baseDirectory = script.BaseDirectory;
            if (_workspace.IsOpen && script.FilePath != null && _workspace.FindProjectOf(script.FilePath) is { } project)
            {
                var workspace = _workspace;
                var open = OpenTexts();
                string self = Path.GetFullPath(script.FilePath);
                return _ =>
                {
                    try
                    {
                        BuildPlan plan;
                        using (EditorTiming.Measure("  build plan")) plan = BuildPlan.Create(workspace, project, path => open.TryGetValue(Path.GetFullPath(path), out var text) ? text : FileTexts.Read(path));
                        var others = plan.Sources.Where(f => !ProjectFiles.PathComparer.Equals(Path.GetFullPath(f.Path), self)).Select(f => (f.Path, f.Text)).ToList();
                        var (libraries, settings) = LiveDiagnostics.PlanInputs(plan);
                        return LiveDiagnostics.Remembered("document:" + self, source, others.Concat(libraries).ToList(), settings, () =>
                        {
                            using var analyze = EditorTiming.Measure($"  analyze in project ({others.Count} other files, {others.Sum(o => o.Text.Length) / 1024} KB)");
                            return LiveDiagnostics.AnalyzeInProject(source, others, baseDirectory, plan);
                        });
                    }
                    // the files cannot be read right now (or the workspace changed under the plan): the file alone
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ProjectException or InvalidOperationException) { }
                    return LiveDiagnostics.Analyze(source, baseDirectory);
                };
            }
            return _ => LiveDiagnostics.Analyze(source, baseDirectory);
        }

        // ---- the files that are not open: one check per project (the errors of all its files at once) ------------------------------------------------------------

        private readonly Dictionary<string, BackgroundCheck<(IReadOnlyCollection<string> Files, List<Diagnostic> Found)>> _projectChecks = new(ProjectFiles.PathComparer);

        /// <summary>The result of the last check of each project (by the path of its project file): its files and their errors (each with its file).</summary>
        private readonly Dictionary<string, (IReadOnlyCollection<string> Files, List<Diagnostic> Found)> _projectErrors = new(ProjectFiles.PathComparer);

        /// <summary>Checks all the files of `project` after `delay` (in the background, one analysis at a time with the documents): their errors go into the error list - also those
        /// of the files that are not open.</summary>
        private void ScheduleProjectCheck(LoadedProject project, TimeSpan delay)
        {
            string key = project.FilePath;
            if (!_projectChecks.TryGetValue(key, out var check))
            {
                check = new BackgroundCheck<(IReadOnlyCollection<string>, List<Diagnostic>)>(action => Dispatcher.UIThread.Post(action, DispatcherPriority.Background), LiveDiagnostics.Gate)
                {
                    Priority = () => LiveDiagnostics.ProjectPriority,   // (after the checks of the open documents: the errors where the user types come first)
                };
                check.Completed += (result, _) =>
                {
                    if (!_workspace.IsOpen || !_projectChecks.ContainsKey(key)) return;
                    _projectErrors[key] = result;
                    ScheduleErrorPanel();
                };
                check.Failed += (ex, _) => EditorTiming.Log($"project check {project.Name} failed: {ex.Message}");
                _projectChecks[key] = check;
            }
            check.Request(() =>
            {
                if (!_workspace.IsOpen) return null;
                var workspace = _workspace;
                var open = OpenTexts();
                return _ =>
                {
                    using var timing = EditorTiming.Measure($"project check {project.Name} (background)");
                    var plan = BuildPlan.Create(workspace, project, path => open.TryGetValue(Path.GetFullPath(path), out var text) ? text : FileTexts.Read(path));
                    var files = plan.Sources.Where(f => !FireProject.IsMarkupFile(f.Path)).Select(f => (Path.GetFullPath(f.Path), f.Text)).ToList();
                    var (libraries, settings) = LiveDiagnostics.PlanInputs(plan);
                    var found = LiveDiagnostics.Remembered("project:" + key, "", files.Concat(libraries).ToList(), settings, () => LiveDiagnostics.AnalyzeProjectFiles(files, plan, project.Directory));
                    return ((IReadOnlyCollection<string>)files.Select(f => f.Item1).ToHashSet(ProjectFiles.PathComparer), found);
                };
            }, delay);
        }

        /// <summary>The projects whose program contains `project`: itself and those that reference it (directly or through other libraries).</summary>
        private List<LoadedProject> ProjectsWith(LoadedProject project)
        {
            var result = new List<LoadedProject> { project };
            for (int i = 0; i < result.Count; i++)
                foreach (var p in _workspace.AllProjects)
                {
                    if (result.Contains(p)) continue;
                    bool references = p.Project.References.Any(r =>
                    {
                        if (!r.IsProject) return false;
                        try { return ProjectFiles.PathComparer.Equals(Path.GetFullPath(r.Project!.Replace('\\', '/'), p.Directory), result[i].FilePath); }
                        catch (Exception) { return false; }
                    });
                    if (references) result.Add(p);
                }
            return result;
        }

        /// <summary>All the projects of the workspace are checked again (opened, changed on the disk); results that did not change cost nothing.</summary>
        private void ScheduleAllProjectChecks(TimeSpan delay)
        {
            if (!_workspace.IsOpen)
            {
                foreach (var check in _projectChecks.Values) check.Cancel();
                _projectChecks.Clear();
                _projectErrors.Clear();
                ScheduleErrorPanel();
                return;
            }
            // (a project that left the workspace leaves the list)
            var current = new HashSet<string>(_workspace.AllProjects.Select(p => p.FilePath), ProjectFiles.PathComparer);
            foreach (var gone in _projectChecks.Keys.Where(k => !current.Contains(k)).ToList()) { _projectChecks[gone].Cancel(); _projectChecks.Remove(gone); _projectErrors.Remove(gone); }
            foreach (var project in _workspace.AllProjects) ScheduleProjectCheck(project, delay);
        }

        /// <summary>The check of a script found that what the other files see of it changed (a declaration, a signature, an `#import` - DeclarationShape): the other open scripts
        /// that are checked with it are checked again, and so are the projects whose program contains it (for their files that are not open). A change inside the body of a function
        /// changes nothing for them: typing there checks the script alone - before, every keystroke asked for the check of every project that contains the file (all fourteen of a
        /// solution whose libraries build on each other, a few seconds of compiling after every pause).</summary>
        private void OnDeclarationsChanged(OpenDocument doc)
        {
            if (FullPathOf(doc) is not { } path) return;
            foreach (var other in _documents)
                if (!ReferenceEquals(other, doc) && other.Script is { } script && script.DependsOn(path)) script.ScheduleDiagnostics(TimeSpan.FromMilliseconds(300));
            if (_workspace.IsOpen && _workspace.FindProjectOf(path) is { } project)
                foreach (var p in ProjectsWith(project)) ScheduleProjectCheck(p, TimeSpan.FromMilliseconds(1500));
        }

        /// <summary>A script was closed: its errors in the error list come from the check of its project from now on - checked again with the text on the disk (its changes
        /// since the last check of the project were known only to the check of the document). Unchanged texts are remembered (LiveDiagnostics.Remembered).</summary>
        private void OnScriptClosed(OpenDocument doc)
        {
            if (doc.Script == null || FullPathOf(doc) is not { } path || !_workspace.IsOpen || _workspace.FindProjectOf(path) is not { } project) return;
            ScheduleProjectCheck(project, TimeSpan.FromMilliseconds(500));
            if (doc.View.IsModified)   // (closed without saving: the others had seen its unsaved declarations)
                foreach (var p in ProjectsWith(project)) ScheduleProjectCheck(p, TimeSpan.FromMilliseconds(1500));
            foreach (var other in _documents)
                if (!ReferenceEquals(other, doc) && doc.View.IsModified && other.Script is { } script && script.DependsOn(path)) script.ScheduleDiagnostics(TimeSpan.FromMilliseconds(300));
        }

        /// <summary>The text of a file of the project: the open buffer (also an unsaved one), else the file (read again when it changed, FileTexts).</summary>
        private string? TextOfFile(string path)
        {
            if (OpenText(path) is { } open) return open;
            try { return FileTexts.Read(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }

        /// <summary>The script of a file of the project: its text, or for the markup of a user interface (`.fxml`) the script that is generated from it (null if it is not valid).</summary>
        private string? ScriptTextOfFile(string path)
        {
            string? text = TextOfFile(path);
            if (text == null || !FireProject.IsMarkupFile(path)) return text;
            try { return BuildPlan.GenerateMarkupScript(path, text); }
            catch (fire.UI.Markup.MarkupException) { return null; }
        }

        /// <summary>The other files of the project of a document, and the files of the libraries that it imports (`#import "Name"`): what they declare is known to completion, tooltips
        /// and "go to definition" without `#include`, as it is to the build.</summary>
        private IReadOnlyList<(string Path, string Text)> ProjectFilesFor(ScriptEditorControl script)
        {
            using var timing = EditorTiming.Measure("project files");
            var result = new List<(string, string)>();
            if (!_workspace.IsOpen || script.FilePath == null || _workspace.FindProjectOf(script.FilePath) is not { } project) return result;
            string self = Path.GetFullPath(script.FilePath);
            foreach (var file in project.Files)
                if (!ProjectFiles.PathComparer.Equals(file, self) && ScriptTextOfFile(file) is { } text) result.Add((file, text));
            var imports = new HashSet<string>(ImportedPreludes.FindImportNames(script.GetText()).Concat(result.SelectMany(r => ImportedPreludes.FindImportNames(r.Item2))), StringComparer.OrdinalIgnoreCase);
            if (imports.Count > 0)
                foreach (var reference in project.Project.References.Where(r => r.IsProject))
                {
                    try
                    {
                        var library = _workspace.LoadProject(Path.GetFullPath(reference.Project!.Replace('\\', '/'), project.Directory));
                        if (!imports.Contains(library.Project.ImportName)) continue;
                        foreach (var file in library.Files)
                            if (ScriptTextOfFile(file) is { } text) result.Add((file, text));
                    }
                    catch (Exception ex) when (ex is ProjectException or IOException) { /* a library that cannot be read adds nothing */ }
                }
            return result;
        }

        private void AttachProjectSupport(ScriptEditorControl script)
        {
            script.ProjectFilesProvider = () => ProjectFilesFor(script);
            script.DiagnosticsJob = source => AnalyzeDocumentJob(script, source);
            script.ExtraDefines = () => ProjectDefinesOf(script);
        }

        // -----------------------------------------------------------
        // Showing it
        // -----------------------------------------------------------

        private void OnWorkspaceChanged()
        {
            mnuCloseWorkspace.IsEnabled = _workspace.IsOpen;
            RefreshProjectUi();
            RefreshGit();
            ScriptEditorControl.NoteProjectChanged();
            fire.Package.Manager.PackageStore.Default.ClearOverlay();   // the natives of the projects are put back by the next build or analysis
            // what belongs to which project (and the libraries) may have changed: check the open scripts again, and the projects (their files that are not open)
            foreach (var doc in _documents) { doc.Script?.InvalidateConditionalSymbols(); doc.Script?.Revalidate(); }
            ScheduleAllProjectChecks(TimeSpan.FromSeconds(2));
            ScheduleTestDiscovery();
            RefreshTodo();
            RefreshFileProperties();
        }

        /// <summary>The explorer and the status bar follow the workspace and the active document.</summary>
        private void RefreshProjectUi()
        {
            var doc = ActiveDocument;
            _solutionPanel.Refresh(_workspace, FullPathOf(doc), _gitRoot == null ? null : _gitStates, _gitBranch);
            var project = ContextProject();
            var start = StartProject();
            ContextText.Text = project != null
                ? $"Build: {project.Name} ({(project.Project.Type == OutputType.Library ? "library" : "project")})" + (start != null && !ReferenceEquals(start, project) ? $", Start: {start.Name}" : "")
                : doc?.Script != null ? $"Build: {doc.DisplayName} (single file)" : start != null ? $"Start: {start.Name}" : "";
            RefreshStartProjectCombo();
            RefreshEngineMenu();
        }

        /// <summary>The box of the start project in the Run tool strip: the programs of the solution, the start project selected (a single project: it alone, fixed; nothing open: empty).</summary>
        private void RefreshStartProjectCombo()
        {
            _updatingStartProjectUi = true;
            try
            {
                var items = new List<ComboBoxItem>();
                var startup = _workspace.Startup;
                if (_workspace.Solution != null)
                    foreach (var p in _workspace.Projects.Where(p => p.Project.Type == OutputType.Exe || ReferenceEquals(p, startup)))
                        items.Add(new ComboBoxItem { Content = p.Name, Tag = p, FontWeight = ReferenceEquals(p, startup) ? Avalonia.Media.FontWeight.SemiBold : Avalonia.Media.FontWeight.Normal });
                else if (startup != null) items.Add(new ComboBoxItem { Content = startup.Name, Tag = startup });
                if (items.Count == 0) items.Add(new ComboBoxItem { Content = _workspace.IsOpen ? "(no program)" : "(no solution)", Tag = null });
                cmbStartProject.ItemsSource = items;
                cmbStartProject.SelectedIndex = Math.Max(0, items.FindIndex(i => ReferenceEquals(i.Tag, startup)));
                cmbStartProject.IsEnabled = _workspace.Solution != null && items.Count > 1;
            }
            finally { _updatingStartProjectUi = false; }
        }

        private void cmbStartProject_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_updatingStartProjectUi || cmbStartProject.SelectedItem is not ComboBoxItem { Tag: LoadedProject project } || ReferenceEquals(project, _workspace.Startup)) return;
            SetStartProject(project);
        }

        /// <summary>Makes a project of the solution the start project (saved in the solution file as `startup`).</summary>
        private void SetStartProject(LoadedProject? project)
        {
            if (project == null || _workspace.Solution == null) return;
            if (project.Project.Type == OutputType.Library) { UpdateStatus($"{project.Name} is a library: it cannot be started."); return; }
            try
            {
                _workspace.SetStartup(project);
                UpdateStatus($"Start project: {project.Name} (saved in {Path.GetFileName(_workspace.Solution.FilePath)}).");
            }
            catch (Exception ex) when (ex is ProjectException or IOException or UnauthorizedAccessException) { _ = Dialogs.Message(this, ex.Message, "Start Project"); }
        }

        private void ShowSolutionExplorer()
        {
            if (!_tools.TryGetValue("solution", out var tool) || RootDock == null) return;
            try
            {
                if (_factory.IsDockablePinned(tool, RootDock)) _factory.UnpinDockable(tool);
                else if (!IsToolVisible(tool)) _factory.RestoreDockable(tool);
                // the Git panel comes out with it, as the tab beside it (docs/tools/git.md)
                if (_tools.TryGetValue("git", out var git) && _factory.IsDockablePinned(git, RootDock)) _factory.UnpinDockable(git);
                _factory.SetActiveDockable(tool);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        }

        // -----------------------------------------------------------
        // File menu: open, new, close
        // -----------------------------------------------------------

        private async void OpenWorkspace_Click(object? sender, RoutedEventArgs e)
        {
            var files = await PickFiles("Open Project or Solution", ProjectFilesType, SolutionOnlyType, ProjectOnlyType);
            if (files.Length > 0) OpenWorkspace(files[0]);
        }

        /// <summary>Opens a `.fireproj` or `.firesln` (the documents that are open stay open).</summary>
        private bool OpenWorkspace(string path)
        {
            try { _workspace.Load(path); }
            catch (Exception ex) when (ex is ProjectException or IOException)
            {
                _ = Dialogs.Message(this, ex.Message, "Open Project");
                return false;
            }
            RememberWorkspace(Path.GetFullPath(path));
            UpdateStatus($"Opened {_workspace.Name}: {_workspace.Projects.Count} project{(_workspace.Projects.Count == 1 ? "" : "s")}.");
            ShowSolutionExplorer();
            return true;
        }

        private void CloseWorkspace_Click(object? sender, RoutedEventArgs e)
        {
            _workspace.Close();
            UpdateStatus("Solution closed (the open files stay open).");
        }

        /// <summary>File > New > Solution: a solution with a project after a template; the one that is open is closed before the new one is loaded.</summary>
        private async void NewSolution_Click(object? sender, RoutedEventArgs e)
        {
            var dialog = new TemplateDialog(TemplateDialogMode.Solution, TemplateCatalog.Load());
            if (await dialog.ShowDialog<bool?>(this) != true || dialog.Template == null) return;
            try
            {
                // the dialog's location is the folder of the solution itself: `{location}/{name}.firesln`, the project in `{location}/{name}`
                string path = TemplateInstaller.CreateSolution(dialog.Template, dialog.Location, dialog.Name);
                _workspace.Close();
                _workspace.Load(path);
                RememberWorkspace(path);
            }
            catch (Exception ex) when (ex is ProjectException or IOException or UnauthorizedAccessException) { await Dialogs.Message(this, ex.Message, "New Solution"); return; }
            ShowSolutionExplorer();
            if (_workspace.Projects.FirstOrDefault() is { } project)
                foreach (var file in TemplateInstaller.ProjectFilesToOpen(dialog.Template, project.FilePath, project.Name)) OpenFile(file);
            UpdateStatus(!dialog.Template.Empty ? $"Solution {_workspace.Name} created with the project {_workspace.Name}." : $"Solution {_workspace.Name} created: add projects with a right click on the solution > New Project.");
        }

        private async void NewProject_Click(object? sender, RoutedEventArgs e) => await NewProject(addToSolution: _workspace.Solution != null, null);

        private async void AddNewProject_Click(object? sender, RoutedEventArgs e)
        {
            if (_workspace.Solution == null) { await Dialogs.Message(this, "There is no solution: create one with File > New > Solution.", "Add Project"); return; }
            await NewProject(addToSolution: true, null);
        }

        /// <summary>Asks for the name, the place and the template of a new project and makes it in a folder of its own (`{location}/{name}`): into the open solution when there is one (the location
        /// is the solution folder or the folder `folder`, e.g. a folder of the solution), else it is opened on its own.</summary>
        private async Task NewProject(bool addToSolution, string? folder)
        {
            string? location = addToSolution && _workspace.Solution != null ? folder ?? _workspace.Solution.Directory : null;
            var dialog = new TemplateDialog(TemplateDialogMode.Project, TemplateCatalog.Load(), location);
            if (await dialog.ShowDialog<bool?>(this) != true || dialog.Template == null) return;
            try
            {
                LoadedProject created;
                if (addToSolution && _workspace.Solution != null) created = _workspace.CreateProject(dialog.Template, dialog.Location, dialog.Name);
                else
                {
                    string projectPath = TemplateInstaller.CreateProject(dialog.Template, dialog.Location, dialog.Name);
                    _workspace.Load(projectPath);
                    RememberWorkspace(projectPath);
                    created = _workspace.Projects[0];
                }
                ShowSolutionExplorer();
                foreach (var file in TemplateInstaller.ProjectFilesToOpen(dialog.Template, created.FilePath, created.Name)) OpenFile(file);
                UpdateStatus($"Project {created.Name} created in {created.Directory}.");
            }
            catch (Exception ex) when (ex is ProjectException or IOException or UnauthorizedAccessException) { await Dialogs.Message(this, ex.Message, "New Project"); }
        }

        private async Task NewSolutionFolder(string parent)
        {
            string? name = await Dialogs.Input(this, "Name of the new folder:", "New Folder");
            if (string.IsNullOrWhiteSpace(name)) return;
            if (ProjectTemplates.CheckName(name.Trim()) is { } problem) { await Dialogs.Message(this, problem, "New Folder"); return; }
            try { _workspace.AddFolder(Path.Combine(parent, name.Trim())); }
            catch (Exception ex) when (ex is ProjectException or IOException or UnauthorizedAccessException) { await Dialogs.Message(this, ex.Message, "New Folder"); }
        }

        private async void AddExistingProject_Click(object? sender, RoutedEventArgs e)
        {
            if (_workspace.Solution == null) { await Dialogs.Message(this, "There is no solution: create one with File > New > Solution.", "Add Project"); return; }
            var files = await PickFiles("Add Existing Project", ProjectOnlyType);
            foreach (var file in files)
            {
                try { _workspace.AddProject(file); }
                catch (Exception ex) when (ex is ProjectException or IOException) { await Dialogs.Message(this, ex.Message, "Add Project"); }
            }
        }

        // -----------------------------------------------------------
        // Project menu
        // -----------------------------------------------------------

        private void ProjectMenu_SubmenuOpened(object? sender, RoutedEventArgs e)
        {
            var project = CommandProject();
            bool has = project != null;
            mnuAddNewFile.IsEnabled = mnuAddExistingFile.IsEnabled = mnuAddReference.IsEnabled = mnuProjectProperties.IsEnabled = mnuReloadProject.IsEnabled = has;
            mnuAddNewProject.IsEnabled = mnuAddExistingProject.IsEnabled = _workspace.Solution != null;
            mnuSetStartup.IsEnabled = has && _workspace.Solution != null && project!.Project.Type == OutputType.Exe;
            mnuPackLibrary.IsEnabled = has && project!.Project.Type == OutputType.Library;
            mnuInstallLibrary.IsEnabled = mnuPackLibrary.IsEnabled;
        }

        // the entries of the menu Project > Add New carry the kind in their Tag
        private async void AddNewFile_Click(object? sender, RoutedEventArgs e) => await AddNewFile(CommandProject(), null, (sender as MenuItem)?.Tag as string ?? "file");
        private async void AddExistingFile_Click(object? sender, RoutedEventArgs e) => await AddExistingFile(CommandProject());
        private async void AddReference_Click(object? sender, RoutedEventArgs e) => await AddReference(CommandProject());
        private void SetStartup_Click(object? sender, RoutedEventArgs e) => SetStartProject(CommandProject());
        private async void PackLibrary_Click(object? sender, RoutedEventArgs e) => await PackLibrary(CommandProject());
        private async void InstallLibrary_Click(object? sender, RoutedEventArgs e) => await InstallLibrary(CommandProject());

        /// <summary>The solution of the standard libraries (Source/Packages.firesln next to the program): edit a library, then Install it.</summary>
        private async void OpenStandardSources_Click(object? sender, RoutedEventArgs e)
        {
            string solution = Path.Combine(fire.Compiler.StandardSources.DefaultFolder, fire.Compiler.StandardSources.SolutionFile);
            if (!File.Exists(solution)) { await Dialogs.Message(this, $"There is no '{solution}'.", "Standard Libraries"); return; }
            OpenWorkspace(solution);
        }

        private async void InstallPackageFile_Click(object? sender, RoutedEventArgs e)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "Install a package file",
                AllowMultiple = false,
                FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType("Package files") { Patterns = new[] { "*.fpk" } }, Avalonia.Platform.Storage.FilePickerFileTypes.All },
            });
            if (files.Count == 0 || files[0].TryGetLocalPath() is not { } file) return;
            await InstallPackageFile(file, "Install Package");
        }

        /// <summary>Installs a package file into the store of this machine; a package of the same name and version that is installed already is replaced only when the user says so.</summary>
        private async Task<bool> InstallPackageFile(string file, string title)
        {
            fire.Package.Manager.PackageManifest manifest;
            try { manifest = fire.Package.Manager.Fpk.ReadManifest(file); }
            catch (fire.Package.Manager.PackageException ex) { await Dialogs.Message(this, ex.Message, title); return false; }
            var store = fire.Package.Manager.PackageStore.Default;
            if (store.Find(manifest.Name) is { } installed && installed.Version == manifest.Version
                && await Dialogs.Ask(this, $"{manifest.Name} {manifest.Version} is installed already.\n\nReplace it with this package?", title,
                    ("Replace", Dialogs.Answer.Yes), ("Cancel", Dialogs.Answer.No)) != Dialogs.Answer.Yes)
                return false;
            string? error = null;
            await Task.Run(() =>
            {
                try { store.ClearOverlay(); store.Install(file); }
                catch (Exception ex) when (ex is fire.Package.Manager.PackageException or IOException or UnauthorizedAccessException) { error = ex.Message; }
            });
            if (error != null) { UpdateStatus("Installing failed."); await Dialogs.Message(this, error, title); return false; }
            UpdateStatus($"Installed {manifest.Name} {manifest.Version}.");
            return true;
        }

        /// <summary>"Install": packs the library (its version as it is set, without its tests) and installs the package - the quick way after changing a library, e.g. a standard library
        /// of Source/Packages.firesln.</summary>
        private async Task InstallLibrary(LoadedProject? project)
        {
            const string title = "Install Library";
            if (project == null) return;
            if (project.Project.Type != OutputType.Library) { await Dialogs.Message(this, $"'{project.Name}' is a program: only a library is installed.", title); return; }
            if (BuildRunning()) return;
            var plan = CreatePlan(project);
            string work = Path.Combine(Path.GetTempPath(), "fire-install-" + Guid.NewGuid().ToString("N"));
            UpdateStatus($"Packing {project.Name}...");
            var (fpk, error, cancelled) = await PackAsJob(plan, work, $"{project.Name} (package)");
            try
            {
                if (cancelled) return;
                if (error != null || fpk == null) { UpdateStatus("Packing failed."); await Dialogs.Message(this, error ?? "The library could not be packed.", title); return; }
                if (await InstallPackageFile(fpk, title))
                    await Dialogs.Message(this, $"{project.Name} {ProjectBuilder.PackageVersion(plan.Settings.Version)} is installed: programs write #import \"{project.Project.ImportName}\".", title);
            }
            finally
            {
                try { Directory.Delete(work, true); } catch (IOException) { }
            }
        }
        private void ReloadProject_Click(object? sender, RoutedEventArgs e) { CommandProject()?.Refresh(); _workspace.Refresh(); }
        private async void ProjectProperties_Click(object? sender, RoutedEventArgs e) => await ShowProjectProperties(CommandProject());

        /// <summary>File > New > Script...: the list of the code templates (Script first) and a name; in the project of the active document, or - without one - as a new document.</summary>
        private void NewScriptDialog_Click(object? sender, RoutedEventArgs e) => _ = NewFileFromTemplate(CommandProject(), null, null, "Script");

        /// <summary>Makes a new file after a code template and opens it: with `fixedTemplate` only the name is asked, else the list of templates is shown (`preselect`: the title that is selected at first).
        /// The files go into `folder`, else the folder of the project; without a project a template of one file becomes a new document (unsaved).</summary>
        private async Task NewFileFromTemplate(LoadedProject? project, string? folder, FireTemplate? fixedTemplate, string? preselect)
        {
            var catalog = TemplateCatalog.Load();
            var dialog = fixedTemplate != null ? new TemplateDialog(TemplateDialogMode.Named, catalog, template: fixedTemplate) : new TemplateDialog(TemplateDialogMode.Code, catalog, preselect: preselect);
            if (await dialog.ShowDialog<bool?>(this) != true || dialog.Template == null) return;
            var template = dialog.Template;
            var values = new TemplateValues(dialog.Name, project?.Name);
            const string title = "New File";
            try
            {
                if (project != null)
                {
                    string target = folder ?? project.Directory;
                    var made = TemplateInstaller.Instantiate(template, target, values);
                    foreach (var file in made) _workspace.AddFile(project, file);
                    foreach (var file in TemplateInstaller.FilesToOpen(template, target, values, made)) OpenFile(file);
                    UpdateStatus($"{template.Title} {dialog.Name} added to {project.Name}.");
                    return;
                }
                // without a project: one unsaved document per file of the template (an FXML window: its code-behind, then the markup in front)
                var opens = template.Open.Select(o => values.Expand(o)).ToHashSet(StringComparer.Ordinal);
                foreach (var rel in template.Files.OrderBy(f => opens.Contains(values.Expand(f)) ? 1 : 0))
                {
                    string name = values.Expand(rel);
                    string text = values.Expand(File.ReadAllText(Path.Combine(template.Directory, rel.Replace('/', Path.DirectorySeparatorChar))));
                    string ext = Path.GetExtension(name).ToLowerInvariant();
                    var kind = ext switch { ".fire" or ".script" or ".fi" or ".fic" => DocumentKind.Script, ".fxml" => DocumentKind.UiMarkup, ".biscuit" => DocumentKind.Circuit, ".fblocks" => DocumentKind.Blocks, ".fmesh" => DocumentKind.Mesh, ".fdata" => DocumentKind.DataModel, ".md" or ".markdown" => DocumentKind.Markdown, _ => DocumentKind.Text };
                    CreateDocument(kind, text, null, untitledName: Path.GetFileName(name));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ProjectException) { await Dialogs.Message(this, ex.Message, title); }
        }

        /// <summary>Makes a new file in the project (in `folder`, else the project folder) and opens it. `kind`: `dialog` (the list of the code templates), `t:Title` (one code template: only the name is asked),
        /// markdown, image (asks for the size) or file (any name).</summary>
        private async Task AddNewFile(LoadedProject? project, string? folder, string kind = "file")
        {
            if (project == null) return;
            const string title = "Add New File";
            if (kind == "dialog") { await NewFileFromTemplate(project, folder, null, "Script"); return; }
            if (kind.StartsWith("t:", StringComparison.Ordinal))
            {
                var template = TemplateCatalog.Load().Find(TemplateScope.Code, kind.Substring(2));
                if (template == null) { await Dialogs.Message(this, $"The template '{kind.Substring(2)}' was not found in the Templates folder.", title); return; }
                await NewFileFromTemplate(project, folder, template, null);
                return;
            }
            string name;
            byte[]? bytes = null;   // an image is written as bytes, everything else as text
            if (kind == "image")
            {
                var dialog = new NewImageDialog();
                if (!await dialog.ShowDialog<bool>(this)) return;
                name = dialog.FileName;
                var pixels = new uint[dialog.ImageWidth * dialog.ImageHeight];
                if (dialog.Background != 0) Array.Fill(pixels, dialog.Background);
                bytes = fire.Terminal.ImageEncoder.Encode(fire.Terminal.ImageData.CreateTruecolor(dialog.ImageWidth, dialog.ImageHeight, pixels, "PNG"), Path.GetExtension(name).TrimStart('.'));
            }
            else
            {
                (string prompt, string initial, string extension) = kind == "markdown" ? ("Name of the new Markdown document:", "notes.md", ".md") : ("Name of the new file:", "newfile.fire", ".fire");
                string? input = await Dialogs.Input(this, prompt, title, initial);
                if (string.IsNullOrWhiteSpace(input)) return;
                name = input.Trim();
                if (Path.GetExtension(name).Length == 0) name += extension;
            }
            string full = Path.GetFullPath(name, folder ?? project.Directory);
            try
            {
                if (File.Exists(full)) { await Dialogs.Message(this, $"'{name}' exists already; use Add Existing File.", title); return; }
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                if (bytes != null) File.WriteAllBytes(full, bytes);
                else File.WriteAllText(full, Path.GetExtension(full).ToLowerInvariant() is ".md" or ".markdown" ? $"# {Path.GetFileNameWithoutExtension(full)}\n\n" : "");
                _workspace.AddFile(project, full);
                // a new picture opens in the pixel editor, the rest in its editor
                OpenFile(full, forceKind: bytes != null ? DocumentKind.Pixel : null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ProjectException or fire.Terminal.ImageFormatException) { await Dialogs.Message(this, ex.Message, title); }
        }


        private async Task AddExistingFile(LoadedProject? project)
        {
            if (project == null) return;
            foreach (var file in await PickFiles("Add Existing File", ScriptFiles, UiMarkupFiles, CircuitFiles))
            {
                try { _workspace.AddFile(project, file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ProjectException) { await Dialogs.Message(this, ex.Message, "Add Existing File"); }
            }
        }

        private async Task AddReference(LoadedProject? project)
        {
            if (project == null) return;
            var kind = await Dialogs.Ask(this, "Reference a library project of yours, or an installed package?", "Add Reference",
                ("Library project...", Dialogs.Answer.Yes), ("Package...", Dialogs.Answer.No), ("Cancel", Dialogs.Answer.Cancel));
            if (kind == Dialogs.Answer.Cancel) return;
            try
            {
                if (kind == Dialogs.Answer.No)
                {
                    string? name = await Dialogs.Input(this, "Name of the package (see Package Manager):", "Add Package Reference");
                    if (!string.IsNullOrWhiteSpace(name)) _workspace.AddPackageReference(project, name.Trim());
                    return;
                }
                var files = await PickFiles("Reference a library project", ProjectOnlyType);
                if (files.Length == 0) return;
                var library = _workspace.LoadProject(files[0]);
                if (library.Project.Type != OutputType.Library) { await Dialogs.Message(this, $"'{library.Name}' is a program: only a library can be referenced (Project Properties > Type).", "Add Reference"); return; }
                if (ProjectFiles.PathComparer.Equals(library.FilePath, project.FilePath)) { await Dialogs.Message(this, "A project cannot reference itself.", "Add Reference"); return; }
                _workspace.AddReference(project, library);
                UpdateStatus($"{project.Name} references {library.Name}: write #import \"{library.Project.ImportName}\" to use it.");
            }
            catch (Exception ex) when (ex is ProjectException or IOException) { await Dialogs.Message(this, ex.Message, "Add Reference"); }
        }

        private async Task ShowProjectProperties(LoadedProject? project, bool solutionTab = false, int tab = -1, string? page = null)
        {
            if (project == null) return;
            var dialog = new ProjectPropertiesDialog(_workspace, project) { EditToolchains = owner => ShowNativeBuildSettings(toolchainPage: true, owner) };
            if (solutionTab) dialog.SelectSolutionTab();
            else if (page != null) dialog.SelectPage(page);
            else if (tab >= 0) dialog.SelectTab(tab);
            if (await dialog.ShowDialog<bool?>(this) != true) return;
            try
            {
                _workspace.SaveProject(project);
                _workspace.SaveSolution();
                UpdateStatus($"Saved {project.FilePath}");
            }
            catch (Exception ex) when (ex is ProjectException or IOException or UnauthorizedAccessException) { await Dialogs.Message(this, ex.Message, "Project Properties"); }
        }

        /// <summary>Packs a library as a build job (its window after half a second, Cancel stops it): the package, or the error, or that it was cancelled.</summary>
        private async Task<(string? Package, string? Error, bool Cancelled)> PackAsJob(BuildPlan plan, string folder, string what)
        {
            var job = BeginBuildJob(what);
            string? error = null, result = null;
            bool cancelled = false;
            await Task.Run(() =>
            {
                try { result = ProjectBuilder.PackLibrary(plan, folder, job.Monitor); }
                catch (OperationCanceledException) { cancelled = true; }
                catch (Exception ex) when (ex is ProjectException or IOException or UnauthorizedAccessException or fire.Package.Manager.PackageException || CommandLineRunner.IsCompileErrorForEditor(ex)) { error = CompileErrors.Describe(ex); }
            });
            EndBuildJob(job, cancelled ? BuildOutcome.Cancelled : error != null ? BuildOutcome.Failed : BuildOutcome.Succeeded, error);
            return (result, error, cancelled);
        }

        /// <summary>Asks for the version and the metadata, checks the library and writes `name-version.fpk` (a package that ember installs and other programs `#import`). The answers are kept in the
        /// project's settings; the version can be raised afterwards for the next package.</summary>
        private async Task PackLibrary(LoadedProject? project)
        {
            if (project == null) return;
            if (project.Project.Type != OutputType.Library) { await Dialogs.Message(this, $"'{project.Name}' is a program: only a library is packed.", "Pack Library"); return; }
            var effective = ProjectSettings.Merge(project.Project.Settings, _workspace.Solution?.Settings);
            string suggested = effective.Output != null ? Path.GetFullPath(effective.Output, project.Directory) : Path.Combine(project.Directory, "bin");
            var dialog = new PackDialog(project, effective, suggested);
            if (await dialog.ShowDialog<bool?>(this) != true) return;

            var settings = project.Project.Settings;
            settings.Version = dialog.Version;
            if (dialog.Description != null) settings.Description = dialog.Description;
            if (dialog.Author != null) settings.Author = dialog.Author;
            if (dialog.License != null) settings.License = dialog.License;
            try { _workspace.SaveProject(project); }
            catch (Exception ex) when (ex is ProjectException or IOException or UnauthorizedAccessException) { await Dialogs.Message(this, ex.Message, "Pack Library"); return; }

            if (BuildRunning()) return;
            var plan = CreatePlan(project);
            UpdateStatus($"Packing {project.Name} {dialog.Version}...");
            var (result, error, cancelled) = await PackAsJob(plan, dialog.Folder, $"{project.Name} {dialog.Version} (package)");
            if (cancelled) return;
            if (error != null) { UpdateStatus("Packing failed."); await Dialogs.Message(this, error, "Pack Library"); return; }
            string next = "";
            if (dialog.BumpAfterwards)
            {
                settings.Version = ProjectBuilder.BumpVersion(dialog.Version);
                try { _workspace.SaveProject(project); next = $"\n\nThe version of the project is {settings.Version} now."; }
                catch (Exception ex) when (ex is ProjectException or IOException or UnauthorizedAccessException) { next = "\n\n(The next version could not be saved: " + ex.Message + ")"; }
            }
            UpdateStatus($"Packed {result}");
            await Dialogs.Message(this, $"{result}\n\nInstall it with the Package Manager (or `ember install`); programs then write #import \"{project.Project.ImportName}\"." + next, "Pack Library");
        }

        // -----------------------------------------------------------
        // The commands of the solution explorer
        // -----------------------------------------------------------

        private async Task OnSolutionCommand(string command, ExplorerNode? node)
        {
            if (command.StartsWith("git-", StringComparison.Ordinal)) { await OnGitExplorerCommand(command, node); return; }
            if (command.StartsWith("build-action:", StringComparison.Ordinal)) { SetBuildAction(command.Substring("build-action:".Length)); return; }
            if (command.StartsWith("folder-", StringComparison.Ordinal)) { if (node != null) OnFolderCommand(command, node); return; }
            var project = node?.Project ?? CommandProject(node);
            switch (command)
            {
                case "new-project": await NewProject(addToSolution: _workspace.Solution != null, null); break;
                case "open-workspace": OpenWorkspace_Click(this, new RoutedEventArgs()); break;
                case "add-new-project": await NewProject(addToSolution: true, node?.Kind == ExplorerKind.SolutionFolder ? node.Path : null); break;
                case "new-folder": if (_workspace.Solution != null) await NewSolutionFolder(node?.Kind == ExplorerKind.SolutionFolder && node.Path != null ? node.Path : _workspace.Solution.Directory!); break;
                case "remove-folder": if (node?.Path != null) _workspace.RemoveFolder(node.Path); break;
                case "add-existing-project": AddExistingProject_Click(this, new RoutedEventArgs()); break;
                case "close-workspace": CloseWorkspace_Click(this, new RoutedEventArgs()); break;
                case "open": if (node?.Path != null) OpenFile(node.Path); break;
                case "open-pixel": if (node?.Path != null) OpenFile(node.Path, forceKind: DocumentKind.Pixel); break;
                case "open-text": if (node?.Path != null) OpenFile(node.Path, forceKind: DocumentKind.Text); break;
                case "open-hex": if (node?.Path != null) OpenFile(node.Path, forceKind: DocumentKind.Hex); break;
                case var c when c.StartsWith("new:"): await AddNewFile(project, node?.Kind == ExplorerKind.Folder ? node.Path : null, c.Substring(4)); break;
                case "add-existing-file": await AddExistingFile(project); break;
                case "add-reference": await AddReference(project); break;
                case "set-startup": SetStartProject(project); break;
                case "pack": await PackLibrary(project); break;
                case "install-library": await InstallLibrary(project); break;
                case "refresh": project?.Refresh(); _workspace.Refresh(); break;
                case "properties": await ShowProjectProperties(project); break;
                case "show-properties":   // the Properties button of the explorer's tool bar on a file (or on nothing): the Properties panel
                    OnExplorerSelection(node);
                    RevealTool("properties");
                    break;
                case "open-notes": await OpenOverview(project); break;
                case "open-todo": TodoOpen_Click(this, new RoutedEventArgs()); break;
                case "solution-properties": await ShowProjectProperties(_workspace.Projects.FirstOrDefault(), solutionTab: true); break;
                case "run-project": await RunProject(project); break;
                case "build-project": await BuildProjectNative(project); break;
                case "clean-project": await CleanProjects(project != null ? new[] { project } : Array.Empty<LoadedProject>(), project?.Name ?? ""); break;
                case "build-solution": await BuildSolutionAsync(); break;
                case "clean-solution": CleanSolution_Click(this, new RoutedEventArgs()); break;
                case "publish-project": await PublishProject(project); break;
                case "publish-solution": await PublishSolutionAsync(); break;
                case "reveal": Reveal(node); break;
                case "remove": await RemoveNode(node); break;
                case "add-resource": await AddResource(project, node); break;
                case "new-project-folder": await NewProjectFolder(project, node); break;
                case "add-native": await AddNative(project, node); break;
                case "delete-file": await DeleteContentFile(node); break;
            }
        }

        /// <summary>The folder a command of a node concerns: the folder itself, the project folder for a project (or a file: the folder it is in).</summary>
        private static string? FolderOf(ExplorerNode? node, LoadedProject project) => node?.Kind switch
        {
            ExplorerKind.Folder => node.Path,
            ExplorerKind.File or ExplorerKind.Content => node.Path != null ? Path.GetDirectoryName(node.Path) : null,
            _ => project.Directory,
        };

        /// <summary>Copies files into the project (resources: nothing is compiled from them until the code says `new Resource("...")`): into the folder that was clicked, for the project itself into a
        /// subfolder to be named (proposed: resources).</summary>
        private async Task AddResource(LoadedProject? project, ExplorerNode? node)
        {
            if (project == null) return;
            string folder = FolderOf(node, project) ?? project.Directory;
            if (node?.Kind == ExplorerKind.Project)
            {
                string? sub = await Dialogs.Input(this, "Copy the files into this folder of the project (empty: the project folder):", "Add Resource", "resources");
                if (sub == null) return;
                sub = sub.Trim();
                folder = sub.Length == 0 ? project.Directory : Path.GetFullPath(sub, project.Directory);
                if (ProjectFiles.Relative(project.Directory, folder).StartsWith("..")) { await Dialogs.Message(this, "The folder has to be inside the project.", "Add Resource"); return; }
            }
            var files = await PickFiles("Add Resource (the files are copied into " + ProjectFiles.Relative(project.Directory, folder) + ")", AllFilesType);
            int copied = 0;
            foreach (var file in files)
            {
                try { _workspace.AddContentFile(project, file, folder); copied++; }
                catch (Exception ex) when (ex is ProjectException or IOException or UnauthorizedAccessException) { await Dialogs.Message(this, ex.Message, "Add Resource"); }
            }
            if (copied > 0) UpdateStatus($"{copied} file{(copied == 1 ? "" : "s")} copied into {ProjectFiles.Relative(project.Directory, folder)}: use new Resource(\"{ProjectFiles.Relative(project.Directory, Path.Combine(folder, Path.GetFileName(files[0])))}\") in the code.");
        }

        private async Task NewProjectFolder(LoadedProject? project, ExplorerNode? node)
        {
            if (project == null) return;
            string? name = await Dialogs.Input(this, "Name of the new folder:", "New Folder");
            if (string.IsNullOrWhiteSpace(name)) return;
            if (ProjectTemplates.CheckName(name.Trim()) is { } problem) { await Dialogs.Message(this, problem, "New Folder"); return; }
            try
            {
                Directory.CreateDirectory(Path.Combine(FolderOf(node, project) ?? project.Directory, name.Trim()));
                project.Refresh();
                _workspace.Refresh();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await Dialogs.Message(this, ex.Message, "New Folder"); }
        }

        /// <summary>Gives the project C++ natives (the folder `native/` with a header that has one example function) - or, when it has them, adds another C++ file to the folder.</summary>
        private async Task AddNative(LoadedProject? project, ExplorerNode? node)
        {
            if (project == null) return;
            try
            {
                if (project.Project.Native == null)
                {
                    string header = _workspace.AddNative(project);
                    OpenFile(header);
                    UpdateStatus("The project has natives now: every `inline Value name(Value a, ...)` in native/ is a function `__name` that fire code can call.");
                    return;
                }
                string? name = await Dialogs.Input(this, "Name of the new C++ file:", "Add C++ File", "more.hpp");
                if (string.IsNullOrWhiteSpace(name)) return;
                name = name.Trim();
                if (Path.GetExtension(name).Length == 0) name += ".hpp";
                string folder = node?.Kind == ExplorerKind.Folder && node.Path != null ? node.Path : Path.Combine(project.Directory, "native");
                string full = Path.GetFullPath(name, folder);
                if (File.Exists(full)) { await Dialogs.Message(this, $"'{name}' exists already.", "Add C++ File"); return; }
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, "#pragma once\n#include <cstdint>\n\nnamespace fire {\n\n}  // namespace fire\n");
                project.Refresh();
                _workspace.Refresh();
                OpenFile(full);
            }
            catch (Exception ex) when (ex is ProjectException or IOException or UnauthorizedAccessException) { await Dialogs.Message(this, ex.Message, "Native Code"); }
        }

        private async Task DeleteContentFile(ExplorerNode? node)
        {
            if (node?.Path == null || node.Project == null) return;
            if (await Dialogs.Ask(this, $"Delete '{Path.GetFileName(node.Path)}' from the disk?", "Delete File", ("Delete", Dialogs.Answer.Yes), ("Cancel", Dialogs.Answer.Cancel)) != Dialogs.Answer.Yes) return;
            try
            {
                if (_documents.FirstOrDefault(d => d.View.FilePath != null && ProjectFiles.PathComparer.Equals(Path.GetFullPath(d.View.FilePath), Path.GetFullPath(node.Path))) is { } open) _factory.CloseDockable(open.Layout);
                File.Delete(node.Path);
                node.Project.Refresh();
                _workspace.Refresh();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await Dialogs.Message(this, ex.Message, "Delete File"); }
        }

        /// <summary>Start Debugging of a project in the explorer: compiles that project again and starts it under the debugger, whichever project is the start project (the start project
        /// stays). A library cannot run: the start project runs instead.</summary>
        private async Task RunProject(LoadedProject? project)
        {
            if (project == null) return;
            if (project.Project.Type == OutputType.Library)
            {
                if (_workspace.Startup is { Project.Type: OutputType.Exe } start) project = start;
                else { await Dialogs.Message(this, $"{project.Name} is a library: it cannot be started (Build packs it).", "Start Debugging"); return; }
            }
            _projectOverride = project;
            try { Restart_Click(this, new RoutedEventArgs()); }   // (it takes the project before its first await)
            finally { _projectOverride = null; }
        }

        private async Task RemoveNode(ExplorerNode? node)
        {
            if (node == null) return;
            try
            {
                switch (node.Kind)
                {
                    case ExplorerKind.File when node.Project != null && node.Path != null:
                        _workspace.RemoveFile(node.Project, node.Path);
                        break;
                    case ExplorerKind.Reference when node.Project != null && node.Reference != null:
                        _workspace.RemoveReference(node.Project, node.Reference);
                        break;
                    case ExplorerKind.Project when node.Project != null && _workspace.Solution != null:
                        if (await Dialogs.Ask(this, $"Remove the project '{node.Project.Name}' from the solution? Its files stay where they are.", "Remove Project", ("Remove", Dialogs.Answer.Yes), ("Cancel", Dialogs.Answer.Cancel)) == Dialogs.Answer.Yes)
                            _workspace.RemoveProject(node.Project);
                        break;
                }
            }
            catch (Exception ex) when (ex is ProjectException or IOException or UnauthorizedAccessException) { await Dialogs.Message(this, ex.Message, "Remove"); }
        }

        private static void Reveal(ExplorerNode? node)
        {
            string? target = node?.Kind switch
            {
                ExplorerKind.File => node.Path != null ? Path.GetDirectoryName(node.Path) : null,
                ExplorerKind.Folder => node.Path,
                ExplorerKind.Project => node.Project?.Directory,
                ExplorerKind.Solution => node.Path != null ? Path.GetDirectoryName(node.Path) : null,
                ExplorerKind.SolutionFolder => node.Path,
                _ => null,
            };
            if (target == null || !Directory.Exists(target)) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true }); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        }
    }
}
