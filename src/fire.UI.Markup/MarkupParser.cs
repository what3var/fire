using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace fire.UI.Markup
{
    /// <summary>Reads a markup file (XML) into a <see cref="MarkupDocument"/>. Mistakes do not stop the reading: they are collected in <see cref="MarkupDocument.Diagnostics"/> with their
    /// line (a design view can show them next to the picture); the document is usable (for a preview) as far as it could be read.</summary>
    public static class MarkupParser
    {
        public static MarkupDocument Parse(string text)
        {
            var errors = new List<MarkupDiagnostic>();
            XDocument xml;
            try
            {
                using var reader = new StringReader(text);
                xml = XDocument.Load(reader, LoadOptions.SetLineInfo);
            }
            catch (XmlException ex)
            {
                var broken = new MarkupDocument { Kind = MarkupRootKind.Window };
                broken.Diagnostics.Add(new MarkupDiagnostic(ex.LineNumber, ex.Message));
                return broken;
            }

            var root = xml.Root!;
            var kind = string.Equals(root.Name.LocalName, "View", StringComparison.OrdinalIgnoreCase) ? MarkupRootKind.View : MarkupRootKind.Window;
            if (!string.Equals(root.Name.LocalName, "Window", StringComparison.OrdinalIgnoreCase) && kind != MarkupRootKind.View)
                errors.Add(new MarkupDiagnostic(Line(root), $"The root element must be 'Window' or 'View', not '{root.Name.LocalName}'."));

            string? className = null, baseName = null, title = null;
            int width = 640, height = 480;
            foreach (var attribute in root.Attributes().Where(a => !a.IsNamespaceDeclaration))
            {
                string name = attribute.Name.LocalName;
                int line = Line(attribute);
                switch (name.ToLowerInvariant())
                {
                    case "class": className = attribute.Value; break;
                    case "base": baseName = attribute.Value; break;
                    case "title" when kind == MarkupRootKind.Window: title = attribute.Value; break;
                    case "width": width = ReadSize(attribute, width, errors); break;
                    case "height": height = ReadSize(attribute, height, errors); break;
                    default:
                        errors.Add(new MarkupDiagnostic(line, $"Unknown attribute '{name}' of '{root.Name.LocalName}' (known: class, base, {(kind == MarkupRootKind.Window ? "title, " : "")}width, height)."));
                        break;
                }
            }

            if (string.IsNullOrWhiteSpace(className))
            {
                errors.Add(new MarkupDiagnostic(Line(root), $"'{root.Name.LocalName}' needs the attribute class=\"...\": the name of the class of the code-behind."));
                className = "Ui";
            }
            else if (!MarkupSchema.IsIdentifier(className))
                errors.Add(new MarkupDiagnostic(Line(root), $"'{className}' is not a valid class name."));

            if (string.IsNullOrWhiteSpace(baseName)) baseName = className + "Base";
            else if (!MarkupSchema.IsIdentifier(baseName))
                errors.Add(new MarkupDiagnostic(Line(root), $"'{baseName}' is not a valid class name."));

            var document = new MarkupDocument
            {
                Kind = kind,
                ClassName = className,
                BaseName = baseName,
                Title = title ?? className,
                Width = width,
                Height = height,
            };

            foreach (var child in root.Elements())
            {
                if (string.Equals(child.Name.LocalName, "Resources", StringComparison.OrdinalIgnoreCase)) ReadResources(child, document, errors);
                else ReadElement(child, document.Children, errors);
            }

            document.Diagnostics.AddRange(errors);
            Validate(document);
            return document;
        }

        private static int Line(XObject node) => node is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : 0;

        private static int ReadSize(XAttribute attribute, int fallback, List<MarkupDiagnostic> errors)
        {
            if (int.TryParse(attribute.Value, out int n) && n > 0) return n;
            errors.Add(new MarkupDiagnostic(Line(attribute), $"'{attribute.Name.LocalName}' must be a positive whole number, not '{attribute.Value}'."));
            return fallback;
        }

        private static void ReadResources(XElement resources, MarkupDocument document, List<MarkupDiagnostic> errors)
        {
            foreach (var item in resources.Elements())
            {
                if (!string.Equals(item.Name.LocalName, "Converter", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(new MarkupDiagnostic(Line(item), $"Unknown resource '{item.Name.LocalName}' (known: Converter)."));
                    continue;
                }
                string? key = (string?)item.Attribute("key"), type = (string?)item.Attribute("type");
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(type))
                {
                    errors.Add(new MarkupDiagnostic(Line(item), "A 'Converter' needs key=\"...\" (the name bindings use) and type=\"...\" (the class)."));
                    continue;
                }
                if (!MarkupSchema.IsIdentifier(key)) errors.Add(new MarkupDiagnostic(Line(item), $"'{key}' is not a valid converter key."));
                else if (!MarkupSchema.IsDottedName(type)) errors.Add(new MarkupDiagnostic(Line(item), $"'{type}' is not a valid class name."));
                else if (document.Converters.Any(c => c.Key == key)) errors.Add(new MarkupDiagnostic(Line(item), $"The converter '{key}' is declared twice."));
                else document.Converters.Add(new ConverterDeclaration(key, type, Line(item)));
            }
        }

        private static void ReadElement(XElement node, List<MarkupElement> into, List<MarkupDiagnostic> errors)
        {
            var def = MarkupSchema.Find(node.Name.LocalName);
            if (def == null)
            {
                errors.Add(new MarkupDiagnostic(Line(node),
                    $"Unknown element '{node.Name.LocalName}' (known: {string.Join(", ", MarkupSchema.Elements.Select(e => e.Tag))})."));
                return;
            }

            var element = new MarkupElement { Tag = def.Tag, Line = Line(node) };
            foreach (var attribute in node.Attributes().Where(a => !a.IsNamespaceDeclaration))
            {
                string name = attribute.Name.LocalName;
                int line = Line(attribute);
                if (string.Equals(name, "name", StringComparison.OrdinalIgnoreCase))
                {
                    if (!MarkupSchema.IsIdentifier(attribute.Value)) errors.Add(new MarkupDiagnostic(line, $"'{attribute.Value}' is not a valid name."));
                    else element.Name = attribute.Value;
                }
                else if (def.Event(name) is { } eventName)
                {
                    if (!MarkupSchema.IsIdentifier(attribute.Value)) errors.Add(new MarkupDiagnostic(line, $"'{attribute.Value}' is not a valid method name for '{eventName}'."));
                    else element.Handlers.Add((eventName, attribute.Value, line));
                }
                else if (def.Property(name) is { } property)
                {
                    var value = ReadValue(attribute.Value, line, errors);
                    if (value != null) element.Attributes.Add(new MarkupAttribute(property.Name, value, line));
                }
                else
                {
                    var known = def.Properties.Select(p => p.Name).Concat(def.Events).Append("name");
                    errors.Add(new MarkupDiagnostic(line, $"'{def.Tag}' has no property or event '{name}' (known: {string.Join(", ", known)})."));
                }
            }

            // the text between the tags is the text of the element: <Button>OK</Button>
            var content = string.Concat(node.Nodes().OfType<XText>().Select(t => t.Value)).Trim();
            if (content.Length > 0)
            {
                if (def.Property("text") == null) errors.Add(new MarkupDiagnostic(Line(node), $"'{def.Tag}' cannot have text content."));
                else if (element.Find("text") != null) errors.Add(new MarkupDiagnostic(Line(node), "The text is given twice (attribute and content)."));
                else element.Attributes.Add(new MarkupAttribute("text", ReadValue(Regex.Replace(content, @"\s+", " "), Line(node), errors) ?? new LiteralValue(content), Line(node)));
            }

            foreach (var child in node.Elements())
            {
                if (!def.IsContainer)
                {
                    errors.Add(new MarkupDiagnostic(Line(child), $"'{def.Tag}' cannot contain elements (only Panel and Stack can)."));
                    continue;
                }
                ReadElement(child, element.Children, errors);
            }
            into.Add(element);
        }

        /// <summary>Reads the text of an attribute: a plain value or a markup extension in braces (`{Binding ...}`, `{Enum ...}`, `{Static ...}`, `{Expr ...}`); `{}text` is the plain text `text` (for text that starts with a brace).</summary>
        public static MarkupValue? ReadValue(string raw, int line, List<MarkupDiagnostic> errors)
        {
            string text = raw.Trim();
            if (raw.StartsWith("{}", StringComparison.Ordinal)) return new LiteralValue(raw.Substring(2));
            if (!(text.StartsWith('{') && text.EndsWith('}'))) return new LiteralValue(raw);

            string inner = text.Substring(1, text.Length - 2).Trim();
            int space = inner.IndexOfAny(new[] { ' ', '\t' });
            string keyword = space < 0 ? inner : inner.Substring(0, space);
            string rest = space < 0 ? "" : inner.Substring(space + 1).Trim();

            switch (keyword.ToLowerInvariant())
            {
                case "enum":
                case "static":
                    if (!MarkupSchema.IsDottedName(rest))
                    {
                        errors.Add(new MarkupDiagnostic(line, $"'{{{keyword} ...}}' needs a name like Type.Member, not '{rest}'."));
                        return null;
                    }
                    return new ExpressionValue(rest);
                case "expr":
                    if (rest.Length == 0)
                    {
                        errors.Add(new MarkupDiagnostic(line, "'{Expr ...}' needs an expression."));
                        return null;
                    }
                    return new ExpressionValue(rest, IsRaw: true);
                case "binding":
                    return ReadBinding(rest, line, errors);
                default:
                    errors.Add(new MarkupDiagnostic(line, $"Unknown markup extension '{{{keyword} ...}}' (known: Binding, Enum, Static, Expr; use '{{}}' in front of a text that starts with a brace)."));
                    return null;
            }
        }

        private static MarkupValue? ReadBinding(string arguments, int line, List<MarkupDiagnostic> errors)
        {
            string? path = null, converter = null, elementName = null;
            var mode = BindingMode.OneWay;
            bool ok = true;
            int position = 0;
            foreach (var part in arguments.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0))
            {
                int equals = part.IndexOf('=');
                string key = equals < 0 ? (position == 0 ? "Path" : "") : part.Substring(0, equals).Trim();
                string value = equals < 0 ? part : part.Substring(equals + 1).Trim();
                position++;
                switch (key.ToLowerInvariant())
                {
                    case "path": path = value; break;
                    case "converter": converter = value; break;
                    case "elementname": elementName = value; break;
                    case "mode":
                        if (!Enum.TryParse(value, true, out mode) || !Enum.IsDefined(mode))
                        {
                            errors.Add(new MarkupDiagnostic(line, $"Unknown binding mode '{value}' (known: OneWay, TwoWay, OneTime)."));
                            ok = false;
                        }
                        break;
                    default:
                        errors.Add(new MarkupDiagnostic(line, $"Unknown binding option '{(key.Length == 0 ? part : key)}' (known: Path, Mode, Converter, ElementName)."));
                        ok = false;
                        break;
                }
            }

            if (string.IsNullOrEmpty(path) || !MarkupSchema.IsDottedName(path))
            {
                errors.Add(new MarkupDiagnostic(line, $"A binding needs a path like Name or Player.Hp, not '{path}'."));
                return null;
            }
            return ok ? new BindingValue(path, mode, converter, elementName) : null;
        }

        /// <summary>The checks that need the whole document: unique names, converters and elements that bindings name.</summary>
        private static void Validate(MarkupDocument document)
        {
            var names = new Dictionary<string, MarkupElement>();
            foreach (var element in document.AllElements())
            {
                if (element.Name == null) continue;
                if (MarkupSchema.ReservedNames.Contains(element.Name) || element.Name.StartsWith("fx", StringComparison.Ordinal))
                    document.Diagnostics.Add(new MarkupDiagnostic(element.Line, $"The name '{element.Name}' is used by the generated class."));
                else if (!names.TryAdd(element.Name, element))
                    document.Diagnostics.Add(new MarkupDiagnostic(element.Line, $"The name '{element.Name}' is used twice."));
            }

            var handlerNames = new HashSet<string>();
            foreach (var element in document.AllElements())
                foreach (var (_, handler, line) in element.Handlers)
                {
                    if (names.ContainsKey(handler) || MarkupSchema.ReservedNames.Contains(handler))
                        document.Diagnostics.Add(new MarkupDiagnostic(line, $"The method name '{handler}' is already used."));
                    handlerNames.Add(handler);
                }

            foreach (var element in document.AllElements())
                foreach (var attribute in element.Attributes)
                {
                    if (attribute.Value is not BindingValue binding) continue;
                    if (binding.ElementName != null && !names.ContainsKey(binding.ElementName))
                        document.Diagnostics.Add(new MarkupDiagnostic(attribute.Line, $"The binding names the element '{binding.ElementName}', which does not exist (give it name=\"...\")."));
                    if (binding.Converter != null && !document.Converters.Any(c => c.Key == binding.Converter) && !MarkupSchema.BuiltInConverters.ContainsKey(binding.Converter))
                        document.Diagnostics.Add(new MarkupDiagnostic(attribute.Line,
                            $"Unknown converter '{binding.Converter}' (declare it in <Resources>, or use one of: {string.Join(", ", MarkupSchema.BuiltInConverters.Keys)})."));
                }
        }
    }
}
