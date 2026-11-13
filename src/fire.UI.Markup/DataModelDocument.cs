using System.Globalization;
using System.Text;
using System.Text.Json;

namespace fire.UI.Markup
{
    /// <summary>A field of an entity or a type of a data model (docs/data/designer.md).</summary>
    public sealed class DmField
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "string";
        public string? Column { get; set; }
        public bool Key { get; set; }
        public bool Generated { get; set; } = true;
        public bool Required { get; set; }
        public int Length { get; set; }
        public bool Unique { get; set; }
        public bool Index { get; set; }
        public bool Version { get; set; }
        public bool Json { get; set; }
        public string? Comment { get; set; }
        public DmField Clone() => (DmField)MemberwiseClone();
    }

    public sealed class DmEntity
    {
        public string Name { get; set; } = "";
        public string? Table { get; set; }
        public string? Comment { get; set; }
        public bool Record { get; set; }
        public string? Set { get; set; }
        public List<DmField> Fields { get; } = new();
    }

    public sealed class DmType
    {
        public string Name { get; set; } = "";
        public string? Comment { get; set; }
        public List<DmField> Fields { get; } = new();
    }

    public sealed class DmEnum
    {
        public string Name { get; set; } = "";
        public string? Comment { get; set; }
        public List<string> Values { get; } = new();
    }

    public sealed class DmRelation
    {
        /// <summary>one-to-many (one From has many To; the foreign key is a field of To) or many-to-many.</summary>
        public string Kind { get; set; } = "one-to-many";
        public string From { get; set; } = "";
        public string To { get; set; } = "";
        public string? ForeignKey { get; set; }
        public string? Navigation { get; set; }
        public string? Inverse { get; set; }
        public string? JoinTable { get; set; }
        public string? FromColumn { get; set; }
        public string? ToColumn { get; set; }
    }

    /// <summary>
    /// A data model as the designer of spark edits it (`.fdata`, docs/data/designer.md): read from its JSON, changed by the operations below, written
    /// back (<see cref="ToJson"/>) in a form that reads well in a diff - one line per field and per relation. The places of the boxes are those of the
    /// first diagram ("Main"). What the generator says of it (<see cref="Problems"/>) and the code it makes (<see cref="GeneratedCode"/>) come from
    /// <see cref="DataModelGenerator"/>, so the designer shows exactly what a build does.
    /// </summary>
    public sealed class DataModelDocument
    {
        public string Namespace { get; set; } = "Model";
        public string TableNaming { get; set; } = "name";
        public string ColumnNaming { get; set; } = "field";
        public List<DmEnum> Enums { get; } = new();
        public List<DmType> Types { get; } = new();
        public List<DmEntity> Entities { get; } = new();
        public List<DmRelation> Relations { get; } = new();
        /// <summary>The place of a box of the diagram (an entity, an enum, a type) by its name.</summary>
        public Dictionary<string, (double X, double Y)> Places { get; } = new(StringComparer.Ordinal);

        public static readonly string[] FieldTypes = { "int", "float", "bool", "string", "char", "bytes", "DateTime" };

        // ---- reading and writing ----

        /// <summary>The model of the text (an empty one for an empty text); a <see cref="MarkupException"/> when it is no JSON object.</summary>
        public static DataModelDocument Parse(string text)
        {
            var m = new DataModelDocument();
            if (string.IsNullOrWhiteSpace(text)) return m;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
            catch (JsonException ex) { throw new MarkupException(new[] { new MarkupDiagnostic((int)(ex.LineNumber ?? -1) + 1, "The file is no valid JSON: " + ex.Message.Split(" Path:")[0]) }); }
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw new MarkupException(new[] { new MarkupDiagnostic(1, "A .fdata file is a JSON object.") });
                m.Namespace = Str(root, "namespace") ?? "Model";
                if (root.TryGetProperty("naming", out var naming) && naming.ValueKind == JsonValueKind.Object)
                {
                    m.TableNaming = Str(naming, "tables") ?? "name";
                    m.ColumnNaming = Str(naming, "columns") ?? "field";
                }
                foreach (var e in Array(root, "enums"))
                {
                    var en = new DmEnum { Name = Str(e, "name") ?? "", Comment = Str(e, "comment") };
                    if (e.TryGetProperty("values", out var vs) && vs.ValueKind == JsonValueKind.Array) en.Values.AddRange(vs.EnumerateArray().Select(v => v.GetString() ?? ""));
                    m.Enums.Add(en);
                }
                foreach (var t in Array(root, "types"))
                {
                    var ty = new DmType { Name = Str(t, "name") ?? "", Comment = Str(t, "comment") };
                    ty.Fields.AddRange(Array(t, "fields").Select(ReadField));
                    m.Types.Add(ty);
                }
                foreach (var e in Array(root, "entities"))
                {
                    var en = new DmEntity { Name = Str(e, "name") ?? "", Table = Str(e, "table"), Comment = Str(e, "comment"), Record = Bool(e, "record"), Set = Str(e, "set") };
                    en.Fields.AddRange(Array(e, "fields").Select(ReadField));
                    m.Entities.Add(en);
                }
                foreach (var r in Array(root, "relations"))
                    m.Relations.Add(new DmRelation
                    {
                        Kind = Str(r, "kind") ?? "one-to-many", From = Str(r, "from") ?? Str(r, "one") ?? "", To = Str(r, "to") ?? Str(r, "many") ?? "",
                        ForeignKey = Str(r, "foreignKey"), Navigation = Str(r, "navigation"), Inverse = Str(r, "inverse"),
                        JoinTable = Str(r, "joinTable"), FromColumn = Str(r, "fromColumn"), ToColumn = Str(r, "toColumn"),
                    });
                var first = Array(root, "diagrams").FirstOrDefault();
                if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("shapes", out var shapes) && shapes.ValueKind == JsonValueKind.Object)
                    foreach (var p in shapes.EnumerateObject())
                        if (p.Value.ValueKind == JsonValueKind.Object)
                            m.Places[p.Name] = (Num(p.Value, "x"), Num(p.Value, "y"));
            }
            return m;
        }

        private static DmField ReadField(JsonElement f) => new()
        {
            Name = Str(f, "name") ?? "", Type = Str(f, "type") ?? "string", Column = Str(f, "column"), Key = Bool(f, "key"),
            Generated = !(f.TryGetProperty("generated", out var g) && g.ValueKind == JsonValueKind.False), Required = Bool(f, "required"),
            Length = f.TryGetProperty("length", out var l) && l.TryGetInt32(out int lv) ? lv : 0, Unique = Bool(f, "unique"), Index = Bool(f, "index"),
            Version = Bool(f, "version"), Json = Bool(f, "json"), Comment = Str(f, "comment"),
        };

        /// <summary>The model as the text of its file.</summary>
        public string ToJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\n  \"format\": ").Append(DataModelGenerator.CurrentFormat).Append(",\n  \"namespace\": ").Append(Q(Namespace));
            if (TableNaming != "name" || ColumnNaming != "field")
                sb.Append(",\n  \"naming\": { \"tables\": ").Append(Q(TableNaming)).Append(", \"columns\": ").Append(Q(ColumnNaming)).Append(" }");
            if (Enums.Count > 0)
            {
                sb.Append(",\n  \"enums\": [\n");
                sb.Append(string.Join(",\n", Enums.Select(e => "    { \"name\": " + Q(e.Name) + ", \"values\": [" + string.Join(", ", e.Values.Select(Q)) + "]" + Opt("comment", e.Comment) + " }")));
                sb.Append("\n  ]");
            }
            if (Types.Count > 0)
            {
                sb.Append(",\n  \"types\": [\n");
                sb.Append(string.Join(",\n", Types.Select(t => "    { \"name\": " + Q(t.Name) + Opt("comment", t.Comment) + ",\n      \"fields\": [\n" + Fields(t.Fields) + "\n      ] }")));
                sb.Append("\n  ]");
            }
            sb.Append(",\n  \"entities\": [\n");
            sb.Append(string.Join(",\n", Entities.Select(e =>
                "    { \"name\": " + Q(e.Name) + Opt("table", e.Table) + (e.Record ? ", \"record\": true" : "") + Opt("set", e.Set) + Opt("comment", e.Comment) +
                ",\n      \"fields\": [\n" + Fields(e.Fields) + "\n      ] }")));
            sb.Append("\n  ]");
            if (Relations.Count > 0)
            {
                sb.Append(",\n  \"relations\": [\n");
                sb.Append(string.Join(",\n", Relations.Select(r => "    { \"kind\": " + Q(r.Kind) + ", \"from\": " + Q(r.From) + ", \"to\": " + Q(r.To) + Opt("foreignKey", r.ForeignKey) +
                    Opt("navigation", r.Navigation) + Opt("inverse", r.Inverse) + Opt("joinTable", r.JoinTable) + Opt("fromColumn", r.FromColumn) + Opt("toColumn", r.ToColumn) + " }")));
                sb.Append("\n  ]");
            }
            if (Places.Count > 0)
            {
                // (the places of the boxes that are there, in the order of the model)
                var names = Entities.Select(e => e.Name).Concat(Enums.Select(e => e.Name)).Concat(Types.Select(t => t.Name)).Where(Places.ContainsKey).Distinct().ToList();
                sb.Append(",\n  \"diagrams\": [ { \"name\": \"Main\", \"shapes\": { ");
                sb.Append(string.Join(", ", names.Select(n => Q(n) + ": { \"x\": " + N(Places[n].X) + ", \"y\": " + N(Places[n].Y) + " }")));
                sb.Append(" } } ]");
            }
            sb.Append("\n}\n");
            return sb.ToString();
        }

        private static string Fields(List<DmField> fields) => string.Join(",\n", fields.Select(f =>
            "        { \"name\": " + Q(f.Name) + ", \"type\": " + Q(f.Type) + Opt("column", f.Column) + (f.Key ? ", \"key\": true" : "") + (f.Key && !f.Generated ? ", \"generated\": false" : "") +
            (f.Required ? ", \"required\": true" : "") + (f.Length > 0 ? ", \"length\": " + f.Length.ToString(CultureInfo.InvariantCulture) : "") + (f.Unique ? ", \"unique\": true" : "") +
            (f.Index ? ", \"index\": true" : "") + (f.Version ? ", \"version\": true" : "") + (f.Json ? ", \"json\": true" : "") + Opt("comment", f.Comment) + " }"));
        private static string Opt(string name, string? value) => string.IsNullOrEmpty(value) ? "" : ", " + Q(name) + ": " + Q(value);
        private static string Q(string s) => JsonSerializer.Serialize(s);
        private static string N(double v) => Math.Round(v).ToString(CultureInfo.InvariantCulture);

        // ---- what the generator says ----

        /// <summary>The mistakes of the model as a build would report them (empty: none).</summary>
        public IReadOnlyList<MarkupDiagnostic> Problems(string path = "Model.fdata")
        {
            try { DataModelGenerator.Generate(ToJson(), path); return System.Array.Empty<MarkupDiagnostic>(); }
            catch (MarkupException ex) { return ex.Diagnostics; }
        }

        /// <summary>The fire code a build makes of the model; null when it has mistakes.</summary>
        public string? GeneratedCode(string path = "Model.fdata")
        {
            try { return DataModelGenerator.Generate(ToJson(), path); }
            catch (MarkupException) { return null; }
        }

        // ---- editing ----

        /// <summary>All names of the model (entities, enums, types).</summary>
        public IEnumerable<string> Names => Entities.Select(e => e.Name).Concat(Enums.Select(e => e.Name)).Concat(Types.Select(t => t.Name));

        /// <summary>A name like `baseName` that the model does not have yet (Entity, Entity2, ...).</summary>
        public string FreeName(string baseName)
        {
            var taken = Names.ToHashSet(StringComparer.Ordinal);
            if (!taken.Contains(baseName)) return baseName;
            for (int i = 2; ; i++) if (!taken.Contains(baseName + i)) return baseName + i;
        }

        /// <summary>A new entity with a key `id` at a place of the diagram.</summary>
        public DmEntity AddEntity(string? name = null, double x = 40, double y = 40)
        {
            var e = new DmEntity { Name = FreeName(name ?? "Entity") };
            e.Fields.Add(new DmField { Name = "id", Type = "int", Key = true });
            Entities.Add(e);
            Places[e.Name] = (x, y);
            return e;
        }

        public DmEnum AddEnum(string? name = null, double x = 40, double y = 40)
        {
            var e = new DmEnum { Name = FreeName(name ?? "Kind") };
            e.Values.AddRange(new[] { "First", "Second" });
            Enums.Add(e);
            Places[e.Name] = (x, y);
            return e;
        }

        public DmType AddType(string? name = null, double x = 40, double y = 40)
        {
            var t = new DmType { Name = FreeName(name ?? "Value") };
            t.Fields.Add(new DmField { Name = "text", Type = "string" });
            Types.Add(t);
            Places[t.Name] = (x, y);
            return t;
        }

        /// <summary>Takes an entity, enum or type out - with the relations of an entity.</summary>
        public void Remove(string name)
        {
            Entities.RemoveAll(e => e.Name == name);
            Enums.RemoveAll(e => e.Name == name);
            Types.RemoveAll(t => t.Name == name);
            Relations.RemoveAll(r => r.From == name || r.To == name);
            Places.Remove(name);
        }

        /// <summary>Renames an entity, enum or type: its relations, the fields of its type and its place go with it. False when the new name is taken.</summary>
        public bool Rename(string oldName, string newName)
        {
            if (oldName == newName) return true;
            if (string.IsNullOrWhiteSpace(newName) || Names.Contains(newName)) return false;
            foreach (var e in Entities.Where(e => e.Name == oldName)) e.Name = newName;
            foreach (var e in Enums.Where(e => e.Name == oldName)) e.Name = newName;
            foreach (var t in Types.Where(t => t.Name == oldName)) t.Name = newName;
            static string Lower(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s.Substring(1);
            foreach (var r in Relations)
            {
                // (the names AddRelation gave go with the entity; names of one's own stay)
                if (r.From == oldName)
                {
                    r.From = newName;
                    if (r.Inverse == Lower(oldName)) r.Inverse = Lower(newName);
                    else if (r.Inverse == Lower(DataModelGenerator.Plural(oldName))) r.Inverse = Lower(DataModelGenerator.Plural(newName));
                }
                if (r.To == oldName)
                {
                    r.To = newName;
                    if (r.Navigation == Lower(DataModelGenerator.Plural(oldName))) r.Navigation = Lower(DataModelGenerator.Plural(newName));
                }
            }
            foreach (var f in Entities.SelectMany(e => e.Fields).Concat(Types.SelectMany(t => t.Fields)).Where(f => f.Type == oldName)) f.Type = newName;
            if (Places.Remove(oldName, out var place)) Places[newName] = place;
            return true;
        }

        public DmEntity? Entity(string name) => Entities.FirstOrDefault(e => e.Name == name);

        /// <summary>A new field of an entity or a type, with a free name.</summary>
        public DmField AddField(List<DmField> fields, string type = "string")
        {
            string name = "field";
            for (int i = 2; fields.Any(f => f.Name == name); i++) name = "field" + i;
            var f = new DmField { Name = name, Type = type };
            fields.Add(f);
            return f;
        }

        /// <summary>Moves a field one place up (-1) or down (+1).</summary>
        public static void MoveField(List<DmField> fields, DmField field, int by)
        {
            int at = fields.IndexOf(field), to = at + by;
            if (at < 0 || to < 0 || to >= fields.Count) return;
            fields.RemoveAt(at);
            fields.Insert(to, field);
        }

        /// <summary>A relation of two entities with the names the generator would choose: one-to-many - the list `purchases` in From, the object
        /// `customer` in To; many-to-many - the lists on both sides.</summary>
        public DmRelation AddRelation(string kind, string from, string to)
        {
            var r = new DmRelation { Kind = kind, From = from, To = to };
            string Lower(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s.Substring(1);
            r.Navigation = Lower(DataModelGenerator.Plural(to));
            r.Inverse = kind == "one-to-many" ? Lower(from) : Lower(DataModelGenerator.Plural(from));
            if (from == to) { r.Navigation = Lower(DataModelGenerator.Plural(to)); r.Inverse = kind == "one-to-many" ? "parent" : Lower(DataModelGenerator.Plural(from)) + "Of"; }
            Relations.Add(r);
            return r;
        }

        /// <summary>The table an entity gets (its own or by the naming of the model).</summary>
        public string TableOf(DmEntity e) => e.Table ?? TableNaming switch
        {
            "plural" => DataModelGenerator.Plural(e.Name),
            "snake" => DataModelGenerator.Snake(e.Name),
            "snake_plural" => DataModelGenerator.Snake(DataModelGenerator.Plural(e.Name)),
            _ => e.Name,
        };

        // ---- helpers ----
        private static IEnumerable<JsonElement> Array(JsonElement owner, string name)
        {
            if (!owner.TryGetProperty(name, out var a) || a.ValueKind != JsonValueKind.Array) yield break;
            foreach (var item in a.EnumerateArray()) if (item.ValueKind == JsonValueKind.Object) yield return item;
        }
        private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        private static double Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetDouble(out double d) ? d : 0;
    }
}
