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
        public override string ToString() => $"Zeile {Line}: {Message}";
    }

    /// <summary>
    /// Live-Fehleranalyse für den Editor: lässt Parser, Resolver UND Compiler
    /// (in dieser Reihenfolge, wie beim echten Kompilieren) auf dem aktuellen
    /// Editor-Inhalt laufen und wandelt die dabei auftretenden Fehler in
    /// Diagnosen mit Zeilenangabe um.
    ///
    /// Resolver und Compiler brechen beim ersten Fehler NICHT ab, sondern
    /// sammeln alle weiteren (siehe ResolverException/CompilerException) -
    /// jeder davon landet als eigene Diagnose im Ergebnis. Der Compiler läuft
    /// nur, wenn der Resolver keinen Fehler fand (er braucht dessen
    /// Ergebnis, ein Lauf über einen unaufgelösten AST würde nur
    /// Folgefehler liefern). Der PARSER dagegen meldet weiterhin nur den
    /// ersten Fehler: er setzt nach einem Syntaxfehler nicht wieder auf,
    /// ein zweiter gemeldeter Fehler wäre also nur ein Folgefehler des
    /// ersten.
    ///
    /// Verwendet - wie DebugSession/RuntimeSession.Build - ParseMultiple
    /// (Prelude + Nutzer-Code als EIN kombiniertes Programm), damit
    /// List/IEnumerable/IndexOutOfBoundsException bekannt sind, UND die
    /// Preludes der per `#import` zugeschalteten Erweiterungen (siehe
    /// ImportedPreludes): `#import "graphics"` macht Framebuffer/Console/
    /// Window bekannt, `#import "devices"` die Geräte-Klassen - genau wie
    /// beim echten Kompilieren, sonst würde jede Verwendung davon als
    /// "Unbekannte Klasse"/"Unbekannter Bezeichner" unterkringelt.
    /// WICHTIG: die von Parser/Resolver gemeldeten Zeilennummern bleiben
    /// dabei trotzdem korrekt auf den NUTZER-Quelltext bezogen, nicht auf
    /// die Preludes verschoben - jeder Programmteil wird intern über eine
    /// EIGENE Lexer/Parser-Instanz mit bei 1 beginnender Zeilenzählung für
    /// GENAU diesen Teil erzeugt (siehe Parser.ParseRaw), die Zeilennummer
    /// an jedem AST-Knoten bleibt danach unverändert erhalten, unabhängig
    /// davon, wie die Teile anschließend zu einer Liste zusammengefügt
    /// werden.
    /// </summary>
    public static class LiveDiagnostics
    {
        public static List<Diagnostic> Analyze(string source) => Analyze(source, Array.Empty<string>());

        /// <summary>`extraImports`: Namen von Erweiterungen (wie in `#import
        /// "name"`), die zusätzlich zu den in `source` selbst
        /// vorkommenden als zugeschaltet gelten - siehe AnalyzeInProject.
        /// Unbekannte Namen werden ignoriert.</summary>
        private static List<Diagnostic> Analyze(string source, IEnumerable<string> extraImports)
        {
            var diagnostics = new List<Diagnostic>();
            if (string.IsNullOrWhiteSpace(source)) return diagnostics;

            try
            {
                var natives = NativeRegistry.CreateDefault();
                var alreadyIncluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var cwd = System.IO.Directory.GetCurrentDirectory();
                var nativeImports = new HashSet<string>();
                foreach (var name in extraImports)
                {
                    try { nativeImports.Add(ImportedPreludes.ParseImportName(name)); }
                    catch (Exception) { /* unbekannte Erweiterung - meldet deren eigene Datei */ }
                }

                // Dieselbe Registry wie beim ECHTEN Kompilieren (siehe
                // RuntimeSession.CreateProjectDirectiveRegistry) - sonst
                // würde z.B. '#import "graphics"' hier fälschlich als
                // unbekannte Präprozessor-Direktive unterkringelt, obwohl
                // sie beim tatsächlichen Ausführen längst akzeptiert wird.
                // Der Callback merkt sich die zugeschalteten Erweiterungen,
                // deren Preludes unten eingesetzt werden.
                var registry = RuntimeSession.CreateProjectDirectiveRegistry(name => nativeImports.Add(name));
                var processed = new List<ProcessedSource>();
                foreach (var s in new[] { fire.Standard.Prelude.Source, source })
                    processed.Add(Preprocessor.Process(s, cwd, alreadyIncluded, registry));

                // Erst NACH dem Vorverarbeiten ALLER Quellen ist bekannt, welche
                // Erweiterungen zugeschaltet sind - wie im Linker.
                ImportedPreludes.Insert(
                    nativeImports, natives, processed,
                    preludeSource => Preprocessor.Process(preludeSource, cwd, alreadyIncluded, registry));

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
                // Kein Line-Feld vorhanden (siehe Klassenkommentar dort,
                // betrifft #include/#extern-Direktiven, die vor jeder
                // Zeilen-Buchhaltung des eigentlichen Lexers verarbeitet
                // werden) - Zeile 1 als bestmöglicher Platzhalter, damit die
                // Fehleransicht trotzdem etwas Klickbares/Sichtbares zeigt.
                diagnostics.Add(new Diagnostic(1, ex.Message));
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
            {
                // Fehler ohne eigene Zeilen-Information, der nicht über den
                // Compiler kam (der meldet über CompilerException MIT Zeile) -
                // ebenfalls Zeile 1 als Platzhalter.
                diagnostics.Add(new Diagnostic(1, ex.Message));
            }
            catch (Exception ex)
            {
                // Absichtlich breit gefangen (wie DebugSession.RunGuarded):
                // Live-Diagnostik läuft ZWANGSLÄUFIG immer wieder über kurz-
                // lebige, noch kaputte Zwischenzustände während des Tippens -
                // JEDE unerwartete Exception-Art landet hier als generische
                // Diagnose, statt ungefangen im DispatcherTimer-Tick zu
                // enden und die ganze Editor-Anwendung mitzureißen.
                diagnostics.Add(new Diagnostic(1, ex.Message));
            }

            // Resolver-/Compiler-Fehler kommen in Reihenfolge der Auflösung
            // (z.B. erst alle Klassen, dann der Top-Level-Code), nicht in
            // Zeilenreihenfolge - für die Fehleransicht nach Zeile sortieren
            // (stabil, gleiche Zeile behält die Reihenfolge).
            return diagnostics.OrderBy(d => d.Line).ToList();
        }

        private static readonly Regex ImportDirective =
            new("^[ \\t]*#import[ \\t]+\"([^\"\\r\\n]+)\"", RegexOptions.Compiled | RegexOptions.Multiline);

        private static readonly Regex UnknownClassOrType =
            new(@"^(?:Unbekannte Klasse|Unbekannter Typ) '([^']+)'", RegexOptions.Compiled);

        /// <summary>Wie Analyze(source), unterdrückt aber Diagnosen, die NUR
        /// daher kommen, dass diese Analyse ausschließlich `source` (+
        /// Prelude) kennt, nicht das GESAMTE Projekt (SPEC "Mehrere
        /// Quelldateien") - eine gültige Referenz auf eine Klasse, die in
        /// EINER DER `otherProjectFiles` definiert ist, würde sonst
        /// fälschlich als "Unbekannte Klasse"/"Unbekannter Typ" gemeldet.
        ///
        /// Bewusst KEIN vollständiger, kombinierter Parser/Resolver/
        /// Compiler-Lauf über das GANZE Projekt: ein Parse-/Resolve-Fehler
        /// trägt keinen Quell-Index (anders als kompilierter Bytecode, siehe
        /// Bytecode.Chunk.MarkLine) - ein Fehler in einer ANDEREN, gerade
        /// kaputten Datei ließe sich dann nicht zuverlässig von einem
        /// ECHTEN Fehler in `source` selbst unterscheiden, man würde also
        /// riskieren, der falschen Datei einen roten Fehler unterzuschieben.
        /// Stattdessen ein GEZIELTER, risikoarmer Nachtrag: Analyze(source)
        /// läuft ganz normal (findet JEDEN echten Fehler in `source` selbst
        /// zuverlässig), und nur für jede resultierende "Unbekannte Klasse/
        /// Unbekannter Typ 'X'"-Diagnose wird geprüft, ob 'X' in EINER der
        /// `otherProjectFiles` als Klasse definiert ist (per
        /// ScriptSymbolIndex, demselben leichtgewichtigen Scanner wie für
        /// Vervollständigung/Navigation) - falls ja, war die Diagnose falsch-
        /// positiv (die Klasse ist projektweit ja tatsächlich bekannt) und
        /// wird entfernt. Eine der `otherProjectFiles` mit einem gerade
        /// eigenen Tippfehler bringt diese Prüfung nicht zum Absturz (wird
        /// einfach übersprungen) - beeinflusst höchstens, ob EINE bestimmte
        /// falsch-positive Diagnose noch übersehen bleibt, nie die
        /// Zuverlässigkeit der echten Fehler in `source` selbst.
        ///
        /// Ausnahme: ein `#import "..."` in einer der `otherProjectFiles`
        /// wird dagegen ECHT mitgezählt - die Erweiterungen (und damit ihre
        /// Preludes, siehe ImportedPreludes) gelten im echten Compiler für
        /// das ganze Projekt, nicht pro Datei.</summary>
        public static List<Diagnostic> AnalyzeInProject(string source, IReadOnlyList<string> otherProjectFiles)
        {
            var importsElsewhere = otherProjectFiles
                .Where(other => !string.IsNullOrWhiteSpace(other))
                .SelectMany(other => ImportDirective.Matches(other).Select(m => m.Groups[1].Value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var diagnostics = Analyze(source, importsElsewhere);
            if (diagnostics.Count == 0 || otherProjectFiles.Count == 0) return diagnostics;

            var knownElsewhere = new HashSet<string>();
            foreach (var other in otherProjectFiles)
            {
                if (string.IsNullOrWhiteSpace(other)) continue;
                ScriptSymbolIndex index;
                try { index = ScriptSymbolIndex.Build(other); }
                catch { continue; }
                foreach (var name in index.Classes.Keys)
                    knownElsewhere.Add(name);
            }
            if (knownElsewhere.Count == 0) return diagnostics;

            return diagnostics.Where(d =>
            {
                var match = UnknownClassOrType.Match(d.Message);
                return !(match.Success && knownElsewhere.Contains(match.Groups[1].Value));
            }).ToList();
        }
    }
}
