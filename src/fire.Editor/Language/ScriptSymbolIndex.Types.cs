using System.Collections.Generic;
using System.Linq;
using fire.Lexing;

namespace fire.Editor
{
    public enum TypeKind
    {
        /// <summary>Not determinable (dynamic typing - the normal case
        /// for untyped variables whose value does not result from a
        /// `new X(...)`/a typed method).</summary>
        Unknown,

        /// <summary>An instance of the class `Name`.</summary>
        Instance,

        /// <summary>The class `Name` ITSELF (`Name.Member`) - static
        /// members only.</summary>
        Static,

        /// <summary>The enum `Name` (`Name.Member`).</summary>
        Enum,

        /// <summary>A simple value (`int`, `string`, ...) or enum value.</summary>
        Primitive,

        /// <summary>An array with element type `Name`.</summary>
        Array,

        /// <summary>The namespace `Name` (fully qualified) - `Name.` shows
        /// its classes/enums/sub-namespaces.</summary>
        Namespace,
    }

    /// <summary>The (best-effort) derived type of an expression in the editor.
    /// `ViaThis`: the expression is `this`/`base` itself - then static
    /// members are also reachable via the dot (see resolver, "this.
    /// StaticMember").</summary>
    public sealed record ExprType(TypeKind Kind, string? Name = null, bool ViaThis = false)
    {
        public static readonly ExprType Unknown = new(TypeKind.Unknown);
    }

    /// <summary>
    /// Best-effort TYPE DERIVATION for completion/navigation - so that
    /// after `x.` the members of the class of `x` appear instead of arbitrary
    /// members of all classes. Deliberately token-based like the rest of this class
    /// (see class comment): no real resolution, but
    /// considerably more than "only explicitly typed variables":
    ///
    /// - Expressions: `new X(...)`/`new X&lt;..&gt;(...)`, `new X[n]` (array),
    ///   `this`/`base`, variables/parameters/fields (also without `this.`),
    ///   class/enum names, calls/field accesses/indices on them - arbitrarily
    ///   chained (`a.B().c[0].`).
    /// - Variable type: from `var x : T`, `T x`, typed parameters, `foreach
    ///   (T x in ...)` - or, for `var x = expression`/`x = expression`, from the
    ///   type of the expression (recursively, with a depth limit).
    /// - Member types: declared type/return type; otherwise for methods from
    ///   the `return` expressions in the body, for fields from `= new X()` or
    ///   an assignment `this.field = ...` somewhere in the class.
    ///
    /// Everything that cannot be determined this way is <see cref="TypeKind.Unknown"/>.
    /// </summary>
    public sealed partial class ScriptSymbolIndex
    {
        /// <summary>Maximum recursion depth of the derivation (variable from
        /// variable from method call ...) - also breaks cycles.</summary>
        private const int MaxDepth = 8;

        private static readonly HashSet<string> PrimitiveTypeNames = new() { "bool", "int", "float", "char", "string", "byte" };

        // -----------------------------------------------------------
        // Enclosing function
        // -----------------------------------------------------------

        private sealed record FunctionContext(
            int BodyStartTokenIdx, int BodyStartOffset, int BodyEndOffset, List<(string Name, string? TypeName)> Params);

        /// <summary>Index of the '{' of the body that belongs to the signature whose
        /// parameter list ends at `afterParen` - or -1 if there is no
        /// function definition there at all (but e.g. a pure call
        /// `foo(x)`, which later happens to be followed by some '{'). Only
        /// `: base(...)`, `=&gt;` and `on x` are allowed between ')' and '{', and
        /// any line break before it, except at the '{' itself, ends the
        /// search.</summary>
        private int FindBodyBrace(int afterParen)
        {
            int j = afterParen;
            while (j < _tokens.Count)
            {
                var t = _tokens[j];
                if (t.Type == TokenType.LBrace) return j;
                if (t.NewlineBefore || t.Type == TokenType.Eof) return -1;
                switch (t.Type)
                {
                    case TokenType.Colon: case TokenType.Arrow: case TokenType.On:
                    case TokenType.Base: case TokenType.This: case TokenType.Identifier: case TokenType.Dot:
                        j++;
                        break;
                    case TokenType.LParen:
                        int close = MatchForward(j, TokenType.LParen, TokenType.RParen);
                        if (close < 0) return -1;
                        j = close + 1;
                        break;
                    default:
                        return -1;
                }
            }
            return -1;
        }

        private List<FunctionContext>? _functions;

        /// <summary>All function definitions (methods, constructors,
        /// lambdas with a block body) of the document, in source order -
        /// computed once (the type derivation asks for them very often).</summary>
        private List<FunctionContext> AllFunctions()
        {
            if (_functions != null) return _functions;
            var result = new List<FunctionContext>();
            for (int i = 0; i < _tokens.Count; i++)
            {
                var type = _tokens[i].Type;
                bool isKeyword = type is TokenType.Construct or TokenType.Func;
                bool isNamed = type == TokenType.Identifier
                    && (i == 0 || _tokens[i - 1].Type is not (TokenType.Dot or TokenType.New));
                if (!isKeyword && !isNamed) continue;

                // '<T>' of a generic method between name and '('.
                int paren = i + 1;
                if (isNamed && paren < _tokens.Count && _tokens[paren].Type == TokenType.Lt)
                    paren = SkipAngleBrackets(paren);
                if (paren >= _tokens.Count || _tokens[paren].Type != TokenType.LParen) continue;

                var parms = ReadParamList(paren, out int afterParen);
                int open = FindBodyBrace(afterParen);
                if (open < 0) continue;

                int end = MatchBrace(open);
                result.Add(new FunctionContext(open, TokenOffset(open), TokenOffset(end) + 1, parms));
            }
            return _functions = result;
        }

        /// <summary>The INNERMOST function whose body contains `offset` (with
        /// nesting the inner one starts later - the last hit).</summary>
        private FunctionContext? EnclosingFunction(int offset)
        {
            FunctionContext? best = null;
            foreach (var fn in AllFunctions())
                if (offset >= fn.BodyStartOffset && offset <= fn.BodyEndOffset)
                    best = fn;
            return best;
        }

        /// <summary>Parameters (name, possibly type name) of the immediately
        /// enclosing method/constructor/lambda at `offset` - the
        /// INNERMOST function signature whose body contains `offset`.</summary>
        public List<(string Name, string? TypeName)> EnclosingFunctionParams(int offset) =>
            EnclosingFunction(offset)?.Params ?? new List<(string, string?)>();

        // -----------------------------------------------------------
        // Public entry points
        // -----------------------------------------------------------

        /// <summary>The type of the expression immediately BEFORE the '.' at
        /// `dotOffset` (character offset of the dot) - `Unknown` if the
        /// dot is not found or the expression cannot be derived.</summary>
        public ExprType ResolveReceiver(int dotOffset)
        {
            int dotIdx = FindTokenIndex(dotOffset);
            if (dotIdx <= 0 || _tokens[dotIdx].Type != TokenType.Dot) return ExprType.Unknown;
            // 'this.'/'base.' keep ViaThis (static members are reachable there too) -
            // any other origin does not, see EvalExprRange.
            return EvalExprRange(FirstTokenOfChainEndingAt(dotIdx - 1), dotIdx - 1, 0, keepViaThis: true);
        }

        /// <summary>The type of a single identifier `name` at `offset`
        /// (local variable/parameter, field of the enclosing class,
        /// class/enum name).</summary>
        public ExprType ResolveIdentifier(int offset, string name) => ResolveName(name, offset, 0);

        /// <summary>Like ResolveIdentifier, but only the class name if
        /// `name` is an INSTANCE of a known class - for
        /// NavigationEngine ("go to definition").</summary>
        public string? TryResolveDeclaredType(int offset, string name)
        {
            var type = ResolveName(name, offset, 0);
            return type.Kind == TypeKind.Instance ? type.Name : null;
        }

        /// <summary>Names of the local variables/parameters that are
        /// visible at `offset`: declarations between the start of the immediately
        /// enclosing function and `offset` (textual, NO real
        /// block-scope tracking - a variable from an already
        /// left sibling block is still suggested here;
        /// deliberate simplification for auto-completion).</summary>
        public List<string> LocalVarsBeforeCursor(int offset)
        {
            var result = new List<string>();
            var fn = EnclosingFunction(offset);
            int n = _tokens.Count;
            for (int k = fn?.BodyStartTokenIdx ?? 0; k < n - 1; k++)
            {
                if (TokenOffset(k) >= offset) break;
                var t = _tokens[k];
                string? declared = null;
                if (t.Type == TokenType.Var && _tokens[k + 1].Type == TokenType.Identifier)
                    declared = _tokens[k + 1].Lexeme;
                else if (t.Type == TokenType.Foreach && k + 3 < n && _tokens[k + 1].Type == TokenType.LParen
                         && _tokens[k + 2].Type == TokenType.Identifier && _tokens[k + 3].Type == TokenType.In)
                    declared = _tokens[k + 2].Lexeme; // 'foreach (name in ...)'
                else if ((t.Type == TokenType.Identifier || IsTypeKeyword(t.Type)) && _tokens[k + 1].Type == TokenType.Identifier
                         && (k == 0 || _tokens[k - 1].Type is not (TokenType.Dot or TokenType.New))
                         && !_tokens[k + 1].NewlineBefore
                         && (PrimitiveTypeNames.Contains(t.Lexeme) || ResolveClassKey(t.Lexeme, ContextAt(offset)) != null
                             || ResolveEnumKey(t.Lexeme, ContextAt(offset)) != null))
                    declared = _tokens[k + 1].Lexeme;

                if (declared != null && TokenOffset(k + 1) < offset && !result.Contains(declared))
                    result.Add(declared);
            }
            return result;
        }

        // -----------------------------------------------------------
        // Token-Hilfen
        // -----------------------------------------------------------

        private int FindTokenIndex(int offset)
        {
            int lo = 0, hi = _tokens.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                int o = TokenOffset(mid);
                if (o == offset) return mid;
                if (o < offset) lo = mid + 1; else hi = mid - 1;
            }
            return -1;
        }

        private int MatchForward(int openIdx, TokenType open, TokenType close)
        {
            int depth = 0;
            for (int j = openIdx; j < _tokens.Count; j++)
            {
                if (_tokens[j].Type == open) depth++;
                else if (_tokens[j].Type == close && --depth == 0) return j;
            }
            return -1;
        }

        private int MatchBackward(int closeIdx, TokenType open, TokenType close)
        {
            int depth = 0;
            for (int j = closeIdx; j >= 0; j--)
            {
                if (_tokens[j].Type == close) depth++;
                else if (_tokens[j].Type == open && --depth == 0) return j;
            }
            return -1;
        }

        // -----------------------------------------------------------
        // Expressions as chains `a.b(...).c[...]`
        // -----------------------------------------------------------

        private enum SegKind { Name, Call, Index, New }

        private readonly record struct Seg(SegKind Kind, string Text);

        /// <summary>Reads the chain that ENDS with the token `endIdx`, BACKWARDS
        /// (where it begins is only known afterwards): identifiers, `this`,
        /// `base`, calls `name(...)` (also `new X&lt;..&gt;(...)`), indices
        /// `[...]`, joined by '.'. `segs` in source order,
        /// `startIdx` the first token of the chain. `false` if what ends
        /// there is not such a chain (literal, parenthesised expression,
        /// operator ...).</summary>
        private bool TryParseChainBackward(int endIdx, out List<Seg> segs, out int startIdx)
        {
            var rev = new List<Seg>();
            segs = rev;
            startIdx = endIdx;
            int j = endIdx;

            while (true)
            {
                if (j < 0) return false;
                var t = _tokens[j];
                Seg seg;
                int segStart;

                if (t.Type == TokenType.RBracket)
                {
                    int open = MatchBackward(j, TokenType.LBracket, TokenType.RBracket);
                    if (open < 0) return false;
                    rev.Add(new Seg(SegKind.Index, string.Empty));
                    j = open - 1; // keep reading the indexed thing before it
                    continue;
                }

                if (t.Type == TokenType.RParen)
                {
                    int open = MatchBackward(j, TokenType.LParen, TokenType.RParen);
                    if (open < 0) return false;
                    int k = open - 1;
                    if (k >= 0 && _tokens[k].Type == TokenType.Gt) // 'new Box<int>(...)'
                    {
                        int lt = k;
                        int depth = 0;
                        for (; lt >= 0; lt--)
                        {
                            if (_tokens[lt].Type == TokenType.Gt) depth++;
                            else if (_tokens[lt].Type == TokenType.Lt && --depth == 0) break;
                            else if (_tokens[lt].Type is TokenType.LParen or TokenType.RParen) return false;
                        }
                        if (lt < 0) return false;
                        k = lt - 1;
                    }
                    if (k < 0 || _tokens[k].Type != TokenType.Identifier) return false; // Parenthesised expression '(...)' - not supported
                    bool isNew = k > 0 && _tokens[k - 1].Type == TokenType.New;
                    seg = new Seg(isNew ? SegKind.New : SegKind.Call, _tokens[k].Lexeme);
                    segStart = isNew ? k - 1 : k;
                }
                else if (t.Type is TokenType.Identifier or TokenType.This or TokenType.Base)
                {
                    // 'new Name' (only with a following '[', see 'new X[n]') - if
                    // there is a '.' behind the name, however, it is the BEGINNING of a
                    // qualified name ('new Geometry.Circle(...)'), see
                    // MergeQualifiedNew.
                    bool isNew = t.Type == TokenType.Identifier && j > 0 && _tokens[j - 1].Type == TokenType.New
                        && !(j + 1 < _tokens.Count && _tokens[j + 1].Type == TokenType.Dot);
                    seg = new Seg(isNew ? SegKind.New : SegKind.Name, t.Lexeme);
                    segStart = isNew ? j - 1 : j;
                }
                else
                {
                    return false;
                }

                rev.Add(seg);
                startIdx = segStart;
                if (seg.Kind == SegKind.New) break;
                if (segStart - 1 >= 0 && _tokens[segStart - 1].Type == TokenType.Dot)
                {
                    j = segStart - 2;
                    continue;
                }
                break;
            }

            rev.Reverse();
            MergeQualifiedNew(rev, ref startIdx);
            return true;
        }

        /// <summary>`new Geometry.Circle(...)`/`new Geometry.Circle[n]`: the
        /// backward run sees here the chain `Geometry` `.` `Circle(...)` and
        /// only afterwards the `new` BEFORE it - then combine into ONE new segment with the
        /// qualified name.</summary>
        private void MergeQualifiedNew(List<Seg> segs, ref int startIdx)
        {
            if (startIdx <= 0 || _tokens[startIdx - 1].Type != TokenType.New) return;

            int core = segs.Count > 0 && segs[^1].Kind == SegKind.Index ? segs.Count - 1 : segs.Count;
            if (core < 2) return;
            for (int q = 0; q < core - 1; q++)
                if (segs[q].Kind != SegKind.Name || segs[q].Text is "this" or "base") return;
            var last = segs[core - 1];
            bool hasIndex = core < segs.Count;
            if (last.Kind != (hasIndex ? SegKind.Name : SegKind.Call)) return;

            string qualified = string.Join(".", segs.Take(core).Select(sg => sg.Text));
            var merged = new List<Seg> { new Seg(SegKind.New, qualified) };
            if (hasIndex) merged.Add(segs[^1]);
            segs.Clear();
            segs.AddRange(merged);
            startIdx--;
        }

        private int FirstTokenOfChainEndingAt(int endIdx) =>
            TryParseChainBackward(endIdx, out _, out int start) ? start : endIdx;

        /// <summary>The type of the expression that comprises EXACTLY the tokens `startIdx`..
        /// `endIdx` (if there is more/something other than a chain
        /// or a literal, the result is Unknown).</summary>
        private ExprType EvalExprRange(int startIdx, int endIdx, int depth, bool keepViaThis = false)
        {
            var type = EvalExprRangeCore(startIdx, endIdx, depth);
            // `var x = this` (or a method that returns `this`) makes x
            // an ORDINARY instance - `x.` reaches no static
            // members, only `this.` itself.
            return type.ViaThis && !keepViaThis ? type with { ViaThis = false } : type;
        }

        private ExprType EvalExprRangeCore(int startIdx, int endIdx, int depth)
        {
            if (depth > MaxDepth || startIdx < 0 || startIdx > endIdx || endIdx >= _tokens.Count)
                return ExprType.Unknown;

            // `flat x` / `copy x` (copy prefixes): the result has the type of the copied expression.
            if (startIdx < endIdx && _tokens[startIdx].Type is TokenType.Flat or TokenType.Copy or TokenType.Take)
                return EvalExprRangeCore(startIdx + 1, endIdx, depth + 1);

            if (startIdx == endIdx)
            {
                switch (_tokens[startIdx].Type)
                {
                    case TokenType.StringLiteral: case TokenType.InterpolatedStringLiteral: return new ExprType(TypeKind.Primitive, "string");
                    case TokenType.IntLiteral: return new ExprType(TypeKind.Primitive, "int");
                    case TokenType.FloatLiteral: return new ExprType(TypeKind.Primitive, "float");
                    case TokenType.CharLiteral: return new ExprType(TypeKind.Primitive, "char");
                    case TokenType.True: case TokenType.False: return new ExprType(TypeKind.Primitive, "bool");
                }
            }

            if (!TryParseChainBackward(endIdx, out var segs, out int start) || start != startIdx)
                return ExprType.Unknown;
            return EvalChain(segs, TokenOffset(startIdx), depth);
        }

        private ExprType EvalChain(List<Seg> segs, int scopeOffset, int depth)
        {
            if (segs.Count == 0 || depth > MaxDepth) return ExprType.Unknown;

            ExprType cur;
            int next = 1;
            var root = segs[0];
            switch (root.Kind)
            {
                case SegKind.New:
                    {
                        var context = ContextAt(scopeOffset);
                        var created = FromTypeName(root.Text, isArray: false, context);
                        if (segs.Count > 1 && segs[1].Kind == SegKind.Index) // 'new X[n]'
                        {
                            created = FromTypeName(root.Text, isArray: true, context);
                            next = 2;
                        }
                        cur = created;
                        break;
                    }
                case SegKind.Name when root.Text == "this":
                    {
                        var cls = EnclosingClassAt(scopeOffset);
                        cur = cls != null ? new ExprType(TypeKind.Instance, cls, ViaThis: true) : ExprType.Unknown;
                        break;
                    }
                case SegKind.Name when root.Text == "base":
                    {
                        var cls = EnclosingClassAt(scopeOffset);
                        string? baseName = cls != null && Classes.TryGetValue(cls, out var info)
                            ? ResolvedBases(info).FirstOrDefault(b => !Classes[b].IsInterface)
                            : null;
                        cur = baseName != null ? new ExprType(TypeKind.Instance, baseName, ViaThis: true) : ExprType.Unknown;
                        break;
                    }
                case SegKind.Name:
                    cur = ResolveName(root.Text, scopeOffset, depth);
                    break;
                case SegKind.Call:
                    {
                        // Call of a method of the enclosing class without 'this.'
                        var cls = EnclosingClassAt(scopeOffset);
                        var method = cls == null ? null
                            : MembersOf(cls).FirstOrDefault(m => m.Kind == MemberKind.Method && m.Name == root.Text);
                        cur = method != null ? TypeOfMember(method, depth) : ExprType.Unknown;
                        break;
                    }
                default:
                    return ExprType.Unknown;
            }

            for (int i = next; i < segs.Count && cur.Kind != TypeKind.Unknown; i++)
            {
                var seg = segs[i];
                cur = seg.Kind switch
                {
                    SegKind.Name => MemberType(cur, seg.Text, isCall: false, depth),
                    SegKind.Call => MemberType(cur, seg.Text, isCall: true, depth),
                    SegKind.Index => ElementType(cur, depth),
                    _ => ExprType.Unknown,
                };
            }
            return cur;
        }

        private ExprType MemberType(ExprType receiver, string name, bool isCall, int depth)
        {
            switch (receiver.Kind)
            {
                case TypeKind.Instance:
                case TypeKind.Static:
                    {
                        var member = MembersOf(receiver.Name!).FirstOrDefault(m => m.Name == name
                            && (isCall ? m.Kind == MemberKind.Method : m.Kind is MemberKind.Field or MemberKind.Property));
                        return member != null ? TypeOfMember(member, depth) : ExprType.Unknown;
                    }
                case TypeKind.Enum:
                    return EnumMembers.TryGetValue(receiver.Name!, out var members) && members.Contains(name)
                        ? new ExprType(TypeKind.Primitive, "int")
                        : ExprType.Unknown;
                case TypeKind.Namespace:
                    {
                        // 'Geometry.Circle' (the class itself: static
                        // access), 'Geometry.Color' (enum), 'Geometry.Inner'
                        // (sub-namespace) - there are no calls on a
                        // namespace.
                        string full = receiver.Name + "." + name;
                        if (isCall) return ExprType.Unknown;
                        if (Classes.ContainsKey(full)) return new ExprType(TypeKind.Static, full);
                        if (EnumMembers.ContainsKey(full)) return new ExprType(TypeKind.Enum, full);
                        if (Namespaces.Contains(full)) return new ExprType(TypeKind.Namespace, full);
                        return ExprType.Unknown;
                    }
                case TypeKind.Primitive:
                case TypeKind.Array:
                    {
                        // Eingebaute Mitglieder (`text.Length`, `text.Trim()`, `buffer.ToString()`).
                        foreach (var member in BuiltinMembers.For(receiver))
                            if (member.Name == name && member.IsProperty == !isCall)
                                return BuiltinMembers.ToExprType(member.ReturnType);

                        // Methods from `class extends string { ... }` (prelude and own extensions).
                        if (isCall && BuiltinMembers.ExtensionClassOf(receiver) is { } extensionKey
                            && Classes.ContainsKey(extensionKey))
                        {
                            var extension = MembersOf(extensionKey).FirstOrDefault(m => m.Kind == MemberKind.Method && m.Name == name);
                            if (extension != null) return TypeOfMember(extension, depth);
                        }
                        return ExprType.Unknown;
                    }
                default:
                    return ExprType.Unknown;
            }
        }

        /// <summary>The type of `x[...]`: for an array the element type, for
        /// an instance the return type of its `operator[]`.</summary>
        private ExprType ElementType(ExprType container, int depth)
        {
            switch (container.Kind)
            {
                case TypeKind.Array:
                    return FromTypeName(container.Name!, isArray: false, System.Array.Empty<string>()); // Name is already a key
                case TypeKind.Primitive when container.Name == "string":
                    return new ExprType(TypeKind.Primitive, "char"); // s[i]
                case TypeKind.Instance:
                    {
                        var op = MembersOf(container.Name!).FirstOrDefault(m => m.Name == "operator[]");
                        return op != null ? TypeOfMember(op, depth) : ExprType.Unknown;
                    }
                default:
                    return ExprType.Unknown;
            }
        }

        /// <summary>The type that the type name `name` (as written, possibly
        /// qualified) means, resolved against `context` (see
        /// ClassInfo.Context) - class, enum, or a simple type like `int`.</summary>
        private ExprType FromTypeName(string? name, bool isArray, IReadOnlyList<string> context)
        {
            if (name == null) return ExprType.Unknown;
            if (PrimitiveTypeNames.Contains(name))
                return new ExprType(isArray ? TypeKind.Array : TypeKind.Primitive, name);

            string? classKey = ResolveClassKey(name, context);
            if (classKey != null)
                return new ExprType(isArray ? TypeKind.Array : TypeKind.Instance, classKey);

            string? enumKey = ResolveEnumKey(name, context);
            if (enumKey != null)
                return new ExprType(isArray ? TypeKind.Array : TypeKind.Primitive, enumKey);
            return ExprType.Unknown;
        }

        // -----------------------------------------------------------
        // Typ eines Mitglieds
        // -----------------------------------------------------------

        private ExprType TypeOfMember(MemberInfo member, int depth)
        {
            if (depth > MaxDepth) return ExprType.Unknown;

            // Type names in the declaration are written relative to the context of the
            // declaring class (namespace + #using of its file).
            var ownerContext = Classes.TryGetValue(member.Owner, out var ownerInfo)
                ? ownerInfo.Context
                : System.Array.Empty<string>();
            var declared = FromTypeName(member.TypeName, member.TypeIsArray, ownerContext);
            if (declared.Kind != TypeKind.Unknown) return declared;

            // No (usable) declared type - infer from the body/the
            // assignments. Always in the index whose token stream the
            // member's indices refer to (for prelude classes that of the
            // prelude, not this one).
            var source = member.Source ?? this;
            return member.Kind switch
            {
                MemberKind.Method => source.InferReturnType(member, depth + 1),
                MemberKind.Field => source.InferFieldType(member, depth + 1),
                _ => ExprType.Unknown,
            };
        }

        /// <summary>The return type of a method without a declared type: the
        /// type of the first derivable `return` expression in the body.</summary>
        internal ExprType InferReturnType(MemberInfo method, int depth)
        {
            if (depth > MaxDepth || method.BodyStart < 0) return ExprType.Unknown;
            for (int k = method.BodyStart + 1; k < method.BodyEnd; k++)
            {
                if (_tokens[k].Type != TokenType.Return) continue;
                int start = k + 1;
                if (start >= method.BodyEnd || _tokens[start].NewlineBefore) continue; // 'return' without a value
                int end = ExpressionEnd(start, method.BodyEnd);
                var type = EvalExprRange(start, end - 1, depth + 1);
                if (type.Kind != TypeKind.Unknown) return type;
            }
            return ExprType.Unknown;
        }

        /// <summary>The type of a field without a declared type: the type of the
        /// first derivable assignment `this.field = expression` somewhere in
        /// the class (typically: in the constructor).</summary>
        internal ExprType InferFieldType(MemberInfo field, int depth)
        {
            if (depth > MaxDepth || !Classes.TryGetValue(field.Owner, out var owner)) return ExprType.Unknown;
            foreach (var (start, end) in owner.BodyRanges)
                for (int k = start + 1; k + 3 < end; k++)
                {
                    if (_tokens[k].Type != TokenType.This || _tokens[k + 1].Type != TokenType.Dot
                        || _tokens[k + 2].Type != TokenType.Identifier || _tokens[k + 2].Lexeme != field.Name
                        || _tokens[k + 3].Type != TokenType.Assign)
                        continue;
                    int valueStart = k + 4;
                    int valueEnd = ExpressionEnd(valueStart, end);
                    var type = EvalExprRange(valueStart, valueEnd - 1, depth + 1);
                    if (type.Kind != TypeKind.Unknown) return type;
                }
            return ExprType.Unknown;
        }

        // -----------------------------------------------------------
        // Bezeichner: lokale Variablen, Parameter, Felder, Klassen, Enums
        // -----------------------------------------------------------

        private ExprType ResolveName(string name, int offset, int depth)
        {
            if (depth > MaxDepth) return ExprType.Unknown;

            var local = FindLocal(name, offset, depth, out bool found);
            if (found) return local;

            // Field/property of the enclosing class (SPEC "Implicit
            // member references": addressable also without 'this.').
            var enclosing = EnclosingClassAt(offset);
            if (enclosing != null)
            {
                var member = MembersOf(enclosing).FirstOrDefault(
                    m => m.Name == name && m.Kind is MemberKind.Field or MemberKind.Property);
                if (member != null) return TypeOfMember(member, depth);
            }

            // Class/enum/namespace names as an expression (`Name.Member`):
            // as in the real compiler ONLY the exactly written, possibly already
            // fully qualified name (see Resolver.TryResolveStaticMemberAccess
            // - no resolution via `#using`/the current namespace).
            if (Classes.ContainsKey(name)) return new ExprType(TypeKind.Static, name);
            if (EnumMembers.ContainsKey(name)) return new ExprType(TypeKind.Enum, name);
            if (Namespaces.Contains(name)) return new ExprType(TypeKind.Namespace, name);
            return ExprType.Unknown;
        }

        private bool IsStatementStart(int k) =>
            k == 0 || _tokens[k].NewlineBefore
            || _tokens[k - 1].Type is TokenType.Semicolon or TokenType.LBrace or TokenType.RBrace
                or TokenType.RParen or TokenType.Else;

        /// <summary>The type of the local variable/parameter `name` at
        /// `offset`, from the LAST matching declaration before it in the
        /// enclosing function (top-level code: in the whole document);
        /// `found` says whether there was a declaration at all (even if the
        /// type stays Unknown - then NO field/class of the same
        /// name should step in).</summary>
        private ExprType FindLocal(string name, int offset, int depth, out bool found)
        {
            found = false;
            var result = ExprType.Unknown;
            bool explicitType = false;

            var context = ContextAt(offset);
            var fn = EnclosingFunction(offset);
            if (fn != null)
                foreach (var (paramName, paramType) in fn.Params)
                    if (paramName == name)
                    {
                        found = true;
                        result = FromTypeName(paramType, isArray: false, context);
                        explicitType = result.Kind != TypeKind.Unknown;
                    }

            int n = _tokens.Count;
            for (int k = fn?.BodyStartTokenIdx ?? 0; k < n; k++)
            {
                if (TokenOffset(k) >= offset) break;
                var t = _tokens[k];

                // 'var name [: unit] [= expression]' - behind the ':' there is only
                // a UNIT (a type is given as 'T name', not as
                // 'var name : T'), so the type comes solely from the
                // initialiser. Except as the loop variable of 'foreach (var
                // name in ...)', which the case below (at the 'foreach' token
                // itself) has already handled completely.
                if (t.Type == TokenType.Var && k + 1 < n && _tokens[k + 1].Type == TokenType.Identifier
                    && _tokens[k + 1].Lexeme == name
                    && !(k >= 2 && _tokens[k - 1].Type == TokenType.LParen && _tokens[k - 2].Type == TokenType.Foreach))
                {
                    found = true;
                    result = ExprType.Unknown;
                    explicitType = false;
                    int statementEnd = ExpressionEnd(k + 1, n);
                    for (int q = k + 2; q < statementEnd; q++)
                        if (_tokens[q].Type == TokenType.Assign)
                        {
                            result = EvalExprRange(q + 1, ExpressionEnd(q + 1, n) - 1, depth + 1);
                            break;
                        }
                    continue;
                }

                // foreach (name in expression) - that is how the language writes it (SPEC 8.5);
                // an additional 'var' in front is recognised as well.
                int loopVar = t.Type == TokenType.Foreach && k + 3 < n && _tokens[k + 1].Type == TokenType.LParen
                    ? (_tokens[k + 2].Type == TokenType.Var ? k + 3 : k + 2)
                    : -1;
                if (loopVar > 0 && loopVar + 1 < n && _tokens[loopVar].Type == TokenType.Identifier
                    && _tokens[loopVar].Lexeme == name && _tokens[loopVar + 1].Type == TokenType.In)
                {
                    found = true;
                    explicitType = false;
                    int close = MatchForward(k + 1, TokenType.LParen, TokenType.RParen);
                    var iterable = close > loopVar + 2 ? EvalExprRange(loopVar + 2, close - 1, depth + 1) : ExprType.Unknown;
                    result = iterable.Kind == TypeKind.Array
                        ? FromTypeName(iterable.Name, isArray: false, System.Array.Empty<string>()) // Name is already a key
                        : ExprType.Unknown;
                    continue;
                }

                // 'T name' (auch 'foreach (T name in ...)', 'catch (T name)'), T
                // evtl. qualifiziert ('Geometry.Circle name').
                if ((t.Type == TokenType.Identifier || IsTypeKeyword(t.Type)) && k + 1 < n
                    && _tokens[k + 1].Type == TokenType.Identifier && _tokens[k + 1].Lexeme == name
                    && !_tokens[k + 1].NewlineBefore)
                {
                    int typeStart = k;
                    while (typeStart >= 2 && _tokens[typeStart - 1].Type == TokenType.Dot
                           && _tokens[typeStart - 2].Type == TokenType.Identifier)
                        typeStart -= 2;
                    if (typeStart == 0 || _tokens[typeStart - 1].Type is not (TokenType.Dot or TokenType.New))
                    {
                        found = true;
                        bool isArray = k + 2 < n && _tokens[k + 2].Type == TokenType.LBracket;
                        string typeText = string.Concat(_tokens.Skip(typeStart).Take(k - typeStart + 1).Select(tok => tok.Lexeme));
                        result = FromTypeName(typeText, isArray, context);
                        explicitType = result.Kind != TypeKind.Unknown;
                        continue;
                    }
                }

                // name = expression - only as a statement, and only if the type
                // is not explicitly declared (then that would be authoritative).
                if (!explicitType && t.Type == TokenType.Identifier && t.Lexeme == name
                    && k + 1 < n && _tokens[k + 1].Type == TokenType.Assign && IsStatementStart(k))
                {
                    var assigned = EvalExprRange(k + 2, ExpressionEnd(k + 2, n) - 1, depth + 1);
                    if (assigned.Kind != TypeKind.Unknown)
                    {
                        found = true;
                        result = assigned;
                    }
                }
            }

            return result;
        }
    }
}
