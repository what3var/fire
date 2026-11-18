using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using fire.Bytecode;
using fire.Compiler;
using fire.Parsing;
using fire.Resolving;
using fire.Runtime;

namespace fire.Editor
{
    /// <summary>
    /// Live error analysis for the editor: runs the parser, resolver AND compiler
    /// (in this order, as in real compiling) on the current
    /// editor content and converts the errors that occur
    /// into diagnostics with line information.
    ///
    /// The resolver and compiler do NOT stop at the first error, but
    /// collect all further ones (see ResolverException/CompilerException) -
    /// each of them ends up as a diagnostic of its own in the result. The compiler runs
    /// only if the resolver found no error (it needs its
    /// result, a run over an unresolved AST would only produce
    /// follow-up errors). The PARSER, on the other hand, still reports only the
    /// first error: it does not resume after a syntax error,
    /// a second reported error would thus only be a follow-up error of the
    /// first.
    ///
    /// Uses - like DebugSession/RuntimeSession.Build - ParseMultiple
    /// (prelude + user code as ONE combined program), so that
    /// List/IEnumerable/IndexOutOfBoundsException are known, AND the
    /// preludes of the extensions switched on via `#import` (see
    /// ImportedPreludes): `#import "graphics"` makes Framebuffer/Console/
    /// Window known, `#import "devices"` the device classes - just as in
    /// real compiling, otherwise every use of them would be squiggled as
    /// "unknown class"/"unknown identifier".
    /// IMPORTANT: the line numbers reported by the parser/resolver
    /// nevertheless stay correctly related to the USER source text, not
    /// shifted onto the preludes - every program part is created internally via an
    /// OWN lexer/parser instance with line counting starting at 1 for
    /// EXACTLY this part (see Parser.ParseRaw), the line number
    /// on every AST node stays unchanged afterwards, regardless of
    /// how the parts are subsequently joined into
    /// a list.
    /// </summary>
    public static class LiveDiagnostics
    {
        public static List<Diagnostic> Analyze(string source, string? basePath = null) => Analyze(source, Array.Empty<string>(), basePath, null, null);

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

        /// <summary>Thrown when one of the other files of the project cannot be read by the parser right now: the analysis is repeated without them.</summary>
        private sealed class OtherFileBroken : Exception { }

        /// <summary>`extraImports`: names of extensions (as in `#import
        /// "name"`) that count as switched on in addition to those occurring in `source`
        /// itself - see AnalyzeInProject.
        /// Unknown names are ignored.</summary>
        /// <param name="otherFiles">The other files of the project (path, text): compiled along like in a real build, so that their classes, namespaces and members are
        /// known; only the errors of `source` are reported.</param>
        private static List<Diagnostic> Analyze(string source, IEnumerable<string> extraImports, string? basePath, fire.Projects.BuildPlan? plan,
            IReadOnlyList<(string Path, string Text)>? otherFiles, bool allFiles = false)
        {
            var diagnostics = new List<Diagnostic>();
            if (!allFiles && string.IsNullOrWhiteSpace(source)) return diagnostics;
            var projectFiles = allFiles ? new HashSet<string>(otherFiles!.Select(o => o.Path), StringComparer.Ordinal) : null;
            var processed = new List<ProcessedSource>();
            int documentSource = -1;   // the index of `source` among the sources of the program
            // the files of the libraries are named "Library/file.fire" in the program: their diagnostics get the path of the file (the error list opens it, and knows it when it is open)
            var libraryPaths = new Dictionary<string, string>(StringComparer.Ordinal);

            // the warnings of the code (`warn 123: "text"`, collected while the parser reads it) are diagnostics, too
            var warnings = new DiagnosticCollector();
            using var collecting = warnings.Begin();
            try
            {
                var natives = NativeRegistry.CreateDefault();
                var alreadyIncluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var cwd = basePath ?? System.IO.Directory.GetCurrentDirectory();
                var nativeImports = new HashSet<string>();
                var projectImports = new List<string>();   // libraries of the project that are imported
                ProjectNativeOverlay.Register(plan);
                if (ProjectNativeOverlay.OwnKey(plan) is { } ownNativeKey) nativeImports.Add(ownNativeKey);
                // as in the linker: a library is its own import (`#import "testing"` in the tests of `testing` means its files), and a standard library of an import the
                // compiler knows (`graphics`, `windows`, `reflection`, ...) has the natives of the virtual machine, without the installed prelude
                string? ownImport = plan?.Type == fire.Projects.OutputType.Library ? plan.Project.Project.ImportName : null;
                bool IsOwn(string name) => ownImport != null && string.Equals(name, ownImport, StringComparison.OrdinalIgnoreCase);
                var ownBuiltIn = new HashSet<string>();
                if (ownImport != null && Linker.BuiltInKey(ownImport) is { } ownKey) { ownBuiltIn.Add(ownKey); nativeImports.Add(ownKey); }
                foreach (var name in extraImports)
                {
                    if (IsOwn(name)) continue;
                    if (ProjectLibraries.TryImport(plan, name, projectImports, nativeImports)) continue;
                    try { foreach (var key in ImportedPreludes.WithDependencies(ImportedPreludes.ParseImportName(name))) nativeImports.Add(key); }
                    catch (Exception) { /* unknown extension - reports its own file */ }
                }

                // The same registry as in REAL compiling (see
                // RuntimeSession.CreateProjectDirectiveRegistry) - otherwise
                // e.g. '#import "graphics"' would be wrongly squiggled here as an
                // unknown preprocessor directive, although
                // it is long accepted when actually executing.
                // The callback remembers the switched-on extensions,
                // whose preludes are inserted below.
                var registry = RuntimeSession.CreateProjectDirectiveRegistry(name => nativeImports.Add(name), tryLibrary: name => IsOwn(name) || ProjectLibraries.TryImport(plan, name, projectImports, nativeImports),
                    unknownHint: name => ProjectLibraries.UnknownHint(plan, name), libraryNamespace: name => ProjectLibraries.NamespaceOf(plan, name));
                processed.Add(Preprocessor.Process(fire.Standard.Prelude.Source, cwd, alreadyIncluded, registry));
                // the other files of the project come before the document (each from its own folder; one that cannot be preprocessed right now is left out)
                foreach (var (otherPath, otherText) in otherFiles ?? Array.Empty<(string, string)>())
                {
                    if (string.IsNullOrWhiteSpace(otherText)) continue;
                    try
                    {
                        var other = Preprocessor.Process(otherText, System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(otherPath)) ?? cwd, alreadyIncluded, registry);
                        processed.Add(other with { Name = otherPath });
                    }
                    catch (Exception ex)
                    {
                        // broken right now: its errors are its own (reported when all the files of the project are checked)
                        if (allFiles) diagnostics.AddRange(CompileDiagnostics.Of(ex, otherPath).Select(d => d with { File = otherPath }));
                    }
                }
                if (!allFiles) processed.Add(Preprocessor.Process(source, cwd, alreadyIncluded, registry));
                // the libraries of the project that this document (or another file of the project) imports: their files are known to the analysis like in a real build
                var libraryFiles = plan != null && projectImports.Count > 0
                    ? ProjectLibraries.Process(plan, projectImports, file => Preprocessor.Process(file.Text, file.Directory, alreadyIncluded, registry))
                    : new List<(ProcessedSource Source, string Path)>();

                // Only AFTER preprocessing ALL sources is it known which
                // extensions are switched on - as in the linker.
                ImportedPreludes.Insert(
                    nativeImports, natives, processed,
                    (preludeSource, folder) => Preprocessor.Process(preludeSource, folder, alreadyIncluded, registry), ownBuiltIn);
                processed.InsertRange(1, libraryFiles.Select(l => l.Source));
                foreach (var (librarySource, libraryPath) in libraryFiles)
                    if (librarySource.Name != null) libraryPaths[librarySource.Name] = libraryPath;
                documentSource = allFiles ? -1 : processed.Count - 1;

                List<fire.Ast.Stmt> program;
                try { program = Parser.ParseMultiple(processed); }
                catch (ParseException pe) when (allFiles)
                {
                    if (pe.FileName != null && projectFiles!.Contains(pe.FileName)) diagnostics.AddRange(CompileDiagnostics.Of(pe));
                    return Sorted(diagnostics, warnings, projectFiles, libraryPaths);
                }
                catch (LexException) when (allFiles)
                {
                    // the lexer does not know the file: the one whose text it cannot read
                    foreach (var p in processed.Where(p => p.Name != null && projectFiles!.Contains(p.Name)))
                    {
                        try { new Lexer(p.Source).Tokenize(); }
                        catch (LexException le) { diagnostics.AddRange(CompileDiagnostics.Of(le, p.Name)); break; }
                    }
                    return Sorted(diagnostics, warnings, projectFiles, libraryPaths);
                }
                catch (ParseException pe) when (otherFiles is { Count: > 0 } && pe.FileName != null) { throw new OtherFileBroken(); }
                catch (LexException) when (otherFiles is { Count: > 0 }) { throw new OtherFileBroken(); }
                var resolveResult = Resolver.Resolve(program, natives.Names);
                // type conflicts (docs/spec/TYPES.md): the code of the document is the last source; warnings in lax mode, errors in strict mode (the project's setting, `#lax` / `#strict` in the file)
                var typeMode = string.Equals(plan?.Settings?.TypeChecking, "strict", StringComparison.OrdinalIgnoreCase) ? TypeCheckMode.Strict : TypeCheckMode.Lax;
                // (all the files of a project: the conflicts of the type check do not know their file - only the documents that are open show them)
                if (!allFiles) diagnostics.AddRange(TypeChecker.Check(program, resolveResult, typeMode, processed.Count - 1));
                Compiler.Compiler.Compile(program, resolveResult, natives);
            }
            catch (OtherFileBroken)
            {
                return Analyze(source, extraImports, basePath, plan, null);
            }
            catch (Exception ex)
            {
                // Deliberately caught broadly (like DebugSession.RunGuarded): live diagnostics INEVITABLY run again and again over short-lived, still broken intermediate states while
                // typing - EVERY kind of exception ends up here as a diagnostic (CompileDiagnostics.Of knows the exceptions of the compiler and takes their line and code; anything
                // else is a general error in line 1), instead of ending uncaught in the DispatcherTimer tick and dragging the whole editor application down with it.
                // The resolver and the compiler report ALL their errors, the parser (which does not resume after a syntax error) and the others one.
                // with the other files of the project: only the errors in the document (the last source); an error whose source is unknown stays
                if (allFiles)
                {
                    // every error in a file of the project, with its file (the errors in the preludes and the libraries are theirs)
                    string? FileOf(int index) => index >= 0 && index < processed.Count && processed[index].Name is { } n && projectFiles!.Contains(n) ? n : null;
                    if (ex is ResolverException re)
                        diagnostics.AddRange(re.Errors.Where(e => FileOf(e.Source) != null).Select(e => new Diagnostic(e.Line, e.RawMessage, DiagnosticSeverity.Error, e.Code, FileOf(e.Source))));
                    else if (ex is CompilerException ce)
                        diagnostics.AddRange(ce.Errors.Where(e => FileOf(e.Source) != null).Select(e => new Diagnostic(e.Line, e.RawMessage, DiagnosticSeverity.Error, e.Code, FileOf(e.Source))));
                    else if (ex is ParseException pe && pe.FileName != null && projectFiles!.Contains(pe.FileName)) diagnostics.AddRange(CompileDiagnostics.Of(pe));
                }
                else if (otherFiles is { Count: > 0 } && ex is ResolverException or CompilerException)
                {
                    int doc = documentSource;
                    var own = ex is ResolverException re
                        ? re.Errors.Where(e => e.Source < 0 || e.Source == doc).Select(e => new Diagnostic(e.Line, e.RawMessage, DiagnosticSeverity.Error, e.Code)).ToList()
                        : ((CompilerException)ex).Errors.Where(e => e.Source < 0 || e.Source == doc).Select(e => new Diagnostic(e.Line, e.RawMessage, DiagnosticSeverity.Error, e.Code)).ToList();
                    diagnostics.AddRange(own);
                }
                else diagnostics.AddRange(CompileDiagnostics.Of(ex));
            }
            return Sorted(diagnostics, warnings, projectFiles, libraryPaths, otherFiles);
        }

        private static List<Diagnostic> Sorted(List<Diagnostic> diagnostics, DiagnosticCollector warnings, HashSet<string>? projectFiles, Dictionary<string, string> libraryPaths,
            IReadOnlyList<(string Path, string Text)>? otherFiles = null)
        {
            // (warnings of the other files carry their file name; all the files of a project: the warnings in them)
            if (projectFiles != null) diagnostics.AddRange(warnings.Items.Where(w => w.File != null && projectFiles.Contains(w.File)));
            else diagnostics.AddRange(warnings.Items.Where(w => w.File == null || otherFiles == null || !otherFiles.Any(o => o.Path == w.File)));

            // Resolver/compiler errors come in the order of resolution
            // (e.g. first all classes, then the top-level code), not in
            // line order - sort by line for the error view
            // (stable, the same line keeps the order).
            return diagnostics.Select(d => d.File != null && libraryPaths.TryGetValue(d.File, out var path) ? d with { File = path } : d)
                .OrderBy(d => d.File ?? "", StringComparer.Ordinal).ThenBy(d => d.Line).ToList();
        }

        /// <summary>The errors of all the files of a project at once (each with its file): one compile of the project, as in a build - the files that are not open in the editor
        /// show their errors in the error list, too. The parser stops at the first error (in whichever file), the resolver and the compiler report all theirs; the conflicts of the
        /// type check are left out (they do not know their file - an open document shows them).</summary>
        public static List<Diagnostic> AnalyzeProjectFiles(IReadOnlyList<(string Path, string Text)> files, fire.Projects.BuildPlan? plan, string? basePath = null)
        {
            if (files.Count == 0) return new List<Diagnostic>();
            var imports = files.Where(f => !string.IsNullOrWhiteSpace(f.Text)).SelectMany(f => ImportedPreludes.FindImportNames(f.Text)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return Analyze("", imports, basePath, plan, files, allFiles: true);
        }

        // what the resolver says about a name that it does not know: a class or type, an exception type in `catch`, a base class or interface after the `:` of a class
        private static readonly Regex UnknownClassOrType =
            new(@"^(?:Unknown (?:class|type|exception type) '(?<name>[^']+)'|'(?<name>[^']+)' of class '[^']+' is neither a known class nor a known interface)", RegexOptions.Compiled);

        /// <summary>The diagnostics of `source` as a file of a project: the other files of the project are compiled along (each a source of its own, before the
        /// document), so that their classes, namespaces, members and `#import`s are known as in a real build; the errors of the resolver and the compiler carry the
        /// source they are in, and only those of the document are reported. If one of the other files cannot be parsed right now, the document is analysed alone
        /// and an "unknown class/type 'X'" is dropped when one of the other files declares X (ScriptSymbolIndex).</summary>
        public static List<Diagnostic> AnalyzeInProject(string source, IReadOnlyList<(string Path, string Text)> otherFiles, string? basePath = null, fire.Projects.BuildPlan? plan = null)
        {
            var otherProjectFiles = otherFiles.Select(f => f.Text).ToList();
            var otherPaths = otherFiles.Select(f => f.Path).ToList();
            var importsElsewhere = otherProjectFiles
                .Where(other => !string.IsNullOrWhiteSpace(other))
                .SelectMany(i => ImportedPreludes.FindImportNames(i))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var diagnostics = Analyze(source, importsElsewhere, basePath, plan, otherFiles);
            if (diagnostics.Count == 0 || otherProjectFiles.Count == 0) return diagnostics;

            var knownElsewhere = new HashSet<string>();
            for (int k = 0; k < otherProjectFiles.Count; k++)
            {
                string other = otherProjectFiles[k], path = otherPaths[k];
                if (string.IsNullOrWhiteSpace(other)) continue;
                ScriptSymbolIndex index;
                try { index = ScriptSymbolIndex.OfProjectFile(path, other); }   // (kept while the file does not change: not built again for every check)
                catch { continue; }
                // The name can be qualified in `source` (`Geometry.Circle`) OR -
                // via the current namespace/a `#using` - simple (`Circle`)
                // written.
                foreach (var cls in index.Classes.Values)
                {
                    knownElsewhere.Add(cls.Name);
                    knownElsewhere.Add(cls.SimpleName);
                }
                foreach (var enumName in index.EnumMembers.Keys)
                {
                    knownElsewhere.Add(enumName);
                    knownElsewhere.Add(enumName.Substring(enumName.LastIndexOf('.') + 1));
                }
            }
            if (knownElsewhere.Count == 0) return diagnostics;

            return diagnostics.Where(d =>
            {
                var match = UnknownClassOrType.Match(d.Message);
                return !(match.Success && knownElsewhere.Contains(match.Groups["name"].Value));
            }).ToList();
        }
    }
}
