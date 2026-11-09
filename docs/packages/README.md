# Packages and `ember`

| Page | |
| --- | --- |
| **This page** | using packages: `ember`, sources, the standard libraries, the cache |
| [Building a package](building.md) | a package in fire only - from a library project or from a description |
| [Packages with natives](natives.md) | C or C++ code in a package: how to write it and connect it to fire |

fire can import more than the libraries that come with the compiler (`graphics`, `io`, `time`, ...): a **package** brings one or more imports of its own, for example the
interface to a sensor, a protocol or an operating system API. `#import "name"` finds the imports of the installed packages like the built-in ones.

`ember` is the package manager (the library `fire.Package.Manager`, used by fire.Native and fire.Project, and the command line program `ember` in `src/fire.Ember`). The editor has a small window for it
(*Tools > Package Manager (ember)...*).

## Using packages

```
ember find sensor          search the package sources (a part of a name or description; no name: everything)
ember install sensorkit    install the newest version (and what it depends on); sensorkit@1.2.0 installs that version
ember install kit.fpk      install a package file
ember list                 the installed packages
ember remove sensorkit     remove a package (not one that another installed package needs)
```

Packages are installed **for the machine**: below `Packages\<name>\` in the folder of the compiler (the folder of `ember` itself). Every script sees the same packages.
A script uses an import of a package like any other:

```
#import "sensorkit"
print(SensorKit.Read(3))
```

An import that is not installed is an error that points to `ember`. The live diagnostics of the editor know the installed imports; after the Package Manager window
installed or removed something, the open scripts are checked again.

### Sources

`ember find` and `ember install` look in the *package sources*. `ember.json` next to the compiler lists them; without the file these are
the folder `PackageSource` next to the compiler and the index on the GitHub page of the packages:

```json
{ "sources": [ "PackageSource", "https://what3var.github.io/fire-packages/index.json", "D:\\my-packages\\index.json" ] }
```

A source is a **folder** (`.fpk` files, and/or an `index.json`) or an **index** (an address of the web, a `file:` address or the path of a json file). The index:

```json
{
  "packages": [
    { "name": "sensorkit", "author": "Jane", "description": "Sensors on the I2C bus",
      "versions": [ { "version": "1.0.0", "url": "sensorkit-1.0.0.fpk", "sha256": "..." } ] }
  ]
}
```

The `url` is relative to the index or absolute; `sha256` (optional) is checked after the download. `ember index Folder [BaseUrl]` writes the `index.json` for the `.fpk`
files of a folder, ready to be put on a web page together with them. A source that cannot be read (no network) is reported and skipped.

## The standard libraries (`packages/`, `Source/`)

The libraries of fire - `graphics`, `windows`, `devices`, `io`, `filesystem`, `dialogs`, `ui`, `ui.docking`, `linq`, `reflection`, `time`, `random`, `json`, `circuits`, `circuits.ui`, `testing`, `net`, `tls`, `http`, `gpio`, `i2c`, `spi`, `wifi`, `audio`,
`tracker`, `scene3d`, `scene3d.terrain`, `scene3d.effects`, `scene3d.physics` - are ordinary **library projects** ([Projects and solutions](../tools/projects.md)) that you can open, change and test like your own: the folder `packages/` of the repository holds the solution
`Packages.firesln` with one project per import:

```
packages/
  Packages.firesln
  time/
    time.fireproj        "name": "fire-time", "import": "time", "standard": true, the natives (sources, functions, reset, link libraries)
    time.fire            the fire code (the prelude of the package)
    native/              the C++ (fire_bridge_time.hpp): the natives, for the VM (a library) and for native builds
    testing/             the tests of the library (forge test), not part of the package
  ui/
    00_core.fire ...     a library may have several files; `#import "graphics"` in them makes graphics a requirement of the package
  ...
```

The build copies the folder next to the programs as **`Source/`** (`src/StandardSources.targets`, imported by the compiler, the editor and the tests) and builds the libraries of the C++ for this
machine into `Prebuilt/` (`forge prebuild-standard Source Prebuilt`). When **spark** or **forge** start (ember asks forge: `forge install-standard`), every project of `Source/Packages.firesln` that
is not installed yet, or whose folder changed since it was installed, is packed (without its tests) and installed - the ones it imports first. A fingerprint of every project folder in
`Packages/.source-stamps.json` keeps the check quick. A prebuilt library goes along when it was built from the same C++, so that the virtual machine needs no C++ compiler for the libraries
as shipped; after a change of the C++ the library is built on first use like that of any package.

**Changing a standard library**: *Tools > Open the Standard Libraries (Source)* opens the solution in spark. Change a file, run its tests (`forge test`, or the tests of the project), then
*Install* the project (context menu of the project, or *Project > Install Library*): it is packed and installed at once; an installed package of the same version is replaced after a question.
The next start of spark or forge would install it, too.

What makes these projects special is small: `"standard": true` lets a project have the import name of a library of the compiler (`time`, `graphics`, ...), `"serializable": false` (in `ui`)
keeps its classes out of JSON ([JSON](../stdlib/json.md); written to the import in `package.json`), and `"native": { "discover": false,
"functions": [...] }` names the natives explicitly (`"c": "fire_io_FileOpen"` for a package in C, `"cpp": "mathkit::Add"` for one in C++). Five imports are known to the compiler by name: `graphics`,
`windows`, `ui`, `linq`, `reflection` - their fire code still comes from the package; the C of `graphics` and `windows` runs in the VM (as a library built for
the machine) and in native builds alike. Only the core prelude (`Exception`,
`List<T>`, `Math`, string methods, ...) is built into the compiler.

Inside its own project a library is the program: `#import "time"` in the tests of `time` means the project's files (and its C++, built for the VM), not the installed package.

### Device drivers

A library with drivers of the device framework names them in its project file, `"drivers": ["MyDriver", "Kit.OtherDriver"]` (classes made with `new Class()`).
`forge` writes them to the import in `package.json` (`"drivers": [...]`). spark loads the drivers of the installed packages and shows their devices in its device
manager ([Devices](../devices/README.md)).

### Plugins

A package (or a library of a project) can be the interface between a program and its plugins ([libraries and plugins](../libraries/README.md)): the program and the plugin both import it, the plugin implements its
interfaces or derives from its classes, the program creates the plugin's classes by name after `Library.Load` ([Native code](../language/18-native-code.md), [Natives Backend (AOT): fire -> C](../spec/NATIVE_BACKEND.md)
"Plugins"). The plugin loads only into a program whose package has the same interface (the hash of its classes and members) - a new version of the package that only
changes the code of its methods keeps it.

### The cache of native libraries

A native build with `"linkage": "static"` ([Natives Backend (AOT): fire -> C](../spec/NATIVE_BACKEND.md) "Getrennte Übersetzung") turns every package (and the prelude, every library of a project) into a static
library (`"shared"`: a shared library, on Linux and macOS, a DLL on Windows with MinGW) and keeps it in a cache of the user: `~/.local/share/fire/cache/native` on Linux, `%LOCALAPPDATA%\fire\cache\native` on Windows, `~/Library/Application Support/fire/cache/native`
on macOS (`FIRE_NATIVE_CACHE` moves it). An entry is found by the content hash of everything its C depends on, so a stale entry is never used, only left behind.

```
ember cache          where the cache is and how big it is
ember cache clean    empty it
```

## Where it is in the code

* `src/fire.Package.Manager`: the manifest (`PackageManifest`), the package file (`Fpk`), the installed packages (`PackageStore`), the sources (`FolderSource`, `IndexSource`),
  install/remove (`PackageManagerService`). A library: fire.Native and fire.Project reference it (the compiler and the editor get it from them). The command line is `src/fire.Ember` (`Program`), an executable that only uses the library.
* The compiler keeps the imports of packages in its set of imports under the key `pkg:name` (`PackageStore.KeyPrefix`): `ImportedPreludes` resolves `#import`, inserts the prelude
  and registers the names of the natives; `LinkedProgram.PackageNatives` carries those names so that a run in the virtual machine (also of a packed program) keeps the
  indexes of the native calls right.
* The native generator (`CppGenerator.CollectPackages`) checks the platforms, puts the C++ sources of the target into the generated file and calls the functions.
* For the virtual machine `PackageLibrary` (fire.Compiler) builds the shared library of an import (generated wrapper + `native/abi/` + the runtime in library mode, `FIRE_LIBRARY`) or
  finds the prebuilt one; `PackageNativeBinding` (fire.Runtime) loads it and marshals the values. `LinkedProgram.PackageNativeLibraries` names the library of each native; a packed program carries
  the libraries in its payload (`PackagePlan`).
