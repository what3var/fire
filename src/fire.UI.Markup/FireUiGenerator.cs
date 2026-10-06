using System.Globalization;
using System.Text;

namespace fire.UI.Markup
{
    /// <summary>Translates a <see cref="MarkupDocument"/> into the fire script of the interface (docs/UI_MARKUP.md). The script defines ONE class, the base class of the code-behind:
    /// it builds the elements (fields for the named ones), connects the handlers (`onClick="Save"` calls the method `Save(sender)`, empty here and overridden in the derived class)
    /// and the data bindings (probes, see SPEC 8.14) and brings `Run()` (window) or `Attach(container)` (view). The code-behind declares `class MyWindow : MyWindowBase { ... }`
    /// and never needs to know more of the generated code than the names it chose itself.</summary>
    public static class FireUiGenerator
    {
        private sealed class Info
        {
            public required MarkupElement Element { get; init; }
            public required ElementDef Def { get; init; }
            public required string Local { get; init; }
            /// <summary>The field that holds the element (named elements, and elements that are the target of a binding); null if there is none.</summary>
            public string? Field { get; set; }
        }

        private sealed class Binding
        {
            public required int Index { get; init; }
            public required Info Target { get; init; }
            public required PropertyDef Property { get; init; }
            public required BindingValue Value { get; init; }
            public string[] Segments => Value.Path.Split('.');
            /// <summary>The code that evaluates to the object the path starts at.</summary>
            public required string Source { get; init; }
            public bool FromDataContext => Value.ElementName == null;
            public string? ConverterField { get; init; }
        }

        // true while GeneratePreview works: no handlers, no bindings (the text shows the path), no code of the program; every element goes into `fxAll`
        [ThreadStatic] private static bool s_preview;

        /// <summary>The script of `document`. `sourceName` (the file name) only goes into the comment at the top. Throws <see cref="MarkupException"/> if the markup has mistakes.</summary>
        public static string Generate(MarkupDocument document, string sourceName = "") => Generate(document, sourceName, false);

        /// <summary>A script that only DRAWS the interface, for the design view of the editor: class `FxPreview` with a window of the size of the markup (a view is put into one), the elements,
        /// styles and templates, but no handlers, no data bindings (a text that is bound shows `‹Path›`) and nothing of the code-behind (`{Enum ...}`/`{Expr ...}` values are left out). The
        /// script ends by drawing twice and printing `@fb ID` (the framebuffer) and `@N X Y W H` for every element (in the order of <see cref="MarkupDocument.AllElements"/>, parts without
        /// an object of their own are not among them).</summary>
        public static string GeneratePreview(MarkupDocument document, string sourceName = "")
        {
            s_preview = true;
            try
            {
                string script = Generate(document, sourceName, true);
                return script + "\nvar fxP = new FxPreview()\nfxP.ui.Draw()\nfxP.ui.Draw()\nfxP.FxDump()\n";
            }
            finally { s_preview = false; }
        }

        private static string Generate(MarkupDocument document, string sourceName, bool preview)
        {
            var errors = new List<MarkupDiagnostic>(document.Diagnostics);
            var infos = new Dictionary<MarkupElement, Info>();
            int counter = 0;
            foreach (var element in document.AllElements())
            {
                var def = MarkupSchema.Find(element.Tag)!;
                infos[element] = new Info { Element = element, Def = def, Local = "e" + counter++, Field = def.Class.Length == 0 ? null : element.Name };
            }
            // the elements inside control templates have no fields (their names are the names of the parts)
            foreach (var element in document.TemplateElements())
            {
                var def = MarkupSchema.Find(element.Tag)!;
                infos[element] = new Info { Element = element, Def = def, Local = "e" + counter++, Field = null };
            }

            // converters: the declared ones and the built-in ones that are used
            var converters = new List<(string Key, string Type)>();
            foreach (var declared in document.Converters) converters.Add((declared.Key, declared.Type));
            var bindings = new List<Binding>();
            foreach (var element in preview ? Enumerable.Empty<MarkupElement>() : document.AllElements())
            {
                var info = infos[element];
                foreach (var attribute in element.Attributes)
                {
                    if (attribute.Value is not BindingValue value) continue;
                    var property = info.Def.Property(attribute.Name)!;
                    if (property.Method != null || info.Def.Class.Length == 0)
                    {
                        errors.Add(new MarkupDiagnostic(attribute.Line, $"'{attribute.Name}' of '{info.Def.Tag}' cannot be bound."));
                        continue;
                    }
                    info.Field ??= "fxEl" + info.Local.Substring(1);

                    string? converterField = null;
                    if (value.Converter != null)
                    {
                        if (converters.All(c => c.Key != value.Converter) && MarkupSchema.BuiltInConverters.TryGetValue(value.Converter, out var builtIn))
                            converters.Add((value.Converter, builtIn));
                        converterField = "fxConv_" + value.Converter;
                    }

                    string source = "this.dataContext";
                    if (value.ElementName != null)
                    {
                        var named = document.AllElements().FirstOrDefault(e => e.Name == value.ElementName);
                        if (named != null) source = "this." + value.ElementName;
                    }
                    bindings.Add(new Binding { Index = bindings.Count, Target = info, Property = property, Value = value, Source = source, ConverterField = converterField });
                }
            }

            if (errors.Count > 0) throw new MarkupException(errors);

            var code = new Writer();
            string file = string.IsNullOrEmpty(sourceName) ? "the markup" : Path.GetFileName(sourceName);
            code.Line($"// Generated from {file} - do not edit, change the markup. Derive from the class and override the methods named in on...=\"...\":");
            code.Line($"//     class {document.ClassName} : {document.BaseName} {{ Save(sender) {{ ... }} }}");
            code.Line("#import \"ui\"");
            code.Line();
            code.Line($"class {(preview ? "FxPreview" : document.BaseName)} {{");
            code.Indent++;

            // ---- fields ----
            if (document.Kind == MarkupRootKind.Window || preview)
            {
                code.Line("Framebuffer framebuffer");
                code.Line("Window window");
                code.Line("UI.Root ui");
            }
            if (document.Kind == MarkupRootKind.View) code.Line("UI.Panel view");
            if (preview) code.Line("List fxAll");
            foreach (var info in infos.Values.Where(i => i.Field != null)) code.Line($"{info.Def.Class} {info.Field}");
            foreach (var style in document.Styles.Where(st => st.Key != null)) code.Line($"UI.Style fxStyle_{style.Key}");
            foreach (var template in document.Templates) code.Line($"UI.ControlTemplate fxTemplate_{template.Key}");
            foreach (var (key, type) in converters) code.Line($"{type} fxConv_{key}");
            if (bindings.Count > 0)
            {
                code.Line("// the data context of the bindings: set it with SetDataContext(object)");
                code.Line("class dataContext");
                foreach (var b in bindings)
                {
                    code.Line($"List fxHandles{b.Index}");
                    code.Line($"bool fxBusy{b.Index} = false");
                }
            }
            code.Line();

            // ---- constructor ----
            code.Line("construct() {");
            code.Indent++;
            if (document.Kind == MarkupRootKind.Window || preview)
            {
                code.Line($"this.framebuffer = new Framebuffer({document.Width}, {document.Height})");
                code.Line($"this.window = new Window(this.framebuffer, {Quote(document.Title)})");
                code.Line("this.ui = new UI.Root(this.framebuffer, this.window)");
            }
            if (document.Kind == MarkupRootKind.View)
            {
                code.Line($"this.view = new UI.Panel(0, 0, {document.Width}, {document.Height})");
                if (preview) code.Line("this.ui.Add(this.view)");
            }
            if (preview) code.Line("this.fxAll = new List()");
            foreach (var (key, type) in converters) code.Line($"this.fxConv_{key} = new {type}()");
            foreach (var b in bindings) code.Line($"this.fxHandles{b.Index} = new List()");

            string container = document.Kind == MarkupRootKind.Window ? "this.ui" : "this.view";
            EmitResources(code, document, infos, document.Kind == MarkupRootKind.Window ? "this.ui.resources" : "this.view.Resources()", errors);
            foreach (var child in document.Children) EmitElement(code, infos, child, container, null, errors);
            if (bindings.Count > 0) code.Line("this.fxBindAll()");
            code.Indent--;
            code.Line("}");
            code.Line();

            // ---- handlers (empty here: the derived class overrides them) ----
            var handlers = new Dictionary<string, string[]>();
            foreach (var element in preview ? Enumerable.Empty<MarkupElement>() : document.AllElements())
                foreach (var (eventName, handler, line) in element.Handlers)
                {
                    var args = infos[element].Def.Event(eventName)!.Args;
                    if (handlers.TryGetValue(handler, out var existing) && !existing.SequenceEqual(args))
                        errors.Add(new MarkupDiagnostic(line, $"The method '{handler}' handles events with different parameters (sender{string.Concat(existing.Select(a => ", " + a))} and sender{string.Concat(args.Select(a => ", " + a))})."));
                    else handlers[handler] = args;
                }
            foreach (var (handler, args) in handlers)
            {
                code.Line($"// the handler of an event in the markup - override it in the derived class");
                code.Line($"{handler}(sender{string.Concat(args.Select(a => ", " + a))}) {{ }}");
            }
            if (handlers.Count > 0) code.Line();

            // ---- run / attach ----
            if (preview)
            {
                code.Line("// where every element ended up, for the design view");
                code.Line("FxDump() {");
                code.Line("    print(\"@fb \" + this.framebuffer.id)");
                code.Line("    for (var i = 0; i < this.fxAll.count; i = i + 1) {");
                code.Line("        var e = this.fxAll[i]");
                code.Line("        print(\"@\" + i + \" \" + e.ax + \" \" + e.ay + \" \" + e.actualWidth + \" \" + e.actualHeight)");
                code.Line("    }");
                code.Line("}");
            }
            else if (document.Kind == MarkupRootKind.Window)
            {
                code.Line("// called once per cycle of Run() - override it for a loop of your own");
                code.Line("OnTick() { }");
                code.Line();
                code.Line("// shows the window and runs until it is closed");
                code.Line("Run() {");
                code.Line("    while (this.ui.Tick()) { this.OnTick() }");
                code.Line("}");
            }
            else
            {
                code.Line("// adds the view to a container (UI.Root.content, a Panel or a Stack)");
                code.Line("Attach(container) { container.Add(this.view) }");
            }

            if (bindings.Count > 0) EmitBindings(code, bindings);

            code.Indent--;
            code.Line("}");

            if (errors.Count > 0) throw new MarkupException(errors);
            return code.ToString();
        }

        /// <summary>Writes the code that builds the element and everything inside it, and adds it to `parent` (the code of the container). `template` is set for the elements of a control template
        /// (they have no fields; a part that is the target of a binding or a setter gets its name); `parentDef` is the definition of the container.</summary>
        private static void EmitElement(Writer code, Dictionary<MarkupElement, Info> infos, MarkupElement element, string? parent, ElementDef? parentDef, List<MarkupDiagnostic> errors,
            List<(string Part, string PartProperty, string OwnerProperty)>? partBindings = null, ElementDef? templateTarget = null)
        {
            var info = infos[element];
            string local = info.Local;

            // a part without an object of its own (a list item, a column, a menu separator): only the call that adds it
            if (info.Def.Class.Length == 0)
            {
                string call = parentDef != null ? info.Def.AddCallIn(parentDef.Tag) : info.Def.AddCall;
                call = call.Replace("{p}", parent ?? "");
                foreach (var property in info.Def.Properties)
                {
                    string placeholder = "{" + property.Name + "}";
                    if (!call.Contains(placeholder)) continue;
                    var attribute = element.Find(property.Name);
                    string? value = null;
                    if (attribute != null) value = ValueCode(property, attribute, errors);
                    call = call.Replace(placeholder, value ?? property.Default ?? "\"\"");
                }
                code.Line(call);
                return;
            }

            code.Line($"var {local} = {info.Def.Create}");
            if (s_preview && partBindings == null && !info.Def.IsPart) code.Line($"this.fxAll.Add({local})");

            string? partName = null;
            if (partBindings != null)
            {
                bool bound = element.Attributes.Any(a => a.Value is TemplateBindingValue);
                if (element.Name != null || bound)
                {
                    partName = element.Name ?? "fxPart" + local.Substring(1);
                    code.Line($"{local}.name = {Quote(partName)}");
                }
            }

            foreach (var attribute in element.Attributes)
            {
                if (attribute.Value is BindingValue shown)
                {
                    // the design view shows the path of a bound text; the other bound properties keep their default
                    if (s_preview && info.Def.Property(attribute.Name) is { Kind: PropertyKind.Text, Method: null } textProperty) code.Line($"{local}.{textProperty.FieldName} = {Quote("‹" + shown.Path + "›")}");
                    continue; // made by the binding
                }
                var property = info.Def.Property(attribute.Name)!;
                if (attribute.Value is TemplateBindingValue binding)
                {
                    if (partBindings != null && partName != null)
                    {
                        string ownerField = templateTarget?.Property(binding.OwnerProperty)?.FieldName
                            ?? MarkupSchema.StateProperties.FirstOrDefault(p => p.Name == binding.OwnerProperty)?.FieldName ?? binding.OwnerProperty;
                        partBindings.Add((partName, property.FieldName, ownerField));
                    }
                    continue;
                }
                string? value = ValueCode(property, attribute, errors);
                if (value == null) continue;
                if (property.Method != null) code.Line($"{local}.{property.Method}({value})");
                else code.Line($"{local}.{property.FieldName} = {value}");
            }

            foreach (var (eventName, handler, _) in s_preview ? new List<(string Event, string Handler, int Line)>() : element.Handlers)
            {
                var args = info.Def.Event(eventName)!.Args;
                string parameters = string.Join(", ", args);
                code.Line($"{local}.{eventName} = func ({parameters}) on this => {{ {handler}({local}{string.Concat(args.Select(a => ", " + a))}) }}");
            }

            foreach (var child in element.Children) EmitElement(code, infos, child, local, info.Def, errors, partBindings, templateTarget);
            if (parent != null)
            {
                // a part knows how its container takes it; any other element is taken the way its container says (`Add`, `SetChild`, `SetContent`)
                string add = info.Def.IsPart && parentDef != null ? info.Def.AddCallIn(parentDef.Tag) : parentDef?.AddCall ?? "{p}.Add({e})";
                code.Line(add.Replace("{p}", parent).Replace("{e}", local));
            }
            if (info.Field != null) code.Line($"this.{info.Field} = {local}");
        }

        /// <summary>The fire expression of the value of an attribute (a literal, `{Enum}`, `{Expr}`), by the type of the property.</summary>
        private static string? ValueCode(PropertyDef property, MarkupAttribute attribute, List<MarkupDiagnostic> errors) => attribute.Value switch
        {
            ExpressionValue when s_preview => null,   // code of the program: the design view does not run it
            ExpressionValue expression => property.Kind switch
            {
                PropertyKind.Color when !expression.IsRaw => $"new SolidBrush({expression.Code})",
                PropertyKind.Pen when !expression.IsRaw => $"new Pen({expression.Code})",
                _ => expression.Code,
            },
            LiteralValue literal => Convert(property, literal.Text, attribute.Line, errors),
            _ => null,
        };

        // ---- styles, templates ----

        private static void EmitResources(Writer code, MarkupDocument document, Dictionary<MarkupElement, Info> infos, string resources, List<MarkupDiagnostic> errors)
        {
            if (document.Styles.Count == 0 && document.Templates.Count == 0) return;
            int counter = 0;

            string SetterCode(SetterDeclaration setter, ElementDef def, string owner, string? partTag)
            {
                var property = def.Property(setter.Property);
                if (property == null) return "";
                if (property.Method != null)
                {
                    errors.Add(new MarkupDiagnostic(setter.Line, $"'{setter.Property}' is set by a method and cannot be part of a style."));
                    return "";
                }
                string? value = ValueCode(property, new MarkupAttribute(property.Name, setter.Value, setter.Line), errors);
                if (value == null) return "";
                string target = setter.Target != null ? $", {Quote(setter.Target)}" : "";
                return $"{owner}.Set({Quote(property.FieldName)}, {value}{target})";
            }

            void EmitTriggers(IEnumerable<TriggerDeclaration> triggers, ElementDef def, string owner, string addCall, TemplateDeclaration? template, Dictionary<string, MarkupElement>? parts)
            {
                foreach (var trigger in triggers)
                {
                    string t = "tr" + counter++;
                    for (int c = 0; c < trigger.Conditions.Count; c++)
                    {
                        var (propertyName, value, source, line) = trigger.Conditions[c];
                        var sourceDef = source != null && parts != null && parts.TryGetValue(source, out var part) ? MarkupSchema.Find(part.Tag) : def;
                        var property = sourceDef?.Property(propertyName) ?? MarkupSchema.StateProperties.FirstOrDefault(p => p.Name == propertyName);
                        if (property == null) continue;
                        string? code1 = ValueCode(property, new MarkupAttribute(property.Name, value, line), errors);
                        if (code1 == null) continue;
                        string src = source != null ? $", {Quote(source)}" : "";
                        if (c == 0) code.Line($"var {t} = new UI.Trigger({Quote(property.FieldName)}, {code1}{src})");
                        else code.Line($"{t}.And({Quote(property.FieldName)}, {code1}{src})");
                    }
                    foreach (var setter in trigger.Setters)
                    {
                        var setterDef = setter.Target != null && parts != null && parts.TryGetValue(setter.Target, out var part) ? MarkupSchema.Find(part.Tag) : def;
                        if (setterDef == null) continue;
                        string line = SetterCode(setter, setterDef, t, null);
                        if (line.Length > 0) code.Line(line);
                    }
                    code.Line($"{owner}.{addCall}({t})");
                }
            }

            // styles: the ones that others are based on first
            var ordered = new List<StyleDeclaration>();
            var pending = new List<StyleDeclaration>(document.Styles);
            while (pending.Count > 0)
            {
                var ready = pending.FirstOrDefault(st => st.BasedOn == null || ordered.Any(o => o.Key == st.BasedOn)) ?? pending[0];
                ordered.Add(ready);
                pending.Remove(ready);
            }
            foreach (var style in ordered)
            {
                var def = MarkupSchema.FindTarget(style.Target)!;
                string v = "st" + counter++;
                code.Line($"var {v} = new UI.Style({Quote(def.StyleTarget)})");
                if (style.BasedOn != null) code.Line($"{v}.basedOn = this.fxStyle_{style.BasedOn}");
                foreach (var setter in style.Setters)
                {
                    string line = SetterCode(setter, def, v, null);
                    if (line.Length > 0) code.Line(line);
                }
                EmitTriggers(style.Triggers, def, v, "AddTrigger", null, null);
                if (style.Key != null)
                {
                    code.Line($"{resources}.Set({Quote(style.Key)}, {v})");
                    code.Line($"this.fxStyle_{style.Key} = {v}");
                }
                else code.Line($"{resources}.AddStyle({v})");
            }

            foreach (var template in document.Templates)
            {
                if (template.Root == null) continue;
                var def = MarkupSchema.FindTarget(template.Target)!;
                string v = "tp" + counter++;
                var bindings = new List<(string Part, string PartProperty, string OwnerProperty)>();
                code.Line($"var {v} = new UI.ControlTemplate(func (owner) on this => {{");
                code.Indent++;
                EmitElement(code, infos, template.Root, null, null, errors, bindings, def);
                code.Line($"return {infos[template.Root].Local}");
                code.Indent--;
                code.Line("})");
                foreach (var (part, partProperty, ownerProperty) in bindings) code.Line($"{v}.Bind({Quote(part)}, {Quote(partProperty)}, {Quote(ownerProperty)})");
                var parts = new Dictionary<string, MarkupElement>();
                var stack = new Stack<MarkupElement>(new[] { template.Root });
                while (stack.Count > 0)
                {
                    var e = stack.Pop();
                    if (e.Name != null) parts[e.Name] = e;
                    foreach (var c in e.Children) stack.Push(c);
                }
                EmitTriggers(template.Triggers, def, v, "AddTrigger", template, parts);
                code.Line($"{resources}.Set({Quote(template.Key)}, {v})");
                code.Line($"this.fxTemplate_{template.Key} = {v}");
            }
        }

        /// <summary>The fire expression of a plain value, by the type of the property; reports a value that does not fit.</summary>
        private static string? Convert(PropertyDef property, string text, int line, List<MarkupDiagnostic> errors)
        {
            string trimmed = text.Trim();
            switch (property.Kind)
            {
                case PropertyKind.Text:
                    return Quote(text);
                case PropertyKind.Int:
                    if (int.TryParse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int n)) return n.ToString(CultureInfo.InvariantCulture);
                    if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && int.TryParse(trimmed.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hex)) return "0x" + hex.ToString("X", CultureInfo.InvariantCulture);
                    errors.Add(new MarkupDiagnostic(line, $"'{property.Name}' needs a whole number (or {{Enum Type.Member}}), not '{text}'."));
                    return null;
                case PropertyKind.Bool:
                    if (trimmed.Equals("true", StringComparison.OrdinalIgnoreCase)) return "true";
                    if (trimmed.Equals("false", StringComparison.OrdinalIgnoreCase)) return "false";
                    errors.Add(new MarkupDiagnostic(line, $"'{property.Name}' needs true or false, not '{text}'."));
                    return null;
                case PropertyKind.Orientation:
                    if (trimmed.Equals("Horizontal", StringComparison.OrdinalIgnoreCase)) return "true";
                    if (trimmed.Equals("Vertical", StringComparison.OrdinalIgnoreCase)) return "false";
                    errors.Add(new MarkupDiagnostic(line, $"'{property.Name}' needs Horizontal or Vertical, not '{text}'."));
                    return null;
                case PropertyKind.Color:
                    if (TryParseColor(trimmed, out int r, out int g, out int b)) return $"new SolidBrush(UI.Color.Rgb({r}, {g}, {b}))";
                    if (int.TryParse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int raw)) return $"new SolidBrush({raw.ToString(CultureInfo.InvariantCulture)})";
                    errors.Add(new MarkupDiagnostic(line, $"'{property.Name}' needs a colour like #RRGGBB (or a whole number or {{Enum Type.Member}}), not '{text}'."));
                    return null;
                case PropertyKind.Pen:
                {
                    var parts = trimmed.Split(',', StringSplitOptions.TrimEntries);
                    string? colorCode = null;
                    if (TryParseColor(parts[0], out int pr, out int pg, out int pb)) colorCode = $"UI.Color.Rgb({pr}, {pg}, {pb})";
                    else if (int.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int rawPen)) colorCode = rawPen.ToString(CultureInfo.InvariantCulture);
                    if (colorCode != null && parts.Length == 1) return $"new Pen({colorCode})";
                    if (colorCode != null && parts.Length == 2 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int width) && width > 0) return $"new Pen({colorCode}, {width})";
                    errors.Add(new MarkupDiagnostic(line, $"'{property.Name}' needs a pen like #RRGGBB or #RRGGBB,3 (colour and width), not '{text}'."));
                    return null;
                }
                case PropertyKind.Thickness:
                {
                    var numbers = trimmed.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if ((numbers.Length is 1 or 2 or 4) && numbers.All(n => int.TryParse(n, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)))
                        return $"new UI.Thickness({string.Join(", ", numbers.Select(n => int.Parse(n, CultureInfo.InvariantCulture)))})";
                    errors.Add(new MarkupDiagnostic(line, $"'{property.Name}' needs 1 number (all sides), 2 (horizontal, vertical) or 4 (left, top, right, bottom), not '{text}'."));
                    return null;
                }
                case PropertyKind.Enum:
                {
                    string? choice = property.Choices?.FirstOrDefault(c => c.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
                    if (choice != null) return $"{property.EnumType}.{choice}";
                    errors.Add(new MarkupDiagnostic(line, $"'{property.Name}' needs one of {string.Join(", ", property.Choices ?? Array.Empty<string>())}, not '{text}'."));
                    return null;
                }
                case PropertyKind.Image:
                    if (trimmed.Length == 0)
                    {
                        errors.Add(new MarkupDiagnostic(line, $"'{property.Name}' needs the path of an image file."));
                        return null;
                    }
                    return $"Framebuffer.FromResource(new Resource({Quote(trimmed)}))";
                case PropertyKind.StyleRef:
                    return "this.fxStyle_" + trimmed;
                case PropertyKind.TemplateRef:
                    return "this.fxTemplate_" + trimmed;
            }
            return null;
        }

        /// <summary>`#RRGGBB` (or `#RGB`).</summary>
        public static bool TryParseColor(string text, out int r, out int g, out int b)
        {
            r = g = b = 0;
            if (!text.StartsWith('#')) return false;
            string hex = text.Substring(1);
            if (hex.Length == 3) hex = string.Concat(hex.Select(c => new string(c, 2)));
            if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb)) return false;
            r = (rgb >> 16) & 0xFF;
            g = (rgb >> 8) & 0xFF;
            b = rgb & 0xFF;
            return true;
        }

        // ---- data binding ----

        private static void EmitBindings(Writer code, List<Binding> bindings)
        {
            code.Line();
            code.Line("// ---- data binding: the properties follow the data context (probes, SPEC 8.14) ----");
            code.Line("// sets the object the bindings read from (and write to) and connects them; null disconnects");
            code.Line("SetDataContext(context) {");
            code.Line("    this.fxUnbindAll()");
            code.Line("    this.dataContext = context");
            code.Line("    this.fxBindAll()");
            code.Line("}");
            code.Line();
            code.Line("fxBindAll() {");
            code.Indent++;
            foreach (var b in bindings) code.Line($"this.fxBind{b.Index}()");
            code.Indent--;
            code.Line("}");
            code.Line();
            code.Line("fxUnbindAll() {");
            code.Indent++;
            foreach (var b in bindings) code.Line($"this.fxUnbind{b.Index}()");
            code.Indent--;
            code.Line("}");

            foreach (var b in bindings)
            {
                var path = b.Segments;
                string target = $"this.{b.Target.Field}";
                code.Line();
                code.Line($"// {b.Target.Def.Tag}.{b.Property.Name} <- {(b.FromDataContext ? "" : b.Value.ElementName + ".")}{b.Value.Path} ({b.Value.Mode})");

                // unbind: silence every probe of the binding
                code.Line($"fxUnbind{b.Index}() {{");
                code.Line($"    for (var i = 0; i < this.fxHandles{b.Index}.count; i = i + 1) {{");
                code.Line($"        var h = this.fxHandles{b.Index}[i]");
                code.Line("        silence h");
                code.Line("    }");
                code.Line($"    this.fxHandles{b.Index} = new List()");
                code.Line("}");
                code.Line();

                // bind: a probe on every step of the path (the last one updates the target, an earlier one connects the path again), and for two-way the probe on the target
                code.Line($"fxBind{b.Index}() {{");
                code.Indent++;
                code.Line($"this.fxUnbind{b.Index}()");
                if (b.Value.Mode != BindingMode.OneTime)
                {
                    code.Line($"var o0 = {b.Source}");
                    for (int i = 0; i < path.Length; i++)
                    {
                        string handler = i == path.Length - 1 ? $"fxUpdate{b.Index}()" : $"fxBind{b.Index}()";
                        code.Line($"if (o{i} != undefined) {{");
                        code.Indent++;
                        code.Line($"var f{i} = func (o, v) on this => {{ {handler} }}");
                        code.Line($"var p{i} = probe o{i}.{path[i]} changed f{i}");
                        code.Line($"this.fxHandles{b.Index}.Add(p{i})");
                        if (i < path.Length - 1) code.Line($"var o{i + 1} = o{i}.{path[i]}");
                    }
                    for (int i = path.Length - 1; i >= 0; i--)
                    {
                        code.Indent--;
                        code.Line("}");
                    }
                    if (b.Value.Mode == BindingMode.TwoWay)
                    {
                        code.Line($"var g = func (o, v) on this => {{ fxPush{b.Index}(v) }}");
                        code.Line($"var pg = probe {target}.{b.Property.FieldName} changed g");
                        code.Line($"this.fxHandles{b.Index}.Add(pg)");
                    }
                }
                code.Line($"this.fxUpdate{b.Index}()");
                code.Indent--;
                code.Line("}");
                code.Line();

                // update: read the source, convert, write the target
                code.Line($"fxUpdate{b.Index}() {{");
                code.Indent++;
                code.Line($"if (this.fxBusy{b.Index}) {{ return }}");
                code.Line($"var v = {b.Source}");
                code.Line("if (v == undefined) { return }");
                foreach (var segment in path)
                {
                    code.Line($"v = v.{segment}");
                    if (segment != path[^1]) code.Line("if (v == undefined) { return }");
                }
                if (b.ConverterField != null) code.Line($"v = this.{b.ConverterField}.Convert(v)");
                code.Line($"this.fxBusy{b.Index} = true");
                code.Line($"{target}.{b.Property.FieldName} = v");
                code.Line($"this.fxBusy{b.Index} = false");
                code.Indent--;
                code.Line("}");

                // push: the other direction
                if (b.Value.Mode == BindingMode.TwoWay)
                {
                    code.Line();
                    code.Line($"fxPush{b.Index}(value) {{");
                    code.Indent++;
                    code.Line($"if (this.fxBusy{b.Index}) {{ return }}");
                    code.Line($"var o = {b.Source}");
                    code.Line("if (o == undefined) { return }");
                    foreach (var segment in path.Take(path.Length - 1))
                    {
                        code.Line($"o = o.{segment}");
                        code.Line("if (o == undefined) { return }");
                    }
                    if (b.ConverterField != null) code.Line($"value = this.{b.ConverterField}.ConvertBack(value)");
                    code.Line($"this.fxBusy{b.Index} = true");
                    code.Line($"o.{path[^1]} = value");
                    code.Line($"this.fxBusy{b.Index} = false");
                    code.Indent--;
                    code.Line("}");
                }
            }
        }

        /// <summary>A fire string literal.</summary>
        public static string Quote(string text)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in text)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.Append('"').ToString();
        }

        private sealed class Writer
        {
            private readonly StringBuilder _text = new();
            public int Indent;
            public void Line(string text = "")
            {
                if (text.Length > 0) _text.Append(' ', Indent * 4).Append(text);
                _text.Append('\n');
            }
            public override string ToString() => _text.ToString();
        }
    }
}
