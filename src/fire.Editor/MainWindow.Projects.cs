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
        private static readonly FilePickerFileType AllFilesType = new("All files") { Patterns = new[] { "*" } };
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

        private readonly Dictionary<string, (DateTime Stamp, string Text)> _fileTexts = new(ProjectFiles.PathComparer);

        /// <summary>The text of a file of the project: the open buffer (also an unsaved one), else the file (read again when it changed).</summary>
        private string? TextOfFile(string path)
        {
            if (OpenText(path) is { } open) return open;
            try
            {
                var stamp = File.GetLastWriteTimeUtc(path);
                if (_fileTexts.TryGetValue(path, out var known) && known.Stamp == stamp) return known.Text;
                string text = File.ReadAllText(path);
                _fileTexts[path] = (stamp, text);
                return text;
            }
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
            RefreshGit();
            ScriptEditorControl.NoteProjectChanged();
            fire.Package.Manager.PackageStore.Default.ClearOverlay();   // the natives of the projects are put back by the next build or analysis
            // what belongs to which project (and the libraries) may have changed: check the open scripts again
            foreach (var doc in _documents) { doc.Script?.InvalidateConditionalSymbols(); doc.Script?.Revalidate(); }
        }

        /// <summary>The explorer and the status bar follow the workspace and the active document.</summary>
        private void RefreshProjectUi()
        {
            var doc = ActiveDocument;
            _solutionPanel.Refresh(_workspace, FullPathOf(doc), _gitRoot == null ? null : _gitStates, _gitBranch);
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
            var dialog = new NewProjectDialog(solution: true);
            if (await dialog.ShowDialog<bool?>(this) != true || dialog.Template == null) return;
            try
            {
                // the dialog's location is the folder of the solution itself: `{location}/{name}.firesln`, the project in `{location}/{name}`
                _workspace.Load(ProjectTemplates.CreateSolution(dialog.Template, dialog.Location, dialog.Name));
            }
            catch (Exception ex) when (ex is ProjectException or IOException or UnauthorizedAccessException) { await Dialogs.Message(this, ex.Message, "New Solution"); return; }
            ShowSolutionExplorer();
            if (_workspace.Projects.FirstOrDefault()?.Files.FirstOrDefault() is { } first) OpenFile(first);
            UpdateStatus(dialog.Template.MakesProject ? $"Solution {_workspace.Name} created with the project {_workspace.Name}." : $"Solution {_workspace.Name} created: add projects with Project > Add New Project.");
        }

        private async void NewProject_Click(object? sender, RoutedEventArgs e) => await NewProject(addToSolution: _workspace.Solution != null, null);

        private async void AddNewProject_Click(object? sender, RoutedEventArgs e)
        {
            if (_workspace.Solution == null) { await Dialogs.Message(this, "There is no solution: create one with File > New Solution.", "Add Project"); return; }
            await NewProject(addToSolution: true, null);
        }

        /// <summary>Asks for the name, the place and the template of a new project and makes it in a folder of its own (`{location}/{name}`): into the open solution when there is one (the location
        /// is the solution folder or the folder `folder`, e.g. a folder of the solution), else it is opened on its own.</summary>
        private async Task NewProject(bool addToSolution, string? folder)
        {
            string? location = addToSolution && _workspace.Solution != null ? folder ?? _workspace.Solution.Directory : null;
            var dialog = new NewProjectDialog(solution: false, location);
            if (await dialog.ShowDialog<bool?>(this) != true || dialog.Template == null) return;
            try
            {
                LoadedProject created;
                if (addToSolution && _workspace.Solution != null) created = _workspace.CreateProject(dialog.Template, dialog.Location, dialog.Name);
                else
                {
                    _workspace.Load(ProjectTemplates.CreateProject(dialog.Template, dialog.Location, dialog.Name));
                    created = _workspace.Projects[0];
                }
                ShowSolutionExplorer();
                if (created.Files.FirstOrDefault() is { } first) OpenFile(first);
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

        // the entries of the menu Project > Add New carry the kind in their Tag
        private async void AddNewFile_Click(object? sender, RoutedEventArgs e) => await AddNewFile(CommandProject(), null, (sender as MenuItem)?.Tag as string ?? "file");
        private async void AddExistingFile_Click(object? sender, RoutedEventArgs e) => await AddExistingFile(CommandProject());
        private async void AddReference_Click(object? sender, RoutedEventArgs e) => await AddReference(CommandProject());
        private void SetStartup_Click(object? sender, RoutedEventArgs e) { if (CommandProject() is { } p) _workspace.SetStartup(p); }
        private async void PackLibrary_Click(object? sender, RoutedEventArgs e) => await PackLibrary(CommandProject());
        private void ReloadProject_Click(object? sender, RoutedEventArgs e) { CommandProject()?.Refresh(); _workspace.Refresh(); }
        private async void ProjectProperties_Click(object? sender, RoutedEventArgs e) => await ShowProjectProperties(CommandProject());

        /// <summary>Makes a new file in the project (in `folder`, else the project folder) and opens it. `kind`: script, fxml, markdown, image (asks for the size) or file (any name).</summary>
        private async Task AddNewFile(LoadedProject? project, string? folder, string kind = "file")
        {
            if (project == null) return;
            const string title = "Add New File";
            string name;
            byte[]? bytes = null;   // an image is written as bytes, everything else as text
            switch (kind)
            {
                case "image":
                    var dialog = new NewImageDialog();
                    if (!await dialog.ShowDialog<bool>(this)) return;
                    name = dialog.FileName;
                    var pixels = new uint[dialog.ImageWidth * dialog.ImageHeight];
                    if (dialog.Background != 0) Array.Fill(pixels, dialog.Background);
                    bytes = fire.Terminal.ImageEncoder.Encode(fire.Terminal.ImageData.CreateTruecolor(dialog.ImageWidth, dialog.ImageHeight, pixels, "PNG"), Path.GetExtension(name).TrimStart('.'));
                    break;
                default:
                    (string prompt, string initial, string extension) = kind switch
                    {
                        "script" => ("Name of the new script:", "newfile.script", ".script"),
                        "fxml" => ("Name of the new user interface:", "newwindow.fxml", ".fxml"),
                        "markdown" => ("Name of the new Markdown document:", "notes.md", ".md"),
                        _ => ("Name of the new file:", "newfile.script", ".script"),
                    };
                    string? input = await Dialogs.Input(this, prompt, title, initial);
                    if (string.IsNullOrWhiteSpace(input)) return;
                    name = input.Trim();
                    // a kind fixes the extension (a typed one of another kind is kept as typed: "Other File" is the way to name anything)
                    if (Path.GetExtension(name).Length == 0) name += extension;
                    break;
            }
            string full = Path.GetFullPath(name, folder ?? project.Directory);
            try
            {
                if (File.Exists(full)) { await Dialogs.Message(this, $"'{name}' exists already; use Add Existing File.", title); return; }
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                if (bytes != null) File.WriteAllBytes(full, bytes);
                else
                {
                    string stem = Path.GetFileNameWithoutExtension(full);
                    string ext = Path.GetExtension(full).ToLowerInvariant();
                    File.WriteAllText(full, FireProject.IsMarkupFile(full) ? NewMarkupText(stem) : ext is ".md" or ".markdown" ? $"# {stem}\n\n" : "");
                }
                _workspace.AddFile(project, full);
                // a new picture opens in the pixel editor, the rest in its editor
                OpenFile(full, forceKind: bytes != null ? DocumentKind.Pixel : null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ProjectException or fire.Terminal.ImageFormatException) { await Dialogs.Message(this, ex.Message, title); }
        }

        private static string NewMarkupText(string fileName)
        {
            var chars = fileName.Select(c => char.IsLetterOrDigit(c) && c < 128 || c == '_' ? c : '_').ToArray();
            string name = new string(chars);
            if (name.Length == 0 || !char.IsLetter(name[0])) name = "W" + name;
            name = char.ToUpperInvariant(name[0]) + name.Substring(1);
            return UiMarkupTemplate.Replace("class=\"MainWindow\"", $"class=\"{name}\"").Replace("title=\"My window\"", $"title=\"{name}\"");
        }

        private async Task AddExistingFile(LoadedProject? project)
        {
            if (project == null) return;
            foreach (var file in await PickFiles("Add Existing File", ScriptFiles, UiMarkupFiles))
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

            var plan = CreatePlan(project);
            UpdateStatus($"Packing {project.Name} {dialog.Version}...");
            string? error = null, result = null;
            await Task.Run(() =>
            {
                try { result = ProjectBuilder.PackLibrary(plan, dialog.Folder); }
                catch (Exception ex) when (ex is ProjectException or IOException or UnauthorizedAccessException or fire.Package.Manager.PackageException || CommandLineRunner.IsCompileErrorForEditor(ex)) { error = CompileErrors.Describe(ex); }
            });
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
                case "set-startup": if (project != null) _workspace.SetStartup(project); break;
                case "pack": await PackLibrary(project); break;
                case "refresh": project?.Refresh(); _workspace.Refresh(); break;
                case "properties": await ShowProjectProperties(project); break;
                case "solution-properties": await ShowProjectProperties(_workspace.Projects.FirstOrDefault(), solutionTab: true); break;
                case "run-project": await RunProject(project); break;
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
                ExplorerKind.SolutionFolder => node.Path,
                _ => null,
            };
            if (target == null || !Directory.Exists(target)) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true }); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        }
    }
}
