using fire.Package.Manager;

namespace fire.Projects
{
    /// <summary>
    /// The templates that the editor offers for new files and projects (docs/TEMPLATES.md), flat in one list. They are folders in
    /// <list type="bullet">
    /// <item>`Templates/` in the program folder (what ships with fire), and `~/spark/templates/` (the user's own), each with `Code/`, `Project/` and `Package/Name_1.2.3/{Code,Project}/`;</item>
    /// <item>`templates/` in the folder of an installed package (`ember` unpacks it there; a package brings it with its forge file or as a library project).</item>
    /// </list>
    /// The folders are found by name without regard to case (`Code`, `code`); `Projekt` is `Project` too.
    /// </summary>
    public sealed class TemplateCatalog
    {
        public IReadOnlyList<FireTemplate> All { get; }

        public IEnumerable<FireTemplate> Code => All.Where(t => t.Scope == TemplateScope.Code);
        /// <summary>Templates for a whole project; with <paramref name="includeEmpty"/> also the one that makes a solution without a project.</summary>
        public IEnumerable<FireTemplate> Projects(bool includeEmpty = true) => All.Where(t => t.Scope == TemplateScope.Project && (includeEmpty || !t.Empty));

        private TemplateCatalog(List<FireTemplate> all) { All = all; }

        public static string BuiltinRoot => Path.Combine(AppContext.BaseDirectory, "Templates");
        public static string UserRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "spark", "templates");

        /// <summary>Reads all templates: the program's, the user's and those of installed packages (<paramref name="store"/>: the default one; null for none). Missing folders are fine.</summary>
        public static TemplateCatalog Load(string? builtinRoot = null, string? userRoot = null, PackageStore? store = null, bool includeInstalledPackages = true)
        {
            var list = new List<FireTemplate>();
            ScanRoot(builtinRoot ?? BuiltinRoot, list);
            ScanRoot(userRoot ?? UserRoot, list);
            if (includeInstalledPackages)
            {
                IReadOnlyList<InstalledPackage> installed;
                try { installed = (store ?? PackageStore.Default).Installed(); } catch (IOException) { installed = Array.Empty<InstalledPackage>(); }
                foreach (var package in installed)
                {
                    string? folder = SubDir(package.Directory, "templates");
                    if (folder != null) ScanScopes(folder, list, package.Name, package.Version);
                }
            }
            list.Sort((a, b) =>
            {
                int c = a.Scope.CompareTo(b.Scope);
                if (c == 0) c = a.Order.CompareTo(b.Order);
                if (c == 0) c = string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
                if (c == 0) c = string.Compare(a.Source, b.Source, StringComparison.OrdinalIgnoreCase);
                return c;
            });
            return new TemplateCatalog(list);
        }

        /// <summary>The first template of a scope with this title (not case sensitive), or null.</summary>
        public FireTemplate? Find(TemplateScope scope, string title) => All.FirstOrDefault(t => t.Scope == scope && string.Equals(t.Title, title, StringComparison.OrdinalIgnoreCase));

        // ---- reading ---------------------------------------------------------------------------------------------------------------------

        /// <summary>The subfolder of `parent` with this name, whatever its case; null if there is none.</summary>
        private static string? SubDir(string parent, string name)
        {
            if (!Directory.Exists(parent)) return null;
            string direct = Path.Combine(parent, name);
            if (Directory.Exists(direct)) return direct;
            try { return Directory.GetDirectories(parent).FirstOrDefault(d => string.Equals(Path.GetFileName(d), name, StringComparison.OrdinalIgnoreCase)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }

        private static void ScanRoot(string root, List<FireTemplate> list)
        {
            if (!Directory.Exists(root)) return;
            ScanScopes(root, list, null, null);
            if (SubDir(root, "Package") is { } packages)
            {
                foreach (var dir in SafeDirectories(packages))
                {
                    var (name, version) = ParsePackageFolder(Path.GetFileName(dir));
                    ScanScopes(dir, list, name, version);
                }
            }
        }

        /// <summary>`Name_1.2.3.4` -> (Name, 1.2.3.4); a folder name without a version after the last `_` is the name alone.</summary>
        public static (string Name, string? Version) ParsePackageFolder(string folder)
        {
            int cut = folder.LastIndexOf('_');
            if (cut > 0 && cut + 1 < folder.Length && char.IsDigit(folder[cut + 1])) return (folder.Substring(0, cut), folder.Substring(cut + 1));
            return (folder, null);
        }

        private static void ScanScopes(string folder, List<FireTemplate> list, string? package, string? version)
        {
            if (SubDir(folder, "Code") is { } code) foreach (var dir in SafeDirectories(code)) AddTemplate(list, dir, TemplateScope.Code, package, version);
            foreach (var name in new[] { "Project", "Projekt" })
                if (SubDir(folder, name) is { } project) foreach (var dir in SafeDirectories(project)) AddTemplate(list, dir, TemplateScope.Project, package, version);
        }

        private static IEnumerable<string> SafeDirectories(string folder)
        {
            try { return Directory.GetDirectories(folder).Where(d => !Path.GetFileName(d).StartsWith('.')).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
        }

        private static readonly string[] ImageExtensions = { ".png", ".bmp", ".gif" };

        private static void AddTemplate(List<FireTemplate> list, string dir, TemplateScope scope, string? package, string? version)
        {
            var description = TemplateDescription.Load(dir);
            if (description.Hidden) return;

            string? iconPath = null;
            string iconKey = scope == TemplateScope.Project ? "project" : "file";
            if (!string.IsNullOrWhiteSpace(description.Icon))
            {
                string candidate = Path.Combine(dir, description.Icon);
                if (ImageExtensions.Contains(Path.GetExtension(description.Icon).ToLowerInvariant()) && File.Exists(candidate)) iconPath = candidate;
                else if (Path.GetExtension(description.Icon).Length == 0) iconKey = description.Icon.Trim().ToLowerInvariant();
            }
            if (iconPath == null)
                foreach (var name in new[] { "icon.png", "template.png" })
                    if (File.Exists(Path.Combine(dir, name))) { iconPath = Path.Combine(dir, name); break; }

            var files = new List<string>();
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
                    if (rel.Split('/').Any(part => part.StartsWith('.') && part.Length > 1 && !part.StartsWith(".gitignore", StringComparison.Ordinal) && part != ".gitkeep")) continue;
                    if (string.Equals(rel, TemplateDescription.FileName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (iconPath != null && string.Equals(Path.GetFullPath(file), Path.GetFullPath(iconPath), StringComparison.OrdinalIgnoreCase)) continue;
                    files.Add(rel);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
            files.Sort(StringComparer.Ordinal);

            string title = string.IsNullOrWhiteSpace(description.Title) ? Path.GetFileName(dir) : description.Title.Trim();
            string extension = description.Extension ?? "";
            if (scope == TemplateScope.Code && string.IsNullOrEmpty(extension))
            {
                string? named = files.FirstOrDefault(f => TemplateValues.HasNamePlaceholder(Path.GetFileName(f))) ?? files.FirstOrDefault();
                if (named != null) extension = Path.GetExtension(named);
            }
            if (extension.Length > 0 && !extension.StartsWith('.')) extension = "." + extension;

            list.Add(new FireTemplate
            {
                Scope = scope,
                Title = title,
                Description = string.IsNullOrWhiteSpace(description.Description) ? null : description.Description.Trim(),
                Directory = dir,
                IconPath = iconPath,
                IconKey = iconKey,
                Source = package == null ? "Local" : version == null ? $"From {package}" : $"From {package} {version}",
                PackageName = package,
                PackageVersion = version,
                DefaultName = string.IsNullOrWhiteSpace(description.DefaultName) ? title.Replace(" ", "") : description.DefaultName.Trim(),
                Extension = extension,
                Empty = description.Empty,
                Order = description.Order,
                Type = description.Type,
                References = (IReadOnlyList<string>?)description.References ?? Array.Empty<string>(),
                AddPackageReference = package != null && description.AddPackageReference != false,
                Open = (IReadOnlyList<string>?)description.Open ?? Array.Empty<string>(),
                Files = files,
            });
        }
    }
}
