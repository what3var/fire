using System.Text.RegularExpressions;

namespace fire.UI.Markup
{
    /// <summary>The type of a property, which decides how its value in the markup is read.</summary>
    public enum PropertyKind
    {
        /// <summary>A whole number (`12`, `-3`, `0x1F`) or an enum member / constant (`{Enum Colors.Red}`).</summary>
        Int,
        /// <summary>`true` or `false`.</summary>
        Bool,
        /// <summary>A text.</summary>
        Text,
        /// <summary>A colour: `#RRGGBB`, a whole number (the raw colour value, see `UI.Color`) or `{Enum ...}` - the field of the element is a brush, the script makes a `SolidBrush` of it.</summary>
        Color,
        /// <summary>`Horizontal` or `Vertical` (stored in the bool field `horizontal`).</summary>
        Orientation,
    }

    /// <summary>A property of an element of the markup: its name in the markup, the field of the fire class it sets and its type.</summary>
    public sealed record PropertyDef(string Name, PropertyKind Kind, string? Field = null)
    {
        public string FieldName => Field ?? Name;
    }

    /// <summary>An element of the markup and the class of the library `ui` it stands for.</summary>
    public sealed record ElementDef(string Tag, string Class, bool IsContainer, string Create, IReadOnlyList<PropertyDef> Properties, IReadOnlyList<string> Events)
    {
        public PropertyDef? Property(string name) => Properties.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        public string? Event(string name) => Events.FirstOrDefault(e => string.Equals(e, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>What the markup knows: the elements of the library `ui` (docs/UI.md) with their properties and events, and the converters that come with the library.</summary>
    public static class MarkupSchema
    {
        private static readonly PropertyDef[] Common =
        {
            new("x", PropertyKind.Int), new("y", PropertyKind.Int), new("width", PropertyKind.Int), new("height", PropertyKind.Int),
            new("visible", PropertyKind.Bool), new("enabled", PropertyKind.Bool),
        };

        private static ElementDef Define(string tag, bool container, string create, PropertyDef[] own, params string[] events) =>
            new(tag, "UI." + tag, container, create, Common.Concat(own).ToArray(), events);

        public static readonly IReadOnlyList<ElementDef> Elements = new[]
        {
            Define("Panel", true, "new UI.Panel(0, 0, 100, 100)", new PropertyDef[]
            {
                new("showBorder", PropertyKind.Bool), new("background", PropertyKind.Color), new("filled", PropertyKind.Bool),
            }),
            Define("Stack", true, "new UI.Stack(0, 0, 100, 100)", new PropertyDef[]
            {
                new("orientation", PropertyKind.Orientation, "horizontal"), new("horizontal", PropertyKind.Bool),
                new("spacing", PropertyKind.Int), new("padding", PropertyKind.Int),
                new("showBorder", PropertyKind.Bool), new("background", PropertyKind.Color), new("filled", PropertyKind.Bool),
            }),
            Define("Label", false, "new UI.Label(\"\", 0, 0)", new PropertyDef[]
            {
                new("text", PropertyKind.Text), new("color", PropertyKind.Color, "brush"),
            }),
            Define("Button", false, "new UI.Button(\"\", 0, 0)", new PropertyDef[]
            {
                new("text", PropertyKind.Text),
            }, "onClick"),
            Define("CheckBox", false, "new UI.CheckBox(\"\", 0, 0)", new PropertyDef[]
            {
                new("text", PropertyKind.Text), new("isChecked", PropertyKind.Bool),
            }, "onChange"),
            Define("TextBox", false, "new UI.TextBox(\"\", 0, 0)", new PropertyDef[]
            {
                new("text", PropertyKind.Text), new("maxLength", PropertyKind.Int),
            }, "onChange", "onEnter"),
        };

        public static ElementDef? Find(string tag) =>
            Elements.FirstOrDefault(e => string.Equals(e.Tag, tag, StringComparison.OrdinalIgnoreCase));

        /// <summary>The converters of the library `ui` that a binding can name without declaring them: key to class.</summary>
        public static readonly IReadOnlyDictionary<string, string> BuiltInConverters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Not"] = "UI.NotConverter",
            ["IsEmpty"] = "UI.IsEmptyConverter",
            ["NotEmpty"] = "UI.NotEmptyConverter",
            ["Text"] = "UI.TextConverter",
        };

        /// <summary>The names the generated class uses itself; a named element must not take them.</summary>
        public static readonly IReadOnlySet<string> ReservedNames = new HashSet<string>
        {
            "framebuffer", "window", "ui", "view", "dataContext", "SetDataContext", "Run", "OnTick", "Attach",
        };

        private static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);
        private static readonly Regex DottedName = new(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$", RegexOptions.Compiled);

        public static bool IsIdentifier(string text) => Identifier.IsMatch(text);

        /// <summary>A name that may be dotted (`UI.NotConverter`, `Colors.Red`).</summary>
        public static bool IsDottedName(string text) => DottedName.IsMatch(text);
    }
}
