# Projects and solutions

A **project** (`name.fireproj`) says which files make up a program or a library, which libraries and packages it uses and which build settings apply to it. A **solution**
(`name.firesln`) holds several projects, says which one runs and can give settings to all of them. Both are optional: a single file without any project still works exactly as
before, in the editor and on the command line - and next to a loaded solution (see "The active document").

Both files are JSON (like `package.json` and `fire.native.json`), written and read by `fire.Project` (`src/fire.Project`, no user interface, no compiler), and used by the
compiler (`fire run|build x.fireproj`), by the editor (spark) and by the tests.

## The files

```json
// App/App.fireproj
{
  "name": "App",
  "type": "exe",                               // "exe" (default) or "library"
  "files": ["helper.fire", "src/**/*.fire"],   // optional: paths and patterns, in this order; default: every fire file below this folder
  "exclude": ["src/old/**"],
  "entry": "main.fire",                      // optional: runs last (default: the last file)
  "references": [
    { "project": "../Core/Core.fireproj" },    // a library of the solution
    { "package": "fire-http", "version": "1.0" }   // an installed package (ember)
  ],
  "settings": { "floatWidth": 32, "mode": "release", "defines": ["PRJ"], "engine": "native", "target": "esp32" }
}
```

```json
// All.firesln
{ "name": "All", "projects": ["Core/Core.fireproj", "App/App.fireproj"], "startup": "App", "settings": { "author": "me" } }
```

**Files.** `files` are paths and patterns relative to the project file (`*` any characters but `/`, `**` any folders, `?`, `{a,b}`); a pattern matches in name order, a path
keeps its place. Without `files` the project takes every fire file (`*.fire`, `*.script`, `*.fi`, `*.fic`) every UI markup file (`*.fxml`) and every schematic of a circuit (`*.biscuit`, [Circuits](../circuits/README.md)) below its folder - except `bin`, `obj`, hidden
folders, the folder `templates/` (what the project offers as templates), the folder `testing/` (its tests: only `forge test` and the Test Explorer of spark compile them, see [Testing: forge test](testing.md)) and the
folders of *other* projects. A program runs its files one after the other as one program (top-level code in the order of the files), so the **entry file is put
last**: what the other files declare is known by then.

**The code of a project is included on its own** - for the build and for the editor (colours of classes, completion, tooltips, "go to definition", the errors that are underlined). No
`#include` is needed to use what another file of the project declares. A **markup file** (`.fxml`, [UI markup (.fxml)](../ui/markup.md)) or a **schematic** (`.biscuit`, [Circuits](../circuits/README.md)) of the project counts as the script that is generated from it
(markup files come first, so that their classes are there before the scripts use them). An `#include` of a file that is part of the project adds nothing (it is there anyway), so nothing
is declared twice. Files that are not code - pictures, data, C++ sources - are the **content** of the project: they live in its folder (shown by the editor, not compiled), see below.

**Content.** Everything else in the project folder is content: resources, the C++ of the natives, notes (the folder `notes/` holds the todo list and the overview of the project, [Notes: todo lists and project overviews](notes.md); `"notes"` in the project or solution file puts it elsewhere). `new Resource("images/logo.png")` takes a file of the folder into the program when
it is compiled ([Resources: files inside the program](../stdlib/resources.md): the path is relative to the file that writes it); the editor copies files in (*Add Resource*) into a folder you choose.

**Natives.** A project with `"native": {}` has C++ natives in its folder `native/` ([Writing the natives of a package (C or C++) and connecting them to the prelude](../packages/natives.md)): every `inline Value name(Value a, ...)` in a file there is the fire function
`__name`; with `"native": { "language": "c" }` they are C (C99, the recommended choice - the template *Native Library (C)*), and every `static inline Value name(Value a, ...)` is one (a last parameter `OwnList* list` says that the function allocates its result and returns it). A library project wraps them in classes; programs that reference the library use
them (in the virtual machine a shared library is built from the C++ for the machine; a native build puts the C++ into the generated file), and the package that is packed carries the C++.
`native` can also name `sources` (files and patterns, default `native/**`), `platformSources`, `platforms`, `linkLibraries`, `exceptions`, `reset` and `functions` (to say more about a function,
like `returnsReference`) - the same as in a package. An entry of `sources`/`platformSources` can have a **build order**, `{ "file": "native/x.h", "order": -10 }` (for a pattern: each of its files):
the sources are put into one file sorted by it (smaller first; without one 0, equal ones in list order, the platform sources first), and a source that `#include`s a later source of the project
is an error with file and line ([Writing the natives of a package (C or C++) and connecting them to the prelude](../packages/natives.md), "Build order"). The editor sets it in the *Properties* of the file (below). `"discover": false` takes only the named `functions` (no search for `inline Value` functions in the files). `"standard": true` (top
level of the project) is for the standard libraries of `Source/Packages.firesln` ([Packages and ember](../packages/README.md)): their import names are those of the compiler. `"serializable": false` (top level
of a library) excludes all classes of the library from JSON, like `[NotSerializable]` on each ([JSON](../stdlib/json.md); the package carries it in `package.json`, the standard library `ui` has it).

**Settings** (all optional): `subsystem` (`console`/`gui`), `mode` (`debug`/`release`/`performance`), `floatWidth` (32/64), `intWidth` (16/32/64, default 64; the width of `word` is not a setting of the project - it is the target's, `wordWidth` of a target in `fire.native.json`, SPEC 8.10a), `name`, `codename`, `description`, `author`, `license`
(a library that is packed), `comments`, `icon`, `version`, `fileVersion` (the assembly information), `defines` (symbols for `#if`), `engine` (`vm`/`native`), `target`, `toolchain`, `linkage` (`source`, the default, `static`: every library and package of a native build becomes a static library in the user-wide cache and is built only once, or `shared` on Linux, macOS and Windows (MinGW): the same as shared libraries (DLLs) next to the program - such a program can load fire plugins -, or `plugin`: the project's own classes become a fire plugin that a program with `shared` loads with `Library.Load` (`<output>.so`/`.dll` and `<output>.fireplugin` for the VM; `--host <program>` names the program it is built for) - docs/spec/NATIVE_BACKEND.md "Getrennte Übersetzung" and "Plugins"; `--linkage` on the command line wins), `stripping` (how much a native build leaves out of what the program does not reach: `0`-`3` or `none`, `protected`, `methods`, `all` - the default 3; a number or a name, docs/spec/NATIVE_BACKEND.md "Größe des Programms"; `--stripping` and `--keep-all` on the command line win, the project wins over `fire.native.json`), `optimize` (`speed` or `size`: the direction of a native build - the flags of the C compiler and what the runtime embeds; default the target's, size for the Open Watcom targets; `--optimize` on the command line wins, the project wins over `fire.native.json`), `globalJobs` (when the main program runs the `fire global` jobs and lets in the sections of its threads by itself: `safepoints`, the default, `sleep` or `sync` - docs/spec/THREADING_DESIGN.md section 7, the same in the VM and a native build; `--global-jobs` on the command line wins, the project wins over `#globaljobs`/`#nosync` in the source and over `"globalJobs"` in `fire.native.json`), `output`, `arguments` (what a run of the program gets as
`Environment.Args`, e.g. `["input.txt", "--verbose"]`; `forge run app.fireproj -- a b` replaces them), `port` and `baud` (the serial port of a board and the baud rate of its
console: Run and Debug on an ESP32 in spark, see [Natives Backend (AOT): fire -> C](../spec/NATIVE_BACKEND.md); default: the only USB serial port, 115200; in spark: *Project properties > Native & Toolchain > Board
(ESP32)*, a list of the serial ports of the machine and of the usual baud rates, "auto"/empty writes no key). Paths are relative to the
file that holds them.

### Precedence

**Project, then solution, then the tags in the source (`#debug`, `#name "..."`, `#floatwidth 32`, `#noconsole`, ...), then the built-in defaults.** What a project says is not
overridden by a tag in one of its files; the symbols (`defines`) of all levels are added up. Options of the command line (`-m`, `-f`, `--engine`, `-t`, `-D`, `-o`) go before all of
it, and the mode chosen in the editor's toolbar is a setting of the run (you need Debug to debug) and goes before the project's mode. The build settings dialog of the editor shows the
effective values and writes into the project (for a file of a project) or - as before - as tags into the file (for a single file).

## Build and run: the active document decides

The editor keeps asking *which project does the active document belong to?* (`Workspace.FindProjectOf`):

* the file is in a project of the loaded solution (or in a library that such a project references): **the project is built/run**, not the single file - all its files, with the
  unsaved text of the open documents, the project's settings, its libraries;
* a file that two projects hold counts for the startup project;
* a file that no project holds (opened next to the solution, like in Visual Studio) is compiled alone, as always;
* a solution-wide command (Build solution, Run startup project) works regardless of the active document.

The status bar and the title say what will be built (`App (project)` or `file.fire (single file)`).

**Where a build goes.** *Build* (a program that carries the virtual machine) and *Build Native* of a **project** do not ask (in spark; `forge build` does the same): the program lands in the project folder
under `bin/<target>-<mode>/` - `<target>` is the target of the native build (`linux`, `windows`, `macos`, `esp32`, `wasm`, ...) or `vm` for a program with the virtual
machine, `<mode>` the mode of the run (`dbg`, `rel`, `perf`); the program has the name of the project (`App/bin/linux-rel/App`, `App/bin/vm-dbg/App.exe`). A toolchain
that only writes files (ESP-IDF) writes them into that folder. The setting `output` of the project goes before it. A **single file** (no project) asks where to put
the program, as before.

## References: libraries without an entry point

A project of `"type": "library"` has no entry point: classes, interfaces, enums, functions, namespaces and extensions only. A statement at the top level is an error with file and
line (`util.fire:12: a library has no entry point ...`). A library is what other projects **reference**:

```json
"references": [ { "project": "../Core/Core.fireproj" } ]
```

The reference makes the library **available**; `#import "Core"` (the import name is `import`, default: the project name) **turns it on** - exactly as with an installed package, so
the same source compiles whether `Core` is a project of the solution or a package from `ember`. Libraries can reference libraries (transitively, no cycles, the ones that others
need come first); only a library can be referenced.

How it works (and why this is the answer to "references to other fire assemblies"):

* **Source-level references.** fire compiles whole programs from sources (one parse, one resolve, one bytecode or one C++ file - the native backend needs the whole program). So a
  library project is not a binary to link against but *source that comes along*: the files of the imported libraries are processed like the prelude of a package, before the files of the
  project. It works unchanged for the VM and for native builds (also for ESP32), debugging steps into library files, namespaces keep names apart, and nothing has to be kept
  compatible between builds.
* **Packages are the binary form.** `fire build Core.fireproj` (or "Pack" in the editor) checks the library and writes `Core-1.2.0.fpk` - an ordinary package of `ember` with the
  library's files as its prelude (an `#include` is inlined), the import name, the version of the settings, and the libraries and packages it references as `dependencies` and
  `requires`. Installed (`ember install`), any program `#import "Core"`s it - without the project. A solution project and its package are interchangeable. What the library's files
  `#import` (`time`, another package) becomes a requirement of the package too; the `#import` lines are not part of its prelude. **Install** (project menu, context menu of a library)
  packs the library and installs it at once (an installed package of the same version is replaced after a question); *File > Install Package File* installs an `.fpk`.
* **The package takes its metadata from the project**: name (the project's), import name (`import`, default the name), version (`settings.version`, three numbers), description, author, license
  (`settings.*`, the solution's go behind the project's), the libraries and packages it references (as dependencies and `requires`). *Pack as Package* (project menu, context menu of a
  library) asks for version, description, author, license and the folder, writes the answers back into the project and can raise the patch number afterwards (`1.2.3` -> `1.2.4`, ready for the
  next package). The file is `Name-1.2.3.fpk`; a version that exists is replaced (the dialog says so).
* **Packages as references.** `{ "package": "fire-http" }` names a package that has to be installed; the build says so if it is not (`ember install fire-http`) and a packed library
  lists it as a dependency.

Not (yet) there, and how it would fit: a *precompiled* library (bytecode with the signatures of its public classes, so the consumer needs no source) would be a second artifact in
the same `.fpk`; C++ natives in a library project would use the existing `native` part of the package manifest; versions of project references (a solution always builds the sources it
has).

## Command line

```
fire.Compiler run   App/App.fireproj            # a project
fire.Compiler run   All.firesln                 # a solution: the startup project
fire.Compiler run   All.firesln -p Tool         # a project of the solution by name
fire.Compiler build All.firesln                 # a program: App/bin/<target>-<mode>/App (vm-rel, linux-dbg, ...; or the "output" setting, or -o), the engine of the settings
fire.Compiler build Core/Core.fireproj          # a library: bin/Core-1.2.0.fpk (-o names the folder)
```

## New solutions and projects

*File > New > Solution...* asks for a **name**, a **folder** and a **template** (a searchable list, see [TEMPLATES.md](templates.md)); the solution that is open is closed before the new one is loaded. A solution lives in a folder of its own - proposed: `$HOME/spark/{name}` - and a template other than
*Empty* makes a project of the same name in a folder of its own below it:

```
$HOME/spark/Demo/Demo.firesln
$HOME/spark/Demo/Demo/Demo.fireproj
$HOME/spark/Demo/Demo/main.fire
```

The templates that ship with fire (folders in `Templates/Project`; yours and those of packages are in the same list):

| template | makes |
|---|---|
| Empty | the solution only |
| Terminal | a program for the console: prints `Hello, World!` |
| Library | a library with a class `Greeter` in `namespace {Name}`; no entry point; a first test in `testing/` (`forge test`) |
| Native Library (C) | a library with C natives (C99, `"language": "c"`): `native/{name}.h` with an example function, wrapped by a class; a native build needs only a C compiler (also TinyCC) |
| Native Library (C++) | a library with C++ natives: `native/{name}.hpp` with an example function, wrapped by a class |

The package **fire-windows** (the standard bridge `windows`, installed with fire) brings one more, in the same list marked `From fire-windows ...`:

| template | makes |
|---|---|
| Desktop | a program with a window (`subsystem: gui`): a label and a button with `#import "ui"` |

*File > New > Project...* (and *New Project...* in the context menu of the solution) asks for the same, with the **location** instead of a solution folder: the project gets a folder `{location}/{name}`. The location is
proposed as the solution folder; a folder of the solution (*New Folder* on the solution or on a folder; folders that hold projects are shown, empty ones are kept in the solution file as
`folders`) puts the project into a subfolder of it. Without a solution a project is made on its own (proposed location `$HOME/spark`).

## The editor

* **Solution Explorer** (a tool window): the solution, its folders and projects (programs and libraries), their files - the code in compile order, the content (resources, C++) next to it,
  also empty folders - and references, each row with the icon of its file type. Double-click opens a file in the editor that fits: scripts, markup (with the design view), C++ and other text (a text editor; C++ with colours),
  pictures (a viewer), other data files (the hex editor). A double click on a row that folds opens it instead of folding it (the arrow still folds): the solution and a project
  open their properties, a `.fxml` or `.biscuit` its designer, the notes folder (always shown, pale until it exists; [Notes: todo lists and project overviews](notes.md)) the overview. The context menus add a new file, an existing file, a resource (the file is *copied* into a folder of the project), a folder, native
  code (C++), a reference, set the startup project, build/run/pack, reveal in the file manager, remove, and open the project's properties; a file can also be opened *as text*, *as hex* or - a
  picture - in the *pixel editor*. The markup of an interface (`Name.fxml`) opens like a folder: its first row **Designer** opens the markup editor (as a double click on the
  file does), below it is the code-behind `Name.fire` of the same folder.
* **Properties of a file**: selecting a file in the Solution Explorer shows it in the tool window **Properties** (until another document becomes active): name, folder, project, what the
  project does with it (*Kind*: its place in the compile order, content, test). A C/C++ file of a project also has the category **Native build**: *Part of the native build* (puts it into
  `native.sources` or takes it out - a file that comes from a pattern stays, the window says so), *Build order* (the `order` of its entry; empty: none, 0), *Platforms* (all, or one list
  of `platformSources`; only for a file the project file names itself), *Position* (its place in the build) and *Listed as* (the file itself or the pattern). A build order that breaks an
  `#include` is shown in red with file and line. A change is written to the `.fireproj` at once (as the project dialog writes it); an order for a file that comes from a pattern gives it an
  entry of its own just before the pattern. The rows come from *file property providers* (`IFilePropertiesProvider` in `src/fire.Editor/Panels/FileProperties.cs`), so other kinds of files can
  get properties of their own.
* **Viewer for pictures**: any size and colour depth the platform decodes (PNG, BMP, GIF, JPEG), zoom with sharp pixels (Ctrl+wheel), a grid of pixels, a chequerboard that shows what is
  transparent; *Edit pixels* opens the picture in the pixel editor.
* **Pixel editor** (File > New > Raster Image, or *Edit pixels*): a palette of 256 colours - the palette of fire with entry 0 transparent - or true colour; pencil, eraser, line, rectangle (filled or
  not), fill, colour picker; the left button draws with the first colour, the right button with the second (transparent at the start); double-click a palette entry to change its colour
  (`#RRGGBB` or `#RRGGBBAA`); size, conversion between palette and true colour, zoom, grid, undo. It reads PNG, BMP and GIF and saves PNG (an indexed picture stays indexed, with the transparent
  entry) and BMP (no transparent entry).
* **Hex editor**: offset, 16 bytes in hex and as text; typing overwrites (hex digits or, in the text column, characters), Insert puts in a zero byte, Delete takes a byte out, Ctrl+G jumps to an
  offset, Find looks for bytes (`DE AD BE EF`) or text (`"abc"`); files up to 128 MB; undo.
* **Start**: a splash screen while the editor starts, then the **welcome window**: the projects and solutions that were opened last (a click opens one; the cross takes it out of the list; one that is gone shows as not found), and what to do - new solution, new project, open a project or solution, open a file, new script, the first steps. *Show this window when the editor starts* turns it off; *? > Welcome...* brings it back. The list is `recent.json` in the application data folder of fire (`fire.Projects.RecentWorkspaces`, at most 20); *File > Open Recent* shows the last ten. Files given on the command line skip the window.
* **File menu**: New (Solution, Project, Script, Markdown, Raster Image; see [TEMPLATES.md](templates.md)) / Open Project or Solution / Close. **Project menu**: add a new or existing project to the solution, set the startup project, reload the project (read its folder
  again), **Project properties**: a window with pages chosen at the left - *General* (name, version, author, description, icon, program or library, entry file or import name, Desktop/Console, lax/strict, VM/native, startup project), *Version & Info* (product name, internal name, file version, license, comments), *Build* (configuration, float precision, symbols for `#if`, output folder), *Native & Toolchain* (target, toolchain, a button to the native build settings, the serial port and baud rate of a board with the state of ESP-IDF), *Run & Debug* (the program arguments), *References*, *Notes* (the notes folder) and, when a solution is open, a page for the solution (startup project, notes folder and the settings that every project without its own value gets). A value the project does not set shows the one it inherits (from the solution or the default) greyed out; *reset* removes the project's own value again.
* The documents stay documents: files of the project open in tabs like any other file, files outside open next to them, and every command that builds looks at the active tab. The status bar
  and the Build/Run commands say what they act on (`Build: App (project)`, `Build: single.fire (single file)`). Unsaved buffers of the project's files go into the build.
* **F5 on a library file** runs the *startup project* (a library has no entry point); **Build** on a library packs it (`Core-1.2.0.fpk`).
* Breakpoints belong to a file of the project (not to "the" script), also in library files; the debugger shows the file it is in. Live diagnostics of a file are made in the context of its project
  (the libraries, the project's symbols for `#if`): the other files of the project are compiled along - their classes, namespaces, members and `#import`s are known -
  and only the errors of the open file are shown (each error of the compiler knows the file it is in). A library that is a standard import (the solution
  `packages/Packages.firesln`) is its own import there, as in a build.
* Old window layouts without the Solution Explorer fall back to the default layout once.

* **Test Explorer** (a tool window, menu *Test*): the tests of the solution, the project or the active file - run all, the selected or the failed ones, results live,
  a double click goes to the test; see [Testing: forge test](testing.md).
* **Git**: the state of the files in the explorer, commit, history, branches, pull and push - see [Git in the editor](git.md).

## Open points

* Solution configurations (Debug/Release sets of settings), project templates in `ember create`, file watching so that files added outside of the editor show up at once
  (Reload Project / `Workspace.Refresh` does it on demand).
