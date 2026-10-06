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
                else ReadElement(child, document.Children, errors, null);
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
                string kind = item.Name.LocalName;
                if (string.Equals(kind, "Style", StringComparison.OrdinalIgnoreCase)) { ReadStyle(item, document, errors); continue; }
                if (string.Equals(kind, "ControlTemplate", StringComparison.OrdinalIgnoreCase)) { ReadTemplate(item, document, errors); continue; }
                if (!string.Equals(kind, "Converter", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(new MarkupDiagnostic(Line(item), $"Unknown resource '{item.Name.LocalName}' (known: Converter, Style, ControlTemplate)."));
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

        private static string? KeyOf(XElement node, string what, List<MarkupDiagnostic> errors, bool required)
        {
            string? key = (string?)node.Attribute("key");
            if (string.IsNullOrWhiteSpace(key))
            {
                if (required) errors.Add(new MarkupDiagnostic(Line(node), $"A '{what}' needs key=\"...\"."));
                return null;
            }
            if (!MarkupSchema.IsIdentifier(key))
            {
                errors.Add(new MarkupDiagnostic(Line(node), $"'{key}' is not a valid key."));
                return null;
            }
            return key;
        }

        private static void CheckAttributes(XElement node, string[] known, List<MarkupDiagnostic> errors)
        {
            foreach (var attribute in node.Attributes().Where(a => !a.IsNamespaceDeclaration))
                if (!known.Contains(attribute.Name.LocalName))
                    errors.Add(new MarkupDiagnostic(Line(attribute), $"'{node.Name.LocalName}' has no attribute '{attribute.Name.LocalName}' (known: {string.Join(", ", known)})."));
        }

        private static void ReadStyle(XElement node, MarkupDocument document, List<MarkupDiagnostic> errors)
        {
            CheckAttributes(node, new[] { "key", "target", "basedOn" }, errors);
            string? key = KeyOf(node, "Style", errors, false);
            string? targetName = (string?)node.Attribute("target");
            var target = targetName == null ? null : MarkupSchema.FindTarget(targetName);
            if (target == null)
            {
                errors.Add(new MarkupDiagnostic(Line(node), targetName == null ? "A 'Style' needs target=\"...\" (the kind of element it is for, e.g. Button)."
                    : $"Unknown style target '{targetName}' (an element of the markup, or Element)."));
                return;
            }
            var style = new StyleDeclaration { Key = key, Target = target.Tag, BasedOn = (string?)node.Attribute("basedOn"), Line = Line(node) };
            foreach (var child in node.Elements())
            {
                if (string.Equals(child.Name.LocalName, "Setter", StringComparison.OrdinalIgnoreCase))
                {
                    if (ReadSetter(child, target, null, errors) is { } setter) style.Setters.Add(setter);
                }
                else if (string.Equals(child.Name.LocalName, "Trigger", StringComparison.OrdinalIgnoreCase))
                {
                    if (ReadTrigger(child, target, null, errors) is { } trigger) style.Triggers.Add(trigger);
                }
                else errors.Add(new MarkupDiagnostic(Line(child), $"A 'Style' contains 'Setter' and 'Trigger', not '{child.Name.LocalName}'."));
            }
            document.Styles.Add(style);
        }

        private static SetterDeclaration? ReadSetter(XElement node, ElementDef target, TemplateDeclaration? template, List<MarkupDiagnostic> errors)
        {
            CheckAttributes(node, template == null ? new[] { "property", "value" } : new[] { "property", "value", "target" }, errors);
            string? property = (string?)node.Attribute("property"), value = (string?)node.Attribute("value"), part = (string?)node.Attribute("target");
            if (string.IsNullOrWhiteSpace(property) || value == null)
            {
                errors.Add(new MarkupDiagnostic(Line(node), "A 'Setter' needs property=\"...\" and value=\"...\"."));
                return null;
            }
            if (template == null && target.Property(property) == null)
            {
                errors.Add(new MarkupDiagnostic(Line(node), $"'{target.Tag}' has no property '{property}' (known: {string.Join(", ", target.Properties.Select(p => p.Name))})."));
                return null;
            }
            var parsed = ReadValue(value, Line(node), errors);
            if (parsed == null) return null;
            if (parsed is BindingValue or TemplateBindingValue)
            {
                errors.Add(new MarkupDiagnostic(Line(node), "A 'Setter' takes a plain value, not a binding."));
                return null;
            }
            return new SetterDeclaration(property, parsed, part, Line(node));
        }

        private static TriggerDeclaration? ReadTrigger(XElement node, ElementDef target, TemplateDeclaration? template, List<MarkupDiagnostic> errors)
        {
            CheckAttributes(node, template == null ? new[] { "property", "value" } : new[] { "property", "value", "source" }, errors);
            var trigger = new TriggerDeclaration { Line = Line(node) };
            bool ok = true;
            void Condition(XElement from)
            {
                string? property = (string?)from.Attribute("property"), value = (string?)from.Attribute("value"), source = (string?)from.Attribute("source");
                if (string.IsNullOrWhiteSpace(property) || value == null)
                {
                    errors.Add(new MarkupDiagnostic(Line(from), "A trigger condition needs property=\"...\" and value=\"...\"."));
                    ok = false;
                    return;
                }
                if (source == null && target.Property(property) == null && !MarkupSchema.StateProperties.Any(p => p.Name == property))
                {
                    errors.Add(new MarkupDiagnostic(Line(from), $"'{target.Tag}' has no property '{property}' to test (known: {string.Join(", ", target.Properties.Select(p => p.Name).Concat(MarkupSchema.StateProperties.Select(p => p.Name)))})."));
                    ok = false;
                    return;
                }
                var parsed = ReadValue(value, Line(from), errors);
                if (parsed == null || parsed is BindingValue or TemplateBindingValue)
                {
                    if (parsed != null) errors.Add(new MarkupDiagnostic(Line(from), "A trigger condition compares with a plain value, not a binding."));
                    ok = false;
                    return;
                }
                trigger.Conditions.Add((property, parsed, source, Line(from)));
            }
            Condition(node);
            foreach (var child in node.Elements())
            {
                if (string.Equals(child.Name.LocalName, "Condition", StringComparison.OrdinalIgnoreCase)) Condition(child);
                else if (string.Equals(child.Name.LocalName, "Setter", StringComparison.OrdinalIgnoreCase))
                {
                    if (ReadSetter(child, target, template, errors) is { } setter) trigger.Setters.Add(setter);
                }
                else errors.Add(new MarkupDiagnostic(Line(child), $"A 'Trigger' contains 'Condition' and 'Setter', not '{child.Name.LocalName}'."));
            }
            return ok ? trigger : null;
        }

        private static void ReadTemplate(XElement node, MarkupDocument document, List<MarkupDiagnostic> errors)
        {
            CheckAttributes(node, new[] { "key", "target" }, errors);
            string? key = KeyOf(node, "ControlTemplate", errors, true);
            string? targetName = (string?)node.Attribute("target");
            var target = targetName == null ? null : MarkupSchema.FindTarget(targetName);
            if (key == null) return;
            if (target == null)
            {
                errors.Add(new MarkupDiagnostic(Line(node), targetName == null ? "A 'ControlTemplate' needs target=\"...\" (the kind of control it is for, e.g. Button)."
                    : $"Unknown template target '{targetName}'."));
                return;
            }
            var template = new TemplateDeclaration { Key = key, Target = target.Tag, Line = Line(node) };
            var roots = new List<MarkupElement>();
            foreach (var child in node.Elements())
            {
                if (string.Equals(child.Name.LocalName, "Trigger", StringComparison.OrdinalIgnoreCase))
                {
                    if (ReadTrigger(child, target, template, errors) is { } trigger) template.Triggers.Add(trigger);
                }
                else ReadElement(child, roots, errors, null);
            }
            if (roots.Count != 1) errors.Add(new MarkupDiagnostic(Line(node), "A 'ControlTemplate' contains exactly one element: the root of the parts."));
            else template.Root = roots[0];
            document.Templates.Add(template);
        }

        private static void ReadElement(XElement node, List<MarkupElement> into, List<MarkupDiagnostic> errors, ElementDef? parent)
        {
            var def = MarkupSchema.Find(node.Name.LocalName);
            if (def == null)
            {
                errors.Add(new MarkupDiagnostic(Line(node),
                    $"Unknown element '{node.Name.LocalName}' (known: {string.Join(", ", MarkupSchema.Elements.Where(e => !e.IsPart).Select(e => e.Tag))})."));
                return;
            }
            if (def.IsPart && (parent == null || !def.PartOf.Contains(parent.Tag)))
            {
                errors.Add(new MarkupDiagnostic(Line(node), $"'{def.Tag}' can only be inside {string.Join(" or ", def.PartOf.Select(t => "'" + t + "'"))}."));
                return;
            }
            if (!def.IsPart && parent is { Children: ChildMode.Parts })
            {
                errors.Add(new MarkupDiagnostic(Line(node), $"'{parent.Tag}' can only contain {string.Join(", ", parent.PartTags.Select(t => "'" + t + "'"))}, not '{def.Tag}'."));
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
                else if (def.Event(name) is { } eventDef)
                {
                    if (!MarkupSchema.IsIdentifier(attribute.Value)) errors.Add(new MarkupDiagnostic(line, $"'{attribute.Value}' is not a valid method name for '{eventDef.Name}'."));
                    else element.Handlers.Add((eventDef.Name, attribute.Value, line));
                }
                else if (def.Property(name) is { } property)
                {
                    var value = ReadValue(attribute.Value, line, errors);
                    if (value != null) element.Attributes.Add(new MarkupAttribute(property.Name, value, line));
                }
                else
                {
                    var known = def.Properties.Select(p => p.Name).Concat(def.Events.Select(e => e.Name)).Append("name");
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
                if (def.Children == ChildMode.None)
                {
                    errors.Add(new MarkupDiagnostic(Line(child), $"'{def.Tag}' cannot contain elements."));
                    continue;
                }
                if (def.Children == ChildMode.Single && element.Children.Count >= 1)
                {
                    errors.Add(new MarkupDiagnostic(Line(child), $"'{def.Tag}' contains only one element."));
                    continue;
                }
                ReadElement(child, element.Children, errors, def);
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
                case "templatebinding":
                    if (!MarkupSchema.IsIdentifier(rest))
                    {
                        errors.Add(new MarkupDiagnostic(line, $"'{{TemplateBinding ...}}' needs a property name, not '{rest}'."));
                        return null;
                    }
                    return new TemplateBindingValue(rest);
                default:
                    errors.Add(new MarkupDiagnostic(line, $"Unknown markup extension '{{{keyword} ...}}' (known: Binding, TemplateBinding, Enum, Static, Expr; use '{{}}' in front of a text that starts with a brace)."));
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

        /// <summary>Keys of styles and templates, references to them, and everything inside the control templates.</summary>
        private static void ValidateResources(MarkupDocument document)
        {
            var keys = new Dictionary<string, int>();
            void Key(string? key, int line)
            {
                if (key == null) return;
                if (keys.ContainsKey(key)) document.Diagnostics.Add(new MarkupDiagnostic(line, $"The key '{key}' of a style or template is used twice."));
                else keys[key] = line;
            }
            foreach (var style in document.Styles) Key(style.Key, style.Line);
            foreach (var template in document.Templates) Key(template.Key, template.Line);

            var implicitTargets = new HashSet<string>();
            foreach (var style in document.Styles)
            {
                if (style.Key == null && !implicitTargets.Add(style.Target))
                    document.Diagnostics.Add(new MarkupDiagnostic(style.Line, $"There are two styles without a key for '{style.Target}'."));
                if (style.BasedOn != null)
                {
                    var based = document.Styles.FirstOrDefault(o => o.Key == style.BasedOn);
                    if (based == null) document.Diagnostics.Add(new MarkupDiagnostic(style.Line, $"The style 'basedOn=\"{style.BasedOn}\"' does not exist (it needs a key)."));
                    else
                    {
                        // a cycle of basedOn
                        var seen = new HashSet<StyleDeclaration> { style };
                        for (var next = based; next != null; next = next.BasedOn == null ? null : document.Styles.FirstOrDefault(o => o.Key == next.BasedOn))
                            if (!seen.Add(next)) { document.Diagnostics.Add(new MarkupDiagnostic(style.Line, $"The styles '{style.Key}' and '{style.BasedOn}' are based on each other.")); break; }
                    }
                }
            }

            // references from the attributes
            foreach (var element in document.AllElements().Concat(document.TemplateElements()))
            {
                var def = MarkupSchema.Find(element.Tag);
                if (def == null) continue;
                foreach (var attribute in element.Attributes)
                {
                    if (attribute.Value is not LiteralValue literal) continue;
                    var property = def.Property(attribute.Name);
                    if (property?.Kind == PropertyKind.StyleRef && !document.Styles.Any(s => s.Key == literal.Text.Trim()))
                        document.Diagnostics.Add(new MarkupDiagnostic(attribute.Line, $"Unknown style '{literal.Text.Trim()}' (give a <Style key=\"...\"> in the Resources)."));
                    if (property?.Kind == PropertyKind.TemplateRef && !document.Templates.Any(t => t.Key == literal.Text.Trim()))
                        document.Diagnostics.Add(new MarkupDiagnostic(attribute.Line, $"Unknown template '{literal.Text.Trim()}' (give a <ControlTemplate key=\"...\"> in the Resources)."));
                }
            }
            foreach (var style in document.Styles)
                foreach (var setter in style.Setters.Concat(style.Triggers.SelectMany(t => t.Setters)))
                {
                    var def = MarkupSchema.FindTarget(style.Target);
                    var property = def?.Property(setter.Property);
                    if (setter.Value is LiteralValue literal && property?.Kind == PropertyKind.StyleRef && !document.Styles.Any(s => s.Key == literal.Text.Trim()))
                        document.Diagnostics.Add(new MarkupDiagnostic(setter.Line, $"Unknown style '{literal.Text.Trim()}'."));
                    if (setter.Value is LiteralValue literal2 && property?.Kind == PropertyKind.TemplateRef && !document.Templates.Any(t => t.Key == literal2.Text.Trim()))
                        document.Diagnostics.Add(new MarkupDiagnostic(setter.Line, $"Unknown template '{literal2.Text.Trim()}'."));
                }

            // inside a template
            foreach (var template in document.Templates)
            {
                if (template.Root == null) continue;
                var target = MarkupSchema.FindTarget(template.Target)!;
                var parts = new Dictionary<string, MarkupElement>();
                var all = new List<MarkupElement>();
                var stack = new Stack<MarkupElement>(new[] { template.Root });
                while (stack.Count > 0)
                {
                    var e = stack.Pop();
                    all.Add(e);
                    foreach (var c in e.Children) stack.Push(c);
                }
                foreach (var e in all)
                {
                    if (e.Name != null && !parts.TryAdd(e.Name, e))
                        document.Diagnostics.Add(new MarkupDiagnostic(e.Line, $"The part name '{e.Name}' is used twice in the template '{template.Key}'."));
                    if (e.Handlers.Count > 0)
                        document.Diagnostics.Add(new MarkupDiagnostic(e.Line, "Event handlers are not available inside a ControlTemplate (react to the control itself)."));
                    foreach (var attribute in e.Attributes)
                    {
                        if (attribute.Value is BindingValue)
                            document.Diagnostics.Add(new MarkupDiagnostic(attribute.Line, "'{Binding ...}' is not available inside a ControlTemplate; use '{TemplateBinding Property}'."));
                        if (attribute.Value is TemplateBindingValue tb && target.Property(tb.OwnerProperty) == null && !MarkupSchema.StateProperties.Any(p => p.Name == tb.OwnerProperty))
                            document.Diagnostics.Add(new MarkupDiagnostic(attribute.Line, $"'{template.Target}' has no property '{tb.OwnerProperty}' to bind to."));
                    }
                }
                foreach (var trigger in template.Triggers)
                {
                    foreach (var (_, _, source, line) in trigger.Conditions)
                        if (source != null && !parts.ContainsKey(source))
                            document.Diagnostics.Add(new MarkupDiagnostic(line, $"The template '{template.Key}' has no part '{source}'."));
                    foreach (var setter in trigger.Setters)
                    {
                        if (setter.Target == null)
                        {
                            if (target.Property(setter.Property) == null)
                                document.Diagnostics.Add(new MarkupDiagnostic(setter.Line, $"'{template.Target}' has no property '{setter.Property}'."));
                        }
                        else if (!parts.TryGetValue(setter.Target, out var part))
                            document.Diagnostics.Add(new MarkupDiagnostic(setter.Line, $"The template '{template.Key}' has no part '{setter.Target}'."));
                        else if (MarkupSchema.Find(part.Tag)?.Property(setter.Property) == null)
                            document.Diagnostics.Add(new MarkupDiagnostic(setter.Line, $"'{part.Tag}' has no property '{setter.Property}'."));
                    }
                }
            }
            // a trigger of a style cannot name parts
            foreach (var style in document.Styles)
                foreach (var trigger in style.Triggers)
                    foreach (var setter in trigger.Setters.Where(x => x.Target != null))
                        document.Diagnostics.Add(new MarkupDiagnostic(setter.Line, "'target' is only for the setters of a ControlTemplate."));
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

            ValidateResources(document);

            foreach (var element in document.AllElements())
                foreach (var attribute in element.Attributes)
                {
                    if (attribute.Value is TemplateBindingValue)
                        document.Diagnostics.Add(new MarkupDiagnostic(attribute.Line, "'{TemplateBinding ...}' is only for the elements inside a ControlTemplate."));
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
