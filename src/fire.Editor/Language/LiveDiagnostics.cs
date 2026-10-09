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
    public enum DiagnosticSeverity
    {
        Error,
    }

    public sealed record Diagnostic(int Line, string Message, DiagnosticSeverity Severity = DiagnosticSeverity.Error)
    {
        public override string ToString() => $"Line {Line}: {Message}";
    }

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
        public static List<Diagnostic> Analyze(string source, string? basePath = null) => Analyze(source, Array.Empty<string>(), basePath, null);

        /// <summary>`extraImports`: names of extensions (as in `#import
        /// "name"`) that count as switched on in addition to those occurring in `source`
        /// itself - see AnalyzeInProject.
        /// Unknown names are ignored.</summary>
        private static List<Diagnostic> Analyze(string source, IEnumerable<string> extraImports, string? basePath, fire.Projects.BuildPlan? plan)
        {
            var diagnostics = new List<Diagnostic>();
            if (string.IsNullOrWhiteSpace(source)) return diagnostics;

            try
            {
                var natives = NativeRegistry.CreateDefault();
                var alreadyIncluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var cwd = basePath ?? System.IO.Directory.GetCurrentDirectory();
                var nativeImports = new HashSet<string>();
                var projectImports = new List<string>();   // libraries of the project that are imported
                ProjectNativeOverlay.Register(plan);
                if (ProjectNativeOverlay.OwnKey(plan) is { } ownNativeKey) nativeImports.Add(ownNativeKey);
                foreach (var name in extraImports)
                {
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
                var registry = RuntimeSession.CreateProjectDirectiveRegistry(name => nativeImports.Add(name), tryLibrary: name => ProjectLibraries.TryImport(plan, name, projectImports, nativeImports));
                var processed = new List<ProcessedSource>();
                foreach (var s in new[] { fire.Standard.Prelude.Source, source })
                    processed.Add(Preprocessor.Process(s, cwd, alreadyIncluded, registry));
                // the libraries of the project that this document (or another file of the project) imports: their files are known to the analysis like in a real build
                var libraryFiles = plan != null && projectImports.Count > 0
                    ? ProjectLibraries.Process(plan, projectImports, file => Preprocessor.Process(file.Text, file.Directory, alreadyIncluded, registry))
                    : new List<(ProcessedSource Source, string Path)>();

                // Only AFTER preprocessing ALL sources is it known which
                // extensions are switched on - as in the linker.
                ImportedPreludes.Insert(
                    nativeImports, natives, processed,
                    preludeSource => Preprocessor.Process(preludeSource, cwd, alreadyIncluded, registry));
                processed.InsertRange(1, libraryFiles.Select(l => l.Source));

                var program = Parser.ParseMultiple(processed);
                var resolveResult = Resolver.Resolve(program, natives.Names);
                Compiler.Compiler.Compile(program, resolveResult, natives);
            }
            catch (ParseException ex)
            {
                diagnostics.Add(new Diagnostic(ex.Line, ex.Message));
            }
            catch (ResolverException ex)
            {
                foreach (var error in ex.Errors)
                    diagnostics.Add(new Diagnostic(error.Line, error.Message));
            }
            catch (CompilerException ex)
            {
                foreach (var error in ex.Errors)
                    diagnostics.Add(new Diagnostic(error.Line, error.Message));
            }
            catch (PreprocessorException ex)
            {
                // No Line field present (see the class comment there,
                // concerns #include/#extern directives, which are processed before any
                // line bookkeeping of the actual lexer happens) - line 1 as the best
                // possible placeholder, so that the
                // error view still shows something clickable/visible.
                diagnostics.Add(new Diagnostic(1, ex.Message));
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
            {
                // Error without line information of its own that did not come via the
                // compiler (which reports via CompilerException WITH a line) -
                // likewise line 1 as a placeholder.
                diagnostics.Add(new Diagnostic(1, ex.Message));
            }
            catch (Exception ex)
            {
                // Deliberately caught broadly (like DebugSession.RunGuarded):
                // live diagnostics INEVITABLY run again and again over short-
                // lived, still broken intermediate states while typing -
                // EVERY kind of unexpected exception ends up here as a generic
                // diagnostic, instead of ending uncaught in the DispatcherTimer tick
                // and dragging the whole editor application down with it.
                diagnostics.Add(new Diagnostic(1, ex.Message));
            }

            // Resolver/compiler errors come in the order of resolution
            // (e.g. first all classes, then the top-level code), not in
            // line order - sort by line for the error view
            // (stable, the same line keeps the order).
            return diagnostics.OrderBy(d => d.Line).ToList();
        }

        // what the resolver says about a name that it does not know: a class or type, an exception type in `catch`, a base class or interface after the `:` of a class
        private static readonly Regex UnknownClassOrType =
            new(@"^(?:Unknown (?:class|type|exception type) '(?<name>[^']+)'|'(?<name>[^']+)' of class '[^']+' is neither a known class nor a known interface)", RegexOptions.Compiled);

        /// <summary>Like Analyze(source), but suppresses diagnostics that arise ONLY
        /// because this analysis knows only `source` (+
        /// prelude), not the ENTIRE project (SPEC "Multiple
        /// source files") - a valid reference to a class that is defined in
        /// ONE OF the `otherProjectFiles` would otherwise
        /// wrongly be reported as "unknown class"/"unknown type".
        ///
        /// Deliberately NO complete, combined parser/resolver/
        /// compiler run over the ENTIRE project: a parse/resolve error
        /// carries no source index (unlike compiled bytecode, see
        /// Bytecode.Chunk.MarkLine) - an error in ANOTHER, currently
        /// broken file could then not be reliably told apart from a
        /// REAL error in `source` itself, so one would
        /// risk pinning a red error on the wrong file.
        /// Instead a TARGETED, low-risk addendum: Analyze(source)
        /// runs quite normally (finds EVERY real error in `source` itself
        /// reliably), and only for each resulting "unknown class/
        /// unknown type 'X'" diagnostic is it checked whether 'X' is defined as a class in ONE OF the
        /// `otherProjectFiles` (via
        /// ScriptSymbolIndex, the same lightweight scanner as for
        /// completion/navigation) - if so, the diagnostic was a false
        /// positive (the class is in fact known project-wide) and
        /// is removed. One of the `otherProjectFiles` with a
        /// typo of its own right now does not crash this check (it is
        /// simply skipped) - at most it influences whether ONE particular
        /// false-positive diagnostic remains overlooked, never the
        /// reliability of the real errors in `source` itself.
        ///
        /// Exception: an `#import "..."` in one of the `otherProjectFiles`
        /// IS actually counted - the extensions (and thus their
        /// preludes, see ImportedPreludes) apply in the real compiler to
        /// the whole project, not per file.</summary>
        public static List<Diagnostic> AnalyzeInProject(string source, IReadOnlyList<string> otherProjectFiles, string? basePath = null, fire.Projects.BuildPlan? plan = null)
        {
            var importsElsewhere = otherProjectFiles
                .Where(other => !string.IsNullOrWhiteSpace(other))
                .SelectMany(i => ImportedPreludes.FindImportNames(i))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var diagnostics = Analyze(source, importsElsewhere, basePath, plan);
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
