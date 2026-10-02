using System.Linq;

namespace fire.Editor
{
    /// <summary>Ergebnis einer Klick-Navigation (siehe NavigationEngine.
    /// TryResolve). `FilePath`: `null` bedeutet "Ziel liegt im AKTUELLEN
    /// Dokument selbst" (nur `Line` relevant); ein gesetzter Pfad ist der
    /// RELATIVE Pfad aus einer `#include`-Zeile - MainWindow löst ihn
    /// relativ zum Verzeichnis der jeweils anzeigenden Datei auf (siehe
    /// dort, kennt als einzige Stelle den tatsächlichen Dateipfad).
    /// `PreludeName`: gesetzt, wenn das Ziel in einer eingebauten Prelude
    /// liegt (Standardbibliothek oder eine per `#import` zugeschaltete
    /// Erweiterung, siehe ScriptSymbolIndex.PreludeSourceOf) - dann ist
    /// `FilePath` IMMER null (kein echter Dateipfad vorhanden) und `Line`
    /// bezieht sich auf den PRELUDE-Quelltext, NICHT auf das aktuelle
    /// Dokument; der Aufrufer zeigt in diesem Fall die Prelude in einem
    /// schreibgeschützten Fenster (FileViewerWindow.ShowPrelude) statt eines
    /// normalen Sprungs im Editor.</summary>
    public sealed record NavigationTarget(string? FilePath, int Line, string? PreludeName = null)
    {
        public bool IsPrelude => PreludeName != null;
    }

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

            string? identifier = ReadIdentifierAt(source, offset, out int idStart);
            if (identifier == null) return null;
            string? receiver = ReadIdentifierBeforeDot(source, idStart);
            // Hinter einem ')' / ']' (`a.B().c`) kennt ReadIdentifierBeforeDot keinen Namen - der Punkt zählt trotzdem.
            if (receiver == null && idStart > 0 && source[idStart - 1] == '.') receiver = "";

            if (receiver != null)
            {
                // Erst über die volle Typherleitung der Vervollständigung (`a.B().c.`, `var x = new T()`,
                // Namespaces, statische Aufrufe ...), dann über die einfache Auflösung des Empfängers.
                // `Geo.Circle` / `A.B.Circle` als Typname (z.B. hinter `new` oder in einer Deklaration): der geschriebene
                // Pfad, vollqualifiziert oder über #using.
                string? dotted = ReadDottedNameBefore(source, idStart - 1);
                if (dotted != null)
                {
                    string qualified = dotted + "." + identifier;
                    string? qualifiedClass = index.TryFindClass(qualified, offset);
                    if (qualifiedClass != null && index.Classes.TryGetValue(qualifiedClass, out var qc) && qc.DeclLine > 0)
                        return new NavigationTarget(null, qc.DeclLine, qc.PreludeName);
                    string? qualifiedEnum = index.TryFindEnum(qualified, offset);
                    if (qualifiedEnum != null && index.EnumDeclLines.ContainsKey(qualifiedEnum))
                        return EnumTarget(index, qualifiedEnum);
                }

                var type = index.ResolveReceiver(idStart - 1);
                var viaType = ResolveMemberOfType(index, type, identifier);
                if (viaType != null) return viaType;
                if (receiver.Length == 0) return null;

                string? className = receiver == "this"
                    ? index.EnclosingClassAt(offset)
                    : index.TryResolveDeclaredType(offset, receiver);
                // 'ClassName.Member' (unüblich, aber abgedeckt) - der Klassenname
                // wie geschrieben, auch aus einem anderen Namespace/per #using.
                className ??= index.TryFindClass(receiver, offset);

                if (className != null)
                {
                    var member = FindMember(index, className, identifier);
                    if (member != null) return MemberTarget(index, member);
                }

                string? receiverEnum = index.TryFindEnum(receiver, offset);
                if (receiverEnum != null && enumMembersContain(index, receiverEnum, identifier))
                    return EnumTarget(index, receiverEnum);

                return null;
            }

            string? classKey = index.TryFindClass(identifier, offset);
            if (classKey != null && index.Classes.TryGetValue(classKey, out var cls) && cls.DeclLine > 0)
                return new NavigationTarget(null, cls.DeclLine, cls.PreludeName);

            string? enumKey = index.TryFindEnum(identifier, offset);
            if (enumKey != null && index.EnumDeclLines.ContainsKey(enumKey))
                return EnumTarget(index, enumKey);

            string? enclosing = index.EnclosingClassAt(offset);
            if (enclosing != null)
            {
                var member = FindMember(index, enclosing, identifier);
                if (member != null) return MemberTarget(index, member);
            }

            return null;
        }

        private static bool enumMembersContain(ScriptSymbolIndex index, string enumKey, string name) =>
            index.EnumMembers.TryGetValue(enumKey, out var members) && members.Contains(name);

        /// <summary>Das Mitglied `name` der Klasse `className` (auch ein geerbtes), mit bekannter Deklarationszeile.</summary>
        private static MemberInfo? FindMember(ScriptSymbolIndex index, string className, string name) =>
            index.MembersOf(className).FirstOrDefault(m => m.Name == name && m.DeclLine > 0);

        /// <summary>Ziel eines Mitglieds: stammt es aus einer Prelude (auch ein geerbtes einer Prelude-Basisklasse), ist das
        /// Ziel dort - nicht in der angeklickten Klasse.</summary>
        private static NavigationTarget MemberTarget(ScriptSymbolIndex index, MemberInfo member) =>
            new(null, member.DeclLine, member.Source?.PreludeName);

        /// <summary>Ziel eines Enums (einzelne Mitglieder haben keine eigene Zeile).</summary>
        private static NavigationTarget EnumTarget(ScriptSymbolIndex index, string enumKey)
        {
            index.EnumPreludes.TryGetValue(enumKey, out var prelude);
            return new NavigationTarget(null, index.EnumDeclLines[enumKey], prelude);
        }

        /// <summary>Das Mitglied `identifier` zu einem hergeleiteten Empfänger-Typ (siehe ScriptSymbolIndex.ResolveReceiver).</summary>
        private static NavigationTarget? ResolveMemberOfType(ScriptSymbolIndex index, ExprType type, string identifier)
        {
            switch (type.Kind)
            {
                case TypeKind.Instance:
                case TypeKind.Static:
                    {
                        var member = FindMember(index, type.Name!, identifier);
                        return member == null ? null : MemberTarget(index, member);
                    }
                case TypeKind.Enum:
                    return enumMembersContain(index, type.Name!, identifier) && index.EnumDeclLines.ContainsKey(type.Name!)
                        ? EnumTarget(index, type.Name!) : null;
                case TypeKind.Namespace:
                    {
                        // `Geometry.Circle`: eine Klasse/ein Enum im Namespace.
                        string full = type.Name + "." + identifier;
                        if (index.Classes.TryGetValue(full, out var cls) && cls.DeclLine > 0)
                            return new NavigationTarget(null, cls.DeclLine, cls.PreludeName);
                        if (index.EnumDeclLines.ContainsKey(full)) return EnumTarget(index, full);
                        return null;
                    }
                case TypeKind.Primitive:
                case TypeKind.Array:
                    {
                        // Methoden aus `class extends string { ... }` (Prelude und eigene Erweiterungen).
                        if (BuiltinMembers.ExtensionClassOf(type) is { } key && index.Classes.ContainsKey(key))
                        {
                            var member = FindMember(index, key, identifier);
                            if (member != null) return MemberTarget(index, member);
                        }
                        return null;
                    }
                default:
                    return null;
            }
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
                // Ohne Offset-Kontext: der Name wie geschrieben (evtl. über die
                // #using der anderen Datei, oder als einziger Treffer).
                string? receiverKey = otherIndex.TryFindClass(receiver, -1);
                if (receiverKey != null && otherIndex.Classes.TryGetValue(receiverKey, out var receiverClass) && !receiverClass.IsFromPrelude)
                {
                    var member = otherIndex.MembersOf(receiverKey).FirstOrDefault(m => m.Name == identifier && m.DeclLine > 0);
                    if (member != null) return member.DeclLine;
                }
                string? receiverEnum = otherIndex.TryFindEnum(receiver, -1);
                if (receiverEnum != null
                    && otherIndex.EnumDeclLines.TryGetValue(receiverEnum, out var enumLine)
                    && otherIndex.EnumMembers.TryGetValue(receiverEnum, out var enumMembers)
                    && enumMembers.Contains(identifier))
                    return enumLine;
                return null;
            }

            string? classKey = otherIndex.TryFindClass(identifier, -1);
            if (classKey != null && otherIndex.Classes.TryGetValue(classKey, out var cls) && cls.DeclLine > 0 && !cls.IsFromPrelude)
                return cls.DeclLine;
            string? enumKey = otherIndex.TryFindEnum(identifier, -1);
            if (enumKey != null && otherIndex.EnumDeclLines.TryGetValue(enumKey, out var directEnumLine))
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

        /// <summary>Der durch Punkte verbundene Bezeichner-Pfad unmittelbar vor `dotIdx` (dem '.'): `A.B` für `A.B.Name`,
        /// null wenn davor kein reiner Pfad steht (z.B. `f().Name`).</summary>
        private static string? ReadDottedNameBefore(string source, int dotIdx)
        {
            int end = dotIdx;
            int start = end;
            while (start > 0 && (IsIdentChar(source[start - 1]) || source[start - 1] == '.')) start--;
            if (start >= end) return null;
            string path = source.Substring(start, end - start);
            if (path.StartsWith('.') || path.EndsWith('.') || path.Contains("..") || char.IsDigit(path[0])) return null;
            return path;
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
