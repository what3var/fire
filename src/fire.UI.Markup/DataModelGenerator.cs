using System.Globalization;
using System.Text;
using System.Text.Json;

namespace fire.UI.Markup
{
    /// <summary>
    /// Translates a data model (`.fdata`: JSON, docs/data/designer.md) into fire: the classes of its entities with the attributes of the mapping of the
    /// package `data` ([Table], [Key], [Column], [Required], [Length], [Unique], [Index], [Version], [Json], [HasMany], [BelongsTo], [ManyToMany]), its
    /// enums and records, `Model` (the types and tables) and `Context` (the database through the model: `ctx.People` is `db.From&lt;Person&gt;()`,
    /// `Session`, `Ensure`, `Migrate`, `Script` - the DDL of the connection's dialect). The file is generated, not edited: own methods go into a class
    /// of the program that derives from an entity, or into helpers.
    /// </summary>
    public static class DataModelGenerator
    {
        public const int CurrentFormat = 1;

        private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
        {
            "class", "var", "int", "float", "bool", "string", "char", "any", "undefined", "true", "false", "if", "else", "while", "for", "foreach", "in", "of", "from", "is",
            "return", "new", "delete", "namespace", "using", "func", "on", "off", "copy", "take", "try", "catch", "finally", "throw", "break", "continue", "switch", "case",
            "default", "static", "construct", "destruct", "fire", "sync", "unit", "enum", "interface", "extern", "native", "byte", "word", "do", "readonly", "flat", "override",
            "record", "actor", "this", "base", "where", "to", "lambda", "await", "process", "taking", "handover", "defined", "trust", "terminate", "leave", "print",
        };
        // the scalar types of a field and their kind in the mapping
        private static readonly HashSet<string> Scalars = new(StringComparer.Ordinal) { "int", "float", "bool", "string", "char", "bytes", "DateTime" };

        private sealed record Field(string Name, string Type, string? Column, bool Key, bool Generated, bool Required, int Length, bool Unique, bool Index, bool Version, bool Json, string? Comment, int Line, string? Check = null, string? DbType = null);
        private sealed record Entity(string Name, string Table, bool Record, string? Set, string? Comment, List<Field> Fields, int Line, string? View = null, List<string>? Checks = null);
        private sealed record TypeDecl(string Name, List<Field> Fields, string? Comment, int Line);
        private sealed record EnumDecl(string Name, List<string> Values, string? Comment, int Line);
        private sealed record Relation(string Kind, string From, string To, string? Key, string? Navigation, string? Inverse, string? JoinTable, string? FromColumn, string? ToColumn, int Line);

        /// <summary>The fire source of the model; a <see cref="MarkupException"/> with every mistake found.</summary>
        public static string Generate(string text, string path)
        {
            var errors = new List<MarkupDiagnostic>();
            JsonDocument doc;
            try { doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
            catch (JsonException ex) { throw new MarkupException(new[] { new MarkupDiagnostic((int)(ex.LineNumber ?? -1) + 1, "The file is no valid JSON: " + ex.Message.Split(" Path:")[0]) }); }
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw new MarkupException(new[] { new MarkupDiagnostic(1, "A .fdata file is a JSON object.") });
                var lines = LineIndex(text);
                int format = root.TryGetProperty("format", out var f) && f.TryGetInt32(out int fv) ? fv : CurrentFormat;
                if (format > CurrentFormat) errors.Add(new MarkupDiagnostic(1, $"The format {format} is newer than this forge knows ({CurrentFormat})."));
                string ns = Str(root, "namespace") ?? Path.GetFileNameWithoutExtension(path);
                if (!IsName(ns.Replace(".", ""))) errors.Add(new MarkupDiagnostic(1, $"'{ns}' is no name for a namespace."));
                string tableNaming = "name", columnNaming = "field";
                if (root.TryGetProperty("naming", out var naming) && naming.ValueKind == JsonValueKind.Object)
                {
                    tableNaming = Str(naming, "tables") ?? "name";
                    columnNaming = Str(naming, "columns") ?? "field";
                    if (tableNaming is not ("name" or "plural" or "snake" or "snake_plural")) errors.Add(new MarkupDiagnostic(1, $"naming.tables is name, plural, snake or snake_plural, not '{tableNaming}'."));
                    if (columnNaming is not ("field" or "snake")) errors.Add(new MarkupDiagnostic(1, $"naming.columns is field or snake, not '{columnNaming}'."));
                }

                var enums = new List<EnumDecl>();
                foreach (var (e, line, _) in Items(root, "enums", lines))
                {
                    string name = Str(e, "name") ?? "";
                    var values = e.TryGetProperty("values", out var vs) && vs.ValueKind == JsonValueKind.Array ? vs.EnumerateArray().Select(v => v.GetString() ?? "").ToList() : new List<string>();
                    if (!IsName(name)) errors.Add(new MarkupDiagnostic(line, $"The enum '{name}' needs a name that fire allows."));
                    if (values.Count == 0) errors.Add(new MarkupDiagnostic(line, $"The enum '{name}' has no values."));
                    foreach (var v in values) if (!IsName(v)) errors.Add(new MarkupDiagnostic(line, $"'{v}' is no name for a value of the enum '{name}'."));
                    enums.Add(new EnumDecl(name, values, Str(e, "comment"), line));
                }
                var types = new List<TypeDecl>();
                foreach (var (t, line, tp) in Items(root, "types", lines))
                    types.Add(new TypeDecl(Str(t, "name") ?? "", ReadFields(t, lines, tp), Str(t, "comment"), line));
                var entities = new List<Entity>();
                foreach (var (e, line, ep) in Items(root, "entities", lines))
                {
                    string name = Str(e, "name") ?? "";
                    string table = Str(e, "table") ?? TableName(name, tableNaming);
                    var checks = e.TryGetProperty("checks", out var cs) && cs.ValueKind == JsonValueKind.Array ? cs.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString()!).Where(c => c.Trim() != "").ToList() : new List<string>();
                    string? view = Str(e, "view");
                    if (view != null && view.Trim() == "") view = null;
                    entities.Add(new Entity(name, table, Bool(e, "record"), Str(e, "set"), Str(e, "comment"), ReadFields(e, lines, ep), line, view, checks));
                }
                var relations = new List<Relation>();
                foreach (var (r, line, _) in Items(root, "relations", lines))
                    relations.Add(new Relation(Str(r, "kind") ?? "", Str(r, "from") ?? Str(r, "one") ?? "", Str(r, "to") ?? Str(r, "many") ?? "", Str(r, "foreignKey"), Str(r, "navigation"), Str(r, "inverse"),
                        Str(r, "joinTable"), Str(r, "fromColumn"), Str(r, "toColumn"), line));

                // ---- the checks ----
                var typeNames = new HashSet<string>(StringComparer.Ordinal);
                void Declare(string name, int line, string what)
                {
                    if (!IsName(name)) errors.Add(new MarkupDiagnostic(line, $"The {what} '{name}' needs a name that fire allows (letters, digits, _; not a keyword)."));
                    else if (!typeNames.Add(name)) errors.Add(new MarkupDiagnostic(line, $"'{name}' is declared twice."));
                }
                foreach (var e in enums) Declare(e.Name, e.Line, "enum");
                foreach (var t in types) Declare(t.Name, t.Line, "type");
                foreach (var e in entities) Declare(e.Name, e.Line, "entity");
                var enumNames = enums.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
                var recordNames = types.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
                var entityNames = entities.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
                void CheckFields(string owner, List<Field> fields, bool needsKey, int line)
                {
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var fd in fields)
                    {
                        if (!IsName(fd.Name)) errors.Add(new MarkupDiagnostic(fd.Line, $"The field '{fd.Name}' of '{owner}' needs a name that fire allows."));
                        else if (!seen.Add(fd.Name)) errors.Add(new MarkupDiagnostic(fd.Line, $"'{owner}' has the field '{fd.Name}' twice."));
                        bool known = Scalars.Contains(fd.Type) || enumNames.Contains(fd.Type) || recordNames.Contains(fd.Type);
                        if (!known) errors.Add(new MarkupDiagnostic(fd.Line, $"The field '{owner}.{fd.Name}' has the type '{fd.Type}' - known are int, float, bool, string, char, bytes, DateTime, the enums and the types of the model."));
                        if (recordNames.Contains(fd.Type) && !fd.Json) errors.Add(new MarkupDiagnostic(fd.Line, $"The field '{owner}.{fd.Name}' of the type '{fd.Type}' is stored as JSON: give it \"json\": true."));
                        if (fd.Key && !(fd.Type is "int" or "string")) errors.Add(new MarkupDiagnostic(fd.Line, $"The key '{owner}.{fd.Name}' is an int or a string, not '{fd.Type}'."));
                        if (fd.Version && fd.Type != "int") errors.Add(new MarkupDiagnostic(fd.Line, $"The version field '{owner}.{fd.Name}' is an int."));
                        if (fd.Length < 0) errors.Add(new MarkupDiagnostic(fd.Line, $"The length of '{owner}.{fd.Name}' is not negative."));
                    }
                    if (needsKey && fields.Count(fd => fd.Key) > 1) errors.Add(new MarkupDiagnostic(line, $"'{owner}' has more than one key (the mapping has one key field)."));
                    if (needsKey && !fields.Any(fd => fd.Key) && !fields.Any(fd => fd.Name == "id")) errors.Add(new MarkupDiagnostic(line, $"The entity '{owner}' has no key: a field with \"key\": true (or a field 'id')."));
                }
                foreach (var t in types) CheckFields(t.Name, t.Fields, false, t.Line);
                foreach (var e in entities) CheckFields(e.Name, e.Fields, e.View == null, e.Line);
                foreach (var e in entities)
                    if (e.View != null && (e.Checks?.Count > 0 || e.Fields.Any(fd => fd.Check != null))) errors.Add(new MarkupDiagnostic(e.Line, $"The view '{e.Name}' has no checks (its query decides its rows)."));
                var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in entities) if (!tables.Add(e.Table)) errors.Add(new MarkupDiagnostic(e.Line, $"Two entities have the table '{e.Table}'."));

                // ---- the relations: the fields they add to both sides ----
                // (an entity declared twice is reported above: its first declaration counts here)
                var extra = entities.GroupBy(e => e.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, _ => new List<string>(), StringComparer.Ordinal);
                var added = entities.GroupBy(e => e.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Fields.Select(fd => fd.Name).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
                void Add(string entity, string name, string line, int at)
                {
                    if (!added[entity].Add(name)) { errors.Add(new MarkupDiagnostic(at, $"'{entity}' has a field '{name}' already - give the relation other names.")); return; }
                    extra[entity].Add(line);
                }
                string KeyType(string entity)
                {
                    var e = entities.First(x => x.Name == entity);
                    var key = e.Fields.FirstOrDefault(fd => fd.Key) ?? e.Fields.FirstOrDefault(fd => fd.Name == "id");
                    return key?.Type ?? "int";
                }
                foreach (var r in relations)
                {
                    if (!entityNames.Contains(r.From) || !entityNames.Contains(r.To)) { errors.Add(new MarkupDiagnostic(r.Line, $"The relation of '{r.From}' and '{r.To}' needs two entities of the model.")); continue; }
                    if (r.Kind == "one-to-one")
                    {
                        // (one From has one To: the foreign key is a field of To, unique)
                        string fk = r.Key ?? Lower(r.From) + "Id";
                        if (!IsName(fk)) { errors.Add(new MarkupDiagnostic(r.Line, $"'{fk}' is no name for a foreign key.")); continue; }
                        var to = entities.First(x => x.Name == r.To);
                        if (!to.Fields.Any(fd => fd.Name == fk))
                        {
                            string column = columnNaming == "snake" && Snake(fk) != fk ? $"[Column \"{Snake(fk)}\"] " : "";
                            Add(r.To, fk, $"{column}[Unique] {KeyType(r.From)} {fk}", r.Line);
                        }
                        if (r.Navigation != null) Add(r.From, r.Navigation, $"[HasOne \"{fk}\"] {r.To} {r.Navigation}", r.Line);
                        if (r.Inverse != null) Add(r.To, r.Inverse, $"[BelongsTo \"{fk}\"] {r.From} {r.Inverse}", r.Line);
                        if (r.Navigation == null && r.Inverse == null) errors.Add(new MarkupDiagnostic(r.Line, $"The relation of '{r.From}' and '{r.To}' needs a navigation field on one side at least (\"navigation\" or \"inverse\")."));
                    }
                    else if (r.Kind == "one-to-many")
                    {
                        // (one From has many To: the foreign key is a field of To)
                        string fk = r.Key ?? Lower(r.From) + "Id";
                        if (!IsName(fk)) { errors.Add(new MarkupDiagnostic(r.Line, $"'{fk}' is no name for a foreign key.")); continue; }
                        var to = entities.First(x => x.Name == r.To);
                        if (!to.Fields.Any(fd => fd.Name == fk))
                        {
                            string column = columnNaming == "snake" && Snake(fk) != fk ? $"[Column \"{Snake(fk)}\"] " : "";
                            Add(r.To, fk, $"{column}[Index] {KeyType(r.From)} {fk}", r.Line);
                        }
                        if (r.Navigation != null) Add(r.From, r.Navigation, $"[HasMany \"{fk}\"] List<{r.To}> {r.Navigation}", r.Line);
                        if (r.Inverse != null) Add(r.To, r.Inverse, $"[BelongsTo \"{fk}\"] {r.From} {r.Inverse}", r.Line);
                        if (r.Navigation == null && r.Inverse == null) errors.Add(new MarkupDiagnostic(r.Line, $"The relation of '{r.From}' and '{r.To}' needs a navigation field on one side at least (\"navigation\" or \"inverse\")."));
                    }
                    else if (r.Kind == "many-to-many")
                    {
                        string join = r.JoinTable ?? Lower(r.From) + "_" + Lower(r.To);
                        string a = r.FromColumn ?? Lower(r.From) + "Id", b = r.ToColumn ?? Lower(r.To) + "Id";
                        if (r.Navigation == null) { errors.Add(new MarkupDiagnostic(r.Line, $"The many-to-many relation of '{r.From}' and '{r.To}' needs \"navigation\" (the list in '{r.From}').")); continue; }
                        Add(r.From, r.Navigation, $"[ManyToMany \"{join}\", \"{a}\", \"{b}\"] List<{r.To}> {r.Navigation}", r.Line);
                        if (r.Inverse != null) Add(r.To, r.Inverse, $"[ManyToMany \"{join}\", \"{b}\", \"{a}\"] List<{r.From}> {r.Inverse}", r.Line);
                    }
                    else errors.Add(new MarkupDiagnostic(r.Line, $"The kind of a relation is one-to-one, one-to-many or many-to-many, not '{r.Kind}'."));
                }
                if (errors.Count > 0) throw new MarkupException(errors);

                // ---- the source ----
                var sb = new StringBuilder();
                string file = Path.GetFileName(path);
                sb.Append("// Generated by forge from ").Append(file).Append(" (the data model, docs/data/designer.md) - not edited by hand.\n");
                sb.Append("#import \"data\"\n\n");
                sb.Append("namespace ").Append(ns).Append(" {\n");
                foreach (var e in enums)
                {
                    Comment(sb, e.Comment, "    ");
                    sb.Append("    enum ").Append(e.Name).Append(" { ").Append(string.Join(", ", e.Values)).Append(" }\n\n");
                }
                foreach (var t in types)
                {
                    Comment(sb, t.Comment, "    ");
                    sb.Append("    record ").Append(t.Name).Append(" {\n");
                    foreach (var fd in t.Fields) sb.Append("        ").Append(FieldLine(fd, columnNaming, isEntity: false)).Append('\n');
                    // (a record is written in its constructor: one parameter per field)
                    sb.Append("        construct(").Append(string.Join(", ", t.Fields.Select(fd => fd.Type == "bytes" ? "byte " + fd.Name + "[]" : fd.Type + " " + fd.Name))).Append(") {\n");
                    foreach (var fd in t.Fields) sb.Append("            this.").Append(fd.Name).Append(" = ").Append(fd.Name).Append('\n');
                    sb.Append("        }\n    }\n\n");
                }
                foreach (var e in entities)
                {
                    Comment(sb, e.Comment, "    ");
                    sb.Append("    [Table \"").Append(e.Table).Append("\"]\n");
                    if (e.View != null) sb.Append("    [View ").Append(Lit(e.View)).Append("]\n");
                    if (e.Checks?.Count > 0) sb.Append("    [Check ").Append(Lit(e.Checks.Count == 1 ? e.Checks[0] : string.Join(" AND ", e.Checks.Select(c => "(" + c + ")")))).Append("]\n");
                    // (a class writes itself: p.Save(db), p.Delete(db), p.Reload(db) - Data.Entity)
                    sb.Append(e.Record ? "    record " : "    class ").Append(e.Name).Append(e.Record ? " {\n" : " : Data.Entity {\n");
                    foreach (var fd in e.Fields)
                    {
                        if (fd.Comment != null) sb.Append("        // ").Append(fd.Comment.Replace("\n", " ")).Append('\n');
                        sb.Append("        ").Append(FieldLine(fd, columnNaming, isEntity: true)).Append('\n');
                    }
                    foreach (var line in extra[e.Name]) sb.Append("        ").Append(line).Append('\n');
                    sb.Append("        construct() { }\n    }\n\n");
                }
                // Model: what the model has
                sb.Append("    /// <summary>The data model ").Append(ns).Append(": its entities and their tables.</summary>\n");
                sb.Append("    class Model {\n");
                // (the views after the tables: their queries name them)
                var ordered = entities.Where(e => e.View == null).Concat(entities.Where(e => e.View != null)).ToList();
                sb.Append("        static any Types() { return [").Append(string.Join(", ", ordered.Select(e => "\"" + ns + "." + e.Name + "\""))).Append("] }\n");
                sb.Append("        static any Tables() { return [").Append(string.Join(", ", ordered.Select(e => "\"" + e.Table + "\""))).Append("] }\n");
                sb.Append("    }\n\n");
                // Context: the database through the model
                sb.Append("    /// <summary>A database through the model ").Append(ns).Append(": a query per entity, sessions, the schema.</summary>\n");
                sb.Append("    class Context {\n        Data.Connection db\n        construct(Data.Connection db) { this.db = db }\n");
                sb.Append("        Data.Connection Connection { get { return this.db } }\n");
                var sets = new HashSet<string>(StringComparer.Ordinal) { "Connection", "Session", "Ensure", "Migrate", "Script", "db" };
                foreach (var e in entities)
                {
                    string set = e.Set ?? Plural(e.Name);
                    if (!IsName(set) || !sets.Add(set)) throw new MarkupException(new[] { new MarkupDiagnostic(e.Line, $"The query of '{e.Name}' in the Context would be '{set}': give the entity another \"set\".") });
                    sb.Append("        Data.Query<").Append(e.Name).Append("> ").Append(set).Append(" { get { return this.db.From<").Append(e.Name).Append(">() } }\n");
                }
                sb.Append("        Data.Session Session() { return this.db.Session() }\n");
                sb.Append("        // (the tables of the model: made when they are not there, missing columns and indexes added - never dropped)\n");
                sb.Append("        undefined Ensure() {\n");
                foreach (var e in ordered) sb.Append("            this.db.Ensure(\"").Append(ns).Append('.').Append(e.Name).Append("\")\n");
                sb.Append("        }\n");
                sb.Append("        int Migrate() { return this.db.Migrate() }\n");
                sb.Append("        // (the SQL that makes the tables of the model in an empty database, in the dialect of the connection)\n");
                sb.Append("        string Script() { return this.db.CreateScriptOf(").Append(ns).Append(".Model.Types()) }\n    }\n}\n");
                return sb.ToString();
            }
        }

        private static string FieldLine(Field fd, string columnNaming, bool isEntity)
        {
            var sb = new StringBuilder();
            if (isEntity && fd.Key) sb.Append(fd.Generated || fd.Type != "int" ? "[Key] " : "[Key \"manual\"] ");
            string? column = fd.Column ?? (columnNaming == "snake" && Snake(fd.Name) != fd.Name ? Snake(fd.Name) : null);
            if (isEntity && column != null && column != fd.Name) sb.Append("[Column \"").Append(column).Append("\"] ");
            if (isEntity && fd.Required && !fd.Key) sb.Append("[Required] ");
            if (fd.Length > 0) sb.Append("[Length ").Append(fd.Length.ToString(CultureInfo.InvariantCulture)).Append("] ");
            if (isEntity && fd.Unique) sb.Append("[Unique] ");
            if (isEntity && fd.Index && !fd.Unique) sb.Append("[Index] ");
            if (isEntity && fd.Version) sb.Append("[Version] ");
            if (fd.Json) sb.Append("[Json] ");
            if (isEntity && fd.Check != null) sb.Append("[Check ").Append(Lit(fd.Check)).Append("] ");
            if (isEntity && fd.DbType != null) sb.Append("[DbType ").Append(Lit(fd.DbType)).Append("] ");
            if (fd.Type == "bytes") sb.Append("byte ").Append(fd.Name).Append("[]");
            else sb.Append(fd.Type).Append(' ').Append(fd.Name);
            return sb.ToString();
        }

        private static List<Field> ReadFields(JsonElement owner, Dictionary<string, int> lines, string ownerPath)
        {
            var list = new List<Field>();
            foreach (var (fd, line, _) in Items(owner, "fields", lines, ownerPath))
            {
                string name = Str(fd, "name") ?? "";
                string type = Str(fd, "type") ?? "string";
                int length = fd.TryGetProperty("length", out var l) && l.TryGetInt32(out int lv) ? lv : 0;
                bool key = Bool(fd, "key");
                bool generated = !fd.TryGetProperty("generated", out var g) || g.ValueKind != JsonValueKind.False;
                string? check = Str(fd, "check");
                if (check != null && check.Trim() == "") check = null;
                // ("dbType": "jsonb" for every database, or { "postgres": "jsonb", "mysql": "JSON", "*": "TEXT" } - [DbType "postgres: jsonb; mysql: JSON; *: TEXT"])
                string? dbType = null;
                if (fd.TryGetProperty("dbType", out var dt))
                {
                    if (dt.ValueKind == JsonValueKind.String && dt.GetString()!.Trim() != "") dbType = dt.GetString()!.Trim();
                    else if (dt.ValueKind == JsonValueKind.Object)
                    {
                        var parts = dt.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).Select(p => p.Name + ": " + p.Value.GetString()).ToList();
                        if (parts.Count > 0) dbType = string.Join("; ", parts);
                    }
                }
                list.Add(new Field(name, type, Str(fd, "column"), key, generated, Bool(fd, "required"), length, Bool(fd, "unique"), Bool(fd, "index"), Bool(fd, "version"), Bool(fd, "json"), Str(fd, "comment"), line, check, dbType));
            }
            return list;
        }

        // ---- helpers ----
        // (the objects of an array of the owner, with their lines and paths)
        private static IEnumerable<(JsonElement Item, int Line, string Path)> Items(JsonElement owner, string name, Dictionary<string, int> lines, string ownerPath = "")
        {
            if (!owner.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) yield break;
            int i = 0;
            foreach (var item in array.EnumerateArray())
            {
                string path = (ownerPath == "" ? name : ownerPath + "." + name) + "[" + i + "]";
                if (item.ValueKind == JsonValueKind.Object) yield return (item, lines.TryGetValue(path, out int line) ? line : 0, path);
                i++;
            }
        }
        // (a text as a fire string literal)
        private static string Lit(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";
        private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        // (the line of every object by its path - "entities[1].fields[0]": JsonElement knows no position, the reader does)
        private static Dictionary<string, int> LineIndex(string text)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            var starts = new List<long> { 0 };
            for (int i = 0; i < bytes.Length; i++) if (bytes[i] == (byte)'\n') starts.Add(i + 1);
            int LineAt(long offset)
            {
                int at = starts.BinarySearch(offset);
                return (at >= 0 ? at : ~at - 1) + 1;
            }
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var stack = new List<(string Path, bool Array, int Index)>();
            string prop = "";
            string ValuePath()
            {
                if (stack.Count == 0) return "";
                var top = stack[^1];
                if (top.Array) return top.Path + "[" + top.Index + "]";
                return top.Path == "" ? prop : top.Path + "." + prop;
            }
            void Advance()
            {
                if (stack.Count > 0 && stack[^1].Array) stack[^1] = (stack[^1].Path, true, stack[^1].Index + 1);
            }
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName: prop = reader.GetString() ?? ""; break;
                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                        string path = ValuePath();
                        if (reader.TokenType == JsonTokenType.StartObject) map[path] = LineAt(reader.TokenStartIndex);
                        stack.Add((path, reader.TokenType == JsonTokenType.StartArray, 0));
                        break;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        stack.RemoveAt(stack.Count - 1);
                        Advance();
                        break;
                    default: Advance(); break;
                }
            }
            return map;
        }

        private static bool IsName(string name) =>
            name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_') && name.All(c => char.IsLetterOrDigit(c) || c == '_') && !Keywords.Contains(name);
        private static string Lower(string name) => name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name.Substring(1);
        public static string Snake(string name)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (char.IsUpper(c) && i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]))) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }
        public static string Plural(string name)
        {
            if (name.EndsWith("y") && name.Length > 1 && !"aeiou".Contains(name[^2])) return name.Substring(0, name.Length - 1) + "ies";
            if (name.EndsWith("s") || name.EndsWith("x") || name.EndsWith("ch") || name.EndsWith("sh")) return name + "es";
            return name + "s";
        }
        private static string TableName(string entity, string naming) => naming switch
        {
            "plural" => Plural(entity),
            "snake" => Snake(entity),
            "snake_plural" => Snake(Plural(entity)),
            _ => entity,
        };
        private static void Comment(StringBuilder sb, string? comment, string indent)
        {
            if (string.IsNullOrWhiteSpace(comment)) return;
            sb.Append(indent).Append("/// <summary>").Append(comment.Replace("\n", " ").Replace("<", "&lt;").Replace(">", "&gt;")).Append("</summary>\n");
        }
    }
}
