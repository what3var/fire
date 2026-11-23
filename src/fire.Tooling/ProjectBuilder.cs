using fire.Projects;

namespace fire.Tooling
{
    /// <summary>The versions of packages and what spark asks of a workspace before it hands it to forge (packing and publishing are forge's: `forge build`, `forge publish`).</summary>
    public static class ProjectBuilder
    {
        /// <summary>The next version: `part` is `major`, `minor` or `patch` (the lower numbers become 0). `1.2.3` -> `1.2.4`; a version with four numbers keeps the fourth out (a package has three).</summary>
        public static string BumpVersion(string? version, string part = "patch")
        {
            var v = PackageVersion(version).Split('.').Select(int.Parse).ToArray();
            switch (part.ToLowerInvariant())
            {
                case "major": v[0]++; v[1] = 0; v[2] = 0; break;
                case "minor": v[1]++; v[2] = 0; break;
                case "patch": v[2]++; break;
                default: throw new ProjectException($"'{part}' is not major, minor or patch.");
            }
            return string.Join('.', v);
        }

        /// <summary>Is `text` a version a package can have (`1.2.3`: three numbers)?</summary>
        public static bool IsPackageVersion(string? text)
        {
            var parts = (text ?? "").Split('.');
            return parts.Length == 3 && parts.All(p => p.Length > 0 && p.All(char.IsDigit));
        }

        /// <summary>The file the package of a library gets in `outputDirectory` (what `forge build` of a library project writes).</summary>
        public static string PackagePathFor(BuildPlan plan, string? outputDirectory = null) =>
            Path.Combine(Path.GetFullPath(outputDirectory ?? plan.Settings.Output ?? Path.Combine(plan.Project.Directory, "bin")), $"{plan.Project.Name}-{PackageVersion(plan.Settings.Version)}.fpk");

        /// <summary>`1.2.3.0` or `1.2` as a package version (three numbers); anything else: 1.0.0.</summary>
        public static string PackageVersion(string? version)
        {
            if (version == null) return "1.0.0";
            var parts = version.Split('.');
            if (parts.Length >= 3 && parts.Take(3).All(p => p.Length > 0 && p.All(char.IsDigit))) return string.Join('.', parts.Take(3));
            if (parts.Length == 2 && parts.All(p => p.Length > 0 && p.All(char.IsDigit))) return version + ".0";
            if (parts.Length == 1 && parts[0].Length > 0 && parts[0].All(char.IsDigit)) return version + ".0.0";
            return "1.0.0";
        }

        /// <summary>The projects of a workspace that are published (`publish.enabled`), in the order of the build.</summary>
        public static IReadOnlyList<LoadedProject> EnabledProjects(Workspace workspace) => workspace.BuildOrder().Where(p => p.Project.Publish?.Enabled == true).ToList();
    }
}
