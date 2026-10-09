namespace fire.Benchmarks
{
    /// <summary>A benchmark: a fire program that prints exactly ONE line (its result).
    /// The result serves as a regression check: an optimization must never change it (see
    /// `--check`/`--save` in Program.cs).</summary>
    public sealed record BenchmarkScript(string Name, string Description, string Source);

    public static class BenchmarkScripts
    {
        public static readonly BenchmarkScript[] All =
        {
            new("loop", "while/for loop with int arithmetic and modulo (stack, locals, jumps)", """
                var sum = 0
                for (var i = 0; i < 1500000; i = i + 1) {
                    sum = sum + i % 7
                }
                print(sum)
                """),

            new("float", "float arithmetic in a loop", """
                var x = 0.0
                for (var i = 0; i < 600000; i = i + 1) {
                    x = x + i * 0.5 - x / 3.0
                }
                print(x)
                """),

            new("fib", "recursive static method (call/return cost, one scope per call)", """
                class M {
                    static int Fib(int n) {
                        if (n < 2) { return n }
                        return M.Fib(n - 1) + M.Fib(n - 2)
                    }
                }
                print(M.Fib(23))
                """),

            new("method", "Instance methods and field accesses (CallMethod, GetField/SetField)", """
                class Counter {
                    int count
                    int step
                    construct() { this.count = 0; this.step = 2 }
                    Inc() { this.count = this.count + this.step }
                    int Get() { return this.count }
                }
                var c = new Counter()
                for (var i = 0; i < 250000; i = i + 1) {
                    c.Inc()
                }
                print(c.Get())
                """),

            new("array", "Fill an array and sum it up (ArrayGet/ArraySet)", """
                var n = 100000
                var a = new int[n]
                var total = 0
                for (var pass = 0; pass < 5; pass = pass + 1) {
                    for (var i = 0; i < n; i = i + 1) { a[i] = i + pass }
                    for (var i = 0; i < n; i = i + 1) { total = total + a[i] }
                }
                print(total)
                """),

            new("string", "String concatenation and prelude string methods (extension -> native ID)", """
                var s = "Hello, World, again"
                var n = 0
                for (var i = 0; i < 40000; i = i + 1) {
                    n = n + s.IndexOf("o") + s.Substring(3, 4).Length + s.Length
                }
                var text = ""
                for (var i = 0; i < 2000; i = i + 1) { text = text + "ab" }
                print(n + text.Length)
                """),

            new("alloc", "Create and release objects (ownership, Scope.Release, constructor)", """
                class Point {
                    int x
                    int y
                    construct(int x, int y) { this.x = x; this.y = y }
                }
                var sum = 0
                for (var i = 0; i < 60000; i = i + 1) {
                    var p = new Point(i, i + 1)
                    sum = sum + p.x + p.y
                }
                print(sum)
                """),

            new("lambda", "Lambda-Aufrufe (Call, Closure-Scope)", """
                var add = func (a, b) => { return a + b }
                var acc = 0
                for (var i = 0; i < 200000; i = i + 1) {
                    acc = add(acc, i)
                }
                print(acc)
                """),

            new("list", "List from the prelude: Add and foreach", """
                var l = new List()
                for (var i = 0; i < 40000; i = i + 1) { l.Add(i) }
                var total = 0
                foreach (v in l) { total = total + v }
                print(total)
                """),
        };
    }
}
