using fire.UI.Markup;

namespace fire.Compiler
{
    /// <summary>
    /// What the designer of data models (spark, docs/data/designer.md) asks of fire: the DDL of a model for a database without a connection, a database
    /// as a model, the tables of a model made in a database, and the migration that a database lacks. Each runs fire code in the VM (the package data
    /// and its drivers) - the same code as `forge data` and a build.
    /// </summary>
    public static class DataModelTools
    {
        /// <summary>The databases the DDL can be shown for: their name and the dialect class of their driver.</summary>
        public static readonly IReadOnlyList<(string Name, string Package, string Dialect)> Dialects = new[]
        {
            ("SQLite", "data.sqlite", "Data.SqliteDialect"),
            ("MySQL / MariaDB", "data.mysql", "Data.MysqlDialect"),
            ("SQL Server", "data.sqlserver", "Data.SqlServerDialect"),
            ("PostgreSQL", "data.postgres", "Data.PostgresDialect"),
            ("In-memory", "data.memory", "Data.MemoryDialect"),
        };

        /// <summary>The SQL that makes the tables of the model in an empty database of the dialect (index into <see cref="Dialects"/>): (the text, or the error).</summary>
        public static (string Text, string? Error) Ddl(string modelText, string path, int dialect)
        {
            string code;
            try { code = DataModelGenerator.Generate(modelText, path); }
            catch (MarkupException ex) { return ("", string.Join("\n", ex.Diagnostics.Select(d => d.ToString()))); }
            var (name, package, cls) = Dialects[dialect];
            string ns = DataModelDocument.Parse(modelText).Namespace;
            var sources = new List<string> { code, $"#import \"{package}\"\n" };
            var result = DataCommand.RunDriver(sources, new string?[] { path, null }, $"print(Data.Script.Of(new {cls}(), {ns}.Model.Types()))\n");
            return (result.Output.TrimEnd('\n') + "\n", result.Error);
        }

        /// <summary>A database as the text of a data model (.fdata): its tables as entities, the relations guessed from their columns.</summary>
        public static (string Text, string? Error) Import(string url, string user, string password, string ns)
        {
            var result = DataCommand.Execute("import", Array.Empty<string>(), Array.Empty<string?>(), url, user, password, ns, "fdata");
            return (result.Output.TrimEnd('\n') + "\n", result.Error);
        }

        /// <summary>Makes the tables of the model in the database (Ensure of every entity: what is missing - nothing is dropped); what was done.</summary>
        public static (string Text, string? Error) CreateDatabase(string modelText, string path, string url, string user, string password)
        {
            string code;
            try { code = DataModelGenerator.Generate(modelText, path); }
            catch (MarkupException ex) { return ("", string.Join("\n", ex.Diagnostics.Select(d => d.ToString()))); }
            string? package = DataCommand.DriverPackage(url);
            if (package == null) return ("", $"No driver for the URL '{url}' (sqlite:, mysql://, sqlserver://, postgres://, memory:).");
            string ns = DataModelDocument.Parse(modelText).Namespace;
            string cred = user == "" ? "undefined" : $"new Data.Credentials({Lit(user)}, {Lit(password)})";
            string driver = $"var db = new Data.Connection({Lit(url)}, {cred})\nvar ctx = new {ns}.Context(db)\nctx.Ensure()\nprint(\"tables: \" + db.Schema().Count)\ndb.Close()\n";
            var result = DataCommand.RunDriver(new List<string> { code, $"#import \"{package}\"\n" }, new string?[] { path, null }, driver);
            return (result.Output, result.Error);
        }

        /// <summary>The migration that the database lacks of the [Table] classes of the program (the sources of its project, the model among them).</summary>
        public static (string Text, string? Error) Diff(IReadOnlyList<string> sources, IReadOnlyList<string?> paths, string url, string user, string password, string title)
        {
            var result = DataCommand.Execute("diff", sources, paths, url, user, password, title, "");
            return (result.Output, result.Error);
        }

        /// <summary>The project the model belongs to: the nearest .fireproj in its folder or above; null: none.</summary>
        public static string? ProjectOf(string? path)
        {
            if (path == null) return null;
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            while (dir != null)
            {
                var found = Directory.GetFiles(dir, "*" + fire.Projects.FireProject.Extension);
                if (found.Length == 1) return found[0];
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        /// <summary>The migration a database lacks of the project (or the model alone), written into the folder Migrations next to the project:
        /// (the output, the error, the file written - null when the database has everything).</summary>
        public static (string Text, string? Error, string? Written) WriteMigration(string? project, string modelPath, string url, string user, string password, string title)
        {
            var sources = new List<string>();
            var paths = new List<string?>();
            string folder;
            if (project != null)
            {
                try
                {
                    var workspace = fire.Projects.Workspace.Open(project);
                    var plan = ProjectBuilder.PlanOrThrow(workspace, ProjectBuilder.SelectProject(workspace, null));
                    sources.AddRange(plan.SourceTexts);
                    paths.AddRange(plan.SourcePaths);
                }
                catch (fire.Projects.ProjectException ex) { return ("", ex.Message, null); }
                folder = Path.Combine(Path.GetDirectoryName(project)!, "Migrations");
            }
            else
            {
                sources.Add(DataModelGenerator.Generate(File.ReadAllText(modelPath), modelPath));
                paths.Add(modelPath);
                folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(modelPath))!, "Migrations");
            }
            var (text, error) = DataModelTools.Diff(sources, paths, url, user, password, title);
            if (error != null) return (text, error, null);
            int at = text.IndexOf("[Migration ", StringComparison.Ordinal);
            if (at < 0) return (text, null, null);
            string number = new string(text.Substring(at + 11).TakeWhile(char.IsDigit).ToArray());
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, "Migration" + number + ".fire");
            File.WriteAllText(file, text);
            return (text, null, file);
        }
        private static string Lit(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
