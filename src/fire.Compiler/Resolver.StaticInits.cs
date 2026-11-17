using System;
using System.Collections.Generic;
using System.Linq;
using fire.Ast;
using fire.Resolving;

namespace fire.Compiler
{
    /// <summary>The order of the static initialisers (SPEC "Static members"): an initialiser runs after the initialisers of the static fields it reads -
    /// directly, or in the code it calls while it runs (static methods and properties, constructors and the initial values of the fields of the objects it
    /// makes, methods called on `this` or on a variable whose class is known). The compiler runs them in that order (a topological order, the order of
    /// the declarations where nothing decides); a cycle is an error (E4078). What the analysis cannot see: calls through `any` or an interface, through a
    /// lambda (its body runs later, so a lambda is not followed), through reflection, and methods of a derived class that override the one called.</summary>
    public sealed partial class Resolver
    {
        /// <summary>The class a member access `x.M` is made on, when the resolver knows it (`this`, a variable declared with a class or `new`) - for the calls
        /// the analysis follows.</summary>
        private readonly Dictionary<MemberExpr, string> _receiverClasses = new(ReferenceEqualityComparer.Instance);

        /// <summary>The static fields (class, field) whose initialiser reads each static field with an initialiser - what must run before it.</summary>
        private readonly Dictionary<(string Class, string Field), List<(string Class, string Field)>> _staticInitDeps = new();

        /// <summary>A piece of code the analysis walks: a method, a constructor, an accessor, an initial value - and the class it belongs to.</summary>
        private sealed record CodeUnit(ClassDecl Class, string Name, IReadOnlyList<object> Roots);

        private readonly Dictionary<object, CodeUnit> _codeUnits = new(ReferenceEqualityComparer.Instance);
        /// <summary>What a unit reads and calls (computed once): the static fields read, the units called.</summary>
        private readonly Dictionary<CodeUnit, (List<(string Class, string Field)> Reads, List<CodeUnit> Calls)> _unitFacts = new(ReferenceEqualityComparer.Instance);

        private CodeUnit CodeUnitOf(ClassDecl cd, object member)
        {
            if (_codeUnits.TryGetValue(member, out var unit)) return unit;
            unit = member switch
            {
                MethodDecl md => new CodeUnit(cd, $"{cd.Name}.{md.Name}()", new object[] { md.Body }.Concat(md.Params.Where(p => p.DefaultValue != null).Select(p => (object)p.DefaultValue!)).ToList()),
                ConstructorDecl ctor => new CodeUnit(cd, $"new {cd.Name}(...)", new object[] { ctor.Body }.Concat(ctor.BaseArgs ?? Array.Empty<Expr>())
                    .Concat(ctor.Params.Where(p => p.DefaultValue != null).Select(p => (object)p.DefaultValue!)).ToList()),
                FieldDecl fd => new CodeUnit(cd, $"{cd.Name}.{fd.Name}", fd.Initializer != null ? new object[] { fd.Initializer } : Array.Empty<object>()),
                Stmt.BlockStmt accessor => new CodeUnit(cd, $"{cd.Name}.{AccessorName(cd, accessor)}", new object[] { accessor }),
                _ => new CodeUnit(cd, cd.Name, Array.Empty<object>()),
            };
            _codeUnits[member] = unit;
            return unit;
        }

        private static string AccessorName(ClassDecl cd, Stmt.BlockStmt accessor) =>
            cd.Members.OfType<PropertyDecl>().FirstOrDefault(p => ReferenceEquals(p.Getter, accessor) || ReferenceEquals(p.Setter, accessor))?.Name ?? "?";

        /// <summary>Finds the order constraints of the static initialisers and reports their cycles (E4073 for a field that reads itself in its own initial
        /// value, E4078 for any other cycle) and the reads of top-level variables in a static initial value (E4079: the top-level code runs after them).</summary>
        private void OrderStaticInits()
        {
            var fields = new List<(ClassDecl Class, FieldDecl Field)>();
            foreach (var cd in _classes.Values)
                foreach (var fd in cd.Members.OfType<FieldDecl>())
                    if (fd.IsStatic && fd.Initializer != null && !fields.Any(f => ReferenceEquals(f.Class, cd) && f.Field.Name == fd.Name)) fields.Add((cd, fd));   // (a second one of the name: E4077)
            if (fields.Count == 0) return;
            var keys = new HashSet<(string, string)>(fields.Select(f => (f.Class.Name, f.Field.Name)));

            // (what each initialiser needs, and through which call - for the message)
            var needs = new Dictionary<(string Class, string Field), List<((string Class, string Field) Field, string? Via)>>();
            foreach (var (cd, fd) in fields)
            {
                var key = (cd.Name, fd.Name);
                var found = new List<((string, string) Field, string? Via)>();
                var seenFields = new HashSet<(string, string)>();
                var start = CodeUnitOf(cd, fd);
                var facts = Facts(start);
                foreach (var read in facts.Reads) if (keys.Contains(read) && seenFields.Add(read)) found.Add((read, null));
                // (the code it calls, breadth first: the first call that leads to a field is named in the message)
                var visited = new HashSet<CodeUnit>(ReferenceEqualityComparer.Instance) { start };
                var queue = new Queue<(CodeUnit Unit, string Via)>();
                foreach (var call in facts.Calls) if (visited.Add(call)) queue.Enqueue((call, call.Name));
                while (queue.Count > 0)
                {
                    var (unit, via) = queue.Dequeue();
                    var f = Facts(unit);
                    foreach (var read in f.Reads) if (keys.Contains(read) && seenFields.Add(read)) found.Add((read, via));
                    foreach (var call in f.Calls) if (visited.Add(call)) queue.Enqueue((call, via));
                }
                needs[key] = found;
                _staticInitDeps[key] = found.Select(x => x.Field).ToList();

                // a top-level variable read by the initial value itself: the top-level code runs only after all static initialisers
                foreach (var node in Walk(new object[] { fd.Initializer! }))
                    if (node is IdentifierExpr id && _refs.TryGetValue(id, out var r) && r is ResolvedRef.Global)
                    {
                        var error = new ResolverException($"The initialiser of the static field '{cd.Name}.{fd.Name}' reads the top-level variable '{id.Name}' - static fields are initialised before the top-level code runs, so it is still undefined there (make it a static field, or set the static field in the top-level code)", id.Line) { Source = cd.Source };
                        AddError(error);
                    }
            }

            // the cycles: strongly connected parts of the graph (Tarjan), each reported once with a path through it
            var index = new Dictionary<(string, string), int>();
            var low = new Dictionary<(string, string), int>();
            var onStack = new HashSet<(string, string)>();
            var stack = new Stack<(string, string)>();
            int counter = 0;
            var byKey = fields.ToDictionary(f => (f.Class.Name, f.Field.Name));
            void Connect((string, string) v)
            {
                index[v] = low[v] = counter++;
                stack.Push(v); onStack.Add(v);
                foreach (var (w, _) in needs[v])
                {
                    if (!index.ContainsKey(w)) { Connect(w); low[v] = Math.Min(low[v], low[w]); }
                    else if (onStack.Contains(w)) low[v] = Math.Min(low[v], index[w]);
                }
                if (low[v] != index[v]) return;
                var part = new List<(string, string)>();
                (string, string) x;
                do { x = stack.Pop(); onStack.Remove(x); part.Add(x); } while (x != v);
                if (part.Count == 1 && !needs[v].Any(n => n.Field == v)) return;
                ReportCycle(part, needs, byKey, fields);
            }
            foreach (var (cd, fd) in fields)
                if (!index.ContainsKey((cd.Name, fd.Name))) Connect((cd.Name, fd.Name));
        }

        private void ReportCycle(List<(string Class, string Field)> part, Dictionary<(string Class, string Field), List<((string Class, string Field) Field, string? Via)>> needs,
            Dictionary<(string, string), (ClassDecl Class, FieldDecl Field)> byKey, List<(ClassDecl Class, FieldDecl Field)> order)
        {
            var inPart = new HashSet<(string, string)>(part);
            // (the path starts at the field declared first)
            var first = order.First(f => inPart.Contains((f.Class.Name, f.Field.Name)));
            var start = (first.Class.Name, first.Field.Name);
            var (cd, fd) = first;
            if (part.Count == 1 && needs[start].First(n => n.Field == start).Via == null)
            {
                AddError(new ResolverException($"The initialiser of the static field '{cd.Name}.{fd.Name}' reads the field itself - it is still undefined there", fd.Line) { Source = cd.Source });
                return;
            }
            // a shortest way back to the start (breadth first inside the cycle)
            var previous = new Dictionary<(string, string), ((string, string) From, string? Via)>();
            var queue = new Queue<(string, string)>();
            queue.Enqueue(start);
            bool closed = false;
            ((string, string) From, string? Via) back = default;
            while (queue.Count > 0 && !closed)
            {
                var v = queue.Dequeue();
                foreach (var (w, via) in needs[v])
                {
                    if (!inPart.Contains(w)) continue;
                    if (w == start) { back = (v, via); closed = true; break; }
                    if (previous.ContainsKey(w)) continue;
                    previous[w] = (v, via);
                    queue.Enqueue(w);
                }
            }
            var steps = new List<string>();
            static string Step((string Class, string Field) f, string? via) => via == null ? $"{f.Class}.{f.Field}" : $"(through {via}) {f.Class}.{f.Field}";
            steps.Add(Step(start, back.Via));
            for (var at = back.From; at != start; at = previous[at].From) steps.Add(Step(at, previous[at].Via));
            steps.Add($"{start.Item1}.{start.Item2}");
            steps.Reverse();
            // (the steps read from the start: A.x needs B.y needs A.x)
            AddError(new ResolverException($"The initialisers of static fields need each other in a cycle: {string.Join(" -> ", steps)} - each one needs the value of the next one first; break the cycle (set one of them later, e.g. in a static method)", fd.Line) { Source = cd.Source });
        }

        /// <summary>What a piece of code reads and calls - the static fields it reads, the code it calls.</summary>
        private (List<(string Class, string Field)> Reads, List<CodeUnit> Calls) Facts(CodeUnit unit)
        {
            if (_unitFacts.TryGetValue(unit, out var known)) return known;
            var reads = new List<(string, string)>();
            var calls = new List<CodeUnit>();
            void Member(string className, string name, bool isStatic)
            {
                for (var cd = _classes.GetValueOrDefault(className); cd != null; cd = GetBaseClassDecl(cd))
                {
                    bool found = false;
                    foreach (var m in cd.Members)
                        switch (m)
                        {
                            case FieldDecl f when f.Name == name && f.IsStatic == isStatic:
                                if (isStatic) reads.Add((cd.Name, f.Name));
                                found = true; break;
                            case PropertyDecl p when p.Name == name && p.IsStatic == isStatic:
                                if (p.Getter != null) calls.Add(CodeUnitOf(cd, p.Getter));
                                if (p.Setter != null) calls.Add(CodeUnitOf(cd, p.Setter));
                                found = true; break;
                            case MethodDecl md when md.Name == name && md.IsStatic == isStatic:
                                calls.Add(CodeUnitOf(cd, md));
                                found = true; break;
                        }
                    if (found) return;
                }
            }
            void New(string className)
            {
                for (var cd = _classes.GetValueOrDefault(className); cd != null; cd = GetBaseClassDecl(cd))
                    foreach (var m in cd.Members)
                        if (m is ConstructorDecl || m is FieldDecl { IsStatic: false, Initializer: not null }) calls.Add(CodeUnitOf(cd, m));
            }
            foreach (var node in Walk(unit.Roots))
                switch (node)
                {
                    case IdentifierExpr id when _refs.TryGetValue(id, out var r):
                        if (r is ResolvedRef.StaticMember sm) Member(sm.ClassName, id.Name, true);
                        else if (r is ResolvedRef.ImplicitThisMember) Member(unit.Class.Name, id.Name, false);
                        break;
                    case MemberExpr me when _refs.TryGetValue(me, out var mr) && mr is ResolvedRef.StaticMember msm:
                        Member(msm.ClassName, me.Name, true);
                        break;
                    case MemberExpr me when _receiverClasses.TryGetValue(me, out var receiver):
                        Member(receiver, me.Name, false);
                        break;
                    case NewExpr ne:
                        New(GenericClassNames.ResolveNewTarget(ne.ClassRef, ne.TypeArgs?.Count ?? 0, _classes.ContainsKey));
                        break;
                }
            return _unitFacts[unit] = (reads, calls);
        }

        /// <summary>The nodes of the code below `roots` that run when it runs: not the body of a lambda (it runs when it is called - later), not the target of
        /// a plain assignment (`A.x = 1` writes, it does not read; its object is still walked).</summary>
        private static IEnumerable<object> Walk(IEnumerable<object> roots)
        {
            var stack = new Stack<object>(roots.Reverse());
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                if (node is LambdaExpr) continue;
                yield return node;
                var children = new List<object>();
                if (node is AssignExpr assign)
                {
                    // (`a += 1` is `a = a + 1`: the target object is the left operand too - read there)
                    if (assign.Target is MemberExpr written) children.Add(written.Target);
                    else if (assign.Target is not IdentifierExpr) children.Add(assign.Target);
                    children.Add(assign.Value);
                }
                else
                    foreach (var prop in node.GetType().GetProperties())
                    {
                        if (prop.GetIndexParameters().Length > 0 || prop.Name == "EqualityContract") continue;
                        var value = prop.GetValue(node);
                        if (value is Expr or Stmt) children.Add(value);
                        else if (value is LambdaParam lp) { if (lp.DefaultValue != null) children.Add(lp.DefaultValue); }
                        else if (value is System.Collections.IEnumerable list and not string)
                            foreach (var item in list) if (item is Expr or Stmt) children.Add(item);
                    }
                for (int k = children.Count - 1; k >= 0; k--) stack.Push(children[k]);
            }
        }
    }
}
