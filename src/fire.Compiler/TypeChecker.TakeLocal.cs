using System.Collections.Generic;
using System.Linq;
using fire.Ast;

namespace fire.Compiler
{
    // TC035: `x.TakeLocal()` in an inner block (the body of an `if`, a loop, a `{ }`, `try`/`catch`) gives x to that block - it is destroyed when the
    // block ends. A reference that leaves the block then holds a destroyed object: x stored in a field, an element, a variable from outside the block
    // or a container from outside it (`list.Add(x)`, which takes only what belongs to the call), or x used after the block (`return x` after an `if`).
    // At the top level of a function (method, constructor, lambda, the program) TakeLocal binds x to the function's scope, and `return x` hands it
    // to the caller: no warning there - nor for a `return x` inside the block itself (the return gives it to the caller as well).
    //
    // TC036: `var x = new ...` (a new object, array or buffer: it belongs to its block) and then `obj.f = x` - the field only refers to it. The block
    // ends, x is destroyed, and the field holds a destroyed object (a DestroyedException at its next use). Reported when the place outlives the block:
    // a field of `this`, of a parameter, of a variable from outside the block or of a class (a static field), or an element of such an array - unless
    // x is given away somewhere in the block (`take x`, `x.TakeTo(...)`, ..., `return x`) or gets another value, or the member is a property (its setter
    // may take it). Not at the top level of the program (there the variable lives as long as the program).
    public sealed partial class TypeChecker
    {
        /// <summary>`x.TakeLocal()` in an inner block and x leaves that block (stored where it outlives the block, or used after it) - a warning in strict mode too.</summary>
        public const string BlockTakeLocalCode = "TC035";

        /// <summary>A new value of a block (`var x = new ...`) stored plainly in a field that outlives the block - a warning in strict mode too.</summary>
        public const string StoredLocalCode = "TC036";

        // the names of the properties of the program (`obj.Name = x` may be a setter that takes x), and the statements of the top level of the program
        private HashSet<string>? _tlProperties;
        private IReadOnlyList<Stmt>? _tlProgramTop;

        // the methods of a container that keep their argument (List, Dictionary, HashSet, JsonObject, queues and stacks)
        private static readonly HashSet<string> s_keepingMethods = new() { "Add", "Insert", "Set", "Push", "Enqueue" };

        /// <summary>Looks at every function body of the program for TC035.</summary>
        private void CheckBlockTakeLocals(IReadOnlyList<Stmt> program)
        {
            int saved = _source;
            // (the statements of the program's top level, per file: the top level of the program's scope)
            var topLevel = new Dictionary<int, List<Stmt>>();
            _tlProperties = new HashSet<string>();
            CollectPropertyNames(program, _tlProperties);
            foreach (var stmt in program)
            {
                if (stmt.Source < _reportFrom) continue;
                _source = stmt.Source;
                if (stmt is ClassDecl or NamespaceDecl or ClassExtensionDecl or MethodDecl or ConstructorDecl or DestructorDecl or PropertyDecl or InterfaceDecl or EnumDecl)
                    TakeLocalDecl(stmt);
                else
                {
                    if (!topLevel.TryGetValue(stmt.Source, out var list)) topLevel[stmt.Source] = list = new List<Stmt>();
                    list.Add(stmt);
                }
            }
            foreach (var (source, statements) in topLevel)
            {
                _source = source;
                _tlProgramTop = statements;
                TakeLocalBody(statements);
            }
            _tlProgramTop = null;
            _source = saved;
        }

        private static void CollectPropertyNames(IEnumerable<Stmt> statements, HashSet<string> names)
        {
            foreach (var stmt in statements)
                switch (stmt)
                {
                    case PropertyDecl p: names.Add(p.Name); break;
                    case ClassDecl c: CollectPropertyNames(c.Members, names); break;
                    case NamespaceDecl n: CollectPropertyNames(n.Members, names); break;
                    case ClassExtensionDecl x: CollectPropertyNames(x.Members, names); break;
                }
        }

        private void TakeLocalDecl(Stmt decl)
        {
            switch (decl)
            {
                case ClassDecl c: foreach (var m in c.Members) TakeLocalDecl(m); break;
                case NamespaceDecl n: foreach (var m in n.Members) TakeLocalDecl(m); break;
                case ClassExtensionDecl x: foreach (var m in x.Members) TakeLocalDecl(m); break;
                case MethodDecl m: TakeLocalBody(m.Body.Statements); break;
                case ConstructorDecl k: TakeLocalBody(k.Body.Statements); break;
                case DestructorDecl d: TakeLocalBody(d.Body.Statements); break;
                case PropertyDecl p:
                    if (p.Getter != null) TakeLocalBody(p.Getter.Statements);
                    if (p.Setter != null) TakeLocalBody(p.Setter.Statements);
                    break;
                case FieldDecl f when f.Initializer != null: TakeLocalLambdas(f.Initializer); break;
            }
        }

        /// <summary>A block inside a function: its statements, the names declared for it (the variable of a foreach or a for, of a catch) and the blocks
        /// around it with the position of the statement that holds it.</summary>
        private sealed record TakeLocalFrame(IReadOnlyList<Stmt> Statements, int Index, TakeLocalFrame? Outer, IReadOnlyCollection<string> Declared);

        /// <summary>The statements at the top level of a function.</summary>
        private void TakeLocalBody(IReadOnlyList<Stmt> statements) => TakeLocalWalk(statements, null, System.Array.Empty<string>());

        private void TakeLocalWalk(IReadOnlyList<Stmt> statements, TakeLocalFrame? outer, IReadOnlyCollection<string> declared)
        {
            for (int i = 0; i < statements.Count; i++)
            {
                var stmt = statements[i];
                var here = new TakeLocalFrame(statements, i, outer, declared);
                if (outer != null && TakeLocalTarget(stmt) is { } name) CheckBlockTakeLocal(name, here, stmt.Line);
                if (stmt is VarDeclStmt { Initializer: NewExpr or NewArrayExpr or NewBufferExpr or ArrayLiteralExpr } fresh && !(outer == null && ReferenceEquals(statements, _tlProgramTop)))
                    CheckStoredLocal(fresh.Name, here);
                foreach (var e in DirectExprs(stmt)) TakeLocalLambdas(e);
                switch (stmt)
                {
                    case Stmt.BlockStmt b: TakeLocalWalk(b.Statements, here, System.Array.Empty<string>()); break;
                    case IfStmt f:
                        TakeLocalWalk(new[] { f.Then }, here, System.Array.Empty<string>());
                        if (f.Else != null) TakeLocalWalk(new[] { f.Else }, here, System.Array.Empty<string>());
                        break;
                    case WhileStmt w: TakeLocalWalk(new[] { w.Body }, here, System.Array.Empty<string>()); break;
                    case DoWhileStmt d: TakeLocalWalk(new[] { d.Body }, here, System.Array.Empty<string>()); break;
                    case ForStmt f:
                        if (f.Init != null) foreach (var e in DirectExprs(f.Init)) TakeLocalLambdas(e);
                        TakeLocalWalk(new[] { f.Body }, here, f.Init is VarDeclStmt v ? new[] { v.Name } : System.Array.Empty<string>());
                        break;
                    case ForeachStmt fe: TakeLocalWalk(new[] { fe.Body }, here, new[] { fe.VarName }); break;
                    case TryStmt t:
                        TakeLocalWalk(new Stmt[] { t.TryBlock }, here, System.Array.Empty<string>());
                        foreach (var c in t.Catches) TakeLocalWalk(new Stmt[] { c.Body }, here, new[] { c.VarName });
                        if (t.Finally != null) TakeLocalWalk(new Stmt[] { t.Finally }, here, System.Array.Empty<string>());
                        break;
                    case UnsafeStmt u: TakeLocalWalk(new Stmt[] { u.Body }, here, System.Array.Empty<string>()); break;
                    case FireStmt fs: TakeLocalBody(fs.Body.Statements); break;   // (a thread: a function of its own)
                    default:
                        // (a declaration inside a block - a local class - and the rest)
                        if (stmt is ClassDecl or MethodDecl or ConstructorDecl or PropertyDecl) TakeLocalDecl(stmt);
                        break;
                }
            }
        }

        /// <summary>The lambdas in an expression: each body is a function of its own.</summary>
        private void TakeLocalLambdas(Expr e)
        {
            foreach (var node in AllNodes(e, intoLambdas: false))
                if (node is LambdaExpr lam) TakeLocalBody(lam.Body.Statements);
        }

        /// <summary>`x.TakeLocal(...)` (or `try x.TakeLocal(...)`) as a statement: the name x.</summary>
        private static string? TakeLocalTarget(Stmt stmt)
        {
            var call = stmt is ExprStmt { Expression: var e } ? e is TryCallExpr t ? t.Call : e : null;
            return call is CallExpr { Callee: MemberExpr { Name: "TakeLocal", Target: IdentifierExpr id } } ? id.Name : null;
        }

        private void CheckBlockTakeLocal(string name, TakeLocalFrame frame, int line)
        {
            // the names declared in the block itself before the call (and those of its foreach/for/catch): they end with the block anyway
            var inner = new HashSet<string>(frame.Declared);
            for (int k = 0; k < frame.Index; k++) if (frame.Statements[k] is VarDeclStmt v) inner.Add(v.Name);
            bool local = inner.Contains(name);
            // 1. the rest of the block: x stored where it outlives the block
            for (int k = frame.Index + 1; k < frame.Statements.Count; k++)
            {
                var stmt = frame.Statements[k];
                if (stmt is VarDeclStmt vd) inner.Add(vd.Name);
                foreach (var node in StmtNodes(stmt))
                {
                    if (node is VarDeclStmt nested) inner.Add(nested.Name);   // (declared in a block inside this one: it ends before this one does)
                    if (Moves(node, name)) return;
                    string? where = StoredOutside(node, name, inner);
                    if (where != null)
                    {
                        Report(BlockTakeLocalCode, $"'{name}.TakeLocal()' gives '{name}' to this inner block, and {where} keeps it: it is destroyed when the block ends, and what keeps it refers to a destroyed object. Call TakeLocal where it is to live (at the top level of the function), give it with 'take {name}' (or '{name}.TakeTo(...)') to what keeps it, or leave TakeLocal out", line);
                        return;
                    }
                }
            }
            if (local) return;
            // 2. after the block: x used (`return x`) in the statements that follow it, up to the top level of the function
            for (var f = frame.Outer; f != null; f = f.Outer)
            {
                for (int k = f.Index + 1; k < f.Statements.Count; k++)
                {
                    var stmt = f.Statements[k];
                    if (stmt is VarDeclStmt { Name: var declaredName } && declaredName == name) return;   // (another variable of that name from here on)
                    foreach (var node in StmtNodes(stmt))
                    {
                        if (node is AssignExpr { Target: IdentifierExpr t } && t.Name == name) return;   // (x gets another value first)
                        if (node is IdentifierExpr id && id.Name == name)
                        {
                            Report(BlockTakeLocalCode, $"'{name}.TakeLocal()' gives '{name}' to the inner block it is called in: it is destroyed when the block ends, and '{name}' is used after it (line {id.Line}) - {(stmt is ReturnStmt ? "the caller gets" : "that is")} a destroyed object. Call TakeLocal at the top level of the function (or where '{name}' is to live), or leave it out", line);
                            return;
                        }
                    }
                }
                if (f.Declared.Contains(name) || Enumerable.Range(0, f.Index).Any(k => f.Statements[k] is VarDeclStmt v && v.Name == name)) return;   // (declared there: it ends with that block)
            }
        }

        /// <summary>TC036: the new value of `name` (declared by the statement at frame.Index) stored plainly in a field that outlives the block.</summary>
        private void CheckStoredLocal(string name, TakeLocalFrame frame)
        {
            // the names declared in the block (and those of its foreach/for/catch): what they hold ends with the block as well
            var inner = new HashSet<string>(frame.Declared);
            var rest = new List<object>();
            for (int k = 0; k < frame.Statements.Count; k++)
                foreach (var node in AllNodes(frame.Statements[k], intoLambdas: false))
                {
                    if (node is VarDeclStmt v) inner.Add(v.Name);
                    if (k > frame.Index) rest.Add(node);
                }
            // (given away, returned, handed to a call - which may take it: `panel.Add(x)` - or overwritten anywhere in the block: no warning; the order
            // is not followed)
            foreach (var node in rest)
                if (Moves(node, name) || node is ReturnStmt { Value: IdentifierExpr r } && r.Name == name
                    || node is CallExpr call && call.Args.Any(arg => arg is IdentifierExpr id && id.Name == name)
                    || node is NewExpr made && made.Args.Any(arg => arg is IdentifierExpr id && id.Name == name)) return;
            static string? Root(Expr e) => e switch { IdentifierExpr id => id.Name, ThisExpr or BaseExpr => "this", MemberExpr m => Root(m.Target), IndexExpr ix => Root(ix.Target), _ => null };
            foreach (var node in rest)
            {
                if (node is not AssignExpr { Value: IdentifierExpr value } a || value.Name != name) continue;
                string? where = a.Target switch
                {
                    MemberExpr m when !_tlProperties!.Contains(m.Name) && Root(m) is { } r && !inner.Contains(r) => $"the field '{m.Name}'",
                    IndexExpr { Target: MemberExpr m } ix when Root(ix) is { } r && !inner.Contains(r) => $"an element of the field '{m.Name}'",
                    _ => null,
                };
                if (where != null)
                    Report(StoredLocalCode, $"'{name}' holds a new value of its block, and {where} only refers to it: the value is destroyed when the block ends, and {where} then holds a destroyed object (DestroyedException at its next use). Give it to the field with '= take {name}' (or '{name}.TakeTo(...)'), or make it where it is kept", a.Line);
            }
        }

        /// <summary>Does x leave the caller here - `take x`, `x.TakeTo(...)`, `x.TakeUpwards()`, `x.TakeGlobal()` - or get another value?</summary>
        private static bool Moves(object node, string name) => node switch
        {
            UnaryExpr { Op: UnaryOp.Take or UnaryOp.TryTake, Operand: IdentifierExpr id } => id.Name == name,
            CallExpr { Callee: MemberExpr { Name: "TakeTo" or "TakeUpwards" or "TakeGlobal", Target: IdentifierExpr id } } => id.Name == name,
            AssignExpr { Target: IdentifierExpr t } => t.Name == name,
            _ => false,
        };

        /// <summary>Where x is kept beyond the block: a field, an element, a variable from outside the block, a container from outside it - null otherwise.</summary>
        private static string? StoredOutside(object node, string name, HashSet<string> inner)
        {
            static bool IsX(Expr e, string name) => e is IdentifierExpr id && id.Name == name;
            static string? Root(Expr e) => e switch { IdentifierExpr id => id.Name, MemberExpr m => Root(m.Target), IndexExpr ix => Root(ix.Target), _ => null };
            switch (node)
            {
                case AssignExpr a when IsX(a.Value, name):
                    return a.Target switch
                    {
                        IdentifierExpr t when !inner.Contains(t.Name) && t.Name != name => $"the variable '{t.Name}' from outside the block",
                        MemberExpr m when Root(m) is not { } r || !inner.Contains(r) => $"the field '{m.Name}'",
                        IndexExpr ix when Root(ix) is not { } r || !inner.Contains(r) => "an element of an array from outside the block",
                        _ => null,
                    };
                case CallExpr { Callee: MemberExpr m } call when s_keepingMethods.Contains(m.Name) && call.Args.Any(arg => IsX(arg, name))
                                                                && (Root(m.Target) is not { } r || !inner.Contains(r)):
                    return $"'{(m.Target is IdentifierExpr ti ? ti.Name : m.Target is MemberExpr tm ? tm.Name : "…")}.{m.Name}(...)'";
                default:
                    return null;
            }
        }

        /// <summary>The expressions of a statement itself (not those of the statements inside it).</summary>
        private static IEnumerable<Expr> DirectExprs(Stmt stmt)
        {
            switch (stmt)
            {
                case ExprStmt e: yield return e.Expression; break;
                case VarDeclStmt v: if (v.Initializer != null) yield return v.Initializer; foreach (var r in v.ArrayRanks) if (r != null) yield return r; break;
                case IfStmt f: yield return f.Condition; break;
                case WhileStmt w: yield return w.Condition; break;
                case DoWhileStmt d: yield return d.Condition; break;
                case ForStmt f: if (f.Condition != null) yield return f.Condition; if (f.Increment != null) yield return f.Increment; break;
                case ForeachStmt fe: yield return fe.Iterable; break;
                case ReturnStmt r: if (r.Value != null) yield return r.Value; break;
                case ThrowStmt t: yield return t.Value; break;
                case DeleteStmt d: yield return d.Target; break;
            }
        }

        /// <summary>Every node of a statement and the statements inside it, the lambdas included (a lambda that uses x uses it too).</summary>
        private static IEnumerable<object> StmtNodes(Stmt stmt) => AllNodes(stmt, intoLambdas: true);

        private static IEnumerable<object> AllNodes(object root, bool intoLambdas)
        {
            var stack = new Stack<object>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                yield return node;
                if (node is LambdaExpr && !intoLambdas) continue;
                var children = new List<object>();
                foreach (var prop in node.GetType().GetProperties())
                {
                    if (prop.GetIndexParameters().Length > 0 || prop.Name == "EqualityContract") continue;
                    var value = prop.GetValue(node);
                    if (value is Expr or Stmt or CatchClause) children.Add(value);
                    else if (value is System.Collections.IEnumerable list and not string)
                        foreach (var item in list) if (item is Expr or Stmt or CatchClause) children.Add(item);
                }
                for (int k = children.Count - 1; k >= 0; k--) stack.Push(children[k]);
            }
        }
    }
}
