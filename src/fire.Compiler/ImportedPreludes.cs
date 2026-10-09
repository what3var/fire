using fire.Bytecode;
using fire.Package.Manager;
using fire.Runtime;
using fire.Terminal.Bridge;
using fire.UI.Bridge;
using fire.Windows.Bridge;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace fire.Compiler
{
    /// <summary>
    /// The extensions that can be switched on via `#import "name"` and their preludes
    /// (in addition to the always present standard prelude, see
    /// fire.Standard.Prelude): each brings fire source (classes like
    /// `Framebuffer`/`Device`) AND native functions. EXACTLY ONE place
    /// that knows which extensions exist and what they
    /// contribute to the program - the real compiler (see Linker.CompileAndLink) and the
    /// live diagnostics of the editor (see Editor.LiveDiagnostics) both use
    /// this class and thus ALWAYS stay in step. Without that,
    /// live diagnostics would squiggle every class/function of an extension as
    /// "unknown", although the program is perfectly fine in
    /// real compiling.
    /// </summary>
    public static class ImportedPreludes
    {
        /// <summary>Translates the name from `#import "name"` (case
        /// does not matter) into the key from <see cref="NativeImports"/>, or throws
        /// - like the real compiler - for an unknown extension.</summary>
        public static string ParseImportName(string name) => name.ToLowerInvariant() switch
        {
            "graphics" => NativeImports.Graphics,
            "windows" => NativeImports.Windows,
            "ui" => NativeImports.Ui,
            "linq" => NativeImports.Linq,
            "reflection" => NativeImports.Reflection,
            _ => PackageStore.Default.FindImport(name)?.Key
                 ?? throw new PreprocessorException(StandardPackages.Bridges.Contains(name.ToLowerInvariant())
                     ? $"The standard package '{StandardPackages.PackageNameOf(name.ToLowerInvariant())}' for `#import \"{name}\"` is not installed (start ember or spark once, or `ember install {StandardPackages.PackageNameOf(name.ToLowerInvariant())}`)."
                     : $"'{name}' is not a known extension (installed packages: `ember list`, available ones: `ember find`; a library project of the solution needs a reference in the project file)."),
        };

        /// <summary>The fire source of the prelude of the extension `importName`
        /// (name from `#import "name"`, case does not matter), null for
        /// an unknown extension.</summary>
        /// <summary>The extensions that `importKey` (key from <see cref="NativeImports"/>) brings along itself: `ui` builds on
        /// `graphics` and switches it on as well. Every place that evaluates an `#import` enters all keys from it.</summary>
        public static IEnumerable<string> WithDependencies(string importKey) => WithDependencies(importKey, new HashSet<string>());

        private static IEnumerable<string> WithDependencies(string importKey, HashSet<string> visited)
        {
            if (importKey.StartsWith(PackageStore.KeyPrefix, StringComparison.Ordinal))
            {
                // an import of a package: what it requires (imports of the compiler or of packages) comes with it
                if (!visited.Add(importKey)) yield break;
                if (PackageStore.Default.FindKey(importKey) is { } package)
                    foreach (var required in package.Import.Requires)
                        foreach (var key in WithDependencies(ParseImportName(required), visited)) yield return key;
                yield return importKey;
                yield break;
            }
            if (importKey == NativeImports.Windows) yield return NativeImports.Graphics; // Window shows a framebuffer
            if (importKey == NativeImports.Ui) { yield return NativeImports.Graphics; yield return NativeImports.Windows; yield return NativeImports.Reflection; } // Styles and triggers set properties by name
            if (importKey == NativeImports.Linq) yield return NativeImports.Reflection; // SelectProperty/SelectField work with selectors
            yield return importKey;
        }

        public static string? TrySourceFor(string importName) => importName.ToLowerInvariant() switch
        {
            "graphics" => GraphicsBridge.PreludeSource,
            "windows" => WindowsBridge.PreludeSource,
            "ui" => UiBridge.PreludeSource,
            "linq" => fire.Standard.LinqPrelude.Source,
            "reflection" => fire.Standard.ReflectionPrelude.Source,
            _ => importName.StartsWith(PackageStore.KeyPrefix, StringComparison.Ordinal)
                ? PackageStore.Default.FindKey(importName)?.ReadPrelude()   // the key of an import of a package (what the editor works with)
                : PackageStore.Default.FindImport(importName)?.ReadPrelude(),
        };

        private static readonly Regex ImportDirective =
            new("^[ \\t]*#import[ \\t]+\"([^\"\\r\\n]+)\"", RegexOptions.Compiled | RegexOptions.Multiline);

        /// <summary>The names of all `#import "name"` lines in `source` (recognised purely
        /// TEXTUALLY, without bothering the preprocessor - for the
        /// editor, which works on unprocessed, possibly just freshly typed text).
        /// Unknown names are included - check with
        /// <see cref="TrySourceFor"/>.</summary>
        public static IEnumerable<string> FindImportNames(string source, ISet<string>? symbols = null) =>
            ImportDirective.Matches(ConditionalSymbols.Apply(source, symbols ?? ConditionalSymbols.For(null)))
                .Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase);

        /// <summary>Sets the preludes of all extensions contained in `nativeImports`
        /// (each preprocessed by `preprocess`) directly
        /// BEHIND the standard prelude (index 0) in `processedSources` and
        /// registers their native functions (as placeholders without
        /// effect - only the NAMES count for compiling, the real
        /// implementations are attached only by RuntimeSession.Build, see there)
        /// in `natives`. Returns the number of inserted preludes.</summary>
        public static int Insert(
            IReadOnlySet<string> nativeImports, NativeRegistry natives,
            List<ProcessedSource> processedSources, Func<string, ProcessedSource> preprocess)
        {
            int inserted = 0;

            if (nativeImports.Contains(NativeImports.Graphics))
            {
                processedSources.Insert(1, preprocess(GraphicsBridge.PreludeSource));
                GraphicsBridge.RegisterStubs(natives);
                inserted++;
            }

            if (nativeImports.Contains(NativeImports.Windows))
            {
                // behind `graphics`: the prelude order (every insertion stands BEFORE the earlier ones) and that of the native functions are independent of each other
                processedSources.Insert(1, preprocess(WindowsBridge.PreludeSource));
                WindowsBridge.RegisterStubs(natives);
                inserted++;
            }

            if (nativeImports.Contains(NativeImports.Ui))
            {
                // pure fire source on the classes of the graphics bridge: no native functions
                processedSources.Insert(1, preprocess(UiBridge.PreludeSource));
                inserted++;
            }

            if (nativeImports.Contains(NativeImports.Linq))
            {
                // pure fire source, no native functions
                processedSources.Insert(1, preprocess(fire.Standard.LinqPrelude.Source));
                inserted++;
            }

            if (nativeImports.Contains(NativeImports.Reflection))
            {
                processedSources.Insert(1, preprocess(fire.Standard.ReflectionPrelude.Source));
                ReflectionNatives.Register(natives); // when translating only the names count (the compiler then writes type metadata along)
                inserted++;
            }

            // the imports of packages (last, like their natives): the prelude, and the natives as names (the calls need them; the virtual machine fails when one runs, see PackageImports)
            foreach (var key in nativeImports.Where(k => k.StartsWith(PackageStore.KeyPrefix, StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal))
            {
                var package = PackageStore.Default.FindKey(key) ?? throw new Exception($"The import '{key.Substring(PackageStore.KeyPrefix.Length)}' belongs to a package that is not installed.");
                if (package.ReadPrelude() is { } prelude) { processedSources.Insert(1, preprocess(prelude)); inserted++; }
                PackageImports.RegisterNames(natives, package);
            }

            return inserted;
        }
    }
}
