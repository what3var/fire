using System.IO.Compression;
using System.Text;
using fire.Package.Manager;
using fire.Projects;

namespace fire.Compiler
{
    /// <summary>
    /// The measures of Phase D 3 of the port to C (docs/spec/FORGE_C.md, "Phase D Schritt 3"): `forge projectplan <file>` prints what the model of projects makes of a
    /// solution or a project - the workspace, the build order, every member's files, folders and build actions and its BuildPlan -, `forge fpk <file.fpk>` a package as
    /// Fpk.ReadManifest reads it and every entry of the zip. `forge-c projectplan` / `forge-c fpk` print the same (tests/compare-frontend.sh ... project). Measuring tools
    /// only: nothing here changes what a build does.
    /// </summary>
    public static class ProjectDump
    {
        public static int Run(IReadOnlyList<string> args)
        {
            if (args.Count != 2) { Console.Error.WriteLine($"usage: {args[0]} <file>"); return 2; }
            string text = args[0] == "fpk" ? DumpPackage(args[1]) : DumpProjectPlan(args[1]);
            var bytes = new UTF8Encoding(false).GetBytes(text);
            using var stdout = Console.OpenStandardOutput();
            stdout.Write(bytes, 0, bytes.Length);
            return 0;
        }

        private sealed class Dump
        {
            public readonly StringBuilder O = new();
            public string Root = "";
            public string P(string? path) => path == null ? "-" : Path.GetRelativePath(Root, Path.GetFullPath(path)).Replace('\\', '/');
            public void L(string indent, string key, string? value) => O.Append(indent).Append(key).Append(' ').Append(value ?? "-").Append('\n');
        }

        private static void Settings(Dump d, string indent, ProjectSettings s)
        {
            void S(string name, string? v) { if (v != null) d.L(indent, "setting", name + "=" + v); }
            void I(string name, int? v) { if (v != null) d.L(indent, "setting", name + "=" + v.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
            S("subsystem", s.Subsystem); S("mode", s.Mode); I("floatWidth", s.FloatWidth); I("intWidth", s.IntWidth); S("name", s.Name); S("codename", s.Codename);
            S("description", s.Description); S("author", s.Author); S("license", s.License); S("comments", s.Comments);
            if (s.Icon != null) d.L(indent, "setting", "icon=" + d.P(s.Icon));
            S("version", s.Version); S("fileVersion", s.FileVersion);
            foreach (var define in s.Defines ?? new List<string>()) d.L(indent, "setting", "define=" + define);
            S("engine", s.Engine); S("target", s.Target); S("toolchain", s.Toolchain); S("linkage", s.Linkage); S("stripping", s.Stripping); S("optimize", s.Optimize);
            if (s.Output != null) d.L(indent, "setting", "output=" + d.P(s.Output));
            S("typeChecking", s.TypeChecking); S("globalJobs", s.GlobalJobs);
            foreach (var argument in s.Arguments ?? new List<string>()) d.L(indent, "setting", "argument=" + argument);
            S("port", s.Port); I("baud", s.Baud);
        }

        private static void SourceList(Dump d, string indent, string key, NativeSourceList list)
        {
            foreach (var f in list) d.L(indent, key, list.OrderOf(f) is int o ? $"{d.P(f)} order {o}" : d.P(f));
        }

        private static void Native(Dump d, string indent, NativePart part)
        {
            var n = part.Native;
            d.L(indent, "native", $"{part.ImportName} {part.ProjectName} {d.P(part.Directory)}");
            if (n.Language != null) d.L(indent, "language", n.Language);
            d.L(indent, "threadSafe", n.ThreadSafe ? "true" : "false");
            if (n.Reset != null) d.L(indent, "reset", n.Reset);
            SourceList(d, indent, "source", n.Sources);
            foreach (var (key, list) in n.PlatformSources) SourceList(d, indent, "platformSource " + key, list);
            foreach (var p in n.Platforms) d.L(indent, "platform", p);
            foreach (var (key, libs) in n.LinkLibraries) d.L(indent, "linkLibraries", key + " " + string.Join(" ", libs));
            foreach (var (fn, bridge) in n.Functions.Select(f => (f, false)).Concat(n.Bridge.Select(f => (f, true))))
                d.L(indent, bridge ? "bridge" : "function", $"{fn.Name} {fn.Arguments} cpp={fn.Cpp} c={fn.C ?? "-"} needsList={(fn.NeedsList ? 1 : 0)} returnsReference={(fn.ReturnsReference ? 1 : 0)} host={(fn.Host ? 1 : 0)} returns={fn.Returns ?? "-"} parameters={(fn.Parameters != null ? string.Join(",", fn.Parameters) : "-")}");
            foreach (var e in n.Exceptions) d.L(indent, "exception", e);
            foreach (var u in n.Uses) d.L(indent, "uses", u.Import + " " + string.Join(",", u.Platforms));
            foreach (var h in n.Headers) d.L(indent, "header", d.P(h));
        }

        private static string Sha(string text) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

        private static void Sources(Dump d, string indent, IEnumerable<SourceFile> sources)
        {
            foreach (var s in sources) d.L(indent, "source", $"{d.P(s.Path)} {Sha(s.Text)}");
        }

        private static readonly string[] FolderStates = { "included", "undecided", "excluded" };

        private static void ProjectOf(Dump d, Workspace w, LoadedProject p)
        {
            d.L("", "project", p.Name);
            d.L("  ", "file", d.P(p.FilePath));
            d.L("  ", "type", p.Project.Type == OutputType.Library ? "library" : "exe");
            d.L("  ", "import", p.Project.ImportName);
            d.L("  ", "inSolution", p.InSolution ? "true" : "false");
            foreach (var x in p.Problems) d.L("  ", "problem", x);
            foreach (var f in p.Files) d.L("  ", "compile", d.P(f));
            foreach (var f in p.TestFiles) d.L("  ", "test", d.P(f));
            foreach (var f in p.ContentFiles) d.L("  ", "content", d.P(f));
            foreach (var f in p.FolderMap.Folders) d.L("  ", "folder", $"{f.Path} {FolderStates[(int)f.State]} {f.Reason} {f.Entry ?? "-"}");
            foreach (var c in p.CopyFiles) d.L("  ", "copy", $"{c.RelativePath} {(c.Always ? "always" : "newer")}");
            foreach (var f in p.IgnoredFiles) d.L("  ", "ignored", d.P(f));
            foreach (var f in PublishSettings.TargetFolders(p, w.Solution?.Directory)) d.L("  ", "publishFolder", d.P(f));
            if (p.Project.Publish is { } pub) d.L("  ", "publish", $"enabled={(pub.Enabled ? "true" : "false")} clean={(pub.Clean == null ? -1 : pub.Clean.Value ? 1 : 0)}");
            BuildPlan plan;
            try { plan = BuildPlan.Create(w, p); }
            catch (Exception ex) when (ex is ProjectException or PackageException) { d.L("  ", "plan-exception", ex.Message); return; }
            foreach (var e in plan.Errors) d.L("  ", "error", e);
            Sources(d, "  ", plan.Sources);
            foreach (var r in plan.Packages) d.L("  ", "package", $"{r.Package ?? "-"} {r.Version ?? "-"}");
            Settings(d, "  ", plan.Settings);
            if (plan.Native != null) Native(d, "  ", plan.Native);
            foreach (var l in plan.Libraries.Values)
            {
                d.L("  ", "library", $"{l.ImportName} {l.Name}");
                foreach (var r in l.Requires) d.L("    ", "requires", r);
                foreach (var r in l.Packages) d.L("    ", "package", r.Package);
                Sources(d, "    ", l.Sources);
                Settings(d, "    ", l.Settings);
                if (l.Native != null) Native(d, "    ", l.Native);
            }
            foreach (var item in ProjectContent.ItemsOf(plan)) d.L("  ", "contentItem", $"{item.RelativePath} {d.P(item.Source)} {(item.Always ? "always" : "newer")}");
            foreach (var l in plan.LibraryOrder(plan.Libraries.Values.Select(l => l.ImportName))) d.L("  ", "libraryOrder", l.ImportName);
            var host = fire.Runtime.TargetProfile.Host;
            d.L("  ", "outputFolder", d.P(ProjectOutput.Folder(p.Directory, host.Name, "release")));
            d.L("  ", "programPath", d.P(ProjectOutput.ProgramPath(p.Directory, p.Name, host.Name, "release", host.ProgramExtension)));
            if (p.Project.Type == OutputType.Library && plan.IsValid)
            {
                d.L("  ", "packagePath", d.P(ProjectBuilder.PackagePathFor(plan)));
                d.L("  ", "packageVersion", ProjectBuilder.PackageVersion(plan.Settings.Version));
                var imports = new List<string>();
                var resources = new Dictionary<string, string>();
                string flat;
                try { flat = ProjectBuilder.Flatten(plan.Sources, imports, resources, p.Directory); }
                catch (ProjectException ex) { d.L("  ", "flatten-exception", ex.Message); return; }
                d.L("  ", "flatten", Sha(flat));
                foreach (var i in imports) d.L("  ", "flattenImport", i);
                foreach (var (key, file) in resources) d.L("  ", "flattenResource", $"{key} {d.P(file)}");
            }
        }

        public static string DumpProjectPlan(string path)
        {
            var d = new Dump { Root = Path.GetDirectoryName(Path.GetFullPath(path))! };
            try
            {
                var w = Workspace.Open(path);
                d.L("", "workspace", w.Name);
                if (w.Solution != null)
                {
                    d.L("", "solution", d.P(w.Solution.FilePath));
                    foreach (var f in w.Solution.Folders) d.L("", "solutionFolder", f);
                    Settings(d, "", w.Solution.Settings ?? new ProjectSettings());
                }
                foreach (var m in w.Projects) d.L("", "member", m.Name);
                d.L("", "startup", w.Startup?.Name ?? "-");
                d.L("", "buildOrder", string.Join(" ", w.BuildOrder().Select(p => p.Name)));
                foreach (var m in w.Projects) ProjectOf(d, w, m);
            }
            catch (Exception ex) when (ex is ProjectException or PackageException) { d.L("", "exception", ex.Message); }
            return d.O.ToString();
        }

        public static string DumpPackage(string path)
        {
            var o = new StringBuilder();
            try
            {
                var manifest = Fpk.ReadManifest(path);
                o.Append(manifest.ToJson()).Append('\n');
                using var zip = ZipFile.OpenRead(path);
                foreach (var e in zip.Entries)
                {
                    using var stream = e.Open();
                    var buffer = new MemoryStream();
                    stream.CopyTo(buffer);
                    string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(buffer.ToArray())).ToLowerInvariant();
                    o.Append("entry ").Append(e.FullName).Append(' ').Append(e.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(' ')
                        .Append(e.Crc32.ToString("x8", System.Globalization.CultureInfo.InvariantCulture)).Append(' ').Append(((uint)e.ExternalAttributes).ToString("x8", System.Globalization.CultureInfo.InvariantCulture))
                        .Append(' ').Append(hash).Append('\n');
                }
            }
            catch (PackageException ex) { o.Append("exception ").Append(ex.Message).Append('\n'); }
            return o.ToString();
        }
    }
}
