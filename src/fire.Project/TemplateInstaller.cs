using System.Text;
using System.Text.RegularExpressions;

namespace fire.Projects
{
    /// <summary>
    /// The values that replace placeholders in the names and the text of a template. A placeholder is `$name$` - or `__name__` for a file system or tool that dislikes the dollar signs:
    /// <c>name</c> (what the user typed), <c>ident</c> (the name as an identifier: letters, digits and `_`), <c>class</c> (the identifier with a capital first letter), <c>identlower</c>,
    /// <c>project</c> and <c>projectident</c> (the project that a code template is added to), <c>year</c> and <c>date</c>. Anything else between the signs stays as it is.
    /// </summary>
    public sealed class TemplateValues
    {
        private readonly Dictionary<string, string> _values;
        private static readonly string[] Keys = { "name", "ident", "class", "identlower", "project", "projectident", "year", "date" };

        // `$key$` or `__key__`; the keys are listed so that a name like `__init__` stays
        private static readonly Regex Placeholder = new(@"\$(?<k>" + string.Join("|", Keys) + @")\$|__(?<k>" + string.Join("|", Keys) + @")__", RegexOptions.Compiled);

        public string Name => _values["name"];

        public TemplateValues(string name, string? project = null)
        {
            string ident = IdentOf(name);
            string projectName = project ?? "";
            _values = new Dictionary<string, string>
            {
                ["name"] = name,
                ["ident"] = ident,
                ["class"] = char.ToUpperInvariant(ident[0]) + ident.Substring(1),
                ["identlower"] = ident.ToLowerInvariant(),
                ["project"] = projectName,
                ["projectident"] = projectName.Length == 0 ? "" : IdentOf(projectName),
                ["year"] = DateTime.Now.Year.ToString(),
                ["date"] = DateTime.Now.ToString("yyyy-MM-dd"),
            };
        }

        /// <summary>The name as an identifier (the same rule as the import name of a library): other characters become `_`, a name that does not start with a letter gets an `L`.</summary>
        public static string IdentOf(string name)
        {
            var chars = name.Select(c => char.IsLetterOrDigit(c) && c < 128 || c == '_' ? c : '_').ToArray();
            string s = new string(chars);
            if (s.Length == 0 || !char.IsLetter(s[0])) s = "L" + s;
            return s;
        }

        public string Expand(string text) => text.Length == 0 ? text : Placeholder.Replace(text, m => _values[m.Groups["k"].Value]);

        /// <summary>Does the text have a `name` placeholder?</summary>
        public static bool HasNamePlaceholder(string text) => text.Contains("$name$", StringComparison.Ordinal) || text.Contains("__name__", StringComparison.Ordinal);
    }

    /// <summary>Makes the files, projects and solutions of templates (see <see cref="TemplateCatalog"/>).</summary>
    public static class TemplateInstaller
    {
        /// <summary>Copies the files of a template into `targetDirectory`, with the placeholders of the names and of the text replaced (a binary file is copied as it is). Nothing is written if one of
        /// the files exists already (a <see cref="ProjectException"/> names it) or a name would leave the folder. Returns the full paths of the files that were made.</summary>
        public static IReadOnlyList<string> Instantiate(FireTemplate template, string targetDirectory, TemplateValues values)
        {
            string root = Path.GetFullPath(targetDirectory);
            string prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var plan = new List<(string Source, string Target)>();
            foreach (var rel in template.Files)
            {
                string expanded = values.Expand(rel);
                string target = Path.GetFullPath(Path.Combine(root, expanded.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(prefix, StringComparison.Ordinal)) throw new ProjectException($"The template '{template.Title}' would write '{expanded}' outside of the folder.");
                if (expanded.Split('/', '\\').Any(part => part.Length == 0 || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) throw new ProjectException($"'{expanded}' is not a file name that the template '{template.Title}' can make.");
                if (File.Exists(target)) throw new ProjectException($"'{target}' exists already.");
                plan.Add((Path.Combine(template.Directory, rel.Replace('/', Path.DirectorySeparatorChar)), target));
            }
            var made = new List<string>();
            foreach (var (source, target) in plan)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                byte[] bytes = File.ReadAllBytes(source);
                if (IsText(bytes))
                {
                    var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
                    File.WriteAllText(target, values.Expand(encoding.GetString(bytes).TrimStart('﻿')), encoding);
                }
                else File.WriteAllBytes(target, bytes);
                made.Add(target);
            }
            // an empty folder of the template (a `.gitkeep`) is made as a folder
            return made;
        }

        private static bool IsText(byte[] bytes)
        {
            int n = Math.Min(bytes.Length, 8000);
            for (int i = 0; i < n; i++) if (bytes[i] == 0) return false;
            return true;
        }

        /// <summary>The files to open after a code template was made: the ones the template names, else the first.</summary>
        public static IReadOnlyList<string> FilesToOpen(FireTemplate template, string targetDirectory, TemplateValues values, IReadOnlyList<string> made)
        {
            if (template.Open.Count == 0) return made.Take(1).ToList();
            string root = Path.GetFullPath(targetDirectory);
            return template.Open.Select(o => Path.GetFullPath(Path.Combine(root, values.Expand(o).Replace('/', Path.DirectorySeparatorChar)))).Where(File.Exists).ToList();
        }

        /// <summary>Makes a project from a template: the folder `parentFolder/name` with the files of the template and a project file `name.fireproj` (the template's own, or - if it has none - a new one).
        /// A template that came with a package makes the project reference that package. Returns the path of the project file.</summary>
        public static string CreateProject(FireTemplate template, string parentFolder, string name)
        {
            if (template.Scope != TemplateScope.Project) throw new ProjectException($"'{template.Title}' is not a template for a project.");
            if (template.Empty) throw new ProjectException("The empty template has no project.");
            if (ProjectTemplates.CheckName(name) is { } problem) throw new ProjectException(problem);
            string directory = Path.GetFullPath(Path.Combine(parentFolder, name));
            string projectPath = Path.Combine(directory, name + FireProject.Extension);
            if (File.Exists(projectPath)) throw new ProjectException($"'{projectPath}' exists already.");
            Directory.CreateDirectory(directory);
            Instantiate(template, directory, new TemplateValues(name, name));

            if (!File.Exists(projectPath))
            {
                var own = Directory.GetFiles(directory, "*" + FireProject.Extension, SearchOption.TopDirectoryOnly);
                if (own.Length == 1) { File.Move(own[0], projectPath); }
                else if (own.Length > 1) throw new ProjectException($"The template '{template.Title}' makes more than one project file; name the one of the project '{name}{FireProject.Extension}'.");
                else new FireProject { Name = name, Type = string.Equals(template.Type, "library", StringComparison.OrdinalIgnoreCase) ? OutputType.Library : OutputType.Exe }.Save(projectPath);
            }
            var wanted = new List<ProjectReference>();
            if (template.AddPackageReference && template.PackageName != null) wanted.Add(new ProjectReference { Package = template.PackageName, Version = template.PackageVersion });
            foreach (var r in template.References) wanted.Add(new ProjectReference { Package = r });
            if (wanted.Count > 0)
            {
                var project = FireProject.Load(projectPath);
                bool changed = false;
                foreach (var r in wanted)
                    if (!project.References.Any(x => string.Equals(x.Package, r.Package, StringComparison.OrdinalIgnoreCase))) { project.References.Add(r); changed = true; }
                if (changed) project.Save();
            }
            return projectPath;
        }

        /// <summary>Makes a solution in `folder` (`folder/name.firesln`) and, unless the template is the empty one, a project of the same name in `folder/name`. Returns the path of the solution file.</summary>
        public static string CreateSolution(FireTemplate template, string folder, string name)
        {
            if (ProjectTemplates.CheckName(name) is { } problem) throw new ProjectException(problem);
            string full = Path.GetFullPath(folder);
            string solutionPath = Path.Combine(full, name + FireSolution.Extension);
            if (File.Exists(solutionPath)) throw new ProjectException($"'{solutionPath}' exists already.");
            Directory.CreateDirectory(full);
            var solution = new FireSolution { Name = name };
            if (!template.Empty)
            {
                string projectPath = CreateProject(template, full, name);
                solution.Projects.Add(ProjectFiles.Relative(full, projectPath));
            }
            solution.Save(solutionPath);
            return solutionPath;
        }

        /// <summary>The main file a project template made, to open afterwards: the template's `open` list (placeholders replaced), else the first source file of the project.</summary>
        public static IReadOnlyList<string> ProjectFilesToOpen(FireTemplate template, string projectPath, string name)
        {
            string dir = Path.GetDirectoryName(projectPath)!;
            if (template.Open.Count > 0)
            {
                var values = new TemplateValues(name, name);
                return template.Open.Select(o => Path.GetFullPath(Path.Combine(dir, values.Expand(o).Replace('/', Path.DirectorySeparatorChar)))).Where(File.Exists).ToList();
            }
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Where(f => FireProject.IsSourceFile(f) && !FireProject.IsMarkupFile(f)).OrderBy(f => f, StringComparer.Ordinal).Take(1).ToList();
        }
    }
}
