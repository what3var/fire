using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using fire.Compiler;
using fire.Projects;

namespace fire.Editor
{
    // Projects and solutions (docs/PROJECTS.md): the workspace, the solution explorer, the Project menu, what Start and Build act on.
    public partial class MainWindow
    {
        private static readonly FilePickerFileType ProjectFilesType = new("fire projects and solutions") { Patterns = new[] { "*" + FireProject.Extension, "*" + FireSolution.Extension } };
        private static readonly FilePickerFileType ProjectOnlyType = new("fire projects") { Patterns = new[] { "*" + FireProject.Extension } };
        private static readonly FilePickerFileType SolutionOnlyType = new("fire solutions") { Patterns = new[] { "*" + FireSolution.Extension } };

        /// <summary>What is open of solutions and projects. Single files are always possible next to it: only the active document decides what Start and Build act on.</summary>
        private readonly Workspace _workspace = new();

        // a command of the solution explorer ("Build / Run this project") acts on this project once, whatever the active document is
        private LoadedProject? _projectOverride;

        private void InitProjects()
        {
            _workspace.Changed += () => Dispatcher.UIThread.Post(OnWorkspaceChanged);
            _solutionPanel.FileOpenRequested += path => OpenFile(path);
            _solutionPanel.CommandRequested += (command, node) => _ = OnSolutionCommand(command, node);
            mnuCloseWorkspace.IsEnabled = false;
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

        /// <summary>The live diagnostics of a document: in a project the other files and the imported libraries are known, else the file alone as always.</summary>
        private List<Diagnostic> AnalyzeDocument(ScriptEditorControl script, string source)
        {
            if (_workspace.IsOpen && script.FilePath != null && _workspace.FindProjectOf(script.FilePath) is { } project)
            {
                try
                {
                    var plan = CreatePlan(project);
                    string self = Path.GetFullPath(script.FilePath);
                    var others = plan.Sources.Where(f => !ProjectFiles.PathComparer.Equals(Path.GetFullPath(f.Path), self)).Select(f => f.Text).ToList();
                    return LiveDiagnostics.AnalyzeInProject(source, others, script.BaseDirectory, plan);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ProjectException) { /* the files cannot be read right now: the file alone */ }
            }
            return LiveDiagnostics.Analyze(source, script.BaseDirectory);
        }

        private void AttachProjectSupport(ScriptEditorControl script)
        {
            script.DiagnosticsProvider = source => AnalyzeDocument(script, source);
            script.ExtraDefines = () => ProjectDefinesOf(script);
        }

        // -----------------------------------------------------------
        // Showing it
        // -----------------------------------------------------------

        private void OnWorkspaceChanged()
        {
            mnuCloseWorkspace.IsEnabled = _workspace.IsOpen;
            RefreshProjectUi();
            // what belongs to which project (and the libraries) may have changed: check the open scripts again
            foreach (var doc in _documents) { doc.Script?.InvalidateConditionalSymbols(); doc.Script?.Revalidate(); }
        }

        /// <summary>The explorer and the status bar follow the workspace and the active document.</summary>
        private void RefreshProjectUi()
        {
            var doc = ActiveDocument;
            _solutionPanel.Refresh(_workspace, FullPathOf(doc));
            var project = ContextProject();
            ContextText.Text = project != null
                ? $"Build: {project.Name} ({(project.Project.Type == OutputType.Library ? "library" : "project")})"
                : doc?.Script != null ? $"Build: {doc.DisplayName} (single file)" : "";
        }

        private void ShowSolutionExplorer()
        {
            if (!_tools.TryGetValue("solution", out var tool) || RootDock == null) return;
            try
            {
                if (_factory.IsDockablePinned(tool, RootDock)) _factory.UnpinDockable(tool);
                else if (!IsToolVisible(tool)) _factory.RestoreDockable(tool);
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
            UpdateStatus($"Opened {_workspace.Name}: {_workspace.Projects.Count} project{(_workspace.Projects.Count == 1 ? "" : "s")}.");
            ShowSolutionExplorer();
            return true;
        }

        private void CloseWorkspace_Click(object? sender, RoutedEventArgs e)
        {
            _workspace.Close();
            UpdateStatus("Solution closed (the open files stay open).");
        }

        private async void NewSolution_Click(object? sender, RoutedEventArgs e)
        {
            string? path = await PickSavePath("New Solution", "MySolution", "firesln", SolutionOnlyType);
            if (path == null) return;
            try
            {
                new FireSolution { Name = Path.GetFileNameWithoutExtension(path) }.Save(path);
                _workspace.Load(path);
            }
            catch (Exception ex) when (ex is ProjectException or IOException or UnauthorizedAccessException) { await Dialogs.Message(this, ex.Message, "New Solution"); return; }
            UpdateStatus($"Solution {_workspace.Name} created: add projects with Project > Add New Project.");
            ShowSolutionExplorer();
        }

        private async void NewProject_Click(object? sender, RoutedEventArgs e) => await NewProject(addToSolution: _workspace.Solution != null);

        private async void AddNewProject_Click(object? sender, RoutedEventArgs e)
        {
            if (_workspace.Solution == null) { await Dialogs.Message(this, "There is no solution: create one with File > New Solution.", "Add Project"); return; }
            await NewProject(addToSolution: true);
        }

        /// <summary>Asks for the kind and the place of a new project and makes it (into the open solution when there is one, else it is opened on its own).</summary>
        private async Task NewProject(bool addToSolution)
        {
            var kind = await Dialogs.Ask(this, "What kind of project?\n\nA program runs; a library has no entry point and is used by other projects (Add Reference) or packed as a package.", "New Project",
                ("Program", Dialogs.Answer.Yes), ("Library", Dialogs.Answer.No), ("Cancel", Dialogs.Answer.Cancel));
            if (kind == Dialogs.Answer.Cancel) return;
            string? path = await PickSavePath("New Project (choose the folder and the name)", kind == Dialogs.Answer.Yes ? "MyProgram" : "MyLibrary", "fireproj", ProjectOnlyType);
            if (path == null) return;
            try
            {
                var type = kind == Dialogs.Answer.Yes ? OutputType.Exe : OutputType.Library;
                LoadedProject created;
                if (addToSolution && _workspace.Solution != null) created = _workspace.CreateProject(path, type);
                else
                {
                    var project = new FireProject { Name = Path.GetFileNameWithoutExtension(path), Type = type };
                    string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
                    Directory.CreateDirectory(dir);
                    string main = Path.Combine(dir, type == OutputType.Library ? project.ImportName.ToLowerInvariant() + ".script" : "main.script");
                    if (!File.Exists(main))
                        File.WriteAllText(main, type == OutputType.Library
                            ? $"// The library {project.Name}: classes and functions, no statements at the top level.\nclass Greeter {{\n    static string Hello(string name) {{ return \"Hello, \" + name }}\n}}\n"
                            : $"print(\"Hello from {project.Name}\")\n");
                    project.Save(path);
                    _workspace.Load(path);
                    created = _workspace.Projects[0];
                }
                ShowSolutionExplorer();
                if (created.Files.FirstOrDefault() is { } first) OpenFile(first);
                UpdateStatus($"Project {created.Name} created.");
            }
            catch (Exception ex) when (ex is ProjectException or IOException or UnauthorizedAccessException) { await Dialogs.Message(this, ex.Message, "New Project"); }
        }

        private async void AddExistingProject_Click(object? sender, RoutedEventArgs e)
        {
            if (_workspace.Solution == null) { await Dialogs.Message(this, "There is no solution: create one with File > New Solution.", "Add Project"); return; }
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
        }

        private async void AddNewFile_Click(object? sender, RoutedEventArgs e) => await AddNewFile(CommandProject(), null);
        private async void AddExistingFile_Click(object? sender, RoutedEventArgs e) => await AddExistingFile(CommandProject());
        private async void AddReference_Click(object? sender, RoutedEventArgs e) => await AddReference(CommandProject());
        private void SetStartup_Click(object? sender, RoutedEventArgs e) { if (CommandProject() is { } p) _workspace.SetStartup(p); }
        private async void PackLibrary_Click(object? sender, RoutedEventArgs e) => await PackLibrary(CommandProject());
        private void ReloadProject_Click(object? sender, RoutedEventArgs e) { CommandProject()?.Refresh(); _workspace.Refresh(); }
        private async void ProjectProperties_Click(object? sender, RoutedEventArgs e) => await ShowProjectProperties(CommandProject());

        private async Task AddNewFile(LoadedProject? project, string? folder)
        {
            if (project == null) return;
            string? name = await Dialogs.Input(this, "Name of the new file:", "Add New File", "newfile.script");
            if (string.IsNullOrWhiteSpace(name)) return;
            name = name.Trim();
            if (Path.GetExtension(name).Length == 0) name += ".script";
            string full = Path.GetFullPath(name, folder ?? project.Directory);
            try
            {
                if (File.Exists(full)) { await Dialogs.Message(this, $"'{name}' exists already; use Add Existing File.", "Add New File"); return; }
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, "");
                _workspace.AddFile(project, full);
                OpenFile(full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ProjectException) { await Dialogs.Message(this, ex.Message, "Add New File"); }
        }

        private async Task AddExistingFile(LoadedProject? project)
        {
            if (project == null) return;
            foreach (var file in await PickFiles("Add Existing File", ScriptFiles))
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

        private async Task ShowProjectProperties(LoadedProject? project, bool solutionTab = false, int tab = -1)
        {
            if (project == null) return;
            var dialog = new ProjectPropertiesDialog(_workspace, project);
            if (solutionTab) dialog.SelectSolutionTab();
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

        /// <summary>Checks the library and writes `name-version.fpk` (a package that ember installs and other programs `#import`).</summary>
        private async Task PackLibrary(LoadedProject? project)
        {
            if (project == null) return;
            if (project.Project.Type != OutputType.Library) { await Dialogs.Message(this, $"'{project.Name}' is a program: only a library is packed.", "Pack Library"); return; }
            var plan = CreatePlan(project);
            string suggested = Path.Combine(project.Directory, "bin");
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = $"Folder for the package of {project.Name}", SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(Directory.Exists(suggested) ? suggested : project.Directory) });
            if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } folder) return;
            UpdateStatus($"Packing {project.Name}...");
            string? error = null, result = null;
            await Task.Run(() =>
            {
                try { result = ProjectBuilder.PackLibrary(plan, folder); }
                catch (Exception ex) when (ex is ProjectException or IOException or UnauthorizedAccessException or fire.Package.Manager.PackageException || CommandLineRunner.IsCompileErrorForEditor(ex)) { error = CompileErrors.Describe(ex); }
            });
            if (error != null) { UpdateStatus("Packing failed."); await Dialogs.Message(this, error, "Pack Library"); return; }
            UpdateStatus($"Packed {result}");
            await Dialogs.Message(this, $"{result}\n\nInstall it with the Package Manager (or `ember install`); programs then write #import \"{project.Project.ImportName}\".", "Pack Library");
        }

        // -----------------------------------------------------------
        // The commands of the solution explorer
        // -----------------------------------------------------------

        private async Task OnSolutionCommand(string command, ExplorerNode? node)
        {
            var project = node?.Project ?? CommandProject(node);
            switch (command)
            {
                case "new-project": await NewProject(addToSolution: false); break;
                case "open-workspace": OpenWorkspace_Click(this, new RoutedEventArgs()); break;
                case "add-new-project": await NewProject(addToSolution: true); break;
                case "add-existing-project": AddExistingProject_Click(this, new RoutedEventArgs()); break;
                case "close-workspace": CloseWorkspace_Click(this, new RoutedEventArgs()); break;
                case "open": if (node?.Path != null) OpenFile(node.Path); break;
                case "add-new-file": await AddNewFile(project, node?.Kind == ExplorerKind.Folder ? node.Path : null); break;
                case "add-existing-file": await AddExistingFile(project); break;
                case "add-reference": await AddReference(project); break;
                case "set-startup": if (project != null) _workspace.SetStartup(project); break;
                case "pack": await PackLibrary(project); break;
                case "refresh": project?.Refresh(); _workspace.Refresh(); break;
                case "properties": await ShowProjectProperties(project); break;
                case "solution-properties": await ShowProjectProperties(_workspace.Projects.FirstOrDefault(), solutionTab: true); break;
                case "run-project": await RunProject(project); break;
                case "reveal": Reveal(node); break;
                case "remove": await RemoveNode(node); break;
            }
        }

        private async Task RunProject(LoadedProject? project)
        {
            if (project == null) return;
            _projectOverride = project;
            try
            {
                if (project.Project.Type == OutputType.Library) { await PackLibrary(project); return; }
                Restart_Click(this, new RoutedEventArgs());
            }
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
                _ => null,
            };
            if (target == null || !Directory.Exists(target)) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true }); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        }
    }
}
