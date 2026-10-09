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
    /// Die per `#import "name"` zuschaltbaren Erweiterungen und ihre Preludes
    /// (zusätzlich zur immer vorhandenen Standard-Prelude, siehe
    /// fire.Standard.Prelude): jede bringt fire-Quelltext (Klassen wie
    /// `Framebuffer`/`Device`) UND native Funktionen mit. GENAU EINE Stelle,
    /// die weiß, welche Erweiterungen es gibt und was sie zum Programm
    /// beitragen - der echte Compiler (siehe Linker.CompileAndLink) und die
    /// Live-Diagnostik des Editors (siehe Editor.LiveDiagnostics) benutzen
    /// beide diese Klasse und bleiben so IMMER im Gleichschritt. Ohne das
    /// würde die Live-Diagnostik jede Klasse/Funktion einer Erweiterung als
    /// "unbekannt" unterkringeln, obwohl das Programm beim echten
    /// Kompilieren völlig in Ordnung ist.
    /// </summary>
    public static class ImportedPreludes
    {
        /// <summary>Übersetzt den Namen aus `#import "name"` (Groß-/Kleinschreibung
        /// egal) in den Schlüssel aus <see cref="NativeImports"/>, oder wirft
        /// - wie der echte Compiler - bei einer unbekannten Erweiterung.</summary>
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

        /// <summary>Der fire-Quelltext der Prelude der Erweiterung `importName`
        /// (Name aus `#import "name"`, Groß-/Kleinschreibung egal), null bei
        /// einer unbekannten Erweiterung.</summary>
        /// <summary>Die Erweiterungen, die `importKey` (Schlüssel aus <see cref="NativeImports"/>) selbst mitbringt: `ui` baut auf
        /// `graphics` auf und schaltet es mit zu. Jede Stelle, die ein `#import` auswertet, trägt alle Schlüssel daraus ein.</summary>
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
            if (importKey == NativeImports.Windows) yield return NativeImports.Graphics; // Window zeigt einen Framebuffer
            if (importKey == NativeImports.Ui) { yield return NativeImports.Graphics; yield return NativeImports.Windows; yield return NativeImports.Reflection; } // Styles und Trigger setzen Eigenschaften per Name
            if (importKey == NativeImports.Linq) yield return NativeImports.Reflection; // SelectProperty/SelectField arbeiten mit Selektoren
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

        /// <summary>Die Namen aller `#import "name"`-Zeilen in `source` (rein
        /// TEXTUELL erkannt, ohne den Präprozessor zu bemühen - für den
        /// Editor, der auf unverarbeitetem, evtl. gerade erst getipptem Text
        /// arbeitet). Unbekannte Namen sind enthalten - prüfen mit
        /// <see cref="TrySourceFor"/>.</summary>
        public static IEnumerable<string> FindImportNames(string source, ISet<string>? symbols = null) =>
            ImportDirective.Matches(ConditionalSymbols.Apply(source, symbols ?? ConditionalSymbols.For(null)))
                .Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase);

        /// <summary>Setzt die Preludes aller in `nativeImports` enthaltenen
        /// Erweiterungen (jeweils durch `preprocess` vorverarbeitet) direkt
        /// HINTER die Standard-Prelude (Index 0) in `processedSources` und
        /// registriert deren native Funktionen (als Platzhalter ohne
        /// Wirkung - nur die NAMEN zählen fürs Kompilieren, die echten
        /// Implementierungen hängt erst RuntimeSession.Build an, siehe dort)
        /// in `natives`. Liefert die Anzahl der eingefügten Preludes.</summary>
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
                // hinter `graphics`: die Prelude-Reihenfolge (jede Einfuegung steht VOR den frueheren) und die der nativen Funktionen sind unabhaengig voneinander
                processedSources.Insert(1, preprocess(WindowsBridge.PreludeSource));
                WindowsBridge.RegisterStubs(natives);
                inserted++;
            }

            if (nativeImports.Contains(NativeImports.Ui))
            {
                // reiner fire-Quelltext auf den Klassen der Grafik-Brücke: keine nativen Funktionen
                processedSources.Insert(1, preprocess(UiBridge.PreludeSource));
                inserted++;
            }

            if (nativeImports.Contains(NativeImports.Linq))
            {
                // reiner fire-Quelltext, keine nativen Funktionen
                processedSources.Insert(1, preprocess(fire.Standard.LinqPrelude.Source));
                inserted++;
            }

            if (nativeImports.Contains(NativeImports.Reflection))
            {
                processedSources.Insert(1, preprocess(fire.Standard.ReflectionPrelude.Source));
                ReflectionNatives.Register(natives); // beim Übersetzen zählen nur die Namen (der Compiler schreibt daraufhin Typ-Metadaten mit)
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
