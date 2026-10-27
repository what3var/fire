using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using fire.Ast;
using fire.Bytecode;
using fire.Runtime;
using fire.Values;

namespace fire.Standard
{
    /// <summary>What the JSON functions need from the virtual machine that runs them (VM implements it): who owns new objects, the classes, the type
    /// arguments of the call (`Json.Deserialize&lt;Person&gt;`), how a script exception is made.</summary>
    public interface IJsonHost
    {
        /// <summary>The owner of the root of a read graph: the scope of the calling code.</summary>
        IOwner CurrentOwner { get; }
        RuntimeClass? FindClass(string name);
        /// <summary>The type arguments of the running generic method (DeclaredType texts), null when there are none.</summary>
        string[]? CallTypeArgs { get; }
        Exception Error(string className, string message);
    }

    /// <summary>
    /// JSON (docs/JSON.md, `#import "json"`): `Json.Serialize(value)`, `Json.Deserialize&lt;T&gt;(text)`, `Json.Parse(text)`. One native function
    /// `__JsonCall(id, ...)` like Math/String; the native backend implements the same (fire_json.hpp).
    ///
    /// The format is plain JSON. An object is a JSON object with its fields (in declaration order, base class first; `[NonSerialized]` leaves a field
    /// out, `[JsonName "x"]` renames it); `"$type"` names its class where the slot does not tell it (the root, an `any` field, an object of a derived
    /// class). An object or array that is reached more than once gets `"$id": n` where it is written out and `{"$ref": n}` everywhere else (cycles
    /// included) - it is written out at its owner (as `copy` keeps the ownership tree), otherwise where it occurs first. A `List` is a JSON array.
    /// Numbers keep their kind (a float always has a `.` or an exponent); a number with a unit is a plain number in a field declared with that unit,
    /// otherwise `{"$value": 5, "$unit": "cm"}`. In an untyped slot a char is `{"$char": "a"}` and a buffer `{"$buffer": "base64"}`; in a typed one
    /// they are a string. Lambdas and pointers are left out.
    /// </summary>
    public static class JsonMethods
    {
        public const string NativeName = "__JsonCall";
        public const int Serialize = 1, Deserialize = 2, Parse = 3;
        public const int FlagIndented = 1, FlagNoTypeNames = 2;

        /// <summary>Set by the VM: the host of the calling thread.</summary>
        public static Func<IJsonHost?>? Host;

        private static IJsonHost RequireHost() => Host?.Invoke() ?? throw new InvalidOperationException("JSON is only possible on the thread of a running program.");

        public static Value Call(Value[] args)
        {
            var host = RequireHost();
            try
            {
                switch ((int)args[0].AsInt())
                {
                    case Serialize:
                        return Value.MakeString(new Writer(host, args.Length > 2 ? (int)args[2].AsInt() : 0).Run(args[1]));
                    case Deserialize:
                    {
                        var root = JsonParser.Parse(args[1].AsString());
                        string type = args.Length > 2 && args[2].Kind == ValueKind.String ? args[2].AsString() : host.CallTypeArgs is { Length: > 0 } t ? t[0] : "";
                        var reader = new Reader(host);
                        var value = reader.Run(root, type);
                        var result = new ScriptArray(2);
                        result.Items[0] = value;
                        var hooks = new ScriptArray(reader.Hooks.Count);
                        for (int i = 0; i < reader.Hooks.Count; i++) hooks.Items[i] = Value.MakeClassRef(reader.Hooks[i]);
                        LeafOwnership.Adopt(hooks, host.CurrentOwner);
                        result.Items[1] = Value.MakeArray(hooks);
                        LeafOwnership.Adopt(result, host.CurrentOwner);
                        return Value.MakeArray(result);
                    }
                    case Parse:
                        return new Reader(host).Run(JsonParser.Parse(args[1].AsString()), "", plain: true);
                }
            }
            catch (JsonError e) { throw host.Error("JsonException", e.Message); }
            throw host.Error("ArgumentException", "Unknown JSON function.");
        }

        private sealed class JsonError : Exception { public JsonError(string message) : base(message) { } }

        // ---------------------------------------------------------------------------------------------------------------------------------
        // Field metadata
        // ---------------------------------------------------------------------------------------------------------------------------------

        /// <summary>A field as JSON sees it: its name, the name in the JSON text, its declared type (FieldInfo.TypeText) and the unit it is declared with.</summary>
        public sealed record JsonField(string Name, string JsonName, string Type, Unit? Unit);

        /// <summary>The fields of a class that go to JSON (the native backend writes them into its tables).</summary>
        public static IReadOnlyList<JsonField> JsonFieldsOf(RuntimeClass rc) => FieldsOf(rc);

        /// <summary>Does reading an object of this class call its `OnDeserialized()`?</summary>
        public static bool HasOnDeserialized(RuntimeClass rc) => rc.FindMethodWithAccess("OnDeserialized", 0).Proto is { IsStatic: false };

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RuntimeClass, List<JsonField>> s_fields = new();

        /// <summary>The fields of a class that go to JSON, in declaration order (base class first).</summary>
        private static List<JsonField> FieldsOf(RuntimeClass rc) => s_fields.GetValue(rc, c =>
        {
            var result = new List<JsonField>();
            var seen = new HashSet<string>();
            var chain = new List<RuntimeClass>();
            for (var r = c; r != null; r = r.Base) chain.Insert(0, r);
            foreach (var r in chain)
                foreach (var (name, _) in r.Fields)
                {
                    var info = FieldInfoOf(c, name);
                    if (info is { IsStatic: true }) continue;
                    if (info?.Attributes?.Any(a => a.Name == "NonSerialized") == true) { seen.Add(name); continue; }
                    if (!seen.Add(name)) { result.RemoveAll(f => f.Name == name); }
                    string jsonName = info?.Attributes?.FirstOrDefault(a => a.Name == "JsonName") is { } attr && ConstantString(attr.ArgsJson) is { } renamed ? renamed : name;
                    // (`#cm` for a float: the `#` only says that a whole number becomes a float)
                    Unit? unit = info?.RequiredUnit?.TrimStart('#') is { Length: > 0 } demand && !demand.StartsWith("any", StringComparison.Ordinal) ? SafeUnit(demand) : null;
                    result.Add(new JsonField(name, jsonName, info?.TypeText ?? "", unit is { IsUnitless: true } ? null : unit));
                }
            return result;
        });

        private static FieldInfo? FieldInfoOf(RuntimeClass rc, string name)
        {
            for (var r = rc; r != null; r = r.Base)
                if (r.OwnFieldInfo.TryGetValue(name, out var info)) return info;
            return null;
        }

        private static Unit? SafeUnit(string demand)
        {
            try { return Unit.Parse(demand); } catch (Exception) { return null; }
        }

        /// <summary>The first argument of an attribute as a string constant (AttributeMeta.ArgsJson `[{"s":"x"}]`), null otherwise.</summary>
        private static string? ConstantString(string argsJson)
        {
            try
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(argsJson) as System.Text.Json.Nodes.JsonArray;
                return node is { Count: > 0 } && node[0] is System.Text.Json.Nodes.JsonObject o && o["s"] is { } s ? s.GetValue<string>() : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>The class name as JSON writes it: the plain name with its type arguments (`List<int>`).</summary>
        private static string TypeName(ObjectInstance obj)
        {
            string plain = GenericClassNames.PlainName(obj.ClassName);
            return obj.TypeArgs is { Length: > 0 } args && args.All(a => a.Length > 0) ? plain + "<" + string.Join(",", args) + ">" : plain;
        }

        /// <summary>The type of a slot with a type parameter of the class (`$0`, as FieldInfo.TypeText writes it) replaced by the object's type argument.</summary>
        private static string Substitute(string type, ObjectInstance? owner)
        {
            if (type.Length < 2 || type[0] != '$') return type;
            int end = 1;
            while (end < type.Length && char.IsAsciiDigit(type[end])) end++;
            int k = int.Parse(type.AsSpan(1, end - 1), CultureInfo.InvariantCulture);
            string arg = owner?.TypeArgs is { } args && k < args.Length ? args[k] : "";
            return arg.Length == 0 ? "" : arg + type.Substring(end);
        }

        private static bool IsList(ObjectInstance obj) => obj.RtClass?.Name == "List" && obj.RtClass.FieldIndex.ContainsKey("items") && obj.RtClass.FieldIndex.ContainsKey("count");
        private static bool IsJsonObject(ObjectInstance obj) => obj.RtClass?.Name == "JsonObject";

        private static string ElementType(string type) =>
            type.EndsWith("[]", StringComparison.Ordinal) ? type.Substring(0, type.Length - 2)
            : SplitGeneric(type) is ("List", [var arg]) ? arg : "";

        /// <summary>`List<Point>` -> ("List", ["Point"]), `Point` -> ("Point", []).</summary>
        private static (string Name, string[] Args) SplitGeneric(string type)
        {
            int lt = type.IndexOf('<');
            if (lt < 0 || !type.EndsWith(">", StringComparison.Ordinal)) return (type, Array.Empty<string>());
            var args = new List<string>();
            int depth = 0, start = lt + 1;
            for (int i = lt + 1; i < type.Length - 1; i++)
            {
                char ch = type[i];
                if (ch == '<') depth++;
                else if (ch == '>') depth--;
                else if (ch == ',' && depth == 0) { args.Add(type.Substring(start, i - start).Trim()); start = i + 1; }
            }
            args.Add(type.Substring(start, type.Length - 1 - start).Trim());
            return (type.Substring(0, lt).Trim(), args.ToArray());
        }

        private static string Plain(string type) => GenericClassNames.PlainName(SplitGeneric(type).Name);

        // ---------------------------------------------------------------------------------------------------------------------------------
        // Writing
        // ---------------------------------------------------------------------------------------------------------------------------------

        private sealed class Writer
        {
            private readonly IJsonHost _host;
            private readonly bool _indent, _typeNames;
            private readonly StringBuilder _sb = new();
            private readonly Dictionary<object, int> _seen = new(ReferenceEqualityComparer.Instance);   // how often an object/array is reached
            private readonly Dictionary<object, int> _ids = new(ReferenceEqualityComparer.Instance);
            private readonly HashSet<object> _written = new(ReferenceEqualityComparer.Instance);
            private readonly HashSet<object> _ownerInGraph = new(ReferenceEqualityComparer.Instance);   // reached from its owner too: written out there
            private int _depth;

            public Writer(IJsonHost host, int flags)
            {
                _host = host;
                _indent = (flags & FlagIndented) != 0;
                _typeNames = (flags & FlagNoTypeNames) == 0;
            }

            public string Run(Value root)
            {
                Discover(root);
                WriteValue(root, "", null, isRoot: true);
                return _sb.ToString();
            }

            /// <summary>Counts how often each object and array is reached, and whether one that is reached more than once is reached from its owner.</summary>
            private void Discover(Value start)
            {
                var pending = new Stack<(Value Value, object? Container)>();
                pending.Push((start, null));
                while (pending.Count > 0)
                {
                    var (v, container) = pending.Pop();
                    object? key = v.Kind switch
                    {
                        ValueKind.Class => v.AsObjectRef() is ObjectInstance { IsDestroyed: false } o ? o : null,
                        ValueKind.Array => v.AsArray(),
                        ValueKind.Buffer => v.AsBuffer(),
                        _ => null,
                    };
                    if (key == null) continue;
                    if (container != null && Owns(container, key)) _ownerInGraph.Add(key);
                    if (_seen.TryGetValue(key, out int n)) { _seen[key] = n + 1; continue; }
                    _seen[key] = 1;
                    switch (key)
                    {
                        case ObjectInstance obj:
                            if (IsList(obj))
                            {
                                var items = obj.Fields["items"];
                                long count = obj.Fields["count"].Kind == ValueKind.Int ? obj.Fields["count"].AsInt() : 0;
                                if (items.Kind == ValueKind.Array)
                                    for (long i = Math.Min(count, items.AsArray().Length) - 1; i >= 0; i--) pending.Push((items.AsArray().Items[i], items.AsArray()));
                                break;
                            }
                            var fields = FieldsOf(obj.RtClass!);
                            for (int i = fields.Count - 1; i >= 0; i--) pending.Push((obj.Fields[fields[i].Name], obj));
                            break;
                        case ScriptArray array:
                            for (int i = array.Length - 1; i >= 0; i--) pending.Push((array.Items[i], array));
                            break;
                    }
                }
                int next = 1;
                foreach (var (key, count) in _seen)
                    if (count > 1) _ids[key] = next++;
            }

            private static bool Owns(object container, object item) => item switch
            {
                ObjectInstance o => ReferenceEquals(o.Owner, container),
                IOwnedLeaf leaf => ReferenceEquals(leaf.LeafOwner, container),
                _ => false,
            };

            private void NewLine()
            {
                if (!_indent) return;
                _sb.Append('\n').Append(' ', _depth * 2);
            }

            private void Key(string name, ref bool first)
            {
                if (!first) _sb.Append(',');
                first = false;
                NewLine();
                WriteString(name);
                _sb.Append(_indent ? ": " : ":");
            }

            /// <summary>Writes a value into a slot of declared type `type` ("" untyped) with the declared unit `unit`, inside `container`.</summary>
            private void WriteValue(Value v, string type, object? container, bool isRoot = false, Unit? unit = null)
            {
                switch (v.Kind)
                {
                    case ValueKind.Undefined: _sb.Append("null"); return;
                    case ValueKind.Bool: _sb.Append(v.AsBool() ? "true" : "false"); return;
                    case ValueKind.Int:
                    case ValueKind.Float:
                        WriteNumber(v, unit);
                        return;
                    case ValueKind.String: WriteString(v.AsString()); return;
                    case ValueKind.Char:
                        if (type == "char") WriteString(v.AsChar().ToString());
                        else { _sb.Append("{\"$char\":"); WriteString(v.AsChar().ToString()); _sb.Append('}'); }
                        return;
                    case ValueKind.Buffer:
                    {
                        var buffer = v.AsBuffer();
                        if (_ids.TryGetValue(buffer, out int bid) && !_written.Add(buffer)) { _sb.Append("{\"$ref\":").Append(bid).Append('}'); return; }
                        string b64 = Convert.ToBase64String(buffer.Bytes);
                        if ((type == "byte[]" || type == "buffer") && !_ids.ContainsKey(buffer)) { WriteString(b64); return; }
                        _sb.Append('{');
                        if (_ids.TryGetValue(buffer, out int id)) _sb.Append("\"$id\":").Append(id).Append(',');
                        _sb.Append("\"$buffer\":");
                        WriteString(b64);
                        _sb.Append('}');
                        return;
                    }
                    case ValueKind.Array:
                        WriteArray(v.AsArray(), ElementType(type), container);
                        return;
                    case ValueKind.Class:
                        if (v.AsObjectRef() is not ObjectInstance obj || obj.IsDestroyed) { _sb.Append("null"); return; }
                        WriteObject(obj, type, container, isRoot);
                        return;
                    default:
                        _sb.Append("null");   // a lambda or pointer in an array (a field of that kind is left out)
                        return;
                }
            }

            private void WriteNumber(Value v, Unit? fieldUnit)
            {
                var unit = v.Unit;
                bool plain = unit == null || unit.IsUnitless || fieldUnit != null && unit.Equals(fieldUnit);
                if (!plain) _sb.Append("{\"$value\":");
                if (v.Kind == ValueKind.Int) _sb.Append(v.AsInt().ToString(CultureInfo.InvariantCulture));
                else
                {
                    double d = v.AsFloat();
                    if (double.IsNaN(d) || double.IsInfinity(d)) WriteString(double.IsNaN(d) ? "NaN" : d > 0 ? "Infinity" : "-Infinity");
                    else _sb.Append(FormatFloat(d));
                }
                if (!plain)
                {
                    _sb.Append(",\"$unit\":");
                    WriteString(unit!.ToString());
                    _sb.Append('}');
                }
            }

            private void WriteArray(ScriptArray array, string elementType, object? container)
            {
                if (_ids.TryGetValue(array, out int id))
                {
                    if (!CanDefine(array, container)) { _sb.Append("{\"$ref\":").Append(id).Append('}'); return; }
                    _written.Add(array);
                    _sb.Append("{\"$id\":").Append(id).Append(_indent ? ", \"$values\": " : ",\"$values\":");
                    WriteItems(array.Items, array.Length, elementType, array);
                    _sb.Append('}');
                    return;
                }
                WriteItems(array.Items, array.Length, elementType, array);
            }

            private void WriteItems(Value[] items, long count, string elementType, object container)
            {
                _sb.Append('[');
                _depth++;
                for (long i = 0; i < count; i++)
                {
                    if (i > 0) _sb.Append(',');
                    NewLine();
                    var item = items[i];
                    if (item.Kind is ValueKind.Lambda or ValueKind.Pointer) { _sb.Append("null"); continue; }
                    WriteValue(item, elementType, container);
                }
                _depth--;
                if (count > 0) NewLine();
                _sb.Append(']');
            }

            /// <summary>May the object/array be written out here? Once, at its owner when its owner reaches it, otherwise at its first occurrence.</summary>
            private bool CanDefine(object key, object? container)
            {
                if (_written.Contains(key)) return false;
                if (_ownerInGraph.Contains(key) && container != null && !Owns(container, key)) return false;
                return true;
            }

            private void WriteObject(ObjectInstance obj, string type, object? container, bool isRoot)
            {
                if (obj.Mailbox != null || obj.RtClass?.IsActor == true) throw new JsonError($"An actor ('{obj.ClassName}') cannot be written to JSON.");
                bool shared = _ids.TryGetValue(obj, out int id);
                if (shared && !(isRoot || CanDefine(obj, container))) { _sb.Append("{\"$ref\":").Append(id).Append('}'); return; }
                if (shared) _written.Add(obj);
                bool withType = _typeNames && (isRoot || type.Length == 0 || Plain(type) != GenericClassNames.PlainName(obj.ClassName)
                    || SplitGeneric(type).Args.Length == 0 && obj.TypeArgs is { Length: > 0 });
                if (IsJsonObject(obj)) withType = false;

                if (IsList(obj))
                {
                    var items = obj.Fields["items"];
                    long count = obj.Fields["count"].Kind == ValueKind.Int ? obj.Fields["count"].AsInt() : 0;
                    string elementType = obj.TypeArgs is [var arg] ? arg : ElementType(type);
                    var array = items.Kind == ValueKind.Array ? items.AsArray() : new ScriptArray(0);
                    count = Math.Min(count, array.Length);
                    if (!shared && !withType) { WriteItems(array.Items, count, elementType, array); return; }
                    _sb.Append('{');
                    bool firstL = true;
                    _depth++;
                    if (withType) { Key("$type", ref firstL); WriteString(TypeName(obj)); }
                    if (shared) { Key("$id", ref firstL); _sb.Append(id); }
                    Key("$values", ref firstL);
                    WriteItems(array.Items, count, elementType, array);
                    _depth--;
                    NewLine();
                    _sb.Append('}');
                    return;
                }

                _sb.Append('{');
                bool first = true;
                _depth++;
                if (withType) { Key("$type", ref first); WriteString(TypeName(obj)); }
                if (shared) { Key("$id", ref first); _sb.Append(id); }
                if (IsJsonObject(obj))
                {
                    var keys = obj.Fields["keys"];
                    var values = obj.Fields["values"];
                    long n = obj.Fields["count"].Kind == ValueKind.Int ? obj.Fields["count"].AsInt() : 0;
                    for (long i = 0; i < n && keys.Kind == ValueKind.Array && values.Kind == ValueKind.Array; i++)
                    {
                        Key(keys.AsArray().Items[i].ToString(), ref first);
                        WriteValue(values.AsArray().Items[i], "", values.AsArray());
                    }
                }
                else
                {
                    foreach (var field in FieldsOf(obj.RtClass!))
                    {
                        var value = obj.Fields[field.Name];
                        if (value.Kind is ValueKind.Lambda or ValueKind.Pointer) continue;
                        Key(field.JsonName, ref first);
                        WriteValue(value, Substitute(field.Type, obj), obj, unit: field.Unit);
                    }
                }
                _depth--;
                if (!first) NewLine();
                _sb.Append('}');
            }

            private void WriteString(string s) => JsonMethods.WriteString(_sb, s);
        }

        internal static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        /// <summary>A float as JSON: the shortest text that reads back as the same number, always with a `.` or an exponent.</summary>
        public static string FormatFloat(double d)
        {
            string text = d.ToString("R", CultureInfo.InvariantCulture);
            if (text.IndexOfAny(new[] { '.', 'E', 'e' }) < 0) text += ".0";
            return text;
        }

        // ---------------------------------------------------------------------------------------------------------------------------------
        // Parsing (a small JSON parser of its own: the same errors in the VM and in native programs)
        // ---------------------------------------------------------------------------------------------------------------------------------

        internal abstract class Node { }
        internal sealed class ObjectNode : Node { public readonly List<KeyValuePair<string, Node>> Members = new(); public Node? Get(string key) { foreach (var m in Members) if (m.Key == key) return m.Value; return null; } }
        internal sealed class ArrayNode : Node { public readonly List<Node> Items = new(); }
        internal sealed class StringNode : Node { public readonly string Value; public StringNode(string v) => Value = v; }
        internal sealed class NumberNode : Node { public readonly string Text; public readonly bool IsInt; public NumberNode(string t, bool isInt) { Text = t; IsInt = isInt; } }
        internal sealed class BoolNode : Node { public readonly bool Value; public BoolNode(bool v) => Value = v; }
        internal sealed class NullNode : Node { public static readonly NullNode Instance = new(); }

        internal static class JsonParser
        {
            public static Node Parse(string text)
            {
                int pos = 0;
                var node = ParseValue(text, ref pos, 0);
                SkipSpace(text, ref pos);
                if (pos < text.Length) throw Fail(text, pos, "Unexpected text after the JSON value");
                return node;
            }

            private static JsonError Fail(string text, int pos, string what)
            {
                int line = 1, column = 1;
                for (int i = 0; i < pos && i < text.Length; i++) { if (text[i] == '\n') { line++; column = 1; } else column++; }
                return new JsonError($"{what} (line {line}, column {column}).");
            }

            private static void SkipSpace(string text, ref int pos)
            {
                while (pos < text.Length && text[pos] is ' ' or '\t' or '\n' or '\r') pos++;
            }

            private static Node ParseValue(string text, ref int pos, int depth)
            {
                if (depth > 512) throw Fail(text, pos, "JSON nested too deeply");
                SkipSpace(text, ref pos);
                if (pos >= text.Length) throw Fail(text, pos, "Unexpected end of the JSON text");
                char c = text[pos];
                switch (c)
                {
                    case '{':
                    {
                        pos++;
                        var obj = new ObjectNode();
                        SkipSpace(text, ref pos);
                        if (pos < text.Length && text[pos] == '}') { pos++; return obj; }
                        while (true)
                        {
                            SkipSpace(text, ref pos);
                            if (pos >= text.Length || text[pos] != '"') throw Fail(text, pos, "Expected a name in quotes");
                            string key = ParseString(text, ref pos);
                            SkipSpace(text, ref pos);
                            if (pos >= text.Length || text[pos] != ':') throw Fail(text, pos, "Expected ':' after a name");
                            pos++;
                            obj.Members.Add(new KeyValuePair<string, Node>(key, ParseValue(text, ref pos, depth + 1)));
                            SkipSpace(text, ref pos);
                            if (pos < text.Length && text[pos] == ',') { pos++; continue; }
                            if (pos < text.Length && text[pos] == '}') { pos++; return obj; }
                            throw Fail(text, pos, "Expected ',' or '}' in an object");
                        }
                    }
                    case '[':
                    {
                        pos++;
                        var array = new ArrayNode();
                        SkipSpace(text, ref pos);
                        if (pos < text.Length && text[pos] == ']') { pos++; return array; }
                        while (true)
                        {
                            array.Items.Add(ParseValue(text, ref pos, depth + 1));
                            SkipSpace(text, ref pos);
                            if (pos < text.Length && text[pos] == ',') { pos++; continue; }
                            if (pos < text.Length && text[pos] == ']') { pos++; return array; }
                            throw Fail(text, pos, "Expected ',' or ']' in an array");
                        }
                    }
                    case '"': return new StringNode(ParseString(text, ref pos));
                    case 't' when string.CompareOrdinal(text, pos, "true", 0, 4) == 0: pos += 4; return new BoolNode(true);
                    case 'f' when string.CompareOrdinal(text, pos, "false", 0, 5) == 0: pos += 5; return new BoolNode(false);
                    case 'n' when string.CompareOrdinal(text, pos, "null", 0, 4) == 0: pos += 4; return NullNode.Instance;
                }
                if (c == '-' || c >= '0' && c <= '9')
                {
                    int start = pos;
                    bool isInt = true;
                    if (text[pos] == '-') pos++;
                    while (pos < text.Length && char.IsAsciiDigit(text[pos])) pos++;
                    if (pos < text.Length && text[pos] == '.') { isInt = false; pos++; while (pos < text.Length && char.IsAsciiDigit(text[pos])) pos++; }
                    if (pos < text.Length && text[pos] is 'e' or 'E')
                    {
                        isInt = false;
                        pos++;
                        if (pos < text.Length && text[pos] is '+' or '-') pos++;
                        while (pos < text.Length && char.IsAsciiDigit(text[pos])) pos++;
                    }
                    string number = text.Substring(start, pos - start);
                    if (number is "-" or "") throw Fail(text, start, "Invalid number");
                    return new NumberNode(number, isInt);
                }
                throw Fail(text, pos, $"Unexpected character '{c}'");
            }

            private static string ParseString(string text, ref int pos)
            {
                int start = pos;
                pos++;   // '"'
                var sb = new StringBuilder();
                while (true)
                {
                    if (pos >= text.Length) throw Fail(text, start, "Unterminated string");
                    char c = text[pos++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (pos >= text.Length) throw Fail(text, start, "Unterminated string");
                    char e = text[pos++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (pos + 4 > text.Length || !int.TryParse(text.AsSpan(pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                                throw Fail(text, pos, "Invalid \\u escape");
                            sb.Append((char)code);
                            pos += 4;
                            break;
                        default: throw Fail(text, pos - 1, $"Invalid escape '\\{e}'");
                    }
                }
            }
        }

        // ---------------------------------------------------------------------------------------------------------------------------------
        // Reading: the parsed tree into fire values
        // ---------------------------------------------------------------------------------------------------------------------------------

        private sealed class Reader
        {
            private readonly IJsonHost _host;
            private readonly Dictionary<long, Value> _byId = new();
            private readonly List<(object Container, object Slot, long Id)> _fixups = new();   // a `$ref` to an object that is written out later
            public readonly List<ObjectInstance> Hooks = new();
            private bool _plain;

            public Reader(IJsonHost host) => _host = host;

            public Value Run(Node root, string type, bool plain = false)
            {
                _plain = plain;
                var owner = _host.CurrentOwner;
                var value = Build(root, type, owner, "$", null);
                foreach (var (container, slot, id) in _fixups)
                {
                    if (!_byId.TryGetValue(id, out var target)) throw new JsonError($"'$ref': {id} names no object of the JSON text.");
                    switch (container)
                    {
                        case ObjectInstance obj: obj.Fields[(string)slot] = target; break;
                        case ScriptArray array: array.Items[(int)slot] = target; break;
                    }
                }
                return value;
            }

            /// <summary>The value of a node for a slot of declared type `type` ("" untyped); `owner` owns what is made (the containing object or array).</summary>
            private Value Build(Node node, string type, IOwner owner, string path, Unit? unit, (object Container, object Slot)? slot = null)
            {
                string bare = type.StartsWith("#", StringComparison.Ordinal) ? type.Substring(1) : type;
                switch (node)
                {
                    case NullNode: return Value.MakeUndefined();
                    case BoolNode b:
                        if (bare is "" or "bool" or "any") return Value.MakeBool(b.Value);
                        throw Mismatch(path, bare, "true/false");
                    case NumberNode n: return Number(n.Text, n.IsInt, bare, unit, path);
                    case StringNode s: return FromString(s.Value, bare, owner, path, unit);
                    case ArrayNode a: return BuildArray(a, bare, owner, path, null);
                    case ObjectNode o: return BuildObject(o, bare, owner, path, unit, slot);
                }
                return Value.MakeUndefined();
            }

            private static JsonError Mismatch(string path, string type, string found) => new($"{path}: expected {Describe(type)}, found {found}.");

            private static string Describe(string type) => type.Length == 0 ? "a value" : type.EndsWith("[]", StringComparison.Ordinal) ? "an array" : $"'{type}'";

            private Value Number(string text, bool isInt, string type, Unit? unit, string path)
            {
                switch (type)
                {
                    case "int": case "byte": case "long": case "short":
                    case "int8": case "int16": case "int32": case "int64": case "uint8": case "uint16": case "uint32": case "uint64":
                        if (isInt && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long i)) return Value.MakeInt(i, unit);
                        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double asDouble) && asDouble == Math.Floor(asDouble) && Math.Abs(asDouble) < 9.2e18)
                            return Value.MakeInt((long)asDouble, unit);
                        throw new JsonError($"{path}: expected a whole number, found {text}.");
                    case "float": case "double": case "float32": case "float64":
                        return Value.MakeFloat(double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture), unit);
                    case "": case "any":
                        if (isInt && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l)) return Value.MakeInt(l, unit);
                        return Value.MakeFloat(double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture), unit);
                }
                throw Mismatch(path, type, text);
            }

            private Value FromString(string s, string type, IOwner owner, string path, Unit? unit)
            {
                switch (type)
                {
                    case "": case "any": case "string": return Value.MakeString(s);
                    case "char":
                        if (s.Length == 1) return Value.MakeChar(s[0]);
                        throw new JsonError($"{path}: expected one character, found \"{s}\".");
                    case "float": case "double": case "float32": case "float64":
                        if (s is "NaN") return Value.MakeFloat(double.NaN, unit);
                        if (s is "Infinity") return Value.MakeFloat(double.PositiveInfinity, unit);
                        if (s is "-Infinity") return Value.MakeFloat(double.NegativeInfinity, unit);
                        break;
                    case "byte[]": case "buffer":
                        try
                        {
                            var buffer = new ByteBuffer(Convert.FromBase64String(s), ByteOrder.Little);
                            LeafOwnership.Adopt(buffer, owner);
                            return Value.MakeBuffer(buffer);
                        }
                        catch (FormatException) { throw new JsonError($"{path}: expected base64 bytes."); }
                }
                throw Mismatch(path, type, "a string");
            }

            private Value BuildArray(ArrayNode a, string type, IOwner owner, string path, long? id)
            {
                if (type.EndsWith("[]", StringComparison.Ordinal) || type is "" or "any" or "class")
                {
                    string element = type.EndsWith("[]", StringComparison.Ordinal) ? type.Substring(0, type.Length - 2) : "";
                    if (element == "byte" && !_plain)
                    {
                        // a byte array written as numbers: a buffer
                        var bytes = new byte[a.Items.Count];
                        for (int i = 0; i < bytes.Length; i++)
                            bytes[i] = a.Items[i] is NumberNode n && int.TryParse(n.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int b) && b is >= 0 and <= 255
                                ? (byte)b : throw new JsonError($"{path}[{i}]: expected a byte.");
                        var buffer = new ByteBuffer(bytes, ByteOrder.Little);
                        LeafOwnership.Adopt(buffer, owner);
                        var bv = Value.MakeBuffer(buffer);
                        if (id is { } bid) _byId[bid] = bv;
                        return bv;
                    }
                    var array = new ScriptArray(a.Items.Count);
                    LeafOwnership.Adopt(array, owner);
                    var value = Value.MakeArray(array);
                    if (id is { } aid) _byId[aid] = value;
                    for (int i = 0; i < a.Items.Count; i++)
                        array.Items[i] = Build(a.Items[i], element, array, $"{path}[{i}]", null, (array, i));
                    return value;
                }
                if (Plain(type) == "List" && !_plain)
                    return BuildList(a, type, owner, path, id);
                throw Mismatch(path, type, "an array");
            }

            private Value BuildList(ArrayNode a, string type, IOwner owner, string path, long? id)
            {
                var rc = _host.FindClass("List") ?? throw new JsonError($"{path}: the class 'List' is not known.");
                var list = new ObjectInstance(rc.Name, owner, rc);
                string element = SplitGeneric(type).Args is [var arg] ? arg : "";
                if (element.Length > 0) list.TypeArgs = new[] { element };
                var value = Value.MakeClassRef(list);
                if (id is { } lid) _byId[lid] = value;
                var items = new ScriptArray(Math.Max(8, a.Items.Count));
                LeafOwnership.Adopt(items, list);
                for (int i = 0; i < a.Items.Count; i++)
                    items.Items[i] = Build(a.Items[i], element, items, $"{path}[{i}]", null, (items, i));
                for (int i = a.Items.Count; i < items.Length; i++) items.Items[i] = Value.MakeUndefined();
                list.Fields["items"] = Value.MakeArray(items);
                list.Fields["count"] = Value.MakeInt(a.Items.Count);
                return value;
            }

            private Value BuildObject(ObjectNode o, string type, IOwner owner, string path, Unit? unit, (object Container, object Slot)? slot)
            {
                if (!_plain)
                {
                    if (o.Get("$ref") is { } refNode)
                    {
                        long refId = Id(refNode, path);
                        if (_byId.TryGetValue(refId, out var known)) return known;
                        if (slot is not { } s) throw new JsonError($"{path}: '$ref' to an object that is not written out before.");
                        _fixups.Add((s.Container, s.Slot, refId));
                        return Value.MakeUndefined();
                    }
                    long? id = o.Get("$id") is { } idNode ? Id(idNode, path) : null;
                    if (o.Get("$value") is { } numberNode)
                    {
                        var withUnit = o.Get("$unit") is StringNode u ? ParseUnit(u.Value, path) : unit;
                        return numberNode is NumberNode nn ? Number(nn.Text, nn.IsInt, type, withUnit, path) : throw new JsonError($"{path}: '$value' is no number.");
                    }
                    if (o.Get("$char") is StringNode ch)
                        return ch.Value.Length == 1 ? Value.MakeChar(ch.Value[0]) : throw new JsonError($"{path}: '$char' must be one character.");
                    if (o.Get("$buffer") is StringNode b64)
                    {
                        var v = FromString(b64.Value, "byte[]", owner, path, null);
                        if (id is { } bid) _byId[bid] = v;
                        return v;
                    }
                    string declared = o.Get("$type") is StringNode t ? t.Value : type;
                    if (o.Get("$values") is ArrayNode values)
                        return BuildArray(values, declared.Length > 0 && Plain(declared) == "List" ? declared : type.Length > 0 ? type : "", owner, path, id);
                    if (declared is "" or "any" or "class" or "JsonObject") return BuildJsonObject(o, owner, path, id);
                    return BuildInstance(o, declared, owner, path, id);
                }
                return BuildJsonObject(o, owner, path, null);
            }

            private static long Id(Node node, string path) =>
                node is NumberNode n && long.TryParse(n.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long id) ? id : throw new JsonError($"{path}: an id must be a whole number.");

            private static Unit ParseUnit(string text, string path)
            {
                try { return Unit.Parse(text); }
                catch (Exception) { throw new JsonError($"{path}: unknown unit '{text}'."); }
            }

            /// <summary>A class name of JSON (`Point`, `Box<int>`) as the program knows it: the generic class has the number of its type parameters in the name
            /// when a class of the same name without them exists (GenericClassNames).</summary>
            private RuntimeClass? ClassFor(string name, out string[]? typeArgs)
            {
                var (plain, args) = SplitGeneric(name);
                typeArgs = args.Length > 0 ? args : null;
                if (args.Length > 0 && _host.FindClass(GenericClassNames.Mangle(plain, args.Length)) is { } generic) return generic;
                return _host.FindClass(plain);
            }

            private Value BuildInstance(ObjectNode o, string type, IOwner owner, string path, long? id)
            {
                var rc = ClassFor(type, out var typeArgs);
                if (rc == null)
                {
                    // an interface or a base class declared for the slot without `$type`: not known which class to make
                    throw new JsonError($"{path}: unknown class '{type}'.");
                }
                if (rc.IsActor) throw new JsonError($"{path}: an actor ('{rc.Name}') cannot be read from JSON.");
                var obj = new ObjectInstance(rc.Name, owner, rc);
                if (typeArgs != null) obj.TypeArgs = typeArgs;
                var value = Value.MakeClassRef(obj);
                if (id is { } oid) _byId[oid] = value;
                var fields = FieldsOf(rc);
                foreach (var (key, child) in o.Members)
                {
                    if (key.StartsWith("$", StringComparison.Ordinal)) continue;
                    var field = fields.FirstOrDefault(f => f.JsonName == key) ?? fields.FirstOrDefault(f => string.Equals(f.JsonName, key, StringComparison.OrdinalIgnoreCase));
                    if (field == null) continue;   // (a member the class does not have: left out)
                    obj.Fields[field.Name] = Build(child, Substitute(field.Type, obj), obj, path + "." + key, field.Unit, (obj, field.Name));
                }
                if (HasOnDeserialized(rc)) Hooks.Add(obj);
                return value;
            }

            private Value BuildJsonObject(ObjectNode o, IOwner owner, string path, long? id)
            {
                var rc = _host.FindClass("JsonObject") ?? throw new JsonError($"{path}: an object without '$type' needs a class to read into (or the class JsonObject of `#import \"json\"`).");
                var obj = new ObjectInstance(rc.Name, owner, rc);
                var value = Value.MakeClassRef(obj);
                if (id is { } oid) _byId[oid] = value;
                var members = o.Members.Where(m => _plain || !m.Key.StartsWith("$", StringComparison.Ordinal)).ToList();
                var keys = new ScriptArray(Math.Max(4, members.Count));
                var values = new ScriptArray(Math.Max(4, members.Count));
                LeafOwnership.Adopt(keys, obj);
                LeafOwnership.Adopt(values, obj);
                for (int i = 0; i < keys.Length; i++) { keys.Items[i] = Value.MakeUndefined(); values.Items[i] = Value.MakeUndefined(); }
                for (int i = 0; i < members.Count; i++)
                {
                    keys.Items[i] = Value.MakeString(members[i].Key);
                    values.Items[i] = Build(members[i].Value, "", values, path + "." + members[i].Key, null, (values, i));
                }
                obj.Fields["keys"] = Value.MakeArray(keys);
                obj.Fields["values"] = Value.MakeArray(values);
                obj.Fields["count"] = Value.MakeInt(members.Count);
                return value;
            }
        }
    }
}
