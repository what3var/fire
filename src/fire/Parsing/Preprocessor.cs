using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using ScriptLang.Lexing;
using ScriptLang.Values;

namespace ScriptLang.Parsing
{
    /// <summary>Kontext, den eine Präprozessor-Direktiven-Implementierung
    /// bei ihrem Aufruf bekommt (siehe DirectiveHandler/DirectiveRegistry) -
    /// erlaubt ihr, rekursiv weitere Dateien einzuschleusen (ProcessFile)
    /// UND auf die GLOBAL geteilte "bereits eingefügt"-Menge zuzugreifen.</summary>
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
        /// Skript, siehe Parser.ParseWithPrelude), sorgt eine GEMEINSAME
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
        private readonly Dictionary<string, DirectiveDefinition> _directives =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Registriert (oder ersetzt) die Direktive `name` - ein
        /// Aufruf `#name ...` mit einer ANDEREN Anzahl Argumente als
        /// `paramCount` ist danach ein klarer Compile-Fehler (siehe
        /// Preprocessor.ParseDirectiveArgs).</summary>
        public void Register(string name, int paramCount, DirectiveHandler handler)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Direktivenname darf nicht leer sein.", nameof(name));
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
                        $"'#include' erwartet einen String als Pfad, nicht {args[0].Kind} (Zeile {line}).");

                string relativePath = args[0].AsString();
                string fullPath = Path.GetFullPath(Path.Combine(ctx.BasePath, relativePath));

                if (!ctx.AlreadyIncluded.Add(fullPath))
                    return ""; // schon (irgendwo in der GESAMTEN Komposition) eingefügt - überspringen

                if (!File.Exists(fullPath))
                    throw new PreprocessorException(
                        $"'#include \"{relativePath}\"' - Datei nicht gefunden: '{fullPath}' (Zeile {line}).");

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

        private readonly DirectiveRegistry _registry;
        private readonly List<string> _includeChain = new();

        private Preprocessor(DirectiveRegistry registry)
        {
            _registry = registry;
        }

        /// <summary>Verarbeitet EINEN Quelltext für sich, mit einer
        /// FRISCHEN "bereits eingefügt"-Menge - für den einfachen Fall,
        /// dass nur EINE Wurzel-Datei kompiliert wird. `registry`: Default
        /// `DirectiveRegistry.CreateDefault()` (nur `#include`).</summary>
        public static string Process(string source, string basePath, DirectiveRegistry? registry = null) =>
            Process(source, basePath, new HashSet<string>(StringComparer.OrdinalIgnoreCase), registry);

        /// <summary>Wie Process(source, basePath), aber mit einer VOM
        /// AUFRUFER bereitgestellten (und damit zwischen mehreren Process()-
        /// Aufrufen TEILBAREN) "bereits eingefügt"-Menge - siehe
        /// DirectiveContext.AlreadyIncluded-Doku für den Grund (globale
        /// statt pro-Wurzeldatei-Komposition, z.B. Prelude + Nutzer-Skript
        /// zusammen, siehe Parser.ParseWithPrelude).</summary>
        public static string Process(
            string source, string basePath, HashSet<string> alreadyIncluded, DirectiveRegistry? registry = null)
        {
            var pre = new Preprocessor(registry ?? DirectiveRegistry.CreateDefault());
            return pre.ProcessInner(source, basePath, alreadyIncluded);
        }

        internal string ProcessFileRecursive(string fullPath, HashSet<string> alreadyIncluded)
        {
            if (_includeChain.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
                throw new PreprocessorException(
                    $"Zirkuläres Einschleusen von '{fullPath}' (Kette: {string.Join(" -> ", _includeChain)} -> {fullPath}).");

            string includedSource;
            try
            {
                includedSource = File.ReadAllText(fullPath);
            }
            catch (Exception ex)
            {
                throw new PreprocessorException($"'{fullPath}' konnte nicht gelesen werden: {ex.Message}", ex);
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

            for (int lineNo = 0; lineNo < lines.Length; lineNo++)
            {
                var line = lines[lineNo].TrimEnd('\r');
                var match = DirectiveLine.Match(line);
                if (!match.Success)
                {
                    sb.Append(line).Append('\n');
                    continue;
                }

                string name = match.Groups[1].Value;
                if (!_registry.TryGet(name, out var def))
                {
                    // Nicht bei DIESEM Preprocessor registriert - z.B.
                    // '#extern "lib"' oder '#noshadow', die der PARSER
                    // selbst behandelt (siehe DirectiveRegistry-Klassendoku) -
                    // unverändert durchreichen, KEIN Fehler.
                    sb.Append(line).Append('\n');
                    continue;
                }

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

            return sb.ToString();
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
                    throw new PreprocessorException($"'#{def.Name}' erwartet keine Parameter (Zeile {line}).");
                return Array.Empty<Value>();
            }

            List<Token> tokens;
            try
            {
                tokens = new Lexer(argText).Tokenize();
            }
            catch (LexException ex)
            {
                throw new PreprocessorException($"Ungültige Parameter für '#{def.Name}' (Zeile {line}): {ex.Message}");
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
                    $"Unerwartetes Token '{tokens[i].Lexeme}' in den Parametern von '#{def.Name}' (Zeile {line}).");

            if (values.Count != def.ParamCount)
                throw new PreprocessorException(
                    $"'#{def.Name}' erwartet {def.ParamCount} Parameter, erhalten {values.Count} (Zeile {line}).");

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
                            $"Erwarte eine Zahl nach '-' in den Parametern von '#{directiveName}' (Zeile {line}).");
                    var numTok = tokens[i];
                    i++;
                    return numTok.Type == TokenType.IntLiteral
                        ? Value.MakeInt(-(long)numTok.LiteralValue!, Unit.Parse(numTok.UnitSuffix ?? ""))
                        : Value.MakeFloat(-(double)numTok.LiteralValue!, Unit.Parse(numTok.UnitSuffix ?? ""));
                }

                default:
                    throw new PreprocessorException(
                        $"Erwarte einen literalen Wert (String/Zahl/Zeichen/bool/undefined) in den Parametern von " +
                        $"'#{directiveName}', nicht '{tok.Lexeme}' (Zeile {line}).");
            }
        }
    }

    public sealed class PreprocessorException : Exception
    {
        public PreprocessorException(string message) : base(message) { }
        public PreprocessorException(string message, Exception inner) : base(message, inner) { }
    }
}
