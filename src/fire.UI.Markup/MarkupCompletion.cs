using System.Text.RegularExpressions;

namespace fire.UI.Markup
{
    /// <summary>What a suggestion of <see cref="MarkupCompletion"/> completes.</summary>
    public enum MarkupCompletionKind { Element, Attribute, Value, Extension, Namespace }

    /// <summary>One suggestion: <paramref name="Text"/> replaces the text from <see cref="MarkupCompletionResult.ReplaceStart"/> to the caret; afterwards the caret goes
    /// <paramref name="CaretBack"/> characters back (into the quotes of `name=""`). <paramref name="Display"/> is what the list shows (null: the text).</summary>
    public sealed record MarkupCompletionItem(string Text, MarkupCompletionKind Kind, string? Detail = null, int CaretBack = 0, string? Display = null)
    {
        public string Label => Display ?? Text;
    }

    /// <summary>The suggestions at a caret: what they replace (from <see cref="ReplaceStart"/> to the caret) and the items, the best first.</summary>
    public sealed record MarkupCompletionResult(int ReplaceStart, IReadOnlyList<MarkupCompletionItem> Items)
    {
        public static readonly MarkupCompletionResult None = new(0, Array.Empty<MarkupCompletionItem>());
    }

    /// <summary>IntelliSense for markup files (.fxml, docs/UI_MARKUP.md): which elements fit where the caret is, the attributes of the element, and the values of an attribute
    /// (by the type of the property, the keys of the resources, the options of a binding, the namespace prefixes). Works on a text that is being typed (not well formed), without
    /// the editor: a small scanner reads the text up to the caret.</summary>
    public static class MarkupCompletion
    {
        private sealed class Open
        {
            public string Tag = "";
            public Dictionary<string, string> Attributes = new(StringComparer.OrdinalIgnoreCase);
        }

        private enum State { Text, TagName, CloseTagName, InTag, AttributeName, AfterAttributeName, AfterEquals, Value }

        private static readonly string[] ResourceKinds = { "Converter", "Style", "ControlTemplate", "DataTemplate", "CollectionView" };
        private static readonly string[] Extensions = { "Binding", "TemplateBinding", "Enum", "Static", "Expr" };

        public static MarkupCompletionResult Suggest(string text, int offset)
        {
            offset = Math.Clamp(offset, 0, text.Length);
            var stack = new List<Open>();
            var state = State.Text;
            Open? tag = null;          // the tag being written (start tag)
            int wordStart = 0;         // start of the name being written
            string attribute = "";     // the attribute whose value is being written
            int valueStart = 0;
            char quote = '"';

            int i = 0;
            while (i < offset)
            {
                char c = text[i];
                switch (state)
                {
                    case State.Text:
                        if (c == '<')
                        {
                            if (Starts(text, i, "<!--")) { int end = text.IndexOf("-->", i + 4, StringComparison.Ordinal); if (end < 0 || end + 3 > offset) return MarkupCompletionResult.None; i = end + 3; continue; }
                            if (Starts(text, i, "<?") || Starts(text, i, "<!")) { int end = text.IndexOf('>', i); if (end < 0 || end + 1 > offset) return MarkupCompletionResult.None; i = end + 1; continue; }
                            if (i + 1 < text.Length && text[i + 1] == '/') { state = State.CloseTagName; wordStart = i + 2; i += 2; continue; }
                            state = State.TagName;
                            tag = new Open();
                            wordStart = i + 1;
                        }
                        break;
                    case State.CloseTagName:
                        if (c == '>')
                        {
                            string name = text.Substring(wordStart, i - wordStart).Trim();
                            int at = stack.FindLastIndex(o => o.Tag == name);
                            if (at >= 0) stack.RemoveRange(at, stack.Count - at);
                            state = State.Text;
                        }
                        break;
                    case State.TagName:
                        if (char.IsWhiteSpace(c) || c == '/' || c == '>')
                        {
                            tag!.Tag = text.Substring(wordStart, i - wordStart);
                            state = State.InTag;
                            continue; // the same character again, in the tag
                        }
                        break;
                    case State.InTag:
                        if (c == '>')
                        {
                            bool selfClosing = i > 0 && text[i - 1] == '/';
                            if (!selfClosing) stack.Add(tag!);
                            state = State.Text;
                        }
                        else if (IsNameChar(c)) { state = State.AttributeName; wordStart = i; }
                        break;
                    case State.AttributeName:
                        if (!IsNameChar(c))
                        {
                            attribute = text.Substring(wordStart, i - wordStart);
                            state = State.AfterAttributeName;
                            continue;
                        }
                        break;
                    case State.AfterAttributeName:
                        if (c == '=') state = State.AfterEquals;
                        else if (!char.IsWhiteSpace(c)) { state = State.InTag; continue; }
                        break;
                    case State.AfterEquals:
                        if (c is '"' or '\'') { quote = c; state = State.Value; valueStart = i + 1; }
                        else if (!char.IsWhiteSpace(c)) { state = State.InTag; continue; }
                        break;
                    case State.Value:
                        if (c == quote)
                        {
                            tag!.Attributes[attribute] = text.Substring(valueStart, i - valueStart);
                            state = State.InTag;
                        }
                        break;
                }
                i++;
            }

            var info = DocumentInfo.Read(text);
            var parent = stack.Count > 0 ? stack[^1] : null;
            switch (state)
            {
                case State.TagName:
                    return new MarkupCompletionResult(wordStart, Rank(ElementsIn(parent, stack), text.Substring(wordStart, offset - wordStart)));
                case State.CloseTagName:
                    return parent == null ? MarkupCompletionResult.None
                        : new MarkupCompletionResult(wordStart, new[] { new MarkupCompletionItem(parent.Tag + ">", MarkupCompletionKind.Element, "closes <" + parent.Tag + ">") });
                case State.InTag:
                    return new MarkupCompletionResult(offset, AttributesOf(tag!, stack, info).ToList());
                case State.AttributeName:
                    return new MarkupCompletionResult(wordStart, Rank(AttributesOf(tag!, stack, info), text.Substring(wordStart, offset - wordStart)));
                case State.Value:
                    return Values(tag!, attribute, stack, info, text, valueStart, offset);
                default:
                    return MarkupCompletionResult.None;
            }
        }

        private static bool Starts(string text, int at, string what) => string.CompareOrdinal(text, at, what, 0, what.Length) == 0;
        private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '.' or ':' or '-';

        /// <summary>The items whose label starts with what is typed first (then those that contain it), the rest left out.</summary>
        private static IReadOnlyList<MarkupCompletionItem> Rank(IEnumerable<MarkupCompletionItem> items, string typed)
        {
            var list = items.ToList();
            if (typed.Length == 0) return list;
            return list.Where(x => x.Label.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
                .Concat(list.Where(x => !x.Label.StartsWith(typed, StringComparison.OrdinalIgnoreCase) && x.Label.Contains(typed, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        // ---- elements ----

        private static IEnumerable<MarkupElementName> InterfaceElements() =>
            MarkupSchema.Elements.Where(e => !e.IsPart).Select(e => new MarkupElementName(e.Tag, e.Class.Length > 0 ? e.Class : "part"));

        private sealed record MarkupElementName(string Tag, string Detail);

        private static IEnumerable<MarkupCompletionItem> ElementsIn(Open? parent, List<Open> stack)
        {
            IEnumerable<MarkupElementName> names;
            if (parent == null) names = new[] { new MarkupElementName("Window", "a window of its own"), new MarkupElementName("View", "a panel that a window attaches") };
            else
            {
                string tag = parent.Tag;
                if (tag.Equals("Window", StringComparison.OrdinalIgnoreCase) || tag.Equals("View", StringComparison.OrdinalIgnoreCase))
                    names = new[] { new MarkupElementName("Resources", "styles, templates, converters, views") }.Concat(InterfaceElements());
                else if (tag.Equals("Resources", StringComparison.OrdinalIgnoreCase)) names = ResourceKinds.Select(k => new MarkupElementName(k, "resource"));
                else if (tag.Equals("Style", StringComparison.OrdinalIgnoreCase)) names = new[] { new MarkupElementName("Setter", "a value"), new MarkupElementName("Trigger", "values while a condition holds") };
                else if (tag.Equals("Trigger", StringComparison.OrdinalIgnoreCase)) names = new[] { new MarkupElementName("Condition", "another condition"), new MarkupElementName("Setter", "a value") };
                else if (tag.Equals("ControlTemplate", StringComparison.OrdinalIgnoreCase)) names = new[] { new MarkupElementName("Trigger", "values while a condition holds") }.Concat(InterfaceElements());
                else if (tag.Equals("DataTemplate", StringComparison.OrdinalIgnoreCase)) names = InterfaceElements();
                else if (MarkupSchema.Find(tag) is { } def)
                    names = def.Children switch
                    {
                        ChildMode.Parts => def.PartTags.Select(p => new MarkupElementName(p, "part of " + def.Tag)),
                        ChildMode.Elements or ChildMode.Single => InterfaceElements(),
                        _ => Array.Empty<MarkupElementName>(),
                    };
                else names = Array.Empty<MarkupElementName>();
            }
            return names.Select(n => new MarkupCompletionItem(n.Tag, MarkupCompletionKind.Element, n.Detail));
        }

        // ---- attributes ----

        private static MarkupCompletionItem Attribute(string name, string? detail) => new(name + "=\"\"", MarkupCompletionKind.Attribute, detail, 1, name);

        private static IEnumerable<MarkupCompletionItem> AttributesOf(Open tag, List<Open> stack, DocumentInfo info)
        {
            var items = new List<MarkupCompletionItem>();
            string t = tag.Tag;
            bool inTemplate = stack.Any(o => o.Tag.Equals("ControlTemplate", StringComparison.OrdinalIgnoreCase));
            void Add(string name, string? detail) { if (!tag.Attributes.ContainsKey(name)) items.Add(Attribute(name, detail)); }

            if (t.Equals("Window", StringComparison.OrdinalIgnoreCase) || t.Equals("View", StringComparison.OrdinalIgnoreCase))
            {
                Add("class", "the class of the code-behind");
                Add("base", "the name of the generated base class");
                Add("namespace", "the namespace of the markup (local:)");
                items.Add(new MarkupCompletionItem("namespace:", MarkupCompletionKind.Namespace, "binds another namespace to a prefix: namespace:m=\"App.Models\"", 0, "namespace:prefix"));
                if (t.Equals("Window", StringComparison.OrdinalIgnoreCase)) Add("title", "the title of the window");
                Add("width", "pixels");
                Add("height", "pixels");
                return items;
            }
            string[]? fixedAttributes = t.ToLowerInvariant() switch
            {
                "converter" => new[] { "key", "type" },
                "style" => new[] { "key", "target", "basedOn" },
                "setter" => inTemplate ? new[] { "property", "value", "target" } : new[] { "property", "value" },
                "trigger" or "condition" => inTemplate ? new[] { "property", "value", "source" } : new[] { "property", "value" },
                "controltemplate" => new[] { "key", "target" },
                "datatemplate" => new[] { "key" },
                "resources" => Array.Empty<string>(),
                _ => null,
            };
            if (t.Equals("CollectionView", StringComparison.OrdinalIgnoreCase))
                fixedAttributes = MarkupSchema.CollectionViewDef.Properties.Select(p => p.Name).Prepend("key").ToArray();
            if (fixedAttributes != null)
            {
                foreach (var a in fixedAttributes) Add(a, null);
                return items;
            }
            if (MarkupSchema.Find(t) is not { } def) return items;
            if (def.Class.Length > 0) Add("name", "a field of the generated class");
            foreach (var p in def.Properties) Add(p.Name, Describe(p));
            foreach (var e in def.Events) Add(e.Name, "event: the name of a method" + (e.Args.Length > 0 ? " (sender, " + string.Join(", ", e.Args) + ")" : " (sender)"));
            return items;
        }

        private static string Describe(PropertyDef p) => p.Kind switch
        {
            PropertyKind.Enum => string.Join(" | ", p.Choices ?? Array.Empty<string>()),
            PropertyKind.Color => "colour: #RRGGBB",
            PropertyKind.Pen => "pen: #RRGGBB or #RRGGBB,width",
            PropertyKind.Thickness => "4 | 4,2 | 1,2,3,4",
            PropertyKind.Orientation => "Horizontal | Vertical",
            PropertyKind.StyleRef => "the key of a Style",
            PropertyKind.TemplateRef => "the key of a ControlTemplate",
            PropertyKind.DataTemplateRef => "the key of a DataTemplate",
            PropertyKind.ViewRef => "the key of a CollectionView",
            PropertyKind.Collection => "a list: {Binding ...}",
            PropertyKind.Code => "{Expr ...}",
            PropertyKind.Image => "the path of an image",
            PropertyKind.Geometry => "a symbol (Up, Home, ...) or path data",
            _ => p.Kind.ToString().ToLowerInvariant(),
        };

        // ---- values ----

        private static MarkupCompletionResult Values(Open tag, string attribute, List<Open> stack, DocumentInfo info, string text, int valueStart, int offset)
        {
            string typed = text.Substring(valueStart, offset - valueStart);

            // inside braces: a markup extension
            int brace = typed.LastIndexOf('{');
            if (brace >= 0 && typed.IndexOf('}', brace) < 0)
                return ExtensionValues(tag, stack, info, typed.Substring(brace + 1), valueStart + brace + 1);

            // an object: [Type ...] - the namespace prefixes for the type name
            if (typed.StartsWith('[') && !typed.Contains(' '))
                return new MarkupCompletionResult(valueStart + 1, Rank(Prefixes(info), typed.Substring(1)));

            string t = tag.Tag.ToLowerInvariant();
            IEnumerable<MarkupCompletionItem> items = Array.Empty<MarkupCompletionItem>();
            ElementDef? target = TargetOf(stack, tag);
            PropertyDef? property = null;
            switch (t)
            {
                case "converter" when attribute == "type":
                    items = Prefixes(info);
                    break;
                case "style" or "controltemplate" when attribute == "target":
                    items = MarkupSchema.Elements.Where(e => !e.IsPart).Select(e => e.Tag).Append("Element").Select(n => new MarkupCompletionItem(n, MarkupCompletionKind.Value));
                    break;
                case "style" when attribute == "basedOn":
                    items = info.Styles.Select(k => new MarkupCompletionItem(k, MarkupCompletionKind.Value, "style"));
                    break;
                case "setter" or "trigger" or "condition" when attribute == "property":
                    if (target != null)
                        items = target.Properties.Select(p => new MarkupCompletionItem(p.Name, MarkupCompletionKind.Value, Describe(p)))
                            .Concat(t == "setter" ? Array.Empty<MarkupCompletionItem>() : MarkupSchema.StateProperties.Select(p => new MarkupCompletionItem(p.Name, MarkupCompletionKind.Value, "state")));
                    break;
                case "setter" or "trigger" or "condition" when attribute == "value":
                    tag.Attributes.TryGetValue("property", out var propertyName);
                    property = propertyName == null ? null : target?.Property(propertyName) ?? MarkupSchema.StateProperties.FirstOrDefault(p => p.Name == propertyName);
                    break;
                default:
                    var def = t == "collectionview" ? MarkupSchema.CollectionViewDef : MarkupSchema.Find(tag.Tag);
                    if (def?.Event(attribute) != null)
                        items = info.Handlers.Select(h => new MarkupCompletionItem(h, MarkupCompletionKind.Value, "handler"));
                    else property = def?.Property(attribute);
                    break;
            }
            if (property != null) items = ValuesOf(property, info);
            return new MarkupCompletionResult(valueStart, Rank(items, typed));
        }

        /// <summary>The element whose properties a Setter/Trigger/Condition names: the target of the enclosing Style or ControlTemplate.</summary>
        private static ElementDef? TargetOf(List<Open> stack, Open tag)
        {
            foreach (var open in stack.AsEnumerable().Reverse())
                if ((open.Tag.Equals("Style", StringComparison.OrdinalIgnoreCase) || open.Tag.Equals("ControlTemplate", StringComparison.OrdinalIgnoreCase))
                    && open.Attributes.TryGetValue("target", out var target))
                    return MarkupSchema.FindTarget(target);
            return null;
        }

        private static IEnumerable<MarkupCompletionItem> ValuesOf(PropertyDef property, DocumentInfo info)
        {
            IEnumerable<MarkupCompletionItem> Words(IEnumerable<string> words, string? detail = null) => words.Select(w => new MarkupCompletionItem(w, MarkupCompletionKind.Value, detail));
            var plain = property.Kind switch
            {
                PropertyKind.Bool => Words(new[] { "true", "false" }),
                PropertyKind.Orientation => Words(new[] { "Horizontal", "Vertical" }),
                PropertyKind.Enum => Words(property.Choices ?? Array.Empty<string>()),
                PropertyKind.StyleRef => Words(info.Styles, "style"),
                PropertyKind.TemplateRef => Words(info.Templates, "control template"),
                PropertyKind.DataTemplateRef => Words(info.DataTemplates, "data template"),
                PropertyKind.ViewRef => Words(info.Views, "collection view"),
                PropertyKind.Color => Words(new[] { "#000000", "#FFFFFF" }, "colour"),
                PropertyKind.Geometry => Words(MarkupSchema.IconNames, "symbol"),
                _ => Enumerable.Empty<MarkupCompletionItem>(),
            };
            bool refKind = property.Kind is PropertyKind.StyleRef or PropertyKind.TemplateRef or PropertyKind.DataTemplateRef or PropertyKind.ViewRef;
            if (refKind) return plain;
            var extensions = new List<MarkupCompletionItem>
            {
                new("{Binding }", MarkupCompletionKind.Extension, "the value follows the data context", 1),
                new("{Enum }", MarkupCompletionKind.Extension, "an enum member of your script", 1),
                new("{Static }", MarkupCompletionKind.Extension, "a constant of your script", 1),
                new("{Expr }", MarkupCompletionKind.Extension, "any fire expression", 1),
                new("[]", MarkupCompletionKind.Extension, "a new object: [Type Arg1, Arg2]", 1, "[Type ...]"),
            };
            return plain.Concat(extensions);
        }

        private static IEnumerable<MarkupCompletionItem> Prefixes(DocumentInfo info) =>
            info.Prefixes.Select(p => new MarkupCompletionItem(p.Key + ":", MarkupCompletionKind.Namespace, p.Value)).Prepend(new MarkupCompletionItem("local:", MarkupCompletionKind.Namespace, info.OwnNamespace ?? "the namespace of the markup"));

        private static MarkupCompletionResult ExtensionValues(Open tag, List<Open> stack, DocumentInfo info, string inside, int insideStart)
        {
            // the keyword after the brace
            int space = inside.IndexOfAny(new[] { ' ', '\t' });
            if (space < 0)
                return new MarkupCompletionResult(insideStart, Rank(Extensions.Select(e => new MarkupCompletionItem(e + " ", MarkupCompletionKind.Extension, null, 0, e)), inside));
            string keyword = inside.Substring(0, space);
            int restStart = insideStart + space + 1;
            string rest = inside.Substring(space + 1);
            switch (keyword.ToLowerInvariant())
            {
                case "binding":
                {
                    // the option being written: after the last comma
                    int comma = rest.LastIndexOf(',');
                    int partStart = comma < 0 ? 0 : comma + 1;
                    while (partStart < rest.Length && rest[partStart] == ' ') partStart++;
                    string part = rest.Substring(partStart);
                    int equals = part.IndexOf('=');
                    if (equals >= 0)
                    {
                        string option = part.Substring(0, equals).Trim();
                        string value = part.Substring(equals + 1);
                        IEnumerable<MarkupCompletionItem> values = option.ToLowerInvariant() switch
                        {
                            "mode" => new[] { "OneWay", "TwoWay", "OneTime" }.Select(m => new MarkupCompletionItem(m, MarkupCompletionKind.Value)),
                            "converter" => info.Converters.Concat(MarkupSchema.BuiltInConverters.Keys).Distinct().Select(k => new MarkupCompletionItem(k, MarkupCompletionKind.Value, "converter")),
                            "elementname" => info.Names.Select(n => new MarkupCompletionItem(n, MarkupCompletionKind.Value, "element")),
                            _ => Array.Empty<MarkupCompletionItem>(),
                        };
                        return new MarkupCompletionResult(restStart + partStart + equals + 1, Rank(values, value));
                    }
                    var options = new[] { "Mode=", "Converter=", "ElementName=", "Path=" }.Select(o => new MarkupCompletionItem(o, MarkupCompletionKind.Extension, "binding option"));
                    return new MarkupCompletionResult(restStart + partStart, partStart == 0 && part.Length == 0 ? options.ToList() : Rank(options, part));
                }
                case "templatebinding":
                {
                    var target = stack.LastOrDefault(o => o.Tag.Equals("ControlTemplate", StringComparison.OrdinalIgnoreCase)) is { } template && template.Attributes.TryGetValue("target", out var name)
                        ? MarkupSchema.FindTarget(name) : null;
                    var props = (target?.Properties ?? Array.Empty<PropertyDef>()).Concat(MarkupSchema.StateProperties)
                        .Select(p => new MarkupCompletionItem(p.Name, MarkupCompletionKind.Value, Describe(p)));
                    return new MarkupCompletionResult(restStart, Rank(props, rest));
                }
                case "enum":
                case "static":
                    return rest.Contains(':') || rest.Contains('.') ? MarkupCompletionResult.None : new MarkupCompletionResult(restStart, Rank(Prefixes(info), rest));
                default:
                    return MarkupCompletionResult.None;
            }
        }

        // ---- what the document declares (read with patterns: the text is being typed) ----

        private sealed class DocumentInfo
        {
            public List<string> Styles = new(), Templates = new(), DataTemplates = new(), Views = new(), Converters = new(), Names = new(), Handlers = new();
            public Dictionary<string, string> Prefixes = new();
            public string? OwnNamespace;

            private static readonly Regex Keyed = new(@"<(Style|ControlTemplate|DataTemplate|CollectionView|Converter)\b[^>]*?\bkey\s*=\s*[""']([A-Za-z_]\w*)[""']", RegexOptions.Compiled);
            private static readonly Regex Name = new(@"\bname\s*=\s*[""']([A-Za-z_]\w*)[""']", RegexOptions.Compiled);
            private static readonly Regex Handler = new(@"\bon[A-Z]\w*\s*=\s*[""']([A-Za-z_]\w*)[""']", RegexOptions.Compiled);
            private static readonly Regex Prefix = new(@"\bnamespace:([A-Za-z_]\w*)\s*=\s*[""']([^""']*)[""']", RegexOptions.Compiled);
            private static readonly Regex Own = new(@"\bnamespace\s*=\s*[""']([^""']*)[""']", RegexOptions.Compiled);

            public static DocumentInfo Read(string text)
            {
                var info = new DocumentInfo();
                foreach (Match m in Keyed.Matches(text))
                {
                    var list = m.Groups[1].Value switch
                    {
                        "Style" => info.Styles,
                        "ControlTemplate" => info.Templates,
                        "DataTemplate" => info.DataTemplates,
                        "CollectionView" => info.Views,
                        _ => info.Converters,
                    };
                    if (!list.Contains(m.Groups[2].Value)) list.Add(m.Groups[2].Value);
                }
                foreach (Match m in Name.Matches(text)) if (!info.Names.Contains(m.Groups[1].Value)) info.Names.Add(m.Groups[1].Value);
                foreach (Match m in Handler.Matches(text)) if (!info.Handlers.Contains(m.Groups[1].Value)) info.Handlers.Add(m.Groups[1].Value);
                foreach (Match m in Prefix.Matches(text)) info.Prefixes[m.Groups[1].Value] = m.Groups[2].Value;
                if (Own.Match(text) is { Success: true } own) info.OwnNamespace = own.Groups[1].Value;
                return info;
            }
        }
    }
}
