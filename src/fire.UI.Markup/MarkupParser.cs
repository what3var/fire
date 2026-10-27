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
        /// <summary>The XML namespace of the attributes `namespace:prefix="..."` on the root element (declared by the parser, the markup does not have to).</summary>
        public const string NamespacePrefixUri = "urn:fire:namespace";

        // the namespaces of the markup that is read right now (ReadValue and the converters qualify names with them)
        [ThreadStatic] private static NamespaceScope? s_scope;

        private sealed class NamespaceScope
        {
            public string? Own;
            public Dictionary<string, string> Bound = new();
        }

        // the first start tag of the document (after the XML declaration, comments and a doctype)
        private static readonly Regex RootTag = new(@"<(?![?!/])[A-Za-z_][\w.\-]*", RegexOptions.Compiled);

        /// <summary>`namespace:prefix="..."` needs the prefix `namespace` declared for XML: the parser declares it on the root element (unless the markup does).</summary>
        private static string DeclareNamespacePrefix(string text)
        {
            if (text.Contains("xmlns:namespace", StringComparison.Ordinal) || !text.Contains("namespace:", StringComparison.Ordinal)) return text;
            var match = RootTag.Match(text);
            return match.Success ? text.Insert(match.Index + match.Length, $" xmlns:namespace=\"{NamespacePrefixUri}\"") : text;
        }

        /// <summary>A type name of the markup as fire code: `local:Name` is the name in the namespace of the markup, `prefix:Name` the name in the namespace bound to
        /// the prefix; a name without prefix stays as it is (fire looks it up in the bound namespaces, see the `#using` of the generated script). null (and a
        /// diagnostic) for an unknown prefix.</summary>
        public static string? QualifyName(string name, int line, List<MarkupDiagnostic> errors)
        {
            int colon = name.IndexOf(':');
            if (colon < 0) return name;
            string prefix = name.Substring(0, colon), rest = name.Substring(colon + 1);
            var scope = s_scope;
            if (prefix == "local") return scope?.Own == null ? rest : scope.Own + "." + rest;
            if (scope != null && scope.Bound.TryGetValue(prefix, out var ns)) return ns + "." + rest;
            errors.Add(new MarkupDiagnostic(line, $"Unknown namespace prefix '{prefix}' (bind it on the root element: namespace:{prefix}=\"Name.Space\"; 'local' is the namespace of the markup)."));
            return null;
        }

        public static MarkupDocument Parse(string text)
        {
            var errors = new List<MarkupDiagnostic>();
            var scope = new NamespaceScope();
            var previous = s_scope;
            s_scope = scope;
            try { return Parse(DeclareNamespacePrefix(text), errors, scope); }
            finally { s_scope = previous; }
        }

        private static MarkupDocument Parse(string text, List<MarkupDiagnostic> errors, NamespaceScope scope)
        {
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
                if (attribute.Name.NamespaceName == NamespacePrefixUri)
                {
                    // namespace:prefix="Name.Space": binds another namespace
                    string bound = attribute.Value.Trim();
                    if (name == "local") errors.Add(new MarkupDiagnostic(line, "The prefix 'local' is the namespace of the markup itself (namespace=\"...\"); bind another one under another prefix."));
                    else if (!MarkupSchema.IsDottedName(bound)) errors.Add(new MarkupDiagnostic(line, $"'{bound}' is not a valid namespace name."));
                    else scope.Bound[name] = bound;
                    continue;
                }
                if (attribute.Name.NamespaceName.Length > 0)
                {
                    errors.Add(new MarkupDiagnostic(line, $"Unknown attribute '{attribute.Name.LocalName}' of '{root.Name.LocalName}' in the XML namespace '{attribute.Name.NamespaceName}'."));
                    continue;
                }
                switch (name.ToLowerInvariant())
                {
                    case "namespace":
                        if (!MarkupSchema.IsDottedName(attribute.Value.Trim())) errors.Add(new MarkupDiagnostic(line, $"'{attribute.Value}' is not a valid namespace name."));
                        else scope.Own = attribute.Value.Trim();
                        break;
                    case "class": className = attribute.Value; break;
                    case "base": baseName = attribute.Value; break;
                    case "title" when kind == MarkupRootKind.Window: title = attribute.Value; break;
                    case "width": width = ReadSize(attribute, width, errors); break;
                    case "height": height = ReadSize(attribute, height, errors); break;
                    default:
                        errors.Add(new MarkupDiagnostic(line, $"Unknown attribute '{name}' of '{root.Name.LocalName}' (known: Class, Base, namespace, namespace:prefix, {(kind == MarkupRootKind.Window ? "Title, " : "")}Width, Height)."));
                        break;
                }
            }

            if (string.IsNullOrWhiteSpace(className))
            {
                errors.Add(new MarkupDiagnostic(Line(root), $"'{root.Name.LocalName}' needs the attribute Class=\"...\": the name of the class of the code-behind."));
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
                Namespace = scope.Own,
            };
            foreach (var (prefix, ns) in scope.Bound) document.Namespaces[prefix] = ns;

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
                if (string.Equals(kind, "DataTemplate", StringComparison.OrdinalIgnoreCase)) { ReadDataTemplate(item, document, errors); continue; }
                if (string.Equals(kind, "CollectionView", StringComparison.OrdinalIgnoreCase)) { ReadView(item, document, errors); continue; }
                if (!string.Equals(kind, "Converter", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(new MarkupDiagnostic(Line(item), $"Unknown resource '{item.Name.LocalName}' (known: Converter, Style, ControlTemplate, DataTemplate, CollectionView)."));
                    continue;
                }
                string? key = Attr(item, "Key"), type = Attr(item, "Type");
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(type))
                {
                    errors.Add(new MarkupDiagnostic(Line(item), "A 'Converter' needs Key=\"...\" (the name bindings use) and Type=\"...\" (the class)."));
                    continue;
                }
                if (!MarkupSchema.IsIdentifier(key)) errors.Add(new MarkupDiagnostic(Line(item), $"'{key}' is not a valid converter key."));
                else if (!IsTypeName(type)) errors.Add(new MarkupDiagnostic(Line(item), $"'{type}' is not a valid class name."));
                else if (document.Converters.Any(c => c.Key == key)) errors.Add(new MarkupDiagnostic(Line(item), $"The converter '{key}' is declared twice."));
                else if (QualifyName(type, Line(item), errors) is { } qualified) document.Converters.Add(new ConverterDeclaration(key, qualified, Line(item)));
            }
        }

        /// <summary>The value of an attribute, whatever the case of its name (Key, key); null if it is missing.</summary>
        private static string? Attr(XElement node, string name) =>
            node.Attributes().FirstOrDefault(a => !a.IsNamespaceDeclaration && a.Name.NamespaceName.Length == 0 && a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

        private static string? KeyOf(XElement node, string what, List<MarkupDiagnostic> errors, bool required)
        {
            string? key = Attr(node, "Key");
            if (string.IsNullOrWhiteSpace(key))
            {
                if (required) errors.Add(new MarkupDiagnostic(Line(node), $"A '{what}' needs Key=\"...\"."));
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
                if (!known.Contains(attribute.Name.LocalName, StringComparer.OrdinalIgnoreCase))
                    errors.Add(new MarkupDiagnostic(Line(attribute), $"'{node.Name.LocalName}' has no attribute '{attribute.Name.LocalName}' (known: {string.Join(", ", known)})."));
        }

        private static void ReadStyle(XElement node, MarkupDocument document, List<MarkupDiagnostic> errors)
        {
            CheckAttributes(node, new[] { "Key", "Target", "BasedOn" }, errors);
            string? key = KeyOf(node, "Style", errors, false);
            string? targetName = Attr(node, "Target");
            var target = targetName == null ? null : MarkupSchema.FindTarget(targetName);
            if (target == null)
            {
                errors.Add(new MarkupDiagnostic(Line(node), targetName == null ? "A 'Style' needs Target=\"...\" (the kind of element it is for, e.g. Button)."
                    : $"Unknown style target '{targetName}' (an element of the markup, or Element)."));
                return;
            }
            var style = new StyleDeclaration { Key = key, Target = target.Tag, BasedOn = Attr(node, "BasedOn"), Line = Line(node) };
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
            CheckAttributes(node, template == null ? new[] { "Property", "Value" } : new[] { "Property", "Value", "Target" }, errors);
            string? property = Attr(node, "Property"), value = Attr(node, "Value"), part = Attr(node, "Target");
            if (string.IsNullOrWhiteSpace(property) || value == null)
            {
                errors.Add(new MarkupDiagnostic(Line(node), "A 'Setter' needs Property=\"...\" and Value=\"...\"."));
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
            CheckAttributes(node, template == null ? new[] { "Property", "Value" } : new[] { "Property", "Value", "Source" }, errors);
            var trigger = new TriggerDeclaration { Line = Line(node) };
            bool ok = true;
            void Condition(XElement from)
            {
                string? property = Attr(from, "Property"), value = Attr(from, "Value"), source = Attr(from, "Source");
                if (string.IsNullOrWhiteSpace(property) || value == null)
                {
                    errors.Add(new MarkupDiagnostic(Line(from), "A trigger condition needs Property=\"...\" and Value=\"...\"."));
                    ok = false;
                    return;
                }
                if (source == null && target.Property(property) == null && MarkupSchema.StateProperty(property) == null)
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
            CheckAttributes(node, new[] { "Key", "Target" }, errors);
            string? key = KeyOf(node, "ControlTemplate", errors, true);
            string? targetName = Attr(node, "Target");
            var target = targetName == null ? null : MarkupSchema.FindTarget(targetName);
            if (key == null) return;
            if (target == null)
            {
                errors.Add(new MarkupDiagnostic(Line(node), targetName == null ? "A 'ControlTemplate' needs Target=\"...\" (the kind of control it is for, e.g. Button)."
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

        private static void ReadDataTemplate(XElement node, MarkupDocument document, List<MarkupDiagnostic> errors)
        {
            CheckAttributes(node, new[] { "Key" }, errors);
            string? key = KeyOf(node, "DataTemplate", errors, true);
            if (key == null) return;
            var template = new DataTemplateDeclaration { Key = key, Line = Line(node) };
            var roots = new List<MarkupElement>();
            foreach (var child in node.Elements()) ReadElement(child, roots, errors, null);
            if (roots.Count != 1) errors.Add(new MarkupDiagnostic(Line(node), "A 'DataTemplate' contains exactly one element: what is shown for one item."));
            else template.Root = roots[0];
            document.DataTemplates.Add(template);
        }

        private static void ReadView(XElement node, MarkupDocument document, List<MarkupDiagnostic> errors)
        {
            var def = MarkupSchema.CollectionViewDef;
            CheckAttributes(node, def.Properties.Select(p => p.Name).Append("Key").ToArray(), errors);
            string? key = KeyOf(node, "CollectionView", errors, true);
            if (key == null) return;
            if (node.HasElements) errors.Add(new MarkupDiagnostic(Line(node.Elements().First()), "A 'CollectionView' contains no elements."));
            var element = new MarkupElement { Tag = def.Tag, Line = Line(node) };
            foreach (var attribute in node.Attributes().Where(a => !a.IsNamespaceDeclaration && !a.Name.LocalName.Equals("Key", StringComparison.OrdinalIgnoreCase)))
            {
                var property = def.Property(attribute.Name.LocalName);
                if (property == null) continue; // reported by CheckAttributes
                var value = ReadValue(attribute.Value, Line(attribute), errors);
                if (value != null) element.Attributes.Add(new MarkupAttribute(property.Name, value, Line(attribute)));
            }
            document.Views.Add(new ViewDeclaration { Key = key, Line = Line(node), Element = element });
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
                    var known = def.Properties.Select(p => p.Name).Concat(def.Events.Select(e => e.Name)).Append("Name");
                    if (MarkupSchema.RenamedAttributes.TryGetValue(name, out var renamed) && def.Property(renamed) != null)
                        errors.Add(new MarkupDiagnostic(line, $"'{name}' is now called '{renamed}'."));
                    else errors.Add(new MarkupDiagnostic(line, $"'{def.Tag}' has no property or event '{name}' (known: {string.Join(", ", known)})."));
                }
            }

            // the text between the tags is the text of the element: <Button>OK</Button>
            var content = string.Concat(node.Nodes().OfType<XText>().Select(t => t.Value)).Trim();
            if (content.Length > 0)
            {
                if (def.Property("Text") == null) errors.Add(new MarkupDiagnostic(Line(node), $"'{def.Tag}' cannot have text content."));
                else if (element.Find("Text") != null) errors.Add(new MarkupDiagnostic(Line(node), "The text is given twice (attribute and content)."));
                else element.Attributes.Add(new MarkupAttribute("Text", ReadValue(Regex.Replace(content, @"\s+", " "), Line(node), errors) ?? new LiteralValue(content), Line(node)));
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
            if (ObjectStart.IsMatch(text) && text.EndsWith(']')) return ReadObject(text, line, errors);
            if (!(text.StartsWith('{') && text.EndsWith('}'))) return new LiteralValue(raw);

            string inner = text.Substring(1, text.Length - 2).Trim();
            int space = inner.IndexOfAny(new[] { ' ', '\t' });
            string keyword = space < 0 ? inner : inner.Substring(0, space);
            string rest = space < 0 ? "" : inner.Substring(space + 1).Trim();

            switch (keyword.ToLowerInvariant())
            {
                case "enum":
                case "static":
                    if (!IsTypeName(rest) || !rest.Contains('.'))
                    {
                        errors.Add(new MarkupDiagnostic(line, $"'{{{keyword} ...}}' needs a name like Type.Member, not '{rest}'."));
                        return null;
                    }
                    return QualifyName(rest, line, errors) is { } member ? new ExpressionValue(member) : null;
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

        // `[Type ...]`: an object value starts with a (possibly prefixed, dotted) type name
        private static readonly Regex ObjectStart = new(@"^\[\s*([A-Za-z_][A-Za-z0-9_]*:)?[A-Za-z_][A-Za-z0-9_.]*(\s|\])", RegexOptions.Compiled);
        private static readonly Regex TypeNamePattern = new(@"^([A-Za-z_][A-Za-z0-9_]*:)?[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$", RegexOptions.Compiled);
        private static readonly Regex NumberArgument = new(@"^-?(\d+(\.\d+)?|\.\d+)([eE][+-]?\d+)?[A-Za-z]*$", RegexOptions.Compiled);

        /// <summary>A (dotted) type name, possibly with a namespace prefix (`models:Person`, `local:Ui.Theme`).</summary>
        public static bool IsTypeName(string text) => TypeNamePattern.IsMatch(text);

        /// <summary>The classes of the libraries that the design view can make (it runs no code of the program).</summary>
        private static bool IsLibraryClass(string name) => name.StartsWith("UI.", StringComparison.Ordinal) || name is "SolidBrush" or "Pen" or "Font" or "Fonts";

        /// <summary>`[Type Arg1, Arg2]`: a new instance. The arguments are numbers (with a unit: `5mm`), texts in '' (or ""; two quotes stand for one), `#RRGGBB`
        /// (a colour number), `true`/`false`/`undefined`, `{Enum ...}`/`{Static ...}`/`{Expr ...}` and objects again.</summary>
        private static MarkupValue? ReadObject(string text, int line, List<MarkupDiagnostic> errors)
        {
            string inner = text.Substring(1, text.Length - 2).Trim();
            int end = 0;
            while (end < inner.Length && !char.IsWhiteSpace(inner[end])) end++;
            string type = inner.Substring(0, end);
            if (!IsTypeName(type))
            {
                errors.Add(new MarkupDiagnostic(line, $"'[{type} ...]' needs a class name, not '{type}'."));
                return null;
            }
            string? qualified = QualifyName(type, line, errors);
            if (qualified == null) return null;

            var arguments = SplitArguments(inner.Substring(end), line, errors);
            if (arguments == null) return null;
            var codes = new List<string>();
            bool safe = IsLibraryClass(qualified);
            foreach (var argument in arguments)
            {
                string a = argument.Trim();
                if (a.Length == 0)
                {
                    errors.Add(new MarkupDiagnostic(line, $"An argument of '[{type} ...]' is empty."));
                    return null;
                }
                if (a.Length >= 2 && (a[0] == '\'' || a[0] == '"') && a[^1] == a[0])
                {
                    string q = a[0].ToString();
                    codes.Add(FireUiGenerator.Quote(a.Substring(1, a.Length - 2).Replace(q + q, q)));
                }
                else if (NumberArgument.IsMatch(a) || a is "true" or "false" or "undefined") codes.Add(a);
                else if (a.StartsWith('#') && FireUiGenerator.TryParseColor(a, out int r, out int g, out int b)) codes.Add($"UI.Color.Rgb({r}, {g}, {b})");
                else if (a.StartsWith('[') || a.StartsWith('{'))
                {
                    var value = ReadValue(a, line, errors);
                    switch (value)
                    {
                        case ObjectValue nested: codes.Add(nested.Code); safe &= nested.PreviewSafe; break;
                        case ExpressionValue expression: codes.Add(expression.Code); safe = false; break;
                        case null: return null;
                        default:
                            errors.Add(new MarkupDiagnostic(line, $"'{a}' cannot be an argument of '[{type} ...]' (a binding is no value)."));
                            return null;
                    }
                }
                else
                {
                    errors.Add(new MarkupDiagnostic(line, $"'{a}' is no argument value: write a text in quotes ('{a}'), a number, true/false, #RRGGBB, [Type ...] or {{Static Type.Member}}."));
                    return null;
                }
            }
            return new ObjectValue(qualified, $"new {qualified}({string.Join(", ", codes)})", safe);
        }

        /// <summary>The arguments of an object value, split at the commas that are not inside quotes, brackets or braces. null (and a diagnostic) when they do not close.</summary>
        private static List<string>? SplitArguments(string text, int line, List<MarkupDiagnostic> errors)
        {
            var result = new List<string>();
            if (text.Trim().Length == 0) return result;
            var current = new System.Text.StringBuilder();
            int depth = 0;
            char quote = '\0';
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quote != '\0')
                {
                    current.Append(c);
                    if (c == quote)
                    {
                        if (i + 1 < text.Length && text[i + 1] == quote) { current.Append(text[++i]); continue; }
                        quote = '\0';
                    }
                    continue;
                }
                if (c is '\'' or '"') quote = c;
                else if (c is '[' or '{') depth++;
                else if (c is ']' or '}') depth--;
                else if (c == ',' && depth == 0) { result.Add(current.ToString()); current.Clear(); continue; }
                current.Append(c);
            }
            if (quote != '\0' || depth != 0)
            {
                errors.Add(new MarkupDiagnostic(line, quote != '\0' ? "A text in an object value is not closed (a quote is missing)." : "The brackets of an object value do not match."));
                return null;
            }
            result.Add(current.ToString());
            return result;
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

            // no path (or `.`): the item itself, which only a DataTemplate can bind to (checked in Validate)
            if (string.IsNullOrEmpty(path) || path == ".") path = "";
            else if (!MarkupSchema.IsDottedName(path))
            {
                errors.Add(new MarkupDiagnostic(line, $"A binding needs a path like Name or Player.Hp, not '{path}'."));
                return null;
            }
            return ok ? new BindingValue(path, mode, converter, elementName) : null;
        }

        /// <summary>Keys of styles and templates, references to them, and everything inside the control templates.</summary>
        private static void ValidateResources(MarkupDocument document)
        {
            void CheckConverter(BindingValue binding, int line)
            {
                if (binding.Converter != null && !document.Converters.Any(c => c.Key == binding.Converter) && !MarkupSchema.BuiltInConverters.ContainsKey(binding.Converter))
                    document.Diagnostics.Add(new MarkupDiagnostic(line,
                        $"Unknown converter '{binding.Converter}' (declare it in <Resources>, or use one of: {string.Join(", ", MarkupSchema.BuiltInConverters.Keys)})."));
            }
            foreach (var view in document.Views)
                foreach (var attribute in view.Element.Attributes)
                    if (attribute.Value is BindingValue viewBinding)
                    {
                        if (viewBinding.Path.Length == 0) document.Diagnostics.Add(new MarkupDiagnostic(attribute.Line, "A binding needs a path like Name or Player.Hp."));
                        CheckConverter(viewBinding, attribute.Line);
                    }
            var keys = new Dictionary<string, int>();
            void Key(string? key, int line)
            {
                if (key == null) return;
                if (keys.ContainsKey(key)) document.Diagnostics.Add(new MarkupDiagnostic(line, $"The key '{key}' of a style or template is used twice."));
                else keys[key] = line;
            }
            foreach (var style in document.Styles) Key(style.Key, style.Line);
            foreach (var template in document.Templates) Key(template.Key, template.Line);
            foreach (var template in document.DataTemplates) Key(template.Key, template.Line);
            foreach (var view in document.Views) Key(view.Key, view.Line);

            var implicitTargets = new HashSet<string>();
            foreach (var style in document.Styles)
            {
                if (style.Key == null && !implicitTargets.Add(style.Target))
                    document.Diagnostics.Add(new MarkupDiagnostic(style.Line, $"There are two styles without a key for '{style.Target}'."));
                if (style.BasedOn != null)
                {
                    var based = document.Styles.FirstOrDefault(o => o.Key == style.BasedOn);
                    if (based == null) document.Diagnostics.Add(new MarkupDiagnostic(style.Line, $"The style 'BasedOn=\"{style.BasedOn}\"' does not exist (it needs a key)."));
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
            void CheckReference(PropertyDef? property, string text, int line)
            {
                string key = text.Trim();
                if (property?.Kind == PropertyKind.StyleRef && !document.Styles.Any(s => s.Key == key))
                    document.Diagnostics.Add(new MarkupDiagnostic(line, $"Unknown style '{key}' (give a <Style Key=\"...\"> in the Resources)."));
                if (property?.Kind == PropertyKind.TemplateRef && !document.Templates.Any(t => t.Key == key))
                    document.Diagnostics.Add(new MarkupDiagnostic(line, $"Unknown template '{key}' (give a <ControlTemplate Key=\"...\"> in the Resources)."));
                if (property?.Kind == PropertyKind.DataTemplateRef && !document.DataTemplates.Any(t => t.Key == key))
                    document.Diagnostics.Add(new MarkupDiagnostic(line, $"Unknown data template '{key}' (give a <DataTemplate Key=\"...\"> in the Resources)."));
                if (property?.Kind == PropertyKind.ViewRef && !document.Views.Any(v => v.Key == key))
                    document.Diagnostics.Add(new MarkupDiagnostic(line, $"Unknown view '{key}' (give a <CollectionView Key=\"...\"> in the Resources)."));
            }
            void CheckValueKind(PropertyDef? property, MarkupValue value, int line)
            {
                if (property == null) return;
                if (property.Kind == PropertyKind.Collection && value is LiteralValue)
                    document.Diagnostics.Add(new MarkupDiagnostic(line, $"'{property.Name}' is a list: give {{Binding ...}} or {{Expr ...}}."));
                if (property.Kind == PropertyKind.Code && value is not (ExpressionValue or ObjectValue))
                    document.Diagnostics.Add(new MarkupDiagnostic(line, $"'{property.Name}' is code: give {{Expr ...}} or [Type ...]."));
            }
            foreach (var element in document.AllElements().Concat(document.TemplateElements()).Concat(document.DataTemplateElements()))
            {
                var def = MarkupSchema.Find(element.Tag);
                if (def == null) continue;
                foreach (var attribute in element.Attributes)
                {
                    var property = def.Property(attribute.Name);
                    if (attribute.Value is LiteralValue literal) CheckReference(property, literal.Text, attribute.Line);
                    CheckValueKind(property, attribute.Value, attribute.Line);
                }
                if (element.Find("view") != null && element.Find("itemsSource") != null)
                    document.Diagnostics.Add(new MarkupDiagnostic(element.Line, $"'{element.Tag}' shows a 'View' or an 'ItemsSource', not both."));
            }
            foreach (var view in document.Views)
                foreach (var attribute in view.Element.Attributes)
                {
                    var property = MarkupSchema.CollectionViewDef.Property(attribute.Name);
                    CheckValueKind(property, attribute.Value, attribute.Line);
                    if (attribute.Value is BindingValue { Mode: BindingMode.TwoWay })
                        document.Diagnostics.Add(new MarkupDiagnostic(attribute.Line, "The source of a 'CollectionView' is read only (Mode=OneWay or OneTime)."));
                    if (attribute.Name.Equals("SortBy", StringComparison.OrdinalIgnoreCase) && attribute.Value is LiteralValue sort && !MarkupSchema.IsIdentifier(sort.Text.Trim()))
                        document.Diagnostics.Add(new MarkupDiagnostic(attribute.Line, $"'SortBy' needs the name of a property, not '{sort.Text}'."));
                    if (attribute.Name.Equals("Descending", StringComparison.OrdinalIgnoreCase) && attribute.Value is not LiteralValue)
                        document.Diagnostics.Add(new MarkupDiagnostic(attribute.Line, "'Descending' needs true or false."));
                }
            foreach (var style in document.Styles)
                foreach (var setter in style.Setters.Concat(style.Triggers.SelectMany(t => t.Setters)))
                {
                    var def = MarkupSchema.FindTarget(style.Target);
                    var property = def?.Property(setter.Property);
                    if (setter.Value is LiteralValue literal) CheckReference(property, literal.Text, setter.Line);
                    CheckValueKind(property, setter.Value, setter.Line);
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
                        if (attribute.Value is TemplateBindingValue tb && target.Property(tb.OwnerProperty) == null && MarkupSchema.StateProperty(tb.OwnerProperty) == null)
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
            // inside a data template: bindings go to the item; nothing that needs a name or a handler
            foreach (var template in document.DataTemplates)
            {
                if (template.Root == null) continue;
                var stack = new Stack<MarkupElement>(new[] { template.Root });
                while (stack.Count > 0)
                {
                    var e = stack.Pop();
                    foreach (var c in e.Children) stack.Push(c);
                    if (e.Name != null)
                        document.Diagnostics.Add(new MarkupDiagnostic(e.Line, "Elements inside a DataTemplate have no name (there is one set of elements per item)."));
                    if (e.Handlers.Count > 0)
                        document.Diagnostics.Add(new MarkupDiagnostic(e.Line, "Event handlers are not available inside a DataTemplate (the rows only show data)."));
                    foreach (var attribute in e.Attributes)
                    {
                        if (attribute.Value is TemplateBindingValue)
                            document.Diagnostics.Add(new MarkupDiagnostic(attribute.Line, "'{TemplateBinding ...}' is only for the elements inside a ControlTemplate."));
                        if (attribute.Value is not BindingValue binding) continue;
                        if (binding.ElementName != null)
                            document.Diagnostics.Add(new MarkupDiagnostic(attribute.Line, "'ElementName' is not available inside a DataTemplate: a binding goes to the item."));
                        if (binding.Path.Length == 0 && binding.Mode == BindingMode.TwoWay)
                            document.Diagnostics.Add(new MarkupDiagnostic(attribute.Line, "A binding to the item itself is read only (Mode=OneWay or OneTime)."));
                        CheckConverter(binding, attribute.Line);
                    }
                }
            }
            // a trigger of a style cannot name parts
            foreach (var style in document.Styles)
                foreach (var trigger in style.Triggers)
                    foreach (var setter in trigger.Setters.Where(x => x.Target != null))
                        document.Diagnostics.Add(new MarkupDiagnostic(setter.Line, "'Target' is only for the setters of a ControlTemplate."));
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

            foreach (var view in document.Views)
                foreach (var attribute in view.Element.Attributes)
                    if (attribute.Value is BindingValue { ElementName: { } viewElement } && !names.ContainsKey(viewElement))
                        document.Diagnostics.Add(new MarkupDiagnostic(attribute.Line, $"The binding names the element '{viewElement}', which does not exist (give it Name=\"...\")."));

            foreach (var element in document.AllElements())
                foreach (var attribute in element.Attributes)
                {
                    if (attribute.Value is TemplateBindingValue)
                        document.Diagnostics.Add(new MarkupDiagnostic(attribute.Line, "'{TemplateBinding ...}' is only for the elements inside a ControlTemplate."));
                    if (attribute.Value is not BindingValue binding) continue;
                    if (binding.Path.Length == 0)
                        document.Diagnostics.Add(new MarkupDiagnostic(attribute.Line, "A binding needs a path like Name or Player.Hp (a binding without a path is for the elements of a DataTemplate)."));
                    if (binding.ElementName != null && !names.ContainsKey(binding.ElementName))
                        document.Diagnostics.Add(new MarkupDiagnostic(attribute.Line, $"The binding names the element '{binding.ElementName}', which does not exist (give it Name=\"...\")."));
                    if (binding.Converter != null && !document.Converters.Any(c => c.Key == binding.Converter) && !MarkupSchema.BuiltInConverters.ContainsKey(binding.Converter))
                        document.Diagnostics.Add(new MarkupDiagnostic(attribute.Line,
                            $"Unknown converter '{binding.Converter}' (declare it in <Resources>, or use one of: {string.Join(", ", MarkupSchema.BuiltInConverters.Keys)})."));
                }
        }
    }
}
