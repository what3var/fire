using System.Text;
using fire.Ast;

namespace fire.Compiler
{
    /// <summary>
    /// The driver of `forge test` (docs/TESTING.md): fire source that runs the tests of a program. Every class with a `[Test]` method gets one object (like NUnit); then `[PrepareTesting]` once,
    /// for every test (and every set of `[Parameters]`) `[PrepareTest]`, the test and `[CleanTest]`, at the end `[CleanTesting]`. The driver reports with lines that start with
    /// <see cref="Marker"/> (<see cref="TestProtocol"/> reads them, in the VM and from a native program alike); everything else the tests print is their output.
    ///
    ///   S|class|method|name|description   a test starts
    ///   P                                  it passed
    ///   F|message                          an `Assertion` failed (a failure)
    ///   E|message                          another exception (an error)
    ///   C|message                          `[CleanTest]` threw (the test is an error)
    ///   X|class|phase|message              the object, `[PrepareTesting]` or `[CleanTesting]` threw
    /// </summary>
    public static class TestDriver
    {
        public const string Marker = "##fire-test##";

        public const string Test = "Test", Parameters = "Parameters", PrepareTest = "PrepareTest", CleanTest = "CleanTest", PrepareTesting = "PrepareTesting", CleanTesting = "CleanTesting";

        private static readonly HashSet<string> Known = new(StringComparer.Ordinal) { Test, Parameters, PrepareTest, CleanTest, PrepareTesting, CleanTesting };

        /// <summary>The driver for the classes of <paramref name="program"/> from source <paramref name="firstUserSource"/> on. What is wrong with an attribute goes to
        /// <paramref name="problems"/> (with file and line, see <paramref name="fileOf"/>); that test does not run. <paramref name="testCount"/>: the tests found (a set of parameters counts once).</summary>
        public static string Generate(IReadOnlyList<Stmt> program, int firstUserSource, Func<int, string?> fileOf, List<string> problems, out int testCount)
        {
            var sb = new StringBuilder();
            var runs = new List<string>();
            testCount = 0;
            sb.AppendLine("class __Tests {");
            sb.AppendLine($"    static undefined Say(string text) {{ print(\"{Marker}\" + text.Replace(\"\\r\", \"\").Replace(\"\\n\", \"\\\\n\")) }}");
            sb.AppendLine("    static string Describe(any e) {");
            sb.AppendLine("        if (e is of Exception) { if (e.message == undefined) { return \"\" } return \"\" + e.message }");
            sb.AppendLine("        return \"\" + e");
            sb.AppendLine("    }");
            sb.AppendLine("    static undefined Failed(any e) {");
            sb.AppendLine("        if (e is of Assertion) { __Tests.Say(\"F|\" + __Tests.Describe(e)) } else { __Tests.Say(\"E|\" + __Tests.Describe(e)) }");
            sb.AppendLine("    }");

            int classIndex = 0;
            foreach (var cls in program.OfType<ClassDecl>().Where(c => c.Source >= firstUserSource))
            {
                var methods = cls.Members.OfType<MethodDecl>().ToList();
                string where(int line) => $"{fileOf(cls.Source) ?? "?"}:{line}";
                foreach (var m in methods)
                    foreach (var a in m.Attributes ?? Array.Empty<AttributeDecl>())
                        if (Known.Contains(a.Name) && a.Name != Test && a.Name != Parameters && m.Params.Count > 0)
                            problems.Add($"{where(a.Line)}: [{a.Name}] needs a method without parameters ('{m.Name}' has {m.Params.Count}).");

                foreach (var m in methods.Where(m => Has(m, Parameters) && !Has(m, Test)))
                    problems.Add($"{where(m.Line)}: '{cls.Name}.{m.Name}' has [Parameters] but no [Test].");
                var tests = methods.Where(m => Has(m, Test)).ToList();
                if (tests.Count == 0) continue;
                if (cls.TypeParams is { Count: > 0 }) { problems.Add($"{where(cls.Line)}: the test class '{cls.Name}' is generic - a test class needs a plain `new {cls.Name}()`."); continue; }

                string Call(MethodDecl m, string args = "") => (m.IsStatic ? cls.Name : "o") + "." + m.Name + "(" + args + ")";
                var prepare = methods.Where(m => Has(m, PrepareTest) && m.Params.Count == 0).ToList();
                var clean = methods.Where(m => Has(m, CleanTest) && m.Params.Count == 0).ToList();

                string run = $"Class{classIndex++}";
                runs.Add(run);
                sb.AppendLine($"    static undefined {run}() {{");
                sb.AppendLine($"        try {{ __Tests.{run}Body(new {cls.Name}()) }} catch (e) {{ __Tests.Say(\"X|{Escape(cls.Name)}|construct|\" + __Tests.Describe(e)) }}");
                sb.AppendLine("    }");
                sb.AppendLine($"    static undefined {run}Body({cls.Name} o) {{");
                foreach (var m in methods.Where(m => Has(m, PrepareTesting) && m.Params.Count == 0))
                    sb.AppendLine($"        try {{ {Call(m)} }} catch (e) {{ __Tests.Say(\"X|{Escape(cls.Name)}|PrepareTesting|\" + __Tests.Describe(e)); return }}");

                foreach (var test in tests)
                {
                    var testAttr = test.Attributes!.First(a => a.Name == Test);
                    string title = testAttr.Args.Count > 0 && testAttr.Args[0] is LiteralExpr { Value.Kind: Values.ValueKind.String } t ? t.Value.AsString() : test.Name;
                    string description = testAttr.Args.Count > 1 && testAttr.Args[1] is LiteralExpr { Value.Kind: Values.ValueKind.String } d ? d.Value.AsString() : "";
                    if (testAttr.Args.Count > 2) problems.Add($"{where(testAttr.Line)}: [Test] takes a title and a description, not {testAttr.Args.Count} arguments.");
                    var sets = test.Attributes!.Where(a => a.Name == Parameters).ToList();
                    if (sets.Count == 0)
                    {
                        if (test.Params.Count > 0) { problems.Add($"{where(test.Line)}: the test '{cls.Name}.{test.Name}' takes {test.Params.Count} parameters - give them with [Parameters ...]."); continue; }
                        testCount++;
                        EmitCase(sb, "        ", cls.Name, test.Name, Quote(title), description, prepare.Select(m => Call(m)), Call(test), clean.Select(m => Call(m)));
                        continue;
                    }
                    foreach (var set in sets)
                    {
                        if (set.Args.Count != test.Params.Count) { problems.Add($"{where(set.Line)}: [Parameters] gives {set.Args.Count} values, '{test.Name}' takes {test.Params.Count}."); continue; }
                        testCount++;
                        // a range (`1..5`) runs the test for each of its numbers; with several ranges for each combination
                        string indent = "        ";
                        var values = new List<string>();
                        var shown = new List<string>();
                        int depth = 0;
                        for (int i = 0; i < set.Args.Count; i++)
                        {
                            if (set.Args[i] is RangeExpr)
                            {
                                string v = $"p{i}";
                                sb.AppendLine($"{indent}foreach ({v} in {set.ArgTexts[i]}) {{");
                                indent += "    ";
                                depth++;
                                values.Add(v);
                                shown.Add($"\"\" + {v}");
                            }
                            else
                            {
                                values.Add(set.ArgTexts[i]);
                                shown.Add(Quote(set.ArgTexts[i]));
                            }
                        }
                        string name = Quote(title + "(") + " + " + string.Join(" + \", \" + ", shown) + " + \")\"";
                        EmitCase(sb, indent, cls.Name, test.Name, name, description, prepare.Select(m => Call(m)), Call(test, string.Join(", ", values)), clean.Select(m => Call(m)));
                        for (int i = 0; i < depth; i++) { indent = indent[..^4]; sb.AppendLine($"{indent}}}"); }
                    }
                }

                foreach (var m in methods.Where(m => Has(m, CleanTesting) && m.Params.Count == 0))
                    sb.AppendLine($"        try {{ {Call(m)} }} catch (e) {{ __Tests.Say(\"X|{Escape(cls.Name)}|CleanTesting|\" + __Tests.Describe(e)) }}");
                sb.AppendLine("    }");
            }
            sb.AppendLine("}");
            foreach (var run in runs) sb.AppendLine($"__Tests.{run}()");
            sb.AppendLine("__Tests.Say(\"D\")");
            return sb.ToString();
        }

        private static void EmitCase(StringBuilder sb, string indent, string cls, string method, string nameExpr, string description, IEnumerable<string> prepare, string call, IEnumerable<string> clean)
        {
            sb.AppendLine($"{indent}__Tests.Say(\"S|{Escape(cls)}|{Escape(method)}|\" + ({nameExpr}).Replace(\"|\", \"/\") + \"|{Escape(description)}\")");
            sb.AppendLine($"{indent}try {{");
            foreach (var p in prepare) sb.AppendLine($"{indent}    {p}");
            sb.AppendLine($"{indent}    {call}");
            sb.AppendLine($"{indent}    __Tests.Say(\"P\")");
            sb.AppendLine($"{indent}}} catch (e) {{ __Tests.Failed(e) }}");
            foreach (var c in clean) sb.AppendLine($"{indent}try {{ {c} }} catch (e) {{ __Tests.Say(\"C|\" + __Tests.Describe(e)) }}");
        }

        private static bool Has(MethodDecl m, string attribute) => m.Attributes?.Any(a => a.Name == attribute) == true;

        /// <summary>Text inside a fire string literal (and without the field separator of the protocol).</summary>
        private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", "").Replace("|", "/");

        private static string Quote(string text) => "\"" + Escape(text) + "\"";
    }
}
