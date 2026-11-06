using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace fire.Package.Manager
{
    /// <summary>
    /// The list of the native sources of a package or a project (`native.sources`, one list of `native.platformSources`): paths (in a project also patterns) in the
    /// order they are written. An entry can have a build order (`{ "file": "native/x.h", "order": -10 }`): all sources of a build - the platform ones first, then
    /// the common ones - are sorted by it (stable; an entry without one has order 0 and keeps its place among them). A plain string stays a plain string when the
    /// list is written again.
    /// </summary>
    [JsonConverter(typeof(NativeSourceListConverter))]
    public sealed class NativeSourceList : List<string>
    {
        private readonly Dictionary<string, int> _orders = new(StringComparer.Ordinal);

        public NativeSourceList() { }
        public NativeSourceList(IEnumerable<string> entries) : base(entries) { }

        /// <summary>The build order given for an entry (null: none, it counts as 0).</summary>
        public int? OrderOf(string entry) => _orders.TryGetValue(entry, out int o) ? o : null;

        /// <summary>Gives an entry a build order, or (null) takes it away.</summary>
        public void SetOrder(string entry, int? order)
        {
            if (order == null) _orders.Remove(entry);
            else _orders[entry] = order.Value;
        }

        /// <summary>Does an entry have a build order?</summary>
        public bool HasOrders => _orders.Keys.Any(Contains);

        /// <summary>The same list with every entry mapped (`map` gives the new path), the orders kept.</summary>
        public NativeSourceList Map(Func<string, string> map)
        {
            var result = new NativeSourceList();
            foreach (var entry in this)
            {
                string mapped = map(entry);
                result.Add(mapped);
                if (OrderOf(entry) is int o) result.SetOrder(mapped, o);
            }
            return result;
        }

        /// <summary>A copy (the orders too).</summary>
        public NativeSourceList Clone() => Map(e => e);
    }

    /// <summary>Reads an entry as a string or as `{ "file": "...", "order": n }` and writes the object form only for an entry that has an order.</summary>
    public sealed class NativeSourceListConverter : JsonConverter<NativeSourceList>
    {
        public override NativeSourceList Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("A list of native sources is a list ([\"native/x.h\", { \"file\": \"native/y.h\", \"order\": 10 }]).");
            var list = new NativeSourceList();
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.EndArray: return list;
                    case JsonTokenType.String: list.Add(reader.GetString()!); break;
                    case JsonTokenType.StartObject:
                    {
                        string? file = null;
                        int? order = null;
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                        {
                            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("A native source is \"path\" or { \"file\": \"path\", \"order\": n }.");
                            string name = reader.GetString()!;
                            reader.Read();
                            if (string.Equals(name, "file", StringComparison.OrdinalIgnoreCase))
                                file = reader.TokenType == JsonTokenType.String ? reader.GetString() : throw new JsonException("'file' of a native source is a path.");
                            else if (string.Equals(name, "order", StringComparison.OrdinalIgnoreCase))
                                order = reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int o) ? o : throw new JsonException("'order' of a native source is a whole number.");
                            else throw new JsonException($"A native source has 'file' and 'order', not '{name}'.");
                        }
                        if (string.IsNullOrWhiteSpace(file)) throw new JsonException("A native source { ... } needs 'file'.");
                        list.Add(file!);
                        if (order != null) list.SetOrder(file!, order);
                        break;
                    }
                    case JsonTokenType.Comment: break;
                    default: throw new JsonException("A native source is \"path\" or { \"file\": \"path\", \"order\": n }.");
                }
            }
            throw new JsonException("The list of native sources is not closed.");
        }

        public override void Write(Utf8JsonWriter writer, NativeSourceList value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (var entry in value)
            {
                if (value.OrderOf(entry) is int o)
                {
                    // (one line, as the plain entries: { "file": "native/x.h", "order": 10 })
                    writer.WriteRawValue("{ \"file\": " + JsonSerializer.Serialize(entry, options) + ", \"order\": " + o.ToString(System.Globalization.CultureInfo.InvariantCulture) + " }", skipInputValidation: true);
                }
                else writer.WriteStringValue(entry);
            }
            writer.WriteEndArray();
        }
    }

    /// <summary>An entry of `native.uses`: an import whose platform layer a package builds on - everywhere (`"io"`), or only on some platforms
    /// (`{ "import": "audio", "platforms": [ "posix", "windows", "vm" ] }`, the keys of `platformSources`).</summary>
    [JsonConverter(typeof(NativeUseConverter))]
    public sealed class NativeUse
    {
        public string Import { get; set; } = "";
        /// <summary>The keys the entry is for; empty: every build.</summary>
        public List<string> Platforms { get; set; } = new();

        public NativeUse() { }
        public NativeUse(string import) { Import = import; }

        /// <summary>Is the entry for a build with these keys (the platform package, the target, `vm` for the library of the VM)?</summary>
        public bool AppliesTo(IEnumerable<string> platformKeys) =>
            Platforms.Count == 0 || platformKeys.Any(k => Platforms.Any(p => string.Equals(p, k, StringComparison.OrdinalIgnoreCase)));

        public static implicit operator NativeUse(string import) => new(import);
        public override string ToString() => Platforms.Count == 0 ? Import : Import + " (" + string.Join(", ", Platforms) + ")";
    }

    /// <summary>Reads an entry of `native.uses` as a string or as `{ "import": "...", "platforms": [ ... ] }`; writes the string form when it is for every build.</summary>
    public sealed class NativeUseConverter : JsonConverter<NativeUse>
    {
        public override NativeUse Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String) return new NativeUse(reader.GetString()!);
            if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("An entry of 'uses' is \"import\" or { \"import\": \"name\", \"platforms\": [ \"posix\", ... ] }.");
            var use = new NativeUse();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("An entry of 'uses' has 'import' and 'platforms'.");
                string name = reader.GetString()!;
                reader.Read();
                if (string.Equals(name, "import", StringComparison.OrdinalIgnoreCase))
                    use.Import = reader.TokenType == JsonTokenType.String ? reader.GetString()! : throw new JsonException("'import' of an entry of 'uses' is the name of an import.");
                else if (string.Equals(name, "platforms", StringComparison.OrdinalIgnoreCase))
                    use.Platforms = JsonSerializer.Deserialize<List<string>>(ref reader, options) ?? new();
                else throw new JsonException($"An entry of 'uses' has 'import' and 'platforms', not '{name}'.");
            }
            if (string.IsNullOrWhiteSpace(use.Import)) throw new JsonException("An entry of 'uses' { ... } needs 'import'.");
            return use;
        }

        public override void Write(Utf8JsonWriter writer, NativeUse value, JsonSerializerOptions options)
        {
            if (value.Platforms.Count == 0) { writer.WriteStringValue(value.Import); return; }
            writer.WriteStartObject();
            writer.WriteString("import", value.Import);
            writer.WritePropertyName("platforms");
            JsonSerializer.Serialize(writer, value.Platforms, options);
            writer.WriteEndObject();
        }
    }

    /// <summary>
    /// The build order of the native sources and its check. The sources of an import are put into one file in that order; an `#include "x.h"` of another source of the
    /// same import is taken out there (the source is in the file already) - so whatever a source includes has to come before it. <see cref="CheckIncludes"/> says where not.
    /// </summary>
    public static class NativeSourceOrder
    {
        private static readonly Regex LocalInclude = new(@"^[ \t]*#[ \t]*include[ \t]+""([^""\r\n]+)""", RegexOptions.Multiline | RegexOptions.Compiled);

        /// <summary>The file name a source is known by in an `#include` (the last part of its path).</summary>
        public static string FileNameOf(string source) => Path.GetFileName(source.Replace('\\', '/'));

        /// <summary>Sorts the sources (in list order: the platform ones first, then the common ones) by their build order, stable.</summary>
        public static List<string> Sort(IEnumerable<(string File, int Order)> sources) =>
            sources.Select((s, i) => (s.File, s.Order, Index: i)).OrderBy(s => s.Order).ThenBy(s => s.Index).Select(s => s.File).ToList();

        /// <summary>The problems of the order (empty: fine): a source that includes another source of the list that is built after it. Each names the file and the
        /// line of the `#include`; <paramref name="display"/> gives the name shown for a source (default: as it is).</summary>
        public static List<string> CheckIncludes(IReadOnlyList<(string Name, string Text)> ordered, Func<string, string>? display = null)
        {
            display ??= n => n;
            var position = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < ordered.Count; i++) position.TryAdd(FileNameOf(ordered[i].Name), i);
            var problems = new List<string>();
            for (int i = 0; i < ordered.Count; i++)
            {
                string text = ordered[i].Text;
                foreach (Match m in LocalInclude.Matches(text))
                {
                    if (!position.TryGetValue(FileNameOf(m.Groups[1].Value), out int at) || at <= i) continue;   // (not a source of the list, or built before it - or itself)
                    int line = 1;
                    for (int k = 0; k < m.Index; k++) if (text[k] == '\n') line++;
                    problems.Add($"{display(ordered[i].Name)}:{line}: #include \"{m.Groups[1].Value}\" is a source of the same package that is built after it - "
                        + $"give '{display(ordered[at].Name)}' a smaller build order (\"order\") than '{display(ordered[i].Name)}', or list it before.");
                }
            }
            return problems;
        }

        /// <summary>The check for every build a native part can have: the common sources alone and with the sources of each key of `platformSources`.
        /// <paramref name="read"/> reads a source (null: it cannot be read, the check leaves it out).</summary>
        public static List<string> CheckAllPlatforms(PackageNative native, Func<string, string?> read, Func<string, string>? display = null)
        {
            var problems = new List<string>();
            var keys = new List<string?> { null };
            keys.AddRange(native.PlatformSources.Keys);
            foreach (var key in keys)
            {
                var files = native.SourcesFor(key == null ? Array.Empty<string>() : new[] { key });
                var texts = new List<(string, string)>();
                foreach (var f in files) if (read(f) is { } t) texts.Add((f, t));
                foreach (var p in CheckIncludes(texts, display))
                    if (!problems.Any(x => x == p || x.StartsWith(p + " (in a build for", StringComparison.Ordinal))) problems.Add(key == null ? p : p + $" (in a build for '{key}')");
            }
            return problems;
        }
    }
}
