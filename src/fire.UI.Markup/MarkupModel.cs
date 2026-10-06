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

    /// <summary>`{Enum Type.Member}`, `{Static Type.Member}` or `{Expr ...}`: a piece of fire code that is used as it is (an enum member, a constant, any expression).</summary>
    public sealed record ExpressionValue(string Code) : MarkupValue;

    /// <summary>`{Binding Path, Mode=TwoWay, Converter=Key, ElementName=name}`: the value of a property of the data context (or of a named element) follows the property.</summary>
    public sealed record BindingValue(string Path, BindingMode Mode, string? Converter, string? ElementName) : MarkupValue;

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
        public List<MarkupElement> Children { get; } = new();
        public List<MarkupDiagnostic> Diagnostics { get; } = new();

        public bool HasErrors => Diagnostics.Count > 0;

        /// <summary>Every element (depth first), the children before the next sibling.</summary>
        public IEnumerable<MarkupElement> AllElements()
        {
            var stack = new Stack<MarkupElement>(Children.AsEnumerable().Reverse());
            while (stack.Count > 0)
            {
                var e = stack.Pop();
                yield return e;
                foreach (var c in e.Children.AsEnumerable().Reverse()) stack.Push(c);
            }
        }
    }
}
