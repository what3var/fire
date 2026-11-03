using System.Globalization;
using System.Text;
using fire.Ast;
using fire.Bytecode;
using fire.Standard;

namespace fire.Native
{
    /// <summary>
    /// JSON (`#import "json"`, docs/JSON.md) in the native backend: the program calls `__JsonCall`, the runtime part is native/runtime/fire_json.h (C)
    /// (put into the generated file), and the generator writes the tables it reads - per class its fields as JSON sees them (JsonMethods.JsonFieldsOf),
    /// the slots of List and JsonObject, and the texts of the type arguments. Every class of the program is generated: `"$type"` can name any of them.
    /// </summary>
    public sealed partial class CppGenerator
    {
        private bool _usesJson;

        private void UseJson()
        {
            if (_usesJson) return;
            _usesJson = true;
            UseExceptions();   // a JSON text that cannot be read is a JsonException
            if (!_errorClasses.Contains("JsonException")) _errorClasses.Add("JsonException");
            _version++;
        }

        /// <summary>The C++ expression of the type argument of the running generic method (`Json.Deserialize&lt;Person&gt;`): its text, or NULL.</summary>
        private static string JsonCallType(Func f) => f.HasCallTArgs ? "(ctargs && ctargs[0] ? jsonTArgText(ctargs[0]) : NULL)" : "NULL";

        /// <summary>What reading JSON can make: the classes the declared types name (`Json.Deserialize&lt;Shape&gt;`, the types of their fields, transitively) with
        /// their subclasses (`"$type"` may name one), every class as soon as a slot without a class is read (`any`, a field without a type, a class name given as a
        /// text) - never a class that is not serializable (`[NotSerializable]`, `"serializable": false` in its package). Writing needs nothing more: the objects
        /// that are written exist. The units and type arguments of the fields of every class that JSON can see are registered for the tables.</summary>
        private void RegisterJsonTargets()
        {
            if (!_usesJson) return;
            if (_program.Program.Classes.TryGetValue("Json", out var json))
            {
                bool reads = false, anySlot = false;
                var declared = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var (name, protos) in json.Methods)
                    foreach (var proto in protos)
                    {
                        if (!_funcByProto.ContainsKey(proto)) continue;
                        if (name == "Parse") reads = true;
                        if (name != "Deserialize") continue;
                        reads = true;
                        if (proto.ParamCount >= 2) anySlot = true;   // (the class is a text the program gives at run time)
                    }
                if (reads)
                {
                    // the type arguments of the calls of Json.Deserialize<T>: a call without a constant one (T of a generic method around it) reads into a slot without a class
                    foreach (var f in _funcs.Append(_main!).ToList())
                    {
                        var code = f.Decoded;
                        for (int i = 0; i < code.Count; i++)
                        {
                            var ins = code[i];
                            if (ins.Op != OpCode.CallStaticMethod || ins.A[2] != 1 || f.Chunk.Constants[ins.A[0]].AsString() != "Json" || f.Chunk.Constants[ins.A[1]].AsString() != "Deserialize") continue;
                            int set = i - 1;
                            if (set >= 0 && code[set].Op == OpCode.CopyArgs) set--;
                            string? text = set >= 0 && code[set].Op == OpCode.SetTypeArgs ? f.Chunk.Constants[code[set].A[0]].AsString() : null;
                            if (text == null || text.Contains('$')) anySlot = true;
                            else declared.Add(text);
                        }
                    }
                    if (_program.Program.Classes.ContainsKey("JsonObject")) RegisterClass("JsonObject");   // (an object without `$type` in a slot without a class)
                    var seen = new HashSet<RuntimeClass>(ReferenceEqualityComparer.Instance);
                    foreach (var text in declared) anySlot |= JsonReachType(text, seen);
                    if (anySlot)
                        foreach (var rc in _program.Program.Classes.Values.ToList())
                            if (!rc.IsNotSerializable()) RegisterClass(rc.Name);
                }
            }
            foreach (var cls in _classList.ToList())
                foreach (var field in JsonMethods.JsonFieldsOf(cls.Rc))
                {
                    UnitId(field.Unit);
                    foreach (var arg in JsonTypeArgTexts(field.Type)) TArgRef(arg);
                }
        }

        private static readonly HashSet<string> JsonPlainTypes = new(StringComparer.Ordinal)
        {
            "int", "float", "bool", "string", "char", "byte", "long", "short", "double", "buffer", "undefined",
            "int8", "int16", "int32", "int64", "uint8", "uint16", "uint32", "uint64", "float32", "float64",
        };

        /// <summary>Registers the classes a declared type of a slot that JSON reads can make (see <see cref="RegisterJsonTargets"/>); true: the type has a slot
        /// without a class (then every class can be made).</summary>
        private bool JsonReachType(string type, HashSet<RuntimeClass> seen)
        {
            string bare = type.TrimStart('#');
            if (bare.Length == 0) return true;
            bool any = false;
            foreach (System.Text.RegularExpressions.Match token in System.Text.RegularExpressions.Regex.Matches(bare, @"[A-Za-z_$][A-Za-z0-9_.`$]*"))
            {
                string name = token.Value;
                if (name.StartsWith('$') || name is "any" or "class") { any = true; continue; }
                if (JsonPlainTypes.Contains(name)) continue;
                var all = _program.Program.Classes.Values.ToList();
                var named = all.Where(rc => rc.Name == name || GenericClassNames.PlainName(rc.Name) == name).Select(rc => rc.Name).Append(name).Distinct().ToList();
                foreach (var rc in all)
                {
                    // the class, its subclasses and the classes of an interface (`"$type"` may name each)
                    if (!named.Any(n => ClassIs(rc, n)) || rc.IsNotSerializable() || !seen.Add(rc)) continue;
                    RegisterClass(rc.Name);
                    // (List and JsonObject are read by JSON itself: their fields are not slots of the text)
                    if (rc.Name is "List" or "JsonObject") continue;
                    foreach (var field in JsonMethods.JsonFieldsOf(rc)) any |= JsonReachType(field.Type, seen);
                }
            }
            return any;
        }

        /// <summary>The type arguments a declared type names (`List&lt;Point&gt;` -> Point) that can be type arguments of an object read from JSON.</summary>
        private static IEnumerable<string> JsonTypeArgTexts(string type)
        {
            int lt = type.IndexOf('<');
            if (lt < 0 || !type.EndsWith('>')) yield break;
            foreach (var part in type.Substring(lt + 1, type.Length - lt - 2).Split(','))
            {
                string arg = part.Trim();
                if (arg.Length == 0 || arg.Contains('<') || arg.Contains('$') || arg.Contains('[')) continue;
                yield return arg;
            }
        }

        /// <summary>The number of type parameters of a class as the program uses them (the type arguments its objects are made with, `$k` in its fields).</summary>
        private int JsonTypeParamCount(RuntimeClass rc)
        {
            int count = 0;
            if (_classTArgTexts.TryGetValue(rc.Name, out var texts))
                foreach (var text in texts) count = Math.Max(count, text.Split('|').Length);
            foreach (var field in JsonMethods.JsonFieldsOf(rc))
                if (field.Type.StartsWith('$'))
                {
                    int end = 1;
                    while (end < field.Type.Length && char.IsAsciiDigit(field.Type[end])) end++;
                    if (end > 1) count = Math.Max(count, int.Parse(field.Type.AsSpan(1, end - 1), CultureInfo.InvariantCulture) + 1);
                }
            return Math.Min(count, 255);
        }

        /// <summary>The engine (fire_json.h) and the tables of the program.</summary>
        private string JsonCode()
        {
            var sb = new StringBuilder();
            // the engine (C, fire_json.h), then the tables of the program's classes and the functions that read them
            sb.AppendLine("#define FIRE_JSON_IMPLEMENTATION 1");
            sb.AppendLine("#include \"fire_json.h\"");
            var rows = new List<string>();
            int listCls = -1, listItems = -1, listCount = -1, objCls = -1, objKeys = -1, objValues = -1, objCount = -1;
            foreach (var ci in _classList.OrderBy(c => c.Id))
            {
                var rc = ci.Rc;
                var fields = JsonMethods.JsonFieldsOf(rc).Where(f => rc.FieldIndex.ContainsKey(f.Name)).ToList();
                string fieldTable = "NULL";
                if (fields.Count > 0)
                {
                    fieldTable = $"kJsonF{ci.Id}";
                    sb.AppendLine($"static const JsonFieldInfo {fieldTable}[] = {{" + string.Join(", ", fields.Select(f =>
                        $"{{{CString(f.JsonName)}, {rc.FieldIndex[f.Name]}, {CString(f.Type)}, {UnitId(f.Unit)}}}")) + "};");
                }
                int flags = 0;
                if (rc.IsActor) flags |= 1;
                if (rc.IsNotSerializable()) flags |= 16;
                if (JsonMethods.HasOnDeserialized(rc)) flags |= 2;
                if (rc.Name == "List" && rc.FieldIndex.TryGetValue("items", out int items) && rc.FieldIndex.TryGetValue("count", out int count))
                {
                    flags |= 4;
                    (listCls, listItems, listCount) = (ci.Id, items, count);
                }
                if (rc.Name == "JsonObject" && rc.FieldIndex.TryGetValue("keys", out int keys) && rc.FieldIndex.TryGetValue("values", out int values) && rc.FieldIndex.TryGetValue("count", out int n))
                {
                    flags |= 8;
                    (objCls, objKeys, objValues, objCount) = (ci.Id, keys, values, n);
                }
                rows.Add($"{{{CString(rc.Name)}, {CString(GenericClassNames.PlainName(rc.Name))}, {ci.Fields.Count}, {fieldTable}, {fields.Count}, {flags}, {JsonTypeParamCount(rc)}}}");
            }
            sb.AppendLine($"static const JsonClassInfo kJsonClasses[] = {{{(rows.Count == 0 ? "{\"\", \"\", 0, NULL, 0, 0, 0}" : string.Join(",\n    ", rows))}}};");
            sb.AppendLine($"static const uint32_t kJsonClassCount = {rows.Count};");
            sb.AppendLine($"static const JsonSpecial kJsonSpecial = {{{listCls}, {listItems}, {listCount}, {objCls}, {objKeys}, {objValues}, {objCount}}};");
            var targs = _targIds.OrderBy(kv => kv.Value).ToList();
            sb.AppendLine("typedef struct JsonTArgText { const char* text; const TArg* targ; } JsonTArgText;");
            sb.AppendLine($"static const JsonTArgText kJsonTArgs[] = {{{(targs.Count == 0 ? "{NULL, NULL}" : string.Join(", ", targs.Select(t => $"{{{CString(t.Key)}, &ta_{t.Value}}}")))}}};");
            sb.AppendLine($"static const uint32_t kJsonTArgCount = {targs.Count};");
            sb.AppendLine("const JsonClassInfo* jsonClassAt(uint32_t cls) { return cls < kJsonClassCount ? &kJsonClasses[cls] : NULL; }");
            sb.AppendLine("int32_t jsonClassNamed(const char* name) {");
            sb.AppendLine("    for (uint32_t i = 0; i < kJsonClassCount; i++) if (strcmp(kJsonClasses[i].name, name) == 0) return (int32_t)i;");
            sb.AppendLine("    return -1;");
            sb.AppendLine("}");
            // the classes JSON may not read that are not in the tables (a name of the text that names one is an ArgumentException, as in the VM)
            var excluded = _program.Program.Classes.Values.Where(rc => rc.IsNotSerializable() && !_classes.ContainsKey(rc.Name)).Select(rc => rc.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            sb.AppendLine("const char* jsonExcludedNamed(const char* name) {");
            if (excluded.Count > 0)
            {
                sb.AppendLine($"    static const char* const names[] = {{{string.Join(", ", excluded.Select(CString))}}};");
                sb.AppendLine($"    for (uint32_t i = 0; i < {excluded.Count}u; i++) if (strcmp(names[i], name) == 0) return names[i];");
            }
            else sb.AppendLine("    (void)name;");
            sb.AppendLine("    return NULL;");
            sb.AppendLine("}");
            sb.AppendLine("const char* jsonTArgText(const TArg* t) {");
            sb.AppendLine("    for (uint32_t i = 0; i < kJsonTArgCount; i++) if (kJsonTArgs[i].targ == t) return kJsonTArgs[i].text;");
            sb.AppendLine("    return NULL;");
            sb.AppendLine("}");
            sb.AppendLine("const TArg* jsonTArgFor(const char* text) {");
            sb.AppendLine("    for (uint32_t i = 0; i < kJsonTArgCount; i++) if (kJsonTArgs[i].text && strcmp(text, kJsonTArgs[i].text) == 0) return kJsonTArgs[i].targ;");
            sb.AppendLine("    return NULL;");
            sb.AppendLine("}");
            sb.AppendLine("const JsonSpecial* jsonSpecial(void) { return &kJsonSpecial; }");
            return sb.ToString();
        }
    }
}
