using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using fire.Projects;

namespace fire.Editor
{
    /// <summary>The file a set of file properties is about: its path and the project it belongs to (null: none).</summary>
    public sealed record FilePropertiesContext(string Path, LoadedProject? Project);

    /// <summary>Some properties of a file (one category of rows) and how to change them.</summary>
    public sealed class FilePropertyGroup
    {
        public IReadOnlyList<PropertyEntry> Entries { get; init; } = Array.Empty<PropertyEntry>();
        /// <summary>A problem to show under the title (e.g. a wrong build order).</summary>
        public string? Error { get; init; }
        /// <summary>Changes a row of this group (name, new text): null when it worked - the project file is then saved - or the problem (nothing was changed).</summary>
        public Func<string, string, string?>? Set { get; init; }
    }

    /// <summary>Shows properties for files of some kind in the property window when the file is selected in the solution explorer. Register more in
    /// <see cref="FilePropertiesHost.Providers"/>; each returns its rows for a file (null: none for this file).</summary>
    public interface IFilePropertiesProvider
    {
        FilePropertyGroup? Describe(FilePropertiesContext file);
    }

    /// <summary>The property window for the file selected in the solution explorer: the rows of every provider that knows the file. A change that a provider accepts is
    /// written to the project file (<see cref="SaveProject"/>), and the rows are read again.</summary>
    public sealed class FilePropertiesHost : IPropertyHost
    {
        /// <summary>The providers, in the order their categories are shown.</summary>
        public static readonly List<IFilePropertiesProvider> Providers = new() { new GeneralFileProperties(), new NativeBuildFileProperties() };

        private FilePropertiesContext? _file;
        private PropertySet? _set;
        private string? _lastError;

        /// <summary>Writes the project file after a change (the main window: Workspace.SaveProject).</summary>
        public Action<LoadedProject>? SaveProject { get; init; }
        /// <summary>After Enter in a field: where the focus goes (the solution explorer).</summary>
        public Action? Focus { get; init; }

        public bool HasFile => _file != null;
        /// <summary>The file shown (null: none).</summary>
        public FilePropertiesContext? Context => _file;
        public PropertySet? Properties => _set;
        public event Action<bool>? PropertiesChanged;
        public void FocusEditor() => Focus?.Invoke();

        /// <summary>Shows a file (null: none).</summary>
        public void Show(FilePropertiesContext? file)
        {
            _file = file;
            _lastError = null;
            Rebuild();
        }

        /// <summary>Reads the rows again (the project changed).</summary>
        public void Refresh()
        {
            if (_file != null && !File.Exists(_file.Path)) _file = null;
            Rebuild();
        }

        private void Rebuild()
        {
            if (_file == null) { _set = null; PropertiesChanged?.Invoke(false); return; }
            var groups = new List<FilePropertyGroup>();
            foreach (var provider in Providers)
            {
                try { if (provider.Describe(_file) is { } g) groups.Add(g); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ProjectException) { groups.Add(new FilePropertyGroup { Error = ex.Message }); }
            }
            var owner = new Dictionary<string, FilePropertyGroup>();
            foreach (var g in groups) foreach (var e in g.Entries) owner.TryAdd(e.Name, g);
            var errors = groups.Select(g => g.Error).Where(e => !string.IsNullOrEmpty(e)).ToList();
            if (_lastError != null) errors.Insert(0, _lastError);
            var file = _file;
            _set = new PropertySet
            {
                Title = Path.GetFileName(file.Path),
                Error = errors.Count > 0 ? string.Join("\n", errors) : null,
                Entries = groups.SelectMany(g => g.Entries).ToList(),
                Set = (name, value) =>
                {
                    if (!ReferenceEquals(file, _file) || !owner.TryGetValue(name, out var group) || group.Set == null) return;
                    string? problem;
                    try { problem = group.Set(name, value); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ProjectException) { problem = ex.Message; }
                    if (problem == null && file.Project != null)
                    {
                        try { SaveProject?.Invoke(file.Project); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ProjectException) { problem = ex.Message; }
                    }
                    _lastError = problem;
                    // (posted: the field that committed is still in its event)
                    Avalonia.Threading.Dispatcher.UIThread.Post(Rebuild);
                },
            };
            PropertiesChanged?.Invoke(false);
        }
    }

    /// <summary>What every file has: its name, folder, project and what the project does with it.</summary>
    public sealed class GeneralFileProperties : IFilePropertiesProvider
    {
        public FilePropertyGroup? Describe(FilePropertiesContext file)
        {
            const string category = "File";
            var project = file.Project;
            string full = Path.GetFullPath(file.Path);
            string folder = project != null ? ProjectFiles.Relative(project.Directory, Path.GetDirectoryName(full)!) : Path.GetDirectoryName(full) ?? "";
            string kind;
            if (project != null && project.Contains(full))
            {
                int at = project.Files.ToList().FindIndex(f => ProjectFiles.PathComparer.Equals(f, full)) + 1;
                kind = $"source, compiled as {at} of {project.Files.Count}" + (project.Project.Type == OutputType.Exe && at == project.Files.Count ? " (the entry file)" : "");
            }
            else if (ProjectNativeFiles.IsNativeFile(full)) kind = "C/C++ source";
            else kind = project != null && project.TestFiles.Contains(full, ProjectFiles.PathComparer) ? "test (compiled by forge test)" : "content";
            var entries = new List<PropertyEntry>
            {
                new("Name", PropertyEditKind.Text, Path.GetFileName(full)) { IsReadOnly = true, Category = category, Tip = full },
                new("Folder", PropertyEditKind.Text, folder == "." ? "(project folder)" : folder) { IsReadOnly = true, Category = category, Tip = "The folder, relative to the project file." },
                new("Kind", PropertyEditKind.Text, kind) { IsReadOnly = true, Category = category, Tip = "What the project does with the file (the order of the sources: 'files' in the project file)." },
            };
            if (project != null) entries.Insert(2, new("Project", PropertyEditKind.Text, project.Name) { IsReadOnly = true, Category = category, Tip = project.FilePath });
            if (File.Exists(full)) entries.Add(new("Size", PropertyEditKind.Text, new FileInfo(full).Length.ToString("N0", CultureInfo.InvariantCulture) + " bytes") { IsReadOnly = true, Category = category });
            return new FilePropertyGroup { Entries = entries };
        }
    }

    /// <summary>The native build options of a C/C++ file of a project (docs/tools/projects.md, "Natives"): whether it is built, for which platforms and its build order.</summary>
    public sealed class NativeBuildFileProperties : IFilePropertiesProvider
    {
        private const string Category = "Native build";
        private const string InBuild = "Part of the native build";
        private const string Order = "Build order";
        private const string Platforms = "Platforms";
        private const string AllPlatforms = "(all platforms)";

        public FilePropertyGroup? Describe(FilePropertiesContext file)
        {
            var project = file.Project?.Project;
            if (project == null || !ProjectNativeFiles.IsNativeFile(file.Path)) return null;
            var info = ProjectNativeFiles.Describe(project, file.Path);
            if (info == null) return null;
            var entries = new List<PropertyEntry>
            {
                new(InBuild, PropertyEditKind.Bool, info.InBuild ? "true" : "false")
                {
                    Category = Category,
                    Tip = "The file is one of the sources of the natives of the project (native.sources or native.platformSources): they are put into one file, in their build order, for the virtual machine's library and for a native build.",
                },
            };
            if (info.InBuild)
            {
                entries.Add(new(Order, PropertyEditKind.Text, info.Order.ToString(CultureInfo.InvariantCulture))
                {
                    Category = Category, IsSet = info.HasOrder, CanClear = true,
                    Tip = "The sources are built sorted by this number (smaller first; without one 0, and equal numbers keep the order of the list: platform sources first). What a source #includes of the project has to come before it. Written as { \"file\": ..., \"order\": n } in the project file.",
                });
                string groups = string.Join(", ", info.Groups.Select(g => g ?? AllPlatforms));
                if (info.Explicit && info.Groups.Count == 1)
                {
                    var choices = new List<string> { AllPlatforms };
                    foreach (var key in (project.Native?.PlatformSources?.Keys ?? Enumerable.Empty<string>()).Concat(ProjectNativeFiles.KnownGroups))
                        if (!choices.Contains(key, StringComparer.OrdinalIgnoreCase)) choices.Add(key);
                    entries.Add(new(Platforms, PropertyEditKind.Choice, groups) { Category = Category, Choices = choices, Tip = "All platforms (native.sources) or only the builds for one platform or target (a list of native.platformSources)." });
                }
                else entries.Add(new(Platforms, PropertyEditKind.Text, groups) { Category = Category, IsReadOnly = true, Tip = "The file comes from a pattern or is in several lists: its platforms are changed in the project file." });
                string build = info.Groups[0] == null ? "the build for all platforms" : "a build for " + info.Groups[0];
                entries.Add(new("Position", PropertyEditKind.Text, $"{info.Position} of {info.Count}") { Category = Category, IsReadOnly = true, Tip = $"The place of the file in {build}." });
                entries.Add(new("Listed as", PropertyEditKind.Text, info.Explicit ? "the file itself" : "pattern " + info.Entry) { Category = Category, IsReadOnly = true, Tip = "The entry of the project file that brings the file into the native build." });
            }
            return new FilePropertyGroup
            {
                Entries = entries,
                Error = info.Problems.Count > 0 ? string.Join("\n", info.Problems) : null,
                Set = (name, value) => name switch
                {
                    InBuild => ProjectNativeFiles.SetInBuild(project, file.Path, value == "true"),
                    Order => value.Trim() == "" ? ProjectNativeFiles.SetOrder(project, file.Path, null)
                        : int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int order) ? ProjectNativeFiles.SetOrder(project, file.Path, order)
                        : $"'{value}' is not a whole number.",
                    Platforms => ProjectNativeFiles.SetGroup(project, file.Path, value == AllPlatforms ? null : value),
                    _ => "This property cannot be changed.",
                },
            };
        }
    }
}
