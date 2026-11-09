# Libraries and plugins

"Library" means four things in fire. They build on each other:

| Kind | What it is | Made with | Used with |
| --- | --- | --- | --- |
| **Library project** | fire code (and optionally C/C++) without an entry point, used as source by the programs of a solution | a `.fireproj` with `"type": "library"` | a reference in the program's project + `#import "Name"` |
| **Package** | a library project packed for other machines (`.fpk`, still source) | `forge build Lib.fireproj` | `ember install`, `#import "Name"` - [packages](../packages/README.md) |
| **Binary libraries of a native build** | every library and package of a program built once as a static or shared library | `"linkage": "static"` or `"shared"` | nothing to change in the code |
| **Plugin** | fire classes built on their own and loaded while a program runs | `"linkage": "plugin"` | `Library.Load("kit.fireplugin")`, then the classes by name |

And a fifth, outside fire: **native C libraries** (`.so`, `.dll`, `.dylib`) that a program calls with `extern` or `Library.Load(path).Get(name, signature)`
([native code](../language/18-native-code.md)).

| Page | |
| --- | --- |
| [Building a library (fire only)](building.md) | library projects, references, linkage, a plugin step by step |
| [Libraries with natives](natives.md) | C or C++ in a library project and in a plugin, calling C libraries |

## Library projects

A library project is **source that comes along**: fire compiles a program with all its libraries in one go (one bytecode program, or one C program),
so a library is not linked as a binary - its files are processed like the prelude of a package, before the program's. That works the same in the VM
and natively, on every target; the debugger steps into library files; nothing has to stay compatible between builds.

- A library has no statements at the top level (that is an error with file and line) - classes, interfaces, enums, namespaces, extensions.
- A program **references** it (`"references": [ { "project": "../Core/Core.fireproj" } ]`) to make it available and **imports** it (`#import "Core"`)
  to switch it on. The same `#import` finds an installed package of that name - a solution project and its package are interchangeable.
- Libraries can reference libraries and packages; the ones others need come first; cycles are an error.

## Binary libraries: linkage

A native build normally writes **one** C file for the whole program (`"linkage": "source"`, the default): the generator sees everything and leaves out
what the program does not reach. Two other ways build the libraries once:

| `linkage` | What a native build does |
| --- | --- |
| `source` (default) | one C file, one program |
| `static` | every library (the core, every import, every library project) becomes a static library in a cache of the user and is built only once; the program is linked with them. Faster builds of big programs; two programs share the cached libraries |
| `shared` | the same as shared libraries (`.so`, `.dylib`, DLLs with MinGW) next to the program; only such a program can load plugins |
| `plugin` | the program's own classes become a plugin (below) |

Set it in the project (`"settings": { "linkage": "static" }`), in `fire.native.json` or with `--linkage` on the command line. `ember cache` shows the
cache, `ember cache clean` empties it. A program with shared libraries checks at its start that every library next to it fits (else it ends with exit
code 70 and says which). Details: [native backend](../spec/NATIVE_BACKEND.md) ("Getrennte Übersetzung").

## Plugins

A **plugin** is a set of fire classes that a running program loads - drivers, file formats, extensions written by others. The program and the plugin
share an interface (a library or package both import); the program creates the plugin's classes by name:

```fire
#import "Api"
#import "reflection"

var kit = Library.Load("plugins/Kit.fireplugin")     // the VM: the bytecode; a native program: plugins/Kit.so
print(kit.Classes()[0])                              // Kit.MyDriver
Api.IDriver d = Reflect.New("Kit.MyDriver", [])
print(d.Name())                                      // my driver
```

- A plugin is built with `--linkage plugin` and `--engine native`: it gives `Kit.so` (`.dll`, `.dylib`) for native programs **and** `Kit.fireplugin`
  for the VM. A native program that loads plugins is built with `"linkage": "shared"`, and the plugin for it with `--host <program>`.
- The plugin is checked when it is loaded: the interfaces of the libraries it was built against must be those of the program (the code of the methods
  may differ), and a native plugin must be built for the program's configuration. Otherwise `Library.Load` throws a `LibraryException` that says why.
- The plugin's classes are then the program's: `Reflect.New`, `Type.Of`, `is of`, `catch`, calls through interfaces, JSON. A plugin may declare units,
  extend classes of its libraries (`class extends`), have natives - and it can be debugged in spark like the program.

The step-by-step guide is in [building a library](building.md#a-plugin-step-by-step); everything about `Library.Load` in
[native code](../language/18-native-code.md#fire-plugins).
