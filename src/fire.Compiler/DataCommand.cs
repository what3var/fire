using System.Text;
using fire.Bytecode;
using fire.Runtime;
using fire.Values;

namespace fire.Compiler
{
    /// <summary>
    /// `forge data` (docs/data/README.md "Migrations"): the migrations and the schema of a database, done by the program itself - its classes and
    /// [Migration]s run in the VM with a driver that calls Data.Tools.Run of the package data (the top level of the program does not run, as with
    /// `forge test`).
    ///
    ///   forge data migrate [program] --url URL [--to N]       the missing migrations (or up/down to N)
    ///   forge data status  [program] --url URL                which migrations ran
    ///   forge data diff    [program] --url URL [--title T]    the migration that the database lacks of the mapping of the [Table] classes
    ///   forge data import  --url URL [--namespace NS]         classes for the tables of a database
    /// The program: files, a project or a solution (-p), or the project in the current folder. --user, --password (or FIRE_DATA_PASSWORD), -o file.
    /// </summary>
    public static class DataCommand
    {
        public const string Usage = """
            forge data migrate|status|diff|import [<file>... | <project.fireproj> | <solution.firesln>] --url <url> [options]

            migrate  runs the migrations ([Migration n, "title"] classes) that have not run on the database, each in a transaction; --to N goes up or
                     down (their Down methods) to the migration N (0: before the first).
            status   lists the migrations of the program and whether they ran.
            diff     writes the migration that makes the database what the mapping of the [Table] classes says (new tables, columns, indexes, join
                     tables; what only the database has is named in comments). The migrations of the program must have run first.
            import   writes classes ([Table], [Key], [Required], [Length], [Unique]) for the tables of the database - no program needed.

            --url      the database (sqlite:app.db, mysql://host/db, sqlserver://host/db?trust=true, memory:)
            --user     the user; --password (or the environment variable FIRE_DATA_PASSWORD)
            --to N     with migrate: the migration to go to
            --title T  with diff: the title of the new migration
            --namespace NS  with import: the namespace of the classes
            -o file    with diff and import: the file to write (else the output)
            -p name    the project of a solution
            """;

        public static int Run(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr)
        {
            if (args.Count == 0 || args[0] is "-h" or "--help" or "help") { stdout.WriteLine(Usage); return CommandLineRunner.ExitOk; }
            string command = args[0].ToLowerInvariant();
            if (command is not ("migrate" or "status" or "diff" or "import")) return Fail(stderr, $"Unknown data command '{args[0]}' (expected: migrate, status, diff or import).");
            string? url = null, user = null, password = Environment.GetEnvironmentVariable("FIRE_DATA_PASSWORD"), to = null, title = null, ns = null, output = null, project = null;
            var files = new List<string>();
            for (int i = 1; i < args.Count; i++)
            {
                string a = args[i];
                string? Value() => i + 1 < args.Count ? args[++i] : null;
                switch (a)
                {
                    case "--url": url = Value(); break;
                    case "--user": user = Value(); break;
                    case "--password": password = Value(); break;
                    case "--to": to = Value(); break;
                    case "--title": title = Value(); break;
                    case "--namespace": ns = Value(); break;
                    case "-o": case "--output": output = Value(); break;
                    case "-p": case "--project": project = Value(); break;
                    default:
                        if (a.StartsWith("-")) return Fail(stderr, $"Unknown option '{a}' of forge data.");
                        files.Add(a);
                        break;
                }
            }
            if (string.IsNullOrWhiteSpace(url)) return Fail(stderr, "forge data needs the database: --url <url>.");
            if (to != null && command != "migrate") return Fail(stderr, "--to is only available with migrate.");
            string? package = DriverPackage(url);
            if (package == null) return Fail(stderr, $"forge data: no driver for the URL '{url}' (sqlite:, mysql://, sqlserver://, memory:).");

            // the program: its sources (for import none is needed)
            var sources = new List<string>();
            var paths = new List<string?>();
            if (files.Count == 0 && command != "import")
            {
                string here = Directory.GetCurrentDirectory();
                var found = Directory.GetFiles(here, "*" + fire.Projects.FireSolution.Extension);
                if (found.Length == 0) found = Directory.GetFiles(here, "*" + fire.Projects.FireProject.Extension);
                if (found.Length != 1) return Fail(stderr, found.Length == 0 ? "No program was named and there is no solution or project in this folder." : "There are several solutions or projects in this folder: name one.");
                files.Add(found[0]);
            }
            foreach (var f in files) if (!File.Exists(f)) return Fail(stderr, $"File not found: {f}");
            if (files.Count == 1 && (files[0].EndsWith(fire.Projects.FireProject.Extension, StringComparison.OrdinalIgnoreCase) || files[0].EndsWith(fire.Projects.FireSolution.Extension, StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var workspace = fire.Projects.Workspace.Open(files[0]);
                    var selected = ProjectBuilder.SelectProject(workspace, project);
                    var plan = ProjectBuilder.PlanOrThrow(workspace, selected);
                    sources.AddRange(plan.SourceTexts);
                    paths.AddRange(plan.SourcePaths);
                }
                catch (fire.Projects.ProjectException ex) { return Fail(stderr, ex.Message); }
            }
            else
                foreach (var f in files) { sources.Add(File.ReadAllText(f)); paths.Add(Path.GetFullPath(f)); }
            // (the packages the tools need: data and the driver of the URL)
            sources.Add($"#import \"data\"\n#import \"{package}\"\n");
            paths.Add(null);

            string arg = command switch { "migrate" => to ?? "", "diff" => title ?? "", "import" => ns ?? "", _ => "" };
            string driver = $"print(Data.Tools.Run({Lit(command)}, {Lit(url)}, {Lit(user ?? "")}, {Lit(password ?? "")}, {Lit(arg)}, \"\"))\n";
            var printed = new StringBuilder();
            RuntimeSession session;
            Linker.TestDriver = (program, first) => driver;
            try
            {
                session = RuntimeSession.Build(sources, VmExecutionMode.Release, values =>
                {
                    if (values.Length > 0) printed.Append(values[0].ToString()).Append('\n');
                    return Value.MakeUndefined();
                }, sourcePaths: paths);
            }
            catch (Exception ex) when (CommandLineRunner.IsCompileError(ex)) { return Fail(stderr, CommandLineRunner.DescribeErrors(ex)); }
            finally { Linker.TestDriver = null; }
            session.Run();
            if (session.VirtualMachine!.UnhandledException is { } error)
            {
                stderr.Write(printed.ToString());
                stderr.WriteLine(new UncaughtScriptException(error).Message);
                return CommandLineRunner.ExitScriptError;
            }
            string text = printed.ToString();
            if (output != null && command is "diff" or "import")
            {
                File.WriteAllText(output, text);
                stdout.WriteLine($"written: {output}");
            }
            else stdout.Write(text);
            return CommandLineRunner.ExitOk;
        }

        /// <summary>The package of the driver for the scheme of the URL.</summary>
        public static string? DriverPackage(string url)
        {
            int colon = url.IndexOf(':');
            if (colon <= 0) return null;
            return url.Substring(0, colon).ToLowerInvariant() switch
            {
                "sqlite" => "data.sqlite",
                "mysql" => "data.mysql",
                "sqlserver" => "data.sqlserver",
                "memory" => "data.memory",
                _ => null,
            };
        }

        private static string Lit(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "") + "\"";

        private static int Fail(TextWriter stderr, string message)
        {
            stderr.WriteLine(message);
            return CommandLineRunner.ExitUsage;
        }
    }
}
