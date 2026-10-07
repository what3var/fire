namespace fire.Projects
{
    public enum TemplateKind { Empty, Terminal, Desktop, Library, NativeLibrary }

    /// <summary>What "New Solution" and "New Project" offer (docs/PROJECTS.md): a kind of project with a tiny Hello-World that fits it.</summary>
    public sealed class ProjectTemplate
    {
        public required TemplateKind Kind { get; init; }
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string Description { get; init; }
        /// <summary>false: nothing is made but the (empty) solution.</summary>
        public bool MakesProject => Kind != TemplateKind.Empty;
    }

    public static class ProjectTemplates
    {
        /// <summary>For a new solution: the empty one and one per kind of project.</summary>
        public static IReadOnlyList<ProjectTemplate> ForSolution { get; } = new[]
        {
            new ProjectTemplate { Kind = TemplateKind.Empty, Id = "empty", Name = "Empty", Description = "A solution without a project: add projects (or existing ones) later." },
            new ProjectTemplate { Kind = TemplateKind.Terminal, Id = "terminal", Name = "Terminal", Description = "A program for the console: it prints Hello, World." },
            new ProjectTemplate { Kind = TemplateKind.Desktop, Id = "desktop", Name = "Desktop", Description = "A program with a window (no console): a label and a button, built with #import \"ui\"." },
            new ProjectTemplate { Kind = TemplateKind.Library, Id = "library", Name = "Library", Description = "Classes and functions without an entry point, used by other projects (Add Reference) or packed as a package (.fpk)." },
            new ProjectTemplate { Kind = TemplateKind.NativeLibrary, Id = "native-library", Name = "Native Library", Description = "A library with C++ natives in the folder native/: fire code wraps them in a class. The C++ goes into the package too." },
        };

        /// <summary>For a new project of a solution (or on its own): the same without the empty one.</summary>
        public static IReadOnlyList<ProjectTemplate> ForProject { get; } = ForSolution.Where(t => t.MakesProject).ToList();

        public static ProjectTemplate? Find(string id) => ForSolution.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

        /// <summary>The folder a new solution proposes: `$HOME/spark/{name}`.</summary>
        public static string DefaultSolutionFolder(string name) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "spark", name);

        /// <summary>The problem with a name for a solution or project (null: fine): it becomes a folder and a file name.</summary>
        public static string? CheckName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "The name is empty.";
            if (name != name.Trim()) return "The name must not start or end with a space.";
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return $"The name '{name}' has characters that a file or folder name cannot have.";
            if (name.StartsWith('.') || name.EndsWith('.')) return "The name must not start or end with a dot.";
            return null;
        }

        /// <summary>Makes the project of a template: the folder `parent/name`, the project file `parent/name/name.fireproj` and its Hello-World files. Returns the path of the project file.
        /// An existing file is never overwritten (the existing project file is an error).</summary>
        public static string CreateProject(ProjectTemplate template, string parentFolder, string name)
        {
            if (!template.MakesProject) throw new ProjectException("The empty template has no project.");
            if (CheckName(name) is { } problem) throw new ProjectException(problem);
            string directory = Path.GetFullPath(Path.Combine(parentFolder, name));
            string projectPath = Path.Combine(directory, name + FireProject.Extension);
            if (File.Exists(projectPath)) throw new ProjectException($"'{projectPath}' exists already.");
            Directory.CreateDirectory(directory);

            var project = new FireProject { Name = name, FilePath = projectPath };
            string ident = project.ImportName;
            void Write(string relative, string text)
            {
                string full = Path.Combine(directory, relative);
                if (File.Exists(full)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, text);
            }

            switch (template.Kind)
            {
                case TemplateKind.Terminal:
                    Write("main.script", $"// {name}: a program for the console.\nprint(\"Hello, World!\")\n");
                    break;
                case TemplateKind.Desktop:
                    project.Settings.Subsystem = "gui";
                    Write("main.script", DesktopSource(name));
                    break;
                case TemplateKind.Library:
                    project.Type = OutputType.Library;
                    Write(ident.ToLowerInvariant() + ".script", LibrarySource(name, ident));
                    break;
                case TemplateKind.NativeLibrary:
                    project.Type = OutputType.Library;
                    project.Native = new ProjectNative();
                    Write(ident.ToLowerInvariant() + ".script", NativeLibrarySource(name, ident));
                    Write("native/" + ident.ToLowerInvariant() + ".hpp", NativeHeader(name, ident));
                    break;
            }
            project.Save(projectPath);
            return projectPath;
        }

        /// <summary>Makes a solution in `folder` (`folder/name.firesln`) and, unless the template is the empty one, a project of the same name in `folder/name`. Returns the path of the solution file.</summary>
        public static string CreateSolution(ProjectTemplate template, string folder, string name)
        {
            if (CheckName(name) is { } problem) throw new ProjectException(problem);
            string full = Path.GetFullPath(folder);
            string solutionPath = Path.Combine(full, name + FireSolution.Extension);
            if (File.Exists(solutionPath)) throw new ProjectException($"'{solutionPath}' exists already.");
            Directory.CreateDirectory(full);
            var solution = new FireSolution { Name = name };
            if (template.MakesProject)
            {
                string projectPath = CreateProject(template, full, name);
                solution.Projects.Add(ProjectFiles.Relative(full, projectPath));
            }
            solution.Save(solutionPath);
            return solutionPath;
        }

        private static string DesktopSource(string name) => $$"""
            // {{name}}: a program with a window.
            #import "ui"

            var fb = new Framebuffer(480, 320)
            var win = new Window(fb, "{{name}}")
            var ui = new UI.Root(fb, win)

            var label = new UI.Label("Hello, World!", 20, 20)
            var button = new UI.Button("Click me", 20, 60)
            var clicks = 0
            button.onClick = func () => { clicks++; label.text = "Clicked " + clicks + " times" }
            ui.Add(label)
            ui.Add(button)

            while (ui.Tick()) { }

            """;

        private static string LibrarySource(string name, string ident) => $$"""
            // The library {{name}}: classes and functions, no statements at the top level.
            // A program that references it writes #import "{{ident}}" and calls {{ident}}.Greeter.Hello("World").
            namespace {{ident}} {
                class Greeter {
                    static string Hello(string name) { return "Hello, " + name + "!" }
                }
            }

            """;

        private static string NativeLibrarySource(string name, string ident) => $$"""
            // The library {{name}} with C++ natives (native/{{ident.ToLowerInvariant()}}.hpp): the fire side wraps them in a class.
            // A program that references it writes #import "{{ident}}" and calls {{ident}}.Native.Add(1, 2).
            namespace {{ident}} {
                class Native {
                    static int Add(int a, int b) { return __{{ident.ToLowerInvariant()}}_add(a, b) }
                }
            }

            """;

        public static string NativeHeader(string name, string ident) => $$"""
            // The natives of {{name}}: C++ functions on Value, in namespace fire (docs/PACKAGE_NATIVES.md).
            // Every `inline Value name(Value a, ...)` below becomes the fire function `__name` that the prelude wraps.
            #pragma once
            #include <cstdint>

            namespace fire {

            // {{ident.ToLowerInvariant()}}_add(a, b) -> int
            inline Value {{ident.ToLowerInvariant()}}_add(Value a, Value b) {
                return Int(a.i + b.i);
            }

            }  // namespace fire

            """;
    }
}
