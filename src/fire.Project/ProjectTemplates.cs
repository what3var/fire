namespace fire.Projects
{
    /// <summary>Helpers around the templates (the templates themselves are folders, see <see cref="TemplateCatalog"/>): names and the starting header of a project's natives.</summary>
    public static class ProjectTemplates
    {
        /// <summary>The folder a new solution proposes: `$HOME/spark/{name}`.</summary>
        public static string DefaultSolutionFolder(string name) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "spark", name);

        /// <summary>The problem with a name for a solution, project or file (null: fine): it becomes a folder and a file name.</summary>
        public static string? CheckName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "The name is empty.";
            if (name != name.Trim()) return "The name must not start or end with a space.";
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return $"The name '{name}' has characters that a file or folder name cannot have.";
            if (name.StartsWith('.') || name.EndsWith('.')) return "The name must not start or end with a dot.";
            return null;
        }

        /// <summary>The starting header of a project that gets natives later ("Add Native Code").</summary>
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
