using System.Text.RegularExpressions;

namespace fire.Tooling
{
    /// <summary>
    /// Which tests run (`forge test --filter`, the Test Explorer of the editor): a list of patterns, each `Class.Method` or just `Class` (all its tests); `*` stands for any text.
    /// A test runs when one pattern matches it.
    /// </summary>
    public sealed class TestFilter
    {
        private readonly List<Regex> _patterns;

        /// <summary>The patterns as `forge test --filter` takes them (separated by `,`).</summary>
        public string Text { get; }

        private TestFilter(IEnumerable<string> patterns)
        {
            Text = string.Join(",", patterns.Select(p => p.Trim()).Where(p => p.Length > 0));
            _patterns = patterns.Select(p => p.Trim()).Where(p => p.Length > 0)
                .Select(p => new Regex("^" + Regex.Escape(p).Replace("\\*", ".*") + "$", RegexOptions.CultureInvariant)).ToList();
        }

        /// <summary>The filter of patterns separated by `,` or `;` (`forge test --filter A.B,C`).</summary>
        public static TestFilter Parse(string patterns) => new(patterns.Split(',', ';'));

        /// <summary>The filter of keys (`Class.Method`, see <see cref="DiscoveredTest.Key"/>) or patterns.</summary>
        public static TestFilter Of(IEnumerable<string> keys) => new(keys);

        public bool IsEmpty => _patterns.Count == 0;

        /// <summary>Does the test <paramref name="method"/> of class <paramref name="cls"/> run?</summary>
        public bool Includes(string cls, string method)
        {
            string key = cls + "." + method;
            return _patterns.Any(p => p.IsMatch(key) || p.IsMatch(cls));
        }
    }
}
