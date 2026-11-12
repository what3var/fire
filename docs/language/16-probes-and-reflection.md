# Probes and reflection

## Probes: watching a member

A `probe` runs code whenever a member of an object is written - from anywhere, without changing the class. `silence` removes probes again. No
import is needed.

```fire
class Config {
    int volume = 10
    int Double { get { return this.volume * 2 } }
}
var cfg = new Config()

var h = probe cfg.volume changed { print(name + ": " + old + " -> " + value) }
probe cfg.volume changing (int old, int nv) => nv <= 100     // false stops the write

cfg.volume = 20             // volume: 10 -> 20
cfg.volume = 500            // refused by the changing probe
print(cfg.volume)           // 20
cfg.volume++                // volume: 20 -> 21

silence h                   // remove this one probe
silence cfg                 // remove all probes of the object
```

### When a probe runs

| Kind | Runs | Can stop the write |
|---|---|---|
| `changing` | **before** the write | yes - a handler that returns `false` stops it |
| `changed` | **after** the write, only if the value really changed | no |

- Writes are `=`, `++`/`--`, `+=` and the other compound assignments, and writes through reflection. Changes **inside** a member (`obj.list.Add(...)`,
  an array element) are not writes to the member.
- On a property the probe runs around the setter. On a **computed property** (only `get`), a `changed` probe follows the fields the getter reads and
  fires when the result changes.
- Probes run in the thread of the writer, in the order they were registered. A handler that writes the same member does not trigger itself again.

### Handlers

| Form | Names / parameters |
|---|---|
| block: `probe x.m changed { ... }` | `sender`, `name`, `old`, `value` |
| expression: `probe x.m changed => expr` | the same four names |
| lambda with 0 to 4 parameters | `()`, `(new)`, `(old, new)`, `(object, old, new)`, `(object, name, old, new)` |
| any lambda value: `probe x.m changed handler` | as above |

### Targets

- `probe a.b.c changed ...` evaluates `a.b` once and attaches the probe to **that object** and member `c`.
- `probe obj.* changed ...` watches all members of `obj`.
- `probe ...` is an expression: it returns a handle (`int`) for `silence handle`.
- `silence obj.member` removes the probes of one member, `silence obj` or `silence obj.*` all of the object.
- Probes end with their object.

Probes are the base of the data bindings of the UI library (see the UI documentation).

## Reflection: `#import "reflection"`

Reflection describes classes at run time and uses members by their **name**.

```fire
#import "reflection"
class Shape { string label = "shape" }
class Circle : Shape {
    float radius
    construct(float r) { this.radius = r }
    float Diameter { get { return this.radius * 2 } set { this.radius = value / 2 } }
    float Scale(float f) { this.radius *= f; return this.radius }
}

var c = new Circle(5.0)
var t = Type.Of(c)
print(t.Name + " : " + t.Base.Name)             // Circle : Shape
foreach (m in t.All) {
    print(m.Kind + " " + m.Access + " " + m.TypeName + " " + m.Name)
}
print(Reflect.Get(c, "radius"))                 // 5
Reflect.Set(c, "Diameter", 20.0)
print(c.radius)                                 // 10
print(Reflect.Call(c, "Scale", [2.0]))          // 20
var c2 = Reflect.New("Circle", [1.5])
print(Reflect.Has(c, "Area"))                   // False
try {
    Reflect.Get(c, "nope")
} catch (ReflectionException e) {
    print(e.message)
}
```

`Reflect.Set(obj, name, value)` owns like `obj.name = value`: what belongs to the call - a fresh value (`new List<Item>()`, an array literal),
`copy x`, `take x` - belongs to `obj` from then on; an object of someone else stays theirs (the field refers to it). The same in the VM and natively.

| Class | Members |
|---|---|
| `Type` | `Type.Of(obj or name)`, `Type.Of<T>()` (the type argument of a generic class or method that runs; also `Type.Of<Person>()`), `Type.Named(name)` (or `undefined`), `Type.Names()`; `Name`, `Base`, `Interfaces`, `DeclaredInterfaces`, `IsActor`, `All`, `Fields()`, `Properties()`, `Methods()`, `Constructors()`, `Find(name)`, `Has(name)`, `IsSubclassOf(type)`, `New(args)`, `Attributes`, `FindAttribute(name)`, `HasAttribute(name)`, `HasFlag(flag)`, `Flags(enum)` |
| `Member` | `Name`, `Kind` (`field`, `property`, `method`, `constructor`), `TypeName`, `Access`, `IsStatic`, `IsReadonly`, `CanRead`, `CanWrite`, `Unit`, `DeclaredIn`, `ParamNames`, `ParamTypes`, `ParamCount()`, `Attributes`, `FindAttribute(name)`, `HasAttribute(name)`, `HasFlag(flag)`, `Flags(enum)`, `Get(obj)`, `Set(obj, v)`, `Call(obj, args)`, `Probe(obj, kind, handler)` |
| `Attribute` | `Name`, `Args`, `ArgTexts`, `ArgCount()` - an attribute of a class, method, field or property (see [Classes](08-classes.md#attributes)) |
| `Reflect` | `Get`, `Set`, `Call`, `New`, `Has`, `Probe`, `ProbeAll`, `Silence`, `SilenceAll`, `SilenceHandle` |

- Reflection follows the same rules as normal code: `private`/`protected` apply to the calling code, `readonly` fields cannot be set, units are checked.
- Errors are `ReflectionException`s.
- Static members are described but cannot be used through `Reflect`.
- `Type.Interfaces` holds every interface an object of the class is of (as `is of` sees it): the ones the class names, the ones those extend and those
  of its base classes - each once, by its full name (`Geo.IShape`), the nearest first. `Type.DeclaredInterfaces` holds only the ones the class names
  itself (`class Ring : Circle, IPrintable`: `IPrintable`). Both are the same in the VM and in native builds (also for classes of modules and plugins).
- `Member.Attributes` holds the attributes of a method, field or property, `Type.Attributes` those before the class, in the order of the source. `Args` are the values of the arguments that are
  constants - numbers (also negative ones), strings, `true`/`false`, arrays of constants and ranges of `int` constants (`1..3` gives `[1, 2, 3]`); any other
  argument (`new List<int>()`, a variable, a call) is `undefined` there. `ArgTexts` always has the source text of every argument.

```fire
#import "reflection"
class Job {
    [Retry 3, "network"]
    undefined Run() { }
}
var m = Type.Of("Job").Find("Run")
var retry = m.FindAttribute("Retry")
print(retry.Args[0] + " " + retry.Args[1])   // 3 network
print(m.HasAttribute("Test"))                 // False
```

An attribute of a [flag enum](10-enums-and-namespaces.md#flag-enums-as-attributes) (`[Access Read, Write]`) has the full name of the enum as its `Name`
and the combined value as its only argument. `HasFlag(flag)` and `Flags(enum)` on a `Type` or `Member` read them: `Flags` combines all attributes of the
enum (given by a member, `Access.Read` - the result is a value of the enum - or by its name, `"Access"` - the result is the number) and gives `0` if
there is none.

```fire
#import "reflection"
flag enum Access { Read = 1, Write = 2 }
[Access Read]
class Doc {
    [Access Write]
    string text
}
var t = Type.Of("Doc")
print(t.HasFlag(Access.Read))               // True
print(t.Find("text").Flags(Access.Read))     // Write
print(t.Find("text").Flags("Access"))       // 2
```

### Reflection in native builds

A native build (`forge build --engine native`) leaves out what the program does not reach. How much is the **stripping level** (`"stripping"` in `fire.native.json` or
in the settings of a project, `--stripping` on the command line; a number or a name):

| level | what stays |
|---|---|
| `0` `none` | everything, as in the VM (`--keep-all`; a debug build always) |
| `1` `protected` | every class and every public member; a private or protected member only when the program uses it |
| `2` `methods` | every class with its constructors, fields and properties (getters and setters); a method only when the program uses it |
| `3` `all` (the default) | only what the program reaches: also a class the program never makes or uses is left out |

At every level these stay:

- what is marked `[Keep]` (see [Classes](08-classes.md#attributes)), and
- the names the program gives **as literals** to reflection: `Reflect.New("Dog", [])`, `Type.Named("Dog")`, `Type.Of("Dog")` keep the class `Dog` like `[Keep]`,
  `Reflect.Call(obj, "Bark", [])`, `Reflect.Get(obj, "legs")`, `type.Find("Bark")` keep the members of that name (of the classes whose objects exist). Every text in the
  function that calls reflection counts (also `var n = "Bark"` a line before), a text made at run time or read from a file does not.
- the classes and members an **expression tree** names (`p => p.address.city == "Bonn"`: `Person`, `Address`, `address`, `city` - see
  [Lambdas](12-lambdas.md#expression-trees-lambda-expressiont)): a translator finds them with `Type.Named(...).Find(...)`, also when the program never
  makes an `Address`.

Reflection that works on an object (`Reflect.Get/Set/Has`, probes, `Type.Of(obj)`) finds every field and property of that object's class at every level;
`Type.Of(obj).Methods()` lists the methods that are there. Looking up what was left out is an error that says so - natively only, the VM keeps everything:

```
Class 'Ghost' was left out of the build (stripping 3). Add [Keep] to 'Ghost' or use stripping <= 2.
Member 'Dog.Wag' was left out of the build (stripping 2). Add [Keep] to 'Dog.Wag' or use stripping <= 1.
```

(a `ReflectionException`; for a private member the advice is stripping 0). `Type.Names()` lists the classes that are there, `Reflect.Has(obj, name)` is false for a
method that was left out. The JSON `"$type"` is data of the run as well: at level 3 it can only name the classes reading can make (JSON (`docs/stdlib/json.md`)).

```fire
#import "reflection"
[Keep]
class Plugin { construct() { } }          // made only by a name that comes at run time
var p = Reflect.New(Environment.Args[0], [])  // works natively because of [Keep]
var q = Reflect.New("Plugin", [])           // a literal: works without [Keep] as well
```

### Selectors

A parameter of a selector type takes a lambda that **selects a member** (`c => c.radius`); inside the method the parameter is a `Selector` that
describes that member:

```fire
#import "reflection"
class Circle {
    float radius
    construct(float r) { this.radius = r }
}
class Watch {
    static undefined Show(lambda member<Circle> sel, Circle target) {
        print(sel.Name + " = " + sel.Get(target))       // radius = 5
        sel.Set(target, 3.0)
    }
}
var c = new Circle(5.0)
Watch.Show(x => x.radius, c)
print(c.radius)                                         // 3
```

| Type | The lambda may select |
|---|---|
| `lambda field<T>` | a field |
| `lambda property<T>` | a property |
| `lambda member<T>` | a field or a property |
| `lambda method<T>` | a method (`x => x.Scale`, without call) |
| `lambda selector<T>` | anything |

A `Selector` has `Name`, `Path` (for `p => p.address.city`), `Kind`, `Parent(obj)`, `ActualKind(obj)`, `Get`, `Set`, `Call`, `Describe(obj)` (the
`Member`), `Probe` and `Silence`. This is how libraries take "which member?" as a checked argument instead of a string.

## Exercises

1. Put a `changing` probe on a field `age` that refuses negative values, and test it.
2. Log every change of every member of an object with `probe obj.* changed`.
3. Print all fields of an object with their values, using `Type.Of(obj).Fields()` and `Get`.
4. Write `static undefined Reset(lambda member<Settings> sel, Settings s)` that sets the selected member to `0`.
