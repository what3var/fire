using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using fire.Ast;
using fire.Lexing;
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
            return MergeClassExtensions(FlattenNamespaceWrappers(combined));
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
            var rest = new List<Stmt>();
            foreach (var stmt in program)
            {
                if (stmt is ClassExtensionDecl ext) extensions.Add(ext);
                else rest.Add(stmt);
            }
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
                        $"'class extends {ext.TargetRef.BaseName}' - Klasse '{ext.TargetRef.BaseName}' ist im selben " +
                        "Programm nicht bekannt (Erweiterungen können keine neue Klasse anlegen).",
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
            if (Check(TokenType.Break))
            {
                int breakLine = Advance().Line;
                ExpectStatementTerminator();
                return new BreakStmt(breakLine);
            }
            if (Check(TokenType.Continue))
            {
                int continueLine = Advance().Line;
                ExpectStatementTerminator();
                return new ContinueStmt(continueLine);
            }
            if (Check(TokenType.Leave))
            {
                int leaveLine = Advance().Line;
                ExpectStatementTerminator();
                return new LeaveStmt(leaveLine);
            }
            if (Check(TokenType.Terminate)) return ParseTerminateStmt();
            if (Check(TokenType.Process)) return ParseProcessStmt();
            if (Check(TokenType.LBrace)) return ParseBlock();

            var expr = ParseExpression();
            ExpectStatementTerminator();
            return new ExprStmt(expr.Line, expr);
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
            Expect(TokenType.LBrace, "Erwarte '{'");

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

            Expect(TokenType.RBrace, "Erwarte '}' am Blockende");
            return new Stmt.BlockStmt(line, statements);
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

            var protectedBlock = new Stmt.BlockStmt(line, restStatements);
            return new TryStmt(line, protectedBlock, catches, finallyBlock);
        }

        private CatchClause ParseCatchClause()
        {
            int line = Peek().Line;
            Expect(TokenType.Catch, "Erwarte 'catch'");
            Expect(TokenType.LParen, "Erwarte '(' nach 'catch'");

            // `catch (TypeName varName)` bzw. ungetypt `catch (varName)` -
            // SEIT SPEC "Einheiten-Deklarationen" dieselbe Reihenfolge wie
            // überall sonst (Typ/`var` zuerst, Name danach), NICHT mehr die
            // alte "Name zuerst, Typ per ':' danach"-Schreibweise (die genau
            // die Mehrdeutigkeit war, die der ':' jetzt überall einheitlich
            // nur noch für Einheiten löst).
            //
            // Eigene, kleine Heuristik statt der geteilten
            // NextLooksLikeTypeThenName() (die für Felder/Parameter reicht):
            // die schaut nur EIN Token voraus (Identifier-dann-Identifier),
            // ein punktierter Typname wie 'Geometry.MyException e' hat an
            // der Stelle aber einen '.' statt direkt des zweiten Bezeichners
            // - würde dort also fälschlich als "ungetypt" durchgehen.
            TypeRef? typeRef = null;
            bool looksTyped = TypeKeywords.Contains(Peek().Type)
                || (Check(TokenType.Identifier) && (PeekAt(1).Type == TokenType.Identifier || PeekAt(1).Type == TokenType.Dot));
            if (looksTyped)
                typeRef = new TypeRef(ParseDottedName("Typname in catch(...)"), null, 0, Namespaces: CurrentNamespaces());

            string varName = Expect(TokenType.Identifier, "Erwarte Bezeichner in catch(...)").Lexeme;

            Expect(TokenType.RParen, "Erwarte ')' nach catch-Parametern");
            var body = ParseBlock();
            return new CatchClause(line, typeRef, varName, body);
        }

        private Stmt ParseTry()
        {
            int line = Peek().Line;
            Expect(TokenType.Try, "Erwarte 'try'");
            var tryBlock = ParseBlock();

            var catches = new List<CatchClause>();
            while (Check(TokenType.Catch))
                catches.Add(ParseCatchClause());

            Stmt.BlockStmt? finallyBlock = null;
            if (Match(TokenType.Finally))
                finallyBlock = ParseBlock();

            if (catches.Count == 0 && finallyBlock == null)
                throw Error("'try' benötigt mindestens einen 'catch'-Block oder 'finally'", Peek());

            return new TryStmt(line, tryBlock, catches, finallyBlock);
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
            Expect(TokenType.Readonly, "Erwarte 'readonly'");

            if (Check(TokenType.Var))
            {
                var decl = ParseVarDeclCore(isReadonly: true);
                ExpectStatementTerminator();
                return decl;
            }
            if (NextLooksLikeTypeThenName())
                return ParseBareTypedDecl(isReadonly: true);

            throw Error("Erwarte 'var' oder eine Typ-Deklaration nach 'readonly'", Peek());
        }

        /// <summary>var-Deklaration ohne eigenen Terminator-Konsum - für Stellen
        /// wie den for-Init, wo der Aufrufer das trennende ';' selbst erwartet
        /// (sonst würde hier schon eines "verschluckt" und der Aufrufer danach
        /// fälschlich ein zweites erwarten).</summary>
        private VarDeclStmt ParseVarDeclCore(bool isReadonly)
        {
            int line = Peek().Line;
            Expect(TokenType.Var, "Erwarte 'var'");
            string name = Expect(TokenType.Identifier, "Erwarte Variablennamen").Lexeme;

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

            return new VarDeclStmt(line, name, type, arrayRanks, initializer, isReadonly);
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
        private string ParseUnitName() => Expect(TokenType.Identifier, "Erwarte Einheitennamen nach ':'").Lexeme;

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
            string name = Expect(TokenType.Identifier, "Erwarte Bezeichner").Lexeme;
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
            return new VarDeclStmt(line, name, type, arrayRanks, initializer, isReadonly);
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
            string name = Expect(TokenType.Identifier, "Erwarte Typnamen").Lexeme;
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
        private TypeRef ParseTypeRef()
        {
            string baseName = ParseTypeAnnotationName();
            var namespaces = CurrentNamespaces();

            if (baseName != "lambda" && Check(TokenType.Identifier) && Peek().Lexeme == "lambda")
            {
                Advance(); // 'lambda' konsumieren - 'baseName' war in Wahrheit der Rückgabetyp
                return new TypeRef("lambda", null, 0, ParseLambdaSignature(returnTypeName: baseName), namespaces);
            }

            if (baseName == "lambda")
                return new TypeRef("lambda", null, 0, ParseLambdaSignature(returnTypeName: null), namespaces);

            // 'byte' ist reines Sugar für 'int[8]' (siehe SPEC 8.10) - eine
            // explizite Bitbreite DANACH wäre widersprüchlich/redundant und
            // wird deshalb abgelehnt, statt sie stillschweigend zu ignorieren
            // oder zu überschreiben.
            if (baseName == "byte")
            {
                if (Check(TokenType.LBracket))
                    throw Error("'byte' hat bereits eine feste Breite von 8 Bit - kein zusätzliches '[...]' danach", Peek());
                int bytePointerDepth = 0;
                while (Match(TokenType.Star)) bytePointerDepth++;
                return new TypeRef("int", 8, bytePointerDepth, Namespaces: namespaces);
            }

            int? width = null;
            if (Match(TokenType.LBracket))
            {
                var widthTok = Expect(TokenType.IntLiteral, "Erwarte Bitbreite (8/16/32/64)");
                width = (int)(long)widthTok.LiteralValue!;
                Expect(TokenType.RBracket, "Erwarte ']' nach Bitbreite");
            }

            int pointerDepth = 0;
            while (Match(TokenType.Star)) pointerDepth++;

            return new TypeRef(baseName, width, pointerDepth, Namespaces: namespaces);
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
                Expect(TokenType.Gt, "Erwarte '>' nach den Lambda-Parametertypen");
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
                Expect(TokenType.RBracket, "Erwarte ']'");
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
        private bool NextLooksLikeTypeThenName()
        {
            if (TypeKeywords.Contains(Peek().Type)) return true;
            // 'lambda' als Typname (siehe ParseTypeRef) kann - anders als ein
            // Klassenname - auch von '<' statt einem weiteren Bezeichner
            // gefolgt werden ('lambda<int> x'), das würde die normale
            // "Identifier gefolgt von Identifier"-Heuristik unten verpassen.
            if (Check(TokenType.Identifier) && Peek().Lexeme == "lambda") return true;
            return Check(TokenType.Identifier) && PeekAt(1).Type == TokenType.Identifier;
        }

        /// <summary>Wie NextLooksLikeTypeThenName(), erkennt zusätzlich einen
        /// VOLLQUALIFIZIERTEN (punktierten) Typnamen wie 'Geometry.Circle
        /// circle' - dort folgt auf den ersten Bezeichner ein '.', nicht
        /// direkt der zweite Bezeichner, die einfache Heuristik oben würde
        /// das fälschlich als "ungetypt" lesen (dieselbe Lücke, die vorher
        /// schon bei 'catch (Typ varName)' aufgefallen war, siehe
        /// ParseCatchClause).
        ///
        /// NUR für Kontexte sicher, in denen IMMER eine Deklaration/ein
        /// Rückgabetyp folgt (Feld, Parameter, extern-/Methoden-Rückgabetyp -
        /// siehe Aufrufstellen) - bewusst NICHT für die Top-Level-Anweisungs-
        /// Weiche (ParseStatement/ParseBareTypedDecl-Dispatch): dort könnte
        /// 'Namespace.Funktion()' genauso gut ein eigenständiger
        /// Ausdrucks-Aufruf sein ('Namespace.Funktion' gefolgt von '(' statt
        /// einem Bezeichner) - das ließe sich mit einem simplen
        /// 2-Token-Vorausblick nicht zuverlässig von einer echten
        /// Deklaration unterscheiden, ohne weiter vorauszuschauen (oder
        /// notfalls zurückzusetzen). In einem GARANTIERTEN Deklarations-
        /// Kontext gibt es diese Mehrdeutigkeit dagegen nicht.</summary>
        private bool NextLooksLikeQualifiedTypeThenName()
        {
            if (TypeKeywords.Contains(Peek().Type)) return true;
            if (Check(TokenType.Identifier) && Peek().Lexeme == "lambda") return true;
            return Check(TokenType.Identifier) && (PeekAt(1).Type == TokenType.Identifier || PeekAt(1).Type == TokenType.Dot);
        }

        private Stmt ParseIf()
        {
            int line = Peek().Line;
            Expect(TokenType.If, "Erwarte 'if'");
            Expect(TokenType.LParen, "Erwarte '(' nach 'if'");
            var cond = ParseExpression();
            Expect(TokenType.RParen, "Erwarte ')' nach if-Bedingung");
            var thenBranch = ParseStatement();
            Stmt? elseBranch = null;
            if (Match(TokenType.Else))
                elseBranch = ParseStatement();
            return new IfStmt(line, cond, thenBranch, elseBranch);
        }

        private Stmt ParseWhile()
        {
            int line = Peek().Line;
            Expect(TokenType.While, "Erwarte 'while'");
            Expect(TokenType.LParen, "Erwarte '(' nach 'while'");
            var cond = ParseExpression();
            Expect(TokenType.RParen, "Erwarte ')' nach while-Bedingung");
            var body = ParseStatement();
            return new WhileStmt(line, cond, body);
        }

        private Stmt ParseFor()
        {
            int line = Peek().Line;
            Expect(TokenType.For, "Erwarte 'for'");
            Expect(TokenType.LParen, "Erwarte '(' nach 'for'");

            Stmt? init = null;
            if (!Check(TokenType.Semicolon))
                init = Check(TokenType.Var) ? ParseVarDeclCore(isReadonly: false) : new ExprStmt(Peek().Line, ParseExpression());
            Expect(TokenType.Semicolon, "Erwarte ';' nach for-Init");

            Expr? cond = null;
            if (!Check(TokenType.Semicolon)) cond = ParseExpression();
            Expect(TokenType.Semicolon, "Erwarte ';' nach for-Bedingung");

            Expr? incr = null;
            if (!Check(TokenType.RParen)) incr = ParseExpression();
            Expect(TokenType.RParen, "Erwarte ')' nach for-Klauseln");

            var body = ParseStatement();
            return new ForStmt(line, init, cond, incr, body);
        }

        private Stmt ParseForeach()
        {
            int line = Peek().Line;
            Expect(TokenType.Foreach, "Erwarte 'foreach'");
            Expect(TokenType.LParen, "Erwarte '(' nach 'foreach'");
            string varName = Expect(TokenType.Identifier, "Erwarte Variablennamen").Lexeme;
            Expect(TokenType.In, "Erwarte 'in' in foreach");
            var iterable = ParseExpression();
            Expect(TokenType.RParen, "Erwarte ')' nach foreach-Klauseln");
            var body = ParseStatement();
            return new ForeachStmt(line, varName, iterable, body);
        }

        private Stmt ParseReturn()
        {
            int line = Peek().Line;
            Expect(TokenType.Return, "Erwarte 'return'");
            Expr? value = null;
            if (!Check(TokenType.Semicolon) && !Check(TokenType.RBrace) && !Check(TokenType.Eof))
                value = ParseExpression();
            ExpectStatementTerminator();
            return new ReturnStmt(line, value);
        }

        private Stmt ParseThrowStmt()
        {
            int line = Peek().Line;
            Expect(TokenType.Throw, "Erwarte 'throw'");
            var value = ParseExpression();
            ExpectStatementTerminator();
            return new ThrowStmt(line, value);
        }

        /// <summary>`extern [ReturnType] Name(params)` - deklariert eine native
        /// Funktionssignatur ohne Body. ReturnType fehlt -> kein Rückgabewert.
        /// Bekommt die zuletzt per `#extern "libName"` gesetzte Bibliothek
        /// gestempelt (siehe ParseDirective/_currentExternLib) - null, wenn
        /// keine solche Direktive vor dieser Deklaration stand.</summary>
        private Stmt ParseExternDecl()
        {
            int line = Peek().Line;
            Expect(TokenType.Extern, "Erwarte 'extern'");

            TypeRef? returnType = null;
            if (NextLooksLikeQualifiedTypeThenName())
                returnType = ParseTypeRef();

            string name = Expect(TokenType.Identifier, "Erwarte Funktionsnamen nach 'extern'").Lexeme;
            var parms = ParseParamList();
            ExpectStatementTerminator();
            return new ExternDecl(line, returnType, name, parms, _currentExternLib);
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
            Expect(TokenType.Hash, "Erwarte '#'");

            if (Match(TokenType.Extern))
            {
                var libTok = Expect(TokenType.StringLiteral, "Erwarte Bibliotheksnamen (String) nach '#extern'");
                _currentExternLib = (string)libTok.LiteralValue!;
                ExpectStatementTerminator();
                return new NoOpStmt(line);
            }

            if (Check(TokenType.Identifier) && Peek().Lexeme == "noshadow")
            {
                Advance();
                ExpectStatementTerminator();
                return new NoShadowDirective(line);
            }

            // '#using' ist ab jetzt reine Preprocessor-Angelegenheit (siehe
            // Preprocessing.Preprocessor.ProcessInner/ProcessedSource) - eine
            // '#using'-Zeile wird dort schon erkannt und aus dem Text entfernt,
            // der Parser sieht sie nie mehr. Kein Fall dafür hier mehr nötig.
            throw Error($"Unbekannte Präprozessor-Direktive '#{Peek().Lexeme}' (bekannt: '#extern \"libName\"', '#noshadow')", Peek());
        }

        private Stmt ParseUnsafeStmt()
        {
            int line = Peek().Line;
            Expect(TokenType.Unsafe, "Erwarte 'unsafe'");
            var body = ParseBlock();
            return new UnsafeStmt(line, body);
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
            Expect(TokenType.With, "Erwarte 'with'");
            var target = ParseExpression();

            string tempName = $"__with{_withCounter}__";
            _withCounter++;
            _withVarStack.Push(tempName);

            Expect(TokenType.LBrace, "Erwarte '{' nach dem with-Ausdruck");

            var statements = new List<Stmt>
            {
                new VarDeclStmt(line, tempName, null, Array.Empty<Expr?>(), target),
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
            Expect(TokenType.RBrace, "Erwarte '}' am Ende des with-Blocks");

            // Erst NACH dem Parsen des Bodies wieder abbauen - Verschachtelung
            // (with a { with b { ... } }) braucht den äußeren Eintrag ja noch,
            // während der innere Body geparst wird, aber nicht mehr danach.
            _withVarStack.Pop();

            return new Stmt.BlockStmt(line, statements);
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
            Expect(TokenType.Switch, "Erwarte 'switch'");
            Expect(TokenType.LParen, "Erwarte '(' nach 'switch'");
            var subject = ParseExpression();
            Expect(TokenType.RParen, "Erwarte ')' nach switch-Ausdruck");
            Expect(TokenType.LBrace, "Erwarte '{' nach switch-Kopf");

            string tempName = $"__switch{_switchCounter}__";
            _switchCounter++;

            var cases = new List<(Expr Condition, List<Stmt> Body)>();
            List<Stmt>? defaultBody = null;

            while (!Check(TokenType.RBrace) && !Check(TokenType.Eof))
            {
                Expect(TokenType.Case, "Erwarte 'case' im switch-Body");

                if (Match(TokenType.Default))
                {
                    if (defaultBody != null)
                        throw Error("Mehrere 'case default' in einem switch sind nicht erlaubt", Previous());
                    Expect(TokenType.Colon, "Erwarte ':' nach 'case default'");
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
                Expect(TokenType.Colon, "Erwarte ':' nach case-Bedingung");
                var condition = new BinaryExpr(line, op, new IdentifierExpr(line, tempName), valueExpr);
                cases.Add((condition, ParseSwitchCaseBody()));
            }
            Expect(TokenType.RBrace, "Erwarte '}' am Ende des switch");

            // If/Else-if-Kette von HINTEN nach VORNE aufbauen - 'case default'
            // (falls vorhanden) wird das innerste 'else', sonst bleibt es null
            // (keiner der Fälle trifft zu -> switch tut einfach nichts).
            Stmt? chain = defaultBody != null ? new Stmt.BlockStmt(line, defaultBody) : null;
            for (int i = cases.Count - 1; i >= 0; i--)
                chain = new IfStmt(line, cases[i].Condition, new Stmt.BlockStmt(line, cases[i].Body), chain);

            var outerStatements = new List<Stmt>
            {
                new VarDeclStmt(line, tempName, null, Array.Empty<Expr?>(), subject),
            };
            if (chain != null) outerStatements.Add(chain);

            return new Stmt.BlockStmt(line, outerStatements);
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
            Expect(TokenType.Fire, "Erwarte 'fire'");

            if (Check(TokenType.Identifier) && PeekAt(1).Type == TokenType.LParen)
                return ParseFireCallForm(line);

            var (takingCaptures, withVarName, withSource) = ParseFireTakingWithClauses();
            var body = ParseBlock();
            return new FireStmt(line, takingCaptures, withVarName, withSource, body);
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
                    var nameTok = Expect(TokenType.Identifier, "Erwarte Bezeichner nach 'taking'");
                    // Referenziert die BEREITS im umgebenden Scope deklarierte
                    // Variable gleichen Namens - ganz normale Identifier-
                    // Auflösung im AUFRUFENDEN Kontext (nicht im isolierten
                    // Fire-Block-Scope, siehe Resolver.ResolveFireStmt).
                    takingCaptures.Add(new FireTakingCapture(nameTok.Lexeme, new IdentifierExpr(nameTok.Line, nameTok.Lexeme)));
                }
                else
                {
                    if (withVarName != null)
                        throw Error("'with' wurde in diesem 'fire' bereits verwendet", Peek());
                    Advance();
                    var nameTok = Expect(TokenType.Identifier, "Erwarte Bezeichner nach 'with'");
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
            Expect(TokenType.LParen, "Erwarte '(' nach '" + methodName + "'");
            var callArgs = new List<Expr>();
            if (!Check(TokenType.RParen))
            {
                do { callArgs.Add(ParseExpression()); } while (Match(TokenType.Comma));
            }
            Expect(TokenType.RParen, "Erwarte ')' nach 'fire " + methodName + "(...)'");

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
            var body = new Stmt.BlockStmt(line, new List<Stmt> { new ExprStmt(line, callExpr) });

            return new FireStmt(line, allCaptures, withVarName, withSource, body);
        }

        /// <summary>`terminate()` / `terminate(wert)` - siehe Ast.TerminateStmt-
        /// Doku. Syntaktisch wie ein Funktionsaufruf, aber ein eigenes
        /// Statement (kein Ausdruck) - `terminate` hat keinen sinnvollen
        /// "Ergebniswert", der weiterverwendet werden könnte.</summary>
        private Stmt ParseTerminateStmt()
        {
            int line = Peek().Line;
            Expect(TokenType.Terminate, "Erwarte 'terminate'");
            Expect(TokenType.LParen, "Erwarte '(' nach 'terminate'");
            Expr? value = null;
            if (!Check(TokenType.RParen))
                value = ParseExpression();
            Expect(TokenType.RParen, "Erwarte ')' nach terminate-Argument");
            ExpectStatementTerminator();
            return new TerminateStmt(line, value);
        }

        /// <summary>`process X` (blockierend, siehe Ast.ProcessStmt-Doku).
        /// Die nicht-blockierende Variante `try process X` ist dagegen ein
        /// AUSDRUCK und wird in ParsePrimary behandelt.</summary>
        private Stmt ParseProcessStmt()
        {
            int line = Peek().Line;
            Expect(TokenType.Process, "Erwarte 'process'");
            var target = ParsePostfix();
            ExpectStatementTerminator();
            return new ProcessStmt(line, target);
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
            Expect(TokenType.Catch, "Erwarte 'catch'");

            if (Match(TokenType.Terminate))
            {
                Expect(TokenType.LParen, "Erwarte '(' nach 'catch terminate'");
                string? paramName = null;
                if (!Check(TokenType.RParen))
                    paramName = Expect(TokenType.Identifier, "Erwarte Parametername in 'catch terminate(...)'").Lexeme;
                Expect(TokenType.RParen, "Erwarte ')' nach 'catch terminate(...)'");
                var terminateBody = ParseBlock();
                return new CatchTerminateDecl(line, paramName, terminateBody);
            }

            // 'threads' ist - wie 'get'/'set'/'value' bei Properties - ein rein
            // kontextabhängiger Bezeichner, kein reserviertes Schlüsselwort.
            Expect(TokenType.Identifier, "Erwarte 'threads' oder 'terminate' nach 'catch'");
            Expect(TokenType.LParen, "Erwarte '(' nach 'catch threads'");

            TypeRef? typeRef = null;
            string? varName = null;
            if (!Check(TokenType.RParen))
            {
                // 'catch threads(ExceptionType e)' - Typ-dann-Name, wie ein
                // normaler Methodenparameter (und inzwischen auch wie beim
                // normalen 'catch (TypeName varName)' - siehe ParseCatchClause).
                string typeName = Expect(TokenType.Identifier, "Erwarte Typnamen in 'catch threads(...)'").Lexeme;
                typeRef = new TypeRef(typeName, null, 0, Namespaces: CurrentNamespaces());
                varName = Expect(TokenType.Identifier, "Erwarte Parametername in 'catch threads(...)'").Lexeme;
            }
            Expect(TokenType.RParen, "Erwarte ')' nach 'catch threads(...)'");
            var threadsBody = ParseBlock();
            return new CatchThreadsDecl(line, typeRef, varName, threadsBody);
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
            if (isActor) Expect(TokenType.Actor, "Erwarte 'actor'");
            else Expect(TokenType.Class, "Erwarte 'class'");

            if (Check(TokenType.Extends))
                return ParseClassExtensionDecl(line);

            string name = Expect(TokenType.Identifier, "Erwarte Klassennamen").Lexeme;

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
                    string baseName = Expect(TokenType.Identifier, "Erwarte Basisklassen-/Interface-Namen").Lexeme;
                    baseRefs.Add(new TypeRef(baseName, null, 0, Namespaces: namespaces));
                } while (Match(TokenType.Comma));
            }

            // 'where'-Klauseln: EINE pro Typ-Parameter (C#-artig), in
            // beliebiger Reihenfolge, alle VOR der öffnenden '{'. Siehe
            // ParseWhereClause für die Constraint-Grammatik selbst.
            var typeParams = ParseWhereClauses(typeParamNames, line);

            Expect(TokenType.LBrace, "Erwarte '{' nach Klassenkopf");
            var members = new List<Stmt>();
            while (!Check(TokenType.RBrace) && !Check(TokenType.Eof))
                members.AddRange(ParseClassMember());
            Expect(TokenType.RBrace, "Erwarte '}' am Ende der Klasse");

            return new ClassDecl(line, QualifyDeclName(name), baseRefs, members, typeParams, IsActor: isActor, SourceIndex: _sourceIndex);
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
            Expect(TokenType.Gt, "Erwarte '>' nach Typ-Parameterliste");
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
            Expect(TokenType.Gt, "Erwarte '>' nach verschachtelter Typ-Argumentliste");
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
                string tpName = Expect(TokenType.Identifier, "Erwarte Typ-Parameternamen nach 'where'").Lexeme;
                if (!typeParamNames.Contains(tpName))
                    throw Error(
                        $"'where {tpName}' bezieht sich auf keinen deklarierten Typ-Parameter " +
                        $"(deklariert: {(typeParamNames.Count == 0 ? "keine" : string.Join(", ", typeParamNames))})",
                        Previous());
                if (constraintsByName.ContainsKey(tpName))
                    throw Error($"Mehrere 'where {tpName}'-Klauseln für denselben Typ-Parameter", Previous());

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
            Expect(TokenType.Is, "Erwarte 'is' in einer where-Bedingung");
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
                var tok = Expect(TokenType.StringLiteral, "Erwarte Einheitenname (als String) nach 'is in'");
                return new TypeConstraint(TypeConstraintKind.IsIn, (string)tok.LiteralValue!);
            }
            throw Error("Erwarte 'of' oder 'in' nach 'is' in einer where-Bedingung", Peek());
        }

        /// <summary>`class extends Name { neue Mitglieder... }` - siehe
        /// Ast.ClassExtensionDecl-Doku. Members werden mit demselben
        /// ParseClassMember() geparst wie in einer normalen Klasse (Felder,
        /// Methoden, Properties, sogar ein weiterer Konstruktor/Destruktor -
        /// ob das beim Zusammenführen sinnvoll ist, prüft erst der Merge-
        /// Schritt, siehe Parser.MergeClassExtensions).</summary>
        private Stmt ParseClassExtensionDecl(int line)
        {
            Expect(TokenType.Extends, "Erwarte 'extends'");
            string targetName = Expect(TokenType.Identifier, "Erwarte Namen der zu erweiternden Klasse").Lexeme;
            var targetRef = new TypeRef(targetName, null, 0, Namespaces: CurrentNamespaces());
            Expect(TokenType.LBrace, "Erwarte '{' nach 'class extends " + targetName + "'");

            var members = new List<Stmt>();
            while (!Check(TokenType.RBrace) && !Check(TokenType.Eof))
                members.AddRange(ParseClassMember());
            Expect(TokenType.RBrace, "Erwarte '}' am Ende der Erweiterung");

            return new ClassExtensionDecl(line, targetRef, members);
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
            Expect(TokenType.Namespace, "Erwarte 'namespace'");
            string name = ParseDottedName("Namespace-Namen");
            Expect(TokenType.LBrace, "Erwarte '{' nach Namespace-Namen");

            string? savedNamespace = _currentNamespace;
            _currentNamespace = _currentNamespace == null ? name : _currentNamespace + "." + name;

            var members = new List<Stmt>();
            while (!Check(TokenType.RBrace) && !Check(TokenType.Eof))
                members.Add(ParseStatement());
            Expect(TokenType.RBrace, "Erwarte '}' am Ende des Namespace");

            _currentNamespace = savedNamespace;

            return new NamespaceDecl(line, name, members);
        }

        /// <summary>Liest einen (möglicherweise mehrteiligen, per '.' getrennten)
        /// Namen wie 'A' oder 'A.B.C' - für Namespace-Namen (Deklaration UND
        /// `#using`) an genau den beiden Stellen genutzt, wo diese Sprache
        /// sonst nirgendwo einen '.' als Teil eines NAMENS selbst erlaubt
        /// (überall sonst ist '.' der Elementzugriffs-Operator).</summary>
        private string ParseDottedName(string what)
        {
            string name = Expect(TokenType.Identifier, $"Erwarte {what}").Lexeme;
            while (Match(TokenType.Dot))
                name += "." + Expect(TokenType.Identifier, $"Erwarte {what} nach '.'").Lexeme;
            return name;
        }

        /// <summary>`interface Name { [ReturnType] Method(params) ... }` - reine
        /// Methodensignaturen, keine Felder/Konstruktor/Bodies.</summary>
        private Stmt ParseInterfaceDecl()
        {
            int line = Peek().Line;
            Expect(TokenType.Interface, "Erwarte 'interface'");
            string name = Expect(TokenType.Identifier, "Erwarte Interface-Namen").Lexeme;
            Expect(TokenType.LBrace, "Erwarte '{' nach Interface-Kopf");

            var methods = new List<InterfaceMethodSig>();
            while (!Check(TokenType.RBrace) && !Check(TokenType.Eof))
            {
                int mLine = Peek().Line;
                TypeRef? returnType = null;
                if (NextLooksLikeQualifiedTypeThenName())
                    returnType = ParseTypeRef();
                string methodName = Expect(TokenType.Identifier, "Erwarte Methodennamen").Lexeme;
                var parms = ParseParamList();
                ExpectStatementTerminator();
                methods.Add(new InterfaceMethodSig(mLine, returnType, methodName, parms));
            }
            Expect(TokenType.RBrace, "Erwarte '}' am Ende des Interface");

            return new InterfaceDecl(line, QualifyDeclName(name), methods);
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
            Expect(TokenType.Enum, "Erwarte 'enum'");
            string name = Expect(TokenType.Identifier, "Erwarte Enum-Namen").Lexeme;
            Expect(TokenType.LBrace, "Erwarte '{' nach Enum-Namen");

            var members = new List<EnumMember>();
            if (!Check(TokenType.RBrace))
            {
                do
                {
                    string memberName = Expect(TokenType.Identifier, "Erwarte Enum-Mitgliedsnamen").Lexeme;
                    Expr? valueExpr = null;
                    if (Match(TokenType.Assign))
                        valueExpr = ParseExpression();
                    members.Add(new EnumMember(memberName, valueExpr));
                } while (Match(TokenType.Comma));
            }

            Expect(TokenType.RBrace, "Erwarte '}' nach Enum-Mitgliedern");
            return new EnumDecl(line, QualifyDeclName(name), members);
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
        private List<Stmt> ParsePropertyBody(int line, TypeRef? type, string name, AccessModifier access)
        {
            Expect(TokenType.LBrace, "Erwarte '{' nach Property-Namen");

            Stmt.BlockStmt? getter = null;
            Stmt.BlockStmt? setter = null;
            bool getterIsAuto = false;
            bool setterIsAuto = false;

            while (!Check(TokenType.RBrace) && !Check(TokenType.Eof))
            {
                if (Check(TokenType.Identifier) && Peek().Lexeme == "get")
                {
                    if (getter != null || getterIsAuto)
                        throw Error($"'get' ist für Property '{name}' bereits definiert", Peek());
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
                        throw Error($"'set' ist für Property '{name}' bereits definiert", Peek());
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
                    throw Error("Erwarte 'get' oder 'set' im Property-Body", Peek());
                }
            }

            Expect(TokenType.RBrace, "Erwarte '}' am Ende der Property");

            if (getter == null && !getterIsAuto && setter == null && !setterIsAuto)
                throw Error($"Property '{name}' braucht mindestens 'get' oder 'set'", Peek());

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
                result.Add(new FieldDecl(line, type, Array.Empty<Expr?>(), backingName, null, IsReadonly: false, Access: AccessModifier.Private));

                if (getterIsAuto)
                    getter = new Stmt.BlockStmt(line, new List<Stmt>
                    {
                        new ReturnStmt(line, new MemberExpr(line, new ThisExpr(line), backingName)),
                    });

                if (setterIsAuto)
                    setter = new Stmt.BlockStmt(line, new List<Stmt>
                    {
                        new ExprStmt(line, new AssignExpr(line,
                            new MemberExpr(line, new ThisExpr(line), backingName),
                            new IdentifierExpr(line, "value"))),
                    });
            }

            result.Add(new PropertyDecl(line, type, name, getter, setter, access));
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
            else if (NextLooksLikeQualifiedTypeThenName())
            {
                type = ParseTypeRef();
            }

            string name = Expect(TokenType.Identifier, "Erwarte Feld- oder Methodennamen").Lexeme;

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
                    throw Error("'readonly' ist nur für Felder gültig, nicht für Methoden", Peek());
                var parms = ParseParamList();
                var methodTypeParams = ParseWhereClauses(methodTypeParamNames, line);
                var body = ParseBlock();
                return new List<Stmt> { new MethodDecl(line, type, name, parms, body,
                    methodTypeParamNames.Count > 0 ? methodTypeParams : null, access) };
            }

            if (methodTypeParamNames.Count > 0)
                throw Error("Ein generischer Typ-Parameterkopf '<...>' muss von einer Parameterliste '(...)' gefolgt werden", Peek());

            if (Check(TokenType.LBrace))
            {
                if (isReadonly)
                    throw Error(
                        "'readonly' ist für Properties nicht gültig - eine Property ohne 'set' ist bereits nur lesbar",
                        Peek());
                return ParsePropertyBody(line, type, name, access);
            }

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
            return new List<Stmt> { new FieldDecl(line, type, arrayRanks, name, initializer, isReadonly, access) };
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
            Expect(TokenType.Operator, "Erwarte 'operator'");
            string symbol = ParseOperatorSymbol();
            var parms = ParseParamList();

            string internalName = symbol == "[]"
                ? parms.Count switch
                {
                    1 => "GetIndex",
                    2 => "SetIndex",
                    _ => throw Error(
                        "'operator[]' braucht entweder 1 Parameter (Lesezugriff: Index) " +
                        "oder 2 Parameter (Schreibzugriff: Index, Wert)", Peek()),
                }
                : "operator" + symbol;

            if (symbol != "[]" && parms.Count != 1)
                throw Error($"'operator{symbol}' braucht genau 1 Parameter (den rechten Operanden - 'this' ist der linke)", Peek());

            var body = ParseBlock();
            return new MethodDecl(line, null, internalName, parms, body, null);
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
                Expect(TokenType.RBracket, "Erwarte ']' nach 'operator['");
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
                "Erwarte einen überladbaren Operator nach 'operator' " +
                "('[]', '+', '-', '*', '/', '%', '^', '&', '|', '#', '<<', '>>', " +
                "'==', '!=', '<', '<=', '>', '>=')", Peek());
        }

        private Stmt ParseConstructor(AccessModifier access)
        {
            int line = Peek().Line;
            Expect(TokenType.Construct, "Erwarte 'construct'");
            var parms = ParseParamList();

            IReadOnlyList<Expr>? baseArgs = null;
            if (Match(TokenType.Colon))
            {
                Expect(TokenType.Base, "Erwarte 'base' nach ':' im Konstruktor");
                baseArgs = ParseArgList();
            }

            var body = ParseBlock();
            return new ConstructorDecl(line, parms, baseArgs, body, access);
        }

        private Stmt ParseDestructor()
        {
            int line = Peek().Line;
            Expect(TokenType.Destruct, "Erwarte 'destruct'");
            Expect(TokenType.LParen, "Erwarte '(' nach 'destruct'");
            Expect(TokenType.RParen, "'destruct' nimmt keine Parameter");
            var body = ParseBlock();
            return new DestructorDecl(line, body);
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
        private LambdaParam ParseOneParam()
        {
            TypeRef? type = null;
            if (Check(TokenType.Var))
            {
                Advance();
                type = new TypeRef(TypeRef.InferredMarker, null, 0, Namespaces: CurrentNamespaces());
            }
            else if (NextLooksLikeQualifiedTypeThenName())
            {
                type = ParseTypeRef();
            }

            string pname = Expect(TokenType.Identifier, "Erwarte Parameternamen").Lexeme;
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
                defaultValue = ParseExpression();

            return new LambdaParam(pname, type, arrayRanks, defaultValue);
        }

        private List<LambdaParam> ParseParamList()
        {
            Expect(TokenType.LParen, "Erwarte '('");
            var parms = new List<LambdaParam>();
            if (!Check(TokenType.RParen))
            {
                do
                {
                    parms.Add(ParseOneParam());
                } while (Match(TokenType.Comma));
            }
            Expect(TokenType.RParen, "Erwarte ')' nach Parameterliste");
            return parms;
        }

        private List<Expr> ParseArgList()
        {
            Expect(TokenType.LParen, "Erwarte '('");
            var args = new List<Expr>();
            if (!Check(TokenType.RParen))
            {
                do
                {
                    args.Add(ParseExpression());
                } while (Match(TokenType.Comma));
            }
            Expect(TokenType.RParen, "Erwarte ')' nach Argumentliste");
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
                throw Error("Ungültiges Ziel für Zuweisung", Previous());
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
                        string unitName = Expect(TokenType.Identifier, "Erwarte Einheitennamen nach 'is in'").Lexeme;
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
                        throw Error("Erwarte 'in', 'of', 'from' oder 'under' nach 'is'", Peek());
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
                    string name = Expect(TokenType.Identifier, "Erwarte Namen nach '.'").Lexeme;
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
                    Expect(TokenType.RBracket, "Erwarte ']' nach Index");
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
            Expect(TokenType.Sync, "Erwarte 'sync'");
            bool isFlat = Match(TokenType.Flat);
            var target = ParsePostfix();
            return new SyncExpr(line, isTry, isFlat, target);
        }

        private Expr ParsePrimary()
        {
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
                        throw Error("Erwarte 'sync', 'process' oder einen Funktionsaufruf nach 'try'", tok);
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
                            "'.' als Ausdrucksanfang ist nur innerhalb eines 'with'-Blocks gültig", tok);
                    Advance();
                    string memberName = Expect(TokenType.Identifier, "Erwarte Namen nach '.'").Lexeme;
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
                    Expect(TokenType.RBracket, "Erwarte ']' nach Array-Literal");
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
                                throw Error("Erwarte '[' nach 'byte' bei 'new'", Peek());
                            if (bufSizeExprs.Count > 1 || bufSizeExprs[0] == null)
                                throw Error("'new byte[...]' erwartet genau EINE Größe (kein mehrdimensionaler Byte-Puffer, keine unbestimmte Größe)", Peek());
                            return new NewBufferExpr(tok.Line, bufSizeExprs[0]!);
                        }

                        string elementTypeName = ParseTypeAnnotationName();
                        var elementType = new TypeRef(elementTypeName, null, 0, Namespaces: CurrentNamespaces());
                        var sizeExprs = ParseArrayRanks();
                        if (sizeExprs.Count == 0)
                            throw Error("Erwarte '[' nach Array-Elementtyp bei 'new'", Peek());
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
                    Advance();
                    var inner = ParseExpression();
                    Expect(TokenType.RParen, "Erwarte ')' nach geklammertem Ausdruck");
                    return inner;
                }

                default:
                    throw Error($"Unerwartetes Token '{tok.Lexeme}' ({tok.Type})", tok);
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
                    throw Error($"Ungültiger Ausdruck in Format-String: {ex.Message}", tok);
                }

                var innerParser = new Parser(innerTokens);
                Expr expr;
                try
                {
                    expr = innerParser.ParseExpression();
                    if (!innerParser.Check(TokenType.Eof))
                        throw Error(
                            "Unerwartete weitere Token nach dem Ausdruck in einer Format-String-Interpolation " +
                            "(fehlt ein Operator, oder ':' versehentlich nicht geklammert?)", innerParser.Peek());
                }
                catch (ParseException ex)
                {
                    throw Error($"Ungültiger Ausdruck in Format-String: {ex.Message}", tok);
                }

                parts.Add(new InterpolationExprPart(expr, seg.Format));
            }

            return new InterpolatedStringExpr(tok.Line, parts);
        }

        private Expr ParseLambda()
        {
            int line = Peek().Line;
            Expect(TokenType.Func, "Erwarte 'func'");
            var parms = ParseParamList();

            Expr? onTarget = null;
            if (Match(TokenType.On))
                onTarget = ParsePostfix();

            Expect(TokenType.Arrow, "Erwarte '=>' im Lambda");

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
                body = new Stmt.BlockStmt(exprLine, new List<Stmt> { new ReturnStmt(exprLine, value) });
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
            throw Error("Erwarte ';' oder einen Zeilenumbruch zwischen Anweisungen", Peek());
        }

        private static ParseException Error(string message, Token at) =>
            new(message, at.Line, at.Column);
    }
}
