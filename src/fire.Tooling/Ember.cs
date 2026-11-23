using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace fire.Tooling
{
    /// <summary>A package as ember lists it (`ember find --json`, `ember list --json`).</summary>
    public sealed record EmberPackage(string Name, string Latest, string Installed, string Author, string Description, string Source, IReadOnlyList<string> Versions,
        IReadOnlyList<string> Imports, string? Origin, string? Verified);

    /// <summary>
    /// ember, the package manager (M3: the fire program tools/ember, built by forge - the C# ember is gone), as spark's Package Manager uses it: through its command line
    /// (`ember find --json`, `ember list --json`, `ember install`, `ember remove`) - the same store, the same sources and lock file as on the command line.
    /// Where it is: <c>FIRE_EMBER</c>, else next to forge (the folder of `forge package-dist`; the build of spark puts it there), else next to spark, else forge builds it
    /// from tools/ember of the repository next to forge.
    /// </summary>
    public static class Ember
    {
        private static string Extension => OperatingSystem.IsWindows() ? ".exe" : "";
        private static readonly object FindLock = new();
        private static string? _found;

        /// <summary>The program ember; an <see cref="InvalidOperationException"/> that says why when there is none.</summary>
        public static string Locate()
        {
            lock (FindLock)
            {
                if (_found != null && File.Exists(_found)) return _found;
                if (Environment.GetEnvironmentVariable("FIRE_EMBER") is { Length: > 0 } own)
                    return _found = File.Exists(own) ? Path.GetFullPath(own) : throw new InvalidOperationException($"FIRE_EMBER={own} does not exist.");
                string forge = Forge.Require();
                foreach (string dir in new[] { Path.GetDirectoryName(forge)!, AppContext.BaseDirectory })
                    if (Path.Combine(dir, "ember" + Extension) is var e && File.Exists(e)) return _found = e;
                // (built by forge from the repository's sources, next to it)
                string? project = null;
                foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
                    for (var d = new DirectoryInfo(start); d != null && project == null; d = d.Parent)
                        if (File.Exists(Path.Combine(d.FullName, "tools", "ember", "ember.fireproj"))) project = Path.Combine(d.FullName, "tools", "ember", "ember.fireproj");
                if (project == null) throw new InvalidOperationException("ember was not found next to forge or spark (and there is no tools/ember to build it from).");
                string target = Path.Combine(Path.GetDirectoryName(forge)!, "ember" + Extension);
                var (code, output, errors) = Forge.Run(new[] { "build", project, "-o", target }, Path.GetDirectoryName(project));
                if (code != 0 || !File.Exists(target)) throw new InvalidOperationException("ember could not be built: " + (errors + output).Trim());
                return _found = target;
            }
        }

        /// <summary>Runs ember to its end: its exit code, its output, its error output (with the environment forge runs with: the store spark uses).</summary>
        public static (int ExitCode, string Output, string Errors) Run(IEnumerable<string> args, int timeoutMs = 900000, CancellationToken cancel = default)
        {
            var info = new ProcessStartInfo(Locate())
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
            };
            foreach (string a in args) info.ArgumentList.Add(a);
            foreach (var (k, v) in Forge.Environment()) info.Environment[k] = v;
            if (!info.Environment.ContainsKey("FIRE_PACKAGES") || string.IsNullOrEmpty(info.Environment["FIRE_PACKAGES"])) info.Environment["FIRE_PACKAGES"] = fire.Projects.PackageStore.Default.Root;
            var output = new StringBuilder();
            var errors = new StringBuilder();
            using var process = new Process { StartInfo = info };
            process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.Append(e.Data).Append('\n'); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errors) errors.Append(e.Data).Append('\n'); };
            process.Start();
            fire.Debugging.ChildProcesses.Add(process);
            process.StandardInput.Close();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            bool killed = false;
            using (cancel.Register(() => { killed = true; try { process.Kill(true); } catch (Exception) { } }))
            {
                if (!process.WaitForExit(timeoutMs)) { killed = true; try { process.Kill(true); } catch (Exception) { } }
                process.WaitForExit();
            }
            lock (output) lock (errors) return (killed ? -1 : process.ExitCode, output.ToString(), errors.ToString());
        }

        /// <summary>Runs ember and throws an <see cref="InvalidOperationException"/> with its message (`ember: ...`) when it fails; its output otherwise.</summary>
        public static string RunOrThrow(params string[] args)
        {
            var (code, output, errors) = Run(args);
            if (code != 0)
            {
                string why = errors.Trim().Length > 0 ? errors.Trim() : output.Trim();
                if (why.StartsWith("ember: ", StringComparison.Ordinal)) why = why.Substring(7);
                throw new InvalidOperationException(why.Length > 0 ? why : $"ember {string.Join(' ', args)} ended with exit code {code}.");
            }
            return output;
        }

        private static string S(JsonNode? n) => n?.GetValue<string>() ?? "";

        /// <summary>`ember find --json [query]`: the packages the sources offer, and the sources that could not be read.</summary>
        public static (List<EmberPackage> Packages, List<string> Warnings) Find(string query)
        {
            var args = new List<string> { "find", "--json" };
            if (query.Trim().Length > 0) args.Add(query.Trim());
            var json = JsonNode.Parse(RunOrThrow(args.ToArray())) as JsonObject ?? throw new InvalidOperationException("ember find gave no list.");
            var list = new List<EmberPackage>();
            foreach (var p in json["packages"]?.AsArray() ?? new JsonArray())
                if (p is JsonObject o)
                    list.Add(new EmberPackage(S(o["name"]), S(o["latest"]), S(o["installed"]), S(o["author"]), S(o["description"]), S(o["source"]),
                        (o["versions"]?.AsArray() ?? new JsonArray()).OfType<JsonObject>().Select(v => S(v["version"]) + (v["yanked"]?.GetValue<bool>() == true ? " (yanked)" : "")).ToList(),
                        Array.Empty<string>(), null, null));
            var warnings = (json["warnings"]?.AsArray() ?? new JsonArray()).Select(w => S(w)).ToList();
            return (list, warnings);
        }

        /// <summary>`ember list --json`: the installed packages.</summary>
        public static List<EmberPackage> Installed()
        {
            var json = JsonNode.Parse(RunOrThrow("list", "--json")) as JsonArray ?? throw new InvalidOperationException("ember list gave no list.");
            return json.OfType<JsonObject>().Select(o => new EmberPackage(S(o["name"]), "", S(o["version"]), S(o["author"]), S(o["description"]), "", Array.Empty<string>(),
                (o["imports"]?.AsArray() ?? new JsonArray()).Select(i => S(i)).ToList(), o["origin"]?.GetValue<string>(), o["verified"]?.GetValue<string>())).ToList();
        }

        /// <summary>`ember install &lt;spec&gt;` (a name, `name@version`, a `.fpk` file, `github:owner/repo[@ref]`).</summary>
        public static void Install(string spec) => RunOrThrow("install", spec);

        /// <summary>`ember remove &lt;name&gt;`.</summary>
        public static void Remove(string name) => RunOrThrow("remove", name);
    }
}
