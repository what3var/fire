using System.Text.RegularExpressions;
using fire.Ast;

namespace fire.Compiler
{
    /// <summary>A test found in a source file without running anything (the Test Explorer of the editor): its class, method, title, file and line.</summary>
    public sealed record DiscoveredTest(string Class, string Method, string Title, string Description, string File, int Line, bool HasParameters)
    {
        /// <summary>The key of the test for <see cref="TestFilter"/>: `Class.Method`.</summary>
        public string Key => Class + "." + Method;
    }

    /// <summary>
    /// Finds the tests of files (docs/TESTING.md): the methods with `[Test]` of the classes of a file, as `forge test` would run them. The file is only parsed - its `#include`s are
    /// left out (what they bring is found in their own file), nothing is compiled or run, so it is quick enough for an editor that refreshes its list after every save.
    /// </summary>
    public static class TestDiscovery
    {
        private static readonly Regex IncludeLine = new(@"^[ \t]*#include\b", RegexOptions.Compiled);

        /// <summary>The tests of one file (<paramref name="text"/>: its current text, else it is read). A file that does not parse goes to <paramref name="problems"/> and has no tests.</summary>
        public static List<DiscoveredTest> InFile(string path, string? text = null, List<string>? problems = null)
        {
            var found = new List<DiscoveredTest>();
            if (fire.Projects.FireProject.IsMarkupFile(path)) return found;   // the markup of an interface has no tests
            try
            {
                text ??= File.ReadAllText(path);
                if (!text.Contains("[Test", StringComparison.Ordinal)) return found;   // the cheap answer for most files
                // the #include lines stay as empty lines, so that the line numbers stay those of the file
                var lines = text.Replace("\r\n", "\n").Split('\n').Select(l => IncludeLine.IsMatch(l) ? "" : l);
                var processed = Preprocessor.Process(string.Join("\n", lines), Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
                var program = Parser.ParseMultiple(new[] { processed });
                foreach (var cls in Classes(program))
                    foreach (var m in cls.Members.OfType<MethodDecl>())
                    {
                        var test = m.Attributes?.FirstOrDefault(a => a.Name == TestDriver.Test);
                        if (test == null) continue;
                        string title = test.Args.Count > 0 && test.Args[0] is LiteralExpr { Value.Kind: Values.ValueKind.String } t ? t.Value.AsString() : m.Name;
                        string description = test.Args.Count > 1 && test.Args[1] is LiteralExpr { Value.Kind: Values.ValueKind.String } d ? d.Value.AsString() : "";
                        found.Add(new DiscoveredTest(cls.Name, m.Name, title, description, path, m.Line, m.Params.Count > 0));
                    }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException || CommandLineRunner.IsCompileError(ex))
            {
                problems?.Add($"{Path.GetFileName(path)}: {(CommandLineRunner.IsCompileError(ex) ? CompileErrors.Describe(ex) : ex.Message)}");
            }
            return found;
        }

        /// <summary>The tests of several files, in their order.</summary>
        public static List<DiscoveredTest> InFiles(IEnumerable<string> paths, Func<string, string?>? textOf = null, List<string>? problems = null) =>
            paths.SelectMany(p => InFile(p, textOf?.Invoke(p), problems)).ToList();

        /// <summary>The classes of a program, also those inside a namespace (with the name the driver uses).</summary>
        private static IEnumerable<ClassDecl> Classes(IEnumerable<Stmt> program)
        {
            foreach (var s in program)
            {
                if (s is ClassDecl c) yield return c;
                else if (s is NamespaceDecl ns) foreach (var inner in Classes(ns.Members)) yield return inner;
            }
        }
    }
}
