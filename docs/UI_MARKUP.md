# UI markup (`.fxml`)

The interface of a program can be designed in a markup file instead of being built in code. The file is XML in the style of XAML; the compiler turns it into a fire script
that defines **one class** - the base class of the code-behind. The code-behind (your script) inherits from it and overrides what it wants to react to, so it never needs to know
more of the generated code than the names it chose itself.

The elements are the ones of the library `ui` (docs/UI.md): `Panel`, `Stack`, `Label`, `Button`, `CheckBox`, `TextBox`.

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
| `Run()`, `OnTick()` | a window: `Run()` loops `ui.Tick()` until the window is closed and calls `OnTick()` once per cycle (override it for a loop of your own) |
| `Attach(container)` | a view: adds the panel to a container (`ui` itself, a `Panel` or a `Stack`) |
| `dataContext`, `SetDataContext(obj)` | only with bindings, see below |

The names of the generated members start with `fx`, plus `framebuffer window ui view dataContext SetDataContext Run OnTick Attach`: a named element or a handler must not use them.

`<View class="Settings" width="300" height="200">` is the root of a part of an interface: no window of its own, but a panel that a window adds with `Attach` - the same markup language, so
that a window can be composed of views.

## Elements and properties

Every element has `x y width height visible enabled` (as the `UI.Element` fields), `name`, and its own properties. The attribute names are those of the fire fields (the first letter may be
upper case: `Text` = `text`).

| element | properties | events |
|---|---|---|
| `Panel` (container) | `showBorder`, `background` | |
| `Stack` (container) | `orientation` (`Horizontal`/`Vertical`), `horizontal`, `spacing`, `padding`, `showBorder`, `background` | |
| `Label` | `text`, `color` | |
| `Button` | `text` | `onClick` |
| `CheckBox` | `text`, `isChecked` | `onChange` |
| `TextBox` | `text`, `maxLength` | `onChange`, `onEnter` |

`<Button>OK</Button>` is `<Button text="OK"/>`. A `Stack` lays out its children itself (their `x`/`y` are ignored), in a `Panel` they are positioned.

**Values** are read by the type of the property: a whole number (`12`, `-3`, `0x1F`), `true`/`false`, a text, a colour (`#RRGGBB`, `#RGB` or the raw value, see `UI.Color`),
`Horizontal`/`Vertical`. A value that does not fit is an error. In braces a value can be

- `{Enum Colors.Red}` (or `{Static Colors.Red}`): an enum member or constant of your script, used as it is - for every number or colour property;
- `{Expr some.expression()}`: any fire expression;
- `{Binding ...}`: a data binding (below).

A text that starts with a brace is written `{}{like this}`.

**Handlers:** `onClick="Ok"` calls the method `Ok(sender)` of the object. The generated base class defines it empty, so a derived class overrides it - and a handler you do not
override does nothing. (The method has to be declared with exactly one parameter, like the generated one: a different parameter list would be an overload, not an override.)

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

`.fxml` files open in a markup editor (File > New UI Markup): XML with colours, next to it the **design view** - a picture of the interface as the library draws it (same sizes, same layout
rules, the light theme of the library). It is only for looking; nothing in it reacts to the mouse. The element at the caret is outlined, the text of a binding is shown as `‹Path›`, and
mistakes of the markup are listed under the picture and underlined in the text. *Insert* adds a snippet at the caret, *Generated script* opens the script that the markup becomes.

## What it is not

There is no resource system besides converters, no styles or templates, no layout other than `Panel` (positions) and `Stack` (one row or column), and the properties are the fields of the
`ui` elements. Bindings do not look *inside* a collection (`list.Add` is not a write that a probe sees, SPEC 8.14).
