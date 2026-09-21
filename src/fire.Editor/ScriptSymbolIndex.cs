using System.Collections.Generic;
using System.Diagnostics;
using fire.Compiler;
using fire.Lexing;

namespace fire.Editor
{
    public enum MemberKind
    {
        Field,
        Method,
        Property,
        Constructor,
    }

    public sealed class MemberInfo
    {
        public string Name { get; }
        public MemberKind Kind { get; }
        public int ParamCount { get; }

        /// <summary>1-basierte Quelltextzeile des NAMENS-Tokens dieses
        /// Mitglieds (für "zu Definition springen", siehe NavigationEngine)
        /// - 0, falls unbekannt (sollte beim normalen Erfassen nicht
        /// vorkommen).</summary>
        public int DeclLine { get; }

        public MemberInfo(string name, MemberKind kind, int paramCount, int declLine = 0)
        {
            Name = name;
            Kind = kind;
            ParamCount = paramCount;
            DeclLine = declLine;
        }
    }

    public sealed class ClassInfo
    {
        public string Name { get; }
        public string? BaseName { get; set; }
        public List<MemberInfo> Members { get; } = new();

        /// <summary>1-basierte Quelltextzeile des Klassennamen-Tokens (für
        /// "zu Definition springen", siehe NavigationEngine) - nachträglich
        /// setzbar, falls eine Erweiterung (siehe ScriptSymbolIndex.
        /// HarvestClassExtension) vor der echten Deklaration im Dokument
        /// stand und zunächst nur einen Platzhalter ohne bekannte Zeile
        /// angelegt hat. Bezieht sich bei <see cref="IsFromPrelude"/> auf
        /// eine Zeile INNERHALB des Prelude-Quelltexts, NICHT auf das
        /// aktuell bearbeitete Dokument.</summary>
        public int DeclLine { get; set; }

        /// <summary>true, wenn diese Klasse aus der eingebauten
        /// Standardbibliothek stammt (siehe ScriptSymbolIndex.
        /// MergeInPrelude/PreludeIndex), NICHT aus dem gerade bearbeiteten
        /// Dokument selbst - "zu Definition springen" muss dafür die
        /// Prelude in einem eigenen, schreibgeschützten Popup zeigen (siehe
        /// NavigationEngine/MainWindow.ShowPreludeSource) statt im
        /// Hauptdokument zu einer (dort gar nicht existierenden) Zeile zu
        /// scrollen.</summary>
        public bool IsFromPrelude { get; set; }

        public ClassInfo(string name, int declLine = 0)
        {
            Name = name;
            DeclLine = declLine;
        }
    }

    /// <summary>Eine `#include "pfad"`-Zeile im Dokument (siehe
    /// Parsing.Preprocessor - rein TEXTUELL erkannt, exakt derselbe reguläre
    /// Ausdruck wie dort, damit "was der Editor beim Klicken erkennt" und
    /// "was der echte Präprozessor tatsächlich einfügt" nie auseinanderlaufen
    /// können).</summary>
    public sealed record IncludeDirective(string RelativePath, int Line);

    /// <summary>
    /// Best-Effort-Symboltabelle für Autovervollständigung im Editor - bewusst
    /// TOKEN-basiert (über den echten Lexer), NICHT über den echten Parser:
    /// der scheitert beim Live-Tippen sehr oft genau an der Stelle, an der
    /// gerade getippt wird (unvollständige Ausdrücke), und liefert dann GAR
    /// NICHTS zurück - Tokenisieren scheitert dagegen nur bei wirklich kaputten
    /// Literalen (offener String etc.), ist also für diesen Zweck deutlich
    /// robuster. Bildet dafür bewusst nur einen TEIL der echten Grammatik
    /// nach (Unterscheidung Feld/Methode/Property/Konstruktor beim Sammeln
    /// von Klassen-Mitgliedern) und macht bei der Scope-Zuordnung (welche
    /// lokale Variable ist an einer Cursor-Position sichtbar) bewusst
    /// vereinfachende Annahmen (siehe LocalsVisibleAt) - für
    /// Vervollständigungs-VORSCHLÄGE ausreichend, ersetzt aber keine echte
    /// Auflösung (dafür bräuchte es Resolver.Resolve auf vollständigem,
    /// gültigem Quelltext).
    /// </summary>
    public sealed class ScriptSymbolIndex
    {
        public Dictionary<string, ClassInfo> Classes { get; } = new();
        public Dictionary<string, List<string>> EnumMembers { get; } = new();

        /// <summary>1-basierte Deklarationszeile pro Enum-Namen (für "zu
        /// Definition springen", siehe NavigationEngine).</summary>
        public Dictionary<string, int> EnumDeclLines { get; } = new();

        /// <summary>Alle irgendwo im Dokument gesehenen Bezeichner-Namen
        /// (Variablen, Parameter, Felder, ...) - unscharfer, aber robuster
        /// Fallback für die allgemeine Bezeichner-Vervollständigung, wenn
        /// keine genauere Information vorliegt.</summary>
        public HashSet<string> AllDeclaredNames { get; } = new();

        /// <summary>Alle `#include "pfad"`-Zeilen im Dokument (siehe
        /// IncludeDirective-Doku) - für "zu Datei springen"
        /// (NavigationEngine). Rein per Regex über den ROHEN Quelltext
        /// erkannt (NICHT über die Tokens - `#include` ist reine
        /// Präprozessor-Textersetzung, siehe Parsing.Preprocessor, der
        /// Lexer sieht davon nach dem echten Kompilieren nichts mehr, aber
        /// HIER arbeiten wir ja auf dem unverarbeiteten Editor-Text).</summary>
        public List<IncludeDirective> IncludeDirectives { get; } = new();

        private readonly List<Token> _tokens = new();
        private readonly string _source = string.Empty;
        private readonly int[] _lineStarts;

        private ScriptSymbolIndex(string source, List<Token> tokens)
        {
            _source = source;
            _tokens = tokens;
            _lineStarts = ComputeLineStarts(source);
        }

        private static int[] ComputeLineStarts(string source)
        {
            var starts = new List<int> { 0 };
            for (int i = 0; i < source.Length; i++)
                if (source[i] == '\n') starts.Add(i + 1);
            return starts.ToArray();
        }

        // Exakt derselbe Ausdruck wie Parsing.Preprocessor.IncludeLine -
        // bewusst dupliziert statt geteilt (Preprocessor ist Teil des
        // KERN-Projekts, ScriptSymbolIndex arbeitet auf UNVERARBEITETEM,
        // evtl. gerade erst getipptem Text und braucht deshalb ohnehin
        // eigene Fehlertoleranz) - muss inhaltlich aber synchron bleiben,
        // sonst erkennt der Editor Includes anders als der echte Compiler.
        private static readonly System.Text.RegularExpressions.Regex IncludeLine =
            new(@"^\s*#include\s+""([^""]*)""\s*$");

        public static ScriptSymbolIndex Build(string source)
        {
            List<Token> tokens;
            try
            {
                tokens = new Lexer(source).Tokenize();
            }
            catch (LexException)
            {
                tokens = new List<Token>();
            }

            var index = new ScriptSymbolIndex(source, tokens);
            index.Harvest();
            index.HarvestIncludes(source);
            index.MergeInPrelude();
            return index;
        }

        /// <summary>Mischt Klassen/Interfaces der eingebauten Standard-
        /// bibliothek (fire.Standard.Prelude, siehe SPEC "Vorangestellte
        /// Standardbibliothek") in DIESEN Index ein, markiert mit
        /// <see cref="ClassInfo.IsFromPrelude"/> - jedes reale Skript wird ja
        /// tatsächlich MIT dieser Bibliothek zusammen kompiliert (siehe
        /// Runtime.RuntimeSession.Build/Parser.ParseMultiple), Vervollständigung/
        /// Mitglieder-Suche sollen `List`/`IEnumerable`/etc. deshalb genauso
        /// kennen wie selbst im Dokument definierte Klassen.
        ///
        /// Eine im Dokument SELBST vollständig (neu) definierte Klasse
        /// gewinnt (kein Überschreiben) - deckt sich mit dem Verhalten des
        /// echten Compilers, der eine solche Namenskollision ohnehin als
        /// Fehler ablehnen würde; für den Editor ist "die eigene Definition
        /// anzeigen" hier die hilfreichere Wahl. Ein `class extends List
        /// { ... }` im Dokument legt beim Harvesten dagegen (siehe
        /// HarvestClassExtension) nur einen PLATZHALTER ohne echte
        /// Deklarationszeile (DeclLine bleibt 0) mit den neuen Erweiterungs-
        /// Mitgliedern an - für DIESEN Fall werden die echten Prelude-
        /// Mitglieder zusätzlich NACHGETRAGEN (statt die Erweiterung durch
        /// die reine Prelude-Definition zu ersetzen), sonst würde eine
        /// erweiterte `List` im Editor plötzlich ihre eigenen Add/Get/...-
        /// Methoden "verlieren".</summary>
        private void MergeInPrelude()
        {
            var prelude = PreludeIndex.Value;
            foreach (var (name, preludeClass) in prelude.Classes)
            {
                if (Classes.TryGetValue(name, out var existing))
                {
                    if (existing.DeclLine != 0) continue; // echte eigene (Neu-)Deklaration gewinnt

                    // Platzhalter aus einer 'class extends X { ... }'-
                    // Erweiterung (siehe HarvestClassExtension) - die echten
                    // Prelude-Mitglieder fehlen ihm noch, hier nachtragen;
                    // die eigenen Erweiterungs-Mitglieder bleiben zusätzlich
                    // erhalten (nur EINGEFÜGT, nicht ersetzt).
                    existing.DeclLine = preludeClass.DeclLine;
                    existing.IsFromPrelude = true;
                    existing.BaseName ??= preludeClass.BaseName;
                    existing.Members.AddRange(preludeClass.Members);
                }
                else
                {
                    Classes[name] = preludeClass;
                }
            }
            foreach (var name in prelude.AllDeclaredNames)
                AllDeclaredNames.Add(name);
        }

        /// <summary>Der Index der eingebauten Standardbibliothek selbst -
        /// EINMALIG gebaut und wiederverwendet (der Quelltext ist eine
        /// Konstante, siehe Standard.Prelude.Source), nicht bei jedem
        /// MergeInPrelude()-Aufruf neu. Jede darin enthaltene ClassInfo wird
        /// hier mit IsFromPrelude=true markiert, BEVOR sie an irgendein
        /// Dokument gemischt wird.</summary>
        private static readonly System.Lazy<ScriptSymbolIndex> PreludeIndex = new(() =>
        {
            var index = Build(fire.Standard.Prelude.Source, isPrelude: true);
            return index;
        });

        /// <summary>Interne Build-Überladung für den Prelude-Quelltext selbst
        /// (siehe PreludeIndex) - baut NUR den rohen Index (keine rekursive
        /// MergeInPrelude(), die Prelude braucht sich ja nicht selbst
        /// einzumischen) und markiert danach jede gefundene Klasse als
        /// IsFromPrelude.</summary>
        private static ScriptSymbolIndex Build(string source, bool isPrelude)
        {
            List<Token> tokens;
            try
            {
                tokens = new Lexer(source).Tokenize();
            }
            catch (LexException)
            {
                tokens = new List<Token>();
            }

            var index = new ScriptSymbolIndex(source, tokens);
            index.Harvest();
            if (isPrelude)
                foreach (var classInfo in index.Classes.Values)
                    classInfo.IsFromPrelude = true;
            return index;
        }

        private void HarvestIncludes(string source)
        {
            var lines = source.Split('\n');
            for (int lineIdx = 0; lineIdx < lines.Length; lineIdx++)
            {
                var match = IncludeLine.Match(lines[lineIdx].TrimEnd('\r'));
                if (match.Success)
                    IncludeDirectives.Add(new IncludeDirective(match.Groups[1].Value, lineIdx + 1));
            }
        }

        // -----------------------------------------------------------
        // Sammeln: Klassen + deren Mitglieder, Enums, alle Bezeichner
        // -----------------------------------------------------------

        private void Harvest()
        {
            int i = 0;
            while (i < _tokens.Count && _tokens[i].Type != TokenType.Eof)
            {
                if (_tokens[i].Type == TokenType.Class || _tokens[i].Type == TokenType.Actor)
                {
                    i = HarvestClass(i);
                }
                else if (_tokens[i].Type == TokenType.Enum)
                {
                    i = HarvestEnum(i);
                }
                else
                {
                    if (_tokens[i].Type == TokenType.Identifier)
                        AllDeclaredNames.Add(_tokens[i].Lexeme);
                    i++;
                }
            }
        }

        /// <summary>`class Name { ... }` / `actor Name { ... }` (beide
        /// gleich behandelt - 'actor' ist für diese Best-Effort-Vorschau
        /// keine eigene Kategorie, nur Klassen mit einer Mailbox zur
        /// Laufzeit, siehe Bytecode.RuntimeClass.IsActor) / `class extends X { ... }`
        /// / `actor extends X { ... }` (Erweiterung - siehe
        /// HarvestClassExtension, mergt in eine BESTEHENDE oder vorab
        /// angelegte ClassInfo statt eine neue anzulegen).</summary>
        private int HarvestClass(int i)
        {
            i++; // 'class'/'actor'

            if (i < _tokens.Count && _tokens[i].Type == TokenType.Extends)
                return HarvestClassExtension(i + 1);

            if (i >= _tokens.Count || _tokens[i].Type != TokenType.Identifier) return i;
            string className = _tokens[i].Lexeme;
            // Falls eine Erweiterung (siehe HarvestClassExtension) VOR der
            // echten Deklaration im Dokument stand, existiert schon ein
            // Platzhalter-ClassInfo mit deren Mitgliedern - den hier
            // WEITERBENUTZEN (nur die Deklarationszeile nachtragen) statt zu
            // überschreiben, sonst gingen die vorher gesammelten Mitglieder
            // verloren.
            var info = Classes.TryGetValue(className, out var existing)
                ? existing
                : new ClassInfo(className, _tokens[i].Line);
            info.DeclLine = _tokens[i].Line;
            i++;

            if (i < _tokens.Count && _tokens[i].Type == TokenType.Colon)
            {
                i++;
                if (i < _tokens.Count && _tokens[i].Type == TokenType.Identifier)
                    info.BaseName = _tokens[i].Lexeme; // erster Name reicht als grobe Näherung (echte Basis/Interface-Trennung macht der Resolver)
                while (i < _tokens.Count && _tokens[i].Type != TokenType.LBrace) i++;
            }

            Classes[className] = info;

            if (i >= _tokens.Count || _tokens[i].Type != TokenType.LBrace) return i;
            return HarvestMembersBody(i, info);
        }

        /// <summary>`class extends X { ... }` / `actor extends X { ... }`
        /// (siehe Parser.ParseClassExtensionDecl/MergeClassExtensions für die
        /// echte Semantik: Mitglieder wandern zur Compile-Zeit 1:1 in die
        /// ZIEL-Klasse) - hier entsprechend: Mitglieder direkt in die
        /// (bereits bekannte, oder noch unbekannte und dann vorab angelegte -
        /// die eigentliche Deklaration von X kann im Dokument vor ODER nach
        /// dieser Erweiterung stehen) ClassInfo von X eintragen, statt eine
        /// eigene neue Klasse zu erzeugen.</summary>
        private int HarvestClassExtension(int i)
        {
            if (i >= _tokens.Count || _tokens[i].Type != TokenType.Identifier) return i;
            string targetName = _tokens[i].Lexeme;
            i++;

            if (!Classes.TryGetValue(targetName, out var info))
            {
                info = new ClassInfo(targetName);
                Classes[targetName] = info;
            }

            if (i >= _tokens.Count || _tokens[i].Type != TokenType.LBrace) return i;
            return HarvestMembersBody(i, info);
        }

        /// <summary>Sammelt die Mitglieder eines Klassen-/Erweiterungs-Bodys
        /// ab dessen öffnender '{' bei `bodyStart` in `info` ein - gemeinsam
        /// genutzt von HarvestClass (echte Deklaration) und
        /// HarvestClassExtension (Erweiterung), da beide dieselbe
        /// Mitglieder-Grammatik haben.</summary>
        private int HarvestMembersBody(int bodyStart, ClassInfo info)
        {
            int bodyEnd = MatchBrace(bodyStart);
            int i = bodyStart + 1; // hinter die öffnende '{'
            string className = info.Name;

            while (i < bodyEnd)
            {
                if (_tokens[i].Type == TokenType.Construct)
                {
                    int parenAt = i + 1;
                    if (parenAt < bodyEnd && _tokens[parenAt].Type == TokenType.LParen)
                    {
                        int paramCount = CountParams(parenAt);
                        info.Members.Add(new MemberInfo(className, MemberKind.Constructor, paramCount, _tokens[i].Line));
                    }
                    i = SkipToNextMemberAfterBody(i, bodyEnd);
                }
                else if (_tokens[i].Type == TokenType.Destruct)
                {
                    i = SkipToNextMemberAfterBody(i, bodyEnd);
                }
                else if (_tokens[i].Type == TokenType.Operator)
                {
                    i = HarvestOperatorMember(i, bodyEnd, info);
                }
                else if (_tokens[i].Type == TokenType.Identifier || IsTypeKeyword(_tokens[i].Type)
                         || _tokens[i].Type == TokenType.Readonly)
                {
                    i = HarvestMember(i, bodyEnd, info);
                }
                else
                {
                    i++;
                }
            }

            return bodyEnd + 1;
        }

        /// <summary>Ein einzelnes Klassen-Mitglied ab Token-Index `i` (nach
        /// optionalem 'readonly' und optionalem Typ folgt der Name) - dieselbe
        /// grobe Heuristik wie Parser.NextLooksLikeTypeThenName (Typ-Keyword
        /// ODER zwei aufeinanderfolgende Bezeichner), nur ohne echte
        /// Ast-Konstruktion.</summary>
        private int HarvestMember(int i, int bodyEnd, ClassInfo info)
        {
            int start = i;
            if (_tokens[i].Type == TokenType.Readonly) i++;
            if (i >= bodyEnd) return bodyEnd;

            // Optionalen Typ überspringen (Typ-Keyword, oder zwei Identifier
            // hintereinander = "Klassenname Feldname").
            if (IsTypeKeyword(_tokens[i].Type))
            {
                i++;
            }
            else if (_tokens[i].Type == TokenType.Identifier && i + 1 < bodyEnd
                     && _tokens[i + 1].Type == TokenType.Identifier)
            {
                i++;
            }

            if (i >= bodyEnd || _tokens[i].Type != TokenType.Identifier)
                return start + 1; // kein erkennbares Mitglied - nur ein Token weiter, nicht hängen bleiben

            string name = _tokens[i].Lexeme;
            int afterName = i + 1;

            if (afterName < bodyEnd && _tokens[afterName].Type == TokenType.LParen)
            {
                int paramCount = CountParams(afterName);
                info.Members.Add(new MemberInfo(name, MemberKind.Method, paramCount, _tokens[i].Line));
                return SkipToNextMemberAfterBody(afterName, bodyEnd);
            }

            if (afterName < bodyEnd && _tokens[afterName].Type == TokenType.LBrace)
            {
                info.Members.Add(new MemberInfo(name, MemberKind.Property, 0, _tokens[i].Line));
                return SkipBraceBlock(afterName) + 1;
            }

            // Sonst: Feld (mit oder ohne Array-Klammern/Initializer) - bis zum
            // nächsten Bezeichner/Schlüsselwort auf Klassen-Ebene weiterlesen.
            info.Members.Add(new MemberInfo(name, MemberKind.Field, 0, _tokens[i].Line));
            return afterName;
        }

        /// <summary>`operator SYMBOL(params) { body }` (siehe Parser.
        /// ParseOperatorMember/ParseOperatorSymbol) - dieselbe grobe
        /// Token-Heuristik wie beim Rest dieser Klasse, ohne echte
        /// Ast-Konstruktion. Erfasst als MemberKind.Method mit einem
        /// lesbaren Namen ("operator+", "operator[]", ...) - für Navigation/
        /// Vervollständigung reicht das, die exakte interne Namenskonvention
        /// (GetIndex/SetIndex/"operator+") muss hier nicht repliziert
        /// werden.</summary>
        private int HarvestOperatorMember(int i, int bodyEnd, ClassInfo info)
        {
            int line = _tokens[i].Line;
            i++; // 'operator'
            if (i >= bodyEnd) return bodyEnd;

            string symbol;
            if (_tokens[i].Type == TokenType.LBracket && i + 1 < bodyEnd && _tokens[i + 1].Type == TokenType.RBracket)
            {
                symbol = "[]";
                i += 2;
            }
            else if ((_tokens[i].Type == TokenType.Lt && i + 1 < bodyEnd && _tokens[i + 1].Type == TokenType.Lt)
                     || (_tokens[i].Type == TokenType.Gt && i + 1 < bodyEnd && _tokens[i + 1].Type == TokenType.Gt))
            {
                symbol = _tokens[i].Type == TokenType.Lt ? "<<" : ">>";
                i += 2;
            }
            else
            {
                symbol = _tokens[i].Lexeme;
                i++;
            }

            if (i >= bodyEnd || _tokens[i].Type != TokenType.LParen)
                return i; // unerwartete Fortsetzung - nicht hängen bleiben

            int paramCount = CountParams(i);
            info.Members.Add(new MemberInfo("operator" + symbol, MemberKind.Method, paramCount, line));
            return SkipToNextMemberAfterBody(i, bodyEnd);
        }

        private int HarvestEnum(int i)
        {
            i++; // 'enum'
            if (i >= _tokens.Count || _tokens[i].Type != TokenType.Identifier) return i;
            string enumName = _tokens[i].Lexeme;
            EnumDeclLines[enumName] = _tokens[i].Line;
            i++;
            if (i >= _tokens.Count || _tokens[i].Type != TokenType.LBrace) return i;

            int bodyEnd = MatchBrace(i);
            var members = new List<string>();
            i++;
            while (i < bodyEnd)
            {
                if (_tokens[i].Type == TokenType.Identifier)
                {
                    members.Add(_tokens[i].Lexeme);
                    i++;
                    // optionalen '= wert' bis zum nächsten Komma überspringen
                    while (i < bodyEnd && _tokens[i].Type != TokenType.Comma) i++;
                }
                else
                {
                    i++;
                }
            }
            EnumMembers[enumName] = members;
            return bodyEnd + 1;
        }

        // -----------------------------------------------------------
        // Hilfsfunktionen über den Token-Strom
        // -----------------------------------------------------------

        private static bool IsTypeKeyword(TokenType t) =>
            t is TokenType.KwBool or TokenType.KwInt or TokenType.KwFloat or TokenType.KwChar or TokenType.KwString or TokenType.KwByte;

        /// <summary>Index der zu `_tokens[openBraceIdx]` (muss '{' sein)
        /// gehörenden schließenden '}'.</summary>
        private int MatchBrace(int openBraceIdx)
        {
            int depth = 0;
            for (int j = openBraceIdx; j < _tokens.Count; j++)
            {
                if (_tokens[j].Type == TokenType.LBrace) depth++;
                else if (_tokens[j].Type == TokenType.RBrace)
                {
                    depth--;
                    if (depth == 0) return j;
                }
            }
            return _tokens.Count - 1;
        }

        private int SkipBraceBlock(int openBraceIdx) => MatchBrace(openBraceIdx);

        /// <summary>Nach einer Methoden-/Konstruktor-Signatur (Parameter
        /// gelesen, Index zeigt auf oder vor dem Body-'{') bis hinter den
        /// zugehörigen Body springen.</summary>
        private int SkipToNextMemberAfterBody(int fromIdx, int bodyEnd)
        {
            int j = fromIdx;
            while (j < bodyEnd && _tokens[j].Type != TokenType.LBrace) j++;
            if (j >= bodyEnd) return bodyEnd;
            return MatchBrace(j) + 1;
        }

        /// <summary>Grobe Parameteranzahl einer Klammerliste ab der öffnenden
        /// '(' bei `parenIdx` - zählt Kommas auf Klammer-Tiefe 1, nicht exakt
        /// (Default-Werte mit Kommas darin würden verfälschen), reicht aber
        /// für eine Anzeige-Zahl in der Vervollständigung.</summary>
        private int CountParams(int parenIdx)
        {
            int depth = 0;
            int commas = 0;
            bool any = false;
            for (int j = parenIdx; j < _tokens.Count; j++)
            {
                if (_tokens[j].Type == TokenType.LParen) depth++;
                else if (_tokens[j].Type == TokenType.RParen)
                {
                    depth--;
                    if (depth == 0) break;
                }
                else if (depth == 1)
                {
                    any = true;
                    if (_tokens[j].Type == TokenType.Comma) commas++;
                }
            }
            return any ? commas + 1 : 0;
        }

        // -----------------------------------------------------------
        // Alle Mitglieder einer Klasse inkl. Basisklassen-Kette (so weit im
        // selben Dokument bekannt - stoppt an einer unbekannten/externen
        // Basisklasse).
        // -----------------------------------------------------------

        public IEnumerable<MemberInfo> MembersOf(string className)
        {
            var seen = new HashSet<string>();
            var visitedClasses = new HashSet<string>();
            string? current = className;
            while (current != null && Classes.TryGetValue(current, out var info) && visitedClasses.Add(current))
            {
                foreach (var m in info.Members)
                    if (seen.Add(m.Name))
                        yield return m;
                current = info.BaseName;
            }
        }

        // -----------------------------------------------------------
        // Kontext an einer Cursor-Position (Zeichen-Offset im Quelltext)
        // -----------------------------------------------------------

        /// <summary>Name der Klasse, deren Body die Cursor-Position `offset`
        /// enthält (für 'this.'), oder null außerhalb jeder Klasse.</summary>
        public string? EnclosingClassAt(int offset)
        {
            string? best = null;
            int i = 0;
            while (i < _tokens.Count && _tokens[i].Type != TokenType.Eof)
            {
                bool isClassOrActor = _tokens[i].Type == TokenType.Class || _tokens[i].Type == TokenType.Actor;
                if (isClassOrActor && i + 1 < _tokens.Count && _tokens[i + 1].Type == TokenType.Extends
                    && i + 2 < _tokens.Count && _tokens[i + 2].Type == TokenType.Identifier)
                {
                    // 'class extends X { ... }' / 'actor extends X { ... }' -
                    // 'this' innerhalb der Erweiterung gehört zur ZIEL-Klasse
                    // X (siehe Parser.ParseClassExtensionDecl), nicht zu
                    // einer eigenen, neuen Klasse.
                    string extName = _tokens[i + 2].Lexeme;
                    int ej = i + 3;
                    while (ej < _tokens.Count && _tokens[ej].Type != TokenType.LBrace) ej++;
                    if (ej >= _tokens.Count) break;
                    int eEnd = MatchBrace(ej);
                    int eStart = OffsetOf(_tokens[ej]);
                    int eEndOffset = OffsetOf(_tokens[eEnd]) + 1;
                    if (offset >= eStart && offset <= eEndOffset)
                        best = extName;
                    i = eEnd + 1;
                }
                else if (isClassOrActor && i + 1 < _tokens.Count && _tokens[i + 1].Type == TokenType.Identifier)
                {
                    string name = _tokens[i + 1].Lexeme;
                    int j = i + 2;
                    while (j < _tokens.Count && _tokens[j].Type != TokenType.LBrace) j++;
                    if (j >= _tokens.Count) break;
                    int end = MatchBrace(j);
                    int startOffset = OffsetOf(_tokens[j]);
                    int endOffset = OffsetOf(_tokens[end]) + 1;
                    if (offset >= startOffset && offset <= endOffset)
                        best = name; // die INNERSTE passende Klasse gewinnt (spätere, engere Treffer überschreiben)
                    i = end + 1;
                }
                else
                {
                    i++;
                }
            }
            return best;
        }

        /// <summary>Parameter (Name, evtl. Typname) der unmittelbar
        /// umschließenden Methode/des Konstruktors/Lambdas an `offset` - die
        /// INNERSTE Funktionssignatur, deren Body `offset` enthält.</summary>
        public List<(string Name, string? TypeName)> EnclosingFunctionParams(int offset)
        {
            var result = new List<(string, string?)>();
            (int Start, int End, List<(string, string?)> Params)? best = null;

            for (int i = 0; i < _tokens.Count; i++)
            {
                bool isFuncKeyword = _tokens[i].Type is TokenType.Construct or TokenType.Func;
                bool isNamedMethodStart = _tokens[i].Type == TokenType.Identifier
                    && i + 1 < _tokens.Count && _tokens[i + 1].Type == TokenType.LParen;

                int parenIdx = -1;
                if (isFuncKeyword && i + 1 < _tokens.Count && _tokens[i + 1].Type == TokenType.LParen)
                    parenIdx = i + 1;
                else if (isNamedMethodStart)
                    parenIdx = i + 1;

                if (parenIdx < 0) continue;

                var parms = ReadParamList(parenIdx, out int afterParen);
                int j = afterParen;
                while (j < _tokens.Count && _tokens[j].Type != TokenType.LBrace
                       && _tokens[j].Type != TokenType.Semicolon && _tokens[j].Type != TokenType.Eof) j++;
                if (j >= _tokens.Count || _tokens[j].Type != TokenType.LBrace) continue;

                int end = MatchBrace(j);
                int startOffset = OffsetOf(_tokens[j]);
                int endOffset = OffsetOf(_tokens[end]) + 1;
                if (offset >= startOffset && offset <= endOffset)
                    best = (startOffset, endOffset, parms); // innerster (zuletzt gefundener, umschließender) Treffer gewinnt
            }

            return best?.Params ?? result;
        }

        private List<(string Name, string? TypeName)> ReadParamList(int parenIdx, out int afterParen)
        {
            var result = new List<(string, string?)>();
            int depth = 0;
            int j = parenIdx;
            var currentTokens = new List<Token>();
            for (; j < _tokens.Count; j++)
            {
                if (_tokens[j].Type == TokenType.LParen) { depth++; if (depth == 1) continue; }
                else if (_tokens[j].Type == TokenType.RParen)
                {
                    depth--;
                    if (depth == 0) { FlushParam(currentTokens, result); j++; break; }
                }
                if (depth >= 1)
                {
                    if (_tokens[j].Type == TokenType.Comma && depth == 1)
                    {
                        FlushParam(currentTokens, result);
                        currentTokens.Clear();
                    }
                    else if (!(depth == 1 && _tokens[j].Type == TokenType.LParen))
                    {
                        currentTokens.Add(_tokens[j]);
                    }
                }
            }
            afterParen = j;
            return result;

            static void FlushParam(List<Token> toks, List<(string, string?)> outList)
            {
                // Letzter Identifier in der Gruppe ist der Parametername, ein
                // davor stehender Identifier/Typ-Keyword (falls vorhanden) der
                // Typname - passt zu "[Typ] Name" wie im Rest der Sprache.
                var idents = toks.FindAll(t => t.Type == TokenType.Identifier || IsTypeKeyword(t.Type));
                if (idents.Count == 0) return;
                string name = idents[^1].Lexeme;
                string? type = idents.Count >= 2 ? idents[^2].Lexeme : null;
                outList.Add((name, type));
            }
        }

        /// <summary>Namen aller `var`-Deklarationen zwischen dem Beginn der
        /// unmittelbar umschließenden Funktion und `offset` (textuell, KEIN
        /// echtes Block-Scope-Tracking - eine Variable aus einem bereits
        /// verlassenen Geschwister-Block wird hier auch noch vorgeschlagen;
        /// bewusste Vereinfachung für Autovervollständigung).</summary>
        public List<string> LocalVarsBeforeCursor(int offset)
        {
            var result = new List<string>();
            for (int i = 0; i < _tokens.Count - 1; i++)
            {
                //Debug.WriteLine($"Offset i: {OffsetOf(_tokens[i])} Offset i+1: {OffsetOf(_tokens[i + 1])} Offset Cursor: {offset}");
                if (OffsetOf(_tokens[i]) >= offset) break;
                if (_tokens[i].Type == TokenType.Var && _tokens[i + 1].Type == TokenType.Identifier)
                    if (OffsetOf(_tokens[i + 1]) < offset)
                    {
                        //Debug.WriteLine(_tokens[i + 1].Lexeme);
                        result.Add(_tokens[i + 1].Lexeme);
                    }
            }
            return result;
        }

        /// <summary>Bestmöglicher Versuch, den deklarierten Klassen-Typ eines
        /// Bezeichners `name` zu finden, der VOR `offset` im Quelltext liegt:
        /// als `var name : Typ`, als "nackte" Deklaration `Typ name`, oder als
        /// Parameter der umschließenden Funktion mit Typ. Nur wenn `Typ` einer
        /// bekannten Klasse entspricht (sonst wäre eine Member-Liste ohnehin
        /// nicht sinnvoll anzeigbar). Liefert null, wenn nichts Passendes
        /// gefunden wird - dynamische Typisierung heißt, das ist der
        /// Normalfall, kein Fehler.</summary>
        public string? TryResolveDeclaredType(int offset, string name)
        {
            foreach (var (pName, pType) in EnclosingFunctionParams(offset))
                if (pName == name && pType != null && Classes.ContainsKey(pType))
                    return pType;

            string? found = null;
            for (int i = 0; i < _tokens.Count - 1; i++)
            {
                if (OffsetOf(_tokens[i]) >= offset) break;

                // 'var name : Typ'
                if (_tokens[i].Type == TokenType.Var && i + 3 < _tokens.Count
                    && _tokens[i + 1].Type == TokenType.Identifier && _tokens[i + 1].Lexeme == name
                    && _tokens[i + 2].Type == TokenType.Colon && _tokens[i + 3].Type == TokenType.Identifier
                    && Classes.ContainsKey(_tokens[i + 3].Lexeme))
                {
                    found = _tokens[i + 3].Lexeme;
                }
                // 'Typ name' (nackte Deklaration, zwei Identifier hintereinander)
                else if (_tokens[i].Type == TokenType.Identifier && Classes.ContainsKey(_tokens[i].Lexeme)
                         && i + 1 < _tokens.Count && _tokens[i + 1].Type == TokenType.Identifier
                         && _tokens[i + 1].Lexeme == name)
                {
                    found = _tokens[i].Lexeme;
                }
            }
            return found;
        }

        private int OffsetOf(Token token)
        {
            int idx = token.Line - 1;
            if (idx < 0 || idx >= _lineStarts.Length) return 0;
            return _lineStarts[idx] + (token.Column - 1);
        }
    }
}
