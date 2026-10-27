using System.Globalization;
using System.Text;
using fire.Ast;
using fire.Bytecode;
using fire.Standard;

namespace fire.Native
{
    /// <summary>
    /// JSON (`#import "json"`, docs/JSON.md) in the native backend: the program calls `__JsonCall`, the runtime part is native/runtime/fire_json.hpp
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

        /// <summary>The C++ expression of the type argument of the running generic method (`Json.Deserialize&lt;Person&gt;`): its text, or nullptr.</summary>
        private static string JsonCallType(Func f) => f.HasCallTArgs ? "(ctargs && ctargs[0] ? jsonTArgText(ctargs[0]) : nullptr)" : "nullptr";

        /// <summary>Every class can be named by `"$type"`: all are registered, with the units of their fields and the type arguments their fields name.</summary>
        private void RegisterJsonTargets()
        {
            if (!_usesJson) return;
            foreach (var rc in _program.Program.Classes.Values.ToList())
            {
                RegisterClass(rc.Name);
                foreach (var field in JsonMethods.JsonFieldsOf(rc))
                {
                    UnitId(field.Unit);
                    foreach (var arg in JsonTypeArgTexts(field.Type)) TArgRef(arg);
                }
            }
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

        /// <summary>The runtime part (fire_json.hpp) and the tables of the program.</summary>
        private string JsonCode()
        {
            var sb = new StringBuilder();
            string runtime = NativeRuntimeFiles.ReadText("runtime/fire_json.hpp") ?? throw new NativeNotSupportedException("the JSON runtime (fire_json.hpp) is missing");
            sb.AppendLine("// ---- runtime/fire_json.hpp");
            sb.AppendLine(runtime);
            sb.AppendLine("// ---- end of runtime/fire_json.hpp");
            sb.AppendLine("namespace fire {");
            var rows = new List<string>();
            int listCls = -1, listItems = -1, listCount = -1, objCls = -1, objKeys = -1, objValues = -1, objCount = -1;
            foreach (var ci in _classList.OrderBy(c => c.Id))
            {
                var rc = ci.Rc;
                var fields = JsonMethods.JsonFieldsOf(rc).Where(f => rc.FieldIndex.ContainsKey(f.Name)).ToList();
                string fieldTable = "nullptr";
                if (fields.Count > 0)
                {
                    fieldTable = $"kJsonF{ci.Id}";
                    sb.AppendLine($"static const JsonFieldInfo {fieldTable}[] = {{" + string.Join(", ", fields.Select(f =>
                        $"{{{CString(f.JsonName)}, {rc.FieldIndex[f.Name]}, {CString(f.Type)}, {UnitId(f.Unit)}}}")) + "};");
                }
                int flags = 0;
                if (rc.IsActor) flags |= 1;
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
            sb.AppendLine($"static const JsonClassInfo kJsonClasses[] = {{{(rows.Count == 0 ? "{\"\", \"\", 0, nullptr, 0, 0, 0}" : string.Join(",\n    ", rows))}}};");
            sb.AppendLine($"static const uint32_t kJsonClassCount = {rows.Count};");
            sb.AppendLine($"static const JsonSpecial kJsonSpecial = {{{listCls}, {listItems}, {listCount}, {objCls}, {objKeys}, {objValues}, {objCount}}};");
            var targs = _targIds.OrderBy(kv => kv.Value).ToList();
            sb.AppendLine("struct JsonTArgText { const char* text; const TArg* targ; };");
            sb.AppendLine($"static const JsonTArgText kJsonTArgs[] = {{{(targs.Count == 0 ? "{nullptr, nullptr}" : string.Join(", ", targs.Select(t => $"{{{CString(t.Key)}, &ta_{t.Value}}}")))}}};");
            sb.AppendLine($"static const uint32_t kJsonTArgCount = {targs.Count};");
            sb.AppendLine("static const JsonClassInfo* jsonClassAt(uint32_t cls) { return cls < kJsonClassCount ? &kJsonClasses[cls] : nullptr; }");
            sb.AppendLine("static int32_t jsonClassNamed(const char* name) {");
            sb.AppendLine("    for (uint32_t i = 0; i < kJsonClassCount; i++) if (std::strcmp(kJsonClasses[i].name, name) == 0) return (int32_t)i;");
            sb.AppendLine("    return -1;");
            sb.AppendLine("}");
            sb.AppendLine("static const char* jsonTArgText(const TArg* t) {");
            sb.AppendLine("    for (uint32_t i = 0; i < kJsonTArgCount; i++) if (kJsonTArgs[i].targ == t) return kJsonTArgs[i].text;");
            sb.AppendLine("    return nullptr;");
            sb.AppendLine("}");
            sb.AppendLine("static const TArg* jsonTArgFor(const std::string& text) {");
            sb.AppendLine("    for (uint32_t i = 0; i < kJsonTArgCount; i++) if (text == kJsonTArgs[i].text) return kJsonTArgs[i].targ;");
            sb.AppendLine("    return nullptr;");
            sb.AppendLine("}");
            sb.AppendLine("static const JsonSpecial& jsonSpecial() { return kJsonSpecial; }");
            sb.AppendLine("}  // namespace fire");
            return sb.ToString();
        }
    }
}
