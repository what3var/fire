using fire.Bytecode;
using fire.Device.Bridge;
using fire.IO.Bridge;
using fire.Runtime;
using fire.Terminal.Bridge;
using fire.UI.Bridge;
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
            "devices" => NativeImports.Devices,
            "io" => NativeImports.IO,
            "ui" => NativeImports.Ui,
            "linq" => NativeImports.Linq,
            "reflection" => NativeImports.Reflection,
            _ => throw new Exception($"'{name}' ist keine bekannte Erweiterung."),
        };

        /// <summary>Der fire-Quelltext der Prelude der Erweiterung `importName`
        /// (Name aus `#import "name"`, Groß-/Kleinschreibung egal), null bei
        /// einer unbekannten Erweiterung.</summary>
        /// <summary>Die Erweiterungen, die `importKey` (Schlüssel aus <see cref="NativeImports"/>) selbst mitbringt: `ui` baut auf
        /// `graphics` auf und schaltet es mit zu. Jede Stelle, die ein `#import` auswertet, trägt alle Schlüssel daraus ein.</summary>
        public static IEnumerable<string> WithDependencies(string importKey)
        {
            if (importKey == NativeImports.Ui) yield return NativeImports.Graphics;
            if (importKey == NativeImports.Linq) yield return NativeImports.Reflection; // SelectProperty/SelectField arbeiten mit Selektoren
            yield return importKey;
        }

        public static string? TrySourceFor(string importName) => importName.ToLowerInvariant() switch
        {
            "graphics" => GraphicsBridge.PreludeSource,
            "devices" => DeviceBridge.PreludeSource,
            "io" => IoBridge.PreludeSource,
            "ui" => UiBridge.PreludeSource,
            "linq" => fire.Standard.LinqPrelude.Source,
            "reflection" => fire.Standard.ReflectionPrelude.Source,
            _ => null,
        };

        private static readonly Regex ImportDirective =
            new("^[ \\t]*#import[ \\t]+\"([^\"\\r\\n]+)\"", RegexOptions.Compiled | RegexOptions.Multiline);

        /// <summary>Die Namen aller `#import "name"`-Zeilen in `source` (rein
        /// TEXTUELL erkannt, ohne den Präprozessor zu bemühen - für den
        /// Editor, der auf unverarbeitetem, evtl. gerade erst getipptem Text
        /// arbeitet). Unbekannte Namen sind enthalten - prüfen mit
        /// <see cref="TrySourceFor"/>.</summary>
        public static IEnumerable<string> FindImportNames(string source) =>
            ImportDirective.Matches(source).Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase);

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

            if (nativeImports.Contains(NativeImports.Devices))
            {
                processedSources.Insert(1, preprocess(DeviceBridge.PreludeSource));
                DeviceBridge.RegisterStubs(natives);
                inserted++;
            }

            if (nativeImports.Contains(NativeImports.IO))
            {
                processedSources.Insert(1, preprocess(IoBridge.PreludeSource));
                IoBridge.RegisterStubs(natives);
                inserted++;
            }

            return inserted;
        }
    }
}
