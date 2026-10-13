using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace fire.Standard
{
    /// <summary>
    /// The query library (`#import "linq"`), written entirely in fire (like the UI library): lazy operators over any collection that
    /// `foreach` iterates (array, `List`, any class with `GetEnumerator`/`MoveNext`/`GetCurrent`).
    ///
    ///   var evenSquares = Linq.From(numbers).Where(x => x % 2 == 0).Select(x => x * x).ToList()
    ///   var expensive = list.Where(p => p.price > limit).OrderBy(p => p.price).First()     // `limit` is a local value: lambda capture
    ///
    /// `Linq.From(source)` returns a <c>Query</c>; on a `List` the same operators are available directly (`class extends List`).
    /// The operators are lazy (only `foreach`/a terminal operator such as `ToList`, `First`, `Count` drives the chain), every query can
    /// be iterated several times. `OrderBy`/`Reverse`/`Distinct` work with a copy of the elements (eager).
    /// </summary>
    public static class LinqPrelude
    {
        /// <summary>Operators that `List` additionally gets directly: name and parameter counts (overloading by count).</summary>
        private static readonly (string Name, int[] Arities)[] ListOperators =
        {
            ("Where", new[] { 1 }), ("Select", new[] { 1 }), ("SelectField", new[] { 1 }), ("SelectProperty", new[] { 1 }), ("SelectMember", new[] { 1 }), ("SelectMany", new[] { 1 }), ("Take", new[] { 1 }), ("Skip", new[] { 1 }),
            ("TakeWhile", new[] { 1 }), ("SkipWhile", new[] { 1 }), ("Concat", new[] { 1 }), ("Zip", new[] { 2 }), ("OrderBy", new[] { 1 }),
            ("OrderByDescending", new[] { 1 }), ("Reverse", new[] { 0 }), ("Distinct", new[] { 0 }),
            ("ToArray", new[] { 0 }), ("First", new[] { 0, 1 }), ("FirstOrDefault", new[] { 1, 2 }), ("Last", new[] { 0 }),
            ("ElementAt", new[] { 1 }), ("Any", new[] { 0, 1 }), ("All", new[] { 1 }), ("Count", new[] { 0, 1 }), ("Sum", new[] { 0, 1 }),
            ("Min", new[] { 0, 1 }), ("Max", new[] { 0, 1 }), ("Average", new[] { 0, 1 }), ("Aggregate", new[] { 2 }),
            ("Contains", new[] { 1 }), ("ForEach", new[] { 1 }), ("Join", new[] { 1 }),
        };

        public static readonly string Source = CoreSource + Extension("List", "new List(this.ToArray())") + Extension("array", "new List(this)");

        /// <summary>`class extends List` / `class extends array`: the same operators directly on the collection (arrays take the extension of a base type,
        /// SPEC 5.5.1; `this` there is the array itself).</summary>
        private static string Extension(string target, string toListExpression)
        {
            var sb = new StringBuilder($"\nclass extends {target} {{\n");
            foreach (var (name, arities) in ListOperators)
            {
                foreach (int arity in arities)
                {
                    // the forwarder takes the parameters (types included) of the method of `Query` it hands over to
                    var (returnType, declared) = QueryParameters(name, arity);
                    var arguments = string.Join(", ", declared.Select(ParameterName));
                    sb.Append($"    {returnType} {name}({string.Join(", ", declared)}) {{ return Linq.From(this).{name}({arguments}) }}\n");
                }
            }
            sb.Append($"    List ToList() {{ return {toListExpression} }}\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        /// <summary>The declared parameters (`bool lambda<any> pred`, `any fallback`) of the method `name` of the class `Query` that takes `arity` of them.</summary>
        private static (string ReturnType, List<string> Parameters) QueryParameters(string name, int arity)
        {
            var match = System.Text.RegularExpressions.Regex.Matches(CoreSource, @"^\s+([A-Za-z\[\]]+)\s+" + name + @"\(([^)]*)\)\s*\{", System.Text.RegularExpressions.RegexOptions.Multiline)
                .Select(m => (m.Groups[1].Value, SplitParameters(m.Groups[2].Value))).FirstOrDefault(p => p.Item2.Count == arity);
            if (match.Item2 == null) throw new InvalidOperationException($"Linq: no typed method {name} with {arity} parameter(s) in Query");
            return match;
        }

        /// <summary>Splits a parameter list at the commas outside of `<...>` (`lambda<any, any> fn, any seed`).</summary>
        private static List<string> SplitParameters(string text)
        {
            var parts = new List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '<') depth++;
                else if (text[i] == '>') depth--;
                else if (text[i] == ',' && depth == 0) { parts.Add(text.Substring(start, i - start).Trim()); start = i + 1; }
            }
            if (text.Substring(start).Trim().Length > 0) parts.Add(text.Substring(start).Trim());
            return parts;
        }

        /// <summary>The name of a declared parameter (`bool lambda<any> pred` -> `pred`, `pred` -> `pred`).</summary>
        private static string ParameterName(string declared)
        {
            string withoutDefault = declared.Split('=')[0].Trim();
            return withoutDefault.Substring(withoutDefault.LastIndexOfAny(new[] { ' ', '>' }) + 1);
        }

        private const string CoreSource = """
            class LinqEmptyException : Exception {
                string message
                construct(string message) { this.message = message }
            }

            class LinqWhereEnumerator : IEnumerator {
                IEnumerable inner
                lambda<any> pred
                any current
                construct(IEnumerable inner, lambda<any> pred) { this.inner = inner; this.pred = pred; inner.TakeTo(this) }
                bool MoveNext() {
                    var p = this.pred
                    while (this.inner.MoveNext()) {
                        var item = this.inner.GetCurrent()
                        if (p(item)) { this.current = item; return true }
                    }
                    return false
                }
                any GetCurrent() { return this.current }
            }

            class LinqSelectEnumerator : IEnumerator {
                IEnumerable inner
                lambda<any> fn
                any current
                construct(IEnumerable inner, lambda<any> fn) { this.inner = inner; this.fn = fn; inner.TakeTo(this) }
                bool MoveNext() {
                    if (!this.inner.MoveNext()) { return false }
                    var f = this.fn
                    this.current = f(this.inner.GetCurrent())
                    return true
                }
                any GetCurrent() { return this.current }
            }

            class LinqSelectManyEnumerator : IEnumerator {
                IEnumerable inner
                lambda<any> fn
                any sub
                any current
                construct(IEnumerable inner, lambda<any> fn) { this.inner = inner; this.fn = fn; inner.TakeTo(this) }
                bool MoveNext() {
                    var f = this.fn
                    while (true) {
                        if (this.sub != undefined) {
                            if (this.sub.MoveNext()) { this.current = this.sub.GetCurrent(); return true }
                            this.sub = undefined
                        }
                        if (!this.inner.MoveNext()) { return false }
                        var next = Linq.Iter(f(this.inner.GetCurrent()))
                        next.TakeTo(this)
                        this.sub = next
                    }
                }
                any GetCurrent() { return this.current }
            }

            class LinqTakeEnumerator : IEnumerator {
                IEnumerable inner
                int left
                construct(IEnumerable inner, int n) { this.inner = inner; this.left = n; inner.TakeTo(this) }
                bool MoveNext() {
                    if (this.left <= 0) { return false }
                    this.left = this.left - 1
                    return this.inner.MoveNext()
                }
                any GetCurrent() { return this.inner.GetCurrent() }
            }

            class LinqSkipEnumerator : IEnumerator {
                IEnumerable inner
                int skip
                construct(IEnumerable inner, int n) { this.inner = inner; this.skip = n; inner.TakeTo(this) }
                bool MoveNext() {
                    while (this.skip > 0) {
                        this.skip = this.skip - 1
                        if (!this.inner.MoveNext()) { return false }
                    }
                    return this.inner.MoveNext()
                }
                any GetCurrent() { return this.inner.GetCurrent() }
            }

            class LinqTakeWhileEnumerator : IEnumerator {
                IEnumerable inner
                lambda<any> pred
                any current
                bool done
                construct(IEnumerable inner, lambda<any> pred) { this.inner = inner; this.pred = pred; this.done = false; inner.TakeTo(this) }
                bool MoveNext() {
                    if (this.done) { return false }
                    var p = this.pred
                    if (this.inner.MoveNext()) {
                        var item = this.inner.GetCurrent()
                        if (p(item)) { this.current = item; return true }
                    }
                    this.done = true
                    return false
                }
                any GetCurrent() { return this.current }
            }

            class LinqSkipWhileEnumerator : IEnumerator {
                IEnumerable inner
                lambda<any> pred
                bool started
                construct(IEnumerable inner, lambda<any> pred) { this.inner = inner; this.pred = pred; this.started = false; inner.TakeTo(this) }
                bool MoveNext() {
                    if (this.started) { return this.inner.MoveNext() }
                    this.started = true
                    var p = this.pred
                    while (this.inner.MoveNext()) {
                        if (!p(this.inner.GetCurrent())) { return true }
                    }
                    return false
                }
                any GetCurrent() { return this.inner.GetCurrent() }
            }

            class LinqConcatEnumerator : IEnumerator {
                IEnumerable first
                IEnumerable second
                bool inSecond
                construct(IEnumerable first, IEnumerable second) { this.first = first; this.second = second; this.inSecond = false; first.TakeTo(this); second.TakeTo(this) }
                bool MoveNext() {
                    if (!this.inSecond) {
                        if (this.first.MoveNext()) { return true }
                        this.inSecond = true
                    }
                    return this.second.MoveNext()
                }
                any GetCurrent() {
                    if (this.inSecond) { return this.second.GetCurrent() }
                    return this.first.GetCurrent()
                }
            }

            class LinqZipEnumerator : IEnumerator {
                IEnumerable a
                IEnumerable b
                lambda<any, any> fn
                any current
                construct(IEnumerable a, IEnumerable b, lambda<any, any> fn) { this.a = a; this.b = b; this.fn = fn; a.TakeTo(this); b.TakeTo(this) }
                bool MoveNext() {
                    if (!this.a.MoveNext()) { return false }
                    if (!this.b.MoveNext()) { return false }
                    var f = this.fn
                    this.current = f(this.a.GetCurrent(), this.b.GetCurrent())
                    return true
                }
                any GetCurrent() { return this.current }
            }

            class LinqRangeEnumerator : IEnumerator {
                int next
                int left
                int current
                construct(int start, int count) { this.next = start; this.left = count }
                bool MoveNext() {
                    if (this.left <= 0) { return false }
                    this.current = this.next
                    this.next = this.next + 1
                    this.left = this.left - 1
                    return true
                }
                int GetCurrent() { return this.current }
            }

            class Linq {
                // An enumerator for everything iterable: objects with GetEnumerator() and arrays (which are also IEnumerable)
                static IEnumerator Iter(IEnumerable source) { return source.GetEnumerator() }

                // Reads the member chain `path` (names from outside to inside) from `obj` - via reflection, i.e. with its access rules;
                // the last member must match `kind` ("field", "property" or "member")
                static any GetPath(any obj, any path, string kind) {
                    var o = obj
                    for (var i = 0; i < path.length; i = i + 1) {
                        if (i == path.length - 1) {
                            var actual = __refl_member_kind(o, path[i])
                            if (!Reflect.KindAllowed(actual, kind)) { throw new ReflectionException(Reflect.KindMessage(path[i], actual, kind)) }
                        }
                        o = Reflect.Get(o, path[i])
                    }
                    return o
                }

                static Query From(IEnumerable source) {
                    var s = source
                    var query = new Query(() => Linq.Iter(s))
                    try source.TakeTo(query)   // `Linq.From(new Bag())`: a fresh argument belongs to this call - the query keeps it
                    return query
                }

                // A query over a collection that was built for it (the result of an eager operator): the query owns the collection -
                // it reads it lazily, long after the function that built it has returned.
                static Query FromOwned(IEnumerable source) {
                    var s = source
                    var query = new Query(() => Linq.Iter(s))
                    source.TakeTo(query)
                    return query
                }

                static Query Range(int start, int count) {
                    return new Query(() => new LinqRangeEnumerator(start, count))
                }

                static Query Repeat(any value, int count) {
                    var items = new class[count]
                    for (var i = 0; i < count; i = i + 1) { items[i] = value }
                    return Linq.FromOwned(items)
                }

                // Stable sorting (merge sort): returns the `items` in the order of the `keys` (arrays of equal length)
                static any Sort(any items, any keys, bool desc) {
                    var n = items.length
                    var idx = new int[n]
                    var tmp = new int[n]
                    for (var i = 0; i < n; i = i + 1) { idx[i] = i }
                    var width = 1
                    while (width < n) {
                        var lo = 0
                        while (lo < n) {
                            var mid = lo + width
                            if (mid > n) { mid = n }
                            var hi = lo + 2 * width
                            if (hi > n) { hi = n }
                            var a = lo
                            var b = mid
                            var k = lo
                            while (a < mid && b < hi) {
                                var takeRight = false
                                if (desc) { takeRight = keys[idx[a]] < keys[idx[b]] } else { takeRight = keys[idx[b]] < keys[idx[a]] }
                                if (takeRight) { tmp[k] = idx[b]; b = b + 1 } else { tmp[k] = idx[a]; a = a + 1 }
                                k = k + 1
                            }
                            while (a < mid) { tmp[k] = idx[a]; a = a + 1; k = k + 1 }
                            while (b < hi) { tmp[k] = idx[b]; b = b + 1; k = k + 1 }
                            lo = lo + 2 * width
                        }
                        for (var i = 0; i < n; i = i + 1) { idx[i] = tmp[i] }
                        width = width * 2
                    }
                    var result = new class[n]
                    for (var i = 0; i < n; i = i + 1) { result[i] = items[idx[i]] }
                    return result
                }
            }

            class Query : IEnumerable {
                lambda factory

                // `factory` is a lambda without parameters that supplies a fresh enumerator on every pass
                construct(lambda factory) { this.factory = factory }

                IEnumerator GetEnumerator() {
                    var f = this.factory
                    return f()
                }

                // ---- lazy operators
                Query Where(bool lambda<any> pred) {
                    var f = this.factory
                    return new Query(() => new LinqWhereEnumerator(f(), pred))
                }
                Query Select(lambda<any> fn) {
                    var f = this.factory
                    return new Query(() => new LinqSelectEnumerator(f(), fn))
                }
                // Projection onto a member, chosen via selector: `list.SelectMember(p => p.name)` (field or property), `SelectField` (a field only),
                // `SelectProperty` (a property only) - the lambda must be a pure member chain (see `lambda member<T>`). Unlike
                // `Select(fn)` the access goes via reflection (with its access rules) and a member of the wrong kind is a ReflectionException.
                Query SelectField(lambda field<class> sel) {
                    var f = this.factory
                    var path = flat sel.Path   // (the selector dies with this call; the query keeps its own copy)
                    var query = new Query(() => new LinqSelectEnumerator(f(), x => Linq.GetPath(x, path, "field")))
                    path.TakeTo(query)
                    return query
                }
                Query SelectProperty(lambda property<class> sel) {
                    var f = this.factory
                    var path = flat sel.Path   // (the selector dies with this call; the query keeps its own copy)
                    var query = new Query(() => new LinqSelectEnumerator(f(), x => Linq.GetPath(x, path, "property")))
                    path.TakeTo(query)
                    return query
                }
                Query SelectMember(lambda member<class> sel) {
                    var f = this.factory
                    var path = flat sel.Path   // (the selector dies with this call; the query keeps its own copy)
                    var query = new Query(() => new LinqSelectEnumerator(f(), x => Linq.GetPath(x, path, "member")))
                    path.TakeTo(query)
                    return query
                }
                Query SelectMany(lambda<any> fn) {
                    var f = this.factory
                    return new Query(() => new LinqSelectManyEnumerator(f(), fn))
                }
                Query Take(int n) {
                    var f = this.factory
                    return new Query(() => new LinqTakeEnumerator(f(), n))
                }
                Query Skip(int n) {
                    var f = this.factory
                    return new Query(() => new LinqSkipEnumerator(f(), n))
                }
                Query TakeWhile(bool lambda<any> pred) {
                    var f = this.factory
                    return new Query(() => new LinqTakeWhileEnumerator(f(), pred))
                }
                Query SkipWhile(bool lambda<any> pred) {
                    var f = this.factory
                    return new Query(() => new LinqSkipWhileEnumerator(f(), pred))
                }
                Query Concat(IEnumerable other) {
                    var f = this.factory
                    return new Query(() => new LinqConcatEnumerator(f(), Linq.Iter(other)))
                }
                Query Zip(IEnumerable other, lambda<any, any> fn) {
                    var f = this.factory
                    return new Query(() => new LinqZipEnumerator(f(), Linq.Iter(other), fn))
                }

                // ---- eager operators (work on a copy, return a Query again)
                Query OrderBy(lambda<any> key) { return this.Ordered(key, false) }
                Query OrderByDescending(lambda<any> key) { return this.Ordered(key, true) }
                Query Ordered(any key, bool desc) {
                    var items = this.ToArray()
                    var keys = new class[items.length]
                    for (var i = 0; i < items.length; i = i + 1) { keys[i] = key(items[i]) }
                    return Linq.FromOwned(Linq.Sort(items, keys, desc))
                }
                Query Reverse() {
                    var items = this.ToArray()
                    var n = items.length
                    var result = new class[n]
                    for (var i = 0; i < n; i = i + 1) { result[i] = items[n - 1 - i] }
                    return Linq.FromOwned(result)
                }
                Query Distinct() {
                    var seen = new List()
                    foreach (x in this) {
                        var known = false
                        foreach (y in seen) { if (x == y) { known = true } }
                        if (!known) { seen.Add(x) }
                    }
                    return Linq.FromOwned(seen)
                }

                // ---- Abschluss-Operatoren
                List ToList() {
                    var result = new List()
                    foreach (x in this) { result.Add(x) }
                    return result
                }
                any ToArray() {
                    var l = this.ToList()
                    var result = new class[l.count]
                    for (var i = 0; i < l.count; i = i + 1) { result[i] = l[i] }
                    return result
                }
                any First() {
                    foreach (x in this) { return x }
                    throw new LinqEmptyException("The sequence contains no element")
                }
                any First(bool lambda<any> pred) {
                    foreach (x in this) { if (pred(x)) { return x } }
                    throw new LinqEmptyException("The sequence contains no matching element")
                }
                any FirstOrDefault(any fallback) {
                    foreach (x in this) { return x }
                    return fallback
                }
                any FirstOrDefault(bool lambda<any> pred, any fallback) {
                    foreach (x in this) { if (pred(x)) { return x } }
                    return fallback
                }
                any Last() {
                    var found = false
                    var last = undefined
                    foreach (x in this) { last = x; found = true }
                    if (!found) { throw new LinqEmptyException("The sequence contains no element") }
                    return last
                }
                any ElementAt(int index) {
                    var i = 0
                    foreach (x in this) {
                        if (i == index) { return x }
                        i = i + 1
                    }
                    throw new LinqEmptyException("Index " + index + " is outside of the sequence")
                }
                bool Any() {
                    foreach (x in this) { return true }
                    return false
                }
                bool Any(bool lambda<any> pred) {
                    foreach (x in this) { if (pred(x)) { return true } }
                    return false
                }
                bool All(bool lambda<any> pred) {
                    foreach (x in this) { if (!pred(x)) { return false } }
                    return true
                }
                int Count() {
                    var n = 0
                    foreach (x in this) { n = n + 1 }
                    return n
                }
                int Count(bool lambda<any> pred) {
                    var n = 0
                    foreach (x in this) { if (pred(x)) { n = n + 1 } }
                    return n
                }
                int Sum() {
                    var s = 0
                    foreach (x in this) { s = s + x }
                    return s
                }
                int Sum(lambda<any> fn) {
                    var s = 0
                    foreach (x in this) { s = s + fn(x) }
                    return s
                }
                any Min() { return this.Select(x => x).Extreme(true) }
                any Min(lambda<any> fn) { return this.Select(fn).Extreme(true) }
                any Max() { return this.Select(x => x).Extreme(false) }
                any Max(lambda<any> fn) { return this.Select(fn).Extreme(false) }
                any Extreme(bool smallest) {
                    var found = false
                    var best = undefined
                    foreach (x in this) {
                        if (!found) { best = x; found = true }
                        else if (smallest) { if (x < best) { best = x } }
                        else { if (x > best) { best = x } }
                    }
                    if (!found) { throw new LinqEmptyException("The sequence contains no element") }
                    return best
                }
                float Average() { return this.Average(x => x) }
                float Average(lambda<any> fn) {
                    var s = 0.0
                    var n = 0
                    foreach (x in this) { s = s + fn(x); n = n + 1 }
                    if (n == 0) { throw new LinqEmptyException("The sequence contains no element") }
                    return s / n
                }
                any Aggregate(any seed, lambda<any, any> fn) {
                    var acc = seed
                    foreach (x in this) { acc = fn(acc, x) }
                    return acc
                }
                bool Contains(any value) {
                    foreach (x in this) { if (x == value) { return true } }
                    return false
                }
                undefined ForEach(lambda<any> fn) {
                    foreach (x in this) { fn(x) }
                }
                string Join(string separator) {
                    var s = ""
                    var first = true
                    foreach (x in this) {
                        if (!first) { s = s + separator }
                        s = s + x
                        first = false
                    }
                    return s
                }
            }
            """;
    }
}
