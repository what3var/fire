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
        /// <summary>The message without position and file.</summary>
        public string RawMessage { get; }
        /// <summary>The file the error is in (set for a program of several files, see ProcessedSource.Name).</summary>
        public string? FileName { get; }

        public ParseException(string message, int line, int column, string? fileName = null)
            : base($"{(fileName != null ? fileName + ": " : "")}{message} ({line}:{column})")
        {
            Line = line;
            Column = column;
            RawMessage = message;
            FileName = fileName;
        }
    }

    /// <summary>
    /// Classic recursive-descent parser. Operator precedence (low -> high):
    /// Assignment -> Or -> And -> Equality -> Relational/Is -> Additive ->
    /// Multiplicative -> Unary (-, !, ~) -> Postfix (call/member/index/coercion) -> Primary.
    ///
    /// Semicolons are optional statement separators (no ASI rule set needed,
    /// since every statement is unambiguously recognisable by its start token).
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

        /// <summary>The library last set via `#extern "libName"` -
        /// is passed to every following `extern` declaration (see
        /// ExternDecl.LibName) until a further `#extern` directive
        /// changes it. Purely parser-internal, no runtime meaning.</summary>
        private string? _currentExternLib;

        /// <summary>The `#using` names of THIS source (SPEC "Namespaces") -
        /// come ready from the preprocessor (see Parsing.ProcessedSource),
        /// are taken over here once BEFORE ParseProgram() and no longer change
        /// during parsing (unlike _currentNamespace).
        /// Every TypeRef that this parser constructs gets them copied into its
        /// own Namespaces field (see CurrentNamespaces) -
        /// thus resolver/compiler later carry no usings state of their own around at all,
        /// every reference knows by itself
        /// where it comes from.</summary>
        private IReadOnlyList<string> _usingNamespaces = Array.Empty<string>();

        /// <summary>The namespace in which parsing is CURRENTLY taking place (SPEC
        /// "Namespaces") - `null` outside any `namespace` block.
        /// Nested blocks append with '.' (see
        /// ParseNamespaceDecl: saves the old value, sets the new one,
        /// writes the old value back on leaving the block - so
        /// the parser knows, when building EVERY TypeRef/EVERY declaration, exactly
        /// "where" in the program parsing is currently taking place, without needing a separate
        /// tree pass afterwards).</summary>
        private string? _currentNamespace;
        private string? _currentClassName;

        /// <summary>`true` while the body of a GENERIC class
        /// is being parsed (see ParsePropertyBody: static auto-property).</summary>
        private bool _currentClassIsGeneric;

        /// <summary>Index of this source in the `sources` list that went to
        /// ParseMultiple (0 = usually the prelude) - set ONCE per
        /// parser instance (see ParseMultiple, every source gets
        /// its OWN, fresh parser instance), no longer changes during
        /// parsing (unlike `_currentNamespace`). Lands directly
        /// in `Ast.ClassDecl.SourceIndex` - basis for Compiler.
        /// CurrentSourceIndex/Bytecode.Chunk.MarkLine: a debugger (see
        /// editor sub-project) needs that in order to know, with several source files,
        /// in WHICH file a given line number lies - a
        /// bare line alone is then ambiguous.</summary>
        private int _sourceIndex;

        /// <summary>Stack of the synthetic target-variable names of active
        /// `with` blocks (innermost last) - see ParseWithStmt. A
        /// bare '.' at the start of an expression (see ParsePrimary) always refers
        /// to the INNERMOST enclosing `with` block.</summary>
        private readonly Stack<string> _withVarStack = new();
        private int _withCounter;

        /// <summary>Counter for the synthetic target variable of a `switch`
        /// (see ParseSwitchStmt) - unlike with `with`, switch needs
        /// NO stack (no '.'-like implicit target that refers to the
        /// innermost enclosing switch), only unique names for
        /// nested switches.</summary>
        private int _switchCounter;

        // Counts open '(' / '['. Inside an open bracket a
        // line break is NOT to act as a statement separator (multi-line
        // function calls/argument lists/conditions must continue to
        // work) - only '{'/'}' deliberately do not count, since inside
        // a block line breaks mark exactly the statement boundaries.
        private int _bracketDepth;

        /// <summary>Non-zero while the TOP-LEVEL value expression of a
        /// switch case condition is being parsed (see ParseSwitchStmt) -
        /// suppresses EXACTLY at this bracket depth (`_bracketDepth`)
        /// the normal ':' postfix handling (unit coercion, see
        /// ParsePostfix), since 'case 2:' would otherwise wrongly swallow the ':' as a
        /// coercion operator before ParseSwitchStmt can consume it itself
        /// as a branch separator. As soon as a nested
        /// '('/'[' is entered, `_bracketDepth` rises above this value -
        /// ':' therefore keeps working quite normally there (e.g.
        /// 'case (x : mm):' - the INNER ':' is intended coercion).</summary>
        private int? _suppressColonPostfixAtDepth;

        public Parser(List<Token> tokens)
        {
            _tokens = tokens;
        }

        /// <summary>Simple redirect to ParseMultiple for tests/callers
        /// that need no namespaces/usings - NO parse logic of its own
        /// (a single ProcessedSource with empty usings is only the
        /// special case "one source, no `#using`"). `source` does NOT run
        /// through the preprocessor - whoever needs `#include`/`#using` calls
        /// Preprocessor.Process themselves and passes its result on to
        /// ParseMultiple.</summary>
        public static List<Stmt> Parse(string source) =>
            ParseMultiple(new[] { new ProcessedSource(source, Array.Empty<string>()) });

        /// <summary>Parses ANY NUMBER of already preprocessed source text
        /// pieces (see Preprocessing.ProcessedSource - each carries its
        /// OWN `#using` names collected by the preprocessor) into ONE
        /// combined program. `#using` applies here ONLY LOCALLY to
        /// declarations AND references from EXACTLY THE ProcessedSource that
        /// contained the directive itself (SPEC "Multiple source files") - with
        /// several files, potentially from different authors,
        /// a program-wide effect would be surprising.
        ///
        /// `sources` in the order in which they are to be combined
        /// (usual: prelude first, then library/helper files, the
        /// actual main script last - the order itself has no meaning for
        /// class resolution, only for overview).
        ///
        /// EVERY type reference (TypeRef, `new X()`, `is of X`, `catch (X e)`,
        /// base classes, `class extends X`) carries its own namespace
        /// context directly on itself, set EXACTLY when it is
        /// parsed (see CurrentNamespaces) - resolver/compiler
        /// thus need no usings state of their own any more, every
        /// reference knows itself how to resolve. This also applies
        /// to `class extends X`, even if extension and target class come from
        /// different files/namespaces (see
        /// Ast.ClassExtensionDecl.TargetRef documentation).</summary>
        public static List<Stmt> ParseMultiple(IReadOnlyList<ProcessedSource> sources)
        {
            var combined = new List<Stmt>();
            //var byStmt = new Dictionary<Stmt, int>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < sources.Count; i++)
            {
                var src = sources[i];
                List<Stmt> stmts;
                try
                {
                    var tokens = new Lexer(src.Source).Tokenize();
                    var parser = new Parser(tokens);
                    parser._usingNamespaces = src.Usings;
                    parser._sourceIndex = i;
                    stmts = parser.ParseProgram();
                }
                catch (ParseException ex) when (src.Name != null && ex.FileName == null)
                {
                    throw new ParseException(ex.RawMessage, ex.Line, ex.Column, src.Name);   // a program of several files: say which one
                }
                //foreach (var stmt in stmts)
                //    byStmt[stmt] = i;
                combined.AddRange(stmts);
            }
            //sourceIndexByStmt = byStmt;
            // Disambiguation BEFORE the extensions: `class extends Box` means
            // (like every reference without type arguments) the non-generic
            // class, see GenericClassNames.
            return MergeClassExtensions(DisambiguateGenericClasses(FlattenNamespaceWrappers(combined)));
        }

        /// <summary>Gives every GENERIC class next to which a
        /// NON-generic class with the same (fully qualified) name
        /// exists, its internal name `Name`N` (see
        /// GenericClassNames) - only here, after parsing ALL sources,
        /// does one know whether such a collision exists (every source is read by
        /// a parser instance of its own). Without a collision the
        /// program stays unchanged. Two generic classes with the same name
        /// AND the same number of type parameters remain a double definition
        /// (the resolver reports it), same name with a different
        /// count without a non-generic class likewise - only "generic
        /// next to non-generic" is deliberately allowed.</summary>
        private static List<Stmt> DisambiguateGenericClasses(List<Stmt> program)
        {
            // Classes and interfaces have separate name tables: `Command` and `Command<T>` collide, `ICommand` and `ICommand<T>` likewise
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

        /// <summary>The namespaces applying to a NOW parsed TypeRef/a NOW parsed
        /// reference (SPEC "Namespaces") - in first
        /// place the current namespace (_currentNamespace, if there is one),
        /// then the `#using` names of this source (_usingNamespaces). Without an
        /// enclosing namespace (top level) the list contains only the
        /// usings. The order already encodes the priority (current
        /// namespace before `#using`) - see TypeRef.ResolveBaseName.</summary>
        private IReadOnlyList<string> CurrentNamespaces() =>
            _currentNamespace == null
                ? _usingNamespaces
                : new[] { _currentNamespace }.Concat(_usingNamespaces).ToList();

        /// <summary>Appends `simpleName` to the current namespace (SPEC
        /// "Namespaces") - `_currentNamespace + "." + simpleName`, or
        /// `simpleName` unchanged outside any `namespace` block. For
        /// class/interface/enum NAMES THEMSELVES (not for references
        /// to them, see TypeRef.ResolveBaseName), applied directly when parsing the
        /// respective declaration.</summary>
        private string QualifyDeclName(string simpleName) =>
            _currentNamespace == null ? simpleName : _currentNamespace + "." + simpleName;

        /// <summary>Merges all `class extends X { ... }` extensions (see
        /// Ast.ClassExtensionDecl) into their respective target class, BEFORE
        /// resolver/compiler see the program at all - the new
        /// members land 1:1 in the ORIGINAL ClassDecl.Members list,
        /// as if they had stood there from the beginning (Ruby-like
        /// "reopening"). Several extensions of the same target class are
        /// all merged, in the order in which they
        /// occur in the program. Throws if an extension names a class that is
        /// NOT found in the same (passed) program.</summary>
        public static List<Stmt> MergeClassExtensions(IReadOnlyList<Stmt> program)
        {
            var extensions = new List<ClassExtensionDecl>();
            var baseTypeExtensions = new Dictionary<string, List<ClassExtensionDecl>>();
            var rest = new List<Stmt>();
            foreach (var stmt in program)
            {
                // `class extends string { ... }`: no ClassDecl to merge into - all
                // extensions of the same base type are turned below into ONE collective class (see
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

            // TargetRef.ResolveBaseName (SPEC "Namespaces") needs the set of
            // ALL class names in the (already namespace-flat) program -
            // TargetRef carries its OWN namespace context (that of the
            // extension itself, not that of the target class, see
            // Ast.ClassExtensionDecl documentation), so an extension finds
            // its target class also across file/namespace boundaries.
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
                        // IMPORTANT: ClassDecl is a record - 'with' creates
                        // a NEW, immutable copy, does not change 'cd'
                        // in place. The merged copy must therefore go explicitly
                        // into the result list - a simple 'result.Add(stmt)'
                        // here would again insert the old, unchanged ClassDecl
                        // and discard the additional members.
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

        /// <summary>Flattens all NamespaceDecl nodes trivially (see
        /// Ast.NamespaceDecl documentation) - NO renaming/qualification any more
        /// needed (that already happened when parsing itself, see
        /// ParseNamespaceDecl/QualifyDeclName/CurrentNamespaces), only
        /// simple recursive unpacking of the members in place of the
        /// wrapper node.</summary>
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
                // 'catch threads(...)'/'catch terminate(...)' are GLOBAL
                // declarations (see ParseGlobalHandlerDecl), no
                // normal statements - recognised only here at top-level,
                // so that they do not collide with the "catch without try" feature inside
                // blocks (see the documentation there).
                if (Check(TokenType.Catch) && NextLooksLikeGlobalHandler())
                    statements.Add(ParseGlobalHandlerDecl());
                else
                    statements.Add(ParseStatement());
            }
            return statements;
        }

        /// <summary>After 'catch' follows either 'terminate' (real keyword)
        /// or the context-dependent identifier 'threads' - both unambiguously
        /// distinguishable from a normal 'catch (...)', which ALWAYS continues directly
        /// with '('.</summary>
        private bool NextLooksLikeGlobalHandler() =>
            PeekAt(1).Type == TokenType.Terminate ||
            (PeekAt(1).Type == TokenType.Identifier && PeekAt(1).Lexeme == "threads");

        // -----------------------------------------------------------
        // Statements
        // -----------------------------------------------------------
        private Stmt ParseStatement()
        {
            // `probe a.b changed ...` / `silence a.b`: context-dependent keywords - only if an identifier/`this` follows directly (two names
            // in a row are otherwise never a valid expression), so `probe`/`silence` stay usable as variable names
            if (Check(TokenType.Identifier) && Peek().Lexeme == "silence" && IsProbeOperandNext()) return ParseSilence();
            if (Check(TokenType.Identifier) && Peek().Lexeme == "probe" && IsProbeOperandNext())
            {
                int probeLine = Peek().Line;
                var probe = ParseProbe();
                ExpectStatementTerminator();
                return new ExprStmt(_sourceIndex, probeLine, probe);
            }
            // `delete x`: context-dependent like `probe`/`silence` - only if a name/`this` follows directly (on the same line)
            if (Check(TokenType.Identifier) && Peek().Lexeme == "delete" && PeekAt(1) is { NewlineBefore: false, Type: TokenType.Identifier or TokenType.This or TokenType.Star })
                return ParseDelete();
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
            // 'try sync'/'try sync flat'/'try process'/'try Name(...)' are
            // EXPRESSIONS (see ParseSyncExpr/ParsePrimary), no try/catch
            // block - a try/catch block ALWAYS has a '{' directly after
            // 'try' (see ParseTry: ParseBlock() immediately after), every
            // other expression form NEVER - therefore this ONE positive
            // check suffices to separate the two cleanly (instead of having to
            // enumerate every single expression form here).
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
        /// Parses a block. If the block meets a "bare" `catch` (without a
        /// preceding `try`), a try region implicitly applies from there to the
        /// end of the block: the remaining statements are parsed recursively as a protected
        /// block and wrapped, together with the catch clauses collected before
        /// (and optional finally), into a TryStmt that closes the block.
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
                    break; // ParseImplicitCatchTail has already consumed the rest of the block.
                }
                statements.Add(ParseStatement());
            }

            Expect(TokenType.RBrace, "Expected '}' at the end of the block");
            return new Stmt.BlockStmt(_sourceIndex, line, statements);
        }

        /// <summary>Parses one or more `catch` clauses without a preceding `try`
        /// and treats the rest of the current block as a protected region.</summary>
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

            // `catch (TypeName varName)` or untyped `catch (varName)` -
            // SINCE SPEC "Unit declarations" the same order as
            // everywhere else (type/`var` first, name afterwards), NO longer the
            // old "name first, type via ':' afterwards" notation (which was exactly
            // the ambiguity that the ':' now resolves uniformly everywhere
            // only for units). NextLooksLikeTypeThenName()
            // also recognises a dotted type name like
            // 'Geometry.MyException e' correctly (see the documentation there).
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

        /// <summary>`readonly var ...` or `readonly Type name ...` - the
        /// resolver afterwards forbids any further assignment (see
        /// Resolver.ResolveAssignTarget). A purely parser-side switch for which
        /// of the two declaration forms is stamped with the flag.</summary>
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

        /// <summary>var declaration without consuming its own terminator - for places
        /// like the for-init, where the caller expects the separating ';' itself
        /// (otherwise one would already be "swallowed" here and the caller afterwards
        /// wrongly expect a second one).</summary>
        private VarDeclStmt ParseVarDeclCore(bool isReadonly)
        {
            int line = Peek().Line;
            Expect(TokenType.Var, "Expected 'var'");
            string name = Expect(TokenType.Identifier, "Expected a variable name").Lexeme;

            // Parse array brackets BEFORE the type (directly behind the identifier) -
            // otherwise `var arr : int[]` would wrongly try to read "[]" as a
            // bit-width bracket behind 'int' (collision, see 'new').
            var arrayRanks = ParseArrayRanks();

            // The ':' after the name ALWAYS fixes only a UNIT, NEVER
            // a type (SPEC "Unit declarations") - `var a : mm` means
            // "type derived from the initialiser as usual, unit
            // is FIXED mm", not "type is mm". The actual type thus stays
            // `null` for `var` (inference by the resolver), unless
            // a unit is given - then a TypeRef with
            // TypeRef.InferredMarker as BaseName carries ONLY the unit (see
            // TypeRef.IsInferred documentation).
            TypeRef? type = null;
            if (Match(TokenType.Colon))
                type = new TypeRef(TypeRef.InferredMarker, null, 0, Unit: ParseUnitName());

            Expr? initializer = null;
            if (Match(TokenType.Assign))
                initializer = ParseExpression();

            return new VarDeclStmt(_sourceIndex, line, name, type, arrayRanks, initializer, isReadonly);
        }

        /// <summary>Reads a unit after ':' (SPEC "Unit declarations") -
        /// deliberately ONLY a single identifier (e.g. "mm"), NEVER a full
        /// TypeRef (no bit width, no pointer, no namespace path) - exactly
        /// that was the earlier ambiguity: the ':' used to be resolved via
        /// ParseTypeRef(), so it could (wrongly) look like a
        /// second, alternative way of specifying the TYPE. The unit itself
        /// is NOT checked here against a known list (Values.Unit.Parse
        /// accepts every identifier as an atomic, freely invented unit,
        /// see the documentation there) - a possible check "is mm really
        /// known yet" would be moot anyway.</summary>
        private string ParseUnitName() => Expect(TokenType.Identifier, "Expected a unit name after ':'").Lexeme;

        /// <summary>"Bare" declaration without `var` (C-like): `Type name[ranks]
        /// [: unit] [= init]`. Semantically identical to `var name : Type`
        /// (except for the additional, optional unit), only a different
        /// surface syntax - represented as the same VarDeclStmt. Only
        /// reachable if NextLooksLikeTypeThenName() has already confirmed
        /// that a type really follows here (and not e.g. an expression
        /// statement).</summary>
        private Stmt ParseBareTypedDecl(bool isReadonly = false)
        {
            int line = Peek().Line;
            var type = ParseTypeRef();
            string name = Expect(TokenType.Identifier, "Expected an identifier").Lexeme;
            var arrayRanks = ParseArrayRanks();

            // As with ParseVarDeclCore: ':' ALWAYS fixes only a unit
            // (SPEC "Unit declarations") - here the type is (unlike
            // with `var`) already explicitly there, so `int a : mm` has BOTH
            // at once: a fixed type AND a fixed unit.
            if (Match(TokenType.Colon))
                type = type with { Unit = ParseUnitName() };

            Expr? initializer = null;
            if (Match(TokenType.Assign))
                initializer = ParseExpression();

            ExpectStatementTerminator();
            return new VarDeclStmt(_sourceIndex, line, name, type, arrayRanks, initializer, isReadonly);
        }

        /// <summary>Type name after a ':' – either one of the base-type keywords
        /// or an identifier (class name). Only the name, without bit width/pointer -
        /// for contexts that (so far) need only a pure name (`is of`,
        /// base class, catch type).</summary>
        /// <summary>Reads a type name - either a type keyword (int/
        /// float/...) or an identifier, optionally followed by one or
        /// several '.'-separated further identifiers (namespace-
        /// qualified name, e.g. 'Foo.Bar' - see Ast.NamespaceDecl/
        /// SPEC "Namespaces"). A '.' is consumed here ONLY if an identifier
        /// follows directly after it - this language otherwise knows no
        /// position at which a type name itself (not an expression) could be
        /// followed by a '.', so the lookahead is purely
        /// defensive.</summary>
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

        /// <summary>Complete type reference: base name, optionally `[bit width]`
        /// directly behind it (only sensible for int/float, checked by the resolver),
        /// optionally followed by one or more '*' for pointer depth.</summary>
        /// <summary>Complete type reference: base name, optionally `[bit width]`
        /// directly behind it (only sensible for int/float, checked by the resolver),
        /// optionally followed by one or more '*' for pointer depth -
        /// OR, if the name (or the name directly after it) is 'lambda',
        /// a lambda type with an optional signature (see ParseLambdaSignature/
        /// Ast.TypeRef.LambdaSignature documentation): `[ReturnType] lambda[&lt;P1,...,Pn&gt;]`
        /// - the return type stands BEFORE 'lambda', not in the angle
        /// brackets (which contain only the parameter types), that makes the
        /// grammar unambiguous without needing a separator between return and
        /// parameter types.</summary>
        private TypeRef ParseTypeRef(bool allowArray = false)
        {
            string baseName = ParseTypeAnnotationName();
            var namespaces = CurrentNamespaces();

            if (baseName != "lambda" && Check(TokenType.Identifier) && Peek().Lexeme == "lambda")
            {
                Advance(); // consume 'lambda' - 'baseName' was in truth the return type
                return new TypeRef("lambda", null, 0, ParseLambdaSignature(returnTypeName: baseName), namespaces);
            }

            if (baseName == "lambda")
            {
                // `lambda field|property|member|selector<T>`: a selector (see LambdaSignature.IsSelector)
                if (Check(TokenType.Identifier) && Peek().Lexeme is "field" or "property" or "member" or "method" or "selector" && PeekAt(1).Type == TokenType.Lt)
                {
                    string selectorKind = Advance().Lexeme; // 'field', 'property', 'member', 'method' or 'selector'
                    Advance(); // '<'
                    var targetTypes = new List<string>();
                    if (!Check(TokenType.Gt)) targetTypes.Add(ParseTypeAnnotationName()); // `lambda selector<>`: without a type
                    Expect(TokenType.Gt, $"Expected '>' after the type of 'lambda {selectorKind}<...>'");
                    return new TypeRef("lambda", null, 0, new LambdaSignature(null, targetTypes, IsSelector: true, SelectorKind: selectorKind), namespaces);
                }
                return new TypeRef("lambda", null, 0, ParseLambdaSignature(returnTypeName: null), namespaces);
            }

            // 'byte' is pure sugar for 'int[8]' (see SPEC 8.10) - an
            // explicit bit width AFTER it would be contradictory/redundant and
            // is therefore rejected, instead of silently ignoring
            // or overriding it.
            if (baseName == "byte")
            {
                if (Check(TokenType.LBracket) && !NextIsEmptyBrackets())
                    throw Error("'byte' already has a fixed width of 8 bits - no additional '[...]' may follow", Peek());
                int bytePointerDepth = 0;
                while (Match(TokenType.Star)) bytePointerDepth++;
                return new TypeRef("int", 8, bytePointerDepth, Namespaces: namespaces, ArrayRank: ParseArrayTypeSuffix(allowArray));
            }

            // `[8]` = bit width; empty brackets `[]` belong to the array return type (see below).
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

        /// <summary>Does `[` `]` (empty brackets) come next? That is an array type
        /// (`int[]`), not a bit width (`int[8]`).</summary>
        private bool NextIsEmptyBrackets() =>
            Check(TokenType.LBracket) && PeekAt(1).Type == TokenType.RBracket;

        /// <summary>Reads the empty bracket pairs of an array TYPE (`int[]`, `Dog[][]`) and returns their
        /// count. Only allowed where the type has no identifier behind which the brackets would stand
        /// (return types) - everywhere else `Type name[]` applies, and the error says so.</summary>
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

        /// <summary>Optional `&lt;Param1,...,ParamN&gt;` parameter list after
        /// 'lambda' - empty (parameterless) if no '&lt;' follows. Every
        /// entry is a pure type name (see TypeRef.LambdaSignature documentation
        /// for the reason why no recursive TypeRefs).</summary>
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

        /// <summary>Array declarator AFTER the identifier: one or more
        /// `[...]` groups, each with an optional size expression (`[5]`) or empty
        /// (`[]`, undetermined size). Empty list = no array.</summary>
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

        /// <summary>Heuristic whether "type then name" follows at this point (for
        /// fields/methods/parameters, where the type is optional): either a
        /// base-type keyword (afterwards bit width/pointer/array can still follow),
        /// or two consecutive identifiers (class name + member name).
        /// A class name as a pointer/array type in this position is thus
        /// deliberately not (yet) covered - a rare/advanced case
        /// that can be retrofitted if needed.</summary>
        /// <summary>Recognises "a type name (possibly dotted) starts here, followed
        /// by a further identifier" - for the decision "declaration
        /// or something else" at EVERY place where both would come into
        /// question syntactically (top-level/local statement, field, parameter, method/
        /// extern return type).
        ///
        /// For that it skips the ENTIRE dotted chain (Geometry.Sub.Circle
        /// ...) and looks at what comes AFTER - only if THAT is again an
        /// identifier was the chain a TYPE NAME followed by the actual
        /// declaration name. This is no mere heuristic, but unambiguous:
        /// two identifiers IMMEDIATELY in a row occur in NO
        /// valid expression (that would always need an operator/a
        /// bracket/a dot in between) - 'Geometry.Function()' has a '(' after
        /// the chain, no identifier (expression call), 'Geometry.
        /// Circle x', by contrast, does (declaration). Class-member accesses
        /// like 'Geometry.Circle.Radius' are independent of that - they run
        /// via the normal postfix chain ('.' accesses), as soon as the first
        /// part was recognised as an ordinary expression (not as a
        /// declaration).</summary>
        private bool NextLooksLikeTypeThenName()
        {
            if (TypeKeywords.Contains(Peek().Type)) return true;
            // 'lambda' as a type name (see ParseTypeRef) can - unlike a
            // class name - also be followed by '<' instead of a further
            // identifier ('lambda<int> x'), which the chain check
            // below would miss.
            if (Check(TokenType.Identifier) && Peek().Lexeme == "lambda") return true;
            if (!Check(TokenType.Identifier)) return false;

            int offset = 1;
            while (PeekAt(offset).Type == TokenType.Dot && PeekAt(offset + 1).Type == TokenType.Identifier)
                offset += 2;
            // `Dog[] Name`: empty brackets behind the type name (array return type) - like two identifiers
            // in a row, `Name[] Name` occurs in no valid expression.
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

        private Stmt ParseDelete()
        {
            int line = Peek().Line;
            Advance();   // delete
            var target = ParseExpression();
            ExpectStatementTerminator();
            return new DeleteStmt(_sourceIndex, line, target);
        }

        private Stmt ParseThrowStmt()
        {
            int line = Peek().Line;
            Expect(TokenType.Throw, "Expected 'throw'");
            var value = ParseExpression();
            ExpectStatementTerminator();
            return new ThrowStmt(_sourceIndex, line, value);
        }

        /// <summary>`extern [ReturnType] Name(params)` - declares a native
        /// function signature without a body. ReturnType missing -> no return value.
        /// Gets stamped with the library last set via `#extern "libName"`
        /// (see ParseDirective/_currentExternLib) - null if
        /// no such directive stood before this declaration.</summary>
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

        /// <summary>Preprocessor directives, currently `#extern "libName"` and
        /// `#noshadow` - sets either the library against which
        /// FOLLOWING `extern` declarations in the source are stamped
        /// (until the next `#extern` directive or end of file),
        /// or (see Ast.NoShadowDirective documentation) switches off the
        /// read-only globals snapshot in `fire` blocks. `#extern`
        /// generates no AST node of its own relevant at runtime (NoOpStmt) -
        /// acts purely when parsing; `#noshadow`, by contrast, generates an
        /// own node that the resolver collects in a pre-pass
        /// (see there).</summary>
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

            // '#using' is from now on purely a preprocessor matter (see
            // Preprocessing.Preprocessor.ProcessInner/ProcessedSource) - a
            // '#using' line is already recognised there and removed from the text,
            // the parser never sees it any more. No case for it needed here any more.
            throw Error($"Unknown preprocessor directive '#{Peek().Lexeme}' (known: '#extern \"libName\"', '#noshadow', '#nosync', '#timeout value')", Peek());
        }

        private Stmt ParseUnsafeStmt()
        {
            int line = Peek().Line;
            Expect(TokenType.Unsafe, "Expected 'unsafe'");
            var body = ParseBlock();
            return new UnsafeStmt(_sourceIndex, line, body);
        }

        /// <summary>`with expression { .Field = x; .Method() }` (BASIC-like) -
        /// pure sugar that the parser resolves completely: the `with`
        /// expression is written ONCE into a synthetic, block-wide valid
        /// variable, and every '.' at the start of an expression
        /// INSIDE the block (see ParsePrimary, TokenType.Dot case)
        /// refers implicitly to exactly this variable. Resolver/compiler
        /// afterwards see only quite ordinary `IdentifierExpr`/`MemberExpr`
        /// nodes - no runtime support of its own needed. Compiled as a `Stmt.BlockStmt` of its own,
        /// so that the synthetic variable gets an
        /// isolated scope (does not collide with variables
        /// of the same name before/after, is released again when leaving the block
        /// ) - `_withVarStack` allows arbitrary
        /// nesting here (a '.' always refers to the INNERMOST
        /// enclosing `with` block).</summary>
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
                    break; // ParseImplicitCatchTail has already consumed the rest of the block.
                }
                statements.Add(ParseStatement());
            }
            Expect(TokenType.RBrace, "Expected '}' at the end of the with block");

            // Only tear down AFTER parsing the body - nesting
            // (with a { with b { ... } }) still needs the outer entry
            // while the inner body is parsed, but not afterwards.
            _withVarStack.Pop();

            return new Stmt.BlockStmt(_sourceIndex, line, statements);
        }

        /// <summary>`switch(expression) { case OP value: ... break; case default: ... }`
        /// - pure sugar like `with`, completely desugared
        /// into an if/else-if chain: the switch expression is written ONCE into a synthetic
        /// variable, every `case` branch becomes a condition
        /// `__switchN__ OP value` (OP missing -> `==`, as with a classic
        /// switch), `case default` becomes the final `else`. Resolver/
        /// compiler/VM see nothing of it - normal If/BinaryExpr nodes.
        /// Unlike a classic C switch: NO fallthrough (every branch
        /// is an own, exclusive if condition, no jump into the next one),
        /// `break` is therefore a purely syntactic branch end (see
        /// ParseSwitchCaseBody), no real jump statement.</summary>
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

            // Build the if/else-if chain from BACK to FRONT - 'case default'
            // (if present) becomes the innermost 'else', otherwise it stays null
            // (none of the cases applies -> switch simply does nothing).
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

        /// <summary>Parses the value expression of a case condition with
        /// suppressed ':' postfix handling at THIS bracket depth
        /// (see _suppressColonPostfixAtDepth documentation) - otherwise e.g.
        /// 'case 2:' would wrongly attach the ':' as a unit coercion to the value
        /// '2', instead of leaving it to the switch as the branch separator.</summary>
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

        /// <summary>Statements of ONE case/default branch - up to the next
        /// 'case', '}', OR a 'break' at the top level of THIS branch.
        /// 'break' is consumed as the branch END (no statement of its own,
        /// no real jump statement - see ParseSwitchStmt
        /// documentation) and must therefore, if used, be the LAST statement of the
        /// branch; everything after it would no longer belong to this branch.
        /// A 'break' INSIDE a nested loop/a
        /// nested block in this branch is not touched by that -
        /// it is already consumed along with the recursive ParseStatement() call for the
        /// loop/the block, before this loop here
        /// gets a turn at all.</summary>
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
        // Multithreading (docs/THREADING_DESIGN.md) - first development stage:
        // only `fire { ... }` / `fire taking X { ... }`.
        // -----------------------------------------------------------

        /// <summary>`fire { ... }` / `fire taking X { ... }` - see
        /// Ast.FireStmt documentation for the scope of this development stage (deliberately still
        /// without the `fire MethodA()` call form, without `with actorA`).</summary>
        /// <summary>`fire { ... }` / `fire taking X { ... }` / `fire with actorA { ... }`
        /// / `fire MethodA(args) ...` - see Ast.FireStmt documentation for the full
        /// scope. The call form (identifier directly followed by '(') is recognised
        /// BEFORE the taking/with clauses, because it itself has NO
        /// opening '{' after it (see ParseFireCallForm).</summary>
        private Stmt ParseFireStmt()
        {
            int line = Peek().Line;
            Expect(TokenType.Fire, "Expected 'fire'");

            if (Check(TokenType.Identifier) && PeekAt(1).Type == TokenType.LParen)
                return ParseFireCallForm(line);

            // `fire global { ... }`: job for the main program instead of a new thread (docs/THREADING_DESIGN.md section 7)
            if (Check(TokenType.Identifier) && Peek().Lexeme == "global" && (PeekAt(1).Type == TokenType.LBrace || PeekAt(1).Type == TokenType.Taking))
                return ParseFireGlobal(line);

            var (takingCaptures, withVarName, withSource) = ParseFireTakingWithClauses();
            var body = ParseBlock();
            return new FireStmt(_sourceIndex, line, takingCaptures, withVarName, withSource, body);
        }

        /// <summary>`sync global { ... }` (docs/THREADING_DESIGN.md section 7): a block that runs with exclusive access to the globals.
        /// Desugared to `SectionEnter; try { Body } finally { SectionExit }` - the section thus also ends on a `throw` in the block.</summary>
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

        /// <summary>`taking X`/`with actorA`, in any order, `with`
        /// at most once, `taking` repeatable any number of times (see
        /// Ast.FireStmt.TakingCaptures documentation for the slot order).</summary>
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
                    // References the variable of the same name ALREADY declared
                    // in the enclosing scope - quite normal identifier
                    // resolution in the CALLING context (not in the isolated
                    // fire block scope, see Resolver.ResolveFireStmt).
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

        /// <summary>`fire MethodA(args) [taking/with clauses]` (see
        /// Ast.FireStmt documentation for the exact desugaring) - pure
        /// parser sugar: `this` and every argument are treated like additional
        /// `taking` targets (under internal names that cannot be
        /// written in user code, see ThisAndArgCaptureNamePrefix), the body
        /// consists of exactly one call of the taken method on the
        /// taken `this` copy with the taken argument copies. No
        /// resolver/compiler/VM code of its own needed - the resulting node
        /// is for the rest of the compiler an entirely normal FireStmt.
        /// Requires 'this' in the calling context (only valid inside a
        /// method/a constructor - checked by the resolver like any
        /// other 'this', no special check needed here).</summary>
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

        /// <summary>`terminate()` / `terminate(value)` - see Ast.TerminateStmt
        /// documentation. Syntactically like a function call, but a statement of its own
        /// (not an expression) - `terminate` has no sensible
        /// "result value" that could be used further.</summary>
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

        /// <summary>`process X` (blocking, see Ast.ProcessStmt documentation).
        /// The non-blocking variant `try process X`, by contrast, is an
        /// EXPRESSION and is handled in ParsePrimary.</summary>
        private Stmt ParseProcessStmt()
        {
            int line = Peek().Line;
            Expect(TokenType.Process, "Expected 'process'");
            var target = ParsePostfix();
            ExpectStatementTerminator();
            return new ProcessStmt(_sourceIndex, line, target);
        }

        /// <summary>`catch threads(ExceptionType e) { ... }` / `catch threads() { ... }`
        /// / `catch terminate(v) { ... }` (see Ast.CatchThreadsDecl/
        /// CatchTerminateDecl documentation) - GLOBAL registration, only
        /// recognised at top-level program position (see ParseProgram), so that it does
        /// not collide with the existing "catch without try extends the block"
        /// feature (ParseBlock/ParseImplicitCatchTail) - both
        /// begin with the same 'catch' token, but are at this point
        /// already told apart by lookahead (does 'threads'/'terminate' follow?)
        /// before it is decided at all which of the
        /// two ways is parsed.</summary>
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

            // 'threads' is - like 'get'/'set'/'value' with properties - a purely
            // context-dependent identifier, no reserved keyword.
            Expect(TokenType.Identifier, "Expected 'threads' or 'terminate' after 'catch'");
            Expect(TokenType.LParen, "Expected '(' after 'catch threads'");

            TypeRef? typeRef = null;
            string? varName = null;
            if (!Check(TokenType.RParen))
            {
                // 'catch threads(ExceptionType e)' - type then name, like an
                // ordinary method parameter (and meanwhile also like with the
                // normal 'catch (TypeName varName)' - see ParseCatchClause).
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

        /// <summary>Common grammar for `class Name { ... }` and
        /// `actor Name { ... }` (see Ast.ClassDecl.IsActor documentation) - both
        /// share fields/methods/constructor/inheritance 1:1, only the
        /// `IsActor` flag on the result distinguishes them. `extends` (class
        /// extension, see ParseClassExtensionDecl/MergeClassExtensions)
        /// works identically for BOTH - `class extends ActorName { ... }`
        /// AND `actor extends ActorName { ... }` extend the same
        /// target class equally, since the extension is merged purely by NAME
        /// and leaves the `IsActor` flag of the already
        /// existing, ORIGINAL declaration untouched - which
        /// keyword one writes for the extension itself therefore
        /// plays no role.</summary>
        private Stmt ParseClassOrActorDecl(bool isActor)
        {
            int line = Peek().Line;
            if (isActor) Expect(TokenType.Actor, "Expected 'actor'");
            else Expect(TokenType.Class, "Expected 'class'");

            if (Check(TokenType.Extends))
                return ParseClassExtensionDecl(line);

            string name = Expect(TokenType.Identifier, "Expected a class name").Lexeme;

            // Generic type parameters: 'class Name<T1, T2>' - see
            // ParseTypeParamList. Empty (no '<' present) for a
            // non-generic class.
            var typeParamNames = ParseOptionalTypeParamNames();

            // Raw name list after ':' - which name (at most one) is the
            // base class and which are interfaces is decided by the
            // resolver (the parser does not yet know the class/interface table
            // ). `class Foo : Bar, IBaz, IQux` or in any
            // order, as long as at most one name is a real class.
            // Every name carries (like every TypeRef) the namespace context
            // current HERE, when parsing the class head (SPEC "Namespaces").
            var namespaces = CurrentNamespaces();
            var baseRefs = new List<TypeRef>();
            if (Match(TokenType.Colon))
            {
                do
                {
                    // Also qualified ('Geometry.Shape' - a base class in another
                    // namespace, see SPEC "Namespaces").
                    string baseName = ParseDottedName("base class/interface names");
                    // `class Home : Command<IDevice>`: the type arguments are (as everywhere) not evaluated, only their COUNT chooses the generic
                    // class or the generic interface of this name (see GenericClassNames.ResolveNewTarget).
                    int typeArgCount = ParseOptionalTypeParamNames().Count;
                    baseRefs.Add(new TypeRef(baseName, null, 0, Namespaces: namespaces, TypeArgCount: typeArgCount));
                } while (Match(TokenType.Comma));
            }

            // 'where' clauses: ONE per type parameter (C#-like), in
            // any order, all BEFORE the opening '{'. See
            // ParseWhereClause for the constraint grammar itself.
            var typeParams = ParseWhereClauses(typeParamNames, line);

            Expect(TokenType.LBrace, "Expected '{' after the class head");
            var members = new List<Stmt>();
            // For static auto-properties (see ParsePropertyBody) - the
            // synthesised backing field there is an access via
            // 'ClassName.field' instead of 'this.field' (no instance bound),
            // so it needs the QUALIFIED class name, EXACTLY as it
            // lands in ClassDecl itself right below. Saved/
            // restored instead of assigned directly, in case a class
            // were ever nested (currently not possible, but
            // robust for that case).
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

        /// <summary>`&lt;T1, T2, ...&gt;` directly after a class/method name -
        /// returns only the pure NAMES (empty list if no '&lt;'
        /// present), the constraints themselves come separately via
        /// 'where' clauses (see ParseWhereClauses). Parsed separately from the
        /// constraints, because 'class Name&lt;T&gt; : Base' needs the base class
        /// in between (C#-like order: type parameters, then
        /// base class(es), then 'where' clauses, then body).</summary>
        private List<string> ParseOptionalTypeParamNames()
        {
            var names = new List<string>();
            if (!Match(TokenType.Lt)) return names;
            do
            {
                // ParseTypeAnnotationName() instead of Expect(Identifier) - a
                // type ARGUMENT (with 'new Name<Arg>') can be a primitive type
                // like 'int'/'float' that is lexed as a KEYWORD token of its own,
                // not a TokenType.Identifier (see
                // ParseTypeAnnotationName documentation elsewhere). For
                // type PARAMETER names (at the declaration, always ordinary
                // identifiers like 'T') this is a call without difference.
                names.Add(ParseTypeAnnotationName());
                // Skip nested type ARGUMENTS ('Box<int>' as ONE argument
                // of e.g. 'new Container<Box<int>>()') purely SYNTACTICALLY,
                // WITHOUT representing them structurally - there is
                // no real generic specialisation (SPEC 5.8), the
                // constraint check (CheckTypeArgs) works anyway only
                // with the OUTER name ('Box', not 'Box<int>'), so
                // discarding the inner arguments is harmless.
                // With a type PARAMETER declaration ('class Box<T>')
                // this call is a pure no-op (T never has a '<' after it).
                SkipOptionalNestedTypeArgs();
            } while (Match(TokenType.Comma));
            Expect(TokenType.Gt, "Expected '>' after the type parameter list");
            return names;
        }

        /// <summary>See ParseOptionalTypeParamNames - consumes an
        /// optional, arbitrarily deeply nested `&lt;...&gt;` type argument list
        /// (`Box&lt;Box&lt;int&gt;&gt;`, `Box&lt;A, B&lt;C&gt;&gt;`, ...) purely syntactically, throws
        /// the content away completely. Recursive for arbitrary nesting
        /// depth. Every '&gt;' is read individually via Expect(Gt) (NEVER via
        /// the expression precedence chain/ParseShift) - hence no conflict
        /// with `&gt;&gt;` as a shift operator: for `&lt;`/`&gt;` the lexer
        /// anyway always delivers only single-character tokens (except `&lt;=`/`&gt;=`), so a
        /// `Box&lt;Box&lt;int&gt;&gt;` ends with two SINGLE `&gt;` tokens, which
        /// here quite normally one after the other (once per
        /// nesting level).</summary>
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

        /// <summary>Zero or more `where Name constraint-group (',' constraint-group)*`
        /// clauses, ONE per type parameter - each references one of the names
        /// declared in `typeParamNames` (error on unknown/
        /// duplicate name). Returns for EVERY declared type parameter
        /// a TypeParam entry, even if it has NO 'where' (then with
        /// empty ConstraintGroups - unrestricted). Pure declaration
        /// syntax; whether type parameters exist at all is decided by
        /// `typeParamNames` (empty -> no 'where' allowed either).</summary>
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

        /// <summary>A single condition: `is of Name` or `is in "unitName"`
        /// (unit name as a string literal, as in the example `is in "mm"` -
        /// not as an identifier, since unit names like `mm`/`kg` could otherwise collide with
        /// type names).</summary>
        private TypeConstraint ParseOneTypeConstraint()
        {
            Expect(TokenType.Is, "Expected 'is' in a where condition");
            if (Match(TokenType.Of))
            {
                // ParseTypeAnnotationName() instead of Expect(Identifier) - the
                // target of 'is of' can be a primitive type like 'float'
                // that is lexed as a KEYWORD token of its own (see
                // ParseOptionalTypeParamNames comment for the same trap).
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

        /// <summary>`class extends Name { new members... }` - see
        /// Ast.ClassExtensionDecl documentation. Members are parsed with the same
        /// ParseClassMember() as in a normal class (fields,
        /// methods, properties, even a further constructor/destructor -
        /// whether that makes sense on merging is only checked by the merge
        /// step, see Parser.MergeClassExtensions).</summary>
        private Stmt ParseClassExtensionDecl(int line)
        {
            Expect(TokenType.Extends, "Expected 'extends'");

            // A base type (`string`, `char`, ...) is a keyword, not an identifier - the
            // extension of a base type (SPEC 5.5.1) may contain ONLY methods.
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

        /// <summary>An extension of a base type (`class extends string { ... }`) may contain only
        /// ordinary instance methods: a base value has no storage for fields/properties,
        /// no constructor/destructor and (VM.BinaryNumericOrOperator checks only objects) no
        /// operator overloading; `static` would have no way of being called (`string.Foo()` does not exist).</summary>
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

        /// <summary>`namespace Name { members... }` or `namespace A.B { ... }`
        /// - see Ast.NamespaceDecl documentation. Members are parsed with the normal
        /// ParseStatement() (classes/interfaces/enums, also
        /// nested further `namespace` blocks), WHILE `_currentNamespace`
        /// points to this (possibly nested) namespace - every declaration/
        /// every TypeRef in it thereby qualifies/links itself correctly already when
        /// parsing itself (see QualifyDeclName/CurrentNamespaces).
        /// The old namespace name is ALWAYS written back on leaving the block
        /// (even if it was `null`), so that nested
        /// AND consecutive `namespace` blocks do not influence
        /// each other.</summary>
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

        /// <summary>Reads a (possibly multi-part, '.'-separated)
        /// name like 'A' or 'A.B.C' - for namespace names (declaration AND
        /// `#using`) used at exactly the two places where this language
        /// otherwise nowhere allows a '.' as part of a NAME itself
        /// (everywhere else '.' is the element-access operator).</summary>
        private string ParseDottedName(string what)
        {
            string name = Expect(TokenType.Identifier, $"Expected {what}").Lexeme;
            while (Match(TokenType.Dot))
                name += "." + Expect(TokenType.Identifier, $"Expected {what} after '.'").Lexeme;
            return name;
        }

        /// <summary>`interface Name { [ReturnType] Method(params) ... }` - pure
        /// method signatures, no fields/constructor/bodies.</summary>
        private Stmt ParseInterfaceDecl()
        {
            int line = Peek().Line;
            Expect(TokenType.Interface, "Expected 'interface'");
            string name = Expect(TokenType.Identifier, "Expected an interface name").Lexeme;
            // `interface ICommand<T> { ... }`: generic like a class (see ParseClassOrActorDecl); as there only name and number of type parameters count
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

        /// <summary>`enum Name { A, B = 5, C }` - see Ast.EnumDecl documentation for the
        /// semantics (pure compile-time constants). Values are only
        /// PARSED here (raw expression or missing) - the actual calculation
        /// (auto-increment, validation "must be an int literal") only happens
        /// in the resolver, which needs all members in declaration order
        /// for that.</summary>
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

        /// <summary>`Type Name { get { ... } set { ... } }` - see
        /// Ast.PropertyDecl documentation. 'get'/'set' are deliberately NOT reserved
        /// keywords (neither is 'value' in the setter body) - pure
        /// context-dependent identifiers, with special meaning only inside a property body,
        /// just as in C#. Order/count: 'get'
        /// and 'set' may stand in any order, each at most
        /// once, at least one of the two must be present.</summary>
        /// <summary>Property body: `{ get ... set ... }` with an explicit body per
        /// accessor (`get { ... }`/`set { ... }`, as before) OR as the
        /// auto-property short form (`get;`/`set;`, without a body of its own) - see
        /// SPEC "Auto-properties". Mixing is allowed (e.g. `get { ... }
        /// set;`). At least ONE accessor (explicit or auto) is mandatory.
        /// Returns a LIST of members (not only the PropertyDecl itself),
        /// since an auto-property additionally needs a synthetic backing field
        /// (see below) - the caller (ParseClassMember) appends
        /// both to the member list of the class.</summary>
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
                // Auto-property: at least one accessor is a pure 'get;'/
                // 'set;' without a body of its own - synthesises a backing field
                // with a predictable name ('_AutoName', see SPEC documentation for the
                // naming convention and its limits: a collision with a
                // field of the same name declared by the user themselves is
                // theoretically possible, deliberately not checked
                // separately here) as well as trivial get_/set_ bodies for it. The
                // backing field is a QUITE NORMAL field (no
                // special status) - code INSIDE the class (constructor
                // included) can access it directly at any time
                // ('this._AutoName'), e.g. to initialise a get-only property anyway in the
                // constructor (there is no setter via the property itself
                // for that).
                // The backing field is ALWAYS private, independent of the modifier
                // of the property itself - a pure implementation detail that is
                // meant to be reachable only via the property (get_Name/set_Name),
                // never directly from outside ('this._AutoName' stays
                // still normally allowed INSIDE the class).
                string backingName = "_Auto" + name;
                result.Add(new FieldDecl(_sourceIndex, line, type, Array.Empty<Expr?>(), backingName, null, IsReadonly: false,
                    Access: AccessModifier.Private, IsStatic: isStatic));

                // Static: backing field via 'ClassName.field' instead of
                // 'this.field' (no instance bound, see
                // ParseClassOrActorDecl for _currentClassName) - both run
                // via the same MemberExpr node, only the target
                // differs (ThisExpr vs. an identifier with the
                // class name, which the resolver recognises as a static access,
                // see Resolver.TryResolveStaticMemberAccess).
                //
                // In a GENERIC class SelfClassExpr instead: the
                // class name alone could point there to a non-generic class of the
                // same name (see GenericClassNames).
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

        /// <summary>Reads an optional access modifier (`public`/
        /// `private`/`protected`) directly before a class member - default
        /// `Public` if none was given (SPEC "Access modifiers",
        /// see AccessModifier documentation). At most ONE allowed (no
        /// `public private ...`) - a second modifier would simply fail as the
        /// next token (type name/method name), no
        /// separate error case needed.</summary>
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

            // 'static' on fields/methods/properties (SPEC "Static
            // members") - ONE shared storage location per class instead of
            // per instance, callable as 'ClassName.Member' instead of
            // 'instance.Member' (see Resolver/VM.GetStaticField etc.).
            // Order fixed [access] [static] [readonly] - the most usual
            // notation ('public static readonly'), no other
            // orders specially supported (simplicity).
            bool isStatic = Match(TokenType.Static);
            bool isReadonly = Match(TokenType.Readonly);

            // SPEC "Unit declarations": `var` is valid as with local
            // variables/parameters - `var` alone (type + possibly unit
            // derived from the initialiser/':') OR an explicit type.
            TypeRef? type = null;
            if (Check(TokenType.Var))
            {
                Advance();
                type = new TypeRef(TypeRef.InferredMarker, null, 0, Namespaces: CurrentNamespaces());
            }
            else if (NextLooksLikeTypeThenName())
            {
                // `int[] Name()`: an array return type (method/property) - for a FIELD the
                // check below catches that (there the brackets stand behind the name).
                type = ParseTypeRef(allowArray: true);
            }

            string name = Expect(TokenType.Identifier, "Expected a field or method name").Lexeme;

            // Generic method: 'Name<T>(...) where T constraint { ... }' -
            // a '<' directly after the name is unambiguously valid here ONLY as a
            // type parameter list (a field could never sensibly have a '<'
            // at this point - see the FieldDecl path below, which expects only
            // '[' (array) or '='), therefore parsed here without look-back risk.
            // Only with EXPLICIT use: no effect on
            // normal, non-generic methods.
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

            // As with var/parameter declarations: ':' ALWAYS fixes only a
            // unit, never a type (SPEC "Unit declarations").
            // As with parameters: if an explicit type/`var` is missing before it, but
            // a unit is there, it counts implicitly like `var`.
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

        /// <summary>`operator SYMBOL(params) { body }` - operator overloading
        /// (see RuntimeClass convention: internally quite ordinary methods with
        /// a special name not directly callable from script code itself,
        /// see ParseOperatorSymbol). `operator[]` is purely
        /// parser sugar for the ALREADY EXISTING `GetIndex`/`SetIndex`
        /// naming convention (see VM.OpCode.ArrayGet/ArraySet) - distinguished
        /// purely by the parameter count (1 = reading, 2 = writing: index,
        /// value), exactly like any other method overload by arity in
        /// this language. All other operators (`+`/`-`/etc.) expect
        /// EXACTLY ONE parameter (the right operand - `this` is implicitly
        /// the left one) and are called by the respective VM opcode handlers
        /// (BinaryNumericOrOperator) when the LEFT operand is an
        /// object with a matching method - an overload only for the
        /// RIGHT operand (like C#'s `operator+` with swapped operand
        /// types, or Python's `__radd__`) is deliberately NOT supported.</summary>
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

        /// <summary>Reads a single, overloadable operator symbol directly
        /// after 'operator'. `<<`/`>>` as with ParseShift: two consecutive
        /// single-character tokens (the lexer does not know them as
        /// two-character tokens of their own, see the documentation there) - checked here BEFORE the
        /// individual `<`/`>` cases, otherwise `operator<<` would wrongly
        /// be read as `operator<` already after the FIRST `<`.</summary>
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

        /// <summary>A single parameter, in both notations (as with
        /// `var`): `[Type] Name` (type first, existing order) OR
        /// `Name [: Type]` (var-like order) - in each case optionally followed
        /// by `= default value`. Which order is present is decided by
        /// NextLooksLikeTypeThenName() exactly as with variable declarations.</summary>
        /// <summary>A parameter: `[Type|var] name[ranks] [: unit] [= default]`
        /// (SPEC "Unit declarations") - the same fixed order as with
        /// var/field declarations, NO alternative "name first, type via ':'
        /// afterwards" notation any more (which existed before - exactly the
        /// ambiguity that the ':' now resolves uniformly ONLY as a unit).
        /// Neither `var` nor a type is mandatory (a parameter without
        /// both stays untyped as before) - BUT if a unit is given without
        /// a preceding `var`/type (`func f(a:mm)`), it is treated
        /// implicitly like `var a:mm` (type taken from the argument at the call,
        /// unit fixed mm).</summary>
        private LambdaParam ParseOneParam(bool allowRef)
        {
            // `ref` before the parameter (contextual: `ref` stays usable as a name/type as long as no further name follows)
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
        // Expressions (precedence from low to high)
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

        /// <summary>`|` (bitwise or), `#` (bitwise exclusive or - NOT
        /// `^`, that is power, see ParsePower) and `&amp;` (bitwise and) -
        /// the same precedence order as in most C-like
        /// languages (or binds weakest, and strongest of these
        /// three), placed between the logical operators `&&`/`||`
        /// and the comparison `==`/`!=`/`&lt;`/etc. All three applicable only to `int`
        /// (see Values.Value.BitAnd/BitOr/BitXor).</summary>
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

        /// <summary>`&lt;&lt;`/`&gt;&gt;` (bit shift operators) - deliberately
        /// NOT recognised by the lexer as two-character tokens of their own (unlike
        /// e.g. `==`/`&amp;&amp;`), but here purely at parser level as two
        /// CONSECUTIVE `&lt;`/`&gt;` tokens: for `&lt;`/`&gt;` the lexer
        /// ALWAYS delivers only single-character tokens (except `&lt;=`/`&gt;=`), because `&gt;` also serves to
        /// close a generic type argument list (`new Box&lt;int&gt;()`,
        /// also arbitrarily deeply NESTED: `new Box&lt;Box&lt;int&gt;&gt;()`, see
        /// ParseOptionalTypeParamNames/SkipOptionalNestedTypeArgs) - a lexer
        /// that greedily merged `&gt;&gt;` into a single shift-operator token
        /// would destroy that at this point. Since type argument lists run via
        /// separate, completely distinct parser methods (never via this
        /// expression precedence chain), there is no conflict here anyway: two
        /// `&gt;`/`&lt;`
        /// in a row INSIDE an expression always mean a
        /// shift operator.</summary>
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

        /// <summary>`^` (power - NOT bitwise XOR, that is `#`, see
        /// ParseBitwiseXor) - binds stronger than `*`/`/`/`%` (`2 * 3^2` is
        /// `2 * 9 = 18`, not `(2*3)^2`). RIGHT-associative (`2^3^2` is
        /// `2^(3^2) = 2^9`, not `(2^3)^2`).
        ///
        /// The interplay with unary prefix operators deliberately follows
        /// Python's `**` convention (not "unary always binds strongest"):
        /// the BASE (left of `^`) is here deliberately only `ParsePostfix()`,
        /// NOT `ParseUnary()` - a sign BEFORE the whole power thereby binds
        /// WEAKER than `^` itself (`-2^2` is `-(2^2) = -4`, not
        /// `(-2)^2 = 4` - see ParseUnary, which on a prefix operator
        /// only comes back here AFTER `^` has already been evaluated).
        /// The EXPONENT (right of `^`), by contrast, is a full
        /// `ParseUnary()`, so that `2^-2` (negative exponent) still works
        /// directly, without having to set parentheses.</summary>
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

        /// <summary>Prefix operators: '-' (sign), '!' (logical negation),
        /// '~' (bitwise inversion), '*' (dereference, only in 'unsafe'),
        /// '&amp;' (address-of, only in 'unsafe'). Right-associatively chainable.
        /// No prefix of its own here -> ParsePower() (see there for the
        /// deliberate order relative to `^`), NOT directly ParsePostfix().</summary>
        private Expr ParseUnary()
        {
            if (Check(TokenType.PlusPlus) || Check(TokenType.MinusMinus))
            {
                var opTok = Advance();
                bool isIncrement = opTok.Type == TokenType.PlusPlus;
                // Deliberately ParsePostfix() instead of recursively ParseUnary() - the
                // operand target of prefix '++'/'--' MUST be an assignable
                // expression (variable/field/index), never a further
                // unary expression (`++!x`/`++-x` would be meaningless, since their
                // result is no valid assignment target) - ParsePostfix
                // covers exactly identifier/member/index access chains,
                // the same level that AssignExpr.Target allows too.
                var target = ParsePostfix();
                return new IncDecExpr(opTok.Line, target, isIncrement, IsPrefix: true);
            }

            // `flat x` / `copy x` (SPEC 2.4): copy prefixes - the operand is again a unary expression,
            // so `copy a.b` copies `a.b`, `copy a + b` is `(copy a) + b`. (`sync flat x` reads its `flat`
            // itself, see ParseSync - it never arrives here.)
            if (Check(TokenType.Flat) || Check(TokenType.Copy))
            {
                var copyTok = Advance();
                return new UnaryExpr(copyTok.Line, copyTok.Type == TokenType.Flat ? UnaryOp.FlatCopy : UnaryOp.DeepCopy, ParseUnary());
            }

            // `take x` (SPEC 2.2): as an argument or right of `=`/`var x =` - the ownership goes to the call or to the owner of the target
            if (Check(TokenType.Take))
            {
                var takeTok = Advance();
                return new UnaryExpr(takeTok.Line, UnaryOp.Take, ParseUnary());
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

        /// <summary>Postfix chain: call, member access, index, as well as the
        /// coercion suffixes ':' (unit) and '!' (type) in any order,
        /// lastly optionally '++'/'--' (see Ast.IncDecExpr) - deliberately NOT
        /// part of the loop above (cannot itself be the target of a further '.'/
        /// '['/... either, `x++.field` would be meaningless, since `x++` is a pure value,
        /// not an object).</summary>
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
                    // Consume the optional lookahead argument (unit name) only on the same
                    // line - otherwise exactly the original bug: an
                    // identifier that actually belongs to the next line/statement
                    // would wrongly be swallowed here along with it.
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
        /// (see Ast.SyncExpr documentation). `Target` is deliberately parsed only up to
        /// postfix level (identifier, member/index access) - not
        /// as a full expression level, so that e.g. `sync a == b` is unambiguously
        /// read as `(sync a) == b`, not as `sync (a == b)`.</summary>
        private Expr ParseSyncExpr()
        {
            int line = Peek().Line;
            bool isTry = Match(TokenType.Try);
            Expect(TokenType.Sync, "Expected 'sync'");
            // `sync globals`: the main program processes the queue of its fire threads
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

        /// <summary>Set as long as the TARGET of `on` of a `func` lambda is being read (`func (x) on win => ...`): an identifier or a parenthesised
        /// specification directly before it must not be read there as a short-form lambda (`win => ...`, `(win) => ...`) - the `=>` belongs to the `func` lambda.
        /// ParsePrimary consumes the flag with the first primary expression.</summary>
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
                    // See ParseStatement lookahead: 'try' is valid as an
                    // expression start only together with 'sync'/'process' or
                    // a direct function call ('try {' would already have been caught
                    // there as a try/catch block).
                    if (PeekAt(1).Type == TokenType.Process)
                    {
                        Advance(); // 'try'
                        Advance(); // 'process'
                        var procTarget = ParsePostfix();
                        return new TryProcessExpr(tok.Line, procTarget);
                    }
                    if (PeekAt(1).Type == TokenType.Sync)
                        return ParseSyncExpr();

                    // 'try Name(args)' - call of a "tryable" native
                    // function (see TryCallExpr documentation, the resolver checks whether
                    // 'Name' is actually registered that way).
                    Advance(); // 'try'
                    var innerCall = ParsePostfix();
                    if (innerCall is not CallExpr)
                        throw Error("Expected 'sync', 'process' or a function call after 'try'", tok);
                    return new TryCallExpr(tok.Line, innerCall);
                }

                case TokenType.Dot:
                {
                    // Valid only inside a 'with' block (see
                    // ParseWithStmt). IMPORTANT: '.' AND the member name
                    // are consumed HERE directly, not left to the general
                    // postfix loop (ParsePostfix) - which stops
                    // immediately if the CURRENT token has a line break
                    // before it (LineContinues() checks exactly that), and a
                    // '.' as the very first token of a new with line
                    // ALWAYS has a line break before it. Without this fix
                    // the '.' would stay unconsumed - an endless loop when
                    // parsing the with block (every further round lands
                    // again exactly here, without ever making progress).
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
                    // 'new Type[sizeExpr]' (array allocation) vs. 'new ClassName(args)'.
                    // The element type here deliberately only as a base name (without bit width/
                    // pointer) - a bit-width bracket directly behind the type would otherwise
                    // collide with the array-size bracket (both stand immediately
                    // behind the type name). A particular element width is fixed via
                    // the declared variable type (`var a : int[16] = new int[10]`).
                    if (TypeKeywords.Contains(Peek().Type)
                        || (Check(TokenType.Identifier) && PeekAt(1).Type == TokenType.LBracket))
                    {
                        // 'new byte[n]' - special case: creates a raw
                        // Values.ByteBuffer instead of a ScriptArray (see
                        // NewBufferExpr documentation), deliberately only one-dimensional -
                        // 'new byte[n][m]' (several ranks) is therefore an
                        // error instead of an "array of buffers".
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
                    // 'new Name<Arg1, Arg2>(...)' for a generic class -
                    // unambiguous, since after 'new Name' an
                    // argument list '(...)' MUST follow anyway (never a comparison),
                    // ParseOptionalTypeParamNames() fits here 1:1 (the same
                    // '<name, name>' grammar as with the declaration).
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

        /// <summary>Builds from the InterpolationSegments already collected by the lexer
        /// (see the documentation there) an
        /// InterpolatedStringExpr: text segments are taken over directly,
        /// every expression segment is re-lexed and parsed INDEPENDENTLY
        /// (a new lexer/parser instance on only this substring) - this way
        /// the full expression grammar inside `{...}` arises without
        /// having to maintain a second, parallel grammar path in the main parser.
        /// After the parsed expression only EOF may follow -
        /// anything else (e.g. a second expression not connected by an
        /// operator) is an error in the format string.</summary>
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

        /// <summary>Does an identifier or `this` follow the current word (`probe`/`silence`)?</summary>
        private bool IsProbeOperandNext() => PeekAt(1).Type is TokenType.Identifier or TokenType.This;

        /// <summary>Reads `a.b.c` (also `a.b.*`, `a`): returns the object expression, the member (null for `.*` and without a dot) and whether a member
        /// was given (`.name` or `.*`).</summary>
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

        /// <summary>`probe target changed|changing handler` - the handler is a block `{ ... }`, `=> expression`, `(a, b) => ...` or any
        /// lambda expression. Block and `=> expression` get the implicit names `sender`, `name`, `old`, `value` (4 parameters, see VM.RunProbeHandler).</summary>
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

        /// <summary>`silence a.b` / `silence a.*` (member or all probes of the object `a`) or `silence x` (probe handle or object).</summary>
        private Stmt ParseSilence()
        {
            int line = Peek().Line;
            Advance(); // 'silence'
            var (target, member, hasMember) = ParseProbePath();
            ExpectStatementTerminator();
            return new SilenceStmt(_sourceIndex, line, target, member, hasMember);
        }

        /// <summary>Does the current `(` stand at the start of a short-form lambda `(...) =>`? (Lookahead up to the matching `)`.)</summary>
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

        /// <summary>`=> expression` or `=> { ... }` of a lambda (after the parameter list and optional `on`).</summary>
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
                // Short form: `=> expression` implicitly becomes `{ return expression; }`.
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

        /// <summary>Lookahead without consuming - `PeekAt(0)` == `Peek()`.
        /// Falls back safely to the last token (EOF) at the end of the file.</summary>
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

        /// <summary>true if the next token lies on the same line as the
        /// last consumed one (no line break in between) – basis for ensuring
        /// that binary operators, assignment and the postfix chain do not
        /// accidentally read on across a new statement in the next line
        /// (it must not).</summary>
        private bool LineContinues() => _bracketDepth > 0 || !Peek().NewlineBefore;

        /// <summary>Enforces the statement separation rule: between two statements
        /// there must be a ';' or a line break. End of block ('}') and end of file
        /// also count as a valid termination (e.g. for single-line blocks like
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
