using System;
using System.Collections.Generic;
using fire.Bytecode;
using fire.Parsing;
using fire.Resolving;

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
    /// Verwendet - wie DebugSession.Compile - ParseWithPrelude (Prelude +
    /// Nutzer-Code als EIN kombiniertes Programm), damit List/IEnumerable/
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
                var program = Parser.ParseWithPrelude(source);
                var resolveResult = Resolver.Resolve(program, natives.Names);
                Compiler.Compile(program, resolveResult, natives);
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
    }
}
