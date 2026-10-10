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
                    var parameters = string.Join(", ", System.Linq.Enumerable.Range(0, arity).Select(i => "a" + i));
                    sb.Append($"    {name}({parameters}) {{ return Linq.From(this).{name}({parameters}) }}\n");
                }
            }
            sb.Append($"    ToList() {{ return {toListExpression} }}\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        private const string CoreSource = """
            class LinqEmptyException : Exception {
                string message
                construct(string message) { this.message = message }
            }

            class LinqWhereEnumerator : IEnumerator {
                class inner
                class pred
                class current
                construct(class inner, class pred) { this.inner = inner; this.pred = pred; inner.TakeTo(this) }
                MoveNext() {
                    var p = this.pred
                    while (this.inner.MoveNext()) {
                        var item = this.inner.GetCurrent()
                        if (p(item)) { this.current = item; return true }
                    }
                    return false
                }
                GetCurrent() { return this.current }
            }

            class LinqSelectEnumerator : IEnumerator {
                class inner
                class fn
                class current
                construct(class inner, class fn) { this.inner = inner; this.fn = fn; inner.TakeTo(this) }
                MoveNext() {
                    if (!this.inner.MoveNext()) { return false }
                    var f = this.fn
                    this.current = f(this.inner.GetCurrent())
                    return true
                }
                GetCurrent() { return this.current }
            }

            class LinqSelectManyEnumerator : IEnumerator {
                class inner
                class fn
                class sub
                class current
                construct(class inner, class fn) { this.inner = inner; this.fn = fn; inner.TakeTo(this) }
                MoveNext() {
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
                GetCurrent() { return this.current }
            }

            class LinqTakeEnumerator : IEnumerator {
                class inner
                int left
                construct(class inner, int n) { this.inner = inner; this.left = n; inner.TakeTo(this) }
                MoveNext() {
                    if (this.left <= 0) { return false }
                    this.left = this.left - 1
                    return this.inner.MoveNext()
                }
                GetCurrent() { return this.inner.GetCurrent() }
            }

            class LinqSkipEnumerator : IEnumerator {
                class inner
                int skip
                construct(class inner, int n) { this.inner = inner; this.skip = n; inner.TakeTo(this) }
                MoveNext() {
                    while (this.skip > 0) {
                        this.skip = this.skip - 1
                        if (!this.inner.MoveNext()) { return false }
                    }
                    return this.inner.MoveNext()
                }
                GetCurrent() { return this.inner.GetCurrent() }
            }

            class LinqTakeWhileEnumerator : IEnumerator {
                class inner
                class pred
                class current
                bool done
                construct(class inner, class pred) { this.inner = inner; this.pred = pred; this.done = false; inner.TakeTo(this) }
                MoveNext() {
                    if (this.done) { return false }
                    var p = this.pred
                    if (this.inner.MoveNext()) {
                        var item = this.inner.GetCurrent()
                        if (p(item)) { this.current = item; return true }
                    }
                    this.done = true
                    return false
                }
                GetCurrent() { return this.current }
            }

            class LinqSkipWhileEnumerator : IEnumerator {
                class inner
                class pred
                bool started
                construct(class inner, class pred) { this.inner = inner; this.pred = pred; this.started = false; inner.TakeTo(this) }
                MoveNext() {
                    if (this.started) { return this.inner.MoveNext() }
                    this.started = true
                    var p = this.pred
                    while (this.inner.MoveNext()) {
                        if (!p(this.inner.GetCurrent())) { return true }
                    }
                    return false
                }
                GetCurrent() { return this.inner.GetCurrent() }
            }

            class LinqConcatEnumerator : IEnumerator {
                class first
                class second
                bool inSecond
                construct(class first, class second) { this.first = first; this.second = second; this.inSecond = false; first.TakeTo(this); second.TakeTo(this) }
                MoveNext() {
                    if (!this.inSecond) {
                        if (this.first.MoveNext()) { return true }
                        this.inSecond = true
                    }
                    return this.second.MoveNext()
                }
                GetCurrent() {
                    if (this.inSecond) { return this.second.GetCurrent() }
                    return this.first.GetCurrent()
                }
            }

            class LinqZipEnumerator : IEnumerator {
                class a
                class b
                class fn
                class current
                construct(class a, class b, class fn) { this.a = a; this.b = b; this.fn = fn; a.TakeTo(this); b.TakeTo(this) }
                MoveNext() {
                    if (!this.a.MoveNext()) { return false }
                    if (!this.b.MoveNext()) { return false }
                    var f = this.fn
                    this.current = f(this.a.GetCurrent(), this.b.GetCurrent())
                    return true
                }
                GetCurrent() { return this.current }
            }

            class LinqRangeEnumerator : IEnumerator {
                int next
                int left
                int current
                construct(int start, int count) { this.next = start; this.left = count }
                MoveNext() {
                    if (this.left <= 0) { return false }
                    this.current = this.next
                    this.next = this.next + 1
                    this.left = this.left - 1
                    return true
                }
                GetCurrent() { return this.current }
            }

            class Linq {
                // An enumerator for everything iterable: objects with GetEnumerator() and arrays (which are also IEnumerable)
                static Iter(class source) { return source.GetEnumerator() }

                // Reads the member chain `path` (names from outside to inside) from `obj` - via reflection, i.e. with its access rules;
                // the last member must match `kind` ("field", "property" or "member")
                static GetPath(class obj, class path, string kind) {
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

                static From(class source) {
                    var s = source
                    var query = new Query(() => Linq.Iter(s))
                    try source.TakeTo(query)   // `Linq.From(new Bag())`: a fresh argument belongs to this call - the query keeps it
                    return query
                }

                // A query over a collection that was built for it (the result of an eager operator): the query owns the collection -
                // it reads it lazily, long after the function that built it has returned.
                static FromOwned(class source) {
                    var s = source
                    var query = new Query(() => Linq.Iter(s))
                    source.TakeTo(query)
                    return query
                }

                static Range(int start, int count) {
                    return new Query(() => new LinqRangeEnumerator(start, count))
                }

                static Repeat(class value, int count) {
                    var items = new class[count]
                    for (var i = 0; i < count; i = i + 1) { items[i] = value }
                    return Linq.FromOwned(items)
                }

                // Stable sorting (merge sort): returns the `items` in the order of the `keys` (arrays of equal length)
                static Sort(class items, class keys, bool desc) {
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
                class factory

                // `factory` is a lambda without parameters that supplies a fresh enumerator on every pass
                construct(class factory) { this.factory = factory }

                GetEnumerator() {
                    var f = this.factory
                    return f()
                }

                // ---- lazy operators
                Where(lambda<int> pred) {
                    var f = this.factory
                    return new Query(() => new LinqWhereEnumerator(f(), pred))
                }
                Select(lambda<int> fn) {
                    var f = this.factory
                    return new Query(() => new LinqSelectEnumerator(f(), fn))
                }
                // Projection onto a member, chosen via selector: `list.SelectMember(p => p.name)` (field or property), `SelectField` (a field only),
                // `SelectProperty` (a property only) - the lambda must be a pure member chain (see `lambda member<T>`). Unlike
                // `Select(fn)` the access goes via reflection (with its access rules) and a member of the wrong kind is a ReflectionException.
                SelectField(lambda field<class> sel) {
                    var f = this.factory
                    var path = flat sel.Path   // (the selector dies with this call; the query keeps its own copy)
                    var query = new Query(() => new LinqSelectEnumerator(f(), x => Linq.GetPath(x, path, "field")))
                    path.TakeTo(query)
                    return query
                }
                SelectProperty(lambda property<class> sel) {
                    var f = this.factory
                    var path = flat sel.Path   // (the selector dies with this call; the query keeps its own copy)
                    var query = new Query(() => new LinqSelectEnumerator(f(), x => Linq.GetPath(x, path, "property")))
                    path.TakeTo(query)
                    return query
                }
                SelectMember(lambda member<class> sel) {
                    var f = this.factory
                    var path = flat sel.Path   // (the selector dies with this call; the query keeps its own copy)
                    var query = new Query(() => new LinqSelectEnumerator(f(), x => Linq.GetPath(x, path, "member")))
                    path.TakeTo(query)
                    return query
                }
                SelectMany(lambda<int> fn) {
                    var f = this.factory
                    return new Query(() => new LinqSelectManyEnumerator(f(), fn))
                }
                Take(int n) {
                    var f = this.factory
                    return new Query(() => new LinqTakeEnumerator(f(), n))
                }
                Skip(int n) {
                    var f = this.factory
                    return new Query(() => new LinqSkipEnumerator(f(), n))
                }
                TakeWhile(lambda<int> pred) {
                    var f = this.factory
                    return new Query(() => new LinqTakeWhileEnumerator(f(), pred))
                }
                SkipWhile(lambda<int> pred) {
                    var f = this.factory
                    return new Query(() => new LinqSkipWhileEnumerator(f(), pred))
                }
                Concat(class other) {
                    var f = this.factory
                    return new Query(() => new LinqConcatEnumerator(f(), Linq.Iter(other)))
                }
                Zip(class other, lambda<int, int> fn) {
                    var f = this.factory
                    return new Query(() => new LinqZipEnumerator(f(), Linq.Iter(other), fn))
                }

                // ---- eager operators (work on a copy, return a Query again)
                OrderBy(lambda<int> key) { return this.Ordered(key, false) }
                OrderByDescending(lambda<int> key) { return this.Ordered(key, true) }
                Ordered(class key, bool desc) {
                    var items = this.ToArray()
                    var keys = new class[items.length]
                    for (var i = 0; i < items.length; i = i + 1) { keys[i] = key(items[i]) }
                    return Linq.FromOwned(Linq.Sort(items, keys, desc))
                }
                Reverse() {
                    var items = this.ToArray()
                    var n = items.length
                    var result = new class[n]
                    for (var i = 0; i < n; i = i + 1) { result[i] = items[n - 1 - i] }
                    return Linq.FromOwned(result)
                }
                Distinct() {
                    var seen = new List()
                    foreach (x in this) {
                        var known = false
                        foreach (y in seen) { if (x == y) { known = true } }
                        if (!known) { seen.Add(x) }
                    }
                    return Linq.FromOwned(seen)
                }

                // ---- Abschluss-Operatoren
                ToList() {
                    var result = new List()
                    foreach (x in this) { result.Add(x) }
                    return result
                }
                ToArray() {
                    var l = this.ToList()
                    var result = new class[l.count]
                    for (var i = 0; i < l.count; i = i + 1) { result[i] = l[i] }
                    return result
                }
                First() {
                    foreach (x in this) { return x }
                    throw new LinqEmptyException("The sequence contains no element")
                }
                First(lambda<int> pred) {
                    foreach (x in this) { if (pred(x)) { return x } }
                    throw new LinqEmptyException("The sequence contains no matching element")
                }
                FirstOrDefault(class fallback) {
                    foreach (x in this) { return x }
                    return fallback
                }
                FirstOrDefault(lambda<int> pred, class fallback) {
                    foreach (x in this) { if (pred(x)) { return x } }
                    return fallback
                }
                Last() {
                    var found = false
                    var last = undefined
                    foreach (x in this) { last = x; found = true }
                    if (!found) { throw new LinqEmptyException("The sequence contains no element") }
                    return last
                }
                ElementAt(int index) {
                    var i = 0
                    foreach (x in this) {
                        if (i == index) { return x }
                        i = i + 1
                    }
                    throw new LinqEmptyException("Index " + index + " is outside of the sequence")
                }
                Any() {
                    foreach (x in this) { return true }
                    return false
                }
                Any(lambda<int> pred) {
                    foreach (x in this) { if (pred(x)) { return true } }
                    return false
                }
                All(lambda<int> pred) {
                    foreach (x in this) { if (!pred(x)) { return false } }
                    return true
                }
                Count() {
                    var n = 0
                    foreach (x in this) { n = n + 1 }
                    return n
                }
                Count(lambda<int> pred) {
                    var n = 0
                    foreach (x in this) { if (pred(x)) { n = n + 1 } }
                    return n
                }
                Sum() {
                    var s = 0
                    foreach (x in this) { s = s + x }
                    return s
                }
                Sum(lambda<int> fn) {
                    var s = 0
                    foreach (x in this) { s = s + fn(x) }
                    return s
                }
                Min() { return this.Select(x => x).Extreme(true) }
                Min(lambda<int> fn) { return this.Select(fn).Extreme(true) }
                Max() { return this.Select(x => x).Extreme(false) }
                Max(lambda<int> fn) { return this.Select(fn).Extreme(false) }
                Extreme(bool smallest) {
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
                Average() { return this.Average(x => x) }
                Average(lambda<int> fn) {
                    var s = 0.0
                    var n = 0
                    foreach (x in this) { s = s + fn(x); n = n + 1 }
                    if (n == 0) { throw new LinqEmptyException("The sequence contains no element") }
                    return s / n
                }
                Aggregate(class seed, lambda<int, int> fn) {
                    var acc = seed
                    foreach (x in this) { acc = fn(acc, x) }
                    return acc
                }
                Contains(class value) {
                    foreach (x in this) { if (x == value) { return true } }
                    return false
                }
                ForEach(lambda<int> fn) {
                    foreach (x in this) { fn(x) }
                }
                Join(string separator) {
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
