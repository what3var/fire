using System.Text;

namespace fire.Standard
{
    /// <summary>
    /// Die Abfrage-Bibliothek (`#import "linq"`), komplett in fire geschrieben (wie die UI-Bibliothek): träge Operatoren über jede Sammlung, die
    /// `foreach` durchläuft (Array, `List`, jede Klasse mit `GetEnumerator`/`MoveNext`/`GetCurrent`).
    ///
    ///   var geradeQuadrate = Linq.From(zahlen).Where(x => x % 2 == 0).Select(x => x * x).ToList()
    ///   var teuer = list.Where(p => p.price > limit).OrderBy(p => p.price).First()     // `limit` ist ein lokaler Wert: Lambda-Capture
    ///
    /// `Linq.From(quelle)` liefert eine <c>Query</c>; auf einer `List` stehen dieselben Operatoren direkt zur Verfügung (`class extends List`).
    /// Die Operatoren sind träge (erst `foreach`/ein Abschluss-Operator wie `ToList`, `First`, `Count` treibt die Kette), jede Abfrage lässt
    /// sich mehrfach durchlaufen. `OrderBy`/`Reverse`/`Distinct` arbeiten mit einer Kopie der Elemente (eifrig).
    /// </summary>
    public static class LinqPrelude
    {
        /// <summary>Operatoren, die `List` zusätzlich direkt bekommt: Name und Parameteranzahlen (Überladung nach Anzahl).</summary>
        private static readonly (string Name, int[] Arities)[] ListOperators =
        {
            ("Where", new[] { 1 }), ("Select", new[] { 1 }), ("SelectMany", new[] { 1 }), ("Take", new[] { 1 }), ("Skip", new[] { 1 }),
            ("TakeWhile", new[] { 1 }), ("SkipWhile", new[] { 1 }), ("Concat", new[] { 1 }), ("Zip", new[] { 2 }), ("OrderBy", new[] { 1 }),
            ("OrderByDescending", new[] { 1 }), ("Reverse", new[] { 0 }), ("Distinct", new[] { 0 }),
            ("ToArray", new[] { 0 }), ("First", new[] { 0, 1 }), ("FirstOrDefault", new[] { 1, 2 }), ("Last", new[] { 0 }),
            ("ElementAt", new[] { 1 }), ("Any", new[] { 0, 1 }), ("All", new[] { 1 }), ("Count", new[] { 0, 1 }), ("Sum", new[] { 0, 1 }),
            ("Min", new[] { 0, 1 }), ("Max", new[] { 0, 1 }), ("Average", new[] { 0, 1 }), ("Aggregate", new[] { 2 }),
            ("Contains", new[] { 1 }), ("ForEach", new[] { 1 }), ("Join", new[] { 1 }),
        };

        public static readonly string Source = CoreSource + ListExtension();

        private static string ListExtension()
        {
            var sb = new StringBuilder("\nclass extends List {\n");
            foreach (var (name, arities) in ListOperators)
            {
                foreach (int arity in arities)
                {
                    var parameters = string.Join(", ", System.Linq.Enumerable.Range(0, arity).Select(i => "a" + i));
                    sb.Append($"    {name}({parameters}) {{ return Linq.From(this).{name}({parameters}) }}\n");
                }
            }
            sb.Append("    ToList() { return new List(this.ToArray()) }\n");
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
                construct(class inner, class pred) { this.inner = inner; this.pred = pred }
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
                construct(class inner, class fn) { this.inner = inner; this.fn = fn }
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
                construct(class inner, class fn) { this.inner = inner; this.fn = fn }
                MoveNext() {
                    var f = this.fn
                    while (true) {
                        if (this.sub != undefined) {
                            if (this.sub.MoveNext()) { this.current = this.sub.GetCurrent(); return true }
                            this.sub = undefined
                        }
                        if (!this.inner.MoveNext()) { return false }
                        this.sub = Linq.Iter(f(this.inner.GetCurrent()))
                    }
                }
                GetCurrent() { return this.current }
            }

            class LinqTakeEnumerator : IEnumerator {
                class inner
                int left
                construct(class inner, int n) { this.inner = inner; this.left = n }
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
                construct(class inner, int n) { this.inner = inner; this.skip = n }
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
                construct(class inner, class pred) { this.inner = inner; this.pred = pred; this.done = false }
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
                construct(class inner, class pred) { this.inner = inner; this.pred = pred; this.started = false }
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
                construct(class first, class second) { this.first = first; this.second = second; this.inSecond = false }
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
                construct(class a, class b, class fn) { this.a = a; this.b = b; this.fn = fn }
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
                // Ein Enumerator für Arrays UND Objekte mit GetEnumerator()
                static Iter(class source) {
                    if (source is of class) { return source.GetEnumerator() }
                    return new ListEnumerator(source, source.length)
                }

                static From(class source) {
                    var s = source
                    return new Query(() => Linq.Iter(s))
                }

                static Range(int start, int count) {
                    return new Query(() => new LinqRangeEnumerator(start, count))
                }

                static Repeat(class value, int count) {
                    var items = new class[count]
                    for (var i = 0; i < count; i = i + 1) { items[i] = value }
                    return Linq.From(items)
                }

                // Stabiles Sortieren (Mergesort): liefert die `items` in der Reihenfolge der `keys` (gleich lange Arrays)
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

                // `factory` ist eine Lambda ohne Parameter, die bei jedem Durchlauf einen frischen Enumerator liefert
                construct(class factory) { this.factory = factory }

                GetEnumerator() {
                    var f = this.factory
                    return f()
                }

                // ---- träge Operatoren
                Where(lambda<int> pred) {
                    var f = this.factory
                    return new Query(() => new LinqWhereEnumerator(f(), pred))
                }
                Select(lambda<int> fn) {
                    var f = this.factory
                    return new Query(() => new LinqSelectEnumerator(f(), fn))
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

                // ---- eifrige Operatoren (arbeiten auf einer Kopie, liefern wieder eine Query)
                OrderBy(lambda<int> key) { return this.Ordered(key, false) }
                OrderByDescending(lambda<int> key) { return this.Ordered(key, true) }
                Ordered(class key, bool desc) {
                    var items = this.ToArray()
                    var keys = new class[items.length]
                    for (var i = 0; i < items.length; i = i + 1) { keys[i] = key(items[i]) }
                    return Linq.From(Linq.Sort(items, keys, desc))
                }
                Reverse() {
                    var items = this.ToArray()
                    var n = items.length
                    var result = new class[n]
                    for (var i = 0; i < n; i = i + 1) { result[i] = items[n - 1 - i] }
                    return Linq.From(result)
                }
                Distinct() {
                    var seen = new List()
                    foreach (x in this) {
                        var known = false
                        foreach (y in seen) { if (x == y) { known = true } }
                        if (!known) { seen.Add(x) }
                    }
                    return Linq.From(seen)
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
                    throw new LinqEmptyException("Die Folge enthaelt kein Element")
                }
                First(lambda<int> pred) {
                    foreach (x in this) { if (pred(x)) { return x } }
                    throw new LinqEmptyException("Die Folge enthaelt kein passendes Element")
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
                    if (!found) { throw new LinqEmptyException("Die Folge enthaelt kein Element") }
                    return last
                }
                ElementAt(int index) {
                    var i = 0
                    foreach (x in this) {
                        if (i == index) { return x }
                        i = i + 1
                    }
                    throw new LinqEmptyException("Index " + index + " liegt ausserhalb der Folge")
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
                    if (!found) { throw new LinqEmptyException("Die Folge enthaelt kein Element") }
                    return best
                }
                Average() { return this.Average(x => x) }
                Average(lambda<int> fn) {
                    var s = 0.0
                    var n = 0
                    foreach (x in this) { s = s + fn(x); n = n + 1 }
                    if (n == 0) { throw new LinqEmptyException("Die Folge enthaelt kein Element") }
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
