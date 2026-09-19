using System.Linq;

namespace fire.Editor
{
    /// <summary>Ergebnis einer Klick-Navigation (siehe NavigationEngine.
    /// TryResolve). `FilePath`: `null` bedeutet "Ziel liegt im AKTUELLEN
    /// Dokument selbst" (nur `Line` relevant); ein gesetzter Pfad ist der
    /// RELATIVE Pfad aus einer `#include`-Zeile - MainWindow löst ihn
    /// relativ zum Verzeichnis der jeweils anzeigenden Datei auf (siehe
    /// dort, kennt als einzige Stelle den tatsächlichen Dateipfad).
    /// `IsPrelude`: true, wenn das Ziel in der eingebauten Standard-
    /// bibliothek liegt (siehe ScriptSymbolIndex.MergeInPrelude) - dann ist
    /// `FilePath` IMMER null (kein echter Dateipfad vorhanden) und `Line`
    /// bezieht sich auf den PRELUDE-Quelltext, NICHT auf das aktuelle
    /// Dokument; der Aufrufer muss in diesem Fall MainWindow.
    /// ShowPreludeSource(line) statt eines normalen Sprungs im
    /// Haupteditor verwenden.</summary>
    public sealed record NavigationTarget(string? FilePath, int Line, bool IsPrelude = false);

    /// <summary>
    /// Bestimmt, wohin ein Klick auf eine bestimmte Zeichen-Position im
    /// Quelltext springen soll - Best-Effort wie der Rest der Editor-Werkzeuge
    /// (ScriptSymbolIndex/CompletionEngine), TOKEN-/TEXT-basiert statt über
    /// den echten Resolver (der bräuchte vollständigen, gültigen Quelltext
    /// und würde bei jedem Tippfehler nichts mehr liefern).
    ///
    /// Erkennt drei Fälle, in dieser Reihenfolge:
    /// 1. Klick auf eine `#include "pfad"`-Zeile -> Ziel ist diese Datei
    ///    (Zeile 1, da die GANZE Datei gemeint ist, kein bestimmtes Symbol
    ///    darin).
    /// 2. Klick auf einen Bezeichner nach einem `.` (`empfänger.Name`) ->
    ///    versucht, den TYP von `empfänger` zu bestimmen (wie bei der
    ///    Autovervollständigung: `this`, typisierte Variable/Parameter -
    ///    siehe ScriptSymbolIndex.TryResolveDeclaredType/EnclosingClassAt)
    ///    und `Name` als deren Mitglied nachzuschlagen.
    /// 3. Klick auf einen "nackten" Bezeichner -> zuerst als bekannter
    ///    Klassen-/Enum-Name versucht, sonst als Mitglied der GERADE
    ///    umschließenden Klasse (impliziter `this.Name`-Aufruf).
    ///
    /// Liefert `null`, wenn im AKTUELLEN Dokument nichts Passendes gefunden
    /// wird - MainWindow versucht in diesem Fall zusätzlich jede
    /// includierte Datei (siehe ExtractIdentifierAndReceiver/
    /// TryResolveInOtherFile, dort ohne Offset-Kontext, da der Klick-Offset
    /// aus DIESEM Dokument in einer ANDEREN Datei bedeutungslos wäre).
    /// </summary>
    public static class NavigationEngine
    {
        public static NavigationTarget? TryResolve(string source, int offset, ScriptSymbolIndex index)
        {
            if (offset < 0 || offset > source.Length) return null;

            int line = LineOf(source, offset);
            var include = index.IncludeDirectives.FirstOrDefault(d => d.Line == line);
            if (include != null) return new NavigationTarget(include.RelativePath, 1);

            var extracted = ExtractIdentifierAndReceiver(source, offset);
            if (extracted == null) return null;
            var (identifier, receiver) = extracted.Value;

            if (receiver != null)
            {
                string? className = receiver == "this"
                    ? index.EnclosingClassAt(offset)
                    : index.TryResolveDeclaredType(offset, receiver);
                if (className == null && index.Classes.ContainsKey(receiver))
                    className = receiver; // 'ClassName.Member' (unüblich, aber abgedeckt)

                if (className != null)
                {
                    var member = index.MembersOf(className).FirstOrDefault(m => m.Name == identifier && m.DeclLine > 0);
                    if (member != null)
                    {
                        bool fromPrelude = index.Classes.TryGetValue(className, out var ownerClass) && ownerClass.IsFromPrelude;
                        return new NavigationTarget(null, member.DeclLine, fromPrelude);
                    }
                }

                if (index.EnumDeclLines.TryGetValue(receiver, out var enumLine)
                    && index.EnumMembers.TryGetValue(receiver, out var enumMembers)
                    && enumMembers.Contains(identifier))
                    return new NavigationTarget(null, enumLine); // Enum selbst - einzelne Mitglieder haben keine eigene Zeile

                return null;
            }

            if (index.Classes.TryGetValue(identifier, out var cls) && cls.DeclLine > 0)
                return new NavigationTarget(null, cls.DeclLine, cls.IsFromPrelude);

            if (index.EnumDeclLines.TryGetValue(identifier, out var directEnumLine))
                return new NavigationTarget(null, directEnumLine);

            string? enclosing = index.EnclosingClassAt(offset);
            if (enclosing != null)
            {
                var member = index.MembersOf(enclosing).FirstOrDefault(m => m.Name == identifier && m.DeclLine > 0);
                if (member != null)
                {
                    bool fromPrelude = index.Classes.TryGetValue(enclosing, out var enclosingClass) && enclosingClass.IsFromPrelude;
                    return new NavigationTarget(null, member.DeclLine, fromPrelude);
                }
            }

            return null;
        }

        /// <summary>Bezeichner + (falls vorhanden) der Empfänger vor einem
        /// '.' an der Klick-Position - öffentlich, damit MainWindow beim
        /// Fehlschlag von TryResolve dieselbe Auflösung gegen die
        /// Symbol-Indizes ANDERER (includierter) Dateien versuchen kann,
        /// OHNE die (dort bedeutungslose) Offset-/Scope-Information aus
        /// DIESEM Dokument erneut zu verwenden (siehe TryResolveInOtherFile).</summary>
        public static (string Identifier, string? Receiver)? ExtractIdentifierAndReceiver(string source, int offset)
        {
            string? identifier = ReadIdentifierAt(source, offset, out int idStart);
            if (identifier == null) return null;
            string? receiver = ReadIdentifierBeforeDot(source, idStart);
            return (identifier, receiver);
        }

        /// <summary>Wie der Kern von TryResolve, aber für eine ANDERE Datei
        /// als die, in der geklickt wurde - deshalb OHNE Offset-abhängige
        /// Fälle ('this', umschließende Klasse, typisierte lokale Variable):
        /// `receiver` wird hier ausschließlich als LITERALER Klassenname
        /// interpretiert, ein `receiver == null` nur als direkter Klassen-/
        /// Enum-Name, NICHT als impliziter `this.identifier`-Aufruf (dafür
        /// fehlt der Scope-Kontext in einer fremden Datei).
        ///
        /// Schließt Treffer aus der eingebauten Standardbibliothek bewusst
        /// AUS (siehe ClassInfo.IsFromPrelude) - JEDER ScriptSymbolIndex
        /// (auch der einer includierten Datei) enthält sie jetzt automatisch
        /// mit (siehe ScriptSymbolIndex.MergeInPrelude); der Rückgabetyp
        /// hier ist aber nur eine reine Zeilennummer OHNE die Möglichkeit,
        /// "das ist eigentlich die Prelude, nicht diese Datei" zu
        /// signalisieren wie beim NavigationTarget.IsPrelude-Feld der
        /// Haupt-Auflösung (TryResolve) - ein Prelude-Treffer würde deshalb
        /// hier fälschlich als Zeile INNERHALB der fremden Datei
        /// interpretiert. Bis das eine eigene Signalisierung bekommt, lieber
        /// KEIN Sprung als ein FALSCHER.</summary>
        public static int? TryResolveInOtherFile(string identifier, string? receiver, ScriptSymbolIndex otherIndex)
        {
            if (receiver != null)
            {
                if (otherIndex.Classes.TryGetValue(receiver, out var receiverClass) && !receiverClass.IsFromPrelude)
                {
                    var member = otherIndex.MembersOf(receiver).FirstOrDefault(m => m.Name == identifier && m.DeclLine > 0);
                    if (member != null) return member.DeclLine;
                }
                if (otherIndex.EnumDeclLines.TryGetValue(receiver, out var enumLine)
                    && otherIndex.EnumMembers.TryGetValue(receiver, out var enumMembers)
                    && enumMembers.Contains(identifier))
                    return enumLine;
                return null;
            }

            if (otherIndex.Classes.TryGetValue(identifier, out var cls) && cls.DeclLine > 0 && !cls.IsFromPrelude)
                return cls.DeclLine;
            if (otherIndex.EnumDeclLines.TryGetValue(identifier, out var directEnumLine))
                return directEnumLine;

            return null;
        }

        private static int LineOf(string source, int offset)
        {
            int line = 1;
            for (int i = 0; i < offset && i < source.Length; i++)
                if (source[i] == '\n') line++;
            return line;
        }

        private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        /// <summary>Liest den Bezeichner AN oder UNMITTELBAR VOR `offset`
        /// (deckt sowohl "Klick mitten im Wort" als auch "Klick direkt
        /// hinter dem letzten Zeichen des Wortes" ab - beides sind bei
        /// einem Mausklick gleichermaßen zu erwarten).</summary>
        private static string? ReadIdentifierAt(string source, int offset, out int idStart)
        {
            idStart = 0;
            int start = offset;
            while (start > 0 && IsIdentChar(source[start - 1])) start--;
            int end = offset;
            while (end < source.Length && IsIdentChar(source[end])) end++;
            if (start >= end) return null;
            if (char.IsDigit(source[start])) return null; // beginnt mit Ziffer - kein gültiger Bezeichner
            idStart = start;
            return source.Substring(start, end - start);
        }

        private static string? ReadIdentifierBeforeDot(string source, int idStart)
        {
            if (idStart <= 0 || source[idStart - 1] != '.') return null;
            int end = idStart - 1;
            int start = end;
            while (start > 0 && IsIdentChar(source[start - 1])) start--;
            if (start >= end) return null;
            return source.Substring(start, end - start);
        }
    }
}
