using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using fire.Ast;
using fire.Lexing;
using fire.Standard;
using fire.Values;

namespace fire.Compiler
{
    public sealed class ParseException : Exception
    {
        public int Line { get; }
        public int Column { get; }

        public ParseException(string message, int line, int column)
            : base($"{message} ({line}:{column})")
        {
            Line = line;
            Column = column;
        }
    }

    /// <summary>
    /// Klassischer rekursiver-Abstieg-Parser. Operator-Präzedenz (niedrig -> hoch):
    /// Assignment -> Or -> And -> Equality -> Relational/Is -> Additive ->
    /// Multiplicative -> Unary (-, !, ~) -> Postfix (Call/Member/Index/Coercion) -> Primary.
    ///
    /// Semikolons sind optionale Statement-Trenner (kein ASI-Regelwerk nötig,
    /// da jedes Statement anhand seines Start-Tokens eindeutig erkennbar ist).
    /// </summary>
    public sealed class Parser
    {
        private static readonly HashSet<TokenType> TypeKeywords = new()
        {
            TokenType.KwBool, TokenType.KwInt, TokenType.KwFloat,
            TokenType.KwChar, TokenType.KwString, TokenType.KwByte, TokenType.Class, TokenType.Undefined,
        };

        private readonly List<Token> _tokens;
        private int _pos;

        /// <summary>Die zuletzt per `#extern "libName"` gesetzte Bibliothek -
        /// wird jeder nachfolgenden `extern`-Deklaration mitgegeben (siehe
        /// ExternDecl.LibName), bis eine weitere `#extern`-Direktive sie
        /// ändert. Rein Parser-intern, keine Laufzeit-Bedeutung.</summary>
        private string? _currentExternLib;

        /// <summary>Die `#using`-Namen DIESER Quelle (SPEC "Namespaces") -
        /// kommen fertig vom Preprocessor (siehe Parsing.ProcessedSource),
        /// werden hier VOR ParseProgram() einmal übernommen und ändern sich
        /// während des Parsens nicht mehr (anders als _currentNamespace).
        /// Jeder TypeRef, den dieser Parser konstruiert, bekommt sie in sein
        /// eigenes Namespaces-Feld kopiert (siehe CurrentNamespaces) -
        /// dadurch tragen Resolver/Compiler später gar keinen eigenen
        /// Usings-Zustand mehr mit sich herum, jede Referenz weiß selbst,
        /// wo sie herkommt.</summary>
        private IReadOnlyList<string> _usingNamespaces = Array.Empty<string>();

        /// <summary>Der Namespace, in dem GERADE geparst wird (SPEC
        /// "Namespaces") - `null` außerhalb jedes `namespace`-Blocks.
        /// Verschachtelte Blöcke hängen sich mit '.' an (siehe
        /// ParseNamespaceDecl: sichert den alten Wert, setzt den neuen,
        /// schreibt beim Verlassen des Blocks den alten Wert zurück - so
        /// weiß der Parser beim Bauen JEDES TypeRef/JEDER Deklaration genau,
        /// "wo" im Programm gerade geparst wird, ohne einen separaten
        /// Baumdurchlauf danach zu brauchen).</summary>
        private string? _currentNamespace;
        private string? _currentClassName;

        /// <summary>`true`, während der Körper einer GENERISCHEN Klasse
        /// geparst wird (siehe ParsePropertyBody: statische Auto-Property).</summary>
        private bool _currentClassIsGeneric;

        /// <summary>Index dieser Quelle in der `sources`-Liste, die an
        /// ParseMultiple ging (0 = üblicherweise die Prelude) - EINMAL pro
        /// Parser-Instanz gesetzt (siehe ParseMultiple, jede Quelle bekommt
        /// ihre EIGENE, frische Parser-Instanz), ändert sich während des
        /// Parsens NICHT mehr (anders als `_currentNamespace`). Landet direkt
        /// in `Ast.ClassDecl.SourceIndex` - Grundlage für Compiler.
        /// CurrentSourceIndex/Bytecode.Chunk.MarkLine: ein Debugger (siehe
        /// Editor-Unterprojekt) braucht das, um bei mehreren Quelldateien zu
        /// wissen, in WELCHER Datei eine gegebene Zeilennummer liegt - eine
        /// nackte Zeile allein ist dann mehrdeutig.</summary>
        private int _sourceIndex;

        /// <summary>Stack der synthetischen Zielvariablen-Namen aktiver
        /// `with`-Blöcke (innerster zuletzt) - siehe ParseWithStmt. Ein
        /// bloßes '.' am Anfang eines Ausdrucks (siehe ParsePrimary) bezieht
        /// sich immer auf den INNERSTEN umschließenden `with`-Block.</summary>
        private readonly Stack<string> _withVarStack = new();
        private int _withCounter;

        /// <summary>Zähler für die synthetische Zielvariable eines `switch`
        /// (siehe ParseSwitchStmt) - anders als bei `with` braucht switch
        /// KEINEN Stack (kein '.'-artiges implizites Ziel, das sich auf den
        /// innersten umschließenden switch bezieht), nur eindeutige Namen für
        /// verschachtelte switches.</summary>
        private int _switchCounter;

        // Zählt offene '(' / '[' . Innerhalb einer offenen Klammer soll ein
        // Zeilenumbruch NICHT als Statement-Trenner wirken (mehrzeilige
        // Funktionsaufrufe/Argumentlisten/Bedingungen müssen weiterhin
        // funktionieren) - nur '{'/'}' zählen bewusst nicht mit, da innerhalb
        // eines Blocks Zeilenumbrüche ja genau die Statement-Grenzen markieren.
        private int _bracketDepth;

        /// <summary>Ungleich null, solange der TOP-LEVEL-Wertausdruck einer
        /// switch-case-Bedingung geparst wird (siehe ParseSwitchStmt) -
        /// unterdrückt dabei GENAU auf dieser Klammerungstiefe (`_bracketDepth`)
        /// die normale ':'-Postfix-Behandlung (Einheiten-Koersion, siehe
        /// ParsePostfix), da 'case 2:' sonst das ':' fälschlich als
        /// Koersions-Operator verschluckt, bevor ParseSwitchStmt es selbst
        /// als Zweig-Trenner konsumieren kann. Sobald eine verschachtelte
        /// '('/'[' betreten wird, steigt `_bracketDepth` über diesen Wert -
        /// ':' funktioniert dort also ganz normal weiter (z.B.
        /// 'case (x : mm):' - der INNERE ':' ist gewollte Koersion).</summary>
        private int? _suppressColonPostfixAtDepth;

        public Parser(List<Token> tokens)
        {
            _tokens = tokens;
        }

        /// <summary>Einfacher Redirect auf ParseMultiple für Tests/Aufrufer,
        /// die keine Namespaces/Usings brauchen - KEINE eigene Parse-Logik
        /// (ein einzelnes ProcessedSource mit leeren Usings ist nur der
        /// Sonderfall "eine Quelle, kein `#using`"). `source` läuft NICHT
        /// durch den Preprocessor - wer `#include`/`#using` braucht, ruft
        /// Preprocessor.Process selbst auf und übergibt dessen Ergebnis an
        /// ParseMultiple.</summary>
        public static List<Stmt> Parse(string source) =>
            ParseMultiple(new[] { new ProcessedSource(source, Array.Empty<string>()) });

        /// <summary>Parst BELIEBIG VIELE bereits vorverarbeitete Quelltext-
        /// Stücke (siehe Preprocessing.ProcessedSource - jedes trägt seine
        /// EIGENEN, vom Preprocessor gesammelten `#using`-Namen) zu EINEM
        /// kombinierten Programm. `#using` gilt dabei NUR LOKAL für
        /// Deklarationen UND Referenzen aus GENAU DEM ProcessedSource, das
        /// die Direktive selbst enthielt (SPEC "Mehrere Quelldateien") - bei
        /// mehreren, potenziell von verschiedenen Autoren stammenden
        /// Dateien wäre eine programmweite Wirkung überraschend.
        ///
        /// `sources` in der Reihenfolge, in der sie kombiniert werden sollen
        /// (üblich: Prelude zuerst, dann Bibliotheks-/Hilfsdateien, das
        /// eigentliche Hauptskript zuletzt - die Reihenfolge selbst hat für
        /// die Klassenauflösung keine Bedeutung, nur zur Übersicht).
        ///
        /// JEDE Typ-Referenz (TypeRef, `new X()`, `is of X`, `catch (X e)`,
        /// Basisklassen, `class extends X`) trägt ihren eigenen Namespace-
        /// Kontext direkt an sich selbst, gesetzt GENAU dann, wenn sie
        /// geparst wird (siehe CurrentNamespaces) - Resolver/Compiler
        /// brauchen dadurch gar keinen eigenen Usings-Zustand mehr, jede
        /// Referenz weiß selbst, wie sie sich auflösen soll. Das gilt auch
        /// für `class extends X`, selbst wenn Erweiterung und Zielklasse aus
        /// unterschiedlichen Dateien/Namespaces stammen (siehe
        /// Ast.ClassExtensionDecl.TargetRef-Doku).</summary>
        public static List<Stmt> ParseMultiple(IReadOnlyList<ProcessedSource> sources)
        {
            var combined = new List<Stmt>();
            //var byStmt = new Dictionary<Stmt, int>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < sources.Count; i++)
            {
                var src = sources[i];
                var tokens = new Lexer(src.Source).Tokenize();
                var parser = new Parser(tokens);
                parser._usingNamespaces = src.Usings;
                parser._sourceIndex = i;
                var stmts = parser.ParseProgram();
                //foreach (var stmt in stmts)
                //    byStmt[stmt] = i;
                combined.AddRange(stmts);
            }
            //sourceIndexByStmt = byStmt;
            // Disambiguierung VOR den Erweiterungen: `class extends Box` meint
            // (wie jede Referenz ohne Typ-Argumente) die nicht-generische
            // Klasse, siehe GenericClassNames.
            return MergeClassExtensions(DisambiguateGenericClasses(FlattenNamespaceWrappers(combined)));
        }

        /// <summary>Gibt jeder GENERISCHEN Klasse, neben der eine
        /// NICHT-generische Klasse mit demselben (vollqualifizierten) Namen
        /// existiert, ihren internen Namen `Name`N` (siehe
        /// GenericClassNames) - erst hier, nach dem Parsen ALLER Quellen,
        /// weiß man ja, ob es so eine Kollision gibt (jede Quelle wird von
        /// einer eigenen Parser-Instanz gelesen). Ohne Kollision bleibt das
        /// Programm unverändert. Zwei generische Klassen mit gleichem Namen
        /// UND gleicher Typ-Parameter-Anzahl bleiben eine Doppeldefinition
        /// (der Resolver meldet sie), gleicher Name mit unterschiedlicher
        /// Anzahl ohne nicht-generische Klasse ebenso - nur "generisch
        /// neben nicht-generisch" ist bewusst erlaubt.</summary>
        private static List<Stmt> DisambiguateGenericClasses(List<Stmt> program)
        {
            // Klassen und Interfaces haben getrennte Namenstabellen: `Command` und `Command<T>` kollidieren, `ICommand` und `ICommand<T>` ebenso
            var nonGenericNames = new HashSet<string>();
            var nonGenericInterfaces = new HashSet<string>();
            foreach (var stmt in program)
            {
                if (stmt is ClassDecl cd && (cd.TypeParams == null || cd.TypeParams.Count == 0)) nonGenericNames.Add(cd.Name);
                if (stmt is InterfaceDecl id && (id.TypeParams == null || id.TypeParams.Count == 0)) nonGenericInterfaces.Add(id.Name);
            }
            if (nonGenericNames.Count == 0 && nonGenericInterfaces.Count == 0) return program;

            var result = new List<Stmt>(program.Count);
            foreach (var stmt in program)
            {
                if (stmt is ClassDecl { TypeParams.Count: > 0 } generic && nonGenericNames.Contains(generic.Name))
                    result.Add(generic with { Name = GenericClassNames.Mangle(generic.Name, generic.TypeParams!.Count) });
                else if (stmt is InterfaceDecl { TypeParams.Count: > 0 } genericInterface && nonGenericInterfaces.Contains(genericInterface.Name))
                    result.Add(genericInterface with { Name = GenericClassNames.Mangle(genericInterface.Name, genericInterface.TypeParams!.Count) });
                else
                    result.Add(stmt);
            }
            return result;
        }

        /// <summary>Die für ein JETZT geparstes TypeRef/eine JETZT geparste
        /// Referenz geltenden Namespaces (SPEC "Namespaces") - an erster
        /// Stelle der aktuelle Namespace (_currentNamespace, falls einer),
        /// danach die `#using`-Namen dieser Quelle (_usingNamespaces). Ohne
        /// umschließenden Namespace (Top-Level) enthält die Liste nur die
        /// Usings. Die Reihenfolge kodiert bereits die Priorität (aktueller
        /// Namespace vor `#using`) - siehe TypeRef.ResolveBaseName.</summary>
        private IReadOnlyList<string> CurrentNamespaces() =>
            _currentNamespace == null
                ? _usingNamespaces
                : new[] { _currentNamespace }.Concat(_usingNamespaces).ToList();

        /// <summary>Hängt `simpleName` an den aktuellen Namespace an (SPEC
        /// "Namespaces") - `_currentNamespace + "." + simpleName`, oder
        /// `simpleName` unverändert außerhalb jedes `namespace`-Blocks. Für
        /// Klassen-/Interface-/Enum-NAMEN SELBST (nicht für Referenzen
        /// darauf, siehe TypeRef.ResolveBaseName), direkt beim Parsen der
        /// jeweiligen Deklaration angewendet.</summary>
        private string QualifyDeclName(string simpleName) =>
            _currentNamespace == null ? simpleName : _currentNamespace + "." + simpleName;

        /// <summary>Führt alle `class extends X { ... }`-Erweiterungen (siehe
        /// Ast.ClassExtensionDecl) in ihre jeweilige Zielklasse zusammen, BEVOR
        /// Resolver/Compiler das Programm überhaupt sehen - die neuen
        /// Mitglieder landen 1:1 in der ORIGINALEN ClassDecl.Members-Liste,
        /// als hätten sie dort von Anfang an gestanden (Ruby-artiges
        /// "Reopening"). Mehrere Erweiterungen derselben Zielklasse werden
        /// alle zusammengeführt, in der Reihenfolge, in der sie im Programm
        /// vorkommen. Wirft, wenn eine Erweiterung eine Klasse nennt, die
        /// NICHT im selben (übergebenen) Programm gefunden wird.</summary>
        public static List<Stmt> MergeClassExtensions(IReadOnlyList<Stmt> program)
        {
            var extensions = new List<ClassExtensionDecl>();
            var baseTypeExtensions = new Dictionary<string, List<ClassExtensionDecl>>();
            var rest = new List<Stmt>();
            foreach (var stmt in program)
            {
                // `class extends string { ... }`: kein ClassDecl, in das man mergen könnte - alle
                // Erweiterungen desselben Basistyps werden unten zu EINER Sammelklasse (siehe
                // BaseTypeExtensions).
                if (stmt is ClassExtensionDecl baseExt
                    && BaseTypeExtensions.IsExtendable(baseExt.TargetRef.BaseName))
                {
                    if (!baseTypeExtensions.TryGetValue(baseExt.TargetRef.BaseName, out var list))
                        baseTypeExtensions[baseExt.TargetRef.BaseName] = list = new List<ClassExtensionDecl>();
                    list.Add(baseExt);
                }
                else if (stmt is ClassExtensionDecl ext) extensions.Add(ext);
                else rest.Add(stmt);
            }
            foreach (var (typeName, list) in baseTypeExtensions)
                rest.Add(new ClassDecl(list[0].Source, list[0].Line, BaseTypeExtensions.ClassName(typeName), null,
                    list.SelectMany(e => e.Members).ToList()));
            if (extensions.Count == 0) return rest;

            // TargetRef.ResolveBaseName (SPEC "Namespaces") braucht die Menge
            // ALLER Klassennamen im (schon namespace-flachen) Programm -
            // TargetRef trägt seinen EIGENEN Namespace-Kontext (den der
            // Erweiterung selbst, nicht den der Zielklasse, siehe
            // Ast.ClassExtensionDecl-Doku), dadurch findet eine Erweiterung
            // ihre Zielklasse auch über Datei-/Namespace-Grenzen hinweg.
            var knownNames = new HashSet<string>(rest.OfType<ClassDecl>().Select(cd => cd.Name));
            bool IsKnown(string n) => knownNames.Contains(n);

            var result = new List<Stmt>(rest.Count);
            var extendedNames = new HashSet<string>();
            foreach (var stmt in rest)
            {
                if (stmt is ClassDecl cd)
                {
                    var extraMembers = extensions
                        .Where(e => e.TargetRef.ResolveBaseName(IsKnown) == cd.Name)
                        .SelectMany(e => e.Members)
                        .ToList();
                    if (extraMembers.Count > 0)
                    {
                        // WICHTIG: ClassDecl ist ein record - 'with' erzeugt
                        // eine NEUE, unveränderliche Kopie, ändert 'cd' nicht
                        // in-place. Die gemergte Kopie muss deshalb explizit
                        // in die Ergebnisliste - ein einfaches 'result.Add(stmt)'
                        // hier würde wieder die alte, unveränderte ClassDecl
                        // einfügen und die Zusatz-Mitglieder verwerfen.
                        cd = cd with { Members = cd.Members.Concat(extraMembers).ToList() };
                        extendedNames.Add(cd.Name);
                    }
                    result.Add(cd);
                    continue;
                }
                result.Add(stmt);
            }

            foreach (var ext in extensions)
            {
                string resolved = ext.TargetRef.ResolveBaseName(IsKnown);
                if (!extendedNames.Contains(resolved))
                    throw new ParseException(
                        $"'class extends {ext.TargetRef.BaseName}' - class '{ext.TargetRef.BaseName}' is not known in the same " +
                        "program (extensions cannot create a new class).",
                        ext.Line, 1);
            }

            return result;
        }

        /// <summary>Klopft alle NamespaceDecl-Knoten trivial platt (siehe
        /// Ast.NamespaceDecl-Doku) - KEINE Umbenennung/Qualifizierung mehr
        /// nötig (das ist beim Parsen selbst schon passiert, siehe
        /// ParseNamespaceDecl/QualifyDeclName/CurrentNamespaces), nur noch
        /// simples rekursives Auspacken der Members an die Stelle des
        /// Wrapper-Knotens.</summary>
        private static List<Stmt> FlattenNamespaceWrappers(List<Stmt> program)
        {
            var result = new List<Stmt>();
            foreach (var stmt in program)
            {
                if (stmt is NamespaceDecl nsDecl)
                    result.AddRange(FlattenNamespaceWrappers(nsDecl.Members.ToList()));
                else
                    result.Add(stmt);
            }
            return result;
        }

        public List<Stmt> ParseProgram()
        {
            var statements = new List<Stmt>();
            while (!Check(TokenType.Eof))
            {
                // 'catch threads(...)'/'catch terminate(...)' sind GLOBALE
                // Deklarationen (siehe ParseGlobalHandlerDecl), keine
                // normalen Statements - nur hier auf Top-Level-Ebene erkannt,
                // damit sie nicht mit dem "catch ohne try"-Feature innerhalb
                // von Blöcken kollidieren (siehe dortige Doku).
                if (Check(TokenType.Catch) && NextLooksLikeGlobalHandler())
                    statements.Add(ParseGlobalHandlerDecl());
                else
                    statements.Add(ParseStatement());
            }
            return statements;
        }

        /// <summary>Nach 'catch' folgt entweder 'terminate' (echtes Keyword)
        /// oder der kontextabhängige Bezeichner 'threads' - beides eindeutig
        /// von einem normalen 'catch (...)' unterscheidbar, das IMMER direkt
        /// mit '(' weitergeht.</summary>
        private bool NextLooksLikeGlobalHandler() =>
            PeekAt(1).Type == TokenType.Terminate ||
            (PeekAt(1).Type == TokenType.Identifier && PeekAt(1).Lexeme == "threads");

        // -----------------------------------------------------------
        // Statements
        // -----------------------------------------------------------
        private Stmt ParseStatement()
        {
            // `probe a.b changed ...` / `silence a.b`: kontextabhängige Schlüsselwörter - nur wenn direkt ein Bezeichner/`this` folgt (zwei Namen
            // hintereinander sind sonst nie ein gültiger Ausdruck), so bleiben `probe`/`silence` als Variablennamen nutzbar
            if (Check(TokenType.Identifier) && Peek().Lexeme == "silence" && IsProbeOperandNext()) return ParseSilence();
            if (Check(TokenType.Identifier) && Peek().Lexeme == "probe" && IsProbeOperandNext())
            {
                int probeLine = Peek().Line;
                var probe = ParseProbe();
                ExpectStatementTerminator();
                return new ExprStmt(_sourceIndex, probeLine, probe);
            }
            if (Check(TokenType.Var)) return ParseVarDecl();
            if (Check(TokenType.Readonly)) return ParseReadonlyDecl();
            if (Check(TokenType.Enum)) return ParseEnumDecl();
            if (Check(TokenType.Class)) return ParseClassDecl();
            if (Check(TokenType.Namespace)) return ParseNamespaceDecl();
            if (Check(TokenType.Actor)) return ParseActorDecl();
            if (Check(TokenType.Interface)) return ParseInterfaceDecl();
            if (NextLooksLikeTypeThenName()) return ParseBareTypedDecl();
            if (Check(TokenType.If)) return ParseIf();
            if (Check(TokenType.While)) return ParseWhile();
            if (Check(TokenType.For)) return ParseFor();
            if (Check(TokenType.Foreach)) return ParseForeach();
            if (Check(TokenType.Return)) return ParseReturn();
            if (Check(TokenType.Throw)) return ParseThrowStmt();
            // 'try sync'/'try sync flat'/'try process'/'try Name(...)' sind
            // AUSDRÜCKE (siehe ParseSyncExpr/ParsePrimary), kein try/catch-
            // Block - ein try/catch-Block hat IMMER direkt eine '{' nach
            // 'try' (siehe ParseTry: ParseBlock() unmittelbar danach), jede
            // andere Ausdrucksform NIE - deshalb reicht diese EINE positive
            // Prüfung, um beide sauber zu trennen (statt jede einzelne
            // Ausdrucksform hier aufzählen zu müssen).
            if (Check(TokenType.Try) && PeekAt(1).Type == TokenType.LBrace) return ParseTry();
            if (Check(TokenType.Hash)) return ParseDirective();
            if (Check(TokenType.Extern)) return ParseExternDecl();
            if (Check(TokenType.Unsafe)) return ParseUnsafeStmt();
            if (Check(TokenType.With)) return ParseWithStmt();
            if (Check(TokenType.Switch)) return ParseSwitchStmt();
            if (Check(TokenType.Fire)) return ParseFireStmt();
            if (Check(TokenType.Sync) && PeekAt(1).Type == TokenType.Identifier && PeekAt(1).Lexeme == "global" && PeekAt(2).Type == TokenType.LBrace)
                return ParseSyncGlobalBlock();
            if (Check(TokenType.Break))
            {
                int breakLine = Advance().Line;
                ExpectStatementTerminator();
                return new BreakStmt(_sourceIndex ,breakLine);
            }
            if (Check(TokenType.Continue))
            {
                int continueLine = Advance().Line;
                ExpectStatementTerminator();
                return new ContinueStmt(_sourceIndex, continueLine);
            }
            if (Check(TokenType.Leave))
            {
                int leaveLine = Advance().Line;
                ExpectStatementTerminator();
                return new LeaveStmt(_sourceIndex, leaveLine);
            }
            if (Check(TokenType.Terminate)) return ParseTerminateStmt();
            if (Check(TokenType.Process)) return ParseProcessStmt();
            if (Check(TokenType.LBrace)) return ParseBlock();

            var expr = ParseExpression();
            ExpectStatementTerminator();
            return new ExprStmt(_sourceIndex, expr.Line, expr);
        }

        /// <summary>
        /// Parst einen Block. Trifft der Block auf ein "nacktes" `catch` (ohne
        /// vorangehendes `try`), gilt ab dort implizit ein try-Bereich bis zum
        /// Blockende: die restlichen Statements werden rekursiv als geschützter
        /// Block geparst und zusammen mit den davor gesammelten catch-Klauseln
        /// (und optionalem finally) in ein TryStmt gewrappt, das den Block abschließt.
        /// </summary>
        private Stmt.BlockStmt ParseBlock()
        {
            int line = Peek().Line;
            Expect(TokenType.LBrace, "Expected '{'");

            var statements = new List<Stmt>();
            while (!Check(TokenType.RBrace) && !Check(TokenType.Eof))
            {
                if (Check(TokenType.Catch))
                {
                    statements.Add(ParseImplicitCatchTail());
                    break; // ParseImplicitCatchTail hat den Rest des Blocks bereits konsumiert.
                }
                statements.Add(ParseStatement());
            }

            Expect(TokenType.RBrace, "Expected '}' at the end of the block");
            return new Stmt.BlockStmt(_sourceIndex, line, statements);
        }

        /// <summary>Parst ein oder mehrere `catch`-Klauseln ohne vorangehendes `try`
        /// und behandelt den Rest des aktuellen Blocks als geschützten Bereich.</summary>
        private Stmt ParseImplicitCatchTail()
        {
            int line = Peek().Line;
            var catches = new List<CatchClause>();
            while (Check(TokenType.Catch))
                catches.Add(ParseCatchClause());

            Stmt.BlockStmt? finallyBlock = null;
            if (Match(TokenType.Finally))
                finallyBlock = ParseBlock();

            var restStatements = new List<Stmt>();
            while (!Check(TokenType.RBrace) && !Check(TokenType.Eof))
            {
                if (Check(TokenType.Catch))
                {
                    // Weitere nackte catch-Ketten direkt hintereinander -> ebenfalls einsammeln.
                    restStatements.Add(ParseImplicitCatchTail());
                    break;
                }
                restStatements.Add(ParseStatement());
            }

            var protectedBlock = new Stmt.BlockStmt(_sourceIndex, line, restStatements);
            return new TryStmt(_sourceIndex, line, protectedBlock, catches, finallyBlock);
        }

        private CatchClause ParseCatchClause()
        {
            int line = Peek().Line;
            Expect(TokenType.Catch, "Expected 'catch'");
            Expect(TokenType.LParen, "Expected '(' after 'catch'");

            // `catch (TypeName varName)` bzw. ungetypt `catch (varName)` -
            // SEIT SPEC "Einheiten-Deklarationen" dieselbe Reihenfolge wie
            // überall sonst (Typ/`var` zuerst, Name danach), NICHT mehr die
            // alte "Name zuerst, Typ per ':' danach"-Schreibweise (die genau
            // die Mehrdeutigkeit war, die der ':' jetzt überall einheitlich
            // nur noch für Einheiten löst). NextLooksLikeTypeThenName()
            // erkennt dabei auch einen punktierten Typnamen wie
            // 'Geometry.MyException e' korrekt (siehe dortige Doku).
            TypeRef? typeRef = null;
            if (NextLooksLikeTypeThenName())
                typeRef = new TypeRef(ParseDottedName("type name in catch(...)"), null, 0, Namespaces: CurrentNamespaces());

            string varName = Expect(TokenType.Identifier, "Expected an identifier in catch(...)").Lexeme;

            Expect(TokenType.RParen, "Expected ')' after the catch parameters");
            var body = ParseBlock();
            return new CatchClause(_sourceIndex, line, typeRef, varName, body);
        }

        private Stmt ParseTry()
        {
            int line = Peek().Line;
            Expect(TokenType.Try, "Expected 'try'");
            var tryBlock = ParseBlock();

            var catches = new List<CatchClause>();
            while (Check(TokenType.Catch))
                catches.Add(ParseCatchClause());

            Stmt.BlockStmt? finallyBlock = null;
            if (Match(TokenType.Finally))
                finallyBlock = ParseBlock();

            if (catches.Count == 0 && finallyBlock == null)
                throw Error("'try' needs at least one 'catch' block or 'finally'", Peek());

            return new TryStmt(_sourceIndex, line, tryBlock, catches, finallyBlock);
        }

        private Stmt ParseVarDecl()
        {
            var decl = ParseVarDeclCore(isReadonly: false);
            ExpectStatementTerminator();
            return decl;
        }

        /// <summary>`readonly var ...` bzw. `readonly Type name ...` - der
        /// Resolver verbietet danach jede weitere Zuweisung (siehe
        /// Resolver.ResolveAssignTarget). Reine Parser-seitige Weiche, welche
        /// der beiden Deklarationsformen mit dem Flag gestempelt wird.</summary>
        private Stmt ParseReadonlyDecl()
        {
            Expect(TokenType.Readonly, "Expected 'readonly'");

            if (Check(TokenType.Var))
            {
                var decl = ParseVarDeclCore(isReadonly: true);
                ExpectStatementTerminator();
                return decl;
            }
            if (NextLooksLikeTypeThenName())
                return ParseBareTypedDecl(isReadonly: true);

            throw Error("Expected 'var' or a type declaration after 'readonly'", Peek());
        }

        /// <summary>var-Deklaration ohne eigenen Terminator-Konsum - für Stellen
        /// wie den for-Init, wo der Aufrufer das trennende ';' selbst erwartet
        /// (sonst würde hier schon eines "verschluckt" und der Aufrufer danach
        /// fälschlich ein zweites erwarten).</summary>
        private VarDeclStmt ParseVarDeclCore(bool isReadonly)
        {
            int line = Peek().Line;
            Expect(TokenType.Var, "Expected 'var'");
            string name = Expect(TokenType.Identifier, "Expected a variable name").Lexeme;

            // Array-Klammern VOR dem Typ parsen (direkt hinter dem Bezeichner) -
            // sonst würde `var arr : int[]` fälschlich versuchen, "[]" als
            // Bitbreiten-Klammer hinter 'int' zu lesen (Kollision, siehe 'new').
            var arrayRanks = ParseArrayRanks();

            // Der ':' nach dem Namen legt IMMER nur eine EINHEIT fest, NIE
            // einen Typ (SPEC "Einheiten-Deklarationen") - `var a : mm` heißt
            // "Typ wie gewöhnlich aus dem Initialisierer hergeleitet, Einheit
            // ist FEST mm", nicht "Typ ist mm". Der eigentliche Typ bleibt bei
            // `var` also weiterhin `null` (Inferenz durch den Resolver), außer
            // eine Einheit ist angegeben - dann trägt ein TypeRef mit
            // TypeRef.InferredMarker als BaseName NUR die Einheit (siehe
            // TypeRef.IsInferred-Doku).
            TypeRef? type = null;
            if (Match(TokenType.Colon))
                type = new TypeRef(TypeRef.InferredMarker, null, 0, Unit: ParseUnitName());

            Expr? initializer = null;
            if (Match(TokenType.Assign))
                initializer = ParseExpression();

            return new VarDeclStmt(_sourceIndex, line, name, type, arrayRanks, initializer, isReadonly);
        }

        /// <summary>Liest eine Einheit nach ':' (SPEC "Einheiten-Deklarationen") -
        /// bewusst NUR ein einzelner Bezeichner (z.B. "mm"), NIE ein voller
        /// TypeRef (keine Bitbreite, kein Pointer, kein Namespace-Pfad) - genau
        /// das war die vorherige Unklarheit: der ':' wurde bisher über
        /// ParseTypeRef() aufgelöst, konnte also (fälschlich) wie eine
        /// zweite, alternative Art der TYP-Angabe aussehen. Die Einheit selbst
        /// wird hier NICHT gegen eine bekannte Liste geprüft (Values.Unit.Parse
        /// akzeptiert jeden Bezeichner als atomare, frei erfundene Einheit,
        /// siehe dortige Doku) - eine etwaige Prüfung "ist mm wirklich schon
        /// bekannt" wäre ohnehin gegenstandslos.</summary>
        private string ParseUnitName() => Expect(TokenType.Identifier, "Expected a unit name after ':'").Lexeme;

        /// <summary>"Nackte" Deklaration ohne `var` (C-artig): `Type name[ranks]
        /// [: einheit] [= init]`. Semantisch identisch zu `var name : Type`
        /// (bis auf die zusätzliche, optionale Einheit), nur andere
        /// Oberflächensyntax - wird als dasselbe VarDeclStmt repräsentiert. Nur
        /// erreichbar, wenn NextLooksLikeTypeThenName() bereits bestätigt hat,
        /// dass hier wirklich ein Typ folgt (und nicht z.B. ein Ausdrucks-
        /// Statement).</summary>
        private Stmt ParseBareTypedDecl(bool isReadonly = false)
        {
            int line = Peek().Line;
            var type = ParseTypeRef();
            string name = Expect(TokenType.Identifier, "Expected an identifier").Lexeme;
            var arrayRanks = ParseArrayRanks();

            // Wie bei ParseVarDeclCore: ':' legt IMMER nur eine Einheit fest
            // (SPEC "Einheiten-Deklarationen") - hier ist der Typ (anders als
            // bei `var`) schon explizit da, `int a : mm` hat also BEIDES
            // gleichzeitig: einen festen Typ UND eine feste Einheit.
            if (Match(TokenType.Colon))
                type = type with { Unit = ParseUnitName() };

            Expr? initializer = null;
            if (Match(TokenType.Assign))
                initializer = ParseExpression();

            ExpectStatementTerminator();
            return new VarDeclStmt(_sourceIndex, line, name, type, arrayRanks, initializer, isReadonly);
        }

        /// <summary>Typname nach einem ':' – entweder eines der Basistyp-Keywords
        /// oder ein Bezeichner (Klassenname). Nur der Name, ohne Bitbreite/Pointer -
        /// für Kontexte, die (bisher) nur einen reinen Namen brauchen (`is of`,
        /// Basisklasse, catch-Typ).</summary>
        /// <summary>Liest einen Typnamen - entweder ein Typ-Keyword (int/
        /// float/...) oder einen Bezeichner, optional gefolgt von einem oder
        /// mehreren '.'-getrennten weiteren Bezeichnern (Namespace-
        /// qualifizierter Name, z.B. 'Foo.Bar' - siehe Ast.NamespaceDecl/
        /// SPEC "Namespaces"). Ein '.' wird hier NUR konsumiert, wenn direkt
        /// danach ein Bezeichner folgt - diese Sprache kennt sonst keine
        /// Position, an der ein Typname selbst (nicht ein Ausdruck) von
        /// einem '.' gefolgt sein könnte, der lookahead ist also rein
        /// defensiv.</summary>
        private string ParseTypeAnnotationName()
        {
            if (TypeKeywords.Contains(Peek().Type))
                return Advance().Lexeme;
            string name = Expect(TokenType.Identifier, "Expected a type name").Lexeme;
            while (Check(TokenType.Dot) && PeekAt(1).Type == TokenType.Identifier)
            {
                Advance(); // '.'
                name += "." + Advance().Lexeme;
            }
            return name;
        }

        /// <summary>Vollständiger Typ-Verweis: Basisname, optional `[Bitbreite]`
        /// direkt dahinter (nur für int/float sinnvoll, vom Resolver geprüft),
        /// optional gefolgt von einem oder mehreren '*' für Pointer-Tiefe.</summary>
        /// <summary>Vollständiger Typ-Verweis: Basisname, optional `[Bitbreite]`
        /// direkt dahinter (nur für int/float sinnvoll, vom Resolver geprüft),
        /// optional gefolgt von einem oder mehreren '*' für Pointer-Tiefe -
        /// ODER, wenn der Name (bzw. der Name direkt danach) 'lambda' ist,
        /// ein Lambda-Typ mit optionaler Signatur (siehe ParseLambdaSignature/
        /// Ast.TypeRef.LambdaSignature-Doku): `[RückgabeTyp] lambda[&lt;P1,...,Pn&gt;]`
        /// - der RückgabeTyp steht dabei VOR 'lambda', nicht in den spitzen
        /// Klammern (die enthalten nur die Parametertypen), das macht die
        /// Grammatik unzweideutig ohne Trennzeichen zwischen Rückgabe- und
        /// Parametertypen zu brauchen.</summary>
        private TypeRef ParseTypeRef(bool allowArray = false)
        {
            string baseName = ParseTypeAnnotationName();
            var namespaces = CurrentNamespaces();

            if (baseName != "lambda" && Check(TokenType.Identifier) && Peek().Lexeme == "lambda")
            {
                Advance(); // 'lambda' konsumieren - 'baseName' war in Wahrheit der Rückgabetyp
                return new TypeRef("lambda", null, 0, ParseLambdaSignature(returnTypeName: baseName), namespaces);
            }

            if (baseName == "lambda")
            {
                // `lambda field|property|member|selector<T>`: ein Selektor (siehe LambdaSignature.IsSelector)
                if (Check(TokenType.Identifier) && Peek().Lexeme is "field" or "property" or "member" or "method" or "selector" && PeekAt(1).Type == TokenType.Lt)
                {
                    string selectorKind = Advance().Lexeme; // 'field', 'property', 'member', 'method' oder 'selector'
                    Advance(); // '<'
                    var targetTypes = new List<string>();
                    if (!Check(TokenType.Gt)) targetTypes.Add(ParseTypeAnnotationName()); // `lambda selector<>`: ohne Typ
                    Expect(TokenType.Gt, $"Expected '>' after the type of 'lambda {selectorKind}<...>'");
                    return new TypeRef("lambda", null, 0, new LambdaSignature(null, targetTypes, IsSelector: true, SelectorKind: selectorKind), namespaces);
                }
                return new TypeRef("lambda", null, 0, ParseLambdaSignature(returnTypeName: null), namespaces);
            }

            // 'byte' ist reines Sugar für 'int[8]' (siehe SPEC 8.10) - eine
            // explizite Bitbreite DANACH wäre widersprüchlich/redundant und
            // wird deshalb abgelehnt, statt sie stillschweigend zu ignorieren
            // oder zu überschreiben.
            if (baseName == "byte")
            {
                if (Check(TokenType.LBracket) && !NextIsEmptyBrackets())
                    throw Error("'byte' already has a fixed width of 8 bits - no additional '[...]' may follow", Peek());
                int bytePointerDepth = 0;
                while (Match(TokenType.Star)) bytePointerDepth++;
                return new TypeRef("int", 8, bytePointerDepth, Namespaces: namespaces, ArrayRank: ParseArrayTypeSuffix(allowArray));
            }

            // `[8]` = Bitbreite; leere Klammern `[]` gehören zum Array-Rückgabetyp (siehe unten).
            int? width = null;
            if (Check(TokenType.LBracket) && !NextIsEmptyBrackets())
            {
                Advance();
                var widthTok = Expect(TokenType.IntLiteral, "Expected a bit width (8/16/32/64)");
                width = (int)(long)widthTok.LiteralValue!;
                Expect(TokenType.RBracket, "Expected ']' after the bit width");
            }

            int pointerDepth = 0;
            while (Match(TokenType.Star)) pointerDepth++;

            return new TypeRef(baseName, width, pointerDepth, Namespaces: namespaces, ArrayRank: ParseArrayTypeSuffix(allowArray));
        }

        /// <summary>Steht als nächstes `[` `]` (leere Klammern)? Das ist ein Array-Typ
        /// (`int[]`), keine Bitbreite (`int[8]`).</summary>
        private bool NextIsEmptyBrackets() =>
            Check(TokenType.LBracket) && PeekAt(1).Type == TokenType.RBracket;

        /// <summary>Liest die leeren Klammerpaare eines Array-TYPS (`int[]`, `Dog[][]`) und liefert ihre
        /// Anzahl. Nur dort erlaubt, wo der Typ keinen Bezeichner hat, hinter dem die Klammern stünden
        /// (Rückgabetypen) - überall sonst gilt `Typ name[]`, und der Fehler sagt das.</summary>
        private int ParseArrayTypeSuffix(bool allowArray)
        {
            int rank = 0;
            while (NextIsEmptyBrackets())
            {
                if (!allowArray)
                    throw Error(
                        "For variables, fields and parameters an array is written with the brackets after the name " +
                        "('int values[]'); 'int[]' as a type only exists as the return type of a method or property", Peek());
                Advance(); // '['
                Advance(); // ']'
                rank++;
            }
            return rank;
        }

        /// <summary>Optionale `&lt;Param1,...,ParamN&gt;`-Parameterliste nach
        /// 'lambda' - leer (parameterlos), wenn kein '&lt;' folgt. Jeder
        /// Eintrag ist ein reiner Typname (siehe TypeRef.LambdaSignature-Doku
        /// für die Begründung, warum keine rekursiven TypeRefs).</summary>
        private LambdaSignature ParseLambdaSignature(string? returnTypeName)
        {
            var paramTypes = new List<string>();
            if (Match(TokenType.Lt))
            {
                if (!Check(TokenType.Gt))
                {
                    do
                    {
                        paramTypes.Add(ParseTypeAnnotationName());
                    } while (Match(TokenType.Comma));
                }
                Expect(TokenType.Gt, "Expected '>' after the lambda parameter types");
            }
            return new LambdaSignature(returnTypeName, paramTypes);
        }

        /// <summary>Array-Deklarator NACH dem Bezeichner: eine oder mehrere
        /// `[...]`-Gruppen, je mit optionalem Größen-Ausdruck (`[5]`) oder leer
        /// (`[]`, unbestimmte Größe). Leere Liste = kein Array.</summary>
        private List<Expr?> ParseArrayRanks()
        {
            var ranks = new List<Expr?>();
            while (Check(TokenType.LBracket))
            {
                Advance();
                Expr? size = Check(TokenType.RBracket) ? null : ParseExpression();
                Expect(TokenType.RBracket, "Expected ']'");
                ranks.Add(size);
            }
            return ranks;
        }

        /// <summary>Heuristik, ob an dieser Stelle "Typ dann Name" folgt (für
        /// Felder/Methoden/Parameter, wo der Typ optional ist): entweder ein
        /// Basistyp-Keyword (danach kann noch Bitbreite/Pointer/Array folgen),
        /// oder zwei aufeinanderfolgende Bezeichner (Klassenname + Membername).
        /// Ein Klassenname als Zeiger-/Array-Typ in dieser Position ist damit
        /// bewusst (noch) nicht abgedeckt - ein seltener/fortgeschrittener Fall,
        /// der sich bei Bedarf nachrüsten lässt.</summary>
        /// <summary>Erkennt "hier startet ein Typname (evtl. punktiert), gefolgt
        /// von einem weiteren Bezeichner" - für die Entscheidung "Deklaration
        /// oder etwas anderes" an JEDER Stelle, an der beides syntaktisch in
        /// Frage käme (Top-Level/lokale Anweisung, Feld, Parameter, Methoden-/
        /// extern-Rückgabetyp).
        ///
        /// Überspringt dafür die GESAMTE punktierte Kette (Geometry.Sub.Circle
        /// ...) und schaut, was DANACH kommt - nur wenn DAS wieder ein
        /// Bezeichner ist, war die Kette ein TYPNAME gefolgt vom eigentlichen
        /// Deklarationsnamen. Das ist keine bloße Heuristik, sondern eindeutig:
        /// zwei Bezeichner UNMITTELBAR hintereinander kommen in KEINEM
        /// gültigen Ausdruck vor (dafür bräuchte es immer einen Operator/eine
        /// Klammer/einen Punkt dazwischen) - 'Geometry.Funktion()' hat nach
        /// der Kette ein '(', keinen Bezeichner (Ausdrucks-Aufruf), 'Geometry.
        /// Circle x' dagegen schon (Deklaration). Klassenmitglieder-Zugriffe
        /// wie 'Geometry.Circle.Radius' sind davon unabhängig - die laufen
        /// über die normale Postfix-Kette ('.'-Zugriffe), sobald der erste
        /// Teil als gewöhnlicher Ausdruck (nicht als Deklaration) erkannt
        /// wurde.</summary>
        private bool NextLooksLikeTypeThenName()
        {
            if (TypeKeywords.Contains(Peek().Type)) return true;
            // 'lambda' als Typname (siehe ParseTypeRef) kann - anders als ein
            // Klassenname - auch von '<' statt einem weiteren Bezeichner
            // gefolgt werden ('lambda<int> x'), das würde die Ketten-Prüfung
            // unten verpassen.
            if (Check(TokenType.Identifier) && Peek().Lexeme == "lambda") return true;
            if (!Check(TokenType.Identifier)) return false;

            int offset = 1;
            while (PeekAt(offset).Type == TokenType.Dot && PeekAt(offset + 1).Type == TokenType.Identifier)
                offset += 2;
            // `Dog[] Name`: leere Klammern hinter dem Typnamen (Array-Rückgabetyp) - wie zwei Bezeichner
            // hintereinander kommt `Name[] Name` in keinem gültigen Ausdruck vor.
            while (PeekAt(offset).Type == TokenType.LBracket && PeekAt(offset + 1).Type == TokenType.RBracket)
                offset += 2;
            return PeekAt(offset).Type == TokenType.Identifier;
        }

        private Stmt ParseIf()
        {
            int line = Peek().Line;
            Expect(TokenType.If, "Expected 'if'");
            Expect(TokenType.LParen, "Expected '(' after 'if'");
            var cond = ParseExpression();
            Expect(TokenType.RParen, "Expected ')' after the if condition");
            var thenBranch = ParseStatement();
            Stmt? elseBranch = null;
            if (Match(TokenType.Else))
                elseBranch = ParseStatement();
            return new IfStmt(_sourceIndex, line, cond, thenBranch, elseBranch);
        }

        private Stmt ParseWhile()
        {
            int line = Peek().Line;
            Expect(TokenType.While, "Expected 'while'");
            Expect(TokenType.LParen, "Expected '(' after 'while'");
            var cond = ParseExpression();
            Expect(TokenType.RParen, "Expected ')' after the while condition");
            var body = ParseStatement();
            return new WhileStmt(_sourceIndex, line, cond, body);
        }

        private Stmt ParseFor()
        {
            int line = Peek().Line;
            Expect(TokenType.For, "Expected 'for'");
            Expect(TokenType.LParen, "Expected '(' after 'for'");

            Stmt? init = null;
            if (!Check(TokenType.Semicolon))
                init = Check(TokenType.Var) ? ParseVarDeclCore(isReadonly: false) : new ExprStmt(_sourceIndex, Peek().Line, ParseExpression());
            Expect(TokenType.Semicolon, "Expected ';' after the for initializer");

            Expr? cond = null;
            if (!Check(TokenType.Semicolon)) cond = ParseExpression();
            Expect(TokenType.Semicolon, "Expected ';' after the for condition");

            Expr? incr = null;
            if (!Check(TokenType.RParen)) incr = ParseExpression();
            Expect(TokenType.RParen, "Expected ')' after the for clauses");

            var body = ParseStatement();
            return new ForStmt(_sourceIndex, line, init, cond, incr, body);
        }

        private Stmt ParseForeach()
        {
            int line = Peek().Line;
            Expect(TokenType.Foreach, "Expected 'foreach'");
            Expect(TokenType.LParen, "Expected '(' after 'foreach'");
            string varName = Expect(TokenType.Identifier, "Expected a variable name").Lexeme;
            Expect(TokenType.In, "Expected 'in' in foreach");
            var iterable = ParseExpression();
            Expect(TokenType.RParen, "Expected ')' after the foreach clauses");
            var body = ParseStatement();
            return new ForeachStmt(_sourceIndex, line, varName, iterable, body);
        }

        private Stmt ParseReturn()
        {
            int line = Peek().Line;
            Expect(TokenType.Return, "Expected 'return'");
            Expr? value = null;
            if (!Check(TokenType.Semicolon) && !Check(TokenType.RBrace) && !Check(TokenType.Eof))
                value = ParseExpression();
            ExpectStatementTerminator();
            return new ReturnStmt(_sourceIndex, line, value);
        }

        private Stmt ParseThrowStmt()
        {
            int line = Peek().Line;
            Expect(TokenType.Throw, "Expected 'throw'");
            var value = ParseExpression();
            ExpectStatementTerminator();
            return new ThrowStmt(_sourceIndex, line, value);
        }

        /// <summary>`extern [ReturnType] Name(params)` - deklariert eine native
        /// Funktionssignatur ohne Body. ReturnType fehlt -> kein Rückgabewert.
        /// Bekommt die zuletzt per `#extern "libName"` gesetzte Bibliothek
        /// gestempelt (siehe ParseDirective/_currentExternLib) - null, wenn
        /// keine solche Direktive vor dieser Deklaration stand.</summary>
        private Stmt ParseExternDecl()
        {
            int line = Peek().Line;
            Expect(TokenType.Extern, "Expected 'extern'");

            TypeRef? returnType = null;
            if (NextLooksLikeTypeThenName())
                returnType = ParseTypeRef();

            string name = Expect(TokenType.Identifier, "Expected a function name after 'extern'").Lexeme;
            var parms = ParseParamList();
            ExpectStatementTerminator();
            return new ExternDecl(_sourceIndex, line, returnType, name, parms, _currentExternLib);
        }

        /// <summary>Präprozessor-Direktiven, aktuell `#extern "libName"` und
        /// `#noshadow` - setzt entweder die Bibliothek, gegen die
        /// NACHFOLGENDE `extern`-Deklarationen im Quelltext gestempelt
        /// werden (bis zur nächsten `#extern`-Direktive oder Dateiende),
        /// oder (siehe Ast.NoShadowDirective-Doku) schaltet den
        /// Read-only-Globals-Snapshot in `fire`-Blöcken ab. `#extern`
        /// erzeugt keinen eigenen, laufzeitrelevanten AST-Knoten (NoOpStmt) -
        /// wirkt rein beim Parsen; `#noshadow` dagegen erzeugt einen
        /// eigenen Knoten, den der Resolver in einem Vorab-Durchlauf
        /// einsammelt (siehe dort).</summary>
        private Stmt ParseDirective()
        {
            int line = Peek().Line;
            Expect(TokenType.Hash, "Expected '#'");

            if (Match(TokenType.Extern))
            {
                var libTok = Expect(TokenType.StringLiteral, "Expected a library name (string) after '#extern'");
                _currentExternLib = (string)libTok.LiteralValue!;
                ExpectStatementTerminator();
                return new NoOpStmt(_sourceIndex, line);
            }

            if (Check(TokenType.Identifier) && Peek().Lexeme == "noshadow")
            {
                Advance();
                ExpectStatementTerminator();
                return new NoShadowDirective(_sourceIndex, line);
            }

            if (Check(TokenType.Identifier) && Peek().Lexeme == "nosync")
            {
                Advance();
                ExpectStatementTerminator();
                return new NoSyncDirective(_sourceIndex, line);
            }

            if (Check(TokenType.Identifier) && Peek().Lexeme == "timeout")
            {
                Advance();
                var value = ParseExpression();
                ExpectStatementTerminator();
                return new TimeoutDirective(_sourceIndex, line, value);
            }

            // '#using' ist ab jetzt reine Preprocessor-Angelegenheit (siehe
            // Preprocessing.Preprocessor.ProcessInner/ProcessedSource) - eine
            // '#using'-Zeile wird dort schon erkannt und aus dem Text entfernt,
            // der Parser sieht sie nie mehr. Kein Fall dafür hier mehr nötig.
            throw Error($"Unknown preprocessor directive '#{Peek().Lexeme}' (known: '#extern \"libName\"', '#noshadow', '#nosync', '#timeout value')", Peek());
        }

        private Stmt ParseUnsafeStmt()
        {
            int line = Peek().Line;
            Expect(TokenType.Unsafe, "Expected 'unsafe'");
            var body = ParseBlock();
            return new UnsafeStmt(_sourceIndex, line, body);
        }

        /// <summary>`with ausdruck { .Feld = x; .Methode() }` (BASIC-artig) -
        /// reines Zucker, das der Parser vollständig auflöst: der `with`-
        /// Ausdruck wird EINMAL in eine synthetische, blockweit gültige
        /// Variable geschrieben, und jedes '.' am Anfang eines Ausdrucks
        /// INNERHALB des Blocks (siehe ParsePrimary, TokenType.Dot-Fall)
        /// bezieht sich implizit auf genau diese Variable. Resolver/Compiler
        /// sehen danach nur noch ganz normale `IdentifierExpr`/`MemberExpr`-
        /// Knoten - keine eigene Runtime-Unterstützung nötig. Als eigener
        /// `Stmt.BlockStmt` kompiliert, damit die synthetische Variable einen
        /// isolierten Scope bekommt (kollidiert nicht mit gleichnamigen
        /// Variablen davor/danach, wird beim Verlassen des Blocks wieder
        /// freigegeben) - `_withVarStack` erlaubt dabei beliebige
        /// Verschachtelung (ein '.' bezieht sich immer auf den INNERSTEN
        /// umschließenden `with`-Block).</summary>
        private Stmt ParseWithStmt()
        {
            int line = Peek().Line;
            Expect(TokenType.With, "Expected 'with'");
            var target = ParseExpression();

            string tempName = $"__with{_withCounter}__";
            _withCounter++;
            _withVarStack.Push(tempName);

            Expect(TokenType.LBrace, "Expected '{' after the with expression");

            var statements = new List<Stmt>
            {
                new VarDeclStmt(_sourceIndex, line, tempName, null, Array.Empty<Expr?>(), target),
            };
            while (!Check(TokenType.RBrace) && !Check(TokenType.Eof))
            {
                if (Check(TokenType.Catch))
                {
                    statements.Add(ParseImplicitCatchTail());
                    break; // ParseImplicitCatchTail hat den Rest des Blocks bereits konsumiert.
                }
                statements.Add(ParseStatement());
            }
            Expect(TokenType.RBrace, "Expected '}' at the end of the with block");

            // Erst NACH dem Parsen des Bodies wieder abbauen - Verschachtelung
            // (with a { with b { ... } }) braucht den äußeren Eintrag ja noch,
            // während der innere Body geparst wird, aber nicht mehr danach.
            _withVarStack.Pop();

            return new Stmt.BlockStmt(_sourceIndex, line, statements);
        }

        /// <summary>`switch(ausdruck) { case OP wert: ... break; case default: ... }`
        /// - reiner Zucker wie `with`, komplett zu einer If/Else-if-Kette
        /// desugarn: der switch-Ausdruck wird EINMAL in eine synthetische
        /// Variable geschrieben, jeder `case`-Zweig wird zu einer Bedingung
        /// `__switchN__ OP wert` (OP fehlt -> `==`, wie bei einem klassischen
        /// switch), `case default` wird zum abschließenden `else`. Resolver/
        /// Compiler/VM sehen davon nichts - normale If/BinaryExpr-Knoten.
        /// Anders als ein klassisches C-switch: KEIN Fallthrough (jeder Zweig
        /// ist eine eigene, exklusive If-Bedingung, kein Sprung ins Nächste),
        /// `break` ist deshalb rein syntaktischer Zweig-Abschluss (siehe
        /// ParseSwitchCaseBody), keine echte Sprunganweisung.</summary>
        private Stmt ParseSwitchStmt()
        {
            int line = Peek().Line;
            Expect(TokenType.Switch, "Expected 'switch'");
            Expect(TokenType.LParen, "Expected '(' after 'switch'");
            var subject = ParseExpression();
            Expect(TokenType.RParen, "Expected ')' after the switch expression");
            Expect(TokenType.LBrace, "Expected '{' after the switch head");

            string tempName = $"__switch{_switchCounter}__";
            _switchCounter++;

            var cases = new List<(Expr Condition, List<Stmt> Body)>();
            List<Stmt>? defaultBody = null;

            while (!Check(TokenType.RBrace) && !Check(TokenType.Eof))
            {
                Expect(TokenType.Case, "Expected 'case' in the switch body");

                if (Match(TokenType.Default))
                {
                    if (defaultBody != null)
                        throw Error("Multiple 'case default' in one switch are not allowed", Previous());
                    Expect(TokenType.Colon, "Expected ':' after 'case default'");
                    defaultBody = ParseSwitchCaseBody();
                    continue;
                }

                BinaryOp op = BinaryOp.Eq;
                if (Check(TokenType.Lt) || Check(TokenType.LtEq) || Check(TokenType.Gt) ||
                    Check(TokenType.GtEq) || Check(TokenType.Eq) || Check(TokenType.NotEq))
                {
                    var opTok = Advance();
                    op = opTok.Type switch
                    {
                        TokenType.Lt => BinaryOp.Lt,
                        TokenType.LtEq => BinaryOp.LtEq,
                        TokenType.Gt => BinaryOp.Gt,
                        TokenType.GtEq => BinaryOp.GtEq,
                        TokenType.Eq => BinaryOp.Eq,
                        _ => BinaryOp.NotEq,
                    };
                }
                var valueExpr = ParseSwitchCaseValue();
                Expect(TokenType.Colon, "Expected ':' after the case condition");
                var condition = new BinaryExpr(line, op, new IdentifierExpr(line, tempName), valueExpr);
                cases.Add((condition, ParseSwitchCaseBody()));
            }
            Expect(TokenType.RBrace, "Expected '}' at the end of the switch");

            // If/Else-if-Kette von HINTEN nach VORNE aufbauen - 'case default'
            // (falls vorhanden) wird das innerste 'else', sonst bleibt es null
            // (keiner der Fälle trifft zu -> switch tut einfach nichts).
            Stmt? chain = defaultBody != null ? new Stmt.BlockStmt(_sourceIndex, line, defaultBody) : null;
            for (int i = cases.Count - 1; i >= 0; i--)
                chain = new IfStmt(_sourceIndex, line, cases[i].Condition, new Stmt.BlockStmt(_sourceIndex, line, cases[i].Body), chain);

            var outerStatements = new List<Stmt>
            {
                new VarDeclStmt(_sourceIndex, line, tempName, null, Array.Empty<Expr?>(), subject),
            };
            if (chain != null) outerStatements.Add(chain);

            return new Stmt.BlockStmt(_sourceIndex, line, outerStatements);
        }

        /// <summary>Parst den Wertausdruck einer case-Bedingung mit
        /// unterdrückter ':'-Postfix-Behandlung auf DIESER Klammerungstiefe
        /// (siehe _suppressColonPostfixAtDepth-Doku) - sonst würde z.B.
        /// 'case 2:' das ':' fälschlich als Einheiten-Koersion an den Wert
        /// '2' hängen, statt es dem switch als Zweig-Trenner zu überlassen.</summary>
        private Expr ParseSwitchCaseValue()
        {
            var saved = _suppressColonPostfixAtDepth;
            _suppressColonPostfixAtDepth = _bracketDepth;
            try
            {
                return ParseExpression();
            }
            finally
            {
                _suppressColonPostfixAtDepth = saved;
            }
        }

        /// <summary>Statements EINES case/default-Zweigs - bis zum nächsten
        /// 'case', '}', ODER einem 'break' auf oberster Ebene DIESES Zweigs.
        /// 'break' wird dabei als Zweig-ABSCHLUSS konsumiert (keine eigene
        /// Anweisung, keine echte Sprunganweisung - siehe ParseSwitchStmt-
        /// Doku) und muss deshalb, falls verwendet, die LETZTE Anweisung des
        /// Zweigs sein; alles danach würde nicht mehr zu diesem Zweig gehören.
        /// Ein 'break' INNERHALB einer verschachtelten Schleife/eines
        /// verschachtelten Blocks in diesem Zweig wird davon nicht berührt -
        /// das wird schon beim rekursiven ParseStatement()-Aufruf für die
        /// Schleife/den Block mitkonsumiert, bevor diese Schleife hier
        /// überhaupt wieder zum Zug kommt.</summary>
        private List<Stmt> ParseSwitchCaseBody()
        {
            var body = new List<Stmt>();
            while (!Check(TokenType.RBrace) && !Check(TokenType.Case) && !Check(TokenType.Eof))
            {
                if (Match(TokenType.Break))
                {
                    ExpectStatementTerminator();
                    break;
                }
                body.Add(ParseStatement());
            }
            return body;
        }

        // -----------------------------------------------------------
        // Multithreading (docs/THREADING_DESIGN.md) - erste Ausbaustufe:
        // nur `fire { ... }` / `fire taking X { ... }`.
        // -----------------------------------------------------------

        /// <summary>`fire { ... }` / `fire taking X { ... }` - siehe
        /// Ast.FireStmt-Doku für den Umfang dieser Ausbaustufe (bewusst noch
        /// ohne `fire MethodA()`-Aufrufform, ohne `with actorA`).</summary>
        /// <summary>`fire { ... }` / `fire taking X { ... }` / `fire with actorA { ... }`
        /// / `fire MethodA(args) ...` - siehe Ast.FireStmt-Doku für den vollen
        /// Umfang. Die Aufrufform (Identifier direkt gefolgt von '(') wird
        /// VOR den taking/with-Klauseln erkannt, weil sie selbst KEINE
        /// öffnende '{' danach hat (siehe ParseFireCallForm).</summary>
        private Stmt ParseFireStmt()
        {
            int line = Peek().Line;
            Expect(TokenType.Fire, "Expected 'fire'");

            if (Check(TokenType.Identifier) && PeekAt(1).Type == TokenType.LParen)
                return ParseFireCallForm(line);

            // `fire global { ... }`: Auftrag für das Hauptprogramm statt eines neuen Threads (docs/THREADING_DESIGN.md Abschnitt 7)
            if (Check(TokenType.Identifier) && Peek().Lexeme == "global" && (PeekAt(1).Type == TokenType.LBrace || PeekAt(1).Type == TokenType.Taking))
                return ParseFireGlobal(line);

            var (takingCaptures, withVarName, withSource) = ParseFireTakingWithClauses();
            var body = ParseBlock();
            return new FireStmt(_sourceIndex, line, takingCaptures, withVarName, withSource, body);
        }

        /// <summary>`sync global { ... }` (docs/THREADING_DESIGN.md Abschnitt 7): ein Block, der mit exklusivem Zugriff auf die Globals läuft.
        /// Entzuckert zu `SectionEnter; try { Body } finally { SectionExit }` - die Sektion endet so auch bei `throw` im Block.</summary>
        private Stmt ParseSyncGlobalBlock()
        {
            int line = Peek().Line;
            Expect(TokenType.Sync, "Expected 'sync'");
            Advance(); // 'global'
            var body = ParseBlock();
            var exit = new Stmt.BlockStmt(_sourceIndex, line, new List<Stmt> { new SectionExitStmt(_sourceIndex, line) });
            return new Stmt.BlockStmt(_sourceIndex, line, new List<Stmt>
            {
                new SectionEnterStmt(_sourceIndex, line),
                new TryStmt(_sourceIndex, line, body, new List<CatchClause>(), exit, IsSyncSection: true),
            });
        }

        /// <summary>`fire global { ... } [taking X ...]` - siehe Ast.PostGlobalStmt.</summary>
        private Stmt ParseFireGlobal(int line)
        {
            Advance(); // 'global'
            var (captures, withVarName, _) = ParseFireTakingWithClauses();
            if (withVarName != null)
                throw Error("'with' does not exist for 'fire global'", Peek());
            var body = ParseBlock();
            var parameters = captures.Select(c => new LambdaParam(c.VarName, null, new List<Expr?>(), null)).ToList();
            var lambda = new LambdaExpr(line, parameters, null, body, AutoCapture: false);
            return new PostGlobalStmt(_sourceIndex, line, lambda, captures.Select(c => c.Source).ToList());
        }

        /// <summary>`taking X`/`with actorA`, in beliebiger Reihenfolge, `with`
        /// höchstens einmal, `taking` beliebig oft wiederholbar (siehe
        /// Ast.FireStmt.TakingCaptures-Doku für die Slot-Reihenfolge).</summary>
        private (List<FireTakingCapture> Taking, string? WithVarName, Expr? WithSource) ParseFireTakingWithClauses()
        {
            var takingCaptures = new List<FireTakingCapture>();
            string? withVarName = null;
            Expr? withSource = null;

            while (Check(TokenType.Taking) || Check(TokenType.With))
            {
                if (Check(TokenType.Taking))
                {
                    Advance();
                    var nameTok = Expect(TokenType.Identifier, "Expected an identifier after 'taking'");
                    // Referenziert die BEREITS im umgebenden Scope deklarierte
                    // Variable gleichen Namens - ganz normale Identifier-
                    // Auflösung im AUFRUFENDEN Kontext (nicht im isolierten
                    // Fire-Block-Scope, siehe Resolver.ResolveFireStmt).
                    takingCaptures.Add(new FireTakingCapture(nameTok.Lexeme, new IdentifierExpr(nameTok.Line, nameTok.Lexeme)));
                }
                else
                {
                    if (withVarName != null)
                        throw Error("'with' was already used in this 'fire'", Peek());
                    Advance();
                    var nameTok = Expect(TokenType.Identifier, "Expected an identifier after 'with'");
                    withVarName = nameTok.Lexeme;
                    withSource = new IdentifierExpr(nameTok.Line, withVarName);
                }
            }

            return (takingCaptures, withVarName, withSource);
        }

        /// <summary>`fire MethodA(args) [taking/with-Klauseln]` (siehe
        /// Ast.FireStmt-Doku für die genaue Entzuckerung) - reines
        /// Parser-Sugar: `this` und jedes Argument werden wie zusätzliche
        /// `taking`-Ziele behandelt (unter internen, für Nutzer-Code nicht
        /// schreibbaren Namen, siehe ThisAndArgCaptureNamePrefix), der Body
        /// besteht aus genau einem Aufruf der genommenen Methode auf der
        /// genommenen `this`-Kopie mit den genommenen Argument-Kopien. Kein
        /// eigener Resolver-/Compiler-/VM-Code nötig - der entstehende Knoten
        /// ist für den Rest des Compilers ein ganz normales FireStmt.
        /// Erfordert 'this' im aufrufenden Kontext (nur innerhalb einer
        /// Methode/eines Konstruktors gültig - wird vom Resolver wie jedes
        /// andere 'this' geprüft, keine Sonderprüfung hier nötig).</summary>
        private Stmt ParseFireCallForm(int line)
        {
            string methodName = Advance().Lexeme;
            Expect(TokenType.LParen, "Expected '(' after '" + methodName + "'");
            var callArgs = new List<Expr>();
            if (!Check(TokenType.RParen))
            {
                do { callArgs.Add(ParseExpression()); } while (Match(TokenType.Comma));
            }
            Expect(TokenType.RParen, "Expected ')' after 'fire " + methodName + "(...)'");

            var (explicitTaking, withVarName, withSource) = ParseFireTakingWithClauses();
            ExpectStatementTerminator();

            const string thisCaptureName = "__fire_this__";
            var allCaptures = new List<FireTakingCapture> { new(thisCaptureName, new ThisExpr(line)) };
            var argRefs = new List<Expr>();
            for (int i = 0; i < callArgs.Count; i++)
            {
                string argCaptureName = $"__fire_arg{i}__";
                allCaptures.Add(new FireTakingCapture(argCaptureName, callArgs[i]));
                argRefs.Add(new IdentifierExpr(line, argCaptureName));
            }
            allCaptures.AddRange(explicitTaking);

            var callExpr = new CallExpr(line, new MemberExpr(line, new IdentifierExpr(line, thisCaptureName), methodName), argRefs);
            var body = new Stmt.BlockStmt(_sourceIndex, line, new List<Stmt> { new ExprStmt(_sourceIndex, line, callExpr) });

            return new FireStmt(_sourceIndex, line, allCaptures, withVarName, withSource, body);
        }

        /// <summary>`terminate()` / `terminate(wert)` - siehe Ast.TerminateStmt-
        /// Doku. Syntaktisch wie ein Funktionsaufruf, aber ein eigenes
        /// Statement (kein Ausdruck) - `terminate` hat keinen sinnvollen
        /// "Ergebniswert", der weiterverwendet werden könnte.</summary>
        private Stmt ParseTerminateStmt()
        {
            int line = Peek().Line;
            Expect(TokenType.Terminate, "Expected 'terminate'");
            Expect(TokenType.LParen, "Expected '(' after 'terminate'");
            Expr? value = null;
            if (!Check(TokenType.RParen))
                value = ParseExpression();
            Expect(TokenType.RParen, "Expected ')' after the terminate argument");
            ExpectStatementTerminator();
            return new TerminateStmt(_sourceIndex, line, value);
        }

        /// <summary>`process X` (blockierend, siehe Ast.ProcessStmt-Doku).
        /// Die nicht-blockierende Variante `try process X` ist dagegen ein
        /// AUSDRUCK und wird in ParsePrimary behandelt.</summary>
        private Stmt ParseProcessStmt()
        {
            int line = Peek().Line;
            Expect(TokenType.Process, "Expected 'process'");
            var target = ParsePostfix();
            ExpectStatementTerminator();
            return new ProcessStmt(_sourceIndex, line, target);
        }

        /// <summary>`catch threads(ExceptionType e) { ... }` / `catch threads() { ... }`
        /// / `catch terminate(v) { ... }` (siehe Ast.CatchThreadsDecl/
        /// CatchTerminateDecl-Doku) - GLOBALE Registrierung, nur an
        /// Top-Level-Programmposition erkannt (siehe ParseProgram), damit sie
        /// nicht mit dem bestehenden "catch ohne try erweitert den Block"-
        /// Feature (ParseBlock/ParseImplicitCatchTail) kollidiert - beide
        /// beginnen mit demselben 'catch'-Token, sind aber an dieser Stelle
        /// bereits per Vorausschau (folgt 'threads'/'terminate'?)
        /// unterschieden, bevor überhaupt entschieden wird, welcher der
        /// beiden Wege geparst wird.</summary>
        private Stmt ParseGlobalHandlerDecl()
        {
            int line = Peek().Line;
            Expect(TokenType.Catch, "Expected 'catch'");

            if (Match(TokenType.Terminate))
            {
                Expect(TokenType.LParen, "Expected '(' after 'catch terminate'");
                string? paramName = null;
                if (!Check(TokenType.RParen))
                    paramName = Expect(TokenType.Identifier, "Expected a parameter name in 'catch terminate(...)'").Lexeme;
                Expect(TokenType.RParen, "Expected ')' after 'catch terminate(...)'");
                var terminateBody = ParseBlock();
                return new CatchTerminateDecl(_sourceIndex, line, paramName, terminateBody);
            }

            // 'threads' ist - wie 'get'/'set'/'value' bei Properties - ein rein
            // kontextabhängiger Bezeichner, kein reserviertes Schlüsselwort.
            Expect(TokenType.Identifier, "Expected 'threads' or 'terminate' after 'catch'");
            Expect(TokenType.LParen, "Expected '(' after 'catch threads'");

            TypeRef? typeRef = null;
            string? varName = null;
            if (!Check(TokenType.RParen))
            {
                // 'catch threads(ExceptionType e)' - Typ-dann-Name, wie ein
                // normaler Methodenparameter (und inzwischen auch wie beim
                // normalen 'catch (TypeName varName)' - siehe ParseCatchClause).
                string typeName = Expect(TokenType.Identifier, "Expected a type name in 'catch threads(...)'").Lexeme;
                typeRef = new TypeRef(typeName, null, 0, Namespaces: CurrentNamespaces());
                varName = Expect(TokenType.Identifier, "Expected a parameter name in 'catch threads(...)'").Lexeme;
            }
            Expect(TokenType.RParen, "Expected ')' after 'catch threads(...)'");
            var threadsBody = ParseBlock();
            return new CatchThreadsDecl(_sourceIndex, line, typeRef, varName, threadsBody);
        }

        // -----------------------------------------------------------
        // Klassen
        // -----------------------------------------------------------
        private Stmt ParseClassDecl() => ParseClassOrActorDecl(isActor: false);
        private Stmt ParseActorDecl() => ParseClassOrActorDecl(isActor: true);

        /// <summary>Gemeinsame Grammatik für `class Name { ... }` und
        /// `actor Name { ... }` (siehe Ast.ClassDecl.IsActor-Doku) - beide
        /// teilen sich Felder/Methoden/Konstruktor/Vererbung 1:1, nur das
        /// `IsActor`-Flag am Ergebnis unterscheidet sie. `extends` (Klassen-
        /// Erweiterung, siehe ParseClassExtensionDecl/MergeClassExtensions)
        /// funktioniert für BEIDE identisch - `class extends AktorName { ... }`
        /// UND `actor extends AktorName { ... }` erweitern dieselbe
        /// Zielklasse gleichermaßen, da die Erweiterung rein nach NAMEN
        /// zusammengeführt wird und das `IsActor`-Flag der bereits
        /// bestehenden, ORIGINALEN Deklaration unangetastet lässt - welches
        /// Schlüsselwort man bei der Erweiterung selbst schreibt, spielt
        /// deshalb keine Rolle.</summary>
        private Stmt ParseClassOrActorDecl(bool isActor)
        {
            int line = Peek().Line;
            if (isActor) Expect(TokenType.Actor, "Expected 'actor'");
            else Expect(TokenType.Class, "Expected 'class'");

            if (Check(TokenType.Extends))
                return ParseClassExtensionDecl(line);

            string name = Expect(TokenType.Identifier, "Expected a class name").Lexeme;

            // Generische Typ-Parameter: 'class Name<T1, T2>' - siehe
            // ParseTypeParamList. Leer (kein '<' vorhanden) für eine
            // nicht-generische Klasse.
            var typeParamNames = ParseOptionalTypeParamNames();

            // Rohe Namensliste nach ':' - welcher Name (max. einer) die
            // Basisklasse ist und welche Interfaces sind, entscheidet der
            // Resolver (der Parser kennt die Klassen-/Interface-Tabelle noch
            // nicht). `class Foo : Bar, IBaz, IQux` oder in beliebiger
            // Reihenfolge, solange höchstens ein Name eine echte Klasse ist.
            // Jeder Name trägt (wie jeder TypeRef) den HIER, beim Parsen des
            // Klassenkopfs aktuellen Namespace-Kontext (SPEC "Namespaces").
            var namespaces = CurrentNamespaces();
            var baseRefs = new List<TypeRef>();
            if (Match(TokenType.Colon))
            {
                do
                {
                    // Auch qualifiziert ('Geometry.Shape' - eine Basisklasse in einem
                    // anderen Namespace, siehe SPEC "Namespaces").
                    string baseName = ParseDottedName("base class/interface names");
                    // `class Home : Command<IDevice>`: die Typ-Argumente werden (wie überall) nicht ausgewertet, nur ihre ANZAHL wählt die generische
                    // Klasse bzw. das generische Interface dieses Namens (siehe GenericClassNames.ResolveNewTarget).
                    int typeArgCount = ParseOptionalTypeParamNames().Count;
                    baseRefs.Add(new TypeRef(baseName, null, 0, Namespaces: namespaces, TypeArgCount: typeArgCount));
                } while (Match(TokenType.Comma));
            }

            // 'where'-Klauseln: EINE pro Typ-Parameter (C#-artig), in
            // beliebiger Reihenfolge, alle VOR der öffnenden '{'. Siehe
            // ParseWhereClause für die Constraint-Grammatik selbst.
            var typeParams = ParseWhereClauses(typeParamNames, line);

            Expect(TokenType.LBrace, "Expected '{' after the class head");
            var members = new List<Stmt>();
            // Für statische Auto-Properties (siehe ParsePropertyBody) - das
            // synthetisierte Backing-Field ist dort ein Zugriff über
            // 'ClassName.feld' statt 'this.feld' (keine Instanz gebunden),
            // braucht also den QUALIFIZIERTEN Klassennamen, GENAU wie er
            // gleich unten in ClassDecl selbst landet. Gespeichert/
            // wiederhergestellt statt direkt zugewiesen, falls eine Klasse
            // jemals verschachtelt vorkäme (aktuell nicht möglich, aber
            // robust für den Fall).
            string? savedClassName = _currentClassName;
            bool savedClassIsGeneric = _currentClassIsGeneric;
            _currentClassName = QualifyDeclName(name);
            _currentClassIsGeneric = typeParamNames.Count > 0;
            try
            {
                while (!Check(TokenType.RBrace) && !Check(TokenType.Eof))
                    members.AddRange(ParseClassMember());
            }
            finally
            {
                _currentClassName = savedClassName;
                _currentClassIsGeneric = savedClassIsGeneric;
            }
            Expect(TokenType.RBrace, "Expected '}' at the end of the class");

            return new ClassDecl(_sourceIndex, line, QualifyDeclName(name), baseRefs, members, typeParams, IsActor: isActor);
        }

        /// <summary>`&lt;T1, T2, ...&gt;` direkt nach einem Klassen-/Methodennamen -
        /// liefert nur die reinen NAMEN (leere Liste, falls kein '&lt;'
        /// vorhanden), die Constraints selbst kommen separat über
        /// 'where'-Klauseln (siehe ParseWhereClauses). Getrennt von den
        /// Constraints geparst, weil 'class Name&lt;T&gt; : Base' die Basisklasse
        /// dazwischen braucht (C#-artige Reihenfolge: Typ-Parameter, dann
        /// Basisklasse(n), dann 'where'-Klauseln, dann Body).</summary>
        private List<string> ParseOptionalTypeParamNames()
        {
            var names = new List<string>();
            if (!Match(TokenType.Lt)) return names;
            do
            {
                // ParseTypeAnnotationName() statt Expect(Identifier) - ein
                // Typ-ARGUMENT (bei 'new Name<Arg>') kann ein primitiver Typ
                // wie 'int'/'float' sein, der als EIGENES Keyword-Token
                // gelexed wird, kein TokenType.Identifier (siehe
                // ParseTypeAnnotationName-Doku an anderer Stelle). Für
                // Typ-PARAMETER-Namen (bei der Deklaration, immer normale
                // Bezeichner wie 'T') ist das ein Aufruf ohne Unterschied.
                names.Add(ParseTypeAnnotationName());
                // Verschachtelte Typ-ARGUMENTE ('Box<int>' als EIN Argument
                // von z.B. 'new Container<Box<int>>()') rein SYNTAKTISCH
                // überspringen, OHNE sie strukturell abzubilden - es gibt
                // keine echte generische Spezialisierung (SPEC 5.8), die
                // Constraint-Prüfung (CheckTypeArgs) arbeitet ohnehin nur
                // mit dem ÄUSSEREN Namen ('Box', nicht 'Box<int>'), das
                // Wegwerfen der inneren Argumente ist deshalb unschädlich.
                // Bei einer Typ-PARAMETER-Deklaration ('class Box<T>') ist
                // dieser Aufruf ein reines No-op (T hat nie ein '<' danach).
                SkipOptionalNestedTypeArgs();
            } while (Match(TokenType.Comma));
            Expect(TokenType.Gt, "Expected '>' after the type parameter list");
            return names;
        }

        /// <summary>Siehe ParseOptionalTypeParamNames - konsumiert eine
        /// optionale, beliebig tief verschachtelte `&lt;...&gt;`-Typ-Argumentliste
        /// (`Box&lt;Box&lt;int&gt;&gt;`, `Box&lt;A, B&lt;C&gt;&gt;`, ...) rein syntaktisch, wirft
        /// den Inhalt komplett weg. Rekursiv für beliebige Verschachtelungs-
        /// tiefe. Jedes '&gt;' wird einzeln über Expect(Gt) gelesen (NIE über
        /// die Ausdrucks-Präzedenzkette/ParseShift) - deshalb kein Konflikt
        /// mit `&gt;&gt;` als Schiebeoperator: der Lexer liefert für `&lt;`/`&gt;`
        /// ohnehin immer nur Einzelzeichen-Tokens (außer `&lt;=`/`&gt;=`), ein
        /// `Box&lt;Box&lt;int&gt;&gt;` endet also mit zwei EINZELNEN `&gt;`-Tokens, die
        /// hier ganz normal nacheinander (einmal pro Verschachtelungsebene)
        /// konsumiert werden.</summary>
        private void SkipOptionalNestedTypeArgs()
        {
            if (!Match(TokenType.Lt)) return;
            do
            {
                ParseTypeAnnotationName();
                SkipOptionalNestedTypeArgs();
            } while (Match(TokenType.Comma));
            Expect(TokenType.Gt, "Expected '>' after the nested type argument list");
        }

        /// <summary>Null oder mehr `where Name constraint-group (',' constraint-group)*`
        /// -Klauseln, EINE pro Typ-Parameter - jede referenziert einen der in
        /// `typeParamNames` deklarierten Namen (Fehler bei unbekanntem/
        /// doppeltem Namen). Liefert für JEDEN deklarierten Typ-Parameter
        /// einen TypeParam-Eintrag, auch wenn er KEIN 'where' hat (dann mit
        /// leeren ConstraintGroups - uneingeschränkt). Reine Deklarations-
        /// Syntax; ob überhaupt Typ-Parameter vorhanden sind, entscheidet
        /// `typeParamNames` (leer -> auch kein 'where' erlaubt).</summary>
        private List<TypeParam> ParseWhereClauses(List<string> typeParamNames, int declLine)
        {
            var constraintsByName = new Dictionary<string, List<TypeConstraintGroup>>();

            while (Match(TokenType.Where))
            {
                string tpName = Expect(TokenType.Identifier, "Expected a type parameter name after 'where'").Lexeme;
                if (!typeParamNames.Contains(tpName))
                    throw Error(
                        $"'where {tpName}' does not refer to a declared type parameter " +
                        $"(declared: {(typeParamNames.Count == 0 ? "none" : string.Join(", ", typeParamNames))})",
                        Previous());
                if (constraintsByName.ContainsKey(tpName))
                    throw Error($"Multiple 'where {tpName}' clauses for the same type parameter", Previous());

                var groups = new List<TypeConstraintGroup>();
                do
                {
                    var constraintsInGroup = new List<TypeConstraint>();
                    do
                    {
                        constraintsInGroup.Add(ParseOneTypeConstraint());
                    } while (Match(TokenType.Colon));
                    groups.Add(new TypeConstraintGroup(constraintsInGroup));
                } while (Match(TokenType.Comma));

                constraintsByName[tpName] = groups;
            }

            return typeParamNames
                .Select(n => new TypeParam(n, constraintsByName.TryGetValue(n, out var g) ? g : Array.Empty<TypeConstraintGroup>()))
                .ToList();
        }

        /// <summary>Eine einzelne Bedingung: `is of Name` oder `is in "unitName"`
        /// (Einheitenname als String-Literal, wie im Beispiel `is in "mm"` -
        /// nicht als Bezeichner, da Einheitennamen wie `mm`/`kg` sonst mit
        /// Typnamen kollidieren könnten).</summary>
        private TypeConstraint ParseOneTypeConstraint()
        {
            Expect(TokenType.Is, "Expected 'is' in a where condition");
            if (Match(TokenType.Of))
            {
                // ParseTypeAnnotationName() statt Expect(Identifier) - das
                // Ziel von 'is of' kann ein primitiver Typ wie 'float' sein,
                // der als EIGENES Keyword-Token gelexed wird (siehe
                // ParseOptionalTypeParamNames-Kommentar für dieselbe Falle).
                string typeName = ParseTypeAnnotationName();
                return new TypeConstraint(TypeConstraintKind.IsOf, typeName);
            }
            if (Match(TokenType.In))
            {
                var tok = Expect(TokenType.StringLiteral, "Expected a unit name (as a string) after 'is in'");
                return new TypeConstraint(TypeConstraintKind.IsIn, (string)tok.LiteralValue!);
            }
            throw Error("Expected 'of' or 'in' after 'is' in a where condition", Peek());
        }

        /// <summary>`class extends Name { neue Mitglieder... }` - siehe
        /// Ast.ClassExtensionDecl-Doku. Members werden mit demselben
        /// ParseClassMember() geparst wie in einer normalen Klasse (Felder,
        /// Methoden, Properties, sogar ein weiterer Konstruktor/Destruktor -
        /// ob das beim Zusammenführen sinnvoll ist, prüft erst der Merge-
        /// Schritt, siehe Parser.MergeClassExtensions).</summary>
        private Stmt ParseClassExtensionDecl(int line)
        {
            Expect(TokenType.Extends, "Expected 'extends'");

            // Ein Basistyp (`string`, `char`, ...) ist ein Schlüsselwort, kein Bezeichner - die
            // Erweiterung eines Basistyps (SPEC 5.5.1) darf NUR Methoden enthalten.
            bool isBaseType = TypeKeywords.Contains(Peek().Type) && Peek().Type != TokenType.Class && Peek().Type != TokenType.Undefined;
            string targetName = isBaseType
                ? Advance().Lexeme
                : Expect(TokenType.Identifier, "Expected the name of the class to extend").Lexeme;
            if (isBaseType && !BaseTypeExtensions.IsExtendable(targetName))
                throw Error(targetName == "byte"
                    ? "'byte' cannot be extended - a byte is an int at run time, extend 'int'"
                    : $"'{targetName}' cannot be extended", Previous());
            var targetRef = new TypeRef(targetName, null, 0, Namespaces: CurrentNamespaces());
            Expect(TokenType.LBrace, "Expected '{' after 'class extends " + targetName + "'");

            var members = new List<Stmt>();
            while (!Check(TokenType.RBrace) && !Check(TokenType.Eof))
                members.AddRange(ParseClassMember());
            Expect(TokenType.RBrace, "Expected '}' at the end of the extension");

            if (isBaseType || BaseTypeExtensions.IsExtendable(targetName))
                foreach (var member in members)
                    ValidateBaseTypeExtensionMember(targetName, member);

            return new ClassExtensionDecl(_sourceIndex, line, targetRef, members);
        }

        /// <summary>Eine Erweiterung eines Basistyps (`class extends string { ... }`) darf nur
        /// gewöhnliche Instanzmethoden enthalten: ein Basiswert hat keinen Speicher für Felder/Properties,
        /// keinen Konstruktor/Destruktor und (VM.BinaryNumericOrOperator prüft nur Objekte) keine
        /// Operator-Überladung; `static` hätte keinen Aufrufweg (`string.Foo()` gibt es nicht).</summary>
        private static void ValidateBaseTypeExtensionMember(string typeName, Stmt member)
        {
            string prefix = $"'class extends {typeName}': ";
            switch (member)
            {
                case MethodDecl { IsStatic: true } m:
                    throw new ParseException(prefix + $"static method '{m.Name}' is not allowed - extensions of base types consist of instance methods only.", m.Line, 1);
                case MethodDecl m when m.Name.StartsWith("operator", StringComparison.Ordinal) || m.Name is "GetIndex" or "SetIndex":
                    throw new ParseException(prefix + "Operators cannot be overloaded for base types.", m.Line, 1);
                case MethodDecl:
                    return;
                case FieldDecl f:
                    throw new ParseException(prefix + $"field '{f.Name}' is not allowed - extensions of base types may only contain methods.", f.Line, 1);
                case PropertyDecl p:
                    throw new ParseException(prefix + $"property '{p.Name}' is not allowed - extensions of base types may only contain methods.", p.Line, 1);
                case ConstructorDecl c:
                    throw new ParseException(prefix + "a constructor is not allowed - extensions of base types may only contain methods.", c.Line, 1);
                case DestructorDecl d:
                    throw new ParseException(prefix + "a destructor is not allowed - extensions of base types may only contain methods.", d.Line, 1);
                default:
                    throw new ParseException(prefix + "only methods are allowed.", member.Line, 1);
            }
        }

        /// <summary>`namespace Name { Mitglieder... }` bzw. `namespace A.B { ... }`
        /// - siehe Ast.NamespaceDecl-Doku. Mitglieder werden mit dem normalen
        /// ParseStatement() geparst (Klassen/Interfaces/Enums, auch
        /// verschachtelte weitere `namespace`-Blöcke), WÄHREND `_currentNamespace`
        /// auf diesen (ggf. verschachtelten) Namespace zeigt - jede Deklaration/
        /// jeder TypeRef darin qualifiziert/verknüpft sich dadurch schon beim
        /// Parsen selbst korrekt (siehe QualifyDeclName/CurrentNamespaces).
        /// Der alte Namespace-Name wird beim Verlassen des Blocks IMMER
        /// zurückgeschrieben (auch wenn er `null` war), damit verschachtelte
        /// UND aufeinanderfolgende `namespace`-Blöcke sich nicht gegenseitig
        /// beeinflussen.</summary>
        private Stmt ParseNamespaceDecl()
        {
            int line = Peek().Line;
            Expect(TokenType.Namespace, "Expected 'namespace'");
            string name = ParseDottedName("Namespace-Namen");
            Expect(TokenType.LBrace, "Expected '{' after the namespace name");

            string? savedNamespace = _currentNamespace;
            _currentNamespace = _currentNamespace == null ? name : _currentNamespace + "." + name;

            var members = new List<Stmt>();
            while (!Check(TokenType.RBrace) && !Check(TokenType.Eof))
                members.Add(ParseStatement());
            Expect(TokenType.RBrace, "Expected '}' at the end of the namespace");

            _currentNamespace = savedNamespace;

            return new NamespaceDecl(_sourceIndex, line, name, members);
        }

        /// <summary>Liest einen (möglicherweise mehrteiligen, per '.' getrennten)
        /// Namen wie 'A' oder 'A.B.C' - für Namespace-Namen (Deklaration UND
        /// `#using`) an genau den beiden Stellen genutzt, wo diese Sprache
        /// sonst nirgendwo einen '.' als Teil eines NAMENS selbst erlaubt
        /// (überall sonst ist '.' der Elementzugriffs-Operator).</summary>
        private string ParseDottedName(string what)
        {
            string name = Expect(TokenType.Identifier, $"Expected {what}").Lexeme;
            while (Match(TokenType.Dot))
                name += "." + Expect(TokenType.Identifier, $"Expected {what} after '.'").Lexeme;
            return name;
        }

        /// <summary>`interface Name { [ReturnType] Method(params) ... }` - reine
        /// Methodensignaturen, keine Felder/Konstruktor/Bodies.</summary>
        private Stmt ParseInterfaceDecl()
        {
            int line = Peek().Line;
            Expect(TokenType.Interface, "Expected 'interface'");
            string name = Expect(TokenType.Identifier, "Expected an interface name").Lexeme;
            // `interface ICommand<T> { ... }`: generisch wie eine Klasse (siehe ParseClassOrActorDecl); wie dort zählt nur Name und Anzahl der Typ-Parameter
            var typeParamNames = ParseOptionalTypeParamNames();
            var typeParams = typeParamNames.Count > 0 ? ParseWhereClauses(typeParamNames, line) : null;
            Expect(TokenType.LBrace, "Expected '{' after the interface head");

            var methods = new List<InterfaceMethodSig>();
            while (!Check(TokenType.RBrace) && !Check(TokenType.Eof))
            {
                int mLine = Peek().Line;
                TypeRef? returnType = null;
                if (NextLooksLikeTypeThenName())
                    returnType = ParseTypeRef(allowArray: true);
                string methodName = Expect(TokenType.Identifier, "Expected a method name").Lexeme;
                var parms = ParseParamList(allowRef: true);
                ExpectStatementTerminator();
                methods.Add(new InterfaceMethodSig(mLine, returnType, methodName, parms));
            }
            Expect(TokenType.RBrace, "Expected '}' at the end of the interface");

            return new InterfaceDecl(_sourceIndex, line, QualifyDeclName(name), methods, typeParams);
        }

        /// <summary>`enum Name { A, B = 5, C }` - siehe Ast.EnumDecl-Doku für die
        /// Semantik (reine Compile-Zeit-Konstanten). Werte werden hier nur
        /// GEPARST (roher Ausdruck oder fehlend) - die eigentliche Berechnung
        /// (Auto-Increment, Validierung "muss Int-Literal sein") passiert erst
        /// im Resolver, der dafür alle Mitglieder in Deklarationsreihenfolge
        /// braucht.</summary>
        private Stmt ParseEnumDecl()
        {
            int line = Peek().Line;
            Expect(TokenType.Enum, "Expected 'enum'");
            string name = Expect(TokenType.Identifier, "Expected an enum name").Lexeme;
            Expect(TokenType.LBrace, "Expected '{' after the enum name");

            var members = new List<EnumMember>();
            if (!Check(TokenType.RBrace))
            {
                do
                {
                    string memberName = Expect(TokenType.Identifier, "Expected an enum member name").Lexeme;
                    Expr? valueExpr = null;
                    if (Match(TokenType.Assign))
                        valueExpr = ParseExpression();
                    members.Add(new EnumMember(memberName, valueExpr));
                } while (Match(TokenType.Comma));
            }

            Expect(TokenType.RBrace, "Expected '}' after the enum members");
            return new EnumDecl(_sourceIndex, line, QualifyDeclName(name), members);
        }

        /// <summary>`Type Name { get { ... } set { ... } }` - siehe
        /// Ast.PropertyDecl-Doku. 'get'/'set' sind bewusst KEINE reservierten
        /// Schlüsselwörter (wie 'value' im Setter-Body auch nicht) - reine
        /// kontextabhängige Bezeichner, nur innerhalb eines Property-Bodies
        /// mit Sonderbedeutung, genau wie in C#. Reihenfolge/Anzahl: 'get'
        /// und 'set' dürfen in beliebiger Reihenfolge stehen, jedes höchstens
        /// einmal, mindestens eines von beiden muss vorhanden sein.</summary>
        /// <summary>Property-Body: `{ get ... set ... }` mit explizitem Body je
        /// Accessor (`get { ... }`/`set { ... }`, wie bisher) ODER als
        /// Auto-Property-Kurzform (`get;`/`set;`, ohne eigenen Body) - siehe
        /// SPEC "Auto-Properties". Mischen ist erlaubt (z.B. `get { ... }
        /// set;`). Mindestens EIN Accessor (explizit oder Auto) ist Pflicht.
        /// Liefert eine LISTE von Members (nicht nur die PropertyDecl selbst),
        /// da eine Auto-Property zusätzlich ein synthetisches Backing-Field
        /// braucht (siehe unten) - der Aufrufer (ParseClassMember) hängt
        /// beides an die Mitgliederliste der Klasse an.</summary>
        private List<Stmt> ParsePropertyBody(int line, TypeRef? type, string name, AccessModifier access, bool isStatic = false)
        {
            Expect(TokenType.LBrace, "Expected '{' after the property name");

            Stmt.BlockStmt? getter = null;
            Stmt.BlockStmt? setter = null;
            bool getterIsAuto = false;
            bool setterIsAuto = false;

            while (!Check(TokenType.RBrace) && !Check(TokenType.Eof))
            {
                if (Check(TokenType.Identifier) && Peek().Lexeme == "get")
                {
                    if (getter != null || getterIsAuto)
                        throw Error($"'get' is already defined for property '{name}'", Peek());
                    Advance();
                    if (Check(TokenType.LBrace))
                        getter = ParseBlock();
                    else
                    {
                        ExpectStatementTerminator();
                        getterIsAuto = true;
                    }
                }
                else if (Check(TokenType.Identifier) && Peek().Lexeme == "set")
                {
                    if (setter != null || setterIsAuto)
                        throw Error($"'set' is already defined for property '{name}'", Peek());
                    Advance();
                    if (Check(TokenType.LBrace))
                        setter = ParseBlock();
                    else
                    {
                        ExpectStatementTerminator();
                        setterIsAuto = true;
                    }
                }
                else
                {
                    throw Error("Expected 'get' or 'set' in the property body", Peek());
                }
            }

            Expect(TokenType.RBrace, "Expected '}' at the end of the property");

            if (getter == null && !getterIsAuto && setter == null && !setterIsAuto)
                throw Error($"Property '{name}' needs at least 'get' or 'set'", Peek());

            var result = new List<Stmt>();

            if (getterIsAuto || setterIsAuto)
            {
                // Auto-Property: mindestens ein Accessor ist reines 'get;'/
                // 'set;' ohne eigenen Body - synthetisiert ein Backing-Field
                // mit vorhersagbarem Namen ('_AutoName', siehe SPEC-Doku für die
                // Namenskonvention und ihre Grenzen: Kollision mit einem
                // gleichnamigen, vom Nutzer selbst deklarierten Feld ist
                // theoretisch möglich, wird hier bewusst nicht extra
                // geprüft) sowie triviale get_/set_-Bodies dafür. Das
                // Backing-Field ist ein GANZ NORMALES Feld (kein
                // Sonderstatus) - Code INNERHALB der Klasse (Konstruktor
                // eingeschlossen) kann jederzeit direkt darauf zugreifen
                // ('this._AutoName'), z.B. um ein get-only Property trotzdem im
                // Konstruktor zu initialisieren (dafür gibt es ja keinen
                // Setter über die Property selbst).
                // Backing-Field ist IMMER private, unabhängig vom Modifikator
                // der Property selbst - reines Implementierungsdetail, das
                // nur über die Property (get_Name/set_Name) erreichbar sein
                // soll, niemals direkt von außen ('this._AutoName' bleibt
                // INNERHALB der Klasse weiterhin normal erlaubt).
                string backingName = "_Auto" + name;
                result.Add(new FieldDecl(_sourceIndex, line, type, Array.Empty<Expr?>(), backingName, null, IsReadonly: false,
                    Access: AccessModifier.Private, IsStatic: isStatic));

                // Statisch: Backing-Field über 'ClassName.feld' statt
                // 'this.feld' (keine Instanz gebunden, siehe
                // ParseClassOrActorDecl für _currentClassName) - beides läuft
                // über denselben MemberExpr-Knoten, nur das Ziel
                // unterscheidet sich (ThisExpr vs. ein Bezeichner mit dem
                // Klassennamen, den der Resolver als statischen Zugriff
                // erkennt, siehe Resolver.TryResolveStaticMemberAccess).
                //
                // In einer GENERISCHEN Klasse stattdessen SelfClassExpr: der
                // Klassenname allein könnte dort auf eine gleichnamige
                // NICHT-generische Klasse zeigen (siehe GenericClassNames).
                Expr backingTarget = !isStatic
                    ? new ThisExpr(line)
                    : _currentClassIsGeneric
                        ? new SelfClassExpr(line)
                        : new IdentifierExpr(line, _currentClassName ?? name);

                if (getterIsAuto)
                    getter = new Stmt.BlockStmt(_sourceIndex, line, new List<Stmt>
                    {
                        new ReturnStmt(_sourceIndex, line, new MemberExpr(line, backingTarget, backingName)),
                    });

                if (setterIsAuto)
                    setter = new Stmt.BlockStmt(_sourceIndex, line, new List<Stmt>
                    {
                        new ExprStmt(_sourceIndex, line, new AssignExpr(line,
                            new MemberExpr(line, backingTarget, backingName),
                            new IdentifierExpr(line, "value"))),
                    });
            }

            result.Add(new PropertyDecl(_sourceIndex, line, type, name, getter, setter, access, isStatic));
            return result;
        }

        /// <summary>Liest einen optionalen Zugriffsmodifikator (`public`/
        /// `private`/`protected`) direkt vor einem Klassenmitglied - Default
        /// `Public`, wenn keiner angegeben wurde (SPEC "Zugriffsmodifikatoren",
        /// siehe AccessModifier-Doku). Höchstens EINER erlaubt (kein
        /// `public private ...`) - ein zweiter Modifikator würde einfach als
        /// nächstes Token (Typname/Methodenname) fehlschlagen, kein
        /// gesonderter Fehlerfall nötig.</summary>
        private AccessModifier ParseOptionalAccessModifier()
        {
            if (Match(TokenType.Public)) return AccessModifier.Public;
            if (Match(TokenType.Private)) return AccessModifier.Private;
            if (Match(TokenType.Protected)) return AccessModifier.Protected;
            return AccessModifier.Public;
        }

        private List<Stmt> ParseClassMember()
        {
            int line = Peek().Line;
            var access = ParseOptionalAccessModifier();

            if (Check(TokenType.Construct))
                return new List<Stmt> { ParseConstructor(access) };
            if (Check(TokenType.Destruct))
                return new List<Stmt> { ParseDestructor() };
            if (Check(TokenType.Operator))
                return new List<Stmt> { ParseOperatorMember(line) };

            // 'static' bei Feldern/Methoden/Properties (SPEC "Statische
            // Mitglieder") - EINE geteilte Speicherstelle pro Klasse statt
            // pro Instanz, aufrufbar als 'ClassName.Member' statt
            // 'instanz.Member' (siehe Resolver/VM.GetStaticField etc.).
            // Reihenfolge fest [access] [static] [readonly] - üblichste
            // Schreibweise ('public static readonly'), keine anderen
            // Reihenfolgen extra unterstützt (Einfachheit).
            bool isStatic = Match(TokenType.Static);
            bool isReadonly = Match(TokenType.Readonly);

            // SPEC "Einheiten-Deklarationen": `var` ist wie bei lokalen
            // Variablen/Parametern gültig - `var` allein (Typ + evtl. Einheit
            // aus Initialisierer/':' hergeleitet) ODER ein expliziter Typ.
            TypeRef? type = null;
            if (Check(TokenType.Var))
            {
                Advance();
                type = new TypeRef(TypeRef.InferredMarker, null, 0, Namespaces: CurrentNamespaces());
            }
            else if (NextLooksLikeTypeThenName())
            {
                // `int[] Name()`: ein Array-Rückgabetyp (Methode/Property) - bei einem FELD fängt das der
                // Check unten ab (dort stehen die Klammern hinter dem Namen).
                type = ParseTypeRef(allowArray: true);
            }

            string name = Expect(TokenType.Identifier, "Expected a field or method name").Lexeme;

            // Generische Methode: 'Name<T>(...) where T constraint { ... }' -
            // ein '<' direkt nach dem Namen ist hier unzweideutig NUR als
            // Typ-Parameterliste gültig (ein Feld könnte an dieser Stelle nie
            // sinnvoll ein '<' haben - siehe FieldDecl-Pfad unten, der nur
            // '[' (Array) oder '=' erwartet), daher hier ohne Rückschau-Risiko
            // geparst. Nur bei EXPLIZITER Verwendung: keine Auswirkung auf
            // normale, nicht-generische Methoden.
            var methodTypeParamNames = Check(TokenType.Lt) ? ParseOptionalTypeParamNames() : new List<string>();

            if (Check(TokenType.LParen))
            {
                if (isReadonly)
                    throw Error("'readonly' is only valid for fields, not for methods", Peek());
                var parms = ParseParamList(allowRef: true);
                var methodTypeParams = ParseWhereClauses(methodTypeParamNames, line);
                var body = ParseBlock();
                return new List<Stmt> { new MethodDecl(_sourceIndex, line, type, name, parms, body,
                    methodTypeParamNames.Count > 0 ? methodTypeParams : null, access, isStatic) };
            }

            if (methodTypeParamNames.Count > 0)
                throw Error("A generic type parameter head '<...>' must be followed by a parameter list '(...)'", Peek());

            if (Check(TokenType.LBrace))
            {
                if (isReadonly)
                    throw Error(
                        "'readonly' is not valid for properties - a property without 'set' is already read-only",
                        Peek());
                return ParsePropertyBody(line, type, name, access, isStatic);
            }

            if (type is { ArrayRank: > 0 })
                throw Error(
                    "An array field is written with the brackets after the name ('int values[]'); " +
                    "'int[]' as a type only exists as the return type of a method or property", Previous());

            var arrayRanks = ParseArrayRanks();

            // Wie bei var-/Parameter-Deklarationen: ':' legt IMMER nur eine
            // Einheit fest, nie einen Typ (SPEC "Einheiten-Deklarationen").
            // Wie bei Parametern: fehlt ein expliziter Typ/`var` davor, aber
            // eine Einheit steht da, gilt das implizit wie `var`.
            if (Match(TokenType.Colon))
            {
                string unitName = ParseUnitName();
                type = type != null
                    ? type with { Unit = unitName }
                    : new TypeRef(TypeRef.InferredMarker, null, 0, Namespaces: CurrentNamespaces(), Unit: unitName);
            }

            Expr? initializer = null;
            if (Match(TokenType.Assign))
                initializer = ParseExpression();
            ExpectStatementTerminator();
            return new List<Stmt> { new FieldDecl(_sourceIndex, line, type, arrayRanks, name, initializer, isReadonly, access, isStatic) };
        }

        /// <summary>`operator SYMBOL(params) { body }` - Operator-Überladung
        /// (siehe RuntimeClass-Konvention: intern ganz normale Methoden mit
        /// einem speziellen, für Skript-Code selbst nicht direkt aufrufbaren
        /// Namen, siehe ParseOperatorSymbol). `operator[]` ist dabei reines
        /// Parser-Sugar für die BEREITS BESTEHENDE `GetIndex`/`SetIndex`-
        /// Namenskonvention (siehe VM.OpCode.ArrayGet/ArraySet) - unterschieden
        /// rein über die Parameteranzahl (1 = lesend, 2 = schreibend: Index,
        /// Wert), exakt wie jede andere Methodenüberladung nach Arity in
        /// dieser Sprache. Alle anderen Operatoren (`+`/`-`/etc.) erwarten
        /// GENAU EINEN Parameter (der rechte Operand - `this` ist implizit
        /// der linke) und werden von den jeweiligen VM-Opcode-Handlern
        /// (BinaryNumericOrOperator) aufgerufen, wenn der LINKE Operand ein
        /// Objekt mit passender Methode ist - eine Überladung nur für den
        /// RECHTEN Operanden (wie C#s `operator+` bei vertauschten Operanden-
        /// Typen, oder Pythons `__radd__`) ist bewusst NICHT unterstützt.</summary>
        private Stmt ParseOperatorMember(int line)
        {
            Expect(TokenType.Operator, "Expected 'operator'");
            string symbol = ParseOperatorSymbol();
            var parms = ParseParamList();

            string internalName = symbol == "[]"
                ? parms.Count switch
                {
                    1 => "GetIndex",
                    2 => "SetIndex",
                    _ => throw Error(
                        "'operator[]' needs either 1 parameter (read access: index) " +
                        "or 2 parameters (write access: index, value)", Peek()),
                }
                : "operator" + symbol;

            if (symbol != "[]" && parms.Count != 1)
                throw Error($"'operator{symbol}' needs exactly 1 parameter (the right operand - 'this' is the left one)", Peek());

            var body = ParseBlock();
            return new MethodDecl(_sourceIndex, line, null, internalName, parms, body, null);
        }

        /// <summary>Liest ein einzelnes, überladbares Operator-Symbol direkt
        /// nach 'operator'. `<<`/`>>` wie bei ParseShift: zwei aufeinander-
        /// folgende Einzelzeichen-Tokens (der Lexer kennt sie nicht als
        /// eigene Zwei-Zeichen-Tokens, siehe dortige Doku) - hier VOR den
        /// einzelnen `<`/`>`-Fällen geprüft, sonst würde `operator<<` fälschlich
        /// schon nach dem ERSTEN `<` als `operator<` gelesen.</summary>
        private string ParseOperatorSymbol()
        {
            if (Match(TokenType.LBracket))
            {
                Expect(TokenType.RBracket, "Expected ']' after 'operator['");
                return "[]";
            }
            if (Check(TokenType.Lt) && PeekAt(1).Type == TokenType.Lt) { Advance(); Advance(); return "<<"; }
            if (Check(TokenType.Gt) && PeekAt(1).Type == TokenType.Gt) { Advance(); Advance(); return ">>"; }
            if (Match(TokenType.Plus)) return "+";
            if (Match(TokenType.Minus)) return "-";
            if (Match(TokenType.Star)) return "*";
            if (Match(TokenType.Slash)) return "/";
            if (Match(TokenType.Percent)) return "%";
            if (Match(TokenType.Caret)) return "^";
            if (Match(TokenType.Amp)) return "&";
            if (Match(TokenType.Pipe)) return "|";
            if (Match(TokenType.Hash)) return "#";
            if (Match(TokenType.Eq)) return "==";
            if (Match(TokenType.NotEq)) return "!=";
            if (Match(TokenType.LtEq)) return "<=";
            if (Match(TokenType.GtEq)) return ">=";
            if (Match(TokenType.Lt)) return "<";
            if (Match(TokenType.Gt)) return ">";
            throw Error(
                "Expected an overloadable operator after 'operator' " +
                "('[]', '+', '-', '*', '/', '%', '^', '&', '|', '#', '<<', '>>', " +
                "'==', '!=', '<', '<=', '>', '>=')", Peek());
        }

        private Stmt ParseConstructor(AccessModifier access)
        {
            int line = Peek().Line;
            Expect(TokenType.Construct, "Expected 'construct'");
            var parms = ParseParamList(allowRef: true);

            IReadOnlyList<Expr>? baseArgs = null;
            if (Match(TokenType.Colon))
            {
                Expect(TokenType.Base, "Expected 'base' after ':' in the constructor");
                baseArgs = ParseArgList();
            }

            var body = ParseBlock();
            return new ConstructorDecl(_sourceIndex, line, parms, baseArgs, body, access);
        }

        private Stmt ParseDestructor()
        {
            int line = Peek().Line;
            Expect(TokenType.Destruct, "Expected 'destruct'");
            Expect(TokenType.LParen, "Expected '(' after 'destruct'");
            Expect(TokenType.RParen, "'destruct' takes no parameters");
            var body = ParseBlock();
            return new DestructorDecl(_sourceIndex, line, body);
        }

        /// <summary>Ein einzelner Parameter, in beiden Schreibweisen (wie bei
        /// `var`): `[Typ] Name` (Typ zuerst, bestehende Reihenfolge) ODER
        /// `Name [: Typ]` (var-artige Reihenfolge) - jeweils optional gefolgt
        /// von `= Standardwert`. Welche Reihenfolge vorliegt, entscheidet
        /// NextLooksLikeTypeThenName() genau wie bei Variablen-Deklarationen.</summary>
        /// <summary>Ein Parameter: `[Typ|var] name[ranks] [: einheit] [= default]`
        /// (SPEC "Einheiten-Deklarationen") - dieselbe feste Reihenfolge wie bei
        /// var-/Feld-Deklarationen, KEINE alternative "Name zuerst, Typ per ':'
        /// danach"-Schreibweise mehr (die es vorher gab - genau die
        /// Mehrdeutigkeit, die der ':' jetzt einheitlich NUR noch als Einheit
        /// löst). Weder `var` noch ein Typ ist Pflicht (ein Parameter ohne
        /// beides bleibt wie bisher ungetypt) - IST aber eine Einheit ohne
        /// vorangestelltes `var`/Typ angegeben (`func f(a:mm)`), wird das
        /// implizit wie `var a:mm` behandelt (Typ aus dem Argument beim Aufruf
        /// übernommen, Einheit fest mm).</summary>
        private LambdaParam ParseOneParam(bool allowRef)
        {
            // `ref` vor dem Parameter (contextual: `ref` bleibt als Name/Typ nutzbar, solange kein weiterer Name darauf folgt)
            bool byRef = false;
            if (Check(TokenType.Identifier) && Peek().Lexeme == "ref"
                && PeekAt(1).Type is not (TokenType.Comma or TokenType.RParen or TokenType.Assign or TokenType.Colon or TokenType.LBracket))
            {
                if (!allowRef)
                    throw Error("'ref' parameters are only possible in methods and constructors", Peek());
                Advance();
                byRef = true;
            }

            TypeRef? type = null;
            if (Check(TokenType.Var))
            {
                Advance();
                type = new TypeRef(TypeRef.InferredMarker, null, 0, Namespaces: CurrentNamespaces());
            }
            else if (NextLooksLikeTypeThenName())
            {
                type = ParseTypeRef();
            }

            string pname = Expect(TokenType.Identifier, "Expected a parameter name").Lexeme;
            var arrayRanks = ParseArrayRanks();

            if (Match(TokenType.Colon))
            {
                string unitName = ParseUnitName();
                type = type != null
                    ? type with { Unit = unitName }
                    : new TypeRef(TypeRef.InferredMarker, null, 0, Namespaces: CurrentNamespaces(), Unit: unitName);
            }

            Expr? defaultValue = null;
            if (Match(TokenType.Assign))
            {
                if (byRef) throw Error("A 'ref' parameter cannot have a default value", Previous());
                defaultValue = ParseExpression();
            }

            return new LambdaParam(pname, type, arrayRanks, defaultValue, byRef);
        }

        private List<LambdaParam> ParseParamList(bool allowRef = false)
        {
            Expect(TokenType.LParen, "Expected '('");
            var parms = new List<LambdaParam>();
            if (!Check(TokenType.RParen))
            {
                do
                {
                    parms.Add(ParseOneParam(allowRef));
                } while (Match(TokenType.Comma));
            }
            Expect(TokenType.RParen, "Expected ')' after the parameter list");
            return parms;
        }

        private List<Expr> ParseArgList()
        {
            Expect(TokenType.LParen, "Expected '('");
            var args = new List<Expr>();
            if (!Check(TokenType.RParen))
            {
                do
                {
                    args.Add(ParseExpression());
                } while (Match(TokenType.Comma));
            }
            Expect(TokenType.RParen, "Expected ')' after the argument list");
            return args;
        }

        // -----------------------------------------------------------
        // Ausdrücke (Präzedenz von niedrig nach hoch)
        // -----------------------------------------------------------
        private Expr ParseExpression() => ParseAssignment();

        private Expr ParseAssignment()
        {
            var left = ParseOr();
            if (LineContinues() && Check(TokenType.Assign))
            {
                Advance();
                int line = Previous().Line;
                var value = ParseAssignment(); // rechts-assoziativ
                if (left is IdentifierExpr or MemberExpr or IndexExpr or UnaryExpr { Op: UnaryOp.Dereference })
                    return new AssignExpr(line, left, value);
                throw Error("Invalid assignment target", Previous());
            }
            return left;
        }

        private Expr ParseOr()
        {
            var left = ParseAnd();
            while (LineContinues() && Check(TokenType.Or))
            {
                int line = Advance().Line;
                left = new BinaryExpr(line, BinaryOp.Or, left, ParseAnd());
            }
            return left;
        }

        private Expr ParseAnd()
        {
            var left = ParseBitwiseOr();
            while (LineContinues() && Check(TokenType.And))
            {
                int line = Advance().Line;
                left = new BinaryExpr(line, BinaryOp.And, left, ParseBitwiseOr());
            }
            return left;
        }

        /// <summary>`|` (bitweises Oder), `#` (bitweises Exklusiv-Oder - NICHT
        /// `^`, das ist Potenz, siehe ParsePower) und `&amp;` (bitweises Und) -
        /// dieselbe Präzedenz-Reihenfolge wie in den meisten C-artigen
        /// Sprachen (Oder bindet am schwächsten, Und am stärksten dieser
        /// drei), eingeordnet zwischen den logischen Operatoren `&&`/`||`
        /// und dem Vergleich `==`/`!=`/`&lt;`/etc. Alle drei nur auf `int`
        /// anwendbar (siehe Values.Value.BitAnd/BitOr/BitXor).</summary>
        private Expr ParseBitwiseOr()
        {
            var left = ParseBitwiseXor();
            while (LineContinues() && Check(TokenType.Pipe))
            {
                int line = Advance().Line;
                left = new BinaryExpr(line, BinaryOp.BitOr, left, ParseBitwiseXor());
            }
            return left;
        }

        private Expr ParseBitwiseXor()
        {
            var left = ParseBitwiseAnd();
            while (LineContinues() && Check(TokenType.Hash))
            {
                int line = Advance().Line;
                left = new BinaryExpr(line, BinaryOp.BitXor, left, ParseBitwiseAnd());
            }
            return left;
        }

        private Expr ParseBitwiseAnd()
        {
            var left = ParseEquality();
            while (LineContinues() && Check(TokenType.Amp))
            {
                int line = Advance().Line;
                left = new BinaryExpr(line, BinaryOp.BitAnd, left, ParseEquality());
            }
            return left;
        }

        private Expr ParseEquality()
        {
            var left = ParseRelationalOrIs();
            while (LineContinues() && (Check(TokenType.Eq) || Check(TokenType.NotEq)))
            {
                var opTok = Advance();
                var op = opTok.Type == TokenType.Eq ? BinaryOp.Eq : BinaryOp.NotEq;
                left = new BinaryExpr(opTok.Line, op, left, ParseRelationalOrIs());
            }
            return left;
        }

        private Expr ParseRelationalOrIs()
        {
            var left = ParseShift();
            while (LineContinues())
            {
                if (Check(TokenType.Lt) || Check(TokenType.LtEq) ||
                    Check(TokenType.Gt) || Check(TokenType.GtEq))
                {
                    var opTok = Advance();
                    var op = opTok.Type switch
                    {
                        TokenType.Lt => BinaryOp.Lt,
                        TokenType.LtEq => BinaryOp.LtEq,
                        TokenType.Gt => BinaryOp.Gt,
                        _ => BinaryOp.GtEq,
                    };
                    left = new BinaryExpr(opTok.Line, op, left, ParseShift());
                }
                else if (Check(TokenType.Is))
                {
                    int line = Advance().Line;
                    if (Match(TokenType.In))
                    {
                        string unitName = Expect(TokenType.Identifier, "Expected a unit name after 'is in'").Lexeme;
                        left = new IsInExpr(line, left, unitName);
                    }
                    else if (Match(TokenType.Of))
                    {
                        string typeName = ParseTypeAnnotationName();
                        left = new IsOfExpr(line, left, new TypeRef(typeName, null, 0, Namespaces: CurrentNamespaces()));
                    }
                    else if (Match(TokenType.From))
                    {
                        left = new IsFromExpr(line, left, ParseShift(), Transitive: false);
                    }
                    else if (Match(TokenType.Under))
                    {
                        left = new IsFromExpr(line, left, ParseShift(), Transitive: true);
                    }
                    else
                    {
                        throw Error("Expected 'in', 'of', 'from' or 'under' after 'is'", Peek());
                    }
                }
                else
                {
                    break;
                }
            }
            return left;
        }

        /// <summary>`&lt;&lt;`/`&gt;&gt;` (Bit-Schiebeoperatoren) - werden bewusst
        /// NICHT vom Lexer als eigene Zwei-Zeichen-Tokens erkannt (anders als
        /// z.B. `==`/`&amp;&amp;`), sondern hier rein auf Parser-Ebene als zwei
        /// AUFEINANDERFOLGENDE `&lt;`/`&gt;`-Tokens: der Lexer liefert für `&lt;`/`&gt;`
        /// IMMER nur Einzelzeichen-Tokens (außer `&lt;=`/`&gt;=`), weil `&gt;` auch zum
        /// Schließen einer generischen Typ-Argumentliste dient (`new Box&lt;int&gt;()`,
        /// auch beliebig tief VERSCHACHTELT: `new Box&lt;Box&lt;int&gt;&gt;()`, siehe
        /// ParseOptionalTypeParamNames/SkipOptionalNestedTypeArgs) - ein Lexer,
        /// der `&gt;&gt;` gierig zu einem einzigen Schiebeoperator-Token zusammenzöge,
        /// würde das an dieser Stelle zerstören. Da Typ-Argumentlisten über
        /// eigene, komplett getrennte Parser-Methoden laufen (nie über diese
        /// Ausdrucks-Präzedenzkette), gibt es hier ohnehin keinen Konflikt: zwei
        /// `&gt;`/`&lt;`
        /// in Folge bedeuten INNERHALB eines Ausdrucks immer einen
        /// Schiebeoperator.</summary>
        private Expr ParseShift()
        {
            var left = ParseAdditive();
            while (LineContinues())
            {
                if (Check(TokenType.Lt) && PeekAt(1).Type == TokenType.Lt)
                {
                    int line = Peek().Line;
                    Advance(); Advance();
                    left = new BinaryExpr(line, BinaryOp.ShiftLeft, left, ParseAdditive());
                }
                else if (Check(TokenType.Gt) && PeekAt(1).Type == TokenType.Gt)
                {
                    int line = Peek().Line;
                    Advance(); Advance();
                    left = new BinaryExpr(line, BinaryOp.ShiftRight, left, ParseAdditive());
                }
                else
                {
                    break;
                }
            }
            return left;
        }

        private Expr ParseAdditive()
        {
            var left = ParseMultiplicative();
            while (LineContinues() && (Check(TokenType.Plus) || Check(TokenType.Minus)))
            {
                var opTok = Advance();
                var op = opTok.Type == TokenType.Plus ? BinaryOp.Add : BinaryOp.Sub;
                left = new BinaryExpr(opTok.Line, op, left, ParseMultiplicative());
            }
            return left;
        }

        private Expr ParseMultiplicative()
        {
            var left = ParseUnary();
            while (LineContinues() && (Check(TokenType.Star) || Check(TokenType.Slash) || Check(TokenType.Percent)))
            {
                var opTok = Advance();
                var op = opTok.Type switch
                {
                    TokenType.Star => BinaryOp.Mul,
                    TokenType.Slash => BinaryOp.Div,
                    _ => BinaryOp.Mod,
                };
                left = new BinaryExpr(opTok.Line, op, left, ParseUnary());
            }
            return left;
        }

        /// <summary>`^` (Potenz - NICHT bitweises XOR, das ist `#`, siehe
        /// ParseBitwiseXor) - bindet stärker als `*`/`/`/`%` (`2 * 3^2` ist
        /// `2 * 9 = 18`, nicht `(2*3)^2`). RECHTS-assoziativ (`2^3^2` ist
        /// `2^(3^2) = 2^9`, nicht `(2^3)^2`).
        ///
        /// Das Zusammenspiel mit unären Präfix-Operatoren folgt bewusst
        /// Pythons `**`-Konvention (nicht "unär bindet immer am stärksten"):
        /// die BASIS (links von `^`) ist hier bewusst nur `ParsePostfix()`,
        /// KEIN `ParseUnary()` - ein Vorzeichen VOR der ganzen Potenz bindet
        /// dadurch SCHWÄCHER als `^` selbst (`-2^2` ist `-(2^2) = -4`, nicht
        /// `(-2)^2 = 4` - siehe ParseUnary, das bei einem Präfix-Operator
        /// erst hierher zurückkommt, NACHDEM `^` schon ausgewertet wurde).
        /// Der EXPONENT (rechts von `^`) dagegen ist ein volles
        /// `ParseUnary()`, damit `2^-2` (negativer Exponent) trotzdem direkt
        /// funktioniert, ohne Klammern setzen zu müssen.</summary>
        private Expr ParsePower()
        {
            var left = ParsePostfix();
            if (LineContinues() && Check(TokenType.Caret))
            {
                int line = Advance().Line;
                var right = ParseUnary();
                return new BinaryExpr(line, BinaryOp.Power, left, right);
            }
            return left;
        }

        /// <summary>Präfix-Operatoren: '-' (Vorzeichen), '!' (logische Negation),
        /// '~' (bitweise Inversion), '*' (Dereferenzierung, nur in 'unsafe'),
        /// '&amp;' (Address-of, nur in 'unsafe'). Rechts-assoziativ verkettbar.
        /// Kein eigener Präfix hier -> ParsePower() (siehe dort für die
        /// bewusste Reihenfolge relativ zu `^`), NICHT direkt ParsePostfix().</summary>
        private Expr ParseUnary()
        {
            if (Check(TokenType.PlusPlus) || Check(TokenType.MinusMinus))
            {
                var opTok = Advance();
                bool isIncrement = opTok.Type == TokenType.PlusPlus;
                // Bewusst ParsePostfix() statt rekursiv ParseUnary() - das
                // Operanden-Ziel von Präfix '++'/'--' MUSS ein zuweisbarer
                // Ausdruck sein (Variable/Feld/Index), nie ein weiterer
                // unärer Ausdruck (`++!x`/`++-x` wären sinnlos, da deren
                // Ergebnis kein gültiges Zuweisungsziel ist) - ParsePostfix
                // deckt genau Bezeichner/Member-/Index-Zugriffsketten ab,
                // dieselbe Ebene, die auch AssignExpr.Target zulässt.
                var target = ParsePostfix();
                return new IncDecExpr(opTok.Line, target, isIncrement, IsPrefix: true);
            }

            // `flat x` / `copy x` (SPEC 2.4): Kopier-Präfixe - der Operand ist wieder ein Unary-Ausdruck,
            // `copy a.b` kopiert also `a.b`, `copy a + b` ist `(copy a) + b`. (`sync flat x` liest sein `flat`
            // selbst, siehe ParseSync - es kommt hier nie an.)
            if (Check(TokenType.Flat) || Check(TokenType.Copy))
            {
                var copyTok = Advance();
                return new UnaryExpr(copyTok.Line, copyTok.Type == TokenType.Flat ? UnaryOp.FlatCopy : UnaryOp.DeepCopy, ParseUnary());
            }

            if (Check(TokenType.Minus) || Check(TokenType.Bang) || Check(TokenType.Tilde)
                || Check(TokenType.Star) || Check(TokenType.Amp))
            {
                var opTok = Advance();
                var op = opTok.Type switch
                {
                    TokenType.Minus => UnaryOp.Negate,
                    TokenType.Bang => UnaryOp.LogicalNot,
                    TokenType.Tilde => UnaryOp.BitNot,
                    TokenType.Star => UnaryOp.Dereference,
                    _ => UnaryOp.AddressOf,
                };
                return new UnaryExpr(opTok.Line, op, ParseUnary());
            }
            return ParsePower();
        }

        /// <summary>Postfix-Kette: Aufruf, Member-Zugriff, Index, sowie die
        /// Coercion-Suffixe ':' (Einheit) und '!' (Typ) in beliebiger Reihenfolge,
        /// zuletzt optional '++'/'--' (siehe Ast.IncDecExpr) - bewusst NICHT
        /// Teil der Schleife oben (kann selbst nicht weiter Ziel eines '.'/
        /// '['/... sein, `x++.feld` wäre sinnlos, da `x++` ein reiner Wert
        /// ist, kein Objekt).</summary>
        private Expr ParsePostfix()
        {
            var expr = ParsePrimary();
            while (LineContinues())
            {
                if (Check(TokenType.Dot))
                {
                    int line = Advance().Line;
                    string name = Expect(TokenType.Identifier, "Expected a name after '.'").Lexeme;
                    expr = new MemberExpr(line, expr, name);
                }
                else if (Check(TokenType.LParen))
                {
                    int line = Peek().Line;
                    var args = ParseArgList();
                    expr = new CallExpr(line, expr, args);
                }
                else if (Check(TokenType.LBracket))
                {
                    int line = Advance().Line;
                    var index = ParseExpression();
                    Expect(TokenType.RBracket, "Expected ']' after the index");
                    expr = new IndexExpr(line, expr, index);
                }
                else if (Check(TokenType.Colon) && _suppressColonPostfixAtDepth != _bracketDepth)
                {
                    int line = Advance().Line;
                    // Optionales Lookahead-Argument (Einheitenname) nur auf derselben
                    // Zeile konsumieren - sonst genau der ursprüngliche Bug: ein
                    // Bezeichner, der eigentlich zur nächsten Zeile/Anweisung gehört,
                    // würde fälschlich hier mit verschluckt.
                    string? unitName = LineContinues() && Check(TokenType.Identifier)
                        ? Advance().Lexeme : null;
                    expr = new UnitCoerceExpr(line, expr, unitName);
                }
                else if (Check(TokenType.Bang))
                {
                    int line = Advance().Line;
                    TokenType? typeKw = LineContinues() && TypeKeywords.Contains(Peek().Type)
                        ? Advance().Type : null;
                    expr = new TypeCoerceExpr(line, expr, typeKw);
                }
                else
                {
                    break;
                }
            }
            if (LineContinues() && (Check(TokenType.PlusPlus) || Check(TokenType.MinusMinus)))
            {
                var opTok = Advance();
                expr = new IncDecExpr(opTok.Line, expr, opTok.Type == TokenType.PlusPlus, IsPrefix: false);
            }
            return expr;
        }

        /// <summary>`sync X` / `try sync X` / `sync flat X` / `try sync flat X`
        /// (siehe Ast.SyncExpr-Doku). `Target` wird bewusst nur bis
        /// Postfix-Ebene geparst (Bezeichner, Member-/Index-Zugriff) - nicht
        /// als volle Ausdrucks-Ebene, damit z.B. `sync a == b` unzweideutig
        /// als `(sync a) == b` gelesen wird, nicht als `sync (a == b)`.</summary>
        private Expr ParseSyncExpr()
        {
            int line = Peek().Line;
            bool isTry = Match(TokenType.Try);
            Expect(TokenType.Sync, "Expected 'sync'");
            // `sync globals`: das Hauptprogramm arbeitet die Warteschlange seiner Fire-Threads ab
            if (Check(TokenType.Identifier) && Peek().Lexeme == "globals")
            {
                if (isTry) throw Error("'try sync globals' does not exist", Peek());
                Advance();
                return new SyncGlobalsExpr(line);
            }
            bool isFlat = Match(TokenType.Flat);
            var target = ParsePostfix();
            return new SyncExpr(line, isTry, isFlat, target);
        }

        /// <summary>Gesetzt, solange das ZIEL von `on` einer `func`-Lambda gelesen wird (`func (x) on win => ...`): ein Bezeichner oder eine geklammerte
        /// Angabe direkt davor darf dort nicht als Kurzform-Lambda (`win => ...`, `(win) => ...`) gelesen werden - das `=>` gehört zur `func`-Lambda.
        /// ParsePrimary verbraucht das Flag mit dem ersten Primärausdruck.</summary>
        private bool _suppressShortLambda;

        private Expr ParsePrimary()
        {
            bool noShortLambda = _suppressShortLambda;
            _suppressShortLambda = false;
            var tok = Peek();

            switch (tok.Type)
            {
                case TokenType.Sync:
                    return ParseSyncExpr();

                case TokenType.Try:
                {
                    // Siehe ParseStatement-Vorausschau: 'try' ist als
                    // Ausdrucksanfang nur zusammen mit 'sync'/'process' oder
                    // einem direkten Funktionsaufruf gültig ('try {' wäre
                    // schon dort als try/catch-Block abgefangen worden).
                    if (PeekAt(1).Type == TokenType.Process)
                    {
                        Advance(); // 'try'
                        Advance(); // 'process'
                        var procTarget = ParsePostfix();
                        return new TryProcessExpr(tok.Line, procTarget);
                    }
                    if (PeekAt(1).Type == TokenType.Sync)
                        return ParseSyncExpr();

                    // 'try Name(args)' - Aufruf einer "tryable" nativen
                    // Funktion (siehe TryCallExpr-Doku, Resolver prüft, ob
                    // 'Name' tatsächlich so registriert ist).
                    Advance(); // 'try'
                    var innerCall = ParsePostfix();
                    if (innerCall is not CallExpr)
                        throw Error("Expected 'sync', 'process' or a function call after 'try'", tok);
                    return new TryCallExpr(tok.Line, innerCall);
                }

                case TokenType.Dot:
                {
                    // Nur innerhalb eines 'with'-Blocks gültig (siehe
                    // ParseWithStmt). WICHTIG: '.' UND der Mitgliedsname
                    // werden HIER direkt konsumiert, nicht der allgemeinen
                    // Postfix-Schleife (ParsePostfix) überlassen - die bricht
                    // sofort ab, wenn der AKTUELLE Token einen Zeilenumbruch
                    // davor hat (LineContinues() prüft genau das), und ein
                    // '.' als allererstes Token einer neuen with-Zeile hat
                    // IMMER einen Zeilenumbruch davor. Ohne diesen Fix bliebe
                    // der '.' unkonsumiert stehen - eine Endlosschleife beim
                    // Parsen des with-Blocks (jede weitere Runde landet
                    // wieder exakt hier, ohne je voranzukommen).
                    if (_withVarStack.Count == 0)
                        throw Error(
                            "'.' at the start of an expression is only valid inside a 'with' block", tok);
                    Advance();
                    string memberName = Expect(TokenType.Identifier, "Expected a name after '.'").Lexeme;
                    return new MemberExpr(tok.Line, new IdentifierExpr(tok.Line, _withVarStack.Peek()), memberName);
                }

                case TokenType.IntLiteral:
                    Advance();
                    return new LiteralExpr(tok.Line,
                        Value.MakeInt((long)tok.LiteralValue!, Unit.Parse(tok.UnitSuffix ?? "")));

                case TokenType.FloatLiteral:
                    Advance();
                    return new LiteralExpr(tok.Line,
                        Value.MakeFloat((double)tok.LiteralValue!, Unit.Parse(tok.UnitSuffix ?? "")));

                case TokenType.StringLiteral:
                    Advance();
                    return new LiteralExpr(tok.Line, Value.MakeString((string)tok.LiteralValue!));

                case TokenType.InterpolatedStringLiteral:
                    Advance();
                    return ParseInterpolatedString(tok);

                case TokenType.CharLiteral:
                    Advance();
                    return new LiteralExpr(tok.Line, Value.MakeChar((char)tok.LiteralValue!));

                case TokenType.True:
                    Advance();
                    return new LiteralExpr(tok.Line, Value.MakeBool(true));

                case TokenType.False:
                    Advance();
                    return new LiteralExpr(tok.Line, Value.MakeBool(false));

                case TokenType.Undefined:
                    Advance();
                    return new LiteralExpr(tok.Line, Value.MakeUndefined());

                case TokenType.Identifier:
                    if (tok.Lexeme == "probe" && IsProbeOperandNext()) return ParseProbe();
                    if (!noShortLambda && PeekAt(1).Type == TokenType.Arrow)
                    {
                        // Kurzform `x => ausdruck`
                        Advance();
                        return ParseLambdaTail(tok.Line, new List<LambdaParam> { new LambdaParam(tok.Lexeme, null, new List<Expr?>(), null) });
                    }
                    Advance();
                    return new IdentifierExpr(tok.Line, tok.Lexeme);

                case TokenType.This:
                    Advance();
                    return new ThisExpr(tok.Line);

                case TokenType.Base:
                    Advance();
                    return new BaseExpr(tok.Line);

                case TokenType.LBracket:
                {
                    Advance();
                    var elements = new List<Expr>();
                    if (!Check(TokenType.RBracket))
                    {
                        do
                        {
                            elements.Add(ParseExpression());
                        } while (Match(TokenType.Comma));
                    }
                    Expect(TokenType.RBracket, "Expected ']' after the array literal");
                    return new ArrayLiteralExpr(tok.Line, elements);
                }

                case TokenType.New:
                {
                    Advance();
                    // 'new Type[sizeExpr]' (Array-Allokation) vs. 'new ClassName(args)'.
                    // Der Elementtyp hier bewusst nur als Basisname (ohne Bitbreite/
                    // Pointer) - eine Bitbreiten-Klammer direkt hinterm Typ würde sonst
                    // mit der Array-Größen-Klammer kollidieren (beide stehen unmittelbar
                    // hinter dem Typnamen). Eine bestimmte Elementbreite legt man über
                    // den deklarierten Variablentyp fest (`var a : int[16] = new int[10]`).
                    if (TypeKeywords.Contains(Peek().Type)
                        || (Check(TokenType.Identifier) && PeekAt(1).Type == TokenType.LBracket))
                    {
                        // 'new byte[n]' - Sonderfall: erzeugt einen rohen
                        // Values.ByteBuffer statt eines ScriptArray (siehe
                        // NewBufferExpr-Doku), bewusst nur eindimensional -
                        // 'new byte[n][m]' (mehrere Ränge) ist deshalb ein
                        // Fehler statt eines "Arrays von Buffern".
                        if (Check(TokenType.KwByte))
                        {
                            Advance(); // 'byte'
                            var bufSizeExprs = ParseArrayRanks();
                            if (bufSizeExprs.Count == 0)
                                throw Error("Expected '[' after 'byte' in 'new'", Peek());
                            if (bufSizeExprs.Count > 1 || bufSizeExprs[0] == null)
                                throw Error("'new byte[...]' expects exactly ONE size (no multidimensional byte buffer, no unspecified size)", Peek());
                            return new NewBufferExpr(tok.Line, bufSizeExprs[0]!);
                        }

                        string elementTypeName = ParseTypeAnnotationName();
                        var elementType = new TypeRef(elementTypeName, null, 0, Namespaces: CurrentNamespaces());
                        var sizeExprs = ParseArrayRanks();
                        if (sizeExprs.Count == 0)
                            throw Error("Expected '[' after the array element type in 'new'", Peek());
                        return new NewArrayExpr(tok.Line, elementType, sizeExprs);
                    }

                    string className = ParseTypeAnnotationName();
                    // 'new Name<Arg1, Arg2>(...)' für eine generische Klasse -
                    // unzweideutig, da nach 'new Name' ohnehin zwingend eine
                    // Argumentliste '(...)' folgen MUSS (nie ein Vergleich),
                    // ParseOptionalTypeParamNames() passt hier 1:1 (dieselbe
                    // '<name, name>'-Grammatik wie bei der Deklaration).
                    var typeArgs = ParseOptionalTypeParamNames();
                    var args = ParseArgList();
                    return new NewExpr(tok.Line, new TypeRef(className, null, 0, Namespaces: CurrentNamespaces()), args, typeArgs);
                }

                case TokenType.Throw:
                {
                    Advance();
                    var value = ParseExpression();
                    return new ThrowExpr(tok.Line, value);
                }

                case TokenType.Func:
                    return ParseLambda();

                case TokenType.LParen:
                {
                    if (!noShortLambda && IsParenLambda())
                    {
                        // Kurzform `(a, b) => ausdruck` / `() => ausdruck`
                        int lambdaLine = tok.Line;
                        return ParseLambdaTail(lambdaLine, ParseParamList());
                    }
                    Advance();
                    var inner = ParseExpression();
                    Expect(TokenType.RParen, "Expected ')' after the parenthesized expression");
                    return inner;
                }

                default:
                    throw Error($"Unexpected token '{tok.Lexeme}' ({tok.Type})", tok);
            }
        }

        /// <summary>Baut aus den bereits vom Lexer gesammelten
        /// InterpolationSegments (siehe dortige Doku) einen
        /// InterpolatedStringExpr: Text-Segmente werden direkt übernommen,
        /// jedes Ausdrucks-Segment wird EIGENSTÄNDIG neu gelext und geparst
        /// (eine neue Lexer/Parser-Instanz auf nur diesem Teilstring) - so
        /// entsteht die volle Ausdrucks-Grammatik innerhalb von `{...}` ganz
        /// ohne einen zweiten, parallelen Grammatik-Pfad im Haupt-Parser
        /// pflegen zu müssen. Nach dem geparsten Ausdruck darf nur noch EOF
        /// folgen - alles andere (z.B. ein zweiter, nicht durch einen
        /// Operator verbundener Ausdruck) ist ein Fehler im Format-String.</summary>
        private Expr ParseInterpolatedString(Token tok)
        {
            var segments = (List<InterpolationSegment>)tok.LiteralValue!;
            var parts = new List<InterpolationPart>();

            foreach (var seg in segments)
            {
                if (!seg.IsExpression)
                {
                    parts.Add(new InterpolationTextPart(seg.Text));
                    continue;
                }

                List<Token> innerTokens;
                try
                {
                    innerTokens = new Lexer(seg.Text).Tokenize();
                }
                catch (LexException ex)
                {
                    throw Error($"Invalid expression in a format string: {ex.Message}", tok);
                }

                var innerParser = new Parser(innerTokens);
                Expr expr;
                try
                {
                    expr = innerParser.ParseExpression();
                    if (!innerParser.Check(TokenType.Eof))
                        throw Error(
                            "Unexpected further tokens after the expression in a format string interpolation " +
                            "(is an operator missing, or is a ':' not enclosed in parentheses by mistake?)", innerParser.Peek());
                }
                catch (ParseException ex)
                {
                    throw Error($"Invalid expression in a format string: {ex.Message}", tok);
                }

                parts.Add(new InterpolationExprPart(expr, seg.Format));
            }

            return new InterpolatedStringExpr(tok.Line, parts);
        }

        private Expr ParseLambda()
        {
            int line = Peek().Line;
            Expect(TokenType.Func, "Expected 'func'");
            var parms = ParseParamList();

            Expr? onTarget = null;
            if (Match(TokenType.On))
            {
                _suppressShortLambda = true;
                onTarget = ParsePostfix();
                _suppressShortLambda = false;
            }

            return ParseLambdaTail(line, parms, onTarget);
        }

        /// <summary>Folgt auf das aktuelle Wort (`probe`/`silence`) ein Bezeichner oder `this`?</summary>
        private bool IsProbeOperandNext() => PeekAt(1).Type is TokenType.Identifier or TokenType.This;

        /// <summary>Liest `a.b.c` (auch `a.b.*`, `a`): liefert das Objekt-Ausdruck, das Mitglied (null bei `.*` und ohne Punkt) und ob ein Mitglied
        /// angegeben war (`.name` oder `.*`).</summary>
        private (Expr Target, string? Member, bool HasMember) ParseProbePath()
        {
            Expr target = ParsePrimary();
            if (!Check(TokenType.Dot)) return (target, null, false);
            string? member = null;
            while (Check(TokenType.Dot))
            {
                Advance();
                if (Check(TokenType.Star))
                {
                    var starTok = Advance();
                    if (member != null) target = new MemberExpr(starTok.Line, target, member);
                    member = null;
                    break;
                }
                var nameTok = Expect(TokenType.Identifier, "Expected a member name after '.'");
                if (member != null) target = new MemberExpr(nameTok.Line, target, member);
                member = nameTok.Lexeme;
            }
            return (target, member, true);
        }

        /// <summary>`probe ziel changed|changing handler` - der Handler ist ein Block `{ ... }`, `=> ausdruck`, `(a, b) => ...` oder ein beliebiger
        /// Lambda-Ausdruck. Block und `=> ausdruck` bekommen die impliziten Namen `sender`, `name`, `old`, `value` (4 Parameter, siehe VM.RunProbeHandler).</summary>
        private Expr ParseProbe()
        {
            int line = Peek().Line;
            Advance(); // 'probe'
            var (target, member, hasMember) = ParseProbePath();
            if (!hasMember)
                throw Error("'probe' expects a member: 'probe object.member changed ...' (or 'object.*' for all)", Peek());
            if (!Check(TokenType.Identifier) || Peek().Lexeme is not ("changed" or "changing"))
                throw Error("Expected 'changed' or 'changing' after the target of 'probe'", Peek());
            bool changing = Advance().Lexeme == "changing";

            Expr handler;
            if (Check(TokenType.LBrace))
            {
                var body = ParseBlock();
                handler = new LambdaExpr(line, ImplicitProbeParams(), null, body);
            }
            else if (Check(TokenType.Arrow))
                handler = ParseLambdaTail(line, ImplicitProbeParams());
            else if (IsParenLambda())
                handler = ParseLambdaTail(line, ParseParamList());
            else
                handler = ParseExpression();
            return new ProbeExpr(line, target, member, changing, handler);
        }

        private static List<LambdaParam> ImplicitProbeParams() =>
            new[] { "sender", "name", "old", "value" }.Select(n => new LambdaParam(n, null, new List<Expr?>(), null)).ToList();

        /// <summary>`silence a.b` / `silence a.*` (Mitglied bzw. alle Proben des Objekts `a`) oder `silence x` (Probe-Handle bzw. Objekt).</summary>
        private Stmt ParseSilence()
        {
            int line = Peek().Line;
            Advance(); // 'silence'
            var (target, member, hasMember) = ParseProbePath();
            ExpectStatementTerminator();
            return new SilenceStmt(_sourceIndex, line, target, member, hasMember);
        }

        /// <summary>Steht der aktuelle `(` am Anfang einer Kurzform-Lambda `(...) =>`? (Lookahead bis zur passenden `)`.)</summary>
        private bool IsParenLambda()
        {
            int depth = 0;
            for (int i = 0; ; i++)
            {
                var t = PeekAt(i);
                if (t.Type == TokenType.Eof) return false;
                if (t.Type == TokenType.LParen) depth++;
                else if (t.Type == TokenType.RParen && --depth == 0) return PeekAt(i + 1).Type == TokenType.Arrow;
            }
        }

        /// <summary>`=> ausdruck` bzw. `=> { ... }` einer Lambda (nach Parameterliste und optionalem `on`).</summary>
        private Expr ParseLambdaTail(int line, List<LambdaParam> parms, Expr? onTarget = null)
        {
            Expect(TokenType.Arrow, "Expected '=>' in the lambda");

            Stmt.BlockStmt body;
            if (Check(TokenType.LBrace))
            {
                body = ParseBlock();
            }
            else
            {
                // Kurzform: `=> ausdruck` wird implizit zu `{ return ausdruck; }`.
                var exprLine = Peek().Line;
                var value = ParseExpression();
                body = new Stmt.BlockStmt(_sourceIndex, exprLine, new List<Stmt> { new ReturnStmt(_sourceIndex, exprLine, value) });
            }

            return new LambdaExpr(line, parms, onTarget, body);
        }

        // -----------------------------------------------------------
        // Low-level Token-Handling
        // -----------------------------------------------------------
        private Token Peek() => _tokens[_pos];
        private Token Previous() => _tokens[_pos - 1];
        private bool Check(TokenType type) => Peek().Type == type;

        /// <summary>Lookahead ohne zu konsumieren - `PeekAt(0)` == `Peek()`.
        /// Fällt am Dateiende sicher auf das letzte Token (Eof) zurück.</summary>
        private Token PeekAt(int offset)
        {
            int idx = _pos + offset;
            return idx < _tokens.Count ? _tokens[idx] : _tokens[^1];
        }

        private Token Advance()
        {
            var t = _tokens[_pos];
            if (t.Type != TokenType.Eof) _pos++;

            if (t.Type is TokenType.LParen or TokenType.LBracket) _bracketDepth++;
            else if (t.Type is TokenType.RParen or TokenType.RBracket) _bracketDepth = Math.Max(0, _bracketDepth - 1);

            return t;
        }

        private bool Match(TokenType type)
        {
            if (!Check(type)) return false;
            Advance();
            return true;
        }

        private Token Expect(TokenType type, string message)
        {
            if (Check(type)) return Advance();
            throw Error(message, Peek());
        }

        /// <summary>true, wenn der nächste Token in derselben Zeile liegt wie der
        /// zuletzt konsumierte (kein Zeilenumbruch dazwischen) – Grundlage dafür,
        /// dass Binär-Operatoren, Zuweisung und die Postfix-Kette nicht
        /// versehentlich über eine neue Anweisung in der nächsten Zeile hinweg
        /// weiterlesen.</summary>
        private bool LineContinues() => _bracketDepth > 0 || !Peek().NewlineBefore;

        /// <summary>Erzwingt die Statement-Trennungsregel: zwischen zwei Statements
        /// muss ein ';' oder ein Zeilenumbruch stehen. Blockende ('}') und Dateiende
        /// zählen ebenfalls als gültiger Abschluss (z.B. für einzeilige Blöcke wie
        /// `{ return x }`).</summary>
        private void ExpectStatementTerminator()
        {
            if (Match(TokenType.Semicolon)) return;
            if (Peek().NewlineBefore) return;
            if (Check(TokenType.RBrace) || Check(TokenType.Eof)) return;
            throw Error("Expected ';' or a line break between statements", Peek());
        }

        private static ParseException Error(string message, Token at) =>
            new(message, at.Line, at.Column);
    }
}
