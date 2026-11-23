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
  "settings": { "floatWidth": 32, "mode": "release", "defines": ["PRJ"], "engine": "native", "target": "esp32" },
  "buildActions": { "data/**": "copy", "old/**": "ignore" },   // optional: what builds do with files (see "Build actions")
  "includedDirectories": ["assets"],          // optional: folders that belong to the project without code (see "Folders")
  "excludedDirectories": ["out", "**/generated"],   // optional: folders the project leaves out - wins over everything
  "publish": { "enabled": true, "targets": ["dist/App"] }        // optional: see "Publish"
}
```

```json
// All.firesln
{ "name": "All", "projects": ["Core/Core.fireproj", "App/App.fireproj"], "startup": "App", "settings": { "author": "me" } }
```

The solution format is read and written by `FireSolution` (`src/fire.Project/FireSolution.cs`): `format`, `name`, `projects` (the project files relative to the solution file, in
the order they are shown), `folders` (empty folders of the solution), `startup`, `settings` and `notes`; what is empty is not written. **`startup`** is the name of the **start
project** - what Start in spark, `fire run All.firesln` and `fire build All.firesln` take. It is optional: a solution without it (also every file written before spark could choose
it) starts its first program, and a name that is no project of the solution any more does the same; spark writes it when the start project is chosen (the box of the *Run* tool
strip, *Set as Start Project*) and takes it out when that project is removed from the solution. The name is matched without regard to case.

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

**The compiler of a native build:** `--toolchain` on the command line, then the project's `toolchain` (the solution's), the toolchain of the target, `"toolchain"` in
`fire.native.json` - and only when none of them names one, the compiler chosen for this machine in spark's *Tools > Options > Compilers* (`Toolchain/toolchain.json`): the
**Build** one for Build, Run, Debug, Build Solution, `forge build` and `forge test`, the **Publish** one for Publish and `forge publish` (there `publish.toolchain` goes
before the project's). *Automatic* is TinyCC for Build and GCC for Publish when they are there, else the best compiler there. The build mode (Debug, Release,
Performance) does not choose the compiler. *Project Properties > Native & Toolchain* shows what an unset toolchain comes to on this machine ([spark](spark.md)).

## Build and run: the active document decides

The editor keeps asking *which project does the active document belong to?* (`Workspace.FindProjectOf`):

* the file is in a project of the loaded solution (or in a library that such a project references): **the project is built/run**, not the single file - all its files, with the
  unsaved text of the open documents, the project's settings, its libraries;
* a file that two projects hold counts for the startup project;
* a file that no project holds (opened next to the solution, like in Visual Studio) is compiled alone, as always;
* a solution-wide command (Build Solution, Clean Solution) works regardless of the active document.

**Start** (F5) is different: from every file that belongs to the solution - a file of any of its projects, a project file, the solution file, notes - it runs the **start
project** (`startup`, see above; without a solution the project itself), as in Visual Studio (`Workspace.StartProjectFor`). Only a script that belongs to no project runs alone.

The status bar and the title say what will be built (`App (project)` or `file.fire (single file)`) and, when it is another project, what Start runs (`Start: Tool`).

### Where a build goes, and Clean

*Build virtual* (a program that carries the virtual machine), *Build* (native) and *Build Solution* of a **project** do not ask (in spark; `forge build` does the same): the program lands in the project folder
under `bin/<target>-<mode>/` - `<target>` is the target of the native build (`linux`, `windows`, `macos`, `esp32`, `wasm`, ...) or `vm` for a program with the virtual
machine, `<mode>` the mode of the run (`dbg`, `rel`, `perf`); the program has the name of the project (`App/bin/linux-rel/App`, `App/bin/vm-dbg/App.exe`). A toolchain
that only writes files (ESP-IDF) writes them into that folder. The setting `output` of the project goes before it. A **single file** (no project) asks where to put
the program, as before.

The build folders of a project are `bin/` (the programs above, the C sources a native build keeps next to a program in `<program>.src/`, the packages of a library) and `obj/` (the
C++ natives of the project built for the virtual machine); the file lists of a project never take files from them. **Clean** (spark: *Run > Clean*, *Clean Solution*, the context menus)
removes exactly these two folders of each project (`ProjectOutput.Clean`) - not the place an `output` setting names elsewhere, and not a build folder that holds a fire source, a
test or a project or solution file (builds write none of them; it is kept and reported). A build folder that is a link loses the link, not its target.
*Build Solution* builds the projects natively in the order of their references (`Workspace.BuildOrder`: a library before whoever references it, otherwise the order of the solution
file); a library is compiled for the target (its code goes into the programs that import it), a project whose library failed is skipped.

### Folders: included, undecided, excluded

Every folder below the project file is in one of three states (`ProjectFolderMap`, `src/fire.Project/ProjectFolders.cs`), decided in this order - the first that fits wins:

1. **Excluded**: named in `excludedDirectories` (a path relative to the project file or a pattern as in `files`: `out`, `**/generated`), inside such a folder, ignored by a
   `.gitignore` (only with *Sync .gitignore*, below) or one of the folders a project never reads (`bin`, `obj`, `.git`, `.vs`, `node_modules`, a name that starts with a dot).
   Nothing in it is compiled - **also not a source the project names in `files`, and also not when it is listed in `includedDirectories`** - it is no content, a *Copy* build action
   inside it never copies, the folder is not entered at all (a `node_modules` with 100 000 files costs nothing) and spark's file watcher passes over it.
2. **Included**: the project folder; named in `includedDirectories` (and everything below it); or it **holds code** - a fire source or markup (`.fire`, `.script`, `.fi`,
   `.fic`, `.fxml`, `.biscuit`, `.fdata`; C/C++ files when the project has natives) in it or anywhere below it, so every folder on the way to code is included too (a folder that
   holds an included folder is included as well). The notes folder is always included.
3. **Undecided**: none of that - no code, nothing names it (`assets/`, `docs/`, an empty folder, `src/images` below a code folder). Its files are shown, but they are no part of
   the build: they do not count for Publish's check whether something changed. A file's **own build action still counts** there (`"assets/logo.png": "copy"` or a pattern
   such as `"assets/**": "copy"` copies it) - you said so explicitly; inside an excluded folder it never does. `new Resource("...")` embeds what the code names, wherever it is.

A project file without the two lists behaves as before: the folders with code are included, all others are undecided, which changes nothing for a build (only the
explorer shows them pale). `exclude` stays what it was - patterns of *files* that are not compiled; `excludedDirectories` is about whole folders.

**Sync .gitignore** (`"syncGitignore": false` turns it off; on by default - *Project Properties > Build > Folders* in spark): excluding a folder in spark also writes it into
the `.gitignore` nearest to the project - the project folder's, else the first one on the way up to the top of the repository; with none there a new one at the top of the
repository (without a repository: in the project folder). The line is the folder relative to that file with `/` at both ends (`/App/assets/raw/`), with the comment
`# spark: excluded from App.fireproj` above it. A line that says the same already is not added again. *Remove Exclusion* (and *Include in Project*) takes the line out again
- only a line with spark's comment above it; a line someone else wrote stays (spark says so, and the folder stays ignored). With sync on, folders that a `.gitignore` ignores
count as excluded (the `.gitignore` files from the top of the repository down to the project folder and those in its folders; `!` lines, `/` at the start or the end, `*`,
`**`, `?` and `[...]` as in git; not read: `.git/info/exclude` and git's global excludes). spark shows their reason ("ignored by .gitignore") and does not change them: that
is the `.gitignore`'s business.

### Build actions

`buildActions` says what builds and Publish do with a file: a path or a pattern (relative to the project file, like `files`) maps to an action.

```json
"buildActions": {
  "data/**": "copy",               // copied next to the program when it is newer than the copy there (and into the publish folders)
  "data/cache/**": "ignore",       // the last pattern that matches decides; a file's own entry goes before every pattern
  "settings.ini": "copyAlways",    // copied on every build and publish
  "legacy/old.fire": "ignore",     // a source that is not compiled
  "scripts/plugin.fire": "copy"    // a source that is not compiled but shipped next to the program (to be loaded at run time)
}
```

| action | a fire source | another file (content) |
|---|---|---|
| `build` (the default: no entry) | compiled, as before | content, as before: shown, not copied; `new Resource("...")` embeds it |
| `ignore` | **not** compiled | not copied, shown pale |
| `copy` | not compiled, copied | copied when newer (or of another size) than the copy there |
| `copyAlways` | not compiled, copied | copied on every build |

A copied file keeps its path below the project: `data/levels/one.txt` lands as `<program folder>/data/levels/one.txt` (the copy gets the time of the original).
*Build*, *Build Solution*, *Build virtual* and the native runs in spark and `forge build` copy them next to the program; the *Copy* files of the libraries a
program references are copied with it (the program's own win when the paths are the same). A project without `buildActions` behaves exactly as before. The
action does not change resources: `new Resource("data/logo.png")` embeds the file whatever its action is - the code asks for it (a resource with *Copy* is then
in the program and next to it). In spark the action is set in the *Properties* of a file or with *Build Action* in its context menu, also for several files at
once (`FileBuildActions`, `src/fire.Project/ProjectPublish.cs`).

### Publish

`publish` makes a project something to hand out: *Publish* (spark: *Run > Publish*, *Publish Solution*, the *Build* tool strip, the context menus; `forge
publish All.firesln`) builds every project that has it on and copies the result into folders of your choice.

```json
"publish": {
  "enabled": true,                             // Publish Solution takes only the projects that have it on (default: off)
  "targets": ["dist/App", "/srv/share/App"],   // absolute or relative to the solution file (no solution: the project file); default: publish/<name> next to the solution
  "engine": "native",                          // vm or native; default: the project's engine, else native
  "mode": "release",                           // default: release
  "target": "linux",                           // the target of a native build; default: the project's, else this machine
  "toolchain": "gcc",                          // default: the project's, else the Publish compiler of this machine (spark: Tools > Options > Compilers)
  "clean": false                               // empty each folder first (default: off)
}
```

* **Order and failures.** The projects go in the order of Build Solution (`Workspace.BuildOrder`): a library before whoever references it; a project whose
  library failed to publish is skipped. A **library** is published as its package (`Lib-1.2.0.fpk`, as *Pack* makes it). Without a solution the default folder is
  `publish/<name>` next to the project's folder.
* **Only what is outdated is built.** The publish build goes to the usual place (`bin/<target>-<mode>/`, or `output`); `obj/publish/<engine>-<target>-<mode>.json`
  remembers from which inputs: the text of every source of the project and its libraries (also unsaved text open in spark), the size and time of their other
  files (resources, natives), the settings, the engine, mode, target and toolchain, `fire.native.json` and the compiler itself. When all of that is the same and
  the program is untouched, it is not built again ("up to date" in the log). Clean removes the record with `obj/`. A native build that does run still takes
  the program cache ([NATIVE_BACKEND](../spec/NATIVE_BACKEND.md)) when the C is the same. The *Copy* files are not part of the record - they are copied anyway.
  Installed packages are not part of it either: after updating a package, publish once with a Clean before.
* **What is copied** into every folder: the program; the files of the same name next to it (`App.wasm` and `App.js` of a browser build, `App.firehost`), not the
  statistics or the C sources; with linkage `shared` the shared libraries next to the program; the whole folder of a toolchain that only writes files (ESP-IDF);
  the *Copy* files of the project and its libraries. A file whose copy in the folder has the same size and is not older is left as it is (`copyAlways` files are
  always copied). A VM program carries the virtual machine and everything it imports, so it is the only file it needs.
* **Other files in a folder stay.** Publish never deletes what it did not write - unless `clean` is on: then the folder is emptied first, but never a drive's root,
  the home folder, a folder that holds the solution or a project of the workspace, a project or solution file or a git repository (the log says why it is kept).
  Each folder gets a small file `.firepublish`: a folder that has it is never part of a project's files, so a publish folder inside a project folder does not
  feed the copies back into the project.
* The log says per project whether it was built or up to date and how many files went where, and ends with the summary (`2 projects published to /x/dist/App,
  /x/out/packages (1 up to date, not built again).`); errors and warnings go into spark's error list. Cancel stops it after the step that runs.

forge does it (`native/forge/project`; spark runs `forge publish --report json`); the tests are `FIRE_TESTS_ONLY=publish`.

## References: libraries without an entry point

A project of `"type": "library"` has no entry point: classes, interfaces, enums, functions, namespaces and extensions only. A statement at the top level is an error with file and
line (`util.fire:12: a library has no entry point ...`). A library is what other projects **reference**:

```json
"references": [ { "project": "../Core/Core.fireproj" } ]
```

The reference makes the library **available**; `#import "Core"` (the import name is `import`, default: the project name - a character a name cannot have becomes `_`, the dots stay: `Raser.Core` is imported as `#import "Raser.Core"`; an unknown name in an `#import` names the import names of the referenced libraries) **turns it on** - exactly as with an installed package, so
the same source compiles whether `Core` is a project of the solution or a package from `ember`. Libraries can reference libraries (transitively, no cycles, the ones that others
need come first); only a library can be referenced.

**Names of libraries.** What a library declares outside of every `namespace` belongs to a namespace with its import name: `class Parser` of the library `LibA` is
`LibA.Parser`. So two libraries can both have a `Parser` (or a `Json`, a `Value`) and a program can use both:

```fire
#import "LibA"
#import "LibB"
print(LibA.Parser.Name() + LibB.Parser.Name())   // the full names keep them apart
```

A file that imports the library finds its names by the short name too (`Parser`, `new Parser()`, `Mode.Fast`), as does the library itself; a short name that two
imported libraries have is ambiguous (`E4075` in an expression, `E4062` as a type) - write the full name. A `namespace` block of a library keeps its name (`namespace
Raser { ... }` in the library `Raser.Core` is `Raser`), and the standard libraries keep their names global (`Framebuffer`, `DateTime`). The same holds for a library
installed as a package (one that is not a standard one).

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
forge run   App/App.fireproj            # a project
forge run   All.firesln                 # a solution: the startup project
forge run   All.firesln -p Tool         # a project of the solution by name
forge build All.firesln                 # a program: App/bin/<target>-<mode>/App (vm-rel, linux-dbg, ...; or the "output" setting, or -o), the engine of the settings
forge build Core/Core.fireproj          # a library: bin/Core-1.2.0.fpk (-o names the folder)
forge publish All.firesln               # every project with publish.enabled, built when outdated, copied into its folders
forge publish All.firesln -p App        # one project (also when its publishing is off); a .fireproj alone: that project
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
  code (C++), a reference, set the start project, *Start Debugging*, *Build* and *Clean* of a project (*Build Solution* and *Clean Solution* of the solution), pack, reveal in the file manager, remove, and open the project's properties; a file can also be opened *as text*, *as hex* or - a
  picture - in the *pixel editor*. The markup of an interface (`Name.fxml`) opens like a folder: its first row **Designer** opens the markup editor (as a double click on the
  file does), below it is the code-behind `Name.fire` of the same folder.
* **Folders in the Solution Explorer** (see "Folders"): an included folder looks as always, an undecided one (and its files) pale and *italic*; an excluded one is only shown with
  not shown at all - only with *Show All Files* does it appear, pale with "excluded" or "ignored by .gitignore" behind it, like the folders a project never reads (`bin`, `obj`) and like a file with the build action
  *Ignore* ("ignored"). The context menu of a folder has *Exclude from Project* (every folder that is not excluded), *Include in Project* (an undecided folder, and an
  excluded one with its own entry) and *Remove Exclusion* (an excluded folder with its own entry; one excluded by a pattern, by a parent or by a `.gitignore` shows the
  reason greyed out instead). With several folders selected the command is for all of them ("Exclude from Project (3 folders)"); the project file (and, with *Sync
  .gitignore*, the `.gitignore`) is written at once. *Project Properties > Build > Folders* lists the excluded and the included folders of the project file, each with
  *Remove* (an exclusion taken out there loses its `.gitignore` line too, with *Sync .gitignore*), the folders a `.gitignore` excludes, and *Sync .gitignore*.
* **Properties of a file**: selecting a file in the Solution Explorer shows it in the tool window **Properties** (until another document becomes active): name, folder, project, what the
  project does with it (*Kind*: its place in the compile order, content, test). A C/C++ file of a project also has the category **Native build**: *Part of the native build* (puts it into
  `native.sources` or takes it out - a file that comes from a pattern stays, the window says so), *Build order* (the `order` of its entry; empty: none, 0), *Platforms* (all, or one list
  of `platformSources`; only for a file the project file names itself), *Position* (its place in the build) and *Listed as* (the file itself or the pattern).
  Every file of a project has the category **Build**: *Build action* (Build, Ignore, Copy if newer, Copy always - see "Build actions"; for several selected
  files at once) and *Set by*. A build order that breaks an
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
  again), **Project properties**: a window with pages chosen at the left - *General* (name, version, author, description, icon, program or library, entry file or import name, Desktop/Console, lax/strict, VM/native, startup project), *Version & Info* (product name, internal name, file version, license, comments), *Build* (configuration, float precision, symbols for `#if`, output folder), *Native & Toolchain* (target, toolchain, a button to the native build settings, the serial port and baud rate of a board with the state of ESP-IDF), *Run & Debug* (the program arguments), *Publish* (on/off, the folders, VM/native, configuration, target, empty the folders first), *References*, *Notes* (the notes folder) and, when a solution is open, a page for the solution (startup project, notes folder and the settings that every project without its own value gets). A value the project does not set shows the one it inherits (from the solution or the default) greyed out; *reset* removes the project's own value again.
* The documents stay documents: files of the project open in tabs like any other file, files outside open next to them, and every command that builds looks at the active tab. The status bar
  and the Build/Run commands say what they act on (`Build: App (project)`, `Build: single.fire (single file)`). Unsaved buffers of the project's files go into the build.
* **F5 on any file of the solution** runs the *start project* (bold in the Solution Explorer); **Build** on a library packs it (`Core-1.2.0.fpk`).
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
