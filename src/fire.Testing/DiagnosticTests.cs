using System.Text;
using fire.Compiler;
using fire.Runtime;
using fire.Values;

/// <summary>Errors with codes and warnings (`warn 123: "text"`): docs/tools/errors.md, SPEC 1.7.</summary>
static class DiagnosticTests
{
    private static int _failures;

    private static void Check(string title, bool ok, string? detail = null)
    {
        if (!ok) _failures++;
        Console.WriteLine(ok ? $"OK: Diagnose: {title}" : $"FEHLER: Diagnose: {title}" + (detail == null ? "" : $"\n{detail}"));
    }

    /// <summary>Compiles and runs <paramref name="source"/>; the warnings and what the program printed. An error of the compile is returned as its diagnostics.</summary>
    private static (IReadOnlyList<Diagnostic> Diagnostics, string Output) Compile(string source)
    {
        var collector = new DiagnosticCollector();
        using var collecting = collector.Begin();
        var lines = new List<string>();
        try
        {
            var session = RuntimeSession.Build(new[] { source }, VmExecutionMode.Release, a => { lines.Add(a[0].ToString()!); return Value.MakeUndefined(); });
            session.Run();
            Value.SingleFloats = false; Value.Ints32 = false; Value.Ints16 = false;
        }
        catch (Exception ex)
        {
            return (CompileDiagnostics.Of(ex).Concat(collector.Items).Where(d => d.Code is not ("TC004" or "TC005")).ToList(), "");
        }
        return (collector.Items.Where(d => d.Code is not ("TC004" or "TC005")).ToList(), string.Join("\n", lines));
    }

    public static void Run()
    {
        Console.WriteLine("=== Fehlercodes und Warnungen ===");

        // ---- warnings ----
        {
            var (diagnostics, output) = Compile("""
                warn 7: "the top level"
                class Box {
                    warn 8: "Box is obsolete"
                    int size
                    construct() { this.size = 3 }
                    Grow() {
                        warn 9: "Grow is obsolete: use Resize"
                        this.size = this.size + 1
                        return this.size
                    }
                }
                var b = new Box()
                for (var i = 0; i < 3; i++) {
                    warn 10: "in a loop"
                    b.Grow()
                }
                if (b.size > 2) warn 11: "as the body of an if"
                print("size " + b.size)
                """);
            Check("warn ID: \"Text\": ausserhalb von Klassen eine Warnung an ihrer Stelle, in Klassen und Methoden an der Stelle, an der sie benutzt werden (Code W<ID>); das Programm laeuft unveraendert",
                output == "size 6" && diagnostics.Count == 5 && diagnostics.All(d => d.IsWarning)
                && string.Join(",", diagnostics.Select(d => d.Code + "@" + d.Line)) == "W7@1,W10@14,W11@17,W8@12,W9@15"
                && diagnostics.First(d => d.Code == "W9").Message == "Grow is obsolete: use Resize (Box.Grow)" && diagnostics.First(d => d.Code == "W8").Message == "Box is obsolete (Box)", string.Join("\n", diagnostics.Select(d => d.ToLogLine())) + "\n" + output);
            Check("eine Warnung nennt sich so im Log: Zeile, Art, Code, Text", diagnostics[0].ToLogLine() == "warning W7: the top level (line 1)" && (diagnostics[0] with { File = "main.fire" }).ToLogLine() == "main.fire(1): warning W7: the top level", diagnostics[0].ToLogLine());

            // declared warnings: only where the class or the member is used
            const string library = """
                class Cache {
                    warn 300: "Cache is going away"
                    int value
                    construct() { this.value = 0 }
                    construct(int start) {
                        warn 301: "the constructor with a start value is obsolete"
                        this.value = start
                    }
                    Flush() { warn 302: "Flush is obsolete, use Clear()"; return this.value }
                    Clear() { return this.Flush() }
                    int Size { get { warn 303: "Size is obsolete"; return this.value } }
                    static Make() { warn 304: "Make is obsolete"; return 1 }
                    static Plain() { return 2 }
                    Other(int a) { warn 305: "Other with one argument"; return a }
                }
                class Unrelated {
                    Flush() { return 5 }
                    Other(int a, int b) { return a + b }
                }
                """;
            var (unused, _) = Compile(library + "\nvar x = 1");
            Check("eine Warnung in einer Klasse oder Methode, die nicht benutzt wird, wird nicht gemeldet", unused.Count == 0, string.Join("\n", unused.Select(d => d.ToLogLine())));
            var (used, _) = Compile(library + "\nvar c = new Cache()\nvar d = new Cache(5)\nc.Flush()\nprint(c.Size)\nCache.Make()\nCache.Plain()\nc.Other(1)");
            Check("benutzt: new (Klasse, Konstruktor mit passender Argumentzahl), Methode, Property, statischer Zugriff (Klasse, Methode) - jede Warnung an ihrer Stelle, nicht benutzte (Plain hat keine eigene) nicht",
                string.Join(",", used.Select(x => x.Code + "@" + x.Line)) == "W300@20,W300@21,W301@21,W302@22,W303@23,W300@24,W304@24,W300@25,W305@26", string.Join("\n", used.Select(x => x.ToLogLine())));
            var (inside, _) = Compile(library + "\nvar u = new Unrelated()\nu.Flush()\nu.Other(1, 2)");
            Check("die Klasse eines Objekts, das mit new angelegt wurde, ist bekannt: Unrelated.Flush meldet nicht die Warnung von Cache.Flush", inside.Count == 0, string.Join("\n", inside.Select(x => x.ToLogLine())));
            var (unknownReceiver, _) = Compile(library + "\nclass Runner { static Go(any obj) { return obj.Flush() } }\nRunner.Go(new Unrelated())");
            Check("ein Objekt, dessen Klasse der Compiler nicht kennt: ein Aufruf gleichen Namens und gleicher Argumentzahl meldet die Warnung (mit dem Klassennamen)", unknownReceiver.Count == 1 && unknownReceiver[0].Code == "W302" && unknownReceiver[0].Message.EndsWith("(Cache.Flush)"), string.Join("\n", unknownReceiver.Select(x => x.ToLogLine())));

            var (names, namesOut) = Compile("var warn = 5\nprint(warn + 1)\nwarn = warn * 2\nprint(warn)");
            Check("warn bleibt ein Name, solange keine Zahl und kein ':' folgen", names.Count == 0 && namesOut == "6\n10", string.Join("\n", names.Select(d => d.ToLogLine())) + namesOut);

            var plain = new List<string>();
            RuntimeSession.Build(new[] { "warn 1: \"x\"\nprint(\"ran\")" }, VmExecutionMode.Release, a => { plain.Add(a[0].ToString()!); return Value.MakeUndefined(); }).Run();
            Check("ohne Sammler (kein Aufrufer, der sie will) wird eine Warnung ohne Folgen uebergangen", plain.Count == 1 && plain[0] == "ran");

            var (bad, _) = Compile("warn 5\nprint(1)");
            Check("warn ohne ':' und Text ist ein ganz normaler Fehler (ein Ausdruck, dem das Zeilenende fehlt)", bad.Count == 1 && bad[0].IsError, string.Join("\n", bad.Select(d => d.ToLogLine())));
            var (noText, _) = Compile("warn 5: 7\nprint(1)");
            Check("warn <ID>: braucht den Text als Zeichenkette (Fehler E3157)", noText.Count == 1 && noText[0].Code == "E3157", string.Join("\n", noText.Select(d => d.ToLogLine())));
            var (bigId, _) = Compile("warn 123456: \"x\"");
            Check("die ID einer Warnung ist eine Zahl von 0 bis 99999 (Fehler E3158)", bigId.Count == 1 && bigId[0].Code == "E3158", string.Join("\n", bigId.Select(d => d.ToLogLine())));
        }

        // ---- codes of errors ----
        {
            var cases = new (string Source, string Code, string Text)[]
            {
                ("var s = \"abc", ErrorCatalog.CodeOf(ErrorArea.Lexer, "String literals must not span multiple lines"), "String literals must not span multiple lines"),
                ("print(unknownName)", ErrorCatalog.CodeOf(ErrorArea.Resolver, "Unknown identifier 'unknownName'"), "Unknown identifier"),
                ("print(1", ErrorCatalog.CodeOf(ErrorArea.Parser, "Expected ')' after the argument list"), "Expected ')' after the argument list"),
                ("#endif", ErrorCatalog.CodeOf(ErrorArea.Preprocessor, "'#endif' without '#if' (line 1)."), "'#endif' without '#if'"),
                ("var x = new Nothing()", ErrorCatalog.CodeOf(ErrorArea.Resolver, "Unknown class 'Nothing'"), "Unknown class"),
            };
            foreach (var (source, code, text) in cases)
            {
                var (diagnostics, _) = Compile(source);
                Check($"Fehler '{text}' hat den Code {code}", diagnostics.Count >= 1 && diagnostics[0].Code == code && diagnostics[0].IsError && code.Length == 5 && code[0] == 'E' && !code.EndsWith("000") && diagnostics[0].Message.Contains(text.Split('\'')[0].Trim()),
                    string.Join("\n", diagnostics.Select(d => d.ToLogLine())));
            }
            var (unknown, _) = Compile("print(unknownName)");
            Check("die Zeile eines Fehlers ist die Zeile des Quelltexts, der Text ohne Zeilenangabe", unknown.Count == 1 && unknown[0].Line == 1 && unknown[0].Message == "Unknown identifier 'unknownName'" && unknown[0].ToLogLine() == "error " + unknown[0].Code + ": Unknown identifier 'unknownName' (line 1)", unknown[0].ToLogLine());

            // the catalogue: unique codes, the codes of the areas, every message finds its own code
            var all = ErrorCatalog.All().ToList();
            Check("der Katalog: jeder Code kommt einmal vor und hat die Form E<Bereich><Nummer>", all.Select(e => e.Code).Distinct().Count() == all.Count && all.All(e => System.Text.RegularExpressions.Regex.IsMatch(e.Code, "^E[1-7][0-9]{3}$")), $"{all.Count} Eintraege");
            Check("der Katalog: viele Meldungen (Lexer, Praeprozessor, Parser, Resolver, Compiler)", all.Count > 250 && Enum.GetValues<ErrorArea>().All(a => all.Any(e => e.Area == a)), all.Count.ToString());
            var shadowed = new List<string>();
            foreach (var entry in all.Where(e => !e.Code.EndsWith("000") && e.Area != ErrorArea.Native))
            {
                string sample = entry.Template.Replace("{*}", "x");
                string found = ErrorCatalog.CodeOf(entry.Area, sample);
                if (found != entry.Code) shadowed.Add($"{entry.Code} '{sample}' -> {found}");
            }
            // (a message that another entry matches as well and that is more specific: it gets that code - the catalogue does not hide one by accident)
            Check("der Katalog: eine Beispielmeldung zu jedem Eintrag bekommt wieder seinen Code (oder den eines genaueren Eintrags desselben Bereichs)", shadowed.Count <= 6, string.Join("\n", shadowed));
            Check("eine Meldung, die der Katalog nicht kennt, hat den allgemeinen Code ihres Bereichs", ErrorCatalog.CodeOf(ErrorArea.Parser, "a message that nobody wrote") == "E3000" && ErrorCatalog.CodeOf(ErrorArea.Resolver, "?") == "E4000" && ErrorCatalog.CodeOf(ErrorArea.Lexer, "?") == "E1000");
            Check("Describe nennt den Text zu einem Code", ErrorCatalog.Describe("E1003") == "Unterminated string literal" && ErrorCatalog.Describe("E1004") == "String literals must not span multiple lines" && ErrorCatalog.Describe("W1") == null);
        }

        // ---- named arguments: the syntax (docs/language/08-classes.md) ----
        {
            const string decl = "class C { static int F(int a, int b = 2) { return a * 10 + b } }\n";
            var (twice, _) = Compile(decl + "print(C.F(a: 1, a: 2))");
            Check("Benannte Argumente: ein doppelter Name ist ein Syntaxfehler", twice.Count == 1 && twice[0].Message.Contains("named twice") && twice[0].Line == 2, string.Join("\n", twice.Select(d => d.ToLogLine())));
            var (order, _) = Compile(decl + "print(C.F(b: 1, 2))");
            Check("Benannte Argumente: ein positionales Argument nach einem benannten ist ein Syntaxfehler", order.Count == 1 && order[0].Message.Contains("positional argument cannot follow"), string.Join("\n", order.Select(d => d.ToLogLine())));
            var (units, unitsOut) = Compile("var a = 2km\nclass U { static string S(any v) { return \"\" + v } }\nprint(U.S(a:m))\nprint(U.S(v: a))");
            Check("Benannte Argumente: `a:m` ohne Leerzeichen bleibt die Umrechnung in eine Einheit", units.Count == 0 && unitsOut == "2000m\n2km", string.Join("\n", units.Select(d => d.ToLogLine())) + unitsOut);
            var (lambda, _) = Compile("var f = (int x) => x\nprint(f(x: 1))");
            Check("Benannte Argumente: beim Aufruf eines Lambdas nicht moeglich", lambda.Count >= 1 && lambda.Any(d => d.Message.Contains("not possible for a lambda")), string.Join("\n", lambda.Select(d => d.ToLogLine())));
        }

        // ---- a member of a class that is not there is found when the program is compiled ----
        {
            var (misspelled, _) = Compile("#import \"time\"\nvar t = DateTime.Nowx");
            Check("ClassName.Member: ein Name, den die Klasse nicht hat, ist ein Uebersetzungsfehler (E4056) mit Zeile", misspelled.Count == 1 && misspelled[0].Code == "E4056" && misspelled[0].Line == 2 && misspelled[0].Message == "Class 'DateTime' has no static member 'Nowx'", string.Join("\n", misspelled.Select(d => d.ToLogLine())));
            var (instance, _) = Compile("class Box {\n int size\n Grow() { return 1 }\n}\nvar x = Box.Grow()");
            Check("ClassName.Member: ein Instanzmitglied braucht ein Objekt (E4057)", instance.Count == 1 && instance[0].Code == "E4057", string.Join("\n", instance.Select(d => d.ToLogLine())));
            var (fine, fineOut) = Compile("class Base { static int Count = 3\n static Twice(int x) { return x * 2 } }\nclass Derived : Base { }\nenum Kind { A, B }\nprint(Derived.Count + \" \" + Base.Twice(4) + \" \" + Kind.B)");
            Check("ClassName.Member: statische Felder, Methoden (auch geerbte) und Enum-Werte bleiben erlaubt", fine.Count == 0 && fineOut == "3 8 B", string.Join("\n", fine.Select(d => d.ToLogLine())) + fineOut);
            var (clock, clockOut) = Compile("#import \"time\"\nprint((DateTime.Now.Year > 2000) + \" \" + DateTime.UtcNow.Kind + \" \" + DateTime.Now.Kind + \" \" + (DateTime.Today.Hour == 0))");
            Check("DateTime.Now, UtcNow und Today sind Eigenschaften (wie in .NET)", clock.Count == 0 && clockOut == "True utc local True", string.Join("\n", clock.Select(d => d.ToLogLine())) + clockOut);
        }

        // ---- the arguments of the program (Environment.Args): after `--` on the command line, else settings.arguments of the project ----
        {
            string dir = Path.Combine(Path.GetTempPath(), "fire-args-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                const string program = "var a = Environment.Args\nif (a.length == 2 && a[0] == \"x\" && a[1] == \"b c\" && Environment.ProgramPath.EndsWith(\"main.fire\")) { terminate(7) }\nterminate(1)\n";
                string file = Path.Combine(dir, "main.fire");
                File.WriteAllText(file, program);
                var outW = new StringWriter(); var errW = new StringWriter();
                int withArgs = CommandLineRunner.Run(new[] { "run", file, "--", "x", "b c" }, outW, errW);
                int without = CommandLineRunner.Run(new[] { "run", file }, outW, errW);
                Check("Befehlszeile: was nach -- steht, bekommt das Programm (Environment.Args), ProgramPath ist die Datei", withArgs == 7 && without == 1, $"{withArgs} {without} {errW}");
                File.WriteAllText(Path.Combine(dir, "App.fireproj"), "{ \"format\": 1, \"name\": \"App\", \"type\": \"exe\", \"settings\": { \"arguments\": [\"x\", \"b c\"] } }");
                int fromProject = CommandLineRunner.Run(new[] { "run", Path.Combine(dir, "App.fireproj") }, outW, errW);
                int replaced = CommandLineRunner.Run(new[] { "run", Path.Combine(dir, "App.fireproj"), "--", "other" }, outW, errW);
                Check("Befehlszeile: ein Projekt gibt settings.arguments mit, -- ersetzt sie", fromProject == 7 && replaced == 1, $"{fromProject} {replaced} {errW}");
                var buildArgs = CommandLineParser.Parse(new[] { "build", file, "--", "x" });
                Check("Befehlszeile: Argumente nach -- gibt es nur bei run", buildArgs.Error?.Contains("only available with run") == true, buildArgs.Error);
            }
            finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
        }

        // ---- a data model (.fdata): its mistakes with their lines; in a project it is compiled without #include ----
        {
            const string bad = "{\n  \"entities\": [\n    { \"name\": \"Person\",\n      \"fields\": [\n        { \"name\": \"name\", \"type\": \"text\" },\n        { \"name\": \"class\", \"type\": \"int\" }\n      ] },\n    { \"name\": \"Person\", \"fields\": [ { \"name\": \"id\", \"type\": \"int\", \"key\": true } ] },\n    { \"name\": \"Order\", \"fields\": [ { \"name\": \"id\", \"type\": \"float\", \"key\": true } ] }\n  ],\n  \"relations\": [\n    { \"kind\": \"one-to-two\", \"from\": \"Person\", \"to\": \"Order\" },\n    { \"kind\": \"one-to-many\", \"from\": \"Person\", \"to\": \"Nobody\", \"navigation\": \"x\" }\n  ]\n}\n";
            string found = "";
            try { fire.UI.Markup.MarkupFiles.GenerateScript("Bad.fdata", bad); }
            catch (fire.UI.Markup.MarkupException ex) { found = string.Join("\n", ex.Diagnostics.Select(d => d.ToString())); }
            Check("Datenmodell: die Fehler mit ihren Zeilen (doppelt, unbekannter Typ, Schluesselwort, kein Schluessel, falscher Schluesseltyp, unbekannte Art und Entitaet einer Beziehung)",
                found.Contains("line 8: 'Person' is declared twice.") && found.Contains("line 5: The field 'Person.name' has the type 'text'") && found.Contains("line 6: The field 'class' of 'Person' needs a name")
                && found.Contains("line 3: The entity 'Person' has no key") && found.Contains("line 9: The key 'Order.id' is an int or a string") && found.Contains("line 12: The kind of a relation is one-to-one, one-to-many or many-to-many")
                && found.Contains("line 13: The relation of 'Person' and 'Nobody' needs two entities"), found);
            string dir = Path.Combine(Path.GetTempPath(), "fire-fdata-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "App.fireproj"), "{ \"format\": 1, \"name\": \"App\", \"type\": \"exe\" }");
                File.WriteAllText(Path.Combine(dir, "Shop.fdata"), "{ \"namespace\": \"Shop\", \"naming\": { \"tables\": \"snake_plural\" }, \"entities\": [ { \"name\": \"OrderLine\", \"fields\": [ { \"name\": \"id\", \"type\": \"int\", \"key\": true }, { \"name\": \"text\", \"type\": \"string\" } ] } ] }");
                File.WriteAllText(Path.Combine(dir, "main.fire"), "#import \"data.memory\"\nvar db = new Data.Connection(\"memory:\")\nvar ctx = new Shop.Context(db)\nctx.Ensure()\nvar l = new Shop.OrderLine()\nl.text = \"x\"\ndb.Insert(l)\nif (Shop.Model.Tables()[0] == \"order_lines\" && ctx.OrderLines.Count() == 1) { terminate(7) }\nterminate(1)\n");
                var outW = new StringWriter(); var errW = new StringWriter();
                int code = CommandLineRunner.Run(new[] { "run", Path.Combine(dir, "App.fireproj") }, outW, errW);
                Check("Datenmodell: ein .fdata im Projekt gehoert ohne #include dazu (Model, Context, Tabellenname snake_plural)", code == 7, $"{code} {errW}");
            }
            finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
        }

        // ---- the data designer (spark): the model edited, its DDL, a database made from it and read back as a model, the migration it lacks ----
        {
            var m = new fire.UI.Markup.DataModelDocument { Namespace = "Shop", TableNaming = "snake_plural" };
            var customer = m.AddEntity("Customer", 40, 40);
            var name = m.AddField(customer.Fields); name.Name = "fullName"; name.Length = 100; name.Required = true;
            var order = m.AddEntity("Order", 320, 40);
            var total = m.AddField(order.Fields, "float"); total.Name = "total";
            var level = m.AddEnum("Level"); m.AddField(customer.Fields, "Level").Name = "level";
            m.AddRelation("one-to-many", "Customer", "Order");
            bool renamed = m.Rename("Order", "Purchase") && !m.Rename("Customer", "Level");
            var back = fire.UI.Markup.DataModelDocument.Parse(m.ToJson());
            Check("Designer: Bearbeiten (Entitaet, Feld, Enum, Beziehung, Umbenennen mit Beziehung und Platz) und die Datei wieder lesen",
                renamed && back.ToJson() == m.ToJson() && back.Relations[0].To == "Purchase" && back.Relations[0].Navigation == "purchases" && back.Relations[0].Inverse == "customer"
                && back.Places.ContainsKey("Purchase") && back.Problems().Count == 0 && back.GeneratedCode()?.Contains("class Purchase") == true, m.ToJson());
            var (ddl, ddlError) = DataModelTools.Ddl(m.ToJson(), "Shop.fdata", 0);
            var (msDdl, msError) = DataModelTools.Ddl(m.ToJson(), "Shop.fdata", 2);
            Check("Designer: die DDL des Modells fuer SQLite und SQL Server (ohne Datenbank)",
                ddlError == null && ddl.Contains("CREATE TABLE") && ddl.Contains("\"purchases\"") && ddl.Contains("\"customerId\" INTEGER") && msError == null && msDdl.Contains("[customers]"), ddl + ddlError + msDdl + msError);
            string dir = Path.Combine(Path.GetTempPath(), "fire-designer-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string url = "sqlite:" + Path.Combine(dir, "app.db");
                var (made, madeError) = DataModelTools.CreateDatabase(m.ToJson(), "Shop.fdata", url, "", "");
                var (imported, importError) = DataModelTools.Import(url, "", "", "Back");
                var model = importError == null ? fire.UI.Markup.DataModelDocument.Parse(imported) : new fire.UI.Markup.DataModelDocument();
                Check("Designer: die Datenbank aus dem Modell machen und wieder als Modell lesen (Entitaeten, one-to-many aus customer_id)",
                    madeError == null && made.Contains("tables: 2") && importError == null && model.Namespace == "Back" && model.Entities.Count == 2
                    && model.Relations.Any(r => r.Kind == "one-to-many" && r.To.Contains("urchase")) && model.Problems().Count == 0, made + madeError + imported + importError);
                File.WriteAllText(Path.Combine(dir, "App.fireproj"), "{ \"format\": 1, \"name\": \"App\", \"type\": \"exe\" }");
                var later = fire.UI.Markup.DataModelDocument.Parse(m.ToJson());
                var email = later.AddField(later.Entity("Customer")!.Fields); email.Name = "email"; email.Length = 200;
                File.WriteAllText(Path.Combine(dir, "Shop.fdata"), later.ToJson());
                File.WriteAllText(Path.Combine(dir, "main.fire"), "#import \"data.sqlite\"\nprint(\"app\")\n");
                string? project = DataModelTools.ProjectOf(Path.Combine(dir, "Shop.fdata"));
                var (text, error, written) = DataModelTools.WriteMigration(project, Path.Combine(dir, "Shop.fdata"), url, "", "", "email");
                Check("Designer: die Migration, die der Datenbank fehlt, ins Projekt schreiben (Migrations/MigrationN.fire)",
                    project != null && error == null && written != null && File.Exists(written) && Path.GetFileName(Path.GetDirectoryName(written)) == "Migrations" && text.Contains("email") && text.Contains("AddColumn"), text + error + written);
            }
            finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
        }

        // ---- the data model with one-to-one, a view, checks and types per database; the designer's diagrams and its clipboard ----
        {
            const string model = "{ \"namespace\": \"Office\", \"entities\": [\n" +
                "  { \"name\": \"Person\", \"checks\": [\"length(name) > 0\", \"age < 200\"], \"fields\": [ { \"name\": \"id\", \"type\": \"int\", \"key\": true }, { \"name\": \"name\", \"type\": \"string\" }, { \"name\": \"age\", \"type\": \"int\", \"check\": \"age >= 0\" }, { \"name\": \"data\", \"type\": \"string\", \"dbType\": { \"postgres\": \"jsonb\", \"*\": \"TEXT\" } } ] },\n" +
                "  { \"name\": \"Elders\", \"view\": \"SELECT id, name FROM Person WHERE age >= 65\", \"fields\": [ { \"name\": \"id\", \"type\": \"int\" }, { \"name\": \"name\", \"type\": \"string\" } ] },\n" +
                "  { \"name\": \"Passport\", \"fields\": [ { \"name\": \"id\", \"type\": \"int\", \"key\": true }, { \"name\": \"number\", \"type\": \"string\" } ] } ],\n" +
                "  \"relations\": [ { \"kind\": \"one-to-one\", \"from\": \"Person\", \"to\": \"Passport\", \"navigation\": \"passport\", \"inverse\": \"holder\" } ],\n" +
                "  \"diagrams\": [ { \"name\": \"Main\", \"shapes\": { \"Person\": { \"x\": 40, \"y\": 40 } } }, { \"name\": \"Travel\", \"shapes\": { \"Passport\": { \"x\": 300, \"y\": 60 } } } ] }\n";
            string code = "";
            try { code = fire.UI.Markup.DataModelGenerator.Generate(model, "Office.fdata"); }
            catch (fire.UI.Markup.MarkupException ex) { code = string.Join("\n", ex.Diagnostics.Select(d => d.ToString())); }
            Check("Datenmodell: one-to-one ([HasOne] und [BelongsTo] auf einem eindeutigen Fremdschluessel), View, Checks von Spalte und Tabelle, dbType je Datenbank; Views zuletzt in Model.Types",
                code.Contains("[HasOne \"personId\"] Passport passport") && code.Contains("[Unique] int personId") && code.Contains("[BelongsTo \"personId\"] Person holder")
                && code.Contains("[View \"SELECT id, name FROM Person WHERE age >= 65\"]") && code.Contains("[Check \"(length(name) > 0) AND (age < 200)\"]")
                && code.Contains("[Check \"age >= 0\"]") && code.Contains("[DbType \"postgres: jsonb; *: TEXT\"]") && code.Contains("[\"Office.Person\", \"Office.Passport\", \"Office.Elders\"]"), code);
            var (pgDdl, pgError) = DataModelTools.Ddl(model, "Office.fdata", DataModelTools.Dialects.ToList().FindIndex(d => d.Name == "PostgreSQL"));
            Check("Datenmodell: die DDL fuer PostgreSQL mit jsonb, CHECK, der View nach den Tabellen",
                pgError == null && pgDdl.Contains("\"data\" jsonb") && pgDdl.Contains("CHECK (age >= 0)") && pgDdl.Contains("CHECK ((length(name) > 0) AND (age < 200))")
                && pgDdl.IndexOf("CREATE VIEW \"Elders\"") > pgDdl.IndexOf("CREATE TABLE \"Passport\""), pgDdl + pgError);
            var doc = fire.UI.Markup.DataModelDocument.Parse(model);
            var again = fire.UI.Markup.DataModelDocument.Parse(doc.ToJson());
            bool kept = again.ToJson() == doc.ToJson() && again.Diagrams.Count == 2 && again.Diagrams[1].Name == "Travel" && again.Shows(1, "Passport") && !again.Shows(1, "Person") && again.Shows(0, "Elders")
                && again.Entity("Person")!.Checks.Count == 2 && again.Entity("Person")!.Fields[3].DbType == "postgres: jsonb; *: TEXT" && again.Entity("Elders")!.View != null && again.Relations[0].Kind == "one-to-one";
            string copied = doc.CopyText("Person")!;
            var pasted = doc.Paste(copied, 500, 40, 1);
            bool clip = pasted.Count == 1 && pasted[0] == "Person2" && doc.Entity("Person2")!.Fields.Count == 4 && doc.Shows(1, "Person2") && doc.Places.ContainsKey("Person2") && doc.Relations.Count == 1;
            doc.Rename("Passport", "Pass");
            doc.Remove("Person2");
            bool renamed = doc.Diagrams[1].Shapes.ContainsKey("Pass") && !doc.Diagrams[1].Shapes.ContainsKey("Person2") && doc.Relations[0].To == "Pass" && doc.Relations[0].Navigation == "pass" && doc.Problems().Count == 0;
            Check("Designer: mehrere Diagramme (Main zeigt alles, die anderen ihre Kaesten), Kopieren und Einfuegen mit freiem Namen, Umbenennen und Loeschen in allen Diagrammen",
                kept && clip && renamed, $"{kept} {clip} {renamed}\n{doc.ToJson()}");
        }

        // ---- forge data: the migrations of a program on a database (SQLite in a file) - the program's classes run, its top level does not ----
        {
            string dir = Path.Combine(Path.GetTempPath(), "fire-data-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string prog = Path.Combine(dir, "shop.fire");
                File.WriteAllText(prog, "#import \"data.sqlite\"\n[Migration 1, \"people\"]\nclass M1 {\n    undefined Up(Data.Migrator m) { m.CreateTable(\"people\", [m.Key(\"id\"), m.Column(\"name\", \"string\", 100, true)]) }\n    undefined Down(Data.Migrator m) { m.DropTable(\"people\") }\n}\n[Table \"people\"]\nclass Person {\n    [Key] int id\n    [Required] [Length 100] string name\n    [Unique] [Length 200] string email\n    construct() { }\n}\nprint(\"the top level\")\n");
                string url = "sqlite:" + Path.Combine(dir, "app.db");
                (int Code, string Out, string Err) Data(params string[] a)
                {
                    var o = new StringWriter(); var e = new StringWriter();
                    int code = CommandLineRunner.Run(new[] { "data" }.Concat(a).ToArray(), o, e);
                    return (code, o.ToString(), e.ToString());
                }
                var status = Data("status", prog, "--url", url);
                var early = Data("diff", prog, "--url", url);
                var migrate = Data("migrate", prog, "--url", url);
                Check("forge data: status, diff vor den Migrationen abgelehnt, migrate (das Programm selbst laeuft nicht)", status.Code == 0 && status.Out.Contains("1  pending  people  (M1)")
                    && early.Code == 1 && early.Err.Contains("have not run on this database") && migrate.Code == 0 && migrate.Out.Contains("migrations run: 1, the database is at 1") && !migrate.Out.Contains("top level"),
                    status.Out + status.Err + early.Err + migrate.Out + migrate.Err);
                string m2 = Path.Combine(dir, "m2.fire");
                var diff = Data("diff", prog, "--url", url, "--title", "email", "-o", m2);
                string written = File.Exists(m2) ? File.ReadAllText(m2) : "";
                Check("forge data diff: die fehlende Migration als fire-Code (neue Spalte, eindeutiger Index, Down rueckwaerts)", diff.Code == 0 && written.Contains("[Migration 2, \"email\"]")
                    && written.Contains("m.AddColumn(\"people\", m.Column(\"email\", \"string\", 200))") && written.Contains("m.CreateIndex(\"ix_people_email\", \"people\", \"email\", true)")
                    && written.IndexOf("m.DropIndex", StringComparison.Ordinal) < written.IndexOf("m.DropColumn", StringComparison.Ordinal), written + diff.Err);
                File.AppendAllText(prog, written);
                var again = Data("migrate", prog, "--url", url);
                var nothing = Data("diff", prog, "--url", url);
                var import = Data("import", "--url", url, "--namespace", "Shop");
                var down = Data("migrate", prog, "--url", url, "--to", "0");
                var after = Data("status", prog, "--url", url);
                var bogus = Data("bogus", "--url", url);
                Check("forge data: die neue Migration laeuft, danach kein Unterschied; import gibt Klassen; --to 0 nimmt alles zurueck; ein falscher Befehl ist Exitcode 2",
                    again.Out.Contains("the database is at 2") && nothing.Out.Contains("has everything") && import.Out.Contains("namespace Shop {") && import.Out.Contains("[Table \"people\"]")
                    && import.Out.Contains("[Length 200] [Unique] string email") && down.Out.Contains("migrations run: 2, the database is at 0") && after.Out.Contains("2  pending  email")
                    && bogus.Code == CommandLineRunner.ExitUsage, again.Out + again.Err + nothing.Out + import.Out + import.Err + down.Out + down.Err + after.Out);
            }
            finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
        }

        // ---- the command line prints them ----
        {
            string dir = Path.Combine(Path.GetTempPath(), "fire-diag-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string warnFile = Path.Combine(dir, "w.fire");
                File.WriteAllText(warnFile, "print(\"hi\")\nwarn 42: \"check this\"\nprint(\"there\")\n");
                var outW = new StringWriter(); var errW = new StringWriter();
                int code = CommandLineRunner.Run(new[] { "run", warnFile }, outW, errW);
                Check("Befehlszeile: eine Warnung steht auf stderr (mit Datei, Zeile und Code), das Programm laeuft und endet mit 0", code == 0 && errW.ToString().Contains("w.fire(2): warning W42: check this") && !outW.ToString().Contains("W42"), errW.ToString());   // (the file of the warning and its line)
                string errFile = Path.Combine(dir, "e.fire");
                File.WriteAllText(errFile, "print(nothing)\n");
                var outE = new StringWriter(); var errE = new StringWriter();
                int codeE = CommandLineRunner.Run(new[] { "run", errFile }, outE, errE);
                Check("Befehlszeile: ein Fehler steht auf stderr mit Code und Zeile, Exitcode 1", codeE == CommandLineRunner.ExitScriptError && System.Text.RegularExpressions.Regex.IsMatch(errE.ToString(), @"error E4\d{3}: Unknown identifier 'nothing' \(line 1\)"), errE.ToString());
                string lexFile = Path.Combine(dir, "l.fire");
                File.WriteAllText(lexFile, "var s = \"open\n");
                var errL = new StringWriter();
                int codeL = CommandLineRunner.Run(new[] { "run", lexFile }, new StringWriter(), errL);
                Check("Befehlszeile: auch ein Fehler im Text des Programms (Lexer) ist eine Meldung mit Code, kein Absturz", codeL == CommandLineRunner.ExitScriptError && errL.ToString().Contains("error E1"), errL.ToString());
            }
            finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
        }

        // ---- Ruhrpott-Raser: a keyword where a name is expected, static initialisers in order, a temporary kept by a constructor, lines going on ----
        {
            foreach (var (src, word, code) in new[] { ("int where = 3\nprint(where)", "where", "E3057"), ("class A {\n    static int F(int from) { return from }\n}", "from", "E3145"),
                                                       ("var base = 1", "base", "E3055"), ("class A {\n    int under\n}", "under", "E3137") })
            {
                var (kw, _) = Compile(src);
                Check($"Schluesselwort als Name: die Meldung nennt '{word}' und behaelt ihren Code", kw.Count == 1 && kw[0].Code == code && kw[0].Message.Contains($"'{word}' is a keyword and cannot be a name"), string.Join("\n", kw.Select(d => d.ToLogLine())));
            }
            var (later, _) = Compile("class A {\n    static int x = B.y + 1\n}\nclass B {\n    static int y = 5\n}\nprint(A.x)");
            Check("Statische Initialisierer: ein Feld, dessen Initialisierer spaeter laeuft, ist ein Fehler (E4074)", later.Count == 1 && later[0].Code == "E4074" && later[0].Line == 2 && later[0].Message.Contains("'B.y'"), string.Join("\n", later.Select(d => d.ToLogLine())));
            var (self, _) = Compile("class A {\n    static int x = x + 1\n}\nprint(A.x)");
            Check("Statische Initialisierer: das Feld selbst ist ein Fehler (E4073)", self.Count == 1 && self[0].Code == "E4073", string.Join("\n", self.Select(d => d.ToLogLine())));
            var (inOrder, inOrderOut) = Compile("class A {\n    static int a = 2\n    static int b = A.a * 3\n    static any f = () => B.late\n    static int c = A.Twice()\n    static int Twice() { return A.a * 2 }\n}\nclass B {\n    static int late = A.b + 1\n}\nany g = A.f\nprint(A.b + \" \" + A.c + \" \" + B.late + \" \" + g())");
            Check("Statische Initialisierer: frueher gesetzte Felder, Methoden und Lambdas bleiben erlaubt", inOrder.Count == 0 && inOrderOut == "6 4 7 7", string.Join("\n", inOrder.Select(d => d.ToLogLine())) + inOrderOut);
            const string classes = "class Sample {\n    int v = 7\n    static Sample Make() { return new Sample() }\n}\nclass Holder {\n    Sample s\n    construct(Sample s) { this.s = s }\n}\nclass Keeper {\n    Sample s\n    construct(Sample s) { this.s = try take s }\n}\n";
            var (temp, _) = Compile(classes + "var h = new Holder(Sample.Make())\nvar h2 = new Holder(new Sample())\nprint(1)");
            Check("TC031: ein Temporaerwert, den der Konstruktor ohne take in einem Feld behaelt (new und Aufruf, der ein neues Objekt gibt)",
                temp.Count == 2 && temp.All(d => d.Code == "TC031" && !d.IsError) && temp[0].Line == 13 && temp[0].Message.Contains("'Sample.Make(...)'") && temp[1].Message.Contains("'new Sample(...)'") && temp[0].Message.Contains("this.s = try take s"),
                string.Join("\n", temp.Select(d => d.ToLogLine())));
            var (kept, keptOut) = Compile(classes + "var k = new Keeper(new Sample())\nvar mine = new Sample()\nvar h = new Holder(mine)\nprint(k.s.v + h.s.v)");
            Check("TC031: kein Hinweis, wenn der Konstruktor nimmt oder das Argument eine Variable ist", kept.Count == 0 && keptOut == "14", string.Join("\n", kept.Select(d => d.ToLogLine())) + keptOut);
            var (lines, linesOut) = Compile("var list = new List<int>([5, 3, 8, 1])\nlist.\n    Sort()\nvar n = list\n    .Count\nvar m = list?.Count\n    + 1\n    - 2\nbool big = n\n    > 3\n    && n < 10\n    || false\nint x = 1\nx++\nprint(n + \" \" + m + \" \" + big + \" \" + x + \" \" + list[0])");
            var (toBuffer, toBufferOut) = Compile("class B {\n    static undefined Fill(byte b[]) { b[0] = 7 }\n}\nint ints[2] = [1, 2]\nB.Fill(ints)\nB.Fill([3, 4])\nbyte buf[2]\nB.Fill(buf)\nprint(ints[0] + \" \" + buf[0])");
            Check("TC032: ein Array einer Variablen an einen byte-Parameter wird ein neuer Puffer (Warnung), ein Literal und ein Puffer nicht", toBuffer.Count == 1 && toBuffer[0].Code == "TC032" && !toBuffer[0].IsError && toBuffer[0].Line == 5 && toBufferOut == "1 7", string.Join("\n", toBuffer.Select(d => d.ToLogLine())) + toBufferOut);
            var (withLines, withOut) = Compile("class Point {\n    int x\n    int y\n}\nvar p = new Point()\nwith p {\n    .x = 10\n    .y = 20\n}\nwith p {\n    print(.x + .y)\n}");
            Check("Zeilen: im with-Block beginnt ein '.' am Zeilenanfang eine neue Anweisung (.x = 10 / .y = 20)", withLines.Count == 0 && withOut == "30", string.Join("\n", withLines.Select(d => d.ToLogLine())) + withOut);
            var (shadow, shadowOut) = Compile("class Info { static string Name = \"info\" }\nclass T {\n    undefined Info() { }\n    static string Of() { return Info.Name }\n}\nprint(T.Of())");
            Check("Namen: in einer statischen Methode verdeckt eine Instanzmethode gleichen Namens die Klasse nicht (Environment.Platform in ProcessTests)", shadow.Count == 0 && shadowOut == "info", string.Join("\n", shadow.Select(d => d.ToLogLine())) + shadowOut);
            Check("Zeilen: '.' am Zeilenende und '.', '?.', Operatoren, && und || am Zeilenanfang setzen fort; ++ beginnt eine neue Anweisung", lines.Count == 0 && linesOut == "4 3 True 2 1", string.Join("\n", lines.Select(d => d.ToLogLine())) + linesOut);
        }

        // ---- a native build reports them in its result ----
        {
            var config = new fire.Native.NativeConfig();
            var target = TargetProfile.Host;
            var toolchain = config.ResolveToolchain(target);
            string tmp = Path.Combine(Path.GetTempPath(), "fire-diag-" + Guid.NewGuid().ToString("N") + ".out");
            var failed = NativeBuilder.BuildSafe(new[] { "warn 3: \"before the error\"\nprint(nothing)" }, config, target, toolchain, tmp);
            Check("nativer Build: die Fehler und Warnungen des Compilers sind im Ergebnis (mit Codes), der Log nennt den Fehler",
                !failed.Ok && failed.Diagnostics is { Count: 2 } && failed.Diagnostics.Count(d => d.IsError && d.Code.StartsWith("E4")) == 1 && failed.Diagnostics.Count(d => d.Code == "W3") == 1 && failed.Log.Contains("Unknown identifier"),
                string.Join("\n", failed.Diagnostics?.Select(d => d.ToLogLine()) ?? Array.Empty<string>()) + "\n" + failed.Log);
            // (every built-in target has fire threads now - over fire's own scheduler where the system has none: a target without them, made for the test)
            var noThreads = TargetProfile.Wasm with { Native = TargetProfile.Wasm.Native with { SupportsThreads = false } };
            var unsupported = NativeBuilder.BuildSafe(new[] { "fire { print(1) }" }, config, noThreads, config.ResolveToolchain(noThreads), tmp);
            Check("nativer Build: etwas, das das Ziel nicht kann (Threads auf einem Ziel ohne), ist ein Fehler E7001", !unsupported.Ok && unsupported.Diagnostics is { Count: 1 } && unsupported.Diagnostics[0].Code == ErrorCatalog.NativeUnsupported, string.Join("\n", unsupported.Diagnostics?.Select(d => d.ToLogLine()) ?? Array.Empty<string>()));
        }

        Console.WriteLine(_failures == 0 ? "Alle Diagnose-Pruefungen bestanden." : $"FEHLER: {_failures} Diagnose-Pruefung(en) fehlgeschlagen.");
    }
}
