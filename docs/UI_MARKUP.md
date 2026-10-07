# UI markup (`.fxml`)

The interface of a program can be designed in a markup file instead of being built in code. The file is XML in the style of XAML; the compiler turns it into a fire script
that defines **one class** - the base class of the code-behind. The code-behind (your script) inherits from it and overrides what it wants to react to, so it never needs to know
more of the generated code than the names it chose itself.

The elements are the ones of the library `ui` (docs/UI.md): the layout containers, the controls, lists, trees, menus, images and shapes.

```xml
<!-- Greeter.fxml -->
<Window class="Greeter" title="Greeter" width="420" height="200">
  <Resources>
    <Converter key="Upper" type="UpperConverter"/>
  </Resources>
  <Stack x="10" y="10" width="400" height="180" spacing="6">
    <Label text="Your name:"/>
    <TextBox name="nameBox" width="220" text="{Binding Name, Mode=TwoWay}"/>
    <CheckBox text="Shout" isChecked="{Binding Shout, Mode=TwoWay}"/>
    <Label text="{Binding Name, Converter=Upper}" color="#C00000"/>
    <Button name="ok" text="OK" onClick="Ok"/>
  </Stack>
</Window>
```

```
// Greeter.script - the code-behind
#include "Greeter.fxml"                   // the script generated from the markup is included right here

class UpperConverter : UI.Converter {
    Convert(value) { return value.ToUpper() }
}

class Person {
    string Name = "fire"
    bool Shout = false
}

class GreeterWindow : GreeterBase {        // the inheritance is declared here, in the code-behind
    Ok(sender) { print("Hello " + this.dataContext.Name) }       // overrides the (empty) handler of onClick="Ok"
}

var window = new GreeterWindow()
window.SetDataContext(new Person())
window.Run()
```

`#include` treats a file ending in `.fxml` like a script: it is translated on the fly, so there is nothing to keep in sync. `fire.Compiler ui Greeter.fxml [-o Greeter.script]` writes the generated
script (to look at it, or to ship it), and the editor shows it too (*Generated script* in the markup editor). Mistakes in the markup are reported with the line of the markup file.

## The generated class

For `<Window class="Greeter">` the script defines `class GreeterBase` (`base="..."` chooses another name). It has

| member | |
|---|---|
| a field per element with `name="..."` | typed with the class of the element: `UI.TextBox nameBox` |
| `framebuffer`, `window`, `ui` | a `Window`: the framebuffer, the window and the `UI.Root` that is created for it (title, width, height from the attributes) |
| `view` | a `View` (below) instead of the three above: a `UI.Panel` |
| one method per handler name | `Ok(sender) { }` - empty; the derived class overrides it. `sender` is the element |
| `Run(other = undefined)`, `OnTick()` | a window: `Run()` loops `ui.Tick()` until the window is closed and calls `OnTick()` once per cycle (override it for a loop of your own); `Run(other)` opens another window first (below) |
| `Open(other)` | a window: another window of the markup (an object of another generated class) or a `UI.Root` is attached to this one: this window's `Run`/`Tick` works it off as well, its own `OnTick()` is called in each cycle, until it is closed. Keep the other window alive as long as it is open (the call moves it under this window) |
| `Attach(container)` | a view: adds the panel to a container (`ui` itself, a `Panel` or a `Stack`) |
| `dataContext`, `SetDataContext(obj)` | only with bindings, see below |

The names of the generated members start with `fx`, plus `framebuffer window ui view dataContext SetDataContext Run OnTick Attach Open`: a named element or a handler must not use them.

`<View class="Settings" width="300" height="200">` is the root of a part of an interface: no window of its own, but a panel that a window adds with `Attach` - the same markup language, so
that a window can be composed of views.

## Elements and properties

Every element has the properties it shares with all others - `x y width height visible enabled`, `margin` (`4`, `4,2` = horizontal, vertical, or `1,2,3,4` = left, top, right, bottom), `halign` (`Stretch Left Center Right`),
`valign` (`Stretch Top Center Bottom`), `minWidth minHeight maxWidth maxHeight`, `background` `foreground` (colours), `pen` (a pen: `#RRGGBB` or `#RRGGBB,3` with the width), `style`, `template` (keys of
resources, see below) - plus `name`, and its own properties. The attached properties of the containers are written as in XAML: `Grid.Row`, `Grid.Column`, `Grid.RowSpan`, `Grid.ColumnSpan`, `DockPanel.Dock` (`Left Top Right Bottom`).
The attribute names are those of the fire fields (the first letter may be upper case: `Text` = `text`).

| element | properties | events |
|---|---|---|
| `Panel`, `Canvas` (container: children are positioned by `x`/`y`) | `showBorder`, `filled` | |
| `Stack` (container, one row or column) | `orientation` (`Horizontal`/`Vertical`), `horizontal`, `spacing`, `padding`, `showBorder`, `filled` | |
| `StackPanel` (like `Stack`, but fits its content instead of 100 x 100) | the same | |
| `DockPanel` (container) | `lastChildFill`; the children say `DockPanel.Dock` | |
| `WrapPanel` (container) | `vertical`, `itemWidth`, `itemHeight` | |
| `Grid` (container) | `rows`, `columns` (`"60, *, auto"`); the children say `Grid.Row` and so on | |
| `Border` (one child) | `padding`, `thickness` | |
| `ScrollViewer` (one child) | `vmode`, `hmode` (`Disabled Auto Visible`) | |
| `ToolBar` (container: buttons and `Separator`s) | `showBorder`, `filled` | |
| `Label` | `text`, `color` (the text brush) | |
| `Button` | `text` | `onClick` |
| `CheckBox` | `text`, `isChecked` | `onChange` |
| `TextBox` | `text`, `maxLength` | `onChange`, `onEnter` |
| `AutoSuggestBox` (`<Suggestion>text</Suggestion>`) | `text`, `maxLength`, `maxSuggestions`, `displayMember` | `onChange`, `onEnter`, `onChosen` |
| `ListBox` (`<Item>text</Item>`) | `displayMember`, `selectedIndex` | `onSelect`, `onActivate` |
| `ListView` (`<Column header="Name" member="name" width="100"/>`) | `displayMember`, `selectedIndex` | `onSelect`, `onActivate` |
| `TreeView` (`<TreeNode text="..." expanded="true">`, nested) | `indent` | `onSelect` |
| `RadioButtons` (`<Item>text</Item>`) | `header`, `horizontal`, `spacing`, `selectedIndex` | `onChange` |
| `MenuBar` (`<Menu header="File">` with `<MenuItem header="Open" shortcut="Ctrl+O" checkable="true" isChecked="true" enabled="false" onClick="Open">`, nested items, `<MenuSeparator/>`) | | `onClick` of the items |
| `Separator` | | |
| `Image` | `source` (a path: the file is embedded, see docs/RESOURCES.md), `stretch` (`None Fill Uniform UniformToFill`) | |
| `Rectangle`, `Ellipse` | `fill` (colour), `stroke` (pen) | |
| `Line` | `x1 y1 x2 y2`, `stroke` | |
| `Path` | `data` (`"M 0 0 L 40 0 L 20 30 Z"`, see docs/UI.md), `fill`, `stroke` | |
| `DrawingCanvas` | `continuous` | `onPaint(canvas)`, `onMouseDown(x, y, button)`, `onMouseMove(x, y)`, `onMouseUp(x, y, button)` |

`<Button>OK</Button>` is `<Button text="OK"/>`, `<Item>Alice</Item>` is `<Item text="Alice"/>`. A `Stack`/`StackPanel`/`DockPanel`/`Grid`/`WrapPanel` lays out its children itself (their `x`/`y` are ignored), in a `Panel` or
`Canvas` they are positioned. A handler of an event with parameters gets them after `sender`: `onMouseDown="Down"` calls `Down(sender, x, y, button)`.

**Values** are read by the type of the property: a whole number (`12`, `-3`, `0x1F`), `true`/`false`, a text, a colour (`#RRGGBB`, `#RGB` or the raw value, see `UI.Color`; colour properties are **brushes** - the generator wraps the value in `new SolidBrush(...)`),
`Horizontal`/`Vertical`, one of the names of an enum (`Left`), a margin, a pen. A value that does not fit is an error. In braces a value can be

- `{Enum Colors.Red}` (or `{Static Colors.Red}`): an enum member or constant of your script, used as it is - for every number or colour property;
- `{Expr some.expression()}`: any fire expression (for a colour property it is a ready-made `Brush`, e.g. `{Expr new SolidBrush(0x80FF0000)}`);
- `{Binding ...}`: a data binding (below); `{TemplateBinding ...}`: inside a control template (below).

A text that starts with a brace is written `{}{like this}`.

**Handlers:** `onClick="Ok"` calls the method `Ok(sender)` of the object. The generated base class defines it empty, so a derived class overrides it - and a handler you do not
override does nothing. (The method has to be declared with exactly the parameters of the generated one: a different parameter list would be an overload, not an override.)

## Styles, triggers and templates

In `<Resources>` (next to the converters):

```xml
<Resources>
  <!-- without a key: the style of ALL buttons (labels, ...) of this interface -->
  <Style target="Label"><Setter property="color" value="#0000C8"/></Style>
  <!-- with a key: for the elements that say style="Primary" -->
  <Style key="Primary" target="Button" basedOn="Base">
    <Setter property="background" value="#3366AA"/>
    <Setter property="margin" value="2,1"/>
    <Trigger property="hover" value="true">                <!-- while the button is hovered -->
      <Setter property="background" value="#4477BB"/>
    </Trigger>
  </Style>
  <ControlTemplate key="Fancy" target="Button">             <!-- the look of a control: ONE root element -->
    <Border name="bd" background="#33AA66" padding="4">
      <Label name="txt" text="{TemplateBinding text}"/>      <!-- follows the property of the control -->
    </Border>
    <Trigger property="pressed" value="true">
      <Setter target="bd" property="background" value="#AA3333"/>   <!-- target: a part of the template -->
    </Trigger>
  </ControlTemplate>
</Resources>
<Button text="Go" style="Primary"/>
<Button text="Go" template="Fancy"/>
```

`target` of a style or template is an element of this table (or `Element`: the shared properties). The properties of a setter are the ones of that element; a trigger tests one of them or `hover`, `pressed`, `focused`
(more conditions with `<Condition property="..." value="..."/>`, in a template with `source="partName"` to test a part). A property that a method sets (`rows`, `data`) cannot be set by a style. Event handlers and `{Binding}` are not available
inside a template (react to the control itself). Everything is explained in docs/UI.md (*Aussehen*); the generated script builds `UI.Style`, `UI.Trigger` and `UI.ControlTemplate` objects (`fxStyle_Key`, `fxTemplate_Key` are fields of the class).

## Lists with data: DataTemplate, CollectionView, itemsSource

`ListBox` and `ListView` can take their rows from data instead of `<Item>` children:

```xml
<Resources>
  <DataTemplate key="Person">                          <!-- one row: ONE root element; bindings go to the ITEM -->
    <StackPanel horizontal="true" spacing="6">
      <Label text="{Binding name, Converter=Upper}"/>
      <Label text="{Binding address.city}"/>
    </StackPanel>
  </DataTemplate>
  <DataTemplate key="Plain"><Label text="{Binding}"/></DataTemplate>      <!-- {Binding} without a path: the item itself -->
  <CollectionView key="ByName" source="{Binding people}" sortBy="name" descending="false" filter="{Expr func (p) on this => { return p.age > 17 }}"/>
</Resources>
<ListBox view="ByName" itemTemplate="Person" selectedIndex="{Binding selected, Mode=TwoWay}"/>
<ListBox itemsSource="{Binding names}" itemTemplate="Plain"/>
```

- **`itemsSource`** is a list: `{Binding path}` (a list of the data context - when the context replaces the list the control follows, when the list changes the rows change) or `{Expr ...}`. The control does not own the list.
- **`<CollectionView key=...>`** is a sorted / filtered view of a list (`UI.CollectionView`): `source` (`{Binding}` or `{Expr}`, read only), `sortBy` (a property of the items), `descending`, `filter` and `comparer`
  (`{Expr ...}`: lambdas; write `&lt;` for `<` inside an attribute, as everywhere in XML). A list says `view="Key"`; give either `view` or `itemsSource`. The view is a field of the class, `fxView_Key`, so the code-behind can change it (`fxView_ByName.SortBy("age")`, `Invalidate()`).
- **`<DataTemplate key=...>`** builds one set of elements per item (`UI.DataTemplate`, the field is `fxData_Key`). `{Binding Path, Mode=..., Converter=...}` inside it binds to the item (`Path` through objects, empty = the item
  itself, which is read only); `TwoWay` writes into the item. There are no names, handlers, `ElementName` or `TemplateBinding` in a data template, and the rows only show data (they do not take input).
  A text property shows any value as its text. Without `itemTemplate` a row shows the item as text (`displayMember` names the property).
- `selectedIndex="{Binding selected, Mode=TwoWay}"` ties the selection to the data context in both directions.
- The design view cannot run the data: a list with `itemsSource` or `view` shows three empty rows, and the bound texts of the data template read `‹path›`.
- Keys of styles, templates, data templates and views share one namespace.

## Data binding

`{Binding Path, Mode=..., Converter=..., ElementName=...}` ties a property of an element to a property of the **data context** - the object given to `SetDataContext(obj)` -
or, with `ElementName`, to a property of another named element. It is built on `probe` (SPEC 8.14): nothing polls, the property changes when the source field is written.

- **Path**: `Name`, or a path through objects: `Player.Stats.Hp`. Every object on the way is watched, so if `Player` is replaced the binding connects to the new one. A `null`/`undefined` on the way leaves the property as it is.
- **Mode**: `OneWay` (default; the source changes the property), `TwoWay` (the property writes back to the source as well - a `TextBox` as the user types, a `CheckBox` when it is toggled; a write
  that does not change the value stops there, so a converter that normalizes a value cannot make the two chase each other), `OneTime` (read once when the context is set).
- **Converter**: the key of a converter that sits between the two sides. A converter is a class with `Convert(value)` (source to property) and, for `TwoWay`, `ConvertBack(value)`;
  deriving from `UI.Converter` supplies the identity for the one you do not write. Declare them in `<Resources><Converter key="Upper" type="UpperConverter"/></Resources>`; these come with the library
  and need no declaration: `Not`, `IsEmpty`, `NotEmpty`, `Text` (`UI.NotConverter`, ...).
- `SetDataContext(obj)` disconnects the old context and connects the new one; `SetDataContext(undefined)` leaves the elements as they are.

## The editor

`.fxml` files open in a markup editor (File > New UI Markup): XML with colours, next to it the **design view** - the picture of the interface drawn by the library `ui` itself (the same code that runs in your program, so sizes,
layout, styles and templates are exact; the markup is translated into a script that only draws and is run in the background - no handler and none of your own code runs, `{Enum ...}` and `{Expr ...}` values are left out and a bound text
is shown as `‹Path›`). It is only for looking; nothing in it reacts to the mouse. The element at the caret is outlined and mistakes of the markup are listed under the picture and underlined in the text (the last good picture stays
while there are mistakes). Image files (`<Image source="logo.png"/>`) are looked for next to the markup file. *Insert* adds a snippet at the caret, *Generated script* opens the script that the markup becomes.
