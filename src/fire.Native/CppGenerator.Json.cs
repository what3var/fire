using System.Globalization;
using System.Text;
using fire.Ast;
using fire.Bytecode;
using fire.Standard;

namespace fire.Native
{
    /// <summary>
    /// JSON (`#import "json"`, docs/stdlib/json.md) in the native backend: the program calls `__JsonCall`, the runtime part is native/runtime/fire_json.h (C)
    /// (put into the generated file), and the generator writes the tables it reads - per class its fields as JSON sees them (JsonMethods.JsonFieldsOf),
    /// the slots of List and JsonObject, and the texts of the type arguments. Every class of the program is generated: `"$type"` can name any of them.
    /// </summary>
    public sealed partial class CppGenerator
    {
        private bool _usesJson;
        /// <summary>The code of this generator calls the JSON engine (fire_jsonCall): with linkage "static" a library that does gets the engine into its file.</summary>
        private bool _callsJsonEngine;
        /// <summary>The program, linkage "static": a library module has the JSON engine (it is not compiled with the program).</summary>
        private bool JsonEngineInLibrary => _separate && _libraries != null && _libraries.Any(l => l.JsonEngine);

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
            "int", "float", "bool", "string", "char", "byte", "word", "long", "short", "double", "buffer", "undefined",
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

        /// <summary>The kind of a `defined` field for JsonFieldInfo.defined (SPEC 8.10b), "0" for any other.</summary>
        private static string DefinedKindOf(byte declared) => declared != 0 && DeclaredKind.IsNonNull(declared) ? KindConstant(declared) : "0";

        /// <summary>The JSON row of a class (a JsonClassInfo) and the table of its fields (written to <paramref name="sb"/> as <paramref name="fieldTable"/>). A plugin
        /// (<paramref name="unitFixups"/>): the table is not const and the units of its fields are set when the plugin is registered (the program's numbers).</summary>
        private string JsonRow(ClassInfo ci, string fieldTable, int flags, StringBuilder sb, List<string>? unitFixups)
        {
            var rc = ci.Rc;
            var fields = JsonMethods.JsonFieldsOf(rc).Where(f => rc.FieldIndex.ContainsKey(f.Name)).ToList();
            // (behind them the `defined` fields that do not go to JSON: a read object holds their zeros too)
            var extra = rc.FieldIndex.Keys.Where(n => fields.All(f => f.Name != n) && rc.FindFieldRequiredKind(n) is var k and not 0 && DeclaredKind.IsNonNull(k))
                .Select(n => new JsonMethods.JsonField(n, n, "", rc.FindFieldRequiredUnit(n)?.TrimStart('#') is { Length: > 0 } demand && !demand.StartsWith("any", StringComparison.Ordinal) ? fire.Values.Unit.Parse(demand) : null)).ToList();
            var rows = fields.Concat(extra).ToList();
            if (rows.Count > 0)
            {
                sb.AppendLine($"static {(unitFixups != null ? "" : "const ")}JsonFieldInfo {fieldTable}[] = {{" + string.Join(", ", rows.Select(f =>
                    $"{{{CString(f.JsonName)}, {Math.Max(0, FieldPlace(rc, rc.FieldIndex[f.Name]).Slot)}, {CString(f.Type)}, {(unitFixups != null ? 0 : UnitId(f.Unit))}, {(rc.FindFieldRequiredUnit(f.Name) == null ? rc.FindFieldRequiredArray(f.Name) : 0)}, {DefinedKindOf(rc.FindFieldRequiredKind(f.Name))}, {RawPlaceOf(rc, rc.FieldIndex[f.Name])}}}")) + ", {NULL, 0, NULL, 0, 0, 0, 0, 0}};");
                if (unitFixups != null)
                    for (int j = 0; j < rows.Count; j++)
                        if (UnitId(rows[j].Unit) is var u and not 0) unitFixups.Add($"{fieldTable}[{j}].unit = {UnitC(u)};");
            }
            else fieldTable = "NULL";
            if (rc.IsActor) flags |= 1;
            if (rc.IsNotSerializable()) flags |= 16;
            if (JsonMethods.HasOnDeserialized(rc)) flags |= 2;
            return $"{{{CString(rc.Name)}, {CString(GenericClassNames.PlainName(rc.Name))}, {SlotCount(ci)}, {fieldTable}, {fields.Count}, {flags}, {JsonTypeParamCount(rc)}, {(PackedOn ? RawLayoutOf(rc).Bytes : 0)}}}";
        }

        /// <summary>A plugin whose program has JSON: the rows of its classes and its type arguments (FirePlugin.jsonAt, ...: the program's jsonClassAt asks for a class
        /// after its own); `fixups` gets the units of their fields (set when it is registered). The fields of FirePlugin from `jsonAt` on.</summary>
        private string PluginJson(StringBuilder sb, List<string> fixups)
        {
            if (!_usesJson) return "NULL, NULL, NULL, NULL";
            var rows = new List<string>();
            for (int k = 0; k < _self!.Classes.Count; k++) rows.Add(JsonRow(RegisterClass(_self.Classes[k]), $"kPlJsonF{k}", 0, sb, fixups));
            sb.AppendLine($"static const JsonClassInfo kPlJson[] = {{\n    {string.Join(",\n    ", rows)}\n}};");
            sb.AppendLine("static const void* fire_pl_jsonAt(uint32_t k) { return &kPlJson[k]; }");
            sb.AppendLine("static int32_t fire_pl_jsonNamed(const char* name) {");
            sb.AppendLine($"    for (uint32_t k = 0; k < {rows.Count}u; k++) if (strcmp(kPlJson[k].name, name) == 0) return (int32_t)k;");
            sb.AppendLine("    return -1;");
            sb.AppendLine("}");
            var targs = _targIds.OrderBy(kv => kv.Value).ToList();
            sb.AppendLine("static const char* fire_pl_targText(const TArg* t) {");
            foreach (var (text, i) in targs) sb.AppendLine($"    if (t == &{Px}ta_{i}) return {CString(text)};");
            if (targs.Count == 0) sb.AppendLine("    (void)t;");
            sb.AppendLine("    return NULL;");
            sb.AppendLine("}");
            sb.AppendLine("static const TArg* fire_pl_targFor(const char* text) {");
            foreach (var (text, i) in targs) sb.AppendLine($"    if (strcmp(text, {CString(text)}) == 0) return &{Px}ta_{i};");
            if (targs.Count == 0) sb.AppendLine("    (void)text;");
            sb.AppendLine("    return NULL;");
            sb.AppendLine("}");
            return "fire_pl_jsonAt, fire_pl_jsonNamed, fire_pl_targText, fire_pl_targFor";
        }

        /// <summary>The engine (fire_json.h) and the tables of the program.</summary>
        private string JsonCode()
        {
            var sb = new StringBuilder();
            // the engine (C, fire_json.h), then the tables of the program's classes and the functions that read them
            // (with library modules in one file it is included before their code; linkage "static": the engine is in the file of the module that calls it)
            if (_moduleBodies == null && !JsonEngineInLibrary)
            {
                sb.AppendLine("#define FIRE_JSON_IMPLEMENTATION 1");
                sb.AppendLine("#include \"fire_json.h\"");
            }
            else if (_moduleBodies == null) sb.AppendLine("#include \"fire_json.h\"   // (the engine: in the library of the module that calls it)");
            var rows = new List<string>();
            int listCls = -1, listItems = -1, listCount = -1, objCls = -1, objKeys = -1, objValues = -1, objCount = -1;
            var jsonById = _classList.ToDictionary(c => c.Id);
            int jsonIds = _classList.Count == 0 ? 0 : _classList.Max(c => c.Id) + 1;
            for (int jsonId = 0; jsonId < jsonIds; jsonId++)
            {
                if (!jsonById.TryGetValue(jsonId, out var ci)) { rows.Add("{\"\", \"\", 0, NULL, 0, 0, 0, 0}"); continue; }   // (an id without a class, see ClassDescTable)
                var rc = ci.Rc;
                int flags = 0;
                if (rc.Name == "List" && rc.FieldIndex.TryGetValue("items", out int items) && rc.FieldIndex.TryGetValue("count", out int count))
                {
                    flags |= 4;
                    (listCls, listItems, listCount) = (ci.Id, FieldPlace(rc, items).Slot, FieldPlace(rc, count).Slot);
                }
                if (rc.Name == "JsonObject" && rc.FieldIndex.TryGetValue("keys", out int keys) && rc.FieldIndex.TryGetValue("values", out int values) && rc.FieldIndex.TryGetValue("count", out int n))
                {
                    flags |= 8;
                    (objCls, objKeys, objValues, objCount) = (ci.Id, FieldPlace(rc, keys).Slot, FieldPlace(rc, values).Slot, FieldPlace(rc, n).Slot);
                }
                rows.Add(JsonRow(ci, $"kJsonF{ci.Id}", flags, sb, null));
            }
            sb.AppendLine($"static const JsonClassInfo kJsonClasses[] = {{{(rows.Count == 0 ? "{\"\", \"\", 0, NULL, 0, 0, 0, 0}" : string.Join(",\n    ", rows))}}};");
            sb.AppendLine($"static const uint32_t kJsonClassCount = {rows.Count};");
            sb.AppendLine($"static const JsonSpecial kJsonSpecial = {{{listCls}, {listItems}, {listCount}, {objCls}, {objKeys}, {objValues}, {objCount}}};");
            var targs = _targIds.OrderBy(kv => kv.Value).ToList();
            sb.AppendLine("typedef struct JsonTArgText { const char* text; const TArg* targ; } JsonTArgText;");
            sb.AppendLine($"static const JsonTArgText kJsonTArgs[] = {{{(targs.Count == 0 ? "{NULL, NULL}" : string.Join(", ", targs.Select(t => $"{{{CString(t.Key)}, &ta_{t.Value}}}")))}}};");
            sb.AppendLine($"static const uint32_t kJsonTArgCount = {targs.Count};");
            // (a program that loads plugins: what is not in its tables may be a class or a type argument of a plugin - the runtime asks them)
            sb.AppendLine($"const JsonClassInfo* jsonClassAt(uint32_t cls) {{ return cls < kJsonClassCount ? &kJsonClasses[cls] : {(_openWorld ? "(const JsonClassInfo*)firePluginJsonAt(cls)" : "NULL")}; }}");
            sb.AppendLine("int32_t jsonClassNamed(const char* name) {");
            sb.AppendLine("    for (uint32_t i = 0; i < kJsonClassCount; i++) if (strcmp(kJsonClasses[i].name, name) == 0) return (int32_t)i;");
            sb.AppendLine($"    return {(_openWorld ? "firePluginJsonNamed(name)" : "-1")};");
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
            sb.AppendLine($"    return {(_openWorld ? "firePluginTArgText(t)" : "NULL")};");
            sb.AppendLine("}");
            sb.AppendLine("const TArg* jsonTArgFor(const char* text) {");
            sb.AppendLine("    for (uint32_t i = 0; i < kJsonTArgCount; i++) if (kJsonTArgs[i].text && strcmp(text, kJsonTArgs[i].text) == 0) return kJsonTArgs[i].targ;");
            sb.AppendLine($"    return {(_openWorld ? "firePluginTArgFor(text)" : "NULL")};");
            sb.AppendLine("}");
            sb.AppendLine("const JsonSpecial* jsonSpecial(void) { return &kJsonSpecial; }");
            return sb.ToString();
        }
    }
}
