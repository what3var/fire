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
        /// <summary>A vector symbol: the name of one of `UI.Icons` (`Up`, `Home`, ...) or path data (`M 8 1 L 15 8 L 1 8 Z`, see UI.Geometry.Parse).</summary>
        Geometry,
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

    /// <summary>A property of an element of the markup: its name in the markup (PascalCase: `Width`, `HorizontalAlign`), the field of the fire class it sets
    /// (the same name, unless given: `Grid.Row` sets `GridRow`) and its type.</summary>
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
        /// <summary>The field of the fire class (a lambda): the same name (`OnClick`).</summary>
        public string FieldName => Name;
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
            new("Left", PropertyKind.Int), new("Top", PropertyKind.Int), new("Width", PropertyKind.Int), new("Height", PropertyKind.Int),
            new("Visible", PropertyKind.Bool), new("Enabled", PropertyKind.Bool),
            new("Margin", PropertyKind.Thickness),
            Enum("HorizontalAlign", "UI.HAlign", "Stretch", "Left", "Center", "Right"),
            Enum("VerticalAlign", "UI.VAlign", "Stretch", "Top", "Center", "Bottom"),
            new("MinWidth", PropertyKind.Int), new("MinHeight", PropertyKind.Int), new("MaxWidth", PropertyKind.Int), new("MaxHeight", PropertyKind.Int),
            new("Background", PropertyKind.Color), new("Foreground", PropertyKind.Color), new("Pen", PropertyKind.Pen),
            // the font of the text: a name for Fonts.Get ("" = that of the surrounding element) and the height of the letters in pixels (0 = that of the surrounding element)
            new("Font", PropertyKind.Text), new("FontSize", PropertyKind.Int),
            new("Style", PropertyKind.StyleRef), new("Template", PropertyKind.TemplateRef),
            // attached properties of the containers
            new("Grid.Row", PropertyKind.Int, "GridRow"), new("Grid.Column", PropertyKind.Int, "GridColumn"),
            new("Grid.RowSpan", PropertyKind.Int, "GridRowSpan"), new("Grid.ColumnSpan", PropertyKind.Int, "GridColumnSpan"),
            new PropertyDef("DockPanel.Dock", PropertyKind.Enum, "Dock") { EnumType = "UI.Dock", Choices = new[] { "Left", "Top", "Right", "Bottom" } },
        };

        /// <summary>The properties that a trigger can test besides the ones of an element: the state the library tracks.</summary>
        public static readonly IReadOnlyList<PropertyDef> StateProperties = new PropertyDef[]
        {
            new("Hover", PropertyKind.Bool), new("Pressed", PropertyKind.Bool), new("Focused", PropertyKind.Bool),
        };

        private static ElementDef Define(string tag, ChildMode children, string create, PropertyDef[] own, params EventDef[] events) =>
            new(tag, "UI." + tag, children, create, Common.Concat(own).ToArray(), events);

        private static ElementDef Define(string tag, bool container, string create, PropertyDef[] own, params string[] events) =>
            Define(tag, container ? ChildMode.Elements : ChildMode.None, create, own, events.Select(e => new EventDef(e)).ToArray());

        private static readonly PropertyDef[] StackProperties =
        {
            new("Orientation", PropertyKind.Orientation, "Horizontal"), new("Horizontal", PropertyKind.Bool),
            new("Spacing", PropertyKind.Int), new("Padding", PropertyKind.Int),
        };

        private static readonly PropertyDef[] PanelLook = { new("ShowBorder", PropertyKind.Bool), new("Filled", PropertyKind.Bool) };

        private static readonly string[] ItemParts = { "Item" };

        private static readonly PropertyDef[] ListSources =
        {
            new("ItemsSource", PropertyKind.Collection), new("ItemTemplate", PropertyKind.DataTemplateRef),
            new("View", PropertyKind.ViewRef) { Method = "SetView" },
        };

        /// <summary>The attributes of a `&lt;CollectionView&gt;` of the Resources (it is no element of the interface: it has no common properties). `sortBy` and `descending` are set together by `SortBy`.</summary>
        public static readonly ElementDef CollectionViewDef = new("CollectionView", "UI.CollectionView", ChildMode.None, "new UI.CollectionView(undefined)", new PropertyDef[]
        {
            new("Source", PropertyKind.Collection), new("SortBy", PropertyKind.Text, "sortBy"), new("Descending", PropertyKind.Bool),
            new("Filter", PropertyKind.Code), new("Comparer", PropertyKind.Code),
        }, Array.Empty<EventDef>());

        public static readonly IReadOnlyList<ElementDef> Elements = new[]
        {
            Define("Panel", true, "new UI.Panel(0, 0, 100, 100)", PanelLook),
            Define("Stack", true, "new UI.Stack(0, 0, 100, 100)", StackProperties.Concat(PanelLook).ToArray()) with { StyleKey = "StackPanel" },
            Define("StackPanel", true, "new UI.StackPanel(0, 0, -1, -1)", StackProperties.Concat(PanelLook).ToArray()),
            Define("Canvas", true, "new UI.Canvas(0, 0, -1, -1)", PanelLook),
            Define("DockPanel", true, "new UI.DockPanel(0, 0, -1, -1)", PanelLook.Append(new("LastChildFill", PropertyKind.Bool)).ToArray()),
            Define("WrapPanel", true, "new UI.WrapPanel(0, 0, -1, -1)", PanelLook.Concat(new PropertyDef[] { new("Vertical", PropertyKind.Bool), new("ItemWidth", PropertyKind.Int), new("ItemHeight", PropertyKind.Int) }).ToArray()),
            Define("Grid", true, "new UI.Grid(0, 0, -1, -1)", PanelLook.Concat(new PropertyDef[]
            {
                new("Rows", PropertyKind.Text) { Method = "SetRows" }, new("Columns", PropertyKind.Text) { Method = "SetColumns" },
            }).ToArray()),
            Define("Border", ChildMode.Single, "new UI.Border(0, 0, -1, -1)", new PropertyDef[]
            {
                new("Padding", PropertyKind.Thickness), new("Thickness", PropertyKind.Int),
            }) with { AddCall = "{p}.SetChild({e})" },
            Define("ScrollViewer", ChildMode.Single, "new UI.ScrollViewer(0, 0, -1, -1)", new PropertyDef[]
            {
                Enum("VerticalScrollMode", "UI.ScrollMode", "Disabled", "Auto", "Visible"), Enum("HorizontalScrollMode", "UI.ScrollMode", "Disabled", "Auto", "Visible"),
            }) with { AddCall = "{p}.SetContent({e})" },
            Define("ToolBar", true, "new UI.ToolBar(0, 0, -1, -1)", PanelLook),
            Define("Label", false, "new UI.Label(\"\", 0, 0)", new PropertyDef[]
            {
                new("Text", PropertyKind.Text), new("Color", PropertyKind.Color),
            }),
            Define("Button", false, "new UI.Button(\"\", 0, 0)", new PropertyDef[]
            {
                new("Text", PropertyKind.Text), new("Icon", PropertyKind.Geometry), new("Image", PropertyKind.Image), new("IconSize", PropertyKind.Int),
            }, "OnClick"),
            Define("CheckBox", false, "new UI.CheckBox(\"\", 0, 0)", new PropertyDef[]
            {
                new("Text", PropertyKind.Text), new("IsChecked", PropertyKind.Bool),
            }, "OnChange"),
            Define("TextBox", false, "new UI.TextBox(\"\", 0, 0)", new PropertyDef[]
            {
                new("Text", PropertyKind.Text), new("MaxLength", PropertyKind.Int),
            }, "OnChange", "OnEnter"),
            Define("AutoSuggestBox", ChildMode.Parts, "new UI.AutoSuggestBox(\"\", 0, 0, 160, 24)", new PropertyDef[]
            {
                new("Text", PropertyKind.Text), new("MaxLength", PropertyKind.Int), new("MaxSuggestions", PropertyKind.Int), new("DisplayMember", PropertyKind.Text),
            }, new[] { new EventDef("OnChange"), new EventDef("OnEnter"), new EventDef("OnChosen") }) with { PartTags = new[] { "Suggestion" } },
            Define("ListBox", ChildMode.Parts, "new UI.ListBox(0, 0, 160, 120)", new PropertyDef[]
            {
                new("DisplayMember", PropertyKind.Text), new("SelectedIndex", PropertyKind.Int),
            }.Concat(ListSources).ToArray(), new[] { new EventDef("OnSelect"), new EventDef("OnActivate") }) with { PartTags = ItemParts },
            Define("ComboBox", ChildMode.Parts, "new UI.ComboBox(0, 0, 140, 24)", new PropertyDef[]
            {
                new("DisplayMember", PropertyKind.Text), new("SelectedIndex", PropertyKind.Int), new("MaxDropDownItems", PropertyKind.Int),
                new("ItemsSource", PropertyKind.Collection),
            }, new[] { new EventDef("OnSelect") }) with { PartTags = ItemParts },
            Define("ListView", ChildMode.Parts, "new UI.ListView(0, 0, 240, 140)", new PropertyDef[]
            {
                new("DisplayMember", PropertyKind.Text), new("SelectedIndex", PropertyKind.Int),
            }.Concat(ListSources).ToArray(), new[] { new EventDef("OnSelect"), new EventDef("OnActivate") }) with { PartTags = new[] { "Column", "Item" } },
            Define("TreeView", ChildMode.Parts, "new UI.TreeView(0, 0, 200, 160)", new PropertyDef[]
            {
                new("Indent", PropertyKind.Int),
            }, new[] { new EventDef("OnSelect") }) with { PartTags = new[] { "TreeNode" } },
            Define("RadioButtons", ChildMode.Parts, "new UI.RadioButtons(0, 0)", new PropertyDef[]
            {
                new("Header", PropertyKind.Text), new("Horizontal", PropertyKind.Bool), new("Spacing", PropertyKind.Int), new("SelectedIndex", PropertyKind.Int),
            }, new[] { new EventDef("OnChange") }) with { PartTags = ItemParts },
            Define("MenuBar", ChildMode.Parts, "new UI.MenuBar(0, 0, -1, -1)", Array.Empty<PropertyDef>()) with { PartTags = new[] { "Menu" } },
            Define("Separator", false, "new UI.Separator()", Array.Empty<PropertyDef>()),
            Define("Image", false, "new UI.Image(undefined, 0, 0, -1, -1)", new PropertyDef[]
            {
                new("Source", PropertyKind.Image), Enum("Stretch", "UI.Stretch", "None", "Fill", "Uniform", "UniformToFill"),
            }),
            Define("Rectangle", false, "new UI.Rectangle(0, 0, -1, -1)", new PropertyDef[] { new("Fill", PropertyKind.Color), new("Stroke", PropertyKind.Pen) }),
            Define("Ellipse", false, "new UI.Ellipse(0, 0, -1, -1)", new PropertyDef[] { new("Fill", PropertyKind.Color), new("Stroke", PropertyKind.Pen) }),
            Define("Line", false, "new UI.Line(0, 0, 0, 0)", new PropertyDef[]
            {
                new("X1", PropertyKind.Int), new("Y1", PropertyKind.Int), new("X2", PropertyKind.Int), new("Y2", PropertyKind.Int), new("Stroke", PropertyKind.Pen),
            }),
            Define("Path", false, "new UI.Path(undefined, 0, 0)", new PropertyDef[]
            {
                new("Data", PropertyKind.Text) { Method = "SetData" }, new("Fill", PropertyKind.Color), new("Stroke", PropertyKind.Pen),
            }),
            Define("DrawingCanvas", ChildMode.None, "new UI.DrawingCanvas(0, 0, 200, 150)", new PropertyDef[] { new("Continuous", PropertyKind.Bool) },
                new[] { new EventDef("OnPaint", new[] { "canvas" }), new EventDef("OnMouseDown", new[] { "x", "y", "button" }), new EventDef("OnMouseMove", new[] { "x", "y" }), new EventDef("OnMouseUp", new[] { "x", "y", "button" }) }),

            // ---- parts: what the containers above contain (no common properties, no style) ----
            new ElementDef("Item", "", ChildMode.None, "", new PropertyDef[] { new("Text", PropertyKind.Text) { Default = "\"\"" } }, Array.Empty<EventDef>())
            {
                PartOf = new[] { "ListBox", "ListView", "RadioButtons", "ComboBox" }, AddCall = "{p}.Add({Text})",
            },
            new ElementDef("Suggestion", "", ChildMode.None, "", new PropertyDef[] { new("Text", PropertyKind.Text) { Default = "\"\"" } }, Array.Empty<EventDef>())
            {
                PartOf = new[] { "AutoSuggestBox" }, AddCall = "{p}.AddSuggestion({Text})",
            },
            new ElementDef("Column", "", ChildMode.None, "", new PropertyDef[]
            {
                new("Header", PropertyKind.Text) { Default = "\"\"" }, new("Member", PropertyKind.Text) { Default = "\"\"" }, new("Width", PropertyKind.Int) { Default = "100" },
            }, Array.Empty<EventDef>())
            {
                PartOf = new[] { "ListView" }, AddCall = "{p}.AddColumn({Header}, {Member}, {Width})",
            },
            new ElementDef("TreeNode", "UI.TreeNode", ChildMode.Parts, "new UI.TreeNode(\"\")", new PropertyDef[]
            {
                new("Text", PropertyKind.Text), new("Expanded", PropertyKind.Bool),
            }, Array.Empty<EventDef>())
            {
                PartOf = new[] { "TreeView", "TreeNode" }, PartTags = new[] { "TreeNode" },
                AddCalls = new Dictionary<string, string> { ["TreeView"] = "{p}.AddNode({e})", ["TreeNode"] = "{p}.Add({e})" },
            },
            new ElementDef("Menu", "UI.MenuItem", ChildMode.Parts, "new UI.MenuItem(\"\")", new PropertyDef[]
            {
                new("Header", PropertyKind.Text), new("Enabled", PropertyKind.Bool),
            }, Array.Empty<EventDef>())
            {
                PartOf = new[] { "MenuBar" }, PartTags = new[] { "MenuItem", "MenuSeparator" },
            },
            new ElementDef("MenuItem", "UI.MenuItem", ChildMode.Parts, "new UI.MenuItem(\"\")", new PropertyDef[]
            {
                new("Header", PropertyKind.Text), new("Shortcut", PropertyKind.Text), new("Checkable", PropertyKind.Bool), new("IsChecked", PropertyKind.Bool), new("Enabled", PropertyKind.Bool),
            }, new[] { new EventDef("OnClick") })
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
        /// <summary>The ready-made symbols of `UI.Icons` (Button.icon).</summary>
        public static readonly string[] IconNames = { "Up", "Down", "Back", "Forward", "Home", "Folder", "File", "Close", "Check", "Add" };

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
            "framebuffer", "window", "ui", "view", "dataContext", "SetDataContext", "Run", "Show", "OnTick", "Attach", "Open",
        };

        private static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);
        private static readonly Regex DottedName = new(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$", RegexOptions.Compiled);

        public static bool IsIdentifier(string text) => Identifier.IsMatch(text);

        /// <summary>The names the markup had before its attributes were written in PascalCase and got clearer names: old name to new.</summary>
        public static readonly IReadOnlyDictionary<string, string> RenamedAttributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["x"] = "Left", ["y"] = "Top", ["halign"] = "HorizontalAlign", ["valign"] = "VerticalAlign", ["vmode"] = "VerticalScrollMode", ["hmode"] = "HorizontalScrollMode",
        };

        /// <summary>A state property a trigger can test (`Hover`, `Pressed`, `Focused`; any case).</summary>
        public static PropertyDef? StateProperty(string? name) => StateProperties.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>A name that may be dotted (`UI.NotConverter`, `Colors.Red`).</summary>
        public static bool IsDottedName(string text) => DottedName.IsMatch(text);
    }
}
