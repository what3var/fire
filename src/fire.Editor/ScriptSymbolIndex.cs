using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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

    public enum MemberAccess
    {
        Public,
        Protected,
        Private,
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

        /// <summary>Der deklarierte Typ eines Feldes/einer Property bzw. der
        /// Rückgabetyp einer Methode (Klassenname oder Typ-Keyword wie `int`),
        /// `null`, wenn keiner angegeben ist (dynamische Typisierung - der
        /// Normalfall, siehe ScriptSymbolIndex.InferReturnType für
        /// Methoden). Bei `TypeIsArray` der ELEMENT-Typ (`Foo items[]`).</summary>
        public string? TypeName { get; init; }
        public bool TypeIsArray { get; init; }

        public bool IsStatic { get; init; }
        public MemberAccess Access { get; init; } = MemberAccess.Public;

        /// <summary>Die Parameterliste einer Methode als Text (`int a, Foo b`),
        /// für die Anzeige in der Vervollständigung.</summary>
        public string? Signature { get; init; }

        /// <summary>Name der Klasse, die dieses Mitglied deklariert.</summary>
        public string Owner { get; internal set; } = string.Empty;

        // Woher die Token-Indizes unten stammen (jeder Index - auch der der
        // Prelude - hat seinen EIGENEN Token-Strom) und wo der Body einer
        // Methode liegt (für die Rückgabetyp-Herleitung, siehe
        // ScriptSymbolIndex.InferReturnType). BodyStart = -1: kein Body.
        internal ScriptSymbolIndex? Source { get; set; }
        internal int BodyStart { get; init; } = -1;
        internal int BodyEnd { get; init; } = -1;

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

        /// <summary>Alle Namen nach dem ':' im Klassenkopf (Basisklasse UND
        /// Interfaces - welcher davon die echte Basisklasse ist, entscheidet
        /// erst der Resolver; für Vervollständigung sind alle gleich
        /// nützlich), in Quelltext-Reihenfolge.</summary>
        public List<string> BaseNames { get; } = new();

        /// <summary>Der ERSTE Name aus <see cref="BaseNames"/> (grobe
        /// Näherung der Basisklasse), null ohne Basis.</summary>
        public string? BaseName
        {
            get => BaseNames.Count > 0 ? BaseNames[0] : null;
            set
            {
                if (value != null && !BaseNames.Contains(value))
                    BaseNames.Insert(0, value);
            }
        }

        public List<MemberInfo> Members { get; } = new();

        /// <summary>Token-Bereiche (Index der öffnenden '{', der schließenden
        /// '}') aller Bodys dieser Klasse im Token-Strom von
        /// <see cref="MemberInfo.Source"/> - mehrere bei `class extends X`
        /// (siehe ScriptSymbolIndex.InferFieldType).</summary>
        internal List<(int Start, int End)> BodyRanges { get; } = new();

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

        /// <summary>true bei einem `interface` (nur Signaturen, nie mit `new`
        /// instanziierbar).</summary>
        public bool IsInterface { get; set; }

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
    public sealed partial class ScriptSymbolIndex
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
        private bool _suppressDeclLines;

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
            index.MergeInPrelude(PreludeIndex.Value);
            // Preludes der per '#import' zugeschalteten Erweiterungen (siehe
            // ImportedPreludes) - `Framebuffer`/`Device`/... sollen genauso
            // vervollständigt werden wie die Standardbibliothek.
            foreach (var importName in ImportedPreludes.FindImportNames(source))
            {
                var importIndex = ImportedPreludeIndex(importName);
                if (importIndex != null) index.MergeInPrelude(importIndex);
            }
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
        private void MergeInPrelude(ScriptSymbolIndex prelude)
        {
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

        private static readonly Dictionary<string, ScriptSymbolIndex?> ImportedPreludeIndexes = new();

        /// <summary>Der Index der Prelude einer per `#import "name"`
        /// zugeschalteten Erweiterung (einmalig gebaut und gemerkt), null
        /// bei einem unbekannten Namen. OHNE Definitionszeilen (siehe
        /// DeclLineOf): deren Quelltext lässt sich im Editor nirgends
        /// anzeigen.</summary>
        private static ScriptSymbolIndex? ImportedPreludeIndex(string importName)
        {
            lock (ImportedPreludeIndexes)
            {
                if (ImportedPreludeIndexes.TryGetValue(importName, out var cached)) return cached;
                string? preludeSource = ImportedPreludes.TrySourceFor(importName);
                var index = preludeSource == null ? null : Build(preludeSource, isPrelude: true, suppressDeclLines: true);
                ImportedPreludeIndexes[importName] = index;
                return index;
            }
        }

        /// <summary>Interne Build-Überladung für den Prelude-Quelltext selbst
        /// (siehe PreludeIndex) - baut NUR den rohen Index (keine rekursive
        /// MergeInPrelude(), die Prelude braucht sich ja nicht selbst
        /// einzumischen) und markiert danach jede gefundene Klasse als
        /// IsFromPrelude.</summary>
        private static ScriptSymbolIndex Build(string source, bool isPrelude, bool suppressDeclLines = false)
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

            var index = new ScriptSymbolIndex(source, tokens) { _suppressDeclLines = suppressDeclLines };
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
                else if (_tokens[i].Type == TokenType.Interface)
                {
                    i = HarvestClass(i, isInterface: true);
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

        /// <summary>Die Zeile, die ein Symbol als Definitionsort bekommt - 0
        /// bei einem Index, dessen Zeilen nirgends angezeigt werden können
        /// (die Preludes zugeschalteter Erweiterungen, siehe
        /// BuildImportedPrelude), damit "zu Definition springen" dort nicht
        /// zu einer Zeile eines FREMDEN Quelltexts springt.</summary>
        private int DeclLineOf(Token token) => _suppressDeclLines ? 0 : token.Line;

        private void AddMember(ClassInfo info, MemberInfo member)
        {
            member.Owner = info.Name;
            member.Source = this;
            info.Members.Add(member);
        }

        /// <summary>Index hinter das zu `_tokens[ltIdx]` (muss `&lt;` sein)
        /// gehörende `&gt;` - für `class Box&lt;T&gt;` und generische
        /// Methoden. Verschachtelte `&lt;...&gt;` werden mitgezählt.</summary>
        private int SkipAngleBrackets(int ltIdx)
        {
            int depth = 0;
            for (int j = ltIdx; j < _tokens.Count; j++)
            {
                if (_tokens[j].Type == TokenType.Lt) depth++;
                else if (_tokens[j].Type == TokenType.Gt)
                {
                    depth--;
                    if (depth == 0) return j + 1;
                }
                else if (_tokens[j].Type is TokenType.LBrace or TokenType.LParen or TokenType.Eof)
                    return ltIdx + 1; // kein echtes Typ-Argument-Paar - nicht hängen bleiben
            }
            return ltIdx + 1;
        }

        /// <summary>`class Name { ... }` / `actor Name { ... }` (beide
        /// gleich behandelt - 'actor' ist für diese Best-Effort-Vorschau
        /// keine eigene Kategorie, nur Klassen mit einer Mailbox zur
        /// Laufzeit, siehe Bytecode.RuntimeClass.IsActor) / `class extends X { ... }`
        /// / `actor extends X { ... }` (Erweiterung - siehe
        /// HarvestClassExtension, mergt in eine BESTEHENDE oder vorab
        /// angelegte ClassInfo statt eine neue anzulegen).</summary>
        private int HarvestClass(int i, bool isInterface = false)
        {
            i++; // 'class'/'actor'/'interface'

            if (!isInterface && i < _tokens.Count && _tokens[i].Type == TokenType.Extends)
                return HarvestClassExtension(i + 1);

            if (i >= _tokens.Count || _tokens[i].Type != TokenType.Identifier) return i;

            // 'class Name(' ist keine Klasse, sondern ein Rückgabetyp 'class'
            // vor einer Methode (z.B. 'class GetCurrent()' in einem Interface).
            if (i + 1 < _tokens.Count && _tokens[i + 1].Type == TokenType.LParen) return i;

            string className = _tokens[i].Lexeme;
            // Falls eine Erweiterung (siehe HarvestClassExtension) VOR der
            // echten Deklaration im Dokument stand, existiert schon ein
            // Platzhalter-ClassInfo mit deren Mitgliedern - den hier
            // WEITERBENUTZEN (nur die Deklarationszeile nachtragen) statt zu
            // überschreiben, sonst gingen die vorher gesammelten Mitglieder
            // verloren.
            var info = Classes.TryGetValue(className, out var existing)
                ? existing
                : new ClassInfo(className, DeclLineOf(_tokens[i]));
            info.DeclLine = DeclLineOf(_tokens[i]);
            info.IsInterface = isInterface;
            i++;

            // Generische Klasse ('class Name<T> ...') - die Typ-Parameter
            // überspringen, sonst wäre der Rest des Kopfes nicht erkennbar
            // und die Klasse hätte keinerlei Mitglieder.
            if (i < _tokens.Count && _tokens[i].Type == TokenType.Lt)
                i = SkipAngleBrackets(i);

            // ': Basis, Interface, ...' - ALLE Namen merken (welcher davon
            // die echte Basisklasse ist, entscheidet erst der Resolver).
            if (i < _tokens.Count && _tokens[i].Type == TokenType.Colon)
            {
                i++;
                while (i < _tokens.Count && _tokens[i].Type != TokenType.LBrace && _tokens[i].Type != TokenType.Where
                       && _tokens[i].Type != TokenType.Eof)
                {
                    if (_tokens[i].Type == TokenType.Identifier && !info.BaseNames.Contains(_tokens[i].Lexeme))
                        info.BaseNames.Add(_tokens[i].Lexeme);
                    i++;
                }
            }

            // 'where'-Klauseln (Generics) bis zum Body überspringen.
            while (i < _tokens.Count && _tokens[i].Type != TokenType.LBrace && _tokens[i].Type != TokenType.Eof) i++;

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
            info.BodyRanges.Add((bodyStart, bodyEnd));
            int i = bodyStart + 1; // hinter die öffnende '{'
            string className = info.Name;

            // Modifikatoren stehen VOR dem eigentlichen Mitglied und gelten
            // für das nächste erkannte Mitglied.
            bool isStatic = false;
            var access = MemberAccess.Public;

            while (i < bodyEnd)
            {
                var type = _tokens[i].Type;
                if (type == TokenType.Static) { isStatic = true; i++; continue; }
                if (type == TokenType.Public) { access = MemberAccess.Public; i++; continue; }
                if (type == TokenType.Private) { access = MemberAccess.Private; i++; continue; }
                if (type == TokenType.Protected) { access = MemberAccess.Protected; i++; continue; }

                if (type == TokenType.Construct)
                {
                    int parenAt = i + 1;
                    if (parenAt < bodyEnd && _tokens[parenAt].Type == TokenType.LParen)
                    {
                        int paramCount = CountParams(parenAt);
                        AddMember(info, new MemberInfo(className, MemberKind.Constructor, paramCount, DeclLineOf(_tokens[i]))
                        {
                            Access = access,
                            Signature = FormatSignature(ReadParamList(parenAt, out _)),
                        });
                    }
                    i = SkipToNextMemberAfterBody(i, bodyEnd);
                }
                else if (type == TokenType.Destruct)
                {
                    i = SkipToNextMemberAfterBody(i, bodyEnd);
                }
                else if (type == TokenType.Operator)
                {
                    i = HarvestOperatorMember(i, bodyEnd, info);
                }
                else if (type == TokenType.Identifier || IsTypeKeyword(type) || type == TokenType.Readonly)
                {
                    i = HarvestMember(i, bodyEnd, info, isStatic, access);
                }
                else
                {
                    i++;
                    continue; // kein Mitglied - Modifikatoren NICHT zurücksetzen
                }

                isStatic = false;
                access = MemberAccess.Public;
            }

            return bodyEnd + 1;
        }

        private static string FormatSignature(List<(string Name, string? TypeName)> parms) =>
            string.Join(", ", parms.Select(p => p.TypeName != null ? p.TypeName + " " + p.Name : p.Name));

        /// <summary>Überspringt hinter einem Typ-Keyword eine Bitbreite
        /// (`int[16]`) und Pointer-Sterne (`int*`), siehe TypeRef.</summary>
        private int SkipTypeSuffix(int i, int end)
        {
            if (i + 2 < end && _tokens[i].Type == TokenType.LBracket
                && _tokens[i + 1].Type == TokenType.IntLiteral && _tokens[i + 2].Type == TokenType.RBracket)
                i += 3;
            while (i < end && _tokens[i].Type == TokenType.Star) i++;
            return i;
        }

        /// <summary>Index des ersten Tokens NACH einem Ausdruck, der bei
        /// `startIdx` beginnt: das Ende ist ein `;`, ein schließendes
        /// '}'/')'/']' oder ein Zeilenumbruch - jeweils nur auf
        /// Klammertiefe 0 (mehrzeilige Argumentlisten/Lambdas bleiben also
        /// EIN Ausdruck), wie bei der Statement-Trennung des Parsers.</summary>
        internal int ExpressionEnd(int startIdx, int limit)
        {
            int depth = 0;
            for (int k = startIdx; k < limit; k++)
            {
                var t = _tokens[k];
                if (t.Type == TokenType.Eof) return k;
                if (depth == 0 && k > startIdx && (t.NewlineBefore || t.Type == TokenType.Semicolon
                    || t.Type is TokenType.RBrace or TokenType.RParen or TokenType.RBracket or TokenType.Comma))
                    return k;
                if (t.Type is TokenType.LParen or TokenType.LBracket or TokenType.LBrace) depth++;
                else if (t.Type is TokenType.RParen or TokenType.RBracket or TokenType.RBrace) depth--;
            }
            return limit;
        }

        /// <summary>Ein einzelnes Klassen-Mitglied ab Token-Index `i` (nach
        /// optionalem 'readonly' und optionalem Typ folgt der Name) - dieselbe
        /// grobe Heuristik wie Parser.NextLooksLikeTypeThenName (Typ-Keyword
        /// ODER zwei aufeinanderfolgende Bezeichner), nur ohne echte
        /// Ast-Konstruktion.</summary>
        private int HarvestMember(int i, int bodyEnd, ClassInfo info, bool isStatic, MemberAccess access)
        {
            int start = i;
            if (_tokens[i].Type == TokenType.Readonly) i++;
            if (i >= bodyEnd) return bodyEnd;

            // Optionalen Typ merken/überspringen (Typ-Keyword, oder zwei
            // Identifier hintereinander = "Klassenname Feldname").
            string? typeName = null;
            if (IsTypeKeyword(_tokens[i].Type))
            {
                typeName = _tokens[i].Lexeme;
                i = SkipTypeSuffix(i + 1, bodyEnd);
            }
            else if (_tokens[i].Type == TokenType.Identifier && i + 1 < bodyEnd
                     && _tokens[i + 1].Type == TokenType.Identifier && !_tokens[i + 1].NewlineBefore)
            {
                typeName = _tokens[i].Lexeme;
                i++;
            }

            if (i >= bodyEnd || _tokens[i].Type != TokenType.Identifier)
                return start + 1; // kein erkennbares Mitglied - nur ein Token weiter, nicht hängen bleiben

            string name = _tokens[i].Lexeme;
            int nameIdx = i;
            int afterName = i + 1;

            // Generische Methode ('Name<T>(...)') - Typ-Parameter überspringen.
            if (afterName < bodyEnd && _tokens[afterName].Type == TokenType.Lt)
                afterName = SkipAngleBrackets(afterName);

            if (afterName < bodyEnd && _tokens[afterName].Type == TokenType.LParen)
            {
                int paramCount = CountParams(afterName);
                var parms = ReadParamList(afterName, out int afterParams);

                // Body direkt hinter der Signatur - eine Interface-Methode hat
                // keinen (dann endet das Mitglied hinter der ')').
                int bodyOpen = FindBodyBrace(afterParams);
                int bodyClose = bodyOpen >= 0 && bodyOpen < bodyEnd ? MatchBrace(bodyOpen) : -1;

                AddMember(info, new MemberInfo(name, MemberKind.Method, paramCount, DeclLineOf(_tokens[nameIdx]))
                {
                    TypeName = typeName,
                    IsStatic = isStatic,
                    Access = access,
                    Signature = FormatSignature(parms),
                    BodyStart = bodyClose >= 0 ? bodyOpen : -1,
                    BodyEnd = bodyClose,
                });
                return bodyClose >= 0 ? bodyClose + 1 : Math.Min(afterParams, bodyEnd);
            }

            if (afterName < bodyEnd && _tokens[afterName].Type == TokenType.LBrace)
            {
                AddMember(info, new MemberInfo(name, MemberKind.Property, 0, DeclLineOf(_tokens[nameIdx]))
                {
                    TypeName = typeName,
                    IsStatic = isStatic,
                    Access = access,
                });
                return SkipBraceBlock(afterName) + 1;
            }

            // Sonst: Feld. Array-Deklarator ('Typ name[]') und Initialisierer
            // ('= ...') mit überspringen - sonst würden Bezeichner DARIN
            // (z.B. 'new Foo()') als eigene Mitglieder missverstanden.
            bool isArray = false;
            while (afterName < bodyEnd && _tokens[afterName].Type == TokenType.LBracket)
            {
                isArray = true;
                int close = afterName;
                int depth = 0;
                for (; close < bodyEnd; close++)
                {
                    if (_tokens[close].Type == TokenType.LBracket) depth++;
                    else if (_tokens[close].Type == TokenType.RBracket && --depth == 0) break;
                }
                afterName = close + 1;
            }

            if (afterName < bodyEnd && _tokens[afterName].Type == TokenType.Assign)
            {
                // Ohne deklarierten Typ aus einem 'new X(...)'-Initialisierer
                // schließen (der häufigste Fall bei dynamisch typisierten Feldern).
                if (typeName == null && afterName + 2 < bodyEnd && _tokens[afterName + 1].Type == TokenType.New
                    && _tokens[afterName + 2].Type == TokenType.Identifier)
                {
                    typeName = _tokens[afterName + 2].Lexeme;
                    isArray = afterName + 3 < bodyEnd && _tokens[afterName + 3].Type == TokenType.LBracket;
                }
                afterName = ExpressionEnd(afterName + 1, bodyEnd);
            }

            AddMember(info, new MemberInfo(name, MemberKind.Field, 0, DeclLineOf(_tokens[nameIdx]))
            {
                TypeName = typeName,
                TypeIsArray = isArray,
                IsStatic = isStatic,
                Access = access,
            });
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
            int line = DeclLineOf(_tokens[i]);
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
            var parms = ReadParamList(i, out int afterParams);
            int bodyOpen = FindBodyBrace(afterParams);
            int bodyClose = bodyOpen >= 0 && bodyOpen < bodyEnd ? MatchBrace(bodyOpen) : -1;
            AddMember(info, new MemberInfo("operator" + symbol, MemberKind.Method, paramCount, line)
            {
                Signature = FormatSignature(parms),
                BodyStart = bodyClose >= 0 ? bodyOpen : -1,
                BodyEnd = bodyClose,
            });
            return bodyClose >= 0 ? bodyClose + 1 : Math.Min(afterParams, bodyEnd);
        }

        private int HarvestEnum(int i)
        {
            i++; // 'enum'
            if (i >= _tokens.Count || _tokens[i].Type != TokenType.Identifier) return i;
            string enumName = _tokens[i].Lexeme;
            EnumDeclLines[enumName] = DeclLineOf(_tokens[i]);
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

        public IEnumerable<MemberInfo> MembersOf(string className) =>
            MembersOfWithDepth(className).Select(x => x.Member);

        /// <summary>Wie <see cref="MembersOf"/>, mit dem Abstand der
        /// deklarierenden Klasse zu `className` (0 = die Klasse selbst, 1 =
        /// direkte Basis/Interface, ...) - breitensuchend über ALLE Basisnamen
        /// (siehe ClassInfo.BaseNames); ein Mitglied, das eine nähere Klasse
        /// schon deklariert (Überschreiben), erscheint nur dort.</summary>
        public IEnumerable<(MemberInfo Member, int Depth)> MembersOfWithDepth(string className)
        {
            var seen = new HashSet<string>();
            var visited = new HashSet<string> { className };
            var queue = new Queue<(string Name, int Depth)>();
            queue.Enqueue((className, 0));
            while (queue.Count > 0)
            {
                var (current, depth) = queue.Dequeue();
                if (!Classes.TryGetValue(current, out var info)) continue;
                foreach (var m in info.Members)
                    if (seen.Add(m.Kind + ":" + m.Name + "/" + m.ParamCount))
                        yield return (m, depth);
                foreach (var baseName in info.BaseNames)
                    if (visited.Add(baseName))
                        queue.Enqueue((baseName, depth + 1));
            }
        }

        /// <summary>Ist `ancestor` (direkt oder über mehrere Stufen) eine Basis
        /// von `className`?</summary>
        public bool DerivesFrom(string className, string ancestor)
        {
            var visited = new HashSet<string> { className };
            var queue = new Queue<string>();
            queue.Enqueue(className);
            while (queue.Count > 0)
            {
                if (!Classes.TryGetValue(queue.Dequeue(), out var info)) continue;
                foreach (var baseName in info.BaseNames)
                {
                    if (baseName == ancestor) return true;
                    if (visited.Add(baseName)) queue.Enqueue(baseName);
                }
            }
            return false;
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

        private int OffsetOf(Token token)
        {
            int idx = token.Line - 1;
            if (idx < 0 || idx >= _lineStarts.Length) return 0;
            return _lineStarts[idx] + (token.Column - 1);
        }

        /// <summary>Zeichen-Offset jedes Tokens (parallel zu `_tokens`),
        /// einmalig berechnet - die Typ-Herleitung (siehe ScriptSymbolIndex.
        /// Types) vergleicht sehr oft Offsets.</summary>
        private int[]? _tokenOffsets;
        private int TokenOffset(int tokenIdx)
        {
            if (_tokenOffsets == null)
            {
                var offsets = new int[_tokens.Count];
                for (int k = 0; k < offsets.Length; k++) offsets[k] = OffsetOf(_tokens[k]);
                _tokenOffsets = offsets;
            }
            return _tokenOffsets[tokenIdx];
        }
    }
}
