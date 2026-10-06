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

        /// <summary>The script of `document`. `sourceName` (the file name) only goes into the comment at the top. Throws <see cref="MarkupException"/> if the markup has mistakes.</summary>
        public static string Generate(MarkupDocument document, string sourceName = "")
        {
            var errors = new List<MarkupDiagnostic>(document.Diagnostics);
            var infos = new Dictionary<MarkupElement, Info>();
            int counter = 0;
            foreach (var element in document.AllElements())
            {
                var def = MarkupSchema.Find(element.Tag)!;
                infos[element] = new Info { Element = element, Def = def, Local = "e" + counter++, Field = element.Name };
            }

            // converters: the declared ones and the built-in ones that are used
            var converters = new List<(string Key, string Type)>();
            foreach (var declared in document.Converters) converters.Add((declared.Key, declared.Type));
            var bindings = new List<Binding>();
            foreach (var element in document.AllElements())
            {
                var info = infos[element];
                foreach (var attribute in element.Attributes)
                {
                    if (attribute.Value is not BindingValue value) continue;
                    var property = info.Def.Property(attribute.Name)!;
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
            code.Line($"class {document.BaseName} {{");
            code.Indent++;

            // ---- fields ----
            if (document.Kind == MarkupRootKind.Window)
            {
                code.Line("Framebuffer framebuffer");
                code.Line("Window window");
                code.Line("UI.Root ui");
            }
            else code.Line("UI.Panel view");
            foreach (var info in infos.Values.Where(i => i.Field != null)) code.Line($"{info.Def.Class} {info.Field}");
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
            if (document.Kind == MarkupRootKind.Window)
            {
                code.Line($"this.framebuffer = new Framebuffer({document.Width}, {document.Height})");
                code.Line($"this.window = new Window(this.framebuffer, {Quote(document.Title)})");
                code.Line("this.ui = new UI.Root(this.framebuffer, this.window)");
            }
            else code.Line($"this.view = new UI.Panel(0, 0, {document.Width}, {document.Height})");
            foreach (var (key, type) in converters) code.Line($"this.fxConv_{key} = new {type}()");
            foreach (var b in bindings) code.Line($"this.fxHandles{b.Index} = new List()");

            string container = document.Kind == MarkupRootKind.Window ? "this.ui" : "this.view";
            foreach (var child in document.Children) EmitElement(code, infos, child, container, errors);
            if (bindings.Count > 0) code.Line("this.fxBindAll()");
            code.Indent--;
            code.Line("}");
            code.Line();

            // ---- handlers (empty here: the derived class overrides them) ----
            var handlers = document.AllElements().SelectMany(e => e.Handlers).Select(h => h.Handler).Distinct().ToList();
            foreach (var handler in handlers)
            {
                code.Line($"// the handler of an event in the markup - override it in the derived class");
                code.Line($"{handler}(sender) {{ }}");
            }
            if (handlers.Count > 0) code.Line();

            // ---- run / attach ----
            if (document.Kind == MarkupRootKind.Window)
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

        private static void EmitElement(Writer code, Dictionary<MarkupElement, Info> infos, MarkupElement element, string parent, List<MarkupDiagnostic> errors)
        {
            var info = infos[element];
            string local = info.Local;
            code.Line($"var {local} = {info.Def.Create}");

            foreach (var attribute in element.Attributes)
            {
                if (attribute.Value is BindingValue) continue; // made by the binding
                var property = info.Def.Property(attribute.Name)!;
                string? value = attribute.Value switch
                {
                    ExpressionValue expression => expression.Code,
                    LiteralValue literal => Convert(property, literal.Text, attribute.Line, errors),
                    _ => null,
                };
                if (value != null) code.Line($"{local}.{property.FieldName} = {value}");
            }

            foreach (var (eventName, handler, _) in element.Handlers)
                code.Line($"{local}.{eventName} = func () on this => {{ {handler}({local}) }}");

            foreach (var child in element.Children) EmitElement(code, infos, child, local, errors);
            code.Line($"{parent}.Add({local})");
            if (info.Field != null) code.Line($"this.{info.Field} = {local}");
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
                    if (TryParseColor(trimmed, out int r, out int g, out int b)) return $"UI.Color.Rgb({r}, {g}, {b})";
                    if (int.TryParse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int raw)) return raw.ToString(CultureInfo.InvariantCulture);
                    errors.Add(new MarkupDiagnostic(line, $"'{property.Name}' needs a colour like #RRGGBB (or a whole number or {{Enum Type.Member}}), not '{text}'."));
                    return null;
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
