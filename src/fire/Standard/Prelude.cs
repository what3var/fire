namespace fire.Standard
{
    /// <summary>
    /// A piece of fire source that is placed before every program and
    /// defines the "built-in" types IndexOutOfBoundsException/AccessDeniedException/
    /// IEnumerable/IEnumerator/List - deliberately written in fire itself
    /// instead of as a native C# implementation, since the language
    /// now has enough substance for that (classes, arrays, interfaces) and this
    /// stays consistent with everything else. `IndexOutOfBoundsException`/
    /// `AccessDeniedException` are constructed and thrown by the VM itself on an invalid
    /// array index or a violated access-modifier rule
    /// (see VM.ThrowIndexOutOfBounds/
    /// ThrowAccessDenied) - scripts catch them quite normally via `try`/`catch`,
    /// like any other exception.
    ///
    /// `List` internally uses a fixed-size array that is doubled when needed
    /// (classic dynamic array). `GetEnumerator` creates an
    /// own `ListEnumerator` that knows only MoveNext()/GetCurrent() -
    /// `foreach` calls these two (and GetEnumerator itself) purely via
    /// name dispatch, so it also works on any OTHER class
    /// that has the same three methods, not only on 'List' itself.
    /// </summary>
    public static class Prelude
    {
        /// <summary>The prelude: the core classes below in fire, plus the extensions of the base types
        /// `string` and `char` (`class extends string { ... }`, SPEC 5.5.1/8.12), which are generated from the
        /// method tables in <see cref="StringMethods"/>/<see cref="CharMethods"/>,
        /// so that the method IDs in the fire text do not have to be maintained by hand.</summary>
        public static readonly string Source = CoreSource + StringMethods.PreludeSource + CharMethods.PreludeSource + ResourceMethods.PreludeSource + MathMethods.PreludeSource;

        private const string CoreSource = """
            /// The base class of all exceptions (SPEC 7.1): it carries a `message`. Own exception classes derive from it (`class NotFound : Exception { }`);
            /// `catch (Exception e)` catches every thrown value. A program that declares a class `Exception` itself replaces this one.
            /// `from..to` (RangeExpr): the whole numbers from `from` to `to`, both included, counting down when `to` is smaller; in the unit of `from`.
            class __Range {
                static int[] Of(int first : any, int last : any) {
                    int n = 0
                    var v = first
                    if (first <= last) { while (v <= last) { n++; v++ } } else { while (v >= last) { n++; v-- } }
                    any r[] = new any[n]
                    v = first
                    for (int i = 0; i < n; i++) {
                        r[i] = v
                        if (first <= last) { v++ } else { v-- }
                    }
                    return r
                }
            }

            class Exception {
                string message

                construct(string message = "") {
                    this.message = message
                }
            }

            // A failed check of a test (`#import "testing"`, `forge test`): a failure instead of an error.
            class Assertion : Exception {
                construct(string message = "assertion failed") {
                    this.message = message
                }
            }

            class IndexOutOfBoundsException : Exception {
                int index
                int length

                construct(string message, int index, int length) {
                    this.message = message
                    this.index = index
                    this.length = length
                }
            }

            // what the ownership methods take along (SPEC 2.2): `x.TakeUpwards(Takes.Locals)`
            enum Takes { This = 0, Children = 1, Locals = 2, All = 3 }

            /// What a Dictionary, HashSet or List owns of what is put into it (`new Dictionary<string, Node>(Owns.Values)`). Explicit (the default): only
            /// what is handed over (`dict.Add(key, take node)`, or created in the call: `list.Add(new Node())`). Keys, Values, All: every key or value that
            /// comes in - and it is destroyed when it falls out (Remove, overwriting, Clear), unless it is taken out first (Take, TakeAt).
            flag enum Owns { Explicit = 0, Keys = 1, Values = 2, All = 3 }

            /// A key of a Dictionary or HashSet with an equality of its own (otherwise objects are equal only to themselves); equal objects must give the
            /// same GetHashCode().
            interface IEquatable {
                bool Equals(any other)
                int GetHashCode()
            }

            class DestroyedException : Exception {

                construct(string message) {
                    this.message = message
                }
            }

            class AccessDeniedException : Exception {

                construct(string message) {
                    this.message = message
                }
            }

            // An integer division or modulo by zero (floats give infinity or NaN instead).
            class DivideByZeroException : Exception {
                construct(string message) { this.message = message }
            }

            // A value that does not fit what its declaration demands: a lambda with another number of parameters than its `lambda<...>` type
            class ArgumentException : Exception {
                construct(string message) { this.message = message }
            }

            // A field, property or method that the value does not have (`obj.Nope()`, `p.Mesage`)
            class MissingMemberException : Exception {
                construct(string message) { this.message = message }
            }

            // A whole number that does not fit any more (converted into the finer unit of a declaration: `int a : nm = 9000000000km`)
            class OverflowException : Exception {
                construct(string message) { this.message = message }
            }

            class UnitMismatchException : Exception {
                string expectedUnit
                string actualUnit

                construct(string message, string expectedUnit, string actualUnit) {
                    this.message = message
                    this.expectedUnit = expectedUnit
                    this.actualUnit = actualUnit
                }
            }

            // A command as an object: `Command` without, `Command<T>` with a context (e.g. `Command<IDevice>` - a command that `Device.DoCommand` executes with the
            // device as context). The lambda `Command` is the body; whoever needs more derives and overrides Execute (`class Home : Command<IDevice>`).
            //   var home = new Command<IDevice>()
            //   home.Command = d => { d.WriteString("G28\n") }
            //   Device.Default.DoCommand(home)
            /// <summary>A command as an object that is executed without a context.</summary>
            interface ICommand {
                any Execute()
            }

            /// <summary>A command as an object that is executed with a context - e.g. `ICommand<IDevice>` is run by `Device.DoCommand` with the device as its context.</summary>
            interface ICommand<T> {
                any Execute(T context)
            }

            /// <summary>A command object without a context. The lambda in `Command` is its body; derive from the class and override `Execute` if you need more.</summary>
            class Command : ICommand {
                lambda Command

                construct() { }

                construct(lambda command) {
                    this.Command = command
                }

                /// <summary>Calls the lambda `Command`; without a lambda nothing happens.</summary>
                /// <returns>The result of the lambda, or `undefined`.</returns>
                any Execute() {
                    var body = this.Command
                    if (body == undefined) {
                        return undefined
                    }
                    return body()
                }
            }

            /// <summary>A command object with a context of type T (e.g. `Command<IDevice>`). The lambda in `Command` receives the context as its argument.</summary>
            class Command<T> : ICommand<T> {
                lambda<T> Command

                construct() { }

                construct(lambda<T> command) {
                    this.Command = command
                }

                /// <summary>Calls the lambda `Command` with the context; without a lambda nothing happens.</summary>
                /// <param name="context">What the command works on, e.g. the device.</param>
                /// <returns>The result of the lambda, or `undefined`.</returns>
                any Execute(T context) {
                    var body = this.Command
                    if (body == undefined) {
                        return undefined
                    }
                    return body(context)
                }
            }

            // `IEnumerable<T>` / `IEnumerator<T>`: `foreach` over a class that implements `IEnumerable<int>` takes `int`s; written without
            // a type argument (`class Bag : IEnumerable`) the elements are `any`
            interface IEnumerator<T> {
                bool MoveNext()
                T GetCurrent()
            }

            interface IEnumerable<T> {
                IEnumerator<T> GetEnumerator()
            }

            class ListEnumerator : IEnumerator {
                any items
                int count
                int index

                construct(any items, int count) {
                    this.items = items
                    this.count = count
                    this.index = -1
                }

                bool MoveNext() {
                    this.index = this.index + 1
                    return this.index < this.count
                }

                any GetCurrent() {
                    return this.items[this.index]
                }
            }

            // The enumerator of a List: reads through the list, so it stays valid when the list grows (its backing array is replaced).
            class ListIter<T> : IEnumerator<T> {
                any list
                int count
                int index

                construct(any list, int count) {
                    this.list = list
                    this.count = count
                    this.index = -1
                }

                bool MoveNext() {
                    this.index = this.index + 1
                    return this.index < this.count
                }

                T GetCurrent() {
                    return this.list[this.index]
                }
            }

            // `List<int>`: a list whose elements are `int`s (for the type checker - at runtime every list holds any value); `List` alone is a list of `any`
            class List<T> : IEnumerable<T> {
                private any items
                private int count
                private Owns owns = Owns.Explicit

                /// The number of elements.
                int Count { get { return this.count } }

                construct() {
                    this.items = new class[8]
                    this.count = 0
                }

                // 'new List([1, 2, 3, 4])' - list-literal-like
                // initialisation via a constructor overload with an
                // array parameter (distinguished from construct() purely by
                // the parameter count, like any other overload in this
                // language) - copies each element individually via Add() (instead of
                // taking 'initial' directly as items), so that later
                // Add() calls can keep growing quite normally, without
                // being bound to the exact size of the array originally
                // passed in.
                //
                // `new List<Node>(Owns.Values)`: a list that owns every element that comes in and destroys it when it falls out (RemoveAt, Remove,
                // overwriting, Clear) - unless it is taken out with TakeAt (see Owns).
                construct(any initial) {
                    this.items = new class[8]
                    this.count = 0
                    if (initial is of Owns) {
                        this.owns = initial
                        return
                    }
                    for (var i = 0; i < initial.length; i = i + 1) {
                        this.Add(initial[i])
                    }
                }

                undefined Add(T value) {
                    if (this.count >= this.items.length) {
                        this.Grow()
                    }
                    if (value is of class) {
                        if (this.owns.HasFlag(Owns.Values)) { value.TakeTo(this) } else { try value.TakeTo(this) }
                    }
                    this.items[this.count] = value
                    this.count = this.count + 1
                }

                // an element that falls out: destroyed when the list owns its elements (Owns.Values) and this one
                undefined DropElement(any value) {
                    if (value is of class && this.owns.HasFlag(Owns.Values) && value is from this) { delete value }
                }

                T operator[](int index) {
                    return this.items[index]
                }

                undefined operator[](int index, T value) {
                    var old = this.items[index]
                    if (value is of class) {
                        if (this.owns.HasFlag(Owns.Values)) { value.TakeTo(this) } else { try value.TakeTo(this) }
                    }
                    this.items[index] = value
                    if (old != value) { this.DropElement(old) }
                }

                // removes the element at `index` (the ones behind it move up); the element itself is destroyed only if the list owns it (Owns.Values)
                undefined RemoveAt(int index) {
                    var old = this.items[index]
                    var i = index
                    while (i < this.count - 1) {
                        this.items[i] = this.items[i + 1]
                        i = i + 1
                    }
                    this.count = this.count - 1
                    this.items[this.count] = undefined
                    this.DropElement(old)
                }

                // removes the element at `index` and gives it to the caller (who owns it then, if the list owned it)
                T TakeAt(int index) {
                    var value = this.items[index]
                    any taken = undefined   // (declared here: the call owns what `take` gives it, not the block of the `if`)
                    if (value is of class && value is from this) { taken = take value }
                    this.RemoveAt(index)
                    if (taken != undefined) { return taken }
                    return value
                }

                // puts `value` in at `index`; the elements from there on move down
                undefined Insert(int index, T value) {
                    if (this.count >= this.items.length) {
                        this.Grow()
                    }
                    var i = this.count
                    while (i > index) {
                        this.items[i] = this.items[i - 1]
                        i = i - 1
                    }
                    if (value is of class) {
                        if (this.owns.HasFlag(Owns.Values)) { value.TakeTo(this) } else { try value.TakeTo(this) }
                    }
                    this.items[index] = value
                    this.count = this.count + 1
                }

                // the index of `value` (the same object), -1 if it is not in the list
                int IndexOf(T value) {
                    var i = 0
                    while (i < this.count) {
                        if (this.items[i] == value) {
                            return i
                        }
                        i = i + 1
                    }
                    return -1
                }

                // removes `value` (the first one); false if it is not in the list
                bool Remove(T value) {
                    var at = this.IndexOf(value)
                    if (at < 0) {
                        return false
                    }
                    this.RemoveAt(at)
                    return true
                }

                // empties the list (the elements are destroyed only if the list owns them, Owns.Values)
                undefined Clear() {
                    var i = 0
                    while (i < this.count) {
                        var old = this.items[i]
                        this.items[i] = undefined
                        this.DropElement(old)
                        i = i + 1
                    }
                    this.count = 0
                }

                undefined Grow() {
                    var newItems = new class[this.items.length * 2]
                    var i = 0
                    while (i < this.count) {
                        newItems[i] = this.items[i]
                        i = i + 1
                    }
                    // the new array belongs to the list (a local array dies with this method), the old one is released
                    var old = this.items
                    this.items = newItems
                    newItems.TakeTo(this)
                    delete old
                }

                ListIter<T> GetEnumerator() {
                    return new ListIter<T>(this, this.count)
                }
            }

            // The table of Dictionary and HashSet: the entries in the order they came in (`keys`, `vals`, their hashes; `live` false for a removed one) and
            // per bucket the first entry of its chain (`buckets`, `next`: index + 1, 0 for none). The keys are compared with `==` (strings by text, numbers by
            // value - `1km` is `1000m`), an IEquatable key with Equals/GetHashCode.
            class __HashTable {
                protected any keys
                protected any vals
                private any hashes
                private any next
                protected any live
                private any buckets
                private int count
                protected int used
                protected Owns owns

                /// The number of entries.
                int Count { get { return this.count } }

                construct(Owns owns) {
                    this.owns = owns
                    this.keys = new class[8]
                    this.vals = new class[8]
                    this.hashes = new int[8]
                    this.next = new int[8]
                    this.live = new bool[8]
                    this.buckets = new int[8]
                    for (var b = 0; b < 8; b = b + 1) { this.buckets[b] = 0 }   // (a new array holds undefined)
                    this.count = 0
                    this.used = 0
                }

                int HashOf(any key) {
                    if (key is of IEquatable) { return key.GetHashCode() }
                    return __MathCall(15, key)
                }

                bool SameKey(any a, any b) {
                    if (a is of IEquatable) { return a.Equals(b) }
                    return a == b
                }

                // the entry of `key` (hash `h`), -1 if there is none
                int Find(any key, int h) {
                    var i = this.buckets[h & (this.buckets.length - 1)] - 1
                    while (i >= 0) {
                        if (this.hashes[i] == h && this.SameKey(this.keys[i], key)) { return i }
                        i = this.next[i] - 1
                    }
                    return -1
                }

                // a new entry at the end (ownership is the caller's business: it must happen in the method that was given the values)
                undefined Insert(any key, any value, int h) {
                    if (this.used >= this.keys.length) { this.Resize() }
                    var i = this.used
                    this.used = i + 1
                    this.keys[i] = key
                    this.vals[i] = value
                    this.hashes[i] = h
                    this.live[i] = true
                    var b = h & (this.buckets.length - 1)
                    this.next[i] = this.buckets[b]
                    this.buckets[b] = i + 1
                    this.count = this.count + 1
                }

                // takes the entry out of its chain; key and value stay with the caller (Drop or TakeLocal)
                undefined Unlink(int at) {
                    var b = this.hashes[at] & (this.buckets.length - 1)
                    var i = this.buckets[b] - 1
                    var prev = -1
                    while (i >= 0 && i != at) {
                        prev = i
                        i = this.next[i] - 1
                    }
                    if (prev < 0) { this.buckets[b] = this.next[at] } else { this.next[prev] = this.next[at] }
                    this.live[at] = false
                    this.keys[at] = undefined
                    this.vals[at] = undefined
                    this.count = this.count - 1
                }

                // a key or value that falls out: destroyed when the table owns that part (`what`) and this object
                undefined Drop(any x, Owns what) {
                    if (x is of class && this.owns.HasFlag(what) && x is from this) { delete x }
                }

                // room for more entries: the live ones move to new arrays (in their order, without the removed ones)
                undefined Resize() {
                    var cap = 8
                    while (cap < this.count * 2) { cap = cap * 2 }
                    var nk = new class[cap]
                    var nv = new class[cap]
                    var nh = new int[cap]
                    var nn = new int[cap]
                    var nl = new bool[cap]
                    var nb = new int[cap]
                    for (var b0 = 0; b0 < cap; b0 = b0 + 1) { nb[b0] = 0 }
                    var j = 0
                    for (var i = 0; i < this.used; i = i + 1) {
                        if (this.live[i]) {
                            nk[j] = this.keys[i]
                            nv[j] = this.vals[i]
                            nh[j] = this.hashes[i]
                            nl[j] = true
                            var b = nh[j] & (cap - 1)
                            nn[j] = nb[b]
                            nb[b] = j + 1
                            j = j + 1
                        }
                    }
                    var oldKeys = this.keys
                    var oldVals = this.vals
                    var oldHashes = this.hashes
                    var oldNext = this.next
                    var oldLive = this.live
                    var oldBuckets = this.buckets
                    this.keys = nk
                    this.vals = nv
                    this.hashes = nh
                    this.next = nn
                    this.live = nl
                    this.buckets = nb
                    nk.TakeTo(this)
                    nv.TakeTo(this)
                    nh.TakeTo(this)
                    nn.TakeTo(this)
                    nl.TakeTo(this)
                    nb.TakeTo(this)
                    this.used = j
                    delete oldKeys
                    delete oldVals
                    delete oldHashes
                    delete oldNext
                    delete oldLive
                    delete oldBuckets
                }

                // empties the table; what it owns (`keyPart`, `valuePart`) is destroyed
                undefined ClearEntries(Owns keyPart, Owns valuePart) {
                    for (var i = 0; i < this.used; i = i + 1) {
                        if (this.live[i]) {
                            var k = this.keys[i]
                            var v = this.vals[i]
                            this.keys[i] = undefined
                            this.vals[i] = undefined
                            this.live[i] = false
                            this.Drop(k, keyPart)
                            this.Drop(v, valuePart)
                        }
                    }
                    for (var b = 0; b < this.buckets.length; b = b + 1) { this.buckets[b] = 0 }
                    this.used = 0
                    this.count = 0
                }

                // for the enumerators: the next live entry after `index` (-1 if there is none), its key and its value
                int __NextEntry(int index) {
                    var i = index + 1
                    while (i < this.used && !this.live[i]) { i = i + 1 }
                    if (i < this.used) { return i }
                    return -1
                }
                any __KeyAt(int index) { return this.keys[index] }
                any __ValueAt(int index) { return this.vals[index] }
            }

            /// An entry of a Dictionary: `foreach (pair in dict) { print(pair.Key + " " + pair.Value) }`.
            class KeyValuePair<K, V> {
                K Key
                V Value
                construct(K key, V value) {
                    this.Key = key
                    this.Value = value
                }
            }

            class DictionaryIter<K, V> : IEnumerator<KeyValuePair<K, V>> {
                any table
                int index
                construct(any table) {
                    this.table = table
                    this.index = -1
                }
                bool MoveNext() {
                    var at = this.table.__NextEntry(this.index)
                    if (at < 0) { return false }
                    this.index = at
                    return true
                }
                KeyValuePair<K, V> GetCurrent() {
                    return new KeyValuePair<K, V>(this.table.__KeyAt(this.index), this.table.__ValueAt(this.index))
                }
            }

            class HashSetIter<T> : IEnumerator<T> {
                any table
                int index
                construct(any table) {
                    this.table = table
                    this.index = -1
                }
                bool MoveNext() {
                    var at = this.table.__NextEntry(this.index)
                    if (at < 0) { return false }
                    this.index = at
                    return true
                }
                T GetCurrent() {
                    return this.table.__KeyAt(this.index)
                }
            }

            /// Values by key (`Dictionary<string, int>`): `dict[key] = value`, `dict[key]`, Add, Get, TryGet, ContainsKey, Remove, Take, Keys, Values,
            /// `Count`; `foreach` gives KeyValuePairs in the order the keys came in. `new Dictionary<string, Node>(Owns.Values)` owns its values (see Owns).
            class Dictionary<K, V> : __HashTable, IEnumerable<KeyValuePair<K, V>> {
                construct(Owns owns = Owns.Explicit) : base(owns) { }

                /// Adds a key that is not there yet (an ArgumentException if it is).
                undefined Add(K key, V value) {
                    var h = this.HashOf(key)
                    if (this.Find(key, h) >= 0) { throw new ArgumentException("The key '" + key + "' is already in the dictionary.") }
                    if (key is of class) {
                        if (this.owns.HasFlag(Owns.Keys)) { key.TakeTo(this) } else { try key.TakeTo(this) }
                    }
                    if (value is of class) {
                        if (this.owns.HasFlag(Owns.Values)) { value.TakeTo(this) } else { try value.TakeTo(this) }
                    }
                    this.Insert(key, value, h)
                }

                /// The value of `key` (an ArgumentException if it is not there).
                V operator[](K key) {
                    var at = this.Find(key, this.HashOf(key))
                    if (at < 0) { throw new ArgumentException("The key '" + key + "' is not in the dictionary.") }
                    return this.vals[at]
                }

                /// Sets the value of `key` - adds it or replaces the value (a replaced value that the dictionary owns is destroyed).
                undefined operator[](K key, V value) {
                    var h = this.HashOf(key)
                    var at = this.Find(key, h)
                    if (value is of class) {
                        if (this.owns.HasFlag(Owns.Values)) { value.TakeTo(this) } else { try value.TakeTo(this) }
                    }
                    if (at >= 0) {
                        var old = this.vals[at]
                        this.vals[at] = value
                        if (old != value) { this.Drop(old, Owns.Values) }
                        return
                    }
                    if (key is of class) {
                        if (this.owns.HasFlag(Owns.Keys)) { key.TakeTo(this) } else { try key.TakeTo(this) }
                    }
                    this.Insert(key, value, h)
                }

                /// The value of `key`, or `fallback` if it is not there.
                V Get(K key, V fallback = undefined) {
                    var at = this.Find(key, this.HashOf(key))
                    if (at < 0) { return fallback }
                    return this.vals[at]
                }

                /// Whether `key` is there; if it is, `value` gets its value.
                bool TryGet(K key, ref V value) {
                    var at = this.Find(key, this.HashOf(key))
                    if (at < 0) { return false }
                    value = this.vals[at]
                    return true
                }

                bool ContainsKey(K key) { return this.Find(key, this.HashOf(key)) >= 0 }

                bool ContainsValue(V value) {
                    for (var i = 0; i < this.used; i = i + 1) {
                        if (this.live[i] && this.SameKey(this.vals[i], value)) { return true }
                    }
                    return false
                }

                /// Removes `key` and its value (what the dictionary owns of them is destroyed); false if it is not there.
                bool Remove(K key) {
                    var at = this.Find(key, this.HashOf(key))
                    if (at < 0) { return false }
                    var k = this.keys[at]
                    var v = this.vals[at]
                    this.Unlink(at)
                    this.Drop(k, Owns.Keys)
                    this.Drop(v, Owns.Values)
                    return true
                }

                /// Removes `key` and gives its value to the caller (who owns it then, if the dictionary owned it).
                V Take(K key) {
                    var at = this.Find(key, this.HashOf(key))
                    if (at < 0) { throw new ArgumentException("The key '" + key + "' is not in the dictionary.") }
                    var k = this.keys[at]
                    var v = this.vals[at]
                    this.Unlink(at)
                    this.Drop(k, Owns.Keys)
                    any taken = undefined   // (declared here: the call owns what `take` gives it, not the block of the `if`)
                    if (v is of class && v is from this) {
                        taken = take v
                        return taken
                    }
                    return v
                }

                /// Empties the dictionary (what it owns is destroyed).
                undefined Clear() { this.ClearEntries(Owns.Keys, Owns.Values) }

                /// The keys in the order they came in (a new list).
                List<K> Keys {
                    get {
                        var list = new List<K>()
                        for (var i = 0; i < this.used; i = i + 1) { if (this.live[i]) { list.Add(this.keys[i]) } }
                        return list
                    }
                }

                /// The values in the order their keys came in (a new list).
                List<V> Values {
                    get {
                        var list = new List<V>()
                        for (var i = 0; i < this.used; i = i + 1) { if (this.live[i]) { list.Add(this.vals[i]) } }
                        return list
                    }
                }

                DictionaryIter<K, V> GetEnumerator() {
                    return new DictionaryIter<K, V>(this)
                }
            }

            /// A set of values, each once (`HashSet<string>`): Add, Contains, Remove, Take, Clear, `Count`; `foreach` in the order they came in.
            /// `new HashSet<Node>(Owns.Values)` owns its elements (see Owns).
            class HashSet<T> : __HashTable, IEnumerable<T> {
                construct(Owns owns = Owns.Explicit) : base(owns) { }

                /// Adds `item`; false if an equal one is already there.
                bool Add(T item) {
                    var h = this.HashOf(item)
                    if (this.Find(item, h) >= 0) { return false }
                    if (item is of class) {
                        if (this.owns.HasFlag(Owns.Values)) { item.TakeTo(this) } else { try item.TakeTo(this) }
                    }
                    this.Insert(item, undefined, h)
                    return true
                }

                bool Contains(T item) { return this.Find(item, this.HashOf(item)) >= 0 }

                /// Removes `item` (destroyed if the set owns it); false if it is not there.
                bool Remove(T item) {
                    var at = this.Find(item, this.HashOf(item))
                    if (at < 0) { return false }
                    var k = this.keys[at]
                    this.Unlink(at)
                    this.Drop(k, Owns.Values)
                    return true
                }

                /// Removes `item` and gives the element of the set to the caller (who owns it then, if the set owned it).
                T Take(T item) {
                    var at = this.Find(item, this.HashOf(item))
                    if (at < 0) { throw new ArgumentException("The item '" + item + "' is not in the set.") }
                    var k = this.keys[at]
                    this.Unlink(at)
                    any taken = undefined   // (declared here: the call owns what `take` gives it, not the block of the `if`)
                    if (k is of class && k is from this) {
                        taken = take k
                        return taken
                    }
                    return k
                }

                /// Empties the set (what it owns is destroyed).
                undefined Clear() { this.ClearEntries(Owns.Values, Owns.Values) }

                HashSetIter<T> GetEnumerator() {
                    return new HashSetIter<T>(this)
                }
            }
            """;
    }
}
