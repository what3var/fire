namespace fire.UI.Markup
{
    /// <summary>A problem in a markup file (the line is 1-based, 0 = unknown).</summary>
    public sealed record MarkupDiagnostic(int Line, string Message)
    {
        public override string ToString() => Line > 0 ? $"line {Line}: {Message}" : Message;
    }

    /// <summary>The markup could not be translated; carries all diagnostics found.</summary>
    public sealed class MarkupException : Exception
    {
        public IReadOnlyList<MarkupDiagnostic> Diagnostics { get; }

        public MarkupException(IReadOnlyList<MarkupDiagnostic> diagnostics)
            : base(string.Join("; ", diagnostics.Select(d => d.ToString()))) => Diagnostics = diagnostics;
    }

    /// <summary>What the root element is: a window of its own (framebuffer, window and UI root are created) or a view (a panel that is added to a container).</summary>
    public enum MarkupRootKind { Window, View }

    /// <summary>How a binding moves values.</summary>
    public enum BindingMode
    {
        /// <summary>Source to target, whenever the source changes (default).</summary>
        OneWay,
        /// <summary>Both directions (a TextBox writes into the source as the user types).</summary>
        TwoWay,
        /// <summary>Source to target once, when the binding is made.</summary>
        OneTime,
    }

    /// <summary>The value of an attribute: a literal, a binding or an expression.</summary>
    public abstract record MarkupValue;

    /// <summary>A plain value as written (`12`, `true`, `#FF0000`, a text).</summary>
    public sealed record LiteralValue(string Text) : MarkupValue;

    /// <summary>`{Enum Type.Member}`, `{Static Type.Member}` or `{Expr ...}`: a piece of fire code that is used as it is (an enum member, a constant, any expression). For a colour property
    /// `Enum`/`Static` give a colour NUMBER (the generated script makes a brush of it), `Expr` (<paramref name="IsRaw"/>) a ready value (a brush).</summary>
    public sealed record ExpressionValue(string Code, bool IsRaw = false) : MarkupValue;

    /// <summary>`{Binding Path, Mode=TwoWay, Converter=Key, ElementName=name}`: the value of a property of the data context (or of a named element) follows the property.</summary>
    public sealed record BindingValue(string Path, BindingMode Mode, string? Converter, string? ElementName) : MarkupValue;

    /// <summary>`{TemplateBinding Property}` in a control template: the property of the part follows the property of the control that the template belongs to.</summary>
    public sealed record TemplateBindingValue(string OwnerProperty) : MarkupValue;

    /// <summary>An attribute of an element.</summary>
    public sealed record MarkupAttribute(string Name, MarkupValue Value, int Line);

    /// <summary>An element of the interface (`Button`, `Stack`, ...) with its attributes, its handlers (`onClick="Name"`) and its children.</summary>
    public sealed class MarkupElement
    {
        public string Tag { get; init; } = "";
        public int Line { get; init; }
        /// <summary>The name of the field the generated class gets (`name="..."`), null when the element is not named.</summary>
        public string? Name { get; set; }
        public List<MarkupAttribute> Attributes { get; } = new();
        /// <summary>Event name (`onClick`) to the name of the method that handles it.</summary>
        public List<(string Event, string Handler, int Line)> Handlers { get; } = new();
        public List<MarkupElement> Children { get; } = new();

        public MarkupAttribute? Find(string name) => Attributes.FirstOrDefault(a => a.Name == name);
    }

    /// <summary>`<Converter key="Upper" type="UpperConverter"/>`: a converter that bindings refer to by its key.</summary>
    public sealed record ConverterDeclaration(string Key, string Type, int Line);

    /// <summary>`<Setter property="background" value="#336699"/>`: gives a property a value (`Target`: the name of a part, inside a control template).</summary>
    public sealed record SetterDeclaration(string Property, MarkupValue Value, string? Target, int Line);

    /// <summary>`<Trigger property="hover" value="true">`: while all conditions hold (the first one is the attribute pair, more come from `<Condition .../>`), the setters apply.</summary>
    public sealed class TriggerDeclaration
    {
        public int Line { get; init; }
        /// <summary>Property, value, source (a part name, or null for the element itself), line.</summary>
        public List<(string Property, MarkupValue Value, string? Source, int Line)> Conditions { get; } = new();
        public List<SetterDeclaration> Setters { get; } = new();
    }

    /// <summary>`<Style key="Primary" target="Button" basedOn="Base">`: setters and triggers for one kind of element. Without a key it is the implicit style of the kind for the whole interface.</summary>
    public sealed class StyleDeclaration
    {
        public string? Key { get; init; }
        public string Target { get; init; } = "";
        public string? BasedOn { get; init; }
        public int Line { get; init; }
        public List<SetterDeclaration> Setters { get; } = new();
        public List<TriggerDeclaration> Triggers { get; } = new();
    }

    /// <summary>`<ControlTemplate key="Fancy" target="Button">`: the look of a control as a tree of elements (the single child element; parts have a `name`), with triggers.</summary>
    public sealed class TemplateDeclaration
    {
        public string Key { get; init; } = "";
        public string Target { get; init; } = "";
        public int Line { get; init; }
        public MarkupElement? Root { get; set; }
        public List<TriggerDeclaration> Triggers { get; } = new();
    }

    /// <summary>`<DataTemplate key="Person">`: how one data item of a list looks - the single child element. `{Binding Path}` inside it binds to the ITEM (an empty path, `{Binding}`, is the item itself).</summary>
    public sealed class DataTemplateDeclaration
    {
        public string Key { get; init; } = "";
        public int Line { get; init; }
        public MarkupElement? Root { get; set; }
    }

    /// <summary>`<CollectionView key="Sorted" source="{Binding People}" sortBy="name" descending="true" filter="{Expr ...}"/>`: a sorted/filtered view of a list that lists show (`view="Sorted"`).
    /// Its attributes are in <see cref="Element"/> (tag `CollectionView`, see <see cref="MarkupSchema.CollectionViewDef"/>).</summary>
    public sealed class ViewDeclaration
    {
        public string Key { get; init; } = "";
        public int Line { get; init; }
        public MarkupElement Element { get; init; } = new();
    }

    /// <summary>A parsed markup file.</summary>
    public sealed class MarkupDocument
    {
        public MarkupRootKind Kind { get; init; }
        /// <summary>The name of the class of the code-behind (`class="..."`).</summary>
        public string ClassName { get; init; } = "";
        /// <summary>The name of the generated base class (`base="..."`, else the class name plus `Base`).</summary>
        public string BaseName { get; init; } = "";
        public string Title { get; init; } = "fire";
        public int Width { get; init; } = 640;
        public int Height { get; init; } = 480;
        public List<ConverterDeclaration> Converters { get; } = new();
        public List<StyleDeclaration> Styles { get; } = new();
        public List<TemplateDeclaration> Templates { get; } = new();
        public List<DataTemplateDeclaration> DataTemplates { get; } = new();
        public List<ViewDeclaration> Views { get; } = new();
        public List<MarkupElement> Children { get; } = new();
        public List<MarkupDiagnostic> Diagnostics { get; } = new();

        public bool HasErrors => Diagnostics.Count > 0;

        /// <summary>Every element of the interface (depth first), the children before the next sibling. The elements inside control templates are not among them (see <see cref="TemplateElements"/>).</summary>
        public IEnumerable<MarkupElement> AllElements() => Walk(Children);

        /// <summary>Every element inside the control templates.</summary>
        public IEnumerable<MarkupElement> TemplateElements() => Walk(Templates.Where(t => t.Root != null).Select(t => t.Root!));

        /// <summary>Every element inside the data templates.</summary>
        public IEnumerable<MarkupElement> DataTemplateElements() => Walk(DataTemplates.Where(t => t.Root != null).Select(t => t.Root!));

        private static IEnumerable<MarkupElement> Walk(IEnumerable<MarkupElement> roots)
        {
            var stack = new Stack<MarkupElement>(roots.Reverse());
            while (stack.Count > 0)
            {
                var e = stack.Pop();
                yield return e;
                foreach (var c in e.Children.AsEnumerable().Reverse()) stack.Push(c);
            }
        }
    }
}
