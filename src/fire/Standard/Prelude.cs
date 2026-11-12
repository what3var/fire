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
        public static readonly string Source = CoreSource + StringMethods.PreludeSource + CharMethods.PreludeSource + ResourceMethods.PreludeSource + MathMethods.PreludeSource + EnvironmentMethods.PreludeSource + LibraryMethods.PreludeSource;

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

            /// `await [ f(), g() ]` (docs/ROADMAP_TASKS.md): every call is a lambda (res, fails, i) made by the parser; they run side by side (natively), then
            /// the first exception in the order of the brackets is thrown, or the results are given back.
            class __Await {
                static any[] All(any items[]) {
                    any res[] = new any[items.Length]
                    any fails[] = new any[items.Length]
                    __await_run(items, res, fails)
                    for (int i = 0; i < items.Length; i++) { if (fails[i] != undefined) { throw fails[i] } }
                    return res
                }
            }

            class Exception {
                string message

                construct(string message = "") {
                    this.message = message
                }
            }

            // Cancellation (docs/ROADMAP_TASKS.md section 6): what a wait throws when the token of the task is cancelled - or its time limit (`until 5s`) ran out.
            class CancelledException : Exception {
                construct(string message = "The task was cancelled.") {
                    this.message = message
                }
            }
            class TimeoutException : CancelledException {
                construct(string message = "The time limit ran out.") {
                    this.message = message
                }
            }

            /// The token of a task, or of a source: `IsCancelled`, `ThrowIfCancelled()`; `CancellationToken.Current()` is the running task's. A token goes to
            /// a task with `until` (`fire until source.Token { ... }`, `await Load(url) until token, 5s`) - it is never copied.
            [NoCopy]
            class CancellationToken {
                int node = 0
                construct(int node) { this.node = node }
                destruct() { __cancel_release(this.node) }
                bool IsCancelled { get { return __cancel_state(this.node) != 0 } }
                undefined ThrowIfCancelled() {
                    int state = __cancel_state(this.node)
                    if (state == 2) { throw new TimeoutException() }
                    if (state == 1) { throw new CancelledException() }
                }
                static CancellationToken Current() { return new CancellationToken(__cancel_current()) }
            }

            /// What cancels: `Cancel()` (at once: the waits of the tasks it reaches wake), `CancelAfter(5s)`. Made inside a task it is a child of the task's
            /// token (cancelling the task cancels it); `CancellationSource.Detached()` follows nobody.
            [NoCopy]
            class CancellationSource {
                int node = 0
                construct(bool linked = true) { this.node = __cancel_new(linked) }
                destruct() { __cancel_release(this.node) }
                static CancellationSource Detached() { return new CancellationSource(false) }
                CancellationToken Token { get { return new CancellationToken(__cancel_ref(this.node)) } }
                bool IsCancelled { get { return __cancel_state(this.node) != 0 } }
                undefined Cancel() { __cancel_cancel(this.node) }
                undefined CancelAfter(any time) { __cancel_after(this.node, time) }
            }

            /// A value that another task delivers (docs/ROADMAP_TASKS.md section 6): `Set(value)` - the first `Set` or `Fail` counts, the Completion keeps a
            /// copy -, `Fail(exception)`, `IsDone`; `Wait(limit)` waits until it is there and returns a copy of the value (or throws a copy of the exception).
            /// A wait follows the token of the task (`until`, cancelling) and ends with a TimeoutException after `limit`. A Completion is never copied:
            /// `Share()` gives another one on the same value, for another task (`fire handover s { s.Set(1) }`, the argument of an actor's method).
            [NoCopy]
            class Completion {
                int node = 0
                construct() { this.node = __completion_new() }
                destruct() { __completion_release(this.node) }
                Completion Share() {
                    var c = new Completion()
                    __completion_release(c.node)
                    c.node = __completion_ref(this.node)
                    return c
                }
                bool IsDone { get { return __completion_done(this.node) } }
                bool Set(any value) { return __completion_set(this.node, value, false) }
                bool Fail(Exception e) { return __completion_set(this.node, e, true) }
                any Wait(any limit = undefined) { return __completion_wait(this.node, limit) }
            }

            // A queue between threads (docs/ROADMAP_TASKS.md step 9): `Send` waits while it is full, `Receive` while it is empty (both serve meanwhile);
            // a closed and empty channel gives undefined (`foreach` ends). A thread gets it with `fire with ch` - an end of its own on the same queue.
            // A value goes as it is; an object, an array or a record only fresh (`new`, the result of a call), copied (`copy x`) or taken (`take x`): it
            // belongs to the channel while it travels, to the receiver after Receive. The channel closes when the one that made it ends. A channel is never
            // copied (`fire taking ch` is the ArgumentException): it goes to a thread with `fire with`.
            /// One end of a channel that sends A and receives B - what every channel offers: a two-way end of Channel.Pair<A, B> (ChannelEnd<A, B>), and
            /// (as IChannel<T>, both directions the same type) Channel<T> between threads, Net.Channel<T> (`#import "net.channel"`) between programs.
            interface IChannelEnd<A, B> : IEnumerable<B> {
                bool Send(A value)
                B Receive(any limit = undefined)
                B TryReceive()
                undefined Close()
                bool IsClosed { get; }
                int Count { get; }
            }

            /// A channel that sends and receives T: Channel<T>, Net.Channel<T> - code that takes an IChannel<T> works with both (and with an end of
            /// Channel.Pair<T, T>, which is an IChannelEnd<T, T>).
            interface IChannel<T> : IChannelEnd<T, T> {
            }

            [NoCopy]
            class Channel<T> : IChannel<T> {
                int node = 0
                bool original = true
                construct(int capacity = 16) { this.node = __chan_new(capacity) }
                destruct() {
                    if (this.original) { __chan_close(this.node) }
                    __chan_release(this.node)
                }
                // (an end of its own on the same queue: what `fire with` gives a thread)
                Channel<T> __End() {
                    var c = new Channel<T>(1)
                    __chan_release(c.node)
                    c.node = __chan_ref(this.node)
                    c.original = false
                    return c
                }
                bool Send(T value) { return __chan_send(this.node, value, value is from local) }
                T Receive(any limit = undefined) { return __chan_receive(this.node, limit, true) }
                T TryReceive() { return __chan_receive(this.node, undefined, false) }
                undefined Close() { __chan_close(this.node) }
                bool IsClosed { get { return __chan_closed(this.node) } }
                int Count { get { return __chan_count(this.node) } }
                IEnumerator<T> GetEnumerator() { return new ChannelIter<T>(this) }
                // Two directions: the end that sends A and receives B (`.Other()` gives the end that sends B and receives A).
                static ChannelEnd<A, B> Pair<A, B>(int capacity = 16) { return new ChannelEnd<A, B>(new Channel<A>(capacity), new Channel<B>(capacity)) }
            }

            // One end of a two-way channel (Channel.Pair): Send goes to the other end, Receive takes what the other end sent. Like a channel it goes to a
            // thread with `fire with` (an end of its own) and is never copied; Close ends this end's direction.
            [NoCopy]
            class ChannelEnd<A, B> : IChannelEnd<A, B> {
                Channel<A> outgoing
                Channel<B> incoming
                construct(Channel<A> o, Channel<B> i) { this.outgoing = take o; this.incoming = take i }
                bool Send(A value) { return this.outgoing.Send(try take value) }
                B Receive(any limit = undefined) { return this.incoming.Receive(limit) }
                B TryReceive() { return this.incoming.TryReceive() }
                undefined Close() { this.outgoing.Close() }
                bool IsClosed { get { return this.incoming.IsClosed } }
                int Count { get { return this.incoming.Count } }
                ChannelEnd<B, A> Other() { return new ChannelEnd<B, A>(this.incoming.__End(), this.outgoing.__End()) }
                ChannelEnd<A, B> __End() { return new ChannelEnd<A, B>(this.outgoing.__End(), this.incoming.__End()) }
                IEnumerator<B> GetEnumerator() { return this.incoming.GetEnumerator() }
            }

            // ---- Expression trees (docs/ROADMAP_EXPRESSIONS.md): a lambda written at a call of a parameter `lambda expression<T>` arrives as a tree of
            // these nodes - what the body reads, compares and calls - next to its compiled code (Invoke). The nodes do not change; ToString is the
            // body in fire syntax; a visitor walks them.

            /// A node of an expression tree: its kind ("Binary", "Member", ...) and the type the type checker found ("int", "Person", "any").
            /// Equals is structural - the same shape: kinds, types, operators, names, constants; a Captured node by its name and type, not its value (a
            /// translator caches the SQL of a shape: the same tree with other captured values has the same text). GetHashCode fits it (a Dictionary key).
            class Expression : IEquatable {
                string Kind
                string Type
                construct(string kind, string type) { this.Kind = kind; this.Type = type }
                /// The same shape (see the class).
                bool Equals(any other) {
                    if (!(other is of Expression)) { return false }
                    if (other.Kind != this.Kind || other.Type != this.Type || other.__Shape() != this.__Shape()) { return false }
                    any mine = this.__Parts()
                    any theirs = other.__Parts()
                    if (mine.length != theirs.length) { return false }
                    for (int i = 0; i < mine.length; i++) {
                        if (mine[i] == undefined) { if (theirs[i] != undefined) { return false } }
                        else if (!mine[i].Equals(theirs[i])) { return false }
                    }
                    return true
                }
                int GetHashCode() {
                    int h = __MathCall(15, this.Kind + "|" + this.Type + "|" + this.__Shape()) & 1073741823
                    any parts = this.__Parts()
                    for (int i = 0; i < parts.length; i++) {
                        int part = 7
                        if (parts[i] != undefined) { part = parts[i].GetHashCode() }
                        h = (h * 31 + part) & 1073741823
                    }
                    return h
                }
                // (what a node is besides its kind, type and children - Equals and GetHashCode compare it; its children in order)
                string __Shape() { return "" }
                any __Parts() { return [] }
                // (a new node of the same shape whose children went through the rewriter)
                Expression __Rebuild(ExpressionRewriter r) { return copy this }

                /// A new tree in which every part equal to `part` (Equals) is a copy of `replacement`. The result has no compiled code (Invoke: an
                /// ArgumentException); a translator reads it.
                static Expression Replace(Expression tree, Expression part, Expression replacement) {
                    return new __ExpressionReplacer(part, replacement).Rewrite(tree)
                }
                /// `a && b` of two conditions with the same parameters (b's parameters become a's); its Invoke runs both compiled lambdas.
                static LambdaExpression And(LambdaExpression a, LambdaExpression b) { return Expression.__Join("&&", a, b) }
                /// `a || b` (as And).
                static LambdaExpression Or(LambdaExpression a, LambdaExpression b) { return Expression.__Join("||", a, b) }
                /// `!a`.
                static LambdaExpression Not(LambdaExpression a) {
                    var tree = new LambdaExpression(Expression.__CopyParameters(a), new UnaryExpression("!", new ExpressionRewriter().Rewrite(a.Body), "bool"), "bool")
                    any fa = copy a.Compiled
                    if (fa != undefined) {
                        if (a.Parameters.length == 1) { tree.Compiled = (any x) => !(handover fa)(x) }
                        else if (a.Parameters.length == 2) { tree.Compiled = (any x, any y) => !(handover fa)(x, y) }
                    }
                    return tree
                }
                static LambdaExpression __Join(string op, LambdaExpression a, LambdaExpression b) {
                    if (a.Parameters.length != b.Parameters.length) {
                        throw new ArgumentException("Expression." + (op == "&&" ? "And" : "Or") + ": the conditions take " + a.Parameters.length + " and " + b.Parameters.length + " parameter(s)")
                    }
                    var right = new __ExpressionParameterRenamer(b, a).Rewrite(b.Body)
                    var tree = new LambdaExpression(Expression.__CopyParameters(a), new BinaryExpression(op, new ExpressionRewriter().Rewrite(a.Body), right, "bool"), "bool")
                    any fa = copy a.Compiled
                    any fb = copy b.Compiled
                    if (fa != undefined && fb != undefined) {
                        bool both = op == "&&"
                        if (a.Parameters.length == 1) { tree.Compiled = (any x) => Expression.__Two((handover fa), (handover fb), both, x, undefined, 1) }
                        else if (a.Parameters.length == 2) { tree.Compiled = (any x, any y) => Expression.__Two((handover fa), (handover fb), both, x, y, 2) }
                    }
                    return tree
                }
                static bool __Two(any fa, any fb, bool both, any x, any y, int n) {
                    bool first = n == 1 ? fa(x) : fa(x, y)
                    if (both && !first) { return false }
                    if (!both && first) { return true }
                    return n == 1 ? fb(x) : fb(x, y)
                }
                static any __CopyParameters(LambdaExpression a) {
                    any ps = new any[a.Parameters.length]
                    for (int i = 0; i < ps.length; i++) { ps[i] = copy a.Parameters[i] }
                    return ps
                }
                /// The node for the visitor (ExpressionVisitor.VisitBinary, ...).
                any Accept(ExpressionVisitor visitor) { return undefined }
                any Visit(ExpressionVisitor visitor) { return this.Accept(visitor) }
                string ToString() { return this.Kind }
                /// The Captured nodes of the tree in their order (the values that do not depend on a parameter - the parameters of an SQL text).
                List<CapturedExpression> Captures() {
                    var found = new List<CapturedExpression>()
                    var walker = new ExpressionCapturesWalker(found)
                    this.Accept(walker)
                    return found
                }
                // (a parameter `lambda expression<T>`: the call built the tree - a bare lambda came through a call the compiler did not know)
                static bool __Require(any v, string name) {
                    if (v == undefined || v is of LambdaExpression) { return true }
                    throw new ArgumentException("'" + name + "' is an expression tree: it needs the lambda written at a call the compiler knows (a call through 'any' passes only the code)")
                }
                // (a value as fire writes it: texts in quotes)
                static string __Text(any v) {
                    if (v == undefined) { return "undefined" }
                    if (v is of string) { return "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"" }
                    if (v is of bool) { if (v) { return "true" } return "false" }
                    return "" + v
                }
                static any __Copy(any items) {
                    any parts = new any[items.length]
                    for (int i = 0; i < items.length; i++) { parts[i] = items[i] }
                    return parts
                }
                static string __List(any items) {
                    string text = ""
                    for (int i = 0; i < items.length; i++) {
                        if (i > 0) { text = text + ", " }
                        text = text + items[i].ToString()
                    }
                    return text
                }
            }

            /// The lambda: its parameters and its body; Invoke runs the compiled lambda (a nested lambda of a tree has none: Compiled is undefined).
            class LambdaExpression : Expression {
                ParameterExpression Parameters[]
                Expression Body
                string ResultType
                any Compiled
                construct(any parameters, Expression body, string resultType) : base("Lambda", resultType) {
                    this.Parameters = take parameters
                    this.Body = take body
                    this.ResultType = resultType
                }
                // (the compiled lambda of the call - the compiler hands it on right after the tree)
                LambdaExpression __Attach(any compiled) { this.Compiled = try take compiled; return this }
                override string __Shape() { return this.Parameters.length + ":" + this.ResultType }
                override any __Parts() {
                    any parts = new any[this.Parameters.length + 1]
                    for (int i = 0; i < this.Parameters.length; i++) { parts[i] = this.Parameters[i] }
                    parts[this.Parameters.length] = this.Body
                    return parts
                }
                override Expression __Rebuild(ExpressionRewriter r) {
                    any ps = new any[this.Parameters.length]
                    for (int i = 0; i < ps.length; i++) { ps[i] = r.Rewrite(this.Parameters[i]) }
                    return new LambdaExpression(take ps, r.Rewrite(this.Body), this.ResultType)
                }
                override any Accept(ExpressionVisitor visitor) { return visitor.VisitLambda(this) }
                override string ToString() {
                    string body = this.Body.ToString()
                    if (this.Parameters.length == 1) { return this.Parameters[0].Name + " => " + body }
                    return "(" + Expression.__List(this.Parameters) + ") => " + body
                }
                /// The compiled lambda with these arguments.
                any Invoke(any a = undefined, any b = undefined, any c = undefined) {
                    any f = this.Compiled
                    if (f == undefined) { throw new ArgumentException("This lambda of an expression tree has no compiled code: a nested lambda (invoke the tree around it), or a tree made by Expression.Replace or an ExpressionRewriter.") }
                    int n = this.Parameters.length
                    if (n == 0) { return f() }
                    if (n == 1) { return f(a) }
                    if (n == 2) { return f(a, b) }
                    return f(a, b, c)
                }
                /// The compiled lambda itself.
                any Compile() { return this.Compiled }
            }

            class ParameterExpression : Expression {
                string Name
                int Index
                construct(string name, int index, string type) : base("Parameter", type) { this.Name = name; this.Index = index }
                override any Accept(ExpressionVisitor visitor) { return visitor.VisitParameter(this) }
                // (a parameter by its place, not its name: `p => p.age` and `x => x.age` have the same shape)
                override string __Shape() { return "" + this.Index }
                override string ToString() { return this.Name }
            }

            /// `p.age`: a field or a property of what Target gives.
            class MemberExpression : Expression {
                Expression Target
                string Name
                bool IsField
                bool IsProperty
                construct(Expression target, string name, string type, bool isField, bool isProperty) : base("Member", type) {
                    this.Target = take target
                    this.Name = name
                    this.IsField = isField
                    this.IsProperty = isProperty
                }
                override any Accept(ExpressionVisitor visitor) { return visitor.VisitMember(this) }
                override string ToString() { return this.Target.ToString() + "." + this.Name }
                override string __Shape() { return this.Name + ":" + this.IsField + ":" + this.IsProperty }
                override any __Parts() { return [this.Target] }
                override Expression __Rebuild(ExpressionRewriter r) { return new MemberExpression(r.Rewrite(this.Target), this.Name, this.Type, this.IsField, this.IsProperty) }
            }

            /// A literal: `18`, `"A"`, `true`, `undefined`, `5m`.
            class ConstantExpression : Expression {
                any Value
                construct(any value, string type) : base("Constant", type) { this.Value = try take value }
                override any Accept(ExpressionVisitor visitor) { return visitor.VisitConstant(this) }
                override string ToString() { return Expression.__Text(this.Value) }
                override string __Shape() { return Expression.__Text(this.Value) }
            }

            /// What does not depend on a parameter - a variable, a field, a member of one, a call: its value when the tree was made.
            class CapturedExpression : Expression {
                string Name
                any Value
                construct(string name, any value, string type) : base("Captured", type) { this.Name = name; this.Value = try take value }
                override any Accept(ExpressionVisitor visitor) { return visitor.VisitCaptured(this) }
                override string ToString() { return this.Name }
                // (by its name: the value is what changes from call to call)
                override string __Shape() { return this.Name }
            }

            /// `!x`, `-x`.
            class UnaryExpression : Expression {
                string Op
                Expression Operand
                construct(string op, Expression operand, string type) : base("Unary", type) { this.Op = op; this.Operand = take operand }
                override any Accept(ExpressionVisitor visitor) { return visitor.VisitUnary(this) }
                override string ToString() { return this.Op + this.Operand.ToString() }
                override string __Shape() { return this.Op }
                override any __Parts() { return [this.Operand] }
                override Expression __Rebuild(ExpressionRewriter r) { return new UnaryExpression(this.Op, r.Rewrite(this.Operand), this.Type) }
            }

            /// `a op b` (`==` `!=` `<` `<=` `>` `>=` `&&` `||` `+` `-` `*` `/` `%` `??`, ...).
            class BinaryExpression : Expression {
                string Op
                Expression Left
                Expression Right
                construct(string op, Expression left, Expression right, string type) : base("Binary", type) {
                    this.Op = op
                    this.Left = take left
                    this.Right = take right
                }
                override any Accept(ExpressionVisitor visitor) { return visitor.VisitBinary(this) }
                override string ToString() { return "(" + this.Left.ToString() + " " + this.Op + " " + this.Right.ToString() + ")" }
                override string __Shape() { return this.Op }
                override any __Parts() { return [this.Left, this.Right] }
                override Expression __Rebuild(ExpressionRewriter r) { return new BinaryExpression(this.Op, r.Rewrite(this.Left), r.Rewrite(this.Right), this.Type) }
            }

            /// `test ? a : b`.
            class ConditionalExpression : Expression {
                Expression Test
                Expression IfTrue
                Expression IfFalse
                construct(Expression test, Expression ifTrue, Expression ifFalse, string type) : base("Conditional", type) {
                    this.Test = take test
                    this.IfTrue = take ifTrue
                    this.IfFalse = take ifFalse
                }
                override any Accept(ExpressionVisitor visitor) { return visitor.VisitConditional(this) }
                override any __Parts() { return [this.Test, this.IfTrue, this.IfFalse] }
                override Expression __Rebuild(ExpressionRewriter r) { return new ConditionalExpression(r.Rewrite(this.Test), r.Rewrite(this.IfTrue), r.Rewrite(this.IfFalse), this.Type) }
                override string ToString() { return "(" + this.Test.ToString() + " ? " + this.IfTrue.ToString() + " : " + this.IfFalse.ToString() + ")" }
            }

            /// `s.StartsWith(x)`, `M.Abs(x)` (Target undefined: a static method of DeclaringType).
            class CallExpression : Expression {
                Expression Target
                string Method
                string DeclaringType
                Expression Arguments[]
                construct(Expression target, string method, string declaringType, any arguments, string type) : base("Call", type) {
                    this.Target = take target
                    this.Method = method
                    this.DeclaringType = declaringType
                    this.Arguments = take arguments
                }
                override any Accept(ExpressionVisitor visitor) { return visitor.VisitCall(this) }
                override string __Shape() { return this.Method + ":" + this.DeclaringType }
                override any __Parts() {
                    any parts = new any[this.Arguments.length + 1]
                    parts[0] = this.Target
                    for (int i = 0; i < this.Arguments.length; i++) { parts[i + 1] = this.Arguments[i] }
                    return parts
                }
                override Expression __Rebuild(ExpressionRewriter r) {
                    any args = new any[this.Arguments.length]
                    for (int i = 0; i < args.length; i++) { args[i] = r.Rewrite(this.Arguments[i]) }
                    any target = undefined
                    if (this.Target != undefined) { target = r.Rewrite(this.Target) }
                    return new CallExpression(take target, this.Method, this.DeclaringType, take args, this.Type)
                }
                override string ToString() {
                    string target = this.DeclaringType
                    if (this.Target != undefined) { target = this.Target.ToString() }
                    return target + "." + this.Method + "(" + Expression.__List(this.Arguments) + ")"
                }
            }

            /// `new T(arguments)`.
            class NewExpression : Expression {
                Expression Arguments[]
                construct(any arguments, string type) : base("New", type) { this.Arguments = take arguments }
                override any Accept(ExpressionVisitor visitor) { return visitor.VisitNew(this) }
                override any __Parts() { return Expression.__Copy(this.Arguments) }
                override Expression __Rebuild(ExpressionRewriter r) {
                    any args = new any[this.Arguments.length]
                    for (int i = 0; i < args.length; i++) { args[i] = r.Rewrite(this.Arguments[i]) }
                    return new NewExpression(take args, this.Type)
                }
                override string ToString() { return "new " + this.Type + "(" + Expression.__List(this.Arguments) + ")" }
            }

            /// `x is of T`.
            class IsTypeExpression : Expression {
                Expression Operand
                string TypeName
                construct(Expression operand, string typeName) : base("IsType", "bool") { this.Operand = take operand; this.TypeName = typeName }
                override any Accept(ExpressionVisitor visitor) { return visitor.VisitIsType(this) }
                override string __Shape() { return this.TypeName }
                override any __Parts() { return [this.Operand] }
                override Expression __Rebuild(ExpressionRewriter r) { return new IsTypeExpression(r.Rewrite(this.Operand), this.TypeName) }
                override string ToString() { return "(" + this.Operand.ToString() + " is of " + this.TypeName + ")" }
            }

            /// `row["name"]`, `arr[0]`.
            class IndexExpression : Expression {
                Expression Target
                Expression Index
                construct(Expression target, Expression index, string type) : base("Index", type) { this.Target = take target; this.Index = take index }
                override any Accept(ExpressionVisitor visitor) { return visitor.VisitIndex(this) }
                override any __Parts() { return [this.Target, this.Index] }
                override Expression __Rebuild(ExpressionRewriter r) { return new IndexExpression(r.Rewrite(this.Target), r.Rewrite(this.Index), this.Type) }
                override string ToString() { return this.Target.ToString() + "[" + this.Index.ToString() + "]" }
            }

            /// `x!int`.
            class ConvertExpression : Expression {
                Expression Operand
                construct(Expression operand, string type) : base("Convert", type) { this.Operand = take operand }
                override any Accept(ExpressionVisitor visitor) { return visitor.VisitConvert(this) }
                override any __Parts() { return [this.Operand] }
                override Expression __Rebuild(ExpressionRewriter r) { return new ConvertExpression(r.Rewrite(this.Operand), this.Type) }
                override string ToString() { return this.Operand.ToString() + "!" + this.Type }
            }

            /// Walks a tree: one method per kind; by default it visits the children and returns undefined (a translator overrides what it knows).
            class ExpressionVisitor {
                any VisitLambda(LambdaExpression e) {
                    for (int i = 0; i < e.Parameters.length; i++) { e.Parameters[i].Accept(this) }
                    e.Body.Accept(this)
                    return undefined
                }
                any VisitParameter(ParameterExpression e) { return undefined }
                any VisitMember(MemberExpression e) { e.Target.Accept(this); return undefined }
                any VisitConstant(ConstantExpression e) { return undefined }
                any VisitCaptured(CapturedExpression e) { return undefined }
                any VisitUnary(UnaryExpression e) { e.Operand.Accept(this); return undefined }
                any VisitBinary(BinaryExpression e) { e.Left.Accept(this); e.Right.Accept(this); return undefined }
                any VisitConditional(ConditionalExpression e) { e.Test.Accept(this); e.IfTrue.Accept(this); e.IfFalse.Accept(this); return undefined }
                any VisitCall(CallExpression e) {
                    if (e.Target != undefined) { e.Target.Accept(this) }
                    for (int i = 0; i < e.Arguments.length; i++) { e.Arguments[i].Accept(this) }
                    return undefined
                }
                any VisitNew(NewExpression e) {
                    for (int i = 0; i < e.Arguments.length; i++) { e.Arguments[i].Accept(this) }
                    return undefined
                }
                any VisitIsType(IsTypeExpression e) { e.Operand.Accept(this); return undefined }
                any VisitIndex(IndexExpression e) { e.Target.Accept(this); e.Index.Accept(this); return undefined }
                any VisitConvert(ConvertExpression e) { e.Operand.Accept(this); return undefined }
            }

            class ExpressionCapturesWalker : ExpressionVisitor {
                any found
                construct(any found) { this.found = found }
                override any VisitCaptured(CapturedExpression e) { this.found.Add(e); return undefined }
            }

            /// Makes a new tree from a tree: Rewrite(e) gives a copy of `e` whose children went through Rewrite. Derive and override Rewrite to change
            /// parts (`base.Rewrite(e)` for the rest). The new tree has no compiled code.
            class ExpressionRewriter {
                Expression Rewrite(Expression e) { return e.__Rebuild(this) }
            }

            class __ExpressionReplacer : ExpressionRewriter {
                Expression part
                Expression replacement
                construct(Expression part, Expression replacement) { this.part = copy part; this.replacement = copy replacement }
                override Expression Rewrite(Expression e) {
                    if (e.Equals(this.part)) { return copy this.replacement }
                    return base.Rewrite(e)
                }
            }

            // (the parameters of `source` become those of `target` - by name; a nested lambda that names its own parameter so hides it)
            class __ExpressionParameterRenamer : ExpressionRewriter {
                any names
                any to
                construct(LambdaExpression source, LambdaExpression target) {
                    this.names = new List<string>()
                    this.to = new List<string>()
                    for (int i = 0; i < source.Parameters.length; i++) { this.names.Add(source.Parameters[i].Name); this.to.Add(target.Parameters[i].Name) }
                }
                override Expression Rewrite(Expression e) {
                    if (e is of ParameterExpression) {
                        int at = this.names.IndexOf(e.Name)
                        if (at >= 0) { return new ParameterExpression(this.to[at], e.Index, e.Type) }
                        return base.Rewrite(e)
                    }
                    if (e is of LambdaExpression) {
                        any hidden = new List<int>()
                        any hiddenNames = new List<string>()
                        for (int i = 0; i < e.Parameters.length; i++) {
                            int at = this.names.IndexOf(e.Parameters[i].Name)
                            if (at >= 0) { hidden.Add(at); hiddenNames.Add(this.names[at]); this.names[at] = "" }
                        }
                        any result = base.Rewrite(e)
                        for (int i = 0; i < hidden.Count; i++) { this.names[hidden[i]] = hiddenNames[i] }
                        return result
                    }
                    return base.Rewrite(e)
                }
            }

            class ChannelIter<T> : IEnumerator<T> {
                any ch
                T current
                construct(any ch) { this.ch = ch }
                bool MoveNext() {
                    T v = this.ch.Receive()
                    if (v == undefined) { return false }
                    this.current = try take v   // (a lent record stays its owner's)
                    return true
                }
                T GetCurrent() { return this.current }
            }

            // (`until x`: a token, a source or a time limit as the node a task follows)
            class __Cancel {
                static int Limit(any limit) {
                    if (limit is of CancellationToken) { return __cancel_ref(limit.node) }
                    if (limit is of CancellationSource) { return __cancel_ref(limit.node) }
                    return __cancel_time(limit)
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
                    this.Command = try take command   // (a fresh lambda belongs to the call: the command keeps it)
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
                    this.Command = try take command   // (a fresh lambda belongs to the call: the command keeps it)
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
                    this.items = new T[8]
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
                    this.items = new T[8]
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
                    // (it takes what belongs to the call - `??.`: a number has no TakeTo; with Owns.Values also an object or a lambda of someone else)
                    if (this.owns.HasFlag(Owns.Values) && (value is of class || value is of lambda)) { value.TakeTo(this) } else { try value??.TakeTo(this) }
                    this.items[this.count] = value
                    this.count = this.count + 1
                }

                // an element that falls out: destroyed when the list owns its elements (Owns.Values) and this one
                undefined DropElement(any value) {
                    if (this.owns.HasFlag(Owns.Values) && value is from this) { delete value }
                }

                T operator[](int index) {
                    return this.items[index]
                }

                undefined operator[](int index, T value) {
                    any old = this.items[index]
                    // (it takes what belongs to the call - `??.`: a number has no TakeTo; with Owns.Values also an object or a lambda of someone else)
                    if (this.owns.HasFlag(Owns.Values) && (value is of class || value is of lambda)) { value.TakeTo(this) } else { try value??.TakeTo(this) }
                    this.items[index] = value
                    if (old != value) { this.DropElement(old) }
                }

                // removes the element at `index` (the ones behind it move up); the element itself is destroyed only if the list owns it (Owns.Values)
                undefined RemoveAt(int index) {
                    any old = this.items[index]
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
                    any value = this.items[index]
                    any taken = undefined   // (declared here: the call owns what `take` gives it, not the block of the `if`)
                    if (value is from this) { taken = take value }
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
                    // (it takes what belongs to the call - `??.`: a number has no TakeTo; with Owns.Values also an object or a lambda of someone else)
                    if (this.owns.HasFlag(Owns.Values) && (value is of class || value is of lambda)) { value.TakeTo(this) } else { try value??.TakeTo(this) }
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

                // is `value` in the list (`==`, an object: the same one)?
                bool Contains(T value) { return this.IndexOf(value) >= 0 }

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
                        any old = this.items[i]
                        this.items[i] = undefined
                        this.DropElement(old)
                        i = i + 1
                    }
                    this.count = 0
                }

                undefined Grow() {
                    var newItems = new T[this.items.length * 2]
                    var i = 0
                    while (i < this.count) {
                        newItems[i] = this.items[i]
                        i = i + 1
                    }
                    // the new array belongs to the list (a local array dies with this method), the old one is released
                    any old = this.items
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
            // value - `1km` is `1000m`), an IEquatable key with Equals/GetHashCode. The arrays are typed: `keys` and `vals` by the type arguments (`new K[n]`:
            // `Dictionary<int, float>` holds its keys and values raw, `Dictionary<string, Node>` as values), the others hold ints and bools.
            class __HashTable<K, V> {
                protected any keys
                protected any vals
                private int hashes[]
                private int next[]
                protected bool live[]
                private int buckets[]
                private int count
                protected int used
                protected Owns owns

                /// The number of entries.
                int Count { get { return this.count } }

                construct(Owns owns) {
                    this.owns = owns
                    this.keys = new K[8]
                    this.vals = new V[8]
                    this.hashes = new int[8]
                    this.next = new int[8]
                    this.live = new bool[8]
                    this.buckets = new int[8]
                    for (var b = 0; b < 8; b = b + 1) { this.buckets[b] = 0 }   // (a new array holds undefined)
                    this.count = 0
                    this.used = 0
                }

                // (30 bits: the hash fits the int array `hashes` also where an int has 32 bits; the buckets take its low bits)
                int HashOf(any key) {
                    if (key is of IEquatable) { return key.GetHashCode() & 1073741823 }
                    return __MathCall(15, key) & 1073741823
                }

                bool SameKey(any a, any b) {
                    if (a is of IEquatable) { return a.Equals(b) }
                    return a == b
                }

                // the entry of `key` (hash `h`), -1 if there is none (a native build reads the elements of the typed arrays in place)
                int Find(any key, int h) {
                    int i = this.buckets[h & (this.buckets.length - 1)] - 1
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
                    int b = h & (this.buckets.length - 1)
                    this.next[i] = this.buckets[b]
                    this.buckets[b] = i + 1
                    this.count = this.count + 1
                }

                // takes the entry out of its chain; key and value stay with the caller (Drop or TakeLocal)
                undefined Unlink(int at) {
                    int b = this.hashes[at] & (this.buckets.length - 1)
                    int i = this.buckets[b] - 1
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
                    if (this.owns.HasFlag(what) && x is from this) { delete x }
                }

                // room for more entries: the live ones move to new arrays (in their order, without the removed ones)
                undefined Resize() {
                    var cap = 8
                    while (cap < this.count * 2) { cap = cap * 2 }
                    var nk = new K[cap]
                    var nv = new V[cap]
                    int nh[] = new int[cap]
                    int nn[] = new int[cap]
                    bool nl[] = new bool[cap]
                    int nb[] = new int[cap]
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
                    any oldKeys = this.keys
                    any oldVals = this.vals
                    any oldHashes = this.hashes
                    any oldNext = this.next
                    any oldLive = this.live
                    any oldBuckets = this.buckets
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
                            any k = this.keys[i]
                            any v = this.vals[i]
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
                    any at = this.table.__NextEntry(this.index)
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
                    any at = this.table.__NextEntry(this.index)
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
            class Dictionary<K, V> : __HashTable<K, V>, IEnumerable<KeyValuePair<K, V>> {
                construct(Owns owns = Owns.Explicit) : base(owns) { }

                /// Adds a key that is not there yet (an ArgumentException if it is).
                undefined Add(K key, V value) {
                    var h = this.HashOf(key)
                    if (this.Find(key, h) >= 0) { throw new ArgumentException("The key '" + key + "' is already in the dictionary.") }
                    // (it takes what belongs to the call - `??.`: a number has no TakeTo; with Owns.Keys also an object or a lambda of someone else)
                    if (this.owns.HasFlag(Owns.Keys) && (key is of class || key is of lambda)) { key.TakeTo(this) } else { try key??.TakeTo(this) }
                    // (it takes what belongs to the call - `??.`: a number has no TakeTo; with Owns.Values also an object or a lambda of someone else)
                    if (this.owns.HasFlag(Owns.Values) && (value is of class || value is of lambda)) { value.TakeTo(this) } else { try value??.TakeTo(this) }
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
                    // (it takes what belongs to the call - `??.`: a number has no TakeTo; with Owns.Values also an object or a lambda of someone else)
                    if (this.owns.HasFlag(Owns.Values) && (value is of class || value is of lambda)) { value.TakeTo(this) } else { try value??.TakeTo(this) }
                    if (at >= 0) {
                        any old = this.vals[at]
                        this.vals[at] = value
                        if (old != value) { this.Drop(old, Owns.Values) }
                        return
                    }
                    // (it takes what belongs to the call - `??.`: a number has no TakeTo; with Owns.Keys also an object or a lambda of someone else)
                    if (this.owns.HasFlag(Owns.Keys) && (key is of class || key is of lambda)) { key.TakeTo(this) } else { try key??.TakeTo(this) }
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
                    any k = this.keys[at]
                    any v = this.vals[at]
                    this.Unlink(at)
                    this.Drop(k, Owns.Keys)
                    this.Drop(v, Owns.Values)
                    return true
                }

                /// Removes `key` and gives its value to the caller (who owns it then, if the dictionary owned it).
                V Take(K key) {
                    var at = this.Find(key, this.HashOf(key))
                    if (at < 0) { throw new ArgumentException("The key '" + key + "' is not in the dictionary.") }
                    any k = this.keys[at]
                    any v = this.vals[at]
                    this.Unlink(at)
                    this.Drop(k, Owns.Keys)
                    any taken = undefined   // (declared here: the call owns what `take` gives it, not the block of the `if`)
                    if (v is from this) {
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
            class HashSet<T> : __HashTable<T, any>, IEnumerable<T> {
                construct(Owns owns = Owns.Explicit) : base(owns) { }

                /// Adds `item`; false if an equal one is already there.
                bool Add(T item) {
                    var h = this.HashOf(item)
                    if (this.Find(item, h) >= 0) { return false }
                    // (it takes what belongs to the call - `??.`: a number has no TakeTo; with Owns.Values also an object or a lambda of someone else)
                    if (this.owns.HasFlag(Owns.Values) && (item is of class || item is of lambda)) { item.TakeTo(this) } else { try item??.TakeTo(this) }
                    this.Insert(item, undefined, h)
                    return true
                }

                bool Contains(T item) { return this.Find(item, this.HashOf(item)) >= 0 }

                /// Removes `item` (destroyed if the set owns it); false if it is not there.
                bool Remove(T item) {
                    var at = this.Find(item, this.HashOf(item))
                    if (at < 0) { return false }
                    any k = this.keys[at]
                    this.Unlink(at)
                    this.Drop(k, Owns.Values)
                    return true
                }

                /// Removes `item` and gives the element of the set to the caller (who owns it then, if the set owned it).
                T Take(T item) {
                    var at = this.Find(item, this.HashOf(item))
                    if (at < 0) { throw new ArgumentException("The item '" + item + "' is not in the set.") }
                    any k = this.keys[at]
                    this.Unlink(at)
                    any taken = undefined   // (declared here: the call owns what `take` gives it, not the block of the `if`)
                    if (k is from this) {
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
