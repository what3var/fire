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
    /// Editor-Inhalt laufen und wandelt die ERSTE dabei auftretende Exception
    /// in eine Diagnose mit Zeilenangabe um - bewusst nur die erste (nicht
    /// "sammle alle Fehler im Dokument"): Parser/Resolver brechen beim ersten
    /// Fehler sofort ab (keine Fehlerkorrektur/Wiederaufsetzen wie ein
    /// produktionsreifer Compiler), ein zweiter gemeldeter Fehler wäre also
    /// ohnehin nur ein Folgefehler des ersten und eher verwirrend als
    /// hilfreich.
    ///
    /// Verwendet - wie DebugSession/RuntimeSession.Build - ParseMultiple
    /// (Prelude + Nutzer-Code als EIN kombiniertes Programm, ParseWithPrelude
    /// gibt es nicht mehr), damit List/IEnumerable/
    /// IndexOutOfBoundsException bekannt sind. WICHTIG: die von Parser/
    /// Resolver gemeldeten Zeilennummern bleiben dabei trotzdem korrekt auf
    /// den NUTZER-Quelltext bezogen, nicht auf die Prelude verschoben - jeder
    /// Programmteil wird intern über eine EIGENE Lexer/Parser-Instanz mit
    /// bei 1 beginnender Zeilenzählung für GENAU diesen Teil erzeugt (siehe
    /// Parser.ParseRaw), die Zeilennummer an jedem AST-Knoten bleibt danach
    /// unverändert erhalten, unabhängig davon, wie die Teile anschließend
    /// zu einer Liste zusammengefügt werden.
    /// </summary>
    public static class LiveDiagnostics
    {
        public static List<Diagnostic> Analyze(string source)
        {
            var diagnostics = new List<Diagnostic>();
            if (string.IsNullOrWhiteSpace(source)) return diagnostics;

            try
            {
                var natives = NativeRegistry.CreateDefault();
                var alreadyIncluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var sources = new[] { fire.Standard.Prelude.Source, source };
                var processed = new List<ProcessedSource>();
                // Dieselbe Registry wie beim ECHTEN Kompilieren (siehe
                // RuntimeSession.Build/CreateProjectDirectiveRegistry) -
                // sonst würde z.B. '#import "graphics"' hier fälschlich als
                // unbekannte Präprozessor-Direktive unterkringelt, obwohl
                // sie beim tatsächlichen Ausführen längst akzeptiert wird.
                // `onImport: null` - die eigentliche WIRKUNG (Grafik-Bridge
                // laden) ist für reine Diagnostik irrelevant, nur "ist das
                // syntaktisch gültig" zählt hier.
                var registry = RuntimeSession.CreateProjectDirectiveRegistry();
                foreach (var s in sources)
                    processed.Add(Preprocessor.Process(s, System.IO.Directory.GetCurrentDirectory(), alreadyIncluded, registry));
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
                diagnostics.Add(new Diagnostic(ex.Line, ex.Message));
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
                // Fehler ohne eigene Zeilen-Information (typischerweise aus
                // dem Compiler, z.B. eine nicht auflösbare Methodenüberladung) -
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

            return diagnostics;
        }

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
        /// Zuverlässigkeit der echten Fehler in `source` selbst.</summary>
        public static List<Diagnostic> AnalyzeInProject(string source, IReadOnlyList<string> otherProjectFiles)
        {
            var diagnostics = Analyze(source);
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
