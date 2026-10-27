using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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

        /// <summary>Thrown when one of the other files of the project cannot be read by the parser right now: the analysis is repeated without them.</summary>
        private sealed class OtherFileBroken : Exception { }

        /// <summary>`extraImports`: names of extensions (as in `#import
        /// "name"`) that count as switched on in addition to those occurring in `source`
        /// itself - see AnalyzeInProject.
        /// Unknown names are ignored.</summary>
        /// <param name="otherFiles">The other files of the project (path, text): compiled along like in a real build, so that their classes, namespaces and members are
        /// known; only the errors of `source` are reported.</param>
        private static List<Diagnostic> Analyze(string source, IEnumerable<string> extraImports, string? basePath, fire.Projects.BuildPlan? plan,
            IReadOnlyList<(string Path, string Text)>? otherFiles)
        {
            var diagnostics = new List<Diagnostic>();
            if (string.IsNullOrWhiteSpace(source)) return diagnostics;
            int documentSource = -1;   // the index of `source` among the sources of the program

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
                var registry = RuntimeSession.CreateProjectDirectiveRegistry(name => nativeImports.Add(name), tryLibrary: name => IsOwn(name) || ProjectLibraries.TryImport(plan, name, projectImports, nativeImports));
                var processed = new List<ProcessedSource>();
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
                    catch (Exception) { /* broken right now: its errors are its own */ }
                }
                processed.Add(Preprocessor.Process(source, cwd, alreadyIncluded, registry));
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
                documentSource = processed.Count - 1;

                List<fire.Ast.Stmt> program;
                try { program = Parser.ParseMultiple(processed); }
                catch (ParseException pe) when (otherFiles is { Count: > 0 } && pe.FileName != null) { throw new OtherFileBroken(); }
                catch (LexException) when (otherFiles is { Count: > 0 }) { throw new OtherFileBroken(); }
                var resolveResult = Resolver.Resolve(program, natives.Names);
                // type conflicts (docs/TYPES.md): the code of the document is the last source; warnings in lax mode, errors in strict mode (the project's setting, `#lax` / `#strict` in the file)
                var typeMode = string.Equals(plan?.Settings?.TypeChecking, "strict", StringComparison.OrdinalIgnoreCase) ? TypeCheckMode.Strict : TypeCheckMode.Lax;
                diagnostics.AddRange(TypeChecker.Check(program, resolveResult, typeMode, processed.Count - 1));
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
                if (otherFiles is { Count: > 0 } && ex is ResolverException or CompilerException)
                {
                    int doc = documentSource;
                    var own = ex is ResolverException re
                        ? re.Errors.Where(e => e.Source < 0 || e.Source == doc).Select(e => new Diagnostic(e.Line, e.RawMessage, DiagnosticSeverity.Error, e.Code)).ToList()
                        : ((CompilerException)ex).Errors.Where(e => e.Source < 0 || e.Source == doc).Select(e => new Diagnostic(e.Line, e.RawMessage, DiagnosticSeverity.Error, e.Code)).ToList();
                    diagnostics.AddRange(own);
                }
                else diagnostics.AddRange(CompileDiagnostics.Of(ex));
            }
            // (warnings of the other files carry their file name)
            diagnostics.AddRange(warnings.Items.Where(w => w.File == null || otherFiles == null || !otherFiles.Any(o => o.Path == w.File)));

            // Resolver/compiler errors come in the order of resolution
            // (e.g. first all classes, then the top-level code), not in
            // line order - sort by line for the error view
            // (stable, the same line keeps the order).
            return diagnostics.OrderBy(d => d.Line).ToList();
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
            var importsElsewhere = otherProjectFiles
                .Where(other => !string.IsNullOrWhiteSpace(other))
                .SelectMany(i => ImportedPreludes.FindImportNames(i))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var diagnostics = Analyze(source, importsElsewhere, basePath, plan, otherFiles);
            if (diagnostics.Count == 0 || otherProjectFiles.Count == 0) return diagnostics;

            var knownElsewhere = new HashSet<string>();
            foreach (var other in otherProjectFiles)
            {
                if (string.IsNullOrWhiteSpace(other)) continue;
                ScriptSymbolIndex index;
                try { index = ScriptSymbolIndex.Build(other); }
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
