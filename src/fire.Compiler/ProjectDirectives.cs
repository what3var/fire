using fire.Bytecode;
using fire.Parsing;
using fire.Values;

namespace fire.Compiler
{
    /// <summary>The directives of a program the compiler knows beyond the built-in ones (`#import`) - for the Linker and the live diagnostics of spark alike (it was
    /// RuntimeSession.CreateProjectDirectiveRegistry before M1).</summary>
    public static class ProjectDirectives
    {
        /// <summary>Builds the DirectiveRegistry that real programs (see
        /// Build) AND the live diagnostics (see Editor.LiveDiagnostics)
        /// use alike - exactly ONE place that knows which
        /// user-defined preprocessor directives exist (currently:
        /// `#import "extension"`, see ImportedPreludes), so that both ALWAYS
        /// stay in step. Without that, a directive recognised here would
        /// continue to be wrongly squiggled in the live diagnostics as "unknown directive",
        /// although it is long
        /// accepted in real compiling (the preprocessor leaves every directive NOT
        /// registered here unchanged in the text, the parser
        /// then does not know it either and throws, see Parsing.
        /// Preprocessor class documentation/Parser.ParseDirective).
        ///
        /// `onImport`: callback for a RECOGNISED `#import "name"`
        /// directive, with the key of the extension from NativeImports
        /// (e.g. to actually load the graphics bridge, or - in the
        /// live diagnostics - to insert its prelude) - `null` if
        /// the caller only wants to know "is this syntactically a valid
        /// directive", without triggering its actual effect. An UNKNOWN extension (`#import
        /// "nonsense"`) still throws - that is a REAL error, not
        /// merely "the live diagnostics just do not know it (yet)".</summary>
        public static DirectiveRegistry CreateRegistry(Action<string>? onImport = null, IEnumerable<string>? defines = null, Func<string, bool>? tryLibrary = null, Func<string, string?>? unknownHint = null, Func<string, string?>? libraryNamespace = null)
        {
            var registry = new DirectiveRegistry(); // completely empty, NOT CreateDefault()
            foreach (var symbol in ConditionalSymbols.For(null, ConditionalSymbols.DefaultEngine, null, defines)) registry.Symbols.Add(symbol); // `#if windows`: the machine the VM runs on
            registry.Register("import", 1, (ctx, args, line) =>
            {
                if (args[0].Kind == ValueKind.String)
                {
                    if (tryLibrary?.Invoke(args[0].AsString()) == true)
                    {
                        // a library of the project (handled by the caller): its names are found by the short name in this file too
                        if (libraryNamespace?.Invoke(args[0].AsString()) is { } ns) ctx.AddImportNamespace(ns);
                        return null;
                    }
                    foreach (var key in ProjectLibraries.ImportKeys(args[0].AsString(), unknownHint)) onImport?.Invoke(key);   // (unknown: names the libraries of the project)
                    if (ProjectLibraries.PackageNamespaceOf(args[0].AsString()) is { } packageNamespace) ctx.AddImportNamespace(packageNamespace);
                    return null;
                }
                throw new Exception($"Wrong arguments for the 'import' directive.");
            });
            return registry;
        }
    }
}
