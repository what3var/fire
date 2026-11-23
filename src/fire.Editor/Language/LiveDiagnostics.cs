using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using fire.Tooling;

namespace fire.Editor
{
    /// <summary>
    /// How spark checks its documents in the background (M3: the analyses are forge serve's - `diagnose`, also for every file of a whole project
    /// in the context of its plan; the logic of the analysis is native/forge/serve_diag.c): the gate of the analyses (one at a time, the document typed in first), and the results
    /// remembered by their inputs.
    /// </summary>
    public static class LiveDiagnostics
    {
        /// <summary>The analyses of the editor run one at a time (whatever the number of open documents): the checks of the documents share this gate (BackgroundCheck) - the
        /// document the user types in first (<see cref="ActivePriority"/>), then the other open ones, then the checks of whole projects.</summary>
        public static readonly CheckGate Gate = new();

        public const int ActivePriority = 0, OpenPriority = 1, ProjectPriority = 2;

        private static long _epoch;

        /// <summary>Forgets the remembered results (the installed packages or the workspace changed: the same texts may have other errors now).</summary>
        public static void Invalidate() => Interlocked.Increment(ref _epoch);

        private sealed record CachedResult(string Source, IReadOnlyList<(string Path, string Text)> Inputs, string Settings, long Epoch, List<Diagnostic> Result);

        private static readonly Dictionary<string, CachedResult> Results = new(StringComparer.Ordinal);

        /// <summary>How often a remembered result was used / computed anew (the timing log, the tests).</summary>
        public static int CacheHits, CacheMisses;

        private static bool SameInputs(IReadOnlyList<(string Path, string Text)> a, IReadOnlyList<(string Path, string Text)> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!string.Equals(a[i].Path, b[i].Path, StringComparison.Ordinal) || !string.Equals(a[i].Text, b[i].Text, StringComparison.Ordinal)) return false;
            return true;
        }

        /// <summary>The result of `compute` for these inputs: remembered under `key` (a document, a project) and given again as long as the source, every input text and the settings
        /// are the same - opening a tab again, an undo back to the checked text, a check of all open documents after a change elsewhere that does not concern them cost nothing.</summary>
        public static List<Diagnostic> Remembered(string key, string source, IReadOnlyList<(string Path, string Text)> inputs, string settings, Func<List<Diagnostic>> compute)
        {
            long epoch = Interlocked.Read(ref _epoch);
            lock (Results)
            {
                if (Results.TryGetValue(key, out var known) && known.Epoch == epoch && string.Equals(known.Source, source, StringComparison.Ordinal)
                    && string.Equals(known.Settings, settings, StringComparison.Ordinal) && SameInputs(known.Inputs, inputs))
                {
                    CacheHits++;
                    return new List<Diagnostic>(known.Result);
                }
                CacheMisses++;
            }
            var result = compute();
            lock (Results) Results[key] = new CachedResult(source, inputs, settings, epoch, result);
            return new List<Diagnostic>(result);
        }

        /// <summary>What an analysis with `plan` depends on besides the document and the other files: the files of the libraries, and the settings.</summary>
        public static (List<(string Path, string Text)> Libraries, string Settings) PlanInputs(fire.Projects.BuildPlan? plan)
        {
            var libraries = new List<(string, string)>();
            if (plan == null) return (libraries, "");
            foreach (var library in plan.Libraries.Values.OrderBy(l => l.ImportName, StringComparer.OrdinalIgnoreCase))
                foreach (var file in library.Sources) libraries.Add((file.Path, file.Text));
            string settings = $"{plan.Settings?.TypeChecking}|{plan.Type}|{plan.Project.Project.ImportName}|{string.Join(",", plan.Packages.Select(p => p.Package + "@" + p.Version))}";
            return (libraries, settings);
        }

        /// <summary>The errors of all the files of a project (each with its file), so that the files that are not open in the editor show their errors in the error list
        /// too: every file analysed by forge serve in the context of the plan (`AnalyzeInProject` - what the editor does for an open document), its errors with its
        /// path, the warnings of the libraries once. Null: forge serve could not be asked.</summary>
        public static List<Diagnostic>? AnalyzeProjectFiles(fire.Projects.BuildPlan plan, IReadOnlyList<(string Path, string Text)> files, CancellationToken cancel = default)
        {
            var found = new List<Diagnostic>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (path, text) in files)
            {
                if (cancel.IsCancellationRequested) break;
                var others = files.Where(f => !string.Equals(f.Path, path, StringComparison.Ordinal)).ToList();
                var items = ServeDocuments.AnalyzeInProject(path, text, Path.GetDirectoryName(path), plan, others);
                if (items == null) return null;
                foreach (var d in items)
                {
                    var withFile = d.File == null ? d with { File = path } : d;
                    if (seen.Add($"{withFile.File}|{withFile.Line}|{withFile.Column}|{withFile.Code}|{withFile.Message}")) found.Add(withFile);
                }
            }
            return found.OrderBy(d => d.File ?? "", StringComparer.Ordinal).ThenBy(d => d.Line).ToList();
        }
    }
}
