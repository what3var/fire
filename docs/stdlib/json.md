# JSON (`#import "json"`)

`#import "json"` turns fire objects into JSON text and back. The format is plain JSON, so other programs can read and write it; a few members that start with `$`
carry what plain JSON cannot express (the class of an object, shared objects and cycles, units). The same program writes the same text in the virtual machine and in a
native build.

```fire
#import "json"

class Point {
    float x : cm
    float y : cm
    construct(float x : cm, float y : cm) { this.x = x; this.y = y }
}

class Person {
    string name
    int age
    Person friend
    List<Point> path
    [NonSerialized]
    int secret
    [JsonName "nick"]
    string nickname
    construct() { }
}

var ann = new Person()
ann.name = "Ann"
ann.age = 41
ann.path = new List<Point>()
ann.path.Add(new Point(1.5cm, 2cm))
var bob = new Person()
bob.name = "Bob"
bob.friend = ann
ann.friend = bob

string text = Json.Serialize(ann)
// {"$type":"Person","$id":1,"name":"Ann","age":41,"friend":{"name":"Bob","age":null,"friend":{"$ref":1},"path":null,"nick":null},
//  "path":[{"x":1.5,"y":2.0}],"nick":null}

Person back = Json.Deserialize<Person>(text)
print(back.friend.friend == back)   // True: the cycle is back
```

## The API

| | |
|---|---|
| `Json.Serialize(value)` | the value as JSON text on one line |
| `Json.Serialize(value, options)` | with `JsonOptions`: `indented` (line breaks, two spaces per level), `typeNames` (default `true`; `false` leaves out every `"$type"`) |
| `Json.Deserialize<T>(text)` | reads the text into a value of type `T` (`Json.Deserialize<Person>(text)`, `Json.Deserialize<List<Point>>(text)`) |
| `Json.Deserialize(text, typeName)` | the same with the type given by its name (`"Person"`, `"Box<int>"`) |
| `Json.Parse(text)` | JSON without classes: objects become `JsonObject`s, arrays arrays, the rest numbers, strings, `bool` and `undefined` (`null`) |
| `JsonObject` | `Get(key)`, `Set(key, value)`, `Has(key)`, `Remove(key)`, `Keys()`, `Count` - members in the order of the text |
| `JsonException` | a text that cannot be read (with line and column: `Expected ',' or ']' in an array (line 1, column 6).`) or a value that does not fit its type (`$.age: expected a whole number, found 1.5.`) |

`new JsonOptions(true)` is the indented form.

## What is written

- **Objects**: a JSON object with the fields in declaration order (base class first). Static fields, lambdas and pointers are left out. Properties are not written:
  their fields are.
- **`[NonSerialized]`** before a field leaves it out; **`[JsonName "x"]`** gives it another name in the text. Reading accepts the name in any case (`"Name"` for
  `name`) when there is no exact match; members the class does not have are ignored.
- **`"$type"`** names the class where the declared type of the slot does not tell it: the root value, a field declared `any`, an object of a derived class in a slot
  of its base class, and a generic object in a slot that does not give its type arguments (`{"$type":"Box<int>","value":42}`). `typeNames = false` never writes it;
  reading such text needs the classes from the declared types.
- **Shared objects and cycles**: an object, array or buffer that is reached more than once gets `"$id": n` where it is written out and is `{"$ref": n}` everywhere
  else - like `copy`, the graph comes back with the same sharing. It is written out at its owner when its owner is part of the text, otherwise where it occurs
  first. A shared array is `{"$id": n, "$values": [...]}`.
- **`List`** is a JSON array (`{"$type": ..., "$values": [...]}` when it needs a type or an id).
- **Numbers** keep their kind: an `int` is written without a point, a `float` always with a `.` or an exponent (`2.0`, `1E+30`); `NaN` and the infinities are the
  strings `"NaN"`, `"Infinity"`, `"-Infinity"`.
- **Units**: a number in a field declared with its unit is a plain number (`"x": 1.5` for `float x : cm`); anywhere else a number with a unit is
  `{"$value": 5, "$unit": "m"}`.
- **`char`** is a one-character string in a field declared `char`, `{"$char": "c"}` in an untyped slot; a **buffer** (`byte data[]`) is base64 text in a field
  declared `byte[]`, `{"$buffer": "..."}` otherwise. Reading also takes a byte array written as numbers (`[1, 2, 3]`).
- An **enum** is its number. Reading takes the number or the member's name (`"Green"`; a flag enum also `"Read, Exec"`) wherever the field, the
  array or the root (`Json.Deserialize<Color>(...)`) is declared with the enum, and gives the enum value (`is of Color`); an unknown name is a
  `JsonException` (`'Pink' is no member of 'Color'`). In an `any` slot the number stays a number. The enum is the one of the running program (as a class
  is): another program in the same process with an enum of the same name - the editor, the tests - does not interfere. An actor cannot be written (a `JsonException`).
- **Classes that are not serializable**: `[NotSerializable]` before a class excludes it and every class derived from it; a package excludes all its classes with
  `"serializable": false` in its project file (the standard package `ui` does: its elements hold windows, renderers and handlers, not data). Writing an object of such a
  class, or reading one (from `"$type"` or the declared type), is an `ArgumentException` - in the VM and natively the same:
  `The class 'Secret' cannot be written to JSON: it is not serializable ([NotSerializable] on it or a base class, or "serializable": false in its package).`,
  `$.shapes[0]: the class 'UI.Label' cannot be read from JSON: ...`.

```fire
#import "json"
[NotSerializable]
class Session { int token = 42 }
class AdminSession : Session { }      // excluded as well
try { Json.Serialize(new AdminSession()) } catch (ArgumentException e) { print(e.message) }
```

## Reading

`Json.Deserialize<T>` makes the objects without running their constructors: every field the text does not mention stays `undefined`. A `List` it makes
is an ordinary list (its inner state as after `new List()`: `Add` and `Insert` work). A class with a method
`OnDeserialized()` gets it called once the whole text is read (inner objects first), so it can fill in what it computes. The objects belong to the caller
(the root to its scope, the rest to the object or array that contains them), as if the code had made them with `new`.

The class of an object comes from `"$type"`, else from the declared type of the slot. An object without either (an `any` field, `Json.Parse`) becomes a
`JsonObject`. An interface or an abstract base without `"$type"` cannot be made: `$.shape: unknown class 'IShape'.`

## Native builds

The native backend ([Natives Backend (AOT): fire -> C](../spec/NATIVE_BACKEND.md)) has the same functions in its runtime (`fire_json.h`, C, included by the program when it uses JSON) and writes a table
of the program's classes for it. It needs exceptions in the runtime (for `JsonException`), not C++ exceptions, so it also works on boards whose compiler has them switched off.

A native build only keeps the classes the program can reach ([Natives Backend (AOT): fire -> C](../spec/NATIVE_BACKEND.md), "Größe des Programms"), and JSON adds what reading can make:

- **Writing** (`Json.Serialize`) adds nothing: the objects it writes exist, so their classes are there.
- **Reading** (`Json.Deserialize<T>`) adds the classes of the declared types: `T`, the types of its fields (transitively), and for each of them the derived classes and the
  classes of an interface - `"$type"` may name any of them. `Json.Parse` adds `JsonObject`.
- As soon as a slot without a class is read - a field declared `any` or without a type, `Json.Deserialize<any>`, `T` of a generic method, the class given as a text
  (`Json.Deserialize(text, "Box<int>")`) - every class of the program is kept.
- Classes that are not serializable are never added.

So a `"$type"` that names a class outside the declared types (a class that is no subclass of the slot's type) is `unknown class 'X'` in a native build, while the
VM makes it (`"$type"` is data of the run: a name in it is not kept like a literal given to reflection). The stripping levels 0-2 (`"stripping"` in
`fire.native.json`, `--stripping`, `--keep-all` for 0) keep every class.
