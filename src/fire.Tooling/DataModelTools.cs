using System.Text;
using fire.UI.Markup;

namespace fire.Tooling
{
    /// <summary>
    /// What the designer of data models (spark, docs/data/designer.md) asks of fire: the DDL of a model for a database without a connection, a database
    /// as a model, the tables of a model made in a database, and the migration that a database lacks. Each runs fire code in the VM (the package data
    /// and its drivers) - through forge (M3): `forge run` of a script made here, `forge data import|diff`.
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

        /// <summary>The package of the driver of a database URL (null: none).</summary>
        public static string? DriverPackage(string url)
        {
            int colon = url.IndexOf(':');
            if (colon <= 0) return null;
            return url.Substring(0, colon).ToLowerInvariant() switch
            {
                "sqlite" => "data.sqlite",
                "mysql" => "data.mysql",
                "sqlserver" => "data.sqlserver",
                "postgres" or "postgresql" => "data.postgres",
                "memory" => "data.memory",
                _ => null,
            };
        }

        /// <summary>Runs a script with `forge run` in the folder of <paramref name="path"/> (the model): (its output, the error - null when it ended well).</summary>
        private static (string Output, string? Error) RunScript(string script, string? path)
        {
            string dir = path != null && Path.GetDirectoryName(Path.GetFullPath(path)) is { } d && Directory.Exists(d) ? d : Path.GetTempPath();
            string file = Path.Combine(dir, $".fire-data-{Environment.ProcessId}-{Guid.NewGuid():N}.fire");
            try
            {
                File.WriteAllText(file, script, new UTF8Encoding(false));
                var (code, output, errors) = Forge.Run(new[] { "run", file }, dir, timeoutMs: 300000);
                return (output, code == 0 ? null : (errors.Trim().Length > 0 ? errors.Trim() : $"forge ended with exit code {code}.").Replace(Path.GetFileName(file), "model"));
            }
            finally { try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
        }

        /// <summary>`forge data ...` with the password in the environment: (its output, the error).</summary>
        private static (string Output, string? Error) RunData(List<string> args, string password, string? workingDirectory = null)
        {
            var env = new Dictionary<string, string> { ["FIRE_DATA_PASSWORD"] = password };
            var (code, output, errors) = Forge.Run(args, workingDirectory, timeoutMs: 300000, environment: env);
            return (output, code == 0 ? null : errors.Trim().Length > 0 ? errors.Trim() : $"forge data ended with exit code {code}.");
        }

        /// <summary>The SQL that makes the tables of the model in an empty database of the dialect (index into <see cref="Dialects"/>): (the text, or the error).</summary>
        public static (string Text, string? Error) Ddl(string modelText, string path, int dialect)
        {
            string code;
            try { code = DataModelGenerator.Generate(modelText, path); }
            catch (MarkupException ex) { return ("", string.Join("\n", ex.Diagnostics.Select(d => d.ToString()))); }
            var (name, package, cls) = Dialects[dialect];
            string ns = DataModelDocument.Parse(modelText).Namespace;
            var result = RunScript($"#import \"{package}\"\n" + code + "\n" + $"print(Data.Script.Of(new {cls}(), {ns}.Model.Types()))\n", path);
            return (result.Output.TrimEnd('\n') + "\n", result.Error);
        }

        /// <summary>A database as the text of a data model (.fdata): its tables as entities, the relations guessed from their columns.</summary>
        public static (string Text, string? Error) Import(string url, string user, string password, string ns)
        {
            var args = new List<string> { "data", "import", "--url", url, "--format", "fdata" };
            if (user.Length > 0) { args.Add("--user"); args.Add(user); }
            if (ns.Length > 0) { args.Add("--namespace"); args.Add(ns); }
            var result = RunData(args, password);
            return (result.Output.TrimEnd('\n') + "\n", result.Error);
        }

        /// <summary>Makes the tables of the model in the database (Ensure of every entity: what is missing - nothing is dropped); what was done.</summary>
        public static (string Text, string? Error) CreateDatabase(string modelText, string path, string url, string user, string password)
        {
            string code;
            try { code = DataModelGenerator.Generate(modelText, path); }
            catch (MarkupException ex) { return ("", string.Join("\n", ex.Diagnostics.Select(d => d.ToString()))); }
            string? package = DriverPackage(url);
            if (package == null) return ("", $"No driver for the URL '{url}' (sqlite:, mysql://, sqlserver://, postgres://, memory:).");
            string ns = DataModelDocument.Parse(modelText).Namespace;
            string cred = user == "" ? "undefined" : $"new Data.Credentials({Lit(user)}, {Lit(password)})";
            string driver = $"var db = new Data.Connection({Lit(url)}, {cred})\nvar ctx = new {ns}.Context(db)\nctx.Ensure()\nprint(\"tables: \" + db.Schema().Count)\ndb.Close()\n";
            return RunScript($"#import \"{package}\"\n" + code + "\n" + driver, path);
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
            string folder;
            string? temp = null;
            string program;
            if (project != null)
            {
                program = project;
                folder = Path.Combine(Path.GetDirectoryName(project)!, "Migrations");
            }
            else
            {
                // (the model alone: its classes as a file of their own next to it)
                string code;
                try { code = DataModelGenerator.Generate(File.ReadAllText(modelPath), modelPath); }
                catch (MarkupException ex) { return ("", string.Join("\n", ex.Diagnostics.Select(d => d.ToString())), null); }
                string dir = Path.GetDirectoryName(Path.GetFullPath(modelPath))!;
                temp = Path.Combine(dir, $".fire-data-{Environment.ProcessId}-{Guid.NewGuid():N}.fire");
                File.WriteAllText(temp, code, new UTF8Encoding(false));
                program = temp;
                folder = Path.Combine(dir, "Migrations");
            }
            string text;
            string? error;
            try
            {
                var args = new List<string> { "data", "diff", program, "--url", url };
                if (user.Length > 0) { args.Add("--user"); args.Add(user); }
                if (title.Length > 0) { args.Add("--title"); args.Add(title); }
                (text, error) = RunData(args, password, Path.GetDirectoryName(Path.GetFullPath(program)));
            }
            finally { if (temp != null) try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
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
