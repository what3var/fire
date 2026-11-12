using System;
using System.Collections.Generic;
using System.Linq;
using fire.Ast;
using fire.Resolving;
using fire.Values;

namespace fire.Compiler
{
    /// <summary>Expression trees (docs/ROADMAP_EXPRESSIONS.md): a lambda written at a parameter `lambda expression&lt;T&gt;` (the type checker found it:
    /// ResolveResult.Trees) becomes `__tree(...).__Attach(lambda)`. `__tree` is a lambda with the same parameters and captures as the written one; its body
    /// builds the nodes (`new BinaryExpression(">=", new MemberExpression(...), ...)`) - so what does not depend on a parameter (a captured variable, a member
    /// of it, a call on it) is evaluated where its names mean what they mean in the lambda, when the tree is made. The nodes are ordinary objects of the
    /// prelude: the VM and the native code build them the same way.</summary>
    public sealed partial class Compiler
    {
        [ThreadStatic] private static ExpressionTrees? s_trees;
        [ThreadStatic] private static IReadOnlyDictionary<string, ClassDecl>? s_classDecls;
        [ThreadStatic] private static HashSet<LambdaExpr>? s_rawTreeLambdasField;
        private static HashSet<LambdaExpr> s_rawTreeLambdas => s_rawTreeLambdasField ??= new HashSet<LambdaExpr>(ReferenceEqualityComparer.Instance);
        [ThreadStatic] private static HashSet<Expr>? s_rawTreeValuesField;
        private static HashSet<Expr> s_rawTreeValues => s_rawTreeValuesField ??= new HashSet<Expr>(ReferenceEqualityComparer.Instance);

        /// <summary>What a part of a tree sees: the parameters of the lambdas around it (name -> index; the innermost wins) and, inside a nested lambda,
        /// how its captures are loaded in the lambda around it.</summary>
        private sealed class TreeScope
        {
            public required Dictionary<string, int> Params { get; init; }
            public LambdaExpr? Nested { get; init; }
            public TreeScope? Outer { get; init; }
        }

        private Expr TreeCall(LambdaExpr lam)
        {
            int line = lam.Line;
            var scope = new TreeScope { Params = ParamsOf(lam, null) };
            var body = ((ReturnStmt)lam.Body.Statements[0]).Value!;
            var build = TreeLambda(lam, body, scope, attachCompiled: false);
            // (the builder: the same parameters and captures as the lambda - its slots mean the same)
            var builder = new LambdaExpr(line, lam.Params, null,
                new Stmt.BlockStmt(lam.Body.Source, line, new List<Stmt> { new ReturnStmt(lam.Body.Source, line, build) }), lam.AutoCapture, lam.CaptureModes, ExpressionBody: true);
            if (_refs is Dictionary<Expr, ResolvedRef> refs && refs.TryGetValue(lam, out var captures)) refs[builder] = captures;
            var undefinedArgs = lam.Params.Select(_ => (Expr)new LiteralExpr(line, Value.MakeUndefined())).ToList();
            return new CallExpr(line, new MemberExpr(line, new CallExpr(line, builder, undefinedArgs), "__Attach"), new List<Expr> { lam });
        }

        private static Dictionary<string, int> ParamsOf(LambdaExpr lam, TreeScope? outer)
        {
            var map = outer != null ? new Dictionary<string, int>(outer.Params) : new Dictionary<string, int>();
            for (int i = 0; i < lam.Params.Count; i++) map[lam.Params[i].Name] = i;
            return map;
        }

        private static string TreeType(Expr e) => s_trees != null && s_trees.Types.TryGetValue(e, out var t) ? t : "any";
        private static Expr Text(int line, string text) => new LiteralExpr(line, Value.MakeString(text));
        private static Expr Node(int line, string cls, params Expr[] args) => new NewExpr(line, new TypeRef(cls, null, 0), args);
        private static Expr Nodes(int line, IEnumerable<Expr> items) => new ArrayLiteralExpr(line, items.ToList());

        private Expr TreeLambda(LambdaExpr lam, Expr body, TreeScope scope, bool attachCompiled)
        {
            int line = lam.Line;
            var parameters = lam.Params.Select((p, i) => Node(line, "ParameterExpression", Text(line, p.Name), new LiteralExpr(line, Value.MakeInt(i)),
                Text(line, p.Type != null ? TypeRefText(p.Type) : ParamTypeOf(lam, i)))).ToList();
            return Node(line, "LambdaExpression", Nodes(line, parameters), TreeNode(body, scope), Text(line, TreeType(body)));
        }

        private static string TypeRefText(TypeRef t) => t.TypeArgs is { Count: > 0 } args ? t.BaseName + "<" + string.Join(", ", args.Select(TypeRefText)) + ">" : t.BaseName;

        // (the type of a parameter without one written: what the type checker found where it is used)
        private static string ParamTypeOf(LambdaExpr lam, int index)
        {
            string name = lam.Params[index].Name;
            var body = ((ReturnStmt)lam.Body.Statements[0]).Value!;
            foreach (var e in Walk(body))
                if (e is IdentifierExpr id && id.Name == name && TreeType(id) is var t && t != "any") return t;
            return "any";
        }

        private static IEnumerable<Expr> Walk(Expr e)
        {
            yield return e;
            foreach (var child in Children(e))
                foreach (var x in Walk(child)) yield return x;
        }

        private static IEnumerable<Expr> Children(Expr e) => e switch
        {
            MemberExpr m => new[] { m.Target },
            IndexExpr ix => new[] { ix.Target, ix.Index },
            CallExpr c => new[] { c.Callee }.Concat(c.Args),
            BinaryExpr b => new[] { b.Left, b.Right },
            UnaryExpr u => new[] { u.Operand },
            ConditionalExpr ce => new[] { ce.Condition, ce.Then, ce.Else },
            NewExpr n => n.Args,
            IsOfExpr io => new[] { io.Operand },
            TypeCoerceExpr tc => new[] { tc.Operand },
            ArrayLiteralExpr al => al.Elements,
            LambdaExpr l when l.Body.Statements is [ReturnStmt { Value: { } v }] => new[] { v },
            _ => Array.Empty<Expr>(),
        };

        /// <summary>Does the expression read a parameter of a lambda of the tree?</summary>
        private static bool DependsOnParams(Expr e, TreeScope scope)
        {
            switch (e)
            {
                case IdentifierExpr id: return scope.Params.ContainsKey(id.Name);
                case LambdaExpr l:
                {
                    var inner = new TreeScope { Params = ParamsOf(l, scope), Outer = scope, Nested = l };
                    return Children(e).Any(c => DependsOnParams(c, inner)) || true;   // (a lambda is a node of its own)
                }
                default: return Children(e).Any(c => DependsOnParams(c, scope));
            }
        }

        private Expr TreeNode(Expr e, TreeScope scope)
        {
            int line = e.Line;
            string type = TreeType(e);
            if (!DependsOnParams(e, scope))
            {
                if (e is LiteralExpr lit) return Node(line, "ConstantExpression", lit, Text(line, type));
                // (what does not depend on a parameter: its value when the tree is made - an object as a copy, like `taking`)
                var value = InScope(e, scope);
                return Node(line, "CapturedExpression", Text(line, SourceText(e)), new UnaryExpr(line, UnaryOp.DeepCopy, value), Text(line, type));
            }
            switch (e)
            {
                case IdentifierExpr id:
                    return Node(line, "ParameterExpression", Text(line, id.Name), new LiteralExpr(line, Value.MakeInt(scope.Params[id.Name])), Text(line, type));
                case MemberExpr m:
                {
                    var (isField, isProperty) = MemberKind(TreeType(m.Target), m.Name);
                    return Node(line, "MemberExpression", TreeNode(m.Target, scope), Text(line, m.Name), Text(line, type),
                        new LiteralExpr(line, Value.MakeBool(isField)), new LiteralExpr(line, Value.MakeBool(isProperty)));
                }
                case CallExpr { Callee: MemberExpr callee } call:
                {
                    bool isStatic = callee.Target is IdentifierExpr cls && _refs.TryGetValue(cls, out var r) && r is not ResolvedRef.Local and not ResolvedRef.Global
                        && !scope.Params.ContainsKey(cls.Name);
                    var args = Nodes(line, call.Args.Select(a => TreeNode(a, scope)));
                    if (isStatic)
                        return Node(line, "CallExpression", new LiteralExpr(line, Value.MakeUndefined()), Text(line, callee.Name), Text(line, ((IdentifierExpr)callee.Target).Name), args, Text(line, type));
                    return Node(line, "CallExpression", TreeNode(callee.Target, scope), Text(line, callee.Name), Text(line, TreeType(callee.Target)), args, Text(line, type));
                }
                case CallExpr { Callee: IdentifierExpr fn } call:
                    return Node(line, "CallExpression", new LiteralExpr(line, Value.MakeUndefined()), Text(line, fn.Name), Text(line, ""), Nodes(line, call.Args.Select(a => TreeNode(a, scope))), Text(line, type));
                case BinaryExpr b:
                    return Node(line, "BinaryExpression", Text(line, OpText(b.Op)), TreeNode(b.Left, scope), TreeNode(b.Right, scope), Text(line, type));
                case UnaryExpr u:
                    return Node(line, "UnaryExpression", Text(line, u.Op switch { UnaryOp.LogicalNot => "!", UnaryOp.Negate => "-", _ => "~" }), TreeNode(u.Operand, scope), Text(line, type));
                case ConditionalExpr ce:
                    return Node(line, "ConditionalExpression", TreeNode(ce.Condition, scope), TreeNode(ce.Then, scope), TreeNode(ce.Else, scope), Text(line, type));
                case NewExpr n:
                    return Node(line, "NewExpression", Nodes(line, n.Args.Select(a => TreeNode(a, scope))), Text(line, TypeRefText(n.ClassRef)));
                case IsOfExpr io:
                    return Node(line, "IsTypeExpression", TreeNode(io.Operand, scope), Text(line, TypeRefText(io.TypeRef)));
                case IndexExpr ix:
                    return Node(line, "IndexExpression", TreeNode(ix.Target, scope), TreeNode(ix.Index, scope), Text(line, type));
                case TypeCoerceExpr tc:
                    return Node(line, "ConvertExpression", TreeNode(tc.Operand, scope), Text(line, type));
                case LambdaExpr nested when nested.Body.Statements is [ReturnStmt { Value: { } nestedBody }]:
                    return TreeLambda(nested, nestedBody, new TreeScope { Params = ParamsOf(nested, scope), Outer = scope, Nested = nested }, attachCompiled: false);
                case ArrayLiteralExpr al:
                    return Node(line, "NewExpression", Nodes(line, al.Elements.Select(a => TreeNode(a, scope))), Text(line, type));
                default:
                    throw new CompilerException($"An expression tree cannot hold this expression ({e.GetType().Name})", line);
            }
        }

        /// <summary>The expression as it is evaluated in the builder (the lambda of the tree): inside a nested lambda a capture of it is what the nested
        /// lambda loads from the lambda around it.</summary>
        private Expr InScope(Expr e, TreeScope scope)
        {
            if (scope.Nested == null) return e;
            // (what reads no local of the nested lambda - globals, static members, literals - means the same in the lambda around it)
            if (!Walk(e).Any(x => x is IdentifierExpr id && _refs.TryGetValue(id, out var lr) && lr is ResolvedRef.Local)) return e;
            switch (e)
            {
                case IdentifierExpr id when _refs.TryGetValue(id, out var r) && r is ResolvedRef.Local { Depth: 0 } local
                    && _refs.TryGetValue(scope.Nested, out var nr) && nr is ResolvedRef.LambdaCaptures caps
                    && local.Slot - scope.Nested.Params.Count is var index && index >= 0 && index < caps.Variables.Count:
                    return InScope(caps.Loads?[index] ?? caps.Variables[index], scope.Outer!);
                case MemberExpr m: return new MemberExpr(m.Line, InScope(m.Target, scope), m.Name);
                case IndexExpr ix: return new IndexExpr(ix.Line, InScope(ix.Target, scope), InScope(ix.Index, scope));
                case LiteralExpr: return e;
                default:
                    throw new CompilerException("A nested lambda of an expression tree can capture variables and their members, nothing else that does not depend on a parameter", e.Line);
            }
        }

        private static (bool IsField, bool IsProperty) MemberKind(string typeName, string member)
        {
            int angle = typeName.IndexOf('<');
            string name = angle >= 0 ? typeName.Substring(0, angle) : typeName;
            for (int guard = 0; guard < 32 && s_classDecls != null && s_classDecls.TryGetValue(name, out var decl); guard++)
            {
                foreach (var m in decl.Members)
                {
                    if (m is FieldDecl f && f.Name == member) return (true, false);
                    if (m is PropertyDecl p && p.Name == member) return (false, true);
                }
                if (decl.BaseRefs is not { Count: > 0 } bases) break;
                name = bases[0].BaseName;
            }
            return (false, false);
        }

        private static string OpText(BinaryOp op) => op switch
        {
            BinaryOp.Add => "+", BinaryOp.Sub => "-", BinaryOp.Mul => "*", BinaryOp.Div => "/", BinaryOp.Mod => "%",
            BinaryOp.Eq => "==", BinaryOp.NotEq => "!=", BinaryOp.Lt => "<", BinaryOp.LtEq => "<=", BinaryOp.Gt => ">", BinaryOp.GtEq => ">=",
            BinaryOp.And => "&&", BinaryOp.Or => "||", BinaryOp.BitAnd => "&", BinaryOp.BitOr => "|", BinaryOp.BitXor => "^^",
            BinaryOp.ShiftLeft => "<<", BinaryOp.ShiftRight => ">>", BinaryOp.Power => "^", _ => op.ToString(),
        };

        /// <summary>The expression in fire syntax (the name of a Captured node: `min`, `filter.minAge`, `Make(x)`).</summary>
        private static string SourceText(Expr e) => e switch
        {
            IdentifierExpr id => id.Name,
            ThisExpr => "this",
            MemberExpr m => SourceText(m.Target) + "." + m.Name,
            IndexExpr ix => SourceText(ix.Target) + "[" + SourceText(ix.Index) + "]",
            CallExpr c => SourceText(c.Callee) + "(" + string.Join(", ", c.Args.Select(SourceText)) + ")",
            LiteralExpr { Value.Kind: ValueKind.String } l => "\"" + l.Value.AsString() + "\"",
            LiteralExpr l => l.Value.Kind == ValueKind.Undefined ? "undefined" : l.Value.Kind == ValueKind.Bool ? (l.Value.AsBool() ? "true" : "false") : l.Value.ToString(),
            BinaryExpr b => "(" + SourceText(b.Left) + " " + OpText(b.Op) + " " + SourceText(b.Right) + ")",
            UnaryExpr u => (u.Op == UnaryOp.LogicalNot ? "!" : "-") + SourceText(u.Operand),
            NewExpr n => "new " + TypeRefText(n.ClassRef) + "(" + string.Join(", ", n.Args.Select(SourceText)) + ")",
            ArrayLiteralExpr al => "[" + string.Join(", ", al.Elements.Select(SourceText)) + "]",
            _ => e.GetType().Name.Replace("Expr", "").ToLowerInvariant(),
        };
    }
}
