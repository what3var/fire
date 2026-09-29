using System;
using System.Collections.Generic;
using System.Linq;

namespace fire.Compiler
{
    /// <summary>Hilfen für Aufrufer, die einen Übersetzungsfehler anzeigen
    /// wollen: Resolver und Compiler sammeln mehrere Fehler (siehe
    /// ResolverException/CompilerException), `Exception.Message` nennt aber
    /// nur den ERSTEN davon - hier stehen alle.</summary>
    public static class CompileErrors
    {
        /// <summary>Die Meldungen ALLER in `ex` gesammelten Fehler (bei einem
        /// Parser-/Präprozessor-/sonstigen Fehler nur dessen eine Meldung).</summary>
        public static IReadOnlyList<string> Messages(Exception ex) => ex switch
        {
            ResolverException r => r.Errors.Select(e => e.Message).ToList(),
            CompilerException c => c.Errors.Select(e => e.Message).ToList(),
            _ => new[] { ex.Message },
        };

        /// <summary>Alle Meldungen, eine pro Zeile.</summary>
        public static string Describe(Exception ex) => string.Join(Environment.NewLine, Messages(ex));
    }
}
