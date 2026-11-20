/* native/forge/markupschema.c - port of src/fire.UI.Markup/MarkupSchema.cs (commit 7a594769): PropertyKind, PropertyDef, ChildMode, EventDef,
 * ElementDef and MarkupSchema (the elements of the library `ui`, the converters, the UIRoot.* properties, the reserved names, the patterns).
 * The definitions are constant tables; the elements with their properties (the common ones first) are made once per context (FcCtx.markupSchema). */

typedef enum FmPropKind {
    PK_Int, PK_Number, PK_Bool, PK_Text, PK_Color, PK_Orientation, PK_Pen, PK_Thickness, PK_Enum, PK_Image, PK_Geometry,
    PK_StyleRef, PK_TemplateRef, PK_DataTemplateRef, PK_ViewRef, PK_Collection, PK_Code
} FmPropKind;

/* PropertyDef (Field: NULL for the name itself; Choices: NULL-terminated) */
typedef struct FmPropDef {
    const char* name;
    FmPropKind kind;
    const char* field;
    const char* enumType;
    const char* const* choices;
    const char* method;
    const char* def;
} FmPropDef;
static const char* fmFieldName(const FmPropDef* p) { return p->field ? p->field : p->name; }

typedef enum FmChildMode { CM_None, CM_Elements, CM_Single, CM_Parts } FmChildMode;

/* EventDef (Args and ArgTypes NULL-terminated; ArgTypes NULL: `any` for all) */
typedef struct FmEventDef { const char* name; const char* const* args; const char* const* argTypes; } FmEventDef;
static const char* fmArgType(const FmEventDef* e, int index) {
    int n = 0;
    if (!e->argTypes) return "any";
    while (e->argTypes[n]) n++;
    return index < n ? e->argTypes[index] : "any";
}
static int fmArgCount(const FmEventDef* e) { int n = 0; while (e->args && e->args[n]) n++; return n; }

/* ElementDef */
typedef struct FmElementDef {
    const char* tag;
    const char* cls;
    FmChildMode children;
    const char* create;
    FcList* properties;        /* const FmPropDef* */
    FcList* events;            /* const FmEventDef* */
    const char* addCall;
    const char* styleKey;
    const char* const* partTags;
    const char* const* partOf;
    const char* const* addCalls;   /* pairs container tag, call; NULL-terminated */
} FmElementDef;

static int fmCount(const char* const* l) { int n = 0; while (l && l[n]) n++; return n; }
static bool fmIsPart(const FmElementDef* d) { return fmCount(d->partOf) > 0; }
static const char* fmStyleTarget(const FmElementDef* d) { return d->styleKey ? d->styleKey : d->tag; }
static const FmPropDef* fmProperty(const FmElementDef* d, const char* name) {
    int i;
    for (i = 0; i < d->properties->count; i++) if (fmIEq(((const FmPropDef*)d->properties->items[i])->name, name)) return (const FmPropDef*)d->properties->items[i];
    return NULL;
}
static const FmEventDef* fmEvent(const FmElementDef* d, const char* name) {
    int i;
    for (i = 0; i < d->events->count; i++) if (fmIEq(((const FmEventDef*)d->events->items[i])->name, name)) return (const FmEventDef*)d->events->items[i];
    return NULL;
}
static const char* fmAddCallIn(const FmElementDef* d, const char* parentTag) {
    int i;
    if (d->addCalls) for (i = 0; d->addCalls[i]; i += 2) if (strcmp(d->addCalls[i], parentTag) == 0) return d->addCalls[i + 1];
    return d->addCall;
}

/* ---- MarkupSchema ---- */

static const char* const fmChoicesHAlign[] = { "Stretch", "Left", "Center", "Right", NULL };
static const char* const fmChoicesVAlign[] = { "Stretch", "Top", "Center", "Bottom", NULL };
static const char* const fmChoicesDock[] = { "Left", "Top", "Right", "Bottom", NULL };
static const char* const fmChoicesScroll[] = { "Disabled", "Auto", "Visible", NULL };
static const char* const fmChoicesStretch[] = { "None", "Fill", "Uniform", "UniformToFill", NULL };
static const char* const fmChoicesBackdrop[] = { "None", "Mica", "Acrylic", "Blur", "Transparent", NULL };

#define FM_P(n, k) { n, k, NULL, NULL, NULL, NULL, NULL }
#define FM_PF(n, k, f) { n, k, f, NULL, NULL, NULL, NULL }
#define FM_PE(n, t, ch) { n, PK_Enum, NULL, t, ch, NULL, NULL }
#define FM_PM(n, k, m) { n, k, NULL, NULL, NULL, m, NULL }
#define FM_PD(n, k, d) { n, k, NULL, NULL, NULL, NULL, d }

static const FmPropDef fmCommon[] = {
    FM_P("Left", PK_Int), FM_P("Top", PK_Int), FM_P("Width", PK_Int), FM_P("Height", PK_Int),
    FM_P("Visible", PK_Bool), FM_P("Enabled", PK_Bool),
    FM_P("Margin", PK_Thickness),
    FM_PE("HorizontalAlign", "UI.HAlign", fmChoicesHAlign),
    FM_PE("VerticalAlign", "UI.VAlign", fmChoicesVAlign),
    FM_P("MinWidth", PK_Int), FM_P("MinHeight", PK_Int), FM_P("MaxWidth", PK_Int), FM_P("MaxHeight", PK_Int),
    FM_P("Background", PK_Color), FM_P("Foreground", PK_Color), FM_P("Pen", PK_Pen),
    FM_P("Font", PK_Text), FM_P("FontSize", PK_Int),
    FM_P("Style", PK_StyleRef), FM_P("Template", PK_TemplateRef),
    FM_PF("Grid.Row", PK_Int, "GridRow"), FM_PF("Grid.Column", PK_Int, "GridColumn"),
    FM_PF("Grid.RowSpan", PK_Int, "GridRowSpan"), FM_PF("Grid.ColumnSpan", PK_Int, "GridColumnSpan"),
    { "DockPanel.Dock", PK_Enum, "Dock", "UI.Dock", fmChoicesDock, NULL, NULL },
};

/* StateProperties */
static const FmPropDef fmStateProperties[] = { FM_P("Hover", PK_Bool), FM_P("Pressed", PK_Bool), FM_P("Focused", PK_Bool) };

static const FmPropDef fmStackProperties[] = {
    FM_PF("Orientation", PK_Orientation, "Horizontal"), FM_P("Horizontal", PK_Bool), FM_P("Spacing", PK_Int), FM_P("Padding", PK_Int),
};
static const FmPropDef fmPanelLook[] = { FM_P("ShowBorder", PK_Bool), FM_P("Filled", PK_Bool) };
static const char* const fmItemParts[] = { "Item", NULL };
static const FmPropDef fmListSources[] = {
    FM_P("ItemsSource", PK_Collection), FM_P("ItemTemplate", PK_DataTemplateRef), FM_PM("View", PK_ViewRef, "SetView"),
};

static const FmPropDef fmCollectionViewProps[] = {
    FM_P("Source", PK_Collection), FM_PF("SortBy", PK_Text, "sortBy"), FM_P("Descending", PK_Bool), FM_P("Filter", PK_Code), FM_P("Comparer", PK_Code),
};

/* the own properties of the elements (each after the common ones) */
static const FmPropDef fmDockOwn[] = { FM_P("ShowBorder", PK_Bool), FM_P("Filled", PK_Bool), FM_P("LastChildFill", PK_Bool) };
static const FmPropDef fmWrapOwn[] = { FM_P("ShowBorder", PK_Bool), FM_P("Filled", PK_Bool), FM_P("Vertical", PK_Bool), FM_P("ItemWidth", PK_Int), FM_P("ItemHeight", PK_Int) };
static const FmPropDef fmFlowOwn[] = { FM_P("ShowBorder", PK_Bool), FM_P("Filled", PK_Bool), FM_P("LineSpacing", PK_Int) };
static const FmPropDef fmRunOwn[] = { FM_P("Text", PK_Text), FM_P("Color", PK_Color) };
static const FmPropDef fmGridOwn[] = { FM_P("ShowBorder", PK_Bool), FM_P("Filled", PK_Bool), FM_PM("Rows", PK_Text, "SetRows"), FM_PM("Columns", PK_Text, "SetColumns") };
static const FmPropDef fmBorderOwn[] = { FM_P("Padding", PK_Thickness), FM_P("Thickness", PK_Int) };
static const FmPropDef fmScrollOwn[] = { FM_PE("VerticalScrollMode", "UI.ScrollMode", fmChoicesScroll), FM_PE("HorizontalScrollMode", "UI.ScrollMode", fmChoicesScroll) };
static const FmPropDef fmLabelOwn[] = { FM_P("Text", PK_Text), FM_P("Color", PK_Color) };
static const FmPropDef fmButtonOwn[] = { FM_P("Text", PK_Text), FM_P("Icon", PK_Geometry), FM_P("Image", PK_Image), FM_P("IconSize", PK_Int) };
static const FmPropDef fmCheckOwn[] = { FM_P("Text", PK_Text), FM_P("IsChecked", PK_Bool) };
static const FmPropDef fmTextBoxOwn[] = { FM_P("Text", PK_Text), FM_P("MaxLength", PK_Int), FM_P("ReadOnly", PK_Bool), FM_P("UndoLimit", PK_Int) };
static const FmPropDef fmTextAreaOwn[] = {
    FM_P("Text", PK_Text), FM_P("MaxLength", PK_Int), FM_P("ReadOnly", PK_Bool), FM_P("WordWrap", PK_Bool),
    FM_P("AcceptsReturn", PK_Bool), FM_P("AcceptsTab", PK_Bool), FM_P("TabSize", PK_Int), FM_P("UndoLimit", PK_Int),
};
static const FmPropDef fmSuggestOwn[] = { FM_P("Text", PK_Text), FM_P("MaxLength", PK_Int), FM_P("MaxSuggestions", PK_Int), FM_P("DisplayMember", PK_Text) };
static const FmPropDef fmListBoxOwn[] = {
    FM_P("DisplayMember", PK_Text), FM_P("SelectedIndex", PK_Int),
    FM_P("ItemsSource", PK_Collection), FM_P("ItemTemplate", PK_DataTemplateRef), FM_PM("View", PK_ViewRef, "SetView"),
};
static const FmPropDef fmComboOwn[] = { FM_P("DisplayMember", PK_Text), FM_P("SelectedIndex", PK_Int), FM_P("MaxDropDownItems", PK_Int), FM_P("ItemsSource", PK_Collection) };
static const FmPropDef fmTreeOwn[] = { FM_P("Indent", PK_Int) };
static const FmPropDef fmRadioOwn[] = { FM_P("Header", PK_Text), FM_P("Horizontal", PK_Bool), FM_P("Spacing", PK_Int), FM_P("SelectedIndex", PK_Int) };
static const FmPropDef fmSliderOwn[] = {
    FM_P("Minimum", PK_Number), FM_P("Maximum", PK_Number), FM_P("Value", PK_Number), FM_P("Step", PK_Number),
    FM_P("LargeStep", PK_Number), FM_P("ShowValue", PK_Bool), FM_P("Decimals", PK_Int),
};
static const FmPropDef fmStepperOwn[] = { FM_P("DisplayMember", PK_Text), FM_P("SelectedIndex", PK_Int), FM_P("Wrap", PK_Bool), FM_P("ItemsSource", PK_Collection) };
static const FmPropDef fmImageOwn[] = { FM_P("Source", PK_Image), FM_PE("Stretch", "UI.Stretch", fmChoicesStretch) };
static const FmPropDef fmShapeOwn[] = { FM_P("Fill", PK_Color), FM_P("Stroke", PK_Pen) };
static const FmPropDef fmLineOwn[] = { FM_P("X1", PK_Int), FM_P("Y1", PK_Int), FM_P("X2", PK_Int), FM_P("Y2", PK_Int), FM_P("Stroke", PK_Pen) };
static const FmPropDef fmPathOwn[] = { FM_PM("Data", PK_Text, "SetData"), FM_P("Fill", PK_Color), FM_P("Stroke", PK_Pen) };
static const FmPropDef fmCanvasOwn[] = { FM_P("Continuous", PK_Bool) };
static const FmPropDef fmItemOwn[] = { FM_PD("Text", PK_Text, "\"\"") };
static const FmPropDef fmColumnOwn[] = { FM_PD("Header", PK_Text, "\"\""), FM_PD("Member", PK_Text, "\"\""), FM_PD("Width", PK_Int, "100") };
static const FmPropDef fmTreeNodeOwn[] = { FM_P("Text", PK_Text), FM_P("Expanded", PK_Bool) };
static const FmPropDef fmMenuOwn[] = { FM_P("Header", PK_Text), FM_P("Enabled", PK_Bool) };
static const FmPropDef fmMenuItemOwn[] = { FM_P("Header", PK_Text), FM_P("Shortcut", PK_Text), FM_P("Checkable", PK_Bool), FM_P("IsChecked", PK_Bool), FM_P("Enabled", PK_Bool) };

static const char* const fmArgsCanvas[] = { "canvas", NULL };
static const char* const fmTypesCanvas[] = { "UI.DrawingCanvas", NULL };
static const char* const fmArgsXYB[] = { "x", "y", "button", NULL };
static const char* const fmTypesXYB[] = { "int", "int", "int", NULL };
static const char* const fmArgsXY[] = { "x", "y", NULL };
static const char* const fmTypesXY[] = { "int", "int", NULL };
static const FmEventDef fmEvOnClick = { "OnClick", NULL, NULL }, fmEvOnChange = { "OnChange", NULL, NULL }, fmEvOnEnter = { "OnEnter", NULL, NULL },
    fmEvOnChosen = { "OnChosen", NULL, NULL }, fmEvOnSelect = { "OnSelect", NULL, NULL }, fmEvOnActivate = { "OnActivate", NULL, NULL },
    fmEvOnPaint = { "OnPaint", fmArgsCanvas, fmTypesCanvas }, fmEvOnMouseDown = { "OnMouseDown", fmArgsXYB, fmTypesXYB },
    fmEvOnMouseMove = { "OnMouseMove", fmArgsXY, fmTypesXY }, fmEvOnMouseUp = { "OnMouseUp", fmArgsXYB, fmTypesXYB };

static const char* const fmPartsSuggestion[] = { "Suggestion", NULL };
static const char* const fmPartsListView[] = { "Column", "Item", NULL };
static const char* const fmPartsTree[] = { "TreeNode", NULL };
static const char* const fmPartsMenuBar[] = { "Menu", NULL };
static const char* const fmPartsMenu[] = { "MenuItem", "MenuSeparator", NULL };
static const char* const fmPartOfItem[] = { "ListBox", "ListView", "RadioButtons", "ComboBox", "Stepper", NULL };
static const char* const fmPartOfSuggestion[] = { "AutoSuggestBox", NULL };
static const char* const fmPartOfColumn[] = { "ListView", NULL };
static const char* const fmPartOfTreeNode[] = { "TreeView", "TreeNode", NULL };
static const char* const fmPartOfMenu[] = { "MenuBar", NULL };
static const char* const fmPartOfMenuItem[] = { "Menu", "MenuItem", NULL };
static const char* const fmAddCallsTreeNode[] = { "TreeView", "{p}.AddNode({e})", "TreeNode", "{p}.Add({e})", NULL };

/* one element of the table: Define (the common properties, then the own ones) or an ElementDef of its own (a part: only its own) */
typedef struct FmElementSpec {
    const char* tag;
    const char* cls;        /* NULL: "UI." + tag */
    FmChildMode children;
    const char* create;
    bool common;
    const FmPropDef* own;
    int ownCount;
    const FmEventDef* events[4];
    const char* addCall;    /* NULL: "{p}.Add({e})" */
    const char* styleKey;
    const char* const* partTags;
    const char* const* partOf;
    const char* const* addCalls;
} FmElementSpec;

#define FM_OWN(a) a, (int)(sizeof a / sizeof a[0])
static const FmElementSpec fmElementSpecs[] = {
    { "Panel", NULL, CM_Elements, "new UI.Panel(0, 0, 100, 100)", true, FM_OWN(fmPanelLook), {0}, NULL, NULL, NULL, NULL, NULL },
    { "Stack", NULL, CM_Elements, "new UI.Stack(0, 0, 100, 100)", true, NULL, -1, {0}, NULL, "StackPanel", NULL, NULL, NULL },
    { "StackPanel", NULL, CM_Elements, "new UI.StackPanel(0, 0, -1, -1)", true, NULL, -1, {0}, NULL, NULL, NULL, NULL, NULL },
    { "Canvas", NULL, CM_Elements, "new UI.Canvas(0, 0, -1, -1)", true, FM_OWN(fmPanelLook), {0}, NULL, NULL, NULL, NULL, NULL },
    { "DockPanel", NULL, CM_Elements, "new UI.DockPanel(0, 0, -1, -1)", true, FM_OWN(fmDockOwn), {0}, NULL, NULL, NULL, NULL, NULL },
    { "WrapPanel", NULL, CM_Elements, "new UI.WrapPanel(0, 0, -1, -1)", true, FM_OWN(fmWrapOwn), {0}, NULL, NULL, NULL, NULL, NULL },
    { "FlowPanel", NULL, CM_Elements, "new UI.FlowPanel(0, 0, -1, -1)", true, FM_OWN(fmFlowOwn), {0}, NULL, NULL, NULL, NULL, NULL },
    { "Run", NULL, CM_None, "new UI.Run(\"\")", true, FM_OWN(fmRunOwn), {0}, NULL, NULL, NULL, NULL, NULL },
    { "LineBreak", NULL, CM_None, "new UI.LineBreak()", true, NULL, 0, {0}, NULL, NULL, NULL, NULL, NULL },
    { "Grid", NULL, CM_Elements, "new UI.Grid(0, 0, -1, -1)", true, FM_OWN(fmGridOwn), {0}, NULL, NULL, NULL, NULL, NULL },
    { "Border", NULL, CM_Single, "new UI.Border(0, 0, -1, -1)", true, FM_OWN(fmBorderOwn), {0}, "{p}.SetChild({e})", NULL, NULL, NULL, NULL },
    { "ScrollViewer", NULL, CM_Single, "new UI.ScrollViewer(0, 0, -1, -1)", true, FM_OWN(fmScrollOwn), {0}, "{p}.SetContent({e})", NULL, NULL, NULL, NULL },
    { "ToolBar", NULL, CM_Elements, "new UI.ToolBar(0, 0, -1, -1)", true, FM_OWN(fmPanelLook), {0}, NULL, NULL, NULL, NULL, NULL },
    { "Label", NULL, CM_None, "new UI.Label(\"\", 0, 0)", true, FM_OWN(fmLabelOwn), {0}, NULL, NULL, NULL, NULL, NULL },
    { "Button", NULL, CM_None, "new UI.Button(\"\", 0, 0)", true, FM_OWN(fmButtonOwn), { &fmEvOnClick }, NULL, NULL, NULL, NULL, NULL },
    { "CheckBox", NULL, CM_None, "new UI.CheckBox(\"\", 0, 0)", true, FM_OWN(fmCheckOwn), { &fmEvOnChange }, NULL, NULL, NULL, NULL, NULL },
    { "TextBox", NULL, CM_None, "new UI.TextBox(\"\", 0, 0)", true, FM_OWN(fmTextBoxOwn), { &fmEvOnChange, &fmEvOnEnter }, NULL, NULL, NULL, NULL, NULL },
    { "TextArea", NULL, CM_None, "new UI.TextArea(\"\", 0, 0)", true, FM_OWN(fmTextAreaOwn), { &fmEvOnChange }, NULL, NULL, NULL, NULL, NULL },
    { "AutoSuggestBox", NULL, CM_Parts, "new UI.AutoSuggestBox(\"\", 0, 0, 160, 24)", true, FM_OWN(fmSuggestOwn), { &fmEvOnChange, &fmEvOnEnter, &fmEvOnChosen }, NULL, NULL, fmPartsSuggestion, NULL, NULL },
    { "ListBox", NULL, CM_Parts, "new UI.ListBox(0, 0, 160, 120)", true, FM_OWN(fmListBoxOwn), { &fmEvOnSelect, &fmEvOnActivate }, NULL, NULL, fmItemParts, NULL, NULL },
    { "ComboBox", NULL, CM_Parts, "new UI.ComboBox(0, 0, 140, 24)", true, FM_OWN(fmComboOwn), { &fmEvOnSelect }, NULL, NULL, fmItemParts, NULL, NULL },
    { "ListView", NULL, CM_Parts, "new UI.ListView(0, 0, 240, 140)", true, FM_OWN(fmListBoxOwn), { &fmEvOnSelect, &fmEvOnActivate }, NULL, NULL, fmPartsListView, NULL, NULL },
    { "TreeView", NULL, CM_Parts, "new UI.TreeView(0, 0, 200, 160)", true, FM_OWN(fmTreeOwn), { &fmEvOnSelect, &fmEvOnActivate }, NULL, NULL, fmPartsTree, NULL, NULL },
    { "RadioButtons", NULL, CM_Parts, "new UI.RadioButtons(0, 0)", true, FM_OWN(fmRadioOwn), { &fmEvOnChange }, NULL, NULL, fmItemParts, NULL, NULL },
    { "Slider", NULL, CM_None, "new UI.Slider()", true, FM_OWN(fmSliderOwn), { &fmEvOnChange }, NULL, NULL, NULL, NULL, NULL },
    { "Stepper", NULL, CM_Parts, "new UI.Stepper(0, 0)", true, FM_OWN(fmStepperOwn), { &fmEvOnChange }, NULL, NULL, fmItemParts, NULL, NULL },
    { "MenuBar", NULL, CM_Parts, "new UI.MenuBar(0, 0, -1, -1)", true, NULL, 0, {0}, NULL, NULL, fmPartsMenuBar, NULL, NULL },
    { "Separator", NULL, CM_None, "new UI.Separator()", true, NULL, 0, {0}, NULL, NULL, NULL, NULL, NULL },
    { "Image", NULL, CM_None, "new UI.Image(undefined, 0, 0, -1, -1)", true, FM_OWN(fmImageOwn), {0}, NULL, NULL, NULL, NULL, NULL },
    { "Rectangle", NULL, CM_None, "new UI.Rectangle(0, 0, -1, -1)", true, FM_OWN(fmShapeOwn), {0}, NULL, NULL, NULL, NULL, NULL },
    { "Ellipse", NULL, CM_None, "new UI.Ellipse(0, 0, -1, -1)", true, FM_OWN(fmShapeOwn), {0}, NULL, NULL, NULL, NULL, NULL },
    { "Line", NULL, CM_None, "new UI.Line(0, 0, 0, 0)", true, FM_OWN(fmLineOwn), {0}, NULL, NULL, NULL, NULL, NULL },
    { "Path", NULL, CM_None, "new UI.Path(undefined, 0, 0)", true, FM_OWN(fmPathOwn), {0}, NULL, NULL, NULL, NULL, NULL },
    { "DrawingCanvas", NULL, CM_None, "new UI.DrawingCanvas(0, 0, 200, 150)", true, FM_OWN(fmCanvasOwn), { &fmEvOnPaint, &fmEvOnMouseDown, &fmEvOnMouseMove, &fmEvOnMouseUp }, NULL, NULL, NULL, NULL, NULL },
    /* ---- parts: what the containers above contain (no common properties, no style) ---- */
    { "Item", "", CM_None, "", false, FM_OWN(fmItemOwn), {0}, "{p}.Add({Text})", NULL, NULL, fmPartOfItem, NULL },
    { "Suggestion", "", CM_None, "", false, FM_OWN(fmItemOwn), {0}, "{p}.AddSuggestion({Text})", NULL, NULL, fmPartOfSuggestion, NULL },
    { "Column", "", CM_None, "", false, FM_OWN(fmColumnOwn), {0}, "{p}.AddColumn({Header}, {Member}, {Width})", NULL, NULL, fmPartOfColumn, NULL },
    { "TreeNode", "UI.TreeNode", CM_Parts, "new UI.TreeNode(\"\")", false, FM_OWN(fmTreeNodeOwn), {0}, NULL, NULL, fmPartsTree, fmPartOfTreeNode, fmAddCallsTreeNode },
    { "Menu", "UI.MenuItem", CM_Parts, "new UI.MenuItem(\"\")", false, FM_OWN(fmMenuOwn), {0}, NULL, NULL, fmPartsMenu, fmPartOfMenu, NULL },
    { "MenuItem", "UI.MenuItem", CM_Parts, "new UI.MenuItem(\"\")", false, FM_OWN(fmMenuItemOwn), { &fmEvOnClick }, NULL, NULL, fmPartsMenu, fmPartOfMenuItem, NULL },
    { "MenuSeparator", "", CM_None, "", false, NULL, 0, {0}, "{p}.Add(UI.MenuItem.Separator())", NULL, NULL, fmPartOfMenuItem, NULL },
};

/* what the context keeps of the schema (made on first use) */
typedef struct FmSchema {
    FcList* elements;          /* FmElementDef* in the order of the table */
    FmElementDef* collectionView;
    FmElementDef* elementTarget;   /* FindTarget("Element") */
} FmSchema;

static void fmAddProps(FcCtx* c, FcList* l, const FmPropDef* p, int n) { int i; for (i = 0; i < n; i++) fcListAdd(c, l, (void*)&p[i]); }

static FmSchema* fmSchema(FcCtx* c) {
    FmSchema* s = (FmSchema*)c->markupSchema;
    size_t k;
    int i;
    if (s) return s;
    s = (FmSchema*)fcAlloc(c, sizeof *s);
    s->elements = fcListNew(c);
    for (k = 0; k < sizeof fmElementSpecs / sizeof fmElementSpecs[0]; k++) {
        const FmElementSpec* sp = &fmElementSpecs[k];
        FmElementDef* d = (FmElementDef*)fcAlloc(c, sizeof *d);
        d->tag = sp->tag;
        d->cls = sp->cls ? sp->cls : fcFmt(c, "UI.%s", sp->tag);
        d->children = sp->children;
        d->create = sp->create;
        d->properties = fcListNew(c);
        if (sp->common) fmAddProps(c, d->properties, fmCommon, (int)(sizeof fmCommon / sizeof fmCommon[0]));
        if (sp->ownCount < 0) {
            /* Stack, StackPanel: StackProperties.Concat(PanelLook) */
            fmAddProps(c, d->properties, fmStackProperties, (int)(sizeof fmStackProperties / sizeof fmStackProperties[0]));
            fmAddProps(c, d->properties, fmPanelLook, (int)(sizeof fmPanelLook / sizeof fmPanelLook[0]));
        } else fmAddProps(c, d->properties, sp->own, sp->ownCount);
        d->events = fcListNew(c);
        for (i = 0; i < 4 && sp->events[i]; i++) fcListAdd(c, d->events, (void*)sp->events[i]);
        d->addCall = sp->addCall ? sp->addCall : "{p}.Add({e})";
        d->styleKey = sp->styleKey;
        d->partTags = sp->partTags;
        d->partOf = sp->partOf;
        d->addCalls = sp->addCalls;
        fcListAdd(c, s->elements, d);
    }
    s->collectionView = (FmElementDef*)fcAlloc(c, sizeof *s->collectionView);
    s->collectionView->tag = "CollectionView";
    s->collectionView->cls = "UI.CollectionView";
    s->collectionView->children = CM_None;
    s->collectionView->create = "new UI.CollectionView(undefined)";
    s->collectionView->properties = fcListNew(c);
    fmAddProps(c, s->collectionView->properties, fmCollectionViewProps, (int)(sizeof fmCollectionViewProps / sizeof fmCollectionViewProps[0]));
    s->collectionView->events = fcListNew(c);
    s->collectionView->addCall = "{p}.Add({e})";
    s->elementTarget = (FmElementDef*)fcAlloc(c, sizeof *s->elementTarget);
    s->elementTarget->tag = "Element";
    s->elementTarget->cls = "UI.Element";
    s->elementTarget->children = CM_None;
    s->elementTarget->create = "";
    s->elementTarget->properties = fcListNew(c);
    fmAddProps(c, s->elementTarget->properties, fmCommon, (int)(sizeof fmCommon / sizeof fmCommon[0]));
    s->elementTarget->events = fcListNew(c);
    s->elementTarget->addCall = "{p}.Add({e})";
    s->elementTarget->styleKey = "Element";
    c->markupSchema = s;
    return s;
}

/* MarkupSchema.Find */
static const FmElementDef* fmFindDef(FcCtx* c, const char* tag) {
    FmSchema* s = fmSchema(c);
    int i;
    for (i = 0; i < s->elements->count; i++) if (fmIEq(((FmElementDef*)s->elements->items[i])->tag, tag)) return (const FmElementDef*)s->elements->items[i];
    return NULL;
}
/* MarkupSchema.FindTarget */
static const FmElementDef* fmFindTarget(FcCtx* c, const char* tag) {
    const FmElementDef* d;
    if (fmIEq(tag, "Element")) return fmSchema(c)->elementTarget;
    d = fmFindDef(c, tag);
    return d && !fmIsPart(d) ? d : NULL;
}

/* IconNames, BuiltInConverters (key, class; OrdinalIgnoreCase) */
static const char* const fmIconNames[] = { "Up", "Down", "Back", "Forward", "Home", "Folder", "File", "Close", "Check", "Add", NULL };
static const char* const fmBuiltInConverters[] = { "Not", "UI.NotConverter", "IsEmpty", "UI.IsEmptyConverter", "NotEmpty", "UI.NotEmptyConverter", "Text", "UI.TextConverter", NULL };
static const char* fmBuiltInConverter(const char* key) {
    int i;
    for (i = 0; fmBuiltInConverters[i]; i += 2) if (fmIEq(fmBuiltInConverters[i], key)) return fmBuiltInConverters[i + 1];
    return NULL;
}
/* string.Join(", ", BuiltInConverters.Keys) */
static const char* fmBuiltInConverterKeys = "Not, IsEmpty, NotEmpty, Text";

/* RootProperties (RootPrefix "UIRoot.") */
static const FmPropDef fmRootProperties[] = {
    { "UIRoot.WindowBackdrop", PK_Enum, "WindowBackdrop", "Backdrop", fmChoicesBackdrop, NULL, NULL },
    FM_PF("UIRoot.WindowBackground", PK_Color, "WindowBackground"),
    FM_PF("UIRoot.Dark", PK_Bool, "Dark"),
    FM_PF("UIRoot.Accent", PK_Int, "Accent"),
    FM_PF("UIRoot.Glass", PK_Bool, "Glass"),
    FM_PF("UIRoot.Transparency", PK_Bool, "Transparency"),
};
static const FmPropDef* fmRootProperty(const char* attribute) {
    size_t i;
    for (i = 0; i < sizeof fmRootProperties / sizeof fmRootProperties[0]; i++) if (fmIEq(fmRootProperties[i].name, attribute)) return &fmRootProperties[i];
    return NULL;
}

/* ReservedNames (ordinal) */
static const char* const fmReservedNames[] = { "framebuffer", "window", "UIRoot", "view", "dataContext", "SetDataContext", "Run", "Show", "OnTick", "Attach", "Open", NULL };
static bool fmIsReserved(const char* name) { int i; for (i = 0; fmReservedNames[i]; i++) if (strcmp(fmReservedNames[i], name) == 0) return true; return false; }

static bool fmAsciiLetter(char ch) { return (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || ch == '_'; }
static bool fmAsciiAlnum(char ch) { return fmAsciiLetter(ch) || (ch >= '0' && ch <= '9'); }
/* ^[A-Za-z_][A-Za-z0-9_]* from p; returns the end or NULL */
static const char* fmMatchIdent(const char* p) {
    if (!fmAsciiLetter(*p)) return NULL;
    p++;
    while (fmAsciiAlnum(*p)) p++;
    return p;
}
/* Identifier: ^[A-Za-z_][A-Za-z0-9_]*$ */
static bool fmIsIdentifier(const char* text) { const char* e = fmMatchIdent(text); return e && fmRegexEnd(e); }

/* RenamedAttributes (OrdinalIgnoreCase) */
static const char* const fmRenamed[] = { "x", "Left", "y", "Top", "halign", "HorizontalAlign", "valign", "VerticalAlign", "vmode", "VerticalScrollMode", "hmode", "HorizontalScrollMode", NULL };
static const char* fmRenamedAttribute(const char* name) { int i; for (i = 0; fmRenamed[i]; i += 2) if (fmIEq(fmRenamed[i], name)) return fmRenamed[i + 1]; return NULL; }

/* StateProperty */
static const FmPropDef* fmStateProperty(const char* name) {
    size_t i;
    if (!name) return NULL;
    for (i = 0; i < sizeof fmStateProperties / sizeof fmStateProperties[0]; i++) if (fmIEq(fmStateProperties[i].name, name)) return &fmStateProperties[i];
    return NULL;
}

/* DottedName: ^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$ */
static bool fmIsDottedName(const char* text) {
    const char* p = fmMatchIdent(text);
    if (!p) return false;
    while (*p == '.' && fmMatchIdent(p + 1)) p = fmMatchIdent(p + 1);
    return fmRegexEnd(p);
}
