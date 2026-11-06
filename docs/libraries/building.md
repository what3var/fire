# Building a library (fire only)

## A library project step by step

**1. Make it.** In spark: *File > New > Project... > Library* (inside a solution: *Add > New Project*). It makes the project file, a source file with
a namespace and a folder `testing/` with a test class. By hand:

`Shapes/Shapes.fireproj`

```json
{
  "format": 1,
  "name": "Shapes",
  "type": "library",
  "settings": { "version": "1.0.0", "description": "Areas of simple shapes" }
}
```

`Shapes/shapes.fire`

```fire
namespace Shapes {
    interface IShape { float Area() }

    class Circle : IShape {
        float r
        construct(float r) { this.r = r }
        float Area() { return Math.PI * this.r * this.r }
    }

    class Square : IShape {
        float side
        construct(float side) { this.side = side }
        float Area() { return this.side * this.side }
    }
}
```

**2. Use it** from a program of the same solution:

`App/App.fireproj`

```json
{ "format": 1, "name": "App", "references": [ { "project": "../Shapes/Shapes.fireproj" } ] }
```

`App/main.fire`

```fire
#import "Shapes"

var shapes = new List<Shapes.IShape>()
shapes.Add(new Shapes.Circle(1.0))
shapes.Add(new Shapes.Square(2.0))
foreach (s in shapes) { print(Math.Round(s.Area() * 100) / 100) }     // 3.14, 4
```

```bash
forge run App/App.fireproj
```

**3. Test it** - the files in `testing/` of a library are its tests ([testing](../tools/testing.md)):

`Shapes/testing/shapes_tests.fire`

```fire
#import "testing"
#import "Shapes"

class ShapeTests {
    [Test "The area of a square"]
    undefined SquareArea() {
        Testing.Assert.AreEqual(4.0, new Shapes.Square(2.0).Area())
    }
}
```

```bash
forge test Shapes/Shapes.fireproj
```

**4. Share it:** `forge build Shapes/Shapes.fireproj` writes `Shapes/bin/Shapes-1.0.0.fpk`, a package ([building a package](../packages/building.md)).

A solution file (`.firesln`) keeps the projects together; spark makes one for every new project ([projects and solutions](../tools/projects.md)).

## Native builds: linkage

Nothing changes in the code. To build the libraries once instead of with every build of the program:

```bash
forge build App/App.fireproj --engine native --linkage static
```

The first build puts `core`, `Shapes` and every import into the cache; the next builds only translate the program. `--linkage shared` puts shared
libraries next to the program instead (needed for plugins).

## A plugin step by step

Three projects: the interface both sides share, the plugin, the program that loads it.

```
Api/   Api.fireproj    "type": "library"
       api.fire        namespace Api { interface IDriver { string Name() } }
Kit/   Kit.fireproj    references Api, "settings": { "linkage": "plugin" }
       kit.fire        a class that implements Api.IDriver
Host/  Host.fireproj   references Api, "settings": { "linkage": "shared" }
       main.fire       loads the plugin
```

`Kit/kit.fire`

```fire
#import "Api"

namespace Kit {
    class MyDriver : Api.IDriver {
        string Name() { return "my driver" }
    }
}
```

`Host/main.fire`

```fire
#import "Api"
#import "reflection"

var kit = Library.Load(Environment.Args[0])
print(kit.Classes()[0])                    // Kit.MyDriver
Api.IDriver d = Reflect.New("Kit.MyDriver", [])
print(d.Name())                            // my driver
```

**In the VM:**

```bash
forge build Kit/Kit.fireproj --engine native
forge run Host/Host.fireproj -- ../Kit/bin/linux-rel/Kit.fireplugin
```

The plugin is built with `--engine native` even for the VM: that build writes both files, `Kit.so` and `Kit.fireplugin` (a C compiler is needed).
With the engine `vm` the project is built as an ordinary program.

**Natively:** the program first (with `linkage` `shared`, from its project), then the plugin **for** it:

```bash
forge build Host/Host.fireproj --engine native
forge build Kit/Kit.fireproj --engine native --host Host/bin/linux-rel/Host
Host/bin/linux-rel/Host Kit/bin/linux-rel/Kit.so
```

`Host/bin/linux-rel/` holds the program, its shared libraries (`libfire_mcore_....so`, ...) and `Host.firehost` (what a plugin needs to know about it).
A plugin built without `--host` is refused: "it was built for another configuration of the runtime ... - build it for this program (--host)".

The program decides what a plugin may be: usually it looks for classes that implement its interface (`kit.Classes()`, `Type.Of`, reflection,
[probes and reflection](../language/16-probes-and-reflection.md)).
