using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using fire.Lexing;
using fire.Values;

namespace fire.Compiler
{
    /// <summary>Kontext, den eine Präprozessor-Direktiven-Implementierung
    /// bei ihrem Aufruf bekommt (siehe DirectiveHandler/DirectiveRegistry) -
    /// erlaubt ihr, rekursiv weitere Dateien einzuschleusen (ProcessFile)
    /// UND auf die GLOBAL geteilte "bereits eingefügt"-Menge zuzugreifen.</summary>
    /// <summary>Ergebnis von Preprocessor.Process: der fertig vorverarbeitete
    /// Quelltext (alle `#include`s eingesetzt, alle erkannten Direktiven-
    /// Zeilen entfernt) UND die in ihm per `#using Name` gesammelten
    /// Namespace-Namen (SPEC "Namespaces") - ab jetzt eine reine
    /// Preprocessor-Angelegenheit, nicht mehr Aufgabe des Parsers (siehe
    /// Parser._usingNamespaces/ParseMultiple): `#using`-Zeilen werden schon
    /// HIER erkannt und aus dem Text entfernt (wie jede andere erkannte
    /// Direktive), der Lexer/Parser sieht sie nie. Eine `#using`-Zeile
    /// INNERHALB einer per `#include` eingefügten Datei landet in
    /// DENSELBEN `Usings` wie die einschließende Datei - konsistent mit der
    /// "reines Text-Splicing"-Semantik von `#include` (SPEC 8.1.5): nach
    /// dem Einsetzen ist nicht mehr unterscheidbar, ob eine Zeile ursprünglich
    /// aus der Wurzel-Datei oder einer eingefügten Datei stammt.</summary>
    public sealed record ProcessedSource(string Source, IReadOnlyList<string> Usings);

    /// <summary>The files embedded in a program while it is preprocessed: the same file is stored once (the id of a resource is its index).</summary>
    public sealed class ResourceTable
    {
        private readonly Dictionary<string, int> _byFullPath = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        public List<fire.Runtime.ResourceEntry> Entries { get; } = new();

        /// <summary>Reads the file and returns its id; `name` is the path as it was written in the source.</summary>
        public int Add(string name, string fullPath)
        {
            if (_byFullPath.TryGetValue(fullPath, out int id)) return id;
            id = Entries.Count;
            Entries.Add(new fire.Runtime.ResourceEntry { Name = name, Data = File.ReadAllBytes(fullPath) });
            _byFullPath[fullPath] = id;
            return id;
        }
    }

    public sealed class DirectiveContext
    {
        /// <summary>Verzeichnis, relativ zu dem Pfad-Argumente DIESER
        /// Direktiven-Zeile aufzulösen sind (das Verzeichnis der gerade
        /// verarbeiteten Datei).</summary>
        public string BasePath { get; }

        /// <summary>GLOBAL geteilte Menge bereits eingefügter (absoluter)
        /// Dateipfade - bewusst NICHT pro Preprocessor.Process()-Aufruf neu
        /// angelegt, sondern vom AUFRUFER bereitgestellt und damit
        /// zwischen mehreren Process()-Aufrufen TEILBAR: verarbeitet ein
        /// Host mehrere Wurzel-Dateien zusammen (z.B. Prelude + Nutzer-
        /// Skript, siehe Runtime.RuntimeSession.Build/Parser.ParseMultiple),
        /// sorgt eine GEMEINSAME
        /// Instanz dafür, dass eine von BEIDEN Seiten (transitiv)
        /// includierte Datei insgesamt nur EIN einziges Mal in der
        /// kombinierten Ausgabe landet - eine globale Code-Komposition,
        /// statt separater, pro Wurzel-Datei eigenständiger Einfüge-Bäume
        /// mit jeweils eigenem "schon gesehen"-Gedächtnis.</summary>
        public HashSet<string> AlreadyIncluded { get; }

        private readonly Preprocessor _owner;

        internal DirectiveContext(string basePath, HashSet<string> alreadyIncluded, Preprocessor owner)
        {
            BasePath = basePath;
            AlreadyIncluded = alreadyIncluded;
            _owner = owner;
        }

        /// <summary>Liest `fullPath` und verarbeitet dessen Inhalt REKURSIV
        /// (inkl. darin evtl. enthaltener eigener Direktiven, mit demselben
        /// Verzeichnis von `fullPath` als neuem BasePath für relative
        /// Pfade darin) - für Direktiven-Implementierungen wie "include",
        /// die fremden Dateiinhalt einschleusen wollen. Nutzt dieselbe
        /// `AlreadyIncluded`-Menge und Direktiven-Registry wie der äußere
        /// Aufruf; erkennt zirkuläre Einschleusungen (A schleust B ein,
        /// B schleust wieder A ein) und bricht dafür mit einem klaren
        /// Fehler ab, statt endlos zu rekursieren.</summary>
        public string ProcessFile(string fullPath) => _owner.ProcessFileRecursive(fullPath, AlreadyIncluded);
    }

    /// <summary>Eine Präprozessor-Direktiven-Implementierung: bekommt den
    /// Aufruf-Kontext, die bereits auf ihre deklarierte Anzahl geprüften
    /// Argumentwerte (siehe DirectiveRegistry.Register) und die Zeilennummer
    /// der Direktiven-Zeile. Liefert den Text, der die Direktiven-Zeile in
    /// der vorverarbeiteten Ausgabe ersetzt (leer/null für "nichts
    /// einfügen").</summary>
    public delegate string? DirectiveHandler(DirectiveContext ctx, IReadOnlyList<Value> args, int line);

    public sealed class DirectiveDefinition
    {
        public string Name { get; }
        public int ParamCount { get; }
        public DirectiveHandler Handler { get; }

        public DirectiveDefinition(string name, int paramCount, DirectiveHandler handler)
        {
            Name = name;
            ParamCount = paramCount;
            Handler = handler;
        }
    }

    /// <summary>
    /// Registry frei definierbarer Präprozessor-Direktiven (SPEC 8.1.5):
    /// Host-C#-Code registriert per <see cref="Register"/> eine neue
    /// `#name wert1, wert2, ...`-Direktive mit einer FEST deklarierten
    /// erwarteten Parameteranzahl (Fehler bei Abweichung, siehe Preprocessor.
    /// ParseDirectiveArgs) und einem Handler, der die geparsten Werte
    /// bekommt. Jeder Parameter ist ein reines LITERAL (String/Zahl mit
    /// optionalem Einheiten-Suffix/Zeichen/bool/undefined) - Präprozessor-
    /// Direktiven laufen VOR dem Lexer/Parser, zu diesem Zeitpunkt gibt es
    /// noch keine Variablen/Ausdrücke, deshalb bewusst keine volle
    /// Ausdrucks-Grammatik.
    ///
    /// `#include` ist ab jetzt NUR NOCH die eingebaute Default-Registrierung
    /// dieses Mechanismus (siehe <see cref="CreateDefault"/>), keine
    /// Sonderbehandlung mehr im restlichen Preprocessor-Code - jede weitere,
    /// selbst registrierte Direktive funktioniert nach demselben Muster.
    /// `#extern "libName"`/`#noshadow` bleiben dagegen bewusst AUSSERHALB
    /// dieser Registry (siehe Preprocessor-Klassendoku: unbekannte `#...`-
    /// Zeilen werden unverändert durchgereicht) - sie haben eine STICKY,
    /// über mehrere nachfolgende Statements hinweg wirkende Bedeutung für
    /// den PARSER selbst (welche Bibliothek nachfolgende `extern`-
    /// Deklarationen verlinken, ob Globals-Shadowing gilt), keine reine
    /// "ersetze diese eine Zeile durch Text"-Semantik wie `#include` - ein
    /// Umbau dorthin wäre möglich, aber ein andersartiger Eingriff (der
    /// Parser müsste dann Zustand aus dem Preprocessor abfragen, statt wie
    /// bisher beides selbst zu verwalten) und deshalb bewusst nicht Teil
    /// dieser Änderung.
    /// </summary>
    public sealed class DirectiveRegistry
    {
        private readonly Dictionary<string, DirectiveDefinition?> _directives =
            new(StringComparer.OrdinalIgnoreCase);

        public void Annouce(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("The directive name must not be empty.", nameof(name));
            _directives[name] = null;
        }

        /// <summary>The symbols of `#if` (see <see cref="ConditionalSymbols"/>), case-insensitive. Shared by everything processed with this registry, so a
        /// `#define` of one file is seen by the next. Empty at first; the linker and the runtime session fill it from the target.</summary>
        public HashSet<string> Symbols { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The files of `new Resource("path")` (docs/RESOURCES.md): where the preprocessor puts them. Without a table (live diagnostics) the text stays as it is.</summary>
        public ResourceTable? Resources { get; set; }

        public DirectiveRegistry()
        {
            Annouce("import");
            Annouce("include");
            Annouce("name");
            Annouce("codename");
            Annouce("author");
            Annouce("comments");
            Annouce("description");
            Annouce("icon");
            Annouce("debug");
            Annouce("performance");
            Annouce("floatwidth");
            Annouce("noconsole");
            Annouce("version");
            Annouce("fileversion");
        }

        /// <summary>Registriert (oder ersetzt) die Direktive `name` - ein
        /// Aufruf `#name ...` mit einer ANDEREN Anzahl Argumente als
        /// `paramCount` ist danach ein klarer Compile-Fehler (siehe
        /// Preprocessor.ParseDirectiveArgs).</summary>
        public void Register(string name, int paramCount, DirectiveHandler handler)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("The directive name must not be empty.", nameof(name));
            if (paramCount < 0)
                throw new ArgumentOutOfRangeException(nameof(paramCount));
            _directives[name] = new DirectiveDefinition(name, paramCount, handler ?? throw new ArgumentNullException(nameof(handler)));
        }

        public bool TryGet(string name, out DirectiveDefinition definition) =>
            _directives.TryGetValue(name, out definition!);

        /// <summary>`#include "pfad"` als einzige eingebaute Direktive (1
        /// Parameter: der Pfad als String) - "Include once" GLOBAL über die
        /// gesamte Komposition (siehe DirectiveContext.AlreadyIncluded-Doku),
        /// nicht wie C's rohes mehrfaches Einfügen. Ein Host, der gar kein
        /// `#include` will/braucht, kann stattdessen eine leere
        /// `new DirectiveRegistry()` verwenden.</summary>
        public static DirectiveRegistry CreateDefault()
        {
            var registry = new DirectiveRegistry();
            registry.Register("include", 1, (ctx, args, line) =>
            {
                if (args[0].Kind != ValueKind.String)
                    throw new PreprocessorException(
                        $"'#include' expects a string as the path, not {args[0].Kind} (line {line}).");

                string relativePath = args[0].AsString();
                string fullPath = Path.GetFullPath(Path.Combine(ctx.BasePath, relativePath));

                if (!ctx.AlreadyIncluded.Add(fullPath))
                    return ""; // schon (irgendwo in der GESAMTEN Komposition) eingefügt - überspringen

                if (!File.Exists(fullPath))
                    throw new PreprocessorException(
                        $"'#include \"{relativePath}\"' - file not found: '{fullPath}' (line {line}).");

                return ctx.ProcessFile(fullPath);
            });
            return registry;
        }
    }

    /// <summary>
    /// Reine Textvorverarbeitung VOR dem Lexer: Zeilen der Form
    /// `#name wert1, wert2, ...` werden über eine <see cref="DirectiveRegistry"/>
    /// verarbeitet (Default: nur `#include`, siehe DirectiveRegistry.
    /// CreateDefault) - bewusst simpel gehalten (kein eigener Lexer-/Parser-
    /// Durchlauf für den REST der Datei nötig, nur für die Argumentliste
    /// EINER Direktiven-Zeile). Eine `#...`-Zeile, deren Name NICHT in der
    /// Registry steht (z.B. `#extern "libName"`, `#noshadow` - siehe
    /// DirectiveRegistry-Klassendoku), wird UNVERÄNDERT durchgereicht - der
    /// Preprocessor mischt sich nur in Direktiven ein, die er tatsächlich
    /// kennt.
    ///
    /// Zeilennummern in Fehlermeldungen werden für eingefügten Text
    /// ungenau (wie bei jedem einfachen Text-Präprozessor, inkl. C ohne
    /// `#line`) - eine bekannte, akzeptierte Grenze dieser einfachen
    /// Umsetzung.
    /// </summary>
    public sealed class Preprocessor
    {
        private static readonly Regex DirectiveLine =
            new(@"^\s*#([A-Za-z_][A-Za-z0-9_]*)(?:[ \t]+(.*))?\s*$", RegexOptions.Compiled);

        internal static Regex DirectiveLineRegex => DirectiveLine;

        /// <summary>Ein gültiger (evtl. punktierter) Namespace-Name nach
        /// `#using` - dieselbe Namensgrammatik wie Parser.ParseDottedName
        /// (`A` oder `A.B.C`), hier aber als reine Text-Prüfung statt über
        /// den echten Lexer/Parser (siehe Klassendoku: bewusst simpel).</summary>
        private static readonly Regex UsingName =
            new(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$", RegexOptions.Compiled);

        private readonly DirectiveRegistry _registry;
        private readonly List<string> _includeChain = new();

        /// <summary>Alle per `#using Name` gesammelten Namespace-Namen - EINE
        /// gemeinsame Liste für den GESAMTEN Process()-Aufruf inkl. aller
        /// rekursiv per `#include` eingefügten Dateien (siehe ProcessedSource-
        /// Doku für die Begründung), deshalb ein Instanzfeld statt eines
        /// Rückgabewerts von ProcessInner (das rekursiv für `#include` läuft).</summary>
        private readonly List<string> _usings = new();

        private Preprocessor(DirectiveRegistry registry)
        {
            _registry = registry;
        }

        /// <summary>Verarbeitet EINEN Quelltext für sich, mit einer
        /// FRISCHEN "bereits eingefügt"-Menge - für den einfachen Fall,
        /// dass nur EINE Wurzel-Datei kompiliert wird. `registry`: Default
        /// `DirectiveRegistry.CreateDefault()` (nur `#include`).</summary>
        public static ProcessedSource Process(string source, string basePath, DirectiveRegistry? registry = null) =>
            Process(source, basePath, new HashSet<string>(StringComparer.OrdinalIgnoreCase), registry);

        /// <summary>Wie Process(source, basePath), aber mit einer VOM
        /// AUFRUFER bereitgestellten (und damit zwischen mehreren Process()-
        /// Aufrufen TEILBAREN) "bereits eingefügt"-Menge - siehe
        /// DirectiveContext.AlreadyIncluded-Doku für den Grund (globale
        /// statt pro-Wurzeldatei-Komposition, z.B. Prelude + mehrere
        /// Nutzer-Dateien zusammen, siehe Parser.ParseMultiple).</summary>
        public static ProcessedSource Process(
            string source, string basePath, HashSet<string> alreadyIncluded, DirectiveRegistry? registry = null)
        {
            var pre = new Preprocessor(registry ?? DirectiveRegistry.CreateDefault());
            string result = pre.ProcessInner(source, basePath, alreadyIncluded);
            return new ProcessedSource(result, pre._usings);
        }

        internal string ProcessFileRecursive(string fullPath, HashSet<string> alreadyIncluded)
        {
            if (_includeChain.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
                throw new PreprocessorException(
                    $"Circular include of '{fullPath}' (chain: {string.Join(" -> ", _includeChain)} -> {fullPath}).");

            string includedSource;
            try
            {
                includedSource = File.ReadAllText(fullPath);
            }
            catch (Exception ex)
            {
                throw new PreprocessorException($"'{fullPath}' could not be read: {ex.Message}", ex);
            }

            // a markup file of a user interface (docs/UI_MARKUP.md) is included as the script generated from it
            if (string.Equals(Path.GetExtension(fullPath), ".fxml", StringComparison.OrdinalIgnoreCase))
            {
                try { includedSource = fire.UI.Markup.FireUiGenerator.Generate(fire.UI.Markup.MarkupParser.Parse(includedSource), fullPath); }
                catch (fire.UI.Markup.MarkupException ex) { throw new PreprocessorException($"'{Path.GetFileName(fullPath)}': {ex.Message}", ex); }
            }

            _includeChain.Add(fullPath);
            string? includedDir = Path.GetDirectoryName(fullPath);
            string result = ProcessInner(includedSource, includedDir ?? ".", alreadyIncluded);
            _includeChain.RemoveAt(_includeChain.Count - 1);
            return result;
        }

        private string ProcessInner(string source, string basePath, HashSet<string> alreadyIncluded)
        {
            var sb = new StringBuilder();
            var lines = source.Split('\n');
            var conditional = new ConditionalState(); // `#if` nesting of this file; the symbols are those of the registry (shared by all files)

            for (int lineNo = 0; lineNo < lines.Length; lineNo++)
            {
                var line = lines[lineNo].TrimEnd('\r');
                var match = DirectiveLine.Match(line);
                if (!match.Success)
                {
                    // a branch that is not taken: an empty line, the numbering stays
                    sb.Append(conditional.Active ? ResolveResources(line, basePath, lineNo + 1) : "").Append('\n');
                    continue;
                }

                string name = match.Groups[1].Value;

                if (conditional.TryHandle(name, match.Groups[2].Success ? match.Groups[2].Value : "", lineNo + 1, _registry.Symbols))
                {
                    sb.Append('\n');
                    continue;
                }
                if (!conditional.Active)
                {
                    sb.Append('\n');
                    continue;
                }

                if (string.Equals(name, "using", StringComparison.OrdinalIgnoreCase))
                {
                    string usingArg = (match.Groups[2].Success ? match.Groups[2].Value : "").Trim();
                    if (!UsingName.IsMatch(usingArg))
                        throw new PreprocessorException(
                            $"'#using' expects a (possibly dotted) namespace name, not '{usingArg}' (line {lineNo + 1}).");
                    _usings.Add(usingArg);
                    sb.Append('\n'); // Zeile "verschwindet" wie jede andere erkannte Direktive.
                    continue;
                }

                if (!_registry.TryGet(name, out var def))
                {
                    // Nicht bei DIESEM Preprocessor registriert - z.B.
                    // '#extern "lib"' oder '#noshadow', die der PARSER
                    // selbst behandelt (siehe DirectiveRegistry-Klassendoku) -
                    // unverändert durchreichen, KEIN Fehler.
                    sb.Append(line).Append('\n');
                    continue;
                }

                if (def == null)
                {
                    sb.Append('\n'); // Zeile "verschwindet", Zeilenzahl bleibt trotzdem erhalten (siehe Klassendoku).
                }
                else
                {
                    string argText = match.Groups[2].Success ? match.Groups[2].Value : "";
                    var args = ParseDirectiveArgs(argText, def, lineNo + 1);

                    var ctx = new DirectiveContext(basePath, alreadyIncluded, this);
                    string? replacement = def.Handler(ctx, args, lineNo + 1);

                    if (!string.IsNullOrEmpty(replacement))
                    {
                        sb.Append(replacement);
                        if (!replacement.EndsWith("\n")) sb.Append('\n');
                    }
                    else
                    {
                        sb.Append('\n'); // Zeile "verschwindet", Zeilenzahl bleibt trotzdem erhalten (siehe Klassendoku).
                    }
                }
            }

            conditional.EnsureClosed();
            return sb.ToString();
        }

        private static readonly Regex ResourceCall = new(@"\bnew\s+Resource\s*\(\s*""((?:[^""\\]|\\.)*)""\s*\)", RegexOptions.Compiled);

        /// <summary>`new Resource("path")` in a line of code: the file (relative to the source file that mentions it) is embedded in the program and the path becomes the number of the
        /// resource - `new Resource(3)`. What stands in a comment (`//`) or a string stays as it is.</summary>
        private string ResolveResources(string line, string basePath, int lineNo)
        {
            if (_registry.Resources is not { } table || line.IndexOf("Resource", StringComparison.Ordinal) < 0) return line;
            return ResourceCall.Replace(line, m =>
            {
                if (!IsCode(line, m.Index)) return m.Value;
                string path = Regex.Unescape(m.Groups[1].Value);
                string full = Path.GetFullPath(path, basePath);
                if (!File.Exists(full))
                    throw new PreprocessorException($"Resource file not found: '{path}' ('{full}', line {lineNo}).");
                return $"new Resource({table.Add(path, full)})";
            });
        }

        /// <summary>Is the position in the line code - not in a string and not behind a `//`?</summary>
        private static bool IsCode(string line, int index)
        {
            bool inString = false;
            for (int i = 0; i < index; i++)
            {
                char c = line[i];
                if (inString)
                {
                    if (c == '\\') i++;
                    else if (c == '"') inString = false;
                }
                else if (c == '"') inString = true;
                else if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') return false;
            }
            return !inString;
        }

        /// <summary>Liest die Argumentliste EINER Direktiven-Zeile - über
        /// den ECHTEN Lexer tokenisiert (korrekt für Strings/Zahlen mit
        /// Einheiten-Suffix/etc.), dann als kommagetrennte Liste reiner
        /// Literal-Werte gelesen (siehe ReadDirectiveLiteral) - KEINE
        /// allgemeine Ausdrucks-Grammatik (siehe Klassendoku). Prüft danach
        /// die tatsächliche gegen die deklarierte Parameteranzahl.</summary>
        private static IReadOnlyList<Value> ParseDirectiveArgs(string argText, DirectiveDefinition def, int line)
        {
            if (def.ParamCount == 0)
            {
                if (!string.IsNullOrWhiteSpace(argText))
                    throw new PreprocessorException($"'#{def.Name}' expects no parameters (line {line}).");
                return Array.Empty<Value>();
            }

            List<Token> tokens;
            try
            {
                tokens = new Lexer(argText).Tokenize();
            }
            catch (LexException ex)
            {
                throw new PreprocessorException($"Invalid parameters for '#{def.Name}' (line {line}): {ex.Message}");
            }

            var values = new List<Value>();
            int i = 0;
            while (i < tokens.Count && tokens[i].Type != TokenType.Eof)
            {
                values.Add(ReadDirectiveLiteral(tokens, ref i, def.Name, line));
                if (i < tokens.Count && tokens[i].Type == TokenType.Comma)
                {
                    i++;
                    continue;
                }
                break;
            }

            if (i < tokens.Count && tokens[i].Type != TokenType.Eof)
                throw new PreprocessorException(
                    $"Unexpected token '{tokens[i].Lexeme}' in the parameters of '#{def.Name}' (line {line}).");

            if (values.Count != def.ParamCount)
                throw new PreprocessorException(
                    $"'#{def.Name}' expects {def.ParamCount} parameters, got {values.Count} (line {line}).");

            return values;
        }

        /// <summary>Liest EIN Literal (String/Int/Float/Char/bool/undefined,
        /// Zahlen mit optionalem führendem '-' und optionalem Einheiten-
        /// Suffix wie '74mm') ab Token-Index `i`, rückt `i` dabei weiter.</summary>
        private static Value ReadDirectiveLiteral(List<Token> tokens, ref int i, string directiveName, int line)
        {
            var tok = tokens[i];
            switch (tok.Type)
            {
                case TokenType.StringLiteral:
                    i++;
                    return Value.MakeString((string)tok.LiteralValue!);

                case TokenType.IntLiteral:
                    i++;
                    return Value.MakeInt((long)tok.LiteralValue!, Unit.Parse(tok.UnitSuffix ?? ""));

                case TokenType.FloatLiteral:
                    i++;
                    return Value.MakeFloat((double)tok.LiteralValue!, Unit.Parse(tok.UnitSuffix ?? ""));

                case TokenType.CharLiteral:
                    i++;
                    return Value.MakeChar((char)tok.LiteralValue!);

                case TokenType.True:
                    i++;
                    return Value.MakeBool(true);

                case TokenType.False:
                    i++;
                    return Value.MakeBool(false);

                case TokenType.Undefined:
                    i++;
                    return Value.MakeUndefined();

                case TokenType.Minus:
                {
                    // Vorzeichen direkt vor einer Zahl ('-5', '-3.2mm') -
                    // Direktiven-Argumente sind reine Literale, kein
                    // allgemeiner Ausdruck (siehe Klassendoku), deshalb hier
                    // als expliziter Sonderfall statt über die volle
                    // Ausdrucks-Präzedenzkette des normalen Parsers.
                    i++;
                    if (i >= tokens.Count || (tokens[i].Type != TokenType.IntLiteral && tokens[i].Type != TokenType.FloatLiteral))
                        throw new PreprocessorException(
                            $"Expected a number after '-' in the parameters of '#{directiveName}' (line {line}).");
                    var numTok = tokens[i];
                    i++;
                    return numTok.Type == TokenType.IntLiteral
                        ? Value.MakeInt(-(long)numTok.LiteralValue!, Unit.Parse(numTok.UnitSuffix ?? ""))
                        : Value.MakeFloat(-(double)numTok.LiteralValue!, Unit.Parse(numTok.UnitSuffix ?? ""));
                }

                default:
                    throw new PreprocessorException(
                        $"Expected a literal value (string/number/char/bool/undefined) in the parameters of " +
                        $"'#{directiveName}', not '{tok.Lexeme}' (line {line}).");
            }
        }
    }

    public sealed class PreprocessorException : Exception
    {
        public PreprocessorException(string message) : base(message) { }
        public PreprocessorException(string message, Exception inner) : base(message, inner) { }
    }
}
