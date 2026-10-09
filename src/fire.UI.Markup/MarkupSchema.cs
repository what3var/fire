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
        /// <summary>A pen: `#RRGGBB` or `#RRGGBB,3` (colour and width); the script makes a `Pen` of it.</summary>
        Pen,
        /// <summary>A margin or padding: `4`, `4,2` (horizontal, vertical) or `1,2,3,4` (left, top, right, bottom).</summary>
        Thickness,
        /// <summary>One of the names of an enum of the library (`Left`, `Center`, ...), see <see cref="PropertyDef.Choices"/>.</summary>
        Enum,
        /// <summary>The path of an image file: the script embeds the file as a resource (docs/RESOURCES.md) and loads it.</summary>
        Image,
        /// <summary>The key of a `Style` of the `Resources`.</summary>
        StyleRef,
        /// <summary>The key of a `ControlTemplate` of the `Resources`.</summary>
        TemplateRef,
        /// <summary>The key of a `DataTemplate` of the `Resources` (`itemTemplate`).</summary>
        DataTemplateRef,
        /// <summary>The key of a `CollectionView` of the `Resources` (`view`).</summary>
        ViewRef,
        /// <summary>A list (`itemsSource`, `source`): only `{Binding ...}` or `{Expr ...}`, no plain value.</summary>
        Collection,
        /// <summary>Any value that only code can make (a filter or comparer lambda): only `{Expr ...}`.</summary>
        Code,
    }

    /// <summary>A property of an element of the markup: its name in the markup, the field of the fire class it sets and its type.</summary>
    public sealed record PropertyDef(string Name, PropertyKind Kind, string? Field = null)
    {
        public string FieldName => Field ?? Name;
        /// <summary>For <see cref="PropertyKind.Enum"/>: the enum in fire (`UI.HAlign`) and the names it has.</summary>
        public string? EnumType { get; init; }
        public string[]? Choices { get; init; }
        /// <summary>A method of the element that takes the value (`SetRows`), instead of a field that is assigned.</summary>
        public string? Method { get; init; }
        /// <summary>For a part that has no object of its own: the fire code of the value when the attribute is missing.</summary>
        public string? Default { get; init; }
    }

    /// <summary>What an element can contain.</summary>
    public enum ChildMode
    {
        /// <summary>Nothing.</summary>
        None,
        /// <summary>Elements (not parts), added with `AddCall`.</summary>
        Elements,
        /// <summary>Exactly one element (`Border`, `ScrollViewer`).</summary>
        Single,
        /// <summary>Only the parts named in <see cref="ElementDef.PartTags"/> (`<Item>` in a list, `<TreeNode>` in a tree, ...).</summary>
        Parts,
    }

    /// <summary>An event of an element: the field of the fire class (a lambda) and the parameters the lambda gets, which the handler method gets after `sender`.</summary>
    public sealed record EventDef(string Name, string[] Args)
    {
        public EventDef(string name) : this(name, Array.Empty<string>()) { }
    }

    /// <summary>An element of the markup and the class of the library `ui` it stands for.</summary>
    public sealed record ElementDef(string Tag, string Class, ChildMode Children, string Create, IReadOnlyList<PropertyDef> Properties, IReadOnlyList<EventDef> Events)
    {
        /// <summary>How an element is added to its container; `{p}` is the container, `{e}` the element, `{name}` the value of a property of a part that has no object.</summary>
        public string AddCall { get; init; } = "{p}.Add({e})";
        /// <summary>The key of the implicit style of this kind (`StyleKey()` of the class), when it is not the tag.</summary>
        public string? StyleKey { get; init; }
        public IReadOnlyList<string> PartTags { get; init; } = Array.Empty<string>();
        /// <summary>Only inside the elements named here (a part).</summary>
        public IReadOnlyList<string> PartOf { get; init; } = Array.Empty<string>();
        /// <summary>For a part inside different containers: container tag to the add call.</summary>
        public IReadOnlyDictionary<string, string>? AddCalls { get; init; }

        public bool IsPart => PartOf.Count > 0;
        public bool IsContainer => Children == ChildMode.Elements;
        public string StyleTarget => StyleKey ?? Tag;

        public PropertyDef? Property(string name) => Properties.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        public EventDef? Event(string name) => Events.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        public string AddCallIn(string parentTag) => AddCalls != null && AddCalls.TryGetValue(parentTag, out var call) ? call : AddCall;
    }

    /// <summary>What the markup knows: the elements of the library `ui` (docs/UI.md) with their properties and events, and the converters that come with the library.</summary>
    public static class MarkupSchema
    {
        private static PropertyDef Enum(string name, string type, params string[] choices) => new(name, PropertyKind.Enum) { EnumType = type, Choices = choices };

        private static readonly PropertyDef[] Common =
        {
            new("x", PropertyKind.Int), new("y", PropertyKind.Int), new("width", PropertyKind.Int), new("height", PropertyKind.Int),
            new("visible", PropertyKind.Bool), new("enabled", PropertyKind.Bool),
            new("margin", PropertyKind.Thickness),
            Enum("halign", "UI.HAlign", "Stretch", "Left", "Center", "Right"),
            Enum("valign", "UI.VAlign", "Stretch", "Top", "Center", "Bottom"),
            new("minWidth", PropertyKind.Int), new("minHeight", PropertyKind.Int), new("maxWidth", PropertyKind.Int), new("maxHeight", PropertyKind.Int),
            new("background", PropertyKind.Color), new("foreground", PropertyKind.Color), new("pen", PropertyKind.Pen),
            new("style", PropertyKind.StyleRef), new("template", PropertyKind.TemplateRef),
            // attached properties of the containers
            new("Grid.Row", PropertyKind.Int, "gridRow"), new("Grid.Column", PropertyKind.Int, "gridColumn"),
            new("Grid.RowSpan", PropertyKind.Int, "gridRowSpan"), new("Grid.ColumnSpan", PropertyKind.Int, "gridColumnSpan"),
            new PropertyDef("DockPanel.Dock", PropertyKind.Enum, "dock") { EnumType = "UI.Dock", Choices = new[] { "Left", "Top", "Right", "Bottom" } },
        };

        /// <summary>The properties that a trigger can test besides the ones of an element: the state the library tracks.</summary>
        public static readonly IReadOnlyList<PropertyDef> StateProperties = new PropertyDef[]
        {
            new("hover", PropertyKind.Bool), new("pressed", PropertyKind.Bool), new("focused", PropertyKind.Bool),
        };

        private static ElementDef Define(string tag, ChildMode children, string create, PropertyDef[] own, params EventDef[] events) =>
            new(tag, "UI." + tag, children, create, Common.Concat(own).ToArray(), events);

        private static ElementDef Define(string tag, bool container, string create, PropertyDef[] own, params string[] events) =>
            Define(tag, container ? ChildMode.Elements : ChildMode.None, create, own, events.Select(e => new EventDef(e)).ToArray());

        private static readonly PropertyDef[] StackProperties =
        {
            new("orientation", PropertyKind.Orientation, "horizontal"), new("horizontal", PropertyKind.Bool),
            new("spacing", PropertyKind.Int), new("padding", PropertyKind.Int),
        };

        private static readonly PropertyDef[] PanelLook = { new("showBorder", PropertyKind.Bool), new("filled", PropertyKind.Bool) };

        private static readonly string[] ItemParts = { "Item" };

        private static readonly PropertyDef[] ListSources =
        {
            new("itemsSource", PropertyKind.Collection), new("itemTemplate", PropertyKind.DataTemplateRef),
            new("view", PropertyKind.ViewRef) { Method = "SetView" },
        };

        /// <summary>The attributes of a `&lt;CollectionView&gt;` of the Resources (it is no element of the interface: it has no common properties). `sortBy` and `descending` are set together by `SortBy`.</summary>
        public static readonly ElementDef CollectionViewDef = new("CollectionView", "UI.CollectionView", ChildMode.None, "new UI.CollectionView(undefined)", new PropertyDef[]
        {
            new("source", PropertyKind.Collection), new("sortBy", PropertyKind.Text), new("descending", PropertyKind.Bool),
            new("filter", PropertyKind.Code), new("comparer", PropertyKind.Code),
        }, Array.Empty<EventDef>());

        public static readonly IReadOnlyList<ElementDef> Elements = new[]
        {
            Define("Panel", true, "new UI.Panel(0, 0, 100, 100)", PanelLook),
            Define("Stack", true, "new UI.Stack(0, 0, 100, 100)", StackProperties.Concat(PanelLook).ToArray()) with { StyleKey = "StackPanel" },
            Define("StackPanel", true, "new UI.StackPanel(0, 0, -1, -1)", StackProperties.Concat(PanelLook).ToArray()),
            Define("Canvas", true, "new UI.Canvas(0, 0, -1, -1)", PanelLook),
            Define("DockPanel", true, "new UI.DockPanel(0, 0, -1, -1)", PanelLook.Append(new("lastChildFill", PropertyKind.Bool)).ToArray()),
            Define("WrapPanel", true, "new UI.WrapPanel(0, 0, -1, -1)", PanelLook.Concat(new PropertyDef[] { new("vertical", PropertyKind.Bool), new("itemWidth", PropertyKind.Int), new("itemHeight", PropertyKind.Int) }).ToArray()),
            Define("Grid", true, "new UI.Grid(0, 0, -1, -1)", PanelLook.Concat(new PropertyDef[]
            {
                new("rows", PropertyKind.Text) { Method = "SetRows" }, new("columns", PropertyKind.Text) { Method = "SetColumns" },
            }).ToArray()),
            Define("Border", ChildMode.Single, "new UI.Border(0, 0, -1, -1)", new PropertyDef[]
            {
                new("padding", PropertyKind.Thickness), new("thickness", PropertyKind.Int),
            }) with { AddCall = "{p}.SetChild({e})" },
            Define("ScrollViewer", ChildMode.Single, "new UI.ScrollViewer(0, 0, -1, -1)", new PropertyDef[]
            {
                Enum("vmode", "UI.ScrollMode", "Disabled", "Auto", "Visible"), Enum("hmode", "UI.ScrollMode", "Disabled", "Auto", "Visible"),
            }) with { AddCall = "{p}.SetContent({e})" },
            Define("ToolBar", true, "new UI.ToolBar(0, 0, -1, -1)", PanelLook),
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
            Define("AutoSuggestBox", ChildMode.Parts, "new UI.AutoSuggestBox(\"\", 0, 0, 160, 24)", new PropertyDef[]
            {
                new("text", PropertyKind.Text), new("maxLength", PropertyKind.Int), new("maxSuggestions", PropertyKind.Int), new("displayMember", PropertyKind.Text),
            }, new[] { new EventDef("onChange"), new EventDef("onEnter"), new EventDef("onChosen") }) with { PartTags = new[] { "Suggestion" } },
            Define("ListBox", ChildMode.Parts, "new UI.ListBox(0, 0, 160, 120)", new PropertyDef[]
            {
                new("displayMember", PropertyKind.Text), new("selectedIndex", PropertyKind.Int),
            }.Concat(ListSources).ToArray(), new[] { new EventDef("onSelect"), new EventDef("onActivate") }) with { PartTags = ItemParts },
            Define("ListView", ChildMode.Parts, "new UI.ListView(0, 0, 240, 140)", new PropertyDef[]
            {
                new("displayMember", PropertyKind.Text), new("selectedIndex", PropertyKind.Int),
            }.Concat(ListSources).ToArray(), new[] { new EventDef("onSelect"), new EventDef("onActivate") }) with { PartTags = new[] { "Column", "Item" } },
            Define("TreeView", ChildMode.Parts, "new UI.TreeView(0, 0, 200, 160)", new PropertyDef[]
            {
                new("indent", PropertyKind.Int),
            }, new[] { new EventDef("onSelect") }) with { PartTags = new[] { "TreeNode" } },
            Define("RadioButtons", ChildMode.Parts, "new UI.RadioButtons(0, 0)", new PropertyDef[]
            {
                new("header", PropertyKind.Text), new("horizontal", PropertyKind.Bool), new("spacing", PropertyKind.Int), new("selectedIndex", PropertyKind.Int),
            }, new[] { new EventDef("onChange") }) with { PartTags = ItemParts },
            Define("MenuBar", ChildMode.Parts, "new UI.MenuBar(0, 0, -1, -1)", Array.Empty<PropertyDef>()) with { PartTags = new[] { "Menu" } },
            Define("Separator", false, "new UI.Separator()", Array.Empty<PropertyDef>()),
            Define("Image", false, "new UI.Image(undefined, 0, 0, -1, -1)", new PropertyDef[]
            {
                new("source", PropertyKind.Image), Enum("stretch", "UI.Stretch", "None", "Fill", "Uniform", "UniformToFill"),
            }),
            Define("Rectangle", false, "new UI.Rectangle(0, 0, -1, -1)", new PropertyDef[] { new("fill", PropertyKind.Color), new("stroke", PropertyKind.Pen) }),
            Define("Ellipse", false, "new UI.Ellipse(0, 0, -1, -1)", new PropertyDef[] { new("fill", PropertyKind.Color), new("stroke", PropertyKind.Pen) }),
            Define("Line", false, "new UI.Line(0, 0, 0, 0)", new PropertyDef[]
            {
                new("x1", PropertyKind.Int), new("y1", PropertyKind.Int), new("x2", PropertyKind.Int), new("y2", PropertyKind.Int), new("stroke", PropertyKind.Pen),
            }),
            Define("Path", false, "new UI.Path(undefined, 0, 0)", new PropertyDef[]
            {
                new("data", PropertyKind.Text) { Method = "SetData" }, new("fill", PropertyKind.Color), new("stroke", PropertyKind.Pen),
            }),
            Define("DrawingCanvas", ChildMode.None, "new UI.DrawingCanvas(0, 0, 200, 150)", new PropertyDef[] { new("continuous", PropertyKind.Bool) },
                new[] { new EventDef("onPaint", new[] { "canvas" }), new EventDef("onMouseDown", new[] { "x", "y", "button" }), new EventDef("onMouseMove", new[] { "x", "y" }), new EventDef("onMouseUp", new[] { "x", "y", "button" }) }),

            // ---- parts: what the containers above contain (no common properties, no style) ----
            new ElementDef("Item", "", ChildMode.None, "", new PropertyDef[] { new("text", PropertyKind.Text) { Default = "\"\"" } }, Array.Empty<EventDef>())
            {
                PartOf = new[] { "ListBox", "ListView", "RadioButtons" }, AddCall = "{p}.Add({text})",
            },
            new ElementDef("Suggestion", "", ChildMode.None, "", new PropertyDef[] { new("text", PropertyKind.Text) { Default = "\"\"" } }, Array.Empty<EventDef>())
            {
                PartOf = new[] { "AutoSuggestBox" }, AddCall = "{p}.AddSuggestion({text})",
            },
            new ElementDef("Column", "", ChildMode.None, "", new PropertyDef[]
            {
                new("header", PropertyKind.Text) { Default = "\"\"" }, new("member", PropertyKind.Text) { Default = "\"\"" }, new("width", PropertyKind.Int) { Default = "100" },
            }, Array.Empty<EventDef>())
            {
                PartOf = new[] { "ListView" }, AddCall = "{p}.AddColumn({header}, {member}, {width})",
            },
            new ElementDef("TreeNode", "UI.TreeNode", ChildMode.Parts, "new UI.TreeNode(\"\")", new PropertyDef[]
            {
                new("text", PropertyKind.Text), new("expanded", PropertyKind.Bool),
            }, Array.Empty<EventDef>())
            {
                PartOf = new[] { "TreeView", "TreeNode" }, PartTags = new[] { "TreeNode" },
                AddCalls = new Dictionary<string, string> { ["TreeView"] = "{p}.AddNode({e})", ["TreeNode"] = "{p}.Add({e})" },
            },
            new ElementDef("Menu", "UI.MenuItem", ChildMode.Parts, "new UI.MenuItem(\"\")", new PropertyDef[]
            {
                new("header", PropertyKind.Text), new("enabled", PropertyKind.Bool),
            }, Array.Empty<EventDef>())
            {
                PartOf = new[] { "MenuBar" }, PartTags = new[] { "MenuItem", "MenuSeparator" },
            },
            new ElementDef("MenuItem", "UI.MenuItem", ChildMode.Parts, "new UI.MenuItem(\"\")", new PropertyDef[]
            {
                new("header", PropertyKind.Text), new("shortcut", PropertyKind.Text), new("checkable", PropertyKind.Bool), new("isChecked", PropertyKind.Bool), new("enabled", PropertyKind.Bool),
            }, new[] { new EventDef("onClick") })
            {
                PartOf = new[] { "Menu", "MenuItem" }, PartTags = new[] { "MenuItem", "MenuSeparator" },
            },
            new ElementDef("MenuSeparator", "", ChildMode.None, "", Array.Empty<PropertyDef>(), Array.Empty<EventDef>())
            {
                PartOf = new[] { "Menu", "MenuItem" }, AddCall = "{p}.Add(UI.MenuItem.Separator())",
            },
        };

        public static ElementDef? Find(string tag) =>
            Elements.FirstOrDefault(e => string.Equals(e.Tag, tag, StringComparison.OrdinalIgnoreCase));

        /// <summary>The element a style or template can name as its target: a tag of the schema (not a part), or `Element` (the properties all elements share).</summary>
        public static ElementDef? FindTarget(string tag) =>
            string.Equals(tag, "Element", StringComparison.OrdinalIgnoreCase)
                ? new ElementDef("Element", "UI.Element", ChildMode.None, "", Common, Array.Empty<EventDef>()) { StyleKey = "Element" }
                : Find(tag) is { IsPart: false } def ? def : null;

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
            "framebuffer", "window", "ui", "view", "dataContext", "SetDataContext", "Run", "OnTick", "Attach", "Open",
        };

        private static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);
        private static readonly Regex DottedName = new(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$", RegexOptions.Compiled);

        public static bool IsIdentifier(string text) => Identifier.IsMatch(text);

        /// <summary>A name that may be dotted (`UI.NotConverter`, `Colors.Red`).</summary>
        public static bool IsDottedName(string text) => DottedName.IsMatch(text);
    }
}
