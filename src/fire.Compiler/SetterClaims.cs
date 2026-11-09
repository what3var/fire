using System.Collections.Generic;
using System.Linq;
using fire.Ast;

namespace fire.Compiler
{
    /// <summary>
    /// Where a setter passes its `value` on (SPEC 2.1 "Setters"): for every control-flow path through the setter body the claim site is the first
    /// assignment of `value` to a field or a property on that path, or - if the path has none - the first call with `value` as an argument.
    ///
    /// Every assignment `x.f = value` / `f = value` is a claim site: at run time it claims only while the setter call still owns the value, so the first one
    /// that runs on a path wins. A call `Store(value)` is a claim site only where it is the claim site of every path through it: no path through it has an
    /// assignment of `value`, and it is the first call with `value` on each. A call that would be the claim site of some path but not of all is not one: on
    /// those paths the value dies with the setter - <see cref="Ambiguous"/> lists them for the warning (TC018).
    /// Only the name `value` is followed; lambdas inside the setter are not looked into.
    /// </summary>
    public sealed class SetterClaims
    {
        /// <summary>The claim sites: the AssignExpr nodes and the argument expressions (the `value` IdentifierExpr nodes in a call) that claim.</summary>
        public HashSet<object> Sites { get; } = new(ReferenceEqualityComparer.Instance);

        /// <summary>Calls with `value` that would be the claim site of some path but are not (another path through them assigns `value`): their lines.</summary>
        public List<int> Ambiguous { get; } = new();

        private const int MaxPaths = 4096;

        private sealed record Event(object Node, bool IsAssign, int Line);

        private readonly HashSet<string> _locals = new();

        public static SetterClaims Analyze(Stmt.BlockStmt body)
        {
            var result = new SetterClaims();
            result.CollectLocals(body);
            var paths = result.PathsOf(body.Statements);
            if (paths == null)
            {
                // too many paths: only the assignments claim (each one at run time only while the setter still owns the value)
                foreach (var e in result.AllEvents(body.Statements).Where(e => e.IsAssign)) result.Sites.Add(e.Node);
                return result;
            }
            var calls = new Dictionary<object, (bool always, bool sometimes, int line)>(ReferenceEqualityComparer.Instance);
            foreach (var path in paths)
            {
                var events = path.Events;
                bool hasAssign = events.Any(e => e.IsAssign);
                foreach (var e in events.Where(e => e.IsAssign)) result.Sites.Add(e.Node);
                var firstCall = hasAssign ? null : events.FirstOrDefault(e => !e.IsAssign);
                foreach (var e in events.Where(e => !e.IsAssign).Distinct())
                {
                    bool claims = ReferenceEquals(e, firstCall);
                    calls[e.Node] = calls.TryGetValue(e.Node, out var c)
                        ? (c.always && claims, c.sometimes || claims, e.Line)
                        : (claims, claims, e.Line);
                }
            }
            foreach (var (node, c) in calls)
            {
                if (c.always) result.Sites.Add(node);
                else if (c.sometimes) result.Ambiguous.Add(c.line);
            }
            return result;
        }

        // ---- paths ----

        private sealed class Path
        {
            public List<Event> Events = new();
            public bool Ended;   // a `return`/`throw`: nothing after it on this path
            public Path Clone() => new() { Events = new List<Event>(Events), Ended = Ended };
        }

        /// <summary>The paths through a statement list (null: more than MaxPaths).</summary>
        private List<Path>? PathsOf(IReadOnlyList<Stmt> statements)
        {
            var paths = new List<Path> { new() };
            foreach (var stmt in statements)
            {
                var next = new List<Path>();
                foreach (var p in paths)
                {
                    if (p.Ended) { next.Add(p); continue; }
                    var tails = PathsOf(stmt);
                    if (tails == null) return null;
                    foreach (var t in tails)
                    {
                        var c = p.Clone();
                        c.Events.AddRange(t.Events);
                        c.Ended = t.Ended;
                        next.Add(c);
                    }
                    if (next.Count > MaxPaths) return null;
                }
                paths = next;
            }
            return paths;
        }

        private List<Path>? PathsOf(Stmt stmt)
        {
            Path Of(params Expr?[] exprs)
            {
                var p = new Path();
                foreach (var e in exprs) Events(e, p.Events);
                return p;
            }
            List<Path>? Join(Path head, List<Path>? tails)
            {
                if (tails == null) return null;
                return tails.Select(t => { var c = head.Clone(); c.Events.AddRange(t.Events); c.Ended = t.Ended; return c; }).ToList();
            }
            switch (stmt)
            {
                case Stmt.BlockStmt b: return PathsOf(b.Statements);
                case ExprStmt es: return new List<Path> { Of(es.Expression) };
                case VarDeclStmt vd: return new List<Path> { Of(vd.Initializer) };
                case DeleteStmt d: return new List<Path> { Of(d.Target) };
                case ReturnStmt r: { var p = Of(r.Value); p.Ended = true; return new List<Path> { p }; }
                case ThrowStmt t: { var p = Of(t.Value); p.Ended = true; return new List<Path> { p }; }
                case IfStmt i:
                {
                    var head = Of(i.Condition);
                    var then = Join(head, PathsOf(i.Then));
                    var other = i.Else != null ? Join(head, PathsOf(i.Else)) : new List<Path> { head };
                    return then == null || other == null ? null : then.Concat(other).ToList();
                }
                case WhileStmt w:
                {
                    // the body runs zero times or once (the first pass claims; more passes add nothing new)
                    var head = Of(w.Condition);
                    var body = Join(head, PathsOf(w.Body));
                    return body == null ? null : body.Select(p => { if (!p.Ended) Events(w.Condition, p.Events); return p; }).Append(head).ToList();
                }
                case DoWhileStmt dw:
                {
                    var body = PathsOf(dw.Body);
                    return body?.Select(p => { if (!p.Ended) Events(dw.Condition, p.Events); return p; }).ToList();
                }
                case ForStmt f:
                {
                    var init = f.Init != null ? PathsOf(f.Init) : new List<Path> { new() };
                    if (init == null) return null;
                    var result = new List<Path>();
                    foreach (var start in init)
                    {
                        var head = start.Clone();
                        Events(f.Condition, head.Events);
                        var body = Join(head, PathsOf(f.Body));
                        if (body == null) return null;
                        foreach (var p in body) if (!p.Ended) Events(f.Increment, p.Events);
                        result.AddRange(body);
                        result.Add(head);
                    }
                    return result;
                }
                case ForeachStmt fe:
                {
                    var head = Of(fe.Iterable);
                    var body = Join(head, PathsOf(fe.Body));
                    return body?.Append(head).ToList();
                }
                case TryStmt t:
                {
                    // the body, or a catch after (some of) it, then the finally
                    var paths = PathsOf(t.TryBlock);
                    if (paths == null) return null;
                    foreach (var c in t.Catches)
                    {
                        var catchPaths = PathsOf(c.Body);
                        if (catchPaths == null) return null;
                        paths.AddRange(catchPaths);
                    }
                    if (t.Finally != null)
                    {
                        var fin = PathsOf(t.Finally);
                        if (fin == null) return null;
                        paths = paths.SelectMany(p => fin.Select(f => { var c = p.Clone(); c.Events.AddRange(f.Events); c.Ended = p.Ended || f.Ended; return c; })).ToList();
                    }
                    return paths;
                }
                default:
                    return new List<Path> { new() };
            }
        }

        /// <summary>The events of all statements (for the fallback with too many paths).</summary>
        private IEnumerable<Event> AllEvents(IReadOnlyList<Stmt> statements)
        {
            var list = new List<Event>();
            void Walk(Stmt s)
            {
                switch (s)
                {
                    case Stmt.BlockStmt b: foreach (var x in b.Statements) Walk(x); break;
                    case ExprStmt es: Events(es.Expression, list); break;
                    case VarDeclStmt vd: Events(vd.Initializer, list); break;
                    case ReturnStmt r: Events(r.Value, list); break;
                    case IfStmt i: Events(i.Condition, list); Walk(i.Then); if (i.Else != null) Walk(i.Else); break;
                    case WhileStmt w: Events(w.Condition, list); Walk(w.Body); break;
                    case DoWhileStmt d: Walk(d.Body); Events(d.Condition, list); break;
                    case ForStmt f: if (f.Init != null) Walk(f.Init); Events(f.Condition, list); Walk(f.Body); Events(f.Increment, list); break;
                    case ForeachStmt fe: Events(fe.Iterable, list); Walk(fe.Body); break;
                    case TryStmt t: Walk(t.TryBlock); foreach (var c in t.Catches) Walk(c.Body); if (t.Finally != null) Walk(t.Finally); break;
                }
            }
            foreach (var s in statements) Walk(s);
            return list;
        }

        // ---- events in the order of evaluation ----

        private static bool IsValue(Expr? e) => e is IdentifierExpr { Name: "value" };

        private void Events(Expr? e, List<Event> list)
        {
            switch (e)
            {
                case null:
                case LambdaExpr:
                    return;   // (a lambda runs later, if at all)
                case AssignExpr a:
                    Events(a.Target is MemberExpr me ? me.Target : a.Target is IndexExpr ix ? ix.Target : null, list);
                    if (a.Target is IndexExpr ix2) Events(ix2.Index, list);
                    Events(a.Value, list);
                    if (IsValue(a.Value) && (a.Target is MemberExpr || a.Target is IdentifierExpr { Name: var bare } && bare != "value" && !_locals.Contains(bare)))
                        list.Add(new Event(a, true, a.Line));
                    return;
                case CallExpr c:
                    Events(c.Callee, list);
                    foreach (var arg in c.Args) Events(arg, list);
                    foreach (var arg in c.Args) if (IsValue(arg)) list.Add(new Event(arg, false, c.Line));
                    return;
                case NewExpr n:
                    foreach (var arg in n.Args) Events(arg, list);
                    foreach (var arg in n.Args) if (IsValue(arg)) list.Add(new Event(arg, false, n.Line));
                    return;
                case MemberExpr m: Events(m.Target, list); return;
                case IndexExpr i: Events(i.Target, list); Events(i.Index, list); return;
                case UnaryExpr u: Events(u.Operand, list); return;
                case BinaryExpr b: Events(b.Left, list); Events(b.Right, list); return;
                case ConditionalExpr c: Events(c.Condition, list); Events(c.Then, list); Events(c.Else, list); return;
                case ArrayLiteralExpr al: foreach (var x in al.Elements) Events(x, list); return;
                case TypeCoerceExpr t: Events(t.Operand, list); return;
                case UnitCoerceExpr u: Events(u.Operand, list); return;
                case IsOfExpr io: Events(io.Operand, list); return;
                case TryCallExpr tc: Events(tc.Call, list); return;
                case InterpolatedStringExpr s:
                    foreach (var part in s.Parts) if (part is InterpolationExprPart ep) Events(ep.Expression, list);
                    return;
                default:
                    return;
            }
        }

        /// <summary>The names the setter declares itself (a bare name among them is a local, not a field).</summary>
        private void CollectLocals(Stmt stmt)
        {
            switch (stmt)
            {
                case Stmt.BlockStmt b: foreach (var s in b.Statements) CollectLocals(s); break;
                case VarDeclStmt vd: _locals.Add(vd.Name); break;
                case IfStmt i: CollectLocals(i.Then); if (i.Else != null) CollectLocals(i.Else); break;
                case WhileStmt w: CollectLocals(w.Body); break;
                case DoWhileStmt d: CollectLocals(d.Body); break;
                case ForStmt f: if (f.Init != null) CollectLocals(f.Init); CollectLocals(f.Body); break;
                case ForeachStmt fe: _locals.Add(fe.VarName); CollectLocals(fe.Body); break;
                case TryStmt t: CollectLocals(t.TryBlock); foreach (var c in t.Catches) { _locals.Add(c.VarName); CollectLocals(c.Body); } if (t.Finally != null) CollectLocals(t.Finally); break;
            }
        }
    }
}
