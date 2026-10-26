# UI markup (`.fxml`)

The interface of a program can be designed in a markup file instead of being built in code. The file is XML in the style of XAML; the compiler turns it into a fire script
that defines **one class** - the base class of the code-behind. The code-behind (your script) inherits from it and overrides what it wants to react to, so it never needs to know
more of the generated code than the names it chose itself.

The elements are the ones of the library `ui` (docs/UI.md): the layout containers, the controls, lists, trees, menus, images and shapes.

```xml
<!-- Greeter.fxml -->
<Window Class="Greeter" Title="Greeter" Width="420" Height="200">
  <Resources>
    <Converter Key="Upper" Type="UpperConverter"/>
  </Resources>
  <Stack Left="10" Top="10" Width="400" Height="180" Spacing="6">
    <Label Text="Your name:"/>
    <TextBox Name="nameBox" Width="220" Text="{Binding Name, Mode=TwoWay}"/>
    <CheckBox Text="Shout" IsChecked="{Binding Shout, Mode=TwoWay}"/>
    <Label Text="{Binding Name, Converter=Upper}" Color="#C00000"/>
    <Button Name="ok" Text="OK" OnClick="Ok"/>
  </Stack>
</Window>
```

```
// Greeter.fire - the code-behind
#include "Greeter.fxml"                   // the script generated from the markup is included right here

class UpperConverter : UI.Converter {
    any Convert(any value) { return value.ToUpper() }
}

class Person {
    string Name = "fire"
    bool Shout = false
}

class GreeterWindow : GreeterBase {        // the inheritance is declared here, in the code-behind
    undefined Ok(any sender) { print("Hello " + this.dataContext.Name) }       // overrides the (empty) handler of OnClick="Ok"
}

var window = new GreeterWindow()
window.SetDataContext(new Person())
window.Run()
```

`SetDataContext` keeps an object that is handed to it (`new Person()` or `take model`); an object that belongs to someone else stays theirs and
the window only points to it.

In a project the `#include` is not needed: the `.fxml` files of a project are compiled with it (docs/PROJECTS.md), and the code-behind is simply the `.fire` file of the
same name next to it. The editor makes both when you add an **FXML Window** or **FXML View** (Add > New), and the solution explorer shows the markup as a node that opens: its
first row **Designer** opens the markup editor (like a double click on the markup itself), below it is the code-behind.

`#include` treats a file ending in `.fxml` like a script: it is translated on the fly, so there is nothing to keep in sync. `fire.Compiler ui Greeter.fxml [-o Greeter.fire]` writes the generated
script (to look at it, or to ship it), and the editor shows it too (*Generated script* in the markup editor). Mistakes in the markup are reported with the line of the markup file.

## Namespaces

The root element says which namespace the markup belongs to and which other namespaces it uses:

```xml
<Window Class="Shop" namespace="App" namespace:m="App.Models" namespace:ui2="Widgets">
  <Resources>
    <Converter Key="Price" Type="PriceConverter"/>          <!-- unqualified: found in App, App.Models or Widgets -->
    <Converter Key="Name" Type="m:NameConverter"/>          <!-- App.Models.NameConverter -->
  </Resources>
  <Label Color="{Enum local:Palette.Accent}" Background="[ui2:Gradient #000000, #336699]"/>
</Window>
```

- `namespace="App"` puts the generated class into `namespace App` (`App.ShopBase`); `local:Name` is a name in it.
- `namespace:prefix="Name.Space"` binds another namespace to a prefix: `m:Person` is `App.Models.Person`.
- A type name without a prefix (a converter, `[Type ...]`) is looked up in the namespace of the markup first and then in all bound namespaces (the generated script carries a `#using` for
  each). As long as only one of them has the name, that is enough; when two bound namespaces have it, the compiler reports the name as ambiguous (`E4062`) - write it with its prefix.
- `{Enum ...}` and `{Static ...}` are fire code, and fire reaches a static member or an enum only by its full name (`#using` applies to type names): write them with a prefix (`{Enum m:Colors.Red}`) or fully qualified.

The `#using` lines of the generated script also apply to the file that includes it (`#include` inserts the script), so the code-behind can use the same names.

## The generated class

For `<Window Class="Greeter">` the script defines `class GreeterBase` (`Base="..."` chooses another name). It has

| member | |
|---|---|
| a field per element with `name="..."` | typed with the class of the element: `UI.TextBox nameBox` |
| `framebuffer`, `window`, `ui` | a `Window`: the framebuffer, the window and the `UI.Root` that is created for it (`Title`, `Width`, `Height` from the attributes) |
| `view` | a `View` (below) instead of the three above: a `UI.Panel` |
| one method per handler name | `undefined Ok(any sender) { }` - empty; the derived class overrides it. `sender` is the element |
| `Run(other = undefined)`, `OnTick()` | a window: `Run()` is the start of the program - it runs the window's manager (docs/CONSOLE.md, *Window managers*) on this thread until the windows are closed (if it already runs, the window is only shown there) and calls `OnTick()` once per cycle (override it for a loop of your own); `Run(other)` opens another window first (below) |
| `Show(modal = false)` | a window: shows it and goes on - a window manager that nobody runs yet gets a thread of its own, the program handles the events at its safe points and waits at its end until the window is closed. `Show(true)`: modal - returns when the window is closed, the other windows of the manager take no input meanwhile |
| `Open(other)` | a window: another window of the markup (an object of another generated class) or a `UI.Root` is attached to this one: this window's `Run`/`Tick` works it off as well, its own `OnTick()` is called in each cycle, until it is closed. Keep the other window alive as long as it is open (the call moves it under this window) |
| `Attach(container)` | a view: adds the panel to a container (`ui` itself, a `Panel` or a `Stack`) |
| `dataContext`, `SetDataContext(obj)` | only with bindings, see below |

The names of the generated members start with `fx`, plus `framebuffer window ui view dataContext SetDataContext Run Show OnTick Attach Open`: a named element or a handler must not use them.

`<View Class="Settings" Width="300" Height="200">` is the root of a part of an interface: no window of its own, but a panel that a window adds with `Attach` - the same markup language, so
that a window can be composed of views.

## Elements and properties

Every element has the properties it shares with all others - `Left Top Width Height Visible Enabled`, `Margin` (`4`, `4,2` = horizontal, vertical, or `1,2,3,4` = left, top, right, bottom), `HorizontalAlign` (`Stretch Left Center Right`),
`VerticalAlign` (`Stretch Top Center Bottom`), `MinWidth MinHeight MaxWidth MaxHeight`, `Background` `Foreground` (colours), `Pen` (a pen: `#RRGGBB` or `#RRGGBB,3` with the width), `Font` (a font name for `Fonts.Get`, docs/FONTS.md; empty = that of the surrounding element) and `FontSize` (pixels), `Style`, `Template` (keys of
resources, see below) - plus `Name`, and its own properties. The attached properties of the containers are written as in XAML: `Grid.Row`, `Grid.Column`, `Grid.RowSpan`, `Grid.ColumnSpan`, `DockPanel.Dock` (`Left Top Right Bottom`).
The attribute names are written in PascalCase (case does not matter when they are read). Each sets the field of the element with exactly the same name
(`Text` sets `button.Text`, `IsChecked` sets `IsChecked`, `OnClick` sets `OnClick`, `Left`/`Top` set `Left`/`Top`), so code and markup use the same names -
only the attached properties differ (`Grid.Row` sets `GridRow`, `DockPanel.Dock` sets `Dock`) and `Orientation` of a stack sets `Horizontal`.
The old names `x`, `y`, `halign`, ... are reported with the new one.

| element | properties | events |
|---|---|---|
| `Panel`, `Canvas` (container: children are positioned by `Left`/`Top`) | `ShowBorder`, `Filled` | |
| `Stack` (container, one row or column) | `Orientation` (`Horizontal`/`Vertical`), `Horizontal`, `Spacing`, `Padding`, `ShowBorder`, `Filled` | |
| `StackPanel` (like `Stack`, but fits its content instead of 100 x 100) | the same | |
| `DockPanel` (container) | `LastChildFill`; the children say `DockPanel.Dock` | |
| `WrapPanel` (container) | `Vertical`, `ItemWidth`, `ItemHeight` | |
| `Grid` (container) | `Rows`, `Columns` (`"60, *, auto"`); the children say `Grid.Row` and so on | |
| `Border` (one child) | `Padding`, `Thickness` | |
| `ScrollViewer` (one child) | `VerticalScrollMode`, `HorizontalScrollMode` (`Disabled Auto Visible`) | |
| `ToolBar` (container: buttons and `Separator`s) | `ShowBorder`, `Filled` | |
| `Label` | `Text`, `Color` (the text brush) | |
| `Button` | `Text`, `Icon` (a symbol of `UI.Icons` such as `Up`, or path data `M 8 1 L 15 8 L 1 8 Z`), `Image` (an image file, embedded), `IconSize` | `OnClick` |
| `CheckBox` | `Text`, `IsChecked` | `OnChange` |
| `TextBox` | `Text`, `MaxLength` | `OnChange`, `OnEnter` |
| `AutoSuggestBox` (`<Suggestion>text</Suggestion>`) | `Text`, `MaxLength`, `MaxSuggestions`, `DisplayMember` | `OnChange`, `OnEnter`, `OnChosen` |
| `ListBox` (`<Item>text</Item>`) | `DisplayMember`, `SelectedIndex` | `OnSelect`, `OnActivate` |
| `ComboBox` (`<Item>text</Item>`) | `DisplayMember`, `SelectedIndex`, `MaxDropDownItems`, `ItemsSource` | `OnSelect` |
| `ListView` (`<Column Header="Name" Member="name" Width="100"/>`) | `DisplayMember`, `SelectedIndex` | `OnSelect`, `OnActivate` |
| `TreeView` (`<TreeNode Text="..." Expanded="true">`, nested) | `Indent` | `OnSelect` |
| `RadioButtons` (`<Item>text</Item>`) | `Header`, `Horizontal`, `Spacing`, `SelectedIndex` | `OnChange` |
| `MenuBar` (`<Menu Header="File">` with `<MenuItem Header="Open" Shortcut="Ctrl+O" Checkable="true" IsChecked="true" Enabled="false" OnClick="Open">`, nested items, `<MenuSeparator/>`) | | `OnClick` of the items |
| `Separator` | | |
| `Image` | `Source` (a path: the file is embedded, see docs/RESOURCES.md), `Stretch` (`None Fill Uniform UniformToFill`) | |
| `Rectangle`, `Ellipse` | `Fill` (colour), `Stroke` (pen) | |
| `Line` | `X1 Y1 X2 Y2`, `Stroke` | |
| `Path` | `Data` (`"M 0 0 L 40 0 L 20 30 Z"`, see docs/UI.md), `Fill`, `Stroke` | |
| `DrawingCanvas` | `Continuous` | `OnPaint(canvas)`, `OnMouseDown(x, Top, button)`, `OnMouseMove(x, Top)`, `OnMouseUp(x, Top, button)` |

`<Button>OK</Button>` is `<Button Text="OK"/>`, `<Item>Alice</Item>` is `<Item Text="Alice"/>`. A `Stack`/`StackPanel`/`DockPanel`/`Grid`/`WrapPanel` lays out its children itself (their `Left`/`Top` are ignored), in a `Panel` or
`Canvas` they are positioned. A handler of an event with parameters gets them after `sender`: `OnMouseDown="Down"` calls `Down(sender, Left, Top, button)`.

**Values** are read by the type of the property: a whole number (`12`, `-3`, `0x1F`), `true`/`false`, a text, a colour (`#RRGGBB`, `#RGB` or the raw value, see `UI.Color`; colour properties are **brushes** - the generator wraps the value in `new SolidBrush(...)`),
`Horizontal`/`Vertical`, one of the names of an enum (`Left`), a margin, a pen. A value that does not fit is an error. In braces a value can be

- `{Enum Colors.Red}` (or `{Static Colors.Red}`): an enum member or constant of your script, used as it is - for every number or colour property;
- `{Expr some.expression()}`: any fire expression (for a colour property it is a ready-made `Brush`, e.g. `{Expr new SolidBrush(0x80FF0000)}`);
- `{Binding ...}`: a data binding (below); `{TemplateBinding ...}`: inside a control template (below).

A text that starts with a brace is written `{}{like this}`.

**Objects:** `[Type Arg1, Arg2]` assigns a new instance of `Type` - the arguments go to its constructor: `Background="[SolidBrush #336699]"`, `ItemsSource="[Names 'Ada', 'Bob']"`.
An argument is a number (also with a unit: `2.5mm`), a text in quotes (`'it''s'` - single quotes are easiest inside an attribute; two quotes stand for one), `#RRGGBB` (a colour number),
`true`/`false`/`undefined`, `{Enum ...}`/`{Static ...}`/`{Expr ...}` or another object (`[Pair 1, [Point 2, 3]]`). The value is used as it is: a colour property gets the brush itself, not a
`SolidBrush` around it. The design view makes objects of the libraries (`UI.*`, `SolidBrush`, `Pen`); objects of your own classes are left out there, like `{Expr ...}`.
A text that starts with `[` and a name is written `{}[like this]`.

**Handlers:** `OnClick="Ok"` calls the method `undefined Ok(any sender)` of the object. The generated base class defines it empty, so a derived class overrides it - and a handler you do not
override does nothing. (The method has to be declared with exactly the parameters of the generated one: a different parameter list would be an overload, not an override.)

## Styles, triggers and templates

In `<Resources>` (next to the converters):

```xml
<Resources>
  <!-- without a key: the style of ALL buttons (labels, ...) of this interface -->
  <Style Target="Label"><Setter Property="Color" Value="#0000C8"/></Style>
  <!-- with a key: for the elements that say style="Primary" -->
  <Style Key="Primary" Target="Button" BasedOn="Base">
    <Setter Property="Background" Value="#3366AA"/>
    <Setter Property="Margin" Value="2,1"/>
    <Trigger Property="Hover" Value="true">                <!-- while the button is hovered -->
      <Setter Property="Background" Value="#4477BB"/>
    </Trigger>
  </Style>
  <ControlTemplate Key="Fancy" Target="Button">             <!-- the look of a control: ONE root element -->
    <Border Name="bd" Background="#33AA66" Padding="4">
      <Label Name="txt" Text="{TemplateBinding Text}"/>      <!-- follows the property of the control -->
    </Border>
    <Trigger Property="Pressed" Value="true">
      <Setter Target="bd" Property="Background" Value="#AA3333"/>   <!-- target: a part of the template -->
    </Trigger>
  </ControlTemplate>
</Resources>
<Button Text="Go" Style="Primary"/>
<Button Text="Go" Template="Fancy"/>
```

`Target` of a style or template is an element of this table (or `Element`: the shared properties). The properties of a setter are the ones of that element; a trigger tests one of them or `Hover`, `Pressed`, `Focused`
(more conditions with `<Condition Property="..." Value="..."/>`, in a template with `Source="partName"` to test a part). A property that a method sets (`Rows`, `Data`) cannot be set by a style. Event handlers and `{Binding}` are not available
inside a template (react to the control itself). Everything is explained in docs/UI.md (*Aussehen*); the generated script builds `UI.Style`, `UI.Trigger` and `UI.ControlTemplate` objects (`fxStyle_Key`, `fxTemplate_Key` are fields of the class).

## Lists with data: DataTemplate, CollectionView, ItemsSource

`ListBox` and `ListView` can take their rows from data instead of `<Item>` children:

```xml
<Resources>
  <DataTemplate Key="Person">                          <!-- one row: ONE root element; bindings go to the ITEM -->
    <StackPanel Horizontal="true" Spacing="6">
      <Label Text="{Binding name, Converter=Upper}"/>
      <Label Text="{Binding address.city}"/>
    </StackPanel>
  </DataTemplate>
  <DataTemplate Key="Plain"><Label Text="{Binding}"/></DataTemplate>      <!-- {Binding} without a path: the item itself -->
  <CollectionView Key="ByName" Source="{Binding people}" SortBy="name" Descending="false" filter="{Expr func (p) on this => { return p.age > 17 }}"/>
</Resources>
<ListBox View="ByName" ItemTemplate="Person" SelectedIndex="{Binding selected, Mode=TwoWay}"/>
<ListBox ItemsSource="{Binding names}" ItemTemplate="Plain"/>
```

- **`ItemsSource`** is a list: `{Binding path}` (a list of the data context - when the context replaces the list the control follows, when the list changes the rows change) or `{Expr ...}`. The control does not own the list.
- **`<CollectionView Key=...>`** is a sorted / filtered view of a list (`UI.CollectionView`): `Source` (`{Binding}` or `{Expr}`, read only), `SortBy` (a property of the items), `Descending`, `Filter` and `Comparer`
  (`{Expr ...}`: lambdas; write `&lt;` for `<` inside an attribute, as everywhere in XML). A list says `View="Key"`; give either `View` or `ItemsSource`. The view is a field of the class, `fxView_Key`, so the code-behind can change it (`fxView_ByName.SortBy("age")`, `Invalidate()`).
- **`<DataTemplate Key=...>`** builds one set of elements per item (`UI.DataTemplate`, the field is `fxData_Key`). `{Binding Path, Mode=..., Converter=...}` inside it binds to the item (`Path` through objects, empty = the item
  itself, which is read only); `TwoWay` writes into the item. There are no names, handlers, `ElementName` or `TemplateBinding` in a data template, and the rows only show data (they do not take input).
  A text property shows any value as its text. Without `ItemTemplate` a row shows the item as text (`DisplayMember` names the property).
- `SelectedIndex="{Binding selected, Mode=TwoWay}"` ties the selection to the data context in both directions.
- The design view cannot run the data: a list with `ItemsSource` or `View` shows three empty rows, and the bound texts of the data template read `‹path›`.
- Keys of styles, templates, data templates and views share one namespace.

## Data binding

`{Binding Path, Mode=..., Converter=..., ElementName=...}` ties a property of an element to a property of the **data context** - the object given to `SetDataContext(obj)` -
or, with `ElementName`, to a property of another named element. It is built on `probe` (SPEC 8.14): nothing polls, the property changes when the source field is written.

- **Path**: `Name`, or a path through objects: `Player.Stats.Hp`. Every object on the way is watched, so if `Player` is replaced the binding connects to the new one. A `null`/`undefined` on the way leaves the property as it is.
- **Fields and properties**: the last step of a path may be a field or a property with `get`/`set`. A write to either (also through the setter of a property) updates the target, and a `TwoWay` binding writes through the setter. A property that only computes its value from other
  members (`get` without `set`) follows its sources: when a field (or settable property) that its getter reads is written, the getter runs again and the target gets the new value
  (SPEC 8.14 - what the getter reads through other objects, `this.player.name`, is not followed).
- **Mode**: `OneWay` (default; the source changes the property), `TwoWay` (the property writes back to the source as well - a `TextBox` as the user types, a `CheckBox` when it is toggled; a write
  that does not change the value stops there, so a converter that normalizes a value cannot make the two chase each other), `OneTime` (read once when the context is set).
- **Converter**: the key of a converter that sits between the two sides. A converter is a class with `any Convert(any value)` (source to property) and, for `TwoWay`, `any ConvertBack(any value)`;
  deriving from `UI.Converter` supplies the identity for the one you do not write. Declare them in `<Resources><Converter Key="Upper" Type="UpperConverter"/></Resources>`; these come with the library
  and need no declaration: `Not`, `IsEmpty`, `NotEmpty`, `Text` (`UI.NotConverter`, ...).
- `SetDataContext(obj)` disconnects the old context and connects the new one; `SetDataContext(undefined)` leaves the elements as they are.

## The editor

`.fxml` files open in a markup editor (File > New > Script... > FXML Window or FXML View): XML with colours, next to it the **design view** - the picture of the interface drawn by the library `ui` itself (the same code that runs in your program, so sizes,
layout, styles and templates are exact; the markup is translated into a script that only draws and is run in the background - no handler and none of your own code runs, `{Enum ...}` and `{Expr ...}` values are left out and a bound text
is shown as `‹Path›`). A click on an element in the picture puts the caret into it; nothing else in it reacts to the mouse. The element at the caret is outlined and mistakes of the markup are listed under the picture and underlined in the text (the last good picture stays
while there are mistakes). Image files (`<Image Source="logo.png"/>`) are looked for next to the markup file. *Insert* adds a snippet at the caret, *Generated script* opens the script that the markup becomes.

The tool window **Properties** (View > Properties) lists the attributes the element at the caret can have - its own properties, the ones all elements share, the attached
properties of the containers and the events - with the ones it has in white. Changing a value writes the attribute into the text (one step of Undo; the rest of the
file stays as it is), choosing *(default)* or emptying a field removes it. Choices (`HorizontalAlign`, `true`/`false`, enums) are lists, colours show a swatch.
