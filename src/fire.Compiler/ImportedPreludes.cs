using fire.Bytecode;
using fire.Device.Bridge;
using fire.Runtime;
using fire.Terminal.Bridge;
using System;
using System.Collections.Generic;

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
            _ => throw new Exception($"'{name}' ist keine bekannte Erweiterung."),
        };

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

            if (nativeImports.Contains(NativeImports.Devices))
            {
                processedSources.Insert(1, preprocess(DeviceBridge.PreludeSource));
                DeviceBridge.RegisterStubs(natives);
                inserted++;
            }

            return inserted;
        }
    }
}
