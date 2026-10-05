using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using fire.Compiler;
using fire.Lexing;
using fire.Standard;

namespace fire.Editor
{
    public enum MemberKind
    {
        Field,
        Method,
        Property,
        Constructor,
    }

    public enum NamespaceMemberKind
    {
        Namespace,
        Class,
        Interface,
        Enum,
    }

    /// <summary>Ein Eintrag IN einem Namespace: `Name` einfach, `FullName`
    /// vollqualifiziert (Schlüssel in Classes/EnumMembers/Namespaces).</summary>
    public sealed record NamespaceMember(string Name, NamespaceMemberKind Kind, string FullName);

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

        /// <summary>The `///` documentation comment above the declaration, null if there is none.</summary>
        public DocComment? Documentation => DeclLine > 0 ? Source?.DocumentationAt(DeclLine) : null;

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
        /// <summary>Der VOLLQUALIFIZIERTE Name (`Geometry.Circle`, ohne
        /// Namespace nur `Circle`) - der Schlüssel in
        /// <see cref="ScriptSymbolIndex.Classes"/>, wie beim echten Compiler
        /// (siehe Parser.QualifyDeclName).</summary>
        public string Name { get; }

        /// <summary>Der Name ohne Namespace (`Circle`).</summary>
        public string SimpleName { get; }

        /// <summary>Der Namespace, in dem die Klasse deklariert ist
        /// (`Geometry`, `A.B`), leer ohne Namespace.</summary>
        public string Namespace { get; }

        /// <summary>Die Namespaces, gegen die Typnamen in der Deklaration dieser
        /// Klasse (Basisklassen, Feld-/Rückgabetypen) aufgelöst werden: ihr
        /// eigener Namespace zuerst, dann die `#using`-Namen ihrer Datei (siehe
        /// TypeRef.ResolveBaseName).</summary>
        public IReadOnlyList<string> Context { get; init; } = System.Array.Empty<string>();

        /// <summary>Der Index, dessen Token-Strom diese Klasse beschreibt (bei
        /// einer Prelude-Klasse der der Prelude).</summary>
        internal ScriptSymbolIndex? Source { get; set; }

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

        /// <summary>The `///` documentation comment above the class declaration, null if there is none.</summary>
        public DocComment? Documentation => DeclLine > 0 ? Source?.DocumentationAt(DeclLine) : null;

        /// <summary>true, wenn diese Klasse aus der eingebauten
        /// Standardbibliothek stammt (siehe ScriptSymbolIndex.
        /// MergeInPrelude/PreludeIndex), NICHT aus dem gerade bearbeiteten
        /// Dokument selbst - "zu Definition springen" muss dafür die
        /// Prelude in einem eigenen, schreibgeschützten Popup zeigen (siehe
        /// NavigationEngine/MainWindow.ShowPreludeSource) statt im
        /// Hauptdokument zu einer (dort gar nicht existierenden) Zeile zu
        /// scrollen.</summary>
        public bool IsFromPrelude => PreludeName != null;

        /// <summary>Aus welcher Prelude die Klasse stammt: <see cref="ScriptSymbolIndex.StandardPreludeName"/>
        /// für die Standardbibliothek, sonst der Name der Erweiterung (`graphics`, `time`, ...);
        /// null = steht im bearbeiteten Dokument selbst.</summary>
        public string? PreludeName { get; set; }

        /// <summary>true bei einem `interface` (nur Signaturen, nie mit `new`
        /// instanziierbar).</summary>
        public bool IsInterface { get; set; }

        public ClassInfo(string name, int declLine = 0)
        {
            Name = name;
            DeclLine = declLine;
            int dot = name.LastIndexOf('.');
            Namespace = dot < 0 ? string.Empty : name.Substring(0, dot);
            SimpleName = dot < 0 ? name : name.Substring(dot + 1);
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

        /// <summary>Aus welcher Prelude ein Enum stammt (Schlüssel wie <see cref="EnumDeclLines"/>); fehlt bei Enums des Dokuments.</summary>
        public Dictionary<string, string> EnumPreludes { get; } = new();

        /// <summary>Name der Standardbibliothek in <see cref="ClassInfo.PreludeName"/>.</summary>
        public const string StandardPreludeName = "standard";

        /// <summary>Wie die Prelude heißt, die DIESER Index beschreibt (null = ein normales Dokument).</summary>
        public string? PreludeName { get; private set; }

        /// <summary>Der Quelltext der Prelude `preludeName` (für die Anzeige beim Springen), null wenn unbekannt.</summary>
        public static string? PreludeSourceOf(string preludeName) =>
            preludeName == StandardPreludeName ? fire.Standard.Prelude.Source : ImportedPreludes.TrySourceFor(preludeName);

        /// <summary>Anzeigename einer Prelude für Fenstertitel.</summary>
        public static string PreludeTitleOf(string preludeName) =>
            preludeName == StandardPreludeName ? "Standard library (prelude)" : $"Prelude '{preludeName}'";

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

        /// <summary>Alle im Dokument deklarierten Namespaces (vollqualifiziert,
        /// samt aller Vorstufen: `A.B` legt auch `A` an).</summary>
        public HashSet<string> Namespaces { get; } = new();

        /// <summary>Die `#using`-Namen des Dokuments (siehe Preprocessor - gelten
        /// für die ganze Datei).</summary>
        public List<string> UsingNamespaces { get; } = new();

        private readonly List<(string Name, int Start, int End)> _namespaceRanges = new();
        private readonly List<(string Key, int Start, int End)> _classSpans = new();
        private readonly Stack<(string Name, int EndIdx)> _namespaceStack = new();
        private readonly List<(string Target, IReadOnlyList<string> Context, int BodyStart)> _pendingExtensions = new();

        private ScriptSymbolIndex(string source, List<Token> tokens)
        {
            _source = source;
            _tokens = tokens;
            _lineStarts = ComputeLineStarts(source);
        }

        /// <summary>The documentation comment (`///` lines) directly above the 1-based line `declLine` of THIS index's source.</summary>
        internal DocComment? DocumentationAt(int declLine) => DocComments.Find(_source, _lineStarts, declLine);

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

        private static readonly System.Text.RegularExpressions.Regex UsingLine =
            new(@"^[ \t]*#using[ \t]+([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)[ \t]*\r?$",
                System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>Die Namen aller `#using X`-Zeilen (dieselbe Namensgrammatik
        /// wie Preprocessor.UsingName).</summary>
        private static IEnumerable<string> FindUsings(string source) =>
            UsingLine.Matches(source).Select(m => m.Groups[1].Value).Distinct();

        public static ScriptSymbolIndex Build(string source) => Build(source, Array.Empty<string>());

        /// <summary>Wie Build(source); `extraImports` gelten zusätzlich zu den `#import`-Zeilen in `source` als
        /// zugeschaltet (z.B. wenn eine Prelude angezeigt wird, die selbst von einer anderen Erweiterung abhängt).</summary>
        public static ScriptSymbolIndex Build(string source, IEnumerable<string> extraImports)
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
            index.UsingNamespaces.AddRange(FindUsings(source));
            index.Harvest();
            index.HarvestIncludes(source);
            index.MergeInPrelude(PreludeIndex.Value);
            // Preludes der per '#import' zugeschalteten Erweiterungen (siehe
            // ImportedPreludes) - `Framebuffer`/`Device`/... sollen genauso
            // vervollständigt werden wie die Standardbibliothek.
            var merged = new HashSet<string>();
            foreach (var name in ImportedPreludes.FindImportNames(source).Concat(extraImports))
            {
                // `#import "ui"` bringt `graphics` mit, `linq` bringt `reflection` mit (siehe ImportedPreludes.WithDependencies).
                IEnumerable<string> keys;
                try { keys = ImportedPreludes.WithDependencies(ImportedPreludes.ParseImportName(name)).ToList(); }
                catch (Exception) { continue; } // unbekannte Erweiterung - meldet die Diagnostik
                foreach (var key in keys)
                {
                    if (!merged.Add(key)) continue;
                    var importIndex = ImportedPreludeIndex(key);
                    if (importIndex != null) index.MergeInPrelude(importIndex);
                }
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
                    existing.PreludeName = preludeClass.PreludeName;
                    existing.BaseName ??= preludeClass.BaseName;
                    existing.Members.AddRange(preludeClass.Members);
                }
                else
                {
                    Classes[name] = preludeClass;
                }
            }
            // Namespaces und Enums der Prelude (z.B. `IO` samt `IO.FileMode` bei
            // `#import "io"`) - ohne sie gäbe es kein `IO.`-Vorschlagen.
            foreach (var ns in prelude.Namespaces)
                Namespaces.Add(ns);
            foreach (var (name, members) in prelude.EnumMembers)
                if (!EnumMembers.ContainsKey(name))
                {
                    EnumMembers[name] = members;
                    if (prelude.EnumDeclLines.TryGetValue(name, out var line)) EnumDeclLines[name] = line;
                    if (prelude.PreludeName != null) EnumPreludes[name] = prelude.PreludeName;
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
            var index = Build(fire.Standard.Prelude.Source, StandardPreludeName);
            return index;
        });

        private static readonly Dictionary<string, ScriptSymbolIndex?> ImportedPreludeIndexes = new();

        /// <summary>Der Index der Prelude einer per `#import "name"`
        /// zugeschalteten Erweiterung (einmalig gebaut und gemerkt), null
        /// bei einem unbekannten Namen. Die Definitionszeilen beziehen sich auf
        /// den Prelude-Quelltext (<see cref="PreludeSourceOf"/>), den der Editor
        /// beim Springen in einem eigenen Fenster zeigt.</summary>
        private static ScriptSymbolIndex? ImportedPreludeIndex(string importName)
        {
            lock (ImportedPreludeIndexes)
            {
                if (ImportedPreludeIndexes.TryGetValue(importName, out var cached)) return cached;
                string? preludeSource = ImportedPreludes.TrySourceFor(importName);
                var index = preludeSource == null ? null : Build(preludeSource, importName.ToLowerInvariant());
                ImportedPreludeIndexes[importName] = index;
                return index;
            }
        }

        /// <summary>Interne Build-Überladung für den Prelude-Quelltext selbst
        /// (siehe PreludeIndex) - baut NUR den rohen Index (keine rekursive
        /// MergeInPrelude(), die Prelude braucht sich ja nicht selbst
        /// einzumischen) und markiert danach jede gefundene Klasse als
        /// IsFromPrelude.</summary>
        private static ScriptSymbolIndex Build(string source, string preludeName)
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

            var index = new ScriptSymbolIndex(source, tokens) { PreludeName = preludeName };
            index.Harvest();
            foreach (var classInfo in index.Classes.Values)
                classInfo.PreludeName = preludeName;
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

        /// <summary>Der Namespace, in dem der Harvest-Lauf gerade steht (leer =
        /// keiner) - verschachtelte Blöcke hängen sich mit '.' an.</summary>
        private string CurrentNamespace => _namespaceStack.Count > 0 ? _namespaceStack.Peek().Name : string.Empty;

        /// <summary>Vollqualifiziert einen im aktuellen Namespace deklarierten
        /// Namen (siehe Parser.QualifyDeclName).</summary>
        private string Qualify(string simpleName) =>
            CurrentNamespace.Length == 0 ? simpleName : CurrentNamespace + "." + simpleName;

        /// <summary>Die Namespaces für die Auflösung von Typnamen an der
        /// aktuellen Harvest-Stelle: aktueller Namespace, dann `#using`.</summary>
        private IReadOnlyList<string> CurrentContext() =>
            CurrentNamespace.Length == 0
                ? UsingNamespaces.ToList()
                : new[] { CurrentNamespace }.Concat(UsingNamespaces).ToList();

        /// <summary>`namespace A.B { ... }` bei `i` - merkt Namespace und
        /// Bereich, liefert den Index NACH der öffnenden '{' (der Inhalt wird
        /// vom Harvest-Lauf normal weitergelesen).</summary>
        private int HarvestNamespace(int i)
        {
            int j = i + 1;
            if (j >= _tokens.Count || _tokens[j].Type != TokenType.Identifier) return i + 1;
            string name = _tokens[j].Lexeme;
            j++;
            while (j + 1 < _tokens.Count && _tokens[j].Type == TokenType.Dot && _tokens[j + 1].Type == TokenType.Identifier)
            {
                name += "." + _tokens[j + 1].Lexeme;
                j += 2;
            }
            if (j >= _tokens.Count || _tokens[j].Type != TokenType.LBrace) return i + 1;

            string full = Qualify(name);
            int end = MatchBrace(j);
            _namespaceStack.Push((full, end));
            _namespaceRanges.Add((full, TokenOffset(j), TokenOffset(end) + 1));
            for (string part = full; ; )
            {
                Namespaces.Add(part);
                int dot = part.LastIndexOf('.');
                if (dot < 0) break;
                part = part.Substring(0, dot);
            }
            return j + 1;
        }

        private void Harvest()
        {
            int i = 0;
            while (i < _tokens.Count && _tokens[i].Type != TokenType.Eof)
            {
                while (_namespaceStack.Count > 0 && _namespaceStack.Peek().EndIdx < i)
                    _namespaceStack.Pop();

                if (_tokens[i].Type == TokenType.Namespace)
                {
                    i = HarvestNamespace(i);
                }
                else if (_tokens[i].Type == TokenType.Class || _tokens[i].Type == TokenType.Actor)
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

            // `class extends X { ... }` erst NACH allen Deklarationen: X kann
            // im Dokument auch NACH der Erweiterung stehen, und in welchem
            // Namespace es liegt, lässt sich erst dann auflösen.
            foreach (var (target, context, bodyStart) in _pendingExtensions)
            {
                string key = ResolveClassKey(target, context) ?? target;
                if (!Classes.TryGetValue(key, out var info))
                {
                    info = new ClassInfo(key) { Source = this };
                    Classes[key] = info;
                }
                HarvestMembersBody(bodyStart, info);
            }
            _pendingExtensions.Clear();
        }

        /// <summary>Die Zeile, die ein Symbol als Definitionsort bekommt.</summary>
        private static int DeclLineOf(Token token) => token.Line;

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

            string className = Qualify(_tokens[i].Lexeme);
            // Bei einer doppelten Deklaration (ein Fehler, den der Resolver
            // meldet) die erste weiterbenutzen - für die Vorschläge egal.
            var info = Classes.TryGetValue(className, out var existing)
                ? existing
                : new ClassInfo(className, DeclLineOf(_tokens[i])) { Context = CurrentContext(), Source = this };
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
                    if (_tokens[i].Type == TokenType.Identifier)
                    {
                        // Auch ein qualifizierter Name ('Geometry.Shape').
                        string baseName = _tokens[i].Lexeme;
                        while (i + 2 < _tokens.Count && _tokens[i + 1].Type == TokenType.Dot
                               && _tokens[i + 2].Type == TokenType.Identifier)
                        {
                            baseName += "." + _tokens[i + 2].Lexeme;
                            i += 2;
                        }
                        if (!info.BaseNames.Contains(baseName)) info.BaseNames.Add(baseName);
                    }
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
            if (i >= _tokens.Count) return i;

            // `class extends string { ... }` (SPEC 5.5.1): ein Basistyp ist ein Schlüsselwort. Seine
            // Methoden landen wie im Compiler in einer Sammelklasse (`$string`, siehe
            // BaseTypeExtensions), die die Vervollständigung für Werte dieses Typs auswertet.
            if (_tokens[i].Type is TokenType.KwString or TokenType.KwChar or TokenType.KwInt or TokenType.KwFloat or TokenType.KwBool)
            {
                string baseTypeClass = BaseTypeExtensions.ClassName(_tokens[i].Lexeme);
                i++;
                if (i >= _tokens.Count || _tokens[i].Type != TokenType.LBrace) return i;
                _pendingExtensions.Add((baseTypeClass, CurrentContext(), i));
                return MatchBrace(i) + 1;
            }

            if (_tokens[i].Type != TokenType.Identifier) return i;
            string targetName = _tokens[i].Lexeme;
            i++;
            while (i + 1 < _tokens.Count && _tokens[i].Type == TokenType.Dot && _tokens[i + 1].Type == TokenType.Identifier)
            {
                targetName += "." + _tokens[i + 1].Lexeme;
                i += 2;
            }

            if (i >= _tokens.Count || _tokens[i].Type != TokenType.LBrace) return i;

            // Die Mitglieder erst am Ende des Harvest-Laufs eintragen (siehe
            // dort) - hier nur den Body überspringen.
            _pendingExtensions.Add((targetName, CurrentContext(), i));
            return MatchBrace(i) + 1;
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
            _classSpans.Add((info.Name, TokenOffset(bodyStart), TokenOffset(bodyEnd) + 1));
            int i = bodyStart + 1; // hinter die öffnende '{'
            string className = info.SimpleName;

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

        /// <summary>Überspringt leere Klammerpaare `[]` ab `i` (Array-Typ, `int[] Name()`) und meldet über
        /// `found`, ob es welche gab.</summary>
        private int SkipEmptyBrackets(int i, int end, ref bool found)
        {
            while (i + 1 < end && _tokens[i].Type == TokenType.LBracket && _tokens[i + 1].Type == TokenType.RBracket)
            {
                found = true;
                i += 2;
            }
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
            // `int[] Name()` - leere Klammern hinter dem Typ = Array-RÜCKGABETYP (Methode/Property).
            string? typeName = null;
            bool returnsArray = false;
            if (IsTypeKeyword(_tokens[i].Type))
            {
                typeName = _tokens[i].Lexeme;
                i = SkipTypeSuffix(i + 1, bodyEnd);
                i = SkipEmptyBrackets(i, bodyEnd, ref returnsArray);
            }
            else if (_tokens[i].Type == TokenType.Identifier)
            {
                // Klassenname, auch qualifiziert ('Geometry.Circle'), gefolgt
                // vom Namen auf derselben Zeile.
                int typeEnd = i;
                while (typeEnd + 2 < bodyEnd && _tokens[typeEnd + 1].Type == TokenType.Dot
                       && _tokens[typeEnd + 2].Type == TokenType.Identifier)
                    typeEnd += 2;
                bool identifierArray = false;
                int afterType = SkipEmptyBrackets(typeEnd + 1, bodyEnd, ref identifierArray);
                if (afterType < bodyEnd && _tokens[afterType].Type == TokenType.Identifier
                    && !_tokens[afterType].NewlineBefore)
                {
                    typeName = string.Concat(_tokens.Skip(i).Take(typeEnd - i + 1).Select(t => t.Lexeme));
                    returnsArray = identifierArray;
                    i = afterType;
                }
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
                    TypeIsArray = returnsArray,
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
                    TypeIsArray = returnsArray,
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
            string enumName = Qualify(_tokens[i].Lexeme);
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
                foreach (var baseKey in ResolvedBases(info))
                    if (visited.Add(baseKey))
                        queue.Enqueue((baseKey, depth + 1));
            }
        }

        /// <summary>Die Basisklassen/Interfaces von `info` als Schlüssel in
        /// <see cref="Classes"/> (die Namen im Klassenkopf sind relativ zum
        /// Kontext der Klasse geschrieben, siehe ClassInfo.Context) - nicht
        /// auflösbare bleiben weg.</summary>
        public IEnumerable<string> ResolvedBases(ClassInfo info)
        {
            foreach (var baseName in info.BaseNames)
            {
                string? key = ResolveClassKey(baseName, info.Context);
                if (key != null) yield return key;
            }
        }

        // -----------------------------------------------------------
        // Namen auflösen: Klassen, Enums, Namespaces
        // -----------------------------------------------------------

        /// <summary>Löst `name` (so geschrieben, evtl. qualifiziert) gegen
        /// `context` (siehe ClassInfo.Context) zu einem Schlüssel in
        /// <see cref="Classes"/> auf - wie TypeRef.ResolveBaseName: erst der
        /// exakte Name, dann jeder Namespace des Kontexts davor. Als letzter
        /// Ausweg (z.B. wegen eines `#using` in einer anderen Datei) ein
        /// EINDEUTIGER Treffer über den einfachen Namen. `null`, wenn nichts
        /// passt.</summary>
        public string? ResolveClassKey(string name, IReadOnlyList<string> context, bool lenient = true)
        {
            if (Classes.ContainsKey(name)) return name;
            foreach (var ns in context)
                if (Classes.ContainsKey(ns + "." + name)) return ns + "." + name;

            if (!lenient || name.Contains('.')) return null;
            string? only = null;
            foreach (var cls in Classes.Values)
            {
                if (cls.SimpleName != name) continue;
                if (only != null) return null; // mehrdeutig
                only = cls.Name;
            }
            return only;
        }

        /// <summary>Wie <see cref="ResolveClassKey"/>, für Enums (Schlüssel in
        /// <see cref="EnumMembers"/>), ohne den Eindeutigkeits-Ausweg.</summary>
        public string? ResolveEnumKey(string name, IReadOnlyList<string> context)
        {
            if (EnumMembers.ContainsKey(name)) return name;
            foreach (var ns in context)
                if (EnumMembers.ContainsKey(ns + "." + name)) return ns + "." + name;
            return null;
        }

        /// <summary>Für Klick-Navigation: `name` (Klassenname wie geschrieben)
        /// an Dokument-Position `offset` (-1: kein Kontext, z.B. eine ANDERE
        /// Datei) zum Schlüssel in <see cref="Classes"/>.</summary>
        public string? TryFindClass(string name, int offset) =>
            ResolveClassKey(name, offset < 0 ? UsingNamespaces : ContextAt(offset));

        /// <summary>Wie <see cref="TryFindClass"/>, für Enums.</summary>
        public string? TryFindEnum(string name, int offset)
        {
            var context = offset < 0 ? UsingNamespaces : ContextAt(offset);
            return ResolveEnumKey(name, context)
                ?? EnumMembers.Keys.FirstOrDefault(k => k.EndsWith("." + name, StringComparison.Ordinal));
        }

        /// <summary>Der innerste Namespace-Block, der `offset` enthält (null
        /// außerhalb jedes Blocks).</summary>
        public string? NamespaceAt(int offset)
        {
            string? best = null;
            int bestStart = -1;
            foreach (var (name, start, end) in _namespaceRanges)
                if (offset >= start && offset <= end && start > bestStart)
                {
                    best = name;
                    bestStart = start;
                }
            return best;
        }

        /// <summary>Die Namespaces, gegen die Typnamen an `offset` aufgelöst
        /// werden: der umschließende Namespace zuerst, dann die `#using`-Namen
        /// des Dokuments (siehe TypeRef.ResolveBaseName - die Namespaces
        /// ÜBER dem aktuellen zählen NICHT mit).</summary>
        public IReadOnlyList<string> ContextAt(int offset)
        {
            string? ns = NamespaceAt(offset);
            return ns == null ? UsingNamespaces : new[] { ns }.Concat(UsingNamespaces).ToList();
        }

        /// <summary>Was direkt IN einem Namespace steht (`ns` leer = ganz oben):
        /// untergeordnete Namespaces, Klassen, Interfaces und Enums, jeweils mit
        /// dem einfachen Namen - Grundlage für `Namespace.`-Vorschläge.</summary>
        public List<NamespaceMember> MembersOfNamespace(string ns)
        {
            var result = new List<NamespaceMember>();
            string prefix = ns.Length == 0 ? string.Empty : ns + ".";

            foreach (var name in Namespaces)
                if (name.StartsWith(prefix, StringComparison.Ordinal) && !name.Substring(prefix.Length).Contains('.')
                    && name.Length > prefix.Length)
                    result.Add(new NamespaceMember(name.Substring(prefix.Length), NamespaceMemberKind.Namespace, name));

            foreach (var cls in Classes.Values)
                if (cls.Namespace == ns)
                    result.Add(new NamespaceMember(cls.SimpleName,
                        cls.IsInterface ? NamespaceMemberKind.Interface : NamespaceMemberKind.Class, cls.Name));

            foreach (var key in EnumMembers.Keys)
            {
                int dot = key.LastIndexOf('.');
                string enumNs = dot < 0 ? string.Empty : key.Substring(0, dot);
                if (enumNs == ns)
                    result.Add(new NamespaceMember(dot < 0 ? key : key.Substring(dot + 1), NamespaceMemberKind.Enum, key));
            }
            return result;
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
                foreach (var baseKey in ResolvedBases(info))
                {
                    if (baseKey == ancestor) return true;
                    if (visited.Add(baseKey)) queue.Enqueue(baseKey);
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
            // Aus den beim Harvest gemerkten Klassen-Bodys: 'class Name {...}'
            // liefert die Klasse selbst, 'class extends X {...}' ihre ZIEL-Klasse
            // X (siehe Parser.ParseClassExtensionDecl) - jeweils als
            // vollqualifizierter Schlüssel in Classes.
            string? best = null;
            int bestStart = -1;
            foreach (var (key, start, end) in _classSpans)
                if (offset >= start && offset <= end && start > bestStart)
                {
                    best = key;
                    bestStart = start;
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
                // Ein Standardwert ('= ...') gehört nicht mehr zu "[Typ] Name".
                int assign = toks.FindIndex(t => t.Type == TokenType.Assign);
                if (assign >= 0) toks = toks.GetRange(0, assign);

                int nameIdx = toks.FindLastIndex(t => t.Type == TokenType.Identifier);
                if (nameIdx < 0) return;
                string name = toks[nameIdx].Lexeme;

                // Typ davor: Bitbreite/Sterne ('int[16]', 'int*') überspringen, dann
                // Typ-Keyword oder (evtl. qualifizierter) Klassenname.
                int t2 = nameIdx - 1;
                while (t2 >= 0 && toks[t2].Type == TokenType.Star) t2--;
                if (t2 >= 0 && toks[t2].Type == TokenType.RBracket)
                {
                    while (t2 >= 0 && toks[t2].Type != TokenType.LBracket) t2--;
                    t2--;
                }
                string? type = null;
                if (t2 >= 0 && (toks[t2].Type == TokenType.Identifier || IsTypeKeyword(toks[t2].Type)))
                {
                    type = toks[t2].Lexeme;
                    while (t2 >= 2 && toks[t2 - 1].Type == TokenType.Dot && toks[t2 - 2].Type == TokenType.Identifier)
                    {
                        type = toks[t2 - 2].Lexeme + "." + type;
                        t2 -= 2;
                    }
                }
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
