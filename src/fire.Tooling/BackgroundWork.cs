namespace fire.Tooling
{
    /// <summary>
    /// Long work that a program waits for without a build window: the standard packages installed, the natives of a package or the library of graphics and windows built for the VM
    /// (PackageLibrary, StandardSources) - the first start of a program that uses them can take a minute. Whoever shows it (spark's status bar: a small bar with the text) listens to
    /// <see cref="Changed"/>; a forge started by spark writes it to its error output as lines of <see cref="LinePrefix"/> (with <see cref="LinesVariable"/> set), so that spark shows
    /// the work of its child processes (the assistant) too.
    /// </summary>
    public static class BackgroundWork
    {
        /// <summary>A piece of work: what it does and, when known, its step (<see cref="Total"/> 0: no count).</summary>
        public readonly record struct Item(long Id, string Text, int Done, int Total);

        /// <summary>With this variable set (to 1), forge writes the work to its error output (<see cref="WriteLines"/>).</summary>
        public const string LinesVariable = "FIRE_PROGRESS_LINES";

        /// <summary>The start of such a line: "##fire-progress <done> <total> <text>" while something runs, "##fire-progress" alone when nothing runs any more.</summary>
        public const string LinePrefix = "##fire-progress";

        private static readonly object s_lock = new();
        private static readonly List<Item> s_items = new();
        private static long s_next;

        /// <summary>The work began, made progress or ended (on the thread that does it).</summary>
        public static event Action? Changed;

        /// <summary>What runs now, the oldest first.</summary>
        public static IReadOnlyList<Item> Items { get { lock (s_lock) return s_items.ToArray(); } }

        /// <summary>The work that runs now and was begun last (null: none).</summary>
        public static Item? Latest { get { lock (s_lock) return s_items.Count > 0 ? s_items[^1] : null; } }

        /// <summary>Work begins; it ends when the result is disposed (also when it failed).</summary>
        public static IDisposable Begin(string text, int done = 0, int total = 0)
        {
            long id;
            lock (s_lock)
            {
                id = ++s_next;
                s_items.Add(new Item(id, text, done, total));
            }
            Raise();
            return new Scope(id);
        }

        private static void End(long id)
        {
            lock (s_lock) s_items.RemoveAll(i => i.Id == id);
            Raise();
        }

        private static void Raise()
        {
            try { Changed?.Invoke(); } catch (Exception) { /* (a listener's failure does not stop the work) */ }
        }

        /// <summary>The line for <see cref="LinePrefix"/> that says what runs now.</summary>
        public static string Line()
        {
            var latest = Latest;
            return latest is { } i ? $"{LinePrefix} {i.Done} {i.Total} {i.Text.Replace('\n', ' ').Replace('\r', ' ')}" : LinePrefix;
        }

        /// <summary>Reads a line of <see cref="LinePrefix"/>: false for any other line; <paramref name="item"/> null when nothing runs any more.</summary>
        public static bool TryParse(string line, out Item? item)
        {
            item = null;
            if (!line.StartsWith(LinePrefix, StringComparison.Ordinal)) return false;
            string rest = line.Substring(LinePrefix.Length).Trim();
            if (rest.Length == 0) return true;
            string[] parts = rest.Split(' ', 3);
            if (parts.Length < 3 || !int.TryParse(parts[0], out int done) || !int.TryParse(parts[1], out int total)) return false;
            item = new Item(0, parts[2], done, total);
            return true;
        }

        /// <summary>forge: with <see cref="LinesVariable"/> set, every change goes to <paramref name="error"/> as a line.</summary>
        public static void WriteLines(TextWriter error)
        {
            if (Environment.GetEnvironmentVariable(LinesVariable) is not { Length: > 0 } v || v == "0") return;
            Changed += () =>
            {
                string line = Line();
                lock (error) { error.WriteLine(line); error.Flush(); }
            };
        }

        private sealed class Scope(long id) : IDisposable
        {
            private int _disposed;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0) End(id);
            }
        }
    }
}
